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
    float a = (1.0 - vUV.x * vUV.x) * (1.0 - vUV.y * vUV.y);
    a = a * a;
    if (a <= 0.01) discard;
    float gap = CxSceneViewZ(gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0))) + vViewZ;
    float k = clamp(gap * uSoft, 0.0, 1.0);
    if (uViz == 1) { frag = vec4(vec3(k), 1.0); return; }
    frag = vec4(uTint * vLight, a * k * k * uAlpha * vSeed);
}
