using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using Sowx.Services.Perf;

namespace Sowx.Alloy;

/// <summary>#215 (ADR 0016 step 6): the CUTOVER. On the Alloy path, in a world frame, the engine's
/// <c>Scene::RenderSinglePass</c> is not called at all -- this runs in its place, calls RSP's per-frame
/// BOOKKEEPING and issues no engine draw.
/// <para><b>Why a shim and not a skip.</b> RSP braids drawing with seven pieces of bookkeeping, four with no
/// other caller in the binary (ADR 0016, "RenderSinglePass is not draw-only"). Skipping it outright freezes
/// door fades -- and with them door CLICKABILITY, because <c>Gob::HitCheck</c> reads the same field -- all
/// particle simulation, grass wind, bin LOD, lens-flare occlusion, and the <c>focuslighting</c> global.</para>
/// <para><b>Why it still walks the buckets.</b> The ADR's shim spec did not, and it is a gap: the part table
/// is populated by hooks on <c>PartTriMesh::Draw</c>/<c>PartSkin::Draw</c>, which are only ever reached from
/// <c>Scene::RenderDrawBucket</c> -- i.e. from inside RSP. A shim that skipped the walk would starve
/// enrollment and Alloy would own nothing to draw. So the walk still runs, with the two engine draw funnels
/// SWALLOWED: every part is still recorded, and no glDraw* issues. <see cref="BucketGate.OnNextItem"/>
/// already skips owned parts before the loop body, so a well-owned area pays this walk almost nothing.
/// ponytail: per-frame walk, no cadence. If it shows up in a profile, the cheap upgrade is to run it only on
/// the frames enrollment is about to consume -- but sightings are the liveness signal, so that is a decision
/// about the staleness window, not a free win.</para>
/// <para><b>Zero draws is measured, not inspected.</b> nwmain imports exactly <c>glDrawArrays</c> and
/// <c>glDrawElements</c>; their only call sites are the two funnels below and <c>nk_sdl_render</c> (2D
/// overlay, outside any Scene pass) -- see <see cref="SymbolMap.GlRenderVertexStreamData"/>. Swallowing both
/// funnels therefore covers the engine's whole 3D draw surface, and <see cref="_passed"/> is counted at the
/// BOTTOM of the chain so it reports what actually reached the original rather than restating the swallow.
/// #258 deleted the one draw path the funnels could not cover -- the batcher's own replayed multidraw,
/// which under this key (the shipped default) never submitted at all.</para>
/// <para>Behind <c>ownframe</c> in <c>sowx.cfg</c>, default ON since #218 closed M4 (ADR 0013 bucket 2: an
/// arming decision, not a look). Set it to 0 and nothing here installs, so the pre-cutover frame is
/// unchanged by construction -- that is the escape hatch, not the default.</para>
/// <para>Dropped on this path, by design and filed as #217's punch list: personal space, paths, drawable
/// squares and light ranges (all four reachable-but-never-issued), the stencil-shadow block, and
/// <c>Camera::DoFrameBufferEffects</c>/<c>CopyRenderSubframe</c>. Anything only the engine drew goes with
/// them -- on SoW that is the dynamic sky (#136). The highlight overlays came BACK in #232/#233; see
/// <see cref="EngineOverlays"/>.</para>
/// <para><b>Also dropped, and it is bookkeeping:</b> RSP's head (@0x46e6b3..0x46e6fb) rebuilds the shadow-light
/// lists <c>Scene+0x60</c>/<c>+0x70</c> from <c>LightManager::GetShadowLights(1)</c>/<c>(0)</c>. Alloy
/// self-drives its own light walk from <c>Scene::AddPartsToDrawBuckets</c>, so the SSBO does not depend on
/// them, and ADR 0016 could not find a consumer outside RSP -- which the shim removes. Not replicated
/// deliberately rather than by oversight: the call returns a <c>CExoArrayList</c> BY VALUE (hidden sret in
/// rdi), and inventing that ABI to preserve a side effect whose only reader we deleted is exactly the guess
/// this repo forbids. If <c>PartLight+0xa8</c>'s frozen shadow-fade weight ever surfaces, close it with RE.</para></summary>
internal static unsafe class RspShim
{
    /// <summary>The config key. Read once, at Initialize; there is no runtime toggle -- <c>/vanilla</c> is
    /// the escape hatch, and it takes the whole Alloy path down including this.</summary>
    internal const string Key = "ownframe";

    /// <summary>Scene vtable slot DISPLACEMENTS, i.e. the offsets from the loaded vptr exactly as
    /// <c>Scene::RenderSinglePass</c> @0x46e5f0 uses them (the byte offset into <c>_ZTV5Scene</c> is 0x10
    /// more, for offset-to-top + typeinfo). Verified against the table rather than taken from the ADR.
    /// Called through the LIVE vptr, as RSP does, so a derived Scene's override is honoured -- and every one
    /// of these targets is a patched hook site, so the anchor, the bucket gate and the emitter suppression
    /// all fire from here for free.</summary>
    private const int VtDoBinBucket = 0x1c0, VtRenderBeams = 0x1d8, VtDoEmitterBucket = 0x1e8,
                      VtRenderLensFlares = 0x230, VtFadeDoors = 0x260, VtDoFog = 0x280;

    /// <summary>#232/#233: the overlay slots RSP calls between bucket 7 and the shadow block, at 0x46e9fb
    /// and 0x46ea15/0x46ee97. RSP calls them UNCONDITIONALLY -- the punch list's "gate" column describes
    /// gates INSIDE them (<c>Scene+0x88</c> for the gob, the trigger-bucket count for the other) -- so the
    /// shim calls them the same way and lets each self-gate.
    /// <para>The third slot RSP calls here, <c>Scene::DoTriggerBucket</c> (0x1f0, punch-list item 14), is
    /// NOT replicated: it draws the entries whose <c>PartTrigger+0xd8</c> is clear, and every trigger the
    /// client displays has it set. See the punch list for the chain that proves it.</para></summary>
    private const int VtDoHighlightGob = 0x1b8, VtDoHighlightTriggers = 0x1f8;

    /// <summary>#235: <c>LightManager</c> vptr displacement of <c>EnableLights()</c>, RSP's very first call
    /// (@0x46e6a5). Verified against <c>_ZTV12LightManager</c> @0x186e2a0, not taken from the ADR.</summary>
    private const int VtEnableLights = 0x18;

    /// <summary>Count of highlight gobs (<c>Scene+0x88</c>); the array is at <c>+0x80</c> and its colours at
    /// <c>+0x90</c>, both read by <c>Scene::DoHighlightGob</c> itself.</summary>
    private const int SceneHighlightGobs = 0x88;

    /// <summary><c>CExoArrayList&lt;Part*&gt;</c> count, at <c>+8</c> of the list head.</summary>
    private const int ListCount = 8;

    /// <summary>Scene -&gt; LightManager* and Scene -&gt; the focus position, read off the
    /// <c>focuslighting</c> block at 0x46ebd7..0x46ec46.</summary>
    private const int SceneLightManager = 0x158, SceneFocusPos = 0x190;

    /// <summary>Draw buckets the walk visits. 0..8 is every id RSP draws (0, 1, 7 opaque; 2, 3 dynamic;
    /// 5 water-sampling; 6 water; 8 fade; 4). Order follows RSP's own; the duplicates its stencil block
    /// re-walks are not repeated, because a second walk would record nothing a first did not.</summary>
    private static readonly int[] WalkOrder = { 0, 1, 7, 2, 3, 5, 8, 6, 4 };

    /// <summary>A three-float value classified the way SysV classifies it -- xmm0 carries x and y, xmm1
    /// carries z. Same reason <c>Particles.V3</c> exists: <c>Vector3</c>'s ABI is the runtime's to choose.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct V3 { public float X, Y, Z; }

    private static ExtenderLog? _log;
    private static bool _enabled;
    private static delegate* unmanaged<IntPtr, int, void> _drawBucket;
    private static delegate* unmanaged<IntPtr, void> _skyBoxes;
    private static delegate* unmanaged<IntPtr, IntPtr, void> _collectLights;
    private static delegate* unmanaged<V3, IntPtr, IntPtr, V3> _emitterLighting;
    private static IntPtr* _focusObject;
    private static float* _focusLighting;
    private static IntPtr* _vdoSlot, _vdoBottomSlot;
    private static int* _fullDynamicLighting;
    private static byte* _triggerBucket;
    private static delegate* unmanaged<IntPtr, uint, uint, uint, void> _origStream;

    /// <summary>Chain priority on <c>GLRender::RenderVertexData</c>. Strictly between
    /// <see cref="DrawFunnelService"/>'s 50 and DrawStatsService's 10, and that placement is the whole
    /// design: the funnel service sits ABOVE and calls the chain before its own <c>Observe</c>, so the bone
    /// capture still happens on a swallowed call; DrawStats sits BELOW, so it does not count a draw that
    /// never issued.</summary>
    private const int SwallowPriority = 20;
    /// <summary>...and the honest counter, at the bottom of the same chain. Anything that reaches the
    /// original during the window passes through here, whatever route it took.</summary>
    private const int BottomPriority = -100;

    /// <summary>True while the shim body is running: the swallow window, and the one key every carve-out
    /// elsewhere reads. Deliberately NOT <see cref="Enabled"/> -- with the key on but the shim branch not
    /// taken (vanilla, no 4.6 context, a GUI scene) the engine is still drawing, and a carve-out keyed on
    /// the setting would arm Alloy over live engine draws.</summary>
    internal static bool InWindow;

    /// <summary>#236: the deliberate hand-back. Set by the bucket walk around every part it is about to
    /// give to the engine BECAUSE ALLOY IS NOT DRAWING IT -- unrecorded, dead, or an identity that moved.
    /// Pre-cutover those parts were engine-drawn and the ownership latency cost a frame of vanilla shading;
    /// under the swallow the same hand-back is a hole, which is what ate the creature torsos. So the window
    /// lets these through and counts them separately: <c>passed</c> (the #218 exit bar) still means a draw
    /// nobody directed, and <c>handback</c> means one Alloy declined by name.</summary>
    internal static bool HandBack;

    /// <summary>#232/#233: does this frame have an engine overlay to run after the bracket? Latched at the
    /// top of <see cref="Run"/>, read by <see cref="PostChain"/> inside it -- the depth hand-back to the
    /// default framebuffer is only worth its blit on the frames an overlay will use it.</summary>
    internal static bool WantsEngineOverlays;

    /// <summary>Did the key arm and every ENGINE symbol resolve? Necessary, not sufficient --
    /// <see cref="Run"/> is reachable only through this AND <see cref="Ready"/>.</summary>
    internal static bool Armed { get; private set; }

    /// <summary>...and is GL up yet? <see cref="Gl"/>'s entry points are resolved lazily on the first frame
    /// that needs them, so at <see cref="Install"/> time -- service init, no context -- every one of them is
    /// null. Asking there is asking too early: it would stand the cutover down for the session over a
    /// condition that is false only before the first frame. So the question moves here, to the FRAME-START
    /// gate, where a "no" still has the engine's own <c>RenderSinglePass</c> behind it (ADR 0016 step 7:
    /// every refusal is frame-start-only, because there is no mid-frame fallback once the shim is in).
    /// <para><see cref="Gl.Init"/> is a single bool on the second call and every call after, so asking per
    /// frame costs a branch.</para></summary>
    internal static bool Ready
    {
        get
        {
            if (Gl.BindFramebuffer != null) return true;
            if (_log == null || !Gl.Init(_log) || Gl.BindFramebuffer == null) return false;
            _log.Info("RspShim: glBindFramebuffer resolved -- the cutover takes the frame from here");
            return true;
        }
    }

    private static bool _checkOk, _barLogged;

    /// <summary>Draw bucket 5, the OTHER framebuffer-sampling class. Bucket 6 is
    /// <see cref="AlloyRenderer.WaterBucket"/>; these two are the only classes whose engine draw reads a
    /// <c>Camera::CopyRenderSubframe</c> snapshot.</summary>
    private const int SampleBucket = 5;

    /// <summary>Engine draws refused because the class samples a copy this shim does not take. See
    /// <see cref="FbSampling"/>.</summary>
    private static long _sampleSwallowed;
    /// <summary>Last period's refusal totals, so the period line can report the CHANGE. See <see cref="Period"/>.</summary>
    private static int _lastRefBuf, _lastRefTex;
    private static long _swallowed, _passed, _handedBack, _ticks, _frames;

    /// <summary>The two invariants the shim cannot discover at runtime and would fail silently on.
    /// <para><b>Chain placement.</b> <see cref="SwallowPriority"/> must sit strictly between
    /// <see cref="DrawFunnelService"/>'s and DrawStatsService's. Above the funnel service and the swallow
    /// eats the bone capture the pose call exists for -- skinned parts would wear the previous frame's pose.
    /// Below DrawStats and the histogram counts draws that never issued.
    /// Neither shows up as an error; both are just a wrong-looking session.</para>
    /// <para><b>Walk coverage.</b> Every bucket id 0..8 exactly once. A missing id is a class that is never
    /// captured and so never owned, which reads as "Alloy cannot draw that class" rather than as a typo.</para></summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = SwallowPriority > DrawStatsService.DrawPriority && SwallowPriority < DrawFunnelService.DrawPriority;
        int seen = 0;
        foreach (int id in WalkOrder) seen |= 1 << id;
        if (seen != 0x1ff || WalkOrder.Length != 9) ok = false;
        // #232/#233: the overlay displacements must be distinct from each other AND from the six the shim
        // already calls. A duplicated displacement is a silent wrong virtual -- FadeDoors run twice, say --
        // which reads as a rendering oddity rather than as the typo it is.
        ReadOnlySpan<int> slots = stackalloc int[] { VtDoBinBucket, VtRenderBeams, VtDoEmitterBucket,
                                                     VtRenderLensFlares, VtFadeDoors, VtDoFog,
                                                     VtDoHighlightGob, VtDoHighlightTriggers };
        for (int i = 0; i < slots.Length && ok; i++)
            for (int j = i + 1; j < slots.Length; j++)
                if (slots[i] == slots[j]) { ok = false; break; }
        _checkOk = ok;
        log.Info(ok ? $"RspShim.SelfCheck: passed (swallow at {SwallowPriority}, "
                    + $"between {DrawStatsService.DrawPriority} and {DrawFunnelService.DrawPriority}; buckets 0-8; "
                    + $"{slots.Length} distinct Scene vtable slots)"
                    : "RspShim.SelfCheck: FAILED -- chain placement, bucket coverage or a vtable displacement "
                    + "is wrong; the cutover would capture nothing, miscount, or call the wrong virtual");
        return ok;
    }

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!_checkOk) return;   // SelfCheck runs unconditionally, early in AlloyRenderer.Initialize
        _enabled = Settings.Bool(Key, true);
        // Key off installs NOTHING: the pre-cutover frame is then byte-for-byte unchanged because there is
        // no layer in the chain to be wrong about, not because a flag was checked.
        if (!_enabled) return;

        _drawBucket = (delegate* unmanaged<IntPtr, int, void>)Native.Sym(SymbolMap.RenderDrawBucket);
        _skyBoxes = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.RenderSkyBoxes);
        _collectLights = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.GobCollectLights);
        _emitterLighting = (delegate* unmanaged<V3, IntPtr, IntPtr, V3>)Native.Sym(SymbolMap.GetEmitterLighting);
        _focusObject = (IntPtr*)Native.Sym(SymbolMap.SceneFocusObject);
        _focusLighting = (float*)Native.Sym(SymbolMap.FocusLighting);
        _fullDynamicLighting = (int*)Native.Sym(SymbolMap.FullDynamicLighting);
        _triggerBucket = (byte*)Native.Sym(SymbolMap.TriggerBucket);
        // Named, not counted: "an engine symbol did not resolve" is unactionable, and this list is the only
        // place that knows which one. glBindFramebuffer is deliberately NOT here -- see Ready.
        string? missing = _drawBucket == null ? SymbolMap.RenderDrawBucket
                        : _skyBoxes == null ? SymbolMap.RenderSkyBoxes
                        : _collectLights == null ? SymbolMap.GobCollectLights
                        : _emitterLighting == null ? SymbolMap.GetEmitterLighting
                        : _focusObject == null ? SymbolMap.SceneFocusObject
                        : _focusLighting == null ? SymbolMap.FocusLighting
                        : _fullDynamicLighting == null ? SymbolMap.FullDynamicLighting
                        : _triggerBucket == null ? SymbolMap.TriggerBucket : null;
        if (missing != null)
        {
            log.Info($"RspShim: {Key}=1 but {missing} did not resolve -- the cutover stands down and "
                   + "Scene::RenderSinglePass keeps running");
            return;
        }

        _vdoSlot = hooks.Chain(SymbolMap.GlRenderVertexData,
            (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)&OnVdoDraw, SwallowPriority);
        _vdoBottomSlot = hooks.Chain(SymbolMap.GlRenderVertexData,
            (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)&OnVdoBottom, BottomPriority);
        _origStream = (delegate* unmanaged<IntPtr, uint, uint, uint, void>)hooks.Install(
            SymbolMap.GlRenderVertexStreamData,
            (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, uint, void>)&OnStreamDraw);
        if (_vdoSlot == null || _vdoBottomSlot == null || _origStream == null)
        {
            log.Info("RspShim: a draw-funnel hook failed -- the cutover stands down rather than replace "
                   + "RenderSinglePass with a shim that cannot prove it drew nothing");
            return;
        }

        Armed = true;
        // "ARMED" is the engine half; the frame does not become Alloy's until Ready as well, which is a
        // first-frame away and logs its own line.
        // #234: the cutover takes Camera::DoFrameBufferEffects with RenderSinglePass, and with it the two
        // client video options that gated it. /knobs is where ADR 0013 says a knob has to be visible, so
        // the layer that removed them says so there rather than leaving them silently inert.
        KnobSurface.Note(static () => !Armed ? null
            : "the client's SSAO and Sharpen video options are INERT this session: they gate "
            + "Camera::DoFrameBufferEffects, which lived inside Scene::RenderSinglePass and went with it "
            + "when ownframe=1 took the frame. An Alloy equivalent is M5 work (ADR 0016); set ownframe=0 "
            + "or /vanilla to get them back.");
        FrameRegistry.Global.Register(FramePhase.PostPresent, 30, Period, "RspShim.period", log);
        log.Info($"RspShim: ARMED -- {Key}=1, Scene::RenderSinglePass replaced by the mutate-only shim on "
               + "the Alloy path ([rspshim] every 900 frames; /vanilla or ownframe=0 restores it)");
    }

    /// <summary>The shim. Runs in ENGINE context, in place of <c>Scene::RenderSinglePass</c>.
    /// <para>Order follows RSP's own. <c>FadeDoors</c> comes first because it is #132's frame anchor: the
    /// whole Alloy frame draws inside that call, which is exactly where it drew before the cutover. The
    /// bucket walk that follows is capture, not drawing.</para>
    /// <para>The default framebuffer is bound on entry and left bound on exit, so Alloy resolves straight
    /// into the backbuffer and the engine's overlay views composite the UI on top of a finished frame. There
    /// is no engine world FBO under the shim: <c>Camera::InitializeFrameBufferResources</c>,
    /// <c>CopyRenderSubframe</c> and <c>DoFrameBufferEffects</c> all live inside RSP and go with it.</para></summary>
    internal static void Run(IntPtr scene)
    {
        long t0 = Stopwatch.GetTimestamp();
        // #238: the Alloy frame draws INSIDE this call (FadeDoors is #132's anchor), so the shim's own
        // number has to have it subtracted -- the published "6ms/frame in the shim" was the walk plus the
        // whole Alloy CPU frame, which is what made the ticket's accounting look like 13ms of 150.
        long alloy0 = FrameZones.Ticks(FrameZones.Zone.Alloy);
        IntPtr vptr = *(IntPtr*)scene;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        // #232/#233: decided BEFORE the anchor, because the answer has to reach PostChain's resolve, which
        // runs inside it. Both counts are already final here -- Scene::AddPartsToDrawBuckets fills the
        // trigger bucket and Scene::AddHighlightGob the gob array, both outside RSP and ahead of it.
        WantsEngineOverlays = *(int*)(scene + SceneHighlightGobs) != 0
                              || *(int*)(_triggerBucket + ListCount) != 0;
        // ...and cleared BEFORE the bracket, not after it: only this frame's blit may arm it. A frame that
        // bails out of DrawOwned early (nothing enrolled, a PostChain refusal, no 4.6 context) never reaches
        // the resolve, and an inherited `true` would depth-test the overlays against a stale buffer.
        PostChain.DefaultDepthOk = false;
        // #235, item 2 and RSP's very first call (@0x46e6a5). Not a draw: it is
        // LightManager::ControlLightsFade(+0x60, +0x40, 0), which advances every PartLight's fade weight at
        // +0x90. That weight is the one FrameRead scales the two area lights at LightManager +0x98/+0xa0
        // by, so this call is what makes FrameInputs.AreaAmbient/AreaDiffuse current -- which is why the
        // anchor's read sits after it (#257). Dropped, the weights freeze wherever the last vanilla frame
        // left them (zero for any light added since), which is wrong-looking lighting, not a missing draw.
        if (*_fullDynamicLighting != 0)
        {
            IntPtr lm = *(IntPtr*)(scene + SceneLightManager);
            if (lm != IntPtr.Zero) Vt(*(IntPtr*)lm, VtEnableLights, lm);
        }
        InWindow = true;
        // BEFORE the anchor, exactly as RSP orders it (@0x46e736, one instruction group ahead of FadeDoors
        // @0x46e742). Scene::DoFog @0x45f1b0 pushes Scene+0x140..0x14c into GLRender::m_fog. Alloy no longer
        // reads that global -- #257 reads the same four Scene fields direct -- but the call stays, because
        // it is RSP bookkeeping like every other item here: EngineOverlays below runs real engine draws
        // through GLRender::SetUniforms, which fogs from m_fog. Dropped, those overlays would fog off
        // whatever RenderDrawBucket's per-item write left there last (0x46d63d writes a SCALED colour).
        Vt(vptr, VtDoFog, scene);              // scene fog -> GLRender::m_fog, for the engine's own overlay draws
        Vt(vptr, VtFadeDoors, scene);          // door fade + hit-test alpha -- AND #132's anchor: Alloy draws here
        _drawBucket(scene, WalkOrder[0]);
        _skyBoxes(scene);
        for (int i = 1; i < WalkOrder.Length; i++)
        {
            int id = WalkOrder[i];
            _drawBucket(scene, id);
            // RSP's own placement, keyed on the BUCKET ID so a reorder of the walk cannot silently move
            // either one: vanilla runs DoBinBucket right after bucket 1 and writes focuslighting after
            // bucket 5.
            if (id == 1) Vt(vptr, VtDoBinBucket, scene);     // grass wind + per-bin LOD distance
            else if (id == 5) FocusLighting(scene);          // the focuslighting write, at RSP's step
        }
        Vt(vptr, VtRenderBeams, scene);        // bucket 15's capture window (#130)
        Vt(vptr, VtDoEmitterBucket, scene);    // the ONLY caller of PartEmitter::AnimateParticles
        Vt(vptr, VtRenderLensFlares, scene);   // flare occlusion fade
        InWindow = false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        EngineOverlays(vptr, scene);
        if (!_barLogged && PostChain.Resolves > 0) Bar();
        long child = FrameZones.Ticks(FrameZones.Zone.Alloy) - alloy0;
        long spent = Stopwatch.GetTimestamp() - t0;
        FrameZones.Add(FrameZones.Zone.Shim, t0, child);
        _ticks += spent - child;
        _frames++;
    }

    /// <summary>#232 + #233: the three dropped overlays, run by the ENGINE at the end of the shim instead of
    /// at RSP's own position, with the swallow window closed so their draws issue.
    /// <para><b>Why the engine draws them and Alloy does not.</b> Both build their geometry into the static
    /// <c>VertexStreamDataObject</c> at 0x1aaba30 through <c>WritePartHighlightVertices</c>, whose arguments
    /// are a <c>Vector</c> and a <c>Quaternion</c> BY VALUE. Calling that from here means inventing an ABI to
    /// re-implement a vertex builder the engine already runs correctly -- the guess this repo forbids, for no
    /// gain. Running the engine's own overlay is faithful by construction and costs two vtable calls.</para>
    /// <para><b>Why here and not at RSP's slot.</b> Alloy draws inside the FadeDoors anchor, in its own 4.6
    /// context, and resolves into the default framebuffer; an engine draw cannot be interleaved into that
    /// bracket. After it, the frame is finished and in DISPLAY space -- which is the space these overlays
    /// blend in -- and <see cref="PostChain.DefaultDepthOk"/> says the owned depth reached the same
    /// framebuffer. Moving them past the water bucket is the one visual difference from vanilla: an
    /// additively-blended highlight now composites over water rather than under it.</para>
    /// <para><b>Item 14 stays dropped, and it is classified rather than assumed.</b>
    /// <c>Scene::DoTriggerBucket</c> walks the SAME trigger bucket and draws exactly the complement of this
    /// pass -- the entries whose <c>PartTrigger+0xd8</c> is clear. That flag's only writer is
    /// <c>PartTrigger::SetHighlighted</c> @0x4da090, reached only through
    /// <c>AurPartTriggerSetHighlight</c>, whose only caller is <c>CNWCTrigger::UpdateTriggerColor</c>
    /// @0x80eff0 -- and every path through that function ends at the same
    /// <c>AurPartTriggerSetHighlight(t, true, …, true)</c>, DM branch included. So the complement is always
    /// empty and the call would draw nothing.</para></summary>
    private static void EngineOverlays(IntPtr vptr, IntPtr scene)
    {
        if (!WantsEngineOverlays || !PostChain.DefaultDepthOk) return;
        Vt(vptr, VtDoHighlightGob, scene);
        // The trigger highlight is a STENCIL pass and needs one: SetShadowStencilBuffer @0x460c30 is
        // ClearStencilValue(0x7f) + SetStencilMask(0xff) + Clear(4), so it clears whatever framebuffer is
        // bound (no FBO of its own to bind, which is why this replay works at all) -- but the mask it then
        // builds is the only thing keeping its full-screen quad off the whole screen.
        if (!PostChain.DefaultStencilOk) return;
        // RSP calls this twice, with the bool that selects which half of the trigger bucket to draw: 0 at
        // 0x46ea15 and 1 at 0x46ee97 (read from the disassembly -- the decompiler drops the argument).
        VtB(vptr, VtDoHighlightTriggers, scene, 0);
        VtB(vptr, VtDoHighlightTriggers, scene, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VtB(IntPtr vptr, int slot, IntPtr scene, byte arg)
        => ((delegate* unmanaged<IntPtr, byte, void>)(*(IntPtr*)(vptr + slot)))(scene, arg);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Vt(IntPtr vptr, int slot, IntPtr scene)
        => ((delegate* unmanaged<IntPtr, void>)(*(IntPtr*)(vptr + slot)))(scene);

    /// <summary>Item 5 of ADR 0016's bookkeeping list, replicated from RSP @0x46ebd7..0x46ec46: collect the
    /// focus object's lights into a stack list, ask the scene's LightManager what that position is lit by,
    /// and store the three floats. <c>Scene::GetLightingValueAtFocus</c> is the exported reader.
    /// <para>ponytail: RSP calls <c>Gob::CollectLights</c> unconditionally, so a null focus object never
    /// happens there. Guarded anyway -- the cost of being wrong is a null <c>this</c> deref in engine code,
    /// and the cost of the guard is a stale <c>focuslighting</c> on a frame with no focus object.</para></summary>
    private static void FocusLighting(IntPtr scene)
    {
        IntPtr focus = *_focusObject;
        if (focus == IntPtr.Zero) return;
        // List<PartLight*> is a two-pointer head/tail block, zeroed before the collect and freed after; the
        // engine's own frame does exactly this on its stack.
        IntPtr* list = stackalloc IntPtr[2];
        list[0] = IntPtr.Zero; list[1] = IntPtr.Zero;
        _collectLights(focus, (IntPtr)list);
        V3 result = _emitterLighting(*(V3*)(scene + SceneFocusPos),
                                     *(IntPtr*)(scene + SceneLightManager), (IntPtr)list);
        _focusLighting[0] = result.X; _focusLighting[1] = result.Y; _focusLighting[2] = result.Z;
        if (list[0] != IntPtr.Zero) NativeMemory.Free((void*)list[0]);
    }

    /// <summary>Is the walk inside a class whose engine draw SAMPLES a framebuffer copy the cutover removed?
    /// <para>Punch-list items 19 and 26 drop both <c>Camera::CopyRenderSubframe</c> calls; items 27 and the
    /// bucket-5 walk keep the walks that consume them. That pair is only coherent while Alloy draws every
    /// part of both classes, and #129 and #236 each build a hand-back for when it does not -- the
    /// <c>!_waterDrew</c> whole-sea fallback in <see cref="AlloyRenderer.IterSight"/>, and the per-part
    /// enrollment latency. Both hand back to an engine draw whose sampler source is never written this
    /// frame, and whose target was never created at all: <c>Camera::InitializeFrameBufferResources</c>'s
    /// sole caller is RSP. Sampling that is undefined, and it is what water tiles flickering in and out
    /// actually is.</para>
    /// <para>So the hand-back is refused for these two classes and only these two. A part Alloy has not
    /// enrolled yet is briefly missing instead of briefly wrong, and converges within
    /// <c>NewCandidateGap</c> frames -- ADR 0016's "temporary non-rendering is acceptable", where drawing
    /// through an uninitialised sampler is not. The bucket stays in <see cref="WalkOrder"/>: the walk is
    /// what carries the sighting, and dropping it would starve the class permanently.</para>
    /// <para>Not the fix for a PERSISTENT refusal -- <c>_waterArmed</c> false all session leaves a silently
    /// missing sea. That is a mid-frame refusal ADR 0016 step 7 says must move to the frame-start gate, and
    /// it is ticket-sized; <see cref="_sampleSwallowed"/> on the period line is what makes it observable
    /// meanwhile.</para></summary>
    private static bool FbSampling
        => BucketGate._inBucket > 0
           && (BucketGate._bucketId == AlloyRenderer.WaterBucket || BucketGate._bucketId == SampleBucket);

    /// <summary>The swallow, layer 20 of the <c>RenderVertexData</c> chain. Returning here skips every lower
    /// subscriber AND the original -- and <see cref="DrawFunnelService"/>, which sits above at 50, still
    /// runs its bone observation after this returns. That is the whole reason the shim can walk the buckets
    /// and still issue no draw.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoDraw(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        if (InWindow && (!HandBack || FbSampling)) { if (HandBack) _sampleSwallowed++; _swallowed++; return; }
        if (InWindow) _handedBack++;
        var next = (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)*_vdoSlot;
        if (next != null) next(vdo, item, prim, size, offset);
    }

    /// <summary>The acceptance counter, at the bottom of the same chain: a call reaching here during the
    /// window reached the ORIGINAL, by whatever route. This is the number that must be zero.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoBottom(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        if (InWindow && !HandBack) _passed++;
        var next = (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)*_vdoBottomSlot;
        if (next != null) next(vdo, item, prim, size, offset);
    }

    /// <summary>The second funnel. Sole owner (nothing else hooks it), so entries are swallowed here and
    /// anything that reaches the trampoline during the window is counted as a pass-through.</summary>
    [UnmanagedCallersOnly]
    private static void OnStreamDraw(IntPtr vsdo, uint prim, uint size, uint offset)
    {
        // #236: honours the hand-back for the same reason the other funnel does. #215 established these two
        // funnels ARE the engine's whole 3D draw surface, so a hand-back routed here and swallowed is a hole
        // exactly like one routed to the other -- without needing to know which parts take which route.
        if (InWindow && (!HandBack || FbSampling)) { if (HandBack) _sampleSwallowed++; _swallowed++; return; }
        if (InWindow) _handedBack++;
        if (_origStream != null) _origStream(vsdo, prim, size, offset);
    }

    /// <summary>#218's second acceptance criterion, asserted rather than assumed: on the first frame the
    /// shim actually took, are the render target, the depth buffer, the vertex/index buffers and the
    /// textures all ALLOY-CREATED?
    /// <para>Each term is the discriminating one for its resource, not a restatement of "the feature is on".
    /// <c>PostChain.Fbo</c>/<c>DepthTex</c> are names Alloy generated -- zero means it is resolving into
    /// something it did not make. The two <c>Refused</c> counters are the borrowed-name leak: both tables
    /// fall back to the ENGINE's name when a copy is refused, silently and per resource, so "armed" alone
    /// does not mean "owned". Read as LIFETIME totals here, because this is the first-resolve verdict and
    /// everything before it is its subject; the period line reports the same two as a per-period delta.</para>
    /// <para>Asked on the first frame Alloy actually RESOLVED, not the first frame the shim took. Those are
    /// not the same frame and the difference inverts the verdict: enrollment needs ~3 walks to converge, so
    /// the first shim frame owns nothing, <c>DrawOwnedInner</c> returns ahead of <c>PostChain.Begin</c>, and
    /// <c>PostChain.Fbo</c> -- generated lazily there -- is still 0. Asked then, this would latch a FAILED
    /// on frame 1 of every clean start and never re-ask. If Alloy never resolves at all, the ABSENCE of this
    /// line says so, alongside the refusals AlloyGate already logs; there is no timeout path.</para>
    /// <para>Logged once, because it is a milestone assert. It is
    /// a log verdict and not a stand-down: by here the engine's frame is already gone, and a shim that
    /// refused to finish the frame over a borrowed texture would trade a wrong-looking world for no world.
    /// ponytail: one line, no per-frame machinery -- the period line re-reads the same terms every 900
    /// frames if a later area regresses one.</para></summary>
    private static void Bar()
    {
        _barLogged = true;
        string? bad = PostChain.Fbo == 0 ? "the render target is not Alloy-created (PostChain.Fbo == 0)"
                    : PostChain.DepthTex == 0 ? "the depth buffer is not Alloy-created (PostChain.DepthTex == 0)"
                    : !OwnedBuffers.Armed ? "owned geometry stood down -- the engine's vertex/index buffers are in use"
                    : !OwnedTextures.Armed ? "owned textures stood down -- engine texture names are being sampled"
                    : OwnedBuffers.Refused != 0 ? $"{OwnedBuffers.Refused} engine buffers refused a copy and are borrowed"
                    : OwnedTextures.Refused != 0 ? $"{OwnedTextures.Refused} engine textures refused a copy and are borrowed"
                    : null;
        _log?.Info(bad == null
            ? $"RspShim: [bar] resources OWNED -- target {PostChain.Fbo}, depth {PostChain.DepthTex}, "
            + "geometry and textures Alloy-created with zero refusals"
            : $"RspShim: [bar] FAILED -- {bad}; the frame is Alloy's but its resources are not entirely");
    }

    /// <summary>The acceptance line. <c>passed</c> is the exit bar's draw counter and must read 0; the shim's
    /// own wall time is the reported half of the frame-time criterion. #238: "own" is now literal -- the
    /// Alloy bracket that runs inside the anchor is subtracted, so this number is the engine walk plus the
    /// bookkeeping vcalls and nothing else. It was the whole Alloy CPU frame as well before that -- <c>/fps</c> is the instrument for
    /// the A/B, which is a live measurement, not a gate.</summary>
    /// <para>#218: the borrowed counts are a DELTA over the period, not the lifetime totals. Both tables
    /// count a refusal once per resource and never decrement, so a lifetime figure would let one transient
    /// refusal in one area taint every line for the rest of the session -- unable to tell a new regression
    /// from a historic one, which is the only thing this column is here to say.</para>
    private static void Period()
    {
        if (_frames < AlloyRenderer.LogEvery) return;
        double ms = _ticks * 1000.0 / Stopwatch.Frequency / _frames;
        _log?.Info($"RspShim: [rspshim] {_frames} shim frames, {_swallowed / _frames} engine draws swallowed/frame, "
                 + $"{_handedBack / _frames} handed back/frame (#236, deliberate), "
                 + $"PASSED {_passed} (must be 0), {ms:F2}ms/frame in the shim, "
                 + $"borrowed {OwnedBuffers.Refused - _lastRefBuf} buffers / "
                 + $"{OwnedTextures.Refused - _lastRefTex} textures this period (must be 0/0), "
                 + $"{_sampleSwallowed} fb-sampling hand-backs refused");
        _lastRefBuf = OwnedBuffers.Refused; _lastRefTex = OwnedTextures.Refused;
        _swallowed = 0; _passed = 0; _handedBack = 0; _sampleSwallowed = 0; _ticks = 0; _frames = 0;
    }
}
