using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using static CX.Framework.EngineLayout;

namespace CX.Alloy;

/// <summary>
/// <para>#173: this file is the SERVICE -- hook install, the frame anchor, the pass-generation clock, the
/// iterator's suppression answer, and the end-of-frame report. The frame path itself lives in siblings, one
/// responsibility each: <c>.Table</c> (the ownership and staging arrays), <c>.Enroll</c> (the ownership
/// walk), <c>.Frame</c> (per-pass dispatch), <c>.Batches</c> (build), <c>.Issue</c> (submit),
/// <c>.Target</c> (framebuffer plumbing), <c>.Programs</c> (link), <c>.Uniforms</c> (per-pass scene terms),
/// <c>.Shaders</c> (the embedded GLSL), with the contract predicates out in <see cref="ShaderContract"/>.
/// One class still, because the hot path reads the tables directly and a boundary between them would be a
/// copy or an indirection on a per-frame walk.</para>
/// #82/#83/#84 (map #68, spec #77 M1): the Alloy opaque world colour pass, scaled from #81's single tracer part
/// to the full ownable-now set (#72). Every qualifying part -- diffuse-textured, no bumpy-shiny, no bone
/// streams, indexed VDO, an ownable bucket -- is drawn by Alloy inside the
/// #80 4.6 context bracket: our own VAO per VDO over the shared engine buffers, texture ids re-fetched every
/// frame so animated textures keep cycling. Parts the engine would draw LIT go through the clustered
/// forward+ program over <see cref="AlloyLights"/>' unbounded light SSBO (#83), so the Alloy path has no
/// light-count ceiling to pop at; parts the engine draws unlit keep the flat variant, which is their vanilla
/// look. #120: a lit part is SHADED from its material, not just textured -- the five stock .mtr map slots
/// beyond the diffuse and the four stock parameter names feed a GGX lobe, which is the term vanilla runs on
/// every lit part and the Alloy pass used to drop on all of them. A lit part we cannot shade faithfully
/// (no normal stream, lit program down) is skipped rather than painted flat -- and since #264 deleted the
/// engine walk, skipped means NOT DRAWN. The exactly-one-owner problem went with the walk: there is exactly
/// one renderer, so there is nothing to suppress and nothing to double-draw.
/// <para>#84: submission is MULTIDRAW-BATCHED, so draw count follows the (program, VDO, texture, primitive)
/// group count rather than the part count. Everything that used to be a per-part uniform -- model matrix,
/// texture rotation, material ambient/diffuse -- moved into a per-draw SSBO the shaders index by
/// <c>uDrawIdx[uDrawBase + gl_DrawID]</c> (#242: the records are persistent and indexed by ownership
/// slot, so the draw carries the slot), which is what lets one <c>glMultiDrawElementsBaseVertex</c> cover a whole
/// group. The draw block is therefore two phases: build (revalidate every part, stage its record, cut the
/// batch at each key change) then issue (one bind set + one multidraw per batch).</para>
/// <para>WHERE the Alloy block runs is load-bearing, and #132 settled it: the anchor is an after-original
/// hook on <c>Scene::FadeDoors</c>, the last engine call inside <c>Scene::RenderSinglePass</c> before
/// <c>RenderDrawBucket(0)</c> -- see <see cref="OnFrameAnchor"/> and <see cref="SymbolMap.FadeDoors"/> for
/// why literal RSP entry (spec #77's and #114's wording) is not reachable. What entry DOES give, and #108
/// established, is the camera: the engine never uses the GL matrix stack, so view/projection are valid from
/// the moment Camera::RenderScene writes them into m_aurMtxStack (nm 0x19e2f60; proj +0x18, view +0x70)
/// before the vcall. Drawing the whole set before the engine's first opaque walk is what keeps suppression
/// honest: the engine re-buckets geometry every frame, so a part's enrolled bucket goes stale and splitting
/// the draw by it lets the engine walk a part before we draw it -- double-draw, z-fighting.</para>
/// <para>Ownership lives as a column on the shared part table (<see cref="AlloyCensus.OwnFlag"/>).
/// The table is rebuilt wholesale at a frame boundary
/// (enrollment, on a scene-walk delta, before the Alloy block) -- that is the frame-atomic
/// swap. The only mid-frame mutation is revocation. <c>vanilla=1</c> in cx.cfg is the kill switch, and
/// since #263 it is restart-only. Hot path allocates nothing.</para>
/// <para>#128: CREATURES join it, and they arrive by relaxing two things rather than by a new path. The
/// engine draws them from <c>Scene::RenderDynamicGeometry</c> -- buckets 2 and 3, but through the same
/// <c>RenderDrawBucket</c> the capture already brackets -- so what actually kept them out was the capture's
/// matrix PROBATION, which killed any part whose transform moved between two sightings; an animating creature
/// failed that by construction and a door failed it the frame it opened. Instances were exempted from it as
/// MOVERS, whose transform this pass reads live at draw time anyway, and #258 deleted probation
/// outright. The second relaxation is the
/// instance modulation: <c>PartTriMesh::Mat</c>'s alpha multiply, colour multiply and self-illum add are
/// folded into the per-draw record every frame instead of refusing the part, which matters because a creature
/// FADES through one of them -- opacity here is per-frame state, never capture state. What still cannot be
/// owned is a true skinmesh (a cloak, a robe, a tail: the Alloy vertex stages have no skinning branch) and the
/// three instance states no Alloy program expresses at all; both refuse against the GOB, and the enrollment
/// sweep then hands that whole creature back to the engine rather than half-own one (#107 s2's rule, per
/// instance instead of per class).</para>
/// <para>#85: water reflections need NOTHING from this pass. Spec #77's "reflection subframes also pass
/// through RenderSinglePass" reads the engine backwards -- <c>Camera::CopyRenderSubframe</c> is a
/// <c>CopyFramebuffer</c> plus a bind of the copy to texture units 11/12 (<c>texFBColor</c>/<c>texFBDepth</c>),
/// not a second scene pass, and RenderSinglePass takes it AFTER buckets 0/1/7 and immediately before the
/// bucket-5 water walk. Water is ordinary bucket-5 geometry whose <c>fswater.shd</c> ray-marches that
/// snapshot in screen space (normals reconstructed from depth, no G-buffer), so Alloy parts reflect because
/// they are already in the colour+depth attachments the snapshot copies.</para>
/// </summary>
public sealed unsafe partial class AlloyRenderer : IExtenderService
{
    internal const int LogEvery = 900;
    /// <summary>Minimum frames between enrollments. A floor on a cold path, NOT a cadence -- the trigger is
    /// always a scene-walk delta, and a new Scene bypasses it outright. The bound it states: a part that
    /// becomes ownable is owned within this many frames plus one budgeted walk (#329). Since #264 it is MISSING until then, not
    /// vanilla-shaded -- there is no engine walk behind it -- so this floor is a visible-latency bound now
    /// rather than a shading-quality one.</summary>
    internal const int EnrollFloor = 10;
    /// <summary>Frames before the FIRST period line. The 900-frame cadence is right for steady-state cost,
    /// but it is also the only proof this pass runs at all -- and at 900 a short session (or one that ends at
    /// a module unload) produces no line, which reads as "never armed" rather than "not yet". Report once
    /// early, then settle into the long cadence.</summary>
    internal const int FirstLogAt = 120;
    /// <summary>Ownership capacity. Beyond this the surplus stays engine-drawn (staging), never dropped.
    /// <para>4096 was under the real population and it showed: a large area logged
    /// <c>4082/5631 parts owned</c> -- pinned at the cap, with ~1550 ownable parts spilling. That alone is
    /// "some tiles render vanilla", but the visible symptom was worse than that, because WHICH parts spill
    /// is decided by a first-come-first-served walk of PartTable's hash table. That order is stable only
    /// while the table's population is: one part recorded into a low slot pushes a previously-owned part
    /// past the cap, so every enrollment (~1/s) re-picked the winners and tiles flipped renderer under a
    /// static camera. Capacity above the population is what makes the churn stop existing rather than
    /// merely slow down.</para>
    /// <para>Sized for object-dense modern areas rather than for the largest one measured: the renderer's
    /// job is to draw the whole area, so the cap is not a budget to tune but a ceiling that should not be
    /// reachable. 163840 is ~29x the 5631 a large stock area carries. 400 bytes per slot across the arrays
    /// below (the per-draw SSBO stage dominates at 176), so 62.5MB -- part of the 135MB these tables cost
    /// resident, up from 22MB. Bought deliberately: the renderer targets modern machines and object-dense
    /// areas, and a cap that cannot hold the area is a rendering fault, not a saving.</para>
    /// <para>Raising it is only meaningful because the group lookup it feeds became a hash at the same time
    /// -- PartTable's linear group walk was O(n^2) across an area load, which is 24M field compares at
    /// 7000 groups and 13.4 BILLION at this cap. Without that, this number buys a load that never
    /// finishes.</para>
    /// <para>ponytail: still first-come-first-served, so an area past even this would flicker the way 4096
    /// did. The fix if that ever happens is a DETERMINISTIC near-to-far spill -- so the surplus is always
    /// the far geometry, where the seam is fog-compressed -- not a bigger number.</para></summary>
    internal const int MaxOwned = 163840, MaxVdos = 4096;
    /// <summary>#84 batch-sort texture keys: distinct diffuse textures the sort can separate, and the
    /// open-addressed slot table that interns them (power of two, ~2x MaxMats for a 0.5 load factor). Overflow
    /// shares the last key, which costs batch size and nothing else -- the live flush key is the texture id.
    /// <para>Raised with <see cref="MaxOwned"/>: a large area logged exactly 1024 textures, i.e. saturated,
    /// which turns the last key into a bucket every unseen texture shares and costs the sort its whole
    /// point. Cheap to fix -- the tables are one int and one short per slot.</para></summary>
    internal const int MaxMats = 16384, MatHashBits = 15, MatHash = 1 << MatHashBits;
    /// <summary>Floats per CxDraw record: std430 <c>mat4 + vec4 rot + vec4 amb + vec4 dif + vec4 emi
    /// + vec4 pbr + vec4 spc + vec4 feat + mat4 prev</c> = 240 bytes (#392 grew it from 176), whose alignment
    /// (16) already divides the size, so the array stride is 240 with no padding.</summary>
    internal const int DrawFloats = 60;
    /// <summary>#392: where <c>prev</c>, the previous frame's model matrix, starts in the record.</summary>
    internal const int PrevFloat = 44;
    /// <summary>#242: record slots the persistent buffer can hold -- one per ownership slot, plus the sky's
    /// reserved block immediately above them. The sky's parts are not in the ownership table (they are
    /// captured off the engine's matrix stack) and are restaged every pass, so they take a fixed block at the
    /// top rather than a slot each.</summary>
    internal const int MaxRecords = MaxOwned + MaxSkyDraws;
    /// <summary>SSBO binding point of the per-draw block. Must match <c>DrawBufDecl</c>'s literal; 0..3 belong
    /// to <see cref="AlloyLights"/> (lights, cluster counts, cluster indices, stats).</summary>
    internal const uint BindDraw = 4;
    /// <summary>#137: SSBO binding of the Alloy pass's bone block. 0..3 are AlloyLights', 4 is the per-draw
    /// record, 5 and 6 belong to <see cref="CasterPass"/>. Must match <c>BoneBufDecl</c>'s literal.
    /// <para>Its own block rather than a share of the caster pass's: that one is written at the shadow
    /// bracket and read at the next bake, this one is written and read inside a single colour pass, and
    /// coupling two lifetimes to save 256KB buys a class of bug nobody can see.</para></summary>
    internal const uint BindBones = 7;
    /// <summary>#153 bindless: SSBO binding of the per-draw texture-handle block (8 uvec2 per record).
    /// 0..3 = the light rig, 4 = draws, 5 = caster records, 6 = caster bones, 7 = Alloy bones.</summary>
    internal const uint BindTexH = 8;
    /// <summary>#242: SSBO binding of the per-DRAW index array -- draw n of this pass names the record slot
    /// it reads. 0..3 are AlloyLights', 4 the records, 5/6 CasterPass', 7 the bones, 8 the handles. Must
    /// match <c>DrawBufDecl</c>'s literal.</summary>
    internal const uint BindDrawIdx = 9;
    /// <summary>Bones per skinned part, fixed by the engine: <c>GLRender::m_bones</c> is a 64 x mat4 static
    /// array and the vertex stream's indices address it directly. The matrices already sit in GLSL's
    /// column-major order (see <see cref="SymbolMap.GlRenderBones"/>), so a snapshot is a straight memcpy.
    /// </summary>
    internal const int BoneCount = 64, BoneFloats = BoneCount * 16;
    /// <summary>Skinned parts posed per frame, INITIAL capacity. 4KB each, so 512 slots is 2MB and covers a
    /// crowd. #262: no longer a ceiling -- the stage doubles rather than drop a creature, and the high-water
    /// mark is reported. <see cref="CasterPass"/> keeps it as a hard cap; a shadow that does not bake is a
    /// missing shadow, not a missing creature.</summary>
    internal const int MaxBoneSlots = 512;
    /// <summary>aurMatrixStack layout (Ghidra GLRender::PushMatrix/PopMatrix/aurMatrixStack::SetIdentity):
    /// stride 0x58 per stack, top matrix (row-major float[16]) at +0x18. Modes: PROJECTION=0, VIEW=1.</summary>
    internal static readonly int StackStride = EngineLayout.MtxStackStride, StackTop = EngineLayout.MtxStackTop;
    internal const int ModeProjection = 0, ModeView = 1;
    /// <summary>The part's MdlNode (part+0x8) and its material colours, from Ghidra
    /// <c>PartTriMesh::MatStatic</c> @0x450560, which calls <c>SetMaterialv(prop 1, node+0xf4)</c>,
    /// <c>SetMaterialv(prop 2, node+0xe8)</c> and <c>SetMaterialv(prop 3, part+0x7c)</c>. Prop 3 takes the
    /// self-illumination colour, so the enum runs ambient/diffuse/emissive -- and node+0xe8 is the one
    /// carrying the part's alpha (part+0xa0), which in fixed-function material setup is the DIFFUSE term.
    /// Distinct from the bucket node at part+0x88.</summary>
    internal static readonly int PartMdlNode = EngineLayout.PartNodeLink, MdlDiffuse = EngineLayout.MdlDiffuse,
                                MdlAmbient = EngineLayout.MdlAmbient;
    /// <summary>Self-illumination colour (3 floats) at part+0x7c -- MatStatic's prop-3 material term. It is
    /// applied whenever <c>part+0x94 == 0</c>, and LightPartTriMesh (@0x47a6a0) clears that byte on every
    /// part under <c>g_bFullDynamicLighting</c>, so under the modern renderer it applies ALWAYS.</summary>
    internal static readonly int PartSelfIllum = EngineLayout.PartSelfIllum;
    /// <summary>#120: the material's parsed .mtr parameter list, <c>List&lt;MaterialParameter&gt;</c> at
    /// mat+0x118 -- Ghidra <c>Material::CheckAndBindCustomShader</c> @0x4c62f0 passes exactly
    /// <c>(this + 0x118)</c> to <c>SharedMaterial::BindShaderParameters</c>, which reads the element pointer
    /// at +0 and the count at +8. So the count sits at mat+0x120.</summary>
    internal static readonly int MatParams = EngineLayout.MatParams, MatParamCount = EngineLayout.MatParamCount;
    /// <summary>MaterialParameter layout, from <c>Material::SetMaterialParam</c> @0x4c5d70 (the name is
    /// <c>strncasecmp</c>'d over 0x40 bytes from the record's start) and BindShaderParameters (value floats at
    /// +0x44, the two cached uniform locations at +0x54/+0x58, the type discriminator at +0x5c, the component
    /// count as a ushort at +0x60). The record is 100 bytes -- both walks step by exactly that.
    /// <para>The type discriminator selects how +0x44 is READ, not where the value lives: 0 takes it as a
    /// <c>float*</c> with <paramref name="ParamComps"/> components, 1 as a single <c>int</c>, both off the
    /// same address.</para></summary>
    internal static readonly int ParamValue = EngineLayout.ParamValue, ParamType = EngineLayout.ParamType,
                                ParamComps = EngineLayout.ParamComps, ParamStride = EngineLayout.ParamStride;
    /// <summary>A parameter list longer than this is not a .mtr, it is a bad pointer -- refuse it rather than
    /// walk off into the heap. Real materials declare a handful.</summary>
    internal const int MaxParams = 64;
    /// <summary>The SharedMaterial's own parameter list -- the one the .mtr's <c>parameter float &lt;Name&gt;
    /// &lt;value&gt;</c> lines are parsed onto. Ghidra <c>Material::SetMaterialParam</c> @0x4c5d70 walks
    /// <c>*(shared+0x1d0)</c> for <c>*(int*)(shared+0x1d8)</c> records of the same 100-byte layout.</summary>
    internal static readonly int SharedParams = EngineLayout.SharedParams,
                                SharedParamCount = EngineLayout.SharedParamCount;
    /// <summary>#120 texture slots the Alloy lit program binds beyond the diffuse: 1 normal, 2 specular,
    /// 3 roughness, 4 height, 5 self-illumination (the stock map, inc_material.shd's header comment).
    /// They resolve through <c>Material::GetTexture</c> exactly as slot 0 does -- a SharedMaterial's own
    /// eleven slots at shared+0xd8+i*0x10 win, the Material's own three are the fallback.</summary>
    internal const int PbrSlots = 5;
    /// <summary>#123: texture slot 6, the flow map, whose RG channels the flowmap shader reads as a UV flow
    /// direction (its own header says "supply your RG flow map in texture6"). Enrolled ONLY for a
    /// <see cref="ShaderFeature.Flowmap"/> part -- slot 6 is not part of the stock vocabulary, so probing it
    /// on every shared material would split batches over a texture nothing samples.</summary>
    internal const int FlowSlot = 5;
    /// <summary>#146: map slot 6 of the block, the ambient-occlusion map -- a baked visibility term in .r,
    /// sampled straight onto the ambient. #275: sourced from <c>.mtr</c> <see cref="AoTexture"/> (texture11),
    /// past the engine's own vocabulary, so it can never collide with a community model's texture7; the
    /// engine's own FB-effect SSAO is a screen-space pass that keeps running underneath it, untouched.
    /// <para>Unlike the flow map this is probed on EVERY shared material -- there is no shader feature that
    /// announces it. That costs one <c>Material::GetTexture</c> per enrolled part, and a batch split only
    /// where a material actually declares one.</para></summary>
    internal const int AoSlot = 6, MapSlots = TextureSlots.MapCount;
    /// <summary>#275: the <c>.mtr</c> line the AO map is declared on -- the first extender-owned slot
    /// (<see cref="MaterialExtraSlots.First"/>). A stock client parses and drops it.</summary>
    internal const int AoTexture = MaterialExtraSlots.First;
    /// <summary>#146: the AO slot's MASK bit -- 1 &lt;&lt; 7, deliberately NOT <c>1 &lt;&lt; AoSlot</c>. Every
    /// other slot's bit is its index, but bit 6 was <see cref="EnvSlot"/> before this ticket and the lit
    /// stage is content-overridable: a hak shipped against an older build still reads 64 as "env-mapped".
    /// Taking 64 for AO would make that stale copy render every env material shiny-and-unreflective with
    /// nothing in the log. The bit is free, the drift is not.</summary>
    internal const int AoBit = 1 << 7;
    /// <summary>Texture unit slot 1 binds on; the rest follow it. Units 0/1/2 are diffuse, sun cascades and
    /// the local cube array, and <see cref="EnvProbe.ProbeUnit"/> sits above all of them -- SelfCheck holds
    /// that ordering, because a samplerCube sharing a unit with a sampler2D kills every lit draw in silence.</summary>
    internal const uint PbrUnit0 = TextureSlots.MapBase;
    /// <summary>#121: the env-map bit of the same mask (<c>Material::HasEnvMap</c>, mat+0x40). One past the
    /// bindable texture slots, and unlike them it binds nothing -- it only tells the fragment stage that this
    /// is legacy "shiny" content whose diffuse alpha is a reflectivity mask.</summary>
    /// <summary>...and FROZEN at 64: shipped <c>cx_ownlit_fs.shd</c> overrides carry a copy of it, so this
    /// value is content, not an implementation detail. A new slot takes the next free bit (see
    /// <see cref="AoBit"/>), never this one.</summary>
    internal const int EnvSlot = 64;
    /// <summary>The SCENE's environment map (Ghidra <c>RenderFlat</c> @0x450eb0: a part with no env map of
    /// its own gets <c>currentscene+0x1e8</c> bound to the env sampler -- but ONLY if that field is set).
    /// Most areas leave it null, and that is why vanilla cobblestone does not reflect the sky: nothing is
    /// bound, so the ambient-specular sample reads nothing. It is the switch, not the texture -- what gets
    /// sampled here is the probe.</summary>
    internal static readonly int SceneEnvMap = EngineLayout.SceneEnvMap;
    internal const uint GlUnsignedShort = 0x1403;   // not-an-offset: GL enum
    /// <summary>The GL blend factors <see cref="BlendGl"/> maps the Aurora ones onto.</summary>
    internal const uint GlZero = 0, GlOne = 1, GlSrcColor = 0x0300, GlSrcAlpha = 0x0302,   // not-an-offset: GL blend-factor enums
                       GlOneMinusSrcAlpha = 0x0303, GlDstColor = 0x0306, GlOneMinusDstAlpha = 0x0305;   // not-an-offset: as above
    /// <summary>Engine attribute locations, fixed for every engine program (Ghidra
    /// <c>GLRender::ConnectShaderData</c>: one glBindAttribLocation table before every link -- 0 vPos,
    /// 1 vTcIn, 2 vColor, 7 vNormal). They say which engine VAO slot holds each stream.</summary>
    internal const uint EngPos = 0, EngUv = 1, EngNormal = 7;
    /// <summary>#96/#128: the bone streams of a true skinmesh, at the same fixed engine locations
    /// (<c>vIndex</c>/<c>vWeight</c> in ConnectShaderData's bind table). Both or neither -- a half-skinned
    /// VAO is not one this pass can reason about either way.</summary>
    internal const uint EngBoneIdx = 8, EngBoneW = 9;
    /// <summary>#137: and OUR locations for the same two streams, which the skinned vertex stages declare.
    /// Same numbers as the engine's by choice, not by necessity -- the Alloy VAO binds them itself.</summary>
    internal const uint LocBoneIdx = 8, LocBoneW = 9;
    /// <summary>Our own program's attribute locations. VAOs are ours, so the engine's locations only tell us
    /// which buffer/offset holds each stream -- we re-bind them wherever the program wants them.</summary>
    internal const uint LocPos = 0, LocUv = 1, LocNrm = 2;
    /// <summary>PartTriMesh::TransformTextureMatrix @0x4500b0 -> Rotate({0,0,1}, part+0xb0), and
    /// aurMatrixStack::Rotate scales by 0x3c8efa35 = pi/180 before RotateRadians: the field is DEGREES.</summary>
    internal const float DegToRad = 0.017453292f;

    private static ExtenderLog? _log;
    private static bool _armed;
    private static delegate* unmanaged<IntPtr, void> _origRsp;
    /// <summary>#132: the frame anchor's trampoline (<c>Scene::FadeDoors</c>).</summary>
    private static delegate* unmanaged<IntPtr, void> _origFade;
    private static delegate* unmanaged<IntPtr, int, long> _getTexture;
    private static uint* _primTable;
    private static byte* _mtxStacks;

    /// <summary>Pass generation: bumped at every RenderSinglePass entry (main view, reflection subframes,
    /// each is its own pass). An Alloy draw stamps it into AlloyCensus's OwnGen column; the iterator
    /// suppresses a part only when the stamp matches the pass it is walking.</summary>
    internal static int PassGen;
    /// <summary>#125: did the Alloy world block take THIS pass? The sky reads it for two things -- whether a
    /// <c>RenderSkyBoxes</c> call is the main world pass (a reflection subframe runs the same routine with a
    /// mirrored camera, and capturing there would position next frame's dome at the reflection eye) and
    /// therefore whether its capture window may open at all.</summary>
    internal static bool DrewThisPass => _drawnPass == PassGen;

    // 4.6-side objects (touched only inside the bracket)
    private static uint _prog46, _progLit46, _progDepth, _progDepthKey;
    /// <summary>The feature-aware depth stage: the lit program's OWN compiled vertex text (override or
    /// embedded, so its displacement is the colour pass's character for character) over an alpha-testing
    /// fragment stage. Takes the cutout class and the wind-displaced batches the plain prepass skips.</summary>
    private static uint _progDepthFeat;
    private static int _depthFeatVp = -1, _depthFeatBase = -1, _depthFeatTex = -1, _depthFeatCut = -1;
    private static FeatLoc _depthFeatFeat;
    private static bool _depthFeatFailed;
    private static bool _prepassFeat => Renderer.PrepassFeat.V;
    /// <summary>The vertex source the lit / fullbright programs actually compiled with (an override wins).</summary>
    private static string? _litVsSrc;
    /// <summary>#350: the spliced programs were built against a resource chain that is gone. Every program
    /// carrying a <c>// $VANILLA</c> region (lit, hosted lit, water, grass) is compiled ONCE from whatever
    /// the chain held at the time -- so a session that logged into SoW first kept SoW's display hooks (the
    /// racial-vision veil, and a second clustered-light walk per fragment for it) running on Arelith, and a
    /// session that logged into Arelith first never picked SoW's files up at all. Set on the module-unload
    /// edge (outside the bracket), acted on by <see cref="DropSpliced"/> inside it.</summary>
    private static bool _spliceStale;

    /// <summary>#136: the hosted sky's display-to-linear decode (<see cref="SkyDecodeFs"/>) and its sampler.</summary>
    private static uint _progSkyDecode;
    private static int _skyDecodeHdr;
    private static int _depthVp = -1, _depthBase = -1;
    /// <summary>#124: the dissolving batches' depth program (<see cref="DepthKeyVs"/>). Zero = it could not
    /// build, and those batches fall back to being excluded from the prepass -- the pre-fix behaviour, which
    /// is slower but never wrong.</summary>
    private static int _depthKeyVp = -1, _depthKeyBase = -1;
    private static FeatLoc _depthKeyFeat;
    /// <summary>The depth prepass could not build (or glColorMask is missing) -- latched, and the colour pass
    /// simply shades every fragment as it did before #119.</summary>
    private static bool _depthFailed;
    /// <summary><c>/alloy prepass 0</c>: skip the prepass, which is the A/B for its own acceptance number.</summary>
    private static bool _depthPrepass => Renderer.Prepass.V;
    /// <summary>Every Alloy vertex stage that ran carries <see cref="ClipExpr"/> under an invariant
    /// qualifier. False = a #92 override rewrote one, and the prepass cannot re-test its depth.</summary>
    private static bool _vsDepthSafe = true;
    private static int _locVp = -1, _locBase = -1, _locTex = -1;
    /// <summary>#126: <c>uAlphaCut</c> on each colour program -- the engine's <c>fAlphaDiscardValue</c>, set
    /// per batch from the batch's class.</summary>
    private static int _locCut = -1;

    /// <summary>The clustered lit program's uniform locations, ONE struct per program: #350 builds the lit
    /// stage twice -- <see cref="_progLit46"/> from the hosted regions' stubs, which every ordinary lit batch
    /// draws with, and <see cref="_progLitHost"/> with the content bodies in, bound only for a batch whose
    /// feature <see cref="ShaderFeatures.Hosted"/> names. The wrap terms (#139) and the rest stay at -1 on a
    /// content override (#92) that predates them, and simply do not get uploaded.</summary>
    internal struct LitLoc
    {
        public int Oit, OitDim, OitBias, OitGob;
        public int Tex, Vp, Base, Cut, Ambient, Diffuse, Dir, Grid, Tile, Z, Falloff, Viz, Wrap, Atten, Hot, WrapN, Sss, Ssao, SsaoOn, SsaoHalf, AoDirect, AoCutout,
                   SunTex, SunVp, SunTexel, SunBias, SunCount, SunFocus, SunRadius, SunDynTex, SunDyn,
                   CubeTex, CubeLight, CubePlane, CubeCount, CubeSoft, CubePcss, CubeDynTex, CubeDynPage, CubeFade, CubeFloor,
                   Probe, ProbeMips, ProbeGain, SceneEnv, Capture, Reach,
                   Weather, WeatherLook, CoverVp, CoverLayer, SnowLook, SnowLight, SnowPom, CoverEsm,
                   LmPages, LmMovers, CubeDynSphere,   // #674
                   LightOcc;                           // light.hidden
        public FeatLoc Feat;
    }
    private static LitLoc _lit, _litH, _litL;
    /// <summary>light.hidden: the roughness under which a surface's highlight is a mirror image of the source
    /// (wet ground is 0.35, a puddle 0.12) and takes the source's shadow whatever the cube cap says; a rougher
    /// lobe is too broad to be an image of anything.</summary>
    private const float GlossyRough = 0.5f;
    /// <summary>The lit program WITH the OIT append block (OitPass.WithLists), or 0. Bound only for lit batches inside a
    /// linked-list bracket; every other lit draw keeps early-Z through <see cref="_progLit46"/>.</summary>
    private static uint _progLitList;
    /// <summary>#350: the lit program WITH the hosted content bodies, or 0 (hosting off, no hosted feature, or
    /// its build failed -- hosted materials then draw the plain lit path through <see cref="_progLit46"/>).</summary>
    private static uint _progLitHost;
    /// <summary>#120: THIS pass's eye, from the inverse of the view matrix the pass is drawing with. Never
    /// <c>FrameInputs.CameraPos</c> -- that belongs to whichever pass set a view last -- and taken per pass
    /// rather than from the scene latch so a reflection subframe reflects from its own camera.</summary>
    private static Vector3 _eyePos;
    /// <summary>#127: this pass's camera, for anything drawing inside the colour bracket with its own
    /// program. Latched by <see cref="DrawOwned"/> from the inverted view matrix -- never
    /// <c>CurrentCamera</c>, which at present time is the UI frame's camera.</summary>
    internal static Vector3 EyePos => _eyePos;

    /// <summary>#127: the PC's world position, straight off the engine's own
    /// <c>m_shaderExposedRuntimeData</c> -- the same value its shaders read as <c>playerPosition</c>. Zero
    /// before the struct resolves, which for the grass displacement window just means it stays at the
    /// origin until it does.</summary>
    internal static Vector3 PlayerPos =>
        _runtime == null ? default : *(Vector3*)(_runtime + RtPlayerPos);
    /// <summary>#352: the module calendar the runtime block carries, what the shaders read as <c>moduleHour</c>
    /// and friends. Zero before the struct resolves.</summary>
    internal static int ModuleHour => _runtime == null ? 0 : *(int*)(_runtime + EngineLayout.RtModuleHour);
    internal static int ModuleDay => _runtime == null ? 0 : *(int*)(_runtime + EngineLayout.RtModuleDay);
    internal static int ModuleMonth => _runtime == null ? 0 : *(int*)(_runtime + EngineLayout.RtModuleMonth);
    internal static float ModuleTimeIntoTransition => _runtime == null ? 0f : *(float*)(_runtime + EngineLayout.RtModuleTimeIntoTransition);
    /// <summary>#352: the presented-frame counter, what a hosted content chain gets as <c>sceneCurrentFrame</c>.</summary>
    internal static int FrameSeq => _frameSeq;
    /// <summary><c>/alloy viz N</c>. 1..3 and 6 attribute a dim (or a too-shiny) frame to a lighting TERM instead
    /// of guessing from the composite; 5 is the local-cube term alone; 4 is the coverage map -- Alloy draws
    /// paint flat (cyan lit / magenta fullbright) and everything still textured is geometry the engine's own
    /// GL 3.3 walk drew, which no other signal makes visible. Each mode is documented where it branches, in
    /// <see cref="OwnedLitFsMain"/>. 7 is the shading normal, which is the only thing that separates a
    /// low-poly TERMINATOR (a wrap dial fixes it) from a discontinuous MESH normal (nothing in the BRDF
    /// can). 8 is #146's occlusion factor -- the AO map times the height term -- which is white everywhere
    /// until some material in sight declares one.
    /// <para>#129 adds the water composite's own terms, which live in <see cref="FrameSnapshot"/>'s stage rather
    /// than the lit one and only paint where Alloy water draws: 9 what is seen THROUGH the surface, 10 the
    /// reflection, 11 fresnel as grey, 12 the framebuffer copy's colour exactly as sampled, 13 the copy's
    /// linearised depth, 14 the screen UV those two sample at, 15 the depth copy's raw texel. 12 and 13 say
    /// whether the one copy is landing; 14 and 15 say which half is at fault when they read flat; 16 and 17 are
    /// the Beer-Lambert clarity and the thickness behind it, which is where a landed copy still produces a
    /// flat surface.</para></summary>
    /// <summary>Highest mode <see cref="_lightViz"/> accepts. Raise it when a stage adds one, or the new
    /// mode is silently discarded at the parse -- which is not a hypothetical: #339 added four water views
    /// above the ceiling and every one of them clamped to 23, so four screenshots of "the new view" were
    /// four screenshots of the old one, and the conclusions drawn from them were void.
    /// <see cref="FrameSnapshot.SelfCheck"/> now asserts the water stage's highest view against this.</summary>
    internal const int LightVizMax = 44;   // 40 = the #493 vis-ID target (IdTarget.VizMode); 41/42 = unused since the probe grid left; 43/44 = the #674 lightmap term and its texel grid
    private static int _lightViz => Renderer.Viz.V;
    /// <summary><c>/alloy aotest 1</c>: bind every lit part's own DIFFUSE texture on the AO slot and set the bit, so
    /// the whole #146 path -- mask, unit, sampler, ambient multiply -- runs end to end on stock content. The
    /// frame looks WRONG on purpose (the diffuse's red channel is not an occlusion map); what it proves is
    /// that a material which does declare one will be sampled. No shipped content uses texture11 yet, so
    /// without this there is nothing to point the feature at.</summary>
    private static bool _aoTest => Renderer.AoTest.V;
    private static int _locViz = -1;
    private static int _locOit = -1, _locOitDim = -1, _locOitBias = -1, _locOitGob = -1;
    /// <summary>#222: the fullbright stage's constant colour. White for every unlit draw but a fog-tinted
    /// sky part -- a GLSL uniform defaults to ZERO, so an unset one draws BLACK and every caller sets it.</summary>
    private static int _locTint = -1;
    private static int _locSkyFog;
    /// <summary>#121: this draw is going into the <see cref="EnvProbe"/> cube, not onto the screen. The lit
    /// and water stages read it, so a capture never samples the probe -- which is currently an attachment of
    /// the framebuffer being drawn into. Since #132 it says nothing about COLOUR SPACE any more: the screen
    /// target is linear too, so the capture and the frame want the same values and the fullbright stage
    /// stopped needing the flag at all.</summary>
    private static bool _capturing;
    internal static bool Capturing => _capturing;
    /// <summary>GL_BLEND as the pass last set it, so <see cref="IssueBatches"/>' per-batch toggle starts from
    /// the truth instead of assuming.</summary>
    private static bool _blendOn;
    /// <summary>Does the area declare an environment map at all? Read off the enrolled Scene each pass. When
    /// it is null the engine binds NOTHING for a plain part, so ambient specular is a term vanilla does not
    /// have there and only genuinely env-mapped materials get the probe.</summary>
    private static bool _sceneEnv;
    private static bool _progFailed, _litFailed;
    /// <summary>#84: the driver has no glMultiDrawElementsBaseVertex or no glBindBufferBase, so batched
    /// submission cannot run at all. Latched, and EndFrame disarms on it by name rather than drawing nothing
    /// forever while the iterator keeps suppressing parts.</summary>
    private static bool _batchUnavailable;
    /// <summary><c>/alloy light 0</c> forces every Alloy part back to the #82 fullbright variant (the lighting A/B).</summary>
    private static bool _lighting => Renderer.Light.V;
    /// <summary>RenderInterface::IsEnabled -- queried for cap 5 (LIGHTING) once per Alloy pass.</summary>
    private static delegate* unmanaged<int, byte> _isEnabled;
    /// <summary>The AuroraEnableEnums value for lighting. <c>MatStatic</c>/<c>RenderFlat</c> both branch on
    /// it, so a pass that runs with it off wants the flat variant for everything.</summary>
    internal const int CapLighting = 5;

    // lifecycle
    private static int _tableGen = -1, _sinceEnroll;
    private static bool _wasInModule;

    // period counters (the Alloy draw log counter, counterpart to M0's Alloy cast)
    private static long _draws;
    /// <summary>Why an Alloy part still did not draw this pass, by term. The single fallback count could not
    /// separate a camera-dependent bail-out (keyhole, tile fade) from a content one, which is exactly the
    /// distinction a part that is Alloy head-on and vanilla at the screen edge turns on.</summary>
    private static long _skipLod;
    private static long _skipNoLight, _skipIdentity, _skipNoMdl, _skipTex,
                        _skipRange, _skipVao;
    /// <summary>Parts whose material declared a normal map, and the subset whose map survived the per-slot
    /// re-fetch into the mask the lit stage samples on.</summary>
    private static long _nrmDeclared, _nrmBound;
    /// <summary>#124's coverage number: Alloy parts the camera dissolves this pass. Before the fold these
    /// were the keyhole SKIP count -- same population, dropped from the frame; now they are drawn by the
    /// Alloy program with the dissolve folded in, so the before/after the ticket asks for is this one figure
    /// read off the period line either side of the change.</summary>
    private static long _ownDissolve;
    /// <summary>...and WHERE the unmatched came from, by captured bucket and by class. A part drawn and
    /// never sighted is either a suppression gap (a bucket we draw into but do not suppress) or geometry the
    /// engine culled this frame and we drew anyway; the two need different fixes and read identically in a
    /// single total.</summary>
    /// <summary>#134 diagnostic: the engine-culled skip, split by the bucket the part was captured in. "Owned
    /// but never walked" is the one skip that cannot be read off the totals -- it means the part is neither
    /// drawn by us nor suppressed, so whichever bucket dominates here names the walk we are missing.</summary>
    /// <summary>#134: a small budget of watched-part draw-side lines, so following one part through the owned
    /// loop costs a handful of log lines rather than one per frame forever.</summary>
    private static int _watchDrawLeft = 20;
    /// <summary>#134: how much of the <c>dumptex</c> population we enrolled, and how much of it we actually
    /// submit each frame. The gap between them, and between both and the engine's own draw count, is the
    /// only honest answer to "is that thing on screen ours".</summary>
    private static int _watchEnrolled;
    private static long _watchSubmitted;

    /// <summary>#134: one line naming what the Alloy draw loop did with the watched part. Always returns
    /// true, so it reads as the reason at a skip site: <c>{ _skipTex++; if (W(i, "tex")) continue; }</c>.</summary>
    private static bool W(int i, string why)
    {
        if (AlloyCensus.WatchedEntry(_oEntry[i]) && _watchDrawLeft-- > 0)
            _log?.Info($"[dumpframe] DRAW {why} (slot {i}, capture bucket {_oBucket[i]}, cls {(AlloyClass)_oCls[i]})");
        return true;
    }
    /// <summary>#126's counterpart to it: Alloy parts the tile fade moves into the blended run this pass.
    /// Before the fold these were the tilefade SKIP count -- same population, dropped from the frame.</summary>
    private static long _ownFade;
    /// <summary>#546's other half: parts whose tile fog ramp (or a gob part's filed tile) read 0, dropped outright at route.
    /// Split by gob/tile because a creature part here is a HOLE the fog-of-war never explains.</summary>
    private static long _skipFogTile, _skipFogGob;
    /// <summary>#126: Alloy draws per class this pass, and the batches each class's pass submitted. The
    /// cutout/blended split is the whole model, so it is reported rather than inferred.</summary>
    private static readonly long[] _drawsByClass = new long[AlloyClasses.Count];
    /// <summary>#84's measurable form of "draw count decoupled from part count": submitted multidraw batches.
    /// draws/batches IS the batching ratio the acceptance criterion asks for, straight out of the log.</summary>
    private static long _batches;
    /// <summary>#292 (AC6): staged draws whose material said <c>twosided</c>, and the batches that the
    /// culling term alone opened. Together they say what reproducing the engine's per-material cull decision
    /// cost the batch count -- the criterion this ticket asks to be measured, not assumed.</summary>
    private static long _twoSidedDraws, _cullCuts;
    /// <summary>#327: opaque-range batch cuts per cut-key term, in <see cref="CutTerms"/> order.</summary>
    private static readonly string[] CutTerms = { "vao", "feat", "dissolve", "blend", "cull", "prim", "lit", "tex" };
    private static readonly long[] _cutBy = new long[CutTerms.Length];
    private static string CutCensus()
    {
        var sb = new System.Text.StringBuilder();   // cold: the period line
        for (int t = 0; t < CutTerms.Length; t++)
            sb.Append(t == 0 ? "" : ", ").Append(CutTerms[t]).Append(' ').Append(((float)_cutBy[t] / _frames).ToString("F0"));
        return sb.ToString();
    }
    private static int _frames, _revokes, _enrolls, _lastEnrolled, _lastCandidates, _enrollDiagLeft = EnrollDiagBudget;
    /// <summary>Passes an empty enrollment may retry back-to-back after a resume before falling back to the
    /// normal cadence (~2s at 60fps): enough for the re-record to complete, bounded so a genuinely unownable
    /// area cannot spin the scan every pass forever.</summary>
    internal const int EnrollDiagBudget = 8;
    /// <summary>Distinct diffuse textures interned at the last enrollment -- the batch floor (see EndFrame).</summary>
    private static int _lastTextures;
    private static bool _reportedHit;
    /// <summary>Lit parts refused at enrollment (no normal stream / lit program down) -- they stay vanilla.</summary>
    private static int _litUnownable;
    /// <summary>#128: parts refused for carrying bone streams, and what the per-instance sweep then cost --
    /// already-accepted parts revoked because a SIBLING of theirs on the same instance was refused. Together
    /// with <c>AlloyCensus.RefusedGobs</c> on the same line they are the honest answer to "why is that
    /// creature still vanilla". Counted as PARTS, which is the figure the sweep actually produces: the gob
    /// count includes instances that had nothing accepted to revoke.</summary>
    private static int _skinUnownable, _gobParts;
    /// <summary>#128: instanced parts this enrollment took -- placeables, doors and creatures alike. Its only
    /// job is to say whether the shadow side needs poking, so it counts PARTS and not instances.</summary>
    private static int _gobOwned;
    /// <summary>The INSTANCED half of the draw accounting: gob parts Alloy staged this pass. Every other
    /// number on the [alloy] line mixes tiles, placeables and creatures into one total, so "are characters
    /// Alloy's" could only be inferred from the capture-bucket split, which is a CLASSIFICATION and not a
    /// per-frame fact. This is the fact. #285: read it against <c>{_gobOwned} owned</c> -- a gap between them
    /// is instanced geometry NOT DRAWN, since #264 left nothing behind an unstaged part.</summary>
    private static long _drawsGob;
    /// <summary>...and the three causes behind the <c>tex</c> fallback, which are three different bugs
    /// wearing one number. "No diffuse" is content; "no GL id" is the copy path; "no owned handle" is the
    /// BINDLESS layer, which #220 already caught being wrong once and which a part can fail every frame
    /// forever -- a steady count here, roughly one creature's worth of parts, is a specific model whose
    /// texture Alloy declined to own, and that model then renders vanilla for the whole session.</summary>
    private static long _skipTexNone, _skipTexNoId, _skipTexNoHandle, _skipTexNotOwned;
    /// <summary>A small budget of NAMED failures. The counters say which fault; only the resref says which
    /// CONTENT, and that is what separates "one model's texture" from "a whole class of them". Budgeted
    /// rather than rate-limited: the population is what matters and it is small, so a handful of distinct
    /// names is the whole answer.</summary>
    private static int _texSkipDiagLeft = 12;
    private static readonly System.Collections.Generic.HashSet<string> _texSkipSeen = new();

    /// <summary>Name a diffuse the bindless guard turned away, once per distinct resref.</summary>
    private static void TexSkipDiag(bool owned, int tid, TextureRef aur)
    {
        if (_texSkipDiagLeft <= 0) return;
        string nm = aur.ResolveName();
        if (!_texSkipSeen.Add(nm)) return;
        _texSkipDiagLeft--;
        _log?.Info($"AlloyRenderer: [texskip] \"{nm}\" tid={tid} owned={owned} -- "
                   + (owned ? "Alloy owns it but GetTextureHandleARB minted nothing"
                            : "never reached the upload funnel, so it is BORROWED and bindless will not vouch for it")
                   + "; #285: every lit part using it is NOT DRAWN (bindless=0 in cx.cfg is the A/B)");
    }
    /// <summary>Monotonic presented-frame counter: the light SSBO uploads once per frame, not once per pass.</summary>
    private static int _frameSeq;

    private static bool _takeOverflow, _ambientFallbackLogged, _loggedOnce;
    private static uint _clusterOverflow;
    /// <summary>#119 measurement: prepass GPU time, and the fragments the COLOUR pass actually shaded.
    /// samples/screen-pixel is the overdraw figure, and its move with <c>/alloy prepass</c> is what the prepass
    /// is worth -- both are period averages on the [alloy] line.</summary>
    /// <summary>...and the shading pass's own GPU time. Frame time cannot answer "is the prepass worth it"
    /// on a vsync-capped display -- both modes sit on the cap and the saving becomes headroom -- so the A/B
    /// reads the work itself. Independent of SAMPLES_PASSED: different query target, both may be active.</summary>
    private static GpuQuery _qSamples;
    /// <summary>#336: SAMPLES_PASSED over the particle pass alone -- its overdraw against the viewport.</summary>
    private static GpuQuery _qPartSamples;
    /// <summary>#325: the timed GPU terms of the colour bracket, in pass order. Beams and the probe capture
    /// are untimed; the shadow bracket (<c>CasterPass</c>) and the cluster build (<c>AlloyLights</c>) run
    /// their own elapsed queries OUTSIDE every term by pass order, which is the premise the guard rests on.
    /// <para>#336: the three <c>*Key</c> terms are the keyhole-dissolving tails -- the prepass's second sweep
    /// and each opaque class's dissolve partition (#327) -- timed apart so "under a roof" has a number.</para></summary>
    internal enum GpuTerm { Prepass, PrepassKey, PrepassFeat, PrepassRetest, Ao, Opaque, OpaqueKey, Cutout, CutoutKey, Grass, GrassDepth, Sky, Blended, Water, Additive, Particles, Motion, Exposure, Post, Cull, HiZ, Beams, WaterCopy, Weather, Count }
    /// <summary>One elapsed query per term. TIME_ELAPSED cannot nest: until #325 the colour timer wrapped
    /// the grass and water ones, the inner End closed it early, and both inner meters wedged on a stale sample.</summary>
    private static readonly GpuQuery[] _qTerm = new GpuQuery[(int)GpuTerm.Count];
    /// <summary>The term whose query is open, -1 for none. Closed at frame start if a pass left it open.</summary>
    private static int _qOpen = -1;
    /// <summary>#287's pixels-per-world-radius-at-unit-depth for THIS pass (0 without perspective), kept for
    /// the cube receiver's disc gate (#326).</summary>
    private static float _lodScale;

    /// <summary>Open a term's elapsed query unless another is already open -- the guard that keeps the
    /// brackets serial. A term whose query declined the frame (result not ready) is simply not measured.</summary>
    /// <summary>Every term's period average summed, shadows excluded (the bake has its own query).</summary>
    private static double GpuTermsTotal()
    { double s = 0; for (int t = 0; t < (int)GpuTerm.Count; t++) s += GpuMs((GpuTerm)t); return s; }

    internal static void GpuBegin(GpuTerm t)
    { if (_qOpen < 0 && _qTerm[(int)t].Begin(Gl.TIME_ELAPSED)) _qOpen = (int)t; }

    /// <summary>Frame start: a term still open from the last frame (an early return between its Begin and
    /// End) is closed here, or every later Begin is refused and the line reads zero for the session.</summary>
    private static void GpuFrameStart()
    {
        if (_qOpen >= 0) GpuEnd((GpuTerm)_qOpen);
        GpuSpanStamp(0);
    }

    /// <summary>Whole-bracket GPU span: TIMESTAMP at frame start and after the pass loop, two frames in flight.
    /// The terms are serial elapsed queries and only cover what is bracketed; this is the number they are
    /// measured against, so the unmeasured remainder has a size.</summary>
    private static readonly uint[] _qSpan = new uint[4];
    private static readonly bool[] _qSpanPending = new bool[2];
    private static int _qSpanAt;
    private static double _spanSum; private static int _spanN;
    internal static double GpuSpanMs => _spanN == 0 ? 0.0 : _spanSum / _spanN / 1e6;

    private static void GpuSpanStamp(int which)
    {
        if (!GpuQuery.Enabled || Gl.QueryCounter == null || Gl.GenQueries == null || Gl.GetQueryObjectui64v == null) return;
        if (_qSpan[0] == 0) fixed (uint* q = _qSpan) Gl.GenQueries(4, q);
        if (which == 0)
        {
            int prev = 1 - _qSpanAt;
            if (_qSpanPending[prev])
            {
                int ready; Gl.GetQueryObjectiv(_qSpan[prev * 2 + 1], Gl.QUERY_RESULT_AVAILABLE, &ready);
                if (ready != 0)
                {
                    ulong t0, t1;
                    Gl.GetQueryObjectui64v(_qSpan[prev * 2], Gl.QUERY_RESULT, &t0);
                    Gl.GetQueryObjectui64v(_qSpan[prev * 2 + 1], Gl.QUERY_RESULT, &t1);
                    _qSpanPending[prev] = false;
                    if (t1 > t0 && t1 - t0 < 10_000_000_000UL) { _spanSum += t1 - t0; _spanN++; }
                }
            }
            if (_qSpanPending[_qSpanAt]) return;
            Gl.QueryCounter(_qSpan[_qSpanAt * 2], Gl.TIMESTAMP);
        }
        else if (!_qSpanPending[_qSpanAt])
        {
            Gl.QueryCounter(_qSpan[_qSpanAt * 2 + 1], Gl.TIMESTAMP);
            _qSpanPending[_qSpanAt] = true;
            _qSpanAt = 1 - _qSpanAt;
        }
    }
    internal static void GpuSpanEnd() => GpuSpanStamp(1);

    /// <summary>Close the term if it is the open one; a no-op for a term that never opened.</summary>
    internal static void GpuEnd(GpuTerm t)
    { if (_qOpen == (int)t) { _qTerm[(int)t].End(Gl.TIME_ELAPSED); _qOpen = -1; } }

    private static double GpuMs(GpuTerm t) => _qTerm[(int)t].Avg / 1e6;

    /// <summary>The read-only half of <see cref="HandleFor"/>: the handle if one was minted, 0 otherwise. Safe
    /// off the main thread as long as nothing mints during the stage, which nothing does.</summary>
    private static ulong HandleLookup(int tid)
        => tid != 0 && Probe(tid, out int hit, out _) ? _hVal[hit] : 0;
    /// <summary>#153: resident bindless handle for a texture name. 0 in, 0 out. Called only inside the 4.6
    /// bracket (handles belong to the share group), and -- while ownedtex is armed -- only ever for an
    /// Alloy-OWNED name; see <see cref="_hTid"/> for why that is the whole of #220's fix.
    /// <para>A handled texture is IMMUTABLE from this moment: a change means a new name
    /// (<c>OwnedTextures.Replace</c>), never an in-place <c>TexSubImage</c> from the engine context. A handle
    /// is not a binding point, so GL 4.6 5.3.3 Rule 4 gives this context no re-attach event through which
    /// another context's update is guaranteed visible -- residency is per context and nothing propagates.
    /// See docs/research/opengl-46-portability-audit.md, C4.</para></summary>
    private static ulong HandleFor(int tid)
    {
        if (tid == 0) return 0;
        // a name that is not ours may already be dead or RECYCLED (Mesa hands the lowest free name straight
        // back): minting on it is a fault on zink and another texture's handle on radeonsi
        if (OwnedTextures.Armed && !OwnedTextures.IsOwned(tid)) return 0;
        if (Probe(tid, out int hit, out int free)) return _hVal[hit];
        if (free < 0) return 0;                        // table full: no handle, caller falls back
        ulong v = Gl.GetTextureHandleARB((uint)tid);
        if (_handleDiagLeft > 0) HandleDiag(tid, v);
        // A failed mint is NOT cached. It used to be, and the enrollment wipe retried it within a second;
        // with that wipe gone (#220) a cached zero would be a permanently black texture.
        if (v == 0) return 0;
        Gl.MakeTextureHandleResidentARB(v);
        FrameZones.Count(FrameZones.Ev.Mint);
        _hTid[free] = tid; _hVal[free] = v; _hRes[free] = true;
        return v;
    }

    /// <summary>Walk the open-addressed chain for <paramref name="tid"/>. True with <paramref name="hit"/>
    /// set when the name is cached; false with <paramref name="free"/> set to the slot an insert should take
    /// (-1 only if the table is full). Split out of <see cref="HandleFor"/> with no GL in it so
    /// <see cref="SelfCheckHandles"/> can exercise the part that has now been wrong twice.
    /// <para>The rule the tombstone exists for: a tombstoned slot must NOT answer for its name -- GL recycles
    /// a deleted name immediately -- but must still be reusable, and must not end the chain.</para></summary>
    private static bool Probe(int tid, out int hit, out int free)
    {
        int mask = (1 << HandleHashBits) - 1;
        int h = Slot(tid);
        hit = -1; free = -1;
        for (int probes = 0; probes <= mask; probes++, h = (h + 1) & mask)
        {
            int k = _hTid[h];
            if (k == HandleTombstone) { if (free < 0) free = h; continue; }
            if (k == tid) { hit = h; return true; }
            if (k == 0) { if (free < 0) free = h; return false; }
        }
        return false;
    }

    /// <summary>Off-device: the handle cache's slot discipline, with no GL. #220 shipped a
    /// <see cref="DropHandle"/> that left the slot keyed on the DELETED name with a zero handle -- so the
    /// next copy, which GL had handed that very name to, matched the dead entry and drew black. That is the
    /// same shape as #221's dead-entry bug, and it is what this asserts can no longer happen.</summary>
    internal static bool SelfCheckHandles(ExtenderLog log)
    {
        int mask = (1 << HandleHashBits) - 1;
        const int A = 4242;
        int home = Slot(A);
        int s0 = _hTid[home], s1 = _hTid[(home + 1) & mask], s2 = _hTid[(home + 2) & mask];
        ulong v0 = _hVal[home];
        _hTid[home] = 0; _hTid[(home + 1) & mask] = 0; _hTid[(home + 2) & mask] = 0;

        bool ok = !Probe(A, out _, out int free) && free == home;      // empty: insert at home
        _hTid[home] = A; _hVal[home] = 7;
        ok &= Probe(A, out int hit, out _) && hit == home && _hVal[hit] == 7;   // cached: found

        // ...the texture dies. DropHandle's slot state, asserted directly rather than through GL.
        _hTid[home] = HandleTombstone; _hVal[home] = 0;
        ok &= !Probe(A, out _, out free);            // the dead entry must NOT answer for its own name...
        ok &= free == home;                          // ...and its slot is the one an insert reuses
        // A tombstone must not END the chain: A itself, displaced to the next slot by an old collision,
        // has to stay reachable through the tombstone rather than reading as absent.
        _hTid[(home + 1) & mask] = A; _hVal[(home + 1) & mask] = 9;
        ok &= Probe(A, out hit, out _) && hit == ((home + 1) & mask) && _hVal[hit] == 9;

        _hTid[home] = s0; _hVal[home] = v0;
        _hTid[(home + 1) & mask] = s1; _hTid[(home + 2) & mask] = s2;
        log.Info(ok ? "AlloyRenderer.SelfCheckHandles: passed" : "AlloyRenderer.SelfCheckHandles: FAILED");
        return ok;
    }

    /// <summary>#220: a slot whose texture has been deleted. Not 0 -- that would end the open-addressed probe
    /// chain and hide every entry behind it -- and not the name either, which is the bug this replaces: GL
    /// recycles a deleted name immediately, so a slot still keyed on it answered the NEXT copy with the dead
    /// entry's zero handle, and the part drew black. No GL texture is ever named -1.</summary>
    private const int HandleTombstone = -1;

    private static int Slot(int tid) =>
        (int)((uint)tid * 0x9E3779B9u >> (32 - HandleHashBits)) & ((1 << HandleHashBits) - 1);

    /// <summary>#220: this name's texture is about to be deleted, so its handle must stop being resident
    /// first -- a resident handle outlives the object it names, and sampling one after the delete is the
    /// corruption this ticket is about. Called by <c>OwnedTextures.Drain</c>, inside the 4.6 bracket, and
    /// only for a name that entry actually owns. A no-op for a name that never got a handle, which is the
    /// common case (grass, sky and caster copies never take the lit bindless path).
    /// <para>The slot becomes a <see cref="HandleTombstone"/>: not 0, which would cut the probe chain, and
    /// not the name, which would answer for the next copy GL hands that recycled name to.</para></summary>
    internal static void DropHandle(uint name) => DropHandle(name, gl: true);

    /// <summary>The texture died in the ENGINE's context before this bracket could run: the handle is gone
    /// with it, and asking the driver to make it non-resident is INVALID_OPERATION (Mesa) or worse. Tombstone only.</summary>
    internal static void ForgetHandle(uint name) => DropHandle(name, gl: false);

    private static void DropHandle(uint name, bool gl)
    {
        if (name == 0) return;
        // #242: a persistent record carries its handles (and, bindless off, its cached tid) across frames, so a
        // dying name must reach the records that hold it. That used to be an epoch bump here; under the texture
        // cache names die every second or two, so it is now the per-slot mark NoteOwnedNamesRetired makes in
        // Drain before this runs.
        if (!_bindless) return;
        int tid = (int)name, h = Slot(tid);
        for (int probes = 0; probes < (1 << HandleHashBits); probes++)
        {
            int k = _hTid[h];
            if (k == 0) return;                       // never cached: nothing to drop
            if (k == tid)
            {
                if (gl && _hRes[h] && _hVal[h] != 0 && Gl.MakeTextureHandleNonResidentARB != null)
                    Gl.MakeTextureHandleNonResidentARB(_hVal[h]);
                ZeroHandle(_hVal[h]);
                _hTid[h] = HandleTombstone; _hVal[h] = 0; _hRes[h] = false;
                return;
            }
            h = (h + 1) & ((1 << HandleHashBits) - 1);
        }
    }

    /// <summary>Diagnostic budget for <see cref="HandleDiag"/>. Cold by construction -- the miss path runs
    /// about once per distinct texture per enrollment, not per draw -- and it stops speaking after this many
    /// reports, so it costs one branch on a warm cache.</summary>
    private static int _handleDiagLeft = HandleDiagBudget;
    private const int HandleDiagBudget = 24;
    /// <summary>#153 follow-up: which GL call is failing on the handle path, and with what. The 4.6 bracket
    /// drains <c>glGetError</c> anonymously, so a session reporting "errors 4.6=450" says a fault exists and
    /// nothing about where -- and the two candidate faults are told apart by the CODE.
    /// <c>INVALID_OPERATION</c> after a non-zero handle is a re-resident of an already-resident handle (the
    /// enrollment wipe forgetting residency it never gave up); <c>INVALID_VALUE</c>, or a zero handle from a
    /// tid the material just resolved, is a dead or recycled texture name.
    /// <para>Deliberately drains here rather than in the bracket: a code without its call site is the same
    /// non-answer the bracket already gives.</para></summary>
    private static void HandleDiag(int tid, ulong v)
    {
        uint err = 0, e;
        int guard = 0;
        while ((e = Gl.GetError()) != 0 && ++guard < 8) err = e;
        if (err == 0 && v != 0) return;   // the normal case; this path allocates nothing, so it stays silent
        _handleDiagLeft--;
        _log?.Info($"AlloyRenderer: [handle] tid={tid} handle=0x{v:x} err=0x{err:x} " +
                   (v == 0 ? "-- glGetTextureHandleARB returned NO handle for a tid the material resolved this frame"
                           : "-- the handle came back but the call errored") +
                   $" ({_handleDiagLeft} reports left)");
    }

    /// <summary>#153: does this context expose ARB_bindless_texture? By STRING -- GLX returns non-null
    /// function pointers for unsupported extensions, so the pointers alone prove nothing.</summary>
    private static bool HasBindless()
        => Gl.GetTextureHandleARB != null && Gl.MakeTextureHandleResidentARB != null
           && HasExt("GL_ARB_bindless_texture"u8);

    /// <summary>Is this extension string in the 4.6 CORE context's list? By enumeration, allocation-free.
    /// Must be called inside the bracket -- it is asking the context Alloy draws in, which is not the
    /// engine's 3.3 one and does not necessarily advertise the same set.</summary>
    internal static bool HasExt(ReadOnlySpan<byte> want)
    {
        if (Gl.GetStringi == null) return false;
        int n = 0; Gl.GetIntegerv(Gl.NUM_EXTENSIONS, &n);
        for (uint i = 0; i < (uint)n; i++)
        {
            byte* e = Gl.GetStringi(Gl.EXTENSIONS, i);
            if (e == null) continue;
            int k = 0;
            for (; k < want.Length && e[k] != 0; k++) if (e[k] != want[k]) break;
            if (k == want.Length && e[k] == 0) return true;
        }
        return false;
    }

    /// <summary>#259: what the driver will say about VRAM, probed once inside the bracket. There is no VRAM
    /// query in the client at all (<c>GLRender::GetDeviceInfo_i</c> @0x4d1d10 answers
    /// <c>UNIFORM_BUFFER_OFFSET_ALIGNMENT</c> for query 0 and <c>0xFFFFFFFF</c> for everything else), so the
    /// two vendor extensions are the only route and neither is guaranteed in a 4.6 core context -- which is
    /// why the answer is measured and printed rather than assumed. Both report KiB.</summary>
    private static void ProbeVram()
    {
        _vramProbed = true;
        // The DISCRIMINANT is which extension answered, never the total -- an NVX driver that reports 0 for
        // its dedicated pool would otherwise be queried with the ATI enum.
        _vramNvx = HasExt("GL_NVX_gpu_memory_info"u8);
        if (_vramNvx) { _vramExt = "NVX_gpu_memory_info"; _vramTotalKib = GetInt(GpuMemTotalNvx); }
        else if (HasExt("GL_ATI_meminfo"u8)) _vramExt = "ATI_meminfo";
        _log?.Info(_vramExt == null
            ? "AlloyRenderer: [vram] no GPU memory query in this context (NVX_gpu_memory_info / ATI_meminfo absent) -- residency is reported as Alloy's own bytes only"
            : $"AlloyRenderer: [vram] {_vramExt} present" + (_vramTotalKib > 0 ? $", {_vramTotalKib / 1024}MiB total" : ""));
        if (_vramExt != null) SampleVram();   // the texture-cache budget reads the free pool at the probe, not a frame later
        // #267's open question, answered from the context that would use them rather than from glxinfo.
        _log?.Info("AlloyRenderer: [vram] arena-relevant extensions in this context: "
            + $"ARB_sparse_texture={(HasExt("GL_ARB_sparse_texture"u8) ? "yes" : "no")}, "
            + $"ARB_sparse_texture2={(HasExt("GL_ARB_sparse_texture2"u8) ? "yes" : "no")}, "
            + $"EXT_memory_object={(HasExt("GL_EXT_memory_object"u8) ? "yes" : "no")}");
    }

    /// <summary>Probe once, then keep the free-VRAM figure fresh. Called from the bracket -- the only place
    /// a GL query may be issued at all -- so <c>/knobs</c> can read a number rather than call into the wrong
    /// context from the chat thread. One <c>glGetIntegerv</c> a frame against a frame's worth of GL traffic.
    /// <c>GL_ATI_meminfo</c>'s query is an array whose FIRST element is the free pool.</summary>
    internal static void SampleVram()
    {
        if (!_vramProbed) ProbeVram();
        if (_vramExt == null) return;
        int* v = stackalloc int[4] { 0, 0, 0, 0 };
        Gl.GetIntegerv(_vramNvx ? GpuMemCurrentNvx : TexFreeMemoryAti, v);
        _vramFreeKib = v[0];
    }
    private static int _vramFreeKib;

    /// <summary>GL_GPU_MEMORY_INFO_DEDICATED_VIDMEM_NVX / _CURRENT_AVAILABLE_VIDMEM_NVX /
    /// GL_TEXTURE_FREE_MEMORY_ATI.</summary>
    private const uint GpuMemTotalNvx = 0x9047, GpuMemCurrentNvx = 0x9049, TexFreeMemoryAti = 0x87FC;   // not-an-offset: GL vendor-extension query enums

    private static bool _vramProbed, _vramNvx;
    private static string? _vramExt;
    private static int _vramTotalKib;
    /// <summary>The probe's total, MiB; 0 before the probe or when the device answers nothing.</summary>
    internal static long VramTotalMib => _vramTotalKib / 1024;
    /// <summary>Is the bindless path armed this session (probed at the first lit program build)?</summary>
    internal static bool Bindless => _bindless;
    /// <summary>ATI_meminfo (Mesa) answers FREE, never total; at probe time that is most of the card.</summary>
    internal static long VramFreeMib => _vramFreeKib / 1024;
    internal static bool VramProbed => _vramProbed;

    /// <summary>One <c>glGetIntegerv</c>, for a query that answers a single value.</summary>
    private static int GetInt(uint pname) { int v = 0; Gl.GetIntegerv(pname, &v); return v; }

    /// <summary>#259's <c>/knobs</c> line: what Alloy is holding, and what the device will say about the
    /// pool it is holding it in. Null until the bracket has probed, which is the honest answer before a
    /// frame has run.</summary>
    internal static string? VramNote()
    {
        if (!_vramProbed) return null;
        string device = _vramExt == null
            ? "the device answers no memory query in this context (NVX_gpu_memory_info / ATI_meminfo absent)"
            : $"{_vramExt}: " + (_vramTotalKib > 0 ? $"{_vramTotalKib / 1024}MiB total, " : "")
              + $"{_vramFreeKib / 1024}MiB free";
        return $"[vram] Alloy holds {OwnedTextures.PeakMib}MiB of textures and {OwnedBuffers.PeakMib}MiB of "
             + $"geometry at high water; {device}";
    }

    /// <summary>#153: rewrite a fragment stage's bound samplers into per-draw bindless handles. The stage
    /// already receives <c>vDraw</c> (the record index) for its material colours; the same index now selects
    /// its textures, which is what lets one multidraw span every texture in a class. Pure text -> pinned by
    /// SelfCheck; if the declarations are not found (a content override with its own text) the output lacks
    /// the handle block and the caller stands the bindless path down rather than shipping a half-rewrite.</summary>
    internal static string BindlessFs(string fs)
    {
        string[] names = { "uTex", "uFlowTex", "uNrmTex", "uSpecTex", "uRoughTex", "uHeightTex", "uIllumTex", "uAoTex" };
        int[] slot = { 0, 6, 1, 2, 3, 4, 5, 7 };
        var sb = new System.Text.StringBuilder(
            "#define CX_BINDLESS 1\n#extension GL_ARB_bindless_texture : require\n" +
            $"layout(std430, binding = {BindTexH}) readonly buffer CxTexH {{ uvec2 uTexH[]; }};\n");
        for (int i = 0; i < names.Length; i++)
            sb.Append($"#define {names[i]} sampler2D(uTexH[(vDraw << 3) + {slot[i]}])\n");
        string outFs = fs;
        foreach (string nm in names)
            outFs = System.Text.RegularExpressions.Regex.Replace(outFs, $@"uniform sampler2D {nm};[^\n]*\n", "");
        if (outFs == fs) return fs;   // nothing replaced -- foreign text, leave it alone
        if (!outFs.Contains("flat in int vDraw;")) return fs;   // no record index to sample by
        // GLSL: #extension must precede every non-preprocessor token, so the whole block goes immediately
        // after #version. The #defines are preprocessor (position-free) and only reference vDraw at their
        // EXPANSION sites, which sit far below its declaration; the SSBO decl merely has to precede use.
        int at = outFs.IndexOf('\n', outFs.IndexOf("#version", StringComparison.Ordinal)) + 1;
        return outFs.Insert(at, sb.ToString());
    }

    private static long _prepassDraws;
    /// <summary>#124: prepass draws that went through the keyhole depth stage. The number that says the fix
    /// is doing its job -- before it, every one of these was in <see cref="_prepassSkipped"/> instead.</summary>
    private static long _prepassKeyed;
    /// <summary>#123: draws the prepass had to leave out because their feature moves vertices, plus (#124)
    /// the ones that dissolve, which discard. Reported, not
    /// hidden -- it is the overdraw the prepass no longer removes, and the number that says whether the wind
    /// content is big enough for a displacing prepass stage to be worth building.</summary>
    private static long _prepassSkipped;
    private static long _prepassFeatDraws;
    private static int _lastPixels;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // #165: ahead of every stand-down and every check below -- /alloy reports the renderer for a
        // misrendering session, so it must exist even in a session whose renderer never armed.
        RenderCommands.Register();
        // Up here with the other unconditional checks, ahead of every stand-down: the static set's decision
        // table and the record contract it submits from are ours and depend on no engine symbol, so a build
        // that never arms still reports whether the arithmetic it ships is right. Advisory -- gpu.static is
        // off by default, so a failing table must cost that path and not drop every user back to vanilla.
        StaticSelfCheck(log);
        CullPass.Init(log);
        // #170: ahead of every stand-down, matching the host's unconditional calls. The verdict must
        // outlive a renderer that never armed -- /alloy is the report it drives.
        AlloyGate.Arm(log);
        PresentTarget.Attach(log);
        // #263: the config half of the verdict is the FIRST stand-down, ahead of every Attach and every
        // Install. Steps 4-7 replace engine paths rather than watching them -- Alloy performs the pool
        // upload, mints the texture handles, forces PLT compositing to the CPU -- so arming them in a
        // session the engine is going to render is not a wasted hook, it is a broken world. Restart-only
        // (ADR 0018's amendment to ADR 0010) is exactly what buys the two worlds never being warm at once.
        if (AlloyGate.OptedOut)
        { log.Info("AlloyRenderer: stood down (alloy.enabled=0) -- the engine owns every resource this session"); return; }
        FrameRegistry.Global.Register(FramePhase.PostPresent, 20, EndFrame, "AlloyRenderer.endFrame", log);
        // #175: the whole pass roll-call, in one walk over one declared table. Attach is ahead of every check
        // because #129's asserts that the ownable-bucket list AGREES with whether Alloy water can draw -- and
        // water.enabled off is exactly the case where it must, so water's knobs have to be read first.
        RenderPasses.Register(log);
        PassRegistry.SelfCheck(log);
        PassRegistry.Global.Attach(log);
        // #211: the retired-texture deletes need a context, not a draw. Hung on the health bracket so they
        // still happen while Alloy is stood down (main menu, the disarm latch).
        // #212: and the geometry deletes with them, for exactly the same reason -- the colour bracket's own
        // Drain never runs in those states, so a revoke there would hold the whole owned set for the session.
        GlContextService.InBracket = static () =>
        { OwnedTextures.KeepPltOnCpu(); OwnedTextures.Drain(); SampleVram(); };
        // #117: ahead of every stand-down -- a broken pass order is a misrender in BOTH bracket sites, so the
        // one service that happens to host the check must not be able to opt the check out.
        if (!FrameGraph.SelfCheck(log)) return;
        // #126: log-only. A wrong classification is a wrong-looking frame, not a wrong-drawing one -- the
        // one-owner invariant is upheld by the bucket list, which the pass checks assert against.
        AlloyClasses.SelfCheck(log);
        // #215: here rather than at the shim's own install, which a client without the engine symbols never
        // reaches -- the two things it checks are ours and are wrong in the same way on every client.
        RspShim.SelfCheck(log);
        SelfCheckDrip(log);
        ShaderStages.SelfCheck(log);
        // #262: Alloy does the bone solve itself now, so its arithmetic is ours to be wrong about. Up here
        // with the other unconditional checks -- a client that never arms still reports whether the maths
        // this build ships is right, and the answer does not depend on any engine symbol.
        _skinMathOk = SkinPose.SelfCheck();
        log.Info(_skinMathOk ? "SkinPose.SelfCheck: passed" : "SkinPose.SelfCheck: FAILED -- skinmeshes stay engine-drawn");
        // ADR 0012: the dump is how a customizer gets a current starting point. Ahead of every stand-down
        // below, so a session that owns nothing can still produce one.
        ShaderStages.DumpIfConfigured(log);
        // Every pass's own check. Advisory by convention -- the checks that GATE arming are the contract ones
        // below, which the passes do not own.
        PassRegistry.Global.RunSelfChecks(log);
        // #179: CX_OWNDRAW is gone rather than moved -- the vanilla setting is the same switch, and it
        // stands the shadow and residency sides down too (#263).
        // never arm on a broken contract: the misrender either predicts must not ship
        // #242: the split joins that chain for the same reason -- a record that stopped carrying a
        // per-frame field renders stale state and says nothing at all.
        // #571: the stock-settings reader forges a std::string for the engine; a header packed wrong is a
        // crash on the first world frame, not a misrender, so it joins the gating chain.
        StockConfig.Attach(log);
        if (!SortSelfCheck(log) || !CullSortSelfCheck(log) || !SplitSelfCheck(log) || !ChaseSelfCheck(log) || !KeyholeBoxSelfCheck(log) || !SphereRectSelfCheck(log)
            || !RunsSelfCheck(log)
            || !PartTable.RenderableSelfCheck(log)
            || !ShaderContract.SelfCheck(log)
            || !StockConfig.SelfCheck(log)) return;
        // NOT in the chain above: those two guard what reaches the screen, and this one guards a debug dump.
        // A regression in the canonicaliser should cost /alloy framedump, not drop every user back to vanilla.
        FrameDump.SelfCheck(log);
        // #128: same rule. A wrong gob offset draws a creature at the wrong alpha and a leaky refused-gob set
        // half-owns one -- both misrenders, neither of which errors.
        if (!AlloyCensus.GobSelfCheck(log)) return;
        _getTexture = (delegate* unmanaged<IntPtr, int, long>)Native.Sym(SymbolMap.MatGetTexture);
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        CycleTextures.Attach(log);   // #331
        ResolveChase(log);   // #330: the three engine sets the chase is bounded by; unresolved = unbounded
        _mtxStacks = (byte*)Native.Sym(SymbolMap.MAurMtxStack);
        _runtime = (byte*)Native.Sym(SymbolMap.GlShaderRuntimeData);
        if (_runtime == null)
            log.Info("AlloyRenderer: GLRender::m_shaderExposedRuntimeData unresolved -- #123's feature " +
                     "switches stay inert (no wind sway, roofs where authored), and #124's camera dissolve " +
                     "with them -- uKeyholeCam reads this struct too");
        _shaderConstF = (float*)Native.Sym(SymbolMap.GlGlobalShaderConstantsF);
        _shaderConstI = (int*)Native.Sym(SymbolMap.GlGlobalShaderConstantsI);
        _shaderConstV = (float*)Native.Sym(SymbolMap.GlGlobalShaderConstantsV);
        _shaderConstV4 = (float*)Native.Sym(SymbolMap.GlGlobalShaderConstantsV4);
        // #346: the targeting shape degrades the same way the dissolve does -- unresolved leaves uTgtMode
        // at the "nothing to draw" default, which is a missing overlay, not a misrender.
        if (_shaderConstI == null || _shaderConstV == null || _shaderConstV4 == null)
            log.Info("AlloyRenderer: GLRender::m_globalShaderConstants{i,v,v4} unresolved -- the spell " +
                     "targeting shape stays undrawn on Alloy parts");
        // #124 degrades one term at a time and says which: no fog block leaves Alloy parts unfogged (the
        // pre-#124 look), no constants leaves the dissolve aperture at zero, i.e. no dissolve. Neither is a
        // misrender, and neither is worth standing the pass down for.
        // The dissolve needs BOTH the diameter pair and the runtime struct's camera terms: without the
        // latter uKeyholeCam stays (0,0), the fragment offset never goes negative, and the aperture never
        // opens. Naming only one of them would send the next reader to the wrong symbol.
        if (_shaderConstF == null || _runtime == null)
            log.Info("AlloyRenderer: " +
                     (_shaderConstF == null ? "GLRender::m_globalShaderConstantsf" : "GLRender::m_shaderExposedRuntimeData") +
                     " unresolved -- #124's camera dissolve stays off on Alloy parts");
        if (_getTexture == null || !AurTex.Ready || _primTable == null || _mtxStacks == null)
        { log.Info("AlloyRenderer: engine symbols unresolved -- Alloy world pass unavailable"); return; }

        _origRsp = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.RenderSinglePass, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnRenderSinglePass);
        // #132: the frame anchor. After-original, so the pass runs with the world FBO bound and cleared, the
        // scene fog and the door/tile fades already written for this frame, and nothing drawn yet.
        _origFade = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.FadeDoors, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnFadeDoors);
        // #336: the highlight SOURCE. Cold -- it fires on a mouseover edge or a VFX starting, never per
        // frame -- and it is the only place the channel that produced a highlight is visible.
        _origRefreshHilite = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.RefreshHilite, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnRefreshHilite);
        if (_origRefreshHilite == null)
            log.Info("AlloyRenderer: RefreshHilite unhooked -- no highlight claims a source, so every"
                     + " highlight keeps the engine glow and the owned outline draws nothing");
        _repackSlot = hooks.Chain(SymbolMap.VdoRepack,   // shared symbol: draw funnels + Alloy cast invalidate too (#88)
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnVdoRepackHook, RepackPriority);
        _commitSlot = hooks.Chain(SymbolMap.VdoCommitItemDynamicData,   // the item-level vertex-dirty signal
            (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, void>)&OnCommitItemDynamicData, CommitPriority);
        if (_commitSlot == null) log.Info("AlloyRenderer: CommitItemDynamicData unhooked -- vertex-dirty stays per pool buffer");
        // #137: the skinmesh class. Config bucket (ADR 0013): a capability gate that decides what the vertex
        // stages are BUILT from, so it cannot move mid-session and is not a command. #262 dropped its second
        // term -- the pose no longer comes out of GLRender::m_bones, so that symbol resolving proves nothing.
        // A failed check disarms the class rather than posing a crowd from a transposed matrix -- the engine
        // still draws every cloak in that case.
        _skinning = Renderer.OwnSkin.Boot && _skinMathOk;
        _enrollBudget = Renderer.EnrollBudget.Boot;
        _enrollDelta = Renderer.EnrollDelta.Boot;
        _isEnabled = (delegate* unmanaged<int, byte>)Native.Sym(SymbolMap.RiIsEnabled);
        ResolveVaoDrip(log);
        OccludeInit(log);   // #537
        // Hook install is its own walk, and it stays HERE rather than joining Attach above: a pass that hooks
        // the engine on a client where the Alloy renderer cannot arm would be suppressing draws nothing
        // replaces. Everything above this line has already returned in that case.
        PassRegistry.Global.Install(log, hooks);
        // #215: last, and after PassRegistry -- the shim calls through hook sites every pass above it owns
        // (the beam window, the emitter suppression, the bucket gate), so they have to be installed first.
        RspShim.Install(log, hooks);
        _armed = _origRsp != null && _origFade != null;
        if (_armed) log.Info(_skinning
            ? "AlloyRenderer: #262 skinmeshes owned -- posed per frame from engine animation state, no draw re-entry (alloy.ownskin=0 in cx.cfg disables)"
            : "AlloyRenderer: skinmeshes stay engine-drawn (cloaks, robes, tails)");
        log.Info(_armed
            ? "AlloyRenderer: armed -- Alloy world pass at the #132 frame anchor, Scene::FadeDoors (alloy.enabled=0 in cx.cfg disables)"
            : $"AlloyRenderer: {(_origRsp == null ? "RenderSinglePass" : "Scene::FadeDoors")} hook failed -- Alloy world pass unavailable");
    }

    /// <summary>Boot worker count for the stage: six, xtul's call. Live-verified 2026-09-05 on 12 cores: audit
    /// 0/0/0/0/0, Build 2,6 -> 1,9ms. A woken worker spins up to 4ms, so on a machine with fewer than eight cores
    /// this oversubscribes; <c>/alloy threads N</c> is the dial.</summary>
    internal const int DefThreads = 6;
    /// <summary>#329: the <c>alloy.enrollbudget</c> default -- candidates per frame, 0 = the whole walk in one frame.</summary>
    internal const int EnrollBudgetDefault = 1500;
    /// <summary>#336: does the prepass run its keyhole sweep? See <c>/alloy prepasskey</c>.</summary>
    private static bool _prepassKey => Renderer.PrepassKey.V;

    /// <summary>#350: <c>scriptableInt1..16</c> and <c>scriptableFloat1..16</c> out of the engine's own
    /// constant tables, exactly what <c>UploadVanillaEnv</c> writes. Cold; the input thread reading ints the
    /// render thread also only reads.</summary>
    internal static unsafe string DescribeScriptables()
    {
        if (_shaderConstI == null || _shaderConstF == null) return "scriptables: no table";
        var sb = new System.Text.StringBuilder("scriptableInt");
        for (int i = 0; i < ScriptableSlots; i++) sb.Append(' ').Append(i + 1).Append('=').Append(_shaderConstI[SIntBase + i]);
        sb.Append("  scriptableFloat");
        for (int i = 0; i < ScriptableSlots; i++) sb.Append(' ').Append(i + 1).Append('=').Append(_shaderConstF[SFloatBase + i].ToString("F2"));
        return sb.ToString();
    }

    /// <summary>The <c>alloy.consts</c> button: the scriptable block as the client table holds it NOW -- the
    /// numbers a hosted content stage gates on (snow, wet, rain).</summary>
    internal static void LogScriptables() => _log?.Info("AlloyRenderer: " + DescribeScriptables());

    /// <summary>#132's anchor site. After-original: <c>Scene::FadeDoors</c> is engine bookkeeping the owned
    /// pass reads, so it has to have run. A failed install leaves <see cref="_origFade"/> null and the pass
    /// never draws -- reported at arm time rather than as a silently vanilla world.</summary>
    [UnmanagedCallersOnly]
    private static void OnFadeDoors(IntPtr scene)
    {
        if (_origFade != null) _origFade(scene);
        // #257: the frame constants, read here and only here. This is the one point in the frame that is
        // both Alloy's own and correctly ordered -- the engine's EnableLights has advanced the area lights'
        // fade weights, CurrentCamera holds the camera whose scene is rendering (Camera::RenderScene sets it
        // at @0x4a822c and clears it at @0x4a8324), and nothing has drawn. It runs on BOTH paths, because
        // the vanilla RenderSinglePass calls FadeDoors at the same step the shim vcalls it.
        // ...and the re-read here is what makes the AREA terms this frame's: LightManager::EnableLights
        // advanced their fade weights inside RSP, after SceneInputs' own earlier read.
        if (IsWorldScene(scene))
        {
            FrameRead.Tick(scene);
            SceneInputs.FrameStart(scene);
        }
        OnFrameAnchor(scene);
    }

    [UnmanagedCallersOnly]
    private static void OnRenderSinglePass(IntPtr scene)
    {
        PassGen++;   // unconditionally: a stale generation would keep the iterator skipping parts nobody draws
        bool world = IsWorldScene(scene);
        // #571: the stock graphics snapshot is retaken on a new world Scene (boot, every area edge), ahead of
        // and regardless of the verdict -- the verdict reads it. A toggle the player flips off AFTER the
        // decision froze has nothing behind it (exclusive residency), so it is the gamma precedent's Fail.
        if (world)
        {
            StockConfig.OnScene(scene);
            if (AlloyGate.Alloy && !StockConfig.EnhancedLighting)
                AlloyGate.Fail("Enhanced Lighting was turned off in Game Options (graphics.lighting.enhanced) and Alloy has no vertex-lit path");
        }
        if (world && AlloyGate.Alloy)
        {
            // saturating, never wrapping: an unchecked ++ past int.MaxValue lands on int.MinValue and the
            // threshold is then unreachable for 2^31 passes -- i.e. enrollment silently never runs
            // #236: the latch enrolls as soon as the throttle opens, so the bound on ownership latency is
            // NewCandidateGap frames from the completing walk rather than the full cadence.
            long tEnroll = FrameZones.Now;
            long bcn0 = FrameZones.Ticks(FrameZones.Zone.Bcn) + FrameZones.Ticks(FrameZones.Zone.Prebuild);   // the drips ride this slot; Enroll must not absorb them
            // #256: the world walk FIRST -- one int load in the common case, a re-walk on the engine's own
            // membership epoch. Enrollment is then edge-triggered on what that walk changed, which is what
            // retires the 60-frame cadence: there is no clock here any more.
            SceneWalk.Tick(scene);
            // #261: bucket 15 is not part of the world walk and never will be -- it is a per-frame,
            // culled classification the engine builds ahead of RSP. Read here rather than enrolled on an
            // edge, in engine context, because that is what the beam VAO harvest needs.
            Beams.Enroll(scene);
            Halos.Enroll(scene);
            // #261: the sky's three gobs are not in Scene+0x130 either -- they hang off Scene+0x18/+0x38 and
            // only Scene::RenderSkyBoxes ever walked them. Same slot, same reason as beams.
            SkyBox.Enroll(scene);
            // #284: the unbuilt-texture retry, and it belongs HERE and not in the 4.6 bracket.
            // CAurTexture::BuildAndStore re-runs the engine's own upload -- SetActiveTexture, AurTextureBindTID,
            // RenderInterface::CreateTexture -- and the engine caches what it thinks is bound. Run inside
            // Alloy's context those binds land on Alloy's objects while the engine's cache records them,
            // which is #220's corruption with a different cause. Engine context, once a frame, budgeted.
            OwnedTextures.DripRebuilds();
            // #284: and the other engine-draw-driven lazy initializer, on the same slot and for the same
            // context reason -- PartSkin::PostProcess @0x483120 ends in RenderInterface pool traffic, and
            // its only vanilla caller was RenderSkinned, i.e. a draw this branch no longer performs.
            SkinPose.DripPrepares();
            // The FLOOR, and it is not a cadence: nothing here fires because time passed, it only delays an
            // edge that has already happened. Enrollment is the cold path (handle wipe, full table scan,
            // texture re-intern, VAO retire) and the walk's delta signal is unbounded by construction --
            // #236 put a floor under the old new-candidate latch for exactly this reason and deleting the
            // cadence took it with it. Measured live at 0,04-0,08 epoch moves per frame (a walk every 12-20
            // frames), so at the observed rate this binds almost never; it exists for the churnier area.
            if (_sinceEnroll < int.MaxValue) _sinceEnroll++;
            bool due = SceneWalk.Dirty || scene != _enrollScene;
            // #329: a walk in progress steps every frame; the floor and the edge gate only its BEGIN.
            if (Walking(scene) || (due && (_sinceEnroll >= EnrollFloor || scene != _enrollScene))) { Enroll(scene); FrameZones.MarkEnrollFrame(); }
            FrameZones.Add(FrameZones.Zone.Enroll, tEnroll, FrameZones.Ticks(FrameZones.Zone.Bcn) + FrameZones.Ticks(FrameZones.Zone.Prebuild) - bcn0);
        }
        // #215: THE CUTOVER. Same gate the enrollment above uses, plus the shim's own arming -- so the key
        // being on is never enough on its own: a pass with no 4.6 context, no module, or a vanilla verdict still gets
        // the engine's own RenderSinglePass, which is the only frame there is behind it.
        if (world && Armed4Shim)
        {
            // #256: the first walk of a new Scene reaches parts whose geometry is not resident yet, so it
            // enrols records that cannot bind until SetupArrays has run for them. The retry budget covers
            // exactly that window -- a Scene that has enrolled NOTHING keeps asking, bounded, rather than
            // waiting on an epoch bump that a purely resident-state change never produces.
            // Against its OWN latch, not against _enrollScene: the enroll block above already ran under the
            // same gate and set _enrollScene = scene, so a test on that is always false by the time it is
            // reached and the budget would silently be per-session.
            if (scene != _forcedScene) { _forcedScene = scene; _forcedEnrolls = 0; }
            // #308: enrolled-count, not the slot high-water mark -- slots are stable now, so _ownCount
            // never returns to zero once anything has enrolled. Zero ENROLLED covers both residency shapes
            // of the race this budget bridges: no candidates yet (records not resident), and candidates
            // that all refused transiently (the VDO drip deferring while pools lack GL names -- nothing
            // re-dirties the walk on pool residency, so without the retry a scene sticks at zero ownership
            // with the engine's 3D draw swallowed). A scan that accepted parts and dropped them all to the
            // all-owned-or-none sweeps re-fires too; that waste is bounded by the same 8-scan budget.
            // ...and the same race at PARTIAL ownership, which the zero test cannot see. An enrollment that
            // left VDOs deferred or past the dedup table has unfinished business, but a walk only re-runs on
            // an edge and none of those states raise one -- so a dense area settled at 910 of 1300 parts and
            // stayed there, the holes permanent. Same budget: the re-ask is bounded per scene, so a VDO that
            // will never harvest cannot spin the walk.
            if ((_lastEnrolled == 0 || DripPending) && _forcedEnrolls < MaxForcedEnrolls)
            { SceneWalk.Dirty = true; _forcedEnrolls++; }
            bool prevShim = _inColourPass;
            _inColourPass = true;
            RspShim.Run(scene);
            _inColourPass = prevShim;
            return;
        }
        if (_origRsp != null) _origRsp(scene);
    }

    /// <summary>Is this the world Scene? A Scene's static tile BSP (<see cref="EngineLayout.SceneBspRoot"/>) is
    /// written by <c>Scene::InitBSP</c> alone (@0x46c330; <c>Scene::Scene</c> zeroes it, and every
    /// <c>SceneBSPAdd*</c> only inserts into an existing root). A GUI 3D panel's Scene -- which reaches the
    /// same <c>Scene::RenderSinglePass</c> through <c>Camera::RenderScene</c>, off <c>Camera+0x90</c> -- is
    /// built by <c>CGuiPanel::CreateSceneView</c> @0x68759e: <c>NewCAurScene("sceneGUI_&lt;panel&gt;")</c> +
    /// camera + SetAmbient, no InitBSP, so its root stays null. This is also the engine's own test --
    /// <c>ManageSceneBSP</c> @0x47fa80 early-outs on exactly it.</summary>
    internal static bool IsWorldScene(IntPtr scene) =>
        scene != IntPtr.Zero && *(IntPtr*)(scene + EngineLayout.SceneBspRoot) != IntPtr.Zero;

    /// <summary>#216: the terms <see cref="AlloyGate"/> reads out of this class. <c>_armed</c> is "the hooks
    /// installed"; the capability pair is "a program built and the multidraw entry points exist", both of
    /// which latch and are deliberately never cleared by a context loss (see <see cref="ContextLost"/>).</summary>
    internal static bool ArmedForFrame => _armed;
    internal static bool DrawCapable => !_progFailed && !_batchUnavailable;

    /// <summary>#215: may the shim replace this pass? Taking the engine's frame away and THEN refusing to
    /// draw leaves the world as bare clear colour under the 2D UI, with nothing behind it -- so every
    /// refusal <see cref="DrawOwned"/> can answer from state that outlives the pass is asked here first.
    /// <para><c>PostChain.On</c> is the <c>hdr=0</c> case and <see cref="PartTable"/><c>.Active</c> the
    /// <c>drawhooks=off</c>/<c>=stats</c> one: both are supported settings that would otherwise produce a
    /// permanently empty world with no reason given.
    /// <c>PartTable.Active</c> is read HERE rather than at the shim's install because DrawFunnelService
    /// initializes after this service (discovery is alphabetical) and has not set it yet.</para>
    /// <para>What this canNOT cover is the gamma refusal, which is re-asked per pass inside the anchor,
    /// after that pass's engine frame is already gone. ADR 0016 scopes exactly that to step 7 (#216): every
    /// refusal becomes frame-start-only, because there is no mid-frame fallback once the shim is in.</para></summary>
    private static bool Armed4Shim => RspShim.Ready && AlloyGate.Alloy;   // AlloyGate.Alloy implies RspShim.Armed (#458)

    /// <summary>#215: consecutive enrollments forced by an empty ownership set, and the ceiling on them.
    /// #256: what they now wait for is RESIDENCY -- the first walk of a Scene records parts whose
    /// VertexPrimitive has no VDO yet, and each frame's rebind pass binds a few more. Without the cap, an
    /// area with genuinely nothing ownable would run a full table scan at frame rate forever.</summary>
    private static int _forcedEnrolls;
    private static IntPtr _forcedScene;
    private const int MaxForcedEnrolls = 8;

    /// <summary>Inside <see cref="RspShim.Run"/>, i.e. a world colour pass that is Alloy's. The anchor draws
    /// only then: since #458 there is no Alloy drawing inside the engine's own RenderSinglePass.</summary>
    private static bool _inColourPass;
    /// <summary>Scene the ownership table was enrolled from. The Alloy block draws only into a bucket walk of
    /// THIS Scene, which is what makes one-owner true by construction rather than by the colour-pass gate
    /// alone. An area transition that replaces the Scene leaves it stale, so nothing draws until the next
    /// enrollment picks up the new one -- which the walk's Scene check triggers on the same frame.</summary>
    private static IntPtr _enrollScene;
    private static int _drawnPass = -1;   // pass whose Alloy block already ran (once per pass)

    /// <summary>Ownable parts the last enrollment turned away for want of a slot. Non-zero means the area is
    /// past <see cref="MaxOwned"/>.
    /// <para>#264 changed what that costs. It used to mean "these tiles stay vanilla", re-picked every
    /// enrollment by hash-table walk order, so the world looked spotty and shifted under a static camera.
    /// With the engine walk deleted there is no vanilla to spill to: a spilled part is INVISIBLE. This is the
    /// first number to read when geometry is missing.</para></summary>
    private static int _capacitySpill;

    /// <summary>The capacity clause of the [alloy] line -- silent when there is room, loud when there is not,
    /// because "4082/5631" only reads as a bug if you happen to know what MaxOwned is.</summary>
    internal static string CapacityNote()
        => _capacitySpill == 0 ? "" :
           $" (AT CAPACITY: {_capacitySpill} ownable parts spilled, MaxOwned={MaxOwned} -- since #264 nothing "
         + "else draws the world, so those parts are NOT RENDERED AT ALL, and the spill set re-picks itself "
         + "every enrollment, so which geometry is missing changes as you watch)";

    /// <summary>#132's frame anchor: the Alloy colour block, run from an after-original hook on
    /// <c>Scene::FadeDoors</c> -- the last engine call inside <c>Scene::RenderSinglePass</c> before
    /// <c>RenderDrawBucket(0)</c>. Every Alloy part -- #126's transparent, fade, cap and additive classes
    /// included -- draws here, ahead of every bucket any of them is sighted in (engine order
    /// 0 -> [sky] -> 1 -> 7 -> 2 -> 3 -> 5 -> 8 -> 6 -> 4). That is what keeps suppression honest, and it is
    /// also why the blending classes composite before the engine's still-vanilla dynamic geometry -- the
    /// approximation named on <see cref="TransparentPass"/>.
    /// <para>Spec #114 asks for "RenderSinglePass entry" and that is not reachable: RSP's first act is
    /// <c>Camera::InitializeFrameBufferResources</c>, whose steady-state path binds the world FBO and then
    /// <c>Clear(7)</c>s it every frame, so a resolve written at literal entry is wiped -- and deferring the
    /// resolve past the clear costs a second colour bracket ADR 0011 does not have. See
    /// <see cref="SymbolMap.FadeDoors"/> for the decompile. The anchor therefore sits after the clear and
    /// after the two Scene vcalls that follow it (<c>DoFog</c>, <c>FadeDoors</c>), both of which write state
    /// this pass reads.</para></summary>
    private static void OnFrameAnchor(IntPtr scene)
    {
        // #86: one gate covers the whole pass. Suppression needs no second one -- the iterator only skips a
        // part whose OwnGen matches this pass, and nothing stamps it when the Alloy block does not run.
        if (!AlloyGate.Alloy || _orderCount == 0) return;   // #308: live count, not the slot high-water mark
        // #91: never paint into a foreign Scene. Only a NON-world pass counts as a foreign sighting -- a world
        // pass that misses is the enrolled Scene having been replaced (area transition), which resolves itself
        // within a walk or two and must not read as evidence that GUI panels reach this anchor.
        if (scene != _enrollScene) return;
        if (!_inColourPass) return;
        if (_drawnPass == PassGen) return;
        _drawnPass = PassGen;
        // Engine context, before the bracket: harvesting a VDO's attribute formats is a read of the ENGINE's
        // VAO (see VdoSlot), so it cannot happen on the 4.6 side.
        for (int i = 0; i < SkyBox.Count; i++)
        {
            ref SkyBox.Part p = ref SkyBox.At(i);
            p.Vao = SkyBox.Drawable(i) ? VdoSlot(p.Vdo) : -1;
        }
        SampleEngineSets();   // #330: engine context, after AddPartsToDrawBuckets, before the first chase
        DrawOwned();
    }

    /// <summary>#137/#262: solve every Alloy skinmesh's pose for THIS frame, before the bracket -- and since
    /// #132 after <see cref="PostChain.Precheck"/>, so a pass that is standing down does not solve poses the
    /// engine is about to solve again itself.
    /// <para>#262 changed WHERE the pose comes from, not when. It used to come from re-entering
    /// <c>PartSkin::Draw</c> with the draw funnel swallowed, purely to make the engine's own bone solve
    /// happen, and then copying <c>GLRender::m_bones</c> out. The reason given for that was a statement about
    /// VANILLA's draw order -- the Alloy pass runs before the engine walks the dynamic buckets, so m_bones
    /// held the previous frame's last skinned part -- and under the cutover the shim decides when the walk
    /// happens. The residual constraint is real and unchanged: the ENGINE owns animation state. So
    /// <see cref="SkinPose"/> READS that state and does the solve itself, and no engine draw function is
    /// entered at all.</para>
    /// <para>A part whose pose does not come back (its <c>MdlNodeSkin</c> is not prepared yet, or its tree
    /// overflowed the walk) is un-owned here and now, which since #264 means it is not drawn this frame.
    /// An unprepared <c>MdlNodeSkin</c> used to be repaired by the engine's own draw of the part, and there
    /// is no such draw any more -- so #284 gave the repair an Alloy edge of its own
    /// (<see cref="SkinPose.DripPrepares"/>, from the frame anchor). The refusal here is one frame, not the
    /// session; a cloak seen permanently missing is now a number on the period line, not a mystery.
    /// <para>#308 follow-up: the shadow bracket's pose. The bake used to sample the bone block the LAST
    /// colour build uploaded -- pose N-1 under a colour pass drawing pose N -- and on a cloak lit by its own
    /// carried torch that one-frame disagreement flips the self-shadow compare's sign every frame: the
    /// flicker. The solve now runs ONCE per frame at the shadow bracket (CPU half here, upload half in
    /// <see cref="ShadowPoseUpload"/>), and the colour preamble reuses it instead of solving again.</para></summary>
    internal static bool PosedByShadow;

    internal static void ShadowPose()
    {
        if (!AlloyGate.Alloy) return;
        long tPose = FrameZones.Now;
        PoseSkinned();
        FrameZones.Add(FrameZones.Zone.Pose, tPose);
        PosedByShadow = true;
    }

    /// <summary>The GL half of <see cref="ShadowPose"/>: push the solved block. Must run inside the 4.6
    /// bracket.</summary>
    internal static void ShadowPoseUpload()
    {
        if (PosedByShadow) UploadBones();
    }

    /// <summary>#308 follow-up: bone slots are STICKY -- a part keeps its slot in the bone block across
    /// frames, allocation mirroring the ownership table's own stable-slot scheme. The slots used to be
    /// assigned sequentially per frame, which the shadow-bracket pose made load-bearing: the bake pairs the
    /// LAST colour build's records (whose feat.y bone bases name last frame's layout) with THIS frame's
    /// block, so a layout that shifted on a population change baked every caster past the shift with
    /// another creature's bones for a frame. A sticky slot never shifts under a survivor.</summary>
    private static readonly int[] _boneFree = new int[MaxBoneSlots];
    private static int _boneFreeN, _boneTop;
    private static int[] _boneOwner = new int[MaxBoneSlots];   // ownership slot holding each bone slot, -1 free

    private static int AllocBoneSlot(int owner)
    {
        int s;
        if (_boneFreeN > 0) s = _boneFree[--_boneFreeN];
        else
        {
            if (!EnsureBoneSlots(_boneTop + 1)) return -1;
            s = _boneTop++;
            if (s >= _boneOwner.Length) Array.Resize(ref _boneOwner, _boneCap);
        }
        _boneOwner[s] = owner;
        return s;
    }

    private static void FreeBoneSlot(int s)
    {
        if (s < 0 || s >= _boneTop || _boneOwner[s] < 0) return;
        _boneOwner[s] = -1;
        if (_boneFreeN < _boneFree.Length) _boneFree[_boneFreeN++] = s;
    }

    private static void PoseSkinned()
    {
        // Nothing to pose for if the pass cannot draw: DrawOwned bails on these two without stamping a
        // single part, so the engine walks every one of them itself -- and would then solve each pose a
        // second time.
        if (!_skinning || _progFailed || _batchUnavailable) { _boneSlots = 0; return; }
        for (int i = 0; i < _ownCount; i++)
        {
            int prev = _oBone[i];
            _oBone[i] = -1;
            if (_oTri[i] == IntPtr.Zero || !_vSkin[_oVao[i]]) { FreeBoneSlot(prev); continue; }
            // Identity first, and here it is not the usual "a stale read is acceptable" case: the solve
            // chases this part's parent chain, child array and MdlNode, so a recycled pointer is a CHASED
            // one. The same single-level compare BuildBatches revokes on. A failure here is not counted as
            // an unsolved pose -- the part was already gone, and conflating the two would send the next
            // diagnosis after a skinning bug that is really a lifetime one.
            if (!IdentityOk(i, _oTri[i])) { FreeBoneSlot(prev); RevokeSkinned(i); continue; }
            int slot = prev >= 0 && prev < _boneTop && _boneOwner[prev] == i ? prev : AllocBoneSlot(i);
            if (slot < 0 || !SkinPose.Solve(_oTri[i], _boneStage + (long)slot * BoneFloats))
            { _skinNoPose++; FreeBoneSlot(slot); RevokeSkinned(i); continue; }
            _oBone[i] = slot;
            _skinDrawn++;
            // #302: no separate shadow feed. The pose lands in _oBone/feat.y and the bake reads it through
            // the same record and the same bone block the colour pass draws from.
        }
        // Reclaim orphans: an enrollment can retire or re-tenant an ownership slot without this walk seeing
        // the handover (a fresh tenant starts at _oBone -1), leaving a bone slot whose owner no longer
        // points back. _boneTop is the peak skinned population -- tiny -- so the sweep is a few compares.
        for (int s = 0; s < _boneTop; s++)
            if (_boneOwner[s] >= 0 && _oBone[_boneOwner[s]] != s) FreeBoneSlot(s);
        // The block uploads the whole (holey) high water: stale floats in freed slots are never indexed,
        // and a contiguous re-pack is exactly the layout shift stickiness exists to prevent.
        _boneSlots = _boneTop;
        if (_boneTop > _boneHigh) _boneHigh = _boneTop;
    }

    /// <summary>#262: room for <paramref name="want"/> bone slots, doubling the stage rather than dropping a
    /// creature past a fixed budget. Native memory on purpose -- the growth is rare but it happens on the
    /// render thread, and a managed array of this size would hand the collector a gen-2 allocation there.
    /// False = the allocator refused; the caller hands the part back like any other unsolved pose.
    /// <para>The catch is the whole point of the return value. <c>NativeMemory.Realloc</c> does not return
    /// null on failure -- it THROWS -- and this runs inside the present hook, so an escaping exception
    /// crosses an <c>[UnmanagedCallersOnly]</c> boundary and takes the client down instead of costing one
    /// creature a frame of vanilla shading.</para></summary>
    private static bool EnsureBoneSlots(int want)
    {
        if (want <= _boneCap) return true;
        int cap = _boneCap == 0 ? MaxBoneSlots : _boneCap;
        while (cap < want) cap *= 2;
        float* next;
        try { next = (float*)NativeMemory.Realloc(_boneStage, (nuint)cap * BoneFloats * sizeof(float)); }
        catch (OutOfMemoryException) { return false; }
        if (next == null) return false;
        _boneStage = next; _boneCap = cap;
        return true;
    }

    /// <summary>#137: hand an unposable skinmesh back to the engine -- AND the rest of its creature with it.
    /// <para>The handing back is in time to matter: the iterator's suppression runs at buckets 2/3, later in
    /// the same frame, so clearing the flag here is a fallback rather than a dropped part.</para>
    /// <para>The rest of the creature is #128's all-owned-or-none rule, which enrollment enforces through
    /// <c>OwnRefuse</c> and this cannot: the refused-gob set is cleared at the start of every scan, so a mark
    /// made between enrollments is wiped before the sweep reads it. Done inline instead. Without it, a crowd
    /// past the bone budget draws bodies through the Alloy pass and their cloaks through the engine, and
    /// re-enrolls into the same split every scan -- a mixed-renderer seam that persists rather than resolves.
    /// O(n) per failure, and failures are the budget's edge, not the common path.</para></summary>
    private static void RevokeSkinned(int i)
    {
        IntPtr gob = _oGob[i];
        Revoke(i);
        if (gob == IntPtr.Zero) return;
        for (int j = 0; j < _ownCount; j++)
            if (j != i && _oTri[j] != IntPtr.Zero && _oGob[j] == gob) { Revoke(j); _oBone[j] = -1; }
    }

    /// <summary>Chain priority on Repack: invalidation order among observers doesn't matter.</summary>
    internal const int RepackPriority = 30;
    private static IntPtr* _repackSlot;

    [UnmanagedCallersOnly]
    private static void OnVdoRepackHook(IntPtr vdo)
    {
        var next = (delegate* unmanaged<IntPtr, void>)*_repackSlot;
        if (next != null) next(vdo);
        // Repack moved item indices/offsets under us: revoke every part on that VDO, re-enroll later
        for (int i = 0; i < _ownCount; i++)
            if (_oTri[i] != IntPtr.Zero && _oVdo[i] == vdo) Revoke(i);
        // #130: beams keep a VAO cache of their own (their VDOs have no UV stream, so they cannot ride this
        // one) and it holds harvested buffer ids and item offsets a repack invalidates just the same.
        Beams.OnRepack(vdo);
    }

    /// <summary>#209: this pass's own context-loss slot, which it drove inline (and incompletely) before the
    /// lifetime audit. Every GL NAME here belongs to the 4.6 context, so nothing is deleted -- the objects
    /// went with it -- the names are dropped so each Ensure rebuilds instead of short-circuiting on a
    /// non-zero handle and drawing through a dead one. That is #175's bug, in the one pass that still had it.
    /// <para>Uniform locations are not listed: every one of them is re-fetched by the Ensure that rebuilds
    /// its program. <c>_progFailed</c>/<c>_litFailed</c> are deliberately NOT cleared -- a stage that could
    /// not build is a fact about the source, not about the context, and clearing them would re-arm a pass
    /// EndFrame already disarmed. <c>_waterFailed</c> is cleared because it also latches a knob state
    /// (<c>Programs.cs:18</c>), so it has to be re-derived rather than carried.</para></summary>
    internal static void ContextLost()
    {
        _prog46 = 0; _progLit46 = 0; _progLitHost = 0; _progLitList = 0; OitPass.ListsArmed = false; _progDepth = 0; _progDepthKey = 0; _progDepthFeat = 0; _progWater = 0; _progSkyDecode = 0;
        HighlightContextLost();
        _drawBuf = 0; _texHBuf = 0; _boneBuf = 0;
        MotionContextLost();
        // #242: ...and the persistent records went with the buffers that held them. Both halves matter -- the
        // capacity so the next upload reallocates rather than writing into a dead name, and the epoch so
        // every record is rebuilt rather than assumed still on the GPU.
        _slotBuf = 0; _recCapGpu = 0; InvalidateRecords();
        // ...and the VAO cache. RevokeAll marks the slots stale, and EnsureVao DELETES a stale non-zero name
        // before rebuilding -- which on a name minted by a context that no longer exists is a delete aimed at
        // whatever the next context recycles that number for.
        // #136: the engine-layout VAO cache goes with it. Same rule, same reason -- a container object is
        // not shared, so every name in it belonged to the context that just went.
        for (int v = 0; v < MaxVdos; v++) { _vVao46[v] = 0; _vOwned[v] = false; ForgetEngineVao(v); ForgetLodVao(v); }
        _waterFailed = false;
        // #257: and so is the target latch -- every name in it belongs to the context that just went.
        ForgetTarget();
        DepthResource.Revoke();
        CullPass.ContextLost();
        PresentTarget.ContextLost();   // #456: names only -- the images died with the context
    }

    /// <summary>Frame tick from the present (after the bucket walks). Module unload, a PartTable
    /// wipe and a lost 4.6 context all invalidate every recorded identity wholesale.</summary>
    public static void EndFrame()
    {
        if (!_armed) return;
        _frameSeq++;
        // Disarming must never be silent: the [alloy] period line is the only evidence this pass runs at
        // all, so its ABSENCE has to name a reason rather than read as "no news". RevokeAll only speaks when
        // something was owned, which is exactly not the case when the program failed before the first enroll.
        if (_progFailed || _batchUnavailable)
        {
            RevokeAll(_progFailed ? "program failed" : "multidraw unavailable");
            _armed = false;
            _log?.Info("AlloyRenderer: DISARMED (" + (_progFailed
                           ? "program build failed"
                           : "glMultiDrawElementsBaseVertex or glBindBufferBase missing") +
                       ") -- the Alloy world pass is off for this session and no [alloy] lines will follow");
            return;
        }
        if (!GlContextService.Available) { RevokeAll("4.6 context lost"); PassRegistry.Global.ContextLost(); }
        bool inModule = GameGate.InModule;
        if (_wasInModule && !inModule) { RevokeAll("module unload"); _spliceStale = true; OwnedTextures.ModuleUnloaded(); StockConfig.ModuleUnloaded(); }
        _wasInModule = inModule;
        if (_tableGen != PartTable.TableGen) { RevokeAll("part table wiped"); _tableGen = PartTable.TableGen; }

        // the overflow counter lives in a GL buffer, so its readback has to happen inside the 4.6 bracket:
        // arm it one frame ahead of the log so the value the log prints is this period's
        int period = _loggedOnce ? LogEvery : FirstLogAt;
        if (_frames == period - 2) _takeOverflow = true;
        if (++_frames < period) return;
        _loggedOnce = true;
        MeshCensus();   // #329: off the walk, for the line below alone
        // distinct textures is the BATCH FLOOR: one program + one VDO cannot batch below one call per texture,
        // so batches/frame ~= textures tells you the grouping is optimal and the ratio is scene-bound, while
        // batches >> textures says the sort is failing. Without it the ratio alone cannot distinguish the two.
        // #120 made the floor a SOFT one: slots 1..5 joined the batch key but not the radix digit (which still
        // sorts on the diffuse alone), so two materials sharing a diffuse and differing in their normal map
        // now cut a batch the count does not predict. A small excess over the floor is that, not a regression.
        // #264: the gap between these two is a HOLE now, not a ratio. Every candidate the enrollment turned
        // away used to fall through to the engine's walk and draw vanilla; there is no walk, so it is simply
        // not drawn. Named in the line rather than left to subtraction -- "6944/7144" reads as coverage, and
        // "200 NOT DRAWN" reads as the missing geometry it actually is. The reject breakdown at the end of
        // this line is what it decomposes into.
        int holes = _lastCandidates - _lastEnrolled;
        // #325: the old single colour timer's span, opaque through additive (water = copy + draw).
        double colourMs = 0; for (int t = (int)GpuTerm.Opaque; t <= (int)GpuTerm.Additive; t++) colourMs += GpuMs((GpuTerm)t);
        _log?.Info($"AlloyRenderer: [alloy] {_lastEnrolled}/{_lastCandidates} parts owned{CapacityNote()}" +
                   (holes <= 0 ? "" : $" ({holes} NOT DRAWN -- nothing renders an unowned part since #264"
                                       + $"; harvest refused VDOs: no-engine-vao {HarvestNoVao} no-elembuf {HarvestNoElem}"
                                       + $" no-pos {HarvestNoPos} no-uv {HarvestNoUv})") +
                   // #284: OUTSIDE the holes parenthetical on purpose. The number that says the drip worked
                   // is the one printed on a period with no holes left at all.
                   DripNote() + " [" + OwnedTextures.TranscodeNote() + "; " + OwnedTextures.CacheNote() + "; " + OwnedTextures.PrefetchNote() + "; " + EnrollNote() + "]" +
                   $" on {_vdoCount} VDOs ({OwnedVaos()} on Alloy-uploaded pools, drawn by {OwnedClasses()}{UvlessNote()}) " +
                   $"/ {_lastTextures} textures " +
                   $"({_litCount} lit, {_litUnownable} lit left to the engine" +
                   // #128: what the creature class costs, split. "Creatures are still vanilla" has three
                   // different fixes behind it -- a skinning stage, an instance-state term, or nothing at all
                   // if the sweep is simply doing its job -- and only the split says which.
                   (_skinUnownable == 0 && _gobParts == 0 ? "" :
                       $", {_skinUnownable} skinned parts left to the engine, {_gobParts} accepted parts revoked " +
                       $"by the per-instance sweep (out of {AlloyCensus.RefusedGobs} instances with a refused part)" +
                       (AlloyCensus.RefusedGobOverflow ? " -- REFUSED-GOB SET OVERFLOWED, every instance dropped" : "")) +
                   // #137: and what the skinmesh class itself did. A zero everywhere with the class armed
                   // means no cloak was on screen, which is a different statement from "the pose failed" --
                   // so the failures are named, not merely absent.
                   (!_skinning ? ", skinmeshes not owned"
                      : _skinDrawn == 0 && _skinNoPose == 0 ? ""
                      : $", {(float)_skinDrawn / _frames:F1} skinmeshes posed/frame" +
                        (_skinNoPose == 0 ? "" : $", {_skinNoPose} handed back unposed" +
                            (SkinPose.NoBind == 0 ? "" : $" ({SkinPose.NoBind} node not prepared)") +
                            (SkinPose.Overflow == 0 ? "" : $" ({SkinPose.Overflow} bone tree too deep)")) +
                        $", {_boneHigh} bone slots peak of {_boneCap}") +
                   // #284: session totals, so a creature that is STILL a hole three periods later is
                   // visible as one rather than being reset out of the line every period.
                   SkinPose.PrepareNote() +
                   "), " +
                   $"Alloy draws {(float)_draws / _frames:F0}/frame in {(float)_batches / _frames:F0} batches " +
                   // #126: the transparency model, as numbers. Cutout should dwarf blended on NWN content;
                   // a blended count that reads 0 while the scene plainly has transparents means the class
                   // was dropped whole rather than never classified -- which the next term names.
                   $"({(float)_drawsByClass[1] / _frames:F0} cutout, {(float)_drawsByClass[2] / _frames:F0} blended, " +
                   // #129: indexed off the enum, not off literals -- inserting Water at 3 renumbered
                   // Additive, and the line went on labelling water draws "additive" while additive itself
                   // stopped being printed at all. This line is the acceptance instrument; it must not lie.
                   $"{(float)_drawsByClass[(int)AlloyClass.Water] / _frames:F0} water, " +
                   $"{(float)_drawsByClass[(int)AlloyClass.Additive] / _frames:F0} additive" +
                   (_blendedDropped == 0 ? "" : $"; {_blendedDropped} BLENDED PARTS HANDED BACK -- the class " +
                    "could not be Alloy whole, so none of it is") + ") " +
                   $"({(_batches == 0 ? 0f : (float)_draws / _batches):F1} draws/batch), " +
                   // #292: the face-culling term's own cost. A two-sided count of 0 with a live scene means
                   // no shipped material here says `twosided`, which is a finding about the content, not a
                   // broken read; cuts above zero is what the term actually charged in batches.
                   $"({(float)_twoSidedDraws / _frames:F0} two-sided draws/frame, " +
                   $"{(float)_cullCuts / _frames:F1} batch cuts/frame on culling; " +
                   // #327: the opaque cut census by term. A term that dominates here is the next sort digit.
                   $"opaque cuts/frame by term: {CutCensus()}), " +
                   // #243 (AC5): what the GPU cull kept, and what it dropped. Taken off the count buffer
                   // three frames behind the dispatch, so nothing on the submission path waits for it. The
                   // "not running" reading is deliberately distinct from a cull that kept everything: one is
                   // "this instrument has nothing to say", the other is a finding about the area.
                   (CullPass.Mode <= CullPass.ModeOff ? "cull OFF (/alloy cull 2 arms it), "
                      : !CullPass.Ready ? "cull unavailable -- no compute or no indirect-count, "
                      : CullPass.CullFrames == 0 ? "cull armed, no counts read back yet, "
                      : $"cull keeps {(float)CullPass.KeptDraws / CullPass.CullFrames:F0}/frame and drops " +
                        $"{(float)CullPass.CulledDraws / CullPass.CullFrames:F0} " +
                        $"({(CullPass.KeptDraws + CullPass.CulledDraws == 0 ? 0f : 100f * CullPass.CulledDraws / (CullPass.KeptDraws + CullPass.CulledDraws)):F0}% of the colour range, " +
                        $"{(CullPass.Mode >= CullPass.ModeHiZ ? "frustum+hi-Z" : "frustum only")}; bounds span " +
                        $"{CullPass.SpreadX:F0}x{CullPass.SpreadY:F0}x{CullPass.SpreadZ:F0}u, largest radius " +
                        $"{CullPass.SpreadMaxR:F0}u), ") +
                   // #285: every reason on this list is a HOLE -- geometry that nothing renders this frame.
                   $"NOT DRAWN this frame: unlit-pass {(float)_skipNoLight / _frames:F0} identity {(float)_skipIdentity / _frames:F0} " +
                   $"inactive-LOD {(float)_skipLod / _frames:F0} " +
                   $"dissolve {(float)_ownDissolve / _frames:F0} owned, fade {(float)_ownFade / _frames:F0} owned " +
                   $"fog-out {(float)_skipFogTile / _frames:F0} tile + {(float)_skipFogGob / _frames:F0} gob " +
                   $"no-mdl {(float)_skipNoMdl / _frames:F0} tex {(float)_skipTex / _frames:F0}" +
                   (_skipTex == 0 ? " " : $" (no-diffuse {(float)_skipTexNone / _frames:F0}" +
                       $" no-gl-id {(float)_skipTexNoId / _frames:F0}" +
                       $" not-owned {(float)_skipTexNotOwned / _frames:F0}" +
                       $" no-handle {(float)_skipTexNoHandle / _frames:F0}) ") +
                   $"range {(float)_skipRange / _frames:F0} vao {(float)_skipVao / _frames:F0}), " +
                   // The one split that answers "are characters Alloy's" without inference. Drawn ~0 with
                   // owned nonzero means every creature and placeable in view is a HOLE (#285): since #264
                   // there is no engine walk behind the skip, so nothing renders them at all.
                   $"instanced {(float)_drawsGob / _frames:F0} drawn/frame, " +
                   // #244 (AC1): the mesh-sharing census. "1,00x" says every owned part has its own item
                   // range and there is nothing for instancing to collapse; the object-space figure is the
                   // only part of the shared count that could become instances, for the reason MeshCensus
                   // states. Reported from the live area rather than assumed from the VDO count.
                   $"mesh sharing {(_msMeshes == 0 ? 0f : (float)_msParts / _msMeshes):F2}x " +
                   $"({_msMeshes} meshes, max {_msMaxInstances} instances, {_msShared} parts share a mesh " +
                   $"of which {_msSharedMx} object-space" +
                   (_msUnread == 0 ? "" : $"; {_msUnread} PARTS UNREAD -- their index range did not resolve, " +
                    "so they are outside this count") + "), " +
                   // #244: the SHAPE tier, which is the one that matters on placeable-heavy content -- fifty
                   // identical barrels are fifty placements of one model, and if the engine copies its
                   // vertices per placement then every RANGE is distinct while the geometry repeats fifty
                   // times. A signature, not a proof (same texture + same index count collides), so 1,00x is
                   // the conclusive reading and anything above it is a population to confirm, not design on.
                   $"shape repeats {(_msShapes == 0 ? 0f : (float)_msParts / _msShapes):F2}x " +
                   $"({_msShapes} shapes, max {_msShapeMax}, {_msShapeRepeat} parts on a repeated shape " +
                   $"of which {_msShapeRepeatGob} placeable/creature and {_msShapeRepeatMx} object-space; " +
                   $"{_msGobParts} gobs and {_msMxParts} object-space of {_msParts} censused parts" +
                   // #244: a SIGNATURE, never a proof -- same texture + same index count collides. ADR 0017
                   // settled the reading (1,00x at record identity, 2,5x at content level); #258 deleted the
                   // content pass that measured the second, because it could only reach the bytes by mapping
                   // a buffer back. Sharing is residency's question now (#254), keyed on content at load.
                   "), " +
                   $"normal maps {(float)_nrmBound / _frames:F0} bound of " +
                   $"{(float)_nrmDeclared / _frames:F0} declared/frame, " +
                   // #242 (AC3): the whole point of the persistent records, as one number. "written" should
                   // be the MOVER count -- creatures, doors, animated textures, fading tiles -- and not the
                   // owned count. If the two are the same the split has a permanent mover in it and step 2
                   // of ADR 0017 bought nothing, which no frame-time average would ever say out loud.
                   $"records {(float)_recWritten / _frames:F0}/frame written, " +
                   $"{(float)_recStable / _frames:F0} stable ({_recOwnCap} slots), " +
                   StaticNote() + ", " +
                   // #330: how much of the engine chase the view bounded away, and the poll audit.
                   ChaseNote() + ", " +
                   (_watchEnrolled == 0 ? "" :
                       $"dumptex {_watchEnrolled} enrolled, {(float)_watchSubmitted / _frames:F1} submitted/frame, " +
                       "") +
                   // #125: "masked" is the criterion, not "captured" -- it counts the frames the engine's own
                   // sky wrote nothing because the Alloy one had already drawn. Captured but never masked
                   // means the pass is staging nothing, which is a different bug from never seeing the sky.
                   // #175: every registered pass's clause, in declaration order. #139's "post 0 resolves"
                   // while the world renders is the whole reason this is a roll-call and not a fixed list --
                   // the frame looks fine, just 8-bit and un-bloomed, and only the clause says so.
                   PassRegistry.Global.Stats(_frames) + ", " +
                   $"enrolls={_enrolls} revokes={_revokes}; cycle={CycleTextures.Count} steps={CycleTextures.Steps}" + (CycleTextures.Overflow == 0 ? "" : $" overflow={CycleTextures.Overflow}") + (CycleTextures.OtherClass == 0 ? "" : $" otherctl={CycleTextures.OtherClass}") + "; " +
                   // #119: the prepass's own cost, and what it bought. samples/px is the overdraw figure --
                   // run the same scene with /alloy prepass 0 and the difference IS the pass's worth.
                   (!_depthPrepass ? "prepass OFF (/alloy prepass 1 restores it)"
                    : _progDepth == 0 ? "prepass unavailable"
                    : $"prepass {(float)_prepassDraws / _frames:F0} draws/frame" +
                      (_prepassKeyed == 0 ? "" : $" ({(float)_prepassKeyed / _frames:F0} of them dissolving, keyhole stage)") +
                      (_prepassSkipped == 0 ? "" : $" (+{(float)_prepassSkipped / _frames:F0} excluded, displacing/skinned or cutout)") +
                      // #489 (AC2): the GPU's kept figures under the staged ceiling -- phase one plus phase
                      // two, and phase two's own share -- with phase two's submission as its own term.
                      (CullPass.CullFrames == 0 ? "" : $" (cull kept {(float)CullPass.PrepassKept / CullPass.CullFrames:F0}, of which retest {(float)CullPass.RetestKept / CullPass.CullFrames:F0})") +
                      (_qTerm[(int)GpuTerm.Prepass].Measured ? $" in {GpuMs(GpuTerm.Prepass):F3}ms" : "") +
                      (_qTerm[(int)GpuTerm.PrepassRetest].Measured ? $" + retest {GpuMs(GpuTerm.PrepassRetest):F3}ms" : "") +
                      (_qTerm[(int)GpuTerm.PrepassKey].Measured ? $" + keyhole sweep {GpuMs(GpuTerm.PrepassKey):F3}ms" : "") +
                      (_qTerm[(int)GpuTerm.PrepassFeat].Measured ? $" + cutout/displaced sweep {GpuMs(GpuTerm.PrepassFeat):F3}ms ({(float)_prepassFeatDraws / _frames:F0} draws)" : "") +
                      (_qTerm[(int)GpuTerm.Ao].Measured ? $" + ao {GpuMs(GpuTerm.Ao):F3}ms" : "") +
                      (!_prepassKey ? " (keyhole sweep OFF -- /alloy prepasskey 1)" : "")) +
                   (_qSamples.Measured && _lastPixels > 0
                       ? $", shaded {_qSamples.Avg / 1e6:F2}M samples/frame over {_lastPixels / 1e6f:F2}Mpx " +
                         $"(overdraw x{_qSamples.Avg / _lastPixels:F2})"
                       : "") +
                   (_qTerm[(int)GpuTerm.Opaque].Measured
                       ? $", gpu ms: opaque {GpuMs(GpuTerm.Opaque):F2}" +
                         (_qTerm[(int)GpuTerm.OpaqueKey].Measured ? $" (+ keyhole {GpuMs(GpuTerm.OpaqueKey):F2})" : "") +
                         $" / cutout {GpuMs(GpuTerm.Cutout):F2}" +
                         (_qTerm[(int)GpuTerm.CutoutKey].Measured ? $" (+ keyhole {GpuMs(GpuTerm.CutoutKey):F2})" : "") + " / " +
                         $"grass {GpuMs(GpuTerm.Grass):F2} (+ depth {GpuMs(GpuTerm.GrassDepth):F2}) / sky {GpuMs(GpuTerm.Sky):F2} / blended {GpuMs(GpuTerm.Blended):F2} / " +
                         $"water {GpuMs(GpuTerm.Water):F2} / additive {GpuMs(GpuTerm.Additive):F2} (colour {colourMs:F2}) " +
                         $"/ particles {GpuMs(GpuTerm.Particles):F2}" +
                         (_qPartSamples.Measured && _lastPixels > 0 ? $" (overdraw x{_qPartSamples.Avg / _lastPixels:F2})" : "") +
                         (_qTerm[(int)GpuTerm.Motion].Measured ? $" / motion {GpuMs(GpuTerm.Motion):F2}" : "") +
                         (_qTerm[(int)GpuTerm.Exposure].Measured ? $" / exposure {GpuMs(GpuTerm.Exposure):F3}" : "") +
                         $" / post {GpuMs(GpuTerm.Post):F2}" +
                         // The terms no pass owned: the cull dispatches, the hi-Z build, beams, the water
                         // copy -- and the total, to read against the [gl46] gpu-span. Span minus total is
                         // idle-or-unbracketed; it is idle if it shrinks when the frame goes GPU-bound.
                         $" / cull {GpuMs(GpuTerm.Cull):F2} / hiz {GpuMs(GpuTerm.HiZ):F2} / beams {GpuMs(GpuTerm.Beams):F2}" +
                         $" / water copy {GpuMs(GpuTerm.WaterCopy):F2}" +
                         (_qTerm[(int)GpuTerm.Weather].Measured ? $" / weather {GpuMs(GpuTerm.Weather):F3}" : "") +
                         $" (gpu terms total {GpuTermsTotal() + CasterPass.ShadowGpuMs:F2} incl. shadows {CasterPass.ShadowGpuMs:F2}; colour bracket span {GpuSpanMs:F2})" : "") +
                   // #121: captures per SESSION against frames per PERIOD. A number that tracks the frame
                   // count is the acceptance criterion ("probe capture does not run per frame") failing.
                   $", probe {(EnvProbe.Ready ? $"ready ({EnvProbe.Captures} captures)" : "none yet")}" +
                   // Which parts the probe reaches: with no area env map it is the env-mapped materials
                   // alone, which is what vanilla binds a texture for.
                   $" {(_sceneEnv && EnvProbe.AreaWide ? "area-wide" : "env-mapped parts only")}" +
                   (_sceneEnv && !EnvProbe.AreaWide ? " (area declares one; /light probeareawide 1 honours it)" : "") + "; " +
                   AlloyLights.Diag(_clusterOverflow) +
                   // The coverage question -- "what is the engine still drawing, and why?" -- was only
                   // answerable from the one-shot enroll diag, which a healthy session prints once at startup
                   // and never again. It belongs where the numbers it explains are.
                   $"; {AlloyCensus.OwnScanDiag()}" +
                   // Nothing lit means nothing samples the shadow maps -- the whole Alloy lit stage, cascades
                   // and cubes included, is dead code for the session. Loud, because it looks like working
                   // output: parts are owned, cubes render, and the frame is merely fullbright.
                   (_litCount == 0 && _lastEnrolled > 0
                       ? "; NOTHING LIT -- no Alloy draw samples the shadow maps"
                       : "") +
                   // Dangling texture pointers AurTex refused. A handful around an area transition is the
                   // engine freeing textures a material still names; a number that climbs every period means
                   // something upstream is holding a dead MATERIAL, which is a lifetime bug rather than a
                   // texture one -- and this line is the only place it would ever be visible.
                   (Framework.AurTex.Refused == 0 ? ""
                       : $"; {Framework.AurTex.Refused} dangling texture pointers refused (session total)"));
        CullPass.ResetStats();
        _frames = 0; _draws = 0; _batches = 0; _revokes = 0; _enrolls = 0;
        _twoSidedDraws = 0; _cullCuts = 0; Array.Clear(_cutBy);
        _drawsGob = 0;
        _skipTexNone = 0; _skipTexNoId = 0; _skipTexNoHandle = 0; _skipTexNotOwned = 0;
        _skinDrawn = 0; _skinNoPose = 0; _boneHigh = 0;
        SkinPose.NoBind = 0; SkinPose.Overflow = 0;
        SkyBox.ResetPeriod();
        GrassField.ResetPeriod();
        Beams.ResetPeriod();
        Halos.ResetPeriod();
        Weather.ResetPeriod();
        Particles.ResetPeriod();
        _watchSubmitted = 0;
        _skipNoLight = _skipIdentity = _skipNoMdl = _skipTex = _skipRange = _skipVao = 0; _skipLod = 0;
        ResetChasePeriod();
        _ownDissolve = 0; _ownFade = 0; _skipFogTile = _skipFogGob = 0;
        _recWritten = 0; _recStable = 0;
        ResetStaticPeriod();
        _nrmDeclared = _nrmBound = 0;
        Array.Clear(_drawsByClass);
        _clusterOverflow = 0;
        _prepassDraws = 0; _prepassSkipped = 0; _prepassKeyed = 0; _prepassFeatDraws = 0; _qSamples.Reset(); _qPartSamples.Reset();
        for (int i = 0; i < _qTerm.Length; i++) _qTerm[i].Reset(); _spanSum = 0; _spanN = 0;
    }

    /// <summary>The unmatched total, split by the bucket the parts were captured in -- only the buckets that
    /// actually contributed, so the line stays short when one class dominates.</summary>

    /// <summary>A per-bucket counter as "bucket:per-frame", only the buckets that contributed.</summary>
    internal static string ByBucket(long[] counts)
    {
        string s = "";
        for (int b = 0; b < counts.Length; b++)
            if (counts[b] != 0) s += $" {b}:{(float)counts[b] / _frames:F0}";
        return s.Length == 0 ? " none" : s;
    }

    private static uint _progWater;
    private static bool _waterFailed, _waterArmed;
    /// <summary>The engine's water draw bucket (<c>Scene::RenderSinglePass</c> @0x46e5f0's "Water" scope).
    /// One name, read by the ownable-bucket list and by the suppression that has to agree with it.</summary>
    internal const int WaterBucket = 6;
    private static float* _waterView, _waterProj;
    private static float _waterNear = 1f, _waterFar = 100f;
    private static int _watVp, _watBase, _watTex, _watCut, _watAmbient, _watDiffuse, _watDir,
                       _watGrid, _watTile, _watZ, _watFalloff, _watWrap, _watAtten, _watHot,
                       _watWrapN, _watSss, _watReach, _watViz, _watSunTex, _watSunVp, _watSunTexel, _watSunBias,
                       _watSunCount, _watCubeTex, _watCubeLight, _watCubePlane, _watCubeCount, _watCubeSoft, _watCubePcss,
                       _watCubeDynTex, _watCubeDynPage, _watCubeFade, _watCubeFloor, _watSunDynTex, _watSunDyn,
                       _watSunFocus, _watSunRadius,
                       _watCapture, _watSplash, _watFbColor, _watFbDepth, _watNoise, _watEnvMap, _watEnvOk, _watNearFar,
                       _watView, _watProj;
    private static FeatLoc _watFeat;

    /// <summary>#341 adds the eye and the four fog-target terms: the fog colour is direction-dependent
    /// now, so every stage that fogs uploads the same five values through <see cref="UploadFog"/>.</summary>
    internal unsafe struct FeatLoc { internal int Time, Wind, Flags, Cam, CamFocus, Player, Fog, FogColor, KhDia, KhCam,
                                       Pixel, Eye, FogSky, FogSkyColor, FogSun, FogSunColor, FogOrigin, LodBias,
                                       TgtShapeL, TgtSelfL, TgtObjL, TgtSizeXL, TgtSizeYL, TgtRangeL,
                                       TgtColL, TgtWorldL, ProjL;
        /// <summary>#551: the WHOLE scriptable block, by location. Picking entries one feature at a time
        /// was a standing bug: a content shader that starts reading a new slot compiles, links, and reads
        /// zero, which is a feature that silently does nothing under Alloy and works under the engine.
        /// sow-topdata#551 is exactly that case (Float13, the sky-light veil fade) and it is why this is a
        /// block. Filled at program build; a slot the stage does not use is -1, which GL ignores.</summary>
        internal fixed int SInt[ScriptableSlots];
        internal fixed int SFloat[ScriptableSlots];
        internal fixed int SVec[ScriptableSlots];
    }
    private static FeatLoc _locFeat;
    /// <summary>#123: <c>GLRender::m_shaderExposedRuntimeData</c>, the struct the engine's own SetUniforms
    /// uploads these five from. Null (symbol unresolved) leaves the wind still and the roofs wherever they
    /// were authored, which is the pre-#123 look, not a misrender.</summary>
    private static byte* _runtime;

    internal static readonly int RtTimeOfDay = EngineLayout.RtTimeOfDay, RtAreaWind = EngineLayout.RtAreaWind,
        RtAreaFlags = EngineLayout.RtAreaFlags, RtPlayerPos = EngineLayout.RtPlayerPos,
        RtCamPos = EngineLayout.RtCamPos;

    /// <summary>#347: the camera's PIVOT, as the engine's own stages read it under the name
    /// <c>cameraFocus</c> -- <c>GLRender::SetUniforms</c> @0x4cf359 uploads
    /// <c>m_shaderExposedRuntimeData + 0x90</c> to the location <c>ConnectShaderData</c> resolved for that
    /// name. Not the camera POSITION (<see cref="RtCamPos"/>, +0x64): in chase cam the pivot sits on the
    /// controlled character at roughly chest height, which is why content measures a sight range against it
    /// and not against the eye.</summary>
    internal static readonly int RtCamFocus = EngineLayout.RtCamFocus;
    /// <summary>#124's two more fields of the same struct, from the same pairing: <c>cameraPitch</c> at
    /// +0x84 and <c>playerCameraDist</c> at +0x8c. Both feed the keyhole aperture.</summary>
    internal static readonly int RtCamPitch = EngineLayout.RtCamPitch, RtCamDist = EngineLayout.RtCamDist;
    /// <summary>#124: the keyhole diameter pair inside
    /// <see cref="SymbolMap.GlGlobalShaderConstantsF"/>. Stored unordered -- SetUniforms uploads min as
    /// <c>keyholeDiameterMin</c> and max as <c>keyholeDiameterMax</c>, so the ordering happens here too.</summary>
    internal const int KeyholeDiaA = 21, KeyholeDiaB = 22;
    private static float* _shaderConstF;

    /// <summary>#346: the spell-targeting shape's own slice of the same three tables, indices read off the
    /// <c>SetUniforms</c>/<c>ConnectShaderData</c> pairing and confirmed against the setters
    /// (<c>SpellTargeting::UpdateSpellTargetingStaticData</c> @0x958790 writes params 0x1b/0x20/0x28/0x29/
    /// 0x2a/0x10, and <c>CClientExoAppInternal::MainLoop</c> @0x658c13 the object type and world point).
    /// Only what <c>inc_target.shd</c> actually reads is listed -- the harms/helps/ignores flags and the
    /// spell and feat ids reach the shader as uniforms nothing in it samples.</summary>
    internal const int TgtShape = 27, TgtOnSelf = 32, TgtObjType = 35,
                       TgtSizeX = 40, TgtSizeY = 41, TgtRange = 42, TgtColour = 16;
    /// <summary>#347: the rest of the scriptable block the spliced stock includes read, by the same
    /// pairing -- <c>scriptableInt(n)</c> is <c>m_globalShaderConstantsi[10 + n]</c> and
    /// <c>scriptableFloat(n)</c> is <c>m_globalShaderConstantsf[23 + n]</c>, from
    /// <c>ConnectShaderData</c>'s n = 1..16 loop against <c>SetUniforms</c>' slot bases (+0x2c and +0x60).
    /// 4 gates and scales the noir desaturation, 11 and 12 the racial-vision mode and its range.</summary>
    /// <summary>#551: the scriptable block is 16 of each, and the engine's own tables hold them at these
    /// bases -- <c>scriptableInt(n)</c> is <c>m_globalShaderConstantsi[10 + n]</c>, <c>scriptableFloat(n)</c>
    /// is <c>m_globalShaderConstantsf[23 + n]</c> and <c>scriptableVec(n)</c> is
    /// <c>m_globalShaderConstantsv4[n - 1]</c>, from <c>ConnectShaderData</c>'s n = 1..16 loop paired with
    /// <c>SetUniforms</c>' slot bases (+0x2c, +0x60, +0x00). NWScript's own count is 48 uniforms, which is
    /// these three sixteens.</summary>
    internal const int ScriptableSlots = 16, SIntBase = 11, SFloatBase = 24, SVecBase = 0;

    /// <summary>#347: this pass's PROJECTION, latched where the frame reads it off the engine's matrix
    /// stack, for the spliced stock files that ask for <c>m_proj</c>. Per pass, not per frame: the probe
    /// cube renders under its own, and a stale one would tell a stock file it was drawing 2D UI.</summary>
    private static Matrix4x4 _projMtx = Matrix4x4.Identity;
    private static int* _shaderConstI;
    private static float* _shaderConstV, _shaderConstV4;

    /// <summary>Stand-in area ambient for the frames before the engine's first index -1 light bind. Grey, not
    /// black: a missing capture must not paint the world dark.</summary>
    internal const float DefaultAmbient = 0.25f;

    /// <summary>Texture units the shadow maps bind on. Unit 0 is the part's diffuse and gets rebound per
    /// batch, so the maps live above it and are bound once per pass.</summary>
    internal const uint SunUnit = TextureSlots.SunShadow, CubeUnit = TextureSlots.LocalShadow,
                        CubeDynUnit = TextureSlots.LocalShadowDyn,
                        SunDynUnit = TextureSlots.SunShadowDyn, SunDynUnitGrass = TextureSlots.SunShadowDynGrass;
    /// <summary>Shadow depth bias in WORLD units, sent per cascade as a fraction of that cascade's published
    /// depth range so it stays a constant world distance whatever the fit. Small because the receiver's normal
    /// offset carries most of the acne fight; <c>/shadow csmbias</c> tunes it.</summary>
    private static float _sunWorldBias => Shadows.CsmBias.V;

}
