using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>CX-116: <b>CX builds the area's tiles.</b> The DB-side replacement for the engine's tile build --
/// <c>Scene::SpawnTile</c> @0x474470 -&gt; <c>SpawnPartsForTile</c> @0x563450 -&gt;
/// <c>CollapsePartTree</c> @0x562ff0 -&gt; <c>CombineParts</c> @0x573d30 -&gt;
/// <c>ProcessStaticParts</c> @0x470970 -&gt; <c>SceneBSPAddTileData</c> @0x47cd00 -- plus the tile lights,
/// the tile flames and animation loops, the texture swap and the fog of war. The walkmesh
/// (<c>CNWTileSurfaceMesh</c>) is NOT here: it serves pathing and stays the game's.
/// <para><b>The API is the <c>Scene</c> vtable.</b> Every method below answers one slot the game layer already
/// calls, so the CX-120 seam forwards rather than translates (Linux slot / Windows slot, from
/// <c>tools/abi/vtable-slots.py Scene</c> -- Windows is Linux-0x8 throughout because Linux carries two
/// destructor slots):
/// <list type="table">
/// <item><term>0x038 / 0x030</term><description><c>SpawnTile</c> -&gt; <see cref="SpawnTile"/></description></item>
/// <item><term>0x040 / 0x038</term><description><c>SetTileLights</c> -&gt; <see cref="SetTileLights"/></description></item>
/// <item><term>0x048 / 0x040</term><description><c>SetTileFlames</c> -&gt; <see cref="SetTileFlames"/></description></item>
/// <item><term>0x050 / 0x048</term><description><c>ReplaceTileTexture</c> -&gt; <see cref="ReplaceTileTexture"/></description></item>
/// <item><term>0x058 / 0x050</term><description><c>DeleteTile</c> -&gt; <see cref="DeleteTile"/></description></item>
/// <item><term>0x030 / 0x028</term><description><c>InitBSP</c> -&gt; <see cref="InitGrid"/></description></item>
/// <item><term>0x0f0 / 0x0e8</term><description><c>GetNumTileFlames</c> -&gt; <see cref="NumTileFlames"/></description></item>
/// <item><term>0x0f8 / 0x0f0</term><description><c>GetNumTileLights</c> -&gt; <see cref="NumTileLights"/></description></item>
/// <item><term>0x080 / 0x078</term><description><c>ResetAreaTileFading</c> -&gt; <see cref="ResetFading"/></description></item>
/// </list>
/// and the one free function, <c>AuroraSetTileFoggedByWar</c> @0x453320 -&gt; <see cref="SetFoggedByWar"/>.</para>
/// <para><b>Model space, one transform per placement.</b> The engine bakes: <c>CollapsePartTree</c> composes the
/// placement into every part's own pose and then CLEARS the tree, and the pools hold the result in world space
/// under an identity matrix (#655) -- which is why ADR 0017's exact-content hash read 1.00x on tiles, and why
/// <c>MeshChart</c> had to rebase a tile mesh to its min corner to share one chart. CX does not bake. A tile
/// placement is ONE object record (<see cref="SceneDb.TileObj"/>) whose <see cref="SceneDb.ObjPos"/> /
/// <see cref="SceneDb.ObjRot"/> ARE the placement, and whose geometry is the model's: the merge
/// (<see cref="Plan"/>) runs once per distinct tile MODEL, in model space, and every placement of that model
/// shares its arena ranges. Two placements of one tile model are then two matrices over one vertex range.</para>
/// <para><b>What the merge is.</b> <c>CombineParts</c> buckets a tile's trimesh parts
/// (<c>CreatePartBuckets</c>) and calls <c>VertexPrimitive::MergePart</c> @0x55adf0 on each bucket's host --
/// which, measured, appends the member part to the host primitive's merge LIST (+0x88, count +0x90) and clears
/// the member's render flag (<c>part[0x96] = 0</c>); the vertices are concatenated later, when the pool is
/// filled, from poses that are already world. The engine runs it ONCE PER MODEL: <c>SpawnTile</c> guards the
/// call on <c>Model+0x8d</c> and sets that byte, so only a tile model's FIRST placement is combined. CX takes
/// the per-model intent and drops the per-placement accident: one merge plan per <see cref="ModelDef"/>,
/// cached, in model space.</para>
/// <para>Cold by construction: every method here runs on the game thread at area load or on a tile message,
/// never per frame. The per-frame half is <see cref="TickFade"/>, which walks the dense tile table and
/// allocates nothing.</para></summary>
public static class TileBuild
{
    /// <summary><c>CNWTile::TILE_WIDTH</c>/<c>TILE_LENGTH</c> = 10.0 and the half-tile the grid-to-world
    /// conversion adds: <c>CNWTile::SetPosition</c> @0x732560 writes
    /// <c>pos = (x*10+5, y*10+5, heightTransition*height)</c>.</summary>
    public const float TileSize = 10f, TileHalf = 5f;

    /// <summary>The engine's own border ring (<c>CNWCArea::LoadBorder</c> walks -5..-1 and dim..dim+4).</summary>
    public const int EngineRing = 5;

    /// <summary>Reveal factors per second: the <c>DeltaT * 4.0</c> of the fog-of-war step in the BSP visitor
    /// (@0x481000), the same rate the two fade buckets move at.</summary>
    private const float RevealRate = 4f;

    /// <summary><c>tilefade 1</c> -- the class <c>CheckFadePlane</c> @0x47bf30 tests. 2 is the cap and 4 the
    /// no-shadow class; <c>ProcessStaticParts</c> files those two in side lists instead.</summary>
    private const int TileFadeFades = 1;

    /// <summary>The light-node controller ids, MEASURED off <c>MdlNodeLight::InternalParseField</c> @0x54baf0:
    /// the id the parser passes is the Linux <c>PartLight</c> field offset the sample lands in, which is why
    /// each one matches its <c>EngineLayout</c> row (<c>colour</c> 0x64, <c>range</c> 0x70, <c>rangeAlt</c>
    /// 0x78, <c>intensity</c> 0xa4).</summary>
    private const int CtlColour = 100, CtlRadius = 112, CtlShadowRadius = 120, CtlMultiplier = 164;

    /// <summary>What <c>Scene::SetTileLights</c> @0x55edf0 writes beside the colour, for each of the two main
    /// lights: <c>range</c>, <c>rangeAlt</c>, <c>castShadows</c> = 1 and <c>priority</c> = 4. Main light 1 gets
    /// 10/12, main light 2 gets 5/8 -- the two literal pairs in that function.</summary>
    private const float Main1Range = 10f, Main1RangeAlt = 12f, Main2Range = 5f, Main2RangeAlt = 8f;
    /// <inheritdoc cref="Main1Range"/>
    private const int MainPriority = 4;

    /// <summary>The eleven tile animations <c>CNWCTile::PlayAnimation</c> @0x80c770 names, in its switch
    /// order (every address in this file is an nm address, i.e. Ghidra - 0x100000). The game layer picks one
    /// and forwards the NAME to <c>Gob::PlayAnimation</c>, so CX keeps the enables rather than the names:
    /// <c>Day</c>/<c>Night</c> at load, <c>AnimLoop01..03</c> per the record's three anim-loop bytes, and
    /// <see cref="AnimName"/> is what the seam turns an index back into.</summary>
    private static readonly string[] AnimNames =
        ["tiledefault", "AnimLoop01", "AnimLoop02", "AnimLoop03", "on", "off", "fade", "Day", "Night", "Day2Night", "Night2Day"];

    /// <summary>One tile model's merge plan, in MODEL space: the merged meshes, the arena range each one took,
    /// and the model's light nodes. Built once per <see cref="ModelDef"/> and shared by every placement.</summary>
    public sealed class MergePlan
    {
        /// <summary>The merged meshes, one per bucket (<c>CombineParts</c>' <c>CreatePartBuckets</c>).</summary>
        public MeshDef[] Meshes = Array.Empty<MeshDef>();
        /// <summary>Per merged mesh, the <see cref="MeshArena"/> range it holds (-1 when the arena refused).</summary>
        public int[] Ranges = Array.Empty<int>();
        /// <summary>Per merged mesh, the <c>tilefade</c> class of its bucket (1 fades, 2 cap, 4 no shadow,
        /// 0 plain): the fade ramps a class-1 mesh and nothing else.</summary>
        public int[] FadeClass = Array.Empty<int>();
        /// <summary>The model nodes each bucket swallowed -- what a part tree marks
        /// <see cref="SceneDb.PartMerged"/> (CX-114 reads this; nothing here writes parts).</summary>
        public int[][] Members = Array.Empty<int[]>();
        /// <summary>The model's light nodes, in node order, with their rest pose and controller values.</summary>
        public PlanLight[] Lights = Array.Empty<PlanLight>();
        /// <summary>The nodes named <c>&lt;model&gt;sl1</c> and <c>&lt;model&gt;sl2</c> (-1 for none), and
        /// their model-space poses. <c>ProcessTilePart</c> stores these PARTS (not their lights) at
        /// <c>TileData+0x10</c>/<c>+0x18</c>, and <c>Scene::SetSingleTileFlame</c> @0x55ef30 copies the part's
        /// position and orientation into the flame object -- so these are where a tile's two flames stand.</summary>
        public int Sl1 = -1, Sl2 = -1;
        /// <inheritdoc cref="Sl1"/>
        public Vector3 Sl1Pos, Sl2Pos;
        /// <inheritdoc cref="Sl1"/>
        public Quaternion Sl1Rot = Quaternion.Identity, Sl2Rot = Quaternion.Identity;
        /// <summary>Model-space bounds of every merged mesh together: the whole tile's geometry, for culling.</summary>
        public Vector3 BMin, BMax;
        /// <summary>Model-space bounds of what the engine FILES in <c>TileData+0x30</c>, which is what
        /// <c>Scene::SpawnTile</c>'s union walks: trimesh parts with <c>tilefade 0</c> and non-empty geometry,
        /// outside the animated half. <c>ProcessStaticParts</c> @0x570970 moves <c>tilefade 2</c> to
        /// <c>TileData+0x90</c> and <c>4</c> to <c>+0xb0</c> and REMOVES both from <c>+0x30</c>, and an AABB
        /// walkmesh part answers <c>AsPartTriMesh</c> (its vtable slot 0x60 is <c>PartTriMesh</c>'s) so it IS
        /// filed and DOES count. This is the box the live comparison uses.</summary>
        public Vector3 FiledBMin, FiledBMax;
        /// <summary>Model-space bounds of the <c>tilefade 1</c> geometry alone: what <c>CheckFadePlane</c>
        /// @0x47bf30 tests against the camera plane.</summary>
        public Vector3 FadeBMin, FadeBMax;
        /// <summary>The tile has any <c>tilefade 1</c> geometry at all (else the ramp never moves).</summary>
        public bool HasFade;
        /// <summary>Model-space box of each <c>tilefade 1</c> node, in node order: what
        /// <c>CheckFadePlane</c> tests ONE AT A TIME.</summary>
        public Vector3[] FadeLo = Array.Empty<Vector3>(), FadeHi = Array.Empty<Vector3>();
        /// <summary>Nodes that did NOT merge (animated, or a kind that never merges): they stay their own parts.</summary>
        public int Unmerged;
        /// <summary>Renderable mesh nodes the model has, merged or not.</summary>
        public int MeshNodes;
        /// <summary>Mesh nodes that land in <c>TileData+0x30</c>: the count a live comparison uses.</summary>
        public int FiledNodes;
        /// <summary>The model's <c>aabb</c> nodes, and how many carry a built tree: the walkmesh vanilla's
        /// floor cast hits (<c>SceneBSPAddPart</c> stores it at the BSP leaf's +0x90).</summary>
        public int AabbNodes, AabbTrees;
    }

    /// <summary>One light node of a tile model, resolved to model space with its rest controller values.</summary>
    public struct PlanLight
    {
        public int Node;
        public Vector3 Pos, Colour;
        public float Range, RangeAlt, Multiplier;
        public bool Shadow, AmbientOnly, Dynamic;
        public int Priority;
        /// <summary>0 for the node named <c>&lt;model&gt;ml1</c> and 1 for <c>&lt;model&gt;ml2</c>, else -1:
        /// the two lights <c>Scene::SetTileLights</c> @0x55edf0 re-colours. <c>ProcessTilePart</c> @0x561720
        /// picks them by NAME, <c>strcasecmp</c> against the model name plus the literal <c>"ml1"</c> (nm
        /// 0x12f0a88) or <c>"ml2"</c> (0x12f0a90), and stores the part's <c>AsPartLight()</c> at
        /// <c>TileData+0x0</c>/<c>+0x8</c>. Node ORDER is not the rule and gets a tile with a third light
        /// node wrong.</summary>
        public int MainSlot;
    }

    private static readonly Dictionary<ModelDef, MergePlan> _plans = new();
    /// <summary>(scene handle, grid cell) -&gt; tile slot: CX's <c>SceneBSPGetTileData</c>. Cold path only.</summary>
    private static readonly Dictionary<(uint Scene, int X, int Y), int> _byCell = new();

    private static int _spawned, _deleted, _replayed, _refusedModel, _refusedTable, _refusedLights;
    private static int _refusedBig, _refusedArena, _collisions, _outOfOrder, _cellTaken, _lightSpill, _refusedHeight;
    private static float _cellSize = 10f;

    /// <summary>Every term <c>CreatePartBuckets</c> compares, stored rather than hashed.</summary>
    private readonly record struct MeshFields(string T0, string T1, string T2, string T3, string Mat, string Hint,
                                              int Fade, int Transparency, Vector3 Diffuse, Vector3 Ambient,
                                              Vector3 Specular, float Shininess, uint Flags);

    private static MeshFields FieldsOf(MeshDef m)
    {
        uint flags = (m.Shadow ? 1u : 0) | (m.Beaming ? 2u : 0) | (m.Lightmapped ? 4u : 0) | (m.RotateTexture ? 8u : 0);
        for (int sx = 0; sx < m.Uv.Length; sx++) if (m.Uv[sx].Length > 0) flags |= 16u << sx;
        if (m.Colors.Length > 0) flags |= 1u << 8;
        if (m.Tangents.Length > 0) flags |= 1u << 9;
        return new MeshFields(Low(m.Textures[0]), Low(m.Textures[1]), Low(m.Textures[2]), Low(m.Textures[3]),
                              Low(m.MaterialName), Low(m.RenderHint), m.TileFade, m.TransparencyHint,
                              m.Diffuse, m.Ambient, m.Specular, m.Shininess, flags);
    }

    private static string Low(string s) => s.ToLowerInvariant();
    private static int _arenaAsks, _planBuilds, _mergedNodes, _litRegistered, _swaps;
    private static int _gridW, _gridH, _ring = EngineRing;
    private static float _heightTransition = 5f;
    private static bool _borderWindow;

    /// <summary>Tile placements the DB holds, and the models they came from.</summary>
    public static int Placements => _spawned - _deleted;
    /// <inheritdoc cref="Placements"/>
    public static int Models => _plans.Count;
    /// <summary>Merged meshes offered to the arena: one per distinct tile mesh, never one per placement. The
    /// CX-113 census and <c>MeshChart</c>'s <c>built</c> count are held to this number.</summary>
    public static int ArenaAsks => _arenaAsks;
    public static int LightsRegistered => _litRegistered;

    // ---- Scene vtable: the grid ----

    /// <summary>Linux <c>Scene</c> +0x30 / Windows +0x28, <c>InitBSP(int, int, int, bool, float)</c>:
    /// <c>CNWCArea::LoadArea</c> calls it <c>(w, h, 5, true, 10.0f)</c>. The DB has no BSP (the visible set is
    /// the grid over the per-tile bounds), so this records the grid and the ring, widened to
    /// <c>alloy.border</c> exactly as <see cref="AreaBorder"/> widens the engine's.
    /// <para><paramref name="cellSize"/> is the BSP CELL SIZE, which is a different number from the tileset's
    /// height step -- it is recorded for the record and is not the height step
    /// (<see cref="SetTileset"/>).</para></summary>
    public static void InitGrid(DbId scene, int w, int h, int border, float cellSize)
    {
        _gridW = w;
        _gridH = h;
        _cellSize = cellSize;
        _ring = border > 0 && border < AreaBorder.Ring ? AreaBorder.Ring : border;
    }

    /// <summary>The area's tileset height step: <c>CNWTileSet::GetHeightTransition</c> @0x5e10c0, which
    /// <c>CNWCArea::LoadArea</c> (src/CNWCArea.c:2554) reads and passes to <c>CNWCTile::SetPosition</c> as the
    /// multiplier on the record's height index. There is NO sane default -- a wrong step puts every raised
    /// tile at the wrong altitude -- so until the seam calls this, every spawn refuses loudly and the step is
    /// reported unset on the period line. The seam's hook point is the tileset the area indexes
    /// (<c>CNWCArea+0x290</c>, already read by <c>ModelLoad.Tiles</c>'s <c>CNWTile::SetID</c> layer).</summary>
    public static void SetTileset(float heightTransition)
    {
        if (heightTransition > 0f) { _heightTransition = heightTransition; return; }
        Refuse($"CNWTileSet::GetHeightTransition answered {heightTransition} -- a tile's height index cannot be placed");
    }

    /// <summary>The height step, or 0 when the seam has not named one yet.</summary>
    public static float HeightTransition => _heightTransition;

    /// <summary>Open or close the <c>LoadBorder</c>/<c>ReloadBorder</c> window: inside it a spawn is a ring
    /// tile and replays outward to the widened ring. <see cref="AreaBorder"/> already brackets
    /// <c>CNWCArea::LoadBorder</c>/<c>ReloadBorder</c> for the engine's scene and calls this from inside its
    /// own window, so the DB ring needs no second hook; the seam may also call it. When <c>AreaBorder</c>
    /// stands down the ring is the engine's five tiles, there is nothing to widen, and the window staying
    /// shut is the right answer.</summary>
    public static void BorderWindow(bool open) => _borderWindow = open;

    /// <summary>The scene handle the live comparison places its throwaway tiles under
    /// (<see cref="TileCompare"/>). A real allocation, so it cannot collide with a scene the seam makes --
    /// <c>DbId.Make(MaxScenes - 1, 0)</c> would have, the moment 255 scenes existed.</summary>
    public static DbId CompareScene
    {
        get
        {
            if (!SceneDb.Scenes.Valid(_compareScene)) _compareScene = SceneDb.Scenes.Alloc();
            return _compareScene;
        }
    }
    private static DbId _compareScene;

    /// <summary>The world position of a grid cell, as <c>CNWTile::SetPosition</c> computes it.</summary>
    public static Vector3 CellPos(int gx, int gy, int height)
        => new(gx * TileSize + TileHalf, gy * TileSize + TileHalf, _heightTransition * height);

    /// <summary>The grid cell a world position names (the inverse of <see cref="CellPos"/>, and the key
    /// <c>SceneBSPGetTileData</c> resolves a <c>Vector const&amp;</c> with).</summary>
    public static void Cell(Vector3 p, out int gx, out int gy)
    {
        gx = (int)MathF.Floor((p.X - TileHalf) / TileSize + 0.5f);
        gy = (int)MathF.Floor((p.Y - TileHalf) / TileSize + 0.5f);
    }

    /// <summary>The tile placement at <paramref name="p"/>, or -1. CX's <c>SceneBSPGetTileData</c>.</summary>
    public static int At(DbId scene, Vector3 p)
    {
        Cell(p, out int gx, out int gy);
        return At(scene, gx, gy);
    }

    /// <inheritdoc cref="At(DbId, Vector3)"/>
    public static int At(DbId scene, int gx, int gy)
        => _byCell.TryGetValue(CellKey(scene, gx, gy), out int t) && SceneDb.Tiles.LiveAt(t) ? t : -1;

    /// <summary>A cell of one scene. The FULL handle, generation included: a scene index is recycled at every area
    /// change, and an index-only key let the old area's late teardown remove the new area's cells and statics.</summary>
    private static (uint Scene, int X, int Y) CellKey(DbId scene, int gx, int gy) => (scene.Value, gx, gy);

    // ---- Scene vtable 0x38: SpawnTile ----

    /// <summary>Linux <c>Scene</c> +0x38 / Windows +0x30,
    /// <c>SpawnTile(char const*, Vector const&amp;, int, int, Quaternion*)</c>: build the tile at
    /// <paramref name="pos"/> from model <paramref name="model"/>, oriented <paramref name="orient"/> quarter
    /// turns about +Z. <paramref name="border"/> non-zero marks a ring tile (the engine's own argument), and
    /// <paramref name="rot"/> overrides the derived quaternion when the caller passes one, as the engine's
    /// optional <c>Quaternion*</c> does.
    /// <para><b>A non-null <paramref name="rot"/> is NOT a grid tile.</b> The same slot spawns every STATIC
    /// placeable: <c>CNWCArea::LoadArea</c> and <c>RespawnStaticPlaceablesForTile</c> @0x708aa0 call it with
    /// the object's own world position and an explicit quaternion, which is why a Static placeable's model is
    /// a tile gob (<c>Gob+0x1a8</c> set -- CX-170 section 5). Those land as
    /// plain object records associated with the cell's tile, not as tile records: they are off the grid, and
    /// several can share one cell, so filing them in the cell index would have them overwrite each other and
    /// the tile. Measured live: a stock area spawned 1155 grid tiles and 2 of these.</para>
    /// <para>Returns the tile slot, or -1 with a loud refusal: a missing model, a full table and a refused
    /// merge are each counted and named, never a quiet hole.</para></summary>
    public static int SpawnTile(DbId scene, string model, Vector3 pos, int orient, int border, Quaternion? rot = null)
    {
        ModelDef? def = ModelCache.Acquire(model);
        if (def == null) { _refusedModel++; Refuse($"no model '{model}'"); return -1; }
        WarmMaterials?.Invoke(def);
        int t = -1;
        if (rot != null) { StaticObject = PlaceStatic(scene, def, pos, rot.Value); t = StaticObject.IsNull ? -1 : StaticTile; }
        else { StaticObject = default; t = PlaceWithRing(scene, def, pos, orient, border, null); }
        // Acquire's reference served the lookup; each placement took one of its own in Hold.
        ModelCache.Release(def);
        return t;
    }

    /// <summary>The object a grid tile hands the game layer as its gob, or null: <c>SpawnPartsForTile</c>
    /// @0x463450 makes one only for a non-border tile whose model has a <c>&lt;resref&gt;a</c> node, and
    /// <c>CNWCTile</c> plays its Day/Night/AnimLoop animations on it (<c>CNWCTile::PlayAnimation</c>
    /// @0x80c770).</summary>
    public static DbId AnimatedTileObject(int t)
    {
        if ((uint)t >= (uint)SceneDb.MaxTiles || SceneDb.TileIsBorder[t]) return default;
        ModelDef? def = SceneDb.TileModel[t];
        if (def == null || def.Find(def.Name + "a") < 0) return default;
        DbId o = SceneDb.TileObj[t];
        return SceneDb.Objects.Valid(o) ? o : default;
    }

    /// <summary>Reads the <c>.mtr</c>s a spawned model's meshes name before its part tree is built, so a
    /// bitmap that names a material resolves to it rather than to a missing texture of that name (white, for the
    /// session: a city's static tents, 2026-10-09). Set by the resource layer (<c>MtrFeed</c>), as
    /// <see cref="MtrDef.Bytes"/> is; null draws the mesh's own names.</summary>
    public static Action<ModelDef>? WarmMaterials;

    /// <summary>The object the last <see cref="SpawnTile"/> made when it was a STATIC placeable (a non-null
    /// <c>Quaternion*</c>), else null. The seam has to hand the game layer a <c>CAurObject*</c> for it, which
    /// a tile slot cannot name -- and -1 must not read as a refusal when the spawn succeeded.</summary>
    public static DbId StaticObject { get; private set; }

    /// <summary>What <see cref="SpawnTile"/> returns for a successful static placeable: not a tile slot, and
    /// distinct from the -1 a refusal returns.</summary>
    public const int StaticTile = -2;

    /// <summary>One <see cref="ModelCache"/> reference per PLACEMENT (CX-113), so a tile model stays resident
    /// exactly as long as something stands on it. A model the cache does not hold -- the self-checks' -- is
    /// not referenced, and is told apart by identity rather than by a flag.</summary>
    private static void Hold(ModelDef def)
    {
        if (ReferenceEquals(ModelCache.FindModel(def.Name), def)) ModelCache.Acquire(def.Name);
    }

    /// <inheritdoc cref="Hold"/>
    private static void Unhold(ModelDef? def)
    {
        if (def != null && ReferenceEquals(ModelCache.FindModel(def.Name), def)) ModelCache.Release(def);
    }

    /// <summary>The CX-113 census, which answers the CX-116 perf clause (meshes against placements for a tile
    /// area): one note per merged mesh per placement. Only when <see cref="PartTree.Build"/> did NOT build a
    /// part range -- when it does, the part tree counts its own placements and this would double them.</summary>
    private static void Census(MergePlan plan, bool ours, bool add)
    {
        if (!ours) return;
        foreach (MeshDef m in plan.Meshes)
            if (add) ModelCache.NotePlacement(m); else ModelCache.DropPlacement(m);
    }

    /// <summary>The <c>Quaternion*</c> path: a static object placed into whatever cell it stands in. One
    /// object record, the model's merge plan, and its lights filed with no tile (so a tile's own light run
    /// stays contiguous and <see cref="SetTileLights"/> cannot re-colour a placeable's lamp).</summary>
    public static DbId PlaceStatic(DbId scene, ModelDef def, Vector3 pos, Quaternion rot)
    {
        MergePlan plan = Plan(def);
        Matrix4x4 place = Matrix4x4.CreateFromQuaternion(rot);
        place.Translation = pos;
        DbId obj = NewObject(scene, def, pos, rot, place, tile: false);
        if (obj.IsNull) return default;
        Hold(def);
        Census(plan, SceneDb.ObjPartCount[obj.Index] == 0, add: true);
        RegisterLights(default, obj, plan, place, null);
        Cell(pos, out int gx, out int gy);
        var key = CellKey(scene, gx, gy);
        if (!_statics.TryGetValue(key, out List<DbId>? list)) _statics[key] = list = new List<DbId>(2);
        list.Add(obj);
        _statics0++;
        return obj;
    }

    /// <summary>Statics by cell: what a <see cref="DeleteTile"/> at that cell takes with the tile, as the
    /// engine's own teardown does (their <c>Gob+0x1a8</c> is that cell's <c>TileData</c>).</summary>
    private static readonly Dictionary<(uint Scene, int X, int Y), List<DbId>> _statics = new();
    private static int _statics0;

    /// <summary>One object record for a placed model: the columns every placement shares.</summary>
    private static DbId NewObject(DbId scene, ModelDef def, Vector3 pos, Quaternion rot, in Matrix4x4 place,
                                 bool tile, bool walkmesh = false)
    {
        DbId obj = SceneDb.Objects.Alloc();
        if (obj.IsNull) { _refusedTable++; Refuse("the object table is full -- a placement has no object record"); return default; }
        int o = obj.Index;
        DbFeed.InitObject(o);
        // ...and the animation runtime's per-object state, which is where the COMPOSE's staleness lives. A
        // recycled slot keeps the previous tenant's _from* snapshot and compose generation, so without this
        // ComposeIndex early-returns on a brand-new object and its parts keep the OLD tenant's world
        // matrices -- which is a static whose hit box sits wherever the last object stood. It is why the
        // first area load worked (fresh slots) and every area after it did not.
        DbAnim.ObjectCreated(obj);
        SceneDb.ObjKindOf[o] = ObjKind.Model;
        SceneDb.ObjModel[o] = def;
        SceneDb.ObjPos[o] = pos;
        SceneDb.ObjRot[o] = rot;
        SceneDb.ObjScale[o] = 1f;
        SceneDb.ObjWorld[o] = place;
        // Never moves once placed, so a visible-set entry rather than a per-frame route. ObjFlag.Tile is
        // receiver class 1 and the LOD/weld skip, which is right for tileset geometry and WRONG for a Static
        // placeable, so the caller says which this is (seam-feed's ObjFlag contract).
        SceneDb.ObjFlags[o] = (tile ? ObjFlag.Tile : ObjFlag.None) | ObjFlag.Static;
        SceneDb.ObjRunRoot[o] = obj;
        MergePlan plan = Plan(def);
        Vector3 c = Vector3.Transform((plan.BMin + plan.BMax) * 0.5f, place);
        SceneDb.ObjSphere[o] = new Vector4(c, (plan.BMax - plan.BMin).Length() * 0.5f);
        // CX-114's tile path: reference expansion, the collapse and the merge, in the order
        // SpawnPartsForTile and Scene::SpawnTile run them -- but with an IDENTITY placement, so the merged
        // geometry stays model-space and one arena copy serves every placement (CONTRACTS.md). The
        // <resref>a subtree is the animated half the collapse keeps under the root.
        // A walkmesh model is one `node aabb` and nothing else: no merge, no collapse, no `<resref>a`
        // subtree to keep -- the plain tree builder is the whole of it.
        bool built = walkmesh ? PartTree.Build(obj, def) : PartTree.BuildTile(obj, def, def.Name + "a");
        if (!built) { SceneDb.ObjPartFirst[o] = 0; SceneDb.ObjPartCount[o] = 0; }
        // Vanilla never RENDERS a walkmesh: it reaches the BSP cell's +0x90 slot through SceneBSPAddPart's
        // AsPartAABB branch and is only ever hit-tested. Invisible keeps it out of the visible set (the feed's
        // own check: "an invisible object yields nothing") while the hit test's STATIC pass, which applies no
        // object gates at all, still sees it -- which is exactly vanilla's split.
        if (walkmesh) SceneDb.ObjVisible[o] = false;
        for (int k = 0; k < SceneDb.ObjPartCount[o]; k++)
        {
            int pk = SceneDb.ObjPartFirst[o] + k;
            SceneDb.PartFade[pk] = 1f;
            if (SceneDb.PartKindOf[pk] == PartKind.Aabb) _aabbParts++;
        }
        _partsMade += SceneDb.ObjPartCount[o];
        if (Plan(def).AabbNodes > 0) _aabbWanted++;
        // The create contract, in its order (CONTRACTS.md "LIFECYCLE WIRING"), and all three were missing:
        //   * the animation runtime has to know the record exists before anything poses it;
        //   * the hit index files it as STATIC -- a tile and a static placeable are the area's static set,
        //     which is what a click-to-move floor cast and the camera's spring arm test against;
        //   * and it JOINS the scene. Writing ObjScene by hand (as this did) leaves the scene with no
        //     members, so GetAurSceneObjectCount answers 0, the draw-order slots are no-ops and -- the one
        //     that bites -- a scene destroy frees no tile at all, because its sweep walks the member list.
        // JoinNew, not Join: the record was allocated three lines up, so the membership test is known.
        DbAnim.ObjectCreated(obj);
        HitFeed.NoteObject(obj, isStatic: true);
        DbScene.JoinNew(scene, obj);
        SceneDb.Objects.Mark(o, Dirty.Created | Dirty.Xform | Dirty.Tree);
        return obj;
    }

    /// <summary>The spawn with the model in hand: <see cref="Place"/> plus the ring replay. This is the entry
    /// a tile build goes through; <see cref="Place"/> itself places exactly one tile and is what the live
    /// comparison uses, so a comparison never widens the ring.</summary>
    public static int PlaceWithRing(DbId scene, ModelDef def, Vector3 pos, int orient, int border, Quaternion? rot = null)
    {
        int t = Place(scene, def, pos, orient, border, rot);
        if (t < 0) return -1;
        if (_borderWindow && border != 0) ReplayOutward(scene, def, pos, orient, border, rot);
        return t;
    }

    /// <summary>Place exactly one tile from a model already in hand -- no ring replay. The live comparison
    /// and <see cref="ReplayOutward"/> call this (one <see cref="ModelCache"/> reference serves the whole strip).</summary>
    public static int Place(DbId scene, ModelDef def, Vector3 pos, int orient, int border, Quaternion? rot = null)
    {
        if (_heightTransition <= 0f)
        { _refusedHeight++; Refuse("no tileset height step yet (TileBuild.SetTileset) -- a tile cannot be placed"); return -1; }
        Cell(pos, out int gx, out int gy);
        var cellKey = CellKey(scene, gx, gy);
        // The engine reuses the cell's TileData rather than making a second one (SpawnTile asks
        // SceneBSPGetTileData first), so a spawn onto an occupied cell replaces that placement here too --
        // dropping it first, because leaving it would make its record, object and lights unreachable.
        if (_byCell.TryGetValue(cellKey, out int had) && SceneDb.Tiles.LiveAt(had)) { _cellTaken++; Drop(had); }
        DbId id = SceneDb.Tiles.Alloc();
        if (id.IsNull) { _refusedTable++; Refuse($"the tile table is full at {SceneDb.MaxTiles} placements"); return -1; }
        int t = id.Index;
        MergePlan plan = Plan(def);
        Quaternion q = rot ?? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, orient * 90f * (MathF.PI / 180f));

        SceneDb.TileScene[t] = scene;
        SceneDb.TileModel[t] = def;
        SceneDb.TileGridX[t] = gx;
        SceneDb.TileGridY[t] = gy;
        SceneDb.TileHeight[t] = _heightTransition > 0f ? (int)MathF.Round(pos.Z / _heightTransition) : 0;
        SceneDb.TileOrient[t] = orient & 3;
        SceneDb.TileSetId[t] = -1;   // the area message's tile id arrives separately, in NoteRecord
        SceneDb.TilePos[t] = pos;
        SceneDb.TileIsBorder[t] = border != 0;
        SceneDb.TileFogged[t] = false;
        SceneDb.TileFade[t] = 1f;
        SceneDb.TileFadeAcc[t] = 0f;
        SceneDb.TileReveal[t] = 1f;
        SceneDb.TileReplaceTex[t] = null;
        SceneDb.TileFlame1[t] = default;
        SceneDb.TileFlame2[t] = default;

        Matrix4x4 place = Matrix4x4.CreateFromQuaternion(q);
        place.Translation = pos;
        // TileBmin/Bmax is the engine's TileData+0x58/+0x64 equivalent: the box over what lands in
        // TileData+0x30. The fade box is the tilefade-1 subset, which is what CheckFadePlane tests.
        Bounds(plan.FiledBMin, plan.FiledBMax, place, out SceneDb.TileBmin[t], out SceneDb.TileBmax[t]);
        if (plan.HasFade) Bounds(plan.FadeBMin, plan.FadeBMax, place, out SceneDb.TileFadeBmin[t], out SceneDb.TileFadeBmax[t]);
        else { SceneDb.TileFadeBmin[t] = Vector3.Zero; SceneDb.TileFadeBmax[t] = Vector3.Zero; }
        // One PLACED box per tilefade-1 part, at spawn: the occlusion test reads them every frame and must
        // not compose a matrix to do it.
        int nf = plan.FadeLo.Length;
        if (nf > SceneDb.TileFadeBoxStride)
        {
            _fadeBoxSpill++;
            Refuse($"{def.Name}: {nf} tilefade-1 parts, past the {SceneDb.TileFadeBoxStride} the fade tracks "
                 + "-- the rest do not occlude");
            nf = SceneDb.TileFadeBoxStride;
        }
        SceneDb.TileFadeBoxN[t] = (byte)nf;
        for (int f = 0; f < nf; f++)
            Bounds(plan.FadeLo[f], plan.FadeHi[f], place,
                   out SceneDb.TileFadeBoxLo[t * SceneDb.TileFadeBoxStride + f],
                   out SceneDb.TileFadeBoxHi[t * SceneDb.TileFadeBoxStride + f]);

        DbId obj = NewObject(scene, def, pos, q, place, tile: true);
        if (obj.IsNull)
        {
            // The object table refused: unwind rather than leaving a tile record with no geometry, no
            // reference released and a published fade (#15).
            SceneDb.Tiles.Free(id);
            return -1;
        }
        SceneDb.TileObj[t] = obj;
        // SpawnPartsForTile's last act before it collapses the tree: the whole part tree's textures turn by
        // orient quarter turns. Recorded on the object because the recipe is shared across orientations.
        SceneDb.ObjTexRotate[obj.Index] = (byte)(orient & 3);
        Walkmesh(scene, def, pos, q, place);
        Hold(def);
        Census(plan, SceneDb.ObjPartCount[obj.Index] == 0, add: true);

        // A BORDER tile carries no lights, no emitters and no ml*/sl* parts at all: ProcessStaticParts
        // @0x570970 deletes every non-trimesh part when its `border` argument is non-zero.
        if (border == 0)
        {
            Array.Fill(SceneDb.TileLightIds, -1, t * SceneDb.TileLightStride, SceneDb.TileLightStride);
            RegisterLights(id, obj, plan, place, _lightScratch);
            Array.Copy(_lightScratch, 0, SceneDb.TileLightIds, t * SceneDb.TileLightStride, SceneDb.TileLightStride);
        }
        else SceneDb.TileLightCount[t] = 0;
        // Compose NOW, not at the next transform pass. A tile is static and the engine casts against it in
        // the SAME message batch that built the area (CNWCArea::LoadArea's tile loop, then the creature
        // update that calls CNWCCreature::ComputeZValue @0x71d4f0), so a part whose world matrix is still
        // identity is a floor at the ORIGIN -- which is why an index-free sweep of the object table found no
        // static record covering the ruins' spawn point and the cast fell back on the 9.0 sentinel.
        DbAnim.ObjectWorld(obj);
        // ...and the hit stream's world box and grid cell, NOW. HitBounds.Refresh normally runs in the
        // fold, which is a frame away -- but the engine casts against these tiles inside the SAME message
        // batch that built the area, and a static never moves again, so a box filed a frame late is a floor
        // that does not exist for the one cast that needed it.
        if (!obj.IsNull) HitBounds.Refresh(obj.Index);
        if (border == 0) _realPlaced++;
        _byCell[cellKey] = t;
        _published[t] = 1f;
        _publishedReveal[t] = 1f;
        SceneDb.Tiles.Mark(t, Dirty.Created | Dirty.Xform);
        _spawned++;
        return t;
    }

    /// <summary>The widened ring (#353), against the DB instead of the engine's scene: the outermost engine
    /// ring tile of every strip is replayed further out, to <c>alloy.border</c> tiles. Sound for the same
    /// reason it is on the engine's scene -- the edge model is chosen per row/column from the nearest real
    /// tile, so every tile along a strip's outward axis is the same model at the same height.</summary>
    private static void ReplayOutward(DbId scene, ModelDef def, Vector3 pos, int orient, int border, Quaternion? rot)
    {
        AreaBorder.Steps(pos, _gridW, _gridH, out int sx, out int sy);
        int extra = _ring - EngineRing;
        if (extra <= 0 || (sx == 0 && sy == 0)) return;
        for (int i = 0; i <= (sx != 0 ? extra : 0); i++)
            for (int j = 0; j <= (sy != 0 ? extra : 0); j++)
            {
                if (i == 0 && j == 0) continue;
                var q = new Vector3(pos.X + sx * i * TileSize, pos.Y + sy * j * TileSize, pos.Z);
                if (Place(scene, def, q, orient, border, rot) >= 0) _replayed++;
            }
    }

    // ---- Scene vtable 0x40: SetTileLights ----

    /// <summary>Linux <c>Scene</c> +0x40 / Windows +0x38,
    /// <c>SetTileLights(Vector const&amp;, Vector const&amp;, Vector const&amp;)</c>: re-colour the tile's two
    /// MAIN lights. Ported write for write from @0x55edf0 -- colour, then <c>range</c>/<c>rangeAlt</c> 10/12
    /// for main light 1 and 5/8 for main light 2, <c>castShadows</c> 1 and <c>priority</c> 4 on both. The
    /// engine's early-out (nothing changes when the colour is already that) is kept: it is what stops a
    /// re-load re-dirtying every tile.</summary>
    public static void SetTileLights(DbId scene, Vector3 pos, Vector3 c1, Vector3 c2)
    {
        _palCalls++;
        if (c1 == Vector3.Zero) _palBlack1++;
        if (c2 == Vector3.Zero) _palBlack2++;
        int t = At(scene, pos);
        if (t < 0) { _tileLightMiss++; return; }
        bool moved = false;
        for (int c = 0; c < SceneDb.TileLightCount[t]; c++)
        {
            int k = SceneDb.TileLightIds[t * SceneDb.TileLightStride + c];
            if (k < 0 || !SceneDb.Lights.LiveAt(k)) continue;
            int slot = SceneDb.LitMainSlot[k];
            if (slot != 0 && slot != 1) continue;
            Vector3 want = slot == 0 ? c1 : c2;
            if (SceneDb.LitColour[k] == want) continue;
            SceneDb.LitColour[k] = want;
            SceneDb.LitRange[k] = slot == 0 ? Main1Range : Main2Range;
            SceneDb.LitRangeAlt[k] = slot == 0 ? Main1RangeAlt : Main2RangeAlt;
            SceneDb.LitShadow[k] = true;
            SceneDb.LitPriority[k] = MainPriority;
            SceneDb.Lights.Mark(k, Dirty.Material);
            moved = true;
        }
        if (moved) SceneDb.Tiles.Mark(t, Dirty.Material);
    }

    /// <summary>The palette rows the area message named, kept for the census and the live comparison. The
    /// COLOURS are the game layer's (<c>CNWCTile::SetMainLightColor</c> @0x90d050 reads RED/GREEN/BLUE out of
    /// the light-colour 2DA itself and hands the Vectors down), so CX reads no 2DA.</summary>
    public static void NoteRecord(DbId scene, Vector3 pos, int tileId, byte main1, byte main2,
                                 byte src1, byte src2, byte replaceRow, byte anim1, byte anim2, byte anim3)
    {
        int t = At(scene, pos);
        if (t < 0) return;
        SceneDb.TileSetId[t] = tileId;
        SceneDb.TileMain1[t] = main1;
        SceneDb.TileMain2[t] = main2;
        SceneDb.TileSrc1[t] = src1;
        SceneDb.TileSrc2[t] = src2;
        SceneDb.TileReplaceRow[t] = replaceRow;
        SceneDb.TileAnim1[t] = anim1;
        SceneDb.TileAnim2[t] = anim2;
        SceneDb.TileAnim3[t] = anim3;
    }

    // ---- Scene vtable 0x48: SetTileFlames ----

    /// <summary>Linux <c>Scene</c> +0x48 / Windows +0x40,
    /// <c>SetTileFlames(Vector const&amp;, CAurObject*, CAurObject*)</c>: the two <c>fx_flame01</c>
    /// "sourcelight" objects the tile's two source-light rows asked for. The game layer mints them
    /// (<c>CNWCTile::SetSourceLightColor</c> @0x90d3c0 calls <c>NewCAurObject("fx_flame01", "sourcelight")</c>
    /// and plays the animation NAMED by the palette row -- <c>"%i"</c> of the index -- then <c>Animate(1.0)</c>),
    /// so CX only files which objects a tile's flames are. Called with two nulls first, to clear.</summary>
    public static void SetTileFlames(DbId scene, Vector3 pos, DbId flame1, DbId flame2)
    {
        int t = At(scene, pos);
        if (t < 0) return;
        ModelDef? def = SceneDb.TileModel[t];
        MergePlan? plan = def != null ? Plan(def) : null;
        PlaceFlame(t, ref SceneDb.TileFlame1[t], flame1, plan?.Sl1 ?? -1, plan?.Sl1Pos ?? Vector3.Zero, plan?.Sl1Rot ?? Quaternion.Identity);
        PlaceFlame(t, ref SceneDb.TileFlame2[t], flame2, plan?.Sl2 ?? -1, plan?.Sl2Pos ?? Vector3.Zero, plan?.Sl2Rot ?? Quaternion.Identity);
        SceneDb.Tiles.Mark(t, Dirty.Tree);
    }

    /// <summary>One flame slot, ported from <c>Scene::SetSingleTileFlame</c> @0x55ef30: nothing happens when
    /// the slot already holds that object; otherwise the old one leaves the slot (its lights re-shade), and
    /// the new one takes the <c>sl1</c>/<c>sl2</c> PART's pose -- the engine copies <c>part+0x10</c> into
    /// <c>gob+0xa8</c> and <c>part+0x1c</c> into <c>gob+0xb4</c> -- enters the scene, and re-shades its own
    /// lights. The engine's parts are world-space by then; CX's are model-space, so the flame's world pose is
    /// the tile placement composed with the part's.
    /// <para>No <c>sl</c> part means the tile has nowhere to put a flame: the object is left where it is and
    /// the refusal is counted, never a flame silently at the origin.</para></summary>
    private static void PlaceFlame(int t, ref DbId slot, DbId want, int node, Vector3 nodePos, Quaternion nodeRot)
    {
        if (slot == want) return;
        if (!slot.IsNull) { MarkLightsOf(slot); slot = default; }
        if (want.IsNull) return;
        // VANILLA SKIPS, SILENTLY. Scene::SetSingleTileFlame @0x55ef30 guards the whole placement on
        // `srcPart != 0 && flame != 0`, so a tile whose model has no sl1/sl2 node clears the slot and never
        // attaches the object -- the flame simply does not appear. That is content, not a failure of ours
        // (Kingmaker's tilesets do it), so it is counted on the period line and not said out loud.
        if (node < 0) { _flamesNoSl++; return; }
        if (!SceneDb.Objects.Valid(want)) return;
        int o = want.Index;
        Quaternion tileRot = SceneDb.TileOrient[t] == 0 && SceneDb.TileModel[t] == null
            ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, SceneDb.TileOrient[t] * 90f * (MathF.PI / 180f));
        SceneDb.ObjPos[o] = SceneDb.TilePos[t] + Vector3.Transform(nodePos, tileRot);
        SceneDb.ObjRot[o] = Quaternion.Normalize(tileRot * nodeRot);
        SceneDb.ObjScene[o] = SceneDb.TileScene[t];
        SceneDb.ObjVisible[o] = true;
        Matrix4x4 w = Matrix4x4.CreateFromQuaternion(SceneDb.ObjRot[o]);
        w.Translation = SceneDb.ObjPos[o];
        SceneDb.ObjWorld[o] = w;
        SceneDb.Objects.Mark(o, Dirty.Xform | Dirty.Visible);
        MarkLightsOf(want);
        slot = want;
        _flames++;
    }

    /// <summary>The engine's <c>UpdateStaticLighting</c> over a flame gob's own lights, as a dirty mark: the
    /// static lighting a flame contributes has to be recomputed where it used to stand and where it now does.</summary>
    private static void MarkLightsOf(DbId obj)
    {
        if (!_objLights.TryGetValue(obj.Value, out List<int>? list)) return;
        foreach (int k in list) if (SceneDb.Lights.LiveAt(k)) SceneDb.Lights.Mark(k, Dirty.Xform | Dirty.Material);
    }

    private static int _flames, _flamesNoSl, _modelPulled, _swapOther;

    /// <summary>Linux <c>Scene</c> +0xf0 / Windows +0xe8, <c>GetNumTileFlames(Vector const&amp;)</c>.</summary>
    public static int NumTileFlames(DbId scene, Vector3 pos)
    {
        int t = At(scene, pos);
        if (t < 0) return 0;
        return (SceneDb.TileFlame1[t].IsNull ? 0 : 1) + (SceneDb.TileFlame2[t].IsNull ? 0 : 1);
    }

    /// <summary>Linux <c>Scene</c> +0xf8 / Windows +0xf0, <c>GetNumTileLights(Vector const&amp;)</c>.</summary>
    public static int NumTileLights(DbId scene, Vector3 pos)
    {
        int t = At(scene, pos);
        return t < 0 ? 0 : SceneDb.TileLightCount[t];
    }

    // ---- Scene vtable 0x50: ReplaceTileTexture ----

    /// <summary>Linux <c>Scene</c> +0x50 / Windows +0x48,
    /// <c>ReplaceTileTexture(Vector const&amp;, char const*, char const*)</c>: swap one texture NAME across the
    /// tile's meshes. The engine is called with <paramref name="oldTex"/> = <c>"replace_tex"</c> (the tileset's
    /// placeholder) and <paramref name="newTex"/> = the <c>TEXTURENAME</c> column of the area's texture
    /// palette row, which <c>CNWCTile::SetReplaceTexture</c> @0x90d580 reads for itself.
    /// <para>The swap is per PLACEMENT, so it cannot change the shared merge plan: CX records the substitution
    /// on the tile and marks it <see cref="Dirty.Texture"/>, and the renderer resolves the name per draw. A
    /// tile that does swap therefore shares its vertex range with every other placement of its model and
    /// differs only in a texture binding.</para>
    /// <para>Recording it is not enough: nothing read <see cref="SceneDb.TileReplaceTex"/>, so every swapped
    /// tile drew the tileset's <c>replace_tex</c> placeholder -- the ground patch showing the wrong floor.
    /// The name is applied to the placement's own parts as a per-part override, which is where
    /// <c>AurTextureReplaceTileReferences</c> @0x58e4b0 puts it too: it collects the tile's textures through
    /// <c>SceneBSPGetTileTextures</c> @0x57c210, which reads each part's material slot 0 and nothing else,
    /// and <c>CAurTexture::SetReference</c>s the one whose reference is the loaded <c>replace_tex</c>. Slot 0
    /// only, therefore, and matched by NAME because CX has no shared <c>CAurTexture</c> to match by
    /// identity.</para></summary>
    public static void ReplaceTileTexture(DbId scene, Vector3 pos, string newTex, string oldTex)
    {
        int t = At(scene, pos);
        if (t < 0) return;
        string? was = SceneDb.TileReplaceTex[t];
        SceneDb.TileReplaceTex[t] = string.IsNullOrEmpty(newTex) ? null : newTex;
        if (was == SceneDb.TileReplaceTex[t]) return;
        _swaps++;
        SceneDb.Tiles.Mark(t, Dirty.Texture);
        SceneDb.Touch(SceneDb.TileObj[t], Dirty.Texture);
        // An EMPTY name drops the override rather than keeping the last one: the engine's own empty-row call
        // is a no-op (AurTextureGet("") answers no bitmap and AurTextureReplaceTileReferences returns 0), so
        // there is no vanilla behaviour to match, and the recorded null has to mean what it says.
        _swapParts += Seam.ObjectTex.ReplaceByTexName(SceneDb.TileObj[t], oldTex, SceneDb.TileReplaceTex[t]);
    }

    /// <summary>Tile parts whose slot 0 the texture swap actually re-pointed. Zero with a non-zero
    /// <c>_swaps</c> means the tileset's placeholder is spelled something other than
    /// <see cref="ReplaceSlot"/> -- the swap recorded and drew nothing.</summary>
    private static int _swapParts;

    /// <summary>The placeholder name the tileset authors and the engine always passes as the texture to
    /// replace.</summary>
    public const string ReplaceSlot = "replace_tex";

    // ---- Scene vtable 0x58: DeleteTile ----

    /// <summary>Linux <c>Scene</c> +0x58 / Windows +0x50, <c>DeleteTile(Vector const&amp;)</c>. The record, its
    /// object, its part range and its lights die here and nowhere else (ADR 0028: CX produces the edge), so
    /// there is no sweep and no tombstone.</summary>
    public static void DeleteTile(DbId scene, Vector3 pos)
    {
        int t = At(scene, pos);
        if (t < 0) return;
        Drop(t);
        if (_borderWindow)
        {
            AreaBorder.Steps(pos, _gridW, _gridH, out int sx, out int sy);
            int extra = _ring - EngineRing;
            for (int i = 0; i <= (sx != 0 ? extra : 0); i++)
                for (int j = 0; j <= (sy != 0 ? extra : 0); j++)
                {
                    if (i == 0 && j == 0) continue;
                    int o = At(scene, new Vector3(pos.X + sx * i * TileSize, pos.Y + sy * j * TileSize, pos.Z));
                    if (o >= 0) Drop(o);
                }
        }
    }

    private static void Drop(int t)
    {
        if (!SceneDb.TileIsBorder[t]) _droppedReal++;
        DbId id = SceneDb.Tiles.IdAt(t);
        var cell = CellKey(SceneDb.TileScene[t], SceneDb.TileGridX[t], SceneDb.TileGridY[t]);
        if (_statics.Remove(cell, out List<DbId>? statics))
            foreach (DbId s in statics) { FreeLights(s, -1); FreeObject(s); }
        FreeLights(SceneDb.TileObj[t], t);
        FreeObject(SceneDb.TileObj[t]);
        SceneDb.TileObj[t] = default;
        SceneDb.TileModel[t] = null;
        _byCell.Remove(cell);
        SceneDb.Tiles.Free(id);
        _deleted++;
    }

    private static void FreeObject(DbId obj)
    {
        if (obj.IsNull || !SceneDb.Objects.Valid(obj)) return;
        // The destroy contract's first two steps: drop the scene's references to it SYNCHRONOUSLY (its
        // member list and the shell's count word are read by game code without waiting for a frame), then
        // tell the animation runtime before the record stops being addressable.
        DbScene.ObjectDied(obj);
        DbAnim.ObjectRetired(obj);
        // An animated tile's gob shell (AnimatedTileObject): the game only ever zeroes its copy
        // (CNWCTile::UnloadModel), so the shell is poisoned here or a late call reads a freed record.
        nint tileShell = SceneDb.ObjFacade[obj.Index];
        if (tileShell != 0) { CX.Alloy.Seam.ObjectSlots.FreeShellLists(tileShell); CX.Alloy.Seam.Facade.Retire(tileShell); }
        int fo = obj.Index, ff = SceneDb.ObjPartFirst[fo];
        for (int k = 0; k < SceneDb.ObjPartCount[fo]; k++)
            if (SceneDb.PartKindOf[ff + k] == PartKind.Aabb) _aabbFreed++;
        FreeLights(obj, -1);
        ModelDef? def = SceneDb.ObjModel[obj.Index];
        // HasPlan, not Plan: a model whose plan has already gone (an eviction) has had its census dropped
        // with it, and rebuilding the plan here would mint new MeshDefs and drop placements nobody noted.
        if (def != null && HasPlan(def)) Census(Plan(def), SceneDb.ObjPartCount[obj.Index] == 0, add: false);
        // The texture references the swap took, BEFORE the parts holding them go: this free path is its own
        // and never reached ObjectSlots.ReleaseRecord, so without this a swapped tile leaked one registry
        // reference per part at every area change -- a slot pinned resident for the session.
        CX.Alloy.Seam.ObjectTex.Free(obj);
        PartTree.Free(obj);
        Unhold(def);
        SceneDb.ObjModel[obj.Index] = null;
        SceneDb.ObjKindOf[obj.Index] = ObjKind.Free;
        SceneDb.Objects.Free(obj);
    }

    // ---- fog of war and the two ramps ----

    /// <summary>The free function <c>AuroraSetTileFoggedByWar</c> @0x453320, which
    /// <c>CNWCTile::SetTileExplored</c> @0x90de40 calls with <c>explored == 0</c> whenever the module's
    /// fog-of-war bits are set. While a tile is fogged its reveal ramp is held at 0, exactly as the
    /// @0x481000 BSP visitor holds the engine's.</summary>
    public static void SetFoggedByWar(DbId scene, Vector3 pos, bool fogged)
    {
        int t = At(scene, pos);
        if (t < 0) return;
        if (SceneDb.TileFogged[t] == fogged) return;
        SceneDb.TileFogged[t] = fogged;
        if (fogged) SceneDb.TileReveal[t] = 0f;
        SceneDb.Tiles.Mark(t, Dirty.Visible);
        Publish(t);
    }

    /// <summary>Linux <c>Scene</c> +0x80 / Windows +0x78, <c>CAurScene::ResetAreaTileFading()</c>: every tile
    /// back to fully drawn and fully revealed.</summary>
    public static void ResetFading(DbId scene)
    {
        for (int t = 1; t < SceneDb.Tiles.High; t++)
        {
            if (!SceneDb.Tiles.LiveAt(t) || SceneDb.TileScene[t] != scene) continue;
            SceneDb.TileFade[t] = 1f;
            SceneDb.TileFadeAcc[t] = 0f;
            SceneDb.TileReveal[t] = SceneDb.TileFogged[t] ? 0f : 1f;
            Publish(t);
        }
    }

    /// <summary>The per-frame half, and the only hot method here: both per-tile ramps, over the dense table.
    /// The camera-occlusion decision and the ramp arithmetic are <see cref="TileFadeRamp"/>'s already ported
    /// ones (<see cref="TileFadeRamp.Plane"/>, <see cref="TileFadeRamp.TwoDBoxAbovePlane"/>,
    /// <see cref="TileFadeRamp.Step"/>) -- DB mode changes where the per-tile boxes come from, not what the
    /// engine decides. Allocation-free.</summary>
    /// <summary>The fade plane's TARGET, as mode 0 reads it: the camera's FOLLOW target's position.
    /// <c>TileFadeRamp.OcclusionPlane</c> goes <c>Camera::BehaviorHost</c> -&gt;
    /// <c>AsBehaviorCameraFollow()</c> -&gt; its <c>Target</c> gob -&gt; <c>Gob::Position</c>: the followed
    /// character, NOT the scene's focus point, which is a different field with a different writer. The focus
    /// is the fallback for a camera with no follow target yet, because a plane through the origin ramps the
    /// wrong roofs (measured: focus &lt;0 0 0&gt; and the plane stood down every frame).</summary>
    public static bool FadeTarget(DbId scene, DbId cam, out Vector3 target)
    {
        target = default;
        if (SceneDb.Cameras.Valid(cam))
        {
            DbId follow = SceneDb.CamFollow[cam.Index];
            if (SceneDb.Objects.Valid(follow)) { target = SceneDb.ObjPos[follow.Index]; return true; }
        }
        if (!SceneDb.Scenes.Valid(scene)) return false;
        target = SceneDb.SceneFocus[scene.Index];
        return target != Vector3.Zero;
    }

    public static void TickFade(DbId scene, Vector3 target, Vector3 camPos, float dt, int geomFadeMode,
                                DbId cam = default)
    {
        _fadeTicks++;
        int fading = 0, veiled = 0, classified = 0, out_ = 0;
        bool planeOk = TileFadeRamp.Plane(target, camPos, out float nx, out float ny, out float d);
        // ProcessTriMeshParts @0x480d40 takes the fade path for (mode == 1 && the plane stands) || mode == 4:
        // mode 4 ("always") fades the roof whatever the camera is doing, so it must not be gated on the
        // plane -- and with mode 4 there is no plane to test against, so every tilefade-1 tile fades out.
        bool live = TileFadeRamp.FadeLive(geomFadeMode, planeOk);
        bool always = live && !planeOk;
        // Only tiles IN FRONT of the camera are classified. Mode 0's ramp culls against all six frustum
        // planes (TileFadeRamp.Tick's BoxVisible), as the engine's BSP visitor does; this culls against the
        // camera's near plane alone, which is the one that matters for the fault it prevents -- a roof
        // BEHIND the camera on the far side of the fade plane fading out unseen, and being transparent
        // already when the camera turns to it. SceneDb.CamVolume cannot serve here: a camera move CLEARS
        // CamVolumeSet (DbCamera.SetPosition/SetRotation) and the chase camera moves every frame, so at
        // animate time it is never set -- measured, "NO VIEW VOLUME YET" on every tick.
        bool cull = SceneDb.Cameras.Valid(cam);
        // DbCamera.Forward, not the raw column: the game turns the chase camera through its sub-object, so
        // CamRot stays identity and this near-plane test faced permanently straight down.
        Vector3 fwd = cull ? DbCamera.Forward(cam) : Vector3.Zero;
        if (cull && fwd.LengthSquared() < 1e-6f) cull = false;
        _culling = cull;
        for (int t = 1; t < SceneDb.Tiles.High; t++)
        {
            if (!SceneDb.Tiles.LiveAt(t) || SceneDb.TileScene[t] != scene) continue;
            int nb = SceneDb.TileFadeBoxN[t];
            bool inView = !cull || !BoxBehind(SceneDb.TileBmin[t], SceneDb.TileBmax[t], camPos, fwd);
            if (live && inView)
            {
                if (nb > 0) classified++;
                // CheckFadePlane @0x47bf30 walks the tile's tilefade-1 parts and fades it when ANY ONE of
                // them is not entirely beyond the plane -- part by part, never their union. A union box has
                // corners no part occupies, and those corners straddle planes every real part is clear of:
                // measured on the stock inn, the union faded 3 roof tiles where the engine faded 1.
                int baseAt = t * SceneDb.TileFadeBoxStride;
                bool fadingOut = always && nb > 0;
                if (!always)
                    for (int f = 0; f < nb; f++)
                    {
                        Vector3 lo = SceneDb.TileFadeBoxLo[baseAt + f], hi = SceneDb.TileFadeBoxHi[baseAt + f];
                        if (TileFadeRamp.TwoDBoxAbovePlane(lo.X, lo.Y, hi.X, hi.Y, nx, ny, d)) continue;
                        fadingOut = true;
                        break;
                    }
                if (fadingOut) out_++;
                TileFadeRamp.Step(ref SceneDb.TileFade[t], ref SceneDb.TileFadeAcc[t], fadingOut, dt);
            }
            else if (!live && SceneDb.TileFade[t] != 1f) { SceneDb.TileFade[t] = 1f; SceneDb.TileFadeAcc[t] = 0f; }

            float reveal = SceneDb.TileReveal[t];
            float want = SceneDb.TileFogged[t] ? 0f : 1f;
            if (reveal != want)
            {
                float step = dt * RevealRate;
                SceneDb.TileReveal[t] = want > reveal ? MathF.Min(want, reveal + step) : MathF.Max(want, reveal - step);
            }
            // Last, so a reveal that moved this tick reaches the parts this tick: publishing before the
            // reveal step left every fog-of-war ramp one frame behind its own value.
            Publish(t);
            if (SceneDb.TileFade[t] < 1f) fading++;
            if (SceneDb.TileReveal[t] < 1f) veiled++;
        }
        _fading = fading;
        _veiled = veiled;
        _classified = classified;
        _fadingOut = out_;
        _fadeMode = geomFadeMode;
        _planeOk = planeOk;
        _fadeTarget = target;
        if (fading > _fadingPeak) _fadingPeak = fading;
    }

    /// <summary>Is the world box entirely BEHIND the camera? The box's most forward corner is the one with
    /// the largest <c>dot(forward, corner - eye)</c>; when even that is negative, nothing of the box is in
    /// front of the eye.</summary>
    private static bool BoxBehind(Vector3 lo, Vector3 hi, Vector3 eye, Vector3 fwd)
    {
        float best = (fwd.X > 0f ? hi.X : lo.X) * fwd.X
                   + (fwd.Y > 0f ? hi.Y : lo.Y) * fwd.Y
                   + (fwd.Z > 0f ? hi.Z : lo.Z) * fwd.Z
                   - Vector3.Dot(fwd, eye);
        return best < 0f;
    }

    /// <summary>What the fade did on the last tick, in the SAME two quantities mode 0's ramp reports
    /// (<c>N classified and M fading out per frame</c>), so the two paths can be compared like for like --
    /// plus the tiles currently mid-ramp and the session peak, which is what says the roof fade worked in a
    /// session that ended with the camera somewhere else.</summary>
    public static int FadeTicks => _fadeTicks;
    /// <inheritdoc cref="FadeTicks"/>
    public static int Classified => _classified;
    /// <inheritdoc cref="FadeTicks"/>
    public static int FadingOut => _fadingOut;
    /// <inheritdoc cref="FadeTicks"/>
    public static int Fading => _fading;
    /// <inheritdoc cref="FadeTicks"/>
    public static int FadingPeak => _fadingPeak;

    private static int _fadeTicks, _fading, _fadingPeak, _veiled, _fadeMode, _classified, _fadingOut, _fadeBoxSpill;
    /// <summary><see cref="SetTileLights"/> calls whose cell held no tile: a palette the area never applied.</summary>
    private static int _tileLightMiss;
    /// <summary><see cref="SetTileLights"/> calls, and how many handed main light 1 / 2 black (lightcolor.2da
    /// row 0): a black main light is the area's choice, not a lost palette. Since boot, these four: the area
    /// edge that resets the other tallies fires AFTER <c>LoadArea</c>'s tile loop, which is when they count.</summary>
    private static int _palCalls, _palBlack1, _palBlack2;
    private static int _modelsWithAabb, _modelsNoAabb, _aabbParts, _partsMade, _aabbWanted, _aabbFreed, _droppedReal, _realPlaced;
    /// <summary>CX-118: walkmesh objects made from a <c>.wok</c>, placements whose tile model already carried
    /// an <c>aabb</c> node (so no <c>.wok</c> is loaded), and the three outcomes that make no object -- no
    /// readable resource (a refusal, named), an EMPTY walkmesh (what a tileset's filler tiles ship, and what
    /// the engine reads as a surface mesh with no face), and one that parsed with nodes but no
    /// <c>aabb</c>.</summary>
    private static int _wokMade, _wokInModel, _wokMissing, _wokNoNode, _wokEmpty;
    private static int _wokSaidN;
    private static readonly System.Collections.Generic.HashSet<string> _wokSaidNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Generic.HashSet<string> _wokSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>CX-118: the tile's WALKMESH, as its own invisible static object.
    /// <para>Vanilla's floor is this and nothing else. <c>SceneBSPAddPart</c> @0x47ca10 files any part whose
    /// <c>Part</c> vtable +0x98 (<c>AsPartAABB</c>) answers into the BSP cell's <c>+0x90</c> slot, and
    /// <c>HitPartOnBSPNode</c> @0x47b790 casts against exactly that, behind <c>hitcheck_static_bsp_parts</c>
    /// (which is 1). Measured live: the floor under the ladder's spawn answers <c>kind=1 face=44
    /// surface=0x10 nrm=(0,0,1) part=Object915</c> with the other two static arms off.</para>
    /// <para>The geometry is NOT in the tile's <c>.mdl</c>: it is a separate <c>.wok</c> resource under the
    /// same resref (restype 2016), an ASCII <c>#MAXWALKMESH</c> file wrapping one <c>node aabb</c>. Only ~15
    /// of ~1150 placements have an <c>aabb</c> node in the model itself, which is why DB mode had a floor on
    /// those and nowhere else -- and why <c>ComputeZValue</c> kept its 9.0 sentinel everywhere else.</para>
    /// <para><b>Both at once:</b> vanilla's <c>+0x90</c> is a plain assignment, so when a model carries an
    /// <c>aabb</c> AND a <c>.wok</c> exists, whichever part is added last wins -- it does not merge them. CX
    /// takes the model's when it has one and loads no <c>.wok</c> for that placement: one walkmesh per tile,
    /// never two coincident ones, and the ~15 that already worked keep answering exactly as before.</para></summary>
    private static void Walkmesh(DbId scene, ModelDef tileDef, Vector3 pos, Quaternion rot, in Matrix4x4 place)
    {
        if (_wokSeen.Add(tileDef.Name) && _wokSeen.Count <= 12)
            _log?.Info($"TileBuild: walkmesh census: tile '{tileDef.Name}' modelAabb={Plan(tileDef).AabbNodes} "
                     + "(0 means its floor must come from the .wok)");
        // The self-check fixtures place tiles of their own, which have no .wok by design -- 1120 of them in a
        // run. They are not this session's world, so they are not on its census either: counted, they read as
        // the area's own missing floors (and the tally is session-long, because an area edge fires AFTER the
        // tile loop and would wipe the area's real numbers).
        bool fixture = tileDef.Name.StartsWith("cxtest", StringComparison.OrdinalIgnoreCase);
        if (Plan(tileDef).AabbNodes > 0) { if (!fixture) _wokInModel++; return; }
        ModelDef? wok = ModelCache.Acquire(tileDef.Name + ModelFeed.WokSuffix, out string? why);
        if (wok == null)
        {
            if (!fixture) { _wokMissing++; Said(tileDef.Name, why ?? "the cache would not hand one back"); }
            return;
        }
        // An empty walkmesh is a tileset's filler tile saying "no floor here", not a failure: the engine's own
        // loader ends with a surface mesh of no faces for exactly these. Counted apart from a refusal, and
        // never said out loud -- it was what made every area read as thousands of MISSING walkmeshes.
        if (wok.Nodes.Length == 0) { if (!fixture) _wokEmpty++; ModelCache.Release(wok); return; }
        bool any = false;
        foreach (NodeDef nd in wok.Nodes) if (nd.Kind == NodeKind.Aabb && nd.Aabb != null && nd.Aabb.Tree.Length > 0) { any = true; break; }
        if (!any) { _wokNoNode++; ModelCache.Release(wok); Said(tileDef.Name, "it parsed with no aabb node"); return; }
        DbId obj = NewObject(scene, wok, pos, rot, place, tile: false, walkmesh: true);
        if (obj.IsNull) { ModelCache.Release(wok); return; }
        // Filed with the cell's statics, so the tile's Drop (DeleteTile, SceneRevoke) frees it and releases the
        // .wok reference: a walkmesh lives exactly as long as its tile.
        Cell(pos, out int gx, out int gy);
        var key = CellKey(scene, gx, gy);
        if (!_statics.TryGetValue(key, out List<DbId>? list)) _statics[key] = list = new List<DbId>(2);
        list.Add(obj);
        _wokMade++;
    }

    /// <summary>Say the first walkmesh refusal out loud: a tileset whose <c>.wok</c>s CX cannot read has no
    /// floor anywhere, and the symptom (a creature nine metres up, a fogged frame) names nothing.</summary>
    private static void Said(string model, string why)
    {
        // The self-check fixtures run first and have no .wok by design; letting them spend the budget means
        // the real tileset's first failure is never named, which is the one that matters.
        if (model.StartsWith("cxtest", StringComparison.OrdinalIgnoreCase)) return;
        // DISTINCT names: a tileset's filler tiles repeat hundreds of times and would otherwise spend the
        // whole budget saying the same thing, which is exactly what hid the real failures once.
        if (_wokSaidNames.Contains(model)) return;
        _wokSaidNames.Add(model);
        if (_wokSaidN >= 8) return;
        _wokSaidN++;
        _log?.Always($"TileBuild: no walkmesh for tile '{model}' -- {why}.");
        if (_wokSaidN < 8) return;
        _log?.Always($"TileBuild: a tile walkmesh could not be loaded ({_wokMissing} with no .wok resource, "
                   + $"{_wokNoNode} parsed with no aabb node). Those tiles have NO FLOOR: a creature's "
                   + "ComputeZValue cast misses and the engine keeps its Z=9 sentinel.");
    }

    private static bool _planeOk, _culling;
    private static Vector3 _fadeTarget;

    /// <summary>Tiles whose model has <c>tilefade 1</c> geometry at all -- the population that CAN fade. Zero
    /// here with a zero peak says the tileset, not the ramp.</summary>
    /// <summary>Live <c>aabb</c> parts in the whole database, counted on demand rather than tallied -- a
    /// tally cannot be read against the area edge that resets it, and this is the number that says whether
    /// the walkmeshes EXIST independently of whether the hit set files them.</summary>
    /// <summary>Live NON-border tile placements: the area's own tiles, the ones a floor cast needs.</summary>
    private static int LiveReal()
    {
        int n = 0;
        for (int t = 1; t < SceneDb.Tiles.High; t++) if (SceneDb.Tiles.LiveAt(t) && !SceneDb.TileIsBorder[t]) n++;
        return n;
    }

    private static int AabbLive() => AabbLive(out _);

    private static int AabbLive(out int boxed)
    {
        int n = 0;
        boxed = 0;
        for (int p = 1; p < SceneDb.Parts.High; p++)
        {
            if (!SceneDb.Parts.LiveAt(p) || SceneDb.PartKindOf[p] != PartKind.Aabb) continue;
            n++;
            if (SceneDb.HitPartBMin[p].X <= SceneDb.HitPartBMax[p].X) boxed++;
        }
        return n;
    }

    private static int FadeCapable()
    {
        int n = 0;
        for (int t = 1; t < SceneDb.Tiles.High; t++) if (SceneDb.Tiles.LiveAt(t) && SceneDb.TileFadeBoxN[t] > 0) n++;
        return n;
    }

    /// <summary>Hand the tile's two ramps to the draw the way seam-feed asks for them, and KEEP THEM APART.
    /// The camera-occlusion fade is an alpha -- <c>Scene</c>'s draw multiplies <c>TileData+0x70</c> into the
    /// part's own alpha (<c>part+0xa0</c>, src/Scene.c:6268-6271) -- so it goes to
    /// <see cref="SceneDb.PartFade"/>. The fog-of-war reveal is NOT: with <c>TileData+0xc8 &lt; 1</c> the
    /// engine enables FOG and scales the scene's fog colour by <c>reveal^2</c> and its start/end by
    /// <c>reveal</c> (src/Scene.c:6257-6266). Folding it into the alpha would make an unexplored tile
    /// transparent instead of fogged, so it goes to <see cref="SceneDb.PartReveal"/> and seam-feed turns it
    /// into fog parameters per draw.</summary>
    private static void Publish(int t)
    {
        float fade = SceneDb.TileFade[t], reveal = SceneDb.TileReveal[t];
        // the common case: a still tile costs two compares, not a part walk
        if (_published[t] == fade && _publishedReveal[t] == reveal) return;
        _published[t] = fade;
        _publishedReveal[t] = reveal;
        DbId obj = SceneDb.TileObj[t];
        if (obj.IsNull || !SceneDb.Objects.Valid(obj)) return;
        int o = obj.Index, first = SceneDb.ObjPartFirst[o], n = SceneDb.ObjPartCount[o];
        for (int k = 0; k < n; k++)
        {
            int p = first + k;
            bool moved = false;
            if (SceneDb.PartFade[p] != fade) { SceneDb.PartFade[p] = fade; moved = true; }
            if (SceneDb.PartReveal[p] != reveal) { SceneDb.PartReveal[p] = reveal; moved = true; }
            if (moved) SceneDb.TouchPart(p, Dirty.Material);
        }
    }

    /// <summary>The fade product each tile last published, so <see cref="TickFade"/> walks a tile's parts only
    /// when it moved. Seeded at -1 so the first publish always lands.</summary>
    private static readonly float[] _published = NewPublished(), _publishedReveal = NewPublished();
    private static float[] NewPublished() { var a = new float[SceneDb.MaxTiles]; Array.Fill(a, -1f); return a; }

    // ---- the merge plan ----

    /// <summary>The merge plan for <paramref name="def"/>, built on first use and shared from then on. ONE call
    /// to <see cref="MeshArena.Acquire"/> per merged mesh, for the model, so every placement of the model
    /// draws the same vertex ranges -- the whole point of CX-116's perf clause.</summary>
    public static MergePlan Plan(ModelDef def)
    {
        if (_plans.TryGetValue(def, out MergePlan? have)) return have;
        MergePlan plan = BuildPlan(def);
        _plans[def] = plan;
        _planBuilds++;
        return plan;
    }

    /// <summary>How many models have a plan, and whether one is held for <paramref name="def"/>.</summary>
    public static bool HasPlan(ModelDef def) => _plans.ContainsKey(def);

    private static MergePlan BuildPlan(ModelDef def)
    {
        var plan = new MergePlan();
        var buckets = new Dictionary<long, List<int>>();
        var order = new List<long>();
        var verts = new Dictionary<long, int>();
        // The fields behind each bucket key, so a 64-bit hash collision cannot merge unlike meshes. Per
        // PLAN: two different models' buckets may hash alike, which is not a collision.
        var keyFields = new Dictionary<long, MeshFields>();
        int unmerged = 0, meshNodes = 0;

        Matrix4x4[] toModel = NodeMatrices(def);
        bool[] animatedHalf = AnimatedHalf(def);
        var filedLo = new Vector3(float.MaxValue);
        var filedHi = new Vector3(float.MinValue);
        var fadeLo = new Vector3(float.MaxValue);
        var fadeHi = new Vector3(float.MinValue);
        var fadeParts = new List<(Vector3 Lo, Vector3 Hi)>(2);
        for (int i = 0; i < def.Nodes.Length; i++)
        {
            NodeDef n = def.Nodes[i];
            // Mesh-bearing nodes the engine FILES in TileData+0x30 and unions its tile box from. An AABB
            // walkmesh node answers AsPartTriMesh (PartAABB's slot 0x60 is PartTriMesh::AsPartTriMesh, dumped
            // with tools/abi/vtable-slots.py), so it is filed and counts; tilefade 2 and 4 are moved out.
            if (n.Mesh != null && !animatedHalf[i] && n.Mesh.Indices.Length >= 3 && n.Mesh.Positions.Length > 0
                && (n.Kind == NodeKind.TriMesh || n.Kind == NodeKind.Aabb) && n.Mesh.TileFade == 0)
            {
                Box(n.Mesh, toModel[i], ref filedLo, ref filedHi);
                plan.FiledNodes++;
            }
            if (n.Mesh != null && !animatedHalf[i] && n.Mesh.TileFade == TileFadeFades && n.Mesh.Positions.Length > 0)
            {
                Box(n.Mesh, toModel[i], ref fadeLo, ref fadeHi);
                Bounds(n.Mesh.BMin, n.Mesh.BMax, toModel[i], out Vector3 flo, out Vector3 fhi);
                fadeParts.Add((flo, fhi));
                plan.HasFade = true;
            }

            if (n.Kind != NodeKind.TriMesh || n.Mesh == null || !n.Mesh.Render) { unmerged++; continue; }
            meshNodes++;
            if (animatedHalf[i] || Animated(def, n)) { unmerged++; continue; }
            // The engine's own ceiling: CreatePartBuckets requires the bucket's total to stay BELOW 0xffff,
            // because the merged primitive is indexed as 16 bits -- and so is every Alloy draw
            // (GL_UNSIGNED_SHORT throughout). A full bucket is left alone and another opened. A single node
            // that cannot fit one on its own is refused here rather than silently overflowing an index.
            if (n.Mesh.Positions.Length > MaxBucketVerts) { unmerged++; _refusedBig++; Refuse($"{def.Name}: node '{n.Name}' has {n.Mesh.Positions.Length} vertices, past the 16-bit index -- it is not merged"); continue; }
            long key = BucketKey(n.Mesh);
            while (buckets.ContainsKey(key) && verts[key] + n.Mesh.Positions.Length > MaxBucketVerts)
                key = (long)Mix((ulong)key, 1);
            if (!buckets.TryGetValue(key, out List<int>? list))
            {
                buckets[key] = list = new List<int>(4);
                verts[key] = 0;
                order.Add(key);
                keyFields[key] = FieldsOf(n.Mesh);
            }
            else if (!keyFields.TryGetValue(key, out MeshFields have) || !have.Equals(FieldsOf(n.Mesh)))
            {
                // #25: the key is a 64-bit hash, so a collision (or a re-key landing on a live key) would
                // merge two unlike meshes. The fields are stored and compared, and a clash opens its own bucket.
                _collisions++;
                Refuse($"{def.Name}: bucket key collision on node '{n.Name}' -- it gets its own bucket");
                key = (long)Mix((ulong)key, 0x9e37);   // not-an-offset: a second probe for a colliding bucket key
                buckets[key] = list = new List<int>(4);
                verts[key] = 0;
                order.Add(key);
                keyFields[key] = FieldsOf(n.Mesh);
            }
            list.Add(i);
            verts[key] += n.Mesh.Positions.Length;
        }

        // CX-116: the tile's WALKMESH. Vanilla's floor cast is the BSP leaf's +0x90, which SceneBSPAddPart
        // @0x47ca10 fills from the tile part whose AsPartAABB is non-null -- i.e. from the tile model's own
        // aabb node. Counted per model so "the cast misses" can be told apart from "the model has none".
        foreach (NodeDef nd2 in def.Nodes)
            if (nd2.Kind == NodeKind.Aabb) { plan.AabbNodes++; if (nd2.Aabb != null && nd2.Aabb.Tree.Length > 0) plan.AabbTrees++; }
        if (plan.AabbNodes > 0) _modelsWithAabb++; else _modelsNoAabb++;
        plan.Unmerged = unmerged;
        plan.MeshNodes = meshNodes;
        int nb = order.Count;
        plan.Meshes = new MeshDef[nb];
        plan.Ranges = new int[nb];
        plan.FadeClass = new int[nb];
        plan.Members = new int[nb][];
        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        for (int b = 0; b < nb; b++)
        {
            List<int> members = buckets[order[b]];
            MeshDef merged = Concat(def, members, toModel);
            plan.Meshes[b] = merged;
            plan.Members[b] = members.ToArray();
            plan.FadeClass[b] = def.Nodes[members[0]].Mesh!.TileFade;
            plan.Ranges[b] = MeshArena.Acquire(merged);
            _arenaAsks++;
            if (members.Count > 1) _mergedNodes += members.Count - 1;
            bmin = Vector3.Min(bmin, merged.BMin);
            bmax = Vector3.Max(bmax, merged.BMax);
        }
        if (nb == 0) { bmin = def.BMin; bmax = def.BMax; }
        // #8: the cull box has to cover the ANIMATED half too (the <resref>a subtree and the nodes an
        // animation names are not in any bucket), so the model's own stated bounds are unioned in.
        plan.BMin = Vector3.Min(bmin, def.BMin);
        plan.BMax = Vector3.Max(bmax, def.BMax);
        plan.FiledBMin = plan.FiledNodes > 0 ? filedLo : plan.BMin;
        plan.FiledBMax = plan.FiledNodes > 0 ? filedHi : plan.BMax;
        plan.FadeBMin = plan.HasFade ? fadeLo : Vector3.Zero;
        plan.FadeBMax = plan.HasFade ? fadeHi : Vector3.Zero;
        plan.FadeLo = new Vector3[fadeParts.Count];
        plan.FadeHi = new Vector3[fadeParts.Count];
        for (int f = 0; f < fadeParts.Count; f++) { plan.FadeLo[f] = fadeParts[f].Lo; plan.FadeHi[f] = fadeParts[f].Hi; }
        plan.Lights = PlanLights(def, toModel);
        plan.Sl1 = def.Find(def.Name + "sl1");
        plan.Sl2 = def.Find(def.Name + "sl2");
        if (plan.Sl1 >= 0) Pose(toModel[plan.Sl1], out plan.Sl1Pos, out plan.Sl1Rot);
        if (plan.Sl2 >= 0) Pose(toModel[plan.Sl2], out plan.Sl2Pos, out plan.Sl2Rot);
        foreach (int r in plan.Ranges) if (r < 0) { _refusedArena++; Refuse($"{def.Name}: the mesh arena refused a merged tile mesh -- that bucket draws nothing"); }
        return plan;
    }

    /// <summary>A model-space box for one mesh under one node transform, unioned into the pair.</summary>
    private static void Box(MeshDef m, in Matrix4x4 w, ref Vector3 lo, ref Vector3 hi)
    {
        Bounds(m.BMin, m.BMax, w, out Vector3 a, out Vector3 b);
        lo = Vector3.Min(lo, a);
        hi = Vector3.Max(hi, b);
    }

    /// <summary>Position and rotation out of a node's model-space matrix (the flame placement reads these).</summary>
    private static void Pose(in Matrix4x4 m, out Vector3 pos, out Quaternion rot)
    {
        pos = m.Translation;
        rot = Quaternion.CreateFromRotationMatrix(m);
    }

    /// <summary>Each node's rest transform into model space: the parent chain's position/orientation/scale, in
    /// pre-order (a parent always precedes its children, so one pass suffices).</summary>
    private static Matrix4x4[] NodeMatrices(ModelDef def)
    {
        var m = new Matrix4x4[def.Nodes.Length];
        for (int i = 0; i < def.Nodes.Length; i++)
        {
            NodeDef n = def.Nodes[i];
            Matrix4x4 local = Matrix4x4.CreateScale(n.Scale <= 0f ? 1f : n.Scale)
                            * Matrix4x4.CreateFromQuaternion(Normalise(n.Orientation));
            local.Translation = n.Position;
            if (n.Parent >= i) { _outOfOrder++; Refuse($"a model node names a parent at or after itself ({n.Parent} >= {i}) -- it is treated as a root"); }
            m[i] = n.Parent >= 0 && n.Parent < i ? local * m[n.Parent] : local;
        }
        return m;
    }

    private static Quaternion Normalise(Quaternion q)
        => q.LengthSquared() > 1e-12f ? Quaternion.Normalize(q) : Quaternion.Identity;

    /// <summary>The merged primitive is indexed as 16 bits, so a bucket's vertices have to fit one
    /// (<c>CreatePartBuckets</c>' own <c>&lt; 0xffff</c> test, and Alloy's index type is
    /// <c>GL_UNSIGNED_SHORT</c> everywhere).</summary>
    private const int MaxBucketVerts = 65535;

    /// <summary>The ANIMATED half of a tile, as <c>CollapsePartTree</c> @0x562ff0 splits it: the subtree under
    /// the node named <c>&lt;resref&gt;a</c>. <c>SpawnPartsForTile</c> @0x563450 looks that node up by name and,
    /// when it exists, gives the tile a real <c>Gob</c> whose part tree is that subtree -- kept in MODEL space
    /// with the placement on the gob -- while everything else is flattened to world space, merged and filed.
    /// So a node under <c>&lt;resref&gt;a</c> never merges, whether or not an animation names it today.</summary>
    private static bool[] AnimatedHalf(ModelDef def)
    {
        var flag = new bool[def.Nodes.Length];
        // The engine keys on Model+0x10, the model's OWN name, which is what ModelDef.Name holds -- and it
        // looks the node up at all only when SpawnPartsForTile's `border` argument is 0: a ring tile takes
        // the gob-less path and its whole tree is collapsed. CX keeps the subtree unmerged either way,
        // because a ring tile's placement shares the model's one plan and a plan cannot be two things at
        // once. A ring tile's animated half therefore stays its own parts instead of being merged away:
        // a handful of draws on the ring, and nothing else.
        int root = def.Find(def.Name + "a");
        if (root < 0) return flag;
        flag[root] = true;
        for (int i = 0; i < def.Nodes.Length; i++)
        {
            NodeDef n = def.Nodes[i];
            if (n.Parent >= 0 && flag[n.Parent]) flag[i] = true;
        }
        return flag;
    }

    /// <summary>A node merges only when nothing animates it: an animation that names it, or a geometry
    /// controller with more than one key.
    /// <para><b>Deliberate difference from the engine</b> (<c>ponytail:</c> revisit if a tile animation is
    /// ever seen to move merged geometry). <c>CreatePartBuckets</c> @0x573570 gates on the MODEL:
    /// <c>MaxTree::AsModel(node+0x58)</c> with <c>Model+0xa0 &gt;= 1</c> -- one animation anywhere in the
    /// model and NOTHING in it is merged. Most NWN tiles declare at least one, so vanilla barely batches
    /// tiles at all. CX gates per NODE instead, which merges strictly more and is safe because an animated
    /// node is still excluded; the engine's two per-node terms (<c>render 1</c> on host and member,
    /// <c>shadow</c> equality) are in <see cref="BucketKey"/> and in the eligibility test above.</para></summary>
    private static bool Animated(ModelDef def, NodeDef n)
    {
        foreach (ControllerTrack c in n.Controllers)
            if (c.Times.Length > 1) return true;
        for (ModelDef? m = def; m != null; m = m.Super)
            foreach (AnimDef a in m.Anims)
                foreach (AnimNode an in a.Nodes)
                    if (string.Equals(an.Name, n.Name, StringComparison.OrdinalIgnoreCase) && an.Controllers.Length > 0)
                        return true;
        return false;
    }

    /// <summary>Everything that has to agree for two tile meshes to merge: the textures, the material, the
    /// transparency and <c>tilefade</c> classes, the fixed-function terms and which UV sets exist. A
    /// difference in any of them is a different draw, so it is a different bucket.</summary>
    private static long BucketKey(MeshDef m)
    {
        ulong h = 0xcbf29ce484222325;   // not-an-offset: the FNV-1a 64-bit offset basis
        foreach (string s in m.Textures) h = MixStr(h, s);
        h = MixStr(h, m.MaterialName);
        h = MixStr(h, m.RenderHint);
        h = Mix(h, (ulong)m.TileFade);
        h = Mix(h, (ulong)m.TransparencyHint);
        h = Mix(h, Bits(m.Diffuse));
        h = Mix(h, Bits(m.Ambient));
        h = Mix(h, Bits(m.Specular));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(m.Shininess));
        uint flags = (m.Shadow ? 1u : 0) | (m.Beaming ? 2u : 0) | (m.Lightmapped ? 4u : 0) | (m.RotateTexture ? 8u : 0);
        for (int s = 0; s < m.Uv.Length; s++) if (m.Uv[s].Length > 0) flags |= 16u << s;
        if (m.Colors.Length > 0) flags |= 1u << 8;
        if (m.Tangents.Length > 0) flags |= 1u << 9;
        return (long)Mix(h, flags);
    }

    private static ulong Bits(Vector3 v)
        => BitConverter.SingleToUInt32Bits(v.X) | ((ulong)BitConverter.SingleToUInt32Bits(v.Y) << 21)
           | ((ulong)BitConverter.SingleToUInt32Bits(v.Z) << 42);

    private static ulong Mix(ulong h, ulong v) => (h ^ v) * 0x100000001b3;   // not-an-offset: the FNV-1a 64-bit prime

    private static ulong MixStr(ulong h, string s)
    {
        foreach (char c in s) h = Mix(h, char.ToLowerInvariant(c));
        return Mix(h, (ulong)s.Length + 1);
    }

    /// <summary>Concatenate one bucket's meshes into a single model-space <see cref="MeshDef"/>: positions by
    /// the node's rest matrix, normals and tangents by its rotation, indices offset. This is the merge the
    /// engine does per placement, done once for the model.</summary>
    /// <summary>Members whose node transform mirrors (det &lt; 0) and whose winding the merge therefore had to
    /// reverse. Reported, because "the tileset mirrors tiles" is a fact about content that should be visible.</summary>
    internal static int MirroredMembers => _mirroredMembers;
    private static int _mirroredMembers;

    /// <summary>Merged members whose face table and triangle list disagree, so their per-face material ids
    /// could not be carried. Counted because the grass source reads those ids.</summary>
    internal static int UnalignedMembers => _unalignedMembers;
    private static int _unalignedMembers;

    private static MeshDef Concat(ModelDef def, List<int> members, Matrix4x4[] toModel)
    {
        MeshDef first = def.Nodes[members[0]].Mesh!;
        if (members.Count == 1 && toModel[members[0]].IsIdentity) return first;

        int nv = 0, ni = 0;
        foreach (int i in members) { MeshDef m = def.Nodes[i].Mesh!; nv += m.Positions.Length; ni += m.Indices.Length; }

        var outMesh = new MeshDef
        {
            Positions = new Vector3[nv],
            Normals = new Vector3[nv],
            Indices = new uint[ni],
            Textures = (string[])first.Textures.Clone(),
            MaterialName = first.MaterialName,
            RenderHint = first.RenderHint,
            Diffuse = first.Diffuse,
            Ambient = first.Ambient,
            Specular = first.Specular,
            Shininess = first.Shininess,
            TransparencyHint = first.TransparencyHint,
            TileFade = first.TileFade,
            Render = first.Render,
            Shadow = first.Shadow,
            Beaming = first.Beaming,
            Lightmapped = first.Lightmapped,
            RotateTexture = first.RotateTexture,
        };
        bool tangents = first.Tangents.Length > 0;
        bool colors = first.Colors.Length > 0;
        if (tangents) outMesh.Tangents = new Vector4[nv];
        if (colors) outMesh.Colors = new uint[nv];
        for (int s = 0; s < 4; s++) if (first.Uv[s].Length > 0) outMesh.Uv[s] = new Vector2[nv];

        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        int vAt = 0, iAt = 0;
        float area = 0f;
        var surfaces = new int[ni / 3];
        foreach (int node in members)
        {
            MeshDef m = def.Nodes[node].Mesh!;
            Matrix4x4 w = toModel[node];
            var rot = new Matrix4x4(w.M11, w.M12, w.M13, 0, w.M21, w.M22, w.M23, 0, w.M31, w.M32, w.M33, 0, 0, 0, 0, 1);
            for (int v = 0; v < m.Positions.Length; v++)
            {
                Vector3 p = Vector3.Transform(m.Positions[v], w);
                outMesh.Positions[vAt + v] = p;
                bmin = Vector3.Min(bmin, p);
                bmax = Vector3.Max(bmax, p);
                outMesh.Normals[vAt + v] = v < m.Normals.Length
                    ? Vector3.Normalize(Vector3.TransformNormal(m.Normals[v], rot)) : Vector3.UnitZ;
                if (tangents && v < m.Tangents.Length)
                {
                    var tg = m.Tangents[v];
                    Vector3 td = Vector3.Normalize(Vector3.TransformNormal(new Vector3(tg.X, tg.Y, tg.Z), rot));
                    outMesh.Tangents[vAt + v] = new Vector4(td, tg.W);
                }
                if (colors && v < m.Colors.Length) outMesh.Colors[vAt + v] = m.Colors[v];
                for (int s = 0; s < 4; s++)
                    if (outMesh.Uv[s].Length > 0 && v < m.Uv[s].Length) outMesh.Uv[s][vAt + v] = m.Uv[s][v];
            }
            // A MIRRORED node (negative determinant) reverses triangle orientation when its vertices are
            // baked into model space, so the merged mesh would wind backwards there: back-face culled away,
            // or lit from behind -- a dark surface where the tileset authored a lit one. The engine never
            // meets this because it draws each node under its own matrix and lets the rasteriser's own
            // front-face rule follow the matrix; a concatenation has one matrix for all of them, so the
            // winding has to be corrected here instead. Flipped per member, and counted.
            bool mirrored = w.GetDeterminant() < 0f;
            if (mirrored) _mirroredMembers++;
            for (int k = 0; k + 2 < m.Indices.Length; k += 3)
            {
                outMesh.Indices[iAt + k] = m.Indices[k] + (uint)vAt;
                outMesh.Indices[iAt + k + 1] = m.Indices[mirrored ? k + 2 : k + 1] + (uint)vAt;
                outMesh.Indices[iAt + k + 2] = m.Indices[mirrored ? k + 1 : k + 2] + (uint)vAt;
            }
            // The member's per-face MATERIAL ids, carried across the merge: the grass source tests them
            // (AurGrassMaterialInUse, GrassField.Db) and a merged mesh that reported one id for every face
            // would grow grass on the whole tile.
            bool aligned = m.Faces.Length * 3 == m.Indices.Length;
            if (!aligned) _unalignedMembers++;
            for (int t = 0; t * 3 + 2 < m.Indices.Length; t++)
                // -1, not 0, for a member whose face table does not line up with its triangle list: 0 is a
                // REAL material id, and a member whose faces and indices disagree (a vertex-group rebuild)
                // would shift every later member's ids -- which is one way grass ends up on a wall.
                surfaces[iAt / 3 + t] = aligned ? m.Faces[t].Surface : -1;
            area += m.SurfaceArea;
            vAt += m.Positions.Length;
            iAt += m.Indices.Length;
        }
        outMesh.Faces = Faces(outMesh);
        for (int t = 0; t < outMesh.Faces.Length && t < surfaces.Length; t++)
            outMesh.Faces[t].Surface = surfaces[t];
        outMesh.BMin = bmin;
        outMesh.BMax = bmax;
        outMesh.Average = (bmin + bmax) * 0.5f;
        outMesh.Radius = (bmax - bmin).Length() * 0.5f;
        outMesh.SurfaceArea = area;
        return outMesh;
    }

    /// <summary>The merged triangle list as faces, with each face's plane: the shadow and hit passes read
    /// these, and a concatenation has no face adjacency across its members (-1, as a border face carries).</summary>
    private static FaceDef[] Faces(MeshDef m)
    {
        var f = new FaceDef[m.Indices.Length / 3];
        for (int t = 0; t < f.Length; t++)
        {
            int a = (int)m.Indices[t * 3], b = (int)m.Indices[t * 3 + 1], c = (int)m.Indices[t * 3 + 2];
            Vector3 n = Vector3.Cross(m.Positions[b] - m.Positions[a], m.Positions[c] - m.Positions[a]);
            float len = n.Length();
            n = len > 1e-12f ? n / len : Vector3.UnitZ;
            f[t] = new FaceDef
            {
                V0 = a, V1 = b, V2 = c, Surface = 0, SmoothGroup = 0,
                Adj0 = -1, Adj1 = -1, Adj2 = -1,
                PlaneNormal = n, PlaneDistance = -Vector3.Dot(n, m.Positions[a]),
            };
        }
        return f;
    }

    /// <summary>The model's light nodes, resolved to model space with their rest controller values. The first
    /// two are the tile's MAIN lights -- the pair <c>TileData+0x0</c>/<c>+0x8</c> holds and
    /// <c>Scene::SetTileLights</c> re-colours -- in node order, which is the order
    /// <c>MdlNodeLight::InternalCreateInstance</c> @0x43f800 creates them in.</summary>
    private static PlanLight[] PlanLights(ModelDef def, Matrix4x4[] toModel)
    {
        int n = 0;
        foreach (NodeDef nd in def.Nodes) if (nd.Kind == NodeKind.Light) n++;
        if (n == 0) return Array.Empty<PlanLight>();
        int ml1 = def.Find(def.Name + "ml1"), ml2 = def.Find(def.Name + "ml2");
        var lights = new PlanLight[n];
        int at = 0;
        for (int i = 0; i < def.Nodes.Length; i++)
        {
            NodeDef nd = def.Nodes[i];
            if (nd.Kind != NodeKind.Light) continue;
            LightDef? ld = nd.Light;
            lights[at] = new PlanLight
            {
                Node = i,
                Pos = toModel[i].Translation,
                Colour = Ctl3(nd, CtlColour, Vector3.One),
                Range = Ctl1(nd, CtlRadius, 0f),
                RangeAlt = Ctl1(nd, CtlShadowRadius, 0f),
                Multiplier = Ctl1(nd, CtlMultiplier, 1f),
                Shadow = ld?.Shadow ?? false,
                AmbientOnly = ld?.AmbientOnly ?? false,
                Dynamic = ld?.Dynamic ?? false,
                Priority = ld?.Priority ?? 0,
                MainSlot = i == ml1 ? 0 : i == ml2 ? 1 : -1,
            };
            at++;
        }
        return lights;
    }

    /// <summary>A controller's value at key 0, one float.</summary>
    private static float Ctl1(NodeDef n, int type, float dflt)
    {
        ControllerTrack? c = n.Controller(type);
        return c != null && c.Values.Length > 0 ? c.Values[0] : dflt;
    }

    /// <summary>A controller's value at key 0, three floats.</summary>
    private static Vector3 Ctl3(NodeDef n, int type, Vector3 dflt)
    {
        ControllerTrack? c = n.Controller(type);
        return c != null && c.Columns >= 3 && c.Values.Length >= 3 ? new Vector3(c.Values[0], c.Values[1], c.Values[2]) : dflt;
    }

    // ---- light registration ----

    /// <summary>File the tile's lights into <see cref="SceneDb.Lights"/> AS THE TILE IS BUILT -- which is
    /// what deleted <c>LightRegistry</c>'s own BSP light pass. That pass existed only because
    /// <c>CollapsePartTree</c> clears every tile part's child list, so no part-tree walk could reach a tile
    /// light and the lights had to be recovered from the tile's own part list during the BSP traversal.
    /// CX-188 deleted the registry outright: <c>SceneWalk</c>, its one feed, went with the engine-scene
    /// Alloy path, so nothing ever admitted a light to it.</summary>
    private static void RegisterLights(DbId tile, DbId obj, MergePlan plan, in Matrix4x4 place, int[]? into)
    {
        if (into != null) Array.Fill(into, -1);
        if (plan.Lights.Length == 0) { if (into != null) SceneDb.TileLightCount[tile.Index] = 0; return; }
        int count = 0;
        foreach (PlanLight pl in plan.Lights)
        {
            if (into != null && count >= SceneDb.TileLightStride)
            { _lightSpill++; Refuse($"a tile model has more than {SceneDb.TileLightStride} light nodes -- the rest are not registered"); break; }
            DbId id = SceneDb.Lights.Alloc();
            if (id.IsNull) { _refusedLights++; Refuse($"the light table is full at {SceneDb.MaxLights} lights -- tile lights are being dropped"); break; }
            int k = id.Index;
            SceneDb.LitObj[k] = obj;
            // The part that instances the light node, when the part tree has one. Registered with DbInputs as
            // well, because the renderer has to see ONE light list however a light arrived -- and a tileset's
            // lights do not hang off an object's part tree the way a gob's do.
            int lp = PartOfNode(obj, pl.Node);
            SceneDb.LitPart[k] = lp;
            if (lp >= 0) { DbInputs.RegisterTileLight(lp, k); _dbInputsHeld++; }
            else _noPartForLight++;
            SceneDb.LitTile[k] = tile;
            SceneDb.LitPos[k] = Vector3.Transform(pl.Pos, place);
            SceneDb.LitColour[k] = pl.Colour;
            SceneDb.LitRange[k] = pl.Range;
            SceneDb.LitRangeAlt[k] = pl.RangeAlt;
            SceneDb.LitMultiplier[k] = pl.Multiplier;
            SceneDb.LitPriority[k] = pl.Priority;
            SceneDb.LitShadow[k] = pl.Shadow;
            SceneDb.LitAmbientOnly[k] = pl.AmbientOnly;
            SceneDb.LitDynamic[k] = pl.Dynamic;
            SceneDb.LitMainSlot[k] = pl.MainSlot;
            if (into != null) into[count] = k;
            else { if (!_objLights.TryGetValue(obj.Value, out List<int>? l)) _objLights[obj.Value] = l = new List<int>(2); l.Add(k); }
            count++;
            _litRegistered++;
        }
        if (into != null) SceneDb.TileLightCount[tile.Index] = count;
    }

    /// <summary>A static placeable's lights, by its object handle. #7: a static's lights used to be allocated
    /// and then forgotten -- nothing freed them, so every area change leaked a row per placeable lamp.</summary>
    private static readonly Dictionary<uint, List<int>> _objLights = new();
    /// <summary>One tile's light slots while they are being filled: the game thread is the only writer, so a
    /// single buffer serves and the fill allocates nothing.</summary>
    private static readonly int[] _lightScratch = new int[SceneDb.TileLightStride];
    private static int _dbInputsHeld, _noPartForLight;

    /// <summary>Free every light row <paramref name="obj"/> owns (a tile's through its slots, a static's
    /// through <see cref="_objLights"/>), symmetrically with <see cref="DbInputs"/>.</summary>
    private static void FreeLights(DbId obj, int tileSlot)
    {
        if (tileSlot >= 0)
        {
            int baseAt = tileSlot * SceneDb.TileLightStride;
            for (int c = 0; c < SceneDb.TileLightCount[tileSlot]; c++)
            {
                DropLight(SceneDb.TileLightIds[baseAt + c]);
                SceneDb.TileLightIds[baseAt + c] = -1;
            }
            SceneDb.TileLightCount[tileSlot] = 0;
            return;
        }
        if (!_objLights.Remove(obj.Value, out List<int>? list)) return;
        foreach (int k in list) DropLight(k);
    }

    private static void DropLight(int k)
    {
        if (k < 0 || !SceneDb.Lights.LiveAt(k)) return;
        int lp = SceneDb.LitPart[k];
        if (lp >= 0) { DbInputs.PartDestroyed(lp); _dbInputsHeld--; }
        SceneDb.LitPart[k] = -1;
        SceneDb.LitTile[k] = default;
        SceneDb.Lights.Free(SceneDb.Lights.IdAt(k));
    }

    /// <summary>The light rows a tile owns, as slot indices (-1 for an empty slot).</summary>
    public static int LightAt(int tileSlot, int which)
        => (uint)which < (uint)SceneDb.TileLightCount[tileSlot]
           ? SceneDb.TileLightIds[tileSlot * SceneDb.TileLightStride + which] : -1;

    /// <summary>The part in <paramref name="obj"/>'s range that instances model node
    /// <paramref name="node"/>, or -1 while <see cref="PartTree.Build"/> is a stub. Scanned rather than
    /// indexed: reference expansion means the range is not node-for-node.</summary>
    private static int PartOfNode(DbId obj, int node)
    {
        if (obj.IsNull || !SceneDb.Objects.Valid(obj)) return -1;
        int o = obj.Index, first = SceneDb.ObjPartFirst[o], n = SceneDb.ObjPartCount[o];
        for (int k = 0; k < n; k++)
            if (SceneDb.PartNode[first + k] == node && ReferenceEquals(SceneDb.PartModel[first + k], SceneDb.ObjModel[o]))
                return first + k;
        return -1;
    }

    // ---- bounds, stats, refusals ----

    /// <summary>A model-space box placed by a transform: the eight corners' world extent. The engine's
    /// equivalent is the union <c>SpawnTile</c> builds into <c>TileData+0x58</c>/<c>+0x64</c> over its trimesh
    /// parts' world boxes -- the same box, from the model rather than from baked vertices.</summary>
    public static void Bounds(Vector3 lo, Vector3 hi, in Matrix4x4 m, out Vector3 outLo, out Vector3 outHi)
    {
        var a = new Vector3(float.MaxValue);
        var b = new Vector3(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var p = new Vector3((c & 1) == 0 ? lo.X : hi.X, (c & 2) == 0 ? lo.Y : hi.Y, (c & 4) == 0 ? lo.Z : hi.Z);
            p = Vector3.Transform(p, m);
            a = Vector3.Min(a, p);
            b = Vector3.Max(b, p);
        }
        outLo = a;
        outHi = b;
    }

    /// <summary>The animation name the game layer's index selects, for the seam to forward
    /// (<c>CNWCTile::PlayAnimation</c>'s switch).</summary>
    public static string AnimName(int which) => (uint)which < (uint)AnimNames.Length ? AnimNames[which] : "";

    /// <summary>One clause of the Alloy period line. The two numbers the CX-116 perf clause is judged on are
    /// <c>meshes</c> (arena asks, one per distinct tile mesh) against <c>placements</c>.</summary>
    public static string Stats()
        => $"dbtiles {Placements} placements / {_plans.Count} models / {_arenaAsks} meshes, {_litRegistered} lights"
           + (_fadeTicks > 0
               ? $", fade {_classified} classified and {_fadingOut} fading out, {_fading} mid-ramp / "
                 + $"{_fadingPeak} peak over {_fadeTicks} ticks (mode {_fadeMode}, plane "
                 + (_planeOk ? "ok" : "down") + (_culling ? ", front cull on" : ", NO CAMERA FACING")
                 + $", {FadeCapable()} can fade, target {_fadeTarget:0.#})"
               : ", FADE NOT TICKED")
           + (_veiled > 0 ? $", {_veiled} fogged" : "")
           + (_heightTransition <= 0f ? ", HEIGHT STEP UNSET" : "")
           + (_statics0 > 0 ? $", {_statics0} off-grid statics" : "")
           + (_flames > 0 ? $", {_flames} flames placed" : "")
           + (_replayed > 0 ? $", {_replayed} ring replays" : "")
           + (_mergedNodes > 0 ? $", {_mergedNodes} nodes merged away" : "")
           + (_mirroredMembers > 0 ? $", {_mirroredMembers} mirrored members rewound" : "")
           + (_swaps > 0 ? $", {_swaps} texture swaps re-pointing {_swapParts} part(s)"
                         + (_swapParts == 0 ? " -- NO PART CARRIES THE PLACEHOLDER, so every swap drew nothing" : "") : "")
           + (_cellTaken > 0 ? $", {_cellTaken} cells respawned" : "")
           + Refusals();

    private static string Refusals()
    {
        string s = "";
        if (_refusedModel > 0) s += $", {_refusedModel} NO MODEL";
        if (_refusedTable > 0) s += $", {_refusedTable} TABLE FULL";
        if (_refusedHeight > 0) s += $", {_refusedHeight} NO HEIGHT STEP";
        if (_refusedLights > 0) s += $", {_refusedLights} LIGHTS DROPPED";
        if (_lightSpill > 0) s += $", {_lightSpill} LIGHT SLOTS SPILLED";
        if (_refusedBig > 0) s += $", {_refusedBig} MESHES PAST THE 16-BIT INDEX";
        if (_refusedArena > 0) s += $", {_refusedArena} ARENA REFUSALS";
        if (_collisions > 0) s += $", {_collisions} BUCKET KEY COLLISIONS";
        if (_outOfOrder > 0) s += $", {_outOfOrder} NODES OUT OF ORDER";
        if (_flamesNoSl > 0) s += $", {_flamesNoSl} flames with no sl node (vanilla skips these too)";
        if (_fadeBoxSpill > 0) s += $", {_fadeBoxSpill} TILEFADE PARTS PAST THE FADE TABLE";
        if (_tileLightMiss > 0) s += $", {_tileLightMiss} TILE PALETTES FOR A CELL WITH NO TILE (their main lights keep the model's colour)";
        s += $", tile palettes {_palCalls} (main1 black {_palBlack1}, main2 black {_palBlack2})";
        s += $", wok {_wokMade} made + {_wokInModel} already in the model"
           + (_wokEmpty > 0 ? $" + {_wokEmpty} empty by design" : "")
           + (_wokMissing + _wokNoNode > 0 ? $" / {_wokMissing} MISSING + {_wokNoNode} with no aabb node" : "")
           + $", walkmesh {_modelsWithAabb}/{_modelsWithAabb + _modelsNoAabb} models, {AabbLive(out int boxed)} aabb live ({boxed} with hit bounds), {_aabbParts} made of {_partsMade} parts, "
           + $"interior {_realPlaced} placed / {_droppedReal} dropped, {LiveReal()} live";
        if (_swapOther > 0) s += $", {_swapOther} UNKNOWN TEXTURE SWAPS";
        if (_modelPulled > 0) s += $", {_modelPulled} TILES DROPPED WITH THEIR MODEL";
        if (_noPartForLight > 0) s += $", {_noPartForLight} lights with no part record";
        return s;
    }

    private static readonly HashSet<string> _said = new();
    private static CX.Framework.ExtenderLog? _log;

    /// <summary>Attach the log so a refusal is a named line (no silent fallbacks: once per cause, loudly).</summary>
    public static void Attach(CX.Framework.ExtenderLog log)
    {
        _log = log;
        if (_evictWired) return;
        _evictWired = true;
        // A model leaving the cache takes its merge plan's arena ranges with it (ADR 0028 section 3:
        // eviction, not refcount zero, is the edge). Added, not assigned: CX-114 points the same hook at
        // the arena.
        ModelCache.Evicting += Forget;
    }

    private static bool _evictWired;

    private static void Refuse(string why)
    {
        if (!_said.Add(why)) return;
        _log?.Always($"TileBuild: REFUSED -- {why}");
    }

    /// <summary>The world edge: an area change drops every placement, its objects and its lights. The merge
    /// plans are kept, because they are keyed on model CONTENT and the next area re-uses every tile model it
    /// shares with this one (ADR 0028).</summary>
    public static void Revoke()
    {
        // NOT a teardown in DB mode. ADR 0028: a tile record dies when the game says so -- DeleteTile, or
        // the scene it belongs to being destroyed (SceneRevoke) -- never on a renderer-side world edge.
        // Measured: AlloyRenderer.AreaEdge fires mid-load, AFTER CNWCArea::LoadArea's tile loop and BEFORE
        // LoadBorder, and this wiped exactly the real tiles. One ladder: "dropping 1155 (15 aabb)", then
        // "dropping 19 (19 aabb)" -- the whole of area 2's walkmesh-carrying geometry -- leaving 1171
        // placements with 1 aabb part, which is the floor every HitCheck then failed to find and the +9
        // focus Z of CX-185.
        if (!Renderer.On) DropAll();
        ResetTallies();
    }

    /// <summary>The scene edge: every tile placement of <paramref name="scene"/> dies with it. This is the
    /// DB-mode teardown, and it is scoped -- a GUI panel's scene dying must not take the world's tiles.</summary>
    public static void SceneRevoke(DbId scene)
    {
        for (int t = 1; t < SceneDb.Tiles.High; t++)
            if (SceneDb.Tiles.LiveAt(t) && SceneDb.TileScene[t] == scene) Drop(t);
        ResetTallies();
    }

    /// <summary>Drop every placement whatever the mode: the off-device suites' teardown between cases, and
    /// the one entry that is not a lifetime edge. Nothing in a session calls it.</summary>
    public static void ResetAll()
    {
        DropAll();
        ResetTallies();
    }

    private static void DropAll()
    {
        for (int t = 1; t < SceneDb.Tiles.High; t++) if (SceneDb.Tiles.LiveAt(t)) Drop(t);
        foreach (List<DbId> statics in _statics.Values) foreach (DbId s in statics) FreeObject(s);
        _statics.Clear();
        _byCell.Clear();
    }

    /// <summary>The per-area tallies the period line reports. The merge plans are NOT cleared: they key on
    /// the ModelDef instance, which the model cache keeps resident past its last reference and re-hands to
    /// the next area naming the same resref. A plan dies with its model, in Forget.</summary>
    private static void ResetTallies()
    {
        _spawned = _deleted = _replayed = _statics0 = 0;
        _refusedModel = _refusedTable = _refusedLights = _refusedHeight = 0;
        _refusedBig = _refusedArena = _collisions = _outOfOrder = _cellTaken = _lightSpill = 0;
        _flames = _flamesNoSl = _noPartForLight = 0;
        _aabbParts = _partsMade = _aabbWanted = _aabbFreed = _droppedReal = _realPlaced = 0;
        _litRegistered = _swaps = _swapParts = 0;
    }

    /// <summary>Drop the merge plans too: a model release (the cache's refcount reaching 0) invalidates the
    /// plan's arena ranges, so the plan goes with it.</summary>
    public static void Forget(ModelDef def)
    {
        // A plan normally goes only when its model is evicted, which a placement's reference prevents --
        // but ModelCache.Clear() (a module unload or a source change) evicts regardless. A placement still
        // standing on this model is then a record whose geometry is gone, so it is dropped here rather than
        // left pointing at a released arena range (#18).
        if (!_plans.Remove(def, out MergePlan? plan)) return;
        for (int t = 1; t < SceneDb.Tiles.High; t++)
            if (SceneDb.Tiles.LiveAt(t) && ReferenceEquals(SceneDb.TileModel[t], def)) { _modelPulled++; Drop(t); }
        // The two plan-scoped counters go with the plan, so the period line's "meshes" is what the table
        // actually holds rather than everything it ever held (#17).
        _arenaAsks -= plan.Meshes.Length;
        foreach (int[] members in plan.Members) if (members.Length > 1) _mergedNodes -= members.Length - 1;
        foreach (int r in plan.Ranges) if (r >= 0) MeshArena.Release(r);
    }
}
