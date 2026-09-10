using System.Globalization;
using Sowx.Framework;

namespace Sowx.Services.CombatText;

/// <summary>Persisted combat-text preferences, flat <c>key=value</c> in <c>sowx-combattext.cfg</c> beside
/// <c>nwn.ini</c>. Every numeric key is clamped on parse, so a hand-edited file can only ever produce a
/// drawable config. Vanilla's own two knobs (<c>ui.floating-text-feedback</c> and
/// <c>ui.floating-text-feedback-damage-totals-only</c>) are NOT duplicated here: the engine still composes
/// the strings, so consolidation and the damage-type colours follow the vanilla settings by construction.</summary>
public sealed class CombatTextConfig
{
    /// <summary>Master switch. Off = the engine's own floaty text is left to run exactly as it always did.</summary>
    public bool On = true;

    /// <summary>Damage numbers alive at once PER TARGET. Older ones are evicted, newest wins.</summary>
    public int Max = 6;

    /// <summary>Lifetime of a normal damage number, ms.</summary>
    public float RiseMs = 1200f;

    /// <summary>How far a damage number scrolls up over its life, design px.</summary>
    public float RisePx = 52f;

    /// <summary>Vertical step between elements stacked on one target, design px. Everything on one head is a
    /// column: a burst of damage types drawn on the same anchor is unreadable, which is the whole reason this
    /// knob exists.</summary>
    public float StackPx = 20f;

    /// <summary>How far above the anchor the first combat-text banner sits, design px, so a banner clears the
    /// damage column instead of landing in it.</summary>
    public float BannerLiftPx = 34f;

    /// <summary>Lifetime of a critical hit, ms. Longer than a normal number: it does not scroll away.</summary>
    public float CritMs = 1700f;

    /// <summary>Size multiplier for a critical hit.</summary>
    public float CritScale = 1.7f;

    /// <summary>Shake amplitude of a critical hit, design px (damped to zero over its life).</summary>
    public float CritShakePx = 3.5f;

    /// <summary>Shake frequency of a critical hit, Hz.</summary>
    public float CritShakeHz = 13f;

    /// <summary>Lifetime of a floating combat-text banner (<c>*Sneak Attack!*</c>), ms.</summary>
    public float TextMs = 1500f;

    /// <summary>Size multiplier for a combat-text banner. It is meant to catch the eye, so it is bigger than
    /// a damage number even at rest.</summary>
    public float TextScale = 1.3f;

    /// <summary>Black outline behind every element. On by default: this text sits over a lit 3D world and is
    /// unreadable against a bright tile without it.</summary>
    public bool Outline = true;

    /// <summary>How long after a critical-hit banner a damage number is still treated as that crit's, ms.
    /// See <c>CombatTextService</c>: the banner rides the ATTACKER and the number rides the TARGET, so the
    /// two cannot be correlated by object id.</summary>
    public float CritWindowMs = 400f;

    public const int MaxMin = 1, MaxMax = 16;
    public const float LifeMin = 200f, LifeMax = 6000f;
    public const float RiseMin = 0f, RiseMax = 400f;
    public const float StackMin = 0f, StackMax = 120f;
    public const float LiftMin = 0f, LiftMax = 300f;
    public const float ScaleMin = 1f, ScaleMax = 4f;
    public const float ShakeMin = 0f, ShakeMax = 20f;
    public const float HzMin = 0f, HzMax = 40f;
    public const float WindowMin = 0f, WindowMax = 3000f;

    public string ToText() =>
        $"on={(On ? 1 : 0)}\nmax={Max}\nrisems={F(RiseMs)}\nrisepx={F(RisePx)}\nstackpx={F(StackPx)}\nbannerliftpx={F(BannerLiftPx)}\n" +
        $"critms={F(CritMs)}\ncritscale={F(CritScale)}\ncritshakepx={F(CritShakePx)}\ncritshakehz={F(CritShakeHz)}\n" +
        $"textms={F(TextMs)}\ntextscale={F(TextScale)}\noutline={(Outline ? 1 : 0)}\ncritwindowms={F(CritWindowMs)}\n";

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static bool TryF(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    public static CombatTextConfig Parse(string text)
    {
        var cfg = new CombatTextConfig();
        ConfigFile.ForEachPair(text, (key, val) =>
        {
            switch (key)
            {
                case "on": if (int.TryParse(val, out int on)) cfg.On = on != 0; break;
                case "max": if (int.TryParse(val, out int mx)) cfg.Max = Clamp(mx, MaxMin, MaxMax); break;
                case "risems": if (TryF(val, out float rm)) cfg.RiseMs = Clamp(rm, LifeMin, LifeMax); break;
                case "risepx": if (TryF(val, out float rp)) cfg.RisePx = Clamp(rp, RiseMin, RiseMax); break;
                case "stackpx": if (TryF(val, out float sp)) cfg.StackPx = Clamp(sp, StackMin, StackMax); break;
                case "bannerliftpx": if (TryF(val, out float bl)) cfg.BannerLiftPx = Clamp(bl, LiftMin, LiftMax); break;
                case "critms": if (TryF(val, out float cm)) cfg.CritMs = Clamp(cm, LifeMin, LifeMax); break;
                case "critscale": if (TryF(val, out float cs)) cfg.CritScale = Clamp(cs, ScaleMin, ScaleMax); break;
                case "critshakepx": if (TryF(val, out float sk)) cfg.CritShakePx = Clamp(sk, ShakeMin, ShakeMax); break;
                case "critshakehz": if (TryF(val, out float hz)) cfg.CritShakeHz = Clamp(hz, HzMin, HzMax); break;
                case "textms": if (TryF(val, out float tm)) cfg.TextMs = Clamp(tm, LifeMin, LifeMax); break;
                case "textscale": if (TryF(val, out float ts)) cfg.TextScale = Clamp(ts, ScaleMin, ScaleMax); break;
                case "outline": if (int.TryParse(val, out int ol)) cfg.Outline = ol != 0; break;
                case "critwindowms": if (TryF(val, out float cw)) cfg.CritWindowMs = Clamp(cw, WindowMin, WindowMax); break;
            }
        });
        return cfg;
    }

    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>Round-trips the defaults through <see cref="ToText"/> / <see cref="Parse"/> and proves the
    /// clamps reject an out-of-range file. Headless.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var a = new CombatTextConfig();
        var b = Parse(a.ToText());
        bool ok = b.On == a.On && b.Max == a.Max && b.RiseMs == a.RiseMs && b.RisePx == a.RisePx
                  && b.StackPx == a.StackPx && b.BannerLiftPx == a.BannerLiftPx
                  && b.CritMs == a.CritMs && b.CritScale == a.CritScale
                  && b.CritShakePx == a.CritShakePx && b.CritShakeHz == a.CritShakeHz
                  && b.TextMs == a.TextMs && b.TextScale == a.TextScale && b.Outline == a.Outline
                  && b.CritWindowMs == a.CritWindowMs;

        var c = Parse("max=999\nrisems=-5\ncritscale=100\noutline=0\n");
        ok &= c.Max == MaxMax && c.RiseMs == LifeMin && c.CritScale == ScaleMax && !c.Outline;

        log.Info(ok ? "CombatTextConfig.SelfCheck: passed" : "CombatTextConfig.SelfCheck: FAILED");
    }
}
