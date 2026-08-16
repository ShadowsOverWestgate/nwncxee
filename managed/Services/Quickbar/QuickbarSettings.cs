using Sowx.Framework;
using Sowx.Services.Chat.Core;

namespace Sowx.Services.Quickbar;

/// <summary>Quickbar settings window: a small floating panel opened by <c>/qbar</c>. Position cycle,
/// bars toggle, hide quickbar, hide compass. Reuses the same <see cref="SetRow"/>/<see cref="SetKind"/>
/// vocabulary as the chatbox settings window — different content, identical rendering strategy.</summary>
public sealed class QuickbarSettings
{
    public float X, Y;
    public float W => 220f * Metrics.Scale;
    public bool Open;
    private bool _pressConsumed;
    private bool _moving;
    private float _mgx, _mgy;

    public static float TitleH => 22f * Metrics.Scale;
    public static float RowH => 24f * Metrics.Scale;
    public static float Pad => 8f * Metrics.Scale;
    public static float CloseW => TitleH;

    private volatile List<SetRow> _rows = new();
    public List<SetRow> Rows => _rows;
    private float _h = TitleH + Pad;
    public float H => _h;

    public bool Editing => false;
    public bool EditingRow(SetRow r) => false;
    public string EditText => "";

    public void SliderTrack(SetRow r, out float x0, out float x1, out float cy)
    { x0 = X + W * 0.42f; x1 = X + W - Pad - 36f; cy = r.Top + RowH * 0.5f; }

    public void ValueBox(SetRow r, out float x0, out float x1, out float y0, out float y1)
    { x1 = X + W - Pad; x0 = x1 - 36f; y0 = r.Top + 3f; y1 = r.Top + RowH - 3f; }

    // Bit discriminators for ToggleBit rows.
    private const uint BitBars = 1, BitHidden = 2, BitCompass = 3;

    /// <summary>Rebuild the row list from the live config.</summary>
    public void Layout(QuickbarConfig cfg)
    {
        var rows = new List<SetRow>();
        float y = Y + TitleH;

        string alignText = cfg.Align switch
        {
            QbarAlign.Left => "Left", QbarAlign.Right => "Right", _ => "Center"
        };
        rows.Add(new SetRow { Kind = SetKind.Cycle, Act = SetAct.CycleAlign, Label = "Position",
            Value = alignText, Top = y, Height = RowH });
        y += RowH;

        rows.Add(new SetRow { Kind = SetKind.Toggle, Act = SetAct.ToggleBit, Label = "Black bars",
            On = cfg.Bars, Top = y, Height = RowH, Bit = BitBars });
        y += RowH;

        rows.Add(new SetRow { Kind = SetKind.Toggle, Act = SetAct.ToggleBit, Label = "Hide quickbar",
            On = cfg.Hidden, Top = y, Height = RowH, Bit = BitHidden });
        y += RowH;

        rows.Add(new SetRow { Kind = SetKind.Toggle, Act = SetAct.ToggleBit, Label = "Hide compass",
            On = cfg.CompassHidden, Top = y, Height = RowH, Bit = BitCompass });
        y += RowH;

        _h = (y - Y) + Pad;
        _rows = rows;
    }

    /// <summary>Open the panel centred in a <paramref name="vpW"/>×<paramref name="vpH"/> viewport.</summary>
    public void OpenAt(float vpW, float vpH, QuickbarConfig cfg)
    {
        Open = true; _pressConsumed = true; _moving = false;
        Layout(cfg);
        X = (vpW - W) / 2f; Y = (vpH - H) / 2f;
        if (X < 0f) X = 0f;
        if (Y < 0f) Y = 0f;
        Layout(cfg);
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

    /// <summary>Pointer press. Returns true when consumed.</summary>
    public bool MouseDown(float mx, float my, QuickbarConfig cfg)
    {
        _pressConsumed = true; _moving = false;
        if (OverClose(mx, my)) { Open = false; return true; }
        if (my < Y + TitleH) { _moving = true; _mgx = mx - X; _mgy = my - Y; return true; }
        SetRow? row = RowAt(mx, my);
        if (row == null) { if (!Contains(mx, my)) Open = false; return true; }

        switch (row.Act)
        {
            case SetAct.CycleAlign:
                cfg.Align = (QbarAlign)(((int)cfg.Align + 1) % 3);
                break;
            case SetAct.ToggleBit:
                switch (row.Bit)
                {
                    case BitBars: cfg.Bars = !cfg.Bars; break;
                    case BitHidden: cfg.Hidden = !cfg.Hidden; break;
                    case BitCompass: cfg.CompassHidden = !cfg.CompassHidden; break;
                }
                break;
        }
        Layout(cfg);
        return true;
    }

    public bool MouseMove(float mx, float my)
    {
        if (!_moving) return false;
        float oldY = Y;
        X = mx - _mgx; Y = my - _mgy;
        float dy = Y - oldY;
        var rows = _rows;
        for (int i = 0; i < rows.Count; i++) rows[i].Top += dy;
        return true;
    }

    public bool MouseUp()
    {
        bool c = _pressConsumed;
        _pressConsumed = false; _moving = false;
        return c;
    }

    public bool Moving => _moving;

    public void Reset() { Open = false; _pressConsumed = false; _moving = false; }

    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"QuickbarSettings.SelfCheck FAIL: {w}"); } }

        var cfg = new QuickbarConfig();
        var s = new QuickbarSettings();
        s.OpenAt(1280, 800, cfg);
        E(s.Open && s.Rows.Count == 4, "opens with 4 rows");
        E(s.MouseUp(), "up after open is consumed");

        // Cycle align: Left(0)->Center(1)->Right(2)->Left(0)
        cfg.Align = QbarAlign.Left;
        s.OpenAt(1280, 800, cfg); s.MouseUp();
        SetRow cy = s.Rows.Find(r => r.Act == SetAct.CycleAlign)!;
        s.MouseDown(s.X + 5, cy.Top + 2, cfg);
        E(cfg.Align == QbarAlign.Center, "cycle advances Left -> Center");
        s.MouseDown(s.X + 5, s.Rows.Find(r => r.Act == SetAct.CycleAlign)!.Top + 2, cfg);
        E(cfg.Align == QbarAlign.Right, "cycle advances Center -> Right");

        // Toggle bars
        s.OpenAt(1280, 800, cfg); s.MouseUp();
        SetRow bars = s.Rows.Find(r => r.Bit == BitBars)!;
        s.MouseDown(s.X + 5, bars.Top + 2, cfg);
        E(!cfg.Bars, "bars toggled off");

        // Toggle hidden
        s.OpenAt(1280, 800, cfg); s.MouseUp();
        SetRow hid = s.Rows.Find(r => r.Bit == BitHidden)!;
        s.MouseDown(s.X + 5, hid.Top + 2, cfg);
        E(cfg.Hidden, "hidden toggled on");

        // Toggle compass
        s.OpenAt(1280, 800, cfg); s.MouseUp();
        SetRow comp = s.Rows.Find(r => r.Bit == BitCompass)!;
        s.MouseDown(s.X + 5, comp.Top + 2, cfg);
        E(cfg.CompassHidden, "compass toggled on");

        // Close
        s.OpenAt(1280, 800, cfg); s.MouseUp();
        s.MouseDown(s.X + s.W - 2, s.Y + 2, cfg);
        E(!s.Open, "close button closes");

        // Click-off
        s.OpenAt(1280, 800, cfg); s.MouseUp();
        s.MouseDown(s.X + 5, s.Y + s.H + 40, cfg);
        E(!s.Open, "click-off closes");

        s.Reset();
        log.Info(ok ? "QuickbarSettings.SelfCheck: passed" : "QuickbarSettings.SelfCheck: FAILED");
    }
}
