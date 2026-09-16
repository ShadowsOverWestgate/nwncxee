using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>
/// Action-camera head tracking: while <c>mouselook.action</c> is held, the local player's creature turns its
/// HEAD toward the camera axis -- which under the action camera is the crosshair -- with the body left to the
/// engine. Nothing is invented: NWN already ships clamped yaw/pitch head tracking (the <c>LookAtAnimation</c>
/// every creature runs when the server sends it a look-at target), so this retargets that machinery instead of
/// overriding a bone.
/// <para><b>Two seams.</b> <c>CNWCCreature::UpdateLookAt</c> is the engine's per-frame target chooser; for the
/// player creature it is skipped and <c>Gob::BeginLookAtAnimate</c> is called directly with the gob as its own
/// target, which builds the animation the engine would have built (and keeps its 0.25 s ease, its clamp and its
/// teardown). The target only feeds the distance check and the SafePointer, so aiming at ourselves is free.
/// Then <c>LookAtAnimation::Control</c> is chained: the original does the keyframe bookkeeping, and the four
/// floats it wrote at <c>+0x58</c> are overwritten with the camera aim, clamped to
/// <c>mouselook.headyaw</c>/<c>headpitch</c>. Everything downstream -- the blend, <c>AnimateHierarchy</c>, the
/// skinned bone matrices Alloy reads, the shadow casters -- is the engine's own path, untouched.</para>
/// <para><b>Aim.</b> The rendered camera pose (<see cref="CameraService.Rendered"/>), not a pick ray: under the
/// action camera the crosshair IS the camera axis, and a ray would snap the head every time it crossed a near
/// wall. The yaw is measured the engine's way -- the arc from the head's world forward (local <b>+Y</b>) onto
/// the aim, composed onto the head's own local rotation -- so the clamp is relative to the body's facing, and
/// the pitch is the aim's elevation.</para>
/// <para><b>Hot.</b> <c>Control</c> runs inside <c>Gob::Animate</c> and <c>UpdateLookAt</c> once per creature
/// per frame: static state, pointer reads, float math, no allocation. The quaternion maths is ported, never
/// called -- the engine's <c>RotationArc</c>/<c>Yaw</c>/<c>YawPitchRoll</c> return a class by value, which is
/// the #456 sret trap in its purest form.</para>
/// <para>Client-only, and only the player who turned it on sees it: relaying the aim to other players is a
/// wire contract of its own and is a follow-up, not part of this.</para>
/// </summary>
public sealed unsafe class HeadTrackService : IExtenderService
{
    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, void> _updateOrig;
    private static IntPtr* _controlNext;
    private static delegate* unmanaged<IntPtr, IntPtr, byte*, byte*, float, float, float, float, void> _begin;
    private static delegate* unmanaged<IntPtr, Vector3*, float*, void> _getWorld;

    /// <summary>The head node name handed to <c>FindPart</c>, preallocated: the hook is not allowed to allocate,
    /// and the engine keeps no copy (it reads it inside the call). <c>head_g</c> is what vanilla looks for when
    /// <c>appearance.2da</c> names none. ponytail: the per-row HEAD_NAME is not read, so a race whose head node
    /// is named otherwise simply does not track -- <c>BeginLookAtAnimate</c> returns silently on a missed
    /// <c>FindPart</c>, which the write count shows; the upgrade path is the creature's appearance id indexed
    /// into <c>appearance.2da</c>'s HEAD_NAME column, taking this name only as the fallback.</summary>
    private static byte* _headNode;

    /// <summary>The gob we drove this frame, and the key <see cref="OnControl"/> matches an animation by
    /// (<c>LookAtAnimation+0x30</c> is the looker's gob). Zero = not driving.</summary>
    private static IntPtr _gob;
    /// <summary>The YAW is beyond what a head can do (behind the shoulder): it is let go, and zeroed, until it
    /// comes back inside the limit. Degrees past <c>headyaw</c> that release it, and the hysteresis band below
    /// that which re-engages it. Pitch is never released.</summary>
    private static bool _released;
    private const float ReleaseOver = 20f, ReleaseHysteresis = 10f;

    private static bool _armed, _probed, _rot;
    private static int _drives, _writes;

    private static readonly int GetModelSlot = EngineLayout.CreatureGetModelSlot;
    private static readonly int AnimFinished = EngineLayout.LookAtFinished;
    private static readonly int AnimGob = EngineLayout.LookAtGob;
    private static readonly int AnimHeadPart = EngineLayout.LookAtHeadPart;
    private static readonly int AnimTargetKey = EngineLayout.LookAtTargetKey;
    private static readonly int PartRot = EngineLayout.PartRot;

    private const float Deg = MathF.PI / 180f;
    /// <summary>The engine's own literals at the <c>BeginLookAtAnimate</c> call site: the 0.25 s ease every
    /// look-at blends over, and a reach the self-target can never exceed (the distance check is gob-to-gob).</summary>
    private const float Blend = 0.25f, Reach = 1e9f;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        CameraKnobs.Declare();
        SelfCheck(log);
        if (!EngineLayout.Require(log, "HeadTrack", "CNWCCreature", "LookAtAnimation", "Part")) return;
        if (!SymbolMap.Require(log, "HeadTrack", SymbolMap.UpdateLookAt, SymbolMap.LookAtControl,
                               SymbolMap.BeginLookAtAnimate, SymbolMap.PartGetWorld)) return;
        _begin = (delegate* unmanaged<IntPtr, IntPtr, byte*, byte*, float, float, float, float, void>)
                 Native.Sym(SymbolMap.BeginLookAtAnimate);
        _getWorld = (delegate* unmanaged<IntPtr, Vector3*, float*, void>)Native.Sym(SymbolMap.PartGetWorld);

        ReadOnlySpan<byte> node = "head_g\0"u8;
        _headNode = (byte*)NativeMemory.AllocZeroed((nuint)node.Length);
        node.CopyTo(new Span<byte>(_headNode, node.Length));

        _updateOrig = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.UpdateLookAt, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnUpdateLookAt);
        if (_updateOrig == null) { log.Info("HeadTrack: standing down (UpdateLookAt install refused)"); return; }
        _controlNext = hooks.Chain(SymbolMap.LookAtControl, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnControl, 50);
        // Both or neither: the chooser hook alone would aim the head at the creature's own head.
        if (_controlNext == null) { log.Info("HeadTrack: standing down (Control chain refused)"); return; }
        _armed = true;
        log.Info("HeadTrack: hooked CNWCCreature::UpdateLookAt + LookAtAnimation::Control "
                 + $"(arcs {MouseLook.HeadYaw.V:0}/{MouseLook.HeadPitch.V:0} deg, {(MouseLook.HeadTrack.V ? "on" : "off")})");
    }

    /// <summary>Driving this frame: the knob, the action camera actually held, and nothing rotten.</summary>
    private static bool Wanted => _armed && !_rot && MouseLook.HeadTrack.V && MouseLookService.ActionHeld;

    /// <summary>The engine's own target chooser. For the local player under the action camera it is replaced
    /// by one <c>BeginLookAtAnimate</c> aimed at ourselves, which <see cref="OnControl"/> then re-aims; every
    /// other creature, and every frame we are not driving, runs vanilla.</summary>
    [UnmanagedCallersOnly]
    private static void OnUpdateLookAt(IntPtr creature)
    {
        if (creature == IntPtr.Zero) { _gob = IntPtr.Zero; return; }   // a null `this` is not the engine's to take
        if (!Wanted) { _gob = IntPtr.Zero; _updateOrig(creature); return; }
        if (creature != CameraService.PlayerCreature()) { _updateOrig(creature); return; }
        if (!_probed)
        {
            _probed = true;
            _rot = !EngineLayout.Probe("CNWCCreature", creature, _log);
            if (_rot) { _gob = IntPtr.Zero; _updateOrig(creature); return; }
        }
        IntPtr gob = Model(creature);
        if (gob == IntPtr.Zero) { _gob = IntPtr.Zero; _updateOrig(creature); return; }
        _gob = gob; _drives++;
        _begin(gob, gob, _headNode, _headNode, MouseLook.HeadYaw.V, MouseLook.HeadPitch.V, Blend, Reach);
    }

    /// <summary>The creature's Gob, through the engine's own accessor: <c>GetModel(0xff, 1)</c> forwards to
    /// the CNWCAnimBase at +0x78, which answers <c>*(animBase + 0xc8)</c>.</summary>
    private static IntPtr Model(IntPtr creature)
    {
        IntPtr vtbl = *(IntPtr*)creature;
        if (vtbl == IntPtr.Zero) return IntPtr.Zero;
        return ((delegate* unmanaged<IntPtr, byte, int, IntPtr>)(*(IntPtr*)(vtbl + GetModelSlot)))(creature, 0xff, 1);
    }

    /// <summary>After the engine's own aim step: ours is the animation whose looker is the gob we drove, and
    /// only while it is still live (the finished byte is the release latch's, not ours to fight).</summary>
    [UnmanagedCallersOnly]
    private static void OnControl(IntPtr self)
    {
        ((delegate* unmanaged<IntPtr, void>)(*_controlNext))(self);
        IntPtr gob = _gob;
        // Wanted as well as the gob: the camera dropping is what stops us driving, and a gob freed after it
        // dropped could otherwise be matched by an address the engine has handed to something else.
        if (gob == IntPtr.Zero || self == IntPtr.Zero || !Wanted || *(IntPtr*)(self + AnimGob) != gob) return;
        if (*(byte*)(self + AnimFinished) != 0) return;
        float* key = *(float**)(self + AnimTargetKey);
        IntPtr head = *(IntPtr*)(self + AnimHeadPart);
        if (key == null || head == IntPtr.Zero) return;
        Aim(head, key);
        _writes++;
    }

    /// <summary>Control's own maths with one substitution: the direction is the camera axis rather than the
    /// vector to a target object. Yaw is the arc from the head's world forward onto it, composed onto the
    /// head's local rotation (so the clamp is measured from the body's facing); pitch is the aim's elevation,
    /// which is what the engine's <c>Pitch(qX(asin(dir.z)))</c> reduces to. The camera sits at focus + dist *
    /// Forward and looks back at the focus, so the axis aimed at is -Forward.
    /// <para>Past the yaw limit by <see cref="ReleaseOver"/> the head lets go and settles level rather than
    /// straining at the clamp, and re-engages a hysteresis band below that. The release is yaw-only: pitch keeps
    /// its own clamp either way, and a <c>headyaw</c> of 0 never releases -- the yaw is simply 0.</para></summary>
    private static void Aim(IntPtr head, float* key)
    {
        CameraService.Rendered(out _, out float camYaw, out float camPitch, out _);
        Vector3 dir = -CameraService.Forward(CameraService.Pose(camYaw, camPitch));
        Vector3 pos;
        float* world = stackalloc float[4];
        _getWorld(head, &pos, world);
        // Engine quaternions are (w, x, y, z); System.Numerics is (x, y, z, w).
        Quaternion wq = new(world[1], world[2], world[3], world[0]);
        float* local = (float*)(head + PartRot);
        Quaternion lq = new(local[1], local[2], local[3], local[0]);
        Vector3 fwd = YAxis(wq);
        if (fwd.LengthSquared() < 1e-12f) return;
        float arcH = MouseLook.HeadYaw.V, arcV = MouseLook.HeadPitch.V;
        float want = YawOf(RotationArc(fwd, dir) * lq);
        float over = arcH > 0f ? MathF.Abs(want) - arcH : 0f;
        if (over > ReleaseOver) _released = true; else if (over < ReleaseOver - ReleaseHysteresis) _released = false;
        float yaw = _released ? 0f : Math.Clamp(want, -arcH, arcH);
        float pitch = Math.Clamp(MathF.Asin(Math.Clamp(dir.Z, -1f, 1f)) / Deg, -arcV, arcV);
        Quaternion q = CameraService.Pose(yaw, pitch);
        key[0] = q.X; key[1] = q.Y; key[2] = q.Z; key[3] = q.W;
    }

    /// <summary>A quaternion's local <b>+Y</b> axis -- the head's forward, and what the engine's
    /// <c>Yaw</c>/<c>Pitch</c> overloads read a quaternion through.</summary>
    private static Vector3 YAxis(in Quaternion q) =>
        new(2f * (q.X * q.Y - q.W * q.Z), 1f - 2f * (q.X * q.X + q.Z * q.Z), 2f * (q.Y * q.Z + q.W * q.X));

    /// <summary><c>Yaw(Quaternion const&amp;)</c> @0x5b68b0 ported: <c>atan2(-y.x, y.y)</c> in degrees over the
    /// quaternion's +Y axis, i.e. the same convention as <c>Yaw(Vector const&amp;)</c> @0x5b6cc0. Zero when the
    /// axis has no horizontal part, as the engine's own guard does.</summary>
    private static float YawOf(in Quaternion q)
    {
        float x = 2f * (q.X * q.Y - q.W * q.Z), y = 1f - 2f * (q.X * q.X + q.Z * q.Z);
        return x == 0f && y == 0f ? 0f : MathF.Atan2(-x, y) / Deg;
    }

    /// <summary><c>RotationArc(Vector, Vector)</c> @0x5b7070 ported: the shortest rotation taking
    /// <paramref name="a"/> onto <paramref name="b"/>, and the engine's own antiparallel answer (a half turn
    /// about +X) when the two oppose.</summary>
    private static Quaternion RotationArc(Vector3 a, Vector3 b)
    {
        a = Vector3.Normalize(a); b = Vector3.Normalize(b);
        float d = Vector3.Dot(a, b);
        if (d <= -1f) return new Quaternion(1f, 0f, 0f, 0f);
        Vector3 c = Vector3.Cross(a, b);
        float s = MathF.Sqrt((d + 1f) * 2f);
        return new Quaternion(c.X / s, c.Y / s, c.Z / s, s * 0.5f);
    }

    /// <summary>The <c>/mouselook</c> tail: whether the head is being driven and whether the aim is landing.
    /// Drives without writes means <c>FindPart</c> never found the head node.</summary>
    internal static string Fragment =>
        !_armed ? "head tracking unhooked"
        : _rot ? "head tracking stood down (layout probe)"
        : FormattableString.Invariant($"head tracking {(MouseLook.HeadTrack.V ? "on" : "off")}, {_drives} drive(s), {_writes} aim write(s)");

    /// <summary>The ported maths against the engine's conventions: a yaw of +Y is 0 and east is -90
    /// (<c>atan2(-x, y)</c>), and a pose written by <see cref="CameraService.Pose"/> reads back as the yaw it
    /// was built from -- the one property the overwrite depends on.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"HeadTrack.SelfCheck FAIL: {w}"); } }
        static bool Near(float a, float b) => MathF.Abs(a - b) < 0.01f;
        E(Near(YawOf(Quaternion.Identity), 0f), "facing +Y is yaw 0");
        E(Near(YawOf(RotationArc(Vector3.UnitY, Vector3.UnitX)), -90f), "an aim due east is yaw -90");
        E(Near(YawOf(RotationArc(Vector3.UnitY, -Vector3.UnitX)), 90f), "an aim due west is yaw +90");
        E(Near(YawOf(CameraService.Pose(35f, 0f)), 35f), "the written pose reads back as its own yaw");
        E(Near(YAxis(Quaternion.Identity).Y, 1f) && Near(YAxis(CameraService.Pose(0f, 30f)).Z, 0.5f),
          "+Y is the unpitched forward, and a written pitch is the elevation the aim was measured as");
        log.Info(ok ? "HeadTrack.SelfCheck: passed" : "HeadTrack.SelfCheck: FAILED");
    }
}
