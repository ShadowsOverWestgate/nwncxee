using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Alloy.SceneLayout;
using static Sowx.Alloy.BucketGate;
namespace Sowx.Alloy;

/// <summary>#188: the part table -- every recorded part, its parallel columns, the group registry they key
/// into, and the identity/lifetime machinery (record slots, sightings, death, wipe, pressure) that the
/// census, the bucket gate and the Alloy pass all ride on. One struct-of-arrays module rather than three,
/// for #173's reason: the hot walks read these arrays directly and a real boundary here costs a copy or an
/// indirection per part per frame.
/// <para>It also owns the engine reads those arrays are filled from -- the <c>Material</c>/<c>Part</c>
/// accessors resolved once at <see cref="Resolve"/> -- because all three consumers call them.</para>
/// <para>Members are <c>internal</c> and reached by the other three modules through
/// <c>using static</c>, which is a compile-time alias: a cross-module read of a column is the same
/// instruction as the same-class read it replaced.</para>
/// <para>What is NOT here: who WRITES the columns. Since #256 that is <see cref="SceneWalk"/>, which
/// enumerates the world and fills a record from field reads; <see cref="DrawFunnelService"/> keeps only the
/// hook bodies (the death notice) that have to sit beside their trampolines.</para>
/// </summary>
internal static unsafe class PartTable
{
    /// <summary>The per-item-alpha bucket. The census owns it (#126).</summary>
    internal const int AlphaBucket = 8;

    /// <summary>Presents since boot, ticked by <see cref="DrawFunnelService.EndFrame"/>. The census's settle
    /// clock and the wipe stamp are counted in it. #264 took the sighting stamps and their staleness windows:
    /// liveness is <see cref="SceneWalk"/>'s mark-and-sweep and visibility is <see cref="CullPass"/>'s.</summary>
    internal static int _frame;

    /// <summary>Whoever logs about the table (the pressure decision and the wipe). Named rather than
    /// <c>_log</c> so a module that imports this one and keeps its own log cannot silence the wrong one.</summary>
    internal static ExtenderLog? Log;
    /// <summary>Tile-fade state and factor (the TileData* hangs off <see cref="PartTile"/>). #124 corrected
    /// both: the factor is at +0x70, NOT +0x8c below it -- <c>TileData::TileData</c> @0x43f050 inits three
    /// separate floats to 1.0 and the original RE picked the wrong one, so this test was permanently false.
    /// <c>ProcessTriMeshParts</c> @0x480d40 is the reader: +0x70 is this frame's factor, +0x74 the pending one
    /// <c>HandleFadingIn/OutBucket</c> step and the top of <c>ManageSceneBSP</c> @0x47fa80 commits (#330;
    /// <c>Scene::ResetTileFading</c> is a dead export), and +0xc0 is the state enum.
    /// <para>A tile that is fading leaves the opaque class outright -- ProcessTriMeshParts routes it into
    /// <c>fadebuckets</c>, and <c>Scene::AddPartsToDrawBuckets</c> @0x4751d0 puts those in bucket 8. Since
    /// #126 bucket 8 is suppressible and the fade is a per-frame overlay on the Alloy blended class
    /// (<see cref="TileFadeFactor"/>).</para></summary>
    internal const int TileFade = 0x70;

    /// <summary>Recorded-part capacity: the reason the renderer can hold a whole object-dense area rather
    /// than the first 4096 parts of one. A cap that cannot hold the area is a rendering fault, not a saving.
    /// <para>#258 deleted the group registry that sat beside it -- 262144 groups over 23 arrays at 181
    /// bytes each, 45,3 MiB, for a batcher that could not submit under the #215 cutover. The part columns
    /// are now the whole of what a record costs, and they went from 13 bytes of batcher state per entry
    /// (group id, member link, submit generation, dirty) to 14 bytes of scene record (bucket, Scene, world,
    /// lit) -- +0,25 MiB against the 45,3 MiB, 1 MiB of submit arrays and 15,2 MiB of dedup registry that
    /// went with it.</para></summary>
    internal const int MaxPartsBits = 18, MaxParts = 1 << MaxPartsBits;


    /// <summary>#330: <c>SuppressGCTransition</c> -- the callee is ~60 instructions of pure field reads
    /// (decompiled @0x4a6f70: three camera fields, three part fields, <c>BoxAbovePlane</c>) and it is asked
    /// once per lit part per frame, so the GC transition around a plain <c>calli</c> was a real share of the
    /// Scan bracket. Legal because it never blocks, never calls back and never throws.</summary>
    internal static delegate* unmanaged[SuppressGCTransition]<IntPtr, IntPtr, byte> _keyhole;
    internal static IntPtr _camSlot;   // &CurrentCamera; live CAurCamera* is *_camSlot
    /// <summary>The live <c>CAurCamera*</c>, or zero before the symbol resolved or while no camera renders.</summary>
    internal static IntPtr CurrentCamera => _camSlot == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_camSlot;
    /// <summary>#330: every camera field <c>Camera::GetCanKeyholeDissolve</c> @0x4a6f70 reads -- the enable
    /// byte at +0xe50, the aperture plane at +0xe54..+0xe60 and the height cut at +0xe64 -- as one 24-byte
    /// span (the 3 bytes of padding after the enable byte ride along; comparing more is only more
    /// conservative). <c>Camera::RenderScene</c> @0x4a7920 is their only writer, every frame, ahead of the
    /// FadeDoors anchor, so a snapshot compare at the anchor is a camera-dirty bit with no hook.</summary>
    internal const int CamKeyholeState = 0xe50, CamKeyholeWords = 3;

    internal static delegate* unmanaged<IntPtr, byte> _isOpaque, _hasEnvMap, _hasBumpyShiny;
    internal static delegate* unmanaged<IntPtr, int, long> _getTexture;
    internal static uint* _primTable;
    /// <summary><c>g_nEnableBumpyWater</c>: null (Windows, or a build without the symbol) leaves the
    /// bumpy-shiny route to water unrecognised, so only .mtr-declared water is owned. Never a misrender:
    /// an unrecognised water part stays the engine's.</summary>
    internal static int* _bumpyWater;
    /// <summary><c>_ZTV14PartDanglyMesh</c> + 0x10 -- the value a PartDanglyMesh's vtable slot holds (the
    /// Itanium ABI puts the function pointers two words in).</summary>
    internal static IntPtr _danglyVtbl;

    /// <summary>Resolve every engine read the three modules make off a part, a node or a material. One
    /// place, because all three call them and none may resolve a symbol of its own. False = a required one
    /// is missing (headless, or Windows), and nothing above may arm.</summary>
    internal static bool Resolve()
    {
        _isOpaque = (delegate* unmanaged<IntPtr, byte>)Native.Sym(SymbolMap.PartGetIsOpaque);
        _hasEnvMap = (delegate* unmanaged<IntPtr, byte>)Native.Sym(SymbolMap.MatHasEnvMap);
        _hasBumpyShiny = (delegate* unmanaged<IntPtr, byte>)Native.Sym(SymbolMap.MatHasBumpyShinyMap);
        IntPtr dv = Native.Sym(SymbolMap.PartDanglyMeshVtbl);
        _danglyVtbl = dv == IntPtr.Zero ? IntPtr.Zero : dv + 0x10;
        _getTexture = (delegate* unmanaged<IntPtr, int, long>)Native.Sym(SymbolMap.MatGetTexture);
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _bumpyWater = (int*)Native.Sym(SymbolMap.EnableBumpyWater);
        _keyhole = (delegate* unmanaged[SuppressGCTransition]<IntPtr, IntPtr, byte>)Native.Sym(SymbolMap.GetCanKeyholeDissolve);
        _camSlot = Native.Sym(SymbolMap.CurrentCamera);
        return _isOpaque != null && _hasEnvMap != null && _hasBumpyShiny != null && _getTexture != null && _primTable != null;
    }

    // part entries: open-address hash on part pointer, parallel arrays, no allocation
    //
    // #194: the layout is struct-of-arrays. #258 deleted the per-part column reader that made the question
    // interesting (and /batchtime, which measured it); what is left touches a handful of columns per part
    // per frame. Convert to PartEntry[] only against a measurement, never against the argument.
    /// <summary>#256: recorded by the scene walk, but its <c>VertexPrimitive</c> has no VDO yet --
    /// <c>VertexPrimitiveFlat::SetupArrays</c> @0x458920 has not run for this part. Not an error and not a
    /// death: enumeration reaches a part before its geometry is resident, so the record waits and the field
    /// is re-read (<c>SceneWalk.Rebind</c>) rather than the triple being invented.</summary>
    internal const byte StEmpty = 0, StActive = 2, StDead = 3, StStale = 4, StUnbound = 5;
    internal static readonly IntPtr[] _pPart = new IntPtr[MaxParts];
    internal static readonly IntPtr[] _pNode = new IntPtr[MaxParts];
    internal static readonly IntPtr[] _pMat = new IntPtr[MaxParts];
    internal static readonly IntPtr[] _pVdo = new IntPtr[MaxParts];
    internal static readonly IntPtr[] _pTex0 = new IntPtr[MaxParts];   // diffuse slot at record (animation guard)
    internal static readonly int[] _pPrim = new int[MaxParts];         // prim type captured at the funnel
    internal static readonly int[] _pRot = new int[MaxParts];          // texture-rotation bits at record
    internal static readonly bool[] _pMx = new bool[MaxParts];         // matrix-path part (phase 2)
    internal static readonly int[] _pItem = new int[MaxParts];
    internal static readonly byte[] _pState = new byte[MaxParts];
    /// <summary>#256: the <see cref="SceneWalk.Gen"/> that last REACHED this entry from the scene. Not a
    /// sighting and not a visibility stamp -- it is the mark half of the walk's mark-and-sweep, and it is
    /// what reclaims a part that stopped being in the scene without being destroyed
    /// (<c>Scene::ChildRemove</c> @0x462cc0 and <c>Gob::Amputate</c> @0x443fa0 both unlink without
    /// freeing, so <c>Part::~Part</c> never fires for them).</summary>
    internal static readonly int[] _pWalkGen = new int[MaxParts];
    /// <summary>#126: an Alloy-ONLY class -- a transparent, additive, cap or fading part, or one that hangs
    /// off a gob. Latched at bind from the classification, and since #258 no gate reads it: the batcher it
    /// excluded the part from is gone. It stays because it is the one place the record says WHY a part is
    /// the Alloy pass's alone, which is what the <c>dumpframe</c> row reports.</summary>
    internal static readonly bool[] _pOwnOnly = new bool[MaxParts];
    /// <summary>#134: which gate killed this entry, for the census. There are ten ways to reach StDead and
    /// the aggregate counters name three of them, which is how 5319 gob draws a frame went unexplained.</summary>
    internal static readonly string[] _pDeath = new string[MaxParts];
    /// <summary>#221: the part this entry recorded has been destroyed (<c>Part::~Part</c>). The entry keeps
    /// its slot -- returning one to <see cref="StEmpty"/> would cut the probe chains that run through it --
    /// but its ADDRESS no longer means anything, so the next part the allocator hands back at it must
    /// re-record instead of landing on the tombstone. Without this a recycled pointer was unownable for the
    /// life of the table: <see cref="Find"/> matched it, the draw took the not-active branch, and the part
    /// drew vanilla forever (the character losing Alloy ownership after a few area transitions, which only
    /// an area transition healed).</summary>
    internal static readonly bool[] _pFreed = new bool[MaxParts];

    /// <summary>#264: has <see cref="SceneWalk"/> already asked this record's VertexPrimitive to set its
    /// arrays up? The call is what fills the VDO/item fields, and it LATCHES its own verdict -- a primitive
    /// whose MdlNode carries no drawable geometry comes back with item -1 and marks itself done, so a second
    /// call can only ever return the same answer. Without this flag the drain queue re-asks every such record
    /// every frame: the first live run burned 512 calls a frame, for ever, on 1899 tiles that were never
    /// going to bind.
    /// <para>#317: but "marks itself done" was read off the comment, not the binary, and the binary says
    /// otherwise. <c>VertexPrimitiveFlat::SetupArrays</c> @0x458920 takes its no-geometry exit when the
    /// vertex or index totals summed off the <c>MdlNodeTriMesh</c> list are zero, and that exit leaves
    /// <see cref="SceneLayout.VpVdo"/> NULL -- so the function's own entry guard
    /// (<c>vp+0x64 &amp;&amp; vp+0x58 &amp;&amp; !vp+0x84</c>) fails on the next call and the ENGINE redoes
    /// the work. Only Alloy latched. A part whose model geometry is not resident yet therefore answered
    /// zero once and stayed invisible for the session, while vanilla re-asked and drew it -- and the one
    /// attempt was spent at the worst possible moment, because a scene change lifts the budget
    /// (<c>_setupLeft = int.MaxValue</c>) and drains the whole queue on the load frame.</para>
    /// <para>So it is an attempt COUNT, not a flag. The ceiling the original latch bought is kept -- the
    /// 1899 permanently empty tiles cost <see cref="SetupAttempts"/> calls each, not 512 a frame for ever
    /// -- and a record still empty after that is taken at its word.
    /// ponytail: a fixed attempt count on a wait, not a signal that the model became resident. The exact
    /// condition is "the MdlNodeTriMesh totals changed since last ask", which is most of SetupArrays' own
    /// prefix re-walked per record; do that if streamed content ever outlives the window.</para>
    /// <para>Cleared wherever the primitive underneath may have changed -- a slot reclaimed by
    /// <see cref="Insert"/>, and a record whose node or material moved (<c>SceneWalk.Rebind</c>).</para></summary>
    internal static readonly byte[] _pSetupTries = new byte[MaxParts];
    /// <summary>#317: the walk generation each record last spent an attempt in, so the retries are spaced
    /// over the load rather than burned in one frame.</summary>
    internal static readonly int[] _pSetupGen = new int[MaxParts];
    /// <summary>#317: how many <c>SetupArrays</c> calls one record is worth, and how many walk generations
    /// it waits between them. The walk runs about once every ten frames, so eight attempts four generations
    /// apart is roughly half a minute of load -- long enough for NWSync to land content the first attempt
    /// missed, and a real ceiling rather than a timer that never terminates.</summary>
    internal const int SetupAttempts = 8, SetupRearmGens = 4;
    /// <summary>#284: has <see cref="SkinPose"/> already spent this record's one <c>PartSkin::PostProcess</c>
    /// @0x483120 on it? Same shape and same reason as <see cref="_pSetupTries"/> -- the engine call latches
    /// its own verdict (it deletes the temporary arrays its entry guard tests), so a second call can only
    /// return immediately, and a node that comes back still unprepared will come back unprepared for ever.
    /// <para>A record column rather than a set of part pointers on purpose: it clears exactly where the part
    /// underneath can have changed -- a slot reclaimed by <see cref="Insert"/>, a re-bound record
    /// (<c>SceneWalk.Rebind</c>) and a table wipe -- so a recycled address is never denied its attempt.</para></summary>
    internal static readonly bool[] _pPoseTried = new bool[MaxParts];
    internal static int _partCount;
    /// <summary>#221: freed slots re-recorded into. Reported by the [batch] line, because "the table is
    /// reclaiming recycled addresses" and "no part ever dies" read the same in every other counter.</summary>
    internal static int _reclaims;


    /// <summary>#137: this entry was recorded through <c>PartSkin::Draw</c>, so it is a TRUE skinmesh --
    /// posed by the engine's 64-matrix bone array rather than by its node matrix alone. The Alloy pass needs
    /// this to know it must force a pose and bind bones; nothing else in this service treats it specially,
    /// because a skinned part is Alloy-only and never batch-submitted.</summary>
    internal static readonly bool[] _pSkin = new bool[MaxParts];
    /// <summary>#128: the Gob this part instances off, copied at <see cref="Record"/> while the part is
    /// certainly live. The all-owned-or-none sweep needs the gob of parts that DIED before enrollment ever
    /// saw them -- a multi-draw creature part dies at Complete as "multidraw-or-unindexed" -- and a dead
    /// entry's part pointer must never be dereferenced to get it.</summary>
    internal static readonly IntPtr[] _pGob = new IntPtr[MaxParts];
    /// <summary>#336: the root Part* of the tree the walk found this part under, and whether that tree is
    /// one of a gob's THREE LOD roots with an alternate present. A part's tree never changes; which of the
    /// three slots holds it does (<c>Gob::SetLODModel</c>), and only the tree in <c>GobPartTree</c> is the
    /// one the engine draws. Without this every LOD level of such a gob drew at once.</summary>
    internal static readonly IntPtr[] _pRoot = new IntPtr[MaxParts];
    internal static readonly bool[] _pLodAlt = new bool[MaxParts];
    /// <summary>#330: the walk reached this part through the BSP's tri-mesh lists, not a gob tree -- the
    /// mesh-bucket loop of <c>Scene::AddPartsToDrawBuckets</c> @0x4751d0 rewrites its matrix (when its node
    /// flags or a gob say so) whether or not any gob is in view.</summary>
    internal static readonly bool[] _pBsp = new bool[MaxParts];

    /// <summary>Bumped on every table wipe: entry indices held elsewhere are stale from that moment.</summary>
    internal static int TableGen;
    /// <summary>Ownership-enrollment scan bound (the part table is open-addressed, so it is scanned whole).</summary>
    internal static int EntryCount => MaxParts;
    /// <summary>Are the CAPTURE hooks in? False = no tables, so the census has nothing to classify, the gate
    /// nothing to suppress and the Alloy world pass nothing to enroll from -- which it reports rather than
    /// sitting at zero silently, because <c>drawhooks=off</c> otherwise disables the Alloy pass too.
    /// <para>#188: this is deliberately NOT "some pass is armed" -- a pass that stands down must not take
    /// the table, the census and the gate with it.</para></summary>
    internal static bool Active;

    /// <summary>Armed AND the frame latched as Alloy's (#86, and #216's whole verdict). The two hot funnels
    /// read this, so a vanilla frame
    /// records nothing, batches nothing and suppresses nothing -- every part takes the engine's own draw.
    /// Bookkeeping (EndFrame, the period log, enrollment candidacy) stays on <see cref="Active"/> so the
    /// counters still report while vanilla, which is what proves they read zero.</summary>
    internal static bool Live => Active && AlloyGate.Alloy;

    /// <summary>#258: what the group registry carried as incidental metadata, now a column of the scene
    /// record itself. The bucket <see cref="SceneWalk.Classify"/> derived, the Scene the walk read the part
    /// from and whether that Scene was the world's (#91 -- a GUI 3D panel's part must never enter the world
    /// table), and whether the material lights (blend flag 0). Nothing else the group key held is read by
    /// anyone: the key existed to decide who could share a multidraw, and there is no multidraw.</summary>
    internal static readonly int[] _pBucket = new int[MaxParts];
    internal static readonly IntPtr[] _pScene = new IntPtr[MaxParts];
    internal static readonly bool[] _pWorld = new bool[MaxParts];
    internal static readonly bool[] _pLight = new bool[MaxParts];

    /// <summary>Is this part an INSTANCE -- does it hang off a <c>Gob</c>? #134: this used to be spelled
    /// <c>IsSkinned</c> and read <c>*(*(node+0x10)+0x50)</c>, which is the same value by a longer route
    /// (part+0x88 is the VertexPrimitive, its +0x10 points back at the part, and part+0x50 is
    /// <see cref="PartGob"/>) -- but it is not a skin test and never was.
    /// <para>Skinned parts cannot reach this hook at all: <c>PartSkin::Draw</c> @0x4830b0 calls
    /// <c>RenderSkinned</c> directly and never enters <c>PartTriMesh::Draw</c>. What the test actually
    /// separates is the second half of <c>RenderDrawBucket</c>'s "does this need SetModelMatrix" condition,
    /// whose first half is the <see cref="NodeFlags"/> <c>&amp; 0x1e</c> beside it. Under the wrong name it
    /// kept every instanced part -- placeables, doors, tileset objects, 5295 draws of 6000 in a warm frame --
    /// out of ownership for the whole of M1 through M3.</para></summary>
    /// <summary>A <c>PartDanglyMesh</c>, which reaches this hook because <c>PartDanglyMesh::Draw</c> @0x451b60
    /// calls <c>PartTriMesh::Draw</c> outright. Kept as an identity for the census and for #135's per-frame
    /// re-harvest; no longer a reason to refuse the part.
    /// <para>#135: the CPU re-solve is real but it does not land where the old exclusion assumed. The solve
    /// is <c>PartDanglyMesh::Animate(float)</c> @0x49bbb0 -- a separate per-frame pass, NOT the draw -- so
    /// suppressing the draw does not freeze the sway. And it writes per-part into that part's own VDO item:
    /// <c>GetItemDynamicData(vp+0x58, vp+0x60)</c>, which resolves to the DYNAMIC vertex pool (vdo+0x30,
    /// stride vdo+0x38) through the same item-offset table at vdo+0x48 the static pool uses. The harvest
    /// reads the engine's own VAO, so it inherits whichever pool the engine bound, and baseVertex addressing
    /// is identical for both. Tested by vtable, which is exact -- the class has no flag to read.</para></summary>
    internal static bool IsDangly(IntPtr part)
    {
        if (_danglyVtbl == IntPtr.Zero || part == IntPtr.Zero) return false;
        return new PartRef(part).Vtbl == _danglyVtbl;
    }

    internal static bool IsInstance(IntPtr part)
    {
        return new PartRef(part).IsInstance;
    }

    /// <summary>#145: is this entry still a live recording of <paramref name="part"/>? Pointer identity is
    /// not enough on its own (a recycled allocation lands at the same address), and neither is the state on
    /// its own (an entry keeps its slot when it dies) -- the pair is what the Alloy pass tests before it
    /// dereferences a part it did not just receive from the engine's own iterator.</summary>
    internal static bool EntryLive(int e, IntPtr part)
        => (uint)e < MaxParts && _pState[e] == StActive && _pPart[e] == part;

    /// <summary>#137: was this entry recorded through <c>PartSkin::Draw</c>?</summary>
    internal static bool Skinned(int e) => (uint)e < MaxParts && _pSkin[e];

    /// <summary>Kill an entry and record which gate did it. Returns StDead so the two assignments that are
    /// expressions rather than statements can use it too.</summary>
    internal static byte Die(int e, string why)
    {
        _pDeath[e] = why;
        return _pState[e] = StDead;
    }

    /// <summary>#221: record that <paramref name="part"/> has been destroyed, and return the entry that held
    /// it (-1 if none). Separate from <c>OnPartDtor</c> so the self-check can exercise it without the
    /// trampoline. Compares the pointer only -- a dying part is never dereferenced.</summary>
    internal static int MarkFreed(IntPtr part)
    {
        int e = Find(part);
        if (e >= 0) _pFreed[e] = true;
        return e;
    }


    /// <summary>#126: the tile fade factor this part draws at, 1.0 for a part with no tile or a tile that is
    /// not fading. This is the multiplier <c>RenderDrawBucket</c> folds into <c>part+0xa0</c> for the length
    /// of a bucket-8 draw and restores afterwards, so the Alloy pass reads it rather than the multiplied
    /// alpha -- our draw is not inside the engine's brackets. Clamped at 0: a factor the engine has driven
    /// past the end of a fade would otherwise come back as a negative alpha.</summary>
    internal static float TileFadeFactor(IntPtr tri)
    {
        IntPtr tile = new PartRef(tri).Tile;
        if (tile == IntPtr.Zero) return 1f;
        float f = *(float*)(tile + TileFade);
        return f >= 1f ? 1f : f > 0f ? f : 0f;
    }

    /// <summary>#338: is this part something the engine would ever draw? <c>PartTriMesh::IsPartRenderable</c>
    /// @0x450e60, reproduced from the decompile documented on <see cref="SceneLayout.PartRenderFlag"/>.
    /// <para>Every <c>Part*::Draw</c> override runs this first -- TriMesh, AnimMesh, AABB, DanglyMesh and
    /// Skin all call it (objdump xref) -- and so does the colour-bucket add. A part that fails it is drawn
    /// from NO engine path, which is why refusing it costs no caster: it was never drawn to begin with.
    /// <para><c>part+0x96</c> has exactly two writers: the <c>PartTriMesh</c> ctor @0x43cb50 sets it, and
    /// <c>CombineParts</c> @0x473d30 clears it on every part it MERGES INTO a combined mesh. So it is
    /// load-time state that never flips back, and the parts it retires are absorbed originals still hanging
    /// in the scene tree. Alloy enumerates the world rather than the engine's buckets (#255), so nothing
    /// else filtered them out: they enrolled and drew their old geometry exactly coincident with the
    /// combined mesh that replaced it, as solid black (authored `black`/`plx_trans`, ambient, diffuse and
    /// emissive all zero) Z-fighting the real surface.</para></summary>
    internal static bool IsRenderable(IntPtr part)
    {
        if (part == IntPtr.Zero || *(byte*)(part + SceneLayout.PartRenderFlag) == 0) return false;
        IntPtr n = *(IntPtr*)(part + AlloyRenderer.PartMdlNode);
        if (n == IntPtr.Zero || *(int*)(n + SceneLayout.MdlRenderGate) == 0) return false;
        return *(short*)(n + SceneLayout.MdlRenderKind) != 0
               || *(IntPtr*)(n + SceneLayout.MdlRenderGeom) != IntPtr.Zero;
    }

    /// <summary>Off-device: the four branches of the renderable rule over a fake part + node. A rule that
    /// inverts drops the world; one that always passes puts the black proxies back on screen.</summary>
    internal static bool RenderableSelfCheck(ExtenderLog log)
    {
        byte* part = stackalloc byte[0x100];
        byte* node = stackalloc byte[0x300];
        for (int i = 0; i < 0x100; i++) part[i] = 0;
        for (int i = 0; i < 0x300; i++) node[i] = 0;
        IntPtr p = (IntPtr)part;
        bool ok = !IsRenderable(IntPtr.Zero) && !IsRenderable(p);          // flag clear, and a null part
        part[SceneLayout.PartRenderFlag] = 1;
        ok &= !IsRenderable(p);                                            // flag set, no node
        *(IntPtr*)(part + AlloyRenderer.PartMdlNode) = (IntPtr)node;
        ok &= !IsRenderable(p);                                            // node, gate 0
        *(int*)(node + SceneLayout.MdlRenderGate) = 1;
        ok &= !IsRenderable(p);                                            // kind 0 and no geometry
        *(IntPtr*)(node + SceneLayout.MdlRenderGeom) = (IntPtr)1;
        ok &= IsRenderable(p);                                             // kind 0 but geometry present
        *(IntPtr*)(node + SceneLayout.MdlRenderGeom) = IntPtr.Zero;
        *(short*)(node + SceneLayout.MdlRenderKind) = 1;
        ok &= IsRenderable(p);                                             // kind set: geometry not asked
        if (!ok) log.Info("PartTable.RenderableSelfCheck: FAILED -- the IsPartRenderable rule disagrees with the decompile");
        return ok;
    }

    /// <summary>Does the camera dissolve this part right now (the keyhole wall-fade)? The Alloy pass asks
    /// per part per frame -- the same test the engine's own loop body runs. Since #124 it is what STAGES the
    /// dissolve into the per-draw record, not what hands the part back.</summary>
    /// <summary>#336: the engine's verdict reproduced from a cached box. <c>Camera::GetCanKeyholeDissolve</c>
    /// @0x4a6f70 decompiled: <c>cam+0xe50 &amp;&amp; part+0x50 == 0 &amp;&amp; part+0x78 &gt; cam+0xe64 &amp;&amp;
    /// BoxAbovePlane(min, max, plane@cam+0xe54) &lt; 0</c>; <c>BoxAbovePlane</c> @0x5b32d0 evaluates the
    /// plane at the box corner that minimises it (x = a &gt; 0 ? min : max, y = b &gt; 0 ? min : max, z =
    /// c &gt; 0 ? min : max) as <c>y*b + a*x + c*z + d</c>, in that order, in floats. The caller has
    /// already required a gobless part; <paramref name="box"/> is its six floats.</summary>
    internal static bool KeyholeDissolvesBox(float* box, byte keyholeOn, float* plane, float cut)
    {
        if (keyholeOn == 0 || !(box[5] > cut)) return false;
        float a = plane[0], b = plane[1], c = plane[2], d = plane[3];
        float x = a > 0f ? box[0] : box[3];
        float y = b > 0f ? box[1] : box[4];
        float z = c > 0f ? box[2] : box[5];
        return y * b + a * x + c * z + d < 0f;
    }

    internal static bool KeyholeArmed => _keyhole != null && _camSlot != IntPtr.Zero;

    internal static bool KeyholeDissolves(IntPtr tri)
    {
        if (_keyhole == null || _camSlot == IntPtr.Zero) return false;
        IntPtr cam = CurrentCamera;
        return cam != IntPtr.Zero && _keyhole(cam, tri) != 0;
    }

    /// <summary>#330: has the keyhole camera state moved since <paramref name="key"/> was last taken? Compares
    /// the live camera pointer plus its <see cref="CamKeyholeState"/> words against the four-word key and
    /// refreshes it. A null camera keys as all-zero, so the verdicts cached under a real camera do not
    /// survive its loss. Pure in its inputs, which is what the self-check exercises with a fake camera block.</summary>
    internal static bool KeyholeCameraMoved(IntPtr cam, ulong* key)
    {
        ulong* live = stackalloc ulong[CamKeyholeWords + 1];
        live[0] = (ulong)cam;
        for (int w = 0; w < CamKeyholeWords; w++)
            live[1 + w] = cam == IntPtr.Zero ? 0 : *(ulong*)(cam + CamKeyholeState + w * 8);
        bool moved = false;
        for (int w = 0; w <= CamKeyholeWords; w++)
            if (key[w] != live[w]) { key[w] = live[w]; moved = true; }
        return moved;
    }

    internal static int Find(IntPtr part)
    {
        int s = (int)(((ulong)part >> 4) % MaxParts);
        for (int probe = 0; probe < 32; probe++, s = (s + 1) % MaxParts)
        {
            // #221: a pointer match on a FREED slot is a miss -- the address was recycled, so the caller
            // must re-record. Stopping here cannot skip a live entry for the same pointer: Insert claims the
            // first reusable slot in the chain, so a live one can never sit behind this pointer's tombstone.
            if (_pPart[s] == part) return _pFreed[s] ? -1 : s;
            if (_pState[s] == StEmpty) return -1;
        }
        return -1;
    }

    internal static int Insert(IntPtr part)
    {
        bool room = _partCount < MaxParts - MaxParts / 4;          // keep load factor sane
        int s = (int)(((ulong)part >> 4) % MaxParts);
        for (int probe = 0; probe < 32; probe++, s = (s + 1) % MaxParts)
        {
            // #221: reuse comes FIRST and is not gated on the load factor. Reusing a slot lengthens no probe
            // chain, and _partCount only ever climbs (nothing returns an entry to StEmpty), so gating reuse
            // on it is what made a long session's recycled pointers unownable once the table crossed 75%.
            // The slot is left indistinguishable from a fresh one; Record/Complete fill the rest.
            if (_pFreed[s])
            {
                // StEmpty here terminates the probe chain of every OTHER key that runs through this slot,
                // and live entries can sit beyond it -- so Record must write a non-empty state before
                // anything that can re-enter Find (a nested Vanilla draw, a diagnostic).
                _pFreed[s] = false; _pState[s] = StEmpty; AlloyCensus.OwnFlag[s] = 0;
                _pSetupTries[s] = 0; _pPoseTried[s] = false;
                _pPart[s] = part; _reclaims++; return s;
            }
            if (_pState[s] == StEmpty)
            { if (!room) return -1; _pPart[s] = part; _pSetupTries[s] = 0; _pPoseTried[s] = false; _partCount++; return s; }
        }
        return -1;
    }

    /// <summary>Drop every recorded identity: part entries and the ownership column.
    /// <see cref="TableGen"/> bumps, which is what tells AlloyRenderer its slot indices died.
    /// The live area re-records over a frame or two.</summary>
    /// <summary>The present at which the part table was last emptied. #134: the census counts from HERE, not
    /// from boot. Three runs in a row armed during the load that follows boot -- one before the module was
    /// even in, so every entry read "ownable-but-not-enrolled" for the honest reason that enrollment had not
    /// happened yet, and the dump said nothing about the question it was asked.</summary>
    internal static int _wipeFrame;

    /// <summary>Live entries at the last pressure check -- reported by the [batch] line so a saturated table
    /// is visible as a number rather than as "the renderer looks wrong in this area".</summary>
    internal static int _satLive, _satDead;
    internal static bool _saturated;

    /// <summary>The table is full. Reclaim it if it is full of the DEAD (an area transition's debris, which
    /// is what the wipe exists for), and refuse to if it is full of the LIVE (a big area, where wiping is
    /// the bug). A saturated table degrades the way it already knows how: <see cref="Insert"/> returns -1
    /// and the group allocator calls <c>Die(e, "groups-full")</c>, so the surplus stays engine-drawn and
    /// everything already recorded keeps rendering.</summary>
    internal static void OnTablePressure()
    {
        int live = 0, dead = 0;
        for (int i = 0; i < MaxParts; i++)
        {
            byte st = _pState[i];
            if (st == StEmpty) continue;
            if (st == StDead) dead++; else live++;
        }
        _satLive = live; _satDead = dead;
        // Majority-dead is the transition case. The margin is deliberately not 50/50-tight: a live area
        // always carries some dead entries (a creature that walked out of range, a part the engine freed),
        // and wiping a mostly-live table is the failure this exists to prevent, so the doubt goes to NOT
        // wiping.
        if (dead > live)
        {
            _saturated = false;
            WipeTables($"table pressure -- {dead} dead vs {live} live, reclaiming transition debris");
            return;
        }
        if (_saturated) return;                       // say it once per saturated stretch, not every frame
        _saturated = true;
        Log?.Info($"PartTable: table SATURATED -- {live} live vs {dead} dead entries in " +
                   $"{_partCount} parts. NOT wiping: this is a large area, not " +
                   "transition debris, and wiping live entries is what used to drop the whole world back " +
                   "to vanilla once a second. Parts past the cap stay engine-drawn.");
    }

    internal static void WipeTables(string why)
    {
        _wipeFrame = _frame;
        Array.Clear(_pPart); Array.Clear(_pState); Array.Clear(_pFreed); Array.Clear(_pSetupTries);
        Array.Clear(_pPoseTried);
        SkinPose.Forget();
        Array.Clear(AlloyCensus.OwnFlag);
        _partCount = 0; TableGen++;
        _saturated = false;
        // #256: the table is empty and only the walk can refill it. The pressure wipe changes no scene
        // membership, so the epoch will not move on its own -- ask for a walk explicitly.
        SceneWalk.RequestWalk();
        Log?.Info($"PartTable: {why} -- part tables wiped");
    }

}
