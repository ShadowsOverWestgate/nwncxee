using System;
using System.Collections.Generic;
using System.Globalization;
using CX.Framework;
using static CX.Ui.PanelChrome;

namespace CX.Ui;

/// <summary>
/// The per-window settings panel (CX-683): the main panel's design (<see cref="KnobPanel"/> -- header, the
/// <see cref="PanelChrome"/> palette, em metrics on its own body / small faces, a row's help wrapped under its
/// label, right-aligned controls) with ONE category column. It owns the geometry -- placement, the header drag,
/// the two scroll columns, the row layout (<see cref="Publish"/> lays the consumer's rows out), hit-test-to-row,
/// the open <see cref="Drop"/> list, the slider grab and the text-entry buffer -- and the draw. A consumer
/// supplies <see cref="Categories"/> and the rows of the chosen one, plus the callbacks: <see cref="Relayout"/>,
/// <see cref="CategoryPicked"/>, <see cref="RowPressed"/>, <see cref="ApplyValue"/>, <see cref="CommitLabel"/>.
/// Pure -- pixels in, hit-tests + callbacks out, no native refs -- so it self-checks off-device.
/// </summary>
public sealed class SettingsPanel
{
    /// <summary>Design face heights before the GUI scale: the main panel's.</summary>
    public const float FacePx = KnobPanel.FacePx, SmallPx = KnobPanel.SmallPx;
    /// <summary>Panel size as a fraction of the viewport, the category column as a fraction of the panel.</summary>
    public const float WFrac = 0.52f, HFrac = 0.66f, CatFrac = 0.30f;
    /// <summary>Rows one wheel notch scrolls a column by, in em.</summary>
    public const float WheelEm = 3.3f;

    public bool Open;
    public float X, Y, W, H;
    /// <summary>The viewport the panel was laid out for.</summary>
    public float VpW, VpH;
    /// <summary>The body face height in px: every metric is a multiple of it.</summary>
    public float Em { get; private set; } = FacePx;
    /// <summary>The faces the layout measured with and the draw uses; the consumer re-fetches them by atlas epoch.</summary>
    public GlyphAtlas? Body, Small;
    /// <summary>Title-bar caption.</summary>
    public string Title = "Settings";
    /// <summary>A button in the header, left of the close ×; "" = none. Its press runs <see cref="HeaderActionPressed"/>.</summary>
    public string HeaderAction = "";
    public Action? HeaderActionPressed;

    /// <summary>The category column's entries and the chosen one; the consumer sets both (a press on an entry
    /// goes through <see cref="CategoryPicked"/> first, so the consumer may redirect it).</summary>
    public string[] Categories = Array.Empty<string>();
    public int Category;

    public readonly ScrollColumn Cats = new(), List = new();
    /// <summary>The one open-list instance every dropdown row shares; <see cref="Dropdown.Tag"/> is the row.</summary>
    public readonly Dropdown Drop = new();

    /// <summary>The laid-out rows; the draw walks these, hit-testing reads them. <see cref="Publish"/> swaps in a
    /// fresh list (never mutates in place), so a reader on another thread iterates a stable snapshot.</summary>
    private volatile List<SetRow> _rows = new();
    public List<SetRow> Rows => _rows;

    private bool _pressConsumed;
    private SetRow? _grab;         // the slider being dragged (mouse-down..up), a live row in the current list
    private bool _moving;          // dragging the panel by its header
    private float _mgx, _mgy;      // move-grab anchor: pointer offset from (X,Y) at grab
    private SetRow? _editRow;      // inline text entry (a slider's value box, or a consumer-started label edit)
    private string _editText = "";
    private bool _editLabel;       // true = free text (broad char set), false = numeric (digits only)
    private int _hoverCat = -1;
    private SetRow? _hoverRow;
    private float _lastMx, _lastMy;

    // ---- consumer contract -----------------------------------------------------------------------

    /// <summary>Rebuild the row list from the live model (the consumer's layout pass, ending in
    /// <see cref="Publish"/>). Called after every row press so the change shows next frame.</summary>
    public Action? Relayout;
    /// <summary>A category entry was pressed. The consumer sets <see cref="Category"/> (the default when unset:
    /// the pressed index) and mutates its model; the panel then re-lays out.</summary>
    public Action<int>? CategoryPicked;
    /// <summary>A press landed on a non-slider row: the consumer mutates its model. Slider rows never reach
    /// here -- the panel owns the track drag and the value box itself.</summary>
    public Action<SetRow>? RowPressed;
    /// <summary>Write a raw value for a slider/channel row (already in the row's Min..Max units) onto the
    /// consumer's model, and mirror it back onto the row's draw state.</summary>
    public Action<SetRow, float>? ApplyValue;
    /// <summary>Commit a free-text edit (the consumer decides what empty means).</summary>
    public Action<SetRow, string>? CommitLabel;

    // ---- em metrics ----
    private float HeaderH => 2.7f * Em;
    private float Pad => 1.2f * Em;
    private float NavPadX => 1f * Em;
    private float NavH => BodyLh + 1.2f * Em;
    private float HeadH => BodyLh + 1.2f * Em;
    private float RowMinH => 3.3f * Em;
    private float RowPlainH => 2.4f * Em;
    private float ChanH => 2f * Em;
    private float RowPadY => 0.4f * Em;
    private float SecH => SmallLh + 1.3f * Em;
    private float BodyLh => Body?.LineHeight ?? Em * 1.2f;
    private float SmallLh => Small?.LineHeight ?? Em;
    private float CloseW => 1.7f * Em;
    private float HeaderBtnH => 1.7f * Em;
    private float HeaderBtnW => (Body != null ? UiText.Measure(Body, HeaderAction) : 6f * Em) + 1.8f * Em;
    private float ToggleW => 2.6f * Em;
    private float ToggleH => 1.3f * Em;
    private float TrackW => 8f * Em;
    private float ValueW => 3.6f * Em;
    private float TextW => 13f * Em;
    private float SwatchW => 2.4f * Em;
    private float CtlH => 1.6f * Em;
    private float Gap => 0.6f * Em;
    private float ChanIndent => 1.4f * Em;

    // ---- open / close / place ----

    /// <summary>Open centred in a <paramref name="vpW"/>×<paramref name="vpH"/> viewport with the faces the
    /// consumer baked at <paramref name="em"/>, clearing any half-owned press, drag or field edit. The consumer
    /// follows with its own layout (<see cref="Publish"/>).</summary>
    public void OpenAt(float vpW, float vpH, float em, GlyphAtlas? body, GlyphAtlas? small)
    {
        Open = true; _pressConsumed = true; _grab = null; _moving = false;
        _editRow = null; _editText = ""; _editLabel = false; _hoverRow = null; _hoverCat = -1;
        Cats.Reset(); List.Reset(); Drop.Close();
        VpW = vpW; VpH = vpH; Em = em; Body = body; Small = small;
        W = MathF.Round(vpW * WFrac); H = MathF.Round(vpH * HFrac);
        X = MathF.Round((vpW - W) / 2f); Y = MathF.Round((vpH - H) / 2f);
        Place();
    }

    /// <summary>Force-close at a session boundary, clearing any half-owned press, drag, or field edit.</summary>
    public void Reset()
    {
        Open = false; _pressConsumed = false; _grab = null; _moving = false; _editRow = null; _editText = ""; _editLabel = false;
        _hoverRow = null; _hoverCat = -1;
        Cats.Reset(); List.Reset(); Drop.Close();
    }

    /// <summary>Seat the two columns under the header for the current X/Y.</summary>
    private void Place()
    {
        float top = Y + HeaderH, h = H - HeaderH;
        float cw = MathF.Round(W * CatFrac);
        Cats.SetRect(X, top, cw, h);
        List.SetRect(X + cw, top + HeadH, W - cw, h - HeadH);
    }

    /// <summary>Lay the consumer's rows out top to bottom for the current faces (control widths, wrapped help,
    /// row heights) and publish the list.</summary>
    public void Publish(List<SetRow> rows)
    {
        Cats.ContentH = 0.4f * Em + Categories.Length * NavH;
        Cats.Clamp();
        float y = 0.4f * Em;
        float inner = ListInnerW();
        for (int i = 0; i < rows.Count; i++)
        {
            SetRow r = rows[i];
            r.Top = y;
            r.CtlW = ControlW(r);
            if (r.Kind == SetKind.Section) { r.Height = SecH; r.HelpLines = Array.Empty<string>(); }
            else if (r.Channel >= 0) { r.Height = ChanH; r.HelpLines = Array.Empty<string>(); }
            else
            {
                float labelW = inner - r.CtlW - Gap;
                r.HelpLines = r.Help.Length > 0 ? KnobPanel.Wrap(Small, r.Help, labelW < 4f * Em ? 4f * Em : labelW) : Array.Empty<string>();
                float h = 2f * RowPadY + BodyLh + (r.HelpLines.Length > 0 ? 0.1f * Em + r.HelpLines.Length * SmallLh : 0f);
                float min = r.HelpLines.Length > 0 ? RowMinH : RowPlainH;
                r.Height = h < min ? min : h;
            }
            y += r.Height;
        }
        List.ContentH = y + 1.5f * Em;
        List.Clamp();
        _rows = rows;
    }

    /// <summary>The row column's inner width: side pads and the gutter off.</summary>
    private float ListInnerW() => List.W - 2f * Pad - ScrollColumn.BarW;

    /// <summary>Control width per kind, at layout: the label clip and the row's hit zones derive from it.</summary>
    private float ControlW(SetRow r)
    {
        switch (r.Kind)
        {
            case SetKind.Toggle: return ToggleW;
            case SetKind.Slider: return TrackW + Gap + ValueW;
            case SetKind.Dropdown:
            {
                float w = 0f;
                if (Body != null && r.Choices != null)
                    for (int i = 0; i < r.Choices.Length; i++) { float cw = UiText.Measure(Body, r.Choices[i]); if (cw > w) w = cw; }
                w += 2.6f * Em;
                float min = 9f * Em, max = 14f * Em;
                return w < min ? min : w > max ? max : w;
            }
            case SetKind.Swatch: return SwatchW;
            case SetKind.Button: return (Body != null ? UiText.Measure(Body, r.Label) : 6f * Em) + 1.8f * Em;
            case SetKind.Text: return TextW;
            default: return 0f;
        }
    }

    // ---- geometry shared by draw and hit-test ----

    public bool Contains(float mx, float my)
        => (mx >= X && mx < X + W && my >= Y && my < Y + H) || Drop.Contains(mx, my);

    public bool OverClose(float mx, float my)
        => mx >= X + W - Pad - CloseW && mx < X + W - Pad + 0.2f * Em && my >= Y && my < Y + HeaderH;

    /// <summary>The header button's box (right of the title, left of the ×).</summary>
    public void HeaderButtonRect(out float x0, out float x1, out float y0, out float y1)
    {
        x1 = X + W - Pad - CloseW - 0.6f * Em; x0 = x1 - HeaderBtnW;
        float mid = Y + HeaderH * 0.5f;
        y0 = mid - HeaderBtnH * 0.5f; y1 = mid + HeaderBtnH * 0.5f;
    }

    private bool OverHeaderButton(float mx, float my)
    {
        if (HeaderAction.Length == 0) return false;
        HeaderButtonRect(out float x0, out float x1, out float y0, out float y1);
        return In(mx, my, x0, x1, y0, y1);
    }

    private float NavTop(int i) => Cats.ToScreen(0.4f * Em + i * NavH);

    private int NavAt(float mx, float my)
    {
        if (!Cats.Contains(mx, my) || Cats.OverBar(mx, my)) return -1;
        int i = (int)((Cats.ToContent(my) - 0.4f * Em) / NavH);
        return i >= 0 && i < Categories.Length ? i : -1;
    }

    /// <summary>Screen y a row's top is drawn at.</summary>
    public float RowTop(SetRow r) => List.ToScreen(r.Top);

    /// <summary>The row under a pointer: inside the list box, offset-corrected, and not over its gutter.</summary>
    private SetRow? RowAt(float mx, float my)
    {
        if (!List.Contains(mx, my) || List.OverBar(mx, my)) return null;
        float cy = List.ToContent(my);
        var rows = _rows;
        for (int i = 0; i < rows.Count; i++)
            if (cy >= rows[i].Top && cy < rows[i].Top + rows[i].Height) return rows[i];
        return null;
    }

    private float RowLeft(SetRow r) => List.X + Pad + (r.Channel >= 0 ? ChanIndent : 0f);

    /// <summary>Right edge of a row's control span and its vertical centre (the label line, or a channel row's middle).</summary>
    private void ControlAnchor(SetRow r, out float right, out float cy)
    {
        right = List.X + List.W - Pad - ScrollColumn.BarW;
        float top = RowTop(r);
        cy = r.Channel >= 0 ? top + r.Height * 0.5f : top + RowPadY + BodyLh * 0.5f;
    }

    /// <summary>Slider track span + centre-y for a row (shared by draw + hit-test so they always agree).</summary>
    public void SliderTrack(SetRow r, out float x0, out float x1, out float cy)
    {
        ControlAnchor(r, out float right, out cy);
        x0 = right - r.CtlW; x1 = x0 + TrackW;
    }

    /// <summary>The slider value box rect (right of the track). Draw + hit-test share it.</summary>
    public void ValueBox(SetRow r, out float x0, out float x1, out float y0, out float y1)
    {
        ControlAnchor(r, out float right, out float cy);
        x0 = right - r.CtlW + TrackW + Gap; x1 = x0 + ValueW;
        y0 = cy - CtlH * 0.5f; y1 = cy + CtlH * 0.5f;
    }

    /// <summary>The one box of a dropdown / text / button / swatch control (the right-aligned span).</summary>
    public void ControlBox(SetRow r, out float x0, out float x1, out float y0, out float y1)
    {
        ControlAnchor(r, out float right, out float cy);
        x1 = right; x0 = right - r.CtlW;
        y0 = cy - CtlH * 0.5f; y1 = cy + CtlH * 0.5f;
    }

    private static bool In(float mx, float my, float x0, float x1, float y0, float y1) => mx >= x0 && mx < x1 && my >= y0 && my < y1;

    // ---- input -----------------------------------------------------------------------------------

    /// <summary>Pointer press while open. Always consumed. A category entry picks; a slider hit starts a drag
    /// (see <see cref="MouseMove"/>) or a value-box edit; a dropdown box opens the list; every other row goes to
    /// <see cref="RowPressed"/>. Close button or a click-off closes.</summary>
    public bool MouseDown(float mx, float my)
    {
        _pressConsumed = true; _grab = null; _moving = false;
        if (_editRow != null) CommitEdit();   // any press ends an in-progress field edit (commit-on-focus-loss)
        if (Drop.Open)
        {
            // The open list owns the press (pick / scroll / close); it never falls through to the rows beneath.
            SetRow? dropRow = Drop.Tag as SetRow;
            Drop.MouseDown(mx, my, out int picked);
            if (picked >= 0 && dropRow != null) Pick(dropRow, picked);
            return true;
        }
        if (OverClose(mx, my)) { Open = false; return true; }
        if (!Contains(mx, my)) { Open = false; return true; }
        if (OverHeaderButton(mx, my)) { HeaderActionPressed?.Invoke(); return true; }
        if (my < Y + HeaderH) { _moving = true; _mgx = mx - X; _mgy = my - Y; return true; }
        if (Cats.MouseDown(mx, my) || List.MouseDown(mx, my)) return true;   // thumb grab / track page
        int c = NavAt(mx, my);
        if (c >= 0)
        {
            if (CategoryPicked != null) CategoryPicked(c); else Category = c;
            List.Reset();
            Relayout?.Invoke();
            return true;
        }
        SetRow? row = RowAt(mx, my);
        if (row == null) return true;

        switch (row.Kind)
        {
            case SetKind.Slider:
            {
                ValueBox(row, out float vx0, out float vx1, out float vy0, out float vy1);
                if (In(mx, my, vx0, vx1, vy0, vy1)) { BeginEdit(row); return true; }
                SliderTrack(row, out float sx0, out float sx1, out float cy);
                if (mx >= sx0 - 0.4f * Em && mx < sx1 + 0.4f * Em && my >= cy - Em && my < cy + Em) { _grab = row; SetSliderFromX(row, mx); }
                return true;
            }
            case SetKind.Dropdown:
            {
                ControlBox(row, out float bx0, out float bx1, out float by0, out float by1);
                if (row.Choices != null && row.Choices.Length > 0 && In(mx, my, bx0, bx1, by0, by1))
                    Drop.OpenAt(row.Choices, row.Selected, row, bx0, by0, by1, bx1 - bx0, CtlH, VpH, search: row.Choices.Length > Dropdown.MaxVisible);
                return true;   // the list is up; nothing changed yet
            }
            case SetKind.Button:
            {
                ControlBox(row, out float bx0, out float bx1, out float by0, out float by1);
                if (!In(mx, my, bx0, bx1, by0, by1)) return true;
                break;
            }
            case SetKind.Section:
                return true;
        }
        RowPressed?.Invoke(row);
        Relayout?.Invoke();
        // Relayout published a fresh list: re-resolve the edited row into it (a vanished row ends the edit).
        if (_editRow != null) _editRow = _rows.Find(r => r.Act == _editRow.Act && r.Key == _editRow.Key && r.Channel == _editRow.Channel);
        return true;
    }

    /// <summary>A pick from the open list: the row's Selected, then the consumer, then a relayout.</summary>
    private void Pick(SetRow row, int i)
    {
        row.Selected = i;
        RowPressed?.Invoke(row);
        Relayout?.Invoke();
    }

    /// <summary>True while the panel is being dragged by its header (not a slider grab).</summary>
    public bool Moving => _moving;

    /// <summary>True while a slider is being dragged: the consumer's model changes per motion.</summary>
    public bool SliderDragging => _grab != null;

    /// <summary>True while a slider, a scrollbar thumb or the panel itself is being dragged.</summary>
    public bool Dragging => _grab != null || _moving || Cats.Dragging || List.Dragging || Drop.Dragging;

    /// <summary>Pointer motion: a header drag moves the panel (clamped to the viewport); a slider / thumb drag
    /// tracks the pointer; the open list hovers; otherwise the category and row hovers update. True when the
    /// panel needs redrawing.</summary>
    public bool MouseMove(float mx, float my)
    {
        _lastMx = mx; _lastMy = my;
        if (_moving)
        {
            X = Window.ClampAxis(mx - _mgx, W, VpW); Y = Window.ClampAxis(my - _mgy, H, VpH);
            Place();
            return true;
        }
        if (Drop.Open) return Drop.MouseMove(mx, my);
        if (Cats.MouseMove(mx, my) || List.MouseMove(mx, my)) return true;
        if (_grab != null) { SetSliderFromX(_grab, mx); return true; }
        int hc = NavAt(mx, my);
        bool changed = hc != _hoverCat;
        _hoverCat = hc;
        SetRow? r = RowAt(mx, my);
        if (!ReferenceEquals(r, _hoverRow)) { _hoverRow = r; changed = true; }
        return changed;
    }

    /// <summary>Wheel notch while open: the open dropdown list, else the category column under the pointer,
    /// else the rows. True when it moved.</summary>
    public bool MouseWheel(int dy)
    {
        if (Drop.Open) return Drop.MouseWheel(dy);
        float step = WheelEm * Em;
        if (Cats.Contains(_lastMx, _lastMy)) return Cats.MouseWheel(dy, step);
        return List.MouseWheel(dy, step);
    }

    /// <summary>Pointer release. Consumed iff we owned the press. Ends any slide / thumb drag.</summary>
    public bool MouseUp()
    {
        bool c = _pressConsumed;
        _pressConsumed = false; _grab = null; _moving = false;
        Cats.MouseUp(); List.MouseUp(); Drop.MouseUp();
        return c;
    }

    private void SetSliderFromX(SetRow r, float mx)
    {
        SliderTrack(r, out float x0, out float x1, out float _);
        float norm = x1 > x0 ? Clamp01((mx - x0) / (x1 - x0)) : 0f;
        ApplyValue?.Invoke(r, r.Min + norm * (r.Max - r.Min));
    }

    // ---- inline text entry -----------------------------------------------------------------------

    /// <summary>True while the panel wants typed text: a value box or label row is being typed into, or an open
    /// searchable list is filtering (the shell routes keys/text here).</summary>
    public bool Editing => _editRow != null || Drop.Searching;
    /// <summary>Is <paramref name="r"/> the row being text-edited? (the draw shows the buffer + a caret).</summary>
    public bool EditingRow(SetRow r) => ReferenceEquals(_editRow, r);
    /// <summary>The in-progress edit buffer (the draw shows it in the active box).</summary>
    public string EditText => _editText;

    /// <summary>Start a numeric edit on a slider's value box: an empty buffer, the knob still at the old value.</summary>
    private void BeginEdit(SetRow r) { _editRow = r; _editText = ""; _editLabel = false; _grab = null; }

    /// <summary>Start a free-text edit on a row (the consumer calls this from <see cref="RowPressed"/>).</summary>
    public void BeginLabelEdit(SetRow r) { _editRow = r; _editText = ""; _editLabel = true; _grab = null; }

    /// <summary>Append typed characters. A searching list takes them as its filter; label mode accepts printable
    /// chars (capped at 32); numeric mode accepts digits/dot/sign (capped at 8).</summary>
    public void EditInsert(string s)
    {
        if (Drop.Searching) { Drop.Type(s); return; }
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
    {
        if (Drop.Searching) { Drop.Backspace(); return; }
        if (_editRow != null && _editText.Length > 0) _editText = _editText.Substring(0, _editText.Length - 1);
    }

    /// <summary>Commit the typed value: a searching list accepts its top match; label mode hands the raw text to
    /// <see cref="CommitLabel"/>; numeric mode parses it and hands the value to <see cref="ApplyValue"/>
    /// (unparseable text keeps the old value).</summary>
    public void CommitEdit()
    {
        if (Drop.Searching)
        {
            SetRow? dr = Drop.Tag as SetRow;
            int i = Drop.Accept();
            if (i >= 0 && dr != null) Pick(dr, i);
            return;
        }
        SetRow? r = _editRow;
        string t = _editText;
        bool label = _editLabel;
        _editRow = null; _editText = ""; _editLabel = false;
        if (r == null) return;
        if (label) CommitLabel?.Invoke(r, t);
        else if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            ApplyValue?.Invoke(r, v);
    }

    /// <summary>Escape: backs out of an edit, else closes an open list.</summary>
    public void CancelEdit()
    {
        if (_editRow != null) { _editRow = null; _editText = ""; _editLabel = false; return; }
        if (Drop.Open) Drop.Close();
    }

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

    /// <summary>Draw the panel: the chrome, the category column, the row column head (the chosen category's
    /// name) and the rows clipped to their boxes, the scrollbars, then the open dropdown list on top. Reads the
    /// rows' current values (the consumer already resolved them from its model). Zero-alloc while idle (a slider
    /// row re-formats its value text only when its Norm moved). Nothing without faces.</summary>
    public void Emit(VertexBatch b)
    {
        if (!Open || Body == null || Small == null) return;
        GlyphAtlas body = Body, small = Small;
        float x = X, y = Y, w = W, h = H, em = Em;

        Fill(b, x, y, x + w, y + h, PanelBg, 0.96f);
        Outline(b, x, y, x + w, y + h, Border);
        Fill(b, x + 1f, y + 1f, x + w - 1f, y + HeaderH, HeaderBg);
        Fill(b, x + 1f, y + HeaderH - 1f, x + w - 1f, y + HeaderH, Border);
        float hbase = y + (HeaderH - body.LineHeight) * 0.5f + body.Ascent;
        UiText.Draw(b, body, Title, x + Pad, hbase, Tx, x + w * 0.6f);
        float closeX1 = x + w - Pad + 0.2f * em, closeX0 = closeX1 - CloseW;
        UiText.Draw(b, body, "×", closeX0 + (CloseW - UiText.Measure(body, "×")) * 0.5f, hbase, Grip, closeX1);
        if (HeaderAction.Length > 0)
        {
            HeaderButtonRect(out float hx0, out float hx1, out float hy0, out float hy1);
            Fill(b, hx0, hy0, hx1, hy1, NavOn);
            Outline(b, hx0, hy0, hx1, hy1, Frame2);
            UiText.Draw(b, body, HeaderAction, hx0 + 0.9f * em, hbase, Tx, hx1 - 0.4f * em);
        }

        // categories
        Fill(b, Cats.X, Cats.Y, Cats.X + Cats.W, Cats.Y + Cats.H, CatBg);
        Fill(b, Cats.X + Cats.W - 1f, Cats.Y, Cats.X + Cats.W, Cats.Y + Cats.H, ColBorder);
        b.ClipY(Cats.Y, Cats.Y + Cats.H);
        var cats = Categories;
        float navX1 = Cats.X + Cats.W - (Cats.Overflows ? ScrollColumn.BarW : 0f);
        for (int i = 0; i < cats.Length; i++)
        {
            float top = NavTop(i);
            if (top + NavH <= Cats.Y || top >= Cats.Y + Cats.H) continue;
            bool on = i == Category;
            if (on) { Fill(b, Cats.X, top, navX1, top + NavH, NavOn); Fill(b, Cats.X, top, Cats.X + 3f, top + NavH, Accent2); }
            else if (i == _hoverCat) Fill(b, Cats.X, top, navX1, top + NavH, 0xFFFFFF, 0.03f);
            UiText.Draw(b, body, cats[i], Cats.X + NavPadX, top + (NavH - body.LineHeight) * 0.5f + body.Ascent, on ? Hi : Tx2, navX1 - 0.3f * em);
        }
        b.ClearClip();
        Cats.Draw(b, C(Accent));

        // row column head: the chosen category's name
        float kx = List.X, headTop = List.Y - HeadH;
        if ((uint)Category < (uint)cats.Length)
            UiText.Draw(b, body, cats[Category], kx + Pad, headTop + 0.6f * em + body.Ascent, Tx, kx + List.W - Pad);
        Fill(b, kx, List.Y - 1f, kx + List.W, List.Y, ColBorder);

        // rows
        float clip0 = List.Y, clip1 = List.Y + List.H;
        float colRight = kx + List.W - Pad - ScrollColumn.BarW;
        b.ClipY(clip0, clip1);
        var rows = _rows;
        for (int i = 0; i < rows.Count; i++)
        {
            SetRow r = rows[i];
            float top = RowTop(r);
            if (top + r.Height <= clip0 || top >= clip1) continue;
            if (r.Kind == SetKind.Section)
            {
                UiText.Draw(b, small, r.Label, kx + Pad, top + 1f * em + small.Ascent, Tx3, colRight);
                continue;
            }
            float left = RowLeft(r);
            ControlAnchor(r, out float right, out float cy);
            Fill(b, kx + Pad, top + r.Height - 1f, colRight, top + r.Height, 0xFFFFFF, 0.03f);
            float ctlX0 = right - r.CtlW;
            float labelClip = r.Kind == SetKind.Button ? right : ctlX0 - Gap;
            if (r.Kind != SetKind.Button)
            {
                UiText.Draw(b, body, r.Label, left, cy - body.LineHeight * 0.5f + body.Ascent, r.Channel >= 0 ? Tx2 : Tx, labelClip);
                float helpBase = top + RowPadY + body.LineHeight + 0.1f * em + small.Ascent;
                for (int l = 0; l < r.HelpLines.Length; l++)
                    UiText.Draw(b, small, r.HelpLines[l], left, helpBase + l * small.LineHeight, Tx2, colRight);
            }

            switch (r.Kind)
            {
                case SetKind.Toggle:
                {
                    float tx0 = right - ToggleW, ty0 = cy - ToggleH * 0.5f, ty1 = cy + ToggleH * 0.5f;
                    Fill(b, tx0, ty0, right, ty1, r.On ? Accent : ToggleOff);
                    Outline(b, tx0, ty0, right, ty1, r.On ? Accent2 : Frame);
                    float kd = ToggleH - 0.35f * em, kx0 = r.On ? right - 0.17f * em - kd : tx0 + 0.17f * em;
                    Fill(b, kx0, cy - kd * 0.5f, kx0 + kd, cy + kd * 0.5f, r.On ? Hi : Grip);
                    break;
                }
                case SetKind.Slider:
                {
                    SliderTrack(r, out float sx0, out float sx1, out _);
                    float th = 0.35f * em;
                    Fill(b, sx0, cy - th * 0.5f, sx1, cy + th * 0.5f, Track);
                    Outline(b, sx0, cy - th * 0.5f, sx1, cy + th * 0.5f, Frame2);
                    float knob = sx0 + r.Norm * (sx1 - sx0);
                    Fill(b, sx0, cy - th * 0.5f, knob, cy + th * 0.5f, Accent);
                    Fill(b, knob - 0.375f * em, cy - 0.55f * em, knob + 0.375f * em, cy + 0.55f * em, Grip);
                    ValueBox(r, out float vx0, out float vx1, out float vy0, out float vy1);
                    bool editing = EditingRow(r);
                    Fill(b, vx0, vy0, vx1, vy1, Input);
                    Outline(b, vx0, vy0, vx1, vy1, editing ? Accent2 : Frame2);
                    if (r.ValueNorm != r.Norm) { r.ValueText = FormatValue(r); r.ValueNorm = r.Norm; }
                    string vtext = editing ? _editText : r.ValueText;
                    float vbase = cy - body.LineHeight * 0.5f + body.Ascent;
                    float vw = UiText.Measure(body, vtext);
                    float vx = editing ? vx0 + 0.3f * em : vx1 - 0.3f * em - vw;
                    if (vx < vx0 + 0.2f * em) vx = vx0 + 0.2f * em;
                    float vend = UiText.Draw(b, body, vtext, vx, vbase, Tx, vx1 - 0.2f * em);
                    if (editing) Fill(b, vend, vy0 + 0.2f * em, vend + 1.5f, vy1 - 0.2f * em, Hi);
                    break;
                }
                case SetKind.Dropdown:
                {
                    ControlBox(r, out float bx0, out float bx1, out float by0, out float by1);
                    bool open = Drop.Open && ReferenceEquals(Drop.Tag, r);
                    string cur = r.Choices != null && (uint)r.Selected < (uint)r.Choices.Length ? r.Choices[r.Selected] : "";
                    Dropdown.DrawBox(b, body, bx0, by0, bx1, by1, cy - body.LineHeight * 0.5f + body.Ascent, cur, C(Input), Tx);
                    Outline(b, bx0, by0, bx1, by1, open ? Accent2 : Frame2);
                    break;
                }
                case SetKind.Text:
                {
                    ControlBox(r, out float bx0, out float bx1, out float by0, out float by1);
                    bool editing = EditingRow(r);
                    Fill(b, bx0, by0, bx1, by1, Input);
                    Outline(b, bx0, by0, bx1, by1, editing ? Accent2 : Frame2);
                    float tend = UiText.Draw(b, body, editing ? _editText : r.Value, bx0 + 0.4f * em, cy - body.LineHeight * 0.5f + body.Ascent, Tx, bx1 - 0.3f * em);
                    if (editing) Fill(b, tend, by0 + 0.2f * em, tend + 1.5f, by1 - 0.2f * em, Hi);
                    break;
                }
                case SetKind.Swatch:
                {
                    ControlBox(r, out float bx0, out float bx1, out float by0, out float by1);
                    Fill(b, bx0, by0, bx1, by1, r.Rgba >> 8, (r.Rgba & 0xFF) / 255f);
                    Outline(b, bx0, by0, bx1, by1, Frame);
                    break;
                }
                case SetKind.Button:
                {
                    ControlBox(r, out float bx0, out float bx1, out float by0, out float by1);
                    Fill(b, bx0, by0, bx1, by1, NavOn);
                    Outline(b, bx0, by0, bx1, by1, ReferenceEquals(r, _hoverRow) ? Accent2 : Frame2);
                    UiText.Draw(b, body, r.Label, bx0 + 0.9f * em, cy - body.LineHeight * 0.5f + body.Ascent, Tx, bx1 - 0.4f * em);
                    break;
                }
            }
        }
        b.ClearClip();
        List.Draw(b, C(Accent));
        Drop.Draw(b, body, body.Ascent, C(ListBg), C(Selected), Tx);
    }

    /// <summary>Off-device proof of the panel's pure logic: layout + hit-test-to-row, the category column, the
    /// close button, the header drag, each control's hit zone, the edit buffer, the searchable list, the scroll
    /// columns, and a zero-alloc draw.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"SettingsPanel.SelfCheck FAIL: {w}"); } }

        var body = new GlyphAtlas { LineHeight = 24, Ascent = 19, SpaceAdvance = 6f };
        var small = new GlyphAtlas { LineHeight = 19, Ascent = 15, SpaceAdvance = 5f };
        for (int cp = 0x20; cp < 0x180; cp++) { body.SetTestGlyph(cp, cp == ' ' ? 6f : 11f); small.SetTestGlyph(cp, cp == ' ' ? 5f : 8f); }
        body.SetTestGlyph(0x25BE, 11f); small.SetTestGlyph(0x25BE, 8f);

        // A synthetic consumer: three categories, a toggle, a slider, a text row, a 27-choice dropdown, a button
        // and a section; every callback recorded.
        var pressed = new List<SetRow>();
        var picked = new List<int>();
        float applied = float.NaN; SetRow? appliedRow = null; string? committed = null; int relayouts = 0;
        var ladder = new string[27];
        for (int i = 0; i < 27; i++) ladder[i] = (6 + i).ToString(CultureInfo.InvariantCulture);
        var panel = new SettingsPanel { Categories = new[] { "Window settings", "All", "Add new tab..." } };
        var toggle = new SetRow { Kind = SetKind.Toggle, Act = SetAct.ToggleBit, Label = "T", Help = "A help line long enough to wrap onto a second line at least once." };
        var slider = new SetRow { Kind = SetKind.Slider, Act = SetAct.SliderHold, Label = "S", Min = 0f, Max = 100f };
        var text = new SetRow { Kind = SetKind.Text, Act = SetAct.TabLabel, Label = "C", Value = "old" };
        var dd = new SetRow { Kind = SetKind.Dropdown, Act = SetAct.SliderFont, Label = "Font", Choices = ladder, Selected = 12 };
        var btn = new SetRow { Kind = SetKind.Button, Act = SetAct.CopyFrom, Label = "Copy" };
        void Lay()
        {
            var rows = panel.Category == 0
                ? new List<SetRow> { new SetRow { Kind = SetKind.Section, Label = "LOOK" }, toggle, slider, text, dd, btn }
                : new List<SetRow> { text };
            panel.Publish(rows);
            relayouts++;
        }
        panel.Relayout = Lay;
        panel.RowPressed = r => pressed.Add(r);
        panel.CategoryPicked = c => { picked.Add(c); panel.Category = c == 2 ? 1 : c; };
        panel.ApplyValue = (r, v) => { appliedRow = r; applied = v; r.Norm = r.Max > r.Min ? (v - r.Min) / (r.Max - r.Min) : 0f; };
        panel.CommitLabel = (r, t) => committed = t;

        panel.OpenAt(1280f, 800f, 20f, body, small);
        Lay();
        E(panel.Open && panel.Rows.Count == 6, "OpenAt + Publish opens with the published rows");
        E(Math.Abs(panel.W - MathF.Round(1280f * WFrac)) < 0.01f && Math.Abs(panel.X - MathF.Round((1280f - panel.W) / 2f)) < 0.01f, "sized and centred in the viewport");
        E(toggle.HelpLines.Length >= 2 && toggle.Height > slider.Height && slider.Height == panel.RowPlainH, "help wraps and grows its row; a plain row is compact");
        E(panel.MouseUp(), "up after open is consumed (press ownership)");

        // Hit-test-to-row: a press anywhere on a toggle row reaches the consumer; the pad above the rows does not.
        panel.MouseDown(panel.List.X + 5f, panel.RowTop(toggle) + 2f);
        E(pressed.Count == 1 && ReferenceEquals(pressed[0], toggle) && relayouts == 2, "press on a toggle row resolves to it and relayouts");
        panel.MouseUp();
        panel.MouseDown(panel.List.X + 5f, panel.List.Y + 1f);
        E(pressed.Count == 1 && panel.Open, "press in the top pad hits no row and stays open");
        panel.MouseUp();
        panel.MouseDown(panel.List.X + 5f, panel.RowTop(panel.Rows[0]) + 2f);
        E(pressed.Count == 1, "a section header is inert");
        panel.MouseUp();
        panel.MouseDown(panel.X + panel.W + 5f, panel.Y + panel.H / 2f);
        E(pressed.Count == 1 && !panel.Open, "press outside the panel closes");

        // Categories: a press picks through the consumer (which may redirect), and relayouts.
        panel.OpenAt(1280f, 800f, 20f, body, small); Lay(); panel.MouseUp();
        panel.MouseDown(panel.Cats.X + 5f, panel.NavTop(1) + 2f); panel.MouseUp();
        E(picked.Count == 1 && picked[0] == 1 && panel.Category == 1 && panel.Rows.Count == 1, "a category press picks it and lays its rows out");
        panel.MouseDown(panel.Cats.X + 5f, panel.NavTop(2) + 2f); panel.MouseUp();
        E(picked[^1] == 2 && panel.Category == 1, "the consumer may redirect a pick (Add new tab... -> the new tab)");
        panel.MouseDown(panel.Cats.X + 5f, panel.NavTop(0) + 2f); panel.MouseUp();
        E(panel.Category == 0 && panel.Rows.Count == 6, "back to the first category");
        panel.MouseDown(panel.Cats.X + 5f, panel.NavTop(3) + 2f); panel.MouseUp();
        E(picked.Count == 3 && panel.Open, "a press below the last entry picks nothing");

        // Header button: its box fires the action; the header around it is the drag handle.
        int headerFires = 0;
        panel.HeaderAction = "Copy"; panel.HeaderActionPressed = () => headerFires++;
        panel.HeaderButtonRect(out float hbx0, out float hbx1, out float hby0, out float hby1);
        panel.MouseDown((hbx0 + hbx1) / 2f, (hby0 + hby1) / 2f); panel.MouseUp();
        E(headerFires == 1 && panel.Open && !panel.Moving, "the header button fires its action, no drag, stays open");
        panel.MouseDown(hbx0 - 2f, (hby0 + hby1) / 2f);
        E(headerFires == 1 && panel.Moving, "left of the button is the drag handle");
        panel.MouseUp();
        panel.HeaderAction = "";

        // Close button: inside its rect closes; left of it is the header (a move), not a close.
        panel.MouseDown(panel.X + panel.W - panel.Pad - panel.CloseW - 2f, panel.Y + 2f);
        E(panel.Open && panel.Moving, "just left of the close button is a header drag, not a close");
        panel.MouseUp();
        panel.MouseDown(panel.X + panel.W - panel.Pad - 2f, panel.Y + 2f);
        E(!panel.Open, "close-X closes");

        // Header drag moves the panel and its columns; clamped to the viewport.
        panel.OpenAt(1280f, 800f, 20f, body, small); Lay(); panel.MouseUp();
        float px = panel.X, py = panel.Y, listY = panel.List.Y;
        E(panel.MouseDown(panel.X + panel.W / 2f, panel.Y + 2f) && panel.Moving && panel.Dragging, "header press latches the move");
        panel.MouseMove(panel.X + panel.W / 2f + 40f, panel.Y + 2f + 25f);
        E(Math.Abs(panel.X - (px + 40f)) < 0.5f && Math.Abs(panel.Y - (py + 25f)) < 0.5f && Math.Abs(panel.List.Y - (listY + 25f)) < 0.5f, "panel and columns drag by the header");
        panel.MouseMove(-500f, -500f);
        E(panel.X == 0f && panel.Y == 0f, "the drag clamps to the viewport");
        E(panel.MouseUp() && !panel.Moving, "release ends the drag");

        // Slider: the track drives ApplyValue; the label zone leaves the value alone; the value box edits.
        panel.SliderTrack(slider, out float sx0, out float sx1, out float scy);
        panel.MouseDown(sx1 - 0.1f, scy);
        E(appliedRow == slider && Math.Abs(applied - 100f) < 0.5f, "slider full-right -> max");
        panel.MouseMove((sx0 + sx1) / 2f, scy);
        E(Math.Abs(applied - 50f) < 1f, "slider drag to mid -> ~half");
        panel.MouseUp();
        applied = float.NaN;
        panel.MouseDown(panel.List.X + 5f, panel.RowTop(slider) + 2f);
        E(float.IsNaN(applied) && pressed.Count == 1, "press on a slider's label changes nothing");
        panel.MouseUp();
        void ClickValueBox()
        {
            panel.ValueBox(slider, out float vx0, out float vx1, out float vy0, out float vy1);
            panel.MouseDown((vx0 + vx1) / 2f, (vy0 + vy1) / 2f);
        }
        ClickValueBox();
        E(panel.Editing && panel.EditingRow(slider), "clicking a value box starts a field edit on that row");
        panel.EditInsert("4a2");
        E(panel.EditText == "42", "numeric mode filters non-digits");
        panel.EditBackspace(); panel.EditInsert("2"); panel.CommitEdit();
        E(!panel.Editing && Math.Abs(applied - 42f) < 0.01f, "typed value commits");
        applied = float.NaN;
        ClickValueBox(); panel.EditInsert("10"); panel.CancelEdit();
        E(!panel.Editing && float.IsNaN(applied), "cancel applies nothing");
        ClickValueBox(); panel.CommitEdit();
        E(float.IsNaN(applied), "empty commit applies nothing");
        ClickValueBox(); panel.EditInsert("77");
        panel.MouseDown(panel.List.X + 5f, panel.RowTop(toggle) + 2f);
        E(!panel.Editing && Math.Abs(applied - 77f) < 0.01f, "press elsewhere commits the in-progress edit");
        panel.MouseUp();

        // Text row: a press reaches the consumer, which starts a label edit; broad char set, raw text committed.
        panel.MouseDown(panel.List.X + 5f, panel.RowTop(text) + 2f); panel.MouseUp();
        E(pressed[^1] == text, "a text row press reaches the consumer");
        panel.BeginLabelEdit(text);
        panel.EditInsert("Ab 1!"); panel.CommitEdit();
        E(committed == "Ab 1!" && !panel.Editing, "label mode commits the raw typed text");

        // Button: only its box presses.
        int n = pressed.Count;
        panel.MouseDown(panel.List.X + 5f, panel.RowTop(btn) + 2f); panel.MouseUp();
        E(pressed.Count == n, "a press left of a button's box is nothing");
        panel.ControlBox(btn, out float bbx0, out float bbx1, out float bby0, out float bby1);
        panel.MouseDown((bbx0 + bbx1) / 2f, (bby0 + bby1) / 2f); panel.MouseUp();
        E(pressed.Count == n + 1 && pressed[^1] == btn, "a press in the box fires the button");

        // Dropdown (27 entries): the box opens a searchable list; the wheel scrolls it; a pick sets Selected.
        panel.MouseDown(panel.List.X + 5f, panel.RowTop(dd) + 2f); panel.MouseUp();
        E(!panel.Drop.Open, "a press on a dropdown's label opens nothing");
        panel.ControlBox(dd, out float dx0, out float dx1, out float dy0, out float dy1);
        panel.MouseDown((dx0 + dx1) / 2f, (dy0 + dy1) / 2f); panel.MouseUp();
        E(panel.Drop.Open && ReferenceEquals(panel.Drop.Tag, dd) && panel.Drop.Searching && panel.Editing, "the box opens the list; 27 entries search, so the panel wants text");
        E(panel.Drop.List.Overflows && panel.Contains(panel.Drop.X + 2f, panel.Drop.Y + panel.Drop.H - 2f), "the list scrolls and counts as the panel");
        float off0 = panel.Drop.List.Offset;
        E(panel.MouseWheel(-1) && Math.Abs(panel.Drop.List.Offset - (off0 + panel.Drop.ItemH)) < 0.01f && panel.List.Offset == 0f, "the wheel scrolls the open list, not the rows");
        panel.EditInsert("10");
        E(panel.Drop.Filter == "10" && panel.Drop.Shown < 27, "typed text filters the list");
        int rl = relayouts;
        panel.CommitEdit();
        E(!panel.Drop.Open && dd.Selected == 4 && pressed[^1] == dd && relayouts == rl + 1, "Enter accepts the exact match (10), fires RowPressed, relayouts");
        panel.MouseDown((dx0 + dx1) / 2f, (dy0 + dy1) / 2f); panel.MouseUp();
        panel.Drop.List.Offset = 0f;
        panel.MouseDown(panel.Drop.X + 5f, panel.Drop.ItemTop(2) + 2f); panel.MouseUp();
        E(!panel.Drop.Open && dd.Selected == 2, "an item press picks it");
        panel.MouseDown((dx0 + dx1) / 2f, (dy0 + dy1) / 2f); panel.MouseUp();
        n = pressed.Count;
        panel.MouseDown(panel.List.X + 5f, panel.RowTop(toggle) + 2f); panel.MouseUp();
        E(!panel.Drop.Open && pressed.Count == n && panel.Open, "an outside press closes the list, is swallowed, and reaches no row beneath");
        panel.MouseDown((dx0 + dx1) / 2f, (dy0 + dy1) / 2f); panel.MouseUp();
        panel.CancelEdit();
        E(!panel.Drop.Open && !panel.Editing, "Escape closes an open list");

        // Reset clears open + every half-owned interaction.
        panel.MouseDown(panel.X + panel.W / 2f, panel.Y + 2f);
        panel.Reset();
        E(!panel.Open && !panel.Moving && !panel.Dragging && !panel.Editing && !panel.MouseUp(), "Reset closes and drops press/drag/edit ownership");

        // Scroll column on 60 rows in a short viewport: wheel, thumb drag, offset-correct hits.
        var tall = new SettingsPanel { Categories = new[] { "A" } };
        var tallPressed = new List<SetRow>();
        void LayTall()
        {
            var rows = new List<SetRow>();
            for (int i = 0; i < 60; i++) rows.Add(new SetRow { Kind = SetKind.Toggle, Act = SetAct.ToggleBit, Label = "row", Bit = (uint)i });
            tall.Publish(rows);
        }
        tall.Relayout = LayTall;
        tall.RowPressed = r => tallPressed.Add(r);
        tall.OpenAt(1280f, 400f, 20f, body, small); LayTall(); tall.MouseUp();
        E(tall.List.Overflows, "60 rows overflow a 400 px viewport");
        float rowY = tall.List.Y + 0.4f * tall.Em + 2f;
        tall.MouseDown(tall.List.X + 5f, rowY); tall.MouseUp();
        E(tallPressed.Count == 1 && tallPressed[0].Bit == 0, "at offset 0 the first band is row 0");
        tall.MouseMove(tall.List.X + 5f, rowY);
        E(tall.MouseWheel(-1) && Math.Abs(tall.List.Offset - WheelEm * tall.Em) < 0.01f, "a notch scrolls WheelEm em");
        tall.MouseDown(tall.List.X + 5f, rowY); tall.MouseUp();
        E(tallPressed.Count == 2 && tallPressed[1].Bit == 1, "after a notch the same band is the next row (offset-correct hit)");
        E(tall.List.ThumbRect(out float tTop, out float _), "the list shows a thumb");
        float gutterX = tall.List.X + tall.List.W - 2f;
        E(tall.MouseDown(gutterX, tTop + 1f) && tall.Dragging && tall.List.Dragging, "gutter press on the thumb grabs it");
        tall.MouseMove(gutterX, tTop + 1f + tall.List.H);
        E(tall.MouseUp() && Math.Abs(tall.List.Offset - tall.List.MaxOffset) < 0.01f, "dragging the thumb to the bottom scrolls to the end");
        tall.MouseDown(tall.List.X + 5f, tall.List.Y + tall.List.H - 1.5f * tall.Em - 2f); tall.MouseUp();
        E(tallPressed.Count == 3 && tallPressed[2].Bit == 59, "the box bottom (above its pad) now hits row 59");
        tall.MouseDown(gutterX, tall.List.Y + tall.List.H - 2f); tall.MouseUp();
        E(tallPressed.Count == 3, "a gutter press is the bar's, not a row's");

        // The draw with the list open, and a scrolled column, allocates nothing and stays inside the panel.
        panel.OpenAt(1280f, 800f, 20f, body, small); Lay(); panel.MouseUp();
        var vb = new VertexBatch(body.SolidU, body.SolidV);
        panel.ControlBox(dd, out dx0, out dx1, out dy0, out dy1);
        panel.MouseDown((dx0 + dx1) / 2f, (dy0 + dy1) / 2f); panel.MouseUp();
        panel.MouseMove(panel.Drop.X + 5f, panel.Drop.Y + panel.Drop.ItemH * 1.5f);
        E(panel.Drop.Open, "pre: list open");
        panel.Emit(vb);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { vb.Clear(); panel.Emit(vb); }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        E(delta == 0, $"100 panel draws with the list open allocate nothing ({delta} bytes)");
        tall.Emit(vb);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { vb.Clear(); tall.Emit(vb); }
        delta = GC.GetAllocatedBytesForCurrentThread() - before;
        E(delta == 0, $"100 scrolled-column draws allocate nothing ({delta} bytes)");
        vb.Clear(); tall.Emit(vb);
        float[] verts = vb.ToArray();
        bool inside = true;
        for (int v = 0; v < vb.VertexCount; v++)
        {
            float vy = verts[v * VertexBatch.Floats + 1];
            if (vy < tall.Y - 0.01f || vy > tall.Y + tall.H + 0.01f) inside = false;
        }
        E(inside, "every vertex of a scrolled panel lies within the panel rect (rows clipped to the box)");

        log.Info(ok ? "SettingsPanel.SelfCheck: passed" : "SettingsPanel.SelfCheck: FAILED");
    }
}
