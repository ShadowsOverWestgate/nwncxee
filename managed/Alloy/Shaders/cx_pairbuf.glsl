#ifndef CX_PAIR_BUF
#define CX_PAIR_BUF
// CX-188 step 4: this frame's (static light, mover) PAIRS -- PairShadows writes the table, the debt dispatch
// walks it per tile, and CxLocalShadowQ reads one slot's run when an unbaked receiver needs the mover shadow
// its light's dyn page used to carry. Its own file so a stage can declare the table without pulling the whole
// light loop in, and so the header's bound is in one place.
// A slot's pairs are CONTIGUOUS and ordered by slot, which is what lets a reader walk one light's run by
// testing ix.z and what lets the debt pass fold two movers of one light into one min() instead of adding twice.
//   vp  = the pair view-projection (ShadowMath.PairViewProj), light -> the tile's clip
//   pr  = the AREA light's (pos, R), for the no-channel luminance estimate
//   col = its colour, and in w one tile texel in WORLD units per unit of distance from the light
//   sph = the mover sphere this tile frames (xyz centre, w radius, already HotPad-padded)
//   ix  = (atlas layer, one tile texel in uv, cube slot, bake-set index)
struct CxPair { mat4 vp; vec4 pr; vec4 col; vec4 sph; vec4 ix; };
layout(std430, binding = 28) readonly buffer CxPairBuf
{
    int uPairN;        // pairs the table holds this frame; 0 = nothing is paired and no reader goes further
    int uPairPad[3];   // std430 pads the struct array to 16 bytes anyway: named rather than implied
    CxPair uPair[];
};
#endif
