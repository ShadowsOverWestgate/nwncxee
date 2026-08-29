using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Alloy.SceneLayout;

namespace Sowx.Alloy;

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
/// every lit part and the Alloy pass used to drop on all of them. A lit part we cannot shade faithfully (no normal stream, lit program down) is handed back to the
/// engine rather than painted flat. The engine's bucket walk then skips
/// exactly those parts at the GetNextItem iterator, and only on passes where the Alloy draw actually
/// happened -- never dropped, never double-drawn (audited live as the UNMATCHED counter).
/// <para>#84: submission is MULTIDRAW-BATCHED, so draw count follows the (program, VDO, texture, primitive)
/// group count rather than the part count. Everything that used to be a per-part uniform -- model matrix,
/// texture rotation, material ambient/diffuse -- moved into a per-draw SSBO the shaders index by
/// <c>uDrawBase + gl_DrawID</c>, which is what lets one <c>glMultiDrawElementsBaseVertex</c> cover a whole
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
/// <para>Ownership lives as a column on the shared part table (<see cref="AlloyCensus.OwnFlag"/>)
/// so the iterator's existing <c>Find</c> serves both. The table is rebuilt wholesale at a frame boundary
/// (enrollment, every <see cref="EnrollEvery"/> frames, before the Alloy block) -- that is the frame-atomic
/// swap. The only mid-frame mutation is revocation, which is safe in both directions because suppression is
/// gated on the per-pass draw generation. <c>/vanilla</c> is the kill switch. Hot path allocates nothing.</para>
/// <para>#128: CREATURES join it, and they arrive by relaxing two things rather than by a new path. The
/// engine draws them from <c>Scene::RenderDynamicGeometry</c> -- buckets 2 and 3, but through the same
/// <c>RenderDrawBucket</c> the capture already brackets -- so what actually kept them out was BatchService's
/// matrix PROBATION, which killed any part whose transform moved between two sightings; an animating creature
/// fails that by construction and a door fails it the frame it opens. Instances skip probation now and are
/// recorded as MOVERS, whose transform this pass reads live at draw time anyway. The second relaxation is the
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
    internal const int LogEvery = 900, EnrollEvery = 60;
    /// <summary>#236: how long an off-screen part keeps its ownership. NOT the draw window -- BuildBatches
    /// refuses to draw anything the engine stopped walking, so an owned invisible part costs a compare and a
    /// continue. This is only how long the ENROLLMENT scan will hold a part the camera has turned away from,
    /// so that turning back is instant rather than a wait for the next scan. Measured live at 60: ~5000 of
    /// 12533 active parts were losing ownership per enrollment to the old one-frame window.</summary>
    internal const int OwnSightingWindow = 60;
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
    /// <summary>Floats per SowxDraw record: std430 <c>mat4 + vec4 rot + vec4 amb + vec4 dif + vec4 emi
    /// + vec4 pbr + vec4 spc + vec4 feat</c> = 176 bytes, whose alignment (16) already divides the size, so
    /// the array stride is 176 with no padding.</summary>
    internal const int DrawFloats = 44;
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
    /// <summary>Bones per skinned part, fixed by the engine: <c>GLRender::m_bones</c> is a 64 x mat4 static
    /// array and the vertex stream's indices address it directly. The matrices already sit in GLSL's
    /// column-major order (see <see cref="SymbolMap.GlRenderBones"/>), so a snapshot is a straight memcpy.
    /// </summary>
    internal const int BoneCount = 64, BoneFloats = BoneCount * 16;
    /// <summary>Skinned parts posed per frame. 4KB each, so 64 slots is 256KB and covers a crowd; past it a
    /// skinmesh is handed back to the engine rather than drawn in a bind pose.</summary>
    internal const int MaxBoneSlots = 512;
    /// <summary>aurMatrixStack layout (Ghidra GLRender::PushMatrix/PopMatrix/aurMatrixStack::SetIdentity):
    /// stride 0x58 per stack, top matrix (row-major float[16]) at +0x18. Modes: PROJECTION=0, VIEW=1.</summary>
    internal const int StackStride = 0x58, StackTop = 0x18, ModeProjection = 0, ModeView = 1;
    /// <summary>The part's MdlNode (part+0x8) and its material colours, from Ghidra
    /// <c>PartTriMesh::MatStatic</c> @0x450560, which calls <c>SetMaterialv(prop 1, node+0xf4)</c>,
    /// <c>SetMaterialv(prop 2, node+0xe8)</c> and <c>SetMaterialv(prop 3, part+0x7c)</c>. Prop 3 takes the
    /// self-illumination colour, so the enum runs ambient/diffuse/emissive -- and node+0xe8 is the one
    /// carrying the part's alpha (part+0xa0), which in fixed-function material setup is the DIFFUSE term.
    /// Distinct from the bucket node at part+0x88.</summary>
    internal const int PartMdlNode = 0x8, MdlDiffuse = 0xe8, MdlAmbient = 0xf4;
    /// <summary>Self-illumination colour (3 floats) at part+0x7c -- MatStatic's prop-3 material term. It is
    /// applied whenever <c>part+0x94 == 0</c>, and LightPartTriMesh (@0x47a6a0) clears that byte on every
    /// part under <c>g_bFullDynamicLighting</c>, so under the modern renderer it applies ALWAYS.</summary>
    internal const int PartSelfIllum = 0x7c;
    /// <summary>#120: the material's parsed .mtr parameter list, <c>List&lt;MaterialParameter&gt;</c> at
    /// mat+0x118 -- Ghidra <c>Material::CheckAndBindCustomShader</c> @0x4c62f0 passes exactly
    /// <c>(this + 0x118)</c> to <c>SharedMaterial::BindShaderParameters</c>, which reads the element pointer
    /// at +0 and the count at +8. So the count sits at mat+0x120.</summary>
    internal const int MatParams = 0x118, MatParamCount = 0x120;
    /// <summary>MaterialParameter layout, from <c>Material::SetMaterialParam</c> @0x4c5d70 (the name is
    /// <c>strncasecmp</c>'d over 0x40 bytes from the record's start) and BindShaderParameters (value floats at
    /// +0x44, the two cached uniform locations at +0x54/+0x58, the type discriminator at +0x5c, the component
    /// count as a ushort at +0x60). The record is 100 bytes -- both walks step by exactly that.
    /// <para>The type discriminator selects how +0x44 is READ, not where the value lives: 0 takes it as a
    /// <c>float*</c> with <paramref name="ParamComps"/> components, 1 as a single <c>int</c>, both off the
    /// same address.</para></summary>
    internal const int ParamValue = 0x44, ParamType = 0x5c, ParamComps = 0x60, ParamStride = 100;
    /// <summary>A parameter list longer than this is not a .mtr, it is a bad pointer -- refuse it rather than
    /// walk off into the heap. Real materials declare a handful.</summary>
    internal const int MaxParams = 64;
    /// <summary>The SharedMaterial's own parameter list -- the one the .mtr's <c>parameter float &lt;Name&gt;
    /// &lt;value&gt;</c> lines are parsed onto. Ghidra <c>Material::SetMaterialParam</c> @0x4c5d70 walks
    /// <c>*(shared+0x1d0)</c> for <c>*(int*)(shared+0x1d8)</c> records of the same 100-byte layout.</summary>
    internal const int SharedParams = 0x1d0, SharedParamCount = 0x1d8;
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
    /// <summary>#146: texture slot 7, the ambient-occlusion map -- a baked visibility term in .r, sampled
    /// straight onto the ambient. Not stock vocabulary either (the stock set stops at texture5 and #123 took
    /// texture6), so this is the extender's convention, named the same way the flowmap's texture6 is; the
    /// engine's own FB-effect SSAO is a screen-space pass that keeps running underneath it, untouched.
    /// <para>Unlike the flow map this is probed on EVERY shared material -- there is no shader feature that
    /// announces it. That costs one <c>Material::GetTexture</c> per enrolled part, and a batch split only
    /// where a material actually declares one.</para></summary>
    internal const int AoSlot = 6, MapSlots = TextureSlots.MapCount;
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
    internal const int SceneEnvMap = 0x1e8;
    internal const uint GlUnsignedShort = 0x1403;
    /// <summary>The GL blend factors <see cref="BlendGl"/> maps the Aurora ones onto.</summary>
    internal const uint GlZero = 0, GlOne = 1, GlSrcColor = 0x0300, GlSrcAlpha = 0x0302,
                       GlOneMinusSrcAlpha = 0x0303, GlDstColor = 0x0306, GlOneMinusDstAlpha = 0x0305;
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
    private static uint _prog46, _progLit46, _progDepth, _progDepthKey, _fbo46;
    private static int _depthVp = -1, _depthBase = -1;
    /// <summary>#124: the dissolving batches' depth program (<see cref="DepthKeyVs"/>). Zero = it could not
    /// build, and those batches fall back to being excluded from the prepass -- the pre-fix behaviour, which
    /// is slower but never wrong.</summary>
    private static int _depthKeyVp = -1, _depthKeyBase = -1;
    private static FeatLoc _depthKeyFeat;
    /// <summary>The depth prepass could not build (or glColorMask is missing) -- latched, and the colour pass
    /// simply shades every fragment as it did before #119.</summary>
    private static bool _depthFailed;
    /// <summary><c>/draw prepass 0</c>: skip the prepass, which is the A/B for its own acceptance number.</summary>
    private static bool _depthPrepass = DefPrepass;
    /// <summary>Every Alloy vertex stage that ran carries <see cref="ClipExpr"/> under an invariant
    /// qualifier. False = a #92 override rewrote one, and the prepass cannot re-test its depth.</summary>
    private static bool _vsDepthSafe = true;
    private static int _locVp = -1, _locBase = -1, _locTex = -1;
    /// <summary>#126: <c>uAlphaCut</c> on each colour program -- the engine's <c>fAlphaDiscardValue</c>, set
    /// per batch from the batch's class.</summary>
    private static int _locCut = -1, _litCut = -1;
    private static int _litVp = -1, _litBase = -1, _litTex = -1,
                       _litAmbient = -1, _litDiffuse = -1, _litDir = -1,
                       _litGrid = -1, _litTile = -1, _litZ = -1, _litFalloff = -1, _litViz = -1,
                       _litSunTex = -1, _litSunVp = -1, _litSunTexel = -1, _litSunBias = -1, _litSunCount = -1,
                       _litEye = -1,
                       // #139: the sphere-light wrap. Its own uniform rather than a spare uFalloff lane, so
                       // a content override (#92) that predates it stays at -1 here and simply does not get
                       // it.
                       _litWrap = -1, _litAtten = -1, _litHot = -1, _litWrapN = -1;
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
    /// <summary><c>/draw viz N</c>. 1..3 and 6 attribute a dim (or a too-shiny) frame to a lighting TERM instead
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
    /// mode is silently discarded at the parse.</summary>
    internal const int LightVizMax = 23;
    private static int _lightViz = DefViz;
    /// <summary><c>/draw aotest 1</c>: bind every lit part's own DIFFUSE texture on the AO slot and set the bit, so
    /// the whole #146 path -- mask, unit, sampler, ambient multiply -- runs end to end on stock content. The
    /// frame looks WRONG on purpose (the diffuse's red channel is not an occlusion map); what it proves is
    /// that a material which does declare one will be sampled. No shipped content uses texture7 yet, so
    /// without this there is nothing to point the feature at.</summary>
    private static bool _aoTest = DefAoTest;
    private static int _locViz = -1;
    /// <summary>#222: the fullbright stage's colour-space switch. Set on every bind of that program -- a
    /// GLSL uniform defaults to 0, i.e. display space, which is right for the sky alone.</summary>
    private static int _locLinear = -1;
    /// <summary>#222: the fullbright stage's constant colour. White for every unlit draw but a fog-tinted
    /// sky part -- a GLSL uniform defaults to ZERO, so an unset one draws BLACK and every caller sets it.</summary>
    private static int _locTint = -1;
    /// <summary>#121: this draw is going into the <see cref="EnvProbe"/> cube, not onto the screen. The lit
    /// and water stages read it, so a capture never samples the probe -- which is currently an attachment of
    /// the framebuffer being drawn into. Since #132 it says nothing about COLOUR SPACE any more: the screen
    /// target is linear too, so the capture and the frame want the same values and the fullbright stage
    /// stopped needing the flag at all.</summary>
    private static bool _capturing;
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
    private static bool _mirrorBad;
    /// <summary><c>/draw light 0</c> forces every Alloy part back to the #82 fullbright variant (the lighting A/B).</summary>
    private static bool _lighting = DefLighting;
    /// <summary>RenderInterface::IsEnabled -- queried for cap 5 (LIGHTING) once per Alloy pass.</summary>
    private static delegate* unmanaged<int, byte> _isEnabled;
    /// <summary>The AuroraEnableEnums value for lighting. <c>MatStatic</c>/<c>RenderFlat</c> both branch on
    /// it, so a pass that runs with it off wants the flat variant for everything.</summary>
    internal const int CapLighting = 5;

    // lifecycle
    private static int _sinceEnroll = EnrollEvery, _tableGen = -1;
    private static bool _wasInModule;

    // period counters (the Alloy draw log counter, counterpart to M0's Alloy cast)
    private static long _draws, _skips, _fallbacks, _unmatched;
    /// <summary>Why an Alloy part still did not draw this pass, by term. The single fallback count could not
    /// separate a camera-dependent bail-out (keyhole, tile fade) from a content one, which is exactly the
    /// distinction a part that is Alloy head-on and vanilla at the screen edge turns on.</summary>
    private static long _skipUnseen;
    private static long _skipNoLight, _skipIdentity, _skipNoMdl, _skipTex,
                        _skipRange, _skipVao;
    /// <summary>Parts whose material declared a normal map, and the subset whose map survived the per-slot
    /// re-fetch into the mask the lit stage samples on.</summary>
    private static long _nrmDeclared, _nrmBound;
    /// <summary>#128: Alloy parts handed back this pass because their INSTANCE state went unownable -- a
    /// highlight, a scene tint, a per-gob env override. Transient by nature, so it is a skip rather than a
    /// revocation, and a skip that is not counted reads as geometry vanishing.</summary>
    private static long _skipGob;
    /// <summary>#124's coverage number: Alloy parts the camera dissolves this pass. Before the fold these
    /// were the keyhole SKIP count -- same population, handed back to the engine; now they are drawn by the
    /// Alloy program with the dissolve folded in, so the before/after the ticket asks for is this one figure
    /// read off the period line either side of the change.</summary>
    private static long _ownDissolve;
    /// <summary>...and WHERE the unmatched came from, by captured bucket and by class. A part drawn and
    /// never sighted is either a suppression gap (a bucket we draw into but do not suppress) or geometry the
    /// engine culled this frame and we drew anyway; the two need different fixes and read identically in a
    /// single total.</summary>
    private static long _unmatchedUnseen;
    private static readonly long[] _unmatchedBucket = new long[16];
    /// <summary>#134 diagnostic: the engine-culled skip, split by the bucket the part was captured in. "Owned
    /// but never walked" is the one skip that cannot be read off the totals -- it means the part is neither
    /// drawn by us nor suppressed, so whichever bucket dominates here names the walk we are missing.</summary>
    private static readonly long[] _unseenBucket = new long[16];
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
    private static readonly long[] _unmatchedCls = new long[AlloyClasses.Count];
    /// <summary>#126's counterpart to it: Alloy parts the tile fade moves into the blended run this pass.
    /// Before the fold these were the tilefade SKIP count -- same population, handed back to the engine.</summary>
    private static long _ownFade;
    /// <summary>#126: Alloy draws per class this pass, and the batches each class's pass submitted. The
    /// cutout/blended split is the whole model, so it is reported rather than inferred.</summary>
    private static readonly long[] _drawsByClass = new long[AlloyClasses.Count];
    /// <summary>#84's measurable form of "draw count decoupled from part count": submitted multidraw batches.
    /// draws/batches IS the batching ratio the acceptance criterion asks for, straight out of the log.</summary>
    private static long _batches;
    private static int _frames, _revokes, _enrolls, _lastEnrolled, _lastCandidates, _enrollDiagLeft = EnrollDiagBudget;
    /// <summary>Passes an empty enrollment may retry back-to-back after a resume before falling back to the
    /// normal cadence (~2s at 60fps): enough for the re-record to complete, bounded so a genuinely unownable
    /// area cannot spin the scan every pass forever.</summary>
    internal const int ResumeRetries = 120, EnrollDiagBudget = 8;
    private static int _resumeRetries;
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
    /// <summary>Monotonic presented-frame counter: the light SSBO uploads once per frame, not once per pass.</summary>
    private static int _frameSeq;
    private static bool _takeOverflow, _ambientFallbackLogged, _loggedOnce;
    private static uint _clusterOverflow;
    /// <summary>#119 measurement: prepass GPU time, and the fragments the COLOUR pass actually shaded.
    /// samples/screen-pixel is the overdraw figure, and its move with <c>/draw prepass</c> is what the prepass
    /// is worth -- both are period averages on the [alloy] line.</summary>
    /// <summary>...and the shading pass's own GPU time. Frame time cannot answer "is the prepass worth it"
    /// on a vsync-capped display -- both modes sit on the cap and the saving becomes headroom -- so the A/B
    /// reads the work itself. Independent of SAMPLES_PASSED: different query target, both may be active.</summary>
    private static GpuQuery _qPrepass, _qSamples, _qColour;
    /// <summary>#151: the passes the colour timer never covered -- particles, water (copy + draw), sky+grass,
    /// and the post chain (bloom + composite). Their sum with colour/prepass/shadow is the whole Alloy GPU
    /// frame; until these existed the variance the user chased lived in the unmeasured slice.</summary>
    private static GpuQuery _qParticles, _qWater, _qSkyGrass, _qPost;

    /// <summary>One-shot FBO-topology report (#151, the "does AA even apply to us" question).</summary>
    private static int _fboDiagSig = -1, _fboDiagLeft = 8;

    /// <summary>#153: resident bindless handle for a texture name. 0 in, 0 out. Called only inside the 4.6
    /// bracket (handles belong to the share group), and -- while ownedtex is armed -- only ever for an
    /// Alloy-OWNED name; see <see cref="_hTid"/> for why that is the whole of #220's fix.</summary>
    private static ulong HandleFor(int tid)
    {
        if (tid == 0) return 0;
        if (Probe(tid, out int hit, out int free)) return _hVal[hit];
        if (free < 0) return 0;                        // table full: no handle, caller falls back
        ulong v = Gl.GetTextureHandleARB((uint)tid);
        if (_handleDiagLeft > 0) HandleDiag(tid, v);
        // A failed mint is NOT cached. It used to be, and the enrollment wipe retried it within a second;
        // with that wipe gone (#220) a cached zero would be a permanently black texture.
        if (v == 0) return 0;
        Gl.MakeTextureHandleResidentARB(v);
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
    internal static void DropHandle(uint name)
    {
        if (!_bindless || name == 0) return;
        int tid = (int)name, h = Slot(tid);
        for (int probes = 0; probes < (1 << HandleHashBits); probes++)
        {
            int k = _hTid[h];
            if (k == 0) return;                       // never cached: nothing to drop
            if (k == tid)
            {
                if (_hRes[h] && _hVal[h] != 0 && Gl.MakeTextureHandleNonResidentARB != null)
                    Gl.MakeTextureHandleNonResidentARB(_hVal[h]);
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
    {
        if (Gl.GetTextureHandleARB == null || Gl.MakeTextureHandleResidentARB == null || Gl.GetStringi == null)
            return false;
        int n = 0; Gl.GetIntegerv(Gl.NUM_EXTENSIONS, &n);
        for (uint i = 0; i < (uint)n; i++)
        {
            byte* e = Gl.GetStringi(Gl.EXTENSIONS, i);
            if (e == null) continue;
            // compare against "GL_ARB_bindless_texture" without allocating
            int k = 0;
            for (; k < BindlessExt.Length && e[k] != 0; k++) if (e[k] != BindlessExt[k]) break;
            if (k == BindlessExt.Length && e[k] == 0) return true;
        }
        return false;
    }
    private static ReadOnlySpan<byte> BindlessExt => "GL_ARB_bindless_texture"u8;

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
            "#define SOWX_BINDLESS 1\n#extension GL_ARB_bindless_texture : require\n" +
            $"layout(std430, binding = {BindTexH}) readonly buffer SowxTexH {{ uvec2 uTexH[]; }};\n");
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

    /// <summary>The colour timer spans three passes now (opaque, blended, additive), so its End lives at the
    /// last of them rather than inside the one that began it.</summary>
    private static bool _colourTiming, _waterTiming;
    private static long _prepassDraws;
    /// <summary>#124: prepass draws that went through the keyhole depth stage. The number that says the fix
    /// is doing its job -- before it, every one of these was in <see cref="_prepassSkipped"/> instead.</summary>
    private static long _prepassKeyed;
    /// <summary>#123: draws the prepass had to leave out because their feature moves vertices, plus (#124)
    /// the ones that dissolve, which discard. Reported, not
    /// hidden -- it is the overdraw the prepass no longer removes, and the number that says whether the wind
    /// content is big enough for a displacing prepass stage to be worth building.</summary>
    private static long _prepassSkipped;
    private static int _lastPixels;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // #165: ahead of every stand-down and every check below -- /vanilla is the escape hatch for a
        // misrendering session, so it must exist even in a session whose renderer never armed.
        RenderCommands.Register(log);
        // #170: both ahead of every stand-down, matching the host's unconditional calls. The latch must
        // outlive a renderer that never armed -- /vanilla is the escape hatch it drives.
        RenderMode.Arm(log);
        // #216: the frame verdict, one priority behind the override latch it reads.
        AlloyGate.Arm(log);
        FrameRegistry.Global.Register(FramePhase.PostPresent, 20, EndFrame, "AlloyRenderer.endFrame", log);
        // #175: the whole pass roll-call, in one walk over one declared table. Attach is ahead of every check
        // because #129's asserts that the ownable-bucket list AGREES with whether Alloy water can draw -- and
        // /water on 0 is exactly the case where it must, so water's knobs have to be read first.
        RenderPasses.Register(log);
        PassRegistry.SelfCheck(log);
        PassRegistry.Global.Attach(log);
        // #211: the retired-texture deletes need a context, not a draw. Hung on the health bracket so they
        // still happen while Alloy is stood down (/vanilla, main menu, the disarm latch).
        // #212: and the geometry deletes with them, for exactly the same reason -- the colour bracket's own
        // Drain never runs in those states, so a revoke there would hold the whole owned set for the session.
        GlContextService.InBracket = static () => { OwnedTextures.Drain(); OwnedBuffers.Drain(); };
        // #117: ahead of every stand-down -- a broken pass order is a misrender in BOTH bracket sites, so the
        // one service that happens to host the check must not be able to opt the check out.
        if (!FrameGraph.SelfCheck(log)) return;
        // #126: log-only. A wrong classification is a wrong-looking frame, not a wrong-drawing one -- the
        // one-owner invariant is upheld by the bucket list, which the pass checks assert against.
        AlloyClasses.SelfCheck(log);
        // #215: here rather than at the shim's own install, which a client without the engine symbols never
        // reaches -- the two things it checks are ours and are wrong in the same way on every client.
        RspShim.SelfCheck(log);
        ShaderStages.SelfCheck(log);
        // ADR 0012: the dump is how a customizer gets a current starting point. Ahead of every stand-down
        // below, so a session that owns nothing can still produce one.
        ShaderStages.DumpIfConfigured(log);
        // Every pass's own check. Advisory by convention -- the checks that GATE arming are the contract ones
        // below, which the passes do not own.
        PassRegistry.Global.RunSelfChecks(log);
        // #179: SOWX_OWNDRAW is gone rather than moved -- /vanilla was already the same switch, only better
        // (a frame edge, no relaunch, and it stands the shadow and batching sides down too). The service now
        // arms unconditionally and idles behind RenderMode.Alloy, which every Alloy render site already reads.
        if (GameGate.Locked)
        { log.Info("AlloyRenderer: stood down (lockchat)"); return; }
        // never arm on a broken contract: the misrender either predicts must not ship
        if (!SortSelfCheck(log) || !ShaderContract.SelfCheck(log)) return;
        // NOT in the chain above: those two guard what reaches the screen, and this one guards a debug dump.
        // A regression in the canonicaliser should cost /framedump, not drop every user back to vanilla.
        FrameDump.SelfCheck(log);
        // #128: same rule. A wrong gob offset draws a creature at the wrong alpha and a leaky refused-gob set
        // half-owns one -- both misrenders, neither of which errors.
        if (!AlloyCensus.GobSelfCheck(log)) return;
        _getTexture = (delegate* unmanaged<IntPtr, int, long>)Native.Sym(SymbolMap.MatGetTexture);
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _mtxStacks = (byte*)Native.Sym(SymbolMap.MAurMtxStack);
        _runtime = (byte*)Native.Sym(SymbolMap.GlShaderRuntimeData);
        if (_runtime == null)
            log.Info("AlloyRenderer: GLRender::m_shaderExposedRuntimeData unresolved -- #123's feature " +
                     "switches stay inert (no wind sway, roofs where authored), and #124's camera dissolve " +
                     "with them -- uKeyholeCam reads this struct too");
        _fog = (float*)Native.Sym(SymbolMap.GlFog);
        _shaderConstF = (float*)Native.Sym(SymbolMap.GlGlobalShaderConstantsF);
        // #124 degrades one term at a time and says which: no fog block leaves Alloy parts unfogged (the
        // pre-#124 look), no constants leaves the dissolve aperture at zero, i.e. no dissolve. Neither is a
        // misrender, and neither is worth standing the pass down for.
        if (_fog == null)
            log.Info("AlloyRenderer: GLRender::m_fog unresolved -- #124's scene fog stays off on Alloy parts");
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
        _repackSlot = hooks.Chain(SymbolMap.VdoRepack,   // shared symbol: batcher + Alloy cast invalidate too (#88)
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnVdoRepackHook, RepackPriority);
        // #179: the six look/debug dials above are /draw's, not the environment's -- the code defaults ARE
        // the boot values and nothing here reads a knob for them any more.
        // #137: the skinmesh class. Two independent things must hold -- the engine's bone array must resolve
        // (nothing to copy without it) and the class must have a capture hook, which BatchService reports.
        // Config bucket (ADR 0013): a capability gate that decides what the vertex stages are BUILT from, so
        // it cannot move mid-session and is not a command.
        _skinning = Settings.Bool("ownskin", true);
        if (_skinning)
        {
            _bones = (float*)Native.Sym(SymbolMap.GlRenderBones);
            if (_bones == null)
            { _skinning = false; log.Info("AlloyRenderer: GLRender::m_bones unresolved -- skinmeshes stay engine-drawn"); }
        }
        _isEnabled = (delegate* unmanaged<int, byte>)Native.Sym(SymbolMap.RiIsEnabled);
        // Hook install is its own walk, and it stays HERE rather than joining Attach above: a pass that hooks
        // the engine on a client where the Alloy renderer cannot arm would be suppressing draws nothing
        // replaces. Everything above this line has already returned in that case.
        PassRegistry.Global.Install(log, hooks);
        // #215: last, and after PassRegistry -- the shim calls through hook sites every pass above it owns
        // (the beam window, the emitter suppression, the bucket gate), so they have to be installed first.
        RspShim.Install(log, hooks);
        _armed = _origRsp != null && _origFade != null;
        if (_armed) log.Info(_skinning
            ? "AlloyRenderer: #137 skinmeshes owned -- posed per frame off GLRender::m_bones (ownskin=0 in sowx.cfg disables)"
            : "AlloyRenderer: skinmeshes stay engine-drawn (cloaks, robes, tails)");
        if (_armed) log.Info($"AlloyRenderer: /draw {DescribeTuning()}");
        log.Info(_armed
            ? "AlloyRenderer: armed -- Alloy world pass at the #132 frame anchor, Scene::FadeDoors (/vanilla disables)"
            : $"AlloyRenderer: {(_origRsp == null ? "RenderSinglePass" : "Scene::FadeDoors")} hook failed -- Alloy world pass unavailable");
    }

    /// <summary>#179: the boot values <c>/draw reset</c> returns to. Consts rather than a captured snapshot,
    /// because with the env reads gone the code default IS the boot value -- and a const cannot drift from
    /// the field initialiser the way a hand-copied literal did.</summary>
    private const bool DefLighting = true, DefPrepass = true, DefAoTest = false;
    private const float DefSunBias = 0.06f;
    private const int DefViz = 0;

    /// <summary>The Alloy pass's live dials, for the <c>/draw</c> echo and the arm-time line.</summary>
    internal static string DescribeTuning() =>
        $"light {(_lighting ? 1 : 0)}  prepass {(_depthPrepass ? 1 : 0)}  " +
        $"viz {_lightViz}/{LightVizMax}  aotest {(_aoTest ? 1 : 0)}  csmbias {_sunWorldBias:F3}";

    /// <summary>Live tuning setter behind <c>/draw</c>. Returns the old value, or NaN for an unknown key.
    /// <para>Same threading argument as <c>/light</c>: each dial is an independent field written from the
    /// input thread and read by the render thread on its next pass, so a frame that catches one changed and
    /// one not is one frame of a value the user is dragging anyway. Booleans are set as 0/1.</para>
    /// <para>The viz ceiling is a REAL gate, not a formality: a value past it used to leave the mode at 0
    /// doing nothing, with no line to say so -- #129's water views (9..13) read as "LIGHTVIZ is broken" for
    /// exactly that reason. Clamped rather than refused, so the echo reports what is actually running.</para></summary>
    internal static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            case "light":    old = _lighting ? 1f : 0f; _lighting = v != 0f; break;
            case "prepass":  old = _depthPrepass ? 1f : 0f; _depthPrepass = v != 0f; break;
            case "viz":      old = _lightViz; _lightViz = Math.Clamp((int)v, 0, LightVizMax); break;
            case "aotest":   old = _aoTest ? 1f : 0f; _aoTest = v != 0f; break;
            case "csmbias":  old = _sunWorldBias; _sunWorldBias = v; break;
            default: return float.NaN;
        }
        _log?.Info($"AlloyRenderer: /draw {key} {v:F2} (was {old:F2}) -> {DescribeTuning()}");
        return old;
    }

    /// <summary>Put every <c>/draw</c> dial back to the value this session booted with.</summary>
    internal static void ResetTuning()
    {
        _lighting = DefLighting; _depthPrepass = DefPrepass; _aoTest = DefAoTest;
        _sunWorldBias = DefSunBias; _lightViz = DefViz;
        _log?.Info($"AlloyRenderer: /draw reset -> {DescribeTuning()}");
    }

    /// <summary>Headless check of the <c>/draw</c> contract: every documented key is reachable, an unknown one
    /// is reported rather than silently ignored, the viz ceiling clamps instead of no-opping, and reset
    /// returns the boot values. Restores whatever it touched -- it runs before the first frame, so a dial left
    /// at 3 would be a debug view nobody asked for.</summary>
    internal static void TuneSelfCheck(ExtenderLog log)
    {
        ExtenderLog? quiet = _log; _log = null;
        bool ok = float.IsNaN(Tune("nope", 1f));
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"AlloyRenderer.TuneSelfCheck FAIL: {w}"); } }

        bool li = _lighting, dp = _depthPrepass, ao = _aoTest;
        float cb = _sunWorldBias;
        int vz = _lightViz;

        foreach (string key in new[] { "light", "prepass", "viz", "aotest", "csmbias" })
            E(!float.IsNaN(Tune(key, 1f)), $"'{key}' is reachable");
        E(_lighting && _depthPrepass && _aoTest && _sunWorldBias == 1f && _lightViz == 1,
          "every key lands on its own field");
        E(Tune("light", 0f) == 1f && !_lighting, "a boolean dial takes 0 and reports its old value as 1");
        E(Tune("viz", LightVizMax + 5f) == 1f && _lightViz == LightVizMax,
          "a viz mode past the ceiling clamps to it rather than silently doing nothing");
        E(Tune("viz", -3f) == LightVizMax && _lightViz == 0, "...and a negative one clamps to off");

        ResetTuning();
        E(_lighting == DefLighting && _depthPrepass == DefPrepass && _aoTest == DefAoTest
          && _sunWorldBias == DefSunBias && _lightViz == DefViz,
          "reset returns every dial to its boot value");
        E(DescribeTuning().Contains("light") && DescribeTuning().Contains("prepass")
          && DescribeTuning().Contains("viz")
          && DescribeTuning().Contains("aotest") && DescribeTuning().Contains("csmbias"),
          "the echo names every key the command accepts");

        _lighting = li; _depthPrepass = dp; _aoTest = ao; _sunWorldBias = cb; _lightViz = vz;
        _log = quiet;
        log.Info(ok ? "AlloyRenderer.TuneSelfCheck: passed"
                    : "AlloyRenderer.TuneSelfCheck: FAILED -- the /draw knob table drifted from its keys");
    }

    /// <summary>#132's anchor site. After-original: <c>Scene::FadeDoors</c> is engine bookkeeping the owned
    /// pass reads, so it has to have run. A failed install leaves <see cref="_origFade"/> null and the pass
    /// never draws -- reported at arm time rather than as a silently vanilla world.</summary>
    [UnmanagedCallersOnly]
    private static void OnFadeDoors(IntPtr scene)
    {
        if (_origFade != null) _origFade(scene);
        OnFrameAnchor(scene);
    }

    [UnmanagedCallersOnly]
    private static void OnRenderSinglePass(IntPtr scene)
    {
        PassGen++;   // unconditionally: a stale generation would keep the iterator skipping parts nobody draws
        bool world = IsWorldScene(scene);
        if (world && AlloyGate.Alloy)
        {
            // saturating, never wrapping: an unchecked ++ past int.MaxValue lands on int.MinValue and the
            // threshold is then unreachable for 2^31 passes -- i.e. enrollment silently never runs
            // #236: the latch enrolls as soon as the throttle opens, so the bound on ownership latency is
            // NewCandidateGap frames from the completing walk rather than the full cadence.
            long tEnroll = FrameZones.Now;
            if (_newCandidate && _sinceEnroll >= NewCandidateGap) { _candidateEnrolls++; Enroll(scene); }
            else if (_sinceEnroll < EnrollEvery) _sinceEnroll++; else Enroll(scene);
            FrameZones.Add(FrameZones.Zone.Enroll, tEnroll);
        }
        // #215: THE CUTOVER. Same gate the enrollment above uses, plus the shim's own arming -- so the key
        // being on is never enough on its own: a pass with no 4.6 context, no module, or /vanilla still gets
        // the engine's own RenderSinglePass, which is the only frame there is behind it.
        if (world && Armed4Shim)
        {
            // Enrollment cadence is the only thing that puts parts on screen now; pre-cutover the engine
            // drew the world through the gap. On a cold start or an area transition that gap is up to
            // EnrollEvery frames of NOTHING AT ALL, so it is closed here rather than waited out.
            // The condition runs until something actually enrolls, not just until the Scene matches: the
            // first walk of a new Scene records parts as StProbe, and TryOwnCandidateInner refuses every
            // non-StActive entry -- so the enrollment a bare scene check triggers sees an empty table,
            // sets _enrollScene, and hands the next real chance back to the 60-frame cadence.
            if (scene != _enrollScene) _forcedEnrolls = 0;
            if (scene != _enrollScene || (_ownCount == 0 && _forcedEnrolls < MaxForcedEnrolls))
            { _sinceEnroll = EnrollEvery; _forcedEnrolls++; }
            bool prevShim = _inColourPass;
            _inColourPass = true;
            RspShim.Run(scene);
            _inColourPass = prevShim;
            if (!prevShim) AuditPass();
            return;
        }
        // Only a colour RenderSinglePass OF THE WORLD SCENE may draw Alloy parts. RenderDrawBucket's other
        // caller is Scene::RenderDynamicGeometry (buckets 2,3; objdump: those two callers and no more), and
        // drawing from there stamps a part "drawn" outside the colour walk, which then suppresses parts
        // nobody drew. A GUI 3D panel is the same hazard one level up: same virtuals, different Scene (#91).
        bool prev = _inColourPass;
        _inColourPass = world;
        if (_origRsp != null) _origRsp(scene);
        _inColourPass = prev;
        if (!prev) AuditPass();
    }

    /// <summary>Is this the world Scene? A Scene's static tile BSP (<see cref="SceneLayout.SceneBspRoot"/>) is
    /// written by <c>Scene::InitBSP</c> alone (@0x46c330; <c>Scene::Scene</c> zeroes it, and every
    /// <c>SceneBSPAdd*</c> only inserts into an existing root). A GUI 3D panel's Scene -- which reaches the
    /// same <c>Scene::RenderSinglePass</c> through <c>Camera::RenderScene</c>, off <c>Camera+0x90</c> -- is
    /// built by <c>CGuiPanel::CreateSceneView</c> @0x68759e: <c>NewCAurScene("sceneGUI_&lt;panel&gt;")</c> +
    /// camera + SetAmbient, no InitBSP, so its root stays null. This is also the engine's own test --
    /// <c>ManageSceneBSP</c> @0x47fa80 early-outs on exactly it.</summary>
    internal static bool IsWorldScene(IntPtr scene) =>
        scene != IntPtr.Zero && *(IntPtr*)(scene + SceneLayout.SceneBspRoot) != IntPtr.Zero;

    /// <summary>#216: the terms <see cref="AlloyGate"/> reads out of this class. <c>_armed</c> is "the hooks
    /// installed"; the capability pair is "a program built and the multidraw entry points exist", both of
    /// which latch and are deliberately never cleared by a context loss (see <see cref="ContextLost"/>).</summary>
    internal static bool ArmedForFrame => _armed;
    internal static bool DrawCapable => !_progFailed && !_batchUnavailable;

    /// <summary>#215: may the shim replace this pass? Taking the engine's frame away and THEN refusing to
    /// draw leaves the world as bare clear colour under the 2D UI, with nothing behind it -- so every
    /// refusal <see cref="DrawOwned"/> can answer from state that outlives the pass is asked here first.
    /// <para><c>PostChain.On</c> is the <c>hdr=0</c> case and <see cref="PartTable"/><c>.Active</c> the
    /// <c>drawhooks=off</c>/<c>=stats</c> one: both are supported settings that, paired with
    /// <c>ownframe=1</c>, would otherwise produce a permanently empty world with no reason given.
    /// <c>PartTable.Active</c> is read HERE rather than at the shim's install because BatchService
    /// initializes after this service (discovery is alphabetical) and has not set it yet.</para>
    /// <para>What this canNOT cover is the gamma refusal, which is re-asked per pass inside the anchor,
    /// after that pass's engine frame is already gone. ADR 0016 scopes exactly that to step 7 (#216): every
    /// refusal becomes frame-start-only, because there is no mid-frame fallback once the shim is in.</para></summary>
    private static bool Armed4Shim => RspShim.Armed && RspShim.Ready && AlloyGate.Alloy;

    /// <summary>#215: consecutive enrollments forced by an empty ownership set, and the ceiling on them.
    /// Convergence takes ~3 (walk records StProbe, probation completes on the next walk, the enrollment
    /// after that accepts) -- the cap is loose around that. Without it, an area with genuinely nothing
    /// ownable would run a full table scan at frame rate for as long as the player stood in it.</summary>
    private static int _forcedEnrolls;
    private const int MaxForcedEnrolls = 8;

    /// <summary>#236: the floor on frames between enrollments a NEW CANDIDATE may force. Enrollment is the
    /// cold path (a full table scan, the handle wipe, the texture re-intern), and creature parts re-record
    /// constantly -- unthrottled, <see cref="NoteNewCandidate"/> would run it every frame. This is also the
    /// stated bound on the latency: a part that becomes ownable is owned within this many frames, and looks
    /// vanilla-shaded (not missing -- see <see cref="RspShim.HandBack"/>) until then.</summary>
    private const int NewCandidateGap = 10;
    private static bool _newCandidate;
    private static long _candidateEnrolls;

    /// <summary>#236: a part just became ownable. The 60-frame cadence was tuned for a world where the engine
    /// drew whatever Alloy had not got to; under the cutover the gap is a hole, so a fresh candidate pulls
    /// the next enrollment forward instead of waiting out the tick.
    /// <para>A LATCH, not a nudge. Dropping the request when the throttle is closed would leave a lone
    /// candidate that completed just after an enrollment waiting out the full cadence anyway -- which is the
    /// bug, not a throttled version of the fix.</para></summary>
    internal static void NoteNewCandidate() => _newCandidate = true;

    private static bool _inColourPass;
    /// <summary>Scene the ownership table was enrolled from. The Alloy block draws only into a bucket walk of
    /// THIS Scene, which is what makes one-owner true by construction rather than by the colour-pass gate
    /// alone. An area transition that replaces the Scene leaves it stale, so nothing draws until the next
    /// enrollment picks up the new one -- bounded by <see cref="EnrollEvery"/>.</summary>
    private static IntPtr _enrollScene;
    /// <summary>Bucket 0/1 walks of a Scene that is not <see cref="_enrollScene"/> -- the #91 sighting counter.
    /// Non-zero means GUI 3D panels really do walk the opaque buckets and the gate is earning its keep.</summary>
    private static int _foreignBuckets;
    private static int _drawnPass = -1;   // pass whose Alloy block already ran (once per pass)

    /// <summary>Ownable parts the last enrollment turned away for want of a slot. Non-zero means the area is
    /// past <see cref="MaxOwned"/>, which is not just "some tiles stay vanilla": the spill set is decided by
    /// hash-table walk order, so it re-picks itself every enrollment and tiles visibly change renderer under
    /// a static camera. It is the first number to read when the world looks spotty.</summary>
    private static int _capacitySpill;

    /// <summary>The capacity clause of the [alloy] line -- silent when there is room, loud when there is not,
    /// because "4082/5631" only reads as a bug if you happen to know what MaxOwned is.</summary>
    internal static string CapacityNote()
        => _capacitySpill == 0 ? "" :
           $" (AT CAPACITY: {_capacitySpill} ownable parts spilled to the engine, MaxOwned={MaxOwned} -- the "
         + "spill set re-picks itself every enrollment, so those parts CHANGE RENDERER as you watch)";

    /// <summary>Exactly-one-owner audit, after the pass's bucket walks have run: a part we drew this pass
    /// that the iterator never suppressed was either drawn by the engine too (double-draw -- the z-fighting
    /// signature) or never walked at all (ghost geometry we drew while the engine had culled it).</summary>
    private static void AuditPass()
    {
        for (int i = 0; i < _ownCount; i++)
        {
            if (_oTri[i] == IntPtr.Zero) continue;
            int e = _oEntry[i];
            if (AlloyCensus.OwnGen[e] == PassGen && AlloyCensus.OwnSupGen[e] != PassGen)
            {
                _unmatched++; _unmatchedBucket[_oBucket[i]]++; _unmatchedCls[_oCls[i]]++;
                if (!PartTable.SightedThisFrame(e)) _unmatchedUnseen++;
            }
        }
    }

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
        if (!AlloyGate.Alloy || _ownCount == 0) return;
        // #91: never paint into a foreign Scene. Only a NON-world pass counts as a foreign sighting -- a world
        // pass that misses is the enrolled Scene having been replaced (area transition), which resolves itself
        // within one EnrollEvery and must not read as evidence that GUI panels reach this anchor.
        if (scene != _enrollScene) { if (!_inColourPass) _foreignBuckets++; return; }
        if (!_inColourPass) return;
        if (_drawnPass == PassGen) return;
        _drawnPass = PassGen;
        // Engine context, before the bracket: harvesting a VDO's attribute formats is a read of the ENGINE's
        // VAO (see VdoSlot), so it cannot happen on the 4.6 side.
        for (int i = 0; i < SkyBox.Count; i++)
        {
            ref SkyBox.Part p = ref SkyBox.At(i);
            p.Vao = SkyBox.Fresh(i) ? VdoSlot(p.Vdo) : -1;
        }
        DrawOwned();
    }

    /// <summary>#137: solve every Alloy skinmesh's pose for THIS frame, in engine context, before the
    /// bracket -- and since #132 after <see cref="PostChain.Precheck"/>, so a pass that is standing down
    /// does not solve poses the engine is about to solve again itself.
    /// <para>The timing is the whole point, and it is why this exists at all rather than the pass simply
    /// reading <c>m_bones</c>. The Alloy pass draws at bucket 0 or 1, before the engine has walked the
    /// dynamic buckets a creature lives in -- so at this moment <c>m_bones</c> holds the LAST skinned part of
    /// the PREVIOUS frame, and nothing else. A robe drawn from that would trail the body it hangs on by a
    /// frame while the body's own node matrix is current: visible swimming at the waist, not a subtle lag.
    /// Calling <c>PartSkin::Draw</c> with the funnel swallowed runs the engine's own bone solve and stops
    /// short of the draw, which costs nothing extra overall -- the iterator then suppresses the draw the
    /// engine was going to make anyway.</para>
    /// <para>A part whose pose does not come back (<c>IsPartRenderable</c> refused it, or the slots ran out)
    /// is un-owned here and now. That is in time to matter: the iterator's suppression happens at buckets
    /// 2/3, later in the same frame, so clearing the flag hands the part back to the engine rather than
    /// dropping it.</para></summary>
    private static void PoseSkinned()
    {
        _boneSlots = 0;
        // Nothing to pose for if the pass cannot draw: DrawOwned bails on these two without stamping a
        // single part, so the engine walks every one of them itself -- and would then solve each pose a
        // second time.
        if (!_skinning || _bones == null || _progFailed || _batchUnavailable) return;
        for (int i = 0; i < _ownCount; i++)
        {
            _oBone[i] = -1;
            if (_oTri[i] == IntPtr.Zero || !_vSkin[_oVao[i]]) continue;
            // Identity first, and here it is not the usual "a stale read is acceptable" case: this call
            // enters ENGINE code with the part as `this`, so a recycled pointer is a CHASED one. The same
            // single-level compare BuildBatches revokes on. A failure here is not counted as an unsolved
            // pose -- the part was already gone, and conflating the two would send the next diagnosis after
            // a skinning bug that is really a lifetime one.
            if (!IdentityOk(i, _oTri[i])) { RevokeSkinned(i); continue; }
            if (_boneSlots >= MaxBoneSlots) { _skinSlotOverflow++; RevokeSkinned(i); continue; }
            if (!BatchService.ForceSkinPose(_oTri[i])) { _skinNoPose++; RevokeSkinned(i); continue; }
            int slot = _boneSlots++;
            fixed (float* dst = _boneStage)
                Buffer.MemoryCopy(_bones, dst + slot * BoneFloats, BoneFloats * 4L, BoneFloats * 4L);
            _oBone[i] = slot;
            _skinDrawn++;
        }
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

    /// <summary>#86 frame-edge handler. Resuming enrolls from a table PartTable just wiped, so the first
    /// attempts necessarily come up empty (parts must be re-recorded and complete first). Retry every pass
    /// until one lands rather than spending a full <see cref="EnrollEvery"/> cadence per attempt -- that is
    /// the difference between ownership resuming in a few frames and in a few seconds. The diagnostics are
    /// re-armed too: every resume is a fresh chance to see WHY nothing enrolled.</summary>
    internal static void OnRenderModeEdge(bool owned)
    {
        if (!owned) return;
        _sinceEnroll = EnrollEvery;
        _resumeRetries = ResumeRetries;
        _enrollDiagLeft = EnrollDiagBudget;
        _reportedHit = false;
    }

    /// <summary>Iterator sighting from BucketGate's GetNextItem hook: an Alloy part just came up in the
    /// engine's bucket walk. True = skip it (we drew it this pass and it sits in buckets 0/1); false = let
    /// the engine draw it, which is what a mid-frame revocation or a skipped Alloy draw falls back to.</summary>
    internal static bool IterSight(int e, int bucket)
    {
        // #126: the bucket list is ONE list with the capture gate's (AlloyCensus.OwnableBucket) -- 0/1 plus
        // the three residual static classes 4/7/8, and since #134 the dynamic gob pair 2/3. Suppressing a
        // bucket the Alloy pass does not draw drops geometry; drawing one it does not suppress double-draws
        // it.
        // #129: ...and bucket 6 answers to one thing more. Every other class is staged and drawn in the same
        // breath, so OwnGen implies "drawn"; water is staged in the preamble and drawn only if the ONE
        // framebuffer copy could be taken, which is not knowable until the copy is attempted. A pass that
        // stood the copy down has stamped water parts it never drew, and suppressing those is a hole in the
        // sea rather than a fallback.
        if (bucket == WaterBucket && !_waterDrew) { _fallbacks++; return false; }
        if (AlloyCensus.OwnGen[e] != PassGen || !AlloyCensus.OwnableBucket(bucket))
        { _fallbacks++; return false; }
        AlloyCensus.OwnSupGen[e] = PassGen;
        _skips++;
        return true;
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
        _prog46 = 0; _progLit46 = 0; _progDepth = 0; _progDepthKey = 0; _progWater = 0; _fbo46 = 0;
        _drawBuf = 0; _texHBuf = 0; _boneBuf = 0;
        // ...and the VAO cache. RevokeAll marks the slots stale, and EnsureVao DELETES a stale non-zero name
        // before rebuilding -- which on a name minted by a context that no longer exists is a delete aimed at
        // whatever the next context recycles that number for.
        for (int v = 0; v < MaxVdos; v++) { _vVao46[v] = 0; _vVaoOwn[v] = 0; }
        _waterFailed = false;
        AlloyGate.Forget();   // #216: its latched refusals are facts about a target this context owned
        DepthResource.Revoke();
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
        if (_wasInModule && !inModule) RevokeAll("module unload");
        _wasInModule = inModule;
        if (_tableGen != PartTable.TableGen) { RevokeAll("part table wiped"); _tableGen = PartTable.TableGen; }

        // the overflow counter lives in a GL buffer, so its readback has to happen inside the 4.6 bracket:
        // arm it one frame ahead of the log so the value the log prints is this period's
        int period = _loggedOnce ? LogEvery : FirstLogAt;
        if (_frames == period - 2) _takeOverflow = true;
        if (++_frames < period) return;
        _loggedOnce = true;
        // distinct textures is the BATCH FLOOR: one program + one VDO cannot batch below one call per texture,
        // so batches/frame ~= textures tells you the grouping is optimal and the ratio is scene-bound, while
        // batches >> textures says the sort is failing. Without it the ratio alone cannot distinguish the two.
        // #120 made the floor a SOFT one: slots 1..5 joined the batch key but not the radix digit (which still
        // sorts on the diffuse alone), so two materials sharing a diffuse and differing in their normal map
        // now cut a batch the count does not predict. A small excess over the floor is that, not a regression.
        _log?.Info($"AlloyRenderer: [alloy] {_lastEnrolled}/{_lastCandidates} parts owned{CapacityNote()} on {_vdoCount} VDOs ({OwnedVaos()} on owned buffers, drawn by {OwnedClasses()}) " +
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
                      : _skinDrawn == 0 && _skinNoPose == 0 && _skinSlotOverflow == 0 ? ""
                      : $", {(float)_skinDrawn / _frames:F1} skinmeshes posed/frame" +
                        (_skinNoPose == 0 ? "" : $", {_skinNoPose} handed back unposed") +
                        (_skinSlotOverflow == 0 ? "" : $", {_skinSlotOverflow} over the {MaxBoneSlots}-slot bone budget")) +
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
                   $"iter-skips {(float)_skips / _frames:F0}/frame, " +
                   $"engine fallbacks {(float)_fallbacks / _frames:F1}/frame " +
                   $"(engine-culled {(float)_skipUnseen / _frames:F0}" +
                   (_skipUnseen == 0 ? "" : " by capture bucket" + ByBucket(_unseenBucket)) + " " +
                   $"unlit-pass {(float)_skipNoLight / _frames:F0} identity {(float)_skipIdentity / _frames:F0} " +
                   $"dissolve {(float)_ownDissolve / _frames:F0} owned, fade {(float)_ownFade / _frames:F0} owned " +
                   $"no-mdl {(float)_skipNoMdl / _frames:F0} tex {(float)_skipTex / _frames:F0} " +
                   $"range {(float)_skipRange / _frames:F0} vao {(float)_skipVao / _frames:F0} " +
                   $"instance-state {(float)_skipGob / _frames:F0}), " +
                   $"normal maps {(float)_nrmBound / _frames:F0} bound of " +
                   $"{(float)_nrmDeclared / _frames:F0} declared/frame, " +
                   $"UNMATCHED {(float)_unmatched / _frames:F1}/frame" +
                   (_unmatched == 0 ? "" : $" ({(float)_unmatchedUnseen / _frames:F0} of them never walked by " +
                    "the engine at all -- geometry it culled and we drew anyway; the rest were walked and " +
                    "not suppressed, which is a bucket-list gap) (by capture bucket" + UnmatchedByBucket() +
                    $"; classes {string.Join("/", _unmatchedCls)})") + ", " +
                   (_watchEnrolled == 0 && AlloyCensus.WatchEngineDraws == 0 ? "" :
                       $"dumptex {_watchEnrolled} enrolled, {(float)_watchSubmitted / _frames:F1} submitted/frame, " +
                       $"{(float)AlloyCensus.WatchEngineDraws / _frames:F1} engine draws/frame, ") +
                   $"foreign-scene buckets {(float)_foreignBuckets / _frames:F1}/frame, " +
                   // #125: "masked" is the criterion, not "captured" -- it counts the frames the engine's own
                   // sky wrote nothing because the Alloy one had already drawn. Captured but never masked
                   // means the pass is staging nothing, which is a different bug from never seeing the sky.
                   // #175: every registered pass's clause, in declaration order. #139's "post 0 resolves"
                   // while the world renders is the whole reason this is a roll-call and not a fixed list --
                   // the frame looks fine, just 8-bit and un-bloomed, and only the clause says so.
                   PassRegistry.Global.Stats(_frames) + ", " +
                   $"enrolls={_enrolls} ({_candidateEnrolls} pulled forward by a new candidate) revokes={_revokes}; " +
                   // #119: the prepass's own cost, and what it bought. samples/px is the overdraw figure --
                   // run the same scene with /draw prepass 0 and the difference IS the pass's worth.
                   (!_depthPrepass ? "prepass OFF (/draw prepass 1 restores it)"
                    : _progDepth == 0 ? "prepass unavailable"
                    : $"prepass {(float)_prepassDraws / _frames:F0} draws/frame" +
                      (_prepassKeyed == 0 ? "" : $" ({(float)_prepassKeyed / _frames:F0} of them dissolving, keyhole stage)") +
                      (_prepassSkipped == 0 ? "" : $" (+{(float)_prepassSkipped / _frames:F0} excluded, displacing/skinned or cutout)") +
                      (_qPrepass.Measured ? $" in {_qPrepass.Avg / 1e6:F3}ms" : "")) +
                   (_qSamples.Measured && _lastPixels > 0
                       ? $", shaded {_qSamples.Avg / 1e6:F2}M samples/frame over {_lastPixels / 1e6f:F2}Mpx " +
                         $"(overdraw x{_qSamples.Avg / _lastPixels:F2})"
                       : "") +
                   (_qColour.Measured ? $", colour passes {_qColour.Avg / 1e6:F3}ms (opaque+blended+additive)" : "") +
                   (_qParticles.Measured || _qWater.Measured || _qPost.Measured
                       ? $", gpu split: particles {_qParticles.Avg / 1e6:F3} / water {_qWater.Avg / 1e6:F3} / " +
                         $"grass {_qSkyGrass.Avg / 1e6:F3} / post {_qPost.Avg / 1e6:F3} ms" : "") +
                   // #121: captures per SESSION against frames per PERIOD. A number that tracks the frame
                   // count is the acceptance criterion ("probe capture does not run per frame") failing.
                   $", probe {(EnvProbe.Ready ? $"ready ({EnvProbe.Captures} captures)" : "none yet")}" +
                   // Which parts the probe reaches: with no area env map it is the env-mapped materials
                   // alone, which is what vanilla binds a texture for.
                   $" {(_sceneEnv && EnvProbe.AreaWide ? "area-wide" : "env-mapped parts only")}" +
                   (_sceneEnv && !EnvProbe.AreaWide ? " (area declares one; /probe areawide 1 honours it)" : "") + "; " +
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
        _frames = 0; _draws = 0; _batches = 0; _skips = 0; _fallbacks = 0; _unmatched = 0; _revokes = 0; _enrolls = 0; _candidateEnrolls = 0;
        _skinDrawn = 0; _skinNoPose = 0; _skinSlotOverflow = 0;
        _foreignBuckets = 0;
        SkyBox.ResetPeriod();
        GrassField.ResetPeriod();
        Beams.ResetPeriod();
        Particles.ResetPeriod();
        _watchSubmitted = 0; AlloyCensus.WatchEngineDraws = 0;
        _skipNoLight = _skipIdentity = _skipNoMdl = _skipTex = _skipRange = _skipVao = _skipUnseen = _skipGob = 0;
        _ownDissolve = 0; _ownFade = 0;
        _nrmDeclared = _nrmBound = 0;
        Array.Clear(_unmatchedBucket); Array.Clear(_unseenBucket); Array.Clear(_unmatchedCls); _unmatchedUnseen = 0;
        Array.Clear(_drawsByClass);
        _clusterOverflow = 0;
        _prepassDraws = 0; _prepassSkipped = 0; _prepassKeyed = 0; _qPrepass.Reset(); _qSamples.Reset(); _qColour.Reset();
        _qParticles.Reset(); _qWater.Reset(); _qSkyGrass.Reset(); _qPost.Reset();
    }

    /// <summary>The unmatched total, split by the bucket the parts were captured in -- only the buckets that
    /// actually contributed, so the line stays short when one class dominates.</summary>
    internal static string UnmatchedByBucket() => ByBucket(_unmatchedBucket);

    /// <summary>A per-bucket counter as "bucket:per-frame", only the buckets that contributed.</summary>
    internal static string ByBucket(long[] counts)
    {
        string s = "";
        for (int b = 0; b < counts.Length; b++)
            if (counts[b] != 0) s += $" {b}:{(float)counts[b] / _frames:F0}";
        return s.Length == 0 ? " none" : s;
    }

    private static uint _progWater;
    private static bool _waterFailed, _waterArmed, _waterDrew;
    /// <summary>The engine's water draw bucket (<c>Scene::RenderSinglePass</c> @0x46e5f0's "Water" scope).
    /// One name, read by the ownable-bucket list and by the suppression that has to agree with it.</summary>
    internal const int WaterBucket = 6;
    private static float* _waterView, _waterProj;
    private static float _waterNear = 1f, _waterFar = 100f;
    private static int _watVp, _watBase, _watTex, _watCut, _watAmbient, _watDiffuse, _watDir,
                       _watGrid, _watTile, _watZ, _watFalloff, _watWrap, _watAtten, _watHot,
                       _watWrapN, _watViz, _watEye, _watSunTex, _watSunVp, _watSunTexel, _watSunBias,
                       _watSunCount, _watCubeTex, _watCubeLight, _watCubePlane, _watCubeCount, _watCubeSoft,
                       _watProbe, _watProbeMips, _watProbeGain, _watSceneEnv, _watCapture,
                       _watFbColor, _watFbDepth, _watNearFar, _watView, _watProj, _watDials,
                       _watWaveTime, _watTess, _watWind;
    private static FeatLoc _watFeat;

    internal struct FeatLoc { internal int Time, Wind, Flags, Cam, Player, Fog, FogColor, KhDia, KhCam, Pixel; }
    private static FeatLoc _locFeat, _litFeat;
    /// <summary>#123: <c>GLRender::m_shaderExposedRuntimeData</c>, the struct the engine's own SetUniforms
    /// uploads these five from. Null (symbol unresolved) leaves the wind still and the roofs wherever they
    /// were authored, which is the pre-#123 look, not a misrender.</summary>
    private static byte* _runtime;

    internal const int RtTimeOfDay = 0x18, RtAreaWind = 0x28, RtAreaFlags = 0x3c,
                      RtPlayerPos = 0x4c, RtCamPos = 0x64;
    /// <summary>#124's two more fields of the same struct, from the same pairing: <c>cameraPitch</c> at
    /// +0x84 and <c>playerCameraDist</c> at +0x8c. Both feed the keyhole aperture.</summary>
    internal const int RtCamPitch = 0x84, RtCamDist = 0x8c;
    /// <summary>#124: <see cref="SymbolMap.GlFog"/>'s fields -- start, end, the display-space colour and the
    /// enable flag. <c>fogParams.w</c> is computed, not stored.</summary>
    internal const int FogStart = 0x00, FogEnd = 0x04, FogColor = 0x08, FogEnabled = 0x18;
    /// <summary>#124: the keyhole diameter pair inside
    /// <see cref="SymbolMap.GlGlobalShaderConstantsF"/>. Stored unordered -- SetUniforms uploads min as
    /// <c>keyholeDiameterMin</c> and max as <c>keyholeDiameterMax</c>, so the ordering happens here too.</summary>
    internal const int KeyholeDiaA = 21, KeyholeDiaB = 22;
    private static float* _fog;
    private static float* _shaderConstF;

    /// <summary>Stand-in area ambient for the frames before the engine's first index -1 light bind. Grey, not
    /// black: a missing capture must not paint the world dark.</summary>
    internal const float DefaultAmbient = 0.25f;

    /// <summary>Texture units the shadow maps bind on. Unit 0 is the part's diffuse and gets rebound per
    /// batch, so the maps live above it and are bound once per pass.</summary>
    internal const uint SunUnit = TextureSlots.SunShadow, CubeUnit = TextureSlots.LocalShadow;
    private static int _litCubeTex = -1, _litCubeLight = -1, _litCubePlane = -1, _litCubeCount = -1,
                       _litCubeSoft = -1;
    private static int _litProbe = -1, _litProbeMips = -1, _litProbeGain = -1, _litCapture = -1,
                       _litSceneEnv = -1;
    /// <summary>Shadow depth bias in WORLD units, sent per cascade as a fraction of that cascade's published
    /// depth range so it stays a constant world distance whatever the fit. Small because the receiver's normal
    /// offset carries most of the acne fight; <c>/draw csmbias</c> tunes it.</summary>
    private static float _sunWorldBias = DefSunBias;

}
