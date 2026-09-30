#version 460 core
// CX-102: the splash stage's own interface. It shares the precipitation pass's blend, depth state and light
// rig but not its placement: a drop falls through a fall-aligned box, a splash sits on the surface the rain
// landed on, which only the cover map knows -- and it needs that map as PLAIN depth, not as the compare
// sampler the drop stage tests its instances against.
uniform mat4 uView;
uniform mat4 uProj;
uniform vec3 uEye;
uniform int uLit;              // 1 = take the light rig; 0 = flat area terms
// x = the splash clock in seconds (wrapped at a whole number of lifetimes), y = one crown's lifetime,
// z = the disc radius in metres, w = pixels per metre at unit view depth (the distance fade's scale)
uniform vec4 uSplashP;
uniform vec3 uSplashC;         // xy = the disc centre (the camera's follow target), z = the height the cover ray starts from
uniform sampler2D uCoverDepth; // the cover layer as plain depth (Weather.Esm's texture view)
uniform mat4 uCoverVp;         // world -> the cover view's clip space; zero matrix = no map, and nothing draws
uniform mat4 uCoverVpInv;      // ...and back, for the landing point
// CX-43: uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
out vec2 vUV;
out float vSeed;               // per-crown brightness, times the distance fade
out float vAge;                // 0..1 through the crown's life
out float vPart;               // 0 = the ring on the ground, >0 = a droplet streak
out vec3 vLight;               // linear light at this splash

vec2 cxClusterXY;
#define CX_CLUSTER_XY cxClusterXY
