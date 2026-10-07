#ifndef CX_WX_RING
#define CX_WX_RING
// The rain ripple's lattice: one drop per world cell per period, the ring it spreads and the field over a point.
// Evaluated once per frame into the ripple tile (cx_wx_ripl_c), which the film and the water stage read;
// CX_RIPPLE_KEY lets that bake wrap the lattice so the tile repeats without a seam.
#ifndef CX_RIPPLE_KEY
#define CX_RIPPLE_KEY(c) (c)
#endif

float CxWeatherHash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

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
        vec2 key = CX_RIPPLE_KEY(cell) + salt;
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

#endif
