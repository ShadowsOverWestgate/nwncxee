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
    /// <summary>Zoom lag: seconds the distance takes to settle after a wheel notch (the engine's own zoom is a
    /// one-frame impulse, so each notch is a step). 0 = welded.</summary>
    public float LagZoom = 0.02f;
    /// <summary>Pitch curve: degrees of pitch added per unit of distance past <see cref="PitchRef"/>; negative
    /// lowers the angle when zoomed out. 0 = the engine's pitch as the player left it.</summary>
    public float PitchCurve;
    /// <summary>The distance at which the pitch curve adds nothing.</summary>
    public float PitchRef = 10f;
    /// <summary>#538: the camera rig's lateral offset from the focus, world units along the camera's right
    /// (positive = the player sits left of centre). 0 = the engine's own orbit.</summary>
    public float OffsetX;
    /// <summary>#538: vertical offset, world units up. 0 = the engine's own orbit.</summary>
    public float OffsetY;
    /// <summary>#540: camera-relative WASD (1) or the engine's facing-relative drive (0). Top-down defaults off.</summary>
    public float Drive = 1f;
    /// <summary>#541: the layer's auto-recenter (1) in place of the engine's chase, or the engine's own (0).
    /// Top-down defaults off (the engine leaves its yaw where the player put it, and so do we).</summary>
    public float Recenter = 1f;

    public const float FovMin = 10f, FovMax = 120f;
    public const float LagMax = 2f;
    public const float CurveMax = 10f;
    public const float RefMin = 1f, RefMax = 60f;
    public const float OffsetMax = 10f;
}

/// <summary>#538: the spring arm that keeps an offset camera out of walls; one set for every mode.</summary>
public sealed class SpringArm
{
    /// <summary>How far short of the hit the camera stops, world units.</summary>
    public float Margin = 0.3f;
    /// <summary>Ease rate toward a closer hit, per second; 0 = snap (a camera inside a wall is the worse pop).</summary>
    public float RateIn;
    /// <summary>Ease rate back out once the ray clears, per second.</summary>
    public float RateOut = 4f;
    /// <summary>Half-width of the ray fan around the focus-&gt;camera segment (0 = one ray).</summary>
    public float Radius = 0.3f;

    public bool Set(string key, float v)
    {
        switch (key)
        {
            case "margin": Margin = System.Math.Clamp(v, 0f, 5f); return true;
            case "in": RateIn = System.Math.Clamp(v, 0f, 60f); return true;
            case "out": RateOut = System.Math.Clamp(v, 0f, 60f); return true;
            case "radius": Radius = System.Math.Clamp(v, 0f, 2f); return true;
        }
        return false;
    }
}

/// <summary>#541: the auto-recenter policy, one set for every mode that has it on.</summary>
public sealed class Recenter
{
    /// <summary>Fastest swing behind the movement direction, deg/s (the ease slows it near the target).</summary>
    public float Rate = 60f;
    /// <summary>Degrees off the movement direction the camera may sit before a recenter starts.</summary>
    public float DeadZone = 15f;
    /// <summary>Seconds after the last orbit input during which no recenter runs.</summary>
    public float Grace = 1.5f;
    /// <summary>Seconds of standing still before the soft recenter to the facing; 0 = never.</summary>
    public float Idle = 5f;
    /// <summary>The soft recenter's fastest swing, deg/s.</summary>
    public float IdleRate = 30f;

    public bool Set(string key, float v)
    {
        switch (key)
        {
            case "rate": Rate = System.Math.Clamp(v, 1f, 720f); return true;
            case "deadzone": DeadZone = System.Math.Clamp(v, 0f, 180f); return true;
            case "grace": Grace = System.Math.Clamp(v, 0f, 30f); return true;
            case "idle": Idle = System.Math.Clamp(v, 0f, 600f); return true;
            case "idlerate": IdleRate = System.Math.Clamp(v, 1f, 720f); return true;
        }
        return false;
    }
}

/// <summary>#543: the free camera's feel and its one arming limit, the leash.</summary>
public sealed class FreeCamera
{
    /// <summary>Fly speed, world units per second (shift = 4x; the wheel scales it).</summary>
    public float Speed = 8f;
    /// <summary>Vertical FOV while free; 0 = whatever the camera had on entry.</summary>
    public float Fov;
    /// <summary>Roll on entry, degrees (Z/X roll live).</summary>
    public float Roll;
    /// <summary>Leash radius from the player's creature, world units, when the server set no camera max distance.
    /// Capped at the engine's own 25 m max-dist default (what top-down at max zoom already surveys); a server
    /// raises it through <c>SetCameraLimits</c>, never the client.</summary>
    public float Leash = LeashMax;
    /// <summary>Mouse-look sensitivity multiplier on top of <c>mouselook_sens</c>.</summary>
    public float Sens = 1f;
    /// <summary>Keyboard turn (the engine's turn keys) in degrees per second.</summary>
    public float Turn = 90f;

    public const float LeashMax = 25f;

    public bool Set(string key, float v)
    {
        switch (key)
        {
            case "speed": Speed = System.Math.Clamp(v, 0.1f, 200f); return true;
            case "fov": Fov = v == 0 ? 0 : System.Math.Clamp(v, CameraMode.FovMin, CameraMode.FovMax); return true;
            case "roll": Roll = System.Math.Clamp(v, -180f, 180f); return true;
            case "leash": Leash = System.Math.Clamp(v, 1f, LeashMax); return true;
            case "sens": Sens = System.Math.Clamp(v, 0.05f, 20f); return true;
            case "turn": Turn = System.Math.Clamp(v, 10f, 720f); return true;
        }
        return false;
    }
}

/// <summary>Persisted camera-layer preferences, flat <c>mode.key=value</c> in <c>sowx-camera.cfg</c> beside
/// <c>nwn.ini</c> (written by <c>/camera</c>, never hand-required). Every numeric key is clamped on parse.</summary>
public sealed class CameraConfig
{
    public static readonly string[] ModeNames = { "chase", "topdown", "stiff" };

    public readonly CameraMode[] Modes = { new(), new() { Drive = 0f, Recenter = 0f }, new() };
    public readonly SpringArm Arm = new();
    public readonly Recenter Recenter = new();
    public readonly FreeCamera Free = new();
    /// <summary>#540: most drive packets per second a heading change may raise (the engine's own keep-alive is 2/s).</summary>
    public float DriveHz = 10f;
    public const float DriveHzMin = 1f, DriveHzMax = 60f;
    /// <summary>#540: how fast the drive heading turns toward the keys, degrees per second (0 = snap).</summary>
    public float DriveTurn = 540f;
    public const float DriveTurnMax = 3600f;

    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        for (int m = 0; m < 3; m++)
        {
            CameraMode c = Modes[m]; string n = ModeNames[m];
            sb.Append($"{n}.fov={F(c.Fov)}\n{n}.lagpos={F(c.LagPos)}\n{n}.lagrot={F(c.LagRot)}\n{n}.lagzoom={F(c.LagZoom)}\n")
              .Append($"{n}.pitchcurve={F(c.PitchCurve)}\n{n}.pitchref={F(c.PitchRef)}\n")
              .Append($"{n}.offsetx={F(c.OffsetX)}\n{n}.offsety={F(c.OffsetY)}\n{n}.drive={F(c.Drive)}\n{n}.recenter={F(c.Recenter)}\n");
        }
        sb.Append($"arm.margin={F(Arm.Margin)}\narm.in={F(Arm.RateIn)}\narm.out={F(Arm.RateOut)}\narm.radius={F(Arm.Radius)}\n")
          .Append($"drive.hz={F(DriveHz)}\ndrive.turn={F(DriveTurn)}\n")
          .Append($"recenter.rate={F(Recenter.Rate)}\nrecenter.deadzone={F(Recenter.DeadZone)}\nrecenter.grace={F(Recenter.Grace)}\nrecenter.idle={F(Recenter.Idle)}\nrecenter.idlerate={F(Recenter.IdleRate)}\n")
          .Append($"free.speed={F(Free.Speed)}\nfree.fov={F(Free.Fov)}\nfree.roll={F(Free.Roll)}\nfree.leash={F(Free.Leash)}\nfree.sens={F(Free.Sens)}\nfree.turn={F(Free.Turn)}\n");
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
            if (key[..dot] == "arm") { cfg.Arm.Set(key[(dot + 1)..], v); return; }
            if (key[..dot] == "recenter") { cfg.Recenter.Set(key[(dot + 1)..], v); return; }
            if (key[..dot] == "free") { cfg.Free.Set(key[(dot + 1)..], v); return; }
            if (key == "drive.hz") { cfg.DriveHz = System.Math.Clamp(v, DriveHzMin, DriveHzMax); return; }
            if (key == "drive.turn") { cfg.DriveTurn = System.Math.Clamp(v, 0f, DriveTurnMax); return; }
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
        var c = Parse("topdown.fov=60\nchase.lagpos=0.3\nstiff.pitchcurve=99\nchase.pitchref=abc\nnope.fov=1\nchase.offsetx=-1.5\narm.margin=0.5\ntopdown.drive=1\ndrive.hz=500\nrecenter.grace=2\nstiff.recenter=0\nfree.speed=12\nfree.leash=90\n");
        E(c.Free.Speed == 12f && c.Free.Leash == FreeCamera.LeashMax && new CameraConfig().Free.Leash == FreeCamera.LeashMax, "free keys parse, the leash never exceeds the engine's 25");
        E(c.Modes[0].OffsetX == -1.5f && c.Arm.Margin == 0.5f, "offset and arm keys parse");
        E(c.Recenter.Grace == 2f && c.Modes[2].Recenter == 0f && c.Modes[0].Recenter == 1f && new CameraConfig().Modes[1].Recenter == 0f, "recenter keys parse, top-down defaults off");
        E(c.Modes[1].Drive == 1f && c.DriveHz == DriveHzMax && new CameraConfig().Modes[1].Drive == 0f && new CameraConfig().Modes[0].Drive == 1f, "drive keys parse, top-down defaults off");
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
            case "lagzoom": c.LagZoom = System.Math.Clamp(v, 0f, CameraMode.LagMax); return true;
            case "pitchcurve": c.PitchCurve = System.Math.Clamp(v, -CameraMode.CurveMax, CameraMode.CurveMax); return true;
            case "pitchref": c.PitchRef = System.Math.Clamp(v, CameraMode.RefMin, CameraMode.RefMax); return true;
            case "offsetx": c.OffsetX = System.Math.Clamp(v, -CameraMode.OffsetMax, CameraMode.OffsetMax); return true;
            case "offsety": c.OffsetY = System.Math.Clamp(v, -CameraMode.OffsetMax, CameraMode.OffsetMax); return true;
            case "drive": c.Drive = v != 0 ? 1f : 0f; return true;
            case "recenter": c.Recenter = v != 0 ? 1f : 0f; return true;
        }
        return false;
    }
}
