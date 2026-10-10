using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Nameplate;

namespace CX.Services.Camera;

/// <summary>
/// Camera-relative WASD: pre-hooks the engine's per-frame drive step, <c>CClientExoAppInternal::UpdateDriveMode</c>,
/// and writes the player creature's orientation from camera yaw + key direction before it runs, so the engine still
/// derives the packet angle, sends, predicts and aborts by itself -- no server change, no new opcode. Only a turn
/// key's release goes unaborted by the engine, so that one <c>AbortDriveControl</c> is sent here. Allocates nothing.
/// </summary>
public sealed unsafe class CameraDriveService : IExtenderService
{
    private static ExtenderLog? _log;
    private static UpdateDriveModeFn _orig;
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    /// <summary>Resolved once: the drive step runs inside ProcessInput every frame.</summary>
    private static readonly GetGameObjectFn _getGameObject = EngineFn.GetGameObject;

    /// <summary>Key slots of <see cref="CClientExoAppInternal.DriveKeys"/>, in the engine's order.</summary>
    private const int Fwd = 0, Back = 1, TurnL = 2, TurnR = 3, StrafeL = 4, StrafeR = 5, KeyCount = 6;
    /// <summary><c>CNWCCreature</c> control flag bit UpdateDriveMode and DrivePlayer both require.</summary>
    private const byte PlayerControlled = 2;
    private const byte MajorInput = 6, MinorAbortDrive = 0x21;  // not-an-offset: a message opcode
    /// <summary>The wire angle is tenths of a degree, East = 0, counter-clockwise.</summary>
    private const int WireTenths = 3600;
    private const float Deg = MathF.PI / 180f;

    private enum Why { Active, Idle, Probe, NoMode, Off, Gate, NoCamera, NoCreature, NotDrivable, Cancelled, Free, Master, TurnOnly }
    private static readonly string[] WhyNames =
        { "active", "no key held", "layout probe failed", "no live camera mode", "off for this mode", "camera held down",
          "no world camera", "no player creature", "creature not drivable", "forward and back cancel", "free camera",
          "CX camera off for this mode", "turning the camera" };

    private static bool _live, _probeOk, _active, _engineAborts;
    /// <summary>The drive is running without a forward component (strafe/turn keys alone, or back), so the movement
    /// is a deliberate sidestep or back-pedal the recenter must not swing behind -- a strafe would become a spiral
    /// and a back-pedal a 180° swing that has no side to take.</summary>
    internal static bool NoForward { get; private set; }
    /// <summary>The engine was handed a key that moves the player on the last drive step -- walk, run, back or
    /// strafe, as it saw them after any rewrite here. <c>DrivePlayer</c> re-extends the path only every 250 ms (the
    /// <c>+0x198</c> gate in <c>UpdateDriveMode</c>), so a path running short is no sign of a release; this is. A
    /// turn key alone moves nothing. Read by the pose layer only once <see cref="Live"/>.</summary>
    internal static bool Held { get; private set; }
    /// <summary>The drive step has run, so <see cref="Held"/> is a reading and not the default.</summary>
    internal static bool Live => _live;
    /// <summary>Face mode (<c>&lt;mode&gt;.face</c>): the turn keys' camera yaw, +1 left / -1 right, consumed by
    /// <see cref="CameraService"/> before Control as a yaw velocity of <c>move.keyturn</c> deg/s.</summary>
    internal static int TurnAxis { get; private set; }
    private static Why _why = Why.Idle;
    private static int _sentTenths = -1, _tenths;
    /// <summary>The slewed heading in degrees, East 0 CCW; seeded from the creature's facing when a drive starts.</summary>
    private static float _heading;
    /// <summary>Last frame's key axes; a change latches a packet regardless of <c>move.hz</c>.</summary>
    private static float _lastF, _lastS;
    private static long _lastSendTick, _lastTick;
    private static int _sends, _latches, _aborts;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        CameraKnobs.Declare();
        SelfCheck(log);
        if (!EngineLayout.Require(log, "CameraDrive", "CClientExoAppInternal", "CNWCObject", "CNWCCreature", "CNWCModule", "Camera")) return;
        if (!SymbolMap.Require(log, "CameraDrive", SymbolMap.UpdateDriveMode, SymbolMap.AppManager, SymbolMap.GetGameObject,
                               SymbolMap.GetModule, SymbolMap.SendPlayerToServerMessage)) return;
        _orig = hooks.InstallUpdateDriveMode(&OnUpdateDriveMode);
        if (_orig == null) { log.Info("CameraDrive: standing down (UpdateDriveMode hook refused)"); return; }
        KnobSurface.Note(Report);
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
        var a = new CClientExoAppInternal(app);
        Span<int> keys = a.DriveKeys;
        if (FreeCameraService.Active)
        {
            // The keys fly the camera; the engine sees none held (a drive under way is aborted, once).
            _why = Why.Free; NoForward = false;
            if (_active) { _active = false; ClientApi.Send(MajorInput, MinorAbortDrive, null, 0); _aborts++; }
            int walk0 = a.DriveWalk, s0 = keys[0], s1 = keys[1], s2 = keys[2], s3 = keys[3], s4 = keys[4], s5 = keys[5];
            a.DriveWalk = 0; keys[0] = keys[1] = keys[2] = keys[3] = keys[4] = keys[5] = 0;
            Step(app, a);
            a.DriveWalk = walk0; keys[0] = s0; keys[1] = s1; keys[2] = s2; keys[3] = s3; keys[4] = s4; keys[5] = s5;
            return;
        }
        ref int walk = ref a.DriveWalk;
        bool held = walk != 0;
        for (int k = 0; k < KeyCount; k++) held |= keys[k] != 0;
        // The engine's own key-up aborts the drive for these; a turn key's release is ours to abort.
        bool engineKey = (walk | keys[Fwd] | keys[Back] | keys[StrafeL] | keys[StrafeR]) != 0;
        IntPtr cre = IntPtr.Zero, cam = IntPtr.Zero;
        Why why = !held ? Why.Idle : !_probeOk ? Why.Probe : Wanted(app, out cre, out cam);
        float f = 0f, s = 0f;
        int liveMode = CameraService.Mode;
        bool face = liveMode >= 0 && CameraModes.ByMode[liveMode].Face.V;
        // Side run: a turn key under <mode>.siderun, or any side key while locked on a target, runs the
        // creature sideways facing where it runs -- no orbit for the turn keys, no held facing to strafe under.
        bool sideRun = false;
        if (face)
        {
            bool locked = TabTargetService.LockedOn;
            sideRun = locked || CameraModes.ByMode[liveMode].SideRun.V;
            int side = (locked ? keys[StrafeL] | keys[StrafeR] : 0) | (sideRun ? keys[TurnL] | keys[TurnR] : 0);
            if (side != 0) face = false;
        }
        TurnAxis = 0;
        if (why == Why.Active)
        {
            f = (keys[Fwd] | walk) != 0 ? 1f : 0f; f -= keys[Back] != 0 ? 1f : 0f;
            if (face)
            {
                s = keys[StrafeR] != 0 ? 1f : 0f; s -= keys[StrafeL] != 0 ? 1f : 0f;
                TurnAxis = (keys[TurnL] != 0 ? 1 : 0) - (keys[TurnR] != 0 ? 1 : 0);
            }
            else
            {
                s = (keys[TurnR] | keys[StrafeR]) != 0 ? 1f : 0f; s -= (keys[TurnL] | keys[StrafeL]) != 0 ? 1f : 0f;
            }
            if (f == 0f && s == 0f) why = TurnAxis != 0 ? Why.TurnOnly : Why.Cancelled;
        }
        _why = why;
        NoForward = why == Why.Active && !face && f <= 0f;
        if (why != Why.Active)
        {
            if (_active && !held)
            {
                _active = false;
                if (!_engineAborts) { ClientApi.Send(MajorInput, MinorAbortDrive, null, 0); _aborts++; }
            }
            if (why == Why.TurnOnly)
            {
                // The turn keys turn the camera, not the creature: the engine sees them up (no turn-on-spot).
                int t2 = keys[TurnL], t3 = keys[TurnR];
                keys[TurnL] = keys[TurnR] = 0;
                Step(app, a);
                keys[TurnL] = t2; keys[TurnR] = t3;
                return;
            }
            Step(app, a);
            return;
        }

        // Pitch is about the camera's X, so its right stays on the ground plane and forward-on-the-ground
        // is up x right = (-right.y, right.x).
        Vector3 r = CameraService.Right(new CX.Engine.Camera(cam).Rot);
        var obj = new CNWCObject(cre);
        long now = Environment.TickCount64;
        // Face mode: the creature faces the camera and the engine's own back/strafe bits carry the direction.
        float want = face ? Heading(r.X, r.Y, 1f, 0f) : Heading(r.X, r.Y, f, s);
        // A drive starts on the wanted heading: the wire angle is a path, so a slew from the old facing walks an arc.
        if (!_active) { _heading = want; _lastTick = now; }
        float dt = MathF.Min((now - _lastTick) * 0.001f, 0.1f);
        _lastTick = now;
        CameraModeKnobs mk = CameraModes.ByMode[liveMode];
        // Side run's back-pedal snaps to the camera: a slew from the sideways facing it just ran on would walk an arc.
        _heading = sideRun && f < 0f ? want : Slew(_heading, want, mk.Turn.V, dt);
        bool keysChanged = f != _lastF || s != _lastS;
        _lastF = f; _lastS = s;
        int tenths = Tenths(_heading);
        _tenths = tenths;
        (float sn, float cs) = MathF.SinCos(tenths * 0.1f * Deg);
        var dir = new Vector3(cs, sn, 0f);
        obj.Orientation = dir;
        var creature = new CNWCCreature(cre);
        creature.FacingTarget = dir;
        creature.FacingTargetB = dir;
        // The chase cam reads these in UpdateCamera, later this frame: no swing behind a strafing creature.
        var mod = new CNWCModule(CameraService.Module());
        mod.ChaseSuppressed = 1;
        mod.ChaseWasIdle = 0;

        // move.hz throttles camera-driven drift only; a key change is sent this frame.
        if (tenths != _sentTenths && a.DriveChanged == 0
            && (keysChanged || now - _lastSendTick >= (long)(1000f / mk.Hz.V))) { a.DriveChanged = 1; _latches++; }

        long sendBefore = a.DriveLastSend;
        int w0 = walk, k0 = keys[0], k1 = keys[1], k2 = keys[2], k3 = keys[3], k4 = keys[4], k5 = keys[5];
        keys[TurnL] = keys[TurnR] = 0;
        (keys[Fwd], walk) = ForwardSlots(face, f, k0, w0);
        if (face) { keys[Back] = f < 0f ? 1 : 0; keys[StrafeL] = s < 0f ? 1 : 0; keys[StrafeR] = s > 0f ? 1 : 0; }
        else keys[Back] = keys[StrafeL] = keys[StrafeR] = 0;
        Step(app, a);
        walk = w0; keys[0] = k0; keys[1] = k1; keys[2] = k2; keys[3] = k3; keys[4] = k4; keys[5] = k5;
        if (a.DriveLastSend != sendBefore) { _sentTenths = tenths; _lastSendTick = now; _sends++; }
        _active = true; _engineAborts = engineKey;
    }

    /// <summary>The engine's drive step, <see cref="Held"/> read off the keys exactly as it is handed them.</summary>
    private static void Step(IntPtr app, CClientExoAppInternal a)
    {
        Span<int> keys = a.DriveKeys;
        Held = (a.DriveWalk | keys[Fwd] | keys[Back] | keys[StrafeL] | keys[StrafeR]) != 0;
        _orig(app);
    }

    /// <summary>The forward slots the engine is handed (run = <see cref="EngineLayout.InternalDriveKeys"/>[0], walk =
    /// <see cref="EngineLayout.InternalDriveWalk"/>): a forward drive passes the pressed pair through so the engine's own
    /// walk/run choice stands; without a forward component, non-face still needs a forward slot (run), face none.</summary>
    private static (int run, int walk) ForwardSlots(bool face, float f, int run, int walk) =>
        f > 0f ? (run, walk) : (face ? 0 : 1, 0);

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
        CameraModeKnobs m = CameraModes.ByMode[mode];
        if (!m.Enabled.V) return Why.Master;
        if (!m.Drive.V) return Why.Off;
        if (CameraService.GateUp) return Why.Gate;
        IntPtr module = CameraService.Module();
        cam = module == IntPtr.Zero ? IntPtr.Zero : new CNWCModule(module).Camera.Ptr;
        if (cam == IntPtr.Zero) return Why.NoCamera;
        IntPtr mgr = *(IntPtr*)_appMgr;
        IntPtr exo = mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
        cre = exo == IntPtr.Zero ? IntPtr.Zero : _getGameObject(exo, new CClientExoAppInternal(app).PlayerCreatureId);
        if (cre == IntPtr.Zero || new CNWCObject(cre).Type != NameplateNative.TypeCreature) return Why.NoCreature;
        if ((new CNWCCreature(cre).AiFlags & PlayerControlled) == 0 || new CNWCCreature(cre).DriveEnabled == 0) return Why.NotDrivable;
        return Why.Active;
    }

    // ---- the /knobs note (cold) ----

    private static string Report()
    {
        int mode = CameraService.Mode;
        string on = mode < 0 ? "?" : !CameraModes.ByMode[mode].Enabled.V ? "off (master)" : CameraModes.ByMode[mode].Drive.V ? "on" : "off";
        return FormattableString.Invariant(
            $"camera drive: {on} ({(mode < 0 ? "no mode" : CameraModes.ByMode[mode].G.Name)}) heading {_tenths / 10f:0.0} | {(_why == Why.Active ? "active" : "held down: " + WhyNames[(int)_why])} | {_sends} send(s), {_latches} latch(es), {_aborts} abort(s){(mode < 0 ? "" : $", hz {CameraModes.ByMode[mode].Hz.V:0.#} turn {CameraModes.ByMode[mode].Turn.V:0}")}");
    }

    /// <summary>The heading maths and the forward-slot hand-off off-device: the four cardinal keys against a camera
    /// looking north (right = +X), and which forward key the engine sees for each held pair.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "CameraDrive.SelfCheck");
        static int T(float rx, float ry, float f, float s) => Tenths(Heading(rx, ry, f, s));
        check.Expect(T(1, 0, 1, 0) == 900, "W walks north (90°)");
        check.Expect(T(1, 0, -1, 0) == 2700, "S walks south (270°)");
        check.Expect(T(1, 0, 0, 1) == 0, "D walks east (0°)");
        check.Expect(T(1, 0, 0, -1) == 1800, "A walks west (180°)");
        check.Expect(T(1, 0, 1, 1) == 450, "W+D is the 45° diagonal");
        check.Expect(T(0, 1, 1, 0) == 1800, "camera turned to look west: W walks west");
        check.Expect(Tenths(Slew(350f, 10f, 540f, 0.01f)) == 3554, "the slew takes the short way across 0 and wraps");
        check.Expect(Tenths(Slew(10f, 350f, 540f, 0.01f)) == 46 && Slew(10f, 350f, 0f, 0.01f) == 350f, "and back; rate 0 snaps");
        check.Expect(Slew(90f, 270f, 540f, 1f) == 270f, "a reach within the budget lands exactly");
        check.Expect(ForwardSlots(false, 1f, 0, 1) == (0, 1) && ForwardSlots(true, 1f, 0, 1) == (0, 1), "the walk key alone walks in both modes");
        check.Expect(ForwardSlots(false, 1f, 1, 0) == (1, 0) && ForwardSlots(true, 1f, 1, 0) == (1, 0), "the run key alone runs");
        check.Expect(ForwardSlots(false, 1f, 1, 1) == (1, 1) && ForwardSlots(true, 1f, 1, 1) == (1, 1), "walk + run held: run wins (the engine checks run first)");
        check.Expect(ForwardSlots(false, 0f, 0, 0) == (1, 0), "strafe alone, non-face: forward run carries the heading");
        check.Expect(ForwardSlots(true, 0f, 0, 0) == (0, 0) && ForwardSlots(true, -1f, 0, 0) == (0, 0), "strafe or back alone, face: no forward key");
        check.Expect(ForwardSlots(false, 0f, 1, 1) == (1, 0), "forward + back cancel with a strafe, non-face: run along the strafe");
        log.Info(check.Ok ? "CameraDrive.SelfCheck: passed" : "CameraDrive.SelfCheck: FAILED");
    }
}
