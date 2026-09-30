using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// CX-86: Alloy's own collision query, so a bouncing particle can bounce without an engine call. Vanilla asks
/// <c>Scene::HitCheck</c> @0x462960, which is two things -- <c>BSPHitCheck</c> over the tile BSP's static
/// parts (<c>HitPartOnBSPNode</c> @0x47b790) and then <c>Scene::HitCheckGob</c> for the dynamic ones -- and
/// CX-81 tier 1's whole point is that no engine code runs in the world frame. So this replaces the first half
/// with an owned, precomputed set.
/// <para><b>What it covers, and what it does not.</b> UP-FACING tile faces: the ground, ramps, steps, table
/// tops. Not walls, not placeables, not creatures. That is a decision, not a ceiling: bounce exists in the
/// content for sparks and debris meeting the FLOOR, up-facing faces are a small fraction of a tile's
/// geometry, and the alternative is a per-tile set an order of magnitude larger for an effect nobody
/// authored. A particle whose step only crosses a wall passes through it, exactly as vanilla's own
/// <c>bounce 0</c> emitters do. <c>emitsim.bounce=0</c> turns the whole query off.</para>
/// <para><b>Built per tile, once, and never on the frame that needs it.</b> A miss queues the tile and the
/// build runs at the top of the next <see cref="EmitterSim.Step"/> under a budget, so the first frame a
/// particle enters a new tile it simply does not bounce there. The set is keyed on the tile's own integer
/// grid coordinate, so the hot query makes NO engine call at all -- <c>SceneBSPGetTileData</c> is a
/// build-time read. Everything is dropped at the area edge (<see cref="Reset"/>, wired to the pass
/// registry's Revoke and ContextLost like every other owned table).</para>
/// <para><b>Zero allocation per frame.</b> Three <c>NativeMemory</c> arenas sized once, a bump allocator
/// inside them, and a full wipe with a counter when one fills -- the shape <see cref="GrassField"/>'s border
/// lists and <see cref="EmitterSim"/>'s own pool already use.</para>
/// </summary>
internal static unsafe class BounceWorld
{
    /// <summary>Tile grid, from <c>CNWTile::TILE_WIDTH</c> @0x1311850: a tile is 10 world units and tile
    /// <c>(i, j)</c> is centred at <c>(i*10+5, j*10+5)</c>, which is what <see cref="AreaBorder.TileSize"/>
    /// already says and what makes a grid coordinate a pure <c>floor(x / 10)</c>.</summary>
    private const float TileSize = AreaBorder.TileSize, TileHalf = AreaBorder.TileHalf;
    /// <summary>Cells across a tile. Eight gives 1.25-unit cells, which is a few times a particle's step, so
    /// a query touches one or two of them and each holds a handful of faces.</summary>
    private const int Cells = 8;
    private const int CellCount = Cells * Cells;
    private const float CellSize = TileSize / Cells;

    /// <summary>A face is ground when its unit normal's |z| clears this -- <see cref="GrassField"/>'s own
    /// threshold, for the same reason: below it the face is a wall or a skirt.</summary>
    private const float MinUpNormal = 0.5f;

    /// <summary>Tiles held at once (an area is at most 32x32, and a session visits far fewer), faces per
    /// tile, and the arena ceilings. 48 bytes a face and 4 a reference, so the caps are 3MB + 1MB.</summary>
    private const int MaxTiles = 1024, MaxFacesPerTile = 2048, MaxFaces = 1 << 16, MaxRefs = 1 << 18;
    /// <summary>Floats a face: the first corner, its two edges, and its unit normal. Precomputed because the
    /// query runs the same Moller-Trumbore on it every step and the normal is what the reflection needs.
    /// </summary>
    private const int FaceFloats = 12;
    /// <summary>Tiles built per frame. A build is tens of microseconds; the budget is there so an area
    /// transition that drops a spell effect over twenty fresh tiles cannot spend them all on one frame.
    /// </summary>
    private const int BuildBudget = 2;
    /// <summary>Tile builds queued at once. Overflow just means a tile waits another frame.</summary>
    private const int MaxPending = 16;

    /// <summary>One built tile: where its cell references start, and the per-cell prefix offsets into
    /// <see cref="_refs"/>. <see cref="Ix"/>/<see cref="Iy"/> are the grid coordinate the query hashes.
    /// </summary>
    private struct Tile
    {
        public int Ix, Iy;
        /// <summary>Index into <see cref="_off"/> of this tile's <c>CellCount + 1</c> prefix offsets.</summary>
        public int Off;
        public int Faces;
    }

    private static readonly Tile[] _tiles = new Tile[MaxTiles];
    private static int _tileN;

    private static float* _faces;
    private static int* _refs;
    private static int* _off;
    private static int _faceTop, _refTop, _offTop;
    /// <summary>Per-query dedupe: a face straddling two cells must not be tested twice, and a stamp is
    /// cheaper than sorting the candidates.</summary>
    private static int* _stamp;
    private static int _query;

    private static readonly int[] _pendX = new int[MaxPending], _pendY = new int[MaxPending];
    private static int _pendN;

    private static ExtenderLog? _log;
    private static SceneBspGetTileDataFn _bspTile;
    private static GlGetPoolFn _getPool;
    private static PartGetWorldFn _getWorld;
    private static bool _ready, _wiped;

    private static int _built, _faceTotal, _tests, _hits, _wipes, _missing;

    /// <summary>Scratch for one tile's build: the faces, then the cell each face lands in. Static, because a
    /// build runs on the render thread inside the frame and must not allocate.</summary>
    private static readonly float[] _sf = new float[MaxFacesPerTile * FaceFloats];
    private static readonly int[] _sCount = new int[CellCount];
    private static readonly int[] _sLo = new int[MaxFacesPerTile * 4];

    internal static bool Ready => _ready;

    internal static void Install(ExtenderLog log)
    {
        _log = log;
        _bspTile = EngineFn.SceneBspGetTileData;
        _getPool = EngineFn.GlGetPool;
        _getWorld = EngineFn.PartGetWorld;
        if (_bspTile == null || _getPool == null || _getWorld == null)
        {
            log.Always("BounceWorld: SceneBSPGetTileData / GetPool / Part::GetWorld did not resolve -- "
                     + "bouncing particles pass through the ground, which is the one thing bounce exists to "
                     + "stop (bounce-nogeom on the [emitsim] line, and /knobs says so under emitsim)");
            return;
        }
        _faces = (float*)NativeMemory.Alloc((nuint)((long)MaxFaces * FaceFloats * sizeof(float)));
        _refs = (int*)NativeMemory.Alloc((nuint)((long)MaxRefs * sizeof(int)));
        _off = (int*)NativeMemory.Alloc((nuint)((long)MaxTiles * (CellCount + 1) * sizeof(int)));
        _stamp = (int*)NativeMemory.AllocZeroed((nuint)((long)MaxFaces * sizeof(int)));
        _ready = true;
    }

    /// <summary>Drop every tile: the area edge, or a context loss. The arenas stay -- they are CPU memory
    /// sized once, and re-allocating them would be churn for nothing.</summary>
    internal static void Reset()
    {
        DropTiles();
        _pendN = 0;
    }

    /// <summary>Everything the arenas hold, and NOT the pending queue. <see cref="Wipe"/> runs from inside
    /// <see cref="Pump"/>'s own loop over that queue, so clearing it there would leave <c>Pump</c>'s cursor
    /// past the end and the next <see cref="Queue"/> writing at a negative index.</summary>
    private static void DropTiles()
    {
        _tileN = 0; _faceTop = 0; _refTop = 0; _offTop = 0;
        _faceTotal = 0;
        if (_stamp != null) NativeMemory.Clear(_stamp, (nuint)((long)MaxFaces * sizeof(int)));
    }

    // ---- the build (cold) ----------------------------------------------------------------------------

    /// <summary>Drain the queue, at the top of the simulation step so a build never lands inside a mover.
    /// </summary>
    internal static void Pump(IntPtr scene)
    {
        if (!_ready || _pendN == 0 || scene == IntPtr.Zero) return;
        int n = _pendN < BuildBudget ? _pendN : BuildBudget;
        for (int i = 0; i < n; i++) Build(scene, _pendX[i], _pendY[i]);
        _pendN -= n;
        for (int i = 0; i < _pendN; i++) { _pendX[i] = _pendX[i + n]; _pendY[i] = _pendY[i + n]; }
    }

    private static void Queue(int ix, int iy)
    {
        for (int i = 0; i < _pendN; i++) if (_pendX[i] == ix && _pendY[i] == iy) return;
        if (_pendN >= MaxPending) return;
        _pendX[_pendN] = ix; _pendY[_pendN] = iy; _pendN++;
    }

    /// <summary>Collect one tile's up-facing faces into a cell grid. The face read is
    /// <c>Scene::AddFacesToBins</c> @0x460ef0's own -- the node's face array and its rest vertex pool, through
    /// the part's world transform -- which is what <see cref="GrassField.BorderSpawn"/> already reads and
    /// what the #338 note says is the only source of a merged-away part's faces.</summary>
    private static void Build(IntPtr scene, int ix, int iy)
    {
        if (Slot(ix, iy) >= 0) return;
        // A full table WIPES rather than stops building: stopping would leave every later tile permanently
        // floorless with only a counter to say so, which is the silent degradation the no-fallback rule is
        // about. A wipe costs a few frames and says so once.
        if (_tileN >= MaxTiles) Wipe();
        Vector3 centre = new(ix * TileSize + TileHalf, iy * TileSize + TileHalf, 0f);
        var td = new TileData(_bspTile(scene, &centre));
        // A tile with no data still gets a slot, empty: the BSP CLAMPS an outside position onto an edge leaf,
        // so retrying every frame would re-walk the same nothing for as long as a particle sits there.
        int faces = td.IsNull ? 0 : Collect(td, ix, iy);
        Place(ix, iy, faces);
    }

    private static int Collect(TileData td, int ix, int iy)
    {
        IntPtr* parts = (IntPtr*)td.Parts;
        int partCount = td.PartCount;
        if (parts == null || partCount <= 0) return 0;
        float x0 = ix * TileSize, y0 = iy * TileSize;
        int faces = 0;
        for (int i = 0; i < partCount && faces < MaxFacesPerTile; i++)
        {
            IntPtr part = parts[i];
            if (part == IntPtr.Zero) continue;
            PartTriMesh tri = new CX.Engine.Part(part).AsPartTriMesh();
            if (tri.IsNull) continue;
            var node = new MdlNodeTriMesh(new PartRef(tri.Ptr).Mdl.Ptr);
            if (node.IsNull) continue;
            var host = new VertexPool((IntPtr)_getPool(node.RestPool));
            var faceRecs = new MaxFace(node.Faces);
            int faceCount = node.FaceCount;
            if (host.IsNull || faceRecs.IsNull || faceCount <= 0) continue;
            Vector3 wp;
            float* q = stackalloc float[4];
            _getWorld(tri.Ptr, &wp, q);
            // Quaternions are stored (w, x, y, z); System.Numerics takes (x, y, z, w).
            var rot = new Quaternion(q[1], q[2], q[3], q[0]);
            for (int f = 0; f < faceCount && faces < MaxFacesPerTile; f++)
            {
                MaxFace mf = faceRecs[f];
                Vector3 a = wp + Vector3.Transform(host[mf.VertA].Pos, rot);
                Vector3 b = wp + Vector3.Transform(host[mf.VertB].Pos, rot);
                Vector3 c = wp + Vector3.Transform(host[mf.VertC].Pos, rot);
                Vector3 e1 = b - a, e2 = c - a;
                Vector3 n = Vector3.Cross(e1, e2);
                float len = n.Length();
                if (!(len > 0f) || MathF.Abs(n.Z) < MinUpNormal * len) continue;
                n /= len;
                int o = faces * FaceFloats;
                _sf[o] = a.X; _sf[o + 1] = a.Y; _sf[o + 2] = a.Z;
                _sf[o + 3] = e1.X; _sf[o + 4] = e1.Y; _sf[o + 5] = e1.Z;
                _sf[o + 6] = e2.X; _sf[o + 7] = e2.Y; _sf[o + 8] = e2.Z;
                _sf[o + 9] = n.X; _sf[o + 10] = n.Y; _sf[o + 11] = n.Z;
                // The XY cell span, clamped to the tile: a face hanging over the seam is kept by whichever
                // tile owns its vertices, and the query tests both endpoints' tiles anyway.
                float lox = MathF.Min(a.X, MathF.Min(b.X, c.X)) - x0;
                float hix = MathF.Max(a.X, MathF.Max(b.X, c.X)) - x0;
                float loy = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - y0;
                float hiy = MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - y0;
                int r = faces * 4;
                _sLo[r] = Clamp((int)MathF.Floor(lox / CellSize));
                _sLo[r + 1] = Clamp((int)MathF.Floor(hix / CellSize));
                _sLo[r + 2] = Clamp((int)MathF.Floor(loy / CellSize));
                _sLo[r + 3] = Clamp((int)MathF.Floor(hiy / CellSize));
                faces++;
            }
        }
        return faces;
    }

    private static int Clamp(int v) => v < 0 ? 0 : v >= Cells ? Cells - 1 : v;

    /// <summary>Commit one tile's scratch into the arenas: a counting sort of the face references into cells,
    /// then the prefix offsets. Wipes everything when an arena fills -- loud, counted, and one area's worth of
    /// bounce, rather than a truncated tile that silently loses its floor.</summary>
    private static void Place(int ix, int iy, int faces)
    {
        int refs = 0;
        for (int k = 0; k < CellCount; k++) _sCount[k] = 0;
        for (int f = 0; f < faces; f++)
        {
            int r = f * 4;
            for (int cy = _sLo[r + 2]; cy <= _sLo[r + 3]; cy++)
                for (int cx = _sLo[r]; cx <= _sLo[r + 1]; cx++) { _sCount[cy * Cells + cx]++; refs++; }
        }
        if (_faceTop + faces > MaxFaces || _refTop + refs > MaxRefs || _offTop + CellCount + 1 > MaxTiles * (CellCount + 1))
        {
            Wipe();
            if (faces > MaxFaces || refs > MaxRefs) return;
        }

        int faceBase = _faceTop, refBase = _refTop, offBase = _offTop;
        new Span<float>(_sf, 0, faces * FaceFloats).CopyTo(new Span<float>(_faces + (long)faceBase * FaceFloats, faces * FaceFloats));
        int at = 0;
        for (int k = 0; k < CellCount; k++) { _off[offBase + k] = refBase + at; at += _sCount[k]; }
        _off[offBase + CellCount] = refBase + at;
        // Second pass fills each cell's slice, using the prefix as a moving cursor and then restoring it.
        for (int k = 0; k < CellCount; k++) _sCount[k] = _off[offBase + k];
        for (int f = 0; f < faces; f++)
        {
            int r = f * 4;
            for (int cy = _sLo[r + 2]; cy <= _sLo[r + 3]; cy++)
                for (int cx = _sLo[r]; cx <= _sLo[r + 1]; cx++) _refs[_sCount[cy * Cells + cx]++] = faceBase + f;
        }
        _faceTop += faces; _refTop += refs; _offTop += CellCount + 1;

        int t = _tileN++;
        _tiles[t].Ix = ix; _tiles[t].Iy = iy;
        _tiles[t].Off = offBase; _tiles[t].Faces = faces;
        _built++; _faceTotal += faces;
    }

    private static void Wipe()
    {
        DropTiles();
        _wipes++;
        if (_wiped) return;
        _wiped = true;
        _log?.Always($"BounceWorld: the collision arena filled ({MaxFaces} faces / {MaxRefs} cell refs) and was "
                   + "wiped -- bouncing particles pass through the ground for a few frames while the tiles "
                   + "under them are rebuilt, and it will repeat while the area stays this dense (bounce-wipe= "
                   + "on the [emitsim] line counts it)");
    }

    private static int Slot(int ix, int iy)
    {
        for (int i = 0; i < _tileN; i++) if (_tiles[i].Ix == ix && _tiles[i].Iy == iy) return i;
        return -1;
    }

    // ---- the query (hot) ----------------------------------------------------------------------------

    /// <summary>Nearest up-facing tile face the segment <paramref name="from"/> -&gt; <paramref name="to"/>
    /// crosses. <paramref name="t"/> is the fraction along it and <paramref name="n"/> the unit normal.
    /// <para>No engine call: the tile is found by arithmetic on its grid coordinate. A tile that is not built
    /// yet is QUEUED and reported as a miss, which costs that particle one frame of not bouncing.</para>
    /// </summary>
    internal static bool Hit(Vector3 from, Vector3 to, out float t, out Vector3 n)
    {
        t = 1f; n = Vector3.Zero;
        if (!_ready) return false;
        // The stamp is a counter, so it has to be allowed to wrap: at int.MaxValue a stale stamp would start
        // matching live queries and faces would be skipped. Hours away, and one memset when it arrives.
        if (++_query == int.MaxValue) { NativeMemory.Clear(_stamp, (nuint)((long)MaxFaces * sizeof(int))); _query = 1; }
        bool got = false;
        int ax = (int)MathF.Floor(from.X / TileSize), ay = (int)MathF.Floor(from.Y / TileSize);
        if (Sweep(ax, ay, from, to, ref t, ref n)) got = true;
        int bx = (int)MathF.Floor(to.X / TileSize), by = (int)MathF.Floor(to.Y / TileSize);
        // A step that crosses a tile seam has to test both, or a particle skims through the floor exactly
        // where two tiles meet -- which would read as a hole in the ground at every tile boundary.
        if ((bx != ax || by != ay) && Sweep(bx, by, from, to, ref t, ref n)) got = true;
        return got;
    }

    private static bool Sweep(int ix, int iy, in Vector3 from, in Vector3 to, ref float t, ref Vector3 n)
    {
        int s = Slot(ix, iy);
        if (s < 0) { Queue(ix, iy); _missing++; return false; }
        if (_tiles[s].Faces == 0) return false;
        int off = _tiles[s].Off;
        float x0 = ix * TileSize, y0 = iy * TileSize;
        int cx0 = Clamp((int)MathF.Floor((MathF.Min(from.X, to.X) - x0) / CellSize));
        int cx1 = Clamp((int)MathF.Floor((MathF.Max(from.X, to.X) - x0) / CellSize));
        int cy0 = Clamp((int)MathF.Floor((MathF.Min(from.Y, to.Y) - y0) / CellSize));
        int cy1 = Clamp((int)MathF.Floor((MathF.Max(from.Y, to.Y) - y0) / CellSize));
        Vector3 d = to - from;
        bool got = false;
        for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int c = cy * Cells + cx;
                for (int r = _off[off + c]; r < _off[off + c + 1]; r++)
                {
                    int f = _refs[r];
                    if (_stamp[f] == _query) continue;
                    _stamp[f] = _query;
                    _tests++;
                    if (!Cross(_faces + (long)f * FaceFloats, from, d, out float u) || u >= t) continue;
                    t = u;
                    float* fp = _faces + (long)f * FaceFloats;
                    n = new Vector3(fp[9], fp[10], fp[11]);
                    got = true;
                }
            }
        if (got) _hits++;
        return got;
    }

    /// <summary>Moller-Trumbore, two-sided: a particle can meet a floor from underneath (an explosion in a
    /// cellar) and the engine's own test does not care about winding either. <paramref name="u"/> is the
    /// fraction along <paramref name="d"/>, in [0, 1].</summary>
    private static bool Cross(float* f, in Vector3 o, in Vector3 d, out float u)
    {
        u = 0f;
        Vector3 a = new(f[0], f[1], f[2]);
        Vector3 e1 = new(f[3], f[4], f[5]);
        Vector3 e2 = new(f[6], f[7], f[8]);
        Vector3 p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        Vector3 tv = o - a;
        float bu = Vector3.Dot(tv, p) * inv;
        if (bu < 0f || bu > 1f) return false;
        Vector3 qv = Vector3.Cross(tv, e1);
        float bv = Vector3.Dot(d, qv) * inv;
        if (bv < 0f || bu + bv > 1f) return false;
        float hit = Vector3.Dot(e2, qv) * inv;
        if (hit < 0f || hit > 1f) return false;
        u = hit;
        return true;
    }

    /// <summary>Off-device: the segment-triangle test, which is the one piece of this file that is pure
    /// arithmetic and the one piece a wrong sign would make invisible rather than loud -- a floor that never
    /// reports a hit looks exactly like a floor nobody bounced on.</summary>
    internal static void Check(Checker check)
    {
        // The unit triangle in the z = 0 plane, as the build stores one: corner, two edges, unit normal.
        float* f = stackalloc float[FaceFloats];
        f[0] = 0f; f[1] = 0f; f[2] = 0f;
        f[3] = 1f; f[4] = 0f; f[5] = 0f;
        f[6] = 0f; f[7] = 1f; f[8] = 0f;
        f[9] = 0f; f[10] = 0f; f[11] = 1f;

        Vector3 from = new(0.2f, 0.2f, 1f);
        check.Expect(Cross(f, from, new Vector3(0f, 0f, -2f), out float u) && MathF.Abs(u - 0.5f) < 1e-5f,
          "a segment straight down through the triangle hits it halfway along");
        check.Expect(Cross(f, new Vector3(0.2f, 0.2f, -1f), new Vector3(0f, 0f, 2f), out _),
          "...and from underneath too: the test is two-sided, because an explosion in a cellar meets its floor from below");
        check.Expect(!Cross(f, from, new Vector3(0f, 0f, -0.5f), out _),
          "a segment that stops short of the plane does NOT hit -- t is clamped to the step, not the ray");
        check.Expect(!Cross(f, new Vector3(0.9f, 0.9f, 1f), new Vector3(0f, 0f, -2f), out _),
          "a segment down through the triangle's PLANE but outside its edges misses (the barycentric gate)");
        check.Expect(!Cross(f, from, new Vector3(1f, 0f, 0f), out _),
          "a segment in the plane misses rather than dividing by a zero determinant");
        check.Expect(Cross(f, new Vector3(0.05f, 0.05f, 1f), new Vector3(0f, 0f, -2f), out _)
                     && !Cross(f, new Vector3(-0.05f, 0.05f, 1f), new Vector3(0f, 0f, -2f), out _),
          "the edge at u = 0 is inclusive on the inside and exclusive on the outside");

        // The grid's cell arithmetic, which decides whether a face is ever a candidate at all.
        check.Expect(Clamp((int)MathF.Floor(0f / CellSize)) == 0
                     && Clamp((int)MathF.Floor((TileSize - 0.01f) / CellSize)) == Cells - 1
                     && Clamp((int)MathF.Floor(-1f / CellSize)) == 0
                     && Clamp((int)MathF.Floor((TileSize + 5f) / CellSize)) == Cells - 1,
          "a tile-local coordinate maps into the cell grid and clamps at both ends");
    }

    /// <summary>The period line's share. <c>tiles=</c> is held/ever-built, so a second number that keeps
    /// climbing while the first does not is the arena thrashing; the rest are per-frame rates.</summary>
    internal static string Stats(int frames)
    {
        if (!_ready) return " bounce-nogeom";
        int f = frames > 0 ? frames : 1;
        string s = $" tiles={_tileN}/{_built} bfaces={_faceTotal} btests={_tests / f} bhits={_hits / f}";
        if (_missing != 0) s += $" bounce-unbuilt={_missing}";
        if (_wipes != 0) s += $" bounce-wipe={_wipes}";
        _tests = 0; _hits = 0; _missing = 0; _wipes = 0;
        return s;
    }
}
