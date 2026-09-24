using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CX.Framework;

/// <summary>
/// Every engine-memory constant the extender reads -- struct field offsets and vtable slots -- in one
/// table, keyed by (os, build). <see cref="SymbolMap"/> answers "where is this function"; this answers
/// "where is this field", which the Windows port (#429) made a per-platform question.
/// </summary>
/// <remarks>
/// <para>Code declares, data measures: a row here is a name, an engine class, a field and a probe;
/// its value per OS comes from the running build's file (<see cref="ClientBuilds"/>,
/// <c>Framework/Builds/*.tsv</c>). "Measured identical", "differs" and "Windows never looked" (a <c>-</c>)
/// are facts about a build, so they live with it; the last is the migration's completion counter.</para>
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
    // Also ahead of the table: a row asks ClientBuilds for its value, which reads the build key off this.
    private static readonly IntPtr Base = NativeLibrary.GetMainProgramHandle();

    // ================= the table =================
    // Each row names the engine class it belongs to: that is the key #431's Ghidra extractor pairs on,
    // and the unit #430 measures (a class diverges in 2-3 contiguous segments, anchored on a function pair).

    /// <summary>CNWCObject + 0x0c: object type byte -- 5 creature, 6 item, 9 placeable, 0xa door.</summary>
    public static readonly int ObjType = Field("CNWCObject", "type");
    /// <summary>CNWCObject -&gt; its object id (uint). Every sender passes <c>obj+8</c> as the OID on both builds
    /// (<c>PerformDefaultActionOnCreature</c>'s Attack / Dialog / UseObject sends, <c>HandleLMouseDown</c>'s

    public static readonly int ObjId = Field("CNWCObject", "id");

    /// <summary>CNWCCreature: "the feedback plate is currently shown for me", set at the top of
    /// ShowFeedbackBubbleText (Linux disasm @0x71a81e, Windows @0x80b240).</summary>
    public static readonly int CreaturePlated = Field("CNWCCreature", "plated", Expect.Byte01);
    /// <summary>CNWCCreature -&gt; CNWCCreatureStats* (docs/re/nameplate.md §7.1; Windows via <c>ShowFeedbackBubbleText</c>
    /// @0x80b240, docs/windows-symbols.md).</summary>
    public static readonly int CreatureStats = Field("CNWCCreature", "stats");
    /// <summary>CNWCCreatureStats first / last name <c>char*</c> + length pairs: <c>GetFullName</c> (Linux nm 0x74db80, Windows
    /// rva 0x7f3f00) reads them the same on both builds; the +0x40/+0x48 override (SetName, a disguise) is not these.</summary>
    public static readonly int StatsFirstName = Field("CNWCCreatureStats", "firstName");
    public static readonly int StatsFirstNameLen = Field("CNWCCreatureStats", "firstNameLen");
    public static readonly int StatsLastName = Field("CNWCCreatureStats", "lastName");
    public static readonly int StatsLastNameLen = Field("CNWCCreatureStats", "lastNameLen");

    /// <summary>CNWCPlaceable, same flag (Linux disasm @0x7ea8b8, Windows @0x871b60).</summary>
    public static readonly int PlaceablePlated = Field("CNWCPlaceable", "plated", Expect.Byte01);

    /// <summary>Virtual slot of <c>GetPortrait(unsigned char)</c> on CNWCObject -- MSVC orders the table
    /// differently, so this is a vtable index, not a field (#420).</summary>
    public static readonly int GetPortraitSlot = Field("CNWCObject", "vtbl.GetPortrait", Expect.VtblSlot);

    // ---- vtable slots Alloy calls through (#433) ----
    // Every one is -8 on Windows and for one reason: Itanium gives a virtual destructor TWO slots
    // (complete + deleting), MSVC gives it one, so everything declared after it shifts by a pointer.
    // Measured, not assumed -- tools/abi/vtable-slots.py prints both tables side by side.

    /// <summary>Part::AsPartTriMesh() -- the type filter SkyBox/BucketGate/GrassField run a part through.</summary>
    public static readonly int PartAsTriMeshSlot = Field("Part", "vtbl.AsPartTriMesh", Expect.VtblSlot);

    /// <summary>Part::AsPartBeam() -- same filter for the beam overlays (#130).</summary>
    public static readonly int PartAsPartBeamSlot = Field("Part", "vtbl.AsPartBeam", Expect.VtblSlot);

    /// <summary>Part::PostProcess(Part*).</summary>
    public static readonly int PartPostProcessSlot = Field("Part", "vtbl.PostProcess", Expect.VtblSlot);

    /// <summary>CAurTexture::AsCAurTextureBasic().</summary>
    public static readonly int TexAsBasicSlot = Field("CAurTexture", "vtbl.AsCAurTextureBasic", Expect.VtblSlot);

    /// <summary>CAurTexture::GetReferencedTexture() -- the indirection every tid read goes through first.</summary>
    public static readonly int TexReferencedSlot = Field("CAurTexture", "vtbl.GetReferencedTexture", Expect.VtblSlot);

    /// <summary>VertexPrimitive::SetupArrays() -- virtual on purpose: Skinned/Anim/Dangly each override it.</summary>
    public static readonly int VpSetupArraysSlot = Field("VertexPrimitive", "vtbl.SetupArrays", Expect.VtblSlot);

    /// <summary>CAurBehavior::AsBehaviorCameraFollow() -- the camera-follow type filter (#65).</summary>
    public static readonly int BehaviorAsFollowSlot = Field("CAurBehavior", "vtbl.AsBehaviorCameraFollow", Expect.VtblSlot);

    /// <summary>CycleTIDTextureController::Control() -- one animated-texture tick.</summary>
    public static readonly int CycleControlSlot = Field("CycleTIDTextureController", "vtbl.Control", Expect.VtblSlot);

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
    public static readonly int SceneGobs = Field("Scene", "gobs");
    /// <inheritdoc cref="SceneGobs"/>
    public static readonly int SceneGobCount = Field("Scene", "gobCount");

    /// <summary>Scene -&gt; BSPNode* root (the static-geometry BSP). Linux-verified in ManageSceneBSP.</summary>
    public static readonly int SceneBspRoot = Field("Scene", "bspRoot");

    /// <summary>The fog block <c>Scene::DoFog</c> @0x45f1b0 pushes into <c>GLRender::m_fog</c>: enable,
    /// start, end, and the colour <c>Scene::SetFogColor(Vector&amp;)</c> @0x45e220 writes. #222: the colour is
    /// also what <c>PartTriMesh::Mat</c> @0x450960 paints a scene-tinted gob in.</summary>
    public static readonly int SceneFogEnable = Field("Scene", "fogEnable");
    /// <inheritdoc cref="SceneFogEnable"/>
    public static readonly int SceneFogStart = Field("Scene", "fogStart");
    /// <inheritdoc cref="SceneFogEnable"/>
    public static readonly int SceneFogEnd = Field("Scene", "fogEnd");
    /// <inheritdoc cref="SceneFogEnable"/>
    public static readonly int SceneFogColor = Field("Scene", "fogColor");

    /// <summary>Scene -&gt; LightManager* (<c>Scene::RenderSinglePass</c> @0x46e5f0 calls
    /// <c>GetShadowLights(*(this + 0x158))</c>).</summary>
    public static readonly int SceneLightManager = Field("Scene", "lightManager");

    /// <summary>Scene -&gt; the embedded <c>DrawBucketManager</c> (0x310 bytes, constructed in place by
    /// <c>Scene::Scene</c> @0x4724f0; every <c>AddPartToBucket</c> call passes <c>this+0x208</c>).
    /// <para>#448: <c>Scene::Scene</c> (@0x4724f0 / rva 0xc0ca0) constructs it in place at <c>this+0x208</c>
    /// on BOTH builds, and the ctor's store run 0x178..0x1f8 below it is store-for-store identical -- so the
    /// region #443 called unmeasured is measured, and Scene does not diverge here.</para></summary>
    public static readonly int SceneDrawBuckets = Field("Scene", "drawBuckets");

    /// <summary>Scene vtable slot 81 -- <c>Scene::IsAnimating</c> @0x45e000. Vanilla gates the whole dangly
    /// solve on it, which is what stops cloaks swaying behind a paused menu. Both tables printed by
    /// <c>tools/abi/vtable-slots.py Scene</c>, and the -8 is the virtual-dtor rule #433 measured.</summary>
    public static readonly int SceneIsAnimatingSlot = Field("Scene", "vtbl.IsAnimating", Expect.VtblSlot);

    // ---- Gob (identical 0x0..0x4b8: 28 offsets paired over 50 functions, no conflict) ----

    /// <summary>Gob fields that change how a part shades, all read by <c>PartTriMesh::Mat</c> @0x450960
    /// except the env override, which <c>PartTriMesh::Draw</c> @0x4513d0 reads to reroute the whole draw to
    /// <c>RenderEnvironmentMapped</c>. The two flags ARM a modulation; the values beside them are the
    /// modulation, and <c>gob+0xfc</c> is the colour whose 12 bytes end exactly where the alpha begins.</summary>
    public static readonly int GobHighlight = Field("Gob", "highlight");
    /// <summary>#222: the gob byte that says a part is painted in the scene's fog colour.
    /// <c>PartTriMesh::Mat</c> @0x450960 takes a branch nothing in the Alloy pass had: with this set and a
    /// live scene it calls <c>SetColor4f(fog.r, fog.g, fog.b, 1.0)</c> and draws the part in the FOG COLOUR
    /// at full alpha. The skyfade gob is that part.</summary>
    public static readonly int GobSceneTint = Field("Gob", "sceneTint");
    /// <summary>Gob -&gt; node pointer, and the same field the fade owner is read through. MUST be non-null
    /// before appending to the gobbucket: the sort comparator <c>FUN_0055e070</c> blindly derefs
    /// <c>*(gob+0x68)+0x8e</c> (the render path null-checks it, the sort does not).</summary>
    public static readonly int GobNode = Field("Gob", "node");
    /// <summary>Gob -&gt; Part* part-tree root. <c>ProcessGob</c>/<c>AddPartsTreeToDrawBuckets</c> walk it;
    /// guard non-null so a geometry-less gob is skipped, not dereferenced.</summary>
    public static readonly int GobPartTree = Field("Gob", "partTree");
    /// <summary>#256: the two LOD-alternate part-tree roots beside <see cref="GobPartTree"/>.
    /// <c>Gob::SetLODModel</c> @0x43e6a0 ROTATES the three against each other every frame, per visible gob,
    /// with no construction, no destruction and no epoch bump.</summary>
    public static readonly int GobLodRoot1 = Field("Gob", "lodRoot1");
    /// <inheritdoc cref="GobLodRoot1"/>
    public static readonly int GobLodRoot2 = Field("Gob", "lodRoot2");
    /// <summary>Gob -&gt; world position (float x@+0xa8, y@+0xac, z@+0xb0).</summary>
    public static readonly int GobPosition = Field("Gob", "position");
    /// <inheritdoc cref="GobHighlight"/>
    public static readonly int GobColourMul = Field("Gob", "colourMul");
    /// <inheritdoc cref="GobHighlight"/>
    public static readonly int GobSelfIllum = Field("Gob", "selfIllum");
    /// <inheritdoc cref="GobHighlight"/>
    public static readonly int GobColour = Field("Gob", "colour");
    /// <inheritdoc cref="GobHighlight"/>
    public static readonly int GobAlpha = Field("Gob", "alpha");
    /// <inheritdoc cref="GobHighlight"/>
    public static readonly int GobFadeAlpha = Field("Gob", "fadeAlpha");
    /// <inheritdoc cref="GobHighlight"/>
    public static readonly int GobEmissive = Field("Gob", "emissive");
    /// <summary>#256: Gob -&gt; <c>CExoArrayList&lt;Part*&gt;</c> of the roots this gob APPENDED at runtime
    /// (items +0x130, count +0x138), parallel to the <c>Model*</c> array at +0x120. <c>Gob::Append</c>
    /// @0x4422a0 pushes here; <c>Gob::Amputate</c> @0x443fa0 removes.</summary>
    public static readonly int GobAppendRoots = Field("Gob", "appendRoots");
    /// <inheritdoc cref="GobAppendRoots"/>
    public static readonly int GobAppendCount = Field("Gob", "appendCount");
    /// <summary>Gob -&gt; the <c>TileData*</c> of the tile it IS, or null for every other gob.
    /// <c>ProcessStaticObject</c> @0x5619e0 stores the tile here (and the gob back at <c>tile+0x40</c>);
    /// <c>Gob::Gob</c> @0x540a00 zeroes it and only tile code ever writes it, so a non-null field is a
    /// positive "this gob is tileset geometry, not an object".</summary>
    public static readonly int GobTileData = Field("Gob", "tileData");
    /// <summary>#128: the per-gob environment-map override flag <c>PartTriMesh::Draw</c> @0x4513d0 reads.</summary>
    public static readonly int GobEnvOverride = Field("Gob", "envOverride");

    // ---- MdlNode and its subclasses ----

    /// <summary>The byte on whatever <c>gob+0x68</c> points at, whose bit 3 arms a second alpha multiply by
    /// <see cref="GobFadeAlpha"/> -- Ghidra <c>PartTriMesh::Mat</c>:
    /// <c>if ((*(*(gob+0x68)+0x8e) &amp; 8) != 0)</c>.
    /// <para>#448 settled both halves. The class is <c>Model</c>: <c>Model::Model</c> (@0x43a850 / rva
    /// 0x87bc0) writes this byte inside its own store run, immediately below the reference count that
    /// <c>Model::AddRef</c> reads (Linux 0x90, Windows 0x94). The offset is read directly on both builds by
    /// <c>PartTriMesh::Mat</c> (@0x450960 / rva 0xa2f50) as the same <c>&amp; 8</c> test on
    /// <c>gob+0x68</c>.</para></summary>
    public static readonly int NodeOwnerFlags = Field("Model", "ownerFlags");
    /// <summary>#262: MdlNode -&gt; its parent MdlNode. <c>GatherBones</c> @0x482b10 admits a child part only
    /// when this matches the walked part's own node, which is what keeps the traversal INDEX equal to the
    /// engine's.</summary>
    public static readonly int MdlNodeParent = Field("MdlNode", "parent");
    /// <summary>MdlNode -&gt; the node name (<c>char[]</c>) <c>FindPart</c> @0x43c5b0 strcasecmp-s through
    /// <see cref="PartNodeLink"/>. #397 forensics only.</summary>
    public static readonly int MdlNodeName = Field("MdlNode", "name");
    /// <summary>The class-kind bits <c>MdlNode::AsMdlNodeTriMesh</c> tests: the cast succeeds iff
    /// <c>(byte &amp; 0x21) == 0x21</c>. The whole function is that one test on both builds -- Linux
    /// @0x43adf0 reads it as a dword, Windows (rva 0x8c360) as a byte, same offset, same mask.</summary>
    public static readonly int MdlNodeKind = Field("MdlNode", "kind");
    /// <summary><see cref="MdlNodeKind"/>'s mask AND its expected value -- both bits must be set.</summary>
    public const int MdlNodeKindTriMesh = 0x21;
    /// <summary>#659: MdlNode -&gt; its <c>NewController</c> array (data +0x78, count +0x80, stride
    /// <see cref="NewControllerStride"/>). <c>MdlNode::InternalControl</c> @0x4990c0 is the loop, byte-for-byte
    /// the same on both builds. NOTE: +0x80 is the same word <see cref="NodeFlags"/> reads as a matrix gate.</summary>
    public static readonly int MdlNodeControllers = Field("MdlNode", "controllers");
    /// <inheritdoc cref="MdlNodeControllers"/>
    public static readonly int MdlNodeControllerCount = Field("MdlNode", "controllerCount");
    /// <summary>#659: a NewController record is 0xc bytes: int target-field id (the raw Part byte offset the
    /// controller writes, e.g. <see cref="LightRange"/>) at +0, short key count at +4
    /// (<c>AddGenericControllerSingle</c> @0x4486f0 hard-codes 1 for a plain <c>radius</c>/<c>color</c> line).</summary>
    public const int NewControllerStride = 0xc;
    /// <inheritdoc cref="NewControllerStride"/>
    public static readonly int NewControllerKeyCount = Field("NewController", "keyCount");
    /// <summary>MdlNode -&gt; model matrix (row-major float[16]). RenderDrawBucket @0x46d060 passes exactly
    /// that address to <c>RenderInterface::SetModelMatrix</c>. #448: <c>Scene::RenderDrawBucket</c>
    /// (@0x46d060 / rva 0xcd210) passes <c>node+0x18</c> to <c>SetModelMatrix</c> under the same
    /// <c>node+0x80 &amp; 0x1e</c> gate on both builds, and <c>MdlNode::MdlNode</c> (@0x43aab0 / rva 0x87290)
    /// has an identical 0x0..0x98 store run.</summary>
    public static readonly int NodeMatrix = Field("MdlNode", "matrix");
    /// <summary>MdlNode -&gt; matrix flags; only a matrix-class part (<c>node+0x80 &amp; 0x1e</c>) gets its
    /// matrix pushed.</summary>
    public static readonly int NodeFlags = Field("MdlNode", "flags");
    /// <summary>#256: the primitive-type index, argument 3 of the engine's own <c>RenderVertexData</c> call.
    /// Read out of <c>RenderFlat</c> (@0x450eb0 / rva 0xa4710) as <c>node+0x270</c> on both builds -- Ghidra
    /// even types the Windows read <c>AuroraPrimitiveTypes*</c> (#448).</summary>
    public static readonly int MdlPrimType = Field("MdlNode", "primType");

    /// <summary>#338: the render gate <c>PartTriMesh::IsPartRenderable</c> @0x450e60 reads off the part's
    /// node -- zero here refuses the draw outright.</summary>
    public static readonly int MdlRenderGate = Field("MdlNodeTriMesh", "renderGate");
    /// <summary>#338: with <see cref="MdlRenderKind"/> zero, a null here refuses the draw.</summary>
    public static readonly int MdlRenderGeom = Field("MdlNodeTriMesh", "renderGeom");
    /// <summary>#338: the short that picks which of the two gate branches runs.</summary>
    public static readonly int MdlRenderKind = Field("MdlNodeTriMesh", "renderKind");
    /// <summary>The pool token holding a dangly mesh's REST positions.</summary>
    public static readonly int MdlRestPool = Field("MdlNodeTriMesh", "restPool");
    /// <summary>Vertex count beside <see cref="MdlRestPool"/>.</summary>
    public static readonly int MdlVertexCount = Field("MdlNodeTriMesh", "vertexCount");
    /// <summary>The per-group index counts (<c>int[]</c>) and how many groups there are. With
    /// <see cref="MdlIndexCount"/> and <see cref="MdlVertexCount"/> these are the ONLY inputs to
    /// <c>VertexPrimitiveFlat::SetupArrays</c>' no-geometry exit, which is why they are read at all
    /// (<see cref="MdlIndexCount"/> carries the anchor).</summary>
    public static readonly int MdlIndexGroupCounts = Field("MdlNodeTriMesh", "indexGroupCounts");
    /// <inheritdoc cref="MdlIndexGroupCounts"/>
    public static readonly int MdlIndexGroups = Field("MdlNodeTriMesh", "indexGroups");
    /// <summary>The ungrouped index count, the last term of the index total. Anchored on the function pair
    /// <c>VertexPrimitiveFlat::SetupArrays</c> (Linux @0x458920, Windows
    /// <c>?SetupArrays@VertexPrimitiveFlat@@UEAAXXZ</c> rva 0xabca0): its summing prefix reads
    /// <c>0x248</c>, <c>0x260</c>, <c>0x26c</c> and <c>0x284</c> at the same offsets on both builds.</summary>
    public static readonly int MdlIndexCount = Field("MdlNodeTriMesh", "indexCount");

    /// <summary>#284: the MdlNode's <c>MdlNodeTriMesh_PostProcess*</c> -- the TEMPORARY load-time data
    /// <c>PartSkin::PostProcess</c> @0x483120 reads to build the bind arrays. Null with a zero bone count
    /// means the temporaries are GONE and no number of calls can ever prepare the node.</summary>
    public static readonly int SkinTempArrays = Field("MdlNodeSkin", "tempArrays");
    /// <summary>#262: bone count on a <c>MdlNodeSkin</c>, a SHORT (zero = not prepared yet).</summary>
    public static readonly int SkinBoneCount = Field("MdlNodeSkin", "boneCount");
    /// <summary>#262: data pointer of the inverse bind-pose <c>CExoArrayList&lt;Quaternion&gt;</c>, indexed by
    /// GATHERED index. #448: <c>PartSkin::PostProcess</c> (@0x483120 / rva 0xdf860) adds to the
    /// <c>CExoArrayList&lt;Quaternion&gt;</c> at <c>node+0x2f0</c> and the <c>&lt;Vector&gt;</c> one at
    /// <c>node+0x300</c> on both builds, in the same gather loop.</summary>
    public static readonly int SkinBindRot = Field("MdlNodeSkin", "bindRot");
    /// <summary>#262: the <c>&lt;Vector&gt;</c> half of <see cref="SkinBindRot"/>.</summary>
    public static readonly int SkinBindPos = Field("MdlNodeSkin", "bindPos");
    /// <summary>#262: the 64-entry <c>short</c> map from bone slot to gathered index. #448: no MdlNodeSkin
    /// METHOD touches it on either build because the only reader is the free function
    /// <c>ExecuteSkinGPU</c> (@0x482800 / rva 0xdeb90), whose 64-iteration stride-2 walk starts at
    /// <c>node+800</c> on both.</summary>
    public static readonly int SkinBoneMap = Field("MdlNodeSkin", "boneMap");

    /// <summary>The dangly solve's own parameters, from <c>MdlNodeDanglyMesh::InternalParseField</c>
    /// (@0x44da00 / rva 0x99e20): the per-vertex constraint array and its count, then displacement,
    /// tightness and period. #448: the four parsed fields sit at the same offsets on both builds, each pinned
    /// by its own <c>.mdl</c> field-name string (<c>displacement</c> 0x2d8, <c>tightness</c> 0x2dc,
    /// <c>period</c> 0x2e0, <c>constraints</c> the <c>List&lt;float&gt;</c> at 0x2c8), and
    /// <c>PartDanglyMesh::Animate</c> (@0x49bbb0 / rva 0x4ef50) reads all four back at those offsets.
    /// <see cref="MdlConstraintCount"/> is that list's own count: <c>List&lt;T&gt;</c> is
    /// <c>{data +0, count +8}</c> on both builds, measured on a <c>List</c> the decompiler TYPES as one --
    /// <c>ExecuteSkinGPU</c>'s <c>List&lt;Vector&gt;*</c> parameter, whose count it reads at <c>+8</c> on each
    /// build.</summary>
    public static readonly int MdlConstraints = Field("MdlNodeDanglyMesh", "constraints");
    /// <inheritdoc cref="MdlConstraints"/>
    public static readonly int MdlConstraintCount = Field("MdlNodeDanglyMesh", "constraintCount");
    /// <inheritdoc cref="MdlConstraints"/>
    public static readonly int MdlDisplacement = Field("MdlNodeDanglyMesh", "displacement");
    /// <inheritdoc cref="MdlConstraints"/>
    public static readonly int MdlTightness = Field("MdlNodeDanglyMesh", "tightness");
    /// <inheritdoc cref="MdlConstraints"/>
    public static readonly int MdlPeriod = Field("MdlNodeDanglyMesh", "period");

    // ---- Part (identical 0x0..0x58) ----

    /// <summary>#262: the part's own LOCAL transform, which the engine's animation step writes and every
    /// bone solve composes: <c>Vector</c> position at +0x10, <c>Quaternion</c> orientation at +0x1c stored
    /// (w,x,y,z). <c>Part::GetWorld</c> and <c>Part::GetLocal</c> touch the same eight offsets on both
    /// builds -- the compilers only order them differently, which is why the strict pairing dropped them.</summary>
    public static readonly int PartPos = Field("Part", "pos");
    /// <inheritdoc cref="PartPos"/>
    public static readonly int PartRot = Field("Part", "rot");
    /// <summary>#256: Part -&gt; child array (items +0x38, count +0x40). Written by
    /// <c>CreateInstanceTreeR</c> @0x43c6b0 and freed by <c>Part::~Part</c> @0x437720.</summary>
    public static readonly int PartChildren = Field("Part", "children");
    /// <inheritdoc cref="PartChildren"/>
    public static readonly int PartChildCount = Field("Part", "childCount");
    /// <summary>The parent back-pointer <c>SkinPose</c> walks UP as well as down.</summary>
    public static readonly int PartParent = Field("Part", "parent");
    /// <summary>Part -&gt; Gob* (the instance the part belongs to; null for world geometry).</summary>
    public static readonly int PartGob = Field("Part", "gob");
    /// <summary>Part -&gt; Material*. Ghidra RenderDrawBucket @0x46d060 / PartTriMesh::Draw @0x4513d0.</summary>
    public static readonly int PartMaterial = Field("Part", "material");

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
    public static readonly int PartBboxMin = Field("PartTriMesh", "bboxMin");
    /// <inheritdoc cref="PartBboxMin"/>
    public static readonly int PartBboxMax = Field("PartTriMesh", "bboxMax");
    /// <summary>Part -&gt; its <c>VertexPrimitive</c>, and NOT a node despite the name (#134: the vtables
    /// prove it, and its +0x10 is the back-pointer to the part itself). Paired twice on PartTriMesh and once
    /// on PartSkin, all +0x8.</summary>
    public static readonly int PartNode = Field("PartTriMesh", "vertexPrimitive");
    /// <summary>Part -&gt; TileData* (Ghidra CheckFadePlane @0x47bf30). The constructor's single 8-byte
    /// write, Linux 0x98 against Windows 0xa0.</summary>
    public static readonly int PartTile = Field("PartTriMesh", "tile");
    /// <summary>#338: <c>part[0x96]</c>, the merged-away marker every <c>Part::Draw</c> override and
    /// <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 check before drawing anything.</summary>
    public static readonly int PartRenderFlag = Field("PartTriMesh", "renderFlag");
    /// <summary>Part -&gt; its own alpha multiplier, the first term of <c>PartTriMesh::GetIsOpaque</c>
    /// @0x43cee0. Read live: it is animated state.</summary>
    public static readonly int PartAlpha = Field("PartTriMesh", "alpha");
    /// <summary>Part -&gt; texture-rotation angle (degrees, float; Ghidra PartTriMesh::RotateTexture
    /// @0x44fff0) -- NWN's tile-texture 0/90/180/270 anti-repetition scheme.</summary>
    public static readonly int PartTexRotate = Field("PartTriMesh", "texRotate");

    /// <summary>PartDanglyMesh's own mutable state: last frame's world pose (the quaternion is w FIRST).
    /// <para>#448: <c>PartDanglyMesh::Animate</c> (@0x49bbb0 / rva 0x4ef50) is the pair -- it reads the three
    /// position floats, tests the quaternion's x/y/z against zero and stores both back, identically shaped on
    /// each build, at +0xb4/+0xc0 on Linux and +0xc0/+0xcc on Windows. The class's own fields shift +0xc, not
    /// the +0x8 of its PartTriMesh base.</para></summary>
    public static readonly int PartPrevPos = Field("PartDanglyMesh", "prevPos");
    /// <inheritdoc cref="PartPrevPos"/>
    public static readonly int PartPrevRot = Field("PartDanglyMesh", "prevRot");

    // ---- PartLight (own block from 0x64 runs +0x4; 0x64/0x68/0x6c paired, the rest is that segment) ----

    /// <summary>PartLight RGB. The direction is <see cref="LightDir"/> and NOT this -- reading the other
    /// pair extrudes the static projections the wrong way.</summary>
    public static readonly int LightColour = Field("PartLight", "colour");
    /// <summary>The MDL light node's <c>radius</c> controller (Ghidra <c>MdlNodeLight::InternalParseField</c>
    /// @0x44baf0). Directionals (sun/moon) carry ~100000 and points ~10, so it is also the discriminator.</summary>
    public static readonly int LightRange = Field("PartLight", "range");
    /// <summary>Engine shadow-caster flag.</summary>
    public static readonly int LightCastShadows = Field("PartLight", "castShadows");
    /// <summary>The <c>shadowradius</c> controller beside <see cref="LightRange"/>
    /// (<c>PartLight::PartLight</c> @0x43d1d0 defaults it to -1). Shadow-only, diagnostics here.</summary>
    public static readonly int LightRangeAlt = Field("PartLight", "rangeAlt");
    /// <summary>Light kind: 0 = point/directional, and a discriminant that must be zero for the walk.</summary>
    public static readonly int LightKind = Field("PartLight", "kind");
    /// <summary>Enable flags -- either set means active.</summary>
    public static readonly int LightFlagA = Field("PartLight", "flagA");
    /// <inheritdoc cref="LightFlagA"/>
    public static readonly int LightFlagB = Field("PartLight", "flagB");
    /// <summary>Engine shadow-fade weight (<c>GetShadowLights</c> decrements it per frame for deprioritized
    /// lights; <c>LightManager::ControlLightsFade</c> @0x4c2050 scales the area terms by it).</summary>
    public static readonly int LightFade = Field("PartLight", "fade");
    /// <summary>Intensity; the lit radius is <c>range * intensity</c> (GetLightAdjustedRadius).</summary>
    public static readonly int LightIntensity = Field("PartLight", "intensity");
    /// <summary>World position, and the direction <c>BSPCreateStaticProjections</c> bakes against.</summary>
    public static readonly int LightDir = Field("PartLight", "pos");

    // ---- LightManager (identical 0x0..0xc0) ----

    /// <summary>The MASTER <c>CExoArrayList&lt;PartLight*&gt;</c> -- every registered light in the loaded
    /// area, creation order (stable). Ghidra AddLight / GetShadowLights /
    /// SetupLightsForThisPositionAndRadius.</summary>
    public static readonly int LmMasterData = Field("LightManager", "masterData");
    /// <inheritdoc cref="LmMasterData"/>
    public static readonly int LmMasterCount = Field("LightManager", "masterCount");
    /// <summary><c>LightManager::ControlLightsFade</c> @0x4c2050: +0xa0 is the area AMBIENT PartLight*,
    /// +0x98 the area DIFFUSE (the sun/moon) -- the two it pushes with index -1.</summary>
    public static readonly int LmAreaDiffuse = Field("LightManager", "areaDiffuse");
    /// <inheritdoc cref="LmAreaDiffuse"/>
    public static readonly int LmAreaAmbient = Field("LightManager", "areaAmbient");

    // ---- Camera (Gob base region delta 0; own block 0x4c4..0x4e4 +0x4, then +0x8) ----

    /// <summary>Camera position and orientation. Linux carries two overloads of each --
    /// <c>GetPosition</c> @0x4813f0 reads +0xb0 and @0x481420 reads +0xa8, <c>GetOrientation</c> @0x4aa610
    /// reads +0xbc and @0x4aa640 reads +0xb4 -- and Windows folds each pair into one function that reads
    /// the SAME offsets this side does (+0xb0 as 8+4 bytes, +0xbc as 8+8). So these are measured on
    /// Camera's own accessors; they are NOT Gob's position/orientation, which sit 8 bytes lower.</summary>
    public static readonly int CamPos = Field("Camera", "pos");
    /// <inheritdoc cref="CamPos"/>
    public static readonly int CamRot = Field("Camera", "rot");
    /// <summary>CAurCamera -&gt; current behavior host, a <c>CAurBehavior*</c>; its
    /// <see cref="BehaviorAsFollowSlot"/> is <c>AsBehaviorCameraFollow()</c>. Paired three times at delta 0,
    /// and the highest field in the base region that is.</summary>
    public static readonly int CameraBehaviorHost = Field("Camera", "behaviorHost");
    /// <summary>Camera::GetViewAngle @0x434b10.</summary>
    public static readonly int CamViewAngle = Field("Camera", "viewAngle");
    /// <summary>Camera::GetClipDist @0x4aa700 -- near and far.</summary>
    public static readonly int CamNear = Field("Camera", "near");
    /// <inheritdoc cref="CamNear"/>
    public static readonly int CamFar = Field("Camera", "far");
    /// <summary>Camera::SetViewPort @0x4a38a0 -- width and height. #420 derived the viewport's
    /// <c>0x4d4 -&gt; 0x4d8</c> by hand inside this same +0x4 segment.</summary>
    public static readonly int CamViewW = Field("Camera", "viewW");
    /// <inheritdoc cref="CamViewW"/>
    public static readonly int CamViewH = Field("Camera", "viewH");
    /// <summary>#330: every camera field <c>Camera::GetCanKeyholeDissolve</c> @0x4a6f70 reads -- the enable
    /// byte, the aperture plane at +0x4..+0x10 and the height cut at +0x14 -- as one 24-byte span.
    /// <c>Camera::RenderScene</c> @0x4a7920 is their only writer, every frame, ahead of the FadeDoors anchor,
    /// so a snapshot compare at the anchor is a camera-dirty bit with no hook. Both ends of the span are
    /// paired (0xe50 and the height cut 0xe64, both +0x8).</summary>
    public static readonly int CamKeyholeState = Field("Camera", "keyholeState");

    // ---- Material / SharedMaterial ----

    /// <summary>#126: the material's blend pair -- source factor at +0x90, destination at +0x94, both
    /// <c>Aurora::AuroraBlendModes</c> (Ghidra <c>Material::SetBlendingMode</c> @0x4c5c50). <c>dst == 2</c>
    /// is <c>GL_ONE</c>, the engine's own additive test.</summary>
    public static readonly int MatSrcBlend = Field("Material", "srcBlend");
    /// <inheritdoc cref="MatSrcBlend"/>
    public static readonly int MatDstBlend = Field("Material", "dstBlend");
    /// <summary>The blend-disable flag <c>RenderFlat</c> reads (Ghidra GetTexture/GetEnvMapTID
    /// @0x4c5810/0x4c5c40).</summary>
    public static readonly int MatBlendFlag = Field("Material", "blendFlag");
    /// <summary>The material's own env map (<c>Material::HasEnvMap</c> @0x4c6180 tests exactly this field).
    /// NOT one of the numbered texture slots, so <c>GetTexture</c> never reaches it. Compared only, never
    /// dereferenced: a <c>CAurTexture*</c> is borrowed.</summary>
    public static readonly int MatEnvMap = Field("Material", "envMap");
    /// <summary>Material -&gt; SharedMaterial* override.</summary>
    public static readonly int MatShared = Field("Material", "shared");

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
    /// <para>#443: resolved. Both constructors write the three resref buffers at 0x188 / 0x199 / 0x1aa --
    /// #441 read Windows' extra stores at 0x190/0x1a1/0x1b2 as an unexplained +0x8 and rightly refused the
    /// whole class on it, but they are MSVC splitting each 17-byte copy into two 8-byte stores. With that
    /// explained, the constructor IS a pair, and it measures this row, <see cref="SharedTransparency"/>,
    /// <see cref="SharedTwoSided"/>, <see cref="SharedSampleFb"/> and <see cref="SharedVolumetric"/>
    /// together.</para>
    public static readonly int SharedVsName = Field("SharedMaterial", "vsName");
    /// <inheritdoc cref="SharedVsName"/>
    public static readonly int SharedFsName = Field("SharedMaterial", "fsName");
    /// <summary>#126: the three <c>.mtr</c> keywords that steer bucket membership
    /// (<c>SharedMaterial::ParseField</c> @0x4c6760): <c>transparency</c>, <c>sample_framebuffer</c>
    /// (1 = bucket 5, 2 = water) and <c>volumetric</c>.</summary>
    public static readonly int SharedTransparency = Field("SharedMaterial", "transparency");
    /// <inheritdoc cref="SharedTransparency"/>
    public static readonly int SharedSampleFb = Field("SharedMaterial", "sampleFb");
    /// <inheritdoc cref="SharedTransparency"/>
    public static readonly int SharedVolumetric = Field("SharedMaterial", "volumetric");
    /// <summary>#292: the <c>twosided</c> keyword, parsed by the same <c>SharedMaterial::ParseField</c>
    /// @0x4c6760 as a bool -- the byte immediately after <c>transparency</c>, not a second field of it. This,
    /// and NOT <c>MdlNodeEmitter+0x164</c>, is what decides face culling on the world path.
    /// <para>The engine spends it at BUCKET-FILL time, not at draw time.
    /// <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 files each part in one of three sub-lists --
    /// <c>sub = twosided ? (volumetric ? 0 : 1) : 0</c> -- and <c>Scene::RenderDrawBucket</c> @0x46d060
    /// draws sub-list 1 under <c>Disable(3)</c> (cap 3 = GL_CULL_FACE), sub-list 2 under
    /// <c>Enable(3) + SetCullFace(0)</c>, and leaves <c>Enable(3) + SetCullFace(1)</c> behind on every exit.
    /// So the vanilla default is CULLING ON, back faces, and two states is the whole vocabulary here.</para>
    /// <para>Windows covers this byte with the SAME 2-byte store that writes <see cref="SharedTransparency"/>
    /// at 0x1f0, which is the shape <c>CAurTextureBasic::Reset</c> shows at 0xe6/0xe7 -- a merged store over
    /// an adjacent pair, not one wider field.</para></summary>
    public static readonly int SharedTwoSided = Field("SharedMaterial", "twoSided");

    /// <summary>CAurTextureBasic's 32-byte name field (Ghidra CAurTextureBasic::CAurTextureBasic @0x487250:
    /// <c>strncpy(this + 0x88, name, 0x1f)</c>). #430 read all thirteen of the class's functions by hand:
    /// every offset 0x28..0xec agrees, and the Windows-only 0x90/0xa0 are MSVC splitting Linux's 16-byte SSE
    /// stores, not layout.</summary>
    public static readonly int TexBasicName = Field("CAurTextureBasic", "name");

    // ---- VertexDataObject (0x8 delta 0; 0x14..0x44 +0x4; 0x48 and up back to delta 0) ----

    /// <summary>#284: the byte <c>VertexDataObjectBase</c>'s ctor zeroes and <c>VertexDataObject::Build</c>
    /// @0x45bac0 sets to 1 for modes 1 and 2. <c>GetElementArrayBuffer</c> @0x45b320 returns 0 while it is
    /// clear, which sends <c>GLRender::BindBuffer</c> @0x4d04a0 down its unbind branch mid-build.</summary>
    public static readonly int VdoIndexed = Field("VertexDataObject", "indexed");
    /// <summary>The VAO id. #448: <c>GLRender::SetVertexDataObject</c> (@0x4cff30 / rva 0xba770) reads
    /// <c>vdo+0xc</c>, calls <c>glGenVertexArrays(1, &amp;it)</c> when it is zero, stores it back to
    /// <c>vdo+0xc</c> and binds it -- at the same offset on both builds. It is below the divergence boundary:
    /// everything from Linux 0x14 up is +4 on Windows (MSVC does not reuse <c>VertexDataObjectBase</c>'s tail
    /// padding), which is what the identical <c>3</c>/<c>-1</c> initializer pattern in the two constructors
    /// and the <c>GetElementArrayBuffer</c> pair both say.</summary>
    public static readonly int VdoVao = Field("VertexDataObject", "vao");
    /// <summary>VDO mode (1 = indexed static).</summary>
    public static readonly int VdoMode = Field("VertexDataObject", "mode");
    /// <summary>#284: the three pool tokens whose GL names the lazy VAO build captures, and the attribute
    /// masks beside two of them -- the loop @0x4d0028..0x4d0427 runs twice, over (+0x24, mask +0x28) and
    /// then (+0x30, mask +0x34). An absent token reads <see cref="VdoNoPool"/> and the engine skips it.</summary>
    public static readonly int VdoElemToken = Field("VertexDataObject", "elemToken");
    /// <inheritdoc cref="VdoElemToken"/>
    public static readonly int VdoVertToken = Field("VertexDataObject", "vertToken");
    /// <inheritdoc cref="VdoElemToken"/>
    public static readonly int VdoVertMask = Field("VertexDataObject", "vertMask");
    /// <inheritdoc cref="VdoElemToken"/>
    public static readonly int VdoDynToken = Field("VertexDataObject", "dynToken");
    /// <inheritdoc cref="VdoElemToken"/>
    public static readonly int VdoDynMask = Field("VertexDataObject", "dynMask");
    /// <summary>The vertex item table {offset,size} and its count (Ghidra AddItem / RenderVertexData /
    /// SetVertexDataObject). #430's demonstration class: Windows spends four bytes low in the object and
    /// gets them back here, so the pointer blocks land at the same offsets on both.</summary>
    public static readonly int VdoVertItems = Field("VertexDataObject", "vertItems");
    /// <inheritdoc cref="VdoVertItems"/>
    public static readonly int VdoVertCount = Field("VertexDataObject", "vertCount");
    /// <summary>The element item table and its count.</summary>
    public static readonly int VdoElemItems = Field("VertexDataObject", "elemItems");
    /// <inheritdoc cref="VdoElemItems"/>
    public static readonly int VdoElemCount = Field("VertexDataObject", "elemCount");

    // ---- classes with no validated function pair on either side ----

    /// <summary>#256: the draw arguments as FIELDS -- <c>RenderFlat</c> @0x450eb0 calls
    /// <c>RenderVertexData(*(vp+0x58), *(uint*)(vp+0x60), ...)</c>, and every draw path reaches it. A part
    /// whose geometry has never been uploaded reads a null VDO and an item of -1: not an error, "not
    /// resident yet". #448: <c>RenderFlat</c> (@0x450eb0 / rva 0xa4710) passes <c>vp+0x58</c> and
    /// <c>vp+0x60</c> as those same two arguments on both builds, and <c>PartDanglyMesh::Animate</c> reads
    /// the pair again at the same offsets -- the class needs no method of its own to be measured.</summary>
    public static readonly int VpVdo = Field("VertexPrimitive", "vdo");
    /// <inheritdoc cref="VpVdo"/>
    public static readonly int VpItem = Field("VertexPrimitive", "item");
    /// <summary>VertexPrimitive -&gt; the <c>MdlNodeTriMesh</c> it draws, and the <c>Part*</c> list of the
    /// siblings merged into it (<c>CombineParts</c>) with its count. <c>SetupArrays</c> sums its geometry
    /// totals over this node first and then over <c>*(list[i] + </c><see cref="PartNodeLink"/><c>)</c> for
    /// <c>i &lt; count</c>; a NEGATIVE count skips the walk outright and the totals stay zero. Same function
    /// pair as <see cref="MdlIndexCount"/>, same offsets on both builds.</summary>
    public static readonly int VpNode = Field("VertexPrimitive", "node");
    /// <inheritdoc cref="VpNode"/>
    public static readonly int VpCombineList = Field("VertexPrimitive", "combineList");
    /// <inheritdoc cref="VpNode"/>
    public static readonly int VpCombineCount = Field("VertexPrimitive", "combineCount");
    /// <summary>VertexPrimitive vtable slot 5 -- returns the <c>VertexPrimitiveDangly</c> view whose
    /// <see cref="VpdVelocity"/> is the velocity array, NOT the VertexPrimitive itself (verified at
    /// 0x49bd85: the engine calls this and indexes +0xa0 off the RESULT). Both tables printed by
    /// <c>tools/abi/vtable-slots.py VertexPrimitive</c>: -8, like the other eight.</summary>
    public static readonly int VpAsDanglySlot = Field("VertexPrimitive", "vtbl.AsDangly", Expect.VtblSlot);
    /// <summary>The engine's own per-vertex velocity array, which the dangly solve integrates in place.
    /// #448: <c>PartDanglyMesh::Animate</c> (@0x49bbb0 / rva 0x4ef50) null-checks <c>vpd+0xa0</c>, calls
    /// <c>GenerateVelocityArray</c> when it is null and then indexes it by vertex -- at 0xa0 on both.</summary>
    public static readonly int VpdVelocity = Field("VertexPrimitiveDangly", "velocity");

    /// <summary>#256: the BSP node's inventory pointer, and inside it the
    /// <c>CExoArrayList&lt;PartTriMesh*&gt;</c> of tile geometry (items +0x20, count +0x28).
    /// <c>SceneBSPAddPart</c> @0x47ca10 files a plain <c>PartTriMesh</c> at exactly
    /// <c>*(BSPFitBox(...)+0x70) + 0x20</c>. #448: <c>SceneBSPAddPart</c> (@0x47ca10 / rva 0xdcfd0) is a
    /// function pair on its whole body -- every one of its seven bucket writes goes through
    /// <c>node+0x70</c> at the same sub-offset on both builds, the <c>PartTriMesh</c> list at <c>+0x20</c>
    /// included. The count is the <c>CExoArrayList&lt;T&gt;</c>'s own, <c>{data +0, count +8}</c>, measured
    /// once per build on a container the decompiler types: Linux in <c>Scene::RenderLensFlares</c>, which
    /// walks <c>GetLensFlareLights</c>'s <c>CExoArrayList&lt;PartLight*&gt;</c> by <c>+8</c>; Windows in
    /// <c>PartSkin::PostProcess</c>, which inlines <c>CExoArrayList&lt;Vector&gt;::Allocate</c> on
    /// <c>node+0x300</c> and bumps that list's count at <c>node+0x308</c>.</summary>
    public static readonly int BspNodeData = Field("BSPNode", "data");
    /// <inheritdoc cref="BspNodeData"/>
    public static readonly int BspDataTriMesh = Field("BSPNode", "triMesh");
    /// <inheritdoc cref="BspNodeData"/>
    public static readonly int BspDataTriMeshCount = Field("BSPNode", "triMeshCount");

    /// <summary>The live tile-fade factor the <c>HandleFadingIn/OutBucket</c> step writes and the top of
    /// <c>ManageSceneBSP</c> (@0x47fa80 / rva 0xdb320) commits (#330), and the pending factor beside it.
    /// #448: that commit is one statement, <c>*(int*)(tile+0x70) = *(int*)(tile+0x74)</c>, inside the same
    /// loop over <c>scene+0x1a8</c> bounded by <c>scene+0x1b0</c> and beside the same <c>tile+0xc0 = 4</c>
    /// store -- identical on both builds. TileData needs no method of its own.</summary>
    public static readonly int TileFade = Field("TileData", "fade");
    /// <inheritdoc cref="TileFade"/>
    public static readonly int TilePendingFade = Field("TileData", "pendingFade");

    /// <summary>The 48 <c>DrawBucket</c> records at <see cref="SceneDrawBuckets"/> are 0x10 apart --
    /// <c>GetDrawBucketByItemType</c> (@0x47bd60 / rva 0xd94d0)'s <c>(type*3 + sub)*0x10</c> stride. A record
    /// SIZE, which MSVC is free to disagree about exactly like a field offset -- so #448 read it on the
    /// Windows caller rather than carrying it over: both that accessor and
    /// <c>DrawBucketManager::AddPartToBucket</c> (@0x47be30 / rva 0xd75a0) are one-line functions with the
    /// same <c>*0x10</c> on each build.</summary>
    public static readonly int BucketStride = Field("DrawBucket", "stride");

    /// <summary>CNWCModule -&gt; the player's current CNWCArea* (the engine's own
    /// HandleServerToPlayerArea chain). #446: <c>CNWCCreature::UpdatePopUpBubble</c> (Linux @0x72e600,
    /// Windows rva 0x8107a0) dereferences it and compares the area's id at +0x118 against the creature's
    /// own area id -- a pointer deref, not a position guess, and the same offset on both.</summary>
    public static readonly int ModuleCurrentArea = Field("CNWCModule", "currentArea");
    /// <summary>CNWCArea -&gt; the ARE Flags DWORD, bit 0 = interior (<c>CNWSArea::PackAreaIntoMessage</c>
    /// writes <c>m_nFlags</c>@+8 -&gt; <c>CNWCArea::LoadArea</c> reads it into +8). #446: the role is pinned
    /// by the WIRE ORDER, which no compiler may reorder -- <c>LoadArea</c> (Linux @0x70e010, Windows rva
    /// 0x7dba70) reads three INTs into 0x2a0/0x2a4/0x2a8 and then the next DWORD into +8, identically on
    /// both builds.</summary>
    public static readonly int AreaFlags = Field("CNWCArea", "flags");
    /// <summary>CAurBehaviorCameraFollow -&gt; <c>SafePointer&lt;CAurObject&gt;</c> to the followed object
    /// (the PC); raw pointer at +0. Its <see cref="GobPosition"/> is what GetTargetWorldPosition returns --
    /// and that accessor (@0x4a03a0 / rva 0x56540) is the whole measurement: one line on each build reading
    /// <c>*(this+0x20) + 0xa8</c> (#448; the Windows half returns the Vector through the sret pointer).</summary>
    public static readonly int BehaviorTargetObj = Field("CAurBehaviorCameraFollow", "target");

    // ---- the camera solve (#536): CAurBehaviorCameraFollow::Control decompiled on both builds (Linux
    //      @0x4a2290, Windows @0x140055100, name-exported) reads every field below at the same offset ----

    /// <summary>CAurBehavior -&gt; owner <c>CAurObject*</c> (= <c>Camera+8</c>). Both ctors store it at +0x18.</summary>
    public static readonly int BehaviorOwner = Field("CAurBehavior", "owner");
    /// <summary>CAurBehaviorCameraFollow -&gt; smoothed focus Vector (+0x30..+0x38), written back at the end of Control.</summary>
    public static readonly int FollowFocus = Field("CAurBehaviorCameraFollow", "focus");
    /// <summary>CAurBehaviorCameraFollow -&gt; orbit distance (float). The engine's pull-in
    /// (<c>UpdateNormalCameraCollision</c> @0x7daf80) writes it DIRECTLY at its tail (<c>+0x48 = dist</c>,
    /// <c>+0x7c = 1</c>), before Control runs -- it is not a zoom velocity (#538).</summary>
    public static readonly int FollowDist = Field("CAurBehaviorCameraFollow", "dist");
    /// <summary>CAurBehaviorCameraFollow -&gt; zoom velocity: Control reads it as <c>dist *= 2^(-v*dt*0.5)</c>
    /// and zeroes it; +0x50 next to it is the focus spring (750). Same body on both builds (§9.4).</summary>
    public static readonly int FollowZoomVel = Field("CAurBehaviorCameraFollow", "zoomVel");
    /// <summary>CAurBehaviorCameraFollow -&gt; yaw, degrees, 0 = looking along +Y.</summary>
    public static readonly int FollowYaw = Field("CAurBehaviorCameraFollow", "yaw");
    /// <summary>CAurBehaviorCameraFollow -&gt; yaw velocity, deg/s: Control adds <c>v*dt</c> and zeroes it. The
    /// engine's chase drivers (Linux @0x7dddd0 / @0x7de660, Windows rva 0x7d0d00 / 0x7d1c90) and
    /// <c>TurnCamera(deg, 0)</c> (Linux @0x7d8960, Windows 0x7cfe60) write it at +0x58 on both builds (#541).</summary>
    public static readonly int FollowYawVel = Field("CAurBehaviorCameraFollow", "yawVel");
    /// <summary>CAurBehaviorCameraFollow -&gt; yaw impulse, degrees added once: <c>TurnCamera(deg, 1)</c> (the drag
    /// paths) accumulates it at +0x88 on both builds; Control consumes and zeroes it.</summary>
    public static readonly int FollowYawImpulse = Field("CAurBehaviorCameraFollow", "yawImpulse");
    /// <summary>CAurBehaviorCameraFollow -&gt; pitch, degrees from vertical (1 = top-down, 89 = horizontal).</summary>
    public static readonly int FollowPitch = Field("CAurBehaviorCameraFollow", "pitch");
    /// <summary>CAurBehaviorCameraFollow -&gt; pitch velocity, deg/s, and pitch impulse, degrees added once: the
    /// yaw pair's twins; Control integrates <c>+0x60*dt + +0x8c</c> into +0x5c and zeroes both, same offsets on
    /// both builds (Linux @0x4a2290, Windows @0x140055100; CX-636).</summary>
    public static readonly int FollowPitchVel = Field("CAurBehaviorCameraFollow", "pitchVel");
    /// <inheritdoc cref="FollowPitchVel"/>
    public static readonly int FollowPitchImpulse = Field("CAurBehaviorCameraFollow", "pitchImpulse");
    /// <summary>CAurBehaviorCameraFollow -&gt; yaw / pitch / dist clamp-enabled flags (floats, 0 or 1). Set by
    /// the server's LockCamera* and the lockcamera console var; the same words the photo mode locks with.</summary>
    public static readonly int FollowYawClamp = Field("CAurBehaviorCameraFollow", "yawClamp");
    /// <inheritdoc cref="FollowYawClamp"/>
    public static readonly int FollowPitchClamp = Field("CAurBehaviorCameraFollow", "pitchClamp");
    /// <inheritdoc cref="FollowYawClamp"/>
    public static readonly int FollowDistClamp = Field("CAurBehaviorCameraFollow", "distClamp");
    /// <summary>The clamp ranges: yaw +0x68/+0x6c, pitch +0x74/+0x78, dist +0x80/+0x84 (min, max). The mode
    /// reset pushes the client's [camera.limits] into them with the clamp ENABLED, so "enabled" is normal play;
    /// a server lock writes min = max = the current value (the Windows handler's lock arms, rva 0x77a7f0).</summary>
    public static readonly int FollowYawMin = Field("CAurBehaviorCameraFollow", "yawMin");
    /// <inheritdoc cref="FollowYawMin"/>
    public static readonly int FollowPitchMin = Field("CAurBehaviorCameraFollow", "pitchMin");
    /// <inheritdoc cref="FollowYawMin"/>
    public static readonly int FollowDistMin = Field("CAurBehaviorCameraFollow", "distMin");

    /// <summary>CNWCModule -&gt; the module <c>Camera*</c>. <c>GetModuleCamera</c> (Linux @0x63a930) and the
    /// Windows camera message handler (rva 0x77a7f0) both read <c>GetModule()+0xe8</c>.</summary>
    public static readonly int ModuleCamera = Field("CNWCModule", "camera");
    /// <summary>CNWCModule -&gt; attached object id (<c>SetCameraFollow</c>: Linux @0x7d8540, Windows rva
    /// 0x7cf6f0, reached from the camera message's AttachCamera arm; both write +0x124 and +0x140).</summary>
    public static readonly int ModuleCameraAttached = Field("CNWCModule", "cameraAttached");
    /// <summary>CNWCModule -&gt; find-clear-view pending (int 0/1). See <see cref="ModuleCameraAttached"/>.</summary>
    public static readonly int ModuleFindClearView = Field("CNWCModule", "findClearView", Expect.Byte01);
    /// <summary>CNWCModule -&gt; dialog camera state byte: 0 none, 1 framing, 2 returning (<c>SetDialogCamera</c>:
    /// Linux @0x7da240, Windows rva 0x7cfa90, reached from the dialog message handler).</summary>
    public static readonly int ModuleDialogCamera = Field("CNWCModule", "dialogCamera");
    /// <summary>CNWCModule -&gt; cutscene transition rate (float; 0 = no cutscene interpolation running).
    /// <c>SetCutsceneCamera</c>: Linux @0x7dd370, Windows rva 0x7cfa00, the camera message's SetCameraFacing tail.</summary>
    public static readonly int ModuleCutsceneRate = Field("CNWCModule", "cutsceneRate");
    /// <summary>CClientExoAppInternal -&gt; <c>CClientOptions*</c> (first word). <c>CClientExoApp::GetClientOptions</c>
    /// is <c>*(*(this+8))</c> on both builds (Linux @0x637ea0, Windows rva 0x6ab9b0).</summary>
    public static readonly int InternalClientOptions = Field("CClientExoAppInternal", "clientOptions");
    /// <summary>CClientOptions -&gt; camera mode byte: 0 chase, 1 top-down, 2 driving (nwscript STIFF_CHASE_CAMERA). Read at +0x1d by
    /// <c>SetCameraFollow</c>/<c>UpdateCameraFollow</c> on Linux and by the Windows camera message handler's
    /// lock arms (<c>*(byte*)(opts+0x1d) &lt; 3</c>).</summary>
    public static readonly int OptionsCameraMode = Field("CClientOptions", "cameraMode");

    // ---- #571: the stock settings.tml reader (StockConfig) ----
    /// <summary>CExoBase -&gt; <c>CExoConfig*</c> (first word). Both ctors store the freshly constructed config
    /// there (Linux <c>CExoBase::CExoBase</c> @0x4dd1c0 region, Windows @0x140114b00 <c>*(this) = new CExoConfig</c>),
    /// and every engine <c>Get</c> caller loads <c>**g_pExoBase</c> for <c>this</c> (Linux
    /// <c>CGuiInGame::InitializeUserOptions</c> @0x66e24a, Windows <c>CAurora::ComputeDesiredGUIScale</c> @0x140074f70).</summary>
    public static readonly int ExoBaseConfig = Field("CExoBase", "config");
    /// <summary>CExoConfig -&gt; the "initializing" byte. <c>Initialize</c> (Linux @0x4e2800) sets it for its own
    /// duration and clears it at the end; while set, <c>Get</c> reads the committed table regardless of its
    /// bool. Same test at <c>*this</c> in both builds' <c>Get&lt;bool&gt;</c> (@0x4efce0 / @0x140119650).</summary>
    public static readonly int ExoConfigInitializing = Field("CExoConfig", "initializing");
    /// <summary>CExoConfig -&gt; the WORKING cpptoml table (<c>shared_ptr</c>, pointer word first): what the engine's
    /// own <c>Get(key, false)</c> callers read, and what the options panel edits before <c>Commit</c> copies it
    /// into the committed table at +0x38. Null between the ctor and <c>Initialize</c> (the ctor zeroes it,
    /// Initialize clones the committed table into it) -- the reader's "not yet" test. Same in both
    /// <c>Get&lt;bool&gt;</c> bodies.</summary>
    public static readonly int ExoConfigWorkingTable = Field("CExoConfig", "workingTable");

    /// <summary>CClientExoApp -&gt; CClientExoAppInternal*. #439: <c>CClientExoApp::GetInGameGui</c> (Linux
    /// @0x737c70, Windows rva 0x6abbc0) is one line on both builds, <c>*(*(this+8) + 0x90)</c>.</summary>
    public static readonly int ExoAppInternal = Field("CClientExoApp", "internal");
    /// <summary>CClientExoAppInternal -&gt; CGuiInGame*. Same one-line getter as <see cref="ExoAppInternal"/>.</summary>
    public static readonly int InternalInGameGui = Field("CClientExoAppInternal", "inGameGui");
    /// <summary>CClientExoAppInternal -&gt; the live <c>CMainMenuPanel*</c> (#593). <c>DisplayMainMenu</c> (Linux nm
    /// 0x63c320, Windows rva 0x6d74f0) returns when it is non-null, else news the panel and stores it here;
    /// <c>DestroyMainMenuPanel</c> (Linux nm 0x63a650) deactivates, deletes and nulls it. Non-null = the main menu is
    /// up (it stays up under the pregame Options modal, which only hides the panel).</summary>
    public static readonly int InternalMainMenuPanel = Field("CClientExoAppInternal", "mainMenuPanel");
    /// <summary>CClientExoAppInternal -&gt; "reached the main menu" latch (int 0/1): the same <c>DisplayMainMenu</c>
    /// sets it to 1 on the first display on both builds, <c>CClientExoApp::GetReachedMainMenu</c> (Linux nm 0x638cf0)
    /// reads it, nothing clears it -- past-the-intro, not menu-is-up.</summary>
    public static readonly int InternalReachedMainMenu = Field("CClientExoAppInternal", "reachedMainMenu", Expect.Byte01);
    /// <summary>CClientExoAppInternal -&gt; "the in-game Options panel is open" int (#593 follow-up: the gear in
    /// game). <c>CClientExoApp::SetOptionsPanelOpen(int)</c> (Linux nm 0x638d30, Windows rva 0x6acbf0) is the one
    /// store, <c>*(*(this+8)+0x604) = arg</c> on both builds; <c>CGuiInGame::HandleOptionsButton</c> passes 1 after
    /// activating the panel and <c>HandleOptionsPanelClose</c> (the panel's close callback) passes 0. Stays 1 under
    /// the Game/Video/Controls sub-panels, which only hide the Options panel.</summary>
    public static readonly int InternalOptionsPanelOpen = Field("CClientExoAppInternal", "optionsPanelOpen", Expect.Byte01);
    /// <summary>CNWCPlayer -&gt; character (creature) name CExoString. <c>CClientExoAppInternal::GetPlayerByCreatureName</c>
    /// (Linux @0x642350, Windows rva 0x6d8910) strcmp()s <c>*(player+0x40)</c> after comparing the length at +0x48
    /// on both builds; the player/account name is the +0x10/+0x18 pair (<c>GetPlayerByPlayerName</c>, rva 0x6d8a20).</summary>
    public static readonly int PlayerCreatureName = Field("CNWCPlayer", "creatureName");
    /// <summary>CNWCPlayer -&gt; character name length (the CExoString's second word). See <see cref="PlayerCreatureName"/>.</summary>
    public static readonly int PlayerCreatureNameLen = Field("CNWCPlayer", "creatureNameLen");
    /// <summary>CNWCPlayer -&gt; the connection-scoped player id (what AppendToMsgBuffer passes and the id-based
    /// tell send takes). <c>CClientExoAppInternal::GetPlayerByPlayerID</c> (Linux nm 0x63f1b0, Windows rva
    /// 0x6d89f0) is a list walk comparing <c>*(player+0x8)</c> on both builds.</summary>
    public static readonly int PlayerId = Field("CNWCPlayer", "playerId");
    /// <summary>CNWCPlayer -&gt; that player's creature object id. <c>CClientExoAppInternal::GetPlayerByGameObjectID</c>
    /// (Linux nm 0x63f280, Windows rva 0x6d89c0) compares <c>*(player+0x34)</c> on both builds.</summary>
    public static readonly int PlayerCreatureId = Field("CNWCPlayer", "creatureId");
    /// <summary>CClientExoAppInternal -&gt; CNWCPlayer** roster base; count at <see cref="InternalPlayerCount"/>. Every
    /// GetPlayerBy* walker above reads <c>*(internal+0x38)</c> .. <c>+ *(int*)(internal+0x40)</c> on both builds.</summary>
    public static readonly int InternalPlayers = Field("CClientExoAppInternal", "players");
    /// <summary>CClientExoAppInternal -&gt; roster entry count (int). See <see cref="InternalPlayers"/>.</summary>
    public static readonly int InternalPlayerCount = Field("CClientExoAppInternal", "playerCount");
    /// <summary>CClientExoAppInternal -&gt; the local player's creature object id. <c>CClientExoApp::GetPlayerCreatureId</c>
    /// (Linux nm 0x6377a0, Windows rva 0x6ac1c0) is the one-liner <c>*(*(this+8)+0x48)</c> on both builds.</summary>
    public static readonly int InternalPlayerCreatureId = Field("CClientExoAppInternal", "playerCreatureId");
    /// <summary>CClientExoAppInternal -&gt; CNetLayer* (CX-694). <c>CClientExoApp::GetNetLayer</c> (Linux nm 0x6361a0, Windows
    /// rva 0x6abfb0, unexported: anchored in docs/windows-symbols.md) is the one-liner <c>*(*(this+8)+0x18)</c> on both builds.</summary>
    public static readonly int InternalNetLayer = Field("CClientExoAppInternal", "netLayer");
    /// <summary>CNWCModule -&gt; server-synced game hour (u32). <c>CNWCMessage::HandleServerToPlayerModuleUpdate_Time</c>
    /// (Linux nm 0x76c630, Windows rva 0x7a58e0) stores the flag-2 byte at <c>module+0x16c</c> on both builds.</summary>
    public static readonly int ModuleHour = Field("CNWCModule", "hour");
    /// <summary>CNWCModule -&gt; real minutes per game hour (byte). <c>CNWCModule::SetTimeOfDayState</c> (Linux nm
    /// 0x7d96e0, Windows rva 0x7cfbb0) computes <c>+0x178 = byte[+0x15c] * 60.0</c> on both builds.</summary>
    public static readonly int ModuleMinutesPerHour = Field("CNWCModule", "minutesPerHour");
    /// <summary>CGuiInGameChatPrompt -&gt; shown flag (int). <c>CGuiInGameChatPrompt::Show</c> tests-then-sets it:
    /// Linux nm 0x6779b0 at +0x230, Windows rva 0x87cc00 at +0x200 (the ctor zeroes the same slot).</summary>
    public static readonly int ChatPromptShown = Field("CGuiInGameChatPrompt", "shown");
    /// <summary>CGuiInGameChatPrompt -&gt; the embedded CGuiEdit (#580). <c>HandleEnter</c> (Linux nm 0x679c50, Windows
    /// rva 0x87b0d0) hands <c>this+off</c> to <c>CGuiEdit::GetText</c>, <c>SetCaretPos</c> and <c>LoseFocus</c> on both.</summary>
    public static readonly int ChatPromptEdit = Field("CGuiInGameChatPrompt", "edit");
    /// <summary>CGuiEdit -&gt; <c>CAurString* m_pString</c>, the text object <c>CGuiEdit::GetText</c> (exported on both,
    /// Linux nm 0x592680 / Windows 0x1e93b0) reads its lines from; HandleEnter clears it through the same pointer.</summary>
    public static readonly int GuiEditString = Field("CGuiEdit", "string");
    /// <summary>Virtual slot of <c>CAurString::SetText(const char*)</c> -- the clear HandleEnter runs on the edit's string
    /// (Linux <c>*vtbl+0xd8</c> = the <c>StringGob</c> thunk <c>_ZThn1216_N9StringGob7SetTextEPKc</c>; Windows <c>+0xd0</c>,
    /// MSVC's single-slot virtual destructor). Both read off the HandleEnter bodies.</summary>
    public static readonly int AurStringSetTextSlot = Field("CAurString", "vtbl.SetText", Expect.VtblSlot);
    /// <summary>CGuiInGame -&gt; CPanelQuickBar*. #439: <c>CGuiInGame::Initialize</c> (Linux @0x772600) and its
    /// Windows twin (rva 0x6f9190, the one caller of the CPanelQuickBar ctor) both store the freshly built
    /// panel to +0x60 and then write <c>+0x28 = 1</c>.</summary>
    public static readonly int InGameGuiQuickbar = Field("CGuiInGame", "quickbar");
    /// <summary>CGuiInGame -&gt; the stock <c>CGuiInGameChatDialog*</c>, null while no engine conversation panel is
    /// up (#591). <c>ShowDialogEntry</c> news it into +0x70 when null, <c>ShowDialogReplies</c> (Linux @0x770c30,
    /// Windows rva 0x6fb8e0) is a no-op on null +0x70, and <c>CloseDialog</c> (Linux @0x770320, rva 0x6f6d80)
    /// virtual-deletes it, nulls +0x70 and runs <c>ResetGuiState(0x298)</c> -- all on both builds.</summary>
    public static readonly int InGameGuiDialog = Field("CGuiInGame", "dialog");
    /// <summary>CGuiInGame -&gt; the primary <c>CGuiInGameChatWindow*</c>, null until a module loads (the in-module
    /// signal the frame gate reads). <c>AppendToMsgBuffer</c> (Linux @0x76d550, Windows rva 0x6f68d0) loads it as
    /// <c>this[1]</c> first thing on both builds; <c>PrimaryMsgWindowIsVisible</c> (Linux @0x76e380) forwards it.</summary>
    public static readonly int InGameGuiChatWindow = Field("CGuiInGame", "chatWindow");
    /// <summary>CGuiInGame -&gt; the live in-game <c>COptionsPanel*</c> (the ESC menu), null while it is down.
    /// <c>HandleOptionsButton</c> (Linux @0x76de30, Windows rva 0x6f86c0 -- the caller of the ctor that owns
    /// <c>"OPTIONS_MAIN"</c>) toggles an existing +0xd8 closed, else news the panel (0xc00 bytes Linux, 0xac0 Windows)
    /// into +0xd8 and activates it; <c>HandleOptionsPanelClose</c> (Linux @0x76ea50, rva 0x6f8820) nulls it before
    /// <c>CGuiPanel::Deactivate</c> deletes the panel. Both builds, same word.</summary>
    public static readonly int InGameGuiOptionsPanel = Field("CGuiInGame", "optionsPanel");
    /// <summary>CGuiPanel -&gt; state bits: 1 active, 2 hidden (<c>Hide</c>/<c>UnHide</c>), 4 force-hidden (<c>ForceVisible</c>
    /// clears both); the engine's own "shown" test is <c>== 1</c> (<c>Activate</c>'s event choice, <c>Hide</c>'s scene
    /// detach). Exported <c>Hide</c>/<c>UnHide</c>/<c>Activate</c> read it at Linux +0xb8 (@0x687160 / 0x687740 / 0x6878b0),
    /// Windows +0xb0 (rva 0x1fc800 / 0x1fd0d0 / 0x1fbe70); the scene view behind it is +0xc0 / +0xb8.</summary>
    public static readonly int GuiPanelState = Field("CGuiPanel", "state");
    /// <summary>CGuiMan viewport width / height (int32 pair). #439: <c>CGuiMan::SetGuiSize</c> (exported,
    /// Linux @0x67efc0 / Windows rva 0x1f6640) stores its two args to +0xb8 / +0xbc on both.</summary>
    public static readonly int GuiManVpW = Field("CGuiMan", "vpWidth");
    public static readonly int GuiManVpH = Field("CGuiMan", "vpHeight");
    /// <summary>CGuiMan's tracked mouse position (int32 pair, engine GUI space: Y flipped), the point its own
    /// press/wheel handlers hit-test. #451: <c>CGuiMan::HandleLMouseDown</c> reads +0xd4/+0xd8 on both builds.</summary>
    public static readonly int GuiManMouseX = Field("CGuiMan", "mouseX");
    public static readonly int GuiManMouseY = Field("CGuiMan", "mouseY");

    /// <summary>What to add to a vtable SYMBOL to reach slot 0. Itanium puts offset-to-top and the typeinfo
    /// pointer in front of the function pointers, so <c>_ZTV&lt;class&gt;</c> is two words short of the
    /// table; MSVC's <c>??_7&lt;class&gt;@@6B@</c> already points at slot 0. Measured by construction in
    /// <c>tools/abi/vtable-slots.py</c>, which is how #433's nine slots were read on both sides.</summary>
    public static readonly int VtblSymbolHeader = Field("(abi)", "vtblSymbolHeader");

    // ================= Alloy: the passes (#441, batch 2 of #429 section 2) =================
    // Measured 2026-09-12, same tooling as #436 (tools/ghidra/FieldDump.java over both programs, .scratch/441)
    // but read per function pair rather than through field-diff.py's strict filter, which rejects a pair the
    // moment the two decompilers split one 8-byte store differently. Reading the pairs by hand is what
    // resolved PartEmitter, the class #430 named as the worst case.
    // Every value here is decompiler evidence -- a CANDIDATE until a runtime probe says otherwise.

    // ---- Part: the base fields the pass walkers read (identical 0x0..0x58, #436) ----

    /// <summary>Part -&gt; its MdlNode. <c>PartEmitter::Initialize</c> @0x4aaaa0 reads it as
    /// <c>MdlNodeEmitter*</c> and <c>Scene::RenderLensFlares</c> @0x471860 as the light's node -- one field,
    /// whichever subclass of node the part instantiates.</summary>
    public static readonly int PartNodeLink = Field("Part", "node");
    /// <summary>Part -&gt; uniform scale, the third term <c>Part::GetWorld</c> composes beside
    /// <see cref="PartPos"/> and <see cref="PartRot"/>. Paired directly on <c>Part::PostProcess</c>.</summary>
    public static readonly int PartScale = Field("Part", "scale");
    /// <summary>Part -&gt; the draw gate <c>Gob::PartDraw</c> reads before descending. Inside Part's measured
    /// identical run, not paired on its own.</summary>
    public static readonly int PartVisible = Field("Part", "visible");

    // ---- PartBeam (its whole Draw is two reads, and they pair at delta 0) ----

    /// <summary>The only two fields <c>PartBeam::Draw</c> @0x450120 reads -- the body is
    /// <c>RenderVertexData(part+0xa0, part+0xa8, 4, 0xffffffff)</c>, so the beam capture needs no funnel arm.
    /// One validated pair, and it maps both offsets at delta 0 -- PartBeam does NOT inherit PartTriMesh's
    /// +0x8 (#430's rule that a class verdict does not travel, running the other way).</summary>
    public static readonly int BeamVdo = Field("PartBeam", "vdo");
    /// <inheritdoc cref="BeamVdo"/>
    public static readonly int BeamItem = Field("PartBeam", "item");

    // ---- PartLight: one more field in the +0x4 block ----

    /// <summary>The lens-flare occlusion fade, and NOT <see cref="LightFade"/>: two distinct floats.
    /// <c>Scene::RenderLensFlares</c> @0x471860 integrates this one toward 1.0 at <c>3/s</c> while the light
    /// is unoccluded and multiplies the flare colour by it, while <c>LightManager::ControlLightsFade</c>
    /// @0x4c2050 scales the area terms by +0x90. #441 opened expecting one of the two to be wrong; the
    /// <c>PartLight</c> constructor @0x43d1d0 stores 8 bytes at 0x8c and again at 0x9c, so both dwords exist.
    /// Windows by the class's uniform +0x4: the ctor's whole run moves 0x64..0xb7 to 0x68..0xbb.</summary>
    public static readonly int LightFlareFade = Field("PartLight", "flareFade");

    // ---- PartEmitter: four segments and a sign change (#430's worst case, now measured) ----
    // Anchored on eight function pairs (RenderEmitter, RenderNothing, GetTileAlpha, chkRandom,
    // chkParticleLife, moveParticle, moveParticleP2PGrav, moveParticleP2PBezier):
    //   0x00..0x50  delta 0     0x64..0xd8  +0x4     0xf8..0x158  +0x8
    //   0x188..0x1a8 delta 0    0x1c0 and up  -0x8
    // The -0x8 is corroborated by the object's own end: moveParticle pairs 0x1f8 -> 0x1f0.

    /// <summary>Blend mode: 1 = punch-through, 0 = normal alpha, anything else = additive.</summary>
    public static readonly int EmBlend = Field("PartEmitter", "blend");
    /// <summary>Alpha at the end and the start of life (in that order in memory).</summary>
    public static readonly int EmAlphaEnd = Field("PartEmitter", "alphaEnd");
    /// <inheritdoc cref="EmAlphaEnd"/>
    public static readonly int EmAlphaStart = Field("PartEmitter", "alphaStart");
    /// <summary>Colour at the end and the start of life, three floats each.</summary>
    public static readonly int EmColorEnd = Field("PartEmitter", "colorEnd");
    /// <inheritdoc cref="EmColorEnd"/>
    public static readonly int EmColorStart = Field("PartEmitter", "colorStart");
    /// <summary>UV-grid animation: frames per second, then the end and start frame indices. A zero fps or
    /// equal ends means the frame index never advances.</summary>
    public static readonly int EmFps = Field("PartEmitter", "fps");
    /// <inheritdoc cref="EmFps"/>
    public static readonly int EmFrameEnd = Field("PartEmitter", "frameEnd");
    /// <inheritdoc cref="EmFps"/>
    public static readonly int EmFrameStart = Field("PartEmitter", "frameStart");
    /// <summary>Particle lifetime. Zero draws nothing at all -- the engine's own early-out.</summary>
    public static readonly int EmLife = Field("PartEmitter", "life");
    /// <summary>Quad half-extents before the 0.5 and the Gob scale: X at start/end, then Y at start/end.
    /// All four move together in <c>RenderEmitter</c>'s own read run.</summary>
    public static readonly int EmSizeStartX = Field("PartEmitter", "sizeStartX");
    /// <inheritdoc cref="EmSizeStartX"/>
    public static readonly int EmSizeEndX = Field("PartEmitter", "sizeEndX");
    /// <inheritdoc cref="EmSizeStartX"/>
    public static readonly int EmSizeStartY = Field("PartEmitter", "sizeStartY");
    /// <inheritdoc cref="EmSizeStartX"/>
    public static readonly int EmSizeEndY = Field("PartEmitter", "sizeEndY");
    /// <summary>The second fade owner the draw early-out tests, beside the Gob's own
    /// <see cref="GobTileData"/>. The only witness of the +0x8 segment's low end, and it has three
    /// (GetTileAlpha, RenderNothing, RenderEmitter all pair 0xf8 -&gt; 0x100).</summary>
    public static readonly int EmTileOwner = Field("PartEmitter", "tileOwner");
    /// <summary><c>Particle**</c>, and the count. The array is dense: index i is drawn as quad i.</summary>
    public static readonly int EmParts = Field("PartEmitter", "parts");
    /// <inheritdoc cref="EmParts"/>
    public static readonly int EmCount = Field("PartEmitter", "count");
    /// <summary>Render type -- which billboard basis the quads get.</summary>
    public static readonly int EmType = Field("PartEmitter", "type");
    /// <summary>The Material whose slot-0 texture every quad samples. First field of the -0x8 segment.</summary>
    public static readonly int EmMaterial = Field("PartEmitter", "material");
    /// <summary>Interpolation domain (a SHORT, not an int): 0 interpolates over the particle's INDEX in the
    /// array, non-zero over its age. Reading it as an int folds in the two bytes above and turns every
    /// index-interpolated emitter into an age-interpolated one.</summary>
    public static readonly int EmLifeInterp = Field("PartEmitter", "lifeInterp");
    /// <summary>The emitter's own orientation quaternion, stored (w, x, y, z).</summary>
    public static readonly int EmOrient = Field("PartEmitter", "orient");
    /// <summary>Emitter world position, the point the particle tint is sampled at.</summary>
    public static readonly int EmPos = Field("PartEmitter", "pos");

    // ---- Particle (identical 0x0..0x77) ----
    // Two anchors, and both are exact: Particle::Particle touches the same thirteen offsets on both builds,
    // and Particle::initialize the same twenty-two -- including every offset below.

    /// <summary>Position, and the position the previous simulation step left. The previous one is only read
    /// by the motion-blurred type, where it is the far edge of a stretched quad.</summary>
    public static readonly int ParticlePos = Field("Particle", "pos");
    /// <inheritdoc cref="ParticlePos"/>
    public static readonly int ParticlePrevPos = Field("Particle", "prevPos");
    /// <summary>Roll about the billboard normal, in radians (the engine feeds it to <c>sincosf</c> after
    /// halving, i.e. as a quaternion half-angle).</summary>
    public static readonly int ParticleRoll = Field("Particle", "roll");
    /// <summary>The particle's own orientation quaternion, (w, x, y, z) -- render type 5's whole basis.</summary>
    public static readonly int ParticleQuat = Field("Particle", "quat");
    /// <summary>UV-grid frame index, and the age at which it last advanced. Both are WRITTEN by the vertex
    /// build: owning the build means owning that advance, or every animated particle freezes.</summary>
    public static readonly int ParticleFrame = Field("Particle", "frame");
    /// <inheritdoc cref="ParticleFrame"/>
    public static readonly int ParticleFrameTime = Field("Particle", "frameTime");
    /// <summary>The splat normal, used as the billboard's reference axis on a splat emitter.</summary>
    public static readonly int ParticleSplatNormal = Field("Particle", "splatNormal");
    /// <summary>Splat flag (one byte) and age.</summary>
    public static readonly int ParticleSplat = Field("Particle", "splat");
    /// <inheritdoc cref="ParticleSplat"/>
    public static readonly int ParticleAge = Field("Particle", "age");

    // ---- MdlNodeEmitter (a +0x4 segment, and a boundary nobody has pinned) ----

    /// <summary>UV grid: columns then rows. Zero means one, and a grid of 1x1 skips the animation entirely.
    /// <para>Windows from the constructor, which pairs 0xa4 -&gt; 0xa8 and 0xb0 -&gt; 0xb4 and so brackets
    /// both of these inside one +0x4 run; every one of the class's 22 Enable/Is accessors pairs its flags
    /// word 0x170 -&gt; 0x174, which is the same +0x4 higher up.</para></summary>
    public static readonly int EmitterGridX = Field("MdlNodeEmitter", "gridX");
    /// <inheritdoc cref="EmitterGridX"/>
    public static readonly int EmitterGridY = Field("MdlNodeEmitter", "gridY");
    /// <summary>Non-zero DISABLES back-face culling (the engine reads it as
    /// <c>if (node+0x164 == 0) Enable(CULL_FACE) else Disable(CULL_FACE)</c>).
    /// <para>#448 found the second reader the ctor's ambiguity was waiting on: <c>PartEmitter::RenderEmitter</c>
    /// (@0x4ad4c0 / rva 0x62900) and <c>RenderStandard</c> (@0x4af4c0 / rva 0x643b0) both branch on
    /// <c>*(int*)(node + off) == 0</c> into <c>Enable(3)</c>/<c>Disable(3)</c>, at 0x164 on Linux and 0x168
    /// on Windows. That is +0x4, the same as the ctor's 0x154 and 0x16c neighbours -- so the store the
    /// Windows decompiler folded onto 0x160 was the artefact, not the map.</para></summary>
    public static readonly int EmitterNoCull = Field("MdlNodeEmitter", "noCull");

    // ---- MdlNodeLight: the lens-flare declaration (no validated pair on either build) ----

    /// <summary>The light node's flare parameters, all read by <c>Scene::RenderLensFlares</c> @0x471860:
    /// <c>flareradius</c>, the flare count, the <c>flaresizes</c> float array and the
    /// <c>flarecolorshifts</c> vec3 array.
    /// <para>#448: MdlNodeLight still pairs on none of its own nine functions, and it did not need to --
    /// <c>Scene::RenderLensFlares</c> (@0x471860 / rva 0xcea90) reads all four off <c>light+8</c> on both
    /// builds, each pinned by its arithmetic: the radius is both the distance cutoff and the normalising
    /// divisor, the count is the loop bound, the sizes array scales the screen offset and the shifts array is
    /// added to the colour as three floats. The class diverges in two segments -- the radius is +0x4, the
    /// three above it +0x8.</para></summary>
    public static readonly int FlareRadius = Field("MdlNodeLight", "flareRadius");
    /// <inheritdoc cref="FlareRadius"/>
    public static readonly int FlareCount = Field("MdlNodeLight", "flareCount");
    /// <inheritdoc cref="FlareRadius"/>
    public static readonly int FlareSizes = Field("MdlNodeLight", "flareSizes");
    /// <inheritdoc cref="FlareRadius"/>
    public static readonly int FlareShifts = Field("MdlNodeLight", "flareShifts");

    // ---- MdlNodeTriMesh: the faces the grass binner reads ----

    /// <summary>MdlNodeTriMesh -&gt; faces (items +0xb0, count +0xb8), the array <c>Scene::AddFacesToBins</c>
    /// @0x460ef0 and <c>CAurTriangleList::AddTriangle</c> @0x4b5650 bin the engine's own grass from.
    /// <c>InternalGenVertices</c> pairs both offsets at delta 0, and <c>BuildVertexIndices</c> agrees.</summary>
    public static readonly int MeshFaces = Field("MdlNodeTriMesh", "faces");
    /// <inheritdoc cref="MeshFaces"/>
    public static readonly int MeshFaceCount = Field("MdlNodeTriMesh", "faceCount");

    /// <summary>One <c>MaxFace</c> record: material index at +0x10, the three vertex indices as u16 at
    /// +0x1a/+0x1c/+0x1e, 0x20 bytes a record.
    /// <para>A record LAYOUT with no functions of its own -- nothing takes a <c>MaxFace*</c> as <c>this</c>.
    /// #448 read it on the CALLER instead: <c>MdlNodeTriMesh::BuildVertexIndices</c> (@0x455af0 / rva
    /// 0xa5e20) walks <c>node+0xb0</c> in <c>0x20</c>-byte steps and copies the three <c>u16</c>s at
    /// +0x1a/+0x1c/+0x1e into the index buffer, three per face, on both builds. The stride is a SIZE, so it
    /// is read off the Windows arithmetic and never carried over.</para></summary>
    public static readonly int FaceStride = Field("MaxFace", "stride");
    /// <inheritdoc cref="FaceStride"/>
    public static readonly int FaceVertA = Field("MaxFace", "vertA");
    /// <inheritdoc cref="FaceStride"/>
    public static readonly int FaceVertB = Field("MaxFace", "vertB");
    /// <inheritdoc cref="FaceStride"/>
    public static readonly int FaceVertC = Field("MaxFace", "vertC");
    /// <summary>Stride of the host-side shadow of a vertex pool: positions 12 bytes apart. Same kind of
    /// unowned record as <see cref="FaceStride"/>, and read the same way -- on the caller. The tangent-basis
    /// worker (Linux @0x456190, reached from <c>MdlNodeTriMesh::CalculateTangentSpaceBasis</c> @0x457710;
    /// Windows inlined into rva 0xa6130) locks <c>node+0x280</c> and indexes it <c>* 0xc</c> with +0/+4/+8 on
    /// both builds, and resizes it to twelve bytes a vertex on each -- Linux as <c>count * 0xc</c>, Windows as
    /// the strength-reduced <c>(count + count * 2) * 4</c> (#448).</summary>
    public static readonly int PoolPosStride = Field("VertexPool", "posStride");

    // ---- Scene: four more spans inside the identical 0x0..0x1e0 run (#436) ----

    /// <summary>The skybox state <c>Scene::RenderSkyBoxes</c> @0x466910, <c>SetSkyBox</c> @0x45e260 and
    /// <c>SetSkyAlpha</c> @0x45f100 share: four skybox gobs from +0x18, the cross-fade gob at +0x38, the fade
    /// parameter t at +0x40, the current and desired slot indices (-1 = none) at +0x44/+0x48, and the
    /// "skybox off" byte at +0x4c that <c>TurnOffSkyBox</c> sets.</summary>
    public static readonly int SceneSkyGobs = Field("Scene", "skyGobs");
    /// <inheritdoc cref="SceneSkyGobs"/>
    public static readonly int SceneSkyFadeGob = Field("Scene", "skyFadeGob");
    /// <inheritdoc cref="SceneSkyGobs"/>
    public static readonly int SceneSkyT = Field("Scene", "skyT");
    /// <inheritdoc cref="SceneSkyGobs"/>
    public static readonly int SceneSkyCur = Field("Scene", "skyCur");
    /// <inheritdoc cref="SceneSkyGobs"/>
    public static readonly int SceneSkyDesired = Field("Scene", "skyDesired");
    /// <inheritdoc cref="SceneSkyGobs"/>
    public static readonly int SceneSkyOff = Field("Scene", "skyOff");
    /// <summary>Where <c>RenderSkyBoxes</c> parks every sky gob: <c>Gob::SetPosition(Scene+0x190)</c>, a
    /// <c>Vector</c>, called once per gob per frame right before <c>Gob::Render</c>. Paired twice.</summary>
    public static readonly int SceneSkyPos = Field("Scene", "skyPos");
    /// <summary>Scene -&gt; the light a shadow view is currently being baked for.</summary>
    public static readonly int SceneCurrentLight = Field("Scene", "currentLight");
    /// <summary>Scene's shadow-light list (<c>PartLight**</c> at +0x60, count at +0x68) -- the one
    /// <c>Scene::RenderBeams</c> @0x46aba0 scans for the beam direction. NOT the LightManager's master list
    /// at <see cref="LmMasterData"/>, and not <see cref="SceneLightManager"/>.</summary>
    public static readonly int SceneShadowLights = Field("Scene", "shadowLights");
    /// <inheritdoc cref="SceneShadowLights"/>
    public static readonly int SceneShadowLightCount = Field("Scene", "shadowLightCount");
    /// <summary><c>Scene+0x1e8</c>: the scene's environment map, a <c>SafePointer&lt;CAurTexture&gt;</c> whose
    /// first word is the borrowed texture. <c>Scene::Render</c> @0x5762d0 fills it once per scene from the
    /// TILESET's own resref (<c>CNWTileSet::GetEnvMapResRef</c>) and falls back to
    /// <c>AurTextureGetFallbackEnvironmentMap</c> when the tileset names none.
    /// <para>#448: <c>Scene::Render</c> (@0x4762d0 / rva 0xcc730) assigns the <c>SafePointer&lt;CAurTexture&gt;</c>
    /// at <c>this+0x1e8</c> from <c>GetEnvMapResRef</c> and falls back to
    /// <c>AurTextureGetFallbackEnvironmentMap</c> at the same offset on both builds; <c>RenderFlat</c> binds
    /// <c>currentscene+0x1e8</c> on both. The Scene ctor zeroes 0x1e8 on both.</para></summary>
    public static readonly int SceneEnvMap = Field("Scene", "envMap");

    // ---- Gob: four more fields and one vtable slot ----

    /// <summary>Gob -&gt; its cached bounding box, min at +0x160 and max at +0x16c.
    /// <c>Gob::GetBoundingBox</c> @0x436ee0 is a plain read of exactly these two, no recompute, and it pairs
    /// at delta 0. Only the EXTENT is used (the pair's space is not established, and the extent is the same
    /// either way); it is centred on <see cref="GobPosition"/>, which is unambiguously world.</summary>
    public static readonly int GobBoundsMin = Field("Gob", "boundsMin");
    /// <inheritdoc cref="GobBoundsMin"/>
    public static readonly int GobBoundsMax = Field("Gob", "boundsMax");
    /// <summary>The "visible" byte <c>Gob::Render</c> gates on.</summary>
    public static readonly int GobVisible = Field("Gob", "visible");
    /// <summary>Gob -&gt; the attachment link whose chain multiplies a further lerped scale into the gob's
    /// own. Paired three times at delta 0, and the highest Gob field #436 measured.</summary>
    public static readonly int GobAttach = Field("Gob", "attach");
    /// <summary>CAurBehavior -&gt; the SafePointer&lt;CAurObject&gt; naming the object the behaviour hangs its gob
    /// off: the parent for the five CAurBehaviorAttach* classes (body parts, held items), the follow target
    /// for the camera ones, zero for Navigate. The raw pointer is the SafePointer's first word. Both ctors
    /// write it at +0x20 (Linux @0x5a2d50, Windows @0x140053b30, #490).</summary>
    public static readonly int BehaviorTarget = Field("CAurBehavior", "target");
    /// <summary><c>Gob::GetAdjustedScale() const</c> -- the scale every emitter size term is multiplied by.
    /// Printed on both sides by <c>tools/abi/vtable-slots.py Gob</c>; -8, like every other slot (#433).</summary>
    public static readonly int GobAdjustedScaleSlot =
        Field("Gob", "vtbl.GetAdjustedScale", Expect.VtblSlot);

    // ---- TileData: three fields the constructor pairs exactly ----
    // TileData::TileData writes 0x0..0xcb on both builds and agrees store for store outside 0x58..0x7c,
    // where MSVC splits three of Linux's 8-byte stores. That murky window is exactly where #436 left
    // TileFade/TilePendingFade unresolved, and it is why these three are resolvable and those two are not.

    /// <summary>TileData -&gt; its surviving parts (<c>CExoArrayList&lt;Part*&gt;</c>, items +0x30, count
    /// +0x38): the array <c>Scene::SpawnTile</c> @0x474470 walks and <c>ProcessStaticParts</c> @0x470970
    /// fills.</summary>
    public static readonly int TileParts = Field("TileData", "parts");
    /// <inheritdoc cref="TileParts"/>
    public static readonly int TilePartCount = Field("TileData", "partCount");
    /// <summary>The fade factor <c>PartEmitter::RenderEmitter</c> @0x4ad4c0 declines on: below 1.0 the engine
    /// draws the emitter not at all. Reached through <see cref="GobTileData"/> or
    /// <see cref="EmTileOwner"/>. A THIRD float, distinct from <see cref="TileFade"/> and
    /// <see cref="TilePendingFade"/>, which <c>HandleFadingInBucket</c> @0x47f670 drives.</summary>
    public static readonly int TileGeomFade = Field("TileData", "geomFade");
    /// <summary>#546: the fog-of-war flag <c>AuroraSetTileFoggedByWar</c> @0x453320 writes (one byte, with
    /// <see cref="TileGeomFade"/> zeroed beside it); while set, the @0x481000 visitor holds the ramp at 0.
    /// Alloy reads the ramp, not the flag: the flag has no expression the ramp does not already carry.
    /// Same on Windows by the ctor pairing (identical stores over 0x7c..0xcb).</summary>
    public static readonly int TileFoggedByWar = Field("TileData", "foggedByWar");

    // ---- the grass classes ----

    /// <summary><c>CAurTriangleBin</c>: the triangle-list array at +0x38 and its count at +0x40, from
    /// <c>RenderGrassPolys</c> @0x4b5410. Nine of the class's functions pair, every one at delta 0.</summary>
    public static readonly int BinLists = Field("CAurTriangleBin", "lists");
    /// <inheritdoc cref="BinLists"/>
    public static readonly int BinListCount = Field("CAurTriangleBin", "listCount");

    /// <summary><c>CAurTriangleList</c>: the flat vertex array (three vec3 per face, stride 0x24) with its
    /// FLOAT count at +8, the parallel per-face blade counts at +0x30 with their count at +0x38, and the
    /// grass material index at +0x40 -- <c>RenderGrassPolys</c> compares exactly that against the material it
    /// is drawing. Seven paired functions, all delta 0, bracketing 0x40 between 0x3c and 0x48.</summary>
    public static readonly int ListVerts = Field("CAurTriangleList", "verts");
    /// <inheritdoc cref="ListVerts"/>
    public static readonly int ListVertFloats = Field("CAurTriangleList", "vertFloats");
    /// <inheritdoc cref="ListVerts"/>
    public static readonly int ListBlades = Field("CAurTriangleList", "blades");
    /// <inheritdoc cref="ListVerts"/>
    public static readonly int ListBladeCount = Field("CAurTriangleList", "bladeCount");
    /// <inheritdoc cref="ListVerts"/>
    public static readonly int ListMat = Field("CAurTriangleList", "mat");

    /// <summary><c>CAurGrassInfo</c>, 0x54 bytes: 32-char texture name, density, blade height (the quad is
    /// height wide AND height tall), diffuse and ambient colours, and the four 2x2-atlas selection
    /// probabilities. Read live off <c>g_GrassInfo</c> -- a server <c>AddGrassOverride</c> rewrites them.
    /// <para>The class has exactly one function, its constructor, and it writes the same offsets on both
    /// builds -- 0x0 and 0x50 as single bytes, 0x20..0x4f as floats (Windows splits the 8-byte stores at
    /// 0x40 and 0x48, which is a decompiler difference, not a layout one). One anchor rather than #430's
    /// preferred pair, because there is no second function to be had.</para></summary>
    public static readonly int GrassInfoName = Field("CAurGrassInfo", "name");
    /// <summary>Blades per face in <c>AddTriangle</c> are <c>|cross| * 0.5 * density</c>, i.e. face area
    /// times this.</summary>
    public static readonly int GrassInfoDensity = Field("CAurGrassInfo", "density");
    /// <inheritdoc cref="GrassInfoName"/>
    public static readonly int GrassInfoHeight = Field("CAurGrassInfo", "height");
    /// <inheritdoc cref="GrassInfoName"/>
    public static readonly int GrassInfoDiffuse = Field("CAurGrassInfo", "diffuse");
    /// <inheritdoc cref="GrassInfoName"/>
    public static readonly int GrassInfoAmbient = Field("CAurGrassInfo", "ambient");
    /// <inheritdoc cref="GrassInfoName"/>
    public static readonly int GrassInfoProb = Field("CAurGrassInfo", "prob");
    /// <inheritdoc cref="GrassInfoName"/>
    public static readonly int GrassInfoInUse = Field("CAurGrassInfo", "inUse");
    /// <summary>Record size of <see cref="GrassInfoName"/>'s class -- the stride <c>g_GrassInfo</c> is
    /// indexed by. A SIZE, and the one row here resting on an ABI argument rather than a reading: the ctor
    /// puts the last member (a byte) at 0x50 on both, the largest member is a float, and the class has no
    /// base -- and base tail-padding reuse, #430's whole cause of divergence, needs a base to reuse.</summary>
    public static readonly int GrassInfoStride = Field("CAurGrassInfo", "stride");

    // ---- aurMatrixStack ----

    /// <summary>The engine's software matrix stack: the top matrix's 16 floats at +0x18, and 0x58 bytes to
    /// the next stack (PROJECTION=0, VIEW=1, MODEL=2 inside <c>m_aurMtxStack</c>).
    /// <para>All seven of the class's functions pair, and the union of what they touch is 0x18..0x57 on both
    /// builds and nothing else -- which measures the top AND the stride, since the stride is the class's own
    /// size.</para></summary>
    public static readonly int MtxStackTop = Field("aurMatrixStack", "top");
    /// <inheritdoc cref="MtxStackTop"/>
    public static readonly int MtxStackStride = Field("aurMatrixStack", "stride");

    // ---- classes the pass reads that no sweep can pair ----

    /// <summary>The two <c>List&lt;MaterialParameter&gt;</c> the .mtr's declared values reach. Shared first,
    /// instance second, so a per-part override still wins -- the instance list is only SIZED to the shared
    /// count and carries defaults until something overrides.
    /// <para>#443 resolved what #441 could not: <c>Material::InitSharedMaterial</c> writes the pointer at
    /// +0x118 and the count at +0x120 on BOTH builds, and Windows <c>CheckAndBindCustomShader</c> reads the
    /// same pair Linux's <c>SetMaterialParam</c> does. Two function pairs, no inference.</para></summary>
    public static readonly int MatParams = Field("Material", "params");
    /// <inheritdoc cref="MatParams"/>
    public static readonly int MatParamCount = Field("Material", "paramCount");
    /// <summary>The shared half of <see cref="MatParams"/>. <c>SharedMaterial::Init</c> reads
    /// <c>1d8:4 / 1d0:8</c> on both builds, and Windows <c>ParseField</c> reads the same pair -- a direct
    /// pairing, not the neighbour corroboration #436 and #441 rightly refused.</summary>
    public static readonly int SharedParams = Field("SharedMaterial", "params");
    /// <inheritdoc cref="SharedParams"/>
    public static readonly int SharedParamCount = Field("SharedMaterial", "paramCount");

    /// <summary>One <c>MaterialParameter</c>: name at +0, value at +0x44, type at +0x5c (0 = float),
    /// component count at +0x60 as a ushort, 100 bytes a record.
    /// <para>Nothing takes one as <c>this</c>, so no class sweep reaches it -- #443 read it out of
    /// <c>SharedMaterial::BindShaderParameters</c> instead, decompiled on both builds
    /// (@0x4c5480 / @0x82aa0). That one function walks the whole record: <c>+0x5c</c> the discriminator,
    /// <c>+0x44</c> the value, <c>+0x60</c> the ushort component count, <c>+0x54/+0x58</c> the two cached
    /// uniform locations, and <c>lVar += 100</c> a record -- byte for byte the same on both.</para></summary>
    public static readonly int ParamValue = Field("MaterialParameter", "value");
    /// <inheritdoc cref="ParamValue"/>
    public static readonly int ParamType = Field("MaterialParameter", "type");
    /// <inheritdoc cref="ParamValue"/>
    public static readonly int ParamComps = Field("MaterialParameter", "comps");
    /// <inheritdoc cref="ParamValue"/>
    public static readonly int ParamStride = Field("MaterialParameter", "stride");

    /// <summary>CNWCArea -&gt; tile grid width (+0xc) and height (+0x10): <c>CNWCArea::LoadBorder</c>
    /// (Linux @0x70a5c0, Windows rva 0x7dea10) multiplies exactly these for the tile count and bounds its
    /// strip loops on them. #446: width is the row-index MULTIPLIER and height the loop bound on both --
    /// <c>(height - 1) * width</c> is the last row's first tile in either decompilation, and the record it
    /// indexes is the same 0x60-byte stride off the tile array at CNWCArea+0x298.</summary>
    public static readonly int AreaTileWidth = Field("CNWCArea", "tileWidth");
    /// <inheritdoc cref="AreaTileWidth"/>
    public static readonly int AreaTileHeight = Field("CNWCArea", "tileHeight");

    /// <summary>The GL program id inside a <c>GLRender::PROG_FIXED</c> record, from
    /// <c>GLRender::SetProgram</c> (@0x4cf940 / rva 0xb7a40). Every function that touches a PROG_FIXED takes
    /// it as an ARGUMENT rather than <c>this</c>, so the sweep never reaches it -- #448 read the caller: on
    /// both builds <c>SetProgram</c> takes <c>prog+0x64</c> as the id it hands to <c>glUseProgram</c>, and
    /// the stock-shader table it indexes has the same <c>0x1ef8</c> record stride on each.</summary>
    public static readonly int ProgFixedId = Field("GLRender::PROG_FIXED", "programId");

    /// <summary>A <c>List&lt;T&gt;</c> / <c>CExoArrayList&lt;T&gt;</c> in this engine is {element pointer,
    /// count}: the symbol's own address is the list, not the elements. Both builds, every paired list in the
    /// #436 and #441 sweeps.</summary>
    public static readonly int ListItems = Field("(abi)", "list.items");
    /// <inheritdoc cref="ListItems"/>
    public static readonly int ListItemCount = Field("(abi)", "list.count");


    // ================= Alloy: the renderer, textures, buffers and shim (#443, batch 3 of #429 section 2) ==
    // The last of section 2. Measured 2026-09-12: one FieldDump.java cycle over CNWCAnimBase/CRes/
    // CAurTexture/Material (.scratch/443), the earlier #436/#441 dumps re-read for the classes they already
    // covered, tools/ghidra/DecompOne.java for the three functions no class sweep reaches
    // (SharedMaterial::BindShaderParameters, GLRender::SetUniforms, GLRender::UnlockPool), and
    // tools/abi/vtable-slots.py for every vtbl row. Decompiler evidence: a CANDIDATE until probed.

    // ---- CAurTextureBasic (identical 0x28..0xec; #436 read the class by hand, this batch pairs the rest) ----

    /// <summary>The decoded image buffer. <c>ConstructImage</c> fills it, <c>FreeImage</c> releases it, and
    /// <c>BuildAndStoreTexture</c> refuses without it -- all three read +0x58 on both builds.</summary>
    public static readonly int TexBasicImage = Field("CAurTextureBasic", "image");

    /// <summary>The GL name the engine bound for a single-cell texture. <c>Reset</c> clears it and
    /// <c>glImageBasic</c> writes it, both at +0x6c on both builds.</summary>
    public static readonly int TexBasicGlId = Field("CAurTextureBasic", "glId");

    /// <summary>#265's discriminator: the compressed byte count, zero for a sheet that came down the
    /// UNCOMPRESSED path. <c>ConstructImage</c> writes it, <c>BuildAndStoreTexture</c> and
    /// <c>GetMemoryUsage</c> read it -- +0x70 on both.</summary>
    public static readonly int TexBasicCompressedSize = Field("CAurTextureBasic", "compressedSize");

    /// <summary>Sheet dimensions and bytes per texel. <c>CheckAttributes</c> reads and rewrites all three
    /// on both builds, which is as clean a pair as this class offers.</summary>
    public static readonly int TexBasicWidth = Field("CAurTextureBasic", "width");
    /// <inheritdoc cref="TexBasicWidth"/>
    public static readonly int TexBasicHeight = Field("CAurTextureBasic", "height");
    /// <inheritdoc cref="TexBasicWidth"/>
    public static readonly int TexBasicBpp = Field("CAurTextureBasic", "bpp");

    /// <summary>The format/kind word, 4 for a PLT. <c>glImageBasic</c> takes the GPU-composite branch on
    /// <c>== 4</c> and nothing else, so this is the ONLY test separating a build Alloy must copy from one it
    /// has already uploaded (#282). <c>FreeImage</c> and <c>glImageBasic</c> read +0x84 on both.</summary>
    public static readonly int TexBasicPltKind = Field("CAurTextureBasic", "pltKind");

    /// <summary><c>uint*</c>, the <c>numX*numY</c> GL names <c>glImageBasic</c> generates, one per cell.
    /// <c>Reset</c> frees and nulls it at +0x50 on both builds.</summary>
    public static readonly int TexBasicCellNames = Field("CAurTextureBasic", "cellNames");

    /// <summary>The PLT layer scheme (<c>int*</c>) and its <c>short</c> count -- the other half of the
    /// engine's own texture identity. <c>SetScheme</c> writes both and <c>CalculateHashID</c> reads both,
    /// at +0x48/+0xd4 on either build.</summary>
    public static readonly int TexBasicSchemes = Field("CAurTextureBasic", "schemes");
    /// <inheritdoc cref="TexBasicSchemes"/>
    public static readonly int TexBasicSchemeCount = Field("CAurTextureBasic", "schemeCount");

    /// <summary>The <c>short</c> atlas grid the engine de-atlases into one GL texture per cell.
    /// <c>Reset</c> and the destructor read both on both builds (MSVC emits them in the other order, which
    /// is scheduling, not layout).</summary>
    public static readonly int TexBasicNumX = Field("CAurTextureBasic", "numX");
    /// <inheritdoc cref="TexBasicNumX"/>
    public static readonly int TexBasicNumY = Field("CAurTextureBasic", "numY");

    /// <summary>The mean-alpha cache and the animation controller pointer. The constructor writes the whole
    /// 0x28..0x37 region on both builds (Linux as two 8-byte stores, Windows as four dwords -- the #441
    /// lesson), <c>ConstructImage</c> rewrites +0x30 and <c>ParseField</c> walks +0x38, both at both
    /// ends.</summary>
    public static readonly int TexBasicAlphaMean = Field("CAurTextureBasic", "alphaMean");
    /// <inheritdoc cref="TexBasicAlphaMean"/>
    public static readonly int TexBasicController = Field("CAurTextureBasic", "controller");

    /// <summary>The RESOLVED texture name -- <c>char[0x20]</c> that <c>LoadImage</c> fills and
    /// <c>CAurTexture::Restore</c> re-acquires by, stable across a delete/rebuild, which is what makes it an
    /// identity (#210).
    /// <para>NOT <see cref="TexBasicName"/>: there are two 32-byte name fields, the constructor's requested
    /// name at +0x88 and this one at +0xa8, and both constructors write both runs (0x88..0xa7 and
    /// 0xa8..0xc7). #441's rule again -- two files naming "the name" at different offsets were two fields,
    /// not one error.</para></summary>
    public static readonly int TexBasicResolvedName = Field("CAurTextureBasic", "resolvedName");

    /// <summary>The four state bytes <c>UnbuiltDiag</c> reads: cube map, image constructed, "no image and no
    /// controller", and never-build. <c>Reset</c> writes 0xe6/0xe7 (Windows as ONE 2-byte store over the
    /// pair), <c>BuildAndStoreTexture</c> reads 0xe6/0xe8 and <c>glImageBasic</c> 0xe3 -- all on both
    /// builds.</summary>
    public static readonly int TexBasicFlagCube = Field("CAurTextureBasic", "flagCube");
    /// <inheritdoc cref="TexBasicFlagCube"/>
    public static readonly int TexBasicFlagBuilt = Field("CAurTextureBasic", "flagBuilt");
    /// <inheritdoc cref="TexBasicFlagCube"/>
    public static readonly int TexBasicFlagNoImage = Field("CAurTextureBasic", "flagNoImage");
    /// <inheritdoc cref="TexBasicFlagCube"/>
    public static readonly int TexBasicFlagNeverBuild = Field("CAurTextureBasic", "flagNeverBuild");

    // ---- CAurTexture's vtable: the ten accessors TexScan reads a sheet through (#219) ----
    // All -8 for #433's virtual-destructor reason EXCEPT GetCompressedSize(), where MSVC's overload
    // REVERSAL cancels the shift exactly. That is the trap #433 named, caught here by measuring rather
    // than by applying the rule: tools/abi/vtable-slots.py CAurTexture prints both tables.

    /// <summary><c>GetImage()</c> -- the pixel pointer every scan starts from.</summary>
    public static readonly int TexGetImageSlot = Field("CAurTexture", "vtbl.GetImage", Expect.VtblSlot);
    /// <summary><c>GetWidth()</c> / <c>GetHeight()</c> / <c>GetBPP()</c> -- the sheet's shape.</summary>
    public static readonly int TexGetWidthSlot = Field("CAurTexture", "vtbl.GetWidth", Expect.VtblSlot);
    /// <inheritdoc cref="TexGetWidthSlot"/>
    public static readonly int TexGetHeightSlot = Field("CAurTexture", "vtbl.GetHeight", Expect.VtblSlot);
    /// <inheritdoc cref="TexGetWidthSlot"/>
    public static readonly int TexGetBppSlot = Field("CAurTexture", "vtbl.GetBPP", Expect.VtblSlot);
    /// <summary><c>GetNumX()</c> / <c>GetNumY()</c> -- the atlas grid an uncompressed sheet is cut on.</summary>
    public static readonly int TexGetNumXSlot = Field("CAurTexture", "vtbl.GetNumX", Expect.VtblSlot);
    /// <inheritdoc cref="TexGetNumXSlot"/>
    public static readonly int TexGetNumYSlot = Field("CAurTexture", "vtbl.GetNumY", Expect.VtblSlot);
    /// <summary><c>GetCompressedSize()</c>, the no-argument overload -- and the one slot in this table that
    /// does NOT move. MSVC emits the overload set <c>(int,int)</c>-then-<c>()</c> where Itanium emits
    /// <c>()</c>-then-<c>(int,int)</c>, so the swap gives back exactly the pointer the virtual destructor
    /// took away. Identify it by signature; "the Nth GetCompressedSize" is meaningless.</summary>
    public static readonly int TexCompressedSizeSlot = Field("CAurTexture", "vtbl.GetCompressedSize", Expect.VtblSlot);
    /// <summary><c>GetNumMipHeaderBytes()</c> -- the header run to skip before the first texel.</summary>
    public static readonly int TexHeaderSkipSlot = Field("CAurTexture", "vtbl.GetNumMipHeaderBytes", Expect.VtblSlot);
    /// <summary><c>GetMipMap()</c>.</summary>
    public static readonly int TexMipMapSlot = Field("CAurTexture", "vtbl.GetMipMap", Expect.VtblSlot);
    /// <summary><c>GetCompressionFormat()</c> -- the index into <c>textureFormatTranslationTable</c>
    /// @0x12f5080 where 7 = DXT1 and 8 = DXT5.</summary>
    public static readonly int TexFormatSlot = Field("CAurTexture", "vtbl.GetCompressionFormat", Expect.VtblSlot);

    // ---- Scene's vtable: the slots RspShim calls through (#132/#232/#233/#235) ----
    // Displacements from the loaded vptr, exactly as Scene::RenderSinglePass uses them. All -8.

    /// <summary><c>Scene::DoBinBucket()</c> -- the per-bucket draw RSP runs for each of the sixteen.</summary>
    public static readonly int SceneDoBinBucketSlot = Field("Scene", "vtbl.DoBinBucket", Expect.VtblSlot);
    /// <summary><c>Scene::RenderBeams()</c> (#130).</summary>
    public static readonly int SceneRenderBeamsSlot = Field("Scene", "vtbl.RenderBeams", Expect.VtblSlot);
    /// <summary><c>Scene::DoEmitterBucket()</c> (#131).</summary>
    public static readonly int SceneDoEmitterBucketSlot = Field("Scene", "vtbl.DoEmitterBucket", Expect.VtblSlot);
    /// <summary><c>Scene::RenderLensFlares()</c>.</summary>
    public static readonly int SceneRenderLensFlaresSlot = Field("Scene", "vtbl.RenderLensFlares", Expect.VtblSlot);
    /// <summary><c>Scene::FadeDoors()</c> -- #132's frame anchor: the one RSP item that runs every frame.</summary>
    public static readonly int SceneFadeDoorsSlot = Field("Scene", "vtbl.FadeDoors", Expect.VtblSlot);
    /// <summary><c>Scene::DoFog()</c>.</summary>
    public static readonly int SceneDoFogSlot = Field("Scene", "vtbl.DoFog", Expect.VtblSlot);
    /// <summary><c>Scene::DoHighlightGob()</c> and <c>Scene::DoHighlightTriggers(bool)</c> -- #232/#233's
    /// two overlays, which RSP calls unconditionally and which self-gate inside.</summary>
    public static readonly int SceneDoHighlightGobSlot = Field("Scene", "vtbl.DoHighlightGob", Expect.VtblSlot);
    /// <inheritdoc cref="SceneDoHighlightGobSlot"/>
    public static readonly int SceneDoHighlightTriggersSlot = Field("Scene", "vtbl.DoHighlightTriggers", Expect.VtblSlot);

    /// <summary><c>LightManager::EnableLights()</c> -- RSP's very first call, and load-bearing: #234 found
    /// the owned pass dark without it.</summary>
    public static readonly int LightMgrEnableLightsSlot = Field("LightManager", "vtbl.EnableLights", Expect.VtblSlot);

    /// <summary><c>Gob::GetAttachedGobs(List&lt;CAurObject*&gt;&amp;)</c> -- fills a malloc'd
    /// {Gob** data, int count} the caller frees. NOT the head/tail list <c>Gob::CollectLights</c> walks.</summary>
    public static readonly int GobAttachedGobsSlot = Field("Gob", "vtbl.GetAttachedGobs", Expect.VtblSlot);

    // ---- the Scene highlight list, and the gob flag the walk reads (#232) ----

    /// <summary><c>Scene</c>'s highlight state as <c>Scene::AddHighlightGob</c> lays it out: a
    /// <c>CExoArrayList&lt;Gob*&gt;</c> (items +0x80, count +0x88) and a parallel RGB <c>Vector</c> array at
    /// +0x90, stride 0xc. <c>Scene::DoHighlightGob</c> reads all three at both ends, and both constructors
    /// write all three -- two pairs.</summary>
    public static readonly int SceneHiliteGobs = Field("Scene", "hiliteGobs");
    /// <inheritdoc cref="SceneHiliteGobs"/>
    public static readonly int SceneHiliteCount = Field("Scene", "hiliteCount");
    /// <inheritdoc cref="SceneHiliteGobs"/>
    public static readonly int SceneHiliteColors = Field("Scene", "hiliteColors");

    /// <summary>The gob byte <c>Scene::DoHighlightGob</c> tests before recursing into attached children.
    /// <c>Gob::SetPropagateHighlightEffects</c> is its only writer on either build, which is also what
    /// NAMES it: the pre-#443 comment called it "the visible byte", and the visible byte is
    /// <see cref="GobVisible"/> at +0x1a4.</summary>
    public static readonly int GobPropagateHilite = Field("Gob", "propagateHilite");

    // ---- CNWCAnimBase: the highlight CHANNELS (#336) ----
    // No CNWC* class is name-exported on Windows, so the class sweep dumps it EMPTY. #446 took #420's RVA
    // path instead: CNWCAnimBase::RefreshHilite is Linux @0x9cb260 / Windows rva 0x966170, paired on its own
    // ExoLog line ("nwcanimbase.cpp", 0x123a, "Attempting to unhighlight a gob in a null scene"), and all
    // three offsets are read off that one body on each build. No probe: Hilite writes the global
    // HiliteEnabled into the active flag rather than a literal 1, and a fresh CNWCAnimBase has every channel
    // zeroed -- a Byte01 here would pass on any offset that happens to be zero, and a probe that cannot fail
    // is not a probe.

    /// <summary>A hilite is written into one of five channel slots -- active flag at +0x9c[ch], colour at
    /// +0x60[ch] (12 bytes), "also add a glow gob" at +0xb0[ch] -- and <c>RefreshHilite</c> takes the LOWEST
    /// active slot as the winner. Tab and mouseover arrive on channel 2, every VFX on 3, which is why a Tab
    /// over a glowing creature shows the Tab colour. The Linux constructor writes +0x9c and +0xc8.</summary>
    public static readonly int AnimHiliteActive = Field("CNWCAnimBase", "hiliteActive");
    /// <inheritdoc cref="AnimHiliteActive"/>
    public static readonly int AnimHiliteGlow = Field("CNWCAnimBase", "hiliteGlow");
    /// <summary>CNWCAnimBase -&gt; its <c>Gob*</c>: the one the winning hilite channel is published against
    /// (<c>RefreshHilite</c> guards the whole body on it), and the one <c>CNWCAnimBase::SetPosition</c> @0x9c9d60
    /// / Windows rva 0x9680a0 forwards to (#663). With <see cref="ObjAnimBase"/> this is the id -&gt; Gob walk.</summary>
    public static readonly int AnimGob = Field("CNWCAnimBase", "gob");

    // ---- GLRender's pool descriptor table (#260) ----

    /// <summary>The 0x20-byte pool descriptor <c>GLRender::RequestPool</c> callocs: host shadow at +0,
    /// byte size +8, GL name +0xc, target index +0x10, lock flags +0x11, usage word +0x14, "uploaded once"
    /// +0x18, "named" +0x19.
    /// <para>Paired whole. Linux <c>GLRender::UnlockPool</c> @0x4cfc80 indexes
    /// <c>DAT_01ae5480 + i*0x20</c> and tests every one of those fields; the Windows UnlockPool forwards to

    /// eight displacements, same bit tests (<c>lockFlags &amp; 0x18</c>, <c>usage &gt;&gt; 8 &amp; 1</c>,
    /// <c>usage &amp; 2</c>).</para></summary>
    public static readonly int PoolStride = Field("GLRender", "poolStride");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolSize = Field("GLRender", "poolSize");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolName = Field("GLRender", "poolName");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolTarget = Field("GLRender", "poolTarget");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolLockFlags = Field("GLRender", "poolLockFlags");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolUsage = Field("GLRender", "poolUsage");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolUploaded = Field("GLRender", "poolUploaded");
    /// <inheritdoc cref="PoolStride"/>
    public static readonly int PoolNamed = Field("GLRender", "poolNamed");

    /// <summary>The descriptor count, one pointer above the table base. The same {base, count} pair on both:
    /// Linux reads the two adjacent globals <c>DAT_01ae5480</c>/<c>DAT_01ae5488</c>, Windows reads

    public static readonly int PoolTableCount = Field("GLRender", "poolTableCount");

    /// <summary>Byte offset from <see cref="SymbolMap.GlPoolTableAnchor"/> to that {base, count} pair.
    /// <para>#447: the row wanted an ANCHOR, not a value, and it got one. Linux has no symbol on the pair,
    /// so it anchors on <c>noemittervar</c> and steps 0x236c; Windows names the pair itself
    /// (<c>At(0x128ead8)</c>, measured off <c>GLRender::GetPoolBuffer</c>'s <c>mov rax,[rip+…]</c> and
    /// <c>UnlockPool</c>'s <c>lea rcx,[rip+…]</c>), so the step is zero. Same two globals, two ways of
    /// naming them -- which is what <c>Diverges</c> is for.</para></summary>
    public static readonly int PoolTableAnchorOffset =
        Field("GLRender", "poolTableAnchorOffset");

    // ---- GLRender::m_shaderExposedRuntimeData (#123/#124/#347) ----
    // The struct the engine's own SetUniforms uploads its scalar shader inputs from. Nothing takes it as
    // `this`, so it is read here the way #441 read the fade fields: off GLRender::SetUniforms decompiled on
    // both builds (@0x4ccbf0 / @0xb8380). The symbol is exported at Linux 0x19e1ea0 and Windows rva
    // 0x128ea10, and every displacement below appears in SetUniforms on BOTH builds at the SAME offset --
    // the field-by-field pairing this batch could not get out of the PROG_FIXED slot map alone.

    // #352: the module calendar the content post chain reads (rays/flare derive the sun from it). Paired
    // by NAME through ConnectShaderData's slot map: slot +0xb08 is "moduleHour" and GLRender::SetUniforms feeds it the
    // struct's first int; +0xb04 "moduleDay" <- +0x04, +0xb00 "moduleMonth" <- +0x08, +0xb0c
    // "moduleTimeIntoTransition" <- +0x10 (float). Identical displacements in the Windows SetUniforms
    // (@0xb8380, _m_shaderExposedRuntimeData rva 0x128ea10).
    /// <summary><c>moduleHour</c>, the calendar hour the shaders see.</summary>
    public static readonly int RtModuleHour = Field("ShaderExposedRuntimeData", "moduleHour");
    /// <summary><c>moduleDay</c>.</summary>
    public static readonly int RtModuleDay = Field("ShaderExposedRuntimeData", "moduleDay");
    /// <summary><c>moduleMonth</c>.</summary>
    public static readonly int RtModuleMonth = Field("ShaderExposedRuntimeData", "moduleMonth");
    /// <summary><c>moduleTimeIntoTransition</c> (float), seconds into the current hour transition.</summary>
    public static readonly int RtModuleTimeIntoTransition = Field("ShaderExposedRuntimeData", "moduleTimeIntoTransition");
    /// <summary>Time of day, 0..1 -- what the sky and the wind phase run off.</summary>
    public static readonly int RtTimeOfDay = Field("ShaderExposedRuntimeData", "timeOfDay");
    /// <summary>The area wind vector (#123).</summary>
    public static readonly int RtAreaWind = Field("ShaderExposedRuntimeData", "areaWind");
    /// <summary>The area flag word the roof-fade and interior tests read.</summary>
    public static readonly int RtAreaFlags = Field("ShaderExposedRuntimeData", "areaFlags");
    /// <summary>The controlled character's position.</summary>
    public static readonly int RtPlayerPos = Field("ShaderExposedRuntimeData", "playerPos");
    /// <summary>The camera EYE position.</summary>
    public static readonly int RtCamPos = Field("ShaderExposedRuntimeData", "camPos");
    /// <summary>Camera pitch and player-camera distance -- #124's keyhole aperture inputs.</summary>
    public static readonly int RtCamPitch = Field("ShaderExposedRuntimeData", "camPitch");
    /// <inheritdoc cref="RtCamPitch"/>
    public static readonly int RtCamDist = Field("ShaderExposedRuntimeData", "camDist");
    /// <summary>#347: the camera PIVOT, uploaded under the name <c>cameraFocus</c>. Not the eye
    /// (<see cref="RtCamPos"/>): in chase cam the pivot sits on the controlled character at roughly chest
    /// height, which is what content measures a sight range against.</summary>
    public static readonly int RtCamFocus = Field("ShaderExposedRuntimeData", "camFocus");

    // ---- the rest ----

    /// <summary>The part's self-illumination colour, <c>PartTriMesh::MatStatic</c>'s prop-3 material term.
    /// <para>#443 filed this in the GAP between PartTriMesh's +0x4 bbox segment and its +0x8 node segment,
    /// because interpolating across a boundary is the Camera +0x4d4 mistake. #448 measured it instead of
    /// interpolating: <c>PartTriMesh::Mat</c> (@0x450960 / rva 0xa2f50) loads three floats off this field
    /// into the emission scratch and hands them to <c>SetMaterialv(0, 3)</c> on both builds -- Linux from
    /// 0x7c, Windows from 0x80. So the field is +0x4 with the bbox, and the extra four bytes the boundary
    /// adds are the alignment pad Windows needs between the float block ending at 0x8c and the 8-aligned
    /// <see cref="PartNode"/> pointer at 0x90; the constructors' store runs say the same.</para></summary>
    public static readonly int PartSelfIllum = Field("PartTriMesh", "selfIllum");

    /// <summary>The node material colours <c>PartTriMesh::MatStatic</c> @0x450560 hands to
    /// <c>SetMaterialv</c>: prop 2 takes +0xe8 and prop 1 takes +0xf4, so the enum runs
    /// ambient/diffuse/emissive and +0xe8 is the DIFFUSE term -- the one carrying the part's alpha in
    /// fixed-function material setup. Both <c>MdlNodeTriMesh</c> constructors write the same run
    /// (0xe8, 0xf0, 0xf4, 0xfc, 0x100, 0x108, 0x10c) on both builds.
    /// <para>Filed under the subclass that declares them, not <c>MdlNode</c>: #436's rule is that a class
    /// verdict does not travel, and it does not travel downward either.</para></summary>
    public static readonly int MdlDiffuse = Field("MdlNodeTriMesh", "diffuse");
    /// <inheritdoc cref="MdlDiffuse"/>
    public static readonly int MdlAmbient = Field("MdlNodeTriMesh", "ambient");

    /// <summary>Stride between the texture slots of a <c>Material</c> -- a <c>SafePointer</c>, pointer plus
    /// a refcount int. The constructor writes slots at 0x00/0x10/../0x60 on both builds and Windows
    /// <c>ClearTextures</c> walks each one's +8 refcount, so 0x10 is measured rather than assumed.</summary>
    public static readonly int MatSlotStride = Field("Material", "slotStride");

    /// <summary>Where a <c>SharedMaterial</c>'s own eleven texture slots begin -- they win over the
    /// Material's three. <c>SharedMaterial::BindAllStandardTextures</c> reads +0xd8 on both builds; the
    /// stride between slots is <see cref="MatSlotStride"/>, measured on the Material array.</summary>
    public static readonly int SharedSlot0 = Field("SharedMaterial", "slot0");

    /// <summary>Stride between the three shader-name resref slots <see cref="SharedVsName"/> names, and the
    /// length of each inline NUL-terminated buffer.
    /// <para>The SharedMaterial constructor writes them at 0x188 / 0x199 / 0x1aa on BOTH builds -- 0x11
    /// apart either way. #441 read Windows' extra stores at 0x190/0x1a1/0x1b2 as an unexplained +0x8; they
    /// are MSVC splitting each 17-byte copy into two 8-byte stores, the same shape #441 itself identified
    /// in the CAurTextureBasic constructor.</para></summary>
    public static readonly int SharedNameLen = Field("SharedMaterial", "resrefStride");

    /// <summary><c>sizeof(CRes)</c> -- what <c>CExoResMan::Get</c> passes to operator new. Both
    /// constructors' highest write is <c>0x58:8</c>, so the object is 0x60 on both.</summary>
    public static readonly int SizeOfCRes = Field("CRes", "sizeof");
    /// <summary><c>CRes::GetData</c> and <c>CRes::GetSize</c> -- the raw file bytes of a serviced .shd and
    /// their length. One-line accessors reading +0x20 and +0x30 on both builds: the cleanest pair in the
    /// batch.</summary>
    public static readonly int CResData = Field("CRes", "data");
    /// <inheritdoc cref="CResData"/>
    public static readonly int CResSize = Field("CRes", "size");

    // ---- the occlusion fade (#537): the camera flags, the hit record, the gob's object kind ----

    /// <summary>CNWCModule -&gt; the <c>CAMERA_FLAG_*</c> bitmask the server's <c>SetCameraFlags</c> lands in;
    /// bit <see cref="CameraFlagDisableCollision"/> is what <c>UpdateCameraCollision</c> tests. Paired on
    /// that function: Linux @0x7dce30 and Windows rva 0x7d07d0 both read <c>+0x140 == 0 &amp;&amp;
    /// (byte)+0x2bc &amp; 2</c> before dispatching on <c>CClientOptions+0x1d</c> (#537).</summary>
    public static readonly int ModuleCameraFlags = Field("CNWCModule", "cameraFlags");

    // ---- camera-relative drive (#540): UpdateDriveMode's own fields. Linux @0x65f780 and Windows rva 0x6f1e40
    //      read every one of these at the SAME offsets (one decompile per build, tools/ghidra/DecompOne.java);
    //      the creature rows below are the two the function gates on, shifted -0x20 on Windows like `plated`. ----

    /// <summary>CClientExoAppInternal -&gt; "drive input changed" latch (int 0/1): HandleInputEvent raises it on
    /// any drive key edge, UpdateDriveMode sends <c>DriveControl</c> while it is up and clears it. No Byte01
    /// probe: the ctor zeroes +0x1b4.. but never +0x1b0, so before the first UpdateDriveMode returns it holds heap
    /// garbage and a probe there fails at random (the "camera drive stopped working" of 2026-09-16).</summary>
    public static readonly int InternalDriveChanged = Field("CClientExoAppInternal", "driveChanged");
    /// <summary>CClientExoAppInternal -&gt; the walk-forward key (int 0/1): keymap action 0x59 "Drive Mode Forward",
    /// held like the six below (press/release, release aborts). UpdateDriveMode reads it only while
    /// <see cref="InternalDriveKeys"/>[0] is clear and then flags walk (0x01), or run (0x03) when
    /// <c>CClientOptions::m_bAlwaysRun</c> (<c>[[app]+4]</c>) is set. Not an auto-run toggle (#637).</summary>
    public static readonly int InternalDriveWalk = Field("CClientExoAppInternal", "driveWalk", Expect.Byte01);
    /// <summary>CClientExoAppInternal -&gt; six int key booleans in a row: forward run (action 0x5a "Drive Mode Forward
    /// Run"; flags run 0x03, or walk 0x01 under the Shift walk modifier <c>creature+0x2b0</c>), backward, turn left,
    /// turn right, strafe left, strafe right (+0x1b8..+0x1cc). <c>ClearDriveMode</c> zeroes +0x1b4..+0x1cf in one go.</summary>
    public static readonly int InternalDriveKeys = Field("CClientExoAppInternal", "driveKeys", Expect.Byte01);
    /// <summary>CClientExoAppInternal -&gt; microsecond stamp of the last drive packet (the 500 ms keep-alive
    /// compares against it); rewritten on every send, so a change across the call means a packet went out.</summary>
    public static readonly int InternalDriveLastSend = Field("CClientExoAppInternal", "driveLastSend");
    /// <summary>CNWCObject -&gt; orientation Vector (x, y, z); position is the Vector at +0x38. UpdateDriveMode reads
    /// +0x44/+0x48 for the wire angle and TurnOnSpot rotates +0x44..+0x4c on both builds.</summary>
    public static readonly int ObjOrientation = Field("CNWCObject", "orientation");
    /// <summary>CNWCObject -&gt; position Vector, the 12 bytes before <see cref="ObjOrientation"/> (<c>SetPosition</c>
    /// Linux @0x7e9180 writes +0x38..+0x40; the Windows orientation row at the same +0x44 pins it).</summary>
    public static readonly int ObjPosition = Field("CNWCObject", "position");
    // ---- action-camera aim assist / auto-target / contextual crosshair (docs/research/action-camera-targeting.md) ----
    // Every row below was read off ONE function pair: CNWCArea::HiliteAreaObjects (Linux @0x7119f0, Windows rva
    // 0x7db2f0), CClientExoAppInternal::UpdateMouseCursorAppearance (Linux @0x6629b0, Windows rva 0x6f2830),
    // CActionTarget::HandleLMouseDown (Linux @0x965840, Windows rva 0x83be00) and
    // CCachedMouseCursorManager::SetMouseCursor (Linux @0x6b72a0, Windows rva 0x83b3e0).

    /// <summary>CNWCObject -&gt; the live fade state (ONE byte, 1 = faded in / on screen). The highlight walk's own
    /// first filter: <c>HiliteAreaObjects</c> skips the object while it is 0 -- Linux @0x711a6d is
    /// <c>cmpb $0x0,0xa8(%rax)</c> and Windows rva 0x7db2f0 reads <c>(char)obj[0x15]</c>, a byte on both. The
    /// writers are <c>CNWCObject::FadeObject</c> (@0x7e7720: it walks the alpha toward the desired state at
    /// <c>+0xa9</c> and stores 0/1 here when it arrives) and <c>SetDesiredFadeState</c>. Read it as a byte: the
    /// three bytes above it are the desired state and padding, not a pointer's tail (which is also why it takes
    /// no <c>Byte01</c> probe: that one reads a dword, and <c>+0xa9</c> would fail it).</summary>
    public static readonly int ObjVisible = Field("CNWCObject", "visible");
    /// <summary>CNWCObject -&gt; "may be highlighted right now" (int). Same walk: zero and the engine un-hilites
    /// instead of hiliting. Identical offset on both builds. <b>Placeables carry it elsewhere</b> -- the type-9
    /// branch reads it through <see cref="GameObjAsNWCPlaceableSlot"/>, not on the CNWCObject.</summary>
    public static readonly int ObjHighlightable = Field("CNWCObject", "highlightable");
    /// <summary>CNWCArea -&gt; the area's object-id array (<c>uint*</c>) and its count. <c>HiliteAreaObjects</c>
    /// walks <c>*(uint*)(*(area+0x318) + i*4)</c> for <c>*(int*)(area+0x320)</c> entries on both builds.</summary>
    public static readonly int AreaObjectIds = Field("CNWCArea", "objectIds");
    /// <inheritdoc cref="AreaObjectIds"/>
    public static readonly int AreaObjectCount = Field("CNWCArea", "objectCount");
    /// <summary>#659/#664: CNWCArea -&gt; the placeable-light registry: an array of pointers (stride 8) to
    /// 16-byte <c>CNWCPlaceableObjectLight</c> records {uint object id @+0, Gob* @+8}, count at +0x330.
    /// <c>CNWCArea::AddPlaceableObjectLight</c> @0x705120 / <c>GetPlaceableObjectLight</c> @0x7057e0 walk
    /// exactly this. Each Gob is a one-node <c>fx_placeable01</c> that owns one PartLight and no mesh.</summary>
    public static readonly int AreaPlaceableLights = Field("CNWCArea", "placeableLights");
    /// <inheritdoc cref="AreaPlaceableLights"/>
    public static readonly int AreaPlaceableLightCount = Field("CNWCArea", "placeableLightCount");
    /// <summary>#659/#674: one registry record's <c>Gob*</c> (the object id is at +0). Same on both binaries:
    /// <c>AddPlaceableObjectLight</c> writes the pair at +0/+8 on the PE decompile too (rva 0x7d8ed0).</summary>
    public static readonly int PlaceableLightGob = Field("CNWCPlaceableObjectLight", "gob");
    /// <summary>#663/#672: CNWCArea -&gt; the Static-placeable list, a <c>CExoArrayList&lt;unsigned int&gt;</c> of
    /// object ids (data +0x338, count +0x340) filled by <c>AddPlaceableObjectStatic</c> @0x7091c0. No per-object
    /// Static flag exists client-side (+0x1b4 is PLOT); membership here IS the static set. docs/engine-layout.md.</summary>
    public static readonly int AreaStaticPlaceables = Field("CNWCArea", "staticPlaceables");
    /// <inheritdoc cref="AreaStaticPlaceables"/>
    public static readonly int AreaStaticPlaceableCount = Field("CNWCArea", "staticPlaceableCount");
    /// <summary>#663: CNWCObject -&gt; its <c>CNWCAnimBase*</c> (<c>CNWCObject::SetPosition</c> @0x7e9180 / rva
    /// 0x766760 reads it before the anim-base vcall). Hop one of id -&gt; placeable -&gt; anim base -&gt;
    /// <see cref="AnimGob"/>; the gob's composed transform is then <see cref="NodeMatrix"/> off its parts.</summary>
    public static readonly int ObjAnimBase = Field("CNWCObject", "animBase");
    /// <summary>CNWCCreature -&gt; the hovered object id mirrored onto the PLAYER creature (0x7f000000 = nothing).
    /// <c>UpdateMouseCursorAppearance</c> is the only writer: it stores the newly picked id there and clears it to
    /// 0x7f000000 when the hover drops. Linux +0x448, Windows +0x428 -- the same -0x20 CNWCCreature delta
    /// <see cref="CreaturePlated"/> and the nameplate rows carry.</summary>
    public static readonly int CreatureHoveredOid = Field("CNWCCreature", "hoveredOid");
    /// <summary>CClientExoAppInternal -&gt; CCachedMouseCursorManager*. Every <c>SetMouseCursor</c> call site in
    /// <c>UpdateMouseCursorAppearance</c> passes <c>*(internal + 0xc8)</c> on both builds.</summary>
    public static readonly int InternalCursorManager = Field("CClientExoAppInternal", "cursorManager");
    /// <summary>CCachedMouseCursorManager -&gt; the live cursor id (int), the one <c>SetMouseCursor</c> early-outs on
    /// and then stores. 1 default, 3 walk, 7 attack, 0x1d magic, 0x5b pickup... (the id table is in the research
    /// doc); 0x125 is the ceiling the setter enforces. Same offset on both builds.</summary>
    public static readonly int CursorCurrentId = Field("CCachedMouseCursorManager", "currentCursor");
    /// <summary>CActionTarget -&gt; the allowed-target array: pairs of int {objectType, cursorId}, stride 8, count
    /// at <see cref="ActionTargetAllowedCount"/>. Type 2 is the synthetic "ground point" (AoE); the rest are the
    /// engine object types. Read off both <c>HandleLMouseDown</c> bodies -- Linux scans <c>+0x148</c> for
    /// <c>+0x150</c> entries, Windows <c>+0x108</c> for <c>+0x110</c> (MSVC packs the CGuiCallback members in 16
    /// bytes where Itanium needs 24, so everything past +0x80 shifts).</summary>
    public static readonly int ActionTargetAllowed = Field("CActionTarget", "allowed");
    /// <inheritdoc cref="ActionTargetAllowed"/>
    public static readonly int ActionTargetAllowedCount = Field("CActionTarget", "allowedCount");
    /// <summary>CClientExoAppInternal -&gt; the live <c>CActionTarget*</c>: <c>RegisterActionTargetGUI</c> (Linux
    /// @0x645530, Windows rva 0x6de8e0) is a one-line store to +0x108 on both, called with <c>this</c> from
    /// <c>ActivateTargetMode</c> and with null from <c>Cleanup</c> (both the fire and the cancel path), so it is
    /// the "is this mode still up" read a deferred cast checks before touching the pointer again (CX-636).</summary>
    public static readonly int InternalActionTarget = Field("CClientExoAppInternal", "actionTarget");
    /// <summary>CNWCCreature -&gt; the server-sent attitude byte: 1 friend, 0 neutral, anything else (2) hostile.
    /// <c>HiliteAreaObjects</c> picks the enemy/friend/neutral colour on it and <c>PerformDefaultActionOnCreature</c>
    /// talks below 2 and attacks at 2. Linux +0x3a8, Windows +0x388 (the Windows HiliteAreaObjects, rva 0x7db2f0,
    /// same three-way branch).</summary>
    public static readonly int CreatureAttitude = Field("CNWCCreature", "attitude");
    /// <summary>CNWCObject -&gt; the script highlight colour override (Vector; x &lt; 0 = none), the first thing every
    /// <c>HiliteAreaObjects</c> branch tests before falling back to the type's global. Same offset on both.</summary>
    public static readonly int ObjHiliteColour = Field("CNWCObject", "hiliteColour");
    /// <summary>Virtual slots of <c>CNWCObject::Hilite(Vector, uchar channel, float, int glow)</c> and
    /// <c>UnHilite(uchar channel)</c>, called through the vtable because CNWCCreature overrides both.
    /// <c>HiliteAreaObjects</c> calls Linux <c>vtbl+0x160</c>/<c>+0x168</c>, Windows <c>+0x158</c>/<c>+0x160</c>
    /// (rva 0x7db2f0), with <c>(colour, 2, 0.0f, 1)</c> and <c>(2)</c>. The Vector crosses by value: the
    /// <c>ColourArg</c> shape of #420 (SysV xmm0/xmm1, Win64 a pointer to a copy).</summary>
    public static readonly int ObjHiliteSlot = Field("CNWCObject", "vtbl.Hilite", Expect.VtblSlot);
    /// <inheritdoc cref="ObjHiliteSlot"/>
    public static readonly int ObjUnHiliteSlot = Field("CNWCObject", "vtbl.UnHilite", Expect.VtblSlot);
    /// <summary>Virtual slots of <c>CNWCObject::MouseOver(int)</c> and <c>MouseOff()</c>: the creature and door arms
    /// of <c>HiliteAreaObjects</c> call Linux <c>vtbl+0x110</c> / <c>+0x118</c>, Windows <c>+0x108</c> / <c>+0x110</c>
    /// (rva 0x7db2f0), with <c>(this, 1)</c> on the way in and <c>(this)</c> on the way out. MouseOver(1) is what
    /// raises the feedback plate (<c>ShowFeedbackBubbleText</c>, the flag <see cref="CreaturePlated"/>), MouseOff
    /// what drops it -- the tab-target lock's plate (CX-636).</summary>
    public static readonly int ObjMouseOverSlot = Field("CNWCObject", "vtbl.MouseOver", Expect.VtblSlot);
    /// <inheritdoc cref="ObjMouseOverSlot"/>
    public static readonly int ObjMouseOffSlot = Field("CNWCObject", "vtbl.MouseOff", Expect.VtblSlot);
    /// <summary>Virtual slot of <c>CGameObject::AsNWCObject()</c> -- what the area walk calls on every id it
    /// resolves. Linux <c>vtbl+0x20</c>, Windows <c>vtbl+0x18</c>: the usual Itanium two-dtor-slot shift.</summary>
    public static readonly int GameObjAsNWCObjectSlot = Field("CGameObject", "vtbl.AsNWCObject", Expect.VtblSlot);
    /// <summary>Virtual slot of <c>CGameObject::AsNWCPlaceable()</c> -- the walk's placeable branch tests
    /// <see cref="ObjHighlightable"/> on what THIS answers rather than on the CNWCObject (Linux @0x711b17 calls
    /// <c>vtbl+0xa8</c> then reads <c>+0x11c</c> on the result, Windows rva 0x7db2f0 the same through
    /// <c>vtbl+0xa0</c>). The base answers null and <c>CNWCPlaceable</c>'s override is an identity
    /// <c>return this</c> (@0x7ecc20), so it is a type test that happens to hand back the same address; mirroring
    /// it keeps a non-placeable carrying type byte 9 out. The -8 is the usual MSVC one-destructor-slot shift.</summary>
    public static readonly int GameObjAsNWCPlaceableSlot = Field("CGameObject", "vtbl.AsNWCPlaceable", Expect.VtblSlot);

    // ---- what the player is doing right now: the action camera's crosshair feedback (docs/research/action-camera-feedback.md) ----
    // Measured 2026-09-17 by decompiling the same function on both programs, never by extrapolating a class delta.

    /// <summary>CGuiInGame -&gt; CPanelActionQueue*, the client's mirror of the player's action queue. Both
    /// <c>HandleServerToPlayerUpdate_PlayerInfo</c> bodies (Linux @0x8a9f90, Windows rva 0x7b4f70) null-check
    /// <c>GetInGameGui()+0xa8</c> and hand exactly that to <c>ClearAllActions</c> / <c>AddAction</c>.</summary>
    public static readonly int GuiInGameActionQueue = Field("CGuiInGame", "actionQueuePanel");

    /// <summary>CPanelActionQueue -&gt; the "Current" button slot (<c>CPanelActionQueueButton*</c>, null = idle).
    /// <c>AddAction</c> fills it first and only overflows into the queued list, so "an action is pending" is this
    /// plus <see cref="ActionQueueCount"/>. Linux @0x7761b0 stores to <c>+0x158</c>, Windows rva 0x873df0 to
    /// <c>+0x150</c>.</summary>
    public static readonly int ActionQueueCurrent = Field("CPanelActionQueue", "current");

    /// <summary>CPanelActionQueue -&gt; the queued-button count (int), the list at <c>current-8</c>'s array.
    /// <c>ClearAllActions</c> drains <c>while (0 &lt; count)</c> and <c>DeleteAction</c> early-outs on
    /// <c>count &lt; 1</c> on both builds (Linux @0x776100 / @0x776020, Windows rva 0x874670 / 0x8748b0).</summary>
    public static readonly int ActionQueueCount = Field("CPanelActionQueue", "queuedCount");

    /// <summary>CNWCCreature -&gt; the server-sent AI state byte; <b>2 = attacking</b> and nothing else in the
    /// enumeration is established. Two independent call sites on each build:
    /// <c>WalkPlayerToPoint</c> (Linux @0x763950, Windows rva 0x6f3650) gates its "already close to my target"
    /// test on <c>== 2</c>, and <c>CPanelQuickBar::UpdateAttackButtons</c> paints IR_DISENGAGE on it.</summary>
    public static readonly int CreatureAiState = Field("CNWCCreature", "aiState");

    /// <summary>CNWCCreature -&gt; whom that creature is attacking (object id), written by
    /// <c>CreatureUpdate_Update</c> only while <see cref="CreatureAiState"/> is 2. Both
    /// <c>WalkPlayerToPoint</c> bodies resolve <c>GetGameObject(*(uint*)(creature + this))</c> inside the
    /// <c>== 2</c> branch.</summary>
    public static readonly int CreatureAiTarget = Field("CNWCCreature", "aiTarget");

    /// <summary>CNWCObject -&gt; current animation (short): <c>GetCurrentAnimation</c> is <c>*(short*)(this+0x5c)</c> on
    /// both builds (Linux @0x7e3f70, Windows rva 0x7630d0, the chase driver's own callee). Locomotion = 2, 3, 4,
    /// 0x4e, 0x4f, 0x54..0x57 (the chase drivers' test).</summary>
    public static readonly int ObjAnimation = Field("CNWCObject", "animation");
    /// <summary>CNWCCreature -&gt; the server's <c>m_nAIState</c> WORD (docs/research/action-camera-feedback.md §2.1):
    /// UpdateDriveMode and DrivePlayer both refuse unless bit 2 is set. <b>0 = inert</b>: the creature update
    /// handler puts a creature whose word is 0 (and has no loot) on the hit-check ignore list and the click
    /// default treats <c>== 0</c> as a corpse to loot, so the tab-target lock reads 0 as "dead" (CX-636).
    /// Linux +0x39c, Windows +0x37c.</summary>
    public static readonly int CreatureAiFlags = Field("CNWCCreature", "aiFlags");
    /// <summary>CNWCCreature -&gt; int that must be non-zero for drive input to act at all (UpdateDriveMode's second
    /// gate). Linux +0x43c, Windows +0x41c.</summary>
    public static readonly int CreatureDriveEnabled = Field("CNWCCreature", "driveEnabled");
    /// <summary>CNWCCreature -&gt; the local facing target Vector the model turns toward: DrivePlayer's ±30° turn arm
    /// writes it alone, and UpdateDriveMode writes it together with <see cref="CreatureFacingTargetB"/> right after
    /// every drive packet. Linux +0x2c8, Windows +0x2a8.</summary>
    public static readonly int CreatureFacingTarget = Field("CNWCCreature", "facingTarget");
    /// <summary>CNWCCreature -&gt; the second Vector of that pair (+0x2d4 / +0x2b4), written with the same direction on
    /// the send path and by TurnOnSpot; its own reader is not identified.</summary>
    public static readonly int CreatureFacingTargetB = Field("CNWCCreature", "facingTargetB");
    /// <summary>CNWCCreature -&gt; the desired position Vector: <c>WalkUpdateLocation</c> steps it along the waypoints each
    /// frame (WalkUpdateLocationDistance starts from it) and <c>MoveCreatureToPosition</c> lerps the displayed +0x38
    /// toward it; the update handler and EvaluateNewWayPoints overwrite it from the server. Every drive line test
    /// starts here. Linux +0x2e0, Windows +0x2c0 (the WalkUpdateLocation bodies, CX-682).</summary>
    public static readonly int CreatureDesiredPos = Field("CNWCCreature", "desiredPos");
    /// <summary>CNWCCreature -&gt; int 0/1 "I am driving": set by ClientSideWalkCommand in drive mode and by a server
    /// update that says so, cleared when the walk ends. Linux +0x318, Windows +0x2f8. No Byte01 probe: the ctor
    /// is not known to zero it.</summary>
    public static readonly int CreatureDriving = Field("CNWCCreature", "driving");
    /// <summary>CNWCCreature -&gt; pointer to the creature's pathfind block (the object whose +0xcc ushort indexes
    /// creaturespeed.2da). Linux +0x320, Windows +0x300.</summary>
    public static readonly int CreaturePathfind = Field("CNWCCreature", "pathfind");
    /// <summary>Pathfind block -&gt; two consecutive floats (+0xe0, +0xe4) that ClientSideWalkCommand, WalkPlayerToPoint
    /// and EvaluateNewWayPoints hand <c>CNWCArea::TestDirectLine</c> as its 4th and 5th arguments (personal space and
    /// height, order as the engine passes them). Same on both builds (WalkPlayerToPoint rva 0x6f3650).</summary>
    public static readonly int PathfindSpaceArgs = Field("CPathfindInformation", "spaceArgs");
    /// <summary>Virtual slot of <c>CNWCCreature::SetPosition(Vector)</c>: MoveCreatureToPosition calls Linux
    /// <c>vtbl+0x130</c> (@0x71cd70), Windows <c>+0x128</c> (rva 0x804840), the class's usual -8. The Vector crosses by
    /// value (SysV xmm0/xmm1, Win64 a pointer to a copy).</summary>
    public static readonly int CreatureSetPositionSlot = Field("CNWCCreature", "vtbl.SetPosition", Expect.VtblSlot);
    /// <summary>CNWCModule -&gt; "chase suppressed" (int 0/1): <c>UpdateCameraModeChaseCam/EQ</c> set it the frame the
    /// user moved the camera (+0x134) and skip the chase while it holds; cleared on the creature's next
    /// idle-to-locomotion edge, which <see cref="ModuleChaseWasIdle"/> records. Same on Windows (rva 0x7d0d00 /
    /// 0x7d1c90 read all four).</summary>
    public static readonly int ModuleChaseSuppressed = Field("CNWCModule", "chaseSuppressed", Expect.Byte01);
    /// <summary>CNWCModule -&gt; "creature was idle" (int 0/1) the chase drivers set on a non-locomotion animation and
    /// consume (clearing <see cref="ModuleChaseSuppressed"/>) on the first locomotion frame after.</summary>
    public static readonly int ModuleChaseWasIdle = Field("CNWCModule", "chaseWasIdle", Expect.Byte01);
    /// <summary>CNWCModule -&gt; the server's <c>SetCameraLimits</c> max distance (float; -1 = not set, use the client
    /// config). Linux @0x7def00 writes +0x2ac..+0x2b8 (min pitch, max pitch, min dist, max dist); Windows rva
    /// 0x7cf9d0 the same four. #543's leash radius when set.</summary>
    public static readonly int ModuleCameraMaxDist = Field("CNWCModule", "cameraMaxDist");
    /// <summary>CNWCCreature -&gt; combat state int, server-fed: <c>SetCombatState(int)</c> (Linux @0x722160, Windows
    /// rva 0x80a230, the callee right after the +0x39c/+0x37c cluster in HandleServerToPlayerCreatureUpdate_Update)
    /// stores it first thing. #543 stands the free camera down on it.</summary>
    public static readonly int CreatureCombatState = Field("CNWCCreature", "combatState");
    /// <summary>CGuiInGame -&gt; "toggled off" int: <c>ToggleOff</c> returns early when set, <c>UnToggleOff</c> when
    /// clear, and <c>SetCutsceneMode</c> reads it to know whether the exit should un-toggle. Linux +0x3d4, Windows +0x374.</summary>
    public static readonly int GuiInGameToggledOff = Field("CGuiInGame", "toggledOff", Expect.Byte01);

    // ---- head tracking: the LookAtAnimation the engine already owns ----
    // One function pair carries every row: LookAtAnimation::Control (Linux @0x4973c0, Windows rva 0x50650, the
    // name-exported ?Control@LookAtAnimation@@UEAAXXZ) reads all four, and the ctor (Linux @0x49d480, Windows
    // rva 0x4d0a0) writes them in the same order on both builds. The class is laid out identically.

    /// <summary>CNWCCreature -&gt; virtual slot of <c>GetModel(unsigned char, int)</c>, which answers the
    /// creature's Gob (<c>*(animBase + 0xc8)</c>, <see cref="AnimGob"/>). Measured on the one function
    /// that calls it on both builds, <c>CNWCCreature::LookAt</c> (Linux @0x726890 uses +0x148, Windows rva
    /// 0x803f20 uses +0x140): the -8 every virtual destructor costs MSVC.</summary>
    public static readonly int CreatureGetModelSlot =
        Field("CNWCCreature", "vtbl.GetModel", Expect.VtblSlot);

    /// <summary>LookAtAnimation -&gt; the "finished" byte <c>Deactivate</c> sets and <c>Gob::Animate</c> deletes
    /// the object on. Control's own gate: set, the aim block is skipped for the release latch at +0x68.</summary>
    public static readonly int LookAtFinished = Field("LookAtAnimation", "finished");
    /// <summary>LookAtAnimation -&gt; the looker's <c>Gob*</c>: the distance check reads its position at +0xa8 on
    /// both builds, and the ctor stores its third argument here.</summary>
    public static readonly int LookAtGob = Field("LookAtAnimation", "gob");
    /// <summary>LookAtAnimation -&gt; the looker's head <c>Part*</c> (HEAD_NAME's node). Control calls
    /// <c>Part::GetWorld</c> through it and copies its local rotation (<see cref="PartRot"/>) into the first
    /// keyframe.</summary>
    public static readonly int LookAtHeadPart = Field("LookAtAnimation", "headPart");
    /// <summary>LookAtAnimation -&gt; <c>float*</c> into the synthetic animation's second keyframe: the four
    /// floats Control writes as <c>(x, y, z, w)</c>, which <c>AnimateHierarchy</c> then blends onto the head
    /// part. This is the seam the head aim is overwritten at.</summary>
    public static readonly int LookAtTargetKey = Field("LookAtAnimation", "targetKey");

    /// <summary>Gob -&gt; the kind of game object behind <c>SetExternalData</c>: 5 creature
    /// (<c>CNWCCreature::LoadModel</c> @0x72a4b0), 9 placeable (@0x7ec500), 10 door (@0x755ec0), through
    /// vtable <c>SetExternalDataType</c>. The accessor pair is one line on both builds: Linux @0x444720,
    /// Windows rva 0x48450 read <c>+0x498</c>.</summary>
    public static readonly int GobExternalType = Field("Gob", "externalType");
    /// <summary>The <see cref="GobExternalType"/> a creature's gob carries.</summary>
    public const int GobKindCreature = 5;
    /// <summary>The <see cref="GobExternalType"/> a placeable's gob carries.</summary>
    public const int GobKindPlaceable = 9;

    /// <summary>CHitInfo, the record <c>Scene::HitCheck</c> fills. <c>Reset</c> (@0x45fa90 / rva 0xd3090)
    /// writes the mode at +0 and zeroes the ignore count at +0x18; <c>ClearOutput</c> (@0x45fa20 / rva
    /// 0xc5a90) clears the kind at +0x24, the hit point at +0x28 and the SafePointer'd gob at +0x48;
    /// <c>Scene::HitCheckGob</c> (@0x45f860 / rva 0xc9f50) walks the gob array at +0x10 for +0x18 entries
    /// to skip. Identical on both builds. The object is 0x80 bytes (Reset's highest store is +0x7d).</summary>
    public static readonly int HitInfoMode = Field("CHitInfo", "mode");
    /// <inheritdoc cref="HitInfoMode"/>
    public static readonly int HitInfoIgnoreList = Field("CHitInfo", "ignoreList");
    /// <inheritdoc cref="HitInfoMode"/>
    public static readonly int HitInfoIgnoreCount = Field("CHitInfo", "ignoreCount");
    /// <inheritdoc cref="HitInfoMode"/>
    public static readonly int HitInfoKind = Field("CHitInfo", "kind");
    /// <inheritdoc cref="HitInfoMode"/>
    public static readonly int HitInfoPoint = Field("CHitInfo", "point");
    /// <inheritdoc cref="HitInfoMode"/>
    public static readonly int HitInfoGob = Field("CHitInfo", "gob");
    /// <inheritdoc cref="HitInfoMode"/>
    public const int SizeOfHitInfo = 0x80;

    // Section 2 is closed: batch 1 took the Scene walk (#436), batch 2 the passes (#441), batch 3 the
    // renderer, textures, buffers and shim (#443), and build.sh now refuses a bare hex offset under
    // managed/Alloy/. A second tested client build gets its own pinned key plus its own values below;
    // today there is exactly one per OS.

    // ================= CX-689: CRadialMenu (docs/research/radial-menu-reimplementation.md §1) =================
    // Linux from the ctor @0x583460 / SetEntryx @0x5837f0 / SetSubMenuCreatorx @0x583f50 / CreateSubMenu @0x584790 /
    // HandleMenuAction @0x585530; Windows from the same exported CRadialMenu bodies in nwmain.exe (ctor rva 0x1f7950,
    // SetEntryx 0x1fa250, SetSubMenuCreatorx 0x1fa9e0, SetSubMenu 0x1fa490, CreateSubMenu 0x1f8960, DisableEntry
    // 0x1f8b30, HandleMenuAction 0x1f9350, RecursiveAutoDelete 0x1f9da0, Activate 0x1f7e80, BuildIcon 0x1f8400),
    // read side by side -- docs/engine-layout.md "The radial rows". The per-slot entry and creator callbacks are
    // CGuiCallback member-fn pairs: {obj, fn, adj} = 0x18 on Itanium, {obj, fn} = 0x10 on MSVC, so every array
    // after them shifts. sizeof(CRadialMenu) is 0x878 / 0x7f8 (each build's deleting dtor), and every subclass
    // starts its own fields right there, so the subclass rows carry a uniform -0x80. CANDIDATES until a live
    // probe (logs=1) reads a root on each platform.

    /// <summary>CRadialMenu: <c>CGuiTextBubble* m_pText[8]</c> -- the slot labels.</summary>
    public static readonly int RadialText = Field("CRadialMenu", "text");
    /// <summary>CRadialMenu: entry callback[8], stride <see cref="RadialCallbackStride"/>. Unset = the object word is
    /// the slot's own address (self-pointer sentinel) and fn is <c>HandleUninitializedCallback</c>.</summary>
    public static readonly int RadialEntry = Field("CRadialMenu", "entry");
    /// <summary>Bytes per callback slot: Itanium <c>{obj, fn, adj}</c> vs MSVC <c>{obj, fn}</c>.</summary>
    public static readonly int RadialCallbackStride = Field("CRadialMenu", "callbackStride");
    /// <summary>CRadialMenu: <c>long m_nParam[8]</c> -- shared by the entry and the creator of a slot.</summary>
    public static readonly int RadialParam = Field("CRadialMenu", "param");
    /// <summary>CRadialMenu: <c>uint flags[8]</c>; bit 0 = disabled (<c>DisableEntry</c>).</summary>
    public static readonly int RadialFlags = Field("CRadialMenu", "flags");
    /// <summary>CRadialMenu: creator callback[8], same stride and sentinel as <see cref="RadialEntry"/>.</summary>
    public static readonly int RadialCreator = Field("CRadialMenu", "creator");
    /// <summary>CRadialMenu: <c>CRadialMenu* m_pSubMenu[8]</c> -- the materialized child, null until CreateSubMenu.</summary>
    public static readonly int RadialSubMenu = Field("CRadialMenu", "subMenu");
    /// <summary>CRadialMenu: <c>CRadialMenu* m_pParent</c>, set by HandleMenuAction on descent.</summary>
    public static readonly int RadialParent = Field("CRadialMenu", "parent");
    /// <summary>CRadialMenu: <c>CGuiIcon* m_pIcon[8]</c>; the resref is the CResRef at CGuiIcon+0x8.</summary>
    public static readonly int RadialIcon = Field("CRadialMenu", "icon");
    /// <summary>CRadialMenu: menu X / Y (int), filled by <c>Activate</c> from <c>CGuiMan::GetMousePos</c>.</summary>
    public static readonly int RadialMenuX = Field("CRadialMenu", "menuX");
    /// <inheritdoc cref="RadialMenuX"/>
    public static readonly int RadialMenuY = Field("CRadialMenu", "menuY");
    /// <summary>CRadialMenu: <c>m_bAutoDelete</c> (byte); every root and creator sets it 1. No <c>Byte01</c> probe:
    /// that reads a dword, and the ctor @0x583460 never writes +0x872/+0x873 (heap garbage) -- RadialService
    /// probes the byte itself.</summary>
    public static readonly int RadialAutoDelete = Field("CRadialMenu", "autoDelete");
    /// <summary>CRadialMenu: refcount (int); CreateSubMenu ++ on the child, RecursiveAutoDelete --.</summary>
    public static readonly int RadialRefCount = Field("CRadialMenu", "refCount");
    /// <summary>CGuiIcon: the icon's <c>CResRef</c> (16 bytes) that <c>Initialize</c> (Linux @0x58cc20, Windows rva
    /// 0x1e4430: the first store on both, then the three size ints at +0x1c/+0x20/+0x24) and
    /// <c>ReplacePrimaryIconTexture</c> (@0x58cd30 / rva 0x1e4690) read.</summary>
    public static readonly int GuiIconResRef = Field("CGuiIcon", "resref");
    /// <summary>CGuiIcon: the uses-left count label, a <c>CAurString*</c> (null when none). <c>SetCountText</c> (Linux
    /// @0x58d130, Windows rva 0x1e4720) tests, feeds and stores it at +0x40 on both; <c>CreateCountLabel</c> @0x58ce80
    /// makes it with <c>NewCAurString(1)</c>, so it is the same StringGob sub-object the bubble label is.</summary>
    public static readonly int GuiIconCountLabel = Field("CGuiIcon", "countLabel");
    /// <summary>CGuiCreatureMenu / CGuiPlaceableMenu / CGuiDoorMenu / CGuiItemMenu / CClientTrapMenu: the target
    /// object id (uint), the first field after the CRadialMenu base on both builds. Read off each subclass ctor pair
    /// (Linux @0x73f2e0 + @0x73f110 / @0x7ecf70 / @0x756d00 / @0x7d1e70 / @0x9c7660; Windows rva 0x98e070 + 0x98e140 /
    /// 0x9b1950 / 0x9b0060 / 0x9a5940 / 0x9b11d0): the id argument's store, or <c>0x7f000000</c> where the ctor
    /// takes none (item, then <c>CGuiItemMenu::ActivateRadialMenu</c> @0x7d3720 / rva 0x9a59d0 stores the id there).
    /// Same offset in every subclass, so one row. No probe: an object id fits neither predicate.</summary>
    public static readonly int RadialTargetId = Field("CRadialMenu", "targetId");
    /// <summary>CClientSpellMenu: the target object id (uint) the cast leaf reads. The ctor pair (Linux @0x6f1650,
    /// Windows rva 0x9a10e0) stores its <c>uint</c> argument there, right after the target <c>Vector</c>
    /// (<c>+0x900</c> / <c>+0x880</c>); <c>RadialMenuCast</c> (Linux @0x6efb10, Windows rva 0x9a4540) passes it as
    /// <c>PerformCastSpell</c>'s target on both.</summary>
    public static readonly int SpellMenuTargetId = Field("CClientSpellMenu", "targetId");
    /// <summary><c>sizeof(CGuiCreatureMenu)</c>: the <c>operator new</c> before its ctor (Linux
    /// <c>ActivatePlayerCharacterRadialMenu</c> @0x73f3a0, Windows the twin of the associate root, rva 0x98e140's caller).</summary>
    public static readonly int CreatureMenuSize = Field("CGuiCreatureMenu", "sizeof");
    /// <summary><c>sizeof(CClientSpellMenu)</c>: the <c>operator new</c> in every slot-1 cast creator (Linux
    /// <c>CreateClientSpellMenu</c> @0x73b360), the deleting dtor's size on Windows (rva 0x9a19c0).</summary>
    public static readonly int SpellMenuSize = Field("CClientSpellMenu", "sizeof");
    /// <summary>CGuiTextBubble: the <c>CAurString*</c> the label lives in -- the ctor (@0x588690 Linux, @rva 0x1fd310
    /// Windows) stores <c>NewCAurString(-1)</c> there and <c>SetText</c> @0x588790 feeds it through the CAurString
    /// vtable.</summary>
    public static readonly int TextBubbleString = Field("CGuiTextBubble", "aurString");
    /// <summary>What to subtract from a <c>CAurString*</c> to get the <c>this</c> the exported
    /// <c>StringGob::GetTextLineCount/GetTextLine</c> want. The sub-object sits at StringGob+0x4c0 on both builds
    /// (<c>NewCAurString</c> @0x494050 / @rva 0x492f0 return <c>this + 0x4c0</c>), but the readers' <c>this</c>
    /// differs: Linux (@0x4922f0/@0x495ac0) reads the object base (<c>+0x500/+0x508</c>, the <c>_ZThn1216_</c> thunks
    /// adjust for interface callers), MSVC (@rva 0x48600/0x48610) reads <c>+0x40/+0x48</c> -- the sub-object itself,
    /// no thunk exists.</summary>
    public static readonly int StringGobReaderAdjust = Field("StringGob", "readerThisAdjust");

    // ================= CX-693: item icons (docs/research/radial-menu-reimplementation.md §5) =================
    // CGuiIcon's three subclasses, each read off its ctor on BOTH builds (Linux @0x58dcb0 / @0x58d9d0 / @0x58d5c0,
    // Windows rva 0x1e3000 / 0x1e2e40 / 0x1e2ca0): every subclass field lands at the same offset on each, because
    // the base is 0x48 on both (a single vptr, no Itanium-only padding). The model builders confirm the reads:
    // CLayeredIcon::CreateCopyIconModel @0x58c8b0 hands +0x48/+0x50 to the PLT ReplaceTexture, CCompositeIcon's
    // @0x58d320 replaces gui_replace01/02/03 with +0x8/+0x48/+0x59, CArmorIcon::LoadArmorLayeredIconModel
    // @0x58d7c0 (rva 0x1e4470) walks +0x48..+0xae by 0x11 and indexes +0xc0 by the compacted layer.

    /// <summary>CLayeredIcon: <c>int</c> palette-scheme length (10 for an item: <c>CNWCItem::UpdateIcons</c> @0x7cbc50
    /// passes 10) and the <c>unsigned short*</c> scheme the PLT is tinted with.</summary>
    public static readonly int LayeredIconSchemeCount = Field("CLayeredIcon", "schemeCount");
    /// <inheritdoc cref="LayeredIconSchemeCount"/>
    public static readonly int LayeredIconScheme = Field("CLayeredIcon", "scheme");
    /// <summary>CCompositeIcon: the second and third <c>CResRef</c> (17 bytes each, the first is the base's +0x8),
    /// bound to <c>gui_replace02</c> / <c>gui_replace03</c> of the <c>GUI_ICON_%dX%dLC</c> model -- bottom to top.</summary>
    public static readonly int CompositeIconResRef2 = Field("CCompositeIcon", "resref2");
    /// <inheritdoc cref="CompositeIconResRef2"/>
    public static readonly int CompositeIconResRef3 = Field("CCompositeIcon", "resref3");
    /// <summary>CArmorIcon: six <c>CResRef</c> layers at stride <see cref="ArmorIconResRefStride"/> (+0x48..+0xae),
    /// an empty one skipped; the shared scheme (<c>int</c> count, <c>unsigned short*</c>) and the optional per-layer
    /// scheme table (<c>count</c> shorts per EXISTING layer, null = every layer shares the one scheme).</summary>
    public static readonly int ArmorIconResRefs = Field("CArmorIcon", "resrefs");
    /// <summary><c>sizeof(CResRef)</c> as the armour layer array packs it (17 bytes, unaligned) and the layer count
    /// (the ctor's zero loop stops at +0xae = 0x48 + 6 * 0x11 on both builds). Values, not places -- no row.</summary>
    public const int ArmorIconResRefStride = 0x11;
    /// <inheritdoc cref="ArmorIconResRefStride"/>
    public const int ArmorIconLayers = 6;
    /// <inheritdoc cref="ArmorIconResRefs"/>
    public static readonly int ArmorIconSchemeCount = Field("CArmorIcon", "schemeCount");
    /// <inheritdoc cref="ArmorIconResRefs"/>
    public static readonly int ArmorIconScheme = Field("CArmorIcon", "scheme");
    /// <inheritdoc cref="ArmorIconResRefs"/>
    public static readonly int ArmorIconLayerSchemes = Field("CArmorIcon", "layerSchemes");
    /// <summary>CNWCItem -&gt; the item's own <c>CGuiIcon*</c> (plain, layered, composite or armour by vptr): built and
    /// REPLACED (old one deleted) by <c>CNWCItem::UpdateIcons</c> @0x7cbc50 on every item update, so it is re-read
    /// from the item every frame, never held. <c>CGuiItemMenu::ActivateRadialMenu</c> hands it to <c>SetCenter</c>:
    /// Linux @0x7d3720 reads <c>item+0x258</c>, Windows rva 0x9a59d0 <c>item+0x238</c>.</summary>
    public static readonly int ItemGuiIcon = Field("CNWCItem", "guiIcon");
    /// <summary>CGameObject: the object type byte <c>CClientExoAppInternal::GetItemByGameObjectID</c> tests for 6
    /// (item) before casting -- Linux @0x63f3a0, Windows rva 0x6d82a0, both <c>go+0xc</c>.</summary>
    public static readonly int GameObjType = Field("CGameObject", "type");
    /// <summary>Virtual slot of <c>CGameObject::AsNWCItem()</c>, the cast the same two bodies make after the type
    /// test: Linux <c>vtbl+0x70</c>, Windows <c>vtbl+0x68</c> (the usual one-destructor-slot shift).</summary>
    public static readonly int GameObjAsNWCItemSlot = Field("CGameObject", "vtbl.AsNWCItem", Expect.VtblSlot);

    // ================= engine constants that are not offsets =================
    // They rode into this table with #436 because the file they lived in was the offset file. They are
    // magic VALUES the engine compares against, not places -- no row, nothing to resolve per platform.

    /// <summary>The engine's "no such pool" token, tested at every one of the three VDO token fields
    /// (<c>cmp $0xffffffff,%eax</c> @0x4d003a) -- an absent token is skipped, not an error.</summary>
    public const uint VdoNoPool = 0xFFFFFFFF;

    /// <summary>Range that tells a directional (sun/moon) light from a placed one: directionals carry
    /// ~100000, points ~10. Read off <see cref="LightRange"/> by everything that has to tell them apart.</summary>
    public const float DirectionalRange = 1000f;

    /// <summary><c>CAMERA_FLAG_DISABLE_COLLISION</c> (nwscript.nss), the <see cref="ModuleCameraFlags"/> bit
    /// <c>UpdateCameraCollision</c> refuses on (#537).</summary>
    public const int CameraFlagDisableCollision = 0x02;

    // ================= build key =================

    /// <summary>The running client's build key, whatever it is -- logged even on mismatch, so the stand-down
    /// line can name what was actually loaded.</summary>
    public static string BuildKey => _key ??= OperatingSystem.IsWindows() ? WindowsKey() : LinuxKey();

    /// <summary>Is the running client a build we have measured (<see cref="ClientBuilds"/>)? #457: this is
    /// the extender's one rot policy -- <see cref="ServiceHost.Run"/> asks it before discovery and stands the
    /// whole unit down, so no per-feature gate can be the first to notice.</summary>
    public static bool KeyOk => ClientBuilds.Current != null;

    /// <summary>Rows the running build's file gives no Windows value (<c>-</c>). Zero is the section-3 finish
    /// line. Counted on BOTH platforms -- the migration is worked on Linux, and a counter that only moves on
    /// Windows is not visible where the work happens.</summary>
    public static int UnresolvedCount { get; private set; }

    /// <summary>Rows with no value for the OS we are running on -- read as 0, so <see cref="Require"/> stands
    /// down every feature that reads one.</summary>
    public static int MissingCount { get; private set; }

    /// <summary>
    /// Unit gate, the <see cref="SymbolMap.Require"/> pattern for layout rather than symbols: a feature that
    /// reads engine memory calls this at the top of <c>Initialize</c> and stands down whole if the answer is
    /// no. Reading a field at an offset measured on a different build is how a port renders garbage instead
    /// of failing. #457: the <c>KeyOk</c> branch below is unreachable in practice -- the host refuses a
    /// rotten build before discovery -- so what this gate is actually for is a row the build file leaves
    /// without a value for this OS (a Windows <c>-</c>, or a gap).
    /// </summary>
    /// <param name="classes">The engine classes this feature reads. Empty means "every row", which is only
    /// right for a feature that reads the whole table -- #436: once Alloy's offsets landed here, an
    /// unmeasured <c>PartTriMesh</c> field would otherwise have stood the nameplates down on Windows.</param>
    public static bool Require(ExtenderLog log, string feature, params string[] classes)
    {
        if (!KeyOk)
        {
            log.Always($"{feature}: standing down as a unit -- client build {BuildKey} is not a known build " +
                       $"({ClientBuilds.Known}) (rot: docs/update-day.md)");
            return false;
        }
        int missing = classes.Length == 0 ? MissingCount : MissingIn(classes);
        if (missing > 0)
        {
            log.Always($"{feature}: standing down as a unit -- {missing} engine offset(s) have no value for " +
                       $"this OS in build {ClientBuilds.Current?.Name} (docs/engine-layout.md)");
            return false;
        }
        return true;
    }

    /// <summary>Join <c>/knobs</c>. Once, from the host.</summary>
    public static void Register() => KnobSurface.Note(() =>
        $"engine layout: {_rows.Count} offset(s), {UnresolvedCount} without a Windows value, {MissingCount} missing here; " +
        $"build {BuildKey} = {ClientBuilds.Current?.Name ?? $"UNKNOWN -- ROT (known: {ClientBuilds.Known})"}");

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

    private readonly record struct Row(string Class, string Field, int Value, Expect Expect, bool Missing);

    private static string? _key;

    /// <summary>Declare a row: its value for this OS is the running build's (<see cref="ClientBuilds"/>).
    /// No value reads as 0 and counts as missing, which <see cref="Require"/> refuses.</summary>
    private static int Field(string cls, string field, Expect probe = Expect.None)
    {
        (int? linux, int? win) = ClientBuilds.Offset(cls, field);
        int? mine = OperatingSystem.IsWindows() ? win : linux;
        if (win == null) UnresolvedCount++;
        if (mine == null) MissingCount++;
        _rows.Add(new Row(cls, field, mine ?? 0, probe, mine == null));
        return mine ?? 0;
    }

    /// <summary>Every declared (class, field), for <see cref="ClientBuilds"/>' completeness check.</summary>
    internal static IEnumerable<(string Class, string Field)> Declared
    {
        get
        {
            foreach (Row r in _rows) yield return (r.Class, r.Field);
        }
    }

    /// <summary>The resolved value of row <c>cls.field</c>, or null when no such row is declared. For
    /// <see cref="ClientBuilds"/>' check that an override names a real row and actually landed.</summary>
    internal static int? RowValue(string cls, string field)
    {
        foreach (Row r in _rows)
            if (r.Class == cls && r.Field == field) return r.Value;
        return null;
    }

    /// <summary>Missing rows belonging to these engine classes. The gate is per feature, so the counter it
    /// reads has to be too: a renderer class nobody has measured must not stand the nameplates down.</summary>
    private static int MissingIn(string[] classes)
    {
        int n = 0;
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].Missing && Array.IndexOf(classes, _rows[i].Class) >= 0) n++;
        return n;
    }

    // ---- build key readers ----

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
        int before = _rows.Count, unresolvedBefore = UnresolvedCount, missingBefore = MissingCount;
        // A row no build file gives a value: it must read 0, count as missing, and gate its class.
        int gap = Field("SelfCheck", "gap");
        if (gap != 0 || _rows.Count != before + 1) { log.Always("layout selfcheck FAIL: a gap did not read 0"); return; }
        if (MissingCount != missingBefore + 1 || MissingIn(["SelfCheck"]) != 1 || MissingIn(["Nope"]) != 0)
        { log.Always("layout selfcheck FAIL: missing counter"); return; }

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
        MissingCount = missingBefore;
        log.Info($"layout selfcheck ok: {_rows.Count} offset(s), {UnresolvedCount} without a Windows value, " +
                 $"{MissingCount} missing here, build {BuildKey}");
    }
}
