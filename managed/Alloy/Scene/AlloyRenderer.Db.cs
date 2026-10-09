using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-120 seam-feed: <b>the ownership table, fed from the scene database.</b> In DB mode this file is
/// what <c>EnrollOne</c> is in engine mode -- the ONE writer of a slot's ownership columns -- except that its
/// input is a <c>(DbId part, int recipe)</c> pair rather than the five engine pointers
/// <c>(Tri, Node, Mat, Vdo, Item)</c>, and it runs on a dirty bit rather than on a table scan.
/// <para><b>How a DB slot is told apart.</b> <see cref="_oDbPart"/> carries the part index, -1 for an engine
/// slot, and <c>_oTri</c> carries a NEGATIVE sentinel so that every existing "slot vacated" test
/// (<c>_oTri[i] == Zero</c>) keeps reading correctly while any dereference that forgot its DB branch faults
/// immediately on a non-canonical address. That is deliberate: a missed branch must be a crash with a name on
/// it, not a quiet misrender off whatever the heap happened to hold (no-silent-fallbacks).</para></summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The scene-database part this slot draws, -1 for a slot fed from the engine's scene graph.
    /// Filled with -1 rather than left zeroed: part 0 is a real index, so a zero default would read every
    /// engine slot in the table as the database's.</summary>
    internal static readonly int[] _oDbPart = Minus1(new int[MaxOwned]);
    /// <summary>The slot's interned <see cref="DrawRecipe"/> -- the stable half the engine path re-read off
    /// <c>Part</c>/<c>MdlNode</c>/<c>Material</c>/<c>VertexPrimitive</c> every frame.</summary>
    internal static readonly int[] _oDbRecipe = Minus1(new int[MaxOwned]);

    private static int[] Minus1(int[] a) { Array.Fill(a, -1); return a; }

    /// <summary>Is slot <paramref name="i"/> the database's?</summary>
    internal static bool DbSlot(int i) => (uint)i < (uint)_oDbPart.Length && _oDbPart[i] >= 0;

    /// <summary>May the lightmap bake take this DB slot? Lit geometry of a Static object (a tile or a Static
    /// placeable) and nothing else -- mode 0 charts only the engine's static pools, and a baked mover keeps
    /// the light of wherever it stood when it enrolled.</summary>
    internal static bool DbBakeable(int i)
    {
        if (!DbSlot(i) || !_oLit[i]) return false;
        DbId o = SceneDb.PartObj[_oDbPart[i]];
        return SceneDb.Objects.Valid(o) && (SceneDb.ObjFlags[o.Index] & (ObjFlag.Static | ObjFlag.Sky)) == ObjFlag.Static;
    }

    /// <summary>The sentinel a DB slot's <c>_oTri</c> carries: non-zero (so "vacated" still means vacated) and
    /// non-canonical (so an unbranched dereference faults rather than reads).</summary>
    private static IntPtr DbTri(int part) => (IntPtr)(-2L - part);

    /// <summary>Seat database part <paramref name="part"/> in an ownership slot and write its stable columns.
    /// Returns the slot, or -1 when the table is full (counted, logged once by the feed -- never a quiet drop).
    /// Idempotent: a part that already holds a slot keeps it, which is what makes the change-driven build's
    /// "update" path a column rewrite rather than a reseat.</summary>
    internal static int DbSeat(int part, int recipe)
    {
        int held = SceneDb.PartSlot[part];
        // R1-6: a part that already holds its slot with the same recipe has NOTHING stable to rewrite -- its
        // move is a per-frame field, which StageDbPart compares into place. Rewriting the columns (and
        // unstamping the record with them) on every move forced a full stable rebuild and a whole-record
        // resend per moving part per frame, which is exactly the delta scheme #242 exists to avoid.
        if (held >= 0 && _oDbPart[held] == part)
        {
            if (_oDbRecipe[held] != recipe) DbWriteColumns(held, part, recipe);
            return held;
        }
        if (_ownLive >= MaxOwned) { _capacitySpill++; return -1; }
        // R1-3: DB mode has no enrolment, so nothing ever advances _freeAvail -- the engine path's floor is
        // raised by EnrollBegin, and none of those run here. Without this the free list is never drawn from,
        // _ownCount climbs past MaxOwned on the first busy area, and the table walks off its own arrays.
        // A DB slot is freed by one told edge and may be reused immediately: there is no cross-frame walk
        // whose floor it could violate.
        if (_freeAvail < _freeCount) _freeAvail = _freeCount;
        int i = AllocSlot();
        if (i >= MaxOwned) { _ownCount = MaxOwned; _capacitySpill++; return -1; }
        _ownLive++;
        _oBone[i] = -1; _oDpAt[i] = -1;
        UnstampRecord(i);
        SetCastOk(i, false);
        // CX-120: the tenancy stamp is minted HERE, for THIS slot, and is unique to this seating -- the one
        // thing every reader of it means by it ("the slot changed hands"). It used to be the generation
        // current at the seat, with DbOrder re-stamping every live slot afterwards; see DbOrder.
        _gate[i].EnrollGen = _oClaim[i] = ++_enrollGen;
        DbWriteColumns(i, part, recipe);
        SceneDb.PartSlot[part] = i;
        _dbSeated++;
        DbSetChanged();
        return i;
    }

    /// <summary>Release the slot database part <paramref name="part"/> held, if any. The one destruction edge:
    /// the game destroyed the object (or its part range changed), so there is nothing to infer.</summary>
    internal static void DbRelease(int part)
    {
        int i = SceneDb.PartSlot[part];
        if (i < 0) return;
        SceneDb.PartSlot[part] = -1;
        if ((uint)i >= (uint)_oDbPart.Length || _oDbPart[i] != part) return;
        _oDbPart[i] = -1; _oDbRecipe[i] = -1;
        FreeSlot(i);
        _dbReleased++;
        DbSetChanged();
    }

    /// <summary>The DB-mode counterpart of <c>EnrollOne</c>'s column block: everything about a draw that does
    /// not change while this part holds this slot.</summary>
    private static void DbWriteColumns(int i, int part, int recipe)
    {
        DbId obj = SceneDb.PartObj[part];
        int o = obj.Index;
        RecipeFlag rf = DrawRecipe.Flags(recipe);
        ObjFlag of = (uint)o < SceneDb.MaxObjects ? SceneDb.ObjFlags[o] : ObjFlag.None;
        _oDbPart[i] = part; _oDbRecipe[i] = recipe;
        _oEntry[i] = -1;
        _oTri[i] = DbTri(part);
        _oNode[i] = IntPtr.Zero; _oMat[i] = IntPtr.Zero; _oVdo[i] = IntPtr.Zero;
        _oItem[i] = -1; _oPrim[i] = (int)DrawRecipe.Prim(recipe);
        // Every DB draw comes out of the one mesh arena, so there is no per-VDO VAO to name and no harvest
        // to wait for: the slot's VAO term is the arena's bit and the range is the part's.
        _oVao[i] = 0; _oLodBit[i] = DbArenaVaoBit;
        _oRotBits[i] = 0;
        // R1-18: the batch sort's texture digit. The recipe's diffuse TexId folded into the key space, so
        // parts sharing a texture sort contiguous exactly as the engine path's interned CAurTexture* did.
        uint tex0 = DrawRecipe.Tex(recipe, 0).Value;
        _oTexKey[i] = tex0 == 0 ? 0 : (int)((tex0 * 2654435761u >> 8) % (MaxMats - 1)) + 1;
        DbWriteTexRot(i, o, recipe, rf);
        // Every DB part is matrix-class: its world matrix is composed, never baked into its vertices. The
        // arena holds MODEL-space geometry (that is what makes one range serve every instance), which is
        // exactly the case `_oMx` names.
        _oMx[i] = true;
        _oLit[i] = (rf & RecipeFlag.Lit) != 0 && _lighting;
        if ((rf & RecipeFlag.Lit) != 0 && (of & (ObjFlag.Static | ObjFlag.Sky)) == ObjFlag.Static)
            MeshArena.WantBake(DrawRecipe.Range(recipe));
        _oCls[i] = (byte)DrawRecipe.Cls(recipe);
        _oSrc[i] = (byte)DrawRecipe.Src(recipe); _oDst[i] = (byte)DrawRecipe.Dst(recipe);
        _oWash[i] = (rf & RecipeFlag.Wash) != 0 ? (byte)1 : (byte)0;
        _oNoCull[i] = (rf & RecipeFlag.TwoSided) != 0;
        _oBucket[i] = 0;
        // No gob pointer exists in DB mode; the questions it answered are columns.
        _oGob[i] = IntPtr.Zero;
        _oTileGob[i] = (of & ObjFlag.Tile) != 0;
        _oLodRoot[i] = IntPtr.Zero;
        _oDangly[i] = (rf & RecipeFlag.Dangly) != 0;
        _oFadeTile[i] = IntPtr.Zero; _oTile[i] = IntPtr.Zero;
        Vector3 c = DrawRecipe.Centre(recipe);
        _oCx[i] = c.X; _oCy[i] = c.Y; _oCz[i] = c.Z;
        _oR[i] = DrawRecipe.Radius(recipe);
        // The WORLD box the area index files on, written here as well as at every stage: the index is built
        // from a slot-set edge and the stage that would fill it can be deferred (no geometry resident yet), so
        // a slot filed between the two would be indexed with a zero box -- i.e. unbounded, i.e. routed every
        // pass for the life of the area. Correct either way, needlessly expensive one way.
        DbWriteWorldBox(i, recipe, SceneDb.PartWorld[part]);
        _oBx[i] = c.X; _oBy[i] = c.Y; _oBz[i] = c.Z; _oBr[i] = _oR[i];
        int po = i * ParamFloats, fo = i * 4;
        for (int k = 0; k < ParamFloats; k++) _oParam[po + k] = DrawRecipe.Param(recipe, k);
        // CX-117: Gob::SetMaterialParam's per-OBJECT overrides, over the recipe's shared floats. Gated on the
        // table being non-empty, so a scene that never calls it pays one bool.
        if (Seam.MaterialParams.Any) Seam.MaterialParams.Apply(SceneDb.PartObj[part], _oParam.AsSpan(po, ParamFloats));
        for (int k = 0; k < 4; k++) _oFeat[fo + k] = DrawRecipe.FeatF(recipe, k);
        _oSlots[i] = DrawRecipe.Slots(recipe);
        UnstampRecord(i);
    }

    /// <summary>The slot's tile TEXTURE rotation, as the record's <c>rot.xy</c>.
    /// <para><c>SpawnPartsForTile</c> @0x563450 ends in <c>SetPartTextureRotate(tree, orient)</c> @0x561670,
    /// which stores <c>orient * 90</c> degrees on every mesh of the tree whose <c>rotatetexture</c> is set or
    /// whose material has a bumpy-shiny map -- both <see cref="RecipeFlag.RotateTexture"/> here, since
    /// <see cref="DrawRecipe"/> latches the second exactly as the engine's own
    /// <c>mdlnode+0x2b9 = 1</c> write does. The angle is per PLACEMENT and one recipe serves all four
    /// orientations of a tile model, so it comes off <see cref="SceneDb.ObjTexRotate"/> and not off the
    /// recipe, whose own pair stays the fallback (a texture-animation controller's, when there is one).</para>
    /// <para>Identity here was the whole bug: every DB recipe reported (1, 0), so NWN's tile anti-repetition
    /// scheme did not happen and a quarter of every tileset's floors and walls drew at the wrong angle.</para></summary>
    private static void DbWriteTexRot(int i, int o, int recipe, RecipeFlag rf)
    {
        int qt = (rf & RecipeFlag.RotateTexture) != 0 && (uint)o < SceneDb.MaxObjects
               ? SceneDb.ObjTexRotate[o] & 3 : 0;
        if (qt == 0) { _oCos[i] = DrawRecipe.Cos(recipe); _oSin[i] = DrawRecipe.Sin(recipe); return; }
        _oCos[i] = TexRotCos[qt]; _oSin[i] = TexRotSin[qt];
    }

    /// <summary>(cos, sin) of 0/90/180/270 degrees, exact rather than <c>MathF</c>'s (which leaves 1e-7 in
    /// the zero terms and skews every rotated tile's UVs by a texel at grazing angles).
    /// <para>Sign convention, verified rather than guessed: <c>aurMatrixStack::Rotate((0,0,1), angle)</c>
    /// @0x5dafe0 lays its matrix out so that READ AS GL COLUMN-MAJOR its first two columns are
    /// <c>(c, s)</c> and <c>(-s, c)</c>, and the stock vertex stage does
    /// <c>vVertexTexCoords = mat2(m_texture) * vTcIn.xy</c> -- which is the
    /// <c>(c*u - s*v, s*u + c*v)</c> that <c>cx_owned_v.glsl</c> computes from <c>rot.xy</c>. So the pair is
    /// the engine's own <c>(cos, sin)</c> of <c>orient * 90</c> degrees, unnegated and unswapped.</para></summary>
    private static readonly float[] TexRotCos = [1f, 0f, -1f, 0f];
    /// <inheritdoc cref="TexRotCos"/>
    private static readonly float[] TexRotSin = [0f, 1f, 0f, -1f];

    /// <summary>The <c>(cos, sin)</c> <paramref name="quarterTurns"/> puts in a record, for the self-check
    /// that ties the table to the engine's <c>orient * 90</c> degrees.</summary>
    internal static (float Cos, float Sin) DbTexRot(int quarterTurns)
        => (TexRotCos[quarterTurns & 3], TexRotSin[quarterTurns & 3]);

    /// <summary><c>_oLodBit</c>'s DB value: this slot draws out of the mesh arena. Above
    /// <see cref="ChartVaoBit"/> so it cannot collide with either engine-mode arena bit.</summary>
    internal const int DbArenaVaoBit = 1 << 22;

    /// <summary>The arena compacted and these ranges moved. Every reader resolves a DB slot's range live off
    /// the arena (the colour stage per frame, the bake through <see cref="DbCasterRange"/>), so nothing is
    /// rewritten here; what remains is the cascades, which hold geometry no mover event describes.</summary>
    internal static void DbRangesMoved(int[] moved, int count)
    {
        if (count <= 0) return;
        int fixedUp = 0;
        for (int i = 0; i < _ownCount; i++)
        {
            int part = _oDbPart[i];
            if (part < 0) continue;
            int range = DrawRecipe.Range(_oDbRecipe[i]);
            if (range < 0) continue;
            bool hit = false;
            for (int k = 0; k < count; k++) if (moved[k] == range) { hit = true; break; }
            if (!hit) continue;
            fixedUp++;
        }
        _dbRangeRefresh += fixedUp;
        // A moved range is new geometry under the published cascades, and no mover event describes it.
        if (fixedUp > 0) CasterPass.ShadowInvalidate();
    }

    /// <summary>A recipe's CLASS moved once its diffuse landed (<see cref="DrawRecipe.Reclassify"/>), so
    /// every slot drawing it has to be told: the class columns are the stable half, written once at the seat,
    /// and the route reads them every pass. Without this a part interned before its texture arrived kept the
    /// conservative cutout guess for ever -- which is what left water, drawn by its own pass on its own
    /// class, in the cutout run with the flat legacy texture on it.
    /// <para>A whole-table walk because classes move at texture-arrival rate, not per frame; only a slot
    /// whose column disagrees with its recipe is rewritten.</para></summary>
    internal static void DbReclassified()
    {
        int moved = 0;
        for (int i = 0; i < _ownCount; i++)
        {
            int recipe = _oDbRecipe[i];
            if (_oDbPart[i] < 0 || recipe < 0) continue;
            byte cls = (byte)DrawRecipe.Cls(recipe);
            // ...and the texture rotation with it: the bumpy-shiny half of RecipeFlag.RotateTexture latches
            // when the diffuse's .txi lands, which is after the slot took its columns.
            float wasCos = _oCos[i], wasSin = _oSin[i];
            DbWriteTexRot(i, SceneDb.PartObj[_oDbPart[i]].Index, recipe, DrawRecipe.Flags(recipe));
            // ...and the .txi's blend pair and fullbright flag, which an offer that waited reads late.
            bool lit = (DrawRecipe.Flags(recipe) & RecipeFlag.Lit) != 0 && _lighting;
            if (_oCls[i] == cls && _oCos[i] == wasCos && _oSin[i] == wasSin
                && _oDst[i] == DrawRecipe.Dst(recipe) && _oLit[i] == lit) continue;
            _oCls[i] = cls; _oLit[i] = lit;
            _oSrc[i] = (byte)DrawRecipe.Src(recipe); _oDst[i] = (byte)DrawRecipe.Dst(recipe);
            // The slot moves between passes, so its staged record and batch row are both stale.
            UnstampRecord(i);
            moved++;
        }
        _dbReclassSlots += moved;
        if (moved > 0) DbSetGen++;   // the route's own change gate: the pass order is not the same set any more
    }

    private static int _dbReclassSlots;
    /// <summary>Slots whose class column followed their recipe's reclassification.</summary>
    internal static int DbReclassSlots => _dbReclassSlots;

    private static int _pcSaid;
    private static long _pcAt;
    private static uint _pcScene, _pcWas;

    /// <summary>PC dumps a session: the boot one plus a few area edges.</summary>
    private const int MaxPcDumps = 6;
    private static int _blackProbed;

    /// <summary>Parts the black probe names per area. Enough for a pattern, few enough to read.</summary>
    private const int BlackProbes = 24;

    /// <summary>A new area is a new budget. <see cref="DbBlackProbe"/>.</summary>
    internal static void DbBlackProbeAreaEdge() => _blackProbed = 0;

    /// <summary>What draws BLACK. A part whose diffuse never resolved samples the white stand-in and a part
    /// whose material terms are zero shades to nothing, and the two look identical on screen -- so this names
    /// the model, the node, the texture it asked for, what the registry did with it, and the terms, for the
    /// first few NON-TILE parts whose diffuse is missing or whose colour terms are black. One-shot per
    /// AREA: the first area would otherwise spend the whole budget and the probe could say nothing about
    /// the area the question is in.</summary>
    internal static void DbBlackProbe(int part, int recipe, ulong h0)
    {
        if (_blackProbed >= BlackProbes || _dbPasses < 300) return;
        DbId po = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(po)) return;
        ObjFlag of = SceneDb.ObjFlags[po.Index];
        if ((of & ObjFlag.Tile) != 0) return;
        TexId id = DbTexOf(part, recipe, 0);
        Vector4 dif = DrawRecipe.Diffuse(recipe);
        Vector4 tint = SceneDb.ObjTint[po.Index];
        bool noTex = h0 == 0 || id.IsNull;
        bool blackTerms = dif.X + dif.Y + dif.Z <= 0.001f;
        if (!noTex && !blackTerms) return;
        _blackProbed++;
        CX.Alloy.Mdl.MeshDef? m = DrawRecipe.Mesh(recipe);
        _log?.Always($"AlloyRenderer: [db black] part={part} obj={po.Value} model='{SceneDb.ObjSeamModelName[po.Index]}' "
                   + $"node='{PartTree.NameOf(part)}' flags={of} kind={SceneDb.PartKindOf[part]} "
                   + $"| tex0='{(m != null ? m.Textures[0] : "?")}' id={id.Value} resref='{TexRegistry.ResrefOf(id)}' "
                   + $"state={TexRegistry.StateOf(id)} h0={(h0 != 0 ? "yes" : "NO")} cov={TexRegistry.Coverage(id)} "
                   + $"cls={(AlloyClass)DrawRecipe.Cls(recipe)} | diffuse=({dif.X:F2},{dif.Y:F2},{dif.Z:F2}) "
                   + $"tint=({tint.X:F2},{tint.Y:F2},{tint.Z:F2},{tint.W:F2}) alpha={SceneDb.PartAlpha[part]:F2} "
                   + $"selfillum=({SceneDb.PartSelfIllum[part].X:F2},{SceneDb.PartSelfIllum[part].Y:F2},"
                   + $"{SceneDb.PartSelfIllum[part].Z:F2}) "
                   + $"| world.t=({SceneDb.PartWorld[part].Translation.X:F1},{SceneDb.PartWorld[part].Translation.Y:F1},{SceneDb.PartWorld[part].Translation.Z:F1})");
    }

    /// <summary>Why the player character does not draw. The camera FOLLOWS the PC, so its object is named
    /// without guessing: dump the object, then every part of it and of everything attached to it, with the
    /// term that would refuse each one. One-shot, once the camera has settled.</summary>
    internal static void DbPcProbe(DbId camera)
    {
        // Once the camera has settled, and again on every AREA EDGE: the body a creature carries across one is
        // the recurring DB question (a column kept from the slot's previous tenant, a texture the engine freed
        // under us), and a dump taken only at boot can never answer it. Capped, and keyed on the PC's scene
        // rather than a frame count, which is the edge itself.
        if (_pcSaid >= MaxPcDumps) return;
        DbId pc = SceneDb.Cameras.Valid(camera) ? SceneDb.CamFollow[camera.Index] : default;
        if (!SceneDb.Objects.Valid(pc)) return;
        DbId sc = SceneDb.ObjScene[pc.Index];
        if (_pcSaid == 0) { if (_dbPasses < 600) return; }
        else if (sc.Value == _pcScene && pc.Value == _pcWas) return;
        else if (_dbPasses < _pcAt + 120) return;   // let the new area's first frames settle
        _pcScene = sc.Value; _pcWas = pc.Value; _pcAt = _dbPasses;
        _pcSaid++;
        Pc(pc, "follow", 0);
        PcBelow(pc, 1);
    }

    /// <summary>Everything hanging off <paramref name="obj"/>, both ways down: the pieces
    /// <c>Gob::Append</c> grafted into its tree, and the separate gobs the game ATTACHED to one of its nodes
    /// -- the cloak, the tail, the wings, the robe, a wielded item and, below that item, the part models a
    /// composite weapon is built from (<c>CNWCAnimBaseItemComp::LoadModel</c> @0xad7780 attaches each to the
    /// item's own root). One-shot, so the table scan per level costs nothing per frame.</summary>
    private static void PcBelow(DbId obj, int depth)
    {
        if (depth > 3) return;
        for (DbId c = SceneDb.ObjSeamAppendHead[obj.Index]; !c.IsNull && SceneDb.Objects.Valid(c);
             c = SceneDb.ObjSeamAppendNext[c.Index]) Pc(c, "append", depth);
        for (int o = 0; o < SceneDb.MaxObjects; o++)
            if (SceneDb.Objects.LiveAt(o) && SceneDb.ObjAttachTo[o].Value == obj.Value
                && (SceneDb.ObjSeamFlags[o] & (uint)SeamFlag.Composed) == 0)
            {
                DbId c = SceneDb.Objects.IdAt(o);
                Pc(c, $"attached at \"{SceneDb.ObjAttachNode[o] ?? "(root)"}\"", depth);
                PcBelow(c, depth + 1);
            }
    }

    private static void Pc(DbId obj, string why, int depth)
    {
        int o = obj.Index;
        int first = SceneDb.ObjPartFirst[o], n = SceneDb.ObjPartCount[o];
        int withMesh = 0, withRecipe = 0, seated = 0, withRange = 0, drawn = 0, skins = 0, skinned = 0;
        string firstBad = "", tex = "";
        for (int k = 0; k < n; k++)
        {
            int p = first + k;
            CX.Alloy.Mdl.ModelDef? pm = SceneDb.PartModel[p];
            int pn = SceneDb.PartNode[p];
            if (pm != null && (uint)pn < (uint)pm.Nodes.Length && pm.Nodes[pn].Skin != null)
            { skins++; if (SceneDb.PartBoneBase[p] >= 0) skinned++; }
            if (SceneDb.PartMeshDef[p] == null) continue;
            withMesh++;
            int rec = SceneDb.PartRecipe[p];
            if (rec < 0) { if (firstBad.Length == 0) firstBad = $"part {k} '{PartTree.NameOf(p)}' has NO RECIPE"; continue; }
            withRecipe++;
            if (tex.Length == 0)
            {
                TexId d = DrawRecipe.Tex(rec, 0);
                tex = $" tex0={(d.IsNull ? "(none)" : TexRegistry.ResrefOf(d))}"
                    + $" {TexRegistry.KindOf(d)}/{TexRegistry.StateOf(d)}"
                    + $" cls={(AlloyClass)DrawRecipe.Cls(rec)} reveal={SceneDb.PartReveal[p]:F2}"
                    + $" alpha={SceneDb.PartAlpha[p]:F2} fade={SceneDb.PartFade[p]:F2}";
            }
            int rng = DrawRecipe.Range(rec);
            if (rng < 0) { if (firstBad.Length == 0) firstBad = $"part {k} '{PartTree.NameOf(p)}' recipe {rec} has NO RANGE"; continue; }
            withRange++;
            int slot = SceneDb.PartSlot[p];
            if (slot < 0) { if (firstBad.Length == 0) firstBad = $"part {k} '{PartTree.NameOf(p)}' is NOT SEATED"; continue; }
            seated++;
            if (_cIdx[slot] > 0) drawn++;
            else if (firstBad.Length == 0) firstBad = $"part {k} '{PartTree.NameOf(p)}' slot {slot} has ZERO index count";
        }
        _log?.Always($"AlloyRenderer: [db pc] {new string(' ', depth * 2)}{why} obj={obj.Value} "
                   + $"model='{SceneDb.ObjSeamModelName[o]}' flags={SceneDb.ObjFlags[o]} visible={SceneDb.ObjVisible[o]} "
                   + $"scene={SceneDb.ObjScene[o].Value} attachTo={SceneDb.ObjAttachTo[o].Value} "
                   + $"pos=({SceneDb.ObjPos[o].X:F1},{SceneDb.ObjPos[o].Y:F1},{SceneDb.ObjPos[o].Z:F1}) "
                   + $"world.t=({SceneDb.ObjWorld[o].Translation.X:F1},{SceneDb.ObjWorld[o].Translation.Y:F1},{SceneDb.ObjWorld[o].Translation.Z:F1}) "
                   + $"sphere.r={SceneDb.ObjSphere[o].W:F1} | parts={n} withMesh={withMesh} recipe={withRecipe} "
                   + $"range={withRange} seated={seated} drawn={drawn} skinned={skinned}/{skins} "
                   + $"anim='{CX.Alloy.Db.DbAnim.CurrentAnimation(obj) ?? "-"}'"
                   + (CX.Alloy.Db.DbAnim.IsAnimating(obj) ? "" : " NOT ANIMATING") + tex
                   + (firstBad.Length > 0 ? $" | FIRST REFUSAL: {firstBad}" : ""));
    }

    /// <summary>A DB slot's index range, for a consumer that would otherwise walk the VDO item tables.
    /// <para>Resolved live off the arena, never out of <c>_dCount</c>/<c>_dOff</c>/<c>_dBase</c>: those are
    /// indexed by DRAW rank and rewritten by every batch build, so reading them by slot handed each caster
    /// another part's mesh under its own matrix -- camera-dependent nonsense in the cascades and the cover
    /// map. The arena's offset is in BYTES of <c>GL_UNSIGNED_SHORT</c>, so the index-valued form is half.</para></summary>
    internal static bool DbCasterRange(int slot, out int idxCount, out uint first, out uint baseVert)
    {
        idxCount = 0; first = 0; baseVert = 0;
        if (!DbSlot(slot)) return false;   // the one already defined above: this slot holds a DB part
        int range = DrawRecipe.Range(_oDbRecipe[slot]);
        if (range < 0 || !MeshArenaDraw.Draw(range, DbLodOf(slot), out int n, out nint off, out int bv) || n <= 0)
            return false;
        idxCount = n;
        first = (uint)((long)off / 2);
        baseVert = (uint)bv;
        return true;
    }

    /// <summary>A DB caster's alpha-mask texture: the recipe's diffuse, which is the same texture the colour
    /// pass binds for it. The engine-path equivalent walks the material, which a DB slot does not have.
    /// <para>This is a <see cref="TexId"/> value -- a REGISTRY SLOT, not a GL name and not an engine tid. It
    /// is the mask's identity, for a key or a counter; a stage that BINDS one wants
    /// <see cref="DbCasterMask"/>.</para></summary>
    internal static int DbCasterTid(int slot)
        => DbSlot(slot) ? (int)DrawRecipe.Tex(_oDbRecipe[slot], 0).Value : 0;

    /// <summary>CX-120: a DB caster's mask as the masked DEPTH stage needs it -- the GL name it binds, and
    /// what the decode said that texture's alpha carries. False = nothing to alpha-test against (no diffuse,
    /// or it has not arrived yet), and the caster casts solid for the frame.
    /// <para>Neither answer may come through <c>OwnedTextures</c>: the database's textures never pass the
    /// engine's upload funnel, so its tables know none of them. It handed the REGISTRY SLOT NUMBER straight
    /// back as the GL name to bind -- so the masked bake alpha-tested every arena caster against whatever
    /// small texture name happened to exist -- and reported "never scanned" for every one, which the hole
    /// gate reads as 1.0, so every DB caster took the masked stage. Together that emptied the sun cascades.</para>
    /// <para><paramref name="holes"/> is a VERDICT here, not a measured fraction: the registry's decode
    /// answers <see cref="TexCoverage"/>, so a cutout reports 1 (alpha-test it) and a slab 0 (cast it solid).
    /// The engine path's fraction exists to decide whether alpha-testing a mostly-opaque texture earns its
    /// batch; with a decoded verdict in hand there is nothing left to weigh.</para></summary>
    internal static bool DbCasterMask(int slot, out int name, out float holes, out float graded)
    {
        name = 0; holes = 0f; graded = 0f;
        if (!DbSlot(slot)) return false;
        TexId id = DrawRecipe.Tex(_oDbRecipe[slot], 0);
        if (!TexRegistry.TryDrawable(id, out uint gl, out _)) return false;
        name = (int)gl;
        switch (TexRegistry.Coverage(id))
        {
            case TexCoverage.Opaque: break;                             // a slab: the solid stage
            case TexCoverage.Blended: holes = 1f; graded = 1f; break;    // #363's glass, through the dither
            default: holes = 1f; break;                                 // Cutout, and Unknown taken as one
        }
        return true;
    }

    /// <summary>Draw arguments rewritten after a compaction moved their range.</summary>
    private static long _dbRangeRefresh;
    internal static long DbRangeRefresh => _dbRangeRefresh;

    /// <summary>Write slot <paramref name="i"/>'s per-draw record from the database columns -- the DB-mode
    /// counterpart of <c>StagePart</c>'s record half, with every term a column read instead of a pointer
    /// chase. Returns false when the part has no resident geometry yet (counted, re-asked next frame).</summary>
    internal static bool DbStage(int i, float* data)
    {
        int part = _oDbPart[i];
        if (part < 0) return false;
        int recipe = _oDbRecipe[i];
        int range = DrawRecipe.Range(recipe);
        if (range < 0) { _dbNoRange++; return false; }
        if (!MeshArenaDraw.Draw(range, DbLodOf(i), out _, out _, out _))
        { _dbNoRange++; return false; }

        DbId obj = SceneDb.PartObj[part];
        int o = obj.Index;
        float* r = data + (long)i * DrawFloats;
        bool fresh = _gate[i].RecGen != _recEpoch || _gate[i].RecEnroll != _gate[i].EnrollGen;
        var f = default(RecordFrameState);
        f.HasModel = true;
        f.Model = SceneDb.PartWorld[part];
        // The keyhole lane is the stage's (1 = dissolving, gated on lit and feature); PartDissolve is the
        // inverse sense, and writing it here flipped every folded solid part to dissolving until the stage
        // flipped it back -- two record rewrites a fold, enough to push the frame into a whole-table upload.
        f.Dissolve = fresh ? 0f : r[18];
        Vector4 tint = SceneDb.ObjTint[o];
        f.Alpha = SceneDb.PartAlpha[part] * SceneDb.PartFade[part] * tint.W;
        f.Lit = _oLit[i];
        if (f.Lit)
        {
            Vector4 amb = DrawRecipe.Ambient(recipe), dif = DrawRecipe.Diffuse(recipe);
            f.AmbR = amb.X * tint.X; f.AmbG = amb.Y * tint.Y; f.AmbB = amb.Z * tint.Z;
            f.DifR = dif.X * tint.X; f.DifG = dif.Y * tint.Y; f.DifB = dif.Z * tint.Z;
            Vector3 emi = SceneDb.PartSelfIllum[part] + SceneDb.ObjEmissive[o];
            f.EmiR = emi.X; f.EmiG = emi.Y; f.EmiB = emi.Z;
            f.Slots = _oSlots[i];
        }
        f.FeatX = _oBone[i] >= 0 ? (float)ShaderFeature.Skin : _oFeat[i * 4];
        f.FeatY = _oBone[i] >= 0 ? _oBone[i] * (float)BoneCount : _oFeat[i * 4 + 1];

        if (fresh) DbWriteStable(r, i);
        // #302's mover level, stamped HERE as well as in StageDbPart: this fold write consumes the move, so the
        // stage that runs after it in the same frame sees the model already in place and would never stamp it.
        bool casterMoved = Moved(in *(Matrix4x4*)r, in f.Model, CasterStep);
        bool moved = WritePerFrame(r, in f, roll: true, fresh: fresh);
        if (fresh || moved) MarkRecordDirty(i);
        if (casterMoved || fresh) _recMovedAt[i] = _frameSeq;
        _gate[i].RecGen = _recEpoch; _gate[i].RecEnroll = _gate[i].EnrollGen; _gate[i].RecFrame = _frameSeq;
        DbWriteSphere(i, in f);
        return true;
    }

    /// <summary>Which LOD of the part's arena range this frame draws.
    /// <para>R1-34: always 0 today, and deliberately. Alloy's AUTOMATIC mesh LOD still runs -- the batch row
    /// picks a level from the cull sphere's screen radius and the arena serves that level's runs -- so a
    /// distant DB part is already thinned. What this would add is the MODEL's own authored alternates
    /// (<c>GobPartTree</c> / <c>SetLODModel</c>), which are a property of the object's animation and part
    /// tree, and seam-anim owns that over the database (its <c>GobLod</c> retarget). When it lands, this
    /// reads <see cref="SceneDb.ObjLodShown"/>; the visible-set query and <c>RouteLive</c> already do.
    /// ponytail: authored LOD alternates unused in DB mode; the hook is one column read.</para></summary>
    private static int DbLodOf(int i) => 0;

    /// <summary>The DB-mode stable half: the same record fields <c>WriteStable</c> writes, sourced from the
    /// recipe and the object's flags instead of <c>_oTri</c>'s vtable and <c>_oGob</c>'s attachment chain.</summary>
    private static void DbWriteStable(float* r, int i)
    {
        int recipe = _oDbRecipe[i], part = _oDbPart[i];
        int o = SceneDb.PartObj[part].Index;
        ObjFlag of = (uint)o < SceneDb.MaxObjects ? SceneDb.ObjFlags[o] : ObjFlag.None;
        r[16] = _oCos[i]; r[17] = _oSin[i];
        int fo = i * 4;
        r[42] = _oFeat[fo + 2];
        // feat.w = -1 marks a DANGLY part for the receiver gate, exactly as the engine path's vtable probe did.
        r[43] = (DrawRecipe.Flags(recipe) & RecipeFlag.Dangly) != 0 ? -1f : _oFeat[fo + 3];
        if (!_oLit[i]) return;
        r[23] = 1f; r[27] = 1f;
        // emi.w, the contact-shadow receiver class and the snow gate: 1 tileset, 0 object, -1 creature. Up the
        // ATTACHMENT chain (CreaturePart), because a body piece, a held weapon or a creature's VFX is its own
        // object and only the body carries the flag -- the object's own flags snowed on every PC's limbs. An
        // EFFECT-class model (a VFX) is no surface for snow either.
        bool fx = ((SceneDb.PartModel[part]?.Classification ?? 0) & HitTest.ClassEffect) != 0;
        r[31] = (of & ObjFlag.Tile) != 0 ? 1f : fx || CreaturePart(i) ? -1f : 0f;
        if (r[31] < 0f) RecCreature++; else if (r[31] > 0f) RecTile++; else RecObject++;
        int po = i * ParamFloats;
        r[32] = _oParam[po]; r[33] = _oParam[po + 1]; r[34] = _oParam[po + 2];
        r[36] = _oParam[po + 4]; r[37] = _oParam[po + 5]; r[38] = _oParam[po + 6]; r[39] = _oParam[po + 7];
        for (int k = 0; k < ParamFloats - ParamExt; k++) r[ExtFloat + k] = _oParam[po + ParamExt + k];
    }

    /// <summary>The DB-mode cull sphere: the recipe's model-space centre and radius through the part's world
    /// matrix. The three exemptions are the engine path's own and for the same reason -- a posed skin, a
    /// dangly and a displacing feature all move their vertices under a matrix that does not.</summary>
    private static void DbWriteSphere(int i, in RecordFrameState f)
    {
        RecipeFlag rf = DrawRecipe.Flags(_oDbRecipe[i]);
        bool exempt = _oBone[i] >= 0 || (rf & RecipeFlag.Dangly) != 0
                      || ShaderFeatures.OutOfPrepass((int)_oFeat[i * 4]) || !(_oR[i] > 0f);
        float cx, cy, cz, rad;
        if (exempt) { cx = 0f; cy = 0f; cz = 0f; rad = NeverCull; }
        else
        {
            Vector3 c = Vector3.Transform(new Vector3(_oCx[i], _oCy[i], _oCz[i]), f.Model);
            cx = c.X; cy = c.Y; cz = c.Z;
            rad = _oR[i] * DbScaleOf(in f.Model);
        }
        fixed (float* sp = _dSph)
        {
            float* s = sp + (long)i * 4;
            s[0] = cx; s[1] = cy; s[2] = cz; s[3] = rad;
        }
        // The caster sphere is the same bound, GEOMETRIC even for the exempted kinds, with a skinned part's
        // pose slack: a bind-pose box does not reach where an extended arm or a swinging cloak does.
        Vector3 gc = Vector3.Transform(new Vector3(_oBx[i], _oBy[i], _oBz[i]), f.Model);
        float gr = _oBr[i] * DbScaleOf(in f.Model);
        if ((rf & RecipeFlag.Skinned) != 0) gr = MathF.Max(gr + 2f, 3f);
        if (!(gr > 0f)) gr = DegenerateCasterRadius;
        fixed (float* sp = _dCastSph)
        {
            float* s = sp + (long)i * 4;
            s[0] = gc.X; s[1] = gc.Y; s[2] = gc.Z; s[3] = gr;
        }
    }

    /// <summary>The largest axis scale a composed matrix applies -- the factor a radius has to carry so a
    /// scaled object culls about its own silhouette rather than its pivot (the scaled-gob-cull lesson).</summary>
    private static float DbScaleOf(in Matrix4x4 m)
    {
        float a = new Vector3(m.M11, m.M12, m.M13).Length();
        float b = new Vector3(m.M21, m.M22, m.M23).Length();
        float c = new Vector3(m.M31, m.M32, m.M33).Length();
        return MathF.Max(a, MathF.Max(b, c));
    }

    /// <summary>The slot-table bookkeeping a self-check has to put back, so exercising the DB path at
    /// Initialize cannot leave the live table holding scratch tenants.</summary>
    internal readonly record struct DbSnapshot(int OwnCount, int OwnLive, int FreeCount, int FreeAvail);

    internal static DbSnapshot DbTableSnapshot() => new(_ownCount, _ownLive, _freeCount, _freeAvail);

    /// <summary>The ownership table's high-water mark, for the self-check that asserts a released DB slot is
    /// handed back out rather than the table only ever growing (R1-3).</summary>
    internal static int OwnHighForSelfCheck => _ownCount;

    /// <summary>The class a seated slot carries, for the order check's own assertions.</summary>
    internal static int ClsOfForSelfCheck(int slot) => _oCls[slot];

    internal static void DbTableRestore(DbSnapshot s)
    {
        // R1-26: every slot at or above the snapshot's high-water mark, plus any the check seated below it
        // (DbRelease already cleared those). A scratch tenant left in _oDbPart would make a live engine slot
        // read as the database's.
        for (int i = 0; i < _ownCount && i < MaxOwned; i++)
            if (i >= s.OwnCount || _oDbPart[i] >= 0)
            { _oDbPart[i] = -1; _oDbRecipe[i] = -1; _oTri[i] = IntPtr.Zero; }
        _ownCount = s.OwnCount; _ownLive = s.OwnLive;
        _freeCount = s.FreeCount; _freeAvail = s.FreeAvail;
        _dbSeated = 0; _dbReleased = 0; _dbNoRange = 0;
    }

    /// <summary>CX-120: bumped whenever the DB slot SET changes (a seat or a release). The order and the area
    /// index are rebuilt off this edge and no other, which is the DB-mode equivalent of an enrolment: a frame
    /// in which nothing was created or destroyed re-sorts nothing.</summary>
    internal static int DbSetGen = 1;

    /// <summary>The slot set changed: a DB part was seated, released, or its arena range moved.
    /// <para>This also has to reach the SHADOW side. The sun's staleness predicate is
    /// <c>invalidate || keyChanged || hadMover || hasMover</c>, and a DB world trips none of them: its parts
    /// are static, so there are no movers, and the cascade keys settle while the arena is still empty -- every
    /// view published its key over a staging of nothing and then skipped forever (<c>sun 0,0 re-baked, 4,0
    /// skipped</c> with 813 draws staged). An arena arrival is exactly the "the world changed under the
    /// published layers" edge <c>ShadowInvalidate</c> names.</para></summary>
    private static void DbSetChanged()
    {
        DbSetGen++;
        CasterPass.ShadowInvalidate();
    }
    private static int _dbOrderGen;

    /// <summary>Rebuild the draw order and the area index if the slot set has moved since the last one.
    /// Returns the live order count.
    /// <para>CX-120: it re-stamps NOTHING. It used to bump <c>_enrollGen</c> and write it onto every live DB
    /// slot, which told every reader of the stamp that all of them had changed hands whenever one part
    /// anywhere in the area appeared or died -- and that is not a cosmetic lie. The shadow caster list reads
    /// the stamp as the tenancy it collected under (<c>CasterPass.Refresh</c>), so one set edge turned the
    /// WHOLE standing list off, and an off entry stages a zero-index draw: the sun cascades were submitted,
    /// culled and drawn every frame with 803 survivors and not one texel of depth in any of the four layers.
    /// A slot that really did change hands is stamped by <see cref="DbSeat"/>, which is the only place a DB
    /// slot ever changes tenant.</para></summary>
    internal static int DbOrder()
    {
        if (_dbOrderGen == DbSetGen) return _orderCount;
        _dbOrderGen = DbSetGen;
        RebuildOrder();
        return _orderCount;
    }

    /// <summary>Off-device: the DB-mode draw ORDER. Every live DB slot appears exactly once, the order is
    /// grouped by class, and each class's [start, end) range covers exactly its own slots -- which is what
    /// every pass depends on when it draws "its" range and nothing else. A slot that fell out of the order is
    /// invisible geometry; a class range that overlapped its neighbour draws parts in the wrong pass with the
    /// wrong depth and blend state. Neither errors.</summary>
    internal static bool DbOrderSelfCheck(ExtenderLog log, int[] slots, int[] wantCls)
    {
        var check = new Checker(log, "AlloyRenderer.DbOrderSelfCheck");
        DbSetChanged();
        _dbOrderGen = DbSetGen - 1;
        int live = DbOrder();
        check.Expect(live == _orderCount, "DbOrder returns the live order count");

        int found = 0;
        for (int k = 0; k < _orderCount; k++)
        {
            int i = _order[k];
            for (int s = 0; s < slots.Length; s++) if (slots[s] == i) { found++; break; }
        }
        check.Expect(found == slots.Length,
                     $"every seated DB slot is in the order exactly once ({found} of {slots.Length})");

        // The order is grouped by class, and each class's range holds only its own.
        bool grouped = true, ranged = true;
        byte last = 0;
        for (int k = 0; k < _orderCount; k++)
        {
            byte c = _oCls[_order[k]];
            if (c < last) grouped = false;
            last = c;
        }
        for (int c = 0; c < AlloyClasses.Count; c++)
            for (int k = _clsStart[c]; k < _clsEnd[c]; k++)
                if (_oCls[_order[k]] != c) ranged = false;
        check.Expect(grouped, "the order walks grouped by class, so each pass owns a contiguous run");
        check.Expect(ranged, "...and each class's range holds only that class's slots");

        int covered = 0;
        for (int c = 0; c < AlloyClasses.Count; c++) covered += _clsEnd[c] - _clsStart[c];
        check.Expect(covered == _orderCount, "the class ranges cover the order exactly once over");
        for (int s = 0; s < slots.Length && s < wantCls.Length; s++)
            check.Expect(_oCls[slots[s]] == wantCls[s], $"slot {s} kept the class its recipe gave it");
        return check.Ok;
    }

    /// <summary>Slots seated, released and refused for want of resident geometry since arming -- the feed's
    /// period line, so a hole is a number rather than an absence.</summary>
    internal static int _dbSeated, _dbReleased, _dbNoRange;
    /// <summary>Whole-table revokes that stood down because the slot space is the feed's (CX-120). Counted so
    /// a DB session that is tearing down every frame is visible rather than merely slow.</summary>
    internal static int _dbRevokeAll;

    internal static string DbTableNote()
        => $"db: seated {_dbSeated} released {_dbReleased} no-geometry {_dbNoRange} live {_ownLive}"
         + (_capacitySpill > 0 ? $" SPILLED {_capacitySpill}" : "");
}
