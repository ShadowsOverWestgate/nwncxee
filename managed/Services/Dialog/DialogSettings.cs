using System.Collections.Generic;
using Sowx.Framework;
using Sowx.Ui;

namespace Sowx.Services.Dialog;

/// <summary>The dialog settings panel: the dialog-side consumer of the shared <see cref="SettingsPanel"/>. It
/// owns the content only -- which rows exist (Lock, Font size, Max height, Justify, Animations and the two
/// fade-duration knobs) and what each press writes to the values below. The panel owns everything else:
/// position, metrics, drag-by-title, the close-button hit, the row list, hit-test-to-row, the inline text
/// buffer, and the draw. Pure (pixels in, value edits out, no native refs), so it self-checks off-device; the
/// edited values live as public fields the shell (<see cref="DialogService"/>) seeds on open and reads back
/// after each interaction. Font is global (the shared atlas), so the shell re-bakes + persists when it moves.</summary>
public sealed class DialogSettings
{
    /// <summary>Design-px panel width, before <see cref="Metrics.Scale"/>.</summary>
    private const float PanelW = 240f;

    /// <summary>The shared panel this window's rows are drawn and hit-tested by (the renderer calls
    /// <see cref="SettingsPanel.Emit"/> on it).</summary>
    public readonly SettingsPanel Panel;

    // ---- the edited values (seeded on open, read back by the shell) ----
    public bool Locked, Anim, Justify;
    public float FontPx = 18f, MaxBodyH = 320f, FadeInMs = 180f, FadeOutMs = 140f;

    public const float FontMin = 10f, FontMax = 32f, FadeMin = 0f, FadeMax = 500f;
    /// <summary>Breakpoint range (px): the content height past which the window stops stretching and scrolls.</summary>
    public const float MaxHMin = 120f, MaxHMax = 800f;

    public DialogSettings()
    {
        Panel = new SettingsPanel(PanelW)
        {
            Title = "Dialog settings",
            Relayout = Layout,
            RowPressed = OnRowPressed,
            ApplyValue = OnApplyValue,
        };
    }

    public float X { get => Panel.X; set => Panel.X = value; }
    public float Y { get => Panel.Y; set => Panel.Y = value; }
    public float W => Panel.W;
    public float H => Panel.H;
    public bool Open => Panel.Open;
    public List<SetRow> Rows => Panel.Rows;

    private static float RowH => SettingsPanel.RowH;

    /// <summary>Rebuild the row list + panel height from the current values, publishing a fresh list (single
    /// writer: only the input thread lays out; the render thread reads the whole snapshot).</summary>
    public void Layout()
    {
        var rows = new List<SetRow>();
        float y = Panel.ContentTop;
        y = AddToggle(rows, y, SetAct.LockWin, "Lock window", Locked);
        y = AddSlider(rows, y, SetAct.SliderFont, "Font size", FontPx, FontMin, FontMax);
        y = AddSlider(rows, y, SetAct.MaxHeight, "Max height", MaxBodyH, MaxHMin, MaxHMax);
        y = AddToggle(rows, y, SetAct.JustifyText, "Justify text", Justify);
        y = AddToggle(rows, y, SetAct.AnimOn, "Animations", Anim);
        if (Anim)
        {
            y = AddSlider(rows, y, SetAct.FadeIn, "Fade in (ms)", FadeInMs, FadeMin, FadeMax);
            y = AddSlider(rows, y, SetAct.FadeOut, "Fade out (ms)", FadeOutMs, FadeMin, FadeMax);
        }
        Panel.Publish(rows, y);
    }

    private static float AddToggle(List<SetRow> rows, float y, SetAct act, string label, bool on)
    { rows.Add(new SetRow { Kind = SetKind.Toggle, Act = act, Label = label, Top = y, Height = RowH, On = on }); return y + RowH; }

    private static float AddSlider(List<SetRow> rows, float y, SetAct act, string label, float value, float min, float max)
    {
        float norm = max > min ? (value - min) / (max - min) : 0f;
        rows.Add(new SetRow { Kind = SetKind.Slider, Act = act, Label = label, Top = y, Height = RowH,
                              Norm = Clamp01(norm), Min = min, Max = max });
        return y + RowH;
    }

    /// <summary>Open the panel anchored at (<paramref name="ax"/>, <paramref name="ay"/>), seeding the values
    /// from the shell and clamping on-screen. Lays out twice: row tops bake in Y, so the clamp needs the height
    /// the first pass produced.</summary>
    public void OpenAt(float ax, float ay, float vpW, float vpH,
                       bool locked, float fontPx, float maxBodyH, bool anim, float fadeIn, float fadeOut, bool justify)
    {
        Panel.BeginOpen();
        Locked = locked; FontPx = fontPx; MaxBodyH = maxBodyH; Anim = anim; FadeInMs = fadeIn; FadeOutMs = fadeOut; Justify = justify;
        X = ax; Y = ay;
        Layout();
        if (vpW > 0f && X + W > vpW) X = vpW - W;
        if (vpH > 0f && Y + H > vpH) Y = vpH - H;
        if (X < 0f) X = 0f;
        if (Y < 0f) Y = 0f;
        Layout();
    }

    /// <summary>Force-close, clearing any half-owned press, drag, or field edit.</summary>
    public void Close() => Panel.Reset();

    public bool Contains(float mx, float my) => Panel.Contains(mx, my);

    public bool MouseDown(float mx, float my) => Panel.MouseDown(mx, my);
    public bool MouseMove(float mx, float my) => Panel.MouseMove(mx, my);
    public bool MouseUp() => Panel.MouseUp();
    public bool Moving => Panel.Moving;
    public bool Dragging => Panel.Dragging;

    /// <summary>A press on a non-slider row: flip the backing flag. Slider rows never reach here -- the panel
    /// owns the track drag and the value box.</summary>
    private void OnRowPressed(SetRow row)
    {
        switch (row.Act)
        {
            case SetAct.LockWin: Locked = !Locked; break;
            case SetAct.JustifyText: Justify = !Justify; break;
            case SetAct.AnimOn: Anim = !Anim; break;
        }
    }

    /// <summary>Clamp <paramref name="v"/> to the row's range, write it to the backing field, and mirror it onto
    /// the row's knob. Shared by slider drag and inline-edit commit (the panel hands over the raw parsed value).</summary>
    private void OnApplyValue(SetRow r, float v)
    {
        float cl = v < r.Min ? r.Min : v > r.Max ? r.Max : v;
        r.Norm = r.Max > r.Min ? (cl - r.Min) / (r.Max - r.Min) : 0f;
        switch (r.Act)
        {
            case SetAct.SliderFont: FontPx = cl; break;
            case SetAct.MaxHeight: MaxBodyH = cl; break;
            case SetAct.FadeIn: FadeInMs = cl; break;
            case SetAct.FadeOut: FadeOutMs = cl; break;
        }
    }

    // ---- inline numeric entry (click a slider's value box to type an exact value) ----

    /// <summary>True while a value box is being typed into (the shell routes keys here).</summary>
    public bool Editing => Panel.Editing;
    public string EditText => Panel.EditText;
    public void EditInsert(string s) => Panel.EditInsert(s);
    public void EditBackspace() => Panel.EditBackspace();
    public void CommitEdit() => Panel.CommitEdit();
    public void CancelEdit() => Panel.CancelEdit();

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>Off-device proof of the dialog-side content: the row list (incl. the anim knob show/hide), each
    /// row's press routing to its backing field, slider + typed-value routing with the range clamp, the value
    /// read-back the shell depends on, and the on-screen clamp. The panel's own hit-test/drag/edit mechanics are
    /// proved by <see cref="SettingsPanel.SelfCheck"/>.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"DialogSettings.SelfCheck FAIL: {w}"); } }

        var s = new DialogSettings();
        s.OpenAt(100, 100, 0, 0, locked: false, fontPx: 18, maxBodyH: 320, anim: true, fadeIn: 180, fadeOut: 140, justify: false);
        E(s.Open && s.Rows.Count == 7, "OpenAt lays out 7 rows (lock, font, maxH, justify, anim, 2 knobs)");
        E(s.MouseUp(), "up after gear-open is consumed (press ownership)");

        // Every row carries a unique (Act, Key, Channel) identity -- the panel re-resolves a grabbed row by it
        // after each relayout, so a duplicate would hand a mid-drag slider to the wrong field.
        var seen = new List<SetAct>();
        foreach (SetRow r in s.Rows) { E(!seen.Contains(r.Act), $"duplicate row action {r.Act}"); seen.Add(r.Act); }

        SetRow Row(SetAct a) => s.Rows.Find(r => r.Act == a)!;

        // Max-height slider full-right -> max.
        SetRow mhR = Row(SetAct.MaxHeight);
        s.Panel.SliderTrack(mhR, out float _mx0, out float mx1, out float _mh);
        s.MouseDown(mx1, mhR.Top + 2);
        E(System.Math.Abs(s.MaxBodyH - MaxHMax) < 0.5f, "max-height slider full-right -> max");
        s.MouseUp();

        // Lock toggle flips, stays open.
        s.MouseDown(s.X + 5, Row(SetAct.LockWin).Top + 2);
        E(s.Locked && s.Open, "lock toggle flips, stays open");
        s.MouseUp();

        // Justify toggle flips.
        s.MouseDown(s.X + 5, Row(SetAct.JustifyText).Top + 2);
        E(s.Justify, "justify toggle flips");
        s.MouseUp();

        // Animations off hides the two knob rows; on shows them.
        s.MouseDown(s.X + 5, Row(SetAct.AnimOn).Top + 2);
        E(!s.Anim && s.Rows.Find(r => r.Act == SetAct.FadeIn) == null, "anim off hides the fade knobs");
        s.MouseDown(s.X + 5, Row(SetAct.AnimOn).Top + 2);
        E(s.Anim && s.Rows.Find(r => r.Act == SetAct.FadeIn) != null, "anim on shows the fade knobs");
        s.MouseUp();

        // Font slider full-right -> max, then dragged to mid -> ~half (the shell re-bakes off FontPx).
        SetRow fontR = Row(SetAct.SliderFont);
        s.Panel.SliderTrack(fontR, out float fx0, out float fx1, out float _f);
        s.MouseDown(fx1, fontR.Top + 2);
        E(System.Math.Abs(s.FontPx - FontMax) < 0.5f, "font slider full-right -> max");
        s.MouseMove((fx0 + fx1) / 2f, fontR.Top + 2);
        E(System.Math.Abs(s.FontPx - (FontMin + FontMax) / 2f) < 1f, "font slider drag to mid -> ~half");
        E(s.MouseUp(), "font slider release consumed");

        // Fade-out slider full-left -> min (and it moves FadeOut, not FadeIn -- distinct actions).
        SetRow foR = Row(SetAct.FadeOut);
        s.Panel.SliderTrack(foR, out float ox0, out float _o1, out float _o2);
        s.MouseDown(ox0, foR.Top + 2);
        E(s.FadeOutMs == FadeMin && System.Math.Abs(s.FadeInMs - 180f) < 0.5f, "fade-out slider full-left -> min, leaves fade-in alone");
        s.MouseUp();

        // Inline numeric entry: click the value box, type an exact number, commit / clamp / cancel / focus-loss.
        void ClickFontValueBox()
        {
            SetRow fr = Row(SetAct.SliderFont);
            s.Panel.ValueBox(fr, out float bx0, out float bx1, out float by0, out float by1);
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
        s.MouseDown(s.X + 5, Row(SetAct.LockWin).Top + 2);          // press elsewhere commits (focus loss)
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

        // Close button + click-off both close; Close() drops every half-owned interaction.
        s.OpenAt(100, 100, 0, 0, false, 18, 320, true, 180, 140, false); s.MouseUp();
        s.MouseDown(s.X + s.W - 2, s.Y + 2);
        E(!s.Open, "close-X closes");
        s.OpenAt(100, 100, 0, 0, false, 18, 320, true, 180, 140, false); s.MouseUp();
        s.MouseDown(s.X + 5, s.Y + s.H + 40);
        E(!s.Open, "click-off closes");
        s.OpenAt(100, 100, 0, 0, false, 18, 320, true, 180, 140, false);
        s.MouseDown(s.X + s.W / 2f, s.Y + 2f);
        s.Close();
        E(!s.Open && !s.Dragging && !s.Editing && !s.MouseUp(), "Close drops press/drag/edit ownership");

        // Opens clamped on-screen.
        var s2 = new DialogSettings();
        s2.OpenAt(1200, 780, 1280, 800, false, 18, 320, true, 180, 140, false); s2.MouseUp();
        E(s2.X + s2.W <= 1280.5f && s2.Y + s2.H <= 800.5f, "panel clamps on-screen");

        log.Info(ok ? "DialogSettings.SelfCheck: passed" : "DialogSettings.SelfCheck: FAILED");
    }
}
