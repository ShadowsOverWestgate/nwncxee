using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>CX-120 seam-feed: <b>the stable half of a draw, interned per <c>(ModelDef, node)</c>.</b> Every
/// term the renderer's record used to re-read off an engine <c>Part</c>, <c>MdlNode</c>, <c>Material</c> or
/// <c>VertexPrimitive</c> is a property of the MODEL, not of the instance -- a thousand doors of one resref
/// share one recipe -- so a part needs a single <c>int</c> (<see cref="SceneDb.PartRecipe"/>) where the engine
/// path kept five pointers and re-chased them every frame.
/// <para>Interned, not cached: a recipe is immutable and lives as long as its <see cref="ModelDef"/>, so the
/// renderer may hold its index across frames and across threads with no identity test. That is what deletes
/// <c>IdentityOk</c> and the recycled-address class of bug with it.</para></summary>
internal static partial class DrawRecipe
{
    /// <summary>Distinct recipes held at once. A heavy SoW area measured ~1,300 distinct drawn nodes across
    /// its tileset and its creatures; a session that visits many areas accumulates, so this is sized for the
    /// session rather than the area and the models' own release is what retires entries.</summary>
    internal const int Max = 1 << 14;

    /// <summary>The PBR floats a recipe stages verbatim into its record, matching
    /// <c>AlloyRenderer.ParamFloats</c>: the two stock vec4s then ADR 0026's three extension vec4s.</summary>
    internal const int ParamFloats = 20;
    /// <summary>Texture slots a recipe names: diffuse, then the five map slots and the flow map.</summary>
    internal const int TexSlots = 8;

    private static readonly Dictionary<(ModelDef, int), int> _byNode = new();
    private static int _n;
    /// <summary>Recipes whose arena range was not resident at their last ask, as a drained list rather than a
    /// walk of the whole table (R1-7). A range that lands leaves the list; nothing re-polls what is resident.</summary>
    private static readonly int[] _rangeWait = new int[Max];
    private static int _rangeWaitN;
    private static readonly bool[] _rangeQueued = new bool[Max];

    // the interned columns, parallel arrays indexed by recipe
    private static readonly ModelDef?[] _model = new ModelDef?[Max];
    private static readonly int[] _node = new int[Max];
    private static readonly MeshDef?[] _mesh = new MeshDef?[Max];
    private static readonly byte[] _cls = new byte[Max];
    private static readonly RecipeFlag[] _flags = new RecipeFlag[Max];
    private static readonly byte[] _src = new byte[Max], _dst = new byte[Max], _prim = new byte[Max];
    private static readonly int[] _feat = new int[Max];
    private static readonly float[] _featF = new float[Max * 4];
    private static readonly float[] _param = new float[Max * ParamFloats];
    private static readonly int[] _slots = new int[Max];
    private static readonly string[] _tex = new string[Max * TexSlots];
    /// <summary>CX-117: the registry slot each texture slot names (<c>TexId.Value</c>; 0 = no texture).
    /// The bindless handle behind it is 64 bits, so the recipe keeps the HANDLE rather than an int id.</summary>
    private static readonly uint[] _texId = new uint[Max * TexSlots];
    /// <summary>1 while this texture slot's registry entry has not settled (arrived or failed).</summary>
    private static readonly byte[] _texWait = new byte[Max * TexSlots];
    /// <summary>How many of <see cref="_texWait"/> are still set. <see cref="Pump"/> skips its whole texture
    /// half while this is zero, so a settled area costs no re-ask at all (CX-117).</summary>
    private static int _texPending;
    private static readonly Vector4[] _amb = new Vector4[Max], _dif = new Vector4[Max];
    private static readonly float[] _cos = new float[Max], _sin = new float[Max];
    private static readonly Vector3[] _centre = new Vector3[Max];
    private static readonly float[] _radius = new float[Max];
    private static readonly float[] _box = new float[Max * 6];
    private static readonly int[] _range = new int[Max];
    /// <summary>The recipe's material DECLARES water (<see cref="DeclaredWater"/>) -- the model-side half of
    /// the engine's water test, kept per recipe because <see cref="Reclassify"/> re-asks the class of every
    /// recipe every frame and resolving a <c>.mtr</c> there would be a dictionary lookup per recipe per
    /// frame.</summary>
    private static readonly bool[] _declWater = new bool[Max];

    internal static int Count => _n;

    /// <summary>CX-120: how many recipes actually have a resolved diffuse, and how many are drawing the white
    /// stand-in. "Most surfaces are untextured" is a question about these two numbers and nothing else.</summary>
    internal static (int named, int resolved, int standIn) DiffuseCensus()
    {
        int named = 0, resolved = 0, standIn = 0;
        for (int r = 0; r < _n; r++)
        {
            if (_mesh[r] == null) continue;
            int o = r * TexSlots;
            if (string.IsNullOrEmpty(_tex[o])) continue;
            named++;
            if (TexRegistry.Handle(new TexId(_texId[o])) != 0) resolved++; else standIn++;
        }
        return (named, resolved, standIn);
    }
    /// <summary>Recipes refused for want of a table slot. Loud, counted, never a quiet hole.</summary>
    internal static int Spilled { get; private set; }
    /// <summary>Nodes not drawn because a later coincident twin covers them (<see cref="TwinShadowed"/>).</summary>
    internal static int TwinRefused { get; private set; }
    /// <summary>Recipes whose file-stated box did not hold their own vertices, and the first one's model:node.
    /// The cull sphere and the area index's box are this box, so an unfixed one culls a visible mesh.</summary>
    internal static int BoxGrown { get; private set; }
    internal static string BoxGrownFirst { get; private set; } = "";

    /// <summary>Siblings past this many are not compared: the mode-0 gate's same bound.</summary>
    private const int TwinMaxFanOut = 32;

    /// <summary>#397's shield in DB mode: stock <c>ashlw_072.mdl</c> carries two sibling tri-meshes with the same
    /// placement and vertices and different textures. Vanilla draws both in tree order under LEQUAL, so the LATER
    /// one's pixels are all that survive; Alloy's culled batches have no stable order, so the twins z-fought.
    /// Dropping the earlier twin gives vanilla's pixels. The door faces of #317 differ in their positions and both draw.
    /// ponytail: a later twin that is blended or hidden at runtime should let the earlier show; stock content has none.</summary>
    internal static bool TwinShadowed(ModelDef model, int node)
    {
        NodeDef nd = model.Nodes[node];
        MeshDef? m = nd.Mesh;
        if (m == null || nd.Skin != null || nd.Controllers.Length != 0 || nd.Parent < 0) return false;
        int[] kids = model.Nodes[nd.Parent].Children;
        if (kids.Length > TwinMaxFanOut) return false;
        bool later = false;
        foreach (int k in kids)
        {
            if (k == node) { later = true; continue; }
            if (!later || (uint)k >= (uint)model.Nodes.Length) continue;
            NodeDef t = model.Nodes[k];
            MeshDef? tm = t.Mesh;
            if (tm == null || !tm.Render || tm.Indices.Length == 0 || t.Skin != null || t.Controllers.Length != 0) continue;
            if (t.Position != nd.Position || t.Orientation != nd.Orientation || t.Scale != nd.Scale) continue;
            if (tm.Positions.AsSpan().SequenceEqual(m.Positions) && tm.Indices.AsSpan().SequenceEqual(m.Indices)) return true;
        }
        return false;
    }

    internal static MeshDef? Mesh(int r) => _mesh[r];
    internal static ModelDef? Model(int r) => _model[r];
    internal static int Node(int r) => _node[r];
    internal static int Cls(int r) => _cls[r];
    internal static RecipeFlag Flags(int r) => _flags[r];
    internal static int Src(int r) => _src[r];
    internal static int Dst(int r) => _dst[r];
    internal static int Prim(int r) => _prim[r];
    internal static int Feat(int r) => _feat[r];
    internal static int Slots(int r) => _slots[r];
    internal static Vector4 Ambient(int r) => _amb[r];
    internal static Vector4 Diffuse(int r) => _dif[r];
    internal static float Cos(int r) => _cos[r];
    internal static float Sin(int r) => _sin[r];
    internal static Vector3 Centre(int r) => _centre[r];
    internal static float Radius(int r) => _radius[r];
    /// <summary>The mesh-arena range this recipe draws from (CX-114), -1 when the arena could not hold it.</summary>
    internal static int Range(int r) => _range[r];
    internal static float Param(int r, int k) => _param[r * ParamFloats + k];

    /// <summary>ADR 0026's extension floats before any material is read -- the SAME seeding the engine path
    /// does in <c>AlloyRenderer.ResetExt</c>. <b>-1 is "not declared"</b> for every term a 0 is a legal value
    /// for (a dielectric, a mirror, an unlit emissive); the layer weights and the sheen colour are 0 = absent,
    /// which is also their default.
    /// <para>Without this the DB path wrote a DECLARED zero where mode 0 writes "undeclared" -- the frame
    /// diff's <c>ext=0,0,0,0</c> against mode 0's <c>ext=-1,-1,0,-1</c> -- which tells the shader to use a
    /// mirror-smooth dielectric instead of synthesising the term from the stock material.</para></summary>
    private static void ResetExt(int r)
    {
        int x = r * ParamFloats + AlloyRenderer.ParamExt;
        for (int k = 0; k < ParamFloats - AlloyRenderer.ParamExt; k++) _param[x + k] = 0f;
        _param[x] = _param[x + 1] = _param[x + 3] = -1f;   // CxMetallic, CxRoughness, CxClearcoatRoughness
        _param[x + 7] = _param[x + 8] = -1f;               // CxSheenRoughness, CxEmissiveStrength
    }
    internal static float FeatF(int r, int k) => _featF[r * 4 + k];
    /// <summary>The resident bindless handle for one texture slot, or 0 -- which the draw submits the
    /// white stand-in for (CX-117: an explicit, counted state, never a hole).</summary>
    internal static ulong TexHandle(int r, int s) => TexRegistry.Handle(new TexId(_texId[r * TexSlots + s]));
    /// <summary>The registry slot one texture slot names, so a record feed can register the part it is
    /// writing as a dependent (<c>TexRegistry.AddDependent</c>) and be TOLD when the texture lands
    /// instead of re-resolving it every frame.</summary>
    internal static TexId Tex(int r, int s) => new(_texId[r * TexSlots + s]);
    /// <summary>Is this slot's diffuse a PLT, i.e. does its record need the per-instance palette rows?</summary>
    internal static bool TexIsPlt(int r, int s) => TexRegistry.KindOf(new TexId(_texId[r * TexSlots + s])) == TexKind.Plt;
    internal static string TexName(int r, int s) => _tex[r * TexSlots + s] ?? "";

    /// <summary>The recipe for node <paramref name="node"/> of <paramref name="model"/>, building it on first
    /// ask; -1 when the node draws nothing or the table is full. <paramref name="plan"/> says whether the PART
    /// asking was built through the merge plan: one model is placed both ways (a static placeable takes the tile
    /// path, a usable one of the same model the plain tree), so the two get separate recipes.</summary>
    internal static int Intern(ModelDef model, int node, bool plan = true)
    {
        if ((uint)node >= (uint)model.Nodes.Length) return -1;
        if (_byNode.TryGetValue((model, plan ? ~node : node), out int r)) return r;
        NodeDef nd = model.Nodes[node];
        MeshDef? mesh = nd.Mesh;
        // CX-114: on a TILE model the geometry a node draws is the merge plan's, not its own -- a bucket head
        // draws the merged model-space mesh (which is why its part's pose is zeroed) and a node the bucket
        // swallowed draws nothing at all. Resolving `nd.Mesh` here drew every tile's UNMERGED pieces at the
        // head's zeroed pose AND drew the swallowed ones on top: the flat lavender mass.
        if (plan)
        {
            MeshDef? planned = PartTree.PlanMeshFor(model, node, out bool swallowed);
            if (swallowed) return -1;
            if (planned != null) mesh = planned;
        }
        if (mesh == null || !mesh.Render || mesh.Indices.Length == 0) return -1;
        if (TwinShadowed(model, node)) { TwinRefused++; return -1; }
        if (_n >= Max) { Spilled++; return -1; }
        r = _n++;
        _byNode[(model, plan ? ~node : node)] = r;
        _model[r] = model; _node[r] = node; _mesh[r] = mesh;
        Fill(r, nd, mesh);
        return r;
    }

    /// <summary>Re-ask the mesh arena and the texture registry for every recipe whose answer was deferred.
    /// Called once per frame from the feed, so a mesh or texture that lands late joins its draws on the next
    /// frame rather than on the next area change. Allocation-free.</summary>
    internal static int Pump()
    {
        int landed = 0;
        // R1-7: the ranges that were NOT resident, drained. The whole-table walk this replaces re-asked the
        // arena for every recipe every frame, inside the frame's own dirty fold.
        int w = 0;
        for (int k = 0; k < _rangeWaitN; k++)
        {
            int r = _rangeWait[k];
            if (_mesh[r] == null) { _rangeQueued[r] = false; continue; }
            if (_range[r] < 0) _range[r] = MeshArena.Acquire(_mesh[r]!);
            if (_range[r] >= 0) { _rangeQueued[r] = false; landed++; continue; }
            _rangeWait[w++] = r;
        }
        _rangeWaitN = w;
        if (_texPending == 0) return landed;
        // The texture half is the registry's own pending gate: a slot that is still Loading has told the
        // registry which parts depend on it (TexRegistry.AddDependent, from StageDbPart), and the arrival
        // marks those parts dirty -- so this only has to notice that the WAIT is over.
        for (int r = 0; r < _n && _texPending > 0; r++)
            for (int s = 0; s < TexSlots; s++)
            {
                int o = r * TexSlots + s;
                if (_texWait[o] == 0) continue;
                TexState st = TexRegistry.StateOf(new TexId(_texId[o]));
                if (st == TexState.Loading) continue;
                _texWait[o] = 0; _texPending--; landed++;
            }
        return landed;
    }

    /// <summary>This recipe's class from its content and its diffuse's measured coverage. Split out because
    /// it is re-asked: the first ask happens while the texture is still loading.
    /// <para>Water is read here and not taken from the caller because half of the term is the DIFFUSE's
    /// (<see cref="TexRegistry.BumpyShiny"/>), which is not known until the texture lands -- the same
    /// deferral the coverage below has, settled by the same <see cref="Reclassify"/>.</para></summary>
    private static AlloyClass ClassOf(int r, bool additive, bool declared)
    {
        var diffuse = new TexId(_texId[r * TexSlots]);
        TexCoverage cov = TexRegistry.Coverage(diffuse);
        bool opaque = cov == TexCoverage.Opaque;
        bool water = _declWater[r] || (PartTable.BumpyWaterOn && TexRegistry.BumpyShiny(diffuse));
        // MEASURED alpha decides opaque-or-not and NOTHING else. Blending is a DECLARED thing: the engine's
        // Material::GetIsOpaque @0x4c6190 reads the material's own `transparency`, an env map and a
        // non-default destination blend, and only then asks the diffuse whether it has alpha at all -- and
        // the answer to that last question lands a part in the alpha-TESTED bucket, which is also where the
        // engine path puts it (AlloyCensus passes the material's transparency keyword and the part's alpha,
        // never a texture measurement). Reading "this texture has fractional alpha" as "blend it" made every
        // equipped weapon and shield translucent: an NWN weapon's diffuse alpha is a REFLECTIVITY mask for
        // its environment map (w_metal_tex.txi: `envmaptexture TTR01__ref01`, 80% of its texels graded;
        // ashlw_031: a tenth of them at 164), and vanilla answers OPAQUE for an env-mapped material outright
        // -- GetIsOpaque returns the HasEnvMap flag itself and never looks at the texture.
        return AlloyClasses.Of(water, additive, opaque, declared, false);
    }

    /// <summary>Is this material DECLARED water, i.e. water by a term the MODEL carries? The <c>.mtr</c>'s
    /// <c>sample_framebuffer 2</c> is the engine's second route into draw bucket 6 (the first is the
    /// diffuse's <c>.txi</c>, read in <see cref="ClassOf"/>), and <c>AlloyCensus.IsWater</c> reads exactly
    /// these two on the engine path, so the modes classify water on one pair of terms.</summary>
    private static bool DeclaredWater(MeshDef mesh)
    {
        if (mesh.RenderHint.Equals("water", StringComparison.OrdinalIgnoreCase)) return true;
        MtrDef? mtr = MtrDef.Of(mesh);
        return mtr != null && mtr.SampleFramebuffer == AlloyCensus.SampleFbWater;
    }

    /// <summary>Re-ask the class -- and the latched bumpy-shiny half of
    /// <see cref="RecipeFlag.RotateTexture"/> -- of every recipe whose diffuse has landed since it was
    /// interned, and report how many MOVED. A move means the records of every part on that recipe have to be
    /// rewritten, which is what the texture's own arrival edge already marks them for.</summary>
    internal static int Reclassify()
    {
        // Every input is a landed texture's answer or the bumpy-water global; a frame where neither moved has
        // nothing to re-ask (this walked all recipes with string compares, twice a frame).
        int serial = System.Threading.Volatile.Read(ref TexRegistry.CoverageSerial);
        if (serial == _classSerial && PartTable.BumpyWaterOn == _classWater) return 0;
        _classSerial = serial; _classWater = PartTable.BumpyWaterOn;
        int moved = 0;
        for (int r = 0; r < _n; r++)
        {
            MeshDef? mesh = _mesh[r];
            if (mesh == null) continue;
            bool additive = mesh.RenderHint.Equals("additive", StringComparison.OrdinalIgnoreCase);
            byte want = (byte)ClassOf(r, additive, mesh.TransparencyHint > 0);
            RecipeFlag wasFlags = _flags[r];
            if (TexRegistry.BumpyShiny(new TexId(_texId[r * TexSlots]))) _flags[r] |= RecipeFlag.RotateTexture;
            if (want == _cls[r] && _flags[r] == wasFlags) continue;
            if (want != _cls[r]) { _cls[r] = want; Reclassified++; }
            moved++;
        }
        return moved;
    }

    private static int _classSerial = -1;
    private static bool _classWater;

    /// <summary>Recipes whose class moved once their diffuse's coverage was known.</summary>
    internal static int Reclassified { get; private set; }

    /// <summary>The water census: how many recipes are classified water, how many have a diffuse whose
    /// <c>.txi</c> names a bumpy-shiny map, and whether the engine's own gate on that route is live. "The
    /// water shader does not run" is a question about these three and nothing else.</summary>
    internal static string WaterNote()
    {
        int water = 0, bumpy = 0, declared = 0;
        for (int r = 0; r < _n; r++)
        {
            if (_mesh[r] == null) continue;
            if (_cls[r] == (byte)AlloyClass.Water) water++;
            if (TexRegistry.BumpyShiny(new TexId(_texId[r * TexSlots]))) bumpy++;
            if (_declWater[r]) declared++;
        }
        return $"water {water} recipe(s) ({bumpy} bumpy-shiny diffuse, {declared} declared, "
             + $"{AlloyRenderer.DbReclassSlots} slot(s) reclassified), "
             + $"g_nEnableBumpyWater {(PartTable.BumpyWaterOn ? "on" : "OFF")}";
    }

    private static void Fill(int r, NodeDef nd, MeshDef mesh)
    {
        _amb[r] = new Vector4(mesh.Ambient, 1f);
        _dif[r] = new Vector4(mesh.Diffuse, 1f);
        // #126's blend pair as the engine states it: an .mdl declares none, so the default pair is the
        // engine's own (SRC_ALPHA, ONE_MINUS_SRC_ALPHA) and a renderhint is what moves it.
        bool additive = nd.ContentFlags != 0 && mesh.RenderHint.Equals("additive", StringComparison.OrdinalIgnoreCase);
        _src[r] = BlendSrcAlpha; _dst[r] = additive ? BlendOne : BlendInvSrcAlpha;
        _declWater[r] = DeclaredWater(mesh);
        // TransparencyHint > 0 is the model's own "this is see-through" (the engine's child-order sort key);
        // a mesh with no alpha anywhere and no hint is genuinely opaque.
        bool declared = mesh.TransparencyHint > 0;
        // R1-8. The engine's own test is Material::GetIsOpaque @0x5c6190, and its terms are: the SharedMaterial's
        // `transparency` keyword, an env map, a non-default destination blend, and -- last -- whether the DIFFUSE
        // actually carries alpha coverage, which it asks the texture's own vtable. The first three are model
        // state and are read here. The fourth is a DECODE question (texture-coverage-is-a-decode-question) and
        // the texture is not resident when a recipe is interned, so it cannot be answered yet.
        // The fourth term is answerable now (CX-117): the decode decides a texture's coverage and the arrival
        // re-dirties its dependents, so a recipe interned before its diffuse landed is reclassified the frame
        // it does. Unknown keeps the conservative guess -- CUTOUT, which is safe rather than merely
        // convenient: a cutout is alpha-tested AND depth-writing, so a diffuse that turns out to be fully
        // opaque passes the 0.2 discard on every texel and draws identically. Guessing OPAQUE the other way
        // would punch the alpha out of every graded leaf and fringe in the game.
        _cls[r] = (byte)ClassOf(r, additive, declared);
        RecipeFlag f = RecipeFlag.None;
        // R1-9. Engine mode reads the Material's blend flag (mat+0x98): set means the engine sends flat white
        // and the part is fullbright. `!Lightmapped` was plainly wrong -- a lightmapped tile is still SHADED
        // in vanilla (lightmap x diffuse), so that unlit every tile in the game. The model-side equivalent of
        // the blend flag is the additive renderhint, which is what the materials carrying it declare.
        if (!additive) f |= RecipeFlag.Lit;
        // NOT from shininess. The engine's Env is Material::HasEnvMap (mat+0x40) -- a DECLARED environment
        // map -- and it means "this is shiny 2002 content, so read its diffuse alpha as a reflectivity mask".
        // Nearly every NWN mesh carries some shininess and almost none declares an env map, so inferring one
        // from the other marked the whole world env-mapped and sent every tile down the reflective branch
        // with no probe behind it. A MeshDef states no env map, so until the material path provides one the
        // honest answer is no.
        // ponytail: no env map in DB mode. The upgrade is the .mtr/material term, not a shininess guess.
        if (mesh.RotateTexture) f |= RecipeFlag.RotateTexture;
        if (nd.Kind == NodeKind.Dangly) f |= RecipeFlag.Dangly;
        if (nd.Kind == NodeKind.Skin) f |= RecipeFlag.Skinned;
        if (nd.Kind == NodeKind.AnimMesh) f |= RecipeFlag.AnimMesh;
        if (!mesh.Shadow) f |= RecipeFlag.NoShadow;
        if (mesh.Beaming) f |= RecipeFlag.Beaming;
        if (mesh.TileFade != 0) f |= RecipeFlag.Fades;
        _flags[r] = f;
        _prim[r] = 0;
        _feat[r] = 0;
        for (int k = 0; k < 4; k++) _featF[r * 4 + k] = 0f;
        // The two stock PBR vec4s: 0 means "not declared" and the shader synthesises it, exactly as the
        // engine path's unparsed .mtr left them. NOT the MDL's shininess: the engine never turns it into a
        // roughness, and declaring 1 - shininess/128 (0.8..0.99 on stock meshes) beat the env curve the
        // palette's reflectivity drives -- PLT armour read metal and fully rough, i.e. matte.
        for (int k = 0; k < ParamFloats; k++) _param[r * ParamFloats + k] = 0f;
        ResetExt(r);
        int mask = 0;
        // Material::GetTexture @0x5c5810: the .mtr's texture wins wherever it states one.
        MtrDef? mtr = MtrDef.Of(mesh);
        for (int s = 0; s < TexSlots; s++)
        {
            string name = mtr != null && mtr.Textures[s].Length > 0 ? mtr.Textures[s]
                        : s < mesh.Textures.Length ? mesh.Textures[s] : "";
            // "null" is the MDL's way of writing NO TEXTURE -- the same spelling the supermodel line uses,
            // and the one MdlPost already honours for the lightmap slot. Taken literally it becomes a resref
            // nothing can load, the slot fails, the record gets no handle, and the part draws BLACK: that is
            // the black door-sized quad in the ladder's area 2 (T_Door09, tex0='null', state=Failed, h0=NO).
            if (name.Equals("null", StringComparison.OrdinalIgnoreCase)) name = "";
            _tex[r * TexSlots + s] = name;
            TexId id = string.IsNullOrEmpty(name) ? default : TexRegistry.Acquire(name);
            _texId[r * TexSlots + s] = id.Value;
            bool wait = !id.IsNull && !TexRegistry.Resident(id);
            _texWait[r * TexSlots + s] = wait ? (byte)1 : (byte)0;
            if (wait) _texPending++;
            if (s > 0 && !string.IsNullOrEmpty(name)) mask |= 1 << (s - 1);
        }
        if ((f & RecipeFlag.Env) != 0) mask |= EnvSlotBit;
        _slots[r] = mask;
        // The engine's second route into the tile texture rotation, and it LATCHES: SetPartTextureRotate
        // @0x561670 writes `mdlnode+0x2b9 = 1` on a material with a bumpy-shiny map, i.e. it turns the mesh's
        // own `rotatetexture` on for good. Re-asked in Reclassify because the diffuse's .txi is not read
        // until the texture lands, which is after the recipe is interned.
        if (TexRegistry.BumpyShiny(new TexId(_texId[r * TexSlots]))) _flags[r] |= RecipeFlag.RotateTexture;
        _cos[r] = 1f; _sin[r] = 0f;
        // A compiled mesh's bmin/bmax is the compiler's word, and community compilers get it wrong; the draw
        // culls on this box, so it is grown over the vertices the arena actually draws.
        Vector3 lo = mesh.BMin, hi = mesh.BMax, flo = lo, fhi = hi;
        foreach (Vector3 v in mesh.Positions) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
        if (lo != flo || hi != fhi)
        {
            if (BoxGrown++ == 0) BoxGrownFirst = $"{_model[r]?.Name}:{nd.Name} {flo}..{fhi} -> {lo}..{hi}";
        }
        _centre[r] = (lo + hi) * 0.5f;
        Vector3 ex = hi - lo;
        _radius[r] = 0.5f * ex.Length();
        _box[r * 6 + 0] = lo.X; _box[r * 6 + 1] = lo.Y; _box[r * 6 + 2] = lo.Z;
        _box[r * 6 + 3] = hi.X; _box[r * 6 + 4] = hi.Y; _box[r * 6 + 5] = hi.Z;
        _range[r] = MeshArena.Acquire(mesh);
        if (_range[r] < 0 && !_rangeQueued[r] && _rangeWaitN < _rangeWait.Length)
        { _rangeWait[_rangeWaitN++] = r; _rangeQueued[r] = true; }
    }

    /// <summary>R1-15 + tex-3: retire every recipe of <paramref name="model"/>. Subscribed to
    /// <c>ModelCache.Evicting</c>, which is the ONE edge that says a model is going: without it a recipe held
    /// a strong reference to the <see cref="ModelDef"/> and a reference to each of its texture slots, so the
    /// model cache could never actually free a model and <c>texcache_mb</c> could never evict a texture.</summary>
    internal static void Forget(ModelDef model)
    {
        for (int r = 0; r < _n; r++)
        {
            if (!ReferenceEquals(_model[r], model)) continue;
            if (_range[r] >= 0) MeshArena.Release(_range[r]);
            _range[r] = -1;
            for (int s = 0; s < TexSlots; s++)
            {
                int o = r * TexSlots + s;
                TexRegistry.Release(new TexId(_texId[o]));
                _texId[o] = 0; _tex[o] = "";
                if (_texWait[o] != 0) { _texWait[o] = 0; _texPending--; }
            }
            if (_byNode.TryGetValue((model, _node[r]), out int a) && a == r) _byNode.Remove((model, _node[r]));
            if (_byNode.TryGetValue((model, ~_node[r]), out int b) && b == r) _byNode.Remove((model, ~_node[r]));
            _model[r] = null; _mesh[r] = null;
            _rangeQueued[r] = false;
            Retired++;
        }
    }

    /// <summary>Recipes retired with their model.</summary>
    internal static int Retired { get; private set; }

    /// <summary>Subscribe to the model cache's eviction edge. Multicast (<c>+=</c>), because the cache's other
    /// listeners are not ours to replace.</summary>
    internal static void Attach()
    {
        if (_attached) return;
        _attached = true;
        ModelCache.Evicting += Forget;
    }

    private static bool _attached;

    /// <summary>The recipe's local box, six floats (min then max), into <paramref name="dst"/>.</summary>
    internal static void Box(int r, Span<float> dst)
    {
        for (int k = 0; k < 6; k++) dst[k] = _box[r * 6 + k];
    }

    /// <summary>Bit 5 of the slot mask: the material declares an env map, with no texture behind it (the
    /// per-area probe replaces what it held). Mirrors <c>AlloyRenderer.EnvSlot</c>.</summary>
    /// <summary>The env-map bit of the slot mask. It is <see cref="AlloyRenderer.EnvSlot"/> and nothing else:
    /// that value is FROZEN at 64 because shipped <c>cx_ownlit_fs.shd</c> overrides carry a copy of it, so a
    /// second definition here is a fact that can drift from content on disk.
    /// <para>It drifted immediately. This read <c>1 &lt;&lt; 5</c> = 32, which in that mask means "map slot 6
    /// is declared" -- the FLOW MAP. So every DB part whose mesh had any shininess told the lit stage to
    /// resolve a flow map it does not have, and the stage distorted its UVs by an unbound RG texture. That is
    /// what rendered the tiles of the first lit DB area black while the objects beside them came out fine:
    /// the objects' meshes happened to declare no shininess.</para></summary>
    internal const int EnvSlotBit = AlloyRenderer.EnvSlot;
    /// <summary><c>Aurora::AuroraBlendModes</c> values, which <see cref="Issue.BlendGl"/> translates to
    /// <c>glBlendFunc</c> factors. These are INDICES into the engine's own <c>blendTranslationTable</c>
    /// @0x12f5040, which <c>GLRender::SetBlendFunc</c> @0x5cb0c0 indexes with both arguments; read out of the
    /// binary's .rodata the table is
    /// <c>SRC_ALPHA, ONE_MINUS_SRC_ALPHA, ONE, ZERO, DST_COLOR, SRC_COLOR, ONE_MINUS_DST_ALPHA</c>, and
    /// <c>ResetRenderStates</c> @0x5d1810 restores the default pair as <c>SetBlendFunc(0, 1)</c>.
    /// <para>These three read 4/5/1 until CX-120. That is not a different convention, it is three wrong
    /// factors: a DB blended part blended <c>DST_COLOR x SRC_COLOR</c> and an "additive" one blended normally.
    /// The engine path never had the bug -- its values come off the material through the census -- so it was
    /// invisible in a mode-0 comparison.</para></summary>
    internal const byte BlendSrcAlpha = 0, BlendInvSrcAlpha = 1, BlendOne = 2;

    /// <summary>Forget every recipe (a session edge: the model cache dropped everything). The arena ranges go
    /// back through <see cref="MeshArena.Release"/> so nothing leaks a range it no longer names.</summary>
    internal static void Reset()
    {
        for (int r = 0; r < _n; r++)
        {
            if (_range[r] >= 0) MeshArena.Release(_range[r]);
            _model[r] = null; _mesh[r] = null;
            for (int s = 0; s < TexSlots; s++) _tex[r * TexSlots + s] = "";
        }
        _byNode.Clear();
        _n = 0; Spilled = 0; TwinRefused = 0; Retired = 0; _rangeWaitN = 0; _texPending = 0;
        Array.Clear(_rangeQueued);
        Array.Clear(_texWait);
    }
}

/// <summary>What a <see cref="DrawRecipe"/> says about its node, in place of the engine vtable probes and
/// material-flag reads the draw used to make per frame.</summary>
[Flags]
internal enum RecipeFlag : ushort
{
    None = 0,
    /// <summary>The engine would draw this part LIT (it is not lightmapped into its own vertices).</summary>
    Lit = 1 << 0,
    /// <summary>The material declares an env map: the "this is shiny" bit the stock shaders read.</summary>
    Env = 1 << 1,
    /// <summary>The material's <c>twosided</c>: draws with face culling off.</summary>
    TwoSided = 1 << 2,
    /// <summary>A graded-alpha cutout re-filed as blended (<c>gradedblend</c>), drawn with no depth write.</summary>
    Wash = 1 << 3,
    /// <summary>A dangly mesh: a permanent shadow mover, posed by the solver.</summary>
    Dangly = 1 << 4,
    /// <summary>A skinmesh: posed by a bone palette.</summary>
    Skinned = 1 << 5,
    /// <summary>An animmesh: its vertices are morphed per frame.</summary>
    AnimMesh = 1 << 6,
    /// <summary>The mesh asked not to cast.</summary>
    NoShadow = 1 << 7,
    /// <summary>The mesh's <c>beaming</c> keyword.</summary>
    Beaming = 1 << 8,
    /// <summary>The mesh takes part in tile fade (its <c>tilefade</c> is non-zero).</summary>
    Fades = 1 << 9,
    /// <summary>The mesh's texture rotates (<c>rotatetexture</c>).</summary>
    RotateTexture = 1 << 10,
}
