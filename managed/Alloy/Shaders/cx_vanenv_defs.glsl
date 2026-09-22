
#define CX_ALLOY 1
#define POSITION_WORLD 1
#define POSITION_VIEW  0
#define LIGHTING 0
#define PI 3.14159265359
#define COLOR_WHITE vec4(1.0, 1.0, 1.0, 1.0)
// inc_standard.shd:64's own value under VERTEX_COLOR 0, inc_common's default; Alloy carries no
// per-vertex colour into the lit path.
#define VertexColor COLOR_WHITE
#define vPosWorld vWorld
#define playerPosition uPlayerPos
// The camera PIVOT, not the eye: inc_uniforms' `cameraFocus`, which the bridge never follows, so
// Alloy owes the value. Content measures a sight range against it because in chase cam it sits on
// the controlled character (inc_vision).
#define cameraFocus uCamFocus
#define worldtimerTimeOfDay uTimeOfDay
// The engine's compatibility preamble, the part of it a spliced file can reach. GLRender builds
// this ahead of every stage it compiles (@0x12f3fd0) and content is written against it.
#define texture2D texture
#define textureCube texture
#define GAMMA_CORRECTION 1
// ...and the one a STAGE declares rather than the preamble: 1 = vertex, 2 = fragment, as every
// stock stage sets for itself (`vsparticle.shd` 1, `fsparticle.shd` 2) and `inc_common.shd` defaults
// to 2 for a file that forgot. This block stands in for the stage a spliced file thinks it is in,
// and that stage is `cx_ownlit_fs` -- so 2. Undefined it is not a compile error, which is the whole
// hazard: `#if SHADER_TYPE == 2` silently reads 0 and the file's fragment half vanishes. `inc_vision`
// is written that way (its body is FRAGMENT ONLY because it reads gl_FragCoord, with an empty
// `ApplyRacialVision` as the #else), so without this the racial-vision hook compiled to a no-op and
// SoW's darkness limiting simply did not happen under Alloy -- no error, no effect.
#define SHADER_TYPE 2
#define vTexCoords vUV
#define texUnit0 uTex
// Material texture slot 6. Stock names it by unit; Alloy binds it as the flow map's sampler, and
// which of the two a material means is its own business -- the slot is the contract, not the name.
#define texUnit6 uFlowTex
// The area's fog colour as the engine's stages see it: DISPLAY space. Alloy linearises it at upload
// (#132), so this undoes that rather than carrying the same number twice.
#define fogColor vec4(pow(max(uFogColor.rgb, vec3(0.0)), vec3(1.0 / $SRGB$)), 1.0)
// The renderer's own encode exponent, for content that has a display-space number to apply to a
// linear value.
#define CX_GAMMA $SRGB$
// #350: inc_material's own resolution of the material terms. A stage that declares one as a
// constant shadows the macro for its own text; a stage that declares none reads and WRITES the
// globals, which is what td_ground_fs's min()/max() idiom does.
#define MATERIAL_ROUGHNESS_VALUE fRoughness
#define MATERIAL_SPECULARITY_VALUE fSpecularity
#define MATERIAL_METALLICNESS_VALUE fMetallicness
#define MATERIAL_SPECULAR_COLOR_VALUE SpecularColor
