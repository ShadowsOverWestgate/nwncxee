using CX.Framework;
using CX.Ui;

namespace CX.Services.Radial;

/// <summary>The <c>radial</c> group (ADR 0025): the master that hands right-click menus to CX's dropdown
/// (CX-689). Off by default until the CRadialMenu layout has been probed live -- the row sits on every
/// right-click, and a wrong offset there is a crash, not a glitch. Touched from <c>RadialService.Initialize</c>
/// so the group declares whether or not the hook arms. The look rows (CX-694) are read per frame by the draw
/// (swatches are RRGGBBAA; <see cref="Opacity"/> multiplies every alpha); the face rows resolve at open, cold.
/// <see cref="Layout"/> is the CX-692 layout bag (<c>radial.p&lt;N&gt;.*</c>, spec §4.2): Hidden, in-file,
/// reset-proof, read and written by <see cref="RadialPins"/>, one layout per <see cref="PlayerIdentity"/>.</summary>
internal static class RadialKnobs
{
    internal const string ModeCascade = "cascade", ModeDrill = "drill";
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Lock, Anim;
    internal static readonly Knob<uint> Bg, Frame, Hover, Text;
    internal static readonly Knob<int> Font, Dwell, AnimMs;
    internal static readonly Knob<float> Opacity;
    internal static readonly Knob<string> Mode, Typeface;
    internal static readonly KnobBag Layout;

    static RadialKnobs()
    {
        G = KnobGroup.Declare("radial", "UI", "Radial menu", master: "Enable the CX radial menu", masterDefault: true,
            masterHelp: "Right-click menus as a nested dropdown drawn by CX. Off, the stock radial menu runs.",
            header: "Drag a row to reorder it, or drag a deep row onto the top list to keep it there; right-click a row for the same edits, Hide and Reset.");
        Enabled = G.Master!;
        Lock = G.Toggle("lock", "Lock the layout", false, help: "Rows cannot be dragged and right-clicking one opens no edit popup. Turn off to rearrange again.");
        Mode = G.Dropdown("mode", "Submenus", new[] { ModeCascade, ModeDrill }, ModeCascade, new[] { "Cascade beside the row", "Drill down in place" },
            help: "Cascade: a submenu opens beside its row, on hover. Drill down: it replaces the list where it is, on click, with a Back row on top -- the pointer never travels.");
        Dwell = G.Slider("dwell", "Submenu delay", 0, 600, 10, (int)CascadeMenu.DefaultDwellMs, "ms", help: "Cascade only: how long the pointer rests on a row before its submenu opens; 0 opens it at once.");
        G.Section("Look");
        Opacity = G.Slider("opacity", "Opacity", 0.2f, 1f, 0.05f, 1f, help: "The whole menu's opacity; each colour's alpha applies on top of it.");
        Bg = G.Swatch("bg", "Background", 0x05060afau, help: "The lists' fill.");
        Frame = G.Swatch("frame", "Frame", 0x8c8c8cffu, help: "The 1 px border around each list.");
        Hover = G.Swatch("hover", "Highlight", 0x38669effu, help: "The hovered row and the row whose submenu is open.");
        Text = G.Swatch("text", "Text", 0xd8dee9ffu, help: "Labels, counts and the submenu marks.");
        int[] ladder = new int[TextRig.MaxFontPx - TextRig.MinFontPx + 2];
        var names = new string[ladder.Length];
        ladder[0] = 0; names[0] = "as UI";
        for (int i = 1; i < ladder.Length; i++) { ladder[i] = TextRig.MinFontPx + i - 1; names[i] = ladder[i].ToString(); }
        Font = G.Dropdown("font", "Text size", ladder, 0, names, layout: true, help: "The menu's own text size, or the UI text size.");
        Typeface = G.Dropdown("typeface", "Typeface", FontRegistry.Names, FontRegistry.DefaultName, layout: true, help: "The menu's own font; default follows the UI typeface.");
        G.Section("Animation");
        Anim = G.Toggle("anim", "Animate", true, help: "Lists fade and slide in as they open.");
        AnimMs = G.Slider("animms", "Animation length", 40, 400, 10, 120, "ms", help: "How long a list takes to appear.");
        Layout = G.Bag("p");
    }
}
