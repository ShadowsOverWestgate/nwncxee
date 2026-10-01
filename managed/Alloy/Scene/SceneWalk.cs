using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using static CX.Alloy.PartTable;

namespace CX.Alloy;

/// <summary>Alloy enumerates the world (ADR 0018 M5 strand 1 step 1, #256). The part table is filled by
/// walking the engine's authoritative containers -- <c>Scene+0x130</c> (every gob), each gob's active part-tree
/// root and its appended roots, and the tile BSP at <c>Scene+0x188</c> -- and NOT by watching the engine try to
/// draw something. Records cover every part in the area rather than the visible slice. (All THREE LOD roots,
/// while <see cref="GobLod"/> is unarmed or has a swap pending: see <see cref="GobTrees"/>.)
/// <para><b>Per-gob deltas, with the walk as the audit.</b> #253 §7.2 concluded that a notice-fed registry
/// could not work, because two membership edges announced nothing; both are hooked now, so it does. Every edge
/// NAMES its gob and costs that one tree: <c>Gob::SetScene</c> @0x43e020, the sole writer of the engine's
/// global epoch <c>Scene::Sequence</c> @0x18aa1b0, for a join or a leave (see <see cref="OnGobSetScene"/> for
/// the accounting that makes trusting it safe); <c>Gob::SetSubtreeEnabled</c> @0x43c670 for the hide and show
/// of a subtree on <c>part+0x34</c>, which is CX-92's edge and the one that used to be read back off the held
/// set; <c>Gob::Append</c> for a grafted item; <see cref="GobLod"/> for a rotation of the active root, which
/// <c>Gob::SetLODModel</c> @0x43e6a0 performs with no ctor, no dtor and no epoch bump; and
/// <c>Part::~Part</c>, which <see cref="DrawFunnelService"/> hooks. A tile names no gob --
/// <c>SpawnPartsForTile</c> passes <c>gob = NULL</c> -- so <c>Scene::SpawnTile</c>/<c>DeleteTile</c> ask for the
/// whole-area walk, as do a Scene change and a table wipe.
/// <para>The FULL walk survives as the net and the validator: every <c>alloy.walkaudit</c> frames (300) it runs
/// anyway and <c>audit misses=</c> counts what it found that the deltas had not, which must be 0.
/// <c>alloy.walkdelta 0</c> puts every epoch bump back on the full walk, which is what the 9,7ms 66-times-per-900-frames
/// stutter was measured on.</para>
/// Everything that is not membership -- transforms, the active LOD root, material bindings -- is still a
/// per-record FIELD READ rather than a notice.</para>
/// <para><b>Every part, means every part CLASS.</b> <c>Scene+0x130</c> and the BSP reach <c>PartLight</c>,
/// <c>PartEmitter</c>, <c>PartTrigger</c>, <c>PartProjection</c>, <c>PartReference</c> and <c>PartDummy</c>
/// as well, and their fields do not mean what a trimesh's mean at the same offsets. <see cref="Visit"/>
/// discriminates with the engine's own vtable slot 0x60 (<c>AsPartTriMesh</c>) before anything is recorded;
/// children are still recursed through every class, because a light or a dummy can parent geometry.</para>
/// <para><b>The bucket arrays are a validator, not a source.</b> <c>Scene+0x208</c> holds the finished,
/// classified, CULLED set; a 90 degree camera turn replaces most of it, so building the delta machinery on it
/// would mean building it twice (ADR 0018). <see cref="CrossCheck"/> asks them whether this walk has a hole,
/// and <see cref="_lastSample"/> counts what is left of the one class the cutover dropped.</para></summary>
internal static unsafe class SceneWalk
{
    /// <summary>Bound on one walk's explicit DFS stack. A part tree is wide and shallow (a tileset model is
    /// a root and a few dozen leaves), so this is a runaway guard rather than a sizing decision.
    /// <para>ponytail: fixed array, no growth path, and an overflow DROPS the remaining children of the node
    /// that hit it -- those subtrees then miss their walk generation and the sweep reclaims them, so the cost
    /// is ownership and not merely a count. <c>stack-overflow=</c> on the period line must stay 0; if it ever
    /// does not, this is a sizing decision after all.</para></summary>
    private const int StackMax = 4096;
    private static readonly IntPtr[] _stack = new IntPtr[StackMax];
    /// <summary>The lights-and-emitters descent into a hidden subtree (<see cref="HiddenSpecials"/>), apart
    /// from <see cref="_stack"/> because it runs while that one is mid-walk.</summary>
    private static readonly IntPtr[] _lightStack = new IntPtr[StackMax];
    /// <summary>The walk is in a gob's OWN tree (<c>Gob+0x90</c>), where a hidden subtree still hands its lights
    /// and emitters to the registries: <c>Scene::ListSpecialPartsR</c> @0x462d50 files both classes on that
    /// tree and never reads <c>part+0x34</c>, and neither the BSP visitor @0x481000 that fills
    /// <c>emitterbucket</c> nor <c>Scene::DoEmitterBucket</c> @0x476f00 reads it either -- so a placeable's
    /// lamp and its torch flame under a hidden node both burn in vanilla.</summary>
    private static bool _mainTree;

    private static int* _sequence;
    private static int* _countPart;
    private static BspTraverseFn _bspTraverse;
    /// <summary><c>_ZTV8PartSkin</c> + 0x10 -- the Itanium ABI puts the function pointers two words past the
    /// symbol, so this is the value a PartSkin's first word holds.</summary>
    private static IntPtr _skinVtbl;
    private static ExtenderLog? _log;

    /// <summary>Every symbol resolved. False = no enumeration, and nothing above may arm: a half-walked
    /// table is worse than none, because the sweep would then revoke the parts it failed to reach.</summary>
    internal static bool Ready { get; private set; }

    /// <summary>The current walk generation. An entry whose <see cref="PartTable._pWalkGen"/> is not this
    /// was not reachable from the scene on the last walk, which is what reclaims a gob that LEFT the scene
    /// without its parts being destroyed (<c>Scene::ChildRemove</c> @0x462cc0 unlinks, it does not free).</summary>
    internal static int Gen { get; private set; }

    /// <summary>The record set changed since the last enrollment -- a part was added, bound or swept.
    /// Enrollment is edge-triggered on this and on nothing else; there is no cadence any more.</summary>
    internal static bool Dirty;

    private static IntPtr _scene;
    private static int _lastSeq;
    private static bool _seqLatched;
    /// <summary>Re-walk on the next tick regardless of the epoch. Set by every edge that empties the table
    /// WITHOUT the engine's scene membership changing -- the pressure wipe does exactly that. Without it
    /// the table would stay empty until some gob happened to join or leave
    /// a scene: an empty world with a perfectly healthy epoch.</summary>
    private static bool _walkPending;

    /// <summary>The table was emptied or invalidated by something other than the scene. Re-enumerate.
    /// <para>The BLUNT edge: it names nothing, so it costs the whole area. Anything that knows WHICH gob
    /// moved calls <see cref="NoteGobDirty"/> instead.</para></summary>
    internal static void RequestWalk() { _walkPending = true; Dirty = true; }

    // ---- per-gob membership deltas (the full re-walk was the frame's #1 stutter) -------------------
    //
    // `Gob::SetScene` @0x43e020 is the SOLE writer of Scene::Sequence -- objdump finds exactly two
    // references to that global in the whole image, the `addl $0x1` in this function and ManageSceneBSP's
    // read. So every epoch move arrives here WITH the gob and both scenes, and the walk that used to answer
    // it (728 gobs, every part tree, a full BSP traverse, a 262144-entry sweep, 9,7ms, 66 times per 900
    // frames, blamed for 47 of 63 hitch frames) is answered by ONE gob's tree instead.
    //
    // The accounting is what makes that safe rather than hopeful: the hook counts the bumps it ANSWERED, the
    // tick compares that count against the epoch's actual movement, and any shortfall -- a bump from a path
    // the hook did not arm for, a queue overflow, a re-entrant call -- falls back to the full walk. The
    // periodic audit walk (alloy.walkaudit) is the second net: it runs the full walk anyway and counts what
    // it finds that the deltas had not, which must be 0.

    /// <summary>Gobs whose tree must be re-walked at the next tick. Small on purpose: the population is
    /// "gobs whose scene membership moved since the last frame", which is single digits in play and an
    /// overflow only during a teardown -- where the full walk is the right answer anyway.</summary>
    private const int GobQueueCap = 256;
    private static readonly IntPtr[] _gobQueue = new IntPtr[GobQueueCap];
    private static int _gobQueueN;
    /// <summary>The last gob offered to <see cref="NoteGobDirty"/>. <c>Part::~Part</c> arrives once per part
    /// of a subtree, so the consecutive-run memo is what keeps the linear de-duplication O(1) there.</summary>
    private static IntPtr _lastNoted;
    private static GobSetSceneFn _origSetScene;
    private static GobSetSubtreeEnabledFn _origSubtree;
    /// <summary>Epoch bumps answered per gob since the last tick read the epoch. The tick's own comparison
    /// against <c>Scene::Sequence</c>'s movement is what turns "we think we saw them all" into a fact.</summary>
    private static int _handledBumps;
    /// <summary>A walk or a delta drain is in progress, so <see cref="_stack"/> is in use and the hook must
    /// not re-enter it. The bump is then left unanswered, which is a full walk at the next tick.</summary>
    private static bool _inWalk;
    /// <summary>The walk is the FULL one, so the per-walk gauges (<see cref="_lastWalked"/> and friends) are
    /// its to publish; a delta must not inflate them.</summary>
    private static bool _gauge;
    private static long _deltaGobs, _deltaLeaves, _deltaOverflow, _epochDelta, _epochFull, _epochOther, _deltaReentry, _deltaBurst;
    /// <summary>Per-gob releases one tick may pay before the full walk is the cheaper answer. See the burst
    /// note in <see cref="OnGobSetScene"/>.</summary>
    private const int LeaveBurst = 64;
    private static int _leaveBurst;
    private static long _auditWalks, _auditMisses, _auditAdded, _auditSweptGob, _auditSweptTile;
    private static bool _auditing;
    private static int _sinceAudit;

    /// <summary>The gob whose tree the walk is inside, or Zero for the tile BSP. Held so a subtree pruned on
    /// <c>part+0x34</c> remembers whose delta can bring it back (see <see cref="HoldHidden"/>).</summary>
    private static IntPtr _curGob;

    /// <summary>This gob's part trees changed, or its scene membership did: re-walk that ONE gob at the next
    /// tick. Zero means the edge names no gob (a tile part) and only the full walk reaches it.
    /// <para>Never dereferences <paramref name="gob"/>: the caller may be <c>Part::~Part</c> inside
    /// <c>Gob::~Gob</c>, where the pointer is alive now and freed by the time the drain runs. The drain
    /// validates every queued pointer against <c>Scene+0x130</c> instead, so a gob that died or left is
    /// simply not there.</para></summary>
    internal static void NoteGobDirty(IntPtr gob)
    {
        if (gob == IntPtr.Zero) { RequestWalk(); return; }
        if (gob == _lastNoted) return;
        for (int k = 0; k < _gobQueueN; k++) if (_gobQueue[k] == gob) { _lastNoted = gob; return; }
        if (_gobQueueN >= GobQueueCap) { _deltaOverflow++; RequestWalk(); return; }
        // Deliberately NOT `Dirty = true`. The drain below sets it if it actually changed a record, and the
        // enrolment's delta needs the entries NAMED -- a dirty flag with an empty change list is the one
        // request only the full census can answer (AlloyRenderer.Enroll's `n == 0`).
        _gobQueue[_gobQueueN++] = gob;
        _lastNoted = gob;
    }

    /// <summary><c>Gob::SetScene</c>: the engine's whole scene-membership epoch, per gob. The old scene is
    /// read BEFORE the original runs (it is <c>gob+0x88</c>, which the original overwrites) and the bump
    /// condition is reproduced exactly -- <c>Gob::SetScene</c> returns without touching
    /// <c>Scene::Sequence</c> when the scene does not actually change.
    /// <para>Cold (single digits per second in play) but allocation-free anyway, because nothing says a
    /// module cannot spawn a hundred gobs on one frame.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnGobSetScene(IntPtr gob, IntPtr scene)
    {
        IntPtr old = gob == IntPtr.Zero ? scene : new Gob(gob).Scene;
        _origSetScene(gob, scene);
        if (!Ready || gob == IntPtr.Zero || old == scene) return;   // no epoch bump: nothing to answer
        // Against the scene the walk ENUMERATES, not against WorldScene: the walk's gob loop is over
        // `_scene`'s own Scene+0x130 whether or not it has a tile BSP, so that is the set whose membership
        // this has to answer for.
        IntPtr walked = _scene;
        // A GUI 3D panel's gob joining or leaving its own scene moves the SAME global epoch, and that churn
        // is most of it. Neither scene is the enumerated one, so the answer is "nothing", which is still an
        // answer -- and the accounting in Tick is what makes saying so safe.
        if (walked == IntPtr.Zero || (old != walked && scene != walked)) { _handledBumps++; _epochOther++; return; }
        // GS2: a gob LEAVING is the only death notice the per-gob POINTER tables have, and they get it ahead of
        // every gate below -- the re-entry bail and the burst bound both leave the tree release to a later
        // walk, but a row keyed on an address the allocator is about to recycle cannot wait for one. O(1),
        // dereferences nothing. Deliberately not on an LOD release (ReleaseTree's `gone: false`): a rotated
        // subtree is the same gob, and dropping its transform row would recompose it for nothing. Nothing is
        // delivered when `_scene` is still Zero (no walk yet) or `old == scene` (a gob destroyed while already
        // out of a scene): both tables carry a load factor that recycles them for exactly that reason, and
        // PoseService's own pointer-keyed slots self-age on a bounded probe window (see GobXform.Forget).
        if (scene != walked) { GobXform.Forget(gob); GobLod.Forget(gob); }
        if (_inWalk) { _deltaReentry++; return; }                   // the stack is in use -- let the tick walk
        if (scene == walked) { NoteGobDirty(gob); if (_walkPending) return; _handledBumps++; return; }
        // LEFT the enumerated scene. Done HERE rather than queued: Scene::ChildRemove UNLINKS without freeing, so
        // the parts are alive and this is the last moment the gob certainly is -- Gob::~Gob calls SetScene(0)
        // before it destroys anything (@0x444140), and Scene::~Scene walks every gob out the same way.
        // ...which is also why there is a burst bound. Scene::~Scene walks EVERY gob out one by one, so an
        // area teardown would pay a per-gob release for all ~730 of them on the frame that is about to wipe
        // the tables wholesale. Past the bound the bump is left unanswered, which is the full walk -- and on
        // a teardown that walk is the Scene change's, which was going to happen anyway.
        if (++_leaveBurst > LeaveBurst) { _deltaBurst++; return; }
        _inWalk = true;
        ReleaseGobTrees(gob);
        _inWalk = false;
        Dequeue(gob);
        _deltaLeaves++; _handledBumps++;
    }

    /// <summary>Drop the delta queue. For a self-check that drove an edge over synthetic gobs: the drain
    /// validates every pointer against <c>Scene+0x130</c> and would discard them anyway, but a table that
    /// reads clean after a headless run is worth three lines.</summary>
    internal static void ForgetGobQueue() { _gobQueueN = 0; _lastNoted = IntPtr.Zero; _walkPending = false; }

    /// <summary><c>Part::~Part</c> for a part whose entry may be held hidden. The dtor path kills the entry
    /// without releasing its slot, so the held list has to be told separately or it keeps a dead index for
    /// good. Dereferences nothing -- it is handed the entry the death notice already found.</summary>
    internal static void NotePartDied(int e) { if ((uint)e < MaxParts) UnHold(e); }

    /// <summary>The part table was wiped (a pressure wipe, a module unload, an area edge): every state byte is
    /// zero, so every held index names an entry that holds nothing. The list goes with it.</summary>
    internal static void ForgetHidden()
    {
        for (int k = 0; k < _hiddenN; k++) _hiddenAt[_hiddenHeld[k]] = -1;
        _hiddenN = 0;
    }

    /// <summary><c>Gob::SetSubtreeEnabled</c>: the engine has just moved <c>part+0x34</c> on one part of this
    /// gob's active tree. The HIDE direction had no notice at all -- it used to be found by the next full walk,
    /// which the scene epoch happened to force every 11-20 frames, and a per-gob delta world has no such
    /// accident. So the edge is told: re-walk that gob, where <see cref="Tree"/> prunes on the byte and
    /// <see cref="HoldHidden"/> takes the pruned root into the held set the re-show reads.
    /// <para>Cold (an equip, a helmet toggle, a creature part hidden by a script) and allocation-free.</para></summary>
    [UnmanagedCallersOnly]
    private static byte OnGobSetSubtreeEnabled(IntPtr gob, IntPtr name, int on)
    {
        byte found = _origSubtree(gob, name, on);
        // A name the tree does not hold wrote no byte, so there is nothing to re-walk.
        if (found != 0 && Ready) { _subtreeEdges++; NoteGobDirty(gob); }
        return found;
    }
    private static long _subtreeEdges;

    /// <summary>A tile joined or left the scene. It names NO gob -- <c>SpawnPartsForTile</c> @0x46352b passes
    /// <c>gob = NULL</c> -- and moves no epoch, so the whole-area walk is the only unit there is; it costs one
    /// bool here and the walk itself runs once, at the next frame anchor, however many tiles crossed.
    /// <para>Not gated on "outside an area load". The gate looked free and was not: <c>Scene::SpawnTile</c> and
    /// <c>DeleteTile</c> are also reached mid-play from <c>CNWCArea::ReloadBorder</c> @0x811ff0 (a server
    /// message, <c>CNWCMessage::HandleServerToPlayerArea</c> @0x8a79b0), which is exactly when
    /// <see cref="AreaBorder"/> has its own load flag raised -- so the gate suppressed the notice on the one
    /// path that needed it. <c>Scene::DeleteTile</c> destroys nothing, so nothing else reports it.</para>
    /// <para>Hooked HERE rather than off <see cref="AreaBorder"/>'s handlers, which is where the notice first
    /// went: that class returns early at <c>alloy.border 5</c> and on any partial hook failure, so the notice
    /// would have been silently absent exactly when a knob was turned down (CX-45). Chained above its
    /// layer.</para></summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnSpawnTile(IntPtr scene, byte* model, System.Numerics.Vector3* pos, int orient, int border, IntPtr quat)
    {
        IntPtr gob = (*_spawnSlot)(scene, model, pos, orient, border, quat);
        _tileEdges++; RequestWalk();
        return gob;
    }

    /// <inheritdoc cref="OnSpawnTile"/>
    [UnmanagedCallersOnly]
    private static void OnDeleteTile(IntPtr scene, System.Numerics.Vector3* pos)
    {
        (*_deleteSlot)(scene, pos);
        _tileEdges++; RequestWalk();
    }

    private static SceneSpawnTileFn* _spawnSlot;
    private static SceneDeleteTileFn* _deleteSlot;
    private static long _tileEdges;
    /// <summary>Above <see cref="AreaBorder"/>'s layer (50), so its border replays call the engine rather than
    /// re-entering this notice once per replayed tile.</summary>
    private const int TilePriority = 60;

    private static void Dequeue(IntPtr gob)
    {
        for (int k = 0; k < _gobQueueN; k++)
            if (_gobQueue[k] == gob) { _gobQueue[k] = _gobQueue[--_gobQueueN]; break; }
        if (_lastNoted == gob) _lastNoted = IntPtr.Zero;
    }

    /// <summary>Every record under this gob's trees leaves Alloy's tables. What <see cref="Sweep"/> would do
    /// for it two seconds later, done on the edge -- and the whole reason the full walk no longer has to run
    /// for a despawn.</summary>
    private static void ReleaseGobTrees(IntPtr gob)
    {
        var gv = new Gob(gob);
        ReleaseTree(gv.PartTree.Ptr, true);
        ReleaseTree(gv.LodRoot1.Ptr, true);
        ReleaseTree(gv.LodRoot2.Ptr, true);
        IntPtr* roots = (IntPtr*)gv.AppendRoots;
        int n = gv.AppendCount;
        if (roots == null) return;
        for (int r = 0; r < n; r++) ReleaseTree(roots[r], true);
    }

    /// <summary>Release every record under <paramref name="rootPart"/>. <paramref name="gone"/> says the parts
    /// have LEFT the scene rather than merely dropped out of the walk on an LOD rotation, and the non-mesh
    /// registries read it differently -- see <see cref="NonMeshParts.Left"/>, where the per-class reason is.
    /// The rule behind it: a delta must leave every registry where the next FULL walk would leave it.</summary>
    private static void ReleaseTree(IntPtr rootPart, bool gone)
    {
        if (rootPart == IntPtr.Zero) return;
        int sp = 0;
        _stack[sp++] = rootPart;
        while (sp > 0)
        {
            IntPtr part = _stack[--sp];
            var pv = new Part(part);
            IntPtr tri = pv.AsPartTriMesh().Ptr;
            // Keyed the way the walk keyed it: a mesh by its PartTriMesh (the same address), and anything else
            // by the WALKED pointer, which is what HoldHidden files a pruned subtree root under -- including a
            // dummy. Missing that is a StHidden record whose gob is gone and whose re-show asks forever.
            int e = Find(tri != IntPtr.Zero ? tri : part);
            if (e >= 0 && _pEnt[e].State != StDead) Release(e, "left the scene");
            if (tri == IntPtr.Zero) NonMeshParts.Left(part, gone);
            // Hidden subtrees are descended into, unlike the walk's: everything under this gob is leaving.
            IntPtr* kids = (IntPtr*)pv.Children;
            int n = pv.ChildCount;
            if (kids == null) continue;
            for (int i = 0; i < n; i++)
            {
                if (kids[i] == IntPtr.Zero) continue;
                if (sp >= StackMax) { _overflow++; break; }
                _stack[sp++] = kids[i];
            }
        }
    }

    /// <summary>The queued gobs' trees, re-walked on the current generation so <see cref="Sweep"/> keeps
    /// them. Driven from <c>Scene+0x130</c> and not from the queue: a queued POINTER may have been freed
    /// since (a gob destroyed between two ticks), and the scene's own list is the one container that can
    /// answer "is this still a gob" without dereferencing anything.</summary>
    private static void DrainGobs(IntPtr scene)
    {
        var sc = new Scene(scene);
        IntPtr* gobs = (IntPtr*)sc.Gobs;
        int nGobs = sc.GobCount;
        // Snapshot the queue length: a part destroyed under this walk notes its gob, and an entry that arrives
        // mid-drain must survive to the next tick rather than be cleared unprocessed.
        int queuedN = _gobQueueN;
        _inWalk = true; _gauge = false;
        int done = 0;
        if (gobs != null)
            for (int i = 0; i < nGobs && done < queuedN; i++)
            {
                IntPtr gob = gobs[i];
                if (gob == IntPtr.Zero) continue;
                bool queued = false;
                for (int k = 0; k < queuedN; k++) if (_gobQueue[k] == gob) { queued = true; break; }
                if (!queued) continue;
                done++;
                GobTrees(gob);
                _deltaGobs++;
            }
        int keep = 0;
        for (int k = queuedN; k < _gobQueueN; k++) _gobQueue[keep++] = _gobQueue[k];
        _gobQueueN = keep; _lastNoted = IntPtr.Zero;
        _inWalk = false;
        // The class registries file into dense lists that only a rebuild publishes, so a spawned VFX
        // emitter would otherwise sit in the table unread until the next FULL walk.
        NonMeshParts.DeltaDone();
    }

    /// <summary>One gob's part trees -- the body of the full walk's gob loop, so a delta and a walk cannot
    /// enumerate a gob differently. The ACTIVE root and the appended roots in the steady state; all three LOD
    /// roots while <see cref="GobLod"/> is unarmed or has a swap pending for this gob, and a release of the two
    /// alternates on a delta once it is neither.</summary>
    private static void GobTrees(IntPtr gob)
    {
        AlloyProbe.Once(ref _probedGob, "Gob", gob, _log);
        var gv = new Gob(gob);
        _curGob = gob;
        _curLodAlt = !gv.LodRoot1.IsNull || !gv.LodRoot2.IsNull;
        if (_curLodAlt && _gauge) _lastLodAltGobs++;
        _mainTree = true;
        Tree(gv.PartTree.Ptr);
        _mainTree = false;
        // CX-105: ONE root per gob while Alloy owns the LOD choice. The three-root enumeration was here
        // because Gob::SetLODModel @0x43e6a0 rotated them with no notice of any kind; GobLod is that notice
        // now, and a swap asks for the re-walk this runs. Unarmed (alloy.ownlod 0, or a symbol that did not
        // resolve) the old enumeration is exactly correct and comes back.
        // ...and the ALTERNATES are enumerated for a gob with a swap registered, until it commits (review):
        // GobLod defers the rotation until every root's parts are in this table and owned, so that they are
        // enrolled BEFORE gob+0x90 moves and RouteLive's active-root gate never has a frame with nothing to
        // draw. Two frames of three roots per swap, one root the rest of the time.
        if (!GobLod.Armed || GobLod.Pending(gob))
        {
            Tree(gv.LodRoot1.Ptr);
            Tree(gv.LodRoot2.Ptr);
            if (_gauge) _lastLodPending++;
        }
        // ...and a DELTA releases the roots it did not enumerate. The full walk leaves that to its sweep, which
        // is what used to reclaim the records of a rotation's now-inactive root (Gob::SetLODModel swaps
        // Gob+0x90 against +0x98/+0xa0 without destroying anything, so those parts are alive and simply not
        // ours). Without this the entries would stand -- undrawn, because RouteLive gates on the active root --
        // until the next full walk, and the audit walk would then report them as misses it healed.
        else if (!_gauge) { ReleaseTree(gv.LodRoot1.Ptr, false); ReleaseTree(gv.LodRoot2.Ptr, false); }
        _curLodAlt = false;
        IntPtr* roots = (IntPtr*)gv.AppendRoots;
        int nRoots = gv.AppendCount;
        if (roots != null)
            for (int r = 0; r < nRoots; r++) Tree(roots[r]);
        _curGob = IntPtr.Zero;
    }

    /// <summary>The entries whose ownership answer may have changed since the last enrolment -- bound,
    /// rebound, released. What lets enrolment be INCREMENTAL: 20k kept slots re-seated and re-read per
    /// epoch move (the engine bumps it every 11-20 frames under VFX and creature churn) was the Restage
    /// and Enroll term of the hitch rows. Bounded; an overflow hands the enrolment back its full walk.</summary>
    private const int ChangeCap = 8192;
    private static readonly int[] _changed = new int[ChangeCap];
    private static int _changedN;
    private static bool _changedOverflow;
    private static readonly int[] _changedGen = new int[MaxParts];
    private static int _changeGen = 1;
    internal static void NoteChanged(int e)
    {
        if ((uint)e >= MaxParts || _changedGen[e] == _changeGen) return;
        _changedGen[e] = _changeGen;
        if (_changedN < ChangeCap) _changed[_changedN++] = e; else _changedOverflow = true;
    }
    /// <summary>Drain the list into <paramref name="dst"/>; returns the count. <paramref name="overflow"/> says
    /// the list lost entries and only a full walk is honest.</summary>
    internal static int TakeChanges(int[] dst, out bool overflow)
    {
        overflow = _changedOverflow || _changedN > dst.Length;
        int n = Math.Min(_changedN, dst.Length);
        Array.Copy(_changed, dst, n);
        _changedN = 0; _changedOverflow = false; _changeGen++;
        return n;
    }

    /// <summary>#227 / the repack edge: entry <paramref name="e"/> was just marked
    /// <see cref="PartTable.StStale"/> and needs its draw arguments re-read. Queued DIRECTLY, by index: a
    /// repack changes no scene membership, so the re-walk this used to ask for was a full area enumeration
    /// run to rediscover entries the caller already had in hand -- and the enrolment behind it a full census,
    /// because the request named no entries (<c>n == 0</c> in <c>TryEnrollDelta</c>).</summary>
    internal static void NoteStale(int e) { Enqueue(e); Dirty = true; }

    // instrumentation -- the ticket owes a bump rate, and the escalation decision rides on it
    // Session totals (never reset) and per-period counters (zeroed by Period), kept apart on purpose:
    // printing a session total beside a per-LogEvery rate is how "walk=0,04ms" comes to mean a mean that
    // has not moved since an area load an hour ago.
    private static long _walks, _bumps, _spill, _overflow, _nonMesh;
    /// <summary>#397: subtrees the walk pruned on the engine's <c>part+0x34</c> draw gate (a hidden
    /// appended item, a hidden body part). CX-92: each pruned root is HELD as a <see cref="StHidden"/> entry
    /// and re-read every tick, because being shown again is an edge nothing announces.</summary>
    private static long _hidden;
    /// <summary>Emitters registered from inside a pruned subtree, as vanilla files them (see <see cref="_mainTree"/>).
    /// </summary>
    private static long _hiddenEmitters;
    private static long _framesSinceLog, _bumpsSinceLog, _walkTicks, _walksSinceLog, _added, _swept, _bound;
    private static int _lastWalked, _lastRoots, _lastLodAltGobs, _lastLodPending;
    /// <summary>The tree the walk is inside, for <c>_pRoot</c>, and whether its gob carries LOD alternates.</summary>
    private static IntPtr _curRoot;
    private static bool _curLodAlt;

    internal static bool Resolve(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _sequence = (int*)Native.Sym(SymbolMap.SceneSequence);
        _countPart = (int*)Native.Sym(SymbolMap.CountPart);
        _bspTraverse = EngineFn.BspTraverse;
        IntPtr sv = Native.Sym(SymbolMap.PartSkinVtbl);
        _skinVtbl = EngineAbi.Vtbl(sv);
        Ready = _sequence != null && _bspTraverse != null && _skinVtbl != IntPtr.Zero;
        if (!Ready)
        {
            log.Info("SceneWalk: " + (_sequence == null ? SymbolMap.SceneSequence
                                    : _bspTraverse == null ? SymbolMap.BspTraverse : SymbolMap.PartSkinVtbl)
                     + " missing -- scene enumeration off, the renderer cannot arm");
            return false;
        }
        // The per-gob membership edge. Not a stand-down if it does not install: the epoch accounting can
        // never be satisfied without it, so every bump falls back to the full walk -- which is exactly the
        // behaviour this replaced. Said out loud rather than inferred from a slow frame (CX-45).
        _origSetScene = hooks.InstallGobSetScene(&OnGobSetScene);
        // The hide/show edge. Also not a stand-down: without it a subtree hidden between two full walks keeps
        // drawing until the audit cadence comes round, which is a wrong pixel rather than a crash -- but it is
        // a wrong pixel nobody would attribute, so it is said out loud.
        _origSubtree = hooks.InstallGobSetSubtreeEnabled(&OnGobSetSubtreeEnabled);
        // The tile edge: a tile names no gob and moves no epoch, so this is the only notice there can be.
        _spawnSlot = hooks.ChainSceneSpawnTile(&OnSpawnTile, TilePriority);
        _deleteSlot = hooks.ChainSceneDeleteTile(&OnDeleteTile, TilePriority);
        if (_spawnSlot == null || _deleteSlot == null)
            log.Always($"SceneWalk: {(_spawnSlot == null ? SymbolMap.SceneSpawnTile : SymbolMap.SceneDeleteTile)} "
                     + "did not chain -- a tile added or removed mid-area is noticed only by the next full walk "
                     + "this session (alloy.walkaudit frames at worst), so the ring can hold stale tiles and "
                     + "miss new ones for that long");
        if (_origSubtree == null)
            log.Always($"SceneWalk: {SymbolMap.GobSetSubtreeEnabled} did not hook -- a part the engine hides or "
                     + "shows through it is only noticed by the next full walk this session (alloy.walkaudit "
                     + "frames at worst), so an unequipped item can keep drawing for up to that long");
        if (_origSetScene == null)
            log.Always($"SceneWalk: {SymbolMap.GobSetScene} did not hook -- every scene-epoch move costs a FULL "
                     + "re-walk of the area this session (9-15ms each), because the per-gob delta has no edge "
                     + "to ride. The renderer is correct, not fast; `[enum] epoch full=` counts it");
        return true;
    }

    /// <summary>Once per world frame, at the frame anchor and before enrollment. One int load in the common
    /// case; a full re-walk when the epoch moved or the Scene changed. Engine thread, allocation-free.</summary>
    /// <summary>#282: the world Scene this walk last enumerated, or Zero. Published for the passes that want
    /// the AREA's gobs rather than the frame's culled <c>gobbucket</c> -- the same "enumerate the world, not
    /// the frame" move the part table itself made, and what keeps a pass from silently inheriting whatever
    /// somebody else last pushed into an engine bucket.</summary>
    internal static IntPtr WorldScene { get; private set; }

    internal static void Tick(IntPtr scene)
    {
        // CX-104's skip experiment: with the engine's walks skipped, nothing calls Gob::ProcessGob or
        // Gob::SetLODModel. Alloy drives them HERE, ahead of the walk, because ProcessGob refreshes the
        // geometry flags this walk reads and SetLODModel is the edge it consumes below (review: driving them
        // from the frame anchor put both a frame behind the walk).
        GobLod.DriveSkipped(scene);
        // CX-105: an LOD swap is a membership edge. Consumed HERE and not at the frame anchor because
        // Gob::SetLODModel fires inside ManageSceneBSP, ahead of this walk (and, on the skip path, in the line
        // above) -- so the re-walk lands on the same frame as the swap rather than the frame after.
        GobLod.Tick();
        if (!Ready || scene == IntPtr.Zero) return;
        AlloyProbe.Once(ref _probedScene, "Scene", scene, _log);
        WorldScene = AlloyRenderer.IsWorldScene(scene) ? scene : IntPtr.Zero;
        _framesSinceLog++;
        int seq = *_sequence;
        int moved = _seqLatched ? seq - _lastSeq : 0;
        int handled = _handledBumps; _handledBumps = 0; _leaveBurst = 0;
        bool bumped = _seqLatched && seq != _lastSeq;
        if (bumped) { _bumps++; _bumpsSinceLog++; }
        // THE ACCOUNTING. Gob::SetScene is the only writer of the epoch, so a move of N means N calls, and N
        // answers means the per-gob deltas queued below cover all of it. Anything less -- a bump the hook saw
        // while the walk stack was in use, a queue overflow, an arm that did not happen -- and the full walk
        // stands, which is what makes this an optimisation rather than a bet.
        if (bumped && moved > 0 && moved == handled && Renderer.WalkDelta.V && _origSetScene != null)
        { bumped = false; _epochDelta++; }
        else if (bumped) _epochFull++;
        bool sceneChanged = scene != _scene;
        _lastSeq = seq; _seqLatched = true;
        if (sceneChanged) { _scene = scene; Reset(); }
        // CX-92: the one membership edge with no engine notice at all -- a subtree the last walk pruned on
        // part+0x34 is showing again. Asked only when nothing already forces the walk, so it costs one byte
        // read per held part on the frames that would otherwise do nothing. A re-show names the gob whose
        // tree held it, so it is a delta too; only a tile's asks for the whole area.
        if (!bumped && !sceneChanged && !_walkPending) HiddenShown();
        // A NEW Scene pays for all of it at once, deliberately. An area load is already a hitch -- the
        // player is on a loading screen -- and dripping the setup through the frames after it is what the
        // first live run showed as tiles arriving in front of you. The budget is for the steady state: a
        // spawned placeable, a door swapping its model, a gob whose LOD subtree rotated in. Since the retry
        // condition is the totals moving and not an attempt count, a record that fails on the load frame
        // loses nothing by being asked there.
        _setupLeft = sceneChanged ? int.MaxValue : SetupBudget;
        bool full = bumped || sceneChanged || _walkPending;
        // The deltas' periodic net: run the full walk anyway every alloy.walkaudit frames and count what it
        // finds. It is also the only thing that still reaches the edges NOTHING announces -- a tile added or
        // removed by Scene::DeleteTile / SpawnPartsForTile moves no epoch and names no gob.
        if (full) _sinceAudit = 0;
        else if (Renderer.WalkAudit.V > 0 && ++_sinceAudit >= Renderer.WalkAudit.V)
        { _sinceAudit = 0; _auditing = true; full = true; }
        if (full)
        { _walkPending = false; Walk(scene); }
        else
        {
            if (_gobQueueN > 0) DrainGobs(scene);
            Rebind();
        }
        // A walk binds what it can as it goes; anything it left behind is drained by the queue right after,
        // while the budget still has room.
        if (_setupLeft > 0 && _unboundCount > 0) Rebind();
        // The bring-up validator, on the period cadence. The engine has already run ManageSceneBSP and
        // AddPartsToDrawBuckets by the time this anchor is reached, so Scene+0x208 holds THIS frame's
        // finished classification -- every part in it must have a record.
        // Its own counter, NOT _framesSinceLog: that one is reset by the period line, which is ticked from
        // a different (per-present, not per-world-frame) clock, so once it crossed the threshold the check
        // would run every frame until the two happened to line up.
        // CX-104: behind alloy.bucketcheck, off by default. Nothing in the frame reads an engine bucket any
        // more, so the walk no longer pays an O(bucket) audit of them unless a live session asks -- and the
        // audit is only meaningful while the engine still fills them (see alloy.skipbuckets).
        if (Renderer.BucketCheck.V && ++_sinceCheck >= AlloyRenderer.LogEvery)
        { _sinceCheck = 0; _lastMiss = CrossCheck(new Scene(scene).DrawBuckets, out _lastSample, out _lastSampleAll); }
    }

    private static int _lastMiss, _sinceCheck;

    /// <summary>#456: the first-sighting layout probes this file owns, one latch each.</summary>
    private static bool _probedScene, _probedGob, _probedPart, _probedVp;

    /// <summary>#264: <c>SetupArrays</c> calls left this frame. It is the geometry upload for a part -- tangent
    /// basis, VDO item allocation, the pool write -- so a fresh area with nine thousand unbound records would
    /// pay for all of them in one frame and hitch. Bounded per frame instead: an area binds over a second or
    /// so, which is the same convergence the capture path had (it bound a part the frame the camera first
    /// faced it) without the camera dependency.
    /// <para>ponytail: flat budget, no priority. Nothing here knows which parts are near the camera, so the
    /// queue order is walk order. If the fill-in is ever visible as geometry arriving in the wrong order,
    /// the upgrade is to sort the unbound queue by distance -- not to raise the budget.</para></summary>
    private const int SetupBudget = 512;
    private static int _setupLeft;
    private static long _setups;
    /// <summary>Unbound records whose <see cref="GeomSig"/> says their primitive carries no geometry at all.
    /// A POPULATION, counted with the other period gauges: how much of the enumerated world is not geometry
    /// YET, and the number that says whether the walk is enumerating things it should not be reaching. None
    /// of them costs a <c>SetupArrays</c> call while it reads the same, and each is re-asked the moment it
    /// does not -- so this number falling without an area load is residency arriving.</summary>
    private static int _waitingGeom;
    /// <summary>Records re-asked this period because their geometry totals moved. The rate that pairs with
    /// <see cref="_waitingGeom"/>: the old eight-attempt window could only ever count down.</summary>
    private static long _reasks;
    /// <summary>#264/CX-90: the HOLE in the engine's draw bucket 5 at the last cross-check -- parts the engine
    /// classified into it that Alloy does not own, so nothing draws them (the walk's deletion left no engine
    /// draw behind). Since CX-90 owns <c>sample_framebuffer 1</c> the residue is <c>volumetric</c>, which is
    /// still refused; <see cref="_lastSampleAll"/> beside it is the class's whole size, so the pair reads as
    /// "hole / class" rather than as one ambiguous number.</summary>
    private static int _lastSample;
    /// <summary>...and the class's size, ownership aside. <inheritdoc cref="_lastSample"/></summary>
    private static int _lastSampleAll;

    /// <summary>A new Scene invalidates every record wholesale: the old Scene's parts are being torn down
    /// (<c>Scene::~Scene</c> @0x472e40 walks its gobs out one by one) and their addresses will be recycled.
    /// Cheaper and safer than letting the sweep discover it one entry at a time.
    /// <para>CX-19: through <c>AlloyRenderer.AreaEdge</c>, not <c>WipeTables</c> alone. This is Path A of an
    /// area change and the AreaLoaded send is Path B; one edge, one revoke list, or the tables one of the two
    /// shapes forgets are exactly the ones nobody notices.</para></summary>
    private static void Reset() => AlloyRenderer.AreaEdge("scene changed -- re-enumerating");

    private static void Walk(IntPtr scene)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Gen++;
        _walks++; _walksSinceLog++;
        _lastWalked = 0; _lastRoots = 0; _lastLodAltGobs = 0; _lastLodPending = 0;

        // CX-92: the held set is NOT reset here any more. It is maintained on the state transitions
        // (HoldHidden in, Release out), which a per-gob delta drives as well as a walk -- and a held part this
        // walk no longer reaches keeps a stale _pWalkGen, so the sweep below releases it and UnHold drops it.
        long added0 = _added, swept0 = _swept;
        _inWalk = true; _gauge = true;
        var sc = new Scene(scene);
        IntPtr* gobs = (IntPtr*)sc.Gobs;
        int nGobs = sc.GobCount;
        if (gobs != null)
            for (int i = 0; i < nGobs; i++)
            {
                IntPtr gob = gobs[i];
                // All THREE LOD roots where the LOD owner is not armed, which is the whole point (see
                // GobLodRoot1): Gob::SetLODModel rotates them with no notice of any kind.
                if (gob != IntPtr.Zero) GobTrees(gob);
            }

        // Tile geometry: SpawnPartsForTile @0x46352b passes gob = NULL, so it hangs off no gob and the loop
        // above cannot reach it. The unculled BSPTraverse visits every node whether the camera can see it or
        // not, which is exactly the difference between enumerating the area and enumerating the frame.
        IntPtr root = sc.BspRoot;
        // ONE traversal of the tile tree, for everything that wants it. The part table, the tile-fade ramp,
        // the tile emitter/beam/trigger registry and the tile lights each used to walk the whole BSP
        // themselves; three of those four traversals are gone, and this is the one they ride.
        _tileSide = WorldScene != IntPtr.Zero && WorldScene == _scene;
        if (_tileSide) { SceneParts.TileScanBegin(Gen); LightRegistry.BspScanBegin(); }
        _walkingBsp = true;
        if (root != IntPtr.Zero)
            // OUR callback's address, not an engine symbol: the cast is the C# way to spell &OnBspNode.
            _bspTraverse(root, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnBspNode, IntPtr.Zero);
        _walkingBsp = false;
        if (_tileSide) { SceneParts.TileScanEnd(); LightRegistry.BspScanEnd(); }
        _tileSide = false;

        Sweep();
        NonMeshParts.WalkDone(Gen);
        // A full walk answers every queued delta by construction, so the queue is spent rather than stale.
        _gobQueueN = 0; _lastNoted = IntPtr.Zero;
        _inWalk = false; _gauge = false;
        // The deltas' validator: this walk was run for no reason but to check them, so anything it added or
        // swept is something they missed. It must read 0 on the period line.
        if (_auditing)
        { _auditing = false; _auditWalks++; _auditAdded += _added - added0; _auditMisses += _added - added0 + (_swept - swept0); }
        _walkTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>#330: the BSP traversal is in progress, so every record made now is BSP-filed.</summary>
    private static bool _walkingBsp;
    /// <summary>This traversal is over the WORLD scene, so the tile registries want its nodes too. A GUI 3D
    /// panel's scene has tiles in neither sense.</summary>
    private static bool _tileSide;

    [UnmanagedCallersOnly]
    private static void OnBspNode(IntPtr node, IntPtr ctx)
    {
        var bsp = new BSPNode(node);
        IntPtr* items = bsp.TileParts;
        int n = bsp.TilePartCount;
        if (items != null)
            for (int i = 0; i < n; i++)
            {
                IntPtr tri = items[i];
                if (tri == IntPtr.Zero) continue;
                Tree(tri);
                if (_tileSide) SceneParts.TileMesh(tri);
            }
        if (_tileSide) LightRegistry.NoteBspNode(node, Gen);
    }

    /// <summary>Walk one part subtree, iteratively. Recursion is not an option here: this runs on the render
    /// thread with an unbounded tree depth in front of it.</summary>
    private static void Tree(IntPtr rootPart)
    {
        if (rootPart == IntPtr.Zero) return;
        if (_gauge) _lastRoots++;   // a per-WALK gauge; only Walk zeroes it, so a delta must not add to it
        _curRoot = rootPart;
        int sp = 0;
        _stack[sp++] = rootPart;
        while (sp > 0)
        {
            IntPtr part = _stack[--sp];
            // #397: the engine's own gate. Scene::AddPartsTreeToDrawBuckets @0x460880 and Gob::PartDraw
            // @0x451d50 both return on part+0x34 == 0 BEFORE descending, so a hidden part hides its subtree --
            // an equipment swap leaves the old item appended and hidden, not amputated, and a walk that read
            // only the mesh part drew both shields coincident. Tile parts (SceneBSPAddPart @0x47ca10) never
            // consult the byte, so the BSP walk does not either.
            if (!_walkingBsp && new Part(part).Visible == 0)
            {
                _hidden++; HoldHidden(part);
                if (_mainTree) HiddenSpecials(part);
                continue;
            }
            ShowHidden(part);
            Visit(part);
            IntPtr* kids = (IntPtr*)new Part(part).Children;
            int n = new Part(part).ChildCount;
            if (kids == null) continue;
            for (int i = 0; i < n; i++)
            {
                IntPtr k = kids[i];
                if (k == IntPtr.Zero) continue;
                if (sp >= StackMax) { _overflow++; break; }
                _stack[sp++] = k;
            }
        }
    }

    /// <summary>Every light and emitter under the hidden <paramref name="root"/>, to their registries and
    /// nothing else: the subtree's meshes stay pruned, exactly as the engine's draw gate prunes them.</summary>
    internal static void HiddenSpecials(IntPtr root)
    {
        int sp = 0;
        _lightStack[sp++] = root;
        while (sp > 0)
        {
            var part = new Part(_lightStack[--sp]);
            if (!part.AsPartLight().IsNull) LightRegistry.Visit(part.Ptr, false, Gen);
            else if (!part.AsPartEmitter().IsNull) { _hiddenEmitters++; SceneParts.Visit(part.Ptr, false, Gen); }
            IntPtr* kids = (IntPtr*)part.Children;
            int n = part.ChildCount;
            if (kids == null) continue;
            for (int i = 0; i < n; i++)
            {
                if (kids[i] == IntPtr.Zero) continue;
                if (sp >= StackMax) { _overflow++; break; }
                _lightStack[sp++] = kids[i];
            }
        }
    }

    private static void Visit(IntPtr walked)
    {
        if (_gauge) _lastWalked++;   // see Tree: a per-walk gauge
        // THE CLASS FILTER, and it is not optional. The walk reaches every Part in the scene -- PartLight,
        // PartEmitter, PartTrigger, PartProjection, PartReference, PartDummy, PartCamera -- while everything
        // downstream of a record assumes PartTriMesh. The capture path never had to ask, because a part only
        // reached it by arriving through PartTriMesh::Draw. Enumeration has no such guarantee, and the cost
        // of skipping the question is a SIGSEGV rather than a wrong pixel: PartTriMesh::GetIsOpaque @0x43cee0
        // reads *(this+0x50) as a Gob* and chases *(gob+0x68)+0x8e, and on a PartLight that offset holds the
        // light's float range (EngineLayout.LightRange).
        // Asked the engine's own way -- vtable slot 0x60 is AsPartTriMesh, which returns null for every other
        // class (SceneBSPAddPart @0x47ca10 discriminates with exactly this call, and BucketGate already
        // resolves every bucket item through it). The RETURNED pointer is the record key, because that is
        // what PartTriMesh::Draw receives as `this` and therefore what every Find() elsewhere looks up.
        IntPtr part = new Part(walked).AsPartTriMesh().Ptr;
        if (part == IntPtr.Zero) { _nonMesh++; NonMeshParts.Visit(walked, _walkingBsp, Gen); return; }
        int e = Find(part);
        if (e < 0)
        {
            e = Enrol(part);
            if (e < 0) { _spill++; return; }
            _added++; Dirty = true;
        }
        _pWalkGen[e] = Gen;
        // #253 §3.2: the engine rebinds materials and swaps LOD subtrees in place, with no notice of any
        // kind. So the walk RE-READS the two columns every gate downstream dereferences, and a drift is a
        // re-bind rather than a death.
        if (_pEnt[e].State == StActive) { var p = new PartRef(part); if (_pNode[e] != p.Node.Ptr || _pMat[e] != p.Material.Ptr) Rebind(e, p); }
        // The record was created before its geometry was resident, or a Repack moved its items. Either way
        // the fix is the same field read, and this is the frame it is retried on.
        if (_pEnt[e].State == StUnbound || _pEnt[e].State == StStale) TryBind(e);
    }

    /// <summary>CX-92: entries HELD hidden by the last walk, and the count of them. Sized like the unbound
    /// queue so it cannot overflow -- an overflow would be a hole with no heal, which is the bug.</summary>
    private static readonly int[] _hiddenHeld = new int[MaxParts];
    /// <summary>Whose tree held each entry in <see cref="_hiddenHeld"/> (Zero for a tile part). A re-show is
    /// then a per-gob delta rather than a full area re-walk.</summary>
    private static readonly IntPtr[] _hiddenGob = new IntPtr[MaxParts];
    private static int _hiddenN;
    /// <summary>Where entry <c>e</c> sits in <see cref="_hiddenHeld"/>, or -1. MEMBERSHIP, not a walk
    /// generation: the list used to be rebuilt by every walk and appended to on
    /// <c>_pWalkGen[e] != Gen</c>, which was exactly right while only a walk ever pruned a subtree and
    /// exactly wrong once a per-gob delta does -- a delta shares the walk's generation, so a part it hid
    /// was never held and the re-show edge (CX-92) had nothing to notice. Maintained on the state
    /// transitions instead: in on <see cref="HoldHidden"/>, out on <see cref="Release"/>.</summary>
    private static readonly int[] _hiddenAt = NewHiddenAt();
    private static int[] NewHiddenAt() { var a = new int[MaxParts]; Array.Fill(a, -1); return a; }
    /// <summary>Hidden roots held by the last walk, and re-shows it noticed. <c>shown</c> is THE instrument
    /// for CX-92: a nonzero count is the engine putting a subtree back with no notice, which before this was
    /// a limb that stayed missing until something unrelated moved the scene epoch.</summary>
    private static long _shown, _shows, _holdRefused;
    private static bool _holdRefusedLogged;

    /// <summary>CX-92: hold the pruned subtree root as a <see cref="StHidden"/> entry so the tick below can
    /// see it come back. Held rather than released because <c>Gob::SetSubtreeEnabled</c> @0x43c670 -- the only
    /// dynamic writer of <c>part+0x34</c>, reached through a name-dispatch table -- bumps
    /// <c>Scene::Sequence</c> for neither direction, creates no part and destroys none, so a released record
    /// would wait for an unrelated walk trigger and the part is drawn by nobody meanwhile (#264).
    /// <para>The gob is cleared: a reclaimed slot carries the previous tenant's, nothing reads a hidden
    /// entry's, and the all-owned-or-none sweep must not take a creature for a part vanilla also skips.</para></summary>
    private static void HoldHidden(IntPtr part)
    {
        // Keyed on the WALKED pointer, not on AsPartTriMesh's: a hidden root is as often a dummy (a bone, an
        // appended item's root) as a mesh, and for a mesh the two are the same address anyway. Nothing reads
        // a held entry's columns, so an entry that names no mesh costs one slot and one byte read.
        int e = Find(part);
        if (e < 0)
        {
            e = Insert(part);
            if (e < 0) { _spill++; return; }
            Track(e);
        }
        // A non-empty state before anything can re-enter Find: Insert leaves a reclaimed slot reading empty,
        // which would terminate the probe chain of every other key running through it.
        if (_pEnt[e].State != StHidden) { _pEnt[e].State = StHidden; Dirty = true; NoteChanged(e); }
        _pGob[e] = IntPtr.Zero;
        AlloyCensus.OwnFlag[e] = 0;
        int h = _hiddenAt[e];
        if (h < 0)
        {
            // Sized like the table, so a refusal is a leak in the list rather than a real population: with
            // every exit maintained it cannot exceed the number of live entries. Loud, because the symptom of a
            // quiet refusal is a limb that stays missing (CX-92) and nothing else would name the cause.
            if (_hiddenN >= MaxParts)
            {
                _holdRefused++;
                if (!_holdRefusedLogged)
                {
                    _holdRefusedLogged = true;
                    _log?.Always($"SceneWalk: the held-hidden list is FULL ({MaxParts}) -- a subtree the engine "
                               + "pruned is NOT being watched for coming back, so nothing will draw it until the "
                               + "next full walk. Counted as hold-refused on the [enum] line");
                }
                _pWalkGen[e] = Gen;
                return;
            }
            h = _hiddenAt[e] = _hiddenN++; _hiddenHeld[h] = e;
        }
        // The gob is re-stamped even for an entry already held: a re-show has to ask for the tree that holds
        // the part NOW, and an appended item's root can change hands between two walks.
        if (h >= 0) _hiddenGob[h] = _curGob;
        _pWalkGen[e] = Gen;
    }

    /// <summary>CX-92: this part was held hidden and is being walked again. <see cref="Release"/> frees the
    /// slot, so <see cref="Visit"/>'s <c>Enrol</c> reclaims this very entry and rebuilds every column from the
    /// live part -- the fresh start a re-shown part has always got, now on a walk this edge asked for.</summary>
    private static void ShowHidden(IntPtr part)
    {
        int e = Find(part);
        if (e >= 0 && _pEnt[e].State == StHidden) { Release(e, "shown"); _shown++; }
    }

    /// <summary>Has any held subtree been shown again? One byte load per held entry, on the ticks that would
    /// otherwise walk nothing. A freed or re-tenanted entry is skipped: <c>Part::~Part</c> marks the first
    /// (<see cref="PartTable.MarkFreed"/>) and only a freed slot is ever reclaimed, so no pointer read here
    /// outlives its part.
    /// <para>Each re-show asks for ITS gob's delta; a held tile part (no gob) is the one case that still
    /// costs the whole area, and there is nothing smaller to ask for -- the BSP walk is how a tile part is
    /// reached at all.</para></summary>
    private static void HiddenShown()
    {
        for (int k = 0; k < _hiddenN; k++)
        {
            int e = _hiddenHeld[k];
            if (_pEnt[e].State != StHidden || _pEnt[e].Freed || _pEnt[e].Part == IntPtr.Zero) continue;
            if (new Part(_pEnt[e].Part).Visible == 0) continue;
            _shows++;
            NoteGobDirty(_hiddenGob[k]);
        }
    }

    /// <summary>Create a record for a part the walk has never seen. The field-read replacement for
    /// the old capture <c>Record</c>: every column it used to fill by observing a vanilla draw is a load
    /// off the part, the material or the VertexPrimitive.</summary>
    private static int Enrol(IntPtr part)
    {
        var p = new PartRef(part);
        int e = Insert(part);
        if (e < 0) return -1;
        Track(e);
        IntPtr vp = p.Node.Ptr;
        IntPtr mat = p.Material.Ptr;
        _pNode[e] = vp; _pMat[e] = mat; _pVdo[e] = IntPtr.Zero;
        // The gob is read off the PART, never inferred from which root the walk arrived by: a reference
        // expansion under a gob's tree carries its own +0x50 (CreateInstanceTreeR @0x43c6b0 is its sole
        // writer), and SpawnPartsForTile @0x46352b leaves it null outright.
        _pGob[e] = p.Gob.Ptr;
        _pRoot[e] = _curRoot; _pLodAlt[e] = _curLodAlt && _pGob[e] != IntPtr.Zero;
        _pBsp[e] = _walkingBsp;
        _pWalkGen[e] = Gen;
        if (_pGob[e] != IntPtr.Zero) AlloyCensus._pInstRecorded++;
        // #137's identity, by vtable rather than by "which hook body is on the stack". A skinmesh is posed
        // by the engine's 64-matrix bone array, so the Alloy pass has to force a pose and bind bones.
        _pSkin[e] = _skinVtbl != IntPtr.Zero && p.Vtbl == _skinVtbl;
        // #134: BOTH halves of RenderDrawBucket's SetModelMatrix condition -- the node flags, and whether
        // the part is an instance. Calling one matrix-class and not the other draws every gob at the origin.
        _pMx[e] = vp != IntPtr.Zero && (new NodeRef(vp).MatrixClass || p.IsInstance);
        // Under capture these two were final: Record only ever ran mid-PartTriMesh::Draw, where both had to
        // be set for the draw to happen at all. Enumeration reaches a part EARLIER, so a null here can be
        // load order -- Release rather than Die, and the next walk re-enrols it.
        if (vp == IntPtr.Zero || mat == IntPtr.Zero)
        { Release(e, vp == IntPtr.Zero ? "no-node" : "no-material"); return e; }
        _pEnt[e].State = StUnbound;
        if (!TryBind(e) && _pEnt[e].State == StUnbound) Enqueue(e);
        return e;
    }

    /// <summary>The vertex and index totals <c>VertexPrimitiveFlat::SetupArrays</c> @0x458920 sums over this
    /// primitive's <c>MdlNodeTriMesh</c> list, packed into one comparable value: zero in either half is
    /// exactly its no-geometry exit, so "the signature moved" is "the answer can have changed" and nothing
    /// else is. Summed the engine's way -- this node first, then the <see cref="EngineLayout.VpCombineList"/>
    /// siblings -- with its <c>CalculateTangentSpaceBasis</c> and <c>SetupProjectionArrays</c> side trips
    /// left out: they write tangent and projection arrays, never a count.
    /// <para>Allocation-free and read-only; three loads per node, and the empty tiles this exists to stop
    /// re-asking have one node and no groups.</para></summary>
    internal static long GeomSig(IntPtr vp)
    {
        var vpv = new VertexPrimitive(vp);
        IntPtr node = vpv.Node.Ptr;
        int count = vpv.CombineCount;
        IntPtr list = vpv.CombineList;
        uint verts = 0, idx = 0;
        // A negative count skips the engine's loop entirely -- including this node -- and leaves both totals
        // zero, so it is a no-geometry verdict rather than a one-node walk.
        for (int i = 0; count >= 0 && node != IntPtr.Zero; i++)
        {
            unchecked
            {
                var tri = new MdlNodeTriMesh(node);
                verts += tri.VertexCount;
                int groups = tri.IndexGroups;
                IntPtr counts = tri.IndexGroupCounts;
                if (counts != IntPtr.Zero)
                    for (int g = 0; g < groups; g++) idx += *(uint*)(counts + g * sizeof(int));
                idx += tri.IndexCount;
            }
            if (i >= count || list == IntPtr.Zero) break;
            IntPtr sib = *(IntPtr*)(list + i * sizeof(IntPtr));
            node = sib == IntPtr.Zero ? IntPtr.Zero : new Part(sib).Node.Ptr;
            // MdlNode::AsMdlNodeTriMesh, inlined: the engine dereferences its result unguarded, so a node
            // that fails the cast never reaches here in a client that is not already crashing.
            if (node != IntPtr.Zero
                && (new MdlNode(node).Kind & EngineLayout.MdlNodeKindTriMesh) != EngineLayout.MdlNodeKindTriMesh)
                node = IntPtr.Zero;
        }
        return ((long)(verts & 0x7fffffff) << 32) | idx;
    }

    /// <summary>Does <paramref name="sig"/> say the primitive has no geometry at all? The population of
    /// records sitting on one is what the period line reports as waiting.</summary>
    internal static bool NoGeometry(long sig) => (sig >> 32) == 0 || (uint)sig == 0;

    /// <summary>Has this record been told, by the engine's own totals, that its primitive carries no geometry
    /// at all? <c>VertexPrimitiveFlat::SetupArrays</c> @0x458920 answers zero totals by setting the item to -1
    /// and leaving the VDO null, and <c>RenderFlat</c> @0x450eb0 opens with
    /// <c>if (vp+0x58 == 0) return;</c> -- so vanilla draws nothing for it either and its siblings are
    /// untouched. False while the signature is still <see cref="PartTable.GeomSigNever"/>: never asked is not
    /// the same answer as asked and empty.</summary>
    internal static bool RecordHasNoGeometry(int e)
        => _pGeomSig[e] != PartTable.GeomSigNever && NoGeometry(_pGeomSig[e]);

    /// <summary>Read the draw arguments off the VertexPrimitive and intern the group. Null VDO or item -1 is
    /// not a failure -- <c>VertexPrimitiveFlat::SetupArrays</c> @0x458920 has not run for this part yet, so
    /// the record simply stays unbound and is retried by <see cref="Rebind"/>. See
    /// <see cref="EngineLayout.VpVdo"/> for why this is a field read at all.</summary>
    private static bool TryBind(int e)
    {
        IntPtr part = _pEnt[e].Part, vp = _pNode[e];
        if (vp == IntPtr.Zero) return false;
        // #456: the two classes whose vtable slots this function devirts through, probed at the first record
        // that carries both -- TryBind reads the VertexPrimitive fields and calls SetupArrays off its vtable.
        AlloyProbe.Once(ref _probedPart, "Part", part, _log);
        AlloyProbe.Once(ref _probedVp, "VertexPrimitive", vp, _log);
        bool wasBound = _pEnt[e].State == StStale;   // only a formerly bound entry's failure is a change the delta must see
        var vpv = new VertexPrimitive(vp);
        IntPtr vdo = vpv.Vdo.Ptr;
        int item = vpv.Item;
        // #264: RESIDENCY IS ALLOY'S TO DRIVE NOW. These two fields are filled by
        // VertexPrimitive::SetupArrays (vtable 0x38), and until #264 the only thing that ever called it was
        // PartTriMesh::Draw -- i.e. the engine bucket walk. With the walk deleted nothing ran it, every
        // record stayed StUnbound, enrollment saw an empty candidate set and the world went black. So the
        // walk makes the call itself, at the same frame anchor the engine's draw used to run from.
        if (vdo == IntPtr.Zero || item < 0)
        {
            // THE ENGINE'S OWN CONDITION, not an attempt budget. SetupArrays' verdict is a pure function of
            // the totals GeomSig reads, so totals unchanged since the last ask cannot answer differently and
            // the call is skipped -- which is what stops the first live run's 512 calls a frame on 1899 tiles
            // that were never going to bind. Totals that moved mean the model became resident, and that
            // re-ask is never spent: there is no ceiling to run out of.
            long sig = GeomSig(vp);
            if (sig == _pGeomSig[e] || _setupLeft <= 0) { _pEnt[e].State = StUnbound; if (wasBound) NoteChanged(e); return false; }
            if (_pGeomSig[e] != PartTable.GeomSigNever) _reasks++;
            _pGeomSig[e] = sig;
            _setupLeft--; _setups++;
            vpv.SetupArrays();
            vdo = vpv.Vdo.Ptr;
            item = vpv.Item;
            if (vdo == IntPtr.Zero || item < 0) { _pEnt[e].State = StUnbound; if (wasBound) NoteChanged(e); return false; }
        }
        // Only mode 1 (indexed static) can be drawn from our own tables; the capture killed the rest as
        // "multidraw-or-unindexed" and that verdict is unchanged, it is just reached without a draw.
        if (new VdoRef(vdo).Mode != 1) { Release(e, "unindexed"); return false; }
        MdlNode mdl = new Part(part).Node;
        if (mdl.IsNull) { Release(e, "no-mdlnode"); return false; }
        _pVdo[e] = vdo; _pItem[e] = item; _pPrim[e] = mdl.PrimType;
        int bucket = Classify(part, _pMat[e]);
        // Not tested against AlphaBucket: Classify never produces 7 or 8 -- they are per-frame fade states,
        // not properties of the part, and BucketGate publishes the live _bucketId for whoever needs it.
        _pOwnOnly[e] = !Engine.IsOpaque(new PartRef(part))
                       || bucket == AlloyRenderer.WaterBucket
                       || _pSkin[e] || _pGob[e] != IntPtr.Zero;
        // #258: what interning a group used to do, minus the group. The metadata the registry carried
        // incidentally is a column now; the diffuse slot and the rotation bits stay because the census and
        // AlloyRenderer.IdentityOk still compare them against the live material to spot an animated texture.
        var mr = new MaterialRef(_pMat[e]);
        _pTex0[e] = mr.Texture(0).Ptr;
        _pRot[e] = new PartRef(part).TexRotateBits;
        _pBucket[e] = bucket;
        _pScene[e] = _scene;
        _pWorld[e] = AlloyRenderer.IsWorldScene(_scene);
        _pLight[e] = mr.BlendFlag == 0;
        _pEnt[e].State = StActive;
        _bound++; Dirty = true; NoteChanged(e);
        return true;
    }

    /// <summary>The record's VertexPrimitive or Material moved under it. Refresh the two columns and put
    /// the entry back in the bind queue: the next <see cref="Rebind()"/> re-reads the draw arguments and
    /// re-interns the group, so an in-place material swap costs a frame rather than the record.</summary>
    internal static void Rebind(int e, PartRef live)
    {
        _pNode[e] = live.Node.Ptr; _pMat[e] = live.Material.Ptr;
        _pGob[e] = live.Gob.Ptr;
        _pVdo[e] = IntPtr.Zero;
        _pMx[e] = _pNode[e] != IntPtr.Zero && (new NodeRef(_pNode[e]).MatrixClass || live.IsInstance);
        AlloyCensus.OwnFlag[e] = 0;
        // #264: the remembered geometry totals belong to the OLD VertexPrimitive, so they cannot stand in
        // for the new one's -- forgetting them is what lets a re-modelled part be asked again at all.
        // #284: and a new MdlNode is a new bind-array question, so the pose attempt clears with it.
        _pGeomSig[e] = PartTable.GeomSigNever; _pPoseTried[e] = false;
        _pEnt[e].State = _pNode[e] == IntPtr.Zero || _pMat[e] == IntPtr.Zero ? Die(e, "no-node") : StUnbound;
        if (_pEnt[e].State == StUnbound) Enqueue(e);
        Dirty = true; NoteChanged(e);
    }

    /// <summary>CX-91: re-enrol an entry whose identity moved, in the same call that noticed. <see cref="Rebind(int, PartRef)"/>
    /// alone only queues the entry, so the census refused it for the frame and the part drew nowhere (since
    /// #264 a refusal is not a hand-back). This re-reads the draw arguments immediately and answers whether
    /// the record is usable again, so the caller can carry on with the refreshed columns.
    /// <para>True only for <see cref="PartTable.StActive"/>: a primitive whose totals are still empty stays
    /// queued and the caller must refuse. Cold -- the census calls it once per drifted candidate per
    /// enrollment, and <see cref="TryBind"/>'s own <c>_setupLeft</c> budget bounds the SetupArrays calls.</para></summary>
    internal static bool RebindNow(int e, PartRef live)
    {
        Rebind(e, live);
        return _pEnt[e].State == StUnbound && TryBind(e) && _pEnt[e].State == StActive;
    }

    /// <summary>Per frame, for the records the last walk could not bind. O(unbound), which is zero in a
    /// settled area -- the alternative is waiting for an epoch bump that a purely resident-state change
    /// never produces.
    /// <para>ponytail: a full table scan rather than a free list of unbound slots. The <c>unbound=</c>
    /// number on the period line is what says whether the list is worth keeping.</para></summary>
    private static void Rebind()
    {
        int left = 0;
        _rebindTick++;
        for (int i = 0; i < _unboundCount; i++)
        {
            int e = _unbound[i];
            byte st = _pEnt[e].State;
            // Leaving the queue clears the mark, so the entry can be queued again by a later edge.
            if (st != StUnbound && st != StStale) { _queued[e] = false; continue; }   // bound, released or swept since
            if (_pWalkGen[e] != Gen) { _queued[e] = false; continue; }                // not in the scene any more
            // Already answered "no geometry": only a model becoming resident moves that, so it is re-read on a
            // staggered 1-in-NoGeomEvery cadence rather than every frame (700 empty tiles were 0,16 ms a frame).
            if (st == StUnbound && RecordHasNoGeometry(e) && ((e + _rebindTick) & (NoGeomEvery - 1)) != 0)
            { _unbound[left++] = e; continue; }
            // A record that produced nothing STAYS queued -- what it is waiting for is its model becoming
            // resident, which nothing in this frame announces. It costs one GeomSig read per tick and no
            // SetupArrays call until the totals move, so the drain drains without the verdict being final.
            if (!TryBind(e) && _pEnt[e].State == StUnbound) _unbound[left++] = e;
            else _queued[e] = false;
        }
        _unboundCount = left;
    }

    /// <summary>The unbound queue: entry indices awaiting a VDO, dense and compacted in place by
    /// <see cref="Rebind()"/>, rebuilt from scratch by <see cref="Sweep"/> (which walks the whole table
    /// anyway, so that pass is also what de-duplicates it).
    /// <para>MEASURED, not anticipated. The first live run held ~1330 permanently unbound records out of
    /// ~9000 -- <c>VertexPrimitiveFlat::SetupArrays</c> @0x458920 runs from <c>PartTriMesh::Draw</c>, so a
    /// part the engine has never drawn has no VDO to read and enumeration alone cannot give it one. The
    /// count therefore never reaches zero, and the full-table scan this replaced ran all 262144 slots every
    /// frame for the life of the session to service those 1330. The queue makes it O(unbound), which is what
    /// #256 asked for; making the 1330 bind at all is strand 1 step 5's (residency: geometry).</para>
    /// <para>A record that answered "no geometry" is NOT dropped from the queue: that answer is the engine's
    /// verdict on the totals it read at the time, and re-reading them is what notices the model arriving.
    /// The queue is therefore a standing population rather than a draining one, and what bounds its cost is
    /// <see cref="PartTable._pGeomSig"/> (no call while the totals hold) and not its length.</para></summary>
    private static readonly int[] _unbound = new int[MaxParts];
    private static int _unboundCount, _rebindTick;
    /// <summary>How often a record already answered "no geometry" is re-read for its model arriving. Power of two.</summary>
    private const int NoGeomEvery = 16;

    /// <summary>Queue an entry for binding. Only on a transition INTO the unbound states, so an entry
    /// already waiting is not enqueued twice; <see cref="Sweep"/>'s rebuild is the backstop.</summary>
    private static void Enqueue(int e)
    {
        if (_queued[e] || _unboundCount >= MaxParts) return;
        _queued[e] = true; _unbound[_unboundCount++] = e;
    }
    /// <summary>Is this entry already in <see cref="_unbound"/>? The queue used to be de-duplicated only by
    /// <see cref="Sweep"/> rebuilding it from scratch, which was fine while every edge forced a walk; now that
    /// the repack queues by index and walks are rare, a re-queued entry that never binds would otherwise take
    /// a queue slot per repack for the life of the area.</summary>
    private static readonly bool[] _queued = new bool[MaxParts];

    /// <summary>Kill a record AND release its slot. The distinction from a bare <c>Die</c> is the whole
    /// difference between "not ours this walk" and "not ours ever": a plain <c>Die</c> leaves
    /// <c>_pEnt[e].Part</c> set, so <see cref="PartTable.Find"/> keeps matching the pointer and no later walk
    /// can re-enrol it -- and the edges this walk exists to handle are round trips.
    /// <c>Gob::Amputate</c> @0x443fa0 then <c>Gob::Append</c> @0x4422a0 is an equipment swap;
    /// <c>Scene::ChildRemove</c> @0x462cc0 then <c>ChildAdd</c> @0x461d50 is a gob leaving and returning.
    /// The part comes back at the SAME address, and a tombstone that outlives it means vanilla for the life
    /// of the area.
    /// <para>Reuses #221's freed mark, which already means exactly "this slot's address no longer means
    /// anything, re-record instead of landing on it": <see cref="PartTable.Find"/> misses it and
    /// <see cref="PartTable.Insert"/> reclaims it ungated by the load factor.</para>
    /// <para>A part killed for a PERMANENT reason (no material, a non-indexed VDO) is therefore re-enrolled
    /// and re-killed once per walk. Walks are edge-triggered and rare, and <c>rec-dead</c> reports it --
    /// which is the trade against the alternative, where one bad frame costs the part forever.</para></summary>
    private static void Release(int e, string why)
    {
        AlloyCensus.OwnFlag[e] = 0;
        UnHold(e);
        Die(e, why);
        _pEnt[e].Freed = true;
        Dirty = true; NoteChanged(e);
    }

    /// <summary>Take an entry out of the held-hidden list, O(1) by swapping the last member into its place.
    /// <para>There are THREE exits from <see cref="PartTable.StHidden"/> and they do not share a funnel:
    /// <see cref="Release"/> (a re-show through <see cref="ShowHidden"/>, or the sweep), <c>Part::~Part</c>
    /// (<see cref="NotePartDied"/> -- it <c>Die</c>s the entry without releasing the slot), and
    /// <c>PartTable.WipeTables</c> (<see cref="ForgetHidden"/> -- it <c>Array.Clear</c>s every entry row and
    /// knows nothing about this list). Each calls this. Missing the last two was a list that only ever grew --
    /// 14,500 deaths a session plus every area change -- until <see cref="_hiddenN"/> reached
    /// <see cref="PartTable.MaxParts"/> and <see cref="HoldHidden"/> quietly stopped holding anything, which is
    /// CX-92 back with no counter.</para></summary>
    private static void UnHold(int e)
    {
        int h = _hiddenAt[e];
        if (h < 0) return;
        _hiddenAt[e] = -1;
        int last = --_hiddenN;
        if (h == last) return;
        int moved = _hiddenHeld[last];
        _hiddenHeld[h] = moved; _hiddenGob[h] = _hiddenGob[last]; _hiddenAt[moved] = h;
    }

    /// <summary>Every entry this file has ever tenanted, densely. <see cref="Sweep"/> used to read all
    /// <see cref="PartTable.MaxParts"/> state bytes to find the ~53k records among them, which is the one place
    /// #256's "O(what changed)" was bought with a constant; this makes it O(tenanted) instead.
    /// <para>The safety argument is that it can only ever be a SUPERSET of the live entries: every entry in the
    /// part table is created by <see cref="Enrol"/> or <see cref="HoldHidden"/>, both of which
    /// <see cref="Track"/> it, and a stale index costs one state read. A MISSING index would be a record
    /// nothing ever reclaims, which is why the audit walk re-reads the whole table and counts the difference
    /// (<c>sweep-miss</c>, must be 0).</para></summary>
    private static readonly int[] _live = new int[MaxParts];
    private static int _liveN;
    /// <summary>Where <paramref name="e"/> sits in <see cref="_live"/>, or -1.</summary>
    private static readonly int[] _liveAt = NewLiveAt();
    private static int[] NewLiveAt() { var a = new int[MaxParts]; Array.Fill(a, -1); return a; }
    private static long _sweepMiss;

    private static void Track(int e)
    {
        if (_liveAt[e] >= 0 || _liveN >= MaxParts) return;
        _liveAt[e] = _liveN; _live[_liveN++] = e;
    }

    /// <summary>Mark-and-sweep: an entry the walk did not reach is no longer in the scene. The edge
    /// <c>Part::~Part</c> cannot cover -- <c>Scene::ChildRemove</c> @0x462cc0 and <c>Gob::Amputate</c>
    /// @0x443fa0 UNLINK without destroying, so the parts are alive and simply not ours any more. Every such
    /// edge now has a notice of its own (the <c>Gob::SetScene</c> hook, <c>Part::~Part</c>, the LOD release in
    /// <see cref="GobTrees"/>), so on an audit walk this should find NOTHING -- which is what
    /// <c>audit misses</c> reports.
    /// <para>O(tenanted) over <see cref="_live"/>, not O(<see cref="PartTable.MaxParts"/>): the 262144-entry
    /// scan was the one place #256's "O(what changed)" was bought with a constant rather than earned.</para></summary>
    private static void Sweep()
    {
        for (int i = 0; i < _unboundCount; i++) _queued[_unbound[i]] = false;
        _unboundCount = 0;
        // A wipe (PartTable.WipeTables) needs no reset of its own: it leaves every slot reading StEmpty, and
        // the loop below drops an empty slot out of the list, so one sweep after a wipe costs the old full
        // scan's worth of work once and the list is dense again.
        // The audit walk pays the full scan as well and counts what the dense list did not name -- the one
        // failure this structure can have, and the only one that would be invisible otherwise.
        if (_auditing)
        {
            for (int e = 0; e < MaxParts; e++)
                if (_pEnt[e].State != StEmpty && _liveAt[e] < 0) { _sweepMiss++; Track(e); }
        }
        int left = 0;
        for (int i = 0; i < _liveN; i++)
        {
            int e = _live[i];
            byte st = _pEnt[e].State;
            // An empty slot is not a tenant any more: dropping it is what keeps the list the size of the
            // table's population rather than of everything that ever passed through it.
            if (st == StEmpty) { _liveAt[e] = -1; continue; }
            _liveAt[e] = left; _live[left++] = e;
            if (_pWalkGen[e] == Gen)
            {
                if (st == StUnbound || st == StStale) Enqueue(e);
                continue;
            }
            if (st != StDead)
            {
                // On an audit walk the sweep is a MISS: the deltas should have released this already. Split by
                // whether the record hangs off a gob, because the two answer to different edges -- a gob record
                // means a membership or LOD edge the deltas did not hear, a tile record means the tile went.
                if (_auditing) { if (_pGob[e] != IntPtr.Zero) _auditSweptGob++; else _auditSweptTile++; }
                Release(e, "unreachable"); _swept++;
            }
        }
        _liveN = left;
    }

    /// <summary>Which draw bucket the engine would file this part in, read out of
    /// <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 rather than observed. That function's whole
    /// classification is a function of the material, and every term is a field read or an exported query:
    /// bumpy-shiny + <c>g_nEnableBumpyWater</c> -&gt; 6; the SharedMaterial's +500 selector -&gt; 6 or 5;
    /// <c>mat+0x94 == 2</c> (dst blend ONE, i.e. additive) -&gt; 4; otherwise opaque 2 / transparent 3.
    /// (#258: the engine's sub-bucket, gated by <c>shared+0x1f1</c> and valued <c>shared+0x1f8 ^ 1</c>, was
    /// only ever part of the group key and is no longer derived.)
    /// <para>ponytail: buckets 7 (neighbour caps) and 8 (a tile mid-fade) are deliberately NOT produced.
    /// Both are per-FRAME conditions the engine reroutes into from <c>HandleFading{In,Out}Bucket</c>
    /// @0x47f670/@0x47f870, not properties of the part. The Alloy pass asks the fade question live, per
    /// frame, through <see cref="TileFadeRamp"/>, which is where it is always current.</para>
    /// </summary>
    internal static int Classify(IntPtr part, IntPtr mat)
    {
        var p = new PartRef(part);
        if (mat == IntPtr.Zero) return Engine.IsOpaque(p) ? 2 : 3;
        var m = new MaterialRef(mat);
        IntPtr shared = m.Shared.Ptr;
        int bucket;
        if (Engine.HasBumpyShiny(m) && _bumpyWater != null && *_bumpyWater != 0) bucket = 6;
        else if (shared == IntPtr.Zero)
            bucket = m.DstBlend == AlloyCensus.BlendOne ? 4 : Engine.IsOpaque(p) ? 2 : 3;
        else
        {
            int sel = m.Shared.SampleFb;
            bucket = sel == 2 ? 6
                   : m.DstBlend == AlloyCensus.BlendOne ? 4
                   : sel == 1 ? 5
                   : m.Shared.Volumetric ? 5
                   : Engine.IsOpaque(p) ? 2 : 3;
        }
        return bucket;
    }


    /// <summary>The one measurement #256 owes: how often the GLOBAL epoch moves. <c>Scene::Sequence</c> is
    /// bumped by <c>Gob::SetScene</c> for ANY scene, a GUI 3D panel included, so a churning panel would
    /// re-walk the world for nothing. If this reads at or near the frame rate the escalation is
    /// <c>Scene::ChildAdd</c> @0x461d50 / <c>ChildRemove</c> @0x462cc0 for exact deltas -- NOT a hash of the
    /// object list, which would have to walk the list to compute and is therefore strictly more expensive
    /// than the epoch it would replace.</summary>
    internal static string Period()
    {
        double ms = _walksSinceLog == 0 ? 0
                  : _walkTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _walksSinceLog;
        double perFrame = _framesSinceLog == 0 ? 0 : (double)_bumpsSinceLog / _framesSinceLog;
        int cp = _countPart != null ? *_countPart : -1;
        // Live gauges, counted here because the period line is the only cold place that wants them. The
        // per-period rates below (added/swept/bound) say what MOVED; these say where the table stands, and
        // reading a rate as a state is how "bound=270" came to look like "270 records are usable".
        int active = 0, unboundGob = 0, unboundTile = 0;
        _waitingGeom = 0;
        for (int e = 0; e < MaxParts; e++)
        {
            byte st = _pEnt[e].State;
            if (st == StActive) { active++; continue; }
            if (st != StUnbound && st != StStale) continue;
            if (_pGob[e] != IntPtr.Zero) unboundGob++; else unboundTile++;
            if (NoGeometry(_pGeomSig[e])) _waitingGeom++;
        }
        string s = $"[enum] walks={_walks} ({_walksSinceLog} this period) epoch moved on {_bumps} frames ({perFrame:0.000}/frame) walk={ms:0.00}ms "
                 + $"roots={_lastRoots} ({_lastLodAltGobs} gobs with LOD alternates, {_lastLodPending} enumerated whole for a pending swap) parts walked={_lastWalked} records={_partCount} active={active} "
                 // The unbound residue, split by whether the record hangs off a Gob. This is step 5's
                 // starting number and the two halves want different answers: a GOB record that never binds
                 // is most likely an inactive LOD alternate (the walk enumerates all three roots, the engine
                 // draws one), which is correct to hold and will bind if the LOD swaps; a TILE record that
                 // never binds is world geometry the camera has not reached, which is the residency gap.
                 + $"unbound={_unboundCount} (gob={unboundGob} tile={unboundTile}) "
                 + $"+{_added} added/-{_swept} swept/{_bound} bound/{_setups} set-up this period "
                 // Waiting is a population and re-asked is a rate: waiting-on-geometry records cost a totals
                 // read and no call, and each moves to re-asked the tick its model becomes resident.
                 + $"waiting-on-geometry={_waitingGeom} re-asked={_reasks} this period "
                 // CX-92: held is the per-tick cost of noticing a re-show, shown is the edge itself. A
                 // nonzero `shown` is the engine putting a subtree back with no notice of any kind -- the
                 // mechanism that left one limb of a creature drawn by nobody until something else moved
                 // the scene epoch. `re-shows` counts the ticks that asked for the walk.
                 + $"non-mesh={_nonMesh} hidden={_hidden} (held={_hiddenN} shown={_shown} re-shows={_shows} emitters-kept={_hiddenEmitters}"
                 + (_holdRefused > 0 ? $" hold-refused={_holdRefused}" : "") + ") "
                 // The delta ledger. `epoch delta` is a membership move answered with ONE gob's tree and
                 // `epoch full` one that was not (an unarmed hook, a queue overflow, a bump the hook saw
                 // mid-walk); `other` is a move in a scene Alloy does not draw, which needed no answer at all.
                 // `audit misses` is the validator and MUST be 0: it is what a full walk found that the
                 // deltas had not (alloy.walkaudit frames apart, 0 = off).
                 + $"epoch delta={_epochDelta} full={_epochFull} other={_epochOther} subtree={_subtreeEdges} "
                 // ENGINE-initiated only: this layer chains above AreaBorder's, so its border replays call the
                 // engine directly and are counted by AreaBorder.Describe's `replayed` instead.
                 + $"tiles={_tileEdges} (engine-initiated); "
                 + $"deltas={_deltaGobs} gob(s) ({_deltaLeaves} left the scene, {_deltaOverflow} queue overflow(s), "
                 + $"{_deltaReentry} mid-walk, {_deltaBurst} over the burst bound) "
                 + $"audit walks={_auditWalks} misses={_auditMisses} (added={_auditAdded} swept gob={_auditSweptGob} tile={_auditSweptTile}) "
                 // The sweep's own validator: entries the dense tenant list did not name, which the audit's
                 // full table scan found. Must be 0 -- a record the sweep cannot reach is one nothing revokes.
                 + $"sweep-miss={_sweepMiss} swept-over={_liveN} "
                 + $"table-full={_spill} stack-overflow={_overflow} "
                 // The two validators. countpart is an UPPER bound, never an equality: it counts every Part
                 // in every scene, GUI 3D panels included. bucket-miss is the sharp one -- it must be 0.
                 // CX-90: hole / class size. The left number must be 0 -- it is the part of bucket 5 nothing
                 // draws -- and the right one is what the late keywords put there at all.
                 + $"countpart={cp} "
                 + (Renderer.BucketCheck.V ? $"bucket-miss={_lastMiss} sample-class={_lastSample}/{_lastSampleAll}"
                                           : "bucket-check off (alloy.bucketcheck 1)");
        _bumpsSinceLog = 0; _framesSinceLog = 0;
        _walksSinceLog = 0; _walkTicks = 0; _added = 0; _swept = 0; _bound = 0; _setups = 0; _reasks = 0;
        return s;
    }

    /// <summary>Bring-up validator (ADR 0018 demotes the bucket arrays to exactly this): every part the
    /// engine classified into <c>Scene+0x208</c> this frame must have a record. A miss means the walk did
    /// not reach something the engine is drawing, which is the failure this ticket has to be able to see.
    /// Returns the number of misses.
    /// <para>CX-90 makes it carry the late class too: <paramref name="sampleAll"/> is bucket 5's size and
    /// <paramref name="sample"/> the part of it Alloy does not own, which is the HOLE. Ownership is read off
    /// the record's own flag rather than inferred from the material, so a part refused by any later gate
    /// counts in it as well.</para></summary>
    internal static int CrossCheck(DrawBucket buckets, out int sample, out int sampleAll)
    {
        int miss = 0; sample = 0; sampleAll = 0;
        for (int id = 0; id < BucketTypes; id++)
            for (int phase = 0; phase < BucketGate.BucketPhases; phase++)
            {
                DrawBucket bucket = buckets[id * BucketGate.BucketPhases + phase];
                IntPtr* items = bucket.Items;
                int count = bucket.Count;
                if (items == null) continue;
                for (int i = 0; i < count; i++)
                {
                    IntPtr part = items[i];
                    if (part == IntPtr.Zero) continue;
                    IntPtr tri = new Part(part).AsPartTriMesh().Ptr;
                    int e = tri == IntPtr.Zero ? -1 : Find(tri);
                    if (tri != IntPtr.Zero && e < 0) miss++;
                    if (id != SampleBucket) continue;
                    sampleAll++;
                    if (e < 0 || AlloyCensus.OwnFlag[e] == 0) sample++;
                }
            }
        return miss;
    }

    /// <summary>The late item type -- <c>sample_framebuffer 1</c> plus <c>volumetric</c>
    /// (<c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880). CX-90 owns the first; the second is still refused
    /// on the material, so what is counted here as unowned is that residue.</summary>
    internal const int SampleBucket = 5;

    /// <summary>16 item types x 3 sub-buckets = the 48 <c>DrawBucket</c> records at <c>Scene+0x208</c>
    /// (<c>GetDrawBucketByItemType</c> @0x47bd60's <c>(type*3 + sub)*0x10</c> stride).</summary>
    private const int BucketTypes = 16;

    /// <summary>Self-check for the mark-and-sweep contract, over synthetic parts in unmanaged scratch: a
    /// reachable record survives a walk, an unreachable one is swept, and a freed slot is reclaimed rather
    /// than matched. No engine state -- these three rules are the ones a live session cannot show you
    /// failing until something has already been drawn wrong.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        // Two synthetic "parts": leaf-only (no children), so nothing here walks engine memory.
        IntPtr blk = Marshal.AllocHGlobal(0x200);
        try
        {
            for (int i = 0; i < 0x200; i++) ((byte*)blk)[i] = 0;
            IntPtr a = blk, b = blk + 0x100;
            int ea = Insert(a), eb = Insert(b);
            if (ea < 0 || eb < 0)
            { log.Info("SceneWalk.SelfCheck: FAILED -- the part table refused a synthetic insert"); return; }
            Track(ea); Track(eb);   // Enrol/HoldHidden do this in production; Insert alone does not
            int savedCount = _partCount;
            _pEnt[ea].State = StActive; _pEnt[eb].State = StActive;
            int gen = ++Gen;
            _pWalkGen[ea] = gen;                       // a is reachable, b is not
            Sweep();
            bool ok = _pEnt[ea].State == StActive;
            // ...and b is not merely dead: its SLOT is released, which is what lets the part come back.
            ok &= _pEnt[eb].State == StDead && _pDeath[eb] == "unreachable" && _pEnt[eb].Freed;
            ok &= Find(b) < 0 && Insert(b) == eb && !_pEnt[eb].Freed;
            // ...and #221's tombstone still governs reuse for a genuinely destroyed part.
            _pEnt[ea].Freed = true;
            ok &= Find(a) < 0;
            ok &= Insert(a) == ea && !_pEnt[ea].Freed;
            // The "the engine draws nothing for it either" answer the instance sweep is exempted on: only a
            // record that has ASKED and been told zero. Never-asked must not read as empty.
            long savedSig = _pGeomSig[ea];
            _pGeomSig[ea] = PartTable.GeomSigNever;
            ok &= !RecordHasNoGeometry(ea);
            _pGeomSig[ea] = 0;
            ok &= RecordHasNoGeometry(ea);
            _pGeomSig[ea] = 12L << 32;                 // vertices but no indices is still nothing to draw
            ok &= RecordHasNoGeometry(ea);
            _pGeomSig[ea] = (12L << 32) | 18;
            ok &= !RecordHasNoGeometry(ea);
            _pGeomSig[ea] = savedSig;

            // CX-92: the part+0x34 round trip. A hidden subtree root is HELD rather than released, the tick
            // notices the byte coming back, and the walk that follows releases the slot so the part re-enrols
            // from scratch. Before this, the show direction had no notice of any kind -- Gob::SetSubtreeEnabled
            // @0x43c670 is the only dynamic writer of the byte and it bumps nothing -- so the part stayed
            // unowned, and since #264 unowned is drawn by nobody. That is a limb missing until something
            // unrelated moved the scene epoch, which is what these four lines go red on.
            int savedGen = Gen;
            int savedInstDead = AlloyCensus._ownInstDead, savedInstHidden = AlloyCensus._ownInstHidden;
            // NOT forced to 0: the list is production state now (it survives walks and is maintained on every
            // exit), so a check that zeroed it would discard real entries. On a headless run it is empty, and
            // saying so is the assertion.
            ok &= _hiddenN == 0;
            Gen++;
            byte* pb = (byte*)b;
            pb[EngineLayout.PartVisible] = 0;
            // not-an-offset: a synthetic gob identity, only ever compared
            IntPtr hiddenHost = (IntPtr)0x1000;
            _curGob = hiddenHost;
            HoldHidden(b);
            _curGob = IntPtr.Zero;
            ok &= _pEnt[eb].State == StHidden && _hiddenN == 1 && _hiddenHeld[0] == eb
                  && _hiddenGob[0] == hiddenHost && _hiddenAt[eb] == 0
                  && _pGob[eb] == IntPtr.Zero && _pWalkGen[eb] == Gen;
            // #2: the list keys on the TRANSITION, not on the walk generation. A per-gob delta shares the
            // walk's generation, so holding on `_pWalkGen[e] != Gen` meant a part a delta hid was never held
            // and CX-92's re-show edge could not see it come back.
            _curGob = hiddenHost; HoldHidden(b); _curGob = IntPtr.Zero;
            ok &= _hiddenN == 1 && _hiddenAt[eb] == 0;   // ...and holding twice does not list it twice
            _gobQueueN = 0; _lastNoted = IntPtr.Zero; _walkPending = false;
            HiddenShown();
            ok &= !_walkPending && _gobQueueN == 0;     // still hidden: no walk and no delta asked for
            pb[EngineLayout.PartVisible] = 1;
            HiddenShown();
            // The edge nothing else in the client announces, and it asks for the ONE gob that held the
            // subtree -- a full area re-walk only when the held part is a tile's (no gob).
            ok &= _gobQueueN == 1 && _gobQueue[0] == hiddenHost && !_walkPending;
            // ...and a held entry does not take its creature with it: the exemption runs AHEAD of the
            // dead-entry branch, which would otherwise read _pGob and refuse the whole gob.
            _pGob[eb] = hiddenHost;
            var hiddenCand = default(AlloyCandidate);
            ok &= !AlloyCensus.TryOwnCandidate(eb, ref hiddenCand)
                  && AlloyCensus._ownInstDead == savedInstDead
                  && AlloyCensus._ownInstHidden == savedInstHidden + 1;
            _pGob[eb] = IntPtr.Zero;
            ShowHidden(b);
            ok &= _pEnt[eb].State == StDead && _pDeath[eb] == "shown" && _pEnt[eb].Freed
                  && Find(b) < 0 && Insert(b) == eb && !_pEnt[eb].Freed;
            _gobQueueN = 0; _lastNoted = IntPtr.Zero;
            HiddenShown();
            // Released: out of the held list altogether, so nothing asks for it again.
            ok &= _gobQueueN == 0 && !_walkPending && _hiddenN == 0 && _hiddenAt[eb] < 0;
            pb[EngineLayout.PartVisible] = 0;
            // The queue de-duplicates (Part::~Part arrives once per part of a subtree) and an overflow asks
            // for the full walk rather than losing the gob.
            NoteGobDirty(hiddenHost); NoteGobDirty(hiddenHost); NoteGobDirty(b);
            ok &= _gobQueueN == 2 && !_walkPending;
            for (int i = 0; i < GobQueueCap + 4; i++) NoteGobDirty(blk + 0x200 + i * 0x40);
            ok &= _gobQueueN == GobQueueCap && _walkPending && _deltaOverflow > 0;
            _gobQueueN = 0; _lastNoted = IntPtr.Zero; _walkPending = false; _deltaOverflow = 0;
            // ...and unwound through the production path rather than by zeroing the count.
            UnHold(ea); UnHold(eb);
            ok &= _hiddenN == 0 && _hiddenAt[ea] < 0 && _hiddenAt[eb] < 0;
            Gen = savedGen; _shown = 0; _shows = 0;
            AlloyCensus._ownInstDead = savedInstDead; AlloyCensus._ownInstHidden = savedInstHidden;
            _pDeath[eb] = null!;

            _pEnt[ea].State = StEmpty; _pEnt[eb].State = StEmpty;
            _pEnt[ea].Part = IntPtr.Zero; _pEnt[eb].Part = IntPtr.Zero;
            _pWalkGen[ea] = 0; _pWalkGen[eb] = 0;
            // ...and out of the tenant list, so the two synthetic slots are not swept over for the session.
            _liveAt[ea] = -1; _liveAt[eb] = -1; _liveN = 0; _sweepMiss = 0;
            _pDeath[eb] = null!;
            _partCount = savedCount;                   // the two synthetic slots are not real tenants
            // The change list: dedupes within a generation, drains in order, flags an overflow.
            var dst = new int[ChangeCap];
            TakeChanges(dst, out _);
            NoteChanged(ea); NoteChanged(ea); NoteChanged(eb);
            ok &= TakeChanges(dst, out bool ovf) == 2 && dst[0] == ea && dst[1] == eb && !ovf;
            NoteChanged(ea);
            ok &= TakeChanges(dst, out ovf) == 1 && dst[0] == ea && !ovf;
            for (int i = 0; i <= ChangeCap; i++) NoteChanged((i + 1) % MaxParts);
            ok &= TakeChanges(dst, out ovf) == ChangeCap && ovf;
            ok &= TakeChanges(dst, out ovf) == 0 && !ovf;
            Dirty = false;
            log.Info(ok ? "SceneWalk.SelfCheck: passed"
                        : "SceneWalk.SelfCheck: FAILED -- the mark-and-sweep contract drifted");
        }
        finally { Marshal.FreeHGlobal(blk); }
    }
}
