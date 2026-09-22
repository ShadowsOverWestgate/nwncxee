#version 460 core
in vec3 vBary;
in vec3 vNrm;
flat in vec3 vW0, vW1, vW2;
uniform vec4 uBakeLight;   // xyz = position, w = the lit radius (GetLightAdjustedRadius)
uniform vec3 uBakeColor;   // the walk's colour, linearised in CxPointLight exactly as the loop does
uniform int uBakeMode;     // 0 = coverage only (A' alpha 1), 1 = one light's contribution, additive
layout(location = 0) out vec4 oA;   // sum rgb, coverage alpha
layout(location = 1) out vec4 oB;   // sum luminance * direction
#include "cx_localshadow.glsl"
#include "cx_lightfrag.glsl"
void main()
{
    if (uBakeMode == 0) { oA = vec4(0.0, 0.0, 0.0, 1.0); oB = vec4(0.0); return; }
    // a pushed texel shades the face's nearest interior point: barycentrics clamped back into the triangle
    vec3 b = max(vBary, vec3(0.0));
    b /= max(b.x + b.y + b.z, 1e-6);
    vec3 vWorld = b.x * vW0 + b.y * vW1 + b.z * vW2;
    vec3 N = normalize(vNrm);
    vec3 c, L; float att, f2, ndlRaw, ndl;
    if (!CxPointLight(uBakeLight, uBakeColor, vWorld, N, c, L, att, f2, ndlRaw, ndl) || ndl <= 0.0) discard;
    // slot 0 of the one-cube scratch array: the same receiver the frame's cube slots go through
    float sh = CxLocalShadow(0, vWorld, N, ndlRaw, 0.0, 0.0);
    vec3 lit = CxPointDiffuse(c, att, ndlRaw, ndl) * mix(uLocalFloor, 1.0, sh);
    oA = vec4(lit, 0.0);
    oB = vec4(dot(lit, vec3(0.2126, 0.7152, 0.0722)) * L, 0.0);
}
