using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Services.Shadow;
using Sowx.Ui;

namespace Sowx.Services.Render;

/// <summary>
/// #82/#83/#84 (map #68, spec #77 M1): the owned opaque world colour pass, scaled from #81's single tracer part
/// to the full ownable-now set (#72). Every qualifying part -- diffuse-textured, no bumpy-shiny, no bone
/// streams, indexed VDO, an ownable bucket -- is drawn by SowxRender inside the
/// #80 4.6 context bracket: our own VAO per VDO over the shared engine buffers, texture ids re-fetched every
/// frame so animated textures keep cycling. Parts the engine would draw LIT go through the clustered
/// forward+ program over <see cref="OwnedLights"/>' unbounded light SSBO (#83), so the owned path has no
/// light-count ceiling to pop at; parts the engine draws unlit keep the flat variant, which is their vanilla
/// look. #120: a lit part is SHADED from its material, not just textured -- the five stock .mtr map slots
/// beyond the diffuse and the four stock parameter names feed a GGX lobe, which is the term vanilla runs on
/// every lit part and the owned pass used to drop on all of them. A lit part we cannot shade faithfully (no normal stream, lit program down) is handed back to the
/// engine rather than painted flat. The engine's bucket walk then skips
/// exactly those parts at the GetNextItem iterator, and only on passes where the owned draw actually
/// happened -- never dropped, never double-drawn (audited live as the UNMATCHED counter).
/// <para>#84: submission is MULTIDRAW-BATCHED, so draw count follows the (program, VDO, texture, primitive)
/// group count rather than the part count. Everything that used to be a per-part uniform -- model matrix,
/// texture rotation, material ambient/diffuse -- moved into a per-draw SSBO the shaders index by
/// <c>uDrawBase + gl_DrawID</c>, which is what lets one <c>glMultiDrawElementsBaseVertex</c> cover a whole
/// group. The draw block is therefore two phases: build (revalidate every part, stage its record, cut the
/// batch at each key change) then issue (one bind set + one multidraw per batch).</para>
/// <para>WHERE the owned block runs is load-bearing, and #132 settled it: the anchor is an after-original
/// hook on <c>Scene::FadeDoors</c>, the last engine call inside <c>Scene::RenderSinglePass</c> before
/// <c>RenderDrawBucket(0)</c> -- see <see cref="OnFrameAnchor"/> and <see cref="SymbolMap.FadeDoors"/> for
/// why literal RSP entry (spec #77's and #114's wording) is not reachable. What entry DOES give, and #108
/// established, is the camera: the engine never uses the GL matrix stack, so view/projection are valid from
/// the moment Camera::RenderScene writes them into m_aurMtxStack (nm 0x19e2f60; proj +0x18, view +0x70)
/// before the vcall. Drawing the whole set before the engine's first opaque walk is what keeps suppression
/// honest: the engine re-buckets geometry every frame, so a part's enrolled bucket goes stale and splitting
/// the draw by it lets the engine walk a part before we draw it -- double-draw, z-fighting.</para>
/// <para>Ownership lives as a column on BatchService's part table (<see cref="Perf.BatchService.OwnFlag"/>)
/// so the iterator's existing <c>Find</c> serves both. The table is rebuilt wholesale at a frame boundary
/// (enrollment, every <see cref="EnrollEvery"/> frames, before the owned block) -- that is the frame-atomic
/// swap. The only mid-frame mutation is revocation, which is safe in both directions because suppression is
/// gated on the per-pass draw generation. <c>SOWX_OWNDRAW=0</c> kill switch. Hot path allocates nothing.</para>
/// <para>#128: CREATURES join it, and they arrive by relaxing two things rather than by a new path. The
/// engine draws them from <c>Scene::RenderDynamicGeometry</c> -- buckets 2 and 3, but through the same
/// <c>RenderDrawBucket</c> the capture already brackets -- so what actually kept them out was BatchService's
/// matrix PROBATION, which killed any part whose transform moved between two sightings; an animating creature
/// fails that by construction and a door fails it the frame it opens. Instances skip probation now and are
/// recorded as MOVERS, whose transform this pass reads live at draw time anyway. The second relaxation is the
/// instance modulation: <c>PartTriMesh::Mat</c>'s alpha multiply, colour multiply and self-illum add are
/// folded into the per-draw record every frame instead of refusing the part, which matters because a creature
/// FADES through one of them -- opacity here is per-frame state, never capture state. What still cannot be
/// owned is a true skinmesh (a cloak, a robe, a tail: the owned vertex stages have no skinning branch) and the
/// three instance states no owned program expresses at all; both refuse against the GOB, and the enrollment
/// sweep then hands that whole creature back to the engine rather than half-own one (#107 s2's rule, per
/// instance instead of per class).</para>
/// <para>#85: water reflections need NOTHING from this pass. Spec #77's "reflection subframes also pass
/// through RenderSinglePass" reads the engine backwards -- <c>Camera::CopyRenderSubframe</c> is a
/// <c>CopyFramebuffer</c> plus a bind of the copy to texture units 11/12 (<c>texFBColor</c>/<c>texFBDepth</c>),
/// not a second scene pass, and RenderSinglePass takes it AFTER buckets 0/1/7 and immediately before the
/// bucket-5 water walk. Water is ordinary bucket-5 geometry whose <c>fswater.shd</c> ray-marches that
/// snapshot in screen space (normals reconstructed from depth, no G-buffer), so owned parts reflect because
/// they are already in the colour+depth attachments the snapshot copies.</para>
/// </summary>
public sealed unsafe class OwnedDrawService : IExtenderService
{
    private const int LogEvery = 900, EnrollEvery = 60;
    /// <summary>Frames before the FIRST period line. The 900-frame cadence is right for steady-state cost,
    /// but it is also the only proof this pass runs at all -- and at 900 a short session (or one that ends at
    /// a module unload) produces no line, which reads as "never armed" rather than "not yet". Report once
    /// early, then settle into the long cadence.</summary>
    private const int FirstLogAt = 120;
    /// <summary>Ownership capacity. Beyond this the surplus stays engine-drawn (staging), never dropped.
    /// <para>4096 was under the real population and it showed: a large area logged
    /// <c>4082/5631 parts owned</c> -- pinned at the cap, with ~1550 ownable parts spilling. That alone is
    /// "some tiles render vanilla", but the visible symptom was worse than that, because WHICH parts spill
    /// is decided by a first-come-first-served walk of BatchService's hash table. That order is stable only
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
    /// -- BatchService's linear group walk was O(n^2) across an area load, which is 24M field compares at
    /// 7000 groups and 13.4 BILLION at this cap. Without that, this number buys a load that never
    /// finishes.</para>
    /// <para>ponytail: still first-come-first-served, so an area past even this would flicker the way 4096
    /// did. The fix if that ever happens is a DETERMINISTIC near-to-far spill -- so the surplus is always
    /// the far geometry, where the seam is fog-compressed -- not a bigger number.</para></summary>
    private const int MaxOwned = 163840, MaxVdos = 4096;
    /// <summary>#84 batch-sort texture keys: distinct diffuse textures the sort can separate, and the
    /// open-addressed slot table that interns them (power of two, ~2x MaxMats for a 0.5 load factor). Overflow
    /// shares the last key, which costs batch size and nothing else -- the live flush key is the texture id.
    /// <para>Raised with <see cref="MaxOwned"/>: a large area logged exactly 1024 textures, i.e. saturated,
    /// which turns the last key into a bucket every unseen texture shares and costs the sort its whole
    /// point. Cheap to fix -- the tables are one int and one short per slot.</para></summary>
    private const int MaxMats = 16384, MatHashBits = 15, MatHash = 1 << MatHashBits;
    /// <summary>Floats per SowxDraw record: std430 <c>mat4 + vec4 rot + vec4 amb + vec4 dif + vec4 emi
    /// + vec4 pbr + vec4 spc + vec4 feat</c> = 176 bytes, whose alignment (16) already divides the size, so
    /// the array stride is 176 with no padding.</summary>
    private const int DrawFloats = 44;
    /// <summary>SSBO binding point of the per-draw block. Must match <c>DrawBufDecl</c>'s literal; 0..3 belong
    /// to <see cref="OwnedLights"/> (lights, cluster counts, cluster indices, stats).</summary>
    private const uint BindDraw = 4;
    /// <summary>#137: SSBO binding of the owned pass's bone block. 0..3 are OwnedLights', 4 is the per-draw
    /// record, 5 and 6 belong to <see cref="Shadow.CasterPass"/>. Must match <c>BoneBufDecl</c>'s literal.
    /// <para>Its own block rather than a share of the caster pass's: that one is written at the shadow
    /// bracket and read at the next bake, this one is written and read inside a single colour pass, and
    /// coupling two lifetimes to save 256KB buys a class of bug nobody can see.</para></summary>
    private const uint BindBones = 7;
    /// <summary>#153 bindless: SSBO binding of the per-draw texture-handle block (8 uvec2 per record).
    /// 0..3 = the light rig, 4 = draws, 5 = caster records, 6 = caster bones, 7 = owned bones.</summary>
    private const uint BindTexH = 8;
    /// <summary>Bones per skinned part, fixed by the engine: <c>GLRender::m_bones</c> is a 64 x mat4 static
    /// array and the vertex stream's indices address it directly. The matrices already sit in GLSL's
    /// column-major order (see <see cref="SymbolMap.GlRenderBones"/>), so a snapshot is a straight memcpy.
    /// </summary>
    private const int BoneCount = 64, BoneFloats = BoneCount * 16;
    /// <summary>Skinned parts posed per frame. 4KB each, so 64 slots is 256KB and covers a crowd; past it a
    /// skinmesh is handed back to the engine rather than drawn in a bind pose.</summary>
    private const int MaxBoneSlots = 512;
    /// <summary>aurMatrixStack layout (Ghidra GLRender::PushMatrix/PopMatrix/aurMatrixStack::SetIdentity):
    /// stride 0x58 per stack, top matrix (row-major float[16]) at +0x18. Modes: PROJECTION=0, VIEW=1.</summary>
    private const int StackStride = 0x58, StackTop = 0x18, ModeProjection = 0, ModeView = 1;
    /// <summary>VDO layout (Ghidra RenderVertexData/SetVertexDataObject): +0xc VAO id, vertex item table
    /// {offset,size} at +0x48 (count +0x50), element item table at +0x58 (count +0x60).</summary>
    private const int VdoVao = 0xc, VdoVertItems = 0x48, VdoVertCount = 0x50, VdoElemItems = 0x58, VdoElemCount = 0x60;
    /// <summary>Part field offsets (BatchService, Ghidra RenderDrawBucket/PartTriMesh::Draw).</summary>
    private const int PartNode = 0x88, PartMaterial = 0x58, PartTexRotate = 0xb0, PartTile = 0x98;
    /// <summary>#126: the part's LOCAL bounding box, min at +0x64 and max at +0x70 -- the pair
    /// <c>Camera::GetCanKeyholeDissolve</c> @0x4a6f70 hands to <c>BoxAbovePlane</c>, and the same one
    /// <c>ShadowFeedService</c>/<c>CasterPass</c> cull against. Its centre is the blended sort key.</summary>
    private const int PartBboxMin = 0x64, PartBboxMax = 0x70;
    /// <summary>#126: the part's own alpha multiplier (<c>PartTriMesh::GetIsOpaque</c>'s first term). Read
    /// live: it is animated state.</summary>
    private const int PartAlphaOff = 0xa0;
    /// <summary>Model matrix at MdlNode+0x18 -- RenderDrawBucket @0x46d060 passes exactly that address to
    /// RenderInterface::SetModelMatrix, and only for matrix-class parts (node+0x80 &amp; 0x1e).</summary>
    internal const int NodeMatrix = 0x18, NodeFlags = 0x80;
    /// <summary>The part's MdlNode (part+0x8) and its material colours, from Ghidra
    /// <c>PartTriMesh::MatStatic</c> @0x450560, which calls <c>SetMaterialv(prop 1, node+0xf4)</c>,
    /// <c>SetMaterialv(prop 2, node+0xe8)</c> and <c>SetMaterialv(prop 3, part+0x7c)</c>. Prop 3 takes the
    /// self-illumination colour, so the enum runs ambient/diffuse/emissive -- and node+0xe8 is the one
    /// carrying the part's alpha (part+0xa0), which in fixed-function material setup is the DIFFUSE term.
    /// Distinct from the bucket node at part+0x88.</summary>
    private const int PartMdlNode = 0x8, MdlDiffuse = 0xe8, MdlAmbient = 0xf4;
    /// <summary>Self-illumination colour (3 floats) at part+0x7c -- MatStatic's prop-3 material term. It is
    /// applied whenever <c>part+0x94 == 0</c>, and LightPartTriMesh (@0x47a6a0) clears that byte on every
    /// part under <c>g_bFullDynamicLighting</c>, so under the modern renderer it applies ALWAYS.</summary>
    private const int PartSelfIllum = 0x7c;
    /// <summary>#120: the material's parsed .mtr parameter list, <c>List&lt;MaterialParameter&gt;</c> at
    /// mat+0x118 -- Ghidra <c>Material::CheckAndBindCustomShader</c> @0x4c62f0 passes exactly
    /// <c>(this + 0x118)</c> to <c>SharedMaterial::BindShaderParameters</c>, which reads the element pointer
    /// at +0 and the count at +8. So the count sits at mat+0x120.</summary>
    private const int MatParams = 0x118, MatParamCount = 0x120;
    /// <summary>MaterialParameter layout, from <c>Material::SetMaterialParam</c> @0x4c5d70 (the name is
    /// <c>strncasecmp</c>'d over 0x40 bytes from the record's start) and BindShaderParameters (value floats at
    /// +0x44, the two cached uniform locations at +0x54/+0x58, the type discriminator at +0x5c, the component
    /// count as a ushort at +0x60). The record is 100 bytes -- both walks step by exactly that.
    /// <para>The type discriminator selects how +0x44 is READ, not where the value lives: 0 takes it as a
    /// <c>float*</c> with <paramref name="ParamComps"/> components, 1 as a single <c>int</c>, both off the
    /// same address.</para></summary>
    private const int ParamValue = 0x44, ParamType = 0x5c, ParamComps = 0x60, ParamStride = 100;
    /// <summary>A parameter list longer than this is not a .mtr, it is a bad pointer -- refuse it rather than
    /// walk off into the heap. Real materials declare a handful.</summary>
    private const int MaxParams = 64;
    /// <summary>SharedMaterial override on the Material (Ghidra <c>Material::GetTexture</c> @0x4c5810): its
    /// own eleven texture slots at shared+0xd8+i*0x10 win over the Material's own three.</summary>
    private const int MatShared = 0xd0;
    /// <summary>The SharedMaterial's own parameter list -- the one the .mtr's <c>parameter float &lt;Name&gt;
    /// &lt;value&gt;</c> lines are parsed onto. Ghidra <c>Material::SetMaterialParam</c> @0x4c5d70 walks
    /// <c>*(shared+0x1d0)</c> for <c>*(int*)(shared+0x1d8)</c> records of the same 100-byte layout.</summary>
    private const int SharedParams = 0x1d0, SharedParamCount = 0x1d8;
    /// <summary>#120 texture slots the owned lit program binds beyond the diffuse: 1 normal, 2 specular,
    /// 3 roughness, 4 height, 5 self-illumination (the stock map, inc_material.shd's header comment).
    /// They resolve through <c>Material::GetTexture</c> exactly as slot 0 does -- a SharedMaterial's own
    /// eleven slots at shared+0xd8+i*0x10 win, the Material's own three are the fallback.</summary>
    private const int PbrSlots = 5;
    /// <summary>#123: texture slot 6, the flow map, whose RG channels the flowmap shader reads as a UV flow
    /// direction (its own header says "supply your RG flow map in texture6"). Enrolled ONLY for a
    /// <see cref="ShaderFeature.Flowmap"/> part -- slot 6 is not part of the stock vocabulary, so probing it
    /// on every shared material would split batches over a texture nothing samples.</summary>
    private const int FlowSlot = 5;
    /// <summary>#146: texture slot 7, the ambient-occlusion map -- a baked visibility term in .r, sampled
    /// straight onto the ambient. Not stock vocabulary either (the stock set stops at texture5 and #123 took
    /// texture6), so this is the extender's convention, named the same way the flowmap's texture6 is; the
    /// engine's own FB-effect SSAO is a screen-space pass that keeps running underneath it, untouched.
    /// <para>Unlike the flow map this is probed on EVERY shared material -- there is no shader feature that
    /// announces it. That costs one <c>Material::GetTexture</c> per enrolled part, and a batch split only
    /// where a material actually declares one.</para></summary>
    private const int AoSlot = 6, MapSlots = 7;
    /// <summary>#146: the AO slot's MASK bit -- 1 &lt;&lt; 7, deliberately NOT <c>1 &lt;&lt; AoSlot</c>. Every
    /// other slot's bit is its index, but bit 6 was <see cref="EnvSlot"/> before this ticket and the lit
    /// stage is content-overridable: a hak shipped against an older build still reads 64 as "env-mapped".
    /// Taking 64 for AO would make that stale copy render every env material shiny-and-unreflective with
    /// nothing in the log. The bit is free, the drift is not.</summary>
    private const int AoBit = 1 << 7;
    /// <summary>Texture unit slot 1 binds on; the rest follow it. Units 0/1/2 are diffuse, sun cascades and
    /// the local cube array, and <see cref="EnvProbe.ProbeUnit"/> sits above all of them -- SelfCheck holds
    /// that ordering, because a samplerCube sharing a unit with a sampler2D kills every lit draw in silence.</summary>
    private const uint PbrUnit0 = 3;
    /// <summary>#121: the env-map bit of the same mask (<c>Material::HasEnvMap</c>, mat+0x40). One past the
    /// bindable texture slots, and unlike them it binds nothing -- it only tells the fragment stage that this
    /// is legacy "shiny" content whose diffuse alpha is a reflectivity mask.</summary>
    /// <summary>...and FROZEN at 64: shipped <c>sowx_ownlit_fs.shd</c> overrides carry a copy of it, so this
    /// value is content, not an implementation detail. A new slot takes the next free bit (see
    /// <see cref="AoBit"/>), never this one.</summary>
    private const int EnvSlot = 64;
    /// <summary>The SCENE's environment map (Ghidra <c>RenderFlat</c> @0x450eb0: a part with no env map of
    /// its own gets <c>currentscene+0x1e8</c> bound to the env sampler -- but ONLY if that field is set).
    /// Most areas leave it null, and that is why vanilla cobblestone does not reflect the sky: nothing is
    /// bound, so the ambient-specular sample reads nothing. It is the switch, not the texture -- what gets
    /// sampled here is the probe.</summary>
    private const int SceneEnvMap = 0x1e8;
    private const uint GlUnsignedShort = 0x1403;
    /// <summary>The GL blend factors <see cref="BlendGl"/> maps the Aurora ones onto.</summary>
    private const uint GlZero = 0, GlOne = 1, GlSrcColor = 0x0300, GlSrcAlpha = 0x0302,
                       GlOneMinusSrcAlpha = 0x0303, GlDstColor = 0x0306, GlOneMinusDstAlpha = 0x0305;
    /// <summary>Engine attribute locations, fixed for every engine program (Ghidra
    /// <c>GLRender::ConnectShaderData</c>: one glBindAttribLocation table before every link -- 0 vPos,
    /// 1 vTcIn, 2 vColor, 7 vNormal). They say which engine VAO slot holds each stream.</summary>
    private const uint EngPos = 0, EngUv = 1, EngNormal = 7;
    /// <summary>#96/#128: the bone streams of a true skinmesh, at the same fixed engine locations
    /// (<c>vIndex</c>/<c>vWeight</c> in ConnectShaderData's bind table). Both or neither -- a half-skinned
    /// VAO is not one this pass can reason about either way.</summary>
    private const uint EngBoneIdx = 8, EngBoneW = 9;
    /// <summary>#137: and OUR locations for the same two streams, which the skinned vertex stages declare.
    /// Same numbers as the engine's by choice, not by necessity -- the owned VAO binds them itself.</summary>
    private const uint LocBoneIdx = 8, LocBoneW = 9;
    /// <summary>Our own program's attribute locations. VAOs are ours, so the engine's locations only tell us
    /// which buffer/offset holds each stream -- we re-bind them wherever the program wants them.</summary>
    private const uint LocPos = 0, LocUv = 1, LocNrm = 2;
    /// <summary>PartTriMesh::TransformTextureMatrix @0x4500b0 -> Rotate({0,0,1}, part+0xb0), and
    /// aurMatrixStack::Rotate scales by 0x3c8efa35 = pi/180 before RotateRadians: the field is DEGREES.</summary>
    private const float DegToRad = 0.017453292f;

    private static ExtenderLog? _log;
    private static bool _armed;
    private static delegate* unmanaged<IntPtr, void> _origRsp;
    /// <summary>#132: the frame anchor's trampoline (<c>Scene::FadeDoors</c>).</summary>
    private static delegate* unmanaged<IntPtr, void> _origFade;
    private static delegate* unmanaged<IntPtr, int, long> _getTexture;
    private static uint* _primTable;
    private static byte* _mtxStacks;

    /// <summary>Pass generation: bumped at every RenderSinglePass entry (main view, reflection subframes,
    /// each is its own pass). An owned draw stamps it into BatchService's OwnGen column; the iterator
    /// suppresses a part only when the stamp matches the pass it is walking.</summary>
    internal static int PassGen;
    /// <summary>#125: did the owned world block take THIS pass? The sky reads it for two things -- whether a
    /// <c>RenderSkyBoxes</c> call is the main world pass (a reflection subframe runs the same routine with a
    /// mirrored camera, and capturing there would position next frame's dome at the reflection eye) and
    /// therefore whether its capture window may open at all.</summary>
    internal static bool DrewThisPass => _drawnPass == PassGen;

    // the ownership table (parallel arrays, index = owned slot; _order walks them grouped by VAO)
    private static readonly int[] _oEntry = new int[MaxOwned];      // BatchService part-table slot
    private static readonly IntPtr[] _oTri = new IntPtr[MaxOwned];  // Zero = revoked, harvested at enrollment
    private static readonly IntPtr[] _oNode = new IntPtr[MaxOwned];
    private static readonly IntPtr[] _oMat = new IntPtr[MaxOwned];
    private static readonly IntPtr[] _oVdo = new IntPtr[MaxOwned];
    private static readonly int[] _oItem = new int[MaxOwned];
    private static readonly int[] _oPrim = new int[MaxOwned];
    private static readonly int[] _oVao = new int[MaxOwned];        // slot in the per-VDO VAO cache
    private static readonly int[] _oRotBits = new int[MaxOwned];    // part+0xb0 at enrollment (revalidation)
    private static readonly float[] _oCos = new float[MaxOwned];
    private static readonly float[] _oSin = new float[MaxOwned];
    private static readonly bool[] _oMx = new bool[MaxOwned];       // matrix-class: needs uMVP = model*view*proj
    private static readonly bool[] _oLit = new bool[MaxOwned];      // #83: shade it, don't paint it fullbright
    private static readonly int[] _oTexKey = new int[MaxOwned];     // #84: interned diffuse-texture sort key
    /// <summary>#128: the Gob this part instances off, zero for static geometry. The key the all-owned-or-none
    /// sweep groups by -- never dereferenced here, only compared.</summary>
    private static readonly IntPtr[] _oGob = new IntPtr[MaxOwned];
    /// <summary>#137: this enrolled part's bone slot for THIS pass, or -1 for a rigid one. Rewritten every
    /// frame in <see cref="PoseSkinned"/>, never carried across one -- a pose is per-frame by definition.
    /// </summary>
    private static readonly int[] _oBone = new int[MaxOwned];
    /// <summary>#120: the two PBR vec4s of this part's draw record, staged verbatim -- (Specularity,
    /// Roughness, Metallicness, _) then (CustomSpecularColor.rgb, declared?). Read once at enrollment off the
    /// material's parsed .mtr list; a value of 0 means "not declared" and the shader synthesises it, which is
    /// what the stock shaders do with the same uniforms. The spare w is filled in per frame with the live
    /// slot mask, so index 3 of each pair is deliberately never written here.</summary>
    private static readonly float[] _oParam = new float[MaxOwned * 8];
    /// <summary>#120: which of slots 1..5 this material had a texture in at enrollment (bit s-1). The live
    /// tid is still re-fetched per frame like slot 0's -- this only says which slots are worth asking for.
    /// #123 adds slot 6, the flow map, at bit 5 -- above the five, below <see cref="EnvSlot"/>.</summary>
    private static readonly int[] _oSlots = new int[MaxOwned];
    /// <summary>#123: this part's <see cref="ShaderFeature"/> and the three .mtr parameters that drive it,
    /// staged verbatim into the record's <c>feat</c> vec4. Which three depends on the feature -- sway
    /// speed/strength/tileset-Z for wind, roof offset/clip/interior for the toggles, slide speed for the
    /// flow map. Read once at enrollment: a .mtr does not animate.</summary>
    private static readonly float[] _oFeat = new float[MaxOwned * 4];
    /// <summary>#126: this part's <see cref="OwnedClass"/> -- the pass it draws in. The most significant
    /// radix digit, so each class occupies a contiguous run of <see cref="_order"/> and each pass owns a
    /// contiguous run of batches.</summary>
    private static readonly byte[] _oCls = new byte[MaxOwned];
    /// <summary>The bucket this part was captured in, for the one-owner audit alone: an UNMATCHED part is
    /// one we drew that the engine's iterator never walked, and "which bucket did it come from" is the only
    /// question that separates a suppression gap from geometry the engine simply culled this frame.</summary>
    private static readonly byte[] _oBucket = new byte[MaxOwned];
    /// <summary>#126: the material's Aurora blend pair, translated to GL at bind time. Only the blending
    /// classes read it.</summary>
    private static readonly byte[] _oSrc = new byte[MaxOwned], _oDst = new byte[MaxOwned];
    /// <summary>#126: the part's LOCAL bbox centre (part+0x64/+0x70, the pair
    /// <c>Camera::GetCanKeyholeDissolve</c> and the caster cull both read). Run through the live model matrix
    /// each pass it gives the world-space sort key for the back-to-front blended order; captured at
    /// enrollment because a bbox does not move under a part.</summary>
    private static readonly float[] _oCx = new float[MaxOwned], _oCy = new float[MaxOwned],
                                    _oCz = new float[MaxOwned];
    /// <summary>#152: the part's bbox RADIUS (half the diagonal), the colour LOD's size input. 0 = bbox
    /// degenerate or unread -- such a part is never LOD-culled, unknown bounds are not "small".</summary>
    private static readonly float[] _oR = new float[MaxOwned];
    private static readonly int[] _order = new int[MaxOwned];
    private static readonly int[] _orderTmp = new int[MaxOwned];    // #84: LSD radix intermediate
    /// <summary>#126: the third radix digit's buckets, and the [start, end) range each class occupies in
    /// <see cref="_order"/> afterwards. A pass draws its class's range and nothing else.</summary>
    private static readonly int[] _clsCount = new int[OwnedClasses.Count],
                                  _clsStart = new int[OwnedClasses.Count],
                                  _clsEnd = new int[OwnedClasses.Count];
    private static int _ownCount, _litCount;
    /// <summary>Entries in <see cref="_order"/>: <see cref="_ownCount"/> minus whatever the all-owned-or-none
    /// sweep dropped. The draw walks this, never the slot count.</summary>
    private static int _orderCount;
    /// <summary>#126: blended parts handed back this enrollment because the class could not be owned whole.
    /// Reported: a class that never lands is the one number that separates "no transparent content here"
    /// from "one part keeps refusing and takes the class down with it".</summary>
    private static int _blendedDropped;

    // per-VDO VAO cache: attribute formats harvested in the ENGINE context at enrollment, the 4.6-side VAO
    // built lazily inside the bracket (VAOs don't share across contexts; the buffers they reference do)
    private static readonly IntPtr[] _vVdo = new IntPtr[MaxVdos];
    private static readonly uint[] _vVao46 = new uint[MaxVdos];
    private static readonly uint[] _vElemBuf = new uint[MaxVdos];
    private static readonly uint[] _vPosBuf = new uint[MaxVdos];
    private static readonly uint[] _vUvBuf = new uint[MaxVdos];
    private static readonly int[] _vPosSize = new int[MaxVdos];
    private static readonly int[] _vUvSize = new int[MaxVdos];
    private static readonly uint[] _vPosType = new uint[MaxVdos];
    private static readonly uint[] _vUvType = new uint[MaxVdos];
    private static readonly int[] _vPosStride = new int[MaxVdos];
    private static readonly int[] _vUvStride = new int[MaxVdos];
    private static readonly long[] _vPosOff = new long[MaxVdos];
    private static readonly long[] _vUvOff = new long[MaxVdos];
    private static readonly uint[] _vNrmBuf = new uint[MaxVdos];
    private static readonly int[] _vNrmSize = new int[MaxVdos];
    private static readonly uint[] _vNrmType = new uint[MaxVdos];
    private static readonly int[] _vNrmStride = new int[MaxVdos];
    private static readonly long[] _vNrmOff = new long[MaxVdos];
    private static readonly bool[] _vLit = new bool[MaxVdos];       // #83: has a usable 3-float normal stream
    /// <summary>The VDO carries the BONE streams, i.e. it belongs to a true skinmesh (a cloak, a robe, a
    /// tail). #128 refused such a part outright -- no skinning branch existed, so drawing one meant a BIND
    /// pose at the node matrix, a misrender rather than a fallback. #137 gave the owned vertex stages that
    /// branch, so the flag is now what SELECTS it: bone attributes bound in the owned VAO, a per-frame pose
    /// forced out of the engine, a bone slot in the record. Both streams or neither -- a half-skinned VAO
    /// would skin some vertices and leave the rest at the origin.</summary>
    private static readonly bool[] _vSkin = new bool[MaxVdos];
    /// <summary>#137: the two bone streams' formats, harvested from the same engine VAO as position and UV.
    /// Normalisation is carried (the weights ship as normalised shorts on some content) because
    /// <c>VertexAttribPointer</c> silently means something else without it.</summary>
    private static readonly uint[] _vBiBuf = new uint[MaxVdos], _vBwBuf = new uint[MaxVdos];
    private static readonly int[] _vBiSize = new int[MaxVdos], _vBwSize = new int[MaxVdos];
    private static readonly uint[] _vBiType = new uint[MaxVdos], _vBwType = new uint[MaxVdos];
    private static readonly int[] _vBiStride = new int[MaxVdos], _vBwStride = new int[MaxVdos];
    private static readonly long[] _vBiOff = new long[MaxVdos], _vBwOff = new long[MaxVdos];
    private static readonly byte[] _vBiNorm = new byte[MaxVdos], _vBwNorm = new byte[MaxVdos];
    private static readonly bool[] _vStale = new bool[MaxVdos];     // slot recycled: rebuild the 4.6 VAO
    private static int _vdoCount;
    /// <summary>Counting-sort buckets. The draw order groups by (lit, VDO) so both the VAO and the PROGRAM
    /// bind once per group -- lit slots are the upper half.</summary>
    private static readonly int[] _sortCount = new int[MaxVdos * 2];
    /// <summary>#84 second radix digit: counting-sort buckets over the interned texture key. Sorting by it
    /// FIRST and then stably by (lit, VDO) leaves the order grouped by (lit, VDO) with equal textures
    /// contiguous inside each group -- i.e. exactly the batch key, in one pass over the table.</summary>
    private static readonly int[] _texSortCount = new int[MaxMats];
    private static readonly IntPtr[] _mKey = new IntPtr[MatHash];
    private static readonly int[] _mVal = new int[MatHash];
    private static int _matCount;

    // #84 multidraw submission: one entry per DRAW in _dCount/_dOff/_dBase + one staged SSBO record per draw,
    // sliced into batches by the _g* table. All static -- the hot path stages into them and allocates nothing.
    private static readonly int[] _dCount = new int[MaxOwned];
    private static readonly nint[] _dOff = new nint[MaxOwned];
    private static readonly int[] _dBase = new int[MaxOwned];
    private static readonly float[] _dData = new float[MaxOwned * DrawFloats];
    /// <summary>#153: per-draw bindless handles, 8 per record (diffuse + material slots 1..7), parallel to
    /// <see cref="_dData"/>. 0 = slot empty; the record's slot mask already forbids sampling those.</summary>
    private static readonly ulong[] _dTexH = new ulong[MaxOwned * 8];
    /// <summary>#153: tid -> resident bindless handle, open-addressed, WIPED every enrollment -- the same
    /// cadence at which tids themselves are re-harvested. A CAurTexture is borrowed and its GL id can be
    /// recycled by the engine; the wipe bounds how long a handle can outlive the texture it named to one
    /// enrollment period, the exact lifetime discipline the tid tables already live under.</summary>
    private const int HandleHashBits = 15;
    private static readonly int[] _hTid = new int[1 << HandleHashBits];
    private static readonly ulong[] _hVal = new ulong[1 << HandleHashBits];
    /// <summary>#153: is the bindless path live? Extension present, entry points resolved, not vetoed by
    /// SOWX_BINDLESS=0, and the LIT program's actual sources carry the handle block. When false everything
    /// draws exactly as before -- per-batch texture binds.</summary>
    private static bool _bindless;
    private static bool _bindlessProbed;
    /// <summary>#126: this pass's draw order, rebuilt every pass from <see cref="_order"/> -- the opaque set
    /// first, then the blended members sorted back-to-front, then additive. It is a separate array from
    /// _order because the membership is per FRAME (a fading tile joins the blended run for the frames it
    /// fades) and the blended order is per PASS (it depends on the camera).</summary>
    private static readonly int[] _pass = new int[MaxOwned];
    /// <summary>...and each entry's class + alpha multiplier for this pass, since neither is
    /// <c>_oCls[i]</c> alone once the fade overlay applies.</summary>
    private static readonly byte[] _passCls = new byte[MaxOwned];
    private static readonly float[] _passAlpha = new float[MaxOwned];
    /// <summary>Blended candidates of this pass and their squared distance from the eye, before the sort.</summary>
    private static readonly int[] _blend = new int[MaxOwned];
    private static readonly float[] _blendKey = new float[MaxOwned];
    private static readonly float[] _blendAlpha = new float[MaxOwned];
    private static readonly int[] _gStart = new int[MaxOwned];
    private static readonly int[] _gLen = new int[MaxOwned];
    private static readonly int[] _gVao = new int[MaxOwned];
    private static readonly int[] _gTex = new int[MaxOwned];
    /// <summary>#120: the live tids of slots 1..5 for this batch (0 = nothing bound there). Part of the batch
    /// KEY, not just of the bind set -- a batch that grouped on the diffuse alone would render every member
    /// under the first member's normal and specular maps.</summary>
    private static readonly int[] _gTexN = new int[MaxOwned * MapSlots];
    private static readonly uint[] _gMode = new uint[MaxOwned];
    private static readonly bool[] _gLit = new bool[MaxOwned];
    /// <summary>#123: the batch's <see cref="ShaderFeature"/>. Read by the depth prepass, which skips a batch
    /// whose feature displaces vertices.</summary>
    private static readonly int[] _gFeat = new int[MaxOwned];
    /// <summary>#124: does this batch camera-dissolve? A key term for one reason only -- the keyhole
    /// fragment stage <c>discard</c>s, and the depth prepass has no per-draw granularity, so a dissolving
    /// batch has to be droppable whole. Prepass depth for a wall the colour pass punches through would cull
    /// the room behind it.</summary>
    private static readonly bool[] _gDis = new bool[MaxOwned];
    /// <summary>#126: the batch's class and blend pair. In the key because they ARE the GL state a multidraw
    /// cannot vary inside: depth mask, blend function and the alpha-discard threshold.</summary>
    private static readonly byte[] _gCls = new byte[MaxOwned];
    private static readonly byte[] _gSrc = new byte[MaxOwned], _gDst = new byte[MaxOwned];
    /// <summary>#126: where the opaque set ends and where the blended run ends, in batch indices. Each pass
    /// draws exactly its own range -- the depth prepass and the probe capture take the opaque prefix.</summary>
    private static int _gOpaqueEnd, _gBlendEnd, _gWaterEnd;
    private static int _drawN;
    // #125: the sky's own draw list, staged after the batch records and issued one draw at a time -- a
    // skybox is a handful of parts and each carries its own captured matrix, so there is nothing to group.
    private const int MaxSkyDraws = 1024;
    private static readonly int[] _skVao = new int[MaxSkyDraws], _skTex = new int[MaxSkyDraws];
    private static readonly uint[] _skMode = new uint[MaxSkyDraws];
    private static readonly int[] _skSrc = new int[MaxSkyDraws], _skDst = new int[MaxSkyDraws];
    private static int _skyFirst, _skyN;
    private static uint _drawBuf;
    /// <summary>#153: the per-draw texture-handle SSBO (binding <see cref="BindTexH"/>).</summary>
    private static uint _texHBuf;
    /// <summary>#137's bone block and the CPU stage behind it. One region, rewritten every colour pass.</summary>
    private static uint _boneBuf;
    private static readonly float[] _boneStage = new float[MaxBoneSlots * BoneFloats];
    private static int _boneSlots;
    /// <summary><c>GLRender::m_bones</c>: where the engine leaves a solved pose. Read only immediately after
    /// <see cref="Perf.BatchService.ForceSkinPose"/> returns true -- it is ONE array shared by every skinned
    /// draw in the frame, so it holds the last part posed and nothing else.</summary>
    private static float* _bones;
    /// <summary>#137 armed: the bone array resolved and <c>SOWX_OWNSKIN</c> not 0. False leaves the skinmesh
    /// class exactly where #128 left it -- refused at enrollment, drawn by the engine.</summary>
    private static bool _skinning;
    private static long _skinDrawn, _skinNoPose, _skinSlotOverflow;

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
    /// <summary>SOWX_DEPTHPREPASS=0: skip the prepass, which is the A/B for its own acceptance number.</summary>
    private static bool _depthPrepass = true;
    /// <summary>Every owned vertex stage that ran carries <see cref="ClipExpr"/> under an invariant
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
    /// <c>ShadowInputs.CameraPos</c> -- that belongs to whichever pass set a view last -- and taken per pass
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
    /// <summary>SOWX_LIGHTVIZ. 1..3 and 6 attribute a dim (or a too-shiny) frame to a lighting TERM instead
    /// of guessing from the composite; 5 is the local-cube term alone; 4 is the coverage map -- owned draws
    /// paint flat (cyan lit / magenta fullbright) and everything still textured is geometry the engine's own
    /// GL 3.3 walk drew, which no other signal makes visible. Each mode is documented where it branches, in
    /// <see cref="OwnedLitFsMain"/>. 7 is the shading normal, which is the only thing that separates a
    /// low-poly TERMINATOR (a wrap dial fixes it) from a discontinuous MESH normal (nothing in the BRDF
    /// can). 8 is #146's occlusion factor -- the AO map times the height term -- which is white everywhere
    /// until some material in sight declares one.
    /// <para>#129 adds the water composite's own terms, which live in <see cref="WaterPass"/>'s stage rather
    /// than the lit one and only paint where owned water draws: 9 what is seen THROUGH the surface, 10 the
    /// reflection, 11 fresnel as grey, 12 the framebuffer copy's colour exactly as sampled, 13 the copy's
    /// linearised depth, 14 the screen UV those two sample at, 15 the depth copy's raw texel. 12 and 13 say
    /// whether the one copy is landing; 14 and 15 say which half is at fault when they read flat; 16 and 17 are
    /// the Beer-Lambert clarity and the thickness behind it, which is where a landed copy still produces a
    /// flat surface.</para></summary>
    /// <summary>Highest mode <see cref="_lightViz"/> accepts. Raise it when a stage adds one, or the new
    /// mode is silently discarded at the parse.</summary>
    private const int LightVizMax = 21;
    private static int _lightViz;
    /// <summary>SOWX_AOTEST=1: bind every lit part's own DIFFUSE texture on the AO slot and set the bit, so
    /// the whole #146 path -- mask, unit, sampler, ambient multiply -- runs end to end on stock content. The
    /// frame looks WRONG on purpose (the diffuse's red channel is not an occlusion map); what it proves is
    /// that a material which does declare one will be sampled. No shipped content uses texture7 yet, so
    /// without this there is nothing to point the feature at.</summary>
    private static bool _aoTest;
    private static int _locViz = -1;
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
    /// <summary>SOWX_OWNLIGHT=0 forces every owned part back to the #82 fullbright variant (the lighting A/B).</summary>
    private static bool _lighting = true;
    /// <summary>RenderInterface::IsEnabled -- queried for cap 5 (LIGHTING) once per owned pass.</summary>
    private static delegate* unmanaged<int, byte> _isEnabled;
    /// <summary>The AuroraEnableEnums value for lighting. <c>MatStatic</c>/<c>RenderFlat</c> both branch on
    /// it, so a pass that runs with it off wants the flat variant for everything.</summary>
    private const int CapLighting = 5;

    // lifecycle
    private static int _sinceEnroll = EnrollEvery, _tableGen = -1;
    private static bool _wasInModule;

    // period counters (the owned-draw log counter, counterpart to M0's owned-cast)
    private static long _draws, _skips, _fallbacks, _unmatched;
    /// <summary>Why an OWNED part still did not draw this pass, by term. The single fallback count could not
    /// separate a camera-dependent bail-out (keyhole, tile fade) from a content one, which is exactly the
    /// distinction a part that is owned head-on and vanilla at the screen edge turns on.</summary>
    private static long _skipUnseen;
    private static long _skipNoLight, _skipIdentity, _skipNoMdl, _skipTex,
                        _skipRange, _skipVao;
    /// <summary>#128: owned parts handed back this pass because their INSTANCE state went unownable -- a
    /// highlight, a scene tint, a per-gob env override. Transient by nature, so it is a skip rather than a
    /// revocation, and a skip that is not counted reads as geometry vanishing.</summary>
    private static long _skipGob;
    /// <summary>#124's coverage number: owned parts the camera dissolves this pass. Before the fold these
    /// were the keyhole SKIP count -- same population, handed back to the engine; now they are drawn by the
    /// owned program with the dissolve folded in, so the before/after the ticket asks for is this one figure
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
    /// <summary>#134: how much of the SOWX_DUMPTEX population we enrolled, and how much of it we actually
    /// submit each frame. The gap between them, and between both and the engine's own draw count, is the
    /// only honest answer to "is that thing on screen ours".</summary>
    private static int _watchEnrolled;
    private static long _watchSubmitted;

    /// <summary>#134: one line naming what the owned draw loop did with the watched part. Always returns
    /// true, so it reads as the reason at a skip site: <c>{ _skipTex++; if (W(i, "tex")) continue; }</c>.</summary>
    private static bool W(int i, string why)
    {
        if (Perf.BatchService.WatchedEntry(_oEntry[i]) && _watchDrawLeft-- > 0)
            _log?.Info($"[dumpframe] DRAW {why} (slot {i}, capture bucket {_oBucket[i]}, cls {(OwnedClass)_oCls[i]})");
        return true;
    }
    private static readonly long[] _unmatchedCls = new long[OwnedClasses.Count];
    /// <summary>#126's counterpart to it: owned parts the tile fade moves into the blended run this pass.
    /// Before the fold these were the tilefade SKIP count -- same population, handed back to the engine.</summary>
    private static long _ownFade;
    /// <summary>#126: owned draws per class this pass, and the batches each class's pass submitted. The
    /// cutout/blended split is the whole model, so it is reported rather than inferred.</summary>
    private static readonly long[] _drawsByClass = new long[OwnedClasses.Count];
    /// <summary>#84's measurable form of "draw count decoupled from part count": submitted multidraw batches.
    /// draws/batches IS the batching ratio the acceptance criterion asks for, straight out of the log.</summary>
    private static long _batches;
    private static int _frames, _revokes, _enrolls, _lastEnrolled, _lastCandidates, _enrollDiagLeft = EnrollDiagBudget;
    /// <summary>Passes an empty enrollment may retry back-to-back after a resume before falling back to the
    /// normal cadence (~2s at 60fps): enough for the re-record to complete, bounded so a genuinely unownable
    /// area cannot spin the scan every pass forever.</summary>
    private const int ResumeRetries = 120, EnrollDiagBudget = 8;
    private static int _resumeRetries;
    /// <summary>Distinct diffuse textures interned at the last enrollment -- the batch floor (see EndFrame).</summary>
    private static int _lastTextures;
    private static bool _reportedHit;
    /// <summary>Lit parts refused at enrollment (no normal stream / lit program down) -- they stay vanilla.</summary>
    private static int _litUnownable;
    /// <summary>#128: parts refused for carrying bone streams, and what the per-instance sweep then cost --
    /// already-accepted parts revoked because a SIBLING of theirs on the same instance was refused. Together
    /// with <c>BatchService.RefusedGobs</c> on the same line they are the honest answer to "why is that
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
    /// samples/screen-pixel is the overdraw figure, and its move with SOWX_DEPTHPREPASS is what the prepass
    /// is worth -- both are period averages on the [owned] line.</summary>
    /// <summary>...and the shading pass's own GPU time. Frame time cannot answer "is the prepass worth it"
    /// on a vsync-capped display -- both modes sit on the cap and the saving becomes headroom -- so the A/B
    /// reads the work itself. Independent of SAMPLES_PASSED: different query target, both may be active.</summary>
    private static GpuQuery _qPrepass, _qSamples, _qColour;
    /// <summary>#151: the passes the colour timer never covered -- particles, water (copy + draw), sky+grass,
    /// and the post chain (bloom + composite). Their sum with colour/prepass/shadow is the whole owned GPU
    /// frame; until these existed the variance the user chased lived in the unmeasured slice.</summary>
    private static GpuQuery _qParticles, _qWater, _qSkyGrass, _qPost;
    /// <summary>#152 colour LOD: a part whose bbox projects to fewer PIXELS than this is skipped -- and
    /// STAMPED, so the engine does not draw it either: sub-pixel geometry is nobody's to render. The classic
    /// LOD trade NWN never had; a whole-area camera is full of parts a fraction of a pixel tall.
    /// <c>SOWX_LOD_PX</c> overrides (0 disables); raise toward 2-3 for aggressive distance thinning.</summary>
    private static float _lodPx = 1.0f;
    /// <summary>Projection scale for the LOD test: <c>proj.M22 * viewportH / 2</c> -- world radius over
    /// distance times this = radius in pixels. Captured per pass beside the eye.</summary>
    private static float _lodScale;
    private static long _lodSkipped;

    /// <summary>One-shot FBO-topology report (#151, the "does AA even apply to us" question).</summary>
    private static int _fboDiagSig = -1, _fboDiagLeft = 8;

    /// <summary>#153: resident bindless handle for a live tid, cached until the next enrollment wipe.
    /// 0 in, 0 out. Called only inside the 4.6 bracket (handles belong to the share group).</summary>
    private static ulong HandleFor(int tid)
    {
        if (tid == 0) return 0;
        int mask = (1 << HandleHashBits) - 1;
        int h = (int)((uint)tid * 0x9E3779B9u >> (32 - HandleHashBits)) & mask;
        while (true)
        {
            int k = _hTid[h];
            if (k == tid) return _hVal[h];
            if (k == 0)
            {
                ulong v = Gl.GetTextureHandleARB((uint)tid);
                if (v != 0) Gl.MakeTextureHandleResidentARB(v);
                _hTid[h] = tid; _hVal[h] = v;
                return v;
            }
            h = (h + 1) & mask;
        }
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

    /// <summary>Sample count of an existing FBO attachment, by its reported object type; 1 = single-sampled
    /// (or unqueryable, which for this diagnostic reads the same).</summary>
    private static int AttachmentSamples(int type, int name)
    {
        int n = 0;
        if (type == (int)Gl.RENDERBUFFER && Gl.GetRenderbufferParameteriv != null && name != 0)
        {
            int prev; Gl.GetIntegerv(Gl.RENDERBUFFER_BINDING, &prev);
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, (uint)name);
            Gl.GetRenderbufferParameteriv(Gl.RENDERBUFFER, Gl.RENDERBUFFER_SAMPLES, &n);
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, (uint)prev);
        }
        else if (type == (int)Gl.ATTACHMENT_TEXTURE && Gl.GetTexLevelParameteriv != null && name != 0)
        {
            // asking TEXTURE_2D_MULTISAMPLE about a plain TEXTURE_2D raises GL_INVALID_OPERATION and writes
            // nothing, which leaves n = 0 -> reported as 1x. The error is harmless and read by nobody.
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D_MULTISAMPLE, 0, Gl.TEXTURE_SAMPLES, &n);
        }
        return n <= 0 ? 1 : n;
    }

    private static string TypeName(int type)
        => type == (int)Gl.RENDERBUFFER ? "renderbuffer" : type == (int)Gl.ATTACHMENT_TEXTURE ? "texture" : $"type 0x{type:x}";
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
        // #129: ahead of the checks, because SelfCheck asserts that the ownable-bucket list AGREES with
        // whether owned water can draw -- and SOWX_WATER=0 is exactly the case where it must.
        WaterPass.Attach(log);
        WaterBodies.Attach(log);
        // #117: ahead of every stand-down -- a broken pass order is a misrender in BOTH bracket sites, so the
        // one service that happens to host the check must not be able to opt the check out.
        if (!FrameGraph.SelfCheck(log)) return;
        // #126: log-only. A wrong classification is a wrong-looking frame, not a wrong-drawing one -- the
        // one-owner invariant is upheld by the bucket list, which SelfCheck below asserts against.
        OwnedClasses.SelfCheck(log);
        SkyBox.SelfCheck(log);
        GrassField.SelfCheck(log);
        Beams.SelfCheck(log);
        Particles.SelfCheck(log);
        if (Environment.GetEnvironmentVariable("SOWX_OWNDRAW") == "0" || UiHostService.Locked)
        { log.Info("OwnedDrawService: stood down (SOWX_OWNDRAW=0 or SOWX_LOCKCHAT)"); return; }
        if (!SelfCheck(log)) return;   // never arm on a broken contract: the misrender it predicts must not ship
        // #128: same rule. A wrong gob offset draws a creature at the wrong alpha and a leaky refused-gob set
        // half-owns one -- both misrenders, neither of which errors.
        if (!Perf.BatchService.GobSelfCheck(log)) return;
        OwnedLights.SelfCheck(log);    // /light knob table (off-device; advisory, a bad dial is not a misrender)
        PostChain.SelfCheck(log);      // #139 /post knob table + the shoulder's identity-below-the-knee property
        // #129: the one-copy contract and the probe fallback, both as text checks on the composed stage --
        // a stage that lost either would still compile, and the failure only shows as a wrong-looking sea.
        WaterPass.SelfCheck(log);
        WaterBodies.SelfCheck(log);

        _getTexture = (delegate* unmanaged<IntPtr, int, long>)Native.Sym(SymbolMap.MatGetTexture);
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _mtxStacks = (byte*)Native.Sym(SymbolMap.MAurMtxStack);
        _runtime = (byte*)Native.Sym(SymbolMap.GlShaderRuntimeData);
        if (_runtime == null)
            log.Info("OwnedDrawService: GLRender::m_shaderExposedRuntimeData unresolved -- #123's feature " +
                     "switches stay inert (no wind sway, roofs where authored), and #124's camera dissolve " +
                     "with them -- uKeyholeCam reads this struct too");
        _fog = (float*)Native.Sym(SymbolMap.GlFog);
        _shaderConstF = (float*)Native.Sym(SymbolMap.GlGlobalShaderConstantsF);
        // #124 degrades one term at a time and says which: no fog block leaves owned parts unfogged (the
        // pre-#124 look), no constants leaves the dissolve aperture at zero, i.e. no dissolve. Neither is a
        // misrender, and neither is worth standing the pass down for.
        if (_fog == null)
            log.Info("OwnedDrawService: GLRender::m_fog unresolved -- #124's scene fog stays off on owned parts");
        // The dissolve needs BOTH the diameter pair and the runtime struct's camera terms: without the
        // latter uKeyholeCam stays (0,0), the fragment offset never goes negative, and the aperture never
        // opens. Naming only one of them would send the next reader to the wrong symbol.
        if (_shaderConstF == null || _runtime == null)
            log.Info("OwnedDrawService: " +
                     (_shaderConstF == null ? "GLRender::m_globalShaderConstantsf" : "GLRender::m_shaderExposedRuntimeData") +
                     " unresolved -- #124's camera dissolve stays off on owned parts");
        if (_getTexture == null || !AurTex.Ready || _primTable == null || _mtxStacks == null)
        { log.Info("OwnedDrawService: engine symbols unresolved -- owned world pass unavailable"); return; }

        _origRsp = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.RenderSinglePass, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnRenderSinglePass);
        // #132: the frame anchor. After-original, so the pass runs with the world FBO bound and cleared, the
        // scene fog and the door/tile fades already written for this frame, and nothing drawn yet.
        _origFade = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.FadeDoors, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnFadeDoors);
        _repackSlot = hooks.Chain(SymbolMap.VdoRepack,   // shared symbol: batcher + owned-cast invalidate too (#88)
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnVdoRepackHook, RepackPriority);
        _lighting = Environment.GetEnvironmentVariable("SOWX_OWNLIGHT") != "0";
        _depthPrepass = Environment.GetEnvironmentVariable("SOWX_DEPTHPREPASS") != "0";
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_LOD_PX"), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float lp) && lp >= 0f) _lodPx = lp;
        // The ceiling is a REAL gate, not a formality: a value past it leaves _lightViz at 0 and the mode
        // does nothing at all, with no line to say so. It has to move with every mode added -- #129's water
        // views (9..13) were added without it and read as "LIGHTVIZ does not work".
        if (int.TryParse(Environment.GetEnvironmentVariable("SOWX_LIGHTVIZ"), out int viz)
            && viz >= 0 && viz <= LightVizMax)
            _lightViz = viz;
        else if (Environment.GetEnvironmentVariable("SOWX_LIGHTVIZ") != null)
            log.Info($"OwnedDrawService: SOWX_LIGHTVIZ must be 0..{LightVizMax} -- ignored, no viz mode is on");
        _aoTest = Environment.GetEnvironmentVariable("SOWX_AOTEST") == "1";
        _sunWorldBias = CasterPass.EnvFloat("SOWX_CSM_BIAS", _sunWorldBias);
        // #137: the skinmesh class. Two independent things must hold -- the engine's bone array must resolve
        // (nothing to copy without it) and the class must have a capture hook, which BatchService reports.
        _skinning = Environment.GetEnvironmentVariable("SOWX_OWNSKIN") != "0";
        if (_skinning)
        {
            _bones = (float*)Native.Sym(SymbolMap.GlRenderBones);
            if (_bones == null)
            { _skinning = false; log.Info("OwnedDrawService: GLRender::m_bones unresolved -- skinmeshes stay engine-drawn"); }
        }
        _isEnabled = (delegate* unmanaged<int, byte>)Native.Sym(SymbolMap.RiIsEnabled);
        OwnedLights.Attach(log);
        EnvProbe.Attach(log);
        PostChain.Attach(log);
        GrassField.Install(log, hooks);   // #127: owns Scene::DoBinBucket; stands down on its own if it cannot
        Beams.Install(log, hooks);        // #130: owns Scene::RenderBeams (bucket 15); same stand-down rule
        Particles.Install(log, hooks);    // #131: owns PartEmitter::RenderEmitter; declines per emitter
        _armed = _origRsp != null && _origFade != null;
        if (_armed && !_lighting) log.Info("OwnedDrawService: SOWX_OWNLIGHT=0 -- owned parts stay fullbright (#82 behaviour)");
        if (_armed && !_depthPrepass) log.Info("OwnedDrawService: SOWX_DEPTHPREPASS=0 -- no depth prepass, the colour pass shades every fragment");
        if (_armed) log.Info(_skinning
            ? "OwnedDrawService: #137 skinmeshes owned -- posed per frame off GLRender::m_bones (SOWX_OWNSKIN=0 disables)"
            : "OwnedDrawService: skinmeshes stay engine-drawn (cloaks, robes, tails)");
        log.Info(_armed
            ? "OwnedDrawService: armed -- owned world pass at the #132 frame anchor, Scene::FadeDoors (SOWX_OWNDRAW=0 disables)"
            : $"OwnedDrawService: {(_origRsp == null ? "RenderSinglePass" : "Scene::FadeDoors")} hook failed -- owned world pass unavailable");
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
        if (world && _armed && RenderMode.Owned && GlContextService.Available && UiHost.InModule)
        {
            // saturating, never wrapping: an unchecked ++ past int.MaxValue lands on int.MinValue and the
            // threshold is then unreachable for 2^31 passes -- i.e. enrollment silently never runs
            if (_sinceEnroll < EnrollEvery) _sinceEnroll++; else Enroll(scene);
        }
        // Only a colour RenderSinglePass OF THE WORLD SCENE may draw owned parts. RenderDrawBucket's other
        // caller is Scene::RenderDynamicGeometry (buckets 2,3; objdump: those two callers and no more), and
        // drawing from there stamps a part "drawn" outside the colour walk, which then suppresses parts
        // nobody drew. A GUI 3D panel is the same hazard one level up: same virtuals, different Scene (#91).
        bool prev = _inColourPass;
        _inColourPass = world;
        if (_origRsp != null) _origRsp(scene);
        _inColourPass = prev;
        if (!prev) AuditPass();
    }

    /// <summary>Is this the world Scene? A Scene's static tile BSP (<see cref="SymbolMap.SceneBspRoot"/>) is
    /// written by <c>Scene::InitBSP</c> alone (@0x46c330; <c>Scene::Scene</c> zeroes it, and every
    /// <c>SceneBSPAdd*</c> only inserts into an existing root). A GUI 3D panel's Scene -- which reaches the
    /// same <c>Scene::RenderSinglePass</c> through <c>Camera::RenderScene</c>, off <c>Camera+0x90</c> -- is
    /// built by <c>CGuiPanel::CreateSceneView</c> @0x68759e: <c>NewCAurScene("sceneGUI_&lt;panel&gt;")</c> +
    /// camera + SetAmbient, no InitBSP, so its root stays null. This is also the engine's own test --
    /// <c>ManageSceneBSP</c> @0x47fa80 early-outs on exactly it.</summary>
    internal static bool IsWorldScene(IntPtr scene) =>
        scene != IntPtr.Zero && *(IntPtr*)(scene + SymbolMap.SceneBspRoot) != IntPtr.Zero;

    private static bool _inColourPass;
    /// <summary>Scene the ownership table was enrolled from. The owned block draws only into a bucket walk of
    /// THIS Scene, which is what makes one-owner true by construction rather than by the colour-pass gate
    /// alone. An area transition that replaces the Scene leaves it stale, so nothing draws until the next
    /// enrollment picks up the new one -- bounded by <see cref="EnrollEvery"/>.</summary>
    private static IntPtr _enrollScene;
    /// <summary>Bucket 0/1 walks of a Scene that is not <see cref="_enrollScene"/> -- the #91 sighting counter.
    /// Non-zero means GUI 3D panels really do walk the opaque buckets and the gate is earning its keep.</summary>
    private static int _foreignBuckets;
    private static int _drawnPass = -1;   // pass whose owned block already ran (once per pass)

    /// <summary>Ownable parts the last enrollment turned away for want of a slot. Non-zero means the area is
    /// past <see cref="MaxOwned"/>, which is not just "some tiles stay vanilla": the spill set is decided by
    /// hash-table walk order, so it re-picks itself every enrollment and tiles visibly change renderer under
    /// a static camera. It is the first number to read when the world looks spotty.</summary>
    private static int _capacitySpill;

    /// <summary>The capacity clause of the [owned] line -- silent when there is room, loud when there is not,
    /// because "4082/5631" only reads as a bug if you happen to know what MaxOwned is.</summary>
    private static string CapacityNote()
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
            if (Perf.BatchService.OwnGen[e] == PassGen && Perf.BatchService.OwnSupGen[e] != PassGen)
            {
                _unmatched++; _unmatchedBucket[_oBucket[i]]++; _unmatchedCls[_oCls[i]]++;
                if (!Perf.BatchService.SightedThisFrame(e)) _unmatchedUnseen++;
            }
        }
    }

    /// <summary>#132's frame anchor: the owned colour block, run from an after-original hook on
    /// <c>Scene::FadeDoors</c> -- the last engine call inside <c>Scene::RenderSinglePass</c> before
    /// <c>RenderDrawBucket(0)</c>. Every owned part -- #126's transparent, fade, cap and additive classes
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
        // part whose OwnGen matches this pass, and nothing stamps it when the owned block does not run.
        if (!_armed || !RenderMode.Owned || _ownCount == 0) return;
        // #91: never paint into a foreign Scene. Only a NON-world pass counts as a foreign sighting -- a world
        // pass that misses is the enrolled Scene having been replaced (area transition), which resolves itself
        // within one EnrollEvery and must not read as evidence that GUI panels reach this anchor.
        if (scene != _enrollScene) { if (!_inColourPass) _foreignBuckets++; return; }
        if (!_inColourPass) return;
        if (_drawnPass == PassGen) return;
        if (!GlContextService.Available || !UiHost.InModule) return;
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

    /// <summary>#137: solve every owned skinmesh's pose for THIS frame, in engine context, before the
    /// bracket -- and since #132 after <see cref="PostChain.Precheck"/>, so a pass that is standing down
    /// does not solve poses the engine is about to solve again itself.
    /// <para>The timing is the whole point, and it is why this exists at all rather than the pass simply
    /// reading <c>m_bones</c>. The owned pass draws at bucket 0 or 1, before the engine has walked the
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
            if (!Perf.BatchService.ForceSkinPose(_oTri[i])) { _skinNoPose++; RevokeSkinned(i); continue; }
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
    /// past the bone budget draws bodies through the owned pass and their cloaks through the engine, and
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

    /// <summary>#86 frame-edge handler. Resuming enrolls from a table BatchService just wiped, so the first
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

    /// <summary>Iterator sighting from BatchService's GetNextItem hook: an owned part just came up in the
    /// engine's bucket walk. True = skip it (we drew it this pass and it sits in buckets 0/1); false = let
    /// the engine draw it, which is what a mid-frame revocation or a skipped owned draw falls back to.</summary>
    internal static bool IterSight(int e, int bucket)
    {
        // #126: the bucket list is ONE list with the capture gate's (BatchService.OwnableBucket) -- 0/1 plus
        // the three residual static classes 4/7/8, and since #134 the dynamic gob pair 2/3. Suppressing a
        // bucket the owned pass does not draw drops geometry; drawing one it does not suppress double-draws
        // it.
        // #129: ...and bucket 6 answers to one thing more. Every other class is staged and drawn in the same
        // breath, so OwnGen implies "drawn"; water is staged in the preamble and drawn only if the ONE
        // framebuffer copy could be taken, which is not knowable until the copy is attempted. A pass that
        // stood the copy down has stamped water parts it never drew, and suppressing those is a hole in the
        // sea rather than a fallback.
        if (bucket == WaterBucket && !_waterDrew) { _fallbacks++; return false; }
        if (Perf.BatchService.OwnGen[e] != PassGen || !Perf.BatchService.OwnableBucket(bucket))
        { _fallbacks++; return false; }
        Perf.BatchService.OwnSupGen[e] = PassGen;
        _skips++;
        return true;
    }

    /// <summary>Chain priority on Repack: invalidation order among observers doesn't matter.</summary>
    private const int RepackPriority = 30;
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

    /// <summary>Frame tick from the present (after the bucket walks). Module unload, a BatchService table
    /// wipe and a lost 4.6 context all invalidate every recorded identity wholesale.</summary>
    public static void EndFrame()
    {
        if (!_armed) return;
        _frameSeq++;
        // Disarming must never be silent: the [owned] period line is the only evidence this pass runs at
        // all, so its ABSENCE has to name a reason rather than read as "no news". RevokeAll only speaks when
        // something was owned, which is exactly not the case when the program failed before the first enroll.
        if (_progFailed || _batchUnavailable)
        {
            RevokeAll(_progFailed ? "program failed" : "multidraw unavailable");
            _armed = false;
            _log?.Info("OwnedDrawService: DISARMED (" + (_progFailed
                           ? "program build failed"
                           : "glMultiDrawElementsBaseVertex or glBindBufferBase missing") +
                       ") -- the owned world pass is off for this session and no [owned] lines will follow");
            return;
        }
        if (!GlContextService.Available)
        { RevokeAll("4.6 context lost"); OwnedLights.Forget(); EnvProbe.Forget(); PostChain.Forget();
          WaterPass.Forget(); _progWater = 0; _waterFailed = false;
          DepthResource.Revoke(); SceneDepth.Forget(); Particles.ContextLost(); }
        bool inModule = UiHost.InModule;
        if (_wasInModule && !inModule) RevokeAll("module unload");
        _wasInModule = inModule;
        if (_tableGen != Perf.BatchService.TableGen) { RevokeAll("part table wiped"); _tableGen = Perf.BatchService.TableGen; }

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
        _log?.Info($"OwnedDrawService: [owned] {_lastEnrolled}/{_lastCandidates} parts owned{CapacityNote()} on {_vdoCount} VDOs " +
                   $"/ {_lastTextures} textures " +
                   $"({_litCount} lit, {_litUnownable} lit left to the engine" +
                   // #128: what the creature class costs, split. "Creatures are still vanilla" has three
                   // different fixes behind it -- a skinning stage, an instance-state term, or nothing at all
                   // if the sweep is simply doing its job -- and only the split says which.
                   (_skinUnownable == 0 && _gobParts == 0 ? "" :
                       $", {_skinUnownable} skinned parts left to the engine, {_gobParts} accepted parts revoked " +
                       $"by the per-instance sweep (out of {Perf.BatchService.RefusedGobs} instances with a refused part)" +
                       (Perf.BatchService.RefusedGobOverflow ? " -- REFUSED-GOB SET OVERFLOWED, every instance dropped" : "")) +
                   // #137: and what the skinmesh class itself did. A zero everywhere with the class armed
                   // means no cloak was on screen, which is a different statement from "the pose failed" --
                   // so the failures are named, not merely absent.
                   (!_skinning ? ", skinmeshes not owned"
                      : _skinDrawn == 0 && _skinNoPose == 0 && _skinSlotOverflow == 0 ? ""
                      : $", {(float)_skinDrawn / _frames:F1} skinmeshes posed/frame" +
                        (_skinNoPose == 0 ? "" : $", {_skinNoPose} handed back unposed") +
                        (_skinSlotOverflow == 0 ? "" : $", {_skinSlotOverflow} over the {MaxBoneSlots}-slot bone budget")) +
                   "), " +
                   $"owned draws {(float)_draws / _frames:F0}/frame in {(float)_batches / _frames:F0} batches " +
                   // #126: the transparency model, as numbers. Cutout should dwarf blended on NWN content;
                   // a blended count that reads 0 while the scene plainly has transparents means the class
                   // was dropped whole rather than never classified -- which the next term names.
                   $"({(float)_drawsByClass[1] / _frames:F0} cutout, {(float)_drawsByClass[2] / _frames:F0} blended, " +
                   // #129: indexed off the enum, not off literals -- inserting Water at 3 renumbered
                   // Additive, and the line went on labelling water draws "additive" while additive itself
                   // stopped being printed at all. This line is the acceptance instrument; it must not lie.
                   $"{(float)_drawsByClass[(int)OwnedClass.Water] / _frames:F0} water, " +
                   $"{(float)_drawsByClass[(int)OwnedClass.Additive] / _frames:F0} additive" +
                   (_blendedDropped == 0 ? "" : $"; {_blendedDropped} BLENDED PARTS HANDED BACK -- the class " +
                    "could not be owned whole, so none of it is") + ") " +
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
                   $"UNMATCHED {(float)_unmatched / _frames:F1}/frame" +
                   (_unmatched == 0 ? "" : $" ({(float)_unmatchedUnseen / _frames:F0} of them never walked by " +
                    "the engine at all -- geometry it culled and we drew anyway; the rest were walked and " +
                    "not suppressed, which is a bucket-list gap) (by capture bucket" + UnmatchedByBucket() +
                    $"; classes {string.Join("/", _unmatchedCls)})") + ", " +
                   (_watchEnrolled == 0 && Perf.BatchService.WatchEngineDraws == 0 ? "" :
                       $"dumptex {_watchEnrolled} enrolled, {(float)_watchSubmitted / _frames:F1} submitted/frame, " +
                       $"{(float)Perf.BatchService.WatchEngineDraws / _frames:F1} engine draws/frame, ") +
                   $"foreign-scene buckets {(float)_foreignBuckets / _frames:F1}/frame, " +
                   // #125: "masked" is the criterion, not "captured" -- it counts the frames the engine's own
                   // sky wrote nothing because the owned one had already drawn. Captured but never masked
                   // means the pass is staging nothing, which is a different bug from never seeing the sky.
                   $"{SkyBox.Stats()}, " +
                   $"{GrassField.Stats()}, " +
                   $"{Beams.Stats()}, " +
                   $"{Particles.Stats()}, " +
                   // #139: whether the HDR target actually engaged. "0 resolves" while the world renders is
                   // the whole failure mode -- the frame looks fine, just 8-bit and un-bloomed -- and the
                   // arm-time line above says which target refused.
                   $"post {PostChain.Stats(_frames)}, " +
                   $"{WaterPass.Stats(_frames)}, " +
                   $"enrolls={_enrolls} revokes={_revokes}; " +
                   // #119: the prepass's own cost, and what it bought. samples/px is the overdraw figure --
                   // run the same scene with SOWX_DEPTHPREPASS=0 and the difference IS the pass's worth.
                   (!_depthPrepass ? "prepass OFF (SOWX_DEPTHPREPASS=0)"
                    : _progDepth == 0 ? "prepass unavailable"
                    : $"prepass {(float)_prepassDraws / _frames:F0} draws/frame" +
                      (_prepassKeyed == 0 ? "" : $" ({(float)_prepassKeyed / _frames:F0} of them dissolving, keyhole stage)") +
                      (_prepassSkipped == 0 ? "" : $" (+{(float)_prepassSkipped / _frames:F0} excluded, displacing/skinned or cutout)") +
                      (_qPrepass.Measured ? $" in {_qPrepass.Avg / 1e6:F3}ms" : "")) +
                   (_qSamples.Measured && _lastPixels > 0
                       ? $", shaded {_qSamples.Avg / 1e6:F2}M samples/frame over {_lastPixels / 1e6f:F2}Mpx " +
                         $"(overdraw x{_qSamples.Avg / _lastPixels:F2})"
                       : "") +
                   $", LOD skipped {_lodSkipped / (float)Math.Max(_frames, 1):F0} sub-pixel draws/frame (SOWX_LOD_PX={_lodPx})" +
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
                   (_sceneEnv && !EnvProbe.AreaWide ? " (area declares one; SOWX_PROBE_AREAWIDE=1 honours it)" : "") + "; " +
                   OwnedLights.Diag(_clusterOverflow) +
                   // The coverage question -- "what is the engine still drawing, and why?" -- was only
                   // answerable from the one-shot enroll diag, which a healthy session prints once at startup
                   // and never again. It belongs where the numbers it explains are.
                   $"; {Perf.BatchService.OwnScanDiag()}" +
                   // Nothing lit means nothing samples the shadow maps -- the whole owned lit stage, cascades
                   // and cubes included, is dead code for the session. Loud, because it looks like working
                   // output: parts are owned, cubes render, and the frame is merely fullbright.
                   (_litCount == 0 && _lastEnrolled > 0
                       ? "; NOTHING LIT -- no owned draw samples the shadow maps"
                       : "") +
                   // Dangling texture pointers AurTex refused. A handful around an area transition is the
                   // engine freeing textures a material still names; a number that climbs every period means
                   // something upstream is holding a dead MATERIAL, which is a lifetime bug rather than a
                   // texture one -- and this line is the only place it would ever be visible.
                   (Framework.AurTex.Refused == 0 ? ""
                       : $"; {Framework.AurTex.Refused} dangling texture pointers refused (session total)"));
        _frames = 0; _draws = 0; _batches = 0; _skips = 0; _fallbacks = 0; _unmatched = 0; _revokes = 0; _enrolls = 0;
        _skinDrawn = 0; _skinNoPose = 0; _skinSlotOverflow = 0;
        _foreignBuckets = 0;
        SkyBox.ResetPeriod();
        GrassField.ResetPeriod();
        Beams.ResetPeriod();
        Particles.ResetPeriod();
        _watchSubmitted = 0; Perf.BatchService.WatchEngineDraws = 0;
        _skipNoLight = _skipIdentity = _skipNoMdl = _skipTex = _skipRange = _skipVao = _skipUnseen = _skipGob = 0;
        _ownDissolve = 0; _ownFade = 0;
        Array.Clear(_unmatchedBucket); Array.Clear(_unseenBucket); Array.Clear(_unmatchedCls); _unmatchedUnseen = 0;
        Array.Clear(_drawsByClass);
        _clusterOverflow = 0;
        _prepassDraws = 0; _prepassSkipped = 0; _prepassKeyed = 0; _lodSkipped = 0; _qPrepass.Reset(); _qSamples.Reset(); _qColour.Reset();
        _qParticles.Reset(); _qWater.Reset(); _qSkyGrass.Reset(); _qPost.Reset();
    }

    /// <summary>The unmatched total, split by the bucket the parts were captured in -- only the buckets that
    /// actually contributed, so the line stays short when one class dominates.</summary>
    private static string UnmatchedByBucket() => ByBucket(_unmatchedBucket);

    /// <summary>A per-bucket counter as "bucket:per-frame", only the buckets that contributed.</summary>
    private static string ByBucket(long[] counts)
    {
        string s = "";
        for (int b = 0; b < counts.Length; b++)
            if (counts[b] != 0) s += $" {b}:{(float)counts[b] / _frames:F0}";
        return s.Length == 0 ? " none" : s;
    }

    /// <summary>Revoke one slot: the part reverts to engine drawing from its next iterator sighting.</summary>
    private static void Revoke(int i)
    {
        Perf.BatchService.OwnFlag[_oEntry[i]] = 0;
        _oTri[i] = IntPtr.Zero;
        _revokes++;
    }

    private static void RevokeAll(string why)
    {
        // #147: ahead of the empty-table guard on purpose. Water planes are keyed on PART POINTERS, and every
        // reason to reach here (context loss, module unload, a wiped part table) is a reason those pointers
        // may be reused by different geometry -- a stale plane would union with the new area's and inflate
        // its fetch. Clearing costs an area's worth of re-measurement, which is a handful of frames.
        WaterBodies.Clear();
        if (_ownCount == 0) return;
        for (int i = 0; i < _ownCount; i++) if (_oTri[i] != IntPtr.Zero) Revoke(i);
        _ownCount = 0;
        // ...and the draw order with it: an empty table must not leave class ranges pointing into it.
        _orderCount = 0;
        Array.Clear(_clsStart); Array.Clear(_clsEnd);
        _enrollScene = IntPtr.Zero;   // the table is gone; a bucket walk must not match a Scene we no longer hold
        // Re-arm the enrollment report: after a release (module unload, context loss) the NEXT enrollment is
        // a genuinely new fact -- whether ownership came back at all, and over how much. Suppressing it as a
        // repeat is what made a post-unload session look identical to one that never enrolled.
        _reportedHit = false;
        // the VAO cache keys on VDO pointers that may now be freed: retire every slot for reuse. The GL
        // objects can only be deleted inside the bracket, so the rebuild happens there (EnsureVao).
        for (int v = 0; v < _vdoCount; v++) { _vVdo[v] = IntPtr.Zero; _vStale[v] = true; }
        // #125: the sky's captured parts hang off the same freed gobs, and its VAO slots off the same
        // retired cache. Dropping them re-arms the engine's own (unmasked) sky until capture warms again.
        SkyBox.Forget();
        // #130: and the beams, whose captured parts hang off the same freed BSP. Dropping them re-arms the
        // engine's own (unmasked) beam pass until capture warms again.
        Beams.Forget();
        Particles.Forget();
        _sinceEnroll = EnrollEvery;
        // ...and the fast retry, not just the next cadence tick. A wipe leaves BatchService's table empty,
        // so the enrollment this queues necessarily misses -- parts have to be re-recorded first -- and
        // without the retry budget the second attempt waits a full EnrollEvery. That is the ~1s of vanilla
        // after every release, and it is the visible half of the fallback. Same re-arm the render-mode edge
        // does, for the same reason.
        _resumeRetries = ResumeRetries;
        _enrollDiagLeft = EnrollDiagBudget;
        _log?.Info($"OwnedDrawService: ownership released ({why}) -- all parts revert to the engine walk");
    }

    /// <summary>Cold, at a frame boundary before the owned block: rebuild the ownership table wholesale from
    /// BatchService's capture tables. This IS the frame-atomic swap -- nothing else adds an entry, so no part
    /// can change owner between the owned draw and the bucket walk that skips it.
    /// <para>ponytail: rebuilt on a fixed cadence rather than per frame. Parts qualify/de-qualify rarely, and
    /// the failure mode is one-sided (a newly-qualifying part stays engine-drawn until the next enrollment);
    /// per-frame rebuild costs a full table scan for nothing.</para></summary>
    private static void Enroll(IntPtr scene)
    {
        _sinceEnroll = 0; _enrolls++; _enrollScene = scene;
        WipeHandles();   // #153: new enrollment, new tids -- yesterday's handles answer for nobody
        _watchEnrolled = 0;
        System.Array.Clear(Perf.BatchService.WatchTex);
        for (int i = 0; i < _ownCount; i++)
            if (_oTri[i] != IntPtr.Zero)
            {
                if (Perf.BatchService.WatchedEntry(_oEntry[i])) _log?.Info("[dumpframe] ENROLL cleared by the re-enrollment reset");
                Perf.BatchService.OwnFlag[_oEntry[i]] = 0;
            }
        _ownCount = 0; _litCount = 0; _litUnownable = 0; _skinUnownable = 0; _gobOwned = 0;
        Array.Clear(_sortCount, 0, MaxVdos * 2);
        Array.Clear(_clsCount); Array.Clear(_clsStart); Array.Clear(_clsEnd);
        _orderCount = 0;
        // the texture-key interning is scoped to ONE enrollment: it is a sort proxy only, so a fresh table
        // every time is both cheaper than invalidating it and immune to the recycled-pointer problem
        Array.Clear(_texSortCount, 0, MaxMats);
        Array.Clear(_mKey, 0, MatHash);
        _matCount = 0;

        // A lit part whose lighting cannot be reproduced (no normal stream, or the lit program failed to
        // build) is NOT enrolled: it goes back to the engine's own vanilla-lit draw, which is the only
        // fallback that holds "owned parts lit at least as well as vanilla". Fullbright is not.
        bool canLight = _lighting && !_litFailed;
        var c = default(OwnedCandidate);
        int candidates = 0;
        _capacitySpill = 0;
        Perf.BatchService.OwnScanBegin(scene);
        for (int e = 0, n = Perf.BatchService.EntryCount; e < n; e++)
        {
            if (!Perf.BatchService.TryOwnCandidate(e, ref c)) continue;
            candidates++;
            if (Perf.BatchService.WatchAccepted(e, c.Mat)) _watchEnrolled++;
            // #126: a refusal from here on is counted against the class it would have joined -- see the
            // all-owned-or-none sweep below. The surplus stays engine-drawn, never dropped.
            bool watch = Perf.BatchService.WatchedEntry(e);
            // #128: the two CAPACITY refusals do not go to the gob. They are not properties of the instance
            // -- the same creature is ownable in an emptier area -- and routing them there turns a full table
            // into a cliff: thousands of gobs marked, the refused set overflows, and the sweep then drops
            // EVERY instance in the scene rather than the surplus. "The surplus stays engine-drawn, never
            // dropped" is the rule, and at capacity a creature split across two renderers (a lighting
            // mismatch) beats every creature in the area reverting.
            if (_ownCount >= MaxOwned)
            { _capacitySpill++; Perf.BatchService.OwnRefusedByClass[c.Cls]++; if (watch) _log?.Info("[dumpframe] ENROLL refused: table full"); continue; }
            int v = VdoSlot(c.Vdo);
            if (v < 0)
            { Perf.BatchService.OwnRefusedByClass[c.Cls]++; if (watch) _log?.Info("[dumpframe] ENROLL refused: no VAO slot (harvest failed)"); continue; }
            // #137: a true skinmesh (a cloak, a robe, a tail) carries the bone streams and is POSED by them.
            // #128 refused it here for want of a skinning branch; the stages have one now, so the only
            // refusal left is the one #128's reason still holds for -- the kill switch off, or a bone stream
            // on a VDO that never reached PartSkin::Draw (so no pose can be forced for it). Either way it is
            // refused against the GOB, and the sweep below hands the creature wearing it back entire.
            // Refused in BOTH directions, because both mismatches are misrenders. A bone stream with no
            // PartSkin capture cannot be posed (no hook fired for it, so no trampoline to force); a PartSkin
            // capture whose VAO harvest came back boneless would enrol as an ordinary rigid part, skip
            // PoseSkinned, and draw in its bind pose -- which is the failure the asymmetric gate would have
            // let through as the QUIET one.
            if (_vSkin[v] != (_skinning && Perf.BatchService.Skinned(e)))
            { _skinUnownable++; Perf.BatchService.OwnRefuse(c.Cls, c.Gob); if (watch) _log?.Info($"[dumpframe] ENROLL refused: skin mismatch (armed={_skinning} boneStreams={_vSkin[v]} skinPart={Perf.BatchService.Skinned(e)})"); continue; }
            bool lit = c.Lit && _lighting;
            if (lit && (!canLight || !_vLit[v]))
            { _litUnownable++; Perf.BatchService.OwnRefuse(c.Cls, c.Gob); if (watch) _log?.Info($"[dumpframe] ENROLL refused: lit but unshadeable (canLight={canLight} vdoLit={_vLit[v]})"); continue; }
            // #121: an env-mapped part the engine draws UNLIT would go through the fullbright variant, which
            // has no env handling at all -- the engine still routes it to RenderEnvironmentMapped (the switch
            // in PartTriMesh::Draw is on HasEnvMap, not on the lit flag), so owning it would silently drop
            // whatever shine it had. Only the lit variant can reproduce it; the rest stay vanilla.
            if (c.Env && !lit)
            { _litUnownable++; Perf.BatchService.OwnRefuse(c.Cls, c.Gob); if (watch) _log?.Info("[dumpframe] ENROLL refused: env-mapped but unlit"); continue; }
            int i = _ownCount++;
            _oEntry[i] = e; _oTri[i] = c.Tri; _oNode[i] = c.Node; _oMat[i] = c.Mat; _oVdo[i] = c.Vdo;
            _oItem[i] = c.Item; _oPrim[i] = c.Prim; _oVao[i] = v; _oMx[i] = c.Mx; _oLit[i] = lit;
            _oRotBits[i] = c.RotBits;
            _oTexKey[i] = TexKey(c.Tex0);
            _oCls[i] = (byte)c.Cls; _oSrc[i] = (byte)c.Src; _oDst[i] = (byte)c.Dst;
            _oBucket[i] = (byte)c.Bucket; _oGob[i] = c.Gob;
            _oBone[i] = -1;
            if (c.Gob != IntPtr.Zero) _gobOwned++;
            // #126: the local bbox centre, for the blended pass's back-to-front key.
            float* lo = (float*)(c.Tri + PartBboxMin), hi = (float*)(c.Tri + PartBboxMax);
            _oCx[i] = (lo[0] + hi[0]) * 0.5f; _oCy[i] = (lo[1] + hi[1]) * 0.5f; _oCz[i] = (lo[2] + hi[2]) * 0.5f;
            float ex = hi[0] - lo[0], ey = hi[1] - lo[1], ez = hi[2] - lo[2];
            _oR[i] = 0.5f * MathF.Sqrt(ex * ex + ey * ey + ez * ez);
            ReadMaterial(c.Mat, i, c.Feat);
            // #121: bit 5 of the slot mask, and the one slot with no texture behind it -- the probe replaces
            // what the material's env map held, so all that is carried is the flag itself.
            if (c.Env) _oSlots[i] |= EnvSlot;
            float rad = BitConverter.Int32BitsToSingle(c.RotBits) * DegToRad;
            _oCos[i] = MathF.Cos(rad); _oSin[i] = MathF.Sin(rad);
            Perf.BatchService.OwnFlag[e] = 1;
            if (watch) _log?.Info($"[dumpframe] ENROLL accepted slot={i} cls={(OwnedClass)c.Cls} lit={lit} vao={v}");
        }
        // #107 s2 / #126: all-owned-or-none, for the one class whose draw ORDER is load-bearing. A half-owned
        // blended set has no coherent order -- our members draw sorted at the frame anchor and the engine's
        // leftovers draw unsorted later, so a window in front of a window composites by whichever renderer
        // happened to get it. Cutout and additive are order-independent by construction (depth-writing with a
        // discard, and addition commutes), so a partial one is merely a partial speed-up, not a wrong frame.
        // #128 / #107 s2: all-owned-or-none per INSTANCE, which is the creature class's version of the same
        // rule. A creature's opacity is a per-gob field re-evaluated every frame, so a half-owned creature is
        // a creature whose head fades and whose arm does not -- and the part we could not take (a skinmesh
        // cloak, a gob with an env override) is exactly the one that would be left standing. Dropping the
        // instance whole is the only state that reads as one renderer.
        _gobParts = 0;
        for (int i = 0; i < _ownCount; i++)
            if (_oTri[i] != IntPtr.Zero && Perf.BatchService.GobRefused(_oGob[i]))
            {
                if (Perf.BatchService.WatchedEntry(_oEntry[i])) _log?.Info("[dumpframe] ENROLL revoked: instance dropped whole");
                Perf.BatchService.OwnFlag[_oEntry[i]] = 0; _oTri[i] = IntPtr.Zero; _gobParts++;
            }
        _blendedDropped = 0;
        if (Perf.BatchService.OwnRefusedByClass[(int)OwnedClass.Blended] > 0)
            for (int i = 0; i < _ownCount; i++)
                if (_oTri[i] != IntPtr.Zero && _oCls[i] == (byte)OwnedClass.Blended)
                {
                    if (Perf.BatchService.WatchedEntry(_oEntry[i])) _log?.Info("[dumpframe] ENROLL revoked: blended class dropped whole");
                    Perf.BatchService.OwnFlag[_oEntry[i]] = 0; _oTri[i] = IntPtr.Zero; _blendedDropped++;
                }
        // The radix digits are counted AFTER the sweep, so a dropped class costs no order slot.
        for (int i = 0; i < _ownCount; i++)
        {
            if (_oTri[i] == IntPtr.Zero) continue;
            _sortCount[SortKey(i)]++;
            _texSortCount[_oTexKey[i]]++;
            _clsCount[_oCls[i]]++;
            if (_oLit[i]) _litCount++;
        }
        // Three-digit LSD radix into _order, every digit counting-sorted (stable): texture, then (lit, VDO),
        // then #126's class. The result walks grouped by CLASS -- so each pass owns a contiguous batch range
        // -- and inside a class by (lit, VDO) with equal textures contiguous, which is the #84 batch key. The
        // draw loop then cuts a batch on a key change instead of searching for one.
        int at = 0;
        for (int k = 0; k < MaxMats; k++) { int n = _texSortCount[k]; _texSortCount[k] = at; at += n; }
        for (int i = 0; i < _ownCount; i++) if (_oTri[i] != IntPtr.Zero) _orderTmp[_texSortCount[_oTexKey[i]]++] = i;
        int live = at;
        at = 0;
        for (int k = 0; k < MaxVdos * 2; k++) { int n = _sortCount[k]; _sortCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _orderTmp[k]; _order[_sortCount[SortKey(i)]++] = i; }
        at = 0;
        for (int k = 0; k < OwnedClasses.Count; k++) { int n = _clsCount[k]; _clsStart[k] = at; _clsCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _order[k]; _orderTmp[_clsCount[_oCls[i]]++] = i; }
        // ...and the cursor each class's fill left behind IS its end index.
        for (int k = 0; k < OwnedClasses.Count; k++) _clsEnd[k] = _clsCount[k];
        Array.Copy(_orderTmp, _order, live);
        _orderCount = live;

        // #128: an instance that just changed hands has to change hands on the SHADOW side too, and the two
        // enrollments run on independent counters -- see CasterPass.ForceEnroll for the frames of darkness
        // that costs. Poked whenever any instanced part is owned, which is the only case that can open it.
        if (_gobOwned > 0) Shadow.CasterPass.ForceEnroll();
        _lastEnrolled = _orderCount; _lastCandidates = candidates; _lastTextures = _matCount;
        // Empty right after a resume means the re-record has not finished, not that nothing is ownable:
        // re-arm the cadence so the next pass tries again instead of waiting out the full countdown.
        // ...on the DRAWABLE count: the all-owned-or-none sweep can empty the order while leaving slots
        // enrolled, and an enrollment that drew nothing is exactly the case this retry exists for.
        if (_orderCount == 0 && _resumeRetries > 0) { _resumeRetries--; _sinceEnroll = EnrollEvery; }
        else _resumeRetries = 0;
        if (!Perf.BatchService.Active)
        {
            if (_enrollDiagLeft-- > 0)
                _log?.Info("OwnedDrawService: BatchService inactive (SOWX_BATCH=0 or hooks unavailable) -- " +
                           "the owned pass enrolls from its capture tables, so nothing can be owned");
            return;
        }
        // a miss spends a diagnostic, a hit reports once and stops: a scan during the loading screen has
        // nothing to offer yet and must not burn the budget before there is an area to enroll from
        if (_orderCount == 0)
        {
            if (_enrollDiagLeft-- > 0)
                _log?.Info($"OwnedDrawService: enroll miss -- {Perf.BatchService.OwnScanDiag()}");
        }
        else if (!_reportedHit)
        {
            _reportedHit = true;
            _log?.Info($"OwnedDrawService: enrolled {_orderCount}/{candidates} ownable parts on {_vdoCount} VDOs " +
                       $"({Perf.BatchService.OwnScanDiag()})");
        }
    }

    /// <summary>Find (or harvest) the VAO-cache slot for a VDO. Harvesting reads the engine VAO's attribute
    /// formats -- engine context, read-only queries, binding restored -- so the 4.6 side can rebuild an
    /// equivalent VAO over the same shared buffers.</summary>
    private static int VdoSlot(IntPtr vdo)
    {
        int s = -1;
        for (int v = 0; v < _vdoCount; v++)
        {
            if (_vVdo[v] == vdo) return v;
            if (_vVdo[v] == IntPtr.Zero && s < 0) s = v;      // retired slot: reuse it, VAO rebuilt on use
        }
        if (s < 0)
        {
            if (_vdoCount >= MaxVdos) return -1;
            s = _vdoCount++;
            _vVao46[s] = 0;
        }
        if (!Harvest(vdo, s)) return -1;
        _vVdo[s] = vdo; _vStale[s] = _vVao46[s] != 0;
        return s;
    }

    /// <summary>Counting-sort key: lit parts occupy the upper half so one program bind covers each half.</summary>
    private static int SortKey(int i) => _oLit[i] ? MaxVdos + _oVao[i] : _oVao[i];

    /// <summary>Headless check of the one #84 desync that fails SILENTLY: the per-draw SSBO's binding point
    /// and record stride live twice -- once as C# constants driving the staging writes and the upload, once as
    /// literal GLSL in <see cref="DrawBufDecl"/>. Bind to the wrong point or stage the wrong stride and the
    /// shaders read zeros or a neighbour's record, which renders as black/misplaced geometry rather than an
    /// error. Verified against glslang reflection: model@0 rot@64 amb@80 dif@96 emi@112 pbr@128 spc@144,
    /// array stride 160.
    /// <para>False = do not arm. A check that only logs would let exactly the misrender it predicts ship, with
    /// one easily-missed line as the signal -- and because the iterator would still suppress the engine's own
    /// draws, the world goes black rather than merely unaccelerated. Standing down is the honest failure.</para></summary>
    private static bool SelfCheck(ExtenderLog log)
    {
        // the record's field list, counted: four vec4s after the mat4 IS the 128-byte stride staged above
        bool ok = SortSelfCheck(log)
                  && DrawBufDecl.Contains($"binding = {BindDraw}") && DrawFloats * sizeof(float) == 176
                  && DrawBufDecl.Split("vec4").Length - 1 == (DrawFloats - 16) / 4
                  && DrawBufDecl.Contains("vec4 emi") && DrawBufDecl.Contains("vec4 spc")
                  && DrawBufDecl.Contains("vec4 feat")
                  // #123: the feature ids are a contract between the ShaderFeature enum (staged into
                  // d.feat.x) and the GLSL that switches on it. Drift renders the wrong effect on the wrong
                  // content -- roofs swaying, plants dropping 500m -- with nothing in the log.
                  && FeatDecl.Contains($"SOWX_FEAT_WINDGRASS {(int)ShaderFeature.WindGrass}")
                  && FeatDecl.Contains($"SOWX_FEAT_WINDLEAF  {(int)ShaderFeature.WindLeaf}")
                  && FeatDecl.Contains($"SOWX_FEAT_WINDPLANT {(int)ShaderFeature.WindPlant}")
                  && FeatDecl.Contains($"SOWX_FEAT_ROOFKN    {(int)ShaderFeature.RoofKn}")
                  && FeatDecl.Contains($"SOWX_FEAT_ROOFKNM   {(int)ShaderFeature.RoofKnM}")
                  && FeatDecl.Contains($"SOWX_FEAT_FLOWMAP   {(int)ShaderFeature.Flowmap}")
                  && FeatDecl.Contains($"SOWX_FEAT_SKIN      {(int)ShaderFeature.Skin}")
                  // #137: the skinning half. The binding point and the two attribute locations live twice
                  // (C# consts, literal GLSL) and neither disagreement is visible -- a wrong binding reads an
                  // empty block and collapses the mesh to the origin, a wrong location reads whatever stream
                  // the engine left at 8/9. The weight guards are bounds protection on an SSBO index, not a
                  // micro-optimisation, so their absence is checked as well.
                  && BoneVs.Contains($"binding = {BindBones}")
                  && BoneVs.Contains($"layout(location = {LocBoneIdx}) in vec4 aBoneIdx;")
                  && BoneVs.Contains($"layout(location = {LocBoneW}) in vec4 aBoneWeight;")
                  && BoneVs.Contains("aBoneWeight.w > 0.0")
                  && BoneVs.Contains("int(d.feat.x) == SOWX_FEAT_SKIN ? int(d.feat.y) : -1")
                  && BoneFloats * sizeof(float) == 4096
                  // ...and both stages take the skinned arm BEFORE the displaced test, which is what keeps
                  // the literal clip expression below reachable for every part that is still in the prepass.
                  && OwnedVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * pos;")
                  && OwnedLitVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * pos;")
                  // ...which is also the token SkinSafe reads to spot a pre-#137 content override. It has to
                  // be in the COMPOSED stage, not merely in BoneVs, or the guard passes on text that never
                  // reached the program. And it must NOT be in the depth stage: that one is the prepass, and
                  // a skinning branch appearing there would mean the exclusion above had been undone.
                  && OwnedVs.Contains("SOWX_FEAT_SKIN") && OwnedLitVs.Contains("SOWX_FEAT_SKIN")
                  && !DepthVs.Contains("SOWX_FEAT_SKIN") && !DepthKeyVs.Contains("SOWX_FEAT_SKIN")
                  // The prepass exclusion is the OTHER half of that decision, and it is the half no shader
                  // text can assert: a skinned batch left in the prepass writes a bind-pose silhouette and
                  // LEQUAL then punches the cloak full of holes.
                  && ShaderFeatures.OutOfPrepass((int)ShaderFeature.Skin)
                  && !ShaderFeatures.Displaces((int)ShaderFeature.Skin)
                  && ShaderFeatures.OutOfPrepass((int)ShaderFeature.WindGrass)
                  && !ShaderFeatures.OutOfPrepass((int)ShaderFeature.Flowmap)
                  // ...and the CPU-side exclusions that go with them: no name here, no switch there.
                  && ShaderFeatures.VsNames.Length == 8
                  && ShaderFeatures.Displaces((int)ShaderFeature.WindGrass)
                  && ShaderFeatures.Displaces((int)ShaderFeature.RoofKnM)
                  && !ShaderFeatures.Displaces((int)ShaderFeature.Flowmap)
                  && !ShaderFeatures.Displaces((int)ShaderFeature.NoKeyhole)
                  && ShaderFeatures.NoKeyholing((int)ShaderFeature.NoKeyhole)
                  && ShaderFeatures.NoKeyholing((int)ShaderFeature.RoofKn)
                  && ShaderFeatures.NoKeyholing((int)ShaderFeature.Flowmap)
                  && !ShaderFeatures.NoKeyholing((int)ShaderFeature.RoofKnM)
                  && !ShaderFeatures.NoKeyholing((int)ShaderFeature.WindPlant)
                  // ...and the GLSL predicate picking the displaced clip expression mirrors Displaces(). If it
                  // ever says "any feature" again, nokeyhole and flowmap sit in the prepass computing a clip
                  // expression the prepass never compiled -- the #119 hole, with nothing in the log.
                  && FeatVs.Contains("bool SowxDisplaces(int feat) { return feat >= SOWX_FEAT_WINDGRASS "
                                     + "&& feat <= SOWX_FEAT_ROOFKNM; }")
                  && OwnedLitVs.Contains("if (!SowxDisplaces(int(d.feat.x))) " + ClipExpr)
                  && OwnedVs.Contains("if (!SowxDisplaces(int(d.feat.x))) " + ClipExpr)
                  // ...and the map slots must stay BELOW the probe's unit. A samplerCube and a sampler2D
                  // pointed at one image unit is INVALID_OPERATION: every lit batch is dropped, and the log
                  // says nothing at all. #123 grew the slots into exactly that collision once.
                  && PbrUnit0 + MapSlots <= EnvProbe.ProbeUnit
                  // #146: the AO map is one more slot on that same ladder, and its GLSL bit is a HAND-WRITTEN
                  // copy of AoSlot/EnvSlot. Drift there is silent twice over: an AO bit that reads as the env
                  // bit turns every AO material legacy-shiny, and a lost multiply just renders unoccluded --
                  // which is what shipped content looks like today, so nothing would look wrong.
                  && OwnedLitFsHead.Contains($"SOWX_SLOT_AO = {AoBit};")
                  && OwnedLitFsHead.Contains($"SOWX_SLOT_ENV = {EnvSlot};")
                  && OwnedLitFsMain.Contains("if ((slots & SOWX_SLOT_AO) != 0 && shadeLod < 1.0) occl *= mix(texture(uAoTex, uv).r, 1.0, shadeLod);")
                  && OwnedLitFsMain.Contains("* mix(0.15, 1.0, sunRaw) * occl")
                  && OwnedVs.Contains("uDrawBase + gl_DrawID") && OwnedLitVs.Contains("uDrawBase + gl_DrawID")
                  // #119: the prepass depth is only re-testable by the colour pass while all three stages
                  // compute the SAME clip expression and declare it invariant. Drift either and lit
                  // surfaces lose fragments to LEQUAL -- a hole, with nothing in the log.
                  && OwnedVs.Contains(ClipExpr) && OwnedLitVs.Contains(ClipExpr) && DepthVs.Contains(ClipExpr)
                  && OwnedVs.Contains("invariant gl_Position;") && OwnedLitVs.Contains("invariant gl_Position;")
                  && DepthVs.Contains("invariant gl_Position;")
                  // #124: ...and the keyhole depth stage is a FOURTH copy of the same contract. It writes the
                  // depth a dissolving batch is tested against, so drift here is the #119 hole again.
                  && DepthKeyVs.Contains(ClipExpr) && DepthKeyVs.Contains("invariant gl_Position;")
                  // #102/#132: the lit stage lights in LINEAR and now STAYS there -- the frame's one encode
                  // is PostChain's resolve. Both halves are silent when they drift: a stage that stopped
                  // linearising its texel renders washed out, one that re-encoded renders twice-encoded, and
                  // neither says anything. So assert both, including the absence.
                  && OwnedLitFsMain.Contains("SowxLinear(texel.rgb)")
                  && !OwnedLitFsMain.Contains("SowxRevert")
                  && !OwnedLights.FragmentLib.Contains("SowxRevert")
                  // #120: the lobe the whole ticket exists to restore, and the one-walk light function it
                  // rides in. A stage that lost either still compiles and still renders -- flat, exactly as
                  // it did before.
                  && OwnedLights.FragmentLib.Contains("float SowxSpecLobe(")
                  && OwnedLights.FragmentLib.Contains("float SowxSpecBase(")
                  && OwnedLitFsMain.Contains("sunSpec * SowxSpecLobe(N, V, uAreaDir")
                  // ...fed the RAW dot. #143's wrap is diffuse-only; a lobe on the wrapped dot lights the
                  // side of a glossy surface the sun cannot reach, and only at uWrapN > 0 -- invisible at
                  // the default.
                  && OwnedLitFsMain.Contains("vec3 sunSpec = uAreaDiffuse * (sunRaw * sunVis);")
                  // ...and the base modifier really is applied to the SUM, once. Folding it per light is
                  // silent and wrong by exactly the light count.
                  && OwnedLitFsMain.Contains("* (specColor * SowxSpecBase(")
                  && OwnedLights.FragmentLib.Contains("vec3 SowxClusteredLight(")
                  // #143: the wrapped-diffuse helper, and the sun actually going THROUGH it --
                  // a lit tail that keeps the raw dot is the punctual terminator back with no other sign.
                  && OwnedLights.FragmentLib.Contains("float SowxWrap(float ndl, float w)")
                  && OwnedLights.FragmentLib.Contains("max(s, uWrapN)")
                  && OwnedLitFsMain.Contains("SowxWrap(dot(N, uAreaDir), uWrapN)")
                  // ...while the ambient hemisphere keeps the RAW dot -- that line is inc_lighting.shd:679
                  // verbatim and wrapping it would drift the owned pass off the engine's own ambient.
                  && OwnedLitFsMain.Contains("mix(0.15, 1.0, sunRaw)")
                  // ...and the fullbright stage must DECODE, unconditionally: #132 made the target linear,
                  // so a stage that passed its display-space texel through would be wrong by a gamma
                  // wherever the engine is not drawing beside it.
                  && OwnedFs.Contains("vec3($SRGB$)") && !OwnedFs.Contains("uCapture")
                  // #121: the ambient-specular term, and the capture pairing that keeps the probe linear.
                  // A stage that lost the sample still renders (flat, as before #121); one that kept the
                  // sample but lost the capture branch samples the cube it is drawing into -- undefined --
                  // and stores display-space values a mip filter then darkens. Both are silent.
                  && OwnedLitFsMain.Contains("textureLod(uProbe, reflect(-V, N)")
                  && OwnedLitFsMain.Contains("uCapture == 0 && uProbeMips > 0.0")
                  && OwnedLitFsMain.Contains("if (uCapture != 0) { frag = vec4(lit, 1.0); return; }")
                  // ...and the weighting itself: the engine tints the light that is already there. Dropping
                  // the (ambient + diffuse) factor is what made every stone surface read as wet.
                  && OwnedLitFsMain.Contains("(ambient + diffuse) * env * F0 * uProbeGain")
                  // ...and the gate that keeps it off the parts the engine binds no env texture for at all.
                  && OwnedLitFsMain.Contains("uSceneEnv != 0 || (slots & SOWX_SLOT_ENV) != 0")
                  // #124/#132: the dissolve and the fog, and the order they apply in. Both are LINEAR now --
                  // #124 welded the engine's keyhole-then-encode-then-fog order and #132 unwelded it, so the
                  // whole tail is one linear expression and the encode is the resolve's. Swap the two and
                  // the world still renders, just with fog that dissolves and a border that does not darken;
                  // nothing says so in the log.
                  && KeyFogFs.Contains("float SowxKeyhole(") && KeyFogFs.Contains("vec3 SowxFog(")
                  && OwnedLitFsMain.Contains("SowxFog(lit * kh, vDepth)")
                  // #139: ...and the two terms whose loss is silent. A stage that dropped the wrap or the
                  // soft cube taps just renders harder -- nothing about either fails to compile or shows in
                  // the log.
                  && OwnedLights.FragmentLib.Contains("float s = uWrap <= 0.0 ? 0.0 : clamp(uWrap * uFalloff.y")
                  // #140: the engine's own range-normalised curve. A stage that kept the old absolute
                  // 1/d^n plateau still renders -- blinding at a metre, black at five -- and says nothing,
                  // so the shape is asserted rather than trusted to stay put.
                  && OwnedLights.FragmentLib.Contains("float att = uFalloff.x * fade / (at.x + at.y * f2);")
                  // #142: the near-field term, and the guard that makes an unset uHot mean "off". A stage
                  // that lost it renders every light flat at the gain the BODY wants -- which is the exact
                  // symptom the term exists to fix, and says nothing.
                  && OwnedLights.FragmentLib.Contains("att += uHot.x * fade * t * t / at.x;")
                  && OwnedLights.FragmentLib.Contains("max(1.0 - f2 / (uHot.y * uHot.y), 0.0)")
                  && OwnedLights.FragmentLib.Contains("float f2 = dd / rr;")
                  // ...and the fallback pair, so a program that never gets uAtten falls back instead of
                  // dividing every light by zero and rendering the world white.
                  && OwnedLights.FragmentLib.Contains("uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0)")
                  && OwnedLights.LocalShadowLib.Contains("if (m < uLocalSoft.y) return 1.0;")
                  // ...and every one of them must read "off" from the ZERO a program that never sets the
                  // uniform leaves behind, or a stale content override (#92) does something wrong rather
                  // than something old. This is the whole reason none of them rides in uFalloff's spare lane.
                  && OwnedLights.LocalShadowLib.Contains("if (uLocalSoft.x <= 0.0) return v;")
                  // ...and the dissolve has to reach the fragment stage at all: it rides in rot.z, which was
                  // a dead float, so a stage reading the wrong lane dissolves nothing and says nothing.
                  && OwnedLitFsMain.Contains("SowxKeyhole(d.rot.z, vWorld, vDepth)")
                  // ...and the FULLBRIGHT stage must have NEITHER. Every unlit fragment stage in
                  // base_shaders.bif compiles FOG 0 and KEYHOLING 0 -- fst/fst_sm/fsglu (textured) and
                  // fs/fstc (untextured) alike -- so an unlit part that fogged or dissolved here would be
                  // doing to a part something vanilla never does to it. Asserted rather than commented:
                  // both are one line to add back and neither would look obviously wrong.
                  && !OwnedFs.Contains("SowxKeyhole") && !OwnedFs.Contains("SowxFog")
                  // ...and the prepass punches the SAME aperture the colour pass will. Both stages call the
                  // one SowxKeyhole out of KeyFogFs, so they cannot drift by a constant -- but a stage that
                  // lost the call writes depth over fragments the colour pass then discards, and the room
                  // behind the wall goes dark with nothing in the log.
                  && DepthKeyFs.Contains("float SowxKeyhole(")
                  && DepthKeyFs.Contains("SowxKeyhole(uDraw[vDraw].rot.z, vWorld, vDepth)")
                  // #126: the cutout, in both colour stages -- and spelled the engine's way, so a stage that
                  // kept the uniform but lost the >= 0.0 guard would discard every fragment of the OPAQUE
                  // set (which passes -1.0) and render an empty world.
                  && OwnedFs.Contains("if (uAlphaCut >= 0.0 && t.a <= uAlphaCut) discard;")
                  && OwnedLitFsMain.Contains("if (uAlphaCut >= 0.0 && texel.a <= uAlphaCut) discard;")
                  // ...and the per-draw alpha reaching the framebuffer, which is the whole of the blended
                  // class: a stage writing a constant 1.0 there renders every transparent surface solid.
                  && OwnedFs.Contains("t.a * vAlpha") && OwnedVs.Contains("vAlpha = d.rot.w;")
                  && OwnedLitFsMain.Contains("texel.a * d.rot.w")
                  // ...and the embedded stages themselves satisfy the override guard, so a wrong guard fails
                  // here rather than rebuilding every program from the embedded text every session (which
                  // would look like the override silently not applying).
                  && CutoutSafe(OwnedVs, OwnedFs)
                  && CutoutSafe(OwnedLitVs, OwnedLitFsHead + OwnedLitFsMain)
                  // #132: ...and the same for the colour-space guard, over every stage it polices. A guard
                  // the embedded text itself trips rebuilds every program from that same text, every
                  // session -- an infinite "your override is stale" that no override caused.
                  && LinearSafe(OwnedFs) && LinearSafe(OwnedLitFsHead + OwnedLitFsMain)
                  // #153: the bindless rewrite must produce a complete swap on the embedded lit stage --
                  // extension + handle block in, every bound-sampler declaration out -- and must leave
                  // foreign text (no known decls) untouched rather than half-rewritten.
                  && BindlessFs(OwnedLitFsHead + MaterialLib).Contains("SowxTexH")
                  && BindlessFs(OwnedLitFsHead + MaterialLib).Contains("GL_ARB_bindless_texture")
                  && !BindlessFs(OwnedLitFsHead + MaterialLib).Contains("uniform sampler2D uTex;")
                  && !BindlessFs(OwnedLitFsHead + MaterialLib).Contains("uniform sampler2D uAoTex;")
                  && BindlessFs("void main() { }") == "void main() { }"
                  && LinearSafe(WaterPass.Fs) && LinearSafe(GrassField.EmbeddedFs)
                  // ...and the suppression list is the capture list. These two disagreeing is the one #126
                  // failure that is silent in both directions -- geometry dropped, or drawn twice.
                  && Perf.BatchService.OwnableBucket(0) && Perf.BatchService.OwnableBucket(1)
                  && Perf.BatchService.OwnableBucket(4) && Perf.BatchService.OwnableBucket(7)
                  && Perf.BatchService.OwnableBucket(8)
                  // #134: the dynamic gob pair joins, and joins TOGETHER -- the engine walks 2 then 3, and
                  // owning one without the other splits one instance's geometry across two renderers.
                  && Perf.BatchService.OwnableBucket(2) && Perf.BatchService.OwnableBucket(3)
                  // 5 (refraction glass) stays the engine's: it reads a framebuffer copy taken after the
                  // owned bracket has already resolved.
                  // #129: 6 is deliberately NOT asserted here. OwnableBucket evaluates it AS
                  // WaterPass.Usable, so any check of the two against each other is a tautology -- it would
                  // read as cover and catch nothing. What actually governs the water draw is _waterArmed
                  // (Usable && lightingOn && the program), and what keeps suppression honest is _waterDrew,
                  // which IterSight tests directly. Both are structural, and neither is assertable here.
                  && !Perf.BatchService.OwnableBucket(5)
                  // #92: a resref past the CResRef limit can never name a resource, so an override
                  // dropped under it would go silently unread.
                  && ShdFullbrightVs.Length <= ShaderResource.ResRefBytes
                  && ShdFullbrightFs.Length <= ShaderResource.ResRefBytes
                  && ShdLitVs.Length <= ShaderResource.ResRefBytes
                  && ShdLitFs.Length <= ShaderResource.ResRefBytes
                  && ShdWaterVs.Length <= ShaderResource.ResRefBytes
                  && ShdWaterFs.Length <= ShaderResource.ResRefBytes
                  && ShdWaterTcs.Length <= ShaderResource.ResRefBytes
                  && ShdWaterTes.Length <= ShaderResource.ResRefBytes;
        // headless shader validation hook: dump the assembled lit stages for glslangValidator -- a compile
        // error there otherwise costs a live run to discover (#153 shipped one exactly that way).
        if (Environment.GetEnvironmentVariable("SOWX_DUMP_SHADERS") is string dumpDir && dumpDir.Length > 0)
        {
            string lit = OwnedLitFsHead + OwnedLights.LocalShadowLib + OwnedLights.FragmentLib
                         + OwnedLights.SunShadowLib + MaterialLib + KeyFogFs + OwnedLitFsMain;
            System.IO.File.WriteAllText(System.IO.Path.Combine(dumpDir, "lit.frag"), lit);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dumpDir, "lit_bindless.frag"), BindlessFs(lit));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dumpDir, "lit.vert"), OwnedLitVs);
        }

        log.Info(ok
            ? "OwnedDrawService.SelfCheck: passed"
            : $"OwnedDrawService.SelfCheck: FAILED -- per-draw SSBO contract (binding {BindDraw}, " +
              $"stride {DrawFloats * sizeof(float)}B) or the #102 linear/display-space pairing drifted; " +
              "standing down rather than drawing from the wrong records");
        return ok;
    }

    /// <summary>#120: harvest what the part's .mtr declared -- which of the extra texture slots carry a
    /// map, and the four stock PBR parameters by name (plus, #123, the parameters of whichever custom shader
    /// <paramref name="feat"/> named). Deliberately reads the STOCK vocabulary
    /// (<c>Specularity</c>, <c>Roughness</c>, <c>Metallicness</c>, <c>CustomSpecularColor</c>) so content
    /// authored for the engine's own shaders works here unchanged; a name we do not know is left alone for
    /// whoever does. Zero means "not declared" in the engine's own shaders too, so it is what the synthesis
    /// path in the fragment stage keys on.
    /// <para>Cold: one call per enrolled part per enrollment. Material parameters are parsed once when the
    /// .mtr loads and do not animate, so nothing re-reads them per frame.</para></summary>
    /// <summary>Mask bit of map slot <paramref name="s"/>: its index, except for the AO slot -- see
    /// <see cref="AoBit"/> for why that one is out of line.</summary>
    private static int SlotBit(int s) => s == AoSlot ? AoBit : 1 << s;

    private static void ReadMaterial(IntPtr mat, int i, int feat)
    {
        // Slots 3..5 exist ONLY on a SharedMaterial. Ghidra Material::GetTexture @0x4c5810 has no bounds
        // check: with no shared material it hands back mat+i*0x10 for any i, and past the Material's own
        // three (BindAllStandardTextures @0x4c6e30 stops at i < 3) those are other fields -- +0x40 is the env
        // map, +0x50 the bumpy-shiny map. Asking for slot 4 on a plain Material would bind an env map as a
        // height map. THIS gate is the only thing stopping that now: #121 made env-mapped parts ownable, so
        // +0x40 is reachable on a live material rather than rejected at Record.
        int top = *(IntPtr*)(mat + MatShared) != IntPtr.Zero ? PbrSlots : 2;
        int mask = 0;
        for (int s = 1; s <= top; s++) if (_getTexture(mat, s) != 0) mask |= 1 << (s - 1);
        // #123: and slot 6 for the one feature that reads it. A flowmap material with no flow map bound
        // leaves the bit clear, and the fragment stage then slides nothing -- vanilla's texture6Bound test.
        if (feat == (int)ShaderFeature.Flowmap && _getTexture(mat, FlowSlot + 1) != 0) mask |= 1 << FlowSlot;
        // #146: ...and slot 7 for the AO map, inside the SAME shared-material gate the loop above needs --
        // on a plain Material, GetTexture(7) is an unbounded read of whatever field sits at mat+0x70.
        if (top == PbrSlots && _getTexture(mat, AoSlot + 1) != 0) mask |= AoBit;
        _oSlots[i] = mask;

        int f = i * 4;
        _oFeat[f] = feat; _oFeat[f + 1] = _oFeat[f + 2] = _oFeat[f + 3] = 0f;

        int o = i * 8;
        _oParam[o] = _oParam[o + 1] = _oParam[o + 2] = 0f;
        _oParam[o + 4] = _oParam[o + 5] = _oParam[o + 6] = _oParam[o + 7] = 0f;
        // TWO lists, shared first. The .mtr's declared values are parsed onto the SHARED material's own list
        // (shared+0x1d0 / count +0x1d8) -- Material::SetMaterialParam @0x4c5d70 copies whole 100-byte records
        // out of exactly that list into the instance one. The instance list at mat+0x118 is only SIZED to the
        // shared count by Material::InitSharedMaterial @0x4c7260, which appends default records rather than
        // the shared values, so reading it alone would hand back floors for the very content this exists for.
        // Reading the instance list second is what lets a per-part override still win where one is set.
        IntPtr shared = *(IntPtr*)(mat + MatShared);
        if (shared != IntPtr.Zero)
            Apply(*(IntPtr*)(shared + SharedParams), *(int*)(shared + SharedParamCount), o, f);
        Apply(*(IntPtr*)(mat + MatParams), *(int*)(mat + MatParamCount), o, f);
        // fSwaySpeed divides. Zero is what an undeclared parameter reads as, and 1.0 is the value every
        // wind .mtr states ("denormalize the parameters set to 1.0 for this type of foliage"), so the
        // undeclared case IS the authored one -- and without this it is a division by zero, i.e. NaN
        // positions and a plant that vanishes.
        if (_oFeat[f] >= (int)ShaderFeature.WindGrass && _oFeat[f] <= (int)ShaderFeature.WindPlant
            && _oFeat[f + 1] == 0f) _oFeat[f + 1] = 1f;
    }

    /// <summary>Fold one <c>List&lt;MaterialParameter&gt;</c> into the staged PBR pair at
    /// <paramref name="o"/> and the #123 feature vec4 at <paramref name="f"/>, matching the stock names and
    /// this part's feature vocabulary. Later calls override earlier ones.</summary>
    private static void Apply(IntPtr list, int count, int o, int f)
    {
        if (list == IntPtr.Zero || count <= 0 || count > MaxParams) return;
        int feat = (int)_oFeat[f];
        for (int k = 0; k < count; k++)
        {
            byte* rec = (byte*)list + (long)k * ParamStride;
            float* p = (float*)(rec + ParamValue);
            int comps = *(ushort*)(rec + ParamComps);
            // #123: iRoofInterior is the one int-valued parameter any of this reads, and the roof shaders
            // compare it to 1. Taken before the float-only gate below, where it would be skipped. The value
            // sits at the SAME +0x44 the floats do -- BindShaderParameters @0x4c5480 sends the type-1 branch
            // to SetCustomParameterValuei(prog, loc, *(int*)(rec + 0x44)), the type-0 branch to
            // SetCustomParameterValuef with the same address as a float*. Only the read type differs.
            if (*(int*)(rec + ParamType) != 0)
            {
                if (feat == (int)ShaderFeature.RoofKn || feat == (int)ShaderFeature.RoofKnM)
                    if (NameIs(rec, "iRoofInterior") && comps >= 1) _oFeat[f + 3] = *(int*)p;
                continue;                                     // otherwise: 1 = int-valued, none of ours are
            }
            // #123: the feature parameters, by the names the seven community shaders declare. Which three
            // land in the record depends on the feature, so one vec4 covers all of them.
            switch (feat)
            {
                case (int)ShaderFeature.WindGrass:
                case (int)ShaderFeature.WindLeaf:
                case (int)ShaderFeature.WindPlant:
                    if (NameIs(rec, "fSwaySpeed") && comps >= 1) { _oFeat[f + 1] = p[0]; continue; }
                    if (NameIs(rec, "fSwayStrength") && comps >= 1) { _oFeat[f + 2] = p[0]; continue; }
                    if (NameIs(rec, "fTilesetMeshOffsetZ") && comps >= 1) { _oFeat[f + 3] = p[0]; continue; }
                    break;
                case (int)ShaderFeature.RoofKn:
                case (int)ShaderFeature.RoofKnM:
                    if (NameIs(rec, "fRoofOffsetZ") && comps >= 1) { _oFeat[f + 1] = p[0]; continue; }
                    if (NameIs(rec, "fRoofCameraClipZ") && comps >= 1) { _oFeat[f + 2] = p[0]; continue; }
                    break;
                case (int)ShaderFeature.Flowmap:
                    if (NameIs(rec, "fSlideSpeed") && comps >= 1) { _oFeat[f + 1] = p[0]; continue; }
                    break;
            }
            if (NameIs(rec, "Specularity") && comps >= 1) _oParam[o] = p[0];
            else if (NameIs(rec, "Roughness") && comps >= 1) _oParam[o + 1] = p[0];
            else if (NameIs(rec, "Metallicness") && comps >= 1) _oParam[o + 2] = p[0];
            else if (NameIs(rec, "CustomSpecularColor") && comps >= 3)
            {
                _oParam[o + 4] = p[0]; _oParam[o + 5] = p[1]; _oParam[o + 6] = p[2];
                // Black IS the engine's "no custom colour" sentinel (inc_material.shd compares against
                // COLOR_BLACK), so a declared black must not turn every highlight off.
                _oParam[o + 7] = p[0] != 0f || p[1] != 0f || p[2] != 0f ? 1f : 0f;
            }
        }
    }

    /// <summary>Case-insensitive ASCII compare of a MaterialParameter's name field against a literal, over
    /// the 0x40 bytes the engine's own <c>strncasecmp</c> compares. Allocation-free, and cold anyway.
    /// <para>Internal rather than private since #148: <see cref="Particles"/> reads its own <c>.mtr</c>
    /// parameter off the same record layout, and two spellings of "compare a material parameter name" is two
    /// different answers to a case-folding question.</para></summary>
    internal static bool NameIs(byte* rec, string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            byte a = rec[i], b = (byte)name[i];
            if (a >= 'A' && a <= 'Z') a += 32;
            if (b >= 'A' && b <= 'Z') b += 32;
            if (a != b) return false;
        }
        return rec[name.Length] == 0;
    }

    /// <summary>Intern a diffuse-texture pointer into a dense small key for the #84 radix digit (open
    /// addressing on a Fibonacci-hashed pointer; cold, one call per enrolled part). The pointer is only ever
    /// compared, never dereferenced. Beyond <see cref="MaxMats"/> distinct textures everything shares the last
    /// key: that merely stops the sort separating them, and the draw loop's live texture-id compare still cuts
    /// the batch correctly.</summary>
    private static int TexKey(IntPtr tex)
    {
        // Fibonacci hashing takes the TOP bits of the product, so the shift is bound to the table size --
        // a literal here silently clusters every key into the first 1/N of the table when MatHash grows,
        // which is exactly what raising it from 2048 nearly did.
        int h = (int)((ulong)(long)tex * 0x9E3779B97F4A7C15UL >> (64 - MatHashBits)) & (MatHash - 1);
        for (int p = 0; p < MatHash; p++)
        {
            int j = (h + p) & (MatHash - 1);
            if (_mKey[j] == tex) return _mVal[j];
            if (_mKey[j] == IntPtr.Zero)
            {
                if (_matCount >= MaxMats) return MaxMats - 1;
                _mKey[j] = tex;
                return _mVal[j] = _matCount++;
            }
        }
        return MaxMats - 1;
    }

    /// <summary>Read the engine VAO's position, UV and normal attribute formats + buffer bindings at their
    /// FIXED engine locations (<see cref="EngPos"/>/<see cref="EngUv"/>/<see cref="EngNormal"/>, from
    /// GLRender::ConnectShaderData's bind table). A failed query (type 0) is refused rather than fed to
    /// VertexAttribPointer -- an enabled-but-unset array crashes the driver's vertex pull (#81). A VDO with
    /// no normal stream is still ownable, just never LIT (#83): lit parts on it hand themselves back to the
    /// engine rather than shade off a guessed normal.</summary>
    private static bool Harvest(IntPtr vdo, int s)
    {
        uint vao = *(uint*)(vdo + VdoVao);
        if (vao == 0) return false;
        int saved;
        Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &saved);
        Gl.BindVertexArray(vao);
        int eb; Gl.GetIntegerv(Gl.ELEMENT_ARRAY_BUFFER_BINDING, &eb);
        _vElemBuf[s] = (uint)eb;
        _vPosBuf[s] = 0; _vUvBuf[s] = 0; _vNrmBuf[s] = 0;
        HarvestOne(EngPos, s, ref _vPosBuf[s], ref _vPosSize[s], ref _vPosType[s], ref _vPosStride[s], ref _vPosOff[s]);
        HarvestOne(EngUv, s, ref _vUvBuf[s], ref _vUvSize[s], ref _vUvType[s], ref _vUvStride[s], ref _vUvOff[s]);
        HarvestOne(EngNormal, s, ref _vNrmBuf[s], ref _vNrmSize[s], ref _vNrmType[s], ref _vNrmStride[s], ref _vNrmOff[s]);
        // ...while the HARVESTED VAO is still bound: Enabled() queries the binding, not the slot, so asking
        // after the restore below would answer for whatever the engine had bound and refuse (or admit) the
        // wrong VDO -- in the admitting direction, exactly the bind-pose misrender this flag exists to stop.
        // #137: the bone streams' FORMATS, not just their presence -- the owned VAO binds them now. Both or
        // neither, for the reason on _vSkin.
        _vSkin[s] = false;
        if (_skinning)
        {
            _vBiBuf[s] = 0; _vBwBuf[s] = 0;
            HarvestOne(EngBoneIdx, s, ref _vBiBuf[s], ref _vBiSize[s], ref _vBiType[s], ref _vBiStride[s],
                       ref _vBiOff[s], ref _vBiNorm[s]);
            HarvestOne(EngBoneW, s, ref _vBwBuf[s], ref _vBwSize[s], ref _vBwType[s], ref _vBwStride[s],
                       ref _vBwOff[s], ref _vBwNorm[s]);
            _vSkin[s] = _vBiBuf[s] != 0 && _vBwBuf[s] != 0;
        }
        else _vSkin[s] = Enabled(EngBoneIdx) && Enabled(EngBoneW);
        Gl.BindVertexArray((uint)saved);
        _vLit[s] = _vNrmBuf[s] != 0 && _vNrmType[s] == Gl.FLOAT && _vNrmSize[s] == 3;
        return _vElemBuf[s] != 0 && _vPosBuf[s] != 0 && _vPosType[s] != 0 && _vUvBuf[s] != 0;
    }

    /// <summary>Is this attribute slot of the currently bound engine VAO enabled at all? The whole test
    /// <see cref="_vSkin"/> needs -- the owned pass never pulls the bone streams, it only has to know they
    /// are there.</summary>
    private static bool Enabled(uint i)
    { int en = 0; Gl.GetVertexAttribiv(i, Gl.VERTEX_ATTRIB_ARRAY_ENABLED, &en); return en != 0; }

    /// <summary>One attribute slot of the currently bound engine VAO. Leaves the outputs zeroed when the slot
    /// is disabled, which is how a missing stream is reported upward.</summary>
    private static void HarvestOne(uint i, int s, ref uint buf, ref int size, ref uint type, ref int stride, ref long off)
    { byte ignored = 0; HarvestOne(i, s, ref buf, ref size, ref type, ref stride, ref off, ref ignored); }

    /// <summary>...and the same read carrying the NORMALISED flag, which only the bone streams need: an
    /// index stream is raw and a weight stream may be normalised shorts, and getting that backwards scales
    /// every weight by 1/32767 (a mesh collapsed to the origin) or by 32767 (one flung off-screen).</summary>
    private static void HarvestOne(uint i, int s, ref uint buf, ref int size, ref uint type, ref int stride,
                                   ref long off, ref byte norm)
    {
        int en; Gl.GetVertexAttribiv(i, Gl.VERTEX_ATTRIB_ARRAY_ENABLED, &en);
        if (en == 0) return;
        int sz, st, ty, bf, nm; void* ptr;
        Gl.GetVertexAttribiv(i, Gl.VERTEX_ATTRIB_ARRAY_SIZE, &sz);
        Gl.GetVertexAttribiv(i, Gl.VERTEX_ATTRIB_ARRAY_STRIDE, &st);
        Gl.GetVertexAttribiv(i, Gl.VERTEX_ATTRIB_ARRAY_TYPE, &ty);
        Gl.GetVertexAttribiv(i, Gl.VERTEX_ATTRIB_ARRAY_BUFFER_BINDING, &bf);
        Gl.GetVertexAttribiv(i, Gl.VERTEX_ATTRIB_ARRAY_NORMALIZED, &nm);
        Gl.GetVertexAttribPointerv(i, Gl.VERTEX_ATTRIB_ARRAY_POINTER, &ptr);
        if (ty == 0 || bf == 0) return;
        buf = (uint)bf; size = sz; type = (uint)ty; stride = st; off = (long)ptr; norm = (byte)nm;
    }

    /// <summary>The owned opaque block (hot, zero-alloc): view*proj off the matrix stacks and the mirror-FBO
    /// inputs once, then -- inside the #80 bracket -- <see cref="BuildBatches"/> revalidates every owned part
    /// and stages its draw (texture id re-fetched every frame per the borrowed-pointer rule, which is also what
    /// keeps animated textures cycling), and <see cref="IssueBatches"/> submits the result as one multidraw per
    /// (program, VDO, texture, primitive) group. Anything that fails revalidation falls straight back to the
    /// engine walk.</summary>
    private static void DrawOwned()
    {
        // #129: last pass's snapshot is not this pass's. Revoked first thing, so every path out of here that
        // never reaches PassId.FbCopy leaves owned water disarmed -- and BatchService's bucket-6 gate reads
        // the same flag, so the engine draws water itself on exactly those passes.
        WaterPass.Revoke();
        if (_progFailed || _batchUnavailable) return;   // EndFrame revokes + disarms; never pay the bracket for nothing
        // Checked BEFORE anything stamps OwnGen: BuildBatches marks parts as drawn, which is only honest while
        // the issue phase cannot fail. Missing entry points are the one way it could.
        if (Gl.MultiDrawElementsBaseVertex == null || Gl.BindBufferBase == null) { _batchUnavailable = true; return; }

        int* viewport = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, viewport);
        int engFbo; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &engFbo);
        int colorType = 0, colorName = 0, depthType = 0, depthName = 0;
        if (engFbo != 0)
        {
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE, &colorType);
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME, &colorName);
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE, &depthType);
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME, &depthName);
        }
        bool srgb = Gl.IsEnabled(Gl.FRAMEBUFFER_SRGB) != 0;
        // #151: settle the AA question with the actual FBO topology, once. The user sees "MSAA on" in the
        // game options while the owned frame draws into PostChain's single-sample RGBA16F target -- whether
        // the ENGINE's world FBO is itself multisampled here decides where anti-aliasing has to be put back.
        if (engFbo != 0 && (engFbo ^ colorName << 8 ^ depthName << 16 ^ colorType << 24 ^ depthType << 28) != _fboDiagSig && _fboDiagLeft > 0)
        {
            // re-log on CHANGE, not once: the first anchor fires on the menu/loading FBO, and the in-game
            // world FBO is the one the AA question is actually about. Capped so a ping-ponging engine
            // cannot spam the log.
            _fboDiagSig = engFbo ^ colorName << 8 ^ depthName << 16 ^ colorType << 24 ^ depthType << 28;
            _fboDiagLeft--;
            int cs = AttachmentSamples(colorType, colorName), ds = AttachmentSamples(depthType, depthName);
            int ctxSamples = 0; Gl.GetIntegerv(Gl.SAMPLES, &ctxSamples);
            _log?.Info($"OwnedDrawService: FBO topology -- engine fbo {engFbo}, colour {TypeName(colorType)} " +
                       $"{colorName} ({cs}x), depth {TypeName(depthType)} {depthName} ({ds}x), " +
                       $"GL_SAMPLES here {ctxSamples}; the owned HDR target is 1x by construction, so owned " +
                       $"passes are anti-aliased only if the engine attachment they END UP in is multisampled");
        }
        // #132: the engine's clear colour, read HERE and nowhere else. A GL share list shares objects, not
        // per-context state, so glClearColor cannot be queried from inside the 4.6 bracket -- that context
        // has its own, and PostChain's target would clear to it. This is the value the engine's own Clear(7)
        // just used at the anchor.
        float* engClear = stackalloc float[4] { 0f, 0f, 0f, 1f };
        if (Gl.GetFloatv != null) Gl.GetFloatv(Gl.COLOR_CLEAR_VALUE, engClear);
        // ...and the refusals that need no bracket at all, ahead of the preamble they would otherwise
        // waste -- PoseSkinned alone force-solves every owned skinmesh through the engine.
        if (!PostChain.Precheck(engFbo, depthName, viewport)) return;
        // Only now: posing force-solves every owned skinmesh through the engine, and a pass that is about
        // to hand the frame back would have the engine solve each of them a second time on its own walk.
        // Still ENGINE context, which is what PartSkin::Draw needs.
        PoseSkinned();

        // row-major v*M convention: clip = v * Model * View * Proj; raw bytes read column-major by GLSL are
        // the transpose, so uVP * d.model * vec4(aPos,1) is exact -- (M*V*P)^T = (V*P)^T * M^T, which is why
        // #84 can hoist view*proj to a per-program uniform and leave only the model matrix per draw.
        _sceneEnv = _enrollScene != IntPtr.Zero && *(IntPtr*)(_enrollScene + SceneEnvMap) != IntPtr.Zero;

        Matrix4x4 view = *(Matrix4x4*)(_mtxStacks + ModeView * StackStride + StackTop);
        Matrix4x4 proj = *(Matrix4x4*)(_mtxStacks + ModeProjection * StackStride + StackTop);
        Matrix4x4 vp = view * proj;
        // every froxel input comes off THIS pass's projection, never the capture side (see TryPerspective)
        bool perspective = OwnedLights.TryPerspective((float*)&proj, out float p00, out float p11,
                                                      out float near, out float far);
        // The scene camera, for the shadow pass (#99). Same row-vector convention as above, so the inverse
        // view's translation IS the eye and view-space -Z rotated by it is the look direction. Gated on
        // TryPerspective, which an ortho/UI pass fails outright.
        if (perspective && Matrix4x4.Invert(view, out Matrix4x4 camToWorld))
        {
            _eyePos = camToWorld.Translation;   // #120: this pass's eye, for the specular view vector
            _lodScale = proj.M22 * viewport[3] * 0.5f;
            Shadow.ShadowInputs.LatchSceneCam(camToWorld.Translation,
                Vector3.Normalize(Vector3.TransformNormal(new Vector3(0f, 0f, -1f), camToWorld)),
                p11, viewport[2] * viewport[3], _frameSeq);
        }
        // Cap 5 is the engine's own lit predicate (Ghidra MatStatic @0x450560 / RenderFlat @0x450eb0): with
        // it off every part gets flat white, so the flat variant IS vanilla for this pass.
        bool lightingOn = _litCount > 0 && _lighting && !_litFailed && perspective
                          && (_isEnabled == null || _isEnabled(CapLighting) != 0);

        if (!GlContextService.Enter()) return;
        if (EnsureProgram() && BindTarget(engFbo, colorType, colorName, depthType, depthName))
        {
            // #117/#119: the lighting rig, the GL state and the batch BUILD are bracket preamble -- both
            // colour-bracket passes draw the same staged batches, and building twice would revalidate (and
            // re-stamp OwnGen for) every owned part twice. The passes come off FrameGraph, not off call order.
            if (lightingOn)
            {
                OwnedLights.Upload(_frameSeq);
                // The cluster buffers are only meaningful once THIS pass's build has run: a skipped build
                // leaves them holding another camera's binning (or, before the first dispatch, undefined
                // BufferData contents), and a lit part would then sample arbitrary light indices.
                lightingOn = OwnedLights.Build((float*)&view, p00, p11, near, far)
                             && _progLit46 != 0;
            }
            Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
            if (srgb) Gl.Enable(Gl.FRAMEBUFFER_SRGB); else Gl.Disable(Gl.FRAMEBUFFER_SRGB);
            Gl.Enable(Gl.DEPTH_TEST); Gl.DepthMask(1);
            Gl.Disable(Gl.BLEND); _blendOn = false;
            Gl.Disable(Gl.CULL_FACE); Gl.Disable(Gl.SCISSOR_TEST);
            Gl.ActiveTexture(Gl.TEXTURE0);
            _lastPixels = viewport[2] * viewport[3];
            // #129: built HERE, not at the pass, because staging has to know. A water part staged for a pass
            // whose program never built would be stamped as owned (and so suppressed in the engine's walk)
            // and then never drawn -- so the decision is made before BuildBatches, which skips the class
            // outright when it is false.
            // ...and on the LIGHT RIG, for a reason the class check cannot supply: the water stage calls
            // SowxClusteredLight unconditionally, and the cluster buffers are only meaningful once THIS
            // pass's build has run. BuildBatches skips lit batches on !lightingOn, but a water batch is
            // keyed on its class rather than on _gLit, so without this term SOWX_OWNLIGHT=0 (or a failed
            // lit program, or a build that refused) would leave the water program reading undefined
            // BufferData contents -- an unbounded per-fragment light loop, not a dim sea.
            _waterArmed = WaterPass.Usable && lightingOn && EnsureWaterProgram();
            _waterDrew = false;
            // #139/#132: from here to PassId.PostProcess the colour passes draw into an RGBA16F target that
            // shares the engine's depth, and everything they write is LINEAR. Asked BEFORE anything is
            // staged, because a refusal is no longer survivable: there is no display-space variant of the
            // owned stages to fall back to, so the whole pass stands down and the engine renders it vanilla.
            // Staging first would stamp OwnGen on parts the iterator then suppresses and nobody draws.
            if (!PostChain.Begin(_fbo46, depthType, depthName, viewport, engClear))
            { GlContextService.Leave(); return; }
            int groups = BuildBatches(lightingOn);
            if (groups > 0)
            {
                StageSky();
                UploadDraws();
                UploadBones();
                foreach ((PassId id, Bracket bracket) in FrameGraph.Passes)
                {
                    if (bracket != Bracket.Colour) continue;
                    switch (id)
                    {
                        case PassId.DepthPrepass:
                            DepthPrepassPass(_gOpaqueEnd, &vp, viewport, depthType, depthName);
                            break;
                        case PassId.OpaqueOwned:
                            OpaqueOwnedPass(_gOpaqueEnd, &vp, viewport, near, far);
                            // The GPU-side cluster stats belong to the SCREEN's build. Harvested here rather
                            // than after the bracket because the probe capture rebuilds the clusters once per
                            // face, and reading afterwards would report cube face 6's overflow as the frame's.
                            if (_takeOverflow)
                            { _takeOverflow = false; _clusterOverflow += OwnedLights.TakeClusterOverflow(); }
                            break;
                        case PassId.GrassOwned:
                            bool tg = _qSkyGrass.Begin(Gl.TIME_ELAPSED);
                            GrassField.Draw(&vp, viewport, near, far, lightingOn);
                            if (tg) _qSkyGrass.End(Gl.TIME_ELAPSED);
                            // Grass draws unblended by construction and toggles GL_BLEND itself; hand the
                            // tracked state back exactly as it was, or the next pass's own bookkeeping
                            // (IssueBatches reads _blendOn as "what is already set") starts from a lie.
                            if (_blendOn) Gl.Enable(Gl.BLEND); else Gl.Disable(Gl.BLEND);
                            break;
                        case PassId.SkyOwned:
                            SkyOwnedPass(&vp, viewport);
                            break;
                        case PassId.BlendedOwned:
                            TransparentPass(_gOpaqueEnd, _gBlendEnd, &vp, viewport, near, far);
                            break;
                        case PassId.FbCopy:
                            _waterTiming = _qWater.Begin(Gl.TIME_ELAPSED);
                            // Only when there is water to read it. The copy is a full-screen blit of colour
                            // AND depth -- cheap, but not free, and every frame without a water surface on
                            // screen would be paying for a texture nothing samples.
                            if (_gWaterEnd > _gBlendEnd && !_capturing)
                            {
                                // The blended class left GL_BLEND on and the depth mask off; the blit does
                                // not care, but leaving the tracked state honest does -- IssueBatches below
                                // reads _blendOn as "what is already set".
                                WaterPass.Copy(viewport);
                            }
                            break;
                        case PassId.WaterOwned:
                            WaterOwnedPass(_gBlendEnd, _gWaterEnd, &vp, (float*)&view, (float*)&proj,
                                           viewport, near, far);
                            if (_waterTiming) { _qWater.End(Gl.TIME_ELAPSED); _waterTiming = false; }
                            break;
                        case PassId.AdditiveOwned:
                            TransparentPass(_gWaterEnd, groups, &vp, viewport, near, far);
                            if (_colourTiming) { _qColour.End(Gl.TIME_ELAPSED); _colourTiming = false; }
                            // The blending classes leave the depth mask off and GL_BLEND on; the probe
                            // capture that follows draws the opaque set again and must find neither.
                            Gl.Disable(Gl.BLEND); _blendOn = false; Gl.DepthMask(1);
                            break;
                        case PassId.BeamsOwned:
                            // Beams bind a program and a VAO of their own and hand the state back exactly as
                            // the additive arm left it; nothing here tracks _blendOn on their behalf.
                            Beams.Draw(&view, &proj);
                            break;
                        case PassId.ParticlesOwned:
                            // Particles bind a program, a VAO and a buffer of their own and hand the state
                            // back the way the beams left it -- blend off, depth mask on, culling off.
                            bool tp = _qParticles.Begin(Gl.TIME_ELAPSED);
                            Particles.Draw(&view, &proj, viewport, near, far, lightingOn);
                            if (tp) _qParticles.End(Gl.TIME_ELAPSED);
                            break;
                        case PassId.ProbeCapture:
                            if (!lightingOn) break;
                            CaptureProbe(_gOpaqueEnd, far);
                            // The capture bound its own framebuffer and viewport; whatever runs after the
                            // bracket must find the engine's target again, not a 128px cube face.
                            BindTarget(engFbo, colorType, colorName, depthType, depthName);
                            Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
                            if (srgb) Gl.Enable(Gl.FRAMEBUFFER_SRGB); else Gl.Disable(Gl.FRAMEBUFFER_SRGB);
                            break;
                        case PassId.PostProcess:
                            if (!PostChain.Armed) break;
                            bool tb = _qPost.Begin(Gl.TIME_ELAPSED);
                            // Bloom runs against the HDR target and binds its own attachments; the engine's
                            // target has to come back before the composite writes into it, and with it the
                            // viewport and the sRGB-encode state the pass was entered with.
                            PostChain.Bloom();
                            BindTarget(engFbo, colorType, colorName, depthType, depthName);
                            if (srgb) Gl.Enable(Gl.FRAMEBUFFER_SRGB); else Gl.Disable(Gl.FRAMEBUFFER_SRGB);
                            PostChain.Composite(viewport);
                            if (tb) _qPost.End(Gl.TIME_ELAPSED);
                            break;
                    }
                }
            }
            Gl.BindVertexArray(0);
        }
        GlContextService.Leave();
    }

    /// <summary>ADR 0011 step 1 (#119): the owned opaque set again, depth-only and colour-masked, so early-Z
    /// throws away the fragments the shading pass would otherwise light and then overwrite. Every owned part
    /// is opaque, so the set qualifies -- with two exclusions, both #123's:
    /// <list type="bullet">
    /// <item>a batch whose feature DISPLACES vertices (wind sway, roof toggle). This stage does not displace,
    /// so its depth is the part's rest silhouette, and the colour pass's LEQUAL would then kill real
    /// fragments -- a roof offset by 500m fails everywhere, and whatever stands behind a swaying plant dies
    /// at the plant's rest outline. Note <see cref="DepthSafe"/> cannot catch this: it is a TEXT check, and
    /// the displacing stage still contains <see cref="ClipExpr"/> verbatim on its undisplaced path.</item>
    /// <item>the roof anchor discard is a fragment discard, and those punch holes the same way -- but it only
    /// exists on a roof, which the first exclusion already removed.</item>
    /// </list>
    /// The cutout class the ADR makes a measurement question is still not owned.
    /// <para>Publishes <see cref="DepthResource"/> on the way out: the buffer is the deliverable, not a
    /// private optimisation.</para></summary>
    private static void DepthPrepassPass(int groups, Matrix4x4* vp, int* viewport, int depthType, int depthName)
    {
        if (!_depthPrepass || !EnsureDepthProgram()) return;
        int skipped = 0, drawn = 0;
        bool timing = _qPrepass.Begin(Gl.TIME_ELAPSED);
        Gl.ColorMask(0, 0, 0, 0);
        Gl.DepthFunc(Gl.LESS);
        Gl.UseProgram(_progDepth);
        Gl.UniformMatrix4fv(_depthVp, 1, 0, (float*)vp);
        // #124: dissolving batches run the keyhole stage instead, so they stay IN the prepass. Groups are
        // already keyed on the dissolve bit, so this switches at most once per pass in practice; `keyed`
        // tracks which program is current so the plain batches do not re-bind theirs on every alternation.
        bool keyed = false;
        int boundVao = -1;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        for (int g = 0; g < groups; g++)
        {
            if (ShaderFeatures.OutOfPrepass(_gFeat[g])) { skipped += _gLen[g]; continue; }
            // #126: a cutout discards, so its prepass depth would be its SOLID silhouette and the colour
            // pass's LEQUAL would cull whatever stands in its holes -- grass turning the ground behind it
            // into a rectangle. ADR 0011 leaves cutouts-in-the-prepass a MEASUREMENT question (they cost a
            // texture fetch per fragment to save shading later); this is the measurement's off side, and
            // _prepassSkipped is what says how much overdraw it leaves on the table.
            if (_gCls[g] == (byte)OwnedClass.Cutout) { skipped += _gLen[g]; continue; }
            bool dis = _gDis[g];
            if (dis && _progDepthKey == 0) { skipped += _gLen[g]; continue; }
            if (dis != keyed)
            {
                keyed = dis;
                Gl.UseProgram(dis ? _progDepthKey : _progDepth);
                Gl.UniformMatrix4fv(dis ? _depthKeyVp : _depthVp, 1, 0, (float*)vp);
                if (dis) UploadFeatureTerms(_depthKeyFeat, viewport);
            }
            int s = _gStart[g];
            if (keyed) _prepassKeyed += _gLen[g];
            Gl.Uniform1i(keyed ? _depthKeyBase : _depthBase, s);
            if (_gVao[g] != boundVao) { Gl.BindVertexArray(_vVao46[_gVao[g]]); boundVao = _gVao[g]; }
            Gl.MultiDrawElementsBaseVertex(_gMode[g], counts + s, GlUnsignedShort,
                                           (void**)(offs + s), _gLen[g], bases + s);
            drawn += _gLen[g];
        }
        Gl.ColorMask(1, 1, 1, 1);
        if (timing) _qPrepass.End(Gl.TIME_ELAPSED);
        // Counted from what this loop submitted, not from the pass's total draw count: since #126 that total
        // spans the blending classes too, and the prepass walks the opaque range alone.
        _prepassDraws += drawn;
        _prepassSkipped += skipped;
        DepthResource.Publish(depthType, depthName, viewport, PassGen);
    }

    /// <summary>ADR 0011 step 2, the owned opaque set: submit the staged batches, shaded.
    /// <para>LEQUAL rather than EQUAL against the prepass depth. All three owned vertex stages declare
    /// <c>invariant gl_Position</c> over the identical clip expression (<see cref="ClipExpr"/>), so equal is
    /// what they produce -- but LEQUAL is also correct when the prepass stood down, which EQUAL is not, and
    /// early-Z rejects the hidden fragments either way.</para></summary>
    private static void OpaqueOwnedPass(int groups, Matrix4x4* vp, int* viewport, float near, float far)
    {
        Gl.DepthFunc(Gl.LEQUAL);
        // SAMPLES_PASSED covers the OPAQUE set alone, so the overdraw figure stays comparable with #119's
        // baseline; the colour timer is ended after the blending classes (see DrawOwned), because "what does
        // the owned colour work cost" is the question the [owned] line exists to answer.
        bool counting = _qSamples.Begin(Gl.SAMPLES_PASSED);
        _colourTiming = _qColour.Begin(Gl.TIME_ELAPSED);
        IssueBatches(0, groups, (float*)vp, viewport, near, far);
        if (counting) _qSamples.End(Gl.SAMPLES_PASSED);
    }

    /// <summary>Double-buffered GPU query: begin on the slot whose result has already been harvested, so the
    /// readback never stalls the pass waiting for a result the GPU has not produced.
    /// <para>Harvesting happens in BEGIN, not in End, and unconditionally. End only runs when Begin took the
    /// pass, so harvesting there wedges the meter the first time both slots are pending at once -- one heavy
    /// frame with the GPU two frames behind, or a third owned pass in a frame -- and it never reports again
    /// for the session. Same rule as <c>OwnedLights.Build</c>, for the same reason.</para></summary>
    internal struct GpuQuery
    {
        private uint _q0, _q1;
        private bool _p0, _p1;
        private int _at;
        private ulong _sum;
        private int _n;

        internal bool Begin(uint target)
        {
            if (Gl.GenQueries == null || Gl.GetQueryObjectui64v == null || Gl.GetQueryObjectiv == null) return false;
            if (_q0 == 0) { uint* q = stackalloc uint[2]; Gl.GenQueries(2, q); _q0 = q[0]; _q1 = q[1]; }
            if (_q0 == 0 || _q1 == 0) return false;
            Harvest(_q0, ref _p0);
            Harvest(_q1, ref _p1);
            if (_at == 0 ? _p0 : _p1) return false;
            Gl.BeginQuery(target, _at == 0 ? _q0 : _q1);
            return true;
        }

        internal void End(uint target)
        {
            Gl.EndQuery(target);
            if (_at == 0) _p0 = true; else _p1 = true;
            _at = 1 - _at;
        }

        private void Harvest(uint q, ref bool pending)
        {
            if (!pending) return;
            int ready; Gl.GetQueryObjectiv(q, Gl.QUERY_RESULT_AVAILABLE, &ready);
            if (ready == 0) return;
            ulong v; Gl.GetQueryObjectui64v(q, Gl.QUERY_RESULT, &v);
            _sum += v; _n++; pending = false;
        }

        /// <summary>Period mean, 0 when nothing was harvested (which reads as "no measurement", not "free").</summary>
        internal double Avg => _n == 0 ? 0.0 : (double)_sum / _n;
        internal bool Measured => _n > 0;
        internal void Reset() { _sum = 0; _n = 0; }
    }

    /// <summary>#126: this pass's draw ORDER, ahead of the staging walk. Three things it settles that the
    /// enrollment order cannot: a tile that started fading since the last enrollment leaves the opaque set
    /// for the blended one (bucket 8 is a per-frame fact, not a material one), the blended run is sorted
    /// back-to-front from THIS pass's eye, and every entry carries the alpha multiplier its draw needs.
    /// <para>The sort is the only one in the frame, and it is paid by the blended remainder alone -- which is
    /// the whole point of the cutout/blended split: the cutout majority is order-independent and stays in the
    /// texture-grouped opaque order.</para>
    /// <para>ponytail: insertion sort, i.e. O(n^2) in the blended count. That count is the small minority of
    /// a scene by construction, and the alternative (a radix over quantised depth) is a bucket table to size
    /// and clear per pass. If a scene ever lands enough blended parts to show on the [owned] line's colour
    /// time, that is the upgrade.</para></summary>
    private static int RouteParts()
    {
        int n = 0, nb = 0;
        int opaqueEnd = _clsEnd[(int)OwnedClass.Cutout];   // classes are contiguous: opaque then cutout
        for (int k = _clsStart[(int)OwnedClass.Opaque]; k < opaqueEnd; k++)
        {
            int i = _order[k];
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) { W(i, "skipped: slot vacated (revoked or class dropped)"); continue; }
            // A part whose identity no longer matches is a recycled (or freed) pointer, and the reads below
            // CHASE one -- part+0x98 to the TileData, then its fade factor. The staging walk revalidates and
            // revokes; this route only has to not dereference garbage before it does, so a mismatch is left
            // in the opaque run for BuildBatches to throw out.
            if (!IdentityOk(i, tri)) { _pass[n] = i; _passCls[n] = _oCls[i]; _passAlpha[n] = 1f; n++; continue; }
            // The fade overlay, and the part's own animated alpha with it: while its tile fades, an opaque
            // or cutout part IS a bucket-8 part -- the engine folds the tile's factor into the part's own
            // alpha for the length of the draw (RenderDrawBucket @0x46d060, bucket 8 only) and draws it
            // blended. So does this pass. Reading the part alpha live here (rather than trusting the class
            // from the last enrollment) is also what keeps a part whose alpha animated below 1 from standing
            // solid for up to an EnrollEvery cadence.
            float alpha = LiveAlpha(tri);
            if (alpha >= 1f) { _pass[n] = i; _passCls[n] = _oCls[i]; _passAlpha[n] = 1f; n++; continue; }
            _ownFade++;
            _blend[nb] = i; _blendAlpha[nb] = alpha; _blendKey[nb] = EyeDist2(i); nb++;
        }
        for (int k = _clsStart[(int)OwnedClass.Blended]; k < _clsEnd[(int)OwnedClass.Blended]; k++)
        {
            int i = _order[k];
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) continue;
            _blend[nb] = i;
            // ...and the sort key is skipped too on a failed identity: EyeDist2 reads the enrolled NODE's
            // matrix for a matrix-class part, and the staging walk is about to revoke this entry anyway, so
            // where it would have sorted does not matter.
            bool live = IdentityOk(i, tri);
            _blendAlpha[nb] = live ? LiveAlpha(tri) : 1f;
            _blendKey[nb] = live ? EyeDist2(i) : 0f; nb++;
        }
        SortBlended(_blend, _blendKey, _blendAlpha, nb);
        for (int a = 0; a < nb; a++)
        { _pass[n] = _blend[a]; _passCls[n] = (byte)OwnedClass.Blended; _passAlpha[n] = _blendAlpha[a]; n++; }
        // #129: water, unsorted. ponytail: a per-part back-to-front sort is skipped because water planes are
        // per-tile, coplanar and depth-writing -- they cannot overlap each other in a way a sort would fix.
        // Add one if a map ever stacks a waterfall sheet in front of a pool.
        for (int k = _clsStart[(int)OwnedClass.Water]; k < _clsEnd[(int)OwnedClass.Water]; k++)
        {
            int i = _order[k];
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) continue;
            _pass[n] = i; _passCls[n] = (byte)OwnedClass.Water;
            _passAlpha[n] = IdentityOk(i, tri) ? LiveAlpha(tri) : 1f;
            // #147: measured here rather than at enroll -- routing is where a water part is certainly live
            // and certainly has its node, and the registry ignores one it already holds.
            WaterBodies.Note(tri, _oNode[i]);
            n++;
        }
        for (int k = _clsStart[(int)OwnedClass.Additive]; k < _clsEnd[(int)OwnedClass.Additive]; k++)
        {
            int i = _order[k];
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) continue;
            _pass[n] = i; _passCls[n] = (byte)OwnedClass.Additive;
            // Additive fades too: the engine routes an additive part on a fading tile into bucket 8 like any
            // other, and IterSight suppresses it there, so a glow left at full brightness through a fade
            // would be ours to explain.
            _passAlpha[n] = IdentityOk(i, tri) ? LiveAlpha(tri) : 1f;
            n++;
        }
        return n;
    }

    /// <summary>Is this slot's part still the one that enrolled? The same single-level compare
    /// <see cref="BuildBatches"/> revokes on -- node, material and texture rotation read straight off the
    /// part. Reading a stale part is the accepted risk model here; chasing a POINTER out of one is not, so
    /// every route-time dereference sits behind this.
    /// <para>#128: pointer identity ONLY. An instance's live shading state used to be folded in here, which
    /// was the wrong place twice over -- a false answer left the part in the OPAQUE run at alpha 1 (drawn
    /// wrong, not handed back) and the staging walk never re-asked. It is a skip of its own now, in
    /// <see cref="BuildBatches"/>.</para></summary>
    /// <summary>#145: ...and the entry must still be a LIVE recording of this part before any of that is
    /// read. The three compares below are reads of the part itself, which is only sound while the part
    /// exists -- a destroyed one can keep every field long enough to pass them, and then the gob chase in
    /// <see cref="LiveAlpha"/> walks a freelist word. <c>Part::~Part</c> is what makes this answer no.</summary>
    private static bool IdentityOk(int i, IntPtr tri)
        => Perf.BatchService.EntryLive(_oEntry[i], tri)
           && *(IntPtr*)(tri + PartNode) == _oNode[i] && *(IntPtr*)(tri + PartMaterial) == _oMat[i]
           && *(int*)(tri + PartTexRotate) == _oRotBits[i];

    /// <summary>#126: order the blended run back to front -- the farthest key first, so nearer surfaces
    /// composite over what is already behind them. Stable, which is what keeps parts at equal distance in
    /// their texture-grouped enrollment order rather than shuffling them from pass to pass.</summary>
    private static void SortBlended(int[] idx, float[] key, float[] alpha, int n)
    {
        for (int a = 1; a < n; a++)
        {
            int i = idx[a]; float k = key[a], al = alpha[a];
            int b = a - 1;
            for (; b >= 0 && key[b] < k; b--)
            { idx[b + 1] = idx[b]; key[b + 1] = key[b]; alpha[b + 1] = alpha[b]; }
            idx[b + 1] = i; key[b + 1] = k; alpha[b + 1] = al;
        }
    }

    /// <summary>Off-device: the sort really is back-to-front, really is stable, and really carries each
    /// entry's alpha with its index -- a permutation that lost the alpha pairing would draw every blended
    /// part at its neighbour's transparency, which reads as "the sort is wrong" and is not.</summary>
    private static bool SortSelfCheck(ExtenderLog log)
    {
        int[] idx = { 10, 11, 12, 13 };
        float[] key = { 1f, 9f, 5f, 9f }, alpha = { 0.1f, 0.2f, 0.3f, 0.4f };
        SortBlended(idx, key, alpha, 4);
        bool ok = idx[0] == 11 && idx[1] == 13 && idx[2] == 12 && idx[3] == 10   // stable within the 9f tie
                  && key[0] == 9f && key[1] == 9f && key[2] == 5f && key[3] == 1f
                  && alpha[0] == 0.2f && alpha[1] == 0.4f && alpha[2] == 0.3f && alpha[3] == 0.1f;
        if (!ok) log.Info("OwnedDrawService.SortSelfCheck: FAILED -- the blended order is not back-to-front");
        return ok;
    }

    /// <summary>The part's own alpha multiplier (part+0xa0), live: it is animated state, not enrollment
    /// state.</summary>
    private static float PartAlpha(IntPtr tri) => *(float*)(tri + PartAlphaOff);

    /// <summary>#128: this frame's whole alpha multiplier for a part -- its own animated alpha, the fade
    /// factor of the tile it stands on, and the instance's two multiplies. Every term is read live and none
    /// is cached at capture, which is what the creature case demands: a gob can fade a whole creature into
    /// the transparent run for a single frame and out again.</summary>
    private static float LiveAlpha(IntPtr tri)
        => PartAlpha(tri) * Perf.BatchService.TileFadeFactor(tri) * Perf.BatchService.GobAlphaMul(tri);

    /// <summary>Squared distance from THIS pass's eye to the part's world centre -- the blended sort key.
    /// Derived exactly as the caster cull derives it and for the same reason (live-verified there): a
    /// matrix-class part's centre is its model matrix translation, and everything else is world-space
    /// geometry whose bbox centre already is the answer.</summary>
    private static float EyeDist2(int i)
    {
        float cx = _oCx[i], cy = _oCy[i], cz = _oCz[i];
        if (_oMx[i])
        {
            float* m = (float*)(_oNode[i] + NodeMatrix);
            cx = m[12]; cy = m[13]; cz = m[14];
        }
        float dx = cx - _eyePos.X, dy = cy - _eyePos.Y, dz = cz - _eyePos.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Phase A of the owned block: walk this pass's order (<see cref="RouteParts"/>), revalidate
    /// every part, and stage the survivors into the multidraw argument arrays plus one per-draw SSBO record
    /// each -- cutting a batch whenever the (class, program, VDO, texture, primitive, blend) key changes.
    /// Primitive mode is in the key because one multidraw carries a single mode; the sort does not group by
    /// it, so a mixed-mode VDO just yields more batches. Returns the batch count, leaving the draw count in
    /// <see cref="_drawN"/> and the per-class batch ranges in <see cref="_gOpaqueEnd"/>/<see cref="_gBlendEnd"/>.
    /// <para>OwnGen is stamped here, which is only honest because the issue phase cannot fail per part: the
    /// entry-point guard ran in <see cref="DrawOwned"/> and EnsureVao ran here.</para></summary>
    private static int BuildBatches(bool lightingOn)
    {
        int groups = 0, n = 0;
        int keyVao = -1, keyTex = -1, keyFeat = -1, keyCls = -1, keyBlend = -1;
        uint keyMode = 0;
        bool keyLit = false, keyDis = false;
        int routed = RouteParts();
        // #134: routing walks the CLASS RANGES, not the ownership table -- a part whose class range was
        // never built simply never appears, and no skip site can report on it. Say so once.
        if (_watchDrawLeft > 0)
        {
            bool routedWatch = false;
            for (int k = 0; k < routed && !routedWatch; k++) routedWatch = Perf.BatchService.WatchedEntry(_oEntry[_pass[k]]);
            if (!routedWatch)
            {
                bool owned = false;
                for (int i = 0; i < _ownCount && !owned; i++)
                    owned = _oTri[i] != IntPtr.Zero && Perf.BatchService.WatchedEntry(_oEntry[i]);
                if (owned) { _watchDrawLeft--; _log?.Info("[dumpframe] DRAW never routed -- owned, but RouteParts did not include it"); }
            }
        }
        _gOpaqueEnd = _gBlendEnd = _gWaterEnd = 0;
        // #120: this part's live slot-1..5 tids. Declared ONCE, outside the loop -- a stackalloc per
        // iteration grows the frame by the part count.
        int* slotTid = stackalloc int[MapSlots];
        fixed (float* data = _dData)
        for (int k = 0; k < routed; k++)
        {
            int i = _pass[k];
            int cls = _passCls[k];
            // #129: never stamp what the frame cannot draw -- see _waterArmed. Ahead of the stale check
            // below because it is a decision about the PASS, not about this part.
            if (cls == (int)OwnedClass.Water && !_waterArmed) continue;
            IntPtr tri = _oTri[i];
            if (tri == IntPtr.Zero) continue;
            // Geometry the engine has stopped walking -- culled by its own visibility, while ownership
            // (rebuilt every EnrollEvery frames) still holds it. Drawing it was ~44% of the owned draw count
            // live, all of it invisible, and it is exactly what the one-owner audit was reporting as
            // UNMATCHED: not a double draw, a ghost. The engine's own sighting window is the predictor, and
            // a part that comes back simply goes unstamped for one frame and is drawn by the engine.
            if (!Perf.BatchService.SightedRecently(_oEntry[i]))
            {
                _skipUnseen++; _unseenBucket[_oBucket[i]]++;
                W(i, "skipped: engine-culled");
                continue;
            }
            // Lit parts sort last. One with the lighting rig unavailable this pass is skipped, not painted
            // flat -- the engine's own walk then draws it, which is its vanilla look.
            bool lit = _oLit[i];
            if (lit && !lightingOn) { _skipNoLight++; W(i, "skipped: lighting off this pass"); continue; }
            // identity + dynamic state: a recycled pointer, a re-rotated tile, a fading or
            // keyhole-dissolving part is not ours this frame -- the engine's own loop body draws it
            if (!IdentityOk(i, tri)) { _skipIdentity++; W(i, "skipped: identity changed -- revoked"); Revoke(i); continue; }
            // #128: instance state the owned programs cannot express -- a highlight, a scene tint, a per-gob
            // env override. SKIPPED, not revoked: a highlight lasts exactly as long as the cursor sits on the
            // placeable, and revoking would spend a whole re-enrollment on a hover. Unstamped means the
            // iterator does not suppress it, so the engine's own walk draws it highlighted, which is vanilla.
            if (!Perf.BatchService.InstanceOwnable(tri))
            { _skipGob++; W(i, "skipped: instance state unownable this pass"); continue; }
            // #152 colour LOD. Stamped THEN skipped: the stamp suppresses the engine's own draw, so the part
            // is rendered by nobody -- handing it back would just move the cost, not remove it. Water is
            // exempt (#129's stamp invariant: a stamped water part must be one the copy machinery drew), and
            // a zero radius means "bounds unknown", never "small".
            // TILES are exempt too (TileData* at part+0x98, the CheckFadePlane discriminator): a small tile
            // part is a patch of ground or wall, and culling it is a HOLE in a surface. Free-standing
            // clutter -- placeables, gobs, decor, everything with no tile record -- pops out at a couple of
            // pixels, which is the artifact-free half of the trade. This is what makes SOWX_LOD_PX safe to
            // raise: the tileset shell of the area stays whole at any threshold.
            if (_lodPx > 0f && cls != (int)OwnedClass.Water && _oR[i] > 0f
                && *(IntPtr*)(tri + PartTile) == IntPtr.Zero)
            {
                float d2 = EyeDist2(i);
                float px = _oR[i] * _lodScale;
                if (px * px < _lodPx * _lodPx * d2)
                { _lodSkipped++; Perf.BatchService.OwnGen[_oEntry[i]] = PassGen; W(i, "skipped: sub-pixel LOD"); continue; }
            }
            // #126: a fading tile is no longer a reason to hand the part back. RouteParts has already moved
            // it into the blended run with the tile's factor folded into its alpha, which is what the engine
            // does with it in bucket 8 -- and bucket 8 is a bucket IterSight can suppress now.
            // #124: the camera dissolve is a FEATURE of the owned program now, not a reason to hand the part
            // back -- dissolving parts sit in bucket 1, which IterSight can suppress. #123's exemption still
            // stands: nokeyhole, roofkn and flowmap compile their FRAGMENT stage with KEYHOLING 0, so vanilla
            // draws them solid through a dissolve and so must we.
            // ...and only on a LIT part: every unlit fragment stage in base_shaders.bif compiles KEYHOLING 0
            // (fst, fst_sm, fsglu, fs, fstc), so vanilla draws an unlit part solid through the aperture.
            // Keeping the flag off them also keeps them out of the prepass exclusion below, which they would
            // otherwise pay for a discard their stage does not have.
            float dissolve = lit && !ShaderFeatures.NoKeyholing((int)_oFeat[i * 4])
                             && Perf.BatchService.KeyholeDissolves(tri) ? 1f : 0f;
            if (dissolve != 0f) _ownDissolve++;
            // MatStatic (Ghidra @0x450560): ambient = node+0xf4, diffuse = node+0xe8, emissive = part+0x7c.
            // ponytail: MatStatic also multiplies ambient+diffuse by AurGetTileColor() when the part's tile
            // record (part+0x98, +0x54) is set. Not reproduced -- add it if tiles read the wrong hue.
            IntPtr mdl = lit ? *(IntPtr*)(tri + PartMdlNode) : IntPtr.Zero;
            if (lit && mdl == IntPtr.Zero) { _skipNoMdl++; W(i, "skipped: no mdl node"); continue; }

            // texture: re-fetch per frame -- CAurTexture* is BORROWED, freed on area transitions (never
            // cache), and the per-frame fetch is exactly what keeps animated textures cycling
            long aur = _getTexture(_oMat[i], 0);
            if (aur == 0) { _skipTex++; W(i, "skipped: no diffuse texture"); continue; }
            int tid = AurTex.Tid((IntPtr)aur);
            if (tid == 0) { _skipTex++; W(i, "skipped: diffuse has no GL id"); continue; }

            // #120: the same re-fetch for whichever of the PBR slots this material declared. A slot that
            // declared a map but resolves to nothing this frame drops out of the mask rather than leaving
            // the shader sampling an empty unit.
            int slots = 0;
            for (int s = 0; s < MapSlots; s++)
            {
                slotTid[s] = 0;
                if (lit && (_oSlots[i] & SlotBit(s)) != 0)
                {
                    long a = _getTexture(_oMat[i], s + 1);
                    int t = AurTex.Tid((IntPtr)a);
                    if (t != 0) { slotTid[s] = t; slots |= SlotBit(s); }
                }
            }
            if (lit) slots |= _oSlots[i] & EnvSlot;   // #121: no live texture to re-fetch, just the flag
            // #146's zero-content test path, see _aoTest. Stands in for a texture7 nothing ships yet.
            if (lit && _aoTest) { slotTid[AoSlot] = tid; slots |= AoBit; }

            // index range re-read live from the VDO item tables (Repack revokes before they move)
            IntPtr vdo = _oVdo[i];
            long elem = (long)*(IntPtr*)(vdo + VdoElemItems);
            long vert = (long)*(IntPtr*)(vdo + VdoVertItems);
            int item = _oItem[i];
            if (elem == 0 || vert == 0 || item < 0
                || item >= *(int*)(vdo + VdoElemCount) || item >= *(int*)(vdo + VdoVertCount))
            { _skipRange++; W(i, "skipped: item out of range"); continue; }
            int idxCount = *(int*)(elem + item * 8 + 4);
            if (idxCount <= 0) { _skipRange++; W(i, "skipped: empty index range"); continue; }

            int v = _oVao[i];
            if (!EnsureVao(v)) { _skipVao++; W(i, "skipped: no VAO"); continue; }
            uint mode = _primTable[_oPrim[i]];
            // #123: the feature joins the batch key. Not for binding -- it rides in the per-draw record --
            // but so the depth prepass can drop a whole batch: a displacing feature must not write its
            // undisplaced depth, and the prepass has no per-draw granularity.
            int feat = (int)_oFeat[i * 4];
            // #137: a posed skinmesh overrides whatever content feature the material named -- see
            // ShaderFeature.Skin for why the two share the field, and why that trade is safe on shipped
            // content. Joining the cut key here is what lets the prepass drop the whole skinned batch.
            if (_oBone[i] >= 0) feat = (int)ShaderFeature.Skin;
            // #124: ...and the dissolve bit joins it for the same reason -- the keyhole stage discards.
            bool dis = dissolve != 0f;
            // #126: the class and the blend pair are GL state, which one multidraw cannot vary inside.
            int blend = OwnedClasses.Blends(cls) ? (_oSrc[i] << 8) | _oDst[i] : 0;
            // #153: under bindless a LIT batch no longer splits on its textures -- the handles ride the
            // record, so one multidraw spans every texture in the class. Feat/dis stay in the key only for
            // the depth prepass's whole-batch drop; unlit parts keep the bind path and the full key.
            bool merge = _bindless && lit && cls != (int)OwnedClass.Water;
            int tidKey = merge ? -2 : tid;
            bool sameSlots = groups != 0 && merge;
            if (!merge)
            {
                sameSlots = groups != 0;
                for (int s = 0; sameSlots && s < MapSlots; s++)
                    sameSlots = _gTexN[(groups - 1) * MapSlots + s] == slotTid[s];
            }
            if (!sameSlots || lit != keyLit || v != keyVao || tidKey != keyTex || mode != keyMode
                || feat != keyFeat || dis != keyDis || cls != keyCls || blend != keyBlend)
            {
                _gStart[groups] = n; _gLen[groups] = 0;
                _gLit[groups] = lit; _gVao[groups] = v; _gTex[groups] = tid; _gMode[groups] = mode;
                _gFeat[groups] = feat; _gDis[groups] = dis;
                _gCls[groups] = (byte)cls; _gSrc[groups] = _oSrc[i]; _gDst[groups] = _oDst[i];
                for (int s = 0; s < MapSlots; s++) _gTexN[groups * MapSlots + s] = slotTid[s];
                groups++;
                keyLit = lit; keyVao = v; keyTex = tidKey; keyMode = mode; keyFeat = feat; keyDis = dis;
                keyCls = cls; keyBlend = blend;
            }
            W(i, "SUBMITTED");
            if (Perf.BatchService.WatchTex[_oEntry[i]]) _watchSubmitted++;
            _dCount[n] = idxCount;
            _dOff[n] = (nint)((long)(uint)*(int*)(elem + item * 8) * 2);
            _dBase[n] = *(int*)(vert + item * 8);

            // the per-draw record. The model matrix drives BOTH the world position the light loop shades at
            // and the world normal (mat3 of the same GLSL-visible matrix is the correct normal transform here
            // -- the raw bytes already read as the transpose), and the shaders reach uVP * model for clip.
            float* r = data + n * DrawFloats;
            *(Matrix4x4*)r = _oMx[i] ? *(Matrix4x4*)(_oNode[i] + NodeMatrix) : Matrix4x4.Identity;
            // rot.z carries #124's dissolve flag and rot.w #126's per-draw alpha multiplier (the part's own
            // alpha, times the tile fade factor where one applies): the record already had two dead floats
            // here, so both folds cost no stride growth and leave RecordSafe's vec4 count alone.
            r[16] = _oCos[i]; r[17] = _oSin[i]; r[18] = dissolve; r[19] = _passAlpha[k];
            if (lit)
            {
                float* amb = (float*)(mdl + MdlAmbient);
                float* dif = (float*)(mdl + MdlDiffuse);
                float* emi = (float*)(tri + PartSelfIllum);
                r[20] = amb[0]; r[21] = amb[1]; r[22] = amb[2]; r[23] = 1f;
                // #128: PartTriMesh::Mat @0x450960's remaining two instance terms, live. Lit-only because
                // the decompile guards its three SetMaterialv calls behind IsEnabled(5) -- an unlit part gets
                // a flat constant colour and the instance's alpha, which is all the record above carries.
                var cm = Perf.BatchService.GobDiffuseMul(tri);
                var ea = Perf.BatchService.GobEmissiveAdd(tri);
                r[24] = dif[0] * cm.X; r[25] = dif[1] * cm.Y; r[26] = dif[2] * cm.Z; r[27] = 1f;
                r[28] = emi[0] + ea.X; r[29] = emi[1] + ea.Y; r[30] = emi[2] + ea.Z; r[31] = 1f;
                int o = i * 8;
                r[32] = _oParam[o]; r[33] = _oParam[o + 1]; r[34] = _oParam[o + 2]; r[35] = slots;
                r[36] = _oParam[o + 4]; r[37] = _oParam[o + 5]; r[38] = _oParam[o + 6]; r[39] = _oParam[o + 7];
            }
            // #147: water reuses the material-colour slot for its BODY. The water stage reads none of
            // amb/dif/emi (its liquid colour is the diffuse's own average, #129), and the alternative --
            // an eighth vec4 -- would grow the stride for one class and force every override to be
            // re-emitted. Overwrites the lit branch above, which ran for a lit water batch.
            if (cls == (int)OwnedClass.Water)
            {
                var wind = AreaWind();
                // Engine wind units -> metres/second, so the shader's growth law is fed the quantity it is
                // written in. See WaterPass.WindScale for why this is a dial and not a constant.
                float speed = wind.Length() * WaterPass.WindScale;
                if (WaterBodies.Body(tri, wind, out float fetch, out float size, out float extent))
                { r[24] = fetch; r[25] = size; r[26] = extent; r[27] = speed; }
                else { r[24] = 0f; r[25] = 0f; r[26] = 0f; r[27] = speed; }
            }
            // #123: outside the lit branch -- the FULLBRIGHT vertex stage switches on it too, so an unlit
            // wind part still sways. Only the fragment-side features (the roof anchor discard, the flow-map
            // slide) are lit-only, and neither of those parts is ever unlit.
            int fo = i * 4;
            r[40] = _oFeat[fo]; r[41] = _oFeat[fo + 1]; r[42] = _oFeat[fo + 2]; r[43] = _oFeat[fo + 3];
            // #137: feat.y is this draw's bone base -- the MATRIX index of its first bone in the block, so
            // the stage indexes it with a single add. Written after the copy above, which it replaces.
            if (_oBone[i] >= 0) { r[40] = (int)ShaderFeature.Skin; r[41] = _oBone[i] * BoneCount; }
            // #153: this record's textures as resident handles -- diffuse at 0, material slots 1..7 in the
            // shader's macro order (slotTid[k] = material texture k+1). Only for lit records; unlit parts
            // draw on the bind path and never read the block.
            if (merge)
            {
                fixed (ulong* th = _dTexH)
                {
                    ulong* hh = th + (long)n * 8;
                    hh[0] = HandleFor(tid);
                    for (int t = 0; t < MapSlots; t++) hh[1 + t] = HandleFor(slotTid[t]);
                }
            }
            _gLen[groups - 1]++;
            n++;
            // #151: an owned rigid INSTANCE part leaves the engine walk, and with it the walk-side shadow
            // capture -- its shadow has to be fed from here. Skinmeshes are already fed by PoseSkinned's
            // bracket; parts the caster CENSUS still carries (table-eligible) would be double-recorded, so
            // only the census's rejects -- the moving creature pieces that probation killed -- recast here.
            if (_oGob[i] != IntPtr.Zero && _oBone[i] < 0
                && !Perf.BatchService.CasterCensusCovers(_oEntry[i]))
                Shadow.CasterPass.RecordOwnedInstance(tri, _oNode[i], _oVdo[i], _oItem[i], _oPrim[i]);
            Perf.BatchService.OwnGen[_oEntry[i]] = PassGen;
        }
        // #126: where each pass's batch range ends. Read off the batches themselves rather than tracked
        // through the cut above -- the routed order is class-ordered by construction, so the first batch of a
        // later class IS the previous range's end, and a class that staged nothing leaves an empty range
        // rather than someone else's batches.
        _gOpaqueEnd = _gBlendEnd = _gWaterEnd = groups;
        for (int g = 0; g < groups; g++)
        {
            if (_gCls[g] > (byte)OwnedClass.Cutout && g < _gOpaqueEnd) _gOpaqueEnd = g;
            if (_gCls[g] > (byte)OwnedClass.Blended && g < _gBlendEnd) _gBlendEnd = g;
            if (_gCls[g] > (byte)OwnedClass.Water && g < _gWaterEnd) _gWaterEnd = g;
        }
        _drawN = n;
        return groups;
    }

    /// <summary>#125: stage the sky's draws behind the batch records, in the engine's own order -- current
    /// sky, then the one fading in over it, then the cross-fade gob. One record per part, carrying the model
    /// matrix <see cref="SkyBox"/> captured off the engine's matrix stack and the role's alpha in
    /// <c>rot.w</c>, which is the per-draw alpha multiplier the fullbright stage already applies. Nothing
    /// here needs a new shader stage, and so nothing here can drift from the sow-topdata overrides.
    /// <para>Every term is re-read live: a part whose texture, index range or VAO does not resolve this
    /// frame is simply not staged, and the engine's own draw (which is still running, colour-masked only
    /// while we DID draw) is the fallback.</para></summary>
    private static void StageSky()
    {
        _skyN = 0; _skyFirst = _drawN;
        IntPtr scene = SkyBox.Scene;
        // #91's rule again: only the enrolled world Scene's sky, never a GUI 3D panel's.
        if (scene == IntPtr.Zero || scene != _enrollScene) return;
        Span<SkyBox.Role> roles = stackalloc SkyBox.Role[3];
        int nr = SkyBox.Roles(scene, roles);
        fixed (float* data = _dData)
        for (int r = 0; r < nr; r++)
        {
            int staged = 0;
            for (int i = 0; i < SkyBox.Count; i++)
            {
                ref SkyBox.Part p = ref SkyBox.At(i);
                if (p.Gob != roles[r].Gob) continue;
                if (p.Vao < 0 || !SkyBox.Fresh(i) || _drawN >= MaxOwned || _skyN >= MaxSkyDraws)
                { staged = -1; break; }
                // borrowed CAurTexture*, re-fetched per frame like every other owned draw
                long aur = _getTexture(p.Mat, 0);
                int tid = AurTex.Tid((IntPtr)aur);
                IntPtr vdo = p.Vdo;
                long elem = (long)*(IntPtr*)(vdo + VdoElemItems);
                long vert = (long)*(IntPtr*)(vdo + VdoVertItems);
                if (tid == 0 || elem == 0 || vert == 0 || p.Item < 0
                    || p.Item >= *(int*)(vdo + VdoElemCount) || p.Item >= *(int*)(vdo + VdoVertCount))
                { staged = -1; break; }
                int idxCount = *(int*)(elem + p.Item * 8 + 4);
                if (idxCount <= 0 || !EnsureVao(p.Vao)) { staged = -1; break; }

                int n = _drawN;
                _dCount[n] = idxCount;
                _dOff[n] = (nint)((long)(uint)*(int*)(elem + p.Item * 8) * 2);
                _dBase[n] = *(int*)(vert + p.Item * 8);
                float* rec = data + n * DrawFloats;
                *(Matrix4x4*)rec = p.Model;
                // identity texture rotation, no dissolve, the role's alpha; no feature, and the lit block is
                // never read because the sky takes the fullbright program (the engine draws it Disable(5)).
                rec[16] = 1f; rec[17] = 0f; rec[18] = 0f; rec[19] = roles[r].Alpha;
                rec[40] = 0f; rec[41] = 0f; rec[42] = 0f; rec[43] = 0f;
                _skVao[_skyN] = p.Vao; _skTex[_skyN] = tid; _skMode[_skyN] = _primTable[p.Prim];
                _skSrc[_skyN] = roles[r].Src; _skDst[_skyN] = roles[r].Dst;
                _skyN++; _drawN = n + 1; staged++;
            }
            // All-owned-or-none, per #107 and exactly as the blended class is: a role that cannot stage
            // every one of its parts -- or has captured none of them yet -- would leave a permanent hole in
            // a sky the engine is being colour-masked out of. Hand the whole sky back for the frame instead,
            // which costs one visible vanilla sky and no hole at all.
            if (staged <= 0) { _drawN = _skyFirst; _skyN = 0; return; }
        }
    }

    /// <summary>ADR 0011 step 3 (#125): the skybox, after the opaque set and before the blended one.
    /// <para><c>glDepthRange(1,1)</c> is what makes "after opaque" mean anything. The dome sits AT the
    /// camera, so its own depth is NEAR and it would pass the test everywhere -- which is exactly why vanilla
    /// paints over bucket 0. Forced to the far plane and tested LEQUAL (the value
    /// <see cref="OpaqueOwnedPass"/> leaves bound), it survives only where nothing has written depth, so
    /// every sky pixel the world covers is rejected before it shades.</para>
    /// <para><c>GL_DEPTH_CLAMP</c> because the matrices were captured under the ENGINE's sky projection
    /// (far 1000.0, <c>RenderSkyBoxes</c>) and are replayed under the world's: the dome can cross the world
    /// far plane, and clipped it would read as a hole at the horizon with nothing in the log.</para>
    /// <para>Depth range and clamp are 4.6-CONTEXT state the later passes share, so both are restored here
    /// rather than left for someone else to find.</para></summary>
    private static void SkyOwnedPass(Matrix4x4* vp, int* viewport)
    {
        if (_skyN == 0 || Gl.DepthRange == null) return;
        Gl.UseProgram(_prog46);
        Gl.Uniform1i(_locTex, 0);
        Gl.Uniform1i(_locViz, _lightViz);
        Gl.UniformMatrix4fv(_locVp, 1, 0, (float*)vp);
        UploadFeatureTerms(_locFeat, viewport);
        Gl.Uniform1f(_locCut, -1f);          // the engine draws the sky with the discard off
        Gl.DepthMask(0);
        Gl.DepthRange(1.0, 1.0);
        Gl.Enable(Gl.DEPTH_CLAMP);
        Gl.Enable(Gl.BLEND); _blendOn = true;
        int boundVao = -1, boundTex = -1;
        uint boundSrc = 0xFFFFFFFF, boundDst = 0xFFFFFFFF;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        for (int i = 0; i < _skyN; i++)
        {
            uint src = BlendGl(_skSrc[i]), dst = BlendGl(_skDst[i]);
            if (src != boundSrc || dst != boundDst) { Gl.BlendFunc(src, dst); boundSrc = src; boundDst = dst; }
            if (_skVao[i] != boundVao) { Gl.BindVertexArray(_vVao46[_skVao[i]]); boundVao = _skVao[i]; }
            if (_skTex[i] != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)_skTex[i]); boundTex = _skTex[i]; }
            int s = _skyFirst + i;
            Gl.Uniform1i(_locBase, s);
            Gl.MultiDrawElementsBaseVertex(_skMode[i], counts + s, GlUnsignedShort, (void**)(offs + s), 1, bases + s);
            _batches++; _draws++;
        }
        Gl.Disable(Gl.DEPTH_CLAMP);
        Gl.DepthRange(0.0, 1.0);
        // The vanilla site reads this to decide whether to colour-mask the engine's own copy: it is set only
        // after the owned sky actually reached the GPU this pass, never on intent.
        SkyBox.MarkDrawn(PassGen);
    }

    /// <summary>Push this pass's staged records into the per-draw SSBO. The BufferData ORPHANS the old
    /// allocation first: a bare BufferSubData over a buffer the GPU may still be reading (2-3 passes run per
    /// frame) makes the driver sync, and donating a stall back is exactly what #84 is trying to avoid.</summary>
    /// <summary>#153: forget every cached handle. Called at each enrollment -- the cadence at which the
    /// tids themselves are re-harvested -- and on release. Handles stay resident until their textures die;
    /// the cache only ever answers for tids the current enrollment produced.</summary>
    private static void WipeHandles()
    {
        Array.Clear(_hTid, 0, _hTid.Length);
    }

    private static void UploadDraws()
    {
        if (_drawBuf == 0) { uint b; Gl.GenBuffers(1, &b); _drawBuf = b; }
        var size = (IntPtr)((long)_drawN * DrawFloats * sizeof(float));
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _drawBuf);
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, size, null, Gl.DYNAMIC_DRAW);
        fixed (float* p = _dData) Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, size, p);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindDraw, _drawBuf);
        if (_bindless)
        {
            if (_texHBuf == 0) { uint b; Gl.GenBuffers(1, &b); _texHBuf = b; }
            var hSize = (IntPtr)((long)_drawN * 8 * sizeof(ulong));
            Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _texHBuf);
            Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, hSize, null, Gl.DYNAMIC_DRAW);
            fixed (ulong* p = _dTexH) Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, hSize, p);
            Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindTexH, _texHBuf);
        }
    }

    /// <summary>#137: push this pass's bone snapshots into the bone block. Orphan-then-fill for
    /// <see cref="UploadDraws"/>'s reason. Nothing to upload is the common case (no skinmesh on screen), and
    /// the block stays bound empty rather than unbound -- a stage that declares it must find something there
    /// even on a frame where no draw reads it.</summary>
    private static void UploadBones()
    {
        if (!_skinning) return;
        if (_boneBuf == 0) { uint b; Gl.GenBuffers(1, &b); _boneBuf = b; }
        var size = (IntPtr)((long)Math.Max(_boneSlots, 1) * BoneFloats * sizeof(float));
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _boneBuf);
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, size, null, Gl.DYNAMIC_DRAW);
        if (_boneSlots > 0)
            fixed (float* p = _boneStage)
                Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero,
                                 (IntPtr)((long)_boneSlots * BoneFloats * sizeof(float)), p);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindBones, _boneBuf);
    }

    /// <summary>Phase B: one bind set + one glMultiDrawElementsBaseVertex per batch. uDrawBase is re-sent for
    /// every batch (and it is a DIFFERENT location per program, so the program switch cannot leave a stale
    /// one behind) -- the shaders read record uDrawBase + gl_DrawID, which is what carries the per-part model
    /// matrix, texture rotation and material colours that used to be per-part uniforms.</summary>
    private static void IssueBatches(int from, int groups, float* vpRaw, int* viewport, float near, float far)
    {
        // A probe capture must not carry the debug viz through: SOWX_LIGHTVIZ would bake flat cyan (or a
        // cluster heat map) into the cube and leave it there for every frame that samples it afterwards.
        int viz = _capturing ? 0 : _lightViz;
        // Which program is bound, -1 for neither yet. In the opaque range this switches once (lit batches
        // sort last inside a class); in the blended range it can alternate, because there the order is the
        // camera's and nothing else. ponytail: a lit rebind re-sends the cascade and cube uniforms, so an
        // alternating blended set pays for them per switch. The set is the small remainder by construction;
        // if it ever is not, the fix is a second sort digit inside the distance order, not a cache.
        // #129: three programs now, not two -- fullbright (0), clustered lit (1) and water (2). Keyed on the
        // batch's CLASS for water and on _gLit for the other two, because water is a class the whole way
        // down: its stage samples the framebuffer copy, and no lit batch ever should.
        int boundProg = -1;
        int boundVao = -1, boundTex = -1;
        uint boundSrc = 0, boundDst = 0;
        // GL_BLEND as this pass found it: DrawOwned's preamble disables it, and TransparentPass enables it
        // before its own range, so the first batch of either kind agrees with the state already set.
        bool boundBlend = _blendOn;
        float boundCut = float.NaN;   // per PROGRAM: reset on every UseProgram below
        int* boundSlot = stackalloc int[MapSlots];
        for (int s = 0; s < MapSlots; s++) boundSlot[s] = -1;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        for (int g = from; g < groups; g++)
        {
            bool water = _gCls[g] == (byte)OwnedClass.Water;
            bool lit = _gLit[g];
            int wantProg = water ? 2 : lit ? 1 : 0;
            if (boundProg != wantProg)
            {
                boundProg = wantProg;
                boundCut = float.NaN;
                if (water)
                {
                    Gl.UseProgram(_progWater);
                    Gl.UniformMatrix4fv(_watVp, 1, 0, vpRaw);
                    Gl.UniformMatrix4fv(_watView, 1, 0, _waterView);
                    Gl.UniformMatrix4fv(_watProj, 1, 0, _waterProj);
                    Gl.Uniform2f(_watNearFar, _waterNear, _waterFar);
                    float* dials = stackalloc float[4];
                    dials[0] = WaterPass.Tint; dials[1] = WaterPass.Wave;
                    dials[2] = WaterPass.Ssr; dials[3] = WaterPass.Foam;
                    Gl.Uniform4fv(_watDials, 1, dials);
                    // #147: REAL seconds, not the game clock -- a module runs time-of-day at any multiple of
                    // real time and the whole sea would move at that multiple.
                    Gl.Uniform1f(_watWaveTime, WaterPass.WaveTime);
                    float* tess = stackalloc float[2];
                    tess[0] = WaterPass.TessSegments; tess[1] = WaterPass.TessMax;
                    Gl.Uniform2fv(_watTess, 1, tess);
                    if (_watWind >= 0)
                    {
                        var w3 = AreaWind();
                        float* wv = stackalloc float[3];
                        wv[0] = w3.X; wv[1] = w3.Y; wv[2] = 0f;
                        Gl.Uniform3fv(_watWind, 1, wv);
                    }
                    OwnedLights.BindForDraw();
                    OwnedLights.SetDrawUniforms(_watGrid, _watTile, _watZ, _watFalloff, _watWrap,
                                                _watAtten, _watHot, _watWrapN, viewport, near, far);
                    Gl.Uniform1i(_watViz, viz);
                    Gl.Uniform1i(_watCapture, _capturing ? 1 : 0);
                    UploadFeatureTerms(_watFeat, viewport);
                    fixed (Vector3* eye = &_eyePos) Gl.Uniform3fv(_watEye, 1, (float*)eye);
                    UploadAreaTerms(_watAmbient, _watDiffuse, _watDir);
                    UploadSunCascades(_watSunTex, _watSunVp, _watSunTexel, _watSunBias, _watSunCount);
                    UploadLocalCubes(_watCubeTex, _watCubeLight, _watCubePlane, _watCubeCount, _watCubeSoft);
                    UploadProbe(_watProbeMips, _watProbeGain, _watSceneEnv);
                    // The cascade/cube/probe uploads each left their own unit current for a moment; the copy
                    // pair has to be back on its units before the first water draw samples them.
                    WaterPass.Bind();
                }
                else if (lit)
                {
                    Gl.UseProgram(_progLit46);
                    Gl.Uniform1i(_litTex, 0);
                    Gl.UniformMatrix4fv(_litVp, 1, 0, vpRaw);
                    OwnedLights.BindForDraw();
                    OwnedLights.SetDrawUniforms(_litGrid, _litTile, _litZ, _litFalloff, _litWrap,
                                                _litAtten, _litHot, _litWrapN, viewport, near, far);
                    Gl.Uniform1i(_litViz, viz);
                    Gl.Uniform1i(_litCapture, _capturing ? 1 : 0);
                    UploadFeatureTerms(_litFeat, viewport);
                    fixed (Vector3* eye = &_eyePos) Gl.Uniform3fv(_litEye, 1, (float*)eye);
                    UploadAreaTerms();
                    UploadSunCascades();
                    UploadLocalCubes();
                    UploadProbe();
                }
                else
                {
                    Gl.UseProgram(_prog46);
                    Gl.Uniform1i(_locTex, 0);
                    Gl.Uniform1i(_locViz, viz);
                    Gl.UniformMatrix4fv(_locVp, 1, 0, vpRaw);
                    UploadFeatureTerms(_locFeat, viewport);
                }
            }
            int s = _gStart[g];
            Gl.Uniform1i(water ? _watBase : lit ? _litBase : _locBase, s);
            // #126: the alpha cutout, per batch because it is per class -- the engine's own
            // fAlphaDiscardValue, which is a global there and a uniform here.
            float cut = OwnedClasses.Discard(_gCls[g]);
            if (cut != boundCut) { Gl.Uniform1f(water ? _watCut : lit ? _litCut : _locCut, cut); boundCut = cut; }
            // ...and the blend state, per batch, because the opaque PASS now mixes both: a plain opaque batch
            // draws with GL_BLEND off and a cutout batch with it on and the material's own pair, which is
            // exactly the pair of states the engine draws buckets 0 and 1 in. Batches are class-grouped, so
            // this toggles about once per pass.
            bool blend = OwnedClasses.Blends(_gCls[g]);
            if (blend != boundBlend)
            {
                boundBlend = blend;
                if (blend) Gl.Enable(Gl.BLEND); else Gl.Disable(Gl.BLEND);
            }
            if (blend)
            {
                uint src = BlendGl(_gSrc[g]), dst = BlendGl(_gDst[g]);
                if (src != boundSrc || dst != boundDst) { Gl.BlendFunc(src, dst); boundSrc = src; boundDst = dst; }
            }
            if (_gVao[g] != boundVao) { Gl.BindVertexArray(_vVao46[_gVao[g]]); boundVao = _gVao[g]; }
            // #153: a merged lit batch spans many textures; its samplers come off the handle block per draw,
            // and binding here would be dead state. Unlit and water batches still bind.
            bool handles = _bindless && lit && !water;
            if (!handles && _gTex[g] != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)_gTex[g]); boundTex = _gTex[g]; }
            // #120: slots 1..5 on units 3..7. Unbound slots get texture 0 rather than being left holding a
            // previous batch's map -- the shader's slot mask says not to sample them, but a stale binding is
            // one override away from being sampled anyway.
            for (int t = 0; !handles && t < MapSlots; t++)
            {
                int want = _gTexN[g * MapSlots + t];
                if (want == boundSlot[t]) continue;
                boundSlot[t] = want;
                // #151: one DSA call instead of ActiveTexture+Bind+ActiveTexture -- this loop is a third of
                // the pass's GL traffic. The fallback keeps pre-4.5 drivers drawing.
                if (Gl.BindTextureUnit != null) Gl.BindTextureUnit(PbrUnit0 + (uint)t, (uint)want);
                else
                {
                    Gl.ActiveTexture(Gl.TEXTURE0 + PbrUnit0 + (uint)t);
                    Gl.BindTexture(Gl.TEXTURE_2D, (uint)want);
                    Gl.ActiveTexture(Gl.TEXTURE0);   // unit 0 stays current: the diffuse rebinds there
                }
            }
            // #147: water is drawn as PATCHES so the tessellator sees it. Same index buffer, same triangles
            // -- only the primitive mode changes, and the tess-control stage retires a patch to level 1 when
            // there is no wave to resolve, so a millpond costs the same as before.
            Gl.MultiDrawElementsBaseVertex(water ? Gl.PATCHES : _gMode[g], counts + s, GlUnsignedShort,
                                           (void**)(offs + s), _gLen[g], bases + s);
            _batches++;
            _draws += _gLen[g];
            _drawsByClass[_gCls[g]] += _gLen[g];
        }
        // Hand the per-batch toggling back to the pass level: the next pass reads this rather than assuming
        // what the last batch left bound.
        _blendOn = boundBlend;
    }

    /// <summary>ADR 0011 steps 4 and 7 (#126): the blending classes. Blended draws sorted back-to-front with
    /// the depth mask OFF -- which is what makes grass's punch-through structurally impossible rather than
    /// fixed, since nothing alpha-blended can write depth any more -- and additive follows it unsorted,
    /// because addition commutes and the engine leaves its own depth mask off there too
    /// (<c>Material::SetBlendingMode</c> @0x4c5c50 turns it off for exactly that one blend pair).
    /// <para>Depth TESTING stays on, against the opaque set's depth: a blended surface behind a wall is still
    /// hidden by it. The per-batch <c>glBlendFunc</c> comes off the material, in
    /// <see cref="IssueBatches"/>.</para>
    /// <para>The same ordering caveat reaches the CUTOUT class, which draws back in the opaque pass: its
    /// graded texels blend, so they composite against whatever the framebuffer already holds. Owned opaque
    /// geometry is there (it draws first, in class order); anything the engine still owns is not, and a
    /// cutout in front of an engine-drawn wall therefore blends against stale colour AND depth-writes over
    /// it. The upgrade that removes the dependency entirely is alpha-to-coverage -- ADR 0011 names it beside
    /// the alpha test -- which needs a multisampled target and is worth taking when the owned frame binds its
    /// own (the M3 exit step).</para>
    /// <para>NAMED APPROXIMATION, for as long as the owned pass anchors ahead of the engine's walk: these classes
    /// composite BEFORE everything the engine still draws itself, and nothing they draw writes depth. So a
    /// creature standing behind an owned window paints over it rather than being seen through it (vanilla
    /// gets a different wrong answer: it depth-writes its blended geometry, so the creature vanishes
    /// instead), and the SKYBOX -- which the engine draws between buckets 0 and 1, depth-mask off -- paints
    /// over a blended or additive surface seen against the sky. The first resolves when the dynamic classes
    /// are owned (#127), the second when the skybox is (#125); neither is a #126 regression to chase, and
    /// both are why ADR 0011's frame anchor is a named exit criterion rather than a detail.</para></summary>
    private static void TransparentPass(int from, int to, Matrix4x4* vp, int* viewport, float near, float far)
    {
        if (to <= from) return;
        Gl.DepthMask(0);
        Gl.Enable(Gl.BLEND); _blendOn = true;
        IssueBatches(from, to, (float*)vp, viewport, near, far);
    }

    /// <summary>ADR 0011 <see cref="PassId.WaterOwned"/> (#129): the water class, drawn from the one
    /// framebuffer copy taken immediately before it.
    /// <para>Depth writes go back ON, which is the one piece of state that separates this pass from the
    /// blended one and the reason it can be here at all: additive draws after water and has to be able to
    /// stand in FRONT of it. That is vanilla's arrangement -- effects are invisible to the reflection and are
    /// not painted over by the surface -- and the ticket keeps it deliberately.</para>
    /// <para>Stands down whole whenever the copy could not be taken (<see cref="WaterPass.Ready"/> false) or
    /// the program did not build. Nothing partial: <see cref="Perf.BatchService"/>'s bucket-6 gate reads the
    /// same flag, so the engine draws every water surface itself on exactly the passes this one does not.</para></summary>
    private static void WaterOwnedPass(int from, int to, Matrix4x4* vp, float* view, float* proj,
                                       int* viewport, float near, float far)
    {
        if (to <= from || !WaterPass.Ready || !_waterArmed) return;
        _waterView = view; _waterProj = proj; _waterNear = near; _waterFar = far;
        Gl.DepthMask(1);
        WaterPass.Bind();
        IssueBatches(from, to, (float*)vp, viewport, near, far);
        // The one flag IterSight reads: staged-and-drawn, not merely staged.
        _waterDrew = true;
        // Leave the state the additive pass expects to find: it enables blending itself, but the depth mask
        // is pass-level state and additive must not write depth.
        Gl.DepthMask(0);
    }

    /// <summary>Build the water variant. A failure disarms owned water for the session -- the engine's own
    /// bucket-6 walk (with its own framebuffer copy) then draws every water surface, which is vanilla.</summary>
    private static bool EnsureWaterProgram()
    {
        if (_progWater != 0) return true;
        if (_waterFailed) return false;
        if (!WaterPass.On) { _waterFailed = true; return false; }
        // OVERRIDABLE, like every other stage in this pass that produces a pixel. The extender's job is to
        // provide the water CLASS -- the one framebuffer copy, the depth, the light rig, the probe -- and to
        // hand a content author a stage they can rewrite; the look itself belongs in a .shd, which is what
        // makes any of this optional. The embedded text below is the DEFAULT, not the source of truth.
        // <para>#147: four stages, all four overridable. Water is the one class that TESSELLATES -- its
        // geometry is two triangles per ten-unit tile, so a displacement in the vertex stage would move the
        // corners and interpolate a flat sheet between them. Its vertex stage is its own for the same reason:
        // it must not project, because the vertices it emits are patch corners that no longer exist by the
        // time anything rasterises.</para>
        string waterFs = WaterPass.Fs;
        uint prog = GlProgram.Graphics(ShdWaterVs, WaterPass.Vs, ShdWaterTcs, WaterPass.Tcs,
                                       ShdWaterTes, WaterPass.Tes, ShdWaterFs, waterFs,
                                       _log, "OwnedDrawService water program",
                                       out string wvs, out string wfs);
        // An override that declares the wrong per-draw record reads every draw out of a neighbour's slot --
        // the #120 failure, and silent. #132 adds the colour space to the same guard, for the same reason.
        // Same pair the fullbright and lit programs take.
        if (prog != 0 && !(RecordSafe(wvs) && RecordSafe(wfs) && LinearSafe(wfs)))
        {
            Gl.DeleteProgram(prog);
            _log?.Info("OwnedDrawService: water override declares the wrong per-draw record or still " +
                       "encodes to display space -- rebuilding every water stage from the embedded default");
            prog = GlProgram.Tessellated(WaterPass.Vs, WaterPass.Tcs, WaterPass.Tes, waterFs,
                                         _log, "OwnedDrawService water program (embedded)");
        }
        if (prog == 0)
        {
            _waterFailed = true;
            _log?.Info("OwnedDrawService: water program unavailable -- the engine draws water itself");
            return false;
        }
        _progWater = prog;
        WaterPass.ClampTess();
        // One patch is one triangle: the owned pass hands the tessellator the engine's own index buffer.
        if (Gl.PatchParameteri != null) Gl.PatchParameteri(Gl.PATCH_VERTICES, 3);
        _watVp = GlProgram.Loc(prog, "uVP"); _watBase = GlProgram.Loc(prog, "uDrawBase");
        _watTex = GlProgram.Loc(prog, "uTex"); _watCut = GlProgram.Loc(prog, "uAlphaCut");
        _watAmbient = GlProgram.Loc(prog, "uAreaAmbient"); _watDiffuse = GlProgram.Loc(prog, "uAreaDiffuse");
        _watDir = GlProgram.Loc(prog, "uAreaDir");
        _watGrid = GlProgram.Loc(prog, "uGrid"); _watTile = GlProgram.Loc(prog, "uTile");
        _watZ = GlProgram.Loc(prog, "uZ"); _watFalloff = GlProgram.Loc(prog, "uFalloff");
        _watWrap = GlProgram.Loc(prog, "uWrap");
        _watAtten = GlProgram.Loc(prog, "uAtten"); _watHot = GlProgram.Loc(prog, "uHot");
        _watWrapN = GlProgram.Loc(prog, "uWrapN");
        _watViz = GlProgram.Loc(prog, "uViz"); _watEye = GlProgram.Loc(prog, "uEyePos");
        _watSunTex = GlProgram.Loc(prog, "uSunShadow"); _watSunVp = GlProgram.Loc(prog, "uSunVp");
        _watSunTexel = GlProgram.Loc(prog, "uSunTexel"); _watSunBias = GlProgram.Loc(prog, "uSunBias");
        _watSunCount = GlProgram.Loc(prog, "uSunCascades");
        _watCubeTex = GlProgram.Loc(prog, "uLocalShadow"); _watCubeLight = GlProgram.Loc(prog, "uLocalLight");
        _watCubePlane = GlProgram.Loc(prog, "uLocalPlane"); _watCubeCount = GlProgram.Loc(prog, "uLocalCubes");
        _watCubeSoft = GlProgram.Loc(prog, "uLocalSoft");
        _watProbe = GlProgram.Loc(prog, "uProbe"); _watProbeMips = GlProgram.Loc(prog, "uProbeMips");
        _watProbeGain = GlProgram.Loc(prog, "uProbeGain"); _watSceneEnv = GlProgram.Loc(prog, "uSceneEnv");
        _watCapture = GlProgram.Loc(prog, "uCapture");
        _watFbColor = GlProgram.Loc(prog, "uFbColor"); _watFbDepth = GlProgram.Loc(prog, "uFbDepth");
        _watNearFar = GlProgram.Loc(prog, "uNearFar");
        _watView = GlProgram.Loc(prog, "uView"); _watProj = GlProgram.Loc(prog, "uProj");
        _watDials = GlProgram.Loc(prog, "uWaterDials");
        _watWaveTime = GlProgram.Loc(prog, "uWaveTime"); _watTess = GlProgram.Loc(prog, "uTessDials");
        _watWind = GlProgram.Loc(prog, "uAreaWind");
        _watFeat = FeatLocs(prog);
        // Sampler units, bound once: a sampler left at its default points at unit 0, and two sampler types
        // on one image unit is INVALID_OPERATION -- the draw is dropped, silently as far as the frame knows.
        Gl.UseProgram(prog);
        Gl.Uniform1i(_watTex, 0);
        Gl.Uniform1i(_watSunTex, (int)SunUnit);
        Gl.Uniform1i(_watCubeTex, (int)CubeUnit);
        Gl.Uniform1i(_watProbe, (int)EnvProbe.ProbeUnit);
        Gl.Uniform1i(_watFbColor, (int)WaterPass.ColorUnit);
        Gl.Uniform1i(_watFbDepth, (int)WaterPass.DepthUnit);
        _log?.Info("OwnedDrawService: water program ready (SOWX_WATER=0 disables) -- " + WaterPass.Describe());
        return true;
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

    /// <summary>One <c>Aurora::AuroraBlendModes</c> value as its GL blend factor -- the engine's own
    /// translation table at 0x12f5040, which <c>GLRender::SetBlendFunc</c> @0x4cb0c0 indexes. Out-of-range
    /// values fall back to the pair the engine's own default is, rather than to whatever GL enum 0 means.</summary>
    private static uint BlendGl(int aurora) => aurora switch
    {
        0 => GlSrcAlpha,
        1 => GlOneMinusSrcAlpha,
        2 => GlOne,
        3 => GlZero,
        4 => GlDstColor,
        5 => GlSrcColor,
        // #125 review: the table's entry 6 is 0x0305, ONE_MINUS_DST_ALPHA -- read out of
        // blendTranslationTable @0x12f5040 rather than inferred from the name beside it.
        6 => GlOneMinusDstAlpha,
        _ => GlSrcAlpha,
    };

    /// <summary>Bind the 4.6-side draw target: default framebuffer when the engine draws to 0 (same
    /// drawable, shared), else a mirror FBO over the engine FBO's attachments (textures/renderbuffers
    /// share; FBO containers do not). Incomplete mirror = clean skip, the engine draws everything.
    /// <para>Re-attached and re-checked EVERY pass rather than cached on attachment name/type. GL reuses
    /// freed object names, and an FBO attachment references the object, not the name -- so when the engine
    /// recreates its render targets (focus loss / alt-tab / workspace switch reruns its device reset) the
    /// names can come back identical while our attachments still point at destroyed objects. Cached, that
    /// reads as "unchanged, still complete" and we draw into a dead target forever while still stamping the
    /// parts as drawn, so the walk suppresses geometry nobody rendered. Three GL calls per pass is the
    /// cheaper half of that trade.</para></summary>
    private static bool BindTarget(int engFbo, int colorType, int colorName, int depthType, int depthName)
    {
        if (engFbo == 0) { Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0); return true; }
        if (_fbo46 == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo46 = f; }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo46);
        Attach(Gl.COLOR_ATTACHMENT0, colorType, (uint)colorName);
        Attach(Gl.DEPTH_ATTACHMENT, depthType, (uint)depthName);
        bool bad = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE;
        if (bad != _mirrorBad)
        {
            _mirrorBad = bad;
            _log?.Info(bad
                ? "OwnedDrawService: engine-FBO mirror incomplete -- owned draws skipped, engine draws everything"
                : "OwnedDrawService: engine-FBO mirror complete again -- owned draws resumed");
        }
        return !bad;
    }

    private static void Attach(uint attachment, int type, uint name)
    {
        if (type == (int)Gl.RENDERBUFFER) Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, attachment, Gl.RENDERBUFFER, name);
        else Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, attachment, Gl.TEXTURE_2D, name, 0);
    }

    /// <summary>Resrefs the owned colour pass looks up before falling back to the GLSL below (#92). A
    /// content author drops <c>&lt;name&gt;.shd</c> in a hak, <c>override/</c> or NWSync and gets that stage
    /// instead; the lit fragment resref replaces the whole composed stage, <see cref="OwnedLights.FragmentLib"/>
    /// included, so an override must carry its own clustered-lighting helpers -- and may spell their sizing
    /// constants as the <see cref="ShaderResource.Tokens"/> the embedded source uses.</summary>
    private const string ShdFullbrightVs = "sowx_own_vs", ShdFullbrightFs = "sowx_own_fs";
    private const string ShdLitVs = "sowx_ownlit_vs", ShdLitFs = "sowx_ownlit_fs";
    /// <summary>#129's water stage. Its own pair rather than the lit one's, so an author can rewrite the
    /// water surface without forking every lit part in the world.</summary>
    private const string ShdWaterVs = "sowx_water_vs", ShdWaterFs = "sowx_water_fs",
                         ShdWaterTcs = "sowx_water_tcs", ShdWaterTes = "sowx_water_tes";

    /// <summary>#84: the per-draw block, indexed by <c>uDrawBase + gl_DrawID</c>. This is what makes one
    /// multidraw able to cover a whole group -- everything that varies per part and used to be a uniform lives
    /// here instead. <c>binding = 4</c> must stay in step with <see cref="BindDraw"/>.
    /// <para><c>rot</c> is (cos, sin) of the tile texture rotation: the engine's own vertex stage does
    /// <c>vVertexTexCoords = mat2(m_texture) * vTcIn.xy</c> (base_shaders.bif), and m_texture is
    /// PartTriMesh::TransformTextureMatrix's Z-rotation -- columns (c,s) and (-s,c). The shaders below are
    /// that mat2. <c>amb</c>/<c>dif</c>/<c>emi</c> are the material colours, written only for lit draws.</para>
    /// <para>#120: <c>pbr</c> is (Specularity, Roughness, Metallicness, live slot mask) and <c>spc</c> is
    /// (CustomSpecularColor.rgb, declared?) -- the four stock .mtr parameters, carried per draw so one batch
    /// can still span materials that declare different ones.</para>
    /// <para>#123: <c>feat</c> is (<see cref="ShaderFeature"/>, and the three parameters that feature reads).
    /// Same reason it is per draw and not a uniform: one batch spans materials asking for different
    /// shaders.</para>
    /// <para>ponytail: both vertex stages read this block, so the whole owned pass -- fullbright included --
    /// now needs vertex-stage SSBO access. GL 4.6 only REQUIRES
    /// <c>MAX_VERTEX_SHADER_STORAGE_BLOCKS >= 0</c> (it is the fragment/compute minimums that are 8), so a
    /// strictly-conformant driver may refuse the link, which disarms #82's and #83's paths along with #84's.
    /// Every desktop driver in practice reports >= 8, and the failure is loud (program build fails -> DISARMED,
    /// by name). Upgrade path if a real client ever hits it: probe the limit and put the fullbright variant's
    /// records in a UBO, which has a required vertex-stage minimum of 12 blocks.</para></summary>
    internal const string DrawBufDecl = """
        struct SowxDraw { mat4 model; vec4 rot; vec4 amb; vec4 dif; vec4 emi; vec4 pbr; vec4 spc; vec4 feat; };
        layout(std430, binding = 4) readonly buffer SowxDrawBuf { SowxDraw uDraw[]; };

        """;

    /// <summary>#123: the feature ids, as GLSL. One contract with <see cref="ShaderFeature"/> -- the C# value
    /// is staged into <c>d.feat.x</c> and compared against these, so <see cref="SelfCheck"/> asserts the two
    /// agree rather than trusting a comment to keep them in step.</summary>
    internal const string FeatDecl = """
        #define SOWX_FEAT_WINDGRASS 1
        #define SOWX_FEAT_WINDLEAF  2
        #define SOWX_FEAT_WINDPLANT 3
        #define SOWX_FEAT_ROOFKN    4
        #define SOWX_FEAT_ROOFKNM   5
        #define SOWX_FEAT_FLOWMAP   7
        #define SOWX_FEAT_SKIN      8
        uniform int uTimeOfDay;        // worldtimerTimeOfDay, the engine's own int
        uniform vec3 uAreaWind;        // areaGlobalWind
        uniform int uAreaFlags;        // areaFlags
        uniform vec3 uCamPos;          // cameraPosition
        uniform vec3 uPlayerPos;       // playerPosition

        """;

    /// <summary>#123: the vertex-side half of the feature switches -- the sine sway of the three wind shaders
    /// and the camera-height branch of the two roof toggles, replacing seven forked vertex stages.
    /// <para>The arithmetic is the community shaders' verbatim, constants and quirks included:
    /// <c>sqrt(dot(wind.x, wind.y))</c> (a one-component dot, i.e. x*y), the 0.356 no-wind floor, the
    /// per-foliage denominators (grass and leaf /100, plant /1000) and speed multipliers (grass 20000, plant
    /// and leaf 7000), the doubled plant frequency, the root-anchor gradient vs the leaf's flat 5.0, and the
    /// roof's four odd-value <c>areaFlags</c> comparisons. Bug-for-bug is the acceptance criterion here: the
    /// content is tuned against these numbers.</para>
    /// <para><c>aPos</c> IS the shaders' <c>vPos</c> and <c>d.model</c> their <c>m_m</c> -- the owned pass
    /// stages the node matrix for matrix-class parts and identity for the rest, which is exactly the model
    /// matrix the engine had bound when it drew them. Skinning is absent because a skinned part is not
    /// ownable at all (<c>TryOwnCandidate</c>'s mode-1 VDO test).</para>
    /// <para>Returns the displaced LOCAL position; <c>anchorZ</c> comes back as the UNDISPLACED world height
    /// the roof fragment stage discards the toolset vertex anchor by.</para></summary>
    /// <summary>#137: the skinning half of the owned vertex stages. Vertex-side only -- the fragment stages
    /// never see a bone -- and shared by both stages verbatim so a robe shades identically lit or unlit.
    /// <para><c>binding = 7</c> must stay in step with <see cref="BindBones"/>, and the two attribute
    /// locations with <see cref="LocBoneIdx"/>/<see cref="LocBoneW"/>. A std430 <c>mat4</c> array has a
    /// 64-byte stride, which IS the engine's own <c>m_bones</c> layout, so the CPU side is a memcpy.</para>
    /// <para>The <c>weight &gt; 0.0</c> guards are not a micro-optimisation: an unused lane carries a junk
    /// bone index, and an SSBO read tolerates that far less gracefully than the engine's fixed-size uniform
    /// array did. Same guards, same reason, as <c>CasterPass.CastVs</c>.</para>
    /// <para>One blended MATRIX rather than a blended position, because the normal needs it too: skinning the
    /// position and leaving the normal in bind space lights a moving cloak off a stationary surface.</para>
    /// </summary>
    private const string BoneVs = """
        layout(location = 8) in vec4 aBoneIdx;
        layout(location = 9) in vec4 aBoneWeight;
        layout(std430, binding = 7) readonly buffer SowxBoneBuf { mat4 uBone[]; };

        // -1 for every draw that is not a posed skinmesh, which is nearly all of them.
        int SowxBoneBase(SowxDraw d) { return int(d.feat.x) == SOWX_FEAT_SKIN ? int(d.feat.y) : -1; }

        mat4 SowxSkinMat(int bb)
        {
            mat4 m = mat4(0.0);
            if (aBoneWeight.x > 0.0) m  = uBone[bb + int(aBoneIdx.x)] * aBoneWeight.x;
            if (aBoneWeight.y > 0.0) m += uBone[bb + int(aBoneIdx.y)] * aBoneWeight.y;
            if (aBoneWeight.z > 0.0) m += uBone[bb + int(aBoneIdx.z)] * aBoneWeight.z;
            if (aBoneWeight.w > 0.0) m += uBone[bb + int(aBoneIdx.w)] * aBoneWeight.w;
            return m;
        }

        """;

    private const string FeatVs = """
        // MUST mirror ShaderFeatures.Displaces: this predicate decides which parts take the displaced clip
        // expression, and that one decides which parts the CPU keeps out of the prepass. Disagree and a part
        // sits in the prepass computing a clip expression the prepass never compiled.
        bool SowxDisplaces(int feat) { return feat >= SOWX_FEAT_WINDGRASS && feat <= SOWX_FEAT_ROOFKNM; }

        vec4 SowxFeatPos(SowxDraw d, vec3 aPos, out float anchorZ)
        {
            vec4 pos = vec4(aPos, 1.0);
            anchorZ = (d.model * pos).z;
            int feat = int(d.feat.x);
            if (feat >= SOWX_FEAT_WINDGRASS && feat <= SOWX_FEAT_WINDPLANT)
            {
                float wind = sqrt(dot(uAreaWind.x, uAreaWind.y));
                if (wind == 0.0) wind = 0.356;
                bool plant = feat == SOWX_FEAT_WINDPLANT;
                float strength = d.feat.z / (plant ? 1000.0 : 100.0);
                float speed = d.feat.y * (feat == SOWX_FEAT_WINDGRASS ? 20000.0 : 7000.0);
                float phase = (d.model * pos).y * (plant ? 2.0 : 1.0) * float(uTimeOfDay) / speed;
                float force = sin(phase) * strength;
                // leaves have no roots to anchor, so a flat multiplier where the others take a gradient
                force *= feat == SOWX_FEAT_WINDLEAF ? 5.0 : clamp(aPos.z, 0.0, 1.0) * 10.0;
                pos.x += force * wind;
                pos.y += force * wind;
                if (plant) pos.z += force * wind / 4.0;
                else if (feat == SOWX_FEAT_WINDLEAF) pos.z += force * wind / 2.0;
                // the tileset-mesh lift: grass and plant meshes are authored under the walkmesh so the
                // toolset will not collide with them. The leaf shader declares no such uniform, and its
                // record's w carries nothing.
                if (!(feat == SOWX_FEAT_WINDLEAF) && d.feat.w != 0.0) pos.z += d.feat.w;
            }
            else if (feat == SOWX_FEAT_ROOFKN || feat == SOWX_FEAT_ROOFKNM)
            {
                // roofknm measures the clip height from the PLAYER, roofkn from the world
                float clip = d.feat.z + (feat == SOWX_FEAT_ROOFKNM ? uPlayerPos.z : 0.0);
                bool interior = uAreaFlags == 1 || uAreaFlags == 3 || uAreaFlags == 5 || uAreaFlags == 7;
                if ((uCamPos.z < clip || d.feat.w == 1.0) && interior) pos.z += d.feat.y;
                else pos.z -= 500.0;   // out of sight under the floor, for pits and open water
            }
            return pos;
        }

        """;
    /// <summary>#124: the uniforms behind the camera dissolve and scene fog, in both fragment stages. All
    /// four come straight out of the engine's own globals -- the same values <c>GLRender::SetUniforms</c>
    /// @0x4ccbf0 hands its own shaders -- so nothing here is a reconstruction of engine state.</summary>
    internal const string KeyFogDecl = """
        uniform vec4 uFogParams;       // (enabled, start, end, 1/(end-start)) -- the engine's fogParams
        uniform vec4 uFogColor;        // #132: LINEAR -- the C# side applies the engine's own sRGB curve
                                       // to the display-space value before uploading it
        uniform vec2 uKeyholeDia;      // (min, max) screen-relative aperture diameters
        uniform vec2 uKeyholeCam;      // (playerCameraDist, cameraPitch)
        uniform vec3 uPixel;           // pixelParams: (1/width, 1/height, width/height)

        """;

    /// <summary>#147: Ashima's simplex noise on its own, so the tessellation-evaluation stage can
    /// have it without dragging in the keyhole and fog block it used to live inside. Same text, one
    /// home -- two copies of a noise function is two different dissolve edges waiting to happen.</summary>
    internal const string NoiseLib = """
        // Ashima Arts' simplex noise, as the engine ships it in inc_common.shd -- the dissolve edge IS this
        // pattern, so an approximation reads as a different effect rather than a slightly different one.
        vec4 SowxPermute(vec4 x) { return mod((x * 34.0 + 1.0) * x, 289.0); }

        float SowxNoise3D(vec3 v)
        {
            const vec2 C = vec2(0.16666667, 0.33333333);
            const vec4 D = vec4(0.0, 0.5, 1.0, 2.0);
            vec3 i  = floor(v + dot(v, C.yyy));
            vec3 x0 = v - i + dot(i, C.xxx);
            vec3 g = step(x0.yzx, x0.xyz);
            vec3 l = 1.0 - g;
            vec3 i1 = min(g.xyz, l.zxy);
            vec3 i2 = max(g.xyz, l.zxy);
            vec3 x1 = x0 - i1 + 1.0 * C.xxx;
            vec3 x2 = x0 - i2 + 2.0 * C.xxx;
            vec3 x3 = x0 - 1.0 + 3.0 * C.xxx;
            i = mod(i, 289.0);
            vec4 p = SowxPermute(SowxPermute(SowxPermute(
                       i.z + vec4(0.0, i1.z, i2.z, 1.0))
                     + i.y + vec4(0.0, i1.y, i2.y, 1.0))
                     + i.x + vec4(0.0, i1.x, i2.x, 1.0));
            const float n_ = 1.0 / 7.0;
            vec3 ns = n_ * D.wyz - D.xzx;
            vec4 j = p - 49.0 * floor(p * ns.z * ns.z);
            vec4 x_ = floor(j * ns.z);
            vec4 y_ = floor(j - 7.0 * x_);
            vec4 x = x_ * ns.x + ns.yyyy;
            vec4 y = y_ * ns.x + ns.yyyy;
            vec4 h = 1.0 - abs(x) - abs(y);
            vec4 b0 = vec4(x.xy, y.xy);
            vec4 b1 = vec4(x.zw, y.zw);
            vec4 s0 = floor(b0) * 2.0 + 1.0;
            vec4 s1 = floor(b1) * 2.0 + 1.0;
            vec4 sh = -step(h, vec4(0.0));
            vec4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
            vec4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
            vec3 p0 = vec3(a0.xy, h.x);
            vec3 p1 = vec3(a0.zw, h.y);
            vec3 p2 = vec3(a1.xy, h.z);
            vec3 p3 = vec3(a1.zw, h.w);
            vec4 norm = inversesqrt(vec4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
            p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;
            vec4 m = max(0.6 - vec4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
            m = m * m;
            return 42.0 * dot(m * m, vec4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
        }
        """;

    /// <summary>#124: the fragment half of the dissolve and the fog, ported from the generation of
    /// <c>inc_keyhole.shd</c> / <c>inc_standard.shd</c> the installed client actually runs -- the monolithic
    /// <c>base_shaders.bif</c> set, not the split-include fork (#107 s11). Constants, the simplex noise and
    /// the punch-through/border split are verbatim; what changed is only where the inputs come from.
    /// <para>The engine computes <c>fFragCamOffset</c> in its VERTEX stage and interpolates it. Recomputing
    /// it per fragment from <c>vDepth</c> is the same value: both are perspective-correct interpolations of
    /// a linear function of the view position, and <c>gl_Position.w</c> IS <c>-vPosView.z</c>.</para>
    /// <para>The engine reconstructs the world position with <c>m_view_inv * vPosView</c> purely because its
    /// stage does not carry one. Ours does, and it is the same point.</para></summary>
    internal const string KeyFogFs = NoiseLib + """

        // inc_keyhole.shd's own constants. FLOOR_OFFSET is the height above the player below which nothing
        // dissolves -- it is what stops the floor under the PC being punched out along with the wall.
        const float SOWX_KH_FLOOR = 1.0, SOWX_KH_BLACK = 0.8, SOWX_KH_NOISE = 0.35,
                    SOWX_KH_PUNCH = 0.3, SOWX_KH_SIZE = 12.5;

        /// Returns the LINEAR colour multiplier the dissolve border wants, or a negative value meaning
        /// "punch through" -- the caller discards. 1.0 = this fragment is not in the aperture at all.
        float SowxKeyhole(float dissolve, vec3 world, float depth)
        {
            // vDepth is -vPosView.z, so every sign below is the engine's with that substitution made.
            float off = depth - uKeyholeCam.x + (90.0 - uKeyholeCam.y) / 45.0;
            if (dissolve == 0.0 || off >= 0.0) return 1.0;
            float r = clamp((uKeyholeDia.y - uKeyholeDia.x) * uKeyholeDia.x * SOWX_KH_SIZE / depth
                            + uKeyholeDia.x, uKeyholeDia.x, uKeyholeDia.y);
            float r2 = r * r;
            vec2 cv = vec2(2.0 * gl_FragCoord.x * uPixel.x - 1.0, 2.0 * gl_FragCoord.y * uPixel.y - 1.0);
            if (uPixel.x < uPixel.y) cv.x *= uPixel.z; else cv.y /= uPixel.z;
            float d2 = dot(cv, cv);
            if (d2 >= r2) return 1.0;
            float noise = SowxNoise3D(world) * 0.5 + 0.5;
            float lvl = 1.0 - d2 / r2 - noise * SOWX_KH_NOISE + 0.5 / off;
            lvl *= clamp(world.z - SOWX_KH_FLOOR - uPlayerPos.z, 0.0, 1.0);
            if (lvl >= SOWX_KH_PUNCH) return -1.0;
            if (lvl > 0.0) return 1.0 - SOWX_KH_BLACK * (lvl * lvl / (SOWX_KH_PUNCH * SOWX_KH_PUNCH));
            return 1.0;
        }

        /// Scene fog: GetFog + the ApplyFog lerp. Two uniforms and a mix, which is all the old generation's
        /// inc_fog.shd is -- the shaped fog in sow-topdata's fork is a look decision that belongs in that
        /// fork's override, not in the embedded text.
        /// #132 moved this lerp across the encode: the engine fogs in DISPLAY space (fslit's tail is
        /// ApplyKeyhole, RevertColorSpace, ApplyFog) and the owned frame no longer has a display-space point
        /// to fog at. Both ends of the mix are linear here instead, which is the physically right space for
        /// it and a knowingly different look on a foggy frame -- #124 welded the engine's order and this
        /// ticket unwelds it.
        vec3 SowxFog(vec3 lin, float depth)
        {
            float f = uFogParams.x == 0.0 ? 0.0 : (depth - uFogParams.y) * uFogParams.w;
            return mix(lin, uFogColor.rgb, clamp(f, 0.0, 1.0));
        }

        """;

    /// <summary>The one clip-space expression EVERY owned vertex stage computes, character for character.
    /// #119's prepass writes the depth the colour pass then tests LEQUAL against, and <c>invariant
    /// gl_Position</c> only guarantees a bit-identical result across programs for the same sequence of
    /// operations -- the lit stage's former <c>uVP * (model * pos)</c> association is a different sequence,
    /// and a ULP of drift the wrong way punches holes in lit surfaces. A content override (#92) that rewrites
    /// this expression, or drops the invariant qualifier, reintroduces exactly that.</summary>
    private const string ClipExpr = "gl_Position = uVP * d.model * vec4(aPos, 1.0);";

    /// <summary>A vertex stage whose depth the prepass may re-test: it computes <see cref="ClipExpr"/> and
    /// declares it invariant. The embedded stages satisfy this by construction (SelfCheck asserts it); a
    /// content override has to say so in its own text.</summary>
    private static bool DepthSafe(string vs)
        => vs.Contains(ClipExpr) && vs.Contains("invariant gl_Position;");

    /// <summary>Does this stage's text declare the per-draw record the CPU is staging? The layout is a
    /// contract between <see cref="DrawFloats"/> and whatever GLSL actually compiled -- and a content
    /// override (#92) is a copy of that GLSL held somewhere else, so it goes stale the moment the record
    /// grows. #120 grew it from four vec4s to six: a stage still declaring four reads every draw's model
    /// matrix and material colours out of its NEIGHBOUR's record, which renders as a black, streaked world
    /// with nothing at all in the log. <see cref="SelfCheck"/> cannot catch it -- it only sees the embedded
    /// text. Counted rather than matched character-for-character, so an override may still reformat or
    /// rename the fields.</summary>
    private static bool RecordSafe(string src)
    {
        int at = src.IndexOf("struct SowxDraw", StringComparison.Ordinal);
        if (at < 0) return true;                        // a stage that never declares it cannot disagree
        int end = src.IndexOf('}', at);
        string decl = end < 0 ? src[at..] : src[at..end];
        return decl.Split("vec4").Length - 1 == (DrawFloats - 16) / 4;
    }

    /// <summary>#126: does this stage pair carry the cutout and the per-draw alpha? A content override (#92)
    /// replaces the stage wholesale, and one written before #126 links CLEANLY without them --
    /// <see cref="RecordSafe"/> passes (the record did not grow), <see cref="DepthSafe"/> passes (the clip
    /// expression did not change), and <c>glUniform1f</c> on the missing uniform's -1 location is a legal
    /// no-op. What ships is then a world with no alpha discard and a constant 1.0 alpha: grass and fences
    /// render as opaque quads and windows as walls, silently. So it is checked on the source that actually
    /// compiled, and a stage that lacks it is rebuilt from the embedded text.
    /// <para>Named terms rather than the whole line, so an override may still restyle everything around
    /// them -- what it may not do is drop them.</para></summary>
    private static bool CutoutSafe(string vs, string fs)
        => fs.Contains("uAlphaCut") && (fs.Contains("vAlpha") ? vs.Contains("vAlpha") : fs.Contains("rot.w"));

    /// <summary>#137: does this VERTEX stage carry the skinning branch? Same shape, same reason, as
    /// <see cref="CutoutSafe"/> -- and the same trap. A <c>sowx_*_vs</c> written before #137 links
    /// perfectly: <see cref="RecordSafe"/> passes (the record did not grow, the bone base rides in
    /// <c>feat.y</c>), <see cref="DepthSafe"/> passes (the clip expression did not change), and the missing
    /// bone attributes are simply disabled. What ships is then a skinned draw taking
    /// <c>!SowxDisplaces(8)</c> -- the literal clip expression -- and rendering the robe in its BIND POSE at
    /// the node matrix, silently.
    /// <para>Not academic, and not only a developer's problem: on a server the HAK wins over a local
    /// override directory, so a HAK that has not caught up with the extender is exactly this case on live
    /// content while a singleplayer test looks right. Checked only while the class is armed -- an override
    /// predating #137 is perfectly correct when nothing skins.</para></summary>
    private static bool SkinSafe(string vs) => !_skinning || vs.Contains("SOWX_FEAT_SKIN");

    /// <summary>#132: does this FRAGMENT stage write LINEAR? Same shape and same trap as
    /// <see cref="CutoutSafe"/> and <see cref="SkinSafe"/>, and the one with the widest blast radius yet: a
    /// <c>sowx_*_fs</c> written before #132 ends in <c>SowxRevert(...)</c>, links perfectly (the record did
    /// not grow, the clip expression did not change, <c>uSoftClip</c> at -1 is a legal no-op) and writes
    /// display-space values into a target the resolve then encodes a SECOND time. The frame is washed out,
    /// end to end, and nothing says so.
    /// <para>On a server the HAK wins over a local override directory, so a HAK that has not caught up with
    /// the extender is exactly this case on live content while a singleplayer test looks right -- which is
    /// why the answer is to rebuild from the embedded text rather than to log and carry on.</para></summary>
    internal static bool LinearSafe(string fs) => !fs.Contains("SowxRevert") && !fs.Contains("SowxSoftClip");

    private const string OwnedVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUV;

        """ + DrawBufDecl + FeatDecl + """
        uniform mat4 uVP;
        uniform int uDrawBase;
        invariant gl_Position;
        out vec2 vUV;
        flat out float vAlpha;         // #126: the per-draw alpha multiplier, forwarded rather than re-read

        """ + FeatVs + BoneVs + """
        void main()
        {
            SowxDraw d = uDraw[uDrawBase + gl_DrawID];
            float anchorZ;
            vec4 pos = SowxFeatPos(d, aPos, anchorZ);
            int bb = SowxBoneBase(d);
            if (bb >= 0) pos = SowxSkinMat(bb) * vec4(aPos, 1.0);
            // The skinned arm takes the DISPLACED expression, not a third one, and takes it first: a skinned
            // position cannot be the literal clip expression the prepass compiled, which is why #137 keeps
            // this class out of the prepass (ShaderFeatures.OutOfPrepass says so in one place for both).
            if (bb >= 0) gl_Position = uVP * d.model * pos;
            else if (!SowxDisplaces(int(d.feat.x))) gl_Position = uVP * d.model * vec4(aPos, 1.0);
            else gl_Position = uVP * d.model * pos;
            vUV = vec2(d.rot.x * aUV.x - d.rot.y * aUV.y, d.rot.y * aUV.x + d.rot.x * aUV.y);
            vAlpha = d.rot.w;
        }
        """;
    /// <summary>The unlit variant: the engine draws these parts with lighting disabled (MatStatic's flat-white
    /// branch -- cap 5 off, or a <c>mat+0x98</c> material), so flat texture IS their vanilla appearance.</summary>
    private const string OwnedFs = """
        #version 460 core
        in vec2 vUV;
        flat in float vAlpha;
        uniform sampler2D uTex;
        uniform int uViz;
        // #126: the engine's fAlphaDiscardValue -- 0.2 on the classes it draws under Enable(1), -1.0 (i.e.
        // no test at all) on the rest. A uniform here because the class is a per-batch fact.
        uniform float uAlphaCut;
        out vec4 frag;
        void main()
        {
            if (uViz == 4) { frag = vec4(1.0, 0.0, 1.0, 1.0); return; }
            vec4 t = texture(uTex, vUV);
            if (uAlphaCut >= 0.0 && t.a <= uAlphaCut) discard;
            // #132: the target is LINEAR, so the texel's display-space encode comes off here too -- the
            // probe and the frame want the same thing now. Same exponent OwnedLights.SowxLinear uses,
            // spelled out because this stage does not include that lib.
            frag = vec4(pow(max(t.rgb, vec3(0.0)), vec3($SRGB$)), t.a * vAlpha);
        }
        """;

    /// <summary>#119's depth-only stage. Deliberately has NO resref: a stage that writes nothing but depth
    /// has nothing an author could restyle, and an override is one more copy of <see cref="ClipExpr"/> to
    /// keep bit-identical. The fragment stage is empty -- colour writes are masked off, and a program with no
    /// fragment stage at all is a portability gamble for nothing.</summary>
    private const string DepthVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;

        """ + DrawBufDecl + """
        uniform mat4 uVP;
        uniform int uDrawBase;
        invariant gl_Position;
        void main()
        {
            SowxDraw d = uDraw[uDrawBase + gl_DrawID];
            gl_Position = uVP * d.model * vec4(aPos, 1.0);
        }
        """;
    private const string DepthFs = """
        #version 460 core
        void main() { }
        """;

    /// <summary>#124: the depth stage for a batch the camera dissolves. Without it those batches leave the
    /// prepass entirely, and <c>Camera::GetCanKeyholeDissolve</c> answers "CAN this dissolve" -- a near-static
    /// property of a third of the owned set in a tiled interior -- while the aperture that actually discards
    /// is a small circle around the PC. Excluding them measured x8,12 -> x15,19 overdraw and doubled the
    /// colour pass, i.e. it handed back most of #119.
    /// <para>So the prepass runs the SAME discard the colour pass will, and writes exactly the holes it is
    /// going to punch. Also has no resref, and for a stronger reason than <see cref="DepthVs"/>: an override
    /// here that drifted from the colour stage's aperture by one constant would write depth over fragments
    /// the colour pass then discards -- the room behind the wall culled, silently.</para></summary>
    private const string DepthKeyVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;

        """ + DrawBufDecl + """
        uniform mat4 uVP;
        uniform int uDrawBase;
        invariant gl_Position;
        out vec3 vWorld;
        out float vDepth;
        flat out int vDraw;
        void main()
        {
            int id = uDrawBase + gl_DrawID;
            SowxDraw d = uDraw[id];
            gl_Position = uVP * d.model * vec4(aPos, 1.0);
            // ponytail: vWorld is the same product the colour stage forms, but it reaches it through `pos`
            // (SowxFeatPos's undisplaced return), so the two are not guaranteed bit-identical the way
            // gl_Position is. Worst case is one fragment at the punch-through threshold deciding differently
            // between the passes -- a sub-pixel speck on an edge the noise already makes ragged. Only a
            // displacing part could make it worse, and those never reach this stage at all. If it ever shows,
            // the fix is to give this stage SowxFeatPos too and mirror the expression character for character.
            vWorld = (d.model * vec4(aPos, 1.0)).xyz;
            vDepth = gl_Position.w;
            vDraw = id;
        }
        """;
    private const string DepthKeyFs = """
        #version 460 core
        in vec3 vWorld;
        in float vDepth;
        flat in int vDraw;

        """ + DrawBufDecl + FeatDecl + KeyFogDecl + """

        """ + KeyFogFs + """
        void main()
        {
            if (SowxKeyhole(uDraw[vDraw].rot.z, vWorld, vDepth) < 0.0) discard;
        }
        """;

    /// <summary>The lit variant's vertex stage. Beyond the fullbright one it carries world position, world
    /// normal and the view depth: <c>gl_Position.w</c> IS the linear view depth under a perspective
    /// projection, so the fragment stage finds its froxel slice without a second matrix.</summary>
    private const string OwnedLitVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUV;
        layout(location = 2) in vec3 aNrm;

        """ + DrawBufDecl + FeatDecl + """
        uniform mat4 uVP;
        uniform int uDrawBase;
        invariant gl_Position;
        out vec2 vUV;
        out vec3 vWorld;
        out vec3 vNrm;
        out float vDepth;
        out float vAnchorZ;            // #123: undisplaced world height, for the roof anchor discard
        flat out int vDraw;

        """ + FeatVs + BoneVs + """
        void main()
        {
            int id = uDrawBase + gl_DrawID;
            SowxDraw d = uDraw[id];
            vec4 pos = SowxFeatPos(d, aPos, vAnchorZ);
            // #137: a posed skinmesh REPLACES the feature position -- the two never coexist (see
            // ShaderFeature.Skin) -- and the same blended matrix then carries the normal, so a cloak is lit
            // off the surface it actually has rather than the one it had in bind pose.
            int bb = SowxBoneBase(d);
            mat4 skin = bb >= 0 ? SowxSkinMat(bb) : mat4(1.0);
            if (bb >= 0) pos = skin * vec4(aPos, 1.0);
            vec4 world = d.model * pos;
            // The undisplaced branch is written out in full rather than folded into the line below, because
            // #119's prepass re-tests this exact expression and `invariant` only holds over an identical
            // SEQUENCE of operations -- not merely identical values. So the test is "does this part displace",
            // NOT "does it have a feature": a nokeyhole or flowmap part does not move, therefore stays in the
            // prepass, and therefore must reach the literal expression the prepass compiled. Only a displacing
            // part takes the other branch, and the CPU excludes exactly those from the prepass with the same
            // predicate -- no text check can catch the two disagreeing, so they mirror each other by
            // construction and SelfCheck holds it.
            // The skinned arm takes the DISPLACED expression, not a third one, and takes it first -- see
            // the fullbright stage for why, and ShaderFeatures.OutOfPrepass for the prepass side of it.
            if (bb >= 0) gl_Position = uVP * d.model * pos;
            else if (!SowxDisplaces(int(d.feat.x))) gl_Position = uVP * d.model * vec4(aPos, 1.0);
            else gl_Position = uVP * d.model * pos;
            vDepth = gl_Position.w;
            // shaded at the DISPLACED position, which is where the vanilla shaders light a swaying plant too
            vWorld = world.xyz;
            vNrm = mat3(d.model) * (bb >= 0 ? mat3(skin) * aNrm : aNrm);
            vUV = vec2(d.rot.x * aUV.x - d.rot.y * aUV.y, d.rot.y * aUV.x + d.rot.x * aUV.y);
            vDraw = id;
        }
        """;

    /// <summary>The lit variant's fragment stage: the engine's area ambient + directional diffuse, then every
    /// point light the froxel holds, then the material terms -- the combine order of inc_lighting.shd's
    /// ComputeLighting (ambient scaled by the material ambient and a sun-facing wrap, diffuse by the material
    /// diffuse, both multiplied onto the LINEARIZED texture).
    /// <para>The sun term is attenuated by <see cref="OwnedLights.SunShadowLib"/>'s cascade lookup (#94);
    /// ambient deliberately is not, it is the term that stands for light arriving from everywhere else.</para>
    /// <para>#120 folds the material read in: GGX specular on every lit part (vanilla runs one on all of them
    /// and the owned pass dropped it on 100%), the five extra stock .mtr texture slots, and the four stock
    /// parameter names honoured as declared.</para>
    /// <para>#121 folds the last of the <c>ENVIRONMENT_MAP == 1</c> branches in: ambient specular off the
    /// per-area <see cref="EnvProbe"/> cube on every lit part (the engine gates that term on the specular
    /// define, not on an env-map define, so vanilla feeds it everywhere), plus the diffuse alpha read as
    /// legacy content's reflectivity mask.</para>
    /// <para>ponytail: no synthesis beyond what the stock shaders already do for a declared-nothing material
    /// (#122).</para></summary>
    private const string OwnedLitFsHead = """
        #version 460 core
        in vec2 vUV;
        in vec3 vWorld;
        in vec3 vNrm;
        in float vDepth;
        in float vAnchorZ;             // #123: undisplaced world height, for the roof anchor discard
        flat in int vDraw;             // this fragment's SowxDraw record: the material colours live there now

        """ + DrawBufDecl + FeatDecl + KeyFogDecl + """
        uniform sampler2D uTex;
        uniform sampler2D uFlowTex;    // #123 slot 6: RG flow direction, only ever bound on a flowmap part
        // #120: the five stock .mtr slots beyond the diffuse, on units 3..7 (1 and 2 are the shadow maps).
        // Sampled only where the record's slot mask says the material actually bound one.
        uniform sampler2D uNrmTex;     // slot 1: tangent-space normal, xy in rg
        uniform sampler2D uSpecTex;    // slot 2: specularity in r
        uniform sampler2D uRoughTex;   // slot 3: roughness in r
        uniform sampler2D uHeightTex;  // slot 4: height, read for surface occlusion
        uniform sampler2D uIllumTex;   // slot 5: self-illumination
        uniform sampler2D uAoTex;      // #146 slot 7: baked ambient occlusion in r
        uniform vec3 uAreaAmbient;
        uniform vec3 uAreaDiffuse;
        uniform vec3 uAreaDir;         // world-space surface->light for the area directional (sun/moon)
        uniform vec3 uEyePos;          // #120: this pass's camera, for the specular view vector
        uniform int uViz;              // SOWX_LIGHTVIZ, see OwnedLitFsMain
        // #121: the per-area IBL probe, holding LINEAR radiance (EnvProbe captures with uCapture = 1, which
        // is what skips the display-space encode below). uProbeMips 0 = nothing captured yet, so no ambient
        // specular at all -- the pre-#121 behaviour, never a half-written cube.
        uniform samplerCube uProbe;
        uniform float uProbeMips;
        uniform float uProbeGain;      // SOWX_PROBE_GAIN, the ambient-specular dial
        uniform int uSceneEnv;         // does the AREA declare an env map (scene+0x1e8)? see below
        uniform int uCapture;          // 1 = this draw IS the probe capture: stay linear, never sample uProbe
        // #126: the engine's fAlphaDiscardValue, per batch -- 0.2 on the cutout and blended classes,
        // -1.0 (no test) on opaque and additive.
        uniform float uAlphaCut;
        out vec4 frag;

        // Slot mask bits, matching OwnedDrawService's 1 << (slot - 1).
        const int SOWX_SLOT_NORMAL = 1, SOWX_SLOT_SPEC = 2, SOWX_SLOT_ROUGH = 4,
                  SOWX_SLOT_HEIGHT = 8, SOWX_SLOT_ILLUM = 16;
        // #146: bit 6, texture slot 7 -- the AO map. It multiplies the SAME occlusion factor the height
        // map's local term feeds, so a material carrying both gets one combined ambient scale.
        const int SOWX_SLOT_AO = 128;
        // Synthesis floors for a material that declares nothing, read straight off the OLD-generation
        // inc_material.shd the installed client actually runs (#107 §11): fSpecularityMin 0.04 (the old gen's
        // name -- the split-include fork calls it fGeneratedSpecularityMin), fGeneratedRoughnessMin 0.125
        // unconditionally, fGeneratedRoughnessMax 0.6 at SHADER_QUALITY_MODE > 1 (0.65 below it, and the
        // owned lobe is GGX + Schlick geometric shadowing unconditionally, which IS that setting), and the
        // specular-map metallicness curve (fMetallicnessModifier 3.0 * fGeneratedMetallicnessOffset 0.2 = 0.6).
        const float SOWX_SPEC_MIN = 0.04, SOWX_ROUGH_MAX = 0.6, SOWX_ROUGH_MIN = 0.125;
        // metallicness curve, both of the old gen's branches: scale * specularity - scale * offset, where the
        // offset is 0.2 with a specular map and fGeneratedSpecularityMin without one.
        const float SOWX_METAL_SCALE_MAP = 3.0, SOWX_METAL_BIAS_MAP = 0.6,
                    SOWX_METAL_SCALE_BARE = 10.0, SOWX_METAL_BIAS_BARE = 0.4;
        // ApplyColorSpace(0.5) at the engine's 2.2 gamma -- the mid-grey inc_material.shd divides the
        // albedo's peak channel by when it synthesises a specular colour.
        const float SOWX_MID_GREY = 0.2176;
        // #121: bit 5 of the same slot mask -- the material declares an env map (Material::HasEnvMap, i.e.
        // mat+0x40). NO texture is bound for it: the probe replaces what it held. What survives is the
        // engine's own "this is shiny" flag on 2002 content, which drives the synthesis below.
        const int SOWX_SLOT_ENV = 64;
        // #123: bit 5, texture slot 6 -- the flow map, and the only slot outside the stock five. Enrolled
        // only on a flowmap part, so on everything else this bit is clear and uFlowTex is never sampled.
        const int SOWX_SLOT_FLOW = 32;
        // inc_material.shd:143-144 -- an env-mapped material with no specular map takes its specularity from
        // the diffuse alpha (fEnvMapLevel = 1 - alpha), and its roughness down toward the floor with it.
        // TWO modifiers, not one: fLegacyEnvMapSpecularityModifier 8.0 and fLegacyEnvMapRoughnessModifier
        // 3.0 (:60-62). Sharing 8.0 would saturate the roughness curve at envLevel 0.125 and render every
        // legacy shiny surface at the floor. 3.0 is the OLD generation's value, which is the one this client
        // runs (#107 s11); the split-include fork in sow-topdata says 2.5.
        const float SOWX_ENV_SPEC_MAX = 0.98, SOWX_ENV_SPEC_SCALE = 8.0, SOWX_ENV_ROUGH_SCALE = 3.0;
        // inc_standard.shd:250-252: the albedo is desaturated toward this grey in proportion to reflectivity,
        // so the reflection is not counted twice. DISPLAY space -- the engine does it before the :253 linearise.
        const vec3 SOWX_ENV_GREY = vec3(0.666667);
        """;
    /// <summary>#122: material synthesis -- what a part gets for every stock .mtr parameter it does NOT
    /// declare. Lifted out of the fragment main so it is a pure function of already-sampled values: that is
    /// what lets <c>check-shaders.py</c> fold it to constants off-device and check "declared beats
    /// synthesised" against the engine's own generators without a GPU.
    /// <para>The reference is <c>inc_material.shd</c>'s <c>SetupSpecularity</c>, from the OLD generation the
    /// installed client actually loads (<c>data/base_shaders.bif</c>) -- NOT the split-include fork, which
    /// differs here (roughness max 0.55, legacy env roughness modifier 2.5).</para>
    /// <para>Two deliberate deviations from stock, both because the owned pass has no permutation defines:
    /// the quality-gated constants are taken at the high setting (the owned lobe is GGX + Schlick geometric
    /// shadowing unconditionally, which IS <c>SHADER_QUALITY_MODE &gt; 1</c>), and a map's presence is read
    /// off the live slot mask where stock reads a compile-time <c>#if</c>.</para></summary>
    private const string MaterialLib = """

        /// (Specularity, Roughness, Metallicness) -- declared wins, then the map, then the generated floor.
        vec3 SowxSynthPbr(vec3 declared, int slots, float envLevel, float specTexel, float roughTexel)
        {
            float envShiny = min(envLevel * SOWX_ENV_SPEC_SCALE, 1.0);
            float envRough = min(envLevel * SOWX_ENV_ROUGH_SCALE, 1.0);
            bool sm = (slots & SOWX_SLOT_SPEC) != 0;
            float spec = declared.x;
            if (spec <= 0.0)
            {
                if (sm) spec = specTexel;
                // #121: env map + no specular map = legacy "shiny" content, and this is the engine's own
                // translation of it (inc_material.shd, "mainly for the sake of enabling specularity on
                // legacy content").
                else if (envShiny > 0.0) spec = mix(SOWX_SPEC_MIN, SOWX_ENV_SPEC_MAX, envShiny);
                else spec = SOWX_SPEC_MIN;
            }
            float rough = declared.y;
            if (rough <= 0.0)
            {
                // A ROUGHNESS MAP IS DECLARED CONTENT: it is taken whole, and the env curve below never
                // touches it -- stock applies that curve only where the roughness was generated
                // (inc_material.shd puts the min() inside the specular-map branch, not after it).
                if ((slots & SOWX_SLOT_ROUGH) != 0) rough = roughTexel;
                else
                {
                    // a specular map with no roughness map is pre-PBR content: translate rather than trust it
                    rough = sm ? mix(SOWX_ROUGH_MIN, SOWX_ROUGH_MAX, (1.0 - spec) * (1.0 - spec))
                               : SOWX_ROUGH_MAX;
                    // ...and the env map drives it toward the same floor, on TOP of that translation. With
                    // no maps at all the min() is stock's bare assignment: the mix never exceeds the max.
                    if (envLevel > 0.0)
                        rough = min(rough, mix(SOWX_ROUGH_MAX, SOWX_ROUGH_MIN, envRough));
                }
            }
            float metal = declared.z;
            // the old gen keys this curve on whether a SPECULAR map is present -- 3.0/0.2 with one, 10.0/0.04
            // without (fMetallicnessModifier * fGeneratedMetallicnessOffset). Collapsing them to one branch
            // would generate visibly different metal for content that declares Specularity but ships no map.
            if (metal <= 0.0)
                metal = clamp((sm ? SOWX_METAL_SCALE_MAP : SOWX_METAL_SCALE_BARE) * spec
                              - (sm ? SOWX_METAL_BIAS_MAP : SOWX_METAL_BIAS_BARE), 0.0, 1.0);
            return vec3(spec, rough, metal);
        }

        /// The material's specular colour: CustomSpecularColor where declared (w = the CPU-side "not black"
        /// test, which is stock's own sentinel), else generated off the albedo.
        vec3 SowxSpecColor(vec4 declared, vec3 base, float metal, int slots)
        {
            if (declared.w > 0.0) return SowxLinear(declared.rgb);
            // stock takes plain white as the non-metal base once a height map is bound -- the peak-channel
            // rescue below is for flat content, and a height-mapped material already carries its crevices
            if ((slots & SOWX_SLOT_HEIGHT) != 0) return mix(vec3(1.0), base, metal);
            // non-metals should be white, but dark diffuse texels stand in for crevices, so the peak channel
            // scales it -- inc_material.shd's own reasoning, in its own words
            return mix(vec3(min(1.0, max(base.r, max(base.g, base.b)) / SOWX_MID_GREY)), base, metal);
        }
        """;

    private const string OwnedLitFsMain = """
        void main()
        {
            vec3 N = normalize(vNrm);
            // SOWX_LIGHTVIZ: attribute a dim frame to a TERM instead of guessing from the composite.
            // 1 = point-light diffuse alone (torch bright here but the frame dark => the torch is fine and
            //     something else lost intensity); 2 = cluster occupancy, green empty -> red full (a black
            //     froxel where the torch stands means the binning, not the light, is at fault).
            // 4 = coverage: every owned draw flat, so what STAYS textured is what the engine still draws.
            //     Cyan lit / magenta fullbright, so the two owned variants are told apart too.
            if (uViz == 4) { frag = vec4(0.0, 1.0, 1.0, 1.0); return; }
            // 5 = the LOCAL cube term alone: white lit, black occluded, flat white where no cube-bearing
            //     light reaches. A pillar that casts nothing here is not writing depth; one that is black
            //     here but bright in the composite is being washed out by a second light.
            if (uViz == 5) { frag = vec4(vec3(SowxLocalShadowViz(vWorld, N, vDepth)), 1.0); return; }
            if (uViz == 1) { frag = vec4(SowxClusteredDiffuse(vWorld, N, vDepth), 1.0); return; }
            if (uViz == 2)
            {
                float n = float(uClCount[SowxCluster(vDepth)]) / float(SOWX_CLUSTER_CAP);
                frag = vec4(n, 1.0 - n, 0.0, 1.0);
                return;
            }
            SowxDraw d = uDraw[vDraw];
            int feat = int(d.feat.x);
            // #123, shd_roofkn's fragment half: the toolset "vertex anchor" is a scrap of mesh authored high
            // above the walkmesh with its normals pointing down, so the toolset can transform the roof
            // without letting the builder collide with it. It must never be seen in game. roofknm dropped it.
            if (feat == SOWX_FEAT_ROOFKN && vAnchorZ > 100.0) discard;
            int slots = int(d.pbr.w);
            vec2 uv = vUV;
            // #123, shd_flowmap: the RG map is a per-texel flow DIRECTION, and the diffuse slides along it in
            // two half-offset phases blended by their own distance from the midpoint -- the blend is what
            // stops each phase's wrap showing as a flash. fMixingTime 5.0 and fFlowMapMod 1.0 are the
            // shader's own hardcoded constants; only fSlideSpeed comes from the .mtr.
            // These are DIFFUSE-only coordinates, deliberately kept off `uv`: the ticket scopes the feature
            // to "offsets diffuse UVs", and the maps below must go on sampling the unslid vUV. Sliding them
            // to phase 1 alone -- which is what reassigning `uv` here would do -- is worse than not sliding
            // them at all, because an unblended phase SNAPS at every wrap while the diffuse over it does not.
            // ponytail: vanilla slides the normal and height maps too, blending both phases the same way.
            // That is two more taps on flowmap fragments; do it here if static ripples ever read wrong.
            vec2 uvA = uv, uvB = uv;
            float flowMix = 0.0;
            if (feat == SOWX_FEAT_FLOWMAP && (slots & SOWX_SLOT_FLOW) != 0)
            {
                vec2 flow = texture(uFlowTex, uv).rg * 2.0 - 1.0;
                float t1 = (float(uTimeOfDay) / 1000.0) / 5.0;
                float c1 = fract(t1), c2 = fract(t1 + 2.5);
                uvA = uv - d.feat.y * flow * c1;
                uvB = uv - d.feat.y * flow * c2;
                flowMix = abs(c1 - 0.5) * 2.0;
            }
            vec3 V = normalize(uEyePos - vWorld);
            // #152: how deep into the cheap band this fragment sits (0 = full cost, 1 = fully banded).
            float shadeLod = SowxShadeLod(vDepth);
            float occl = 1.0;
            // #146: outside the normal/height branch below -- a material may declare an AO map and nothing
            // else, and the engine's screen-space SSAO (an FB effect, downstream of this pass entirely) does
            // not see the contact detail a baked map carries.
            if ((slots & SOWX_SLOT_AO) != 0 && shadeLod < 1.0) occl *= mix(texture(uAoTex, uv).r, 1.0, shadeLod);
            if ((slots & (SOWX_SLOT_NORMAL | SOWX_SLOT_HEIGHT)) != 0 && shadeLod < 1.0)
            {
                // Cotangent frame from screen-space derivatives (Mikkelsen), NOT the engine's vTangent /
                // fHandedness streams at attribute locations 4/5 (GLRender::ConnectShaderData's bind table).
                // Those would mean two more harvested attributes on every owned VAO plus a per-VDO "does this
                // mesh even carry them" branch, and the derivative frame gets mirrored UVs right for free. It
                // also picks the tile texture rotation up automatically, because vUV arrives already rotated
                // -- the stock stage has to undo it by hand (inc_standard.shd, transpose(mat2(m_texture))).
                // ponytail: four derivatives on normal-mapped fragments. Harvest locations 4/5 into the owned
                // VAO if the quality ever asks for it.
                vec3 dp1 = dFdx(vWorld), dp2 = dFdy(vWorld);
                vec2 du1 = dFdx(uv), du2 = dFdy(uv);
                vec3 p2 = cross(dp2, N), p1 = cross(N, dp1);
                vec3 T = p2 * du1.x + p1 * du2.x;
                vec3 B = p2 * du1.y + p1 * du2.y;
                float inv = inversesqrt(max(max(dot(T, T), dot(B, B)), 1e-12));
                if ((slots & SOWX_SLOT_HEIGHT) != 0)
                {
                    // inc_material.shd's SetupOcclusion: the height against a blurrier tap of itself IS the
                    // local self-occlusion (fOcclusionVicinityRange 2.0, base 1.0, modifier 0.0).
                    // ponytail: no parallax displacement of the UVs -- that is the same map read again, per
                    // fragment, in a loop. #122 owns what else the height map is worth.
                    float o = texture(uHeightTex, uv, 2.0).r - texture(uHeightTex, uv).r;
                    occl *= 1.0 - clamp(o, 0.0, 0.99);
                }
                if ((slots & SOWX_SLOT_NORMAL) != 0)
                {
                    vec3 nm;
                    nm.xy = texture(uNrmTex, uv).rg * 2.0 - 1.0;
                    nm.z = sqrt(max(1.0 - dot(nm.xy, nm.xy), 0.0));
                    N = normalize(mat3(T * inv, B * inv, N) * nm);
                }
            }
            // 7 = the SHADING NORMAL as colour, AFTER the normal map. Flat wedges with straight edges
            //     here are the mesh's own normals: no diffuse term, #143's wrap included, can hide a
            //     DISCONTINUITY in N, and nothing else tells that apart from a terminator crossing a facet.
            if (uViz == 7) { frag = vec4(N * 0.5 + 0.5, 1.0); return; }
            // 8 = the occlusion factor: white unoccluded. #146's map and the height term are the only two
            //     things that write it, so a frame that is white everywhere means no material in sight
            //     declared either -- which is the state shipped content is in today.
            if (uViz == 8) { frag = vec4(vec3(occl), 1.0); return; }
            float sunRaw = clamp(dot(N, uAreaDir), 0.0, 1.0);
            float sunNdl = SowxWrap(dot(N, uAreaDir), uWrapN);
            // the wrapped value, not the raw one -- a fragment the wrap has just lit must still be able to
            // be in shadow, or the band the wrap opens up glows through every caster
            // #152: the sun's PCF taps fade to lit across the band -- the cascades cover 120u and the far
            // half of that is exactly where their texels are coarsest and fog is thickest.
            float sunVis = shadeLod >= 1.0 ? 1.0 : mix(SowxSunShadow(vWorld, N, sunNdl), 1.0, shadeLod);
            // 3 = sun visibility alone: white lit, black shadowed. A frame that looks unshadowed reads here
            //     as "the cascades are empty" or "nothing selected a cascade", which the composite cannot say.
            if (uViz == 3) { frag = vec4(vec3(sunVis), 1.0); return; }
            // Material colours go in RAW: inc_lighting.shd uses materialFrontAmbient (517), materialFrontDiffuse
            // (529) and materialFrontEmissive (625) with no ApplyColorSpace. Only the TEXTURE is linearised
            // (inc_standard.shd:253). Linearising the material terms too double-darkened every non-white material.
            vec3 matAmbient = d.amb.rgb;
            vec3 matDiffuse = d.dif.rgb;
            vec4 texel = texture(uTex, uvA);
            if (flowMix > 0.0) texel = mix(texel, texture(uTex, uvB), flowMix);
            // #126: the cutout, on the FINAL diffuse alpha and ahead of every other branch -- a discarded
            // fragment must cost neither the material synthesis nor a probe sample, and the probe capture
            // has to punch the same holes the screen does or the cube reflects solid foliage.
            if (uAlphaCut >= 0.0 && texel.a <= uAlphaCut) discard;
            // #121: the diffuse ALPHA is the reflectivity mask on env-mapped content (inc_standard.shd:248,
            // fEnvMapLevel = 1 - alpha). A diffuse with no alpha channel reads 1.0, i.e. level 0 -- which is
            // why this costs nothing on the parts that are not env-mapped.
            float envLevel = (slots & SOWX_SLOT_ENV) != 0 ? 1.0 - texel.a : 0.0;
            texel.rgb = mix(texel.rgb, SOWX_ENV_GREY, envLevel);
            vec3 albedo = SowxLinear(texel.rgb);
            // #122: the maps are sampled here and the synthesis itself is a pure function -- it has to fold
            // to a constant off-device (check-shaders.py) for the declared-beats-synthesised rule to be
            // checkable at all. A slot with nothing bound is not sampled, exactly as before.
            float specTexel = (slots & SOWX_SLOT_SPEC) != 0 && shadeLod < 1.0 ? texture(uSpecTex, uv).r : 0.0;
            float roughTexel = (slots & SOWX_SLOT_ROUGH) != 0 && shadeLod < 1.0 ? texture(uRoughTex, uv).r : 0.0;
            vec3 pbr = SowxSynthPbr(d.pbr.xyz, slots, envLevel, specTexel, roughTexel);
            float spec = pbr.x, rough = pbr.y, metal = pbr.z;
            vec3 base = albedo * matDiffuse;
            vec3 specColor = SowxSpecColor(d.spc, base, metal, slots);
            vec3 F0 = specColor * spec;

            vec3 sun = uAreaDiffuse * (sunNdl * sunVis);
            // #143's wrap is a DIFFUSE fix -- it stands in for light bleeding across a low-poly terminator,
            // which is a subsurface/interreflection effect. A specular highlight has no such excuse: fed the
            // wrapped dot, a glossy face angled away from the sun picks up a lobe it cannot geometrically
            // see. The lobe takes the raw dot, the way the ambient hemisphere already does.
            vec3 sunSpec = uAreaDiffuse * (sunRaw * sunVis);
            vec3 pointSpec;
            vec3 point = SowxClusteredLight(vWorld, N, V, vDepth, rough, spec, pointSpec);
            // The engine's split: every light accumulates its own lobe, then ONE view-side normalisation
            // (fSpecularBaseModifier) and the material's specular colour scale the sum
            // (inc_lighting.shd:456 and :615). fSpecularity is already inside the lobe via the Fresnel term.
            vec3 specLight = (sunSpec * SowxSpecLobe(N, V, uAreaDir, rough, spec) + pointSpec)
                             * (specColor * SowxSpecBase(max(dot(N, V), 0.0), rough));
            vec3 diffuse = sun + point;
            // the 0.15 ambient floor and the sun-facing wrap are inc_lighting.shd:679 verbatim; the height
            // map's occlusion scales the AMBIENT, which is the term it is actually occluding
            vec3 ambient = uAreaAmbient * matAmbient * mix(0.15, 1.0, sunRaw) * occl;
            // #121: ambient specular -- the term vanilla feeds from the area env map on EVERY lit part
            // (inc_lighting.shd:456-512 gates it on SPECULAR_LIGHT, not on ENVIRONMENT_MAP) and the owned
            // pass dropped on all of them. Weighted the ENGINE's way: the env sample TINTS the light already
            // arriving at this fragment -- `EnvironmentColor = (AmbientLight + DiffuseLight) * envTexel`,
            // then `* fEnvironmentSpecularity` -- rather than adding radiance on top of it. Added radiance
            // (the split-sum form this first shipped as) makes a brick road reflect the sky like wet stone
            // and lights unlit corners out of the probe; this form cannot exceed the light that is there.
            // ponytail: no split-sum BRDF and no specular Fresnel (inc_lighting.shd:463, SPECULAR_FRESNEL).
            // SOWX_PROBE_GAIN is the dial if the term wants more presence than the engine gave it.
            // ...and fed only where the material DECLARES an env map. RenderFlat @0x450eb0 also binds the
            // area's own env map (currentscene+0x1e8, verified) for plain parts, so being faithful to the
            // BINDING would put this term on everything in such an area -- and live that reflects the sky off
            // city cobblestone, which vanilla plainly does not do: its authored spheremap is a dim texel,
            // while the probe carries real scene radiance. SOWX_PROBE_AREAWIDE=1 opts back into the engine's
            // rule (uSceneEnv); the default is the case the reflection exists for.
            if (uCapture == 0 && uProbeMips > 0.0 && (uSceneEnv != 0 || (slots & SOWX_SLOT_ENV) != 0))
            {
                vec3 env = textureLod(uProbe, reflect(-V, N), rough * (uProbeMips - 1.0)).rgb;
                specLight += (ambient + diffuse) * env * F0 * uProbeGain;
            }
            // 6 = the specular lobe alone: exactly what #120 adds. A part that reads flat in the composite is
            //     either black here (no lobe at all) or washed out by the diffuse, and nothing else tells the
            //     two apart.
            if (uViz == 6) { frag = vec4(specLight, 1.0); return; }
            // energy split: what the specular lobe reflects, the diffuse does not -- inc_lighting.shd's own
            // `DiffuseLight * (1.0 - fSpecularity)` and `AmbientLight * (1.0 - fSpecularity)`
            vec3 total = (ambient + diffuse * matDiffuse) * (1.0 - spec) + d.emi.rgb;
            // Specular and self-illumination land AFTER the albedo multiply: a highlight is light off the
            // surface, not light through it (inc_lighting.shd:615 `ColorIO.rgb += SpecularColor *
            // SpecularLight`, and the texUnit5 add just above it).
            vec3 lit = albedo * total + specLight;
            if ((slots & SOWX_SLOT_ILLUM) != 0) lit += SowxLinear(texture(uIllumTex, uv).rgb);
            // #132: everything from here down stays LINEAR -- the screen and the probe want the same values
            // now, and PostChain's resolve does the frame's one encode. The probe never wanted anything else
            // (#121: glGenerateMipmap filters what is STORED, and filtering display-space values darkens
            // them), so what changed is the screen path catching up to it.
            // The dissolve and the fog are both linear multiplies/lerps here. #124 matched the fslit tail's
            // own order (ApplyKeyhole, RevertColorSpace, ApplyFog) and #132 knowingly drops the encode out of
            // the middle of it, which moves fog into linear -- a deliberate look change on foggy frames.
            // Both terms are camera-dependent, so the probe capture takes neither: a dissolve baked into the
            // cube is a permanent hole in every reflection the area ever shows.
            if (uCapture != 0) { frag = vec4(lit, 1.0); return; }
            float kh = SowxKeyhole(d.rot.z, vWorld, vDepth);
            if (kh < 0.0) discard;
            // #126: rot.w is the per-draw alpha multiplier -- the part's own alpha times the tile fade
            // factor. It reaches the framebuffer only where the class blends; the opaque set draws with
            // GL_BLEND off, so its alpha channel is written and ignored, exactly as before.
            // #132: linear out. No encode, no shoulder -- the resolve owns both for the whole frame.
            frag = vec4(SowxFog(lit * kh, vDepth), texel.a * d.rot.w);
        }
        """;

    /// <summary>Throw away a program built from a stale content override and rebuild it from the embedded
    /// stages. Preferred over standing the pass down: the embedded source is the one that agrees with the
    /// record the CPU stages, so the frame is CORRECT here and merely un-restyled -- and the override's
    /// author gets a line naming the file to update rather than a black world to bisect. Cold: once per
    /// program per session.</summary>
    private static uint RebuildEmbedded(uint stale, string vs, string fs, string what, ref string vsSrc)
    {
        Gl.DeleteProgram(stale);
        _log?.Info($"OwnedDrawService: a content .shd for the {what} program disagrees with the pass -- either " +
                   $"it declares a per-draw record that is not the {DrawFloats * sizeof(float)}-byte one being " +
                   "staged (#92 override vs #120's grown SowxDraw), or it predates #126 and carries no " +
                   "uAlphaCut/per-draw alpha (which would render every cutout and blended surface solid). " +
                   "Ignoring it and building the embedded stages, which agree. Regenerate the override from " +
                   "the current embedded stage to get it back.");
        vsSrc = vs;
        return GlProgram.Graphics(vs, fs, _log, $"OwnedDrawService {what} program (embedded)");
    }

    private static bool EnsureProgram()
    {
        if (_prog46 == 0 && !_progFailed)
        {
            uint prog = GlProgram.Graphics(ShdFullbrightVs, OwnedVs, ShdFullbrightFs, OwnedFs,
                                           _log, "OwnedDrawService fullbright program",
                                           out string vsSrc, out string fsSrc);
            if (prog != 0 && !(RecordSafe(vsSrc) && RecordSafe(fsSrc) && CutoutSafe(vsSrc, fsSrc)
                               && SkinSafe(vsSrc) && LinearSafe(fsSrc)))
            {
                prog = RebuildEmbedded(prog, OwnedVs, OwnedFs, "fullbright", ref vsSrc);
            }
            _vsDepthSafe &= DepthSafe(vsSrc);
            if (prog == 0) { _progFailed = true; return false; }
            _prog46 = prog;
            _locVp = GlProgram.Loc(prog, "uVP"); _locBase = GlProgram.Loc(prog, "uDrawBase");
            _locTex = GlProgram.Loc(prog, "uTex"); _locViz = GlProgram.Loc(prog, "uViz");
            _locCut = GlProgram.Loc(prog, "uAlphaCut");
            _locFeat = FeatLocs(prog);
            _log?.Info("OwnedDrawService: fullbright program ready (4.6 context)");
        }
        if (_prog46 == 0) return false;
        if (_lighting && _progLit46 == 0 && !_litFailed) EnsureLitProgram();
        return true;
    }

    /// <summary>Build the depth-only program. A failure disarms the prepass for the session and the colour
    /// pass keeps its pre-#119 behaviour -- shading every fragment -- which is slower, never wrong.</summary>
    private static bool EnsureDepthProgram()
    {
        if (_progDepth != 0) return true;
        if (_depthFailed) return false;
        // A content vertex override (#92) replaces the stage wholesale, so what SelfCheck asserts about the
        // embedded constants is no longer what runs -- and a rewritten association loses colour-pass fragments
        // to LEQUAL as speckled holes with nothing in the log. Checked on the source that ACTUALLY compiled,
        // not on whether an override exists at all: every real deployment ships these shaders, and refusing
        // the pass on their mere presence would mean it never runs anywhere it matters.
        if (!_vsDepthSafe)
        {
            _depthFailed = true;
            _log?.Info("OwnedDrawService: a content vertex .shd does not carry `" + ClipExpr +
                       "` under `invariant gl_Position;` -- no depth prepass (update the override to match " +
                       "the embedded stage, or the colour pass would lose fragments to LEQUAL)");
            return false;
        }
        if (Gl.ColorMask == null)
        {
            _depthFailed = true;
            _log?.Info("OwnedDrawService: glColorMask unresolved -- no depth prepass");
            return false;
        }
        uint prog = GlProgram.Graphics(DepthVs, DepthFs, _log, "OwnedDrawService depth prepass program");
        if (prog == 0)
        {
            _depthFailed = true;
            _log?.Info("OwnedDrawService: depth prepass program unavailable -- the colour pass shades every fragment");
            return false;
        }
        _progDepth = prog;
        _depthVp = GlProgram.Loc(prog, "uVP"); _depthBase = GlProgram.Loc(prog, "uDrawBase");
        // #124's second stage. A failure here is NOT fatal to the prepass -- dissolving batches simply go
        // back to being left out of it, which is what they did before the fix.
        uint keyProg = GlProgram.Graphics(DepthKeyVs, DepthKeyFs, _log,
                                          "OwnedDrawService keyhole depth prepass program");
        if (keyProg != 0)
        {
            _progDepthKey = keyProg;
            _depthKeyVp = GlProgram.Loc(keyProg, "uVP");
            _depthKeyBase = GlProgram.Loc(keyProg, "uDrawBase");
            _depthKeyFeat = FeatLocs(keyProg);
        }
        _log?.Info("OwnedDrawService: depth prepass ready (SOWX_DEPTHPREPASS=0 disables)" +
                   (keyProg != 0 ? "" : " -- WITHOUT the keyhole stage, so dissolving batches stay out of it"));
        return true;
    }

    /// <summary>Build the lit variant. A failure disarms lit ownership for the session (and the next
    /// enrollment hands every lit part back to the engine) -- never a silent fallback to fullbright, which
    /// would look worse than vanilla.</summary>
    private static void EnsureLitProgram()
    {
        // #153: decide bindless BEFORE assembling text -- the transform must apply to the same source the
        // program is built from, and to nothing when the context cannot honour it.
        if (!_bindlessProbed)
        {
            _bindlessProbed = true;
            _bindless = Environment.GetEnvironmentVariable("SOWX_BINDLESS") != "0" && HasBindless();
            _log?.Info(_bindless
                ? "OwnedDrawService: ARB_bindless_texture armed -- lit batches merge across textures (SOWX_BINDLESS=0 reverts)"
                : "OwnedDrawService: bindless texturing unavailable or vetoed -- per-batch texture binds");
        }
        string litFs = OwnedLitFsHead + OwnedLights.LocalShadowLib + OwnedLights.FragmentLib
                       + OwnedLights.SunShadowLib + MaterialLib + KeyFogFs + OwnedLitFsMain;
        if (_bindless) litFs = BindlessFs(litFs);
        uint prog = GlProgram.Graphics(ShdLitVs, OwnedLitVs, ShdLitFs, litFs,
                                       _log, "OwnedDrawService clustered lit program",
                                       out string vsSrc, out string fsSrc);
        if (prog != 0 && !(RecordSafe(vsSrc) && RecordSafe(fsSrc) && CutoutSafe(vsSrc, fsSrc)
                           && SkinSafe(vsSrc) && LinearSafe(fsSrc)
                           && (!_bindless || fsSrc.Contains("SowxTexH"))))
        {
            prog = RebuildEmbedded(prog, OwnedLitVs, litFs, "clustered lit", ref vsSrc);
        }
        // a content override without the handle block compiled fine but still samples bound units: the
        // C# side must keep binding for it, so the whole bindless path stands down for the session
        if (prog != 0 && _bindless && !fsSrc.Contains("SowxTexH") && !litFs.Contains("SowxTexH"))
        {
            _bindless = false;
            _log?.Info("OwnedDrawService: lit stage carries no handle block -- bindless stands down, binds resume");
        }
        _vsDepthSafe &= DepthSafe(vsSrc);
        if (prog == 0)
        {
            _litFailed = true;
            _sinceEnroll = EnrollEvery;   // re-enroll at once so lit parts revert to the engine's own draw
            _log?.Info("OwnedDrawService: clustered lit program unavailable -- lit parts revert to engine drawing");
            return;
        }
        _progLit46 = prog;
        _litVp = GlProgram.Loc(prog, "uVP"); _litBase = GlProgram.Loc(prog, "uDrawBase");
        _litTex = GlProgram.Loc(prog, "uTex");
        _litCut = GlProgram.Loc(prog, "uAlphaCut");
        _litAmbient = GlProgram.Loc(prog, "uAreaAmbient"); _litDiffuse = GlProgram.Loc(prog, "uAreaDiffuse");
        _litDir = GlProgram.Loc(prog, "uAreaDir");
        _litGrid = GlProgram.Loc(prog, "uGrid"); _litTile = GlProgram.Loc(prog, "uTile");
        _litZ = GlProgram.Loc(prog, "uZ"); _litFalloff = GlProgram.Loc(prog, "uFalloff");
        _litWrap = GlProgram.Loc(prog, "uWrap");
        _litAtten = GlProgram.Loc(prog, "uAtten"); _litHot = GlProgram.Loc(prog, "uHot");
        _litWrapN = GlProgram.Loc(prog, "uWrapN");
        _litViz = GlProgram.Loc(prog, "uViz");
        _litEye = GlProgram.Loc(prog, "uEyePos");
        _litSunTex = GlProgram.Loc(prog, "uSunShadow"); _litSunVp = GlProgram.Loc(prog, "uSunVp");
        _litSunTexel = GlProgram.Loc(prog, "uSunTexel"); _litSunBias = GlProgram.Loc(prog, "uSunBias");
        _litSunCount = GlProgram.Loc(prog, "uSunCascades");
        _litCubeTex = GlProgram.Loc(prog, "uLocalShadow"); _litCubeLight = GlProgram.Loc(prog, "uLocalLight");
        _litCubePlane = GlProgram.Loc(prog, "uLocalPlane"); _litCubeCount = GlProgram.Loc(prog, "uLocalCubes");
        _litCubeSoft = GlProgram.Loc(prog, "uLocalSoft");
        _litProbe = GlProgram.Loc(prog, "uProbe"); _litProbeMips = GlProgram.Loc(prog, "uProbeMips");
        _litProbeGain = GlProgram.Loc(prog, "uProbeGain");
        _litSceneEnv = GlProgram.Loc(prog, "uSceneEnv");
        _litCapture = GlProgram.Loc(prog, "uCapture");
        _litFeat = FeatLocs(prog);
        // Bind the two shadow samplers to their units ONCE, here, and not only on the frames a map is fresh.
        // A sampler uniform defaults to unit 0 -- the same unit uTex lives on -- and GL forbids two samplers
        // of different types pointing at one image unit: the draw raises INVALID_OPERATION and is dropped.
        // The fresh path alone is not enough, because a session that never renders a map (an interior start,
        // SOWX_CUBE_COUNT=0) never reaches it, and then EVERY owned lit batch silently vanishes.
        Gl.UseProgram(prog);
        Gl.Uniform1i(_litSunTex, (int)SunUnit);
        Gl.Uniform1i(_litCubeTex, (int)CubeUnit);
        Gl.Uniform1i(_litProbe, (int)EnvProbe.ProbeUnit);
        // #120's five map samplers, for the same reason: left at their default they would all point at unit
        // 0 and read the diffuse as a normal map on every part that declares one.
        string[] pbr = { "uNrmTex", "uSpecTex", "uRoughTex", "uHeightTex", "uIllumTex", "uFlowTex", "uAoTex" };
        for (int i = 0; i < MapSlots; i++) Gl.Uniform1i(GlProgram.Loc(prog, pbr[i]), (int)PbrUnit0 + i);
        _log?.Info("OwnedDrawService: clustered forward+ lit program ready (4.6 context)");
    }

    /// <summary>#123: the five <see cref="FeatDecl"/> uniform locations of one program, in the order
    /// <see cref="UploadFeatureTerms"/> writes them.</summary>
    internal static FeatLoc FeatLocs(uint prog) => new()
    {
        Time = GlProgram.Loc(prog, "uTimeOfDay"),
        Wind = GlProgram.Loc(prog, "uAreaWind"),
        Flags = GlProgram.Loc(prog, "uAreaFlags"),
        Cam = GlProgram.Loc(prog, "uCamPos"),
        Player = GlProgram.Loc(prog, "uPlayerPos"),
        Fog = GlProgram.Loc(prog, "uFogParams"),
        FogColor = GlProgram.Loc(prog, "uFogColor"),
        KhDia = GlProgram.Loc(prog, "uKeyholeDia"),
        KhCam = GlProgram.Loc(prog, "uKeyholeCam"),
        Pixel = GlProgram.Loc(prog, "uPixel"),
    };

    internal struct FeatLoc { internal int Time, Wind, Flags, Cam, Player, Fog, FogColor, KhDia, KhCam, Pixel; }
    private static FeatLoc _locFeat, _litFeat;
    /// <summary>#123: <c>GLRender::m_shaderExposedRuntimeData</c>, the struct the engine's own SetUniforms
    /// uploads these five from. Null (symbol unresolved) leaves the wind still and the roofs wherever they
    /// were authored, which is the pre-#123 look, not a misrender.</summary>
    private static byte* _runtime;
    /// <summary>Field offsets inside it, from pairing GLRender::ConnectShaderData (uniform name -&gt;
    /// location slot) with GLRender::SetUniforms (location slot -&gt; field).</summary>
    /// <summary>#147: <c>areaGlobalWind</c> in plan, as the CPU sees it -- a world vector whose magnitude is
    /// the speed, which is what <see cref="WaterBodies"/> projects fetch onto and what the wave model scales
    /// by. Zero before the runtime block resolves, and a still area is genuinely zero; the shader decides
    /// what a dead calm looks like, not this.
    /// <para>Deliberately NOT <c>sqrt(dot(w.x, w.y))</c>. That spelling appears in the #123 feature port and
    /// in the stock <c>shd_windgrass_vs.shd</c> it reproduces, where it means <c>sqrt(x*y)</c> and goes NaN
    /// the moment the components differ in sign. Faithful there, wrong here.</para></summary>
    /// <summary>#147: the RAW engine wind magnitude alongside the metres/second the sea state is fed. The
    /// conversion (<see cref="WaterPass.WindScale"/>) is an admitted guess, and this line is how it gets
    /// calibrated -- read it in an area whose weather you know and set SOWX_WATER_WIND from it.</summary>
    internal static string DescribeWind()
    {
        var w = AreaWind();
        float raw = w.Length();
        return $"wind raw={raw:F3} -> {raw * WaterPass.WindScale:F1}m/s";
    }

    private static Vector2 AreaWind()
        => _runtime == null ? default
         : new Vector2(*(float*)(_runtime + RtAreaWind), *(float*)(_runtime + RtAreaWind + 4));

    private const int RtTimeOfDay = 0x18, RtAreaWind = 0x28, RtAreaFlags = 0x3c,
                      RtPlayerPos = 0x4c, RtCamPos = 0x64;
    /// <summary>#124's two more fields of the same struct, from the same pairing: <c>cameraPitch</c> at
    /// +0x84 and <c>playerCameraDist</c> at +0x8c. Both feed the keyhole aperture.</summary>
    private const int RtCamPitch = 0x84, RtCamDist = 0x8c;
    /// <summary>#124: <see cref="SymbolMap.GlFog"/>'s fields -- start, end, the display-space colour and the
    /// enable flag. <c>fogParams.w</c> is computed, not stored.</summary>
    private const int FogStart = 0x00, FogEnd = 0x04, FogColor = 0x08, FogEnabled = 0x18;
    /// <summary>#124: the keyhole diameter pair inside
    /// <see cref="SymbolMap.GlGlobalShaderConstantsF"/>. Stored unordered -- SetUniforms uploads min as
    /// <c>keyholeDiameterMin</c> and max as <c>keyholeDiameterMax</c>, so the ordering happens here too.</summary>
    private const int KeyholeDiaA = 21, KeyholeDiaB = 22;
    private static float* _fog;
    private static float* _shaderConstF;

    /// <summary>#132: the engine's fog colour, linearised. <c>m_fog</c> stores it display-space -- the stock
    /// shaders lerp toward it AFTER RevertColorSpace -- and the owned frame is linear end to end, so the
    /// curve comes off here rather than in every stage that fogs.</summary>
    private static void UploadFogColour(int loc)
    {
        float* c = stackalloc float[4];
        float* src = _fog + FogColor / 4;
        for (int i = 0; i < 3; i++) c[i] = MathF.Pow(MathF.Max(src[i], 0f), OwnedLights.SrgbGamma);
        c[3] = src[3];
        Gl.Uniform4fv(loc, 1, c);
    }

    /// <summary>#131: just the scene-fog pair, for a program that wants fog and none of the rest of
    /// <see cref="UploadFeatureTerms"/>. Same source, same bool-byte conversion; split out rather than
    /// copied so <see cref="GlFog"/>'s layout stays known in one place.</summary>
    internal static void UploadFog(int fogLoc, int fogColorLoc)
    {
        float* px = stackalloc float[4];
        if (_fog != null)
        {
            px[0] = ((byte*)_fog)[FogEnabled] != 0 ? 1f : 0f;
            px[1] = _fog[FogStart / 4]; px[2] = _fog[FogEnd / 4];
            float span = px[2] - px[1];
            px[3] = span != 0f ? 1f / span : 0f;
            Gl.Uniform4fv(fogLoc, 1, px);
            UploadFogColour(fogColorLoc);
        }
        else { px[0] = px[1] = px[2] = px[3] = 0f; Gl.Uniform4fv(fogLoc, 1, px); }
    }

    /// <summary>Feed one program the engine's own live world state -- the same five values its own shaders
    /// read under these names. Once per program per pass; nothing here is per part.</summary>
    internal static void UploadFeatureTerms(in FeatLoc loc, int* viewport)
    {
        // #124: pixelParams is the ONE term not read out of an engine global -- SetUniforms builds it from
        // the window size, and this pass may be rendering into the probe cube instead, so it comes from the
        // viewport actually bound. Everything else below is the engine's own live value.
        float w = viewport[2] > 0 ? viewport[2] : 1f, h = viewport[3] > 0 ? viewport[3] : 1f;
        float* px = stackalloc float[4];
        px[0] = 1f / w; px[1] = 1f / h; px[2] = w / h;
        Gl.Uniform3fv(loc.Pixel, 1, px);
        if (_fog != null)
        {
            // fogEnabled is a BOOL BYTE, and SetUniforms @0x4cdb33 converts it: movzbl 0x18(%r12) then
            // cvtsi2ss. Reading those 4 bytes as a float instead gives bit pattern 1 -- a denormal, which
            // GLSL is free to flush to zero, so `uFogParams.x == 0.0` would go TRUE on an FTZ driver and
            // the owned pass would render unfogged on that machine and only that machine.
            px[0] = ((byte*)_fog)[FogEnabled] != 0 ? 1f : 0f;
            px[1] = _fog[FogStart / 4]; px[2] = _fog[FogEnd / 4];
            float span = px[2] - px[1];
            px[3] = span != 0f ? 1f / span : 0f;
            Gl.Uniform4fv(loc.Fog, 1, px);
            UploadFogColour(loc.FogColor);
        }
        else { px[0] = px[1] = px[2] = px[3] = 0f; Gl.Uniform4fv(loc.Fog, 1, px); }
        if (_shaderConstF != null)
        {
            float a = _shaderConstF[KeyholeDiaA], b = _shaderConstF[KeyholeDiaB];
            px[0] = MathF.Min(a, b); px[1] = MathF.Max(a, b);
            Gl.Uniform2fv(loc.KhDia, 1, px);
        }
        if (_runtime == null) return;
        Gl.Uniform1i(loc.Time, *(int*)(_runtime + RtTimeOfDay));

        Gl.Uniform3fv(loc.Wind, 1, (float*)(_runtime + RtAreaWind));
        Gl.Uniform1i(loc.Flags, *(int*)(_runtime + RtAreaFlags));
        Gl.Uniform3fv(loc.Cam, 1, (float*)(_runtime + RtCamPos));
        Gl.Uniform3fv(loc.Player, 1, (float*)(_runtime + RtPlayerPos));
        px[0] = *(float*)(_runtime + RtCamDist); px[1] = *(float*)(_runtime + RtCamPitch);
        Gl.Uniform2fv(loc.KhCam, 1, px);
    }

    /// <summary>The engine's own area terms, captured from SetLightGL's index -1 path. No capture yet (a
    /// pass before the first EnableLights) leaves the surface on ambient alone rather than unlit black.</summary>
    private static void UploadAreaTerms() => UploadAreaTerms(_litAmbient, _litDiffuse, _litDir);

    /// <summary>#127: the same terms, into whichever program's locations the caller names -- the owned grass
    /// pass runs its own program over the same lights, so the area terms cannot be a property of the lit
    /// program's uniform cache.</summary>
    internal static void UploadAreaTerms(int locAmbient, int locDiffuse, int locDir)
    {
        float* v = stackalloc float[3];
        v[0] = v[1] = v[2] = ShadowInputs.HasAreaAmbient ? 0f : DefaultAmbient;
        if (ShadowInputs.HasAreaAmbient)
        {
            // OwnedLights.AmbientScale / SunScale are the live /light dials -- applied here, at the upload,
            // so a retune costs one uniform write and shows on the very next frame.
            var a = ShadowInputs.AreaAmbient * OwnedLights.AmbientScale;
            v[0] = a.X; v[1] = a.Y; v[2] = a.Z;
        }
        else if (!_ambientFallbackLogged)
        {
            _ambientFallbackLogged = true;
            _log?.Info("OwnedDrawService: no area ambient captured yet -- owned lit parts on the grey stand-in " +
                       "(if this persists, SetLightGL's index -1 path never fired)");
        }
        Gl.Uniform3fv(locAmbient, 1, v);
        var d = ShadowInputs.HasAreaDiffuse ? ShadowInputs.AreaDiffuse * OwnedLights.SunScale : default;
        v[0] = d.X; v[1] = d.Y; v[2] = d.Z;
        Gl.Uniform3fv(locDiffuse, 1, v);
        var dir = ShadowInputs.HasAreaDiffuse ? ShadowInputs.AreaDiffuseDir : new Vector3(0f, 0f, 1f);
        v[0] = dir.X; v[1] = dir.Y; v[2] = dir.Z;
        Gl.Uniform3fv(locDir, 1, v);
    }

    /// <summary>Stand-in area ambient for the frames before the engine's first index -1 light bind. Grey, not
    /// black: a missing capture must not paint the world dark.</summary>
    private const float DefaultAmbient = 0.25f;

    /// <summary>Texture units the shadow maps bind on. Unit 0 is the part's diffuse and gets rebound per
    /// batch, so the maps live above it and are bound once per pass.</summary>
    internal const uint SunUnit = 1, CubeUnit = 2;
    private static int _litCubeTex = -1, _litCubeLight = -1, _litCubePlane = -1, _litCubeCount = -1,
                       _litCubeSoft = -1;
    private static int _litProbe = -1, _litProbeMips = -1, _litProbeGain = -1, _litCapture = -1,
                       _litSceneEnv = -1;
    /// <summary>Shadow depth bias in WORLD units, sent per cascade as a fraction of that cascade's published
    /// depth range so it stays a constant world distance whatever the fit. Small because the receiver's normal
    /// offset carries most of the acne fight; SOWX_CSM_BIAS tunes it.</summary>
    private static float _sunWorldBias = 0.06f;

    /// <summary>Point the lit program at <see cref="CasterPass"/>'s cascade array, or tell it there is nothing
    /// to sample. Everything the receiver needs comes from that pass's published render-time values -- the
    /// matrices are never re-derived here, and the freshness test is the single gate that covers interiors, a
    /// vanilla stretch and a disarm alike.</summary>
    private static void UploadSunCascades()
        => UploadSunCascades(_litSunTex, _litSunVp, _litSunTexel, _litSunBias, _litSunCount);

    /// <summary>#127: the cascade receiver's uniforms, into a named program's locations. Same values, same
    /// freshness gate; only where they land differs.</summary>
    internal static void UploadSunCascades(int locTex, int locVp, int locTexel, int locBias, int locCount)
    {
        if (locCount < 0) return;
        if (!CasterPass.CascadesFresh) { Gl.Uniform1i(locCount, 0); return; }
        int n = CasterPass.CascadeCount;
        Gl.ActiveTexture(Gl.TEXTURE0 + SunUnit);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, CasterPass.CascadeTex);
        Gl.ActiveTexture(Gl.TEXTURE0);       // leave unit 0 current: the batch loop rebinds diffuse textures
        Gl.Uniform1i(locTex, (int)SunUnit);
        fixed (float* vp = CasterPass.CascadeVp) Gl.UniformMatrix4fv(locVp, n, 0, vp);
        float* pack = stackalloc float[4];
        for (int i = 0; i < 4; i++) pack[i] = i < n ? CasterPass.CascadeTexel[i] : 0f;
        Gl.Uniform4fv(locTexel, 1, pack);
        for (int i = 0; i < 4; i++)
        {
            float range = i < n ? CasterPass.CascadeRange[i] : 0f;
            pack[i] = range > 0f ? _sunWorldBias / range : 0f;
        }
        Gl.Uniform4fv(locBias, 1, pack);
        Gl.Uniform1i(locCount, n);
    }

    /// <summary>Point the lit program at <see cref="CasterPass"/>'s local-light cube array, or tell it there
    /// is nothing to sample. The per-slot position and far plane are the ones each cube was RENDERED with --
    /// the receiver never re-derives them from the live light, which is what keeps a carried torch's shadow
    /// attached to the torch. Same freshness gate as the cascades.</summary>
    private static void UploadLocalCubes()
        => UploadLocalCubes(_litCubeTex, _litCubeLight, _litCubePlane, _litCubeCount, _litCubeSoft);

    /// <summary>#127: the local-cube receiver's uniforms, into a named program's locations.</summary>
    internal static void UploadLocalCubes(int locTex, int locLight, int locPlane, int locCount, int locSoft)
    {
        if (locCount < 0) return;
        // #139: the softness pair goes out even when there are no cubes -- it is program state, not per-cube
        // state, and a program that only ever sees the no-cubes path would otherwise keep the zeroes.
        if (locSoft >= 0) Gl.Uniform2f(locSoft, OwnedLights.ShadowSoft, OwnedLights.ShadowNearWorld);
        if (!CasterPass.CubesFresh) { Gl.Uniform1i(locCount, 0); return; }
        int n = CasterPass.CubeCount;
        Gl.ActiveTexture(Gl.TEXTURE0 + CubeUnit);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, CasterPass.CubeTex);
        Gl.ActiveTexture(Gl.TEXTURE0);       // leave unit 0 current: the batch loop rebinds diffuse textures
        Gl.Uniform1i(locTex, (int)CubeUnit);
        fixed (float* l = CasterPass.CubeLight) Gl.Uniform4fv(locLight, n, l);
        Gl.Uniform2f(locPlane, CasterPass.CubeNearPlane, 1f / CasterPass.CubeFaceSize);
        Gl.Uniform1i(locCount, n);
    }

    /// <summary>#121: point the lit program at the IBL probe, or tell it there is nothing to sample
    /// (<c>uProbeMips</c> 0). Unbinding rather than leaving the cube bound during a capture is not
    /// belt-and-braces: sampling a texture that is an attachment of the framebuffer being written is
    /// undefined, and the capture draws through this very program.</summary>
    private static void UploadProbe() => UploadProbe(_litProbeMips, _litProbeGain, _litSceneEnv);

    /// <summary>#129: the same terms into a named program's locations -- the owned water stage samples the
    /// same cube as its reflection fallback, so the probe cannot be a property of the lit program's cache.</summary>
    private static void UploadProbe(int locMips, int locGain, int locSceneEnv)
    {
        if (locMips < 0) return;
        bool live = !_capturing && EnvProbe.Ready && EnvProbe.Tex != 0;
        Gl.ActiveTexture(Gl.TEXTURE0 + EnvProbe.ProbeUnit);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, live ? EnvProbe.Tex : 0);
        Gl.ActiveTexture(Gl.TEXTURE0);       // leave unit 0 current: the batch loop rebinds diffuse textures
        Gl.Uniform1f(locMips, live ? EnvProbe.Mips : 0f);
        Gl.Uniform1f(locGain, EnvProbe.Gain);
        Gl.Uniform1i(locSceneEnv, _sceneEnv && EnvProbe.AreaWide ? 1 : 0);
    }

    /// <summary>#121: capture the area's IBL probe -- the same staged batches, drawn six more times into a
    /// small cube from the player's position. Runs at the END of the colour bracket, after both on-screen
    /// passes have issued, for two reasons: the cluster build is per-pass state that each face has to
    /// overwrite (a face lit by the SCREEN's froxels gets the wrong point lights, which is precisely wrong in
    /// the interiors the probe exists for), and trashing it here costs nothing because the next pass rebuilds
    /// it anyway.
    /// <para>Not per frame: <see cref="EnvProbe.Wanted"/> gates on the area, the walked distance and a frame
    /// floor. The #119 meters are saved and restored across it -- a capture's draws are not the frame's
    /// draws, and folding them in would make the overdraw and batching figures lie on capture frames.</para></summary>
    private static void CaptureProbe(int groups, float far)
    {
        IntPtr bsp = _enrollScene != IntPtr.Zero ? *(IntPtr*)(_enrollScene + SymbolMap.SceneBspRoot) : IntPtr.Zero;
        if (!EnvProbe.Wanted(_eyePos, bsp, _frameSeq)) return;
        if (!EnvProbe.Begin(_eyePos, bsp, _frameSeq, far, ProbeClear())) return;

        long draws = _draws, batches = _batches;
        float buildMs = OwnedLights.LastBuildMs;   // the [owned] line's cluster timer is the SCREEN's build
        Vector3 eye = _eyePos;
        _capturing = true;
        _eyePos = EnvProbe.Origin;
        (float fnear, float ffar) = EnvProbe.FacePlanes;
        int* faceVp = stackalloc int[4] { 0, 0, EnvProbe.Size, EnvProbe.Size };
        bool ok = true;
        for (int f = 0; f < 6 && ok; f++)
        {
            ok = EnvProbe.Face(f, out Matrix4x4 vp);
            if (!ok) break;
            Matrix4x4 view = EnvProbe.FaceView(f);
            // A cube face is exactly 90deg, so the projection's p00 and p11 are both cot(45) = 1.
            OwnedLights.Build((float*)&view, 1f, 1f, fnear, ffar);
            IssueBatches(0, groups, (float*)&vp, faceVp, fnear, ffar);
        }
        if (ok) EnvProbe.End(); else Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        _capturing = false;
        _eyePos = eye;
        _draws = draws; _batches = batches;
        OwnedLights.LastBuildMs = buildMs;
    }

    /// <summary>What the probe's faces are cleared to -- the sky stand-in.
    /// <para>ponytail: the skybox is still engine-drawn, so it is absent from the owned set the capture
    /// draws; a black clear would make outdoor metal reflect a black dome, which is WORSE than the stock
    /// spheremap it replaces. The area's own ambient plus half its directional is a defensible sky colour
    /// and costs one uniform. Upgrade path: the real sky, once the skybox class is owned (ADR 0011 step 3).</para></summary>
    private static Vector3 ProbeClear()
    {
        Vector3 amb = ShadowInputs.HasAreaAmbient
            ? ShadowInputs.AreaAmbient * OwnedLights.AmbientScale
            : new Vector3(DefaultAmbient);
        Vector3 sun = ShadowInputs.HasAreaDiffuse ? ShadowInputs.AreaDiffuse * OwnedLights.SunScale : default;
        return amb + sun * 0.5f;
    }

    /// <summary>Build the 4.6-side VAO over the SHARED engine buffers using the harvested formats (VAOs don't
    /// share across contexts; buffers do -- #70). ONLY position, UV and (where the stream exists) normal are
    /// bound, at OUR locations: replicating every enabled engine attrib crashed the driver's vertex-pull JIT
    /// when one carried a client-pointer binding (#81), and neither owned program consumes anything else.</summary>
    private static bool EnsureVao(int v)
    {
        if (_vVao46[v] != 0 && !_vStale[v]) return true;
        if (_vVao46[v] != 0) { uint old = _vVao46[v]; Gl.DeleteVertexArrays(1, &old); _vVao46[v] = 0; }
        uint vao; Gl.GenVertexArrays(1, &vao);
        Gl.BindVertexArray(vao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _vPosBuf[v]);
        Gl.VertexAttribPointer(LocPos, _vPosSize[v], _vPosType[v], 0, _vPosStride[v], (void*)_vPosOff[v]);
        Gl.EnableVertexAttribArray(LocPos);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _vUvBuf[v]);
        Gl.VertexAttribPointer(LocUv, _vUvSize[v], _vUvType[v], 0, _vUvStride[v], (void*)_vUvOff[v]);
        Gl.EnableVertexAttribArray(LocUv);
        if (_vLit[v])
        {
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _vNrmBuf[v]);
            Gl.VertexAttribPointer(LocNrm, _vNrmSize[v], _vNrmType[v], 0, _vNrmStride[v], (void*)_vNrmOff[v]);
            Gl.EnableVertexAttribArray(LocNrm);
        }
        // #137: and the bone streams, on a skinned VDO. Same "one stream and no more" rule -- these two are
        // the only additions, and both are consumed by the skinning branch the owned stages now carry.
        if (_vSkin[v] && _skinning)
        {
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _vBiBuf[v]);
            Gl.VertexAttribPointer(LocBoneIdx, _vBiSize[v], _vBiType[v], _vBiNorm[v], _vBiStride[v], (void*)_vBiOff[v]);
            Gl.EnableVertexAttribArray(LocBoneIdx);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _vBwBuf[v]);
            Gl.VertexAttribPointer(LocBoneW, _vBwSize[v], _vBwType[v], _vBwNorm[v], _vBwStride[v], (void*)_vBwOff[v]);
            Gl.EnableVertexAttribArray(LocBoneW);
        }
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _vElemBuf[v]);
        Gl.BindVertexArray(0);
        _vVao46[v] = vao; _vStale[v] = false;
        return true;
    }

}
