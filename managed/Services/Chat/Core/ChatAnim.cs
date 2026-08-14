using System;
using Sowx.Framework;

namespace Sowx.Services.Chat.Core;

/// <summary>
/// Time-driven chatbox animation state + curves (map #13 polish). Pure math + a few static floats, no
/// native refs, so it self-checks off-device. Two moving parts:
/// <list type="bullet">
/// <item><b>Fade</b> -- a per-line alpha: fully visible for <see cref="HoldSec"/>, then ramped to 0 over
/// <see cref="FadeSec"/>. Age is measured from <c>max(line, <see cref="LastScroll"/>)</c>, so a scroll
/// resets every line's clock (the messages reappear, then fade again a minute later).</item>
/// <item><b>Residual</b> -- one shared pixel offset applied to the whole body, eased toward 0 each frame.
/// Slide-in (a new line pushes the column up) and smooth scroll (a wheel notch slides the gesture out) are
/// the same mechanism: bump the residual, let it decay. Frame-rate independent (exponential toward 0).</item>
/// </list>
/// Read hot (the SwapBuffers draw), written cold (ingest / scroll). <see cref="Residual"/> is lock-free: the
/// compound bumps (+=, *=) aren't atomic, so a bump landing exactly under a Tick's read-modify-write can
/// rarely be lost -- one line then skips its slide, purely cosmetic. ponytail: a lock/Interlocked isn't
/// worth stalling the hot draw to close a cosmetic miss. All values allocation-free.
/// </summary>
public static class ChatAnim
{
    /// <summary>Seconds a line stays fully opaque before it begins to fade. ponytail: fixed feel; a
    /// per-user timeout is the ceiling.</summary>
    public const double HoldSec = 60.0;
    /// <summary>Seconds the fade ramp takes (opaque -&gt; gone) once <see cref="HoldSec"/> passes.</summary>
    public const double FadeSec = 6.0;

    /// <summary>Residual decay time constant (seconds): the e-folding time of the slide/scroll ease.
    /// ~70ms reads as a quick, soft settle. ponytail: one knob for the whole animation's snappiness.</summary>
    private const double Tau = 0.07;
    /// <summary>Below this many pixels the residual is snapped to 0 (ends the animation cleanly).</summary>
    private const float SnapPx = 0.5f;

    /// <summary>Live body pixel offset, eased toward 0 by <see cref="Tick"/>. Positive = body drawn lower.</summary>
    public static float Residual;

    /// <summary>Wall-clock of the last scroll (fade clock reset). MinValue = never scrolled.</summary>
    public static DateTime LastScroll = DateTime.MinValue;

    private static long _lastTickMs;
    private static bool _primed;

    /// <summary>Largest slide/scroll offset the body eases over. Two jobs: it caps the ease distance for a
    /// fast multi-notch scroll (the view still jumps instantly, only the final stretch eases), and it bounds
    /// how far off-viewport content the renderer must emit so nothing pops in/out mid-ease -- WindowRenderer
    /// emits exactly this margin around the body, so a bounded residual guarantees the eased content exists.</summary>
    public const float MaxResidualPx = 160f;

    /// <summary>Add to the shared body residual (cold), clamped to +/-<see cref="MaxResidualPx"/>. Slide-in
    /// bumps +blockHeight; a scroll bumps by the applied gesture (opposite sign) so the new layout eases in
    /// from the pre-gesture position.</summary>
    public static void AddResidual(float px)
    {
        float r = Residual + px;
        Residual = r > MaxResidualPx ? MaxResidualPx : (r < -MaxResidualPx ? -MaxResidualPx : r);
    }

    /// <summary>Mark a scroll now: every line's fade clock restarts, so the messages reappear (cold).</summary>
    public static void MarkScroll() => LastScroll = DateTime.UtcNow;

    /// <summary>Advance the residual to <paramref name="nowMs"/> (monotonic ms) and return it. Called once
    /// per frame in the hot draw; exponential decay keeps it frame-rate independent.</summary>
    public static float Tick(long nowMs)
    {
        if (!_primed) { _primed = true; _lastTickMs = nowMs; return Residual; }
        double dt = (nowMs - _lastTickMs) / 1000.0;
        _lastTickMs = nowMs;
        if (Residual != 0f)
        {
            Residual *= (float)Math.Exp(-dt / Tau);
            if (Residual < SnapPx && Residual > -SnapPx) Residual = 0f;
        }
        return Residual;
    }

    /// <summary>Alpha multiplier for a line whose message time is <paramref name="ts"/>, at <paramref name="now"/>,
    /// given the shared <paramref name="lastScroll"/>. 1 while fresh, 0 once fully faded.</summary>
    public static float FadeAlpha(DateTime now, DateTime ts, DateTime lastScroll)
    {
        DateTime from = ts > lastScroll ? ts : lastScroll;   // scroll resets the clock for every line
        double age = (now - from).TotalSeconds;
        if (age <= HoldSec) return 1f;
        if (age >= HoldSec + FadeSec) return 0f;
        return (float)(1.0 - (age - HoldSec) / FadeSec);
    }

    /// <summary>Off-device proof of the fade curve, the scroll reset, and residual decay+snap.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"ChatAnim.SelfCheck FAIL: {w}"); } }

        var t0 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime never = DateTime.MinValue;
        // Curve asserted in terms of Hold/FadeSec so it survives tuning either constant.
        E(FadeAlpha(t0, t0, never) == 1f, "fresh line fully opaque");
        E(FadeAlpha(t0.AddSeconds(HoldSec - 1), t0, never) == 1f, "within hold still opaque");
        E(Math.Abs(FadeAlpha(t0.AddSeconds(HoldSec + FadeSec / 2), t0, never) - 0.5f) < 1e-4, "mid-fade at hold+half");
        E(FadeAlpha(t0.AddSeconds(HoldSec + FadeSec + 1), t0, never) == 0f, "gone past hold+fade");
        // A scroll well past the fade resets a line ingested at t0: it is opaque again.
        double past = HoldSec + FadeSec + 60;
        E(FadeAlpha(t0.AddSeconds(past), t0, t0.AddSeconds(past)) == 1f, "scroll resets the fade clock");

        // Residual decays toward 0 and snaps.
        Residual = 100f; _primed = false; _lastTickMs = 0;
        Tick(1000);                    // primes the clock, no decay
        float a = Tick(1070);          // one tau -> ~1/e
        E(a < 100f && a > 20f, "residual decays after ~tau");
        float b = Tick(1070 + 1000);   // long dt collapses under the snap threshold
        E(b == 0f, "residual snaps to 0");
        Residual = 0f; _primed = false; _lastTickMs = 0; LastScroll = DateTime.MinValue;   // leave state clean

        log.Info(ok ? "ChatAnim.SelfCheck: passed" : "ChatAnim.SelfCheck: FAILED");
    }
}
