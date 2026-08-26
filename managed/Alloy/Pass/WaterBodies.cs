using System;
using System.Numerics;
using Sowx.Framework;
using static Sowx.Alloy.SceneLayout;

namespace Sowx.Alloy;

/// <summary>
/// #147: how big is this body of water? A fragment shader cannot answer that -- it sees one surface and a
/// screen, and has no way to know whether the water continues past the frame. So the renderer answers it,
/// and the answer is a physical quantity rather than a look: <b>fetch</b>, the distance the wind blows over
/// open water, in world units.
/// <para>Fetch is the right number because real wave height is fetch-limited -- it grows with wind speed AND
/// with the run of open water the wind has crossed. That is exactly the difference between a well and a bay,
/// and it keeps the renderer/shader split honest: this file will get better (a rasterised water mask, a
/// distance field) without the shader contract changing at all.</para>
/// <para>Water in NWN is per-TILE: a lake is dozens of small coplanar planes, not one mesh. A per-part extent
/// would therefore call an ocean "small", so parts are union-found into BODIES first -- same plane height,
/// overlapping or touching footprints, transitively.</para>
/// </summary>
internal static unsafe class WaterBodies
{
    /// <summary>Water planes tracked per area. A tileset lake is one part per tile, so this is roughly a
    /// 22x22 tile area entirely under water -- far past anything shipped.</summary>
    private const int MaxPlanes = 8192;
    /// <summary>Two planes join a body when their heights agree within this, in world units. Generous on
    /// purpose: coplanar tile water is authored at one Z, and a canal stepping down a lock is genuinely two
    /// bodies rather than one long one.</summary>
    private const float PlaneEpsilon = 0.25f;
    /// <summary>...and when their footprints come within this of touching. Tile water meets edge to edge with
    /// no overlap, so a pure intersection test would find every tile its own body.</summary>
    private const float JoinSlack = 1.5f;
    /// <summary>Floor under a reported fetch. A degenerate or single-tile plane is still water, and 0 would
    /// make the wave model divide its way to a flat mirror.</summary>
    private const float MinFetch = 2f;
    /// <summary>Half an NWN tile. The footprint assumed for a water part whose object bbox is degenerate,
    /// which live measurement says is common -- tile water is exactly one tile, so this is the right
    /// stand-in rather than a fudge.</summary>
    private const float TileHalf = 5f;

    private static readonly IntPtr[] _part = new IntPtr[MaxPlanes];
    private static readonly float[] _minX = new float[MaxPlanes];
    private static readonly float[] _minY = new float[MaxPlanes];
    private static readonly float[] _maxX = new float[MaxPlanes];
    private static readonly float[] _maxY = new float[MaxPlanes];
    private static readonly float[] _z = new float[MaxPlanes];
    /// <summary>Union-find parent, path-compressed by <see cref="Root"/>.</summary>
    private static readonly int[] _up = new int[MaxPlanes];
    /// <summary>Per-plane body footprint, resolved by <see cref="Rebuild"/> and read per draw.</summary>
    private static readonly float[] _bMinX = new float[MaxPlanes];
    private static readonly float[] _bMinY = new float[MaxPlanes];
    private static readonly float[] _bMaxX = new float[MaxPlanes];
    private static readonly float[] _bMaxY = new float[MaxPlanes];

    private static int _count;
    private static bool _dirty;
    private static int _bodies;
    private static int _overflow;
    /// <summary>How many planes came in with a degenerate bbox and took the nominal tile footprint. Printed,
    /// because it is the difference between "the harbour is one body" and "the harbour is four".</summary>
    private static int _degenerate;
    private static ExtenderLog? _log;


    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Forget every plane. Called wherever the Alloy pass revokes wholesale -- a module unload, an
    /// area transition, a wiped part table. Water planes are area-scoped and a stale one from the last area
    /// would union with this one's and inflate its fetch.</summary>
    internal static void Clear()
    {
        Array.Clear(_part, 0, _count);
        _count = 0; _bodies = 0; _overflow = 0; _degenerate = 0; _dirty = false;
    }

    /// <summary>Record one water plane, once. Cheap enough to call per water draw per frame: a known part is
    /// a hash probe and nothing else.
    /// <para>Accumulating across frames rather than measuring what is on screen is the whole point. The
    /// staged set is CULLED, so a lake measured from it would shrink as you turned away from it and its waves
    /// would change with the camera. Accumulated, fetch only ever grows within an area.</para>
    /// <para>ponytail: which means it is also an UNDER-estimate until you have looked around, and a body
    /// whose far end you never see never joins. The upgrade is a coarse rasterised water mask per area with a
    /// distance field -- that also gives true per-direction fetch and an exact shoreline for foam, and it is
    /// what to build when "the waves grew as I panned" is the complaint.</para></summary>
    internal static void Note(IntPtr part, IntPtr node)
    {
        if (part == IntPtr.Zero || node == IntPtr.Zero) return;
        int slot = Probe(part);
        if (slot >= 0) return;
        if (_count >= MaxPlanes)
        {
            if (_overflow++ == 0)
                _log?.Info($"WaterBodies: more than {MaxPlanes} water planes in one area -- fetch is measured " +
                           "from the first that many and under-reports from here");
            return;
        }

        Matrix4x4 m = new NodeRef(node).Model;
        float* bn = (float*)(part + PartBboxMin);
        float* bx = (float*)(part + PartBboxMax);
        // Measured live: a good many water parts carry a DEGENERATE object bbox (min == max), and the ones
        // that do came out as 0x0u bodies -- their own tiny body, their own tiny fetch, their own wavelength.
        // That is what put a fine corduroy on some tiles and a long swell on the next, and what cracked the
        // tessellation open between them (neighbouring patches computing different edge levels cannot share
        // an edge). A tile's nominal footprint is the honest stand-in: NWN tile water is exactly one tile.
        bool degenerate = !(bx[0] - bn[0] > 1e-3f) || !(bx[1] - bn[1] > 1e-3f);
        float lo0 = 0f, lo1 = 0f, hi0 = 0f, hi1 = 0f, zSum = 0f;
        for (int c = 0; c < 8; c++)
        {
            var p = Vector3.Transform(new Vector3((c & 1) == 0 ? bn[0] : bx[0],
                                                  (c & 2) == 0 ? bn[1] : bx[1],
                                                  (c & 4) == 0 ? bn[2] : bx[2]), m);
            if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z)) return;
            if (c == 0) { lo0 = hi0 = p.X; lo1 = hi1 = p.Y; }
            else
            {
                if (p.X < lo0) lo0 = p.X; else if (p.X > hi0) hi0 = p.X;
                if (p.Y < lo1) lo1 = p.Y; else if (p.Y > hi1) hi1 = p.Y;
            }
            zSum += p.Z;
        }

        if (degenerate)
        {
            // The node's translation is where the plane IS even when its bbox says nothing about how big.
            lo0 = m.M41 - TileHalf; hi0 = m.M41 + TileHalf;
            lo1 = m.M42 - TileHalf; hi1 = m.M42 + TileHalf;
            _degenerate++;
        }

        int i = _count++;
        _part[i] = part;
        _minX[i] = lo0; _minY[i] = lo1; _maxX[i] = hi0; _maxY[i] = hi1;
        _z[i] = zSum * 0.125f;
        _dirty = true;
    }

    /// <summary>The body this plane belongs to: <paramref name="fetch"/> along the wind (drives wave
    /// AMPLITUDE), <paramref name="size"/> direction-independent (drives the WAVELENGTH, and must be, see
    /// below), <paramref name="extent"/> the smaller footprint dimension (caps the wavelength).
    /// False means "not a tracked plane" and the caller should leave the draw alone.</summary>
    internal static bool Body(IntPtr part, Vector2 wind, out float fetch, out float size, out float extent)
    {
        fetch = size = extent = 0f;
        int slot = Probe(part);
        if (slot < 0) return false;
        if (_dirty) Rebuild();

        float dx = _bMaxX[slot] - _bMinX[slot], dy = _bMaxY[slot] - _bMinY[slot];
        // Fetch along the wind is the footprint's extent PROJECTED onto the wind direction: |dx*wx| + |dy*wy|
        // is the support width of an axis-aligned box in that direction. A long canal blowing along its length
        // is a big fetch; the same canal blowing across it is a small one, which is the behaviour that makes
        // this worth doing per-direction rather than as one size number.
        float len = MathF.Sqrt(wind.X * wind.X + wind.Y * wind.Y);
        if (len > 1e-4f)
        {
            float wx = wind.X / len, wy = wind.Y / len;
            fetch = MathF.Abs(dx * wx) + MathF.Abs(dy * wy);
        }
        else fetch = 0.5f * (dx + dy);   // a still area still has a size; the shader decides what to do with it
        if (fetch < MinFetch) fetch = MinFetch;
        // ...and a DIRECTION-INDEPENDENT size alongside it, which is what the wave SPECTRUM is allowed to
        // read. Fetch is projected onto the live wind, so on an anisotropic body (a canal: 200 long, 4 wide)
        // it swings 50:1 as the wind turns. Feeding that to the wavelength would swing the frequency with it
        // and teleport the whole field -- the inc_clouds.shd failure, arriving through the direction term
        // instead of the speed term. Amplitude may track the live wind; wavelength may not.
        size = 0.5f * (dx + dy);
        if (size < MinFetch) size = MinFetch;
        // The SMALLER dimension, which is what actually bounds a wave that is not travelling along the body:
        // a 200x4 canal must not whitecap just because it is long.
        extent = MathF.Min(dx, dy);
        if (extent < MinFetch) extent = MinFetch;
        return true;
    }

    /// <summary>Union-find the planes into bodies and resolve each one's footprint. O(n^2) over the plane
    /// count, run only when a plane is new -- which is a handful of times on entering an area and never
    /// again.
    /// <para>ponytail: the quadratic pass is 512^2 = 262k float compares at its absolute worst, once. A grid
    /// bucket would make it linear and is not worth the code until an area actually holds that many.</para>
    /// </summary>
    private static void Rebuild()
    {
        _dirty = false;
        for (int i = 0; i < _count; i++) _up[i] = i;
        for (int i = 0; i < _count; i++)
            for (int j = i + 1; j < _count; j++)
            {
                if (MathF.Abs(_z[i] - _z[j]) > PlaneEpsilon) continue;
                if (_minX[i] - JoinSlack > _maxX[j] || _minX[j] - JoinSlack > _maxX[i]) continue;
                if (_minY[i] - JoinSlack > _maxY[j] || _minY[j] - JoinSlack > _maxY[i]) continue;
                int a = Root(i), b = Root(j);
                if (a != b) _up[a] = b;
            }

        for (int i = 0; i < _count; i++)
        {
            int r = Root(i);
            if (r == i) { _bMinX[i] = _minX[i]; _bMinY[i] = _minY[i]; _bMaxX[i] = _maxX[i]; _bMaxY[i] = _maxY[i]; }
        }
        for (int i = 0; i < _count; i++)
        {
            int r = Root(i);
            if (r == i) continue;
            if (_minX[i] < _bMinX[r]) _bMinX[r] = _minX[i];
            if (_minY[i] < _bMinY[r]) _bMinY[r] = _minY[i];
            if (_maxX[i] > _bMaxX[r]) _bMaxX[r] = _maxX[i];
            if (_maxY[i] > _bMaxY[r]) _bMaxY[r] = _maxY[i];
        }
        // ...and copy the root's footprint down, so the per-draw read is one indexed load with no find.
        _bodies = 0;
        for (int i = 0; i < _count; i++)
        {
            int r = Root(i);
            if (r == i) { _bodies++; continue; }
            _bMinX[i] = _bMinX[r]; _bMinY[i] = _bMinY[r]; _bMaxX[i] = _bMaxX[r]; _bMaxY[i] = _bMaxY[r];
        }
        _log?.Info($"WaterBodies: {_count} planes -> {_bodies} bod{(_bodies == 1 ? "y" : "ies")} " +
                   $"({Describe()})" +
                   (_degenerate == 0 ? "" : $", {_degenerate} took the nominal tile footprint"));
    }

    private static int Root(int i)
    {
        while (_up[i] != i) { _up[i] = _up[_up[i]]; i = _up[i]; }
        return i;
    }

    /// <summary>Linear probe on the part pointer. No hash table: the plane count is small, the miss path runs
    /// once per plane per area, and the hit path is a pointer compare over a contiguous array -- which on
    /// these counts beats a hash probe's indirection.</summary>
    private static int Probe(IntPtr part)
    {
        for (int i = 0; i < _count; i++) if (_part[i] == part) return i;
        return -1;
    }

    /// <summary>Every distinct body's footprint, for the census line. Bodies are what the discriminator is
    /// FOR, so a wrong grouping has to be visible without a debugger.</summary>
    internal static string Describe()
    {
        if (_dirty) Rebuild();
        if (_count == 0) return "no water";
        string s = "";
        int shown = 0;
        for (int i = 0; i < _count && shown < 4; i++)
        {
            if (Root(i) != i) continue;
            shown++;
            s += $"{(s.Length == 0 ? "" : ", ")}{_bMaxX[i] - _bMinX[i]:F0}x{_bMaxY[i] - _bMinY[i]:F0}u @z{_z[i]:F1}";
        }
        return _bodies > shown ? s + $", +{_bodies - shown} more" : s;
    }

    /// <summary>Off-device (run-demo.sh): the grouping itself, which is the one part of this that can be
    /// wrong in a way no log line shows. Tile water touches edge to edge without overlapping, coplanar
    /// neighbours must join, and a pond on a ledge above a lake must NOT join the lake below it.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"WaterBodies.SelfCheck FAIL: {w}"); } }

        Clear();
        // Four tiles in a row at one height, meeting edge to edge -- one body 40 units long.
        for (int t = 0; t < 4; t++) Fake(new IntPtr(1 + t), t * 10f, 0f, t * 10f + 10f, 10f, 0f);
        // ...and one pond well away and 5 units up: its own body, however close in plan.
        Fake(new IntPtr(99), 15f, 3f, 20f, 8f, 5f);
        Rebuild();
        E(_bodies == 2, $"four touching tiles and a raised pond are 2 bodies, got {_bodies}");

        var along = new Vector2(1f, 0f);
        var across = new Vector2(0f, 1f);
        E(Body(new IntPtr(1), along, out float fAlong, out float sizeA, out float ext) && fAlong > 39f && fAlong < 41f,
          $"fetch along a 40x10 body is its length, got {fAlong:F1}");
        E(Body(new IntPtr(1), across, out float fAcross, out float sizeB, out _) && fAcross > 9f && fAcross < 11f,
          $"fetch across the same body is its width, got {fAcross:F1}");
        E(fAcross < fAlong, "which is the whole reason fetch is directional");
        // The one that guards the phase-jump invariant: turn the wind on an anisotropic body and the SIZE
        // the spectrum reads must not move, or the wavelength swings and the field teleports.
        E(sizeA == sizeB, $"size is direction-INDEPENDENT, got {sizeA:F1} vs {sizeB:F1}");
        E(ext > 9f && ext < 11f, $"the half-extent is the SMALLER dimension, got {ext:F1}");
        E(Body(new IntPtr(99), along, out float fPond, out _, out _) && fPond < 6f,
          "the raised pond keeps its own small fetch");
        E(!Body(new IntPtr(1234), along, out _, out _, out _), "an untracked part reports nothing");
        Clear();

        log.Info(ok ? "WaterBodies.SelfCheck: passed" : "WaterBodies.SelfCheck: FAILED");
    }

    /// <summary>Off-device only: seed a plane directly, bypassing the bbox/matrix read the live path uses.</summary>
    private static void Fake(IntPtr id, float minX, float minY, float maxX, float maxY, float z)
    {
        int i = _count++;
        _part[i] = id;
        _minX[i] = minX; _minY[i] = minY; _maxX[i] = maxX; _maxY[i] = maxY; _z[i] = z;
        _dirty = true;
    }
}
