#version 460 core
#define CX_FS 1
// CX-24: the blade fragment head. Opaque, and no discard anywhere in the stage: early-Z does the work the
// card path's depth prepass does.
in vec3 vLight;
in vec3 vWorld;
in vec3 vNrm;
in float vDepth;
in float vDist;
in float vRoot;
in float vLookDown;
flat in float vFade;
flat in vec3 vColRoot;
flat in vec3 vColMid;
flat in vec3 vColTip;
flat in vec3 vMatDif;
flat in vec3 vMatAmb;
#include "cx_featdecl.glsl"
#include "cx_keyfogdecl.glsl"
uniform sampler2D uTex;                 // never sampled: named so a content file that reads it still compiles
uniform sampler2D uFlowTex;             // likewise (cx_grass_fhead.glsl, #347)
uniform float uShade;                   // past this, no cascade taps
uniform int uViz;                       // /alloy viz: 3 = the sun's visibility, as the lit stage's own 3
// CX-43: uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
uniform samplerCube uProbe;               // CX-51: the reflection probe, on the lit stage's unit
#include "cx_ssr.glsl"
uniform int uLit;
uniform int uVLit;
uniform float uRootAo;
uniform vec2 uSnow;
uniform float uGroundTint;
uniform vec4 uLook;                     // x backlit transmission, y specular sheen, z rain wetness, w its darkening
uniform sampler2D uGround;
uniform int uGroundOn;
out vec4 frag;
