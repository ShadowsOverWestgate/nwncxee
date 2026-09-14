using System;
using System.Collections.Generic;
using System.Globalization;
using CX.Framework;

namespace CX.Ui;

/// <summary>
/// The shared settings panel: a floating, title-draggable panel of typed <see cref="SetRow"/>s (checkboxes,
/// dropdowns, sliders with inline numeric entry, colour swatches, text boxes, buttons). It owns the panel
/// geometry -- position, metrics, the published row list, hit-test-to-row, the close-button hit, the title drag,
/// the slider grab and the text-entry buffer -- and the draw. The rows sit in one <see cref="ScrollColumn"/>
/// (the panel caps at <see cref="ViewportH"/> and scrolls past it), dropdown rows open the one
/// <see cref="Drop"/> list, and rows with <see cref="SetRow.Help"/> carry the <see cref="Tip"/> marker (#587).
/// A consumer supplies the rows (<see cref="Publish"/> after its own layout pass) plus four callbacks:
/// <see cref="Relayout"/>, <see cref="RowPressed"/>, <see cref="ApplyValue"/> and <see cref="CommitLabel"/>.
/// Pure -- pixels in, hit-tests + callbacks out, no native refs -- so it self-checks off-device.
/// ponytail: no theming, and one set of metric constants shared by every consumer (Chat's literals).
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

    /// <summary>The consumer's screen size: the panel never grows taller than <see cref="ViewportH"/> (its rows
    /// scroll instead), an open dropdown list flips above its row rather than run past it, and the tooltip box
    /// stays left of <see cref="ViewportW"/>. Unset = unbounded (the pre-#587 behaviour).</summary>
    public float ViewportW = float.MaxValue, ViewportH = float.MaxValue;

    /// <summary>The rows' scroll box: the band under the title bar down to the bottom pad. Its Offset is what
    /// separates a row's laid-out <see cref="SetRow.Top"/> from where it is drawn and hit.</summary>
    public readonly ScrollColumn Scroll = new();
    /// <summary>The one open-list instance every dropdown row shares; <see cref="Dropdown.Tag"/> is the row.</summary>
    public readonly Dropdown Drop = new();
    /// <summary>The help tooltip; hovered row = <see cref="Tooltip.Key"/>.</summary>
    public readonly Tooltip Tip = new();
    /// <summary>Rows a wheel notch scrolls the panel by.</summary>
    public const float WheelRows = 2f;

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
    /// <summary>Left inset of the control (slider track / dropdown box / swatch) -- labels sit left of it.</summary>
    public const float ControlX = 0.42f;
    /// <summary>Left inset of a <see cref="SetKind.Text"/> row's box. Text rows carry short labels and long free
    /// text (a tab name up to the 32-rune edit cap), so the box takes most of the row (#572).</summary>
    public const float TextControlX = 0.28f;
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

    /// <summary>Total panel height (title + laid-out rows + pad). Grows with the option list up to
    /// <see cref="ViewportH"/>; past that the rows scroll.</summary>
    public float H => _h;
    private float _contentH;

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
        _contentH = contentBottom - ContentTop;
        float full = TitleH + _contentH + Pad;
        _h = full > ViewportH ? ViewportH : full;
        _rows = rows;
        SyncScroll();
    }

    /// <summary>Re-seat the scroll box under the title bar (after a publish or a title-bar move).</summary>
    private void SyncScroll()
    {
        Scroll.ContentH = _contentH;
        Scroll.SetRect(X, ContentTop, W, _h - TitleH - Pad);
    }

    /// <summary>Screen y a row's top is drawn at (its laid-out <see cref="SetRow.Top"/> minus the scroll).</summary>
    public float RowTop(SetRow r) => r.Top - Scroll.Offset;

    /// <summary>Y the consumer's first row starts at (below the title bar).</summary>
    public float ContentTop => Y + TitleH;

    /// <summary>Enter the open state, clearing any half-owned press, drag or field edit. The consumer follows
    /// with its own layout + placement.</summary>
    public void BeginOpen()
    {
        Open = true; _pressConsumed = true; _grab = null; _moving = false;
        _editRow = null; _editText = ""; _editLabel = false;
        Scroll.Reset(); Drop.Close(); Tip.Leave();
    }

    /// <summary>Force-close at a session boundary, clearing any half-owned press, drag, or field edit.</summary>
    public void Reset()
    {
        Open = false; _pressConsumed = false; _grab = null; _moving = false; _editRow = null; _editText = ""; _editLabel = false;
        Scroll.Reset(); Drop.Close(); Tip.Leave();
    }

    // ---- geometry --------------------------------------------------------------------------------

    /// <summary>Slider track span + centre-y for a row (shared by draw + hit-test so they always agree). The
    /// track stops short of the right-edge value box.</summary>
    public void SliderTrack(SetRow r, out float x0, out float x1, out float cy)
    {
        float indent = r.Channel >= 0 ? ChannelIndent : 0f;
        x0 = X + W * ControlX + indent;
        x1 = X + W - Pad - ValueW - 4f;
        cy = RowTop(r) + RowH * 0.5f;
    }

    /// <summary>The slider value box rect (right edge of the row). Draw + hit-test share it.</summary>
    public void ValueBox(SetRow r, out float x0, out float x1, out float y0, out float y1)
    {
        x1 = X + W - Pad; x0 = x1 - ValueW;
        float top = RowTop(r);
        y0 = top + 3f; y1 = top + RowH - 3f;
    }

    /// <summary>The control box of a dropdown / text row (the value box right of the label column; a text row's
    /// column starts at <see cref="TextControlX"/>).</summary>
    public void ControlBox(SetRow r, out float x0, out float x1, out float y0, out float y1)
    {
        x0 = X + W * (r.Kind == SetKind.Text ? TextControlX : ControlX); x1 = X + W - Pad;
        float top = RowTop(r);
        y0 = top + 3f; y1 = top + RowH - 3f;
    }
    private bool OverValueBox(SetRow r, float mx, float my)
    { ValueBox(r, out float x0, out float x1, out float y0, out float y1); return mx >= x0 && mx <= x1 && my >= y0 && my <= y1; }

    public bool OverClose(float mx, float my)
        => mx >= X + W - CloseW && mx <= X + W && my >= Y && my <= Y + TitleH;

    /// <summary>Over the panel body or its open dropdown list.</summary>
    public bool Contains(float mx, float my)
        => (mx >= X && mx <= X + W && my >= Y && my <= Y + H) || Drop.Contains(mx, my);

    /// <summary>The row under a pointer: inside the scroll box, offset-corrected, and not over its gutter.</summary>
    private SetRow? RowAt(float mx, float my)
    {
        if (mx < X || mx > X + W) return null;
        if (my < Scroll.Y || my >= Scroll.Y + Scroll.H || Scroll.OverBar(mx, my)) return null;
        float cy = my + Scroll.Offset;
        foreach (SetRow r in Rows)
            if (cy >= r.Top && cy < r.Top + r.Height) return r;
        return null;
    }

    // ---- input -----------------------------------------------------------------------------------

    /// <summary>Pointer press while open. Always consumed. A slider hit starts a drag (see
    /// <see cref="MouseMove"/>) or a value-box edit; every other row goes to <see cref="RowPressed"/>. Close
    /// button or a click-off closes.</summary>
    public bool MouseDown(float mx, float my)
    {
        _pressConsumed = true; _grab = null; _moving = false;
        Tip.Leave();
        if (_editRow != null) CommitEdit();   // any press ends an in-progress field edit (commit-on-focus-loss)
        if (Drop.Open)
        {
            // The open list owns the press: an item picks (the row's Selected + RowPressed), the gutter scrolls,
            // anything else just closes it. Never falls through to the rows beneath.
            SetRow? dropRow = Drop.Tag as SetRow;
            Drop.MouseDown(mx, my, out int picked);
            if (picked >= 0 && dropRow != null)
            {
                dropRow.Selected = picked;
                RowPressed?.Invoke(dropRow);
                Relayout?.Invoke();
            }
            return true;
        }
        if (OverClose(mx, my)) { Open = false; return true; }
        // Title bar (above the first row) is the move handle -- drag the panel anywhere on-screen.
        if (my < Y + TitleH) { _moving = true; _mgx = mx - X; _mgy = my - Y; return true; }
        if (Scroll.MouseDown(mx, my)) return true;   // thumb grab / track page
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
        else if (row.Kind == SetKind.Dropdown)
        {
            if (row.Choices != null && row.Choices.Length > 0)
            {
                ControlBox(row, out float bx0, out float bx1, out float by0, out float by1);
                Drop.OpenAt(row.Choices, row.Selected, row, bx0, by0, by1, bx1 - bx0, RowH, ViewportH);
            }
            return true;   // the list is up; nothing changed yet
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

    /// <summary>True while a slider, a scrollbar thumb or the panel itself is being dragged.</summary>
    public bool Dragging => _grab != null || _moving || Scroll.Dragging || Drop.Dragging;

    /// <summary>Pointer motion: a title-bar move translates row positions in place without a full relayout
    /// (#53); a slider / thumb drag tracks the pointer; otherwise the dropdown hover and the tooltip marker
    /// hover update. True when the panel needs redrawing (a drag, or a hover change).</summary>
    public bool MouseMove(float mx, float my)
    {
        if (_moving)
        {
            float oldY = Y;
            X = mx - _mgx; Y = my - _mgy;
            float dy = Y - oldY;
            var rows = _rows;
            for (int i = 0; i < rows.Count; i++) rows[i].Top += dy;
            SyncScroll();
            return true;
        }
        if (Drop.Open) return Drop.MouseMove(mx, my);
        if (Scroll.MouseMove(mx, my)) return true;
        if (_grab != null) { SetSliderFromX(_grab, mx); return true; }
        return HoverTip(mx, my);
    }

    /// <summary>Tooltip hover: the pointer over a row's (?) marker (the span the last draw wrote) starts the
    /// dwell; anywhere else ends it. True when the hovered key changed.</summary>
    private bool HoverTip(float mx, float my)
    {
        SetRow? row = RowAt(mx, my);
        if (row != null && row.Help.Length > 0 && row.TipW > 0f && mx >= row.TipX && mx < row.TipX + row.TipW)
            return Tip.Hover(row, row.Help, row.TipX, RowTop(row), RowTop(row) + RowH);
        return Tip.Leave();
    }

    /// <summary>Wheel notch while open: scrolls the open dropdown list, else the rows. True when it moved.</summary>
    public bool MouseWheel(int dy)
    {
        Tip.Leave();
        if (Drop.Open) return Drop.MouseWheel(dy);
        return Scroll.MouseWheel(dy, RowH * WheelRows);
    }

    /// <summary>Pointer release. Consumed iff we owned the press. Ends any slide / thumb drag.</summary>
    public bool MouseUp()
    {
        bool c = _pressConsumed;
        _pressConsumed = false; _grab = null; _moving = false;
        Scroll.MouseUp(); Drop.MouseUp();
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
    /// out -- checkboxes, dropdown boxes, sliders, colour swatches, text boxes -- clipped to the scroll box, then
    /// the scrollbar, the open dropdown list and the tooltip on top. Reads the rows' current values (the consumer
    /// already resolved them from its config), so it never touches the config itself. Zero-alloc except a
    /// slider row's <see cref="FormatValue"/> string. <paramref name="baseDrop"/> is the text baseline offset
    /// from a row's top.</summary>
    public void Emit(VertexBatch b, GlyphAtlas atlas, float baseDrop, Palette p)
    {
        float x = X, y = Y, w = W;
        float vpad = (RowH - atlas.LineHeight) / 2f;
        float tipW = Tooltip.MarkerW(atlas);

        b.Rect(x, y, x + w, y + H, p.Body.r, p.Body.g, p.Body.b, 1f);
        b.Rect(x, y, x + w, y + TitleH, p.TitleBar.r, p.TitleBar.g, p.TitleBar.b, 1f);
        DrawString(b, atlas, Title, x + Pad, y + baseDrop, p.TextRgb, x + w - CloseW);
        DrawString(b, atlas, "×", x + w - CloseW + 5f, y + baseDrop, CloseRgb, x + w);

        float clip0 = Scroll.Y, clip1 = Scroll.Y + Scroll.H;
        b.ClipY(clip0, clip1);
        foreach (SetRow r in Rows)
        {
            float top = RowTop(r);
            if (top + r.Height <= clip0 || top >= clip1) continue;   // scrolled out of the box
            float labelX = x + Pad + (r.Channel >= 0 ? ChannelIndent : 0f);
            float labelClip = r.Kind == SetKind.Toggle ? x + w - CheckSize - Pad
                            : r.Kind == SetKind.Text ? x + w * TextControlX - 2f
                                                     : x + w * ControlX - 2f;
            bool help = r.Help.Length > 0;
            if (help) labelClip -= tipW + 4f;
            float labelEnd = DrawString(b, atlas, r.Label, labelX, top + baseDrop + vpad, p.TextRgb, labelClip);
            if (help)
            {
                r.TipX = labelEnd + 4f; r.TipW = tipW;
                Tooltip.DrawMarker(b, atlas, r.TipX, top + baseDrop + vpad, p.TextRgb);
            }
            else r.TipW = 0f;

            switch (r.Kind)
            {
                case SetKind.Toggle:
                {
                    float cs = CheckSize;
                    float cbx = x + w - Pad - cs, cby = top + (RowH - cs) / 2f;
                    b.Rect(cbx, cby, cbx + cs, cby + cs, CheckFrame.r, CheckFrame.g, CheckFrame.b, CheckFrame.a);
                    var fill = r.On ? p.Active : p.Body;
                    b.Rect(cbx + 1, cby + 1, cbx + cs - 1, cby + cs - 1, fill.r, fill.g, fill.b, 1f);
                    break;
                }
                case SetKind.Dropdown:
                {
                    ControlBox(r, out float bx0, out float bx1, out float by0, out float by1);
                    bool open = Drop.Open && ReferenceEquals(Drop.Tag, r);
                    string cur = r.Choices != null && (uint)r.Selected < (uint)r.Choices.Length ? r.Choices[r.Selected] : "";
                    Dropdown.DrawBox(b, atlas, bx0, by0, bx1, by1, top + baseDrop + vpad, cur, open ? p.Active : p.TitleBar, p.TextRgb);
                    break;
                }
                case SetKind.Text:
                {
                    ControlBox(r, out float bx0, out float bx1, out float by0, out float by1);
                    bool editing = EditingRow(r);
                    var cbg = editing ? p.Active : p.TitleBar;
                    b.Rect(bx0, by0, bx1, by1, cbg.r, cbg.g, cbg.b, cbg.a);
                    string ctext = editing ? EditText : r.Value;
                    float cend = DrawString(b, atlas, ctext, bx0 + 4f, top + baseDrop + vpad, p.TextRgb, bx1 - 2f);
                    if (editing)
                        b.Rect(cend, top + 5f, cend + 1.5f, top + RowH - 5f,
                               ((CaretRgb >> 16) & 0xFF) / 255f, ((CaretRgb >> 8) & 0xFF) / 255f, (CaretRgb & 0xFF) / 255f, 1f);
                    break;
                }
                case SetKind.Slider:
                {
                    SliderTrack(r, out float sx0, out float sx1, out float cy);
                    b.Rect(sx0, cy - 2f, sx1, cy + 2f, SbTrack.r, SbTrack.g, SbTrack.b, SbTrack.a);
                    float knob = sx0 + r.Norm * (sx1 - sx0);
                    b.Rect(sx0, cy - 2f, knob, cy + 2f, p.Active.r, p.Active.g, p.Active.b, p.Active.a);
                    b.Rect(knob - 2f, top + 4f, knob + 2f, top + RowH - 4f,
                           ((KnobRgb >> 16) & 0xFF) / 255f, ((KnobRgb >> 8) & 0xFF) / 255f, (KnobRgb & 0xFF) / 255f, 1f);
                    ValueBox(r, out float vx0, out float vx1, out float vy0, out float vy1);
                    bool editing = EditingRow(r);
                    var vbg = editing ? p.Active : p.TitleBar;
                    b.Rect(vx0, vy0, vx1, vy1, vbg.r, vbg.g, vbg.b, vbg.a);
                    string vtext = editing ? EditText : FormatValue(r);
                    float vend = DrawString(b, atlas, vtext, vx0 + 3f, top + baseDrop + vpad, p.TextRgb, vx1 - 2f);
                    if (editing)
                        b.Rect(vend, vy0 + 2f, vend + 1.5f, vy1 - 2f,
                               ((CaretRgb >> 16) & 0xFF) / 255f, ((CaretRgb >> 8) & 0xFF) / 255f, (CaretRgb & 0xFF) / 255f, 1f);
                    break;
                }
                case SetKind.Swatch:
                {
                    float px0 = x + w - Pad - SwatchW, px1 = x + w - Pad;
                    float py0 = top + 3f, py1 = top + RowH - 3f;
                    b.Rect(px0 - 1f, py0 - 1f, px1 + 1f, py1 + 1f, CheckFrame.r, CheckFrame.g, CheckFrame.b, CheckFrame.a);
                    b.Rect(px0, py0, px1, py1, ((r.Rgba >> 24) & 0xFF) / 255f, ((r.Rgba >> 16) & 0xFF) / 255f,
                           ((r.Rgba >> 8) & 0xFF) / 255f, (r.Rgba & 0xFF) / 255f);
                    break;
                }
                case SetKind.Button:
                    break;   // label already drawn above; no control widget
            }
        }
        b.ClearClip();
        Scroll.Draw(b, p.Active);
        Drop.Draw(b, atlas, baseDrop, p.TitleBar, p.Active, p.TextRgb);
        Tip.Draw(b, atlas, baseDrop, p.TitleBar, p.TextRgb, ViewportW, ViewportH);
    }

    /// <summary>Draw one clipped string, returning the pen x it ended at (the caret anchor).</summary>
    private static float DrawString(VertexBatch b, GlyphAtlas atlas, string s, float startX, float baseline,
                                    uint rgb, float clipX)
        => UiText.Draw(b, atlas, s, startX, baseline, rgb, clipX);

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
        var cycle = new SetRow { Kind = SetKind.Text, Act = SetAct.TabLabel, Label = "C", Height = RowH };
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

        // ---- #587: scroll column on 60 rows -- wheel, thumb drag, offset-correct hits ----
        var tall = new SettingsPanel(264f) { ViewportH = TitleH + 10f * RowH + Pad };
        var tallRows = new List<SetRow>();
        var tallPressed = new List<SetRow>();
        void LayTall()
        {
            tallRows.Clear();
            float y = tall.ContentTop;
            for (int i = 0; i < 60; i++)
            {
                tallRows.Add(new SetRow { Kind = SetKind.Toggle, Act = SetAct.ToggleBit, Label = "row", Top = y, Height = RowH, Bit = (uint)i });
                y += RowH;
            }
            tall.Publish(new List<SetRow>(tallRows), y);
        }
        tall.Relayout = LayTall;
        tall.RowPressed = r => tallPressed.Add(r);
        tall.BeginOpen(); tall.X = 10f; tall.Y = 10f; LayTall(); tall.MouseUp();
        E(Math.Abs(tall.H - tall.ViewportH) < 0.01f, "60 rows: the panel caps at ViewportH");
        E(tall.Scroll.Overflows && Math.Abs(tall.Scroll.MaxOffset - 50f * RowH) < 0.01f, "50 rows lie past the box");
        float rowY = tall.ContentTop + 2f;
        tall.MouseDown(tall.X + 5f, rowY); tall.MouseUp();
        E(tallPressed.Count == 1 && tallPressed[0].Bit == 0, "at offset 0 the first band is row 0");
        E(tall.MouseWheel(-1) && Math.Abs(tall.Scroll.Offset - WheelRows * RowH) < 0.01f, "a notch scrolls WheelRows rows");
        tall.MouseDown(tall.X + 5f, rowY); tall.MouseUp();
        E(tallPressed.Count == 2 && tallPressed[1].Bit == (uint)WheelRows, "after a notch the same band is row WheelRows (offset-correct hit)");
        E(Math.Abs(tall.RowTop(tall.Rows[(int)WheelRows]) - tall.ContentTop) < 0.01f, "RowTop of the scrolled-to row is the box top");
        // Thumb drag: grab and pull to the track bottom -> last row visible + hit at the box bottom.
        E(tall.Scroll.ThumbRect(out float tTop, out float tH), "the panel shows a thumb");
        float gutterX = tall.X + tall.W - 2f;
        E(tall.MouseDown(gutterX, tTop + 1f) && tall.Dragging && tall.Scroll.Dragging, "gutter press on the thumb grabs it");
        tall.MouseMove(gutterX, tTop + 1f + tall.Scroll.H);
        E(tall.MouseUp() && Math.Abs(tall.Scroll.Offset - tall.Scroll.MaxOffset) < 0.01f, "dragging the thumb to the bottom scrolls to the end");
        tall.MouseDown(tall.X + 5f, tall.Scroll.Y + tall.Scroll.H - 2f); tall.MouseUp();
        E(tallPressed.Count == 3 && tallPressed[2].Bit == 59, "the box bottom now hits row 59");
        tall.MouseDown(gutterX, tall.Scroll.Y + tall.Scroll.H - 2f); tall.MouseUp();
        E(tallPressed.Count == 3, "a gutter press is the bar's, not a row's");
        tall.MouseDown(tall.X + 5f, tall.Y + tall.H + 5f);
        E(!tall.Open, "a press below the capped panel is outside it");

        // ---- #587: a 27-entry dropdown row opens, scrolls, picks, closes on outside press ----
        var ladder = new string[27];
        for (int i = 0; i < 27; i++) ladder[i] = (6 + i).ToString(CultureInfo.InvariantCulture);
        var dp = new SettingsPanel(264f) { ViewportH = 800f };
        var ddRow = new SetRow { Kind = SetKind.Dropdown, Act = SetAct.SliderFont, Label = "Font", Height = RowH, Choices = ladder, Selected = 12 };
        var tipRow = new SetRow { Kind = SetKind.Toggle, Act = SetAct.OutlineOn, Label = "Outline", Height = RowH, Help = "Draw a dark halo around the text." };
        SetRow? ddPressed = null; int relayouts = 0;
        void LayDd()
        {
            float y = dp.ContentTop;
            ddRow.Top = y; y += RowH;
            tipRow.Top = y; y += RowH;
            dp.Publish(new List<SetRow> { ddRow, tipRow }, y);
            relayouts++;
        }
        dp.Relayout = LayDd;
        dp.RowPressed = r => ddPressed = r;
        dp.BeginOpen(); dp.X = 100f; dp.Y = 100f; LayDd(); dp.MouseUp();
        E(dp.MouseDown(dp.X + 5f, ddRow.Top + 2f) && dp.Drop.Open && ReferenceEquals(dp.Drop.Tag, ddRow), "a press on a dropdown row opens its list");
        E(ddPressed == null, "opening changes nothing yet");
        dp.MouseUp();
        dp.ControlBox(ddRow, out float cbx0, out float cbx1, out float _, out float cby1);
        E(Math.Abs(dp.Drop.X - cbx0) < 0.01f && Math.Abs(dp.Drop.Y - cby1) < 0.01f && Math.Abs(dp.Drop.W - (cbx1 - cbx0)) < 0.01f,
          "the list hangs under the row's control box");
        E(dp.Drop.H == Dropdown.MaxVisible * RowH && dp.Drop.List.Overflows, "27 entries: 8 visible, the rest scroll");
        E(dp.Contains(dp.Drop.X + 2f, dp.Drop.Y + dp.Drop.H - 2f), "the open list counts as the panel for hover/over-UI");
        float off0 = dp.Drop.List.Offset;
        E(off0 > 0f && dp.Drop.ItemTop(12) >= dp.Drop.Y && dp.Drop.ItemTop(12) + RowH <= dp.Drop.Y + dp.Drop.H, "the list opens scrolled so the selected entry is in view");
        E(dp.MouseWheel(-1) && Math.Abs(dp.Drop.List.Offset - (off0 + RowH)) < 0.01f && dp.Scroll.Offset == 0f, "the wheel scrolls the open list, not the panel");
        E(dp.MouseMove(dp.Drop.X + 5f, dp.Drop.ItemTop(14) + 2f) && dp.Drop.Hover == 14, "motion over an item highlights it");
        int rl = relayouts;
        E(dp.MouseDown(dp.Drop.X + 5f, dp.Drop.ItemTop(14) + 2f), "item press consumed");
        E(!dp.Drop.Open && ddRow.Selected == 14 && ReferenceEquals(ddPressed, ddRow) && relayouts == rl + 1,
          "a pick writes Selected, fires RowPressed, relayouts, closes");
        dp.MouseUp();
        ddPressed = null;
        dp.MouseDown(dp.X + 5f, ddRow.Top + 2f); dp.MouseUp();
        E(dp.Drop.Open, "re-opened");
        E(dp.MouseDown(dp.X + 5f, tipRow.Top + 2f) && !dp.Drop.Open && ddPressed == null && dp.Open,
          "an outside press closes the list, is swallowed, and reaches no row beneath");
        dp.MouseUp();
        dp.MouseDown(dp.X + 5f, ddRow.Top + 2f); dp.MouseUp();
        E(dp.MouseDown(dp.X + dp.W + 200f, dp.Y + dp.H + 200f) && !dp.Drop.Open && dp.Open, "a press off the panel closes the list first, not the panel");
        dp.MouseUp();

        // ---- #587: tooltip -- the (?) marker beside the label shows after the dwell, never grabs anything ----
        long fakeNow = 0;
        Func<long> realClock = Tooltip.Clock;
        Tooltip.Clock = () => fakeNow;
        try
        {
            var atlasT = new GlyphAtlas { LineHeight = 18, Ascent = 14, SpaceAdvance = 4f };
            atlasT.SetTestGlyph(' ', 4f);
            for (char ch = 'A'; ch <= 'z'; ch++) atlasT.SetTestGlyph(ch, 7f);
            foreach (char ch in "(?)×.") atlasT.SetTestGlyph(ch, 7f);
            var vb = new VertexBatch(atlasT.SolidU, atlasT.SolidV);
            var pal = new Palette { Body = (0.1f, 0.1f, 0.1f, 1f), TitleBar = (0.2f, 0.2f, 0.2f, 1f), Active = (0.2f, 0.3f, 0.5f, 1f), TextRgb = 0xFFFFFF };
            E(tipRow.TipW == 0f, "before a draw the marker has no span (no hover possible)");
            dp.Emit(vb, atlasT, 14f, pal);
            E(tipRow.TipW > 0f && ddRow.TipW == 0f, "the draw places a marker on the help row only");
            E(Math.Abs(tipRow.TipX - (dp.X + Pad + 7f * "Outline".Length + 4f)) < 0.01f, "the marker sits right after the label");
            float mkx = tipRow.TipX + 1f, mky = tipRow.Top + 2f;
            E(dp.MouseMove(mkx, mky) && dp.Tip.Hovering && !dp.Tip.Visible, "hovering the marker starts the dwell");
            E(!dp.Tip.Tick(), "not due yet");
            E(!dp.Dragging && !dp.Editing, "hover grabs nothing");
            fakeNow = (long)(Tooltip.DelaySec * System.Diagnostics.Stopwatch.Frequency);
            E(dp.Tip.Visible && dp.Tip.Tick() && !dp.Tip.Tick(), "after the dwell it is visible and Tick fires once");
            E(!dp.MouseMove(mkx + 1f, mky), "motion within the marker changes nothing");
            E(dp.MouseMove(dp.X + 5f, mky) && !dp.Tip.Hovering, "leaving the marker hides it");
            dp.MouseMove(mkx, mky); fakeNow *= 2;
            E(dp.Tip.Visible, "re-hovered and due");
            dp.MouseDown(mkx, mky);
            E(!dp.Tip.Hovering && dp.Open, "a press dismisses the tooltip and the row beneath still gets it");
            dp.MouseUp();

            // ---- #587: the draw with a visible tooltip, with an open list, and with a scrolled column allocates nothing ----
            dp.MouseMove(mkx, mky); fakeNow *= 2;
            E(dp.Tip.Visible, "pre: tooltip due");
            dp.Emit(vb, atlasT, 14f, pal);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { vb.Clear(); dp.Emit(vb, atlasT, 14f, pal); }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            E(delta == 0, $"100 panel draws with the tooltip up allocate nothing ({delta} bytes)");
            dp.MouseDown(dp.X + 5f, ddRow.Top + 2f); dp.MouseUp();   // list open
            dp.MouseMove(dp.Drop.X + 5f, dp.Drop.Y + 2f);             // an item hovered
            E(dp.Drop.Open && dp.Drop.Hover >= 0, "pre: list open with a hovered item");
            dp.Emit(vb, atlasT, 14f, pal);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { vb.Clear(); dp.Emit(vb, atlasT, 14f, pal); }
            delta = GC.GetAllocatedBytesForCurrentThread() - before;
            E(delta == 0, $"100 panel draws with the list open allocate nothing ({delta} bytes)");
            tall.BeginOpen(); LayTall(); tall.MouseWheel(-3);
            tall.Emit(vb, atlasT, 14f, pal);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { vb.Clear(); tall.Emit(vb, atlasT, 14f, pal); }
            delta = GC.GetAllocatedBytesForCurrentThread() - before;
            E(delta == 0, $"100 scrolled-column draws allocate nothing ({delta} bytes)");
            // The clip: nothing drawn above the box top or below its bottom.
            vb.Clear(); tall.Emit(vb, atlasT, 14f, pal);
            float[] verts = vb.ToArray();
            bool clipped = true;
            float boxTop = tall.Scroll.Y, boxBot = tall.Scroll.Y + tall.Scroll.H;
            for (int v = 0; v < vb.VertexCount; v++)
            {
                float vy = verts[v * VertexBatch.Floats + 1];
                if (vy < tall.Y - 0.01f || vy > tall.Y + tall.H + 0.01f) clipped = false;
            }
            E(clipped && boxTop < boxBot, "every vertex of a scrolled panel lies within the panel rect (rows clipped to the box)");
        }
        finally { Tooltip.Clock = realClock; }

        log.Info(ok ? "SettingsPanel.SelfCheck: passed" : "SettingsPanel.SelfCheck: FAILED");
    }
}
