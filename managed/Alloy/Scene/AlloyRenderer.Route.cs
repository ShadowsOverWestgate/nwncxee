using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-103/CX-104: <b>the route gate, over Alloy's own scene state.</b> This file is what is left of
/// the bounded chase (#330) once the engine's per-frame scene work is nobody's input any more.
///
/// <para><b>What went.</b> The chase existed because three things Alloy drew with were the engine's to write
/// and there was no notice for any of them. All three are ours now:
/// <list type="bullet">
/// <item>the world matrix -- <see cref="GobXform"/> composes it (CX-103), so the <c>gobbucket</c> visited set,
/// the <c>Fault 1</c> placement latch (<c>PartPlaced</c>/<c>_oPlaced</c>, which existed only because a gob the
/// engine never placed drew at the area's (0,0) corner) and the per-frame re-read of <c>vp+0x18</c> are
/// deleted;</item>
/// <item>the tile fade -- <see cref="TileFadeRamp"/> owns the ramp (CX-104), so the two fading buckets, the
/// moving-tile set and the per-slot fade cache that existed only because the engine read was expensive are
/// deleted;</item>
/// <item>the enumeration hint -- <see cref="AreaGrid"/> is Alloy's own index of the area (CX-104), so the
/// visible set is built from it instead of from an O(area) scan, and <see cref="VisibleGobs"/> answers off the
/// Scene's own gob list rather than off <c>gobbucket</c>.</item>
/// </list>
/// The keyhole verdict KEPT its cache: <c>Camera::GetCanKeyholeDissolve</c> is pure in the camera's keyhole
/// words plus spawn-time part fields, so an unchanged snapshot really does mean every verdict is last
/// frame's -- that is a property of the engine's own function, not a bet on what it wrote.</para>
///
/// <para><c>/alloy poll 1</c> is still the audit dial: it bypasses the keyhole cache and compares.
/// <c>alloy.xformcheck</c> is the matrix one (<see cref="GobXform.Check"/>) and is the only read of
/// <c>VertexPrimitive+0x18</c> left in Alloy.</para></summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The keyhole camera key (<see cref="PartTable.KeyholeCameraMoved"/>) and the generation it
    /// last changed on. A slot's verdict is current while <see cref="_camGen"/> matches.</summary>
    private static readonly ulong[] _camKey = new ulong[PartTable.CamKeyholeWords + 1];
    private static int _camGen = 1;
    private static readonly int[] _disGen = new int[MaxOwned];
    private static readonly bool[] _disVerdict = new bool[MaxOwned];

    /// <summary>The part's TileData (part+0x98, spawn-time), so the fade question is answered without
    /// dereferencing the part. #546: a tile GOB part (window, vine) carries no part+0x98; its gob's
    /// <see cref="EngineLayout.GobTileData"/> stands in, which is the owner <c>Gob::ProxyPartDraw</c>
    /// @0x4520c0 fogs it by.</summary>
    private static readonly IntPtr[] _oTile = new IntPtr[MaxOwned];
    /// <summary>...and the tile whose CAMERA-OCCLUSION fade this part takes, which is its own
    /// <c>part+0x98</c>, and only for a <c>tilefade 1</c> part (<see cref="TileFadeRamp.Fades"/>): the rest of
    /// the tile keeps drawing under the lifted roof. Kept apart from <see cref="_oTile"/> because the two populations
    /// differ: the fading buckets hold tile PARTS, so a tile GOB's parts (the window that lights up, the vine
    /// that danglies) never took the occlusion fade in vanilla -- only <c>Gob::ProxyPartDraw</c> @0x4520c0's
    /// fog-of-war ramp reaches them. Merging the two would fade a window out with the wall it sits in, which
    /// is a change nobody asked for.</summary>
    private static readonly IntPtr[] _oFadeTile = new IntPtr[MaxOwned];
    /// <summary>This pass's six frustum planes (<see cref="CullPass.Planes"/>), and whether the pass had a
    /// perspective projection to take them from. Without one every slot routes.</summary>
    private static readonly float[] _passPlanes = new float[24];
    private static bool _passFrustumOk;
    /// <summary>World units added to a slot's slack-scaled sphere before the plane test: one frame of camera
    /// motion, so a part entering the view is routed the frame it appears rather than the frame after.</summary>
    private const float RouteSlackUnits = 3f;

    /// <summary><c>/alloy poll 1</c>: bypass the keyhole cache and audit it.</summary>
    private static bool _fullPoll => Renderer.Poll.V;
    /// <summary>Period counters: matrices recomposed vs served from their own record, the keyhole cache's two
    /// halves, and the route gate's two.</summary>
    private static long _mxChased, _mxCached, _disAsked, _disCached;
    private static long _pollMissDis;
    private static long _routeIn, _routeOut, _disBoxed, _pollMissBox, _pollMissAlpha;
    /// <summary>Why a part was refused the dissolve, per frame. The three gates ahead of the aperture test,
    /// counted apart because "the keyhole stops at that window" is always one of them and the census is the
    /// only thing that says which.</summary>
    private static long _disNoLit, _disNoFeat, _disGobUnpierced;

    /// <summary>Whether a TILE gob's parts may dissolve at all. <c>/alloy tiledissolve 0</c> is the vanilla A/B:
    /// off is the engine's own rule, where the aperture stops at every gob. See <see cref="Dissolves"/>.</summary>
    internal static bool GobKeyhole => Renderer.TileDissolve.V;
    /// <summary>#336: the part's world box at enrolment (six floats), for the dissolve verdict AND for
    /// <see cref="AreaGrid"/>'s filing -- it is the same box, populated by <c>ComputePartBBox</c> @0x561370 for
    /// tile geometry and six zeros for everything hanging off a gob.</summary>
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
        var c = new Camera(cam);
        _khOn = c.KeyholeState;
        c.KeyholePlane.CopyTo(_khPlane);
        _khCut = c.KeyholeCut;
    }

    // ================= the visible set =================

    /// <summary>#336 / CX-104: the VISIBLE SET -- per class, in enrollment order, the slots the route walk
    /// visits. Built from <see cref="AreaGrid"/> rather than by scanning the whole table: every slot on a gob
    /// (a mover by construction, and the population <see cref="GobXform"/> recomposes), every slot the grid
    /// could not bound, and every filed slot whose cell survives this pass's planes. Rebuilt when a plane could
    /// have drifted past <see cref="VisDriftUnits"/> anywhere in view (<see cref="VisDrifted"/>); in between
    /// the walk skips the off-screen world outright.</summary>
    private static readonly int[] _vis = new int[MaxOwned];
    private static readonly int[] _visStart = new int[AlloyClasses.Count], _visEnd = new int[AlloyClasses.Count];
    private static readonly float[] _visPlanes = new float[24];
    private static int _visBuiltAt, _visEnrollGen = -1, _visEpoch = -1;
    /// <summary>Bumped by every <see cref="BuildIndex"/>: the set must be rebuilt WHOLE after one, even when
    /// no enrolment generation moved (a budgeted walk bumps it at its begin, frames before the finish that
    /// re-seats slots). It used to be "_visBuiltAt = int.MinValue", and <c>_frameSeq - int.MinValue</c>
    /// overflows negative -- so the rebuild never came, and a repacked table was routed off the old set.</summary>
    private static int _visIndexGen, _visIndexSeen = -1;
    private static bool _visFull = true;
    private static bool _visList => Renderer.VisList.V;
    private static long _visRebuilds, _visIndexed;
    /// <summary>How far (world units) any plane may have moved, anywhere the pass can see, before a reused set
    /// is rebuilt. The index query takes it on top of <see cref="RouteSlackUnits"/>, so a reused set still holds
    /// every box the pass's own planes would keep. TAA's sub-pixel jitter stays well under it.</summary>
    private const float VisDriftUnits = 2f;

    /// <summary>CX-104: <see cref="_order"/>'s inverse permutation -- where slot i sits in the enrollment
    /// order. The index yields slots in CELL order, and the enrollment order is the batch key (class, then
    /// texture, then feature/primitive, then VAO), so the query's output is sorted back into it before the
    /// class ranges are cut. Without that, one visible cell per tile would cut a batch per tile.</summary>
    private static readonly int[] _orderRank = new int[MaxOwned];
    /// <summary>CX-104: the slots on a gob, in enrollment order. Always in the visible set: they are the movers,
    /// and their exact per-pass verdict is <see cref="RouteWanted"/>'s sphere test.</summary>
    private static readonly int[] _dynList = new int[MaxOwned];
    private static int _dynN;
    /// <summary>The radix sort's scratch and its 9-bit counters (two passes cover
    /// <see cref="MaxOwned"/>'s 18 bits).</summary>
    private static readonly int[] _visTmp = new int[MaxOwned];
    private const int RadixBits = 9, RadixBuckets = 1 << RadixBits, RadixMask = RadixBuckets - 1;
    private static readonly int[] _radix = new int[RadixBuckets];

    /// <summary>CX-104: rebuild the area index and the two order-derived lists. Called at the end of every
    /// enrolment -- the one edge that changes which slot holds which part -- so nothing incremental is needed
    /// and a slot never has to be removed from a cell.</summary>
    private static void BuildIndex()
    {
        for (int k = 0; k < _orderCount; k++) _orderRank[_order[k]] = k;
        AreaGrid.Begin();
        _dynN = 0; _unboxedEmpty = 0;
        fixed (float* boxes = _oBox)
            for (int k = 0; k < _orderCount; k++)
            {
                int i = _order[k];
                if (_oGob[i] != IntPtr.Zero) { _dynList[_dynN++] = i; continue; }
                // The world box ComputePartBBox filled at tile spawn. One that was never filled is a part with
                // no bounds to index on, and unbounded means VISIBLE -- the same answer its NeverCull sphere
                // already gave the cull.
                float* b = boxes + (long)i * 6;
                if (HasBox(b)) AreaGrid.Add(i, b);
                else { AreaGrid.AddUnboxed(i); if (b[0] == 0f && b[1] == 0f && b[2] == 0f && b[3] == 0f && b[4] == 0f && b[5] == 0f) _unboxedEmpty++; }
            }
        // The set of slots changed, so last pass's visible set describes nothing.
        _visIndexGen++;
    }

    /// <summary>Does this world box carry bounds? The engine zeroes it in the <c>PartTriMesh</c> ctor and only
    /// <c>Scene::SpawnTile</c> fills it, so all-zero is "never filled"; a zero extent on one axis is a flat,
    /// axis-aligned surface (a wall, a floor) and IS a box. Requiring a positive width on x and y left every
    /// axis-aligned wall unboxed -- walked and sphere-tested every pass, whatever the camera saw.</summary>
    private static bool HasBox(float* b)
        => b[3] >= b[0] && b[4] >= b[1] && b[5] >= b[2]
           && (b[0] != 0f || b[1] != 0f || b[2] != 0f || b[3] != 0f || b[4] != 0f || b[5] != 0f);
    /// <summary>Of the unboxed slots at the last index build, how many had an all-zero (never filled) box.</summary>
    private static int _unboxedEmpty;

    /// <summary>Before the route walk: decide whether this pass walks the whole order or the visible set,
    /// and rebuild the set if it is due.</summary>
    private static void PrepareVisible()
    {
        ReadKeyholeCamera();
        _visFull = !_visList || !_passFrustumOk;
        if (_visFull) { _texStalePending = false; return; }   // this pass walks every slot anyway
        // CX-104 (review): a cold path has marked slots tex-stale, and RouteWanted can only re-resolve the
        // ones the set visits -- see NoteTexStalePending.
        bool full = _visEnrollGen != _enrollGen || _visEpoch != _recEpoch || _visIndexSeen != _visIndexGen
                    || _texStalePending;
        if (!full && !VisDrifted()) { _visWhy = 0; return; }
        _visWhy = full ? 2 : 1;
        _visRebuilds++; FrameZones.Count(FrameZones.Ev.VisRebuild);
        _visBuiltAt = _frameSeq; _visEnrollGen = _enrollGen; _visEpoch = _recEpoch; _visIndexSeen = _visIndexGen;
        Array.Copy(_passPlanes, _visPlanes, 24);
        // An enrolment or an epoch bump means some slot has no record to serve and MUST be rebuilt whatever
        // the frustum says, so that one rebuild walks the whole order. Every other rebuild is index-driven.
        if (full) { _texStalePending = false; BuildVisibleFull(); return; }
        int n = 0;
        for (int k = 0; k < _dynN && n < _vis.Length; k++) _vis[n++] = _dynList[k];
        fixed (float* p = _passPlanes) n = AreaGrid.Query(p, RouteSlackUnits + VisDriftUnits, _vis, n);
        _visIndexed += n;
        SortVisibleByOrder(n);
        CutVisibleClasses(n);
    }

    /// <summary>Could a plane have moved more than <see cref="VisDriftUnits"/> at a point in view since the set
    /// was built? A plane's change at p is <c>dn.(p - eye) + (dn.eye + dd)</c>; p lies within the far plane's
    /// distance of the eye times <see cref="VisReachScale"/>, which covers a frustum corner at any sane FOV.</summary>
    private static bool VisDrifted()
    {
        Vector3 e = _eyePos;
        float reach = MathF.Max(0f, _passPlanes[20] * e.X + _passPlanes[21] * e.Y + _passPlanes[22] * e.Z + _passPlanes[23])
                      * VisReachScale;
        for (int o = 0; o < 24; o += 4)
        {
            float dx = _passPlanes[o] - _visPlanes[o], dy = _passPlanes[o + 1] - _visPlanes[o + 1];
            float dz = _passPlanes[o + 2] - _visPlanes[o + 2], dw = _passPlanes[o + 3] - _visPlanes[o + 3];
            if (MathF.Abs(dx * e.X + dy * e.Y + dz * e.Z + dw) + MathF.Sqrt(dx * dx + dy * dy + dz * dz) * reach > VisDriftUnits)
                return true;
        }
        return false;
    }
    /// <summary>Frustum corner distance over far-plane distance, generously: 1,55 at a 90x60 degree view.</summary>
    private const float VisReachScale = 2f;

    /// <summary>The flicker hunt: this pass's visible-set verdict (0 reused, 1 rebuilt from the index, 2 full),
    /// the running mean of opaque slots routed, and what the audit found.</summary>
    private static int _visWhy, _routedAvg, _visLogged;
    private static long _visCollapses, _visAudits, _visMisses;
    private static readonly int[] _visMark = new int[MaxOwned];
    private static int _visMarkGen;
    private const int VisLogCap = 6;

    /// <summary>After the opaque/cutout route: did this pass's visible set collapse against the running mean,
    /// and -- on a collapse or 1 frame in 64 -- does the set miss any live slot the pass's own sphere test
    /// would route? A miss is a slot the full walk draws and the visible set does not: the flicker.</summary>
    private static void AuditVisible(int routed, float* sph)
    {
        if (_visFull) return;
        bool collapse = _routedAvg > 400 && routed * 4 < _routedAvg;
        _routedAvg = _routedAvg == 0 ? routed : (_routedAvg * 7 + routed) / 8;
        if (collapse) _visCollapses++;
        if (!collapse && (_frameSeq & 63) != 0) return;
        _visAudits++;
        int gen = ++_visMarkGen, total = 0;
        for (int c = 0; c < AlloyClasses.Count; c++)
            for (int k = _visStart[c]; k < _visEnd[c]; k++) { _visMark[_vis[k]] = gen; total++; }
        int missed = 0, first = -1;
        for (int k = 0; k < _orderCount; k++)
        {
            int i = _order[k];
            if (_oTri[i] == IntPtr.Zero || _recGen[i] != _recEpoch || _visMark[i] == gen) continue;
            if (!SphereInPass(i, sph) || !BoxInPass(i)) continue;
            if (first < 0) first = i;
            missed++;
        }
        _visMisses += missed;
        // a collapse the set did not cause (a camera with nothing in front of it) is not this hunt's
        if (missed == 0 || _visLogged >= VisLogCap) return;
        _visLogged++;
        string where = "";
        if (first >= 0)
            fixed (float* ob = _oBox)
            {
                float* b = ob + (long)first * 6;
                float* sp = sph + (long)first * 4;
                int held = Array.IndexOf(_vis, first, 0, _visEnd[AlloyClasses.Count - 1]);
                where = $" first miss slot {first} cls {_oCls[first]} rank {_orderRank[first]} gob {(_oGob[first] != IntPtr.Zero ? "yes" : "no")} "
                      + $"{(held >= 0 ? $"IN the set at {held} but outside its class cut" : "absent from the set")} "
                      + $"box ({b[0]:F1},{b[1]:F1},{b[2]:F1})-({b[3]:F1},{b[4]:F1},{b[5]:F1}) "
                      + $"sphere ({sp[0]:F1},{sp[1]:F1},{sp[2]:F1} r{sp[3]:F1})";
            }
        _log?.Always($"AlloyRenderer: [vischeck] {(collapse ? "COLLAPSE" : "miss")} frame {_frameSeq}: routed {routed} vs mean "
                   + $"{_routedAvg}, visible set {total} ({(_visWhy == 0 ? "reused" : _visWhy == 1 ? "rebuilt from the index" : "rebuilt full")}, "
                   + $"built at {_visBuiltAt}, enrol gen {_visEnrollGen}/{_enrollGen}, epoch {_visEpoch}/{_recEpoch}), {_dynN} gob slots, "
                   + $"{AreaGrid.Filed} filed in {AreaGrid.Occupied} cells, {AreaGrid.Unboxed} unboxed, {missed} live slot(s) the set misses;"
                   + where + $" eye ({_eyePos.X:F1},{_eyePos.Y:F1},{_eyePos.Z:F1}) near plane ({_passPlanes[16]:F2},{_passPlanes[17]:F2},{_passPlanes[18]:F2},{_passPlanes[19]:F1})");
    }

    /// <summary>Is a static slot's own world box in this pass's planes with the route slack? A gob slot or an
    /// unboxed one always is: neither is filed, so the set cannot miss it by position.</summary>
    private static bool BoxInPass(int i)
    {
        if (_oGob[i] != IntPtr.Zero) return true;
        fixed (float* ob = _oBox) fixed (float* pp = _passPlanes)
        {
            float* b = ob + (long)i * 6;
            return !HasBox(b) || AreaGrid.BoxInFrustum(pp, b, RouteSlackUnits);
        }
    }

    /// <summary>The [alloy] clause for the hunt.</summary>
    private static string VisAuditNote()
        => _visAudits == 0 ? "" : $", vischeck {_visCollapses} collapse(s), {_visMisses} miss(es) over {_visAudits} audit(s)";

    /// <summary>The whole order, filtered by nothing: what an enrolment or epoch rebuild does, and the shape
    /// the index-driven path has to end up equal to.</summary>
    private static void BuildVisibleFull()
    {
        int n = 0;
        for (int c = 0; c < AlloyClasses.Count; c++)
        {
            _visStart[c] = n;
            for (int k = _clsStart[c]; k < _clsEnd[c]; k++)
            {
                int i = _order[k];
                if (_oTri[i] != IntPtr.Zero) _vis[n++] = i;
            }
            _visEnd[c] = n;
        }
    }

    /// <summary>LSD radix over <see cref="_orderRank"/>, two 9-bit passes. Stable and allocation-free.</summary>
    private static void SortVisibleByOrder(int n)
    {
        for (int shift = 0; shift < 18; shift += RadixBits)
        {
            Array.Clear(_radix, 0, RadixBuckets);
            for (int k = 0; k < n; k++) _radix[(_orderRank[_vis[k]] >> shift) & RadixMask]++;
            int at = 0;
            for (int b = 0; b < RadixBuckets; b++) { int c = _radix[b]; _radix[b] = at; at += c; }
            for (int k = 0; k < n; k++) _visTmp[_radix[(_orderRank[_vis[k]] >> shift) & RadixMask]++] = _vis[k];
            Array.Copy(_visTmp, _vis, n);
        }
    }

    /// <summary>...and, because <see cref="_order"/>'s most significant radix digit IS the class, the sorted
    /// output is already grouped by it: one scan cuts the per-class ranges, by RANK against the order's own
    /// class ranges. Not by <c>_oCls</c>: a budgeted walk's step rewrites a kept tenant's class in place frames
    /// before the finish re-sorts, and one flipped slot split a run so the cut kept only its tail -- the
    /// sky-only frames.</summary>
    private static void CutVisibleClasses(int n)
    {
        int k = 0;
        for (int c = 0; c < AlloyClasses.Count; c++)
        {
            _visStart[c] = k;
            while (k < n && _orderRank[_vis[k]] < _clsEnd[c]) k++;
            _visEnd[c] = k;
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

    /// <summary>#330 / CX-103: should this pass touch the slot at all? A record only needs rewriting when
    /// (a) there is none to serve (stale epoch, new tenant, a retired texture name), or (b) the part is in this
    /// pass's view, where its identity is re-verified and its alpha, fade and dissolve re-read. A slot failing
    /// both keeps its record, its spheres and its shadow levels exactly as they stand, and is stamped verified
    /// so the shadow side does not read its last build as a fresh move.
    /// <para>The gob-visited term is GONE with the bounded chase: it asked the engine's <c>gobbucket</c> whether
    /// a part could have moved, and the answer is now Alloy's own (<see cref="GobXform.Moved"/>, consulted where
    /// the matrix is composed). A mover the pass cannot see does not need staging -- it needs its record, and
    /// its record is rebuilt the moment it is routed again.</para></summary>
    private static bool RouteWanted(int i, float* sph)
    {
        if (_fullPoll || !_passFrustumOk) return true;
        if (_recGen[i] != _recEpoch || _recEnroll[i] != _oEnrollGen[i] || _recTexStale[i]) return true;
        // #336: no stamp on the rejected slot -- the shadow side reads "moved on its LAST build, and that
        // build was last frame" (SlotRecMoved), so an unvisited slot reads still on its own.
        if (!SphereInPass(i, sph)) { _routeOut++; return false; }
        _routeIn++;
        return true;
    }

    // ================= the visible gobs =================

    /// <summary>CX-104: the gobs the world view is about to render, from Alloy's own enumeration
    /// (<c>Scene+0x130</c>, every gob in the AREA) culled by distance against the camera's far plane. Valid from
    /// the top of <see cref="RspShim.Run"/>, which is what its callers need -- <c>DoorFade</c> integrates a
    /// door's fade before the anchor and <c>GrassField</c> pushes grass from the gobs standing in it.
    /// <para>This REPLACES <c>gobbucket</c>, and it is a superset of it: the engine's list is frustum-culled and
    /// this one is not, because neither caller wants a camera-facing answer (a creature just off-camera bends
    /// the grass it is standing in, and a door behind you still has to stay clickable). The distance cull is
    /// there to keep the list the few hundred entries the callers were sized for rather than the area's whole
    /// population.</para></summary>
    internal static bool VisibleGobs(out IntPtr* items, out int n)
    {
        items = null; n = 0;
        if (_visGobN <= 0) return false;
        items = _visGobs;
        n = _visGobN;
        return true;
    }

    /// <summary>Capacity of the visible-gob list. Past it the list is truncated and the count says so on the
    /// period line; a caller that walks a short list pushes less grass, never the wrong grass.</summary>
    private const int MaxVisGobs = 4096;
    /// <summary>NATIVE, not a managed array: the callers take the raw pointer and walk it, exactly as they
    /// walked the engine's own list, so it may not move under them.</summary>
    private static readonly IntPtr* _visGobs =
        (IntPtr*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(MaxVisGobs, (nuint)sizeof(IntPtr));
    private static int _visGobN, _visGobAll;
    private static bool _visGobTrunc;

    /// <summary>Once per world frame, at the TOP of <see cref="RspShim.Run"/> (engine context): rebuild the
    /// visible-gob list. Allocation-free; one sphere test per gob in the area.</summary>
    internal static void SampleVisibleGobs(IntPtr scene)
    {
        _visGobN = 0; _visGobAll = 0; _visGobTrunc = false;
        if (scene == IntPtr.Zero || !IsWorldScene(scene)) return;
        var sc = new Scene(scene);
        IntPtr* gobs = (IntPtr*)sc.Gobs;
        int count = sc.GobCount;
        if (gobs == null || count <= 0) return;
        _visGobAll = count;
        var cam = new Camera(AlloyCamera.World);
        bool haveCam = !cam.IsNull;
        Vector3 eye = haveCam ? cam.Pos : default;
        float far = haveCam ? cam.Far : 0f;
        for (int i = 0; i < count; i++)
        {
            IntPtr gob = gobs[i];
            if (gob == IntPtr.Zero) continue;
            if (haveCam && far > 0f)
            {
                var g = new Gob(gob);
                // The centre the engine's own BSP visitor @0x481000 measures the LOD distance from: the gob's
                // position plus its LOCAL bbox centre through its orientation.
                Vector3 c = g.Position + Vector3.Transform(g.Centre, (Quaternion)g.Rot);
                float d = (c - eye).Length() - g.Radius;
                if (d > far) continue;
            }
            if (_visGobN >= MaxVisGobs) { _visGobTrunc = true; break; }
            _visGobs[_visGobN++] = gob;
        }
    }

    // ================= per-slot state the route reads =================

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
    /// the parts that need their own identity -- the window that lights up at night, the vine that danglies --
    /// and a plain part list for the static remainder. So a tileset window IS a gob part.
    /// <see cref="EngineLayout.GobTileData"/> identifies that half positively, and it dissolves with the wall
    /// it belongs to. <c>/alloy tiledissolve 0</c> is the vanilla A/B.</para>
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
            // is an ENABLE bit and CxKeyhole owns the whole shape of the hole.
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

    /// <summary>CX-104: the tile fade factor this part draws at -- the camera-occlusion ramp times the
    /// fog-of-war exploration ramp, both from <see cref="TileFadeRamp"/>, which owns them. Read live: the ramp
    /// is a handful of tiles and two float loads, where the per-slot cache this replaced existed only because
    /// the value came out of the engine's fading buckets.
    /// <para>Vanilla draws an unexplored interior tile fogged to black (<c>RenderDrawBucket</c> @0x46d060: fog
    /// colour x f^2, fog end x f); Alloy fades it by alpha instead, at the same 4/s rate. ponytail: alpha, not
    /// fog-to-black -- a per-draw fog scale in the record is the upgrade if the 0,25 s see-through fade-in ever
    /// reads wrong.</para></summary>
    private static float TileFade(int i, IntPtr tri)
    {
        IntPtr owner = _oTile[i];
        float fog = owner == IntPtr.Zero ? 1f : TileFadeRamp.Explored(owner);
        if (fog <= 0f) return 0f;
        IntPtr occluder = _oFadeTile[i];
        return occluder == IntPtr.Zero ? fog : TileFadeRamp.Fade(occluder) * fog;
    }

    /// <summary>CX-103: the slot's model matrix -- COMPOSED, not read. Recomposed when there is no record to
    /// serve, when the record was not built on the previous frame (a slot re-entering the view, or a pass that
    /// skipped it), or when <see cref="GobXform.Moved"/> says the gob's inputs changed. A static placement and
    /// every tile part answer none of those and are served from their own record, which is the whole point:
    /// per-frame cost scales with what moved.
    /// <para><c>alloy.xformcheck</c> adds the one read of <c>vp+0x18</c> that is left, as an audit.
    /// <c>alloy.ownxform 0</c> hands the whole job back -- and shows the fault CX-103 removed, because the
    /// <c>Fault 1</c> placement latch went with it: a gob the engine never placed draws at the area corner.
    /// </para></summary>
    private static Matrix4x4 ChaseModel(int i, float* r, bool stale, bool newTenant, ref StageCounters c)
    {
        if (!GobXform.Armed)
        {
            c.MxChased++;
            return new NodeRef(_oNode[i]).Model;
        }
        bool recompose = stale || newTenant || _recFrame[i] < _frameSeq - 1 || GobXform.Moved(_oGob[i]);
        if (!recompose) { c.MxCached++; return *(Matrix4x4*)r; }
        c.MxChased++;
        Matrix4x4 m = GobXform.Compose(_oTri[i], _oGob[i]);
        if (Renderer.XformCheck.V) GobXform.Check(_oNode[i], _oGob[i], in m);
        return m;
    }

    /// <summary>A re-tenanted slot's caches describe the previous tenant.</summary>
    private static void UnstampChase(int i) { _disGen[i] = 0; _alphaKnown[i] = false; _occFade[i] = 0f; _occHeld[i] = 0f; }

    /// <summary>The [alloy] line's clause.</summary>
    private static string ChaseNote()
    {
        long mxAll = _mxChased + _mxCached, disAll = _disAsked + _disCached;
        long frames = Math.Max(1, Counters._frames);
        string s = $"route {(float)_routeIn / frames:F0}/frame, {(float)_routeOut / frames:F0} off-frustum untouched"
                 + (_visList ? $", visible set rebuilt {_visRebuilds} of {frames} frames from "
                             + $"{AreaGrid.Occupied} cells ({AreaGrid.Filed} indexed, {AreaGrid.Unboxed} unboxed ({_unboxedEmpty} never filled), "
                             + $"{_dynN} on gobs, {_visIndexed / Math.Max(1, _visRebuilds)}/rebuild)"
                             : ", visible set OFF") + VisAuditNote()
                 + $"; matrices {(float)_mxChased / frames:F0}/frame composed of {(float)mxAll / frames:F0}"
                 + $", keyhole {(float)_disAsked / frames:F0}/frame asked of {(float)disAll / frames:F0}"
                 + $", keyhole {(float)_disBoxed / frames:F0}/frame from the cached box"
                 + $"; dissolve refused {(float)_disNoLit / frames:F0} unlit + {(float)_disNoFeat / frames:F0} nokeyhole-feature"
                 + $" + {(float)_disGobUnpierced / frames:F0} gob/frame"
                 + $"; gobs in view {_visGobN}/{_visGobAll}" + (_visGobTrunc ? " TRUNCATED" : "")
                 + "; " + GobXform.Period() + "; " + GobLod.Period()
                 + (_fullPoll ? $"; POLL AUDIT misses (must be 0): keyhole {_pollMissDis} box {_pollMissBox} alpha {_pollMissAlpha}" : "");
        return s;
    }

    private static void ResetChasePeriod()
    {
        _mxChased = _mxCached = _disAsked = _disCached = 0;
        _routeIn = _routeOut = 0; _visRebuilds = 0; _visIndexed = 0;
        _pollMissDis = 0; _pollMissBox = 0; _pollMissAlpha = 0; _disBoxed = 0;
        _disNoLit = _disNoFeat = _disGobUnpierced = 0;
        GobXform.ResetPeriod();
    }

    /// <summary>Once per world frame at the frame anchor, engine context, after the engine's own scene work and
    /// before the first record build: take the frame's scene state. CX-103 replaced three engine-set samples
    /// with one -- which gobs moved -- and CX-105 turns an LOD swap into the re-walk it needs.</summary>
    private static void SampleEngineSets(IntPtr scene)
    {
        GobXform.Tick(scene);
        fixed (ulong* key = _camKey)
            if (PartTable.KeyholeCameraMoved(PartTable.CurrentCamera, key)) _camGen++;
    }

    /// <summary>Off-device: the parts of the route that fail as a creature frozen mid-stride rather than as an
    /// error -- the camera key compare, the visible set's class cut, and the radix sort that keeps the batch
    /// order across an index-driven rebuild.</summary>
    internal static bool ChaseSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.ChaseSelfCheck");
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

        // CX-104: the radix sort restores enrollment order, and the class cut follows it. Four slots handed
        // to the sort in cell order must come back in rank order and split into two contiguous class runs --
        // a sort that lost the order would cut a batch per cell, and a cut that read the wrong class run
        // would draw the blended set in the opaque pass.
        int a = 0, b = 1, c2 = 2, d = 3;
        int[] saveRank = { _orderRank[a], _orderRank[b], _orderRank[c2], _orderRank[d] };
        byte[] saveCls = { _oCls[a], _oCls[b], _oCls[c2], _oCls[d] };
        int[] saveEnd = (int[])_clsEnd.Clone();
        _orderRank[a] = 7; _orderRank[b] = 2; _orderRank[c2] = 5; _orderRank[d] = 300;
        for (int c = 0; c < AlloyClasses.Count; c++) _clsEnd[c] = c < (int)AlloyClass.Blended ? 100 : 400;
        // c2 ranks opaque but a mid-walk step already re-read it as blended: the cut follows the rank
        _oCls[a] = (byte)AlloyClass.Opaque; _oCls[b] = (byte)AlloyClass.Opaque;
        _oCls[c2] = (byte)AlloyClass.Blended; _oCls[d] = (byte)AlloyClass.Blended;
        _vis[0] = d; _vis[1] = a; _vis[2] = c2; _vis[3] = b;
        SortVisibleByOrder(4);
        bool sorted = _vis[0] == b && _vis[1] == c2 && _vis[2] == a && _vis[3] == d;
        check.Expect(sorted, "the visible set comes back in enrollment order, which is the batch key");
        CutVisibleClasses(4);
        check.Expect(_visStart[(int)AlloyClass.Opaque] == 0 && _visEnd[(int)AlloyClass.Opaque] == 3
                     && _visStart[(int)AlloyClass.Blended] == 3 && _visEnd[(int)AlloyClass.Blended] == 4
                     && _visStart[(int)AlloyClass.Water] == _visEnd[(int)AlloyClass.Water],
                     "...and cuts one range per class by the ORDER's class, even for a slot whose live class a budgeted step flipped");
        for (int k = 0; k < 4; k++) { _orderRank[k] = saveRank[k]; _oCls[k] = saveCls[k]; }
        Array.Copy(saveEnd, _clsEnd, saveEnd.Length);
        for (int k = 0; k < AlloyClasses.Count; k++) { _visStart[k] = 0; _visEnd[k] = 0; }
        _visIndexGen++;

        float* wall = stackalloc float[6] { 10f, 20f, 0f, 20f, 20f, 5f };
        float* never = stackalloc float[6];
        float* inverted = stackalloc float[6] { 5f, 5f, 0f, 1f, 6f, 1f };
        check.Expect(HasBox(wall) && !HasBox(never) && !HasBox(inverted),
                     "a flat axis-aligned wall is indexed; a never-filled or inverted box is not");

        log.Info(check.Ok ? "AlloyRenderer.ChaseSelfCheck: passed"
                          : "AlloyRenderer.ChaseSelfCheck: FAILED -- the route would serve a stale record");
        return check.Ok;
    }
}
