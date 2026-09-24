using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Services.Perf;

namespace CX.Alloy;

/// <summary>The CUTOVER (ADR 0016 step 6, #215): on the Alloy path, in a world frame, the engine's
/// <c>Scene::RenderSinglePass</c> is not called at all -- this runs in its place, calls RSP's per-frame
/// BOOKKEEPING and issues no engine draw.
/// <para><b>Why a shim and not a skip.</b> RSP braids drawing with seven pieces of bookkeeping, four with no
/// other caller in the binary (ADR 0016, "RenderSinglePass is not draw-only"). Skipping it outright freezes
/// door fades -- and with them door CLICKABILITY, because <c>Gob::HitCheck</c> reads the same field -- all
/// particle simulation, grass wind, bin LOD, lens-flare occlusion, and the <c>focuslighting</c> global.</para>
/// <para><b>It does not walk the buckets.</b> Discovery is <see cref="SceneWalk"/>'s and visibility is
/// <see cref="CullPass"/>'s, so the frame is built from Alloy's own state end to end (ADR 0018, #264). What
/// that costs: a part no Alloy program covers is not drawn, since nothing walks that could hand it back;
/// <c>sample-class=</c> on the <c>[scenewalk]</c> line is the <c>sample_framebuffer</c> class's size, measured
/// rather than argued.</para>
/// <para><b>Zero draws is measured, not inspected.</b> nwmain imports exactly <c>glDrawArrays</c> and
/// <c>glDrawElements</c>; their only call sites are the two funnels below and <c>nk_sdl_render</c> (2D overlay,
/// outside any Scene pass) -- see <see cref="SymbolMap.GlRenderVertexStreamData"/>. Swallowing both funnels
/// covers the engine's whole 3D draw surface, and <see cref="_passed"/> is counted at the BOTTOM of the chain
/// so it reports what actually reached the original. The funnels stay because the shim still calls the
/// emitter, sky, beam and lens-flare virtuals for their simulation, and every one of those draws for itself.</para>
/// <para>Always on: submitting inside the engine's frame was unusably slow, not a fallback (#458).
/// <c>vanilla=1</c> is the escape hatch, and it takes the whole Alloy path down. A shim that cannot arm is an
/// <see cref="AlloyGate"/> refusal, so an unarmed shim never has Alloy drawing inside <c>RenderSinglePass</c>.</para>
/// <para>Dropped on this path, by design (#217's punch list): personal space, paths, drawable squares and
/// light ranges (all four reachable-but-never-issued), the stencil-shadow block, and
/// <c>Camera::DoFrameBufferEffects</c>/<c>CopyRenderSubframe</c>. Anything only the engine drew goes with them
/// -- on SoW that is the dynamic sky (#136). The highlight overlays run through <see cref="EngineOverlays"/>.</para>
/// <para><b>Also dropped, and it is bookkeeping:</b> RSP's head (@0x46e6b3..0x46e6fb) rebuilds the shadow-light
/// lists <c>Scene+0x60</c>/<c>+0x70</c> from <c>LightManager::GetShadowLights(1)</c>/<c>(0)</c>. Alloy
/// self-drives its own light walk from <c>Scene::AddPartsToDrawBuckets</c>, and ADR 0016 found no consumer
/// outside RSP. Not replicated deliberately: the call returns a <c>CExoArrayList</c> BY VALUE (hidden sret in
/// rdi), and inventing that ABI to preserve a side effect with no reader is the guess this repo forbids. If
/// <c>PartLight+0xa8</c>'s frozen shadow-fade weight ever surfaces, close it with RE.</para></summary>
internal static unsafe class RspShim
{
    // The Scene vtable calls RSP makes are declared on their EngineLayout rows (`sig:`) and reached as
    // Scene view methods -- each through the LIVE vptr, as RSP does, so a derived Scene's override is
    // honoured, and every one of those targets is a patched hook site, so the anchor, the bucket gate and
    // the emitter suppression all fire from here for free.
    //
    // #232/#233: the overlay pair RSP calls between bucket 7 and the shadow block (at 0x46e9fb and
    // 0x46ea15/0x46ee97) it calls UNCONDITIONALLY -- the punch list's "gate" column describes gates INSIDE
    // them (Scene+0x88 for the gob, the trigger-bucket count for the other) -- so the shim calls them the
    // same way and lets each self-gate. The third, Scene::DoTriggerBucket (0x1f0, punch-list item 14), is
    // NOT replicated: it draws the entries whose PartTrigger+0xd8 is clear, and every trigger the client
    // displays has it set. See the punch list for the chain that proves it.

    private static ExtenderLog? _log;
    /// <summary>#456: the LightManager layout probe's one-shot latch.</summary>
    private static bool _probedLightMgr;
    private static RenderSkyBoxesFn _skyBoxes;
    private static GobCollectLightsFn _collectLights;
    private static IntPtr* _focusObject;
    private static float* _focusLighting;
    private static GlRenderVertexDataFn* _vdoSlot, _vdoBottomSlot;
    private static int* _fullDynamicLighting;
    /// <summary>The trigger bucket: a bare <c>CExoArrayList&lt;Part*&gt;</c> head at a global address.</summary>
    private static EngineList _triggerBucket;
    private static GlRenderVertexStreamDataFn _origStream;
    private static RiEnableFn _riEnable, _riDisable;
    /// <summary><c>GLRender::m_pCurrentCustomProgram</c> -- the latch both draw funnels read. Cleared at the
    /// shim's exit; see <see cref="EngineOverlays"/>.</summary>
    private static IntPtr* _curCustom;

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

    /// <summary>#232/#233: does this frame have an engine overlay to run after the bracket? Latched at the
    /// top of <see cref="Run"/>, read by <see cref="PostChain"/> inside it -- the depth hand-back to the
    /// default framebuffer is only worth its blit on the frames an overlay will use it.</summary>
    internal static bool WantsEngineOverlays;

    /// <summary>Did every ENGINE symbol resolve and both draw funnels hook? Necessary, not sufficient --
    /// <see cref="Run"/> is reachable only through this AND <see cref="Ready"/>. False is an
    /// <see cref="AlloyGate"/> refusal: the frame is then the engine's whole, not Alloy-inside-RSP.</summary>
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

    /// <summary>Last period's refusal totals, so the period line can report the CHANGE. See <see cref="Period"/>.</summary>
    private static int _lastRefBuf, _lastRefTex;
    private static long _swallowed, _passed, _ticks, _frames;
    /// <summary>Per-vcall tick sums over the period, in the order the shim runs them: emitters, anchor
    /// (net of the Alloy bracket inside it), sky, bin bucket, beams+flares, engine overlays. What is left
    /// against <see cref="_ticks"/> is fog/lights/focus and the shim's own bookkeeping. Attribution for the
    /// period line: the shim's ~6ms is engine simulation, and this says whose.</summary>
    private static readonly long[] _stepTicks = new long[6];
    private static readonly string[] _stepNames = { "emit", "anchor", "sky", "bin", "beams", "overlay" };

    /// <summary>The two invariants the shim cannot discover at runtime and would fail silently on.
    /// <para><b>Chain placement.</b> <see cref="SwallowPriority"/> must sit above DrawStatsService's, or the
    /// histogram counts draws that never issued. It does not show up as an error; it is just a wrong-looking
    /// session. #264 removed the upper bound with the funnel service's own chain subscriber.</para>
    /// <para>#264 deleted the bucket-coverage half of this check with the walk it checked.</para></summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = SwallowPriority > DrawStatsService.DrawPriority;
        // #232/#233: the overlay displacements must be distinct from each other AND from the six the shim
        // already calls. A duplicated displacement is a silent wrong virtual -- FadeDoors run twice, say --
        // which reads as a rendering oddity rather than as the typo it is.
        ReadOnlySpan<int> slots = stackalloc int[] { EngineLayout.SceneDoBinBucketSlot, EngineLayout.SceneRenderBeamsSlot,
                                                     EngineLayout.SceneDoEmitterBucketSlot, EngineLayout.SceneRenderLensFlaresSlot,
                                                     EngineLayout.SceneFadeDoorsSlot, EngineLayout.SceneDoFogSlot,
                                                     EngineLayout.SceneDoHighlightGobSlot, EngineLayout.SceneDoHighlightTriggersSlot };
        for (int i = 0; i < slots.Length && ok; i++)
            for (int j = i + 1; j < slots.Length; j++)
                if (slots[i] == slots[j]) { ok = false; break; }
        _checkOk = ok;
        log.Info(ok ? $"RspShim.SelfCheck: passed (swallow at {SwallowPriority}, "
                    + $"above {DrawStatsService.DrawPriority}; "
                    + $"{slots.Length} distinct Scene vtable slots)"
                    : "RspShim.SelfCheck: FAILED -- chain placement or a vtable displacement is wrong; the "
                    + "cutover would miscount or call the wrong virtual");
        return ok;
    }

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!_checkOk) return;   // SelfCheck runs unconditionally, early in AlloyRenderer.Initialize
        _skyBoxes = EngineFn.RenderSkyBoxes;
        _collectLights = EngineFn.GobCollectLights;
        EmitterLighting.Resolve();
        _focusObject = (IntPtr*)Native.Sym(SymbolMap.SceneFocusObject);
        _focusLighting = (float*)Native.Sym(SymbolMap.FocusLighting);
        _fullDynamicLighting = (int*)Native.Sym(SymbolMap.FullDynamicLighting);
        _triggerBucket = new EngineList(Native.Sym(SymbolMap.TriggerBucket));
        // #237: the epilogue's two entry points. Deliberately NOT in the `missing` list below -- a shim that
        // cannot restore five enable bits is still a shim that draws the frame, and standing the cutover down
        // over its epilogue would be the worse trade.
        _riEnable = EngineFn.RiEnable;
        _riDisable = EngineFn.RiDisable;
        _curCustom = (IntPtr*)Native.Sym(SymbolMap.GlCurrentCustomProgram);
        // Named, not counted: "an engine symbol did not resolve" is unactionable, and this list is the only
        // place that knows which one. glBindFramebuffer is deliberately NOT here -- see Ready.
        string? missing = _skyBoxes == null ? SymbolMap.RenderSkyBoxes
                        : _collectLights == null ? SymbolMap.GobCollectLights
                        : !EmitterLighting.Ready ? SymbolMap.GetEmitterLighting
                        : _focusObject == null ? SymbolMap.SceneFocusObject
                        : _focusLighting == null ? SymbolMap.FocusLighting
                        : _fullDynamicLighting == null ? SymbolMap.FullDynamicLighting
                        : _triggerBucket.IsNull ? SymbolMap.TriggerBucket : null;
        if (missing != null)
        {
            log.Info($"RspShim: {missing} did not resolve -- the shim stands down and with it the whole "
                   + "Alloy path (AlloyGate refuses an unarmed shim); the engine renders");
            return;
        }

        _vdoSlot = hooks.ChainGlRenderVertexData(&OnVdoDraw, SwallowPriority);
        _vdoBottomSlot = hooks.ChainGlRenderVertexData(&OnVdoBottom, BottomPriority);
        _origStream = hooks.InstallGlRenderVertexStreamData(&OnStreamDraw);
        if (_vdoSlot == null || _vdoBottomSlot == null || _origStream == null)
        {
            log.Info("RspShim: a draw-funnel hook failed -- the cutover stands down rather than replace "
                   + "RenderSinglePass with a shim that cannot prove it drew nothing");
            return;
        }

        Armed = true;
        // "ARMED" is the engine half; the frame does not become Alloy's until Ready as well, which is a
        // first-frame away and logs its own line.
        // #234/#352: the cutover took Camera::DoFrameBufferEffects with RenderSinglePass; every client video
        // option it gated is served by the Alloy post stack now (gamma #375, aa #377, ssao #379, the rest of
        // the fsfbpostpr chain #352), and each of those notes itself on /knobs.
        FrameRegistry.Global.Register(FramePhase.PostPresent, 30, Period, "RspShim.period", log);
        log.Info("RspShim: ARMED -- Scene::RenderSinglePass replaced by the mutate-only shim on "
               + "the Alloy path ([rspshim] every 900 frames; alloy.enabled=0 restores it)");
    }

    /// <summary>The shim. Runs in ENGINE context, in place of <c>Scene::RenderSinglePass</c>.
    /// <para>Order follows RSP's own. <c>FadeDoors</c> comes first because it is #132's frame anchor: the
    /// whole Alloy frame draws inside that call, which is exactly where it drew before the cutover. What
    /// follows it is bookkeeping -- #264 deleted the bucket walk that used to.</para>
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
        // The shadow bracket runs inside the anchor too (measured: anchor tracked Zone.Shadow tick for
        // tick, 2026-08-31) -- net it out the same way, or the shim re-reports the whole CasterPass CPU.
        long shadow0 = FrameZones.Ticks(FrameZones.Zone.Shadow);
        var sc = new Scene(scene);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        // #232/#233: decided BEFORE the anchor, because the answer has to reach PostChain's resolve, which
        // runs inside it. Both counts are already final here -- Scene::AddPartsToDrawBuckets fills the
        // trigger bucket and Scene::AddHighlightGob the gob array, both outside RSP and ahead of it.
        WantsEngineOverlays = sc.HiliteCount != 0
                              || _triggerBucket.Count != 0;
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
            LightManager lm = sc.LightManager;
            // #456: the EIGHTH VtblSlot class -- the ticket's list of seven missed it, and a slot Alloy
            // calls every frame is exactly what the probe vocabulary reaches.
            AlloyProbe.Once(ref _probedLightMgr, "LightManager", lm.Ptr, _log);
            if (!lm.IsNull) lm.EnableLights();
        }
        InWindow = true;
        // BEFORE the anchor, exactly as RSP orders it (@0x46e736, one instruction group ahead of FadeDoors
        // @0x46e742). Scene::DoFog @0x45f1b0 pushes Scene+0x140..0x14c into GLRender::m_fog. Alloy no longer
        // reads that global -- #257 reads the same four Scene fields direct -- but the call stays, because
        // it is RSP bookkeeping like every other item here: EngineOverlays below runs real engine draws
        // through GLRender::SetUniforms, which fogs from m_fog. Dropped, those overlays would fog off
        // whatever RenderDrawBucket's per-item write left there last (0x46d63d writes a SCALED colour).
        sc.DoFog();                            // scene fog -> GLRender::m_fog, for the engine's own overlay draws
        // #261, and it is the whole of the particle lag. DoEmitterBucket is the only caller of
        // PartEmitter::AnimateParticles, and RSP runs it at step 28 -- AFTER the anchor, so an Alloy frame
        // drew the positions the PREVIOUS frame's simulation left. Under the cutover the order is Alloy's
        // to pick, so the simulation runs first and the pass draws this frame's particles. Its own draws
        // are swallowed by the funnels, exactly as they were at step 28, and the walk is where the
        // population and the engine's back-to-front sort come from.
        long s0 = Stopwatch.GetTimestamp();
        sc.DoEmitterBucket();
        long s1 = Stopwatch.GetTimestamp(); _stepTicks[0] += s1 - s0;
        // #322 (review): the child brackets netted out of the ANCHOR step are sampled across the anchor
        // vcall alone -- the whole-call child below stays scoped to the whole call. Charging the wider scope
        // here goes negative the moment any Alloy/Shadow bracket runs outside the anchor.
        // #261 took the sky POPULATION off this call -- SkyBox.Enroll walks the Scene's own gobs. It stays
        // as the sky's SIMULATION tick: ProcessGob, node animation, the cross-fade alpha writes, and the
        // per-gob SetPosition(Scene+0x190) that parks each dome at the camera. Its draws are swallowed.
        // #136 moved it AHEAD of the anchor, for the reason #261 moved DoEmitterBucket: Alloy's sky draws
        // inside the anchor, so a tick that ran after it fed this frame's sky from last frame's simulation
        // -- and now that the swallow captures the engine's sky PROGRAM and its uniforms, one frame late is
        // one frame of the wrong camera, visible as jitter the moment the player turns.
        SkyBox.InTick = true;
        _skyBoxes(scene);
        SkyBox.InTick = false;
        long s2 = Stopwatch.GetTimestamp(); _stepTicks[2] += s2 - s1;
        // #322 (review): the child brackets netted out of the ANCHOR step are sampled across the anchor
        // vcall alone -- the whole-call child below stays scoped to the whole call. Charging the wider scope
        // here goes negative the moment any Alloy/Shadow bracket runs outside the anchor.
        long ca1 = FrameZones.Ticks(FrameZones.Zone.Alloy), cs1 = FrameZones.Ticks(FrameZones.Zone.Shadow);
        sc.FadeDoors();                        // door fade + hit-test alpha -- AND #132's anchor: Alloy draws here
        long anchorChild = FrameZones.Ticks(FrameZones.Zone.Alloy) - ca1
                         + FrameZones.Ticks(FrameZones.Zone.Shadow) - cs1;
        long s3 = Stopwatch.GetTimestamp();
        // #264: the two items the bucket walk used to carry between its draws. Neither reads a thing the
        // walk leaves behind -- DoBinBucket @0x474c50 iterates the `binbucket` global (wind update, camera
        // distance, the grass sort) and FocusLighting is Scene field arithmetic of our own -- so with the
        // walk gone they simply run in RSP's relative order and nothing else moves.
        sc.DoBinBucket();                      // grass wind + per-bin LOD distance
        long s4 = Stopwatch.GetTimestamp(); _stepTicks[3] += s4 - s3;
        FocusLighting(scene);                  // the focuslighting write, at RSP's step
        // #261 took the beam POPULATION off this call -- Beams.Enroll reads bucket 15 itself. The vcall
        // stays as bookkeeping, like DoFog above: its own draws are swallowed by the funnels, and what is
        // left is the state prologue/epilogue (blend, depth mask, global colour) that RSP's later steps and
        // EngineOverlays below both inherit.
        long s5 = Stopwatch.GetTimestamp();
        sc.RenderBeams();
        sc.RenderLensFlares();                 // flare occlusion fade
        InWindow = false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        // #456: HERE and not one line earlier -- the bracket has closed, so this is the ENGINE's context and
        // its framebuffer 0 is the one about to be presented. Everything the engine draws below (overlays,
        // the 2D UI) then lands on top of the owned frame instead of beside it.
        PresentTarget.HandBack();
        long s6 = Stopwatch.GetTimestamp(); _stepTicks[4] += s6 - s5;
        EngineOverlays(scene);
        Epilogue();
        _stepTicks[5] += Stopwatch.GetTimestamp() - s6;
        if (!_barLogged && PostChain.Resolves > 0) Bar();
        long child = FrameZones.Ticks(FrameZones.Zone.Alloy) - alloy0
                   + FrameZones.Ticks(FrameZones.Zone.Shadow) - shadow0;
        long spent = Stopwatch.GetTimestamp() - t0;
        FrameZones.Add(FrameZones.Zone.Shim, t0, child);
        _ticks += Math.Max(0, spent - child);
        // The Alloy bracket runs inside the anchor vcall; net it out so the step reads the ENGINE's share.
        _stepTicks[1] += Math.Max(0, (s3 - s2) - anchorChild);
        _frames++;
    }

    /// <summary><c>Scene::RenderSinglePass</c>'s trailing state block, replayed verbatim at the shim's own exit
    /// -- <c>Disable(5) Enable(1) Enable(0xb) Enable(0) Disable(2)</c>, read off the decompilation's tail.
    /// Routed through <c>RenderInterface</c> so the engine's enable cache learns about it; the enum values are
    /// not interpreted, because replaying RSP is faithful by construction (#237, punch-list item 38).
    /// <para><b>Why the exit EDGE matters, not the write set.</b> The frame runs engine 3D (this shim) -&gt;
    /// the engine's own 2D UI -&gt; <c>UiHostService.OnSwap</c>, whose <c>ResetRenderStates</c> hand-back is
    /// the LAST thing before the present, so both resyncs land AFTER the 2D UI has drawn and nothing resynced
    /// at the point RSP's epilogue ran. RSP's opening <c>Enable(5)/Enable(0)/SetBlendFunc(0)</c> (item 6)
    /// stays dropped: its readers are the engine draws INSIDE the pass, which under the cutover are only the
    /// shim's own sim vcalls and the overlays above.</para>
    /// <para><b>Why two of the five are replayed as a toggle.</b> <c>GLRender::Enable</c> @0x4cae40 opens with
    /// <c>IsEnabled</c> and returns before touching GL when the bit in <c>m_bEnabledStates</c> already agrees;
    /// <c>Disable</c> @0x4caf80 mirrors it. Alloy drives raw GL for a whole frame and never writes that cache,
    /// so at the shim's exit the cache holds the last engine vcall's belief while GL holds Alloy's leftovers,
    /// and a plain replay would short-circuit in exactly the desynced case. Of the five caps only
    /// <see cref="CapBlend"/> (<c>GL_BLEND</c>) and <see cref="CapDepthTest"/> (<c>GL_DEPTH_TEST</c>) reach GL:
    /// disabling each first forces the cache to disagree, so the enable that follows always issues the real
    /// call. Caps 5, 1 and 2 are cache/uniform-sequence writes with no GL call behind them, so for those the
    /// cache IS the state and they stay a straight replay.</para></summary>
    private const int CapBlend = 0, CapDepthTest = 0xb;   // not-an-offset: engine RenderInterface cap enum, an argument to Enable/Disable

    private static void Epilogue()
    {
        if (_riEnable == null || _riDisable == null) return;
        _riDisable(5);
        _riEnable(1);
        _riDisable(CapDepthTest); _riEnable(CapDepthTest);
        _riDisable(CapBlend); _riEnable(CapBlend);
        _riDisable(2);
    }

    /// <summary>The three dropped highlight overlays, run by the ENGINE at the end of the shim instead of at
    /// RSP's own position, with the swallow window closed so their draws issue (#232, #233).
    /// <para><b>Why the engine draws them and Alloy does not.</b> Both build their geometry into the static
    /// <c>VertexStreamDataObject</c> at 0x1aaba30 through <c>WritePartHighlightVertices</c>, whose arguments
    /// are a <c>Vector</c> and a <c>Quaternion</c> BY VALUE -- re-implementing that means inventing an ABI for
    /// a vertex builder the engine already runs correctly. Two vtable calls, faithful by construction.</para>
    /// <para><b>Why here and not at RSP's slot.</b> Alloy draws inside the FadeDoors anchor, in its own 4.6
    /// context, and resolves into the default framebuffer; an engine draw cannot be interleaved into that
    /// bracket. After it the frame is finished and in DISPLAY space, the space these overlays blend in, and
    /// <see cref="PostChain.DefaultDepthOk"/> says the owned depth reached the same framebuffer. The one visual
    /// difference from vanilla: an additively-blended highlight composites over water rather than under it.</para>
    /// <para><c>Scene::DoTriggerBucket</c> stays dropped, classified rather than assumed: it draws exactly the
    /// complement of this pass -- entries whose <c>PartTrigger+0xd8</c> is clear -- and that flag's only writer
    /// chain (<c>PartTrigger::SetHighlighted</c> @0x4da090 via <c>AurPartTriggerSetHighlight</c> via
    /// <c>CNWCTrigger::UpdateTriggerColor</c> @0x80eff0) always sets it, DM branch included. The complement is
    /// always empty.</para></summary>
    /// <summary>#462: <c>/alloy hlglow 0</c> skips the engine's <c>DoHighlightGob</c> replay (the trigger
    /// stencil pass still runs), isolating the glow from the gob's emissive/colour-shift fold.</summary>
    internal static bool GlowReplay => Renderer.HlGlow.V;

    private static void EngineOverlays(IntPtr scene)
    {
        var sc = new Scene(scene);
        // The custom-program LATCH, cleared before any engine draw issues again. Both funnels open with
        // `if (m_pCurrentCustomProgram) SetProgram(0xe)` (RenderVertexData @0x4d05a8, RenderVertexStreamData
        // @0x4d0cd6), and shader type 0xe means "use whatever that global points at". Its only clearers are
        // DrawReset @0x4cfa00 and ResetRenderStates @0x4d1b29, both on the engine 3D path the swallow eats --
        // and SetCustomShaderProgram @0x4ce890 ignores a null argument, so it cannot clear it either. So the
        // sky's PROG_FIXED, latched by SkyBox.NoteEngineDraw at the swallow, is still current here and the
        // FIRST engine draw after the bracket -- the glow, then the 2D UI -- is forced onto the sky program
        // with the sky's uniforms. Zeroed rather than DrawReset()'d: this is the one field of its write set
        // that the swallow left stale, and its Disable() calls go through the cached wrapper (#237).
        if (_curCustom != null) *_curCustom = IntPtr.Zero;
        // #232 second mode: on a frame the owned outline+fill highlight drew, the engine glow would double
        // up on the gobs it covered -- but only on those. Read-and-clear, so a frame where the owned pass
        // refused (no depth blit, program failure) still gets the full engine glow instead of nothing.
        bool ownedHighlight = Highlight.TakeHighlightDrawn();
        if (!WantsEngineOverlays || !PostChain.DefaultDepthOk) return;
        if (!GlowReplay) { }
        else if (!ownedHighlight) sc.DoHighlightGob();
        else GlowRemainder(scene);
        // The trigger highlight is a STENCIL pass and needs one: SetShadowStencilBuffer @0x460c30 is
        // ClearStencilValue(0x7f) + SetStencilMask(0xff) + Clear(4), so it clears whatever framebuffer is
        // bound (no FBO of its own to bind, which is why this replay works at all) -- but the mask it then
        // builds is the only thing keeping its full-screen quad off the whole screen.
        if (!PostChain.DefaultStencilOk) return;
        // RSP calls this twice, with the bool that selects which half of the trigger bucket to draw: 0 at
        // 0x46ea15 and 1 at 0x46ee97 (read from the disassembly -- the decompiler drops the argument).
        sc.DoHighlightTriggers(0);
        sc.DoHighlightTriggers(1);
    }

    /// <summary>#232 second mode, the remainder: a highlighted gob with no Alloy draw records (an invisible
    /// usable placeable, a climb/sit indicator) never reaches the owned mask, and the engine glow -- which
    /// builds its own vertices from the part tree -- is the only thing that can mark it. So on a frame the
    /// owned pass drew, the engine's <c>DoHighlightGob</c> still runs, NARROWED: Scene's highlight list
    /// (+0x80 gobs / +0x88 count / +0x90 colours, stride 0xc) is compacted in place to the unhandled gobs,
    /// the virtual called, and both arrays restored -- entries and count exactly as found. Same thread,
    /// same frame, no engine code runs in between.
    /// <para>ponytail: past <see cref="GlowCap"/> roots the compaction buffers stop scaling and the FULL
    /// glow runs instead -- a doubled highlight on a hundred-object sweep beats losing the invisible ones.</para></summary>
    private const int GlowCap = 128;

    private static void GlowRemainder(IntPtr scene)
    {
        var sc = new Scene(scene);
        int n = sc.HiliteCount;
        int handled = Highlight.HandledCount;
        if (n <= 0 || handled >= n) return;   // everything owned: no glow at all
        if (n > GlowCap) { sc.DoHighlightGob(); return; }
        IntPtr* gobs = (IntPtr*)sc.HiliteGobs;
        float* cols = (float*)sc.HiliteColors;
        IntPtr* savedGobs = stackalloc IntPtr[GlowCap];
        float* savedCols = stackalloc float[GlowCap * 3];
        new Span<IntPtr>(gobs, n).CopyTo(new Span<IntPtr>(savedGobs, n));
        new Span<float>(cols, n * 3).CopyTo(new Span<float>(savedCols, n * 3));
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            bool owned = false;
            for (int k = 0; k < handled; k++)
                if (Highlight.HandledHl[k] == savedGobs[i]) { owned = true; break; }
            if (owned) continue;
            gobs[m] = savedGobs[i];
            cols[m * 3] = savedCols[i * 3];
            cols[m * 3 + 1] = savedCols[i * 3 + 1];
            cols[m * 3 + 2] = savedCols[i * 3 + 2];
            m++;
        }
        sc.HiliteCount = m;
        if (m > 0) sc.DoHighlightGob();
        new Span<IntPtr>(savedGobs, n).CopyTo(new Span<IntPtr>(gobs, n));
        new Span<float>(savedCols, n * 3).CopyTo(new Span<float>(cols, n * 3));
        sc.HiliteCount = n;
    }

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
        var sc = new Scene(scene);
        EmitterLighting.V3 focusPos = *(EmitterLighting.V3*)Unsafe.AsPointer(ref sc.SkyPos);
        EmitterLighting.V3 result = EmitterLighting.At(focusPos, sc.LightManager.Ptr, (IntPtr)list);
        _focusLighting[0] = result.X; _focusLighting[1] = result.Y; _focusLighting[2] = result.Z;
        if (list[0] != IntPtr.Zero) NativeMemory.Free((void*)list[0]);
    }

    /// <summary>The swallow, layer 20 of the <c>RenderVertexData</c> chain. Returning here skips every lower
    /// subscriber AND the original -- and <see cref="DrawFunnelService"/>, which sits above at 50, still
    /// runs its bone observation after this returns. That is the whole reason the shim can walk the buckets
    /// and still issue no draw.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoDraw(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        if (InWindow)
        {
            // #136: the sky's own draws are the one kind we look at before dropping. SetProgram -- and with
            // it the whole uniform upload -- lives INSIDE the original we are about to skip, so a custom-
            // shader sky would reach Alloy's replay with nothing uploaded. SkyBox makes that call here, in
            // engine context, and records the program.
            if (SkyBox.InTick) SkyBox.NoteEngineDraw(vdo, item);
            _swallowed++; return;
        }
        // #260's tagging: outside the window the ENGINE is drawing for itself -- the UI, the menu, a GUI 3D
        // panel -- so the pools this VDO names are the ones that question is about.
        else OwnedBuffers.NoteEngineDraw(vdo);
        GlRenderVertexDataFn next = *_vdoSlot;
        if (next != null) next(vdo, item, prim, size, offset);
    }

    /// <summary>The acceptance counter, at the bottom of the same chain: a call reaching here during the
    /// window reached the ORIGINAL, by whatever route. This is the number that must be zero.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoBottom(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        if (InWindow) _passed++;
        GlRenderVertexDataFn next = *_vdoBottomSlot;
        if (next != null) next(vdo, item, prim, size, offset);
    }

    /// <summary>The second funnel. Sole owner (nothing else hooks it), so entries are swallowed here and
    /// anything that reaches the trampoline during the window is counted as a pass-through.</summary>
    [UnmanagedCallersOnly]
    private static void OnStreamDraw(IntPtr vsdo, uint prim, uint size, uint offset)
    {
        if (InWindow) { _swallowed++; return; }
        if (_origStream != null) _origStream(vsdo, prim, size, offset);
    }

    /// <summary>Asserted rather than assumed, once: on the first frame Alloy actually RESOLVED, are the render
    /// target, the depth buffer, the vertex/index buffers and the textures all ALLOY-CREATED (#218)? Each term
    /// is the discriminating one for its resource: <c>PostChain.Fbo</c>/<c>DepthTex</c> are names Alloy
    /// generated (zero means it resolves into something it did not make); the two <c>Refused</c> counters are
    /// the borrowed-name leak, since both tables fall back to the ENGINE's name when a copy is refused. Read as
    /// LIFETIME totals here; the period line reports the same two as a per-period delta.
    /// <para>Asked on the first RESOLVED frame, not the first shim frame: enrollment needs ~3 walks to
    /// converge, so the first shim frame owns nothing and <c>PostChain.Fbo</c>, generated lazily, is still 0 --
    /// asked then, this would latch FAILED on every clean start. If Alloy never resolves, the ABSENCE of this
    /// line says so, alongside the refusals AlloyGate logs. A log verdict, not a stand-down: by here the
    /// engine's frame is gone, and refusing to finish it over a borrowed texture trades a wrong-looking world
    /// for no world. ponytail: one line, no per-frame machinery -- the period line re-reads the same terms
    /// every 900 frames if a later area regresses one.</para></summary>
    private static void Bar()
    {
        _barLogged = true;
        string? bad = PostChain.Fbo == 0 ? "the render target is not Alloy-created (PostChain.Fbo == 0)"
                    : PostChain.DepthTex == 0 ? "the depth buffer is not Alloy-created (PostChain.DepthTex == 0)"
                    : !OwnedBuffers.Armed ? "owned geometry stood down -- the engine uploads its own vertex/index pools"
                    : !OwnedTextures.Armed ? "owned textures stood down -- engine texture names are being sampled"
                    : OwnedBuffers.Borrowed != 0 ? $"{OwnedBuffers.Borrowed} engine buffers are drawn from but were never uploaded by Alloy"
                    : OwnedTextures.Borrowed != 0 ? $"{OwnedTextures.Borrowed} engine textures never came through the upload funnel and are borrowed"
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
    /// <para>#218: the borrowed counts are a DELTA over the period, not the lifetime totals. A lifetime
    /// figure would let one transient refusal in one area taint every line for the rest of the session --
    /// unable to tell a new regression from a historic one, which is the only thing this column is here to
    /// say. #259: the texture count can now fall as well as rise (a tid drawn before its upload stops being
    /// borrowed the moment Alloy adopts it), so the delta is signed and a negative reads as recovery.</para>
    private static void Period()
    {
        if (_frames < AlloyRenderer.LogEvery) return;
        double ms = _ticks * 1000.0 / Stopwatch.Frequency / _frames;
        _log?.Info($"RspShim: [rspshim] {_frames} shim frames, {_swallowed / _frames} engine draws swallowed/frame, "
                 + $"PASSED {_passed} (must be 0), {ms:F2}ms/frame in the shim ({StepLine()}), "
                 + $"borrowed {OwnedBuffers.Borrowed - _lastRefBuf} buffers / "
                 + $"{OwnedTextures.Borrowed - _lastRefTex} textures this period (must be 0/0)"
                 // #284: lifetime, not a delta -- one attempt per texture makes it a POPULATION (how much of
                 // the world lost its build race) and never a rate. Empty when nothing has ever been unbuilt.
                 + OwnedTextures.RebuildNote());
        _lastRefBuf = OwnedBuffers.Borrowed; _lastRefTex = OwnedTextures.Borrowed;
        _swallowed = 0; _passed = 0; _ticks = 0; _frames = 0;
        Array.Clear(_stepTicks);
    }

    /// <summary>The per-vcall halves of the shim number, ms/frame each.</summary>
    private static string StepLine()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _stepTicks.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(_stepNames[i]).Append(':')
              .Append((_stepTicks[i] * 1000.0 / Stopwatch.Frequency / _frames).ToString("F2"));
        }
        return sb.ToString();
    }
}
