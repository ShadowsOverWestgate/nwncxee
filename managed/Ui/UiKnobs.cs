using CX.Framework;

namespace CX.Ui;

/// <summary>The <c>ui</c> group (spec §4.2 Windows &amp; fonts): the knobs every CX window shares. <see cref="Font"/>
/// is the text size in design px before GUI scale (was chat's <c>font</c>); <see cref="UiHostService"/> bakes
/// the one atlas at <c>font × GUI scale</c> and sets <see cref="Metrics.Scale"/> from it, on a set and on the
/// GUI-scale edge; every window re-lays out from the atlas epoch. <see cref="Typeface"/> (#681) picks the face
/// <c>default</c> is bound to, from <see cref="FontRegistry.Names"/>. Both are the base every element's own
/// <see cref="TextStyle"/> rows follow until set. <see cref="HoverFocus"/> is read by <see cref="UiHost"/>
/// (was <c>cx-ui.cfg</c>'s <c>hoverToFocus</c>); the three snapping knobs (were <c>cx-quickbar.cfg</c>'s grid
/// keys) are applied to <c>ChatWindow</c>'s snap statics by the quickbar tick. Declared through
/// <see cref="UiCategory.Declare"/> from every UI service's Initialize.</summary>
internal static class UiKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<int> Font, GridPx;
    internal static readonly Knob<string> Typeface;
    internal static readonly Knob<bool> HoverFocus, Snap, EdgeSnap;

    static UiKnobs()
    {
        G = KnobGroup.Declare("ui", "UI", "Windows & fonts");
        int[] ladder = new int[TextRig.MaxFontPx - TextRig.MinFontPx + 1];
        for (int i = 0; i < ladder.Length; i++) ladder[i] = TextRig.MinFontPx + i;
        Font = G.Dropdown("font", "Text size", ladder, (int)Metrics.DesignFontPx, layout: true,
                          help: "Size of the text in every CX window and label. Each element may set its own in its Text rows; a chat window on its gear.",
                          changed: UiHostService.ApplyFont);
        Typeface = G.Dropdown("typeface", "Typeface", FontRegistry.Names, FontRegistry.DefaultName, layout: true,
                              help: "The font every CX window draws with: the built-in DejaVu Sans, a font.<name> from cx.cfg, or one installed on this machine. Each element may pick its own in its Text rows. Type in the list to search.",
                              changed: UiHostService.ApplyTypeface);
        HoverFocus = G.Toggle("hoverfocus", "Focus a window by hovering", false,
                              help: "A CX window takes the keyboard when the pointer rests on it, without a click.");
        Snap = G.Toggle("snap", "Snap windows to a grid", false, help: "Dragged windows land on grid points.");
        GridPx = G.Dropdown("gridpx", "Grid size", new[] { 25, 50, 75, 100, 150, 200 }, 50,
                            help: "Spacing of the grid points, in screen px.");
        EdgeSnap = G.Toggle("edgesnap", "Snap to screen edges", true,
                            help: "Dragged windows stick to the screen edge and to each other.");
    }
}

/// <summary>The UI category in panel order (spec §4.2): Chat, Chat shortcuts, Dialog, Nameplates, Combat text, Inventory, Quickbar &amp;
/// compass, Radial menu, FPS overlay, NUI windows, Windows &amp; fonts. Every UI service's Initialize calls <see cref="Declare"/>, so the
/// submenus sit in that order whatever initializes first and whether or not a feature arms. A class of its own
/// with no static state: a static method on a group class would run that group's constructor first.</summary>
internal static class UiCategory
{
    internal static void Declare()
    {
        _ = CX.Services.Chat.Core.ChatKnobs.G; _ = CX.Services.Chat.Core.SnippetKnobs.G; _ = CX.Services.Dialog.DialogKnobs.G; _ = CX.Services.Nameplate.NameplateKnobs.G;
        _ = CX.Services.CombatText.CombatTextKnobs.G; _ = CX.Services.Inventory.InventoryKnobs.G; _ = CX.Services.Quickbar.QuickbarKnobs.G;
        _ = CX.Services.Radial.RadialKnobs.G; _ = CX.Services.Perf.FpsKnobs.G; _ = CX.Services.Nui.NuiKnobs.G;
        _ = UiKnobs.G;
    }

    /// <summary>Off-device, read-only (a set would arm the writer against the user's real file): the eleven groups
    /// declare in panel order, the typeface row offers the registry's names, the font ladder is 6-32, and the rows the
    /// spec re-keyed from the retired files bind under their new names with the old defaults.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UiCategory.SelfCheck");
        Declare();
        string[] order = { "chat", "snippets", "dialog", "nameplate", "combattext", "inventory", "quickbar", "radial", "fps", "nui", "ui" };
        int at = -1;
        var all = KnobGroup.All;
        foreach (string g in order)
        {
            int i = -1;
            for (int k = 0; k < all.Count; k++) if (all[k].Name == g) i = k;
            check.Expect(i > at, $"group {g} declares after the one before it");
            at = i;
        }
        check.Expect(!UiKnobs.Typeface.Disabled && UiKnobs.Typeface.Tier == Tier.User && UiKnobs.Typeface.Layout
          && UiKnobs.Typeface.Values.Length >= 1 && UiKnobs.Typeface.Values[0] == FontRegistry.DefaultName, "ui.typeface is a live layout row over the registry's names, default first");
        check.Expect(UiKnobs.Font.Values[0] == TextRig.MinFontPx && UiKnobs.Font.Values[^1] == TextRig.MaxFontPx && UiKnobs.Font.Layout, "ui.font is the 6-32 layout ladder");
        check.Expect(KnobGroup.Find("ui.hoverfocus") == UiKnobs.HoverFocus && !UiKnobs.HoverFocus.Default && UiKnobs.GridPx.Default == 50 && UiKnobs.EdgeSnap.Default,
          "hoverfocus / gridpx / edgesnap carry the cx-ui / cx-quickbar defaults");
        check.Expect(KnobGroup.Find("fps.enabled") is Knob<bool> fe && fe.Persisted && !fe.Default, "fps.enabled persists and ships off");
        check.Expect(KnobGroup.Find("nameplate.probe") is KnobButton { Tier: Tier.ChatOnly } && KnobGroup.Find("nameplate.mode") == null, "nameplate.probe is the Chat-only button; mode is gone");
        check.Expect(KnobGroup.Find("dialog.placed") is Knob<bool> { Tier: Tier.Hidden } && KnobGroup.Find("dialog.x") is Knob<float> { Tier: Tier.Hidden }, "the dialog rect is Hidden state");
        check.Expect(KnobGroup.Find("quickbar.compass") is Knob<bool> { Default: true } && KnobGroup.Find("quickbar.align") is Knob<int> { Default: 1 }, "quickbar.compass shows by default; align defaults to centre");
        check.Expect(KnobGroup.Find("combattext.enabled") is Knob<bool> { Default: true } && KnobGroup.Find("combattext.bannersize") is Knob<float> { Default: 1.3f }, "combattext rows bind");
        var live = CommandRegistry.Global;
        foreach (string dead in new[] { "/np", "/ct", "/qbar", "/compass" }) check.Expect(!live.Knows(dead), $"{dead} is gone");
        log.Info(check.Ok ? "UiCategory.SelfCheck: passed" : "UiCategory.SelfCheck: FAILED");
    }
}
