// CX-188 step 3 (ADR 0029): the per-pixel SHADOW VISIBILITY RECORD's format, in one place for the pass that
// writes it (cx_defshadow_cs) and the deferred lighting pass that reads it back (cx_deferred_cs). This file IS
// that format: change a lane here or nowhere.
// 12 bytes a record texel -- one RGBA8 and one RG32UI. The record holds, per pixel, the shadow.perpixel
// shadow-casting lights CONTRIBUTING most to it and each one's visibility evaluated ONCE -- faded toward 1 by
// CxShFade where the lane sits near the budget's own edge -- plus the depth and normal of the
// surface the record was taken on, which is what lets a half-resolution record be upsampled without bleeding
// one surface's shadow onto another. A texel whose depth reads back 0 holds no record: the pixel it stands for
// was not a deferred one, or owed no cube read at all.
#ifndef CX_SHREC
#define CX_SHREC
#include "cx_ssrrec.glsl"

/// Shadow-casting lights one pixel's record CAN hold -- the format's ceiling, and the ceiling shadow.perpixel
/// is declared against; four slot bytes in one word and four visibilities in one RGBA8, so widening it is a
/// format change. A light past the budget the record was filled to lights the pixel UNSHADOWED (CX-188 budget);
/// only a pixel with no record at all falls back to the walk's single hardware tap per light.
const int CX_SH_MAX = 4;
/// CX-188 budget: how far above the best DROPPED light's score a lane must sit to keep its shadow in full.
/// Membership is a STEP -- the Kth strongest light at a pixel has a shadow, the (K+1)th does not -- and the
/// pre-budget path hid that step behind a hardware tap for the (K+1)th. Nothing hides it now, so a full shadow
/// would end at a hard contour that crawls as the camera moves, and no temporal trick is allowed to fix it.
/// Instead the record FADES a lane's visibility toward 1 as that lane's score falls toward the best score that
/// did not fit: at the crossover the two lights' scores are equal, the lane's weight is exactly 0, and the
/// light leaving the record had already dissolved. An octave, the same band light.fullmodel fades over; lanes
/// well clear of the edge -- the hero lights -- are untouched.
const float CX_SH_FADE = 2.0;

/// Per-lane budget weight: 1 where a lane's score is <c>CX_SH_FADE</c> times the best dropped light's, 0 where
/// it only equals it. <paramref name="drop"/> 0 means nothing was dropped and there is no edge to dissolve, so
/// every lane keeps its shadow whole -- which is also what the budget's own off position reads as.
vec4 CxShFade(vec4 rank, float drop)
{
    return drop > 0.0 ? smoothstep(vec4(drop), vec4(drop * CX_SH_FADE), rank) : vec4(1.0);
}

/// How far (a share of view depth) a record texel's own depth may sit off the reader's before that texel is
/// judged to be another surface's and left out of the upsample. Looser than cx_gbuf's CX_GB_RECORD because a
/// half-resolution texel legitimately stands a pixel away from its reader.
const float CX_SH_DEPTH = 0.02;
/// The cosine band a record texel's normal is weighted over: fully in at y, out at x. A crease is where an
/// upsample without this term smears one face's shadow round the corner onto the next.
const vec2 CX_SH_NORMAL = vec2(0.86, 0.985);

/// The four cube slots as one word, each a byte holding the slot PLUS ONE so that 0 is "no light here".
/// CasterPass.MaxCubes is 64, so a slot always fits a byte.
uint CxShPackSlots(ivec4 s)
{
    uvec4 b = uvec4(clamp(s + 1, ivec4(0), ivec4(255)));
    return b.x | (b.y << 8u) | (b.z << 16u) | (b.w << 24u);
}
ivec4 CxShUnpackSlots(uint w)
{
    return ivec4(uvec4(w & 255u, (w >> 8u) & 255u, (w >> 16u) & 255u, w >> 24u)) - 1;
}

/// The surface key: linear view depth as a half float in the low 16 bits, the shading normal as a unorm8
/// octahedral pair in the high 16. A key of 0 is therefore depth 0 -- "no record" -- which is what a cleared
/// or never-written texel reads back as.
uint CxShPackKey(float viewDepth, vec3 N)
{
    uvec2 o = uvec2(round(clamp(CxOctEncode(N) * 0.5 + 0.5, 0.0, 1.0) * 255.0));
    return (packHalf2x16(vec2(max(viewDepth, 0.0), 0.0)) & 0xffffu) | (o.x << 16u) | (o.y << 24u);
}
void CxShUnpackKey(uint k, out float viewDepth, out vec3 N)
{
    viewDepth = unpackHalf2x16(k & 0xffffu).x;
    N = CxOctDecode(vec2(uvec2((k >> 16u) & 255u, k >> 24u)) / 255.0 * 2.0 - 1.0);
}
#endif
