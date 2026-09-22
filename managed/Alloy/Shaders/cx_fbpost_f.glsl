// #352: the engine's post program (fsfbpostpr = inc_postpr + main), hosted. What is ahead of the
// region is the ENVIRONMENT: the names inc_common/inc_uniforms give a fragment post stage, from
// Alloy's values. Between the markers is the resource chain's inc_postpr; the stub is a pass-through.
#define CX_ALLOY 1
#define SHADER_TYPE 2
#define GAMMA_CORRECTION 1
#define SHADER_QUALITY_MODE $SQM$
#define POSTPROCESSING_TYPES_ENABLED $POSTMASK$
#define texture2D texture
#define textureCube texture
#define vVertexTexCoords vUV
// texFBColor / texFBDepth are the chain's OWN declarations (inc_framebuffer); Prepare binds them to
// the stack's units by name, so no macro stands between the two and no line has to be stripped.
#ifndef PI
#define PI 3.14159265359
#endif
uniform highp vec3 pixelParams;
#define vPixelSize (pixelParams.xy)
#define invScreenWidth (pixelParams.x)
#define invScreenHeight (pixelParams.y)
#define fAspectRatio (pixelParams.z)
uniform highp vec3 clipParams;
#define nearClip (clipParams.x)
#define farClip (clipParams.y)
#define fDepthToLinearModifier (clipParams.z)
uniform highp mat4 m_view;
uniform highp mat4 m_proj;
uniform int sceneCurrentFrame;
uniform int moduleHour;
uniform int moduleMonth;
uniform int moduleDay;
uniform highp vec3 uPlayerPos;
#define playerPosition uPlayerPos
float sqr(float a) { return a * a; }
float ColorToLuma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
float ColorToLuma(vec4 c) { return ColorToLuma(c.rgb); }

// $VANILLA-SKIP inc_lighting inc_fog inc_shadow
// $VANILLA inc_postpr
vec4 FS_PostProcess() { return texture(uSrc, vUV); }
// $VANILLA-END

vec4 CxStage(vec4 c)
{
    vec4 o = FS_PostProcess();
    o.a = c.a;
    return o;
}
