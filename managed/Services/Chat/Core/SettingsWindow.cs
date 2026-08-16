using System.Collections.Generic;
using Sowx.Framework;

namespace Sowx.Services.Chat.Core;

/// <summary>What a settings row edits. The row carries the extras each needs (bit, colour key, slider range),
/// and <see cref="SettingsWindow.MouseDown"/> switches on it to mutate the window/config.</summary>
public enum SetAct
{
    ToggleBit, InputTop, AutoHideHdr, LockWin, TellsHere, AnimOn, SliderHold, SliderFade, SliderTau, SliderFont, OutlineOn,
    CycleFmt, CycleSrc, CycleAlign, CycleTellOpen, CycleBgMode, ColorSwatch, ColorChannel,
    TabExpand, TabAdd, TabRemove, TabLabel, TabPin, TabChannel,
    CopyFrom,
}

/// <summary>The visual kind of a row (how the renderer draws it).</summary>
public enum SetKind { Toggle, Cycle, Slider, Swatch, Button }

/// <summary>GUIINGAME_MESSAGE_* flag values read from the binary's rodata. The channel value from the
/// ingest hook IS the flag, and both <see cref="Tab.Matches"/> and <see cref="ChatStore"/> filter via
/// direct <c>mask &amp; channel</c>. "Combat" maps to TO_HIT; CCMESSAGE (0x80) is feedback text.</summary>
public static class FilterChannel
{
    public static readonly (uint Bit, string Label)[] Editable =
    {
        (0x001, "Talk"),
        (0x002, "Tell"),
        (0x004, "Shout"),
        (0x008, "Whisper"),
        (0x010, "To-Hit"),
        (0x020, "Server"),
        (0x040, "Party"),
        (0x080, "Feedback"),
        (0x400, "DM"),
    };
}

/// <summary>One laid-out settings row: its kind, action, label, computed geometry, and the current value the
/// renderer reads. Built by <see cref="SettingsWindow.Layout"/> from the live window+config each rebuild.</summary>
public sealed class SetRow
{
    public SetKind Kind;
    public SetAct Act;
    public string Label = "";
    public float Top, Height;
    // Render-read state / mutation extras:
    public bool On;          // Toggle
    public string Value = "";// Cycle current text
    public float Norm;       // Slider fill 0..1
    public uint Rgba;        // Swatch colour
    public uint Bit;         // ToggleBit
    public string Key = "";  // colour key (Swatch / ColorChannel)
    public int Channel = -1; // 0=R 1=G 2=B 3=A (ColorChannel)
    public float Min, Max;   // Slider range
}

/// <summary>
/// The chat settings window: a scrolling-free floating panel of typed rows (checkboxes, cycle buttons,
/// sliders, and expandable RGBA colour pickers) that edits the <see cref="ChatWindow"/> toggles and the
/// <see cref="ChatConfig"/>. Pure -- pixels in, hit-tests + value edits out, no native refs -- so it
/// self-checks off-device. Opened from the gear icon; anchored over the chat window and clamped on-screen
/// (the shell passes the last-known viewport). ponytail: fixed anchor, no drag, no scroll; a scroll pane is
/// the ceiling once the option list can exceed the viewport height.
/// </summary>
public sealed class SettingsWindow
{
    public float X, Y;
    /// <summary>Panel width. Scales with the font/UI so the label|control split (<see cref="ControlX"/>) and the
    /// value boxes keep their proportions -- labels and values don't run past their column when the font grows.</summary>
    public float W => 264f * Metrics.Scale;
    public bool Open;
    private bool _pressConsumed;
    // The slider being dragged (mouse-down..up), a live row in the current Rows list. Layout() only runs on the
    // input thread (never from the ingest-thread Rebuild), and no Layout runs mid-drag -- MouseDown re-resolves
    // _grab into the freshly published list right after its Layout, and MouseMove doesn't re-lay -- so this
    // reference stays a drawn row for the whole drag, and its Norm/Rgba update in place track the pointer live.
    private SetRow? _grab;
    private bool _moving;          // dragging the panel by its title bar
    private float _mgx, _mgy;      // move-grab anchor: pointer offset from (X,Y) at grab
    private string _expanded = "";// colour key whose RGBA channels are shown, or ""
    private int _expandedTab = -1; // tab index whose channels are shown, or -1
    // Inline numeric text entry (click a slider's value box to type an exact value). _editRow is a live row in
    // the current Rows list; like _grab it's re-resolved after MouseDown's Layout and dropped if the row vanishes.
    private SetRow? _editRow;
    private string _editText = "";
    private bool _editLabel;       // true = editing a tab label (broad char set), false = numeric (digits only)

    // Pixel metrics scale with Metrics.Scale (font size + game UI scale) so labels/values/title don't clip when
    // the font grows; ControlX stays a const -- it's a width fraction, not a pixel length.
    public static float TitleH => 22f * Metrics.Scale;
    public static float RowH => 24f * Metrics.Scale;
    public static float Pad => 8f * Metrics.Scale;
    public static float CheckSize => 14f * Metrics.Scale;
    public static float CloseW => TitleH;
    /// <summary>Colour swatch box drawn at the right edge of a swatch row.</summary>
    public static float SwatchW => 34f * Metrics.Scale;
    /// <summary>Left inset of the control (slider track / cycle value / swatch) -- labels sit left of it.</summary>
    public const float ControlX = 0.42f;
    /// <summary>Extra left indent of a colour channel sub-row.</summary>
    public static float ChannelIndent => 12f * Metrics.Scale;
    /// <summary>Numeric value box at a slider row's right edge (click to type an exact value).</summary>
    public static float ValueW => 36f * Metrics.Scale;

    /// <summary>The laid-out rows; the renderer draws these, hit-testing reads them. <see cref="Layout"/> swap-
    /// publishes a fresh list (never mutates in place), so a reader on another thread (ingest-thread Rebuild ->
    /// EmitSettings) iterates a stable snapshot instead of racing a Clear -- the lock-free pattern the store uses.</summary>
    private volatile List<SetRow> _rows = new();
    public List<SetRow> Rows => _rows;
    // ponytail: X/Y/W/_h are plain fields read by the (cold) draw while Layout writes them; a torn read is a
    // one-frame cosmetic panel-geometry glitch, memory-safe -- the same tolerance the store's ring read carries.
    private float _h = TitleH + Pad;
    /// <summary>Total panel height (title + laid-out rows + pad). Grows with the option list.</summary>
    public float H => _h;

    /// <summary>Slider track span + centre-y for a row (shared by draw + hit-test so they always agree). The
    /// track stops short of the right-edge value box.</summary>
    public void SliderTrack(SetRow r, out float x0, out float x1, out float cy)
    {
        float indent = r.Channel >= 0 ? ChannelIndent : 0f;
        x0 = X + W * ControlX + indent;
        x1 = X + W - Pad - ValueW - 4f;
        cy = r.Top + RowH * 0.5f;
    }

    /// <summary>The slider value box rect (right edge of the row). Draw + hit-test share it.</summary>
    public void ValueBox(SetRow r, out float x0, out float x1, out float y0, out float y1)
    {
        x1 = X + W - Pad; x0 = x1 - ValueW;
        y0 = r.Top + 3f; y1 = r.Top + RowH - 3f;
    }
    private bool OverValueBox(SetRow r, float mx, float my)
    { ValueBox(r, out float x0, out float x1, out float y0, out float y1); return mx >= x0 && mx <= x1 && my >= y0 && my <= y1; }

    /// <summary>Number of chat windows (snapshot), used to show "Copy from Window N" rows. Set by the shell
    /// before <see cref="Layout"/> (the panel is modal, so window count can't change while it's open).</summary>
    public int WindowCount;
    /// <summary>Index of the window this panel is editing (0 = Main). Set by the shell on open.</summary>
    public int WindowIndex;

    /// <summary>Rebuild the row list + panel height from the live window + config, publishing a fresh list.
    /// Input-thread only (open + each settings interaction); the ingest-thread Rebuild never calls it, so the
    /// list it builds is single-writer and only ever read cross-thread as a whole snapshot.</summary>
    public void Layout(ChatWindow win, ChatConfig cfg)
    {
        var rows = new List<SetRow>();
        float y = Y + TitleH;

        // Render toggles (the window's option bits).
        foreach (var (bit, label) in ChatWindow.Options)
            y = AddToggle(rows, y, SetAct.ToggleBit, label, (win.Toggles & bit) != 0, bit);

        y = AddToggle(rows, y, SetAct.InputTop, "Input on top", win.InputOnTop, 0);
        y = AddToggle(rows, y, SetAct.AutoHideHdr, "Auto-hide header", win.AutoHideHeader, 0);
        y = AddToggle(rows, y, SetAct.LockWin, "Lock window", win.Locked, 0);
        y = AddToggle(rows, y, SetAct.TellsHere, "Tells here", win.TellsHere, 0);
        y = AddCycle(rows, y, SetAct.CycleTellOpen, "Tell auto-open", TellOpenText(cfg.TellOpen));
        y = AddToggle(rows, y, SetAct.AnimOn, "Animations", win.AnimEnabled, 0);
        if (win.AnimEnabled)
        {
            y = AddSlider(rows, y, SetAct.SliderHold, "Hold (s)", win.HoldSec, 0f, 300f);
            y = AddSlider(rows, y, SetAct.SliderFade, "Fade (s)", win.FadeSec, 0f, 30f);
            y = AddSlider(rows, y, SetAct.SliderTau, "Ease (ms)", win.TauMs, 10f, 500f);
        }

        y = AddSlider(rows, y, SetAct.SliderFont, "Font size", cfg.FontPx, 10f, 32f);
        y = AddToggle(rows, y, SetAct.OutlineOn, "Outline", win.Outline, 0);

        y = AddCycle(rows, y, SetAct.CycleAlign, "Alignment", win.Align.ToString());
        y = AddCycle(rows, y, SetAct.CycleFmt, "Time format", FormatText(win.TimeFormat));
        y = AddCycle(rows, y, SetAct.CycleSrc, "Time source", win.TimeSource.ToString());

        foreach (var (key, label, alpha) in ChatColor.Editable)
        {
            uint rgba = ColorOf(win, key);
            y = AddSwatch(rows, y, label, key, rgba);
            if (_expanded == key)
                for (int ch = 0; ch < (alpha ? 4 : 3); ch++)
                    y = AddChannel(rows, y, key, ch, ChannelByte(rgba, ch));
            if (key == ChatColor.Background)
            {
                y = AddCycle(rows, y, SetAct.CycleBgMode, "Bg style", BgModeText(win.BgMode));
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
            rows.Add(new SetRow { Kind = SetKind.Cycle, Act = SetAct.TabExpand,
                Label = $"Tab {ti + 1}", Value = tab.Label, Top = y, Height = RowH, Key = tk });
            y += RowH;
            if (_expandedTab == ti)
            {
                rows.Add(new SetRow { Kind = SetKind.Cycle, Act = SetAct.TabLabel,
                    Label = "  Label", Value = tab.Label, Top = y, Height = RowH, Key = tk });
                y += RowH;
                rows.Add(new SetRow { Kind = SetKind.Cycle, Act = SetAct.TabPin,
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

        _h = (y - Y) + Pad;
        _rows = rows;
    }

    /// <summary>Resolve a per-window colour to its current RGBA8 (override or default). Set by the shell so
    /// the pure core doesn't reach into the renderer for defaults.</summary>
    public static System.Func<ChatWindow, string, uint> ColorResolver = (win, key) =>
        win.Colors.TryGetValue(key, out uint v) ? v : 0xffffffffu;
    private static uint ColorOf(ChatWindow win, string key) => ColorResolver(win, key);

    private static float AddToggle(List<SetRow> rows, float y, SetAct act, string label, bool on, uint bit)
    { rows.Add(new SetRow { Kind = SetKind.Toggle, Act = act, Label = label, Top = y, Height = RowH, On = on, Bit = bit }); return y + RowH; }

    private static float AddCycle(List<SetRow> rows, float y, SetAct act, string label, string val)
    { rows.Add(new SetRow { Kind = SetKind.Cycle, Act = act, Label = label, Top = y, Height = RowH, Value = val }); return y + RowH; }

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
    public void OpenAt(ChatWindow win, ChatConfig cfg, float vpW, float vpH,
                       int winIndex = 0, int winCount = 1)
    {
        Open = true; _pressConsumed = true; _grab = null; _moving = false; _editRow = null; _editText = ""; _editLabel = false;
        WindowIndex = winIndex; WindowCount = winCount;
        Layout(win, cfg);
        if (vpW > 0f && vpH > 0f) { X = (vpW - W) / 2f; Y = (vpH - H) / 2f; }
        else { X = win.X + (win.W - W) / 2f; Y = win.Y + ChatWindow.TabBarH + 6f; }
        if (X < 0f) X = 0f;
        if (Y < 0f) Y = 0f;
        Layout(win, cfg);
    }

    public bool OverClose(float mx, float my)
        => mx >= X + W - CloseW && mx <= X + W && my >= Y && my <= Y + TitleH;

    internal bool Contains(float mx, float my) => mx >= X && mx <= X + W && my >= Y && my <= Y + H;

    private SetRow? RowAt(float mx, float my)
    {
        if (mx < X || mx > X + W) return null;
        foreach (SetRow r in Rows)
            if (my >= r.Top && my < r.Top + r.Height) return r;
        return null;
    }

    /// <summary>Pointer press while open. Always consumed. Mutates the window/config per the hit row; a slider
    /// hit starts a drag (see <see cref="MouseMove"/>). Close button or a click-off closes.</summary>
    public bool MouseDown(float mx, float my, ChatWindow win, ChatConfig cfg)
    {
        _pressConsumed = true; _grab = null; _moving = false;
        if (_editRow != null) CommitEdit(win, cfg);   // any press ends an in-progress field edit (commit-on-focus-loss)
        if (OverClose(mx, my)) { Open = false; return true; }
        // Title bar (above the first row) is the move handle -- drag the panel anywhere on-screen.
        if (my < Y + TitleH) { _moving = true; _mgx = mx - X; _mgy = my - Y; return true; }
        SetRow? row = RowAt(mx, my);
        if (row == null) { if (!Contains(mx, my)) Open = false; return true; }

        switch (row.Act)
        {
            case SetAct.ToggleBit: win.Toggles ^= row.Bit; break;
            case SetAct.InputTop: win.InputOnTop = !win.InputOnTop; break;
            case SetAct.AutoHideHdr: win.AutoHideHeader = !win.AutoHideHeader; break;
            case SetAct.LockWin: win.Locked = !win.Locked; break;
            case SetAct.TellsHere: win.TellsHere = !win.TellsHere; TellsHereChanged = win.TellsHere; break;
            case SetAct.CycleTellOpen: cfg.TellOpen = (TellAutoOpen)(((int)cfg.TellOpen + 1) % 3); break;
            case SetAct.AnimOn: win.AnimEnabled = !win.AnimEnabled; break;
            case SetAct.OutlineOn: win.Outline = !win.Outline; break;
            case SetAct.CycleFmt: win.TimeFormat = (TsFormat)(((int)win.TimeFormat + 1) % 3); break;
            case SetAct.CycleSrc: win.TimeSource = (TsSource)(((int)win.TimeSource + 1) % 2); break;
            case SetAct.CycleAlign: win.Align = (TextAlign)(((int)win.Align + 1) % 4); break;
            case SetAct.CycleBgMode: win.BgMode = (BgMode)(((int)win.BgMode + 1) % 10); break;
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
                BeginLabelEdit(row);
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
            case SetAct.SliderHold: case SetAct.SliderFade: case SetAct.SliderTau: case SetAct.SliderFont: case SetAct.ColorChannel:
                if (OverValueBox(row, mx, my)) { BeginEdit(row); break; }
                SliderTrack(row, out float sx0, out float sx1, out float _);
                if (mx >= sx0 && mx <= sx1) { _grab = row; SetSliderFromX(row, mx, win, cfg); }
                break;
        }
        Layout(win, cfg);   // reflect the change (e.g. anim toggle shows/hides its sliders) for the next frame
        // Layout published a fresh list; re-resolve the grabbed/edited slider into it so the drawn knob + value
        // box (not a detached row) track the interaction. A vanished row (Find null) ends the grab/edit cleanly.
        if (_grab != null) _grab = _rows.Find(r => r.Act == _grab.Act && r.Key == _grab.Key && r.Channel == _grab.Channel);
        if (_editRow != null) _editRow = _rows.Find(r => r.Act == _editRow.Act && r.Key == _editRow.Key && r.Channel == _editRow.Channel);
        return true;
    }

    /// <summary>Index of the source window for a pending "Copy from" action, or -1. The shell reads + clears
    /// this after MouseDown to perform the actual copy (it owns the window list, which Layout doesn't).</summary>
    public int CopyRequested = -1;

    /// <summary>Set true when the user just turned TellsHere on. The shell reads + clears this to enforce
    /// exclusivity (clear TellsHere on all other windows — the panel only sees one window).</summary>
    public bool TellsHereChanged;

    /// <summary>True while the panel is being dragged by its title bar (not a slider grab).</summary>
    public bool Moving => _moving;

    /// <summary>Pointer motion while a slider or the title bar is grabbed. A title-bar move translates
    /// row positions in place without a full <see cref="Layout"/> (#53).</summary>
    public bool MouseMove(float mx, float my, ChatWindow win, ChatConfig cfg)
    {
        if (_moving)
        {
            float oldY = Y;
            X = mx - _mgx; Y = my - _mgy;
            float dy = Y - oldY;
            var rows = _rows;
            for (int i = 0; i < rows.Count; i++) rows[i].Top += dy;
            return true;
        }
        if (_grab == null) return false;
        SetSliderFromX(_grab, mx, win, cfg);
        return true;
    }

    private void SetSliderFromX(SetRow r, float mx, ChatWindow win, ChatConfig cfg)
    {
        SliderTrack(r, out float x0, out float x1, out float _);
        float norm = x1 > x0 ? Clamp01((mx - x0) / (x1 - x0)) : 0f;
        ApplyValue(r, r.Act == SetAct.ColorChannel ? norm * 255f : r.Min + norm * (r.Max - r.Min), win, cfg);
    }

    /// <summary>Write a raw value onto the window (per-window settings) or config (font, which is global) for a
    /// slider/channel row and mirror it back onto the row's draw state so the knob/value box track the pointer.</summary>
    private void ApplyValue(SetRow r, float v, ChatWindow win, ChatConfig cfg)
    {
        if (r.Act == SetAct.ColorChannel)
        {
            int b = (int)(v + 0.5f); b = b < 0 ? 0 : b > 255 ? 255 : b;
            uint cur = ColorOf(win, r.Key);
            win.Colors[r.Key] = WithChannel(cur, r.Channel, b);
            r.Rgba = win.Colors[r.Key];
            r.Norm = b / 255f;
            foreach (SetRow sw in _rows)
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
                case SetAct.SliderFont: cfg.FontPx = cl; break;
            }
        }
    }

    // ---- inline numeric text entry ------------------------------------------------------------

    /// <summary>True while a slider value box is being typed into (the shell routes keys/text here).</summary>
    public bool Editing => _editRow != null;
    /// <summary>Is <paramref name="r"/> the row being text-edited? (the renderer shows the buffer + a caret).</summary>
    public bool EditingRow(SetRow r) => ReferenceEquals(_editRow, r);
    /// <summary>The in-progress edit buffer (renderer draws it in the active value box).</summary>
    public string EditText => _editText;

    // Seeds an empty buffer (type the exact value); the value box shows the caret, the knob still marks the old
    // position. Committing empty/unparseable text keeps the old value.
    private void BeginEdit(SetRow r) { _editRow = r; _editText = ""; _editLabel = false; _grab = null; }

    private void BeginLabelEdit(SetRow r) { _editRow = r; _editText = ""; _editLabel = true; _grab = null; }

    private static int ParseTabKey(string key)
        => int.TryParse(key, System.Globalization.NumberStyles.None,
                         System.Globalization.CultureInfo.InvariantCulture, out int i) ? i : -1;

    /// <summary>Append typed characters. Label mode accepts printable chars (capped at 24); numeric mode
    /// accepts digits/dot/sign (capped at 8).</summary>
    public void EditInsert(string s)
    {
        if (_editRow == null) return;
        if (_editLabel)
        {
            foreach (char c in s)
                if (_editText.Length < 32 && c >= ' ' && c != '\n' && c != '\r')
                    _editText += c;
        }
        else
        {
            foreach (char c in s)
                if (_editText.Length < 8 && ((c >= '0' && c <= '9') || c == '.' || c == '-'))
                    _editText += c;
        }
    }

    public void EditBackspace()
    { if (_editRow != null && _editText.Length > 0) _editText = _editText.Substring(0, _editText.Length - 1); }

    /// <summary>Commit the typed value. Label mode writes the trimmed text to the tab (empty keeps old);
    /// pin mode writes (empty clears the pin — that's the only unpin affordance); numeric mode parses +
    /// clamps via <see cref="ApplyValue"/>.</summary>
    public void CommitEdit(ChatWindow? win, ChatConfig cfg)
    {
        SetRow? r = _editRow;
        string t = _editText;
        bool label = _editLabel;
        _editRow = null; _editText = ""; _editLabel = false;
        if (r == null) return;
        if (label && r.Act == SetAct.TabPin)
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
        else if (label)
        {
            string trimmed = t.Trim();
            if (trimmed.Length > 0 && win != null)
            {
                int ti = ParseTabKey(r.Key);
                if ((uint)ti < (uint)win.Tabs.Count)
                {

                    win.Tabs[ti].Label = trimmed;
                    r.Value = trimmed;
                    foreach (SetRow hdr in _rows)
                        if (hdr.Act == SetAct.TabExpand && hdr.Key == r.Key) hdr.Value = trimmed;
                }
            }
        }
        else if (float.TryParse(t, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float v) && win != null)
            ApplyValue(r, v, win, cfg);
    }

    public void CancelEdit() { _editRow = null; _editText = ""; _editLabel = false; }

    /// <summary>The current numeric value of a slider/channel row, formatted for the value box.</summary>
    public static string FormatValue(SetRow r)
    {
        float v = r.Act == SetAct.ColorChannel ? r.Norm * 255f : r.Min + r.Norm * (r.Max - r.Min);
        return r.Act == SetAct.ColorChannel
            ? ((int)(v + 0.5f)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Pointer release. Consumed iff we owned the press. Ends any slide drag.</summary>
    public bool MouseUp()
    {
        bool c = _pressConsumed;
        _pressConsumed = false; _grab = null; _moving = false;
        return c;
    }

    /// <summary>True while a slider or the panel itself is being dragged.</summary>
    public bool Dragging => _grab != null || _moving;

    /// <summary>Force-close at a session boundary, clearing any half-owned press, drag, or field edit.</summary>
    public void Reset() { Open = false; _pressConsumed = false; _grab = null; _moving = false; _editRow = null; _editText = ""; _editLabel = false; _expanded = ""; _expandedTab = -1; TellsHereChanged = false; }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    private static int ChannelByte(uint rgba, int ch) => (int)((rgba >> (24 - ch * 8)) & 0xFF);
    private static uint WithChannel(uint rgba, int ch, int val)
    {
        int shift = 24 - ch * 8;
        return (rgba & ~(0xFFu << shift)) | ((uint)(val & 0xFF) << shift);
    }

    private static string FormatText(TsFormat f) => f switch
    { TsFormat.HourMinSec => "HH:mm:ss", TsFormat.Hour12 => "h:mm tt", _ => "HH:mm" };

    private static string BgModeText(BgMode m) => m switch
    { BgMode.GradientV => "Grad V", BgMode.GradientH => "Grad H", BgMode.GradientD => "Grad D",
      BgMode.StripesH => "Stripe H", BgMode.StripesV => "Stripe V",
      BgMode.GradientBox => "Box", BgMode.GradientCircle => "Radial",
      BgMode.Conic => "Conic", BgMode.Conic4 => "Conic 4", _ => "Solid" };

    private static string TellOpenText(TellAutoOpen m) => m switch
    { TellAutoOpen.NewTab => "New tab", TellAutoOpen.NewWindow => "New window", _ => "Off" };

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
        var cfg = new ChatConfig();
        var s = new SettingsWindow();

        s.OpenAt(win, cfg, 0, 0);
        E(s.Open && s.Rows.Count > 0, "OpenAt opens + lays out rows");
        E(s.MouseUp(), "up after gear-open is consumed (press ownership)");

        // Toggle row 0 flips its bit and stays open.
        s.OpenAt(win, cfg, 0, 0); s.MouseUp();
        SetRow tgl = s.Rows.Find(r => r.Act == SetAct.ToggleBit)!;
        uint before = win.Toggles;
        E(s.MouseDown(s.X + 5, tgl.Top + 2, win, cfg), "toggle click consumed");
        E(s.Open && win.Toggles == (before ^ tgl.Bit), "toggle flips its bit, stays open");

        // Animations off hides the duration sliders; on shows them.
        SetRow anim = s.Rows.Find(r => r.Act == SetAct.AnimOn)!;
        s.MouseDown(s.X + 5, anim.Top + 2, win, cfg);
        E(!win.AnimEnabled && s.Rows.Find(r => r.Act == SetAct.SliderHold) == null, "anim off hides sliders");
        s.MouseDown(s.X + 5, s.Rows.Find(r => r.Act == SetAct.AnimOn)!.Top + 2, win, cfg);
        E(win.AnimEnabled && s.Rows.Find(r => r.Act == SetAct.SliderHold) != null, "anim on shows sliders");

        // Slider drag sets the config value from the pointer x (full-right -> max).
        SetRow hold = s.Rows.Find(r => r.Act == SetAct.SliderHold)!;
        s.SliderTrack(hold, out float hx0, out float hx1, out float _);
        s.MouseDown(hx1, hold.Top + 2, win, cfg);
        E(System.Math.Abs(win.HoldSec - 300f) < 0.5f, "hold slider full-right -> max");
        s.MouseMove((hx0 + hx1) / 2f, hold.Top + 2, win, cfg);
        E(System.Math.Abs(win.HoldSec - 150f) < 2f, "hold slider drag to mid -> ~half");
        // Live-preview regression: the drag must move the row that's actually IN Rows (the drawn knob), not a
        // detached copy -- MouseDown re-published the list, so _grab has to point into the new one.
        E(System.Math.Abs(s.Rows.Find(r => r.Act == SetAct.SliderHold)!.Norm - 0.5f) < 0.02f, "drag updates the drawn row's Norm");
        E(s.MouseUp(), "slider release consumed");

        // Clicking a slider's label (left of the track) is consumed but leaves the value untouched.
        SetRow fade = s.Rows.Find(r => r.Act == SetAct.SliderFade)!;
        float fadeBefore = win.FadeSec;
        s.MouseDown(s.X + 5, fade.Top + 2, win, cfg);   // s.X+5 is in the label zone, left of ControlX
        E(win.FadeSec == fadeBefore, "click on a slider label leaves the value untouched");
        s.MouseUp();

        // Inline text entry: click the value box, type an exact number, commit / clamp / cancel / keep-on-empty.
        void ClickValueBox()
        {
            SetRow hr = s.Rows.Find(r => r.Act == SetAct.SliderHold)!;
            s.ValueBox(hr, out float vx0, out float vx1, out float vy0, out float vy1);
            s.MouseDown((vx0 + vx1) / 2f, (vy0 + vy1) / 2f, win, cfg);
        }
        ClickValueBox();
        E(s.Editing, "clicking a value box starts a field edit");
        s.EditInsert("4a2");   // letters filtered out -> "42"
        s.CommitEdit(win, cfg);
        E(!s.Editing && System.Math.Abs(win.HoldSec - 42f) < 0.01f, "typed value (filtered) commits");
        ClickValueBox(); s.EditInsert("999"); s.CommitEdit(win, cfg);   // over max (300) -> clamps
        E(System.Math.Abs(win.HoldSec - 300f) < 0.01f, "typed value clamps to max");
        ClickValueBox(); s.EditInsert("10"); s.CancelEdit();
        E(!s.Editing && System.Math.Abs(win.HoldSec - 300f) < 0.01f, "cancel leaves the value untouched");
        ClickValueBox(); s.CommitEdit(win, cfg);   // empty buffer -> unparseable -> keep old value (never zero)
        E(System.Math.Abs(win.HoldSec - 300f) < 0.01f, "empty commit keeps the old value");
        // A press elsewhere while editing commits the in-progress edit (focus loss).
        ClickValueBox(); s.EditInsert("77");
        SetRow animRow = s.Rows.Find(r => r.Act == SetAct.AnimOn)!;
        s.MouseDown(s.X + 5, animRow.Top + 2, win, cfg);
        E(!s.Editing && System.Math.Abs(win.HoldSec - 77f) < 0.01f, "click-away commits the edit");
        // restore anim (the click above toggled it off, hiding the sliders)
        s.MouseDown(s.X + 5, s.Rows.Find(r => r.Act == SetAct.AnimOn)!.Top + 2, win, cfg);

        // Cycle advances the enum.
        SetRow alR = s.Rows.Find(r => r.Act == SetAct.CycleAlign)!;
        TextAlign a0 = win.Align;
        s.MouseDown(s.X + 5, alR.Top + 2, win, cfg);
        E(win.Align != a0, "alignment cycle advances");

        SetRow fmt = s.Rows.Find(r => r.Act == SetAct.CycleFmt)!;
        TsFormat f0 = win.TimeFormat;
        s.MouseDown(s.X + 5, fmt.Top + 2, win, cfg);
        E(win.TimeFormat != f0, "time-format cycle advances");

        // An alpha colour (Background) expands to 4 channels; the opaque Text colour to 3 (no dead A slider).
        SetRow bgSw = s.Rows.Find(r => r.Act == SetAct.ColorSwatch && r.Key == ChatColor.Background)!;
        s.MouseDown(s.X + 5, bgSw.Top + 2, win, cfg);
        E(s.Rows.FindAll(r => r.Act == SetAct.ColorChannel).Count == 4, "alpha swatch expands to 4 channel rows");
        s.MouseDown(s.X + 5, s.Rows.Find(r => r.Act == SetAct.ColorSwatch && r.Key == ChatColor.Background)!.Top + 2, win, cfg); // collapse

        // Colour swatch expands; dragging the R channel writes win.Colors.
        SetRow sw = s.Rows.Find(r => r.Act == SetAct.ColorSwatch && r.Key == ChatColor.Text)!;
        s.MouseDown(s.X + 5, sw.Top + 2, win, cfg);
        E(s.Rows.FindAll(r => r.Act == SetAct.ColorChannel).Count == 3, "opaque (Text) swatch expands to 3 channel rows");
        SetRow rCh = s.Rows.Find(r => r.Act == SetAct.ColorChannel && r.Channel == 0)!;
        s.SliderTrack(rCh, out float cx0, out float cx1, out float _);
        s.MouseDown(cx0, rCh.Top + 2, win, cfg);   // full-left -> R = 0
        E(win.Colors.TryGetValue(ChatColor.Text, out uint tv) && ((tv >> 24) & 0xFF) == 0, "R channel full-left -> 0");
        s.MouseUp();

        // Close button closes.
        s.OpenAt(win, cfg, 0, 0); s.MouseUp();
        s.MouseDown(s.X + s.W - 2, s.Y + 2, win, cfg);
        E(!s.Open, "close-X closes");

        // Click-off closes.
        s.OpenAt(win, cfg, 0, 0); s.MouseUp();
        s.MouseDown(s.X + 5, s.Y + s.H + 40, win, cfg);
        E(!s.Open, "click-off closes");

        // Opens centred in the viewport (independent of where the chat window sits) and fits on-screen.
        var low = new ChatWindow(100, 700, 400, 100, new[] { new Tab("All", 0xffffffff) });
        s.OpenAt(low, cfg, 1280, 800); s.MouseUp();
        E(System.Math.Abs(s.X - (1280 - s.W) / 2f) < 0.5f, "panel opens horizontally centred");
        E(System.Math.Abs(s.Y - (800 - s.H) / 2f) < 0.5f, "panel opens vertically centred");
        E(s.Y + s.H <= 800.5f && s.X + s.W <= 1280.5f, "centred panel fits on-screen");

        // Title-bar drag moves the whole panel; the rows move with it (translate-only, #53).
        float px = s.X, py = s.Y;
        float topBefore = s.Rows[0].Top;
        E(s.MouseDown(s.X + s.W / 2f, s.Y + 2f, win, cfg), "title-bar press consumed");
        s.MouseMove(s.X + s.W / 2f + 40f, s.Y + 2f + 25f, win, cfg);
        E(System.Math.Abs(s.X - (px + 40f)) < 0.5f && System.Math.Abs(s.Y - (py + 25f)) < 0.5f, "panel drags by the title bar");
        E(System.Math.Abs(s.Rows[0].Top - (topBefore + 25f)) < 0.5f, "rows follow the panel move");
        E(s.MouseUp(), "panel move release consumed");
        s.Reset();

        // ---- Tab editing (#39) ----
        s.OpenAt(win, cfg, 0, 0); s.MouseUp();
        E(win.Tabs.Count == 1, "pre-tab: one default tab");

        // Add tab: click the "+ Add tab" button. Add auto-expands the new tab.
        SetRow addBtn = s.Rows.Find(r => r.Act == SetAct.TabAdd)!;
        s.MouseDown(s.X + 5, addBtn.Top + 2, win, cfg); s.MouseUp();
        E(win.Tabs.Count == 2 && win.Tabs[1].Label == "All" && win.Tabs[1].ChannelMask == 0xffffffff,
          "add tab creates an All-channel tab");
        E(s.Rows.FindAll(r => r.Act == SetAct.TabChannel && r.Key == "1").Count == FilterChannel.Editable.Length,
          "add tab auto-expands: channel toggles shown");

        // Collapse then re-expand via header click.
        SetRow hdr1 = s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "1")!;
        s.MouseDown(s.X + 5, hdr1.Top + 2, win, cfg); s.MouseUp();
        E(s.Rows.Find(r => r.Act == SetAct.TabChannel && r.Key == "1") == null, "header click collapses");
        hdr1 = s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "1")!;
        s.MouseDown(s.X + 5, hdr1.Top + 2, win, cfg); s.MouseUp();
        E(s.Rows.FindAll(r => r.Act == SetAct.TabChannel && r.Key == "1").Count == FilterChannel.Editable.Length,
          "header click re-expands");

        // Channel toggle: flip the Talk bit (0x001) and verify XOR preserves other bits.
        SetRow talkCh = s.Rows.Find(r => r.Act == SetAct.TabChannel && r.Key == "1" && r.Bit == 0x001)!;
        uint maskBefore = win.Tabs[1].ChannelMask;
        s.MouseDown(s.X + 5, talkCh.Top + 2, win, cfg); s.MouseUp();
        E(win.Tabs[1].ChannelMask == (maskBefore ^ 0x001), "channel toggle XORs exactly its bit");
        E((win.Tabs[1].ChannelMask & ~0x001u) == (maskBefore & ~0x001u), "other bits unchanged");

        // Label edit: click the label row, type a new label, commit.
        SetRow lblRow = s.Rows.Find(r => r.Act == SetAct.TabLabel && r.Key == "1")!;
        s.MouseDown(s.X + 5, lblRow.Top + 2, win, cfg); s.MouseUp();
        E(s.Editing, "clicking a tab label row starts a label edit");
        s.EditInsert("Custom");
        s.CommitEdit(win, cfg);
        E(win.Tabs[1].Label == "Custom", "label edit commits the typed text");
        E(s.Rows.Find(r => r.Act == SetAct.TabLabel && r.Key == "1")!.Value == "Custom", "label row Value mirrors the commit");
        E(s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "1")!.Value == "Custom", "header row Value mirrors the commit");

        // Empty label edit keeps the old label. Tab 1 is still expanded from above.
        s.OpenAt(win, cfg, 0, 0); s.MouseUp();
        lblRow = s.Rows.Find(r => r.Act == SetAct.TabLabel && r.Key == "1")!;
        s.MouseDown(s.X + 5, lblRow.Top + 2, win, cfg); s.MouseUp();
        s.CommitEdit(win, cfg);
        E(win.Tabs[1].Label == "Custom", "empty label commit keeps old label");

        // Pin edit: click the pin row, type a player name, commit.
        SetRow pinRow = s.Rows.Find(r => r.Act == SetAct.TabPin && r.Key == "1")!;
        E(pinRow.Value == "(none)", "pin row shows (none) when unpinned");
        s.MouseDown(s.X + 5, pinRow.Top + 2, win, cfg); s.MouseUp();
        E(s.Editing, "clicking a pin row starts a label edit");
        s.EditInsert("Alice");
        s.CommitEdit(win, cfg);
        E(win.Tabs[1].PinnedName == "Alice", "pin edit commits the typed name");
        pinRow = s.Rows.Find(r => r.Act == SetAct.TabPin && r.Key == "1")!;
        E(pinRow.Value == "Alice", "pin row Value mirrors the commit");

        // Empty pin commit clears the pin (unpin affordance).
        s.OpenAt(win, cfg, 0, 0); s.MouseUp();
        pinRow = s.Rows.Find(r => r.Act == SetAct.TabPin && r.Key == "1")!;
        s.MouseDown(s.X + 5, pinRow.Top + 2, win, cfg); s.MouseUp();
        s.CommitEdit(win, cfg);
        E(win.Tabs[1].PinnedName == "", "empty pin commit clears the pin");

        // Remove tab: present when >1 tab, removes the tab.
        SetRow rmBtn = s.Rows.Find(r => r.Act == SetAct.TabRemove && r.Key == "1")!;
        s.MouseDown(s.X + 5, rmBtn.Top + 2, win, cfg); s.MouseUp();
        E(win.Tabs.Count == 1, "remove tab removes the tab");

        // Remove row absent when only one tab remains.
        s.OpenAt(win, cfg, 0, 0); s.MouseUp();
        hdr1 = s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "0")!;
        s.MouseDown(s.X + 5, hdr1.Top + 2, win, cfg); s.MouseUp();
        E(s.Rows.Find(r => r.Act == SetAct.TabRemove) == null, "remove button absent at 1 tab");

        // Ephemeral tabs are entirely hidden from settings.
        win.AddTab(new Tab("Tell:Bob", 0x002));
        E(win.Tabs[^1].Ephemeral, "pre: Tell-only tab is ephemeral");
        s.OpenAt(win, cfg, 0, 0); s.MouseUp();
        E(s.Rows.Find(r => r.Act == SetAct.TabExpand && r.Key == "1") == null, "ephemeral tab: no header row");
        win.RemoveTab(1);

        s.Reset();

        // ---- Copy from (#41) ----
        // With 2 windows: window 0 sees "Copy from Window 2"; window 1 sees "Copy from Window 1".
        s.OpenAt(win, cfg, 0, 0, winIndex: 0, winCount: 2); s.MouseUp();
        SetRow? copyRow = s.Rows.Find(r => r.Act == SetAct.CopyFrom);
        E(copyRow != null && copyRow.Label == "Copy from Window 2", "copy row shown for the other window");
        s.MouseDown(s.X + 5, copyRow!.Top + 2, win, cfg); s.MouseUp();
        E(s.CopyRequested == 1, "CopyRequested set to the source window index");
        s.CopyRequested = -1;

        // Single window: no copy row.
        s.OpenAt(win, cfg, 0, 0, winIndex: 0, winCount: 1); s.MouseUp();
        E(s.Rows.Find(r => r.Act == SetAct.CopyFrom) == null, "no copy row when only one window");
        s.Reset();

        // ---- BgMode cycle + conditional bg2 swatch ----
        E(win.BgMode == BgMode.Solid, "pre: BgMode starts Solid");
        s.OpenAt(win, cfg, 0, 0); s.MouseUp();
        SetRow bgmRow = s.Rows.Find(r => r.Act == SetAct.CycleBgMode)!;
        E(bgmRow != null, "BgMode cycle row present");
        E(s.Rows.Find(r => r.Act == SetAct.ColorSwatch && r.Key == ChatColor.Background2) == null,
          "bg2 swatch hidden when Solid");
        s.MouseDown(s.X + 5, bgmRow.Top + 2, win, cfg); s.MouseUp();
        E(win.BgMode == BgMode.GradientV, "BgMode cycle advances to GradientV");
        E(s.Rows.Find(r => r.Act == SetAct.ColorSwatch && r.Key == ChatColor.Background2) != null,
          "bg2 swatch shown when non-Solid");
        for (int ci2 = 0; ci2 < 9; ci2++)
        {
            bgmRow = s.Rows.Find(r => r.Act == SetAct.CycleBgMode)!;
            s.MouseDown(s.X + 5, bgmRow.Top + 2, win, cfg); s.MouseUp();
        }
        E(win.BgMode == BgMode.Solid, "BgMode cycle wraps to Solid");
        s.Reset();

        log.Info(ok ? "SettingsWindow.SelfCheck: passed" : "SettingsWindow.SelfCheck: FAILED");
    }
}
