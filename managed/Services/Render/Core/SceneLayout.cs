namespace Sowx.Services.Render;

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
    /// <summary>Gob -&gt; node pointer. MUST be non-null before appending to the gobbucket: the sort comparator
    /// <c>FUN_0055e070</c> blindly derefs <c>*(gob+0x68)+0x8e</c> (the render path null-checks it, the sort
    /// does not) -- a gob with 0x68==0 in the bucket crashes the sort.</summary>
    public const int GobNode = 0x68;
    /// <summary>Gob -&gt; Part* part-tree root. <c>ProcessGob</c>/<c>AddPartsTreeToDrawBuckets</c> walk it; guard
    /// non-null so a geometry-less gob is skipped, not dereferenced.</summary>
    public const int GobPartTree = 0x90;
    /// <summary>Gob -&gt; world position (float x@+0xa8, y@+0xac, z@+0xb0).</summary>
    public const int GobPosition = 0xa8;

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

    /// <summary>Material -&gt; SharedMaterial* override at +0xd0 (Ghidra GetTexture/GetEnvMapTID
    /// @0x4c5810/0x4c5c40).</summary>
    public const int MatShared = 0xd0;

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
