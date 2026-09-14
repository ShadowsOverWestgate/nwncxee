using System.Collections.Generic;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Chat.Core;

/// <summary>
/// The chat settings window: the chat-side consumer of the shared <see cref="SettingsPanel"/>. It owns the
/// content only -- which rows exist (checkboxes, dropdowns, sliders, expandable RGBA colour pickers, tab
/// editing, "copy from window") and what each press does to the <see cref="ChatWindow"/>. The two shared knobs it
/// still fronts (<c>chat.tellopen</c>, <c>ui.font</c>) are seeded from the knobs and a pick is handed back to the
/// shell (<see cref="TellOpenPicked"/> / <see cref="FontPicked"/>, like <see cref="CopyRequested"/>), so the pure
/// core never sets a live knob.
/// The panel owns everything else: position, drag-by-title, the close-button hit, the metrics, the row list,
/// hit-test-to-row, the scroll column, the dropdown list, the tooltip, the inline text buffer, and the draw.
/// Pure -- pixels in, hit-tests + value edits out, no native refs -- so it self-checks off-device.
/// </summary>
public sealed class SettingsWindow
{
    /// <summary>Design-px panel width. Scales with the font/UI so the label|control split and the value boxes
    /// keep their proportions -- labels and values don't run past their column when the font grows. Sized so a
    /// tab editor text box spans <see cref="TabLabelW"/> (#572).</summary>
    private const float PanelW = 400f;
    /// <summary>Design px a full-length tab label (the 32-rune edit cap at the design font) needs in its text
    /// box; the box must never clip one.</summary>
    private const float TabLabelW = 260f;

    /// <summary>The shared panel this window's rows are drawn and hit-tested by (the renderer calls
    /// <see cref="SettingsPanel.Emit"/> on it).</summary>
    public readonly SettingsPanel Panel;

    // The model the panel's callbacks edit. Stashed at each public entry point because the shell passes the live
    // window per call; a null window means "no model" and the callbacks no-op (the shell's focused window can be
    // gone by the time a key commits an edit).
    private ChatWindow? _win;

    private string _expanded = "";// colour key whose RGBA channels are shown, or ""
    private int _expandedTab = -1; // tab index whose channels are shown, or -1

    // Dropdown choice tables, in enum order (the picked index IS the enum value). Static: a row points at
    // them, never copies.
    private static readonly string[] TellOpenNames = { "Off", "New tab", "New window" };
    private static readonly string[] AlignNames = { "Left", "Right", "Center", "Justify" };
    private static readonly string[] FormatNames = { "HH:mm", "HH:mm:ss", "h:mm tt" };
    private static readonly string[] SourceNames = { "Local", "Game" };
    private static readonly string[] BgModeNames =
        { "Solid", "Grad V", "Grad H", "Grad D", "Stripe H", "Stripe V", "Box", "Radial", "Conic", "Conic 4" };
    /// <summary>The font-size ladder, every point from <see cref="TextRig.MinFontPx"/> to <see cref="TextRig.MaxFontPx"/>
    /// (a layout-affecting knob is a dropdown, never a slider -- ADR 0025).</summary>
    public static readonly string[] FontSizes = BuildFontSizes();
    private static string[] BuildFontSizes()
    {
        int lo = TextRig.MinFontPx, hi = TextRig.MaxFontPx;
        var a = new string[hi - lo + 1];
        for (int i = 0; i < a.Length; i++) a[i] = (lo + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return a;
    }

    public SettingsWindow()
    {
        Panel = new SettingsPanel(PanelW)
        {
            Relayout = () => { if (_win != null) Layout(_win); },
            RowPressed = OnRowPressed,
            ApplyValue = OnApplyValue,
            CommitLabel = OnCommitLabel,
        };
    }

    public float X { get => Panel.X; set => Panel.X = value; }
    public float Y { get => Panel.Y; set => Panel.Y = value; }
    public float W => Panel.W;
    public float H => Panel.H;
    public bool Open { get => Panel.Open; set => Panel.Open = value; }
    public List<SetRow> Rows => Panel.Rows;

    /// <summary>Number of chat windows (snapshot), used to show "Copy from Window N" rows. Set by the shell
    /// before <see cref="Layout"/> (the panel is modal, so window count can't change while it's open).</summary>
    public int WindowCount;
    /// <summary>Index of the window this panel is editing (0 = Main). Set by the shell on open.</summary>
    public int WindowIndex;

    /// <summary>Index of the source window for a pending "Copy from" action, or -1. The shell reads + clears
    /// this after MouseDown to perform the actual copy (it owns the window list, which Layout doesn't).</summary>
    public int CopyRequested = -1;

    /// <summary>A "Tell auto-open" pick (the <see cref="TellAutoOpen"/> value) or a "Font size" pick (the point
    /// size), or -1. The shell reads + clears these after MouseDown and sets the knobs (<c>chat.tellopen</c>,
    /// <c>ui.font</c>); the core stays pure and the SelfCheck sets nothing live.</summary>
    public int TellOpenPicked = -1, FontPicked = -1;

    /// <summary>Set true when the user just turned TellsHere on. The shell reads + clears this to enforce
    /// exclusivity (clear TellsHere on all other windows — the panel only sees one window).</summary>
    public bool TellsHereChanged;

    /// <summary>Rebuild the row list + panel height from the live window + config, publishing a fresh list.
    /// Input-thread only (open + each settings interaction); the ingest-thread Rebuild never calls it, so the
    /// list it builds is single-writer and only ever read cross-thread as a whole snapshot.</summary>
    public void Layout(ChatWindow win)
    {
        _win = win;
        var rows = new List<SetRow>();
        float y = Panel.ContentTop;

        // Render toggles (the window's option bits).
        foreach (var (bit, label) in ChatWindow.Options)
            y = AddToggle(rows, y, SetAct.ToggleBit, label, (win.Toggles & bit) != 0, bit);

        y = AddToggle(rows, y, SetAct.InputTop, "Input on top", win.InputOnTop, 0,
                      "The input line pops out above the window instead of below it.");
        y = AddToggle(rows, y, SetAct.AutoHideHdr, "Auto-hide header", win.AutoHideHeader, 0,
                      "The tab bar hides until the pointer is over the window.");
        y = AddToggle(rows, y, SetAct.LockWin, "Lock window", win.Locked, 0,
                      "No moving, resizing or tab dragging on this window.");
        y = AddToggle(rows, y, SetAct.TellsHere, "Tells here", win.TellsHere, 0,
                      "Tells land in this window. Only one window can hold this.");
        y = AddDropdown(rows, y, SetAct.CycleTellOpen, "Tell auto-open", TellOpenNames, ChatKnobs.TellOpen.V,
                        "Where a tell from someone new lands: nowhere special, a new tab, or its own window.");
        y = AddToggle(rows, y, SetAct.AnimOn, "Animations", win.AnimEnabled, 0,
                      "Lines fade in and out; the window eases when scrolled.");
        if (win.AnimEnabled)
        {
            y = AddSlider(rows, y, SetAct.SliderHold, "Hold (s)", win.HoldSec, 0f, 300f);
            y = AddSlider(rows, y, SetAct.SliderFade, "Fade (s)", win.FadeSec, 0f, 30f);
            y = AddSlider(rows, y, SetAct.SliderTau, "Ease (ms)", win.TauMs, 10f, 500f);
        }

        y = AddDropdown(rows, y, SetAct.SliderFont, "Font size", FontSizes, FontIndex(UiKnobs.Font.V),
                        "Text size in every CX window.");
        y = AddToggle(rows, y, SetAct.OutlineOn, "Outline", win.Outline, 0,
                      "A dark halo around the text for readability over bright scenes.");

        y = AddDropdown(rows, y, SetAct.CycleAlign, "Alignment", AlignNames, (int)win.Align, "");
        y = AddDropdown(rows, y, SetAct.CycleFmt, "Time format", FormatNames, (int)win.TimeFormat, "");
        y = AddDropdown(rows, y, SetAct.CycleSrc, "Time source", SourceNames, (int)win.TimeSource,
                        "Local: your clock. Game: the in-game world clock.");

        foreach (var (key, label, alpha) in ChatColor.Editable)
        {
            uint rgba = ColorOf(win, key);
            y = AddSwatch(rows, y, label, key, rgba);
            if (_expanded == key)
                for (int ch = 0; ch < (alpha ? 4 : 3); ch++)
                    y = AddChannel(rows, y, key, ch, ChannelByte(rgba, ch));
            if (key == ChatColor.Background)
            {
                y = AddDropdown(rows, y, SetAct.CycleBgMode, "Bg style", BgModeNames, (int)win.BgMode, "");
                if (win.BgMode != BgMode.Solid)
                {
                    uint bg2 = ColorOf(win, ChatColor.Background2);
                    y = AddSwatch(rows, y, "Background 2", ChatColor.Background2, bg2);
                    if (_expanded == ChatColor.Background2)
                        for (int ch = 0; ch < 4; ch++)
                            y = AddChannel(rows, y, ChatColor.Background2, ch, ChannelByte(bg2, ch));
                }
            }
        }

        // Tab section (#39): one header per tab, expandable to label + channel toggles + remove.
        for (int ti = 0; ti < win.Tabs.Count; ti++)
        {
            Tab tab = win.Tabs[ti];
            if (tab.Ephemeral) continue;
            string tk = ti.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rows.Add(new SetRow { Kind = SetKind.Text, Act = SetAct.TabExpand,
                Label = $"Tab {ti + 1}", Value = tab.Label, Top = y, Height = RowH, Key = tk });
            y += RowH;
            if (_expandedTab == ti)
            {
                rows.Add(new SetRow { Kind = SetKind.Text, Act = SetAct.TabLabel,
                    Label = "  Label", Value = tab.Label, Top = y, Height = RowH, Key = tk });
                y += RowH;
                rows.Add(new SetRow { Kind = SetKind.Text, Act = SetAct.TabPin,
                    Label = "  Tell pin", Value = tab.PinnedName.Length > 0 ? tab.PinnedName : "(none)",
                    Top = y, Height = RowH, Key = tk });
                y += RowH;
                foreach (var (bit, chLabel) in FilterChannel.Editable)
                {
                    rows.Add(new SetRow { Kind = SetKind.Toggle, Act = SetAct.TabChannel,
                        Label = $"  {chLabel}", On = (tab.ChannelMask & bit) != 0,
                        Bit = bit, Top = y, Height = RowH, Key = tk });
                    y += RowH;
                }
                if (win.Tabs.Count > 1)
                {
                    rows.Add(new SetRow { Kind = SetKind.Button, Act = SetAct.TabRemove,
                        Label = "  Remove tab", Top = y, Height = RowH, Key = tk });
                    y += RowH;
                }
            }
        }
        rows.Add(new SetRow { Kind = SetKind.Button, Act = SetAct.TabAdd,
            Label = "+ Add tab", Top = y, Height = RowH });
        y += RowH;

        // Copy from another window (hidden when only one window exists).
        for (int wi = 0; wi < WindowCount; wi++)
        {
            if (wi == WindowIndex) continue;
            rows.Add(new SetRow { Kind = SetKind.Button, Act = SetAct.CopyFrom,
                Label = $"Copy from Window {wi + 1}", Top = y, Height = RowH,
                Key = wi.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            y += RowH;
        }

        Panel.Publish(rows, y);
    }

    private static float RowH => SettingsPanel.RowH;

    /// <summary>Resolve a per-window colour to its current RGBA8 (override or default). Set by the shell so
    /// the pure core doesn't reach into the renderer for defaults.</summary>
    public static System.Func<ChatWindow, string, uint> ColorResolver = (win, key) =>
        win.Colors.TryGetValue(key, out uint v) ? v : 0xffffffffu;
    private static uint ColorOf(ChatWindow win, string key) => ColorResolver(win, key);

    private static float AddToggle(List<SetRow> rows, float y, SetAct act, string label, bool on, uint bit, string help = "")
    { rows.Add(new SetRow { Kind = SetKind.Toggle, Act = act, Label = label, Top = y, Height = RowH, On = on, Bit = bit, Help = help }); return y + RowH; }

    private static float AddDropdown(List<SetRow> rows, float y, SetAct act, string label, string[] choices, int selected, string help)
    {
        rows.Add(new SetRow { Kind = SetKind.Dropdown, Act = act, Label = label, Top = y, Height = RowH,
                              Choices = choices, Selected = selected, Help = help });
        return y + RowH;
    }

    /// <summary>Index of a font px in <see cref="FontSizes"/> (nearest point, clamped to the ladder).</summary>
    private static int FontIndex(int px)
    {
        int i = px - TextRig.MinFontPx;
        return i < 0 ? 0 : i >= FontSizes.Length ? FontSizes.Length - 1 : i;
    }

    private static float AddSlider(List<SetRow> rows, float y, SetAct act, string label, float value, float min, float max)
    {
        float norm = max > min ? (value - min) / (max - min) : 0f;
        rows.Add(new SetRow { Kind = SetKind.Slider, Act = act, Label = label, Top = y, Height = RowH,
                              Norm = Clamp01(norm), Min = min, Max = max });
        return y + RowH;
    }

    private static float AddSwatch(List<SetRow> rows, float y, string label, string key, uint rgba)
    { rows.Add(new SetRow { Kind = SetKind.Swatch, Act = SetAct.ColorSwatch, Label = label, Top = y, Height = RowH, Key = key, Rgba = rgba }); return y + RowH; }

    private static float AddChannel(List<SetRow> rows, float y, string key, int ch, int val)
    {
        rows.Add(new SetRow { Kind = SetKind.Slider, Act = SetAct.ColorChannel, Label = "RGBA"[ch].ToString(),
                              Top = y, Height = RowH, Key = key, Channel = ch, Norm = val / 255f, Min = 0f, Max = 255f });
        return y + RowH;
    }

    /// <summary>Open the panel centred in a <paramref name="vpW"/>×<paramref name="vpH"/> viewport.
    /// <paramref name="winIndex"/> and <paramref name="winCount"/> drive the "Copy from" rows.</summary>
    public void OpenAt(ChatWindow win, float vpW, float vpH,
                       int winIndex = 0, int winCount = 1)
    {
        Panel.BeginOpen();
        Panel.ViewportW = vpW > 0f ? vpW : float.MaxValue;
        Panel.ViewportH = vpH > 0f ? vpH : float.MaxValue;
        WindowIndex = winIndex; WindowCount = winCount;
        Layout(win);
        if (vpW > 0f && vpH > 0f) { X = (vpW - W) / 2f; Y = (vpH - H) / 2f; }
        else { X = win.X + (win.W - W) / 2f; Y = win.Y + ChatWindow.TabBarH + 6f; }
        if (X < 0f) X = 0f;
        if (Y < 0f) Y = 0f;
        Layout(win);
    }

    public bool OverClose(float mx, float my) => Panel.OverClose(mx, my);

    internal bool Contains(float mx, float my) => Panel.Contains(mx, my);

    public void SliderTrack(SetRow r, out float x0, out float x1, out float cy) => Panel.SliderTrack(r, out x0, out x1, out cy);

    public void ValueBox(SetRow r, out float x0, out float x1, out float y0, out float y1)
        => Panel.ValueBox(r, out x0, out x1, out y0, out y1);

    /// <summary>Pointer press while open. Always consumed. Mutates the window/config per the hit row; a slider
    /// hit starts a drag (see <see cref="MouseMove"/>). Close button or a click-off closes.</summary>
    public bool MouseDown(float mx, float my, ChatWindow win)
    {
        _win = win;
        return Panel.MouseDown(mx, my);
    }

    /// <summary>A press on a non-slider row: mutate the window/config. Slider rows never reach here -- the panel
    /// owns the track drag and the value box. A dropdown row arrives after a pick, with <see cref="SetRow.Selected"/>
    /// already the chosen index (= the enum value).</summary>
    private void OnRowPressed(SetRow row)
    {
        ChatWindow? win = _win;
        if (win == null) return;
        switch (row.Act)
        {
            case SetAct.ToggleBit: win.Toggles ^= row.Bit; break;
            case SetAct.InputTop: win.InputOnTop = !win.InputOnTop; break;
            case SetAct.AutoHideHdr: win.AutoHideHeader = !win.AutoHideHeader; break;
            case SetAct.LockWin: win.Locked = !win.Locked; break;
            case SetAct.TellsHere: win.TellsHere = !win.TellsHere; TellsHereChanged = win.TellsHere; break;
            case SetAct.CycleTellOpen: TellOpenPicked = row.Selected; break;
            case SetAct.AnimOn: win.AnimEnabled = !win.AnimEnabled; break;
            case SetAct.OutlineOn: win.Outline = !win.Outline; break;
            case SetAct.CycleFmt: win.TimeFormat = (TsFormat)row.Selected; break;
            case SetAct.CycleSrc: win.TimeSource = (TsSource)row.Selected; break;
            case SetAct.CycleAlign: win.Align = (TextAlign)row.Selected; break;
            case SetAct.CycleBgMode: win.BgMode = (BgMode)row.Selected; break;
            case SetAct.SliderFont: FontPicked = TextRig.MinFontPx + row.Selected; break;
            case SetAct.ColorSwatch: _expanded = _expanded == row.Key ? "" : row.Key; break;
            case SetAct.TabExpand:
            {
                int ti = ParseTabKey(row.Key);
                _expandedTab = _expandedTab == ti ? -1 : ti;
                break;
            }
            case SetAct.TabAdd:
                win.AddTab(new Tab("All", 0xffffffff));
                _expandedTab = win.Tabs.Count - 1;
                break;
            case SetAct.TabRemove:
            {
                int ti = ParseTabKey(row.Key);
                if (win.RemoveTab(ti))
                    _expandedTab = -1;
                break;
            }
            case SetAct.TabLabel:
            case SetAct.TabPin:
            {
                Panel.BeginLabelEdit(row);
                break;
            }
            case SetAct.TabChannel:
            {
                int ti = ParseTabKey(row.Key);
                if ((uint)ti < (uint)win.Tabs.Count)
                {
                    win.Tabs[ti].ChannelMask ^= row.Bit;

                    if (ti == win.ActiveTab) win.ClampSendChannel();
                }
                break;
            }
            case SetAct.CopyFrom:
            {
                int src = ParseTabKey(row.Key);
                CopyRequested = src;
                break;
            }
        }
    }

    /// <summary>True while the panel is being dragged by its title bar (not a slider grab).</summary>
    public bool Moving => Panel.Moving;

    /// <summary>Pointer motion while a slider or the title bar is grabbed. A title-bar move translates
    /// row positions in place without a full <see cref="Layout"/> (#53).</summary>
    public bool MouseMove(float mx, float my, ChatWindow win)
    {
        _win = win;
        return Panel.MouseMove(mx, my);
    }

    /// <summary>Write a raw value onto the window (per-window settings) or config (font, which is global) for a
    /// slider/channel row and mirror it back onto the row's draw state so the knob/value box track the pointer.</summary>
    private void OnApplyValue(SetRow r, float v)
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
            foreach (SetRow sw in Panel.Rows)
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
            }
        }
    }

    // ---- inline text entry ---------------------------------------------------------------------

    /// <summary>True while a slider value box is being typed into (the shell routes keys/text here).</summary>
    public bool Editing => Panel.Editing;
    /// <summary>Is <paramref name="r"/> the row being text-edited? (the renderer shows the buffer + a caret).</summary>
    public bool EditingRow(SetRow r) => Panel.EditingRow(r);
    /// <summary>The in-progress edit buffer (renderer draws it in the active value box).</summary>
    public string EditText => Panel.EditText;

    private static int ParseTabKey(string key)
        => int.TryParse(key, System.Globalization.NumberStyles.None,
                         System.Globalization.CultureInfo.InvariantCulture, out int i) ? i : -1;

    /// <summary>Append typed characters (the panel filters them by edit mode).</summary>
    public void EditInsert(string s) => Panel.EditInsert(s);

    public void EditBackspace() => Panel.EditBackspace();

    /// <summary>Commit the typed value; the panel routes free text here and parses numeric text itself.</summary>
    public void CommitEdit(ChatWindow? win)
    {
        _win = win;
        Panel.CommitEdit();
    }

    /// <summary>Commit a tab label or tell pin. A label writes the trimmed text to the tab (empty keeps old);
    /// a pin writes it too (empty clears the pin — that's the only unpin affordance).</summary>
    private void OnCommitLabel(SetRow r, string t)
    {
        ChatWindow? win = _win;
        if (r.Act == SetAct.TabPin)
        {
            string trimmed = t.Trim();
            if (win != null)
            {
                int ti = ParseTabKey(r.Key);
                if ((uint)ti < (uint)win.Tabs.Count)
                {
                    win.Tabs[ti].PinnedName = trimmed;

                    r.Value = trimmed.Length > 0 ? trimmed : "(none)";
                }
            }
        }
        else
        {
            string trimmed = t.Trim();
            if (trimmed.Length > 0 && win != null)
            {
                int ti = ParseTabKey(r.Key);
                if ((uint)ti < (uint)win.Tabs.Count)
                {

                    win.Tabs[ti].Label = trimmed;
                    r.Value = trimmed;
                    foreach (SetRow hdr in Panel.Rows)
                        if (hdr.Act == SetAct.TabExpand && hdr.Key == r.Key) hdr.Value = trimmed;
                }
            }
        }
    }

    public void CancelEdit() => Panel.CancelEdit();

    /// <summary>Pointer release. Consumed iff we owned the press. Ends any slide drag.</summary>
    public bool MouseUp() => Panel.MouseUp();

    /// <summary>Wheel notch while open: the open dropdown list, else the rows. True when something scrolled.</summary>
    public bool MouseWheel(int dy) => Panel.MouseWheel(dy);

    /// <summary>Per-frame poll: true once when a hovered (?) marker's dwell elapses (the shell rebuilds).</summary>
    public bool TooltipTick() => Panel.Tip.Tick();

    /// <summary>True while a slider or the panel itself is being dragged.</summary>
    public bool Dragging => Panel.Dragging;

    /// <summary>Force-close at a session boundary, clearing any half-owned press, drag, or field edit.</summary>
    public void Reset() { Panel.Reset(); _expanded = ""; _expandedTab = -1; TellsHereChanged = false; }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    private static int ChannelByte(uint rgba, int ch) => (int)((rgba >> (24 - ch * 8)) & 0xFF);
    private static uint WithChannel(uint rgba, int ch, int val)
    {
        int shift = 24 - ch * 8;
        return (rgba & ~(0xFFu << shift)) | ((uint)(val & 0xFF) << shift);
    }

    /// <summary>Off-device proof of the widget state machine: open/close/ownership, each control type's edit,
    /// and colour-channel expansion.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"SettingsWindow.SelfCheck FAIL: {w}"); } }

        // Resolve colours against a fixed table for the test (the shell wires the real renderer resolver).
        var defs = new Dictionary<string, uint> { [ChatColor.Text] = 0x102030ff };
        ColorResolver = (w, key) => w.Colors.TryGetValue(key, out uint v) ? v : (defs.TryGetValue(key, out uint d) ? d : 0x808080ffu);

        var win = new ChatWindow(100, 100, 400, 300, new[] { new Tab("All", 0xffffffff) });
        var s = new SettingsWindow();

        s.OpenAt(win, 0, 0);
        E(s.Open && s.Rows.Count > 0, "OpenAt opens + lays out rows");
        E(s.MouseUp(), "up after gear-open is consumed (press ownership)");

        // Toggle row 0 flips its bit and stays open.
        s.OpenAt(win, 0, 0); s.MouseUp();
        SetRow tgl = s.Rows.Find(r => r.Act == SetAct.ToggleBit)!;
        uint before = win.Toggles;
        E(s.MouseDown(s.X + 5, tgl.Top + 2, win), "toggle click consumed");
        E(s.Open && win.Toggles == (before ^ tgl.Bit), "toggle flips its bit, stays open");

        // Animations off hides the duration sliders; on shows them.
        SetRow anim = s.Rows.Find(r => r.Act == SetAct.AnimOn)!;
        s.MouseDown(s.X + 5, anim.Top + 2, win);
        E(!win.AnimEnabled && s.Rows.Find(r => r.Act == SetAct.SliderHold) == null, "anim off hides sliders");
        s.MouseDown(s.X + 5, s.Rows.Find(r => r.Act == SetAct.AnimOn)!.Top + 2, win);
        E(win.AnimEnabled && s.Rows.Find(r => r.Act == SetAct.SliderHold) != null, "anim on shows sliders");

        // Slider drag sets the config value from the pointer x (full-right -> max).
        SetRow hold = s.Rows.Find(r => r.Act == SetAct.SliderHold)!;
        s.SliderTrack(hold, out float hx0, out float hx1, out float _);
        s.MouseDown(hx1, hold.Top + 2, win);
        E(System.Math.Abs(win.HoldSec - 300f) < 0.5f, "hold slider full-right -> max");
        s.MouseMove((hx0 + hx1) / 2f, hold.Top + 2, win);
        E(System.Math.Abs(win.HoldSec - 150f) < 2f, "hold slider drag to mid -> ~half");
        // Live-preview regression: the drag must move the row that's actually IN Rows (the drawn knob), not a
        // detached copy -- MouseDown re-published the list, so _grab has to point into the new one.
        E(System.Math.Abs(s.Rows.Find(r => r.Act == SetAct.SliderHold)!.Norm - 0.5f) < 0.02f, "drag updates the drawn row's Norm");
        E(s.MouseUp(), "slider release consumed");

        // Clicking a slider's label (left of the track) is consumed but leaves the value untouched.
        SetRow fade = s.Rows.Find(r => r.Act == SetAct.SliderFade)!;
        float fadeBefore = win.FadeSec;
        s.MouseDown(s.X + 5, fade.Top + 2, win);   // s.X+5 is in the label zone, left of ControlX
        E(win.FadeSec == fadeBefore, "click on a slider label leaves the value untouched");
        s.MouseUp();

        // Inline text entry: click the value box, type an exact number, commit / clamp / cancel / keep-on-empty.
        void ClickValueBox()
        {
            SetRow hr = s.Rows.Find(r => r.Act == SetAct.SliderHold)!;
            s.ValueBox(hr, out float vx0, out float vx1, out float vy0, out float vy1);
            s.MouseDown((vx0 + vx1) / 2f, (vy0 + vy1) / 2f, win);
        }
        ClickValueBox();
        E(s.Editing, "clicking a value box starts a field edit");
        s.EditInsert("4a2");   // letters filtered out -> "42"
        s.CommitEdit(win);
        E(!s.Editing && System.Math.Abs(win.HoldSec - 42f) < 0.01f, "typed value (filtered) commits");
        ClickValueBox(); s.EditInsert("999"); s.CommitEdit(win);   // over max (300) -> clamps
        E(System.Math.Abs(win.HoldSec - 300f) < 0.01f, "typed value clamps to max");
        ClickValueBox(); s.EditInsert("10"); s.CancelEdit();
        E(!s.Editing && System.Math.Abs(win.HoldSec - 300f) < 0.01f, "cancel leaves the value untouched");
        ClickValueBox(); s.CommitEdit(win);   // empty buffer -> unparseable -> keep old value (never zero)
        E(System.Math.Abs(win.HoldSec - 300f) < 0.01f, "empty commit keeps the old value");
        // A press elsewhere while editing commits the in-progress edit (focus loss).
        ClickValueBox(); s.EditInsert("77");
        SetRow animRow = s.Rows.Find(r => r.Act == SetAct.AnimOn)!;
        s.MouseDown(s.X + 5, animRow.Top + 2, win);
        E(!s.Editing && System.Math.Abs(win.HoldSec - 77f) < 0.01f, "click-away commits the edit");
        // restore anim (the click above toggled it off, hiding the sliders)
        s.MouseDown(s.X + 5, s.Rows.Find(r => r.Act == SetAct.AnimOn)!.Top + 2, win);

        // Dropdown rows (#587): a press opens the list; picking an item sets the enum by index.
        SetRow alR = s.Rows.Find(r => r.Act == SetAct.CycleAlign)!;
        E(alR.Kind == SetKind.Dropdown && alR.Choices!.Length == 4 && alR.Selected == (int)win.Align, "alignment is a 4-choice dropdown seeded from the window");
        s.MouseDown(s.X + 5, alR.Top + 2, win); s.MouseUp();
        E(s.Panel.Drop.Open && win.Align == TextAlign.Left, "pressing the row opens the list without changing the value");
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)TextAlign.Center) + 2, win); s.MouseUp();
        E(!s.Panel.Drop.Open && win.Align == TextAlign.Center, "picking Center sets the alignment and closes the list");
        E(s.Rows.Find(r => r.Act == SetAct.CycleAlign)!.Selected == (int)TextAlign.Center, "the relaid row shows the pick");

        SetRow fmt = s.Rows.Find(r => r.Act == SetAct.CycleFmt)!;
        s.MouseDown(s.X + 5, fmt.Top + 2, win); s.MouseUp();
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)TsFormat.Hour12) + 2, win); s.MouseUp();
        E(win.TimeFormat == TsFormat.Hour12, "time-format pick sets the enum");

        // Font size: the ladder dropdown, every point from MinFontPx to MaxFontPx; a pick writes the px.
        SetRow fs = s.Rows.Find(r => r.Act == SetAct.SliderFont)!;
        E(fs.Kind == SetKind.Dropdown && fs.Choices!.Length == TextRig.MaxFontPx - TextRig.MinFontPx + 1,
          "font size is a dropdown over the whole point ladder");
        s.MouseDown(s.X + 5, fs.Top + 2, win); s.MouseUp();
        E(s.Panel.Drop.Open && s.Panel.Drop.List.Overflows, "the font ladder is longer than the list box, so it scrolls");
        s.MouseWheel(1);   // scroll to the top of the ladder
        s.Panel.Drop.List.Offset = 0f;
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop(2) + 2, win); s.MouseUp();
        E(!s.Panel.Drop.Open && s.FontPicked == TextRig.MinFontPx + 2, "picking the third size hands the shell MinFontPx + 2");
        s.FontPicked = -1;

        // An outside press closes the list and reaches nothing beneath.
        SetRow tellOpen = s.Rows.Find(r => r.Act == SetAct.CycleTellOpen)!;
        s.MouseDown(s.X + 5, tellOpen.Top + 2, win); s.MouseUp();
        E(s.Panel.Drop.Open, "tell auto-open list opened");
        SetRow outl = s.Rows.Find(r => r.Act == SetAct.OutlineOn)!;
        bool outlBefore = win.Outline;
        s.MouseDown(s.X + 5, outl.Top + 2, win); s.MouseUp();
        E(!s.Panel.Drop.Open && win.Outline == outlBefore && s.TellOpenPicked == -1, "outside press closes the list, toggles nothing");
        E(outl.Help.Length > 0 && tellOpen.Help.Length > 0, "help rows carry tooltip text");

        // An alpha colour (Background) expands to 4 channels; the opaque Text colour to 3 (no dead A slider).
        SetRow bgSw = s.Rows.Find(r => r.Act == SetAct.ColorSwatch && r.Key == ChatColor.Background)!;
        s.MouseDown(s.X + 5, bgSw.Top + 2, win);
        E(s.Rows.FindAll(r => r.Act == SetAct.ColorChannel).Count == 4, "alpha swatch expands to 4 channel rows");
        s.MouseDown(s.X + 5, s.Rows.Find(r => r.Act == SetAct.ColorSwatch && r.Key == ChatColor.Background)!.Top + 2, win); // collapse

        // Colour swatch expands; dragging the R channel writes win.Colors.
        SetRow sw = s.Rows.Find(r => r.Act == SetAct.ColorSwatch && r.Key == ChatColor.Text)!;
        s.MouseDown(s.X + 5, sw.Top + 2, win);
        E(s.Rows.FindAll(r => r.Act == SetAct.ColorChannel).Count == 3, "opaque (Text) swatch expands to 3 channel rows");
        SetRow rCh = s.Rows.Find(r => r.Act == SetAct.ColorChannel && r.Channel == 0)!;
        s.SliderTrack(rCh, out float cx0, out float cx1, out float _);
        s.MouseDown(cx0, rCh.Top + 2, win);   // full-left -> R = 0
        E(win.Colors.TryGetValue(ChatColor.Text, out uint tv) && ((tv >> 24) & 0xFF) == 0, "R channel full-left -> 0");
        s.MouseUp();

        // Close button closes.
        s.OpenAt(win, 0, 0); s.MouseUp();
        s.MouseDown(s.X + s.W - 2, s.Y + 2, win);
        E(!s.Open, "close-X closes");

        // Click-off closes.
        s.OpenAt(win, 0, 0); s.MouseUp();
        s.MouseDown(s.X + 5, s.Y + s.H + 40, win);
        E(!s.Open, "click-off closes");

        // Opens centred in the viewport (independent of where the chat window sits) and fits on-screen.
        var low = new ChatWindow(100, 700, 400, 100, new[] { new Tab("All", 0xffffffff) });
        s.OpenAt(low, 1280, 800); s.MouseUp();
        E(System.Math.Abs(s.X - (1280 - s.W) / 2f) < 0.5f, "panel opens horizontally centred");
        E(System.Math.Abs(s.Y - (800 - s.H) / 2f) < 0.5f, "panel opens vertically centred");
        E(s.Y + s.H <= 800.5f && s.X + s.W <= 1280.5f, "centred panel fits on-screen");

        // Title-bar drag moves the whole panel; the rows move with it (translate-only, #53).
        float px = s.X, py = s.Y;
        float topBefore = s.Rows[0].Top;
        E(s.MouseDown(s.X + s.W / 2f, s.Y + 2f, win), "title-bar press consumed");
        s.MouseMove(s.X + s.W / 2f + 40f, s.Y + 2f + 25f, win);
        E(System.Math.Abs(s.X - (px + 40f)) < 0.5f && System.Math.Abs(s.Y - (py + 25f)) < 0.5f, "panel drags by the title bar");
        E(System.Math.Abs(s.Rows[0].Top - (topBefore + 25f)) < 0.5f, "rows follow the panel move");
        E(s.MouseUp(), "panel move release consumed");
        s.Reset();

        // ---- Tab editing (#39) ----
        s.OpenAt(win, 0, 0); s.MouseUp();
        E(win.Tabs.Count == 1, "pre-tab: one default tab");

        // Add tab: click the "+ Add tab" button. Add auto-expands the new tab.
        SetRow addBtn = s.Rows.Find(r => r.Act == SetAct.TabAdd)!;
        s.MouseDown(s.X + 5, addBtn.Top + 2, win); s.MouseUp();
        E(win.Tabs.Count == 2 && win.Tabs[1].Label == "All" && win.Tabs[1].ChannelMask == 0xffffffff,
          "add tab creates an All-channel tab");
        E(s.Rows.FindAll(r => r.Act == SetAct.TabChannel && r.Key == "1").Count == FilterChannel.Editable.Length,
          "add tab auto-expands: channel toggles shown");

        // Collapse then re-expand via header click.
        SetRow hdr1 = s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "1")!;
        s.MouseDown(s.X + 5, hdr1.Top + 2, win); s.MouseUp();
        E(s.Rows.Find(r => r.Act == SetAct.TabChannel && r.Key == "1") == null, "header click collapses");
        hdr1 = s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "1")!;
        s.MouseDown(s.X + 5, hdr1.Top + 2, win); s.MouseUp();
        E(s.Rows.FindAll(r => r.Act == SetAct.TabChannel && r.Key == "1").Count == FilterChannel.Editable.Length,
          "header click re-expands");

        // Channel toggle: flip the Talk bit (0x001) and verify XOR preserves other bits.
        SetRow talkCh = s.Rows.Find(r => r.Act == SetAct.TabChannel && r.Key == "1" && r.Bit == 0x001)!;
        uint maskBefore = win.Tabs[1].ChannelMask;
        s.MouseDown(s.X + 5, talkCh.Top + 2, win); s.MouseUp();
        E(win.Tabs[1].ChannelMask == (maskBefore ^ 0x001), "channel toggle XORs exactly its bit");
        E((win.Tabs[1].ChannelMask & ~0x001u) == (maskBefore & ~0x001u), "other bits unchanged");

        // Label edit: click the label row, type a new label, commit.
        SetRow lblRow = s.Rows.Find(r => r.Act == SetAct.TabLabel && r.Key == "1")!;
        s.MouseDown(s.X + 5, lblRow.Top + 2, win); s.MouseUp();
        E(s.Editing, "clicking a tab label row starts a label edit");
        s.EditInsert("Custom");
        s.CommitEdit(win);
        E(win.Tabs[1].Label == "Custom", "label edit commits the typed text");
        E(s.Rows.Find(r => r.Act == SetAct.TabLabel && r.Key == "1")!.Value == "Custom", "label row Value mirrors the commit");
        E(s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "1")!.Value == "Custom", "header row Value mirrors the commit");

        // Empty label edit keeps the old label. Tab 1 is still expanded from above.
        s.OpenAt(win, 0, 0); s.MouseUp();
        lblRow = s.Rows.Find(r => r.Act == SetAct.TabLabel && r.Key == "1")!;
        s.MouseDown(s.X + 5, lblRow.Top + 2, win); s.MouseUp();
        s.CommitEdit(win);
        E(win.Tabs[1].Label == "Custom", "empty label commit keeps old label");

        // Pin edit: click the pin row, type a player name, commit.
        SetRow pinRow = s.Rows.Find(r => r.Act == SetAct.TabPin && r.Key == "1")!;
        E(pinRow.Value == "(none)", "pin row shows (none) when unpinned");
        s.MouseDown(s.X + 5, pinRow.Top + 2, win); s.MouseUp();
        E(s.Editing, "clicking a pin row starts a label edit");
        s.EditInsert("Alice");
        s.CommitEdit(win);
        E(win.Tabs[1].PinnedName == "Alice", "pin edit commits the typed name");
        pinRow = s.Rows.Find(r => r.Act == SetAct.TabPin && r.Key == "1")!;
        E(pinRow.Value == "Alice", "pin row Value mirrors the commit");

        // Empty pin commit clears the pin (unpin affordance).
        s.OpenAt(win, 0, 0); s.MouseUp();
        pinRow = s.Rows.Find(r => r.Act == SetAct.TabPin && r.Key == "1")!;
        s.MouseDown(s.X + 5, pinRow.Top + 2, win); s.MouseUp();
        s.CommitEdit(win);
        E(win.Tabs[1].PinnedName == "", "empty pin commit clears the pin");

        // Remove tab: present when >1 tab, removes the tab.
        SetRow rmBtn = s.Rows.Find(r => r.Act == SetAct.TabRemove && r.Key == "1")!;
        s.MouseDown(s.X + 5, rmBtn.Top + 2, win); s.MouseUp();
        E(win.Tabs.Count == 1, "remove tab removes the tab");

        // Remove row absent when only one tab remains.
        s.OpenAt(win, 0, 0); s.MouseUp();
        hdr1 = s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "0")!;
        s.MouseDown(s.X + 5, hdr1.Top + 2, win); s.MouseUp();
        E(s.Rows.Find(r => r.Act == SetAct.TabRemove) == null, "remove button absent at 1 tab");

        // Ephemeral tabs are entirely hidden from settings.
        win.AddTab(new Tab("Tell:Bob", 0x002));
        E(win.Tabs[^1].Ephemeral, "pre: Tell-only tab is ephemeral");
        s.OpenAt(win, 0, 0); s.MouseUp();
        E(s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "1") == null, "ephemeral tab: no header row");
        win.RemoveTab(1);

        s.Reset();

        // ---- Copy from (#41) ----
        // With 2 windows: window 0 sees "Copy from Window 2"; window 1 sees "Copy from Window 1".
        s.OpenAt(win, 0, 0, winIndex: 0, winCount: 2); s.MouseUp();
        SetRow? copyRow = s.Rows.Find(r => r.Act == SetAct.CopyFrom);
        E(copyRow != null && copyRow.Label == "Copy from Window 2", "copy row shown for the other window");
        s.MouseDown(s.X + 5, copyRow!.Top + 2, win); s.MouseUp();
        E(s.CopyRequested == 1, "CopyRequested set to the source window index");
        s.CopyRequested = -1;

        // Single window: no copy row.
        s.OpenAt(win, 0, 0, winIndex: 0, winCount: 1); s.MouseUp();
        E(s.Rows.Find(r => r.Act == SetAct.CopyFrom) == null, "no copy row when only one window");
        s.Reset();

        // ---- BgMode dropdown + conditional bg2 swatch ----
        E(win.BgMode == BgMode.Solid, "pre: BgMode starts Solid");
        s.OpenAt(win, 0, 0); s.MouseUp();
        SetRow bgmRow = s.Rows.Find(r => r.Act == SetAct.CycleBgMode)!;
        E(bgmRow != null && bgmRow.Kind == SetKind.Dropdown && bgmRow.Choices!.Length == 10, "BgMode is a 10-choice dropdown");
        E(s.Rows.Find(r => r.Act == SetAct.ColorSwatch && r.Key == ChatColor.Background2) == null,
          "bg2 swatch hidden when Solid");
        s.MouseDown(s.X + 5, bgmRow.Top + 2, win); s.MouseUp();
        E(s.Panel.Drop.Open && s.Panel.Drop.List.Overflows, "10 styles: the list scrolls past 8");
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)BgMode.GradientV) + 2, win); s.MouseUp();
        E(win.BgMode == BgMode.GradientV, "picking Grad V sets GradientV");
        E(s.Rows.Find(r => r.Act == SetAct.ColorSwatch && r.Key == ChatColor.Background2) != null,
          "bg2 swatch shown when non-Solid");
        bgmRow = s.Rows.Find(r => r.Act == SetAct.CycleBgMode)!;
        s.MouseDown(s.X + 5, bgmRow.Top + 2, win); s.MouseUp();
        s.Panel.Drop.List.Offset = 0f;
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)BgMode.Solid) + 2, win); s.MouseUp();
        E(win.BgMode == BgMode.Solid, "picking Solid returns to Solid");
        s.Reset();

        // ---- #587: the panel caps at the viewport and scrolls; a hit past the cap is offset-correct ----
        var many = new ChatWindow(100, 100, 400, 300, new[] { new Tab("All", 0xffffffff) });
        s.OpenAt(many, 1280, 200); s.MouseUp();
        E(System.Math.Abs(s.H - 200f) < 0.01f && s.Panel.Scroll.Overflows, "a 200 px viewport caps the panel; the rows scroll");
        SetRow first = s.Rows[0];
        E(s.MouseWheel(-1) && s.Panel.Scroll.Offset > 0f, "the wheel scrolls the rows");
        E(s.Panel.RowTop(first) < s.Panel.ContentTop, "the first row moved up under the title");
        SetRow? under = null;
        for (int ri = 0; ri < s.Rows.Count; ri++)
            if (s.Panel.RowTop(s.Rows[ri]) <= s.Panel.ContentTop + 2 && s.Panel.RowTop(s.Rows[ri]) + RowH > s.Panel.ContentTop + 2) under = s.Rows[ri];
        E(under != null && !ReferenceEquals(under, first), "a different row now sits at the box top");
        s.Reset();

        // ---- #572: the tab editor is wide enough for a full-length label and scrolls into reach ----
        var tabbed = new ChatWindow(100, 100, 400, 300, new[] { new Tab("All", 0xffffffff) }) { AnimEnabled = false };
        for (int i = 1; i < 12; i++) tabbed.AddTab(new Tab(new string('W', 32), 0xffffffff));
        s.OpenAt(tabbed, 1280, 240); s.MouseUp();
        SetRow hdr0 = s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "0")!;
        s.Panel.ControlBox(hdr0, out float tbx0, out float tbx1, out _, out _);
        E(tbx1 - tbx0 - 6f >= TabLabelW, "a tab text box spans a 32-rune label");
        E(s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "11")!.Value.Length == 32, "the header row carries the whole label");
        E(s.Panel.Scroll.Overflows, "12 tabs in a 240 px viewport: the tab rows overflow the box");
        SetRow addLast = s.Rows.Find(r => r.Act == SetAct.TabAdd)!;
        E(s.Panel.RowTop(addLast) >= s.Panel.Scroll.Y + s.Panel.Scroll.H, "pre: the add row lies past the box");
        while (s.MouseWheel(-1)) { }
        addLast = s.Rows.Find(r => r.Act == SetAct.TabAdd)!;
        float addTop = s.Panel.RowTop(addLast);
        E(addTop >= s.Panel.Scroll.Y && addTop + RowH <= s.Panel.Scroll.Y + s.Panel.Scroll.H + 0.01f, "wheeling to the end brings the add row into the box");
        SetRow hdr11 = s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "11")!;
        s.MouseDown(s.X + 5, s.Panel.RowTop(hdr11) + 2, tabbed); s.MouseUp();
        E(s.Rows.FindAll(r => r.Act == SetAct.TabChannel && r.Key == "11").Count == FilterChannel.Editable.Length, "a scrolled-to header expands (offset-correct hit)");
        while (s.MouseWheel(-1)) { }
        SetRow dmCh = s.Rows.Find(r => r.Act == SetAct.TabChannel && r.Key == "11" && r.Bit == 0x400)!;
        float dmTop = s.Panel.RowTop(dmCh);
        E(dmTop >= s.Panel.Scroll.Y && dmTop < s.Panel.Scroll.Y + s.Panel.Scroll.H, "the last channel toggle scrolls into the box");
        s.MouseDown(s.X + 5, dmTop + 2, tabbed); s.MouseUp();
        E(tabbed.Tabs[11].ChannelMask == (0xffffffffu ^ 0x400u), "the scrolled-to toggle flips exactly its bit");
        {
            var atlasT = new GlyphAtlas { LineHeight = 18, Ascent = 14, SpaceAdvance = 4f };
            atlasT.SetTestGlyph(' ', 4f);
            for (char ch = '(' ; ch <= 'z'; ch++) atlasT.SetTestGlyph(ch, 7f);
            atlasT.SetTestGlyph('×', 7f);
            var vb = new VertexBatch(atlasT.SolidU, atlasT.SolidV);
            var pal = new SettingsPanel.Palette { Body = (0.1f, 0.1f, 0.1f, 1f), TitleBar = (0.2f, 0.2f, 0.2f, 1f), Active = (0.2f, 0.3f, 0.5f, 1f), TextRgb = 0xFFFFFF };
            s.Panel.Emit(vb, atlasT, 14f, pal);
            long alloc0 = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { vb.Clear(); s.Panel.Emit(vb, atlasT, 14f, pal); }
            long delta = System.GC.GetAllocatedBytesForCurrentThread() - alloc0;
            E(delta == 0, $"100 tab-editor draws (expanded, scrolled) allocate nothing ({delta} bytes)");
        }
        s.Reset();

        log.Info(ok ? "SettingsWindow.SelfCheck: passed" : "SettingsWindow.SelfCheck: FAILED");
    }
}
