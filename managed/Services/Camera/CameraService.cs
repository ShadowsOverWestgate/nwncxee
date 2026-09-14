using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Alloy;
using Sowx.Framework;

namespace Sowx.Services.Camera;

/// <summary>
/// The camera layer (#536, map #535, ADR: docs/research/custom-camera.md §8 option A). Chains on the engine's
/// one camera solve, <c>CAurBehaviorCameraFollow::Control</c>, lets it run, then rewrites its answer -- the
/// pose at Camera+0xb0/+0xbc and the view angle at +0x4c4 -- from the same behavior state plus three feels the
/// engine lacks: an FOV knob, a critically damped position/rotation lag, and pitch as a function of distance.
/// The behavior's own yaw/pitch/dist are never written, so dialog framing, cutscenes, Store/RestoreCameraFacing
/// and the server's locks keep reading the truth.
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

    private static CameraConfig _cfg = new();
    private static readonly ConfigFile _file = new("sowx-camera.cfg");
    private static bool _configLoaded, _dirty;

    /// <summary>Why the layer is (or is not) touching the pose this frame.</summary>
    private enum Gate { Active, NoModule, NotWorldCamera, Dialog, Cutscene, ClearView, Attached, Locked, Mode }

    private static readonly string[] GateNames =
        { "active", "no module", "not the world camera", "dialog camera", "cutscene", "clear-view pending",
          "attached elsewhere", "server lock", "unknown camera mode" };

    // ---- last solve, for /camera (written hot, read cold) ----
    private static Gate _gate = Gate.NoModule;
    private static int _mode = -1;
    private static float _yaw, _pitch, _dist, _fovRead, _outPitch;
    private static Vector3 _pos;

    // ---- lag state, keyed by the behavior object: SetFollow replaces it on every attach / transition ----
    private static IntPtr _lastSelf;
    private static int _lastMode = -1;
    private static bool _welded = true;
    private static float _sYaw, _sPitch, _vYaw, _vPitch;
    private static Vector3 _sFocus, _vFocus;

    // ---- §9 instruments (logs=1): Control cadence per present, runtime FOV writers ----
    private static int _calls, _callsMin = int.MaxValue, _callsMax, _frames, _fovRewrites;
    private static float _fovWritten = float.NaN, _fovFirst = float.NaN;
    private static bool _live, _probeOk;

    private static readonly int OwnerOff = EngineLayout.BehaviorOwner;
    private static readonly int TargetOff = EngineLayout.BehaviorTargetObj;
    private static readonly int FocusOff = EngineLayout.FollowFocus;
    private static readonly int DistOff = EngineLayout.FollowDist;
    private static readonly int YawOff = EngineLayout.FollowYaw;
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
    private static readonly int InternalOff = EngineLayout.ExoAppInternal;
    private static readonly int PlayerIdOff = EngineLayout.InternalPlayerCreatureId;
    private static readonly int OptionsOff = EngineLayout.InternalClientOptions;
    private static readonly int ModeOff = EngineLayout.OptionsCameraMode;

    /// <summary>The Camera object sits 8 bytes before the CAurObject the behavior owns (<c>Camera::SetFollow</c>
    /// passes <c>this+8</c> on both builds).</summary>
    private const int CameraFromOwner = 8;

    private const float Deg = MathF.PI / 180f;
    /// <summary>The engine's own pitch limits (settings.tml [camera.limits]); the curve never leaves them.</summary>
    private const float PitchFloor = 1f, PitchCeil = 89f;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        CameraConfig.SelfCheck(log);
        SelfCheck(log);
        if (!EngineLayout.Require(log, "CameraService", "CAurBehavior", "CAurBehaviorCameraFollow", "CNWCModule",
                                  "CClientExoAppInternal", "CClientOptions", "Camera")) return;
        if (!SymbolMap.Require(log, "CameraService", SymbolMap.CameraFollowControl, SymbolMap.AppManager,
                               SymbolMap.GetModule)) return;

        _next = hooks.Chain(SymbolMap.CameraFollowControl,
                            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnControl, 10);
        if (_next == null) { log.Info("CameraService: standing down (Control chain refused)"); return; }

        if (GameGate.OwnsFramePresent)
            FrameRegistry.Global.Register(FramePhase.PostPresent, 50, OnFrameEnd, "CameraService", log);
        CameraCommand.Register("fov", "vertical FOV in degrees for the current camera mode (0 = engine's 45)", a => Knob("fov", a));
        CameraCommand.Register("lag", "`lag <pos> [rot]` seconds of position / rotation lag (0 = welded)", Lag);
        CameraCommand.Register("pitchcurve", "`pitchcurve <deg/unit> [ref]` pitch added per unit of distance past ref", PitchCurve);
        CameraCommand.Register("reset", "restore the engine feel for the current mode", _ => Reset());
        CameraCommand.Report(Report);
        log.Info("CameraService: chained on CAurBehaviorCameraFollow::Control");
    }

    // ---- the hook ---------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static int OnControl(IntPtr self, float dt)
    {
        // Read BEFORE the call: with no target the engine detaches (owner->SetBehavior(0)) and `self` is freed.
        IntPtr target = *(IntPtr*)(self + TargetOff);
        int r = ((delegate* unmanaged<IntPtr, float, int>)(*_next))(self, dt);
        _calls++;
        if (target == IntPtr.Zero) return r;
        if (!_live)
        {
            // One-shot boot probe of the module rows (#536 AC: Windows offsets get a probe, not a hope).
            _live = true;
            _probeOk = EngineLayout.Probe("CNWCModule", Module(), _log);
            _log?.Info(_probeOk ? "CameraService: first Control -- hook is LIVE" : "CameraService: layout probe failed -- passing the engine's answer through");
        }
        if (_probeOk) Adjust(self, dt);
        return r;
    }

    private static void Adjust(IntPtr self, float dt)
    {
        IntPtr cam = *(IntPtr*)(self + OwnerOff) - CameraFromOwner;
        Gate g = Check(self, cam, out int mode);
        _gate = g; _mode = mode;
        _yaw = *(float*)(self + YawOff); _pitch = *(float*)(self + PitchOff); _dist = *(float*)(self + DistOff);
        _fovRead = *(float*)(cam + CamFovOff);
        _pos = *(Vector3*)(cam + CamPosOff);
        if (g != Gate.Active) { _welded = true; return; }

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
            _sYaw = _yaw; _sPitch = _pitch; _sFocus = focus; _vYaw = _vPitch = 0; _vFocus = default;
            AlloyRenderer.CameraCut();
        }
        _sFocus = m.LagPos > 0 ? SmoothDamp(_sFocus, focus, ref _vFocus, m.LagPos, dt) : focus;
        if (m.LagRot > 0)
        {
            _sYaw = SmoothDamp(_sYaw, _sYaw + Wrap(_yaw - _sYaw), ref _vYaw, m.LagRot, dt);
            _sPitch = SmoothDamp(_sPitch, _pitch, ref _vPitch, m.LagRot, dt);
        }
        else { _sYaw = _yaw; _sPitch = _pitch; }

        float pitch = Math.Clamp(_sPitch + m.PitchCurve * (_dist - m.PitchRef), PitchFloor, PitchCeil);
        _outPitch = pitch;
        Quaternion q = Pose(_sYaw, pitch);
        Vector3 pos = _sFocus + _dist * Forward(q);
        // Camera+0xbc holds (w, x, y, z): Control's own position formula reads it in that order.
        float* rot = (float*)(cam + CamRotOff);
        rot[0] = q.W; rot[1] = q.X; rot[2] = q.Y; rot[3] = q.Z;
        *(Vector3*)(cam + CamPosOff) = pos;
        _pos = pos;
        if (m.Fov > 0) { *(float*)(cam + CamFovOff) = m.Fov; _fovWritten = m.Fov; }
        else _fovWritten = float.NaN;
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

    private static IntPtr App()
    {
        IntPtr mgr = *(IntPtr*)_appMgr;
        return mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
    }

    private static IntPtr Module()
    {
        IntPtr app = App();
        return app == IntPtr.Zero ? IntPtr.Zero : _getModule(app);
    }

    // ---- the maths: YawPitchRoll(yaw, pitch, 0) = qZ(yaw) * qX(pitch), Hamilton, (w,x,y,z) --------------
    // Axes are the engine's static-init globals (yaw 0x19e7ce8 = +Z, pitch 0x19e7d08 = +X); operator* @0x5b5c30
    // is the Hamilton product. So yaw 0 / pitch p puts the camera at focus + dist*(0, -sin p, cos p): south
    // of the focus, looking north, pitch measured from vertical.

    private static Quaternion Pose(float yawDeg, float pitchDeg)
    {
        (float sy, float cy) = MathF.SinCos(yawDeg * Deg * 0.5f);
        (float sp, float cp) = MathF.SinCos(pitchDeg * Deg * 0.5f);
        // qZ(yaw) = (cy, 0, 0, sy); qX(pitch) = (cp, sp, 0, 0); product expanded.
        return new Quaternion(cy * sp, sy * sp, sy * cp, cy * cp);
    }

    /// <summary>The unit the camera sits along from the focus: <c>q * +Z</c>, Control's own expansion.</summary>
    private static Vector3 Forward(in Quaternion q) =>
        new(2f * (q.X * q.Z + q.W * q.Y), 2f * (q.Y * q.Z - q.W * q.X), 1f - 2f * (q.X * q.X + q.Y * q.Y));

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
                 + Inv($"view angle first read {_fovFirst:0.##}, {_fovRewrites} rewrite(s) between ours; gate: {GateNames[(int)_gate]}"));
        _frames = 0; _callsMin = int.MaxValue; _callsMax = 0;
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
            Inv($"; {mode}: fov {(m.Fov > 0 ? m.Fov.ToString("0.#", CultureInfo.InvariantCulture) : "engine")} lag {m.LagPos:0.##}/{m.LagRot:0.##} pitchcurve {m.PitchCurve:0.##}@{m.PitchRef:0.#}");
        string curve = _gate == Gate.Active && _outPitch != _pitch ? Inv($"->{_outPitch:0.#}") : "";
        return Inv($"[camera] pos ({_pos.X:0.0}, {_pos.Y:0.0}, {_pos.Z:0.0}) yaw {_yaw:0.#} pitch {_pitch:0.#}{curve} dist {_dist:0.#} fov {_fovRead:0.#}")
             + $" | {(_gate == Gate.Active ? "active" : "held down: " + GateNames[(int)_gate])}{knobs}";
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
        E(MathF.Abs(Wrap(350f - 10f) + 20f) < 1e-5f, "yaw lag takes the short way round");
        float v = 0, x = 0;
        for (int i = 0; i < 240; i++) x = SmoothDamp(x, 1f, ref v, 0.2f, 1f / 60f);
        E(MathF.Abs(x - 1f) < 1e-3f, "the spring settles on its target");
        log.Info(ok ? "CameraService.SelfCheck: passed" : "CameraService.SelfCheck: FAILED");
    }
}
