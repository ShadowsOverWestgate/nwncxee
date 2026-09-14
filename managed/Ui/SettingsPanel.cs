using System;
using System.Collections.Generic;
using System.Globalization;
using CX.Framework;

namespace CX.Ui;

/// <summary>
/// The shared settings panel: a floating, title-draggable panel of typed <see cref="SetRow"/>s (checkboxes,
/// cycle buttons, sliders with inline numeric entry, colour swatches, buttons). It owns the panel geometry --
/// position, metrics, the published row list, hit-test-to-row, the close-button hit, the title drag, the slider
/// grab and the text-entry buffer -- and the draw. A consumer supplies the rows (<see cref="Publish"/> after its
/// own layout pass) plus four callbacks: <see cref="Relayout"/>, <see cref="RowPressed"/>,
/// <see cref="ApplyValue"/> and <see cref="CommitLabel"/>. Pure -- pixels in, hit-tests + callbacks out, no
/// native refs -- so it self-checks off-device.
/// ponytail: no scroll pane, no theming, and one set of metric constants shared
/// by every consumer (Chat's literals). Per-consumer metrics are the ceiling once #167/#168 migrate the other
/// two panels.
/// </summary>
public sealed class SettingsPanel
{
    public float X, Y;

    /// <summary>Panel width in design px, before <see cref="Metrics.Scale"/>. Set by the consumer at construction.</summary>
    private readonly float _designW;

    /// <summary>Panel width. Scales with the font/UI so the label|control split (<see cref="ControlX"/>) and the
    /// value boxes keep their proportions -- labels and values don't run past their column when the font grows.</summary>
    public float W => _designW * Metrics.Scale;

    public bool Open;

    private bool _pressConsumed;
    // The slider being dragged (mouse-down..up), a live row in the current Rows list. The consumer's Relayout
    // only runs on the input thread (never from an ingest-thread rebuild), and no relayout runs mid-drag --
    // MouseDown re-resolves _grab into the freshly published list right after its Relayout, and MouseMove doesn't
    // re-lay -- so this reference stays a drawn row for the whole drag, and its Norm/Rgba update in place track
    // the pointer live.
    private SetRow? _grab;
    private bool _moving;          // dragging the panel by its title bar
    private float _mgx, _mgy;      // move-grab anchor: pointer offset from (X,Y) at grab
    // Inline text entry (click a slider's value box to type an exact value; a consumer may also start a label
    // edit on any row). _editRow is a live row in the current Rows list; like _grab it's re-resolved after
    // MouseDown's Relayout and dropped if the row vanishes.
    private SetRow? _editRow;
    private string _editText = "";
    private bool _editLabel;       // true = editing free text (broad char set), false = numeric (digits only)

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

    /// <summary>The laid-out rows; the draw walks these, hit-testing reads them. <see cref="Publish"/> swaps in a
    /// fresh list (never mutates in place), so a reader on another thread (an ingest-thread rebuild -> Emit)
    /// iterates a stable snapshot instead of racing a Clear -- the lock-free pattern the chat store uses.</summary>
    private volatile List<SetRow> _rows = new();
    public List<SetRow> Rows => _rows;
    // ponytail: X/Y/W/_h are plain fields read by the (cold) draw while the consumer's layout writes them; a torn
    // read is a one-frame cosmetic panel-geometry glitch, memory-safe -- the same tolerance the store's ring read
    // carries.
    private float _h = TitleH + Pad;
    /// <summary>Title-bar caption. Consumers that are not simply "Settings" set it at construction.</summary>
    public string Title = "Settings";

    /// <summary>Total panel height (title + laid-out rows + pad). Grows with the option list.</summary>
    public float H => _h;

    public SettingsPanel(float designW) { _designW = designW; }

    // ---- consumer contract -----------------------------------------------------------------------

    /// <summary>Rebuild the row list from the live model (the consumer's layout pass, ending in
    /// <see cref="Publish"/>). Called after every row press so the change shows next frame.</summary>
    public Action? Relayout;

    /// <summary>A press landed on a non-slider row: the consumer mutates its model. Slider rows never reach
    /// here -- the panel owns the track drag and the value box itself.</summary>
    public Action<SetRow>? RowPressed;

    /// <summary>Write a raw value for a slider/channel row (already in the row's Min..Max units) onto the
    /// consumer's model, and mirror it back onto the row's draw state.</summary>
    public Action<SetRow, float>? ApplyValue;

    /// <summary>Commit a free-text edit (the consumer decides what empty means).</summary>
    public Action<SetRow, string>? CommitLabel;

    /// <summary>Publish a freshly laid-out row list. <paramref name="contentBottom"/> is the y the layout
    /// finished at; the panel adds its bottom pad to get <see cref="H"/>.</summary>
    public void Publish(List<SetRow> rows, float contentBottom)
    {
        _h = (contentBottom - Y) + Pad;
        _rows = rows;
    }

    /// <summary>Y the consumer's first row starts at (below the title bar).</summary>
    public float ContentTop => Y + TitleH;

    /// <summary>Enter the open state, clearing any half-owned press, drag or field edit. The consumer follows
    /// with its own layout + placement.</summary>
    public void BeginOpen()
    {
        Open = true; _pressConsumed = true; _grab = null; _moving = false;
        _editRow = null; _editText = ""; _editLabel = false;
    }

    /// <summary>Force-close at a session boundary, clearing any half-owned press, drag, or field edit.</summary>
    public void Reset()
    { Open = false; _pressConsumed = false; _grab = null; _moving = false; _editRow = null; _editText = ""; _editLabel = false; }

    // ---- geometry --------------------------------------------------------------------------------

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

    public bool OverClose(float mx, float my)
        => mx >= X + W - CloseW && mx <= X + W && my >= Y && my <= Y + TitleH;

    public bool Contains(float mx, float my) => mx >= X && mx <= X + W && my >= Y && my <= Y + H;

    private SetRow? RowAt(float mx, float my)
    {
        if (mx < X || mx > X + W) return null;
        foreach (SetRow r in Rows)
            if (my >= r.Top && my < r.Top + r.Height) return r;
        return null;
    }

    // ---- input -----------------------------------------------------------------------------------

    /// <summary>Pointer press while open. Always consumed. A slider hit starts a drag (see
    /// <see cref="MouseMove"/>) or a value-box edit; every other row goes to <see cref="RowPressed"/>. Close
    /// button or a click-off closes.</summary>
    public bool MouseDown(float mx, float my)
    {
        _pressConsumed = true; _grab = null; _moving = false;
        if (_editRow != null) CommitEdit();   // any press ends an in-progress field edit (commit-on-focus-loss)
        if (OverClose(mx, my)) { Open = false; return true; }
        // Title bar (above the first row) is the move handle -- drag the panel anywhere on-screen.
        if (my < Y + TitleH) { _moving = true; _mgx = mx - X; _mgy = my - Y; return true; }
        SetRow? row = RowAt(mx, my);
        if (row == null) { if (!Contains(mx, my)) Open = false; return true; }

        if (row.Kind == SetKind.Slider)
        {
            if (OverValueBox(row, mx, my)) BeginEdit(row);
            else
            {
                SliderTrack(row, out float sx0, out float sx1, out float _);
                if (mx >= sx0 && mx <= sx1) { _grab = row; SetSliderFromX(row, mx); }
            }
        }
        else RowPressed?.Invoke(row);

        Relayout?.Invoke();   // reflect the change (e.g. an anim toggle shows/hides its sliders) for the next frame
        // Relayout published a fresh list; re-resolve the grabbed/edited row into it so the drawn knob + value box
        // (not a detached row) track the interaction. A vanished row (Find null) ends the grab/edit cleanly.
        if (_grab != null) _grab = _rows.Find(r => r.Act == _grab.Act && r.Key == _grab.Key && r.Channel == _grab.Channel);
        if (_editRow != null) _editRow = _rows.Find(r => r.Act == _editRow.Act && r.Key == _editRow.Key && r.Channel == _editRow.Channel);
        return true;
    }

    /// <summary>True while the panel is being dragged by its title bar (not a slider grab).</summary>
    public bool Moving => _moving;

    /// <summary>True while a slider or the panel itself is being dragged.</summary>
    public bool Dragging => _grab != null || _moving;

    /// <summary>Pointer motion while a slider or the title bar is grabbed. A title-bar move translates row
    /// positions in place without a full relayout (#53).</summary>
    public bool MouseMove(float mx, float my)
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
        SetSliderFromX(_grab, mx);
        return true;
    }

    /// <summary>Pointer release. Consumed iff we owned the press. Ends any slide drag.</summary>
    public bool MouseUp()
    {
        bool c = _pressConsumed;
        _pressConsumed = false; _grab = null; _moving = false;
        return c;
    }

    private void SetSliderFromX(SetRow r, float mx)
    {
        SliderTrack(r, out float x0, out float x1, out float _);
        float norm = x1 > x0 ? Clamp01((mx - x0) / (x1 - x0)) : 0f;
        ApplyValue?.Invoke(r, r.Min + norm * (r.Max - r.Min));
    }

    // ---- inline text entry -----------------------------------------------------------------------

    /// <summary>True while a value box or label row is being typed into (the shell routes keys/text here).</summary>
    public bool Editing => _editRow != null;
    /// <summary>Is <paramref name="r"/> the row being text-edited? (the draw shows the buffer + a caret).</summary>
    public bool EditingRow(SetRow r) => ReferenceEquals(_editRow, r);
    /// <summary>The in-progress edit buffer (the draw shows it in the active box).</summary>
    public string EditText => _editText;

    // Seeds an empty buffer (type the exact value); the value box shows the caret, the knob still marks the old
    // position. Committing empty/unparseable text keeps the old value.
    private void BeginEdit(SetRow r) { _editRow = r; _editText = ""; _editLabel = false; _grab = null; }

    /// <summary>Start a free-text edit on a row (the consumer calls this from <see cref="RowPressed"/>).</summary>
    public void BeginLabelEdit(SetRow r) { _editRow = r; _editText = ""; _editLabel = true; _grab = null; }

    /// <summary>Append typed characters. Label mode accepts printable chars (capped at 32); numeric mode
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

    /// <summary>Commit the typed value: label mode hands the raw text to <see cref="CommitLabel"/>, numeric mode
    /// parses it and hands the value to <see cref="ApplyValue"/> (unparseable text keeps the old value).</summary>
    public void CommitEdit()
    {
        SetRow? r = _editRow;
        string t = _editText;
        bool label = _editLabel;
        _editRow = null; _editText = ""; _editLabel = false;
        if (r == null) return;
        if (label) CommitLabel?.Invoke(r, t);
        else if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            ApplyValue?.Invoke(r, v);
    }

    public void CancelEdit() { _editRow = null; _editText = ""; _editLabel = false; }

    /// <summary>The current numeric value of a slider/channel row, formatted for the value box. A channel row
    /// (<see cref="SetRow.Channel"/> >= 0) reads as a 0..255 integer.</summary>
    public static string FormatValue(SetRow r)
    {
        float v = r.Min + r.Norm * (r.Max - r.Min);
        return r.Channel >= 0
            ? ((int)(v + 0.5f)).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    // ---- draw ------------------------------------------------------------------------------------

    /// <summary>Chrome colours the panel draws with, resolved by the consumer from its own palette.</summary>
    public struct Palette
    {
        /// <summary>Panel body, title bar, and the "on"/active fill (checkbox tick, slider fill, edit box).</summary>
        public (float r, float g, float b, float a) Body, TitleBar, Active;
        public uint TextRgb;
    }

    private const uint CloseRgb = 0xB8C0CC, CaretRgb = 0xE8E8E8, KnobRgb = 0xB8C0CC;
    private static readonly (float r, float g, float b, float a) CheckFrame = (0.55f, 0.55f, 0.55f, 1f);
    private static readonly (float r, float g, float b, float a) SbTrack = (0.04f, 0.05f, 0.07f, 0.55f);

    /// <summary>Draw the panel: a title bar (<see cref="Title"/> + a × close button) over the typed rows the consumer laid
    /// out -- checkboxes, cycle buttons, sliders, and colour swatches. Reads the rows' current values (the
    /// consumer already resolved them from its config), so it never touches the config itself.
    /// <paramref name="baseDrop"/> is the text baseline offset from a row's top.</summary>
    public void Emit(VertexBatch b, GlyphAtlas atlas, float baseDrop, Palette p)
    {
        float x = X, y = Y, w = W;
        float vpad = (RowH - atlas.LineHeight) / 2f;

        b.Rect(x, y, x + w, y + H, p.Body.r, p.Body.g, p.Body.b, 1f);
        b.Rect(x, y, x + w, y + TitleH, p.TitleBar.r, p.TitleBar.g, p.TitleBar.b, 1f);
        DrawString(b, atlas, Title, x + Pad, y + baseDrop, p.TextRgb, x + w - CloseW);
        DrawString(b, atlas, "×", x + w - CloseW + 5f, y + baseDrop, CloseRgb, x + w);

        foreach (SetRow r in Rows)
        {
            float labelX = x + Pad + (r.Channel >= 0 ? ChannelIndent : 0f);
            float labelClip = r.Kind == SetKind.Toggle ? x + w - CheckSize - Pad
                                                        : x + w * ControlX - 2f;
            DrawString(b, atlas, r.Label, labelX, r.Top + baseDrop + vpad, p.TextRgb, labelClip);

            switch (r.Kind)
            {
                case SetKind.Toggle:
                {
                    float cs = CheckSize;
                    float cbx = x + w - Pad - cs, cby = r.Top + (RowH - cs) / 2f;
                    b.Rect(cbx, cby, cbx + cs, cby + cs, CheckFrame.r, CheckFrame.g, CheckFrame.b, CheckFrame.a);
                    var fill = r.On ? p.Active : p.Body;
                    b.Rect(cbx + 1, cby + 1, cbx + cs - 1, cby + cs - 1, fill.r, fill.g, fill.b, 1f);
                    break;
                }
                case SetKind.Cycle:
                {
                    float bx0 = x + w * ControlX, bx1 = x + w - Pad;
                    bool editing = EditingRow(r);
                    var cbg = editing ? p.Active : p.TitleBar;
                    b.Rect(bx0, r.Top + 3, bx1, r.Top + RowH - 3, cbg.r, cbg.g, cbg.b, cbg.a);
                    string ctext = editing ? EditText : r.Value;
                    float cend = DrawString(b, atlas, ctext, bx0 + 4f, r.Top + baseDrop + vpad, p.TextRgb, bx1 - 2f);
                    if (editing)
                        b.Rect(cend, r.Top + 5f, cend + 1.5f, r.Top + RowH - 5f,
                               ((CaretRgb >> 16) & 0xFF) / 255f, ((CaretRgb >> 8) & 0xFF) / 255f, (CaretRgb & 0xFF) / 255f, 1f);
                    break;
                }
                case SetKind.Slider:
                {
                    SliderTrack(r, out float sx0, out float sx1, out float cy);
                    b.Rect(sx0, cy - 2f, sx1, cy + 2f, SbTrack.r, SbTrack.g, SbTrack.b, SbTrack.a);
                    float knob = sx0 + r.Norm * (sx1 - sx0);
                    b.Rect(sx0, cy - 2f, knob, cy + 2f, p.Active.r, p.Active.g, p.Active.b, p.Active.a);
                    b.Rect(knob - 2f, r.Top + 4f, knob + 2f, r.Top + RowH - 4f,
                           ((KnobRgb >> 16) & 0xFF) / 255f, ((KnobRgb >> 8) & 0xFF) / 255f, (KnobRgb & 0xFF) / 255f, 1f);
                    ValueBox(r, out float vx0, out float vx1, out float vy0, out float vy1);
                    bool editing = EditingRow(r);
                    var vbg = editing ? p.Active : p.TitleBar;
                    b.Rect(vx0, vy0, vx1, vy1, vbg.r, vbg.g, vbg.b, vbg.a);
                    string vtext = editing ? EditText : FormatValue(r);
                    float vend = DrawString(b, atlas, vtext, vx0 + 3f, r.Top + baseDrop + vpad, p.TextRgb, vx1 - 2f);
                    if (editing)
                        b.Rect(vend, vy0 + 2f, vend + 1.5f, vy1 - 2f,
                               ((CaretRgb >> 16) & 0xFF) / 255f, ((CaretRgb >> 8) & 0xFF) / 255f, (CaretRgb & 0xFF) / 255f, 1f);
                    break;
                }
                case SetKind.Swatch:
                {
                    float px0 = x + w - Pad - SwatchW, px1 = x + w - Pad;
                    float py0 = r.Top + 3f, py1 = r.Top + RowH - 3f;
                    b.Rect(px0 - 1f, py0 - 1f, px1 + 1f, py1 + 1f, CheckFrame.r, CheckFrame.g, CheckFrame.b, CheckFrame.a);
                    b.Rect(px0, py0, px1, py1, ((r.Rgba >> 24) & 0xFF) / 255f, ((r.Rgba >> 16) & 0xFF) / 255f,
                           ((r.Rgba >> 8) & 0xFF) / 255f, (r.Rgba & 0xFF) / 255f);
                    break;
                }
                case SetKind.Button:
                    break;   // label already drawn above; no control widget
            }
        }
    }

    /// <summary>Draw one clipped string, returning the pen x it ended at (the caret anchor).</summary>
    private static float DrawString(VertexBatch b, GlyphAtlas atlas, string s, float startX, float baseline,
                                    uint rgb, float clipX)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, bl = (rgb & 0xFF) / 255f;
        float penX = startX;
        foreach (System.Text.Rune ru in s.EnumerateRunes())
        {
            if (penX >= clipX) break;
            if (atlas.TryGet(ru.Value, out Glyph gl)) { b.Glyph(gl, penX, baseline, r, g, bl, 1f); penX += gl.Advance; }
            else penX += atlas.AdvanceOf(ru);
        }
        return penX;
    }

    /// <summary>Off-device proof of the panel's pure logic: hit-test-to-row (including the gaps between rows and
    /// the bottom pad), the close button, the title drag, the slider grab, the edit buffer, and open/close.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"SettingsPanel.SelfCheck FAIL: {w}"); } }

        // A synthetic consumer: three rows with a deliberate one-row gap between #1 and #2, recording callbacks.
        var pressed = new List<SetRow>();
        float applied = float.NaN;
        SetRow? appliedRow = null;
        string? committed = null;
        var panel = new SettingsPanel(264f);
        var toggle = new SetRow { Kind = SetKind.Toggle, Act = SetAct.ToggleBit, Label = "T", Height = RowH };
        var slider = new SetRow { Kind = SetKind.Slider, Act = SetAct.SliderHold, Label = "S", Height = RowH, Min = 0f, Max = 100f };
        var cycle = new SetRow { Kind = SetKind.Cycle, Act = SetAct.CycleFmt, Label = "C", Height = RowH };
        void Lay()
        {
            float y = panel.ContentTop;
            toggle.Top = y; y += RowH;
            y += RowH;                 // the gap: no row occupies this band
            slider.Top = y; y += RowH;
            cycle.Top = y; y += RowH;
            panel.Publish(new List<SetRow> { toggle, slider, cycle }, y);
        }
        panel.Relayout = Lay;
        panel.RowPressed = r => pressed.Add(r);
        panel.ApplyValue = (r, v) => { appliedRow = r; applied = v; r.Norm = r.Max > r.Min ? (v - r.Min) / (r.Max - r.Min) : 0f; };
        panel.CommitLabel = (r, t) => committed = t;

        panel.BeginOpen();
        panel.X = 100f; panel.Y = 50f;
        Lay();
        E(panel.Open && panel.Rows.Count == 3, "BeginOpen + Publish opens with the published rows");
        E(Math.Abs(panel.H - (4f * RowH + TitleH + Pad)) < 0.01f, "H = title + laid-out span + pad");
        E(panel.MouseUp(), "up after open is consumed (press ownership)");

        // Hit-test-to-row: a press inside a row's band reaches the consumer; the gap and the bottom pad do not.
        panel.MouseDown(panel.X + 5f, toggle.Top + 2f);
        E(pressed.Count == 1 && ReferenceEquals(pressed[0], toggle), "press inside a row resolves to that row");
        panel.MouseUp();
        panel.MouseDown(panel.X + 5f, toggle.Top + RowH + 2f);   // the gap band
        E(pressed.Count == 1 && panel.Open, "press in the gap between rows hits no row and stays open");
        panel.MouseUp();
        panel.MouseDown(panel.X + 5f, cycle.Top + RowH + 2f);    // inside the bottom pad strip
        E(pressed.Count == 1 && panel.Open, "press in the bottom pad hits no row and stays open");
        panel.MouseUp();
        panel.MouseDown(panel.X + panel.W + 5f, toggle.Top + 2f);// outside the panel horizontally
        E(pressed.Count == 1 && !panel.Open, "press outside the panel closes");

        // Close button: inside its rect closes; one pixel left of it is title bar (a move), not a close.
        panel.BeginOpen(); Lay(); panel.MouseUp();
        panel.MouseDown(panel.X + panel.W - CloseW - 2f, panel.Y + 2f);
        E(panel.Open && panel.Moving, "just left of the close button is a title drag, not a close");
        panel.MouseUp();
        panel.MouseDown(panel.X + panel.W - 2f, panel.Y + 2f);
        E(!panel.Open, "close-X closes");

        // Title drag moves the panel and every row with it (translate-only, #53).
        panel.BeginOpen(); Lay(); panel.MouseUp();
        float px = panel.X, py = panel.Y, topBefore = toggle.Top;
        E(panel.MouseDown(panel.X + panel.W / 2f, panel.Y + 2f), "title-bar press consumed");
        E(panel.Moving && panel.Dragging, "title press latches the move");
        panel.MouseMove(panel.X + panel.W / 2f + 40f, panel.Y + 2f + 25f);
        E(Math.Abs(panel.X - (px + 40f)) < 0.5f && Math.Abs(panel.Y - (py + 25f)) < 0.5f, "panel drags by the title bar");
        E(Math.Abs(toggle.Top - (topBefore + 25f)) < 0.5f, "rows follow the panel move");
        E(panel.MouseUp() && !panel.Moving, "panel move release consumed + ends the drag");

        // Slider: the track drives ApplyValue; the label zone (left of the track) leaves the value alone.
        panel.SliderTrack(slider, out float sx0, out float sx1, out float _);
        panel.MouseDown(sx1, slider.Top + 2f);
        E(appliedRow == slider && Math.Abs(applied - 100f) < 0.01f, "slider full-right -> max");
        panel.MouseMove((sx0 + sx1) / 2f, slider.Top + 2f);
        E(Math.Abs(applied - 50f) < 1f, "slider drag to mid -> ~half");
        panel.MouseUp();
        applied = float.NaN;
        panel.MouseDown(panel.X + 5f, slider.Top + 2f);
        E(float.IsNaN(applied), "press on a slider's label leaves the value untouched");
        panel.MouseUp();

        // Inline numeric entry: value box -> digits only -> commit / cancel / empty.
        void ClickValueBox()
        {
            panel.ValueBox(slider, out float vx0, out float vx1, out float vy0, out float vy1);
            panel.MouseDown((vx0 + vx1) / 2f, (vy0 + vy1) / 2f);
        }
        ClickValueBox();
        E(panel.Editing && panel.EditingRow(slider), "clicking a value box starts a field edit on that row");
        panel.EditInsert("4a2");
        E(panel.EditText == "42", "numeric mode filters non-digits");
        panel.EditBackspace();
        E(panel.EditText == "4", "backspace drops the last char");
        panel.EditInsert("2");
        panel.CommitEdit();
        E(!panel.Editing && Math.Abs(applied - 42f) < 0.01f, "typed value commits");
        applied = float.NaN;
        ClickValueBox(); panel.EditInsert("10"); panel.CancelEdit();
        E(!panel.Editing && float.IsNaN(applied), "cancel applies nothing");
        ClickValueBox(); panel.CommitEdit();
        E(float.IsNaN(applied), "empty commit applies nothing (keeps the old value)");
        ClickValueBox(); panel.EditInsert("77");
        panel.MouseDown(panel.X + 5f, cycle.Top + 2f);
        E(!panel.Editing && Math.Abs(applied - 77f) < 0.01f, "press elsewhere commits the in-progress edit");
        panel.MouseUp();

        // Label mode: broad char set, raw text handed to the consumer.
        panel.BeginLabelEdit(cycle);
        panel.EditInsert("Ab 1!");
        panel.CommitEdit();
        E(committed == "Ab 1!" && !panel.Editing, "label mode commits the raw typed text");

        // Reset clears open + every half-owned interaction.
        panel.BeginOpen(); Lay();
        panel.MouseDown(panel.X + panel.W / 2f, panel.Y + 2f);
        panel.Reset();
        E(!panel.Open && !panel.Moving && !panel.Dragging && !panel.Editing && !panel.MouseUp(),
          "Reset closes and drops press/drag/edit ownership");

        log.Info(ok ? "SettingsPanel.SelfCheck: passed" : "SettingsPanel.SelfCheck: FAILED");
    }
}
