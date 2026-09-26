// CX-43: the pass's shared scene terms -- one std140 block every Alloy lighting stage reads, filled once
// per pass by PassBlock and bound once, so a program switch uploads none of it. Every member used to be a
// loose uniform in its own library; the block's layout is a contract with PassBlock's offsets and
// ShaderContract.PassSafe refuses an override that declares anything else.
// A stage includes this from every library that reads a member, so the guard keeps it to one declaration.
#ifndef CX_PASS_BLOCK
#define CX_PASS_BLOCK
layout(std140, binding = 0) uniform CxPass
{
    vec4 uLocalLight[64];     // xyz = the position the cube was baked from, w = its far plane
    float uLocalDynPage[64];  // #305: the dynamic pool PAGE PLUS ONE per slot (0 = no movers near that light)
    float uLocalFade[64];     // per-slot shadow strength: a slot fading in after its bake or out after its light left the budget
    mat4 uSunVp[4];
    vec4 uSunTexel;           // world units per shadow texel, per cascade
    vec4 uSunBias;            // depth bias in DEVICE units, per cascade (world bias / that fit's range)
    vec4 uSunDyn;             // #306: 1 where cascade i holds a fresh dynamic bake this frame
    vec4 uSunRadius;          // per-cascade fit radius
    vec4 uLocalSoft;          // x = disc texels, y = near skip, z = disc stand-down: world radius per unit view depth (0 = none), w = cube taps per fragment (0 = every light)
    vec4 uLocalPcss;          // #642: x = source radius (0 = no blocker search), y = temporal phase, z = disc taps, w = the view depth past which no cube is read
    vec3 uSunFocus;           // the LIVE fit focus this frame
    int uSunCascades;         // 0 = no cascades to sample this frame (interior, vanilla, not yet drawn)
    vec3 uAreaAmbient;
    float uLocalFloor;        // #641: the shadow floor, pre-scaled by the area ambient
    vec3 uAreaDiffuse;
    float uProbeMips;         // #121: 0 = no probe captured yet (or this draw is the capture)
    vec3 uAreaDir;            // world-space surface->light for the area directional (sun/moon)
    float uProbeGain;         // /light probegain, the ambient-specular dial
    vec2 uLocalPlane;         // x = the near plane every cube shares, y = 1 / face size in texels
    int uSceneEnv;            // does the AREA declare an env map (scene+0x1e8)?
    vec4 uTile;               // xy = viewport origin in window pixels, zw = pixels per tile
    ivec3 uGrid;
    float uReach;             // the cluster bins a light out to uReach * R (/light binreach); 0 = no window
    vec3 uFalloff;            // x = master gain (1 = the engine's own peak), y = sphere-light wrap radius (#139), z = exponent on the rim fade (1 = vanilla)
    float uWrap;              // #139: sphere-light wrap, as a multiple of uFalloff.y; 0 = the punctual N.L
    vec2 uZ;
    vec2 uAtten;              // #140: (lightMaxIntensityInv, lightFalloffFactor); zero = the shipped pair
    vec2 uHot;                // #142: (near-field weight, core size as a fraction of the range)
    vec2 uSss;                // #375: the stock subsurface term as (wrap, energy)
    float uWrapN;             // #143: the distance-INDEPENDENT wrap floor; 0 = off
    float uDayDim;            // 1 / the daylight ratio: every lamp term's scale, 1 at night and indoors
};
#endif
