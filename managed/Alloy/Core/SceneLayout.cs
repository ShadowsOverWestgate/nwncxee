namespace Sowx.Alloy;

/// <summary>
/// Engine struct offsets that more than one scene walker dereferences. These are not symbols -- nothing
/// resolves them -- so they live with the code that reads them, not in <c>SymbolMap</c> (#159). An offset
/// with exactly one reader stays private in that reader; only the shared ones are here, written once so
/// the RE provenance cannot drift between copies. All Ghidra-verified against the Linux client.
/// </summary>
internal static class SceneLayout
{
    /// <summary>Scene -&gt; BSPNode* root (the static-geometry BSP). Linux-verified in ManageSceneBSP.</summary>
    public const int SceneBspRoot = 0x188;
    /// <summary>#256: Scene -&gt; <c>CExoArrayList&lt;Gob*&gt;</c>, EVERY gob in the scene: items at +0x130,
    /// count at +0x138. <c>Scene::ChildAdd</c> @0x461d50 appends here and <c>ManageSceneBSP</c> @0x47fa80
    /// iterates <c>0..*(Scene+0x138)</c> over it three separate times. This is the authoritative membership
    /// list -- <c>gobbucket</c> @0x19abbf0 is only the VISIBLE slice and is rebuilt every frame.</summary>
    public const int SceneGobs = 0x130, SceneGobCount = 0x138;

    /// <summary>#256: the BSP node's inventory pointer (<c>BSPNode+0x70</c>) and, inside it, the
    /// <c>CExoArrayList&lt;PartTriMesh*&gt;</c> of tile geometry (items +0x20, count +0x28).
    /// <c>SceneBSPAddPart</c> @0x47ca10 files a plain <c>PartTriMesh</c> at exactly
    /// <c>*(BSPFitBox(...)+0x70) + 0x20</c>; the other lists at +0x10 (gobs), +0x30 (emitters), +0x40
    /// (projections), +0x50 (beams), +0x70 (triggers) are other classes' and are not walked here.</summary>
    public const int BspNodeData = 0x70, BspDataTriMesh = 0x20, BspDataTriMeshCount = 0x28;
    /// <summary>Gob -&gt; node pointer. MUST be non-null before appending to the gobbucket: the sort comparator
    /// <c>FUN_0055e070</c> blindly derefs <c>*(gob+0x68)+0x8e</c> (the render path null-checks it, the sort
    /// does not) -- a gob with 0x68==0 in the bucket crashes the sort.</summary>
    public const int GobNode = 0x68;
    /// <summary>Gob -&gt; Part* part-tree root. <c>ProcessGob</c>/<c>AddPartsTreeToDrawBuckets</c> walk it; guard
    /// non-null so a geometry-less gob is skipped, not dereferenced.</summary>
    public const int GobPartTree = 0x90;
    /// <summary>Gob -&gt; world position (float x@+0xa8, y@+0xac, z@+0xb0).</summary>
    public const int GobPosition = 0xa8;
    /// <summary>#256: the two LOD-alternate part-tree roots beside <see cref="GobPartTree"/>.
    /// <c>Gob::SetLODModel</c> @0x43e6a0 ROTATES the three against each other every frame, per visible gob,
    /// with no construction, no destruction and no epoch bump -- #253 §3.2's registry-killer. Enumerating
    /// all three makes the rotation a field read: whichever root is active, its parts are already recorded.
    /// </summary>
    public const int GobLodRoot1 = 0x98, GobLodRoot2 = 0xa0;
    /// <summary>#256: Gob -&gt; <c>CExoArrayList&lt;Part*&gt;</c> of the roots this gob APPENDED at runtime
    /// (items +0x130, count +0x138), parallel to the <c>Model*</c> array at +0x120. <c>Gob::Append</c>
    /// @0x4422a0 pushes here; <c>Gob::Amputate</c> @0x443fa0 removes.</summary>
    public const int GobAppendRoots = 0x130, GobAppendCount = 0x138;

    /// <summary>Part -&gt; Material*. Ghidra RenderDrawBucket @0x46d060 / PartTriMesh::Draw @0x4513d0.</summary>
    public const int PartMaterial = 0x58;
    /// <summary>Part -&gt; Gob* (the instance the part belongs to; null for world geometry).</summary>
    public const int PartGob = 0x50;
    /// <summary>Part -&gt; its <c>VertexPrimitive</c>, and NOT a node despite the name.
    /// <para>#134 named it: the vtables prove it (live capture, resolved against nm:
    /// <c>_ZTV19VertexPrimitiveFlat</c> for a TriMesh part, <c>_ZTV21VertexPrimitiveDangly</c> for a dangly
    /// one), and so does its +0x10, which is the back-pointer to the part itself -- <c>n10 == part</c> on all
    /// 12000 draws of a two-run census. The name stays because it is threaded through four services; what it
    /// holds does not change.</para></summary>
    public const int PartNode = 0x88;
    /// <summary>Part -&gt; TileData* (Ghidra CheckFadePlane @0x47bf30).</summary>
    public const int PartTile = 0x98;
    /// <summary>#256: Part -&gt; child array (items +0x38, count +0x40). Written by
    /// <c>CreateInstanceTreeR</c> @0x43c6b0 and freed by <c>Part::~Part</c> @0x437720; the parent
    /// back-pointer is +0x48. This is what makes a part TREE walkable without entering a draw.</summary>
    public const int PartChildren = 0x38, PartChildCount = 0x40;
    /// <summary>#256: the primitive-type index on the MdlNode (<c>AlloyRenderer.PartMdlNode</c>), argument 3 of the engine's own
    /// <c>RenderVertexData</c> call. Read out of <c>RenderFlat</c> @0x450eb0:

    /// <see cref="PartMdlNode"/>. Indexes <c>PrimTypeTranslationTable</c>.</summary>
    public const int MdlPrimType = 0x270;
    /// <summary>#256: the draw arguments, as FIELDS. <c>RenderFlat</c> @0x450eb0 calls
    /// <c>RenderVertexData(*(vp+0x58), *(uint*)(vp+0x60), prim, size, offset)</c> where size and offset are
    /// <c>VertexDataObject::GetItemElementSize/Offset</c> of that same pair. So the whole triple the batcher
    /// used to learn by watching the engine draw is two loads off the VertexPrimitive.
    /// <para><b>This is every draw path, checked and not inferred.</b> <c>RenderEnvironmentMapped</c>
    /// @0x451040 and <c>RenderBumpyShiny</c> @0x451200 both end in a tail call to <c>RenderFlat</c> with the
    /// same VertexPrimitive; and a SKINNED part reaches it too -- <c>PartSkin::Draw</c> @0x4830b0 -&gt;
    /// <c>RenderSkinned</c> @0x482c70 -&gt; <c>ExecuteSkinGPU</c> @0x482800, whose tail is
    /// <c>RenderFlat((VertexPrimitiveFlat*)vp)</c> on the <c>VertexPrimitiveSkinned</c>. Same two offsets
    /// for all four classes.</para>
    /// <para><c>VertexPrimitiveFlat::SetupArrays</c> @0x458920 is the writer, and it runs from the top of
    /// <c>PartTriMesh::Draw</c> (vp vtable +0x38; <c>ExecuteSkinGPU</c> calls the same slot). A part whose geometry has never been uploaded therefore
    /// reads a null VDO and an item of -1 -- that is not an error, it is "not resident yet", and the record
    /// waits unbound rather than being invented.</para></summary>
    public const int VpVdo = 0x58, VpItem = 0x60;
    /// <summary>Part -&gt; texture-rotation angle (degrees, float; Ghidra PartTriMesh::RotateTexture
    /// @0x44fff0): TransformTextureMatrix pushes it before every draw -- NWN's tile-texture 0/90/180/270
    /// anti-repetition scheme. Part of the group key (raw bits), else a batch renders every member under the
    /// first member's rotation.</summary>
    public const int PartTexRotate = 0xb0;
    /// <summary>Part -&gt; its own alpha multiplier, the first term of <c>PartTriMesh::GetIsOpaque</c>
    /// @0x43cee0 -- and the one <c>RenderDrawBucket</c> folds the tile's fade factor into for the length of a
    /// bucket-8 draw. Read live: it is animated state.</summary>
    public const int PartAlpha = 0xa0;
    /// <summary>#126: the part's LOCAL bounding box, min at +0x64 and max at +0x70 -- the pair
    /// <c>Camera::GetCanKeyholeDissolve</c> @0x4a6f70 hands to <c>BoxAbovePlane</c>, and the same one
    /// <c>SceneInputs</c>/<c>CasterPass</c>/<c>WaterBodies</c> cull against. Its centre is the blended sort
    /// key.</summary>
    public const int PartBboxMin = 0x64, PartBboxMax = 0x70;

    /// <summary>MdlNode -&gt; model matrix (row-major float[16]) at +0x18 and its flags at +0x80 --
    /// RenderDrawBucket @0x46d060 passes exactly that address to RenderInterface::SetModelMatrix, and only
    /// for matrix-class parts (node+0x80 &amp; 0x1e). Bit-identical matrix = same placeable transform.</summary>
    public const int NodeMatrix = 0x18, NodeFlags = 0x80;

    /// <summary>The blend-disable flag <c>RenderFlat</c> reads at mat+0x98 (Ghidra
    /// GetTexture/GetEnvMapTID @0x4c5810/0x4c5c40; texture slots sit at mat+i*0x10).</summary>
    public const int MatBlendFlag = 0x98;

    /// <summary>The material's own env map (<c>Material::HasEnvMap</c> @0x4c6180 tests exactly this field).
    /// It is NOT one of the numbered texture slots, so <c>GetTexture</c> never reaches it -- a group keyed on
    /// the numbered slots alone cannot tell an env-mapped material from a plain one sharing its diffuse, and
    /// one batched draw would then paint all of them with the representative's shine (or none of them).
    /// Compared only, never dereferenced: a <c>CAurTexture*</c> is borrowed.</summary>
    public const int MatEnvMap = 0x40;

    /// <summary>Material -&gt; SharedMaterial* override at +0xd0 (Ghidra GetTexture/GetEnvMapTID
    /// @0x4c5810/0x4c5c40).</summary>
    public const int MatShared = 0xd0;

    /// <summary>#222: the Scene's fog COLOUR (Ghidra <c>Scene::SetFogColor(Vector&amp;)</c> @0x45e220 writes
    /// exactly these three floats), and the gob byte that says a part is painted in it.
    /// <c>PartTriMesh::Mat</c> @0x450960 takes a branch nothing in the Alloy pass had: with
    /// <c>gob+0x19</c> set and a live scene it calls <c>SetColor4f(fog.r, fog.g, fog.b, 1.0)</c> and draws
    /// the part in the FOG COLOUR at full alpha, instead of white at the part's own alpha. The skyfade gob
    /// is that part -- which is why vanilla's horizon fade is dark at night and ours was a bright grey
    /// texture.</summary>
    public const int SceneFogColor = 0x14c, GobFogTint = 0x19;

    /// <summary>VDO layout (Ghidra AddItem/RenderVertexData/SetVertexDataObject): +0xc VAO id, +0x14 mode
    /// (1 = indexed static), vertex item table {offset,size} at +0x48 (count +0x50), element item table at
    /// +0x58 (count +0x60).</summary>
    public const int VdoVao = 0xc, VdoMode = 0x14, VdoVertItems = 0x48, VdoVertCount = 0x50,
                     VdoElemItems = 0x58, VdoElemCount = 0x60;

    /// <summary>PartLight fields three scene walkers read: range at +0x70 (sun/moon carry ~100000, point
    /// lights ~10, so &gt;1000 is the directional discriminator), a discriminant that must be zero at +0x80,
    /// and the direction vector at +0xac. The direction is +0xac and NOT the +0x64 one <c>SetLightGL</c> uses
    /// -- <c>BSPCreateStaticProjections</c> bakes the volumes against +0xac too, so reading the other pair
    /// extrudes them the wrong way.</summary>
    public const int LightRange = 0x70, LightKind = 0x80, LightDir = 0xac;
}
