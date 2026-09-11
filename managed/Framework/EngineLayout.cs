using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Sowx.Framework;

/// <summary>
/// Every engine-memory constant the extender reads -- struct field offsets and vtable slots -- in one
/// table, keyed by (os, build). <see cref="SymbolMap"/> answers "where is this function"; this answers
/// "where is this field", which the Windows port (#429) made a per-platform question.
/// </summary>
/// <remarks>
/// <para>Three declaration forms, and there is no fourth: <see cref="Diverges"/> (a value per OS),
/// <see cref="Same"/> (measured identical on both) and <see cref="Unresolved"/> (a Linux value, Windows
/// never looked). The point of <c>Same</c> is that "verified identical" and "never looked" stop being the
/// same bare <c>const</c>, and <c>Unresolved</c> gives the migration a completion counter.</para>
/// <para>Resolved once at static init, read afterwards as a plain field -- Alloy reads these in the frame
/// loop, so there is no lookup at the use site. Declarations live here rather than in the file that reads
/// them so that the count is honest: per-file static initializers run on first touch, and a count that
/// depends on what the player has done is not a count.</para>
/// <para>Provenance -- where each Windows value came from -- is <c>docs/engine-layout.md</c>.</para>
/// </remarks>
public static unsafe class EngineLayout
{
    // Declared first on purpose: static field initializers run in textual order, and the table below
    // registers into this list as it resolves.
    private static readonly List<Row> _rows = new();

    // ================= the table =================
    // Each row names the engine class it belongs to: that is the key #431's Ghidra extractor pairs on,
    // and the unit #430 measures (a class diverges in 2-3 contiguous segments, anchored on a function pair).

    /// <summary>CNWCObject + 0x0c: object type byte -- 5 creature, 6 item, 9 placeable, 0xa door.</summary>
    public static readonly int ObjType = Same("CNWCObject", "type", 0x0c);

    /// <summary>CNWCCreature: "the feedback plate is currently shown for me", set at the top of
    /// ShowFeedbackBubbleText (Linux disasm @0x71a81e, Windows @0x80b240).</summary>
    public static readonly int CreaturePlated = Diverges("CNWCCreature", "plated", linux: 0x468, win: 0x448, Expect.Byte01);

    /// <summary>CNWCPlaceable, same flag (Linux disasm @0x7ea8b8, Windows @0x871b60).</summary>
    public static readonly int PlaceablePlated = Diverges("CNWCPlaceable", "plated", linux: 0x218, win: 0x1f8, Expect.Byte01);

    /// <summary>Virtual slot of <c>GetPortrait(unsigned char)</c> on CNWCObject -- MSVC orders the table
    /// differently, so this is a vtable index, not a field (#420).</summary>
    public static readonly int GetPortraitSlot = Diverges("CNWCObject", "vtbl.GetPortrait", linux: 0x1d8, win: 0x1d0, Expect.VtblSlot);

    // ---- vtable slots Alloy calls through (#433) ----
    // Every one is -8 on Windows and for one reason: Itanium gives a virtual destructor TWO slots
    // (complete + deleting), MSVC gives it one, so everything declared after it shifts by a pointer.
    // Measured, not assumed -- tools/abi/vtable-slots.py prints both tables side by side.

    /// <summary>Part::AsPartTriMesh() -- the type filter SkyBox/BucketGate/GrassField run a part through.</summary>
    public static readonly int PartAsTriMeshSlot = Diverges("Part", "vtbl.AsPartTriMesh", linux: 0x60, win: 0x58, Expect.VtblSlot);

    /// <summary>Part::AsPartBeam() -- same filter for the beam overlays (#130).</summary>
    public static readonly int PartAsPartBeamSlot = Diverges("Part", "vtbl.AsPartBeam", linux: 0x90, win: 0x88, Expect.VtblSlot);

    /// <summary>Part::PostProcess(Part*).</summary>
    public static readonly int PartPostProcessSlot = Diverges("Part", "vtbl.PostProcess", linux: 0xe0, win: 0xd8, Expect.VtblSlot);

    /// <summary>CAurTexture::AsCAurTextureBasic().</summary>
    public static readonly int TexAsBasicSlot = Diverges("CAurTexture", "vtbl.AsCAurTextureBasic", linux: 0x10, win: 0x08, Expect.VtblSlot);

    /// <summary>CAurTexture::GetReferencedTexture() -- the indirection every tid read goes through first.</summary>
    public static readonly int TexReferencedSlot = Diverges("CAurTexture", "vtbl.GetReferencedTexture", linux: 0x18, win: 0x10, Expect.VtblSlot);

    /// <summary>VertexPrimitive::SetupArrays() -- virtual on purpose: Skinned/Anim/Dangly each override it.</summary>
    public static readonly int VpSetupArraysSlot = Diverges("VertexPrimitive", "vtbl.SetupArrays", linux: 0x38, win: 0x30, Expect.VtblSlot);

    /// <summary>CAurBehavior::AsBehaviorCameraFollow() -- the camera-follow type filter (#65).</summary>
    public static readonly int BehaviorAsFollowSlot = Diverges("CAurBehavior", "vtbl.AsBehaviorCameraFollow", linux: 0x48, win: 0x40, Expect.VtblSlot);

    /// <summary>CycleTIDTextureController::Control() -- one animated-texture tick.</summary>
    public static readonly int CycleControlSlot = Diverges("CycleTIDTextureController", "vtbl.Control", linux: 0x10, win: 0x08, Expect.VtblSlot);

    // ================= Alloy: the Scene walk (#436, batch 1 of #429 section 2) =================
    // Measured 2026-09-11 with tools/ghidra/FieldDump.java over both programs in ~/ghidra/nwn-linux.rep,
    // diffed by tools/ghidra/field-diff.py (.scratch/436). A row is Same/Diverges only where a paired
    // function, a constructor block or a class-wide segment measured at both ends says so; where the class
    // had no validated pair, the row is Unresolved, which is the honest answer and not a failure.
    // Every value here is decompiler evidence -- a CANDIDATE until a runtime probe says otherwise.

    // ---- Scene (identical 0x0..0x1e0: 32 offsets paired over 38 functions, no conflict) ----

    /// <summary>Scene -&gt; <c>CExoArrayList&lt;Gob*&gt;</c>, EVERY gob in the scene: items at +0x130, count at
    /// +0x138. <c>Scene::ChildAdd</c> @0x461d50 appends here and <c>ManageSceneBSP</c> @0x47fa80 iterates
    /// <c>0..*(Scene+0x138)</c> over it three separate times. This is the authoritative membership list --
    /// <c>gobbucket</c> @0x19abbf0 is only the VISIBLE slice and is rebuilt every frame (#256).</summary>
    public static readonly int SceneGobs = Same("Scene", "gobs", 0x130);
    /// <inheritdoc cref="SceneGobs"/>
    public static readonly int SceneGobCount = Same("Scene", "gobCount", 0x138);

    /// <summary>Scene -&gt; BSPNode* root (the static-geometry BSP). Linux-verified in ManageSceneBSP.</summary>
    public static readonly int SceneBspRoot = Same("Scene", "bspRoot", 0x188);

    /// <summary>The fog block <c>Scene::DoFog</c> @0x45f1b0 pushes into <c>GLRender::m_fog</c>: enable,
    /// start, end, and the colour <c>Scene::SetFogColor(Vector&amp;)</c> @0x45e220 writes. #222: the colour is
    /// also what <c>PartTriMesh::Mat</c> @0x450960 paints a scene-tinted gob in.</summary>
    public static readonly int SceneFogEnable = Same("Scene", "fogEnable", 0x140);
    /// <inheritdoc cref="SceneFogEnable"/>
    public static readonly int SceneFogStart = Same("Scene", "fogStart", 0x144);
    /// <inheritdoc cref="SceneFogEnable"/>
    public static readonly int SceneFogEnd = Same("Scene", "fogEnd", 0x148);
    /// <inheritdoc cref="SceneFogEnable"/>
    public static readonly int SceneFogColor = Same("Scene", "fogColor", 0x14c);

    /// <summary>Scene -&gt; LightManager* (<c>Scene::RenderSinglePass</c> @0x46e5f0 calls
    /// <c>GetShadowLights(*(this + 0x158))</c>).</summary>
    public static readonly int SceneLightManager = Same("Scene", "lightManager", 0x158);

    /// <summary>Scene -&gt; the embedded <c>DrawBucketManager</c> (0x310 bytes, constructed in place by
    /// <c>Scene::Scene</c> @0x4724f0; every <c>AddPartToBucket</c> call passes <c>this+0x208</c>).
    /// <para>Above the measured 0x0..0x1e0 identical run, so Windows is unmeasured -- the Windows Scene
    /// functions touch 0x1f0 and 0x200 that Linux's do not, which is exactly the region in question.</para></summary>
    public static readonly int SceneDrawBuckets = Unresolved("Scene", "drawBuckets", 0x208);

    /// <summary>Scene vtable slot 81 -- <c>Scene::IsAnimating</c> @0x45e000. Vanilla gates the whole dangly
    /// solve on it, which is what stops cloaks swaying behind a paused menu. Both tables printed by
    /// <c>tools/abi/vtable-slots.py Scene</c>, and the -8 is the virtual-dtor rule #433 measured.</summary>
    public static readonly int SceneIsAnimatingSlot = Diverges("Scene", "vtbl.IsAnimating", linux: 0x288, win: 0x280, Expect.VtblSlot);

    // ---- Gob (identical 0x0..0x4b8: 28 offsets paired over 50 functions, no conflict) ----

    /// <summary>Gob fields that change how a part shades, all read by <c>PartTriMesh::Mat</c> @0x450960
    /// except the env override, which <c>PartTriMesh::Draw</c> @0x4513d0 reads to reroute the whole draw to
    /// <c>RenderEnvironmentMapped</c>. The two flags ARM a modulation; the values beside them are the
    /// modulation, and <c>gob+0xfc</c> is the colour whose 12 bytes end exactly where the alpha begins.</summary>
    public static readonly int GobHighlight = Same("Gob", "highlight", 0x18);
    /// <summary>#222: the gob byte that says a part is painted in the scene's fog colour.
    /// <c>PartTriMesh::Mat</c> @0x450960 takes a branch nothing in the Alloy pass had: with this set and a
    /// live scene it calls <c>SetColor4f(fog.r, fog.g, fog.b, 1.0)</c> and draws the part in the FOG COLOUR
    /// at full alpha. The skyfade gob is that part.</summary>
    public static readonly int GobSceneTint = Same("Gob", "sceneTint", 0x19);
    /// <summary>Gob -&gt; node pointer, and the same field the fade owner is read through. MUST be non-null
    /// before appending to the gobbucket: the sort comparator <c>FUN_0055e070</c> blindly derefs
    /// <c>*(gob+0x68)+0x8e</c> (the render path null-checks it, the sort does not).</summary>
    public static readonly int GobNode = Same("Gob", "node", 0x68);
    /// <summary>Gob -&gt; Part* part-tree root. <c>ProcessGob</c>/<c>AddPartsTreeToDrawBuckets</c> walk it;
    /// guard non-null so a geometry-less gob is skipped, not dereferenced.</summary>
    public static readonly int GobPartTree = Same("Gob", "partTree", 0x90);
    /// <summary>#256: the two LOD-alternate part-tree roots beside <see cref="GobPartTree"/>.
    /// <c>Gob::SetLODModel</c> @0x43e6a0 ROTATES the three against each other every frame, per visible gob,
    /// with no construction, no destruction and no epoch bump.</summary>
    public static readonly int GobLodRoot1 = Same("Gob", "lodRoot1", 0x98);
    /// <inheritdoc cref="GobLodRoot1"/>
    public static readonly int GobLodRoot2 = Same("Gob", "lodRoot2", 0xa0);
    /// <summary>Gob -&gt; world position (float x@+0xa8, y@+0xac, z@+0xb0).</summary>
    public static readonly int GobPosition = Same("Gob", "position", 0xa8);
    /// <inheritdoc cref="GobHighlight"/>
    public static readonly int GobColourMul = Same("Gob", "colourMul", 0xf6);
    /// <inheritdoc cref="GobHighlight"/>
    public static readonly int GobSelfIllum = Same("Gob", "selfIllum", 0xf8);
    /// <inheritdoc cref="GobHighlight"/>
    public static readonly int GobColour = Same("Gob", "colour", 0xfc);
    /// <inheritdoc cref="GobHighlight"/>
    public static readonly int GobAlpha = Same("Gob", "alpha", 0x108);
    /// <inheritdoc cref="GobHighlight"/>
    public static readonly int GobFadeAlpha = Same("Gob", "fadeAlpha", 0x10c);
    /// <inheritdoc cref="GobHighlight"/>
    public static readonly int GobEmissive = Same("Gob", "emissive", 0x110);
    /// <summary>#256: Gob -&gt; <c>CExoArrayList&lt;Part*&gt;</c> of the roots this gob APPENDED at runtime
    /// (items +0x130, count +0x138), parallel to the <c>Model*</c> array at +0x120. <c>Gob::Append</c>
    /// @0x4422a0 pushes here; <c>Gob::Amputate</c> @0x443fa0 removes.</summary>
    public static readonly int GobAppendRoots = Same("Gob", "appendRoots", 0x130);
    /// <inheritdoc cref="GobAppendRoots"/>
    public static readonly int GobAppendCount = Same("Gob", "appendCount", 0x138);
    /// <summary>Gob -&gt; the <c>TileData*</c> of the tile it IS, or null for every other gob.
    /// <c>ProcessStaticObject</c> @0x5619e0 stores the tile here (and the gob back at <c>tile+0x40</c>);
    /// <c>Gob::Gob</c> @0x540a00 zeroes it and only tile code ever writes it, so a non-null field is a
    /// positive "this gob is tileset geometry, not an object".</summary>
    public static readonly int GobTileData = Same("Gob", "tileData", 0x1a8);
    /// <summary>#128: the per-gob environment-map override flag <c>PartTriMesh::Draw</c> @0x4513d0 reads.</summary>
    public static readonly int GobEnvOverride = Same("Gob", "envOverride", 0x480);

    // ---- MdlNode and its subclasses ----

    /// <summary>The byte on whatever <c>gob+0x68</c> points at, whose bit 3 arms a second alpha multiply by
    /// <see cref="GobFadeAlpha"/> -- Ghidra <c>PartTriMesh::Mat</c>:
    /// <c>if ((*(*(gob+0x68)+0x8e) &amp; 8) != 0)</c>.
    /// <para>Filed under <c>Model</c> rather than <c>MdlNode</c> because <c>Gob::GetModelName</c> @0x437270
    /// reads a name off that same field -- but NEITHER class is confirmed, and no function of any class in
    /// the #436 sweep touches +0x8e on either build. #430 lists <c>Model</c> as unresolved for want of
    /// paired functions, so this row is unresolved twice over: the class and the offset.</para></summary>
    public static readonly int NodeOwnerFlags = Unresolved("Model", "ownerFlags", 0x8e);
    /// <summary>#262: MdlNode -&gt; its parent MdlNode. <c>GatherBones</c> @0x482b10 admits a child part only
    /// when this matches the walked part's own node, which is what keeps the traversal INDEX equal to the
    /// engine's.</summary>
    public static readonly int MdlNodeParent = Same("MdlNode", "parent", 0x60);
    /// <summary>MdlNode -&gt; model matrix (row-major float[16]). RenderDrawBucket @0x46d060 passes exactly
    /// that address to <c>RenderInterface::SetModelMatrix</c>. Below MdlNode's measured 0x58..0x98 run and
    /// touched by no paired MdlNode function, so Windows is unmeasured.</summary>
    public static readonly int NodeMatrix = Unresolved("MdlNode", "matrix", 0x18);
    /// <summary>MdlNode -&gt; matrix flags; only a matrix-class part (<c>node+0x80 &amp; 0x1e</c>) gets its
    /// matrix pushed.</summary>
    public static readonly int NodeFlags = Same("MdlNode", "flags", 0x80);
    /// <summary>#256: the primitive-type index, argument 3 of the engine's own <c>RenderVertexData</c> call.
    /// Read out of <c>RenderFlat</c> @0x450eb0. Far above MdlNode's measured run.</summary>
    public static readonly int MdlPrimType = Unresolved("MdlNode", "primType", 0x270);

    /// <summary>#338: the render gate <c>PartTriMesh::IsPartRenderable</c> @0x450e60 reads off the part's
    /// node -- zero here refuses the draw outright.</summary>
    public static readonly int MdlRenderGate = Same("MdlNodeTriMesh", "renderGate", 0x118);
    /// <summary>#338: with <see cref="MdlRenderKind"/> zero, a null here refuses the draw.</summary>
    public static readonly int MdlRenderGeom = Same("MdlNodeTriMesh", "renderGeom", 0x278);
    /// <summary>#338: the short that picks which of the two gate branches runs.</summary>
    public static readonly int MdlRenderKind = Same("MdlNodeTriMesh", "renderKind", 0x284);
    /// <summary>The pool token holding a dangly mesh's REST positions.</summary>
    public static readonly int MdlRestPool = Same("MdlNodeTriMesh", "restPool", 0x280);
    /// <summary>Vertex count beside <see cref="MdlRestPool"/>.</summary>
    public static readonly int MdlVertexCount = Same("MdlNodeTriMesh", "vertexCount", 0x284);

    /// <summary>#284: the MdlNode's <c>MdlNodeTriMesh_PostProcess*</c> -- the TEMPORARY load-time data
    /// <c>PartSkin::PostProcess</c> @0x483120 reads to build the bind arrays. Null with a zero bone count
    /// means the temporaries are GONE and no number of calls can ever prepare the node.</summary>
    public static readonly int SkinTempArrays = Same("MdlNodeSkin", "tempArrays", 0x278);
    /// <summary>#262: bone count on a <c>MdlNodeSkin</c>, a SHORT (zero = not prepared yet).</summary>
    public static readonly int SkinBoneCount = Same("MdlNodeSkin", "boneCount", 0x284);
    /// <summary>#262: data pointer of the inverse bind-pose <c>CExoArrayList&lt;Quaternion&gt;</c>, indexed by
    /// GATHERED index. Written by <c>PartSkin::PostProcess</c> @0x483120. MdlNodeSkin yielded one validated
    /// function pair, which reached 0x278/0x284 and nothing higher.</summary>
    public static readonly int SkinBindRot = Unresolved("MdlNodeSkin", "bindRot", 0x2f0);
    /// <summary>#262: the <c>&lt;Vector&gt;</c> half of <see cref="SkinBindRot"/>.</summary>
    public static readonly int SkinBindPos = Unresolved("MdlNodeSkin", "bindPos", 0x300);
    /// <summary>#262: the 64-entry <c>short</c> map from bone slot to gathered index. No Windows MdlNodeSkin
    /// function touches this offset at all, which is a positive hint that it moved.</summary>
    public static readonly int SkinBoneMap = Unresolved("MdlNodeSkin", "boneMap", 0x320);

    /// <summary>The dangly solve's own parameters, from <c>MdlNodeDanglyMesh::InternalParseField</c>
    /// @0x44da00: the per-vertex constraint array and its count, then displacement, tightness and period.
    /// MdlNodeDanglyMesh pairs only as far as 0x284; nothing above it is measured on Windows.</summary>
    public static readonly int MdlConstraints = Unresolved("MdlNodeDanglyMesh", "constraints", 0x2c8);
    /// <inheritdoc cref="MdlConstraints"/>
    public static readonly int MdlConstraintCount = Unresolved("MdlNodeDanglyMesh", "constraintCount", 0x2d0);
    /// <inheritdoc cref="MdlConstraints"/>
    public static readonly int MdlDisplacement = Unresolved("MdlNodeDanglyMesh", "displacement", 0x2d8);
    /// <inheritdoc cref="MdlConstraints"/>
    public static readonly int MdlTightness = Unresolved("MdlNodeDanglyMesh", "tightness", 0x2dc);
    /// <inheritdoc cref="MdlConstraints"/>
    public static readonly int MdlPeriod = Unresolved("MdlNodeDanglyMesh", "period", 0x2e0);

    // ---- Part (identical 0x0..0x58) ----

    /// <summary>#262: the part's own LOCAL transform, which the engine's animation step writes and every
    /// bone solve composes: <c>Vector</c> position at +0x10, <c>Quaternion</c> orientation at +0x1c stored
    /// (w,x,y,z). <c>Part::GetWorld</c> and <c>Part::GetLocal</c> touch the same eight offsets on both
    /// builds -- the compilers only order them differently, which is why the strict pairing dropped them.</summary>
    public static readonly int PartPos = Same("Part", "pos", 0x10);
    /// <inheritdoc cref="PartPos"/>
    public static readonly int PartRot = Same("Part", "rot", 0x1c);
    /// <summary>#256: Part -&gt; child array (items +0x38, count +0x40). Written by
    /// <c>CreateInstanceTreeR</c> @0x43c6b0 and freed by <c>Part::~Part</c> @0x437720.</summary>
    public static readonly int PartChildren = Same("Part", "children", 0x38);
    /// <inheritdoc cref="PartChildren"/>
    public static readonly int PartChildCount = Same("Part", "childCount", 0x40);
    /// <summary>The parent back-pointer <c>SkinPose</c> walks UP as well as down.</summary>
    public static readonly int PartParent = Same("Part", "parent", 0x48);
    /// <summary>Part -&gt; Gob* (the instance the part belongs to; null for world geometry).</summary>
    public static readonly int PartGob = Same("Part", "gob", 0x50);
    /// <summary>Part -&gt; Material*. Ghidra RenderDrawBucket @0x46d060 / PartTriMesh::Draw @0x4513d0.</summary>
    public static readonly int PartMaterial = Same("Part", "material", 0x58);

    // ---- PartTriMesh (segmented: its own block starts at 0x64 and runs +0x4, then +0x8 from 0x88) ----
    // The constructor is the anchor: Linux zeroes 0x64,0x6c,0x74,0x7c,0x84,0x8c as six 8-byte stores
    // (0x64..0x93), Windows the same six at 0x68..0x97, so the block opens +0x4; above it every paired
    // read is +0x8 (0x88, 0x96, 0xa0, 0xb0, and the three bytes 0x94/0x95/0x96 -> 0x9c/0x9d/0x9e).

    /// <summary>#126/#336: the part's WORLD bbox, min at +0x64 and max at +0x70 (three floats each) -- what
    /// <c>Camera::GetCanKeyholeDissolve</c> @0x4a6f70 hands <c>BoxAbovePlane</c>, and the same pair
    /// SceneInputs/CasterPass/WaterBodies cull against.
    /// <para>WORLD space and TILE PARTS ONLY: <c>ComputePartBBox</c> @0x561370 is fed only from
    /// <c>ProcessStaticParts</c> @0x570970, and <c>PartTriMesh::PartTriMesh</c> @0x53cb50 zeroes the six
    /// floats, so a GOB part carries zeros here forever.</para>
    /// <para>Windows: the ctor block above, plus <c>PartTriMesh::WithinRange</c> reading the six floats at
    /// 0x68..0x7c where Linux reads 0x64..0x78. Two witnesses, and it agrees with #430's hand pairing.</para></summary>
    public static readonly int PartBboxMin = Diverges("PartTriMesh", "bboxMin", linux: 0x64, win: 0x68);
    /// <inheritdoc cref="PartBboxMin"/>
    public static readonly int PartBboxMax = Diverges("PartTriMesh", "bboxMax", linux: 0x70, win: 0x74);
    /// <summary>Part -&gt; its <c>VertexPrimitive</c>, and NOT a node despite the name (#134: the vtables
    /// prove it, and its +0x10 is the back-pointer to the part itself). Paired twice on PartTriMesh and once
    /// on PartSkin, all +0x8.</summary>
    public static readonly int PartNode = Diverges("PartTriMesh", "vertexPrimitive", linux: 0x88, win: 0x90);
    /// <summary>Part -&gt; TileData* (Ghidra CheckFadePlane @0x47bf30). The constructor's single 8-byte
    /// write, Linux 0x98 against Windows 0xa0.</summary>
    public static readonly int PartTile = Diverges("PartTriMesh", "tile", linux: 0x98, win: 0xa0);
    /// <summary>#338: <c>part[0x96]</c>, the merged-away marker every <c>Part::Draw</c> override and
    /// <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 check before drawing anything.</summary>
    public static readonly int PartRenderFlag = Diverges("PartTriMesh", "renderFlag", linux: 0x96, win: 0x9e);
    /// <summary>Part -&gt; its own alpha multiplier, the first term of <c>PartTriMesh::GetIsOpaque</c>
    /// @0x43cee0. Read live: it is animated state.</summary>
    public static readonly int PartAlpha = Diverges("PartTriMesh", "alpha", linux: 0xa0, win: 0xa8);
    /// <summary>Part -&gt; texture-rotation angle (degrees, float; Ghidra PartTriMesh::RotateTexture
    /// @0x44fff0) -- NWN's tile-texture 0/90/180/270 anti-repetition scheme.</summary>
    public static readonly int PartTexRotate = Diverges("PartTriMesh", "texRotate", linux: 0xb0, win: 0xb8);

    /// <summary>PartDanglyMesh's own mutable state: last frame's world pose (the quaternion is w FIRST).
    /// PartDanglyMesh yielded no validated function pair on either side, so neither field is measured on
    /// Windows -- and this is one of the classes where the +0x8 of its PartTriMesh base need not carry.</summary>
    public static readonly int PartPrevPos = Unresolved("PartDanglyMesh", "prevPos", 0xb4);
    /// <inheritdoc cref="PartPrevPos"/>
    public static readonly int PartPrevRot = Unresolved("PartDanglyMesh", "prevRot", 0xc0);

    // ---- PartLight (own block from 0x64 runs +0x4; 0x64/0x68/0x6c paired, the rest is that segment) ----

    /// <summary>PartLight RGB. The direction is <see cref="LightDir"/> and NOT this -- reading the other
    /// pair extrudes the static projections the wrong way.</summary>
    public static readonly int LightColour = Diverges("PartLight", "colour", linux: 0x64, win: 0x68);
    /// <summary>The MDL light node's <c>radius</c> controller (Ghidra <c>MdlNodeLight::InternalParseField</c>
    /// @0x44baf0). Directionals (sun/moon) carry ~100000 and points ~10, so it is also the discriminator.</summary>
    public static readonly int LightRange = Diverges("PartLight", "range", linux: 0x70, win: 0x74);
    /// <summary>Engine shadow-caster flag.</summary>
    public static readonly int LightCastShadows = Diverges("PartLight", "castShadows", linux: 0x74, win: 0x78);
    /// <summary>The <c>shadowradius</c> controller beside <see cref="LightRange"/>
    /// (<c>PartLight::PartLight</c> @0x43d1d0 defaults it to -1). Shadow-only, diagnostics here.</summary>
    public static readonly int LightRangeAlt = Diverges("PartLight", "rangeAlt", linux: 0x78, win: 0x7c);
    /// <summary>Light kind: 0 = point/directional, and a discriminant that must be zero for the walk.</summary>
    public static readonly int LightKind = Diverges("PartLight", "kind", linux: 0x80, win: 0x84);
    /// <summary>Enable flags -- either set means active.</summary>
    public static readonly int LightFlagA = Diverges("PartLight", "flagA", linux: 0x84, win: 0x88);
    /// <inheritdoc cref="LightFlagA"/>
    public static readonly int LightFlagB = Diverges("PartLight", "flagB", linux: 0x88, win: 0x8c);
    /// <summary>Engine shadow-fade weight (<c>GetShadowLights</c> decrements it per frame for deprioritized
    /// lights; <c>LightManager::ControlLightsFade</c> @0x4c2050 scales the area terms by it).</summary>
    public static readonly int LightFade = Diverges("PartLight", "fade", linux: 0x90, win: 0x94);
    /// <summary>Intensity; the lit radius is <c>range * intensity</c> (GetLightAdjustedRadius).</summary>
    public static readonly int LightIntensity = Diverges("PartLight", "intensity", linux: 0xa4, win: 0xa8);
    /// <summary>World position, and the direction <c>BSPCreateStaticProjections</c> bakes against.</summary>
    public static readonly int LightDir = Diverges("PartLight", "pos", linux: 0xac, win: 0xb0);

    // ---- LightManager (identical 0x0..0xc0) ----

    /// <summary>The MASTER <c>CExoArrayList&lt;PartLight*&gt;</c> -- every registered light in the loaded
    /// area, creation order (stable). Ghidra AddLight / GetShadowLights /
    /// SetupLightsForThisPositionAndRadius.</summary>
    public static readonly int LmMasterData = Same("LightManager", "masterData", 0x10);
    /// <inheritdoc cref="LmMasterData"/>
    public static readonly int LmMasterCount = Same("LightManager", "masterCount", 0x18);
    /// <summary><c>LightManager::ControlLightsFade</c> @0x4c2050: +0xa0 is the area AMBIENT PartLight*,
    /// +0x98 the area DIFFUSE (the sun/moon) -- the two it pushes with index -1.</summary>
    public static readonly int LmAreaDiffuse = Same("LightManager", "areaDiffuse", 0x98);
    /// <inheritdoc cref="LmAreaDiffuse"/>
    public static readonly int LmAreaAmbient = Same("LightManager", "areaAmbient", 0xa0);

    // ---- Camera (Gob base region delta 0; own block 0x4c4..0x4e4 +0x4, then +0x8) ----

    /// <summary>Camera position and orientation. Linux carries two overloads of each --
    /// <c>GetPosition</c> @0x4813f0 reads +0xb0 and @0x481420 reads +0xa8, <c>GetOrientation</c> @0x4aa610
    /// reads +0xbc and @0x4aa640 reads +0xb4 -- and Windows folds each pair into one function that reads
    /// the SAME offsets this side does (+0xb0 as 8+4 bytes, +0xbc as 8+8). So these are measured on
    /// Camera's own accessors; they are NOT Gob's position/orientation, which sit 8 bytes lower.</summary>
    public static readonly int CamPos = Same("Camera", "pos", 0xb0);
    /// <inheritdoc cref="CamPos"/>
    public static readonly int CamRot = Same("Camera", "rot", 0xbc);
    /// <summary>CAurCamera -&gt; current behavior host, a <c>CAurBehavior*</c>; its
    /// <see cref="BehaviorAsFollowSlot"/> is <c>AsBehaviorCameraFollow()</c>. Paired three times at delta 0,
    /// and the highest field in the base region that is.</summary>
    public static readonly int CameraBehaviorHost = Same("Camera", "behaviorHost", 0x458);
    /// <summary>Camera::GetViewAngle @0x434b10.</summary>
    public static readonly int CamViewAngle = Diverges("Camera", "viewAngle", linux: 0x4c4, win: 0x4c8);
    /// <summary>Camera::GetClipDist @0x4aa700 -- near and far.</summary>
    public static readonly int CamNear = Diverges("Camera", "near", linux: 0x4cc, win: 0x4d0);
    /// <inheritdoc cref="CamNear"/>
    public static readonly int CamFar = Diverges("Camera", "far", linux: 0x4d0, win: 0x4d4);
    /// <summary>Camera::SetViewPort @0x4a38a0 -- width and height. #420 derived the viewport's
    /// <c>0x4d4 -&gt; 0x4d8</c> by hand inside this same +0x4 segment.</summary>
    public static readonly int CamViewW = Diverges("Camera", "viewW", linux: 0x4dc, win: 0x4e0);
    /// <inheritdoc cref="CamViewW"/>
    public static readonly int CamViewH = Diverges("Camera", "viewH", linux: 0x4e0, win: 0x4e4);
    /// <summary>#330: every camera field <c>Camera::GetCanKeyholeDissolve</c> @0x4a6f70 reads -- the enable
    /// byte, the aperture plane at +0x4..+0x10 and the height cut at +0x14 -- as one 24-byte span.
    /// <c>Camera::RenderScene</c> @0x4a7920 is their only writer, every frame, ahead of the FadeDoors anchor,
    /// so a snapshot compare at the anchor is a camera-dirty bit with no hook. Both ends of the span are
    /// paired (0xe50 and the height cut 0xe64, both +0x8).</summary>
    public static readonly int CamKeyholeState = Diverges("Camera", "keyholeState", linux: 0xe50, win: 0xe58);

    // ---- Material / SharedMaterial ----

    /// <summary>#126: the material's blend pair -- source factor at +0x90, destination at +0x94, both
    /// <c>Aurora::AuroraBlendModes</c> (Ghidra <c>Material::SetBlendingMode</c> @0x4c5c50). <c>dst == 2</c>
    /// is <c>GL_ONE</c>, the engine's own additive test.</summary>
    public static readonly int MatSrcBlend = Same("Material", "srcBlend", 0x90);
    /// <inheritdoc cref="MatSrcBlend"/>
    public static readonly int MatDstBlend = Same("Material", "dstBlend", 0x94);
    /// <summary>The blend-disable flag <c>RenderFlat</c> reads (Ghidra GetTexture/GetEnvMapTID
    /// @0x4c5810/0x4c5c40).</summary>
    public static readonly int MatBlendFlag = Same("Material", "blendFlag", 0x98);
    /// <summary>The material's own env map (<c>Material::HasEnvMap</c> @0x4c6180 tests exactly this field).
    /// NOT one of the numbered texture slots, so <c>GetTexture</c> never reaches it. Compared only, never
    /// dereferenced: a <c>CAurTexture*</c> is borrowed.</summary>
    public static readonly int MatEnvMap = Same("Material", "envMap", 0x40);
    /// <summary>Material -&gt; SharedMaterial* override.</summary>
    public static readonly int MatShared = Same("Material", "shared", 0xd0);

    // SharedMaterial is the one class here where the evidence points AT divergence without resolving it:
    // Windows SharedMaterial functions touch 0x190/0x1a1/0x1b2 -- the 0x11 resref stride, +0x8 from Linux's
    // 0x188/0x199/0x1aa -- while 0x1c0/0x1c8/0x1e8 pair at delta 0 and Linux's 0x1f1 has no Windows
    // counterpart at all. A +0x8 name block under delta-0 neighbours is not a segment anyone has explained,
    // so nothing here is resolved on a guess.

    /// <summary>#123/#125: the <c>customShaderVS</c>/<c>FS</c> resrefs the .mtr named, as inline 17-byte
    /// NUL-terminated buffers (<c>SharedMaterial::ParseField</c> @0x4c6760 parses VS/FS/GS to +0x188/+0x199/
    /// +0x1aa, hence the 0x11 stride, and <c>SharedMaterial::Init</c> @0x4c6ff0 hands those same three
    /// addresses to <c>GetCustomShaderProgram</c>).
    /// <para>This is what a material is CLASSIFIED by, not the compiled programs it caches at +0x1c0 (the
    /// discard-capable build) and +0x1c8 (the NO_DISCARD, Early-Z-friendly one <c>PartTriMesh::Mat</c>
    /// @0x450960 picks for an opaque part that cannot dissolve): those stay null until <c>Init</c> runs,
    /// whereas the names are parsed the moment the .mtr loads. An empty VS name is the real "pure texture
    /// redirect" test -- such a shared material adds no program at all.</para></summary>
    public static readonly int SharedVsName = Unresolved("SharedMaterial", "vsName", 0x188);
    /// <inheritdoc cref="SharedVsName"/>
    public static readonly int SharedFsName = Unresolved("SharedMaterial", "fsName", 0x199);
    /// <summary>#126: the three <c>.mtr</c> keywords that steer bucket membership
    /// (<c>SharedMaterial::ParseField</c> @0x4c6760): <c>transparency</c>, <c>sample_framebuffer</c>
    /// (1 = bucket 5, 2 = water) and <c>volumetric</c>.</summary>
    public static readonly int SharedTransparency = Unresolved("SharedMaterial", "transparency", 0x1f0);
    /// <inheritdoc cref="SharedTransparency"/>
    public static readonly int SharedSampleFb = Unresolved("SharedMaterial", "sampleFb", 0x1f4);
    /// <inheritdoc cref="SharedTransparency"/>
    public static readonly int SharedVolumetric = Unresolved("SharedMaterial", "volumetric", 0x1f8);
    /// <summary>#292: the <c>twosided</c> keyword, parsed by the same <c>SharedMaterial::ParseField</c>
    /// @0x4c6760 as a bool -- the byte immediately after <c>transparency</c>, not a second field of it. This,
    /// and NOT <c>MdlNodeEmitter+0x164</c>, is what decides face culling on the world path.
    /// <para>The engine spends it at BUCKET-FILL time, not at draw time.
    /// <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 files each part in one of three sub-lists --
    /// <c>sub = twosided ? (volumetric ? 0 : 1) : 0</c> -- and <c>Scene::RenderDrawBucket</c> @0x46d060
    /// draws sub-list 1 under <c>Disable(3)</c> (cap 3 = GL_CULL_FACE), sub-list 2 under
    /// <c>Enable(3) + SetCullFace(0)</c>, and leaves <c>Enable(3) + SetCullFace(1)</c> behind on every exit.
    /// So the vanilla default is CULLING ON, back faces, and two states is the whole vocabulary here.</para></summary>
    public static readonly int SharedTwoSided = Unresolved("SharedMaterial", "twoSided", 0x1f1);

    /// <summary>CAurTextureBasic's 32-byte name field (Ghidra CAurTextureBasic::CAurTextureBasic @0x487250:
    /// <c>strncpy(this + 0x88, name, 0x1f)</c>). #430 read all thirteen of the class's functions by hand:
    /// every offset 0x28..0xec agrees, and the Windows-only 0x90/0xa0 are MSVC splitting Linux's 16-byte SSE
    /// stores, not layout.</summary>
    public static readonly int TexBasicName = Same("CAurTextureBasic", "name", 0x88);

    // ---- VertexDataObject (0x8 delta 0; 0x14..0x44 +0x4; 0x48 and up back to delta 0) ----

    /// <summary>#284: the byte <c>VertexDataObjectBase</c>'s ctor zeroes and <c>VertexDataObject::Build</c>
    /// @0x45bac0 sets to 1 for modes 1 and 2. <c>GetElementArrayBuffer</c> @0x45b320 returns 0 while it is
    /// clear, which sends <c>GLRender::BindBuffer</c> @0x4d04a0 down its unbind branch mid-build.</summary>
    public static readonly int VdoIndexed = Same("VertexDataObject", "indexed", 0x8);
    /// <summary>The VAO id. It sits between the two measured segments and no paired Linux function touches
    /// it, so its Windows home is unmeasured.</summary>
    public static readonly int VdoVao = Unresolved("VertexDataObject", "vao", 0xc);
    /// <summary>VDO mode (1 = indexed static).</summary>
    public static readonly int VdoMode = Diverges("VertexDataObject", "mode", linux: 0x14, win: 0x18);
    /// <summary>#284: the three pool tokens whose GL names the lazy VAO build captures, and the attribute
    /// masks beside two of them -- the loop @0x4d0028..0x4d0427 runs twice, over (+0x24, mask +0x28) and
    /// then (+0x30, mask +0x34). An absent token reads <see cref="VdoNoPool"/> and the engine skips it.</summary>
    public static readonly int VdoElemToken = Diverges("VertexDataObject", "elemToken", linux: 0x18, win: 0x1c);
    /// <inheritdoc cref="VdoElemToken"/>
    public static readonly int VdoVertToken = Diverges("VertexDataObject", "vertToken", linux: 0x24, win: 0x28);
    /// <inheritdoc cref="VdoElemToken"/>
    public static readonly int VdoVertMask = Diverges("VertexDataObject", "vertMask", linux: 0x28, win: 0x2c);
    /// <inheritdoc cref="VdoElemToken"/>
    public static readonly int VdoDynToken = Diverges("VertexDataObject", "dynToken", linux: 0x30, win: 0x34);
    /// <inheritdoc cref="VdoElemToken"/>
    public static readonly int VdoDynMask = Diverges("VertexDataObject", "dynMask", linux: 0x34, win: 0x38);
    /// <summary>The vertex item table {offset,size} and its count (Ghidra AddItem / RenderVertexData /
    /// SetVertexDataObject). #430's demonstration class: Windows spends four bytes low in the object and
    /// gets them back here, so the pointer blocks land at the same offsets on both.</summary>
    public static readonly int VdoVertItems = Same("VertexDataObject", "vertItems", 0x48);
    /// <inheritdoc cref="VdoVertItems"/>
    public static readonly int VdoVertCount = Same("VertexDataObject", "vertCount", 0x50);
    /// <summary>The element item table and its count.</summary>
    public static readonly int VdoElemItems = Same("VertexDataObject", "elemItems", 0x58);
    /// <inheritdoc cref="VdoElemItems"/>
    public static readonly int VdoElemCount = Same("VertexDataObject", "elemCount", 0x60);

    // ---- classes with no validated function pair on either side ----

    /// <summary>#256: the draw arguments as FIELDS -- <c>RenderFlat</c> @0x450eb0 calls
    /// <c>RenderVertexData(*(vp+0x58), *(uint*)(vp+0x60), ...)</c>, and every draw path reaches it. A part
    /// whose geometry has never been uploaded reads a null VDO and an item of -1: not an error, "not
    /// resident yet". VertexPrimitive decompiles to no validated pair on either build.</summary>
    public static readonly int VpVdo = Unresolved("VertexPrimitive", "vdo", 0x58);
    /// <inheritdoc cref="VpVdo"/>
    public static readonly int VpItem = Unresolved("VertexPrimitive", "item", 0x60);
    /// <summary>VertexPrimitive vtable slot 5 -- returns the <c>VertexPrimitiveDangly</c> view whose
    /// <see cref="VpdVelocity"/> is the velocity array, NOT the VertexPrimitive itself (verified at
    /// 0x49bd85: the engine calls this and indexes +0xa0 off the RESULT). Both tables printed by
    /// <c>tools/abi/vtable-slots.py VertexPrimitive</c>: -8, like the other eight.</summary>
    public static readonly int VpAsDanglySlot = Diverges("VertexPrimitive", "vtbl.AsDangly", linux: 0x28, win: 0x20, Expect.VtblSlot);
    /// <summary>The engine's own per-vertex velocity array, which the dangly solve integrates in place.</summary>
    public static readonly int VpdVelocity = Unresolved("VertexPrimitiveDangly", "velocity", 0xa0);

    /// <summary>#256: the BSP node's inventory pointer, and inside it the
    /// <c>CExoArrayList&lt;PartTriMesh*&gt;</c> of tile geometry (items +0x20, count +0x28).
    /// <c>SceneBSPAddPart</c> @0x47ca10 files a plain <c>PartTriMesh</c> at exactly
    /// <c>*(BSPFitBox(...)+0x70) + 0x20</c>. BSPNode has too few functions to pair on either build.</summary>
    public static readonly int BspNodeData = Unresolved("BSPNode", "data", 0x70);
    /// <inheritdoc cref="BspNodeData"/>
    public static readonly int BspDataTriMesh = Unresolved("BSPNode", "triMesh", 0x20);
    /// <inheritdoc cref="BspNodeData"/>
    public static readonly int BspDataTriMeshCount = Unresolved("BSPNode", "triMeshCount", 0x28);

    /// <summary>The live tile-fade factor the <c>HandleFadingIn/OutBucket</c> step writes and the top of
    /// <c>ManageSceneBSP</c> @0x47fa80 commits (#330), and the pending factor beside it. TileData pairs on
    /// neither build, and Windows touches 0x64/0x6c/0x74 where Linux touches 0x78.</summary>
    public static readonly int TileFade = Unresolved("TileData", "fade", 0x70);
    /// <inheritdoc cref="TileFade"/>
    public static readonly int TilePendingFade = Unresolved("TileData", "pendingFade", 0x74);

    /// <summary>The 48 <c>DrawBucket</c> records at <see cref="SceneDrawBuckets"/> are 0x10 apart --
    /// <c>GetDrawBucketByItemType</c> @0x47bd60's <c>(type*3 + sub)*0x10</c> stride. A record SIZE, which
    /// MSVC is free to disagree about exactly like a field offset.</summary>
    public static readonly int BucketStride = Unresolved("DrawBucket", "stride", 0x10);

    /// <summary>CNWCModule -&gt; the player's current CNWCArea* (the engine's own
    /// HandleServerToPlayerArea chain). No <c>CNWC*</c> class is name-exported on Windows, so this needs
    /// #420's RVA path rather than the class sweep.</summary>
    public static readonly int ModuleCurrentArea = Unresolved("CNWCModule", "currentArea", 0xf8);
    /// <summary>CNWCArea -&gt; the ARE Flags DWORD, bit 0 = interior (<c>CNWSArea::PackAreaIntoMessage</c>
    /// writes <c>m_nFlags</c>@+8 -&gt; <c>CNWCArea::LoadArea</c> reads it into +8). RVA path, as above.</summary>
    public static readonly int AreaFlags = Unresolved("CNWCArea", "flags", 0x8);
    /// <summary>CAurBehaviorCameraFollow -&gt; <c>SafePointer&lt;CAurObject&gt;</c> to the followed object
    /// (the PC); raw pointer at +0. Its <see cref="GobPosition"/> is what GetTargetWorldPosition returns.</summary>
    public static readonly int BehaviorTargetObj = Unresolved("CAurBehaviorCameraFollow", "target", 0x20);

    /// <summary>What to add to a vtable SYMBOL to reach slot 0. Itanium puts offset-to-top and the typeinfo
    /// pointer in front of the function pointers, so <c>_ZTV&lt;class&gt;</c> is two words short of the
    /// table; MSVC's <c>??_7&lt;class&gt;@@6B@</c> already points at slot 0. Measured by construction in
    /// <c>tools/abi/vtable-slots.py</c>, which is how #433's nine slots were read on both sides.</summary>
    public static readonly int VtblSymbolHeader = Diverges("(abi)", "vtblSymbolHeader", linux: 0x10, win: 0x0);

    // ponytail: the rest of the Alloy offsets land here one subsystem at a time (#429 section 2). A second tested
    // client build gets its own pinned key plus its own values below; today there is exactly one per OS.

    // ================= engine constants that are not offsets =================
    // They rode into this table with #436 because the file they lived in was the offset file. They are
    // magic VALUES the engine compares against, not places -- no row, nothing to resolve per platform.

    /// <summary>The engine's "no such pool" token, tested at every one of the three VDO token fields
    /// (<c>cmp $0xffffffff,%eax</c> @0x4d003a) -- an absent token is skipped, not an error.</summary>
    public const uint VdoNoPool = 0xFFFFFFFF;

    /// <summary>Range that tells a directional (sun/moon) light from a placed one: directionals carry
    /// ~100000, points ~10. Read off <see cref="LightRange"/> by everything that has to tell them apart.</summary>
    public const float DirectionalRange = 1000f;

    // ================= build key =================

    /// <summary>The Linux client this table was measured against: <c>.note.gnu.build-id</c> of
    /// <c>nwmain-linux</c>. Build identity from the linker, so a rewrapped Steam/GOG copy of the same build
    /// is the same key -- unlike a content hash.</summary>
    private const string LinuxBuildId = "3e2500a82e0c32008d24c3e74bbff3021da5e1b7";

    /// <summary>The Windows client: CodeView PDB GUID + age from <c>nwmain.exe</c>'s debug directory.
    /// Same build revision as <see cref="LinuxBuildId"/> (<c>bin/*/build.txt</c>), which is the precondition
    /// for any pairing being meaningful at all.</summary>
    private const string WindowsBuildId = "29431F05E0F2461A8BECCFC9E80D8F271";

    /// <summary>The running client's build key, whatever it is -- logged even on mismatch, so the stand-down
    /// line can name what was actually loaded.</summary>
    public static string BuildKey => _key ??= OperatingSystem.IsWindows() ? WindowsKey() : LinuxKey();

    /// <summary>Does the running client match the build these offsets were measured against?</summary>
    public static bool KeyOk => string.Equals(BuildKey, Expected, StringComparison.OrdinalIgnoreCase);

    private static string Expected => OperatingSystem.IsWindows() ? WindowsBuildId : LinuxBuildId;

    /// <summary>Offsets still carrying no Windows resolution. Zero is the section-3 finish line. Counted on
    /// BOTH platforms -- the migration is worked on Linux, and a counter that only moves on Windows is not
    /// visible where the work happens. Only the <see cref="Require"/> gate is Windows-only.</summary>
    public static int UnresolvedCount { get; private set; }

    /// <summary>
    /// Unit gate, the <see cref="SymbolMap.Require"/> pattern for layout rather than symbols: a feature that
    /// reads engine memory calls this at the top of <c>Initialize</c> and stands down whole if the answer is
    /// no. Reading a field at an offset measured on a different build is how a port renders garbage instead
    /// of failing.
    /// </summary>
    /// <param name="classes">The engine classes this feature reads. Empty means "every row", which is only
    /// right for a feature that reads the whole table -- #436: once Alloy's offsets landed here, an
    /// unmeasured <c>PartTriMesh</c> field would otherwise have stood the nameplates down on Windows.</param>
    public static bool Require(ExtenderLog log, string feature, params string[] classes)
    {
        if (!KeyOk)
        {
            log.Always($"{feature}: standing down as a unit -- client build {BuildKey} != {Expected} " +
                       "(rot: re-measure, docs/engine-layout.md)");
            return false;
        }
        int unresolved = classes.Length == 0 ? UnresolvedCount : UnresolvedIn(classes);
        if (OperatingSystem.IsWindows() && unresolved > 0)
        {
            log.Always($"{feature}: standing down as a unit -- {unresolved} engine offset(s) have no " +
                       "Windows resolution (docs/engine-layout.md)");
            return false;
        }
        return true;
    }

    /// <summary>Join <c>/knobs</c>. Once, from the host.</summary>
    public static void Register() => KnobSurface.Note(() =>
        $"engine layout: {_rows.Count} offset(s), {UnresolvedCount} unresolved; " +
        $"build {BuildKey}{(KeyOk ? "" : $" != expected {Expected} -- ROT")}");

    // ================= probes =================

    /// <summary>What a resolved offset can assert about live memory before anything renders off it.</summary>
    private enum Expect
    {
        /// <summary>Nothing this offset can cheaply claim.</summary>
        None,
        /// <summary>A boolean field: the dword is 0 or 1. A wrong offset lands in a pointer or a float.</summary>
        Byte01,
        /// <summary>A vtable slot: <c>*(*obj + off)</c> is a code address inside the client image.</summary>
        VtblSlot,
    }

    /// <summary>
    /// Validate this class's resolved offsets against a live instance, once, and stand the caller down
    /// loudly rather than let it render off a wrong offset. Call at the first sighting of a pointer of that
    /// class, behind the caller's own one-shot latch -- a name lookup does not belong in a frame loop.
    /// </summary>
    /// <returns>false if any probe failed -- the caller should treat that as rot.</returns>
    public static bool Probe(string cls, IntPtr obj, ExtenderLog? log)
    {
        if (obj == IntPtr.Zero) return true;
        _ = BuildKey;   // the key readers are also what fill the image range VtblSlot compares against
        bool ok = true;
        for (int i = 0; i < _rows.Count; i++)
        {
            Row r = _rows[i];
            if (r.Class != cls || r.Expect == Expect.None) continue;
            if (Holds(r, obj)) continue;
            log?.Always($"layout: probe FAILED -- {r.Class}.{r.Field} at +0x{r.Value:x} " +
                        $"(build {BuildKey}); re-measure, docs/engine-layout.md");
            ok = false;
        }
        return ok;
    }

    private static bool Holds(Row r, IntPtr obj)
    {
        switch (r.Expect)
        {
            case Expect.Byte01:
                int v = *(int*)(obj + r.Value);
                return v == 0 || v == 1;
            case Expect.VtblSlot:
                IntPtr vtbl = *(IntPtr*)obj;
                if (vtbl == IntPtr.Zero) return true;   // no instance to judge, not a failed probe
                IntPtr fn = *(IntPtr*)(vtbl + r.Value);
                return (ulong)fn >= _imageLo && (ulong)fn < _imageHi;
            default:
                return true;
        }
    }

    // ================= plumbing =================

    private readonly record struct Row(string Class, string Field, int Value, Expect Expect, bool Unresolved);

    private static string? _key;

    private static int Diverges(string cls, string field, int linux, int win, Expect probe = Expect.None)
        => Add(cls, field, OperatingSystem.IsWindows() ? win : linux, probe, false);

    private static int Same(string cls, string field, int off, Expect probe = Expect.None)
        => Add(cls, field, off, probe, false);

    /// <summary>A Linux value whose Windows counterpart nobody has measured. Usable on Linux; on Windows
    /// <see cref="Require"/> refuses while any exist, so it can never reach a read there.</summary>
    private static int Unresolved(string cls, string field, int linux, Expect probe = Expect.None)
    {
        UnresolvedCount++;
        return Add(cls, field, linux, probe, true);
    }

    private static int Add(string cls, string field, int value, Expect probe, bool unresolved)
    {
        _rows.Add(new Row(cls, field, value, probe, unresolved));
        return value;
    }

    /// <summary>Unresolved rows belonging to these engine classes. The gate is per feature, so the counter
    /// it reads has to be too: a renderer class nobody has measured must not stand the nameplates down.</summary>
    private static int UnresolvedIn(string[] classes)
    {
        int n = 0;
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].Unresolved && Array.IndexOf(classes, _rows[i].Class) >= 0) n++;
        return n;
    }

    // ---- build key readers ----

    private static readonly IntPtr Base = NativeLibrary.GetMainProgramHandle();
    private static ulong _imageLo, _imageHi;

    /// <summary>PE CodeView GUID + age, read from the loaded image: e_lfanew -> DataDirectory[6] ->
    /// IMAGE_DEBUG_DIRECTORY entries -> the type-2 (CODEVIEW) one -> "RSDS" + GUID + age.</summary>
    private static string WindowsKey()
    {
        byte* b = (byte*)Base;
        int lfanew = *(int*)(b + 0x3c);
        byte* opt = b + lfanew + 24;
        _imageLo = (ulong)Base;
        _imageHi = _imageLo + *(uint*)(opt + 56);            // SizeOfImage (PE32+)
        uint dbgRva = *(uint*)(opt + 112 + 6 * 8), dbgSize = *(uint*)(opt + 112 + 6 * 8 + 4);
        for (uint i = 0; i + 28 <= dbgSize; i += 28)
        {
            byte* e = b + dbgRva + i;
            if (*(uint*)(e + 12) != 2) continue;             // IMAGE_DEBUG_TYPE_CODEVIEW
            byte* cv = b + *(uint*)(e + 20);                 // AddressOfRawData
            if (cv[0] != 'R' || cv[1] != 'S' || cv[2] != 'D' || cv[3] != 'S') continue;
            var sb = new StringBuilder(33);
            sb.Append((*(uint*)(cv + 4)).ToString("X8"))
              .Append((*(ushort*)(cv + 8)).ToString("X4"))
              .Append((*(ushort*)(cv + 10)).ToString("X4"));
            for (int k = 12; k < 20; k++) sb.Append(cv[k].ToString("X2"));
            sb.Append(*(uint*)(cv + 20));                    // age
            return sb.ToString();
        }
        return "no-codeview";
    }

    /// <summary>ELF NT_GNU_BUILD_ID of the running program. Read from <c>/proc/self/exe</c> -- under
    /// LD_PRELOAD that is the client, and <see cref="Base"/> is a dlopen handle rather than a load address,
    /// so the in-memory shortcut the Windows reader takes is not available here.</summary>
    private static string LinuxKey()
    {
        try
        {
            ImageRangeFromMaps();
            using var fs = File.OpenRead("/proc/self/exe");
            var hdr = new byte[64];
            if (fs.Read(hdr, 0, 64) != 64) return "no-elf-header";
            long phoff = BitConverter.ToInt64(hdr, 0x20);
            int phentsize = BitConverter.ToUInt16(hdr, 0x36), phnum = BitConverter.ToUInt16(hdr, 0x38);
            var ph = new byte[phentsize];
            for (int i = 0; i < phnum; i++)
            {
                fs.Position = phoff + (long)i * phentsize;
                if (fs.Read(ph, 0, phentsize) != phentsize) break;
                if (BitConverter.ToUInt32(ph, 0) != 4) continue;                 // PT_NOTE
                long off = BitConverter.ToInt64(ph, 0x08), size = BitConverter.ToInt64(ph, 0x20);
                var notes = new byte[size];
                fs.Position = off;
                if (fs.Read(notes, 0, notes.Length) != notes.Length) continue;
                for (int p = 0; p + 12 <= notes.Length; )
                {
                    int namesz = BitConverter.ToInt32(notes, p);
                    int descsz = BitConverter.ToInt32(notes, p + 4);
                    int type = BitConverter.ToInt32(notes, p + 8);
                    int name = p + 12, desc = name + ((namesz + 3) & ~3);
                    if (type == 3 && namesz == 4 && notes[name] == 'G' && notes[name + 1] == 'N')
                        return Convert.ToHexString(notes, desc, descsz).ToLowerInvariant();
                    p = desc + ((descsz + 3) & ~3);
                }
            }
            return "no-build-id";
        }
        catch (Exception e) { return "unreadable:" + e.GetType().Name; }
    }

    /// <summary>Image extent of the main program, for the vtable-slot probe. From <c>/proc/self/maps</c>:
    /// the lines whose path is the exe.</summary>
    private static void ImageRangeFromMaps()
    {
        string exe = File.ResolveLinkTarget("/proc/self/exe", true)?.FullName ?? "";
        if (exe.Length == 0) return;
        foreach (string line in File.ReadLines("/proc/self/maps"))
        {
            if (!line.EndsWith(exe, StringComparison.Ordinal)) continue;
            int dash = line.IndexOf('-'), sp = line.IndexOf(' ');
            if (dash < 0 || sp < dash) continue;
            ulong lo = Convert.ToUInt64(line[..dash], 16), hi = Convert.ToUInt64(line[(dash + 1)..sp], 16);
            if (_imageLo == 0 || lo < _imageLo) _imageLo = lo;
            if (hi > _imageHi) _imageHi = hi;
        }
    }

    /// <summary>
    /// #432: the one runnable check. Proves the three forms register and count, that the build key reads,
    /// and -- the part that matters -- that a deliberately wrong offset makes a probe FAIL rather than pass
    /// quietly. Off-device: the probe runs against a hand-built fake object, not the client.
    /// </summary>
    public static void SelfCheck(ExtenderLog log)
    {
        int before = _rows.Count, unresolvedBefore = UnresolvedCount;
        int same = Same("SelfCheck", "same", 0x10);
        int div = Diverges("SelfCheck", "div", linux: 0x20, win: 0x24);
        int unk = Unresolved("SelfCheck", "unk", 0x30);
        bool win = OperatingSystem.IsWindows();
        if (same != 0x10 || div != (win ? 0x24 : 0x20) || unk != 0x30) { log.Always("layout selfcheck FAIL: resolution"); return; }
        if (_rows.Count != before + 3) { log.Always("layout selfcheck FAIL: registration"); return; }
        if (UnresolvedCount != unresolvedBefore + 1) { log.Always("layout selfcheck FAIL: counter"); return; }

        // A probe that cannot fail is not a probe: +0x40 holds 2, which no boolean does.
        long* fake = stackalloc long[16];
        for (int i = 0; i < 16; i++) fake[i] = 0;
        *(int*)((byte*)fake + 0x38) = 1;
        *(int*)((byte*)fake + 0x40) = 2;
        _rows.Add(new Row("SelfCheckProbe", "good", 0x38, Expect.Byte01, false));
        if (!Probe("SelfCheckProbe", (IntPtr)fake, null)) { log.Always("layout selfcheck FAIL: good probe rejected"); return; }
        _rows.Add(new Row("SelfCheckProbe", "bad", 0x40, Expect.Byte01, false));
        if (Probe("SelfCheckProbe", (IntPtr)fake, null)) { log.Always("layout selfcheck FAIL: bad probe accepted"); return; }

        _rows.RemoveRange(before, _rows.Count - before);
        UnresolvedCount = unresolvedBefore;
        log.Info($"layout selfcheck ok: {_rows.Count} offset(s), {UnresolvedCount} unresolved, build {BuildKey}");
    }
}
