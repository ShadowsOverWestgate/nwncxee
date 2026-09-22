// The vertex head: attributes, uniforms, outs, and the cluster tile hook the light libraries
// read (a vertex stage has no gl_FragCoord; the tile comes from this vertex's own NDC, as the particle
// rig does it).
#version 460 core
layout(location = 0) in vec3 aPos;      // blade-local: xy in [-0.5,0.5], z 0 (root) .. 1 (tip)
layout(location = 1) in vec2 aUV;       // 0..1 within one atlas cell
layout(location = 2) in vec4 iPos;      // xyz world root, w rotation about Z
layout(location = 3) in vec4 iParam;    // x scale (blade height), y atlas cell 0..3, z rank in [0,1), w bend
#include "AlloyRenderer.FeatDecl.glsl"
uniform mat4 uVP;
uniform vec3 uEyePos;
uniform vec2 uFade;                     // (full-density radius, cut-off radius) in world units
uniform vec4 uLod;                      // x full-density radius, y keep floor, z width exponent, w height exponent
uniform vec2 uSnow;                     // x = share of blades gone under snow (by rank), y = winter look
uniform float uWindGain;
uniform float uWindSpeed;      // uTimeOfDay -> sway phase; see GrassField._windSpeed
uniform sampler2D uPushTex;    // top-down displacement field, RG signed around 0.5
uniform float uPushGain;       // 0 = nothing displaces the grass
uniform vec3 uPushWindow;      // xy = the field's world origin, z = uv per world unit
uniform float uTuft;           // max blade copies across a widened far blade (1 = off)
uniform int uVLit;             // 1 = the light rig runs here, per vertex
uniform int uCast;             // 1 = a shadow view
uniform float uCastWidth;      // blade width multiplier in a shadow view: a 2-texel blade filters to nothing
uniform int uLit;              // 0 = no cluster build this pass
uniform float uShade;          // past this, the sun goes unshadowed (the per-vertex rig still runs)
uniform vec3 uAreaAmbient;
uniform vec3 uAreaDiffuse;
uniform vec3 uAreaDir;
out vec2 vCellUv;              // 0..1 within the blade's atlas cell
flat out vec2 vCellBase;       // the cell's atlas origin
flat out float vTuft;          // copies of the cell across this blade
out vec3 vLight;               // point light, per vertex, for the near blades
out vec3 vWorld;
out vec3 vNrm;
out float vDepth;
out float vFade;
out float vDist;
flat out uint vSeed;
out float vRoot;               // 0 at the root, 1 at the tip: the root-darkening ramp
out float vLookDown;           // 1 when the eye looks DOWN onto this root, 0 when level or below it
// #355: the depth prepass and the colour pass are two programs over this one text; equal depth is
// what lets the colour pass test LEQUAL with depth writes off.
invariant gl_Position;

// #355: the fraction of a patch's blades that draw at this distance -- GrassField.Keep, character
// for character, so the CPU's prefix count and this blade's own verdict agree.
float CxGrassKeep(float d)
{
    if (d <= uLod.x) return 1.0;
    float r = uLod.x / d;
    return max(uLod.y, r * r);
}

vec2 cxClusterXY;
#define CX_CLUSTER_XY cxClusterXY
