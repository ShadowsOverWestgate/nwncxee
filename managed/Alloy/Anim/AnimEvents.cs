using System;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// CX-106: an animation's own keyed events, fired through the engine's <c>Gob::DoEvent</c>. Ports
/// <c>AnimateEvents</c> @0x497f30 -- the surface footsteps, hit sounds, projectile detonation and blur all
/// arrive on.
/// </summary>
/// <remarks>
/// <para>The window is the run's time step, and it is HALF-OPEN at the low end: forward, an event fires when
/// <c>prevTime &lt; time_e &lt;= time</c>; in reverse, when <c>time &lt;= time_e &lt; prevTime</c>. A frame
/// that wraps a looping cycle fires the tail of the cycle first and then its head, so events stay in time
/// order across the seam. Forward order is ascending event index, reverse is descending.</para>
/// <para><b>One deliberate improvement over vanilla.</b> A handler can destroy the gob -- that is what the
/// <c>SafePointer</c> in <see cref="AnimRuntime"/> is for -- and the engine's <c>AnimateEvents</c> does not
/// check it, so the remaining events of that frame fire on freed memory. Here the guard is threaded in and
/// the walk stops at the first event that killed the gob. Listed as a difference; it can only turn a crash
/// into a missed event.</para>
/// </remarks>
internal static unsafe class AnimEvents
{
    /// <summary>The engine's "past either end" sentinels, used verbatim so a wrapped window splits at the
    /// same places.</summary>
    private const float Far = 1e9f, Near = -1e9f;
    /// <summary>Flag bit 1 of the <c>reverse</c> argument: play the whole animation's events at once. Nothing
    /// in <c>Gob::Animate</c> passes it; it exists for callers that fire an animation's events outright.</summary>
    private const int Whole = 2, Reverse = 1;

    internal static int Fired;

    /// <summary>Fire every event of <paramref name="anim"/> inside the window, on <paramref name="gob"/>.
    /// <paramref name="alive"/> points at the runtime's <c>SafePointer</c> object slot: zero means an event
    /// already destroyed the gob and nothing more may be fired.</summary>
    internal static void Fire(IntPtr gob, IntPtr anim, float from, float to, int flags, IntPtr* alive)
    {
        if (anim == IntPtr.Zero) return;
        if ((flags & Whole) != 0) { flags &= ~Whole; from = Near; to = Far; }
        var a = new Animation(anim);
        if ((flags & Reverse) == 0)
        {
            if (from > to) { Fire(gob, anim, from, Far, flags, alive); if (*alive == IntPtr.Zero) return; from = Near; }
            for (int i = 0; i < a.EventCount; i++)
            {
                byte* e = (byte*)a.Events + i * EngineLayout.AnimEventStride;
                float at = *(float*)e;
                if (at <= to && from < at)
                {
                    Fired++;
                    new Gob(gob).DoEvent(e + EngineLayout.AnimEventName, IntPtr.Zero);
                    if (*alive == IntPtr.Zero) return;
                }
            }
            return;
        }
        if (from < to) { Fire(gob, anim, from, Near, flags, alive); if (*alive == IntPtr.Zero) return; from = Far; }
        for (int i = a.EventCount - 1; i >= 0; i--)
        {
            byte* e = (byte*)a.Events + i * EngineLayout.AnimEventStride;
            float at = *(float*)e;
            if (to <= at && at < from)
            {
                Fired++;
                new Gob(gob).DoEvent(e + EngineLayout.AnimEventName, IntPtr.Zero);
                if (*alive == IntPtr.Zero) return;
            }
        }
    }
}
