using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Nameplate;

/// <summary>The nameplate-side consumer of the shared <see cref="SettingsPanel"/>: it owns the row list and what
/// each press writes to the live <see cref="NameplateConfig"/>, nothing else. The model is the getter handed in
/// at construction (never a stashed copy), and every mutation calls <c>onChanged</c> so the service can persist
/// and re-lay. No new <see cref="SetAct"/> member: toggles and the mode cycle discriminate on
/// <see cref="SetRow.Bit"/>, sliders on <see cref="SetRow.Key"/> -- which is also what the panel re-resolves a
/// live slider grab by, so those keys must be stable across a relayout.</summary>
public sealed class NameplateSettings
{
    /// <summary>Design-px panel width, before <see cref="Metrics.Scale"/>.</summary>
    private const float PanelW = 260f;

    private readonly Func<NameplateConfig> _get;
    private readonly Action _onChanged;

    public SettingsPanel Panel { get; }

    public NameplateSettings(Func<NameplateConfig> get, Action onChanged)
    {
        _get = get;
        _onChanged = onChanged;
        Panel = new SettingsPanel(PanelW)
        {
            Title = "Nameplates",
            Relayout = Layout,
            RowPressed = OnRowPressed,
            ApplyValue = OnApplyValue,
        };
    }

    public bool IsOpen => Panel.Open;
    public float X { get => Panel.X; set => Panel.X = value; }
    public float Y { get => Panel.Y; set => Panel.Y = value; }
    public float W => Panel.W;
    public float H => Panel.H;
    public List<SetRow> Rows => Panel.Rows;

    // ToggleBit discriminators (toggles + the mode cycle).
    private const uint BitOn = 1, BitMode = 2, BitPortraits = 3, BitBackground = 4, BitOutline = 5,
                       BitBand = 6, BitBar = 7, BitChannel = 8, BitTell = 9;

    // Slider identity keys -- also the panel's mid-drag re-resolve key, so they are consts, not literals.
    private const string KeyDist = "maxdist", KeyQueue = "queue", KeyWidth = "width", KeyAnim = "animms";
    private const string KeyLines = "bubblelines";

    /// <summary>Max-distance slider ceiling, metres. 0 on the slider means unlimited, as the config documents.</summary>
    private const float DistMin = 0f, DistMax = 100f;

    private static float RowH => SettingsPanel.RowH;

    private static readonly string[] ModeNames = { "Off", "Text only", "Full" };

    /// <summary>Rebuild the row list from the live config and publish it.</summary>
    public void Layout()
    {
        NameplateConfig cfg = _get();
        var rows = new List<SetRow>();
        float y = Panel.ContentTop;

        y = AddToggle(rows, y, BitOn, "Nameplates", cfg.On);
        rows.Add(new SetRow { Kind = SetKind.Cycle, Act = SetAct.ToggleBit, Label = "Mode", Bit = BitMode,
            Value = ModeNames[cfg.Mode < 0 ? 0 : cfg.Mode >= ModeNames.Length ? ModeNames.Length - 1 : cfg.Mode],
            Top = y, Height = RowH });
        y += RowH;

        y = AddToggle(rows, y, BitPortraits, "Portraits", cfg.Portraits);
        y = AddToggle(rows, y, BitBackground, "Background", cfg.Background);
        y = AddToggle(rows, y, BitOutline, "Outline", cfg.Outline);
        y = AddToggle(rows, y, BitBand, "Health band", cfg.HealthBand);
        y = AddToggle(rows, y, BitBar, "Health bar", cfg.HealthBar);

        y = AddSlider(rows, y, KeyDist, "Max distance", cfg.MaxDistance, DistMin, DistMax);
        y = AddSlider(rows, y, KeyQueue, "Bubble queue", cfg.BubbleQueue,
                      NameplateConfig.QueueMin, NameplateConfig.QueueMax);
        y = AddSlider(rows, y, KeyWidth, "Max text width", cfg.BubbleWidth,
                      NameplateConfig.WidthMin, NameplateConfig.WidthMax);
        y = AddSlider(rows, y, KeyLines, "Bubble lines", cfg.BubbleLines,
                      NameplateConfig.LinesMin, NameplateConfig.LinesMax);

        y = AddSlider(rows, y, KeyAnim, "Animation (ms)", cfg.AnimMs,
                      NameplateConfig.AnimMin, NameplateConfig.AnimMax);

        y = AddToggle(rows, y, BitChannel, "Channel styling", cfg.ChannelStyle);
        y = AddToggle(rows, y, BitTell, "Click to tell", cfg.ClickToTell);

        Panel.Publish(rows, y);
    }

    private static float AddToggle(List<SetRow> rows, float y, uint bit, string label, bool on)
    {
        rows.Add(new SetRow { Kind = SetKind.Toggle, Act = SetAct.ToggleBit, Label = label, Bit = bit,
                              On = on, Top = y, Height = RowH });
        return y + RowH;
    }

    private static float AddSlider(List<SetRow> rows, float y, string key, string label,
                                   float value, float min, float max)
    {
        float norm = max > min ? (value - min) / (max - min) : 0f;
        rows.Add(new SetRow { Kind = SetKind.Slider, Act = SetAct.ToggleBit, Label = label, Key = key,
                              Norm = norm < 0f ? 0f : norm > 1f ? 1f : norm, Min = min, Max = max,
                              Top = y, Height = RowH });
        return y + RowH;
    }

    /// <summary>Open centred in the live viewport.</summary>
    public void Open() => OpenAt(TextRig.ViewportW, TextRig.ViewportH);

    /// <summary>Open centred in a <paramref name="vpW"/>×<paramref name="vpH"/> viewport. Lays out twice: row
    /// tops bake in Y, so the centring needs the height the first pass produced.</summary>
    public void OpenAt(float vpW, float vpH)
    {
        Panel.BeginOpen();
        Layout();
        X = (vpW - W) / 2f; Y = (vpH - H) / 2f;
        if (X < 0f) X = 0f;
        if (Y < 0f) Y = 0f;
        Layout();
    }

    public void Close() => Panel.Reset();

    public bool Contains(float mx, float my) => Panel.Contains(mx, my);
    public bool OverClose(float mx, float my) => Panel.OverClose(mx, my);
    public bool MouseDown(float mx, float my) => Panel.MouseDown(mx, my);
    public bool MouseMove(float mx, float my) => Panel.MouseMove(mx, my);
    public bool MouseUp() => Panel.MouseUp();
    public bool Moving => Panel.Moving;

    /// <summary>A press on a non-slider row: flip the config bit or advance the mode. Slider rows never reach
    /// here -- the panel owns the track drag and the value box.</summary>
    private void OnRowPressed(SetRow row)
    {
        NameplateConfig cfg = _get();
        if (row.Act != SetAct.ToggleBit) return;
        switch (row.Bit)
        {
            case BitOn: cfg.On = !cfg.On; break;
            case BitMode: cfg.Mode = (cfg.Mode + 1) % ModeNames.Length; break;
            case BitPortraits: cfg.Portraits = !cfg.Portraits; break;
            case BitBackground: cfg.Background = !cfg.Background; break;
            case BitOutline: cfg.Outline = !cfg.Outline; break;
            case BitBand: cfg.HealthBand = !cfg.HealthBand; break;
            case BitBar: cfg.HealthBar = !cfg.HealthBar; break;
            case BitChannel: cfg.ChannelStyle = !cfg.ChannelStyle; break;
            case BitTell: cfg.ClickToTell = !cfg.ClickToTell; break;
            default: return;
        }
        _onChanged();
    }

    /// <summary>Clamp to the row's range, write it to the config, and mirror it onto the row's knob. Shared by
    /// the slider drag and the inline-edit commit, so it fires at drag rate -- the callback must mark dirty,
    /// not write the file synchronously.</summary>
    private void OnApplyValue(SetRow r, float v)
    {
        float cl = v < r.Min ? r.Min : v > r.Max ? r.Max : v;
        NameplateConfig cfg = _get();
        switch (r.Key)
        {
            case KeyDist: cfg.MaxDistance = cl; break;
            case KeyQueue: cl = (int)System.MathF.Round(cl); cfg.BubbleQueue = (int)cl; break;
            case KeyWidth: cfg.BubbleWidth = cl; break;
            case KeyLines: cl = (int)System.MathF.Round(cl); cfg.BubbleLines = (int)cl; break;
            case KeyAnim: cl = System.MathF.Round(cl); cfg.AnimMs = cl; break;
            default: return;
        }
        r.Norm = r.Max > r.Min ? (cl - r.Min) / (r.Max - r.Min) : 0f;
        _onChanged();
    }

    /// <summary>True while a slider's value box is being typed into (the service routes keys here).</summary>
    public bool Editing => Panel.Editing;
    public string EditText => Panel.EditText;

    /// <summary>The inline value-box edit, routed from the service's <c>OnKey</c>. Without these the caret the
    /// panel opens on a value-box click can never be typed into, and the keys fall through to gameplay keybinds.</summary>
    public void EditInsert(string s) => Panel.EditInsert(s);
    public void EditBackspace() => Panel.EditBackspace();
    public void CommitEdit() => Panel.CommitEdit();
    public void CancelEdit() => Panel.CancelEdit();

    public void Reset() => Panel.Reset();

    /// <summary>Off-device proof of the row set, every press path, the slider apply and the change callback.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"NameplateSettings.SelfCheck FAIL: {w}"); } }

        var cfg = new NameplateConfig();
        int changed = 0;
        var s = new NameplateSettings(() => cfg, () => changed++);

        s.OpenAt(1280, 800);
        E(s.IsOpen && s.Rows.Count == 14, "opens with 14 rows");
        E(s.MouseUp(), "up after open is consumed");
        E(System.Math.Abs(s.W - PanelW * Metrics.Scale) < 0.01f, "panel width is the design width, scaled");
        E(System.Math.Abs(s.H - (SettingsPanel.TitleH + 14f * RowH + SettingsPanel.Pad)) < 0.01f,
          "panel height = title + 14 rows + pad");
        E(ReferenceEquals(s.Rows, s.Panel.Rows), "the rows the service draws are the panel's published rows");

        SetRow Row(uint bit) => s.Rows.Find(r => r.Kind != SetKind.Slider && r.Bit == bit)!;
        SetRow Slider(string key) => s.Rows.Find(r => r.Key == key)!;

        E(Row(BitOn).On && Row(BitMode).Value == "Full", "rows seed from the config defaults");
        E(!Row(BitBar).On, "health bar row defaults off");

        int before = changed;
        s.MouseDown(s.X + 5, Row(BitOn).Top + 2);
        E(!cfg.On && changed == before + 1, "master toggle flips and reports one change");

        s.MouseDown(s.X + 5, Row(BitMode).Top + 2);
        E(cfg.Mode == 0 && Row(BitMode).Value == "Off", "mode cycles 2 -> 0 and relabels");
        s.MouseDown(s.X + 5, Row(BitMode).Top + 2);
        E(cfg.Mode == 1 && Row(BitMode).Value == "Text only", "mode cycles 0 -> 1");

        s.MouseDown(s.X + 5, Row(BitPortraits).Top + 2);
        E(!cfg.Portraits, "portraits toggles off");
        s.MouseDown(s.X + 5, Row(BitBackground).Top + 2);
        E(!cfg.Background, "background toggles off");
        s.MouseDown(s.X + 5, Row(BitOutline).Top + 2);
        E(!cfg.Outline, "outline toggles off");
        s.MouseDown(s.X + 5, Row(BitBand).Top + 2);
        E(!cfg.HealthBand, "health band toggles off");
        s.MouseDown(s.X + 5, Row(BitBar).Top + 2);
        E(cfg.HealthBar, "health bar toggles on");
        s.MouseDown(s.X + 5, Row(BitChannel).Top + 2);
        E(!cfg.ChannelStyle, "channel styling toggles off");
        s.MouseDown(s.X + 5, Row(BitTell).Top + 2);
        E(cfg.ClickToTell, "click-to-tell toggles on (it ships off)");

        // Sliders: the panel hands the raw value straight to ApplyValue (drag and value box share the path).
        SetRow dist = Slider(KeyDist);
        E(dist.Min == DistMin && dist.Max == DistMax, "distance slider range");
        before = changed;
        s.Panel.ApplyValue!(dist, 30f);
        E(System.Math.Abs(cfg.MaxDistance - 30f) < 1e-4f, "distance slider writes the config");
        E(System.Math.Abs(dist.Norm - 0.3f) < 1e-4f, "distance slider mirrors the knob");
        E(changed == before + 1, "a slider apply reports one change");
        s.Panel.ApplyValue!(dist, 9999f);
        E(cfg.MaxDistance == DistMax && dist.Norm == 1f, "distance slider clamps at the top");
        s.Panel.ApplyValue!(dist, -5f);
        E(cfg.MaxDistance == DistMin && dist.Norm == 0f, "distance slider clamps at the bottom");

        s.Panel.ApplyValue!(Slider(KeyDist), 0f);
        E(cfg.MaxDistance == 0f, "max distance reaches 0 (unlimited)");
        s.Panel.ApplyValue!(Slider(KeyQueue), 3.4f);
        E(cfg.BubbleQueue == 3, "bubble queue rounds to an int");
        s.Panel.ApplyValue!(Slider(KeyWidth), 300f);
        E(System.Math.Abs(cfg.BubbleWidth - 300f) < 1e-4f, "bubble width writes the config");
        s.Panel.ApplyValue!(Slider(KeyLines), 11.6f);
        E(cfg.BubbleLines == 12, "bubble lines rounds to an int");
        s.Panel.ApplyValue!(Slider(KeyLines), 999f);
        E(cfg.BubbleLines == NameplateConfig.LinesMax, "bubble lines clamps at the wrapped-line ceiling");
        s.Panel.ApplyValue!(Slider(KeyAnim), 220.6f);
        E(cfg.AnimMs == 221f, "animation slider rounds to whole ms");
        s.Panel.ApplyValue!(Slider(KeyAnim), 0f);
        E(cfg.AnimMs == 0f, "animation slider reaches 0 (no animation)");
        s.Panel.ApplyValue!(Slider(KeyAnim), 99999f);
        E(cfg.AnimMs == NameplateConfig.AnimMax, "animation slider clamps at the config ceiling");

        // The inline value-box edit the service's OnKey routes into: without these passthroughs the caret a
        // value-box click opens can never be typed into and the keys fall through to gameplay keybinds.
        s.OpenAt(1280, 800); s.MouseUp();
        SetRow box = Slider(KeyDist);
        void ClickValueBox()
        {
            s.Panel.ValueBox(box, out float vx0, out float vx1, out float vy0, out float vy1);
            s.MouseDown((vx0 + vx1) / 2f, (vy0 + vy1) / 2f);
            s.MouseUp();
        }
        ClickValueBox();
        E(s.Editing, "clicking a slider's value box starts an edit the service must route keys into");
        before = changed;
        s.EditInsert("3a0");
        E(s.EditText == "30", "EditInsert reaches the panel and its numeric filter");
        s.EditInsert("9"); s.EditBackspace();
        E(s.EditText == "30", "EditBackspace reaches the panel");
        s.CommitEdit();
        E(!s.Editing && System.Math.Abs(cfg.MaxDistance - 30f) < 1e-4f && changed == before + 1,
          "CommitEdit writes the typed value through ApplyValue and reports the change");
        ClickValueBox();
        s.EditInsert("77"); s.CancelEdit();
        E(!s.Editing && System.Math.Abs(cfg.MaxDistance - 30f) < 1e-4f, "CancelEdit drops the edit, value untouched");

        // Slider identity: Act+Key is what the panel re-resolves a live grab by, so every key must be distinct.
        E(s.Rows.FindAll(r => r.Kind == SetKind.Slider).Count == 5, "five slider rows");
        E(Slider(KeyDist) != Slider(KeyQueue) && Slider(KeyQueue) != Slider(KeyWidth)
          && Slider(KeyWidth) != Slider(KeyAnim), "slider keys are distinct");
        E(Slider(KeyQueue).Max == NameplateConfig.QueueMax, "the queue slider's top notch is a value the table can hold");

        // A title drag edits nothing.
        s.OpenAt(1280, 800); s.MouseUp();
        float px = s.X, py = s.Y;
        int quiet = changed;
        E(s.MouseDown(s.X + s.W / 2f, s.Y + 2f) && s.Moving, "title-bar press latches a move");
        s.MouseMove(s.X + s.W / 2f + 30f, s.Y + 2f + 20f);
        E(System.Math.Abs(s.X - (px + 30f)) < 0.5f && System.Math.Abs(s.Y - (py + 20f)) < 0.5f,
          "panel drags by the title bar");
        E(changed == quiet, "a title drag edits no config");
        E(s.MouseUp() && !s.Moving, "release consumed + ends the drag");

        s.OpenAt(1280, 800); s.MouseUp();
        s.MouseDown(s.X + s.W - 2, s.Y + 2);
        E(!s.IsOpen, "close button closes");
        s.OpenAt(1280, 800); s.MouseUp();
        s.MouseDown(s.X + 5, s.Y + s.H + 40);
        E(!s.IsOpen, "click-off closes");

        s.OpenAt(1280, 800);
        s.Close();
        E(!s.IsOpen && !s.Moving && !s.MouseUp(), "Close drops the panel and any half-owned press");

        log.Info(ok ? "NameplateSettings.SelfCheck: passed" : "NameplateSettings.SelfCheck: FAILED");
    }
}
