using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Shadow caster selection in compute, in <see cref="CullPass"/>'s shape (ADR 0018): one staging pass per
/// FRAME, then per view a handful of uniforms, one dispatch that tests each caster and compacts the survivors
/// into an indirect command buffer, and one <c>glMultiDrawElementsIndirectCount</c> per batch -- no count ever
/// comes back to the CPU. The bounds are <see cref="CullPass"/>'s own spheres, so both culls test one set.
/// </summary>
internal static unsafe class CasterGather
{
    /// <summary>SSBO bindings private to this dispatch. 0..3 are <see cref="AlloyLights"/>, 10/11
    /// <see cref="Particles"/>, 12..16 <see cref="CullPass"/> -- whose <see cref="CullPass.BindSphere"/> this
    /// dispatch also reads, because the bounds are the colour cull's own.</summary>
    internal const uint BindSrc = 17, BindCmd = 18, BindCount = 19;

    /// <summary>Uints per source record, and their order -- index count, first index, base vertex, OWNERSHIP
    /// slot (the sphere index, and the <c>baseInstance</c> the depth stages read <c>uDraw</c> through), batch,
    /// batch output base, and the DYNAMIC flag: 1 = this caster is a mover this frame, which is what
    /// <see cref="Dispatch"/>'s set filter tests. Otherwise <see cref="CullPass.SrcUints"/>'s layout.</summary>
    internal const int SrcUints = 7;
    /// <summary>The source word that carries the set flag -- the one word the standing staging rewrites
    /// per frame, so its index is a contract shared with <c>GatherCs</c>.</summary>
    internal const int SetFlagWord = 6;

    /// <summary>Which half of the population a dispatch admits. <see cref="SetAll"/> = no filter (the
    /// sun, and the combined bake <c>/shadow split 0</c> restores); <see cref="SetStatic"/> = still casters
    /// only; <see cref="SetDynamic"/> = movers only. Applied BEFORE the cull and independent of
    /// <c>/shadow gather 0</c>, because the filter is what the two arrays' contents MEAN, not a cull.</summary>
    internal const int SetAll = 0, SetStatic = 1, SetDynamic = 2;
    /// <summary>Uints per <c>DrawElementsIndirectCommand</c>. Fixed by GL.</summary>
    internal const int CmdUints = 5, CmdBytes = CmdUints * 4;
    private const int GroupSize = 64;
    /// <summary>Command/count buffer pairs rotated per VIEW, against a write-after-read: view K's indirect
    /// draws source their commands from one pair while view K+Rotation's dispatch rewrites it. Reusing a pair
    /// R views later leaves R-1 symmetric barriers standing between the last read and the rewrite, so 2 is the
    /// proven floor (pinned by <c>CasterPass.SelfCheck</c>) and 3 is that floor with one spare.</summary>
    internal const int Rotation = 3;

    /// <summary>Live dial behind <c>/shadow gather</c>: 1 = the dispatch culls, 0 = it admits every staged
    /// caster into every view, the same A/B pair <c>/alloy cull</c> spells for the colour path. The third
    /// bisect step is <c>shadow.mode=0</c>, which hands the whole shadow question back to the engine's
    /// stencil.</summary>
    internal static int Mode => Shadows.Gather.V ? 1 : 0;
    /// <summary>Live dial behind <c>/shadow receiver</c>: 1 = a view flagged as re-baked under this
    /// frame's camera also rejects casters whose shadow volume cannot reach the camera frustum; 0 = the
    /// light-cone tests alone. Inside <see cref="Mode"/>'s gate, so <c>/shadow gather 0</c> still disables
    /// everything. Its own dial because the bake A/B has to isolate this term from the cone cull.</summary>
    internal static int Receiver => Shadows.Receiver.V ? 1 : 0;
    /// <summary>This frame's receiver frustum, six planes as <c>ShadowMath.ReceiverPlanes</c> writes
    /// them (already pushed out by the camera epoch's slack). Filled by <c>CasterPass</c> once per frame.</summary>
    internal static readonly float[] Planes = new float[24];

    private static ExtenderLog? _log;
    private static uint _prog;
    private static bool _failed;
    private static int _uDrawN, _uPoint, _uFocus, _uDir, _uReach, _uAlong, _uCull, _uSet, _uRx, _uGroups, _uPlanes;

    private static uint _srcBuf;
    private static int _srcCap;
    /// <summary>The DYNAMIC source -- a copy of just the staged records whose set flag is 1, in the same
    /// 7-word layout and carrying their OWN batch id and output base, so a survivor lands in exactly the
    /// region and the batch it would have from the full source. A dynamic view's dispatch then covers a few
    /// hundred draws instead of tens of thousands, with nothing else about a survivor changed.</summary>
    private static uint _dynSrcBuf;
    private static int _dynSrcCap, _dynDrawN;
    /// <summary>Dispatches that ran over the dynamic source rather than the full staging -- against
    /// <see cref="Views"/> plus <see cref="LayerDispatches"/>, the period line's witness for S8.</summary>
    internal static long DynDispatches;
    /// <summary>A caller reported changed bytes that have not reached the buffer yet (a refused Begin keeps it).</summary>
    private static bool _srcDirty;
    private static readonly uint[] _cmdBuf = new uint[Rotation], _cntBuf = new uint[Rotation];
    private static int _cmdCap, _cntCap, _at;
    private static int _drawN, _groups;

    /// <summary>The latent count sample's ring: one copy per frame of the LAST view's per-batch counts, read
    /// back two frames later -- <see cref="CullPass"/>'s own instrument shape, so the culled-vs-drawn figure
    /// reaches the period line without a readback on the draw path.</summary>
    private static GpuReadback _sampleRead;
    private static readonly int[] _readN = new int[3];
    private static readonly int[] _readDrawN = new int[3];
    /// <summary>Capacity tracker for a sample slot: the per-batch counts plus the receiver tally.</summary>
    private static uint[] _counts = new uint[1024];
    private static int _readSeq;
    private static bool _dispatched;
    /// <summary>The count buffer the last dispatch (per-view or layered) wrote, and how many views it
    /// covered -- what <see cref="Sample"/> copies aside. The layered path is the default one, so a sample
    /// of the per-view buffer alone would read nothing all session.</summary>
    private static uint _sampleBuf;
    private static int _sampleViews, _sampleRxViews;
    /// <summary>Draws the sampled dispatch actually covered -- the DYNAMIC source's count when it ran over
    /// that, so the kept/staged pair compares like with like instead of against the full staging -- and
    /// whether that is what it was.</summary>
    private static int _sampleDrawN;
    private static bool _sampleDyn;
    private static readonly int[] _readViews = new int[3], _readRxViews = new int[3];
    /// <summary>Did the dispatch this slot sampled run on the dynamic source? Decides which pair it folds into.</summary>
    private static readonly bool[] _readDyn = new bool[3];

    /// <summary>Views dispatched, batches submitted, and the latent sample's tally (survivors and staged
    /// draws of one view per frame, three frames late) -- the gather's half of the caster pass's period
    /// line.</summary>
    internal static long Views, Batches, SampledKept, SampledStaged, Samples;
    /// <summary>S8: the same three over the samples whose dispatch ran on the DYNAMIC source. Kept apart
    /// because the two populations are two orders of magnitude apart -- averaging ~438 staged draws with ~37k
    /// gives a figure that describes neither.</summary>
    internal static long SampledKeptDyn, SampledStagedDyn, SamplesDyn, SampledRxDyn;
    /// <summary>Of the sampled view's staged draws, how many the receiver-frustum test rejected
    /// (the word after the per-batch counts, <c>uCnt[uGroups]</c>).</summary>
    internal static long SampledRx;
    /// <summary>Frames whose staged source matched the persistent buffer byte for byte, so nothing was
    /// uploaded -- the number that says the per-frame upload is really gone.</summary>
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
        _srcBuf = 0; _srcCap = 0; _srcDirty = false; _cmdCap = 0; _cntCap = 0; _at = 0;
        _dynSrcBuf = 0; _dynSrcCap = 0; _dynDrawN = 0;
        Array.Clear(_cmdBuf); Array.Clear(_cntBuf);
        _layerProg = 0; _layerFailed = false; _viewBuf = 0; _viewCap = 0;
        _lyCmdCap = 0; _lyCntCap = 0; _lyAt = 0;
        Array.Clear(_lyCmd); Array.Clear(_lyCnt);
        _sampleRead.ContextLost(); Array.Clear(_readN); Array.Clear(_readDrawN); Array.Clear(_readViews); Array.Clear(_readRxViews);
        _drawN = 0; _groups = 0; _dispatched = false; _sampleBuf = 0; _sampleViews = 0; _sampleDrawN = 0;
        _sampleDyn = false; Array.Clear(_readDyn);
    }

    /// <summary>Everything the dispatch needs from GL, checked once and latched: a missing entry point is a
    /// permanent property of the driver and the gather then simply never arms.</summary>
    private static bool Capable()
        => Gl.DispatchCompute != null && Gl.MemoryBarrier != null && Gl.BindBufferBase != null
           && Gl.MultiDrawElementsIndirectCount != null;

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
        _uRx = GlProgram.Loc(_prog, "uRx");
        _uGroups = GlProgram.Loc(_prog, "uGroups");
        _uPlanes = GlProgram.Loc(_prog, "uPlanes");
        _log?.Info("CasterGather: armed -- shadow caster selection in compute over the shared CxCullSphere " +
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
    internal static void Stand() { _drawN = 0; _dynDrawN = 0; }

    /// <summary>S8: publish this frame's dynamic source. Uploaded whole every frame it is used -- it is a few
    /// hundred records, and its membership moves every frame by construction. False = there is none, and a
    /// dynamic view falls back to the full source, which is the same set by a longer road.</summary>
    internal static bool DynamicSource(int drawN, uint* src)
    {
        _dynDrawN = 0;
        if (_prog == 0 || drawN <= 0) return false;
        if (_dynSrcBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _dynSrcBuf = b; _dynSrcCap = 0; }
        long bytes = (long)drawN * SrcUints * sizeof(uint);
        if (drawN > _dynSrcCap)
        { Gl.NamedBufferData(_dynSrcBuf, (IntPtr)bytes, null, Gl.DYNAMIC_DRAW); _dynSrcCap = drawN; }
        Gl.NamedBufferSubData(_dynSrcBuf, IntPtr.Zero, (IntPtr)bytes, src);
        _dynDrawN = drawN;
        return true;
    }

    /// <summary>No dynamic source this frame: every view dispatches over the full staging, as before S8.</summary>
    internal static void StandDynamic() => _dynDrawN = 0;

    /// <summary>Is the dynamic source standing for this frame?</summary>
    internal static bool DynamicReady => _dynDrawN > 0 && _dynSrcBuf != 0;

    /// <summary>Publish one frame's staging. The source buffer is persistent, and a frame whose caller
    /// reports no <paramref name="changed"/> bytes sends nothing at all (the caller knows; no hash). The
    /// bounds live in <see cref="CullPass"/>'s sphere buffer, which the colour build maintains, so nothing
    /// per-draw is uploaded here on a settled frame.</summary>
    internal static bool Begin(int drawN, int groups, uint* src, bool changed)
    {
        _drawN = 0;
        _srcDirty |= changed;
        if (drawN <= 0 || groups <= 0 || !Ensure()) return false;
        if (CullPass.SphereBuf == 0) return false;   // no colour build yet: no bounds to cull against

        long srcWords = (long)drawN * SrcUints;
        bool grow = drawN > _srcCap;
        if (_srcBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _srcBuf = b; grow = true; }
        if (grow || _srcDirty)
        {
            if (grow)
            { Gl.NamedBufferData(_srcBuf, (IntPtr)((long)drawN * SrcUints * sizeof(uint)), null, Gl.DYNAMIC_DRAW); _srcCap = drawN; }
            Gl.NamedBufferSubData(_srcBuf, IntPtr.Zero, (IntPtr)(srcWords * sizeof(uint)), src);
            _srcDirty = false;
            Uploads++;
        }
        else UploadsSkipped++;

        for (int i = 0; i < Rotation; i++)
        {
            Gl.EnsureStorage(ref _cmdBuf[i], (long)drawN * CmdBytes, drawN > _cmdCap);
            Gl.EnsureStorage(ref _cntBuf[i], (long)(groups + 1) * sizeof(uint), groups > _cntCap);
        }
        if (drawN > _cmdCap) _cmdCap = drawN;
        if (groups > _cntCap) _cntCap = groups;
        _drawN = drawN; _groups = groups;
        return true;
    }

    /// <summary>Select this view's casters. <paramref name="point"/> picks the cube face's cone test over the
    /// cascade fit's axis test; a <see cref="AlloyRenderer.NeverCull"/> sphere is exempt from the reach
    /// tests -- the same exemption, from the same buffer, the colour cull applies.</summary>
    internal static void Dispatch(Vector3 focus, Vector3 dir, float reach, float along, bool point,
                                  int set = SetAll, bool receiver = false, bool dynSrc = false)
    {
        _at = (_at + 1) % Rotation;
        Gl.ZeroBuffer(_cntBuf[_at], (long)(_groups + 1) * sizeof(uint));
        // S8: the source, and with it the thread count. Everything downstream is indexed by the records'
        // OWN batch and output base, so the shorter source writes into the same regions.
        bool dyn = dynSrc && DynamicReady;
        int drawN = dyn ? _dynDrawN : _drawN;
        if (dyn) DynDispatches++;

        Gl.UseProgram(_prog);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSrc, dyn ? _dynSrcBuf : _srcBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, CullPass.BindSphere, CullPass.SphereBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _cmdBuf[_at]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCount, _cntBuf[_at]);
        Gl.Uniform1i(_uDrawN, drawN);
        Gl.Uniform1i(_uPoint, point ? 1 : 0);
        Gl.Uniform1i(_uCull, Mode != 0 ? 1 : 0);
        Gl.Uniform1i(_uSet, set);
        Gl.Uniform1i(_uRx, receiver && Receiver != 0 ? 1 : 0);
        Gl.Uniform1i(_uGroups, _groups);
        fixed (float* pl = Planes) Gl.Uniform4fv(_uPlanes, 6, pl);
        float* v = stackalloc float[3];
        v[0] = focus.X; v[1] = focus.Y; v[2] = focus.Z;
        Gl.Uniform3fv(_uFocus, 1, v);
        v[0] = dir.X; v[1] = dir.Y; v[2] = dir.Z;
        Gl.Uniform3fv(_uDir, 1, v);
        Gl.Uniform1f(_uReach, reach);
        Gl.Uniform1f(_uAlong, along);
        Gl.DispatchCompute((uint)((drawN + GroupSize - 1) / GroupSize), 1, 1);
        // The commands are consumed as indirect state and the counts as parameter-buffer state; neither is in
        // the shader-storage class, so SHADER_STORAGE alone leaves both undefined.
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.COMMAND_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _cmdBuf[_at]);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, _cntBuf[_at]);
        Views++;
        _dispatched = true; _sampleBuf = _cntBuf[_at]; _sampleViews = 1; _sampleRxViews = receiver ? 1 : 0;
        _sampleDrawN = drawN; _sampleDyn = dyn;
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
        if (!_dispatched || _groups <= 0 || _sampleBuf == 0 || !GpuReadback.EntryPoints) return;
        // Sample-only entry points: a driver without them loses the figure, never the shadows.
        _dispatched = false;
        int w = _readSeq % GpuReadback.Slots, r = (_readSeq + 1) % GpuReadback.Slots;
        _readSeq++;
        if (_groups + 1 > _counts.Length)
        {
            int cap = _counts.Length;
            while (cap < _groups + 1) cap *= 2;
            _counts = new uint[cap];
        }
        // a regrow drops every slot in flight, so none is read at a size it was not written at
        long need = (long)_counts.Length * sizeof(uint);
        if (need > _sampleRead.SlotBytes) { Array.Clear(_readN); if (!_sampleRead.Ensure(need)) return; }
        // No extra barrier: the dispatch's own MemoryBarrier names BUFFER_UPDATE_BARRIER_BIT, which is the
        // bit that orders shader-storage writes before buffer-copy reads, and it stands between the last
        // dispatch's atomicAdds and this copy in command order.
        _sampleRead.Copy(_sampleBuf, 0, w, 0, (long)(_groups + 1) * sizeof(uint));
        _sampleRead.Fence(w);
        _readN[w] = _groups; _readDrawN[w] = _sampleDrawN > 0 ? _sampleDrawN : _drawN;
        _readViews[w] = _sampleViews; _readRxViews[w] = _sampleRxViews; _readDyn[w] = _sampleDyn;
        if (_readN[r] <= 0) return;
        uint* c = (uint*)_sampleRead.Ready(r);
        if (c == null) return;
        long kept = 0;
        for (int i = 0; i < _readN[r]; i++) kept += c[i];
        // Against the draw count of the frame that WROTE those counts, not this frame's; a layered chunk's
        // counts cover every view it dispatched, so they are averaged back to one view -- the receiver
        // tally over the views that carried the flag.
        int views = Math.Max(_readViews[r], 1), rxViews = Math.Max(_readRxViews[r], 1);
        long keptV = Math.Min(kept / views, _readDrawN[r]), rx = Math.Min(c[_readN[r]] / rxViews, _readDrawN[r]);
        if (_readDyn[r])
        { SampledKeptDyn += keptV; SampledRxDyn += rx; SampledStagedDyn += _readDrawN[r]; SamplesDyn++; }
        else
        { SampledKept += keptV; SampledRx += rx; SampledStaged += _readDrawN[r]; Samples++; }
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
    internal static string ViewTblDecl => ShaderSource.Get("cx_viewtbl.glsl");

    private static uint _layerProg;
    private static bool _layerFailed;
    private static int _luDrawN, _luViewN, _luCull, _luRx, _luGroups, _luPlanes;
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
        _luRx = GlProgram.Loc(_layerProg, "uRx");
        _luGroups = GlProgram.Loc(_layerProg, "uGroups");
        _luPlanes = GlProgram.Loc(_layerProg, "uPlanes");
        return true;
    }

    /// <summary>Select every queued view's casters in ONE dispatch: upload the chunk's view table, size the
    /// command buffer to draws x views, and leave the indirect/parameter bindings armed for
    /// <see cref="DrawLayer"/>. Same rotation argument as <see cref="Dispatch"/> -- chunks are serial, and
    /// reusing a pair <see cref="Rotation"/> chunks later leaves the barriers standing.</summary>
    internal static bool DispatchLayer(float* views, int viewN, bool dynSrc = false)
    {
        if (_layerProg == 0 || _drawN <= 0 || viewN <= 0 || CullPass.SphereBuf == 0) return false;
        // S8, as in Dispatch. The COMMAND buffer stays sized by the full staging: a survivor's region is its
        // own batch's, which is the full staging's, whichever source the thread came out of.
        bool dyn = dynSrc && DynamicReady;
        int srcN = dyn ? _dynDrawN : _drawN;
        if (dyn) DynDispatches++;
        if (_viewBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _viewBuf = b; _viewCap = 0; }
        if (viewN > _viewCap)
        { Gl.NamedBufferData(_viewBuf, (IntPtr)((long)viewN * ViewFloats * sizeof(float)), null, Gl.DYNAMIC_DRAW); _viewCap = viewN; }
        Gl.NamedBufferSubData(_viewBuf, IntPtr.Zero, (IntPtr)((long)viewN * ViewFloats * sizeof(float)), views);

        _lyAt = (_lyAt + 1) % Rotation;
        long cmds = (long)_drawN * viewN;
        bool growCmd = cmds > _lyCmdCap, growCnt = _groups > _lyCntCap;
        for (int i = 0; i < Rotation; i++)
        {
            Gl.EnsureStorage(ref _lyCmd[i], cmds * CmdBytes, growCmd);
            Gl.EnsureStorage(ref _lyCnt[i], (long)(_groups + 1) * sizeof(uint), growCnt);
        }
        if (growCmd) _lyCmdCap = cmds;
        if (growCnt) _lyCntCap = _groups;
        Gl.ZeroBuffer(_lyCnt[_lyAt], (long)(_groups + 1) * sizeof(uint));

        Gl.UseProgram(_layerProg);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindSrc, dyn ? _dynSrcBuf : _srcBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, CullPass.BindSphere, CullPass.SphereBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _lyCmd[_lyAt]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCount, _lyCnt[_lyAt]);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindView, _viewBuf);
        Gl.Uniform1i(_luDrawN, srcN);
        Gl.Uniform1i(_luViewN, viewN);
        Gl.Uniform1i(_luCull, Mode != 0 ? 1 : 0);
        Gl.Uniform1i(_luRx, Receiver != 0 ? 1 : 0);
        Gl.Uniform1i(_luGroups, _groups);
        fixed (float* pl = Planes) Gl.Uniform4fv(_luPlanes, 6, pl);
        Gl.DispatchCompute((uint)((srcN + GroupSize - 1) / GroupSize), (uint)viewN, 1);
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.COMMAND_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _lyCmd[_lyAt]);
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, _lyCnt[_lyAt]);
        LayerDispatches++; LayerViews += viewN;
        int rxViews = 0;
        for (int i = 0; i < viewN; i++) if (views[i * ViewFloats + 24] != 0f) rxViews++;
        _dispatched = true; _sampleBuf = _lyCnt[_lyAt]; _sampleViews = viewN; _sampleRxViews = rxViews;
        _sampleDrawN = srcN; _sampleDyn = dyn;
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

    /// <summary>The two culls both dispatches share, <c>@SLOPE@</c> unexpanded: the receiver cull
    /// and the point-light face cull. Spliced, then the slope is interpolated over the whole text.</summary>
    private static string ReceiverCullLib => ShaderSource.Get("cx_recvcull.glsl");

    /// <summary>The cull. One thread per staged draw; a survivor appends into its batch's own output region
    /// with an <c>atomicAdd</c>, so every batch keeps its boundaries and only its interior order moves. Both
    /// view tests are the CPU reference's, term for term, and the cone test is conservative BY CONSTRUCTION:
    /// admitting a caster a face cannot see costs a depth draw, rejecting one it can see deletes a shadow.</summary>
    private static readonly string GatherCs = ("""
        #version 460 core
        layout(local_size_x = 64) in;   // GroupSize

        layout(std430, binding = 13) readonly buffer CxCullSphere { vec4 uSph[]; };
        layout(std430, binding = 17) readonly buffer CxGatherSrc  { uint uSrc[]; };
        layout(std430, binding = 18) writeonly buffer CxGatherCmd { uint uCmd[]; };
        layout(std430, binding = 19) buffer CxGatherCount         { uint uCnt[]; };
        uniform int uDrawN;
        uniform int uPoint;
        uniform int uCull;
        uniform int uSet;
        uniform vec3 uFocus;
        uniform vec3 uDir;
        uniform float uReach;
        uniform float uAlong;
        uniform int uRx;
        uniform int uGroups;

        """ + ReceiverCullLib + """
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
                bool offAxis, offEnds, rx = uRx != 0;
                vec3 p1 = s.xyz; float r0 = r, r1 = r;
                if (uPoint != 0)
                {
                    float dSq = dot(d, d);
                    offAxis = dSq > (uReach + r) * (uReach + r);
                    offEnds = CxFaceCulls(dSq, t, r);
                    // the tangent cone from the light, out to its reach; a light inside the caster keeps it
                    float dist = sqrt(dSq);
                    if (dist <= r) rx = false;
                    else { float k = 1.0 / (dist - r); r0 = r * dist * k; r1 = r * uReach * k; p1 = uFocus + d * (uReach / dist); }
                }
                else
                {
                    vec3 perp = d - t * uDir;
                    offAxis = dot(perp, perp) > (uReach + r) * (uReach + r);
                    offEnds = abs(t) > uAlong + r;
                    p1 = s.xyz + uDir * max(0.0, uAlong - t);
                }
                if (offAxis || offEnds) return;
                if (rx && CxShadowMisses(s.xyz, r0, p1, r1)) { atomicAdd(uCnt[uGroups], 1u); return; }
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

    /// <summary>The layered cull -- <see cref="GatherCs"/>'s tests, term for term, with the view's parameters
    /// read out of the view table instead of uniforms and one thread per (draw, view) pair. A survivor's
    /// command region is its batch's staged region scaled by the chunk's view count, and its
    /// <c>baseInstance</c> packs the view-table index above the ownership slot, for routing by <c>gl_Layer</c>.</summary>
    private static readonly string LayerCs = ("""
        #version 460 core
        layout(local_size_x = 64, local_size_y = 1) in;   // GroupSize

        layout(std430, binding = 13) readonly buffer CxCullSphere { vec4 uSph[]; };
        layout(std430, binding = 17) readonly buffer CxGatherSrc  { uint uSrc[]; };
        layout(std430, binding = 18) writeonly buffer CxGatherCmd { uint uCmd[]; };
        layout(std430, binding = 19) buffer CxGatherCount         { uint uCnt[]; };

        """ + ViewTblDecl + """
        uniform int uDrawN;
        uniform int uViewN;
        uniform int uCull;
        uniform int uRx;
        uniform int uGroups;

        """ + ReceiverCullLib + """
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
                bool offAxis, offEnds, rx = uRx != 0 && vc.x != 0.0;
                vec3 p1 = s.xyz; float r0 = r, r1 = r;
                if (vc.y != 0.0)
                {
                    float dSq = dot(d, d);
                    offAxis = dSq > (reach + r) * (reach + r);
                    offEnds = CxFaceCulls(dSq, t, r);
                    float dist = sqrt(dSq);
                    if (dist <= r) rx = false;
                    else { float k = 1.0 / (dist - r); r0 = r * dist * k; r1 = r * reach * k; p1 = uViewTbl[vi].a.xyz + d * (reach / dist); }
                }
                else
                {
                    vec3 perp = d - t * dir;
                    offAxis = dot(perp, perp) > (reach + r) * (reach + r);
                    offEnds = abs(t) > uViewTbl[vi].b.w + r;
                    p1 = s.xyz + dir * max(0.0, uViewTbl[vi].b.w - t);
                }
                if (offAxis || offEnds) return;
                if (rx && CxShadowMisses(s.xyz, r0, p1, r1)) { atomicAdd(uCnt[uGroups], 1u); return; }
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
           && LayerCs.Contains($"binding = {CullPass.BindSphere}") && LayerCs.Contains("CxCullSphere")
           && LayerCs.Contains("if (uCull != 0 && s.w < 1e29)")
           && LayerCs.Contains("uint b = i * 7u;")
           && LayerCs.Contains("if (set == 1 && uSrc[b + 6u] != 0u) return;")
           && LayerCs.Contains("if (set == 2 && uSrc[b + 6u] == 0u) return;")
           // the pack: the shift is ViewShift on both sides, the batch region scales by the view count, and
           // the slot rides the low bits the depth stage masks back out
           && LayerCs.Contains($"uCmd[q + 4u] = uint(slot) | (vi << {ViewShift}u);")
           && LayerCs.Contains("uint o = uSrc[b + 5u] * uint(uViewN) + atomicAdd(uCnt[uSrc[b + 4u]], 1u);")
           && LayerCs.Contains($"binding = {BindView}")
           // #488: the receiver cull is per-VIEW flagged (c.x), inside the uCull gate, and conservative in
           // the two-spheres form; a rejection tallies past the batch counts, never into one
           && LayerCs.Contains("bool offAxis, offEnds, rx = uRx != 0 && vc.x != 0.0;")
           && LayerCs.Contains("if (dot(pl.xyz, p0) + pl.w < -r0 && dot(pl.xyz, p1) + pl.w < -r1) return true;")
           && LayerCs.Contains("if (rx && CxShadowMisses(s.xyz, r0, p1, r1)) { atomicAdd(uCnt[uGroups], 1u); return; }")
           && LayerCs.Contains("if (dist <= r) rx = false;")
           && LayerCs.Contains("p1 = s.xyz + dir * max(0.0, uViewTbl[vi].b.w - t);")
           && LayerCs.Contains($"binding = {BindSrc}") && LayerCs.Contains($"binding = {BindCmd}")
           && LayerCs.Contains($"binding = {BindCount}");

    /// <summary>What <c>CasterPass.SelfCheck</c> pins about the dispatch: the cull it runs is the CPU
    /// reference's, over the SHARED sphere buffer, with the shared exemption, conservative in the direction
    /// that costs a draw. Text rather than arithmetic because GLSL cannot be run headless, but every term
    /// that would silently delete a shadow if it drifted is named here.</summary>
    internal static bool SourceSafe()
        => GatherCs.Contains("if (t < -r) return true;")
           && GatherCs.Contains($"float lim = t * {Slope} + 2.0 * r;")
           && GatherCs.Contains("return lim < 0.0 || dSq - t * t > lim * lim;")
           && GatherCs.Contains("offEnds = abs(t) > uAlong + r;")
           && GatherCs.Contains("offAxis = dot(perp, perp) > (uReach + r) * (uReach + r);")
           // #303: the shared bounds, at the colour cull's own binding, with its own exemption value.
           && GatherCs.Contains($"binding = {CullPass.BindSphere}") && GatherCs.Contains("CxCullSphere")
           && GatherCs.Contains("if (uCull != 0 && s.w < 1e29)")
           && GatherCs.Contains("uCmd[q + 4u] = uint(slot);")
           // #488: same receiver cull, flagged per dispatch
           && GatherCs.Contains("bool offAxis, offEnds, rx = uRx != 0;")
           && GatherCs.Contains("if (dot(pl.xyz, p0) + pl.w < -r0 && dot(pl.xyz, p1) + pl.w < -r1) return true;")
           && GatherCs.Contains("if (rx && CxShadowMisses(s.xyz, r0, p1, r1)) { atomicAdd(uCnt[uGroups], 1u); return; }")
           && GatherCs.Contains("if (dist <= r) rx = false;")
           && GatherCs.Contains("p1 = s.xyz + uDir * max(0.0, uAlong - t);")
           // #305: the set filter reads the staged dynamic flag at the 7-uint stride, in BOTH directions --
           // a filter that lost either arm bakes the same caster into both arrays, and min() then hides it.
           && GatherCs.Contains("uint b = i * 7u;") && SrcUints == 7
           && GatherCs.Contains("if (uSet == 1 && uSrc[b + 6u] != 0u) return;")
           && GatherCs.Contains("if (uSet == 2 && uSrc[b + 6u] == 0u) return;")
           && GatherCs.Contains($"binding = {BindSrc}") && GatherCs.Contains($"binding = {BindCmd}")
           && GatherCs.Contains($"binding = {BindCount}");
}
