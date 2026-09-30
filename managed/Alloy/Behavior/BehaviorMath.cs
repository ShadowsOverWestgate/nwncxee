using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Engine;

namespace CX.Alloy;

/// <summary>
/// CX-107: the engine's own quaternion/Euler arithmetic, ported term for term from the free functions the
/// <c>CAurBehavior</c> solvers are built out of. Every formula below was read off <c>nwmain-linux</c> --
/// from the decompiled dump where it was unambiguous, and from <c>objdump</c> where the decompiler dropped
/// a lane or a float argument. Provenance and the addresses are in
/// <c>docs/research/behaviour-solvers.md</c> §4.
/// </summary>
/// <remarks>
/// <para>The engine's <c>Quaternion</c> is stored <b>(w, x, y, z)</b>, which is what
/// <see cref="CX.Engine.Quat"/> already is. Its product has the same operand order as
/// <c>System.Numerics.Quaternion</c>'s, and its vector rotation is the same matrix as
/// <c>Vector3.Transform</c>'s -- but the engine adds three exact-compare early-outs to the rotation and
/// groups its sums in a particular order, so the port keeps both: a solver that differed only in the last
/// bit would still drift a VFX offset over a few hundred frames.</para>
/// <para>Hot: every one of these runs inside a solver, so nothing here allocates and nothing here is
/// virtual.</para>
/// </remarks>
internal static partial class BehaviorMath
{
    /// <summary>The engine's own degree/radian constants (<c>DegToRad</c> @0x5b3100 and the literal
    /// <c>57.295776</c> the three Euler extractors multiply by -- not <c>180/pi</c> to the last bit, and the
    /// port keeps the engine's value).</summary>
    internal const float Deg2Rad = 0.017453292f;
    /// <inheritdoc cref="Deg2Rad"/>
    internal const float Rad2Deg = 57.295776f;

    internal static readonly Quat Identity = new() { W = 1f, X = 0f, Y = 0f, Z = 0f };

    /// <summary><c>operator*(Quaternion const&amp;, Quaternion const&amp;)</c> @0x5b5c30 -- the Hamilton
    /// product, in the engine's own term grouping.</summary>
    internal static Quat Mul(in Quat a, in Quat b) => new()
    {
        W = ((a.W * b.W - a.X * b.X) - a.Y * b.Y) - a.Z * b.Z,
        X = (a.W * b.X + a.X * b.W + b.Z * a.Y) - a.Z * b.Y,
        Y = (a.W * b.Y - b.Z * a.X) + a.Y * b.W + b.X * a.Z,
        Z = ((a.W * b.Z + a.X * b.Y) - a.Y * b.X) + a.Z * b.W,
    };

    /// <summary><c>operator*(Quaternion const&amp;, Vector const&amp;)</c> @0x5b5e40 -- rotate
    /// <paramref name="v"/> by <paramref name="q"/>. The three early-outs are the engine's, exact compares
    /// included: a <c>w</c> of exactly 1 is taken for the identity <i>whatever</i> x/y/z hold, which is a
    /// quirk the port reproduces rather than fixes (an engine-written quaternion that trips it would move
    /// differently under the two paths, and parity is the constraint).</summary>
    internal static Vector3 Rotate(in Quat q, in Vector3 v)
    {
        if (q.W == 1f) return v;
        if (v.X == 0f && v.Y == 0f && v.Z == 0f) return v;
        if (q.X == 0f && q.Y == 0f && q.Z == 0f) return v;
        float xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z;
        float xy = q.X * q.Y, xz = q.X * q.Z, yz = q.Y * q.Z;
        float wx = q.X * q.W, wy = q.Y * q.W, wz = q.W * q.Z;
        return new Vector3(
            (1f - (yy + zz) * 2f) * v.X + (xy - wz) * 2f * v.Y + (xz + wy) * 2f * v.Z,
            (wz + xy) * 2f * v.X + (1f - (zz + xx) * 2f) * v.Y + (yz - wx) * 2f * v.Z,
            (xz - wy) * 2f * v.X + (wx + yz) * 2f * v.Y + (1f - (xx + yy) * 2f) * v.Z);
    }

    /// <summary><c>YawPitchRoll(yaw, pitch, roll)</c> @0x5b6690 -- degrees, composed
    /// <c>qZ(yaw) * qX(pitch) * qY(roll)</c>. The three axes live in <c>.bss</c> and a static initialiser
    /// sets them to Z, X and Y respectively (docs/research/behaviour-solvers.md §4); a term whose angle is
    /// exactly 0 is skipped, so the port skips it too.</summary>
    internal static Quat YawPitchRoll(float yaw, float pitch, float roll)
    {
        Quat q = Identity;
        if (yaw != 0f)
        {
            (float s, float c) = MathF.SinCos(yaw * Deg2Rad * 0.5f);
            q = new Quat { W = c, X = 0f, Y = 0f, Z = s };
        }
        if (pitch != 0f)
        {
            (float s, float c) = MathF.SinCos(pitch * Deg2Rad * 0.5f);
            q = Mul(q, new Quat { W = c, X = s, Y = 0f, Z = 0f });
        }
        if (roll != 0f)
        {
            (float s, float c) = MathF.SinCos(roll * Deg2Rad * 0.5f);
            q = Mul(q, new Quat { W = c, X = 0f, Y = s, Z = 0f });
        }
        return q;
    }

    /// <summary><c>dot(Quaternion const&amp;, Quaternion const&amp;)</c> @0x5b6280.</summary>
    internal static float Dot(in Quat a, in Quat b) => a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary><c>slerp(Quaternion, Quaternion const&amp;, float)</c> @0x5b6500 -- the camera solve's
    /// orientation step. ONE transcription of it exists, <see cref="AnimKeys.Slerp"/> (CX-105 needs the same
    /// function for every rotation key), and this forwards to it so a future correction lands once. Its form
    /// is also the more faithful of the two: the engine's escape is <c>if (|dot| &lt; 1)</c>, so a NaN dot
    /// falls OUT of the interpolation, which <c>!(ad &lt; 1f)</c> reproduces and <c>|d| &gt;= 1f</c> does
    /// not.</summary>
    internal static Quat Slerp(in Quat a, in Quat b, float t) => AnimKeys.Slerp(a, b, t);

    /// <summary><c>normalize(Quaternion)</c> @0x5b6440 -- unit length is left alone, a degenerate length
    /// becomes the identity (the engine's own answer, not a NaN).</summary>
    internal static Quat Normalize(in Quat q)
    {
        float len2 = q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z;
        if (len2 == 1f) return q;
        if (len2 <= 0f) return Identity;
        float k = MathF.Sqrt(1f / len2);
        return new Quat { W = q.W * k, X = q.X * k, Y = q.Y * k, Z = q.Z * k };
    }

    /// <summary><c>Yaw(Quaternion const&amp;)</c> @0x5b68b0 -- degrees about Z, from the rotation matrix's
    /// <c>atan2(-R01, R11)</c>. Zero when both terms are zero (the engine's guard against
    /// <c>atan2(0, 0)</c>).</summary>
    internal static float Yaw(in Quat q)
    {
        float a = (q.Y * q.X - q.Z * q.W) * 2f;
        float m = 1f - (q.X * q.X + q.Z * q.Z) * 2f;
        return a == 0f && m == 0f ? 0f : MathF.Atan2(-a, m) * Rad2Deg;
    }

    /// <summary><c>Pitch(Quaternion const&amp;)</c> @0x5b69b0 -- degrees about X, measured against the
    /// length of the yaw pair so it stays well behaved at the poles.</summary>
    internal static float Pitch(in Quat q)
    {
        float up = (q.Y * q.Z + q.W * q.X) * 2f;
        float a = (q.Y * q.X - q.Z * q.W) * 2f;
        float m = 1f - (q.X * q.X + q.Z * q.Z) * 2f;
        return MathF.Atan2(up, MathF.Sqrt(a * a + m * m)) * Rad2Deg;
    }

    /// <summary><c>Roll(Quaternion)</c> @0x5b6ac0 -- degrees about Y, and the only one of the three that is
    /// not a closed form: the engine removes the yaw, then the pitch, then reads what is left. Its two
    /// unrotations use a NORMALISED axis and the negated angle, exactly as transcribed here.</summary>
    internal static float Roll(in Quat q)
    {
        Quat q1 = Mul(AxisAngle(0f, 0f, 1f, -Yaw(q)), q);
        Quat q2 = Mul(AxisAngle(1f, 0f, 0f, -Pitch(q1)), q1);
        float a = (q2.Z * q2.X - q2.Y * q2.W) * 2f;
        float m = 1f - (q2.Y * q2.Y + q2.Z * q2.Z) * 2f;
        return MathF.Atan2(-a, m) * Rad2Deg;
    }

    /// <summary>The half-angle quaternion <c>Roll</c> builds by hand: <c>Vector::Normalize</c> on the axis,
    /// then <c>axis * sin(deg*pi/180*0.5)</c> with <c>cos</c> as the scalar.</summary>
    private static Quat AxisAngle(float ax, float ay, float az, float degrees)
    {
        float len = MathF.Sqrt(ax * ax + ay * ay + az * az);
        if (len > 0f) { ax /= len; ay /= len; az /= len; }
        (float s, float c) = MathF.SinCos(degrees * Deg2Rad * 0.5f);
        return new Quat { W = c, X = ax * s, Y = ay * s, Z = az * s };
    }

    /// <summary>The camera solvers' own expansion of <c>q * +Z</c> -- the unit the camera sits along from
    /// its focus. Inlined by the compiler in <c>CAurBehaviorCameraFollow::Control</c>
    /// (<c>objdump</c> 0x4a25be) and by <see cref="CX.Services.Camera.CameraService"/>'s <c>Forward</c>,
    /// which this matches term for term.</summary>
    internal static Vector3 Forward(in Quat q) => new(
        (q.X * q.Z + q.W * q.Y) * 2f,
        (q.Y * q.Z - q.W * q.X) * 2f,
        1f - (q.X * q.X + q.Y * q.Y) * 2f);

    /// <summary>The clamp every camera-follow axis uses: <c>max(min(v, hi), lo)</c>, which -- deliberately --
    /// answers <paramref name="lo"/> when the two bounds are crossed. Verified branch by branch against
    /// the three copies in <c>Control</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Clamp(float v, float lo, float hi) => MathF.Max(MathF.Min(v, hi), lo);

    /// <summary>The engine's round-half-up, written as <c>(int)(v + 1000.0f + 0.5f) - 1000</c> so that a
    /// small negative rounds the way the subwindow solver expects.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Round(float v) => (int)(v + 1000f + 0.5f) - 1000;

    /// <summary><c>Yaw(Vector const&amp;)</c> @0x5b6cc0 -- the bearing of a direction in degrees, and the
    /// engine's whole convention in one line: <c>+Y</c> is yaw 0, <c>+X</c> is -90.
    /// <see cref="CX.Services.Camera.CameraService"/>'s <c>FacingYaw</c> is the same formula and its
    /// self-check already pins it.</summary>
    internal static float YawOf(in Vector3 v) =>
        v.Y == 0f && v.X == 0f ? 0f : MathF.Atan2(-v.X, v.Y) * Rad2Deg;

    /// <summary><c>cross(Vector const&amp;, Vector const&amp;)</c> @0x5b3aa0, for the navigate solver's
    /// strafe axis.</summary>
    internal static Vector3 Cross(in Vector3 a, in Vector3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);
}
