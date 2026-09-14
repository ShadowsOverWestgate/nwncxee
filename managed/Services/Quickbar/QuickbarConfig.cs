using CX.Framework;

namespace CX.Services.Quickbar;

/// <summary>Persisted quickbar/HUD settings: alignment, bars toggle, hide toggles. Flat key=value in
/// <c>cx-quickbar.cfg</c>, stored in the engine's user directory (next to nwn.ini).</summary>
public sealed class QuickbarConfig
{
    public QbarAlign Align = QbarAlign.Center;
    public bool Bars = true;
    public bool Hidden;
    public bool CompassHidden;
    public bool GridOn;
    public int GridPx = 50;
    public bool EdgeSnap = true;

    /// <summary>#232 second mode: highlighted objects draw as an owned outline + 50% fill instead of the
    /// engine's glow gobs. Read at frame time by the Alloy highlight pass via
    /// <c>QuickbarService.OutlineHighlight</c>.</summary>
    public bool OutlineHl;

    public string ToText() =>
        $"align={(int)Align}\nbars={(Bars ? 1 : 0)}\nhidden={(Hidden ? 1 : 0)}\ncompass={(CompassHidden ? 1 : 0)}\n" +
        $"grid={(GridOn ? 1 : 0)}\ngridpx={GridPx}\nedgesnap={(EdgeSnap ? 1 : 0)}\noutlinehl={(OutlineHl ? 1 : 0)}\n";

    public static QuickbarConfig Parse(string text)
    {
        var cfg = new QuickbarConfig();
        ConfigFile.ForEachPair(text, (key, val) =>
        {
            switch (key)
            {
                case "align":
                    if (int.TryParse(val, out int a) && a >= 0 && a <= 2) cfg.Align = (QbarAlign)a;
                    break;
                case "bars":
                    if (int.TryParse(val, out int b)) cfg.Bars = b != 0;
                    break;
                case "hidden":
                    if (int.TryParse(val, out int h)) cfg.Hidden = h != 0;
                    break;
                case "compass":
                    if (int.TryParse(val, out int c)) cfg.CompassHidden = c != 0;
                    break;
                case "grid":
                    if (int.TryParse(val, out int g)) cfg.GridOn = g != 0;
                    break;
                case "gridpx":
                    if (int.TryParse(val, out int gp) && gp >= 10 && gp <= 500) cfg.GridPx = gp;
                    break;
                case "edgesnap":
                    if (int.TryParse(val, out int es)) cfg.EdgeSnap = es != 0;
                    break;
                case "outlinehl":
                    if (int.TryParse(val, out int oh)) cfg.OutlineHl = oh != 0;
                    break;
            }
        });
        return cfg;
    }

    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"QuickbarConfig.SelfCheck FAIL: {w}"); } }

        var cfg = new QuickbarConfig { Align = QbarAlign.Right, Bars = false, Hidden = true, CompassHidden = true, GridOn = true, GridPx = 75, EdgeSnap = false, OutlineHl = true };
        string text = cfg.ToText();
        var rt = Parse(text);
        E(rt.Align == QbarAlign.Right, "align round-trips");
        E(!rt.Bars, "bars round-trips");
        E(rt.Hidden, "hidden round-trips");
        E(rt.CompassHidden, "compass round-trips");
        E(rt.GridOn, "grid round-trips");
        E(rt.GridPx == 75, "gridpx round-trips");
        E(!rt.EdgeSnap, "edgesnap round-trips (off)");
        E(rt.OutlineHl, "outlinehl round-trips");

        var def = Parse("");
        E(def.Align == QbarAlign.Center, "default align is Center");
        E(def.Bars, "default bars is true");
        E(!def.Hidden, "default hidden is false");
        E(!def.CompassHidden, "default compass is false");
        E(!def.GridOn, "default grid is false");
        E(def.GridPx == 50, "default gridpx is 50");
        E(def.EdgeSnap, "default edgesnap is true");
        E(!def.OutlineHl, "default outlinehl is false");

        log.Info(ok ? "QuickbarConfig.SelfCheck: passed" : "QuickbarConfig.SelfCheck: FAILED");
    }
}
