using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>water</c> group (spec §4.3): the storage <see cref="FrameSnapshot"/> reads. <see cref="Diag"/>
/// is a BUDGET of readbacks the pass counts down, not a switch. Touched from
/// <see cref="RenderCommands.Register"/> so the group declares at Initialize on the main thread.</summary>
internal static class Water
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Rect;
    internal static readonly Knob<int> Diag;

    static Water()
    {
        G = KnobGroup.Declare("water", "Graphics", "Water", master: "Enable Alloy water",
            masterHelp: "Water drawn through CX's pipeline (the stock water shader, reflections from the finished frame). Off, the stock water draws.",
            parent: "alloy.enabled");
        Enabled = G.Master!;
        G.Status = FrameSnapshot.Describe;
        Rect = G.Toggle("rect", "rect", true, Tier.ChatOnly);
        Diag = G.Slider("diag", "diag", 0, 64, 1, 0, tier: Tier.ChatOnly);
    }
}
