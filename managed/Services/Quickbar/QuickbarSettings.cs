using Sowx.Framework;
using Sowx.Ui;

namespace Sowx.Services.Quickbar;

/// <summary>The quickbar-side consumer of the shared <see cref="SettingsPanel"/>: a small floating panel opened
/// by <c>/settings</c>. It owns the content only -- position cycle, black bars, hide quickbar, hide compass,
/// snap grid (+ size) and edge snap -- and what each press does to the <see cref="QuickbarConfig"/>. The panel
/// owns position, drag-by-title, the close hit, the metrics, the row list, hit-test-to-row and the draw.
/// ponytail: no sliders and no inline entry here, so <see cref="SettingsPanel.ApplyValue"/> /
/// <see cref="SettingsPanel.CommitLabel"/> stay unwired -- wire them if a numeric quickbar option ever lands.</summary>
public sealed class QuickbarSettings
{
    /// <summary>Design-px panel width, before <see cref="Metrics.Scale"/>.</summary>
    private const float PanelW = 220f;

    /// <summary>The shared panel this window's rows are drawn and hit-tested by (the renderer calls
    /// <see cref="SettingsPanel.Emit"/> on it).</summary>
    public readonly SettingsPanel Panel;

    // The model the panel's callbacks edit. Stashed at each public entry point because the service passes the
    // live config per call; a null config means "no model" and the callbacks no-op.
    private QuickbarConfig? _cfg;

    public QuickbarSettings()
    {
        Panel = new SettingsPanel(PanelW)
        {
            Relayout = () => { if (_cfg != null) Layout(_cfg); },
            RowPressed = OnRowPressed,
        };
    }

    public float X { get => Panel.X; set => Panel.X = value; }
    public float Y { get => Panel.Y; set => Panel.Y = value; }
    public float W => Panel.W;
    public float H => Panel.H;
    public bool Open => Panel.Open;
    public List<SetRow> Rows => Panel.Rows;

    // Bit discriminators for ToggleBit rows.
    private const uint BitBars = 1, BitHidden = 2, BitCompass = 3, BitGrid = 4, BitEdge = 5;

    private static readonly int[] GridSizes = { 25, 50, 75, 100, 150, 200 };
    private static int NextGridSize(int cur)
    {
        for (int i = 0; i < GridSizes.Length; i++)
            if (GridSizes[i] == cur) return GridSizes[(i + 1) % GridSizes.Length];
        return GridSizes[0];
    }

    private static float RowH => SettingsPanel.RowH;

    /// <summary>Rebuild the row list from the live config, publishing a fresh list to the panel.</summary>
    public void Layout(QuickbarConfig cfg)
    {
        _cfg = cfg;
        var rows = new List<SetRow>();
        float y = Panel.ContentTop;

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

        rows.Add(new SetRow { Kind = SetKind.Toggle, Act = SetAct.ToggleBit, Label = "Snap grid",
            On = cfg.GridOn, Top = y, Height = RowH, Bit = BitGrid });
        y += RowH;

        if (cfg.GridOn)
        {
            rows.Add(new SetRow { Kind = SetKind.Cycle, Act = SetAct.CycleGridSize, Label = "Grid size",
                Value = cfg.GridPx.ToString(System.Globalization.CultureInfo.InvariantCulture) + " px",
                Top = y, Height = RowH });
            y += RowH;
        }

        rows.Add(new SetRow { Kind = SetKind.Toggle, Act = SetAct.ToggleBit, Label = "Edge snap",
            On = cfg.EdgeSnap, Top = y, Height = RowH, Bit = BitEdge });
        y += RowH;

        Panel.Publish(rows, y);
    }

    /// <summary>Open the panel centred in a <paramref name="vpW"/>×<paramref name="vpH"/> viewport.</summary>
    public void OpenAt(float vpW, float vpH, QuickbarConfig cfg)
    {
        Panel.BeginOpen();
        Layout(cfg);
        X = (vpW - W) / 2f; Y = (vpH - H) / 2f;
        if (X < 0f) X = 0f;
        if (Y < 0f) Y = 0f;
        Layout(cfg);
    }

    public bool OverClose(float mx, float my) => Panel.OverClose(mx, my);

    internal bool Contains(float mx, float my) => Panel.Contains(mx, my);

    /// <summary>Pointer press. Returns true when consumed.</summary>
    public bool MouseDown(float mx, float my, QuickbarConfig cfg)
    {
        _cfg = cfg;
        return Panel.MouseDown(mx, my);
    }

    /// <summary>A press on a row: mutate the config. The panel relayouts right after, so a grid-size row
    /// appearing/vanishing shows next frame.</summary>
    private void OnRowPressed(SetRow row)
    {
        QuickbarConfig? cfg = _cfg;
        if (cfg == null) return;
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
                    case BitGrid: cfg.GridOn = !cfg.GridOn; break;
                    case BitEdge: cfg.EdgeSnap = !cfg.EdgeSnap; break;
                }
                break;
            case SetAct.CycleGridSize:
                cfg.GridPx = NextGridSize(cfg.GridPx);
                break;
        }
    }

    public bool MouseMove(float mx, float my) => Panel.MouseMove(mx, my);

    public bool MouseUp() => Panel.MouseUp();

    public bool Moving => Panel.Moving;

    public void Reset() { Panel.Reset(); }

    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"QuickbarSettings.SelfCheck FAIL: {w}"); } }

        var cfg = new QuickbarConfig();
        var s = new QuickbarSettings();
        s.OpenAt(1280, 800, cfg);
        E(s.Open && s.Rows.Count == 6, "opens with 6 rows (grid off hides size)");
        E(s.MouseUp(), "up after open is consumed");

        // The panel owns the geometry now: width from the design width, height from the published layout.
        E(System.Math.Abs(s.W - PanelW * Metrics.Scale) < 0.01f, "panel width is the design width, scaled");
        E(System.Math.Abs(s.H - (SettingsPanel.TitleH + 6f * RowH + SettingsPanel.Pad)) < 0.01f,
          "panel height = title + 6 rows + pad");
        E(ReferenceEquals(s.Rows, s.Panel.Rows), "the rows the service draws are the panel's published rows");

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

        // Title drag: the panel moves and takes its rows with it, and no config bit flips on the way.
        s.OpenAt(1280, 800, cfg); s.MouseUp();
        float px = s.X, py = s.Y, topBefore = s.Rows[0].Top;
        bool barsBefore = cfg.Bars;
        E(s.MouseDown(s.X + s.W / 2f, s.Y + 2f, cfg) && s.Moving, "title-bar press latches a move");
        s.MouseMove(s.X + s.W / 2f + 40f, s.Y + 2f + 25f);
        E(System.Math.Abs(s.X - (px + 40f)) < 0.5f && System.Math.Abs(s.Y - (py + 25f)) < 0.5f,
          "panel drags by the title bar");
        E(System.Math.Abs(s.Rows[0].Top - (topBefore + 25f)) < 0.5f, "rows follow the panel move");
        E(cfg.Bars == barsBefore, "a title drag edits no config");
        E(s.MouseUp() && !s.Moving, "panel move release consumed + ends the drag");

        // Grid toggle + size cycle
        cfg.GridOn = false; cfg.GridPx = 50;
        s.OpenAt(1280, 800, cfg); s.MouseUp();
        SetRow grid = s.Rows.Find(r => r.Bit == BitGrid)!;
        s.MouseDown(s.X + 5, grid.Top + 2, cfg);
        E(cfg.GridOn, "grid toggled on");
        E(s.Rows.Count == 7, "grid on shows size cycle row (the press relayouts)");
        SetRow gsz = s.Rows.Find(r => r.Act == SetAct.CycleGridSize)!;
        s.MouseDown(s.X + 5, gsz.Top + 2, cfg);
        E(cfg.GridPx == 75, "grid size cycles 50 -> 75");
        s.MouseDown(s.X + 5, s.Rows.Find(r => r.Bit == BitGrid)!.Top + 2, cfg);
        E(!cfg.GridOn, "grid toggled off");
        E(s.Rows.Find(r => r.Act == SetAct.CycleGridSize) == null, "grid off hides size row");

        // Edge snap toggle (default on)
        cfg.EdgeSnap = true;
        s.OpenAt(1280, 800, cfg); s.MouseUp();
        SetRow edge = s.Rows.Find(r => r.Bit == BitEdge)!;
        E(edge.On, "edge snap default on");
        s.MouseDown(s.X + 5, edge.Top + 2, cfg);
        E(!cfg.EdgeSnap, "edge snap toggled off");

        s.Reset();
        E(!s.Open && !s.Moving && !s.MouseUp(), "Reset closes and drops press/drag ownership");
        log.Info(ok ? "QuickbarSettings.SelfCheck: passed" : "QuickbarSettings.SelfCheck: FAILED");
    }
}
