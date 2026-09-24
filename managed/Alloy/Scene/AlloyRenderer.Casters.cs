using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>#302: the SHADOW pass's view of the ownership table. Alloy already keeps every fact a depth bake
/// needs -- the model matrix in the persistent record, the bone base in <c>feat.y</c>, the index range in the
/// VDO item tables under <c>_oVdo/_oItem</c>, the VAO in the shared cache, the world sphere beside the record
/// -- so <see cref="CasterPass"/> reads them from here rather than maintaining the parallel copy it kept from
/// before Alloy existed (ADR 0019).
/// <para>Read-only and NON-CONSUMING by construction. Nothing below clears a flag, advances a cursor or takes
/// an edge: ADR 0017's hazard was a mover signal spent by whichever pass asked first, and the shadow bracket
/// runs before the colour build, so a consuming read here would be that same bug by a new route.</para>
/// <para>One frame late, deliberately. The records describe the world as of the last colour build, which is
/// the frame before this one -- the same lag the skin poses already carry by design (#113). It is COHERENT:
/// the mover stamp and the record content describe the same world state, so the re-bake predicate cannot
/// miss a bake, and the shadow trails a fast rigid mover by one frame rather than disagreeing with it.</para>
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The frame this slot's record last CHANGED, stamped beside <c>_recFrame</c> (which is stamped
    /// every build, changed or not). The two being equal is the mover signal: the slot's most recent build
    /// was also a build that moved something.</summary>
    private static readonly int[] _recMovedAt = new int[MaxOwned];
    /// <summary>#302: the caster's world sphere, written on the same edge as <c>_dSph</c> and from the same
    /// terms -- but GEOMETRIC even where the cull is exempted. A skinmesh, a dangly mesh and a displacing
    /// feature all get <see cref="NeverCull"/> in <c>_dSph</c>, which is right for a culler and useless for a
    /// re-bake predicate: "somewhere" cannot name the views it dirties, so every such part would blanket-
    /// invalidate and #241's whole win with it. This carries the bind-pose bbox instead, which is exactly
    /// what the shadow pass located them by before.</summary>
    private static readonly float[] _dCastSph = new float[MaxRecords * 4];
    /// <summary>A <c>PartDanglyMesh</c>: its vertices move while its node matrix does not, so no record
    /// compare can infer motion for it. Captured at enrollment, as the caster pass captured it before.</summary>
    private static readonly bool[] _oDangly = new bool[MaxOwned];
    /// <summary>The frame the dangly solve last DISPLACED this slot's vertices past its still threshold
    /// (<see cref="NoteDanglyMoved"/>). Alloy owns the solve, so "is this banner moving" is a measurement
    /// and not a declaration: a settled cloak is a caster, not a mover. Stamp 0 = never moved.</summary>
    private static readonly int[] _oDanglyMovedAt = new int[MaxOwned];
    /// <summary>Path length the solve has displaced this slot's vertices since its last reported move. A
    /// one-frame test would miss slow continuous deformation (a banner's lean following the wandering breeze
    /// at well under the threshold per frame) and freeze its static shadow at the pose it was baked in.</summary>
    private static readonly float[] _oDanglyAcc = new float[MaxOwned];
    /// <summary>Frames a dangly slot stays a mover after its last measured displacement. Two covers either
    /// order of the Animate hook against the frame edge; the caster pass's own 8-frame hold sits on top.</summary>
    private const int DanglyMoveHold = 2;
    /// <summary>#302: the slot TENANCY generation -- which enrollment owns this slot, and which enrollment
    /// the record in it was built for. NOT the record epoch, and the distinction is a live-diagnosed flicker:
    /// the epoch is bumped by every wholesale invalidation -- enrollment, but ALSO an animated texture
    /// retiring a copy (<c>Drain -&gt; DropHandle</c>, roughly once a second in a torch-lit area) and a
    /// <c>/alloy</c> dial -- and those non-enrollment bumps land AFTER the frame's record build, so the next
    /// shadow bracket would see the whole table epoch-stale and refuse it: every shadow gone for one frame,
    /// once a second. What the bake actually must not trust is a record built for a DIFFERENT PART in the
    /// same slot, and only an enrollment can change that; so tenancy gets its own generation, bumped in
    /// Enroll alone and restamped by the same colour pass that rebuilds the records.
    /// <para><c>_enrollGen</c> starts at 1 so a record never built (stamp 0) disagrees with every real
    /// enrollment.</para></summary>
    private static readonly int[] _oEnrollGen = new int[MaxOwned];
    private static readonly int[] _recEnroll = new int[MaxOwned];
    private static int _enrollGen = 1;
    /// <summary>Casters admitted with an unknown (degenerate) sphere this frame -- each is a PERMANENT MOVER
    /// with infinite reach, so a nonzero steady value here means the re-bake predicate is being defeated and
    /// says by how much. Read and cleared by the caster period line.</summary>
    internal static int CasterDegenerate;

    /// <summary>Casters refused this frame for want of a record built for the slot's current tenant. Read
    /// and cleared by the caster period line; a number that stays high after an area settles means the colour
    /// build is skipping slots the bake wants.</summary>
    internal static int CasterNoRecord;

    /// <summary>#328: may the shadow pass cast this slot? A LEVEL maintained on the record write edge --
    /// true when a record landed for the slot's current tenant on a non-fading tile in a depth-writing
    /// class, false on every refusal (a skipped part, a re-tenanted or freed slot, a tile mid-fade). It
    /// replaces the per-caster-per-frame tile-fade chase and tenancy compare, and every FLIP bumps
    /// <see cref="CasterGen"/>. One frame late like the record it rides with, and coherent with it.</summary>
    private static readonly bool[] _castOk = new bool[MaxOwned];
    /// <summary>The slot's tile is mid-fade this frame, written by <c>RouteParts</c> where the fade is
    /// chased for the colour route anyway. Read on the record write edge into <see cref="_castOk"/>.</summary>
    private static readonly bool[] _oFading = new bool[MaxOwned];

    /// <summary>#328: the caster WORLD generation -- bumped whenever the set of castable slots, or a fact the
    /// bake caches about one (its class, material slots, VDO item, tenant), changes. <see cref="CasterPass"/>
    /// keeps its collected list across frames while this holds and walks only movers in between; a bump is
    /// what makes it re-collect. Compared, never consumed (ADR 0017): two readers see the same value.</summary>
    internal static int CasterGen = 1;

    /// <summary>The record epoch, for the shadow side's insurance re-collect: a bump retires texture copies
    /// (an animated diffuse cycling, a dial), which is the one input the collect caches that no slot fact
    /// names. Roughly once a second in a torch-lit area.</summary>
    internal static int RecordEpoch => _recEpoch;

    /// <summary>#336: the slots whose record CHANGED in the latest colour build -- the only slots whose mover
    /// level or caster sphere can differ from the frame before (both are written on that same edge; an
    /// untouched slot's <c>_recFrame</c> stamp reads it still). The shadow side's <c>Refresh</c> walks this
    /// list plus its own dynamic set instead of every caster. Reset by the first note of a new frame, so the
    /// shadow bracket (which runs BEFORE the build) reads the previous build's list intact; compared, never
    /// consumed. Overflow (a mid-frame epoch bump rebuilding every slot twice) sets the flag and the refresh
    /// falls back to the full walk for that frame.</summary>
    private static readonly int[] _castTouched = new int[MaxOwned];
    private static int _castTouchedN, _castTouchedFrame = -1;
    private static bool _castTouchedOverflow;

    internal static void NoteCasterTouched(int i)
    {
        BeginCasterTouched();
        // reserve-then-write: the stage's workers append concurrently, after the main thread's Begin
        int n = System.Threading.Interlocked.Increment(ref _castTouchedN) - 1;
        if (n < _castTouched.Length) _castTouched[n] = i; else _castTouchedOverflow = true;
    }
    /// <summary>The per-frame reset, hoisted so it runs on the main thread before any worker can append.</summary>
    private static void BeginCasterTouched()
    {
        if (_castTouchedFrame != _frameSeq) { _castTouchedFrame = _frameSeq; _castTouchedN = 0; _castTouchedOverflow = false; }
    }
    internal static int CasterTouchedCount => _castTouchedN;
    internal static int CasterTouchedSlot(int k) => _castTouched[k];
    internal static bool CasterTouchedOverflow => _castTouchedOverflow;
    /// <summary>Off-device only: the self-check seeds the list and clears it again.</summary>
    internal static void ResetCasterTouched() { _castTouchedN = 0; _castTouchedFrame = -1; _castTouchedOverflow = false; }

    /// <summary>Publish the slot's cast admission, bumping <see cref="CasterGen"/> only on a change.</summary>
    private static void SetCastOk(int i, bool ok)
    {
        if (_castOk[i] == ok) return;
        _castOk[i] = ok;
        // Off (a freed slot, an inactive LOD) is handed to the standing list's off queue: its entry turns into
        // a ghost and a zero-count draw at the next refresh. Only an on-flip the list cannot absorb re-collects.
        if (!ok) { CasterPass.NoteSlotOff(i); return; }
        if (!CasterPass.InList(i)) CasterGen++;
    }
    /// <summary>The stage's form: the generation bump is deferred to the fold, one per frame, so workers
    /// never touch the shared counter. Consumers compare generations, so one bump keeps the contract.</summary>
    /// <para>A flip is NOT a membership change while the caster list already holds this tenant: the list keeps
    /// the entry and its refresh turns it off (a ghost at its sphere) or back on (an appearance). Only a slot
    /// turning on that the list never collected under this tenant needs the re-collect. Tile fades, dissolves
    /// and texture skips are the flips that re-collected ~20k casters on a quarter of frames.</para>
    private static void SetCastOk(int i, bool ok, ref StageCounters c)
    {
        if (_castOk[i] == ok) return;
        _castOk[i] = ok;
        NoteCasterTouched(i);
        if (CasterPass.FlipCollect || (ok && !CasterPass.InList(i))) c.CastChanged = true;
    }
    internal static bool CastOk(int slot) => _castOk[slot];
    /// <summary>Off-device only: the caster self-check seeds admission for its synthetic slots.</summary>
    internal static void SelfCheckCastOk(int slot, bool ok) => _castOk[slot] = ok;
    internal static int SlotEnrollGen(int slot) => _oEnrollGen[slot];

    /// <summary>The mover LEVEL for one slot, read the same way <see cref="TryCaster"/> reads it -- the
    /// slot's most recent build was also a build that moved something.</summary>
    /// <para>#336: ...AND that build was last frame's. The route walk no longer stamps the slots it skips
    /// (the visible set never visits them), so a slot that moved on its last build and then left the view
    /// would otherwise read as a mover for as long as it stays out.</para></summary>
    internal static bool SlotRecMoved(int slot) => _recMovedAt[slot] == _recFrame[slot] && _recFrame[slot] == _frameSeq - 1;
    /// <summary>A dangly slot whose solve displaced it within <see cref="DanglyMoveHold"/> frames. A LEVEL:
    /// the stamp is re-read, never consumed.</summary>
    internal static bool SlotDangly(int slot) => _oDangly[slot] && _frameSeq - _oDanglyMovedAt[slot] <= DanglyMoveHold;
    /// <summary>Is the slot a <c>PartDanglyMesh</c> at all, moving or not. Its VDO is committed every frame
    /// the solve runs, so the vertex-dirty mover term must not speak for it -- the measured verdict does.</summary>
    internal static bool SlotIsDangly(int slot) => _oDangly[slot];

    /// <summary>The dangly solve's report for <paramref name="part"/>: this frame's largest per-vertex
    /// displacement, accumulated against <paramref name="still"/> (infinity forces a move). True when the
    /// slot became a mover. Hot (per animated part per frame): the O(1) enrollment-hash probe
    /// <see cref="NotePartDeath"/> uses, no allocation. A part not (yet) owned is not a caster and needs no stamp.</summary>
    internal static bool NoteDanglyMoved(IntPtr part, float dist, float still)
    {
        int i = OwnedSlotOf(part);
        if (i < 0 || !Accumulate(ref _oDanglyAcc[i], dist, still)) return false;
        _oDanglyMovedAt[i] = _frameSeq;
        return true;
    }

    /// <summary>The ownership slot holding <paramref name="part"/>, or -1: the O(1) enrollment-hash probe.</summary>
    private static int OwnedSlotOf(IntPtr part)
    {
        int mask = (1 << SlotHashBits) - 1;
        for (int j = SlotHash(part); _shGen[j] == _enrollGen; j = (j + 1) & mask)
            if (_oTri[_shSlot[j]] == part) return _shSlot[j];
        return -1;
    }

    /// <summary>The mover verdict: the displacement path length since the last report crosses the threshold.
    /// <c>still</c> 0 reports every frame with any motion, the pre-#383 behaviour.</summary>
    internal static bool Accumulate(ref float acc, float dist, float still)
    {
        acc += dist;
        if (!(acc > still)) return false;
        acc = 0f;
        return true;
    }

    /// <summary>Entries in the draw order -- the caster walk's own bound.</summary>
    internal static int OrderCount => _orderCount;
    /// <summary>The ownership slot at draw-order position <paramref name="k"/>. Grouped by class then VAO,
    /// which is the order a bake wants to batch in anyway.</summary>
    internal static int OrderSlot(int k) => _order[k];

    /// <summary>Everything one shadow caster is, resolved out of the ownership table and the records. Values,
    /// not references: the bake stages an index range and a slot number, and reads the rest through
    /// <c>uDraw[slot]</c> on the GPU.</summary>
    internal struct CasterFacts
    {
        public IntPtr Vdo, Mat;
        public int Item, Prim, Vao;
        /// <summary>World sphere: centre and radius, both from <see cref="_dCastSph"/>.</summary>
        public float Cx, Cy, Cz, R;
        /// <summary>The material's diffuse alpha is REFLECTIVITY rather than coverage (env-mapped), so its
        /// cutout runs at the threshold that only drops empty texels.</summary>
        public bool EnvAlpha;
        /// <summary>#363: a blended part -- it casts at its record alpha (<c>rot.w</c>), so it takes the masked
        /// stage whatever its diffuse says.</summary>
        public bool Blended;
        /// <summary>The record changed on its last build -- the matrix, the fade, a material colour.</summary>
        public bool RecMoved;
        /// <summary>A <c>PartDanglyMesh</c> the solve displaced this frame or last -- a mover no record compare
        /// can see. Still danglies are casters, not movers (<see cref="SlotDangly"/>).</summary>
        public bool Dangly;
        /// <summary>Posed by bones, so it is exempt from the sub-texel LOD cull for the same reason the
        /// colour cull exempts it: the player's own shadow must not flicker out in a far cascade.</summary>
        public bool Skinned;
    }

    /// <summary>Resolve one ownership slot as a shadow caster. False = this slot casts nothing: it is not an
    /// opaque or cutout part, its ownership was revoked, or the record write refused it (see
    /// <see cref="_castOk"/>). Never false for a slot whose record is merely epoch-stale: with agreeing
    /// tenancy it still carries the right geometry, and geometry is all the depth stages read.</summary>
    internal static bool TryCaster(int slot, out CasterFacts f)
    {
        f = default;
        // Opaque, cutout and (#363) blended -- the last at its alpha, through the masked stage's dither. An
        // additive part emits light and water is its own pass. Same rule the caster census applied.
        if (_oTri[slot] == IntPtr.Zero || _oCls[slot] > (byte)AlloyClass.Blended) return false;
        // #328: tenancy and tile fade, as the level the record write left -- deliberately NOT the record
        // epoch, see _oEnrollGen for the once-a-second flicker asking the wider question produced.
        if (!_castOk[slot]) { CasterNoRecord++; return false; }
        return TryCasterGeom(slot, out f);
    }
    /// <summary><see cref="TryCaster"/> without the admission gate: the geometry facts of a freshly seated
    /// slot whose record has not landed yet, so the standing caster list can take it as an OFF entry that the
    /// stage's admission turns on.</summary>
    internal static bool TryCasterGeom(int slot, out CasterFacts f)
    {
        f = default;
        if (_oTri[slot] == IntPtr.Zero || _oCls[slot] > (byte)AlloyClass.Blended) return false;
        f.Blended = _oCls[slot] == (byte)AlloyClass.Blended;
        if (f.Blended && _oBone[slot] >= 0) return false;   // posed: no mask stage, see SetCastOk
        f.Vdo = _oVdo[slot]; f.Mat = _oMat[slot];
        f.Item = _oItem[slot]; f.Prim = _oPrim[slot]; f.Vao = _oVao[slot];
        f.EnvAlpha = (_oSlots[slot] & EnvSlot) != 0;
        f.Skinned = _oBone[slot] >= 0;
        // Read as a LEVEL, never as an edge: _recFrame is stamped on every build and _recMovedAt only on a
        // build that changed something, so the two agreeing is "the last build moved it" and asking twice
        // gives the same answer. ADR 0017's hazard was a signal the first pass to ask consumed.
        f.Dangly = SlotDangly(slot);
        f.RecMoved = SlotRecMoved(slot);
        MoverSphere(slot, out f.Cx, out f.Cy, out f.Cz, out f.R);
        return true;
    }

    /// <summary>A caster's sphere as the bake ADMITS it: unknown BOUNDS mean cast ("unknown means cast" is
    /// right about bounds, never about a record built for another tenant -- that is <see cref="_castOk"/>).
    /// A degenerate sphere becomes an infinite radius, which passes every reach test and fails every
    /// sub-texel one, so such a caster is neither culled nor LOD-dropped. Matches CullPass -- and is COUNTED,
    /// because an infinite mover defeats the re-bake predicate in every view it is admitted to, which is
    /// exactly the silent cost ADR 0019 S3 exists to keep visible. The ghost sweep reads
    /// <see cref="CasterSphere"/> instead: a departing caster with unknown bounds takes the blanket.</summary>
    internal static void MoverSphere(int slot, out float cx, out float cy, out float cz, out float r)
    {
        CasterSphere(slot, out cx, out cy, out cz, out r);
        if (r > 0f) return;
        cx = 0f; cy = 0f; cz = 0f; r = NeverCull;
        CasterDegenerate++;
    }

    /// <summary>Cold, for the <c>/shadow movers</c> dump: the slot's diffuse texture name, the one label a
    /// part carries that a human can read. Borrowed pointers, resolved inside the pass slot.</summary>
    internal static string SlotTexName(int slot)
        => _oTri[slot] == IntPtr.Zero ? "-" : AlloyCensus.TexName(new MaterialRef(_oMat[slot]).Texture(0).Ptr);

    /// <summary>Is this ownership slot posed by bones? The cheap gate the caster walk uses to put the
    /// skinned casters at the head of its list without resolving every slot twice.</summary>
    internal static bool SlotSkinned(int slot) => _oBone[slot] >= 0;

    /// <summary>The engine wrote vertices into this VDO's position pool this frame, so every caster on it
    /// moved without its matrix moving. Resolved per VDO slot, not per caster.</summary>
    internal static bool VdoDirty(int v)
        => (uint)v < MaxVdos && _vVdo[v] != IntPtr.Zero && OwnedBuffers.DirtySince(_vPos[v].Buf);

    /// <summary>The ITEM-level vertex-dirty signal. <see cref="VdoDirty"/> is per pool BUFFER, and the engine
    /// pools a whole area into a handful of them (GL_MAX_ELEMENTS 1048576: ~9 VDOs for 16k parts), so one
    /// dangly solve committing one item marked every caster on its pool a vertex-dirty mover -- measured at
    /// 290-540 movers/frame, each a dynamic-set flip that re-baked the static cube it stood in and kept every
    /// sun cascade combined. <c>CommitItemDynamicData</c> names the item, so the hook below records
    /// (vdo, item) pairs for the frame and a caster is dirty only if ITS item was committed. A pool that is
    /// dirty with NO item commit on it moved nothing: UnlockPool's NAMED callers (Ghidra xrefs @0x55d3a0) are
    /// AddItem, Repack, Build, CommitItemStaticData, SetupArrays, the CPU hit-check/bbox reads and load-time
    /// generators (tangent basis, the IODispatcher model-load chain) -- none is motion -- and the motion
    /// writers that bypass the commit (PartDanglyMesh::Animate, PartSkin::PostProcess) are the dangly and
    /// skinned flags. Canary: a shadow that stops following an animated mesh that is neither dangly nor
    /// skinned is an unnamed bypass writer; /shadow itemdirty 0 is the revert. Reading such a pool as moved flipped its ~2500 casters between the
    /// shadow sets (DynFlip 470-735/frame in the hitch rows, Bake +3ms, Restage/Chase x2).</summary>
    internal static bool ItemDirty(int v, int item) => VdoDirty(v) && ItemMoved(v, item);
    /// <summary>The item half alone, for a caller that already holds the buffer verdict per VDO.</summary>
    internal static bool ItemMoved(int v, int item) => _ciOverflow || ItemCommitted(v, item);

    private const int CiBits = 14;
    private static readonly ulong[] _ciKey = new ulong[1 << CiBits];
    private static readonly int[] _ciGen = new int[1 << CiBits];
    private static readonly int[] _ciPerVdo = new int[MaxVdos];
    private static int _ciGeneration = 1, _ciCount;
    private static bool _ciOverflow;
    private static VdoCommitItemDynamicDataFn* _commitSlot;
    private const int CommitPriority = 10;
    private static long _ciCommits, _ciOverflows;

    private static int CiHash(ulong key) => (int)(key * 0x9E3779B97F4A7C15UL >> (64 - CiBits));

    private static bool ItemCommitted(int v, int item)
    {
        ulong key = ((ulong)(uint)v << 32) | (uint)item;
        int mask = (1 << CiBits) - 1;
        for (int j = CiHash(key); _ciGen[j] == _ciGeneration; j = (j + 1) & mask)
            if (_ciKey[j] == key) return true;
        return false;
    }

    /// <summary>Record one item commit. Engine thread, hot (every dangly/morph item per frame): a slot scan
    /// over ~10 VDOs and one hash insert, nothing allocated. Past the table's load the frame falls back to
    /// the buffer-level signal rather than dropping a commit.</summary>
    internal static void NoteItemCommit(IntPtr vdo, uint item)
    {
        _ciCommits++;
        int v = -1;
        for (int k = 0; k < _vdoCount; k++) if (_vVdo[k] == vdo) { v = k; break; }
        if (v < 0) return;
        if (_ciCount >= (1 << CiBits) * 3 / 4) { if (!_ciOverflow) _ciOverflows++; _ciOverflow = true; return; }
        ulong key = ((ulong)(uint)v << 32) | item;
        int mask = (1 << CiBits) - 1;
        int j = CiHash(key);
        for (; _ciGen[j] == _ciGeneration; j = (j + 1) & mask) if (_ciKey[j] == key) return;
        _ciGen[j] = _ciGeneration; _ciKey[j] = key; _ciCount++; _ciPerVdo[v]++;
    }

    /// <summary>Close the item window with the buffer one (<c>OwnedBuffers.ClearDirty</c>): the readers ran.</summary>
    internal static void ClearItemCommits()
    {
        _ciGeneration++; _ciCount = 0; _ciOverflow = false;
        Array.Clear(_ciPerVdo, 0, MaxVdos);
    }

    [UnmanagedCallersOnly]
    private static void OnCommitItemDynamicData(IntPtr vdo, uint item, uint flags)
    {
        NoteItemCommit(vdo, item);
        if (_commitSlot != null && *_commitSlot != null)
            (*_commitSlot)(vdo, item, flags);
    }

    /// <summary>The item table: a committed item is dirty, its pool-mates are not, a pool with no commit
    /// stays dirty as a whole, and the window closes.</summary>
    internal static void SelfCheckItemDirty(ExtenderLog log)
    {
        int sv = _vdoCount; var svVdo = new IntPtr[MaxVdos]; Array.Copy(_vVdo, svVdo, MaxVdos);
        ClearItemCommits();
        _vdoCount = 2; _vVdo[0] = (IntPtr)0x10; _vVdo[1] = (IntPtr)0x20;
        NoteItemCommit((IntPtr)0x10, 7); NoteItemCommit((IntPtr)0x10, 7);
        bool ok = ItemCommitted(0, 7) && !ItemCommitted(0, 8) && !ItemCommitted(1, 7) && _ciPerVdo[0] == 1 && _ciCount == 1;
        ok &= _ciPerVdo[1] == 0 && !ItemMoved(1, 7) && !ItemMoved(0, 8) && ItemMoved(0, 7);   // no commit = nothing moved
        NoteItemCommit((IntPtr)0x30, 1); ok &= _ciCount == 1;   // an unknown VDO is not booked
        ClearItemCommits();
        ok &= !ItemCommitted(0, 7) && _ciPerVdo[0] == 0;
        _vdoCount = sv; Array.Copy(svVdo, _vVdo, MaxVdos);
        log.Info(ok ? "AlloyRenderer.SelfCheckItemDirty: passed" : "AlloyRenderer.SelfCheckItemDirty: FAILED");
    }

    /// <summary>The last sphere written for a slot, live or revoked. A caster that VANISHED leaves no live
    /// entry to name the views it was standing in, so its last known sphere is what invalidates them.</summary>
    internal static void CasterSphere(int slot, out float cx, out float cy, out float cz, out float r)
    {
        fixed (float* sp = _dCastSph)
        {
            float* s = sp + (long)slot * 4;
            cx = s[0]; cy = s[1]; cz = s[2]; r = s[3];
        }
    }

    /// <summary>Does this VDO slot carry a diffuse UV stream? A masked caster needs one to sample its cutout
    /// with; without it there is nothing to test and the caster draws solid.</summary>
    internal static bool VdoHasUv(int v) => (uint)v < MaxVdos && _vUv[v].Present;

    /// <summary>The 4.6 VAO for a VDO slot, built on demand. Shadow and colour draw in the SAME context, so
    /// there is one VAO for both -- the whole reason the caster pass no longer keeps its own cache. 0 = the
    /// harvest could not build one and this caster does not cast this frame.</summary>
    internal static uint CasterVao(int v)
        => (uint)v < MaxVdos && EnsureVao(v) ? _vVao46[v] : 0u;

    /// <summary>#302 triage: what the bake will actually transform this slot by, beside what the engine says
    /// it is right now. The record is the shadow pass's ONE input for placement, so a disagreement here is
    /// the whole bug and a match acquits the data plane -- there is nothing else it could be reading.
    /// <para><paramref name="lag"/> is the record's translation, <paramref name="live"/> the node's this
    /// instant. They differ by one frame's motion on a mover and by nothing at all on a still part; anything
    /// else (a zero record, a wildly different place) is a slot whose record was never built.</para></summary>
    internal static void CasterProbe(int slot, out Vector3 lag, out Vector3 live,
                                     out bool mx, out bool known, out int feat)
    {
        fixed (float* d = _dData)
        {
            var m = *(Matrix4x4*)(d + (long)slot * DrawFloats);
            lag = new Vector3(m.M41, m.M42, m.M43);
            feat = (int)d[(long)slot * DrawFloats + 40];
        }
        mx = _oMx[slot];
        known = _recGen[slot] == _recEpoch;
        live = mx && _oNode[slot] != IntPtr.Zero ? new NodeRef(_oNode[slot]).Model.Translation : Vector3.Zero;
    }

    /// <summary>The shadow pass's own copy of the buffer names it binds: the persistent per-draw records and
    /// the bone block, both of which the bake reads through the SAME bindings the colour path uses.</summary>
    internal static uint DrawRecordBuf => _drawBuf;
    internal static uint BoneBlockBuf => _boneBuf;
    /// <summary>The bindless handle block the lit stages sample through; 0 until the first bindless upload.</summary>
    internal static uint TexHandleBuf => _texHBuf;
    /// <summary>The slot's diffuse handle as the block holds it; 0 = never minted (unlit, unmerged, or
    /// refused), which a sampler reads as black.</summary>
    internal static ulong RecordHandle0(int slot) => _dTexH[(long)slot * 8];
}
