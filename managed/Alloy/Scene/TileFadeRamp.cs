using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The engine's second-story fade, as arithmetic: the occlusion plane through the followed character,
/// <c>TwoDBoxAbovePlane</c>'s four-corner test, the debounced 4/s ramp, and the game option's gate. The tile
/// stream (<see cref="Db.TileBuild"/>) runs it over the database's own tile boxes. Pure and allocation-free.</summary>
internal static class TileFadeRamp
{
    /// <summary>Factors per second, from <c>DeltaT * 4.0</c> in all three ramps (the two fade buckets and the
    /// fog-of-war step in the BSP visitor).</summary>
    private const float Rate = 4f;

    /// <summary>Seconds of consistent intent before the fade starts moving: the +/-0.1 band
    /// <c>TileData+0x78</c> has to leave. Stops a tile on the plane boundary flickering.</summary>
    private const float Hysteresis = 0.1f;

    /// <summary>Below this horizontal component of the unit camera-to-target direction the engine stands the
    /// whole fade down (<c>SQRT(__x) &lt; 0.4</c> @0x47ffd6): a top-down camera has no "between".</summary>
    private const float MinHorizontal = 0.4f;

    /// <summary><c>Scene::geomFadeEnabled</c>'s three values: the plane test, no fade, and every roof out.</summary>
    private const int ModeAuto = 1, ModeNever = 2, ModeAlways = 4;

    /// <summary>The plane maths on its own, so the SelfCheck can pin it to numbers.</summary>
    internal static bool Plane(Vector3 target, Vector3 camPos, out float nx, out float ny, out float d)
    {
        nx = 0f; ny = 0f; d = 0f;
        Vector3 delta = target - camPos;
        float len = delta.Length();
        if (!(len > 1e-4f)) return false;
        Vector3 u = delta / len;
        float flat = MathF.Sqrt(u.X * u.X + u.Y * u.Y);
        if (flat < MinHorizontal) return false;
        float inv = 1f / flat;
        nx = u.X * inv; ny = u.Y * inv;
        d = -(target.X * nx + target.Y * ny);
        return true;
    }

    /// <summary><c>TwoDBoxAbovePlane</c> @0x47a0b0: all FOUR corners of the XY rectangle strictly on the
    /// plane's positive side. Four and not two -- a box straddling the plane diagonally passes a
    /// min/max-only test.</summary>
    internal static bool TwoDBoxAbovePlane(float minX, float minY, float maxX, float maxY,
                                           float nx, float ny, float d)
    {
        float a = minX * nx, b = minY * ny, c = maxX * nx, e = maxY * ny;
        return a + b + d > 0f && c + e + d > 0f && e + a + d > 0f && c + b + d > 0f;
    }

    /// <summary>One tile's fade step: <c>HandleFadingOutBucket</c> @0x47f870 and
    /// <c>HandleFadingInBucket</c> @0x47f670, term for term. The counter moves only while it is inside the
    /// band, so it is bounded at +/-(0.1 + dt), and switching intent restarts it from 0 rather than from the
    /// other end -- which is what makes the debounce symmetric.</summary>
    internal static void Step(ref float fade, ref float acc, bool fadingOut, float dt)
    {
        if (fadingOut)
        {
            if (acc <= Hysteresis) acc = MathF.Max(acc, 0f) + dt;
            if (acc > Hysteresis) fade = MathF.Max(0f, fade - dt * Rate);
        }
        else
        {
            if (acc >= -Hysteresis) acc = MathF.Min(acc, 0f) - dt;
            if (acc < -Hysteresis) fade = MathF.Min(1f, fade + dt * Rate);
        }
    }

    /// <summary><c>ProcessTriMeshParts</c> @0x480d40's gate: does a <c>tilefade 1</c> part take its tile's factor
    /// this frame? Never under "never"; always under "always"; under "automatic" only while the occlusion plane
    /// stands (a follow target, and a camera not looking straight down) -- otherwise the roof draws opaque.
    /// </summary>
    internal static bool FadeLive(int mode, bool planeOk) => (mode == ModeAuto && planeOk) || mode == ModeAlways;

    /// <summary>Off-device: the plane, the four-corner box test and the debounced ramp, pinned to numbers
    /// derived from the decompilation rather than from this implementation.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TileFadeRamp.SelfCheck");

        // The plane: camera at the origin, player 10 units along +X and 5 up. The unit direction's horizontal
        // component is 10/sqrt(125) = 0.894 -- well past 0.4 -- so the plane is x = 10 facing +X.
        bool ok = Plane(new Vector3(10f, 0f, 5f), Vector3.Zero, out float nx, out float ny, out float d);
        check.Expect(ok && MathF.Abs(nx - 1f) < 1e-5f && MathF.Abs(ny) < 1e-5f && MathF.Abs(d + 10f) < 1e-3f,
                     "the plane is the flattened camera-to-target direction, through the target");
        check.Expect(!Plane(new Vector3(0f, 0f, -10f), Vector3.Zero, out _, out _, out _),
                     "a straight-down camera stands the fade down");
        // 0.4 is the threshold on the NORMALIZED direction: 4 out along x, sqrt(84) down, so the unit
        // vector's |xy| is 4/10 = 0.4 exactly, which the engine's strict `< 0.4` keeps.
        check.Expect(Plane(new Vector3(4f, 0f, -MathF.Sqrt(84f)), Vector3.Zero, out _, out _, out _),
                     "the 0.4 horizontal threshold is on the unit direction and is not exclusive at 0.4");
        check.Expect(!Plane(new Vector3(3.5f, 0f, -MathF.Sqrt(100f - 3.5f * 3.5f)), Vector3.Zero, out _, out _, out _),
                     "...and under it the fade is down");

        // The four-corner test, against the x = 10 plane (n = +X, d = -10).
        check.Expect(TwoDBoxAbovePlane(11f, -1f, 12f, 1f, 1f, 0f, -10f), "a box entirely beyond the player is above");
        check.Expect(!TwoDBoxAbovePlane(8f, -1f, 9f, 1f, 1f, 0f, -10f), "a box entirely in front of the player is not");
        check.Expect(!TwoDBoxAbovePlane(9f, -1f, 11f, 1f, 1f, 0f, -10f), "a straddling box is not");
        // The corner a min/max-only test gets wrong: normal along the diagonal, and the box corner that fails
        // is (min.x, min.y) rather than either extreme of the box along one axis.
        float s = 1f / MathF.Sqrt(2f);
        check.Expect(!TwoDBoxAbovePlane(0f, 10f, 10f, 20f, s, s, -12f),
                     "the corner a two-corner test would pass is tested");

        // The ramp. dt = 0.05 s: the first two steps are inside the 0.1 s band and must not move the factor.
        float fade = 1f, acc = 0f;
        Step(ref fade, ref acc, true, 0.05f);
        check.Expect(fade == 1f && MathF.Abs(acc - 0.05f) < 1e-6f, "the first step only charges the debounce");
        Step(ref fade, ref acc, true, 0.05f);
        check.Expect(fade == 1f && MathF.Abs(acc - 0.1f) < 1e-6f, "...and so does the step that reaches 0.1");
        Step(ref fade, ref acc, true, 0.05f);
        check.Expect(MathF.Abs(acc - 0.15f) < 1e-6f && MathF.Abs(fade - 0.8f) < 1e-5f,
                     "past the band the factor moves at 4.0/s");
        for (int i = 0; i < 8; i++) Step(ref fade, ref acc, true, 0.05f);
        check.Expect(fade == 0f, "the fade clamps at 0");
        // Turning round: the counter restarts from 0, so the ramp back needs its own 0.1 s.
        Step(ref fade, ref acc, false, 0.05f);
        check.Expect(fade == 0f && MathF.Abs(acc + 0.05f) < 1e-6f, "reversing restarts the debounce from 0");
        Step(ref fade, ref acc, false, 0.05f);
        Step(ref fade, ref acc, false, 0.05f);
        check.Expect(MathF.Abs(fade - 0.2f) < 1e-5f, "...and then it ramps back in at the same 4.0/s");
        for (int i = 0; i < 8; i++) Step(ref fade, ref acc, false, 0.05f);
        check.Expect(fade == 1f, "the fade clamps at 1");

        check.Expect(!FadeLive(ModeNever, true) && FadeLive(ModeAlways, false) && FadeLive(ModeAuto, true)
                     && !FadeLive(ModeAuto, false), "the fade gate follows the second-story option");

        log.Info(check.Ok ? "TileFadeRamp.SelfCheck: passed" : "TileFadeRamp.SelfCheck: FAILED");
        return check.Ok;
    }
}
