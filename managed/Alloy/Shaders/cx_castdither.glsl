// ...and the cutout itself, at the engine's own fAlphaDiscardValue -- a per-BATCH
// uniform since #302, spelled uAlphaCut exactly as the colour path spells it, rather than a
// per-draw record field. Two thresholds exist and the batch key carries which.
// KNOWN COLLISION, named because it fails the same way this fix was reported and in the opposite
// direction: on env-mapped content the diffuse alpha carries TWO authorings. On foliage it is coverage
// (transparent between the leaves) and the discard is exactly right; on a polished floor or chrome trim
// it is REFLECTIVITY -- fEnvMapLevel = 1 - alpha, inc_standard.shd:248 -- so solid geometry
// authored as very shiny sits at alpha <= 0.2 by design, and only essentially-empty texels may punch
// through there.
// #363: after the cutout, the stochastic discard on the COLOUR pass's blend weight -- coverage
// (1 on env-alpha batches, where the diffuse alpha is reflectivity) times the record's rot.w --
// in uDither-texel blocks. That is what turns a translucent diffuse, or a half-alpha part, into a
// partial shadow of the same strength.
uniform int uDither;
uniform int uEnvAlpha;
// #363: ordered 4x4 Bayer threshold in (0, 1) -- alpha 1 never discards, and EVERY 4x4 block drops
// exactly the alpha fraction, which is what reads as a uniformly fainter shadow (white noise reads
// as blotches: a 4x4 patch of it lands anywhere between a quarter and three quarters)
float cxHash(uvec2 p)
{
    const float m[16] = float[16](0., 8., 2., 10., 12., 4., 14., 6., 3., 11., 1., 9., 15., 7., 13., 5.);
    return (m[(p.y & 3u) * 4u + (p.x & 3u)] + 0.5) / 16.0;
}
