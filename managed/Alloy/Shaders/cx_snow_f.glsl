#version 460 core
in vec2 vUV;
in float vViewZ;
in float vSeed;
in vec3 vLight;
uniform vec3 uTint;
uniform float uAlpha;
uniform float uSoft;
#include "cx_scenedepth.glsl"
void main()
{
    float r = length(vUV);
    if (r >= 1.0) discard;
    float a = smoothstep(1.0, 0.5, r);
    float gap = CxSceneViewZ(gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0))) + vViewZ;
    float k = clamp(gap * uSoft, 0.0, 1.0);
    if (uViz == 1) { frag = vec4(vec3(k), 1.0); return; }
    frag = vec4(uTint * vLight, a * k * k * uAlpha * (0.5 + 0.5 * vSeed));
}
