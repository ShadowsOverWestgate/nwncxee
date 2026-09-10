using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Alloy.SceneLayout;

namespace Sowx.Alloy;

/// <summary>#173: the OWNERSHIP WALK. One pass over the scene's parts decides what the Alloy renderer takes this generation -- material read, feature classification, texture and VDO interning, the per-VDO attribute harvest and the 4.6-side VAO built over it -- and revocation is its inverse. Everything here is cold: it runs on an enrollment edge, never per frame.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Revoke one slot: the part reverts to engine drawing from its next iterator sighting, and the
    /// slot joins the free list for a later newcomer. Idempotent -- an already-empty slot is left alone, so
    /// no sweep needs a guard of its own and no slot can enter the free list twice.</summary>
    private static void Revoke(int i)
    {
        if (_oTri[i] == IntPtr.Zero) return;
        AlloyCensus.OwnFlag[_oEntry[i]] = 0;
        FreeSlot(i);
        _revokes++;   // mid-frame reverts only -- enrollment's own sweeps must not inflate `revokes=` (#149's triage habit)
    }

    /// <summary>#308: retire a slot WITHOUT touching its entry's OwnFlag or the revoke counter. The
    /// enrollment sweeps need this variant: a PartTable entry can change hands between enrollments, so by
    /// sweep time <c>_oEntry[i]</c> may name an entry a DIFFERENT slot claimed this scan -- clearing its
    /// flag would un-suppress a part Alloy is about to draw. The scan already bulk-cleared every flag and
    /// re-set the claimed ones, so the sweeps have no flag left to clear.</summary>
    private static void FreeSlot(int i)
    {
        if ((uint)_oEntry[i] < (uint)_entrySlot.Length && _entrySlot[_oEntry[i]] == i) _entrySlot[_oEntry[i]] = -1;
        _oTri[i] = IntPtr.Zero;
        SetCastOk(i, false);   // #328: a vacated slot casts nothing, and the shadow side re-collects on it
        _ownLive--;
        _freeSlots[_freeCount++] = i;
    }

    /// <summary>#308: stable ownership slots. A part KEEPS its slot across enrollments -- the slot index is
    /// the identity every persistent, slot-keyed table hangs off (the per-draw records, both sphere tables,
    /// the shadow tenancy stamps, the cull buffers), and the dense renumbering that used to happen here was
    /// the tenancy flap behind the shadow flicker: one part entering or leaving the candidate set shifted
    /// every slot scanned after it, each shifted slot was refused by the same-frame shadow bracket for want
    /// of a record, and the flap ghosted its views on the way out and re-baked them on the way back.
    /// <para>The hash maps a live tenant (its PartTriMesh*) to its slot for the next scan, generation-stamped
    /// so it never needs clearing; the free list feeds newcomers the slots that sweeps vacated; the claim
    /// stamp is how the post-scan sweep tells a re-claimed slot from a departed one.</para></summary>
    private static readonly int[] _oClaim = new int[MaxOwned];
    private static readonly int[] _freeSlots = new int[MaxOwned];
    private static int _freeCount, _ownLive;
    // MaxOwned MUST stay below 1<<SlotHashBits or HashLiveSlots's probe loop never finds an empty bucket
    private const int SlotHashBits = 19;   // 1<<19 = 524288, >= 3x MaxOwned: probe chains stay short
    private static readonly int[] _shSlot = new int[1 << SlotHashBits];
    private static readonly int[] _shGen = new int[1 << SlotHashBits];

    private static int SlotHash(IntPtr tri)
        => (int)((ulong)(long)tri * 0x9E3779B97F4A7C15UL >> (64 - SlotHashBits));

    /// <summary>Publish tri -&gt; slot for every live tenant under this enrollment's generation (so the table
    /// never needs clearing). The tenant pointer itself is not stored: <c>_oTri[_shSlot[j]]</c> IS the key,
    /// which holds because nothing reseats a hashed slot before the scan's last FindSlot -- the sweeps all
    /// run after the scan, and AllocSlot only hands out slots freed by earlier walks (#329's floor). A slot
    /// NotePartDeath revokes between two steps holds Zero, which no live tri matches.</summary>
    private static void HashLiveSlots()
    {
        int mask = (1 << SlotHashBits) - 1;
        for (int i = 0; i < _ownCount; i++)
        {
            if (_oTri[i] == IntPtr.Zero) continue;
            int j = SlotHash(_oTri[i]);
            while (_shGen[j] == _enrollGen) j = (j + 1) & mask;
            _shGen[j] = _enrollGen; _shSlot[j] = i;
        }
    }

    /// <summary>The slot this tenant held last enrollment, or -1 for a newcomer. A slot already claimed this
    /// scan is not offered twice (two entries carrying one tri would be a table bug; the second seats fresh).</summary>
    private static int FindSlot(IntPtr tri)
    {
        int mask = (1 << SlotHashBits) - 1;
        for (int j = SlotHash(tri); _shGen[j] == _enrollGen; j = (j + 1) & mask)
            if (_oTri[_shSlot[j]] == tri)
            { int i = _shSlot[j]; return _oClaim[i] == _enrollGen ? -1 : i; }
        return -1;
    }

    /// <summary>The <c>Part::~Part</c> death notice, delivered to the OWNERSHIP tables. #308 made slots
    /// persistent across enrollments, and enrollments run only every EnrollFloor-ish frames -- so between
    /// scans a dead tenant's <c>_oTri</c> kept dangling and every shadow bracket raw-chased it
    /// (<c>TryCaster</c>'s tile-fade read, <c>PoseSkinned</c>'s bone solve). On an area transition the
    /// engine frees parts by the thousand and returns their pages, which turns that chase from a stale read
    /// into the SIGSEGV under <c>Scene::Render</c>. Same doctrine as the part table's own tombstone (#145,
    /// #221): every owned pointer table gets the death notice, told, not inferred. O(1) through the
    /// enrollment hash; the vanish sweep then ghosts the freed slot at its last sphere, exactly as any
    /// mid-frame revoke. Compares the dying pointer only -- never dereferences it.</summary>
    internal static void NotePartDeath(IntPtr part)
    {
        int mask = (1 << SlotHashBits) - 1;
        for (int j = SlotHash(part); _shGen[j] == _enrollGen; j = (j + 1) & mask)
            if (_oTri[_shSlot[j]] == part) Revoke(_shSlot[j]);   // #329: EVERY slot -- a reseated part holds two until the finish
    }

    /// <summary>A newcomer's slot: a vacated one if any sweep left one, the next high-water slot otherwise.
    /// Never overflows past MaxOwned -- the capacity gate refuses on the LIVE count, and every slot below the
    /// high-water mark is either live or on the free list.
    /// <para>#329: only slots below <see cref="_freeAvail"/> are offered. The walk now spans frames, and a
    /// part death between two of them (NotePartDeath -> Revoke) pushes a slot ABOVE the floor; handing that
    /// slot out inside the same walk would re-tenant it in the scan that vacated it, which is the case
    /// #308's lapse-in-place shadow story rules out. The hole left below the floor is filled from the top.</para></summary>
    private static int AllocSlot()
    {
        if (_freeAvail == 0) return _ownCount++;
        int s = _freeSlots[--_freeAvail];
        _freeSlots[_freeAvail] = _freeSlots[--_freeCount];
        return s;
    }

    /// <summary>#329: the free-list floor a walk snapshots at its begin -- see <see cref="AllocSlot"/>.</summary>
    private static int _freeAvail;
    /// <summary>#329: the walk's resumable cursor over PartTable entries, -1 between walks. A walk in progress
    /// keeps going on every frame regardless of the floor or the dirty edge; the draw side keeps the previous
    /// complete census (<c>_order</c> and the class ranges are rebuilt only at <see cref="EnrollFinish"/>).</summary>
    private static int _walkAt = -1;
    /// <summary>#329: candidates this walk may process per frame; int.MaxValue for an unbudgeted one.</summary>
    private static int _walkBudget;
    /// <summary>#329: <c>enrollbudget</c> in sowx.cfg -- candidates per frame, 0 = the whole walk in one frame.
    /// 12ms walks measured at ~15k candidates put one candidate near 0,8us, so 1500 is ~1,2ms of walk plus the
    /// finish frame's sweeps and radix. Enrolment lag in a big area is the accepted cost (#329).
    /// ponytail: a fixed count, so the finish frame (sweeps, radix, MeshCensus) is unbudgeted and the
    /// per-candidate cost is assumed; the upgrade is a time budget off FrameZones.Now if p99 still misses.</summary>
    private static int _enrollBudget = 1500;
    /// <summary>#329: carried from begin to finish -- the candidate tally for the report line, the order count
    /// before this walk (the set-change edge for the shadow side), and whether the table was wiped at begin.</summary>
    private static int _walkCandidates, _walkOrderBefore;
    private static bool _walkWiped;

    /// <summary>#329: a walk is in progress for this Scene. The step gate and the begin gate must agree.</summary>
    private static bool Walking(IntPtr scene) => _walkAt >= 0 && scene == _enrollScene;

    /// <summary>#329: the free-list floor, checked once at Initialize on the empty table: a walk begins over
    /// two vacated slots, a death mid-walk frees a third, and only the first two may be handed out.</summary>
    internal static void EnrollSelfCheck(ExtenderLog log)
    {
        _freeSlots[0] = 7; _freeSlots[1] = 8; _freeCount = 2; _freeAvail = 2;
        _freeSlots[_freeCount++] = 9;
        bool ok = AllocSlot() == 8 && AllocSlot() == 7 && AllocSlot() == _ownCount - 1
                  && _freeCount == 1 && _freeSlots[0] == 9 && _freeAvail == 0;
        log.Info(ok ? "AlloyRenderer.EnrollSelfCheck: passed" : "AlloyRenderer.EnrollSelfCheck: FAILED -- a mid-walk vacated slot was re-offered inside the walk");
        _freeCount = 0; _freeAvail = 0; _ownCount = 0;
    }

    private static void RevokeAll(string why)
    {
        // #209: above the empty-table guard, because the ownership count says what ALLOY took and nothing
        // about what the passes captured -- and for beams, particles and grass this walk is the only
        // teardown there is. See docs/owned-resource-lifetimes.md.
        WipeHandles();
        // #212: above the guard for the same reason the registry walk is. The registry's Revoke deletes the
        // owned buffer copies, and the VAOs built over them are only invalidated here -- an enrollment that
        // took nothing (_ownCount == 0) with slots left from an earlier one would otherwise keep binding a
        // VAO whose buffers were just freed, permanently, with the map empty so no upload can re-copy them.
        // The VAO cache keys on VDO pointers that may now be freed: retire every slot for reuse. The GL
        // objects can only be deleted inside the bracket, so the rebuild happens there (EnsureVao).
        for (int v = 0; v < _vdoCount; v++) { _vVdo[v] = IntPtr.Zero; _vStale[v] = true; }
        PassRegistry.Global.Revoke();
        if (_ownCount == 0) return;
        for (int i = 0; i < _ownCount; i++) if (_oTri[i] != IntPtr.Zero) Revoke(i);
        _ownCount = 0; _freeCount = 0; _freeAvail = 0; _ownLive = 0;   // #308: the whole slot space is fresh again
        _walkAt = -1;   // #329: a walk in progress was over slots that no longer exist
        // ...and the draw order with it: an empty table must not leave class ranges pointing into it.
        _orderCount = 0;
        Array.Clear(_clsStart); Array.Clear(_clsEnd);
        _enrollScene = IntPtr.Zero;   // the table is gone; a bucket walk must not match a Scene we no longer hold
        // Re-arm the enrollment report: after a release (module unload, context loss) the NEXT enrollment is
        // a genuinely new fact -- whether ownership came back at all, and over how much. Suppressing it as a
        // repeat is what made a post-unload session look identical to one that never enrolled.
        _reportedHit = false;
        // #256: a wipe leaves the part table empty and the walk generation stale, so the next Tick must
        // re-enumerate rather than wait on an epoch bump that a release does not produce.
        SceneWalk.Dirty = true;
        _enrollDiagLeft = EnrollDiagBudget;
        _log?.Info($"AlloyRenderer: ownership released ({why}) -- all parts revert to the engine walk");
    }

    /// <summary>Cold, at a frame boundary before the Alloy block: rebuild the ownership table wholesale from
    /// PartTable's capture tables. This IS the frame-atomic swap -- nothing else adds an entry, so no part
    /// can change owner between the Alloy draw and the bucket walk that skips it.
    /// <para>#256: EDGE-TRIGGERED, not periodic. It runs when <see cref="SceneWalk"/> changed the record
    /// set (a part enumerated, bound or swept) or the Scene changed, and otherwise not at all -- so a
    /// settled area pays nothing for it, and a mid-area spawn is picked up on the frame the engine's own
    /// membership epoch moves rather than up to a second later.
    /// <para>#308: the SCAN is wholesale, the SLOTS are stable. Every candidate is re-gated and re-harvested
    /// on the edge, but a part that held a slot last enrollment is seated back in it -- the slot index is
    /// the identity every persistent slot-keyed table hangs off, and renumbering it was the shadow tenancy
    /// flap. Only genuine arrivals and departures move anything downstream.</para>
    /// <para>#329: BUDGETED. One walk is <see cref="EnrollBegin"/>, then one <see cref="EnrollStep"/> per
    /// frame until the cursor runs off the table, then <see cref="EnrollFinish"/> in that last step. The
    /// 12ms every-tenth-frame hitch was the whole walk on one frame; a frame mid-walk draws from the previous
    /// complete census, since nothing the draw side reads is rebuilt before the finish. A wipe, an empty
    /// census and <c>enrollbudget=0</c> walk unbudgeted -- there is nothing standing to draw from meanwhile.</para></summary>
    private static void Enroll(IntPtr scene)
    {
        if (!Walking(scene))
        {
            if (TryEnrollDelta(scene)) return;
            EnrollBegin(scene);
        }
        EnrollStep();
    }

    /// <summary>INCREMENTAL enrolment: the PartTable names the entries that changed since the last one
    /// (<see cref="SceneWalk.TakeChanges"/>), so only those are freed and re-seated; the 20k kept slots are
    /// not touched, hashed, re-read or compared, and the order is rebuilt by the O(live) radix. Falls back to
    /// the full walk on a new scene, a wiped table, an overflowed change list, or any REFUSAL -- the
    /// all-owned-or-none sweeps (instance, blended class) need the full scan's tallies. <c>enrolldelta=0</c>
    /// in sowx.cfg is the A/B. Runs in one frame: the change set is a few hundred parts at most.</summary>
    private static bool TryEnrollDelta(IntPtr scene)
    {
        bool newScene = scene != _enrollScene;
        bool tableWiped = _tableGen != PartTable.TableGen;
        int n = SceneWalk.TakeChanges(_deltaBuf, out bool overflow);
        // n == 0 is a request for a walk that names no entries (a deferred VDO's re-ask, a repack's stale
        // marks, a RequestWalk); only the full walk answers those.
        if (!_enrollDelta || newScene || tableWiped || overflow || n == 0 || _orderCount == 0 || _enrollBudget == 0)
        { _deltaFull++; return false; }
        SceneWalk.Dirty = false; _sinceEnroll = 0; _enrolls++; _deltaEnrolls++; _deltaChanges += n;
        _enrollGen++;
        // The tri->slot hash is keyed on _enrollGen: the death notice (NotePartDeath) looks tenants up
        // through it between enrolments, so a bump without a re-hash leaves every standing slot invisible
        // to it and a dying part's slot dangling -- the 14:42 SIGSEGV on a stage worker (AurTex.Live on a
        // freed material's texture). ~20k inserts, on the ~1 frame in 4 that takes a delta.
        HashLiveSlots();
        bool canLight = _lighting && !_litFailed;
        var c = default(AlloyCandidate);
        float* keep = stackalloc float[12];
        int orderBefore = _orderCount;
        bool refused = false, collect = false;
        _freeAvail = _freeCount;
        for (int k = 0; k < n; k++)
        {
            int e = _deltaBuf[k];
            int old = _entrySlot[e];
            if (old >= 0) { AlloyCensus.OwnFlag[e] = 0; FreeSlot(old); _freeAvail = _freeCount; }
            if (!AlloyCensus.TryOwnCandidate(e, ref c)) continue;
            int seat = EnrollOne(e, ref c, -1, canLight, keep);
            if (seat == Refused) refused = true;
            // A newcomer joins the standing caster list in place (as an off entry the stage's admission turns
            // on); one the list cannot take -- skinned, or no list standing -- costs the full re-collect.
            else if (!CasterPass.Append(seat)) collect = true;
        }
        RebuildOrder();
        if (collect) CasterGen++;
        _lastEnrolled = _orderCount;
        if (refused) { _deltaRefused++; return false; }   // the full walk follows, this frame
        return true;
    }
    private static readonly int[] _deltaBuf = new int[8192];
    private static readonly int[] _entrySlot = NewEntrySlot();
    private static int[] NewEntrySlot() { var a = new int[PartTable.EntryCount]; Array.Fill(a, -1); return a; }
    private static bool _enrollDelta = true;
    private static long _deltaEnrolls, _deltaChanges, _deltaFull, _deltaRefused;
    internal static string EnrollNote()
        => $"enrol: {_deltaEnrolls} incremental ({_deltaChanges} changed entries), {_deltaFull} full, {_deltaRefused} fell back on a refusal; the parts total is the last full walk's";

    private static void EnrollBegin(IntPtr scene)
    {
        // a new Scene walks unbudgeted: the standing order is the OLD scene's, and drawing it into the new
        // one for a walk's length is exactly the half-built census the budget must never show
        bool newScene = scene != _enrollScene;
        SceneWalk.Dirty = false; _sinceEnroll = 0; _enrolls++; _enrollScene = scene;
        // #284: a new enrollment re-asks every VDO the drip deferred for want of resident pools. This is
        // the bounded re-arm -- the alternative, one attempt per VDO for the session, rebuilds the exact
        // permanence the drip exists to remove whenever the first attempt loses a race with residency.
        DripRearm();
        // #256: consume the table-wipe edge HERE. A Scene change wipes the tables (SceneWalk.Reset) and this
        // enrollment then rebuilds ownership from the re-walked table in the same pass -- but the TableGen
        // edge is checked at the present, and would fire RevokeAll on an enrollment whose slots are in fact
        // current. Every area transition otherwise paid a wasted full scan, a handle wipe and a VAO retire,
        // an extra frame of nothing, and an "ownership released" line that named no real release.
        bool tableWiped = _walkWiped = _tableGen != PartTable.TableGen;
        _tableGen = PartTable.TableGen;
        _walkBudget = tableWiped || newScene || _orderCount == 0 || _enrollBudget == 0 ? int.MaxValue : _enrollBudget;
        _walkAt = 0; _walkCandidates = 0; _walkOrderBefore = _orderCount; _freeAvail = _freeCount;
        _watchEnrolled = 0;
        System.Array.Clear(AlloyCensus.WatchTex);
        for (int i = 0; i < _ownCount; i++)
            if (_oTri[i] != IntPtr.Zero)
            {
                if (AlloyCensus.WatchedEntry(_oEntry[i])) _log?.Info("[dumpframe] ENROLL cleared by the re-enrollment reset");
                AlloyCensus.OwnFlag[_oEntry[i]] = 0;
            }
        // #308: a wiped part table is dead pointers in every slot -- stability has no value across that
        // edge (matching a recycled address would seat a newcomer on a dead tenant's record), and keeping
        // the old high-water mark would ratchet the slot space by an area's population per transition. The
        // whole space resets and the scan below repacks densely; the shadow side gets the blanket at the
        // bottom of this pass.
        if (tableWiped) { _ownCount = 0; _freeCount = 0; _freeAvail = 0; _ownLive = 0; }
        _litUnownable = 0; _skinUnownable = 0; _gobOwned = 0;
        // #308: bumped per SCAN as before, but a slot now only ADOPTS the new generation when its tenant
        // changes -- the same-frame shadow bracket runs before the record rebuild, and must not refuse a
        // still world. The tenants that survive are found through the hash, seated in their old slots, and
        // keep their old stamp.
        _enrollGen++;
        HashLiveSlots();
        // the texture-key interning is scoped to ONE enrollment: it is a sort proxy only, so a fresh table
        // every time is both cheaper than invalidating it and immune to the recycled-pointer problem
        Array.Clear(_mKey, 0, MatHash);
        _matCount = 0;
        _capacitySpill = 0;
        AlloyCensus.OwnScanBegin(scene);
    }

    /// <summary>#329: up to <see cref="_walkBudget"/> candidates from the cursor, then the finish if the
    /// table ran out. Kept tenants are re-seated in place with the values they already hold, so a frame
    /// between two steps draws them unchanged; newcomers take slots outside the standing order and enter
    /// it at the finish; departures stay drawn until then.</summary>
    private static void EnrollStep()
    {
        // A lit part whose lighting cannot be reproduced (no normal stream, or the lit program failed to
        // build) is NOT enrolled: it goes back to the engine's own vanilla-lit draw, which is the only
        // fallback that holds "Alloy parts lit at least as well as vanilla". Fullbright is not.
        bool canLight = _lighting && !_litFailed;
        var c = default(AlloyCandidate);
        // #308 follow-up: scratch for the kept-slot stable-half content compare below -- 8 PBR params + 4
        // feature floats. Outside the loop: a stackalloc inside it would stack a copy per candidate.
        float* keep = stackalloc float[12];
        int stepped = 0;
        for (; _walkAt < PartTable.EntryCount; _walkAt++)
        {
            if (stepped >= _walkBudget) return;   // resume here next frame
            int e = _walkAt;
            if (!AlloyCensus.TryOwnCandidate(e, ref c)) continue;
            stepped++; _walkCandidates++;
            if (AlloyCensus.WatchAccepted(e, c.Mat)) _watchEnrolled++;
            EnrollOne(e, ref c, FindSlot(c.Tri), canLight, keep);
        }
        // a budgeted walk finishes on a frame of its OWN: measured live, a finish riding the last step was
        // the 3-5,6ms p99 (step 1,3ms + sweeps + radix + census) against a 2ms bar
        if (stepped > 0 && _walkBudget != int.MaxValue) return;
        _walkAt = -1;
        EnrollFinish();
    }

    private const int Refused = -1;

    /// <summary>One candidate's acceptance: the refusals, the seat (kept in place or a fresh slot), the
    /// material/VAO/box reads, the stable-half compare. Shared by the full walk (<paramref name="slot"/> from
    /// <see cref="FindSlot"/>) and the incremental path (always -1: a changed entry is seated fresh).
    /// Returns the slot, or <see cref="Refused"/>.</summary>
    private static int EnrollOne(int e, ref AlloyCandidate c, int slot, bool canLight, float* keep)
    {
        // #126: a refusal from here on is counted against the class it would have joined -- see the
        // all-owned-or-none sweep below. The surplus stays engine-drawn, never dropped.
        bool watch = AlloyCensus.WatchedEntry(e);
        // #128: the two CAPACITY refusals do not go to the gob. They are not properties of the instance
        // -- the same creature is ownable in an emptier area -- and routing them there turns a full table
        // into a cliff: thousands of gobs marked, the refused set overflows, and the sweep then drops
        // EVERY instance in the scene rather than the surplus. "The surplus stays engine-drawn, never
        // dropped" is the rule, and at capacity a creature split across two renderers (a lighting
        // mismatch) beats every creature in the area reverting.
        // #308: the slot this tenant held last enrollment, found up front so a KEPT tenant can never be
        // spilled by capacity -- the live count includes this scan's not-yet-swept departures, and
        // refusing a kept tenant on their account would oscillate the whole table at the full boundary.
        int i = slot;
        bool kept = i >= 0;
        bool lit = c.Lit && _lighting;
        // The record survives its tenant only while the stable half still describes it: a changed
        // tenant rebuilds from scratch, and so does a kept one whose harvest drifted under it (a
        // rebind moved the node or material, the walk re-seated the VDO item) or whose class moved.
        // Everything WriteStable reads is derived from these fields, and _recAur catches the textures.
        // ponytail: the eight-field list mirrors WriteStable's transitive inputs by hand; grow that
        // function and this compare must grow with it, or kept slots serve stale stable halves.
        // #329: a drifted tenant is reseated as a NEWCOMER. Rewriting its old slot in place mid-walk
        // would draw it under the standing order's old class with new state, and finishing the walk
        // in-frame on every drift ran half the walks of a busy area whole (measured: p99 13-15ms).
        // The old slot keeps drawing what it held until the departed sweep vacates it at the finish;
        // a pointer drift never draws stale because BuildBatches revokes on the same identity compare.
        bool drifted = kept && (_oMat[i] != c.Mat || _oNode[i] != c.Node || _oVdo[i] != c.Vdo
            || _oItem[i] != c.Item || _oRotBits[i] != c.RotBits || _oMx[i] != c.Mx || _oLit[i] != lit
            || _oCls[i] != (byte)c.Cls);
        if (drifted) kept = false;
        if (!kept && _ownLive >= MaxOwned)
        { _capacitySpill++; AlloyCensus.OwnRefusedByClass[c.Cls]++; AlloyCensus.RejReason(AlloyCensus.RejTableFull, c.Cls); if (watch) _log?.Info("[dumpframe] ENROLL refused: table full"); return Refused; }
        int v = VdoSlot(c.Vdo);
        if (v < 0)
        { AlloyCensus.OwnRefusedByClass[c.Cls]++; AlloyCensus.RejReason(AlloyCensus.RejNoVao, c.Cls); if (watch) _log?.Info("[dumpframe] ENROLL refused: no VAO slot (harvest failed)"); return Refused; }
        // #137: a true skinmesh (a cloak, a robe, a tail) carries the bone streams and is POSED by them.
        // #128 refused it here for want of a skinning branch; the stages have one now, so the only
        // refusal left is the one #128's reason still holds for -- the kill switch off, or a bone stream
        // on a VDO whose part is not a PartSkin (so nothing addresses those bones). Either way it is
        // refused against the GOB, and the sweep below drops the creature wearing it entire. #285: that
        // is the whole creature NOT DRAWN, not one drawn vanilla -- the sweep is all-or-nothing so the
        // creature is not split across two renderers, and `instances:` on the census line is the count.
        // Refused in BOTH directions, because both mismatches are misrenders. A bone stream on a part
        // with no skin identity has no bone map to solve against; a PartSkin whose VAO harvest came back
        // boneless would enrol as an ordinary rigid part, skip PoseSkinned, and draw in its bind pose --
        // which is the failure the asymmetric gate would have let through as the QUIET one.
        if (_vSkin[v] != (_skinning && PartTable.Skinned(e)))
        { _skinUnownable++; AlloyCensus.OwnRefuse(c.Cls, c.Gob); if (watch) _log?.Info($"[dumpframe] ENROLL refused: skin mismatch (armed={_skinning} boneStreams={_vSkin[v]} skinPart={PartTable.Skinned(e)})"); return Refused; }
        if (lit && (!canLight || !_vLit[v]))
        { _litUnownable++; AlloyCensus.OwnRefuse(c.Cls, c.Gob); if (watch) _log?.Info($"[dumpframe] ENROLL refused: lit but unshadeable (canLight={canLight} vdoLit={_vLit[v]})"); return Refused; }
        // #121: an env-mapped part the engine draws UNLIT would go through the fullbright variant, which
        // has no env handling at all -- the engine still routes it to RenderEnvironmentMapped (the switch
        // in PartTriMesh::Draw is on HasEnvMap, not on the lit flag), so owning it would silently drop
        // whatever shine it had. Only the lit variant can reproduce it; the rest stay vanilla.
        if (c.Env && !lit)
        { _litUnownable++; AlloyCensus.OwnRefuse(c.Cls, c.Gob); if (watch) _log?.Info("[dumpframe] ENROLL refused: env-mapped but unlit"); return Refused; }
        // #308: seat the tenant. A part that held a slot last enrollment keeps it -- the slot index is
        // what every persistent table is keyed by, so keeping it is what keeps the record, the spheres
        // and the shadow tenancy coherent across the scan. Only a genuine newcomer takes a fresh slot
        // and the new tenancy generation; the #302 (review) intent -- the stamp moves ONLY when the
        // tenant does -- is now true by construction (the old compare ran AFTER _oTri was overwritten,
        // so it never fired and every re-tenanted slot was trusted with its previous tenant's record).
        if (!kept) i = AllocSlot();
        _oClaim[i] = _enrollGen;
        if (!kept) UnstampRecord(i);
        // #328: a new tenant has no record the bake may trust until the next write lands one.
        if (!kept) SetCastOk(i, false);
        // A kept tenant whose identity fields drifted may not be the same part at all -- a freed
        // PartTriMesh recycled at the same address inside one frame window would otherwise inherit the
        // dead part's record with no vanish, appear, or move ever reaching the shadow side. Drift is
        // rare (a rebind, a walk re-seat) so the cost of suspicion is one honest one-frame flap.
        if (!kept) _oEnrollGen[i] = _enrollGen;
        if (!kept) _ownLive++;
        // A NEWCOMER joins the tri->slot hash now, not at the next scan's HashLiveSlots: the death
        // notice (NotePartDeath) looks tenants up through this hash BETWEEN scans, and a part that
        // enrolled and died inside one enrollment gap would otherwise stay chased by the shadow
        // bracket as a dangling pointer. Appending mid-scan is safe for FindSlot -- a hashed slot
        // claimed this scan is refused by the _oClaim compare exactly as before.
        if (!kept)
        {
            int hj = SlotHash(c.Tri);
            while (_shGen[hj] == _enrollGen) hj = (hj + 1) & ((1 << SlotHashBits) - 1);
            _shGen[hj] = _enrollGen; _shSlot[hj] = i;
        }
        _oEntry[i] = e; _entrySlot[e] = i; _oTri[i] = c.Tri; _oNode[i] = c.Node; _oMat[i] = c.Mat; _oVdo[i] = c.Vdo;
        _oItem[i] = c.Item; _oPrim[i] = c.Prim; _oVao[i] = v; _oMx[i] = c.Mx; _oLit[i] = lit;
        _oRotBits[i] = c.RotBits;
        _oTexKey[i] = TexKey(c.Tex0);
        _oCls[i] = (byte)c.Cls; _oSrc[i] = (byte)c.Src; _oDst[i] = (byte)c.Dst; _oWash[i] = c.Wash ? (byte)1 : (byte)0;
        _oNoCull[i] = c.TwoSided;
        _oBucket[i] = (byte)c.Bucket; _oGob[i] = c.Gob; _oLodRoot[i] = c.LodRoot;
        // Sampled HERE, cold, and never re-read: the walk has just proved this gob live, where the route
        // walk has not -- every other reader of _oGob only COMPARES it (or derefs it behind the LodRoot
        // guard), so a per-frame deref would be a new way to touch a freed gob. A gob does not become a
        // tile or stop being one.
        _oTileGob[i] = c.Gob != IntPtr.Zero && *(IntPtr*)(c.Gob + GobTileData) != IntPtr.Zero;
        for (int b = 0; b < 6; b++) _oBox[i * 6 + b] = *(float*)(c.Tri + PartBoxMin + b * 4);   // #336: +0x64..+0x78
        // Kept tenants keep their sticky bone slot too (#308 follow-up); a fresh tenant starts unposed
        // and PoseSkinned's orphan sweep reclaims whatever the previous tenant held.
        if (!kept) _oBone[i] = -1;
        _oDangly[i] = PartTable.IsDangly(c.Tri);   // #302: a permanent shadow mover
        if (c.Gob != IntPtr.Zero) _gobOwned++;
        // #126: the local bbox centre, for the blended pass's back-to-front key.
        var tri = new PartRef(c.Tri);
        float* lo = tri.BboxMin, hi = tri.BboxMax;
        _oCx[i] = (lo[0] + hi[0]) * 0.5f; _oCy[i] = (lo[1] + hi[1]) * 0.5f; _oCz[i] = (lo[2] + hi[2]) * 0.5f;
        float ex = hi[0] - lo[0], ey = hi[1] - lo[1], ez = hi[2] - lo[2];
        _oR[i] = 0.5f * MathF.Sqrt(ex * ex + ey * ey + ez * ez);
        // #330: spawn-time fields the chase keys on without touching the part again.
        _oTile[i] = tri.Tile; _oBsp[i] = PartTable._pBsp[e];
        // #308 follow-up: a kept slot's record survives the scan, but the .mtr CONTENT under a stable
        // Material* can still move (the engine exports runtime SetMaterialParam mutators). ReadMaterial
        // rewrites the CPU arrays either way; snapshot them first and unstamp on any drift, which is
        // the per-slot form of the heal the deleted whole-table epoch bump used to provide.
        bool stableHalf = kept;
        int po = i * 8, fo = i * 4, oldSlots = _oSlots[i];
        if (stableHalf)
        {
            for (int k = 0; k < 8; k++) keep[k] = _oParam[po + k];
            for (int k = 0; k < 4; k++) keep[8 + k] = _oFeat[fo + k];
        }
        ReadMaterial(c.Mat, i, c.Feat);
        // #121: bit 5 of the slot mask, and the one slot with no texture behind it -- the probe replaces
        // what the material's env map held, so all that is carried is the flag itself.
        if (c.Env) _oSlots[i] |= EnvSlot;
        if (stableHalf)
        {
            bool moved = oldSlots != _oSlots[i];
            for (int k = 0; k < 8 && !moved; k++) moved = keep[k] != _oParam[po + k];
            for (int k = 0; k < 4 && !moved; k++) moved = keep[8 + k] != _oFeat[fo + k];
            if (moved) { UnstampRecord(i); CasterGen++; }   // #328: the env-alpha bit rides _oSlots
        }
        float rad = BitConverter.Int32BitsToSingle(c.RotBits) * DegToRad;
        _oCos[i] = MathF.Cos(rad); _oSin[i] = MathF.Sin(rad);
        AlloyCensus.OwnFlag[e] = 1;
        if (watch)
            // #336: what GL actually holds for the diffuse, on the line that says the part was taken.
            // A UV-less mesh reads ONE texel at the generic (0,0), so "the whole model is the wrong
            // colour" is a question about that texel -- and the uploaded format and the mip chain are
            // the two things between the file and it. Both have been wrong before (#265, #129).
            _log?.Info($"[dumpframe] ENROLL accepted slot={i} cls={(AlloyClass)c.Cls} lit={lit} vao={v}"
                       + $" uv={(SlotHasUv(v) ? 1 : 0)}"
                       + $" tex0 {TexProbe.TexFormat(new TextureRef(c.Tex0).OwnedTid())}"
                       + $" {TexProbe.TexTopMip(new TextureRef(c.Tex0).OwnedTid())}");
        return i;
    }

    /// <summary>#329: the walk's atomic tail -- the sweeps, the handle wipe and the order rebuild, all in
    /// one frame, so the census the draw side reads goes from one complete state to the next.</summary>
    /// <summary>The four-pass radix over every live slot: texture key, feature/primitive, VAO sort key, class.
    /// O(live) integer passes -- cheap enough to re-run after an incremental enrolment too.</summary>
    private static void RebuildOrder()
    {
        _litCount = 0;
        Array.Clear(_sortCount, 0, MaxVdos * 4);
        Array.Clear(_texSortCount, 0, MaxMats);
        Array.Clear(_featPrimCount);
        Array.Clear(_clsCount); Array.Clear(_clsStart); Array.Clear(_clsEnd);
        for (int i = 0; i < _ownCount; i++)
        {
            if (_oTri[i] == IntPtr.Zero) continue;
            _sortCount[SortKey(i)]++;
            _texSortCount[_oTexKey[i]]++;
            _featPrimCount[FeatPrimKey(i)]++;
            _clsCount[_oCls[i]]++;
            if (_oLit[i]) _litCount++;
        }
        // Four-digit LSD radix into _order, every digit counting-sorted (stable): texture, then #327's
        // (feature, primitive), then (lit, #292's no-cull bit, VDO), then #126's class. The result walks
        // grouped by CLASS -- so each pass owns a contiguous batch range -- and inside a class by (lit, cull,
        // VDO, feature, primitive) with equal textures contiguous, which is the whole enrollment-static half
        // of the batch cut key. The draw loop then cuts a batch on a key change instead of searching for one;
        // the one per-frame term left, the dissolve bit, is RouteParts's partition.
        int at = 0;
        for (int k = 0; k < MaxMats; k++) { int n = _texSortCount[k]; _texSortCount[k] = at; at += n; }
        for (int i = 0; i < _ownCount; i++) if (_oTri[i] != IntPtr.Zero) _orderTmp[_texSortCount[_oTexKey[i]]++] = i;
        int live = at;
        at = 0;
        for (int k = 0; k < FeatPrimKeys; k++) { int n = _featPrimCount[k]; _featPrimCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _orderTmp[k]; _order[_featPrimCount[FeatPrimKey(i)]++] = i; }
        at = 0;
        for (int k = 0; k < MaxVdos * 4; k++) { int n = _sortCount[k]; _sortCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _order[k]; _orderTmp[_sortCount[SortKey(i)]++] = i; }
        at = 0;
        for (int k = 0; k < AlloyClasses.Count; k++) { int n = _clsCount[k]; _clsStart[k] = at; _clsCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _orderTmp[k]; _order[_clsCount[_oCls[i]]++] = i; }
        // ...and the cursor each class's fill left behind IS its end index.
        for (int k = 0; k < AlloyClasses.Count; k++) _clsEnd[k] = _clsCount[k];
        _orderCount = live;
    }

    private static void EnrollFinish()
    {
        bool tableWiped = _walkWiped;
        int candidates = _walkCandidates, orderBefore = _walkOrderBefore;
        WipeHandles();   // #153: still per-enrollment -- a no-op in the armed mode, and in the non-armed one
                         // it is the only heal for the engine-name handle cache (#220). #308 removed only
                         // the unconditional record-epoch bump that rode with it: slots are stable now, so
                         // the records a scan orphans are unstamped one by one where they are re-seated.
        // #308: the departed sweep -- slots no candidate re-claimed. Their parts left the table (or the
        // walk's candidate set), so the slot is vacated for a later newcomer. FreeSlot, not Revoke: the
        // OwnFlag under _oEntry may have changed hands this scan (see FreeSlot), and the bulk clear above
        // already took care of every flag. Runs BEFORE the all-owned-or-none sweeps below, so those only
        // ever look at slots seated this scan, whose entries are current and safe to Revoke through.
        for (int i = 0; i < _ownCount; i++)
            if (_oTri[i] != IntPtr.Zero && _oClaim[i] != _enrollGen)
            {
                if (AlloyCensus.WatchedEntry(_oEntry[i])) _log?.Info("[dumpframe] ENROLL departed: slot vacated");
                FreeSlot(i);
            }
        // #107 s2 / #126: all-owned-or-none, for the one class whose draw ORDER is load-bearing. A half-owned
        // blended set has no coherent order -- our members draw sorted at the frame anchor and the engine's
        // leftovers draw unsorted later, so a window in front of a window composites by whichever renderer
        // happened to get it. Cutout and additive are order-independent by construction (depth-writing with a
        // discard, and addition commutes), so a partial one is merely a partial speed-up, not a wrong frame.
        // #128 / #107 s2: all-owned-or-none per INSTANCE, which is the creature class's version of the same
        // rule. A creature's opacity is a per-gob field re-evaluated every frame, so a half-owned creature is
        // a creature whose head fades and whose arm does not -- and the part we could not take (a skinmesh
        // cloak, a gob with an env override) is exactly the one that would be left standing. Dropping the
        // instance whole is the only state that reads as one renderer.
        _gobParts = 0;
        for (int i = 0; i < _ownCount; i++)
            if (_oTri[i] != IntPtr.Zero && AlloyCensus.GobRefused(_oGob[i]))
            {
                if (AlloyCensus.WatchedEntry(_oEntry[i])) _log?.Info("[dumpframe] ENROLL revoked: instance dropped whole");
                AlloyCensus.OwnFlag[_oEntry[i]] = 0; FreeSlot(i); _gobParts++;
            }
        _blendedDropped = 0;
        if (AlloyCensus.OwnRefusedByClass[(int)AlloyClass.Blended] > 0)
            for (int i = 0; i < _ownCount; i++)
                if (_oTri[i] != IntPtr.Zero && _oCls[i] == (byte)AlloyClass.Blended)
                {
                    if (AlloyCensus.WatchedEntry(_oEntry[i])) _log?.Info("[dumpframe] ENROLL revoked: blended class dropped whole");
                    AlloyCensus.OwnFlag[_oEntry[i]] = 0; FreeSlot(i); _blendedDropped++;
                }
        // The radix digits are counted AFTER the sweep, so a dropped class costs no order slot.
        RebuildOrder();
        // #308: the set-signature blanket is retired with the renumbering that made it necessary. A slot
        // is never re-tenanted within the scan that vacates it (the departed sweep runs after the scan, and
        // newcomers pop only slots freed by EARLIER enrollments), so a departing caster now always lapses
        // in place: the vanish sweep ghosts it at its last sphere and re-bakes exactly the views it stood
        // in, and a newcomer fires an appeared event when its record lands. The one edge with no per-slot
        // story left is the part-table wipe -- dead pointers everywhere, every slot vacated at once -- and
        // that keeps the blanket.
        if (tableWiped) CasterPass.ShadowInvalidate();
        // #328: a wipe reseats every slot without a FreeSlot, and a population change is a set change
        // whatever the per-slot stamps say -- both are re-collect edges for the shadow side.
        if (tableWiped || _orderCount != orderBefore) CasterGen++;

        // #302: there is ONE enrollment now. The shadow pass walks this same table, so an instance that
        // changes hands changes hands on both sides in the same breath -- the two counters, and #128's
        // frames of darkness between them, are gone.
        _lastEnrolled = _orderCount; _lastCandidates = candidates; _lastTextures = _matCount;
        // Empty right after a resume means the records are not RESIDENT yet, not that nothing is ownable --
        // a fresh Scene binds its VertexPrimitives over the next few frames. OnRenderSinglePass's bounded
        // forced-enroll budget is what retries; nothing here may re-arm unboundedly.
        if (!PartTable.Active)
        {
            if (_enrollDiagLeft-- > 0)
                _log?.Info("AlloyRenderer: part capture inactive (drawhooks != batch or hooks unavailable) -- " +
                           "the Alloy pass enrolls from its capture tables, so nothing can be owned");
            return;
        }
        // a miss spends a diagnostic, a hit reports once and stops: a scan during the loading screen has
        // nothing to offer yet and must not burn the budget before there is an area to enroll from
        if (_orderCount == 0)
        {
            if (_enrollDiagLeft-- > 0)
                _log?.Info($"AlloyRenderer: enroll miss -- {AlloyCensus.OwnScanDiag()}");
        }
        else if (!_reportedHit)
        {
            _reportedHit = true;
            _log?.Info($"AlloyRenderer: enrolled {_orderCount}/{candidates} ownable parts on {_vdoCount} VDOs " +
                       $"({AlloyCensus.OwnScanDiag()}; {VertexArrays.Unaligned}/{VertexArrays.Streams} streams off 4-byte alignment)");
        }
    }

    /// <summary>Find (or harvest) the VAO-cache slot for a VDO. Harvesting reads the engine VAO's attribute
    /// formats -- engine context, read-only queries, binding restored -- so the 4.6 side can rebuild an
    /// equivalent VAO over the same shared buffers.</summary>
    private static int VdoSlot(IntPtr vdo)
    {
        int s = -1;
        for (int v = 0; v < _vdoCount; v++)
        {
            if (_vVdo[v] == vdo)
            {
                if (_vSig[v] == VdoSig.Of(vdo)) return v;
                // Same address, different VDO: the one harvested here died and operator new handed its
                // block out again. Vacate and re-harvest on the spot; a stale layout must never be served.
                _vdoRecycled++;
                _vVdo[v] = IntPtr.Zero; _vElemBuf[v] = 0; DripForget(vdo);
                if (_recycleDiagLeft-- > 0)
                    _log?.Info($"AlloyRenderer: VDO {vdo:X} re-minted under slot {v} -- re-harvested");
                s = v;
                break;
            }
            if (_vVdo[v] == IntPtr.Zero && s < 0) s = v;      // retired slot: reuse it, VAO rebuilt on use
        }
        if (s < 0)
        {
            if (_vdoCount >= MaxVdos) return -1;
            s = _vdoCount++;
            _vVao46[s] = 0; _vOwned[s] = false; ForgetEngineVao(s);
        }
        if (!Harvest(vdo, s)) return -1;
        _vVdo[s] = vdo; _vStale[s] = _vVao46[s] != 0; _vSig[s] = VdoSig.Of(vdo);
        // #136: a slot re-pointed at a different VDO has different streams, so its engine-layout VAO is
        // wrong in exactly the way EnsureVao's staleness flag exists to catch for the 4.6 one.
        ForgetEngineVao(s);
        return s;
    }

    /// <summary>#244: census the repeated population rather than assuming it -- or assuming it away. Runs on
    /// the enrollment cadence over the WHOLE ownership table (tileset parts, placeables and creature parts
    /// alike), building two keys per part and measuring the equal runs of each.
    /// <para><b>The range key</b> is (VDO, matrix-class, first index, element count): parts that draw the
    /// SAME geometry out of the buffer. Matrix-class is in it because it changes what a shared mesh means --
    /// an object-space part carries its placement in a model matrix, so N of them on one range is N
    /// instances of one draw, while a world-space part has an identity matrix with its placement baked into
    /// the vertices, so two on one range would be the same triangles drawn twice in the same place.</para>
    /// <para><b>The shape key</b> is (gob?, primitive, diffuse texture, element count): parts that draw the
    /// same MESH, wherever its vertices sit. This is the one the range key cannot see, and it is the case
    /// that matters on real content -- an area's fifty identical barrels are fifty placements of one model,
    /// and if the engine copies that model's vertices into the buffer once per placement then every range is
    /// distinct while the geometry is the same fifty times over. A range census alone reads that as "no
    /// repetition", which is false in the way that costs the most.</para>
    /// <para>The shape key is a SIGNATURE, not a proof: two different meshes sharing a texture and an index
    /// count collide. It is built only from values the enrollment already validated -- no new pointer chase,
    /// and specifically not the vertex-item count, which nothing in the renderer reads and which this is not
    /// entitled to guess. Read it as a screen: 1,00x is conclusive (nothing repeats), and anything above it
    /// is a population worth confirming before it is designed against.</para>
    /// <para>Cold by construction -- period-line cadence (#329 took its two sorts off the walk's finish
    /// frame), static scratch arrays, sorted in place.</para>
    /// </summary>
    private static void MeshCensus()
    {
        int n = 0;
        _msUnread = 0; _msGobParts = 0; _msMxParts = 0;
        for (int i = 0; i < _ownCount; i++)
        {
            if (_oTri[i] == IntPtr.Zero) continue;
            // The index range this part draws, read the way the staging walk reads it -- and guarded the same
            // way, because this is the one place the census dereferences engine memory rather than its own
            // tables. A part whose range will not resolve is left OUT and counted, so a census that went
            // blind says so instead of reporting the survivors as the population.
            var vdo = new VdoRef(_oVdo[i]);
            long elem = vdo.ElemItems;
            int item = _oItem[i];
            if (elem == 0 || item < 0 || item >= vdo.ElemCount) { _msUnread++; continue; }
            long first = (uint)*(int*)(elem + item * 8);
            int count = *(int*)(elem + item * 8 + 4);
            if (count <= 0) { _msUnread++; continue; }
            bool gob = _oGob[i] != IntPtr.Zero;
            if (gob) _msGobParts++;
            long clamped = (uint)Math.Min(count, MsCountMax);
            _msKey[n] = ((long)(uint)_oVao[i] << MsVaoShift) | (_oMx[i] ? MsMxBit : 0L)
                        | (first << MsFirstShift) | clamped;
            if (_oMx[i]) _msMxParts++;
            _msShape[n] = (gob ? MsGobBit : 0L) | (_oMx[i] ? MsShapeMxBit : 0L)
                          | ((long)(uint)_oPrim[i] << MsPrimShift)
                          | ((long)(uint)_oTexKey[i] << MsTexShift) | clamped;
            n++;
        }
        _msParts = n;
        Array.Sort(_msKey, 0, n);
        Array.Sort(_msShape, 0, n);
        ScanRuns(_msKey, n, MsMxBit, out _msMeshes, out _msMaxInstances, out _msShared, out _msSharedMx);
        ScanRuns(_msShape, n, MsGobBit, out _msShapes, out _msShapeMax, out _msShapeRepeat, out _msShapeRepeatGob);
        // Same sorted key, a second flag: how much of the repeated population is COLLAPSIBLE at all. Cheaper
        // than a second key, and the three run figures it recomputes are identical by construction.
        ScanRuns(_msShape, n, MsShapeMxBit, out _, out _, out _, out _msShapeRepeatMx);
    }

    /// <summary>#244: the equal runs of a sorted census key -- distinct values, the largest run, how many
    /// parts sit in a run of two or more, and how many of THOSE carry <paramref name="flag"/>. One scanner
    /// for both keys: the two differ in what they pack, never in how a run is counted.</summary>
    private static void ScanRuns(long[] key, int n, long flag,
                                 out int groups, out int max, out int shared, out int sharedFlagged)
    {
        groups = max = shared = sharedFlagged = 0;
        for (int a = 0; a < n; )
        {
            int b = a;
            while (b < n && key[b] == key[a]) b++;
            int c = b - a;
            groups++;
            if (c > max) max = c;
            if (c > 1)
            {
                shared += c;
                if ((key[a] & flag) != 0) sharedFlagged += c;
            }
            a = b;
        }
    }

    /// <summary>Counting-sort key: lit parts occupy the upper half so one program bind covers each half.</summary>
    /// <summary>The middle radix digit: (lit, no-cull, VDO). #292 put the cull bit UNDER the lit one -- see
    /// <see cref="SortKeyOf"/> -- so that inside a class the two-sided parts of each lighting mode run
    /// contiguously and the cut in <c>BuildBatches</c> flips face culling at most twice, without ever
    /// splitting a program's run in half.</summary>
    private static int SortKey(int i) => SortKeyOf(_oNoCull[i], _oLit[i], _oVao[i]);

    /// <summary>#327: the (feature, primitive) radix digit, with the skin override BuildBatches applies to
    /// the feature. A fresh tenant is unposed at enrollment (its bone slot lands on the first pose after),
    /// so a skinmesh sorts under its content feature until the next enrollment -- one extra cut, briefly, on
    /// the rare part class that is skinned at all.</summary>
    private static int FeatPrimKey(int i)
        => FeatPrimKeyOf(_oBone[i] >= 0 ? (int)ShaderFeature.Skin : (int)_oFeat[i * 4], _oPrim[i]);

    /// <summary>#120: harvest what the part's .mtr declared -- which of the extra texture slots carry a
    /// map, and the four stock PBR parameters by name (plus, #123, the parameters of whichever custom shader
    /// <paramref name="feat"/> named). Deliberately reads the STOCK vocabulary
    /// (<c>Specularity</c>, <c>Roughness</c>, <c>Metallicness</c>, <c>CustomSpecularColor</c>) so content
    /// authored for the engine's own shaders works here unchanged; a name we do not know is left alone for
    /// whoever does. Zero means "not declared" in the engine's own shaders too, so it is what the synthesis
    /// path in the fragment stage keys on.
    /// <para>Cold: one call per enrolled part per enrollment. Material parameters are parsed once when the
    /// .mtr loads and do not animate, so nothing re-reads them per frame.</para></summary>
    /// <summary>Mask bit of map slot <paramref name="s"/>: its index, except for the AO slot -- see
    /// <see cref="AoBit"/> for why that one is out of line.</summary>
    internal static int SlotBit(int s) => s == AoSlot ? AoBit : 1 << s;

    private static void ReadMaterial(IntPtr mat, int i, int feat)
    {
        // Slots 3..5 exist ONLY on a SharedMaterial. Ghidra Material::GetTexture @0x4c5810 has no bounds
        // check: with no shared material it hands back mat+i*0x10 for any i, and past the Material's own
        // three (BindAllStandardTextures @0x4c6e30 stops at i < 3) those are other fields -- +0x40 is the env
        // map, +0x50 the bumpy-shiny map. Asking for slot 4 on a plain Material would bind an env map as a
        // height map. THIS gate is the only thing stopping that now: #121 made env-mapped parts ownable, so
        // +0x40 is reachable on a live material rather than rejected at Record.
        var matRef = new MaterialRef(mat);
        int top = !matRef.Shared.IsNull ? PbrSlots : 2;
        int mask = 0;
        for (int s = 1; s <= top; s++) if (!matRef.Texture(s).IsNull) { mask |= 1 << (s - 1); OwnedTextures.Prefetch(matRef.Texture(s).Ptr); }
        if (!matRef.Texture(0).IsNull) OwnedTextures.Prefetch(matRef.Texture(0).Ptr);   // #386: the diffuse, built before the camera gets here
        // #123/#556: and slot 6 for the features that read it -- the flow map, and the weather ground's
        // snow overlay. A material with nothing bound there leaves the bit clear and its stage samples
        // nothing, which is vanilla's own texture6Bound test.
        if (ShaderFeatures.ReadsAuxSlot(feat) && !matRef.Texture(FlowSlot + 1).IsNull) mask |= 1 << FlowSlot;
        // #146: ...and slot 7 for the AO map, inside the SAME shared-material gate the loop above needs --
        // on a plain Material, GetTexture(7) is an unbounded read of whatever field sits at mat+0x70.
        if (top == PbrSlots && !matRef.Texture(AoSlot + 1).IsNull) mask |= AoBit;
        _oSlots[i] = mask;

        int f = i * 4;
        _oFeat[f] = feat; _oFeat[f + 1] = _oFeat[f + 2] = _oFeat[f + 3] = 0f;

        int o = i * 8;
        _oParam[o] = _oParam[o + 1] = _oParam[o + 2] = 0f;
        _oParam[o + 4] = _oParam[o + 5] = _oParam[o + 6] = _oParam[o + 7] = 0f;
        // TWO lists, shared first. The .mtr's declared values are parsed onto the SHARED material's own list
        // (shared+0x1d0 / count +0x1d8) -- Material::SetMaterialParam @0x4c5d70 copies whole 100-byte records
        // out of exactly that list into the instance one. The instance list at mat+0x118 is only SIZED to the
        // shared count by Material::InitSharedMaterial @0x4c7260, which appends default records rather than
        // the shared values, so reading it alone would hand back floors for the very content this exists for.
        // Reading the instance list second is what lets a per-part override still win where one is set.
        var shared = matRef.Shared;
        if (!shared.IsNull)
            Apply(shared.Params, shared.ParamCount, o, f);
        Apply(matRef.Params, matRef.ParamCount, o, f);
        // fSwaySpeed divides. Zero is what an undeclared parameter reads as, and 1.0 is the value every
        // wind .mtr states ("denormalize the parameters set to 1.0 for this type of foliage"), so the
        // undeclared case IS the authored one -- and without this it is a division by zero, i.e. NaN
        // positions and a plant that vanishes.
        if (_oFeat[f] >= (int)ShaderFeature.WindGrass && _oFeat[f] <= (int)ShaderFeature.WindPlant
            && _oFeat[f + 1] == 0f) _oFeat[f + 1] = 1f;
    }

    /// <summary>Fold one <c>List&lt;MaterialParameter&gt;</c> into the staged PBR pair at
    /// <paramref name="o"/> and the #123 feature vec4 at <paramref name="f"/>, matching the stock names and
    /// this part's feature vocabulary. Later calls override earlier ones.</summary>
    private static void Apply(IntPtr list, int count, int o, int f)
    {
        if (list == IntPtr.Zero || count <= 0 || count > MaxParams) return;
        int feat = (int)_oFeat[f];
        for (int k = 0; k < count; k++)
        {
            byte* rec = (byte*)list + (long)k * ParamStride;
            float* p = (float*)(rec + ParamValue);
            int comps = *(ushort*)(rec + ParamComps);
            // #123: iRoofInterior is the one int-valued parameter any of this reads, and the roof shaders
            // compare it to 1. Taken before the float-only gate below, where it would be skipped. The value
            // sits at the SAME +0x44 the floats do -- BindShaderParameters @0x4c5480 sends the type-1 branch
            // to SetCustomParameterValuei(prog, loc, *(int*)(rec + 0x44)), the type-0 branch to
            // SetCustomParameterValuef with the same address as a float*. Only the read type differs.
            if (*(int*)(rec + ParamType) != 0)
            {
                if (feat == (int)ShaderFeature.RoofKn || feat == (int)ShaderFeature.RoofKnM)
                    if (NameIs(rec, "iRoofInterior") && comps >= 1) _oFeat[f + 3] = *(int*)p;
                continue;                                     // otherwise: 1 = int-valued, none of ours are
            }
            // #123: the feature parameters, by the names the seven community shaders declare. Which three
            // land in the record depends on the feature, so one vec4 covers all of them.
            switch (feat)
            {
                case (int)ShaderFeature.WindGrass:
                case (int)ShaderFeature.WindLeaf:
                case (int)ShaderFeature.WindPlant:
                    if (NameIs(rec, "fSwaySpeed") && comps >= 1) { _oFeat[f + 1] = p[0]; continue; }
                    if (NameIs(rec, "fSwayStrength") && comps >= 1) { _oFeat[f + 2] = p[0]; continue; }
                    if (NameIs(rec, "fTilesetMeshOffsetZ") && comps >= 1) { _oFeat[f + 3] = p[0]; continue; }
                    break;
                case (int)ShaderFeature.RoofKn:
                case (int)ShaderFeature.RoofKnM:
                    if (NameIs(rec, "fRoofOffsetZ") && comps >= 1) { _oFeat[f + 1] = p[0]; continue; }
                    if (NameIs(rec, "fRoofCameraClipZ") && comps >= 1) { _oFeat[f + 2] = p[0]; continue; }
                    break;
                case (int)ShaderFeature.Flowmap:
                    if (NameIs(rec, "fSlideSpeed") && comps >= 1) { _oFeat[f + 1] = p[0]; continue; }
                    break;
            }
            if (NameIs(rec, "Specularity") && comps >= 1) _oParam[o] = p[0];
            else if (NameIs(rec, "Roughness") && comps >= 1) _oParam[o + 1] = p[0];
            else if (NameIs(rec, "Metallicness") && comps >= 1) _oParam[o + 2] = p[0];
            else if (NameIs(rec, "CustomSpecularColor") && comps >= 3)
            {
                _oParam[o + 4] = p[0]; _oParam[o + 5] = p[1]; _oParam[o + 6] = p[2];
                // Black IS the engine's "no custom colour" sentinel (inc_material.shd compares against
                // COLOR_BLACK), so a declared black must not turn every highlight off.
                _oParam[o + 7] = p[0] != 0f || p[1] != 0f || p[2] != 0f ? 1f : 0f;
            }
        }
    }

    /// <summary>Case-insensitive ASCII compare of a MaterialParameter's name field against a literal, over
    /// the 0x40 bytes the engine's own <c>strncasecmp</c> compares. Allocation-free, and cold anyway.
    /// <para>Internal rather than private since #148: <see cref="Particles"/> reads its own <c>.mtr</c>
    /// parameter off the same record layout, and two spellings of "compare a material parameter name" is two
    /// different answers to a case-folding question.</para></summary>
    internal static bool NameIs(byte* rec, string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            byte a = rec[i], b = (byte)name[i];
            if (a >= 'A' && a <= 'Z') a += 32;
            if (b >= 'A' && b <= 'Z') b += 32;
            if (a != b) return false;
        }
        return rec[name.Length] == 0;
    }

    /// <summary>Intern a diffuse-texture pointer into a dense small key for the #84 radix digit (open
    /// addressing on a Fibonacci-hashed pointer; cold, one call per enrolled part). The pointer is only ever
    /// compared, never dereferenced. Beyond <see cref="MaxMats"/> distinct textures everything shares the last
    /// key: that merely stops the sort separating them, and the draw loop's live texture-id compare still cuts
    /// the batch correctly.</summary>
    private static int TexKey(IntPtr tex)
    {
        // Fibonacci hashing takes the TOP bits of the product, so the shift is bound to the table size --
        // a literal here silently clusters every key into the first 1/N of the table when MatHash grows,
        // which is exactly what raising it from 2048 nearly did.
        int h = (int)((ulong)(long)tex * 0x9E3779B97F4A7C15UL >> (64 - MatHashBits)) & (MatHash - 1);
        for (int p = 0; p < MatHash; p++)
        {
            int j = (h + p) & (MatHash - 1);
            if (_mKey[j] == tex) return _mVal[j];
            if (_mKey[j] == IntPtr.Zero)
            {
                if (_matCount >= MaxMats) return MaxMats - 1;
                _mKey[j] = tex;
                return _mVal[j] = _matCount++;
            }
        }
        return MaxMats - 1;
    }

}
