using CX.Framework;

namespace CX.Ui;

/// <summary>The <c>ui</c> group (spec §4.2 Windows &amp; fonts): the knobs every CX window shares. <see cref="Font"/>
/// is the text size in design px before GUI scale (was chat's <c>font</c>); <see cref="UiHostService"/> bakes
/// the one atlas at <c>font × GUI scale</c> and sets <see cref="Metrics.Scale"/> from it, on a set and on the
/// GUI-scale edge; every window re-lays out from the atlas epoch. <see cref="Typeface"/> is the one
/// <see cref="Knob.Disabled"/> row until multi-font. <see cref="HoverFocus"/> is read by <see cref="UiHost"/>
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
                          help: "Size of the text in every CX window: chat, dialog, nameplates.",
                          changed: UiHostService.ApplyFont);
        Typeface = G.Dropdown("typeface", "Typeface", new[] { FontRegistry.DefaultName }, FontRegistry.DefaultName,
                              help: "The font every CX window draws with. One choice until multi-font lands.");
        Typeface.Disabled = true;
        HoverFocus = G.Toggle("hoverfocus", "Focus a window by hovering", false,
                              help: "A CX window takes the keyboard when the pointer rests on it, without a click.");
        Snap = G.Toggle("snap", "Snap windows to a grid", false, help: "Dragged windows land on grid points.");
        GridPx = G.Dropdown("gridpx", "Grid size", new[] { 25, 50, 75, 100, 150, 200 }, 50,
                            help: "Spacing of the grid points, in screen px.");
        EdgeSnap = G.Toggle("edgesnap", "Snap to screen edges", true,
                            help: "Dragged windows stick to the screen edge and to each other.");
    }
}

/// <summary>The UI category in panel order (spec §4.2): Chat, Dialog, Nameplates, Combat text, Quickbar &amp;
/// compass, FPS overlay, Windows &amp; fonts. Every UI service's Initialize calls <see cref="Declare"/>, so the
/// submenus sit in that order whatever initializes first and whether or not a feature arms. A class of its own
/// with no static state: a static method on a group class would run that group's constructor first.</summary>
internal static class UiCategory
{
    internal static void Declare()
    {
        _ = CX.Services.Chat.Core.ChatKnobs.G; _ = CX.Services.Dialog.DialogKnobs.G; _ = CX.Services.Nameplate.NameplateKnobs.G;
        _ = CX.Services.CombatText.CombatTextKnobs.G; _ = CX.Services.Quickbar.QuickbarKnobs.G; _ = CX.Services.Perf.FpsKnobs.G;
        _ = UiKnobs.G;
    }

    /// <summary>Off-device, read-only (a set would arm the writer against the user's real file): the seven groups
    /// declare in panel order, the typeface row is the disabled one, the font ladder is 6-32, and the rows the
    /// spec re-keyed from the retired files bind under their new names with the old defaults.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"UiCategory.SelfCheck FAIL: {w}"); } }
        Declare();
        string[] order = { "chat", "dialog", "nameplate", "combattext", "quickbar", "fps", "ui" };
        int at = -1;
        var all = KnobGroup.All;
        foreach (string g in order)
        {
            int i = -1;
            for (int k = 0; k < all.Count; k++) if (all[k].Name == g) i = k;
            E(i > at, $"group {g} declares after the one before it");
            at = i;
        }
        E(UiKnobs.Typeface.Disabled && !UiKnobs.Font.Disabled && UiKnobs.Typeface.Tier == Tier.User, "ui.typeface is the disabled User row");
        E(UiKnobs.Font.Values[0] == TextRig.MinFontPx && UiKnobs.Font.Values[^1] == TextRig.MaxFontPx && UiKnobs.Font.Layout, "ui.font is the 6-32 layout ladder");
        E(KnobGroup.Find("ui.hoverfocus") == UiKnobs.HoverFocus && !UiKnobs.HoverFocus.Default && UiKnobs.GridPx.Default == 50 && UiKnobs.EdgeSnap.Default,
          "hoverfocus / gridpx / edgesnap carry the cx-ui / cx-quickbar defaults");
        E(KnobGroup.Find("fps.enabled") is Knob<bool> fe && fe.Persisted && !fe.Default, "fps.enabled persists and ships off");
        E(KnobGroup.Find("nameplate.probe") is KnobButton { Tier: Tier.ChatOnly } && KnobGroup.Find("nameplate.mode") == null, "nameplate.probe is the Chat-only button; mode is gone");
        E(KnobGroup.Find("dialog.placed") is Knob<bool> { Tier: Tier.Hidden } && KnobGroup.Find("dialog.x") is Knob<float> { Tier: Tier.Hidden }, "the dialog rect is Hidden state");
        E(KnobGroup.Find("quickbar.compass") is Knob<bool> { Default: true } && KnobGroup.Find("quickbar.align") is Knob<int> { Default: 1 }, "quickbar.compass shows by default; align defaults to centre");
        E(KnobGroup.Find("combattext.enabled") is Knob<bool> { Default: true } && KnobGroup.Find("combattext.bannersize") is Knob<float> { Default: 1.3f }, "combattext rows bind");
        var live = CommandRegistry.Global;
        foreach (string dead in new[] { "/np", "/ct", "/qbar", "/compass" }) E(!live.Knows(dead), $"{dead} is gone");
        log.Info(ok ? "UiCategory.SelfCheck: passed" : "UiCategory.SelfCheck: FAILED");
    }
}
