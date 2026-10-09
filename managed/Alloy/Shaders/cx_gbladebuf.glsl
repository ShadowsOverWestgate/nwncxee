// CX-24: the per-material table the grass scatter and the blade vertex stage share, written by
// GrassField.WriteMaterials -- CX_GRASS_MATV vec4s per grass material slot, in this order:
//   0 the 2x2 atlas cell probabilities (CAurGrassInfo +0x44)
//   1 (blade height, card draw slot or -1, records that slot may hold, BLADE cell bits)
//   2 material diffuse (CAurGrassInfo +0x28)   3 material ambient (+0x34)
//   4 + cell*3 + (0 root, 1 mid, 2 tip): the cell's colour ramp off the art, display space; the root's w is
//     the art's mean blade length over the card height
const int CX_GRASS_MATV = 16;   // GrassField.MatVec4
layout(std430, binding = 14) readonly buffer CxGrassMats { vec4 uGrassMat[]; };
vec4 CxGrassMat(uint mat, int k) { return uGrassMat[int(mat) * CX_GRASS_MATV + k]; }
