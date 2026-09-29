using System;
using System.Numerics;
using CX.Engine;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Movement changes blended, a <see cref="PoseService"/> module (<c>anim.blend</c>). The engine crossfades only a
/// change of animation NAME, over its authored transtime; a change that keeps the name -- walk to walk-back, which is
/// the walk cycle at negative speed, or a diagonal swapped for the straight cycle -- flips the pace in one frame, and
/// a short transtime reads as a snap.
/// <para>Dead blending: when the creature's anim id changes between two movement anims (<see cref="Movement"/>), each
/// tracked bone's last pose keeps moving with its own angular velocity, decaying over <see cref="SpinHalfLife"/>, and
/// is crossfaded into what the engine now poses over <see cref="BlendTime"/> (smoothstep). The first blended frame is
/// last frame's pose carried one step, so nothing jumps; a reversal slows through zero instead of flipping. It is the
/// ANIMATION layer: the other modules' corrections go on top every frame and are never carried in the blend.</para>
/// </summary>
internal static unsafe class Transition
{
    /// <summary>Seconds the old movement takes to hand over to the new one.</summary>
    private const float BlendTime = 0.3f;
    /// <summary>Seconds over which the carried movement's velocity halves: it follows through, then settles.</summary>
    private const float SpinHalfLife = 0.06f;
    /// <summary>Radians per second a bone's measured velocity is clamped to: a restarted cycle's jump is not a swing.</summary>
    private const float MaxSpin = 15f;
    internal static int Blends;

    /// <summary>Anim ids a change between is blended: standing, combat-ready, the turns on the spot and every walk and run.</summary>
    internal static bool Movement(int anim) => anim is 0 or 1 or >= 0x50 and <= 0x53 || PoseService.IsLocomotion(anim);

    /// <summary>Starts a blend when a <paramref name="tracked"/> body's played anim id changed between two movements -- a
    /// diagonal swapped for the straight cycle keeps playing the same cycle and is no change -- and advances
    /// one that runs; true while it does. A blend seeds from the last output and velocity <see cref="Pose"/> recorded.</summary>
    internal static bool Begin(ref PoseService.Slot s, int anim, bool tracked, float dt)
    {
        int played = Diagonal.Played(anim, s.Swapped);
        bool change = played != s.LastAnim && Movement(played) && Movement(s.LastAnim);
        s.LastAnim = played;
        if (!AnimKnobs.Blend.V || !tracked) return s.Blending = false;
        if (change)
        {
            s.Blending = true;
            s.BlendT = 0f;
            Blends++;
            fixed (float* o = s.Out, sp = s.Spin, src = s.Src, ss = s.SrcSpin)
            {
                new Span<float>(o, PoseService.Bones.Count * 4).CopyTo(new Span<float>(src, PoseService.Bones.Count * 4));
                new Span<float>(sp, PoseService.Bones.Count * 3).CopyTo(new Span<float>(ss, PoseService.Bones.Count * 3));
            }
        }
        if (s.Blending && (s.BlendT += dt) >= BlendTime) s.Blending = false;
        return s.Blending;
    }

    /// <summary>The animation layer's pose for this frame: while <paramref name="blending"/>, each bone carried on from the
    /// old movement and crossfaded into the engine's; always, the output and its velocity recorded for the next change.
    /// The caller has saved the engine's values before a blend writes.</summary>
    internal static void Pose(ref PoseService.Slot s, in PoseService.Bones b, bool blending, bool tracked, float dt)
    {
        float w = Ease01(s.BlendT / BlendTime), decay = MathF.Exp(-dt * MathF.Log(2f) / SpinHalfLife);
        fixed (float* o = s.Out, sp = s.Spin, src = s.Src, ss = s.SrcSpin)
        fixed (long* parts = s.OutParts)
            for (int i = 0; i < PoseService.Bones.Count; i++)
            {
                IntPtr p = b[i];
                bool same = tracked && p != IntPtr.Zero && parts[i] == (long)p;
                parts[i] = (long)p;
                if (p == IntPtr.Zero) continue;
                Quaternion e = Local(p), q = e;
                if (blending && same)
                {
                    Vector3 v = *(Vector3*)(ss + 3 * i) * decay;
                    Quaternion from = Quaternion.Normalize(Exp(v * dt) * *(Quat*)(src + 4 * i));
                    *(Vector3*)(ss + 3 * i) = v;
                    *(Quat*)(src + 4 * i) = from;
                    q = Quaternion.Slerp(from, e, w);
                    SetLocal(p, q);
                }
                *(Vector3*)(sp + 3 * i) = same && dt > 0f ? Clamp(Log(q * Quaternion.Conjugate(*(Quat*)(o + 4 * i))) / dt) : Vector3.Zero;
                *(Quat*)(o + 4 * i) = q;
            }
    }

    /// <summary>Smoothstep over [0, 1]: the hand-over starts and ends at rest.</summary>
    private static float Ease01(float x) => x <= 0f ? 0f : x >= 1f ? 1f : x * x * (3f - 2f * x);

    private static Vector3 Clamp(Vector3 v)
    {
        float l = v.Length();
        return l > MaxSpin ? v * (MaxSpin / l) : v;
    }

    /// <summary>A knee swinging forward at 4 rad/s reverses (walk to walk-back): the blend starts where the swing was,
    /// carries it on, turns round with no frame's step past twice the swing's own (a snap is the whole gap at once),
    /// and lands on the new motion.</summary>
    internal static bool SelfCheck()
    {
        float dt = 1f / 60f, spin = 4f, prev = 0f, maxStep = 0f;
        Quaternion from = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f);
        Vector3 v = Vector3.UnitX * spin;
        float t = 0f, angle = 0f, target = 0.5f;
        bool first = true, landed = false, carried = false;
        for (int i = 0; i < 40; i++)
        {
            t += dt;
            target -= spin * dt;
            v *= MathF.Exp(-dt * MathF.Log(2f) / SpinHalfLife);
            from = Quaternion.Normalize(Exp(v * dt) * from);
            Quaternion q = Quaternion.Slerp(from, Quaternion.CreateFromAxisAngle(Vector3.UnitX, target), Ease01(t / BlendTime));
            angle = 2f * MathF.Atan2(q.X, q.W);
            if (first) { carried = angle > 0.5f; first = false; }
            else maxStep = MathF.Max(maxStep, MathF.Abs(angle - prev));
            prev = angle;
            if (t >= BlendTime) landed = MathF.Abs(angle - target) < 1e-3f;
        }
        return carried && maxStep <= 2f * spin * dt && landed && Movement(2) && Movement(3) && Movement(0) && !Movement(0x2f);
    }
}
