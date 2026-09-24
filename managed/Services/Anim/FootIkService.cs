using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Services.Camera;
using static CX.Framework.EngineLayout;

namespace CX.Services.Anim;

/// <summary>The <c>anim</c> group (CX-29): foot placement. Read hot by <see cref="FootIkService"/>.</summary>
internal static class FootIk
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<float> Max;
    internal static readonly Knob<float> Tilt;
    internal static readonly Knob<float> Lift;

    static FootIk()
    {
        G = KnobGroup.Declare("anim", "Graphics", "Animation",
            header: "No master: foot placement is one toggle beside its limit. Client-side only: other players see nothing different, and a client without CX looks exactly as before.");
        Enabled = G.Toggle("footik", "Stand on uneven floors", true,
            help: "Characters stand on the floor that is drawn -- raised platforms, rugs, decks, stairs and sloped tiles -- instead of the flat walkmesh under it: the body is raised or lowered and each leg bends to its own foothold. Sitting, lying, knockdown and death are left alone.");
        Max = G.Slider("footik_max", "Largest step", 0.1f, 1.5f, 0.05f, 0.5f, "m",
            help: "How far above or below the walkmesh a floor may be and still count. A table the character walks past is higher than this and is ignored.");
        Tilt = G.Slider("footik_tilt", "Foot tilt", 0f, 45f, 5f, 30f, "°",
            help: "The steepest slope a foot turns to lie flat on, so a sole follows a ramp or a sloped tile instead of cutting into it. 0 keeps the foot as animated.");
        Lift = G.Slider("footik_lift", "Highest knee lift", 0.05f, 0.6f, 0.05f, 0.25f, "m",
            help: "How much higher one foot may be set than the other. A steep step past this leaves the foot in the step rather than folding the leg up to it.");
        G.Status = FootIkService.Report;
    }
}

/// <summary>
/// Foot placement on uneven floors (CX-29). Chains <c>Gob::Animate</c>: after the engine has posed a creature, each
/// foot casts one segment down through <c>Scene::HitCheck</c> (<see cref="SceneRay"/>: tile render triangles, then
/// placeable render triangles) over <c>[rendered height +- footik_max]</c>; the root part's local Z takes the lower
/// foot's offset, and an analytic two-bone IK lifts the other ankle by the difference, knee bent toward the
/// character's forward. Offsets are critically damped, so a step onto a platform edge eases in.
/// <para><b>The pose is restored before the engine animates.</b> A crossfade blends FROM the part's current
/// local value (<c>NewController::Control</c>), and an unkeyed part (the root, usually) keeps whatever it holds,
/// so a correction left in place would leak into the next crossfade and accumulate on unkeyed parts. Each
/// creature gob that is corrected owns a slot holding the engine's own values; nothing is ever written to a gob
/// without one.</para>
/// <para><b>Reference height</b> is the rendered one: the root part's world position minus its own local offset,
/// i.e. <c>Gob::GetAdjustedPosition</c> including a script's <c>SetObjectVisualTransform</c> translation. A
/// character a script already raised onto the platform measures an offset near 0.</para>
/// <para><b>Leg chains</b> are <c>{r,l}thigh_g -&gt; {r,l}shin_g -&gt; {r,l}foot_g</c>, the names FindPart would
/// match (case-insensitive): every stock humanoid (a_ba and its descendants, 257 of 663 stock a_/c_ models)
/// carries all six. Quadrupeds, dragons, giants and the like name their legs otherwise and are skipped.</para>
/// <para><b>Foot tilt</b> (<c>anim.footik_tilt</c>) measures the slope itself: in triangle mode
/// <c>PartTriMesh::HitCheckGeom</c> clears the hit normal (<c>CHitInfo+0x5c</c> = 0) after <c>polyhit</c>, so the
/// engine hands back none. Each foot keeps two slopes (toward the toe, and sideways), each from one short cast
/// <see cref="ProbeReach"/> off the foothold; one of the four is re-cast per frame, so tilt costs one cast per
/// creature per frame, and the springs hide the staleness.</para>
/// <para><b>The gate is a deny list</b>, not the allow list the ticket words: every stock stance/move/attack name
/// would have to be listed, and module-added animations mostly arrive as <c>custom*</c>, which is denied.</para>
/// <para><b>Hot.</b> Once per creature per frame: pointer reads, a part-tree walk, two to seven HitCheck casts, a
/// few quaternion products, no allocation.</para>
/// </summary>
public sealed unsafe class FootIkService : IExtenderService
{
    private static ExtenderLog? _log;
    private static IntPtr* _next;
    private static delegate* unmanaged<IntPtr, Vector3*, float*, void> _getWorld;
    private static delegate* unmanaged<IntPtr, float> _scale;
    private static bool _armed;

    /// <summary>Casts per foot: the first may land on a creature, a held item or a VFX, which are not ground.</summary>
    private const int MaxCasts = 3;
    /// <summary>Seconds the offset springs take to settle (critically damped).</summary>
    private const float SmoothTime = 0.12f;
    /// <summary>World units between two frames past which the gob teleported: springs snap, nothing eases.</summary>
    private const float Teleport = 2f;
    /// <summary>Ankle lifts below this are no lift: the IK is skipped.</summary>
    private const float MinLift = 1e-4f;
    /// <summary>World units (times the gob's scale) from a foothold to the cast that measures its slope: about
    /// half a sole, so a stair riser is past it rather than read as a slope.</summary>
    private const float ProbeReach = 0.1f;
    /// <summary>Walkmesh rise per unit of travel past which a MOVING character is on stairs (or a ramp that
    /// steep): foot placement drops to <see cref="StairWeight"/> there. The walk cycle is wide and runs at full
    /// speed up a steep flight, so no foothold per frame matches it.</summary>
    private const float StairGrade = 0.2f;
    /// <summary>Foot placement's strength while moving on stairs: halfway between vanilla's feet-on-the-ramp and
    /// full placement, which is what live testing liked (a flickering full fade averaged to it).</summary>
    private const float StairWeight = 0.5f;
    /// <summary>World units per second (times scale) above which a character is moving, not shuffling.</summary>
    private const float MoveSpeed = 0.5f;
    /// <summary>Seconds a character still counts as moving after a frame without travel: the engine does not
    /// advance a walking gob's position every frame, and a gate that trusts one frame flickers.</summary>
    private const float MoveHold = 0.2f;
    /// <summary>Seconds of travel the measured grade is averaged over: one frame's step is noise.</summary>
    private const float GradeTime = 0.2f;
    /// <summary>Seconds the stairs fade takes, out and back in.</summary>
    private const float FadeTime = 0.25f;
    /// <summary>Part-tree recursion guard: a cycle in a torn tree must not blow the stack. Real trees are ~6 deep.</summary>
    private const int MaxDepth = 64;

    /// <summary>Animations whose name starts with one of these (NUL-separated) get no correction: sitting,
    /// lying, knockdown and getting up, death, kneeling emotes, mounting, flying in/out and the module-defined
    /// customs. Every stock creature animation was listed to pick them (the census in the CX-29 notes).</summary>
    private static ReadOnlySpan<byte> Denied =>
        "sit\0standup\0kd\0ckd\0dead\0cdead\0die\0gu\0cgu\0getlow\0getmid\0cgetmid\0kneel\0meditate\0worship\0mount\0custom\0appear\0cappear\0disappear\0cdisappear\0"u8;

    /// <summary>The six parts of a leg pair, found by name each frame (equipment swaps rebuild parts).</summary>
    private struct Legs
    {
        public IntPtr RThigh, RShin, RFoot, LThigh, LShin, LFoot;
        /// <summary>By <see cref="LegIndex"/>: r-thigh, r-shin, r-foot, l-thigh, l-shin, l-foot.</summary>
        public IntPtr this[int i]
        {
            readonly get => i switch { 0 => RThigh, 1 => RShin, 2 => RFoot, 3 => LThigh, 4 => LShin, _ => LFoot };
            set { switch (i) { case 0: RThigh = value; break; case 1: RShin = value; break; case 2: RFoot = value; break;
                               case 3: LThigh = value; break; case 4: LShin = value; break; default: LFoot = value; break; } }
        }
        public readonly bool Complete => RThigh != 0 && RShin != 0 && RFoot != 0 && LThigh != 0 && LShin != 0 && LFoot != 0;
    }

    /// <summary>One corrected gob: the engine's own values for everything written (root pos + six rotations,
    /// w-first) and the offset springs. Keyed by gob AND root part so a recycled address never restores.</summary>
    private struct Slot
    {
        public IntPtr Gob, Root;
        public long Stamp;
        public bool Applied;
        public Vector3 RootPos, LastPos;
        public float OffR, VelR, OffL, VelL;
        /// <summary>Floor slopes (rise per unit) under each foot, toward the toe (F) and to the right (S), with
        /// their spring velocities; <see cref="Phase"/> picks the one re-cast this frame.</summary>
        public float FR, VFR, SR, VSR, FL, VFL, SL, VSL;
        public int Phase;
        /// <summary>Walkmesh grade under the character (rise per unit of travel, averaged), and the correction's
        /// weight with its spring velocity: 0 while moving on stairs, 1 otherwise.</summary>
        public float Grade, W, VW, Moving;
        /// <summary>Six w-first rotations, in <see cref="LegIndex"/> order (4 floats each).</summary>
        public fixed float Rot[24];
        /// <summary>The six parts those rotations came from: a rotation is restored only onto the same part.</summary>
        public fixed long Parts[6];
    }

    /// <summary>Open-addressed, <see cref="Probe"/> slots per lookup. ponytail: a gob that leaves the scene
    /// without dying keeps its slot until <see cref="StaleTicks"/>, and one evicted after that and later re-added
    /// carries its last correction as a fixed offset -- the upgrade is a <c>Gob::~Gob</c> death notice.</summary>
    private static Slot[] _slots = [];
    private const int SlotCount = 1024, Probe = 8;
    private static long StaleTicks => System.Diagnostics.Stopwatch.Frequency * 30;

    private static int _gobs, _rays, _tile, _placeable, _gated, _stairs, _noSlot;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _ = FootIk.G;
        SelfCheck(log);
        if (!EngineLayout.Require(log, "FootIk", "Gob", "Part", "MdlNode", "AnimRun", "Animation", "CHitInfo", "Scene")) return;
        if (!SymbolMap.Require(log, "FootIk", SymbolMap.GobAnimate, SymbolMap.PartGetWorld, SymbolMap.GobGetAdjustedScale,
                               SymbolMap.SceneHitCheck, SymbolMap.HitInfoReset, SymbolMap.CurrentScene)) return;
        SceneRay.Init();
        _getWorld = (delegate* unmanaged<IntPtr, Vector3*, float*, void>)Native.Sym(SymbolMap.PartGetWorld);
        _scale = (delegate* unmanaged<IntPtr, float>)Native.Sym(SymbolMap.GobGetAdjustedScale);
        if (!SceneRay.Armed || _getWorld == null || _scale == null) { log.Info("FootIk: standing down (symbols unresolved)"); return; }
        _slots = new Slot[SlotCount];
        _next = hooks.Chain(SymbolMap.GobAnimate, (IntPtr)(delegate* unmanaged<IntPtr, float, void>)&OnAnimate, 50);
        if (_next == null) { log.Info("FootIk: standing down (Gob::Animate chain refused)"); return; }
        _armed = true;
        log.Info($"FootIk: chained Gob::Animate (anim.footik {(FootIk.Enabled.V ? "on" : "off")}, max {FootIk.Max.V:0.00})");
    }

    [UnmanagedCallersOnly]
    private static void OnAnimate(IntPtr gob, float dt)
    {
        var next = (delegate* unmanaged<IntPtr, float, void>)(*_next);
        if (!_armed || gob == IntPtr.Zero || *(int*)(gob + GobExternalType) != GobKindCreature) { next(gob, dt); return; }

        IntPtr root = *(IntPtr*)(gob + GobPartTree);
        Legs legs = default;
        if (root != IntPtr.Zero) FindLegs(root, ref legs, 0);
        bool found = legs.Complete;
        int slot = Find(gob);
        if (slot >= 0 && _slots[slot].Applied)
        {
            ref Slot s = ref _slots[slot];
            if (s.Root == root) Restore(ref s, legs);
            s.Applied = false;
        }

        next(gob, dt);

        if (!FootIk.Enabled.V || !found) return;
        IntPtr scene = SceneRay.WorldScene();
        if (scene == IntPtr.Zero || *(IntPtr*)(gob + GobScene) != scene) return;
        if (slot < 0 && (slot = Claim(gob)) < 0) { _noSlot++; return; }
        Apply(ref _slots[slot], gob, root, legs, scene, dt);
    }

    /// <summary>One pass over the part tree, matching all six leg names at once (first match wins).</summary>
    private static void FindLegs(IntPtr part, ref Legs legs, int depth)
    {
        if (depth > MaxDepth) return;
        IntPtr node = *(IntPtr*)(part + PartNodeLink);
        int leg = node == IntPtr.Zero ? -1 : LegIndex((byte*)(node + MdlNodeName));
        if (leg >= 0 && legs[leg] == IntPtr.Zero) legs[leg] = part;
        IntPtr* kids = *(IntPtr**)(part + PartChildren);
        int n = *(int*)(part + PartChildCount);
        for (int i = 0; i < n && kids != null; i++)
            if (kids[i] != IntPtr.Zero) FindLegs(kids[i], ref legs, depth + 1);
    }

    /// <summary>0..5 for r-thigh, r-shin, r-foot, l-thigh, l-shin, l-foot (case-insensitive, <c>_g</c> suffix), else -1.</summary>
    internal static int LegIndex(byte* name)
    {
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
    /// blended from counts as much as the one blended to, so a sit-down or a get-up never gets a correction.</summary>
    private static bool Gated(IntPtr gob)
    {
        IntPtr* runs = *(IntPtr**)(gob + GobAnimRuns);
        int n = *(int*)(gob + GobAnimRunCount);
        for (int i = 0; i < n && runs != null; i++)
        {
            if (runs[i] == IntPtr.Zero) continue;
            IntPtr anim = *(IntPtr*)(runs[i] + AnimRunAnimation);
            if (anim != IntPtr.Zero && IsDenied((byte*)(anim + AnimationName))) return true;
        }
        return false;
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
            return (h + i) & (SlotCount - 1);
        }
        return -1;
    }

    /// <summary>The root first and on its own: it is the one part no animation keys, so leaving it shifted is the
    /// one mistake that would compound. Each rotation only onto the part it was saved from and found again this
    /// frame: a part the tree no longer reaches may already be freed.</summary>
    private static void Restore(ref Slot s, in Legs legs)
    {
        *(Vector3*)(s.Root + PartPos) = s.RootPos;
        fixed (float* r = s.Rot)
        fixed (long* p = s.Parts)
            for (int i = 0; i < 6; i++)
                if (legs[i] != IntPtr.Zero && legs[i] == (IntPtr)p[i]) *(Vector4*)(legs[i] + PartRot) = *(Vector4*)(r + 4 * i);
    }

    private static void Save(ref Slot s, IntPtr root, in Legs legs)
    {
        s.RootPos = *(Vector3*)(root + PartPos);
        fixed (float* r = s.Rot)
        fixed (long* p = s.Parts)
            for (int i = 0; i < 6; i++) { p[i] = (long)legs[i]; *(Vector4*)(r + 4 * i) = *(Vector4*)(legs[i] + PartRot); }
    }

    private static void Apply(ref Slot s, IntPtr gob, IntPtr root, in Legs legs, IntPtr scene, float dt)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        s.Stamp = now;
        float scale = _scale(gob);
        if (!(scale > 1e-4f)) return;
        World(root, out Vector3 rootW, out Quaternion rootWq);
        Vector3 rootL = *(Vector3*)(root + PartPos);
        Quaternion adj = rootWq * Quaternion.Conjugate(Local(root));
        Vector3 adjPos = rootW - Vector3.Transform(rootL * scale, adj);
        float refZ = adjPos.Z;

        bool gated = Gated(gob);
        if (gated) _gated++;
        float max = FootIk.Max.V;
        World(legs.RFoot, out Vector3 rAnkle, out _);
        World(legs.LFoot, out Vector3 lAnkle, out _);
        float tr = 0f, tl = 0f;
        bool hr = false, hl = false;
        if (!gated)
        {
            hr = FloorOffset(scene, gob, rAnkle, refZ, max, out tr); hl = FloorOffset(scene, gob, lAnkle, refZ, max, out tl);
            // a foot with no floor in range borrows the other foot's: "none" is not "at the walkmesh"
            if (!hr && hl) tr = tl; else if (hr && !hl) tl = tr;
        }

        Vector3 fwd = Vector3.Transform(Vector3.UnitY, adj);
        Vector3 fwdH = new(fwd.X, fwd.Y, 0f);
        fwdH = fwdH.LengthSquared() > 1e-8f ? Vector3.Normalize(fwdH) : Vector3.UnitY;
        Vector3 right = new(fwdH.Y, -fwdH.X, 0f);
        float maxSlope = MathF.Tan(FootIk.Tilt.V * (MathF.PI / 180f));
        bool tilt = !gated && maxSlope > 1e-4f;
        float reach = ProbeReach * scale;
        float tfr = s.FR, tsr = s.SR, tfl = s.FL, tsl = s.SL;
        if (tilt)
            switch (s.Phase++ & 3)
            {
                case 0: tfr = Slope(scene, gob, rAnkle, fwdH, hr, refZ + tr, reach, maxSlope); break;
                case 1: tsr = Slope(scene, gob, rAnkle, right, hr, refZ + tr, reach, maxSlope); break;
                case 2: tfl = Slope(scene, gob, lAnkle, fwdH, hl, refZ + tl, reach, maxSlope); break;
                default: tsl = Slope(scene, gob, lAnkle, right, hl, refZ + tl, reach, maxSlope); break;
            }
        else tfr = tsr = tfl = tsl = 0f;

        bool snap = s.Root != root || Vector3.DistanceSquared(adjPos, s.LastPos) > Teleport * Teleport;
        float weight = Weight(ref s.Grade, ref s.Moving, snap ? Vector3.Zero : adjPos - s.LastPos, dt, scale);
        if (weight < 1f) _stairs++;
        if (snap)
        {
            s.W = weight; s.VW = 0f;
            s.OffR = tr; s.OffL = tl; s.FR = tfr; s.SR = tsr; s.FL = tfl; s.SL = tsl;
            s.VelR = s.VelL = s.VFR = s.VSR = s.VFL = s.VSL = 0f;
        }
        else
        {
            Smooth(ref s.OffR, ref s.VelR, tr, dt); Smooth(ref s.OffL, ref s.VelL, tl, dt);
            Smooth(ref s.FR, ref s.VFR, tfr, dt); Smooth(ref s.SR, ref s.VSR, tsr, dt);
            Smooth(ref s.FL, ref s.VFL, tfl, dt); Smooth(ref s.SL, ref s.VSL, tsl, dt);
            Smooth(ref s.W, ref s.VW, weight, dt, FadeTime);
        }
        s.LastPos = adjPos;
        s.Root = root;

        float w = Math.Clamp(s.W, 0f, 1f);
        float offR = s.OffR * w, offL = s.OffL * w, fr = s.FR * w, sr = s.SR * w, fl = s.FL * w, sl = s.SL * w;
        float body = MathF.Min(offR, offL);
        bool tilted = MathF.Abs(fr) + MathF.Abs(sr) + MathF.Abs(fl) + MathF.Abs(sl) > MinLift;
        if (MathF.Abs(offR) < MinLift && MathF.Abs(offL) < MinLift && !tilted) return;

        Save(ref s, root, legs);
        s.Applied = true;
        _gobs++;
        *(Vector3*)(root + PartPos) = rootL + Vector3.Transform(new Vector3(0f, 0f, body / scale), Quaternion.Conjugate(adj));
        if (gated) return;
        float cap = FootIk.Lift.V * scale;
        Leg(legs.RThigh, legs.RShin, legs.RFoot, MathF.Min(offR - body, cap), fwd, FootTilt(fwdH, right, fr, sr));
        Leg(legs.LThigh, legs.LShin, legs.LFoot, MathF.Min(offL - body, cap), fwd, FootTilt(fwdH, right, fl, sl));
    }

    /// <summary>The weight foot placement should have: <see cref="StairWeight"/> while the character moves
    /// (<see cref="MoveSpeed"/>, held <see cref="MoveHold"/> across frames without travel) along a walkmesh steeper
    /// than <see cref="StairGrade"/>, else 1. <paramref name="grade"/> averages the rise per unit of travel over
    /// <see cref="GradeTime"/>, so stopping on a flight brings full placement back.</summary>
    internal static float Weight(ref float grade, ref float moving, Vector3 moved, float dt, float scale)
    {
        if (!(dt > 0f)) return 1f;
        float run = MathF.Sqrt(moved.X * moved.X + moved.Y * moved.Y);
        if (run >= MoveSpeed * scale * dt)
        {
            moving = MoveHold;
            grade += (MathF.Abs(moved.Z) / run - grade) * MathF.Min(1f, dt / GradeTime);
        }
        else moving -= dt;
        return moving > 0f && grade > StairGrade ? StairWeight : 1f;
    }

    /// <summary>The floor's rise per unit from a foothold at <paramref name="z"/> toward <paramref name="dir"/>,
    /// clamped to <paramref name="maxSlope"/>: one cast <paramref name="reach"/> away. 0 (flat) when the foot has no
    /// foothold of its own or the cast finds no floor in the tilt range -- a stair edge, a drop.</summary>
    private static float Slope(IntPtr scene, IntPtr gob, Vector3 ankle, Vector3 dir, bool hit, float z, float reach, float maxSlope)
    {
        if (!hit) return 0f;
        Vector3 at = ankle + dir * reach;
        return FloorOffset(scene, gob, at, z, reach * maxSlope, out float rise) ? Math.Clamp(rise / reach, -maxSlope, maxSlope) : 0f;
    }

    /// <summary>The world rotation laying a foot flat on a floor rising <paramref name="f"/> per unit toward
    /// <paramref name="fwd"/> and <paramref name="s"/> toward <paramref name="right"/> (both horizontal).</summary>
    internal static Quaternion FootTilt(Vector3 fwd, Vector3 right, float f, float s)
        => MathF.Abs(f) + MathF.Abs(s) < 1e-5f ? Quaternion.Identity : Arc(Vector3.UnitZ, Vector3.UnitZ - f * fwd - s * right);

    /// <summary>The floor under <paramref name="ankle"/> within <c>refZ +- max</c>, as an offset from
    /// <paramref name="refZ"/>; 0 when there is none. Tiles and static placeables (BSP parts, counted as tile
    /// hits), tile gobs and placeables are ground;
    /// anything else hit -- a creature, a held item, a VFX -- is cast past.</summary>
    private static bool FloorOffset(IntPtr scene, IntPtr self, Vector3 ankle, float refZ, float max, out float off)
    {
        Vector3 from = new(ankle.X, ankle.Y, refZ + max), to = new(ankle.X, ankle.Y, refZ - max);
        off = 0f;
        for (int i = 0; i < MaxCasts && from.Z > to.Z; i++)
        {
            _rays++;
            if (!SceneRay.Cast(scene, from, to, self, out Vector3 hit, out IntPtr g, statics: true)) break;
            if (g == IntPtr.Zero || *(IntPtr*)(g + GobTileData) != IntPtr.Zero) { _tile++; off = hit.Z - refZ; SceneRay.Release(); return true; }
            if (SceneRay.IsPlaceable(g)) { _placeable++; off = hit.Z - refZ; SceneRay.Release(); return true; }
            from.Z = hit.Z - SceneRay.Nudge;
        }
        SceneRay.Release();
        return false;
    }

    /// <summary>Game Programming Gems 4's SmoothCD: a critically damped spring, stable at any <paramref name="dt"/>.</summary>
    internal static void Smooth(ref float x, ref float v, float target, float dt, float time = SmoothTime)
    {
        float omega = 2f / time, k = omega * dt;
        float e = 1f / (1f + k + 0.48f * k * k + 0.235f * k * k * k);
        float change = x - target, temp = (v + omega * change) * dt;
        v = (v - omega * temp) * e;
        x = target + (change + temp) * e;
    }

    /// <summary>Lift one ankle by <paramref name="lift"/> world units: two-bone IK in world space, written back as
    /// locals. The foot keeps its animated world orientation, turned by <paramref name="tilt"/> about the ankle.
    /// A chain whose parents are not thigh-&gt;shin-&gt;foot is left alone.</summary>
    private static void Leg(IntPtr thigh, IntPtr shin, IntPtr foot, float lift, Vector3 fwd, Quaternion tilt)
    {
        bool lifts = MathF.Abs(lift) >= MinLift;
        if (!lifts && tilt.IsIdentity) return;
        IntPtr hip = *(IntPtr*)(thigh + PartParent);
        if (hip == IntPtr.Zero || *(IntPtr*)(shin + PartParent) != thigh || *(IntPtr*)(foot + PartParent) != shin) return;
        World(shin, out Vector3 k, out Quaternion qs);
        World(foot, out Vector3 a, out Quaternion qf);
        if (lifts)
        {
            World(hip, out _, out Quaternion qp);
            World(thigh, out Vector3 h, out Quaternion qt);
            if (TwoBone(h, k, a, a + new Vector3(0f, 0f, lift), fwd, out Quaternion r1, out Quaternion r2))
            {
                Quaternion qt2 = Quaternion.Normalize(r1 * qt);
                qs = Quaternion.Normalize(r2 * r1 * qs);
                SetLocal(thigh, Quaternion.Conjugate(qp) * qt2);
                SetLocal(shin, Quaternion.Conjugate(qt2) * qs);
            }
        }
        SetLocal(foot, Quaternion.Conjugate(qs) * tilt * qf);
    }

    /// <summary>World-space rotations taking the chain hip <paramref name="h"/>, knee <paramref name="k"/>, ankle
    /// <paramref name="a"/> to reach <paramref name="t"/> (clamped to the chain's reach): <paramref name="r1"/>
    /// about the hip, then <paramref name="r2"/> about the new knee. The knee bends toward the character's
    /// forward (<paramref name="fwd"/>): the animated knee's own bulge is mostly SIDEWAYS on a splayed idle leg,
    /// and a knee pushed out that way reads as a bow-legged step. False when degenerate.</summary>
    internal static bool TwoBone(Vector3 h, Vector3 k, Vector3 a, Vector3 t, Vector3 fwd, out Quaternion r1, out Quaternion r2)
    {
        r1 = r2 = Quaternion.Identity;
        float la = Vector3.Distance(h, k), lb = Vector3.Distance(k, a);
        Vector3 d = t - h;
        float c = d.Length();
        if (la < 1e-4f || lb < 1e-4f || c < 1e-4f) return false;
        Vector3 dir = d / c;
        c = Math.Clamp(c, MathF.Abs(la - lb) + 1e-4f, la + lb - 1e-4f);
        Vector3 pole = fwd - dir * Vector3.Dot(fwd, dir);
        if (pole.LengthSquared() < 1e-12f) return false;
        pole = Vector3.Normalize(pole);
        float cosA = Math.Clamp((la * la + c * c - lb * lb) / (2f * la * c), -1f, 1f);
        Vector3 knee = h + la * (dir * cosA + pole * MathF.Sqrt(1f - cosA * cosA));
        r1 = Arc(k - h, knee - h);
        Vector3 a1 = h + Vector3.Transform(a - h, r1);
        r2 = Arc(a1 - knee, h + dir * c - knee);
        return true;
    }

    /// <summary>The shortest rotation taking <paramref name="u"/> onto <paramref name="v"/>.</summary>
    private static Quaternion Arc(Vector3 u, Vector3 v)
    {
        u = Vector3.Normalize(u); v = Vector3.Normalize(v);
        float d = Vector3.Dot(u, v);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f)
        {
            Vector3 axis = Vector3.Cross(MathF.Abs(u.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, u);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        Vector3 c = Vector3.Cross(u, v);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + d));
    }

    /// <summary><c>Part::GetWorld</c>; the engine's quaternion is w-first, System.Numerics' is w-last. Both products
    /// are Hamilton (<c>operator*(Quaternion const&amp;, Quaternion const&amp;)</c> @0x5b5c30), and a child's world
    /// rotation is its parent's times its local.</summary>
    private static void World(IntPtr part, out Vector3 pos, out Quaternion rot)
    {
        Vector3 p;
        float* q = stackalloc float[4];
        _getWorld(part, &p, q);
        pos = p;
        rot = new Quaternion(q[1], q[2], q[3], q[0]);
    }

    private static Quaternion Local(IntPtr part)
    {
        float* q = (float*)(part + PartRot);
        return new Quaternion(q[1], q[2], q[3], q[0]);
    }

    private static void SetLocal(IntPtr part, Quaternion q)
    {
        q = Quaternion.Normalize(q);
        float* r = (float*)(part + PartRot);
        r[0] = q.W; r[1] = q.X; r[2] = q.Y; r[3] = q.Z;
    }

    /// <summary>The period clause: corrected gobs, casts, what the casts landed on, and the gobs refused (an
    /// animation gate, or no slot). Resets its counters -- one caller, once per period.</summary>
    internal static string Stats(int frames)
    {
        if (!_armed || frames <= 0) return "";
        string s = FormattableString.Invariant(
            $"ik {(float)_gobs / frames:F1} gobs/f, {(float)_rays / frames:F1} rays/f, hits {(float)_tile / frames:F1} tile + {(float)_placeable / frames:F1} placeable/f, refused {(float)_gated / frames:F1} gated + {(float)_noSlot / frames:F1} no slot/f, {(float)_stairs / frames:F1} on stairs/f");
        _gobs = _rays = _tile = _placeable = _gated = _stairs = _noSlot = 0;
        return s;
    }

    /// <summary>The group's status line.</summary>
    internal static string Report()
        => !_armed ? "foot placement unhooked" : $"foot placement {(FootIk.Enabled.V ? "on" : "off")}";

    /// <summary>The pure parts off-device: leg names, the animation gate, the spring and the two-bone solve
    /// (reach, bone lengths, knee direction).</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "FootIk.SelfCheck");
        fixed (byte* a = "RThigh_g\0"u8, b = "lfoot_g\0"u8, c = "rthigh_g01\0"u8, d = "torso_g\0"u8)
            check.Expect(LegIndex(a) == 0 && LegIndex(b) == 5 && LegIndex(c) == -1 && LegIndex(d) == -1,
                         "leg names match whole and case-insensitive");
        fixed (byte* a = "sitdown\0"u8, b = "ckdbckps\0"u8, c = "pause1\0"u8, d = "cwalkf\0"u8, e = "gustandb\0"u8, f = "greeting\0"u8)
            check.Expect(IsDenied(a) && IsDenied(b) && IsDenied(e) && !IsDenied(c) && !IsDenied(d) && !IsDenied(f),
                         "sitting, knockdown and getting up are gated; standing, walking and greeting are not");

        float x = 0f, v = 0f, peak = 0f;
        for (int i = 0; i < 120; i++) { Smooth(ref x, ref v, 0.3f, 1f / 60f); peak = MathF.Max(peak, x); }
        float y = 0f, w = 0f;
        Smooth(ref y, ref w, 0.3f, 0.5f);
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
        float gr = 0f, mv = 0f, dt60 = 1f / 60f, wUp = 1f, wFlat = 0f, wStop = 0f;
        bool steady = true;
        for (int i = 0; i < 30; i++)
        {
            wUp = Weight(ref gr, ref mv, (i & 1) == 0 ? new Vector3(0.08f, 0f, 0.05f) : Vector3.Zero, dt60, 1f);
            if (i > 10 && wUp != StairWeight) steady = false;
        }
        for (int i = 0; i < 20; i++) wStop = Weight(ref gr, ref mv, Vector3.Zero, dt60, 1f);
        float gf = 0f, mf = 0f;
        for (int i = 0; i < 30; i++) wFlat = Weight(ref gf, ref mf, new Vector3(0.04f, 0f, 0f), dt60, 1f);
        check.Expect(steady && wStop == 1f && wFlat == 1f,
                     "walking a stair-steep walkmesh (position advancing every other frame) holds the stairs weight without flicker; stopping on it, or walking a flat platform, is full placement");
        Vector3 toe = Vector3.Transform(Vector3.UnitY, FootTilt(Vector3.UnitY, Vector3.UnitX, 0.5f, 0f));
        Vector3 sole = Vector3.Transform(Vector3.UnitX, FootTilt(Vector3.UnitY, Vector3.UnitX, 0f, -0.3f));
        check.Expect(MathF.Abs(toe.Z / toe.Y - 0.5f) < 1e-3f && MathF.Abs(sole.Z / sole.X + 0.3f) < 1e-3f
                     && FootTilt(Vector3.UnitY, Vector3.UnitX, 0f, 0f).IsIdentity,
                     "a tilted foot lies on the slope: toe up a rise, sideways down a fall, untouched on the flat");
        log.Info(check.Ok ? "FootIk.SelfCheck: passed" : "FootIk.SelfCheck: FAILED");
    }
}
