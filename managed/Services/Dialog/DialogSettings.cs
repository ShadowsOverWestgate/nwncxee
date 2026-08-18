using System.Collections.Generic;
using Sowx.Framework;
using Sowx.Services.Chat.Core;
using Sowx.Ui;

namespace Sowx.Services.Dialog;

/// <summary>The dialog settings panel: a small floating panel of typed rows -- Lock, Font size, Animations, and
/// the two fade-duration knobs -- opened from the dialog window's gear icon. Pure (pixels in, hit-tests + value
/// edits out, no native refs), so it self-checks off-device; the edited values live as public fields the
/// shell (<see cref="DialogService"/>) seeds on open and reads back after each interaction. Font is global (the
/// shared atlas), so a font change is flagged via <see cref="LastChange"/> for the shell to re-bake + persist.
/// Drag-only sliders (no inline numeric typing -- YAGNI). Metrics scale with <see cref="Metrics.Scale"/> so the
/// panel tracks the font/UI size like the chat settings window.</summary>
public sealed class DialogSettings
{
    /// <summary>Which value a row edits (and what the shell must re-apply). <see cref="Font"/> is global.</summary>
    public enum Act { None, Lock, Font, MaxH, Justify, Anim, FadeIn, FadeOut }

    public sealed class Row
    {
        public Act Act;
        public bool Slider;      // false = toggle checkbox
        public string Label = "";
        public float Top, Height;
        public bool On;          // toggle state
        public float Norm;       // slider fill 0..1
        public float Min, Max;   // slider range
        public string Value = "";// slider value text
    }

    public float X, Y;
    public bool Open;

    // ---- the edited values (seeded on open, read back by the shell) ----
    public bool Locked, Anim, Justify;
    public float FontPx = 18f, MaxBodyH = 320f, FadeInMs = 180f, FadeOutMs = 140f;
    /// <summary>The value touched by the last press/drag, so the shell knows whether to re-bake the atlas
    /// (Font) or just persist. Reset to <see cref="Act.None"/> after the shell consumes it.</summary>
    public Act LastChange;

    public const float FontMin = 10f, FontMax = 32f, FadeMin = 0f, FadeMax = 500f;
    /// <summary>Breakpoint range (px): the content height past which the window stops stretching and scrolls.</summary>
    public const float MaxHMin = 120f, MaxHMax = 800f;

    public static float TitleH => 22f * Metrics.Scale;
    public static float RowH => 24f * Metrics.Scale;
    public static float Pad => 8f * Metrics.Scale;
    public static float CheckSize => 14f * Metrics.Scale;
    public static float CloseW => TitleH;
    public const float ControlX = 0.46f;   // label|control split as a width fraction
    public static float ValueW => 40f * Metrics.Scale;
    public float W => 240f * Metrics.Scale;

    private volatile List<Row> _rows = new();
    public List<Row> Rows => _rows;
    private float _h = TitleH + Pad;
    public float H => _h;

    private Row? _grab;            // slider being dragged
    private bool _moving, _pressConsumed;
    private float _mgx, _mgy;      // panel move-grab anchor
    private Row? _editRow;         // slider whose value box is being typed into
    private string _editText = "";

    public void SliderTrack(Row r, out float x0, out float x1, out float cy)
    {
        x0 = X + W * ControlX;
        x1 = X + W - Pad - ValueW - 4f;
        cy = r.Top + RowH * 0.5f;
    }

    /// <summary>The numeric value box at a slider row's right edge (click to type an exact value). Draw + hit-test share it.</summary>
    public void ValueBox(Row r, out float x0, out float x1, out float y0, out float y1)
    {
        x1 = X + W - Pad; x0 = x1 - ValueW;
        y0 = r.Top + 3f; y1 = r.Top + RowH - 3f;
    }
    private bool OverValueBox(Row r, float mx, float my)
    { ValueBox(r, out float x0, out float x1, out float y0, out float y1); return mx >= x0 && mx <= x1 && my >= y0 && my <= y1; }

    /// <summary>True while a value box is being typed into (the shell routes keys here).</summary>
    public bool Editing => _editRow != null;
    /// <summary>Is <paramref name="r"/> the row being edited? (the renderer shows the buffer + a caret).</summary>
    public bool EditingRow(Row r) => ReferenceEquals(_editRow, r);
    public string EditText => _editText;

    /// <summary>Rebuild the row list + panel height from the current values, publishing a fresh list (single
    /// writer: only the input thread lays out; the render thread reads the whole snapshot).</summary>
    public void Layout()
    {
        var rows = new List<Row>();
        float y = Y + TitleH;
        y = AddToggle(rows, y, Act.Lock, "Lock window", Locked);
        y = AddSlider(rows, y, Act.Font, "Font size", FontPx, FontMin, FontMax);
        y = AddSlider(rows, y, Act.MaxH, "Max height", MaxBodyH, MaxHMin, MaxHMax);
        y = AddToggle(rows, y, Act.Justify, "Justify text", Justify);
        y = AddToggle(rows, y, Act.Anim, "Animations", Anim);
        if (Anim)
        {
            y = AddSlider(rows, y, Act.FadeIn, "Fade in (ms)", FadeInMs, FadeMin, FadeMax);
            y = AddSlider(rows, y, Act.FadeOut, "Fade out (ms)", FadeOutMs, FadeMin, FadeMax);
        }
        _h = (y - Y) + Pad;
        _rows = rows;
    }

    private static float AddToggle(List<Row> rows, float y, Act act, string label, bool on)
    { rows.Add(new Row { Act = act, Slider = false, Label = label, Top = y, Height = RowH, On = on }); return y + RowH; }

    private static float AddSlider(List<Row> rows, float y, Act act, string label, float value, float min, float max)
    {
        float norm = max > min ? (value - min) / (max - min) : 0f;
        rows.Add(new Row { Act = act, Slider = true, Label = label, Top = y, Height = RowH,
                           Norm = Clamp01(norm), Min = min, Max = max, Value = ValueText(act, value) });
        return y + RowH;
    }

    private static string ValueText(Act act, float v)
        => ((int)(v + 0.5f)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Open the panel anchored at (<paramref name="ax"/>, <paramref name="ay"/>), seeding the five
    /// values from the shell and clamping on-screen.</summary>
    public void OpenAt(float ax, float ay, float vpW, float vpH,
                       bool locked, float fontPx, float maxBodyH, bool anim, float fadeIn, float fadeOut, bool justify)
    {
        Open = true; _pressConsumed = true; _grab = null; _moving = false; LastChange = Act.None; _editRow = null; _editText = "";
        Locked = locked; FontPx = fontPx; MaxBodyH = maxBodyH; Anim = anim; FadeInMs = fadeIn; FadeOutMs = fadeOut; Justify = justify;
        X = ax; Y = ay;
        Layout();
        if (vpW > 0f && X + W > vpW) X = vpW - W;
        if (vpH > 0f && Y + H > vpH) Y = vpH - H;
        if (X < 0f) X = 0f;
        if (Y < 0f) Y = 0f;
        Layout();
    }

    public void Close() { Open = false; _grab = null; _moving = false; _pressConsumed = false; LastChange = Act.None; _editRow = null; _editText = ""; }

    public bool OverClose(float mx, float my)
        => mx >= X + W - CloseW && mx <= X + W && my >= Y && my <= Y + TitleH;
    public bool Contains(float mx, float my) => mx >= X && mx <= X + W && my >= Y && my <= Y + H;

    private Row? RowAt(float mx, float my)
    {
        if (mx < X || mx > X + W) return null;
        foreach (Row r in _rows) if (my >= r.Top && my < r.Top + r.Height) return r;
        return null;
    }

    /// <summary>Pointer press. Always consumed while open. Toggles flip; a slider hit starts a drag; the close
    /// button or a click-off closes. Returns the acted-on row's <see cref="Act"/> via <see cref="LastChange"/>.</summary>
    public bool MouseDown(float mx, float my)
    {
        _pressConsumed = true; _grab = null; _moving = false; LastChange = Act.None;
        if (_editRow != null) CommitEdit();   // any press ends an in-progress field edit (commit-on-focus-loss)
        if (OverClose(mx, my)) { Close(); return true; }
        if (my < Y + TitleH) { _moving = true; _mgx = mx - X; _mgy = my - Y; return true; }   // title bar = move handle
        Row? row = RowAt(mx, my);
        if (row == null) { if (!Contains(mx, my)) Open = false; return true; }

        if (!row.Slider)
        {
            switch (row.Act)
            {
                case Act.Lock: Locked = !Locked; break;
                case Act.Justify: Justify = !Justify; break;
                case Act.Anim: Anim = !Anim; break;
            }
            LastChange = row.Act;
            Layout();   // anim toggle shows/hides its knob rows
        }
        else if (OverValueBox(row, mx, my))
        {
            _editRow = row; _editText = "";   // click the value box -> type an exact value (empty keeps the old on commit)
        }
        else
        {
            SliderTrack(row, out float x0, out float x1, out float _);
            if (mx >= x0 && mx <= x1) { _grab = row; SetSliderFromX(row, mx); }
        }
        return true;
    }

    // ---- inline numeric entry (click a slider's value box to type an exact value) ----

    /// <summary>Append typed characters (digits / one dot / leading sign), capped at 8.</summary>
    public void EditInsert(string s)
    {
        if (_editRow == null) return;
        foreach (char c in s)
            if (_editText.Length < 8 && ((c >= '0' && c <= '9') || c == '.' || c == '-'))
                _editText += c;
    }

    public void EditBackspace()
    { if (_editRow != null && _editText.Length > 0) _editText = _editText.Substring(0, _editText.Length - 1); }

    /// <summary>Commit the typed value: parse + clamp via the row's range and write it (empty/unparseable keeps the
    /// old value). Sets <see cref="LastChange"/> so the shell re-applies (e.g. font re-bake).</summary>
    public void CommitEdit()
    {
        Row? r = _editRow;
        string t = _editText;
        _editRow = null; _editText = "";
        if (r == null) return;
        if (float.TryParse(t, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float v))
            ApplyValue(r, v);
    }

    public void CancelEdit() { _editRow = null; _editText = ""; }

    /// <summary>Pointer motion while a slider or the title bar is grabbed. A title-bar move translates the rows
    /// in place (no full re-layout).</summary>
    public bool MouseMove(float mx, float my)
    {
        if (_moving)
        {
            float dy = (my - _mgy) - Y;
            X = mx - _mgx; Y = my - _mgy;
            var rows = _rows;
            for (int i = 0; i < rows.Count; i++) rows[i].Top += dy;
            return true;
        }
        if (_grab == null) return false;
        SetSliderFromX(_grab, mx);
        return true;
    }

    private void SetSliderFromX(Row r, float mx)
    {
        SliderTrack(r, out float x0, out float x1, out float _);
        float norm = x1 > x0 ? Clamp01((mx - x0) / (x1 - x0)) : 0f;
        ApplyValue(r, r.Min + norm * (r.Max - r.Min));
    }

    /// <summary>Clamp <paramref name="v"/> to the row's range, write it to the backing field, and mirror it onto
    /// the row's draw state (knob + value text). Shared by slider drag and inline-edit commit.</summary>
    private void ApplyValue(Row r, float v)
    {
        float cl = v < r.Min ? r.Min : v > r.Max ? r.Max : v;
        r.Norm = r.Max > r.Min ? (cl - r.Min) / (r.Max - r.Min) : 0f;
        r.Value = ValueText(r.Act, cl);
        switch (r.Act)
        {
            case Act.Font: FontPx = cl; break;
            case Act.MaxH: MaxBodyH = cl; break;
            case Act.FadeIn: FadeInMs = cl; break;
            case Act.FadeOut: FadeOutMs = cl; break;
        }
        LastChange = r.Act;
    }

    public bool Moving => _moving;
    public bool Dragging => _grab != null || _moving;

    /// <summary>Pointer release. Consumed iff we owned the press. Ends any drag.</summary>
    public bool MouseUp()
    {
        bool c = _pressConsumed;
        _pressConsumed = false; _grab = null; _moving = false;
        return c;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>Off-device proof: open/close/ownership, toggle flips, the anim-knob show/hide, slider drag, and
    /// the LastChange signal (so the shell knows when to re-bake the font atlas).</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"DialogSettings.SelfCheck FAIL: {w}"); } }

        var s = new DialogSettings();
        s.OpenAt(100, 100, 0, 0, locked: false, fontPx: 18, maxBodyH: 320, anim: true, fadeIn: 180, fadeOut: 140, justify: false);
        E(s.Open && s.Rows.Count == 7, "OpenAt lays out 7 rows (lock, font, maxH, justify, anim, 2 knobs)");
        E(s.MouseUp(), "up after gear-open is consumed (press ownership)");

        // Max-height slider full-right -> max, and it signals MaxH.
        Row mhR = s.Rows.Find(r => r.Act == Act.MaxH)!;
        s.SliderTrack(mhR, out float mx0, out float mx1, out float _mh);
        s.MouseDown(mx1, mhR.Top + 2);
        E(System.Math.Abs(s.MaxBodyH - MaxHMax) < 0.5f && s.LastChange == Act.MaxH, "max-height slider full-right -> max, signals MaxH");
        s.MouseUp();

        // Lock toggle flips + signals.
        Row lockR = s.Rows.Find(r => r.Act == Act.Lock)!;
        s.MouseDown(s.X + 5, lockR.Top + 2);
        E(s.Locked && s.LastChange == Act.Lock && s.Open, "lock toggle flips + signals, stays open");
        s.MouseUp();

        // Justify toggle flips + signals.
        Row jR = s.Rows.Find(r => r.Act == Act.Justify)!;
        s.MouseDown(s.X + 5, jR.Top + 2);
        E(s.Justify && s.LastChange == Act.Justify, "justify toggle flips + signals");
        s.MouseUp();

        // Animations off hides the two knob rows; on shows them.
        Row animR = s.Rows.Find(r => r.Act == Act.Anim)!;
        s.MouseDown(s.X + 5, animR.Top + 2);
        E(!s.Anim && s.Rows.Find(r => r.Act == Act.FadeIn) == null, "anim off hides the fade knobs");
        s.MouseDown(s.X + 5, s.Rows.Find(r => r.Act == Act.Anim)!.Top + 2);
        E(s.Anim && s.Rows.Find(r => r.Act == Act.FadeIn) != null, "anim on shows the fade knobs");
        s.MouseUp();

        // Font slider full-right -> max, and it signals Font so the shell re-bakes.
        Row fontR = s.Rows.Find(r => r.Act == Act.Font)!;
        s.SliderTrack(fontR, out float fx0, out float fx1, out float _);
        s.MouseDown(fx1, fontR.Top + 2);
        E(System.Math.Abs(s.FontPx - FontMax) < 0.5f && s.LastChange == Act.Font, "font slider full-right -> max, signals Font");
        s.MouseMove((fx0 + fx1) / 2f, fontR.Top + 2);
        E(System.Math.Abs(s.FontPx - (FontMin + FontMax) / 2f) < 1f, "font slider drag to mid -> ~half");
        E(s.MouseUp(), "font slider release consumed");

        // Fade-out slider full-left -> min.
        Row foR = s.Rows.Find(r => r.Act == Act.FadeOut)!;
        s.SliderTrack(foR, out float ox0, out float _1, out float _2);
        s.MouseDown(ox0, foR.Top + 2);
        E(s.FadeOutMs == FadeMin && s.LastChange == Act.FadeOut, "fade-out slider full-left -> min");
        s.MouseUp();

        // Inline numeric entry: click the value box, type an exact number, commit / clamp / cancel / focus-loss.
        void ClickFontValueBox()
        {
            Row fr = s.Rows.Find(rr => rr.Act == Act.Font)!;
            s.ValueBox(fr, out float bx0, out float bx1, out float by0, out float by1);
            s.MouseDown((bx0 + bx1) / 2f, (by0 + by1) / 2f);
        }
        ClickFontValueBox();
        E(s.Editing, "clicking a value box starts a field edit");
        s.EditInsert("2a4");                 // letters filtered out -> "24"
        s.CommitEdit();
        E(!s.Editing && System.Math.Abs(s.FontPx - 24f) < 0.01f, "typed value (filtered) commits");
        ClickFontValueBox(); s.EditInsert("999"); s.CommitEdit();   // over max -> clamps
        E(System.Math.Abs(s.FontPx - FontMax) < 0.01f, "typed value clamps to max");
        ClickFontValueBox(); s.EditInsert("14"); s.CancelEdit();
        E(!s.Editing && System.Math.Abs(s.FontPx - FontMax) < 0.01f, "cancel leaves the value untouched");
        ClickFontValueBox(); s.CommitEdit();                        // empty -> unparseable -> keep old
        E(System.Math.Abs(s.FontPx - FontMax) < 0.01f, "empty commit keeps the old value");
        ClickFontValueBox(); s.EditInsert("16");
        s.MouseDown(s.X + 5, s.Rows.Find(rr => rr.Act == Act.Lock)!.Top + 2);   // press elsewhere commits (focus loss)
        E(!s.Editing && System.Math.Abs(s.FontPx - 16f) < 0.01f, "a press elsewhere commits the edit");
        s.MouseUp();

        // Title-bar drag moves the panel; rows follow.
        s.OpenAt(200, 200, 0, 0, false, 18, 320, true, 180, 140, false); s.MouseUp();
        float px = s.X, py = s.Y, top0 = s.Rows[0].Top;
        s.MouseDown(s.X + s.W / 2f, s.Y + 2f);
        s.MouseMove(s.X + s.W / 2f + 30f, s.Y + 2f + 20f);
        E(System.Math.Abs(s.X - (px + 30f)) < 0.5f && System.Math.Abs(s.Y - (py + 20f)) < 0.5f, "panel drags by the title bar");
        E(System.Math.Abs(s.Rows[0].Top - (top0 + 20f)) < 0.5f, "rows follow the panel move");
        s.MouseUp();

        // Close button + click-off both close.
        s.OpenAt(100, 100, 0, 0, false, 18, 320, true, 180, 140, false); s.MouseUp();
        s.MouseDown(s.X + s.W - 2, s.Y + 2);
        E(!s.Open, "close-X closes");
        s.OpenAt(100, 100, 0, 0, false, 18, 320, true, 180, 140, false); s.MouseUp();
        s.MouseDown(s.X + 5, s.Y + s.H + 40);
        E(!s.Open, "click-off closes");

        // Opens clamped on-screen.
        var s2 = new DialogSettings();
        s2.OpenAt(1200, 780, 1280, 800, false, 18, 320, true, 180, 140, false); s2.MouseUp();
        E(s2.X + s2.W <= 1280.5f && s2.Y + s2.H <= 800.5f, "panel clamps on-screen");

        // Scale invariance: metrics track Metrics.Scale (save/restore -- self-checks run at Scale 1).
        float sc0 = Metrics.Scale;
        float rh1 = RowH; Metrics.Scale = 2f;
        E(System.Math.Abs(RowH - 2f * rh1) < 1e-3f, "RowH scales with Metrics.Scale");
        Metrics.Scale = sc0;

        log.Info(ok ? "DialogSettings.SelfCheck: passed" : "DialogSettings.SelfCheck: FAILED");
    }
}
