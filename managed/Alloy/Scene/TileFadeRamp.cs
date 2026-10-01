using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-104: the tile fade, Alloy's. Two independent per-tile ramps the world frame draws at, both of
/// which used to be read straight off <c>TileData</c> where <c>ManageSceneBSP</c> @0x47fa80 and its visitor
/// (@0x481000) left them:
/// <list type="number">
/// <item><see cref="Fade"/> -- the CAMERA-OCCLUSION fade. A tileset mesh authored <c>tilefade 1</c> (the roof)
/// drops to transparent while it stands between the camera and the player, and comes back when it does
/// not.</item>
/// <item><see cref="Explored"/> -- the fog-of-war reveal, which ramps a tile in once the area stops calling it
/// fogged.</item>
/// </list>
/// <para><b>The decision, ported.</b> Per frame <c>ManageSceneBSP</c> builds ONE plane through the followed
/// gob, facing the way the camera looks: normal = the unit camera-to-target direction flattened into XY, and
/// the plane passes through the target (@0x47ff8c..0x480068). A camera looking down more steeply than
/// horizontal component 0.4 stands the whole fade down (<c>Scene+0x1a0 = 1</c>). <c>ProcessTriMeshParts</c>
/// @0x480d40 then classifies each visited tile once, through <c>CheckFadePlane</c> @0x47bf30: if ANY of its
/// <c>tilefade 1</c> parts has a 2D world box that is not ENTIRELY beyond that plane the tile is fading OUT,
/// otherwise IN. <c>HandleFadingOutBucket</c> @0x47f870 / <c>HandleFadingInBucket</c> @0x47f670 then move the
/// factor at 4.0/s, but only after 0.1 s of consistent intent -- <c>TileData+0x78</c> is a signed second
/// counter that has to leave the +/-0.1 band before the ramp starts, which is what stops a roof flickering at
/// the boundary. Every one of those numbers is reproduced here, over Alloy's own arrays.</para>
/// <para><b>Mode.</b> <c>Scene::geomFadeEnabled</c> gates the whole thing, and it is the player's "Hide
/// second-story tiles" option: <c>CNWCArea::SetHideFadeGeometryMode</c> @0x704ef0 calls
/// <c>Scene::SetGeomFadeProperties</c> @0x45e1d0 through the Scene VTABLE (+0xb8, which is why no call-graph
/// edge shows it) with 1 (automatic: the plane test), 2 (never) or 4 (always). <c>ProcessTriMeshParts</c> takes
/// the fade path only for <c>(mode == 1 &amp;&amp; Scene+0x1a0 == 2) || mode == 4</c>; anywhere else a
/// <c>tilefade 1</c> part goes to the plain mesh buckets and draws opaque, whatever its tile's factor
/// (<see cref="FadeLive"/>).</para>
/// <para><b>Why nothing is written back.</b> The fade's readers are all render-side: the fade and cap draw
/// buckets, <c>RenderDrawBucket</c>'s alpha fold, and <c>PartEmitter::RenderEmitter</c>'s
/// <c>geomFade &lt; 1</c> refusal -- all of them Alloy's now. It is not hit testing (that is
/// <c>Gob+0x10c</c>, which <see cref="DoorFade"/> does write back for exactly this reason), not the minimap
/// (<c>CNWCArea</c>'s own fog-of-war bytes, which the map panel draws from) and not picking. Alloy simply
/// stops depending on it.
/// <para>That used to be softened by "and the engine's own copy stays current anyway". It does not, from
/// <c>alloy.skipbuckets=3</c> on: the commit <c>TileData+0x70 = +0x74</c> is at the TOP of
/// <c>ManageSceneBSP</c> @0x47fa80, the two <c>HandleFading*Bucket</c> writers of <c>+0x74</c> are at its
/// bottom, and the fog-of-war step on <c>+0xc8</c> is in its visitor -- so with the walk skipped all three
/// engine-side factors FREEZE, along with <c>Scene+0x1a8</c>'s classified-tile list and the <c>+0xc0</c>
/// state enum. Which is why the reader audit above had to be exhaustive rather than reassuring, and why
/// <c>/buckets tilefade 0</c> re-arms the engine's walk instead of quietly reading a stale field.</para>
/// <para><b>Scale.</b> One box test plus a handful of multiply-adds per tile, over a registry built on the
/// scene-walk edge -- an area's ~1k tiles is microseconds, and nothing per frame scales with anything but the
/// tile count. Allocation-free.</para></summary>
internal static unsafe class TileFadeRamp
{
    /// <summary>Factors per second, from <c>DeltaT * 4.0</c> in all three ramps (the two fade buckets and the
    /// fog-of-war step in the BSP visitor).</summary>
    private const float Rate = 4f;
    /// <summary>Seconds of consistent intent before the fade starts moving: the +/-0.1 band
    /// <c>TileData+0x78</c> has to leave. Stops a tile on the plane boundary flickering.</summary>
    private const float Hysteresis = 0.1f;
    /// <summary>Below this horizontal component of the unit camera-to-target direction the engine stands the
    /// whole fade down (<c>SQRT(__x) &lt; 0.4</c> @0x47ffd6): a top-down camera has no "between".</summary>
    private const float MinHorizontal = 0.4f;
    /// <summary><c>tilefade 1</c> -- the MDL class that fades. 2 is the cap that shows through the hole and 4
    /// is the no-shadow class; neither is ever tested for occlusion.</summary>
    private const int TileFadeFades = 1;
    /// <summary><c>Scene::geomFadeEnabled</c>'s three values: the plane test, no fade, and every roof out.</summary>
    private const int ModeAuto = 1, ModeNever = 2, ModeAlways = 4;

    /// <summary>The table's starting size, and the floor it is never sized below: enough for a 32x32 area
    /// inside the engine's own five-tile ring before an area is known.
    /// <para>It used to be the whole capacity, as a const, on the claim that 2048 is "double 32x32". It is
    /// not: <see cref="AreaBorder"/> replays the border out to <c>alloy.border</c> tiles (default 15), so a
    /// 32x32 area carries (32+30)^2 = 3844 tiles and a modest 20x20 one carries 2500. The live table sat at
    /// exactly 2048 and refused the rest, which is CX-45's silent degradation: those tiles never fade, and
    /// (until the scene walk's own fix) never registered an emitter either. The capacity is now DERIVED --
    /// see <see cref="AreaBound"/> -- and overflow past it is a loud bug rather than a ceiling.</para></summary>
    private const int MinTiles = 2048;
    /// <summary>The absolute ceiling, from the engine's own maxima: a 64-tile area side (double the largest
    /// shipped 32) inside <see cref="AreaBorder.MaxRing"/>, squared and rounded up to a power of two. Past
    /// this a tile is REFUSED, which is the only remaining path that drops one.</summary>
    private const int MaxTilesCeiling = 1 << 15;
    /// <summary>Per-tile slab of <c>tilefade 1</c> part boxes. Stock roofs are one to three pieces; past this
    /// the tile falls back to its own union box, which fades MORE readily rather than less.</summary>
    private const int FadePartsPerTile = 8;
    private const int PlaneFloats = 24;

    /// <summary>Live capacity of every per-tile column, and the mask of the hash over them (always at least
    /// twice the capacity, so a table holding <see cref="_cap"/> live tiles still has an empty bucket on every
    /// probe chain).</summary>
    private static int _cap, _hashMask;

    private static IntPtr[] _tile = Array.Empty<IntPtr>();
    private static int[] _slot = Array.Empty<int>();
    private static bool[] _freed = Array.Empty<bool>();
    private static int[] _gen = Array.Empty<int>();
    private static Vector3[] _boxMin = Array.Empty<Vector3>();
    private static Vector3[] _boxMax = Array.Empty<Vector3>();
    /// <summary>XY boxes of this tile's <c>tilefade 1</c> parts: (minX, minY, maxX, maxY).</summary>
    private static Vector4[] _fadeBox = Array.Empty<Vector4>();
    private static int[] _fadeBoxN = Array.Empty<int>();
    /// <summary>This tile had more <c>tilefade 1</c> parts than the slab holds, so the occlusion test also
    /// uses its union box -- conservative on purpose (see <see cref="FadePartsPerTile"/>).</summary>
    private static bool[] _fadeBoxOver = Array.Empty<bool>();
    /// <summary>The two factors, and the intent counter behind the first of them.</summary>
    private static float[] _fade = Array.Empty<float>();
    private static float[] _acc = Array.Empty<float>();
    private static float[] _explored = Array.Empty<float>();
    /// <summary>Tile slots back in the pool. Same reason <see cref="SceneParts"/> keeps one: without it
    /// <see cref="_count"/> is a high-water mark, not a capacity -- <see cref="Insert"/> reclaims a freed slot
    /// only when it lands on the new tile's probe chain, so a session that keeps replacing tiles would walk
    /// the array to <see cref="_cap"/> and then stop registering any tile whose chain reaches an empty
    /// hash bucket. Lower churn than the part table, identical shape.</summary>
    private static int[] _freeTile = Array.Empty<int>();
    private static int _freeN;
    private static int _count;
    private static long _reused, _compactions;
    /// <summary>Times the table had to grow past <see cref="AreaBound"/>, and the census of the area the
    /// bound was derived from when that first happened. A nonzero count is a BUG in the bound, not a tuning
    /// knob: the table grows anyway so the area still fades, and says so once.</summary>
    private static long _grewPastBound;
    private static bool _grewLogged;

    /// <summary>Six inward-facing camera planes (xyz = unit normal, w = offset), rebuilt each tick.</summary>
    private static readonly float[] _planes = new float[PlaneFloats];

    private static ExtenderLog? _log;
    private static float* _deltaT;
    private static int* _geomFadeMode;
    /// <summary>The engine's own chain to the current area, for <see cref="AreaBound"/>: the area's tile grid
    /// is what sizes the table. Read at the rebuild edge, never dereferenced beyond the two ints, and asked
    /// directly rather than off <see cref="FrameInputs.AreaExtent"/> -- that copy is refreshed once per
    /// presented frame, so the FIRST rebuild of a new area would still read the previous area's grid and a
    /// bigger new area would look like a bound bug.</summary>
    private static IntPtr _appMgr;
    private static GetModuleFn _getModule;
    /// <summary>Did the two layout groups <see cref="AreaBound"/> reads resolve? Without them the bound is not
    /// derived at all rather than derived from a garbage offset.</summary>
    private static bool _areaCensus;
    /// <summary>This frame's verdict of <see cref="FadeLive"/>: the factors are applied at all.</summary>
    private static bool _fadeLive;
    private static int _mode = ModeAuto;
    private static bool _ready;
    private static int _rebuildGen, _rebuiltTiles;
    private static long _ticks, _stepped, _fadingOut, _revealing, _capped;
    private static bool _cappedLogged, _planeOk;

    /// <summary>Per tile, ONE of its mesh parts, and the stamped map from that part back to the tile. This is
    /// the tile's liveness witness, and it is the same argument the engine relies on: <c>RenderDrawBucket</c>
    /// and <c>PartEmitter::RenderEmitter</c> both dereference a part's <c>TileData*</c> unchecked, so a live
    /// part implies a live tile. <c>Scene::DeleteTile</c> destroys a tile's parts with it and does NOT bump
    /// <c>Scene::Sequence</c>, so without this the registry would keep dereferencing the freed
    /// <c>TileData</c> -- every frame, for as long as nothing else happened to force a rebuild.
    /// <para>One witness and not a list: a tile dying takes all its parts with it, so one is enough to see it.
    /// The other direction -- a single part destroyed on its own -- frees a LIVE tile, which costs it its fade
    /// state until the next rebuild and can never be a wrong dereference.</para></summary>
    private static IntPtr[] _witness = Array.Empty<IntPtr>();
    private static int[] _witnessSlot = Array.Empty<int>();
    private static int[] _witnessStamp = Array.Empty<int>();
    private static long _witnessDeaths;

    /// <summary>BW4: the generation a resolved tile SLOT and the factors behind it are good for. Bumped by
    /// every edge that can move either -- a registry rebuild or sweep (which renumbers slots), a witness death
    /// (which frees one), a new Scene, and any tick that actually moved a factor or flipped
    /// <see cref="FadeLive"/>. A caller that resolved <see cref="SlotOf"/> under this generation may keep the
    /// index and the values it read; a caller that only wants the values (the route memo) may skip reading them
    /// at all.
    /// <para>It replaces a one-entry memo on <see cref="Slot"/> whose doc claimed "a route walks one tile's
    /// parts together". It does not: the route walks BATCH-KEY order, and the two questions per part are asked
    /// of two different tiles (the fog owner and the occluding tile), so the memo thrashed and the hash probe
    /// ran about twice per accepted slot per frame.</para></summary>
    internal static int Gen => _valueGen;
    private static int _valueGen = 1;
    /// <summary>Any resolved slot index or factor just became suspect.</summary>
    private static void Moved() => _valueGen++;

    /// <summary>The first symbol or layout that did not resolve, or null.</summary>
    internal static string? Missing { get; private set; }

    internal static void Resolve(ExtenderLog log)
    {
        _log = log;
        _deltaT = (float*)Native.Sym(SymbolMap.DeltaT);
        _geomFadeMode = (int*)Native.Sym(SymbolMap.GeomFadeEnabled);
        _appMgr = Native.Sym(SymbolMap.AppManager);
        _getModule = EngineFn.GetModule;
        Ensure(MinTiles);
        // AreaBound reads the module's current area and its tile grid. An unresolved row is a ZERO offset
        // (EngineLayout.Field returns `mine ?? 0`), and none of these three fields lives at 0, so that is the
        // test -- reading them anyway would clamp a bound derived from whatever sits at the top of the object.
        // Not part of Missing: the fade does not need the census, it only starts smaller without it.
        _areaCensus = EngineLayout.ModuleCurrentArea != 0 && EngineLayout.AreaTileWidth != 0
                      && EngineLayout.AreaTileHeight != 0;
        if (!_areaCensus)
            log.Always("TileFadeRamp: the area census rows (CNWCModule.currentArea, CNWCArea.tileWidth/"
                     + $"tileHeight) have no value for this OS -- the tile table starts at {MinTiles} and grows "
                     + "on demand, so its growth line means that rather than a wrong bound");
        Missing = _deltaT == null ? SymbolMap.DeltaT
                : _geomFadeMode == null ? SymbolMap.GeomFadeEnabled
                : !EngineLayout.Require(log, "TileFadeRamp", "TileData", "PartTriMesh", "MdlNodeTriMesh", "Camera")
                  ? "TileData/PartTriMesh/MdlNodeTriMesh/Camera layout"
                : null;
        _ready = Missing == null;
        if (!_ready)
            log.Always($"TileFadeRamp: {Missing} did not resolve -- CX cannot drive the tile fade this "
                     + "session, so it reads the factors the engine's own BSP walk leaves on each tile "
                     + "(the same route /buckets tilefade 0 takes)");
    }

    // ---- the registry, filled by SceneParts' BSP scan --------------------------------------------

    /// <summary>A rebuild is starting: bump the generation so a tile this pass does not reach is dropped.
    /// State is KEPT for a tile that survives -- a placeable spawning bumps the scene epoch and re-walks, and
    /// snapping every roof back to opaque there would be visible.</summary>
    internal static void BeginRebuild()
    {
        _rebuildGen++;
        _rebuiltTiles = 0;
        Ensure(AreaBound());
    }

    /// <summary>How many tiles this area can present: its ARE tile grid grown by the border ring
    /// <see cref="AreaBorder"/> actually spawns, with a tile of slack on each side (the BSP's leaves CLAMP, so
    /// a spawn at the outer edge lands on the last cell rather than past it). One <c>TileData</c> per grid
    /// cell -- <c>Scene::SpawnTile</c> makes one per position and <c>SceneBSPGetTileData</c> answers by
    /// position -- so this is the real bound, not a guess with headroom. <see cref="MinTiles"/> while no area
    /// is known.</summary>
    private static int AreaBound()
    {
        CNWCArea a = _areaCensus ? Area() : default;
        if (a.IsNull) return MinTiles;
        int ring = Math.Max(AreaBorder.Ring, AreaBorder.EngineRing);
        long w = a.TileWidth + 2L * ring + 2, h = a.TileHeight + 2L * ring + 2;
        if (w <= 2 || h <= 2) return MinTiles;
        return (int)Math.Clamp(w * h, MinTiles, MaxTilesCeiling);
    }

    /// <summary>The current CNWCArea, through the engine's own chain (g_pAppManager needs two derefs -- the
    /// CAppManager first, whose first field is the CClientExoApp*). Every deref guarded; the null view while
    /// the chain is unavailable.</summary>
    private static CNWCArea Area()
    {
        if (_appMgr == IntPtr.Zero || _getModule == null) return default;
        IntPtr mgr = *(IntPtr*)_appMgr;
        if (mgr == IntPtr.Zero) return default;
        IntPtr app = *(IntPtr*)mgr;
        if (app == IntPtr.Zero) return default;
        var module = new CNWCModule(_getModule(app));
        return module.IsNull ? default : module.CurrentArea;
    }

    /// <summary>Grow every per-tile column to hold <paramref name="need"/> tiles (and the hash to twice that).
    /// Only ever called from the rebuild edge and from the overflow path, never per frame, and never shrinks:
    /// an area load is where an NWN client allocates.</summary>
    private static void Ensure(int need)
    {
        if (need <= _cap) return;
        int cap = Math.Max(MinTiles, _cap);
        while (cap < need && cap < MaxTilesCeiling) cap *= 2;
        cap = Math.Min(cap, MaxTilesCeiling);
        if (cap <= _cap) return;
        int hash = 1;
        while (hash < cap * 2) hash <<= 1;
        Grow(ref _tile, cap); Grow(ref _freed, cap); Grow(ref _gen, cap);
        Grow(ref _boxMin, cap); Grow(ref _boxMax, cap);
        Grow(ref _fadeBox, cap * FadePartsPerTile); Grow(ref _fadeBoxN, cap); Grow(ref _fadeBoxOver, cap);
        Grow(ref _fade, cap); Grow(ref _acc, cap); Grow(ref _explored, cap);
        Grow(ref _freeTile, cap); Grow(ref _witness, cap);
        _cap = cap;
        _slot = new int[hash]; _witnessSlot = new int[hash]; _witnessStamp = new int[hash];
        _hashMask = hash - 1;
        Rehash();
        _log?.Info($"TileFadeRamp: table sized for {cap} tile(s) ({_count} live)");
    }

    private static void Grow<T>(ref T[] a, int n)
    {
        var next = new T[n];
        Array.Copy(a, next, a.Length);
        a = next;
    }

    /// <summary>Rebuild both hash maps over the live tiles. The slab moved, so nothing in the old buckets
    /// means anything; the tile indices themselves are unchanged.
    /// <para>Called mid-rebuild (from <see cref="Ensure"/>) this re-stamps witnesses interned by the PREVIOUS
    /// rebuild with the current <see cref="_rebuildGen"/>, which makes them findable again. That is sound and
    /// slightly better than the alternative: a witness is still the part whose death frees its tile, so a death
    /// notice arriving mid-rebuild now frees the right tile instead of finding nothing, and
    /// <see cref="WitnessTile"/> confirms the pointer before it answers.</para></summary>
    private static void Rehash()
    {
        Array.Fill(_slot, -1);
        Array.Clear(_witnessStamp);
        for (int t = 0; t < _count; t++)
        {
            if (_freed[t] || _tile[t] == IntPtr.Zero) continue;
            Link(t);
            if (_witness[t] != IntPtr.Zero) InternWitness(_witness[t], t);
        }
    }

    /// <summary>Intern <paramref name="tile"/> for this rebuild; <paramref name="fresh"/> = first sighting on
    /// this pass, which is the caller's cue to contribute the tile's part list once. -1 = the table is
    /// full.</summary>
    internal static int NoteTile(IntPtr tile, out bool fresh)
    {
        fresh = false;
        if (!_ready || tile == IntPtr.Zero) return -1;
        int t = Find(tile);
        if (t >= 0)
        {
            if (_gen[t] == _rebuildGen) return t;
            Restart(t);         // first sighting this pass: box and fade-part slab are rebuilt from scratch
            fresh = true;
            return t;
        }
        t = Insert(tile);
        if (t < 0 && GrowPastBound()) t = Insert(tile);
        if (t < 0) { Cap(); return -1; }
        Restart(t);
        _fade[t] = 1f; _acc[t] = 0f;
        // A tile the area has never fogged is explored. The reveal ramp exists for a tile coming OUT of the
        // fog; starting every tile at 0 instead would let a mis-answered in-view test HIDE the world rather
        // than show one extra frame of fade-in, and that is not a failure mode worth buying parity with.
        _explored[t] = new TileData(tile).FoggedByWar != 0 ? 0f : 1f;
        fresh = true;
        return t;
    }

    private static void Restart(int t)
    {
        _gen[t] = _rebuildGen;
        _witness[t] = IntPtr.Zero;
        _fadeBoxN[t] = 0; _fadeBoxOver[t] = false;
        _boxMin[t] = new Vector3(float.MaxValue); _boxMax[t] = new Vector3(float.MinValue);
        _rebuiltTiles++;
    }

    /// <summary>Is <paramref name="tri"/> authored <c>tilefade 1</c>? The only class the occlusion fade
    /// reaches: <c>ProcessTriMeshParts</c> @0x480d40 sends every part with <c>PartTriMesh+0xa4 != 1</c> (the
    /// node's <c>tileFade</c>, copied by <c>MdlNodeTriMesh::InternalCreateInstance</c>) to the plain mesh
    /// buckets, so the floor and walls under a fading roof never fade.</summary>
    internal static bool Fades(IntPtr tri)
    {
        var mdl = new MdlNodeTriMesh(new CX.Engine.Part(tri).Node.Ptr);
        return !mdl.IsNull && mdl.TileFade == TileFadeFades;
    }

    /// <summary>One of tile <paramref name="t"/>'s mesh parts: grow the tile's world box with it, and if it is
    /// authored <c>tilefade 1</c> record its XY box as an occlusion test surface.</summary>
    internal static void NoteTilePart(int t, IntPtr tri)
    {
        if (!_ready || (uint)t >= (uint)_cap || tri == IntPtr.Zero) return;
        var p = new PartTriMesh(tri);
        Vector3 lo = p.BboxMin, hi = p.BboxMax;
        // A part whose world box was never computed reads as six zeros (the PartTriMesh constructor zeroes
        // them and only ProcessStaticParts fills them in), which would drag the tile's box to the origin.
        if (lo == Vector3.Zero && hi == Vector3.Zero) return;
        _boxMin[t] = Vector3.Min(_boxMin[t], lo);
        _boxMax[t] = Vector3.Max(_boxMax[t], hi);
        if (_witness[t] == IntPtr.Zero) { _witness[t] = tri; InternWitness(tri, t); }
        if (!Fades(tri)) return;
        int n = _fadeBoxN[t];
        if (n >= FadePartsPerTile) { _fadeBoxOver[t] = true; return; }
        _fadeBox[t * FadePartsPerTile + n] = new Vector4(lo.X, lo.Y, hi.X, hi.Y);
        _fadeBoxN[t] = n + 1;
    }

    /// <summary>The rebuild is done: every tile the pass did not reach is gone. Returns how many tiles it
    /// reached, for the status line.</summary>
    internal static int EndRebuild()
    {
        for (int t = 0; t < _count; t++)
            if (!_freed[t] && _gen[t] != _rebuildGen) Release(t);
        Compact();
        Moved();
        return _rebuiltTiles;
    }

    /// <summary><c>Part::~Part</c> for a part that may be a tile's liveness witness: free the tile rather than
    /// keep dereferencing a <c>TileData</c> whose last part just went. Compares the pointer, never
    /// dereferences it. O(1) -- an area teardown destroys parts by the thousand.</summary>
    internal static void PartDied(IntPtr part)
    {
        if (!_ready || part == IntPtr.Zero) return;
        int t = WitnessTile(part);
        if (t < 0) return;
        _witnessDeaths++;
        Release(t);
        Moved();
    }

    private static void InternWitness(IntPtr part, int t)
    {
        int h = Bucket(part);
        for (int probe = 0; probe <= _hashMask; probe++, h = (h + 1) & _hashMask)
        {
            if (_witnessStamp[h] == _rebuildGen && _witnessSlot[h] >= 0
                && _witness[_witnessSlot[h]] == part) return;
            if (_witnessStamp[h] == _rebuildGen) continue;
            _witnessStamp[h] = _rebuildGen; _witnessSlot[h] = t;
            return;
        }
    }

    private static int WitnessTile(IntPtr part)
    {
        int h = Bucket(part);
        for (int probe = 0; probe <= _hashMask; probe++, h = (h + 1) & _hashMask)
        {
            if (_witnessStamp[h] != _rebuildGen) return -1;
            int t = _witnessSlot[h];
            if ((uint)t < (uint)_cap && _witness[t] == part) return t;
        }
        return -1;
    }

    /// <summary>A new Scene: every tile belongs to the old one.</summary>
    internal static void Reset()
    {
        _count = 0; _freeN = 0;
        Array.Fill(_slot, -1);
        Array.Clear(_tile); Array.Clear(_freed); Array.Clear(_gen); Array.Clear(_fadeBoxN);
        Array.Clear(_fadeBoxOver); Array.Clear(_witness); Array.Clear(_witnessStamp);
        Moved();
    }

    // ---- the per-frame step ----------------------------------------------------------------------

    /// <summary>Once per world frame, from <see cref="RspShim.Run"/>, ahead of the frame anchor -- the slot the
    /// two <c>HandleFading*Bucket</c> calls held at the end of <c>ManageSceneBSP</c>, moved in front of the
    /// draw so this frame draws this frame's factors instead of the previous frame's.</summary>
    internal static void Tick(IntPtr scene)
    {
        if (!_ready || !Buckets.TileFade.V || scene == IntPtr.Zero || _count == 0) return;
        _ticks++;
        float dt = *_deltaT;
        if (!(dt > 0f)) return;

        var cam = new Camera(AlloyCamera.World);
        if (cam.IsNull) return;
        Vector3 camPos = cam.Pos;
        float aspect = cam.ViewH > 0f ? cam.ViewW / cam.ViewH : 1f;
        fixed (float* planes = _planes)
        {
            ShadowMath.ReceiverPlanes(camPos, FrameRead.ViewForward(cam.Rot), cam.ViewAngle, aspect,
                                      cam.Near, cam.Far, 0f, new Span<float>(planes, PlaneFloats));
            _mode = *_geomFadeMode;
            _planeOk = OcclusionPlane(cam, camPos, out float nx, out float ny, out float pd);
            bool liveWas = _fadeLive;
            _fadeLive = FadeLive(_mode, _planeOk);
            // BW4/BW1: whether any factor this frame's readers see actually moved. A city with the
            // second-story option off steps nothing for minutes at a time, and that is what lets the route
            // reuse a verdict rather than re-deriving it per slot.
            bool moved = _fadeLive != liveWas;
            for (int t = 0; t < _count; t++)
            {
                if (_freed[t]) continue;
                bool inView = BoxVisible(_boxMin[t], _boxMax[t], planes);
                float was = _explored[t];
                if (new TileData(_tile[t]).FoggedByWar != 0) _explored[t] = 0f;
                else if (inView && _explored[t] < 1f)
                {
                    _explored[t] = MathF.Min(1f, _explored[t] + dt * Rate);
                    _revealing++;
                }
                if (_explored[t] != was) moved = true;
                if (!_fadeLive || !inView || _fadeBoxN[t] == 0) continue;
                _stepped++;
                bool occluding = _mode == ModeAlways || Occludes(t, nx, ny, pd);
                if (occluding) _fadingOut++;
                float fadeWas = _fade[t];
                Step(ref _fade[t], ref _acc[t], occluding, dt);
                if (_fade[t] != fadeWas) moved = true;
            }
            if (moved) Moved();
        }
        if (Buckets.Validate.V) BucketValidator.CheckTiles(_tile, _freed, _fade, _explored, _count);
    }

    /// <summary>The engine's plane, term for term: the unit camera-to-target direction flattened into XY,
    /// through the target. False = no follow target, or the camera looks down too steeply -- both of which are
    /// <c>Scene+0x1a0 = 1</c> and stand the fade down, holding every factor where it is.</summary>
    private static bool OcclusionPlane(Camera cam, Vector3 camPos, out float nx, out float ny, out float d)
    {
        nx = 0f; ny = 0f; d = 0f;
        CAurBehavior hv = cam.BehaviorHost;
        // No vtable yet means AsBehaviorCameraFollow has nothing to call through -- the engine's own guard.
        if (hv.IsNull || *(IntPtr*)hv.Ptr == IntPtr.Zero || hv.AsBehaviorCameraFollowFn == IntPtr.Zero) return false;
        CAurBehaviorCameraFollow follow = hv.AsBehaviorCameraFollow();
        if (follow.IsNull) return false;
        IntPtr target = follow.Target;
        if (target == IntPtr.Zero) return false;
        return Plane(new Gob(target).Position, camPos, out nx, out ny, out d);
    }

    /// <summary>The plane maths on its own, so the SelfCheck can pin it to numbers.</summary>
    internal static bool Plane(Vector3 target, Vector3 camPos, out float nx, out float ny, out float d)
    {
        nx = 0f; ny = 0f; d = 0f;
        Vector3 delta = target - camPos;
        float len = delta.Length();
        if (!(len > 1e-4f)) return false;
        Vector3 u = delta / len;
        float flat = MathF.Sqrt(u.X * u.X + u.Y * u.Y);
        if (flat < MinHorizontal) return false;
        float inv = 1f / flat;
        nx = u.X * inv; ny = u.Y * inv;
        d = -(target.X * nx + target.Y * ny);
        return true;
    }

    /// <summary>Does tile <paramref name="t"/> stand between the camera and the player? True when ANY of its
    /// <c>tilefade 1</c> parts fails to be entirely beyond the plane, which is <c>CheckFadePlane</c>'s
    /// loop.</summary>
    private static bool Occludes(int t, float nx, float ny, float d)
    {
        int n = _fadeBoxN[t], b = t * FadePartsPerTile;
        for (int i = 0; i < n; i++)
        {
            Vector4 q = _fadeBox[b + i];
            if (!TwoDBoxAbovePlane(q.X, q.Y, q.Z, q.W, nx, ny, d)) return true;
        }
        if (!_fadeBoxOver[t]) return false;
        Vector3 lo = _boxMin[t], hi = _boxMax[t];
        return !TwoDBoxAbovePlane(lo.X, lo.Y, hi.X, hi.Y, nx, ny, d);
    }

    /// <summary><c>TwoDBoxAbovePlane</c> @0x47a0b0: all FOUR corners of the XY rectangle strictly on the
    /// plane's positive side. Four and not two -- a box straddling the plane diagonally passes a
    /// min/max-only test.</summary>
    internal static bool TwoDBoxAbovePlane(float minX, float minY, float maxX, float maxY,
                                           float nx, float ny, float d)
    {
        float a = minX * nx, b = minY * ny, c = maxX * nx, e = maxY * ny;
        return a + b + d > 0f && c + e + d > 0f && e + a + d > 0f && c + b + d > 0f;
    }

    /// <summary>One tile's fade step: <c>HandleFadingOutBucket</c> @0x47f870 and
    /// <c>HandleFadingInBucket</c> @0x47f670, term for term. The counter moves only while it is inside the
    /// band, so it is bounded at +/-(0.1 + dt), and switching intent restarts it from 0 rather than from the
    /// other end -- which is what makes the debounce symmetric.</summary>
    internal static void Step(ref float fade, ref float acc, bool fadingOut, float dt)
    {
        if (fadingOut)
        {
            if (acc <= Hysteresis) acc = MathF.Max(acc, 0f) + dt;
            if (acc > Hysteresis) fade = MathF.Max(0f, fade - dt * Rate);
        }
        else
        {
            if (acc >= -Hysteresis) acc = MathF.Min(acc, 0f) - dt;
            if (acc < -Hysteresis) fade = MathF.Min(1f, fade + dt * Rate);
        }
    }

    /// <summary>Is the AABB on the inside of every plane? The positive-vertex test: only the corner furthest
    /// along each normal can save the box. A tile whose box was never filled in (min &gt; max) is kept.</summary>
    private static bool BoxVisible(Vector3 min, Vector3 max, float* planes)
    {
        if (min.X > max.X) return true;
        for (int i = 0; i < 6; i++)
        {
            float nx = planes[i * 4], ny = planes[i * 4 + 1], nz = planes[i * 4 + 2], d = planes[i * 4 + 3];
            float px = nx >= 0f ? max.X : min.X;
            float py = ny >= 0f ? max.Y : min.Y;
            float pz = nz >= 0f ? max.Z : min.Z;
            if (nx * px + ny * py + nz * pz + d < 0f) return false;
        }
        return true;
    }

    // ---- what the route reads --------------------------------------------------------------------

    /// <summary>The occlusion fade factor of <paramref name="tile"/> (a <c>TileData*</c>), 0..1; 1 for a tile
    /// this does not know, which is the honest answer for one the registry has not reached yet.</summary>
    internal static float Fade(IntPtr tile)
    {
        if (tile == IntPtr.Zero) return 1f;
        if (!Owned) return Clamp01(new TileData(tile).Fade);
        return FadeAt(Find(tile));
    }

    /// <summary>BW4: the same factor from an already-resolved slot (see <see cref="Gen"/>). -1 = a tile the
    /// registry does not know, which is opaque.</summary>
    internal static float FadeAt(int t)
        => !_fadeLive || (uint)t >= (uint)_cap ? 1f : _fade[t];

    /// <summary>...and the exploration factor. Not gated on <see cref="FadeLive"/>: the fog-of-war ramp is not
    /// the second-story option's.</summary>
    internal static float ExploredAt(int t) => (uint)t >= (uint)_cap ? 1f : _explored[t];

    /// <summary>Is the registry the factors' source this session? False stands the whole by-slot path down --
    /// the caller reads the engine's own <c>TileData</c> fields by pointer, as <c>/buckets tilefade 0</c>
    /// does.</summary>
    internal static bool Owned => _ready && Buckets.TileFade.V;

    /// <summary>BW4: the registry slot of a tile, to be kept only as long as <see cref="Gen"/> holds. -1 for a
    /// tile the registry has not reached (or a null one), which every by-slot reader answers as opaque and
    /// explored.</summary>
    internal static int SlotOf(IntPtr tile) => tile == IntPtr.Zero ? -1 : Find(tile);

    /// <summary><c>ProcessTriMeshParts</c> @0x480d40's gate: does a <c>tilefade 1</c> part take its tile's factor
    /// this frame? Never under "never"; always under "always"; under "automatic" only while the occlusion plane
    /// stands (a follow target, and a camera not looking straight down) -- otherwise the roof draws opaque.
    /// </summary>
    internal static bool FadeLive(int mode, bool planeOk) => (mode == ModeAuto && planeOk) || mode == ModeAlways;

    /// <summary>The fog-of-war exploration factor of <paramref name="tile"/>, 0..1; 1 for none.</summary>
    internal static float Explored(IntPtr tile)
    {
        if (tile == IntPtr.Zero) return 1f;
        if (!Owned) return Clamp01(new TileData(tile).GeomFade);
        return ExploredAt(Find(tile));
    }

    private static float Clamp01(float f) => f >= 1f ? 1f : f > 0f ? f : 0f;

    /// <summary>The clause the <c>buckets</c> knob group prints.</summary>
    internal static string Note()
        => !_ready ? $"tile fade stood down ({Missing} unresolved)"
         : _count == 0 ? "no tiles registered"
         : $"{_count} tile(s) of {_cap}, {(_ticks == 0 ? 0 : _stepped / _ticks)} classified and "
           + $"{(_ticks == 0 ? 0 : _fadingOut / _ticks)} fading out per frame, {_revealing} reveal step(s)"
           + (_grewPastBound > 0 ? $", GREW PAST THE DERIVED BOUND {_grewPastBound}x (a bug -- see the log)" : "")
           + (_capped > 0 ? $", REFUSED {_capped} registration(s) at the {MaxTilesCeiling} ceiling -- those "
                          + "tiles never fade" : "")
           + (_reused > 0 ? $", {_reused} slot(s) reused from the pool" : "")
           + (_witnessDeaths > 0 ? $", {_witnessDeaths} tile(s) dropped on a part death" : "")
           + (_mode == ModeNever ? ", second-story tiles never hidden (the game option)"
              : _mode == ModeAlways ? ", second-story tiles always hidden (the game option)"
              : _planeOk ? "" : ", fade stood down (no follow target, or the camera looks straight down)");

    // ---- table plumbing --------------------------------------------------------------------------

    private static int Bucket(IntPtr tile) => (int)((ulong)tile >> 4) & _hashMask;

    private static int Find(IntPtr tile)
    {
        int h = Bucket(tile);
        for (int probe = 0; probe <= _hashMask; probe++, h = (h + 1) & _hashMask)
        {
            int t = _slot[h];
            if (t < 0) return -1;
            if (_tile[t] == tile && !_freed[t]) return t;
        }
        return -1;
    }

    /// <summary>Claim a slot: a freed one on the probe chain first, else a fresh index, else one out of the
    /// pool. -1 = every slot is LIVE. A pointer match on a freed slot is a MISS in <see cref="Find"/> and the
    /// slot is reclaimed here, so a recycled <c>TileData</c> address is never answered for by the tile that
    /// used to live there (the #221 bug class).</summary>
    private static int Insert(IntPtr tile)
    {
        int h = Bucket(tile);
        for (int probe = 0; probe <= _hashMask; probe++, h = (h + 1) & _hashMask)
        {
            int t = _slot[h];
            if (t >= 0)
            {
                if (!_freed[t]) continue;
                _tile[t] = tile; _freed[t] = false;
                return t;
            }
            if (_count < _cap) t = _count++;
            else if (_freeN > 0) { t = _freeTile[--_freeN]; _reused++; }
            else return -1;
            _slot[h] = t;
            _tile[t] = tile; _freed[t] = false;
            return t;
        }
        return -1;
    }

    /// <summary>Return a slot to the pool. It stays FREED, so a pointer match on it is still a miss until it
    /// is claimed again.</summary>
    private static void Release(int t)
    {
        _freed[t] = true;
        _witness[t] = IntPtr.Zero;
        if (_freeN < _cap) _freeTile[_freeN++] = t;
    }

    /// <summary>Renumber the live tiles densely and rebuild both maps over them, at the end of every rebuild.
    /// After this <see cref="_count"/> IS the live tile count, the pool is empty, and no hash slot references
    /// a dead tile -- so neither the array nor the probe chains creep. Every per-tile column moves with the
    /// slot, the fade-part slab included, and nothing outside holds a tile index across it: the indices
    /// <see cref="NoteTile"/> hands out are consumed by <see cref="NoteTilePart"/> inside the same
    /// traversal, which has finished by the time this runs.</summary>
    private static void Compact()
    {
        _compactions++;
        int live = 0;
        for (int t = 0; t < _count; t++)
        {
            if (_freed[t]) continue;
            if (live != t)
            {
                _tile[live] = _tile[t]; _gen[live] = _gen[t];
                _boxMin[live] = _boxMin[t]; _boxMax[live] = _boxMax[t];
                _fadeBoxN[live] = _fadeBoxN[t]; _fadeBoxOver[live] = _fadeBoxOver[t];
                _fade[live] = _fade[t]; _acc[live] = _acc[t]; _explored[live] = _explored[t];
                _witness[live] = _witness[t];
                int n = _fadeBoxN[t];
                for (int i = 0; i < n; i++)
                    _fadeBox[live * FadePartsPerTile + i] = _fadeBox[t * FadePartsPerTile + i];
                _freed[live] = false;
            }
            live++;
        }
        for (int t = live; t < _count; t++)
        {
            _tile[t] = IntPtr.Zero; _freed[t] = false; _witness[t] = IntPtr.Zero; _fadeBoxN[t] = 0;
        }
        _count = live;
        _freeN = 0;
        Rehash();
    }

    /// <summary>Point the first free hash slot on this tile's probe chain at it. Only ever called on a table
    /// whose slots were just cleared, so it cannot displace a live reference.</summary>
    private static void Link(int t)
    {
        int h = Bucket(_tile[t]);
        for (int probe = 0; probe <= _hashMask; probe++, h = (h + 1) & _hashMask)
            if (_slot[h] < 0) { _slot[h] = t; return; }
    }

    /// <summary>The table is full at a capacity <see cref="AreaBound"/> said would hold the area: the bound is
    /// WRONG. Grow anyway -- a refused tile is a roof that never fades and (before the scene walk's own fix) a
    /// tile whose emitters never register -- and say so once, with the census that derived the bound.
    /// Returns whether there is now room.</summary>
    private static bool GrowPastBound()
    {
        int was = _cap;
        Ensure(_cap + 1);
        // At the ceiling there is nothing to report but the refusal: counting the attempt here would print
        // GREW PAST and REFUSED for the same tile and name two bugs where there is one.
        if (_cap == was) return false;
        _grewPastBound++;
        if (!_grewLogged)
        {
            _grewLogged = true;
            CNWCArea a = Area();
            _log?.Always($"TileFadeRamp: {_count} tile(s) registered past the {was} the area's own census "
                       + $"derived ({(a.IsNull ? "no area, or its layout rows did not resolve"
                                              : $"{a.TileWidth}x{a.TileHeight} grid")}, border ring "
                       + $"{AreaBorder.Ring}) -- the derived bound is wrong. The table grows so the area still "
                       + "fades; the bound in TileFadeRamp.AreaBound wants fixing against this area");
        }
        return _count < _cap || _freeN > 0;
    }

    /// <summary>Past <see cref="MaxTilesCeiling"/> there is nowhere left to grow: the tile is REFUSED, it
    /// never fades, and <see cref="NoteTile"/> hands back -1.</summary>
    private static void Cap()
    {
        _capped++;
        if (_cappedLogged) return;
        _cappedLogged = true;
        _log?.Always($"TileFadeRamp: the absolute tile ceiling ({MaxTilesCeiling}) is full -- the rest of this "
                   + "area's tiles draw at full opacity and never fade. That is a real bound being hit, not a "
                   + "transient: no shipped area and border ring can reach it, so something is registering "
                   + "tiles that are not tiles");
    }

    // ---- the check -------------------------------------------------------------------------------

    /// <summary>Off-device: the plane, the four-corner box test and the debounced ramp, pinned to numbers
    /// derived from the decompilation rather than from this implementation.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TileFadeRamp.SelfCheck");

        // The plane: camera at the origin, player 10 units along +X and 5 up. The unit direction's horizontal
        // component is 10/sqrt(125) = 0.894 -- well past 0.4 -- so the plane is x = 10 facing +X.
        bool ok = Plane(new Vector3(10f, 0f, 5f), Vector3.Zero, out float nx, out float ny, out float d);
        check.Expect(ok && MathF.Abs(nx - 1f) < 1e-5f && MathF.Abs(ny) < 1e-5f && MathF.Abs(d + 10f) < 1e-3f,
                     "the plane is the flattened camera-to-target direction, through the target");
        check.Expect(!Plane(new Vector3(0f, 0f, -10f), Vector3.Zero, out _, out _, out _),
                     "a straight-down camera stands the fade down");
        // 0.4 is the threshold on the NORMALIZED direction: 4 out along x, sqrt(84) down, so the unit
        // vector's |xy| is 4/10 = 0.4 exactly, which the engine's strict `< 0.4` keeps.
        check.Expect(Plane(new Vector3(4f, 0f, -MathF.Sqrt(84f)), Vector3.Zero, out _, out _, out _),
                     "the 0.4 horizontal threshold is on the unit direction and is not exclusive at 0.4");
        check.Expect(!Plane(new Vector3(3.5f, 0f, -MathF.Sqrt(100f - 3.5f * 3.5f)), Vector3.Zero, out _, out _, out _),
                     "...and under it the fade is down");

        // The four-corner test, against the x = 10 plane (n = +X, d = -10).
        check.Expect(TwoDBoxAbovePlane(11f, -1f, 12f, 1f, 1f, 0f, -10f), "a box entirely beyond the player is above");
        check.Expect(!TwoDBoxAbovePlane(8f, -1f, 9f, 1f, 1f, 0f, -10f), "a box entirely in front of the player is not");
        check.Expect(!TwoDBoxAbovePlane(9f, -1f, 11f, 1f, 1f, 0f, -10f), "a straddling box is not");
        // The corner a min/max-only test gets wrong: normal along the diagonal, and the box corner that fails
        // is (min.x, min.y) rather than either extreme of the box along one axis.
        float s = 1f / MathF.Sqrt(2f);
        check.Expect(!TwoDBoxAbovePlane(0f, 10f, 10f, 20f, s, s, -12f),
                     "the corner a two-corner test would pass is tested");

        // The ramp. dt = 0.05 s: the first two steps are inside the 0.1 s band and must not move the factor.
        float fade = 1f, acc = 0f;
        Step(ref fade, ref acc, true, 0.05f);
        check.Expect(fade == 1f && MathF.Abs(acc - 0.05f) < 1e-6f, "the first step only charges the debounce");
        Step(ref fade, ref acc, true, 0.05f);
        check.Expect(fade == 1f && MathF.Abs(acc - 0.1f) < 1e-6f, "...and so does the step that reaches 0.1");
        Step(ref fade, ref acc, true, 0.05f);
        check.Expect(MathF.Abs(acc - 0.15f) < 1e-6f && MathF.Abs(fade - 0.8f) < 1e-5f,
                     "past the band the factor moves at 4.0/s");
        for (int i = 0; i < 8; i++) Step(ref fade, ref acc, true, 0.05f);
        check.Expect(fade == 0f, "the fade clamps at 0");
        // Turning round: the counter restarts from 0, so the ramp back needs its own 0.1 s.
        Step(ref fade, ref acc, false, 0.05f);
        check.Expect(fade == 0f && MathF.Abs(acc + 0.05f) < 1e-6f, "reversing restarts the debounce from 0");
        Step(ref fade, ref acc, false, 0.05f);
        Step(ref fade, ref acc, false, 0.05f);
        check.Expect(MathF.Abs(fade - 0.2f) < 1e-5f, "...and then it ramps back in at the same 4.0/s");
        for (int i = 0; i < 8; i++) Step(ref fade, ref acc, false, 0.05f);
        check.Expect(fade == 1f, "the fade clamps at 1");

        RebuildCheck(check);

        log.Info(check.Ok ? "TileFadeRamp.SelfCheck: passed" : "TileFadeRamp.SelfCheck: FAILED");
        bool parts = SceneParts.SelfCheck(log);
        bool val = BucketValidator.SelfCheck(log);
        bool prep = ScenePrep.SelfCheck(log);
        return check.Ok && parts && val && prep;
    }

    /// <summary>The registry's rebuild and sweep, over synthetic engine memory: two tiles, one with a
    /// <c>tilefade 1</c> part and one without, then a rebuild that reaches only the first. Everything here is
    /// stack memory laid out by the same <see cref="EngineLayout"/> rows the live reads use, so a wrong offset
    /// fails this rather than showing up as a roof that will not fade.</summary>
    private static void RebuildCheck(Checker check)
    {
        // SIZES, not field reads: the smallest fake object each read fits inside.
        int tileBytes = EngineLayout.TileFoggedByWar + 1;
        int partBytes = EngineLayout.PartTile + sizeof(IntPtr);
        int mdlBytes = EngineLayout.MdlTileFade + sizeof(int);
        byte* tileA = stackalloc byte[tileBytes], tileB = stackalloc byte[tileBytes];
        byte* fadePart = stackalloc byte[partBytes], plainPart = stackalloc byte[partBytes];
        byte* fadeMdl = stackalloc byte[mdlBytes], plainMdl = stackalloc byte[mdlBytes];
        for (int i = 0; i < tileBytes; i++) { tileA[i] = 0; tileB[i] = 0; }
        for (int i = 0; i < partBytes; i++) { fadePart[i] = 0; plainPart[i] = 0; }
        for (int i = 0; i < mdlBytes; i++) { fadeMdl[i] = 0; plainMdl[i] = 0; }

        new MdlNodeTriMesh((IntPtr)fadeMdl).TileFade = TileFadeFades;
        new MdlNodeTriMesh((IntPtr)plainMdl).TileFade = 0;
        var fp = new PartTriMesh((IntPtr)fadePart);
        fp.BboxMin = new Vector3(20f, -1f, 0f); fp.BboxMax = new Vector3(21f, 1f, 4f);
        new CX.Engine.Part((IntPtr)fadePart).Node = new MdlNode((IntPtr)fadeMdl);
        var pp = new PartTriMesh((IntPtr)plainPart);
        pp.BboxMin = new Vector3(0f, 0f, 0f); pp.BboxMax = new Vector3(1f, 1f, 1f);
        new CX.Engine.Part((IntPtr)plainPart).Node = new MdlNode((IntPtr)plainMdl);

        bool savedReady = _ready;
        int savedCount = _count, savedGen = _rebuildGen;
        _ready = true;
        Ensure(MinTiles);
        _count = 0;
        Array.Fill(_slot, -1);
        Array.Clear(_tile); Array.Clear(_freed); Array.Clear(_gen); Array.Clear(_fadeBoxN);
        Array.Clear(_fadeBoxOver); Array.Clear(_witness); Array.Clear(_witnessStamp);
        Moved();

        BeginRebuild();
        int a = NoteTile((IntPtr)tileA, out bool freshA);
        int aAgain = NoteTile((IntPtr)tileA, out bool freshAgain);
        int b = NoteTile((IntPtr)tileB, out bool freshB);
        check.Expect(a >= 0 && b >= 0 && a != b, "two tiles take two registry slots");
        check.Expect(freshA && freshB && aAgain == a && !freshAgain,
                     "a tile is fresh on its first sighting of a rebuild and not on the second");
        NoteTilePart(a, (IntPtr)fadePart);
        NoteTilePart(a, (IntPtr)plainPart);
        NoteTilePart(b, (IntPtr)plainPart);
        check.Expect(_fadeBoxN[a] == 1 && _fadeBoxN[b] == 0,
                     "only a tilefade-1 part becomes an occlusion surface");
        check.Expect(!FadeLive(ModeNever, true) && FadeLive(ModeAlways, false) && FadeLive(ModeAuto, true)
                     && !FadeLive(ModeAuto, false),
                     "the second-story option gates the fade: never, always, or the plane test while it stands");
        check.Expect(Fades((IntPtr)fadePart) && !Fades((IntPtr)plainPart),
                     "only a tilefade-1 part takes its tile's occlusion fade -- the floor under a roof stays drawn");
        check.Expect(_boxMin[a] == new Vector3(0f, -1f, 0f) && _boxMax[a] == new Vector3(21f, 1f, 4f),
                     "the tile box is the union of its parts' world boxes");
        check.Expect(EndRebuild() == 2, "the rebuild reports the tiles it reached");
        check.Expect(Fade((IntPtr)tileA) == 1f && Explored((IntPtr)tileA) == 1f,
                     "a fresh tile starts opaque and explored");

        // A step with the plane at x = 10 facing +X: tile A's only fade part sits beyond it, so A is fading IN
        // and its factor does not move off 1.
        Step(ref _fade[a], ref _acc[a], Occludes(a, 1f, 0f, -10f), 0.2f);
        check.Expect(_fade[a] == 1f && _acc[a] < 0f, "a tile beyond the plane is classified fading in");
        // ...and with the plane at x = 30 it is in front of the player, so it fades out.
        _acc[a] = 0.2f;
        Step(ref _fade[a], ref _acc[a], Occludes(a, 1f, 0f, -30f), 0.2f);
        check.Expect(_fade[a] < 1f, "a tile in front of the player fades out");

        // A rebuild that reaches only A drops B, and A keeps its factor rather than snapping back. Indices are
        // re-looked-up after every EndRebuild on purpose -- the compaction renumbers the live tiles, and a
        // check that cached one would be asserting about whatever moved into it.
        float held = _fade[a];
        BeginRebuild();
        int a2 = NoteTile((IntPtr)tileA, out _);
        NoteTilePart(a2, (IntPtr)fadePart);
        EndRebuild();
        a2 = SlotOf((IntPtr)tileA);
        check.Expect(a2 >= 0 && SlotOf((IntPtr)tileB) < 0, "the sweep drops a tile the rebuild did not reach");
        check.Expect(_count == 1, "...and the compaction gives its slot back rather than keeping the high mark");
        check.Expect(_fade[a2] == held, "a tile that survives a rebuild keeps its fade");
        check.Expect(Fade((IntPtr)tileB) == 1f, "a swept tile answers as unknown, which is opaque");

        // The witness: the part that first grew tile A's box is what keeps A safe to dereference, and its
        // death frees A rather than leaving a freed TileData in the table.
        check.Expect(_witness[a2] == (IntPtr)fadePart, "a tile's first mesh part becomes its liveness witness");
        PartDied((IntPtr)fadePart);
        check.Expect(_freed[a2] && Fade((IntPtr)tileA) == 1f,
                     "the witness's death frees the tile, which then answers as unknown");
        PartDied((IntPtr)plainPart);
        check.Expect(_witnessDeaths == 1, "a part that is nobody's witness frees nothing");

        // The fog-of-war seed: a tile the area calls fogged starts at 0 rather than 1.
        *(byte*)(tileB + EngineLayout.TileFoggedByWar) = 1;
        BeginRebuild();
        NoteTile((IntPtr)tileB, out _);
        EndRebuild();
        int c = SlotOf((IntPtr)tileB);
        check.Expect(c >= 0 && _explored[c] == 0f, "a fogged tile is seeded unexplored");

        // The leak the review found: churn far past the cap and every new tile must still register. Without
        // the pool and the compaction _count is a HIGH-WATER MARK, so this stops handing out slots a third of
        // the way in and the rest of the area never fades. The churn tiles are real addresses inside one
        // buffer, 16 bytes apart so each hashes to its own bucket -- NoteTile seeds the reveal factor off the
        // tile's fog byte, so a synthetic address that cannot be read is not a usable stand-in.
        const int ChurnStride = 16;
        int churn = MinTiles * 3, refused = 0;
        byte[] churnMem = new byte[(long)churn * ChurnStride + tileBytes];
        fixed (byte* pool = churnMem)
        {
            for (int i = 0; i < churn; i++)
            {
                BeginRebuild();
                if (NoteTile((IntPtr)(pool + (long)i * ChurnStride), out _) < 0) refused++;
                EndRebuild();   // reaches nothing else, so every earlier tile is swept and its slot returned
            }
            check.Expect(refused == 0,
                         $"{churn} tiles of sweep churn register without exhausting the {_cap} slots");
            check.Expect(_count <= 1, "...and the live count tracks what is live, not what was ever inserted");

            // ...and the same churn with no sweep in between, which is the pool's own path.
            _count = 0; _freeN = 0; Array.Fill(_slot, -1);
            Array.Clear(_tile); Array.Clear(_freed); Array.Clear(_witness); Array.Clear(_witnessStamp);
            BeginRebuild();
            refused = 0;
            for (int i = 0; i < churn; i++)
            {
                IntPtr fake = (IntPtr)(pool + (long)i * ChurnStride);
                int s = NoteTile(fake, out _);
                if (s < 0) { refused++; continue; }
                _witness[s] = fake; InternWitness(fake, s);
                PartDied(fake);
            }
            check.Expect(refused == 0, "insert-and-die churn past the cap is served from the pool");

            // BUG-TILES: the area the table used to refuse. An area of 20x20 tiles inside the default border
            // ring is 2500 live tiles, which the old 2048-slot const could not hold -- it registered 2048 and
            // dropped the rest for the life of the area (live: `2048 tile(s), CAPPED 40128840x`). All of them
            // must register in ONE rebuild, the table growing rather than refusing, and the ceiling refusal
            // must stay reachable so nothing quietly stops being a bound.
            const int Side = 20;
            int live = (Side + 2 * AreaBorder.DefaultRing) * (Side + 2 * AreaBorder.DefaultRing);
            check.Expect(live > MinTiles, "a 20x20 area inside the default border ring outgrows the starting table");
            _count = 0; _freeN = 0; Array.Fill(_slot, -1);
            Array.Clear(_tile); Array.Clear(_freed); Array.Clear(_witness); Array.Clear(_witnessStamp);
            long grewWas = _grewPastBound, cappedWas = _capped;
            bool logWas = _grewLogged;
            _grewLogged = true;   // the stub host has no area to derive a bound from: its growth is not a bug report
            BeginRebuild();
            Ensure(live);         // what a real area's BeginRebuild does, from AreaBound
            refused = 0;
            for (int i = 0; i < live && i < churn; i++)
                if (NoteTile((IntPtr)(pool + (long)i * ChurnStride), out _) < 0) refused++;
            check.Expect(refused == 0 && _count == Math.Min(live, churn),
                         $"all {live} tiles of a 20x20 area with a {AreaBorder.DefaultRing}-tile border ring register");
            check.Expect(_cap >= live && _grewPastBound == grewWas && _capped == cappedWas,
                         "...in a table sized for them, with nothing refused and no overflow reported");
            // ...and one more tile than the table holds GROWS rather than being dropped: a refusal is only ever
            // the absolute ceiling, and the bound being wrong is a reported bug, not a silent cap.
            int capWas = _cap;
            _count = _cap;        // a full table, without minting another _cap synthetic tiles to fill it
            check.Expect(NoteTile((IntPtr)(pool + (long)churn * ChurnStride), out _) >= 0 && _cap > capWas
                         && _grewPastBound > grewWas && _capped == cappedWas,
                         "a tile past the derived bound grows the table and is reported, never dropped");
            EndRebuild();
            _grewPastBound = grewWas; _grewLogged = logWas; _capped = cappedWas;
        }

        _count = savedCount; _ready = savedReady; _rebuildGen = savedGen;
        Array.Fill(_slot, -1);
        Array.Clear(_tile); Array.Clear(_freed); Array.Clear(_gen); Array.Clear(_fadeBoxN);
        Array.Clear(_fadeBoxOver); Array.Clear(_witness); Array.Clear(_witnessStamp);
        _count = 0; _witnessDeaths = 0;
        Moved();
    }
}
