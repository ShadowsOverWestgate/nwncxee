using System;
using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>
/// Time-driven chatbox animation state + curves (map #13 polish). Pure math + a few static floats, no
/// native refs, so it self-checks off-device. Two moving parts:
/// <list type="bullet">
/// <item><b>Fade</b> -- a per-line alpha: fully visible for <see cref="HoldSec"/>, then ramped to 0 over
/// <see cref="FadeSec"/>. Age is measured from <c>max(line, lastScroll)</c>, so a scroll
/// resets that window's lines (the messages reappear, then fade again a minute later).</item>
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
    /// <summary>Below this many pixels the residual is snapped to 0 (ends the animation cleanly).</summary>
    private const float SnapPx = 0.5f;

    /// <summary>Live body pixel offset, eased toward 0 by <see cref="Tick"/>. Positive = body drawn lower.</summary>
    public static float Residual;


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

    /// <summary>Advance the residual to <paramref name="nowMs"/> (monotonic ms) and return it. Called once
    /// per frame in the hot draw. <paramref name="tauMs"/> comes from the focused window (#41).</summary>
    public static float Tick(long nowMs, float tauMs = 70f)
    {
        double tau = tauMs / 1000.0 <= 0 ? 0.001 : tauMs / 1000.0;
        if (!_primed) { _primed = true; _lastTickMs = nowMs; return Residual; }
        double dt = (nowMs - _lastTickMs) / 1000.0;
        _lastTickMs = nowMs;
        if (Residual != 0f)
        {
            Residual *= (float)Math.Exp(-dt / tau);
            if (Residual < SnapPx && Residual > -SnapPx) Residual = 0f;
        }
        return Residual;
    }

    /// <summary>Alpha multiplier for a line whose message time is <paramref name="ts"/>, at <paramref name="now"/>,
    /// given the shared <paramref name="lastScroll"/>. Uses per-window hold/fade from the RenderFrame.</summary>
    public static float FadeAlpha(DateTime now, DateTime ts, DateTime lastScroll,
                                  bool enabled, double holdSec, double fadeSec)
    {
        if (!enabled) return 1f;
        DateTime from = ts > lastScroll ? ts : lastScroll;
        double age = (now - from).TotalSeconds;
        if (age <= holdSec) return 1f;
        if (age >= holdSec + fadeSec) return 0f;
        return (float)(1.0 - (age - holdSec) / fadeSec);
    }

    /// <summary>Off-device proof of the fade curve, the scroll reset, and residual decay+snap.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        double holdSec = 60.0, fadeSec = 6.0;
        var t0 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime never = DateTime.MinValue;
        check.Expect(FadeAlpha(t0, t0, never, true, holdSec, fadeSec) == 1f, "fresh line fully opaque");
        check.Expect(FadeAlpha(t0.AddSeconds(holdSec - 1), t0, never, true, holdSec, fadeSec) == 1f, "within hold still opaque");
        check.Expect(Math.Abs(FadeAlpha(t0.AddSeconds(holdSec + fadeSec / 2), t0, never, true, holdSec, fadeSec) - 0.5f) < 1e-4, "mid-fade at hold+half");
        check.Expect(FadeAlpha(t0.AddSeconds(holdSec + fadeSec + 1), t0, never, true, holdSec, fadeSec) == 0f, "gone past hold+fade");
        double past = holdSec + fadeSec + 60;
        check.Expect(FadeAlpha(t0.AddSeconds(past), t0, t0.AddSeconds(past), true, holdSec, fadeSec) == 1f, "scroll resets the fade clock");

        // Residual decays toward 0 and snaps.
        Residual = 100f; _primed = false; _lastTickMs = 0;
        Tick(1000);                    // primes the clock, no decay
        float a = Tick(1070);          // one tau -> ~1/e
        check.Expect(a < 100f && a > 20f, "residual decays after ~tau");
        float b = Tick(1070 + 1000);   // long dt collapses under the snap threshold
        check.Expect(b == 0f, "residual snaps to 0");

        // Master toggle off: no fade.
        check.Expect(FadeAlpha(t0.AddSeconds(holdSec + fadeSec + 1), t0, never, false, holdSec, fadeSec) == 1f, "disabled: fully faded line stays opaque");
        Residual = 0f; _primed = false; _lastTickMs = 0;

        // Span stability predicate (#53): all-in-hold -> stable; mid-fade -> not; mixed 0/1 -> stable.
        check.Expect(FadeAlpha(t0, t0, never, true, holdSec, fadeSec) == 1f, "stability: fresh line at 1.0");
        float mid = FadeAlpha(t0.AddSeconds(holdSec + fadeSec / 2), t0, never, true, holdSec, fadeSec);
        check.Expect(mid > 0f && mid < 1f, "stability: mid-fade not at 0 or 1");
        check.Expect(FadeAlpha(t0.AddSeconds(holdSec + fadeSec + 10), t0, never, true, holdSec, fadeSec) == 0f, "stability: fully faded at 0.0");

        log.Info(check.Ok ? "ChatAnim.SelfCheck: passed" : "ChatAnim.SelfCheck: FAILED");
    }
}
