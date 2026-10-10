// CX-88: the SKY, as an Alloy program. What is ahead of the region is the ENVIRONMENT -- the names
// inc_common/inc_uniforms/inc_standard give a sky fragment stage, from Alloy's values, under the ENGINE's
// names -- so the content's own sky stage (e.g. a ~1900-line cloud, star, sun and moon stage)
// drops in unedited. Between the markers is that stage, its main() renamed; the stub is a black dome.
#version 460 core
in vec2 vUV;
flat in float vAlpha;
in vec3 vWorld;
layout(location = 0) out vec4 frag;
#include "cx_featdecl.glsl"
#include "cx_fogdecl.glsl"
#include "cx_fogmix.glsl"
#include "cx_oitout.glsl"
uniform sampler2D uTex;          // material texture 0
uniform vec3 uTint;              // #222: white, or the scene's fog colour for the skyfade gob
uniform float uSkyFog;           // the horizon band (see cx_owned_f.glsl); 0 = no band
uniform mat4 uViewInv;           // the camera's world matrix: column 3 is the eye, which is all stock reads
uniform mat4 m_proj;             // inc_noir's ortho-vs-perspective test reads [3][3]

#define CX_ALLOY 1
#define GAMMA_CORRECTION 1
#define texture2D texture
#define textureCube texture
#ifndef PI
#define PI 3.14159265359
#endif
#define COLOR_WHITE vec4(1.0, 1.0, 1.0, 1.0)
#define VertexColor COLOR_WHITE
#define vTexCoords vUV
#define vPosWorld vWorld
#define texUnit0 uTex
#define m_view_inv uViewInv
#define playerPosition uPlayerPos
#define cameraPosition uCamPos
#define cameraFocus uCamFocus
#define worldtimerTimeOfDay uTimeOfDay
#define areaGlobalWind uAreaWind
#define areaFlags uAreaFlags
// The area's fog colour as the engine's stages see it: DISPLAY space (Alloy linearises at upload, #132).
#define fogColor vec4(pow(max(uFogColor.rgb, vec3(0.0)), vec3(1.0 / $SRGB$)), 1.0)
// The engine's calendar and weather scalars, from m_shaderExposedRuntimeData: the same values
// GLRender::SetUniforms @0x4ccbf0 feeds the engine's own copy of this stage, under the names
// GLRender::ConnectShaderData @0x4ce8e0 binds them to. Macros rather than declarations under those names,
// so a content stage that declares one for itself (inc_sunm does, for moduleTimeIntoTransition) has its
// line dropped instead of colliding.
uniform int uModYear, uModMonth, uModDay, uModHour, uModDawn, uModDusk;
uniform float uModTransition, uWeatherDensity;
#define moduleYear uModYear
#define moduleMonth uModMonth
#define moduleDay uModDay
#define moduleHour uModHour
#define moduleDawnHour uModDawn
#define moduleDuskHour uModDusk
#define moduleTimeIntoTransition uModTransition
#define areaWeatherDensity uWeatherDensity

// inc_common's colour-space pair and its exact curve: ApplyColorSpace goes display -> WORKING space.
vec3 ApplyColorSpace(vec3 c)  { return sign(c) * pow(abs(c), vec3($SRGB$)); }
vec4 ApplyColorSpace(vec4 c)  { return vec4(ApplyColorSpace(c.rgb), c.a); }
vec3 RevertColorSpace(vec3 c) { return sign(c) * pow(abs(c), vec3(1.0 / $SRGB$)); }
vec4 RevertColorSpace(vec4 c) { return vec4(RevertColorSpace(c.rgb), c.a); }
// The stock working colour, and the texel inc_standard's setup call multiplies into it -- sampled by the
// wrapper before the stage's body runs, in DISPLAY space, which is where the stage expects to find it.
vec4 FragmentColor;
vec4 CxWholeTexel;

// $VANILLA-MAIN $SKYFS$
void CxVanMain() { FragmentColor = vec4(0.0, 0.0, 0.0, 1.0); }
// $VANILLA-END

void main()
{
    CxWholeTexel = texture(uTex, vUV, uLodBias);
    FragmentColor = vec4(0.0, 0.0, 0.0, 1.0);
    CxVanMain();
    // The stage hands back DISPLAY space (its own RevertColorSpace tail, and every layer after it is
    // display-space arithmetic); the Alloy frame is LINEAR, so the one encode comes off here. That is what
    // the #136 SkyDecodeFs fullscreen pass used to do a pass later over every depth==1.0 pixel.
    // The stage was authored against the engine's UNORM target: [0,1], no NaN. The frame and the probe cube
    // are float, and one NaN texel poisons the probe's whole mip chain and SH (every reflection went white).
    vec4 c = mix(clamp(FragmentColor, 0.0, 1.0), vec4(0.0), isnan(FragmentColor));
    vec3 rgb = pow(c.rgb, vec3($SRGB$));
    if (uSkyFog > 0.0 && uFogParams.x != 0.0)
        rgb = CxFogMix(rgb, vWorld, 1.0 - smoothstep(0.0, uSkyFog, normalize(vWorld - uEyePos).z));
    frag = CxOitOut(vec4(rgb, c.a));
}
