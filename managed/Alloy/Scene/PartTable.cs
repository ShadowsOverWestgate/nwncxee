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
/// hook bodies (the death notice, the sighting stamp) that have to sit beside their trampolines.</para>
/// </summary>
internal static unsafe class PartTable
{
    /// <summary>The per-item-alpha bucket. The census owns it (#126).</summary>
    internal const int AlphaBucket = 8;

    /// <summary>Presents since boot, ticked by <see cref="DrawFunnelService.EndFrame"/>. The sighting stamps,
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


    internal static delegate* unmanaged<IntPtr, IntPtr, byte> _keyhole;
    internal static IntPtr _camSlot;   // &CurrentCamera; live CAurCamera* is *_camSlot

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
        _keyhole = (delegate* unmanaged<IntPtr, IntPtr, byte>)Native.Sym(SymbolMap.GetCanKeyholeDissolve);
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
    internal static readonly int[] _pSeen = new int[MaxParts];
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
    /// <c>/vanilla</c> -- the one path to <see cref="WipeTables"/> during play -- healed).</summary>
    internal static readonly bool[] _pFreed = new bool[MaxParts];
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

    /// <summary>Did the engine's own walk sight this part THIS frame? The Alloy pass's audit asks it about
    /// a part it drew that was never suppressed: a part the engine did not walk at all is geometry it culled
    /// and we drew anyway (a stale-ownership cost, bounded by the enrollment cadence), while one it DID walk
    /// and we still failed to suppress is a real gap in the bucket list. The two read identically in a
    /// single UNMATCHED total and want opposite fixes.</summary>
    internal static bool SightedThisFrame(int e) => _pSeen[e] >= _frame;

    /// <summary>#256: is this part in THIS frame's draw-bucket arrays? Enrolment no longer asks anything
    /// about sightings -- a record exists because the walk reached the part, whether or not it is on screen
    /// -- so this is now purely the per-frame VISIBILITY question, and the answer is the engine's own
    /// finished cull (#253 §7.1: "is this part in this frame's bucket array", obtained by a pointer read).
    /// Drawing the whole enumerated area every frame would be most of it wasted.
    /// <para>The stamp comes from <see cref="BucketGate"/>'s reads of <c>Scene+0x208</c>, not from a capture.
    /// #243 replaces the source with Alloy's own cull; this predicate is where it plugs in.</para></summary>
    internal static bool InBucketsRecently(int e) => _pSeen[e] >= _frame - 1;

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
                _pPart[s] = part; _reclaims++; return s;
            }
            if (_pState[s] == StEmpty) { if (!room) return -1; _pPart[s] = part; _partCount++; return s; }
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
        Array.Clear(_pPart); Array.Clear(_pState); Array.Clear(_pFreed); Array.Clear(AlloyCensus.OwnFlag);
        _partCount = 0; TableGen++;
        _saturated = false;
        // #256: the table is empty and only the walk can refill it. The vanilla toggle and the pressure wipe
        // change no scene membership, so the epoch will not move on its own -- ask for a walk explicitly.
        SceneWalk.RequestWalk();
        Log?.Info($"PartTable: {why} -- part tables wiped");
    }

    /// <summary>#86 frame-edge handler. A vanilla stretch freezes capture while the world keeps changing
    /// underneath it -- liveness stamps stop. Resuming on top of that is
    /// what left parts recorded-but-never-drawn.
    /// So the vanilla edge tears the tables down and the Alloy edge re-records from nothing, which is exactly
    /// what an area transition already does (and why switching areas healed it).</summary>
    public static void OnRenderModeEdge(bool owned)
    {
        if (!owned && Active) WipeTables("vanilla toggle");
    }
}
