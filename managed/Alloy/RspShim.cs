using CX.Alloy.Db;
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
/// other caller in the binary (ADR 0016, "RenderSinglePass is not draw-only"). Skipping it outright freezes all
/// particle simulation, grass wind and bin LOD. CX-89 took the door fade off that list (<see cref="DoorFade"/>)
/// and CX-85 the light-side three -- the fade weights, the flare occlusion fade and the <c>focuslighting</c>
/// global are <see cref="Halos"/>' -- so neither <c>Scene::FadeDoors</c> nor any
/// engine light code runs on an Alloy frame.</para>
/// <para><b>It does not walk the buckets.</b> Discovery is <see cref="SceneWalk"/>'s and visibility is
/// <see cref="CullPass"/>'s, so the frame is built from Alloy's own state end to end (ADR 0018, #264). What
/// that costs: a part no Alloy program covers is not drawn, since nothing walks that could hand it back;
/// <c>sample-class=</c> on the <c>[scenewalk]</c> line is the HOLE in the late class (draw bucket 5) over that
/// class's size, measured rather than argued -- CX-90 took the left number to zero for
/// <c>sample_framebuffer 1</c>, and <c>volumetric</c> is what can still stand in it.</para>
/// <para><b>Zero draws is measured, not inspected.</b> nwmain imports exactly <c>glDrawArrays</c> and
/// <c>glDrawElements</c>; their only call sites are the two funnels below and <c>nk_sdl_render</c> (2D overlay,
/// outside any Scene pass) -- see <see cref="SymbolMap.GlRenderVertexStreamData"/>. Swallowing both funnels
/// covers the engine's whole 3D draw surface, and <see cref="_passed"/> is counted at the BOTTOM of the chain
/// so it reports what actually reached the original. The funnels stay because the shim still calls the
/// emitter, sky and beam virtuals for their simulation, and every one of those draws for itself.</para>
/// <para>Always on: submitting inside the engine's frame was unusably slow, not a fallback (#458).
/// <c>vanilla=1</c> is the escape hatch, and it takes the whole Alloy path down. A shim that cannot arm is an
/// <see cref="AlloyGate"/> refusal, so an unarmed shim never has Alloy drawing inside <c>RenderSinglePass</c>.</para>
/// <para>Dropped on this path, by design (#217's punch list): personal space, paths, drawable squares and
/// light ranges (all four reachable-but-never-issued), the stencil-shadow block, and
/// <c>Camera::DoFrameBufferEffects</c>/<c>CopyRenderSubframe</c>. Anything only the engine drew goes with them
/// -- on SoW that is the dynamic sky (#136). CX-82/CX-83 took the last two engine draws with them: the
/// object glow is <see cref="Highlight"/> plus <see cref="HighlightGlow"/>, and the area-transition highlight
/// is <see cref="TriggerHighlight"/>.</para>
/// <para><b>Also dropped, and it is bookkeeping:</b> RSP's head (@0x46e6b3..0x46e6fb) rebuilds the shadow-light
/// lists <c>Scene+0x60</c>/<c>+0x70</c> from <c>LightManager::GetShadowLights(1)</c>/<c>(0)</c>. Alloy
/// self-drives its own light walk from <c>Scene::AddPartsToDrawBuckets</c>, and ADR 0016 found no consumer
/// outside RSP. Not replicated deliberately: the call returns a <c>CExoArrayList</c> BY VALUE (hidden sret in
/// rdi), and inventing that ABI to preserve a side effect with no reader is the guess this repo forbids.
/// <b>SL5 settled the last claim on it:</b> those two lists have no reader in Alloy either -- the sun shafts
/// were the one, and they now take the area's own directional (<c>Beams.Direction</c>). MEASURED on both
/// binaries: this head is their ONLY live-frame writer, the others being <c>Scene::Scene</c> (zeroes) and
/// <c>~Scene</c> (frees without clearing), so anything reading them on an Alloy frame reads nothing or the
/// last vanilla frame. The <c>Scene+0x60</c>/<c>+0x68</c> layout rows went with the reader. If
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
    private static GlRenderVertexDataFn* _vdoSlot, _vdoBottomSlot;
    private static int* _fullDynamicLighting;
    private static GlRenderVertexStreamDataFn _origStream;
    private static RiEnableFn _riEnable, _riDisable;
    /// <summary><c>GLRender::m_pCurrentCustomProgram</c> -- the latch both draw funnels read. Cleared at the
    /// shim's exit; see <see cref="HandBackEngineState"/>.</summary>
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
    /// <summary>Per-step tick sums over the period, indexed by <see cref="_stepNames"/>: emitters, anchor (the
    /// door fade plus the Alloy frame, net of the Alloy bracket inside it), sky, bin bucket, the framebuffer
    /// hand-back, the engine state hand-back. The shim RUNS the sky before the anchor; the index order is the
    /// period line's. What is left against <see cref="_ticks"/> is the light tick and the shim's own
    /// bookkeeping.</summary>
    private static readonly long[] _stepTicks = new long[6];
    private static readonly string[] _stepNames = { "emit", "anchor", "sky", "bin", "present", "handback" };

    /// <summary>The two invariants the shim cannot discover at runtime and would fail silently on.
    /// <para><b>Chain placement.</b> <see cref="SwallowPriority"/> must sit above DrawStatsService's, or the
    /// histogram counts draws that never issued. It does not show up as an error; it is just a wrong-looking
    /// session. #264 removed the upper bound with the funnel service's own chain subscriber.</para>
    /// <para>#264 deleted the bucket-coverage half of this check with the walk it checked.</para></summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = SwallowPriority > DrawStatsService.DrawPriority;
        // The displacements the shim calls through must be distinct. A duplicated displacement is a silent
        // wrong virtual -- FadeDoors run twice, say -- which reads as a rendering oddity rather than as the
        // typo it is. Only the rows the shim still calls are listed; CX-84/85/89 took theirs out with the calls.
        ReadOnlySpan<int> slots = stackalloc int[] { EngineLayout.SceneDoBinBucketSlot, EngineLayout.SceneDoEmitterBucketSlot };
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
        // The engine's frame delta: every ported integrator steps on it, so a gap shows up in the one
        // refusal line below.
        FrameDelta.Resolve(log);
        _fullDynamicLighting = (int*)Native.Sym(SymbolMap.FullDynamicLighting);
        // #237: the state hand-back's two entry points. Deliberately NOT in the `missing` list below -- a shim
        // that cannot restore five enable bits is still a shim that draws the frame, and standing the cutover
        // down over its epilogue would be the worse trade.
        _riEnable = EngineFn.RiEnable;
        _riDisable = EngineFn.RiDisable;
        _curCustom = (IntPtr*)Native.Sym(SymbolMap.GlCurrentCustomProgram);
        // Named, not counted: "an engine symbol did not resolve" is unactionable, and this list is the only
        // place that knows which one. glBindFramebuffer is deliberately NOT here -- see Ready.
        // CX-87 dropped Scene::RenderSkyBoxes from this list with the call: the owned tick needs
        // Gob::ProcessGob instead, and SkyTick stands ITSELF down when that is missing rather than taking the
        // whole cutover with it -- stale sky visibility flags are not a reason to give the engine the frame.
        string? missing = FrameDelta.Missing
                        ?? (_fullDynamicLighting == null ? SymbolMap.FullDynamicLighting : null)
                        // CX-86: unlike the sky, the particle simulation CANNOT stand itself down -- the only
                        // thing behind it is Scene::DoEmitterBucket, whose draws are the engine's, so the
                        // choice is an owned simulation or a vanilla frame.
                        ?? EmitterSim.Missing;
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

    /// <summary>The engine-state hand-back at the shim's exit: <c>Scene::RenderSinglePass</c>'s own trailing
    /// block, replayed verbatim -- <c>Disable(5) Enable(1) Enable(0xb) Enable(0) Disable(2)</c>, read off the
    /// decompilation's tail -- plus the custom-program latch.
    /// <para><b>CX-84 kept it, and this is why.</b> The ticket expected the epilogue to be an overlay artefact.
    /// It is not: it is RSP's own exit contract, and its readers are still there. The frame after the shim is
    /// engine 3D (this) -&gt; the engine's GUI scenes (<c>Scene::RenderOnlyGobs</c> @0x4682e0 and every panel's
    /// own <c>CAurScene</c>) -&gt; the 2D UI and <c>AurRenderStrings</c> -&gt; <c>UiHostService.OnSwap</c>, whose
    /// <c>ResetRenderStates</c> lands AFTER all of them. Those engine draws set their state through the
    /// CACHED <c>RenderInterface</c> wrappers, and Alloy has just driven raw GL for a whole frame without
    /// writing that cache -- so with no resync here a panel that asks for blending can be told it already has
    /// it. RSP's opening <c>Enable(5)/Enable(0)/SetBlendFunc(0)</c> stays dropped: its readers were the engine
    /// draws INSIDE the pass, and there are none.</para>
    /// <para><b>Why two of the five are replayed as a toggle.</b> <c>GLRender::Enable</c> @0x4cae40 opens with
    /// <c>IsEnabled</c> and returns before touching GL when the bit in <c>m_bEnabledStates</c> already agrees;
    /// <c>Disable</c> @0x4caf80 mirrors it. So a plain replay would short-circuit in exactly the desynced
    /// case. Of the five caps only <see cref="CapBlend"/> (<c>GL_BLEND</c>) and <see cref="CapDepthTest"/>
    /// (<c>GL_DEPTH_TEST</c>) reach GL: disabling each first forces the cache to disagree, so the enable that
    /// follows always issues the real call. Caps 5, 1 and 2 are cache/uniform-sequence writes with no GL call
    /// behind them, so for those the cache IS the state and they stay a straight replay.</para>
    /// <para><b>The latch.</b> Both draw funnels open with <c>if (m_pCurrentCustomProgram) SetProgram(0xe)</c>
    /// (<c>RenderVertexData</c> @0x4d05a8, <c>RenderVertexStreamData</c> @0x4d0cd6), and shader type 0xe means
    /// "use whatever that global points at". Its only clearers are <c>DrawReset</c> @0x4cfa00 and
    /// <c>ResetRenderStates</c> @0x4d1b29, both on the engine 3D path the swallow eats, and
    /// <c>SetCustomShaderProgram</c> @0x4ce890 ignores a null argument -- so a program latched inside the
    /// bracket is still current here and would force the FIRST engine draw after it onto that program with
    /// that draw's uniforms. The known latcher was the sky's captured program, which CX-88 deleted; the clear
    /// stays because the latch is a property of the FUNNELS, not of the sky -- <c>SetCustomShaderProgram</c> is
    /// also called from <c>PartEmitter::RenderEmitter</c> @0x4ad4c0 and <c>SharedMaterial::BindCustomShader</c>
    /// @0x4c5590, ahead of the funnel the shim swallows. Zeroed rather than <c>DrawReset</c>'d: this is the one
    /// field of that write set the swallow leaves stale.</para></summary>
    private const int CapBlend = 0, CapDepthTest = 0xb;   // not-an-offset: engine RenderInterface cap enum, an argument to Enable/Disable

    private static void HandBackEngineState()
    {
        if (_curCustom != null) *_curCustom = IntPtr.Zero;
        if (_riEnable == null || _riDisable == null) return;
        _riDisable(5);
        _riEnable(1);
        _riDisable(CapDepthTest); _riEnable(CapDepthTest);
        _riDisable(CapBlend); _riEnable(CapBlend);
        _riDisable(2);
    }

    /// <summary>CX-101: engine draws that reached GL while the GUI was Alloy's -- outside every window and every GUI
    /// scene. Tier 2's claim is that this stays 0; the first one is said, the total is on the status line.</summary>
    internal static long StrayDraws;

    private static void Stray()
    {
        if (!GuiPass.Capturing || GuiCapture.Open) return;   // inside a GUI scene it is a counted custom-shader hand-off
        if (StrayDraws++ == 0)
            _log?.Always("RspShim: an ENGINE draw reached GL while the GUI is Alloy's (outside any GUI scene) -- a tier-2 hole; "
                       + "the count is on /alloy");
    }

    /// <summary>The swallow, layer 20 of the <c>RenderVertexData</c> chain. Returning here skips every lower
    /// subscriber AND the original -- and <see cref="DrawFunnelService"/>, which sits above at 50, still
    /// runs its bone observation after this returns. That is the whole reason the shim can walk the buckets
    /// and still issue no draw. CX-95: inside a GUI scene it records instead (<see cref="GuiCapture.Vdo"/>).</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoDraw(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        if (GuiCapture.Open && GuiCapture.Vdo(vdo, item, prim, size, offset)) return;
        _swallowed++;
    }

    /// <summary>The acceptance counter, at the bottom of the same chain: a call reaching here during the
    /// window reached the ORIGINAL, by whatever route. This is the number that must be zero.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoBottom(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        _passed++;
        GlRenderVertexDataFn next = *_vdoBottomSlot;
        if (next != null) next(vdo, item, prim, size, offset);
    }

    /// <summary>The second funnel. Sole owner (nothing else hooks it), so entries are swallowed here and
    /// anything that reaches the trampoline during the window is counted as a pass-through.</summary>
    [UnmanagedCallersOnly]
    private static void OnStreamDraw(IntPtr vsdo, uint prim, uint size, uint offset)
    {
        if (GuiCapture.Open && (vsdo == IntPtr.Zero || GuiCapture.Stream(vsdo, prim, size, offset))) return;
        _swallowed++;
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
                 + OwnedTextures.RebuildNote()
                 // CX-85: the ported light tick has no other visible surface. `moved` counting down to a
                 // handful once an area settles, and `focus` tracking day/night, is what says it is running.
                 + $"; {FrameDelta.Describe()}");
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
