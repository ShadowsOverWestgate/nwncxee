using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Ui;

namespace Sowx.Services.Perf;

/// <summary>
/// ADR 0006 phase 1: extender-owned draw submission for opaque, pre-transformed static geometry.
/// The engine's 2002 bucket loop submits ~2000 tiny draws/frame (each with full per-draw state churn);
/// this service records each batchable part's draw once -- (VDO, item, prim) captured at the
/// <c>GLRender::RenderVertexData</c> funnel, grouped by (VDO, Material, bucket, cull phase, render hint,
/// lighting toggle) -- and thereafter, when the first clean member of a group draws (engine state fully
/// configured by the engine itself), submits every other member in ONE <c>glMultiDrawElementsBaseVertex</c>
/// and skips their per-part path. Geometry is byte-identical to vanilla: same shared vertex buffers, same
/// index ranges (re-read from the VDO item tables each submit, so <c>Repack()</c> moves are absorbed),
/// same program/textures/uniforms (the first member's own draw applied them).
/// <para>Excluded from BATCHING (always vanilla, unless the owned pass takes them): matrix-path/skinned
/// parts, bumpy-shiny materials, non-opaque parts, bucket 8 (per-item alpha), keyhole-dissolving or
/// tile-fading members (per-frame via the engine's own <c>SetCanDissolve</c> value + the part's tile-fade
/// factor). #126 split "not batchable" from "not recorded": the transparent classes are captured and
/// grouped like anything else, because that is what the owned pass enrolls from, and carry a
/// <see cref="_pOwnOnly"/> flag that keeps them out of this service's own submits. Lifetime: entries are keyed by part+node+material pointer identity; a mismatch kills the entry
/// (recycled pointer), and submits never dereference a part -- only our own copies + the live VDO tables --
/// so freed parts simply stop being seen (members must have drawn within the last 2 frames to be included).</para>
/// <para><c>SOWX_BATCH=0</c> kill switch. Stands down under <c>SOWX_DRAWSTATS=1</c> (both hook the same
/// symbols; drawstats also wants the vanilla draw stream). Hot and alloc-free throughout.</para>
/// </summary>
public sealed unsafe class BatchService : IExtenderService
{
    /// <summary>Part field offsets (Ghidra RenderDrawBucket/PartTriMesh::Draw @0x46d060/0x4513d0):
    /// material at part+0x58, TileData* at part+0x98 (Ghidra CheckFadePlane @0x47bf30), and
    /// <see cref="PartNode"/> -- which is NOT a node.
    /// <para>#134 named it: part+0x88 is the part's <c>VertexPrimitive</c>. The vtables prove it (live
    /// capture, resolved against nm: <c>_ZTV19VertexPrimitiveFlat</c> for a TriMesh part,
    /// <c>_ZTV21VertexPrimitiveDangly</c> for a dangly one), and so does its +0x10, which is the back-pointer
    /// to the part itself -- <c>n10 == part</c> on all 12000 draws of a two-run census. The name stays
    /// because it is threaded through three services; what it holds does not change.</para></summary>
    private const int PartNode = 0x88, PartMaterial = 0x58, PartTile = 0x98;
    /// <summary>Texture-rotation angle (degrees, float) at part+0xb0 (Ghidra PartTriMesh::RotateTexture
    /// @0x44fff0): TransformTextureMatrix pushes it before every draw -- NWN's tile-texture 0/90/180/270
    /// anti-repetition scheme. Part of the group key (raw bits), else a batch renders every member under
    /// the first member's rotation.</summary>
    private const int PartTexRotate = 0xb0;
    /// <summary>Material layout (Ghidra GetTexture/GetEnvMapTID @0x4c5810/0x4c5c40): texture slots at
    /// mat+i*0x10, SharedMaterial override at +0xd0, blend-disable flag RenderFlat reads at +0x98.</summary>
    private const int MatShared = 0xd0, MatBlendFlag = 0x98;
    /// <summary>#126: the material's blend pair -- source factor at +0x90, destination at +0x94, both
    /// <c>Aurora::AuroraBlendModes</c> (Ghidra <c>Material::SetBlendingMode</c> @0x4c5c50 does
    /// <c>SetBlendFunc(*(mat+0x90), *(mat+0x94))</c>). <c>dst == 2</c> is <c>GL_ONE</c>, which is the
    /// engine's own additive test and the one pair it turns the depth mask off for.</summary>
    private const int MatSrcBlend = 0x90, MatDstBlend = 0x94, BlendOne = 2;
    /// <summary>#126: the part's own alpha multiplier, the first term of
    /// <c>PartTriMesh::GetIsOpaque</c> @0x43cee0 -- and the one <c>RenderDrawBucket</c> folds the tile's
    /// fade factor into for the length of a bucket-8 draw.</summary>
    private const int PartAlpha = 0xa0;
    /// <summary>#126: the three <c>.mtr</c> keywords on the SharedMaterial that steer bucket membership
    /// (<c>SharedMaterial::ParseField</c> @0x4c6760): <c>transparency</c> at +0x1f0, <c>sample_framebuffer</c>
    /// at +0x1f4 (1 = bucket 5, 2 = water) and <c>volumetric</c> at +0x1f8.</summary>
    private const int SharedTransparency = 0x1f0, SharedSampleFb = 0x1f4, SharedVolumetric = 0x1f8;
    /// <summary>The tilefade cap bucket -- Tracy scope "Tilefade Cap", fed from <c>capbuckets</c>.</summary>
    private const int CapBucket = 7;
    /// <summary>#134: the dynamic gob pair (#103's taxonomy). Everything instanced lands in one of these two
    /// -- placeables, doors, and the tileset's own static objects -- and until the <c>shaped</c> gate was
    /// corrected nothing in them ever reached enrollment, so excluding them here cost nothing visible. They
    /// are ownable as a PAIR: the engine walks 2 then 3, and owning one without the other splits a single
    /// instance's geometry across two renderers.</summary>
    private const int GobOpaqueBucket = 2, GobBlendBucket = 3;
    /// <summary>The material's own env map (<c>Material::HasEnvMap</c> @0x4c6180 tests exactly this field).
    /// It is NOT one of the numbered texture slots, so <c>GetTexture</c> never reaches it -- a group keyed on
    /// the numbered slots alone cannot tell an env-mapped material from a plain one sharing its diffuse, and
    /// one batched draw would then paint all of them with the representative's shine (or none of them).
    /// Compared only, never dereferenced: a <c>CAurTexture*</c> is borrowed.</summary>
    private const int MatEnvMap = 0x40;
    /// <summary>#123: the <c>customShaderVS</c>/<c>FS</c> resrefs the .mtr named, as inline 17-byte NUL-
    /// terminated buffers. Ghidra <c>SharedMaterial::ParseField</c> @0x4c6760 parses them to exactly
    /// <c>this+0x188</c> and <c>this+0x199</c> (GS at +0x1aa, hence the 0x11 stride), and
    /// <c>SharedMaterial::Init</c> @0x4c6ff0 hands those same three addresses to
    /// <c>GetCustomShaderProgram</c>, which confirms them.
    /// <para>This is what a material is classified by, NOT the compiled programs it caches at +0x1c0 (the
    /// discard-capable build) and +0x1c8 (the NO_DISCARD, Early-Z-friendly one that
    /// <c>PartTriMesh::Mat</c> @0x450960 picks for an opaque part that cannot dissolve). Those are filled in
    /// by Init and stay null until it runs, whereas the names are parsed the moment the .mtr loads. An empty
    /// VS name is the real "pure texture redirect" test: such a shared material adds no program at all, and
    /// the owned pass reproduces it exactly.</para></summary>
    private const int SharedVsName = 0x188, SharedNameLen = 0x11;
    /// <summary>#125: <c>customShaderFS</c>, the next 17-byte resref slot after
    /// <see cref="SharedVsName"/> (SharedMaterial::ParseField @0x4c6760 writes +0x188/+0x199/+0x1aa for
    /// VS/FS/GS). A material can name a fragment shader and no vertex shader.</summary>
    private const int SharedFsName = 0x199;
    /// <summary>Gob (the per-instance modifier block PartTriMesh::Mat reads: alpha/colour/selfIllum
    /// multipliers and the env-map override at gob+0x480). Non-null = engine-drawn, never ownable (#72).</summary>
    private const int PartGob = 0x50;
    /// <summary>Tile-fade state and factor (the TileData* hangs off <see cref="PartTile"/>). #124 corrected
    /// both: the factor is at +0x70, NOT +0x8c below it -- <c>TileData::TileData</c> @0x43f050 inits three
    /// separate floats to 1.0 and the original RE picked the wrong one, so this test was permanently false.
    /// <c>ProcessTriMeshParts</c> @0x480d40 is the reader: +0x70 is this frame's factor (+0x74 is the pending
    /// one <c>Scene::ResetTileFading</c> swaps in), and +0xc0 is the state enum.
    /// <para>A tile that is fading leaves the opaque class outright -- ProcessTriMeshParts routes it into
    /// <c>fadebuckets</c>, and <c>Scene::AddPartsToDrawBuckets</c> @0x4751d0 puts those in bucket 8. That was
    /// a hand-back until #126, which made bucket 8 suppressible and the fade a per-frame overlay on the
    /// owned blended class (<see cref="TileFadeFactor"/>). This test still says "not batchable by this
    /// service", which it is: the per-item alpha multiply is per part.</para></summary>
    private const int NodeFlags = 0x80, TileFade = 0x70, TileFadeState = 0xc0;
    /// <summary><c>TileData::FADE</c> (@0x12ef184, value 1). The other two states (DO_NOT_FADE 2,
    /// FADE_NOT_CHECKED 4) leave the part in the opaque buckets, and the factor alone is not enough: a tile
    /// entering FADE still reads 1.0 for its first frame.</summary>
    private const int TileStateFade = 1;
    /// <summary>VDO layout (Ghidra AddItem/RenderVertexData): +0x14 mode (1 = indexed static), vertex item
    /// table {offset,size} at +0x48 (count +0x50), element item table at +0x58 (count +0x60).</summary>
    private const int VdoMode = 0x14, VdoVertItems = 0x48, VdoVertCount = 0x50, VdoElemItems = 0x58, VdoElemCount = 0x60;
    private const uint GlUnsignedShort = 0x1403;
    /// <summary>Recorded-part capacity, and with <c>MaxGroups</c> the reason the renderer can hold a whole
    /// object-dense area rather than the first 4096 parts of one. 262144 parts + 262144 groups is 70MB of
    /// the 135MB these tables cost resident (up from 22MB) -- bought deliberately, because a cap that cannot
    /// hold the area is a rendering fault and not a saving.
    /// <para>Power of two because the group intern table derives its Fibonacci shift from the exponent, and
    /// a shift written as a literal is the bug that has now bitten this codebase twice.</para></summary>
    private const int MaxPartsBits = 18, MaxParts = 1 << MaxPartsBits;
    /// <summary>Members of ONE group a single multidraw can carry. Raised with the group table: a dense
    /// area's biggest group is bounded by the group table, not by a separate number, and overflow here is a
    /// SILENT truncation -- the tail of the group simply is not drawn. Counted now (<c>_submitTrunc</c>) so
    /// it can never be silent again.</summary>
    private const int MaxSubmit = 65536;
    private const int AlphaBucket = 8, LogEvery = 900;
    /// <summary>Group capacity, and it is <see cref="MaxParts"/> deliberately rather than a fraction of it.
    /// <para>Sizing the two apart was fiction from the moment the model matrix entered the group key: a
    /// static matrix-class part groups only with parts at the IDENTICAL 16-float transform, and in a tileset
    /// almost nothing is, so <c>_groupCount</c> tracks <c>_partCount</c> one-to-one (measured live: 5828 of
    /// 5832 groups solo). At 8192 the group table was the binding cap on the whole renderer, 8x tighter than
    /// the part table it is fed by, and every area past ~7000 ownable parts tripped the pressure wipe below
    /// -- once a second, forever, each one dropping the world back to vanilla.</para>
    /// <para>Costs ~165 bytes per group across the 22 arrays below (64 for the eight pointers, 32 for the
    /// eight ints, 5 for the flags, 64 for the matrix), so ~10.8MB rather than ~1.4MB. That is the price of
    /// a large area rendering at all, and it is in line with what the shadow rings already map.</para></summary>
    private const int MaxGroups = MaxParts;
    /// <summary>Bucket-item vtable slot the RenderDrawBucket loop devirts to get the PartTriMesh from the
    /// bucket's Part (Ghidra @0x56d118: <c>(**(code**)(*part+0x60))(part)</c>).</summary>
    private const int PartVtblGetTriMesh = 0x60;
    /// <summary>Model matrix the bucket loop feeds to SetModelMatrix for matrix-path parts (node+0x18,
    /// Ghidra RenderDrawBucket). Bit-identical matrix = same placeable transform -> groupable (phase 2).</summary>
    private const int NodeMatrix = 0x18;

    private static ExtenderLog? _log;
    private static bool _active;
    private static delegate* unmanaged<IntPtr, void> _origPartDraw;
    /// <summary>#137: <c>PartSkin::Draw</c>'s trampoline. A separate entry point for the same job, because
    /// the true skinmesh class never passes through <see cref="_origPartDraw"/> at all -- see
    /// <see cref="SymbolMap.PartSkinDraw"/>. Null leaves the class exactly where it was before #137: engine
    /// drawn, uncaptured, and (like every un-hooked class) invisible to the census.</summary>
    private static delegate* unmanaged<IntPtr, void> _origPartSkinDraw;
    private static delegate* unmanaged<IntPtr, int, void> _origBucket;
    private static delegate* unmanaged<IntPtr, int, int, IntPtr> _origFirstItem;
    private static delegate* unmanaged<IntPtr, int, int, IntPtr> _origNextItem;
    private static delegate* unmanaged<IntPtr, IntPtr, byte> _keyhole;
    private static IntPtr _camSlot;   // &CurrentCamera; live CAurCamera* is *_camSlot
    private static delegate* unmanaged<int, void> _origCanDissolve;
    private static delegate* unmanaged<IntPtr, byte> _isOpaque, _hasEnvMap, _hasBumpyShiny;
    private static delegate* unmanaged<IntPtr, int> _getRenderHint;
    private static delegate* unmanaged<IntPtr, int, long> _getTexture;
    private static uint* _primTable;

    /// <summary>#126 triage: one-shot census of every part the ENGINE draws in one world pass -- which
    /// bucket it was captured in, why ownership passed it over, and what its diffuse texture actually is.
    /// Armed by <c>SOWX_DUMPFRAME=1</c>, fires on the next world bucket-0 entry and disarms. Cold by
    /// construction (it only ever runs on the one frame) and allocating is fine there.</summary>
    private static int _dumpAt = -1, _dumpLeft;
    /// <summary>SOWX_DUMPTEX: log only parts whose diffuse texture name contains this, one frame's worth at a
    /// time, for as long as it takes the camera to find them.</summary>
    private static string? _dumpTex;
    private static int _dumpTexFrame = -1, _dumpTexRows;
    /// <summary>Census sampling for SOWX_DUMPTEX: every Nth frame, up to M matching parts of it.</summary>
    private const int DumpTexEvery = 120, DumpTexRows = 16, DumpAllRows = 200;
    /// <summary>SOWX_DUMPTEX=* -- census every engine-drawn part of the sampled frame, not one texture's.</summary>
    private static bool _dumpAll;
    /// <summary>Table slot of the part SOWX_DUMPTEX last matched, published by the census for enrollment to
    /// recognise without dereferencing anything. -1 until the camera finds it.</summary>
    private static int _watchEntry = -1;

    // per-bucket-walk context
    private static int _inBucket, _bucketId = -1, _phase, _gen, _canDissolve, _frame;
    /// <summary>Scene whose bucket walk is currently running. Camera::RenderScene is not exclusive to the
    /// world view -- GUI 3D panels drive the same Scene::RenderSinglePass path with their own Scene -- so a
    /// capture group is keyed by the Scene it was recorded from and ownership can never harvest across two (#91).</summary>
    private static IntPtr _bucketScene;
    /// <summary>Whether <see cref="_bucketScene"/> is the world Scene, resolved while it is still live. Caster
    /// enrollment runs at the present with no Scene in hand, so it reads this instead of dereferencing a group's
    /// stored (possibly freed) Scene pointer.</summary>
    private static bool _bucketWorld;

    // part entries: open-address hash on part pointer, parallel arrays, no allocation
    private const byte StEmpty = 0, StCapturing = 1, StActive = 2, StDead = 3, StStale = 4, StProbe = 5;
    private static readonly IntPtr[] _pPart = new IntPtr[MaxParts];
    private static readonly IntPtr[] _pNode = new IntPtr[MaxParts];
    private static readonly IntPtr[] _pMat = new IntPtr[MaxParts];
    private static readonly IntPtr[] _pVdo = new IntPtr[MaxParts];
    private static readonly IntPtr[] _pTex0 = new IntPtr[MaxParts];   // diffuse slot at record (animation guard)
    private static readonly int[] _pPrim = new int[MaxParts];         // prim type captured at the funnel
    private static readonly int[] _pRot = new int[MaxParts];          // texture-rotation bits at record
    private static readonly bool[] _pMx = new bool[MaxParts];         // matrix-path part (phase 2)
    private static readonly ulong[] _pMxHash = new ulong[MaxParts];   // probation matrix hash
    private static readonly int[] _pItem = new int[MaxParts];
    private static readonly byte[] _pState = new byte[MaxParts];
    private static readonly bool[] _pDirty = new bool[MaxParts];
    private static readonly int[] _pGroup = new int[MaxParts];
    private static readonly int[] _pNext = new int[MaxParts];      // group member chain
    private static readonly int[] _pSeen = new int[MaxParts];
    private static readonly int[] _pIncluded = new int[MaxParts];
    /// <summary>#126: recorded for the OWNED pass only -- a transparent, additive, cap or fading part. It is
    /// captured (VDO, item, prim) and grouped like any other, because that is what ownership enrolls from,
    /// but this batcher never submits it: its own multidraw replays the first member's engine state, and
    /// blend factors, the bucket-8 alpha multiply and the cutout toggle all vary per part inside one
    /// material group. Its own draw stays vanilla until the owned pass takes it.
    /// <para>ponytail: latched at Record and never recomputed, so a part first sighted while its tile was
    /// fading stays unbatchable by THIS service for the life of the entry (the owned pass still takes it --
    /// it reclassifies per frame). The ceiling is batch coverage decaying across a session as tiles fade,
    /// recovering at the next table wipe. Recomputing it would also have to re-key the entry's group, whose
    /// bucket is part of the key; do that if the [batch] line's own-only count ever climbs.</para></summary>
    private static readonly bool[] _pOwnOnly = new bool[MaxParts];
    /// <summary>#134: which gate killed this entry, for the census. There are ten ways to reach StDead and
    /// the aggregate counters name three of them, which is how 5319 gob draws a frame went unexplained.</summary>
    private static readonly string[] _pDeath = new string[MaxParts];
    private static int _partCount;

    /// <summary>#82 ownership columns, driven by OwnedDrawService: 1 = the owned world pass owns this part
    /// (the batcher must never draw it), and the pass generation its owned draw last succeeded in. They live
    /// here so the iterator's existing <c>Find</c> serves both owners -- no second hash probe per part.</summary>
    internal static readonly byte[] OwnFlag = new byte[MaxParts];
    /// <summary>#135: this part is a <c>PartDanglyMesh</c>. Its vertices are re-solved every frame while its
    /// node matrix never moves, so a caster cache keyed on matrix motion would hold its shadow frozen.</summary>
    private static readonly bool[] _pDangly = new bool[MaxParts];
    /// <summary>#137: this entry was recorded through <c>PartSkin::Draw</c>, so it is a TRUE skinmesh --
    /// posed by the engine's 64-matrix bone array rather than by its node matrix alone. The owned pass needs
    /// this to know it must force a pose and bind bones; nothing else in this service treats it specially,
    /// because a skinned part is own-only and never batch-submitted.</summary>
    private static readonly bool[] _pSkin = new bool[MaxParts];
    /// <summary>#128: the Gob this part instances off, copied at <see cref="Record"/> while the part is
    /// certainly live. The all-owned-or-none sweep needs the gob of parts that DIED before enrollment ever
    /// saw them -- a multi-draw creature part dies at Complete as "multidraw-or-unindexed" -- and a dead
    /// entry's part pointer must never be dereferenced to get it.</summary>
    private static readonly IntPtr[] _pGob = new IntPtr[MaxParts];
    internal static readonly int[] OwnGen = new int[MaxParts];
    /// <summary>Pass generation in which the iterator last SUPPRESSED this part. Compared against
    /// <see cref="OwnGen"/> to audit the exactly-one-owner invariant: drawn but never suppressed in the
    /// same pass means the engine drew it too (double-draw) or nobody walked it (ghost geometry).</summary>
    internal static readonly int[] OwnSupGen = new int[MaxParts];
    /// <summary>Bumped on every table wipe: entry indices held elsewhere are stale from that moment.</summary>
    internal static int TableGen;
    /// <summary>Ownership-enrollment scan bound (the part table is open-addressed, so it is scanned whole).</summary>
    internal static int EntryCount => MaxParts;
    /// <summary>False = no capture tables, so the owned world pass has nothing to enroll from. It reports
    /// this rather than sitting at zero silently: SOWX_BATCH=0 otherwise disables the owned pass too.</summary>
    internal static bool Active => _active;

    /// <summary>Armed AND the frame latched owned (#86). The two hot funnels read this, so a vanilla frame
    /// records nothing, batches nothing and suppresses nothing -- every part takes the engine's own draw.
    /// Bookkeeping (EndFrame, the period log, enrollment candidacy) stays on <see cref="Active"/> so the
    /// counters still report while vanilla, which is what proves they read zero.</summary>
    private static bool Live => _active && Render.RenderMode.Owned;

    // groups: linear-scan registry (records are rare/cold), full component compare -- no hash collisions.
    // Keyed on material CONTENT (textures + shared-material + blend flag), never the Material* -- each part
    // owns its Material instance, so pointer identity shatters every group to size 1 (first live run).
    private static readonly IntPtr[] _gVdo = new IntPtr[MaxGroups];
    private static readonly IntPtr[] _gTex0 = new IntPtr[MaxGroups];
    private static readonly IntPtr[] _gTex1 = new IntPtr[MaxGroups];
    private static readonly IntPtr[] _gTex2 = new IntPtr[MaxGroups];
    private static readonly IntPtr[] _gTex3 = new IntPtr[MaxGroups];
    private static readonly IntPtr[] _gShared = new IntPtr[MaxGroups];
    private static readonly IntPtr[] _gEnv = new IntPtr[MaxGroups];     // #121: mat+0x40, part of the key
    private static readonly byte[] _gFlag = new byte[MaxGroups];
    private static readonly int[] _gBucket = new int[MaxGroups];
    private static readonly IntPtr[] _gScene = new IntPtr[MaxGroups];   // #91: the Scene this group was recorded from
    private static readonly bool[] _gWorld = new bool[MaxGroups];       // ... and whether that Scene was the world's
    private static readonly int[] _gPhase = new int[MaxGroups];
    private static readonly int[] _gHint = new int[MaxGroups];
    private static readonly bool[] _gLight = new bool[MaxGroups];
    private static readonly int[] _gPrim = new int[MaxGroups];
    private static readonly int[] _gRot = new int[MaxGroups];
    private static readonly bool[] _gMx = new bool[MaxGroups];
    /// <summary>#128: the group's members are MOVERS -- instanced parts whose transform changes every frame.
    /// Its <see cref="_gMatrix"/> is therefore meaningless and never written; the flag is in the group key so
    /// a mover can never join a static group whose frozen matrix a replayed multidraw would apply.</summary>
    private static readonly bool[] _gMover = new bool[MaxGroups];
    private static readonly float[] _gMatrix = new float[MaxGroups * 16];   // model matrix for matrix-path groups
    private static readonly int[] _gFirst = new int[MaxGroups];
    private static readonly int[] _gCount = new int[MaxGroups];
    private static readonly int[] _gSubmitGen = new int[MaxGroups];
    /// <summary>#149: the group-key intern table. <c>_gHash</c> is each group's key hash; <c>_gSlot</c> is the
    /// open-addressed index into it, -1 = empty, sized 2x for a 0.5 load factor.
    /// <para>The shift is bound to <see cref="GroupHashBits"/> rather than written as a literal. Fibonacci
    /// hashing takes the TOP bits of the product, so a hardcoded shift silently clusters every key into the
    /// first 1/N of the table the moment the table grows -- the bug that was sitting in <c>TexKey</c>.</para></summary>
    private const int GroupHashBits = MaxPartsBits + 1, GroupHashSize = 1 << GroupHashBits;
    private static readonly ulong[] _gHash = new ulong[MaxGroups];
    private static readonly int[] _gSlot = EmptySlots();
    /// <summary>-1, not 0, and it has to be done at construction as well as at every wipe: 0 is a valid
    /// group id, so a zero-filled table reads as "group 0 lives in every slot" and the very first record
    /// probes the whole table without finding a free one.</summary>
    private static int[] EmptySlots()
    {
        int[] a = new int[GroupHashSize];
        a.AsSpan().Fill(-1);
        return a;
    }
    private static int _groupCount;

    private static int _capture = -1, _pending = -1, _pendingSelf = -1;
    private static readonly int[] _mdCounts = new int[MaxSubmit];
    private static readonly void*[] _mdOffsets = new void*[MaxSubmit];
    private static readonly int[] _mdBase = new int[MaxSubmit];
    /// <summary>Group members a multidraw could not carry this period. Non-zero means geometry is being
    /// dropped from the tail of a group -- raise <see cref="MaxSubmit"/>.</summary>
    private static int _submitTrunc;

    // period stats
    private static long _batched, _calls, _skipped, _iterSkip, _vanDead, _vanSolo, _vanDirty, _vanOwnOnly;
    private static long _recNotOk, _recNoCap, _recBadVdo;   // why-dead forensics (record path, cold)
    private static int _capDiagLeft = 5, _subDiagLeft = 3;   // #81 crash-triage one-shots
    private static bool _wasInModule;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (Environment.GetEnvironmentVariable("SOWX_BATCH") == "0") return;
        if (Environment.GetEnvironmentVariable("SOWX_DRAWSTATS") == "1")
        { log.Info("BatchService: standing down (SOWX_DRAWSTATS diagnostics own the draw hooks)"); return; }

        _isOpaque = (delegate* unmanaged<IntPtr, byte>)Native.Sym(SymbolMap.PartGetIsOpaque);
        _hasEnvMap = (delegate* unmanaged<IntPtr, byte>)Native.Sym(SymbolMap.MatHasEnvMap);
        _hasBumpyShiny = (delegate* unmanaged<IntPtr, byte>)Native.Sym(SymbolMap.MatHasBumpyShinyMap);
        _getRenderHint = (delegate* unmanaged<IntPtr, int>)Native.Sym(SymbolMap.PartGetRenderHint);
        IntPtr dv = Native.Sym(SymbolMap.PartDanglyMeshVtbl);
        _danglyVtbl = dv == IntPtr.Zero ? IntPtr.Zero : dv + 0x10;
        // #126: warm-up in PRESENTS, not "the next pass" -- the first census fired on the freshest possible
        // table and read as enrollment lag rather than steady state, which is the opposite of the question.
        string? dumpAt = System.Environment.GetEnvironmentVariable("SOWX_DUMPFRAME");
        _dumpAt = dumpAt == null ? -1 : int.TryParse(dumpAt, out int df) && df > 1 ? df : 300;
        // #134: aim the census at a texture instead of at a moment. Five rolls in a row fired while the
        // camera was somewhere else, because "300 frames after the table settled" is not "standing at the
        // thing we are debugging" and no warm-up rule can make it so.
        _dumpTex = System.Environment.GetEnvironmentVariable("SOWX_DUMPTEX");
        _dumpAll = _dumpTex == "*";
        if (_dumpTex != null) _dumpAt = _dumpAt < 0 ? 0 : _dumpAt;
        _getTexture = (delegate* unmanaged<IntPtr, int, long>)Native.Sym(SymbolMap.MatGetTexture);
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        // #129: null (Windows, or a build without the symbol) leaves the bumpy-shiny route unrecognised, so
        // only .mtr-declared water is owned. Never a misrender: an unrecognised water part stays the engine's.
        _bumpyWater = (int*)Native.Sym(SymbolMap.EnableBumpyWater);
        if (_isOpaque == null || _hasEnvMap == null || _hasBumpyShiny == null || _getRenderHint == null || _getTexture == null || _primTable == null)
        { log.Info("BatchService: engine symbols unresolved -- batching unavailable (headless/Windows?)"); return; }

        _origPartDraw = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.PartTriMeshDraw, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnPartDraw);
        _origPartSkinDraw = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.PartSkinDraw, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnSkinDraw);
        // Chained, not Install()ed: RenderVertexData and Repack both have several subscribers (#88).
        _drawSlot = hooks.Chain(SymbolMap.GlRenderVertexData,
            (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)&OnVdoDraw, DrawPriority);
        _origBucket = (delegate* unmanaged<IntPtr, int, void>)hooks.Install(
            SymbolMap.RenderDrawBucket, (IntPtr)(delegate* unmanaged<IntPtr, int, void>)&OnBucket);
        _origFirstItem = (delegate* unmanaged<IntPtr, int, int, IntPtr>)hooks.Install(
            SymbolMap.BucketGetFirstItem, (IntPtr)(delegate* unmanaged<IntPtr, int, int, IntPtr>)&OnFirstItem);
        _origNextItem = (delegate* unmanaged<IntPtr, int, int, IntPtr>)hooks.Install(
            SymbolMap.BucketGetNextItem, (IntPtr)(delegate* unmanaged<IntPtr, int, int, IntPtr>)&OnNextItem);
        // #129: the water block's own gate. Null leaves bucket 6 to the engine, which is exactly the
        // pre-#129 behaviour -- water still renders, it just costs the engine's second framebuffer copy.
        _origHasItems = (delegate* unmanaged<IntPtr, int, int>)hooks.Install(
            SymbolMap.BucketHasItemsOfType, (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&OnHasItems);
        _keyhole = (delegate* unmanaged<IntPtr, IntPtr, byte>)Native.Sym(SymbolMap.GetCanKeyholeDissolve);
        _camSlot = Native.Sym(SymbolMap.CurrentCamera);
        _origCanDissolve = (delegate* unmanaged<int, void>)hooks.Install(
            SymbolMap.RiSetCanDissolve, (IntPtr)(delegate* unmanaged<int, void>)&OnSetCanDissolve);
        Render.SkyBox.Install(log, hooks);
        _repackSlot = hooks.Chain(SymbolMap.VdoRepack,
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnVdoRepack, RepackPriority);
        // #145: the death notice. Every other gate infers liveness (a sighting window, an identity compare
        // against fields that a freed block may still be holding); this one is told.
        _origPartDtor = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.PartDtor, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnPartDtor);
        _active = _origPartDraw != null && _drawSlot != null && _origBucket != null && _origFirstItem != null
                  && _repackSlot != null && _origPartDtor != null;
        log.Info(_active
            ? "BatchService: static-geometry batching armed (SOWX_BATCH=0 disables; [batch] every 900 frames)"
            : _origPartDtor == null
              // #145: deliberate. Without the death notice, every liveness test left is an inference, and
              // the one the owned pass used walked a freed part into a freelist word.
              ? "BatchService: Part::~Part unhooked -- batching stands down rather than track parts it "
                + "cannot be told the death of"
              : "BatchService: hook install failed -- batching unavailable");
    }

    [UnmanagedCallersOnly]
    private static void OnBucket(IntPtr scene, int id)
    {
        int prevId = _bucketId;
        IntPtr prevScene = _bucketScene;
        bool prevWorld = _bucketWorld;
        _inBucket++; _bucketId = id; _bucketScene = scene; _gen++;
        _bucketWorld = Render.OwnedDrawService.IsWorldScene(scene);
        if (_dumpAt >= 0 && _frame - _wipeFrame >= _dumpAt && _bucketWorld && id == 0)
        {
            _dumpAt = -1; _dumpLeft = DumpMax;
            _log?.Info(_dumpTex == null
                ? $"[dumpframe] begin, {_frame - _wipeFrame} frames after the table settled"
                : $"[dumpframe] begin, watching for \"{_dumpTex}\" until {DumpMax} rows or the module ends");
        }
        // #132: the owned block no longer hangs off this walk -- the frame anchor moved to
        // Scene::FadeDoors, the last engine call before RenderDrawBucket(0). All this hook still owes the
        // owned pass is the capture/suppression bracket around the engine's own walk.
        if (_origBucket != null) _origBucket(scene, id);
        _inBucket--; _bucketId = prevId; _bucketScene = prevScene; _bucketWorld = prevWorld;
    }

    /// <summary>#129: the whole of the engine's water block, gated on one answer.
    /// <para><c>Scene::RenderSinglePass</c> @0x46e5f0 wraps its water work in
    /// <c>if (HasItemsOfType(mgr, 6))</c>, and inside that block it does exactly two things:
    /// <c>Camera::CopyRenderSubframe</c> -- its SECOND full colour+depth snapshot of the frame -- and
    /// <c>RenderDrawBucket(6)</c>. Answering "no" removes both together, which is what makes the ticket's
    /// one-copy claim structural: there is no second suppression to keep in step with the first, and no way
    /// for the copy to survive a change to the walk.</para>
    /// <para>The answer is "no" only when every part in the bucket is one the owned pass DREW this pass, and
    /// that is checked against the bucket's live contents rather than against our own tables. The difference
    /// matters: with the walk suppressed, a water part that appears later (an area transition, a placeable
    /// that swims into view, anything sighted after the last enrollment) never runs its vanilla draw, and the
    /// capture funnel that would record it hangs off exactly that draw. Checked against our tables it would
    /// be in the bucket, suppressed, unowned and invisible -- permanently, since nothing would ever record
    /// it. Checked against the bucket, the newcomer simply makes this frame the engine's; it gets recorded
    /// through the normal funnel and the owned pass takes over a pass or two later.</para>
    /// <para>Only type 6. Type 5 is the framebuffer-sampling class, which is still the engine's.</para></summary>
    [UnmanagedCallersOnly]
    private static int OnHasItems(IntPtr mgr, int id)
    {
        int has = _origHasItems != null ? _origHasItems(mgr, id) : 0;
        if (id != Render.OwnedDrawService.WaterBucket || has == 0 || !Live) return has;
        // The walk runs FIRST and unconditionally, because it carries the sighting -- short-circuiting on
        // WaterPass.Ready would starve exactly the parts a stood-down copy is about to hand back, and they
        // would then be stale on the frame the copy recovers.
        bool covered = AllBucketPartsOwned(mgr, id);
        if (!covered || !Render.WaterPass.Ready) { _engineWaterWalks++; return has; }
        _ownedWaterWalks++;
        return 0;
    }

    /// <summary>Is every part currently filed in <paramref name="id"/> one the owned pass drew this pass?
    /// <para>Reads the bucket arrays directly. <c>DrawBucketManager::GetDrawBucketByItemType</c> @0x47bd60
    /// is <c>this + (type*3 + phase)*0x10</c> and <c>GetNextItem</c> @0x47be90 reads {<c>Part** items</c>
    /// @+0, <c>int count</c> @+8} out of it, so the layout is not inferred. The ITERATOR pair is deliberately
    /// not used: <c>GetFirstItem</c> resets a cursor at manager+0x300 that the engine's own walk owns.</para>
    /// <para>Each entry is a <c>Part*</c>; the recorded identity is its <c>PartTriMesh</c>, resolved through
    /// vtable slot 0x60 exactly as <see cref="OnNextItem"/> does. Cold-ish: a handful of parts, once per
    /// frame, no allocation.</para></summary>
    private static bool AllBucketPartsOwned(IntPtr mgr, int id)
    {
        // Walked to the END even once the answer is known: the walk is also where every part in the bucket
        // gets its sighting stamped, and an early return would starve exactly the parts it skipped.
        bool all = true;
        for (int phase = 0; phase < BucketPhases; phase++)
        {
            IntPtr bucket = mgr + (id * BucketPhases + phase) * BucketStride;
            IntPtr* items = *(IntPtr**)bucket;
            int count = *(int*)(bucket + BucketCount);
            if (items == null) continue;
            for (int i = 0; i < count; i++)
            {
                IntPtr part = items[i];
                if (part == IntPtr.Zero) continue;
                IntPtr tri = ((delegate* unmanaged<IntPtr, IntPtr>)
                              (*(IntPtr*)(*(IntPtr*)part + PartVtblGetTriMesh)))(part);
                if (tri == IntPtr.Zero) { all = false; continue; }
                int e = Find(tri);
                if (e < 0) { all = false; continue; }
                // THE SIGHTING, and it has to happen here or the class starves itself. Every other
                // _pSeen write is on the vanilla draw or on the bucket iterator -- and suppressing the
                // bucket-6 walk removes BOTH for water. Without this the liveness signal decays after one
                // frame, the staging walk drops every water part as "engine-culled", the gate hands the
                // frame back, the engine draws and re-sights, and the class oscillates on a two-frame
                // period: visibly, water flickering on and off. Presence in the bucket IS the sighting the
                // walk would have given -- the engine's own BSP visibility put it there this frame.
                _pSeen[e] = _frame;
                if (OwnFlag[e] == 0 || OwnGen[e] != Render.OwnedDrawService.PassGen) { all = false; continue; }
                // ...and the SUPPRESSION stamp, for the same reason. The audit's double-draw signature is
                // "owned this pass and not suppressed", and IterSight is what normally clears it -- but
                // IterSight only ever runs from the walk this gate is about to remove. Left unstamped, every
                // owned water part reports as UNMATCHED on every successful pass, which is exactly the alarm
                // that has to stay quiet to be worth anything. Stamped here rather than in a second walk:
                // for an owned part the engine's own walk would have stamped it too.
                OwnSupGen[e] = Render.OwnedDrawService.PassGen;
            }
        }
        return all;
    }

    /// <summary>Bucket-array geometry, from <c>GetDrawBucketByItemType</c> @0x47bd60 (stride 0x10, three
    /// phases per item type) and <c>GetNextItem</c> @0x47be90 (items at +0, count at +8).</summary>
    private const int BucketPhases = 3, BucketStride = 0x10, BucketCount = 8;
    private static delegate* unmanaged<IntPtr, int, int> _origHasItems;
    /// <summary>#129's acceptance counter: frames the ENGINE walked bucket 6 (and therefore took its own
    /// framebuffer copy), and frames the owned pass took it over instead.</summary>
    private static long _engineWaterWalks, _ownedWaterWalks;

    [UnmanagedCallersOnly]
    private static IntPtr OnFirstItem(IntPtr mgr, int id, int phase)
    {
        if (phase != _phase) { _phase = phase; _gen++; }
        return _origFirstItem != null ? _origFirstItem(mgr, id, phase) : IntPtr.Zero;
    }

    /// <summary>ADR 0006 phase 3: iteration-level ownership. Members already covered by this walk's
    /// multi-draw are skipped HERE, so the engine's loop body (matrix push, SetShaderParamv, fog/light/cull
    /// toggles, dissolve test, SetUniforms dirty-scan) never runs for them at all -- previously every skipped
    /// member still paid all of that and we only suppressed the terminal draw. The slot's end sentinel derefs
    /// to null, exactly the caller's own exit test, so looping past skipped slots is transparent. Any
    /// revalidation failure (recycled pointer, re-rotated/animated texture, tile fade, keyhole dissolve) falls
    /// through to the vanilla loop body, where OnPartDraw dirties/demotes as before -- worst case is one
    /// z-identical duplicate opaque draw that frame.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnNextItem(IntPtr mgr, int id, int phase)
    {
        for (;;)
        {
            IntPtr slot = _origNextItem(mgr, id, phase);
            if (slot == IntPtr.Zero) return slot;
            IntPtr part = *(IntPtr*)slot;
            if (part == IntPtr.Zero || !Live || _inBucket == 0)
                return slot;
            IntPtr tri = ((delegate* unmanaged<IntPtr, IntPtr>)(*(IntPtr*)(*(IntPtr*)part + PartVtblGetTriMesh)))(part);
            if (tri == IntPtr.Zero) return slot;
            int e = Find(tri);
            if (e < 0) return slot;
            // #82: a part the owned world pass drew this pass is skipped HERE (exactly-one-owner). The
            // sighting is also its liveness signal, exactly as for batched members below.
            if (OwnFlag[e] != 0)
            {
                _pSeen[e] = _frame;
                if (Render.OwnedDrawService.IterSight(e, _bucketId)) continue;
                return slot;
            }
            // ...and only THEN the batcher's own suppression, which bucket 8 stays out of (#126 moved the
            // gate here: the owned branch above must be able to suppress a fading part, which is a bucket-8
            // part by definition).
            if (_bucketId == AlphaBucket) return slot;
            if (_pState[e] != StActive || _pOwnOnly[e] || _pIncluded[e] != _gen) return slot;
            if (*(IntPtr*)(tri + PartNode) != _pNode[e] || *(IntPtr*)(tri + PartMaterial) != _pMat[e]) return slot;
            if (*(int*)(tri + PartTexRotate) != _pRot[e] || (IntPtr)_getTexture(_pMat[e], 0) != _pTex0[e]) return slot;
            IntPtr tile = *(IntPtr*)(tri + PartTile);
            if (tile != IntPtr.Zero && Fading(tile)) return slot;
            if (_keyhole != null && _camSlot != IntPtr.Zero)
            {
                IntPtr cam = *(IntPtr*)_camSlot;
                if (cam != IntPtr.Zero && _keyhole(cam, tri) != 0) return slot;
            }
            _pSeen[e] = _frame;   // the iterator sighting IS the liveness signal now
            _iterSkip++;
        }
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
        if (_bucketId <= MaxCasterBucket && IsInstance(part) && Shadow.CasterPass.SkinBegin(part, node))
        {
            PartDraw(part);
            Shadow.CasterPass.SkinEnd();
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
                       && Shadow.CasterPass.SkinBegin(part, node);
        PartDraw(part);
        if (bracket) Shadow.CasterPass.SkinEnd();
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
    private static bool _inSkinDraw;
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
        bool bracket = Shadow.CasterPass.SkinBegin(part, node);
        _origPartSkinDraw(part);
        if (bracket) Shadow.CasterPass.SkinEnd();
        _inSkinDraw = prev; _skinDrawPart = prevPart;
        _skinForce = false;
        _skinForced++;
        if (!_skinPosed) _skinForceFailed++;
        return _skinPosed;
    }

    /// <summary>#137: was this entry recorded through <c>PartSkin::Draw</c>?</summary>
    internal static bool Skinned(int e) => (uint)e < MaxParts && _pSkin[e];

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
    private static bool IsDangly(IntPtr part)
    {
        if (_danglyVtbl == IntPtr.Zero || part == IntPtr.Zero) return false;
        return *(IntPtr*)part == _danglyVtbl;
    }

    /// <summary><c>_ZTV14PartDanglyMesh</c> + 0x10 -- the value a PartDanglyMesh's vtable slot holds (the
    /// Itanium ABI puts the function pointers two words in).</summary>
    private static IntPtr _danglyVtbl;

    private static bool IsInstance(IntPtr part)
    {
        return part != IntPtr.Zero && *(IntPtr*)(part + PartGob) != IntPtr.Zero;
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
        if (_gCount[g] < 2 || Gl.MultiDrawElementsBaseVertex == null) { Vanilla(part); _vanSolo++; return; }

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
            Shadow.CasterPass.SkinObserve(vdo, (int)item, (int)prim);
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
            Shadow.CasterPass.SkinObserve(vdo, (int)item, prim);
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
        if (Shadow.CasterPass.SkinObserve(vdo, (int)item, prim)) return;
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
                  && (_hasBumpyShiny(mat) == 0 || _bucketId == Render.OwnedDrawService.WaterBucket);
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
                  && _bucketId != Render.OwnedDrawService.WaterBucket;
        int e = Insert(part);
        if (e < 0) { Vanilla(part); return; }                // table full -> vanilla forever
        _pNode[e] = node; _pMat[e] = mat; _pMx[e] = mx; _pVdo[e] = IntPtr.Zero; _pSeen[e] = _frame;
        _pDangly[e] = IsDangly(part); _pGob[e] = *(IntPtr*)(part + PartGob);
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

    /// <summary>#145: is this entry still a live recording of <paramref name="part"/>? Pointer identity is
    /// not enough on its own (a recycled allocation lands at the same address), and neither is the state on
    /// its own (an entry keeps its slot when it dies) -- the pair is what the owned pass tests before it
    /// dereferences a part it did not just receive from the engine's own iterator.</summary>
    internal static bool EntryLive(int e, IntPtr part)
        => (uint)e < MaxParts && _pState[e] == StActive && _pPart[e] == part;

    /// <summary>Kill an entry and record which gate did it. Returns StDead so the two assignments that are
    /// expressions rather than statements can use it too.</summary>
    private static byte Die(int e, string why)
    {
        _pDeath[e] = why;
        return _pState[e] = StDead;
    }

    private static ulong HashMatrix(float* m)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < 16; i++) { h ^= *(uint*)(m + i); h *= 1099511628211UL; }
        return h;
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

    /// <summary>#129: does the engine route this material to draw bucket 6, i.e. is this water?
    /// <para><c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880's own two terms, in its own order:
    /// <c>Material::HasBumpyShinyMap</c> gated on the global <c>g_nEnableBumpyWater</c> (which is what the
    /// engine tests before it looks at the SharedMaterial at all), else the <c>.mtr</c>'s
    /// <c>sample_framebuffer 2</c>. Value 1 of that same keyword is refraction glass and bucket 5, which is
    /// NOT water and which the owned pass still refuses.</para>
    /// <para>Read off the material rather than off the bucket on purpose: bucket membership is recomputed
    /// every frame while these two are material state, which is the rule <see cref="Render.OwnedClasses"/>
    /// states for the whole classifier.</para></summary>
    private static bool IsWater(IntPtr mat, IntPtr shared)
    {
        if (mat == IntPtr.Zero) return false;
        if (_bumpyWater != null && *_bumpyWater != 0 && _hasBumpyShiny(mat) != 0) return true;
        return shared != IntPtr.Zero && *(int*)(shared + SharedSampleFb) == SampleFbWater;
    }

    /// <summary><c>sample_framebuffer</c>'s two values: 1 routes to draw bucket 5 (refraction glass), 2 to
    /// bucket 6 (water). Different classes, not degrees of one.</summary>
    private const int SampleFbWater = 2;
    private static int* _bumpyWater;

    /// <summary>Draw buckets the owned pass takes parts from. 0/1 are the static tile pair (#82), and #126
    /// adds the three residual static classes named by <see cref="Render.OwnedClass"/>: 4 additive, 7 the
    /// tilefade cap, 8 the fade buckets. #134 and #128 then added the dynamic gob pair 2/3 -- drawn from
    /// Scene::RenderDynamicGeometry rather than from RenderSinglePass's own walk, but through the same
    /// RenderDrawBucket this service brackets, which is why owning placeables and then creatures needed no new
    /// capture point. What stays the engine's is 5, framebuffer-sampling geometry, and 6, water, both of
    /// which the owned pass has no program for and both of which <see cref="TryOwnCandidate"/> also rejects
    /// on the material so a mis-bucketed part cannot slip through.
    /// <para>Membership here and suppression in <c>OwnedDrawService.IterSight</c> are ONE list: suppressing a
    /// bucket we do not draw drops geometry, and drawing one we do not suppress double-draws it.</para></summary>
    internal static bool OwnableBucket(int bucket)
        => bucket == 0 || bucket == 1 || bucket == GobOpaqueBucket || bucket == GobBlendBucket
           || bucket == 4 || bucket == 7 || bucket == AlphaBucket
           || (bucket == Render.OwnedDrawService.WaterBucket && Render.WaterPass.Usable);

    /// <summary>#82 ownership enrollment: is the part in table slot <paramref name="e"/> in the ownable-now
    /// set (#72)? Non-skinned, no bumpy-shiny (both settled at Record), plus --
    /// checked here because they are material/instance state, not capture state -- no gob
    /// modifiers, indexed VDO, an <see cref="OwnableBucket"/>, a live index range, and sighted within the
    /// last frame. Neither a SharedMaterial nor the _nm/_sm map slots reject any more -- see the notes at
    /// their tests.
    /// <para>#126: opacity classifies rather than rejects. What still rejects on the material is the pair the
    /// owned pass has no program for -- <c>sample_framebuffer</c> (buckets 5/6) -- and <c>volumetric</c>,
    /// which the engine files in TWO buckets and draws twice per pass, so owning it would break the
    /// one-owner invariant by construction.</para>
    /// Cold: one call per table slot per enrollment.</summary>
    internal static bool TryOwnCandidate(int e, ref Render.OwnedCandidate c)
    {
        // #134: SOWX_DUMPTEX follows its texture into ENROLLMENT too. The census runs inside the bucket walk
        // and enrollment runs before it, so an entry can be ownable at one and refused at the other -- which
        // is exactly the shape of "ownable-but-not-enrolled every frame while the counters report nothing
        // refused". One line per matching entry per enrollment, and enrollment is every 60 frames.
        bool got = TryOwnCandidateInner(e, ref c);
        // Enrollment identifies the watched part by ENTRY INDEX, which the census hands it from the draw
        // side. Matching on the texture name here needed a Material and then a CAurTexture dereference, and
        // this table holds area-transition debris whose pointers are freed -- two crashes. Guarding that
        // with "live and freshly sighted" made it safe and useless in the same stroke: STALE is the state
        // under suspicion, so the guard silenced exactly the line worth reading.
        if (e == _watchEntry && !_ownDry)
            _log?.Info($"[dumpframe] ENROLL entry={e} -> {(got ? "candidate" : _ownGate ?? "?")} " +
                       $"cls={c.Cls} state={_pState[e]} seenAgo={_frame - _pSeen[e]} " +
                       $"bucket={(_pState[e] == StActive ? _gBucket[_pGroup[e]] : -1)} " +
                       $"vdo=0x{(ulong)_pVdo[e]:x} item={_pItem[e]} ownflagBefore={OwnFlag[e]}");
        return got;
    }

    /// <summary>Is this the table slot the census is watching? Index comparison only -- see the note in
    /// <see cref="TryOwnCandidate"/> for why nothing here may dereference a Material or a texture.</summary>
    internal static bool WatchedEntry(int e) => e >= 0 && e == _watchEntry;

    /// <summary>#134: adopt the watch from ENROLLMENT rather than from the census. Once a part is owned it
    /// stops reaching the engine's draw -- which is the point -- so the census that named the watch goes
    /// silent exactly when the interesting half of its life begins. Called only for a candidate the gates
    /// have already accepted, so the Material is live and the name lookup is safe.</summary>
    internal static bool WatchAccepted(int e, IntPtr mat)
    {
        if (_dumpTex == null || mat == IntPtr.Zero) return false;
        IntPtr t = (IntPtr)_getTexture(mat, 0);
        if (t == IntPtr.Zero || !TexName(t).Contains(_dumpTex, System.StringComparison.OrdinalIgnoreCase))
            return false;
        if (_watchEntry < 0) _watchEntry = e;
        WatchTex[e] = true;
        return true;
    }

    /// <summary>#134: entries whose diffuse matched SOWX_DUMPTEX at the last enrollment. A POPULATION, not a
    /// specimen: following one part answered "is this part owned" four rolls running while the question was
    /// "how much of what I am looking at is".</summary>
    internal static readonly bool[] WatchTex = new bool[MaxParts];

    /// <summary>Engine draws of a matching part, per frame -- counted before every gate, so a part drawn
    /// outside the bucket walk still lands here.</summary>
    internal static int WatchEngineDraws;

    private static bool TryOwnCandidateInner(int e, ref Render.OwnedCandidate c)
    {
        // the candidate struct is reused across the whole enrollment scan, and enrollment reads Gob back on a
        // REFUSAL -- so a gate that fires before the part loads must not leave the previous entry's gob behind
        c.Gob = IntPtr.Zero;
        if (!_active) return Gate("inactive");
        if (_pState[e] != StActive)
        {
            if (!_ownDry && _pGob[e] != IntPtr.Zero) _ownInstDead++;
            // #128: a part that died before enrollment could classify it still belongs to a creature, and a
            // creature is all-owned-or-none. The commonest case is a multi-draw part, which dies at Complete
            // as "multidraw-or-unindexed" -- own its siblings and one arm of that creature interleaves a
            // second renderer through every fade. Marked from the copy taken at Record, never from the dead
            // part, and only while it is still drawing, so a despawn clears itself within a frame.
            // ...and the gob must still be the one we copied. An identity-changed entry keeps its old _pGob
            // while a RECYCLED part at that address goes on refreshing the sighting, so without this a reused
            // gob pointer would sweep a live creature every enrollment. Same single-level read the death
            // branch already does, on a part that drew within the frame. The multi-draw case this mark exists
            // for passes it unchanged.
            if (!_ownDry && _pGob[e] != IntPtr.Zero && _pSeen[e] >= _frame - 1
                && _pPart[e] != IntPtr.Zero && *(IntPtr*)(_pPart[e] + PartGob) == _pGob[e])
                OwnRefuse(0, _pGob[e]);
            return Gate("state");
        }
        if (OwnFlag[e] != 0) return Gate("OWNED");
        if (!_ownDry) { _ownSeen++; if (_pGob[e] != IntPtr.Zero) _ownInstSeen++; }
        if (_pSeen[e] < _frame - 1)
        {
            if (!_ownDry && _pGob[e] != IntPtr.Zero) _ownInstStale++;
            Bump(ref _ownRejStale); return Gate("stale");
        }
        int g = _pGroup[e];
        if (!OwnableBucket(_gBucket[g])) { Bump(ref _ownRejBucket); return Gate("bucket"); }
        // #91: a part recorded from another Scene (a GUI 3D panel) must never enter the world table -- the
        // owned block draws the whole table in one go, so one foreign part is area geometry in a portrait.
        if (_gScene[g] != _ownScene) { Bump(ref _ownRejScene); return Gate("scene"); }
        IntPtr v = _pVdo[e], mat = _pMat[e], part = _pPart[e];
        // Published the moment the part is in hand, and kept through every refusal from here down: creature
        // colour is all-owned-or-none per instance (#107/#128), so a refusal has to be attributable to its
        // gob. The gates ABOVE this line need no attribution -- a stale or foreign-scene part is not drawing.
        c.Gob = *(IntPtr*)(part + PartGob);
        // A SharedMaterial is NOT a reason to hand a part back. Ghidra Material::GetTexture @0x4c5810
        // resolves every slot through it already (shared+0xd8+i*0x10+8, else the material's own), so the
        // texture we captured is the right one. All it adds on top is a custom shader program
        // (SharedMaterial::BindCustomShader @0x4c5590) -- and that early-returns when both program slots are
        // null, so a redirect-only shared material costs nothing at all. #123 takes the next slice: the seven
        // SoW custom programs are recognised by their customShaderVS name and reproduced as feature switches
        // in the owned program, so they are exact too. What is left over is still approximated with the
        // generic lit program -- the deliberate trade, one renderer on screen beats a correct half -- and the
        // three-way split is what says how much of it there is, and which shaders.
        IntPtr shared = *(IntPtr*)(mat + MatShared);
        // #126: sample_framebuffer and volumetric reject on the MATERIAL, ahead of everything else, because
        // they are the two classes the owned pass has no answer for: the first is buckets 5/6 (refraction
        // glass and water, both of which read a framebuffer copy this pass does not take yet), and the second
        // the engine files in two buckets and draws twice per pass, which no one-owner scheme survives. Both
        // are SharedMaterial keywords parsed straight out of the .mtr (SharedMaterial::ParseField @0x4c6760).
        // #129: ...except sample_framebuffer 2, which IS water and which the owned pass now draws. The two
        // values are different classes, not degrees of one: 1 is refraction glass (draw bucket 5) and 2 is
        // water (bucket 6), and Scene::AddPartsTreeToDrawBuckets @0x460880 branches on exactly that.
        bool water = IsWater(mat, shared);
        if (water) NoteWaterMaterial(mat, shared);
        if (shared != IntPtr.Zero
            && ((*(int*)(shared + SharedSampleFb) != 0 && !water) || *(byte*)(shared + SharedVolumetric) != 0))
        { Bump(ref _ownRejFbSample); return Gate("fb/volumetric"); }
        // ...and the classification itself, which from here on is what a refusal is counted AGAINST: the
        // blended class is all-owned-or-none (#107 s2), so every gate below has to say which class it ate.
        c.Src = *(int*)(mat + MatSrcBlend); c.Dst = *(int*)(mat + MatDstBlend);
        // The cap bucket overrides the material: RenderSinglePass draws bucket 7 with Disable(0) and
        // Disable(1) -- blending and the alpha discard both off -- so a cap is an opaque draw whatever its
        // material declares.
        c.Cls = _gBucket[g] == CapBucket ? (int)Render.OwnedClass.Opaque
              : (int)Render.OwnedClasses.Of(water, c.Dst == BlendOne, _isOpaque(part) != 0,
                    shared != IntPtr.Zero && *(byte*)(shared + SharedTransparency) != 0,
                    *(float*)(part + PartAlpha) < 1f);
        if (!_ownDry) _ownByClass[c.Cls]++;
        if (v == IntPtr.Zero || *(int*)(v + VdoMode) != 1 || _pTex0[e] == IntPtr.Zero)
        { Refuse(ref _ownRejVdo, c); return Gate("vdo"); }
        c.Feat = 0;
        if (shared != IntPtr.Zero)
        {
            byte* vs = (byte*)(shared + SharedVsName);
            if (*vs == 0) Bump(ref _ownSharedPlain);
            else if ((c.Feat = Classify(vs)) != 0) Bump(ref _ownSharedFeature);
            else { Bump(ref _ownSharedShader); if (!_ownDry) NoteUnknown(vs); }
        }
        // #134/#128: an instance is ownable, and so is a MODULATED one -- PartTriMesh::Mat @0x450960's three
        // terms are folded live by the draw loop now. What still refuses is the handful of instance state no
        // owned program can express at all.
        if (c.Gob != IntPtr.Zero && GobUnownable(c.Gob))
        { Refuse(ref _ownRejGob, c); return Gate("gob-unownable"); }
        // Slots 1/2 are the normal and spec/roughness maps of the fslit_nm/_sm variants. #120 made the owned
        // lit stage read them (and slots 3/4/5) rather than bind slot 0 alone, so this is now a coverage
        // count of how much of the owned set is PBR content -- never a reason to reject.
        if (_getTexture(mat, 1) != 0 || _getTexture(mat, 2) != 0) Bump(ref _ownWithMaps);
        // #128: the engine's own routing condition, both halves -- PartTriMesh::Draw @0x4513d0 takes
        // RenderEnvironmentMapped when EITHER the instance overrides the env map or the material declares one.
        c.Env = _hasEnvMap(mat) != 0 || GobHasEnvMap(c.Gob);
        if (c.Env) Bump(ref _ownWithEnv);
        long elem = (long)*(IntPtr*)(v + VdoElemItems);
        int it = _pItem[e];
        if (elem == 0 || it < 0 || it >= *(int*)(v + VdoElemCount) || it >= *(int*)(v + VdoVertCount))
        { Refuse(ref _ownRejItem, c); return Gate("item"); }
        if (*(int*)(elem + it * 8 + 4) <= 0) { Refuse(ref _ownRejItem, c); return Gate("item-range"); }

        c.Bucket = _gBucket[g];
        c.Tri = part; c.Node = _pNode[e]; c.Mat = mat; c.Vdo = v; c.Tex0 = _pTex0[e];
        c.Item = it; c.Prim = _pPrim[e]; c.RotBits = _pRot[e]; c.Mx = _pMx[e];
        c.Lit = _gLight[g];
        _ownGate = null;
        return true;
    }

    /// <summary>Record which gate refused, and refuse. #134: the census used to ask a hand-written MIRROR of
    /// the chain above the same question, and the mirror's own doc comment warned that the two would be
    /// worthless the day they disagreed. They disagreed within the week -- the mirror kept a blanket gob
    /// reject that the real predicate had already narrowed to "gob MODULATES", so the census blamed a gate
    /// nobody was failing and cost a round trip. There is one chain now.</summary>
    private static bool Gate(string why) { _ownGate = why; return false; }
    private static void Bump(ref int counter) { if (!_ownDry) counter++; }
    private static void Refuse(ref int counter, in Render.OwnedCandidate c)
    { if (!_ownDry) { counter++; OwnRefuse(c.Cls, c.Gob); } }
    private static string? _ownGate;
    /// <summary>Set while the census asks the real predicate a hypothetical: same gates, no counters, no
    /// unknown-shader notes.</summary>
    private static bool _ownDry;

    /// <summary>Which gate would refuse this entry, by name -- the real chain, run for its answer only.</summary>
    internal static string WhyNotOwnable(int e)
    {
        _ownDry = true;
        var scratch = default(Render.OwnedCandidate);
        scratch.Cls = -1;
        bool ok = TryOwnCandidate(e, ref scratch);
        _ownDry = false;
        string cls = scratch.Cls < 0 ? "?" : ((Render.OwnedClass)scratch.Cls).ToString();
        return $"{(ok ? "ownable-but-not-enrolled" : _ownGate ?? "?")} cls={cls}";
    }

    /// <summary>Gob fields that change how a part shades, all read by <c>PartTriMesh::Mat</c> @0x450960
    /// except the env override, which <c>PartTriMesh::Draw</c> @0x4513d0 reads to reroute the whole draw to
    /// <c>RenderEnvironmentMapped</c>. The two flags ARM a modulation; the values beside them are the
    /// modulation, and <c>gob+0xfc</c> is the colour whose 12 bytes end exactly where the alpha begins.</summary>
    private const int GobHighlight = 0x18, GobSceneTint = 0x19, GobColourMul = 0xf6, GobSelfIllum = 0xf8,
                      GobFadeOwner = 0x68, GobEnvOverride = 0x480,
                      GobColour = 0xfc, GobAlpha = 0x108, GobFadeAlpha = 0x10c, GobEmissive = 0x110;
    /// <summary>The byte on the gob's owner (<c>gob+0x68</c>) whose bit 3 arms a second alpha multiply
    /// (<c>gob+0x10c</c>) -- Ghidra <c>PartTriMesh::Mat</c>: <c>if ((*(*(gob+0x68)+0x8e) &amp; 8) != 0)</c>.</summary>
    private const int GobOwnerFlags = 0x8e, GobOwnerFadeBit = 8;

    /// <summary>#128: does this instance carry state no owned program can reproduce? TWO cases, and only
    /// two -- the highlight modes, which do not shade at all but paint a selection colour sampled from the
    /// Scene.
    /// <para>Everything else <c>PartTriMesh::Mat</c> @0x450960 does with a gob is arithmetic on terms the
    /// SowxDraw record already carries -- an alpha multiply, a component-wise colour multiply and a self-illum
    /// add -- so #128 FOLDS them (see <see cref="GobAlphaMul"/> and its two neighbours) rather than refusing
    /// the part. That fold is what makes creatures ownable at all: a creature fades through <c>gob+0x108</c>,
    /// so under #134's rule "modulating" and "on screen and interesting" were the same population.</para>
    /// <para>#134's own finding still holds and is why the flags below are read as VALUES, not as arms:
    /// <c>gob+0xf6</c> is set on essentially every instance in the game while its colour is (1,1,1). The
    /// cases here refuse whatever their values, because neither is a multiply at all.</para>
    /// <para>The per-gob ENV OVERRIDE used to refuse here and was the gate that actually kept the whole
    /// instanced population out -- a live census put it on 2814 of 3110 instanced part draws, against 296
    /// owned, so #134's placeables were never really owned either. It is not a special class:
    /// <c>PartTriMesh::Draw</c> @0x4513d0 routes on <c>gob+0x480 || Material::HasEnvMap</c> into ONE function,
    /// and <c>RenderEnvironmentMapped</c> @0x451040 picks gob+0x480 else mat+0x40 else the scene's own
    /// <c>currentscene+0x1e8</c>, binds it, and then calls <c>RenderFlat</c> anyway. It is the env FLAG with a
    /// different source for the texture, and #121's owned path already replaces that texture with the per-area
    /// IBL probe. So it sets <see cref="Render.OwnedCandidate.Env"/> now, exactly as a material env map does.
    /// <para>ponytail: RenderEnvironmentMapped also passes <c>part+0xa0 * gob+0x108</c> as the ambient
    /// intensity to <c>SetPixelShaderAmbientColor</c> -- the reflection's STRENGTH. Not reproduced; the owned
    /// record stages ambient.w as 1. Fold it into that slot if instanced content ever reads too shiny.</para></para>
    /// Cold at enrollment, hot once per owned part per frame via <see cref="InstanceOwnable"/>.</summary>
    private static bool GobUnownable(IntPtr gob)
        => *(byte*)(gob + GobHighlight) != 0 || *(byte*)(gob + GobSceneTint) != 0;

    /// <summary>Does this instance override the env map (<c>gob+0x480</c>)? The engine's own routing test in
    /// <c>PartTriMesh::Draw</c> @0x4513d0, ORed with the material's own -- either one sends the draw through
    /// <c>RenderEnvironmentMapped</c>, and the owned pass answers both the same way.</summary>
    internal static bool GobHasEnvMap(IntPtr gob)
        => gob != IntPtr.Zero && *(IntPtr*)(gob + GobEnvOverride) != IntPtr.Zero;

    /// <summary>The instance's live alpha multiplier: <c>PartTriMesh::Mat</c>'s two multiplies onto
    /// <c>part+0xa0</c>, armed by <c>gob+0xf6</c> and by bit 3 of the owner byte at <c>*(gob+0x68)+0x8e</c>.
    /// Read EVERY frame and never at capture -- this is the field that fades a whole creature into the
    /// transparent run for as little as a single frame, which is why #128 could not cache opacity.</summary>
    internal static float GobAlphaMul(IntPtr part)
    {
        IntPtr gob = *(IntPtr*)(part + PartGob);
        if (gob == IntPtr.Zero) return 1f;
        float a = *(short*)(gob + GobColourMul) != 0 ? *(float*)(gob + GobAlpha) : 1f;
        IntPtr owner = *(IntPtr*)(gob + GobFadeOwner);
        if (owner != IntPtr.Zero && (*(byte*)(owner + GobOwnerFlags) & GobOwnerFadeBit) != 0)
            a *= *(float*)(gob + GobFadeAlpha);
        return a;
    }

    /// <summary>The instance's live colour multiply -- <c>cmul</c> @0x5b3a00 onto the diffuse term, armed by
    /// the same <c>gob+0xf6</c>. A component-wise product, so the identity is (1,1,1).</summary>
    internal static System.Numerics.Vector3 GobDiffuseMul(IntPtr part)
    {
        IntPtr gob = *(IntPtr*)(part + PartGob);
        if (gob == IntPtr.Zero || *(short*)(gob + GobColourMul) == 0) return System.Numerics.Vector3.One;
        float* c = (float*)(gob + GobColour);
        return new System.Numerics.Vector3(c[0], c[1], c[2]);
    }

    /// <summary>The instance's live self-illumination term, armed by <c>gob+0xf8</c>. An ADD, so the identity
    /// is zero -- getting that backwards paints every plain instance white.</summary>
    internal static System.Numerics.Vector3 GobEmissiveAdd(IntPtr part)
    {
        IntPtr gob = *(IntPtr*)(part + PartGob);
        if (gob == IntPtr.Zero || *(short*)(gob + GobSelfIllum) == 0) return default;
        float* e = (float*)(gob + GobEmissive);
        return new System.Numerics.Vector3(e[0], e[1], e[2]);
    }

    /// <summary>Hot (once per owned part per frame): may this part's instance still be drawn by the owned
    /// pass at all? A part with no gob trivially may, which is the tile case and the only load most calls do.
    /// <para>#128: the states this asks about are TRANSIENT -- a placeable highlights for exactly as long as
    /// the cursor is over it -- so the draw loop SKIPS on a false rather than revoking, and the engine's own
    /// walk draws it highlighted. Revoking would spend a full re-enrollment on a hover.</para></summary>
    internal static bool InstanceOwnable(IntPtr part)
    {
        IntPtr gob = *(IntPtr*)(part + PartGob);
        return gob == IntPtr.Zero || !GobUnownable(gob);
    }

    /// <summary>#93 caster enrollment: is the part in table slot <paramref name="e"/> a rigid shadow caster?
    /// Deliberately looser than <see cref="TryOwnCandidate"/> -- a depth pass has no material, so env maps,
    /// SharedMaterial, gob modifiers and the missing diffuse slot are all irrelevant. What remains is what
    /// the depth draw actually needs: an indexed (mode-1) VDO with a live index range, a class that casts
    /// (opaque, or -- since the cutout caster below -- alpha-tested), and a recent sighting. Buckets 0..4 pass, which
    /// is exactly what makes tiles (0/1) and placeables (2/3/4) one set with no kind branching; skinned parts
    /// fall out at the mode-1 test and stay #96's problem. Cold: one call per table slot per enrollment.</summary>
    /// <summary>#151: will the caster CENSUS carry this entry? The cheap prefix of
    /// <see cref="TryCasterCandidate"/>'s acceptance -- state, sighting, bucket, world -- used by the owned
    /// pass to avoid double-recording a part the table already casts. An entry probation KILLED (the moving
    /// creature piece) fails here, which is exactly the population the per-frame recast exists for.</summary>
    internal static bool CasterCensusCovers(int e)
    {
        if ((uint)e >= MaxParts || !_active || _pState[e] != StActive) return false;
        if (_pSeen[e] < _frame - 1) return false;
        int g = _pGroup[e];
        return _gBucket[g] <= MaxCasterBucket && _gWorld[g];
    }

    internal static bool TryCasterCandidate(int e, ref Shadow.CasterCandidate c)
    {
        if (!_active || _pState[e] != StActive) return false;
        if (_pSeen[e] < _frame - 1) return false;
        int g = _pGroup[e];
        if (_gBucket[g] > MaxCasterBucket) return false;
        // #91, the caster half: a GUI 3D panel's parts sit at that panel's node coordinates, so enrolling one
        // as a caster paints a phantom shadow into the world from geometry that exists only in a portrait.
        // Caster enrollment runs at the present with no Scene in hand, hence the flag rather than a comparison.
        if (!_gWorld[g]) return false;
        IntPtr v = _pVdo[e], part = _pPart[e];
        if (v == IntPtr.Zero || *(int*)(v + VdoMode) != 1) return false;
        // #126 follow-up -- the cutout caster. "Opaque geometry casts, transparent geometry does not" was
        // the vanilla-shaped rule, and vanilla only ever cast authored stencil volumes, so foliage cast
        // NOTHING and the question never came up. A rasterizing caster pass can do better: the 0.2 cutout
        // that makes a leaf a leaf in the colour pass makes it a leaf in the shadow map too.
        // So the class decides, not the opacity bit:
        //   Opaque  -- casts solid, no texture fetch (the majority).
        //   Cutout  -- casts ALPHA-TESTED. New, and the whole point of this.
        //   Blended -- does not cast. A binary shadow map can only say "fully shadowed" about a surface that
        //              is by definition partly see-through; that wants transmittance (M4), not a lie.
        //   Additive-- does not cast. It emits light.
        IntPtr mat = _pMat[e], shared = mat == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(mat + MatShared);
        if (shared != IntPtr.Zero
            && (*(int*)(shared + SharedSampleFb) != 0 || *(byte*)(shared + SharedVolumetric) != 0)) return false;
        bool env = mat != IntPtr.Zero && _hasEnvMap(mat) != 0;
        var cls = mat == IntPtr.Zero ? Render.OwnedClass.Opaque
                // ...never water: the sample_framebuffer refusal above already sent it back, and bucket 6 is
                // above MaxCasterBucket anyway, so nothing in the water class reaches the caster pass.
                : Render.OwnedClasses.Of(false,
                                         *(int*)(mat + MatDstBlend) == BlendOne, _isOpaque(part) != 0,
                      shared != IntPtr.Zero && *(byte*)(shared + SharedTransparency) != 0,
                      *(float*)(part + PartAlpha) < 1f);
        if (cls != Render.OwnedClass.Opaque && cls != Render.OwnedClass.Cutout) return false;
        long elem = (long)*(IntPtr*)(v + VdoElemItems);
        int it = _pItem[e];
        if (elem == 0 || it < 0 || it >= *(int*)(v + VdoElemCount) || it >= *(int*)(v + VdoVertCount)) return false;
        if (*(int*)(elem + it * 8 + 4) <= 0) return false;

        c.Tri = part; c.Node = _pNode[e]; c.Mat = _pMat[e]; c.Vdo = v;
        c.Item = it; c.Prim = _pPrim[e]; c.Mx = _pMx[e];
        // WHETHER a caster is alpha-tested is decided by its texture, inside the bracket (CasterPass's
        // TexHasAlpha) -- not here, and never from the engine's opacity flags, which short-circuit on an env
        // map, on the gob alpha and on their own memo. What IS decided here is the THRESHOLD, and only one
        // authoring makes a difference to it: an env-mapped material's diffuse alpha is reflectivity rather
        // than coverage (inc_standard.shd:248), so it must not be allowed to cut holes in a solid surface's
        // shadow at the cutout threshold.
        c.EnvAlpha = env;
        c.Dangly = _pDangly[e];
        return true;
    }

    /// <summary>Highest draw bucket a shadow caster may live in. 0/1 are the static tile buckets and 2/3 the
    /// dynamic gob pair (opaque/transparent, drawn inside <c>Scene::RenderDynamicGeometry</c>); above that
    /// nothing casts -- 5 is Sample Framebuffer, 6 is water, 7 the tilefade cap, 8 Fade (#103 named these off
    /// the engine's own Tracy scope literals; the older "5+ is water, 8 is per-item alpha" note was wrong).
    /// ponytail: 4 is Additive and arguably should not cast either, but narrowing this is a behaviour change,
    /// not a comment fix -- decide it when the owned pipeline takes the residual buckets.</summary>
    private const int MaxCasterBucket = 4;

    /// <summary>#125: does this material's SharedMaterial name a custom shader the owned pass does not
    /// implement? For world geometry an unknown custom shader is owned anyway and drawn with the plain
    /// stage, which is usually a small difference on one part. The sky is the case where it is not: a
    /// dynamic skybox IS its fragment shader (sow-topdata's <c>fst_sky</c> is ~1900 lines of procedural
    /// gradient, clouds, stars, sun and moon over a near-black night texture), so drawing it flat sends the
    /// diffuse alone and the sky comes out black.</summary>
    internal static bool HasUnportedShader(IntPtr mat)
    {
        if (mat == IntPtr.Zero) return false;
        IntPtr shared = *(IntPtr*)(mat + MatShared);
        if (shared == IntPtr.Zero) return false;
        // The VS name decides when there is one -- that is #123's model, and a recognised feature covers
        // its fragment half by construction. What the VS test alone MISSES is a .mtr that names only
        // customShaderFS (+0x199, the next resref slot after +0x188 per SharedMaterial::ParseField
        // @0x4c6760): none of the seven features is authored that way, so an FS with no VS is a custom
        // program by definition -- and the black sky is a fragment shader.
        byte* vs = (byte*)(shared + SharedVsName);
        if (*vs != 0) return Classify(vs) == 0;
        return *(byte*)(shared + SharedFsName) != 0;
    }

    /// <summary>#123: match a SharedMaterial's <c>customShaderVS</c> resref against the seven the owned
    /// program reproduces as feature switches. Case-insensitive over the whole NUL-terminated name, so
    /// <c>shd_roofkn_vs</c> cannot swallow <c>shd_roofknm_vs</c>. Cold: one call per shared-material part per
    /// enrollment scan.</summary>
    private static int Classify(byte* vs)
    {
        var names = Render.ShaderFeatures.VsNames;
        for (int f = 1; f < names.Length; f++)
        {
            string n = names[f];
            int i = 0;
            for (; i < n.Length; i++)
            {
                byte a = vs[i], b = (byte)n[i];
                if (a >= 'A' && a <= 'Z') a += 32;
                if (a != b) break;
            }
            if (i == n.Length && vs[i] == 0) return f;
        }
        return 0;
    }

    /// <summary>Keep the first few unrecognised custom-shader names so the diag can NAME what is still
    /// approximated rather than only counting it -- a number says work remains, a name says which shader.</summary>
    private static void NoteUnknown(byte* vs)
    {
        if (_unknownCount >= MaxUnknown) return;
        int len = 0;
        while (len < SharedNameLen - 1 && vs[len] != 0) len++;
        // Dedupe on the RAW BYTES first. Every shared-material part reaches this, so allocating a string per
        // part and throwing all but the first away would put hundreds of allocations in an enrollment scan
        // for a diag line that holds at most six.
        for (int i = 0; i < _unknownCount; i++)
        {
            string k = _unknown[i];
            if (k.Length != len) continue;
            int j = 0;
            while (j < len && k[j] == (char)vs[j]) j++;
            if (j == len) return;
        }
        _unknown[_unknownCount++] = System.Text.Encoding.ASCII.GetString(vs, len);
    }

    /// <summary>Distinct unrecognised custom-shader names the diag names. A handful is a report; the whole
    /// set would be a wall, and past a handful the answer is "port more features", not "list them all".</summary>
    private const int MaxUnknown = 6;
    private static readonly string[] _unknown = new string[MaxUnknown];
    private static int _unknownCount;

    /// <summary>#126: candidates seen per <see cref="Render.OwnedClass"/>, and the ones a gate refused after
    /// they were classified. The second array is what the all-owned-or-none rule reads: a class with a
    /// refusal cannot be owned in part, because its draw order is load-bearing (#107 s2). OwnedDrawService
    /// adds its OWN refusals (capacity, VAO slot, unshadeable lit) to it, so one array covers both scans.</summary>
    private static readonly int[] _ownByClass = new int[Render.OwnedClasses.Count];
    internal static readonly int[] OwnRefusedByClass = new int[Render.OwnedClasses.Count];
    /// <summary>#128: gobs with at least one refused part this enrollment, open-addressed (power of two,
    /// linear probe). Creature colour is all-owned-or-none per INSTANCE, so a creature carrying one part we
    /// cannot draw -- a true skinmesh cloak, an env override, a full table -- has to go back to the engine
    /// entire; this set is what the enrollment sweep asks. Sized well above the instance count of a crowded
    /// exterior, and never dereferenced: a Gob* here is an identity, not an object.</summary>
    /// <summary>#128 coverage: how the INSTANCED population fares through the enrollment scan, which the
    /// per-gate totals cannot answer -- they mix instances in with ten times as many tiles, and the two gates
    /// an instance actually dies at (the state gate, and staleness) are the two that say nothing about kind.
    /// Counted off <see cref="_pGob"/>, which is a copy taken at Record and never dereferenced, so it is safe
    /// on exactly the dead and stale entries this is asking about.</summary>
    private static int _ownInstSeen, _ownInstStale, _ownInstDead;
    /// <summary>Instanced parts ever Recorded. Session-cumulative on purpose: a zero here means instances are
    /// not reaching PartTriMesh::Draw at all, which is a different bug from every gate below.</summary>
    private static int _pInstRecorded;

    private const int RefGobHash = 4096;
    private static readonly IntPtr[] _refGob = new IntPtr[RefGobHash];
    private static int _refGobCount;
    /// <summary>The set filled up, so "not in the set" no longer means "not refused". The sweep falls back to
    /// dropping EVERY instanced part rather than half-owning a creature -- correct, drastic, and loud on the
    /// period line, which is the right shape for a state that should not occur.</summary>
    internal static bool RefusedGobOverflow;

    /// <summary>Count one enrollment refusal: against the GOB when the part instances off one (the per-instance
    /// sweep takes it from there), against its CLASS when it does not. The two must not be conflated -- the
    /// class-wide blended drop exists for statics, and letting one tinted creature trip it would flap every
    /// blended wall in the area back to vanilla for an enrollment.</summary>
    internal static void OwnRefuse(int cls, IntPtr gob)
    {
        if (gob == IntPtr.Zero) { OwnRefusedByClass[cls]++; return; }
        if (_refGobCount * 2 >= RefGobHash) { RefusedGobOverflow = true; return; }
        int h = (int)(((ulong)gob >> 4) & (RefGobHash - 1));
        while (_refGob[h] != IntPtr.Zero)
        {
            if (_refGob[h] == gob) return;
            h = (h + 1) & (RefGobHash - 1);
        }
        _refGob[h] = gob; _refGobCount++;
    }

    /// <summary>Did any part of this instance get refused this enrollment? Cold: once per owned slot, in the
    /// enrollment sweep.</summary>
    internal static bool GobRefused(IntPtr gob)
    {
        if (gob == IntPtr.Zero) return false;
        if (RefusedGobOverflow) return true;
        int h = (int)(((ulong)gob >> 4) & (RefGobHash - 1));
        while (_refGob[h] != IntPtr.Zero)
        {
            if (_refGob[h] == gob) return true;
            h = (h + 1) & (RefGobHash - 1);
        }
        return false;
    }

    /// <summary>Instances the sweep will drop whole this enrollment -- the number that says how much of the
    /// creature population one unownable class is taking with it.</summary>
    internal static int RefusedGobs => _refGobCount;

    /// <summary>#128, off-device (run-demo.sh): the two halves of the instance model that fail SILENTLY.
    /// <para>The FOLD is a pile of hardcoded offsets into <c>PartTriMesh::Mat</c>'s gob, and every one of them
    /// has an identity that looks exactly like "this instance does not modulate" when it is really "we read
    /// the wrong field" -- an alpha that stays 1, a colour that stays white. Driven here against a synthetic
    /// gob whose fields are set one at a time, so a transposed offset reads as a failure rather than as a
    /// creature that never fades.</para>
    /// <para>The refused-gob SET is the all-owned-or-none sweep's whole memory: a lookup that misses does not
    /// error, it half-owns a creature. Insert, dedup, miss and the class-vs-gob split are checked directly.</para>
    /// False = do not arm the owned pass, per the house rule.</summary>
    internal static bool GobSelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"BatchService.GobSelfCheck FAIL: {w}"); } }

        byte* gob = stackalloc byte[0x500];
        byte* owner = stackalloc byte[0x100];
        byte* part = stackalloc byte[0x100];
        for (int i = 0; i < 0x500; i++) gob[i] = 0;
        for (int i = 0; i < 0x100; i++) { owner[i] = 0; part[i] = 0; }
        *(IntPtr*)(part + PartGob) = (IntPtr)gob;
        IntPtr p = (IntPtr)part;

        // an unarmed gob is the identity of all three terms -- the case every plain instance takes
        E(!GobUnownable((IntPtr)gob), "a bare gob is ownable");
        E(GobAlphaMul(p) == 1f, "alpha identity is 1");
        E(GobDiffuseMul(p) == System.Numerics.Vector3.One, "colour identity is (1,1,1) -- it is a MULTIPLY");
        E(GobEmissiveAdd(p) == default, "self-illum identity is 0 -- it is an ADD");
        // ...and so is a part with no gob at all, without dereferencing anything
        *(IntPtr*)(part + PartGob) = IntPtr.Zero;
        E(GobAlphaMul(p) == 1f && GobDiffuseMul(p) == System.Numerics.Vector3.One, "a gobless part folds nothing");
        *(IntPtr*)(part + PartGob) = (IntPtr)gob;

        // the colour/alpha pair, armed together by gob+0xf6 and dead without it
        *(float*)(gob + GobAlpha) = 0.5f;
        *(float*)(gob + GobColour) = 0.25f;
        *(float*)(gob + GobColour + 4) = 1f;
        *(float*)(gob + GobColour + 8) = 1f;
        E(GobAlphaMul(p) == 1f && GobDiffuseMul(p) == System.Numerics.Vector3.One, "unarmed values do not apply");
        *(short*)(gob + GobColourMul) = 1;
        E(GobAlphaMul(p) == 0.5f, "armed gob alpha multiplies");
        E(GobDiffuseMul(p) == new System.Numerics.Vector3(0.25f, 1f, 1f), "armed gob colour multiplies, in order");

        // the SECOND alpha multiply, armed on the gob's OWNER rather than on the gob -- the fade that moves a
        // whole creature into the transparent run, and the one term whose flag is not on the object it scales
        *(float*)(gob + GobFadeAlpha) = 0.5f;
        E(GobAlphaMul(p) == 0.5f, "the fade alpha is dead without its owner");
        *(IntPtr*)(gob + GobFadeOwner) = (IntPtr)owner;
        E(GobAlphaMul(p) == 0.5f, "...and dead without the owner's fade bit");
        owner[GobOwnerFlags] = GobOwnerFadeBit;
        E(GobAlphaMul(p) == 0.25f, "both multiplies compose");

        *(short*)(gob + GobSelfIllum) = 1;
        *(float*)(gob + GobEmissive + 4) = 2f;
        E(GobEmissiveAdd(p) == new System.Numerics.Vector3(0f, 2f, 0f), "armed self-illum adds, in order");

        // ...and none of that made the instance unownable, which is the whole of #128 over #134
        E(!GobUnownable((IntPtr)gob), "a MODULATING instance is still ownable: the terms are folded, not refused");
        gob[GobHighlight] = 1;
        E(GobUnownable((IntPtr)gob), "a highlight is not");
        gob[GobHighlight] = 0; gob[GobSceneTint] = 1;
        E(GobUnownable((IntPtr)gob), "nor a scene tint");
        gob[GobSceneTint] = 0;
        E(!GobHasEnvMap((IntPtr)gob), "a bare gob overrides no env map");
        *(IntPtr*)(gob + GobEnvOverride) = (IntPtr)owner;
        E(!GobUnownable((IntPtr)gob), "a per-instance env override is OWNABLE -- it is the env flag, not a class");
        E(GobHasEnvMap((IntPtr)gob), "...and it reports as one, so the candidate takes #121's env path");
        E(!GobHasEnvMap(IntPtr.Zero), "a gobless part overrides nothing, without dereferencing");

        // the refused-gob set, over pointers it never dereferences
        Array.Clear(_refGob); _refGobCount = 0; RefusedGobOverflow = false;
        Array.Clear(OwnRefusedByClass);
        E(!GobRefused((IntPtr)gob), "an empty set refuses nothing");
        E(!GobRefused(IntPtr.Zero), "and a gobless part is never in it");
        OwnRefuse(2, (IntPtr)gob);
        OwnRefuse(2, (IntPtr)gob);
        E(GobRefused((IntPtr)gob) && _refGobCount == 1, "a gob is remembered once, however often it is refused");
        E(!GobRefused((IntPtr)owner), "and only the gob that was refused");
        E(OwnRefusedByClass[2] == 0, "a GOB refusal never trips the class-wide blended drop");
        OwnRefuse(2, IntPtr.Zero);
        E(OwnRefusedByClass[2] == 1, "...and a static refusal still does");
        Array.Clear(_refGob); _refGobCount = 0; RefusedGobOverflow = false;
        Array.Clear(OwnRefusedByClass);

        // #149: the group-key intern. The linear walk it replaced IS the specification, so every lookup here
        // is done BOTH ways and the two must agree -- including the pair a hash gets wrong on its own: a
        // matrix differing in one float (a different group) and a -0/+0 pair (the SAME group, because
        // -0f != 0f is false, so the comparison calls them equal while their bits differ).
        int savedG = _groupCount, savedB = _bucketId, savedPh = _phase;
        IntPtr savedSc = _bucketScene;
        try
        {
            _groupCount = 0; _bucketId = 0; _phase = 0; _bucketScene = IntPtr.Zero;
            _gSlot.AsSpan().Fill(-1);
            float* m = stackalloc float[16];
            for (int i = 0; i < 16; i++) m[i] = i;

            E(SelfGroup(1, m, E) == 0 && _groupCount == 1, "a fresh key mints group 0");
            E(SelfGroup(1, m, E) == 0 && _groupCount == 1, "...and the same key interns to it, not to a second");
            E(SelfGroup(2, m, E) == 1 && _groupCount == 2, "a different VDO is a different group");
            m[7] = 99f;
            E(SelfGroup(1, m, E) == 2 && _groupCount == 3, "a matrix differing in ONE float is a different group");
            m[7] = 0f;
            int plus = SelfGroup(1, m, E);
            m[7] = -0f;
            E(SelfGroup(1, m, E) == plus, "-0 and +0 in the matrix are the SAME group");

            // ...and the SHIFT spreads across the whole table. This one is invisible to any correctness
            // oracle -- a mis-bound shift still returns the right group, it just crams every key into the
            // first 1/N of the slots and turns O(1) probing back into a linear walk. It has now been a live
            // bug twice (TexKey's hardcoded >>53, and this table inherited the pattern), so it is asserted
            // rather than trusted: distinct keys must reach the upper half of the table.
            int top = 0;
            for (int i = 0; i < 256; i++)
            {
                ulong hh = GroupHash((IntPtr)(0x1000 + i * 0x30), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                     IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 0, false, false, false, m);
                int sl = (int)(hh * 0x9E3779B97F4A7C15UL >> (64 - GroupHashBits));
                if (sl > top) top = sl;
            }
            E(top >= GroupHashSize / 4,
              $"the group hash spreads over the whole table (highest of 256 slots {top} of {GroupHashSize}) " +
              "-- a shift not bound to GroupHashBits clusters every key into the low slots");
        }
        finally
        {
            _groupCount = savedG; _bucketId = savedB; _phase = savedPh; _bucketScene = savedSc;
            _gSlot.AsSpan().Fill(-1);
        }

        // The table-pressure decision, which is the whole of the "renderer keeps falling back to vanilla in
        // big areas" fix and is otherwise only observable on a live client in a big area. Runs on the real
        // arrays and restores them: nothing is recorded yet at self-check time, so there is nothing to lose,
        // but the counters are put back regardless in case that ever stops being true.
        int savedParts = _partCount, savedGroups = _groupCount, savedGen = TableGen;
        bool savedSat = _saturated;
        try
        {
            // a table full of the LIVE is a big area: saturate, never wipe
            for (int i = 0; i < 64; i++) _pState[i] = StActive;
            for (int i = 64; i < 96; i++) _pState[i] = StDead;
            _saturated = false; _log = null;
            OnTablePressure();
            E(_saturated && TableGen == savedGen, "a mostly-LIVE table saturates and is NOT wiped");
            E(_satLive == 64 && _satDead == 32, "...and the census it reports is the one it measured");
            // ...and a table full of the DEAD is transition debris: wipe, which is what the wipe is for
            for (int i = 0; i < 64; i++) _pState[i] = StDead;
            OnTablePressure();
            E(!_saturated && TableGen == savedGen + 1, "a mostly-DEAD table is wiped as transition debris");
        }
        finally
        {
            Array.Clear(_pState); Array.Clear(_pPart); Array.Clear(OwnFlag);
            _partCount = savedParts; _groupCount = savedGroups; TableGen = savedGen; _saturated = savedSat;
            _log = log;
        }

        log.Info(ok ? "BatchService.GobSelfCheck: passed" : "BatchService.GobSelfCheck: FAILED");
        return ok;
    }

    // per-gate enrollment forensics: which predicate is eating the candidates? (cold, scan-scoped)
    private static int _ownSeen, _ownRejStale, _ownRejBucket, _ownRejVdo, _ownRejGob, _ownRejFbSample,
                      _ownWithMaps, _ownRejItem, _ownSharedPlain, _ownSharedShader, _ownRejScene,
                      _ownWithEnv, _ownSharedFeature;
    private static IntPtr _ownScene;

    /// <summary>Reset the enrollment reject counters and set the Scene this scan enrolls for; call once
    /// before an enrollment scan. Only groups recorded from <paramref name="scene"/> are candidates (#91).</summary>
    internal static void OwnScanBegin(IntPtr scene)
    {
        _ownScene = scene;
        _ownSeen = _ownRejStale = _ownRejBucket = _ownRejVdo = _ownRejGob = _ownWithEnv =
            _ownWithMaps = _ownRejItem = _ownSharedPlain = _ownSharedShader = _ownRejScene =
            _ownSharedFeature = _ownRejFbSample = _ownInstSeen = _ownInstStale = _ownInstDead = 0;
            _engineWaterWalks = _ownedWaterWalks = 0;
        Array.Clear(_ownByClass); Array.Clear(OwnRefusedByClass);
        Array.Clear(_refGob); _refGobCount = 0; RefusedGobOverflow = false;
        _unknownCount = 0;
    }

    /// <summary>Why the last enrollment scan rejected what it rejected -- one line, cold.</summary>
    internal static string OwnScanDiag() =>
        $"active={_ownSeen} rejects: stale={_ownRejStale} bucket={_ownRejBucket} vdo={_ownRejVdo} " +
        $"gob={_ownRejGob} item={_ownRejItem} foreign-scene={_ownRejScene} " +
        // #128: the instanced population's own fate. "Creatures are not owned" reads identically to
        // "creatures are not in the table"; these three tell them apart without another round trip.
        $"| instances: {_ownInstSeen} live ({_ownInstStale} of them stale), {_ownInstDead} dead before the scan, " +
        $"{_pInstRecorded} recorded ever, " +
        $"fb-sampling/volumetric={_ownRejFbSample}, " +
        // #126: the classification, as coverage. Cutout-vs-blended is the split the whole transparency
        // model turns on, so it is reported rather than inferred from the draw counts.
        $"classes: {_ownByClass[0]} opaque / {_ownByClass[1]} cutout / {_ownByClass[2]} blended / " +
        $"{_ownByClass[(int)Render.OwnedClass.Water]} water / " +
        $"{_ownByClass[(int)Render.OwnedClass.Additive]} additive (refused after classifying: " +
        $"{OwnRefusedByClass[1]} cutout / {OwnRefusedByClass[2]} blended / " +
        $"{OwnRefusedByClass[(int)Render.OwnedClass.Water]} water / " +
        $"{OwnRefusedByClass[(int)Render.OwnedClass.Additive]} additive), " +
        // #129: the ticket's own criterion, as a counter. The engine takes a copy per bucket-6 walk it runs,
        // so "engine water walks" IS "engine framebuffer copies for water" -- and the number that has to be
        // zero while owned water is drawing.
        $"water walks: {_ownedWaterWalks} owned / {_engineWaterWalks} engine (each engine walk is one " +
        $"engine framebuffer copy), " +
        $"owned-with-unused-maps={_ownWithMaps}, env-mapped={_ownWithEnv}, " +
        $"shared-material={_ownSharedPlain} exact + {_ownSharedFeature} by feature switch (#123) + " +
        $"{_ownSharedShader} approximated" +
        (_unknownCount == 0 ? "" : $" ({string.Join(", ", _unknown, 0, _unknownCount)})");

    /// <summary>Did the engine's own walk sight this part THIS frame? The owned pass's audit asks it about
    /// a part it drew that was never suppressed: a part the engine did not walk at all is geometry it culled
    /// and we drew anyway (a stale-ownership cost, bounded by the enrollment cadence), while one it DID walk
    /// and we still failed to suppress is a real gap in the bucket list. The two read identically in a
    /// single UNMATCHED total and want opposite fixes.</summary>
    internal static bool SightedThisFrame(int e) => _pSeen[e] >= _frame;

    /// <summary>Has the engine walked this part within the last frame? The same window enrollment qualifies
    /// candidates on, asked per DRAW instead of per enrollment: ownership is rebuilt on a 60-frame cadence,
    /// so between rebuilds the owned set keeps every part the camera has moved away from, and the owned pass
    /// draws all of them for nothing. A part that comes back is simply not drawn by us for the frame it
    /// reappears -- the iterator sights it, finds no stamp for this pass, and the engine draws it.</summary>
    internal static bool SightedRecently(int e) => _pSeen[e] >= _frame - 1;

    /// <summary>Has this part's tile left the opaque class this frame? Mirrors ProcessTriMeshParts's own
    /// condition: the state says FADE, or the factor has already come off 1.0.</summary>
    internal static bool Fading(IntPtr tile)
        => *(int*)(tile + TileFadeState) == TileStateFade || *(float*)(tile + TileFade) < 1f;

    /// <summary>#126: the tile fade factor this part draws at, 1.0 for a part with no tile or a tile that is
    /// not fading. This is the multiplier <c>RenderDrawBucket</c> folds into <c>part+0xa0</c> for the length
    /// of a bucket-8 draw and restores afterwards, so the owned pass reads it rather than the multiplied
    /// alpha -- our draw is not inside the engine's brackets. Clamped at 0: a factor the engine has driven
    /// past the end of a fade would otherwise come back as a negative alpha.</summary>
    internal static float TileFadeFactor(IntPtr tri)
    {
        IntPtr tile = *(IntPtr*)(tri + PartTile);
        if (tile == IntPtr.Zero) return 1f;
        float f = *(float*)(tile + TileFade);
        return f >= 1f ? 1f : f > 0f ? f : 0f;
    }

    /// <summary>Does the camera dissolve this part right now (the keyhole wall-fade)? The owned pass asks
    /// per part per frame -- the same test the engine's own loop body runs. Since #124 it is what STAGES the
    /// dissolve into the per-draw record, not what hands the part back.</summary>
    internal static bool KeyholeDissolves(IntPtr tri)
    {
        if (_keyhole == null || _camSlot == IntPtr.Zero) return false;
        IntPtr cam = *(IntPtr*)_camSlot;
        return cam != IntPtr.Zero && _keyhole(cam, tri) != 0;
    }

    private static int Find(IntPtr part)
    {
        int s = (int)(((ulong)part >> 4) % MaxParts);
        for (int probe = 0; probe < 32; probe++, s = (s + 1) % MaxParts)
        {
            if (_pPart[s] == part) return s;
            if (_pState[s] == StEmpty) return -1;
        }
        return -1;
    }

    private static int Insert(IntPtr part)
    {
        if (_partCount >= MaxParts - MaxParts / 4) return -1;      // keep load factor sane
        int s = (int)(((ulong)part >> 4) % MaxParts);
        for (int probe = 0; probe < 32; probe++, s = (s + 1) % MaxParts)
            if (_pState[s] == StEmpty) { _pPart[s] = part; _partCount++; return s; }
        return -1;
    }


    /// <summary>#149 self-check helper: intern one synthetic group key through the HASH path, and assert the
    /// linear walk the hash replaced would have reached the same answer. Distinguished from
    /// <see cref="Record"/> only by not needing a live part -- the key fields are the same ones.</summary>
    private static int SelfGroup(long vdo, float* m, Action<bool, string> e)
    {
        IntPtr v = (IntPtr)vdo, z = IntPtr.Zero;
        ulong h = GroupHash(v, z, z, z, z, z, z, 0, 0, 0, 0, false, true, false, m);
        int slot = (int)(h * 0x9E3779B97F4A7C15UL >> (64 - GroupHashBits));
        int g = -1, free = -1;
        for (int p = 0; p < GroupHashSize; p++)
        {
            int j = (slot + p) & (GroupHashSize - 1);
            int cand = _gSlot[j];
            if (cand < 0) { free = j; break; }
            if (_gHash[cand] == h && GroupMatches(cand, v, z, z, z, z, z, z, 0, 0, 0, 0, false, true, false, m))
            { g = cand; break; }
        }
        // the oracle: the walk this replaced, over the same table and the same comparison
        int lin = -1;
        for (int i = 0; i < _groupCount; i++)
            if (GroupMatches(i, v, z, z, z, z, z, z, 0, 0, 0, 0, false, true, false, m)) { lin = i; break; }
        e(g == lin, $"the hash and the linear walk agree (hash={g} walk={lin})");
        if (g >= 0) return g;
        g = _groupCount++;
        _gSlot[free] = g; _gHash[g] = h;
        _gVdo[g] = v; _gTex0[g] = z; _gTex1[g] = z; _gTex2[g] = z; _gTex3[g] = z;
        _gShared[g] = z; _gEnv[g] = z; _gFlag[g] = 0; _gRot[g] = 0; _gBucket[g] = _bucketId; _gPhase[g] = _phase;
        _gScene[g] = _bucketScene; _gWorld[g] = false; _gMx[g] = true; _gMover[g] = false;
        for (int f = 0; f < 16; f++) _gMatrix[g * 16 + f] = m[f];
        _gHint[g] = 0; _gLight[g] = false; _gPrim[g] = 0; _gFirst[g] = -1; _gCount[g] = 0; _gSubmitGen[g] = -1;
        return g;
    }

    /// <summary>#149: the group key's identity test -- the ONE definition of "these two draws can share a
    /// group". Shared by the hash probe in <see cref="Record"/> and by the self-check's reference walk, so
    /// the fast path and the specification cannot drift apart.
    /// <para>A MOVER's transform is deliberately absent: it changes every frame, so keying on it would mint
    /// one group per part per pose. The flag itself IS in the key, which is what keeps a mover out of a
    /// static group whose frozen matrix a replayed multidraw would apply (#128).</para></summary>
    private static bool GroupMatches(int i, IntPtr vdo, IntPtr t0, IntPtr t1, IntPtr t2, IntPtr t3,
                                     IntPtr shared, IntPtr env, byte flag, int rot, int hint, int prim,
                                     bool light, bool mx, bool mover, float* liveM)
    {
        if (_gVdo[i] != vdo || _gTex0[i] != t0 || _gTex1[i] != t1 || _gTex2[i] != t2 || _gTex3[i] != t3
            || _gShared[i] != shared || _gEnv[i] != env
            || _gFlag[i] != flag || _gRot[i] != rot || _gBucket[i] != _bucketId
            || _gScene[i] != _bucketScene
            || _gPhase[i] != _phase || _gHint[i] != hint || _gLight[i] != light || _gPrim[i] != prim
            || _gMx[i] != mx || _gMover[i] != mover) return false;
        if (mx && !mover)
            for (int f = 0; f < 16; f++) if (_gMatrix[i * 16 + f] != liveM[f]) return false;
        return true;
    }

    /// <summary>...and its hash, over exactly the fields <see cref="GroupMatches"/> compares. The bucket,
    /// Scene and phase come off the capture state the same way that method reads them, so the two cannot
    /// disagree about what is in the key.
    /// <para>Float bits are hashed with -0 normalised to +0: <c>-0f != 0f</c> is FALSE, so the comparison
    /// calls them equal while their bit patterns differ, and an un-normalised hash would mint a second group
    /// for the same key. One extra group, never a misrender -- but free to get right.</para></summary>
    private static ulong GroupHash(IntPtr vdo, IntPtr t0, IntPtr t1, IntPtr t2, IntPtr t3,
                                   IntPtr shared, IntPtr env, byte flag, int rot, int hint, int prim,
                                   bool light, bool mx, bool mover, float* liveM)
    {
        ulong h = 0xcbf29ce484222325UL;
        void Mix(ulong v) { h = (h ^ v) * 0x100000001b3UL; }
        Mix((ulong)vdo); Mix((ulong)t0); Mix((ulong)t1); Mix((ulong)t2); Mix((ulong)t3);
        Mix((ulong)shared); Mix((ulong)env);
        Mix(flag); Mix((uint)rot); Mix((uint)_bucketId); Mix((ulong)_bucketScene);
        Mix((uint)_phase); Mix((uint)hint); Mix((uint)prim);
        Mix(light ? 1UL : 0UL); Mix(mx ? 1UL : 0UL); Mix(mover ? 1UL : 0UL);
        if (mx && !mover)
            for (int f = 0; f < 16; f++) Mix(BitConverter.SingleToUInt32Bits(liveM[f] + 0f));
        return h;
    }

    /// <summary>Drop every recorded identity: part entries, groups and the ownership column.
    /// <see cref="TableGen"/> bumps, which is what tells OwnedDrawService its slot indices died.
    /// The live area re-records over a frame or two.</summary>
    /// <summary>The present at which the part table was last emptied. #134: the census counts from HERE, not
    /// from boot. Three runs in a row armed during the load that follows boot -- one before the module was
    /// even in, so every entry read "ownable-but-not-enrolled" for the honest reason that enrollment had not
    /// happened yet, and the dump said nothing about the question it was asked.</summary>
    private static int _wipeFrame;

    /// <summary>Live entries at the last pressure check -- reported by the [batch] line so a saturated table
    /// is visible as a number rather than as "the renderer looks wrong in this area".</summary>
    private static int _satLive, _satDead;
    private static bool _saturated;

    /// <summary>The table is full. Reclaim it if it is full of the DEAD (an area transition's debris, which
    /// is what the wipe exists for), and refuse to if it is full of the LIVE (a big area, where wiping is
    /// the bug). A saturated table degrades the way it already knows how: <see cref="Insert"/> returns -1
    /// and the group allocator calls <c>Die(e, "groups-full")</c>, so the surplus stays engine-drawn and
    /// everything already recorded keeps rendering.</summary>
    private static void OnTablePressure()
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
        _log?.Info($"BatchService: table SATURATED -- {live} live vs {dead} dead entries in " +
                   $"{_groupCount} groups / {_partCount} parts. NOT wiping: this is a large area, not " +
                   "transition debris, and wiping live entries is what used to drop the whole world back " +
                   "to vanilla once a second. Parts past the cap stay engine-drawn.");
    }

    private static void WipeTables(string why)
    {
        _wipeFrame = _frame;
        Array.Clear(_pPart); Array.Clear(_pState); Array.Clear(OwnFlag);
        _gSlot.AsSpan().Fill(-1);            // -1 is empty; 0 is a valid group id
        _partCount = 0; _groupCount = 0; TableGen++;
        _saturated = false;
        _log?.Info($"BatchService: {why} -- batch tables wiped");
    }

    /// <summary>#86 frame-edge handler. A vanilla stretch freezes capture while the world keeps changing
    /// underneath it -- liveness stamps stop and groups keep their membership. Resuming on top of that is
    /// what left parts recorded-but-never-drawn.
    /// So the vanilla edge tears the tables down and the owned edge re-records from nothing, which is exactly
    /// what an area transition already does (and why switching areas healed it).</summary>
    public static void OnRenderModeEdge(bool owned)
    {
        if (!owned && _active) WipeTables("vanilla toggle");
    }

    /// <summary>Frame tick from UiHostService. Periodic stats; the log is the only cold path.</summary>
    public static void EndFrame()
    {
        if (!_active) return;
        if (Gl.MultiDrawElementsBaseVertex == null && _log != null)
        {
            Gl.Init(_log);                                         // GL context exists here, not at service init
            if (Gl.MultiDrawElementsBaseVertex == null) { _active = false; _log.Info("BatchService: glMultiDrawElementsBaseVertex missing -- batching off"); return; }
        }
        // module-unload edge: every part/VDO dies with the module -- drop all recorded identity
        bool inModule = UiHost.InModule;
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
                   $"iter-skipped {(float)_iterSkip / LogEvery:F0} + late {(float)_skipped / LogEvery:F0}, " +
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
        _batched = 0; _calls = 0; _skipped = 0; _iterSkip = 0; _vanSolo = 0; _vanDirty = 0; _vanDead = 0;
        _vanOwnOnly = 0; _skinSeen = 0; _skinForced = 0; _skinForceFailed = 0;
        _recNotOk = 0; _recNoCap = 0; _recBadVdo = 0; _submitTrunc = 0;
    }

    private const int DumpMax = 6000;
    /// <summary>CAurTextureBasic's 32-byte name field (Ghidra CAurTextureBasic::CAurTextureBasic @0x487250:
    /// <c>strncpy(this + 0x88, name, 0x1f)</c>). Reached the way AurTextureMessage @0x488b10 reaches it --
    /// vtable slot 0x18 resolves a reference texture to the real one, slot 0x10 is AsCAurTextureBasic.</summary>
    private const int TexBasicName = 0x88, TexVtblAsBasic = 0x10, TexVtblReferenced = 0x18;

    /// <summary>Triage only: what GL actually holds for this texture. The engine's opacity flags and the
    /// file's own header have each been wrong once in this investigation; the uploaded format is neither.</summary>
    private static string TexFormat(int tid)
    {
        if (tid <= 0 || Ui.Gl.GetTexLevelParameteriv == null) return "fmt=?";
        int saved; Ui.Gl.GetIntegerv(Ui.Gl.TEXTURE_BINDING_2D, &saved);
        Ui.Gl.BindTexture(Ui.Gl.TEXTURE_2D, (uint)tid);
        int fmt = 0, asize = 0, w = 0, h = 0;
        Ui.Gl.GetTexLevelParameteriv(Ui.Gl.TEXTURE_2D, 0, Ui.Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        Ui.Gl.GetTexLevelParameteriv(Ui.Gl.TEXTURE_2D, 0, Ui.Gl.TEXTURE_ALPHA_SIZE, &asize);
        Ui.Gl.GetTexLevelParameteriv(Ui.Gl.TEXTURE_2D, 0, Ui.Gl.TEXTURE_WIDTH, &w);
        Ui.Gl.GetTexLevelParameteriv(Ui.Gl.TEXTURE_2D, 0, Ui.Gl.TEXTURE_HEIGHT, &h);
        Ui.Gl.BindTexture(Ui.Gl.TEXTURE_2D, (uint)saved);
        return $"fmt=0x{fmt:x} asize={asize} {w}x{h}";
    }

    /// <summary>Triage: every gob term the gate and the fold read, printed rather than reduced to a bool.
    /// "The instance modulates" is not actionable; "it modulates because its owner's fade bit is set" is.</summary>
    private static string GobTerms(IntPtr gob)
    {
        if (gob == IntPtr.Zero) return "gobmod=-";
        IntPtr owner = *(IntPtr*)(gob + GobFadeOwner);
        return $"gobmod=hl{*(byte*)(gob + GobHighlight)}/tint{*(byte*)(gob + GobSceneTint)}" +
               $"/col{*(short*)(gob + GobColourMul)}/emi{*(short*)(gob + GobSelfIllum)}" +
               $"/env{(*(IntPtr*)(gob + GobEnvOverride) != IntPtr.Zero ? 1 : 0)}" +
               $"/own{(owner == IntPtr.Zero ? -1 : *(byte*)(owner + GobOwnerFlags) & GobOwnerFadeBit)}" +
               $" gmul=a{*(float*)(gob + GobAlpha):0.00}/a2{*(float*)(gob + GobFadeAlpha):0.00}" +
               $" gcol={*(float*)(gob + GobColour):0.00},{*(float*)(gob + GobColour + 4):0.00},{*(float*)(gob + GobColour + 8):0.00}" +
               $" gemi={*(float*)(gob + GobEmissive):0.00},{*(float*)(gob + GobEmissive + 4):0.00},{*(float*)(gob + GobEmissive + 8):0.00}";
    }

    /// <summary>#129 diagnosis: name each distinct water material once. The stage's liquid colour is the
    /// AVERAGE of texture 0, so which texture that is decides the whole body colour of a body of water --
    /// and the two routes into bucket 6 (bumpy-shiny versus <c>sample_framebuffer 2</c>) are different
    /// content with different conventions. Cheap: a set lookup on the handful of water materials an area has.
    /// ponytail: never cleared, so a session that visits many areas keeps a few dozen strings.</summary>
    private static void NoteWaterMaterial(IntPtr mat, IntPtr shared)
    {
        IntPtr t0 = (IntPtr)_getTexture(mat, 0);
        string name = TexName(t0);
        if (!_waterMats.Add(name)) return;
        bool bumpy = _bumpyWater != null && *_bumpyWater != 0 && _hasBumpyShiny(mat) != 0;
        int fb = shared == IntPtr.Zero ? -1 : *(int*)(shared + SharedSampleFb);
        _log?.Info($"[water] material tex0=\"{name}\" tid={AurTex.Tid(t0)} {TexFormat(AurTex.Tid(t0))} " +
                   $"route={(bumpy ? "bumpy-shiny" : "sample_framebuffer")} sample_fb={fb} " +
                   $"blend={*(int*)(mat + MatSrcBlend)}/{*(int*)(mat + MatDstBlend)} " +
                   $"env={_hasEnvMap(mat) != 0} {TexTopMip(AurTex.Tid(t0))} " +
                   $"{Render.WaterBodies.Describe()} {Render.OwnedDrawService.DescribeWind()}");
    }

    /// <summary>#129: what <c>textureLod(uTex, vec2(0.5), 20.0)</c> actually returns for this texture -- the
    /// stage's whole liquid colour. LOD 20 CLAMPS to <c>TEXTURE_MAX_LEVEL</c>, so a texture with no mip chain
    /// answers with one centre texel of the ripple pattern instead of the average the shader is asking for,
    /// and the pool takes that texel's colour. Prints the level landed on and its mean, so "the water is the
    /// wrong colour" becomes a number that can be compared between two areas.</summary>
    private static string TexTopMip(int tid)
    {
        if (tid <= 0 || Ui.Gl.GetTexImage == null || Ui.Gl.GetTexParameteriv == null) return "top=?";
        int saved; Ui.Gl.GetIntegerv(Ui.Gl.TEXTURE_BINDING_2D, &saved);
        Ui.Gl.BindTexture(Ui.Gl.TEXTURE_2D, (uint)tid);
        int maxLevel = 0;
        Ui.Gl.GetTexParameteriv(Ui.Gl.TEXTURE_2D, Ui.Gl.TEXTURE_MAX_LEVEL, &maxLevel);
        int lod = maxLevel < 20 ? maxLevel : 20, w = 0, h = 0;
        // ...and MAX_LEVEL is only a cap: the chain may be shorter than it claims. Walk down to a level that
        // actually has storage, which is where the sampler lands too.
        for (; lod > 0; lod--)
        {
            Ui.Gl.GetTexLevelParameteriv(Ui.Gl.TEXTURE_2D, lod, Ui.Gl.TEXTURE_WIDTH, &w);
            Ui.Gl.GetTexLevelParameteriv(Ui.Gl.TEXTURE_2D, lod, Ui.Gl.TEXTURE_HEIGHT, &h);
            if (w > 0 && h > 0) break;
        }
        if (lod == 0)
        {
            Ui.Gl.GetTexLevelParameteriv(Ui.Gl.TEXTURE_2D, 0, Ui.Gl.TEXTURE_WIDTH, &w);
            Ui.Gl.GetTexLevelParameteriv(Ui.Gl.TEXTURE_2D, 0, Ui.Gl.TEXTURE_HEIGHT, &h);
        }
        // Only the small end is read back: a level-0 readback of a 256x256 diffuse is a pipeline stall for a
        // diagnostic, and any level big enough to cost that is not the one the shader lands on anyway.
        if (w <= 0 || h <= 0 || w * h > 64) { Ui.Gl.BindTexture(Ui.Gl.TEXTURE_2D, (uint)saved); return $"top=L{lod} {w}x{h} (not read)"; }
        byte* px = stackalloc byte[64 * 4];
        Ui.Gl.GetTexImage(Ui.Gl.TEXTURE_2D, lod, Ui.Gl.RGBA, Ui.Gl.UNSIGNED_BYTE, px);
        Ui.Gl.BindTexture(Ui.Gl.TEXTURE_2D, (uint)saved);
        int r = 0, g = 0, b = 0, a = 0, n = w * h;
        for (int i = 0; i < n; i++) { r += px[i * 4]; g += px[i * 4 + 1]; b += px[i * 4 + 2]; a += px[i * 4 + 3]; }
        return $"top=L{lod}/{maxLevel} {w}x{h} rgba({r / n},{g / n},{b / n},{a / n})";
    }

    private static readonly HashSet<string> _waterMats = new();

    private static string TexName(IntPtr tex)
    {
        if (tex == IntPtr.Zero) return "-";
        IntPtr real = ((delegate* unmanaged<IntPtr, IntPtr>)*(IntPtr*)(*(IntPtr*)tex + TexVtblReferenced))(tex);
        if (real == IntPtr.Zero) real = tex;
        IntPtr basic = ((delegate* unmanaged<IntPtr, IntPtr>)*(IntPtr*)(*(IntPtr*)real + TexVtblAsBasic))(real);
        return basic == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(basic + TexBasicName) ?? "?";
    }

    /// <summary>One census line for one engine-drawn part. Everything the four open questions need at once:
    /// which bucket the engine is drawing it in, which bucket we CAPTURED it in (they differ, and ownership
    /// keys on the capture), what state its table entry is in, what class it classified as, and whether its
    /// diffuse carries alpha the caster mask could have used.</summary>
    private static void DumpPart(IntPtr part)
    {
        IntPtr mat = *(IntPtr*)(part + PartMaterial);
        if (_dumpTex != null)
        {
            IntPtr t = mat == IntPtr.Zero ? IntPtr.Zero : (IntPtr)_getTexture(mat, 0);
            // #134: "*" matches every part. Four rolls asked "is the ivy owned" of a substring that named
            // exactly ONE part in the area -- the answer was yes every time and the thing on screen was
            // other geometry. A name you have to guess is not a filter, it is a hypothesis.
            if (!_dumpAll
                && (t == IntPtr.Zero || !TexName(t).Contains(_dumpTex, System.StringComparison.OrdinalIgnoreCase)))
            { _dumpLeft++; return; }                       // refund: only matches spend the budget
            WatchEngineDraws++;
            // #134: a WHOLE sampled frame, not one row of it. One row per frame made every roll read as
            // "the ivy", singular, while the thing on screen is a dozen parts -- and the one row happened to
            // be an owned one for four rolls running. Sample every DumpTexEvery frames, up to DumpTexRows
            // parts, so the population is visible rather than a representative of it.
            if (_frame != _dumpTexFrame)
            {
                if (_frame - _dumpTexFrame < DumpTexEvery) { _dumpLeft++; return; }
                _dumpTexFrame = _frame; _dumpTexRows = 0;
            }
            if (_dumpTexRows++ >= (_dumpAll ? DumpAllRows : DumpTexRows)) { _dumpLeft++; return; }
        }
        IntPtr node = *(IntPtr*)(part + PartNode);
        int e = Find(part);
        if (e >= 0) _watchEntry = e;
        string ent = e < 0 ? "unrecorded" : $"entry={e} st={_pState[e]} died={(_pState[e] == StDead ? _pDeath[e] ?? "?" : "-")} own={OwnFlag[e]} ownonly={_pOwnOnly[e]} capbucket={(_pState[e] == StActive ? _gBucket[_pGroup[e]] : -1)} seen={_frame - _pSeen[e]}";
        IntPtr t0 = mat == IntPtr.Zero ? IntPtr.Zero : (IntPtr)_getTexture(mat, 0);
        int tid = AurTex.Tid(t0);
        IntPtr shared = mat == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(mat + MatShared);
        _log?.Info($"[dumpframe] bucket={_bucketId} inbucket={_inBucket} dangly={IsDangly(part)} skin={_inSkinDraw} why={(e < 0 ? "unrecorded" : WhyNotOwnable(e))} part=0x{(ulong)part:x} vtbl=0x{(ulong)*(IntPtr*)part:x} {ent} " +
                   $"tex=\"{TexName(t0)}\" tid={tid} texalpha={(tid > 0 && Shadow.CasterPass.TexHasAlpha(tid))} {TexFormat(tid)} " +
                   $"opaque={(mat != IntPtr.Zero && _isOpaque(part) != 0)} env={(mat != IntPtr.Zero && _hasEnvMap(mat) != 0)} " +
                   $"blend={(mat == IntPtr.Zero ? 0 : *(int*)(mat + MatSrcBlend))}/{(mat == IntPtr.Zero ? 0 : *(int*)(mat + MatDstBlend))} " +
                   $"alpha={*(float*)(part + PartAlpha):0.00} gob=0x{(ulong)*(IntPtr*)(part + PartGob):x} " +
                   $"node=0x{(ulong)node:x} nvtbl=0x{(ulong)(node == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)node):x} " +
                   $"n10=0x{(ulong)(node == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(node + 0x10)):x} " +
                   $"nflags=0x{(node == IntPtr.Zero ? 0 : *(byte*)(node + NodeFlags)):x} {GobTerms(*(IntPtr*)(part + PartGob))} " +
                   $"transp={(shared != IntPtr.Zero && *(byte*)(shared + SharedTransparency) != 0)} " +
                   $"shvs=\"{(shared == IntPtr.Zero ? "-" : Marshal.PtrToStringAnsi(shared + SharedVsName) ?? "")}\"");
    }
}
