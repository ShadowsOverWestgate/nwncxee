using System;
using System.Numerics;
using CX.Engine;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Stride matching and foot locking, a <see cref="PoseService"/> module (docs/research/procedural-anim-locomotion.md).
/// <para><b>Stride.</b> The engine already paces walk and run: <c>CNWCCreature::WalkUpdateLocation</c> sets the
/// run's rate (<c>AnimRun+0x08</c>) to the ground speed over <c>appearance.2da</c>'s WALKDIST/RUNDIST, a stride
/// length per appearance that a custom cycle, a slope or the two-clock step does not match. Here the stance
/// foot's model-space speed is measured per unit of the engine's own rate and the rate multiplied so it equals
/// the gob's measured ground speed. No table, no content contract: any walk cycle is paced by its own feet.
/// Every run on the gob is scaled alike, so a torso overlay (<c>walk_swordl</c>) stays in step with its walk.</para>
/// <para><b>Attachments follow.</b> A cloak, robe, tail or wings is a gob of its own (<c>CNWCAnimBaseTW::LoadModel</c>
/// attaches it to the body's root) playing the body's clip on its own run list, which the engine paces equal to
/// the body's on every AI update but never re-syncs in time (docs/research/cloak-animation.md). Scaling the body
/// alone drifts them apart -- and vanilla drifts them too, without any help (a cloak equipped mid-stride starts
/// at time 0, rate 1). So each attachment is phase-locked to the body in its own <c>Gob::Animate</c>, just
/// before the body's: <see cref="Sync"/>.</para>
/// <para><b>The rate is not restored</b> like the pose: the engine rewrites it on every AI update, and a frame
/// without one leaves this module's own value, which <see cref="Pre"/> recognises and multiplies from the
/// engine value it kept rather than compounding.</para>
/// <para><b>Lock.</b> While locomoting, a foot within <see cref="PlantTol"/> of the stance height is planted:
/// its world XY is held from the frame it lands until the animation lifts it (or it is dragged
/// <see cref="MaxSlip"/> away), then eased back to the animated foot over <see cref="ReleaseTime"/>; the
/// offset is a spring both ways (<see cref="Foot"/>).</para>
/// </summary>
internal static unsafe class Stride
{
    /// <summary>Model units the stance foot must sit below the other before its speed is sampled: in double
    /// support neither foot is the one carrying the stride.</summary>
    private const float StanceGap = 0.03f;
    /// <summary>Seconds of stance samples the measured stride speed averages over.</summary>
    private const float StanceTime = 0.5f;
    /// <summary>Stride speeds below this (model units per unit rate per second) are no measurement: a shuffle.</summary>
    private const float MinStance = 0.2f;
    /// <summary>The correction's range: past it the cycle is not a walk at all and is left to the engine.</summary>
    private const float MinFactor = 0.6f, MaxFactor = 1.6f;
    /// <summary>Corrections inside this are no correction: the engine's pacing is already right.</summary>
    private const float Deadband = 0.04f;
    /// <summary>Seconds the applied factor takes to follow the measurement.</summary>
    private const float FactorTime = 0.3f;
    /// <summary>Model units above the stance height a foot may be and still count as planted.</summary>
    private const float PlantTol = 0.03f;
    /// <summary>Seconds the stance height takes to rise back after a lower foot set it.</summary>
    private const float PlantRise = 1.5f;
    /// <summary>World units (times scale) a lock may drag the foot from its animated place before letting go.</summary>
    private const float MaxSlip = 0.2f;
    /// <summary>Seconds a held foot's offset takes to reach the held point: short, but a spring, so a landing
    /// foot decelerates into its plant.</summary>
    private const float HoldTime = 0.06f;
    /// <summary>Seconds a released lock takes to ease back onto the animated foot: most of a swing, so the
    /// catch-up happens while the foot is in the air and moving anyway.</summary>
    private const float ReleaseTime = 0.25f;
    /// <summary>Runs whose rate is tracked: the walk, its overlay, and the ones fading out.</summary>
    internal const int TrackedRuns = 4;

    internal static int Strided, Locked, Synced;

    /// <summary>Before the engine animates: every run gets the engine's rate times the gob's stride factor
    /// (1 when not locomoting or the knob is off, which puts the engine's own value back).</summary>
    internal static void Pre(ref PoseService.Slot s, IntPtr gob)
    {
        float f = Of(s);
        Scale(ref s, gob, f);
        if (f != 1f) Strided++;
    }

    /// <summary>The multiplier a body with slot <paramref name="s"/> plays its runs at this frame: last frame's
    /// eased factor while locomoting, else 1. Read by the body itself and by its attachments, which animate
    /// first in the same frame and so see the same value.</summary>
    internal static float Of(in PoseService.Slot s) => AnimKnobs.Stride.V && s.Loco ? 1f + s.Factor : 1f;

    /// <summary>Sets every run on <paramref name="gob"/> to the engine's own rate times <paramref name="f"/>,
    /// telling the engine's value from this module's last write (<paramref name="s"/> keeps both).</summary>
    internal static void Scale(ref PoseService.Slot s, IntPtr gob, float f)
    {
        s.RateF = f;
        var g = new Gob(gob);
        int n = Math.Min(g.RunCount, TrackedRuns);
        fixed (long* run = s.RateRun)
        fixed (float* eng = s.RateEngine, wrote = s.RateWritten)
        {
            long* keepRun = stackalloc long[TrackedRuns];
            float* keepEng = stackalloc float[TrackedRuns];
            float* keepWrote = stackalloc float[TrackedRuns];
            int kept = 0;
            for (int i = 0; i < n; i++)
            {
                AnimRun r = g.Run(i);
                if (r.IsNull) continue;
                float cur = r.Rate, baseRate = cur;
                for (int k = 0; k < TrackedRuns; k++)
                    if (run[k] == (long)r.Ptr) { if (cur == wrote[k]) baseRate = eng[k]; break; }
                float want = baseRate * f;
                if (want != cur) r.Rate = want;
                keepRun[kept] = (long)r.Ptr; keepEng[kept] = baseRate; keepWrote[kept] = want; kept++;
            }
            for (int k = 0; k < TrackedRuns; k++)
            {
                run[k] = k < kept ? keepRun[k] : 0;
                eng[k] = k < kept ? keepEng[k] : 0f;
                wrote[k] = k < kept ? keepWrote[k] : 0f;
            }
        }
    }

    /// <summary>Longest animation name compared when matching an attachment's run to the body's.</summary>
    private const int MaxName = 64;

    /// <summary>Before an attachment animates: each of its runs takes the rate of the body's run playing the
    /// same animation (the engine's value, times <paramref name="f"/>), and with <c>anim.syncattach</c> its time
    /// and, when both fade the same way, its blend. The attachment then advances exactly as the body is about
    /// to, so the two end every frame in phase whatever put them out of it. <paramref name="b"/> is the body's
    /// slot when <paramref name="tracked"/>, to tell the engine's rate from this module's last write.</summary>
    internal static void Sync(IntPtr gob, IntPtr body, ref PoseService.Slot b, bool tracked, float f)
    {
        var g = new Gob(gob);
        var bg = new Gob(body);
        int n = g.RunCount, bn = bg.RunCount;
        if (n == 0 || bn == 0) return;
        bool phase = AnimKnobs.SyncAttach.V;
        if (!phase && f == 1f) return;
        for (int i = 0; i < n; i++)
        {
            AnimRun r = g.Run(i);
            if (r.IsNull) continue;
            AnimRun br = Match(r, bg, bn, i);
            if (br.IsNull) continue;
            float rate = br.Rate;
            if (tracked)
                fixed (long* run = b.RateRun)
                fixed (float* eng = b.RateEngine, wrote = b.RateWritten)
                    for (int k = 0; k < TrackedRuns; k++)
                        if (run[k] == (long)br.Ptr) { if (rate == wrote[k]) rate = eng[k]; break; }
            r.Rate = rate * f;
            if (!phase) continue;
            r.Time = br.Time;
            if (((r.Flags ^ br.Flags) & FadingOut) == 0)
            {
                r.Blend = br.Blend;
                r.BlendTime = br.BlendTime;
            }
            Synced++;
        }
    }

    /// <summary>AnimRun flag bit: the run is fading out.</summary>
    private const uint FadingOut = 0x1;  // not-an-offset: a flag bit

    /// <summary>The body's run playing the animation <paramref name="run"/> plays, by name (the two gobs load
    /// their own <c>Animation</c> objects): the one at the same list position first, else the first by name.</summary>
    private static AnimRun Match(AnimRun run, Gob body, int bn, int at)
    {
        Animation anim = run.Animation;
        if (anim.IsNull) return default;
        byte* name = anim.Chars;
        AnimRun same = at < bn ? body.Run(at) : default;
        if (SameName(same, name)) return same;
        for (int j = 0; j < bn; j++)
        {
            AnimRun br = body.Run(j);
            if (j != at && SameName(br, name)) return br;
        }
        return default;
    }

    private static bool SameName(AnimRun run, byte* name)
    {
        if (run.IsNull) return false;
        Animation anim = run.Animation;
        if (anim.IsNull) return false;
        byte* other = anim.Chars;
        for (int i = 0; i < MaxName; i++)
        {
            if (name[i] != other[i]) return false;
            if (name[i] == 0) return true;
        }
        return true;
    }

    /// <summary>After the engine animates, before any correction: samples the stance foot's model-space speed
    /// (<paramref name="mr"/>/<paramref name="ml"/>, the ankles in model units) and eases the factor toward
    /// ground speed over it. <paramref name="groundSpeed"/> is in model units per second.</summary>
    internal static void Measure(ref PoseService.Slot s, Vector3 mr, Vector3 ml, float groundSpeed, bool fading, float dt)
    {
        bool sample = s.Loco && !fading && s.HasPrev && dt > 0f && MathF.Abs(mr.Z - ml.Z) > StanceGap;
        if (sample)
        {
            Vector3 d = mr.Z < ml.Z ? mr - s.PrevR : ml - s.PrevL;
            float v = MathF.Sqrt(d.X * d.X + d.Y * d.Y) / dt / s.RateF;
            s.Stance = s.Stance <= 0f ? v : s.Stance + (v - s.Stance) * MathF.Min(1f, dt / StanceTime);
        }
        s.PrevR = mr; s.PrevL = ml; s.HasPrev = true;
        float target = s.Loco ? Factor(groundSpeed, s.Stance) - 1f : 0f;
        Smooth(ref s.Factor, ref s.VFactor, target, dt, FactorTime);
    }

    /// <summary>The rate multiplier making a stance foot moving <paramref name="stance"/> per unit rate keep
    /// pace with the ground at <paramref name="ground"/>; 1 inside the deadband, out of range or unmeasured.</summary>
    internal static float Factor(float ground, float stance)
    {
        if (!(stance > MinStance) || !(ground > 0f)) return 1f;
        float f = ground / stance;
        return f < MinFactor || f > MaxFactor || MathF.Abs(f - 1f) < Deadband ? 1f : f;
    }

    /// <summary>The world XY each ankle is moved by this frame (Z is foot placement's).
    /// <paramref name="mrz"/>/<paramref name="mlz"/> are the ankles' model-space heights.</summary>
    internal static void Lock(ref PoseService.Slot s, Vector3 ankleR, Vector3 ankleL, float mrz, float mlz,
                              bool active, bool snap, float scale, float dt, out Vector3 dr, out Vector3 dl)
    {
        float low = MathF.Min(mrz, mlz);
        s.PlantZ = s.PlantZ == 0f || low < s.PlantZ ? low : s.PlantZ + (low - s.PlantZ) * MathF.Min(1f, dt / PlantRise);
        active &= AnimKnobs.FootLock.V && !snap;
        dr = Foot(ref s.LockR, ref s.HeldR, ref s.SlipR, ref s.LockDR, ref s.LockVR, ankleR, active && mrz - s.PlantZ < PlantTol, snap, MaxSlip * scale, dt);
        dl = Foot(ref s.LockL, ref s.HeldL, ref s.SlipL, ref s.LockDL, ref s.LockVL, ankleL, active && mlz - s.PlantZ < PlantTol, snap, MaxSlip * scale, dt);
    }

    /// <summary>One foot's lock: the XY offset from the animated ankle to where it is drawn. The offset is a
    /// spring, never a switch: while held it chases the held point over <see cref="HoldTime"/> (a landing foot
    /// settles rather than stopping dead), and let go it returns to 0 over <see cref="ReleaseTime"/>. A lock
    /// dragged past <paramref name="slip"/> lets go and stays let go (<paramref name="slipped"/>) until the foot
    /// lifts.</summary>
    internal static Vector3 Foot(ref Vector3 lockAt, ref bool held, ref bool slipped, ref Vector2 d, ref Vector2 vd,
                                 Vector3 ankle, bool planted, bool snap, float slip, float dt)
    {
        if (snap) { held = slipped = false; d = vd = Vector2.Zero; return Vector3.Zero; }
        if (!planted) held = slipped = false;
        else if (!held && !slipped)
        {
            // a fresh plant holds where the foot is drawn now, so a lock still easing out never pops
            lockAt = ankle + new Vector3(d.X, d.Y, 0f);
            held = true;
        }
        Vector2 target = held ? new Vector2(lockAt.X - ankle.X, lockAt.Y - ankle.Y) : Vector2.Zero;
        if (held && target.LengthSquared() > slip * slip) { held = false; slipped = true; target = Vector2.Zero; }
        if (held) Locked++;
        Smooth(ref d, ref vd, target, dt, held ? HoldTime : ReleaseTime);
        return new Vector3(d.X, d.Y, 0f);
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, v.Y, 0f);
}
