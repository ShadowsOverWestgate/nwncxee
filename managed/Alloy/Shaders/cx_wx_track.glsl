#ifndef CX_WX_TRACK
#define CX_WX_TRACK
// Footprints in the snow (Weather.Tracks): one world-tiling RGBA32F texture the CPU stamps a print into at each
// real foot contact. x = depth 0..1, y = when it was pressed (uWxRun.z's clock), zw = which repeat of the tile the
// print belongs to -- the texture repeats every CX_TRACK_PERIOD_M, so each texel names its own repeat and a print
// one period away never shows through here. Needs uWxRun (cx_wx_lib): z = the clock now, w = the refill rate
// per second, 0 = no prints.
layout(binding = 27) uniform sampler2D uWxTrack;
const int CX_TRACK_SIZE = 512;
const float CX_TRACK_PERIOD_M = 12.8;
/// Share of the cover a full-depth print packs away: the ground starts to show through a trampled path.
const float CX_WX_TRACK_BARE = 0.3;
/// Metres a full-depth print sinks, for the tilt of its walls.
const float CX_WX_TRACK_DENT = 0.04;
/// Share of its brightness a full-depth print's floor loses: packed snow is greyer than powder.
const float CX_WX_TRACK_DARK = 0.25;

float CxTrackTexel(ivec2 g)
{
    ivec2 tile = ivec2(floor(vec2(g) / float(CX_TRACK_SIZE)));
    vec4 t = texelFetch(uWxTrack, g - tile * CX_TRACK_SIZE, 0);
    if (t.z != float(tile.x) || t.w != float(tile.y)) return 0.0;
    return t.x * clamp(1.0 - (uWxRun.z - t.y) * uWxRun.w, 0.0, 1.0);
}

/// x = the print's depth at this point (0..1), yz = its gradient per metre, pointing into the print. Bilinear by
/// hand -- the tile tags must be compared per texel, never filtered -- and the gradient is the same four taps'.
vec3 CxTrack(vec2 p)
{
    if (uWxRun.w <= 0.0) return vec3(0.0);
    const float tpm = float(CX_TRACK_SIZE) / CX_TRACK_PERIOD_M;
    vec2 g = p * tpm - 0.5;
    ivec2 i = ivec2(floor(g));
    vec2 f = g - vec2(i);
    float a = CxTrackTexel(i), b = CxTrackTexel(i + ivec2(1, 0));
    float c = CxTrackTexel(i + ivec2(0, 1)), d = CxTrackTexel(i + ivec2(1, 1));
    return vec3(mix(mix(a, b, f.x), mix(c, d, f.x), f.y),
                mix(b - a, d - c, f.y) * tpm, mix(c - a, d - b, f.x) * tpm);
}
#endif
