using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary><b>The hit test's spatial index over the scene database</b>: a uniform hash grid in XY over each
/// object's world BOUNDS -- not the engine's pivot-keyed BSP, whose wide-object hole it therefore lacks -- so a
/// cast visits the objects near its segment. Change-driven and allocation-free after the first reservation:
/// <see cref="File"/> runs at creation and on a bounds move, and an unchanged cell rectangle is not re-filed.</summary>
public static class HitGrid
{
    /// <summary>Cell edge, metres. 10 m is the engine's own tile-cell size (the float <c>LoadArea</c> hands
    /// <c>Scene::InitBSP</c>), which makes a CX cell exactly one vanilla BSP cell -- so the index's
    /// granularity is not a new tuning parameter.</summary>
    public const float CellSize = 10f;
    /// <summary>Hash buckets. A power of two so the modulo is a mask; sized for an area's worth of cells
    /// with room to spare, because a collision costs a longer chain, never a wrong answer.</summary>
    private const int Buckets = 1 << 14;
    /// <summary>Cells one object may be filed into before it goes on the oversize list, which every query
    /// visits: filing a cathedral into hundreds of cells costs more than visiting the handful of them.</summary>
    public const int MaxCellsPerObject = 32;
    /// <summary>Cells one query may walk, and the ceiling on the candidate span a caller must provide.</summary>
    public const int MaxQuery = 4096;

    private static readonly int[] _head = new int[Buckets];
    private static int[] _entNext = Array.Empty<int>(), _entObj = Array.Empty<int>(), _entObjNext = Array.Empty<int>(), _entBucket = Array.Empty<int>();
    private static int _entHigh, _entFree = -1;
    /// <summary>Per object: the head of its entry chain, and the cell rectangle those entries cover.</summary>
    private static int[] _objEnt = Array.Empty<int>();
    private static int[] _objX0 = Array.Empty<int>(), _objY0 = Array.Empty<int>(), _objX1 = Array.Empty<int>(), _objY1 = Array.Empty<int>();
    private static bool[] _objOversize = Array.Empty<bool>();
    private static int[] _oversize = Array.Empty<int>();
    private static int _oversizeN;
    /// <summary>Query stamps: an object already in this query's candidate span carries the current stamp.</summary>
    private static int[] _stamp = Array.Empty<int>();
    private static int _stampNow;

    /// <summary>File operations that actually re-filed, entries live, and the oversize list's length -- the
    /// census a status line prints.</summary>
    public static int Filed { get; private set; }
    /// <inheritdoc cref="Filed"/>
    public static int Entries { get; private set; }
    /// <inheritdoc cref="Filed"/>
    public static int Oversize => _oversizeN;
    /// <summary>Re-files that moved an object's cell rectangle, and the ones that did not (the cheap case).</summary>
    public static long Refiles, RefilesSkipped;
    /// <summary>Queries that filled their candidate span and stopped early -- a loud count, never a quiet
    /// hole: a non-zero rate here means a cast answered against part of the world.</summary>
    public static long Truncated;

    static HitGrid() { Reserve(SceneDb.MaxObjects); }

    private static void Reserve(int objects)
    {
        Array.Fill(_head, -1);
        _objEnt = new int[objects];
        Array.Fill(_objEnt, -1);
        _objX0 = new int[objects]; _objY0 = new int[objects];
        _objX1 = new int[objects]; _objY1 = new int[objects];
        _objOversize = new bool[objects];
        _stamp = new int[objects];
        _oversize = new int[1024];
        int cap = objects * 2;
        _entNext = new int[cap]; _entObj = new int[cap]; _entObjNext = new int[cap]; _entBucket = new int[cap];
        _entHigh = 0; _entFree = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Cell(float v) => (int)MathF.Floor(v / CellSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Bucket(int cx, int cy) => (int)(((uint)cx * 0x9E3779B1u ^ (uint)cy * 0x85EBCA77u) >> 2) & (Buckets - 1);

    /// <summary>File or re-file object index <paramref name="o"/> from <c>SceneDb.HitObjBMin/BMax</c>.
    /// Cheap when its cell rectangle is unchanged, which is the common case for a mover.</summary>
    public static void File(int o)
    {
        Vector3 lo = SceneDb.HitObjBMin[o], hi = SceneDb.HitObjBMax[o];
        if (lo.X > hi.X) { Remove(o); return; }
        int x0 = Cell(lo.X), y0 = Cell(lo.Y), x1 = Cell(hi.X), y1 = Cell(hi.Y);
        bool had = _objEnt[o] >= 0 || _objOversize[o];
        if (had && _objX0[o] == x0 && _objY0[o] == y0 && _objX1[o] == x1 && _objY1[o] == y1)
        { RefilesSkipped++; return; }
        if (had) Refiles++;
        Remove(o);
        _objX0[o] = x0; _objY0[o] = y0; _objX1[o] = x1; _objY1[o] = y1;
        Filed++;

        long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
        if (cells > MaxCellsPerObject)
        {
            _objOversize[o] = true;
            if (_oversizeN == _oversize.Length) Array.Resize(ref _oversize, _oversize.Length * 2);
            _oversize[_oversizeN++] = o;
            return;
        }
        for (int cy = y0; cy <= y1; cy++)
            for (int cx = x0; cx <= x1; cx++)
            {
                int e = Alloc();
                // The entry pool is exhausted. A half-filed object would answer for some of its cells and
                // not others, and the same-rectangle shortcut above would never retry it -- a silently
                // unpickable object. So it goes on the OVERSIZE list instead, which every query visits: a
                // candidate too many, never a hit lost. Counted and said out loud.
                if (e < 0) { Starved(o); return; }
                int b = Bucket(cx, cy);
                _entObj[e] = o; _entBucket[e] = b;
                _entNext[e] = _head[b]; _head[b] = e;
                _entObjNext[e] = _objEnt[o]; _objEnt[o] = e;
                Entries++;
            }
    }

    /// <summary>Unfile object index <paramref name="o"/>: the object destroy edge, and the first half of a
    /// re-file.</summary>
    public static void Remove(int o)
    {
        if (_objOversize[o])
        {
            _objOversize[o] = false;
            for (int i = 0; i < _oversizeN; i++)
                if (_oversize[i] == o) { _oversize[i] = _oversize[--_oversizeN]; break; }
        }
        for (int e = _objEnt[o]; e >= 0;)
        {
            int next = _entObjNext[e];
            int b = _entBucket[e];
            if (_head[b] == e) _head[b] = _entNext[e];
            else
                for (int k = _head[b]; k >= 0; k = _entNext[k])
                    if (_entNext[k] == e) { _entNext[k] = _entNext[e]; break; }
            _entNext[e] = _entFree; _entFree = e;
            Entries--;
            e = next;
        }
        _objEnt[o] = -1;
    }

    /// <summary>Objects the entry pool could not hold, and the log latch for the first one.</summary>
    public static long StarvedObjects;
    private static bool _starvedLogged;
    private static ExtenderLog? _log;
    /// <summary>Set once at service start so a refusal can be loud.</summary>
    public static void Logger(ExtenderLog log) => _log = log;

    private static void Starved(int o)
    {
        Remove(o);
        _objOversize[o] = true;
        if (_oversizeN == _oversize.Length) Array.Resize(ref _oversize, _oversize.Length * 2);
        _oversize[_oversizeN++] = o;
        StarvedObjects++;
        if (!_starvedLogged)
        {
            _starvedLogged = true;
            _log?.Always($"HitGrid: the cell-entry pool ({_entNext.Length} entries) is full -- object {o} and "
                       + "any after it are tested by EVERY cast instead of being indexed. Picks stay correct "
                       + "and get slower; raise the pool if this rate is not zero.");
        }
    }

    private static int Alloc()
    {
        if (_entFree >= 0) { int e = _entFree; _entFree = _entNext[e]; return e; }
        if (_entHigh < _entNext.Length) return _entHigh++;
        return -1;
    }

    /// <summary>Candidate object indices whose bounds could touch the segment
    /// <paramref name="from"/>-&gt;<paramref name="to"/>, deduplicated, into <paramref name="candidates"/>.
    /// Walks the cells the segment's XY projection crosses (a 2D DDA, so a long cast costs cells along it and
    /// not an area) plus the oversize list. Allocation-free.</summary>
    public static int Query(Vector3 from, Vector3 to, Span<int> candidates)
    {
        int n = 0;
        unchecked { _stampNow++; }
        if (_stampNow == 0) { Array.Clear(_stamp); _stampNow = 1; }
        int stamp = _stampNow;

        for (int i = 0; i < _oversizeN && n < candidates.Length; i++)
        {
            int o = _oversize[i];
            if (_stamp[o] == stamp) continue;
            _stamp[o] = stamp;
            candidates[n++] = o;
        }

        int cx = Cell(from.X), cy = Cell(from.Y);
        int ex = Cell(to.X), ey = Cell(to.Y);
        float dx = to.X - from.X, dy = to.Y - from.Y;
        int sx = dx > 0 ? 1 : -1, sy = dy > 0 ? 1 : -1;
        // Parametric distance to the next cell edge on each axis, in segment units; infinite for an axis the
        // segment does not move along, which is what retires that axis from the walk.
        float tx = MathF.Abs(dx) < 1e-12f ? float.MaxValue
                 : (((dx > 0 ? cx + 1 : cx) * CellSize) - from.X) / dx;
        float ty = MathF.Abs(dy) < 1e-12f ? float.MaxValue
                 : (((dy > 0 ? cy + 1 : cy) * CellSize) - from.Y) / dy;
        float stepX = MathF.Abs(dx) < 1e-12f ? float.MaxValue : CellSize / MathF.Abs(dx);
        float stepY = MathF.Abs(dy) < 1e-12f ? float.MaxValue : CellSize / MathF.Abs(dy);

        int guard = MaxQuery;
        while (guard-- > 0)
        {
            n = Gather(cx, cy, stamp, candidates, n);
            if (n >= candidates.Length) { Truncated++; break; }
            if (cx == ex && cy == ey) break;
            if (tx <= ty) { if (tx > 1f) break; cx += sx; tx += stepX; }
            else { if (ty > 1f) break; cy += sy; ty += stepY; }
        }
        return n;
    }

    /// <summary>Append one cell's chain. Only this cell is needed: an object is filed into EVERY cell its
    /// bounds overlap, so a neighbour sweep would only re-find what the DDA visits next.</summary>
    private static int Gather(int cx, int cy, int stamp, Span<int> candidates, int n)
    {
        for (int e = _head[Bucket(cx, cy)]; e >= 0; e = _entNext[e])
        {
            int o = _entObj[e];
            if (_stamp[o] == stamp) continue;
            // A hash collision puts foreign cells on the same chain; the rectangle test is the real filter.
            if (cx < _objX0[o] || cx > _objX1[o] || cy < _objY0[o] || cy > _objY1[o]) continue;
            _stamp[o] = stamp;
            if (n >= candidates.Length) return n;
            candidates[n++] = o;
        }
        return n;
    }

    /// <summary>The cell index of a world coordinate -- what a query's own cell is, for a diagnostic that has
    /// to say whether a record's filed rectangle covers the point a cast started from.</summary>
    public static int CellOf(float v) => Cell(v);

    /// <summary>How object <paramref name="o"/> sits in the INDEX: whether it has cell entries at all, the
    /// cell rectangle they cover, and whether cell (<paramref name="cx"/>,<paramref name="cy"/>) is inside it.
    /// A record with a sound world box that the index does not hand back is a FILING fault, which nothing about
    /// the record distinguishes from a placement fault -- so the floor-miss dump prints this beside the box.</summary>
    public static string Describe(int o, int cx, int cy)
        => $"ent={(_objEnt[o] >= 0 ? "yes" : "NONE")}{(_objOversize[o] ? " oversize" : "")}"
         + $" rect=({_objX0[o]},{_objY0[o]})..({_objX1[o]},{_objY1[o]})"
         + (cx >= _objX0[o] && cx <= _objX1[o] && cy >= _objY0[o] && cy <= _objY1[o] ? " covers" : " MISSES");

    /// <summary>One line for a status/period report: what the index holds and how it is being maintained.</summary>
    public static string Stats()
        => $"grid {Entries} cell entries / {Oversize} oversize now, {Filed} filing(s) so far, "
         + $"refile {Refiles} moved + {RefilesSkipped} same-cell"
         + (Truncated > 0 ? $", TRUNCATED {Truncated} queries" : "")
         + (StarvedObjects > 0 ? $", POOL FULL for {StarvedObjects} object(s) (on the oversize list, so slower not wrong)" : "");
}
