using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Alloy.SceneLayout;
using static Sowx.Alloy.PartTable;
using static Sowx.Alloy.BucketGate;
using static Sowx.Alloy.AlloyCensus;
namespace Sowx.Alloy;

/// <summary>ADR 0006 phase 1: extender-owned draw submission for opaque, pre-transformed static geometry --
/// and, since #188, that is ALL this type is. The ownership census went to <see cref="AlloyCensus"/>,
/// the engine bucket-walk suppression to <see cref="BucketGate"/> and the tables they share to
/// <see cref="PartTable"/>; what stayed is the capture funnel (the two <c>Draw</c> hooks that put a part in
/// the table) and the batcher the name describes. The capture stays here rather than in the table module
/// because it IS the hook body -- <c>Record</c>/<c>Complete</c> run a vanilla draw through the trampoline
/// this type owns to make the funnel fire.
/// <para>The engine's 2002 bucket loop submits ~2000 tiny draws/frame (each with full per-draw state
/// churn); this service records each batchable part's draw once -- (VDO, item, prim) captured at the
/// <c>GLRender::RenderVertexData</c> funnel, grouped by (VDO, Material, bucket, cull phase, render hint,
/// lighting toggle) -- and thereafter, when the first clean member of a group draws (engine state fully
/// configured by the engine itself), submits every other member in ONE <c>glMultiDrawElementsBaseVertex</c>
/// and skips their per-part path. Geometry is byte-identical to vanilla: same shared vertex buffers, same
/// index ranges (re-read from the VDO item tables each submit, so <c>Repack()</c> moves are absorbed),
/// same program/textures/uniforms (the first member's own draw applied them).</para>
/// <para>Excluded from BATCHING (always vanilla, unless the Alloy pass takes them): matrix-path/skinned
/// parts, bumpy-shiny materials, non-opaque parts, bucket 8 (per-item alpha), keyhole-dissolving or
/// tile-fading members (per-frame via the engine's own <c>SetCanDissolve</c> value + the part's tile-fade
/// factor). #126 split "not batchable" from "not recorded": the transparent classes are captured and
/// grouped like anything else, because that is what the Alloy pass enrolls from, and carry a
/// <see cref="PartTable._pOwnOnly"/> flag that keeps them out of this service's own submits. Lifetime is
/// <see cref="PartTable"/>'s: entries are keyed by part+node+material pointer identity, a mismatch kills the
/// entry (recycled pointer), and submits never dereference a part -- only our own copies + the live VDO
/// tables -- so freed parts simply stop being seen (members must have drawn within the last 2 frames).</para>
/// <para>Armed by <c>drawhooks=batch</c> in <c>sowx.cfg</c>, the default; <c>drawhooks=stats</c> hands the draw hooks to
/// <c>DrawStatsService</c> and <c>drawhooks=off</c> disables both (#182 -- one mode, because both hook the same
/// symbols; drawstats also wants the vanilla draw stream). Hot and alloc-free throughout.</para>
/// </summary>
public sealed unsafe class BatchService : IExtenderService
{
    /// <summary>#182: the ONE key that says who owns the draw hooks. <c>batch</c> (default) = this service,
    /// <c>stats</c> = <c>DrawStatsService</c>'s histogram, anything else = neither. It is one mode rather than
    /// two booleans because the two arm the same symbols and cannot coexist; and it is config rather than a
    /// command because both decide what installs, which only happens once.</summary>
    internal const string DrawModeKey = "drawhooks", DrawModeBatch = "batch", DrawModeStats = "stats";

    private const uint GlUnsignedShort = 0x1403;

    /// <summary>Members of ONE group a single multidraw can carry. Raised with the group table: a dense
    /// area's biggest group is bounded by the group table, not by a separate number, and overflow here is a
    /// SILENT truncation -- the tail of the group simply is not drawn. Counted now (<c>_submitTrunc</c>) so
    /// it can never be silent again.</summary>
    private const int MaxSubmit = 65536;

    private const int LogEvery = 900;
    private static ExtenderLog? _log;

    /// <summary>#188: the BATCHER's own latch, and the whole of what "the batcher is armed" means. It goes
    /// down when <c>glMultiDrawElementsBaseVertex</c> turns out to be missing, and it takes nothing with it:
    /// the capture tables, the census and the bucket gate stay up on <see cref="PartTable.Active"/>. Before
    /// the split these were one flag, so an absent multidraw stood the whole Alloy pipeline down.</summary>
    private static bool _batchArmed;
    private static delegate* unmanaged<IntPtr, void> _origPartDraw;
    /// <summary>#137: <c>PartSkin::Draw</c>'s trampoline. A separate entry point for the same job, because
    /// the true skinmesh class never passes through <see cref="_origPartDraw"/> at all -- see
    /// <see cref="SymbolMap.PartSkinDraw"/>. Null leaves the class exactly where it was before #137: engine
    /// drawn, uncaptured, and (like every un-hooked class) invisible to the census.</summary>
    private static delegate* unmanaged<IntPtr, void> _origPartSkinDraw;

    /// <summary>The engine's own <c>SetCanDissolve</c> value for the draw in flight. A dissolving part is
    /// dirty and never batched.</summary>
    private static int _canDissolve;
    private static delegate* unmanaged<int, void> _origCanDissolve;
    private static int _pending = -1, _pendingSelf = -1;
    private static readonly int[] _mdCounts = new int[MaxSubmit];
    private static readonly void*[] _mdOffsets = new void*[MaxSubmit];
    private static readonly int[] _mdBase = new int[MaxSubmit];
    /// <summary>Group members a multidraw could not carry this period. Non-zero means geometry is being
    /// dropped from the tail of a group -- raise <see cref="MaxSubmit"/>.</summary>
    private static int _submitTrunc;

    // period stats
    private static long _batched, _calls, _skipped, _vanDead, _vanSolo, _vanDirty, _vanOwnOnly;
    private static long _recNoCap;   // why-dead forensics (bind path, cold)
    /// <summary>#256: parts the engine drew that the scene walk never recorded -- the acceptance counter for
    /// "every part the engine classified this frame is in the record set". Non-zero means the walk has a hole.</summary>
    internal static long _unrecorded;
    private static int _subDiagLeft = 3;   // #81 crash-triage one-shot
    private static bool _wasInModule;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // #170: ahead of every stand-down, as the host's unconditional call was. Priority pins it AFTER
        // DrawStats and BEFORE OwnedDraw -- alphabetical service discovery gives the wrong order on its own.
        FrameRegistry.Global.Register(FramePhase.PostPresent, 10, EndFrame, "BatchService.endFrame", log);
        string mode = Settings.Text(DrawModeKey, DrawModeBatch);
        if (mode != DrawModeBatch)
        { log.Info($"BatchService: standing down (sowx.cfg drawhooks={mode})"); return; }

        PartTable.Log = log;
        AlloyCensus.Configure(log);
        // Above the symbol gate on purpose: the mark-and-sweep contract is pure managed logic over synthetic
        // parts, so it is checkable on a headless run -- which is the only place it is ever checked at all.
        SceneWalk.SelfCheck(log);
        if (!PartTable.Resolve())
        { log.Info("BatchService: engine symbols unresolved -- batching unavailable (headless/Windows?)"); return; }
        // #256: enumeration is the SOURCE of every record now, so it resolves before the tables arm. Without
        // it there is no discovery at all -- nothing below may pretend otherwise by falling back to capture.
        if (!SceneWalk.Resolve(log)) return;

        _origPartDraw = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.PartTriMeshDraw, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnPartDraw);
        _origPartSkinDraw = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.PartSkinDraw, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnSkinDraw);
        // Chained, not Install()ed: RenderVertexData and Repack both have several subscribers (#88).
        _drawSlot = hooks.Chain(SymbolMap.GlRenderVertexData,
            (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)&OnVdoDraw, DrawPriority);
        BucketGate.Install(hooks);
        _origCanDissolve = (delegate* unmanaged<int, void>)hooks.Install(
            SymbolMap.RiSetCanDissolve, (IntPtr)(delegate* unmanaged<int, void>)&OnSetCanDissolve);
        SkyBox.Install(log, hooks);
        _repackSlot = hooks.Chain(SymbolMap.VdoRepack,
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnVdoRepack, RepackPriority);
        // #145: the death notice. Every other gate infers liveness (a sighting window, an identity compare
        // against fields that a freed block may still be holding); this one is told.
        _origPartDtor = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.PartDtor, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnPartDtor);
        // The CAPTURE tables arm here, and the census and the gate ride on them; the batcher's own latch is
        // separate and goes down on its own (see _batchArmed).
        PartTable.Active = _origPartDraw != null && _drawSlot != null && BucketGate.Installed
                           && _repackSlot != null && _origPartDtor != null;
        // #215: the one engine-side draw path the shim's funnel swallow cannot see. Under the cutover the
        // bucket walk still runs (that is how parts get captured at all) and Observe still fires, so a
        // pending group would submit a REAL glMultiDrawElementsBaseVertex through Alloy's own bindings --
        // invisible to the funnel counter, and drawn with no engine state bound. Batching goes down; every
        // part it would have batched is an Alloy part on this path anyway.
        _batchArmed = PartTable.Active && !RspShim.Armed;
        log.Info(!_batchArmed && PartTable.Active
            // #149's lesson: a period line reporting zero batched draws with no stated reason reads as a
            // bug. Under the cutover batching is OFF on purpose, and the arm-time line has to say so.
            ? "BatchService: capture armed, batching OFF -- #215's shim owns the frame (ownframe=1), and a "
              + "replayed multidraw is the one draw path its funnel swallow cannot see"
            : PartTable.Active
            ? "BatchService: static-geometry batching armed (sowx.cfg drawhooks=off disables; [batch] every 900 frames)"
            : _origPartDtor == null
              // #145: deliberate. Without the death notice, every liveness test left is an inference, and
              // the one the Alloy pass used walked a freed part into a freelist word.
              ? "BatchService: Part::~Part unhooked -- batching stands down rather than track parts it "
                + "cannot be told the death of"
              : "BatchService: hook install failed -- batching unavailable");
    }

    /// <summary>Chain priorities (#88): both symbols are shared, we sit under the shadow pipeline.</summary>
    internal const int DrawPriority = 50;
    private const int RepackPriority = 50;
    private static IntPtr* _drawSlot;
    private static IntPtr* _repackSlot;

    /// <summary>Repack observer: items of this VDO just moved -- force every member on it to re-read its
    /// draw arguments before it can batch again.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoRepack(IntPtr vdo)
    {
        var next = (delegate* unmanaged<IntPtr, void>)*_repackSlot;
        if (next != null) next(vdo);

        int stale = 0;
        for (int i = 0; i < MaxParts; i++)
            if (_pState[i] == StActive && _pVdo[i] == vdo) { _pState[i] = StStale; stale++; }
        // #256: a repack changes no scene membership, so nothing else would ever look at these again --
        // the epoch will not move for a layout change. Ask for a walk, whose sweep re-queues them.
        if (stale > 0) SceneWalk.NoteStale();
    }

    [UnmanagedCallersOnly]
    private static void OnSetCanDissolve(int v)
    {
        _canDissolve = v;
        if (_origCanDissolve != null) _origCanDissolve(v);
    }

    /// <summary>#96 passive bone capture: a skinned part is never batchable and never owned, so its draw runs
    /// vanilla either way -- but that draw is the one moment in the frame its bone matrices are live, so the
    /// caster pass brackets it and snapshots them. The bracket wraps the WHOLE body because every branch below
    /// ends in exactly one <c>_origPartDraw</c>, and the funnel observation has to land inside it.</summary>
    [UnmanagedCallersOnly]
    private static void OnPartDraw(IntPtr part)
    {
        // #126: bucket 8 reaches the capture path now. A fading part has to be RECORDED (and kept alive by
        // its sightings) or the Alloy pass has nothing to enroll for the fade class; what it must not do is
        // batch, which PartDraw refuses below.
        // #134: the census runs BEFORE the bucket gate. A part drawn outside Scene::RenderDrawBucket is
        // invisible to every counter this service keeps -- not owned, not captured, not even refused -- and
        // "no rows at all" reads identically to "nothing to report". The row names its _inBucket so the two
        // can be told apart.
        if (Live && part != IntPtr.Zero && _dumpLeft > 0)
        { _dumpLeft--; DumpPart(part); if (_dumpLeft == 0) _log?.Info("[dumpframe] end"); }

        // #125: the sky window comes FIRST and returns -- a sky part is drawn outside any bucket, so the
        // gate below would hand it straight to the engine and it would never be captured at all. It must also
        // stay out of the general part table: its transform is the engine's matrix STACK, which changes every
        // frame, so matrix probation would never settle for one.
        if (Live && part != IntPtr.Zero && SkyBox.InWindow)
        {
            int sky = SkyBox.Begin(part);
            Vanilla(part);
            SkyBox.End(sky);
            return;
        }
        if (!Live || _inBucket == 0 || part == IntPtr.Zero)
        { Vanilla(part); return; }
        IntPtr node = new PartRef(part).Node.Ptr;
        if (_bucketId <= MaxCasterBucket && IsInstance(part) && CasterPass.SkinBegin(part, node))
        {
            PartDraw(part);
            CasterPass.SkinEnd();
            return;
        }
        PartDraw(part);
    }

    /// <summary>#137: the TRUE skinmesh's capture point. Same body as <see cref="OnPartDraw"/> and
    /// deliberately so -- a cloak is a part like any other once it is in the table, and everything downstream
    /// (the census, candidacy, enrollment, the iterator's suppression, #128's per-instance sweep) then reaches
    /// it for free. Only two things differ: the vanilla draw goes through <see cref="_origPartSkinDraw"/>
    /// (which <see cref="Vanilla"/> routes), and the entry is stamped <see cref="_pSkin"/> so the Alloy pass
    /// knows to force a pose and bind bones rather than draw it in its bind pose.
    /// <para>The ticket assumed the bucket iterator could not suppress this class. It can, and the walk
    /// proves it rather than the reasoning: <c>Scene::RenderDrawBucket</c> @0x46d060 resolves each item
    /// through vtable slot 0x60 (<c>AsPartTriMesh</c>, which <see cref="OnNextItem"/> already mimics) and
    /// draws it through slot 0xd0 -- and slot 0xd0 of <c>_ZTV8PartSkin</c> IS <c>PartSkin::Draw</c>
    /// @0x4830b0, exactly as slot 0xd0 of <c>_ZTV11PartTriMesh</c> is @0x4513d0. So a skinmesh arrives on
    /// the same walk, under the same <c>_inBucket</c>, resolving to the same pointer this hook records.
    /// What was missing was only the entry.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnSkinDraw(IntPtr part)
    {
        // The swallow window: AlloyRenderer is calling the trampoline purely to make the engine solve this
        // part's bones. Never reached through the engine's own walk -- Vanilla() calls the trampoline, not the
        // hook -- so this is only ever a re-entry guard, and cheap enough to keep as one.
        bool prev = _inSkinDraw; IntPtr prevPart = _skinDrawPart;
        _inSkinDraw = true; _skinDrawPart = part;
        if (Live && part != IntPtr.Zero && _dumpLeft > 0)
        { _dumpLeft--; DumpPart(part); if (_dumpLeft == 0) _log?.Info("[dumpframe] end"); }
        if (!Live || _inBucket == 0 || part == IntPtr.Zero || _origPartSkinDraw == null)
        { Vanilla(part); _inSkinDraw = prev; _skinDrawPart = prevPart; return; }
        _skinSeen++;
        IntPtr node = new PartRef(part).Node.Ptr;
        bool bracket = _bucketId <= MaxCasterBucket && IsInstance(part)
                       && CasterPass.SkinBegin(part, node);
        PartDraw(part);
        if (bracket) CasterPass.SkinEnd();
        _inSkinDraw = prev; _skinDrawPart = prevPart;
    }

    /// <summary>Hand this part to the engine. Two draw entry points reach the shared body below and they are
    /// NOT interchangeable: calling <c>PartTriMesh::Draw</c> on a <c>PartSkin</c> would draw it in its bind
    /// pose (the bone solve lives in <c>PartSkin::Draw</c> alone), and every "fall back to vanilla" branch in
    /// this service is one place that could do it. So the choice is made once, here.</summary>
    private static void Vanilla(IntPtr part)
    {
        if (_timing)
        {
            long t0 = Stopwatch.GetTimestamp();
            VanillaBody(part);
            _vanTicks += Stopwatch.GetTimestamp() - t0;
            return;
        }
        VanillaBody(part);
    }

    private static void VanillaBody(IntPtr part)
    {
        // Keyed on the PART, not merely on the window. "PartSkin::Draw never enters PartTriMesh::Draw" is an
        // RE claim, and the code cannot check it -- but it does not have to depend on it either. If some
        // nested rigid draw ever did land inside the window, a flag would route a PartTriMesh* into
        // PartSkin::Draw: a call through the wrong vtable, i.e. a crash rather than a misrender.
        if (part != IntPtr.Zero && part == _skinDrawPart && _origPartSkinDraw != null)
        { _origPartSkinDraw(part); return; }
        if (_origPartDraw != null) _origPartDraw(part);
    }

    /// <summary>The part whose <c>PartSkin::Draw</c> body is on the stack, so <see cref="Vanilla"/> picks the
    /// right trampoline for it and the wrong one for nobody else. Zero outside a skin body.</summary>
    private static IntPtr _skinDrawPart;
    /// <summary>...and whether we are inside one at all, which is what <see cref="Record"/> stamps
    /// <see cref="_pSkin"/> from and the census reports.</summary>
    internal static bool _inSkinDraw;
    /// <summary>#137: the funnel-swallow window. While set, <see cref="OnVdoDraw"/> does not call the rest of
    /// the chain -- the engine's draw call never issues -- but everything BEFORE it in
    /// <c>PartSkin::Draw</c> has already run, which is the whole point: <c>GatherBones</c> and the 64
    /// <c>SetBoneMatrix</c> calls are what put this frame's pose in <c>GLRender::m_bones</c>.</summary>
    private static bool _skinForce;
    /// <summary>Did the swallowed call actually reach the funnel? <c>PartSkin::Draw</c> returns early on a
    /// part <c>IsPartRenderable</c> refuses, and a pose that was never solved is the previous part's -- so
    /// the Alloy pass hands a part back to the engine rather than draw it wearing someone else's bones.</summary>
    private static bool _skinPosed;
    private static long _skinSeen, _skinForced, _skinForceFailed;

    /// <summary>#137: make the engine solve this skinned part's pose WITHOUT drawing it. The Alloy colour
    /// pass runs at bucket 0/1, before the engine ever walks the dynamic buckets, so the bones sitting in
    /// <c>GLRender::m_bones</c> at that moment are the previous frame's -- a robe one frame behind the body
    /// it hangs on, which is visible swimming, not a subtle lag. Calling the trampoline with the funnel
    /// swallowed costs the same bone solve the engine was going to do anyway (the iterator then suppresses
    /// its draw entirely), and leaves this frame's pose in <c>m_bones</c> for the caller to copy.
    /// <para>Engine context only: the trampoline touches engine GL state. False = no pose was solved; the
    /// caller must hand the part back rather than draw it.</para></summary>
    internal static bool ForceSkinPose(IntPtr part)
    {
        if (_origPartSkinDraw == null || part == IntPtr.Zero) return false;
        _skinForce = true; _skinPosed = false;
        bool prev = _inSkinDraw; IntPtr prevPart = _skinDrawPart;
        _inSkinDraw = true; _skinDrawPart = part;
        IntPtr node = new PartRef(part).Node.Ptr;
        // The caster pass wants the same solved pose, and this is now the only place in the frame it is
        // live: the iterator suppresses the engine's own draw of an Alloy skinmesh.
        bool bracket = CasterPass.SkinBegin(part, node);
        _origPartSkinDraw(part);
        if (bracket) CasterPass.SkinEnd();
        _inSkinDraw = prev; _skinDrawPart = prevPart;
        _skinForce = false;
        _skinForced++;
        if (!_skinPosed) _skinForceFailed++;
        return _skinPosed;
    }

    /// <summary>#194: the entry-read timer. OFF by default and toggled by <c>/batchtime</c>, because the two
    /// <c>Stopwatch.GetTimestamp</c> pairs cost ~20ns each and this hook fires once per part per frame --
    /// measuring it costs more than the thing measured is suspected of costing.
    /// <para>What it isolates is the QUESTION the ticket asks and nothing else: the time
    /// <see cref="PartDrawBody"/> spends reading its own table, with the engine's draw subtracted out.
    /// Timing the hook whole would have measured <c>PartTriMesh::Draw</c>, which is orders of magnitude
    /// larger and would have answered "no" to every layout regardless of the layout.</para></summary>
    internal static bool _timing;
    private static long _pdTicks, _vanTicks, _pdCalls;
    /// <summary>Frames the timer has actually accumulated over. NOT <c>LogEvery</c>: the toggle zeroes the
    /// counters mid-window, so dividing a partial window by the full one under-reports ms/frame by up to
    /// 900x -- and it is the FIRST line anyone reads.</summary>
    private static int _pdFrames;

    /// <summary>Toggle the entry-read timer, reported on the next <c>[batch]</c> line.</summary>
    internal static string BatchTime()
    {
        _timing = !_timing;
        _pdTicks = 0; _vanTicks = 0; _pdCalls = 0; _pdFrames = 0;
        return _timing
            ? $"[batchtime] on -- PartDraw entry-read time on the next [batch] line (every {LogEvery} frames)"
            : "[batchtime] off";
    }

    private static void PartDraw(IntPtr part)
    {
        // #236: the walk HANDS BACK. Every Vanilla() below this point is Alloy saying "not mine, not this
        // pass" -- and under the #215 shim a hand-back that gets swallowed is a hole, not a frame of vanilla
        // shading. Nothing here clears the mark: a part that reaches the loop body at all was not drawn by
        // Alloy this pass, which is what the iterator already decided.
        // Scoped to the WALK, deliberately: the three hand-backs above (the sky window, the not-in-a-bucket
        // catch-all, the skin bail) stay swallowed. Whether any of them fires inside the window is not
        // measured, so marking them would be a guess about who draws the sky.
        bool prevHand = RspShim.HandBack;
        RspShim.HandBack = true;
        if (!_timing) PartDrawBody(part);
        else
        {
            long t0 = Stopwatch.GetTimestamp(), v0 = _vanTicks;
            PartDrawBody(part);
            // Minus whatever Vanilla spent inside, read as a DELTA so a nested vanilla draw is subtracted once.
            _pdTicks += Stopwatch.GetTimestamp() - t0 - (_vanTicks - v0);
            _pdCalls++;
        }
        RspShim.HandBack = prevHand;
    }

    private static void PartDrawBody(IntPtr part)
    {
        var p = new PartRef(part);
        int e = Find(part);
        // #256: a part the engine is drawing that Alloy has no record for is a part the scene walk did not
        // reach. It is NOT recorded here -- discovery is SceneWalk's, and inventing a record from a draw is
        // exactly the inheritance M5 exists to end. The engine draws it; SceneWalk.CrossCheck counts it.
        // Counted only for the WORLD scene: SceneWalk.Tick is never called for a GUI 3D panel's Scene (#91),
        // so every panel part is legitimately unenumerated and would otherwise make the alarm ring whenever
        // a character sheet is open -- i.e. exactly when nobody would trust it.
        if (e < 0) { if (_bucketWorld) _unrecorded++; Vanilla(part); return; }

        // #82: Alloy parts reaching the loop body mean the Alloy draw did not happen for them this pass --
        // vanilla, and never batched (a batch submit on top of an Alloy draw is the double-draw failure)
        // #236: HANDED BACK, not swallowed. Reaching this branch means the iterator's IterSight said Alloy
        // did NOT draw the part this pass (BucketGate returns the slot only then), which is #82's own
        // invariant -- so there is no draw to double. That is the engine-culled part coming back into view,
        // and the class stand-downs (the blended handback) with it: swallowing them was one invisible frame
        // per part, thousands at once on a camera spin, i.e. pop-in traded for flicker.
        if (OwnFlag[e] != 0) { _pSeen[e] = _frame; Vanilla(part); return; }

        // #256: StUnbound and StStale are both "re-read the VertexPrimitive", and SceneWalk.Rebind does it
        // once per frame off the record rather than by re-entering a vanilla draw to watch the funnel.
        if (_pState[e] != StActive || _pNode[e] != p.Node.Ptr || _pMat[e] != p.Material.Ptr)
        {
            // #256: an identity change is a RE-READ, not a death. Gob::ReplaceTexture @0x43e980 and friends
            // rewrite Part+0x58 in place with no birth and no death (#253 §3.2 item 4), and under capture
            // the record could only come back by being re-Recorded from a later draw. There is no such draw
            // any more, so killing the entry here would strand the part in vanilla for the life of the area.
            if (_pState[e] == StActive) SceneWalk.Rebind(e, p);
            // #128: a DEAD entry that is still drawing is still sighted. Enrollment reads this to decide
            // whether a dead instanced part is a live one it must drop its whole creature for, or despawned
            // debris it should forget -- so the sighting has to keep ticking past the death.
            _pSeen[e] = _frame;
            Vanilla(part); _vanDead++;
            return;
        }

        _pSeen[e] = _frame;
        // #126: recorded for the Alloy pass, never for this one -- and bucket 8 is the same case per frame
        // rather than per part (RenderDrawBucket folds the tile's fade factor into part+0xa0 for the length
        // of the draw, which no group's replayed state carries).
        // #236: NOT a hand-back to suppress. Alloy-only says "the Alloy pass draws this, not the batcher"
        // -- but OwnFlag is 0 here, so the Alloy pass has not enrolled it and nobody is drawing it. That is
        // the creature case exactly: instances are all Alloy-only, and swallowing this ate the torsos.
        if (_pOwnOnly[e] || _bucketId == AlphaBucket) { Vanilla(part); _vanOwnOnly++; return; }
        IntPtr node = _pNode[e];
        // matrix-class members must still match the group's transform exactly; identity-class members must
        // still BE identity-class -- any transition (mover) permanently demotes
        if (_pMx[e])
        {
            float* live = new NodeRef(node).Matrix;
            int gm = _pGroup[e] * 16;
            for (int f = 0; f < 16; f++)
                if (_gMatrix[gm + f] != live[f])
                { Die(e, "matrix-moved"); Vanilla(part); _vanDead++; return; }
        }
        else if (new NodeRef(node).MatrixClass || IsInstance(part))
        { Die(e, "became-matrix-class"); Vanilla(part); _vanDead++; return; }
        // #134: same read as the old `shaped` skin term, same correction -- an instance carries per-draw
        // state (alpha, colour, self-illum) that a replayed multidraw cannot vary, so THIS service still
        // never batches one. The Alloy pass takes them instead, one draw record each.
        bool dirty = IsInstance(part) || _canDissolve != 0;
        IntPtr tile = p.Tile;
        if (tile != IntPtr.Zero && Fading(tile)) dirty = true;
        // animated/cycled diffuse texture or re-rotated tile: content no longer matches the group -> vanilla
        if (new MaterialRef(_pMat[e]).Texture(0).Ptr != _pTex0[e] || p.TexRotateBits != _pRot[e])
        { Die(e, "texture-or-rotation-changed"); Vanilla(part); _vanDead++; return; }
        _pDirty[e] = dirty;

        int g = _pGroup[e];
        if (_gSubmitGen[g] == _gen)
        {
            if (_pIncluded[e] == _gen) { _skipped++; return; }   // covered by this walk's multi-draw
            Vanilla(part); _vanDirty++;                    // was stale/dirty at submit -> vanilla
            return;
        }
        if (dirty) { Vanilla(part); _vanDirty++; return; }
        if (_gCount[g] < 2 || !_batchArmed || Gl.MultiDrawElementsBaseVertex == null) { Vanilla(part); _vanSolo++; return; }

        // first clean member this walk: draw it vanilla (engine configures all state), then the funnel
        // hook submits the rest of the group while that state is still bound
        _gSubmitGen[g] = _gen; _pIncluded[e] = _gen;
        _pending = g; _pendingSelf = e;
        Vanilla(part);
        _pending = -1; _pendingSelf = -1;
    }

    /// <summary>Post-draw observer: runs the rest of the chain (the real draw) first, so engine state
    /// is fully bound. Capture + batch-submit path; near-free when nothing is pending.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoDraw(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        // #137: the swallow. This is a POSE call, not a draw -- the bone solve above it has already run and
        // that is all it was for, so the chain below (and with it the engine's draw) is skipped. Every other
        // subscriber sits at a LOWER priority, i.e. inside this call, so none of them counts a draw that did
        // not happen -- DrawStatsService says so in its own handler.
        if (_skinForce)
        {
            _skinPosed = true;
            CasterPass.SkinObserve(vdo, (int)item, (int)prim);
            return;
        }
        var next = (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)*_drawSlot;
        if (next != null) next(vdo, item, prim, size, offset);
        Observe(vdo, item, (int)prim, size, offset);
    }

    private static void Observe(IntPtr vdo, uint item, int prim, uint size, uint offset)
    {
        // #125: inside a sky part's vanilla draw this IS the (VDO, item, prim) capture -- and nothing else
        // in this service has a claim on it, since a sky part never reaches the tables below.
        // Gated on the WINDOW, not on a live capture slot: SkyBox.Begin refuses a part (no node, no
        // material, table full) without arming one, and the engine still draws that part. Falling through
        // would hand its funnel event to the batcher below, which could flush a pending group of WORLD
        // geometry under the sky's GL state -- sky projection, (SRC_ALPHA, ZERO), depth mask off.
        if (SkyBox.InWindow) { SkyBox.Observe(vdo, (int)item, prim); return; }
        // #256: the batch capture branch that used to live here is GONE. (VDO, item, prim) is read off
        // the VertexPrimitive by SceneWalk; the funnel is now only the CASTER's bone-capture point and the
        // batch submit trigger, neither of which is a record source.
        if (CasterPass.SkinObserve(vdo, (int)item, prim)) return;
        if (_pending < 0) return;

        int g = _pending;
        _pending = -1;                                            // one submit per trigger
        IntPtr gvdo = _gVdo[g];
        var gv = new VdoRef(gvdo);
        long elem = gv.ElemItems;
        long vert = gv.VertItems;
        int nElem = gv.ElemCount, nVert = gv.VertCount;
        if (elem == 0 || vert == 0) return;
        int k = 0;
        int m0 = _gFirst[g];
        for (int m = m0; m >= 0; m = _pNext[m])
        {
            // A ceiling you cannot observe hitting is a budget, and this one drops geometry when it does.
            if (k >= MaxSubmit) { _submitTrunc++; break; }
            if (m == _pendingSelf || _pState[m] != StActive || _pDirty[m] || _pSeen[m] < _frame - 1
                || OwnFlag[m] != 0 || _pOwnOnly[m]) continue;
            int it = _pItem[m];
            if (it < 0 || it >= nElem || it >= nVert) continue;
            _mdCounts[k] = *(int*)(elem + it * 8 + 4);
            _mdOffsets[k] = (void*)(long)((uint)(*(int*)(elem + it * 8)) * 2);
            _mdBase[k] = *(int*)(vert + it * 8);
            _pIncluded[m] = _gen;
            k++;
        }
        if (k == 0) return;
        if (_subDiagLeft > 0)
        {
            _subDiagLeft--;
            _log?.Info($"BatchService: submit-diag vdo=0x{(ulong)gvdo:x} k={k} prim=0x{_primTable[_gPrim[g]]:x} first: count={_mdCounts[0]} off={(long)_mdOffsets[0]} base={_mdBase[0]}");
        }
        fixed (int* pc = _mdCounts) fixed (void** po = _mdOffsets) fixed (int* pb = _mdBase)
            Gl.MultiDrawElementsBaseVertex(_primTable[_gPrim[g]], pc, GlUnsignedShort, po, k, pb);
        _batched += k; _calls++;
    }

    private static delegate* unmanaged<IntPtr, void> _origPartDtor;
    /// <summary>#145: parts destroyed out from under a recorded entry, this session. The [batch] line prints
    /// it, and a count that stays 0 across an area transition means the hook is not firing -- which looks
    /// exactly like "fixed" from the outside, so it is the number to read.</summary>
    internal static long DtorRevokes { get; private set; }

    /// <summary>#145: <c>Part::~Part</c>. The part table is keyed by POINTER and every liveness test on it is
    /// an inference -- a sighting window, or an identity compare against fields a freed block can still be
    /// holding. That is how the Alloy pass came to walk a freed part into <c>GobAlphaMul</c> and chase
    /// <c>*(gob+0x68)</c> into a freelist word: the enrolled entry never learned its part had died.
    /// <para>The handler only COMPARES the dying pointer -- it never dereferences it -- and does its work in
    /// O(1) through the same hash the walk uses, because an area transition destroys parts by the thousand.
    /// Allocation-free for the same reason.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnPartDtor(IntPtr part)
    {
        // #150: unconditional, and NOT inside the Find branch below -- a sky part is deliberately routed away
        // from this table (OnPartDraw's SkyBox window returns early), so Find never sees one.
        SkyBox.Revoke(part);
        // #221: the freed mark goes on unconditionally -- including an entry some other gate already killed,
        // whose tombstone would otherwise outlive the allocation and block the address for good.
        int e = MarkFreed(part);
        if (e >= 0 && _pState[e] != StEmpty && _pState[e] != StDead)
        {
            OwnFlag[e] = 0;
            // ...and the gob with it: the dead-entry branch of TryOwnCandidateInner reads _pGob[e] and then
            // dereferences _pPart[e] to confirm it, which on a destroyed part is the same stale chase.
            _pGob[e] = IntPtr.Zero;
            Die(e, "destroyed");
            DtorRevokes++;
        }
        _origPartDtor(part);
    }

    /// <summary>#256: intern this record's group from field reads. The tail of what used to be
    /// <c>Complete</c>, with the two things it could only learn from a draw -- the (VDO, item, prim) triple
    /// and which bucket the engine filed the part in -- supplied by <see cref="SceneWalk"/> instead, off the
    /// VertexPrimitive and off <c>Scene::AddPartsTreeToDrawBuckets</c>'s own material rules. The group key
    /// itself is unchanged: material CONTENT, never the <c>Material*</c>.
    /// <para>False = the entry died here (no VDO, or the group table is full); the caller must not treat it
    /// as bound. Cold -- once per record, on a walk.</para></summary>
    internal static bool Mint(IntPtr part, int e, int bucket, int sub, IntPtr scene, bool world, bool ownOnly)
    {
        IntPtr mat = _pMat[e];
        if (_pVdo[e] == IntPtr.Zero) { _recNoCap++; Die(e, "no-vdo"); return false; }
        _pOwnOnly[e] = ownOnly;
        // #221: a reused slot must not be spliced into its previous tenant's member chain, and re-interning
        // to the SAME group would write _pNext[e] = e and hang the submit walk on the render thread.
        Unlink(e);
        int prim = _pPrim[e];
        var pr = new PartRef(part);
        var mr = new MaterialRef(mat);
        int hint = Engine.RenderHint(pr);
        // material content: texture slots (equality only -- CAurTexture* are borrowed, never deref'd),
        // shared-material override, and the blend-disable flag RenderFlat reads at mat+0x98
        IntPtr t0 = mr.Texture(0).Ptr, t1 = mr.Texture(1).Ptr,
               t2 = mr.Texture(2).Ptr, t3 = mr.Texture(3).Ptr;
        IntPtr shared = mr.Shared.Ptr;
        IntPtr env = mr.EnvMap;
        byte flag = mr.BlendFlag;
        bool light = flag == 0;
        int rot = pr.TexRotateBits;
        bool mx = _pMx[e];
        // #128: a MOVER's transform is not part of its group key -- it changes every frame, so keying on it
        // would mint one group per part per pose and exhaust the table. The flag itself IS in the key, which
        // is what keeps a mover out of a static group whose frozen matrix a replayed multidraw would apply.
        bool mover = IsInstance(part);
        float* liveM = new NodeRef(_pNode[e]).Matrix;
        // The group key, hashed and interned. This was a linear walk of _groupCount until the caps were
        // raised for object-dense areas: O(n^2) across an area load, which is 24M field compares at the 7000
        // groups that used to be the ceiling and 13.4 BILLION at 163840 -- minutes of hung load, i.e. the
        // raise would have traded "some tiles render vanilla" for "the area never finishes loading".
        // The probe carries the SAME comparison the walk did (GroupMatches), so a hash collision costs a
        // compare and never a wrong group.
        ulong h = GroupHash(_pVdo[e], t0, t1, t2, t3, shared, env, flag, rot, hint, prim,
                            light, mx, mover, liveM);
        int slot = (int)(h * 0x9E3779B97F4A7C15UL >> (64 - GroupHashBits));
        int g = -1, free = -1;
        for (int p = 0; p < GroupHashSize; p++)
        {
            int j = (slot + p) & (GroupHashSize - 1);
            int cand = _gSlot[j];
            if (cand < 0) { free = j; break; }
            if (_gHash[cand] == h && GroupMatches(cand, _pVdo[e], t0, t1, t2, t3, shared, env, flag, rot,
                                                  hint, prim, light, mx, mover, liveM))
            { g = cand; break; }
        }
        if (g < 0)
        {
            if (_groupCount >= MaxGroups || free < 0) { Die(e, "groups-full"); return false; }
            g = _groupCount++;
            _gSlot[free] = g; _gHash[g] = h;
            _gVdo[g] = _pVdo[e]; _gTex0[g] = t0; _gTex1[g] = t1; _gTex2[g] = t2; _gTex3[g] = t3;
            _gShared[g] = shared; _gEnv[g] = env; _gFlag[g] = flag; _gRot[g] = rot; _gBucket[g] = bucket; _gPhase[g] = sub;
            _gScene[g] = scene; _gWorld[g] = world;
            _gMx[g] = mx; _gMover[g] = mover;
            if (mx && !mover) for (int f = 0; f < 16; f++) _gMatrix[g * 16 + f] = liveM[f];
            _gHint[g] = hint; _gLight[g] = light; _gPrim[g] = prim; _gFirst[g] = -1; _gCount[g] = 0; _gSubmitGen[g] = -1;
        }
        _pTex0[e] = t0; _pRot[e] = rot;
        _pGroup[e] = g; _pNext[e] = _gFirst[g]; _gFirst[g] = e; _gCount[g]++;
        _pState[e] = StActive; _pDirty[e] = false; _pIncluded[e] = -1;
        return true;
    }


    /// <summary>Frame tick from UiHostService. Periodic stats; the log is the only cold path.</summary>
    public static void EndFrame()
    {
        if (!PartTable.Active) return;
        // #188: the stand-down is the BATCHER's, not the table's. Before the split this cleared the one
        // shared flag, so a GL without multidraw took the census and the bucket gate down with the submits.
        if (_batchArmed && Gl.MultiDrawElementsBaseVertex == null && _log != null)
        {
            Gl.Init(_log);                                         // GL context exists here, not at service init
            if (Gl.MultiDrawElementsBaseVertex == null) { _batchArmed = false; _log.Info("BatchService: glMultiDrawElementsBaseVertex missing -- batching off"); return; }
        }
        // module-unload edge: every part/VDO dies with the module -- drop all recorded identity
        bool inModule = GameGate.InModule;
        if (_wasInModule && !inModule) WipeTables("module unloaded");
        // ...and entering one restarts the settle clock too: the table is empty either way, but an area load
        // is where the frames that matter to the census actually begin.
        if (!_wasInModule && inModule) _wipeFrame = _frame;
        _wasInModule = inModule;
        // Table pressure. Entries are never reclaimed individually -- Insert only ever probes for StEmpty --
        // so an area transition leaves its dead behind forever and the table eventually fills with debris.
        // Wiping is the reclamation, and it is right for exactly that case.
        //
        // What it is NOT right for is a table full of LIVE parts, which is what a large area is. Wiping
        // those reclaims nothing, drops every Alloy part back to the engine, and refills within the second
        // -- the 1Hz "the renderer keeps falling back to vanilla" collapse, which got worse the bigger the
        // area precisely because a bigger area reaches the cap sooner. So the diagnosis is MEASURED here
        // rather than asserted: one sweep of the state array, and only at pressure, so it costs nothing on
        // an ordinary frame.
        if (_partCount >= MaxParts - MaxParts / 4 || _groupCount >= MaxGroups - MaxGroups / 8)
            OnTablePressure();
        if (_timing) _pdFrames++;
        if (++_frame % LogEvery != 0) return;
        _log?.Info("BatchService: " + SceneWalk.Period());
        int members = 0, solo = 0;
        for (int i = 0; i < _groupCount; i++) { members += _gCount[i]; if (_gCount[i] < 2) solo++; }
        _log?.Info($"BatchService: [batch] {_groupCount} groups ({solo} solo) / {members} members ({_partCount} parts tracked), " +
                   $"batched {(float)_batched / LogEvery:F0} draws/frame in {(float)_calls / LogEvery:F1} calls, " +
                   $"iter-skipped {(float)BucketGate._iterSkip / LogEvery:F0} + late {(float)_skipped / LogEvery:F0}, " +
                   $"vanilla solo {(float)_vanSolo / LogEvery:F0} / " +
                   $"dirty {(float)_vanDirty / LogEvery:F0} / dead {(float)_vanDead / LogEvery:F0} / " +
                   $"Alloy-only {(float)_vanOwnOnly / LogEvery:F0}, " +
                   (_saturated ? $"TABLE SATURATED ({_satLive} live / {_satDead} dead, surplus engine-drawn), " : "") +
                   (_submitTrunc > 0 ? $"SUBMIT TRUNCATED {_submitTrunc}x (a group exceeded {MaxSubmit} members -- geometry dropped), " : "") +
                   $"rec-dead noVdo={_recNoCap}" +
                   // #256: parts the engine drew that the walk never enumerated -- the acceptance number for
                   // "the record set covers what the engine classified". Non-zero is a hole in the walk.
                   $" unrecorded-draws={_unrecorded}" +
                   // #145: parts the engine destroyed under a recorded entry. A 0 that survives an area
                   // transition means Part::~Part is not reaching us, which is indistinguishable from
                   // "nothing died" in every other number here.
                   $" dtor-revoked={DtorRevokes} reclaimed={_reclaims}" +
                   // #137: the skinmesh class, which had no row at all before it had a hook. A zero here is
                   // "the hook never fired", not "there were no cloaks"; forced/no-pose separate a class the
                   // Alloy pass took from one it could not solve.
                   (_origPartSkinDraw == null ? " | skinned: no hook"
                      : $" | skinned: {(float)_skinSeen / LogEvery:F1} draws/frame, " +
                        $"{(float)_skinForced / LogEvery:F1} poses forced ({_skinForceFailed} unsolved)")
                   // #194: the parallel-array question, as a number. ns/call is the per-part entry read;
                   // ms/frame is what a struct-of-arrays -> array-of-structs conversion could win at most.
                   + (!_timing || _pdCalls == 0 ? ""
                      : $" | entry reads: {(float)_pdCalls / _pdFrames:F0}/frame at " +
                        $"{_pdTicks * 1e9d / Stopwatch.Frequency / _pdCalls:F0}ns = " +
                        $"{_pdTicks * 1e3d / Stopwatch.Frequency / _pdFrames:F3}ms/frame" +
                        $" over {_pdFrames} frames"));
        _batched = 0; _calls = 0; _skipped = 0; BucketGate._iterSkip = 0; _vanSolo = 0; _vanDirty = 0; _vanDead = 0;
        _vanOwnOnly = 0; _skinSeen = 0; _skinForced = 0; _skinForceFailed = 0;
        _recNoCap = 0; _unrecorded = 0; _submitTrunc = 0;
        _pdTicks = 0; _vanTicks = 0; _pdCalls = 0; _pdFrames = 0;
    }
}
