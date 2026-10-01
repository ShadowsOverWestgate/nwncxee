using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Anim;

namespace CX.Alloy;

/// <summary>
/// CX-106: Alloy owns the animation runtime. Replaces <c>Scene::Animate</c> @0x469810 (the per-frame walk)
/// and the body of <c>Gob::Animate</c> @0x49d8c0 (the AnimRun queue, the crossfade weights, the lifecycle
/// events, the look-at teardown and the wind point source); <see cref="AnimHierarchy"/> and
/// <see cref="AnimKeys"/> own the pose, <see cref="AnimEvents"/> the animation's keyed events.
/// </summary>
/// <remarks>
/// <para><b>What still runs, and why.</b> Three seams are kept deliberately:</para>
/// <list type="bullet">
/// <item><c>Gob::Animate</c>'s ENTRY POINT is the per-gob dispatch. The owned scene walk calls the symbol, so
/// the funchook patch on it is reached and every existing consumer of that seam keeps its place --
/// <c>PoseService</c> chains it at priority 50 and its <c>next(gob, dt)</c> lands on the owned body chained
/// at 10. Nothing of the engine's own body runs. It is also how <c>CAurBehavior::Control</c>'s recursion into
/// an attach parent, and <c>CAurObject::AnimateExternal</c>, reach the owned runtime for free.</item>
/// <item><c>Gob::DoEvent</c> is the engine's, called through its vtable. That IS the seam into game code, and
/// re-implementing the callback registry would buy nothing and break footsteps.</item>
/// <item>the behaviour solvers and <c>NewController::ReportKeys</c> are dispatched exactly where the engine
/// dispatches them. <c>Part::Animate</c> is NOT, any more: <see cref="PartWalk"/> owns that recursion and
/// calls out only at a part whose Animate slot overrides it -- which is how the dangly solve is still
/// reached, and the only way it is.</item>
/// </list>
/// <para><b>Gate.</b> Boot-time renderer choice (<c>alloy.enabled</c>: this whole pass is only registered
/// when Alloy is not opted out) plus the live <c>anim.owned</c> knob. It is NOT gated on the per-frame draw
/// verdict: animation has to keep running on frames the renderer refuses, and on GUI scenes -- which are
/// separate <c>Scene</c> objects reached through the same symbol, so they get the owned runtime too.</para>
/// <para><b>Allocation-free.</b> The <c>SafePointer</c> death notice is a <c>stackalloc</c>; the event names
/// are built in a stack buffer; nothing here touches the managed heap.</para>
/// </remarks>
internal static unsafe partial class AnimRuntime
{
    /// <summary>The <c>AnimRun</c> flag bits the queue advance reads (docs/research/anim-runtime.md §4).</summary>
    private const uint FadeOut = 0x1, Loop = 0x4, Sticky = 0x8, Queued = 0x20, Trace = 0x100, FadeNow = 0x200;   // not-an-offset: AnimRun+0xc flag bits
    /// <summary>"No previous transform yet" sentinel in <see cref="EngineLayout.GobPrevPosition"/>.</summary>
    private const float NoPrev = -1e7f;
    /// <summary>The engine's <c>sprintf</c> buffer for a lifecycle event name.</summary>
    private const int NameMax = 0x40;   // not-an-offset: the engine sprintf buffer size
    /// <summary>The bounding sphere is refreshed every Nth animate.</summary>
    private const int SphereEvery = 5;
    /// <summary>AN4: one refusal in this many is taken anyway and its answer compared. A power of two.</summary>
    private const int SphereAudit = 64;
    /// <summary>Two sticky bits Alloy keeps in the spare high end of <see cref="EngineLayout.GobSphereTick"/>,
    /// whose own counter only ever holds 0..4. <see cref="SphereMovedBit"/> is "something moved since the last
    /// refresh", which is the predicate the refresh cadence actually needs; <see cref="LightHostBit"/> is "the
    /// pose walk has seen a light part in this tree".
    /// <para>Alloy is the field's only writer on an owned frame -- the engine's <c>Gob::Animate</c> is the sole
    /// other one and this runtime replaces it. With <c>anim.owned 0</c> the engine writes the counter plainly
    /// and clears both bits, which costs one cadence step and one re-latch, never correctness.</para></summary>
    // ponytail: 0x8000 is the ceiling for spare bits here. BOTH builds read Gob+0x188 SIGNED (movsx, then the
    // imul 0x66666667 magic divide), so a bit in the sign position would make the engine's own (x+1)%5 negative
    // and never zero -- with anim.owned 0 the sphere would then never refresh at all. A third flag is fine;
    // the upgrade past 0x4000 is a side table keyed on the gob, not another bit.
    private const int SphereMovedBit = 0x100, LightHostBit = 0x200;   // not-an-offset: spare bits of Gob+0x188
    /// <summary>The counter's own range inside that field.</summary>
    private const int SphereTickMask = 0xff;   // not-an-offset: the counter's own range below those bits
    /// <summary>The wind gust's radius: the engine hard-codes 1.0 at the call site.</summary>
    private const float GustRadius = 1.0f;

    private static ReadOnlySpan<byte> StartPrefix => "start"u8;
    private static ReadOnlySpan<byte> LoopPrefix => "loop"u8;
    private static ReadOnlySpan<byte> DonePrefix => "done"u8;

    private static ExtenderLog? _log;
    private static SceneAnimateFn* _sceneNext;
    private static GobAnimateFn* _gobNext;
    private static GobAnimateFn _gobEntry;
    private static IntPtr _gobAnimateSym;
    private static AnimRunListRemoveFn _remove;
    private static AnimRunListDelIndexFn _delIndex;
    private static SafePointerAddFn _safeAdd;
    private static OperatorDeleteFn _free;
    private static IntPtr* _pCurrentScene;
    private static IntPtr* _pTraceRun;
    private static int* _pTraceFlags;
    private static float* _pTraceLength;
    private static float* _pPosScale;
    private static bool _armed;

    private static int _scenes, _gobs, _skipped, _runs, _crossfades, _dropped, _gusts, _lookAts, _marked, _rootless;
    /// <summary>The two per-gob tree costs the survey could not attribute: how many gobs had their part tree
    /// animated at all, and how many of those refreshed the bounding sphere on this frame's cadence.</summary>
    private static int _trees, _spheres;
    /// <summary>AN4: refreshes refused because the gob advanced nothing, and the audit's verdict on them --
    /// a sphere that came back DIFFERENT from a refused refresh, which must be 0.</summary>
    private static int _sphereSkipped, _sphereMiss, _sphereTick;
    /// <summary>Events fired per kind, which is what a side-by-side against the engine's own run compares:
    /// a missing footstep is a missing KEYED event, a pose that never starts is a missing <c>start</c>.</summary>
    private static int _starts, _loops, _dones;
    private static long _ticks, _peakTicks;

    /// <summary>The owned runtime drives this frame: the boot renderer choice already picked this pass, so
    /// only the live A/B knob is left.</summary>
    internal static bool Owned => _armed && AnimKnobs.Owned.V;

    /// <summary>A <c>SafePointer&lt;CAurObject&gt;</c> as the engine lays it out: the object, then its index
    /// in the owner's notice list. <c>Gob::~Gob</c> zeroes <see cref="Object"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SafePtr { public IntPtr Object; public int Index; }

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _ = AnimKnobs.G;
        if (!EngineLayout.Require(log, "AnimRuntime", "Scene", "Gob", "Part", "PartLight", "PartTriMesh",
                                  "VertexPrimitive", "MdlNode", "MdlNodeAnimMesh", "NewController", "AnimRun",
                                  "Animation", "Model", "CAurBehavior", "LookAtAnimation")) return;
        if (!SymbolMap.Require(log, "AnimRuntime", SymbolMap.SceneAnimate, SymbolMap.GobAnimate,
                               SymbolMap.CurrentScene, SymbolMap.PositionScaleFactor, SymbolMap.AnimTraceRun,
                               SymbolMap.AnimTraceFlags, SymbolMap.AnimTraceLength, SymbolMap.CtrlGetFloat,
                               SymbolMap.CtrlGetVector, SymbolMap.CtrlReportKeys, SymbolMap.EmitterCalcRadius,
                               SymbolMap.AnimRunListRemove, SymbolMap.AnimRunListDelIndex,
                               SymbolMap.SafePointerAdd, SymbolMap.OperatorDelete,
                               SymbolMap.MdlNodeControlFn, SymbolMap.MdlNodeAnimMeshControlFn,
                               SymbolMap.MdlNodeCameraControlFn, SymbolMap.MdlNodeEmitterControlFn,
                               SymbolMap.MdlNodeLightControlFn, SymbolMap.MdlNodeReferenceControlFn,
                               SymbolMap.MdlNodeTriMeshControlFn)) return;

        _pCurrentScene = (IntPtr*)Native.Sym(SymbolMap.CurrentScene);
        _pTraceRun = (IntPtr*)Native.Sym(SymbolMap.AnimTraceRun);
        _pTraceFlags = (int*)Native.Sym(SymbolMap.AnimTraceFlags);
        _pTraceLength = (float*)Native.Sym(SymbolMap.AnimTraceLength);
        _pPosScale = (float*)Native.Sym(SymbolMap.PositionScaleFactor);
        _remove = EngineFn.AnimRunListRemove;
        _delIndex = EngineFn.AnimRunListDelIndex;
        _safeAdd = EngineFn.SafePointerAdd;
        _free = EngineFn.OperatorDelete;
        _gobAnimateSym = Native.Sym(SymbolMap.GobAnimate);
        _gobEntry = EngineFn.GobAnimate;
        // EmitterSim resolves this too, but registers after animrt: without our own call Ready reads false.
        AurXform.Resolve();

        string? missing = _pCurrentScene == null || _pTraceRun == null || _pTraceFlags == null || _pTraceLength == null
                          || _pPosScale == null || _remove == null || _delIndex == null || _safeAdd == null
                          || _free == null || _gobAnimateSym == IntPtr.Zero || _gobEntry == null ? "a runtime symbol"
                        : !AurXform.Ready ? AurXform.Missing
                        : !AnimKeys.Attach() ? "the key readers"
                        : !AnimHierarchy.Attach(log) ? "the node Control table"
                        : !PartWalk.Attach(log) ? "the Part::Animate identities"
                        : null;
        if (missing != null)
        {
            log.Always($"AnimRuntime: standing down ({missing} did not resolve) -- Scene::Animate keeps the frame");
            return;
        }

        // Chain, never Install: Scene::Animate has no other consumer today but plausibly will, and
        // Gob::Animate certainly does (PoseService at 50). Priority 10 puts the owned body at the BOTTOM of
        // both chains, so every existing subscriber keeps running above it and its `next` lands here.
        _sceneNext = hooks.ChainSceneAnimate(&OnSceneAnimate, 10);
        _gobNext = hooks.ChainGobAnimate(&OnGobAnimate, 10);
        if (_sceneNext == null || _gobNext == null)
        {
            log.Info("AnimRuntime: standing down (a chain was refused) -- the engine keeps the animation runtime");
            return;
        }
        _armed = true;
        log.Info($"AnimRuntime: armed -- Alloy owns Scene::Animate and the Gob::Animate body "
               + $"(anim.owned {(AnimKnobs.Owned.V ? "on" : "off")}); /anim owned 0 hands both back");
    }

    /// <summary>The <c>[alloy]</c> period clause: the numbers a live side-by-side compares.</summary>
    internal static string Stats(int frames)
    {
        if (frames <= 0 || !_armed) { Reset(); return _armed ? "" : "animrt unarmed"; }
        if (_scenes == 0) { Reset(); return "animrt 0 walks/f (the engine walked the scenes)"; }
        double toMs = 1000.0 / Stopwatch.Frequency;
        string s = $"animrt {(float)_scenes / frames:F1} walks/f, {(float)_gobs / frames:F0} gobs/f "
                 + $"({(float)_skipped / frames:F0} latched), {(float)_runs / frames:F0} runs/f, "
                 + $"{AnimHierarchy.Parts / (float)frames:F0} parts/f, {AnimHierarchy.Controllers / (float)frames:F0} ctrl/f, "
                 + $"{(float)_crossfades / frames:F1} fades/f, {(float)_dropped / frames:F1} runs dropped/f, "
                 + $"events/f {AnimEvents.Fired / (float)frames:F1} keyed + {(float)_starts / frames:F2} start "
                 + $"+ {(float)_loops / frames:F2} loop + {(float)_dones / frames:F2} done, "
                 + $"{(float)_marked / frames:F0} marked/f, "
                 + $"{(float)_lodFar / frames:F0} far/f ({(float)_lodHeld / frames:F0} held), "
                 + $"{(float)_trees / frames:F0} trees/f ({PartWalk.Visits / (float)frames:F0} parts, "
                 + $"{PartWalk.Dispatches / (float)frames:F0} dispatched), "
                 + $"{(float)_spheres / frames:F0} spheres/f "
                 + $"({(float)_sphereSkipped / frames:F0} still), "
                 + $"{AnimHierarchy.PairProbes / (float)frames:F1} pair probes/f, "
                 + $"{AnimHierarchy.StaticLights / (float)frames:F0} staticlight/f "
                 + $"({AnimHierarchy.StaticLightsSkipped / (float)frames:F0} unheard), "
                 + $"solve {_ticks * toMs / frames:F3} ms/f incl. the dangly drain (peak gob {_peakTicks * toMs:F3} ms)";
        if (AnimHierarchy.Morphs != 0) s += $", {AnimHierarchy.Morphs / (float)frames:F1} morphs/f";
        if (AnimHierarchy.Traced != 0) s += $", {AnimHierarchy.Traced / (float)frames:F1} traced/f";
        if (_lookAts != 0) s += $", {_lookAts} look-at teardowns";
        if (_gusts != 0) s += $", {_gusts} gusts";
        int bez = AnimKeys.TakeBezier();
        if (bez != 0) s += $", {bez / (float)frames:F1} bezier/f (engine getter)";
        if (AnimHierarchy.HandedBack != 0) s += $", {AnimHierarchy.HandedBack} UNKNOWN node classes handed back";
        if (_rootless != 0) s += $", {_rootless} runs REFUSED (no part tree or no animation root node)";
        if (_sphereMiss != 0) { s += $", {_sphereMiss} SPHERE AUDIT misses (must be 0)"; _sphereMiss = 0; }
        if (PartWalk.Unknown != 0) s += $", {PartWalk.Unknown / (float)frames:F1} UNKNOWN Animate overrides/f";
        if (PartWalk.Overflow != 0) s += $", {PartWalk.Overflow / (float)frames:F1} subtree(s)/f DEEPER than the walk descends";
        Reset();
        return s;
    }

    private static void Reset()
    {
        _scenes = _gobs = _skipped = _runs = _crossfades = _dropped = _gusts = _lookAts = _marked = _rootless = 0;
        _starts = _loops = _dones = 0;
        _trees = _spheres = _sphereSkipped = 0;
        _lodFar = _lodHeld = 0;
        _ticks = _peakTicks = 0;
        AnimHierarchy.Parts = AnimHierarchy.Controllers = AnimHierarchy.Morphs = 0;
        AnimHierarchy.StaticLights = AnimHierarchy.StaticLightsSkipped = AnimHierarchy.PairProbes = 0;
        PartWalk.Visits = PartWalk.Dispatches = PartWalk.Unknown = PartWalk.Overflow = 0;
        AnimHierarchy.HandedBack = AnimHierarchy.Traced = 0;
        AnimEvents.Fired = 0;
        AnimKeys.TakeBezier();
    }

    /// <summary>The <c>/anim</c> status clause for this pass.</summary>
    internal static string Describe()
        => !_armed ? "owned runtime unarmed (the engine animates)"
         : AnimKnobs.Owned.V ? "owned runtime on (Alloy walks the scenes and advances every queue)"
         : "owned runtime off -- Scene::Animate and Gob::Animate are the engine's";

    // ================= the scene walk =================

    /// <summary>
    /// <c>Scene::Animate</c>. Backwards over the child list, re-reading the count each step (a child that
    /// removes itself or anything above it must not be skipped), with <c>currentscene</c> latched for the
    /// duration. A child whose <see cref="EngineLayout.GobAnimateSlot"/> is not <c>Gob::Animate</c> --
    /// <c>Camera::Animate</c> is the one override -- is dispatched through its own slot.
    /// <para><b>The <c>currentscene</c> latch is the engine's and it is load-bearing.</b> Ghidra drops the
    /// store, so the decompiled body looks like it only clears the global -- the aligned disassembly has
    /// <c>mov r13,rdi</c> @0x46981c and <c>mov QWORD PTR [rax],r13</c> @0x46986c, i.e.
    /// <c>currentscene = this</c>, before the walk and <c>= 0</c> @0x4698c0 after it. The consumer is INSIDE
    /// the walk: <c>PartDanglyMesh::Animate</c> @0x49bbb0 reads it at 0x49bc3d and hands the whole solve back
    /// when it is null, and that is reached from the <c>Part::Animate</c> call in every gob's step. Dropping
    /// the latch would stand <see cref="DanglyPhysics"/> down on every frame.</para>
    /// <para>The previous value is restored where the engine writes a literal zero. They coincide in every
    /// engine flow -- nothing nests a scene walk, so the global is already zero on entry -- and the restore
    /// is the safer of the two if one is ever nested.</para>
    /// </summary>
    [UnmanagedCallersOnly]
    private static void OnSceneAnimate(IntPtr scene, float dt)
    {
        if (!Owned || scene == IntPtr.Zero) { (*_sceneNext)(scene, dt); return; }
        _scenes++;
        IntPtr was = *_pCurrentScene;
        *_pCurrentScene = scene;
        // AN2: the dangly solves of this walk are collected rather than run, and drained below -- inside the
        // latch, which PartDanglyMesh::Animate's own gate reads, and after every gob's pose is final.
        bool wasBatching = DanglyPhysics.Batching;
        DanglyPhysics.Batching = true;
        LodBegin(scene, dt);
        var s = new Scene(scene);
        for (int i = s.GobCount - 1; i >= 0; i--)
        {
            if (s.GobCount <= i) continue;
            IntPtr gob = ((IntPtr*)s.Gobs)[i];
            if (gob == IntPtr.Zero) continue;
            IntPtr fn = *(IntPtr*)(*(IntPtr*)gob + EngineLayout.GobAnimateSlot);
            // Deliberately through the SYMBOL, not straight into One(): that address carries the funchook
            // patch, so the per-gob chain (PoseService and anything else on this seam) runs above us.
            if (fn == _gobAnimateSym) _gobEntry(gob, dt);
            else ((GobAnimateFn)fn)(gob, dt);
        }
        // Billed to `animrt solve` exactly as the inline solves were: the drain moved the dangly arithmetic out
        // of the per-gob step, and leaving it out of this total would read as a 1 ms win that is a relocation.
        long tDrain = Stopwatch.GetTimestamp();
        DanglyPhysics.Drain();
        _ticks += Stopwatch.GetTimestamp() - tDrain;
        DanglyPhysics.Batching = wasBatching;
        // The camera answer belongs to this walk only: a Gob::Animate from outside one (AnimateExternal) must
        // not be rate-limited against where the camera was some frames ago.
        _haveCam = false;
        *_pCurrentScene = was;
    }

    /// <summary>The bottom of the <c>Gob::Animate</c> chain: the owned per-gob step, or the engine's when the
    /// A/B knob is off.</summary>
    [UnmanagedCallersOnly]
    private static void OnGobAnimate(IntPtr gob, float dt)
    {
        if (!Owned || gob == IntPtr.Zero) { (*_gobNext)(gob, dt); return; }
        long t0 = Stopwatch.GetTimestamp();
        One(gob, dt);
        long d = Stopwatch.GetTimestamp() - t0;
        _ticks += d;
        if (d > _peakTicks) _peakTicks = d;
    }

    // ================= one gob =================

    private static void One(IntPtr gob, float dt)
    {
        var g = new Gob(gob);
        IntPtr scene = g.Scene;
        // The double-step guard, and the ONLY reader of the scene's own counter: it is a render count, not an
        // animate count (see _lodClock), so nothing else may use it as a clock. Read before any game code runs,
        // because the behaviour solve below can replace or free this scene (see the re-read at Gates).
        if (scene != IntPtr.Zero)
        {
            int tick = new Scene(scene).AnimFrame;
            if (g.AnimFrame == tick) { _skipped++; return; }
            g.AnimFrame = tick;
        }
        _gobs++;

        Wind(g, scene);
        // The previous transform is latched unconditionally, after the gust has read it.
        g.PrevPosition = g.Position;
        g.PrevRot = g.Rot;

        // The death notice. Everything below can run game code, and a handler may delete this gob.
        SafePtr sp;
        sp.Object = gob;
        sp.Index = -1;
        _safeAdd(gob + EngineLayout.GobSafePointers, (IntPtr)(void*)(&sp));
        sp.Index = g.SafePointerCount - 1;
        bool moved = false;

        IntPtr beh = g.Attach.Ptr;
        if (beh != IntPtr.Zero)
        {
            // BEFORE the pose and before any event: an attachment is solved against the transform the gob
            // had at the END of the previous frame. The base solver recurses into each dependency gob's
            // Gob::Animate, so an attach parent is animated before its child.
            new CAurBehavior(beh).Control(dt);
            if (sp.Object == IntPtr.Zero) return;   // freed: the pointer must not reach GobMotion
            moved = true;
        }

        // Re-read: the engine loads `gob->scene` again at 0x49d9e2, AFTER the behaviour solve. That solve runs
        // game code (the base one recurses into every dependency gob's whole animate), so a handler can move
        // this gob to another scene or destroy the one it was in -- and the gate would then be a virtual call
        // through freed memory. The wind block above deliberately keeps the pre-solve read, as the engine does.
        if (!Gates(g, g.Scene)) { Done(gob, ref sp, moved); return; }
        if (!LookAt(g, gob, ref sp, ref moved)) return;
        if (!Visible(g)) { Done(gob, ref sp, moved); return; }

        // AN5: how many frames of animation this step covers. 1 is every frame, which is every gob when the
        // knob is off, everything near the camera, and everything pinned.
        int rate = LodRate(g, gob);
        if (rate > 1)
        {
            _lodFar++;
            if (!LodDue(gob, rate)) { _lodHeld++; Tree(g, dt, moved); Done(gob, ref sp, moved); return; }
        }

        int n = g.RunCount;
        if (n != 0)
        {
            var top = g.Run(n - 1);
            if (!top.IsNull && (top.Flags & Queued) != 0)
            {
                top.Flags &= ~Queued;
                Start(gob, top.Animation.Ptr);
                if (sp.Object == IntPtr.Zero) return;
                n = g.RunCount;
            }
        }

        // The queue advances by the time that has actually elapsed since it last did -- the SUM of the frame
        // times of the frames held, never dt scaled by the rate, which would only be the same number while the
        // frame time held still.
        if (!Advance(g, gob, rate == 1 ? dt : Elapsed(rate), n, ref sp, ref moved)) return;
        Tree(g, dt, moved);
        Done(gob, ref sp, moved);
    }

    /// <summary>The part tree and the bounding sphere. Always the REAL frame time: the dangly solve hangs off
    /// this walk (<c>PartDanglyMesh::Animate</c> is how <see cref="DanglyPhysics"/> is reached), and a cloak
    /// sampled every other frame stutters where a limb pose does not.</summary>
    private static void Tree(Gob g, float dt, bool moved)
    {
        IntPtr root = g.PartTree.Ptr;
        if (root == IntPtr.Zero) return;
        _trees++;
        // AN1: the owned walk, not `new Part(root).Animate(dt)`. Part::Animate does nothing but recurse,
        // so the engine's version was one indirect call per part to find the few that override it.
        PartWalk.Walk(root, dt);
        int raw = g.SphereTick;
        Step(raw, moved, out int tick, out bool movedSince, out short next);
        g.SphereTick = next;
        if (tick == 0) Sphere(g, movedSince);
    }

    /// <summary>
    /// One step of the refresh cadence. Pure, so <see cref="SelfCheck"/> can drive it.
    /// <para><b>Why the sticky bit exists.</b> The refusal predicate has to be "nothing moved SINCE THE LAST
    /// REFRESH", not "nothing moved on this step": the cadence only reaches a refresh every fifth step, so a
    /// gob that advanced on step 3 and stood still on step 5 would refuse with a pose two steps old. With
    /// <c>anim.lod</c> on it is worse -- a far gob's held frames make <c>moved</c> false on frames whose pose
    /// did advance, so roughly half of its refresh opportunities would refuse against a pose up to ten frames
    /// stale, and the sphere feeds the far-plane visible test and the LOD distance. The bit is ORed on every
    /// moving step and cleared at the refresh.</para>
    /// </summary>
    internal static void Step(int raw, bool moved, out int tick, out bool movedSince, out short next)
    {
        tick = ((raw & SphereTickMask) + 1) % SphereEvery;
        movedSince = moved || (raw & SphereMovedBit) != 0;
        // The light-host latch is independent of the cadence and survives it.
        int keep = raw & LightHostBit;
        next = (short)(tick == 0 ? tick | keep : tick | keep | (movedSince ? SphereMovedBit : 0));
    }

    /// <summary>The pose walk's note that this tree holds a light part, latched for
    /// <see cref="LodRate"/>. Called from <see cref="AnimHierarchy"/> with the owning gob.</summary>
    internal static void NoteLightHost(IntPtr gob)
    {
        var g = new Gob(gob);
        int raw = g.SphereTick;
        if ((raw & LightHostBit) == 0) g.SphereTick = (short)(raw | LightHostBit);
    }

    // ================= AN5: distance animation LOD =================

    /// <summary>The callback names that PIN a gob to full rate.
    /// <para><c>CAurBehaviorAttach</c>'s constructor registers all six of its callbacks on the DEPENDENCY --
    /// the parent it is attached to (<c>src/CAurBehaviorAttach.c:49-70</c>, on <c>this+0x20</c>) -- and the
    /// attached child gets none of them; the child only has events FIRED at it. So what these names pin is the
    /// PARENT: <c>"*"</c> (<c>UpdateScenesAttach</c>, a wildcard <c>Gob::DoEvent</c> fires for every event) and
    /// <c>UpdateDependentPosition</c> both name a gob something hangs off.</para>
    /// <para>The child needs no pin of its own, and that is not luck: an attached gob's transform comes from
    /// <c>CAurBehavior::Control</c>, which <see cref="One"/> runs BEFORE this gate and on every frame whatever
    /// the rate. Only the child's own animation queue is sampled coarsely, which is the intent.</para>
    /// <para><c>AnimateStaticLight</c> names a tile static object. That is NOT how light hosts are pinned --
    /// it would miss every light that is not a tile prop's, a carried torch above all -- it is kept only
    /// because such a gob is a prop whose animation is worth nothing to sample coarsely. The light pin is
    /// <see cref="LightHostBit"/>, latched by the pose walk from the part class itself.</para></summary>
    private static ReadOnlySpan<byte> PinDependent => "UpdateDependentPosition\0"u8;
    /// <inheritdoc cref="PinDependent"/>
    private static ReadOnlySpan<byte> PinStaticLight => "AnimateStaticLight\0"u8;

    private static Vector3 _camPos;
    private static bool _haveCam;
    private static IntPtr _focusGob;
    private static int _lodFar, _lodHeld;
    /// <summary>The last <see cref="DtRing"/> frame times of the world scene, indexed by its animate tick. A
    /// gob due at tick <c>t</c> with rate <c>r</c> advances by the sum of the <c>r</c> most recent entries,
    /// which IS the time elapsed since its last due frame -- exact under a variable frame time, where
    /// <c>dt * rate</c> is only right while the frame time holds still. One ring for the whole scene rather
    /// than an accumulator per gob: every gob in a scene is handed the same <c>dt</c>, so the per-gob answer is
    /// a window over this.</summary>
    private static readonly float[] _dtRing = new float[DtRing];
    /// <summary>Entries. A power of two, at least <c>anim.lodrate</c>'s ceiling.</summary>
    private const int DtRing = 8;
    private static IntPtr _dtScene;
    /// <summary>
    /// Alloy's own LOD clock: one tick per world-scene animate, incremented here and nowhere else.
    /// <para><b>Why not the scene's own tick.</b> <c>Scene+0x1b8</c> is incremented only by
    /// <c>Scene::Render</c> @0x4762d0, so it counts RENDERS of the scene, not animates. Nothing makes those
    /// one-to-one: two viewports (#391), a reflection or mirror pass, or any second <c>Scene::Render</c> in a
    /// frame advances it by two while this runtime animated once. At <c>lodrate 2</c> that makes
    /// <c>(tick + phase) % 2</c> constant per gob -- half the far gobs would FREEZE outright and half would
    /// advance every frame -- and the ring's <c>tick-1</c> slot would hold a different frame's time. The
    /// scene's counter is left to the double-step guard, which is the one thing it is right for.</para>
    /// </summary>
    private static int _lodClock;
    /// <summary>The clock value the ring was last written at, so a gap -- the knob toggled off and back on
    /// inside one area -- reseeds it instead of summing frame times from before the gap.</summary>
    private static int _dtClockAt = int.MinValue;

    /// <summary>Once per scene walk: where the camera is and what it is following. Both are read here rather
    /// than per gob -- they cannot move inside one walk.
    /// <para><b>World scenes only.</b> A GUI 3D scene -- the character sheet's figure, an inventory item
    /// spinning, a portrait -- is a Scene of its own reached through the same symbol, and its gobs sit wherever
    /// that scene's own camera put them, hundreds of units from the in-game one. Measuring them against the
    /// world camera reads every one of them as far away while the player is looking at it from a foot off, so
    /// the whole feature stands down for them (the discriminator <see cref="GobLod"/> and the route walk use:
    /// the tile BSP root).</para></summary>
    private static void LodBegin(IntPtr scene, float dt)
    {
        _haveCam = false;
        _focusGob = IntPtr.Zero;
        if (!AlloyRenderer.IsWorldScene(scene)) return;
        // Advanced for every world-scene animate, knob or no knob, so that a toggle shows up below as a GAP in
        // the clock rather than as a seamless continuation over frames whose time was never filed.
        _lodClock++;
        if (!AnimKnobs.Lod.V || AnimKnobs.LodRate.V <= 1) return;
        // This frame's time, filed under the clock value a due gob will read it by. A scene the ring has never
        // seen (the first frame, an area load) or a gap in the clock is seeded whole, so the first few windows
        // are this dt rather than zeros or another session's -- which would mis-advance a far gob.
        if (_dtScene != scene || _dtClockAt + 1 != _lodClock)
        { _dtScene = scene; for (int i = 0; i < DtRing; i++) _dtRing[i] = dt; }
        _dtRing[_lodClock & (DtRing - 1)] = dt;
        _dtClockAt = _lodClock;
        var cam = new Camera(AlloyCamera.World);
        if (cam.IsNull) return;
        _camPos = cam.Pos;
        _haveCam = true;
        // The camera's follow target IS the player's own gob under every ordinary mode; the same chain
        // ManageSceneBSP walks (AlloyRenderer.Occlude's OccludeTargetGob).
        CAurBehavior hv = cam.BehaviorHost;
        if (hv.IsNull) return;
        var follow = hv.AsBehaviorCameraFollowFn == IntPtr.Zero ? default : hv.AsBehaviorCameraFollow();
        if (!follow.IsNull) _focusGob = follow.Target;
    }

    /// <summary>
    /// How many frames of animation one step of this gob's queue should cover.
    /// <para>1 -- every frame -- for the knob off, for a scene with no camera, for the gob the camera follows,
    /// for anything within <c>anim.loddist</c> of the camera, for a tree the pose walk has seen a light part in
    /// (<see cref="LightHostBit"/>), and for anything the callback list PINS: either end of an attachment, or a
    /// tile static object. Dangly parts need no exemption of their own -- <see cref="Tree"/> runs at the real
    /// frame time either way.</para>
    /// <para><b>Conservative by construction.</b> The default is 45 m and one frame in two. At 45 m a
    /// human figure is a few dozen pixels tall at the measured 2544x1378, which is below the size at which a
    /// limb's per-frame position is legible, and 30 Hz of pose on a 60 Hz frame is above the rate at which a
    /// walk cycle reads as stepped. It is also well inside the visible range (camera far 215, fog end 125), so
    /// this is a sampling decision and not a culling one -- nothing disappears and nothing changes pace.</para>
    /// </summary>
    private static int LodRate(Gob g, IntPtr gob)
    {
        if (!_haveCam || gob == _focusGob) return 1;
        int rate = AnimKnobs.LodRate.V;
        if (rate <= 1) return 1;
        float d = AnimKnobs.LodDist.V;
        if (Vector3.DistanceSquared(g.Position, _camPos) <= d * d) return 1;
        // A tree with a light part in it: its controllers ARE the flicker, and a torch pulsing at half rate is
        // visible where a limb pose is not. Latched by the pose walk (NoteLightHost), so a gob that is already
        // far on the frame it first poses takes one reduced step before the pin bites.
        if ((g.SphereTick & LightHostBit) != 0) return 1;
        return Pinned(g) ? 1 : rate;
    }

    /// <summary>Does this gob carry a callback that pins it to full rate?</summary>
    private static bool Pinned(Gob g)
    {
        int n = g.CallbackCount;
        IntPtr* list = (IntPtr*)g.Callbacks;
        if (n <= 0 || list == null) return false;
        for (int i = 0; i < n; i++)
        {
            byte* name = (byte*)list[i];
            if (name == null) continue;
            if (*name == 0 || *name == (byte)'*') return true;
            if (NameIs(name, PinDependent) || NameIs(name, PinStaticLight)) return true;
        }
        return false;
    }

    /// <summary><c>strcasecmp</c> against a NUL-terminated literal, the compare <c>Gob::DoEvent</c> makes.</summary>
    private static bool NameIs(byte* name, ReadOnlySpan<byte> want)
    {
        for (int i = 0; i < want.Length; i++)
        {
            byte a = name[i], b = want[i];
            if (a >= (byte)'A' && a <= (byte)'Z') a += 32;
            if (b >= (byte)'A' && b <= (byte)'Z') b += 32;
            if (a != b) return false;
            if (a == 0) return true;
        }
        return true;
    }

    /// <summary>Is this the frame this gob's reduced-rate step falls on? The phase comes from the gob POINTER,
    /// so a crowd does not advance in lockstep and spike one frame in <paramref name="rate"/>; the clock is
    /// <see cref="_lodClock"/>, which counts animates and not renders.</summary>
    /// <summary>
    /// The time elapsed over the <paramref name="rate"/> frames ending at this one, out of the scene's ring.
    /// <para><b>Exact while the gob's rate holds.</b> A gob whose rate just CHANGED is off by at most
    /// <c>rate-1</c> frame times, once: crossing <c>anim.loddist</c> outward its first held window counts frames
    /// it had already advanced through, and crossing inward the frames it held are not counted at all -- about
    /// 17 ms at the default rate, absorbed by the run's own clock within that step. A gob's first due step after
    /// it spawns is the same case. The alternative, a latched per-gob clock value, buys exactness in a term
    /// smaller than one frame of animation; it is named here rather than claimed away. Keyed events are
    /// unaffected either way: <see cref="AnimEvents.Fire"/> covers whatever window it is handed.</para></summary>
    private static float Elapsed(int rate)
    {
        float sum = 0f;
        for (int k = 0; k < rate && k < DtRing; k++) sum += _dtRing[(_lodClock - k) & (DtRing - 1)];
        return sum;
    }

    private static bool LodDue(IntPtr gob, int rate)
    {
        uint phase = (uint)(Mix((ulong)gob) >> 40);
        return ((uint)_lodClock + phase) % (uint)rate == 0;
    }

    /// <summary>splitmix64's finalizer: the gob's address spread into well-mixed bits.</summary>
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        return z ^ (z >> 31);
    }

    /// <summary>
    /// AN4: the every-fifth-animate bounding-sphere refresh, on the frames it can change anything.
    /// <para><b>What the sphere is and who reads it</b> (nm addresses; the decompile's are these plus
    /// 0x100000). <c>Gob::GetMinimumSphere</c> @0x4370a0 writes
    /// <see cref="EngineLayout.GobRadius"/> and <see cref="EngineLayout.GobCentre"/> from
    /// <c>Part::GetMinimumSphere</c> over the part tree plus the root part's local offset, scaled by the
    /// adjusted scale. It is a pure function of the tree's POSE and SHAPE. The raw readers are
    /// <c>Scene::AddPartsToDrawBuckets</c> @0x4751d0 and the engine's BSP visitor @0x481000 (both on the
    /// engine's bucket path), and on an Alloy frame <see cref="GobLod"/>'s LOD distance and
    /// <c>SampleVisibleGobs</c>' far-plane test. <c>Gob::GetMinimumBoundingSphere</c> @0x437050 refreshes
    /// through the vtable before it reads, so every caller of the accessor is exact whatever this does.</para>
    /// <para><b>The survey's premise was wrong on one point, recorded here:</b> the sphere does NOT feed
    /// <c>Gob+0x1b</c>. <c>Gob::IsInViewVolume</c> @0x444780 returns that byte and its only writers are
    /// <c>Gob::EnableInViewVolume</c> @0x436f10, <c>Gob::DisableInViewVolume</c> @0x436f30 and the
    /// constructor -- no sphere is involved.</para>
    /// <para><b>The gate.</b> A gob that advanced nothing this step (no behaviour, no look-at, no run) has the
    /// same pose it had, and <see cref="EngineLayout.GobGeomDirty"/> is the engine's own note that the active
    /// tree changed under an LOD swap, so the shape term is covered too. The third term is the SCALE, which the
    /// sphere is also a function of -- see <see cref="UnitScale"/>. One refusal in <see cref="SphereAudit"/> is
    /// taken anyway and the answer compared, which is what makes the skip a claim rather than a hope.</para>
    /// </summary>
    private static void Sphere(Gob g, bool moved)
    {
        if (moved || g.GeomDirty != 0 || !UnitScale(g)) { _spheres++; g.GetMinimumSphere(); return; }
        _sphereSkipped++;
        if ((++_sphereTick & (SphereAudit - 1)) != 0) return;
        float r = g.Radius;
        Vector3 c = g.Centre;
        _spheres++;
        g.GetMinimumSphere();
        if (g.Radius != r || g.Centre != c) _sphereMiss++;
    }

    /// <summary>
    /// Is this gob's adjusted scale exactly 1 and unable to move on its own? Only then is the bounding sphere a
    /// function of the pose and the shape alone, which is what lets <see cref="Sphere"/> refuse a refresh.
    /// <para><c>Gob::GetAdjustedScale</c> @0x437cf0 is <c>LerpFloat::Lerped(gob+0x1c0) * *(float*)(gob+0x49c)</c>
    /// and <c>Gob::GetMinimumSphere</c> multiplies both the radius and the centre by it. A lerp in flight
    /// changes that number every frame with no run advancing and no dirty byte set, and
    /// <c>Gob::CalculateFinalScale</c> @0x43eef0 writes <c>+0x49c</c> without marking anything -- so neither
    /// <c>moved</c> nor the LOD-swap byte covers a scaling gob.</para>
    /// <para>Rather than remember a scale per gob, the test is the strongest thing that needs no memory: a
    /// settled lerp (<see cref="EngineLayout.LerpMode"/> 0, so <c>Lerped</c> returns
    /// <see cref="EngineLayout.LerpValue"/> verbatim) whose value and base are both exactly 1. Anything else
    /// keeps the engine's every-fifth-frame cadence, which in a city is the handful of gobs that are scaled at
    /// all. Provably complete: a gob that passes cannot have a scale other than 1.0f, this frame or the next.</para>
    /// </summary>
    private static bool UnitScale(Gob g)
    {
        var lerp = g.ScaleLerp;
        return lerp.Mode == 0 && lerp.Value == 1f && g.ScaleBase == 1f;
    }

    /// <summary>A moving gob is a wind point source. LIVE: <c>Gob::SetWind</c> @0x436f40 has no direct caller,
    /// but <c>CNWCCreature::Update</c> reaches it every update through <c>CNWCObject::SetWind</c> @0x8e8df0 and
    /// the <c>CAurObject</c> vtable (+0x1c0), with <c>creaturewindintensity</c>, gated on the client's creature
    /// wind option -- which is what arms <see cref="EngineLayout.GobWindSource"/>.</summary>
    private static void Wind(Gob g, IntPtr scene)
    {
        if (g.WindSource == 0 || scene == IntPtr.Zero) return;
        Vector3 prev = g.PrevPosition, pos = g.Position;
        if (prev.X == NoPrev && prev.Y == NoPrev && prev.Z == NoPrev) return;
        if (prev == pos) return;
        IntPtr wm = new Scene(scene).WindManager;
        if (wm == IntPtr.Zero) return;
        // The engine's argument order is (origin, radius = 1, lifetime = distance moved, strength = Gob+0x1a0).
        _gusts++;
        AurXform.Gust(wm, new AurXform.V3 { X = pos.X, Y = pos.Y, Z = pos.Z }, GustRadius, (pos - prev).Length(), g.WindStrength);
    }

    /// <summary>The three animate gates, in the engine's order.</summary>
    private static bool Gates(Gob g, IntPtr scene)
    {
        if (scene != IntPtr.Zero && new Scene(scene).IsAnimating() == 0 && g.IsForceAnimated() == 0) return false;
        return g.IsAnimating() != 0;
    }

    /// <summary>An off-screen gob does not advance its animation at all, unless it is exempt.</summary>
    private static bool Visible(Gob g)
    {
        IntPtr node = g.Node.Ptr;
        if (node == IntPtr.Zero) return true;
        return g.IsInViewVolume() != 0 || g.AnimOffScreen != 0 || (new Model(node).OwnerFlags & 1) != 0;
    }

    /// <summary>The look-at run: stepped while it is active, torn down when it is not. False = the gob died.</summary>
    private static bool LookAt(Gob g, IntPtr gob, ref SafePtr sp, ref bool moved)
    {
        IntPtr la = g.LookAt.Ptr;
        if (la == IntPtr.Zero) return true;
        var l = new LookAtAnimation(la);
        if (l.IsActive() != 0) { l.Control(); moved = true; return sp.Object != IntPtr.Zero; }

        _lookAts++;
        moved = true;
        IntPtr anim = l.Animation.Ptr;
        for (int i = g.RunCount - 1; i >= 0; i--)
        {
            var r = g.Run(i);
            if (!r.IsNull && r.Animation.Ptr == anim) _delIndex(gob + EngineLayout.GobAnimRuns, i);
        }
        IntPtr run = l.Run.Ptr;
        // ponytail: the engine calls AnimRun::~AnimRun (@0x497c20, an empty body on this build) and then the
        // SIZED delete; this calls the unsized one, which libstdc++ and the game's CRT both route to free.
        // The upgrade, if AnimRun ever gains a member with a destructor: call the dtor symbol first.
        if (run != IntPtr.Zero) _free(run);
        la = g.LookAt.Ptr;
        if (la != IntPtr.Zero)
        {
            // The DELETING destructor: Itanium gives it a slot of its own and one argument, MSVC folds it
            // into slot 0 and passes 1 to mean "and free the storage".
            IntPtr fn = new LookAtAnimation(la).DeleteFn;
            if (OperatingSystem.IsWindows()) ((delegate* unmanaged<IntPtr, uint, void>)fn)(la, 1u);
            else ((delegate* unmanaged<IntPtr, void>)fn)(la);
        }
        g.LookAt = default;
        return sp.Object != IntPtr.Zero;
    }

    /// <summary>
    /// The queue advance, oldest run to newest (index <c>n-1</c> down to 0), so the newest -- index 0 --
    /// blends over everything above it and dominates. False = the gob died under an event.
    /// </summary>
    private static bool Advance(Gob g, IntPtr gob, float dt, int n, ref SafePtr sp, ref bool moved)
    {
        IntPtr list = gob + EngineLayout.GobAnimRuns;
        for (int i = n - 1; i >= 0; i--)
        {
            if (i >= g.RunCount) continue;
            var r = g.Run(i);
            if (r.IsNull) continue;
            uint fl = r.Flags;
            if ((fl & Queued) != 0) continue;
            float rate = r.Rate;
            r.PrevTime = r.Time;
            float t = r.Time + dt * rate;
            r.Time = t;
            float w = 0f;

            if ((fl & FadeOut) == 0)
            {
                var a = r.Animation;
                if (t < 0f && rate <= 0f)
                {
                    if ((fl & Loop) != 0)
                    {
                        Looped(gob, a.Ptr);
                        if (sp.Object == IntPtr.Zero) return false;
                        t = r.Time; a = r.Animation; fl = r.Flags;
                    }
                    t += a.Length;
                    if (t < 0f) { r.Time = 0f; t = 0f; } else r.Time = t;
                }
                float len = a.Length;
                if (t <= len || r.Rate < 0f) { w = r.Blend; }
                else
                {
                    if ((fl & Loop) != 0)
                    {
                        Looped(gob, a.Ptr);
                        if (sp.Object == IntPtr.Zero) return false;
                        a = r.Animation; t = r.Time; fl = r.Flags; len = a.Length;
                    }
                    w = r.Blend;
                    r.Time = t - len;
                    if (t - len > a.Length) r.Time = a.Length;
                }
            }
            else if (r.Done != 0)
            {
                // Removed FIRST, then the event, then freed -- so a handler that walks the queue cannot see
                // the run it is being told about.
                IntPtr anim = r.Animation.Ptr, ptr = r.Ptr;
                _remove(list, ptr);
                _dropped++;
                Finished(gob, anim);
                _free(ptr);
                if (sp.Object == IntPtr.Zero) return false;
                continue;
            }
            else
            {
                bool ramp = false;
                if (t >= 0f || rate > 0f)
                {
                    if (t > r.Animation.Length && rate >= 0f) r.Time = r.Animation.Length;
                    else if ((fl & FadeNow) == 0) { w = r.Blend; ramp = true; }
                }
                else r.Time = 0f;

                if (!ramp)
                {
                    if (i > 0)
                    {
                        var u = g.Run(i - 1);
                        if (!u.IsNull && (u.Flags & Queued) != 0)
                        {
                            u.Flags &= ~Queued;
                            Start(gob, u.Animation.Ptr);
                            if (sp.Object == IntPtr.Zero) return false;
                            fl = r.Flags;
                        }
                    }
                    float ft = r.BlendTime;
                    w = r.Blend - (ft <= 0f ? 2f : (dt + dt) / ft);
                    r.Blend = w;
                    if (!(w > 0f))
                    {
                        r.Blend = 0f;
                        r.Done = 1;
                        // A zero fade time is "no fade": the run plays at full weight for this one frame and
                        // is removed on the next visit.
                        if (ft == 0f) { r.Blend = 1f; w = 1f; } else w = 0f;
                    }
                }
            }

            // The weight ramp. Reached from every path above, including the fade-out one while its weight is
            // still positive -- which is why the effective fade-out step is dt/fadeTime and not 2*dt/fadeTime
            // (the 2x subtraction above is immediately followed by a 1x addition here).
            if (r.Done == 0 && w < 1f)
            {
                float step = r.BlendTime > 0f ? dt / r.BlendTime : 1f;
                w += step;
                if (w <= 1f) { r.Blend = w; _crossfades++; }
                else
                {
                    r.Blend = 1f;
                    w = 1f;
                    if ((fl & Sticky) == 0)
                        while (g.RunCount > i + 1)
                        {
                            var old = g.Run(i + 1);
                            if (old.IsNull) break;
                            _remove(list, old.Ptr);
                            _free(old.Ptr);
                            _dropped++;
                        }
                    fl = r.Flags;
                    w = r.Blend;
                }
            }

            *_pPosScale = r.PosScale;
            // The FIELD, not the rate cached at the top: an event fired above may have changed it.
            int reverse = r.Rate < 0f ? 1 : 0;
            if ((fl & Trace) != 0)
            {
                *_pTraceLength = r.Animation.Length;
                *_pTraceRun = r.Ptr;
                *_pTraceFlags = reverse;
            }
            IntPtr root = g.PartTree.Ptr, animNode = r.Animation.Node.Ptr;
            // Either null faults inside the engine's own AnimateHierarchy (it reads Part+0x60 and calls the
            // node's control pointer unconditionally); refuse it here and COUNT it rather than reproduce the
            // fault or drop it silently.
            if (root != IntPtr.Zero && animNode != IntPtr.Zero)
                AnimHierarchy.Walk(root, animNode, r.PrevTime, r.Time, CrossfadeService.Weight(w));
            else _rootless++;
            fixed (IntPtr* alive = &sp.Object)
                AnimEvents.Fire(gob, r.Animation.Ptr, r.PrevTime, r.Time, reverse, alive);
            *_pTraceRun = IntPtr.Zero;
            *_pTraceFlags = 0;
            *_pTraceLength = 0f;
            _runs++;
            moved = true;
            if (sp.Object == IntPtr.Zero) return false;
        }
        return true;
    }

    // ================= plumbing =================

    /// <summary>Unregister the death notice, then report the motion. A gob that died under the last thing
    /// this step ran (<c>Part::Animate</c> or the sphere refresh) is neither unregistered -- the list belongs
    /// to the freed object -- nor reported: the engine exits outright there, and a freed pointer in the
    /// transform owner's registry would alias a recycled gob into one spurious "moved" frame.</summary>
    private static void Done(IntPtr gob, ref SafePtr sp, bool moved)
    {
        if (sp.Object == IntPtr.Zero) return;
        Drop(sp.Object, ref sp);
        Mark(gob, moved);
    }

    /// <summary>The engine's swap-remove (@0x497370): the last entry takes the freed slot and learns its new
    /// index. Ported rather than called -- it has no symbol of its own on either build.</summary>
    private static void Drop(IntPtr owner, ref SafePtr sp)
    {
        int i = sp.Index;
        IntPtr* data = (IntPtr*)*(IntPtr*)(owner + EngineLayout.GobSafePointers);
        int* count = (int*)(owner + EngineLayout.GobSafePointerCount);
        int n = *count;
        if (i >= 0 && i < n && data != null) { data[i] = data[n - 1]; *count = --n; }
        if (i >= 0 && i < n && data != null) *(int*)(data[i] + EngineLayout.SafePointerIndex) = i;
        sp.Index = -1;
    }

    /// <summary>Tell the transform owner which gobs this runtime wrote. Cheap to skip: a gob with no running
    /// animation, no behaviour and no look-at never gets here with <paramref name="moved"/> set.</summary>
    private static void Mark(IntPtr gob, bool moved)
    {
        if (!moved) return;
        _marked++;
        GobMotion.Mark(gob);
    }

    /// <summary>The three lifecycle events, each counted for the period line.</summary>
    private static void Start(IntPtr gob, IntPtr anim) { _starts++; Fire(gob, StartPrefix, anim); }
    /// <inheritdoc cref="Start"/>
    private static void Looped(IntPtr gob, IntPtr anim) { _loops++; Fire(gob, LoopPrefix, anim); }
    /// <inheritdoc cref="Start"/>
    private static void Finished(IntPtr gob, IntPtr anim) { _dones++; Fire(gob, DonePrefix, anim); }

    /// <summary>One lifecycle event: <c>"start"</c>, <c>"loop"</c> or <c>"done"</c> followed by the
    /// animation's name, as the engine's <c>sprintf</c> builds it.</summary>
    private static void Fire(IntPtr gob, ReadOnlySpan<byte> prefix, IntPtr anim)
    {
        // A span compares by reference, so the kind is counted at the three call-site helpers below.
        if (anim == IntPtr.Zero) return;
        byte* buf = stackalloc byte[NameMax];
        int k = 0;
        for (; k < prefix.Length; k++) buf[k] = prefix[k];
        byte* src = (byte*)(anim + EngineLayout.AnimationName);
        while (k < NameMax - 1 && *src != 0) buf[k++] = *src++;
        buf[k] = 0;
        new Gob(gob).DoEvent(buf, IntPtr.Zero);
    }
}
