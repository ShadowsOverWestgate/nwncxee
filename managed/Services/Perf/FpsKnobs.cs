using CX.Framework;

namespace CX.Services.Perf;

/// <summary>The <c>fps</c> group (spec §4.2): the master is the overlay's visibility and now persists (amends
/// #182's session-only <c>/fps</c>); <see cref="Text"/> is the readout's own size (14 unless set; <c>fps.font</c>,
/// which held a Chat-only typeface name before) / typeface / outline. Touched from <c>FpsOverlayService.Initialize</c>.</summary>
internal static class FpsKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly CX.Ui.TextStyle Text;

    static FpsKnobs()
    {
        G = KnobGroup.Declare("fps", "UI", "FPS overlay", master: "Show FPS and ping", masterDefault: false,
            masterHelp: "Frame rate, frame time and server round-trip in the top-right corner.",
            masterChanged: FpsOverlayService.VisibilityChanged);
        Enabled = G.Master!;
        G.Section("Text");
        Text = new CX.Ui.TextStyle(G, "the readout", size: 14, changed: FpsOverlayService.FaceChanged);
    }
}
