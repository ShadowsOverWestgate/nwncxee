#version 460 core
in vec2 vUV;
in float vViewZ;
in float vSeed;
in vec3 vLight;
uniform vec3 uTint;
uniform float uAlpha;
uniform float uSoft;
#include "cx_scenedepth.glsl"
/// Rec.709 luminance: how much of its scatter a flake also occludes.
const vec3 CX_WX_LUMA = vec3(0.2126, 0.7152, 0.0722);
void main()
{
    float r = length(vUV);
    if (r >= 1.0) discard;
    float a = smoothstep(1.0, 0.5, r);
    float gap = CxSceneViewZ(gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0))) + vViewZ;
    float k = clamp(gap * uSoft, 0.0, 1.0);
    if (uViz == 1) { frag = vec4(vec3(k), 1.0); return; }
    // R1: a flake is a high-albedo scatterer, premultiplied like the streak (ONE / ONE_MINUS_SRC_ALPHA).
    // uAlpha is near 1, so in light it hides what it covers exactly as it did; the extinction still rides
    // the scatter, so an unlit flake is not a grey disc over the scene.
    float cov = a * k * k * (0.5 + 0.5 * vSeed);
    vec3 scatter = uTint * vLight;
    frag = vec4(scatter * cov, cov * uAlpha * min(dot(scatter, CX_WX_LUMA), 1.0));
}
