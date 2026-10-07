using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>CX-116's live check: while the ENGINE still builds the tiles (engine mode, before the CX-120 seam
/// is installed), build CX's tile from the same inputs and compare. One chained layer on
/// <c>Scene::SpawnTile</c>, the engine's own <c>SceneBSPGetTileData</c> for the tile it just made, and a
/// PASS/FAIL line per area.
/// <para><b>What is compared.</b> The placement (the cell the position resolves to, and the position the cell
/// recomputes -- the <c>CNWTile::SetPosition</c> arithmetic), and the tile's WORLD BOUNDS against the union of
/// the engine's own tile parts' world boxes (<c>PartTriMesh+0x64</c>/<c>+0x70</c>, which
/// <c>ComputePartBBox</c> @0x561370 fills for tile parts only and which is exactly what <c>SpawnTile</c>
/// unions into <c>TileData+0x58</c>). Part count and light count are compared too, but only once CX's own
/// model parse exists: with <c>ModelCache</c> still a stub those two are counted as "no model" rather than
/// reported as agreement, because a comparison nobody could make is not a pass.</para>
/// <para>Bounds are NOT read off <c>TileData+0x58</c>: that field sits inside the one window
/// <c>TileData::TileData</c> does not agree on between the Linux and Windows builds (the same window that
/// leaves <c>TileFade</c>/<c>TilePendingFade</c> unresolved), so an <c>EngineLayout</c> row for it would be a
/// guess on one of the two OSes. The part boxes are resolved on both, so the union is the portable read --
/// and it is the same number.</para>
/// <para>Cold: one chained layer, area load only, and it does nothing at all with the knob off.</para></summary>
internal static unsafe class TileCompare
{
    /// <summary>Innermost of the three layers on this symbol (<see cref="AreaBorder"/> 50,
    /// <see cref="SceneWalk"/> 60), so the ring tiles those layers replay are compared too.</summary>
    private const int TilePriority = 30;

    /// <summary>Agreement to 1 cm: the engine's boxes come from float accumulation over a tile's parts.</summary>
    private const float Tol = 0.01f;

    private static ExtenderLog? _log;
    private static SceneSpawnTileFn* _orig;
    private static SceneBspGetTileDataFn _tileData;
    private static bool _armed;

    private static int _static, _cxRefused;
    private static int _seen, _passPlace, _failPlace, _passBounds, _failBounds, _noModel, _noTile;
    private static int _passParts, _failParts, _passLights, _failLights;
    private static float _worstBounds;
    private static bool _said;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        TileBuild.Attach(log);
        _ = DbTiles.G;
        if (!DbTiles.Compare.V) return;
        if (!SymbolMap.Require(log, "TileCompare", SymbolMap.SceneSpawnTile)
            || !EngineLayout.Require(log, "TileCompare", "TileData", "PartTriMesh"))
            return;
        _tileData = EngineFn.SceneBspGetTileData;
        _orig = hooks.ChainSceneSpawnTile(&OnSpawnTile, TilePriority);
        if (_tileData == null || _orig == null)
        {
            log.Always("TileCompare: REFUSED -- the spawn chain or SceneBSPGetTileData did not resolve; "
                     + "dbtiles.compare reports nothing this session");
            return;
        }
        _armed = true;
        log.Info("TileCompare: armed -- every engine tile is rebuilt by CX and compared (dbtiles.compare=1)");
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnSpawnTile(IntPtr scene, byte* model, Vector3* pos, int orient, int border, IntPtr quat)
    {
        IntPtr gob = (*_orig)(scene, model, pos, orient, border, quat);
        if (!_armed) return gob;
        // A non-null Quaternion* is a STATIC placeable, not a grid tile: the same slot spawns both, and the
        // placeable's position is its own, off the grid (TileBuild.SpawnTile's remarks). Measured: 2 of 1157.
        if (quat != IntPtr.Zero) { _seen++; _static++; return gob; }
        Compare(scene, model, *pos, orient, border);
        return gob;
    }

    private static void Compare(IntPtr scene, byte* model, Vector3 pos, int orient, int border)
    {
        _seen++;

        // 1. The placement arithmetic, which needs no model at all.
        TileBuild.Cell(pos, out int gx, out int gy);
        Vector3 back = TileBuild.CellPos(gx, gy, TileBuild.HeightTransition > 0f
            ? (int)MathF.Round(pos.Z / TileBuild.HeightTransition) : 0);
        bool placeOk = MathF.Abs(back.X - pos.X) < Tol && MathF.Abs(back.Y - pos.Y) < Tol
                       && MathF.Abs(back.Z - pos.Z) < Tol;
        if (placeOk) _passPlace++; else { _failPlace++; Say($"placement {pos} -> cell ({gx},{gy}) -> {back}"); }

        // 2. The engine's tile: its parts' world boxes unioned, and its light population.
        IntPtr td = _tileData(scene, &pos);
        if (td == IntPtr.Zero) { _noTile++; return; }
        var tile = new TileData(td);
        IntPtr* parts = (IntPtr*)tile.Parts;
        int n = tile.PartCount;
        if (parts == null || n <= 0) { _noTile++; return; }

        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        int meshes = 0, lights = 0;
        for (int i = 0; i < n; i++)
        {
            IntPtr p = parts[i];
            if (p == IntPtr.Zero) continue;
            var part = new Part(p);
            if (!part.AsPartLight().IsNull) { lights++; continue; }
            PartTriMesh tri = part.AsPartTriMesh();
            if (tri.IsNull) continue;
            // Nothing in +0x30 can be tilefade 2 or 4 (ProcessStaticParts @0x570970 moved those to
            // TileData+0x90/+0xb0 and removed them from this list), so this list IS the comparable set --
            // including the AABB walkmesh part, whose vtable slot 0x60 is PartTriMesh::AsPartTriMesh.
            meshes++;
            lo = Vector3.Min(lo, tri.BboxMin);
            hi = Vector3.Max(hi, tri.BboxMax);
        }
        if (meshes == 0) { _noTile++; return; }
        // The named four, through the typed view: TileData's MainLight1/2 are the ml1/ml2 PartLight*,
        // SourcePart1/2 the sl1/sl2 Part* (ProcessTilePart @0x561720). Presence only -- the pointers are
        // the engine's and are never dereferenced here.
        int mainLights = (tile.MainLight1.IsNull ? 0 : 1) + (tile.MainLight2.IsNull ? 0 : 1);
        int flameParts = (tile.SourcePart1 != IntPtr.Zero ? 1 : 0) + (tile.SourcePart2 != IntPtr.Zero ? 1 : 0);

        // 3. CX's own build from the same inputs. Without the parser there is no model, and a comparison
        //    nobody could make is counted as such rather than passed.
        string name = model == null ? "" : Marshal.PtrToStringUTF8((IntPtr)model) ?? "";
        Mdl.ModelDef? def = Mdl.ModelCache.Acquire(name);
        if (def == null) { _noModel++; return; }
        try
        {
            CompareBuilt(name, def, pos, orient, border, lo, hi, meshes, lights, mainLights, flameParts);
        }
        finally
        {
            // The reference Acquire took has to go back, or nothing is evictable for the rest of the run.
            Mdl.ModelCache.Release(def);
        }
    }

    private static void CompareBuilt(string name, Mdl.ModelDef def, Vector3 pos, int orient, int border,
                                     Vector3 lo, Vector3 hi, int meshes, int lights, int mainLights, int flameParts)
    {
        int t = TileBuild.Place(TileBuild.CompareScene, def, pos, orient, border);
        if (t < 0) { _cxRefused++; return; }
        TileBuild.MergePlan plan = TileBuild.Plan(def);
        try
        {
            Vector3 cxLo = SceneDb.TileBmin[t], cxHi = SceneDb.TileBmax[t];
            float err = MathF.Max((cxLo - lo).Length(), (cxHi - hi).Length());
            if (err > _worstBounds) _worstBounds = err;
            if (err < Tol) _passBounds++; else { _failBounds++; Say($"{name} bounds off by {err:0.###} m ({cxLo}..{cxHi} vs {lo}..{hi})"); }

            // The comparable count is the nodes CX would see FILED in TileData+0x30, which is the list the
            // engine side walked: tilefade 0 with geometry, outside the animated half.
            if (plan.FiledNodes == meshes) _passParts++;
            else { _failParts++; Say($"{name} files {plan.FiledNodes} mesh nodes, the engine {meshes}"); }

            // Identity, not just a count: the two named main lights have to be the two the engine put at
            // TileData+0x0/+0x8, and the flames the two parts at +0x10/+0x18.
            int cxMain = 0;
            foreach (TileBuild.PlanLight pl in plan.Lights) if (pl.MainSlot == 0 || pl.MainSlot == 1) cxMain++;
            int cxFlame = (plan.Sl1 >= 0 ? 1 : 0) + (plan.Sl2 >= 0 ? 1 : 0);
            if (plan.Lights.Length == lights && cxMain == mainLights && cxFlame == flameParts) _passLights++;
            else
            {
                _failLights++;
                Say($"{name}: CX {plan.Lights.Length} lights ({cxMain} named ml, {cxFlame} named sl), "
                  + $"the engine {lights} lights ({mainLights} at TileData+0/+8, {flameParts} at +0x10/+0x18)");
            }
        }
        finally
        {
            TileBuild.DeleteTile(TileBuild.CompareScene, pos);
        }
    }

    private static void Say(string what)
    {
        if (_said) return;
        _said = true;
        _log?.Always($"TileCompare: FIRST DISAGREEMENT -- {what}");
    }

    /// <summary>The PASS/FAIL line. Emitted on the period report and at the area edge, so one live run with
    /// <c>dbtiles.compare=1</c> is the evidence.</summary>
    internal static string Note()
    {
        if (!_armed) return "dbcompare off";
        bool ok = _failPlace == 0 && _failBounds == 0 && _failParts == 0 && _failLights == 0;
        return $"dbcompare {(ok ? "PASS" : "FAIL")} {_seen} tiles: place {_passPlace}/{_failPlace}, "
             + $"bounds {_passBounds}/{_failBounds} (worst {_worstBounds:0.###} m), parts {_passParts}/{_failParts}, "
             + $"lights {_passLights}/{_failLights}"
             + (_static > 0 ? $", {_static} off-grid statics (not grid tiles)" : "")
             + (_cxRefused > 0 ? $", {_cxRefused} CX REFUSALS" : "")
             + (_noModel > 0 ? $", {_noModel} NO CX MODEL (the CX-112 parser is still a stub)" : "")
             + (_noTile > 0 ? $", {_noTile} no engine tile data" : "");
    }

    /// <summary>The world edge: a new area starts a new tally, and the line for the last one is logged.</summary>
    internal static void Revoke()
    {
        if (_armed && _seen > 0) _log?.Always("TileCompare: " + Note());
        _seen = _passPlace = _failPlace = _passBounds = _failBounds = _noModel = _noTile = 0;
        _passParts = _failParts = _passLights = _failLights = _static = _cxRefused = 0;
        _worstBounds = 0f;
        _said = false;
    }
}
