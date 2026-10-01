using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using static CX.Alloy.BucketGate;
namespace CX.Alloy;

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
    /// Scan bracket. Legal because it never blocks, never calls back and never throws.
    /// <para>CX-39: hand-written on purpose -- the calling CONVENTION is the point of this pointer, and a
    /// declared signature carries none.</para></summary>
    internal static delegate* unmanaged[SuppressGCTransition]<IntPtr, IntPtr, byte> _keyhole;
    internal static IntPtr _camSlot;   // &CurrentCamera; the KEYHOLE gate only -- see CurrentCamera
    /// <summary>The camera the world view renders from, or zero when there is none.
    /// <para>CX-109: <see cref="AlloyCamera.World"/> IS the answer now -- Alloy's own latch, set at the world
    /// view's <c>Camera::RenderScene</c> and good for the whole frame, where the engine's <c>CurrentCamera</c>
    /// global was null outside a scene render. <see cref="_camSlot"/> stays as the RESOLUTION check behind
    /// <see cref="KeyholeArmed"/>, which is about whether this client's symbol table is complete, not about
    /// which camera is live.</para></summary>
    internal static IntPtr CurrentCamera => AlloyCamera.World;
    /// <inheritdoc cref="Camera.KeyholeWordCount"/>
    internal const int CamKeyholeWords = Camera.KeyholeWordCount;

    internal static PartGetIsOpaqueFn _isOpaque;
    internal static MatHasEnvMapFn _hasEnvMap;
    internal static MatHasBumpyShinyMapFn _hasBumpyShiny;
    internal static MatGetTextureFn _getTexture;
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
        // Resolved ONCE into a static here rather than through EngineFn at the use site: these three are
        // asked per part per frame, and a lazy compare in that loop is the cost the declaration removes.
        _isOpaque = EngineFn.PartGetIsOpaque;
        _hasEnvMap = EngineFn.MatHasEnvMap;
        _hasBumpyShiny = EngineFn.MatHasBumpyShinyMap;
        IntPtr dv = Native.Sym(SymbolMap.PartDanglyMeshVtbl);
        _danglyVtbl = EngineAbi.Vtbl(dv);
        _getTexture = EngineFn.MatGetTexture;
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
    // per frame. Convert to PartEntry[] only against a measurement, never against the argument -- and BW7's
    // second half IS that measurement, for the four columns the route gate asks together (see PartEntry).
    // The rest stay columns: nothing reads them at one index in the same breath.
    /// <summary>#256: recorded by the scene walk, but its <c>VertexPrimitive</c> has no VDO yet --
    /// <c>VertexPrimitiveFlat::SetupArrays</c> @0x458920 has not run for this part. Not an error and not a
    /// death: enumeration reaches a part before its geometry is resident, so the record waits and the field
    /// is re-read (<c>SceneWalk.Rebind</c>) rather than the triple being invented.</summary>
    /// <summary>CX-92: the part carries <c>part+0x34 == 0</c>, the engine's own subtree draw gate -- it and
    /// its children are drawn from no engine path, so nothing is owned for it. NOT a death: the byte flips
    /// back with no ctor, no dtor and no <c>Scene::Sequence</c> bump (<c>Gob::SetSubtreeEnabled</c> @0x43c670
    /// is its only dynamic writer and bumps nothing), so the entry is HELD -- alive, unfreed, and re-read by
    /// <see cref="SceneWalk"/> every tick -- because a released one is the limb that never comes back.</summary>
    internal const byte StEmpty = 0, StActive = 2, StDead = 3, StStale = 4, StUnbound = 5, StHidden = 6;
    internal static readonly IntPtr[] _pNode = new IntPtr[MaxParts];
    internal static readonly IntPtr[] _pMat = new IntPtr[MaxParts];
    internal static readonly IntPtr[] _pVdo = new IntPtr[MaxParts];
    internal static readonly IntPtr[] _pTex0 = new IntPtr[MaxParts];   // diffuse slot at record (animation guard)
    internal static readonly int[] _pPrim = new int[MaxParts];         // prim type captured at the funnel
    internal static readonly int[] _pRot = new int[MaxParts];          // texture-rotation bits at record
    internal static readonly bool[] _pMx = new bool[MaxParts];         // matrix-path part (phase 2)
    internal static readonly int[] _pItem = new int[MaxParts];
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
    /// <summary>BW7: the four entry columns that are asked TOGETHER, at one hashed index, in one row. The
    /// route gate asks all of them per visited candidate -- <see cref="EntryLive"/> reads
    /// <see cref="State"/> and <see cref="Part"/>, <see cref="AlphaDirty"/> follows, and
    /// <see cref="Find"/>'s probe reads <see cref="Part"/> then <see cref="Freed"/> -- which was three or
    /// four cache lines per visit at an index that is a HASH of a pointer, so every one of them is a true
    /// miss rather than something a prefetcher sees coming. The row is 11 bytes of fields in a 16-byte stride,
    /// which the pointer's own alignment would impose on any struct holding it: 4,00 MiB over
    /// <see cref="MaxParts"/> against the 2,75 the four columns took, so the packing BUYS that 1,25 MiB of
    /// padding -- the three byte columns paid none of it.
    /// <para>This is the measurement the #194 note above asks for, and it stops here: the remaining columns
    /// are read one at a time by different walks, and folding them would widen the row every one of those
    /// walks streams.</para></summary>
    [StructLayout(LayoutKind.Sequential, Size = EntrySize)]
    internal struct PartEntry
    {
        /// <summary>The <c>PartTriMesh*</c> this entry recorded. Pointer identity is the hash key and half of
        /// <see cref="EntryLive"/>.</summary>
        public IntPtr Part;
        /// <summary>One of <see cref="StEmpty"/>..<see cref="StHidden"/>.</summary>
        public byte State;
        /// <summary>#221: the part this entry recorded has been destroyed (<c>Part::~Part</c>). The entry keeps
        /// its slot -- returning one to <see cref="StEmpty"/> would cut the probe chains that run through it --
        /// but its ADDRESS no longer means anything, so the next part the allocator hands back at it must
        /// re-record instead of landing on the tombstone. Without this a recycled pointer was unownable for the
        /// life of the table: <see cref="Find"/> matched it, the draw took the not-active branch, and the part
        /// drew vanilla forever (the character losing Alloy ownership after a few area transitions, which only
        /// an area transition healed).</summary>
        public bool Freed;
        /// <summary>BW3: Alloy's own animation runtime wrote this part's ALPHA
        /// (<see cref="EngineLayout.PartAlpha"/>) and the value changed, so a cached alpha describing it is
        /// stale. With <c>anim.owned</c> the controller runtime is the only writer of that field --
        /// <c>NewController::Control</c>'s store is the whole population, since the other writer
        /// (<c>RenderDrawBucket</c>'s tile-fade fold) does not run at <c>alloy.skipbuckets=3</c> -- which is
        /// what makes a dirty MARK exact rather than a heuristic.
        /// <para>ALPHA only. Self-illum is the record's other part-local colour term, but it is not a ROUTE
        /// term (it cannot change a part's class, alpha or dissolve), and it already rides the colour cadence
        /// that <c>RouteMemo</c>'s own cadence frame keeps firing -- a mark on it would have had no
        /// consumer.</para>
        /// <para>Set cold (only where a write moves the value), cleared wherever the live alpha is re-read. A
        /// stale mark on a recycled entry costs one extra read of one float and nothing else, so neither
        /// <see cref="Insert"/> nor the death notice has to clear it.</para></summary>
        public bool AlphaDirty;
    }
    /// <summary>The entry rows.</summary>
    internal static readonly PartEntry[] _pEnt = new PartEntry[MaxParts];
    /// <summary>What <see cref="PartEntry"/> must still cost: four rows to a cache line, declared as the
    /// struct's <c>Size</c> so a field appended past it cannot load at all -- which is the loudest a stride
    /// drift gets, and it happens on the next <c>run-demo.sh</c> rather than in somebody's session. Raise this
    /// with the field and <see cref="RenderableSelfCheck"/> says so on the log instead, loudly but without
    /// standing Alloy down for what is a cost rather than a misrender.
    /// not-an-offset: a size, not an engine layout.</summary>
    internal const int EntrySize = 16;

    /// <summary>BW3: <c>NewController::Control</c> moved <paramref name="part"/>'s alpha. One hash probe, on a
    /// change only; nothing here allocates or logs (the animation walk is the caller).</summary>
    internal static void NoteAlphaWrite(IntPtr part)
    {
        int e = Find(part);
        if (e >= 0) _pEnt[e].AlphaDirty = true;
    }

    /// <summary>Has this entry's alpha been written since it was last read? An entry outside the table reads
    /// dirty, which is the safe answer for a caller deciding whether it may skip the read.</summary>
    internal static bool AlphaDirty(int e) => (uint)e >= MaxParts || _pEnt[e].AlphaDirty;
    /// <summary>The live alpha was just read: the mark is spent.</summary>
    internal static void ClearAlphaDirty(int e) { if ((uint)e < MaxParts) _pEnt[e].AlphaDirty = false; }

    /// <summary>The geometry totals (<see cref="SceneWalk.GeomSig"/>) this record's VertexPrimitive carried
    /// the last time <see cref="SceneWalk"/> asked it to set its arrays up, or <see cref="GeomSigNever"/>
    /// before the first ask. It is the ENGINE's own condition, not a retry budget:
    /// <c>VertexPrimitiveFlat::SetupArrays</c> @0x458920 takes its no-geometry exit exactly when the vertex
    /// or index totals summed off the <c>MdlNodeTriMesh</c> list are zero, so totals unchanged since the last
    /// failed ask means the same exit and the call is skipped -- and totals that MOVED mean the model became
    /// resident, which is the one thing worth re-asking on. That exit leaves <see cref="EngineLayout.VpVdo"/>
    /// NULL, so the engine's own entry guard (<c>vp+0x64 &amp;&amp; vp+0x58 &amp;&amp; !vp+0x84</c>) fails
    /// next call and vanilla redoes the work every draw, for ever (#317); this is the same "no verdict is
    /// ever final" with one cheap read standing in for the call.
    /// <para>Cleared wherever the primitive underneath may have changed -- a slot reclaimed by
    /// <see cref="Insert"/>, and a record whose node or material moved (<c>SceneWalk.Rebind</c>).</para></summary>
    internal static readonly long[] _pGeomSig = new long[MaxParts];
    /// <summary>The <see cref="_pGeomSig"/> value meaning "never asked", distinct from every real signature
    /// (which <see cref="SceneWalk.GeomSig"/> keeps non-negative). Zero would collide with the empty totals
    /// that are the common case, and the first ask must happen whatever the totals read.</summary>
    internal const long GeomSigNever = -1;
    /// <summary>#284: has <see cref="SkinPose"/> already spent this record's one <c>PartSkin::PostProcess</c>
    /// @0x483120 on it? A genuine one-shot, unlike <see cref="_pGeomSig"/> -- the engine call latches
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
    /// sitting at zero silently, because <c>alloy.drawhooks=stats</c> otherwise disables the Alloy pass too.
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

    /// <summary>A <c>PartDanglyMesh</c>, which reaches this hook because <c>PartDanglyMesh::Draw</c> @0x451b60
    /// calls <c>PartTriMesh::Draw</c> outright. An identity for the census and the per-frame re-harvest; not a
    /// reason to refuse the part. The CPU re-solve is <c>PartDanglyMesh::Animate(float)</c> @0x49bbb0 -- a
    /// separate per-frame pass, NOT the draw, so suppressing the draw does not freeze the sway -- and it
    /// writes per-part into that part's own VDO item (<c>GetItemDynamicData(vp+0x58, vp+0x60)</c>, the DYNAMIC
    /// vertex pool at vdo+0x30, stride vdo+0x38, through the same item-offset table at vdo+0x48 the static
    /// pool uses), so a harvest off the engine's own VAO inherits whichever pool the engine bound. Tested by
    /// vtable, which is exact -- the class has no flag to read (#135).</summary>
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
    /// dereferences a part it did not just receive from the engine's own iterator.
    /// <para>BW7: both halves come off one <see cref="PartEntry"/> row, so this and the
    /// <see cref="AlphaDirty"/> that follows it in <c>RouteMemo</c> are one cache line between them.</para></summary>
    internal static bool EntryLive(int e, IntPtr part)
    {
        if ((uint)e >= MaxParts) return false;
        ref PartEntry en = ref _pEnt[e];
        return en.State == StActive && en.Part == part;
    }

    /// <summary>#137: was this entry recorded through <c>PartSkin::Draw</c>?</summary>
    internal static bool Skinned(int e) => (uint)e < MaxParts && _pSkin[e];

    /// <summary>Kill an entry and record which gate did it. Returns StDead so the two assignments that are
    /// expressions rather than statements can use it too.</summary>
    internal static byte Die(int e, string why)
    {
        _pDeath[e] = why;
        return _pEnt[e].State = StDead;
    }

    /// <summary>#221: record that <paramref name="part"/> has been destroyed, and return the entry that held
    /// it (-1 if none). Separate from <c>OnPartDtor</c> so the self-check can exercise it without the
    /// trampoline. Compares the pointer only -- a dying part is never dereferenced.</summary>
    internal static int MarkFreed(IntPtr part)
    {
        int e = Find(part);
        if (e >= 0) _pEnt[e].Freed = true;
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
        float f = new TileData(tile).Fade;
        return f >= 1f ? 1f : f > 0f ? f : 0f;
    }

    /// <summary>#338: is this part something the engine would ever draw? <c>PartTriMesh::IsPartRenderable</c>
    /// @0x450e60, reproduced from the decompile documented on <see cref="EngineLayout.PartRenderFlag"/>.
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
        if (part == IntPtr.Zero || new PartTriMesh(part).RenderFlag == 0) return false;
        var n = new MdlNodeTriMesh(new Part(part).Node.Ptr);
        if (n.IsNull || n.RenderGate == 0) return false;
        return n.RenderKind != 0 || n.RenderGeom != IntPtr.Zero;
    }

    /// <summary>CX-91: does the cached VDO still hold this record's item, as a mode-1 indexed static?
    /// <para>The four things that can stop being true after a successful bind are all one fault -- the
    /// VertexDataObject moved under us. <c>VertexPrimitiveFlat::SetupArrays</c> @0x458920 calls
    /// <c>VertexDataObject::RemoveItem</c> before it re-adds, and a VDO block freed and re-minted at the same
    /// address carries somebody else's item table (see the VdoSig fingerprint). Either way the answer is to
    /// re-read the draw arguments, not to refuse: a refused part draws nowhere since #264.</para></summary>
    internal static bool ItemResident(int e)
    {
        IntPtr v = _pVdo[e];
        if (v == IntPtr.Zero) return false;
        var vd = new VdoRef(v);
        if (vd.Mode != 1 || vd.ElemItems == 0) return false;
        int it = _pItem[e];
        return it >= 0 && it < vd.ElemCount && it < vd.VertCount;
    }

    /// <summary>Off-device: every branch of <see cref="ItemResident"/> over a fake VDO. Getting this wrong
    /// either refuses live geometry or lets the census index an item table past its end.</summary>
    internal static bool ItemResidentSelfCheck(ExtenderLog log)
    {
        byte* vdo = stackalloc byte[0x100];
        byte* items = stackalloc byte[0x40];
        for (int i = 0; i < 0x100; i++) vdo[i] = 0;
        for (int i = 0; i < 0x40; i++) items[i] = 0;
        const int e = MaxParts - 1;
        IntPtr savedVdo = _pVdo[e]; int savedItem = _pItem[e];
        var vv = new VertexDataObject((IntPtr)vdo);
        bool ok;
        try
        {
            _pVdo[e] = IntPtr.Zero; _pItem[e] = 0;
            ok = !ItemResident(e);                                     // no VDO at all
            _pVdo[e] = (IntPtr)vdo;
            ok &= !ItemResident(e);                                    // mode 0: not an indexed static
            vv.Mode = 1;
            ok &= !ItemResident(e);                                    // no element item table
            vv.ElemItems = (IntPtr)items;
            ok &= !ItemResident(e);                                    // counts 0: the item is not in it
            vv.ElemCount = 1; vv.VertCount = 1;
            ok &= ItemResident(e);                                     // item 0 of 1
            _pItem[e] = 1;
            vv.ElemCount = 1; vv.VertCount = 2;
            ok &= !ItemResident(e);                                    // past the ELEMENT count alone
            vv.ElemCount = 2; vv.VertCount = 1;
            ok &= !ItemResident(e);                                    // past the VERTEX count alone
            vv.VertCount = 2;
            ok &= ItemResident(e);                                     // inside both
            _pItem[e] = -1;
            ok &= !ItemResident(e);                                    // SetupArrays' zero-totals verdict
        }
        finally { _pVdo[e] = savedVdo; _pItem[e] = savedItem; }
        log.Info(ok ? "PartTable.ItemResidentSelfCheck: passed"
                    : "PartTable.ItemResidentSelfCheck: FAILED -- the item-residency rule is wrong");
        return ok;
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
        var pv = new PartTriMesh(p);
        var nv = new MdlNodeTriMesh((IntPtr)node);
        pv.RenderFlag = 1;
        ok &= !IsRenderable(p);                                            // flag set, no node
        new Part(p).Node = new MdlNode((IntPtr)node);
        ok &= !IsRenderable(p);                                            // node, gate 0
        nv.RenderGate = 1;
        ok &= !IsRenderable(p);                                            // kind 0 and no geometry
        nv.RenderGeom = (IntPtr)1;
        ok &= IsRenderable(p);                                             // kind 0 but geometry present
        nv.RenderGeom = IntPtr.Zero;
        nv.RenderKind = 1;
        ok &= IsRenderable(p);                                             // kind set: geometry not asked
        // BW7: the entry row's stride is the whole point of packing it, so drift is SAID rather than assumed.
        // Loud, not gating: this function sits in the chain that stands Alloy down (AlloyRenderer.cs), and a
        // row that grew a field costs cache lines, not correctness -- taking every user back to vanilla over
        // it would be the wrong trade.
        if (sizeof(PartEntry) != EntrySize)
            log.Always($"PartTable: the entry row is {sizeof(PartEntry)} bytes, not {EntrySize} -- the route "
                     + "gate is back to a cache line per column (BW7)");
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
    /// the live camera pointer plus its <see cref="Camera.KeyholeWords"/> against the four-word key and
    /// refreshes it. A null camera keys as all-zero, so the verdicts cached under a real camera do not
    /// survive its loss. Pure in its inputs, which is what the self-check exercises with a fake camera block.</summary>
    internal static bool KeyholeCameraMoved(IntPtr cam, ulong* key)
    {
        ulong* live = stackalloc ulong[CamKeyholeWords + 1];
        live[0] = (ulong)cam;
        Span<ulong> words = cam == IntPtr.Zero ? default : new Camera(cam).KeyholeWords;
        for (int w = 0; w < CamKeyholeWords; w++)
            live[1 + w] = words.IsEmpty ? 0 : words[w];
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
            ref PartEntry en = ref _pEnt[s];   // BW7: pointer, tombstone and state are one line
            if (en.Part == part) return en.Freed ? -1 : s;
            if (en.State == StEmpty) return -1;
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
            ref PartEntry en = ref _pEnt[s];
            if (en.Freed)
            {
                // StEmpty here terminates the probe chain of every OTHER key that runs through this slot,
                // and live entries can sit beyond it -- so Record must write a non-empty state before
                // anything that can re-enter Find (a nested Vanilla draw, a diagnostic).
                en.Freed = false; en.State = StEmpty; AlloyCensus.OwnFlag[s] = 0;
                _pGeomSig[s] = GeomSigNever; _pPoseTried[s] = false;
                en.Part = part; _reclaims++; return s;
            }
            if (en.State == StEmpty)
            { if (!room) return -1; en.Part = part; _pGeomSig[s] = GeomSigNever; _pPoseTried[s] = false; _partCount++; return s; }
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
    /// the bug). A saturated table degrades the way it already knows how: <see cref="Insert"/> returns -1,
    /// the surplus is NOT DRAWN (no engine walk stands behind a refusal since #264) and everything already
    /// recorded keeps rendering.</summary>
    internal static void OnTablePressure()
    {
        int live = 0, dead = 0;
        for (int i = 0; i < MaxParts; i++)
        {
            byte st = _pEnt[i].State;
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
                   "to vanilla once a second. Parts past the cap are not drawn.");
    }

    internal static void WipeTables(string why)
    {
        _wipeFrame = _frame;
        // The whole row: pointer, state, tombstone -- and, newly, the BW3 alpha mark, which the three
        // separate clears here did not touch. Harmless: every identity is gone, so the entry a mark belonged
        // to re-records, and a re-tenanted route slot re-reads its alpha anyway (UnstampChase).
        Array.Clear(_pEnt);
        Array.Fill(_pGeomSig, GeomSigNever); Array.Clear(_pPoseTried);
        SkinPose.Forget();
        Array.Clear(AlloyCensus.OwnFlag);
        _partCount = 0; TableGen++;
        _saturated = false;
        // ...and the held-hidden list with it: every state byte above is now zero, so every index it holds
        // names an entry that holds nothing, and nothing else would ever take them out (CX-92).
        SceneWalk.ForgetHidden();
        // #256: the table is empty and only the walk can refill it. The pressure wipe changes no scene
        // membership, so the epoch will not move on its own -- ask for a walk explicitly.
        SceneWalk.RequestWalk();
        Log?.Info($"PartTable: {why} -- part tables wiped");
    }

}
