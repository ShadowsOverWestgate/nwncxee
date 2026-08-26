using Sowx.Framework;

namespace Sowx.Alloy;

/// <summary>
/// #86 (map #68, spec #77 M1): the frame-atomic vanilla rendering toggle -- the escape hatch for any visual
/// bug without relaunching, and the A/B seam for every M1 acceptance run. <c>/vanilla</c> in chat requests a
/// flip; <see cref="Latch"/> applies it once per frame from the present, so a whole frame is either fully
/// owned or fully vanilla -- never a mix. Every Alloy render site reads <see cref="Alloy"/>, never the
/// request, which is what makes rapid toggling free: N requests inside one frame collapse into the last.
/// <para>Vanilla means the ENGINE renders everything: no Alloy colour pass, no Alloy shadow/depth work, no
/// batching -- and the vanilla stencil shadows the pipeline normally suppresses come back. UI services are
/// untouched by design, so opting out of rendering costs no chat features.</para>
/// </summary>
public static class RenderMode
{
    /// <summary>Written from the input thread (chat command), read by the render thread at the frame edge.</summary>
    private static volatile bool _wantVanilla;
    private static bool _alloy = true;

    /// <summary>The latched mode every render site reads: true = the extender owns rendering.</summary>
    public static bool Alloy => _alloy;

    /// <summary>Request the opposite mode from the next frame on. Returns the requested vanilla state.</summary>
    public static bool Toggle() => _wantVanilla = !_wantVanilla;

    /// <summary>Apply the pending request at the frame boundary. Returns true only on a transition, which is
    /// what the edge work (batch-table teardown) hangs off.</summary>
    public static bool Latch()
    {
        bool owned = !_wantVanilla;
        if (owned == _alloy) return false;
        _alloy = owned;
        return true;
    }

    /// <summary>#86: the ONE place the vanilla toggle takes effect, run from the frame present's Start phase.
    /// The present sits between scene frames, so a flip applied here is frame-atomic -- the next frame is wholly
    /// owned or wholly vanilla. Edge work runs only on a transition (rapid toggling collapses into it).</summary>
    internal static void OnFrameStart()
    {
        if (!Latch()) return;
        bool owned = _alloy;
        // The wipe must land before AlloyRenderer's EndFrame sees the new TableGen at the bottom of this present.
        PartTable.OnRenderModeEdge(owned);
        AlloyRenderer.OnRenderModeEdge(owned);
        _log?.Info(owned
            ? "[rendermode] ALLOY -- Alloy colour pass, shadow pipeline and batching live"
            : "[rendermode] VANILLA -- all Alloy rendering off, engine renders everything (/vanilla to toggle)");
    }

    private static ExtenderLog? _log;

    /// <summary>Join the frame and self-check. Called from <c>AlloyRenderer.Initialize</c>, which owns the
    /// Alloy render pass this latch arms.</summary>
    internal static void Arm(ExtenderLog log)
    {
        _log = log;
        SelfCheck(log);   // #86 latch semantics (off-device)
        FrameRegistry.Global.Register(FramePhase.Start, 0, OnFrameStart, "RenderMode.latch", log);
    }

    /// <summary>Latch semantics, off-device: a request never takes effect before the next <see cref="Latch"/>,
    /// and a request that nets out inside one frame produces no edge.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"RenderMode.SelfCheck FAIL: {w}"); } }

        bool savedWant = _wantVanilla, savedOwned = _alloy;
        try
        {
            _wantVanilla = false; _alloy = true;
            E(!Latch(), "no request -> no edge");
            E(Alloy, "default is owned");

            Toggle();
            E(Alloy, "a request does not take effect mid-frame");
            E(Latch(), "the frame edge reports the transition");
            E(!Alloy, "and applies it");
            E(!Latch(), "a second latch on the same request is not an edge");

            Toggle(); Toggle();   // netted out inside one frame
            E(!Latch(), "toggle-toggle in one frame produces no edge");
            E(!Alloy, "and leaves the mode alone");

            Toggle();
            E(Latch() && Alloy, "toggling back restores Alloy rendering");
        }
        finally { _wantVanilla = savedWant; _alloy = savedOwned; }

        log.Info(ok ? "RenderMode.SelfCheck: passed" : "RenderMode.SelfCheck: FAILED");
    }
}
