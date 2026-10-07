#ifndef CX_WX_SHAPE
#define CX_WX_SHAPE
// The shelter test's shared arithmetic: its constants, its world-space breakup and the final ramp. Both the
// live disk (cx_wx_edge) and the cached one (cx_wx_shel_c, run once per cover bake) end in CxWetOpenShape,
// so a fragment that reads the cache and one that runs the disk draw the same boundary.
/// Metres of surface the compare forgives outright, before the slope term. The cover view's depth range
/// is 3 x coverrange, so this is fractions of a depth unit -- it is written in metres because that is
/// what it means.
const float CX_WX_EDGE_BIAS_M = 0.12;
/// ...plus the cover view's own depth epsilon, matching the hard compare in cx_wx_lib.
const float CX_WX_EDGE_EPS = 0.0015;
/// Blobs per metre in the world-space breakup. A real eave's wet line is ragged at about the scale of
/// the tiles that shed onto it, not at the scale of the noise a shader usually reaches for.
const float CX_WX_EDGE_FREQ = 2.2;
/// Cells per metre the disk's rotation is hashed on. A rotation that is constant over the whole kernel
/// leaves the tap count's own plateaus in the ramp -- the nine visible bands under an awning that sent
/// the cover edge to a pre-filtered map in the first place. Hashing it per world CELL breaks them into a
/// dither that is fixed in the world, so it reads as grain on the ground and never crawls with the
/// camera; at roughly one cover texel a cell the cell itself is under the resolution being filtered.
const float CX_WX_EDGE_CELL = 24.0;
/// The golden angle: a Vogel disk stays evenly spread at any tap count, so dropping to four taps
/// thins the disk instead of clustering it.
const float CX_WX_EDGE_GOLD = 2.399963;

float CxWxEdgeHash(vec2 p) { return fract(sin(dot(p, vec2(21.98, 78.233))) * 43758.5453); }

float CxWxEdgeNoise(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(CxWxEdgeHash(i), CxWxEdgeHash(i + vec2(1.0, 0.0)), f.x),
               mix(CxWxEdgeHash(i + vec2(0.0, 1.0)), CxWxEdgeHash(i + vec2(1.0, 1.0)), f.x), f.y);
}

/// The breakup and the ramp over a disk's open fraction. The noise moves the THRESHOLD, so the ramp keeps
/// its width and only its line wanders -- and it is windowed by 4 open (1 - open), which is 1 across the
/// transition and exactly 0 at both ends, so open ground stays fully wet and deep shelter fully dry however
/// far the breakup is turned up. Splash-back, a gust and a gutter's drip make a real wet line ragged; a
/// straight one where a wall meets the floor is the one thing that reads as a shader.
float CxWetOpenShape(float open, vec2 worldXY, float breakup)
{
    float n = CxWxEdgeNoise(worldXY * CX_WX_EDGE_FREQ) - 0.5;
    return clamp(smoothstep(0.15, 0.85, open + n * breakup * 4.0 * open * (1.0 - open)), 0.0, 1.0);
}
#endif
