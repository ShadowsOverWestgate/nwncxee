using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy;
using CX.Engine;
using CX.Framework;
using EngCamera = CX.Engine.Camera;

namespace CX.Services.Camera;

/// <summary>
/// The camera layer (#536; docs/research/custom-camera.md §8 option A). Chains on the engine's one camera
/// solve, <c>CAurBehaviorCameraFollow::Control</c>, lets it run, then rewrites its answer -- the pose at
/// Camera+0xb0/+0xbc and the view angle at +0x4c4 -- from the same behavior state plus three feels the engine
/// lacks: an FOV knob, a critically damped position/rotation/zoom lag (the engine's zoom is a one-frame
/// impulse per wheel notch, so the rendered distance is damped toward the behavior's), and pitch as a function
/// of distance. A free lateral/vertical offset of the rig from the focus has its own spring arm; the engine's
/// pull-in predicts from the un-offset pose, so it is switched off through <see cref="SettleCollision"/> while
/// an offset is active, and at 0 0 the engine's collision is left alone (#538). The behavior's own
/// yaw/pitch/dist are never written, so dialog framing, cutscenes, Store/RestoreCameraFacing and the server's
/// locks keep reading the truth. The one exception goes through the engine's own input channel: the
/// auto-recenter (<see cref="Recenter"/>) adds a yaw <i>velocity</i> before Control, the same field the
/// engine's chase drivers write, with those drivers held down by the two module flags they honour (#541).
/// <para><b>Stand-down gates</b>, checked every call and reported under <c>/knobs</c>: not the module camera
/// (a GUI panel's), dialog camera, cutscene interpolation, clear-view pending, attached to something other
/// than the player, any server lock (photo mode locks all three axes), unknown mode byte, and the mode's own
/// master <c>&lt;mode&gt;.enabled</c> off -- the stock camera for that mode: feel, recenter and the drive all
/// stand aside, and the view angle goes back to the first one read (the engine has no restorer for it). When
/// a gate is up the engine's answer passes through untouched, and the frame after it lifts is a cut
/// (<see cref="AlloyRenderer.CameraCut"/>).</para>
/// <para><b>Hot.</b> Runs inside <c>CNWCArea::Update</c> once per world frame: pointer reads, float math, no
/// allocation. The knobs are plain field reads (ADR 0025); every string lives on the frame-end tick and the
/// <c>/knobs</c> note.</para>
/// </summary>
public sealed unsafe class CameraService : IExtenderService
{
    private static ExtenderLog? _log;
    private static CameraFollowControlFn* _next;
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    /// <summary>Resolved once: <see cref="Module"/> and <see cref="PlayerCreature"/> are read from inside the
    /// camera solve, so neither takes the lazy resolve's compare per call.</summary>
    private static readonly GetModuleFn _getModule = EngineFn.GetModule;
    /// <inheritdoc cref="_getModule"/>
    private static readonly GetGameObjectFn _getGameObject = EngineFn.GetGameObject;

    /// <summary>Why the layer is (or is not) touching the pose this frame.</summary>
    internal enum Gate { Active, NoModule, NotWorldCamera, Dialog, Cutscene, ClearView, Attached, Locked, Mode, Off }

    internal static readonly string[] GateNames =
        { "active", "no module", "not the world camera", "dialog camera", "cutscene", "clear-view pending",
          "attached elsewhere", "server lock", "unknown camera mode", "CX camera off for this mode" };

    // ---- last solve, for the /knobs note (written hot, read cold) ----
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
    /// <summary>Why the recenter did nothing on the last call, for the /knobs note.</summary>
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
        CameraKnobs.SelfCheck(log);
        SelfCheck(log);
        if (!EngineLayout.Require(log, "CameraService", "CAurBehavior", "CAurBehaviorCameraFollow", "CNWCModule",
                                  "CClientExoAppInternal", "CClientOptions", "Camera", "CNWCObject")) return;
        if (!SymbolMap.Require(log, "CameraService", SymbolMap.CameraFollowControl, SymbolMap.AppManager,
                               SymbolMap.GetModule, SymbolMap.GetGameObject)) return;

        _next = hooks.ChainCameraFollowControl(&OnControl, 10);
        if (_next == null) { log.Info("CameraService: standing down (Control chain refused)"); return; }

        FrameRegistry.Global.Register(FramePhase.PostPresent, 50, OnFrameEnd, "CameraService", log);
        KnobSurface.Note(Report);
        SceneRay.Init();
        log.Info("CameraService: chained on CAurBehaviorCameraFollow::Control");
    }

    // ---- the hook ---------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static int OnControl(IntPtr self, float dt)
    {
        var b = new CAurBehaviorCameraFollow(self);
        // Read BEFORE the call: with no target the engine detaches (owner->SetBehavior(0)) and `self` is freed.
        IntPtr target = b.Target;
        if (target != IntPtr.Zero && !float.IsNaN(_distAfter) && b.ZoomVel == 0f
            && b.Dist < _distAfter - 1e-4f) { if (_offsetOn) _pullInsOffset++; else _pullIns++; }
        // Before the solve: the velocity is consumed by this call (the engine's chase writes it in the same slot).
        if (target != IntPtr.Zero && _probeOk && !FreeCameraService.Active) Recenter(self, dt);
        int r = (*_next)(self, dt);
        _calls++;
        if (target == IntPtr.Zero) { _distAfter = float.NaN; return r; }
        _distAfter = b.Dist;
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
        var mod = new CNWCModule(module);
        ref int flags = ref mod.CameraFlags;
        if (_wantNoCollision || AlloyRenderer.OccludeFadeWanted)
        {
            if ((flags & EngineLayout.CameraFlagDisableCollision) == 0) { flags |= EngineLayout.CameraFlagDisableCollision; _weSetFlag = true; }
        }
        else if (_weSetFlag) { flags &= ~EngineLayout.CameraFlagDisableCollision; _weSetFlag = false; }
    }

    private static void Adjust(IntPtr self, float dt)
    {
        var b = new CAurBehaviorCameraFollow(self);
        IntPtr cam = new CAurBehavior(self).Owner - CameraFromOwner;
        Gate g = Check(self, cam, out int mode);
        _gate = g; _mode = mode;
        _yaw = b.Yaw; _pitch = b.Pitch; _dist = b.Dist;
        var c = new EngCamera(cam);
        _fovRead = c.ViewAngle;
        _pos = c.Pos;
        if (FreeCameraService.Active)
        {
            // #543: the free camera owns the pose; the frame it lets go is a weld (snap + cut) like any gate's.
            // _fovWritten is kept: Leave() puts the entry angle back, which is the layer's last write or nothing.
            FreeCameraService.Frame(cam, g, dt);
            if (FreeCameraService.Active) { _welded = true; _wantNoCollision = true; _offsetOn = false; _arm = 1f; return; }
        }
        if (g != Gate.Active)
        {
            // Off is enterable by the free camera: the seed it reads (Rendered) must be the engine's pose, not a stale lag.
            if (g == Gate.Off) { RestoreFov(cam); _sYaw = _yaw; _outPitch = _pitch; }
            _welded = true; _offsetOn = _wantNoCollision = false; _arm = 1f; return;
        }

        // §9.2: anything rewriting the view angle between our writes shows up here.
        if (!float.IsNaN(_fovWritten) && _fovRead != _fovWritten) _fovRewrites++;
        if (float.IsNaN(_fovFirst)) _fovFirst = _fovRead;

        CameraModeKnobs m = CameraModes.ByMode[mode];
        Vector3 focus = b.Focus;
        // A mode switch resets the engine's dist/pitch (+0x25c): that frame is a cut, not something to lag through.
        if (mode != _lastMode) { _lastMode = mode; _welded = true; }
        if (self != _lastSelf || _welded)
        {
            // A new behavior (SetFollow) or the frame after a gate: snap, and tell Alloy the history is stale.
            _lastSelf = self; _welded = false;
            _sYaw = _yaw; _sPitch = _pitch; _sDist = _dist; _sFocus = focus; _vYaw = _vPitch = _vDist = 0; _vFocus = default;
            AlloyRenderer.CameraCut();
        }
        float lagPos = m.LagPos.V, lagRot = m.LagRot.V, lagZoom = m.LagZoom.V;
        _sFocus = lagPos > 0 ? SmoothDamp(_sFocus, focus, ref _vFocus, lagPos, dt) : focus;
        if (lagRot > 0)
        {
            _sYaw = SmoothDamp(_sYaw, _sYaw + Wrap(_yaw - _sYaw), ref _vYaw, lagRot, dt);
            _sPitch = SmoothDamp(_sPitch, _pitch, ref _vPitch, lagRot, dt);
        }
        else { _sYaw = _yaw; _sPitch = _pitch; }
        _sDist = lagZoom > 0 ? SmoothDamp(_sDist, _dist, ref _vDist, lagZoom, dt) : _dist;

        // The curve stays inside the behavior's own pitch range (settings.tml [camera.limits] lands there), not a
        // range of ours: a max past 90 is the player's to have.
        float lo = PitchFloor, hi = PitchCeil;
        if (b.PitchClamp != 0f) { lo = b.PitchMin[0]; hi = b.PitchMin[1]; }
        float pitch = Math.Clamp(_sPitch + m.PitchCurve.V * (_sDist - m.PitchRef.V), lo, hi);
        _outPitch = pitch;
        Quaternion q = Pose(_sYaw, pitch);
        Vector3 pos = _sFocus + _sDist * Forward(q);
        float offX = m.OffsetX.V, offY = m.OffsetY.V;
        _offsetOn = offX != 0f || offY != 0f;
        _wantNoCollision = _offsetOn;
        if (_offsetOn) pos = SpringArm(self, focus, pos + offX * Right(q) + offY * Vector3.UnitZ, dt);
        else _arm = 1f;
        c.Rot = q;
        c.Pos = pos;
        _pos = pos;
        // No magic zero (ADR 0025): the slider is written whenever the layer drives the mode.
        float fov = m.Fov.V;
        c.ViewAngle = fov; _fovWritten = fov;
    }

    /// <summary>#584: the mode's master went off, or the mode changed into one whose master is off, with the layer's
    /// view angle still in the camera. <c>Camera+0x4c4</c> has no engine restorer, so the first angle read goes back,
    /// once, and the frame is a cut. NaN = the layer never wrote it (nothing to restore); the on edge needs nothing
    /// here, <see cref="Adjust"/> writes the slider again on its first Active frame.</summary>
    private static void RestoreFov(IntPtr cam)
    {
        if (float.IsNaN(_fovWritten)) return;
        new EngCamera(cam).ViewAngle = _fovFirst;
        _fovWritten = float.NaN;
        AlloyRenderer.CameraCut();
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
    /// too so a gate that went up this frame is honoured before, not after, the solve); under the mode's master off
    /// the chase flags are not held either, so the engine's own chase runs (#584).</summary>
    private static void Recenter(IntPtr self, float dt)
    {
        var b = new CAurBehaviorCameraFollow(self);
        Gate g = Check(self, new CAurBehavior(self).Owner - CameraFromOwner, out int mode);
        if (g != Gate.Active)
        {
            if (g != Gate.NotWorldCamera) { _pursuing = false; _recenterVel = 0f; }
            if (g == Gate.Off) _hold = Hold.Off;
            return;
        }
        CameraModeKnobs m = CameraModes.ByMode[mode];
        bool aim = TabTargetService.AimPoint(out float ax, out float ay, out float az);
        if (!aim) _aimBase = float.NaN;
        if (!aim && !m.Recenter.V) { _hold = Hold.Off; _pursuing = false; return; }
        var mod = new CNWCModule(Module());
        mod.ChaseSuppressed = 1;
        mod.ChaseWasIdle = 0;
        ref float vel = ref b.YawVel;
        // Face mode: the turn keys are a keyboard orbit (yaw + is left, facing north = 0, east = -90).
        if (CameraDriveService.TurnAxis != 0) vel += CameraDriveService.TurnAxis * m.KeyTurn.V;
        bool orbiting = vel != 0f || b.YawImpulse != 0f;
        if (aim)
        {
            // The tab-target's aim (CX-636): the camera looks from behind the player toward the target, fast and
            // with no dead zone, grace, movement, idle or orbit gate -- the hand's impulse still adds, the aim's
            // velocity pulls the view back. The lock key (or a cleared lock) is what ends a lock-on.
            IntPtr me = PlayerCreature();
            if (me == IntPtr.Zero) { _hold = Hold.NoCreature; return; }
            Vector3 pp = new CNWCObject(me).Position;
            float dx = ax - pp.X, dy = ay - pp.Y;
            Pursue(self, FacingYaw(dx, dy), AimRate, 0f, AimGain, out bool settled);
            if (float.IsNaN(_aimBase)) _aimBase = b.Pitch;
            _aimBase += b.PitchImpulse;
            bool tilted = PursuePitch(self, AimPitch(self, MathF.Sqrt(dx * dx + dy * dy), az - pp.Z, TabTarget.Tilt.V, ref _aimBase));
            if (settled && tilted) TabTargetService.AimSettled();
            return;
        }
        if (orbiting)
        {
            _grace = m.Grace.V; _idle = 0f; _pursuing = false; _recenterVel = 0f; _hold = Hold.Orbiting; return;
        }
        if (_grace > 0f) { _grace -= dt; _idle = 0f; _hold = Hold.Grace; return; }
        IntPtr cre = PlayerCreature();
        if (cre == IntPtr.Zero) { _hold = Hold.NoCreature; return; }
        float rate;
        if (Locomotion(new CNWCObject(cre).Animation))
        {
            _idle = 0f;
            if (CameraDriveService.NoForward) { _pursuing = false; _hold = Hold.Lateral; return; }
            rate = m.Rate.V;
        }
        else
        {
            _idle += dt;
            float idle = m.Idle.V;
            if (idle <= 0f || _idle < idle) { _pursuing = false; _hold = Hold.Idle; return; }
            rate = m.IdleRate.V;
        }
        Vector3 ori = new CNWCObject(cre).Orientation;
        Pursue(self, FacingYaw(ori.X, ori.Y), rate, rate == m.Rate.V ? m.DeadZone.V : 0f, EaseGain, out _);
    }

    /// <summary>The tab-target aim's swing: twice the default recenter ceiling and a stiffer ease, so a 90°
    /// turn lands in well under half a second -- a look, not a drift.</summary>
    private const float AimRate = 720f, AimGain = 8f;

    /// <summary>The aim's pitch: the player's own tilt (<paramref name="basePitch"/>, the pitch the aim found plus
    /// every hand tilt since) unless it puts the target more than <paramref name="band"/> degrees off the middle of
    /// the view -- then the nearest pitch that holds it on the band's edge: flatter for a far or raised target,
    /// steeper for one below. The camera sits <c>dist</c> behind the focus at <c>(dist sin p, dist cos p)</c> (pitch
    /// from vertical) looking at the focus, so a target <paramref name="r"/> out and <paramref name="h"/> up sits
    /// <c>atan2(h - dist cos p, r + dist sin p) - (p - 90)</c> above the centre: falling in p, solved by bisection
    /// inside the behavior's own clamp range (the engine clamps the same way after Control).</summary>
    private static float AimPitch(IntPtr self, float r, float h, float band, ref float basePitch)
    {
        var b = new CAurBehaviorCameraFollow(self);
        float d = b.Dist;
        float lo = PitchFloor, hi = PitchCeil;
        if (b.PitchClamp != 0f) { lo = b.PitchMin[0]; hi = b.PitchMin[1]; }
        basePitch = Math.Clamp(basePitch, lo, hi);
        float e = Elevation(basePitch);
        if (MathF.Abs(e) <= band) return basePitch;
        float want = e > 0f ? band : -band;
        if (e > 0f) lo = basePitch; else hi = basePitch;
        if (Elevation(lo) <= want) return lo;
        if (Elevation(hi) >= want) return hi;
        for (int i = 0; i < 20; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (Elevation(mid) > want) lo = mid; else hi = mid;
        }
        return 0.5f * (lo + hi);

        float Elevation(float p)
        {
            (float sn, float cs) = MathF.SinCos(p * Deg);
            return MathF.Atan2(h - d * cs, r + d * sn) / Deg - (p - 90f);
        }
    }

    /// <summary>The tilt the aim holds the camera around: the behavior's pitch when the aim began, moved by every
    /// hand tilt (the pitch impulse) since; NaN while no aim runs.</summary>
    private static float _aimBase = float.NaN;

    /// <summary>The aim's pitch leg: the same ease as the yaw's into the pitch velocity slot; true once within
    /// <see cref="Settled"/>. No tie-break: pitch never wraps.</summary>
    private static bool PursuePitch(IntPtr self, float targetPitch)
    {
        var b = new CAurBehaviorCameraFollow(self);
        float delta = targetPitch - b.Pitch;
        if (MathF.Abs(delta) <= Settled) return true;
        b.PitchVel = Math.Clamp(delta * AimGain, -AimRate, AimRate);
        return false;
    }

    /// <summary>Ease the behavior's yaw toward <paramref name="targetYaw"/>: <paramref name="gain"/> x the error as
    /// the velocity into the yaw slot, clamped to <paramref name="rate"/>, held inside <paramref name="deadZone"/>
    /// until a pursuit is under way.</summary>
    private static void Pursue(IntPtr self, float targetYaw, float rate, float deadZone, float gain, out bool settled)
    {
        settled = false;
        var b = new CAurBehaviorCameraFollow(self);
        float delta = Wrap(targetYaw - b.Yaw);
        float abs = MathF.Abs(delta);
        // Near 180 the short way flips sign frame to frame; once swinging, keep the side already taken.
        if (abs > TieZone && _recenterVel != 0f) delta = MathF.CopySign(abs, _recenterVel);
        if (abs <= Settled) { _pursuing = false; _recenterVel = 0f; _hold = Hold.Centered; settled = true; return; }
        if (!_pursuing && abs < deadZone) { _hold = Hold.DeadZone; return; }
        _pursuing = true;
        _recenterVel = Math.Clamp(delta * gain, -rate, rate);
        b.YawVel = _recenterVel;
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
        IntPtr cre = _getGameObject(app, new CClientExoApp(app).Internal.PlayerCreatureId);
        return cre != IntPtr.Zero && new CNWCObject(cre).Type == TypeCreature ? cre : IntPtr.Zero;
    }

    private static Gate Check(IntPtr self, IntPtr cam, out int mode)
    {
        mode = -1;
        IntPtr module = Module();
        if (module == IntPtr.Zero) return Gate.NoModule;
        var mod = new CNWCModule(module);
        CClientExoAppInternal intern = new CClientExoApp(App()).Internal;
        CClientOptions opts = intern.ClientOptions;
        if (!opts.IsNull) mode = opts.CameraMode;   // before the gates: the drive needs it held down too
        if (mod.Camera.Ptr != cam) return Gate.NotWorldCamera;
        if (mod.DialogCamera != 0) return Gate.Dialog;
        if (mod.CutsceneRate != 0f) return Gate.Cutscene;
        if (mod.FindClearView != 0) return Gate.ClearView;
        if (mod.CameraAttached != intern.PlayerCreatureId) return Gate.Attached;
        // Clamps are ENABLED in normal play (the mode reset pushes [camera.limits] in); a lock is min == max.
        var b = new CAurBehaviorCameraFollow(self);
        if (Locked(b.YawClamp, b.YawMin) || Locked(b.PitchClamp, b.PitchMin) || Locked(b.DistClamp, b.DistMin)) return Gate.Locked;
        if ((uint)mode >= 3u) return Gate.Mode;
        return CameraModes.ByMode[mode].Enabled.V ? Gate.Active : Gate.Off;
    }

    /// <summary>A locked axis: the clamp is on and its range (min, max) has collapsed.</summary>
    private static bool Locked(float clamp, Span<float> range) => clamp != 0f && range[0] >= range[1];

    internal static IntPtr App()
    {
        IntPtr mgr = *(IntPtr*)_appMgr;
        return mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
    }

    /// <summary>#539: a gate the mouse-look must let go for -- dialog, cutscene, clear-view, attach, a server lock.
    /// ponytail: reads the last Control's verdict; a GUI 3D panel's camera (NotWorldCamera) is not a gate here
    /// because its Control interleaves with the world's and would flap the flag every frame, and a mode's master
    /// off (<see cref="Gate.Off"/>) is not one either: mouse-look and the free camera have masters of their own.</summary>
    public static bool GateUp => _live && _probeOk && _gate is not (Gate.Active or Gate.NotWorldCamera or Gate.Off);
    /// <summary>#543: the last Control's verdict by name, and whether the layer is live at all.</summary>
    internal static string GateName => GateNames[(int)_gate];
    internal static bool Live => _live && _probeOk;
    /// <summary>#543: the pose the layer last wrote (the rendered one: lag, curve, offset and arm included), the
    /// seed for a free camera that must not jump on entry.</summary>
    internal static void Rendered(out Vector3 pos, out float yaw, out float pitch, out float fov)
    { pos = _pos; yaw = _sYaw; pitch = _outPitch; fov = _fovRead; }

    /// <summary>#540: the mode byte the last Control saw (-1 before the first).</summary>
    internal static int Mode => _mode;
    /// <summary>The spring arm's current fraction of the offset rig, for the <c>/walls</c> status line.</summary>
    internal static float ArmFraction => _arm;

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
    /// camera slides along the segment to the nearest hit minus the margin. Snaps (or eases, walls.armin) in
    /// and eases (walls.armout) back out. At most 15 casts, only while an offset is on.</summary>
    private static Vector3 SpringArm(IntPtr self, Vector3 pivot, Vector3 want, float dt)
    {
        float radius = Walls.ArmRadius.V;
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
            IntPtr player = new CAurBehaviorCameraFollow(self).Target;
            float nearest = len;
            for (int r = 0; r < ArmRays; r++)
            {
                Vector3 o = r switch { 1 => right * radius, 2 => -right * radius, 3 => up * radius, 4 => -up * radius, _ => Vector3.Zero };
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
            target = Math.Clamp((nearest - Walls.Margin.V) / len, ArmMin, 1f);
        }
        float rate = target < _arm ? Walls.ArmIn.V : Walls.ArmOut.V;
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

    // ---- the /knobs note (cold) ----------------------------------------------------------------

    private static string Inv(FormattableString f) => FormattableString.Invariant(f);

    /// <summary>The layer's last solve: pose, gate, the live mode's knobs and the recenter's hold reason.</summary>
    private static string Report()
    {
        CameraModeKnobs? m = _mode is >= 0 and < 3 ? CameraModes.ByMode[_mode] : null;
        string knobs = m == null ? "" :
            Inv($"; {m.G.Name}: fov {m.Fov.V:0.#} lag {m.LagPos.V:0.##}/{m.LagRot.V:0.##}/{m.LagZoom.V:0.##} pitchcurve {m.PitchCurve.V:0.##}@{m.PitchRef.V:0.#}")
          + Inv($" offset {m.OffsetX.V:0.##} {m.OffsetY.V:0.##}")
          + (_offsetOn ? Inv($" arm {_arm:0.00} (margin {Walls.Margin.V:0.##} in {Walls.ArmIn.V:0.#} out {Walls.ArmOut.V:0.#} radius {Walls.ArmRadius.V:0.##})") : "");
        string recenter = m == null ? "" : !m.Recenter.V ? "; recenter off" :
            Inv($"; recenter {HoldNames[(int)_hold]}{(_hold == Hold.Recentering ? Inv($" {_recenterVel:+0;-0} deg/s") : "")} (rate {m.Rate.V:0} deadzone {m.DeadZone.V:0} grace {m.Grace.V:0.#} idle {m.Idle.V:0.#}@{m.IdleRate.V:0})");
        string curve = _gate == Gate.Active && _outPitch != _pitch ? Inv($"->{_outPitch:0.#}") : "";
        string zoom = _gate == Gate.Active && MathF.Abs(_sDist - _dist) > 0.05f ? Inv($"->{_sDist:0.#}") : "";
        return Inv($"camera: pos ({_pos.X:0.0}, {_pos.Y:0.0}, {_pos.Z:0.0}) yaw {_yaw:0.#} pitch {_pitch:0.#}{curve} dist {_dist:0.#}{zoom} fov {_fovRead:0.#}")
             + $" | {(_gate == Gate.Active ? "active" : "held down: " + GateNames[(int)_gate])}{knobs}{recenter}";
    }

    /// <summary>The pose maths against Control's own arithmetic, off-device: the two things a wrong axis or a
    /// wrong product order would break -- where the camera sits for yaw 0, and that yaw turns about Z.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        static bool Near(Vector3 a, Vector3 b) => (a - b).Length() < 1e-4f;
        Vector3 f0 = Forward(Pose(0, 0));
        check.Expect(Near(f0, new Vector3(0, 0, 1)), "pitch 0 is straight above the focus (top-down)");
        Vector3 f90 = Forward(Pose(0, 90));
        check.Expect(Near(f90, new Vector3(0, -1, 0)), "yaw 0 pitch 90 sits due south, looking north (+Y)");
        Vector3 fy = Forward(Pose(90, 90));
        check.Expect(Near(fy, new Vector3(1, 0, 0)) || Near(fy, new Vector3(-1, 0, 0)), "yaw turns about Z");
        check.Expect(MathF.Abs(Pose(30, 45).Length() - 1f) < 1e-5f, "the pose is a unit quaternion");
        check.Expect(Near(Right(Pose(0, 90)), new Vector3(1, 0, 0)) && Near(Right(Pose(0, 30)), new Vector3(1, 0, 0)), "right is +X looking north at any pitch");
        check.Expect(Near(Right(Pose(90, 60)), new Vector3(0, 1, 0)) || Near(Right(Pose(90, 60)), new Vector3(0, -1, 0)), "right turns with yaw");
        check.Expect(MathF.Abs(Wrap(350f - 10f) + 20f) < 1e-5f, "yaw lag takes the short way round");
        check.Expect(FacingYaw(0, 1) == 0f && MathF.Abs(FacingYaw(1, 0) + 90f) < 1e-4f && MathF.Abs(FacingYaw(-1, 0) - 90f) < 1e-4f,
          "facing north is yaw 0, east -90, west +90 (the engine's Yaw(Vector))");
        check.Expect(Locomotion(2) && Locomotion(0x57) && !Locomotion(0) && !Locomotion(0x58), "the locomotion set");
        check.Expect((int)Gate.Off == 9 && GateNames.Length == 10 && GateNames[(int)Gate.Off].Contains("off"), "the mode master is the last gate and has its name");
        check.Expect(Math.Clamp(120f * EaseGain, -60f, 60f) == 60f && MathF.Abs(Math.Clamp(5f * EaseGain, -60f, 60f) - 15f) < 1e-5f, "the ease caps at the rate and slows near the target");
        float v = 0, x = 0;
        for (int i = 0; i < 240; i++) x = SmoothDamp(x, 1f, ref v, 0.2f, 1f / 60f);
        check.Expect(MathF.Abs(x - 1f) < 1e-3f, "the spring settles on its target");
        log.Info(check.Ok ? "CameraService.SelfCheck: passed" : "CameraService.SelfCheck: FAILED");
    }
}
