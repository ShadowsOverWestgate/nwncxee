// The look: a hot gaussian core, a soft skirt, a faint six-point streak and a slight chromatic
// spread (red skirt widest), times a per-pixel occlusion against the scene depth copy (soft over one
// radius, so a pillar clips the halo where it covers it). Linear, additive, no encode (#132).
// /effects haloviz 1 paints that visibility alone.
#version 460 core
in vec2 vUV;
in vec3 vColor;
in float vViewZ;
in float vRadius;
#include "AlloyRenderer.SceneDepthLib.glsl"
void main()
{
    float r = length(vUV);
    if (r >= 1.0) discard;
    // Per-pixel occlusion: a surface nearer than the light hides this pixel, softly over one radius.
    float sceneDepth = CxSceneViewZ(gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0)));   // positive distance
    float lightDepth = -vViewZ;
    float vis = clamp((sceneDepth - (lightDepth - vRadius)) / max(vRadius, 1e-3), 0.0, 1.0);
    if (uViz == 1) { frag = vec4(vec3(vis), 1.0); return; }
    vec3 rr = vec3(r * 0.92, r, r * 1.08);            // chromatic spread: red reaches furthest
    vec3 core = exp(-rr * rr * 22.0);
    vec3 skirt = pow(max(1.0 - rr, 0.0), vec3(3.0)) * 0.35;
    float ang = atan(vUV.y, vUV.x);
    float streak = pow(max(abs(cos(ang * 3.0)), 0.0), 40.0) * pow(1.0 - r, 2.0) * 0.25;
    vec3 profile = core + skirt + streak;
    frag = vec4(vColor * profile * vis, 1.0);
}
