using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

internal static partial class BehaviorMath
{
    private static bool Near(float a, float b, float eps = 2e-4f) => MathF.Abs(a - b) < eps;

    private static bool Near(in Quat a, in Quat b, float eps = 2e-4f) =>
        Near(a.W, b.W, eps) && Near(a.X, b.X, eps) && Near(a.Y, b.Y, eps) && Near(a.Z, b.Z, eps);

    private static bool Near(in Vector3 a, in Vector3 b, float eps = 2e-4f) => (a - b).Length() < eps;

    /// <summary>
    /// The ported engine arithmetic against numbers taken from the decompilation, off-device. What this is
    /// for: a wrong axis, a swapped Euler slot, a reversed product order or a dropped early-out are all
    /// silent in a build and only show up as a VFX in the wrong hand or a camera on the wrong side.
    /// </summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        int n = 0;
        void Expect(bool cond, string what) { n++; check.Expect(cond, what); }

        // ---- layout and product order ----
        Quat qi = Identity;
        Expect(qi.W == 1f && qi.X == 0f && qi.Y == 0f && qi.Z == 0f, "the identity is (w=1, 0, 0, 0)");
        var a = new Quat { W = 0.3f, X = 0.5f, Y = -0.2f, Z = 0.7f };
        var b = new Quat { W = -0.4f, X = 0.1f, Y = 0.6f, Z = 0.2f };
        Quaternion net = (Quaternion)a * (Quaternion)b;    // System.Numerics agrees term for term
        Expect(Near(Mul(a, b), (Quat)net), "the quaternion product matches System.Numerics in the same operand order");
        Expect(!Near(Mul(a, b), Mul(b, a)), "the product is not commutative (so the order under test is meaningful)");

        // ---- YawPitchRoll: which slot is which axis ----
        Quat yaw90 = YawPitchRoll(90f, 0f, 0f);
        float h = MathF.Sqrt(0.5f);
        Expect(Near(yaw90, new Quat { W = h, X = 0f, Y = 0f, Z = h }), "yaw turns about Z");
        Expect(Near(YawPitchRoll(0f, 90f, 0f), new Quat { W = h, X = h, Y = 0f, Z = 0f }), "pitch turns about X");
        Expect(Near(YawPitchRoll(0f, 0f, 90f), new Quat { W = h, X = 0f, Y = h, Z = 0f }), "roll turns about Y");
        Expect(Near(YawPitchRoll(0f, 0f, 0f), Identity), "all three zero is the identity, not a product of three identities");

        // ---- the rotation, and the engine's three early-outs ----
        var v = new Vector3(1f, 2f, 3f);
        Quat na = Normalize(a), nb = Normalize(b);
        Expect(Near(Rotate(yaw90, new Vector3(1f, 0f, 0f)), new Vector3(0f, 1f, 0f)),
                     "+90 yaw takes +X to +Y");
        Expect(Near(Rotate(YawPitchRoll(0f, 90f, 0f), new Vector3(0f, 0f, 1f)), new Vector3(0f, -1f, 0f)),
                     "+90 pitch takes +Z to -Y");
        // Unit quaternion on purpose: .NET's Transform is a two-cross-product form, which only agrees with the
        // engine's matrix (and with itself) for unit input. Every engine caller passes one.
        Expect(Near(Rotate(na, v), Vector3.Transform(v, (Quaternion)na)),
                     "the rotation is the same matrix as Vector3.Transform");
        Expect(Near(Rotate(na, v).Length(), v.Length(), 1e-3f), "a unit rotation preserves length");
        Expect(Rotate(new Quat { W = 1f, X = 9f, Y = 9f, Z = 9f }, v) == v,
                     "w == 1 exactly is taken for the identity whatever x/y/z hold (the engine's own quirk)");
        Expect(Rotate(yaw90, Vector3.Zero) == Vector3.Zero, "the zero vector short-circuits");
        Expect(Rotate(new Quat { W = 0.5f, X = 0f, Y = 0f, Z = 0f }, v) == v, "a zero axis short-circuits");

        // ---- the Euler extractors, and that they invert YawPitchRoll ----
        Expect(Near(Yaw(YawPitchRoll(37f, 0f, 0f)), 37f), "Yaw recovers the yaw");
        Expect(Near(Pitch(YawPitchRoll(37f, 21f, 0f)), 21f), "Pitch recovers the pitch under a yaw");
        Expect(Near(Yaw(YawPitchRoll(37f, 21f, 0f)), 37f), "Yaw survives a pitch");
        Expect(Near(Roll(YawPitchRoll(0f, 0f, 24f)), 24f), "Roll recovers the roll");
        Expect(Near(Roll(YawPitchRoll(37f, 0f, 24f)), 24f), "Roll survives a yaw");
        Expect(Yaw(Identity) == 0f && Pitch(Identity) == 0f, "the identity is yaw 0, pitch 0");
        Expect(Near(YawOf(new Vector3(0f, 1f, 0f)), 0f) && Near(YawOf(new Vector3(1f, 0f, 0f)), -90f)
                     && Near(YawOf(new Vector3(-1f, 0f, 0f)), 90f) && YawOf(Vector3.Zero) == 0f,
                     "Yaw(Vector) is +Y = 0, +X = -90, +Z-only = 0");

        // ---- slerp / normalize ----
        Expect(Near(Slerp(na, na, 0.5f), na), "slerping a unit quaternion to itself is itself (|dot| >= 1)");
        // dot(na, nb) is negative here, so the engine negates the START to take the short way round: t = 0
        // answers -na and t = 1 answers nb, both of which name the same rotation.
        Quat flip = new() { W = -na.W, X = -na.X, Y = -na.Y, Z = -na.Z };
        Expect(Dot(na, nb) < 0f, "the fixture pair is obtuse, which is what exercises the negation");
        Expect(Near(Slerp(na, nb, 0f), flip, 1e-3f), "t = 0 is the start, negated to take the short way round");
        Expect(Near(Slerp(na, nb, 1f), nb, 1e-3f), "t = 1 is the end");
        Expect(Near(Length(Normalize(new Quat { W = 2f, X = 0f, Y = 0f, Z = 0f })), 1f), "normalize scales to unit length");
        Expect(Near(Length(na), 1f), "the fixture normalizes");
        Expect(Near(Normalize(default), Identity), "a degenerate quaternion normalizes to the identity, not NaN");
        Expect(Near(Length(Normalize(Slerp(na, nb, 0.37f))), 1f), "the camera solve's normalize(slerp(..)) is a unit quaternion");

        // ---- the clamp and the rounding the solvers rely on ----
        Expect(Clamp(5f, 0f, 10f) == 5f && Clamp(-1f, 0f, 10f) == 0f && Clamp(11f, 0f, 10f) == 10f,
                     "the clamp clamps");
        Expect(Clamp(5f, 10f, 0f) == 10f && Clamp(-5f, 10f, 0f) == 10f,
                     "a crossed range answers the minimum on both sides (the engine's max(min(v,hi),lo))");
        Expect(Round(0.5f) == 1 && Round(0.49f) == 0 && Round(2.5f) == 3 && Round(-0.5f) == 0 && Round(-1.5f) == -1,
                     "the engine's round is half-up, negatives included");

        // ---- the camera rig vector ----
        Expect(Near(Forward(YawPitchRoll(0f, 0f, 0f)), new Vector3(0f, 0f, 1f)),
                     "pitch 0 puts the camera straight above the focus");
        Expect(Near(Forward(YawPitchRoll(0f, 90f, 0f)), new Vector3(0f, -1f, 0f)),
                     "yaw 0 pitch 90 puts it due south looking north");
        Expect(Near(Forward(YawPitchRoll(90f, 90f, 0f)), new Vector3(1f, 0f, 0f)),
                     "yaw 90 swings it to +X");
        Expect(Near(Forward(YawPitchRoll(30f, 50f, 0f)), Rotate(YawPitchRoll(30f, 50f, 0f), new Vector3(0f, 0f, 1f))),
                     "Forward is exactly q * +Z");

        log.Info(check.Ok ? $"BehaviorMath.SelfCheck: passed ({n} checks)" : "BehaviorMath.SelfCheck: FAILED");
    }

    private static float Length(in Quat q) => MathF.Sqrt(q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z);
}
