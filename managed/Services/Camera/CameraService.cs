using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>
/// The camera layer (#536, map #535, ADR: docs/research/custom-camera.md §8 option A). Chains on the engine's
/// one camera solve, <c>CAurBehaviorCameraFollow::Control</c>, lets it run, then rewrites its answer -- the
/// pose at Camera+0xb0/+0xbc and the view angle at +0x4c4 -- from the same behavior state plus three feels the
/// engine lacks: an FOV knob, a critically damped position/rotation/zoom lag (the engine's zoom is a one-frame
/// impulse per wheel notch, so the rendered distance is damped toward the behavior's), and pitch as a function of distance.
/// #538 adds a free lateral/vertical offset of the rig from the focus with its own spring arm (the engine's
/// pull-in predicts from the un-offset pose, so it is switched off through <see cref="SettleCollision"/> while
/// an offset is active; at 0 0 the pose is #536's, bit for bit, and the engine's collision is left alone).
/// The behavior's own yaw/pitch/dist are never written, so dialog framing, cutscenes, Store/RestoreCameraFacing
/// and the server's locks keep reading the truth. #541 is the one exception, and it goes through the engine's own
/// input channel: the auto-recenter (<see cref="Recenter"/>) adds a yaw <i>velocity</i> before Control, the same
/// field the engine's chase drivers write, with those drivers held down by the two module flags they honour.
/// <para><b>Stand-down gates</b>, checked every call and reported by <c>/camera</c>: not the module camera (a
/// GUI panel's), dialog camera, cutscene interpolation, clear-view pending, attached to something other than the
/// player, any server lock (photo mode locks all three axes), unknown mode byte. When a gate is up the engine's
/// answer passes through untouched, and the frame after it lifts is a cut (<see cref="AlloyRenderer.CameraCut"/>).</para>
/// <para><b>Hot.</b> Runs inside <c>CNWCArea::Update</c> once per world frame: pointer reads, float math, no
/// allocation. The cfg load and every string live on the frame-end tick and the command.</para>
/// </summary>
public sealed unsafe class CameraService : IExtenderService
{
    private static ExtenderLog? _log;
    private static IntPtr* _next;
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    private static readonly delegate* unmanaged<IntPtr, IntPtr> _getModule =
        (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetModule);
    private static readonly delegate* unmanaged<IntPtr, uint, IntPtr> _getGameObject =
        (delegate* unmanaged<IntPtr, uint, IntPtr>)Native.Sym(SymbolMap.GetGameObject);

    private static CameraConfig _cfg = new();
    private static readonly ConfigFile _file = new("cx-camera.cfg");
    private static bool _configLoaded, _dirty;

    /// <summary>Why the layer is (or is not) touching the pose this frame.</summary>
    internal enum Gate { Active, NoModule, NotWorldCamera, Dialog, Cutscene, ClearView, Attached, Locked, Mode }

    internal static readonly string[] GateNames =
        { "active", "no module", "not the world camera", "dialog camera", "cutscene", "clear-view pending",
          "attached elsewhere", "server lock", "unknown camera mode" };

    // ---- last solve, for /camera (written hot, read cold) ----
    private static Gate _gate = Gate.NoModule;
    private static int _mode = -1;
    private static float _yaw, _pitch, _dist, _fovRead, _outPitch;
    private static Vector3 _pos;
    /// <summary>The spring arm's fraction of the focus-&gt;camera segment the camera sits at (1 = the full offset rig).</summary>
    private static float _arm = 1f;
    private static bool _offsetOn, _wantNoCollision, _weSetFlag;

    // ---- #541 recenter state: the policy's clocks ----
    private static float _grace, _idle, _recenterVel;
    private static bool _pursuing;
    /// <summary>Why the recenter did nothing on the last call, for /camera.</summary>
    private enum Hold { Recentering, Off, NoCreature, Orbiting, Grace, Lateral, DeadZone, Idle, Centered }
    private static readonly string[] HoldNames =
        { "recentering", "off for this mode", "no player creature", "orbiting", "orbit grace", "strafing or backing", "in the dead-zone", "idle", "centered" };
    private static Hold _hold = Hold.Off;
    /// <summary>Degrees of error beyond which a swing already under way keeps its side (a target straight behind
    /// the camera has no short way round).</summary>
    private const float TieZone = 150f;

    // ---- lag state, keyed by the behavior object: SetFollow replaces it on every attach / transition ----
    private static IntPtr _lastSelf;
    private static int _lastMode = -1;
    private static bool _welded = true;
    private static float _sYaw, _sPitch, _vYaw, _vPitch, _sDist, _vDist;
    private static Vector3 _sFocus, _vFocus;

    // ---- §9 instruments (logs=1): Control cadence per present, runtime FOV writers ----
    private static int _calls, _callsMin = int.MaxValue, _callsMax, _frames, _fovRewrites;
    /// <summary>§9.5: frames the engine's pull-in shortened dist without the user zooming, offset off / on.</summary>
    private static int _pullIns, _pullInsOffset;
    private static float _distAfter = float.NaN;
    private static float _fovWritten = float.NaN, _fovFirst = float.NaN;
    private static bool _live, _probeOk;

    private static readonly int OwnerOff = EngineLayout.BehaviorOwner;
    private static readonly int TargetOff = EngineLayout.BehaviorTargetObj;
    private static readonly int FocusOff = EngineLayout.FollowFocus;
    private static readonly int DistOff = EngineLayout.FollowDist;
    private static readonly int ZoomVelOff = EngineLayout.FollowZoomVel;
    private static readonly int YawOff = EngineLayout.FollowYaw;
    private static readonly int YawVelOff = EngineLayout.FollowYawVel;
    private static readonly int YawImpulseOff = EngineLayout.FollowYawImpulse;
    private static readonly int PitchOff = EngineLayout.FollowPitch;
    private static readonly int YawClampOff = EngineLayout.FollowYawClamp;
    private static readonly int PitchClampOff = EngineLayout.FollowPitchClamp;
    private static readonly int DistClampOff = EngineLayout.FollowDistClamp;
    private static readonly int YawMinOff = EngineLayout.FollowYawMin;
    private static readonly int PitchMinOff = EngineLayout.FollowPitchMin;
    private static readonly int DistMinOff = EngineLayout.FollowDistMin;
    private static readonly int CamPosOff = EngineLayout.CamPos;
    private static readonly int CamRotOff = EngineLayout.CamRot;
    private static readonly int CamFovOff = EngineLayout.CamViewAngle;
    private static readonly int ModCameraOff = EngineLayout.ModuleCamera;
    private static readonly int ModAttachedOff = EngineLayout.ModuleCameraAttached;
    private static readonly int ModClearViewOff = EngineLayout.ModuleFindClearView;
    private static readonly int ModDialogOff = EngineLayout.ModuleDialogCamera;
    private static readonly int ModCutsceneOff = EngineLayout.ModuleCutsceneRate;
    private static readonly int ModFlagsOff = EngineLayout.ModuleCameraFlags;
    private static readonly int ModChaseOffOff = EngineLayout.ModuleChaseSuppressed;
    private static readonly int ModWasIdleOff = EngineLayout.ModuleChaseWasIdle;
    private static readonly int ObjTypeOff = EngineLayout.ObjType;
    private static readonly int OriOff = EngineLayout.ObjOrientation;
    private static readonly int AnimOff = EngineLayout.ObjAnimation;
    private static readonly int InternalOff = EngineLayout.ExoAppInternal;
    private static readonly int PlayerIdOff = EngineLayout.InternalPlayerCreatureId;
    private static readonly int OptionsOff = EngineLayout.InternalClientOptions;
    private static readonly int ModeOff = EngineLayout.OptionsCameraMode;

    /// <summary>The Camera object sits 8 bytes before the CAurObject the behavior owns (<c>Camera::SetFollow</c>
    /// passes <c>this+8</c> on both builds).</summary>
    private const int CameraFromOwner = 8;

    private const float Deg = MathF.PI / 180f;
    /// <summary>Pitch limits when the behavior's own clamp is off: the full sphere short of the poles.</summary>
    private const float PitchFloor = 0.5f, PitchCeil = 179.5f;
    /// <summary>The recenter's ease: velocity = error * this, capped at the rate knob, so the swing slows into place
    /// instead of the engine's hard stop at 1°.</summary>
    private const float EaseGain = 3f;
    /// <summary>Where a recenter stops, degrees (the engine's own threshold).</summary>
    private const float Settled = 1f;
    private const byte TypeCreature = 5;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        CameraConfig.SelfCheck(log);
        SelfCheck(log);
        if (!EngineLayout.Require(log, "CameraService", "CAurBehavior", "CAurBehaviorCameraFollow", "CNWCModule",
                                  "CClientExoAppInternal", "CClientOptions", "Camera", "CNWCObject")) return;
        if (!SymbolMap.Require(log, "CameraService", SymbolMap.CameraFollowControl, SymbolMap.AppManager,
                               SymbolMap.GetModule, SymbolMap.GetGameObject)) return;

        _next = hooks.Chain(SymbolMap.CameraFollowControl,
                            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnControl, 10);
        if (_next == null) { log.Info("CameraService: standing down (Control chain refused)"); return; }

        if (GameGate.OwnsFramePresent)
            FrameRegistry.Global.Register(FramePhase.PostPresent, 50, OnFrameEnd, "CameraService", log);
        CameraCommand.Register("fov", "vertical FOV in degrees for the current camera mode (0 = engine's 45)", a => Knob("fov", a));
        CameraCommand.Register("lag", "`lag <pos> [rot] [zoom]` seconds of position / rotation / zoom lag (0 = welded; zoom smooths the wheel's stepped notches)", Lag);
        CameraCommand.Register("pitchcurve", "`pitchcurve <deg/unit> [ref]` pitch added per unit of distance past ref", PitchCurve);
        CameraCommand.Register("offset", "`offset <x> <y>` rig offset from the focus in world units, right and up (0 0 = the engine's orbit)", Offset);
        CameraCommand.Register("arm", "`arm margin|in|out|radius <n>` the offset camera's spring arm: stop short of a hit by margin, ease rates per second (in 0 = snap), ray fan half-width", Arm);
        CameraCommand.Register("recenter", "`recenter 0|1` auto-recenter behind the movement for the current mode; `recenter rate|deadzone|grace|idle|idlerate <n>` swing deg/s, start threshold deg, seconds after an orbit, seconds still before the soft recenter (0 = never), its deg/s", RecenterKnob);
        CameraCommand.Register("reset", "restore the engine feel for the current mode", _ => Reset());
        CameraCommand.Report(Report);
        KnobSurface.Note(() => $"camera feel (per mode: fov, lag, pitch curve, offset, drive, recenter; spring arm, drive.hz, recenter.*) in {_file.Path}");
        SceneRay.Init();
        log.Info("CameraService: chained on CAurBehaviorCameraFollow::Control");
    }

    // ---- the hook ---------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static int OnControl(IntPtr self, float dt)
    {
        // Read BEFORE the call: with no target the engine detaches (owner->SetBehavior(0)) and `self` is freed.
        IntPtr target = *(IntPtr*)(self + TargetOff);
        if (target != IntPtr.Zero && !float.IsNaN(_distAfter) && *(float*)(self + ZoomVelOff) == 0f
            && *(float*)(self + DistOff) < _distAfter - 1e-4f) { if (_offsetOn) _pullInsOffset++; else _pullIns++; }
        // Before the solve: the velocity is consumed by this call (the engine's chase writes it in the same slot).
        if (target != IntPtr.Zero && _probeOk && !FreeCameraService.Active) Recenter(self, dt);
        int r = ((delegate* unmanaged<IntPtr, float, int>)(*_next))(self, dt);
        _calls++;
        if (target == IntPtr.Zero) { _distAfter = float.NaN; return r; }
        _distAfter = *(float*)(self + DistOff);
        if (!_live)
        {
            // One-shot boot probe of the module rows (#536 AC: Windows offsets get a probe, not a hope).
            _live = true;
            _probeOk = EngineLayout.Probe("CNWCModule", Module(), _log);
            _log?.Info(_probeOk ? "CameraService: first Control -- hook is LIVE" : "CameraService: layout probe failed -- passing the engine's answer through");
        }
        if (_probeOk) { Adjust(self, dt); SettleCollision(Module()); }
        return r;
    }

    /// <summary>The one writer of <c>CAMERA_FLAG_DISABLE_COLLISION</c>: set while the spring arm (#538) or the
    /// occlusion fade (#537) wants the engine's pull-in off, cleared only if this session set it, never
    /// rewritten when it already holds -- the server's own bits stay untouched.</summary>
    public static void SettleCollision(IntPtr module)
    {
        if (module == IntPtr.Zero) return;
        int* flags = (int*)(module + ModFlagsOff);
        if (_wantNoCollision || AlloyRenderer.OccludeFadeWanted)
        {
            if ((*flags & EngineLayout.CameraFlagDisableCollision) == 0) { *flags |= EngineLayout.CameraFlagDisableCollision; _weSetFlag = true; }
        }
        else if (_weSetFlag) { *flags &= ~EngineLayout.CameraFlagDisableCollision; _weSetFlag = false; }
    }

    private static void Adjust(IntPtr self, float dt)
    {
        IntPtr cam = *(IntPtr*)(self + OwnerOff) - CameraFromOwner;
        Gate g = Check(self, cam, out int mode);
        _gate = g; _mode = mode;
        _yaw = *(float*)(self + YawOff); _pitch = *(float*)(self + PitchOff); _dist = *(float*)(self + DistOff);
        _fovRead = *(float*)(cam + CamFovOff);
        _pos = *(Vector3*)(cam + CamPosOff);
        if (FreeCameraService.Active)
        {
            // #543: the free camera owns the pose; the frame it lets go is a weld (snap + cut) like any gate's.
            FreeCameraService.Frame(cam, g, dt);
            if (FreeCameraService.Active) { _welded = true; _wantNoCollision = true; _offsetOn = false; _arm = 1f; _fovWritten = float.NaN; return; }
        }
        if (g != Gate.Active) { _welded = true; _offsetOn = _wantNoCollision = false; _arm = 1f; return; }

        // §9.2: anything rewriting the view angle between our writes shows up here.
        if (!float.IsNaN(_fovWritten) && _fovRead != _fovWritten) _fovRewrites++;
        if (float.IsNaN(_fovFirst)) _fovFirst = _fovRead;

        CameraMode m = _cfg.Modes[mode];
        Vector3 focus = *(Vector3*)(self + FocusOff);
        // A mode switch resets the engine's dist/pitch (+0x25c): that frame is a cut, not something to lag through.
        if (mode != _lastMode) { _lastMode = mode; _welded = true; }
        if (self != _lastSelf || _welded)
        {
            // A new behavior (SetFollow) or the frame after a gate: snap, and tell Alloy the history is stale.
            _lastSelf = self; _welded = false;
            _sYaw = _yaw; _sPitch = _pitch; _sDist = _dist; _sFocus = focus; _vYaw = _vPitch = _vDist = 0; _vFocus = default;
            AlloyRenderer.CameraCut();
        }
        _sFocus = m.LagPos > 0 ? SmoothDamp(_sFocus, focus, ref _vFocus, m.LagPos, dt) : focus;
        if (m.LagRot > 0)
        {
            _sYaw = SmoothDamp(_sYaw, _sYaw + Wrap(_yaw - _sYaw), ref _vYaw, m.LagRot, dt);
            _sPitch = SmoothDamp(_sPitch, _pitch, ref _vPitch, m.LagRot, dt);
        }
        else { _sYaw = _yaw; _sPitch = _pitch; }
        _sDist = m.LagZoom > 0 ? SmoothDamp(_sDist, _dist, ref _vDist, m.LagZoom, dt) : _dist;

        // The curve stays inside the behavior's own pitch range (settings.tml [camera.limits] lands there), not a
        // range of ours: a max past 90 is the player's to have.
        float lo = PitchFloor, hi = PitchCeil;
        if (*(float*)(self + PitchClampOff) != 0f) { lo = *(float*)(self + PitchMinOff); hi = *(float*)(self + PitchMinOff + 4); }
        float pitch = Math.Clamp(_sPitch + m.PitchCurve * (_sDist - m.PitchRef), lo, hi);
        _outPitch = pitch;
        Quaternion q = Pose(_sYaw, pitch);
        Vector3 pos = _sFocus + _sDist * Forward(q);
        _offsetOn = m.OffsetX != 0f || m.OffsetY != 0f;
        _wantNoCollision = _offsetOn;
        if (_offsetOn) pos = SpringArm(self, focus, pos + m.OffsetX * Right(q) + m.OffsetY * Vector3.UnitZ, dt);
        else _arm = 1f;
        // Camera+0xbc holds (w, x, y, z): Control's own position formula reads it in that order.
        float* rot = (float*)(cam + CamRotOff);
        rot[0] = q.W; rot[1] = q.X; rot[2] = q.Y; rot[3] = q.Z;
        *(Vector3*)(cam + CamPosOff) = pos;
        _pos = pos;
        if (m.Fov > 0) { *(float*)(cam + CamFovOff) = m.Fov; _fovWritten = m.Fov; }
        else _fovWritten = float.NaN;
    }

    /// <summary>#541: the auto-recenter, in place of the engine's chase (Nesky's rules). Runs before Control on the
    /// world camera's behavior: the engine's own chase is held down through the two module flags it honours
    /// (<see cref="EngineLayout.ModuleChaseSuppressed"/> up, its idle edge down), so its 30°-then-snap never fires,
    /// and the policy adds a yaw velocity in the slot it would have used. Moving (a locomotion animation on the
    /// player) eases the camera behind the creature's facing -- which is the movement direction for both
    /// click-to-move and #540's drive -- at most <c>recenter.rate</c> deg/s once the error leaves the dead-zone;
    /// standing still for <c>recenter.idle</c> seconds eases it there at <c>recenter.idlerate</c>. Any yaw the user
    /// put in this frame (an impulse or a velocity already in the behavior: mouse-look, middle-drag, the turn
    /// keys) suspends it and starts the grace clock; a drive with no forward component (strafe, back-pedal) holds it
    /// (a spiral is not a recenter, and a 180° swing has no side to take).
    /// Dialog, cutscene, locks and the rest are the same gates as the pose rewrite (<see cref="Check"/>, run here
    /// too so a gate that went up this frame is honoured before, not after, the solve).</summary>
    private static void Recenter(IntPtr self, float dt)
    {
        Gate g = Check(self, *(IntPtr*)(self + OwnerOff) - CameraFromOwner, out int mode);
        if (g != Gate.Active) { if (g != Gate.NotWorldCamera) { _pursuing = false; _recenterVel = 0f; } return; }
        CameraMode m = _cfg.Modes[mode];
        if (m.Recenter == 0f) { _hold = Hold.Off; _pursuing = false; return; }
        Recenter r = _cfg.Recenter;
        IntPtr module = Module();
        *(int*)(module + ModChaseOffOff) = 1;
        *(int*)(module + ModWasIdleOff) = 0;
        float* vel = (float*)(self + YawVelOff);
        if (*vel != 0f || *(float*)(self + YawImpulseOff) != 0f)
        {
            _grace = r.Grace; _idle = 0f; _pursuing = false; _recenterVel = 0f; _hold = Hold.Orbiting; return;
        }
        if (_grace > 0f) { _grace -= dt; _idle = 0f; _hold = Hold.Grace; return; }
        IntPtr cre = PlayerCreature();
        if (cre == IntPtr.Zero) { _hold = Hold.NoCreature; return; }
        float rate;
        if (Locomotion(*(short*)(cre + AnimOff)))
        {
            _idle = 0f;
            if (CameraDriveService.NoForward) { _pursuing = false; _hold = Hold.Lateral; return; }
            rate = r.Rate;
        }
        else
        {
            _idle += dt;
            if (r.Idle <= 0f || _idle < r.Idle) { _pursuing = false; _hold = Hold.Idle; return; }
            rate = r.IdleRate;
        }
        float* ori = (float*)(cre + OriOff);
        float delta = Wrap(FacingYaw(ori[0], ori[1]) - *(float*)(self + YawOff));
        float abs = MathF.Abs(delta);
        // Near 180 the short way flips sign frame to frame; once swinging, keep the side already taken.
        if (abs > TieZone && _recenterVel != 0f) delta = MathF.CopySign(abs, _recenterVel);
        if (abs <= Settled) { _pursuing = false; _recenterVel = 0f; _hold = Hold.Centered; return; }
        if (!_pursuing && abs < r.DeadZone && rate == r.Rate) { _hold = Hold.DeadZone; return; }
        _pursuing = true;
        _recenterVel = Math.Clamp(delta * EaseGain, -rate, rate);
        *vel = _recenterVel;
        _hold = Hold.Recentering;
    }

    /// <summary>The engine's <c>Yaw(Vector)</c> @0x5b6cc0: <c>atan2(-x, y)</c> in degrees, so a facing of +Y is
    /// yaw 0 -- the behavior's own convention (Camera::GetYaw reads +0x54 and the chase subtracts the two).</summary>
    private static float FacingYaw(float x, float y) => x == 0f && y == 0f ? 0f : MathF.Atan2(-x, y) / Deg;

    /// <summary>The chase drivers' locomotion set (walk / run / their variants), both builds.</summary>
    private static bool Locomotion(short anim) => anim is 2 or 3 or 4 or 0x4e or 0x4f or (>= 0x54 and <= 0x57);

    internal static IntPtr PlayerCreature()
    {
        IntPtr app = App();
        if (app == IntPtr.Zero) return IntPtr.Zero;
        IntPtr cre = _getGameObject(app, *(uint*)(*(IntPtr*)(app + InternalOff) + PlayerIdOff));
        return cre != IntPtr.Zero && *(byte*)(cre + ObjTypeOff) == TypeCreature ? cre : IntPtr.Zero;
    }

    private static Gate Check(IntPtr self, IntPtr cam, out int mode)
    {
        mode = -1;
        IntPtr module = Module();
        if (module == IntPtr.Zero) return Gate.NoModule;
        IntPtr intern = *(IntPtr*)(App() + InternalOff);
        IntPtr opts = *(IntPtr*)(intern + OptionsOff);
        if (opts != IntPtr.Zero) mode = *(byte*)(opts + ModeOff);   // before the gates: /camera needs it held down too
        if (*(IntPtr*)(module + ModCameraOff) != cam) return Gate.NotWorldCamera;
        if (*(byte*)(module + ModDialogOff) != 0) return Gate.Dialog;
        if (*(float*)(module + ModCutsceneOff) != 0f) return Gate.Cutscene;
        if (*(int*)(module + ModClearViewOff) != 0) return Gate.ClearView;
        if (*(uint*)(module + ModAttachedOff) != *(uint*)(intern + PlayerIdOff)) return Gate.Attached;
        // Clamps are ENABLED in normal play (the mode reset pushes [camera.limits] in); a lock is min == max.
        if (Locked(self, YawClampOff, YawMinOff) || Locked(self, PitchClampOff, PitchMinOff)
            || Locked(self, DistClampOff, DistMinOff)) return Gate.Locked;
        return (uint)mode < 3u ? Gate.Active : Gate.Mode;
    }

    private static bool Locked(IntPtr self, int clampOff, int minOff) =>
        *(float*)(self + clampOff) != 0f && *(float*)(self + minOff) >= *(float*)(self + minOff + 4);

    internal static IntPtr App()
    {
        IntPtr mgr = *(IntPtr*)_appMgr;
        return mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
    }

    /// <summary>#539: a gate the mouse-look must let go for -- dialog, cutscene, clear-view, attach, a server lock.
    /// ponytail: reads the last Control's verdict; a GUI 3D panel's camera (NotWorldCamera) is not a gate here
    /// because its Control interleaves with the world's and would flap the flag every frame.</summary>
    public static bool GateUp => _live && _probeOk && _gate is not (Gate.Active or Gate.NotWorldCamera);
    /// <summary>#543: the last Control's verdict by name, and whether the layer is live at all.</summary>
    internal static string GateName => GateNames[(int)_gate];
    internal static bool Live => _live && _probeOk;
    /// <summary>#543: the pose the layer last wrote (the rendered one: lag, curve, offset and arm included), the
    /// seed for a free camera that must not jump on entry.</summary>
    internal static void Rendered(out Vector3 pos, out float yaw, out float pitch, out float fov)
    { pos = _pos; yaw = _sYaw; pitch = _outPitch; fov = _fovRead; }

    /// <summary>#540: the live preferences and the mode byte the last Control saw (-1 before the first).</summary>
    internal static CameraConfig Config => _cfg;
    internal static int Mode => _mode;
    /// <summary>The preferences for editing: loaded from the file if not yet, and marked for the frame-end save.</summary>
    internal static CameraConfig Edit() { if (!_configLoaded) LoadConfig(); _dirty = true; return _cfg; }

    internal static IntPtr Module()
    {
        IntPtr app = App();
        return app == IntPtr.Zero ? IntPtr.Zero : _getModule(app);
    }

    // ---- the maths: YawPitchRoll(yaw, pitch, 0) = qZ(yaw) * qX(pitch), Hamilton, (w,x,y,z) --------------
    // Axes are the engine's static-init globals (yaw 0x19e7ce8 = +Z, pitch 0x19e7d08 = +X); operator* @0x5b5c30
    // is the Hamilton product. So yaw 0 / pitch p puts the camera at focus + dist*(0, -sin p, cos p): south
    // of the focus, looking north, pitch measured from vertical.

    internal static Quaternion Pose(float yawDeg, float pitchDeg)
    {
        (float sy, float cy) = MathF.SinCos(yawDeg * Deg * 0.5f);
        (float sp, float cp) = MathF.SinCos(pitchDeg * Deg * 0.5f);
        // qZ(yaw) = (cy, 0, 0, sy); qX(pitch) = (cp, sp, 0, 0); product expanded.
        return new Quaternion(cy * sp, sy * sp, sy * cp, cy * cp);
    }

    /// <summary>The unit the camera sits along from the focus: <c>q * +Z</c>, Control's own expansion.</summary>
    internal static Vector3 Forward(in Quaternion q) =>
        new(2f * (q.X * q.Z + q.W * q.Y), 2f * (q.Y * q.Z - q.W * q.X), 1f - 2f * (q.X * q.X + q.Y * q.Y));

    /// <summary>The camera's right: <c>q * +X</c>. Pitch is about X, so this stays horizontal at every pitch.</summary>
    internal static Vector3 Right(in Quaternion q) =>
        new(1f - 2f * (q.Y * q.Y + q.Z * q.Z), 2f * (q.X * q.Y + q.W * q.Z), 2f * (q.X * q.Z - q.W * q.Y));

    /// <summary>The offset floor: the arm never pulls the camera closer than this fraction of its rig.</summary>
    private const float ArmMin = 0.05f;
    private const int ArmRays = 5, ArmHops = 3;

    /// <summary>#538: keep the offset camera out of walls. The pivot is the player, not the offset focus (an
    /// offset can put the focus itself inside a wall); a fan of <see cref="ArmRays"/> parallel segments from
    /// there to <paramref name="want"/> is marched through <see cref="SceneRay"/>, creatures skipped, and the
    /// camera slides along the segment to the nearest hit minus the margin. Snaps (or eases, arm.in) in and
    /// eases (arm.out) back out. At most 15 casts, only while an offset is on.</summary>
    private static Vector3 SpringArm(IntPtr self, Vector3 pivot, Vector3 want, float dt)
    {
        SpringArm a = _cfg.Arm;
        Vector3 seg = want - pivot;
        float len = seg.Length();
        IntPtr scene = SceneRay.Armed ? SceneRay.WorldScene() : IntPtr.Zero;
        float target = 1f;
        if (scene != IntPtr.Zero && len > 0.05f)
        {
            Vector3 d = seg / len;
            Vector3 right = Vector3.Cross(d, Vector3.UnitZ);
            right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);
            Vector3 up = Vector3.Cross(right, d);
            IntPtr player = *(IntPtr*)(self + TargetOff);
            float nearest = len;
            for (int r = 0; r < ArmRays; r++)
            {
                Vector3 o = r switch { 1 => right * a.Radius, 2 => -right * a.Radius, 3 => up * a.Radius, 4 => -up * a.Radius, _ => Vector3.Zero };
                Vector3 from = pivot + o, to = want + o;
                for (int h = 0; h < ArmHops; h++)
                {
                    if (!SceneRay.Cast(scene, from, to, player, out Vector3 hit, out IntPtr gob)) break;
                    if (!SceneRay.IsCreature(gob)) { nearest = MathF.Min(nearest, Vector3.Dot(hit - (pivot + o), d)); break; }
                    from = hit + d * SceneRay.Nudge;
                    if (Vector3.Dot(to - from, d) <= 0f) break;
                }
            }
            SceneRay.Release();
            target = Math.Clamp((nearest - a.Margin) / len, ArmMin, 1f);
        }
        float rate = target < _arm ? a.RateIn : a.RateOut;
        _arm = rate <= 0f ? target : _arm + (target - _arm) * Math.Clamp(rate * Math.Min(dt, 0.1f), 0f, 1f);
        if (MathF.Abs(target - _arm) < 0.002f) _arm = target;
        return pivot + seg * _arm;
    }

    private static float Wrap(float deg) => deg - 360f * MathF.Round(deg / 360f);

    /// <summary>Critically damped spring toward <paramref name="target"/>; <paramref name="smooth"/> is the
    /// settle time in seconds. The standard Game Programming Gems 4 form.</summary>
    private static float SmoothDamp(float cur, float target, ref float vel, float smooth, float dt)
    {
        float omega = 2f / MathF.Max(smooth, 1e-4f);
        float x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float change = cur - target;
        float temp = (vel + omega * change) * dt;
        vel = (vel - omega * temp) * exp;
        return target + (change + temp) * exp;
    }

    private static Vector3 SmoothDamp(Vector3 cur, Vector3 target, ref Vector3 vel, float smooth, float dt) =>
        new(SmoothDamp(cur.X, target.X, ref vel.X, smooth, dt),
            SmoothDamp(cur.Y, target.Y, ref vel.Y, smooth, dt),
            SmoothDamp(cur.Z, target.Z, ref vel.Z, smooth, dt));

    // ---- frame end (cold-ish: one tick per present, strings only under logs=1) -------------------

    private static void OnFrameEnd()
    {
        if (!_configLoaded) LoadConfig();
        else if (_dirty) { _file.Save(_cfg.ToText(), _log); _dirty = false; }
        int c = _calls; _calls = 0;
        if (c < _callsMin) _callsMin = c;
        if (c > _callsMax) _callsMax = c;
        if (++_frames < 600 || !ExtenderLog.Enabled) return;
        // §9.3 / §9.2 under logs=1, once per 600 presents.
        _log?.Info(Inv($"[camera] Control {_callsMin}..{_callsMax} call(s)/present over {_frames} presents; ")
                 + Inv($"view angle first read {_fovFirst:0.##}, {_fovRewrites} rewrite(s) between ours; gate: {GateNames[(int)_gate]}; ")
                 + Inv($"engine pull-ins {_pullIns} (offset off) / {_pullInsOffset} (offset on), arm {_arm:0.00}"));
        _frames = 0; _callsMin = int.MaxValue; _callsMax = 0; _pullIns = _pullInsOffset = 0;
    }

    private static void LoadConfig()
    {
        if (!_file.Load(_log, t => _cfg = CameraConfig.Parse(t))) return;
        _configLoaded = true;
        _file.Baseline(_cfg.ToText());
    }

    // ---- /camera --------------------------------------------------------------------------------

    private static string Inv(FormattableString f) => FormattableString.Invariant(f);

    private static string Report()
    {
        string mode = _mode is >= 0 and < 3 ? CameraConfig.ModeNames[_mode] : "?";
        CameraMode? m = _mode is >= 0 and < 3 ? _cfg.Modes[_mode] : null;
        string knobs = m == null ? "" :
            Inv($"; {mode}: fov {(m.Fov > 0 ? m.Fov.ToString("0.#", CultureInfo.InvariantCulture) : "engine")} lag {m.LagPos:0.##}/{m.LagRot:0.##}/{m.LagZoom:0.##} pitchcurve {m.PitchCurve:0.##}@{m.PitchRef:0.#}")
          + Inv($" offset {m.OffsetX:0.##} {m.OffsetY:0.##}")
          + (_offsetOn ? Inv($" arm {_arm:0.00} (margin {_cfg.Arm.Margin:0.##} in {_cfg.Arm.RateIn:0.#} out {_cfg.Arm.RateOut:0.#} radius {_cfg.Arm.Radius:0.##})") : "");
        string recenter = m == null ? "" : m.Recenter == 0f ? "; recenter off" :
            Inv($"; recenter {HoldNames[(int)_hold]}{(_hold == Hold.Recentering ? Inv($" {_recenterVel:+0;-0} deg/s") : "")} (rate {_cfg.Recenter.Rate:0} deadzone {_cfg.Recenter.DeadZone:0} grace {_cfg.Recenter.Grace:0.#} idle {_cfg.Recenter.Idle:0.#}@{_cfg.Recenter.IdleRate:0})");
        string curve = _gate == Gate.Active && _outPitch != _pitch ? Inv($"->{_outPitch:0.#}") : "";
        string zoom = _gate == Gate.Active && MathF.Abs(_sDist - _dist) > 0.05f ? Inv($"->{_sDist:0.#}") : "";
        return Inv($"[camera] pos ({_pos.X:0.0}, {_pos.Y:0.0}, {_pos.Z:0.0}) yaw {_yaw:0.#} pitch {_pitch:0.#}{curve} dist {_dist:0.#}{zoom} fov {_fovRead:0.#}")
             + $" | {(_gate == Gate.Active ? "active" : "held down: " + GateNames[(int)_gate])}{knobs}{recenter}";
    }

    private static string Knob(string key, string args)
    {
        if (!_configLoaded) LoadConfig();
        if (_mode is < 0 or > 2) return "[camera] no live camera mode yet -- log in first";
        CameraMode m = _cfg.Modes[_mode];
        if (args.Length == 0 || !CameraConfig.TryF(args, out float v)) return Report();
        m.Set(key, v); _dirty = true;
        return Report();
    }

    private static string Lag(string args)
    {
        if (!_configLoaded) LoadConfig();
        if (_mode is < 0 or > 2) return "[camera] no live camera mode yet -- log in first";
        string[] p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0 || !CameraConfig.TryF(p[0], out float pos)) return Report();
        CameraMode m = _cfg.Modes[_mode];
        m.Set("lagpos", pos);
        if (p.Length > 1 && CameraConfig.TryF(p[1], out float rot)) m.Set("lagrot", rot);
        if (p.Length > 2 && CameraConfig.TryF(p[2], out float zoom)) m.Set("lagzoom", zoom);
        _dirty = true;
        return Report();
    }

    private static string PitchCurve(string args)
    {
        if (!_configLoaded) LoadConfig();
        if (_mode is < 0 or > 2) return "[camera] no live camera mode yet -- log in first";
        string[] p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0 || !CameraConfig.TryF(p[0], out float k)) return Report();
        CameraMode m = _cfg.Modes[_mode];
        m.Set("pitchcurve", k);
        if (p.Length > 1 && CameraConfig.TryF(p[1], out float r)) m.Set("pitchref", r);
        _dirty = true;
        return Report();
    }

    private static string Offset(string args)
    {
        if (!_configLoaded) LoadConfig();
        if (_mode is < 0 or > 2) return "[camera] no live camera mode yet -- log in first";
        string[] p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2 || !CameraConfig.TryF(p[0], out float x) || !CameraConfig.TryF(p[1], out float y)) return Report();
        CameraMode m = _cfg.Modes[_mode];
        m.Set("offsetx", x); m.Set("offsety", y); _dirty = true;
        return Report();
    }

    private static string Arm(string args)
    {
        if (!_configLoaded) LoadConfig();
        string[] p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2 || !CameraConfig.TryF(p[1], out float v) || !_cfg.Arm.Set(p[0], v)) return "[camera] arm margin|in|out|radius <n>";
        _dirty = true;
        return Report();
    }

    private static string RecenterKnob(string args)
    {
        if (!_configLoaded) LoadConfig();
        string[] p = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 1 && CameraConfig.TryF(p[0], out float on))
        {
            if (_mode is < 0 or > 2) return "[camera] no live camera mode yet -- log in first";
            _cfg.Modes[_mode].Set("recenter", on); _dirty = true;
            return Report();
        }
        if (p.Length < 2 || !CameraConfig.TryF(p[1], out float v) || !_cfg.Recenter.Set(p[0], v)) return "[camera] recenter 0|1, or recenter rate|deadzone|grace|idle|idlerate <n>";
        _dirty = true;
        return Report();
    }

    private static string Reset()
    {
        if (_mode is < 0 or > 2) return "[camera] no live camera mode yet -- log in first";
        _cfg.Modes[_mode] = new CameraMode(); _dirty = true;
        return Report();
    }

    /// <summary>The pose maths against Control's own arithmetic, off-device: the two things a wrong axis or a
    /// wrong product order would break -- where the camera sits for yaw 0, and that yaw turns about Z.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"CameraService.SelfCheck FAIL: {w}"); } }
        static bool Near(Vector3 a, Vector3 b) => (a - b).Length() < 1e-4f;
        Vector3 f0 = Forward(Pose(0, 0));
        E(Near(f0, new Vector3(0, 0, 1)), "pitch 0 is straight above the focus (top-down)");
        Vector3 f90 = Forward(Pose(0, 90));
        E(Near(f90, new Vector3(0, -1, 0)), "yaw 0 pitch 90 sits due south, looking north (+Y)");
        Vector3 fy = Forward(Pose(90, 90));
        E(Near(fy, new Vector3(1, 0, 0)) || Near(fy, new Vector3(-1, 0, 0)), "yaw turns about Z");
        E(MathF.Abs(Pose(30, 45).Length() - 1f) < 1e-5f, "the pose is a unit quaternion");
        E(Near(Right(Pose(0, 90)), new Vector3(1, 0, 0)) && Near(Right(Pose(0, 30)), new Vector3(1, 0, 0)), "right is +X looking north at any pitch");
        E(Near(Right(Pose(90, 60)), new Vector3(0, 1, 0)) || Near(Right(Pose(90, 60)), new Vector3(0, -1, 0)), "right turns with yaw");
        E(MathF.Abs(Wrap(350f - 10f) + 20f) < 1e-5f, "yaw lag takes the short way round");
        E(FacingYaw(0, 1) == 0f && MathF.Abs(FacingYaw(1, 0) + 90f) < 1e-4f && MathF.Abs(FacingYaw(-1, 0) - 90f) < 1e-4f,
          "facing north is yaw 0, east -90, west +90 (the engine's Yaw(Vector))");
        E(Locomotion(2) && Locomotion(0x57) && !Locomotion(0) && !Locomotion(0x58), "the locomotion set");
        E(Math.Clamp(120f * EaseGain, -60f, 60f) == 60f && MathF.Abs(Math.Clamp(5f * EaseGain, -60f, 60f) - 15f) < 1e-5f, "the ease caps at the rate and slows near the target");
        float v = 0, x = 0;
        for (int i = 0; i < 240; i++) x = SmoothDamp(x, 1f, ref v, 0.2f, 1f / 60f);
        E(MathF.Abs(x - 1f) < 1e-3f, "the spring settles on its target");
        log.Info(ok ? "CameraService.SelfCheck: passed" : "CameraService.SelfCheck: FAILED");
    }
}
