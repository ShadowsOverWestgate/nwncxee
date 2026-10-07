using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>#353: the area border ring, widened. The engine pads every area with five tiles of unwalkable
/// edge geometry (<c>CNWCArea::LoadBorder</c> @0x70a5c0, hard-coded <c>-5..-1</c> / <c>+4</c> in four strip
/// loops); this replays the OUTERMOST engine tile of every strip further out, to <c>border</c> tiles
/// (<c>cx.cfg</c>, default 15). Sound because the edge model is chosen per row/column from the nearest real
/// tile's corner and edge types, so every tile along a strip's outward axis is the same model at the same
/// height -- the ring is widened, not re-derived.
/// <para>Three hooks carry it: <c>Scene::InitBSP</c> widens the tile BSP to match (its leaves CLAMP, so a
/// tile past the border would overwrite an edge leaf's data), <c>SpawnTile</c> replays outward inside a
/// LoadBorder/ReloadBorder window, and <c>DeleteTile</c> replays the same window's deletes onto the outer
/// tiles that exist. Outside the window (a real tile from <c>CNWCTile::LoadModel</c>) nothing changes.</para>
/// <para>The engine's "shadowed" border tint is deliberately not reproduced. Grass on the ring is
/// <see cref="GrassField.BorderSpawn"/>'s, fed from here at every spawn.</para></summary>
internal static unsafe class AreaBorder
{
    /// <summary>The engine's own ring: LoadBorder walks <c>-5..-1</c> past each near edge and <c>dim..dim+4</c>
    /// past each far one, and LoadArea sizes the BSP with <c>InitBSP(w, h, 5)</c>.</summary>
    internal const int EngineRing = 5;
    internal const int DefaultRing = 15, MaxRing = 40;
    /// <summary><c>CNWTile::TILE_WIDTH</c> / <c>TILE_LENGTH</c> @0x1311850 = 10.0, and the half-tile the border
    /// loop adds (DAT_013ee9e8 = 5.0): grid <c>(i, j)</c> spawns at <c>(i*10+5, j*10+5)</c>.</summary>
    internal const float TileSize = 10f, TileHalf = 5f;

    private static ExtenderLog? _log;
    private static int _ring = DefaultRing;
    /// <summary>The CNWCArea whose LoadBorder/ReloadBorder is on the stack, else zero: the only window in
    /// which a SpawnTile or DeleteTile is a ring tile.</summary>
    private static IntPtr _area;
    private static int _spawned, _replayed, _deleted, _areas;
    private static AreaLoadBorderFn _origLoad;
    private static AreaReloadBorderFn _origReload;
    private static SceneInitBspFn _origInitBsp;
    /// <summary>CHAINED, not installed (#88): <see cref="CX.Alloy.SceneWalk"/> watches the same two symbols for
    /// the scene-membership notice a tile gives no other way, and funchook silently orphans the first of two
    /// Installs on one target. This layer sits BELOW that one, so the border replays below reach the engine
    /// directly instead of re-entering it.</summary>
    private const int TilePriority = 50;
    private static SceneSpawnTileFn* _origSpawn;
    private static SceneDeleteTileFn* _origDelete;
    private static SceneBspGetTileDataFn _tileData;

    internal static int Ring => _ring;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // Restart knob: Boot -- the ring is binned at area load from what this session started with.
        _ring = Renderer.Border.Boot;
        if (_ring == EngineRing) { log.Info("AreaBorder: alloy.border=5 is the engine's own ring -- nothing to widen"); return; }
        _tileData = EngineFn.SceneBspGetTileData;
        _origLoad = hooks.InstallAreaLoadBorder(&OnLoadBorder);
        _origReload = hooks.InstallAreaReloadBorder(&OnReloadBorder);
        _origInitBsp = hooks.InstallSceneInitBsp(&OnInitBsp);
        _origSpawn = hooks.ChainSceneSpawnTile(&OnSpawnTile, TilePriority);
        _origDelete = hooks.ChainSceneDeleteTile(&OnDeleteTile, TilePriority);
        if (_tileData == null || _origLoad == null || _origReload == null || _origInitBsp == null
            || _origSpawn == null || _origDelete == null)
        {
            // A partial install could widen the BSP without the ring or the ring without the BSP; neither is
            // safe, so the window never opens and InitBSP passes its argument through.
            _ring = EngineRing;
            log.Info("AreaBorder: a border hook did not install -- the ring stays the engine's");
            return;
        }
        log.Info($"AreaBorder: armed -- border ring {_ring} tiles (cx.cfg border=N)");
    }

    [UnmanagedCallersOnly]
    private static int OnLoadBorder(IntPtr area, IntPtr screen, float progress)
    {
        _areas++;
        GrassField.BorderClear();
        _area = area;
        CX.Alloy.Db.TileBuild.BorderWindow(true);   // CX-116: the same window widens the DB ring
        int r = _origLoad(area, screen, progress);
        CX.Alloy.Db.TileBuild.BorderWindow(false);
        _area = IntPtr.Zero;
        return r;
    }

    [UnmanagedCallersOnly]
    private static void OnReloadBorder(IntPtr area)
    {
        _area = area;
        CX.Alloy.Db.TileBuild.BorderWindow(true);
        _origReload(area);
        CX.Alloy.Db.TileBuild.BorderWindow(false);
        _area = IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    private static void OnInitBsp(IntPtr scene, int w, int h, int border, byte flag, float cell)
    {
        if (border > 0 && border < _ring) border = _ring;
        _origInitBsp(scene, w, h, border, flag, cell);
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnSpawnTile(IntPtr scene, byte* model, Vector3* pos, int orient, int border, IntPtr quat)
    {
        IntPtr gob = (*_origSpawn)(scene, model, pos, orient, border, quat);
        if (_area == IntPtr.Zero || border == 0) return gob;
        _spawned++;
        Vector3 p = *pos;
        GrassField.BorderSpawn(scene, p);
        Steps(p, out int sx, out int sy);
        int extra = _ring - EngineRing;
        for (int i = 0; i <= (sx != 0 ? extra : 0); i++)
            for (int j = 0; j <= (sy != 0 ? extra : 0); j++)
            {
                if (i == 0 && j == 0) continue;
                Vector3 q = new(p.X + sx * i * TileSize, p.Y + sy * j * TileSize, p.Z);
                (*_origSpawn)(scene, model, &q, orient, border, quat);
                GrassField.BorderSpawn(scene, q);
                _replayed++;
            }
        return gob;
    }

    [UnmanagedCallersOnly]
    private static void OnDeleteTile(IntPtr scene, Vector3* pos)
    {
        if (_area != IntPtr.Zero)
        {
            Vector3 p = *pos;
            GrassField.BorderDrop(p);
            Steps(p, out int sx, out int sy);
            int extra = _ring - EngineRing;
            for (int i = 0; i <= (sx != 0 ? extra : 0); i++)
                for (int j = 0; j <= (sy != 0 ? extra : 0); j++)
                {
                    if (i == 0 && j == 0) continue;
                    Vector3 q = new(p.X + sx * i * TileSize, p.Y + sy * j * TileSize, p.Z);
                    GrassField.BorderDrop(q);
                    if (_tileData(scene, &q) == IntPtr.Zero) continue;
                    (*_origDelete)(scene, &q);
                    _deleted++;
                }
        }
        (*_origDelete)(scene, pos);
    }

    /// <summary>The outward axes a ring tile replays along: -1/+1 on x when it sits on the engine ring's
    /// outermost west/east column, likewise on y, else 0. Only the outermost tile of a strip replays, and a
    /// corner tile replays along both -- which is what makes every outer cell the replay of exactly ONE
    /// engine tile (its clamp onto the engine ring). Grid width and height come off the live area.</summary>
    private static void Steps(Vector3 p, out int sx, out int sy)
        => Steps(p, new CNWCArea(_area).TileWidth, new CNWCArea(_area).TileHeight, out sx, out sy);

    internal static void Steps(Vector3 p, int w, int h, out int sx, out int sy)
    {
        int ix = (int)MathF.Floor((p.X - TileHalf) / TileSize + 0.5f);
        int iy = (int)MathF.Floor((p.Y - TileHalf) / TileSize + 0.5f);
        sx = ix == -EngineRing ? -1 : ix == w + EngineRing - 1 ? 1 : 0;
        sy = iy == -EngineRing ? -1 : iy == h + EngineRing - 1 ? 1 : 0;
    }

    internal static string Describe()
        => $"ring {_ring} ({_spawned} engine tiles, {_replayed} replayed, {_deleted} outer deletes, {_areas} areas)";

    internal static string Stats() => _ring == EngineRing ? "" : $"border {Describe()}";

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        // Replay the engine's own 5-ring of a 4x3 area out to ring 7 and count how many times each outer cell
        // is produced: exactly once each, and never a cell the engine already spawned.
        const int w = 4, h = 3, ring = 7, extra = ring - EngineRing;
        var hits = new int[w + 2 * ring, h + 2 * ring];
        for (int ix = -EngineRing; ix < w + EngineRing; ix++)
            for (int iy = -EngineRing; iy < h + EngineRing; iy++)
            {
                if (ix >= 0 && ix < w && iy >= 0 && iy < h) continue;   // a real tile, not the engine's ring
                Steps(new Vector3(ix * TileSize + TileHalf, iy * TileSize + TileHalf, 0f), w, h, out int sx, out int sy);
                for (int i = 0; i <= (sx != 0 ? extra : 0); i++)
                    for (int j = 0; j <= (sy != 0 ? extra : 0); j++)
                        if (i != 0 || j != 0) hits[ix + sx * i + ring, iy + sy * j + ring]++;
            }
        bool once = true, engineUntouched = true;
        for (int x = -ring; x < w + ring; x++)
            for (int y = -ring; y < h + ring; y++)
            {
                bool outer = x < -EngineRing || y < -EngineRing || x >= w + EngineRing || y >= h + EngineRing;
                int n = hits[x + ring, y + ring];
                if (outer && n != 1) once = false;
                if (!outer && n != 0) engineUntouched = false;
            }
        check.Expect(once, "every outer ring cell is the replay of exactly one engine tile");
        check.Expect(engineUntouched, "no replay lands on a cell the engine spawns itself");
        Steps(new Vector3(TileHalf, TileHalf, 0f), w, h, out int cx, out int cy);
        check.Expect(cx == 0 && cy == 0, "a real tile has no outward axis");
        if (check.Ok) log.Info("AreaBorder.SelfCheck: ok");
    }
}
