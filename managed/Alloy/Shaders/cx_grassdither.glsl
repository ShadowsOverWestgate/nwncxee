// The coverage decision both fragment stages share: the engine's own cutout threshold
// and the distance-fade dither, seeded per blade by vSeed.
// The engine's own cutout threshold (fAlphaDiscardValue under RenderInterface::Enable(1)).
const float CX_GRASS_CUT = 0.2;

// Interleaved gradient noise (Jimenez): a hash with no visible tiling and no texture fetch. The
// instance seed decorrelates neighbouring blades, so the dither reads as thinning grass rather than
// as a screen-space grid crawling over it. uFrame moves the pattern every frame under TAA (5.588238 px
// per frame, Jimenez's temporal step), so the accumulator averages a fading field into smooth coverage
// instead of freezing one pattern into it; 0 with TAA off, where a moving pattern would only shimmer.
uniform int uFrame;
float CxGrassDither()
{
    vec2 p = gl_FragCoord.xy + vec2(float(vSeed & 63u), float((vSeed >> 6) & 63u)) + 5.588238 * float(uFrame);
    return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715))));
}
