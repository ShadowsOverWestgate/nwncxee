using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Services.Camera;
using static CX.Framework.EngineLayout;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// The procedural pose layer. Chains <c>Gob::Animate</c>: after the engine has posed a humanoid creature, the
/// modules correct it in order -- <see cref="FootPlacement"/> (CX-29), <see cref="Stride"/> (pace and foot lock),
/// <see cref="UpperBody"/> (look-at shared down the spine, lean, aim, hit reactions) -- and only <see cref="Stride.Pre"/> runs before the engine,
/// to pace the runs it is about to advance.
/// <para><b>The pose is restored before the engine animates.</b> A crossfade blends FROM the part's current
/// local value (<c>NewController::Control</c>), and an unkeyed part (the root, usually) keeps whatever it holds,
/// so a correction left in place would leak into the next crossfade and accumulate on unkeyed parts. Each
/// creature gob that is corrected owns a slot holding the engine's own values; nothing is ever written to a gob
/// without one.</para>
/// <para><b>Bones</b> are <c>{r,l}thigh_g -&gt; {r,l}shin_g -&gt; {r,l}foot_g</c>, <c>torso_g</c>,
/// <c>neck_g</c> and <c>head_g</c>, the names FindPart would match (case-insensitive): every stock humanoid (a_ba and its
/// descendants, 257 of 663 stock a_/c_ models) carries them, and every rest orientation is identity, so a
/// bone's rest frame is the model's: +X right, +Y forward, +Z up (docs/research/procedural-anim-skeleton.md).
/// A gob without all six leg parts is skipped whole.</para>
/// <para><b>The gate is a deny list</b>: every stock stance/move/attack name would have to be listed for an
/// allow list, and module-added animations mostly arrive as <c>custom*</c>, which is denied.</para>
/// <para><b>Hot.</b> Once per creature per frame: pointer reads, a part-tree walk, cached HitCheck casts, a few
/// quaternion products, no allocation. Only gobs in the engine's view volume and within <c>anim.distance</c>
/// of the player are corrected; the clause's <c>ms/f</c> is the layer's own time, the engine's excluded.</para>
/// </summary>
public sealed unsafe class PoseService : IExtenderService
{
    private static ExtenderLog? _log;
    private static IntPtr* _next;
    private static delegate* unmanaged<IntPtr, float> _scale;
    private static bool _armed, _creatures;

    /// <summary>World units between two frames past which the gob teleported: springs snap, nothing eases.</summary>
    private const float Teleport = 2f;
    /// <summary>Part-tree recursion guard: a cycle in a torn tree must not blow the stack. Real trees are ~6 deep.</summary>
    private const int MaxDepth = 64;
    /// <summary>Seconds the measured ground speed, yaw rate and slope average over: the engine steps a
    /// walking gob's position once per AI update on an integer-millisecond clock, so one frame's travel is noise.</summary>
    private const float MotionTime = 0.15f;
    /// <summary>Seconds the acceleration averages over: it is the derivative of a speed that steps, so it wants
    /// twice the smoothing the speed does or the lean pitch shivers.</summary>
    private const float AccelTime = 0.3f;
    /// <summary>Blend weight below which a run is still fading in or out: no stride sample is taken then.</summary>
    private const float Settled = 0.99f;

    /// <summary>Animations whose name starts with one of these (NUL-separated) get no correction: sitting,
    /// lying, knockdown and getting up, death, kneeling emotes, mounting, flying in/out and the module-defined
    /// customs. Every stock creature animation was listed to pick them (the census in the CX-29 notes).</summary>
    private static ReadOnlySpan<byte> Denied =>
        "sit\0standup\0kd\0ckd\0dead\0cdead\0die\0gu\0cgu\0getlow\0getmid\0cgetmid\0kneel\0meditate\0worship\0mount\0custom\0appear\0cappear\0disappear\0cdisappear\0"u8;

    /// <summary>The parts the layer writes, found by name each frame (equipment swaps rebuild parts).</summary>
    internal struct Bones
    {
        public const int Count = 9;
        public IntPtr RThigh, RShin, RFoot, LThigh, LShin, LFoot, Torso, Neck, Head;
        /// <summary>By <see cref="BoneIndex"/>: r-thigh, r-shin, r-foot, l-thigh, l-shin, l-foot, torso, neck, head.</summary>
        public IntPtr this[int i]
        {
            readonly get => i switch { 0 => RThigh, 1 => RShin, 2 => RFoot, 3 => LThigh, 4 => LShin, 5 => LFoot, 6 => Torso, 7 => Neck, _ => Head };
            set { switch (i) { case 0: RThigh = value; break; case 1: RShin = value; break; case 2: RFoot = value; break;
                               case 3: LThigh = value; break; case 4: LShin = value; break; case 5: LFoot = value; break;
                               case 6: Torso = value; break; case 7: Neck = value; break; default: Head = value; break; } }
        }
        public readonly bool Legs => RThigh != 0 && RShin != 0 && RFoot != 0 && LThigh != 0 && LShin != 0 && LFoot != 0;
    }

    /// <summary>One corrected gob: the engine's own values for everything written (root pos + the bones'
    /// rotations, w-first) and every module's springs. Keyed by gob AND root part so a recycled address never
    /// restores.</summary>
    internal struct Slot
    {
        public IntPtr Gob, Root;
        public long Stamp;
        public bool Applied;
        public Vector3 RootPos, LastPos;
        /// <summary>Measured motion: horizontal ground speed (world units/s), forward acceleration, yaw and its rate
        /// (rad/s, positive = left), and the signed walkmesh rise per unit of travel. Fwd/Right are the last
        /// horizontal axes, for a hit that arrives between frames.</summary>
        public float Speed, Accel, Yaw, YawRate, Slope;
        public Vector3 Fwd, Right;
        /// <summary><see cref="FootPlacement.Weight"/>'s stairs grade and the moving hold timer.</summary>
        public float Grade, Moving;
        /// <summary>Foot placement: the offset springs, the floor slopes under each foot toward the toe (F) and
        /// to the right (S), the stairs weight, and which slope is re-cast this frame.</summary>
        public float OffR, VelR, OffL, VelL, FR, VFR, SR, VSR, FL, VFL, SL, VSL, W, VW;
        public int Phase;
        /// <summary>Each foot's cached floor cast (<see cref="FootPlacement.FootCast"/>).</summary>
        public FootPlacement.FootCast R, L;
        /// <summary>Stride: locomoting last frame, the stance foot's speed per unit engine rate, the eased
        /// factor minus one, the multiplier applied this frame, and last frame's model-space ankles.</summary>
        public bool Loco, HasPrev;
        public float Stance, Factor, VFactor, RateF;
        public Vector3 PrevR, PrevL;
        public fixed long RateRun[Stride.TrackedRuns];
        public fixed float RateEngine[Stride.TrackedRuns];
        public fixed float RateWritten[Stride.TrackedRuns];
        /// <summary>Foot lock: each foot's held world point, whether it is held or let go until it lifts, and
        /// the drawn XY offset with its spring velocity; the stance height in model units.</summary>
        public Vector3 LockR, LockL;
        public bool HeldR, HeldL, SlipR, SlipL;
        public Vector2 LockDR, LockVR, LockDL, LockVL;
        public float PlantZ;
        /// <summary>Upper body: the lean, aim and flinch springs, (R, F) radians.</summary>
        public Vector2 Lean, LeanV, Hit, HitV;
        public float AimPitch, AimV, AimW, AimWV;
        /// <summary>The look share's spring: the head's turn past the deadzone, (yaw, pitch) radians.</summary>
        public Vector2 Look, LookV;
        /// <summary>The engine's rotations, w-first, in <see cref="BoneIndex"/> order (4 floats each), and the
        /// parts they came from: a rotation is restored only onto the same part.</summary>
        public fixed float Rot[Bones.Count * 4];
        public fixed long Parts[Bones.Count];
    }

    /// <summary>Open-addressed, <see cref="Probe"/> slots per lookup. ponytail: a gob that leaves the scene
    /// without dying keeps its slot until <see cref="StaleTicks"/>, and one evicted after that and later re-added
    /// carries its last correction as a fixed offset -- the upgrade is a <c>Gob::~Gob</c> death notice.</summary>
    private static Slot[] _slots = [];
    /// <summary>Stands in for a body slot that does not exist, where a <c>ref</c> is required.</summary>
    private static Slot _none;
    private const int SlotCount = 1024, Probe = 8;
    private static long StaleTicks => System.Diagnostics.Stopwatch.Frequency * 30;

    private static int _gobs, _gated, _noSlot, _followed;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _ = AnimKnobs.G;
        SelfCheck(log);
        if (!EngineLayout.Require(log, "Pose", "Gob", "Part", "MdlNode", "AnimRun", "Animation", "CHitInfo", "Scene")) return;
        if (!SymbolMap.Require(log, "Pose", SymbolMap.GobAnimate, SymbolMap.PartGetWorld, SymbolMap.GobGetAdjustedScale,
                               SymbolMap.SceneHitCheck, SymbolMap.HitInfoReset, SymbolMap.CurrentScene)) return;
        SceneRay.Init();
        GetWorld = (delegate* unmanaged<IntPtr, Vector3*, float*, void>)Native.Sym(SymbolMap.PartGetWorld);
        _scale = (delegate* unmanaged<IntPtr, float>)Native.Sym(SymbolMap.GobGetAdjustedScale);
        if (!SceneRay.Armed || GetWorld == null || _scale == null) { log.Info("Pose: standing down (symbols unresolved)"); return; }
        _creatures = EngineLayout.Require(log, "Pose.creature", "CNWCObject", "CNWCCreature");
        _slots = new Slot[SlotCount];
        _pending = new Pending[PendingCount];
        _next = hooks.Chain(SymbolMap.GobAnimate, (IntPtr)(delegate* unmanaged<IntPtr, float, void>)&OnAnimate, 50);
        if (_next == null) { log.Info("Pose: standing down (Gob::Animate chain refused)"); return; }
        _armed = true;
        UpperBody.Install(log, hooks);
        log.Info($"Pose: chained Gob::Animate (footik {On(AnimKnobs.FootIk.V)}, stride {On(AnimKnobs.Stride.V)}, footlock {On(AnimKnobs.FootLock.V)}, lookbody {On(AnimKnobs.LookBody.V)}, lean {AnimKnobs.Lean.V:0}%, aim {On(AnimKnobs.Aim.V)}, hitreact {AnimKnobs.Hit.V:0}%)");
    }

    private static string On(bool b) => b ? "on" : "off";

    private static bool AnyOn => AnimKnobs.FootIk.V || AnimKnobs.Stride.V || AnimKnobs.FootLock.V || AnimKnobs.SyncAttach.V
                                 || AnimKnobs.LookBody.V || AnimKnobs.Lean.V > 0f || AnimKnobs.Aim.V || AnimKnobs.Hit.V > 0f;

    [UnmanagedCallersOnly]
    private static void OnAnimate(IntPtr gob, float dt)
    {
        var next = (delegate* unmanaged<IntPtr, float, void>)(*_next);
        if (!_armed || gob == IntPtr.Zero || *(int*)(gob + GobExternalType) != GobKindCreature) { next(gob, dt); return; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        IntPtr root = *(IntPtr*)(gob + GobPartTree);
        Bones bones = default;
        if (root != IntPtr.Zero) FindBones(root, ref bones, 0);
        bool found = bones.Legs;
        int slot = Find(gob);
        if (slot >= 0)
        {
            ref Slot s = ref _slots[slot];
            if (s.Applied)
            {
                if (s.Root == root) Restore(ref s, bones);
                s.Applied = false;
            }
        }
        IntPtr body = Owner(gob);
        if (body != IntPtr.Zero) { Attached(gob, root, bones, slot, body, next, dt, t0); return; }
        // out of the view volume the engine does not even advance the gob's time: nothing to correct
        found &= *(byte*)(gob + GobInView) != 0 && Near(gob);
        if (slot >= 0 && found && _slots[slot].Root == root) Stride.Pre(ref _slots[slot], gob);

        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        next(gob, dt);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();

        if (found && AnyOn) Correct(gob, root, bones, slot, dt);
        CopyToAttachments(gob, bones);
        _ticks += t1 - t0 + System.Diagnostics.Stopwatch.GetTimestamp() - t2;
    }

    /// <summary>Within <c>anim.distance</c> of the player's creature (always, when the player cannot be
    /// resolved). The player's position is read once per <see cref="PlayerTicks"/>, not per creature.</summary>
    private static bool Near(IntPtr gob)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _playerAt > PlayerTicks)
        {
            _playerAt = now;
            IntPtr me = UpperBody.Ready ? CameraService.PlayerCreature() : IntPtr.Zero;
            _hasPlayer = me != IntPtr.Zero;
            if (_hasPlayer) _player = *(Vector3*)(me + ObjPosition);
        }
        if (!_hasPlayer) return true;
        float d = AnimKnobs.Distance.V;
        return Vector3.DistanceSquared(*(Vector3*)(gob + GobPosition), _player) <= d * d;
    }

    private static Vector3 _player;
    private static bool _hasPlayer;
    private static long _playerAt;
    private static long PlayerTicks => System.Diagnostics.Stopwatch.Frequency / 200;
    private static long _ticks;
    /// <summary>The engine's in-view-volume flag (<c>Gob::IsInViewVolume</c> is <c>return *(this+0x1b)</c>:
    /// Linux @0x444780, Windows rva 0x49110, both disassembled), refreshed by <c>ManageSceneBSP</c>.</summary>
    private const int GobInView = 0x1b;

    /// <summary>The body's corrections, when it is in the world scene and has (or gets) a slot.</summary>
    private static void Correct(IntPtr gob, IntPtr root, in Bones bones, int slot, float dt)
    {
        IntPtr scene = SceneRay.WorldScene();
        if (scene == IntPtr.Zero || *(IntPtr*)(gob + GobScene) != scene) return;
        if (slot < 0 && (slot = Claim(gob)) < 0) { _noSlot++; return; }
        Apply(ref _slots[slot], gob, root, bones, scene, dt);
    }

    /// <summary>The body gob a creature gob is attached to, or zero when it IS the body (or has no creature).
    /// A cloak, robe, tail or wings carries the creature as external data too (<c>CNWCCreature::LoadModel</c>),
    /// and ~85% of robe models have a full set of leg bones, so "has legs" does not tell a body; the creature's
    /// own model does (docs/research/cloak-animation.md §6).</summary>
    private static IntPtr Owner(IntPtr gob)
    {
        if (!_creatures) return IntPtr.Zero;
        IntPtr creature = *(IntPtr*)(gob + GobExternalData);
        if (creature == IntPtr.Zero) return IntPtr.Zero;
        IntPtr body = GobOf(creature);
        return body == gob ? IntPtr.Zero : body;
    }

    /// <summary>An attachment computes nothing of its own. Before it animates, its runs are phase-locked to
    /// the body's (<see cref="Stride.Sync"/>: time, rate times the stride factor, blend). After, it saves its
    /// engine pose and waits for its body (<see cref="Pending"/>): it animates just before the body in the same
    /// frame (<c>Scene::Animate</c> walks backwards), and the body then copies its FINAL rotations onto every
    /// bone name the two share (<see cref="CopyToAttachments"/>). Its skin binds to bones inside its own gob
    /// (<c>PartSkin</c>), so nothing the body does reaches it otherwise. The body's raise is not copied: the
    /// attachment hangs off the body's root part, which carries it already.</summary>
    private static void Attached(IntPtr gob, IntPtr root, in Bones bones, int slot, IntPtr body,
                                 delegate* unmanaged<IntPtr, float, void> next, float dt, long t0)
    {
        int bs = Find(body);
        bool tracked = bs >= 0;
        Stride.Sync(gob, body, ref tracked ? ref _slots[bs] : ref _none, tracked, tracked ? Stride.Of(_slots[bs]) : 1f);
        bool copy = root != IntPtr.Zero && (AnimKnobs.SyncAttach.V || tracked) && *(byte*)(body + GobInView) != 0;
        if (slot < 0 && copy) slot = Claim(gob);

        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        next(gob, dt);
        _ticks += t1 - t0;

        if (!copy || slot < 0) return;
        ref Slot s = ref _slots[slot];
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        s.Stamp = now;
        Save(ref s, root, bones);
        s.Root = root;
        s.Applied = true;
        Pend(body, gob, slot, now);
    }

    /// <summary>Attachments that animated and wait for their body this frame: (body, attachment, its slot,
    /// when). A body consumes its own; an entry older than <see cref="PendingTicks"/> (its body never animated)
    /// is dropped unread, so a stale attachment pointer is never written through.</summary>
    private struct Pending { public IntPtr Body, Gob; public int Slot; public long At; }
    private static Pending[] _pending = [];
    private static int _pendingCount;
    private const int PendingCount = 64;
    private static long PendingTicks => System.Diagnostics.Stopwatch.Frequency / 20;

    private static void Pend(IntPtr body, IntPtr gob, int slot, long now)
    {
        for (int i = _pendingCount - 1; i >= 0; i--)
            if (_pending[i].Gob == gob || now - _pending[i].At > PendingTicks) _pending[i] = _pending[--_pendingCount];
        if (_pendingCount < PendingCount) _pending[_pendingCount++] = new Pending { Body = body, Gob = gob, Slot = slot, At = now };
    }

    /// <summary>After the body's own pose and corrections: each of its waiting attachments takes the body's
    /// local rotation on every bone both have. Only onto a part the attachment saved this frame, from a slot
    /// still its own.</summary>
    private static void CopyToAttachments(IntPtr body, in Bones bones)
    {
        if (_pendingCount == 0) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = _pendingCount - 1; i >= 0; i--)
        {
            Pending p = _pending[i];
            if (p.Body != body) continue;
            _pending[i] = _pending[--_pendingCount];
            if (now - p.At > PendingTicks) continue;
            ref Slot s = ref _slots[p.Slot];
            if (s.Gob != p.Gob || !s.Applied) continue;
            fixed (long* parts = s.Parts)
                for (int k = 0; k < Bones.Count; k++)
                    if (parts[k] != 0 && bones[k] != IntPtr.Zero)
                        *(Vector4*)((IntPtr)parts[k] + PartRot) = *(Vector4*)(bones[k] + PartRot);
            _followed++;
        }
    }

    /// <summary>One pass over the part tree, matching every bone name at once (first match wins).</summary>
    private static void FindBones(IntPtr part, ref Bones bones, int depth)
    {
        if (depth > MaxDepth) return;
        IntPtr node = *(IntPtr*)(part + PartNodeLink);
        int bone = node == IntPtr.Zero ? -1 : BoneIndex((byte*)(node + MdlNodeName));
        if (bone >= 0 && bones[bone] == IntPtr.Zero) bones[bone] = part;
        IntPtr* kids = *(IntPtr**)(part + PartChildren);
        int n = *(int*)(part + PartChildCount);
        for (int i = 0; i < n && kids != null; i++)
            if (kids[i] != IntPtr.Zero) FindBones(kids[i], ref bones, depth + 1);
    }

    /// <summary>0..5 for r-thigh, r-shin, r-foot, l-thigh, l-shin, l-foot, 6 torso, 7 neck, 8 head
    /// (case-insensitive, <c>_g</c> suffix), else -1.</summary>
    internal static int BoneIndex(byte* name)
    {
        if (NameIs(name, "torso_g\0"u8)) return 6;
        if (NameIs(name, "neck_g\0"u8)) return 7;
        if (NameIs(name, "head_g\0"u8)) return 8;
        int side = Lower(name[0]) switch { (byte)'r' => 0, (byte)'l' => 3, _ => -1 };
        if (side < 0) return -1;
        if (NameIs(name + 1, "thigh_g\0"u8)) return side;
        if (NameIs(name + 1, "shin_g\0"u8)) return side + 1;
        if (NameIs(name + 1, "foot_g\0"u8)) return side + 2;
        return -1;
    }

    /// <summary>ASCII case-insensitive compare of a C string against a NUL-terminated literal.</summary>
    private static bool NameIs(byte* s, ReadOnlySpan<byte> lit)
    {
        for (int i = 0; i < lit.Length; i++)
            if (Lower(s[i]) != lit[i]) return false;
        return true;
    }

    private static byte Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c | 0x20) : c;

    /// <summary>True when any animation on the gob's run list is one <see cref="Denied"/> names: the one being
    /// blended from counts as much as the one blended to, so a sit-down or a get-up never gets a correction.
    /// <paramref name="fading"/> reports a run still blending in or out.</summary>
    private static bool Gated(IntPtr gob, out bool fading)
    {
        fading = false;
        IntPtr* runs = *(IntPtr**)(gob + GobAnimRuns);
        int n = *(int*)(gob + GobAnimRunCount);
        bool gated = false;
        for (int i = 0; i < n && runs != null; i++)
        {
            if (runs[i] == IntPtr.Zero) continue;
            if (*(float*)(runs[i] + AnimRunBlend) < Settled) fading = true;
            IntPtr anim = *(IntPtr*)(runs[i] + AnimRunAnimation);
            if (anim != IntPtr.Zero && IsDenied((byte*)(anim + AnimationName))) gated = true;
        }
        return gated;
    }

    internal static bool IsDenied(byte* name)
    {
        ReadOnlySpan<byte> list = Denied;
        while (list.Length > 0)
        {
            int end = list.IndexOf((byte)0);
            ReadOnlySpan<byte> prefix = list[..end];
            int i = 0;
            while (i < prefix.Length && Lower(name[i]) == prefix[i]) i++;
            if (i == prefix.Length) return true;
            list = list[(end + 1)..];
        }
        return false;
    }

    /// <summary>The creature animation ids that walk or run (the chase drivers' test, <see cref="ObjAnimation"/>).</summary>
    internal static bool IsLocomotion(int anim) => anim is 2 or 3 or 4 or 0x4e or 0x4f or >= 0x54 and <= 0x57;

    private static int Hash(IntPtr gob) => (int)(((ulong)gob >> 4 ^ (ulong)gob >> 14) & (SlotCount - 1));

    private static int Find(IntPtr gob)
    {
        int h = Hash(gob);
        for (int i = 0; i < Probe; i++)
            if (_slots[(h + i) & (SlotCount - 1)].Gob == gob) return (h + i) & (SlotCount - 1);
        return -1;
    }

    /// <summary>A free slot in the probe window, else one untouched for <see cref="StaleTicks"/>; -1 = the gob
    /// goes uncorrected this frame (it has nothing to restore, so skipping is always safe).</summary>
    private static int Claim(IntPtr gob)
    {
        int h = Hash(gob);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < Probe; i++)
        {
            ref Slot s = ref _slots[(h + i) & (SlotCount - 1)];
            if (s.Gob != IntPtr.Zero && now - s.Stamp < StaleTicks) continue;
            s = default;
            s.Gob = gob;
            s.Stamp = now;
            s.RateF = 1f;
            return (h + i) & (SlotCount - 1);
        }
        return -1;
    }

    /// <summary>The root first and on its own: it is the one part no animation keys, so leaving it shifted is the
    /// one mistake that would compound. Each rotation only onto the part it was saved from and found again this
    /// frame: a part the tree no longer reaches may already be freed.</summary>
    private static void Restore(ref Slot s, in Bones bones)
    {
        *(Vector3*)(s.Root + PartPos) = s.RootPos;
        fixed (float* r = s.Rot)
        fixed (long* p = s.Parts)
            for (int i = 0; i < Bones.Count; i++)
                if (bones[i] != IntPtr.Zero && bones[i] == (IntPtr)p[i]) *(Vector4*)(bones[i] + PartRot) = *(Vector4*)(r + 4 * i);
    }

    private static void Save(ref Slot s, IntPtr root, in Bones bones)
    {
        s.RootPos = *(Vector3*)(root + PartPos);
        fixed (float* r = s.Rot)
        fixed (long* p = s.Parts)
            for (int i = 0; i < Bones.Count; i++)
            {
                p[i] = (long)bones[i];
                if (bones[i] != IntPtr.Zero) *(Vector4*)(r + 4 * i) = *(Vector4*)(bones[i] + PartRot);
            }
    }

    private static void Apply(ref Slot s, IntPtr gob, IntPtr root, in Bones bones, IntPtr scene, float dt)
    {
        s.Stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        float scale = _scale(gob);
        if (!(scale > 1e-4f)) return;
        World(root, out Vector3 rootW, out Quaternion rootWq);
        Vector3 rootL = *(Vector3*)(root + PartPos);
        Quaternion adj = rootWq * Quaternion.Conjugate(Local(root));
        Vector3 adjPos = rootW - Vector3.Transform(rootL * scale, adj);
        Vector3 fwd = Vector3.Transform(Vector3.UnitY, adj);
        Vector3 fwdH = new(fwd.X, fwd.Y, 0f);
        fwdH = fwdH.LengthSquared() > 1e-8f ? Vector3.Normalize(fwdH) : Vector3.UnitY;
        Vector3 right = new(fwdH.Y, -fwdH.X, 0f);

        bool snap = s.Root != root || Vector3.DistanceSquared(adjPos, s.LastPos) > Teleport * Teleport;
        Vector3 moved = snap ? Vector3.Zero : adjPos - s.LastPos;
        float weight = FootPlacement.Weight(ref s.Grade, ref s.Moving, moved, dt, scale);
        Motion(ref s, moved, fwdH, snap, dt);
        s.Fwd = fwdH; s.Right = right;
        s.LastPos = adjPos;
        s.Root = root;

        bool gated = Gated(gob, out bool fading);
        if (gated) _gated++;
        IntPtr creature = _creatures ? *(IntPtr*)(gob + GobExternalData) : IntPtr.Zero;
        s.Loco = !gated && s.Moving > 0f && creature != IntPtr.Zero && IsLocomotion(*(short*)(creature + ObjAnimation));

        World(bones.RFoot, out Vector3 rAnkle, out _);
        World(bones.LFoot, out Vector3 lAnkle, out _);
        Quaternion toModel = Quaternion.Conjugate(adj);
        Vector3 mr = Vector3.Transform(rAnkle - adjPos, toModel) / scale, ml = Vector3.Transform(lAnkle - adjPos, toModel) / scale;
        Stride.Measure(ref s, mr, ml, s.Speed / scale, fading, dt);

        FootPlacement.Compute(ref s, scene, gob, rAnkle, lAnkle, adjPos.Z, fwdH, right, scale, weight, gated, snap, dt, out var feet);
        Stride.Lock(ref s, rAnkle, lAnkle, mr.Z, ml.Z, s.Loco, snap, scale, dt, out Vector3 lockR, out Vector3 lockL);
        UpperBody.Step(ref s, creature, rootW, rootW.Z - adjPos.Z, scale, s.Speed, gated, snap, dt);
        UpperBody.Rotations(s, gated, out Vector2 torsoRot, out Vector2 neckRot);

        float body = MathF.Min(feet.OffR, feet.OffL);
        bool legs = !gated && (MathF.Abs(feet.OffR - body) + MathF.Abs(feet.OffL - body) > FootPlacement.MinLift
                               || !feet.TiltR.IsIdentity || !feet.TiltL.IsIdentity
                               || lockR.LengthSquared() + lockL.LengthSquared() > FootPlacement.MinLift * FootPlacement.MinLift);
        bool upper = bones.Torso != IntPtr.Zero && torsoRot.LengthSquared() + neckRot.LengthSquared() > 1e-8f;
        bool look = UpperBody.LookStep(ref s, bones, !gated && AnimKnobs.LookBody.V && LookingAt(gob), snap, dt);
        if (MathF.Abs(body) < FootPlacement.MinLift && !legs && !upper && !look) return;

        Save(ref s, root, bones);
        s.Applied = true;
        _gobs++;
        *(Vector3*)(root + PartPos) = rootL + Vector3.Transform(new Vector3(0f, 0f, body / scale), toModel);
        if (legs)
        {
            float cap = AnimKnobs.Lift.V * scale;
            Leg(bones.RThigh, bones.RShin, bones.RFoot, lockR + new Vector3(0f, 0f, MathF.Min(feet.OffR - body, cap)), fwd, feet.TiltR);
            Leg(bones.LThigh, bones.LShin, bones.LFoot, lockL + new Vector3(0f, 0f, MathF.Min(feet.OffL - body, cap)), fwd, feet.TiltL);
        }
        if (look) UpperBody.ShareLook(bones, s.Look.X, s.Look.Y);
        if (upper) UpperBody.Write(bones.Torso, bones.Neck, fwdH, right, torsoRot, neckRot);
    }

    /// <summary>The engine's live look-at on this gob: <c>Gob+0x1b0</c> holds the <c>LookAtAnimation</c>
    /// (<c>BeginLookAtAnimate</c> stores it, <c>EndLookAtAnimate</c> reads it there: Linux @0x4979e0, Windows
    /// rva 0x50eb0, both disassembled), not yet finished. An idle animation's own head tilt is not a look-at and
    /// is left on the head.</summary>
    private static bool LookingAt(IntPtr gob)
    {
        IntPtr look = *(IntPtr*)(gob + GobLookAt);
        return look != IntPtr.Zero && *(byte*)(look + LookAtFinished) == 0;
    }

    private const int GobLookAt = 0x1b0;

    /// <summary>Eases the measured motion: ground speed, yaw rate and signed slope over <see cref="MotionTime"/>,
    /// forward acceleration over <see cref="AccelTime"/>. A snap zeroes the rates rather than reading a teleport as a sprint.</summary>
    internal static void Motion(ref Slot s, Vector3 moved, Vector3 fwdH, bool snap, float dt)
    {
        float yaw = MathF.Atan2(fwdH.Y, fwdH.X);
        if (snap || !(dt > 0f)) { s.Yaw = yaw; s.YawRate = s.Accel = s.Speed = s.Slope = 0f; return; }
        float a = MathF.Min(1f, dt / MotionTime);
        float run = MathF.Sqrt(moved.X * moved.X + moved.Y * moved.Y);
        float speed = s.Speed + (run / dt - s.Speed) * a;
        s.Accel += ((speed - s.Speed) / dt - s.Accel) * MathF.Min(1f, dt / AccelTime);
        s.Speed = speed;
        float turn = yaw - s.Yaw;
        if (turn > MathF.PI) turn -= 2f * MathF.PI; else if (turn < -MathF.PI) turn += 2f * MathF.PI;
        s.YawRate += (turn / dt - s.YawRate) * a;
        s.Yaw = yaw;
        if (run > 1e-4f) s.Slope += (moved.Z / run - s.Slope) * a;
    }

    /// <summary>Starts a flinch on <paramref name="gob"/>: <paramref name="peak"/> radians toward
    /// <paramref name="push"/>. Only a gob the layer already corrects has a slot; any other (no humanoid legs,
    /// not in the world scene) is not one the flinch could be drawn on.</summary>
    internal static void Kick(IntPtr gob, Vector3 push, float peak)
    {
        if (!_armed) return;
        int slot = Find(gob);
        if (slot < 0) return;
        ref Slot s = ref _slots[slot];
        s.HitV += UpperBody.Launch(push, s.Fwd, s.Right, peak);
    }

    /// <summary>A creature's Gob, through the engine's own accessor: <c>GetModel(0xff, 1)</c> forwards to the
    /// CNWCAnimBase at +0x78 (as <see cref="HeadTrackService"/> reaches it).</summary>
    internal static IntPtr GobOf(IntPtr creature)
    {
        IntPtr vtbl = *(IntPtr*)creature;
        if (vtbl == IntPtr.Zero) return IntPtr.Zero;
        return ((delegate* unmanaged<IntPtr, byte, int, IntPtr>)(*(IntPtr*)(vtbl + CreatureGetModelSlot)))(creature, 0xff, 1);
    }

    /// <summary>Moves one ankle by <paramref name="delta"/> world units: two-bone IK in world space, written back as
    /// locals. The foot keeps its animated world orientation, turned by <paramref name="tilt"/> about the ankle.
    /// A chain whose parents are not thigh-&gt;shin-&gt;foot is left alone.</summary>
    private static void Leg(IntPtr thigh, IntPtr shin, IntPtr foot, Vector3 delta, Vector3 fwd, Quaternion tilt)
    {
        bool moves = delta.LengthSquared() >= FootPlacement.MinLift * FootPlacement.MinLift;
        if (!moves && tilt.IsIdentity) return;
        IntPtr hip = *(IntPtr*)(thigh + PartParent);
        if (hip == IntPtr.Zero || *(IntPtr*)(shin + PartParent) != thigh || *(IntPtr*)(foot + PartParent) != shin) return;
        World(shin, out Vector3 k, out Quaternion qs);
        World(foot, out Vector3 a, out Quaternion qf);
        if (moves)
        {
            World(hip, out _, out Quaternion qp);
            World(thigh, out Vector3 h, out Quaternion qt);
            if (TwoBone(h, k, a, a + delta, fwd, out Quaternion r1, out Quaternion r2))
            {
                Quaternion qt2 = Quaternion.Normalize(r1 * qt);
                qs = Quaternion.Normalize(r2 * r1 * qs);
                SetLocal(thigh, Quaternion.Conjugate(qp) * qt2);
                SetLocal(shin, Quaternion.Conjugate(qt2) * qs);
            }
        }
        SetLocal(foot, Quaternion.Conjugate(qs) * tilt * qf);
    }

    /// <summary>The period clause: corrected gobs, casts, what the casts landed on, the gobs refused (an
    /// animation gate, or no slot), and each module's count. Resets its counters -- one caller, once per period.</summary>
    internal static string Stats(int frames)
    {
        if (!_armed || frames <= 0) return "";
        float f = frames;
        string s = FormattableString.Invariant(
            $"pose {System.Diagnostics.Stopwatch.GetElapsedTime(0, _ticks).TotalMilliseconds / f:F2} ms/f, {_gobs / f:F1} gobs/f, {FootPlacement.Rays / f:F1} rays/f, hits {FootPlacement.Tile / f:F1} tile + {FootPlacement.Placeable / f:F1} placeable/f, refused {_gated / f:F1} gated + {_noSlot / f:F1} no slot/f, {FootPlacement.Stairs / f:F1} on stairs/f, {Stride.Strided / f:F1} strided, {Stride.Synced / f:F1} attachment runs synced + {_followed / f:F1} attachments posed/f, + {Stride.Locked / f:F1} feet locked/f, {UpperBody.Looked / f:F1} looking/f, {UpperBody.Aimed / f:F1} aiming/f, {UpperBody.Hits} flinches");
        _gobs = _gated = _noSlot = _followed = 0;
        _ticks = 0;
        FootPlacement.Rays = FootPlacement.Tile = FootPlacement.Placeable = FootPlacement.Stairs = 0;
        Stride.Strided = Stride.Locked = Stride.Synced = UpperBody.Looked = UpperBody.Aimed = UpperBody.Hits = 0;
        return s;
    }

    /// <summary>The group's status line.</summary>
    internal static string Report() => !_armed ? "pose layer unhooked" : "pose layer on";

    /// <summary>The pure parts off-device: bone names, the animation gate, the springs, the two-bone solve, the
    /// stride factor, the foot lock, and the signs of lean, aim and flinch.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "Pose.SelfCheck");
        fixed (byte* a = "RThigh_g\0"u8, b = "lfoot_g\0"u8, c = "rthigh_g01\0"u8, d = "Torso_g\0"u8, e = "neck_g\0"u8, g = "head_g\0"u8)
            check.Expect(BoneIndex(a) == 0 && BoneIndex(b) == 5 && BoneIndex(c) == -1 && BoneIndex(d) == 6 && BoneIndex(e) == 7 && BoneIndex(g) == 8,
                         "bone names match whole and case-insensitive");
        fixed (byte* a = "sitdown\0"u8, b = "ckdbckps\0"u8, c = "pause1\0"u8, d = "cwalkf\0"u8, e = "gustandb\0"u8, f = "greeting\0"u8)
            check.Expect(IsDenied(a) && IsDenied(b) && IsDenied(e) && !IsDenied(c) && !IsDenied(d) && !IsDenied(f),
                         "sitting, knockdown and getting up are gated; standing, walking and greeting are not");

        float x = 0f, v = 0f, peak = 0f;
        for (int i = 0; i < 120; i++) { Smooth(ref x, ref v, 0.3f, 1f / 60f, 0.12f); peak = MathF.Max(peak, x); }
        float y = 0f, w = 0f;
        Smooth(ref y, ref w, 0.3f, 0.5f, 0.12f);
        check.Expect(MathF.Abs(x - 0.3f) < 1e-3f && peak <= 0.3f + 1e-4f && y > 0f && y <= 0.3f,
                     "the spring settles on its target without overshoot, and a hitch-sized dt stays bounded");

        Vector3 h = new(0f, 0f, 1f), k = new(0f, 0.05f, 0.5f), an = new(0f, 0f, 0f), t = new(0f, 0f, 0.2f);
        bool ok = TwoBone(h, k, an, t, Vector3.UnitY, out Quaternion r1, out Quaternion r2);
        Vector3 k2 = h + Vector3.Transform(k - h, r1);
        Vector3 a2 = k2 + Vector3.Transform(Vector3.Transform(an - k, r1), r2);
        check.Expect(ok && Vector3.Distance(a2, t) < 1e-3f, "the ankle reaches the lifted target");
        check.Expect(MathF.Abs(Vector3.Distance(h, k2) - Vector3.Distance(h, k)) < 1e-4f
                     && MathF.Abs(Vector3.Distance(k2, a2) - Vector3.Distance(k, an)) < 1e-4f, "bone lengths are kept");
        check.Expect(k2.Y > k.Y, "the knee bends forward");
        ok = TwoBone(h, new Vector3(0.05f, 0f, 0.5f), an, t, Vector3.UnitY, out r1, out _);
        Vector3 k3 = h + Vector3.Transform(new Vector3(0.05f, 0f, -0.5f), r1);
        check.Expect(ok && k3.Y > 0.1f && MathF.Abs(k3.X) < 0.06f, "a leg splayed sideways still bends its knee forward, not further out");
        ok = TwoBone(h, new Vector3(0f, 0f, 0.5f), an, t, Vector3.UnitY, out r1, out _);
        check.Expect(ok && Vector3.Transform(new Vector3(0f, 0f, -0.5f), r1).Y > 0.1f, "a straight leg bends its knee forward");
        t = new Vector3(0.1f, 0.15f, 0.05f);
        ok = TwoBone(h, k, an, t, Vector3.UnitY, out r1, out r2);
        k2 = h + Vector3.Transform(k - h, r1);
        a2 = k2 + Vector3.Transform(Vector3.Transform(an - k, r1), r2);
        check.Expect(ok && Vector3.Distance(a2, t) < 1e-3f, "the ankle reaches a target moved sideways and forward (a locked foot)");
        Vector3 kx = new(0.12f, 0.03f, 0.5f);
        ok = TwoBone(h, kx, an, an, Vector3.UnitY, out r1, out r2);
        bool still = ok && Vector3.Distance(h + Vector3.Transform(kx - h, r1), kx) < 1e-4f;
        TwoBone(h, kx, an, an + new Vector3(0f, 0.002f, 0f), Vector3.UnitY, out r1, out _);
        Vector3 kSmall = h + Vector3.Transform(kx - h, r1);
        check.Expect(still && Vector3.Distance(kSmall, kx) < 0.01f,
                     "a knee bent sideways (legs crossing) stays in its own plane: no move returns it exactly, a tiny move barely shifts it");
        check.Expect(Soften(0.5f, 0.6f, 1f) == 0.5f && Soften(0.99f, 0.8f, 1f) < 0.99f && Soften(5f, 0.8f, 1f) <= 1f
                     && Soften(0.97f, 0.97f, 1f) == 0.97f,
                     "soft IK eases a stretch into full reach, never past it, and leaves an unmoved ankle alone");

        float gr = 0f, mv = 0f, dt60 = 1f / 60f, wUp = 1f, wFlat = 0f, wStop = 0f;
        bool steady = true;
        for (int i = 0; i < 30; i++)
        {
            wUp = FootPlacement.Weight(ref gr, ref mv, (i & 1) == 0 ? new Vector3(0.08f, 0f, 0.05f) : Vector3.Zero, dt60, 1f);
            if (i > 10 && wUp != FootPlacement.StairWeight) steady = false;
        }
        for (int i = 0; i < 20; i++) wStop = FootPlacement.Weight(ref gr, ref mv, Vector3.Zero, dt60, 1f);
        float gf = 0f, mf = 0f;
        for (int i = 0; i < 30; i++) wFlat = FootPlacement.Weight(ref gf, ref mf, new Vector3(0.04f, 0f, 0f), dt60, 1f);
        check.Expect(steady && wStop == 1f && wFlat == 1f,
                     "walking a stair-steep walkmesh (position advancing every other frame) holds the stairs weight without flicker; stopping on it, or walking a flat platform, is full placement");
        Vector3 toe = Vector3.Transform(Vector3.UnitY, FootPlacement.FootTilt(Vector3.UnitY, Vector3.UnitX, 0.5f, 0f));
        Vector3 sole = Vector3.Transform(Vector3.UnitX, FootPlacement.FootTilt(Vector3.UnitY, Vector3.UnitX, 0f, -0.3f));
        check.Expect(MathF.Abs(toe.Z / toe.Y - 0.5f) < 1e-3f && MathF.Abs(sole.Z / sole.X + 0.3f) < 1e-3f
                     && FootPlacement.FootTilt(Vector3.UnitY, Vector3.UnitX, 0f, 0f).IsIdentity,
                     "a tilted foot lies on the slope: toe up a rise, sideways down a fall, untouched on the flat");

        check.Expect(MathF.Abs(Stride.Factor(2.0f, 1.58f) - 2.0f / 1.58f) < 1e-4f && Stride.Factor(1.6f, 1.58f) == 1f
                     && Stride.Factor(5f, 1.58f) == 1f && Stride.Factor(2f, 0.05f) == 1f,
                     "stride: a player walking 2.0 m/s on a 1.58 m/s cycle plays it 1.27x; inside the deadband, out of range or unmeasured is 1");
        Vector3 lockAt = default;
        bool held = false, slipped = false;
        Vector2 ld = Vector2.Zero, lv = Vector2.Zero;
        Vector3 d0 = Stride.Foot(ref lockAt, ref held, ref slipped, ref ld, ref lv, new Vector3(1f, 1f, 0f), true, false, 0.2f, dt60);
        Vector3 dh = d0;
        for (int i = 1; i <= 30; i++)
            dh = Stride.Foot(ref lockAt, ref held, ref slipped, ref ld, ref lv, new Vector3(1f + 0.002f * i, 1f, 0f), true, false, 0.2f, dt60);
        Vector3 first = Stride.Foot(ref lockAt, ref held, ref slipped, ref ld, ref lv, new Vector3(1.4f, 1f, 0f), true, false, 0.2f, dt60);
        Vector3 dr = first;
        for (int i = 0; i < 60; i++)
            dr = Stride.Foot(ref lockAt, ref held, ref slipped, ref ld, ref lv, new Vector3(1.4f, 1f, 0f), true, false, 0.2f, dt60);
        check.Expect(d0 == Vector3.Zero && MathF.Abs(dh.X + 0.06f) < 0.015f && slipped && MathF.Abs(first.X) < 0.07f
                     && MathF.Abs(dr.X) < 0.01f,
                     "foot lock: a planted foot holds where it landed, lets go past the slip without a jump, and eases back without re-locking");

        Vector3 turned = Vector3.Transform(Vector3.UnitY, UpperBody.Turn(Vector3.UnitZ, Vector3.UnitX, 0.3f, 0.2f));
        check.Expect(turned.X < -0.2f && turned.Z > 0.1f,
                     "look share: a positive yaw turns the chest left (as the head's own yaw reads), a positive pitch raises it");
        Vector2 left = UpperBody.LeanTarget(4f, 1f, 0f, 0f, true), speedUp = UpperBody.LeanTarget(2f, 0f, 3f, 0f, true);
        Vector3 topL = Vector3.Transform(Vector3.UnitZ, Tip(Vector3.UnitX, Vector3.UnitY, left.X, left.Y));
        Vector3 topA = Vector3.Transform(Vector3.UnitZ, Tip(Vector3.UnitX, Vector3.UnitY, speedUp.X, speedUp.Y));
        check.Expect(topL.X < -0.05f && topA.Y > 0.03f, "lean: a left turn tips the top left, speeding up tips it forward");
        Vector3 chest = Vector3.Transform(Vector3.UnitY, Tip(Vector3.UnitX, Vector3.UnitY, 20f * MathF.PI / 180f, 0f));
        check.Expect(chest.Z > 0.3f, "aim: a positive pitch (target above) raises the chest's forward");
        Vector2 hx = Vector2.Zero, hv = UpperBody.Launch(-Vector3.UnitY, Vector3.UnitY, Vector3.UnitX, 0.14f);
        float hitPeak = 0f;
        for (int i = 0; i < 120; i++)
        {
            Wobble(ref hx, ref hv, 2f * MathF.PI * 2.5f, 0.4f, dt60);
            Vector3 top = Vector3.Transform(Vector3.UnitZ, Tip(Vector3.UnitX, Vector3.UnitY, hx.X, hx.Y));
            if (i == 5 && !(top.Y < 0f)) hitPeak = -1f;
            if (hitPeak >= 0f) hitPeak = MathF.Max(hitPeak, hx.Length());
        }
        check.Expect(hitPeak > 0.12f && hitPeak < 0.16f && hx.Length() < 0.01f,
                     "flinch: a blow from the front tips the top back, peaks at its magnitude and settles");
        log.Info(check.Ok ? "Pose.SelfCheck: passed" : "Pose.SelfCheck: FAILED");
    }
}
