using CX.Framework;

namespace CX.Services.Perf;

/// <summary>The <c>fps</c> group (spec §4.2): the master is the overlay's visibility and now persists (amends
/// #182's session-only <c>/fps</c>); <see cref="Font"/> is the Chat-only <c>font.&lt;name&gt;</c> typeface until
/// multi-font. Touched from <c>FpsOverlayService.Initialize</c>.</summary>
internal static class FpsKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<string> Font;

    static FpsKnobs()
    {
        G = KnobGroup.Declare("fps", "UI", "FPS overlay", master: "Show FPS and ping", masterDefault: false,
            masterHelp: "Frame rate, frame time and server round-trip in the top-right corner.",
            masterChanged: FpsOverlayService.VisibilityChanged);
        Enabled = G.Master!;
        Font = G.Text("font", "font", CX.Ui.FontRegistry.DefaultName, Tier.ChatOnly, changed: FpsOverlayService.FaceChanged);
    }
}
