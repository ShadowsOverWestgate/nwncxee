using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>Order-independent transparency for the blended class, two modes behind <c>/draw oit</c>.
/// <para>Mode 1: weighted blended (McGuire &amp; Bavoil 2013). The class draws in ONE pass, unsorted,
/// depth-tested against the opaque set and writing no depth, into an accumulation pair -- premultiplied
/// colour times a depth weight (RGBA32F, ONE/ONE) and a revealage product (R16F, ZERO/ONE_MINUS_SRC_COLOR)
/// -- which <see cref="End"/> composites over the frame in one full-screen draw. Any number of layers
/// combine; nothing a translucent surface draws can hide another. Approximate: stacked layers blur.</para>
/// <para>Mode 2 (#490, the default): per-pixel linked lists. Every fragment ALSO appends a (colour, depth) node to an
/// SSBO behind an atomic per-pixel head, and the resolve sorts each pixel's list and composites it exactly.
/// The pair is still written underneath, so a frame whose node buffer overflows resolves the weighted way
/// with nothing missing and no CPU readback in the frame; the overflow shows on the Stats line two frames
/// later. Heads live in a pixel-indexed SSBO rather than an image: same atomics, no image binding to add.</para>
/// <para>Replaces the sorted, depth-writing blended pass and its per-creature one-layer sweep, whose four
/// depth decisions (opaque prepass, run sweep, colour draw, hi-Z) each punched a hole in a different
/// scene -- the floor decal under a light shaft, two ghosted creatures on it. Stands down to the old path
/// when the frame has no owned depth to attach (post not resolving) or a target refused.</para></summary>
internal static unsafe class OitPass
{
    private const uint R16F = 0x822D, RGBA32F = 0x8814;   // not-an-offset: GL internal-format enums
    /// <summary>SSBO binding points of the head table (count at 0, then one head per pixel) and the node pool.</summary>
    private const uint BindHeads = 22, BindNodes = 23;   // 21 is CullPass.BindRej (#489)
    private const int NodeBytes = 16;
    /// <summary>The depth slack the linked-list append gives a wash batch, standing in for the polygon offset
    /// IssueColour sets for it (which the GL applies after shading, where <c>gl_FragCoord.z</c> cannot see it).
    /// ponytail: one constant in window z, unmeasured; make it slope-scaled if far decals still drop out.</summary>
    internal const float WashBias = 2e-5f;
    /// <summary>Mode 2's object table for the frame: a creature's parts share one id, and the resolve composites
    /// the nearest node of an id once, so a ghosted creature is translucent as a whole rather than per part.
    /// ponytail: 255 ids a frame, first come; a 256th object gets 0 (per-part again). Linear scan, ~100 batches.</summary>
    private static readonly IntPtr[] _gobs = new IntPtr[255];
    private static int _gobN;
    internal static int GobId(IntPtr gob)
    {
        if (gob == IntPtr.Zero || !_linked) { _gobZero++; return 0; }
        for (int i = 0; i < _gobN; i++) if (_gobs[i] == gob) return i + 1;
        if (_gobN == _gobs.Length) return 0;
        _gobs[_gobN++] = gob;
        return _gobN;
    }
    /// <summary>The head table: the node count, then one head per pixel.</summary>
    private static long HeadBytes(int w, int h) => 4L + 4L * w * h;
    /// <summary><c>oitlayers</c> in sowx.cfg: node pool capacity as an average of nodes per pixel. 16 bytes a
    /// node, so 3 at 1080p is ~100 MB, allocated only once mode 2 is first used.</summary>
    private static readonly int Layers = Settings.Int("oitlayers", 3, 1, 32);
    /// <summary><c>/draw oit</c>: 0 = the sorted, depth-writing pass (A/B instrument), 1 = weighted blended, 2 = linked lists
    /// (default since the plaza A/B measured it at parity with 1; 1 is the fallback a refused pool lands on).</summary>
    internal static int Mode = 2;
    /// <summary>True while the lit twin that carries the append block (<see cref="WithLists"/>) exists. Without it
    /// mode 2 stands down to the weighted pair: a linked resolve over lists nothing appended to draws nothing.</summary>
    internal static bool ListsArmed;
    /// <summary>True inside a bracket that is resolving through the lists: IssueColour binds the twin then.</summary>
    internal static bool Linked => _linked;
    /// <summary>The define that compiles the append block into a fragment stage, after its #version line.</summary>
    internal static string WithLists(string fs)
    {
        int v = fs.IndexOf("#version", StringComparison.Ordinal);
        if (v < 0) return fs;
        return fs.Insert(fs.IndexOf('\n', v) + 1, "#define SOWX_OIT_LISTS 1\n");
    }
    private static uint _fbo, _accum, _reveal, _prog, _nodes;
    /// <summary>The head table, cleared on the GPU by <see cref="_progClear"/> (a full-screen draw writing zeros)
    /// rather than by glClearBufferSubData: any CPU-issued write to a buffer the GPU still holds is an implicit
    /// sync, and one per frame serialised the CPU and GPU -- the plaza's frame rate halved for it.</summary>
    private static uint _heads, _progClear;
    private static int _uClearStride;
    /// <summary>The count comes back through a GPU copy into a persistently mapped ring, read two frames later
    /// behind a fence: no GL call in the frame ever waits.</summary>
    private const int ReadSlots = 3;
    private static uint _readBuf;
    private static uint* _readMap;
    private static readonly IntPtr[] _fence = new IntPtr[ReadSlots];
    private static long _skippedReads, _frame;
    private static int _w, _h, _uAccum, _uReveal, _uViz, _uMode, _uDim, _cap;
    /// <summary><c>/draw oitviz</c>: 1 = revealage as grey, 2 = accumulated mean colour, 3 = accumulated weight/10,
    /// 4 = list length/16 (mode 2), 5 = the nearest node's object id as a colour (mode 2). Unblended.</summary>
    internal static int Viz;
    private static bool _failed, _listsFailed, _linked, _engagedLogged;
    private static ExtenderLog? _log;
    private static long _runs, _linkedRuns, _overflowFrames, _nodesPeak, _nodesSum, _gobPeak, _gobZero;

    internal static void Init(ExtenderLog log) => _log = log;
    /// <summary>The frame's own draw framebuffer, read at <see cref="Begin"/> and restored by <see cref="End"/>.</summary>
    private static int _restoreFbo;
    /// <summary>A program's <c>uOit</c> (0 outside the bracket, else the mode -- 1 when the lists refused)
    /// and <c>uOitDim</c> (head-table stride in pixels, node capacity), set on bind.</summary>
    internal static void Upload(int locOit, int locDim, bool inBracket)
    {
        Gl.Uniform1i(locOit, !inBracket ? 0 : _linked ? 2 : 1);
        UploadDim(locDim);
    }
    private static void UploadDim(int loc)
    {
        int* dim = stackalloc int[2] { _w, _cap };
        Gl.Uniform2iv(loc, 1, dim);
    }

    /// <summary>Open the accumulation pair for the blended range. False = draw the old way this frame.</summary>
    internal static bool Begin(uint depthTex, int* viewport)
    {
        if (_failed || Mode == 0 || depthTex == 0 || Gl.BlendFunci == null || Gl.DrawBuffers == null) return false;
        int w = viewport[0] + viewport[2], h = viewport[1] + viewport[3];
        if (!EnsureTargets(w, h) || !EnsurePrograms()) return false;
        _linked = Mode == 2 && ListsArmed && EnsureLists(w, h);
        _gobN = 0;
        int cur; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &cur); _restoreFbo = cur;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        // Mode 2 attaches no depth: the append's own compare is the depth test, and a depth texture that is
        // sampled while attached is decompressed by the driver, which taxed the NEXT frame's opaque pass.
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, _linked ? 0 : depthTex, 0);
        uint* bufs = stackalloc uint[2] { Gl.COLOR_ATTACHMENT0, Gl.COLOR_ATTACHMENT1 };
        Gl.DrawBuffers(2, bufs);
        float* zero = stackalloc float[4] { 0f, 0f, 0f, 0f };
        float* one = stackalloc float[4] { 1f, 1f, 1f, 1f };
        Gl.ClearBufferfv(Gl.COLOR, 0, zero);
        Gl.ClearBufferfv(Gl.COLOR, 1, one);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LEQUAL); Gl.DepthMask(0);
        Gl.Enable(Gl.BLEND);
        Gl.BlendFunci(0, Gl.ONE, Gl.ONE);
        Gl.BlendFunci(1, Gl.ZERO, Gl.ONE_MINUS_SRC_COLOR);
        if (_linked)
        {
            _frame++;
            int slot = (int)((_frame + 1) % ReadSlots);   // the slot End filled two frames ago
            IntPtr fence = _fence[slot];
            if (fence != IntPtr.Zero)
            {
                uint st = Gl.ClientWaitSync(fence, 0, 0);
                if (st == Gl.ALREADY_SIGNALED || st == Gl.CONDITION_SATISFIED)
                {
                    uint count = _readMap[slot];
                    if (count > (uint)_cap) _overflowFrames++;
                    if (count > _nodesPeak) _nodesPeak = count;
                    _nodesSum += count;
                }
                else _skippedReads++;
                Gl.DeleteSync(fence); _fence[slot] = IntPtr.Zero;
            }
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindHeads, _heads);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindNodes, _nodes);
            // last frame's resolve read the table; this frame's clear writes it, then the appends read it
            Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT);
            Gl.UseProgram(_progClear);
            Gl.Uniform1i(_uClearStride, _w);
            Gl.ColorMask(0, 0, 0, 0); Gl.Disable(Gl.DEPTH_TEST);
            Gl.BindVertexArray(PostChain.FullscreenVao);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
            Gl.BindVertexArray(0);
            Gl.ColorMask(1, 1, 1, 1);   // and the depth test stays OFF: the shader compare is the test in mode 2
            Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT);
            Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.OitDepth); Gl.BindTexture(Gl.TEXTURE_2D, depthTex);
            Gl.ActiveTexture(Gl.TEXTURE0);
            _linkedRuns++;
            if (!_engagedLogged) { _engagedLogged = true; _log?.Info($"OitPass: mode 2 engaged -- {_w}x{_h}, cap {_cap}, heads {_heads}, nodes {_nodes}"); }
        }
        _runs++;
        return true;
    }

    /// <summary>Composite the pair (or the lists) over the frame and hand the frame's framebuffer back. Leaves
    /// GL_BLEND on with the depth mask off, the state the passes after the blended class expect to find.</summary>
    internal static void End(int* viewport)
    {
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, 0, 0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)_restoreFbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        if (_linked) Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT);
        Gl.UseProgram(_prog);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.OitAccum); Gl.BindTexture(Gl.TEXTURE_2D, _accum);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.OitReveal); Gl.BindTexture(Gl.TEXTURE_2D, _reveal);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(_uAccum, (int)TextureSlots.OitAccum); Gl.Uniform1i(_uReveal, (int)TextureSlots.OitReveal);
        Gl.Uniform1i(_uViz, Viz);
        Gl.Uniform1i(_uMode, _linked ? 2 : 1);
        UploadDim(_uDim);
        new PassState(depth: false, depthWrite: false, blend: Viz == 0,
                      blendSrc: _linked ? Gl.ONE : Gl.ONE_MINUS_SRC_ALPHA, blendDst: Gl.SRC_ALPHA).Apply();
        Gl.BindVertexArray(PostChain.FullscreenVao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindVertexArray(0);
        // The draw loop tracks the depth test as ON and never re-enables it; the water and additive passes
        // after this one would otherwise draw through every wall.
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LEQUAL);
        if (_gobN > _gobPeak) _gobPeak = _gobN;
        if (_linked)
        {
            int slot = (int)(_frame % ReadSlots);
            Gl.MemoryBarrier(Gl.BUFFER_UPDATE_BARRIER_BIT);
            Gl.BindBuffer(Gl.COPY_READ_BUFFER, _heads); Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, _readBuf);
            Gl.CopyBufferSubData(Gl.COPY_READ_BUFFER, Gl.COPY_WRITE_BUFFER, IntPtr.Zero, (IntPtr)(slot * 4), (IntPtr)4);
            Gl.BindBuffer(Gl.COPY_READ_BUFFER, 0); Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, 0);
            _fence[slot] = Gl.FenceSync(Gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
        }
        _linked = false;
    }

    internal static string Stats(int frames)
    {
        long n = _runs, l = _linkedRuns, o = _overflowFrames, peak = _nodesPeak, sum = _nodesSum, gp = _gobPeak, gz = _gobZero, sk = _skippedReads;
        _runs = 0; _linkedRuns = 0; _overflowFrames = 0; _nodesPeak = 0; _nodesSum = 0; _gobPeak = 0; _gobZero = 0; _skippedReads = 0;
        if (_failed) return "oit STOOD DOWN";
        if (n == 0) return "oit idle";
        string s = $"oit {(float)n / System.Math.Max(frames, 1):F2} runs/frame at {_w}x{_h}";
        if (l > 0) s += $" linked {l} nodes avg {sum / System.Math.Max(l - sk, 1)} peak {peak} ({sk} reads not ready) cap {_cap} overflow {o} frames, objects {gp} peak/frame, {gz} ungrouped batches" + (_listsFailed ? " (lists REFUSED)" : "");
        return s;
    }

    internal static void ContextLost()
    {
        _fbo = 0; _accum = 0; _reveal = 0; _prog = 0; _progClear = 0; _nodes = 0; _heads = 0; _readBuf = 0; _readMap = null;
        for (int i = 0; i < ReadSlots; i++) _fence[i] = IntPtr.Zero;
        _w = 0; _h = 0; _cap = 0; _failed = false; _listsFailed = false; _linked = false;
    }

    private static bool EnsureTargets(int w, int h)
    {
        if (_accum != 0 && w == _w && h == _h) return true;
        if (w <= 0 || h <= 0) return false;
        if (_accum == 0)
        {
            uint* t = stackalloc uint[2]; Gl.GenTextures(2, t); _accum = t[0]; _reveal = t[1];
            uint f; Gl.GenFramebuffers(1, &f); _fbo = f;
        }
        // 32-bit: the accumulation is HDR colour times a weight up to 3e3, and a few stacked emissive flame
        // layers overflow fp16 to inf -- which resolved as a white patch over the lamp and the torch.
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, _accum);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)RGBA32F, w, h, 0, Gl.RGBA, Gl.FLOAT, null);
        RenderTarget.Clamp();
        Gl.BindTexture(Gl.TEXTURE_2D, _reveal);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)R16F, w, h, 0, Gl.RED, Gl.HALF_FLOAT, null);
        RenderTarget.Clamp();
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _accum, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, Gl.TEXTURE_2D, _reveal, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _failed = true;
            _log?.Info($"OitPass: the accumulation target is incomplete (0x{status:x}) -- blended draws the sorted way on this context");
            return false;
        }
        _w = w; _h = h;
        _cap = 0;   // the lists are sized to the target; a resize re-cuts them on the next mode-2 frame
        _log?.Info($"OitPass: accumulation pair {w}x{h} RGBA32F + R16F ready -- blended class is order-independent");
        return true;
    }

    /// <summary>The head tables and node pool for mode 2, cut to the current target. Immutable GPU-only stores.</summary>
    private static bool EnsureLists(int w, int h)
    {
        if (_listsFailed) return false;
        if (Gl.BufferStorage == null || Gl.ClearBufferSubData == null || Gl.CopyBufferSubData == null || Gl.FenceSync == null || Gl.MapBufferRange == null)
        {
            _listsFailed = true;
            _log?.Info("OitPass: a GL 4.4 entry point is unresolved -- mode 2 resolves the weighted way on this context");
            return false;
        }
        int cap = w * h * Layers;
        if (_nodes != 0 && cap == _cap) return true;
        if (_nodes != 0) { uint* old = stackalloc uint[2] { _nodes, _heads }; Gl.DeleteBuffers(2, old); }
        Gl.GetError();   // drain: the refusal test below must see only this allocation
        uint* b = stackalloc uint[2]; Gl.GenBuffers(2, b);
        _nodes = b[0]; _heads = b[1];
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _nodes);
        Gl.BufferStorage(Gl.SHADER_STORAGE_BUFFER, (IntPtr)((long)(cap + 1) * NodeBytes), null, 0);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _heads);
        Gl.BufferStorage(Gl.SHADER_STORAGE_BUFFER, (IntPtr)HeadBytes(w, h), null, 0);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        if (_readBuf == 0)
        {
            const uint flags = Gl.MAP_READ_BIT | Gl.MAP_PERSISTENT_BIT | Gl.MAP_COHERENT_BIT;
            uint r; Gl.GenBuffers(1, &r); _readBuf = r;
            Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, _readBuf);
            Gl.BufferStorage(Gl.COPY_WRITE_BUFFER, (IntPtr)(ReadSlots * 4), null, flags);
            _readMap = (uint*)Gl.MapBufferRange(Gl.COPY_WRITE_BUFFER, IntPtr.Zero, (IntPtr)(ReadSlots * 4), flags);
            Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, 0);
        }
        if (_progClear == 0)
        {
            _progClear = GlProgram.Graphics(PostChain.FullscreenVs, OitClearFs, _log, "OitPass clear");
            _uClearStride = GlProgram.Loc(_progClear, "uStride");
        }
        if (Gl.GetError() != 0 || _readMap == null || _progClear == 0)
        {
            _listsFailed = true;
            _log?.Info($"OitPass: the node pool ({(long)cap * NodeBytes >> 20} MB) or its readback refused -- mode 2 resolves the weighted way on this context");
            return false;
        }
        _cap = cap;
        Gl.ZeroBuffer(_heads, HeadBytes(w, h));   // once, before any GPU use: no sync to take
        _log?.Info($"OitPass: linked lists ready, {cap} nodes ({(long)cap * NodeBytes >> 20} MB, {Layers}/pixel) -- blended class sorts per pixel");
        return true;
    }

    private static bool EnsurePrograms()
    {
        if (_prog != 0) return true;
        _prog = GlProgram.Graphics(PostChain.FullscreenVs, OitResolveFs, _log, "OitPass resolve");
        if (_prog == 0) { _failed = true; return false; }
        _uAccum = GlProgram.Loc(_prog, "uAccum"); _uReveal = GlProgram.Loc(_prog, "uReveal"); _uViz = GlProgram.Loc(_prog, "uViz");
        _uMode = GlProgram.Loc(_prog, "uMode"); _uDim = GlProgram.Loc(_prog, "uOitDim");
        return true;
    }

    /// <summary>Composite. Mode 1: colour = accum.rgb / accum.a (the weighted mean), alpha = revealage, under
    /// ONE_MINUS_SRC_ALPHA / SRC_ALPHA. Mode 2: the pixel's list gathered into a depth-sorted local array and
    /// composited front to back; premultiplied colour plus transmittance under ONE / SRC_ALPHA. A list longer
    /// than the array spills its farthest nodes into an unsorted tail behind the sorted ones, and a frame
    /// whose count passed the capacity takes the mode 1 branch: nothing is dropped either way.</summary>
    /// <summary>Zero the head table and the count from the GPU, one texel per pixel. Colour writes are masked
    /// by the caller, so this is a buffer write with a rasteriser for an index generator.</summary>
    internal const string OitClearFs = """
        #version 460 core
        out vec4 frag;
        uniform int uStride;
        layout(std430, binding = 22) writeonly buffer SowxOitHeads { uint uOitCount; uint uOitHead[]; };
        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            uOitHead[p.y * uStride + p.x] = 0u;
            if (p.x == 0 && p.y == 0) uOitCount = 0u;
            frag = vec4(0.0);
        }
        """;

    internal const string OitResolveFs = """
        #version 460 core
        out vec4 frag;
        uniform sampler2D uAccum;
        uniform sampler2D uReveal;
        uniform int uViz;
        uniform int uMode;
        uniform ivec2 uOitDim;   // (head-table stride, node capacity)
        layout(std430, binding = 22) readonly buffer SowxOitHeads { uint uOitCount; uint uOitHead[]; };
        layout(std430, binding = 23) readonly buffer SowxOitNodes { uvec4 uOitNode[]; };
        // ponytail: 16 sorted layers a pixel; past that the far end composites unsorted. Raise if glass stacks deeper.
        const int K = 16;
        const int VizWalk = 64;   // uViz 4: nodes walked per pixel before the grey saturates
        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            float reveal = texelFetch(uReveal, p, 0).r;
            if (uViz == 1) { frag = vec4(vec3(reveal), 1.0); return; }
            if (uViz == 2) { vec4 a = texelFetch(uAccum, p, 0); frag = vec4(a.rgb / max(a.a, 1e-5), 1.0); return; }
            if (uViz == 3) { frag = vec4(vec3(texelFetch(uAccum, p, 0).a * 0.1), 1.0); return; }
            bool linked = uMode == 2 && uOitCount <= uint(uOitDim.y);
            if (uViz == 4)
            {
                // magenta = the list path is off this frame, yellow = the node pool overflowed; black = empty list
                if (uMode != 2) { frag = vec4(1.0, 0.0, 1.0, 1.0); return; }
                if (!linked) { frag = vec4(1.0, 1.0, 0.0, 1.0); return; }
                int n = 0;
                for (uint i = linked ? uOitHead[p.y * uOitDim.x + p.x] : 0u; i != 0u && n < VizWalk; i = uOitNode[i].x) n++;
                frag = vec4(vec3(float(n) / float(K)), 1.0); return;
            }
            if (uViz == 5)
            {
                // the NEAREST node's object id, hashed to a colour; white = id 0 (ungrouped), black = no list
                uint best = 0xFFFFFFFFu;
                for (uint i = linked ? uOitHead[p.y * uOitDim.x + p.x] : 0u; i != 0u; i = uOitNode[i].x) best = min(best, uOitNode[i].y);
                if (best == 0xFFFFFFFFu) { frag = vec4(0.0, 0.0, 0.0, 1.0); return; }
                uint g = best & 0xFFu;
                frag = g == 0u ? vec4(1.0) : vec4(float(g * 97u % 255u) / 255.0, float(g * 57u % 255u) / 255.0, float(g * 31u % 255u) / 255.0, 1.0);
                return;
            }
            if (linked)
            {
                uint head = uOitHead[p.y * uOitDim.x + p.x];
                if (head == 0u) discard;
                uint idx[K]; uint z[K];   // z = the node's key: depth in the high 24 bits, object id in the low 8
                int n = 0;
                vec3 tailC = vec3(0.0); float tailT = 1.0;
                for (uint i = head; i != 0u; i = uOitNode[i].x)
                {
                    uint d = uOitNode[i].y;
                    uint spill = 0u;
                    if (n == K)
                    {
                        if (d >= z[K - 1]) spill = i;
                        else { spill = idx[K - 1]; n = K - 1; }
                    }
                    if (spill != 0u)
                    {
                        // an object already kept nearer composites once: its farther parts are its own back faces
                        uint sg = uOitNode[spill].y & 0xFFu;
                        bool dup = false;
                        for (int k = 0; k < n && sg != 0u; k++) dup = dup || (z[k] & 0xFFu) == sg;
                        if (!dup)
                        {
                            vec4 c = vec4(unpackHalf2x16(uOitNode[spill].z), unpackHalf2x16(uOitNode[spill].w));
                            tailC += tailT * c.rgb * c.a; tailT *= 1.0 - c.a;
                        }
                        if (spill == i) continue;
                    }
                    int j = n++;
                    while (j > 0 && z[j - 1] > d) { z[j] = z[j - 1]; idx[j] = idx[j - 1]; j--; }
                    z[j] = d; idx[j] = i;
                }
                vec3 col = vec3(0.0); float T = 1.0;
                for (int k = 0; k < n; k++)
                {
                    uint g = z[k] & 0xFFu;
                    bool dup = false;
                    for (int m = 0; m < k && g != 0u; m++) dup = dup || (z[m] & 0xFFu) == g;
                    if (dup) continue;
                    vec4 c = vec4(unpackHalf2x16(uOitNode[idx[k]].z), unpackHalf2x16(uOitNode[idx[k]].w));
                    col += T * c.rgb * c.a; T *= 1.0 - c.a;
                }
                col += T * tailC; T *= tailT;
                frag = vec4(col, T);
                return;
            }
            if (!(reveal < 1.0)) discard;   // also NaN: nothing well-formed wrote here
            reveal = max(reveal, 0.0);
            vec4 accum = texelFetch(uAccum, p, 0);
            vec3 c = accum.rgb / max(accum.a, 1e-5);
            if (any(isinf(c)) || any(isnan(c))) c = vec3(0.0);
            // mode 2's blend pair is ONE / SRC_ALPHA: hand it the premultiplied form of the same composite
            frag = uMode == 2 ? vec4(c * (1.0 - reveal), reveal) : vec4(c, reveal);
        }
        """;
}
