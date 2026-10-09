using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>CX-120 seam-feed: <b>what the non-mesh consumers read in DB mode.</b> Lights, emitters, beams, the
/// sky, the highlight set and the lightmap key all used to be answered by walking engine objects -- a BSP node
/// list, a draw bucket, three gobs off <c>Scene+0x18/+0x38</c>, a <c>Gob</c> flag. Each is a database question
/// here, and this is where it is asked.
/// <para><b>Why a side table rather than more <see cref="SceneDb"/> columns.</b> A light part is one in a
/// thousand, so a column over <see cref="SceneDb.MaxParts"/> would cost megabytes to answer a question about a
/// few thousand records. The two side tables below are built from the SAME dirty fold the record feed runs, so
/// they are change-driven in exactly the same way and cannot drift from it.</para>
/// <para><b>What is a seam, not an answer.</b> The consumers themselves (<c>LightmapBake</c>,
/// <c>EmitterSim</c>, <c>Particles</c>, <c>Beams</c>, <c>SkyBox</c>,
/// <c>LightmapBake</c>) are still keyed on engine pointers. Retargeting them is theirs to do; what is here is
/// the engine-free input they take, in the shape they take it, so that retarget is a key change and not a
/// re-derivation.</para></summary>
internal static partial class DbInputs
{
    /// <summary>Light and emitter parts held at once. A heavy SoW area measured a few hundred lights; the
    /// ceiling is loud (<see cref="Spilled"/>) rather than silent.</summary>
    internal const int MaxLights = 1 << 12, MaxEmitters = 1 << 12;

    // ---- lights ----
    private static readonly int[] _lightPart = new int[MaxLights];
    private static readonly LightDef?[] _lightDef = new LightDef?[MaxLights];
    /// <summary>The tile-light record (<see cref="SceneDb.LitColour"/> row) a TILE light reads its colour, range
    /// and multiplier from, or -1. A tileset's main lights are coloured by the area palette at runtime
    /// (<see cref="TileBuild.SetTileLights"/>); their model rest keys are not what the game shows.</summary>
    private static readonly int[] _lightLit = new int[MaxLights];
    private static int _lightN;
    /// <summary>What a light node that states no light fields of its own falls back to: the defaults
    /// <c>MdlNodeLight</c>'s constructor leaves when the file sets none. Shared, never written.</summary>
    private static readonly LightDef DefaultLight = new();
    /// <summary>Light parts filed with no light fields of their own.</summary>
    internal static int LightsWithoutDef => _lightNoDef;
    private static int _lightNoDef;
    /// <summary>Emitter NODES whose parsed model declared no emitter block, so nothing registered them. An
    /// emitter kind with no block is an emitter that can never spawn; counted so "no particles on that model"
    /// has a line of its own.</summary>
    internal static int EmittersWithoutDef => _emitNoDef;
    private static int _emitNoDef;
    // ---- emitters ----
    private static readonly int[] _emitPart = new int[MaxEmitters];
    private static readonly EmitterDef?[] _emitDef = new EmitterDef?[MaxEmitters];
    private static int _emitN;

    /// <summary>Entries a table could not hold. Counted and reported; never a quiet hole.</summary>
    internal static int Spilled { get; private set; }

    internal static int LightCount => _lightN;

    /// <summary>CX-120: bumped by every write that can change what <see cref="DbLights.Walk"/> produces -- a
    /// light registered or dropped, a tile light's record bound to a part, a light column animated, a light's
    /// part moving. <see cref="DbLights"/> rebuilds the rig only when this moves (plus the area edge and the
    /// scene it is asked for), instead of once per world frame.
    /// <para>It is a WRITER's counter rather than a dirty list because a dirty list is consumed, and a second
    /// consumer taking it first would silently freeze the lighting. A missed writer is the failure mode here,
    /// which is why <c>DbLights</c> re-walks periodically and SHOUTS if the answer changed.</para></summary>
    internal static uint LightRev => _lightRev;
    private static uint _lightRev = 1;

    /// <summary>Say that something the light rig reads has changed.</summary>
    internal static void LightsChanged() => _lightRev++;
    internal static int EmitterCount => _emitN;

    /// <summary>Light <paramref name="k"/>: which part carries it, its model-declared parameters, and its
    /// world placement.
    /// <para>R1-41 closed: the colour, radius and multiplier come from the part's animated columns when an
    /// animation has written them (seam-anim's pose step samples <c>LightColor</c>/<c>LightRadius</c>/
    /// <c>LightMultiplier</c> like any other controller) and from the node's REST keys until then -- so a
    /// guttering torch gutters and a light with no such controller costs exactly what it did.
    /// See <c>cx-wt/notes/seam-anim-light-columns.md</c>.</para></summary>
    internal static bool Light(int k, out DbLight light)
    {
        light = default;
        if ((uint)k >= (uint)_lightN) return false;
        int p = _lightPart[k];
        if (!SceneDb.Parts.LiveAt(p)) return false;
        NodeDef? nd = NodeOf(p);
        if (nd == null) return false;
        Matrix4x4 w = SceneDb.PartWorld[p];
        int l = _lightLit[k];
        bool tile = l >= 0 && SceneDb.Lights.LiveAt(l);
        light = new DbLight
        {
            Part = p,
            Obj = SceneDb.PartObj[p],
            Position = new Vector3(w.M41, w.M42, w.M43),
            // R1-41 closed: the animated column when a controller has written it, else the tile record (the
            // area palette's colour) for a tile light, else this node's rest key.
            Colour = DbAnim.LitColour3(p, tile ? SceneDb.LitColour[l] : Rest3(nd, CtlColor, Vector3.One)),
            Radius = DbAnim.LitRadius1(p, tile ? SceneDb.LitRange[l] : Rest1(nd, CtlRadius, 1f)),
            Multiplier = DbAnim.LitMul1(p, tile ? SceneDb.LitMultiplier[l] : Rest1(nd, CtlMultiplier, 1f)),
            Def = _lightDef[k],
            // NOT PartEnabled: a light part is no geometry, so the engine's own enable gate
            // (TreeSetGeometryFlags @0x43e620) turns it OFF -- and the engine's light walk never reads that
            // byte. Gating on it here would put out every object light in the area.
            Enabled = SceneDb.ObjVisible[SceneDb.PartObj[p].Index],
            Lit = tile ? l : -1,
            Static = tile ? TileLitStatic(l) : ObjLitStatic(p),
            Id = tile ? LitId(l) : (IntPtr)(p + 1),
        };
        return true;
    }

    /// <summary>The identity a <see cref="SceneDb.Lights"/> record carries into the rig and the bake. Offset
    /// past the part-index space so a tile light and an object light can never collide on one id.</summary>
    internal static IntPtr LitId(int lit) => (IntPtr)(SceneDb.MaxParts + 1L + lit);

    /// <summary>One light straight off the <see cref="SceneDb.Lights"/> table -- the AUTHORITATIVE tile-light
    /// set. A tile's light node is not a part in most tile trees, so the part-registered enumeration saw only
    /// the handful that happened to have one (4 of 22 live); this is where the rest are, kept current by
    /// <c>Scene::SetTileLights</c> from the area palette.</summary>
    internal static bool LitLight(int lit, out DbLight light)
    {
        light = default;
        if (!SceneDb.Lights.LiveAt(lit)) return false;
        DbId obj = SceneDb.LitObj[lit];
        // The tile's own animations (Day/Night, AnimLoop) drive the light through its part, as they drive an
        // object light: the window lights are radius 0 until "Night" plays.
        int lp = SceneDb.LitPart[lit];
        if (lp >= 0 && !SceneDb.Parts.LiveAt(lp)) lp = -1;
        light = new DbLight
        {
            Part = lp,
            Obj = obj,
            Position = SceneDb.LitPos[lit],
            Colour = DbAnim.LitColour3(lp, SceneDb.LitColour[lit]),
            Radius = DbAnim.LitRadius1(lp, SceneDb.LitRange[lit]),
            Multiplier = DbAnim.LitMul1(lp, SceneDb.LitMultiplier[lit]),
            Def = null,
            // A tile light has no object to hide it; one that belongs to a placement follows that object.
            Enabled = !SceneDb.Objects.Valid(obj) || SceneDb.ObjVisible[obj.Index],
            Lit = lit,
            Static = TileLitStatic(lit),
            Id = LitId(lit),
        };
        return true;
    }

    /// <summary>The bake's static verdict for a tile light: its record is not flagged dynamic, no multi-key
    /// track drives its part, and the tile's runs have finished blending in.</summary>
    private static bool TileLitStatic(int lit)
    {
        if (SceneDb.LitDynamic[lit]) return false;
        int lp = SceneDb.LitPart[lit];
        return lp < 0 || !SceneDb.Parts.LiveAt(lp) || !DbAnim.LitMoving(lp) && DbAnim.Settled(SceneDb.PartObj[lp]);
    }

    /// <summary>The bake's static verdict for an object light, mode 0's <c>LightmapBake.Classify</c> rule: any
    /// light present at the close that is not carried by a creature and that no multi-key track drives, once
    /// its object's runs have settled -- a placeable's lamp included, Static flag or not.</summary>
    private static bool ObjLitStatic(int p)
    {
        DbId o = SceneDb.PartObj[p];
        if (!SceneDb.Objects.Valid(o) || (SceneDb.ObjFlags[o.Index] & ObjFlag.Creature) != 0) return false;
        return !DbAnim.LitMoving(p) && DbAnim.Settled(o);
    }

    /// <summary>Is this light's record ambient-only? The <see cref="SceneDb.Lights"/> table states it; a
    /// part-registered light says so through its <c>LightDef</c>.</summary>
    internal static bool AmbientOnly(in DbLight l)
        => l.Lit >= 0 ? SceneDb.LitAmbientOnly[l.Lit] : l.Def is { AmbientOnly: true };

    /// <summary>Emitter <paramref name="k"/>: its part, its model-declared parameters and its world placement.
    /// A CHUNK emitter spawns objects rather than particles, which is why its spawn goes back through the
    /// object seam (<see cref="ChunkSpawn"/>) instead of into the particle arrays.</summary>
    internal static bool Emitter(int k, out DbEmitter emitter)
    {
        emitter = default;
        if ((uint)k >= (uint)_emitN) return false;
        int p = _emitPart[k];
        if (!SceneDb.Parts.LiveAt(p)) return false;
        Matrix4x4 w = SceneDb.PartWorld[p];
        emitter = new DbEmitter
        {
            Part = p,
            Obj = SceneDb.PartObj[p],
            World = w,
            Def = _emitDef[k],
            // NOT PartEnabled, for the same reason as a light: an emitter part is no geometry, so the gate
            // turns it off and the engine's emitter walk does not care.
            Enabled = SceneDb.ObjVisible[SceneDb.PartObj[p].Index],
        };
        return true;
    }

    /// <summary>R1-5: the light controller ids come from <see cref="Ctl"/>, the parser's own table, not from a
    /// private copy. <c>ControllerTrack.Type</c> carries the BINARY FILE's numbering (CX-112), which is not the
    /// numbering the engine's ASCII parser registers the same controllers under (colour 100, radius 0x70,
    /// multiplier 0xa4 there) and cannot be derived from it -- the geometry position is 8 here and 0x10 there,
    /// a different delta again. One table, so the two cannot drift.</summary>
    private const int CtlColor = Ctl.LightColor, CtlRadius = Ctl.LightRadius,
                      CtlMultiplier = Ctl.LightMultiplier;

    /// <summary>The light node's name, for diagnostics.</summary>
    internal static string NodeName(int p) => NodeOf(p)?.Name ?? "?";

    /// <summary>The light node's file fields: an object light's own <see cref="DbLight.Def"/>, else the node
    /// behind a tile light's part. Null when the light has neither.</summary>
    internal static LightDef? LightDefOf(in DbLight l) => l.Def ?? (l.Part >= 0 ? NodeOf(l.Part)?.Light : null);

    private static NodeDef? NodeOf(int p)
    {
        ModelDef? m = SceneDb.PartModel[p];
        int n = SceneDb.PartNode[p];
        return m != null && (uint)n < (uint)m.Nodes.Length ? m.Nodes[n] : null;
    }

    private static float Rest1(NodeDef nd, int type, float def)
    {
        ControllerTrack? c = nd.Controller(type);
        return c != null && c.Values.Length > 0 ? c.Values[0] : def;
    }

    private static Vector3 Rest3(NodeDef nd, int type, Vector3 def)
    {
        ControllerTrack? c = nd.Controller(type);
        return c != null && c.Values.Length >= 3 ? new Vector3(c.Values[0], c.Values[1], c.Values[2]) : def;
    }

    /// <summary>A part was created: file it under whichever non-mesh kind it is. Called from the record feed's
    /// own fold, so the side tables move on the same edge the records do.</summary>
    internal static void PartCreated(int p)
    {
        _lightRev++;
        NodeDef? nd = NodeOf(p);
        if (nd == null) return;
        // R1-22: idempotent. RegisterTileLight routes through here too, so a tile whose light the tiles
        // stream registers AND whose part the record fold creates would otherwise be two lights at one place
        // -- which reads as "that torch is twice as bright" and is the hardest kind of lighting bug to see.
        // CX-116: the KIND files it, not the presence of parsed light FIELDS. A light node whose file states
        // none still shades -- its colour, radius and multiplier are controllers, which is exactly
        // fx_placeable01's "auroralight01", the node every placeable lamp's colour goes through
        // (CNWCArea::AddPlaceableObjectLight -> Gob::ControlPartLightColor @0x4436d0, which is also what
        // Part::AsPartLight answers for). Requiring NodeDef.Light left every one of those dark.
        if (nd.Kind == NodeKind.Light)
        {
            for (int k = 0; k < _lightN; k++) if (_lightPart[k] == p) return;
            if (_lightN >= MaxLights) { Spilled++; return; }
            if (nd.Light == null) _lightNoDef++;
            _lightPart[_lightN] = p; _lightDef[_lightN] = nd.Light ?? DefaultLight; _lightLit[_lightN] = -1; _lightN++;
        }
        else if (nd.Kind == NodeKind.Emitter)
        {
            if (nd.Emitter == null) { _emitNoDef++; return; }
            for (int k = 0; k < _emitN; k++) if (_emitPart[k] == p) return;
            if (_emitN >= MaxEmitters) { Spilled++; return; }
            _emitPart[_emitN] = p; _emitDef[_emitN] = nd.Emitter; _emitN++;
        }
    }

    /// <summary>A part died: drop it from whichever table holds it. Swap-with-last, so the tables stay dense
    /// and a walk of them is a walk of what is live.</summary>
    internal static void PartDestroyed(int p)
    {
        _lightRev++;
        for (int k = 0; k < _lightN; k++)
            if (_lightPart[k] == p)
            {
                _lightPart[k] = _lightPart[--_lightN]; _lightDef[k] = _lightDef[_lightN]; _lightLit[k] = _lightLit[_lightN];
                _lightDef[_lightN] = null; break;
            }
        for (int k = 0; k < _emitN; k++)
            if (_emitPart[k] == p)
            {
                _emitPart[k] = _emitPart[--_emitN]; _emitDef[k] = _emitDef[_emitN]; _emitDef[_emitN] = null;
                // CX-120: the emitter's two pieces of per-part state -- its animated channel block and the
                // simulation's own slot (its particles AND its texture reference) -- are keyed by this index,
                // and the database will re-tenant it. Released on the edge, not by a stale sweep.
                DbEmitterChannels.Forget(p);
                EmitterSim.ForgetDbPart(p);
                break;
            }
    }

    /// <summary>TILE lights. A tileset's lights do not hang off an object's part tree -- the tiles stream owns
    /// the tile records and registers their lights here, because the renderer must see one light list however
    /// a light arrived. SEAM: <c>tar/tiles</c> calls this; until it does the list is the object lights alone,
    /// which is a visible difference and is why the count is on the period line.</summary>
    /// <summary>CX-116: is part <paramref name="p"/> a LIGHT part, the way <c>Part::AsPartLight()</c> answers
    /// it -- the node's kind, not this table's membership. The two differ: a part is filed here only when its
    /// node also carries parsed light FIELDS (<see cref="NodeDef.Light"/>), while the engine's cast succeeds
    /// for any light node. A recolour has to follow the engine, because <c>fx_placeable01</c>'s
    /// <c>auroralight01</c> -- which every placeable lamp's colour goes through -- is exactly such a node.</summary>
    internal static bool IsLight(int p) => NodeOf(p)?.Kind == NodeKind.Light;

    /// <summary>CX-116: the <see cref="SceneDb.Lights"/> row part <paramref name="p"/> reads as a TILE light,
    /// or -1. The feed walks that table directly for tile lights, so a setter has to write there too.</summary>
    internal static int LitOf(int p)
    {
        for (int k = 0; k < _lightN; k++) if (_lightPart[k] == p) return _lightLit[k];
        return -1;
    }

    internal static void RegisterTileLight(int part, int lit)
    {
        _lightRev++;
        if (!SceneDb.Parts.LiveAt(part)) return;
        PartCreated(part);
        for (int k = 0; k < _lightN; k++)
            if (_lightPart[k] == part) { _lightLit[k] = lit; break; }
        TileLights++;
    }

    /// <summary>Tile lights registered by the tiles stream this session.</summary>
    internal static int TileLights { get; private set; }

    /// <summary>CHUNK emitters spawn OBJECTS, not particles (a shattering crate is gobs). SEAM: seam-obj owns
    /// object creation, so the emitter simulation asks for a spawn through this delegate rather than minting a
    /// record itself. Unset means a chunk emitter spawns nothing, which is counted below.</summary>
    internal static Func<DbId, string, Matrix4x4, DbId>? ChunkSpawn;
    /// <summary>A live chunk's per-frame placement: its particle's position and spin (<c>PartEmitter::RenderNothing</c>
    /// @0x5ad200 sets the gob's position and orientation every frame).</summary>
    internal static Action<DbId, Vector3, Quaternion>? ChunkPlace;
    /// <summary>Put a chunk in <paramref name="scene"/>, or take it out with a null scene -- the engine's
    /// <c>AttachToScene</c> on a birth (<c>ChunkyParticle::initialize</c> @0x5abed0) and <c>AttachToScene(0)</c> on
    /// a death (<c>chkParticleLife</c> @0x5b1b50), which pools the gob rather than freeing it.</summary>
    internal static Action<DbId, DbId>? ChunkScene;
    /// <summary>Free a pooled chunk whose particle record is going away (the engine's <c>~ChunkyParticle</c>).</summary>
    internal static Action<DbId>? ChunkDestroy;
    /// <summary>Chunk spawns asked for with no spawner registered.</summary>
    internal static int ChunkSpawnsRefused { get; private set; }

    /// <summary>Spawn one chunk object of <paramref name="model"/> at <paramref name="world"/> in
    /// <paramref name="scene"/>; null when nothing can spawn it.</summary>
    internal static DbId SpawnChunk(DbId scene, string model, in Matrix4x4 world)
    {
        if (ChunkSpawn == null) { ChunkSpawnsRefused++; return default; }
        return ChunkSpawn(scene, model, world);
    }

    /// <summary>Is this part a BEAM (the mesh's <c>beaming</c> keyword)? The engine answered it by looking in
    /// draw bucket 15, which is a per-frame classification; the model says so once.</summary>
    internal static bool IsBeam(int p)
    {
        int r = SceneDb.PartRecipe[p];
        return r >= 0 && (DrawRecipe.Flags(r) & RecipeFlag.Beaming) != 0;
    }

    /// <summary>The lightmap bake's instance key. CX-120 changes what a bake is keyed ON: the engine path used
    /// <c>(vao, item)</c>, which is an address pair that an area change invalidates, where the content pair
    /// below survives it -- two instances of one mesh at one placement share a bake across areas and across
    /// sessions. The placement is rounded to the record's own move step so a matrix rebuilt from a quaternion
    /// does not key as a different placement.</summary>
    internal static ulong LightmapKey(int p)
    {
        int r = SceneDb.PartRecipe[p];
        MeshDef? mesh = r >= 0 ? DrawRecipe.Mesh(r) : null;
        if (mesh == null) return 0;
        ModelDef? m = SceneDb.PartModel[p];
        ulong h = m != null ? m.ContentHash : 0;
        h = Mix(h, (ulong)(uint)SceneDb.PartNode[p]);
        Matrix4x4 w = SceneDb.PartWorld[p];
        h = Mix(h, Quantise(w.M11)); h = Mix(h, Quantise(w.M12)); h = Mix(h, Quantise(w.M13));
        h = Mix(h, Quantise(w.M21)); h = Mix(h, Quantise(w.M22)); h = Mix(h, Quantise(w.M23));
        h = Mix(h, Quantise(w.M31)); h = Mix(h, Quantise(w.M32)); h = Mix(h, Quantise(w.M33));
        h = Mix(h, Quantise(w.M41)); h = Mix(h, Quantise(w.M42)); h = Mix(h, Quantise(w.M43));
        return h;
    }

    /// <summary>One tenth of a millimetre, which is the record's own <c>MoveStep</c>: a placement that does not
    /// move the record must not move the bake either.</summary>
    private static ulong Quantise(float v) => (ulong)(long)MathF.Round(v * 10000f);

    private static ulong Mix(ulong h, ulong v)
    {
        h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
        return h;
    }

    /// <summary>Forget everything (a session edge or a self-check).</summary>
    internal static void Reset()
    {
        for (int k = 0; k < _lightN; k++) _lightDef[k] = null;
        for (int k = 0; k < _emitN; k++) _emitDef[k] = null;
        _lightN = 0; _emitN = 0; Spilled = 0; TileLights = 0; ChunkSpawnsRefused = 0;
    }

    internal static string Stats()
        => $"dbinputs: lights {_lightN} (tile {TileLights}) emitters {_emitN} (nodef {_emitNoDef})"
         + (Spilled > 0 ? $" SPILLED {Spilled}" : "")
         + (ChunkSpawnsRefused > 0 ? $" CHUNK SPAWNS REFUSED {ChunkSpawnsRefused}" : "");
}

/// <summary>One light the renderer's light registry takes in DB mode: where it is, what it is, and whose.</summary>
internal struct DbLight
{
    internal int Part;
    internal DbId Obj;
    internal Vector3 Position, Colour;
    internal float Radius, Multiplier;
    internal LightDef? Def;
    internal bool Enabled;
    /// <summary>The <see cref="SceneDb.Lights"/> record behind this light, -1 for one that has only a part.
    /// The tile walk dedupes on it: a tile light whose node also became a part is in BOTH enumerations, and
    /// counting it twice is a torch at double brightness -- the hardest lighting bug there is to see.</summary>
    internal int Lit;
    /// <summary>Is this light part of the area's STATIC set -- the one the lightmap bakes? A tile light says
    /// so itself (<c>!LitDynamic</c>); an object light is dynamic until something says otherwise.</summary>
    internal bool Static;
    /// <summary>A stable identity for the bake and the flicker table: the Lit record for a tile light, the
    /// part index for an object light. Never dereferenced -- stored and compared -- and unlike the engine's
    /// <c>PartLight*</c> it is not recycled by an allocator.</summary>
    internal IntPtr Id;
}

/// <summary>One emitter the particle simulation takes in DB mode.</summary>
internal struct DbEmitter
{
    internal int Part;
    internal DbId Obj;
    internal Matrix4x4 World;
    internal EmitterDef? Def;
    internal bool Enabled;
}
