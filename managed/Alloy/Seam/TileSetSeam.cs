using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>CX-120 (seam-scene): the one engine fact the CX tile build cannot derive -- the area's HEIGHT
/// STEP -- taken at the moment the area's tileset is resolved and handed to <see cref="TileBuild.SetTileset"/>
/// before any tile spawns.
///
/// <para><b>Why a hook and not a read at spawn time.</b> <c>Scene::SpawnTile</c> is handed a model name and a
/// grid position and nothing that names the tileset, so by the time the first tile arrives there is no object
/// to ask. <c>CNWTileSetManager::GetTileSet</c> is where the area names its tileset, and
/// <c>CNWCArea::LoadArea</c> calls it before the spawn loop -- so chaining it puts the step in hand exactly
/// one step ahead of the first tile that needs it.</para>
///
/// <para><b>Chained, not installed.</b> <c>ModelLoad</c> already owns a subscriber on this symbol
/// (<c>funchook-collision-funnels</c>: a second <c>Install</c> would silently orphan the first), so this goes
/// on the same chain at its own priority and only reads.</para>
///
/// <para>The step itself is a FIELD, not the call: <c>CNWTileSet::GetHeightTransition</c> @0x5e10c0 is one
/// load, and the two builds disagree about where (<see cref="EngineLayout.TileSetHeightTransition"/>), which
/// is why it is an <c>EngineLayout</c> row with both offsets rather than a signature.</para></summary>
internal static unsafe class TileSetSeam
{
    private static ExtenderLog? _log;
    private static TileSetGetFn* _slot;
    private static long _resolved;
    private static float _last;

    /// <summary>Chain the tileset resolve. False = the symbol or the layout row is missing, which stands DB
    /// mode's tile build down rather than letting every tile refuse one at a time with no cause named.</summary>
    internal static bool Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!EngineLayout.Require(log, "TileSetSeam", "CNWTileSet")) return false;
        _slot = hooks.ChainTileSetGet(&OnGetTileSet, Priority);
        if (_slot == null)
        {
            log.Always("TileSetSeam: CNWTileSetManager::GetTileSet did not chain -- the area's height step "
                     + "cannot be read, so EVERY tile spawn would refuse. DB mode's tile build stands down.");
            return false;
        }
        return true;
    }

    /// <summary>Below <c>ModelLoad</c>'s, which serves the call: this one only observes the answer.</summary>
    private const int Priority = 5;

    [UnmanagedCallersOnly]
    private static IntPtr OnGetTileSet(IntPtr mgr, IntPtr resref)
    {
        IntPtr tileset = (*_slot)(mgr, resref);
        if (tileset != IntPtr.Zero && Renderer.On)
        {
            float step = *(float*)(tileset + EngineLayout.TileSetHeightTransition);
            if (step != _last)
            {
                _last = step;
                _resolved++;
                TileBuild.SetTileset(step);
            }
        }
        return tileset;
    }

    internal static string Stats() => _slot == null ? "tileset: NOT CHAINED"
        : $"tileset: height step {TileBuild.HeightTransition} ({_resolved} resolves)";
}
