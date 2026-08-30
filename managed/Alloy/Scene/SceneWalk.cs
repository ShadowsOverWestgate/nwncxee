using System;
using System.Runtime.InteropServices;
using Sowx.Framework;
using static Sowx.Alloy.SceneLayout;
using static Sowx.Alloy.PartTable;

namespace Sowx.Alloy;

/// <summary>#256 (M5 strand 1 step 1): Alloy enumerates the world. The part table is filled by walking the
/// engine's authoritative containers -- <c>Scene+0x130</c> (every gob), each gob's three LOD part-tree roots
/// and its appended roots, and the tile BSP at <c>Scene+0x188</c> -- and NOT by watching the engine try to
/// draw something. Records therefore cover every part in the area rather than the visible slice, and a part
/// the camera has never faced is as recorded as one on screen.
/// <para><b>Maintained by re-reading, never by inferring</b> (ADR 0018). #253 §7.2 killed the notice-fed
/// registry: <c>Gob::SetLODModel</c> @0x43e6a0 rotates a gob's whole active part subtree every frame, per
/// visible gob, with no ctor, no dtor and no epoch bump, and two more edges have no notice at all. So the
/// three triggers here are (a) <c>Scene::Sequence</c> @0x18aa1b0, the engine's own global membership epoch,
/// (b) a Scene change, and (c) <c>Part::~Part</c>, which <see cref="DrawFunnelService"/> already hooks (#145,
/// #221). Everything else -- transforms, the active LOD root, material bindings -- is a per-record FIELD
/// READ, which is why the LOD rotation is a non-event: all three roots are enumerated, so whichever one the
/// engine swaps in is already in the table.</para>
/// <para><b>Every part, means every part CLASS.</b> The capture path only ever met a part that had arrived
/// through <c>PartTriMesh::Draw</c>, so it could assume one. The walk cannot: <c>Scene+0x130</c> and the BSP
/// reach <c>PartLight</c>, <c>PartEmitter</c>, <c>PartTrigger</c>, <c>PartProjection</c>,
/// <c>PartReference</c> and <c>PartDummy</c> as well, and their fields do not mean what a trimesh's mean at
/// the same offsets. <see cref="Visit"/> discriminates with the engine's own vtable slot 0x60
/// (<c>AsPartTriMesh</c>) before anything is recorded; children are still recursed through every class,
/// because a light or a dummy can parent geometry.</para>
/// <para><b>The bucket arrays are a validator, not a source.</b> <c>Scene+0x208</c> holds the finished,
/// classified, CULLED set; a 90 degree camera turn replaces most of it. Reading it as the record source
/// would build the delta machinery against a churning population (ADR 0018's "building it twice"). Since
/// #264 deleted the engine walk they are read for nothing else: <see cref="CrossCheck"/> asks them whether
/// this walk has a hole, and <see cref="_lastSample"/> counts the one class the cutover dropped.</para></summary>
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

    private static int* _sequence;
    private static int* _countPart;
    private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> _bspTraverse;
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

    /// <summary>The table was emptied or invalidated by something other than the scene. Re-enumerate.</summary>
    internal static void RequestWalk() { _walkPending = true; Dirty = true; }

    /// <summary>#227 / the repack edge: <paramref name="n"/> entries were just marked
    /// <see cref="PartTable.StStale"/> and need their draw arguments re-read. They are counted into the
    /// rebind queue here, because <see cref="Rebind()"/> skips the scan when the queue is empty and a repack
    /// changes no scene membership at all -- so nothing else would ever look at them again.</summary>
    internal static void NoteStale() { _staleQueued = true; Dirty = true; }
    /// <summary>A repack marked entries stale but named no indices. Rather than scan for them, let the next
    /// walk's <see cref="Sweep"/> rebuild the queue -- and ask for that walk, because a repack changes no
    /// scene membership and the epoch will not move for it.</summary>
    private static bool _staleQueued;

    // instrumentation -- the ticket owes a bump rate, and the escalation decision rides on it
    // Session totals (never reset) and per-period counters (zeroed by Period), kept apart on purpose:
    // printing a session total beside a per-LogEvery rate is how "walk=0,04ms" comes to mean a mean that
    // has not moved since an area load an hour ago.
    private static long _walks, _bumps, _spill, _overflow, _nonMesh;
    private static long _framesSinceLog, _bumpsSinceLog, _walkTicks, _walksSinceLog, _added, _swept, _bound;
    private static int _lastWalked, _lastRoots;

    internal static bool Resolve(ExtenderLog log)
    {
        _log = log;
        _sequence = (int*)Native.Sym(SymbolMap.SceneSequence);
        _countPart = (int*)Native.Sym(SymbolMap.CountPart);
        _bspTraverse = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Native.Sym(SymbolMap.BspTraverse);
        IntPtr sv = Native.Sym(SymbolMap.PartSkinVtbl);
        _skinVtbl = sv == IntPtr.Zero ? IntPtr.Zero : sv + 0x10;
        Ready = _sequence != null && _bspTraverse != null && _skinVtbl != IntPtr.Zero;
        if (!Ready)
            log.Info("SceneWalk: " + (_sequence == null ? SymbolMap.SceneSequence
                                    : _bspTraverse == null ? SymbolMap.BspTraverse : SymbolMap.PartSkinVtbl)
                     + " missing -- scene enumeration off, the renderer cannot arm");
        return Ready;
    }

    /// <summary>Once per world frame, at the frame anchor and before enrollment. One int load in the common
    /// case; a full re-walk when the epoch moved or the Scene changed. Engine thread, allocation-free.</summary>
    internal static void Tick(IntPtr scene)
    {
        if (!Ready || scene == IntPtr.Zero) return;
        _framesSinceLog++;
        int seq = *_sequence;
        bool bumped = _seqLatched && seq != _lastSeq;
        if (bumped) { _bumps++; _bumpsSinceLog++; }
        bool sceneChanged = scene != _scene;
        _lastSeq = seq; _seqLatched = true;
        if (sceneChanged) { _scene = scene; Reset(); }
        // A NEW Scene pays for all of it at once, deliberately. An area load is already a hitch -- the
        // player is on a loading screen -- and dripping the setup through the frames after it is what the
        // first live run showed as tiles arriving in front of you. The budget is for the steady state: a
        // spawned placeable, a door swapping its model, a gob whose LOD subtree rotated in.
        _setupLeft = sceneChanged ? int.MaxValue : SetupBudget;
        if (bumped || sceneChanged || _walkPending || _staleQueued)
        { _walkPending = false; _staleQueued = false; Walk(scene); }
        else Rebind();
        // A walk binds what it can as it goes; anything it left behind is drained by the queue right after,
        // while the budget still has room.
        if (_setupLeft > 0 && _unboundCount > 0) Rebind();
        // The bring-up validator, on the period cadence. The engine has already run ManageSceneBSP and
        // AddPartsToDrawBuckets by the time this anchor is reached, so Scene+0x208 holds THIS frame's
        // finished classification -- every part in it must have a record.
        // Its own counter, NOT _framesSinceLog: that one is reset by the period line, which is ticked from
        // a different (per-present, not per-world-frame) clock, so once it crossed the threshold the check
        // would run every frame until the two happened to line up.
        if (++_sinceCheck >= AlloyRenderer.LogEvery)
        { _sinceCheck = 0; _lastMiss = CrossCheck(scene + SceneDrawBuckets, out _lastSample); }
    }

    /// <summary>Scene -&gt; the embedded <c>DrawBucketManager</c> (0x310 bytes, constructed in place by
    /// <c>Scene::Scene</c> @0x4724f0; every <c>AddPartToBucket</c> call passes <c>this+0x208</c>).</summary>
    private const int SceneDrawBuckets = 0x208;
    private static int _lastMiss, _sinceCheck;

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
    /// <summary>#264: records whose <c>SetupArrays</c> came back with no drawable geometry. They are asked
    /// once and then left alone, so this is a POPULATION and not a rate -- it is how much of the enumerated
    /// world is permanently not geometry, and the number that says whether the walk is enumerating things it
    /// should not be reaching at all.</summary>
    private static long _noGeom;
    /// <summary>#264: parts the engine classified into the <c>sample_framebuffer</c> bucket at the last
    /// cross-check. That class is the one thing the walk's deletion dropped outright -- Alloy has no program
    /// for it, so nothing draws it now -- and this is the number that says whether the hole is three
    /// distortion effects or half an area. It must be read before anyone argues about how big it is.</summary>
    private static int _lastSample;

    /// <summary>A new Scene invalidates every record wholesale: the old Scene's parts are being torn down
    /// (<c>Scene::~Scene</c> @0x472e40 walks its gobs out one by one) and their addresses will be recycled.
    /// Cheaper and safer than letting the sweep discover it one entry at a time.</summary>
    private static void Reset() => WipeTables("scene changed -- re-enumerating");

    private static void Walk(IntPtr scene)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Gen++;
        _walks++; _walksSinceLog++;
        _lastWalked = 0; _lastRoots = 0;

        IntPtr* gobs = *(IntPtr**)(scene + SceneGobs);
        int nGobs = *(int*)(scene + SceneGobCount);
        if (gobs != null)
            for (int i = 0; i < nGobs; i++)
            {
                IntPtr gob = gobs[i];
                if (gob == IntPtr.Zero) continue;
                // All THREE LOD roots, which is the whole point (see GobLodRoot1): Gob::SetLODModel rotates
                // them with no notice of any kind, so enumerating only the active one would go stale the
                // moment the camera crossed an LOD radius.
                Tree(*(IntPtr*)(gob + GobPartTree));
                Tree(*(IntPtr*)(gob + GobLodRoot1));
                Tree(*(IntPtr*)(gob + GobLodRoot2));
                IntPtr* roots = *(IntPtr**)(gob + GobAppendRoots);
                int nRoots = *(int*)(gob + GobAppendCount);
                if (roots == null) continue;
                for (int r = 0; r < nRoots; r++) Tree(roots[r]);
            }

        // Tile geometry: SpawnPartsForTile @0x46352b passes gob = NULL, so it hangs off no gob and the loop
        // above cannot reach it. The unculled BSPTraverse visits every node whether the camera can see it or
        // not, which is exactly the difference between enumerating the area and enumerating the frame.
        IntPtr root = *(IntPtr*)(scene + SceneBspRoot);
        if (root != IntPtr.Zero)
            _bspTraverse(root, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnBspNode, IntPtr.Zero);

        Sweep();
        _walkTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    [UnmanagedCallersOnly]
    private static void OnBspNode(IntPtr node, IntPtr ctx)
    {
        IntPtr data = *(IntPtr*)(node + BspNodeData);
        if (data == IntPtr.Zero) return;
        IntPtr* items = *(IntPtr**)(data + BspDataTriMesh);
        int n = *(int*)(data + BspDataTriMeshCount);
        if (items == null) return;
        for (int i = 0; i < n; i++) Tree(items[i]);
    }

    /// <summary>Walk one part subtree, iteratively. Recursion is not an option here: this runs on the render
    /// thread with an unbounded tree depth in front of it.</summary>
    private static void Tree(IntPtr rootPart)
    {
        if (rootPart == IntPtr.Zero) return;
        _lastRoots++;
        int sp = 0;
        _stack[sp++] = rootPart;
        while (sp > 0)
        {
            IntPtr part = _stack[--sp];
            Visit(part);
            IntPtr* kids = *(IntPtr**)(part + PartChildren);
            int n = *(int*)(part + PartChildCount);
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

    private static void Visit(IntPtr walked)
    {
        _lastWalked++;
        // THE CLASS FILTER, and it is not optional. The walk reaches every Part in the scene -- PartLight,
        // PartEmitter, PartTrigger, PartProjection, PartReference, PartDummy, PartCamera -- while everything
        // downstream of a record assumes PartTriMesh. The capture path never had to ask, because a part only
        // reached it by arriving through PartTriMesh::Draw. Enumeration has no such guarantee, and the cost
        // of skipping the question is a SIGSEGV rather than a wrong pixel: PartTriMesh::GetIsOpaque @0x43cee0
        // reads *(this+0x50) as a Gob* and chases *(gob+0x68)+0x8e, and on a PartLight that offset holds the
        // light's float range (SceneLayout.LightRange).
        // Asked the engine's own way -- vtable slot 0x60 is AsPartTriMesh, which returns null for every other
        // class (SceneBSPAddPart @0x47ca10 discriminates with exactly this call, and BucketGate already
        // resolves every bucket item through it). The RETURNED pointer is the record key, because that is
        // what PartTriMesh::Draw receives as `this` and therefore what every Find() elsewhere looks up.
        IntPtr part = ((delegate* unmanaged<IntPtr, IntPtr>)
                       (*(IntPtr*)(*(IntPtr*)walked + BucketGate.PartVtblGetTriMesh)))(walked);
        if (part == IntPtr.Zero) { _nonMesh++; return; }
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
        if (_pState[e] == StActive) { var p = new PartRef(part); if (_pNode[e] != p.Node.Ptr || _pMat[e] != p.Material.Ptr) Rebind(e, p); }
        // The record was created before its geometry was resident, or a Repack moved its items. Either way
        // the fix is the same field read, and this is the frame it is retried on.
        if (_pState[e] == StUnbound || _pState[e] == StStale) TryBind(e);
    }

    /// <summary>Create a record for a part the walk has never seen. The field-read replacement for
    /// the old capture <c>Record</c>: every column it used to fill by observing a vanilla draw is a load
    /// off the part, the material or the VertexPrimitive.</summary>
    private static int Enrol(IntPtr part)
    {
        var p = new PartRef(part);
        int e = Insert(part);
        if (e < 0) return -1;
        IntPtr vp = p.Node.Ptr;
        IntPtr mat = p.Material.Ptr;
        _pNode[e] = vp; _pMat[e] = mat; _pVdo[e] = IntPtr.Zero;
        // The gob is read off the PART, never inferred from which root the walk arrived by: a reference
        // expansion under a gob's tree carries its own +0x50 (CreateInstanceTreeR @0x43c6b0 is its sole
        // writer), and SpawnPartsForTile @0x46352b leaves it null outright.
        _pGob[e] = p.Gob.Ptr;
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
        _pState[e] = StUnbound;
        if (!TryBind(e) && _pState[e] == StUnbound && !_pSetupTried[e]) Enqueue(e);
        return e;
    }

    /// <summary>Read the draw arguments off the VertexPrimitive and intern the group. Null VDO or item -1 is
    /// not a failure -- <c>VertexPrimitiveFlat::SetupArrays</c> @0x458920 has not run for this part yet, so
    /// the record simply stays unbound and is retried by <see cref="Rebind"/>. See
    /// <see cref="SceneLayout.VpVdo"/> for why this is a field read at all.</summary>
    private static bool TryBind(int e)
    {
        IntPtr part = _pPart[e], vp = _pNode[e];
        if (vp == IntPtr.Zero) return false;
        IntPtr vdo = *(IntPtr*)(vp + VpVdo);
        int item = *(int*)(vp + VpItem);
        // #264: RESIDENCY IS ALLOY'S TO DRIVE NOW. These two fields are filled by
        // VertexPrimitive::SetupArrays (vtable 0x38), and until #264 the only thing that ever called it was
        // PartTriMesh::Draw -- i.e. the engine bucket walk. With the walk deleted nothing ran it, every
        // record stayed StUnbound, enrollment saw an empty candidate set and the world went black. So the
        // walk makes the call itself, at the same frame anchor the engine's draw used to run from.
        if (vdo == IntPtr.Zero || item < 0)
        {
            // ONCE per record, and the flag is why. SetupArrays latches its own verdict, so a primitive that
            // comes back with item -1 has no drawable geometry and will answer the same for ever. The first
            // live run re-asked every frame and spent 512 calls a frame on 1899 tiles that never bound --
            // which is a rendering system doing nothing, expensively, in perpetuity.
            if (_pSetupTried[e] || _setupLeft <= 0) { _pState[e] = StUnbound; return false; }
            _pSetupTried[e] = true;
            _setupLeft--; _setups++;
            ((delegate* unmanaged<IntPtr, void>)(*(IntPtr*)(*(IntPtr*)vp + VpVtblSetupArrays)))(vp);
            vdo = *(IntPtr*)(vp + VpVdo);
            item = *(int*)(vp + VpItem);
            if (vdo == IntPtr.Zero || item < 0) { _pState[e] = StUnbound; _noGeom++; return false; }
        }
        // Only mode 1 (indexed static) can be drawn from our own tables; the capture killed the rest as
        // "multidraw-or-unindexed" and that verdict is unchanged, it is just reached without a draw.
        if (new VdoRef(vdo).Mode != 1) { Release(e, "unindexed"); return false; }
        IntPtr mdl = *(IntPtr*)(part + AlloyRenderer.PartMdlNode);
        if (mdl == IntPtr.Zero) { Release(e, "no-mdlnode"); return false; }
        _pVdo[e] = vdo; _pItem[e] = item; _pPrim[e] = *(int*)(mdl + MdlPrimType);
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
        _pState[e] = StActive;
        _bound++; Dirty = true;
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
        // #264: a new VertexPrimitive has its own setup state, so the latched "nothing here" verdict does
        // not carry across the swap -- clearing it is what lets a re-modelled part bind at all.
        // #284: and a new MdlNode is a new bind-array question, so the pose attempt clears with it.
        _pSetupTried[e] = false; _pPoseTried[e] = false;
        _pState[e] = _pNode[e] == IntPtr.Zero || _pMat[e] == IntPtr.Zero ? Die(e, "no-node") : StUnbound;
        if (_pState[e] == StUnbound) Enqueue(e);
        Dirty = true;
    }

    /// <summary>Per frame, for the records the last walk could not bind. O(unbound), which is zero in a
    /// settled area -- the alternative is waiting for an epoch bump that a purely resident-state change
    /// never produces.
    /// <para>ponytail: a full table scan rather than a free list of unbound slots. The <c>unbound=</c>
    /// number on the period line is what says whether the list is worth keeping.</para></summary>
    private static void Rebind()
    {
        int left = 0;
        for (int i = 0; i < _unboundCount; i++)
        {
            int e = _unbound[i];
            byte st = _pState[e];
            if (st != StUnbound && st != StStale) continue;   // bound, released or swept since
            if (_pWalkGen[e] != Gen) continue;                // not in the scene any more
            // ...and a record whose setup was tried and produced nothing does NOT go back in the queue.
            // It is not waiting for anything: nothing else in the frame will make its primitive drawable,
            // and re-queueing it is what made the drain never drain.
            if (!TryBind(e) && _pState[e] == StUnbound && !_pSetupTried[e]) _unbound[left++] = e;
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
    /// #256 asked for; making the 1330 bind at all is strand 1 step 5's (residency: geometry).</para></summary>
    private static readonly int[] _unbound = new int[MaxParts];
    private static int _unboundCount;

    /// <summary>Queue an entry for binding. Only on a transition INTO the unbound states, so an entry
    /// already waiting is not enqueued twice; <see cref="Sweep"/>'s rebuild is the backstop.</summary>
    private static void Enqueue(int e)
    {
        if (_unboundCount < MaxParts) _unbound[_unboundCount++] = e;
    }

    /// <summary>Mark-and-sweep: an entry the walk did not reach is no longer in the scene. This is the edge
    /// <c>Part::~Part</c> cannot cover -- <c>Scene::ChildRemove</c> @0x462cc0 and <c>Gob::Amputate</c>
    /// @0x443fa0 UNLINK without destroying, so the parts are alive and simply not ours any more.
    /// <para>ponytail: O(MaxParts) per walk, not O(what changed). Enrollment already scans the whole table,
    /// so this is not a new order of cost -- but it is the one place #256's "O(what changed)" is bought with
    /// a constant rather than earned. A dense live-index list is the upgrade; the walk timing on the period
    /// line is what says whether it is needed.</para></summary>
    /// <summary>Kill a record AND release its slot. The distinction from a bare <c>Die</c> is the whole
    /// difference between "not ours this walk" and "not ours ever": a plain <c>Die</c> leaves
    /// <c>_pPart[e]</c> set, so <see cref="PartTable.Find"/> keeps matching the pointer and no later walk
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
        Die(e, why);
        _pFreed[e] = true;
        Dirty = true;
    }

    private static void Sweep()
    {
        _unboundCount = 0;
        for (int e = 0; e < MaxParts; e++)
        {
            byte st = _pState[e];
            if (st == StEmpty) continue;
            if (_pWalkGen[e] == Gen)
            {
                if (st == StUnbound || st == StStale) Enqueue(e);
                continue;
            }
            if (st != StDead) { Release(e, "unreachable"); _swept++; }
        }
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
    /// frame, through <see cref="PartTable.TileFadeFactor"/>, which is where it is always current.</para>
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
        for (int e = 0; e < MaxParts; e++)
        {
            byte st = _pState[e];
            if (st == StActive) { active++; continue; }
            if (st != StUnbound && st != StStale) continue;
            if (_pGob[e] != IntPtr.Zero) unboundGob++; else unboundTile++;
        }
        string s = $"[enum] walks={_walks} ({_walksSinceLog} this period) epoch moved on {_bumps} frames ({perFrame:0.000}/frame) walk={ms:0.00}ms "
                 + $"roots={_lastRoots} parts walked={_lastWalked} records={_partCount} active={active} "
                 // The unbound residue, split by whether the record hangs off a Gob. This is step 5's
                 // starting number and the two halves want different answers: a GOB record that never binds
                 // is most likely an inactive LOD alternate (the walk enumerates all three roots, the engine
                 // draws one), which is correct to hold and will bind if the LOD swaps; a TILE record that
                 // never binds is world geometry the camera has not reached, which is the residency gap.
                 + $"unbound={_unboundCount} (gob={unboundGob} tile={unboundTile}) "
                 + $"+{_added} added/-{_swept} swept/{_bound} bound/{_setups} set-up this period "
                 + $"no-geometry={_noGeom} "
                 + $"non-mesh={_nonMesh} table-full={_spill} stack-overflow={_overflow} "
                 // The two validators. countpart is an UPPER bound, never an equality: it counts every Part
                 // in every scene, GUI 3D panels included. bucket-miss is the sharp one -- it must be 0.
                 + $"countpart={cp} bucket-miss={_lastMiss} sample-class={_lastSample}";
        _bumpsSinceLog = 0; _framesSinceLog = 0;
        _walksSinceLog = 0; _walkTicks = 0; _added = 0; _swept = 0; _bound = 0; _setups = 0;
        return s;
    }

    /// <summary>Bring-up validator (ADR 0018 demotes the bucket arrays to exactly this): every part the
    /// engine classified into <c>Scene+0x208</c> this frame must have a record. A miss means the walk did
    /// not reach something the engine is drawing, which is the failure this ticket has to be able to see.
    /// Returns the number of misses.</summary>
    internal static int CrossCheck(IntPtr mgr, out int sample)
    {
        int miss = 0; sample = 0;
        for (int id = 0; id < BucketTypes; id++)
            for (int phase = 0; phase < BucketGate.BucketPhases; phase++)
            {
                IntPtr bucket = mgr + (id * BucketGate.BucketPhases + phase) * BucketGate.BucketStride;
                IntPtr* items = *(IntPtr**)bucket;
                int count = *(int*)(bucket + BucketGate.BucketCount);
                if (items == null) continue;
                for (int i = 0; i < count; i++)
                {
                    IntPtr part = items[i];
                    if (part == IntPtr.Zero) continue;
                    IntPtr tri = ((delegate* unmanaged<IntPtr, IntPtr>)
                                  (*(IntPtr*)(*(IntPtr*)part + BucketGate.PartVtblGetTriMesh)))(part);
                    if (tri != IntPtr.Zero && Find(tri) < 0) miss++;
                    if (id == SampleBucket) sample++;
                }
            }
        return miss;
    }

    /// <summary>The <c>sample_framebuffer</c> item type -- the class whose engine draw READS the frame
    /// buffer copy the #215 cutover removed, and which no Alloy program covers. Counted, not drawn.</summary>
    private const int SampleBucket = 5;

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
            int savedCount = _partCount;
            _pState[ea] = StActive; _pState[eb] = StActive;
            int gen = ++Gen;
            _pWalkGen[ea] = gen;                       // a is reachable, b is not
            Sweep();
            bool ok = _pState[ea] == StActive;
            // ...and b is not merely dead: its SLOT is released, which is what lets the part come back.
            ok &= _pState[eb] == StDead && _pDeath[eb] == "unreachable" && _pFreed[eb];
            ok &= Find(b) < 0 && Insert(b) == eb && !_pFreed[eb];
            // ...and #221's tombstone still governs reuse for a genuinely destroyed part.
            _pFreed[ea] = true;
            ok &= Find(a) < 0;
            ok &= Insert(a) == ea && !_pFreed[ea];
            _pState[ea] = StEmpty; _pState[eb] = StEmpty;
            _pPart[ea] = IntPtr.Zero; _pPart[eb] = IntPtr.Zero;
            _pWalkGen[ea] = 0; _pWalkGen[eb] = 0;
            _pDeath[eb] = null!;
            _partCount = savedCount;                   // the two synthetic slots are not real tenants
            Dirty = false;
            log.Info(ok ? "SceneWalk.SelfCheck: passed"
                        : "SceneWalk.SelfCheck: FAILED -- the mark-and-sweep contract drifted");
        }
        finally { Marshal.FreeHGlobal(blk); }
    }
}
