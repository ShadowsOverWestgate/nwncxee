using CX.Framework;

namespace CX.Services.Perf;

/// <summary>The <c>fps</c> group: the master is the overlay's visibility and persists; <see cref="Text"/> is the
/// readout's own size (14 unless set), typeface and outline. Touched from <c>FpsOverlayService.Initialize</c>.</summary>
internal static class FpsKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly CX.Ui.TextStyle Text;

    static FpsKnobs()
    {
        G = KnobGroup.Declare("fps", "Interface", "Windows & text", master: "Show FPS and ping", masterDefault: false,
            masterHelp: "Frame rate, frame time and server round-trip in the top-right corner.",
            masterChanged: FpsOverlayService.VisibilityChanged, order: 202, title: "FPS overlay");
        Enabled = G.Master!;
        G.Section("Text");
        Text = new CX.Ui.TextStyle(G, "the readout", size: 14, changed: FpsOverlayService.FaceChanged, tier: Tier.Advanced);
    }
}
