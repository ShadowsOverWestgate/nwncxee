// The rain ripple, its own lib because the WATER stage takes it too (fed uSplash there, uCoverLayer.w on
// the lit stage); the hash rides with it.
//
// CX-102: the rings are WORLD-SPACE. A drop's ring is one physical size wherever the camera stands -- it
// spreads from nothing to about 8 cm over half a second and dies there -- so walking away from a puddle
// makes its rings smaller on screen, which is the whole point. What came before was a four-rung mip ladder
// that grew the ring with the pixel footprint to hold a constant size ON SCREEN: rings that never changed
// as you approached, which read as a bug because it was one. The footprint is still used, for exactly one
// thing: FADING a ring out once its crest is thinner than a pixel or two. It never sizes anything.

#include "cx_wx_ring.glsl"
uniform float uSplash;         // the water stage's rain impact rate (0 = none)
/// The ripple tile (Weather.Shelter): this frame's ring field over CX_RIPPLE_WRAP x CX_RIPPLE_WRAP cells, as
/// the surface tilt. One fetch where the two lattices cost eight cells of hashing per pixel.
layout(binding = 25) uniform sampler2D uWxRipple;
/// Cells across the tile: 4.8 m, wide enough that a puddle field does not read as a repeat.
const float CX_RIPPLE_WRAP = 24.0;

/// A pixel's footprint on the ground, in metres: the geometric mean of its two axes (sqrt of N.V -- V.z here, the
/// rings only ride level ground), so a grazing view stretches it by the axis it actually stretches.
float CxWeatherImpactPx(vec3 worldPos, vec3 V)
{
    return distance(worldPos, uEyePos) * uPixel.y / sqrt(max(V.z, 0.01));
}

/// How much of a ring a footprint of <c>px</c> metres still resolves. This is the ONLY thing the footprint
/// does: a crest that spans under about one and a half pixels fades out at its own world size rather than
/// being grown to stay visible.
float CxWeatherRippleFade(float px)
{
    return 1.0 - smoothstep(CX_RIPPLE_CREST_M * 0.75, CX_RIPPLE_CREST_M * 2.0, px);
}

/// The rings at <c>p</c>, world-sized at every distance: returns the surface tilt (the normal's own xy,
/// which is minus the height gradient) and in <c>kept</c> the share of a full field the footprint resolved.
/// Two half-cell-offset lattices: one alone reads as a grid.
vec2 CxWeatherRipple(vec2 p, float rate, float px, out float kept)
{
    kept = CxWeatherRippleFade(px);
    if (kept <= 0.0 || rate <= 0.0) return vec2(0.0);
    return textureLod(uWxRipple, p / (CX_RIPPLE_CELL_M * CX_RIPPLE_WRAP), 0.0).xy * kept;
}

/// The roughness the rings a pixel cannot resolve leave behind: their mean squared tilt, as microfacet
/// slope variance, added under the surface's own alpha.
float CxWeatherRippleRough(float alpha, float kept, float amp, float rate)
{
    return sqrt(alpha * alpha + (1.0 - kept * kept) * amp * amp * CX_RIPPLE_VAR * rate);
}
