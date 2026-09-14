using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Services.Nameplate;

namespace Sowx.Services.Camera;

/// <summary>
/// Camera-relative WASD (#540, map #535; docs/research/custom-camera.md §10). Pre-hooks the engine's per-frame
/// drive step, <c>CClientExoAppInternal::UpdateDriveMode</c>: while a drive key is down and the mode's <c>drive</c>
/// knob is on, the player creature's orientation is written to camera yaw + key direction (W = the camera's
/// forward, A/D and the strafe keys = its right, diagonals at 45°), forward is the only key the engine sees for
/// the call, and the engine's "input changed" latch is raised when the heading moved past the wire's 0.1°
/// quantum (at most <c>drive.hz</c> times a second). The engine then derives the packet angle from that
/// orientation, sends run-forward, snaps the local facing, predicts, keeps alive and aborts on release by itself,
/// and the server adopts the world-space heading (§10.2): no server change, no new opcode. The one release the
/// engine does not abort on is a turn key's (its key-up path aborts only for forward/back/strafe/auto-run), so
/// that one sends the same <c>AbortDriveControl</c> (major 6, minor 0x21) from here.
/// <para>The heading slews toward the keys at <c>drive.turn</c> deg/s (starting from the creature's facing), and
/// the creature's local facing-target pair is written with it every frame so the model turns as smoothly as the
/// wire does. While the drive is active the engine's chase cam stands down (<see cref="EngineLayout.ModuleChaseSuppressed"/>
/// held up, its idle edge held down): a chase that swings the camera behind a creature strafing at 90° would turn
/// "strafe" into a spiral, and a held mouse orbit must win over every other camera motion.</para>
/// <para>Stands down (engine drive untouched) under every <see cref="CameraService"/> gate -- dialog, cutscene,
/// clear-view, attached elsewhere, a server lock -- before the first camera solve, and when the creature's own
/// drive gates are shut. Hot: once per frame from ProcessInput, pointer reads and one atan2, no allocation.</para>
/// </summary>
public sealed unsafe class CameraDriveService : IExtenderService
{
    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, void> _orig;
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    private static readonly delegate* unmanaged<IntPtr, uint, IntPtr> _getGameObject =
        (delegate* unmanaged<IntPtr, uint, IntPtr>)Native.Sym(SymbolMap.GetGameObject);

    private static readonly int ChangedOff = EngineLayout.InternalDriveChanged;
    private static readonly int AutoRunOff = EngineLayout.InternalDriveAutoRun;
    private static readonly int KeysOff = EngineLayout.InternalDriveKeys;
    private static readonly int LastSendOff = EngineLayout.InternalDriveLastSend;
    private static readonly int PlayerIdOff = EngineLayout.InternalPlayerCreatureId;
    private static readonly int OriOff = EngineLayout.ObjOrientation;
    private static readonly int DriveFlagsOff = EngineLayout.CreatureDriveFlags;
    private static readonly int DriveEnabledOff = EngineLayout.CreatureDriveEnabled;
    private static readonly int FacingTargetOff = EngineLayout.CreatureFacingTarget;
    private static readonly int FacingTargetBOff = EngineLayout.CreatureFacingTargetB;
    private static readonly int ModCameraOff = EngineLayout.ModuleCamera;
    private static readonly int ModChaseOffOff = EngineLayout.ModuleChaseSuppressed;
    private static readonly int ModWasIdleOff = EngineLayout.ModuleChaseWasIdle;
    private static readonly int CamRotOff = EngineLayout.CamRot;

    /// <summary>Key slots at <see cref="EngineLayout.InternalDriveKeys"/>, in the engine's order.</summary>
    private const int Fwd = 0, Back = 1, TurnL = 2, TurnR = 3, StrafeL = 4, StrafeR = 5, KeyCount = 6;
    /// <summary><c>CNWCCreature</c> control flag bit UpdateDriveMode and DrivePlayer both require.</summary>
    private const byte PlayerControlled = 2;
    private const byte MajorInput = 6, MinorAbortDrive = 0x21;
    /// <summary>The wire angle is tenths of a degree, East = 0, counter-clockwise.</summary>
    private const int WireTenths = 3600;
    private const float Deg = MathF.PI / 180f;

    private enum Why { Active, Idle, Probe, NoMode, Off, Gate, NoCamera, NoCreature, NotDrivable, Cancelled, Free }
    private static readonly string[] WhyNames =
        { "active", "no key held", "layout probe failed", "no live camera mode", "off for this mode", "camera held down",
          "no world camera", "no player creature", "creature not drivable", "forward and back cancel", "free camera" };

    private static bool _live, _probeOk, _active, _engineAborts;
    /// <summary>#541: the drive is running without a forward component (strafe/turn keys alone, or back), so the
    /// movement is a deliberate sidestep or back-pedal the recenter must not swing behind -- a strafe would
    /// become a spiral and a back-pedal a 180° swing that has no side to take.</summary>
    internal static bool NoForward { get; private set; }
    private static Why _why = Why.Idle;
    private static int _sentTenths = -1, _tenths;
    /// <summary>The slewed heading in degrees, East 0 CCW; seeded from the creature's facing when a drive starts.</summary>
    private static float _heading;
    private static long _lastSendTick, _lastTick;
    private static int _sends, _latches, _aborts;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        SelfCheck(log);
        if (!EngineLayout.Require(log, "CameraDrive", "CClientExoAppInternal", "CNWCObject", "CNWCCreature", "CNWCModule", "Camera")) return;
        if (!SymbolMap.Require(log, "CameraDrive", SymbolMap.UpdateDriveMode, SymbolMap.AppManager, SymbolMap.GetGameObject,
                               SymbolMap.GetModule, SymbolMap.SendPlayerToServerMessage)) return;
        _orig = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.UpdateDriveMode,
                                                                   (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnUpdateDriveMode);
        if (_orig == null) { log.Info("CameraDrive: standing down (UpdateDriveMode hook refused)"); return; }
        CameraCommand.Register("drive", "`drive 0|1` camera-relative WASD for the current camera mode; `drive hz <n>` most heading packets per second; `drive turn <deg/s>` heading slew (0 = snap)", Drive);
        CameraCommand.Report(Report);
        log.Info("CameraDrive: pre-hooked CClientExoAppInternal::UpdateDriveMode");
    }

    [UnmanagedCallersOnly]
    private static void OnUpdateDriveMode(IntPtr app)
    {
        if (!_live)
        {
            _live = true;
            _probeOk = EngineLayout.Probe("CClientExoAppInternal", app, _log);
            _log?.Info(_probeOk ? "CameraDrive: first UpdateDriveMode -- hook is LIVE" : "CameraDrive: layout probe failed -- engine drive untouched");
        }
        int* keys = (int*)(app + KeysOff);
        if (FreeCameraService.Active)
        {
            // #543: the keys fly the camera; the engine sees none held (a drive under way is aborted, once).
            _why = Why.Free; NoForward = false;
            if (_active) { _active = false; ClientApi.Send(MajorInput, MinorAbortDrive, null, 0); _aborts++; }
            int a0 = *(int*)(app + AutoRunOff), s0 = keys[0], s1 = keys[1], s2 = keys[2], s3 = keys[3], s4 = keys[4], s5 = keys[5];
            *(int*)(app + AutoRunOff) = 0; keys[0] = keys[1] = keys[2] = keys[3] = keys[4] = keys[5] = 0;
            _orig(app);
            *(int*)(app + AutoRunOff) = a0; keys[0] = s0; keys[1] = s1; keys[2] = s2; keys[3] = s3; keys[4] = s4; keys[5] = s5;
            return;
        }
        int autoRun = *(int*)(app + AutoRunOff);
        bool held = autoRun != 0;
        for (int k = 0; k < KeyCount; k++) held |= keys[k] != 0;
        // The engine's own key-up aborts the drive for these; a turn key's release is ours to abort.
        bool engineKey = (autoRun | keys[Fwd] | keys[Back] | keys[StrafeL] | keys[StrafeR]) != 0;
        IntPtr cre = IntPtr.Zero, cam = IntPtr.Zero;
        Why why = !held ? Why.Idle : !_probeOk ? Why.Probe : Wanted(app, out cre, out cam);
        float f = 0f, s = 0f;
        if (why == Why.Active)
        {
            f = (keys[Fwd] | autoRun) != 0 ? 1f : 0f; f -= keys[Back] != 0 ? 1f : 0f;
            s = (keys[TurnR] | keys[StrafeR]) != 0 ? 1f : 0f; s -= (keys[TurnL] | keys[StrafeL]) != 0 ? 1f : 0f;
            if (f == 0f && s == 0f) why = Why.Cancelled;
        }
        _why = why;
        NoForward = why == Why.Active && f <= 0f;
        if (why != Why.Active)
        {
            if (_active && !held)
            {
                _active = false;
                if (!_engineAborts) { ClientApi.Send(MajorInput, MinorAbortDrive, null, 0); _aborts++; }
            }
            _orig(app);
            return;
        }

        // Camera+0xbc is (w, x, y, z); pitch is about the camera's X, so its right stays on the ground plane
        // and forward-on-the-ground is up x right = (-right.y, right.x).
        float* rot = (float*)(cam + CamRotOff);
        Vector3 r = CameraService.Right(new Quaternion(rot[1], rot[2], rot[3], rot[0]));
        float* ori = (float*)(cre + OriOff);
        long now = Environment.TickCount64;
        if (!_active) { _heading = Degrees(ori[0], ori[1]); _lastTick = now; }
        float dt = MathF.Min((now - _lastTick) * 0.001f, 0.1f);
        _lastTick = now;
        _heading = Slew(_heading, Heading(r.X, r.Y, f, s), CameraService.Config.DriveTurn, dt);
        int tenths = Tenths(_heading);
        _tenths = tenths;
        (float sn, float cs) = MathF.SinCos(tenths * 0.1f * Deg);
        ori[0] = cs; ori[1] = sn; ori[2] = 0f;
        float* tgt = (float*)(cre + FacingTargetOff);
        tgt[0] = cs; tgt[1] = sn; tgt[2] = 0f;
        tgt = (float*)(cre + FacingTargetBOff);
        tgt[0] = cs; tgt[1] = sn; tgt[2] = 0f;
        // The chase cam reads these in UpdateCamera, later this frame: no swing behind a strafing creature.
        IntPtr module = CameraService.Module();
        *(int*)(module + ModChaseOffOff) = 1;
        *(int*)(module + ModWasIdleOff) = 0;

        if (tenths != _sentTenths && *(int*)(app + ChangedOff) == 0
            && now - _lastSendTick >= (long)(1000f / CameraService.Config.DriveHz)) { *(int*)(app + ChangedOff) = 1; _latches++; }

        long sendBefore = *(long*)(app + LastSendOff);
        int k0 = keys[0], k1 = keys[1], k2 = keys[2], k3 = keys[3], k4 = keys[4], k5 = keys[5];
        keys[Fwd] = 1; keys[Back] = keys[TurnL] = keys[TurnR] = keys[StrafeL] = keys[StrafeR] = 0;
        _orig(app);
        keys[0] = k0; keys[1] = k1; keys[2] = k2; keys[3] = k3; keys[4] = k4; keys[5] = k5;
        if (*(long*)(app + LastSendOff) != sendBefore) { _sentTenths = tenths; _lastSendTick = now; _sends++; }
        _active = true; _engineAborts = engineKey;
    }

    /// <summary>The heading in degrees (East 0, CCW, [0, 360)) of <c>forward * f + right * s</c> for a camera whose
    /// ground-plane right is (rx, ry).</summary>
    private static float Heading(float rx, float ry, float f, float s) => Degrees(-ry * f + rx * s, rx * f + ry * s);

    private static float Degrees(float x, float y)
    {
        float deg = MathF.Atan2(y, x) / Deg;
        return deg < 0f ? deg + 360f : deg;
    }

    /// <summary>The wire's tenths of a degree.</summary>
    private static int Tenths(float deg) => ((int)MathF.Round(deg * 10f) % WireTenths + WireTenths) % WireTenths;

    /// <summary>Turn <paramref name="from"/> toward <paramref name="to"/> the short way, at most rate*dt degrees (rate 0 = snap).</summary>
    private static float Slew(float from, float to, float rate, float dt)
    {
        float d = to - from;
        d -= 360f * MathF.Round(d / 360f);
        if (rate > 0f) d = Math.Clamp(d, -rate * dt, rate * dt);
        float h = from + d;
        return h < 0f ? h + 360f : h >= 360f ? h - 360f : h;
    }

    private static Why Wanted(IntPtr app, out IntPtr cre, out IntPtr cam)
    {
        cre = cam = IntPtr.Zero;
        int mode = CameraService.Mode;
        if (mode < 0) return Why.NoMode;
        if (CameraService.Config.Modes[mode].Drive == 0f) return Why.Off;
        if (CameraService.GateUp) return Why.Gate;
        IntPtr module = CameraService.Module();
        cam = module == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(module + ModCameraOff);
        if (cam == IntPtr.Zero) return Why.NoCamera;
        IntPtr mgr = *(IntPtr*)_appMgr;
        IntPtr exo = mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
        cre = exo == IntPtr.Zero ? IntPtr.Zero : _getGameObject(exo, *(uint*)(app + PlayerIdOff));
        if (cre == IntPtr.Zero || *(byte*)(cre + EngineLayout.ObjType) != NameplateNative.TypeCreature) return Why.NoCreature;
        if ((*(byte*)(cre + DriveFlagsOff) & PlayerControlled) == 0 || *(int*)(cre + DriveEnabledOff) == 0) return Why.NotDrivable;
        return Why.Active;
    }

    // ---- /camera drive (cold) ----

    private static string Drive(string args)
    {
        string[] p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 2 && p[0] == "hz" && CameraConfig.TryF(p[1], out float hz))
            CameraService.Edit().DriveHz = Math.Clamp(hz, CameraConfig.DriveHzMin, CameraConfig.DriveHzMax);
        else if (p.Length == 2 && p[0] == "turn" && CameraConfig.TryF(p[1], out float turn))
            CameraService.Edit().DriveTurn = Math.Clamp(turn, 0f, CameraConfig.DriveTurnMax);
        else if (p.Length == 1 && CameraConfig.TryF(p[0], out float on))
        {
            if (CameraService.Mode < 0) return "[camera] no live camera mode yet -- log in first";
            CameraService.Edit().Modes[CameraService.Mode].Set("drive", on);
        }
        return Report();
    }

    private static string Report()
    {
        int mode = CameraService.Mode;
        string on = mode < 0 ? "?" : CameraService.Config.Modes[mode].Drive != 0f ? "on" : "off";
        return FormattableString.Invariant(
            $"[camera] drive: {on} ({(mode < 0 ? "no mode" : CameraConfig.ModeNames[mode])}) heading {_tenths / 10f:0.0} | {(_why == Why.Active ? "active" : "held down: " + WhyNames[(int)_why])} | {_sends} send(s), {_latches} latch(es), {_aborts} abort(s), drive.hz {CameraService.Config.DriveHz:0.#} drive.turn {CameraService.Config.DriveTurn:0}");
    }

    /// <summary>The heading maths off-device: the four cardinal keys against a camera looking north (right = +X).</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"CameraDrive.SelfCheck FAIL: {w}"); } }
        static int T(float rx, float ry, float f, float s) => Tenths(Heading(rx, ry, f, s));
        E(T(1, 0, 1, 0) == 900, "W walks north (90°)");
        E(T(1, 0, -1, 0) == 2700, "S walks south (270°)");
        E(T(1, 0, 0, 1) == 0, "D walks east (0°)");
        E(T(1, 0, 0, -1) == 1800, "A walks west (180°)");
        E(T(1, 0, 1, 1) == 450, "W+D is the 45° diagonal");
        E(T(0, 1, 1, 0) == 1800, "camera turned to look west: W walks west");
        E(Tenths(Slew(350f, 10f, 540f, 0.01f)) == 3554, "the slew takes the short way across 0 and wraps");
        E(Tenths(Slew(10f, 350f, 540f, 0.01f)) == 46 && Slew(10f, 350f, 0f, 0.01f) == 350f, "and back; rate 0 snaps");
        E(Slew(90f, 270f, 540f, 1f) == 270f, "a reach within the budget lands exactly");
        log.Info(ok ? "CameraDrive.SelfCheck: passed" : "CameraDrive.SelfCheck: FAILED");
    }
}
