using System;
using System.Numerics;
using System.Runtime.InteropServices;
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

    /// <summary>No keyhole camera this pass: the aperture is CLEARED rather than left at last frame's. Every
    /// per-part read of it is already behind <see cref="_khCam"/>, but <see cref="PrepareRouteMemo"/> asks
    /// <see cref="_khOn"/> on its own, and a stale non-zero byte there disarmed the route memo for the rest of
    /// the session with nothing on the line to say why. Pure in the four fields, which is how the self-check
    /// exercises it without a camera to lose.</summary>
    private static void ClearKeyholeCamera()
    { _khCam = false; _khOn = 0; _khCut = 0f; Array.Clear(_khPlane); }

    private static void ReadKeyholeCamera()
    {
        IntPtr cam = PartTable.KeyholeArmed ? PartTable.CurrentCamera : IntPtr.Zero;
        if (cam == IntPtr.Zero) { ClearKeyholeCamera(); return; }
        _khCam = true;
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
        if (full) { _texStalePending = false; BuildVisibleFull(); MarkVisibleSet(); return; }
        int n = 0;
        for (int k = 0; k < _dynN && n < _vis.Length; k++) _vis[n++] = _dynList[k];
        int fromGrid = n;
        fixed (float* p = _passPlanes) n = AreaGrid.Query(p, RouteSlackUnits + VisDriftUnits, _vis, n);
        n = TrimVisible(fromGrid, n);
        _visIndexed += n;
        SortVisibleByOrder(n);
        CutVisibleClasses(n);
        MarkVisibleSet();   // S1-6d: membership is stamped by the build, so the audit can slice
    }

    /// <summary>BW6: keep the candidates whose OWN box survives this pass's planes, not every slot filed in a
    /// surviving cell. A slot is filed under one cell -- the one its box's MIN corner falls in -- and that cell
    /// carries the union of its members' boxes, so a cell that anything reaches yields all of it: the live
    /// route walked 25,6k slots to route 7,9k, and it walks them EVERY frame while the set is reused, where this
    /// test is paid once per rebuild (48-309 of 900 frames live).
    /// <para>Nothing can be lost by it. The cell test passes a cell exactly when SOME member's box could be in
    /// view, since the cell box is their union, so testing each member is strictly tighter with the same slack;
    /// and the slack is the route's own plus the drift the set is rebuilt at, which is why a set reused for
    /// several frames still holds every box the pass's own gate would keep (<see cref="AuditVisible"/> is the
    /// live proof -- <c>vischeck</c> misses must stay 0).</para>
    /// <para>This is also why the FILING stays keyed on the min corner: once every candidate is tested, cell
    /// granularity only costs query time. Filing under every overlapped cell would need the query to dedup
    /// (a slot yielded twice is a draw issued twice), and filing by centre would inflate a different cell
    /// rather than a smaller one.</para></summary>
    /// <summary>Does this slot's <see cref="_oBox"/> bind where its geometry can be -- i.e. may a box test
    /// EXCLUDE it? Three slots it does not bind, and each would vanish at the screen edge if it did:
    /// <list type="bullet">
    /// <item>a slot with no filled box (nothing to test);</item>
    /// <item>a slot whose cull sphere is <see cref="NeverCull"/> -- the exemption exists because the geometry
    /// leaves its bounds (a wind-swayed plant, a roofknm offset), which is a statement about this box too;</item>
    /// <item>a MATRIX slot. <c>_oBox</c> is the engine's own <c>ComputePartBBox</c> box in area space,
    /// UNSCALED, while the cull sphere this is weighed against has been scaled by <c>GobXform.ScaleOf</c> since
    /// !790 -- so for a part whose matrix scales, the box is simply not where the geometry is, and a matrix
    /// part may move its box between frames anyway (see <see cref="Dissolves"/>, which keeps the engine call
    /// for exactly this population).</item>
    /// </list>
    /// <para>What is left unfixed, honestly: <see cref="AreaGrid"/>'s FILING is keyed on the same unscaled box,
    /// so a scaled matrix part can still be missed by the CELL test before the trim ever sees it. This makes
    /// that case show up -- <see cref="BoxInPass"/> refuses to excuse it, so it lands on <c>vischeck</c>'s miss
    /// count rather than in the box-excluded population -- which is the right order: measure it, then fix the
    /// filing if it is ever non-zero.</para></summary>
    private static bool BoxBinds(int i, float* b, float* sph)
        => HasBox(b) && sph[(long)i * 4 + 3] < NeverCull && !_oMx[i];

    private static int TrimVisible(int from, int n)
    {
        int w = from;
        fixed (float* ob = _oBox)
        fixed (float* sp = _dSph)
        fixed (float* pp = _passPlanes)
            for (int k = from; k < n; k++)
            {
                int i = _vis[k];
                float* b = ob + (long)i * 6;
                // An unboundable slot has no box to test and is always visible, exactly as its NeverCull
                // sphere already made it. So is a slot whose SPHERE is NeverCull: that exemption exists
                // because the geometry leaves its bounds (a wind-swayed plant, a roofknm offset), which is a
                // statement about this box too -- the cell union used to give such a part a tile of margin,
                // and taking that away with a box its vertices do not stay inside is how geometry vanishes at
                // the screen edge. A MATRIX slot is exempt too -- see BoxBinds for all three.
                if (!BoxBinds(i, b, sp)
                    || AreaGrid.BoxInFrustum(pp, b, RouteSlackUnits + VisDriftUnits)) _vis[w++] = i;
            }
        _visTrimmed += n - w;
        return w;
    }

    /// <summary>Off-device: the trim keeps what the route can still want. The check is here rather than in
    /// <see cref="AuditVisible"/> because the live audit cannot see a trim loss -- its own reference test uses
    /// the same box predicate -- so what the trim needs is an independent statement of its four rules: a box
    /// inside the planes survives, a box well outside does not, and a slot whose box does not BIND it -- the
    /// sphere exempt from culling, and the matrix slot whose unscaled box the !790 sphere scale left behind --
    /// survives whatever that box says.</summary>
    private static bool TrimSelfCheck(Checker check)
    {
        const int inView = 0, outView = 1, exempt = 2, mx = 3;
        float[] boxWas = new float[24];
        Array.Copy(_oBox, boxWas, 24);
        float[] sphWas = new float[16];
        Array.Copy(_dSph, sphWas, 16);
        float[] planesWas = (float[])_passPlanes.Clone();
        int[] visWas = { _vis[0], _vis[1], _vis[2], _vis[3] };
        bool mxWas = _oMx[mx];

        // Six planes keeping x >= 0 (five degenerate ones that keep everything), as AreaGrid's own check does.
        for (int f = 0; f < 6; f++)
        { _passPlanes[f * 4] = 0f; _passPlanes[f * 4 + 1] = 0f; _passPlanes[f * 4 + 2] = 0f; _passPlanes[f * 4 + 3] = 1f; }
        _passPlanes[0] = 1f; _passPlanes[3] = 0f;
        void Box(int i, float x0, float x1)
        {
            _oBox[i * 6] = x0; _oBox[i * 6 + 1] = 0f; _oBox[i * 6 + 2] = 0f;
            _oBox[i * 6 + 3] = x1; _oBox[i * 6 + 4] = 1f; _oBox[i * 6 + 5] = 1f;
        }
        Box(inView, 10f, 20f);
        Box(outView, -500f, -490f);
        Box(exempt, -500f, -490f);
        Box(mx, -500f, -490f);
        _dSph[inView * 4 + 3] = 1f; _dSph[outView * 4 + 3] = 1f; _dSph[exempt * 4 + 3] = NeverCull;
        // A matrix slot with an ordinary, cullable sphere: the only thing that keeps it is its class.
        _dSph[mx * 4 + 3] = 1f; _oMx[mx] = true;
        _vis[0] = inView; _vis[1] = outView; _vis[2] = exempt; _vis[3] = mx;
        int n = TrimVisible(0, 4);
        bool ok = n == 3 && _vis[0] == inView && _vis[1] == exempt && _vis[2] == mx;
        check.Expect(ok, "the candidate trim drops a box in view, keeps one behind the planes, or drops a slot "
                       + "whose box does not bind it (a NeverCull sphere, or a matrix slot whose box is unscaled)");
        // ...and the audit agrees, so such a slot's absence reads as a MISS rather than as box-excluded.
        check.Expect(!BoxInPass(outView) && BoxInPass(exempt) && BoxInPass(mx),
                     "the audit's reference box test disagrees with the trim, so a scaled matrix part the cell "
                     + "test dropped would be excused instead of counted");
        ok &= !BoxInPass(outView) && BoxInPass(exempt) && BoxInPass(mx);

        Array.Copy(boxWas, _oBox, 24);
        Array.Copy(sphWas, _dSph, 16);
        Array.Copy(planesWas, _passPlanes, 24);
        for (int k = 0; k < 4; k++) _vis[k] = visWas[k];
        _oMx[mx] = mxWas;
        return ok;
    }
    /// <summary>Candidates the box test dropped that a cell would have yielded, over the period.</summary>
    private static long _visTrimmed;

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
    private static long _visCollapses, _visSlices, _visMisses, _visBoxOnly;
    private const int VisLogCap = 6;

    /// <summary>S1-6d: the set's MEMBERSHIP mark, stamped once per set BUILD rather than once per audit -- a
    /// slot is in the current set exactly while <see cref="_visMark"/> holds <see cref="_visMarkGen"/>. The
    /// audit used to mark the whole set itself, which is why it could only run as one big walk; the build
    /// already walks every member, so the mark is free there and the audit can ask about any slot on any
    /// frame. Gen 0 means no set has been built and nothing may be audited (the array's own default would
    /// otherwise read as "every slot is a member").</summary>
    private static readonly int[] _visMark = new int[MaxOwned];
    private static int _visMarkGen, _visTotal;

    private static void MarkVisibleSet()
    {
        int gen = ++_visMarkGen, total = 0;
        for (int c = 0; c < AlloyClasses.Count; c++)
            for (int k = _visStart[c]; k < _visEnd[c]; k++) { _visMark[_vis[k]] = gen; total++; }
        _visTotal = total;
    }

    /// <summary>S1-6d: how many frames one full sweep of the table takes, and where the next slice starts.
    /// Power of two (the old audit's own 1-in-64 cadence), so the coverage per 64 frames is what it always
    /// was -- 37,7k slots once -- and the 37,7k-slot SPIKE every 64th frame is gone.</summary>
    private const int VisAuditSlices = 64;
    private static int _visSliceAt;

    /// <summary>The slice to audit this frame, and the cursor advanced past it. Pure in its arguments, so the
    /// coverage claim -- every slot of the table exactly once per cycle, no gap and no slot visited twice -- is
    /// a self-check rather than a comment. A count that is not a multiple of
    /// <see cref="VisAuditSlices"/> rounds the step UP, so a cycle finishes in at most that many frames.</summary>
    private static void AuditSlice(int count, ref int at, out int from, out int to)
    {
        int step = (count + VisAuditSlices - 1) / VisAuditSlices;
        if (at >= count || at < 0) at = 0;
        from = at;
        to = Math.Min(count, from + step);
        at = to;
    }

    /// <summary>After the opaque/cutout route: did this pass's visible set collapse against the running mean,
    /// and does the set miss any live slot the pass's own sphere test would route? A miss is a slot the full
    /// walk draws and the visible set does not: the flicker.
    /// <para>S1-6d: the walk is a rolling 1/<see cref="VisAuditSlices"/> SLICE of the table per PASS instead of
    /// the whole table one frame in 64 -- the same slots per 64 passes, and no p99 spike. (A frame with two
    /// culled passes therefore covers the table twice as fast, which is coverage, not cost.) A COLLAPSE still
    /// walks the whole table at once: it is already an anomalous frame and the question it asks ("which slot is
    /// missing RIGHT NOW") cannot be answered by a slice.</para>
    /// <para><b>What a slice compares against.</b> This frame's set and this frame's planes, both live: every
    /// term in the test below is read now, so a slice is a complete and correct statement about the slots in
    /// it, and a set rebuilt mid-cycle cannot make an earlier slice wrong -- a slice is never carried across
    /// frames. What a rebuild changes is COVERAGE, not soundness: over a cycle the slots are checked against
    /// successive sets rather than one, so the property lost against the old audit is "all 37,7k slots against
    /// ONE set". That property only ever held for the 1 frame in 64 it ran on, and the set it held for was
    /// usually several frames old (rebuilt 48-309 of 900 frames live), while the slice audit now covers the
    /// other 63 frames the old one said nothing about -- including, in particular, the frames right after a
    /// rebuild.</para></summary>
    private static void AuditVisible(int routed, float* sph)
    {
        // A full pass has no set to be missing from -- and it must NOT touch the cursor: a probe pass that
        // walks the whole order every frame would otherwise rewind it, and the world pass would audit the
        // first slice for ever while the rest of the table went unchecked.
        if (_visFull) return;
        bool collapse = _routedAvg > 400 && routed * 4 < _routedAvg;
        _routedAvg = _routedAvg == 0 ? routed : (_routedAvg * 7 + routed) / 8;
        if (collapse) _visCollapses++;
        if (_visMarkGen == 0 || _orderCount <= 0) return;   // no set has been built: nothing to be missing from
        int gen = _visMarkGen, total = _visTotal;
        int from, to;
        if (collapse) { from = 0; to = _orderCount; }
        else { AuditSlice(_orderCount, ref _visSliceAt, out from, out to); _visSlices++; }
        int missed = 0, first = -1;
        for (int k = from; k < to; k++)
        {
            int i = _order[k];
            if (_oTri[i] == IntPtr.Zero || _gate[i].RecGen != _recEpoch || _visMark[i] == gen) continue;
            if (!SphereInPass(i, sph)) continue;
            // BW6: the set now excludes a candidate by its OWN box rather than by its cell's union, so the
            // population "the sphere would route it and the box says it is not in view" is what the trim
            // removed. It is not a miss -- the part's geometry is inside a box that BINDS it, which is the one
            // thing BoxInPass tests (BoxBinds: never a matrix slot, whose box the sphere's !790 scale has left
            // behind, and never a NeverCull or unboxed one) -- but it is the number that moves if the trim ever
            // drops more than it should, which is why it is counted rather than skipped silently. A slot the
            // trim dropped whose box the pass's own test DOES keep still lands below as a real miss: the trim's
            // slack (route + drift) is looser than BoxInPass's, and a plane cannot have moved further than the
            // drift without rebuilding the set.
            if (!BoxInPass(i)) { _visBoxOnly++; continue; }
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
        _log?.Always($"AlloyRenderer: [vischeck] {(collapse ? "COLLAPSE (whole table)" : $"miss (slice {from}..{to} of {_orderCount})")} frame {_frameSeq}: routed {routed} vs mean "
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
        fixed (float* ob = _oBox) fixed (float* pp = _passPlanes) fixed (float* sp = _dSph)
        {
            float* b = ob + (long)i * 6;
            // Exactly the trim's own predicate: a box that does not bind cannot excuse the slot's absence, so
            // such a slot reads as a real miss rather than as box-excluded.
            return !BoxBinds(i, b, sp) || AreaGrid.BoxInFrustum(pp, b, RouteSlackUnits);
        }
    }

    /// <summary>The hunt's clause. Collapses and misses are LIFETIME totals (a miss must read 0 for the
    /// session, not for the period); the slice count and the box-excluded population are this period's, and the
    /// latter is scaled to one CYCLE so it stays the number the whole-table audit used to print in one go.
    /// <para>Printed when EITHER a slice or a collapse has happened: a period made of nothing but collapse
    /// frames does no slices, and that is the last period that should go quiet.</para></summary>
    private static string VisAuditNote()
        => _visSlices == 0 && _visCollapses == 0 ? "" : $", vischeck {_visCollapses} collapse(s) and {_visMisses} miss(es) lifetime "
                                  + $"(must be 0) over {_visSlices} slice(s) of 1/{VisAuditSlices} of the table "
                                  + $"this period ({_visBoxOnly * VisAuditSlices / Math.Max(1, _visSlices)} box-excluded/cycle)";

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

    // ================= BW7: the gate row =================

    /// <summary>BW7: every per-slot record stamp the route gate reads, in ONE row. They were eight separate
    /// columns, seven of them on the gate's own path, touched at a scattered ownership index for every visited
    /// candidate -- seven cache lines per slot where the whole question fits in one -- and after BW1 and BW6
    /// that gate IS what the walk costs: the live median visits 23,063 candidates to route 5,650, and a
    /// replayed frozen slot asks nothing else (<see cref="RouteMemo"/>).
    /// <para>Padded to a 32-byte STRIDE, so a row spans at most two lines and two consecutive rows share one
    /// -- not an alignment guarantee: a managed array is 8-byte aligned, so where the first row lands is the
    /// runtime's business and only the stride is ours. It also narrows the false sharing the parallel stage
    /// walk already had: a worker staging slot <c>i</c> dirtied one line in each of four columns (sixteen
    /// neighbours apart), and now dirties one line holding one neighbour.</para>
    /// <para>Writers, all of which must keep the row whole: the record build (<c>StagePart</c>: every stamp),
    /// <see cref="UnstampRecord"/> and <see cref="UnstampChase"/> (a re-tenanted or drifted slot),
    /// the enrolment (<see cref="EnrollGen"/>), the texture retire/swap scans in
    /// <see cref="NoteOwnedNameRetired"/> and friends (<see cref="TexStale"/>), the static freeze
    /// (<see cref="FrozenAt"/>) and the wall-fade verdict (<see cref="OccFade"/>).</para></summary>
    [StructLayout(LayoutKind.Sequential, Size = GateRowBytes)]
    private struct RouteGate
    {
        /// <summary>#242: the epoch this slot's record was built under, and the frame it was last verified on.
        /// A slot whose epoch is stale rebuilds unconditionally; one already verified this frame is free for
        /// every pass after the first. Epoch 0 is "never built", so the row needs no initialisation.</summary>
        public int RecGen, RecFrame;
        /// <summary>#302: the slot TENANCY generation -- which enrollment owns this slot
        /// (<see cref="EnrollGen"/>), and which enrollment the record in it was built for
        /// (<see cref="RecEnroll"/>). NOT the record epoch, and the distinction is a live-diagnosed flicker:
        /// the epoch is bumped by every wholesale invalidation -- enrollment, but ALSO an animated texture
        /// retiring a copy (<c>Drain -&gt; DropHandle</c>, roughly once a second in a torch-lit area) and a
        /// <c>/alloy</c> dial -- and those non-enrollment bumps land AFTER the frame's record build, so the next
        /// shadow bracket would see the whole table epoch-stale and refuse it: every shadow gone for one frame,
        /// once a second. What the bake actually must not trust is a record built for a DIFFERENT PART in the
        /// same slot, and only an enrollment can change that; so tenancy gets its own generation, bumped in
        /// Enroll alone and restamped by the same colour pass that rebuilds the records.
        /// <para><c>_enrollGen</c> starts at 1 so a record never built (stamp 0) disagrees with every real
        /// enrollment.</para></summary>
        public int RecEnroll, EnrollGen;
        /// <summary>BW1: the frame this slot was last FROZEN by the static path (<see cref="StaticFrozen"/>),
        /// i.e. the frame Alloy last decided nothing about it can move. Stamped where the freeze happens, which
        /// is after the route walk -- so it is last frame's answer, which is exactly what a memo needs.
        /// <para>-1, not 0: frames 0 and 1 would otherwise read as "frozen last frame" for every slot. Nothing
        /// could come of it (no slot has a record to serve that early, which the memo also tests), but a gate
        /// that is only right because a second gate catches it is not a gate. <see cref="ResetGate"/> is what
        /// seeds it, since a struct array cannot carry a non-zero default.</para></summary>
        public int FrozenAt;
        /// <summary>#537: how far toward the floor the part is faded, 0 = not at all. Zero is the row's
        /// default, so a fresh slot needs no seeding and a re-tenanted one is cleared in
        /// <see cref="UnstampChase"/>.</summary>
        public float OccFade;
        /// <summary>#537: seconds of <c>OccHold</c> left since the last hit; cleared with the fade.</summary>
        public float OccHeld;
        /// <summary>The record's owned texture name RETIRED under it (the transcode drip swapping a raw upload
        /// for its compressed copy): the engine pointer is unchanged, so the pointer compare alone would keep
        /// the record sampling a handle the drain has since dropped. Routed and re-resolved regardless of the
        /// frustum, then cleared. Set by <see cref="NoteOwnedNameRetired"/> from a walk over the record tids --
        /// per slot on purpose: a global generation re-staged the whole world on every drip frame (Restage
        /// 5,7ms in hitch frames against 0,8 mean, measured).</summary>
        public bool TexStale;
    }
    /// <summary>29 bytes of fields, padded to a 32-byte stride. 5,00 MiB over <see cref="MaxOwned"/> against
    /// the 4,53 MiB the eight columns held: 0,47 MiB of padding, bought for the stride. Declared as the
    /// struct's <c>Size</c>, so a field appended past it cannot load; raise it with the field and the loud
    /// line in <see cref="ChaseSelfCheck"/> takes over.
    /// not-an-offset: a size, not an engine layout.</summary>
    private const int GateRowBytes = 32;
    /// <summary>The gate row per ownership slot. <see cref="ResetGate"/> seeds the one field whose default is
    /// not zero.</summary>
    private static readonly RouteGate[] _gate = ResetGate(new RouteGate[MaxOwned]);
    private static RouteGate[] ResetGate(RouteGate[] a)
    { for (int i = 0; i < a.Length; i++) a[i].FrozenAt = -1; return a; }

    /// <summary>#330 / CX-103: should this pass touch the slot at all? A record only needs rewriting when
    /// (a) there is none to serve (stale epoch, new tenant, a retired texture name), or (b) the part is in this
    /// pass's view, where its identity is re-verified and its alpha, fade and dissolve re-read. A slot failing
    /// both keeps its record, its spheres and its shadow levels exactly as they stand, and is stamped verified
    /// so the shadow side does not read its last build as a fresh move.
    /// <para>The gob-visited term is GONE with the bounded chase: it asked the engine's <c>gobbucket</c> whether
    /// a part could have moved, and the answer is now Alloy's own (<see cref="GobXform.Moved"/>, consulted where
    /// the matrix is composed). A mover the pass cannot see does not need staging -- it needs its record, and
    /// its record is rebuilt the moment it is routed again.</para>
    /// <para>BW7: the four record terms are one <see cref="RouteGate"/> row, so the gate is one cache line at a
    /// scattered <c>i</c> rather than four (<see cref="_gate"/> names the whole move and what it measured).</para>
    /// <para>BW5, decided against and recorded here rather than left as a marker: the walk STAYS serial (ADR
    /// 0022). Three numbers settle it. (1) What is left to parallelise is the full-path minority -- with BW1
    /// replaying 15 of every 16 frozen gobless slots and BW6 trimming the candidates to the boxes actually in
    /// view, the live median (5,650 routed of 23,063 visited, <c>Scan</c> p50 1,90 ms on the mean-frame row
    /// and 2,3 ms on the hitch row, over 238 periods) derives to ~1,3k slots still
    /// deriving a verdict and ~7k paying this gate: the gate, not the derivation, is the remaining cost, and
    /// BW7 above is what that asks for. (2) The routing walk is the pool's WAKE SHADOW:
    /// <see cref="StagePool.Prewake"/> fires at the top of the build precisely so the workers are spinning by
    /// the time <c>StagePool.Run</c> publishes the stage, and StagePool's own measurement is that an unhidden
    /// wake can cost as much as the job. Making this walk the first pool job would either pay that wake itself
    /// or move Prewake to the frame anchor, which is the case StagePool measured as four spinning cores per
    /// frame. (3) Exactness: phase A would have to defer <see cref="Revoke"/>, the <c>[dumpframe]</c> watch and
    /// every shared counter, and phase B would still walk every candidate to rebuild <c>_pass</c>/<c>_blend</c>
    /// in order -- so the ceiling is a ~0,3 ms saving on a 14 ms frame, bought with a second pool run per pass
    /// and a new class of nondeterminism. Revisit only if a measured <c>Scan</c> stays above a millisecond with
    /// the memo on and the gate packed.</para></summary>
    private static bool RouteWanted(int i, float* sph)
    {
        if (_fullPoll || !_passFrustumOk) return true;
        // BW7: one address, one bounds check, one line -- the four record terms are one row.
        ref RouteGate g = ref _gate[i];
        if (g.RecGen != _recEpoch || g.RecEnroll != g.EnrollGen || g.TexStale) return true;
        // #336: no stamp on the rejected slot -- the shadow side reads "moved on its LAST build, and that
        // build was last frame" (SlotRecMoved), so an unvisited slot reads still on its own.
        if (!SphereInPass(i, sph)) { _routeOut++; return false; }
        _routeIn++;
        return true;
    }

    // ================= BW1: the frozen slot's route verdict =================

    /// <summary>Is the route memo armed this pass? Every term is global: the freeze itself
    /// (<c>gpu.static</c>, which <c>/alloy poll 1</c> stands down), the ramp being ALLOY's (under
    /// <c>/buckets tilefade 0</c>, or a ramp that did not resolve, the factors come off the engine's own
    /// <c>TileData</c> and the generation below says nothing about them -- <see cref="TileFadeRamp.Tick"/>
    /// returns before it can move), a camera with no keyhole aperture open (the only way a still gobless part
    /// starts dissolving), a tile-fade generation that has not moved, and no <c>[dumpframe]</c> watch armed (the
    /// memo skips the per-slot watch lines, so a watch takes the full path).
    /// <para>The aperture term is "no camera, or a camera with its aperture shut", and both halves are
    /// literally "nothing dissolves": <c>_khCam</c> is false exactly when
    /// <see cref="PartTable.KeyholeDissolves"/> returns false for every part by construction (no armed symbol,
    /// no camera slot, or no current camera), and <see cref="Dissolves"/> refuses every gob part on the same
    /// byte. It is spelled out rather than left to <c>_khOn</c> alone because a lost camera used to leave
    /// <c>_khOn</c> holding its last value, which disarmed the memo for the rest of the session
    /// (<c>0/frame replayed frozen</c>, no reason on the line); <see cref="ReadKeyholeCamera"/> now clears
    /// it, and this reads the same either way.</para></summary>
    private static bool _routeMemoOn;
    /// <summary>The tile-fade generation, latched ONCE per frame together with the frame it was read on. Per
    /// PASS would consume a move: a capture or probe pass runs the frame path again under the same
    /// <see cref="_frameSeq"/>, and a slot that pass did not route would then be replayed by the next pass
    /// against a fade that moved this frame.</summary>
    private static int _routeTileGen, _routeTileFrame = -1;
    private static bool _routeTileHeld;
    private static long _routeMemoed, _routeAudited, _routeMisses;
    private static int _routeLogged;
    private const int RouteLogCap = 6;

    /// <summary>Per pass, before the walk: may a frozen slot's route verdict be replayed at all?</summary>
    private static void PrepareRouteMemo()
    {
        if (_routeTileFrame != _frameSeq)
        {
            _routeTileFrame = _frameSeq;
            _routeTileHeld = TileFadeRamp.Gen == _routeTileGen;
            _routeTileGen = TileFadeRamp.Gen;
        }
        _routeMemoOn = GpuStatic.On && TileFadeRamp.Owned && (!_khCam || _khOn == 0) && _routeTileHeld
                       && AlloyCensus._watchEntry < 0;
    }

    private const int MemoFull = 0, MemoReplayed = 1, MemoAudit = 2;

    /// <summary>BW1: replay a frozen slot's route verdict instead of re-deriving it. A slot the static path
    /// froze last frame was REFUSED membership unless its own alpha was 1, its tile was not fading, it was not
    /// dissolving and its record was current (<c>StaticShape</c>) -- so last frame's verdict is known exactly:
    /// class <c>_oCls[i]</c>, alpha 1, no dissolve. The walk therefore does not have to re-derive
    /// <see cref="TileFade"/>, <c>PartAlphaCached</c>, <c>GobAlphaMul</c>, <see cref="OccludeVerdict"/>,
    /// <see cref="Dissolves"/> or the cadenced identity compares to reach it.
    /// <para>What still runs, per slot: the <c>Part::~Part</c> liveness verdict (#145 -- every dereference
    /// downstream sits behind it, and the staging walk still reads the part on its colour frames), the record
    /// terms <see cref="RouteWanted"/> gates on, the pass's own SPHERE test, which is camera-dependent and
    /// cannot be memoised, and -- one frame in <see cref="ColourCadence"/> per slot -- the whole derivation, as
    /// the audit.</para>
    /// <para>What makes each skipped term sound. <b>Tile fade</b>: the registry bumps
    /// <see cref="TileFadeRamp.Gen"/> whenever a factor moves, and the memo is disarmed for that frame.
    /// <b>The part's own alpha</b>: with <c>anim.owned</c> the controller runtime is its only writer and it can
    /// only reach a part inside a gob's part tree, which a GOBLESS slot is not -- and a write is marked anyway
    /// (BW3), which stands the slot's memo down and forces the read. <b>The gob folds and the occlusion
    /// fade</b>: both need a gob, and only gobless slots are eligible, so a hit cannot tag one and the folds
    /// are the identity. <b>The dissolve</b>: with no keyhole aperture open the verdict is false for every
    /// part, and the aperture is a global this pass already read. <b>Identity</b>: the three field compares are
    /// an AUDIT on a cadence, not a gate (see <see cref="IdentityOk"/>), and the audit below re-runs them.</para>
    /// <para>Returns <see cref="MemoReplayed"/> when the slot is handled here, <see cref="MemoAudit"/> when it
    /// is eligible but this frame re-derives it to check the memo, and <see cref="MemoFull"/> otherwise.</para>
    /// <para>BW8: a replayed slot takes none of <see cref="FrameZones"/>'s <c>Route</c>/<c>Ident</c>/<c>Fade</c>
    /// samples, which were 2 timestamps per sampled visit at <see cref="ChaseEvery"/> and are now paid by the
    /// full-path population alone. Those three rows therefore read "per slot the walk re-derived"; the
    /// <c>Scan</c> zone around the whole walk is unchanged and is still the whole truth, and
    /// <c>replayed frozen</c> on the period line is the population they no longer cover.</para></summary>
    private static int RouteMemo(int i, float* sph, ref int n)
    {
        // BW7: every term but the part pointer, its entry and the sphere comes off ONE row (one cache line for
        // the six reads below), which is what a replayed slot's whole visit costs.
        ref RouteGate g = ref _gate[i];
        if (!_routeMemoOn || g.FrozenAt < _frameSeq - 1 || _oGob[i] != IntPtr.Zero) return MemoFull;
        if (g.OccFade != 0f || g.OccHeld > 0f) return MemoFull;
        IntPtr tri = _oTri[i];
        if (tri == IntPtr.Zero || !PartTable.EntryLive(_oEntry[i], tri)) return MemoFull;
        if (g.RecGen != _recEpoch || g.RecEnroll != g.EnrollGen || g.TexStale) return MemoFull;
        // BW3: the controller runtime moved this part's alpha. Consume the mark and force the read -- clearing
        // _alphaKnown is what makes PartAlphaCached poll on a slot that is otherwise on the colour cadence.
        if (PartTable.AlphaDirty(_oEntry[i]))
        { PartTable.ClearAlphaDirty(_oEntry[i]); _alphaKnown[i] = false; return MemoFull; }
        // The slot's own COLOUR frame is its audit frame, and the two have to be the same frame. Every cadence
        // the full path carries is keyed on this residue -- PartAlphaCached's poll, IdentityOk's three field
        // compares, StagePart's colour reads -- and the freeze's own recheck stagger (1 in 16, the same sum)
        // means the memo's full-path frame would otherwise land on residue 1 and never on 0: a frozen slot
        // would replay its alpha and its identity for ever, so an input this memo does not model (the ENGINE's
        // controller under `anim.owned 0`, a writer the #336 store scan could have missed) would never be seen.
        // Re-deriving here restores all of them AND gives every eligible slot an audit every ColourCadence
        // frames, at every residue, for the price of a walk the cadence was always going to pay.
        if (((i + _frameSeq) & (ColourCadence - 1)) == 0) { _routeAudited++; return MemoAudit; }
        if (_passFrustumOk && !SphereInPass(i, sph)) { _routeOut++; return MemoReplayed; }
        _routeIn++; _routeMemoed++;
        _pass[n] = i; _passCls[n] = _oCls[i]; _passAlpha[n] = 1f; _passDis[n] = false; n++;
        return MemoReplayed;
    }

    /// <summary>The audit found a slot whose live verdict is not the one the memo would have replayed: the
    /// memo's premise is wrong somewhere. Counted on the period line (which must read 0) and named for the
    /// first few, because one term being wrong reads as a part that will not fade rather than as an error.</summary>
    private static void RouteMemoMiss(int i, float alpha, bool dis, string why)
    {
        _routeMisses++;
        if (_routeLogged >= RouteLogCap) return;
        _routeLogged++;
        _log?.Always($"AlloyRenderer: [routecheck] frame {_frameSeq} slot {i} cls {_oCls[i]} would have been "
                   + $"replayed as opaque at alpha 1, but {why} (alpha {alpha:F3}, dissolve {dis}, fading "
                   + $"{_oFading[i]}, tile fade gen {TileFadeRamp.Gen}) -- the route memo's premise is wrong");
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
    /// <summary>GS1: the scene <see cref="VisibleGobsFrom"/> declared the list filled FOR, or Zero. The frame's
    /// one gob sweep (<see cref="ScenePrep"/>'s in-view pass) cuts this list in passing, so the sampler below
    /// has nothing left to walk. Keyed on the scene rather than latched as a bool so a headless check cannot
    /// leave a real frame believing its list was filled -- Zero is never consumed.</summary>
    private static IntPtr _visGobFilled;
    /// <summary>One frame in this many re-walks the array and compares, because the fused verdict is only as
    /// good as the claim that nothing between the two sweeps moves a gob. A power of two.</summary>
    private const int VisGobAuditEvery = 64;
    private static int _visGobAuditTick;
    private static long _visGobAudits, _visGobAuditMiss;
    private static bool _visGobAuditSaid;
    /// <summary>Audits skipped because the list was truncated, and whether that has been said once.</summary>
    private static long _visGobTruncSkips;
    private static bool _visGobTruncSaid;

    /// <summary>GS1: the far-plane test the list is cut on, for ONE gob. <see cref="ScenePrep"/>'s in-view
    /// sweep applies it as it goes -- it has the gob in cache for its own frustum test -- and
    /// <see cref="SampleVisibleGobs"/> applies the same one on a frame that sweep did not run. ONE
    /// implementation, so the two cannot drift, and <see cref="AuditVisibleGobs"/> says so on a cadence.
    /// <para><b>No scale factor, and that is the answer to !790's open question, not an omission.</b>
    /// <see cref="EngineLayout.GobCentre"/> and <see cref="EngineLayout.GobRadius"/> are ALREADY scaled:
    /// <c>Gob::GetMinimumSphere</c> @0x5370a0 is their only writer after the constructor (the ctor leaves radius
    /// 1 and the centre 0, <c>src/Gob.c:2651,2809</c>), and it ends with two separate <c>vtbl+0x2d8</c>
    /// (<c>GetAdjustedScale</c>) calls -- one multiplying the centre triple <c>+0x17c/+0x180/+0x184</c>, one
    /// multiplying the radius <c>+0x178</c> (<c>src/Gob.c:2371-2376</c>). So the two fields this reads are the
    /// gob's sphere WITH its visual transform in it, and multiplying again would make it <c>s^2</c>. A sphere
    /// that grows as the square is not a tighter cull, it is a different bug.</para>
    /// <para>The position and orientation stay the RAW fields, which is what this test was written against
    /// (the BSP visitor @0x481000 measures its LOD distance from those).</para>
    /// <para><paramref name="far"/> at or below zero means "no camera answer", which admits every gob --
    /// exactly what the standalone walk did with no <c>CurrentCamera</c>.</para></summary>
    internal static bool GobWithinFar(IntPtr gob, in Vector3 eye, float far)
    {
        if (!(far > 0f)) return true;
        var g = new Gob(gob);
        // The centre the engine's own BSP visitor @0x481000 measures the LOD distance from: the gob's
        // position plus its bbox centre offset through its orientation. Both terms carry the gob's scale
        // already -- see the summary; this is the engine's own sphere, read, not rebuilt.
        Vector3 c = g.Position + Vector3.Transform(g.Centre, (Quaternion)g.Rot);
        return (c - eye).Length() - g.Radius <= far;
    }

    /// <summary>GS1: the frame's gob sweep is about to offer this list's members, out of
    /// <paramref name="all"/> gobs in <paramref name="scene"/>. Must be followed by
    /// <see cref="OfferVisibleGob"/> in scene order, which is what lets the audit compare positionally.</summary>
    internal static void VisibleGobsFrom(IntPtr scene, int all)
    {
        _visGobN = 0; _visGobAll = all; _visGobTrunc = false; _visGobFilled = scene;
    }

    /// <summary>GS1: one gob that passed <see cref="GobWithinFar"/>, in scene order.</summary>
    internal static void OfferVisibleGob(IntPtr gob)
    {
        if (_visGobN >= MaxVisGobs) { _visGobTrunc = true; return; }
        _visGobs[_visGobN++] = gob;
    }

    /// <summary>GS1: has the frame's gob sweep declared this list filled for <paramref name="scene"/>? The
    /// self-check's question; <see cref="SampleVisibleGobs"/> asks the same one and CONSUMES the answer.</summary>
    internal static bool VisibleGobsFilledFor(IntPtr scene) => _visGobFilled == scene && scene != IntPtr.Zero;

    /// <summary>Once per world frame, at the TOP of <see cref="RspShim.Run"/> (engine context): rebuild the
    /// visible-gob list. Allocation-free; one sphere test per gob in the area.
    /// <para>GS1: on the armed path there is nothing to do -- <see cref="ScenePrep"/>'s in-view sweep, one
    /// call earlier over the same array, cut the list as it went, and this walk over 728 gobs (a quaternion
    /// rotate and four field loads each) was the same answer a second time. It still runs whenever that sweep
    /// did not: <c>alloy.skipbuckets</c> below 3, a <c>/buckets</c> A/B or a validator re-arming the engine's
    /// own walk, an unresolved symbol, or no <c>CurrentCamera</c> at the top of the frame.</para></summary>
    internal static void SampleVisibleGobs(IntPtr scene)
    {
        bool fused = VisibleGobsFilledFor(scene);
        _visGobFilled = IntPtr.Zero;
        if (fused) { AuditVisibleGobs(scene); return; }
        _visGobN = 0; _visGobAll = 0; _visGobTrunc = false;
        WalkVisibleGobs(scene, audit: false);
    }

    /// <summary>The standalone walk. <paramref name="audit"/> false FILLS the list; true re-derives every
    /// verdict live and compares against the list the fused sweep left -- positionally, because both emit in
    /// scene order, so a pointer-for-pointer match over the same length is the whole equivalence.</summary>
    private static void WalkVisibleGobs(IntPtr scene, bool audit)
    {
        if (scene == IntPtr.Zero || !IsWorldScene(scene)) return;
        var sc = new Scene(scene);
        IntPtr* gobs = (IntPtr*)sc.Gobs;
        int count = sc.GobCount;
        if (gobs == null || count <= 0) return;
        var cam = new Camera(AlloyCamera.World);
        bool haveCam = !cam.IsNull;
        Vector3 eye = haveCam ? cam.Pos : default;
        float far = haveCam ? cam.Far : 0f;
        if (!audit) _visGobAll = count;
        int k = 0;
        bool miss = audit && count != _visGobAll;
        for (int i = 0; i < count && !miss; i++)
        {
            IntPtr gob = gobs[i];
            if (gob == IntPtr.Zero) continue;
            if (!GobWithinFar(gob, eye, far)) continue;
            if (!audit) { OfferVisibleGob(gob); continue; }
            if (k >= _visGobN || _visGobs[k] != gob) miss = true; else k++;
        }
        if (!audit) return;
        _visGobAudits++;
        if (!miss && k == _visGobN) return;
        _visGobAuditMiss++;
        if (_visGobAuditSaid) return;
        _visGobAuditSaid = true;
        _log?.Always($"AlloyRenderer: [visgobs] the fused visible-gob list ({_visGobN} of {_visGobAll}) is not "
                   + $"what a live re-walk finds ({k} matched of {count} gob(s)) -- the gob array or a gob's "
                   + "placement changed between ScenePrep's in-view sweep and this call, INSIDE one "
                   + "Scene::Render, so DoorFade and GrassField are reading a list for a scene that has moved "
                   + "under them");
    }

    /// <summary>One frame in <see cref="VisGobAuditEvery"/>: the fused list, re-derived. Skipped on a truncated
    /// list, which is not a set the positional compare can speak about -- and said once when that happens,
    /// because a chronically truncated area is one the fusion is never audited in at all, which is exactly the
    /// kind of silence CX-45 forbids.
    /// <para><b>What it can and cannot catch.</b> It re-walks <c>Scene+0x130</c> and re-derives every verdict at
    /// the sampler's own position in the frame, so it catches the array changing length, a gob appearing or
    /// disappearing, and a placement moving far enough to cross the far plane -- which is the whole of what the
    /// fusion assumes does not happen between the two points. It cannot see a change that happened and was
    /// undone in between, and it says nothing about the 63 frames it did not run on.</para></summary>
    private static void AuditVisibleGobs(IntPtr scene)
    {
        if ((++_visGobAuditTick & (VisGobAuditEvery - 1)) != 0) return;
        if (_visGobTrunc)
        {
            _visGobTruncSkips++;
            if (!_visGobTruncSaid)
            {
                _visGobTruncSaid = true;
                _log?.Always($"AlloyRenderer: [visgobs] the visible-gob list is TRUNCATED at {MaxVisGobs} of "
                           + $"{_visGobAll} gob(s), so the fused sweep cannot be audited here -- while this area "
                           + "stays this crowded the list DoorFade and GrassField read is unchecked (the skip "
                           + "count is on the [alloy] line)");
            }
            return;
        }
        WalkVisibleGobs(scene, audit: true);
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
        if (!TileFadeRamp.Owned)
        {
            float own = owner == IntPtr.Zero ? 1f : TileFadeRamp.Explored(owner);
            if (own <= 0f) return 0f;
            IntPtr occ = _oFadeTile[i];
            return occ == IntPtr.Zero ? own : TileFadeRamp.Fade(occ) * own;
        }
        // BW4: the two tiles resolved to registry SLOTS once per slot per registry generation. The two
        // questions are asked of different tiles (the fog owner and the occluding tile), so the one-entry memo
        // this replaces thrashed and ran the hash probe about twice per accepted slot per frame.
        int gen = TileFadeRamp.Gen;
        if (_oTileGen[i] != gen)
        {
            _oTileSlot[i] = TileFadeRamp.SlotOf(owner);
            _oFadeSlot[i] = TileFadeRamp.SlotOf(_oFadeTile[i]);
            _oTileGen[i] = gen;
        }
        float fog = TileFadeRamp.ExploredAt(_oTileSlot[i]);
        if (fog <= 0f) return 0f;
        return TileFadeRamp.FadeAt(_oFadeSlot[i]) * fog;
    }

    /// <summary>BW4: the registry slots of <see cref="_oTile"/> and <see cref="_oFadeTile"/>, and the
    /// <see cref="TileFadeRamp.Gen"/> they were resolved under. -1 = a tile the registry does not know, which
    /// reads as opaque and explored -- so a stale generation costs one probe, never a wrong factor. A
    /// re-tenanted slot is cleared by <see cref="UnstampChase"/>, on the same edge and the same guarantee the
    /// alpha cache beside it already keeps: the new tenant's tile is a different tile, and its slot must be
    /// resolved again rather than inherited.</summary>
    private static readonly int[] _oTileSlot = new int[MaxOwned];
    private static readonly int[] _oFadeSlot = new int[MaxOwned];
    private static readonly int[] _oTileGen = new int[MaxOwned];

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
        bool recompose = stale || newTenant || _gate[i].RecFrame < _frameSeq - 1 || GobXform.Moved(_oGob[i]);
        if (!recompose) { c.MxCached++; return *(Matrix4x4*)r; }
        c.MxChased++;
        Matrix4x4 m = GobXform.Compose(_oTri[i], _oGob[i]);
        if (Renderer.XformCheck.V) GobXform.Check(_oNode[i], _oGob[i], in m);
        return m;
    }

    /// <summary>A re-tenanted slot's caches describe the previous tenant.</summary>
    private static void UnstampChase(int i)
    { _disGen[i] = 0; _alphaKnown[i] = false; _gate[i].OccFade = 0f; _gate[i].OccHeld = 0f; _oTileGen[i] = 0;
      _gate[i].FrozenAt = -1; }

    /// <summary>The [alloy] line's clause.</summary>
    private static string ChaseNote()
    {
        long mxAll = _mxChased + _mxCached, disAll = _disAsked + _disCached;
        long frames = Math.Max(1, Counters._frames);
        string s = $"route {(float)_routeIn / frames:F0}/frame, {(float)_routeOut / frames:F0} off-frustum untouched"
                 + $", {(float)_routeMemoed / frames:F0}/frame replayed frozen"
                 + (_routeAudited > 0 ? $" (routecheck {_routeMisses} miss(es) over "
                                      + $"{(float)_routeAudited / frames:F0} re-derived/frame on their colour "
                                      + "cadence, must be 0)"
                                      : _routeMemoed > 0 ? " (routecheck has not run yet this period)" : "")
                 + (_visList ? $", visible set rebuilt {_visRebuilds} of {frames} frames from "
                             + $"{AreaGrid.Occupied} cells ({AreaGrid.Filed} indexed, {AreaGrid.Unboxed} unboxed ({_unboxedEmpty} never filled), "
                             + $"{_dynN} on gobs, {_visIndexed / Math.Max(1, _visRebuilds)}/rebuild, {_visTrimmed / Math.Max(1, _visRebuilds)} cell-only candidates trimmed/rebuild)"
                             : ", visible set OFF") + VisAuditNote()
                 + $"; matrices {(float)_mxChased / frames:F0}/frame composed of {(float)mxAll / frames:F0}"
                 + $", keyhole {(float)_disAsked / frames:F0}/frame asked of {(float)disAll / frames:F0}"
                 + $", keyhole {(float)_disBoxed / frames:F0}/frame from the cached box"
                 // BW1: every census below counts the slots the walk RE-DERIVED -- a replayed slot asks none of
                 // these questions, so `replayed frozen` above is the population they do not cover.
                 + $"; of the re-derived: dissolve refused {(float)_disNoLit / frames:F0} unlit + "
                 + $"{(float)_disNoFeat / frames:F0} nokeyhole-feature"
                 + $" + {(float)_disGobUnpierced / frames:F0} gob/frame"
                 + $"; gobs in view {_visGobN}/{_visGobAll}" + (_visGobTrunc ? " TRUNCATED" : "")
                 // GS1: the list is cut inside ScenePrep's in-view sweep now; this is the cadenced re-walk
                 // that holds that fusion to account, and its miss count must read 0.
                 + (_visGobAudits == 0 ? "" : $" ({_visGobAuditMiss} of {_visGobAudits} audit(s) disagree)")
                 + (_visGobTruncSkips == 0 ? "" : $" ({_visGobTruncSkips} audit(s) skipped, list truncated)")
                 + "; " + GobXform.Period() + "; " + GobLod.Period() + BatchAuditNote()
                 // BW1: `route` is the memo's miss count. Under a full poll gpu.static is stood down, so the
                 // memo never runs and this reads 0 by construction -- the routecheck clause above is what
                 // holds the memo to account, on its own cadence, in a session that is not polling.
                 + (_fullPoll ? $"; POLL AUDIT misses (must be 0): keyhole {_pollMissDis} box {_pollMissBox} "
                              + $"alpha {_pollMissAlpha} route {_routeMisses}" : "");
        return s;
    }

    private static void ResetChasePeriod()
    {
        _mxChased = _mxCached = _disAsked = _disCached = 0;
        _routeIn = _routeOut = 0; _visRebuilds = 0; _visIndexed = 0;
        _pollMissDis = 0; _pollMissBox = 0; _pollMissAlpha = 0; _disBoxed = 0;
        _routeMemoed = 0; _routeAudited = 0; _routeMisses = 0; _visTrimmed = 0; _visBoxOnly = 0; _visSlices = 0;
        _visGobAudits = 0; _visGobAuditMiss = 0; _visGobTruncSkips = 0;
        ResetBatchAuditPeriod();
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

    /// <summary>BW1, off-device: the memo's decision table. Each row is one term that can move a frozen slot's
    /// verdict, and the row exists because replaying the verdict through it would draw the wrong thing -- a
    /// window solid through a keyhole, a fading roof at full opacity, a revoked part. A term dropped from
    /// <see cref="RouteMemo"/> is this check going red rather than a bug found live weeks later.</summary>
    private static bool RouteMemoSelfCheck(Checker check)
    {
        const int i = MaxOwned - 2;
        IntPtr triWas = _oTri[i], gobWas = _oGob[i];
        int entryWas = _oEntry[i], frozenWas = _gate[i].FrozenAt, recGenWas = _gate[i].RecGen;
        int recEnrollWas = _gate[i].RecEnroll, enrollWas = _gate[i].EnrollGen;
        bool staleWas = _gate[i].TexStale, memoWas = _routeMemoOn, frustumWas = _passFrustumOk;
        bool knownWas = _alphaKnown[i];
        float occWas = _gate[i].OccFade, heldWas = _gate[i].OccHeld;
        byte clsWas = _oCls[i];
        long memoedWas = _routeMemoed, auditedWas = _routeAudited;
        int seqWas = _frameSeq;
        // A slot's audit frame is the one its colour cadence lands on, so the two frames the check needs are a
        // residue apart: on `audit` the memo must re-derive, one frame off it the memo must replay.
        int audit = 1024 + ((-i) & (ColourCadence - 1)), replay = audit + 1;

        // A part the table holds as live, and a slot the static path froze last frame with every other term
        // at rest. The entry is the one PartTable's own hash gives this pointer, so the dirty MARK below (which
        // goes through Find) lands on the entry the memo reads. No sphere test: the pass has no frustum here,
        // which is the path that routes everything.
        IntPtr tri = (IntPtr)0x1000;
        int e = (int)(((ulong)tri >> 4) % (ulong)PartTable.EntryCount);
        byte stateWas = PartTable._pEnt[e].State;
        IntPtr partWas = PartTable._pEnt[e].Part;
        PartTable._pEnt[e].Part = tri; PartTable._pEnt[e].State = PartTable.StActive;
        _oTri[i] = tri; _oEntry[i] = e; _oGob[i] = IntPtr.Zero; _oCls[i] = (byte)AlloyClass.Cutout;
        _gate[i].FrozenAt = _frameSeq - 1; _gate[i].RecGen = _recEpoch; _gate[i].RecEnroll = _gate[i].EnrollGen = 7;
        _gate[i].TexStale = false; _gate[i].OccFade = 0f; _gate[i].OccHeld = 0f;
        _routeMemoOn = true; _passFrustumOk = false;
        _frameSeq = replay; _gate[i].FrozenAt = replay - 1;

        int n = 0;
        bool ok = RouteMemo(i, null, ref n) == MemoReplayed && n == 1 && _pass[0] == i
                  && _passCls[0] == (byte)AlloyClass.Cutout && _passAlpha[0] == 1f && !_passDis[0];
        check.Expect(ok, "a slot frozen last frame does not replay its verdict, or replays the wrong one");
        // ...and one term at a time, each restored after it.
        bool Refuses(string why)
        {
            int m = 0;
            bool full = RouteMemo(i, null, ref m) == MemoFull && m == 0;
            check.Expect(full, $"the memo replays a verdict {why}");
            return full;
        }
        _routeMemoOn = false; ok &= Refuses("with the memo disarmed (gpu.static off, a keyhole open, a moved tile fade)");
        _routeMemoOn = true;
        _gate[i].FrozenAt = replay - 2; ok &= Refuses("for a slot that was not frozen last frame");
        _gate[i].FrozenAt = replay - 1;
        _oGob[i] = (IntPtr)1; ok &= Refuses("for a slot on a gob, whose folds and occlusion fade it does not read");
        _oGob[i] = IntPtr.Zero;
        _gate[i].OccFade = 0.5f; ok &= Refuses("for a slot the wall fade is easing");
        _gate[i].OccFade = 0f; _gate[i].OccHeld = 0.1f; ok &= Refuses("for a slot inside the wall fade's hold");
        _gate[i].OccHeld = 0f;
        _oTri[i] = IntPtr.Zero; ok &= Refuses("for a vacated slot");
        _oTri[i] = tri;
        PartTable._pEnt[e].State = PartTable.StDead; ok &= Refuses("for a part the death notice took");
        PartTable._pEnt[e].State = PartTable.StActive;
        _gate[i].RecGen = _recEpoch - 1; ok &= Refuses("for a slot with no record to serve");
        _gate[i].RecGen = _recEpoch;
        _gate[i].RecEnroll = 8; ok &= Refuses("for a re-tenanted slot");
        _gate[i].RecEnroll = 7;
        _gate[i].TexStale = true; ok &= Refuses("for a slot whose texture is stale");
        _gate[i].TexStale = false;
        // BW3: the alpha mark stands the slot down AND forces the read that the colour cadence would skip.
        _alphaKnown[i] = true;
        PartTable.NoteAlphaWrite(tri);
        ok &= Refuses("for a part whose alpha the controller runtime just wrote");
        check.Expect(!_alphaKnown[i] && !PartTable.AlphaDirty(e),
                     "the alpha mark is consumed: the slot must re-read +0xa0 rather than serve its cache");
        ok &= !_alphaKnown[i] && !PartTable.AlphaDirty(e);
        // The slot's colour frame re-derives it instead of replaying, which is what keeps the alpha read, the
        // identity compares and the colour reads on their cadence -- the freeze's own 1-in-16 stagger puts the
        // memo's full-path frame one residue off the cadence, so without this they would never fire again.
        _alphaKnown[i] = true;
        _frameSeq = audit; _gate[i].FrozenAt = audit - 1;
        int a = 0;
        bool audited = RouteMemo(i, null, ref a) == MemoAudit && a == 0;
        check.Expect(audited, "a slot's colour frame does not re-derive it, so its cadenced reads never fire");
        ok &= audited;
        _frameSeq = replay; _gate[i].FrozenAt = replay - 1;
        int b = 0;
        bool replayed = RouteMemo(i, null, ref b) == MemoReplayed && b == 1;
        check.Expect(replayed, "...and one frame off the cadence it is not replayed after all");
        ok &= replayed;

        _oTri[i] = triWas; _oGob[i] = gobWas; _oEntry[i] = entryWas; _gate[i].FrozenAt = frozenWas;
        _gate[i].RecGen = recGenWas; _gate[i].RecEnroll = recEnrollWas; _gate[i].EnrollGen = enrollWas;
        _gate[i].TexStale = staleWas; _gate[i].OccFade = occWas; _gate[i].OccHeld = heldWas; _oCls[i] = clsWas;
        _alphaKnown[i] = knownWas; _routeMemoOn = memoWas; _passFrustumOk = frustumWas;
        _frameSeq = seqWas;
        PartTable._pEnt[e].Part = partWas; PartTable._pEnt[e].State = stateWas;
        _routeMemoed = memoedWas; _routeAudited = auditedWas;
        return ok;
    }

    /// <summary>S1-6d, off-device: one cycle of <see cref="AuditSlice"/> visits every index of the table exactly
    /// once, in order, and the next cycle starts over. Run on a count with a remainder (1000 over 64 slices) and
    /// on counts smaller than the slice count, where the step rounds up to 1 and a cycle is short.</summary>
    private static bool SliceCoverSelfCheck(Checker check)
    {
        bool ok = true;
        foreach (int count in stackalloc int[] { 1000, 64, 7, 1 })
        {
            var seen = new int[count];
            int at = 0, cycles = 0, next = 0;
            while (next < count && cycles <= VisAuditSlices)
            {
                cycles++;
                AuditSlice(count, ref at, out int from, out int to);
                ok &= from == next && to > from;
                for (int k = from; k < to; k++) seen[k]++;
                next = to;
            }
            for (int k = 0; k < count; k++) ok &= seen[k] == 1;
            ok &= cycles <= VisAuditSlices && next == count;
            // ...and the cursor wraps rather than running off the end
            AuditSlice(count, ref at, out int f2, out _);
            ok &= f2 == 0;
        }
        check.Expect(ok, "a cycle of audit slices leaves slots unvisited, visits one twice, or does not wrap");
        return ok;
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

        check.Expect(RouteMemoSelfCheck(check), "the route memo replays a verdict it should have re-derived");
        check.Expect(TrimSelfCheck(check), "the visible-set trim would hide geometry from a pass");

        // BW7: a row grown past its stride costs cache lines, not correctness, so it is SAID loudly and does
        // NOT join this check's verdict -- a false verdict here stands the whole renderer down
        // (AlloyRenderer.cs), which would be the wrong price for a field somebody appended.
        if (sizeof(RouteGate) != GateRowBytes)
            log.Always($"AlloyRenderer: the route gate row is {sizeof(RouteGate)} bytes, not {GateRowBytes} -- "
                     + "the gate costs a second cache line per visited slot again (BW7)");
        // ...and the aperture the memo arms on is cleared when the camera goes, rather than held at its last
        // value -- which disarmed the memo permanently and said nothing. The CLEAR is asserted, not the read:
        // this check gates the whole renderer (a false verdict stands Alloy down), so it may not depend on
        // there being no live camera at whatever point in service order it runs.
        byte khWas = _khOn; float cutWas = _khCut; bool camWas = _khCam;
        float[] planeWas = (float[])_khPlane.Clone();
        _khCam = true; _khOn = 1; _khCut = 5f; _khPlane[2] = 1f; _khPlane[3] = -5f;
        ClearKeyholeCamera();
        check.Expect(!_khCam && _khOn == 0 && _khCut == 0f && _khPlane[2] == 0f && _khPlane[3] == 0f,
                     "losing the keyhole camera leaves a stale aperture, which disarms the route memo for good");
        _khOn = khWas; _khCut = cutWas; _khCam = camWas;
        Array.Copy(planeWas, _khPlane, _khPlane.Length);

        check.Expect(ResetGate(new RouteGate[2])[1].FrozenAt == -1,
                     "a fresh gate row's FrozenAt is not seeded, so an unfrozen slot reads as frozen on frame 1");

        // S1-6d: the rolling audit's whole claim is that a cycle of slices covers the table exactly as the
        // whole-table walk did. Checked on a count that is NOT a multiple of the slice count, which is where an
        // off-by-one leaves a tail of slots no frame ever audits -- the flicker hunt going quiet about them.
        check.Expect(SliceCoverSelfCheck(check), "the rolling visible-set audit does not cover the table");

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
