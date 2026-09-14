using CX.Framework;

namespace CX.Services.Quickbar;

/// <summary>The <c>quickbar</c> group (spec §4.2 Quickbar &amp; compass) and the storage
/// <see cref="QuickbarService"/> reads: the master (off = the stock centre position, no bars, compass shown;
/// live both ways, #591), the three quickbar rows and the compass toggle -- <c>quickbar.compass=1</c> shows it, the inverse of the old
/// <c>compass</c> hide key. Window snapping moved to the <c>ui</c> group. A layout set marks the service dirty;
/// the in-game tick repositions and redraws on the frame. Touched from <c>QuickbarService.Initialize</c> so the
/// group declares whether or not the hook arms.</summary>
internal static class QuickbarKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Bars, Hidden, Compass;
    internal static readonly Knob<int> Align;

    static QuickbarKnobs()
    {
        G = KnobGroup.Declare("quickbar", "UI", "Quickbar & compass", master: "Enable the CX quickbar layout",
            masterHelp: "Centres the quickbar correctly and lets it sit left or right with background bars. Off, the stock quickbar position is used.",
            masterChanged: QuickbarService.LayoutChanged);
        Enabled = G.Master!;
        G.Section("Quickbar");
        Align = G.Dropdown("align", "Position", new[] { (int)QbarAlign.Left, (int)QbarAlign.Center, (int)QbarAlign.Right },
                           (int)QbarAlign.Center, new[] { "left", "centre", "right" },
                           help: "Where the quickbar sits along the bottom edge.", changed: QuickbarService.LayoutChanged);
        Bars = G.Toggle("bars", "Black bars", true, help: "Full-width bars behind the quickbar and under the chat.",
                        changed: QuickbarService.LayoutChanged);
        Hidden = G.Toggle("hidden", "Hide the quickbar", false, help: "Moves the quickbar off-screen.",
                          changed: QuickbarService.LayoutChanged);
        G.Section("Compass");
        Compass = G.Toggle("compass", "Show the compass", true, help: "The compass panel of the stock HUD.");
    }

    /// <summary>The alignment <see cref="Align"/> stores.</summary>
    internal static QbarAlign Alignment => (QbarAlign)Align.V;
}
