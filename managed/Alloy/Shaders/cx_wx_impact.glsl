// The rain-impact ripple, its own lib because the WATER stage takes it too (fed uSplash
// there, uCoverLayer.w on the lit stage); the hash rides with it.

float CxWeatherHash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
uniform float uSplash;         // the water stage's rain impact rate (0 = none)

// One ring of the train below is about 6 cm across, so a pixel that spans a whole one samples the
// wave train at random phase and the rings read as chunky sparkle instead of rings. Each size fades
// between a pixel spanning a third of a ring and three quarters of one, and the next size up takes over
// (the ladder at the end).
const float CX_IMPACT_RING_M = 0.06;
// How far the wave tilts the surface at the crest. A drop's ring is a dent a millimetre deep on
// water this calm: enough to bend the highlight, not enough to read as a dimple in the stone.
const float CX_IMPACT_TILT = 0.2;
// The train's mean squared tilt per unit rate, measured over space and time (linear in the rate): the
// share a pixel too wide for the rings still sees, as microfacet slope variance -- roughness. What the
// fade takes out of the normal goes into the lobe instead, so distant rain hazes the sheen out to any
// range rather than ending at a line.
const float CX_IMPACT_VAR = 0.002;
float CxWeatherImpactRough(float alpha, float fade, float amp, float rate)
{
    return sqrt(alpha * alpha + (1.0 - fade * fade) * amp * amp * CX_IMPACT_VAR * rate);
}

// Rain impacts: two offset 25 cm lattices, one drop per cell per period (0.4-1 s, hashed per cell), present
// with probability = the rate, each its own size. Not a circle: a damped wave TRAIN -- three rings
// behind a front that runs out and spreads, the whole train dying as exp(-3 age) the way a real ripple
// does -- and the normal tilts along its radial slope. No albedo mark: a drop leaves nothing on the
// water but the wave, and what the eye sees is the highlight rippling. Returns the tilt.
vec2 CxWeatherImpactAt(vec2 p, float rate, float scale)
{
    float t = CxClock() * 0.001;
    vec2 r = vec2(0.0);
    for (int i = 0; i < 2; i++)
    {
        vec2 q = p * (4.0 / scale) + float(i) * 0.5;
        vec2 cell = floor(q) + float(i) * 37.0 + scale * 101.0;
        float period = 0.4 + 0.6 * CxWeatherHash(cell);
        float ph = t / period + CxWeatherHash(cell + 11.0);
        float idx = floor(ph), age = fract(ph);
        if (CxWeatherHash(cell + idx) > rate) continue;
        vec2 c = vec2(CxWeatherHash(cell + idx + 3.0), CxWeatherHash(cell + idx + 5.0)) * 0.5 + 0.25;
        float size = 0.4 + 0.6 * CxWeatherHash(cell + idx + 7.0);   // reach and height of the drop's wave
        vec2 dv = fract(q) - c;
        float d = length(dv);
        // x = cell units from the front, in wavelengths; the packet sin(2x) e^(-0.12 x^2) is three
        // rings wide, and the slope below is its derivative in x. The 14 sets the ring at ~6 cm:
        // fine enough to read as a drop, coarse enough to survive a pixel out at chase distance.
        float x = (d - age * 0.45 * size) * (14.0 - 5.0 * age);
        float env = exp(-x * x * 0.12);
        float slope = (2.0 * cos(2.0 * x) - 0.24 * x * sin(2.0 * x)) * env;
        r += dv / max(d, 1e-4) * slope * exp(-3.0 * age) * size;
    }
    return r * CX_IMPACT_TILT;
}

/// A pixel's footprint on the ground, in metres: the geometric mean of its two axes (sqrt of N.V -- V.z here, the
/// train only rides level ground), so a grazing view stretches it by the axis it actually stretches.
float CxWeatherImpactPx(vec3 worldPos, vec3 V)
{
    return distance(worldPos, uEyePos) * uPixel.y / sqrt(max(V.z, 0.01));
}

/// How much of one ring size's train a footprint of px metres still resolves.
float CxWeatherImpactFade(float px, float ring)
{
    return 1.0 - smoothstep(ring * 0.333, ring * 0.75, px);
}

// The ladder: each rung is the train at CX_IMPACT_STEP times the last one's size, and takes over exactly as the
// rung below stops resolving, so the rings keep a near-constant size ON SCREEN out to the last rung's reach.
// ponytail: far rungs are bigger rings than rain really makes; they read as rain at range, not as drops.
const int CX_IMPACT_RUNGS = 4;
const float CX_IMPACT_STEP = 2.5;

/// The rings at every distance: returns the tilt, and in `kept` how much of a full train it carries (the rest is
/// CxWeatherImpactRough's to add as roughness).
vec2 CxWeatherImpact(vec2 p, float rate, float px, out float kept)
{
    vec2 r = vec2(0.0);
    float below = 1.0, scale = 1.0;
    kept = 0.0;
    for (int k = 0; k < CX_IMPACT_RUNGS; k++, scale *= CX_IMPACT_STEP)
    {
        float f = CxWeatherImpactFade(px, CX_IMPACT_RING_M * scale);
        float w = f * (k == 0 ? 1.0 : 1.0 - below);
        below = f;
        if (w <= 0.0) continue;
        r += CxWeatherImpactAt(p, rate, scale) * w;
        kept += w;
    }
    return r;
}

