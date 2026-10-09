using CX.Framework;
using CX.Ui;

namespace CX.Services.CombatText;

/// <summary>The <c>combattext</c> group (spec §4.2) and the storage <see cref="CombatTextService"/> and
/// <see cref="FloatyTable"/> read: the master (off = the hook forwards and the stock floating text draws),
/// three User rows, the Advanced banner and <see cref="Text"/> rows, and the Chat-only pixel spacings. Vanilla's own two knobs (<c>ui.floating-text-feedback</c> and the
/// damage-totals option) are NOT duplicated: the engine still composes the strings, so consolidation and the
/// damage-type colours follow the stock settings by construction. <see cref="Text"/> is the numbers' own size /
/// typeface / outline (the outline the old toggle's key). Touched from
/// <c>CombatTextService.Initialize</c> so the group declares whether or not the hook arms.</summary>
internal static class CombatTextKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly TextStyle Text;
    internal static readonly Knob<int> Max, Life, BannerLife, Rise, Stack, Lift;
    internal static readonly Knob<float> BannerSize;

    static CombatTextKnobs()
    {
        G = KnobGroup.Declare("combattext", "Interface", "Names & combat text", master: "Enable CX combat text",
            masterHelp: "Damage numbers and combat banners drawn by CX: stacked per target, outlined, animated. Needs CX nameplates, "
                      + "which anchor them. Off, the stock floating text draws.",
            order: 222, title: "Combat text");
        Enabled = G.Master!;
        Max = G.Slider("max", "Numbers per target", 1, 16, 1, 6,
                       help: "Damage numbers shown on one creature at once; older ones are dropped.");
        Life = G.Slider("life", "Number lifetime", 200, 6000, 100, 1200, "ms", help: "How long a damage number stays.");
        BannerLife = G.Slider("bannerlife", "Banner lifetime", 200, 6000, 100, 1500, "ms", Tier.Advanced,
                              help: "How long a banner (*Sneak Attack!*) stays.");
        BannerSize = G.Slider("bannersize", "Banner size", 1f, 4f, 0.1f, 1.3f, "×", Tier.Advanced,
                              help: "Banner text relative to a damage number.", changed: CombatTextService.RequestPeakFace);
        Rise = G.Slider("rise", "Rise distance", 0, 400, 4, 52, "px", Tier.ChatOnly,
                        help: "How far a number floats up over its life.");
        Stack = G.Slider("stack", "Stack spacing", 0, 120, 2, 20, "px", Tier.ChatOnly);
        Lift = G.Slider("lift", "Banner lift", 0, 300, 2, 34, "px", Tier.ChatOnly,
                        help: "Banners start this far above the damage column.");
        G.Section("Text");
        Text = new TextStyle(G, "damage numbers and banners", outline: 1, changed: CombatTextService.RequestPeakFace,
                             tier: Tier.Advanced);
    }
}
