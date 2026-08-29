using System;
using System.Numerics;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #243 (ADR 0017 step 3): Alloy's own visibility. Until this existed the only visibility input the colour
/// path had was <c>PartTable.InBucketsRecently</c> -- "did the engine's bucket walk touch this part in the
/// last frame or two" -- which is a property of ENROLMENT CADENCE, not of what the camera can see. That is
/// why #236's pop-in was tuned with a window instead of answered.
/// <para>What replaces it: a compute dispatch over the per-draw list that tests each draw's world bounding
/// sphere against this pass's frustum (and, for the colour pass, against a hi-Z pyramid), compacts the
/// survivors into an indirect command buffer and writes a per-batch count. Submission is
/// <c>glMultiDrawElementsIndirectCount</c>, so the count never comes back to the CPU.</para>
/// <para><b>Same-frame, not reprojected.</b> The pyramid is built from #119's depth prepass INSIDE the
/// frame that consumes it: dispatch A (frustum only) feeds the prepass, the prepass depth becomes the
/// pyramid, dispatch B (frustum + hi-Z) feeds the colour pass. Reprojection was rejected because it buys
/// nothing here -- the prepass depth is already paid for, and a pyramid one frame stale is exactly the
/// silhouette-popping artefact the ticket names. What the prepass leaves out (cutouts, discarding classes
/// it drops, everything the engine still draws) only leaves the pyramid holding the FARTHER depth that is
/// actually behind them, and a farther max culls less. The direction that matters, and that #243's first
/// live run got wrong: the test is <c>objectNear &gt; maxSceneDepth</c>, so anything that LOWERS the max
/// culls MORE. Over-covering the sampled region is safe; under-covering it is the bug.</para>
/// <para><b>Opaque range only.</b> Compaction is an <c>atomicAdd</c>, so the order inside a batch is not
/// this frame's staging order. Opaque draws are order-independent; the blended and water ranges are sorted
/// back-to-front and stay on the <c>BaseVertex</c> path untouched. Cutout batches blend and DO ride the
/// indirect path -- overlapping cutouts can therefore composite in a different order from frame to frame.
/// Named rather than hidden: they depth-write with a discard, so the visible consequence is at most a
/// shimmer where two graded edges overlap, and <c>/draw cull 0</c> is the A/B that proves it either way.
/// </para>
/// <para>Stands down whole when 4.6 indirect-count or compute is missing, when the programs fail to build,
/// or when the knob is off -- and standing down means the caller's existing <c>BaseVertex</c> submission,
/// which is unchanged. A cull that cannot run must never mean an empty world.</para>
/// </summary>
internal static unsafe class CullPass
{
    /// <summary>SSBO bindings. 0..3 are <see cref="AlloyLights"/>, 4 the per-draw records, 5/6
    /// <see cref="CasterPass"/>, 7 the Alloy bones, 8 the bindless handles, 9 the per-draw index array,
    /// 10/11 <see cref="Particles"/>. Everything here is private to the dispatch except
    /// <see cref="AlloyRenderer.BindDrawIdx"/>, which the compacted slot buffer takes at DRAW time.</summary>
    internal const uint BindSrc = 12, BindSphere = 13, BindCmd = 14, BindOutSlot = 15, BindCount = 16;

    /// <summary>Uints per source record: index count, first index, base vertex, record slot, batch, and the
    /// batch's output base. Six rather than five because the batch's start offset is what turns an
    /// <c>atomicAdd</c> into an output index without a second buffer to look it up in.</summary>
    internal const int SrcUints = 6;
    /// <summary>Uints per <c>DrawElementsIndirectCommand</c>: count, instanceCount, firstIndex, baseVertex,
    /// baseInstance. Fixed by GL, and the stride the draw calls pass.</summary>
    internal const int CmdUints = 5;

    /// <summary>Compute workgroup size; the dispatch is 1D over draws.</summary>
    private const int GroupSize = 64;

    /// <summary>The two dispatch phases, and the two output sets they fill.</summary>
    internal const int PhasePrepass = 0, PhaseColour = 1;

    private static ExtenderLog? _log;
    private static uint _prog, _progHiZ;
    private static bool _failed;
    private static int _uDrawN, _uPlanes, _uHiZ, _uVP, _uPyr, _uPyrSize, _uMaxLod;
    private static int _hSrc, _hLevel;

    private static uint _srcBuf, _sphBuf;
    private static readonly uint[] _cmdBuf = new uint[2];
    private static readonly uint[] _slotBuf = new uint[2];
    private static readonly uint[] _cntBuf = new uint[2];
    private static int _cmdCap, _cntCap, _sphCap;

    private static uint _pyrTex, _pyrFbo, _pyrVao;
    private static int _pyrW, _pyrH, _pyrLevels;
    /// <summary>The pass generation the pyramid was last BUILT for. Not "a pyramid exists": a declined build
    /// (no depth texture, a zero viewport) would otherwise leave the previous frame's pyramid armed, which
    /// is the reprojected cull this pass exists to not be.</summary>
    private static int _pyrGen = -1;

    /// <summary>Staging for the source records. One entry per DRAW of this pass, rebuilt every pass exactly
    /// as the index array it replaces was -- the draw ORDER is per pass even though the records are not.</summary>
    private static readonly uint[] _src = new uint[AlloyRenderer.MaxOwned * SrcUints];
    private static uint[] _zero = new uint[1024];

    /// <summary>#243 (AC5): what the last readable dispatch actually kept. Copied out of the count buffer
    /// three frames late through a small ring, so the period line can report culled-vs-drawn without a
    /// readback anywhere near the submission path.</summary>
    private static readonly uint[] _readBuf = new uint[3];
    /// <summary>How many count words each ring slot actually holds. Without it a frame whose batch cut came
    /// out shorter reads the tail of a copy three frames older as if it were counts -- which is exactly the
    /// "keeps 7474870/frame" nonsense the first live run printed.</summary>
    private static readonly int[] _readN = new int[3];
    private static readonly int[] _readDrawN = new int[3];
    private static uint[] _counts = new uint[1024];
    private static int _readSeq, _readCap;
    internal static long CulledDraws, KeptDraws, CullFrames;

    /// <summary>Live dial behind <c>/draw cull</c>, and deliberately three-state rather than two: culling is
    /// the one system whose bug is invisible geometry, and "something is missing" has two possible authors
    /// here. <b>0</b> stands the whole pass down (both submissions back on <c>BaseVertex</c>, the engine's
    /// bucket walk back in charge of the draw-path gate); <b>1</b> is the frustum test alone; <b>2</b> adds
    /// the hi-Z occlusion test. Reporting a hole means saying which of the three it survives.</summary>
    internal static int Mode = ModeHiZ;
    internal const int ModeOff = 0, ModeFrustum = 1, ModeHiZ = 2;

    /// <summary>The pass can actually run: the programs built and every entry point it needs exists. Asked
    /// by the STAGING walk as well as by this class -- the walk drops the engine's visibility gate only when
    /// something is going to take over the question, and on a driver where nothing can, dropping it would
    /// mean drawing the whole area with no cull anywhere.</summary>
    internal static bool Ready => _prog != 0;

    /// <summary>#243: the extent of the staged sphere CENTRES, and the eye they were culled against. A
    /// diagnostic with one job: the bounds are derived from <c>PartTriMesh</c>'s bbox, which is world-space
    /// for a world-space part and object-space for a matrix-class one, and that reading has never been
    /// verified for TILES. If it is wrong for them every tile's sphere collapses toward one point, and this
    /// line says so -- a centre spread far smaller than the area is the signature.</summary>
    internal static float SpreadX, SpreadY, SpreadZ, SpreadMaxR;
    private static int _drawN, _groups, _phase = -1;

    internal static void Init(ExtenderLog log) => _log = log;

    /// <summary>The GL names are gone with the context; nothing is deleted (that would be a call into a
    /// dead context) and everything rebuilds on the next pass. See docs/owned-resource-lifetimes.md.</summary>
    internal static void ContextLost()
    {
        _prog = 0; _progHiZ = 0; _failed = false;
        _srcBuf = 0; _sphBuf = 0; _cmdCap = 0; _cntCap = 0; _sphCap = 0;
        Array.Clear(_cmdBuf); Array.Clear(_slotBuf); Array.Clear(_cntBuf);
        Array.Clear(_readBuf); Array.Clear(_readN); Array.Clear(_readDrawN); _readCap = 0;
        _pyrTex = 0; _pyrFbo = 0; _pyrVao = 0; _pyrW = 0; _pyrH = 0; _pyrLevels = 0; _pyrGen = -1;
        _phase = -1;
    }

    /// <summary>Everything the dispatch needs from GL. Checked once and latched: a missing entry point is a
    /// permanent property of the driver, and the pass simply never arms.</summary>
    private static bool Capable()
        => Gl.DispatchCompute != null && Gl.MemoryBarrier != null && Gl.BindBufferBase != null
           && Gl.MultiDrawElementsIndirectCount != null && Gl.BufferSubData != null
           && Gl.GenFramebuffers != null && Gl.FramebufferTexture2D != null && Gl.TexStorage2D != null
           && Gl.CopyBufferSubData != null && Gl.GetBufferSubData != null;

    /// <summary>Build the two programs. Neither is an overridable stage (ADR 0012's table is the set a
    /// content author can restyle): an override here could only make the world invisible, and there is no
    /// look to restyle.</summary>
    private static bool Ensure()
    {
        if (_prog != 0) return true;
        if (_failed || !Capable()) return false;
        _prog = GlProgram.Compute(CullCs, _log, "CullPass frustum/hi-Z cull");
        _progHiZ = GlProgram.Graphics(PostChain.FullscreenVs, HiZFs, _log, "CullPass hi-Z pyramid");
        if (_prog == 0 || _progHiZ == 0) { _failed = true; _prog = 0; return false; }
        _uDrawN = GlProgram.Loc(_prog, "uDrawN");
        _uPlanes = GlProgram.Loc(_prog, "uPlanes");
        _uHiZ = GlProgram.Loc(_prog, "uHiZ");
        _uVP = GlProgram.Loc(_prog, "uVP");
        _uPyr = GlProgram.Loc(_prog, "uPyramid");
        _uPyrSize = GlProgram.Loc(_prog, "uPyrSize");
        _uMaxLod = GlProgram.Loc(_prog, "uMaxLod");
        _hSrc = GlProgram.Loc(_progHiZ, "uSrc");
        _hLevel = GlProgram.Loc(_progHiZ, "uLevel");
        if (_pyrVao == 0) { uint v; Gl.GenVertexArrays(1, &v); _pyrVao = v; }
        _log?.Info("CullPass: armed -- GPU frustum + hi-Z cull, glMultiDrawElementsIndirectCount submission");
        return true;
    }

    /// <summary>Is the cull driving this pass's opaque submission? Asked by the two issue sites, and the
    /// ONLY thing that may switch them off the <c>BaseVertex</c> path.</summary>
    internal static bool Active => Mode > ModeOff && _phase >= 0;

    /// <summary>Stage the pass's opaque draw list and upload it, plus the per-record bounding spheres.
    /// Returns false when the cull is not running this pass, and the caller then submits exactly as before.
    /// <para><paramref name="groups"/> is the OPAQUE batch count: the blended and water ranges are sorted
    /// and never reach here.</para></summary>
    internal static bool Begin(int groups, int drawN, int* dCount, nint* dOff, int* dBase, int* dSlot,
                              int* gStart, int* gLen, float* spheres, int sphereSlots)
    {
        _phase = -1;
        if (Mode <= ModeOff || drawN <= 0 || groups <= 0 || !Ensure()) return false;
        // The per-batch count array is indexed by batch; the zero-fill in Dispatch is what resets it. GROWN
        // rather than capped: a refusal here would be a refusal AFTER the staging walk had already dropped
        // the engine's visibility gate on the strength of Ready, and the frame would then draw the whole
        // area unculled -- the cap has to not exist rather than be generous.
        if (groups > _zero.Length)
        {
            int cap = _zero.Length;
            while (cap < groups) cap *= 2;
            _zero = new uint[cap]; _counts = new uint[cap];
        }

        int n = 0;
        float lox = float.MaxValue, loy = float.MaxValue, loz = float.MaxValue;
        float hix = float.MinValue, hiy = float.MinValue, hiz = float.MinValue, maxR = 0f;
        fixed (uint* s = _src)
        for (int g = 0; g < groups; g++)
            for (int d = gStart[g]; d < gStart[g] + gLen[g]; d++)
            {
                uint* r = s + (long)n++ * SrcUints;
                r[0] = (uint)dCount[d];
                // The indirect command's firstIndex is in ELEMENTS; _dOff carries it pre-multiplied to the
                // byte offset MultiDrawElementsBaseVertex takes. Two-byte indices, so the halving is exact.
                r[1] = (uint)((long)dOff[d] / 2);
                r[2] = (uint)dBase[d];
                r[3] = (uint)dSlot[d];
                r[4] = (uint)g;
                r[5] = (uint)gStart[g];
                float* sp = spheres + (long)dSlot[d] * 4;
                if (sp[3] < AlloyRenderer.NeverCull)
                {
                    lox = MathF.Min(lox, sp[0]); hix = MathF.Max(hix, sp[0]);
                    loy = MathF.Min(loy, sp[1]); hiy = MathF.Max(hiy, sp[1]);
                    loz = MathF.Min(loz, sp[2]); hiz = MathF.Max(hiz, sp[2]);
                    maxR = MathF.Max(maxR, sp[3]);
                }
            }
        _drawN = n; _groups = groups;
        if (hix >= lox) { SpreadX = hix - lox; SpreadY = hiy - loy; SpreadZ = hiz - loz; SpreadMaxR = maxR; }

        if (_srcBuf == 0) { uint b; Gl.GenBuffers(1, &b); _srcBuf = b; }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _srcBuf);
        var srcSize = (IntPtr)((long)n * SrcUints * sizeof(uint));
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, srcSize, null, Gl.DYNAMIC_DRAW);
        fixed (uint* s = _src) Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, srcSize, s);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSrc, _srcBuf);

        // The spheres are indexed by RECORD slot, exactly as the records are, and go up whole. They are four
        // floats against the record's forty-four, and they change on the same edges -- so a dirty list here
        // would be #242's machinery again for a fortieth of the traffic.
        if (_sphBuf == 0) { uint b; Gl.GenBuffers(1, &b); _sphBuf = b; }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _sphBuf);
        var sphSize = (IntPtr)((long)sphereSlots * 4 * sizeof(float));
        if (sphereSlots > _sphCap) { Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, sphSize, null, Gl.DYNAMIC_DRAW); _sphCap = sphereSlots; }
        Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, sphSize, spheres);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSphere, _sphBuf);

        for (int i = 0; i < 2; i++)
        {
            Ensure(ref _cmdBuf[i], (long)n * CmdUints * sizeof(uint), n > _cmdCap);
            Ensure(ref _slotBuf[i], (long)n * sizeof(uint), n > _cmdCap);
            Ensure(ref _cntBuf[i], (long)groups * sizeof(uint), groups > _cntCap);
        }
        if (n > _cmdCap) _cmdCap = n;
        if (groups > _cntCap) _cntCap = groups;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        _phase = -2;   // staged; the first Dispatch publishes a phase
        return true;
    }

    private static void Ensure(ref uint buf, long bytes, bool grow)
    {
        if (buf == 0) { uint b; Gl.GenBuffers(1, &b); buf = b; grow = true; }
        if (!grow) return;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, buf);
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)Math.Max(bytes, 4), null, Gl.DYNAMIC_DRAW);
    }

    /// <summary>Run one dispatch into output set <paramref name="phase"/>. <paramref name="hiZ"/> false is
    /// the frustum-only pass the depth prepass consumes; true adds the occlusion test against the pyramid
    /// the prepass just filled.</summary>
    internal static void Dispatch(int phase, Matrix4x4* vp, bool hiZ, int gen)
    {
        if (_phase == -1 || _drawN <= 0) return;
        // Zero the counters. glClearBufferData is not in the loader, and a BufferSubData of a few hundred
        // bytes off a static zero array costs nothing next to the dispatch it precedes.
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _cntBuf[phase]);
        fixed (uint* z = _zero)
            Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, (IntPtr)((long)_groups * sizeof(uint)), z);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);

        Gl.UseProgram(_prog);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSrc, _srcBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSphere, _sphBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _cmdBuf[phase]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindOutSlot, _slotBuf[phase]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCount, _cntBuf[phase]);
        Gl.Uniform1i(_uDrawN, _drawN);
        Gl.UniformMatrix4fv(_uVP, 1, 0, (float*)vp);
        float* planes = stackalloc float[24];
        Planes((float*)vp, planes);
        Gl.Uniform4fv(_uPlanes, 6, planes);
        bool useHiZ = hiZ && Mode >= ModeHiZ && _pyrTex != 0 && _pyrLevels > 0 && _pyrGen == gen;
        Gl.Uniform1i(_uHiZ, useHiZ ? 1 : 0);
        if (useHiZ)
        {
            Gl.Uniform1i(_uPyr, (int)PyramidUnit);
            Gl.Uniform2f(_uPyrSize, _pyrW, _pyrH);
            Gl.Uniform1f(_uMaxLod, _pyrLevels - 1);
            Gl.ActiveTexture(Gl.TEXTURE0 + PyramidUnit);
            Gl.BindTexture(Gl.TEXTURE_2D, _pyrTex);
            Gl.ActiveTexture(Gl.TEXTURE0);
        }
        Gl.DispatchCompute((uint)((_drawN + GroupSize - 1) / GroupSize), 1, 1);
        // Both consumers are outside the shader-storage class: the commands are read as indirect state and
        // the count as parameter-buffer state. SHADER_STORAGE alone leaves both undefined.
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.COMMAND_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        // The pyramid's unit is shared with the probe's cube (TextureSlots.HiZ): leave nothing of ours on it.
        if (useHiZ)
        {
            Gl.ActiveTexture(Gl.TEXTURE0 + PyramidUnit);
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
            Gl.ActiveTexture(Gl.TEXTURE0);
        }
        _phase = phase;
        if (phase == PhaseColour) Sample();
    }

    /// <summary>Texture unit the pyramid takes for the dispatch -- see <see cref="TextureSlots.HiZ"/> for
    /// why it is shared rather than its own.</summary>
    private const uint PyramidUnit = TextureSlots.HiZ;

    /// <summary>Bind the compacted list of output set <paramref name="phase"/> for submission: the indirect
    /// commands, the parameter buffer the count comes from, and the per-draw slot array the vertex stages
    /// already read at <see cref="AlloyRenderer.BindDrawIdx"/>. The stages are untouched -- the compacted
    /// array holds the same record slots in a different order, so <c>uDraw[uDrawIdx[uDrawBase + gl_DrawID]]</c>
    /// resolves exactly as it did (and #119's clip expression never moves).</summary>
    internal static void BindForDraw(int phase)
    {
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _cmdBuf[phase]);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, _cntBuf[phase]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, AlloyRenderer.BindDrawIdx, _slotBuf[phase]);
    }

    /// <summary>...and hand the bindings back, so a later pass on the <c>BaseVertex</c> path (the blended
    /// and water ranges) does not find an indirect buffer bound under it.</summary>
    internal static void UnbindForDraw()
    {
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, 0);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, 0);
    }

    /// <summary>One batch, submitted from the compacted list. <paramref name="start"/> is the batch's
    /// output base -- the same <c>_gStart[g]</c> the record lookup uses as <c>uDrawBase</c>, because the
    /// cull compacts each batch into its OWN source region rather than into one global run -- and
    /// <paramref name="batch"/> indexes the parameter buffer, whose word <c>g</c> is that batch's survivor
    /// count. <paramref name="max"/> is the batch's staged length, the ceiling GL clamps the count to.</summary>
    internal static void Draw(uint mode, int batch, int start, int max)
        => Gl.MultiDrawElementsIndirectCount(mode, AlloyRenderer.GlUnsignedShort,
                                             (void*)((long)start * CmdUints * sizeof(uint)),
                                             (IntPtr)((long)batch * sizeof(uint)),
                                             max, CmdUints * sizeof(uint));

    /// <summary>Build the hi-Z pyramid from the depth the prepass just wrote. Level 0 is a 2x2 MAX reduction
    /// of the depth buffer (so the pyramid is half resolution and every level after it chains from the one
    /// above), and MAX is what makes the test conservative: an object is culled only when its NEAREST depth
    /// is behind the FARTHEST scene depth anywhere in the region it covers.
    /// <para>Restores the framebuffer, viewport and program the caller had -- the colour bracket is mid-pass
    /// and everything it set is still live.</para></summary>
    internal static void BuildHiZ(uint depthTex, int w, int h, int* viewport, uint restoreFbo, int gen)
    {
        _pyrGen = -1;
        if (_phase == -1 || _progHiZ == 0 || depthTex == 0 || w <= 0 || h <= 0) return;
        if (!EnsurePyramid(w / 2, h / 2)) return;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _pyrFbo);
        // NOT a PassState: this runs INSIDE the colour bracket, between the prepass and the opaque pass, and
        // the bracket's own state (depth test on, depth writes on, sRGB as the target declared it) has to be
        // exactly what the opaque pass finds. Only the two things a full-screen reduction cannot tolerate
        // are touched, and both are put back below.
        Gl.Disable(Gl.DEPTH_TEST);
        Gl.DepthMask(0);
        Gl.BindVertexArray(_pyrVao);
        Gl.UseProgram(_progHiZ);
        Gl.Uniform1i(_hSrc, (int)PyramidUnit);
        Gl.ActiveTexture(Gl.TEXTURE0 + PyramidUnit);
        for (int i = 0; i < _pyrLevels; i++)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _pyrTex, i);
            Gl.Viewport(0, 0, Math.Max(_pyrW >> i, 1), Math.Max(_pyrH >> i, 1));
            if (i == 0) { Gl.BindTexture(Gl.TEXTURE_2D, depthTex); Gl.Uniform1i(_hLevel, 0); }
            else
            {
                // Reading level i-1 of the texture level i is attached to: legal only while BASE/MAX_LEVEL
                // exclude the attachment, which is exactly what the clamp is for (PostChain's bloom chain
                // takes the same trade for the same reason).
                Gl.BindTexture(Gl.TEXTURE_2D, _pyrTex);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_BASE_LEVEL, i - 1);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, i - 1);
                Gl.Uniform1i(_hLevel, 0);
            }
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        }
        // The dispatch samples arbitrary LODs, so the clamp has to come back off.
        Gl.BindTexture(Gl.TEXTURE_2D, _pyrTex);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_BASE_LEVEL, 0);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, _pyrLevels - 1);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, restoreFbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);
        _pyrGen = gen;
    }

    private static bool EnsurePyramid(int w, int h)
    {
        w = Math.Max(w, 1); h = Math.Max(h, 1);
        if (_pyrTex != 0 && w == _pyrW && h == _pyrH) return true;
        if (_pyrTex != 0) { uint t = _pyrTex; Gl.DeleteTextures(1, &t); _pyrTex = 0; }
        int levels = 1;
        while ((w >> levels) > 0 || (h >> levels) > 0) levels++;
        uint tex; Gl.GenTextures(1, &tex);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        Gl.TexStorage2D(Gl.TEXTURE_2D, levels, Gl.R32F, w, h);
        // NEAREST all the way: an interpolated max is not a max, and a filtered pyramid would cull against
        // depths that are in no texel of it.
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST_MIPMAP_NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        if (_pyrFbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _pyrFbo = f; }
        _pyrTex = tex; _pyrW = w; _pyrH = h; _pyrLevels = levels;
        _log?.Info($"CullPass: hi-Z pyramid {w}x{h}, {levels} levels (R32F, max reduction)");
        return true;
    }

    /// <summary>#243 (AC5): the culled-vs-drawn figure, taken three frames behind the dispatch that produced
    /// it. A <c>glCopyBufferSubData</c> into a small ring is a GPU-side copy; the read that follows names a
    /// buffer the GPU finished with two frames ago, so it is not the readback the ticket forbids -- nothing
    /// on the submission path waits for it.</summary>
    private static void Sample()
    {
        if (_groups <= 0) return;
        int w = _readSeq % 3, r = (_readSeq + 1) % 3;
        _readSeq++;
        var bytes = (IntPtr)((long)_groups * sizeof(uint));
        Ensure(ref _readBuf[w], (long)_counts.Length * sizeof(uint),
               _readBuf[w] == 0 || _counts.Length > _readCap);
        _readCap = _counts.Length;
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, _cntBuf[PhaseColour]);
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, _readBuf[w]);
        Gl.CopyBufferSubData(Gl.COPY_READ_BUFFER, Gl.COPY_WRITE_BUFFER, IntPtr.Zero, IntPtr.Zero, bytes);
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, 0);
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, 0);
        _readN[w] = _groups; _readDrawN[w] = _drawN;
        if (_readBuf[r] == 0 || _readN[r] <= 0) return;
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, _readBuf[r]);
        fixed (uint* c = _counts)
            Gl.GetBufferSubData(Gl.COPY_READ_BUFFER, IntPtr.Zero, (IntPtr)((long)_readN[r] * sizeof(uint)), c);
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, 0);
        long kept = 0;
        for (int i = 0; i < _readN[r]; i++) kept += _counts[i];
        // Against the draw count of the frame that WROTE those counts, not this frame's.
        KeptDraws += Math.Min(kept, _readDrawN[r]);
        CulledDraws += Math.Max(_readDrawN[r] - kept, 0);
        CullFrames++;
    }

    internal static void ResetStats() { CulledDraws = 0; KeptDraws = 0; CullFrames = 0; }

    /// <summary>The six frustum planes of a row-major <c>view*proj</c>, Gribb-Hartmann, normalised so the
    /// plane distance is in world units and the sphere test is a plain compare. Row-major here means the
    /// combination is <c>clip = v * VP</c>, so the planes come off the COLUMNS -- the transpose of the form
    /// the paper writes for column-vector convention.</summary>
    private static void Planes(float* m, float* p)
    {
        // m[r*4+c] is row r, column c of the row-vector matrix: clip.c = sum_r v[r] * m[r*4+c].
        for (int i = 0; i < 6; i++)
        {
            int c = i >> 1;
            float s = (i & 1) == 0 ? 1f : -1f;
            float x = m[0 * 4 + 3] + s * m[0 * 4 + c];
            float y = m[1 * 4 + 3] + s * m[1 * 4 + c];
            float z = m[2 * 4 + 3] + s * m[2 * 4 + c];
            float w = m[3 * 4 + 3] + s * m[3 * 4 + c];
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (len > 0f) { x /= len; y /= len; z /= len; w /= len; }
            p[i * 4 + 0] = x; p[i * 4 + 1] = y; p[i * 4 + 2] = z; p[i * 4 + 3] = w;
        }
    }

    /// <summary>Headless check of the two pieces that are arithmetic rather than GL: the plane extraction
    /// and the source-record packing. Both fail silently in the worst possible way -- a wrong plane culls
    /// the world, a wrong pack draws a neighbour's geometry -- and neither needs a context.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"CullPass.SelfCheck FAIL: {w}"); } }

        // A right-handed look-down-(-Z) camera in the row-vector convention the renderer uses.
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 0), new Vector3(0, 0, -10), Vector3.UnitY);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1.0f, 1f, 100f);
        Matrix4x4 vp = view * proj;
        float* planes = stackalloc float[24];
        Planes((float*)&vp, planes);
        bool Inside(Vector3 pt, float r)
        {
            for (int i = 0; i < 6; i++)
                if (planes[i * 4] * pt.X + planes[i * 4 + 1] * pt.Y + planes[i * 4 + 2] * pt.Z
                    + planes[i * 4 + 3] < -r) return false;
            return true;
        }
        E(Inside(new Vector3(0, 0, -10), 1f), "a point straight ahead is inside the frustum");
        E(!Inside(new Vector3(0, 0, 10), 1f), "a point BEHIND the camera is outside it");
        E(!Inside(new Vector3(0, 0, -500), 1f), "a point past the far plane is outside it");
        E(!Inside(new Vector3(200, 0, -10), 1f), "a point far off to the side is outside it");
        E(Inside(new Vector3(200, 0, -10), 400f), "...and is back inside once its radius reaches the camera");

        // The pack, against what the shader reads out of it. Not a GL call in sight: this is a memory layout
        // shared with GLSL, and every field landing one slot over is a draw reading its neighbour's range.
        int* count = stackalloc int[2] { 30, 60 };
        nint* off = stackalloc nint[2] { 200, 400 };
        int* bas = stackalloc int[2] { 7, 9 };
        int* slot = stackalloc int[2] { 11, 13 };
        int* gStart = stackalloc int[1] { 0 };
        int* gLen = stackalloc int[1] { 2 };
        int n = 0;
        float lox = float.MaxValue, loy = float.MaxValue, loz = float.MaxValue;
        float hix = float.MinValue, hiy = float.MinValue, hiz = float.MinValue, maxR = 0f;
        fixed (uint* s = _src)
        for (int g = 0; g < 1; g++)
            for (int d = gStart[g]; d < gStart[g] + gLen[g]; d++)
            {
                uint* r = s + (long)n++ * SrcUints;
                r[0] = (uint)count[d]; r[1] = (uint)((long)off[d] / 2); r[2] = (uint)bas[d];
                r[3] = (uint)slot[d]; r[4] = (uint)g; r[5] = (uint)gStart[g];
            }
        E(_src[0] == 30 && _src[1] == 100 && _src[2] == 7 && _src[3] == 11 && _src[4] == 0 && _src[5] == 0,
          "draw 0 packs count/firstIndex/baseVertex/slot/batch/start in that order");
        E(_src[SrcUints + 1] == 200, "...and the byte offset becomes an ELEMENT index (two-byte indices)");
        E(CmdUints * sizeof(uint) == 20, "the indirect command stride is GL's 20 bytes");

        log.Info(ok ? "CullPass.SelfCheck: passed" : "CullPass.SelfCheck: FAILED -- see above");
    }

    /// <summary>The cull itself. One thread per draw; a survivor appends into its batch's own region with an
    /// atomicAdd, so the compacted list keeps every batch's boundaries and only its interior order moves.
    /// <para>A radius of infinity is "do not cull me" -- the CPU writes it for anything whose vertices move
    /// under a matrix that does not (skinned, dangly, a displacing feature), and for a degenerate bbox.
    /// Unknown bounds mean visible, always: the failure mode of this pass is invisible geometry.</para></summary>
    private const string CullCs = """
        #version 460 core
        layout(local_size_x = 64) in;   // GroupSize
        layout(std430, binding = 12) readonly buffer SowxCullSrc     { uint uSrc[]; };
        layout(std430, binding = 13) readonly buffer SowxCullSphere  { vec4 uSph[]; };
        layout(std430, binding = 14) writeonly buffer SowxCullCmd    { uint uCmd[]; };
        layout(std430, binding = 15) writeonly buffer SowxCullOutIdx { int  uOut[]; };
        layout(std430, binding = 16) buffer SowxCullCount            { uint uCnt[]; };
        uniform int uDrawN;
        uniform vec4 uPlanes[6];
        uniform mat4 uVP;
        uniform int uHiZ;
        uniform sampler2D uPyramid;
        uniform vec2 uPyrSize;
        uniform float uMaxLod;

        // One pyramid tap, addressed through the LEVEL-0 grid rather than the level's own.
        // This is the fix for #243's first live bug. `textureLod(uv, L)` maps uv across the width of level
        // L, which is floor(w0 / 2^L) -- and for a non-power-of-two screen that is NOT w0 / 2^L. At level 8
        // of a 1272-wide pyramid the level is 4 texels representing 1024 source columns, so uv 0.9 (source
        // column 1145) samples the texel covering columns 768..1023: a completely different part of the
        // screen, with a completely unrelated max depth. Large screen boxes take the high LODs, and a
        // distant tile's bounding cube IS a large screen box -- which is why the artefact was distant
        // tileset geometry vanishing while near geometry (whose box leaves the screen and exits above)
        // stayed. Converting to a level-0 texel first and shifting keeps the correspondence exact.
        float SowxPyr(vec2 uv, int lod)
        {
            ivec2 lsz = max(ivec2(uPyrSize) >> lod, ivec2(1));
            ivec2 t = clamp(ivec2(uv * uPyrSize) >> lod, ivec2(0), lsz - ivec2(1));
            return texelFetch(uPyramid, t, lod).r;
        }

        bool SowxOccluded(vec3 c, float r)
        {
            vec2 lo = vec2(1e30), hi = vec2(-1e30);
            float near = 1e30;
            for (int i = 0; i < 8; i++)
            {
                vec3 o = vec3((i & 1) != 0 ? r : -r, (i & 2) != 0 ? r : -r, (i & 4) != 0 ? r : -r);
                vec4 cp = uVP * vec4(c + o, 1.0);
                // Straddling the eye plane: the screen box is meaningless there, so do not cull.
                if (cp.w <= 0.0) return false;
                vec3 ndc = cp.xyz / cp.w;
                lo = min(lo, ndc.xy); hi = max(hi, ndc.xy); near = min(near, ndc.z);
            }
            // Partly off screen: CLAMP_TO_EDGE would answer for a region the box does not cover, and that
            // region's max can be FARTHER than the box's -- the one direction that culls something visible.
            if (lo.x < -1.0 || lo.y < -1.0 || hi.x > 1.0 || hi.y > 1.0) return false;
            vec2 uv0 = lo * 0.5 + 0.5, uv1 = hi * 0.5 + 0.5;
            vec2 px = (uv1 - uv0) * uPyrSize;
            // A level whose neighbourhood covers the box. Rounding UP over-covers, which only ever raises
            // the max it compares against, so it errs toward drawing.
            int lod = int(clamp(ceil(log2(max(max(px.x, px.y), 1.0))), 0.0, uMaxLod));
            float d = max(max(SowxPyr(uv0, lod), SowxPyr(vec2(uv1.x, uv0.y), lod)),
                          max(SowxPyr(vec2(uv0.x, uv1.y), lod), SowxPyr(uv1, lod)));
            return near * 0.5 + 0.5 > d;
        }

        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            if (i >= uint(uDrawN)) return;
            uint b = i * 6u;
            int slot = int(uSrc[b + 3u]);
            vec4 s = uSph[slot];
            if (s.w < 1e29)
            {
                for (int p = 0; p < 6; p++)
                    if (dot(uPlanes[p].xyz, s.xyz) + uPlanes[p].w < -s.w) return;
                if (uHiZ != 0 && SowxOccluded(s.xyz, s.w)) return;
            }
            uint o = uSrc[b + 5u] + atomicAdd(uCnt[uSrc[b + 4u]], 1u);
            uint c = o * 5u;
            uCmd[c] = uSrc[b];        // count
            uCmd[c + 1u] = 1u;        // instanceCount
            uCmd[c + 2u] = uSrc[b + 1u];   // firstIndex
            uCmd[c + 3u] = uSrc[b + 2u];   // baseVertex
            uCmd[c + 4u] = 0u;        // baseInstance
            uOut[o] = slot;
        }
        """;

    /// <summary>One level of the pyramid: the MAX of the source region the destination texel covers.
    /// <para>A 3x3 tap, not a 2x2, and that is a correctness fix rather than a refinement. GL sizes mip
    /// <c>i</c> as <c>floor(size / 2^i)</c>, so a source level with an ODD dimension has a last row (or
    /// column) that no 2x2 destination texel reads. The test downstream is
    /// <c>objectNear &gt; maxSceneDepth =&gt; occluded</c>, so a max that is missing texels fires the cull
    /// MORE readily -- dropping them is the dangerous direction, not the safe one. The third tap folds the
    /// remainder in; where there is no remainder it merely over-covers by one texel, which raises the max
    /// and therefore only ever draws more.</para></summary>
    private const string HiZFs = """
        #version 460 core
        uniform sampler2D uSrc;
        uniform int uLevel;
        in vec2 vUV;
        out vec4 oCol;
        void main()
        {
            ivec2 lim = textureSize(uSrc, uLevel) - ivec2(1);
            ivec2 s = ivec2(gl_FragCoord.xy) * 2;
            float m = 0.0;
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                    m = max(m, texelFetch(uSrc, min(s + ivec2(x, y), lim), uLevel).r);
            oCol = vec4(m);
        }
        """;
}
