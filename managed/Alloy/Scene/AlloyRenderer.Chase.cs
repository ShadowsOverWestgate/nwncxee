using System;
using System.Numerics;
using Sowx.Framework;

namespace Sowx.Alloy;

/// <summary>#330: the engine chase, BOUNDED. Restage used to re-read every owned part's borrowed engine
/// state every frame -- node matrix, keyhole verdict, tile fade -- which made it O(world) whatever the
/// camera saw. The binary has no per-change setter for any of those (research on the ticket), but it does
/// keep the sets that say which parts CAN have changed, and they are all view-sized:
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
/// <para><c>/draw poll 1</c> is the A/B and the soundness instrument: every cache is bypassed AND audited --
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
    /// moving-tile set without dereferencing the part.</summary>
    private static readonly IntPtr[] _oTile = new IntPtr[MaxOwned];
    /// <summary>The part was filed in the BSP (mesh-bucket loop territory) rather than reached through a gob
    /// tree. Its matrix rewrite does not depend on <c>gobbucket</c>, so it is chased every frame.</summary>
    private static readonly bool[] _oBsp = new bool[MaxOwned];
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
    /// <summary><c>TileData+0x74</c>: the pending factor the fade handlers step; committed into
    /// <see cref="PartTable.TileFade"/> at the top of <c>ManageSceneBSP</c> @0x47fa80.</summary>
    private const int TilePendingFade = 0x74, TileStubBytes = TilePendingFade + sizeof(float);
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

    /// <summary><c>/draw poll 1</c>: bypass every chase cache and audit it.</summary>
    private static bool _fullPoll;
    /// <summary>Period counters: per term, reads made vs reads the cache absorbed; the audit's misses; the
    /// BSP-filed gob parts the visited set cannot bound.</summary>
    private static long _mxChased, _mxCached, _disAsked, _disCached, _fadeRead, _fadeServed;
    private static long _pollMissMx, _pollMissDis, _pollMissFade;
    private static long _routeIn, _routeOut;
    private static int _visGobsLast;

    private static void SetPassFrustum(float* vp, bool perspective)
    {
        _passFrustumOk = perspective;
        if (perspective) fixed (float* p = _passPlanes) CullPass.Planes(vp, p);
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
        if (_recGen[i] != _recEpoch || _recEnroll[i] != _oEnrollGen[i]) return true;
        if (_oGob[i] != IntPtr.Zero && VisSlot(_oGob[i], false)) return true;
        float* s = sph + (long)i * 4;
        if (s[3] >= NeverCull) return true;
        float r = s[3] * ShadowMath.FrustumCullSlack + RouteSlackUnits;
        fixed (float* p = _passPlanes)
            for (int f = 0; f < 6; f++)
            {
                float* pl = p + f * 4;
                if (pl[0] * s[0] + pl[1] * s[1] + pl[2] * s[2] + pl[3] < -r)
                {
                    _routeOut++;
                    _recFrame[i] = _frameSeq;
                    return false;
                }
            }
        _routeIn++;
        return true;
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
        _visAll = !continuous || _gobBucket == null;
        if (!_visAll)
        {
            int n = *(int*)(_gobBucket + BucketCount);
            IntPtr* items = *(IntPtr**)(_gobBucket + BucketItems);
            // an EMPTY CExoArrayList keeps a null items pointer: nothing in view, not an unreadable list
            if (n < 0 || n > VisLoadCap || (n > 0 && items == null)) _visAll = true;
            else for (int k = 0; k < n && !_visAll; k++) if (!VisSlot(items[k], true)) _visAll = true;
        }
        _visGobsLast = _visCount;
        fixed (ulong* key = _camKey)
            if (PartTable.KeyholeCameraMoved(PartTable.CurrentCamera, key)) _camGen++;
        _movCountPrev = _movCount;
        _movCount = 0;
        _fadeAll = !continuous || !MarkPendingTiles(_fadeOutBucket) || !MarkPendingTiles(_fadeInBucket);
    }

    /// <summary>Probe the visited set for <paramref name="gob"/>; with <paramref name="insert"/> an absent gob
    /// is added. Returns membership after the call, or false on a full table with insert set.</summary>
    private static bool VisSlot(IntPtr gob, bool insert)
    {
        if (gob == IntPtr.Zero) return false;
        int h = (int)((ulong)gob >> 4) & VisMask;
        for (int probe = 0; probe <= VisMask; probe++, h = (h + 1) & VisMask)
        {
            if (_visStamp[h] != _visSeq)
            {
                if (!insert) return false;
                _visStamp[h] = _visSeq; _visGob[h] = gob; _visCount++;
                return true;
            }
            if (_visGob[h] == gob) return true;
        }
        return false;
    }

    /// <summary>Was this gob in the engine's in-view list this frame -- i.e. may its parts' matrices have
    /// been rewritten? True for everything on an overflow or resume frame.</summary>
    private static bool GobVisited(IntPtr gob) => _visAll || VisSlot(gob, false);

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
            float live = *(float*)(tile + PartTable.TileFade), pending = *(float*)(tile + TilePendingFade);
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
    private static Matrix4x4 ChaseModel(int i, float* r, bool stale, bool newTenant)
    {
        bool chase = stale || newTenant || _oBsp[i] || _oGob[i] == IntPtr.Zero || GobVisited(_oGob[i]);
        if (chase) { _mxChased++; return new NodeRef(_oNode[i]).Model; }
        _mxCached++;
        if (_fullPoll)
        {
            var live = new NodeRef(_oNode[i]).Model;
            if (live != *(Matrix4x4*)r) _pollMissMx++;
            return live;
        }
        return *(Matrix4x4*)r;
    }

    /// <summary>#124/#327/#330: does the camera keyhole dissolve this part this frame -- lit, a feature that
    /// keyholes, and the engine's aperture test says so. The engine call is paid once per slot per camera
    /// change; while the keyhole camera words hold still the verdict is served from the slot.</summary>
    private static bool Dissolves(int i, IntPtr tri)
    {
        if (!_oLit[i] || ShaderFeatures.NoKeyholing((int)_oFeat[i * 4])) return false;
        bool cached = _disGen[i] == _camGen;
        if (cached && !_fullPoll) { _disCached++; return _disVerdict[i]; }
        bool live = PartTable.KeyholeDissolves(tri);
        _disAsked++;
        if (cached && live != _disVerdict[i]) _pollMissDis++;
        _disVerdict[i] = live; _disGen[i] = _camGen;
        return live;
    }

    /// <summary>#126/#330: the tile fade factor this part draws at, served from the slot while its tile is
    /// not in the moving set.</summary>
    private static float TileFade(int i, IntPtr tri)
    {
        bool cached = _fadeKnown[i] && !TileMoving(_oTile[i]);
        if (cached && !_fullPoll) { _fadeServed++; return _oFadeF[i]; }
        float f = PartTable.TileFadeFactor(tri);
        _fadeRead++;
        if (cached && f != _oFadeF[i]) _pollMissFade++;
        _oFadeF[i] = f; _fadeKnown[i] = true;
        return f;
    }

    /// <summary>A re-tenanted slot's caches describe the previous tenant.</summary>
    private static void UnstampChase(int i) { _disGen[i] = 0; _fadeKnown[i] = false; }

    /// <summary>A pass skipped a part without running its record block, so the engine may move that part
    /// unobserved before the next build: the next sample chases everything once.</summary>
    private static void BreakChaseContinuity() => _lastSampleFrame = int.MinValue;

    /// <summary>The [alloy] line's clause.</summary>
    private static string ChaseNote()
    {
        long mxAll = _mxChased + _mxCached, disAll = _disAsked + _disCached, fadeAll = _fadeRead + _fadeServed;
        int bspGob = 0;   // cold: once per period, over the live slots
        for (int i = 0; i < _ownCount; i++) if (_oTri[i] != IntPtr.Zero && _oBsp[i] && _oMx[i] && _oGob[i] != IntPtr.Zero) bspGob++;
        return $"chase {_visGobsLast} gobs in view: routed {(float)_routeIn / _frames:F0}/frame, " +
               $"{(float)_routeOut / _frames:F0} off-frustum untouched; matrices {(float)_mxChased / _frames:F0}/frame read of " +
               $"{(float)mxAll / _frames:F0}" + (bspGob == 0 ? "" : $" ({bspGob} BSP-filed gob parts always read)") +
               $", keyhole {(float)_disAsked / _frames:F0}/frame asked of {(float)disAll / _frames:F0}, " +
               $"tile fade {(float)_fadeRead / _frames:F0}/frame read of {(float)fadeAll / _frames:F0} " +
               $"({_movCount} tiles pending" + (_movCount == 0 ? "" : $", first live {_movLive:F3} pending {_movPending:F3}") + ")" +
               (_fullPoll ? $"; POLL AUDIT misses (must be 0): matrix {_pollMissMx} keyhole {_pollMissDis} fade {_pollMissFade}" : "");
    }

    private static void ResetChasePeriod()
    {
        _mxChased = _mxCached = _disAsked = _disCached = _fadeRead = _fadeServed = 0;
        _routeIn = _routeOut = 0;
        _pollMissMx = _pollMissDis = _pollMissFade = 0;
    }

    /// <summary>Off-device: the visited set (hit, miss, per-sample forgetting, overflow), the camera key
    /// compare, the moving-tile walk over a fake bucket with its two-sample memory, and the resume rule.
    /// Each is the kind of logic that fails as a creature frozen mid-stride rather than as an error.</summary>
    internal static bool ChaseSelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"AlloyRenderer.ChaseSelfCheck FAIL: {w}"); } }
        _visSeq++; _visCount = 0; _visAll = false;
        VisSlot((IntPtr)0x1000, true); VisSlot((IntPtr)0x2000, true); VisSlot((IntPtr)0x1000, true);
        E(_visCount == 2 && GobVisited((IntPtr)0x1000) && GobVisited((IntPtr)0x2000) && !GobVisited((IntPtr)0x3000),
          "the visited set answers membership and ignores a duplicate");
        _visSeq++; _visCount = 0;
        E(!GobVisited((IntPtr)0x1000), "a new sample forgets the previous frame's gobs");
        bool full = true;
        for (long g = 1; g <= (1 << VisBits) + 1 && full; g++) full = VisSlot((IntPtr)(g << 4), true);
        E(!full, "a full set reports the overflow");

        ulong* key = stackalloc ulong[PartTable.CamKeyholeWords + 1];
        const int camBytes = PartTable.CamKeyholeState + PartTable.CamKeyholeWords * 8;
        byte* cam = stackalloc byte[camBytes];
        new Span<byte>(cam, camBytes).Clear();
        new Span<ulong>(key, PartTable.CamKeyholeWords + 1).Clear();
        E(PartTable.KeyholeCameraMoved((IntPtr)cam, key) && !PartTable.KeyholeCameraMoved((IntPtr)cam, key),
          "the camera key moves once for a new camera and then holds");
        cam[camBytes - 4] = 1;   // the +0xe64 height cut, last word of the span
        E(PartTable.KeyholeCameraMoved((IntPtr)cam, key), "a change in the last keyhole word is seen");
        E(PartTable.KeyholeCameraMoved(IntPtr.Zero, key) && !PartTable.KeyholeCameraMoved(IntPtr.Zero, key),
          "losing the camera moves the key once");

        byte* tile = stackalloc byte[TileStubBytes];
        new Span<byte>(tile, TileStubBytes).Clear();
        IntPtr* items = stackalloc IntPtr[1];
        items[0] = (IntPtr)tile;
        byte* bucket = stackalloc byte[BucketBytes];
        *(IntPtr**)(bucket + BucketItems) = items; *(int*)(bucket + BucketCount) = 1;
        *(float*)(tile + PartTable.TileFade) = 1f; *(float*)(tile + TilePendingFade) = 1f;
        _visSeq++; _movCountPrev = _movCount = 0; _fadeAll = false;
        E(MarkPendingTiles(bucket) && _movCount == 0 && !TileMoving((IntPtr)tile), "a settled tile pends nothing");
        *(float*)(tile + TilePendingFade) = 0.5f;
        E(MarkPendingTiles(bucket) && _movCount == 1 && TileMoving((IntPtr)tile) && !TileMoving((IntPtr)0x4000),
          "a stepped tile is moving, its neighbour is not");
        _visSeq++; _movCountPrev = _movCount; _movCount = 0;
        E(TileMoving((IntPtr)tile), "...and still moving the sample after (the commit lands then)");
        _visSeq++; _movCountPrev = _movCount; _movCount = 0;
        E(!TileMoving((IntPtr)tile), "...and settled two samples on");
        E(!MarkPendingTiles(null), "an unresolved bucket reads as every tile moving");
        *(IntPtr**)(bucket + BucketItems) = null; *(int*)(bucket + BucketCount) = 0;
        E(MarkPendingTiles(bucket), "...but an EMPTY one (null items, count 0) marks nothing and stays bounded");

        // the resume rule: a gap in the sample frames chases everything once
        int wasFrame = _frameSeq;
        byte* saveGob = _gobBucket; _gobBucket = null;
        _lastSampleFrame = int.MinValue; _frameSeq = 10;
        SampleEngineSets();
        E(_visAll && _fadeAll, "the first sample after a gap serves no cache");
        _gobBucket = saveGob; _frameSeq = wasFrame; _lastSampleFrame = int.MinValue; _fadeAll = _visAll = true;
        log.Info(ok ? "AlloyRenderer.ChaseSelfCheck: passed" : "AlloyRenderer.ChaseSelfCheck: FAILED -- a bounded chase would serve a stale record");
        return ok;
    }
}
