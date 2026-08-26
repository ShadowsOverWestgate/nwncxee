using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Alloy.SceneLayout;
using static Sowx.Alloy.BucketGate;
namespace Sowx.Alloy;

/// <summary>#188: the part table -- every recorded part, its parallel columns, the group registry they key
/// into, and the identity/lifetime machinery (record slots, sightings, death, wipe, pressure) that the
/// census, the bucket gate and the batcher all ride on. One struct-of-arrays module rather than three,
/// for #173's reason: the hot walks read these arrays directly and a real boundary here costs a copy or an
/// indirection per part per frame.
/// <para>It also owns the engine reads those arrays are filled from -- the <c>Material</c>/<c>Part</c>
/// accessors resolved once at <see cref="Resolve"/> -- because all three consumers call them.</para>
/// <para>Members are <c>internal</c> and reached by the other three modules through
/// <c>using static</c>, which is a compile-time alias: a cross-module read of a column is the same
/// instruction as the same-class read it replaced.</para>
/// <para>What is NOT here: the capture funnel that WRITES the columns (<c>PartTriMesh::Draw</c>,
/// <c>Record</c>, <c>Complete</c>) stays in <see cref="BatchService"/> beside the hooks it hangs off --
/// splitting a hook body from its trampoline buys nothing and risks the ordering.</para></summary>
internal static unsafe class PartTable
{
    /// <summary>The per-item-alpha bucket. Read by all three: the gate never suppresses it for the batcher,
    /// the batcher never submits into it, and the census owns it (#126).</summary>
    internal const int AlphaBucket = 8;

    /// <summary>Presents since boot, ticked by <see cref="BatchService.EndFrame"/>. The sighting stamps,
    /// the staleness windows and the census's settle clock are all counted in it.</summary>
    internal static int _frame;

    /// <summary>Whoever logs about the table (the pressure decision and the wipe). Named rather than
    /// <c>_log</c> so a module that imports this one and keeps its own log cannot silence the wrong one.</summary>
    internal static ExtenderLog? Log;
    /// <summary>Tile-fade state and factor (the TileData* hangs off <see cref="PartTile"/>). #124 corrected
    /// both: the factor is at +0x70, NOT +0x8c below it -- <c>TileData::TileData</c> @0x43f050 inits three
    /// separate floats to 1.0 and the original RE picked the wrong one, so this test was permanently false.
    /// <c>ProcessTriMeshParts</c> @0x480d40 is the reader: +0x70 is this frame's factor (+0x74 is the pending
    /// one <c>Scene::ResetTileFading</c> swaps in), and +0xc0 is the state enum.
    /// <para>A tile that is fading leaves the opaque class outright -- ProcessTriMeshParts routes it into
    /// <c>fadebuckets</c>, and <c>Scene::AddPartsToDrawBuckets</c> @0x4751d0 puts those in bucket 8. That was
    /// a hand-back until #126, which made bucket 8 suppressible and the fade a per-frame overlay on the
    /// Alloy blended class (<see cref="TileFadeFactor"/>). This test still says "not batchable by this
    /// service", which it is: the per-item alpha multiply is per part.</para></summary>
    internal const int TileFade = 0x70, TileFadeState = 0xc0;
    /// <summary><c>TileData::FADE</c> (@0x12ef184, value 1). The other two states (DO_NOT_FADE 2,
    /// FADE_NOT_CHECKED 4) leave the part in the opaque buckets, and the factor alone is not enough: a tile
    /// entering FADE still reads 1.0 for its first frame.</summary>
    internal const int TileStateFade = 1;

    /// <summary>Recorded-part capacity, and with <c>MaxGroups</c> the reason the renderer can hold a whole
    /// object-dense area rather than the first 4096 parts of one. 262144 parts + 262144 groups is 70MB of
    /// the 135MB these tables cost resident (up from 22MB) -- bought deliberately, because a cap that cannot
    /// hold the area is a rendering fault and not a saving.
    /// <para>Power of two because the group intern table derives its Fibonacci shift from the exponent, and
    /// a shift written as a literal is the bug that has now bitten this codebase twice.</para></summary>
    internal const int MaxPartsBits = 18, MaxParts = 1 << MaxPartsBits;

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
    internal const int MaxGroups = MaxParts;

    internal static delegate* unmanaged<IntPtr, IntPtr, byte> _keyhole;
    internal static IntPtr _camSlot;   // &CurrentCamera; live CAurCamera* is *_camSlot

    internal static delegate* unmanaged<IntPtr, byte> _isOpaque, _hasEnvMap, _hasBumpyShiny;
    internal static delegate* unmanaged<IntPtr, int> _getRenderHint;
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
        _getRenderHint = (delegate* unmanaged<IntPtr, int>)Native.Sym(SymbolMap.PartGetRenderHint);
        IntPtr dv = Native.Sym(SymbolMap.PartDanglyMeshVtbl);
        _danglyVtbl = dv == IntPtr.Zero ? IntPtr.Zero : dv + 0x10;
        _getTexture = (delegate* unmanaged<IntPtr, int, long>)Native.Sym(SymbolMap.MatGetTexture);
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _bumpyWater = (int*)Native.Sym(SymbolMap.EnableBumpyWater);
        _keyhole = (delegate* unmanaged<IntPtr, IntPtr, byte>)Native.Sym(SymbolMap.GetCanKeyholeDissolve);
        _camSlot = Native.Sym(SymbolMap.CurrentCamera);
        return _isOpaque != null && _hasEnvMap != null && _hasBumpyShiny != null && _getRenderHint != null
               && _getTexture != null && _primTable != null;
    }

    // part entries: open-address hash on part pointer, parallel arrays, no allocation
    internal const byte StEmpty = 0, StCapturing = 1, StActive = 2, StDead = 3, StStale = 4, StProbe = 5;
    internal static readonly IntPtr[] _pPart = new IntPtr[MaxParts];
    internal static readonly IntPtr[] _pNode = new IntPtr[MaxParts];
    internal static readonly IntPtr[] _pMat = new IntPtr[MaxParts];
    internal static readonly IntPtr[] _pVdo = new IntPtr[MaxParts];
    internal static readonly IntPtr[] _pTex0 = new IntPtr[MaxParts];   // diffuse slot at record (animation guard)
    internal static readonly int[] _pPrim = new int[MaxParts];         // prim type captured at the funnel
    internal static readonly int[] _pRot = new int[MaxParts];          // texture-rotation bits at record
    internal static readonly bool[] _pMx = new bool[MaxParts];         // matrix-path part (phase 2)
    internal static readonly ulong[] _pMxHash = new ulong[MaxParts];   // probation matrix hash
    internal static readonly int[] _pItem = new int[MaxParts];
    internal static readonly byte[] _pState = new byte[MaxParts];
    internal static readonly bool[] _pDirty = new bool[MaxParts];
    internal static readonly int[] _pGroup = new int[MaxParts];
    internal static readonly int[] _pNext = new int[MaxParts];      // group member chain
    internal static readonly int[] _pSeen = new int[MaxParts];
    internal static readonly int[] _pIncluded = new int[MaxParts];
    /// <summary>#126: recorded for the Alloy pass only -- a transparent, additive, cap or fading part. It is
    /// captured (VDO, item, prim) and grouped like any other, because that is what ownership enrolls from,
    /// but this batcher never submits it: its own multidraw replays the first member's engine state, and
    /// blend factors, the bucket-8 alpha multiply and the cutout toggle all vary per part inside one
    /// material group. Its own draw stays vanilla until the Alloy pass takes it.
    /// <para>ponytail: latched at Record and never recomputed, so a part first sighted while its tile was
    /// fading stays unbatchable by THIS service for the life of the entry (the Alloy pass still takes it --
    /// it reclassifies per frame). The ceiling is batch coverage decaying across a session as tiles fade,
    /// recovering at the next table wipe. Recomputing it would also have to re-key the entry's group, whose
    /// bucket is part of the key; do that if the [batch] line's Alloy-only count ever climbs.</para></summary>
    internal static readonly bool[] _pOwnOnly = new bool[MaxParts];
    /// <summary>#134: which gate killed this entry, for the census. There are ten ways to reach StDead and
    /// the aggregate counters name three of them, which is how 5319 gob draws a frame went unexplained.</summary>
    internal static readonly string[] _pDeath = new string[MaxParts];
    internal static int _partCount;


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

    /// <summary>Bumped on every table wipe: entry indices held elsewhere are stale from that moment.</summary>
    internal static int TableGen;
    /// <summary>Ownership-enrollment scan bound (the part table is open-addressed, so it is scanned whole).</summary>
    internal static int EntryCount => MaxParts;
    /// <summary>Are the CAPTURE hooks in? False = no tables, so the census has nothing to classify, the gate
    /// nothing to suppress and the Alloy world pass nothing to enroll from -- which it reports rather than
    /// sitting at zero silently, because <c>drawhooks=off</c> otherwise disables the Alloy pass too.
    /// <para>#188: this is deliberately NOT "the batcher is armed". Multidraw availability moves the
    /// batcher's own latch (BatchService's <c>_batchArmed</c>) and leaves this one alone, so a batcher that
    /// stood down cannot take the table, the census and the gate with it.</para></summary>
    internal static bool Active;

    /// <summary>Armed AND the frame latched owned (#86). The two hot funnels read this, so a vanilla frame
    /// records nothing, batches nothing and suppresses nothing -- every part takes the engine's own draw.
    /// Bookkeeping (EndFrame, the period log, enrollment candidacy) stays on <see cref="Active"/> so the
    /// counters still report while vanilla, which is what proves they read zero.</summary>
    internal static bool Live => Active && RenderMode.Alloy;

    // groups: linear-scan registry (records are rare/cold), full component compare -- no hash collisions.
    // Keyed on material CONTENT (textures + shared-material + blend flag), never the Material* -- each part
    // owns its Material instance, so pointer identity shatters every group to size 1 (first live run).
    internal static readonly IntPtr[] _gVdo = new IntPtr[MaxGroups];
    internal static readonly IntPtr[] _gTex0 = new IntPtr[MaxGroups];
    internal static readonly IntPtr[] _gTex1 = new IntPtr[MaxGroups];
    internal static readonly IntPtr[] _gTex2 = new IntPtr[MaxGroups];
    internal static readonly IntPtr[] _gTex3 = new IntPtr[MaxGroups];
    internal static readonly IntPtr[] _gShared = new IntPtr[MaxGroups];
    internal static readonly IntPtr[] _gEnv = new IntPtr[MaxGroups];     // #121: mat+0x40, part of the key
    internal static readonly byte[] _gFlag = new byte[MaxGroups];
    internal static readonly int[] _gBucket = new int[MaxGroups];
    internal static readonly IntPtr[] _gScene = new IntPtr[MaxGroups];   // #91: the Scene this group was recorded from
    internal static readonly bool[] _gWorld = new bool[MaxGroups];       // ... and whether that Scene was the world's
    internal static readonly int[] _gPhase = new int[MaxGroups];
    internal static readonly int[] _gHint = new int[MaxGroups];
    internal static readonly bool[] _gLight = new bool[MaxGroups];
    internal static readonly int[] _gPrim = new int[MaxGroups];
    internal static readonly int[] _gRot = new int[MaxGroups];
    internal static readonly bool[] _gMx = new bool[MaxGroups];
    /// <summary>#128: the group's members are MOVERS -- instanced parts whose transform changes every frame.
    /// Its <see cref="_gMatrix"/> is therefore meaningless and never written; the flag is in the group key so
    /// a mover can never join a static group whose frozen matrix a replayed multidraw would apply.</summary>
    internal static readonly bool[] _gMover = new bool[MaxGroups];
    internal static readonly float[] _gMatrix = new float[MaxGroups * 16];   // model matrix for matrix-path groups
    internal static readonly int[] _gFirst = new int[MaxGroups];
    internal static readonly int[] _gCount = new int[MaxGroups];
    internal static readonly int[] _gSubmitGen = new int[MaxGroups];
    /// <summary>#149: the group-key intern table. <c>_gHash</c> is each group's key hash; <c>_gSlot</c> is the
    /// open-addressed index into it, -1 = empty, sized 2x for a 0.5 load factor.
    /// <para>The shift is bound to <see cref="GroupHashBits"/> rather than written as a literal. Fibonacci
    /// hashing takes the TOP bits of the product, so a hardcoded shift silently clusters every key into the
    /// first 1/N of the table the moment the table grows -- the bug that was sitting in <c>TexKey</c>.</para></summary>
    internal const int GroupHashBits = MaxPartsBits + 1, GroupHashSize = 1 << GroupHashBits;
    internal static readonly ulong[] _gHash = new ulong[MaxGroups];
    internal static readonly int[] _gSlot = EmptySlots();
    /// <summary>-1, not 0, and it has to be done at construction as well as at every wipe: 0 is a valid
    /// group id, so a zero-filled table reads as "group 0 lives in every slot" and the very first record
    /// probes the whole table without finding a free one.</summary>
    internal static int[] EmptySlots()
    {
        int[] a = new int[GroupHashSize];
        a.AsSpan().Fill(-1);
        return a;
    }
    internal static int _groupCount;

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
        return *(IntPtr*)part == _danglyVtbl;
    }

    internal static bool IsInstance(IntPtr part)
    {
        return part != IntPtr.Zero && *(IntPtr*)(part + PartGob) != IntPtr.Zero;
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

    internal static ulong HashMatrix(float* m)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < 16; i++) { h ^= *(uint*)(m + i); h *= 1099511628211UL; }
        return h;
    }

    /// <summary>Did the engine's own walk sight this part THIS frame? The Alloy pass's audit asks it about
    /// a part it drew that was never suppressed: a part the engine did not walk at all is geometry it culled
    /// and we drew anyway (a stale-ownership cost, bounded by the enrollment cadence), while one it DID walk
    /// and we still failed to suppress is a real gap in the bucket list. The two read identically in a
    /// single UNMATCHED total and want opposite fixes.</summary>
    internal static bool SightedThisFrame(int e) => _pSeen[e] >= _frame;

    /// <summary>Has the engine walked this part within the last frame? The same window enrollment qualifies
    /// candidates on, asked per DRAW instead of per enrollment: ownership is rebuilt on a 60-frame cadence,
    /// so between rebuilds the Alloy set keeps every part the camera has moved away from, and the Alloy pass
    /// draws all of them for nothing. A part that comes back is simply not drawn by us for the frame it
    /// reappears -- the iterator sights it, finds no stamp for this pass, and the engine draws it.</summary>
    internal static bool SightedRecently(int e) => _pSeen[e] >= _frame - 1;

    /// <summary>Has this part's tile left the opaque class this frame? Mirrors ProcessTriMeshParts's own
    /// condition: the state says FADE, or the factor has already come off 1.0.</summary>
    internal static bool Fading(IntPtr tile)
        => *(int*)(tile + TileFadeState) == TileStateFade || *(float*)(tile + TileFade) < 1f;

    /// <summary>#126: the tile fade factor this part draws at, 1.0 for a part with no tile or a tile that is
    /// not fading. This is the multiplier <c>RenderDrawBucket</c> folds into <c>part+0xa0</c> for the length
    /// of a bucket-8 draw and restores afterwards, so the Alloy pass reads it rather than the multiplied
    /// alpha -- our draw is not inside the engine's brackets. Clamped at 0: a factor the engine has driven
    /// past the end of a fade would otherwise come back as a negative alpha.</summary>
    internal static float TileFadeFactor(IntPtr tri)
    {
        IntPtr tile = *(IntPtr*)(tri + PartTile);
        if (tile == IntPtr.Zero) return 1f;
        float f = *(float*)(tile + TileFade);
        return f >= 1f ? 1f : f > 0f ? f : 0f;
    }

    /// <summary>Does the camera dissolve this part right now (the keyhole wall-fade)? The Alloy pass asks
    /// per part per frame -- the same test the engine's own loop body runs. Since #124 it is what STAGES the
    /// dissolve into the per-draw record, not what hands the part back.</summary>
    internal static bool KeyholeDissolves(IntPtr tri)
    {
        if (_keyhole == null || _camSlot == IntPtr.Zero) return false;
        IntPtr cam = *(IntPtr*)_camSlot;
        return cam != IntPtr.Zero && _keyhole(cam, tri) != 0;
    }

    internal static int Find(IntPtr part)
    {
        int s = (int)(((ulong)part >> 4) % MaxParts);
        for (int probe = 0; probe < 32; probe++, s = (s + 1) % MaxParts)
        {
            if (_pPart[s] == part) return s;
            if (_pState[s] == StEmpty) return -1;
        }
        return -1;
    }

    internal static int Insert(IntPtr part)
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
    internal static int SelfGroup(long vdo, float* m, Action<bool, string> e)
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
    internal static bool GroupMatches(int i, IntPtr vdo, IntPtr t0, IntPtr t1, IntPtr t2, IntPtr t3,
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
    internal static ulong GroupHash(IntPtr vdo, IntPtr t0, IntPtr t1, IntPtr t2, IntPtr t3,
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
                   $"{_groupCount} groups / {_partCount} parts. NOT wiping: this is a large area, not " +
                   "transition debris, and wiping live entries is what used to drop the whole world back " +
                   "to vanilla once a second. Parts past the cap stay engine-drawn.");
    }

    internal static void WipeTables(string why)
    {
        _wipeFrame = _frame;
        Array.Clear(_pPart); Array.Clear(_pState); Array.Clear(AlloyCensus.OwnFlag);
        _gSlot.AsSpan().Fill(-1);            // -1 is empty; 0 is a valid group id
        _partCount = 0; _groupCount = 0; TableGen++;
        _saturated = false;
        Log?.Info($"PartTable: {why} -- part tables wiped");
    }

    /// <summary>#86 frame-edge handler. A vanilla stretch freezes capture while the world keeps changing
    /// underneath it -- liveness stamps stop and groups keep their membership. Resuming on top of that is
    /// what left parts recorded-but-never-drawn.
    /// So the vanilla edge tears the tables down and the Alloy edge re-records from nothing, which is exactly
    /// what an area transition already does (and why switching areas healed it).</summary>
    public static void OnRenderModeEdge(bool owned)
    {
        if (!owned && Active) WipeTables("vanilla toggle");
    }
}
