using System;
using System.Numerics;
using CX.Engine;

namespace CX.Services.Anim;

/// <summary>
/// The skating the engine cannot fix, a <see cref="Stride"/> companion under <c>anim.stride</c>, and the pose-read
/// tests that recognise it: the combat shuffle the engine never paces (<see cref="IsShuffle"/>), clips whose feet miss
/// <c>appearance.2da</c>'s assumed speed (<see cref="Paced"/>, worst −16 % on <c>runfl</c>/<c>runfr</c>), and poses not
/// on the ground at all (<see cref="Bears"/>, <see cref="Liftoff"/>) -- <c>animationsets.2da</c> hides swims and floats behind <c>walk</c>.
/// </summary>
internal static unsafe class Skating
{
    /// <summary>Seconds without a weight-bearing foot after which the pose is taken to be off the ground. Longer
    /// than any stock cycle's flight (a run's is 0.17 s between stances) and shorter than the shortest
    /// correction the shuffle covers.</summary>
    internal const float Liftoff = 0.5f;
    /// <summary>The share of the ground speed a foot must travel backward against the body to count as bearing
    /// weight. Well under what any plausible playback rate leaves (a cycle 2.5x off still gives 0.4), and well
    /// over the drift of a foot the body is carrying.</summary>
    private const float BearsShare = 0.35f;
    /// <summary>Ground speed (model units/s) below which there is no verdict: a body barely moving gives a
    /// planted foot nothing to travel backward by, so every pose would read as carried.</summary>
    private const float MinGround = 0.3f;
    /// <summary>Milliseconds per second: <c>correctionMs</c> is a whole-millisecond countdown.</summary>
    private const float Milli = 1000f;

    /// <summary>The combat shuffle's animation ids, the four <c>MoveCreatureToPosition</c> picks from by the dot of
    /// the facing against the gap: forward, back, left, right. It plays them fire-and-forget at rate 1 and never paces
    /// them -- <c>CNWCObject::AnimationStationary</c> answers "stationary" for these ids, so <c>AnimationUpdate</c>
    /// never reaches <c>WalkUpdateLocation</c>, the one place outside <c>Gob::PlayAnimation</c> writing a run's rate.</summary>
    internal static bool IsShuffle(int anim) => anim is >= 0x18 and <= 0x1b;  // not-an-offset: the engine's animation ids

    /// <summary>The ground speed (world units/s) a creature's position correction is covering: the horizontal gap
    /// still to close over the countdown still to run. <c>MoveCreatureToPosition</c> closes the gap by
    /// <c>remaining/(remaining+step)</c> after taking the step off the countdown, which scales both by the same
    /// factor every update and so leaves the ratio constant for the whole correction.</summary>
    internal static float Speed(IntPtr creature, float measured)
    {
        if (creature == IntPtr.Zero) return measured;
        var c = new CNWCCreature(creature);
        Vector3 d = c.DesiredPos - new CNWCObject(creature).Position;
        return Speed(MathF.Sqrt(d.X * d.X + d.Y * d.Y), c.CorrectionMs, measured);
    }

    /// <inheritdoc cref="Speed(IntPtr,float)"/>
    /// <remarks>A spent countdown is the engine snapping to the desired position instead of lerping, and leaves
    /// nothing to read: the measured step stands in.</remarks>
    internal static float Speed(float gap, uint ms, float measured) => ms == 0 ? measured : gap * Milli / ms;

    /// <summary>Whether a foot travelling <paramref name="back"/> against the body (model units per second per
    /// unit of engine rate, positive = opposite the travel) is bearing the body's weight at ground speed
    /// <paramref name="ground"/>. Below <see cref="MinGround"/> there is no travel to judge by and the answer is
    /// yes, so a body standing still, turning on the spot or barely creeping keeps its foot lock.</summary>
    internal static bool Bears(float back, float ground) => !(ground > MinGround) || back > BearsShare * ground;

    /// <summary>The engine's assumed animation speed for a clip: <c>appearance.2da</c>'s WALKDIST or RUNDIST over
    /// the clip's own length, which is what <c>WalkUpdateLocation</c> divides the ground speed by to get the
    /// playback rate. A clip whose feet do not match it skates by exactly that ratio at every speed.</summary>
    private static float Paced(float measured, float reference, float ground) => measured * ground / reference;

    /// <summary>A mis-paced clip: the correction <see cref="Stride"/> would apply to a clip the engine plays
    /// against <paramref name="reference"/> when its feet actually measure <paramref name="measured"/>, which is
    /// <c>reference/measured</c> at every ground speed. <paramref name="want"/> is that ratio, or 1 when the clip
    /// is close enough that the deadband should leave it alone.</summary>
    private static bool Clip(float measured, float reference, float want)
    {
        for (float ground = 0.8f; ground <= 4.2f; ground += 0.35f)
            if (MathF.Abs(Stride.Factor(ground, Paced(measured, reference, ground)) - want) > 2e-3f) return false;
        return true;
    }

    /// <summary>Runs <paramref name="frames"/> frames of a cycle through <see cref="Stride.Measure"/> with the ankles
    /// placed by hand, and reports the rate multiplier and ground verdict it ends on: the body travels along model +Y
    /// at <paramref name="ground"/>, each foot bears <paramref name="back"/> backward (0 = carried) for
    /// <paramref name="duty"/> of <paramref name="cycle"/> and rises <paramref name="lift"/> in flight, where the foot's direction of travel, not the height gate, refuses the sample.</summary>
    private static void Cycle(float ground, float back, float duty, float lift, float cycle, int frames,
                              out float rate, out bool grounded)
    {
        PoseService.Slot s = default;
        s.RateF = 1f;
        s.Loco = true;
        Vector3 r = new(0.1f, 0f, 0f), l = new(-0.1f, 0f, 0f);
        const float Dt = 1f / 60f;
        for (int i = 0; i < frames; i++)
        {
            s.RateF = 1f + s.Factor;
            float step = back * s.RateF * Dt, t = i * Dt % cycle, half = cycle * 0.5f;
            bool right = t < half;
            bool down = (right ? t : t - half) < duty * cycle;
            float bearing = -step, swing = 2f * step;
            r += new Vector3(0f, down && right ? bearing : swing, 0f);
            l += new Vector3(0f, down && !right ? bearing : swing, 0f);
            r.Z = down && right ? 0f : right ? lift : lift * 0.5f;
            l.Z = down && !right ? 0f : right ? lift * 0.5f : lift;
            Stride.Measure(ref s, r, l, Vector3.UnitY, ground, false, Dt);
        }
        rate = 1f + s.Factor;
        grounded = s.Grounded;
    }

    /// <summary>The shuffle's rate for a known gap and countdown; the factor for each clip the engine mis-paces;
    /// and the ground verdict on a walk, a float and a crawl.</summary>
    internal static bool SelfCheck()
    {
        // the shuffle: 0.6 m still to cover over the 512 ms a server correction opens is 1.172 m/s, which on
        // cwalkf's own 1.011 m/s stride is a 16 % speed-up; a spent countdown falls back to the measured step
        float fore = Speed(0.6f, 512, 9f), side = Speed(1.0f, 512, 9f);
        if (!IsShuffle(0x18) || !IsShuffle(0x1b) || IsShuffle(0x17) || IsShuffle(0x1c) || IsShuffle(2)) return false;
        if (MathF.Abs(fore - 1.171875f) > 1e-4f || MathF.Abs(Speed(0.6f, 0, 9f) - 9f) > 1e-4f
            || MathF.Abs(Stride.Factor(fore, 1.011f) - fore / 1.011f) > 1e-4f
            || MathF.Abs(Stride.Factor(side, 1.61f) - side / 1.61f) > 1e-4f) return false;
        // a gap the shuffle barely has to close, and one it has to sprint over, are both out of range: left alone
        if (Stride.Factor(Speed(0.25f, 512, 0f), 1.011f) != 1f || Stride.Factor(Speed(3.9f, 60, 0f), 1.011f) != 1f)
            return false;

        // the clips measured, speed against the engine's reference
        if (!Clip(4.02f, 4.80f, 4.80f / 4.02f)                  // runfl/runfr, human male: -16.2 %
            || !Clip(3.7632f, 4.80f, 4.80f / 3.7632f)           // runfl/runfr, halfling female: -21.6 %, the worst
            || !Clip(1.469f, 1.600f, 1.600f / 1.469f)           // the female walk: -8.2 %
            || !Clip(0.945f, 1.060f, 1.060f / 0.945f)           // the dwarf male walk: -10.8 %
            || !Clip(0.889f, 1.060f, 1.060f / 0.889f)           // the dwarf female walk: -16.1 %
            || !Clip(0.862f, 1.040f, 1.040f / 0.862f)           // the halfling female walk: -17.1 %
            || !Clip(1.585f, 1.600f, 1f)                        // the male walk is right: inside the deadband
            || !Clip(3.994f, 4.000f, 1f)) return false;         // so is the male run

        // a female walk at 2.0 m/s: the engine plays it 1.25x, its feet carry 1.836 and the correction is 1.089
        Cycle(2.0f, 1.836f, 0.5f, 0.12f, 1.0f, 180, out float walk, out bool onWalk);
        if (!onWalk || MathF.Abs(walk - 2.0f / 1.836f) > 0.02f) return false;
        // a run's 41 % flight is the gap the verdict has to survive: 0.17 s between stances, a third of Liftoff
        Cycle(4.0f, 3.35f, 0.29f, 0.25f, 0.8f, 180, out float run, out bool onRun);
        if (!onRun || MathF.Abs(run - 4.0f / 3.35f) > 0.03f) return false;

        // floating (ar_walk, root 1.50 m): the body travels and the feet are carried, so none of them ever bears
        Cycle(2.0f, 0f, 0.5f, 0.12f, 1.0f, 180, out float floated, out bool onFloat);
        if (onFloat || MathF.Abs(floated - 1f) > 0.01f) return false;
        // crawling: the ankles are dragged along, moving a little against the body but nothing like carrying it
        Cycle(2.0f, 0.3f, 0.5f, 0.12f, 1.0f, 180, out float crawled, out bool onCrawl);
        return !onCrawl && MathF.Abs(crawled - 1f) < 0.01f;
    }
}
