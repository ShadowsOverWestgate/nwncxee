#version 460 core
in vec2 vUV;
in float vViewZ;
in float vSeed;
in vec3 vLight;
uniform vec3 uTint;
uniform float uAlpha;
uniform float uSoft;
#include "cx_scenedepth.glsl"
/// Rec.709 luminance: how much of its scatter a drop also occludes.
const vec3 CX_WX_LUMA = vec3(0.2126, 0.7152, 0.0722);
void main()
{
    float a = (1.0 - vUV.x * vUV.x) * (1.0 - vUV.y * vUV.y);
    a = a * a;
    if (a <= 0.01) discard;
    float gap = CxSceneViewZ(gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0))) + vViewZ;
    float k = clamp(gap * uSoft, 0.0, 1.0);
    if (uViz == 1) { frag = vec4(vec3(k), 1.0); return; }
    // R1: premultiplied scatter, blended ONE / ONE_MINUS_SRC_ALPHA. rgb is the light the streak throws
    // toward the eye, weighted by the share of the pixel it covers; alpha is the extinction behind it,
    // and it rides the SAME scatter -- a streak with no light on it neither adds nor hides anything.
    // Unlit in the dark, a glint by a lamp, and never the grey film a straight-alpha drop drew.
    float cov = a * k * k * vSeed;
    vec3 scatter = uTint * vLight;
    frag = vec4(scatter * cov, cov * uAlpha * min(dot(scatter, CX_WX_LUMA), 1.0));
}
