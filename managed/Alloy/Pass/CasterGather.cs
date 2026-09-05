using System;
using System.Numerics;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #286 (M5 strand 2, ADR 0018): shadow caster selection in compute, in <see cref="CullPass"/>'s shape.
/// One staging pass per FRAME (batch keys, VAO and mask-texture resolves), then per view a handful of
/// uniforms, one dispatch that tests each caster against that view and compacts the survivors into an
/// indirect command buffer, and one <c>glMultiDrawElementsIndirectCount</c> per batch. No count ever comes
/// back to the CPU on the draw path.
/// <para>#303: the bounds are <see cref="CullPass"/>'s own <c>SowxCullSphere</c> buffer, indexed by
/// ownership slot -- the shadow cull and the colour cull test ONE set of spheres, with one set of
/// exemptions (a <see cref="AlloyRenderer.NeverCull"/> radius -- posed skinmesh, dangly mesh, displacing
/// feature, degenerate bounds -- passes every reach test, exactly as
/// the colour dispatch spells it). The per-draw <c>SowxCaster</c> record died with that: <c>baseInstance</c>
/// carries the ownership slot itself and the depth stages read <c>uDraw[gl_BaseInstance]</c> directly.</para>
/// <para>#303: the source buffer is PERSISTENT. The staging walk still runs per frame (index ranges are
/// re-read live because Repack moves them), but the upload is skipped whenever the staged bytes match what
/// the buffer already holds -- on a settled frame nothing goes up at all.</para>
/// <para>The cull ITSELF is ported semantics, not a re-derivation: the cone test circumscribes the 90deg face
/// pyramid and doubles the radius allowance, exactly as <c>CasterPass.FaceCulls</c> does, and the slope is
/// interpolated from that same constant so the two cannot drift. A caster wrongly REJECTED is a deleted
/// shadow; one wrongly admitted costs a draw. <c>/shadow gather 0</c> disables the cull in the dispatch --
/// every staged caster draws in every view -- which is the A/B that says whether the cull deleted a
/// shadow.</para>
/// </summary>
internal static unsafe class CasterGather
{
    /// <summary>SSBO bindings private to this dispatch. 0..3 are <see cref="AlloyLights"/>, 10/11
    /// <see cref="Particles"/>, 12..16 <see cref="CullPass"/> -- whose <see cref="CullPass.BindSphere"/> this
    /// dispatch also reads, because the bounds are the colour cull's own.</summary>
    internal const uint BindSrc = 17, BindCmd = 18, BindCount = 19;

    /// <summary>Uints per source record, and their order -- index count, first index, base vertex, OWNERSHIP
    /// slot (#303: the sphere index and the <c>baseInstance</c> the depth stages read <c>uDraw</c> through),
    /// batch, batch output base, and (#305) the DYNAMIC flag: 1 = this caster is a mover this frame. The
    /// flag is what <see cref="Dispatch"/>'s set filter tests, so one frame staging serves the static bake,
    /// the dynamic bake and the sun alike. Otherwise <see cref="CullPass.SrcUints"/>'s layout, deliberately:
    /// one shape for both gathers, and the batch's output base is what turns an <c>atomicAdd</c> into an
    /// output index.</summary>
    internal const int SrcUints = 7;
    /// <summary>The source word that carries the #305 set flag -- the one word the standing staging
    /// rewrites per frame (#328), so its index is a contract shared with <c>GatherCs</c>.</summary>
    internal const int SetFlagWord = 6;

    /// <summary>#305: which half of the population a dispatch admits. <see cref="SetAll"/> = no filter (the
    /// sun, and the combined bake <c>/shadow split 0</c> restores); <see cref="SetStatic"/> = still casters
    /// only; <see cref="SetDynamic"/> = movers only. Applied BEFORE the cull and independent of
    /// <c>/shadow gather 0</c>, because the filter is what the two arrays' contents MEAN, not a cull.</summary>
    internal const int SetAll = 0, SetStatic = 1, SetDynamic = 2;
    /// <summary>Uints per <c>DrawElementsIndirectCommand</c>. Fixed by GL.</summary>
    internal const int CmdUints = 5, CmdBytes = CmdUints * 4;
    private const int GroupSize = 64;
    /// <summary>Command/count buffer pairs rotated per VIEW. A frame issues ~200 views into these, and the
    /// hazard is a write-after-read: view K's indirect draws source their commands (and their count) from one
    /// pair while view K+Rotation's dispatch shader-writes it. The per-view barrier does not close that on its
    /// own -- it is issued AFTER its own dispatch, so at a rotation of 1 nothing at all separates the read
    /// from the rewrite.
    /// <para>The rotation depth is what closes it. MemoryBarrier "defines a barrier ordering the memory
    /// transactions issued prior to the command relative to those issued after" -- symmetric, and a
    /// write-after-read needs only that ordering, not visibility. Reusing a pair R views later leaves R-1
    /// barriers standing in command order between the last read of it and the rewrite, each naming
    /// COMMAND_BARRIER_BIT -- whose buffer set covers the PARAMETER_BUFFER binding too, per
    /// ARB_indirect_parameters, which is how the count side is included -- and BUFFER_UPDATE_BARRIER_BIT, for
    /// the per-view zeroing. So 2 is the proven floor, pinned by <c>CasterPass.SelfCheck</c>, and 3 is that
    /// floor with one spare.</para></summary>
    internal const int Rotation = 3;

    /// <summary>Live dial behind <c>/shadow gather</c>: 1 = the dispatch culls, 0 = the dispatch admits every
    /// staged caster into every view (#303: the CPU walk this used to fall back to is deleted, so the A/B is
    /// now cull-on against cull-off, the same pair <c>/draw cull</c> spells for the colour path). The third
    /// bisect step is <c>noshadows=0</c>, which hands the whole shadow question back to the engine's
    /// stencil.</summary>
    internal static int Mode = 1;

    private static ExtenderLog? _log;
    private static uint _prog;
    private static bool _failed;
    private static int _uDrawN, _uPoint, _uFocus, _uDir, _uReach, _uAlong, _uCull, _uSet;

    private static uint _srcBuf;
    private static int _srcCap;
    private static ulong _srcHash;
    private static readonly uint[] _cmdBuf = new uint[Rotation], _cntBuf = new uint[Rotation];
    private static int _cmdCap, _cntCap, _at;
    private static int _drawN, _groups;

    /// <summary>The latent count sample's ring: one copy per frame of the LAST view's per-batch counts, read
    /// back two frames later -- <see cref="CullPass"/>'s own instrument shape, so the culled-vs-drawn figure
    /// reaches the period line without a readback on the draw path.</summary>
    private static readonly uint[] _readBuf = new uint[3];
    private static readonly int[] _readN = new int[3];
    private static readonly int[] _readDrawN = new int[3];
    private static readonly int[] _readCap = new int[3];
    private static uint[] _counts = new uint[1024];
    private static int _readSeq;
    private static bool _dispatched;

    /// <summary>Views dispatched, batches submitted, and the latent sample's tally (survivors and staged
    /// draws of one view per frame, three frames late) -- the gather's half of the caster pass's period
    /// line.</summary>
    internal static long Views, Batches, SampledKept, SampledStaged, Samples;
    /// <summary>Frames whose staged source matched the persistent buffer byte for byte, so nothing was
    /// uploaded -- the number that says #303's "the per-frame upload is gone" is actually true.</summary>
    internal static long UploadsSkipped, Uploads;

    /// <summary>The program failed to build and will never arm; <see cref="CasterPass"/> disarms on it
    /// rather than idling forever.</summary>
    internal static bool Failed => _failed;

    internal static void Init(ExtenderLog log) => _log = log;

    /// <summary>The GL names went with the context; nothing is deleted (that would be a call into a dead
    /// context) and everything rebuilds on the next frame. See docs/owned-resource-lifetimes.md.</summary>
    internal static void ContextLost()
    {
        _prog = 0; _failed = false;
        _srcBuf = 0; _srcCap = 0; _srcHash = 0; _cmdCap = 0; _cntCap = 0; _at = 0;
        Array.Clear(_cmdBuf); Array.Clear(_cntBuf);
        _layerProg = 0; _layerFailed = false; _viewBuf = 0; _viewCap = 0;
        _lyCmdCap = 0; _lyCntCap = 0; _lyAt = 0;
        Array.Clear(_lyCmd); Array.Clear(_lyCnt);
        Array.Clear(_readBuf); Array.Clear(_readN); Array.Clear(_readDrawN); Array.Clear(_readCap);
        _drawN = 0; _groups = 0; _dispatched = false;
    }

    /// <summary>Everything the dispatch needs from GL, checked once and latched: a missing entry point is a
    /// permanent property of the driver and the gather then simply never arms.</summary>
    private static bool Capable()
        => Gl.DispatchCompute != null && Gl.MemoryBarrier != null && Gl.BindBufferBase != null
           && Gl.MultiDrawElementsIndirectCount != null && Gl.BufferSubData != null && Gl.BufferData != null;

    /// <summary>Build the dispatch program. Not an overridable stage (ADR 0012's table is what a content
    /// author may restyle): an override here could only delete shadows, and there is no look to restyle.</summary>
    internal static bool Ensure()
    {
        if (_prog != 0) return true;
        if (_failed || !Capable()) { _failed = true; return false; }
        _prog = GlProgram.Compute(GatherCs, _log, "CasterGather caster cull");
        if (_prog == 0) { _failed = true; return false; }
        _uDrawN = GlProgram.Loc(_prog, "uDrawN");
        _uPoint = GlProgram.Loc(_prog, "uPoint");
        _uFocus = GlProgram.Loc(_prog, "uFocus");
        _uDir = GlProgram.Loc(_prog, "uDir");
        _uReach = GlProgram.Loc(_prog, "uReach");
        _uAlong = GlProgram.Loc(_prog, "uAlong");
        _uCull = GlProgram.Loc(_prog, "uCull");
        _uSet = GlProgram.Loc(_prog, "uSet");
        _log?.Info("CasterGather: armed -- shadow caster selection in compute over the shared SowxCullSphere " +
                   "bounds, glMultiDrawElementsIndirectCount submission (/shadow gather 0 disables the cull)");
        return true;
    }

    /// <summary>Is the gather driving this frame's views with a non-empty staging? A frame that staged ZERO
    /// draws is not inactive -- its views still clear -- so <see cref="CasterPass"/> gates on its own staged
    /// flag, not on this.</summary>
    internal static bool Active => _prog != 0 && _drawN > 0;

    /// <summary>Forget the frame's staging. Called before every staging attempt, so a frame that stages
    /// nothing (a collect came back empty, an upload was refused) bakes empty views instead of baking the
    /// PREVIOUS frame's staging into this frame's views.</summary>
    internal static void Stand() => _drawN = 0;

    /// <summary>Publish one frame's staging. #303: the source buffer is persistent -- FNV-hashed against the
    /// last upload, and a frame whose staged bytes match sends nothing at all. The bounds live in
    /// <see cref="CullPass"/>'s sphere buffer, which the colour build maintains; nothing per-draw is uploaded
    /// here on a settled frame.</summary>
    internal static bool Begin(int drawN, int groups, uint* src)
    {
        _drawN = 0;
        if (drawN <= 0 || groups <= 0 || !Ensure()) return false;
        if (CullPass.SphereBuf == 0) return false;   // no colour build yet: no bounds to cull against

        long srcWords = (long)drawN * SrcUints;
        ulong h = 1469598103934665603ul;
        for (long i = 0; i < srcWords; i++) h = (h ^ src[i]) * 1099511628211ul;
        h = (h ^ (uint)drawN) * 1099511628211ul ^ (uint)groups;
        bool grow = drawN > _srcCap;
        if (_srcBuf == 0) { uint b; Gl.GenBuffers(1, &b); _srcBuf = b; grow = true; }
        if (grow || h != _srcHash)
        {
            Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _srcBuf);
            if (grow)
            { Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)((long)drawN * SrcUints * sizeof(uint)), null, Gl.DYNAMIC_DRAW); _srcCap = drawN; }
            Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, (IntPtr)(srcWords * sizeof(uint)), src);
            _srcHash = h;
            Uploads++;
        }
        else UploadsSkipped++;

        for (int i = 0; i < Rotation; i++)
        {
            Ensure(ref _cmdBuf[i], (long)drawN * CmdBytes, drawN > _cmdCap);
            Ensure(ref _cntBuf[i], (long)groups * sizeof(uint), groups > _cntCap);
        }
        if (drawN > _cmdCap) _cmdCap = drawN;
        if (groups > _cntCap) _cntCap = groups;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        _drawN = drawN; _groups = groups;
        return true;
    }

    private static void Ensure(ref uint buf, long bytes, bool grow)
    {
        if (buf == 0) { uint b; Gl.GenBuffers(1, &b); buf = b; grow = true; }
        if (!grow) return;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, buf);
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)Math.Max(bytes, 4), null, Gl.DYNAMIC_DRAW);
    }

    /// <summary>Select this view's casters. <paramref name="point"/> picks the cube face's cone test over the
    /// cascade fit's axis test; a <see cref="AlloyRenderer.NeverCull"/> sphere is exempt from the reach
    /// tests -- the same exemption, from the same buffer, the colour cull applies.</summary>
    internal static void Dispatch(Vector3 focus, Vector3 dir, float reach, float along, bool point,
                                  int set = SetAll)
    {
        _at = (_at + 1) % Rotation;
        Gl.ZeroBuffer(_cntBuf[_at], (long)_groups * sizeof(uint));

        Gl.UseProgram(_prog);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSrc, _srcBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, CullPass.BindSphere, CullPass.SphereBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _cmdBuf[_at]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCount, _cntBuf[_at]);
        Gl.Uniform1i(_uDrawN, _drawN);
        Gl.Uniform1i(_uPoint, point ? 1 : 0);
        Gl.Uniform1i(_uCull, Mode != 0 ? 1 : 0);
        Gl.Uniform1i(_uSet, set);
        float* v = stackalloc float[3];
        v[0] = focus.X; v[1] = focus.Y; v[2] = focus.Z;
        Gl.Uniform3fv(_uFocus, 1, v);
        v[0] = dir.X; v[1] = dir.Y; v[2] = dir.Z;
        Gl.Uniform3fv(_uDir, 1, v);
        Gl.Uniform1f(_uReach, reach);
        Gl.Uniform1f(_uAlong, along);
        Gl.DispatchCompute((uint)((_drawN + GroupSize - 1) / GroupSize), 1, 1);
        // The commands are consumed as indirect state and the counts as parameter-buffer state; neither is in
        // the shader-storage class, so SHADER_STORAGE alone leaves both undefined.
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.COMMAND_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _cmdBuf[_at]);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, _cntBuf[_at]);
        Views++;
        _dispatched = true;
    }

    /// <summary>One batch, out of the compacted list the dispatch just wrote. <paramref name="start"/> is the
    /// batch's own output base (the region the dispatch compacted into), <paramref name="batch"/> indexes the
    /// parameter buffer, and <paramref name="max"/> is the batch's staged length -- the ceiling GL clamps the
    /// count to.</summary>
    internal static void Draw(uint mode, int batch, int start, int max)
    {
        Gl.MultiDrawElementsIndirectCount(mode, CasterPass.GlUnsignedShort,
                                          (void*)((long)start * CmdBytes),
                                          (IntPtr)((long)batch * sizeof(uint)), max, CmdBytes);
        Batches++;
    }

    /// <summary>Hand the indirect bindings back, so nothing later in the frame -- the colour pass's own
    /// submission -- finds them bound.</summary>
    internal static void End()
    {
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, 0);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, 0);
    }

    /// <summary>Once per frame, after the views: copy the LAST dispatched view's per-batch counts aside and
    /// harvest the copy from two frames ago. One view per frame is a sample, not a total -- but it is the
    /// same latent-copy instrument <see cref="CullPass"/> uses, it costs no stall, and a cull that started
    /// deleting shadows shows in it immediately.</summary>
    internal static void Sample()
    {
        if (!_dispatched || _groups <= 0) return;
        // Sample-only entry points: a driver without them loses the figure, never the shadows.
        if (Gl.CopyBufferSubData == null || Gl.GetBufferSubData == null) return;
        _dispatched = false;
        int w = _readSeq % 3, r = (_readSeq + 1) % 3;
        _readSeq++;
        if (_groups > _counts.Length)
        {
            int cap = _counts.Length;
            while (cap < _groups) cap *= 2;
            _counts = new uint[cap];
        }
        // Capacity is PER RING SLOT: after a growth the other two slots still hold their old size, and a
        // shared cap would skip re-sizing them -- a copy past the end of a too-small buffer.
        Ensure(ref _readBuf[w], (long)_counts.Length * sizeof(uint),
               _readBuf[w] == 0 || _counts.Length > _readCap[w]);
        _readCap[w] = _counts.Length;
        // No extra barrier: the dispatch's own MemoryBarrier names BUFFER_UPDATE_BARRIER_BIT, which is the
        // bit that orders shader-storage writes before buffer-copy reads, and it stands between the last
        // dispatch's atomicAdds and this copy in command order.
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, _cntBuf[_at]);
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, _readBuf[w]);
        Gl.CopyBufferSubData(Gl.COPY_READ_BUFFER, Gl.COPY_WRITE_BUFFER, IntPtr.Zero, IntPtr.Zero,
                             (IntPtr)((long)_groups * sizeof(uint)));
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
        SampledKept += Math.Min(kept, _readDrawN[r]);
        SampledStaged += _readDrawN[r];
        Samples++;
    }

    // --- #307: the layered bake. One dispatch selects casters for MANY stale views at once -- one thread
    // per (draw, view) pair -- and writes layer-tagged commands into one command buffer, region per batch
    // sized draws x views. The depth stages then route by gl_Layer out of the view table, so the whole bake
    // for one target texture is one dispatch plus one MDI per batch instead of that pair per view.

    /// <summary>The view table's SSBO binding, and the packing of a command's <c>baseInstance</c>:
    /// bits [0, <see cref="ViewShift"/>) are the ownership slot, the bits above are the view-table index the
    /// vertex stage reads its fit matrix and layer from. <c>CasterPass.SelfCheck</c> pins the shift against
    /// <see cref="AlloyRenderer.MaxOwned"/>.</summary>
    internal const uint BindView = 20;
    internal const int ViewShift = 18;
    /// <summary>Floats per view-table record: mat4 vp + (focus, reach) + (dir, along) + (spare, point, set,
    /// layer). std430 gives this struct no padding.</summary>
    internal const int ViewFloats = 28;

    /// <summary>The view table's GLSL declaration, shared by the layered dispatch and both layered depth
    /// stages -- <c>AlloyRenderer.DrawBufDecl</c>'s pattern, so the three cannot drift apart.</summary>
    internal const string ViewTblDecl = """
        struct SowxView { mat4 vp; vec4 a; vec4 b; vec4 c; };   // a = focus/reach, b = dir/along, c = spare/point/set/layer
        layout(std430, binding = 20) readonly buffer SowxBakeViews { SowxView uViewTbl[]; };
        """;

    private static uint _layerProg;
    private static bool _layerFailed;
    private static int _luDrawN, _luViewN, _luCull;
    private static uint _viewBuf;
    private static int _viewCap;
    private static readonly uint[] _lyCmd = new uint[Rotation], _lyCnt = new uint[Rotation];
    private static long _lyCmdCap;
    private static int _lyCntCap, _lyAt;
    /// <summary>Layered dispatches issued, and views they covered -- the period line's halves.</summary>
    internal static long LayerDispatches, LayerViews;

    /// <summary>The layered dispatch program could not build; the caller keeps the per-view path.</summary>
    internal static bool LayerFailed => _layerFailed;

    /// <summary>Build the layered dispatch. Same non-overridable stance as <see cref="Ensure"/>.</summary>
    internal static bool EnsureLayer()
    {
        if (_layerProg != 0) return true;
        if (_layerFailed || !Capable()) { _layerFailed = true; return false; }
        _layerProg = GlProgram.Compute(LayerCs, _log, "CasterGather layered caster cull");
        if (_layerProg == 0) { _layerFailed = true; return false; }
        _luDrawN = GlProgram.Loc(_layerProg, "uDrawN");
        _luViewN = GlProgram.Loc(_layerProg, "uViewN");
        _luCull = GlProgram.Loc(_layerProg, "uCull");
        return true;
    }

    /// <summary>Select every queued view's casters in ONE dispatch: upload the chunk's view table, size the
    /// command buffer to draws x views, and leave the indirect/parameter bindings armed for
    /// <see cref="DrawLayer"/>. Same rotation argument as <see cref="Dispatch"/> -- chunks are serial, and
    /// reusing a pair <see cref="Rotation"/> chunks later leaves the barriers standing.</summary>
    internal static bool DispatchLayer(float* views, int viewN)
    {
        if (_layerProg == 0 || _drawN <= 0 || viewN <= 0 || CullPass.SphereBuf == 0) return false;
        if (_viewBuf == 0) { uint b; Gl.GenBuffers(1, &b); _viewBuf = b; _viewCap = 0; }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _viewBuf);
        if (viewN > _viewCap)
        { Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)((long)viewN * ViewFloats * sizeof(float)), null, Gl.DYNAMIC_DRAW); _viewCap = viewN; }
        Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, (IntPtr)((long)viewN * ViewFloats * sizeof(float)), views);

        _lyAt = (_lyAt + 1) % Rotation;
        long cmds = (long)_drawN * viewN;
        bool growCmd = cmds > _lyCmdCap, growCnt = _groups > _lyCntCap;
        for (int i = 0; i < Rotation; i++)
        {
            Ensure(ref _lyCmd[i], cmds * CmdBytes, growCmd);
            Ensure(ref _lyCnt[i], (long)_groups * sizeof(uint), growCnt);
        }
        if (growCmd) _lyCmdCap = cmds;
        if (growCnt) _lyCntCap = _groups;
        Gl.ZeroBuffer(_lyCnt[_lyAt], (long)_groups * sizeof(uint));

        Gl.UseProgram(_layerProg);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSrc, _srcBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, CullPass.BindSphere, CullPass.SphereBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _lyCmd[_lyAt]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCount, _lyCnt[_lyAt]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindView, _viewBuf);
        Gl.Uniform1i(_luDrawN, _drawN);
        Gl.Uniform1i(_luViewN, viewN);
        Gl.Uniform1i(_luCull, Mode != 0 ? 1 : 0);
        Gl.DispatchCompute((uint)((_drawN + GroupSize - 1) / GroupSize), (uint)viewN, 1);
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.COMMAND_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _lyCmd[_lyAt]);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, _lyCnt[_lyAt]);
        LayerDispatches++; LayerViews += viewN;
        return true;
    }

    /// <summary>One batch of the layered pass: the same indirect-count draw as <see cref="Draw"/>, with the
    /// batch's region and ceiling scaled by the chunk's view count.</summary>
    internal static void DrawLayer(uint mode, int batch, int start, int max, int viewN)
    {
        Gl.MultiDrawElementsIndirectCount(mode, CasterPass.GlUnsignedShort,
                                          (void*)((long)start * viewN * CmdBytes),
                                          (IntPtr)((long)batch * sizeof(uint)), max * viewN, CmdBytes);
        Batches++;
    }

    /// <summary>The cone slope, as the shader spells it. Interpolated from <see cref="CasterPass.FaceConeSlope"/>
    /// so the GPU test cannot drift from the CPU reference it is a port of, and pinned by
    /// <c>CasterPass.SelfCheck</c>.</summary>
    private static readonly string Slope =
        CasterPass.FaceConeSlope.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The cull. One thread per staged draw; a survivor appends into its batch's own output region
    /// with an <c>atomicAdd</c>, so every batch keeps its boundaries and only its interior order moves (depth
    /// draws are order-independent).
    /// <para>#303: the bounds come from <c>SowxCullSphere</c> -- the colour cull's own buffer, indexed by the
    /// ownership slot the source carries -- and a <c>NeverCull</c> radius (&gt;= 1e29, the exemption the
    /// colour dispatch also tests) skips every test: unknown or unbounded means CAST. Both view tests are the
    /// CPU reference's, term for term. The cone test is conservative BY CONSTRUCTION -- it circumscribes the
    /// 90deg face pyramid (corners at <c>|perp| = sqrt2 * t</c>) and allows twice the caster radius -- and
    /// that direction is the whole point: admitting a caster the face cannot see costs a depth draw,
    /// rejecting one it can see deletes a shadow.</para></summary>
    private static readonly string GatherCs = ("""
        #version 460 core
        layout(local_size_x = 64) in;   // GroupSize

        layout(std430, binding = 13) readonly buffer SowxCullSphere { vec4 uSph[]; };
        layout(std430, binding = 17) readonly buffer SowxGatherSrc  { uint uSrc[]; };
        layout(std430, binding = 18) writeonly buffer SowxGatherCmd { uint uCmd[]; };
        layout(std430, binding = 19) buffer SowxGatherCount         { uint uCnt[]; };
        uniform int uDrawN;
        uniform int uPoint;
        uniform int uCull;
        uniform int uSet;
        uniform vec3 uFocus;
        uniform vec3 uDir;
        uniform float uReach;
        uniform float uAlong;

        bool SowxFaceCulls(float dSq, float t, float r)
        {
            if (t < -r) return true;
            float lim = t * @SLOPE@ + 2.0 * r;
            return lim < 0.0 || dSq - t * t > lim * lim;
        }

        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            if (i >= uint(uDrawN)) return;
            uint b = i * 7u;
            // #305: the set filter, ahead of the cull and NOT gated on uCull -- which half of the split a
            // caster belongs in is what the two arrays' contents mean, not a cull /shadow gather 0 may waive.
            if (uSet == 1 && uSrc[b + 6u] != 0u) return;
            if (uSet == 2 && uSrc[b + 6u] == 0u) return;
            int slot = int(uSrc[b + 3u]);
            vec4 s = uSph[slot];
            // s.w >= 1e29 is NeverCull: bounds the colour cull exempts (posed skinmesh, dangly, displacing,
            // degenerate) are exempt here identically -- never reach-culled.
            if (uCull != 0 && s.w < 1e29)
            {
                float r = s.w;
                vec3 d = s.xyz - uFocus;
                float t = dot(d, uDir);
                bool offAxis, offEnds;
                if (uPoint != 0)
                {
                    float dSq = dot(d, d);
                    offAxis = dSq > (uReach + r) * (uReach + r);
                    offEnds = SowxFaceCulls(dSq, t, r);
                }
                else
                {
                    vec3 perp = d - t * uDir;
                    offAxis = dot(perp, perp) > (uReach + r) * (uReach + r);
                    offEnds = abs(t) > uAlong + r;
                }
                if (offAxis || offEnds) return;
            }
            uint o = uSrc[b + 5u] + atomicAdd(uCnt[uSrc[b + 4u]], 1u);
            uint q = o * 5u;
            uCmd[q] = uSrc[b];             // count
            uCmd[q + 1u] = 1u;             // instanceCount
            uCmd[q + 2u] = uSrc[b + 1u];   // firstIndex, in INDICES
            uCmd[q + 3u] = uSrc[b + 2u];   // baseVertex
            uCmd[q + 4u] = uint(slot);     // baseInstance IS the ownership slot the depth stages read uDraw at
        }
        """).Replace("@SLOPE@", Slope);

    /// <summary>#307: the layered cull -- <see cref="GatherCs"/>'s tests, term for term, with the view's
    /// parameters read out of the view table instead of uniforms and one thread per (draw, view) pair. A
    /// survivor's command region is its batch's staged region scaled by the chunk's view count, and its
    /// <c>baseInstance</c> packs the view-table index above the ownership slot so the depth stage can route
    /// by <c>gl_Layer</c>. The set filter stays AHEAD of the cull and ungated by <c>uCull</c>, for #305's
    /// reason: which half of the split a caster belongs in is what the arrays' contents MEAN.</summary>
    private static readonly string LayerCs = ("""
        #version 460 core
        layout(local_size_x = 64, local_size_y = 1) in;   // GroupSize

        layout(std430, binding = 13) readonly buffer SowxCullSphere { vec4 uSph[]; };
        layout(std430, binding = 17) readonly buffer SowxGatherSrc  { uint uSrc[]; };
        layout(std430, binding = 18) writeonly buffer SowxGatherCmd { uint uCmd[]; };
        layout(std430, binding = 19) buffer SowxGatherCount         { uint uCnt[]; };

        """ + ViewTblDecl + """
        uniform int uDrawN;
        uniform int uViewN;
        uniform int uCull;

        bool SowxFaceCulls(float dSq, float t, float r)
        {
            if (t < -r) return true;
            float lim = t * @SLOPE@ + 2.0 * r;
            return lim < 0.0 || dSq - t * t > lim * lim;
        }

        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            uint vi = gl_GlobalInvocationID.y;
            if (i >= uint(uDrawN) || vi >= uint(uViewN)) return;
            uint b = i * 7u;
            vec4 vc = uViewTbl[vi].c;
            int set = int(vc.z);
            if (set == 1 && uSrc[b + 6u] != 0u) return;
            if (set == 2 && uSrc[b + 6u] == 0u) return;
            int slot = int(uSrc[b + 3u]);
            vec4 s = uSph[slot];
            if (uCull != 0 && s.w < 1e29)
            {
                float r = s.w;
                vec3 d = s.xyz - uViewTbl[vi].a.xyz;
                float reach = uViewTbl[vi].a.w;
                vec3 dir = uViewTbl[vi].b.xyz;
                float t = dot(d, dir);
                bool offAxis, offEnds;
                if (vc.y != 0.0)
                {
                    float dSq = dot(d, d);
                    offAxis = dSq > (reach + r) * (reach + r);
                    offEnds = SowxFaceCulls(dSq, t, r);
                }
                else
                {
                    vec3 perp = d - t * dir;
                    offAxis = dot(perp, perp) > (reach + r) * (reach + r);
                    offEnds = abs(t) > uViewTbl[vi].b.w + r;
                }
                if (offAxis || offEnds) return;
            }
            uint o = uSrc[b + 5u] * uint(uViewN) + atomicAdd(uCnt[uSrc[b + 4u]], 1u);
            uint q = o * 5u;
            uCmd[q] = uSrc[b];             // count
            uCmd[q + 1u] = 1u;             // instanceCount
            uCmd[q + 2u] = uSrc[b + 1u];   // firstIndex, in INDICES
            uCmd[q + 3u] = uSrc[b + 2u];   // baseVertex
            uCmd[q + 4u] = uint(slot) | (vi << 18u);   // baseInstance = ownership slot | view index
        }
        """).Replace("@SLOPE@", Slope);

    /// <summary>What <c>CasterPass.SelfCheck</c> pins about the layered dispatch, on <see cref="SourceSafe"/>'s
    /// argument: every term whose silent drift deletes a shadow, spelled from the same constants.</summary>
    internal static bool LayerSafe()
        => LayerCs.Contains($"float lim = t * {Slope} + 2.0 * r;")
           && LayerCs.Contains("offEnds = abs(t) > uViewTbl[vi].b.w + r;")
           && LayerCs.Contains("offAxis = dot(perp, perp) > (reach + r) * (reach + r);")
           && LayerCs.Contains($"binding = {CullPass.BindSphere}") && LayerCs.Contains("SowxCullSphere")
           && LayerCs.Contains("if (uCull != 0 && s.w < 1e29)")
           && LayerCs.Contains("uint b = i * 7u;")
           && LayerCs.Contains("if (set == 1 && uSrc[b + 6u] != 0u) return;")
           && LayerCs.Contains("if (set == 2 && uSrc[b + 6u] == 0u) return;")
           // the pack: the shift is ViewShift on both sides, the batch region scales by the view count, and
           // the slot rides the low bits the depth stage masks back out
           && LayerCs.Contains($"uCmd[q + 4u] = uint(slot) | (vi << {ViewShift}u);")
           && LayerCs.Contains("uint o = uSrc[b + 5u] * uint(uViewN) + atomicAdd(uCnt[uSrc[b + 4u]], 1u);")
           && LayerCs.Contains($"binding = {BindView}")
           && LayerCs.Contains($"binding = {BindSrc}") && LayerCs.Contains($"binding = {BindCmd}")
           && LayerCs.Contains($"binding = {BindCount}");

    /// <summary>What <c>CasterPass.SelfCheck</c> pins about the dispatch: the cull it runs is the CPU
    /// reference's, over the SHARED sphere buffer, with the shared exemption, conservative in the direction
    /// that costs a draw. Text rather than arithmetic because GLSL cannot be run headless -- but every term
    /// that would silently delete a shadow if it drifted is named here, and the slope is interpolated from
    /// the C# constant rather than typed twice.</summary>
    internal static bool SourceSafe()
        => GatherCs.Contains("if (t < -r) return true;")
           && GatherCs.Contains($"float lim = t * {Slope} + 2.0 * r;")
           && GatherCs.Contains("return lim < 0.0 || dSq - t * t > lim * lim;")
           && GatherCs.Contains("offEnds = abs(t) > uAlong + r;")
           && GatherCs.Contains("offAxis = dot(perp, perp) > (uReach + r) * (uReach + r);")
           // #303: the shared bounds, at the colour cull's own binding, with its own exemption value.
           && GatherCs.Contains($"binding = {CullPass.BindSphere}") && GatherCs.Contains("SowxCullSphere")
           && GatherCs.Contains("if (uCull != 0 && s.w < 1e29)")
           && GatherCs.Contains("uCmd[q + 4u] = uint(slot);")
           // #305: the set filter reads the staged dynamic flag at the 7-uint stride, in BOTH directions --
           // a filter that lost either arm bakes the same caster into both arrays, and min() then hides it.
           && GatherCs.Contains("uint b = i * 7u;") && SrcUints == 7
           && GatherCs.Contains("if (uSet == 1 && uSrc[b + 6u] != 0u) return;")
           && GatherCs.Contains("if (uSet == 2 && uSrc[b + 6u] == 0u) return;")
           && GatherCs.Contains($"binding = {BindSrc}") && GatherCs.Contains($"binding = {BindCmd}")
           && GatherCs.Contains($"binding = {BindCount}");
}
