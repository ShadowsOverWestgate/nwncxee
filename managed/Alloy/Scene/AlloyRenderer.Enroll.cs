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
    /// <summary>Revoke one slot: the part reverts to engine drawing from its next iterator sighting.</summary>
    private static void Revoke(int i)
    {
        AlloyCensus.OwnFlag[_oEntry[i]] = 0;
        _oTri[i] = IntPtr.Zero;
        _revokes++;
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
        _ownCount = 0;
        // ...and the draw order with it: an empty table must not leave class ranges pointing into it.
        _orderCount = 0;
        Array.Clear(_clsStart); Array.Clear(_clsEnd);
        _enrollScene = IntPtr.Zero;   // the table is gone; a bucket walk must not match a Scene we no longer hold
        // Re-arm the enrollment report: after a release (module unload, context loss) the NEXT enrollment is
        // a genuinely new fact -- whether ownership came back at all, and over how much. Suppressing it as a
        // repeat is what made a post-unload session look identical to one that never enrolled.
        _reportedHit = false;
        _sinceEnroll = EnrollEvery;
        // ...and the fast retry, not just the next cadence tick. A wipe leaves the part table empty,
        // so the enrollment this queues necessarily misses -- parts have to be re-recorded first -- and
        // without the retry budget the second attempt waits a full EnrollEvery. That is the ~1s of vanilla
        // after every release, and it is the visible half of the fallback. Same re-arm the render-mode edge
        // does, for the same reason.
        _resumeRetries = ResumeRetries;
        _enrollDiagLeft = EnrollDiagBudget;
        _log?.Info($"AlloyRenderer: ownership released ({why}) -- all parts revert to the engine walk");
    }

    /// <summary>Cold, at a frame boundary before the Alloy block: rebuild the ownership table wholesale from
    /// PartTable's capture tables. This IS the frame-atomic swap -- nothing else adds an entry, so no part
    /// can change owner between the Alloy draw and the bucket walk that skips it.
    /// <para>ponytail: rebuilt on a cadence rather than per frame. #236 falsified the half of this note that
    /// said parts qualify rarely -- creature parts re-record constantly, and under the cutover the failure
    /// mode stopped being one-sided (an unowned part is not engine-drawn now, it is nothing). So a new
    /// candidate pulls the next rebuild forward, floored at <see cref="NewCandidateGap"/> frames. The
    /// ceiling that remains: in a creature-heavy area that floor can hold, i.e. this cold path (handle wipe,
    /// full table scan, texture re-intern, VAO retire) at ~6Hz. <c>pulled forward by a new candidate</c> on
    /// the period line is the number that says whether the floor needs widening.</para></summary>
    private static void Enroll(IntPtr scene)
    {
        _sinceEnroll = 0; _newCandidate = false; _enrolls++; _enrollScene = scene;
        WipeHandles();   // #153: new enrollment, new tids -- yesterday's handles answer for nobody
        // #242: and the persistent records with them. The table is about to be rebuilt from zero, so slot N
        // is a different part after this line than before it -- the one invalidation no per-part compare
        // could ever catch, because the comparison is against the previous tenant's record.
        InvalidateRecords();
        _watchEnrolled = 0;
        System.Array.Clear(AlloyCensus.WatchTex);
        for (int i = 0; i < _ownCount; i++)
            if (_oTri[i] != IntPtr.Zero)
            {
                if (AlloyCensus.WatchedEntry(_oEntry[i])) _log?.Info("[dumpframe] ENROLL cleared by the re-enrollment reset");
                AlloyCensus.OwnFlag[_oEntry[i]] = 0;
            }
        _ownCount = 0; _litCount = 0; _litUnownable = 0; _skinUnownable = 0; _gobOwned = 0;
        Array.Clear(_sortCount, 0, MaxVdos * 2);
        Array.Clear(_clsCount); Array.Clear(_clsStart); Array.Clear(_clsEnd);
        _orderCount = 0;
        // the texture-key interning is scoped to ONE enrollment: it is a sort proxy only, so a fresh table
        // every time is both cheaper than invalidating it and immune to the recycled-pointer problem
        Array.Clear(_texSortCount, 0, MaxMats);
        Array.Clear(_mKey, 0, MatHash);
        _matCount = 0;

        // A lit part whose lighting cannot be reproduced (no normal stream, or the lit program failed to
        // build) is NOT enrolled: it goes back to the engine's own vanilla-lit draw, which is the only
        // fallback that holds "Alloy parts lit at least as well as vanilla". Fullbright is not.
        bool canLight = _lighting && !_litFailed;
        var c = default(AlloyCandidate);
        int candidates = 0;
        _capacitySpill = 0;
        AlloyCensus.OwnScanBegin(scene);
        for (int e = 0, n = PartTable.EntryCount; e < n; e++)
        {
            if (!AlloyCensus.TryOwnCandidate(e, ref c)) continue;
            candidates++;
            if (AlloyCensus.WatchAccepted(e, c.Mat)) _watchEnrolled++;
            // #126: a refusal from here on is counted against the class it would have joined -- see the
            // all-owned-or-none sweep below. The surplus stays engine-drawn, never dropped.
            bool watch = AlloyCensus.WatchedEntry(e);
            // #128: the two CAPACITY refusals do not go to the gob. They are not properties of the instance
            // -- the same creature is ownable in an emptier area -- and routing them there turns a full table
            // into a cliff: thousands of gobs marked, the refused set overflows, and the sweep then drops
            // EVERY instance in the scene rather than the surplus. "The surplus stays engine-drawn, never
            // dropped" is the rule, and at capacity a creature split across two renderers (a lighting
            // mismatch) beats every creature in the area reverting.
            if (_ownCount >= MaxOwned)
            { _capacitySpill++; AlloyCensus.OwnRefusedByClass[c.Cls]++; if (watch) _log?.Info("[dumpframe] ENROLL refused: table full"); continue; }
            int v = VdoSlot(c.Vdo);
            if (v < 0)
            { AlloyCensus.OwnRefusedByClass[c.Cls]++; if (watch) _log?.Info("[dumpframe] ENROLL refused: no VAO slot (harvest failed)"); continue; }
            // #137: a true skinmesh (a cloak, a robe, a tail) carries the bone streams and is POSED by them.
            // #128 refused it here for want of a skinning branch; the stages have one now, so the only
            // refusal left is the one #128's reason still holds for -- the kill switch off, or a bone stream
            // on a VDO that never reached PartSkin::Draw (so no pose can be forced for it). Either way it is
            // refused against the GOB, and the sweep below hands the creature wearing it back entire.
            // Refused in BOTH directions, because both mismatches are misrenders. A bone stream with no
            // PartSkin capture cannot be posed (no hook fired for it, so no trampoline to force); a PartSkin
            // capture whose VAO harvest came back boneless would enrol as an ordinary rigid part, skip
            // PoseSkinned, and draw in its bind pose -- which is the failure the asymmetric gate would have
            // let through as the QUIET one.
            if (_vSkin[v] != (_skinning && PartTable.Skinned(e)))
            { _skinUnownable++; AlloyCensus.OwnRefuse(c.Cls, c.Gob); if (watch) _log?.Info($"[dumpframe] ENROLL refused: skin mismatch (armed={_skinning} boneStreams={_vSkin[v]} skinPart={PartTable.Skinned(e)})"); continue; }
            bool lit = c.Lit && _lighting;
            if (lit && (!canLight || !_vLit[v]))
            { _litUnownable++; AlloyCensus.OwnRefuse(c.Cls, c.Gob); if (watch) _log?.Info($"[dumpframe] ENROLL refused: lit but unshadeable (canLight={canLight} vdoLit={_vLit[v]})"); continue; }
            // #121: an env-mapped part the engine draws UNLIT would go through the fullbright variant, which
            // has no env handling at all -- the engine still routes it to RenderEnvironmentMapped (the switch
            // in PartTriMesh::Draw is on HasEnvMap, not on the lit flag), so owning it would silently drop
            // whatever shine it had. Only the lit variant can reproduce it; the rest stay vanilla.
            if (c.Env && !lit)
            { _litUnownable++; AlloyCensus.OwnRefuse(c.Cls, c.Gob); if (watch) _log?.Info("[dumpframe] ENROLL refused: env-mapped but unlit"); continue; }
            int i = _ownCount++;
            _oEntry[i] = e; _oTri[i] = c.Tri; _oNode[i] = c.Node; _oMat[i] = c.Mat; _oVdo[i] = c.Vdo;
            _oItem[i] = c.Item; _oPrim[i] = c.Prim; _oVao[i] = v; _oMx[i] = c.Mx; _oLit[i] = lit;
            _oRotBits[i] = c.RotBits;
            _oTexKey[i] = TexKey(c.Tex0);
            _oCls[i] = (byte)c.Cls; _oSrc[i] = (byte)c.Src; _oDst[i] = (byte)c.Dst;
            _oBucket[i] = (byte)c.Bucket; _oGob[i] = c.Gob;
            _oBone[i] = -1;
            if (c.Gob != IntPtr.Zero) _gobOwned++;
            // #126: the local bbox centre, for the blended pass's back-to-front key.
            var tri = new PartRef(c.Tri);
            float* lo = tri.BboxMin, hi = tri.BboxMax;
            _oCx[i] = (lo[0] + hi[0]) * 0.5f; _oCy[i] = (lo[1] + hi[1]) * 0.5f; _oCz[i] = (lo[2] + hi[2]) * 0.5f;
            float ex = hi[0] - lo[0], ey = hi[1] - lo[1], ez = hi[2] - lo[2];
            _oR[i] = 0.5f * MathF.Sqrt(ex * ex + ey * ey + ez * ez);
            ReadMaterial(c.Mat, i, c.Feat);
            // #121: bit 5 of the slot mask, and the one slot with no texture behind it -- the probe replaces
            // what the material's env map held, so all that is carried is the flag itself.
            if (c.Env) _oSlots[i] |= EnvSlot;
            float rad = BitConverter.Int32BitsToSingle(c.RotBits) * DegToRad;
            _oCos[i] = MathF.Cos(rad); _oSin[i] = MathF.Sin(rad);
            AlloyCensus.OwnFlag[e] = 1;
            if (watch) _log?.Info($"[dumpframe] ENROLL accepted slot={i} cls={(AlloyClass)c.Cls} lit={lit} vao={v}");
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
                AlloyCensus.OwnFlag[_oEntry[i]] = 0; _oTri[i] = IntPtr.Zero; _gobParts++;
            }
        _blendedDropped = 0;
        if (AlloyCensus.OwnRefusedByClass[(int)AlloyClass.Blended] > 0)
            for (int i = 0; i < _ownCount; i++)
                if (_oTri[i] != IntPtr.Zero && _oCls[i] == (byte)AlloyClass.Blended)
                {
                    if (AlloyCensus.WatchedEntry(_oEntry[i])) _log?.Info("[dumpframe] ENROLL revoked: blended class dropped whole");
                    AlloyCensus.OwnFlag[_oEntry[i]] = 0; _oTri[i] = IntPtr.Zero; _blendedDropped++;
                }
        // The radix digits are counted AFTER the sweep, so a dropped class costs no order slot.
        for (int i = 0; i < _ownCount; i++)
        {
            if (_oTri[i] == IntPtr.Zero) continue;
            _sortCount[SortKey(i)]++;
            _texSortCount[_oTexKey[i]]++;
            _clsCount[_oCls[i]]++;
            if (_oLit[i]) _litCount++;
        }
        // Three-digit LSD radix into _order, every digit counting-sorted (stable): texture, then (lit, VDO),
        // then #126's class. The result walks grouped by CLASS -- so each pass owns a contiguous batch range
        // -- and inside a class by (lit, VDO) with equal textures contiguous, which is the #84 batch key. The
        // draw loop then cuts a batch on a key change instead of searching for one.
        int at = 0;
        for (int k = 0; k < MaxMats; k++) { int n = _texSortCount[k]; _texSortCount[k] = at; at += n; }
        for (int i = 0; i < _ownCount; i++) if (_oTri[i] != IntPtr.Zero) _orderTmp[_texSortCount[_oTexKey[i]]++] = i;
        int live = at;
        at = 0;
        for (int k = 0; k < MaxVdos * 2; k++) { int n = _sortCount[k]; _sortCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _orderTmp[k]; _order[_sortCount[SortKey(i)]++] = i; }
        at = 0;
        for (int k = 0; k < AlloyClasses.Count; k++) { int n = _clsCount[k]; _clsStart[k] = at; _clsCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _order[k]; _orderTmp[_clsCount[_oCls[i]]++] = i; }
        // ...and the cursor each class's fill left behind IS its end index.
        for (int k = 0; k < AlloyClasses.Count; k++) _clsEnd[k] = _clsCount[k];
        Array.Copy(_orderTmp, _order, live);
        _orderCount = live;

        // #128: an instance that just changed hands has to change hands on the SHADOW side too, and the two
        // enrollments run on independent counters -- see CasterPass.ForceEnroll for the frames of darkness
        // that costs. Poked whenever any instanced part is owned, which is the only case that can open it.
        if (_gobOwned > 0) CasterPass.ForceEnroll();
        _lastEnrolled = _orderCount; _lastCandidates = candidates; _lastTextures = _matCount;
        MeshCensus();
        // Empty right after a resume means the re-record has not finished, not that nothing is ownable:
        // re-arm the cadence so the next pass tries again instead of waiting out the full countdown.
        // ...on the DRAWABLE count: the all-owned-or-none sweep can empty the order while leaving slots
        // enrolled, and an enrollment that drew nothing is exactly the case this retry exists for.
        if (_orderCount == 0 && _resumeRetries > 0) { _resumeRetries--; _sinceEnroll = EnrollEvery; }
        else _resumeRetries = 0;
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
                       $"({AlloyCensus.OwnScanDiag()})");
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
            if (_vVdo[v] == vdo) return v;
            if (_vVdo[v] == IntPtr.Zero && s < 0) s = v;      // retired slot: reuse it, VAO rebuilt on use
        }
        if (s < 0)
        {
            if (_vdoCount >= MaxVdos) return -1;
            s = _vdoCount++;
            _vVao46[s] = 0;
        }
        if (!Harvest(vdo, s)) return -1;
        _vVdo[s] = vdo; _vStale[s] = _vVao46[s] != 0;
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
    /// <para>Cold by construction -- enrollment cadence, static scratch arrays, sorted in place.</para>
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
            _msCount[n] = count; _msSlot[n] = i;
            _msKey[n] = ((long)(uint)_oVao[i] << MsVaoShift) | (_oMx[i] ? MsMxBit : 0L)
                        | (first << MsFirstShift) | clamped;
            if (_oMx[i]) _msMxParts++;
            _msShape[n] = (gob ? MsGobBit : 0L) | (_oMx[i] ? MsShapeMxBit : 0L)
                          | ((long)(uint)_oPrim[i] << MsPrimShift)
                          | ((long)(uint)_oTexKey[i] << MsTexShift) | clamped;
            // ...and the vertex range the content hash reads, when it is armed. The vert item table is
            // (base, count) as two int32s -- VertexDataObject::AddItem @0x45ca94 computes the next free
            // vertex as item[last].base + item[last].count over this same table, which is what pins the
            // second field. Clamping to that count is what stops a short mesh's hash running on into its
            // NEIGHBOUR's vertices and reading two identical meshes as different.
            long vert = vdo.VertItems;
            _msVao[n] = _oVao[i]; _msBase[n] = 0; _msVerts[n] = 0;
            if (vert != 0 && item < vdo.VertCount)
            { _msBase[n] = *(int*)(vert + item * 8); _msVerts[n] = *(int*)(vert + item * 8 + 4); }
            n++;
        }
        _msParts = n;
        // The content pass, if armed: it maps each VDO's position buffer once and hashes every part on it,
        // so it needs the whole staged population before it can run -- and it is GL, so it lives outside
        // Scene/. The hash then joins the shape key, which is why the key is completed here and not above.
        bool hashed = HashVertices(n);
        for (int k = 0; k < n; k++)
        {
            // The rigid key drops the VDO and the texture: a model repeated across two buffers, or retextured,
            // is still the same geometry. What it keeps is the index count and matrix-class -- the first
            // because two shapes agreeing on a distance multiset and a triangle count are the same shape, the
            // second because it splits "could share vertices today" from "would have to be un-baked first".
            _msRigidKey[k] = (_msShape[k] & (MsShapeMxBit | ((1L << MsTexShift) - 1)))
                             | ((long)(hashed ? _msRigid[k] : 0u) << MsHashShift);
            if (hashed) _msShape[k] |= (long)(_msHash[k] & ((1u << MsHashBits) - 1)) << MsHashShift;
        }
        Array.Sort(_msKey, 0, n);
        Array.Sort(_msShape, 0, n);
        for (int k = 0; k < n; k++) _msIdx[k] = k;
        Array.Sort(_msRigidKey, _msIdx, 0, n);
        ScanRuns(_msRigidKey, n, MsShapeMxBit,
                 out _msRigidShapes, out _msRigidMax, out _msRigidRepeat, out _msRigidRepeatMx);
        // #244 phase A: with the rigid grouping in hand, derive a placement for every repeated part and check
        // it reproduces the copy exactly. This is the de-duplication's own arithmetic, run and audited before
        // anything is stored or drawn from it -- a nearly-right transform is the one outcome that must never
        // reach the draw path.
        if (hashed) VerifyDedupPass(n);
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
    private static int SortKey(int i) => _oLit[i] ? MaxVdos + _oVao[i] : _oVao[i];

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
        for (int s = 1; s <= top; s++) if (!matRef.Texture(s).IsNull) mask |= 1 << (s - 1);
        // #123: and slot 6 for the one feature that reads it. A flowmap material with no flow map bound
        // leaves the bit clear, and the fragment stage then slides nothing -- vanilla's texture6Bound test.
        if (feat == (int)ShaderFeature.Flowmap && !matRef.Texture(FlowSlot + 1).IsNull) mask |= 1 << FlowSlot;
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
