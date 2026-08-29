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

/// <summary>#258: the engine draw funnels Alloy sits on, and nothing else. This was <c>BatchService</c>,
/// ADR 0006's extender-owned multidraw submission; the #215 cutover made <c>RspShim</c> own the frame, and a
/// replayed multidraw is the one draw path its funnel swallow cannot see -- so the batcher had been latched
/// off since it shipped while everything upstream of it still ran per part per frame. The batcher, its group
/// registry, matrix probation and the 64K submit arrays are gone; what stayed is the set of hooks the rest
/// of Alloy is built on, and every one of them has a live consumer:
/// <list type="bullet">
/// <item><c>PartTriMesh::Draw</c> / <c>PartSkin::Draw</c> -- the vanilla draw, which under
/// <see cref="RspShim"/> is also the HAND-BACK (#236); <see cref="SkyBox"/>'s capture window (#125); the
/// caster pass's bone bracket (#96); and #256's <c>unrecorded-draws</c> counter, the acceptance number for
/// "the scene walk reached every part the engine draws".</item>
/// <item><c>Part::~Part</c> -- the death notice (#145, #221). Every other liveness test is an inference.</item>
/// <item><c>GLRender::RenderVertexData</c> -- the bone-capture and pose-swallow funnel (#137).</item>
/// <item><c>VertexDataObject::Repack</c> -- the layout-change edge no scene epoch reports.</item>
/// </list>
/// <para>Armed by <c>drawhooks</c> in <c>sowx.cfg</c> (the default arms this service, <c>stats</c> hands the
/// draw hooks to <c>DrawStatsService</c>, anything else disables both -- one mode, because both hook the
/// same symbols). Hot and alloc-free throughout.</para></summary>
public sealed unsafe class DrawFunnelService : IExtenderService
{
    /// <summary>#182: the ONE key that says who owns the draw hooks. <c>batch</c> (default) = this service
    /// -- the value keeps its name after #258 deleted the batcher, because it is a <c>sowx.cfg</c> contract
    /// with existing installs (ADR 0013) and renaming it would silently disarm them. Reads as:
    /// <c>stats</c> = <c>DrawStatsService</c>'s histogram, anything else = neither. It is one mode rather than
    /// two booleans because the two arm the same symbols and cannot coexist; and it is config rather than a
    /// command because both decide what installs, which only happens once.</summary>
    internal const string DrawModeKey = "drawhooks", DrawModeBatch = "batch", DrawModeStats = "stats";

    private const int LogEvery = 900;
    private static ExtenderLog? _log;

    private static delegate* unmanaged<IntPtr, void> _origPartDraw;
    /// <summary>#137: <c>PartSkin::Draw</c>'s trampoline. A separate entry point for the same job, because
    /// the true skinmesh class never passes through <see cref="_origPartDraw"/> at all -- see
    /// <see cref="SymbolMap.PartSkinDraw"/>. Null leaves the class exactly where it was before #137: engine
    /// drawn, uncaptured, and (like every un-hooked class) invisible to the census.</summary>
    private static delegate* unmanaged<IntPtr, void> _origPartSkinDraw;

    // period stats
    /// <summary>#256: parts the engine drew that the scene walk never recorded -- the acceptance counter for
    /// "every part the engine classified this frame is in the record set". Non-zero means the walk has a hole.</summary>
    internal static long _unrecorded;
    private static bool _wasInModule;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // #170: ahead of every stand-down, as the host's unconditional call was. Priority pins it AFTER
        // DrawStats and BEFORE OwnedDraw -- alphabetical service discovery gives the wrong order on its own.
        FrameRegistry.Global.Register(FramePhase.PostPresent, 10, EndFrame, "DrawFunnels.endFrame", log);
        string mode = Settings.Text(DrawModeKey, DrawModeBatch);
        if (mode != DrawModeBatch)
        { log.Info($"DrawFunnels: standing down (sowx.cfg drawhooks={mode})"); return; }

        PartTable.Log = log;
        AlloyCensus.Configure(log);
        // Above the symbol gate on purpose: the mark-and-sweep contract is pure managed logic over synthetic
        // parts, so it is checkable on a headless run -- which is the only place it is ever checked at all.
        SceneWalk.SelfCheck(log);
        if (!PartTable.Resolve())
        { log.Info("DrawFunnels: engine symbols unresolved -- Alloy unavailable (headless/Windows?)"); return; }
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
        SkyBox.Install(log, hooks);   // #261: resolves symbols only -- there is no sky hook any more
        _repackSlot = hooks.Chain(SymbolMap.VdoRepack,
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnVdoRepack, RepackPriority);
        // #145: the death notice. Every other gate infers liveness (a sighting window, an identity compare
        // against fields that a freed block may still be holding); this one is told.
        _origPartDtor = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.PartDtor, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnPartDtor);
        // The record tables arm here, and the census and the gate ride on them.
        PartTable.Active = _origPartDraw != null && _drawSlot != null && BucketGate.Installed
                           && _repackSlot != null && _origPartDtor != null;
        log.Info(PartTable.Active
            ? "DrawFunnels: engine draw funnels armed (sowx.cfg drawhooks=off disables; [funnels] every 900 frames)"
            : _origPartDtor == null
              // #145: deliberate. Without the death notice, every liveness test left is an inference, and
              // the one the Alloy pass used walked a freed part into a freelist word.
              ? "DrawFunnels: Part::~Part unhooked -- Alloy stands down rather than track parts it "
                + "cannot be told the death of"
              : "DrawFunnels: hook install failed -- Alloy unavailable");
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
        OwnedBuffers.NoteRepack();   // #260: the arena argument's other rate -- see OwnedBuffers.NoteRepack

        int stale = 0;
        for (int i = 0; i < MaxParts; i++)
            if (_pState[i] == StActive && _pVdo[i] == vdo) { _pState[i] = StStale; stale++; }
        // #256: a repack changes no scene membership, so nothing else would ever look at these again --
        // the epoch will not move for a layout change. Ask for a walk, whose sweep re-queues them.
        if (stale > 0) SceneWalk.NoteStale();
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
        PartDrawBody(part);
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

        // #82: Alloy parts reaching the loop body mean the Alloy draw did not happen for them this pass.
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
            Vanilla(part);
            return;
        }

        _pSeen[e] = _frame;
        // #258: every path out of here is the same one. The batcher's tail -- matrix probation, the dirty
        // tests, the group submit -- decided whether THIS service would draw the part, and it no longer
        // draws anything. Under the shim a part reaching this body was handed back by the Alloy pass, and
        // the engine is what draws it.
        Vanilla(part);
    }

    /// <summary>Post-draw observer: runs the rest of the chain (the real draw) first, so engine state is
    /// fully bound. Near-free -- the sky window and the bone capture are all that read it.</summary>
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
        Observe(vdo, item, (int)prim);
    }

    private static void Observe(IntPtr vdo, uint item, int prim)
    {
        // #256: the capture branch that used to live here is GONE -- (VDO, item, prim) is read off the
        // VertexPrimitive by SceneWalk. #258 took the batch submit that was the other half, so the funnel
        // is now the CASTER's bone-capture point and nothing else.
        CasterPass.SkinObserve(vdo, (int)item, prim);
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

    /// <summary>Frame tick from UiHostService. Periodic stats; the log is the only cold path.</summary>
    public static void EndFrame()
    {
        if (!PartTable.Active) return;
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
        if (_partCount >= MaxParts - MaxParts / 4) OnTablePressure();
        if (++_frame % LogEvery != 0) return;
        _log?.Info("DrawFunnels: " + SceneWalk.Period());
        _log?.Info($"DrawFunnels: [funnels] {_partCount} parts tracked, " +
                   (_saturated ? $"TABLE SATURATED ({_satLive} live / {_satDead} dead, surplus engine-drawn), " : "") +
                   // #256: parts the engine drew that the walk never enumerated -- the acceptance number for
                   // "the record set covers what the engine classified". Non-zero is a hole in the walk.
                   $"unrecorded-draws={_unrecorded}" +
                   // #145: parts the engine destroyed under a recorded entry. A 0 that survives an area
                   // transition means Part::~Part is not reaching us, which is indistinguishable from
                   // "nothing died" in every other number here.
                   $" dtor-revoked={DtorRevokes} reclaimed={_reclaims}" +
                   // #137: the skinmesh class, which had no row at all before it had a hook. A zero here is
                   // "the hook never fired", not "there were no cloaks"; forced/no-pose separate a class the
                   // Alloy pass took from one it could not solve.
                   (_origPartSkinDraw == null ? " | skinned: no hook"
                      : $" | skinned: {(float)_skinSeen / LogEvery:F1} draws/frame, " +
                        $"{(float)_skinForced / LogEvery:F1} poses forced ({_skinForceFailed} unsolved)"));
        _skinSeen = 0; _skinForced = 0; _skinForceFailed = 0;
        _unrecorded = 0;
    }
}
