// The rain ripple, its own lib because the WATER stage takes it too (fed uSplash there, uCoverLayer.w on
// the lit stage); the hash rides with it.
//
// CX-102: the rings are WORLD-SPACE. A drop's ring is one physical size wherever the camera stands -- it
// spreads from nothing to about 8 cm over half a second and dies there -- so walking away from a puddle
// makes its rings smaller on screen, which is the whole point. What came before was a four-rung mip ladder
// that grew the ring with the pixel footprint to hold a constant size ON SCREEN: rings that never changed
// as you approached, which read as a bug because it was one. The footprint is still used, for exactly one
// thing: FADING a ring out once its crest is thinner than a pixel or two. It never sizes anything.

float CxWeatherHash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
uniform float uSplash;         // the water stage's rain impact rate (0 = none)

/// The world cell one drop lands in per period. The 2x2 neighbourhood below is EXACT only while the crest
/// reach stays under half a cell, which pins this to CX_RIPPLE_REACH_M (Weather.SelfCheck asserts the pair).
const float CX_RIPPLE_CELL_M = 0.20;
/// Seconds from the strike to the ring's death.
const float CX_RIPPLE_LIFE = 0.5;
/// How far the crest gets in that time. A rain drop on standing water rings out about this far and stops.
const float CX_RIPPLE_REACH_M = 0.08;
/// Half-width of the crest itself: the ring is this thin whatever radius it has reached.
const float CX_RIPPLE_CREST_M = 0.012;
/// Peak surface slope at the crest of a full-strength ring. A dent well under a millimetre deep on water
/// this calm: enough to bend the highlight, not enough to read as a dimple in the stone.
const float CX_RIPPLE_TILT = 0.30;
/// Mean squared tilt of the whole field per unit rate. Estimated from the crest's own coverage: a live
/// crest is a ~2.4 cm annulus at a mean 5 cm radius inside a 4 cm^2 cell, alive about three quarters of
/// each period, and its RMS slope is about half the peak -- two layers of that come to ~0.007.
/// What the footprint fade takes out of the normal goes into the lobe as microfacet variance instead, so
/// distant rain hazes the sheen out to any range rather than ending at a line.
/// ponytail: a number off the geometry, not a measurement. Re-derive it if the crest profile changes.
const float CX_RIPPLE_VAR = 0.006;

/// One lattice's rings over <c>p</c> (metres): the 2x2 cells whose drops can reach it, each carrying at
/// most one live ring. <c>salt</c> decorrelates a layer's hashes from its neighbours'. Returns the height
/// gradient, dimensionless (metres of rise per metre).
vec2 CxRippleLayer(vec2 p, float rate, float salt)
{
    float t = CxClock() * 0.001;
    vec2 q = p / CX_RIPPLE_CELL_M;
    vec2 base = floor(q - 0.5);
    vec2 g = vec2(0.0);
    for (int i = 0; i < 4; i++)
    {
        vec2 cell = base + vec2(float(i & 1), float(i >> 1));
        vec2 key = cell + salt;
        float period = CX_RIPPLE_LIFE * (0.9 + 0.8 * CxWeatherHash(key));
        float ph = t / period + CxWeatherHash(key + 11.0);
        float idx = floor(ph);
        float age = fract(ph) * period;
        if (age > CX_RIPPLE_LIFE) continue;
        if (CxWeatherHash(key + idx * 7.0 + 3.0) > rate) continue;
        vec2 c = cell + vec2(CxWeatherHash(key + idx + 5.0), CxWeatherHash(key + idx + 9.0));
        vec2 dv = (q - c) * CX_RIPPLE_CELL_M;
        float d = length(dv);
        // sqrt in time: the crest leaves fast and slows as it spreads, the way a capillary ring does
        float r = CX_RIPPLE_REACH_M * sqrt(age / CX_RIPPLE_LIFE);
        float u = (d - r) / CX_RIPPLE_CREST_M;
        if (abs(u) > 3.0) continue;
        float damp = 1.0 - age / CX_RIPPLE_LIFE;
        float amp = CX_RIPPLE_TILT * damp * damp * (0.6 + 0.4 * CxWeatherHash(key + idx + 13.0));
        // h(u) = amp * CREST * u * exp(-u^2/2): water piled just outside the crest and hollowed just
        // inside it, one ring wide. This is dh/dd, which the CREST in the amplitude cancels out of.
        g += dv / max(d, 1e-4) * (amp * (1.0 - u * u) * exp(-0.5 * u * u));
    }
    return g;
}

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
    vec2 g = CxRippleLayer(p, rate, 0.0)
           + CxRippleLayer(p + CX_RIPPLE_CELL_M * 0.5, rate, 37.0);
    return -g * kept;
}

/// The roughness the rings a pixel cannot resolve leave behind: their mean squared tilt, as microfacet
/// slope variance, added under the surface's own alpha.
float CxWeatherRippleRough(float alpha, float kept, float amp, float rate)
{
    return sqrt(alpha * alpha + (1.0 - kept * kept) * amp * amp * CX_RIPPLE_VAR * rate);
}
