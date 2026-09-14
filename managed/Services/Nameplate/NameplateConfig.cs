using System.Globalization;
using CX.Framework;

namespace CX.Services.Nameplate;

/// <summary>Persisted overhead-nameplate / chat-bubble preferences. Flat <c>key=value</c> in
/// <c>cx-nameplate.cfg</c>, stored in the engine's user directory (next to nwn.ini). Every numeric key is
/// clamped on parse, so a hand-edited file can only ever produce a drawable config.</summary>
public sealed class NameplateConfig
{
    /// <summary>Master switch. Off = neither plate nor bubble draws, and the vanilla suppression still stands.</summary>
    public bool On = true;

    /// <summary>0 off, 1 text only, 2 portrait + background (vanilla parity).</summary>
    public int Mode = 2;

    public bool Outline = true;

    /// <summary>Independent of <see cref="Mode"/>; Mode 2 forces it on at defaults.</summary>
    public bool Background = true;

    public bool Portraits = true;
    public bool HealthBand = true;

    /// <summary>Thin band-coloured bar under the name. Ours, not vanilla; off by default.</summary>
    public bool HealthBar;

    /// <summary>Metres; 0 = unlimited.</summary>
    public float MaxDistance = 45f;

    /// <summary>Fade band immediately before <see cref="MaxDistance"/>.</summary>
    public float FadeDistance = 10f;

    /// <summary>Max wrapped lines kept per bubble. Defaults to the whole ceiling: anything lower cuts text
    /// vanilla would have shown, which is the complaint this default answers.</summary>
    public int BubbleLines = 32;

    /// <summary>How many recent lines stack (vanilla = 1).</summary>
    public int BubbleQueue = 2;

    /// <summary>Max width of BOTH a bubble box and a plate's name column, design px before
    /// <see cref="CX.Ui.Metrics.Scale"/>. One knob for one thing -- the widest an overhead element's text is
    /// allowed to get -- kept under the <c>bubblewidth</c> key so existing cfg files keep working.
    /// Paragraph-length emotes are what the ceiling is for, and 260 wrapped them into a narrow column.</summary>
    public float BubbleWidth = 300f;

    /// <summary>Per-channel colour / emphasis. Off = one neutral style.</summary>
    public bool ChannelStyle = true;

    /// <summary>How long a plate/bubble takes to fade in and slide up into place, ms. 0 = no animation.</summary>
    public float AnimMs = 180f;

    /// <summary>Click a plate's NAME to prefill the chat entry with <c>/tell &lt;name&gt; </c>. Off by default:
    /// a plate sits over the creature it names, so any clickable region takes a click the game world would
    /// otherwise have had (attack, talk, use). Opt-in, and deliberately only the name glyphs.</summary>
    public bool ClickToTell;

    public const int ModeMin = 0, ModeMax = 2;
    /// <summary>The queue ceiling is <see cref="PlateTable.MaxLines"/> -- the table cannot hold more, so a
    /// higher bound would report a value the drain silently clamps away. Self-checked against it.</summary>
    public const int QueueMin = 1, QueueMax = 4;
    /// <summary>The wrapped-line ceiling is <see cref="PlateTable.MaxWrapped"/> -- the line's span array cannot
    /// hold more. Self-checked against it.</summary>
    public const int LinesMin = 1, LinesMax = 32;
    /// <summary>Design-px bounds of the text wrap width (bubbles AND plate names); matches the settings
    /// slider's range.</summary>
    public const float WidthMin = 120f, WidthMax = 480f;
    /// <summary>Bounds of the animation interval, ms; matches the settings slider's range. The ceiling is a
    /// second, which is already far past anything that reads as UI feedback rather than as lag.</summary>
    public const float AnimMin = 0f, AnimMax = 1000f;
    /// <summary>Upper bound of the distance knobs, so a malformed file cannot ask for an unbounded cull radius.</summary>
    public const float DistMax = 100f;

    public string ToText() =>
        $"on={(On ? 1 : 0)}\nmode={Mode}\noutline={(Outline ? 1 : 0)}\nbackground={(Background ? 1 : 0)}\n" +
        $"portraits={(Portraits ? 1 : 0)}\nhealthband={(HealthBand ? 1 : 0)}\nhealthbar={(HealthBar ? 1 : 0)}\n" +
        $"maxdist={F(MaxDistance)}\nfadedist={F(FadeDistance)}\n" +
        $"bubblelines={BubbleLines}\nbubblequeue={BubbleQueue}\nbubblewidth={F(BubbleWidth)}\n" +
        $"channelstyle={(ChannelStyle ? 1 : 0)}\nclicktotell={(ClickToTell ? 1 : 0)}\n" +
        $"animms={F(AnimMs)}\n";

    /// <summary>Invariant formatting on the WRITE side too: a comma-decimal desktop would otherwise emit
    /// <c>1,0</c> and lose the value on the next load.</summary>
    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static bool TryF(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    public static NameplateConfig Parse(string text)
    {
        var cfg = new NameplateConfig();
        ConfigFile.ForEachPair(text, (key, val) =>
        {
            switch (key)
            {
                case "on": if (int.TryParse(val, out int on)) cfg.On = on != 0; break;
                case "mode": if (int.TryParse(val, out int m)) cfg.Mode = Clamp(m, ModeMin, ModeMax); break;
                case "outline": if (int.TryParse(val, out int ol)) cfg.Outline = ol != 0; break;
                case "background": if (int.TryParse(val, out int bg)) cfg.Background = bg != 0; break;
                case "portraits": if (int.TryParse(val, out int po)) cfg.Portraits = po != 0; break;
                case "healthband": if (int.TryParse(val, out int hb)) cfg.HealthBand = hb != 0; break;
                case "healthbar": if (int.TryParse(val, out int hr)) cfg.HealthBar = hr != 0; break;
                case "maxdist": if (TryF(val, out float md)) cfg.MaxDistance = Clamp(md, 0f, DistMax); break;
                case "fadedist": if (TryF(val, out float fd)) cfg.FadeDistance = Clamp(fd, 0f, DistMax); break;
                case "bubblelines": if (int.TryParse(val, out int bl)) cfg.BubbleLines = Clamp(bl, LinesMin, LinesMax); break;
                case "bubblequeue": if (int.TryParse(val, out int bq)) cfg.BubbleQueue = Clamp(bq, QueueMin, QueueMax); break;
                case "bubblewidth": if (TryF(val, out float bw)) cfg.BubbleWidth = Clamp(bw, WidthMin, WidthMax); break;
                case "channelstyle": if (int.TryParse(val, out int cs)) cfg.ChannelStyle = cs != 0; break;
                case "clicktotell": if (int.TryParse(val, out int ct)) cfg.ClickToTell = ct != 0; break;
                case "animms": if (TryF(val, out float am)) cfg.AnimMs = Clamp(am, AnimMin, AnimMax); break;
            }
        });
        return cfg;
    }

    /// <summary>Off-device proof of the round-trip, the defaults, every clamp, and that malformed input degrades
    /// to defaults instead of throwing.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"NameplateConfig.SelfCheck FAIL: {w}"); } }

        var cfg = new NameplateConfig
        {
            On = false, Mode = 1, Outline = false, Background = false, Portraits = false,
            HealthBand = false, HealthBar = true, MaxDistance = 30f, FadeDistance = 5f,
            BubbleLines = 6, BubbleQueue = 3, BubbleWidth = 340f, ChannelStyle = false, ClickToTell = true,
            AnimMs = 250f,
        };
        var rt = Parse(cfg.ToText());
        E(!rt.On, "on round-trips (off)");
        E(rt.Mode == 1, "mode round-trips");
        E(!rt.Outline, "outline round-trips (off)");
        E(!rt.Background, "background round-trips (off)");
        E(!rt.Portraits, "portraits round-trips (off)");
        E(!rt.HealthBand, "healthband round-trips (off)");
        E(rt.HealthBar, "healthbar round-trips (on)");
        E(System.Math.Abs(rt.MaxDistance - 30f) < 1e-5f, "maxdist round-trips");
        E(System.Math.Abs(rt.FadeDistance - 5f) < 1e-5f, "fadedist round-trips");
        E(rt.BubbleLines == 6, "bubblelines round-trips");
        E(rt.BubbleQueue == 3, "bubblequeue round-trips");
        E(System.Math.Abs(rt.BubbleWidth - 340f) < 1e-5f, "bubblewidth round-trips");
        E(!rt.ChannelStyle, "channelstyle round-trips (off)");
        E(rt.ClickToTell, "clicktotell round-trips (on)");
        E(System.Math.Abs(rt.AnimMs - 250f) < 1e-5f, "animms round-trips");

        var def = Parse("");
        E(def.On, "default on is true");
        E(def.Mode == 2, "default mode is 2");
        E(def.Outline, "default outline is true");
        E(def.Background, "default background is true");
        E(def.Portraits, "default portraits is true");
        E(def.HealthBand, "default healthband is true");
        E(!def.HealthBar, "default healthbar is false");
        E(def.MaxDistance == 45f, "default maxdist is 45");
        E(def.FadeDistance == 10f, "default fadedist is 10");
        E(def.BubbleLines == 32, "default bubblelines is the whole wrapped-line ceiling");
        E(def.BubbleQueue == 2, "default bubblequeue is 2");
        E(def.BubbleWidth == 300f, "default bubblewidth is 300");
        E(def.ChannelStyle, "default channelstyle is true");
        E(def.AnimMs == 180f, "default animms is 180");
        E(!def.ClickToTell, "default clicktotell is false -- a plate must never steal a world click by accident");

        var lo = Parse("mode=-4\nmaxdist=-9\nfadedist=-1\nbubblelines=0\nbubblequeue=0\nbubblewidth=1\nanimms=-50\n");
        E(lo.Mode == ModeMin, "mode clamps up to 0");
        E(lo.MaxDistance == 0f, "maxdist clamps up to 0");
        E(lo.FadeDistance == 0f, "fadedist clamps up to 0");
        E(lo.BubbleLines == LinesMin, "bubblelines clamps up to 1");
        E(lo.BubbleQueue == QueueMin, "bubblequeue clamps up to 1");
        E(lo.BubbleWidth == WidthMin, "bubblewidth clamps up to 120");
        E(lo.AnimMs == AnimMin, "animms clamps up to 0 -- a negative interval would never step");

        var hi = Parse("mode=9\nmaxdist=5000\nfadedist=5000\nbubblelines=99\nbubblequeue=99\nbubblewidth=9999\nanimms=99999\n");
        E(hi.Mode == ModeMax, "mode clamps down to 2");
        E(hi.MaxDistance == DistMax, "maxdist clamps down to 100");
        E(hi.FadeDistance == DistMax, "fadedist clamps down to 100");
        E(hi.BubbleLines == LinesMax, "bubblelines clamps down to the table's wrap ceiling");
        E(LinesMax == PlateTable.MaxWrapped, "the wrapped-line ceiling is exactly what a BubbleLine can hold");
        E(hi.BubbleQueue == QueueMax, "bubblequeue clamps down to the table's line ceiling");
        E(QueueMax == PlateTable.MaxLines, "the queue ceiling is exactly what PlateTable can hold");
        E(hi.BubbleWidth == WidthMax, "bubblewidth clamps down to 480");
        E(hi.AnimMs == AnimMax, "animms clamps down to a second");

        var junk = Parse("garbage\n=5\nmode\nmode=abc\nmaxdist=not-a-number\n\n   \non=1\n");
        E(junk.Mode == 2, "malformed mode leaves the default");
        E(junk.MaxDistance == 45f, "malformed maxdist leaves the default");
        E(junk.On, "a good key still applies after malformed lines");

        // The documented comma-decimal-locale bug: a dot decimal must survive whatever the desktop locale is.
        E(System.Math.Abs(Parse("maxdist=12.5\n").MaxDistance - 12.5f) < 1e-5f, "dot decimals parse locale-independently");
        E(new NameplateConfig { MaxDistance = 12.5f }.ToText().Contains("maxdist=12.5"),
          "floats are written with a dot decimal");

        log.Info(ok ? "NameplateConfig.SelfCheck: passed" : "NameplateConfig.SelfCheck: FAILED");
    }
}
