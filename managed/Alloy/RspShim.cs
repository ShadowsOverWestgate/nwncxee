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
/// The one draw path the funnels do not cover is the batcher's own multidraw, which is why
/// <see cref="BatchService"/> disarms batching outright when this key is on.</para>
/// <para>Behind <c>ownframe</c> in <c>sowx.cfg</c>, default OFF (ADR 0013 bucket 2: an arming decision, not a
/// look). With the key off nothing here installs, so the pre-cutover frame is unchanged by construction.</para>
/// <para>Dropped on this path, by design and filed as #217's punch list: the six engine overlays (highlight
/// gob, triggers, personal space, paths, drawable squares, light ranges), the stencil-shadow block, and
/// <c>Camera::DoFrameBufferEffects</c>/<c>CopyRenderSubframe</c>. Anything only the engine drew goes with
/// them -- on SoW that is the dynamic sky (#136).</para>
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
    private static delegate* unmanaged<IntPtr, uint, uint, uint, void> _origStream;

    /// <summary>Chain priority on <c>GLRender::RenderVertexData</c>. Strictly between BatchService's 50 and
    /// DrawStatsService's 10, and that placement is the whole design: BatchService sits ABOVE and calls the
    /// chain before its own <c>Observe</c>, so capture still happens on a swallowed call; DrawStats sits
    /// BELOW, so it does not count a draw that never issued.</summary>
    private const int SwallowPriority = 20;
    /// <summary>...and the honest counter, at the bottom of the same chain. Anything that reaches the
    /// original during the window passes through here, whatever route it took.</summary>
    private const int BottomPriority = -100;

    /// <summary>True while the shim body is running: the swallow window, and the one key every carve-out
    /// elsewhere reads. Deliberately NOT <see cref="Enabled"/> -- with the key on but the shim branch not
    /// taken (vanilla, no 4.6 context, a GUI scene) the engine is still drawing, and a carve-out keyed on
    /// the setting would arm Alloy over live engine draws.</summary>
    internal static bool InWindow;

    /// <summary>Did the key arm and every symbol resolve? <see cref="Run"/> is only reachable through this.</summary>
    internal static bool Armed { get; private set; }

    private static bool _checkOk;
    private static long _swallowed, _passed, _ticks, _frames;

    /// <summary>The two invariants the shim cannot discover at runtime and would fail silently on.
    /// <para><b>Chain placement.</b> <see cref="SwallowPriority"/> must sit strictly between BatchService's
    /// and DrawStatsService's. Above BatchService and the swallow eats the capture the walk exists for --
    /// the world would come up empty. Below DrawStats and the histogram counts draws that never issued.
    /// Neither shows up as an error; both are just a wrong-looking session.</para>
    /// <para><b>Walk coverage.</b> Every bucket id 0..8 exactly once. A missing id is a class that is never
    /// captured and so never owned, which reads as "Alloy cannot draw that class" rather than as a typo.</para></summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = SwallowPriority > DrawStatsService.DrawPriority && SwallowPriority < BatchService.DrawPriority;
        int seen = 0;
        foreach (int id in WalkOrder) seen |= 1 << id;
        if (seen != 0x1ff || WalkOrder.Length != 9) ok = false;
        _checkOk = ok;
        log.Info(ok ? $"RspShim.SelfCheck: passed (swallow at {SwallowPriority}, "
                    + $"between {DrawStatsService.DrawPriority} and {BatchService.DrawPriority}; buckets 0-8)"
                    : "RspShim.SelfCheck: FAILED -- chain placement or bucket coverage is wrong; the cutover "
                    + "would capture nothing or miscount");
        return ok;
    }

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!_checkOk) return;   // SelfCheck runs unconditionally, early in AlloyRenderer.Initialize
        _enabled = Settings.Bool(Key, false);
        // Key off installs NOTHING: the pre-cutover frame is then byte-for-byte unchanged because there is
        // no layer in the chain to be wrong about, not because a flag was checked.
        if (!_enabled) return;

        _drawBucket = (delegate* unmanaged<IntPtr, int, void>)Native.Sym(SymbolMap.RenderDrawBucket);
        _skyBoxes = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.RenderSkyBoxes);
        _collectLights = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.GobCollectLights);
        _emitterLighting = (delegate* unmanaged<V3, IntPtr, IntPtr, V3>)Native.Sym(SymbolMap.GetEmitterLighting);
        _focusObject = (IntPtr*)Native.Sym(SymbolMap.SceneFocusObject);
        _focusLighting = (float*)Native.Sym(SymbolMap.FocusLighting);
        if (_drawBucket == null || _skyBoxes == null || _collectLights == null || _emitterLighting == null
            || _focusObject == null || _focusLighting == null || Gl.BindFramebuffer == null)
        {
            log.Info($"RspShim: {Key}=1 but an engine symbol or glBindFramebuffer did not resolve -- the "
                   + "cutover stands down and Scene::RenderSinglePass keeps running");
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
        IntPtr vptr = *(IntPtr*)scene;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        InWindow = true;
        // BEFORE the anchor, exactly as RSP orders it (@0x46e736, one instruction group ahead of FadeDoors
        // @0x46e742). Scene::DoFog @0x45f1b0 reads Scene+0x140..0x14c and drives SetFogModef/SetFogModev
        // into GLRender::m_fog -- which is the block AlloyRenderer.Uniforms reads for the fog enable, start,
        // end and colour of EVERY owned part. Dropped, the last writer before the anchor is
        // RenderDrawBucket's own per-item fog from the PREVIOUS frame's walk (0x46d63d writes a per-item
        // SCALED colour), so Alloy's fog would be stale residue tinted by whichever part drew last, and
        // would stop tracking area transitions and script fog changes entirely.
        Vt(vptr, VtDoFog, scene);              // scene fog -> GLRender::m_fog, which Alloy's stages read
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
        _ticks += Stopwatch.GetTimestamp() - t0;
        _frames++;
    }

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

    /// <summary>The swallow, layer 20 of the <c>RenderVertexData</c> chain. Returning here skips every lower
    /// subscriber AND the original -- and BatchService, which sits above at 50, still runs its capture
    /// observation after this returns. That is the whole reason the shim can walk the buckets for capture
    /// and still issue no draw.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoDraw(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        if (InWindow) { _swallowed++; return; }
        var next = (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)*_vdoSlot;
        if (next != null) next(vdo, item, prim, size, offset);
    }

    /// <summary>The acceptance counter, at the bottom of the same chain: a call reaching here during the
    /// window reached the ORIGINAL, by whatever route. This is the number that must be zero.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoBottom(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        if (InWindow) _passed++;
        var next = (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)*_vdoBottomSlot;
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

    /// <summary>The acceptance line. <c>passed</c> is the exit bar's draw counter and must read 0; the shim's
    /// own wall time is the reported half of the frame-time criterion -- <c>/fps</c> is the instrument for
    /// the A/B, which is a live measurement, not a gate.</summary>
    private static void Period()
    {
        if (_frames < AlloyRenderer.LogEvery) return;
        double ms = _ticks * 1000.0 / Stopwatch.Frequency / _frames;
        _log?.Info($"RspShim: [rspshim] {_frames} shim frames, {_swallowed / _frames} engine draws swallowed/frame, "
                 + $"PASSED {_passed} (must be 0), {ms:F2}ms/frame in the shim");
        _swallowed = 0; _passed = 0; _ticks = 0; _frames = 0;
    }
}
