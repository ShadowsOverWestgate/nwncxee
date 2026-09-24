using System;
using System.Numerics;
using CX.Framework;
using static CX.Framework.EngineLayout;

namespace CX.Alloy;

/// <summary>The engine chase, BOUNDED (#330): the per-frame re-read of every owned part's borrowed engine
/// state -- node matrix, keyhole verdict, tile fade -- is limited to the parts that CAN have changed. The
/// binary has no per-change setter for any of those (research on the ticket), but it does keep the sets, and
/// they are all view-sized:
/// <list type="bullet">
/// <item><c>gobbucket</c>: the only route to a gob-tree part's VertexPrimitive matrix is
/// <c>AddPartsTreeToDrawBuckets</c> over this list, so a part whose gob is not in it holds last frame's
/// matrix bit for bit. A BSP-filed part is written by the mesh-bucket loop instead (gated on its node flags
/// or a gob) and stays chased.</item>
/// <item>the keyhole camera words: <c>Camera::GetCanKeyholeDissolve</c> is pure in them plus spawn-time
/// part fields, so an unchanged snapshot means every verdict is last frame's.</item>
/// <item>the two fade buckets, which <c>ClearBuckets</c> @0x47f4e0 empties every frame and
/// <c>ProcessTriMeshParts</c> refills with the tiles it checked: a tile's live factor (+0x70) moves only at
/// the top of ManageSceneBSP, from the +0x74 the handlers stepped, and only for a checked tile. So a bucket
/// tile with +0x74 != +0x70 at the anchor is the whole set whose factor moves next frame.</item>
/// </list>
/// <para>Every cache here is served only when the sample was taken on the immediately previous frame too --
/// a frame Alloy did not build (a gate closed, a vanilla frame, a pass that skipped its records) is a frame
/// the engine may have moved things unobserved, and the resume chases everything once.</para>
/// <para><c>/alloy poll 1</c> is the A/B and the soundness instrument: every cache is bypassed AND audited --
/// the live value is compared against what the cache would have served, and a disagreement counts. The
/// three <c>POLL AUDIT</c> counters on the [alloy] line must read 0.</para></summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Visited-gob set capacity. The engine's in-view gob list is low thousands; past the load cap
    /// the set overflows and the frame chases everything, which is correct and merely the old cost.</summary>
    private const int VisBits = 14, VisMask = (1 << VisBits) - 1, VisLoadCap = (1 << VisBits) / 2;
    private static readonly IntPtr[] _visGob = new IntPtr[1 << VisBits];
    /// <summary>The sample each slot was filled on; a stale stamp is an empty slot, so no clear per frame.</summary>
    private static readonly int[] _visStamp = new int[1 << VisBits];
    /// <summary>Per visited entry: did the engine WRITE this gob's parts a world matrix this frame -- in
    /// <c>gobbucket</c> and past the two field tests <c>Scene::AddPartsToDrawBuckets</c> @0x4751d0 makes
    /// before it walks the tree. Carried beside the entry so the placement gate costs one probe and no
    /// dereference of a gob the route walk has not proved live.</summary>
    private static readonly bool[] _visPlaced = new bool[1 << VisBits];
    /// <summary>The visited set faithfully holds this frame's <c>gobbucket</c> -- the list read sane and the
    /// set did not overflow. False stands the placement gate down: an unreadable list may not hide the world.</summary>
    private static bool _visSetOk;
    /// <summary>The sample counter (one per anchor) and this sample's gob count.</summary>
    private static int _visSeq, _visCount;
    /// <summary>Chase every matrix this frame: the set overflowed, the symbol never resolved, or the previous
    /// frame went unsampled. The poll dial does NOT set it -- the audit needs the set's real answer.</summary>
    private static bool _visAll = true;
    /// <summary>The frame of the last sample; continuity is this being the previous frame.</summary>
    private static int _lastSampleFrame = int.MinValue;
    /// <summary>The three engine lists, resolved once; null stands the matching cache down.</summary>
    private static byte* _gobBucket, _fadeOutBucket, _fadeInBucket;
    /// <summary>A <c>CExoArrayList</c>'s items pointer and count, as the buckets lay them out.</summary>
    private const int BucketItems = 0, BucketCount = 8, BucketBytes = BucketCount + sizeof(int);

    /// <summary>The keyhole camera key (<see cref="PartTable.KeyholeCameraMoved"/>) and the generation it
    /// last changed on. A slot's verdict is current while <see cref="_disGen"/> matches.</summary>
    private static readonly ulong[] _camKey = new ulong[PartTable.CamKeyholeWords + 1];
    private static int _camGen = 1;
    private static readonly int[] _disGen = new int[MaxOwned];
    private static readonly bool[] _disVerdict = new bool[MaxOwned];

    /// <summary>The part's TileData (part+0x98, spawn-time), so the fade question is answered from the
    /// moving-tile set without dereferencing the part. #546: a tile GOB part (window, vine) carries no
    /// part+0x98; its gob's <see cref="EngineLayout.GobTileData"/> stands in, which is the owner
    /// <c>Gob::ProxyPartDraw</c> @0x4520c0 fogs it by.</summary>
    private static readonly IntPtr[] _oTile = new IntPtr[MaxOwned];
    /// <summary>The part was filed in the BSP (mesh-bucket loop territory) rather than reached through a gob
    /// tree. Its matrix rewrite does not depend on <c>gobbucket</c>, so it is chased every frame.</summary>
    private static readonly bool[] _oBsp = new bool[MaxOwned];
    /// <summary>Fault 1 (<c>docs/research/alloy-visibility-structural.md</c>): the engine has placed this
    /// slot's gob at least once since the slot was tenanted. <c>VertexPrimitive::VertexPrimitive</c>
    /// @0x45a9f0 leaves vp+0x18 -- the matrix Alloy reads -- at IDENTITY, and the only writer of a gob
    /// part's is <c>AddPartsTreeToDrawBuckets</c> @0x460880 over <c>gobbucket</c>. So a gob the engine has
    /// never composed a matrix for (a far city-window tile gob, a placeable added before its placement
    /// lands) would draw at world (0,0,0), the outer corner of tile (0,0). A LATCH and not a per-frame
    /// gate: once placed, an off-camera part keeps drawing and casting from the matrix it holds, which is
    /// the whole point of the bounded chase. Cleared on every re-tenanting, so a recycled gob address
    /// cannot inherit the previous tenant's answer.</summary>
    private static readonly bool[] _oPlaced = new bool[MaxOwned];
    /// <summary>Moving-tile set: tiles whose factor moves next frame, stamped with the sample they were seen
    /// on; a tile is moving if seen this sample or the last (the commit lands the frame after the step).</summary>
    private const int MovBits = 10, MovMask = (1 << MovBits) - 1;
    private static readonly IntPtr[] _movTile = new IntPtr[1 << MovBits];
    private static readonly int[] _movStamp = new int[1 << MovBits];
    private static int _movCount, _movCountPrev;
    /// <summary>Every tile factor may move: the set overflowed, a bucket never resolved, or the frame before
    /// went unsampled.</summary>
    private static bool _fadeAll = true;
    /// <summary>The last cached factor per slot, and whether one was ever read for this tenant.</summary>
    private static readonly float[] _oFadeF = new float[MaxOwned];
    private static readonly bool[] _fadeKnown = new bool[MaxOwned];
    /// <summary>Bytes of the TileData stub the self-check hands the fade walk: enough to hold
    /// <see cref="EngineLayout.TilePendingFade"/>.</summary>
    private static readonly int TileStubBytes = TilePendingFade + sizeof(float);
    /// <summary>The first pending tile's live and pending factors this sample, for the period line -- a tile
    /// that pends forever is the one number that explains a fade cache that never serves.</summary>
    private static float _movLive, _movPending;

    /// <summary>This pass's six frustum planes (<see cref="CullPass.Planes"/>), and whether the pass had a
    /// perspective projection to take them from. Without one every slot routes.</summary>
    private static readonly float[] _passPlanes = new float[24];
    private static bool _passFrustumOk;
    /// <summary>World units added to a slot's slack-scaled sphere before the plane test: one frame of camera
    /// motion, so a part entering the view is routed the frame it appears rather than the frame after.</summary>
    private const float RouteSlackUnits = 3f;

    /// <summary><c>/alloy poll 1</c>: bypass every chase cache and audit it.</summary>
    private static bool _fullPoll => Renderer.Poll.V;
    /// <summary>Period counters: per term, reads made vs reads the cache absorbed; the audit's misses; the
    /// BSP-filed gob parts the visited set cannot bound.</summary>
    private static long _mxChased, _mxCached, _disAsked, _disCached, _fadeRead, _fadeServed;
    private static long _pollMissMx, _pollMissDis, _pollMissFade;
    private static long _routeIn, _routeOut, _disBoxed, _pollMissBox, _pollMissAlpha;
    /// <summary>Why a part was refused the dissolve, per frame. The three gates ahead of the aperture test,
    /// counted apart because "the keyhole stops at that window" is always one of them and the census is the
    /// only thing that says which.</summary>
    private static long _disNoLit, _disNoFeat, _disGobUnpierced;

    /// <summary>Whether a TILE gob's parts may dissolve at all. <c>/alloy tiledissolve 0</c> is the vanilla A/B:
    /// off is the engine's own rule, where the aperture stops at every gob. See <see cref="Dissolves"/>.</summary>
    internal static bool GobKeyhole => Renderer.TileDissolve.V;
    private static int _visGobsLast;
    /// <summary>#336: the part's world box at enrolment (six floats), for the dissolve verdict.</summary>
    private static readonly float[] _oBox = new float[MaxOwned * 6];
    /// <summary>#336: the camera's keyhole state, read once per pass at the top of the route walk: the on
    /// byte, the aperture plane, the height cut; <c>_khCam</c> false = no camera or no engine verdict armed,
    /// and the engine call stands in.</summary>
    private static readonly float[] _khPlane = new float[4];
    private static byte _khOn;
    private static float _khCut;
    private static bool _khCam;

    /// <summary>Off-device: the box verdict against the decompiled rule. A plane z = 5 facing up (0,0,1,-5):
    /// a box below it dissolves (its lowest corner is under the plane), a box above it does not, the height
    /// cut refuses a box whose top is under it, and keyhole-off refuses everything. A verdict that picks the
    /// wrong corner passes the roof through the aperture or dissolves the floor.</summary>
    private static bool KeyholeBoxSelfCheck(ExtenderLog log)
    {
        float* pl = stackalloc float[4] { 0f, 0f, 1f, -5f };
        float* below = stackalloc float[6] { -1f, -1f, 0f, 1f, 1f, 2f };
        float* above = stackalloc float[6] { -1f, -1f, 6f, 1f, 1f, 8f };
        float* straddle = stackalloc float[6] { -1f, -1f, 4f, 1f, 1f, 6f };
        bool ok = PartTable.KeyholeDissolvesBox(below, 1, pl, -10f)
                  && !PartTable.KeyholeDissolvesBox(above, 1, pl, -10f)
                  && PartTable.KeyholeDissolvesBox(straddle, 1, pl, -10f)
                  && !PartTable.KeyholeDissolvesBox(below, 1, pl, 3f)      // top at 2, cut at 3: refused
                  && !PartTable.KeyholeDissolvesBox(below, 0, pl, -10f);
        if (!ok) log.Info("AlloyRenderer.KeyholeBoxSelfCheck: FAILED -- the cached-box dissolve verdict disagrees with the decompiled rule");
        return ok;
    }

    private static void ReadKeyholeCamera()
    {
        IntPtr cam = PartTable.KeyholeArmed ? PartTable.CurrentCamera : IntPtr.Zero;
        _khCam = cam != IntPtr.Zero;
        if (!_khCam) return;
        _khOn = *(byte*)(cam + EngineLayout.CamKeyholeState);
        for (int k = 0; k < 4; k++) _khPlane[k] = *(float*)(cam + EngineLayout.CamKeyholeState + 4 + k * 4);
        _khCut = *(float*)(cam + EngineLayout.CamKeyholeState + 20);
    }

    /// <summary>#336 step 4: the VISIBLE SET -- per class, in enrollment order, the slots the route walk
    /// visits: every gob part (its gob may be visited any frame), every slot without a record for its
    /// tenant (it must build one to cast at all), and every gobless part whose sphere is inside this pass's
    /// planes. Rebuilt from the whole order every <see cref="VisEvery"/> frames, on an enrollment, an
    /// epoch bump, or a camera that turned or moved past the slack; in between the walk skips the
    /// off-screen world outright. The Route sample measured that walk at 0,4-0,5ms in the #324 area.</summary>
    private static readonly int[] _vis = new int[MaxOwned];
    private static readonly int[] _visStart = new int[AlloyClasses.Count], _visEnd = new int[AlloyClasses.Count];
    private static readonly float[] _visPlanes = new float[24];
    private static int _visBuiltAt = int.MinValue, _visEnrollGen = -1, _visEpoch = -1;
    private static bool _visFull = true;
    private static bool _visList => Renderer.VisList.V;
    private static long _visRebuilds;
    private const int VisEvery = 4;
    /// <summary>Plane drift that forces a rebuild: a normal component (about 0,6 degrees of turn) and a
    /// distance term in world units, both well inside <see cref="RouteSlackUnits"/> over VisEvery frames.</summary>
    private const float VisNormalEps = 0.01f, VisDistEps = 0.5f;

    /// <summary>Before the route walk: decide whether this pass walks the whole order or the visible set,
    /// and rebuild the set if it is due.</summary>
    private static void PrepareVisible()
    {
        ReadKeyholeCamera();
        _visFull = !_visList || _fullPoll || !_passFrustumOk || _visAll;
        if (_visFull) return;
        bool due = _frameSeq - _visBuiltAt >= VisEvery || _visEnrollGen != _enrollGen || _visEpoch != _recEpoch;
        if (!due)
            for (int f = 0; f < 6 && !due; f++)
            {
                int o = f * 4;
                due = MathF.Abs(_passPlanes[o] - _visPlanes[o]) > VisNormalEps
                      || MathF.Abs(_passPlanes[o + 1] - _visPlanes[o + 1]) > VisNormalEps
                      || MathF.Abs(_passPlanes[o + 2] - _visPlanes[o + 2]) > VisNormalEps
                      || MathF.Abs(_passPlanes[o + 3] - _visPlanes[o + 3]) > VisDistEps;
            }
        if (!due) return;
        _visRebuilds++; FrameZones.Count(FrameZones.Ev.VisRebuild);
        _visBuiltAt = _frameSeq; _visEnrollGen = _enrollGen; _visEpoch = _recEpoch;
        Array.Copy(_passPlanes, _visPlanes, 24);
        int n = 0;
        fixed (float* sph = _dSph)
            for (int c = 0; c < AlloyClasses.Count; c++)
            {
                _visStart[c] = n;
                for (int k = _clsStart[c]; k < _clsEnd[c]; k++)
                {
                    int i = _order[k];
                    if (_oTri[i] == IntPtr.Zero) continue;
                    if (_oGob[i] != IntPtr.Zero || _recGen[i] != _recEpoch || _recEnroll[i] != _oEnrollGen[i]
                        || SphereInPass(i, sph))
                        _vis[n++] = i;
                }
                _visEnd[c] = n;
            }
    }

    /// <summary>The list this pass routes class <paramref name="c"/> from, and its range.</summary>
    private static int[] RouteRange(int c, out int from, out int to)
    {
        if (_visFull) { from = _clsStart[c]; to = _clsEnd[c]; return _order; }
        from = _visStart[c]; to = _visEnd[c]; return _vis;
    }

    /// <summary>Is the slot's cull sphere inside this pass's six planes, with the route slack?</summary>
    private static bool SphereInPass(int i, float* sph)
    {
        float* s = sph + (long)i * 4;
        if (s[3] >= NeverCull) return true;
        float r = s[3] * ShadowMath.FrustumCullSlack + RouteSlackUnits;
        fixed (float* p = _passPlanes)
            for (int f = 0; f < 6; f++)
            {
                float* pl = p + f * 4;
                if (pl[0] * s[0] + pl[1] * s[1] + pl[2] * s[2] + pl[3] < -r) return false;
            }
        return true;
    }

    private static void SetPassFrustum(float* vp, bool perspective)
    {
        _passFrustumOk = perspective;
        if (perspective) fixed (float* p = _passPlanes) CullPass.Planes(vp, p, _eyePos);
    }

    /// <summary>#330: should this pass touch the slot at all? Everything the engine can do to a part
    /// reaches Alloy through the record write, and the record only needs rewriting when (a) there is none
    /// to serve (stale epoch, new tenant), (b) the engine visited the gob this frame, or (c) the part is in
    /// this pass's view, where its identity is re-verified and its alpha, fade and dissolve re-read. A slot
    /// failing all three keeps its record, its spheres and its shadow levels exactly as they stand -- the
    /// engine rewrites nothing on a part its own camera cull dropped -- and is stamped verified so the
    /// shadow side does not read its last build as a fresh move. The poll dial routes everything.</summary>
    private static bool RouteWanted(int i, float* sph)
    {
        if (_fullPoll || !_passFrustumOk || _visAll) return true;
        if (_recGen[i] != _recEpoch || _recEnroll[i] != _oEnrollGen[i] || _recTexStale[i]) return true;
        if (_oGob[i] != IntPtr.Zero && VisSlot(_oGob[i], false)) return true;
        // #336: no stamp on the rejected slot -- the shadow side reads "moved on its LAST build, and that
        // build was last frame" (SlotRecMoved), so an unvisited slot reads still on its own.
        if (!SphereInPass(i, sph)) { _routeOut++; return false; }
        _routeIn++;
        return true;
    }

    /// <summary>The engine's visible-gob list for this frame (<c>gobbucket</c>, filled by ManageSceneBSP's
    /// view-volume filter), when the anchor sample found it sane. False = walk the whole scene instead.</summary>
    internal static bool VisibleGobs(out IntPtr* items, out int n)
    {
        items = null; n = 0;
        if (_gobBucket == null || _visAll) return false;
        n = *(int*)(_gobBucket + BucketCount);
        items = *(IntPtr**)(_gobBucket + BucketItems);
        return n >= 0 && n <= VisLoadCap && (n == 0 || items != null);
    }

    private static void ResolveChase(ExtenderLog log)
    {
        _gobBucket = (byte*)Native.Sym(SymbolMap.GobBucket);
        _fadeOutBucket = (byte*)Native.Sym(SymbolMap.FadingOutBucket);
        _fadeInBucket = (byte*)Native.Sym(SymbolMap.FadingInBucket);
        if (_gobBucket == null || _fadeOutBucket == null || _fadeInBucket == null)
            log.Info("AlloyRenderer: #330 engine list unresolved (gobbucket " + (_gobBucket == null ? "no" : "ok")
                     + ", fadingoutbucket " + (_fadeOutBucket == null ? "no" : "ok") + ", fadinginbucket "
                     + (_fadeInBucket == null ? "no" : "ok") + ") -- that chase stays unbounded");
    }

    /// <summary>Once per frame at the anchor, engine context: take the three samples above. Allocates
    /// nothing; the gob walk is one store per visible gob, the fade walk two loads per checked tile.</summary>
    private static void SampleEngineSets()
    {
        bool continuous = _lastSampleFrame == _frameSeq - 1;
        _lastSampleFrame = _frameSeq;
        _visSeq++;
        _visCount = 0;
        // The set is filled whenever the list reads sane, continuous frame or not: the placement latch
        // needs this frame's membership even on a resume frame, where the chase caches serve nothing.
        _visSetOk = SampleVisibleGobs(_gobBucket);
        _visAll = !continuous || !_visSetOk;
        _visGobsLast = _visCount;
        fixed (ulong* key = _camKey)
            if (PartTable.KeyholeCameraMoved(PartTable.CurrentCamera, key)) _camGen++;
        _movCountPrev = _movCount;
        _movCount = 0;
        _fadeAll = !continuous || !MarkPendingTiles(_fadeOutBucket) || !MarkPendingTiles(_fadeInBucket);
    }

    /// <summary>Stamp every gob in <paramref name="bucket"/> into the visited set, each carrying the engine's
    /// own placement verdict. False = the list read insane, an entry was null, or the set overflowed, and the
    /// set then describes nothing this frame. Allocates nothing; two loads per visible gob on top of the
    /// store the set already cost, off gobs the engine dereferenced moments earlier in the same walk.</summary>
    private static bool SampleVisibleGobs(byte* bucket)
    {
        if (bucket == null) return false;
        int n = *(int*)(bucket + BucketCount);
        IntPtr* items = *(IntPtr**)(bucket + BucketItems);
        // an EMPTY CExoArrayList keeps a null items pointer: nothing in view, not an unreadable list
        if (n < 0 || n > VisLoadCap || (n > 0 && items == null)) return false;
        for (int k = 0; k < n; k++)
        {
            IntPtr gob = items[k];
            int h = VisIndex(gob, true);
            if (h < 0) return false;
            // the two tests Scene::AddPartsToDrawBuckets @0x4751d0 makes on a gobbucket entry before it
            // walks the tree: everything it skips keeps the identity matrix the VertexPrimitive ctor left
            _visPlaced[h] = *(byte*)(gob + EngineLayout.GobVisible) != 0
                            && *(float*)(gob + EngineLayout.GobAlpha) > 0f;
        }
        return true;
    }

    /// <summary>The visited set's entry for <paramref name="gob"/>, or -1 when it is absent (and
    /// <paramref name="insert"/> is clear), null, or the table is full.</summary>
    private static int VisIndex(IntPtr gob, bool insert)
    {
        if (gob == IntPtr.Zero) return -1;
        int h = (int)((ulong)gob >> 4) & VisMask;
        for (int probe = 0; probe <= VisMask; probe++, h = (h + 1) & VisMask)
        {
            if (_visStamp[h] != _visSeq)
            {
                if (!insert) return -1;
                _visStamp[h] = _visSeq; _visGob[h] = gob; _visPlaced[h] = false; _visCount++;
                return h;
            }
            if (_visGob[h] == gob) return h;
        }
        return -1;
    }

    /// <summary>Probe the visited set for <paramref name="gob"/>; with <paramref name="insert"/> an absent gob
    /// is added. Returns membership after the call, or false on a full table with insert set.</summary>
    private static bool VisSlot(IntPtr gob, bool insert) => VisIndex(gob, insert) >= 0;

    /// <summary>Was this gob in the engine's in-view list this frame -- i.e. may its parts' matrices have
    /// been rewritten? True for everything on an overflow or resume frame.</summary>
    private static bool GobVisited(IntPtr gob) => _visAll || VisSlot(gob, false);

    /// <summary>Fault 1: may this slot be drawn at all -- has the engine ever written the matrix Alloy reads
    /// off it? A gob-tree part whose gob has not once reached <see cref="_visPlaced"/> since the slot was
    /// tenanted holds the identity vp+0x18 its VertexPrimitive was constructed with, and drawing it puts the
    /// asset at the area's (0,0) corner. Sets the latch on the first sighting, so the answer costs one probe
    /// until then and nothing after.
    /// <para>Not gated: a part with no gob and a BSP-filed one, whose matrices the mesh-bucket loop writes in
    /// world space; and every part on a frame whose <c>gobbucket</c> read did not stand up, since an
    /// instrument that cannot see may not hide the world.</para>
    /// <para>ponytail: per GOB, where the engine decides per PART -- <c>AddPartsTreeToDrawBuckets</c> also
    /// returns on <c>part+0x34 == 0</c>, so a subtree under a switched-off part is placed by this gate and
    /// was not by the engine. Upgrade path is a per-part read of +0x34 at route, which costs a dereference
    /// per slot per frame; the gob answer covers the whole reported symptom (#2).</para></summary>
    private static bool PartPlaced(int i)
    {
        if (_oPlaced[i]) return true;
        IntPtr gob = _oGob[i];
        if (gob == IntPtr.Zero || _oBsp[i] || !_visSetOk) return true;
        int h = VisIndex(gob, false);
        if (h < 0 || !_visPlaced[h]) return false;
        _oPlaced[i] = true;
        return true;
    }

    /// <summary>Stamp every tile in this bucket whose pending factor differs from its live one into the
    /// moving set. False = the bucket is unresolved or the set overflowed, so every factor may move.</summary>
    private static bool MarkPendingTiles(byte* bucket)
    {
        if (bucket == null) return false;
        int n = *(int*)(bucket + BucketCount);
        IntPtr* items = *(IntPtr**)(bucket + BucketItems);
        if (n <= 0) return n == 0;   // empty list = null items pointer, and no tile to mark
        if (items == null) return false;
        for (int k = 0; k < n; k++)
        {
            IntPtr tile = items[k];
            if (tile == IntPtr.Zero) continue;
            float live = *(float*)(tile + EngineLayout.TileFade), pending = *(float*)(tile + TilePendingFade);
            if (live == pending) continue;
            if (_movCount == 0) { _movLive = live; _movPending = pending; }
            if (!MovSlot(tile, true)) return false;
        }
        return true;
    }

    /// <summary>The moving-tile set's probe; same shape as <see cref="VisSlot"/> over a stamp that accepts
    /// this sample and the last.</summary>
    private static bool MovSlot(IntPtr tile, bool insert)
    {
        int h = (int)((ulong)tile >> 4) & MovMask;
        for (int probe = 0; probe <= MovMask; probe++, h = (h + 1) & MovMask)
        {
            int st = _movStamp[h];
            if (st != _visSeq && st != _visSeq - 1)
            {
                if (!insert) return false;
                _movStamp[h] = _visSeq; _movTile[h] = tile; _movCount++;
                return true;
            }
            if (_movTile[h] == tile)
            {
                if (insert && st != _visSeq) { _movStamp[h] = _visSeq; _movCount++; }
                return true;
            }
        }
        return false;
    }

    /// <summary>May this tile's factor have moved this frame, or move next? Tiles stamped this sample or the
    /// last; everything on an overflow or resume frame.</summary>
    private static bool TileMoving(IntPtr tile)
        => _fadeAll || (tile != IntPtr.Zero && (_movCount != 0 || _movCountPrev != 0) && MovSlot(tile, false));

    /// <summary>The slot's live model matrix, chased only if it can have moved: the record is stale or
    /// newly tenanted, the part is BSP-filed or gobless (mesh-bucket rewrite), or its gob was visited.
    /// Otherwise the record's own matrix is the live one.</summary>
    private static Matrix4x4 ChaseModel(int i, float* r, bool stale, bool newTenant, ref StageCounters c)
    {
        bool chase = stale || newTenant || _oBsp[i] || _oGob[i] == IntPtr.Zero || GobVisited(_oGob[i]);
        if (chase) { c.MxChased++; return new NodeRef(_oNode[i]).Model; }
        c.MxCached++;
        if (_fullPoll)
        {
            var live = new NodeRef(_oNode[i]).Model;
            if (live != *(Matrix4x4*)r) c.PollMissMx++;
            return live;
        }
        return *(Matrix4x4*)r;
    }

    /// <summary>#124/#327/#330: does the camera keyhole dissolve this part this frame -- lit, a feature that
    /// keyholes, and the engine's aperture test says so. The engine call is paid once per slot per camera
    /// change; while the keyhole camera words hold still the verdict is served from the slot.</summary>
    /// <para>#336: ...and for a still gobless part -- the tile geometry that is most of the routed set -- the
    /// verdict is computed from the box captured at enrolment and the camera state read once per pass, so a
    /// walking camera (which changes the key every frame) costs no engine call and no part touch. A
    /// matrix-class part may move its box and keeps the engine call. <c>/alloy poll 1</c> asks the engine too
    /// and counts a disagreement.</para>
    /// <para>The engine refuses every gob part (the decompile's <c>part+0x50 == 0</c>) and so did this, which
    /// is why vanilla's aperture punches a wall and leaves its windows floating in the hole.
    /// <c>Scene::SpawnTile</c> @0x574470 splits a tile in TWO: <c>SpawnPartsForTile</c> hands back a Gob for
    /// the parts that need their own identity -- the window that lights up at night (note the
    /// <c>AnimateStaticLight</c> behaviour <c>ProcessStaticObject</c> @0x5619e0 attaches to exactly this),
    /// the vine that danglies -- and a plain part list for the static remainder. So a tileset window IS a
    /// gob part. <see cref="EngineLayout.GobTileData"/> identifies that half positively, and it dissolves
    /// with the wall it belongs to. <c>/alloy tiledissolve 0</c> is the vanilla A/B.</para>
    private static bool Dissolves(int i, IntPtr tri, ref StageCounters c)
    {
        // Gate order is the census order: each counter must read "parts that would have dissolved but for
        // THIS", so the cheap content refusals come first and the lit one last.
        if (ShaderFeatures.NoKeyholing((int)_oFeat[i * 4])) { c.DisNoFeat++; return false; }
        IntPtr gob = _oGob[i];
        if (gob != IntPtr.Zero && !(GobKeyhole && _oTileGob[i])) { c.DisGobUnpierced++; return false; }
        // Vanilla's unlit fragment stages all compile KEYHOLING 0, and so does Alloy's (ShaderContract
        // asserts it), so the bit would reach a stage that cannot use it.
        if (!_oLit[i]) { c.DisNoLit++; return false; }
        if (gob != IntPtr.Zero)
        {
            // A gob part carries NO bbox to test against: PartTriMesh's ctor @0x53cb50 zeroes +0x64..+0x78
            // and ComputePartBBox @0x561370 -- the only thing that fills them -- runs from Scene::SpawnTile
            // alone, so tile geometry has a world box and a placeable has six zeros. That is also why the
            // engine's own test refuses gob parts outright. Nothing is lost by skipping it: the CPU verdict
            // is an ENABLE bit and CxKeyhole owns the whole shape of the hole -- the in-front-of-the-player
            // depth test, the screen aperture and the floor cut are all per fragment. Over-enabling costs a
            // batch split, never a wrong pixel.
            return _khCam && _khOn != 0;
        }
        bool cached = _disGen[i] == _camGen;
        if (cached && !_fullPoll) { c.DisCached++; return _disVerdict[i]; }
        bool live;
        if (!_oMx[i] && _khCam)
        {
            fixed (float* bx = _oBox) fixed (float* pl = _khPlane)
                live = PartTable.KeyholeDissolvesBox(bx + (long)i * 6, _khOn, pl, _khCut);
            c.DisBoxed++;
            if (_fullPoll && PartTable.KeyholeDissolves(tri) != live) c.PollMissBox++;
        }
        else live = PartTable.KeyholeDissolves(tri);
        c.DisAsked++;
        if (cached && live != _disVerdict[i]) c.PollMissDis++;
        _disVerdict[i] = live; _disGen[i] = _camGen;
        return live;
    }

    /// <summary>#126/#330: the tile fade factor this part draws at, served from the slot while its tile is
    /// not in the moving set; times the tile's fog-of-war ramp (#546), read live every frame because
    /// <c>ManageSceneBSP</c>'s visitor @0x481000 ramps it outside the fading buckets the moving set is
    /// built from. Vanilla draws an unexplored interior tile fogged to black (<c>RenderDrawBucket</c>
    /// @0x46d060: fog colour x f^2, fog end x f); Alloy fades it by alpha instead, at the same 4/s rate.
    /// ponytail: alpha, not fog-to-black -- a per-draw fog scale in the record is the upgrade if the
    /// 0,25 s see-through fade-in ever reads wrong.</summary>
    private static float TileFade(int i, IntPtr tri)
    {
        IntPtr owner = _oTile[i];
        float fog = owner == IntPtr.Zero ? 1f : *(float*)(owner + TileGeomFade);
        if (fog <= 0f) return 0f;
        if (fog > 1f) fog = 1f;
        bool cached = _fadeKnown[i] && !TileMoving(owner);
        if (cached && !_fullPoll) { _fadeServed++; return _oFadeF[i] * fog; }
        float f = PartTable.TileFadeFactor(tri);
        _fadeRead++;
        if (cached && f != _oFadeF[i]) _pollMissFade++;
        _oFadeF[i] = f; _fadeKnown[i] = true;
        return f * fog;
    }

    /// <summary>A re-tenanted slot's caches describe the previous tenant.</summary>
    private static void UnstampChase(int i) { _disGen[i] = 0; _fadeKnown[i] = false; _alphaKnown[i] = false; _occFade[i] = 0f; _occHeld[i] = 0f; }

    /// <summary>A pass skipped a part without running its record block, so the engine may move that part
    /// unobserved before the next build: the next sample chases everything once.</summary>
    private static void BreakChaseContinuity() => _lastSampleFrame = int.MinValue;

    /// <summary>The [alloy] line's clause.</summary>
    private static string ChaseNote()
    {
        long mxAll = _mxChased + _mxCached, disAll = _disAsked + _disCached, fadeAll = _fadeRead + _fadeServed;
        int bspGob = 0;   // cold: once per period, over the live slots
        for (int i = 0; i < _ownCount; i++) if (_oTri[i] != IntPtr.Zero && _oBsp[i] && _oMx[i] && _oGob[i] != IntPtr.Zero) bspGob++;
        return $"chase {_visGobsLast} gobs in view: routed {(float)_routeIn / Counters._frames:F0}/frame, " +
               $"{(float)_routeOut / Counters._frames:F0} off-frustum untouched" +
               (_visList ? $", visible set rebuilt {_visRebuilds} of {Counters._frames} frames" : ", visible set OFF") +
               $"; matrices {(float)_mxChased / Counters._frames:F0}/frame read of " +
               $"{(float)mxAll / Counters._frames:F0}" + (bspGob == 0 ? "" : $" ({bspGob} BSP-filed gob parts always read)") +
               $", keyhole {(float)_disAsked / Counters._frames:F0}/frame asked of {(float)disAll / Counters._frames:F0}, " +
               $"tile fade {(float)_fadeRead / Counters._frames:F0}/frame read of {(float)fadeAll / Counters._frames:F0} " +
               $"({_movCount} tiles pending" + (_movCount == 0 ? "" : $", first live {_movLive:F3} pending {_movPending:F3}") + ")" +
               $", keyhole {(float)_disBoxed / Counters._frames:F0}/frame from the cached box" +
               $"; dissolve refused {(float)_disNoLit / Counters._frames:F0} unlit + {(float)_disNoFeat / Counters._frames:F0} nokeyhole-feature"
               + $" + {(float)_disGobUnpierced / Counters._frames:F0} gob/frame" +
               (_fullPoll ? $"; POLL AUDIT misses (must be 0): matrix {_pollMissMx} keyhole {_pollMissDis} fade {_pollMissFade} box {_pollMissBox} alpha {_pollMissAlpha}" : "");
    }

    private static void ResetChasePeriod()
    {
        _mxChased = _mxCached = _disAsked = _disCached = _fadeRead = _fadeServed = 0;
        _routeIn = _routeOut = 0; _visRebuilds = 0;
        _pollMissMx = _pollMissDis = _pollMissFade = 0; _pollMissBox = _pollMissAlpha = 0; _disBoxed = 0;
        _disNoLit = _disNoFeat = _disGobUnpierced = 0;
    }

    /// <summary>Off-device: the visited set (hit, miss, per-sample forgetting, overflow), the camera key
    /// compare, the moving-tile walk over a fake bucket with its two-sample memory, and the resume rule.
    /// Each is the kind of logic that fails as a creature frozen mid-stride rather than as an error.</summary>
    internal static bool ChaseSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.ChaseSelfCheck");
        _visSeq++; _visCount = 0; _visAll = false;
        VisSlot((IntPtr)0x1000, true); VisSlot((IntPtr)0x2000, true); VisSlot((IntPtr)0x1000, true);
        check.Expect(_visCount == 2 && GobVisited((IntPtr)0x1000) && GobVisited((IntPtr)0x2000) && !GobVisited((IntPtr)0x3000),
          "the visited set answers membership and ignores a duplicate");
        _visSeq++; _visCount = 0;
        check.Expect(!GobVisited((IntPtr)0x1000), "a new sample forgets the previous frame's gobs");
        bool full = true;
        for (long g = 1; g <= (1 << VisBits) + 1 && full; g++) full = VisSlot((IntPtr)(g << 4), true);
        check.Expect(!full, "a full set reports the overflow");

        // Fault 1: the placement gate. A gobbucket entry is placed only if it also passes the two field
        // tests AddPartsToDrawBuckets makes; a part whose gob fails them holds the identity matrix.
        byte* pGob = stackalloc byte[EngineLayout.GobVisible + 1];
        new Span<byte>(pGob, EngineLayout.GobVisible + 1).Clear();
        IntPtr* pItems = stackalloc IntPtr[1];
        pItems[0] = (IntPtr)pGob;
        byte* pBucket = stackalloc byte[BucketBytes];
        *(IntPtr**)(pBucket + BucketItems) = pItems; *(int*)(pBucket + BucketCount) = 1;
        int ps = MaxOwned - 1;
        IntPtr wasGob = _oGob[ps]; bool wasBsp = _oBsp[ps], wasPlaced = _oPlaced[ps];
        _oGob[ps] = (IntPtr)pGob; _oBsp[ps] = false; _oPlaced[ps] = false;
        *(byte*)(pGob + EngineLayout.GobVisible) = 1; *(float*)(pGob + EngineLayout.GobAlpha) = 0f;
        _visSeq++; _visCount = 0;
        _visSetOk = SampleVisibleGobs(pBucket);
        check.Expect(_visSetOk && !PartPlaced(ps), "a gobbucket gob at zero alpha is unplaced (no matrix was written)");
        *(float*)(pGob + EngineLayout.GobAlpha) = 1f; *(byte*)(pGob + EngineLayout.GobVisible) = 0;
        _visSeq++; _visCount = 0; _visSetOk = SampleVisibleGobs(pBucket);
        check.Expect(!PartPlaced(ps), "an invisible gobbucket gob is unplaced");
        *(byte*)(pGob + EngineLayout.GobVisible) = 1;
        _visSeq++; _visCount = 0; _visSetOk = SampleVisibleGobs(pBucket);
        check.Expect(PartPlaced(ps) && _oPlaced[ps], "a visible, opaque gob in gobbucket places the slot");
        _visSeq++; _visCount = 0;   // the set now holds nothing: the gob left the engine's view volume
        check.Expect(PartPlaced(ps), "the latch holds after the gob leaves the engine's set");
        _oPlaced[ps] = false; _visSetOk = false;
        check.Expect(PartPlaced(ps), "an unreadable gobbucket stands the gate down");
        _oPlaced[ps] = false; _visSetOk = true; _oBsp[ps] = true;
        check.Expect(PartPlaced(ps), "a BSP-filed part is not gated -- its matrix is the mesh-bucket loop's");
        _oBsp[ps] = false; _oGob[ps] = IntPtr.Zero;
        check.Expect(PartPlaced(ps), "a gobless part is not gated");
        _oGob[ps] = wasGob; _oBsp[ps] = wasBsp; _oPlaced[ps] = wasPlaced; _visSetOk = false;

        ulong* key = stackalloc ulong[PartTable.CamKeyholeWords + 1];
        int camBytes = EngineLayout.CamKeyholeState + PartTable.CamKeyholeWords * 8;
        byte* cam = stackalloc byte[camBytes];
        new Span<byte>(cam, camBytes).Clear();
        new Span<ulong>(key, PartTable.CamKeyholeWords + 1).Clear();
        check.Expect(PartTable.KeyholeCameraMoved((IntPtr)cam, key) && !PartTable.KeyholeCameraMoved((IntPtr)cam, key),
          "the camera key moves once for a new camera and then holds");
        cam[camBytes - 4] = 1;   // the +0xe64 height cut, last word of the span
        check.Expect(PartTable.KeyholeCameraMoved((IntPtr)cam, key), "a change in the last keyhole word is seen");
        check.Expect(PartTable.KeyholeCameraMoved(IntPtr.Zero, key) && !PartTable.KeyholeCameraMoved(IntPtr.Zero, key),
          "losing the camera moves the key once");

        byte* tile = stackalloc byte[TileStubBytes];
        new Span<byte>(tile, TileStubBytes).Clear();
        IntPtr* items = stackalloc IntPtr[1];
        items[0] = (IntPtr)tile;
        byte* bucket = stackalloc byte[BucketBytes];
        *(IntPtr**)(bucket + BucketItems) = items; *(int*)(bucket + BucketCount) = 1;
        *(float*)(tile + EngineLayout.TileFade) = 1f; *(float*)(tile + TilePendingFade) = 1f;
        _visSeq++; _movCountPrev = _movCount = 0; _fadeAll = false;
        check.Expect(MarkPendingTiles(bucket) && _movCount == 0 && !TileMoving((IntPtr)tile), "a settled tile pends nothing");
        *(float*)(tile + TilePendingFade) = 0.5f;
        check.Expect(MarkPendingTiles(bucket) && _movCount == 1 && TileMoving((IntPtr)tile) && !TileMoving((IntPtr)0x4000),
          "a stepped tile is moving, its neighbour is not");
        _visSeq++; _movCountPrev = _movCount; _movCount = 0;
        check.Expect(TileMoving((IntPtr)tile), "...and still moving the sample after (the commit lands then)");
        _visSeq++; _movCountPrev = _movCount; _movCount = 0;
        check.Expect(!TileMoving((IntPtr)tile), "...and settled two samples on");
        check.Expect(!MarkPendingTiles(null), "an unresolved bucket reads as every tile moving");
        *(IntPtr**)(bucket + BucketItems) = null; *(int*)(bucket + BucketCount) = 0;
        check.Expect(MarkPendingTiles(bucket), "...but an EMPTY one (null items, count 0) marks nothing and stays bounded");

        // #546: the fog-of-war ramp rides the served factor, live, and 0 is a skip even from a warm cache
        byte* fogTile = stackalloc byte[TileGeomFade + sizeof(float)];
        new Span<byte>(fogTile, TileGeomFade + sizeof(float)).Clear();
        byte* fogTri = stackalloc byte[PartTile + sizeof(IntPtr)];
        new Span<byte>(fogTri, PartTile + sizeof(IntPtr)).Clear();
        *(IntPtr*)(fogTri + PartTile) = (IntPtr)fogTile;
        int slot = MaxOwned - 1; IntPtr wasTile = _oTile[slot]; bool wasKnown = _fadeKnown[slot]; float wasF = _oFadeF[slot];
        _oTile[slot] = (IntPtr)fogTile; _fadeKnown[slot] = false; _fadeAll = false;
        *(float*)(fogTile + EngineLayout.TileFade) = 1f; *(float*)(fogTile + TileGeomFade) = 1f;
        check.Expect(TileFade(slot, (IntPtr)fogTri) == 1f && _fadeKnown[slot], "an explored tile fades at its own factor");
        *(float*)(fogTile + TileGeomFade) = 0.5f;
        check.Expect(TileFade(slot, (IntPtr)fogTri) == 0.5f, "a tile exploring in is served half from the warm cache");
        *(float*)(fogTile + TileGeomFade) = 0f;
        check.Expect(TileFade(slot, (IntPtr)fogTri) == 0f, "an unexplored tile is 0, cache or not");
        _oTile[slot] = wasTile; _fadeKnown[slot] = wasKnown; _oFadeF[slot] = wasF;

        // the resume rule: a gap in the sample frames chases everything once
        int wasFrame = _frameSeq;
        byte* saveGob = _gobBucket; _gobBucket = null;
        _lastSampleFrame = int.MinValue; _frameSeq = 10;
        SampleEngineSets();
        check.Expect(_visAll && _fadeAll, "the first sample after a gap serves no cache");
        _gobBucket = saveGob; _frameSeq = wasFrame; _lastSampleFrame = int.MinValue; _fadeAll = _visAll = true;
        log.Info(check.Ok ? "AlloyRenderer.ChaseSelfCheck: passed" : "AlloyRenderer.ChaseSelfCheck: FAILED -- a bounded chase would serve a stale record");
        return check.Ok;
    }
}
