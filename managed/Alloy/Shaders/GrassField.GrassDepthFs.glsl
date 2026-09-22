#version 460 core
in vec3 vWorld;
in vec3 vNrm;
in float vDepth;
in float vFade;
in float vDist;
flat in uint vSeed;
#include "AlloyRenderer.FeatDecl.glsl"
#include "AlloyRenderer.KeyFogDecl.glsl"
uniform sampler2D uTex;
uniform int uMsaa;
uniform int uSoftEdge;                  // 1 = depth for the opaque core only; the fringe blends without depth
uniform int uCast;                      // 1 = a shadow view: no keyhole (its depth term is the camera's)
uniform float uEdge;
uniform vec4 uLod;                      // x = the full-density radius the hard far edge starts inside
out vec4 frag;
#include "AlloyRenderer.KeyholeFs.glsl"
#include "GrassField.GrassFetch.glsl"
#include "GrassField.GrassDither.glsl"
void main()
{
    vec4 texel = CxGrassTexel();
    float aw = max(fwidth(texel.a), 1e-5);
    // Only the DITHER path hardens the far edge: there a one-pixel ramp on a one-pixel blade reads as
    // floating pixels. Blended (softedge) or A2C, the ramp is the anti-aliasing -- a hard step on a
    // one-pixel blade is sub-pixel noise SMAA never sees an edge run in. Both stages carry this.
    float edge = (uMsaa != 0 || uSoftEdge != 0) ? uEdge : mix(uEdge, 0.01, smoothstep(0.5 * uLod.x, uLod.x, vDist));
    float cov = clamp((texel.a - CX_GRASS_CUT) / (aw * edge) + 0.5, 0.0, 1.0);
    float keep = (uMsaa != 0 || uSoftEdge != 0) ? vFade : cov * vFade;
    if (keep <= 0.0) discard;
    if (uSoftEdge != 0 && cov < 0.5) discard;
    if (keep < 1.0 && CxGrassDither() > keep) discard;
    if (uCast == 0 && CxKeyhole(1.0, vWorld, vDepth) < 0.0) discard;
    frag = vec4(0.0, 0.0, 0.0, uMsaa != 0 ? cov : 1.0);
}
