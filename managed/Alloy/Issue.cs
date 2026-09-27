using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

using static CX.Alloy.AlloyRenderer;

namespace CX.Alloy;

/// <summary>#173: BATCH ISSUE. Uploading the staged per-draw records and bone snapshots, then submitting the built groups as one multidraw per (program, VDO, texture, primitive) cut -- and the blended and water variants that ride the same records under different GL state. Hot: nothing on this path allocates.
/// <para>An ADR 0014 module, not a partial (CX-699): it reaches the renderer's tables and state through
/// <c>using static</c>, a compile-time alias that emits the same static load the same-class read did.</para></summary>
internal static unsafe class Issue
{
    /// <summary>#153: forget every cached handle. Called at each enrollment -- the cadence at which the
    /// tids themselves are re-harvested -- and on release. Handles stay resident until their textures die;
    /// the cache only ever answers for tids the current enrollment produced.</summary>
    /// <summary>#220: forget every cached handle. Only meaningful while the cache holds ENGINE names --
    /// those get recycled behind us and the wipe is the only heal. With <c>ownedtex</c> armed the names are
    /// Alloy's own, invalidated precisely by <c>OwnedTextures.Drain</c> -&gt; <see cref="DropHandle"/>, and
    /// wiping here would only make the next mint re-resident an already-resident handle (GL_INVALID_
    /// OPERATION -- the error burst #220 recorded on transitions).</summary>
    /// <summary>#220: the 4.6 context is gone, so every handle went with it. Cleared unconditionally, and
    /// nothing is made non-resident -- that would be a call into a dead context.</summary>
    internal static void ForgetHandles()
    {
        InvalidateRecords();   // #242: the persistent records hold the handles this forgets
        Array.Clear(_hTid, 0, _hTid.Length);
        Array.Clear(_hVal, 0, _hVal.Length);
        Array.Clear(_hRes, 0, _hRes.Length);
    }

    internal static void WipeHandles()
    {
        if (OwnedTextures.Armed) return;
        InvalidateRecords();   // #242: same reason as ForgetHandles -- a record outlives the wipe otherwise
        Array.Clear(_hTid, 0, _hTid.Length);
        Array.Clear(_hRes, 0, _hRes.Length);
    }

    /// <summary>The sorted dirty list as runs of consecutive slots, one <c>BufferSubData</c> per run. Per
    /// slot was one call each, and radeonsi stages, copies and barriers EVERY call into a buffer the GPU is
    /// still reading (<c>si_buffer_subdata</c> forces DISCARD_RANGE), so the call count is the cost there.
    /// Duplicates collapse. Caller sorts once; both record buffers walk the same list.</summary>
    private static void UploadRuns(uint buf, void* data, int recBytes)
    {
        for (int d = 0; d < _recDirtyN;)
        {
            d = NextRun(_recDirty, _recDirtyN, d, out int first, out int last);
            long off = (long)first * recBytes;
            Gl.NamedBufferSubData(buf, (IntPtr)off,
                             (IntPtr)((long)(last - first + 1) * recBytes), (byte*)data + off);
        }
    }

    /// <summary>The run of consecutive (or repeated) slots starting at sorted <c>_recDirty[d]</c>; returns
    /// the index past it.</summary>
    private static int NextRun(int[] dirty, int n, int d, out int first, out int last)
    {
        first = dirty[d]; last = first;
        while (++d < n && dirty[d] <= last + 1) last = dirty[d];
        return d;
    }

    /// <summary>A wrong run uploads a stale record over a live slot, silently, so this joins the disarm
    /// chain: duplicates collapse, adjacent slots merge, a gap splits.</summary>
    internal static bool RunsSelfCheck(ExtenderLog log)
    {
        int[] dirty = { 5, 3, 4, 9, 3, 11, 10 };
        Array.Sort(dirty);
        int d = NextRun(dirty, dirty.Length, 0, out int f0, out int l0);
        d = NextRun(dirty, dirty.Length, d, out int f1, out int l1);
        bool ok = f0 == 3 && l0 == 5 && f1 == 9 && l1 == 11 && d == dirty.Length;
        if (!ok) log.Info("AlloyRenderer.RunsSelfCheck: FAILED -- the dirty-slot runs do not merge as sorted");
        return ok;
    }

    /// <summary>#242: push the records that MOVED into the persistent per-draw SSBO, then the small
    /// draw-to-slot index array this pass's order needs. The records are indexed by ownership slot and
    /// survive the frame, so what goes up is the mover set -- a walking creature, an animated texture, a
    /// fading tile -- rather than the whole owned table.
    /// <para>Past a quarter of the table, naming slots costs more than sending everything, so the frame is
    /// promoted to one wholesale upload. That is also what a reallocation and a dirty-list overflow take.
    /// </para>
    /// <para>No orphan any more, and that is the trade this makes: a bare <c>BufferSubData</c> over a buffer
    /// the GPU may still be reading can make the driver sync, which is the stall #84's orphan was avoiding.
    /// Orphaning is not available to a persistent buffer -- the whole point is that last frame's contents are
    /// still there, which is also why the records are not on <see cref="StreamRing"/> (CX-45).</para>
    /// <para>False = the slot array did not stream and the colour frame sits out.</para></summary>
    internal static bool UploadDraws()
    {
        int cap = _recOwnCap + MaxSkyDraws;
        bool grew = false;
        if (_drawBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _drawBuf = b; }
        if (cap > _recCapGpu)
        {
            Gl.NamedBufferData(_drawBuf,
                          (IntPtr)((long)cap * DrawFloats * sizeof(float)), null, Gl.DYNAMIC_DRAW);
            grew = true;
        }
        // Against what this frame DRAWS, not against the high-water capacity: _recOwnCap latches at the
        // largest area of the session and never shrinks, so thresholding on it would leave a small interior
        // issuing thousands of scattered SubData calls into a buffer that can no longer orphan -- one
        // enrollment's worth of hitch, once a cadence, in exactly the "just left the city" case.
        bool whole = grew || _recDirtyAll || _recDirtyN > _drawN / 4;
        if (!whole) Array.Sort(_recDirty, 0, _recDirtyN);
        fixed (float* p = _dData)
        {
            if (whole)
                Gl.NamedBufferSubData(_drawBuf, IntPtr.Zero,
                                      (IntPtr)((long)cap * DrawFloats * sizeof(float)), p);
            else UploadRuns(_drawBuf, p, DrawFloats * sizeof(float));
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindDraw, _drawBuf);
        if (_bindless) UploadHandles(cap, whole);
        if (grew) _recCapGpu = cap;
        _recDirtyN = 0; _recDirtyAll = false;
        // ...and the one array that IS per draw and per pass, rewritten whole every pass: the ring's shape.
        fixed (int* p = _dSlot)
            return StreamRing.Stream(StreamRing.Site.DrawSlots, BindDrawIdx, p, (long)_drawN * sizeof(int), out _slotRange);
    }

    /// <summary>CX-45: the staged draw-to-slot array's ring slice, for <see cref="BindStagedSlots"/>.</summary>
    internal static StreamRing.Range _slotRange;

    /// <summary>The handle table alone. Its capacity is tracked apart from the draw records' so the bake can
    /// bring it up to date on a frame the colour bracket has not reached (or refused).</summary>
    internal static void UploadHandles(int cap, bool whole)
    {
        if (_texHBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _texHBuf = b; }
        if (cap > _texHCapGpu)
        {
            Gl.NamedBufferData(_texHBuf, (IntPtr)((long)cap * 8 * sizeof(ulong)), null, Gl.DYNAMIC_DRAW);
            _texHCapGpu = cap; whole = true;
        }
        fixed (ulong* p = _dTexH)
        {
            if (whole)
                Gl.NamedBufferSubData(_texHBuf, IntPtr.Zero, (IntPtr)((long)cap * 8 * sizeof(ulong)), p);
            else
                UploadRuns(_texHBuf, p, 8 * sizeof(ulong));
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindTexH, _texHBuf);
        _texHDirty = false;
    }
    private static int _texHCapGpu;

    /// <summary>Called by the caster bake, inside its bracket, before its first view: the standing staging
    /// samples the GPU handle table by record slot, so a handle dropped since the last colour upload must
    /// not still be there. Leaves the dirty list for the colour bracket, which uploads the records too.</summary>
    internal static void UploadHandlesForBake()
    {
        if (!_bindless || !_texHDirty) return;
        int cap = _recOwnCap + MaxSkyDraws;
        bool whole = _recDirtyAll || _recDirtyN > _drawN / 4 || cap > _texHCapGpu;
        if (!whole) Array.Sort(_recDirty, 0, Math.Min(_recDirtyN, _recDirty.Length));
        UploadHandles(cap, whole);
        BakeHandleUploads++;
    }

    /// <summary>#243: put the CPU-staged per-draw index array back on <see cref="BindDrawIdx"/>. The cull
    /// binds its own compacted one for the passes it drives; every other pass reads the staged order.</summary>
    internal static bool BindStagedSlots() => StreamRing.Rebind(StreamRing.Site.DrawSlots, BindDrawIdx, in _slotRange);

    /// <summary>#137: push this pass's bone snapshots into the bone block. Orphan-then-fill for
    /// <see cref="UploadDraws"/>'s reason. Nothing to upload is the common case (no skinmesh on screen), and
    /// the block stays bound empty rather than unbound -- a stage that declares it must find something there
    /// even on a frame where no draw reads it.
    /// <para>Not on <see cref="StreamRing"/>: the next frame's caster bake reads it when the shadow bracket did not pose (#308).</para></summary>
    internal static void UploadBones()
    {
        if (_skinning) UploadBoneBlock(ref _boneBuf, _boneStage, BindBones);
    }

    /// <summary>One bone block, <c>_boneSlots</c> palettes from <paramref name="stage"/>, onto
    /// <paramref name="binding"/>.</summary>
    internal static void UploadBoneBlock(ref uint buf, float* stage, uint binding)
    {
        if (buf == 0) { uint b; Gl.CreateBuffers(1, &b); buf = b; }
        var size = (IntPtr)((long)Math.Max(_boneSlots, 1) * BoneFloats * sizeof(float));
        Gl.NamedBufferData(buf, size, null, Gl.DYNAMIC_DRAW);
        if (_boneSlots > 0)
            Gl.NamedBufferSubData(buf, IntPtr.Zero,
                                  (IntPtr)((long)_boneSlots * BoneFloats * sizeof(float)), stage);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, binding, buf);
    }

    /// <summary>Phase B: one bind set + one glMultiDrawElementsBaseVertex per batch. uDrawBase is re-sent for
    /// every batch (and it is a DIFFERENT location per program, so the program switch cannot leave a stale
    /// one behind) -- the shaders read record uDrawBase + gl_DrawID, which is what carries the per-part model
    /// matrix, texture rotation and material colours that used to be per-part uniforms.</summary>
    /// <summary>#290: one colour-bracket range, from the cull's compacted list where the cull ran. Bound
    /// around the range rather than for the whole bracket -- the sky, grass, beam and particle passes bind
    /// buffers of their own in between, and finding an indirect buffer still bound under one of them is a
    /// class of bug that shows up as a wrong batch rather than as an error.</summary>
    /// <summary>The blended pass's per-creature filter: when set, only batches tagged with this run gob
    /// issue; when zero, only UNTAGGED ones. Zero-valued and unset (<see cref="_onlyGobSet"/> false) issues
    /// everything, which is every other pass.</summary>
    internal static IntPtr _onlyGob;
    internal static bool _onlyGobSet;

    /// <summary>CX-10: the program the last issue loop left bound (-1 none). <see cref="_holdProg"/> lets the
    /// next loop start from it instead of re-sending the rig; set only when nothing binds between the two.</summary>
    internal static int _heldProg = -1;
    internal static bool _holdProg;

    internal static void IssueColour(int from, int to, Matrix4x4* vp, int* viewport, float near, float far)
    {
        bool indirect = CullPass.Active;
        if (indirect) CullPass.BindForDraw(CullPass.PhaseColour);
        IssueBatches(from, to, (float*)vp, viewport, near, far, indirect);
        if (indirect) { CullPass.UnbindForDraw(); BindStagedSlots(); }
    }

    internal static void IssueBatches(int from, int groups, float* vpRaw, int* viewport, float near, float far,
                                     bool indirect = false)
    {
        long tIssue = FrameZones.Now;
        try { IssueBatchesInner(from, groups, vpRaw, viewport, near, far, indirect); }
        finally { FrameZones.Add(FrameZones.Zone.Issue, tIssue); }
    }

    /// <summary>Did batch <paramref name="g"/> write the depth <see cref="SsaoPass"/> read, and if not, WHICH
    /// of the prepass's three exclusions caught it? The prepass walks the opaque range and skips the cutout
    /// class and every out-of-prepass feature, so those three are the same three here -- a batch outside them
    /// must not consume the visibility buffer at all. The reason travels to the lit stage as the sign of
    /// <c>uSsaoOn</c> (which tests <c>&gt; 0</c>) so <c>/alloy viz 35</c> can name it on the pixel, rather than
    /// leaving "the door has no AO" and "the door CANNOT have AO" the same picture.</summary>
    internal const int AoIn = 1, AoCutout = -1, AoOutOfPrepass = -2, AoBlended = -3, AoKeyhole = -4;

    /// <summary>Membership is MEASURED (<see cref="_gPrepassed"/>, written by the prepass as it walks); only
    /// the reason a batch is out is inferred, and only to colour <c>/alloy viz 35</c>. Predicting membership
    /// from the class was the bug this replaces.</summary>
    private static int AoWhy(int g)
        => _gPrepassed[g] ? AoIn
         : g >= _gOpaqueEnd ? AoBlended
         : _gCls[g] == (byte)AlloyClass.Cutout ? AoCutout
         : ShaderFeatures.OutOfPrepass(_gFeat[g]) ? AoOutOfPrepass
         : _gDis[g] ? AoKeyhole
         : AoOutOfPrepass;

    internal static void IssueBatchesInner(int from, int groups, float* vpRaw, int* viewport, float near, float far,
                                          bool indirect)
    {
        // A probe capture must not carry the debug viz through: /alloy viz would bake flat cyan (or a
        // cluster heat map) into the cube and leave it there for every frame that samples it afterwards.
        int viz = _capturing ? 0 : _lightViz;
        // Which program is bound, -1 for neither yet. In the opaque range this switches once (lit batches
        // sort last inside a class -- #292 put the face-culling bit BELOW the lit one precisely so that
        // stays true); in the blended range it can alternate, because there the order is the camera's and
        // nothing else. ponytail: a lit rebind still re-sends the feature, fog and lit-only terms (the pass
        // block covers the receiver and light curve, CX-43), so an alternating blended set pays for them per
        // switch. The set is the small remainder by construction; if it ever is not, move those terms into
        // CxPass or add a second sort digit inside the distance order.
        // #129: three programs now, not two -- fullbright (0), clustered lit (1) and water (2). Keyed on the
        // batch's CLASS for water and on _gLit for the other two, because water is a class the whole way
        // down: its stage samples the framebuffer copy, and no lit batch ever should.
        int boundProg = _holdProg ? _heldProg : -1;
        _holdProg = false;
        uint boundVao = 0; int boundTex = -1;
        uint boundSrc = 0, boundDst = 0;
        // GL_BLEND as this pass found it: DrawOwned's preamble disables it, and TransparentPass enables it
        // before its own range, so the first batch of either kind agrees with the state already set.
        bool boundBlend = _blendOn;
        byte boundMask = _passDepthMask;
        bool boundEqual = false;
        bool boundTest = true;
        bool boundOffset = false;
        float boundOitBias = -1f;   // per program: reset on bind, so a wash batch's bias never leaks across
        int boundOitGob = -1;
        // #292: face culling, per batch. -1 rather than a guess at the preamble's state: the sky, grass,
        // particle and beam passes all leave their own culling behind them, and a probe capture re-enters
        // this loop six more times, so the first batch of every entry issues the call rather than trusting
        // what someone else set.
        int boundCull = -1;
        float boundCut = float.NaN;   // per PROGRAM: reset on every UseProgram below
        int litBase = -1, litCut = -1, litOitBias = -1, litOitGob = -1; // the bound lit program's, whichever of the two it is
        int litSsaoOn = -1, litAoDirect = -1;
        if (boundProg is 1 or 3 or 4)
        {
            ref LitLoc H = ref (boundProg == 3 ? ref Programs._litH : ref (boundProg == 4 ? ref Programs._litL : ref Programs._lit));
            litBase = H.Base; litCut = H.Cut; litOitBias = H.OitBias; litOitGob = H.OitGob;
            litSsaoOn = H.SsaoOn; litAoDirect = H.AoDirect;
        }
        // #379 follow-up: SSAO is per BATCH, not per program bind. The visibility buffer is computed from
        // the DEPTH PREPASS, and a batch the prepass skipped (cutout, an out-of-prepass feature, the whole
        // blended range) has no depth of its own in it -- its fragments would read the occlusion of whatever
        // stands BEHIND them. Live: the far corridor's occlusion showing through a closed door.
        int boundSsao = -1;
        int* boundSlot = stackalloc int[MapSlots];
        for (int s = 0; s < MapSlots; s++) boundSlot[s] = -1;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        for (int g = from; g < groups; g++)
        {
            if (_onlyGobSet && _gGob[g] != _onlyGob) continue;
            bool water = _gCls[g] == (byte)AlloyClass.Water;
            bool lit = _gLit[g];
            // #350: a hosted feature draws through the second lit program (3); everything else lit through
            // the first (1). Batches are cut on the feature id, so this is one switch per hosted batch.
            bool hosted = lit && !water && Programs._progLitHost != 0 && ShaderFeatures.Hosted(_gFeat[g]);
            // A hosted content program declares no revealage output: under OIT it would leave that buffer
            // undefined (NaN, which the resolve and the bloom then spread as white patches and blue blotches).
            // Those batches sit the accumulation out and draw after the resolve the plain-blend way.
            // ...and so does any pair that is not SrcAlpha/OneMinusSrcAlpha: weighted blending can represent
            // only that one. SoW's veil (a modulating fog dome around the player) fed through it composites
            // the veil's colour over the whole view instead of darkening by it.
            bool oitOut = hosted || _gSrc[g] != AuroraSrcAlpha || _gDst[g] != AuroraOneMinusSrcAlpha;
            if (_oitOn && oitOut) continue;
            if (_onlyHosted && !oitOut) continue;
            bool lists = lit && !water && !hosted && _oitOn && OitPass.Linked;   // the append twin, bracket only
            int wantProg = water ? 2 : hosted ? 3 : lists ? 4 : lit ? 1 : 0;
            if (boundProg != wantProg)
            {
                boundProg = wantProg;
                boundCut = float.NaN;
                // CX-51: the probe's alpha is 1 / distance, and only the lit stage writes one; the rest keep the clear's "far"
                if (_capturing && !_planar) Gl.ColorMask(1, 1, 1, (byte)(wantProg == 1 ? 1 : 0));
                if (water)
                {
                    Gl.UseProgram(Programs._progWater);
                    Gl.UniformMatrix4fv(Programs._watVp, 1, 0, vpRaw);
                    Gl.UniformMatrix4fv(Programs._watView, 1, 0, _waterView);
                    Gl.UniformMatrix4fv(Programs._watProj, 1, 0, _waterProj);
                    Gl.Uniform2f(Programs._watNearFar, _waterNear, _waterFar);
                    AlloyLights.BindForDraw();
                    Gl.Uniform1i(Programs._watViz, viz);
                    Gl.Uniform1i(Programs._watEnvOk, FrameSnapshot.EnvOk);
                    Gl.Uniform1i(Programs._watCapture, _capturing ? 1 : 0);
                    Gl.Uniform1f(Programs._watSplash, Weather.SplashRate(_capturing));
                    UploadFeatureTerms(Programs._watFeat, viewport);
                    BindPass(Programs._watCubeCount, viewport, near, far);
                    // The cascade and cube binds each left their own unit current for a moment; the copy
                    // pair and the noise texture have to be back on their units before the first water draw
                    // samples them.
                    FrameSnapshot.Bind();
                }
                else if (lit)
                {
                    ref LitLoc L = ref (hosted ? ref Programs._litH : ref (lists ? ref Programs._litL : ref Programs._lit));
                    Gl.UseProgram(hosted ? Programs._progLitHost : lists ? Programs._progLitList : Programs._progLit46);
                    Gl.Uniform1i(L.Tex, 0);
                    Gl.UniformMatrix4fv(L.Vp, 1, 0, vpRaw);
                    AlloyLights.BindForDraw();
                    Gl.Uniform1i(L.Viz, viz);
                    Gl.Uniform1f(L.AoCutout, Ao.Cutout.V);
                    OitPass.Upload(L.Oit, L.OitDim, _oitOn);
                    Gl.Uniform1i(L.Capture, _capturing ? (_planar ? 2 : 1) : 0);
                    LightmapBake.BindForDraw(L.LmPages, L.LmMovers);
                    UploadFeatureTerms(L.Feat, viewport);
                    BindPass(L.CubeCount, viewport, near, far);
                    BindProbe();
                    SsaoPass.BindForDraw(SsaoPass.Live && !_capturing, PostChain.DepthTex);
                    Gl.Uniform1i(L.SsaoHalf, SsaoPass.Half ? 1 : 0);
                    Gl.Uniform2f(L.LightOcc, Lighting.HiddenLights.V ? 1f : 0f, GlossyRough);
                    UploadMaterialModel(L.Material);
                    Weather.UploadLit(L.Weather, L.WeatherLook, L.CoverVp, L.CoverLayer, L.SnowLook, L.SnowLight, L.SnowPom, _capturing);
                    Weather.BindEsm(L.CoverEsm, _oitOn || lists);
                    litBase = L.Base; litCut = L.Cut; litOitBias = L.OitBias; litOitGob = L.OitGob; boundOitBias = -1f; boundOitGob = -1;
                    litSsaoOn = L.SsaoOn; litAoDirect = L.AoDirect;
                    boundSsao = -1;
                }
                else
                {
                    Gl.UseProgram(Programs._prog46);
                    Gl.Uniform1i(Programs._locTex, 0);
                    float* white = stackalloc float[3] { 1f, 1f, 1f };
                    Gl.Uniform3fv(Programs._locTint, 1, white);    // ...and white, which is Mat()'s ordinary branch
                    Gl.Uniform1f(Programs._locSkyFog, 0f);
                    Gl.Uniform1i(Programs._locViz, viz);
                    OitPass.Upload(Programs._locOit, Programs._locOitDim, _oitOn); boundOitBias = -1f; boundOitGob = -1;
                    Gl.UniformMatrix4fv(Programs._locVp, 1, 0, vpRaw);
                    UploadFeatureTerms(Programs._locFeat, viewport);
                }
            }
            int s = _gStart[g];
            Gl.Uniform1i(water ? Programs._watBase : lit ? litBase : Programs._locBase, s);
            // #126: the alpha cutout, per batch because it is per class -- the engine's own
            // fAlphaDiscardValue, which is a global there and a uniform here.
            if (lit)
            {
                int ssao = SsaoPass.Live && !_capturing ? AoWhy(g) : 0;
                if (ssao != boundSsao)
                {
                    boundSsao = ssao;
                    Gl.Uniform1i(litSsaoOn, ssao);
                    Gl.Uniform1f(litAoDirect, ssao > 0 ? SsaoPass.Direct : 0f);
                }
            }
            float cut = AlloyClasses.Discard(_gCls[g]);
            if (cut != boundCut) { Gl.Uniform1f(water ? Programs._watCut : lit ? litCut : Programs._locCut, cut); boundCut = cut; }
            // ...and the blend state, per batch, because the opaque PASS now mixes both: a plain opaque batch
            // draws with GL_BLEND off and a cutout batch with it on and the material's own pair, which is
            // exactly the pair of states the engine draws buckets 0 and 1 in. Batches are class-grouped, so
            // this toggles about once per pass.
            bool blend = AlloyClasses.Blends(_gCls[g]);
            if (blend != boundBlend)
            {
                boundBlend = blend;
                if (blend) Gl.Enable(Gl.BLEND); else Gl.Disable(Gl.BLEND);
            }
            // Under OIT the pair is per draw buffer and set once by OitPass.Begin; glBlendFunc would clobber both.
            if (blend && !_oitOn)
            {
                uint src = BlendGl(_gSrc[g]), dst = BlendGl(_gDst[g]);
                if (src != boundSrc || dst != boundDst) { Gl.BlendFunc(src, dst); boundSrc = src; boundDst = dst; }
            }
            bool wash = blend && _gWash[g] != 0;
            bool equal = _matchDepth && _gPrepassed[g];
            // A wash takes the pass's own depth rule, like any other blended draw: it is a cutout Alloy chose
            // to BLEND for its fringe, and vanilla writes that part's depth in bucket 1. Holding the mask off
            // here left it with no depth anywhere -- the blended class is never prepassed -- and water and
            // particles painted over the creature heads the reroute catches.
            byte mask = equal ? (byte)0 : _passDepthMask;
            if (mask != boundMask) { Gl.DepthMask(mask); boundMask = mask; }
            if (equal != boundEqual) { Gl.DepthFunc(equal ? Gl.EQUAL : Gl.LEQUAL); boundEqual = equal; }
            bool test = !(wash && !_washTest);
            if (test != boundTest) { if (test) Gl.Enable(Gl.DEPTH_TEST); else Gl.Disable(Gl.DEPTH_TEST); boundTest = test; }
            // A wash is routinely a decal lying ON an opaque surface (a floor lattice at +0.005), and its
            // depth comes from a different program than the prepass wrote the floor's with: LEQUAL alone
            // z-fights at range. Pull it toward the eye by the same offset the highlight mask uses.
            if (wash != boundOffset)
            {
                if (wash && Gl.PolygonOffset != null) { Gl.PolygonOffset(WashOffsetFactor, WashOffsetUnits); Gl.Enable(Gl.POLYGON_OFFSET_FILL); }
                else Gl.Disable(Gl.POLYGON_OFFSET_FILL);
                boundOffset = wash;
            }
            if (_oitOn)
            {
                float bias = wash ? OitPass.WashBias : 0f;
                if (bias != boundOitBias) { Gl.Uniform1f(lit ? litOitBias : Programs._locOitBias, bias); boundOitBias = bias; }
                int gid = OitPass.GobId(_gGob[g]);
                if (gid != boundOitGob) { Gl.Uniform1i(lit ? litOitGob : Programs._locOitGob, gid); boundOitGob = gid; }
            }
            BindBatchState(g, ref boundCull, ref boundVao);
            // #153: a merged lit batch spans many textures; its samplers come off the handle block per draw,
            // and binding here would be dead state. Unlit and water batches still bind.
            bool handles = _bindless && lit && !water;
            if (!handles && _gTex[g] != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)_gTex[g]); boundTex = _gTex[g]; }
            // #120: slots 1..5 on units 3..7. Unbound slots get texture 0 rather than being left holding a
            // previous batch's map -- the shader's slot mask says not to sample them, but a stale binding is
            // one override away from being sampled anyway.
            for (int t = 0; !handles && t < MapSlots; t++)
            {
                int want = _gTexN[g * MapSlots + t];
                if (want == boundSlot[t]) continue;
                boundSlot[t] = want;
                // #151: one DSA call instead of ActiveTexture+Bind+ActiveTexture -- this loop is a third of
                // the pass's GL traffic.
                Gl.BindTextureUnit(PbrUnit0 + (uint)t, (uint)want);
            }
            // #243: the same batch, from a list the GPU compacted. The bind set above is unchanged (the
            // cull moves WHICH draws are in the batch, never what state they draw under), and the record
            // lookup is unchanged too -- uDrawBase is still this batch's start, because the cull compacts
            // each batch into its own source region rather than into one global run.
            uint prim = _gMode[g];
            if (indirect) CullPass.Draw(prim, g, s, _gLen[g]);
            else
                Gl.MultiDrawElementsBaseVertex(prim, counts + s, GlUnsignedShort,
                                               (void**)(offs + s), _gLen[g], bases + s);
            Counters._batches++;
            Counters._draws += _gLen[g];
            Counters._drawsByClass[_gCls[g]] += _gLen[g];
        }
        // Hand the per-batch toggling back to the pass level: the next pass reads this rather than assuming
        // what the last batch left bound.
        _blendOn = boundBlend;
        _heldProg = boundProg;
        if (boundMask != _passDepthMask) Gl.DepthMask(_passDepthMask);
        if (boundEqual) Gl.DepthFunc(Gl.LEQUAL);
        if (!boundTest) Gl.Enable(Gl.DEPTH_TEST);
        if (boundOffset) Gl.Disable(Gl.POLYGON_OFFSET_FILL);
    }

    /// <summary>Polygon offset for wash batches (see the wash note in the issue loop).</summary>
    private const float WashOffsetFactor = -1f, WashOffsetUnits = -2f;

    /// <summary><c>/alloy washtest 0</c>: wash batches draw with the depth TEST off too. A/B instrument, not a look.</summary>
    private static bool _washTest => Renderer.WashTest.V;

    /// <summary>The depth mask the current pass set before <see cref="IssueColour"/>; a wash batch drops it
    /// to 0 for its own draws and the loop restores it. 1 everywhere but the transparent passes.</summary>
    private static byte _passDepthMask = 1;
    /// <summary>The opaque and cutout passes only: a batch the prepass laid depth for draws at <c>GL_EQUAL</c>
    /// with the mask off. Its keyhole and alpha discards already shaped that depth, so every hidden or holed
    /// fragment fails the test before shading, and with no depth write the driver keeps early-Z despite the
    /// stage's <c>discard</c>. Callers set it around the pass and must restore false.</summary>
    internal static bool _matchDepth;
    /// <summary>The blended range is being drawn into OitPass's accumulation pair: programs write the pair,
    /// and the per-batch blend pair stays off the state.</summary>
    internal static bool _oitOn;
    /// <summary>The post-resolve sweep for the batches OIT skipped: hosted programs only.</summary>
    internal static bool _onlyHosted;
    /// <summary>Aurora blend-mode indices (see <see cref="BlendGl"/>) of the one pair OIT accumulates.</summary>
    private const byte AuroraSrcAlpha = 0, AuroraOneMinusSrcAlpha = 1;

    /// <summary>The blending classes (ADR 0011 steps 4 and 7, #126). Blended draws sorted back-to-front with
    /// the depth mask OFF -- what makes grass's punch-through structurally impossible, since nothing
    /// alpha-blended can write depth -- and additive follows it unsorted, because addition commutes and the
    /// engine leaves its own depth mask off there too (<c>Material::SetBlendingMode</c> @0x4c5c50 turns it
    /// off for exactly that one blend pair). Depth TESTING stays on, against the opaque set's depth. The
    /// per-batch <c>glBlendFunc</c> comes off the material, in <see cref="IssueBatches"/>.
    /// <para>The CUTOUT class draws back in the opaque pass: its graded texels blend, so they composite
    /// against whatever the framebuffer already holds, and depth-write over it. The upgrade that removes the
    /// ordering dependency is alpha-to-coverage (ADR 0011 names it beside the alpha test), which needs a
    /// multisampled target.</para></summary>
    /// <para><paramref name="depthWrite"/>: the engine's own rule, <c>Material::SetBlendingMode</c> @0x4c5c50 --
    /// depth writes ON for every blend pair but additive (src 0 / dst 2), OFF for additive. The blended class
    /// therefore writes depth: a translucent creature's back parts are rejected by its front ones instead of
    /// compositing N times, and the outline mask has a depth to match at <c>GL_EQUAL</c>.</para>
    internal static void TransparentPass(int from, int to, Matrix4x4* vp, int* viewport, float near, float far,
                                        bool depthWrite = false)
    {
        if (to <= from) return;
        _passDepthMask = depthWrite ? (byte)1 : (byte)0;
        Gl.DepthMask(_passDepthMask);
        Gl.Enable(Gl.BLEND); _blendOn = true;
        // #290: the same compacted list the opaque pass drew from, whose batches in THIS range were placed
        // by the sort dispatch -- back-to-front from this pass's eye, measured on the GPU.
        IssueColour(from, to, vp, viewport, near, far);
        _passDepthMask = 1;
    }

    /// <summary>ADR 0011 <see cref="PassId.WaterOwned"/> (#129): the water class, drawn from the one
    /// framebuffer copy taken immediately before it.
    /// <para>Depth writes go back ON, which is the one piece of state that separates this pass from the
    /// blended one and the reason it can be here at all: additive draws after water and has to be able to
    /// stand in FRONT of it. That is vanilla's arrangement -- effects are invisible to the reflection and are
    /// not painted over by the surface -- and the ticket keeps it deliberately.</para>
    /// <para>Stands down whole whenever the copy could not be taken (<see cref="FrameSnapshot.Ready"/> false) or
    /// the program did not build. Nothing partial: <see cref="BucketGate"/>'s bucket-6 gate reads the
    /// same flag, so water is simply NOT DRAWN on exactly the passes this one does not take (#285).</para></summary>
    internal static void WaterOwnedPass(int from, int to, Matrix4x4* vp, float* view, float* proj,
                                       int* viewport, float near, float far)
    {
        if (to <= from || !FrameSnapshot.Ready || !_waterArmed) return;
        _waterView = view; _waterProj = proj; _waterNear = near; _waterFar = far;
        Gl.DepthMask(1);
        FrameSnapshot.Bind();
        IssueColour(from, to, vp, viewport, near, far);
        FrameSnapshot.Unbind();
        // Leave the state the additive pass expects to find: it enables blending itself, but the depth mask
        // is pass-level state and additive must not write depth.
        Gl.DepthMask(0);
    }

    /// <summary>One <c>Aurora::AuroraBlendModes</c> value as its GL blend factor -- the engine's own
    /// translation table at 0x12f5040, which <c>GLRender::SetBlendFunc</c> @0x4cb0c0 indexes. Out-of-range
    /// values fall back to the pair the engine's own default is, rather than to whatever GL enum 0 means.</summary>
    internal static uint BlendGl(int aurora) => aurora switch
    {
        0 => GlSrcAlpha,
        1 => GlOneMinusSrcAlpha,
        2 => GlOne,
        3 => GlZero,
        4 => GlDstColor,
        5 => GlSrcColor,
        // #125 review: the table's entry 6 is 0x0305, ONE_MINUS_DST_ALPHA -- read out of
        // blendTranslationTable @0x12f5040 rather than inferred from the name beside it.
        6 => GlOneMinusDstAlpha,
        _ => GlSrcAlpha,
    };
}
