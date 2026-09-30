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
/// <item>the behaviour solvers, <c>Part::Animate</c> (and so the dangly solve) and
/// <c>NewController::ReportKeys</c> are dispatched exactly where the engine dispatches them.</item>
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
                        : !AnimHierarchy.Attach() ? "the node Control table"
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
                 + $"solve {_ticks * toMs / frames:F3} ms/f (peak gob {_peakTicks * toMs:F3} ms)";
        if (AnimHierarchy.Morphs != 0) s += $", {AnimHierarchy.Morphs / (float)frames:F1} morphs/f";
        if (AnimHierarchy.Traced != 0) s += $", {AnimHierarchy.Traced / (float)frames:F1} traced/f";
        if (_lookAts != 0) s += $", {_lookAts} look-at teardowns";
        if (_gusts != 0) s += $", {_gusts} gusts";
        int bez = AnimKeys.TakeBezier();
        if (bez != 0) s += $", {bez / (float)frames:F1} bezier/f (engine getter)";
        if (AnimHierarchy.HandedBack != 0) s += $", {AnimHierarchy.HandedBack} UNKNOWN node classes handed back";
        if (_rootless != 0) s += $", {_rootless} runs REFUSED (no part tree or no animation root node)";
        Reset();
        return s;
    }

    private static void Reset()
    {
        _scenes = _gobs = _skipped = _runs = _crossfades = _dropped = _gusts = _lookAts = _marked = _rootless = 0;
        _starts = _loops = _dones = 0;
        _ticks = _peakTicks = 0;
        AnimHierarchy.Parts = AnimHierarchy.Controllers = AnimHierarchy.Morphs = 0;
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

        if (!Advance(g, gob, dt, n, ref sp, ref moved)) return;

        IntPtr root = g.PartTree.Ptr;
        if (root != IntPtr.Zero)
        {
            // Virtual: this is how PartDanglyMesh::Animate (and so DanglyPhysics) is reached.
            new Part(root).Animate(dt);
            short c = (short)((g.SphereTick + 1) % SphereEvery);
            g.SphereTick = c;
            if (c == 0) g.GetMinimumSphere();
        }
        Done(gob, ref sp, moved);
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
