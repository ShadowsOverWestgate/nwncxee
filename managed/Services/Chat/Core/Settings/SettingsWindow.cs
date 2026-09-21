using System.Collections.Generic;
using CX.Framework;
using CX.Ui;
using Row = CX.Ui.KnobPanel.Row;

namespace CX.Services.Chat.Core;

/// <summary>
/// The chat settings window: the chat-side consumer of the shared <see cref="KnobPanel"/> (CX-683, CX-696). It owns
/// the content only -- the category list (<c>Window settings</c>, one entry per kept tab, <c>Add new tab...</c>),
/// which rows the chosen category holds (toggles, dropdowns, sliders, expandable RGBA colour pickers, the tab
/// editor, "copy from window") and what each press does to the <see cref="ChatWindow"/>. The one shared knob it
/// still fronts (<c>chat.tellopen</c>) is seeded from the knob and a pick is handed back to the shell
/// (<see cref="TellOpenPicked"/>, like <see cref="CopyRequested"/>); the font rows are the window's own
/// (<see cref="ChatWindow.FontPx"/> / <see cref="ChatWindow.Typeface"/>) and a pick raises <see cref="FontChanged"/>
/// so the shell bakes the face, cold. The pure core never sets a live knob and never touches the text rig.
/// The panel owns everything else: placement, the header drag, the columns, the row layout, hit-test-to-row,
/// the dropdown list, the inline text buffer, and the draw. Pure -- pixels in, hit-tests + value edits out, no
/// native refs -- so it self-checks off-device.
/// </summary>
public sealed class SettingsWindow
{
    public const string WindowCategory = "Window settings", AddTabCategory = "Add new tab...";
    /// <summary>The default typeface / text size entry: the window follows <c>ui.typeface</c> / <c>ui.font</c>.</summary>
    public const string FollowUi = "UI default";

    /// <summary>The shared panel this window's rows are drawn and hit-tested by (the renderer calls
    /// <see cref="KnobPanel.Emit"/> on it): two columns, modal, its rows from <see cref="Layout"/>.</summary>
    public readonly KnobPanel Panel;

    // The model the panel's callbacks edit. Stashed at each public entry point because the shell passes the live
    // window per call; a null window means "no model" and the callbacks no-op (the shell's focused window can be
    // gone by the time a key commits an edit).
    private ChatWindow? _win;

    private string _expanded = "";// colour key whose RGBA channels are shown, or ""
    /// <summary>Tab index per category entry: -1 for <see cref="WindowCategory"/> and <see cref="AddTabCategory"/>.</summary>
    private int[] _catTab = System.Array.Empty<int>();

    // Dropdown choice tables, in enum order (the picked index IS the enum value). Static: a row points at
    // them, never copies.
    private static readonly string[] TellOpenNames = { "Off", "New tab", "New window" };
    private static readonly string[] AlignNames = { "Left", "Right", "Center", "Justify" };
    private static readonly string[] FormatNames = { "HH:mm", "HH:mm:ss", "h:mm tt" };
    private static readonly string[] SourceNames = { "Local", "Game" };
    private static readonly string[] BgModeNames = { "Solid", "Linear", "Radial", "Conic" };
    /// <summary>The text-size ladder: <see cref="FollowUi"/>, then every point from <see cref="TextRig.MinFontPx"/> to
    /// <see cref="TextRig.MaxFontPx"/> (a layout-affecting knob is a dropdown, never a slider -- ADR 0025).</summary>
    public static readonly string[] FontSizes = BuildFontSizes();
    private static string[] BuildFontSizes()
    {
        int lo = TextRig.MinFontPx, hi = TextRig.MaxFontPx;
        var a = new string[hi - lo + 2];
        a[0] = FollowUi;
        for (int i = 1; i < a.Length; i++) a[i] = (lo + i - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return a;
    }
    /// <summary>The typeface choices: <see cref="FollowUi"/>, then the registry's names past <c>default</c>. Set by the
    /// shell once the registry loaded; the self-check supplies its own.</summary>
    public static string[] Typefaces = { FollowUi };

    public SettingsWindow()
    {
        Panel = new KnobPanel
        {
            Title = "Chat window",
            HeaderAction = "Copy this window",
            HeaderActionPressed = () => CopyWindowRequested = true,
            WFrac = 0.52f, HFrac = 0.66f, CatFrac = 0.30f, SubFrac = 0f,
            Modal = true,
            Source = () => { if (_win != null) Layout(_win); },
            CategoryPicked = OnCategoryPicked,
            RowPressed = OnRowPressed,
            ApplyValue = OnApplyValue,
            CommitLabel = OnCommitLabel,
        };
    }

    public float X => Panel.X;
    public float Y => Panel.Y;
    public float W => Panel.W;
    public float H => Panel.H;
    public bool Open => Panel.Open;
    public List<Row> Rows => Panel.Rows;

    /// <summary>Number of chat windows (snapshot), used to show "Copy from Window N" rows. Set by the shell
    /// before <see cref="Layout"/> (the panel is modal, so window count can't change while it's open).</summary>
    public int WindowCount;
    /// <summary>Index of the window this panel is editing (0 = Main). Set by the shell on open.</summary>
    public int WindowIndex;

    /// <summary>Set by the header's "Copy this window": the shell opens a new window carrying this one's settings.</summary>
    public bool CopyWindowRequested;

    /// <summary>Set by the second press of "Close this window" (the first only arms it); the shell closes the window.</summary>
    public bool CloseWindowRequested;
    /// <summary>True between the first press of "Close this window" and the confirming second; any other press disarms.</summary>
    public bool CloseArmed;

    /// <summary>Index of the source window for a pending "Copy from" action, or -1. The shell reads + clears
    /// this after MouseDown to perform the actual copy (it owns the window list, which Layout doesn't).</summary>
    public int CopyRequested = -1;

    /// <summary>A "Tell auto-open" pick (the <see cref="TellAutoOpen"/> value), or -1. The shell reads + clears it
    /// after MouseDown and sets <c>chat.tellopen</c>; the core stays pure and the SelfCheck sets nothing live.</summary>
    public int TellOpenPicked = -1;

    /// <summary>Set true when the window's typeface or text size was picked. The shell reads + clears it to bake the
    /// face (cold, on the input thread) and relay the window.</summary>
    public bool FontChanged;

    /// <summary>Set true when the user just turned TellsHere on. The shell reads + clears this to enforce
    /// exclusivity (clear TellsHere on all other windows — the panel only sees one window).</summary>
    public bool TellsHereChanged;

    /// <summary>The tab the chosen category edits, or -1 for the window / add entries.</summary>
    public int CategoryTab => (uint)Panel.Category < (uint)_catTab.Length ? _catTab[Panel.Category] : -1;

    /// <summary>Rebuild the category list and the chosen category's rows from the live window + config,
    /// publishing a fresh list. Input-thread only (open + each settings interaction); the ingest-thread Rebuild
    /// never calls it, so the list it builds is single-writer and only ever read cross-thread as a whole snapshot.</summary>
    public void Layout(ChatWindow win)
    {
        _win = win;
        var cats = new List<string> { WindowCategory };
        var catTab = new List<int> { -1 };
        for (int ti = 0; ti < win.Tabs.Count; ti++)
        {
            if (win.Tabs[ti].Ephemeral) continue;
            cats.Add(win.Tabs[ti].Label); catTab.Add(ti);
        }
        cats.Add(AddTabCategory); catTab.Add(-1);
        Panel.Categories = cats.ToArray(); _catTab = catTab.ToArray();
        if ((uint)Panel.Category >= (uint)cats.Count - 1) Panel.Category = 0;   // a removed tab, or the add entry: back to the window

        var rows = new List<Row>();
        int tab = CategoryTab;
        if (tab >= 0) LayoutTab(rows, win, tab); else LayoutWindow(rows, win);
        Panel.Publish(rows);
    }

    private void LayoutWindow(List<Row> rows, ChatWindow win)
    {
        AddSection(rows, "Window");
        AddToggle(rows, SetAct.InputTop, "Input on top", win.InputOnTop, 0,
                  "The input line pops out above the window instead of below it.");
        AddToggle(rows, SetAct.AutoHideHdr, "Auto-hide header", win.AutoHideHeader, 0,
                  "The tab bar hides until the pointer is over the window.");
        AddToggle(rows, SetAct.AutoHideFrame, "Auto-hide outline", win.AutoHideFrame, 0,
                  "The frame and its shadow hide until the pointer is over the window.");
        AddToggle(rows, SetAct.LockWin, "Lock window", win.Locked, 0,
                  "No moving, resizing or tab dragging on this window.");
        AddToggle(rows, SetAct.TellsHere, "Tells here", win.TellsHere, 0,
                  "Tells land in this window. Only one window can hold this.");
        // A pending pick shows at once: the shell sets the knob only after the press returns.
        AddDropdown(rows, SetAct.CycleTellOpen, "Tell auto-open", TellOpenNames, TellOpenPicked >= 0 ? TellOpenPicked : ChatKnobs.TellOpen.V,
                    "Where a tell from someone new lands: nowhere special, a new tab, or its own window.");

        AddSection(rows, "Lines");
        foreach (var (bit, label) in ChatWindow.Options)
            AddToggle(rows, SetAct.ToggleBit, label, (win.Toggles & bit) != 0, bit);
        AddDropdown(rows, SetAct.CycleAlign, "Alignment", AlignNames, (int)win.Align, "");
        AddDropdown(rows, SetAct.CycleFmt, "Time format", FormatNames, (int)win.TimeFormat, "");
        AddDropdown(rows, SetAct.CycleSrc, "Time source", SourceNames, (int)win.TimeSource,
                    "Local: your clock. Game: the in-game world clock.");

        AddSection(rows, "Font");
        AddDropdown(rows, SetAct.Typeface, "Typeface", Typefaces, TypefaceIndex(win.Typeface),
                    "The font this window draws its text with; UI default follows the Typeface setting under UI.");
        AddDropdown(rows, SetAct.SliderFont, "Text size", FontSizes, FontIndex(win.FontPx),
                    "Text size in this window; UI default follows the Text size setting under UI.");
        AddToggle(rows, SetAct.OutlineOn, "Outline", win.Outline, 0,
                  "A dark halo around the text for readability over bright scenes.");

        AddSection(rows, "Animation");
        AddToggle(rows, SetAct.AnimOn, "Animations", win.AnimEnabled, 0,
                  "Lines fade in and out; the window eases when scrolled.");
        if (win.AnimEnabled)
        {
            AddSlider(rows, SetAct.SliderHold, "Hold (s)", win.HoldSec, 0f, 300f);
            AddSlider(rows, SetAct.SliderFade, "Fade (s)", win.FadeSec, 0f, 30f);
            AddSlider(rows, SetAct.SliderTau, "Ease (ms)", win.TauMs, 10f, 500f);
        }

        AddSection(rows, "Colours");
        foreach (var (key, label, alpha) in ChatColor.Editable)
        {
            uint rgba = ColorOf(win, key);
            AddSwatch(rows, label, key, rgba);
            if (_expanded == key)
                for (int ch = 0; ch < (alpha ? 4 : 3); ch++)
                    AddChannel(rows, key, ch, ChannelByte(rgba, ch));
            if (key == ChatColor.Background)
            {
                AddDropdown(rows, SetAct.CycleBgMode, "Bg style", BgModeNames, (int)win.BgMode,
                            "Solid, or a gradient from Background to Background 2: Linear along a direction, Radial from the centre, Conic around it.");
                if (win.BgMode != BgMode.Solid)
                {
                    if (win.BgMode != BgMode.Radial)
                        AddSlider(rows, SetAct.SliderBgAngle, "Direction (°)", win.BgAngle, 0f, 360f,
                                  "0 runs left to right, 90 top to bottom.");
                    AddSlider(rows, SetAct.SliderBgStart, "Start (%)", win.BgStart * 100f, 0f, 100f,
                              "Background holds up to here.");
                    AddSlider(rows, SetAct.SliderBgEnd, "End (%)", win.BgEnd * 100f, 0f, 100f,
                              "Background 2 holds from here; at or before Start makes a hard edge.");
                    uint bg2 = ColorOf(win, ChatColor.Background2);
                    AddSwatch(rows, "Background 2", ChatColor.Background2, bg2);
                    if (_expanded == ChatColor.Background2)
                        for (int ch = 0; ch < 4; ch++)
                            AddChannel(rows, ChatColor.Background2, ch, ChannelByte(bg2, ch));
                }
            }
        }

        // Copy from another window (hidden when only one window exists).
        if (WindowCount > 1) AddSection(rows, "Other windows");
        for (int wi = 0; wi < WindowCount; wi++)
        {
            if (wi == WindowIndex) continue;
            rows.Add(new Row { Ctl = KnobKind.Button, Act = SetAct.CopyFrom, Text = $"Copy from Window {wi + 1}",
                               Key = wi.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        // Closing lives here (the tab bar's [×] went with CX-683); Main is undeletable, so it gets no row.
        if (win.Closeable)
        {
            AddSection(rows, "Close");
            rows.Add(new Row { Ctl = KnobKind.Button, Act = SetAct.CloseWindow,
                               Text = CloseArmed ? "Click again to confirm" : "Close this window",
                               HelpText = "Closes this chat window. Its tabs are gone; press twice to confirm." });
        }
    }

    /// <summary>The tab editor (#39): label, the channel filter, remove. The tell pin is the tab bar's own affordance, not a setting.</summary>
    private static void LayoutTab(List<Row> rows, ChatWindow win, int ti)
    {
        Tab tab = win.Tabs[ti];
        string tk = ti.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AddSection(rows, "Tab");
        rows.Add(new Row { Ctl = KnobKind.Text, Act = SetAct.TabLabel, Text = "Label", Value = tab.Label, Key = tk,
                           HelpText = "The name on the tab." });
        AddSection(rows, "Channels");
        foreach (var (bit, chLabel) in FilterChannel.Editable)
            rows.Add(new Row { Ctl = KnobKind.Toggle, Act = SetAct.TabChannel, Text = chLabel,
                               On = (tab.ChannelMask & bit) != 0, Bit = bit, Key = tk });
        if (win.Tabs.Count > 1)
            rows.Add(new Row { Ctl = KnobKind.Button, Act = SetAct.TabRemove, Text = "Remove tab", Key = tk });
    }

    /// <summary>Resolve a per-window colour to its current RGBA8 (override or default). Set by the shell so
    /// the pure core doesn't reach into the renderer for defaults.</summary>
    public static System.Func<ChatWindow, string, uint> ColorResolver = (win, key) =>
        win.Colors.TryGetValue(key, out uint v) ? v : 0xffffffffu;
    private static uint ColorOf(ChatWindow win, string key) => ColorResolver(win, key);

    private static void AddSection(List<Row> rows, string label)
        => rows.Add(new Row { Kind = KnobPanel.RowKind.Section, Act = SetAct.None, Text = label.ToUpperInvariant() });

    private static void AddToggle(List<Row> rows, SetAct act, string label, bool on, uint bit, string help = "")
    { rows.Add(new Row { Ctl = KnobKind.Toggle, Act = act, Text = label, On = on, Bit = bit, HelpText = help }); }

    private static void AddDropdown(List<Row> rows, SetAct act, string label, string[] choices, int selected, string help)
    { rows.Add(new Row { Ctl = KnobKind.Dropdown, Act = act, Text = label, Choices = choices, Selected = selected, HelpText = help }); }

    /// <summary>Index of a window font px in <see cref="FontSizes"/>: 0 follows the UI, else the point, clamped to the ladder.</summary>
    private static int FontIndex(int px)
    {
        if (px <= 0) return 0;
        int i = px - TextRig.MinFontPx + 1;
        return i < 1 ? 1 : i >= FontSizes.Length ? FontSizes.Length - 1 : i;
    }

    /// <summary>Index of a window typeface in <see cref="Typefaces"/>: 0 (follow the UI) when empty or unknown.</summary>
    private static int TypefaceIndex(string name)
    {
        if (name.Length == 0) return 0;
        for (int i = 1; i < Typefaces.Length; i++)
            if (string.Equals(Typefaces[i], name, System.StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private static void AddSlider(List<Row> rows, SetAct act, string label, float value, float min, float max, string help = "")
    {
        float norm = max > min ? (value - min) / (max - min) : 0f;
        rows.Add(new Row { Ctl = KnobKind.Slider, Act = act, Text = label, Norm = Clamp01(norm), Min = min, Max = max, HelpText = help });
    }

    private static void AddSwatch(List<Row> rows, string label, string key, uint rgba)
    { rows.Add(new Row { Ctl = KnobKind.Swatch, Act = SetAct.ColorSwatch, Text = label, Key = key, Rgba = rgba }); }

    private static void AddChannel(List<Row> rows, string key, int ch, int val)
    {
        rows.Add(new Row { Kind = KnobPanel.RowKind.Channel, Act = SetAct.ColorChannel, Text = "RGBA"[ch].ToString(),
                           Key = key, Channel = ch, Norm = val / 255f, Min = 0f, Max = 255f });
    }

    /// <summary>Open the panel centred in a <paramref name="vpW"/>×<paramref name="vpH"/> viewport on the faces the
    /// shell baked at <paramref name="em"/> (null faces lay out on estimates and draw nothing). Opens on the
    /// window category. <paramref name="winIndex"/> and <paramref name="winCount"/> drive the "Copy from" rows.</summary>
    public void OpenAt(ChatWindow win, float vpW, float vpH, float em = KnobPanel.FacePx,
                       GlyphAtlas? body = null, GlyphAtlas? small = null, int winIndex = 0, int winCount = 1)
    {
        _win = win;
        WindowIndex = winIndex; WindowCount = winCount;
        Panel.Category = 0; Panel.Cats.Reset(); Panel.List.Reset();
        Panel.OpenAt(vpW, vpH, em, body, small);   // lays out through Source
    }

    /// <summary>Swap in fresh faces (the sheet re-baked while open: same px, so the layout stands; the next
    /// interaction re-lays out). Safe off the input thread -- two field writes.</summary>
    public void SetFaces(GlyphAtlas? body, GlyphAtlas? small) { Panel.Body = body; Panel.Small = small; }

    internal bool Contains(float mx, float my) => Panel.Contains(mx, my);

    /// <summary>Pointer press while open. Always consumed (the panel is modal). Mutates the window/config per
    /// the hit row; a slider hit starts a drag (see <see cref="MouseMove"/>). Close button or a click-off closes.</summary>
    public bool MouseDown(float mx, float my, ChatWindow win)
    {
        _win = win;
        return Panel.MouseDown(mx, my);
    }

    /// <summary>A category press: a tab or the window entry selects it; <see cref="AddTabCategory"/> adds an
    /// All-channel tab and selects its new entry.</summary>
    private void OnCategoryPicked(int c)
    {
        ChatWindow? win = _win;
        if (win != null && c == Panel.Categories.Length - 1)
        {
            win.AddTab(new Tab("All", 0xffffffff));
            Panel.Category = c;   // the new tab's entry takes this slot; the add entry moves one down
            return;
        }
        Panel.Category = c;
    }

    /// <summary>A press on a non-slider row: mutate the window/config. Slider and text rows never reach here --
    /// the panel owns the track drag, the value box and the label edit. A dropdown row arrives after a pick,
    /// with <see cref="Row.Selected"/> already the chosen index (= the enum value).</summary>
    private void OnRowPressed(Row row)
    {
        ChatWindow? win = _win;
        if (win == null) return;
        bool armed = CloseArmed;
        CloseArmed = false;
        switch (row.Act)
        {
            case SetAct.CloseWindow:
                if (armed) CloseWindowRequested = true; else CloseArmed = true;
                break;
            case SetAct.ToggleBit: win.Toggles ^= row.Bit; break;
            case SetAct.InputTop: win.InputOnTop = !win.InputOnTop; break;
            case SetAct.AutoHideHdr: win.AutoHideHeader = !win.AutoHideHeader; break;
            case SetAct.AutoHideFrame: win.AutoHideFrame = !win.AutoHideFrame; break;
            case SetAct.LockWin: win.Locked = !win.Locked; break;
            case SetAct.TellsHere: win.TellsHere = !win.TellsHere; TellsHereChanged = win.TellsHere; break;
            case SetAct.CycleTellOpen: TellOpenPicked = row.Selected; break;
            case SetAct.AnimOn: win.AnimEnabled = !win.AnimEnabled; break;
            case SetAct.OutlineOn: win.Outline = !win.Outline; break;
            case SetAct.CycleFmt: win.TimeFormat = (TsFormat)row.Selected; break;
            case SetAct.CycleSrc: win.TimeSource = (TsSource)row.Selected; break;
            case SetAct.CycleAlign: win.Align = (TextAlign)row.Selected; break;
            case SetAct.CycleBgMode: win.BgMode = (BgMode)row.Selected; break;
            case SetAct.SliderFont: win.FontPx = row.Selected <= 0 ? 0 : TextRig.MinFontPx + row.Selected - 1; FontChanged = true; break;
            case SetAct.Typeface: win.Typeface = row.Selected <= 0 || row.Selected >= Typefaces.Length ? "" : Typefaces[row.Selected]; FontChanged = true; break;
            case SetAct.ColorSwatch: _expanded = _expanded == row.Key ? "" : row.Key; break;
            case SetAct.TabRemove:
            {
                int ti = ParseIndex(row.Key);
                if (win.RemoveTab(ti)) Panel.Category = 0;
                break;
            }
            case SetAct.TabChannel:
            {
                int ti = ParseIndex(row.Key);
                if ((uint)ti < (uint)win.Tabs.Count)
                {
                    win.Tabs[ti].ChannelMask ^= row.Bit;
                    if (ti == win.ActiveTab) win.ClampSendChannel();
                }
                break;
            }
            case SetAct.CopyFrom:
                CopyRequested = ParseIndex(row.Key);
                break;
        }
    }

    /// <summary>True while the panel is being dragged by its header (not a slider grab).</summary>
    public bool Moving => Panel.Moving;

    /// <summary>Pointer motion while a slider or the header is grabbed, or a hover moved.</summary>
    public bool MouseMove(float mx, float my, ChatWindow win)
    {
        _win = win;
        return Panel.MouseMove(mx, my);
    }

    /// <summary>Write a raw value onto the window for a slider/channel row and mirror it back onto the row's
    /// draw state so the knob/value box track the pointer.</summary>
    private void OnApplyValue(Row r, float v)
    {
        ChatWindow? win = _win;
        if (win == null) return;
        if (r.Act == SetAct.ColorChannel)
        {
            int b = (int)(v + 0.5f); b = b < 0 ? 0 : b > 255 ? 255 : b;
            uint cur = ColorOf(win, r.Key);
            win.Colors[r.Key] = WithChannel(cur, r.Channel, b);
            r.Rgba = win.Colors[r.Key];
            r.Norm = b / 255f;
            foreach (Row sw in Panel.Rows)
                if (sw.Act == SetAct.ColorSwatch && sw.Key == r.Key) sw.Rgba = win.Colors[r.Key];
        }
        else
        {
            float cl = v < r.Min ? r.Min : v > r.Max ? r.Max : v;
            r.Norm = r.Max > r.Min ? (cl - r.Min) / (r.Max - r.Min) : 0f;
            switch (r.Act)
            {
                case SetAct.SliderHold: win.HoldSec = cl; break;
                case SetAct.SliderFade: win.FadeSec = cl; break;
                case SetAct.SliderTau: win.TauMs = cl; break;
                case SetAct.SliderBgAngle: win.BgAngle = cl; break;
                case SetAct.SliderBgStart: win.BgStart = cl / 100f; break;
                case SetAct.SliderBgEnd: win.BgEnd = cl / 100f; break;
            }
        }
    }

    // ---- inline text entry ---------------------------------------------------------------------

    /// <summary>True while the panel wants typed text: a value box or label edit, or a searching list.</summary>
    public bool Editing => Panel.Editing;
    /// <summary>Is <paramref name="r"/> the row being text-edited? (the renderer shows the buffer + a caret).</summary>
    public bool EditingRow(Row r) => Panel.EditingRow(r);
    /// <summary>The in-progress edit buffer (renderer draws it in the active value box).</summary>
    public string EditText => Panel.EditText;

    /// <summary>A row's <see cref="Row.Key"/> as an index (a tab, or a "Copy from" source window); -1 when it is none.</summary>
    private static int ParseIndex(string key)
        => int.TryParse(key, System.Globalization.NumberStyles.None,
                         System.Globalization.CultureInfo.InvariantCulture, out int i) ? i : -1;

    /// <summary>Append typed characters (a searching list takes them as its filter).</summary>
    public void EditInsert(string s) => Panel.EditInsert(s);

    public void EditBackspace() => Panel.EditBackspace();

    /// <summary>Commit the typed value; the panel routes a label here and parses a value box itself.</summary>
    public void CommitEdit(ChatWindow? win)
    {
        _win = win;
        Panel.CommitEdit();
    }

    /// <summary>Commit a tab label: the trimmed text goes on the tab (empty keeps the old one) and its category
    /// entry is renamed.</summary>
    private void OnCommitLabel(Row r, string t)
    {
        ChatWindow? win = _win;
        if (win == null) return;
        int ti = ParseIndex(r.Key);
        string trimmed = t.Trim();
        if ((uint)ti >= (uint)win.Tabs.Count || trimmed.Length == 0) return;
        win.Tabs[ti].Label = trimmed;
        r.Value = trimmed;
        Layout(win);   // the category entry carries the label
    }

    public void CancelEdit() => Panel.CancelEdit();

    /// <summary>Pointer release. Consumed iff we owned the press. Ends any slide drag.</summary>
    public bool MouseUp() => Panel.MouseUp();

    /// <summary>Wheel notch while open: the open dropdown list, else the column under the pointer.</summary>
    public bool MouseWheel(int dy) => Panel.MouseWheel(dy);

    /// <summary>True while a slider or the panel itself is being dragged.</summary>
    public bool Dragging => Panel.Dragging;

    /// <summary>Force-close at a session boundary, clearing any half-owned press, drag, or field edit.</summary>
    public void Reset() { Panel.Close(); _expanded = ""; TellsHereChanged = false; FontChanged = false; CopyWindowRequested = false; CloseWindowRequested = false; CloseArmed = false; }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    private static int ChannelByte(uint rgba, int ch) => (int)((rgba >> (24 - ch * 8)) & 0xFF);
    private static uint WithChannel(uint rgba, int ch, int val)
    {
        int shift = 24 - ch * 8;
        return (rgba & ~(0xFFu << shift)) | ((uint)(val & 0xFF) << shift);
    }

    /// <summary>Off-device proof of the widget state machine: the category list, open/close/ownership, each
    /// control type's edit, colour-channel expansion, the tab editor and the font rows.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        // Resolve colours against a fixed table for the test (the shell wires the real renderer resolver).
        var defs = new Dictionary<string, uint> { [ChatColor.Text] = 0x102030ff };
        ColorResolver = (w, key) => w.Colors.TryGetValue(key, out uint v) ? v : (defs.TryGetValue(key, out uint d) ? d : 0x808080ffu);
        string[] realFaces = Typefaces;
        Typefaces = new[] { FollowUi, "Arial", "DejaVu Serif" };

        var body = new GlyphAtlas { LineHeight = 24, Ascent = 19, SpaceAdvance = 6f };
        var small = new GlyphAtlas { LineHeight = 19, Ascent = 15, SpaceAdvance = 5f };
        for (int cp = 0x20; cp < 0x180; cp++) { body.SetTestGlyph(cp, cp == ' ' ? 6f : 11f); small.SetTestGlyph(cp, cp == ' ' ? 5f : 8f); }
        body.SetTestGlyph(0x25BE, 11f); small.SetTestGlyph(0x25BE, 8f);
        void Open(SettingsWindow sw, ChatWindow w, int winIndex = 0, int winCount = 1)
        { sw.OpenAt(w, 1280, 800, 20f, body, small, winIndex, winCount); sw.MouseUp(); }
        Row Find(SettingsWindow sw, SetAct act, string key = "") => sw.Rows.Find(r => r.Act == act && (key.Length == 0 || r.Key == key))!;
        // The window rows overflow the column: scroll a row into the box before pressing it (offset-correct hits).
        void Reveal(SettingsWindow sw, Row r) { sw.Panel.List.Offset = r.Top - 0.4f * sw.Panel.Em; sw.Panel.List.Clamp(); }
        void PressRow(SettingsWindow sw, ChatWindow w, Row r) { Reveal(sw, r); sw.MouseDown(sw.Panel.List.X + 5, sw.Panel.RowTop(r) + 2, w); sw.MouseUp(); }
        void PressBox(SettingsWindow sw, ChatWindow w, Row r)
        { Reveal(sw, r); sw.Panel.BoxRect(r, sw.Panel.RowTop(r), out float x0, out float x1, out float y0, out float y1); sw.MouseDown((x0 + x1) / 2f, (y0 + y1) / 2f, w); sw.MouseUp(); }
        void WheelToEnd(SettingsWindow sw) { for (int i = 0; i < 64; i++) sw.MouseWheel(-1); }
        void PressCat(SettingsWindow sw, ChatWindow w, int c)
        { sw.MouseDown(sw.Panel.Cats.X + 5, sw.Panel.Cats.Y + 0.4f * sw.Panel.Em + c * (body.LineHeight + 1.2f * sw.Panel.Em) + 2, w); sw.MouseUp(); }

        var win = new ChatWindow(100, 100, 400, 300, new[] { new Tab("All", 0xffffffff) });
        var s = new SettingsWindow();

        Open(s, win);
        check.Expect(s.Open && s.Rows.Count > 0 && s.Panel.Category == 0, "OpenAt opens on the window category with rows");
        check.Expect(s.Panel.Categories.Length == 3 && s.Panel.Categories[0] == WindowCategory && s.Panel.Categories[1] == "All" && s.Panel.Categories[2] == AddTabCategory,
          "categories: the window, one per tab, add");
        check.Expect(Find(s, SetAct.TabLabel) == null && Find(s, SetAct.ToggleBit) != null, "the window category holds the window rows, no tab editor");
        check.Expect(System.Math.Abs(s.X - (1280 - s.W) / 2f) < 0.5f && System.Math.Abs(s.Y - (800 - s.H) / 2f) < 0.5f, "panel opens centred");

        // Toggle row flips its bit and stays open.
        Row tgl = Find(s, SetAct.ToggleBit);
        uint before = win.Toggles;
        Reveal(s, tgl);
        check.Expect(s.MouseDown(s.Panel.List.X + 5, s.Panel.RowTop(tgl) + 2, win), "toggle click consumed");
        check.Expect(s.Open && win.Toggles == (before ^ tgl.Bit), "toggle flips its bit, stays open");
        s.MouseUp();

        // Animations off hides the duration sliders; on shows them.
        PressRow(s, win, Find(s, SetAct.AnimOn));
        check.Expect(!win.AnimEnabled && Find(s, SetAct.SliderHold) == null, "anim off hides sliders");
        PressRow(s, win, Find(s, SetAct.AnimOn));
        check.Expect(win.AnimEnabled && Find(s, SetAct.SliderHold) != null, "anim on shows sliders");

        // Slider drag sets the window value from the pointer x (full-right -> max), on the drawn row.
        Row hold = Find(s, SetAct.SliderHold);
        Reveal(s, hold);
        s.Panel.TrackRect(hold, s.Panel.RowTop(hold), out float hx0, out float hx1, out float hcy);
        s.MouseDown(hx1 - 0.1f, hcy, win);
        check.Expect(System.Math.Abs(win.HoldSec - 300f) < 0.5f, "hold slider full-right -> max");
        s.MouseMove((hx0 + hx1) / 2f, hcy, win);
        check.Expect(System.Math.Abs(win.HoldSec - 150f) < 2f, "hold slider drag to mid -> ~half");
        check.Expect(System.Math.Abs(Find(s, SetAct.SliderHold).Norm - 0.5f) < 0.02f, "drag updates the drawn row's Norm");
        check.Expect(s.MouseUp(), "slider release consumed");
        float fadeBefore = win.FadeSec;
        PressRow(s, win, Find(s, SetAct.SliderFade));
        check.Expect(win.FadeSec == fadeBefore, "click on a slider label leaves the value untouched");

        // Inline numeric entry: value box -> commit / clamp / cancel / keep-on-empty / click-away.
        void ClickValueBox()
        {
            Row hr = Find(s, SetAct.SliderHold);
            Reveal(s, hr);
            s.Panel.ValueRect(hr, s.Panel.RowTop(hr), out float vx0, out float vx1, out float vy0, out float vy1);
            s.MouseDown((vx0 + vx1) / 2f, (vy0 + vy1) / 2f, win);
        }
        ClickValueBox();
        check.Expect(s.Editing, "clicking a value box starts a field edit");
        s.EditInsert("42"); s.CommitEdit(win);
        check.Expect(!s.Editing && System.Math.Abs(win.HoldSec - 42f) < 0.01f && Find(s, SetAct.SliderHold).Value == "42", "typed value commits and the value text follows");
        ClickValueBox(); s.EditInsert("999"); s.CommitEdit(win);
        check.Expect(System.Math.Abs(win.HoldSec - 300f) < 0.01f, "typed value clamps to max");
        ClickValueBox(); s.EditInsert("10"); s.CancelEdit();
        check.Expect(!s.Editing && System.Math.Abs(win.HoldSec - 300f) < 0.01f, "cancel leaves the value untouched");
        ClickValueBox(); s.CommitEdit(win);
        check.Expect(System.Math.Abs(win.HoldSec - 300f) < 0.01f, "empty commit keeps the old value");
        ClickValueBox(); s.EditInsert("77");
        PressRow(s, win, Find(s, SetAct.AnimOn));
        check.Expect(!s.Editing && System.Math.Abs(win.HoldSec - 77f) < 0.01f, "click-away commits the edit");
        PressRow(s, win, Find(s, SetAct.AnimOn));

        // Dropdown rows: the box opens the list; picking an item sets the enum by index.
        Row alR = Find(s, SetAct.CycleAlign);
        check.Expect(alR.Ctl == KnobKind.Dropdown && alR.Choices!.Length == 4 && alR.Selected == (int)win.Align, "alignment is a 4-choice dropdown seeded from the window");
        PressBox(s, win, alR);
        check.Expect(s.Panel.Drop.Open && win.Align == TextAlign.Left, "pressing the box opens the list without changing the value");
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)TextAlign.Center) + 2, win); s.MouseUp();
        check.Expect(!s.Panel.Drop.Open && win.Align == TextAlign.Center, "picking Center sets the alignment and closes the list");
        check.Expect(Find(s, SetAct.CycleAlign).Selected == (int)TextAlign.Center, "the relaid row shows the pick");
        PressBox(s, win, Find(s, SetAct.CycleFmt));
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)TsFormat.Hour12) + 2, win); s.MouseUp();
        check.Expect(win.TimeFormat == TsFormat.Hour12, "time-format pick sets the enum");

        // Font rows: the per-window size ladder (UI default + every point) and the typeface list; a pick raises
        // FontChanged for the shell and writes the window, never a knob.
        Row fs = Find(s, SetAct.SliderFont);
        check.Expect(fs.Ctl == KnobKind.Dropdown && fs.Choices!.Length == TextRig.MaxFontPx - TextRig.MinFontPx + 2 && fs.Selected == 0 && win.FontPx == 0,
          "text size is a dropdown over UI default + the whole point ladder, at UI default");
        PressBox(s, win, fs);
        check.Expect(s.Panel.Drop.Open && s.Panel.Drop.List.Overflows && s.Panel.Drop.Searching, "the ladder scrolls and searches");
        s.Panel.Drop.List.Offset = 0f;
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop(3) + 2, win); s.MouseUp();
        check.Expect(!s.Panel.Drop.Open && win.FontPx == TextRig.MinFontPx + 2 && s.FontChanged, "picking the third size sets MinFontPx + 2 and flags the shell");
        check.Expect(Find(s, SetAct.SliderFont).Selected == 3, "the relaid row shows the size");
        s.FontChanged = false;
        PressBox(s, win, Find(s, SetAct.SliderFont));
        s.Panel.Drop.List.Offset = 0f;
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop(0) + 2, win); s.MouseUp();
        check.Expect(win.FontPx == 0 && s.FontChanged, "UI default sets 0");
        s.FontChanged = false;
        Row tf = Find(s, SetAct.Typeface);
        check.Expect(tf.Choices == Typefaces && tf.Selected == 0 && win.Typeface == "", "typeface row: UI default at an unset window");
        PressBox(s, win, tf);
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop(2) + 2, win); s.MouseUp();
        check.Expect(win.Typeface == "DejaVu Serif" && s.FontChanged && Find(s, SetAct.Typeface).Selected == 2, "a typeface pick writes the name and flags the shell");
        s.FontChanged = false;
        win.Typeface = "nosuch";
        s.Layout(win);
        check.Expect(Find(s, SetAct.Typeface).Selected == 0, "an unknown typeface shows as UI default");
        win.Typeface = "";

        // Tell auto-open: the pick is handed to the shell AND shows on the relaid row at once (the knob is set later).
        Row tellOpen = Find(s, SetAct.CycleTellOpen);
        check.Expect(tellOpen.Selected == ChatKnobs.TellOpen.V, "tell auto-open seeds from the knob");
        PressBox(s, win, tellOpen);
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop(2) + 2, win); s.MouseUp();
        check.Expect(s.TellOpenPicked == 2 && Find(s, SetAct.CycleTellOpen).Selected == 2, "a pick is handed to the shell and the row shows it before the knob moves");
        s.TellOpenPicked = -1;

        // An outside press closes the list and reaches nothing beneath.
        PressBox(s, win, Find(s, SetAct.CycleTellOpen));
        check.Expect(s.Panel.Drop.Open, "tell auto-open list opened");
        Row outl = Find(s, SetAct.OutlineOn);
        bool outlBefore = win.Outline;
        PressRow(s, win, outl);
        check.Expect(!s.Panel.Drop.Open && win.Outline == outlBefore && s.TellOpenPicked == -1, "outside press closes the list, toggles nothing");
        check.Expect(outl.Help.Length > 0 && outl.H > tgl.H, "help rows wrap their help under the label; a row without help is compact");

        // An alpha colour (Background) expands to 4 channels; the opaque Text colour to 3 (no dead A slider).
        PressRow(s, win, Find(s, SetAct.ColorSwatch, ChatColor.Background));
        check.Expect(s.Rows.FindAll(r => r.Act == SetAct.ColorChannel).Count == 4, "alpha swatch expands to 4 channel rows");
        PressRow(s, win, Find(s, SetAct.ColorSwatch, ChatColor.Background));
        PressRow(s, win, Find(s, SetAct.ColorSwatch, ChatColor.Text));
        check.Expect(s.Rows.FindAll(r => r.Act == SetAct.ColorChannel).Count == 3, "opaque (Text) swatch expands to 3 channel rows");
        Row rCh = s.Rows.Find(r => r.Act == SetAct.ColorChannel && r.Channel == 0)!;
        Reveal(s, rCh);
        s.Panel.TrackRect(rCh, s.Panel.RowTop(rCh), out float cx0, out float _, out float ccy);
        s.MouseDown(cx0, ccy, win);
        check.Expect(win.Colors.TryGetValue(ChatColor.Text, out uint tv) && ((tv >> 24) & 0xFF) == 0, "R channel full-left -> 0");
        s.MouseUp();

        // Close button, click-off.
        Open(s, win);
        s.MouseDown(s.X + s.W - 1.2f * s.Panel.Em - 2, s.Y + 2, win);
        check.Expect(!s.Open, "close-X closes");
        Open(s, win);
        check.Expect(s.MouseDown(s.X + 5, s.Y + s.H + 40, win) && !s.Open && s.MouseUp(), "click-off closes and owns the press (the shell saves on the release)");
        s.Reset();

        // ---- Tab editing (#39) as categories ----
        Open(s, win);
        check.Expect(win.Tabs.Count == 1, "pre-tab: one default tab");
        PressCat(s, win, 2);   // Add new tab...
        check.Expect(win.Tabs.Count == 2 && win.Tabs[1].Label == "All" && win.Tabs[1].ChannelMask == 0xffffffff, "add creates an All-channel tab");
        check.Expect(s.Panel.Category == 2 && s.CategoryTab == 1 && s.Panel.Categories.Length == 4 && s.Panel.Categories[3] == AddTabCategory,
          "the new tab's category is selected; add moved down");
        check.Expect(s.Rows.FindAll(r => r.Act == SetAct.TabChannel && r.Key == "1").Count == FilterChannel.Editable.Length, "the tab category shows the channel toggles");
        check.Expect(Find(s, SetAct.ToggleBit) == null, "...and none of the window rows");

        // Channel toggle: flip the Talk bit (0x001) and verify XOR preserves other bits.
        uint maskBefore = win.Tabs[1].ChannelMask;
        PressRow(s, win, s.Rows.Find(r => r.Act == SetAct.TabChannel && r.Key == "1" && r.Bit == 0x001)!);
        check.Expect(win.Tabs[1].ChannelMask == (maskBefore ^ 0x001), "channel toggle XORs exactly its bit");
        check.Expect((win.Tabs[1].ChannelMask & ~0x001u) == (maskBefore & ~0x001u), "other bits unchanged");

        // Label edit: press the label's box (seeded with the name), retype, commit; the category entry follows.
        PressRow(s, win, Find(s, SetAct.TabLabel, "1"));
        check.Expect(!s.Editing, "a press beside a tab label's box edits nothing");
        PressBox(s, win, Find(s, SetAct.TabLabel, "1"));
        check.Expect(s.Editing && s.EditText == "All", "pressing a tab label's box starts the edit seeded with the name");
        for (int i = 0; i < 3; i++) s.EditBackspace();
        s.EditInsert("Custom"); s.CommitEdit(win);
        check.Expect(win.Tabs[1].Label == "Custom" && Find(s, SetAct.TabLabel, "1").Value == "Custom" && s.Panel.Categories[2] == "Custom",
          "label edit commits the typed text and renames the category");
        PressBox(s, win, Find(s, SetAct.TabLabel, "1"));
        for (int i = 0; i < 6; i++) s.EditBackspace();
        s.CommitEdit(win);
        check.Expect(win.Tabs[1].Label == "Custom" && !s.Editing, "empty label commit keeps old label");

        check.Expect(s.Rows.Find(r => r.Ctl == KnobKind.Text && r.Act != SetAct.TabLabel) == null, "the tell pin is not a setting (no pin row)");

        // The header's Copy this window hands the shell a request.
        s.Panel.HeaderButtonRect(out float cbx0, out float cbx1, out float cby0, out float cby1);
        s.MouseDown((cbx0 + cbx1) / 2f, (cby0 + cby1) / 2f, win); s.MouseUp();
        check.Expect(s.CopyWindowRequested && s.Open, "Copy this window requests a copy and stays open");
        s.CopyWindowRequested = false;

        // Close this window: Main has no row; a closeable window needs two presses, any other press disarms.
        PressCat(s, win, 0);
        check.Expect(Find(s, SetAct.CloseWindow) == null, "Main has no close row");
        win.Closeable = true; s.Layout(win);
        Row? closeRow = Find(s, SetAct.CloseWindow);
        check.Expect(closeRow != null && closeRow.Text == "Close this window", "closeable window gets the close row");
        PressBox(s, win, closeRow!);
        check.Expect(s.CloseArmed && !s.CloseWindowRequested && Find(s, SetAct.CloseWindow).Text == "Click again to confirm", "first press arms");
        PressRow(s, win, Find(s, SetAct.ToggleBit));
        check.Expect(!s.CloseArmed && Find(s, SetAct.CloseWindow).Text == "Close this window", "another press disarms");
        PressBox(s, win, Find(s, SetAct.CloseWindow)); PressBox(s, win, Find(s, SetAct.CloseWindow));
        check.Expect(s.CloseWindowRequested, "second press requests the close");
        s.Reset(); win.Closeable = false; Open(s, win);

        // Switching categories: the window entry, back to the tab.
        PressCat(s, win, 0);
        check.Expect(s.Panel.Category == 0 && Find(s, SetAct.ToggleBit) != null, "the window entry shows the window rows");
        PressCat(s, win, 1);
        check.Expect(s.CategoryTab == 0 && Find(s, SetAct.TabLabel, "0") != null, "the first tab's entry edits tab 0");

        // Remove tab: present when >1 tab, removes it and returns to the window category.
        PressCat(s, win, 2);
        PressBox(s, win, Find(s, SetAct.TabRemove, "1"));
        check.Expect(win.Tabs.Count == 1 && s.Panel.Category == 0 && s.Panel.Categories.Length == 3, "remove tab removes it; back to the window category");
        PressCat(s, win, 1);
        check.Expect(Find(s, SetAct.TabRemove) == null, "remove button absent at 1 tab");

        // Ephemeral tabs are entirely hidden from settings.
        win.AddTab(new Tab("Tell:Bob", 0x002));
        check.Expect(win.Tabs[^1].Ephemeral, "pre: Tell-only tab is ephemeral");
        Open(s, win);
        check.Expect(s.Panel.Categories.Length == 3, "ephemeral tab: no category");
        win.RemoveTab(1);
        s.Reset();

        // ---- Copy from (#41) ----
        Open(s, win, winIndex: 0, winCount: 2);
        Row? copyRow = s.Rows.Find(r => r.Act == SetAct.CopyFrom);
        check.Expect(copyRow != null && copyRow.Text == "Copy from Window 2", "copy row shown for the other window");
        s.MouseMove(s.Panel.List.X + 5, s.Panel.List.Y + 5, win);
        WheelToEnd(s);
        PressBox(s, win, copyRow!);
        check.Expect(s.CopyRequested == 1, "CopyRequested set to the source window index");
        s.CopyRequested = -1;
        Open(s, win, winIndex: 0, winCount: 1);
        check.Expect(s.Rows.Find(r => r.Act == SetAct.CopyFrom) == null, "no copy row when only one window");
        s.Reset();

        // ---- BgMode dropdown + conditional bg2 swatch ----
        check.Expect(win.BgMode == BgMode.Solid, "pre: BgMode starts Solid");
        Open(s, win);
        Row bgmRow = Find(s, SetAct.CycleBgMode);
        check.Expect(bgmRow.Ctl == KnobKind.Dropdown && bgmRow.Choices!.Length == 4, "BgMode is a 4-choice dropdown");
        check.Expect(Find(s, SetAct.SliderBgAngle) == null && Find(s, SetAct.SliderBgStart) == null, "gradient rows hidden when Solid");
        check.Expect(Find(s, SetAct.ColorSwatch, ChatColor.Background2) == null, "bg2 swatch hidden when Solid");
        PressBox(s, win, bgmRow);
        check.Expect(s.Panel.Drop.Open && !s.Panel.Drop.List.Overflows, "4 shapes: the list fits");
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)BgMode.Linear) + 2, win); s.MouseUp();
        check.Expect(win.BgMode == BgMode.Linear, "picking Linear sets Linear");
        check.Expect(Find(s, SetAct.ColorSwatch, ChatColor.Background2) != null, "bg2 swatch shown when non-Solid");
        check.Expect(Find(s, SetAct.SliderBgAngle) != null && Find(s, SetAct.SliderBgEnd) != null, "Linear shows direction + stops");
        Row endRow = Find(s, SetAct.SliderBgEnd);
        check.Expect(System.Math.Abs(endRow.Norm - 1f) < 1e-4f, "End seeds from BgEnd (100%)");
        s.Panel.ApplyValue!(endRow, 40f);
        check.Expect(System.Math.Abs(win.BgEnd - 0.4f) < 1e-4f, "End slider writes BgEnd as a fraction");
        PressBox(s, win, Find(s, SetAct.CycleBgMode));
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)BgMode.Radial) + 2, win); s.MouseUp();
        check.Expect(win.BgMode == BgMode.Radial && Find(s, SetAct.SliderBgAngle) == null && Find(s, SetAct.SliderBgStart) != null, "Radial hides direction, keeps stops");
        PressBox(s, win, Find(s, SetAct.CycleBgMode));
        s.Panel.Drop.List.Offset = 0f;
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)BgMode.Solid) + 2, win); s.MouseUp();
        check.Expect(win.BgMode == BgMode.Solid, "picking Solid returns to Solid");
        s.Reset();

        // ---- the rows overflow the column and scroll; a hit past the fold is offset-correct ----
        Open(s, win);
        check.Expect(s.Panel.List.Overflows, "the window rows overflow the column");
        Row last = s.Rows[^1];
        check.Expect(s.Panel.RowTop(last) >= s.Panel.List.Y + s.Panel.List.H, "pre: the last row lies past the box");
        s.MouseMove(s.Panel.List.X + 5, s.Panel.List.Y + 5, win);
        WheelToEnd(s);
        float lastTop = s.Panel.RowTop(s.Rows[^1]);
        check.Expect(lastTop >= s.Panel.List.Y && lastTop + s.Rows[^1].H <= s.Panel.List.Y + s.Panel.List.H + 0.01f, "wheeling to the end brings the last row into the box");
        Row bgSw = Find(s, SetAct.ColorSwatch, ChatColor.Background);
        PressRow(s, win, bgSw);
        check.Expect(s.Rows.FindAll(r => r.Act == SetAct.ColorChannel).Count == 4, "a scrolled-to swatch expands (offset-correct hit)");
        {
            var vb = new VertexBatch(body.SolidU, body.SolidV);
            s.Panel.Emit(vb);
            long alloc0 = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { vb.Clear(); s.Panel.Emit(vb); }
            long delta = System.GC.GetAllocatedBytesForCurrentThread() - alloc0;
            check.Expect(delta == 0, $"100 draws (expanded, scrolled) allocate nothing ({delta} bytes)");
        }
        s.Reset();

        // ---- 12 tabs: the category column overflows and scrolls; a scrolled-to entry picks ----
        var tabbed = new ChatWindow(100, 100, 400, 300, new[] { new Tab("All", 0xffffffff) }) { AnimEnabled = false };
        for (int i = 1; i < 12; i++) tabbed.AddTab(new Tab(new string('W', 32), 0xffffffff));
        s.OpenAt(tabbed, 1280, 400, 20f, body, small); s.MouseUp();
        check.Expect(s.Panel.Categories.Length == 14 && s.Panel.Cats.Overflows, "12 tabs in a 400 px viewport: the categories overflow");
        s.MouseMove(s.Panel.Cats.X + 5, s.Panel.Cats.Y + 5, tabbed);
        WheelToEnd(s);
        float navTop = s.Panel.Cats.ToScreen(0.4f * s.Panel.Em + 12 * (body.LineHeight + 1.2f * s.Panel.Em));
        check.Expect(navTop >= s.Panel.Cats.Y && navTop < s.Panel.Cats.Y + s.Panel.Cats.H, "wheeling the column brings the last tab's entry into view");
        s.MouseDown(s.Panel.Cats.X + 5, navTop + 2, tabbed); s.MouseUp();
        check.Expect(s.CategoryTab == 11 && Find(s, SetAct.TabLabel, "11").Value.Length == 32, "a scrolled-to entry picks its tab (offset-correct hit)");
        s.Reset();

        Typefaces = realFaces;
        log.Info(check.Ok ? "SettingsWindow.SelfCheck: passed" : "SettingsWindow.SelfCheck: FAILED");
    }
}
