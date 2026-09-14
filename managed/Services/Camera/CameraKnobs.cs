using CX.Framework;

namespace CX.Services.Camera;

/// <summary>The seven camera groups of spec §4.4 in panel order. <see cref="Declare"/> touches each class so
/// the groups declare on the main thread at Initialize, in this order, whether or not a camera service arms
/// (a group that stands down still declares).</summary>
internal static class CameraKnobs
{
    internal static void Declare()
    {
        _ = CameraModes.ByMode; _ = Move.G; _ = Walls.G; _ = FreeCam.G; _ = MouseLook.G;
    }

    internal const float FovMin = 10f, FovMax = 120f;

    /// <summary>Off-device, read-only (a set would arm the writer against the user's real file): the seven groups
    /// declare in panel order, the per-mode defaults differ where the spec says, the FOV magic zero is gone from
    /// the modes and kept on <c>free.fov</c>, and <c>/chase fov 60</c> parses to the chase row alone.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"CameraKnobs.SelfCheck FAIL: {w}"); } }
        Declare();
        string[] order = { "chase", "topdown", "stiff", "move", "walls", "free", "mouselook" };
        int at = -1;
        foreach (string g in order)
        {
            int i = -1;
            var all = KnobGroup.All;
            for (int k = 0; k < all.Count; k++) if (all[k].Name == g) i = k;
            E(i > at, $"group {g} declares after the one before it");
            at = i;
        }
        E(CameraModes.ByMode[0] == CameraModes.Chase && CameraModes.ByMode[1] == CameraModes.TopDown && CameraModes.ByMode[2] == CameraModes.Stiff,
          "the mode byte indexes chase, top-down, stiff");
        var fov = KnobGroup.Find("chase.fov") as Knob<float>;
        E(fov != null && fov == CameraModes.Chase.Fov && fov.Default == 45f && fov.Min == FovMin && fov.Max == FovMax, "chase.fov is the 10-120 slider defaulting to 45");
        E(fov!.TryParse("60", out float v) && v == 60f && CameraModes.TopDown.Fov != fov, "/chase fov 60 parses and names the chase row only");
        E(CameraModes.Chase.Drive.Default && !CameraModes.TopDown.Drive.Default && CameraModes.Stiff.Drive.Default
          && CameraModes.Chase.Recenter.Default && !CameraModes.TopDown.Recenter.Default, "drive and recenter default off for top-down only");
        E(CameraModes.Chase.Enabled.Key == "chase.enabled" && MouseLook.Enabled.Key == "mouselook.enabled", "the masters are declared");
        E(MouseLook.Enabled.Changed == MouseLookService.MasterChanged && CameraModes.Chase.Enabled.Changed == null, "mouselook's master releases on its edge; a mode's master is read per Control");
        E(KnobGroup.Find("free.enter") is KnobButton && FreeCam.G.Knobs[0] == FreeCam.Enter, "free.enter is the first row and a button");
        E(FreeCam.Fov.Default == 0f && FreeCam.Fov.Min == 0f && FreeCam.Leash.Default == FreeCam.LeashMax && FreeCam.Leash.Max == FreeCam.LeashMax,
          "free.fov keeps 0 = the mode's; the leash never exceeds the engine's 25");
        E(Walls.Fade.Default && Walls.Floor.Default == 0.25f && Walls.ArmOut.Default == 4f, "walls defaults are the #537/#538 boot values");
        E(!MouseLook.Right.Default && MouseLook.Sens.Default == 1f && MouseLook.Threshold.Default == 4 && !MouseLook.Invert.Default, "mouselook defaults are the old boot keys'");
        E(Move.Rate.Default == 60f && Move.Idle.Default == 5f && Move.Hz.Default == 10f && Move.Turn.Default == 540f, "move defaults are the #540/#541 values");
        E(KnobGroup.Find("camera.occlude") == null && KnobGroup.Find("mouselook_sens") == null, "the retired flat keys are not declared");
        log.Info(ok ? "CameraKnobs.SelfCheck: passed" : "CameraKnobs.SelfCheck: FAILED");
    }
}

/// <summary>One engine camera mode's group: <c>chase</c> / <c>topdown</c> / <c>stiff</c>, identical rows with
/// per-mode values, each with its own master. Off is the stock camera for that mode (#584): the ninth
/// <c>CameraService.Gate</c> value, read per Control (feel, recenter and the drive stand aside, the view angle
/// goes back to the first one read), so it needs no <c>changed:</c>. Read hot by <see cref="CameraService"/> and
/// <see cref="CameraDriveService"/> through <see cref="CameraModes.ByMode"/>.</summary>
public sealed class CameraModeKnobs
{
    public readonly KnobGroup G;
    public readonly Knob<bool> Enabled, Drive, Recenter;
    public readonly Knob<float> Fov, LagPos, LagRot, LagZoom, PitchCurve, PitchRef, OffsetX, OffsetY;

    internal CameraModeKnobs(string name, string submenu, bool moveDefault)
    {
        G = KnobGroup.Declare(name, "Camera", submenu, master: "Enable the CX camera for this mode",
            masterHelp: "CX shapes this camera mode: smoothing, field of view, offset, camera-relative movement, auto-recenter. Off, the stock camera runs untouched in this mode.");
        Enabled = G.Master!;
        G.Section("View");
        Fov = G.Slider("fov", "Field of view", CameraKnobs.FovMin, CameraKnobs.FovMax, 1f, 45f, "deg",
                       help: "Vertical field of view. The game's own is 45.");
        G.Section("Smoothing");
        LagPos = G.Slider("lagpos", "Position smoothing", 0f, 2f, 0.05f, 0f, "s",
                          help: "How long the camera takes to catch up with your character. 0 is welded, as in the stock game.");
        LagRot = G.Slider("lagrot", "Rotation smoothing", 0f, 2f, 0.05f, 0f, "s",
                          help: "How long a turn takes to settle. 0 is welded.");
        LagZoom = G.Slider("lagzoom", "Zoom smoothing", 0f, 2f, 0.01f, 0.02f, "s",
                           help: "Smooths the mouse wheel's stepped zoom. 0 is the stock game's steps.");
        G.Section("View");
        PitchCurve = G.Slider("pitchcurve", "Tilt down when zoomed out", -10f, 10f, 0.5f, 0f, "deg/m",
                              help: "Degrees of extra tilt per metre of zoom past the reference distance. Negative lowers the angle instead. 0 leaves the tilt where you put it.");
        PitchRef = G.Slider("pitchref", "Tilt reference distance", 1f, 60f, 1f, 10f, "m", Tier.Advanced);
        G.Section("Offset");
        OffsetX = G.Slider("offsetx", "Sideways offset", -10f, 10f, 0.25f, 0f, "m",
                           help: "Moves your character off centre; positive puts them on the left. 0 is the stock orbit.");
        OffsetY = G.Slider("offsety", "Height offset", -10f, 10f, 0.25f, 0f, "m",
                           help: "Raises the camera's focus above your character.");
        G.Section("Movement");
        Drive = G.Toggle("drive", "Move relative to the camera", moveDefault,
                         help: "Forward is where the camera looks, not where your character faces.");
        Recenter = G.Toggle("recenter", "Swing behind you as you move", moveDefault,
                            help: "The camera turns to follow your movement direction instead of the stock chase. Tuning is under Movement.");
    }
}

/// <summary>The three mode groups, indexed by the engine's camera mode byte (0 chase, 1 top-down, 2 stiff chase).</summary>
internal static class CameraModes
{
    internal static readonly CameraModeKnobs Chase, TopDown, Stiff;
    internal static readonly CameraModeKnobs[] ByMode;

    static CameraModes()
    {
        Chase = new CameraModeKnobs("chase", "Chase", moveDefault: true);
        TopDown = new CameraModeKnobs("topdown", "Top-down", moveDefault: false);
        Stiff = new CameraModeKnobs("stiff", "Stiff chase", moveDefault: true);
        ByMode = new[] { Chase, TopDown, Stiff };
    }
}

/// <summary>The <c>move</c> group: the auto-recenter policy (#541) and the drive tuning (#540), one set for every
/// mode whose Movement toggles are on.</summary>
internal static class Move
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Rate, DeadZone, Idle, Grace, IdleRate, Hz, Turn;

    static Move()
    {
        G = KnobGroup.Declare("move", "Camera", "Movement",
            header: "No master: tuning for the Movement toggles of each camera mode.");
        G.Section("Recenter");
        Rate = G.Slider("rate", "Recenter speed", 1f, 720f, 5f, 60f, "deg/s",
                        help: "Fastest swing behind your movement; it eases in near the target.");
        DeadZone = G.Slider("deadzone", "Recenter dead zone", 0f, 180f, 5f, 15f, "deg",
                            help: "How far off your movement direction the camera may sit before it starts to swing.");
        Idle = G.Slider("idle", "Recenter after standing still", 0f, 60f, 0.5f, 5f, "s",
                        help: "Seconds of standing still before the camera drifts behind your character's facing. 0 never does.");
        Grace = G.Slider("grace", "Orbit grace", 0f, 30f, 0.1f, 1.5f, "s", Tier.Advanced,
                         help: "No recenter this long after you orbit by hand.");
        IdleRate = G.Slider("idlerate", "Idle recenter speed", 1f, 720f, 5f, 30f, "deg/s", Tier.Advanced);
        G.Section("Drive");
        Hz = G.Slider("hz", "Heading updates per second", 1f, 60f, 1f, 10f, "", Tier.Advanced,
                      help: "Most heading packets sent per second; the stock game sends 2.");
        Turn = G.Slider("turn", "Heading turn rate", 0f, 3600f, 30f, 540f, "deg/s", Tier.Advanced, help: "0 = snap.");
    }
}

/// <summary>The <c>walls</c> group: the occlusion fade (#537, read by <c>AlloyRenderer.Occlude</c>) and the offset
/// camera's spring arm (#538, read by <see cref="CameraService"/>). The fade rate stays a const.</summary>
internal static class Walls
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Fade;
    internal static readonly Knob<float> Floor, Radius, Margin, ArmIn, ArmOut, ArmRadius;

    static Walls()
    {
        G = KnobGroup.Declare("walls", "Camera", "Walls",
            header: "The spring arm only acts while a sideways or height offset is set.");
        G.Section("Fade");
        Fade = G.Toggle("fade", "Fade objects that hide you", true,
                        help: "Walls and props between the camera and your character turn see-through. Off, the camera pulls in closer as in the stock game. Needs the Alloy renderer.");
        Floor = G.Slider("floor", "Faded opacity", 0f, 1f, 0.05f, 0.25f,
                         help: "How solid a faded object stays. 0 hides it entirely.");
        Radius = G.Slider("radius", "Fade ray width", 0f, 5f, 0.1f, 0.5f, "m", Tier.Advanced,
                          help: "Half-width of the ray fan that finds occluders.");
        G.Section("Spring arm");
        Margin = G.Slider("margin", "Arm margin", 0f, 5f, 0.1f, 0.3f, "m", Tier.Advanced,
                          help: "How far short of a wall the offset camera stops.");
        ArmIn = G.Slider("armin", "Arm pull-in rate", 0f, 60f, 1f, 0f, "/s", Tier.Advanced, help: "0 = snap.");
        ArmOut = G.Slider("armout", "Arm ease-out rate", 0f, 60f, 1f, 4f, "/s", Tier.Advanced);
        ArmRadius = G.Slider("armradius", "Arm ray width", 0f, 2f, 0.1f, 0.3f, "m", Tier.Advanced, help: "0 = one ray.");
    }
}

/// <summary>The <c>free</c> group (#543): the enter button first, then the feel. <see cref="Fov"/> keeps the spec's
/// one magic zero -- 0 = the field of view of the mode the camera left -- so its floor is 0 and a value under
/// <see cref="CameraKnobs.FovMin"/> snaps up to it.</summary>
internal static class FreeCam
{
    internal static readonly KnobGroup G;
    internal static readonly KnobButton Enter;
    internal static readonly Knob<float> Speed, Leash, Fov, Sens, Turn, Roll;

    /// <summary>The engine's own 25 m max-dist default; a server raises it through <c>SetCameraLimits</c>, never the client.</summary>
    internal const float LeashMax = 25f;

    static FreeCam()
    {
        G = KnobGroup.Declare("free", "Camera", "Free camera",
            header: "No master: the free camera is entered from the button and left with Escape.");
        Enter = G.Button("enter", "Enter the free camera", FreeCameraService.Toggle,
                         help: "Fly the camera freely: move keys fly, Space/C up and down, Z/X roll, Shift is fast, the wheel sets speed, right-drag looks. Escape returns. Closes this panel.");
        G.Section("Feel");
        Speed = G.Slider("speed", "Fly speed", 0.1f, 200f, 0.5f, 8f, "m/s", help: "How fast the move keys fly the camera; Shift is four times this.");
        Leash = G.Slider("leash", "Range from your character", 1f, LeashMax, 1f, LeashMax, "m",
                         help: "The camera stops this far from your character. A server that sets its own camera limit overrides it.");
        Fov = G.Slider("fov", "Field of view", 0f, CameraKnobs.FovMax, 1f, 0f, "deg", Tier.Advanced,
                       help: "0 keeps the field of view of the mode you came from.", changed: FovChanged);
        Sens = G.Slider("sens", "Look sensitivity", 0.05f, 20f, 0.05f, 1f, "x", Tier.Advanced, help: "On top of Mouse-look's sensitivity.");
        Turn = G.Slider("turn", "Keyboard turn speed", 10f, 720f, 10f, 90f, "deg/s", Tier.Advanced);
        Roll = G.Slider("roll", "Roll on entry", -180f, 180f, 5f, 0f, "deg", Tier.Advanced, changed: FreeCameraService.RollChanged);
    }

    private static void FovChanged()
    {
        if (Fov.V > 0f && Fov.V < CameraKnobs.FovMin) Fov.V = CameraKnobs.FovMin;
        FreeCameraService.FovChanged();
    }
}

/// <summary>The <c>mouselook</c> group (#539). The master is the whole service (#584): off = the stock middle-drag,
/// read per event, with <see cref="MouseLookService.MasterChanged"/> releasing a drag under way on the off edge.
/// The four former boot keys are live.</summary>
internal static class MouseLook
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Right, Invert;
    internal static readonly Knob<float> Sens;
    internal static readonly Knob<int> Threshold;

    static MouseLook()
    {
        G = KnobGroup.Declare("mouselook", "Camera", "Mouse-look", master: "Enable CX mouse-look",
            masterHelp: "CX handles the mouse-drag camera: the cursor stays put and the view never stalls at the screen edge. Off, the stock middle-drag orbit runs.",
            masterChanged: MouseLookService.MasterChanged);
        Enabled = G.Master!;
        Right = G.Toggle("right", "Right-drag looks around", false,
                         help: "Hold the right button and drag to orbit; a still right-click still opens the radial menu.");
        Sens = G.Slider("sens", "Sensitivity", 0.05f, 20f, 0.05f, 1f, "x", help: "1 matches the stock middle-drag.");
        Invert = G.Toggle("invert", "Invert vertical", false, help: "Dragging up tilts the view down.");
        Threshold = G.Slider("threshold", "Click-to-drag threshold", 0, 100, 1, 4, "px", Tier.Advanced,
                             help: "Pixels of movement before a right press becomes a look instead of a click.");
    }
}
