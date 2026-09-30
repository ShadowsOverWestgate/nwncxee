// The lit stage's weather reads, composed after AlloyLights.SunShadowLib (they sample
// the sun array's cover layer). Exposure is a four-tap PCF like a cascade's; the hollow test is eight
// compares against this point's own depth minus a step -- a neighbour standing higher fails it.
#include "cx_wx_impact.glsl"
#include "cx_wx_map.glsl"
#include "cx_wx_edge.glsl"
// ---- #617/#618/#619: weather on surfaces --------------------------------------------------------
uniform vec4 uWeather;         // x wetness, y snow, z puddle fill (all 0..1), w = /weather viz
uniform vec4 uWeatherLook;     // x wet darkening, y snow albedo, z puddle edge, w puddle coverage
// uCoverVp / uCoverLayer are declared by cx_wx_edge above: the shelter test owns them now, and the reads
// below share them.
uniform vec4 uSnowLook;        // x sparkle, y mound relief, z drift against walls, w = cos(steepest slope snow holds to)
uniform vec4 uSnowLight;       // x scatter wrap, y sky in the shade, z drift width in cover uv, w ground variation
uniform vec4 uSnowPom;         // x relief height in metres (0 = off), y march steps, z = 1 when tileset parts take no cover

uniform sampler2D uCoverEsm;   // the filtered cover map (Weather.Esm), on the snapshot-depth unit
uniform vec4 uCoverEsmP;       // x = the variance floor in depth units squared, y = height bias in depth units, z = 1 when the map is bound
// R5, the realism terms the splice reads: x = run-off down walls and the band at their foot,
// y = the puddle shoreline's rim, zw spare.
uniform vec4 uWxRun;

/// Up-facing enough to be ground rather than a wall: past this a face takes the even wetting, under it
/// the run-off streaks.
const float CX_WX_WALL_UP = 0.85;
/// Metres of fall the streak noise spans down a wall -- a run-off streak is long and thin, so the noise
/// is slack along z and tight across the face.
const float CX_WX_STREAK_Z = 0.5;
/// The driest a streak leaves a wall, as a share of the even wetting. The bright side goes as far the
/// other way, so the streaks REDISTRIBUTE the wetness rather than removing it.
const float CX_WX_STREAK_MIN = 0.35;
/// Metres up the wall the run-off band at its foot reaches, and metres out along the face the ground's
/// own height is read from (the map texel under the wall holds the wall's top, not the floor).
const float CX_WX_FOOT_M = 0.35;
/// The shoreline rim's width, as a share of the puddle-depth knob: ground this far under the water's own
/// level, in depth terms, is soaked through without being covered.
const float CX_WX_SHORE = 0.6;

float CxWeatherExposure(vec3 worldPos, vec3 N)
{
    if (uCoverVp[3][3] == 0.0) return 1.0;
    vec4 clip = uCoverVp * vec4(worldPos + N * 0.15, 1.0);
    vec3 ndc = clip.xyz / clip.w;
    if (any(greaterThan(abs(ndc.xy), vec2(1.0))) || abs(ndc.z) > 1.0) return 1.0;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float ref = ndc.z * 0.5 + 0.5;
    // the filtered map: one fetch of the two moments, Chebyshev's bound on how much of the kernel
    // stands above this point -- a ramp as wide as the feather whatever the roof's height. Under the
    // OIT accumulate the unit holds the opaque depth instead, and a blended part takes the hard compare.
    if (uCoverEsmP.z > 0.5 && uOit == 0)
    {
        vec2 m = texture(uCoverEsm, uv).rg;
        float dd = ref - uCoverEsmP.y - m.x;
        if (dd <= 0.0) return 1.0;
        float var = max(m.y - m.x * m.x, uCoverEsmP.x);
        // the bleed cut: a roof's own texels, all at one depth, must read 0 under it and not the
        // floor's share; the ramp keeps its width
        return smoothstep(0.2, 1.0, var / (var + dd * dd));
    }
    return texture(uSunShadow, vec4(uv, uCoverLayer.x, ref - 0.0015));
}

float CxWeatherHollow(vec3 worldPos)
{
    if (uCoverVp[3][3] == 0.0) return 0.0;
    vec4 clip = uCoverVp * vec4(worldPos, 1.0);
    vec3 ndc = clip.xyz / clip.w;
    if (any(greaterThan(abs(ndc.xy), vec2(1.0))) || abs(ndc.z) > 1.0) return 0.0;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float ref = ndc.z * 0.5 + 0.5 - uCoverLayer.y;
    vec2 o = 4.0 / vec2(textureSize(uSunShadow, 0).xy);
    float L = uCoverLayer.x, s = 0.0;
    for (int i = 0; i < 8; i++)
    {
        float a = float(i) * 0.7853982;
        s += texture(uSunShadow, vec4(uv + vec2(cos(a), sin(a)) * o, L, ref));
    }
    return 1.0 - s * 0.125;
}

// Snow drift against walls: three rings of eight compares out to the drift width, each tap asking whether
// the ground there stands a step higher. Returns x = drift height (1 at the wall, falling off with the
// ring radius), yz = the uv direction toward the higher ground -- the slope the mound rises along.
vec3 CxSnowDrift(vec3 worldPos, float width)
{
    if (uCoverVp[3][3] == 0.0 || width <= 0.0) return vec3(0.0);
    vec4 clip = uCoverVp * vec4(worldPos, 1.0);
    vec3 ndc = clip.xyz / clip.w;
    if (any(greaterThan(abs(ndc.xy), vec2(1.0))) || abs(ndc.z) > 1.0) return vec3(0.0);
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float ref = ndc.z * 0.5 + 0.5 - uCoverLayer.y;
    float L = uCoverLayer.x;
    vec3 r = vec3(0.0);
    for (int k = 0; k < 3; k++)
    {
        float rad = width * (0.25 + 0.375 * float(k));
        float w = 1.0 - 0.3 * float(k);
        for (int i = 0; i < 8; i++)
        {
            float a = float(i) * 0.7853982 + float(k) * 0.3;
            vec2 o = vec2(cos(a), sin(a));
            float higher = 1.0 - texture(uSunShadow, vec4(uv + o * rad, L, ref));
            r.x += higher * w;
            r.yz += o * higher * w;
        }
    }
    r.x /= 8.0 * 2.1;
    return r;
}

float CxWeatherNoise(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(CxWeatherHash(i), CxWeatherHash(i + vec2(1.0, 0.0)), f.x),
               mix(CxWeatherHash(i + vec2(0.0, 1.0)), CxWeatherHash(i + vec2(1.0, 1.0)), f.x), f.y);
}

// The snow relief, 0..1 of the depth knob: mounds from two noise octaves under the ground-variation
// patches, plus the drift banked against the wall. Cheap enough to march: the drift (the expensive
// part) comes in as a value, interpolated by the caller along the ray.
float CxSnowHeight(vec2 p, float vary, float pile)
{
    float m = 0.3 + 0.45 * CxWeatherNoise(p * 1.7) + 0.25 * CxWeatherNoise(p * 6.0);
    m *= 1.0 - vary * smoothstep(0.35, 0.7, CxWeatherNoise(p * 0.23)) * 0.6;
    return clamp(m * 0.7 + pile * 0.6, 0.0, 1.0);
}
