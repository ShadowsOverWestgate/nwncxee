// The rain-impact ripple, its own lib because the WATER stage takes it too (fed uSplash
// there, uCoverLayer.w on the lit stage); the hash rides with it.

float CxWeatherHash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
uniform float uSplash;         // the water stage's rain impact rate (0 = none)

// One ring of the train below is about 6 cm across, so a pixel that spans a whole one samples the
// wave train at random phase and the rings read as chunky sparkle instead of rings. Fade the tilt
// out between a pixel spanning a third of a ring and three quarters of one -- past the textbook
// Nyquist cut, which measured far more conservative than the frame looks: rings hold clean to the
// end of the visible ground. uPixel.y carries the target's height, so a taller target (or a higher
// render scale) keeps them further out still.
const float CX_IMPACT_RING_M = 0.06;
// How far the wave tilts the surface at the crest. A drop's ring is a dent a millimetre deep on
// water this calm: enough to bend the highlight, not enough to read as a dimple in the stone.
const float CX_IMPACT_TILT = 0.2;
float CxWeatherImpactFade(vec3 worldPos, vec3 V)
{
    // the footprint stretches as the surface turns away: V.z IS N.V here, the train only rides level ground
    float px = distance(worldPos, uEyePos) * uPixel.y / max(V.z, 0.15);
    return 1.0 - smoothstep(CX_IMPACT_RING_M * 0.333, CX_IMPACT_RING_M * 0.75, px);
}

// Rain impacts: two offset 25 cm lattices, one drop per cell per period (0.4-1 s, hashed per cell), present
// with probability = the rate, each its own size. Not a circle: a damped wave TRAIN -- three rings
// behind a front that runs out and spreads, the whole train dying as exp(-3 age) the way a real ripple
// does -- and the normal tilts along its radial slope. No albedo mark: a drop leaves nothing on the
// water but the wave, and what the eye sees is the highlight rippling. Returns the tilt.
vec2 CxWeatherImpact(vec2 p, float rate)
{
    float t = mod(float(uTimeOfDay), 3600000.0) * 0.001;   // hour wrap keeps fract() fine-grained
    vec2 r = vec2(0.0);
    for (int i = 0; i < 2; i++)
    {
        vec2 q = p * 4.0 + float(i) * 0.5;
        vec2 cell = floor(q) + float(i) * 37.0;
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
