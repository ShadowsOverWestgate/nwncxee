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

    /// <summary>An NWN area is at most 32x32 tiles; this is double that, with room for a tileset that spawns
    /// more than one <c>TileData</c> per cell.</summary>
    private const int MaxTiles = 2048;
    /// <summary>Per-tile slab of <c>tilefade 1</c> part boxes. Stock roofs are one to three pieces; past this
    /// the tile falls back to its own union box, which fades MORE readily rather than less.</summary>
    private const int FadePartsPerTile = 8;
    private const int HashSize = 1 << 12;
    private const int HashMask = HashSize - 1;
    private const int PlaneFloats = 24;

    private static readonly IntPtr[] _tile = new IntPtr[MaxTiles];
    private static readonly int[] _slot = new int[HashSize];
    private static readonly bool[] _freed = new bool[MaxTiles];
    private static readonly int[] _gen = new int[MaxTiles];
    private static readonly Vector3[] _boxMin = new Vector3[MaxTiles];
    private static readonly Vector3[] _boxMax = new Vector3[MaxTiles];
    /// <summary>XY boxes of this tile's <c>tilefade 1</c> parts: (minX, minY, maxX, maxY).</summary>
    private static readonly Vector4[] _fadeBox = new Vector4[MaxTiles * FadePartsPerTile];
    private static readonly int[] _fadeBoxN = new int[MaxTiles];
    /// <summary>This tile had more <c>tilefade 1</c> parts than the slab holds, so the occlusion test also
    /// uses its union box -- conservative on purpose (see <see cref="FadePartsPerTile"/>).</summary>
    private static readonly bool[] _fadeBoxOver = new bool[MaxTiles];
    /// <summary>The two factors, and the intent counter behind the first of them.</summary>
    private static readonly float[] _fade = new float[MaxTiles];
    private static readonly float[] _acc = new float[MaxTiles];
    private static readonly float[] _explored = new float[MaxTiles];
    /// <summary>Tile slots back in the pool. Same reason <see cref="SceneParts"/> keeps one: without it
    /// <see cref="_count"/> is a high-water mark, not a capacity -- <see cref="Insert"/> reclaims a freed slot
    /// only when it lands on the new tile's probe chain, so a session that keeps replacing tiles would walk
    /// the array to <see cref="MaxTiles"/> and then stop registering any tile whose chain reaches an empty
    /// hash bucket. Lower churn than the part table, identical shape.</summary>
    private static readonly int[] _freeTile = new int[MaxTiles];
    private static int _freeN;
    private static int _count;
    private static long _reused, _compactions;

    /// <summary>Six inward-facing camera planes (xyz = unit normal, w = offset), rebuilt each tick.</summary>
    private static readonly float[] _planes = new float[PlaneFloats];

    private static ExtenderLog? _log;
    private static float* _deltaT;
    private static int* _geomFadeMode;
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
    private static readonly IntPtr[] _witness = new IntPtr[MaxTiles];
    private static readonly int[] _witnessSlot = new int[HashSize];
    private static readonly int[] _witnessStamp = new int[HashSize];
    private static long _witnessDeaths;

    /// <summary>One-entry memo for <see cref="Slot"/>: a route walks a tile's parts together, so the same tile
    /// is asked for many times in a row.</summary>
    private static IntPtr _memoTile;
    private static int _memoSlot = -1;

    /// <summary>The first symbol or layout that did not resolve, or null.</summary>
    internal static string? Missing { get; private set; }

    internal static void Resolve(ExtenderLog log)
    {
        _log = log;
        _deltaT = (float*)Native.Sym(SymbolMap.DeltaT);
        _geomFadeMode = (int*)Native.Sym(SymbolMap.GeomFadeEnabled);
        Array.Fill(_slot, -1);
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
        if (t < 0) { Cap("tiles"); return -1; }
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
        if (!_ready || (uint)t >= MaxTiles || tri == IntPtr.Zero) return;
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
        _memoTile = IntPtr.Zero; _memoSlot = -1;
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
        if (_memoSlot == t) { _memoTile = IntPtr.Zero; _memoSlot = -1; }
    }

    private static void InternWitness(IntPtr part, int t)
    {
        int h = Bucket(part);
        for (int probe = 0; probe <= HashMask; probe++, h = (h + 1) & HashMask)
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
        for (int probe = 0; probe <= HashMask; probe++, h = (h + 1) & HashMask)
        {
            if (_witnessStamp[h] != _rebuildGen) return -1;
            int t = _witnessSlot[h];
            if ((uint)t < MaxTiles && _witness[t] == part) return t;
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
        _memoTile = IntPtr.Zero; _memoSlot = -1;
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
            _fadeLive = FadeLive(_mode, _planeOk);
            for (int t = 0; t < _count; t++)
            {
                if (_freed[t]) continue;
                bool inView = BoxVisible(_boxMin[t], _boxMax[t], planes);
                if (new TileData(_tile[t]).FoggedByWar != 0) _explored[t] = 0f;
                else if (inView && _explored[t] < 1f)
                {
                    _explored[t] = MathF.Min(1f, _explored[t] + dt * Rate);
                    _revealing++;
                }
                if (!_fadeLive || !inView || _fadeBoxN[t] == 0) continue;
                _stepped++;
                bool occluding = _mode == ModeAlways || Occludes(t, nx, ny, pd);
                if (occluding) _fadingOut++;
                Step(ref _fade[t], ref _acc[t], occluding, dt);
            }
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
        if (!_ready || !Buckets.TileFade.V) return Clamp01(new TileData(tile).Fade);
        if (!_fadeLive) return 1f;
        int t = Slot(tile);
        return t < 0 ? 1f : _fade[t];
    }

    /// <summary><c>ProcessTriMeshParts</c> @0x480d40's gate: does a <c>tilefade 1</c> part take its tile's factor
    /// this frame? Never under "never"; always under "always"; under "automatic" only while the occlusion plane
    /// stands (a follow target, and a camera not looking straight down) -- otherwise the roof draws opaque.
    /// </summary>
    internal static bool FadeLive(int mode, bool planeOk) => (mode == ModeAuto && planeOk) || mode == ModeAlways;

    /// <summary>The fog-of-war exploration factor of <paramref name="tile"/>, 0..1; 1 for none.</summary>
    internal static float Explored(IntPtr tile)
    {
        if (tile == IntPtr.Zero) return 1f;
        if (!_ready || !Buckets.TileFade.V) return Clamp01(new TileData(tile).GeomFade);
        int t = Slot(tile);
        return t < 0 ? 1f : _explored[t];
    }

    private static float Clamp01(float f) => f >= 1f ? 1f : f > 0f ? f : 0f;

    /// <summary>The registry slot of a tile, memoised on the last answer: a route walks one tile's parts
    /// together, so the memo covers nearly every call and the hash probe is the exception.</summary>
    private static int Slot(IntPtr tile)
    {
        if (tile == _memoTile) return _memoSlot;
        int t = Find(tile);
        _memoTile = tile; _memoSlot = t;
        return t;
    }

    /// <summary>The clause the <c>buckets</c> knob group prints.</summary>
    internal static string Note()
        => !_ready ? $"tile fade stood down ({Missing} unresolved)"
         : _count == 0 ? "no tiles registered"
         : $"{_count} tile(s), {(_ticks == 0 ? 0 : _stepped / _ticks)} classified and "
           + $"{(_ticks == 0 ? 0 : _fadingOut / _ticks)} fading out per frame, {_revealing} reveal step(s)"
           + (_capped > 0 ? $", CAPPED {_capped}x" : "")
           + (_reused > 0 ? $", {_reused} slot(s) reused from the pool" : "")
           + (_witnessDeaths > 0 ? $", {_witnessDeaths} tile(s) dropped on a part death" : "")
           + (_mode == ModeNever ? ", second-story tiles never hidden (the game option)"
              : _mode == ModeAlways ? ", second-story tiles always hidden (the game option)"
              : _planeOk ? "" : ", fade stood down (no follow target, or the camera looks straight down)");

    // ---- table plumbing --------------------------------------------------------------------------

    private static int Bucket(IntPtr tile) => (int)((ulong)tile >> 4) & HashMask;

    private static int Find(IntPtr tile)
    {
        int h = Bucket(tile);
        for (int probe = 0; probe <= HashMask; probe++, h = (h + 1) & HashMask)
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
        for (int probe = 0; probe <= HashMask; probe++, h = (h + 1) & HashMask)
        {
            int t = _slot[h];
            if (t >= 0)
            {
                if (!_freed[t]) continue;
                _tile[t] = tile; _freed[t] = false;
                return t;
            }
            if (_count < MaxTiles) t = _count++;
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
        if (_freeN < MaxTiles) _freeTile[_freeN++] = t;
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
        Array.Fill(_slot, -1);
        Array.Clear(_witnessStamp);
        for (int t = 0; t < live; t++)
        {
            Link(t);
            if (_witness[t] != IntPtr.Zero) InternWitness(_witness[t], t);
        }
    }

    /// <summary>Point the first free hash slot on this tile's probe chain at it. Only ever called on a table
    /// whose slots were just cleared, so it cannot displace a live reference.</summary>
    private static void Link(int t)
    {
        int h = Bucket(_tile[t]);
        for (int probe = 0; probe <= HashMask; probe++, h = (h + 1) & HashMask)
            if (_slot[h] < 0) { _slot[h] = t; return; }
    }

    private static void Cap(string what)
    {
        _capped++;
        if (_cappedLogged) return;
        _cappedLogged = true;
        _log?.Always($"TileFadeRamp: the {what} ceiling ({MaxTiles}) is full -- the rest of this area's tiles "
                   + "draw at full opacity and never fade. That is a real bound being hit, not a transient: "
                   + "raise the cap against a census of the area");
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
        _count = 0;
        Array.Fill(_slot, -1);
        Array.Clear(_tile); Array.Clear(_freed); Array.Clear(_gen); Array.Clear(_fadeBoxN);
        Array.Clear(_fadeBoxOver); Array.Clear(_witness); Array.Clear(_witnessStamp);
        _memoTile = IntPtr.Zero; _memoSlot = -1;

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
        a2 = Slot((IntPtr)tileA);
        check.Expect(a2 >= 0 && Slot((IntPtr)tileB) < 0, "the sweep drops a tile the rebuild did not reach");
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
        int c = Slot((IntPtr)tileB);
        check.Expect(c >= 0 && _explored[c] == 0f, "a fogged tile is seeded unexplored");

        // The leak the review found: churn far past the cap and every new tile must still register. Without
        // the pool and the compaction _count is a HIGH-WATER MARK, so this stops handing out slots a third of
        // the way in and the rest of the area never fades. The churn tiles are real addresses inside one
        // buffer, 16 bytes apart so each hashes to its own bucket -- NoteTile seeds the reveal factor off the
        // tile's fog byte, so a synthetic address that cannot be read is not a usable stand-in.
        const int ChurnStride = 16;
        int churn = MaxTiles * 3, refused = 0;
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
                         $"{churn} tiles of sweep churn register without exhausting the {MaxTiles} slots");
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
        }

        _count = savedCount; _ready = savedReady; _rebuildGen = savedGen;
        Array.Fill(_slot, -1);
        Array.Clear(_tile); Array.Clear(_freed); Array.Clear(_gen); Array.Clear(_fadeBoxN);
        Array.Clear(_fadeBoxOver); Array.Clear(_witness); Array.Clear(_witnessStamp);
        _count = 0; _witnessDeaths = 0;
        _memoTile = IntPtr.Zero; _memoSlot = -1;
    }
}
