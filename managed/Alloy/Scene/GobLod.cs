using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-105: <b>Alloy chooses each gob's LOD.</b> <c>Gob::SetLODModel</c> @0x43e6a0 ROTATES a gob's
/// three part-tree roots (<c>gob+0x90/0x98/0xa0</c>) and their three <c>Model*</c>
/// (<c>gob+0x68/0x70/0x78</c>) so the chosen pair sits at <c>+0x90</c>/<c>+0x68</c>, writes the level to
/// <c>gob+0x440</c>, calls <c>Gob::Reset</c> and sets the <c>ProcessGob</c> dirty byte. Its only caller is the
/// BSP visitor @0x481000, once per in-view gob per frame, and it announces nothing -- which is why
/// <see cref="SceneWalk"/> had to enumerate all THREE roots and why every routed part then paid a per-frame
/// "is my root still the active one" read (<c>Counters._skipLod</c>).
///
/// <para><b>How the choice is taken without duplicating the permutation.</b> Which levels a gob can reach
/// depends on which of the three slots are occupied, and that depends on the rotation state -- so Alloy does
/// not reimplement the swap. It decides the BAND (with hysteresis the engine has none of), writes the squared
/// distance at <see cref="EngineLayout.GobLodDist2"/> that lands in that band, calls the original so the engine
/// performs its own permutation, and restores the field (the gobbucket sort comparator reads it too). When the
/// band is the level the gob already holds, the original is not called at all -- which is also what the engine
/// would have done, since every branch of it returns early on that.</para>
///
/// <para><b>Hysteresis.</b> Vanilla switches on a bare threshold, so a camera sitting on an LOD radius pops
/// every frame. Alloy widens each boundary by <c>alloy.lodhyst</c> (a fraction of DISTANCE, applied squared)
/// in the direction of travel and only ever moves one level at a time. <c>alloy.ownlod 0</c> is the A/B: the
/// engine's own choice, and <see cref="SceneWalk"/> goes back to enumerating all three roots.</para>
///
/// <para><b>The swap is DEFERRED until Alloy can draw the new tree</b> (review, 2026-09-30). The first cut
/// swapped immediately, and that opened a hole nothing closed for up to <c>AlloyRenderer.EnrollFloor</c>
/// frames: the instant <c>gob+0x90</c> rotates, <c>RouteLive</c>'s active-root gate refuses every enrolled part
/// of the OLD root, while the new root's parts are not in the part table until the re-walk and have no
/// ownership slot until the enrolment after it -- so the gob went INVISIBLE across its own LOD swap.
/// <list type="bullet">
/// <item>A wanted level change is <b>registered</b>, not performed. The engine's state is untouched, so the
/// active tree keeps animating and keeps drawing exactly as before.</item>
/// <item>While a gob is registered, <see cref="SceneWalk"/> enumerates its ALTERNATE roots as well, so their
/// parts enter the part table and the next enrolment seats them. <see cref="WantEnroll"/> lets that enrolment
/// bypass the floor, because a delta enrolment of a handful of parts is cheap.</item>
/// <item>The swap <b>commits</b> on the next call, once <see cref="Ready"/> says every part of every root is
/// bound AND owned. At that instant <c>gob+0x90</c> and <c>RouteLive</c>'s verdict flip together, with both
/// sides' records live: there is no frame in which the gob is undrawn.</item>
/// <item>A candidate that will not become drawable (a part the census refuses, geometry that never binds)
/// times out after <see cref="PendingMax"/> frames and the swap is CANCELLED with a cooldown -- the gob keeps
/// the LOD it is already drawing, which is the safe failure. <c>lod-stuck=</c> on the period line counts it.
/// </item>
/// </list>
/// So the steady state is one root per gob, and a gob crossing an LOD radius costs two frames of three.</para>
/// </summary>
internal static unsafe class GobLod
{
    private static GobSetLODModelFn _orig;
    private static int* _enable;
    private static float* _scaleFactor;
    private static bool _resolved;
    private static ExtenderLog? _log;

    /// <summary>Alloy owns the choice: the hook installed, both globals resolved, and <c>alloy.ownlod</c> on.
    /// <see cref="SceneWalk"/> reads this to decide whether one root per gob is enough.</summary>
    internal static bool Armed => _resolved && Renderer.OwnLod.V;

    /// <summary>A gob swapped its active root since the last walk, so the part table names trees that are not
    /// being drawn. Coalesced: one re-walk request per frame however many gobs swapped.</summary>
    internal static bool Swapped;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _enable = (int*)Native.Sym(SymbolMap.EnableLod);
        _scaleFactor = (float*)Native.Sym(SymbolMap.LodScaleFactor);
        _orig = hooks.InstallGobSetLODModel(&OnSetLod);
        _resolved = _orig != null && _enable != null && _scaleFactor != null;
        if (!_resolved)
            log.Always("GobLod: " + (_orig == null ? SymbolMap.GobSetLODModel
                                   : _enable == null ? SymbolMap.EnableLod : SymbolMap.LodScaleFactor)
                       + " unresolved -- the engine keeps the LOD choice and SceneWalk enumerates all three "
                       + "roots per gob, as before CX-105");
        else
            log.Info($"GobLod: Alloy chooses each gob's LOD (hysteresis {Renderer.LodHyst.V:0.00} of distance); "
                     + "SceneWalk enumerates one root per gob (alloy.ownlod 0 hands both back)");
    }

    /// <summary>HOT: once per in-view gob per frame, engine context, allocation-free.</summary>
    [UnmanagedCallersOnly]
    private static void OnSetLod(IntPtr gob)
    {
        // The deferral protects ALLOY's tables, so it only applies while Alloy is the renderer. On a vanilla
        // frame (a stand-down, a failed gate, no module) the engine owns the frame and nothing waits on a
        // part table -- deferring there would simply freeze every gob's LOD, and Tick is not being called to
        // age the table either.
        if (!Armed || gob == IntPtr.Zero || !AlloyGate.Alloy)
        {
            int hv = Slot(gob, false);
            if (hv >= 0 && _pWant[hv] != NoWant) { _pWant[hv] = NoWant; _cancelled++; }
            if (_orig != null) _orig(gob);
            return;
        }
        var g = new Gob(gob);
        int cur = g.LodLevel;
        float d2 = g.LodDist2;
        int want = Choose(d2, g.LodNear, g.LodFar, cur, *_enable != 0, *_scaleFactor, Renderer.LodHyst.V,
                          !g.LodRoot1.IsNull || !g.LodRoot2.IsNull);
        int h = Slot(gob, want != cur);
        if (want == cur)
        {
            // The camera came back inside the band before the swap could commit: drop it. Nothing was done to
            // the engine, so there is nothing to undo.
            if (h >= 0 && _pWant[h] != NoWant) { _pWant[h] = NoWant; _cancelled++; }
            _held++;
            return;
        }
        if (h < 0) { _overflow++; return; }          // no pending slot: the gob keeps its level, retried next frame
        // The liveness stamp Tick ages the table by: the BSP visitor calls this only for gobs it walked, so a
        // gob that leaves the view stops being asked and its registration must not go on forcing an enrolment.
        _pSeenAt[h] = _frame;
        if (_pWant[h] != want)
        {
            // A cooldown slot has just had a swap cancelled for want of a drawable candidate; asking again
            // every frame would re-walk the area every frame for a tree that is not going to bind.
            if (_frame - _pCoolAt[h] < _pCoolFor[h]) return;
            _pWant[h] = want; _pAt[h] = _frame; _pending++;
            // The alternates have to enter the part table before they can be owned, and only a walk puts
            // them there.
            Swapped = true;
            return;
        }
        if (!Ready(gob)) { _waiting++; return; }
        // COMMIT. The engine's own branch set decides whether `want` is REACHABLE for this rotation state;
        // all this has to do is put the distance in the band. The field is restored because the gobbucket
        // sort comparator @0x55e070 reads it as its key later in the same frame.
        _pWant[h] = NoWant;
        g.LodDist2 = BandDistance(want, g.LodNear, g.LodFar, *_scaleFactor);
        if (_orig != null) _orig(gob);
        g.LodDist2 = d2;
        if (g.LodLevel == cur) { _refused++; _pCoolAt[h] = _frame; _pCoolFor[h] = PendingCool; return; }
        _swaps++;
        Swapped = true;                              // the old root leaves the walk
        // The gob's whole active subtree changed under the part table AND under the transform owner.
        GobXform.Mark(gob);
    }

    /// <summary>Is every part of every one of this gob's three roots drawable by Alloy -- in the part table,
    /// BOUND (a VDO and an item), and seated in the ownership table? That is the condition the commit turns
    /// on: it is asked of all three roots rather than of the one the engine is about to pick, because which
    /// one that is depends on the rotation state <c>Gob::SetLODModel</c> keeps and this deliberately does not
    /// reimplement.
    /// <para>A part whose subtree the engine itself gates off (<c>part+0x34 == 0</c>) and one the part table
    /// has killed for a permanent reason are READY: vanilla draws nothing for either, so waiting on them would
    /// mean never switching LOD at all. A part still waiting for <c>SetupArrays</c>, or active but unseated,
    /// is not.</para>
    /// <para><b>A candidate the census REFUSES is deliberately never ready</b>, and the swap is not made. Its
    /// parts stay <c>OwnFlag == 0</c>, so this answers no until the <see cref="PendingMax"/> timeout cancels
    /// the registration and the gob keeps the LOD it is already drawing. That is the right end of the trade:
    /// swapping onto a tree Alloy cannot own whole would put the refusable part on the ACTIVE root, where
    /// <see cref="AlloyCensus.InactiveLodPart"/> no longer exempts it, and the all-owned-or-none sweep would
    /// drop the whole creature. Extra triangles at distance beats an invisible creature. The exemption and
    /// this rule are two halves of one decision: the exemption is what stops the WAIT from costing the
    /// creature, and this is what stops the SWAP from costing it. <c>lod-stuck=</c> is the count.</para>
    /// </summary>
    private static bool Ready(IntPtr gob)
    {
        var g = new Gob(gob);
        return TreeReady(g.PartTree.Ptr) && TreeReady(g.LodRoot1.Ptr) && TreeReady(g.LodRoot2.Ptr);
    }

    /// <summary>Bound walk of one root. Iterative: this runs on the render thread with an unbounded tree depth
    /// in front of it, exactly as <see cref="SceneWalk"/>'s own walk does.</summary>
    private static bool TreeReady(IntPtr root)
    {
        if (root == IntPtr.Zero) return true;
        int sp = 0;
        _stack[sp++] = root;
        while (sp > 0)
        {
            var part = new Part(_stack[--sp]);
            // The engine's own subtree gate: SceneWalk prunes here too, so nothing below is in the table.
            if (part.Visible == 0) continue;
            IntPtr tri = part.AsPartTriMesh().Ptr;
            if (tri != IntPtr.Zero && !PartReady(tri)) return false;
            IntPtr* kids = (IntPtr*)part.Children;
            int n = part.ChildCount;
            if (kids == null) continue;
            for (int i = 0; i < n; i++)
            {
                if (kids[i] == IntPtr.Zero) continue;
                if (sp >= StackMax) return false;     // an overflowed walk cannot promise anything
                _stack[sp++] = kids[i];
            }
        }
        return true;
    }

    private static bool PartReady(IntPtr tri)
    {
        int e = PartTable.Find(tri);
        if (e < 0) return false;                                   // no record yet: the walk has not reached it
        byte st = PartTable._pState[e];
        if (st == PartTable.StUnbound || st == PartTable.StStale) return false;
        if (st != PartTable.StActive) return true;                 // hidden or dead: nothing draws it either way
        return AlloyCensus.OwnFlag[e] != 0;                        // active: it must be SEATED, not just bound
    }

    /// <summary>A tree walk's explicit stack. Same sizing note as <see cref="SceneWalk"/>'s: a part tree is
    /// wide and shallow, so this is a runaway guard rather than a sizing decision, and an overflow refuses the
    /// swap rather than promising a readiness it did not establish.</summary>
    private const int StackMax = 4096;
    private static readonly IntPtr[] _stack = new IntPtr[StackMax];

    // ---- the pending table: gob -> the level it wants, open-addressed, generation-stamped ----
    /// <summary>Sized well past the number of gobs that can cross an LOD radius at once. An area load sets
    /// every gob's level for the first time, which is the burst this has to absorb; an overflow leaves that
    /// gob's swap for the next frame and is counted.</summary>
    private const int PendBits = 11, PendCap = 1 << PendBits, PendMask = PendCap - 1;
    private const int NoWant = -1;
    private static readonly IntPtr[] _pKey = new IntPtr[PendCap];
    private static readonly int[] _pWant = new int[PendCap];
    private static readonly int[] _pAt = new int[PendCap];
    /// <summary>The frame <see cref="OnSetLod"/> was last called for this gob. <see cref="Tick"/> cancels a
    /// registration that was not asked for this frame: the BSP visitor only calls the hook for gobs it walked,
    /// so a gob leaving the view would otherwise leave a registration standing -- enumerating three roots and
    /// forcing an enrolment every frame for something nobody is looking at.</summary>
    private static readonly int[] _pSeenAt = new int[PendCap];
    private static readonly int[] _pCoolAt = new int[PendCap];
    /// <summary>...and how long that cooldown runs: <see cref="StuckCool"/> for a candidate that never became
    /// drawable, <see cref="PendingCool"/> for a swap the engine refused for want of a root.</summary>
    private static readonly int[] _pCoolFor = new int[PendCap];
    private static readonly int[] _pSeen = new int[PendCap];
    private static int _pGen = 1, _pCount, _frame;
    /// <summary>Frames a registered swap may wait for its candidate to become drawable, and how long a gob
    /// whose candidate never did is left alone afterwards. A second and four seconds at 60 fps: long enough
    /// for the <c>SetupArrays</c> drip and an enrolment, short enough that <c>lod-stuck=</c> means something.
    /// </summary>
    /// <summary>Frames a registered swap may wait for its candidate to become drawable, then how long a gob
    /// whose candidate never became drawable is left alone (30 s: a candidate the census refuses will refuse
    /// again, and one retry per half-minute keeps the walk churn off the period line while still healing a
    /// transient -- geometry that was not resident, an enrolment that was at capacity), and the shorter one
    /// for a swap the ENGINE refused for want of a root, which is cheap to re-ask.</summary>
    private const int PendingMax = 60, StuckCool = 1800, PendingCool = 240;

    /// <summary>Is any swap waiting on an enrolment? Read by the enrol gate so a LOD-only change bypasses
    /// <c>AlloyRenderer.EnrollFloor</c> -- the delta enrolment of a handful of parts is cheap, and the floor is
    /// there to bound the COLD full scan.</summary>
    internal static bool WantEnroll => _pCount > 0;

    /// <summary>Is a swap registered for this gob -- i.e. must <see cref="SceneWalk"/> enumerate its alternate
    /// roots this walk? One probe, no dereference.</summary>
    internal static bool Pending(IntPtr gob)
    {
        if (_pCount == 0 || gob == IntPtr.Zero) return false;
        int h = Slot(gob, false);
        return h >= 0 && _pWant[h] != NoWant;
    }

    /// <summary>The pending table's slot for <paramref name="gob"/>, or -1 when absent (and
    /// <paramref name="insert"/> is clear) or the table is full. A slot stamped with an older generation reads
    /// empty, so an area edge costs no clear.</summary>
    private static int Slot(IntPtr gob, bool insert)
    {
        if (gob == IntPtr.Zero) return -1;
        int h = (int)((ulong)gob >> 4) & PendMask;
        for (int probe = 0; probe <= PendMask; probe++, h = (h + 1) & PendMask)
        {
            if (_pSeen[h] != _pGen)
            {
                if (!insert) return -1;
                _pSeen[h] = _pGen; _pKey[h] = gob;
                _pWant[h] = NoWant; _pAt[h] = 0; _pSeenAt[h] = 0;
                _pCoolAt[h] = int.MinValue / 2; _pCoolFor[h] = PendingCool;
                return h;
            }
            if (_pKey[h] == gob) return h;
        }
        // Full. Entries are never removed -- a cancelled swap keeps its slot for the cooldown -- so over a
        // long session the table can fill with cooldowns alone. Forgetting the whole table costs the
        // cooldowns and nothing else (a registration is never a write to the engine), and the re-probe lands
        // in an empty one.
        if (insert)
        {
            _overflow++;
            _pGen++; _pCount = 0;
            h = (int)((ulong)gob >> 4) & PendMask;
            _pSeen[h] = _pGen; _pKey[h] = gob;
            _pWant[h] = NoWant; _pAt[h] = 0; _pSeenAt[h] = 0;
            _pCoolAt[h] = int.MinValue / 2; _pCoolFor[h] = PendingCool;
            return h;
        }
        return -1;
    }

    /// <summary>The area changed, or the knob was flipped: every gob address in the pending table is about to
    /// mean something else. Nothing has to be undone -- a registered swap was never performed.</summary>
    internal static void Reset() { _pGen++; _pCount = 0; Swapped = true; }

    /// <summary>CX-104's skip experiment (<c>alloy.skipbuckets</c> 1 or 3): drive the two per-gob engine steps
    /// the skipped walks used to make. With <c>Scene::AddPartsToDrawBuckets</c> gone nothing calls
    /// <c>Gob::ProcessGob</c>, which refreshes <see cref="EngineLayout.GobVisible"/> and the tree's geometry
    /// flags; with <c>ManageSceneBSP</c> gone nothing calls <c>Gob::SetLODModel</c> either.
    /// <para>Called from the TOP of <see cref="SceneWalk.Tick"/>, ahead of the walk -- ProcessGob's flags are
    /// what the walk's renderable gate reads, and the LOD call has to fire before the walk that consumes its
    /// edge. Driving it from the frame anchor (the first cut) put both a frame behind the walk.</para>
    /// <para>A no-op on <c>alloy.skipbuckets=2</c> or below, where both engine walks still run.</para>
    /// </summary>
    internal static void DriveSkipped(IntPtr scene)
    {
        int skip = Renderer.SkipBuckets.Boot;
        bool process = (skip == 1 || skip == 3) && EngineFn.GobProcessGob != null;
        bool lod = skip == 3 && Armed;
        if (!process && !lod) return;
        if (scene == IntPtr.Zero || !AlloyRenderer.IsWorldScene(scene)) return;
        var sc = new Scene(scene);
        IntPtr* gobs = (IntPtr*)sc.Gobs;
        int n = sc.GobCount;
        if (gobs == null || n <= 0) return;
        var cam = new Camera(AlloyCamera.World);
        bool haveCam = lod && !cam.IsNull;
        System.Numerics.Vector3 eye = haveCam ? cam.Pos : default;
        for (int k = 0; k < n; k++)
        {
            IntPtr gob = gobs[k];
            if (gob == IntPtr.Zero) continue;
            var g = new Gob(gob);
            if (process) g.GobProcessGob();
            if (!haveCam) continue;
            // The distance the engine's own BSP visitor @0x481000 writes: |camPos - (pos + rot * centre)|^2.
            System.Numerics.Vector3 c = g.Position
                + System.Numerics.Vector3.Transform(g.Centre, (System.Numerics.Quaternion)g.Rot);
            g.LodDist2 = (c - eye).LengthSquared();
            g.GobSetLODModel();
        }
    }

    /// <summary>The level Alloy wants, from the engine's own two thresholds plus hysteresis. Pure: the
    /// self-check drives it with no engine at all.
    /// <para><paramref name="hyst"/> is a fraction of DISTANCE, so it enters squared. Rising past a boundary
    /// needs <c>d2 &gt; T (1+h)^2</c>, falling back needs <c>d2 &lt; T / (1+h)^2</c>, and in between the level
    /// holds -- which is the whole anti-popping property. At most one level per call, matching the engine's own
    /// single-step branches.</para></summary>
    internal static int Choose(float d2, float near, float far, int cur, bool enabled, float scaleFactor,
                              float hyst, bool hasAlternate)
    {
        // The engine's own stand-downs, in its order: no LOD, no alternate root, no distance yet.
        if (!enabled || !hasAlternate || d2 < 0f || !(scaleFactor > 0f)) return cur;
        if (cur < 0 || cur > 2) return cur;
        float t1 = near * scaleFactor, t2 = far * scaleFactor;
        float t1sq = t1 * t1, t2sq = t2 * t2;
        int plain = d2 > t2sq ? 2 : d2 > t1sq ? 1 : 0;
        if (plain == cur) return cur;
        float up = (1f + hyst) * (1f + hyst);
        if (plain > cur)
        {
            float bound = cur == 0 ? t1sq : t2sq;
            return d2 > bound * up ? cur + 1 : cur;
        }
        float down = cur == 1 ? t1sq : t2sq;
        return d2 * up < down ? cur - 1 : cur;
    }

    /// <summary>A squared distance squarely inside <paramref name="level"/>'s band, for the engine to re-derive
    /// the same level from. Level 0 is 0 (the engine stands down on a NEGATIVE distance, not on zero); the
    /// others step past their boundary by a relative epsilon so a float that lands exactly on it still
    /// crosses.</summary>
    internal static float BandDistance(int level, float near, float far, float scaleFactor)
    {
        float t1 = near * scaleFactor, t2 = far * scaleFactor;
        float t1sq = t1 * t1, t2sq = t2 * t2;
        if (level <= 0) return 0f;
        if (level == 1) return Past(t1sq);
        return Past(t2sq);
    }

    /// <summary>The next value the engine's strict <c>&lt;</c> accepts, with a floor for a zero threshold.</summary>
    private static float Past(float v) => v + MathF.Max(1e-3f, MathF.Abs(v) * 1e-3f);

    private static long _swaps, _held, _refused, _pending, _waiting, _stuck, _cancelled, _left, _overflow;

    /// <summary>Once per world frame, from the frame anchor: turn a swap into the re-walk it needs. The walk is
    /// edge-triggered, so this is the LOD edge (#256 §7.2's "no notice of any kind" made good).</summary>
    internal static void Tick()
    {
        // AGE the pending table, then recount it. The count drives WantEnroll and Pending(), and both have to
        // be false again the moment a registration stops being live -- the gob left the view (the hook is no
        // longer called for it) or its candidate is never going to become drawable.
        int n = 0;
        for (int h = 0; h < PendCap; h++)
        {
            if (_pSeen[h] != _pGen || _pWant[h] == NoWant) continue;
            if (_pSeenAt[h] != _frame) { _pWant[h] = NoWant; _left++; Swapped = true; continue; }
            if (_frame - _pAt[h] > PendingMax)
            {
                // No Swapped here. The alternates stop being enumerated at whatever walk comes next (the
                // engine bumps Scene::Sequence every 11-20 frames anyway), and asking for one per timeout
                // would re-walk the area on a cadence for a tree that is not going to become drawable.
                _pWant[h] = NoWant; _pCoolAt[h] = _frame; _pCoolFor[h] = StuckCool; _stuck++;
                continue;
            }
            n++;
        }
        _pCount = n;
        if (n > _pHigh) _pHigh = n;
        // LAST, so that a hook call earlier in this frame and this scan share one frame number.
        _frame++;
        if (!Swapped) return;
        Swapped = false;
        SceneWalk.RequestWalk();
        _walks++;
    }
    private static long _walks;
    private static int _pHigh;

    internal static string Period()
    {
        long frames = Math.Max(1, AlloyRenderer.Counters._frames);
        string s = Armed
            ? $"lod {(float)_held / frames:F0} held/frame, {_swaps} swaps of {_pending} wanted "
              + $"({_walks} re-walks, {_pCount} pending now / {_pHigh} peak, {_waiting} waits, "
              + $"lod-stuck={_stuck}, {_cancelled} cancelled by the camera, {_left} left the view, "
              + $"{_refused} the engine refused for "
              + $"want of a root{(_overflow == 0 ? "" : $", {_overflow} PENDING TABLE FULL")})"
            : "lod ENGINE'S (alloy.ownlod 0 or unresolved) -- all three roots enumerated";
        _swaps = _held = _refused = _walks = _pending = _waiting = _cancelled = _left = _overflow = 0;
        _pHigh = _pCount;
        return s;
    }

    /// <summary>Off-device: the band choice and its hysteresis, against numbers derived from
    /// <c>Gob::SetLODModel</c>'s two comparisons. The failure this goes red on is the one a live session cannot
    /// show you happening: a boundary with no hysteresis pops every frame when the camera sits on it, and a
    /// chooser that moves two levels at once skips the middle mesh entirely.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GobLod.SelfCheck");
        const float near = 10f, far = 20f, k = 1f, h = 0.1f;
        float up = (1f + h) * (1f + h);
        // The engine's plain verdict, with no hysteresis asked for: exactly its two comparisons.
        check.Expect(Choose(150f, near, far, 0, true, k, 0f, true) == 1, "past the near boundary (10 units, squared), level 0 rises one step");
        check.Expect(Choose(150f, near, far, 1, true, k, 0f, true) == 1, "...and holds there");
        check.Expect(Choose(500f, near, far, 1, true, k, 0f, true) == 2, "past the far boundary, level 1 rises");
        check.Expect(Choose(500f, near, far, 0, true, k, 0f, true) == 1, "...one level at a time, never two");
        check.Expect(Choose(150f, near, far, 2, true, k, 0f, true) == 1, "inside the middle band, level 2 falls one step");
        check.Expect(Choose(1f, near, far, 1, true, k, 0f, true) == 0, "inside the near band, level 1 falls");

        // Hysteresis: a distance just past the raw boundary must NOT switch, one past the widened one must.
        float justPast = near * near * 1.02f;
        check.Expect(Choose(justPast, near, far, 0, true, k, h, true) == 0,
                     "a distance just past the near boundary does not switch: that is the pop");
        check.Expect(Choose(near * near * up * 1.01f, near, far, 0, true, k, h, true) == 1,
                     "...and one past the widened boundary does");
        float justUnder = near * near * 0.98f;
        check.Expect(Choose(justUnder, near, far, 1, true, k, h, true) == 1, "coming back, level 1 holds inside the band");
        check.Expect(Choose(near * near / up * 0.99f, near, far, 1, true, k, h, true) == 0, "...and drops past it");

        // The stand-downs, each on its own: the engine returns without touching anything for all four.
        check.Expect(Choose(500f, near, far, 0, false, k, h, true) == 0, "g_bEnableLOD 0 leaves the level alone");
        check.Expect(Choose(500f, near, far, 0, true, k, h, false) == 0, "a gob with no alternate root has no choice");
        check.Expect(Choose(-1f, near, far, 0, true, k, h, true) == 0, "a negative distance (never measured) stands down");
        check.Expect(Choose(500f, near, far, 0, true, 0f, h, true) == 0, "a zero LOD scale factor stands down");
        // g_fLODScaleFactor really does scale: at 10x, the near boundary sits at 100 units.
        check.Expect(Choose(99f * 99f, near, far, 0, true, 10f, 0f, true) == 0
                     && Choose(101f * 101f, near, far, 0, true, 10f, 0f, true) == 1,
                     "g_fLODScaleFactor multiplies the switch distance before it is squared");

        // The fed distance must re-derive the level Alloy chose, through the engine's own comparisons.
        for (int lvl = 0; lvl <= 2; lvl++)
        {
            float d = BandDistance(lvl, near, far, k);
            int back = d > far * far ? 2 : d > near * near ? 1 : 0;
            check.Expect(back == lvl, $"the band distance for level {lvl} reads back as level {lvl}");
        }
        // A degenerate model (both thresholds 0) must still produce a distance the engine accepts.
        check.Expect(BandDistance(2, 0f, 0f, k) > 0f, "a zero threshold still yields a positive band distance");

        ReadySelfCheck(log, check);
        PendingSelfCheck(check);

        log.Info(check.Ok ? "GobLod.SelfCheck: passed"
                          : "GobLod.SelfCheck: FAILED -- the owned LOD choice disagrees with Gob::SetLODModel");
        return check.Ok;
    }

    /// <summary>Off-device: THE rule the zero-hole swap turns on (review). A commit may only happen once every
    /// part of every root is bound AND seated, because the instant <c>gob+0x90</c> rotates,
    /// <c>RouteLive</c>'s active-root gate refuses the old root's parts -- so a readiness that says yes too
    /// early is the gob going invisible across its own LOD swap, and one that says no forever is the LOD never
    /// switching. Neither reads as an error live.
    /// <para>Synthetic parts in unmanaged scratch, under a FAKE vtable whose <c>AsPartTriMesh</c> slot returns
    /// <c>this</c> -- the walk asks the engine's own discriminator, so the check has to answer it.</para>
    /// </summary>
    private static void ReadySelfCheck(ExtenderLog log, Checker check)
    {
        int bytes = 0x100;
        IntPtr blk = Marshal.AllocHGlobal(bytes * 2 + 0x200);
        try
        {
            new Span<byte>((void*)blk, bytes * 2 + 0x200).Clear();
            IntPtr root = blk, child = blk + bytes, vtbl = blk + bytes * 2;
            *(IntPtr*)(vtbl + EngineLayout.PartAsTriMeshSlot) = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr>)&SelfThis;
            IntPtr* kids = (IntPtr*)(blk + bytes * 2 + 0x100);
            kids[0] = child;
            foreach (IntPtr part in stackalloc[] { root, child })
            {
                *(IntPtr*)part = vtbl;
                new Part(part).Visible = 1;
            }
            var rv = new Part(root);
            rv.Children = (IntPtr)kids; rv.ChildCount = 1;

            int savedCount = PartTable._partCount;
            check.Expect(!TreeReady(root), "a root whose parts have no record yet is NOT ready");
            int er = PartTable.Insert(root), ec = PartTable.Insert(child);
            if (er < 0 || ec < 0) { log.Info("GobLod.SelfCheck FAIL: the part table refused a synthetic insert"); check.Expect(false, "synthetic insert"); return; }
            PartTable._pState[er] = PartTable.StActive; AlloyCensus.OwnFlag[er] = 1;
            PartTable._pState[ec] = PartTable.StUnbound; AlloyCensus.OwnFlag[ec] = 0;
            check.Expect(!TreeReady(root), "a part still waiting for SetupArrays (StUnbound) is NOT ready");
            PartTable._pState[ec] = PartTable.StStale;
            check.Expect(!TreeReady(root), "...nor one whose draw arguments went stale");
            PartTable._pState[ec] = PartTable.StActive;
            check.Expect(!TreeReady(root), "a BOUND part that enrolment has not seated (OwnFlag 0) is NOT ready");
            AlloyCensus.OwnFlag[ec] = 1;
            check.Expect(TreeReady(root), "bound and seated on every part IS ready");
            PartTable._pState[ec] = PartTable.StHidden;
            check.Expect(TreeReady(root), "a part the engine's own part+0x34 gate hides is ready -- nothing draws it");
            PartTable._pState[ec] = PartTable.StDead;
            check.Expect(TreeReady(root), "...and so is one the part table killed permanently");
            // A pruned subtree is ready even with no record at all: SceneWalk prunes on the same byte, so
            // waiting on what is below it would wait forever.
            PartTable._pState[ec] = PartTable.StEmpty; PartTable._pPart[ec] = IntPtr.Zero;
            new Part(child).Visible = 0;
            check.Expect(TreeReady(root), "a subtree the engine gates off is ready however little is recorded");
            check.Expect(TreeReady(IntPtr.Zero), "a gob with no alternate root is trivially ready");

            PartTable._pState[er] = PartTable.StEmpty; PartTable._pPart[er] = IntPtr.Zero;
            AlloyCensus.OwnFlag[er] = 0; AlloyCensus.OwnFlag[ec] = 0;
            PartTable._pWalkGen[er] = 0; PartTable._pWalkGen[ec] = 0;
            PartTable._partCount = savedCount;
        }
        finally { Marshal.FreeHGlobal(blk); }
    }

    /// <summary>The fake <c>Part::AsPartTriMesh</c> the readiness check installs: every synthetic part IS its
    /// own mesh, which is what the real slot returns for a <c>PartTriMesh</c>.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr SelfThis(IntPtr self) => self;

    /// <summary>Off-device: the pending table's three rules -- a registered gob reports itself to the walk, a
    /// reset forgets every registration without having performed one, and a cancelled swap is left alone for
    /// the cooldown rather than asked again every frame (which would re-walk the area every frame for a tree
    /// that is not going to bind).</summary>
    private static void PendingSelfCheck(Checker check)
    {
        int savedGen = _pGen, savedCount = _pCount, savedFrame = _frame;
        _pGen++; _pCount = 0; _frame = 1000;
        IntPtr g = (IntPtr)0x51000;
        check.Expect(!Pending(g), "a gob with no registered swap is not enumerated whole");
        int h = Slot(g, true);
        check.Expect(h >= 0, "the pending table takes a registration");
        _pWant[h] = 2; _pAt[h] = _frame; _pCount = 1;
        check.Expect(Pending(g), "a registered gob is enumerated whole until it commits");
        check.Expect(WantEnroll, "...and its enrolment bypasses the floor while it waits");
        _pWant[h] = NoWant; _pCoolAt[h] = _frame;
        _pCount = 0;
        check.Expect(!Pending(g) && !WantEnroll, "a committed or cancelled swap stops both");
        check.Expect(_frame - _pCoolAt[h] < _pCoolFor[h], "a cancelled swap is inside its cooldown");
        _frame += _pCoolFor[h];
        check.Expect(_frame - _pCoolAt[h] >= _pCoolFor[h], "...and out of it once the cooldown has run");
        Reset();
        check.Expect(!Pending(g) && !WantEnroll, "a reset forgets every registration");

        // Tick AGES the table. A registration the hook asked for again this frame survives; one it did not
        // (the gob left the view, so the BSP visitor stopped calling us) is cancelled at once -- otherwise it
        // would enumerate three roots and force an enrolment every frame for a gob nobody is looking at. And
        // one whose candidate never becomes drawable times out.
        _pGen++; _pCount = 0; _frame = 2000;
        h = Slot(g, true);
        _pWant[h] = 2; _pAt[h] = _frame; _pSeenAt[h] = _frame;
        Tick();
        check.Expect(Pending(g) && _pCount == 1, "a registration asked for again this frame survives Tick");
        Tick();
        check.Expect(!Pending(g) && _pCount == 0, "...and is cancelled the frame the hook stops asking");
        _pWant[h] = 2; _pAt[h] = _frame - PendingMax - 1; _pSeenAt[h] = _frame;
        Tick();
        check.Expect(!Pending(g) && _pCoolAt[h] == _frame - 1 && _pCoolFor[h] == StuckCool,
                     "a candidate that never became drawable times out into the LONG cooldown");

        _pGen = savedGen; _pCount = savedCount; _frame = savedFrame; Swapped = false;
    }
}

/// <summary>CX-105: the one hook the LOD chooser needs. Its own service so the install does not have to share
/// a file with anything else's.</summary>
public sealed unsafe class GobLodService : IExtenderService
{
    public void Initialize(ExtenderLog log, HookService hooks) => GobLod.Install(log, hooks);
}
