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
    /// (projections), +0x50 (beams), +0x70 (triggers) are other classes' and are not walked here.
    /// <para>#282, from the caster re-walk it deleted: that walk ALSO read a single "primary" mesh at
    /// <c>nodedata+0x90</c>, on the reading that the engine takes one branch or the other depending on
    /// <c>currentscene+0x1e0</c>. <see cref="SceneWalk"/> has only ever read +0x20 and has been the caster
    /// source since M5 strand 1, so nothing changed when the second read went -- but the offset is recorded
    /// here rather than lost with the code, because a primary-only tile node would be a HOLE in the walk and
    /// this is the only note of where to look for one.</para></summary>
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

    /// <summary>Gob -&gt; the <c>TileData*</c> of the tile it IS, or null for every other gob.
    /// <c>Scene::SpawnTile</c> @0x574470 splits a tile in two: <c>SpawnPartsForTile</c> hands back a Gob for
    /// the parts that need their own identity (the night-lit window, the dangly vine -- note the
    /// <c>AnimateStaticLight</c> behaviour <c>ProcessStaticObject</c> @0x5619e0 attaches), and a plain part
    /// list for the static remainder. <c>ProcessStaticObject</c> stores the tile here (and the gob back at
    /// <c>tile+0x40</c>); <c>Gob::Gob</c> @0x540a00 zeroes it and only tile code ever writes it, so a
    /// non-null field is a positive "this gob is tileset geometry, not an object".</summary>
    public const int GobTileData = 0x1a8;

    /// <summary>Part -&gt; Material*. Ghidra RenderDrawBucket @0x46d060 / PartTriMesh::Draw @0x4513d0.</summary>
    public const int PartMaterial = 0x58;
    /// <summary>#336: the part's WORLD bbox, min at +0x64 and max at +0x70 (three floats each) -- what
    /// <c>Camera::GetCanKeyholeDissolve</c> @0x4a6f70 hands <c>BoxAbovePlane</c> @0x5b32d0, and whose max.z
    /// (+0x78) it compares against the camera's height cut. Static for a gobless, matrix-less part, and all
    /// zeros for a gob part -- see <see cref="PartBboxMin"/> for who fills it and who does not.</summary>
    public const int PartBoxMin = 0x64, PartBoxMax = 0x70;
    /// <summary>Part -&gt; Gob* (the instance the part belongs to; null for world geometry).</summary>
    public const int PartGob = 0x50;

    /// <summary>#338: the four reads of <c>PartTriMesh::IsPartRenderable</c> @0x450e60, the gate EVERY
    /// <c>Part*::Draw</c> override runs before it draws anything (TriMesh, AnimMesh, AABB, DanglyMesh,
    /// Skin) and that <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 runs before every colour-bucket
    /// add. Named for their role in that rule, not for a guessed semantic:
    /// <code>
    /// v = part[0x96];
    /// if (v != 0) {
    ///     n = *(MdlNode**)(part + 8);                       // PartMdlNode
    ///     if (n == 0 || *(int*)(n + 0x118) == 0) return 0;  // MdlRenderGate
    ///     if (*(short*)(n + 0x284) == 0)                    // MdlRenderKind
    ///         v = (*(void**)(n + 0x278) != 0);              // MdlRenderGeom
    /// }
    /// return v;
    /// </code></summary>
    public const int PartRenderFlag = 0x96;
    public const int MdlRenderGate = 0x118, MdlRenderGeom = 0x278, MdlRenderKind = 0x284;
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
    /// <summary>#262: the part's own LOCAL transform, which the engine's animation step writes and every
    /// bone solve composes: <c>Vector</c> position at +0x10, <c>Quaternion</c> orientation at +0x1c stored
    /// (w,x,y,z) -- pinned by <c>operator-(Quaternion const&amp;)</c> @0x5b5d50 negating the first word alone.
    /// The parent back-pointer named on <see cref="PartChildren"/>, spelled out because
    /// <see cref="SkinPose"/> walks UP it as well as down the child array.</summary>
    public const int PartPos = 0x10, PartRot = 0x1c, PartParent = 0x48;
    /// <summary>#262: MdlNode -&gt; its parent MdlNode. <c>GatherBones</c> @0x482b10 admits a child part only
    /// when this matches the walked part's own node, which is what keeps the traversal INDEX -- the thing the
    /// bone map refers to -- equal to the engine's.</summary>
    public const int MdlNodeParent = 0x60;
    /// <summary>#262: the prepared skinning data on a <c>MdlNodeSkin</c>, all of it written by
    /// <c>PartSkin::PostProcess</c> @0x483120 and read by <c>ExecuteSkinGPU</c> @0x482800. Bone count is a
    /// SHORT at +0x284 (zero = not prepared yet); +0x2f0 and +0x300 are the data pointers of the inverse
    /// bind-pose <c>CExoArrayList&lt;Quaternion&gt;</c>/<c>&lt;Vector&gt;</c>, indexed by GATHERED index;
    /// +0x320 is the 64-entry <c>short</c> map from bone slot to gathered index.</summary>
    public const int SkinBoneCount = 0x284, SkinBindRot = 0x2f0, SkinBindPos = 0x300, SkinBoneMap = 0x320;
    /// <summary>#284: the MdlNode's <c>MdlNodeTriMesh_PostProcess*</c> -- the TEMPORARY load-time data
    /// <c>PartSkin::PostProcess</c> @0x483120 reads to build everything on <see cref="SkinBoneCount"/>. It is
    /// both that function's entry guard and its own undoing: the tail calls node <c>+0xa8</c>
    /// (<c>MdlNodeSkinRemoveTemporaryArrays</c> @0x437540 -&gt; <c>MdlNodeTriMesh::InternalRemoveTemporaryArrays</c>
    /// @0x4542a0), which <c>operator delete</c>s the block and writes 0 here.
    /// <para>So this field is the whole idempotency story, and the whole permanence story: null with a zero
    /// bone count means the temporaries are GONE and no number of calls can ever prepare the node.</para></summary>
    public const int SkinTempArrays = 0x278;
    /// <summary>#284: <c>Part</c> vtable DISPLACEMENT of <c>PostProcess(Part*)</c> -- the skinmesh's bind
    /// arrays and bone map. Read off <c>_ZTV8PartSkin</c> (+0x10 for the Itanium header; slot 0xe0 =
    /// <c>PartSkin::PostProcess</c> @0x483120), and it is the same displacement <c>RenderSkinned</c>
    /// @0x482c70 calls it through, on EVERY skinned draw, as its first act.
    /// <para>ABI: <c>(Part* /*rdi*/, Part* /*rsi*/)</c> -- a non-static virtual whose explicit argument is the
    /// base <c>Part::PostProcess</c> @0x436e10 recursion context, threaded down the child array unchanged.
    /// <c>RenderSkinned</c> passes the part as BOTH, and the <c>PartSkin</c> override never reads rsi at all
    /// (disassembled: rsi is dead from entry). Pass the part twice, as the engine does.</para>
    /// <para>VIRTUAL on purpose, and the vptr must be proven to be <c>PartSkin</c>'s exactly: on any other
    /// part this slot is <c>Part::PostProcess</c>, which recurses the whole subtree.</para></summary>
    public const int PartVtblPostProcess = 0xe0;
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

    /// <summary>#264: <c>VertexPrimitive</c> vtable DISPLACEMENT of <c>SetupArrays()</c> -- the call that
    /// fills <see cref="VpVdo"/>/<see cref="VpItem"/> in the first place. Read off
    /// <c>_ZTV19VertexPrimitiveFlat</c> (slot 0x38 = <c>VertexPrimitiveFlat::SetupArrays</c> @0x458920), and
    /// it is the same displacement <c>PartTriMesh::Draw</c> @0x4513d0 calls it through:
    /// <c>(**(code**)(*(long*)(this+0x88) + 0x38))()</c>. VIRTUAL on purpose -- Skinned, Anim and Dangly
    /// primitives each override it, so it must go through the live vptr and never through the Flat symbol.
    /// <para>Idempotent: the body's first act is an early-out when the primitive is already set up
    /// (<c>+0x64</c> set, VDO non-null, <c>+0x84</c> clear), so a call on a bound primitive is a compare.</para>
    /// ABI: <c>(VertexPrimitive* /*rdi*/)</c>, no arguments, no return.</summary>
    public const int VpVtblSetupArrays = 0x38;
    /// <summary>Part -&gt; texture-rotation angle (degrees, float; Ghidra PartTriMesh::RotateTexture
    /// @0x44fff0): TransformTextureMatrix pushes it before every draw -- NWN's tile-texture 0/90/180/270
    /// anti-repetition scheme. Part of the group key (raw bits), else a batch renders every member under the
    /// first member's rotation.</summary>
    public const int PartTexRotate = 0xb0;
    /// <summary>Part -&gt; its own alpha multiplier, the first term of <c>PartTriMesh::GetIsOpaque</c>
    /// @0x43cee0 -- and the one <c>RenderDrawBucket</c> folds the tile's fade factor into for the length of a
    /// bucket-8 draw. Read live: it is animated state.</summary>
    public const int PartAlpha = 0xa0;
    /// <summary>#126: the part's bounding box, min at +0x64 and max at +0x70 -- the pair
    /// <c>Camera::GetCanKeyholeDissolve</c> @0x4a6f70 hands to <c>BoxAbovePlane</c>, and the same one
    /// <c>SceneInputs</c>/<c>CasterPass</c>/<c>WaterBodies</c> cull against. Its centre is the blended sort
    /// key.</summary>
    /// <para>WORLD space, and TILE PARTS ONLY: <c>ComputePartBBox</c> @0x561370 rotates and translates
    /// every vertex into world before min/maxing, and its sole caller is <c>ProcessStaticParts</c>
    /// @0x570970 off <c>Scene::SpawnTile</c>. <c>PartTriMesh::PartTriMesh</c> @0x53cb50 zeroes the six
    /// floats, so a GOB part (a placeable, a creature) carries zeros here forever -- which is why a
    /// matrix-class part's world centre is taken from its node matrix translation instead, and why the
    /// engine's own keyhole test refuses any part with a gob.</para>
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

    /// <summary>#284: the three fields the engine's own lazy VAO build reads, and which therefore decide
    /// whether driving that build is safe yet. <c>VdoIndexed</c> is the byte
    /// <c>VertexDataObjectBase</c>'s constructor zeroes and <c>VertexDataObject::Build</c> @0x45bac0 sets to
    /// 1 for modes 1 and 2 -- <c>GetElementArrayBuffer</c> @0x45b320 returns 0 while it is clear, and a zero
    /// there sends <c>GLRender::BindBuffer</c> @0x4d04a0 down its unbind branch in the middle of the build.
    /// <c>VdoElemToken</c>/<c>VdoVertToken</c>/<c>VdoDynToken</c> are the three pool tokens whose GL names
    /// the build captures into the new VAO -- the attribute loop @0x4d0028..0x4d0427 runs TWICE, over
    /// (+0x24, mask +0x28) and then (+0x30, mask +0x34) (<c>mov 0x30(%r14),%eax</c> @0x4d040d). An absent
    /// token reads 0xffffffff and the engine skips it (<c>cmp $0xffffffff,%eax</c> @0x4d003a), which is why
    /// the drip's residency gate treats absent and resident alike.</summary>
    public const int VdoIndexed = 0x8, VdoElemToken = 0x18, VdoVertToken = 0x24, VdoDynToken = 0x30;
    /// <summary>The attribute masks the two passes of that loop read (+0x28 with +0x24, +0x34 with +0x30):
    /// together they fix every stride and offset the build captures.</summary>
    public const int VdoVertMask = 0x28, VdoDynMask = 0x34;
    /// <summary>The engine's "no such pool" token, tested at every one of the three token fields.</summary>
    public const uint VdoNoPool = 0xFFFFFFFF;

    /// <summary>PartLight fields three scene walkers read: range at +0x70 (sun/moon carry ~100000, point
    /// lights ~10, so &gt;1000 is the directional discriminator), a discriminant that must be zero at +0x80,
    /// and the direction vector at +0xac. The direction is +0xac and NOT the +0x64 one <c>SetLightGL</c> uses
    /// -- <c>BSPCreateStaticProjections</c> bakes the volumes against +0xac too, so reading the other pair
    /// extrudes them the wrong way.</summary>
    public const int LightRange = 0x70, LightKind = 0x80, LightDir = 0xac;

    /// <summary>Range that tells a directional (sun/moon) light from a placed one: directionals carry
    /// ~100000, points ~10. Read off the same <see cref="LightRange"/> field by everything that has to make
    /// the distinction.</summary>
    public const float DirectionalRange = 1000f;
}
