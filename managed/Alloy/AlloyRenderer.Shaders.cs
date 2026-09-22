using System;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>#171: the names the Alloy pass's embedded GLSL is reached by. The text itself lives in
/// <c>managed/Alloy/Shaders/*.glsl</c> since #707; what is left here is one accessor per stage or library
/// the C# side still names -- a contract predicate that greps it, a program that compiles it -- as
/// <c>partial</c>-class members of the same type, so every call site still names them unqualified.</summary>
public sealed unsafe partial class AlloyRenderer
{

    /// <summary>Resrefs the Alloy colour pass looks up before falling back to the embedded stage (#92). A
    /// content author drops <c>&lt;name&gt;.shd</c> in a hak, <c>override/</c> or NWSync and gets that stage
    /// instead; the lit fragment resref replaces the whole composed stage, <see cref="AlloyLights.FragmentLib"/>
    /// included, so an override must carry its own clustered-lighting helpers -- and may spell their sizing
    /// constants as the <see cref="ShaderResource.Tokens"/> the embedded source uses.</summary>
    internal const string ShdFullbrightVs = "cx_own_vs", ShdFullbrightFs = "cx_own_fs";
    internal const string ShdLitVs = "cx_ownlit_vs", ShdLitFs = "cx_ownlit_fs";
    /// <summary>#129's water stage. Its own pair rather than the lit one's, so an author can rewrite the
    /// water surface without forking every lit part in the world.</summary>
    internal const string ShdWaterVs = "cx_water_vs", ShdWaterFs = "cx_water_fs";

    /// <summary>The per-draw block, reached through <c>uDrawIdx[uDrawBase + gl_DrawID]</c>: everything that
    /// varies per part lives here instead of in a uniform, which is what lets one multidraw cover a whole
    /// group (#84). The records are PERSISTENT -- indexed by ownership slot and written only when the part
    /// moves -- while the draw order is per pass; the index array is the only per-draw thing the frame
    /// uploads whole, at four bytes each (#242). <c>binding = 4</c> must stay in step with <see cref="BindDraw"/>.
    /// <para><c>rot</c> is (cos, sin) of the tile texture rotation: the engine's own vertex stage does
    /// <c>vVertexTexCoords = mat2(m_texture) * vTcIn.xy</c> (base_shaders.bif), and m_texture is
    /// PartTriMesh::TransformTextureMatrix's Z-rotation -- columns (c,s) and (-s,c). <c>amb</c>/<c>dif</c>/
    /// <c>emi</c> are the material colours, written only for lit draws. <c>pbr</c> is (Specularity, Roughness,
    /// Metallicness, live slot mask) and <c>spc</c> is (CustomSpecularColor.rgb, declared?) -- the four stock
    /// .mtr parameters (#120). <c>feat</c> is (<see cref="ShaderFeature"/>, and the three parameters that
    /// feature reads); per draw for the same reason: one batch spans materials asking for different shaders
    /// (#123). <c>prev</c> is the model matrix of the PREVIOUS frame, rolled forward on every frame the record
    /// is built, so it equals <c>model</c> for everything standing still; only <see cref="MotionVs"/> reads
    /// it (#392).</para>
    /// <para>ponytail: both vertex stages read this block, so the whole Alloy pass needs vertex-stage SSBO
    /// access. GL 4.6 only REQUIRES <c>MAX_VERTEX_SHADER_STORAGE_BLOCKS >= 0</c>, so a strictly-conformant
    /// driver may refuse the link; every desktop driver in practice reports >= 8, and the failure is loud
    /// (program build fails -> DISARMED, by name). Upgrade path: probe the limit and put the fullbright
    /// variant's records in a UBO, whose required vertex-stage minimum is 12 blocks.</para></summary>
    internal static string DrawBufDecl => ShaderSource.Get("AlloyRenderer.DrawBufDecl.glsl");

    /// <summary>#123: the feature ids, as GLSL. One contract with <see cref="ShaderFeature"/> -- the C# value
    /// is staged into <c>d.feat.x</c> and compared against these, so <see cref="SelfCheck"/> asserts the two
    /// agree rather than trusting a comment to keep them in step.</summary>
    internal static string FeatDecl => ShaderSource.Get("AlloyRenderer.FeatDecl.glsl");

    /// <summary>The vertex-side half of the feature switches -- the sine sway of the three wind shaders and
    /// the camera-height branch of the two roof toggles, one stage instead of seven forks (#123). The
    /// arithmetic is the community shaders' verbatim, constants and quirks included: <c>sqrt(dot(wind.x,
    /// wind.y))</c> (a one-component dot, i.e. x*y), the 0.356 no-wind floor, the per-foliage denominators
    /// (grass and leaf /100, plant /1000) and speed multipliers (grass 20000, plant and leaf 7000), the
    /// doubled plant frequency, the root-anchor gradient vs the leaf's flat 5.0, and the roof's four odd-value
    /// <c>areaFlags</c> comparisons. Bug-for-bug is the acceptance criterion: the content is tuned against
    /// these numbers.
    /// <para><c>aPos</c> IS the shaders' <c>vPos</c> and <c>d.model</c> their <c>m_m</c> -- the node matrix
    /// for matrix-class parts and identity for the rest, exactly the model matrix the engine had bound. Returns
    /// the displaced LOCAL position; <c>anchorZ</c> comes back as the UNDISPLACED world height the roof
    /// fragment stage discards the toolset vertex anchor by.</para></summary>
    /// <summary>The skinning half of the Alloy vertex stages (#137). Vertex-side only -- the fragment stages
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
    internal static string BoneVs => ShaderSource.Get("AlloyRenderer.BoneVs.glsl");

    internal static string FeatVs => ShaderSource.Get("AlloyRenderer.FeatVs.glsl");
    /// <summary>#341: the fog, split out of the keyhole so a stage can have one without the other (the
    /// particle stage fogs and never dissolves; the depth prepass dissolves and never fogs).
    /// <para>The ramp is untouched engine: <c>GetFog</c>'s linear lerp between the area author's start and
    /// end, in view depth. What #341 changes is the two ends of it -- the target colour is built per ray
    /// instead of being one constant, and the factor can be thinned by height.</para></summary>
    internal static string FogFs => ShaderSource.Get("AlloyRenderer.FogFs.glsl");

    /// <summary>The keyhole and the fog, in the order every fragment stage wants them. Declared after
    /// both halves: <c>check-shaders.py</c> inlines only constants it has already read.</summary>
    internal static string KeyFogFs => ShaderSource.Get("AlloyRenderer.KeyFogFs.glsl");

    /// <summary>#347: the VANILLA ENVIRONMENT -- everything a spliced stock include would have got from
    /// the <c>#include</c> lines <see cref="VanillaShader"/> drops, in one block ahead of every marked
    /// region so that two spliced files cannot each declare it. That collision is not hypothetical: both
    /// <c>inc_noir.shd</c> and <c>inc_vision.shd</c> include <c>inc_scriptable</c>, and a uniform declared
    /// twice in one compilation unit is a compile error, not a warning.
    /// <para>Macros where Alloy already carries the value under its own name, so a splice costs no extra
    /// upload; uniforms only where it does not.</para>
    /// <para><b><c>CX_ALLOY</c> is the contract with the content repo.</b> A stock file that needs to do
    /// something different when Alloy is the renderer -- because Alloy replaced the engine machinery it
    /// would otherwise read -- branches on this. Old content that has never heard of it compiles exactly as
    /// it does for the engine, which is why the extender and the hak can land in either order.</para>
    /// <para><c>LIGHTING 0</c> is that case's first customer. The engine's forward light arrays
    /// (<c>numLights</c>, <c>lightPosition[]</c>) do not exist here -- Alloy's lights are an SSBO the
    /// clustered pass walks -- so every stock block guarded on them compiles out, and where a file wants a
    /// light term anyway it asks <see cref="CxVisionLight"/> for Alloy's.</para></summary>
    internal static string VanillaEnvDefs => ShaderSource.Get("AlloyRenderer.VanillaEnvDefs.glsl");

    /// <summary>#350: what a hosted stage gets in place of <c>inc_standard</c>. The two calls every stock
    /// fragment stage wraps its own work in are Alloy's: the normal is final before the stage's body runs
    /// and the lighting after it is the clustered pass. The setup call is NOT empty, and that was measured:
    /// a stock main opens with <c>FragmentColor = vec4(1.0)</c> and it is <c>SetupStandardShaderInputs</c>
    /// that multiplies the texel in (<c>inc_standard.shd</c>, <c>FragmentColor *= MainTextureColor</c>),
    /// so an empty stand-in hands every hosted body a WHITE surface -- the weather ground rendered as a
    /// snowfield with the snow at 0. The scriptable block still has to come from somewhere, and stock gets
    /// it through this include.</summary>
    internal static string VanillaStandardFs => ShaderSource.Get("AlloyRenderer.VanillaStandardFs.glsl");

    /// <summary>The declarations half of the environment, after <see cref="VanillaEnvDefs"/>. The macro
    /// half is separate because a hosted stage re-issues it after scoping out its own defines.</summary>
    internal static string VanillaEnvFs => ShaderSource.Get("AlloyRenderer.VanillaEnvFs.glsl");

    /// <summary>#347/#348: the CONTENT HOOK. A function the lit stage calls and does not implement, so
    /// that display-space shading a content repo invented -- SoW's noir desaturation and racial vision --
    /// lives in that repo and not in this one. (#350 deleted the surface hook beside it: a material's own
    /// custom FS states the surface work already, and <see cref="HostFs"/> runs that file instead of
    /// asking content to restate it.)
    /// <para><b>Why the extender carries none of it.</b> The bridge exists so one copy of a shader runs, and
    /// the copy that runs should be the one its author edits. A stock file (<c>inc_target.shd</c>) belongs
    /// to the ENGINE, so splicing it here is faithful; <c>inc_noir</c> and <c>inc_vision</c> are SoW's, and
    /// an extender that embedded even their stubs would be asserting what a content repo's shaders are
    /// called. This extender is optional and drop-in: run it against any module and these two hooks are
    /// no-ops, which is exactly the right answer for content that never asked for them.</para>
    /// <para>The content file is <c>cx_lit_hooks</c> -- one resref, composed by the content out of its own
    /// includes (which <see cref="VanillaShader"/> resolves through the resource chain). The signature
    /// below is the whole contract, and it is generic on purpose: nothing in it names a feature. It stays
    /// an adapter deliberately: "which display filters, in which order" is stated in no stock file, and
    /// deriving it would mean Alloy naming SoW's functions.</para>
    /// <para>It runs after fog, in DISPLAY space, where <c>inc_standard.shd</c> puts the desaturation and
    /// the vision passes.</para></summary>
    internal static string HookFs => ShaderSource.Get("AlloyRenderer.HookFs.glsl");

    /// <summary>#350: the HOSTED content stages -- one region per <see cref="ShaderFeature"/> that names a
    /// custom FS (<see cref="ShaderFeatures.FsNames"/>; <c>SelfCheck</c> holds the two lists together). Each
    /// region is the material's own <c>customShaderFS</c>, run as a function of this program: see
    /// <see cref="VanillaShader.HostText"/> for the rewrite and <see cref="VanillaStandardFs"/> for what
    /// stands in for <c>inc_standard</c>. The stub is the wrapper's own empty signature, so a feature whose
    /// file is missing, stale or broken draws the plain lit path -- and the log names it.
    /// <para>The dispatcher is what the lit main calls, at the point a stock stage runs its body: after the
    /// texel is sampled and the normal is final, before anything lights either. It returns whether a body
    /// ran, so the read-back costs nothing on the parts that have none.</para></summary>
    internal static string HostFs => ShaderSource.Get("AlloyRenderer.HostFs.glsl");

    /// <summary>The SPELL-TARGETING SHAPE -- the red circle (or cone, or rectangle) a caster sees on the
    /// ground while picking a target. Vanilla draws it as arithmetic in <c>inc_standard.shd</c>'s tail, not as
    /// a draw call, so <see cref="RspShim.EngineOverlays"/> is the wrong tool: there is no draw to replay (#346).
    /// <para><b>The shape's code is not written here.</b> It is the engine's own <c>inc_target.shd</c>, spliced
    /// in at compile time by <see cref="VanillaShader"/> from whatever the resource chain supplies -- a hak,
    /// <c>override/</c>, NWSync, or the stock <c>base_shaders.bif</c> -- so <c>sow-topdata</c> customises the
    /// file and nothing in the extender has to know.</para>
    /// <para><b>Between the markers is a STUB, not a snapshot.</b> A copy would be a second implementation
    /// that runs only when the splice failed, so its drift from the content would never be seen. What the
    /// fallback owes is the world, not the feature -- a lit program that fails to build reverts every Alloy
    /// part to engine drawing -- so the stub answers "no shape here" and the log says so.</para>
    /// <para>Everything the file wants from its own includes is in <see cref="VanillaEnvFs"/>. The uniforms it
    /// declares ITSELF (<c>targetShape</c> and friends) are not repeated there -- <c>UploadTargetShape</c>
    /// writes them under the engine's own names, which is what lets the stock text drop in unedited.</para></summary>
    internal static string TargetFs => ShaderSource.Get("AlloyRenderer.TargetFs.glsl");

    /// <summary>The blended class's output stage, shared by every program the class draws through. <c>uOit</c>
    /// 0 = a composited colour; 1 = the weighted-blended accumulation pair (<see cref="OitPass"/>);
    /// 2 = the pair AND a (colour, depth) node appended to the pixel's linked list (#490). The list's depth
    /// test is done here against the opaque depth: an SSBO append is a side effect, and the GL may run
    /// this stage before its own depth test. The node's alpha is the same coverage x rot.w the caster
    /// pass dithers on (#363), so the shadow strength is the same in every mode.</summary>
    internal static string OitOutFs => ShaderSource.Get("AlloyRenderer.OitOutFs.glsl");

    /// <summary>#136's known ceiling, closed: the hosted sky program is the ENGINE's and writes DISPLAY space
    /// into Alloy's LINEAR target, so the resolve encoded it a second time and the sky read brighter than
    /// vanilla. This runs right after the hosted draw, over the pixels it wrote (the depth range and LEQUAL
    /// test the sky pass already holds: only where nothing wrote depth), reading each texel back through a
    /// texture barrier and decoding it once. The frame stays linear everywhere, which is the contract the
    /// resolve's own guard enforces -- the fix could not live there.</summary>
    internal static string SkyDecodeFs => ShaderSource.Get("AlloyRenderer.SkyDecodeFs.glsl");

    /// <summary>The one clip-space expression EVERY Alloy vertex stage computes, character for character.
    /// #119's prepass writes the depth the colour pass then tests LEQUAL against, and <c>invariant
    /// gl_Position</c> only guarantees a bit-identical result across programs for the same sequence of
    /// operations -- the lit stage's former <c>uVP * (model * pos)</c> association is a different sequence,
    /// and a ULP of drift the wrong way punches holes in lit surfaces. A content override (#92) that rewrites
    /// this expression, or drops the invariant qualifier, reintroduces exactly that.</summary>
    internal const string ClipExpr = "gl_Position = uVP * d.model * vec4(aPos, 1.0);";

    internal static string OwnedVs => ShaderSource.Get("AlloyRenderer.OwnedVs.glsl");
    /// <summary>The unlit variant: the engine draws these parts with lighting disabled (MatStatic's flat-white
    /// branch -- cap 5 off, or a <c>mat+0x98</c> material), so flat texture IS their vanilla appearance.</summary>
    internal static string OwnedFs => ShaderSource.Get("AlloyRenderer.OwnedFs.glsl");

    /// <summary>#119's depth-only stage. Deliberately has NO resref: a stage that writes nothing but depth
    /// has nothing an author could restyle, and an override is one more copy of <see cref="ClipExpr"/> to
    /// keep bit-identical. The fragment stage is empty -- colour writes are masked off, and a program with no
    /// fragment stage at all is a portability gamble for nothing.</summary>
    internal static string DepthVs => ShaderSource.Get("AlloyRenderer.DepthVs.glsl");
    internal static string DepthFs => ShaderSource.Get("AlloyRenderer.DepthFs.glsl");

    /// <summary>The feature prepass's fragment stage: the cutout test the colour stages make, and nothing else.
    /// Same texel (unit 0, the batch's diffuse, the lit stage's rotated <c>vUV</c>), same threshold
    /// (<c>AlloyClasses.Discard</c>), same env exemption (an env-mapped material's alpha is reflectivity, not
    /// coverage -- <c>CX_SLOT_ENV</c>). A negative cut (an opaque displaced batch) tests nothing. Flowmap
    /// batches slide their diffuse uv and stay out on the CPU side, so the fetch here is never wrong for them.</summary>
    internal static string DepthFeatFs => ShaderSource.Get("AlloyRenderer.DepthFeatFs.glsl");

    /// <summary>#124: the depth stage for a batch the camera dissolves. Without it those batches leave the
    /// prepass entirely, and <c>Camera::GetCanKeyholeDissolve</c> answers "CAN this dissolve" -- a near-static
    /// property of a third of the Alloy set in a tiled interior -- while the aperture that actually discards
    /// is a small circle around the PC. Excluding them measured x8,12 -> x15,19 overdraw and doubled the
    /// colour pass, i.e. it handed back most of #119.
    /// <para>So the prepass runs the SAME discard the colour pass will, and writes exactly the holes it is
    /// going to punch. Also has no resref, and for a stronger reason than <see cref="DepthVs"/>: an override
    /// here that drifted from the colour stage's aperture by one constant would write depth over fragments
    /// the colour pass then discards -- the room behind the wall culled, silently.</para></summary>
    internal static string DepthKeyVs => ShaderSource.Get("AlloyRenderer.DepthKeyVs.glsl");
    internal static string DepthKeyFs => ShaderSource.Get("AlloyRenderer.DepthKeyFs.glsl");

    /// <summary>The lit variant's vertex stage. Beyond the fullbright one it carries world position, world
    /// normal and the view depth: <c>gl_Position.w</c> IS the linear view depth under a perspective
    /// projection, so the fragment stage finds its froxel slice without a second matrix.</summary>
    internal static string OwnedLitVs => ShaderSource.Get("AlloyRenderer.OwnedLitVs.glsl");

    /// <summary>The lit variant's fragment stage: the engine's area ambient + directional diffuse, then every
    /// point light the froxel holds, then the material terms -- the combine order of inc_lighting.shd's
    /// ComputeLighting (ambient scaled by the material ambient and a sun-facing wrap, diffuse by the material
    /// diffuse, both multiplied onto the LINEARIZED texture).
    /// <para>The sun term is attenuated by <see cref="AlloyLights.SunShadowLib"/>'s cascade lookup (#94);
    /// ambient deliberately is not, it is the term that stands for light arriving from everywhere else.</para>
    /// <para>#120 folds the material read in: GGX specular on every lit part (vanilla runs one on all of them
    /// and the Alloy pass dropped it on 100%), the five extra stock .mtr texture slots, and the four stock
    /// parameter names honoured as declared.</para>
    /// <para>#121 folds the last of the <c>ENVIRONMENT_MAP == 1</c> branches in: ambient specular off the
    /// per-area <see cref="EnvProbe"/> cube on every lit part (the engine gates that term on the specular
    /// define, not on an env-map define, so vanilla feeds it everywhere), plus the diffuse alpha read as
    /// legacy content's reflectivity mask.</para>
    /// <para>ponytail: no synthesis beyond what the stock shaders already do for a declared-nothing material
    /// (#122).</para></summary>
    internal static string OwnedLitFsHead => ShaderSource.Get("AlloyRenderer.OwnedLitFsHead.glsl");
    /// <summary>#122: material synthesis -- what a part gets for every stock .mtr parameter it does NOT
    /// declare. Lifted out of the fragment main so it is a pure function of already-sampled values: that is
    /// what lets <c>check-shaders.py</c> fold it to constants off-device and check "declared beats
    /// synthesised" against the engine's own generators without a GPU.
    /// <para>The reference is <c>inc_material.shd</c>'s <c>SetupSpecularity</c>, from the OLD generation the
    /// installed client actually loads (<c>data/base_shaders.bif</c>) -- NOT the split-include fork, which
    /// differs here (roughness max 0.55, legacy env roughness modifier 2.5).</para>
    /// <para>Two deliberate deviations from stock, both because the Alloy pass has no permutation defines:
    /// the quality-gated constants are taken at the high setting (the Alloy lobe is GGX + Schlick geometric
    /// shadowing unconditionally, which IS <c>SHADER_QUALITY_MODE &gt; 1</c>), and a map's presence is read
    /// off the live slot mask where stock reads a compile-time <c>#if</c>.</para></summary>
    internal static string MaterialLib => ShaderSource.Get("AlloyRenderer.MaterialLib.glsl");

    /// <summary>The lit main, with the weather splices (#618/#619) folded in at their two seams: the surface
    /// rewrite between material synthesis and the lighting, and the wet reflection after the probe term.</summary>
    internal static string OwnedLitFsMain => ShaderSource.Get("AlloyRenderer.OwnedLitFsMain.glsl");
}
