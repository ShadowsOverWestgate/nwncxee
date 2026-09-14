using System.Globalization;
using Sowx.Framework;

namespace Sowx.Services.Camera;

/// <summary>Per-engine-camera-mode feel: the three things <see cref="CameraService"/> changes about the engine's
/// answer. One block per mode byte (0 chase, 1 top-down, 2 stiff chase), so top-down can pitch toward the map
/// while chase keeps a lower angle.</summary>
public sealed class CameraMode
{
    /// <summary>Vertical FOV in degrees written to Camera+0x4c4 every frame; 0 = leave the engine's 45.</summary>
    public float Fov;
    /// <summary>Position lag: seconds the focus takes to settle (critically damped). 0 = welded, the engine's own.</summary>
    public float LagPos;
    /// <summary>Rotation lag: seconds yaw/pitch take to settle. 0 = welded.</summary>
    public float LagRot;
    /// <summary>Pitch curve: degrees of pitch added per unit of distance past <see cref="PitchRef"/>; negative
    /// lowers the angle when zoomed out. 0 = the engine's pitch as the player left it.</summary>
    public float PitchCurve;
    /// <summary>The distance at which the pitch curve adds nothing.</summary>
    public float PitchRef = 10f;

    public const float FovMin = 10f, FovMax = 120f;
    public const float LagMax = 2f;
    public const float CurveMax = 10f;
    public const float RefMin = 1f, RefMax = 60f;
}

/// <summary>Persisted camera-layer preferences, flat <c>mode.key=value</c> in <c>sowx-camera.cfg</c> beside
/// <c>nwn.ini</c> (written by <c>/camera</c>, never hand-required). Every numeric key is clamped on parse.</summary>
public sealed class CameraConfig
{
    public static readonly string[] ModeNames = { "chase", "topdown", "stiff" };

    public readonly CameraMode[] Modes = { new(), new(), new() };

    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        for (int m = 0; m < 3; m++)
        {
            CameraMode c = Modes[m]; string n = ModeNames[m];
            sb.Append($"{n}.fov={F(c.Fov)}\n{n}.lagpos={F(c.LagPos)}\n{n}.lagrot={F(c.LagRot)}\n")
              .Append($"{n}.pitchcurve={F(c.PitchCurve)}\n{n}.pitchref={F(c.PitchRef)}\n");
        }
        return sb.ToString();
    }

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    public static bool TryF(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    public static CameraConfig Parse(string text)
    {
        var cfg = new CameraConfig();
        ConfigFile.ForEachPair(text, (key, val) =>
        {
            int dot = key.IndexOf('.');
            if (dot < 0 || !TryF(val, out float v)) return;
            int m = System.Array.IndexOf(ModeNames, key[..dot]);
            if (m < 0) return;
            cfg.Modes[m].Set(key[(dot + 1)..], v);
        });
        return cfg;
    }

    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"CameraConfig.SelfCheck FAIL: {w}"); } }
        var c = Parse("topdown.fov=60\nchase.lagpos=0.3\nstiff.pitchcurve=99\nchase.pitchref=abc\nnope.fov=1\n");
        E(c.Modes[1].Fov == 60f && c.Modes[0].Fov == 0f, "a mode key lands on its mode only");
        E(c.Modes[0].LagPos == 0.3f, "lag parses");
        E(c.Modes[2].PitchCurve == CameraMode.CurveMax, "out-of-range is clamped");
        E(c.Modes[0].PitchRef == 10f, "a bad value keeps the default");
        E(Parse(c.ToText()).ToText() == c.ToText(), "the text round-trips");
        log.Info(ok ? "CameraConfig.SelfCheck: passed" : "CameraConfig.SelfCheck: FAILED");
    }
}

public static class CameraModeExt
{
    /// <summary>Apply one <c>/camera</c> knob by name; false when the name is not a knob.</summary>
    public static bool Set(this CameraMode c, string key, float v)
    {
        switch (key)
        {
            case "fov": c.Fov = v == 0 ? 0 : System.Math.Clamp(v, CameraMode.FovMin, CameraMode.FovMax); return true;
            case "lagpos": c.LagPos = System.Math.Clamp(v, 0f, CameraMode.LagMax); return true;
            case "lagrot": c.LagRot = System.Math.Clamp(v, 0f, CameraMode.LagMax); return true;
            case "pitchcurve": c.PitchCurve = System.Math.Clamp(v, -CameraMode.CurveMax, CameraMode.CurveMax); return true;
            case "pitchref": c.PitchRef = System.Math.Clamp(v, CameraMode.RefMin, CameraMode.RefMax); return true;
        }
        return false;
    }
}
