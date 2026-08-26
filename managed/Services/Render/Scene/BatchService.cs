using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Services.Render.SceneLayout;
using static Sowx.Services.Render.PartTable;
using static Sowx.Services.Render.BucketGate;
using static Sowx.Services.Render.OwnershipCensus;
namespace Sowx.Services.Render;

/// <summary>ADR 0006 phase 1: extender-owned draw submission for opaque, pre-transformed static geometry --
/// and, since #188, that is ALL this type is. The ownership census went to <see cref="OwnershipCensus"/>,
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
/// <para>Excluded from BATCHING (always vanilla, unless the owned pass takes them): matrix-path/skinned
/// parts, bumpy-shiny materials, non-opaque parts, bucket 8 (per-item alpha), keyhole-dissolving or
/// tile-fading members (per-frame via the engine's own <c>SetCanDissolve</c> value + the part's tile-fade
/// factor). #126 split "not batchable" from "not recorded": the transparent classes are captured and
/// grouped like anything else, because that is what the owned pass enrolls from, and carry a
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

    /// <summary>The blend-disable flag <c>RenderFlat</c> reads at mat+0x98 (Ghidra
    /// GetTexture/GetEnvMapTID @0x4c5810/0x4c5c40; texture slots sit at mat+i*0x10).</summary>
    private const int MatBlendFlag = 0x98;

    /// <summary>The material's own env map (<c>Material::HasEnvMap</c> @0x4c6180 tests exactly this field).
    /// It is NOT one of the numbered texture slots, so <c>GetTexture</c> never reaches it -- a group keyed on
    /// the numbered slots alone cannot tell an env-mapped material from a plain one sharing its diffuse, and
    /// one batched draw would then paint all of them with the representative's shine (or none of them).
    /// Compared only, never dereferenced: a <c>CAurTexture*</c> is borrowed.</summary>
    private const int MatEnvMap = 0x40;

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
    /// the split these were one flag, so an absent multidraw stood the whole owned pipeline down.</summary>
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
    private static int _capture = -1, _pending = -1, _pendingSelf = -1;
    private static readonly int[] _mdCounts = new int[MaxSubmit];
    private static readonly void*[] _mdOffsets = new void*[MaxSubmit];
    private static readonly int[] _mdBase = new int[MaxSubmit];
    /// <summary>Group members a multidraw could not carry this period. Non-zero means geometry is being
    /// dropped from the tail of a group -- raise <see cref="MaxSubmit"/>.</summary>
    private static int _submitTrunc;

    // period stats
    private static long _batched, _calls, _skipped, _vanDead, _vanSolo, _vanDirty, _vanOwnOnly;
    private static long _recNotOk, _recNoCap, _recBadVdo;   // why-dead forensics (record path, cold)
    private static int _capDiagLeft = 5, _subDiagLeft = 3;   // #81 crash-triage one-shots
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
        OwnershipCensus.Configure(log);
        if (!PartTable.Resolve())
        { log.Info("BatchService: engine symbols unresolved -- batching unavailable (headless/Windows?)"); return; }

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
        Render.SkyBox.Install(log, hooks);
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
        _batchArmed = PartTable.Active;
        log.Info(PartTable.Active
            ? "BatchService: static-geometry batching armed (sowx.cfg drawhooks=off disables; [batch] every 900 frames)"
            : _origPartDtor == null
              // #145: deliberate. Without the death notice, every liveness test left is an inference, and
              // the one the owned pass used walked a freed part into a freelist word.
              ? "BatchService: Part::~Part unhooked -- batching stands down rather than track parts it "
                + "cannot be told the death of"
              : "BatchService: hook install failed -- batching unavailable");
    }

    /// <summary>Chain priorities (#88): both symbols are shared, we sit under the shadow pipeline.</summary>
    private const int DrawPriority = 50;
    private const int RepackPriority = 50;
    private static IntPtr* _drawSlot;
    private static IntPtr* _repackSlot;

    /// <summary>Repack observer: items of this VDO just moved -- force every member on it to
    /// recapture before it can batch again.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoRepack(IntPtr vdo)
    {
        var next = (delegate* unmanaged<IntPtr, void>)*_repackSlot;
        if (next != null) next(vdo);

        for (int i = 0; i < MaxParts; i++)
            if (_pState[i] == StActive && _pVdo[i] == vdo) _pState[i] = StStale;
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
        // its sightings) or the owned pass has nothing to enroll for the fade class; what it must not do is
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
        if (Live && part != IntPtr.Zero && Render.SkyBox.InWindow)
        {
            int sky = Render.SkyBox.Begin(part);
            Vanilla(part);
            Render.SkyBox.End(sky);
            return;
        }
        if (!Live || _inBucket == 0 || part == IntPtr.Zero)
        { Vanilla(part); return; }
        IntPtr node = *(IntPtr*)(part + PartNode);
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
    /// (which <see cref="Vanilla"/> routes), and the entry is stamped <see cref="_pSkin"/> so the owned pass
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
        // The swallow window: OwnedDrawService is calling the trampoline purely to make the engine solve this
        // part's bones. Never reached through the engine's own walk -- Vanilla() calls the trampoline, not the
        // hook -- so this is only ever a re-entry guard, and cheap enough to keep as one.
        bool prev = _inSkinDraw; IntPtr prevPart = _skinDrawPart;
        _inSkinDraw = true; _skinDrawPart = part;
        if (Live && part != IntPtr.Zero && _dumpLeft > 0)
        { _dumpLeft--; DumpPart(part); if (_dumpLeft == 0) _log?.Info("[dumpframe] end"); }
        if (!Live || _inBucket == 0 || part == IntPtr.Zero || _origPartSkinDraw == null)
        { Vanilla(part); _inSkinDraw = prev; _skinDrawPart = prevPart; return; }
        _skinSeen++;
        IntPtr node = *(IntPtr*)(part + PartNode);
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
    /// the owned pass hands a part back to the engine rather than draw it wearing someone else's bones.</summary>
    private static bool _skinPosed;
    private static long _skinSeen, _skinForced, _skinForceFailed;

    /// <summary>#137: make the engine solve this skinned part's pose WITHOUT drawing it. The owned colour
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
        IntPtr node = *(IntPtr*)(part + PartNode);
        // The caster pass wants the same solved pose, and this is now the only place in the frame it is
        // live: the iterator suppresses the engine's own draw of an owned skinmesh.
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
        int e = Find(part);
        if (e < 0) { Record(part); return; }

        // #82: owned parts reaching the loop body mean the owned draw did not happen for them this pass --
        // vanilla, and never batched (a batch submit on top of an owned draw is the double-draw failure)
        if (OwnFlag[e] != 0) { _pSeen[e] = _frame; Vanilla(part); return; }

        if (_pState[e] == StProbe)
        {
            if (_pNode[e] != *(IntPtr*)(part + PartNode) || _pMat[e] != *(IntPtr*)(part + PartMaterial)
                || HashMatrix((float*)(_pNode[e] + NodeMatrix)) != _pMxHash[e])
            { Die(e, "probation-moved"); Vanilla(part); _vanDead++; return; }
            Complete(part, e);
            return;
        }
        if (_pState[e] == StStale && _pNode[e] == *(IntPtr*)(part + PartNode) && _pMat[e] == *(IntPtr*)(part + PartMaterial))
        {
            // Repack moved this VDO's items: recapture (item index + prim) through the funnel during a
            // vanilla draw; the entry keeps its group (Repack changes layout, not content/textures)
            IntPtr oldVdo = _pVdo[e];
            _pVdo[e] = IntPtr.Zero; _pState[e] = StCapturing;
            _capture = e;
            Vanilla(part);
            _capture = -1;
            _pState[e] = _pState[e] == StCapturing && _pVdo[e] == oldVdo ? StActive : Die(e, "recapture-failed");
            _pSeen[e] = _frame; _pDirty[e] = false;
            _vanDead++;
            return;
        }
        if (_pState[e] != StActive || _pNode[e] != *(IntPtr*)(part + PartNode) || _pMat[e] != *(IntPtr*)(part + PartMaterial))
        {
            // recycled pointer or known-unbatchable: vanilla (identity mismatch also permanently kills it)
            if (_pState[e] == StActive) Die(e, "identity-changed");
            // #128: a DEAD entry that is still drawing is still sighted. Enrollment reads this to decide
            // whether a dead instanced part is a live one it must drop its whole creature for, or despawned
            // debris it should forget -- so the sighting has to keep ticking past the death.
            _pSeen[e] = _frame;
            Vanilla(part); _vanDead++;
            return;
        }

        _pSeen[e] = _frame;
        // #126: recorded for the owned pass, never for this one -- and bucket 8 is the same case per frame
        // rather than per part (RenderDrawBucket folds the tile's fade factor into part+0xa0 for the length
        // of the draw, which no group's replayed state carries).
        if (_pOwnOnly[e] || _bucketId == AlphaBucket) { Vanilla(part); _vanOwnOnly++; return; }
        IntPtr node = _pNode[e];
        // matrix-class members must still match the group's transform exactly; identity-class members must
        // still BE identity-class -- any transition (mover) permanently demotes
        if (_pMx[e])
        {
            float* live = (float*)(node + NodeMatrix);
            int gm = _pGroup[e] * 16;
            for (int f = 0; f < 16; f++)
                if (_gMatrix[gm + f] != live[f])
                { Die(e, "matrix-moved"); Vanilla(part); _vanDead++; return; }
        }
        else if ((*(byte*)(node + NodeFlags) & 0x1e) != 0 || IsInstance(part))
        { Die(e, "became-matrix-class"); Vanilla(part); _vanDead++; return; }
        // #134: same read as the old `shaped` skin term, same correction -- an instance carries per-draw
        // state (alpha, colour, self-illum) that a replayed multidraw cannot vary, so THIS service still
        // never batches one. The owned pass takes them instead, one draw record each.
        bool dirty = IsInstance(part) || _canDissolve != 0;
        IntPtr tile = *(IntPtr*)(part + PartTile);
        if (tile != IntPtr.Zero && Fading(tile)) dirty = true;
        // animated/cycled diffuse texture or re-rotated tile: content no longer matches the group -> vanilla
        if ((IntPtr)_getTexture(_pMat[e], 0) != _pTex0[e] || *(int*)(part + PartTexRotate) != _pRot[e])
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
        if (Render.SkyBox.InWindow) { Render.SkyBox.Observe(vdo, (int)item, prim); return; }
        // #96: inside an instanced part's draw the funnel IS the caster capture point -- the node matrix is
        // live there, and the (VDO, item, prim) triple is the same one the depth pass will submit.
        // #134 splits it from the batch capture rather than letting it swallow one. Before, every instanced
        // part was dead by the time it got here, so consuming the observation cost nothing; now that they
        // enrol, an eaten observation leaves _pVdo null and Complete kills the entry as "no-capture" --
        // i.e. the gate flip would have been a no-op with a plausible-looking reason. The two records read
        // the same three values and neither writes what the other reads, so both simply happen.
        if (_capture >= 0)
        {
            CasterPass.SkinObserve(vdo, (int)item, prim);
            int e = _capture;
            if (_pVdo[e] != IntPtr.Zero || vdo == IntPtr.Zero || *(int*)(vdo + VdoMode) != 1)
                Die(e, "multidraw-or-unindexed");                              // multi-draw part / non-indexed VDO
            else
            {
                _pVdo[e] = vdo; _pItem[e] = (int)item; _pPrim[e] = prim;
                if (_capDiagLeft > 0)
                {   // one-shot: prove the table-derived range matches the engine's own draw args (#81 crash triage)
                    _capDiagLeft--;
                    long el = (long)*(IntPtr*)(vdo + VdoElemItems);
                    int it = (int)item;
                    string tab = el != 0 && it < *(int*)(vdo + VdoElemCount)
                        ? $"count={*(int*)(el + it * 8 + 4)} byteOff={(long)(uint)*(int*)(el + it * 8) * 2}"
                        : "tables unavailable";
                    _log?.Info($"BatchService: capture-diag vdo=0x{(ulong)vdo:x} item={item} prim={prim} engineArgs size={size} offset={offset} vs table {tab}");
                }
            }
            return;
        }
        if (CasterPass.SkinObserve(vdo, (int)item, prim)) return;
        if (_pending < 0) return;

        int g = _pending;
        _pending = -1;                                            // one submit per trigger
        IntPtr gvdo = _gVdo[g];
        long elem = (long)*(IntPtr*)(gvdo + VdoElemItems);
        long vert = (long)*(IntPtr*)(gvdo + VdoVertItems);
        int nElem = *(int*)(gvdo + VdoElemCount), nVert = *(int*)(gvdo + VdoVertCount);
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

    /// <summary>First sighting of a part in a batchable context: qualify it, capture its (VDO, item, prim)
    /// through the funnel while its vanilla draw runs, then bind it to a group. Cold (once per part).</summary>
    private static void Record(IntPtr part)
    {
        IntPtr node = *(IntPtr*)(part + PartNode);
        IntPtr mat = *(IntPtr*)(part + PartMaterial);
        // phase 2: matrix-path parts (placeables) qualify too -- the model matrix joins the group key and
        // any later change (door opens, creature moves, billboard) permanently demotes to vanilla.
        // #134: BOTH halves of RenderDrawBucket's matrix condition, not just the flags. An instance is
        // transformed by SetModelMatrix(vp+0x18) exactly as a flagged part is, so calling one matrix-class
        // and not the other would draw every gob at the world origin.
        bool mx = node != IntPtr.Zero
                  && ((*(byte*)(node + NodeFlags) & 0x1e) != 0 || IsInstance(part));
        // #135: dangly parts are recorded like any other. The exclusion assumed the CPU re-solve invalidated
        // a (VDO, item) capture; it does not -- see IsDangly for where the solve runs and what it writes.
        bool shaped = node != IntPtr.Zero && mat != IntPtr.Zero
                  // #121: an env-mapped part is batchable and ownable. The owned lit stage feeds its ambient
                  // specular from the per-area IBL probe rather than from the material's env texture, so the
                  // texture never has to be bound and cannot split a batch.
                  // #129: bumpy-shiny is no longer a blanket refusal -- that flag routes the part to the
                  // WATER program (vswater/fswater), and the owned pass has one of those now. It stays a
                  // refusal everywhere ELSE: outside draw bucket 6 a bumpy-shiny material means the engine
                  // did NOT route it to water (g_nEnableBumpyWater off), and the owned pass has no answer for
                  // whatever it did route it to.
                  && (_hasBumpyShiny(mat) == 0 || _bucketId == OwnedDrawService.WaterBucket);
        // #126: opacity no longer decides whether a part is RECORDED, only whether this batcher may submit
        // it. The transparent classes are what the owned pass enrolls from, and it can only enroll what the
        // capture tables hold. Bucket 8 joins them by position rather than by opacity: the engine has already
        // folded the tile's fade factor into part+0xa0 by the time this runs, so a part fading from 1.0 would
        // otherwise read opaque on its first fading frame.
        // #129: ...and never water. A water part that happens to read opaque under GetIsOpaque would
        // otherwise mint a batch group in bucket 6, and this service would then multidraw-replay it INSIDE
        // the engine's own bucket-6 walk -- under fswater's per-part state, on exactly the fallback frames
        // where the owned pass has handed water back. Own-only instead: still captured, still enrollable,
        // and Vanilla on every frame the owned pass does not draw it.
        bool ok = shaped && _isOpaque(part) != 0 && _bucketId != AlphaBucket
                  && _bucketId != OwnedDrawService.WaterBucket;
        int e = Insert(part);
        if (e < 0) { Vanilla(part); return; }                // table full -> vanilla forever
        _pNode[e] = node; _pMat[e] = mat; _pMx[e] = mx; _pVdo[e] = IntPtr.Zero; _pSeen[e] = _frame;
        _pGob[e] = *(IntPtr*)(part + PartGob);
        _pSkin[e] = _inSkinDraw;
        if (_pGob[e] != IntPtr.Zero) _pInstRecorded++;
        // #137: ...and a skinmesh is own-only WHATEVER else is true of it. The doc says a skinned part is
        // never batch-submitted, and until this line that held only via the IsInstance branch below -- i.e.
        // on the assumption that every PartSkin hangs off a Gob. One that did not would pass probation, mint
        // a matrix-keyed group, and be replayed by glMultiDrawElementsBaseVertex under whatever pose
        // m_bones happened to hold: the bind-pose misrender this whole ticket exists to avoid.
        _pOwnOnly[e] = (shaped && !ok) || _pSkin[e];
        if (!shaped)
        {   // #134: which of the three terms, not just "one of them" -- the gob population dies here in bulk
            _recNotOk++;
            Die(e, node == IntPtr.Zero ? "no-node" : mat == IntPtr.Zero ? "no-material" : "bumpy-shiny-outside-water");
            Vanilla(part); return;
        }
        // #128: an INSTANCE skips probation outright. Probation exists so a matrix-keyed group cannot be
        // minted for a transform that will not hold still -- but an instance is never batch-submitted by this
        // service anyway (the `dirty` term in PartDraw refuses every one of them), so it has nothing to gain
        // from a matrix-keyed group and everything to lose from the death that follows failing probation.
        // Under probation a creature died on its second sighting ("probation-moved") and a door died the frame
        // it opened ("matrix-moved"), permanently -- which is what kept the whole dynamic-geometry class out
        // of ownership. Recorded as a MOVER instead: captured and grouped for the owned pass, own-only so this
        // service never touches it, and its transform read live at draw time, where it is always current.
        if (IsInstance(part)) { _pOwnOnly[e] = true; Complete(part, e); return; }
        if (mx)
        {
            // probation: a matrix-path part must show a bit-stable matrix on two sightings before it gets
            // a group -- else every flagged part that turns out to be a mover would mint a dead one-member
            // group (table spam)
            _pMxHash[e] = HashMatrix((float*)(node + NodeMatrix));
            _pState[e] = StProbe;
            Vanilla(part);
            return;
        }
        Complete(part, e);
    }

    private static delegate* unmanaged<IntPtr, void> _origPartDtor;
    /// <summary>#145: parts destroyed out from under a recorded entry, this session. The [batch] line prints
    /// it, and a count that stays 0 across an area transition means the hook is not firing -- which looks
    /// exactly like "fixed" from the outside, so it is the number to read.</summary>
    internal static long DtorRevokes { get; private set; }

    /// <summary>#145: <c>Part::~Part</c>. The part table is keyed by POINTER and every liveness test on it is
    /// an inference -- a sighting window, or an identity compare against fields a freed block can still be
    /// holding. That is how the owned pass came to walk a freed part into <c>GobAlphaMul</c> and chase
    /// <c>*(gob+0x68)</c> into a freelist word: the enrolled entry never learned its part had died.
    /// <para>The handler only COMPARES the dying pointer -- it never dereferences it -- and does its work in
    /// O(1) through the same hash the walk uses, because an area transition destroys parts by the thousand.
    /// Allocation-free for the same reason.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnPartDtor(IntPtr part)
    {
        // #150: unconditional, and NOT inside the Find branch below -- a sky part is deliberately routed away
        // from this table (OnPartDraw's SkyBox window returns early), so Find never sees one.
        Render.SkyBox.Revoke(part);
        int e = Find(part);
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

    /// <summary>Capture (VDO, item, prim) through the funnel during a vanilla draw, then bind to a group.</summary>
    private static void Complete(IntPtr part, int e)
    {
        IntPtr node = _pNode[e], mat = _pMat[e];
        _pState[e] = StCapturing;
        _capture = e;
        Vanilla(part);
        _capture = -1;
        if (_pState[e] == StDead || _pVdo[e] == IntPtr.Zero)
        { if (_pVdo[e] == IntPtr.Zero) _recNoCap++; else _recBadVdo++; Die(e, _pVdo[e] == IntPtr.Zero ? "no-capture" : "bad-vdo"); return; }

        int prim = _pPrim[e];
        int hint = _getRenderHint(part);
        // material content: texture slots (equality only -- CAurTexture* are borrowed, never deref'd),
        // shared-material override, and the blend-disable flag RenderFlat reads at mat+0x98
        IntPtr t0 = (IntPtr)_getTexture(mat, 0), t1 = (IntPtr)_getTexture(mat, 1),
               t2 = (IntPtr)_getTexture(mat, 2), t3 = (IntPtr)_getTexture(mat, 3);
        IntPtr shared = *(IntPtr*)(mat + MatShared);
        IntPtr env = *(IntPtr*)(mat + MatEnvMap);
        byte flag = *(byte*)(mat + MatBlendFlag);
        bool light = flag == 0;
        int rot = *(int*)(part + PartTexRotate);
        bool mx = _pMx[e];
        // #128: a MOVER's transform is not part of its group key -- it changes every frame, so keying on it
        // would mint one group per part per pose and exhaust the table. The flag itself IS in the key, which
        // is what keeps a mover out of a static group whose frozen matrix a replayed multidraw would apply.
        bool mover = IsInstance(part);
        float* liveM = (float*)(node + NodeMatrix);
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
            if (_groupCount >= MaxGroups || free < 0) { Die(e, "groups-full"); return; }
            g = _groupCount++;
            _gSlot[free] = g; _gHash[g] = h;
            _gVdo[g] = _pVdo[e]; _gTex0[g] = t0; _gTex1[g] = t1; _gTex2[g] = t2; _gTex3[g] = t3;
            _gShared[g] = shared; _gEnv[g] = env; _gFlag[g] = flag; _gRot[g] = rot; _gBucket[g] = _bucketId; _gPhase[g] = _phase;
            _gScene[g] = _bucketScene; _gWorld[g] = _bucketWorld;
            _gMx[g] = mx; _gMover[g] = mover;
            if (mx && !mover) for (int f = 0; f < 16; f++) _gMatrix[g * 16 + f] = liveM[f];
            _gHint[g] = hint; _gLight[g] = light; _gPrim[g] = prim; _gFirst[g] = -1; _gCount[g] = 0; _gSubmitGen[g] = -1;
        }
        _pTex0[e] = t0; _pRot[e] = rot;
        _pGroup[e] = g; _pNext[e] = _gFirst[g]; _gFirst[g] = e; _gCount[g]++;
        _pState[e] = StActive; _pDirty[e] = false; _pIncluded[e] = -1;
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
        // those reclaims nothing, drops every owned part back to the engine, and refills within the second
        // -- the 1Hz "the renderer keeps falling back to vanilla" collapse, which got worse the bigger the
        // area precisely because a bigger area reaches the cap sooner. So the diagnosis is MEASURED here
        // rather than asserted: one sweep of the state array, and only at pressure, so it costs nothing on
        // an ordinary frame.
        if (_partCount >= MaxParts - MaxParts / 4 || _groupCount >= MaxGroups - MaxGroups / 8)
            OnTablePressure();
        if (++_frame % LogEvery != 0) return;
        int members = 0, solo = 0;
        for (int i = 0; i < _groupCount; i++) { members += _gCount[i]; if (_gCount[i] < 2) solo++; }
        _log?.Info($"BatchService: [batch] {_groupCount} groups ({solo} solo) / {members} members ({_partCount} parts tracked), " +
                   $"batched {(float)_batched / LogEvery:F0} draws/frame in {(float)_calls / LogEvery:F1} calls, " +
                   $"iter-skipped {(float)BucketGate._iterSkip / LogEvery:F0} + late {(float)_skipped / LogEvery:F0}, " +
                   $"vanilla solo {(float)_vanSolo / LogEvery:F0} / " +
                   $"dirty {(float)_vanDirty / LogEvery:F0} / dead {(float)_vanDead / LogEvery:F0} / " +
                   $"own-only {(float)_vanOwnOnly / LogEvery:F0}, " +
                   (_saturated ? $"TABLE SATURATED ({_satLive} live / {_satDead} dead, surplus engine-drawn), " : "") +
                   (_submitTrunc > 0 ? $"SUBMIT TRUNCATED {_submitTrunc}x (a group exceeded {MaxSubmit} members -- geometry dropped), " : "") +
                   $"rec-dead notOk={_recNotOk} noCap={_recNoCap} badVdo={_recBadVdo}" +
                   // #145: parts the engine destroyed under a recorded entry. A 0 that survives an area
                   // transition means Part::~Part is not reaching us, which is indistinguishable from
                   // "nothing died" in every other number here.
                   $" dtor-revoked={DtorRevokes}" +
                   // #137: the skinmesh class, which had no row at all before it had a hook. A zero here is
                   // "the hook never fired", not "there were no cloaks"; forced/no-pose separate a class the
                   // owned pass took from one it could not solve.
                   (_origPartSkinDraw == null ? " | skinned: no hook"
                      : $" | skinned: {(float)_skinSeen / LogEvery:F1} draws/frame, " +
                        $"{(float)_skinForced / LogEvery:F1} poses forced ({_skinForceFailed} unsolved)"));
        _batched = 0; _calls = 0; _skipped = 0; BucketGate._iterSkip = 0; _vanSolo = 0; _vanDirty = 0; _vanDead = 0;
        _vanOwnOnly = 0; _skinSeen = 0; _skinForced = 0; _skinForceFailed = 0;
        _recNotOk = 0; _recNoCap = 0; _recBadVdo = 0; _submitTrunc = 0;
    }
}
