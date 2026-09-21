using System;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>#618/#619: the world's response -- wet, snowed and puddled surfaces in the lit stage. Exposure is
/// the rain occlusion map (#617) sampled per fragment; HOW MUCH is three CPU integrators, uniforms the lit
/// program reads: wetness and snow accumulate density x time while the weather runs and decay after the stop
/// edge, puddles fill once wetness passes a threshold and drain slower than the sheen dries. No per-texel
/// storage anywhere. Puddle placement is the AO the lit stage already receives (ADR 0021) gated on a flat
/// up-facing geometric normal and broken by world-space noise, plus a ring test on the rain view's depth
/// where geometry really is lower (<c>CxWeatherHollow</c>).</summary>
internal static unsafe partial class Weather
{
    /// <summary>The cover view's depth range is <c>2 x radius + margin</c> with margin = radius (CasterPass.Cover).</summary>
    private const float CoverDepthRadii = 3f;
    /// <summary>World height step a neighbour must stand above a point for the ring test to call it a hollow.</summary>
    private const float HollowStepM = 0.12f;
    /// <summary>Weather that starts this soon after an area edge was already running there: arrive soaked.</summary>
    private const float ArriveWindowSec = 10f;

    internal static Knob<bool> Arrive = null!;
    internal static Knob<float> WetStrength = null!, SnowStrength = null!, AccumSec = null!, DrySec = null!, MeltSec = null!,
                                PuddleThreshold = null!, PuddleCoverage = null!, PuddleEdge = null!, Splash = null!,
                                SnowAlbedo = null!, SnowSlope = null!, SnowPile = null!, SnowBump = null!, SnowSparkle = null!, SnowWrap = null!, SnowSky = null!, SnowPileWidth = null!, SnowVary = null!, SnowDepth = null!, SnowSteps = null!;
    private static float _wet, _snow, _puddle;
    private static long _surfTicks, _enterTicks;

    internal static float Wet => _wet;
    internal static float Snow => _snow;
    internal static float Puddle => _puddle;

    private static void DeclareSurfaceKnobs()
    {
        G.Section("Surfaces");
        Arrive = G.Toggle("arrive", "Arrive into settled weather", true,
                          help: "Weather already running when you enter an area is fully soaked or snowed on arrival, instead of accumulating in front of you.");
        WetStrength = G.Slider("wet", "Wet surfaces", 0f, 1f, 0.05f, 0.1f,
                               help: "Exposed ground and roofs darken and turn glossy in rain, reflecting the sky. 0 = off.");
        SnowStrength = G.Slider("snow", "Snow cover", 0f, 1f, 0.05f, 1f,
                                help: "Exposed up-facing surfaces whiten in snow. 0 = off.");
        AccumSec = G.Slider("accum", "Soak / settle time", 5f, 600f, 5f, 60f, "s",
                            help: "Seconds of full-density weather to fully wet or snow the ground.");
        DrySec = G.Slider("dry", "Dry-out time", 10f, 1800f, 10f, 180f, "s", help: "Seconds for wet ground to dry after the rain stops.");
        MeltSec = G.Slider("melt", "Melt time", 30f, 7200f, 30f, 900f, "s", help: "Seconds for snow cover to melt after the snowfall ends.");
        Splash = G.Slider("splash", "Rain impacts", 0f, 1f, 0.05f, 1f,
                          help: "Drops ring the puddles they hit. 0 = off.");
        G.Section("Snow look");
        SnowAlbedo = G.Slider("snowalbedo", "Snow brightness", 0.3f, 1f, 0.05f, 0.9f, help: "Albedo of settled snow. Fresh snow is near white.");
        SnowSlope = G.Slider("snowslope", "Snow holds to", 10f, 85f, 5f, 55f, "°",
                             help: "Steepest slope snow still lies on. Higher = snow on steeper roofs and banks.");
        SnowPile = G.Slider("snowpile", "Drift against walls", 0f, 2f, 0.1f, 1f,
                            help: "Mounds banked against walls and ledges, rising toward whatever stands higher. 0 = even cover.");
        SnowPileWidth = G.Slider("snowpilewidth", "Drift width", 0.3f, 4f, 0.1f, 1.5f, "m",
                                 help: "How far out from a wall its drift reaches before the cover is level again.");
        SnowVary = G.Slider("snowvary", "Ground variation", 0f, 1f, 0.05f, 0.5f,
                            help: "Uneven depth across open ground: patches of thinner and thicker cover.");
        SnowDepth = G.Slider("snowdepth", "Snow depth", 0f, 0.6f, 0.02f, 0.25f, "m",
                             help: "Height of the mounds and drifts: the ground is marched as a relief so they stand up, self-occlude and lean. 0 = flat shading only.");
        SnowSteps = G.Slider("snowsteps", "Relief steps", 4f, 24f, 1f, 10f, tier: Tier.Advanced,
                             help: "March steps through the relief. More is smoother at grazing angles and costs more per snowed pixel.");
        SnowBump = G.Slider("snowbump", "Mound relief", 0f, 1f, 0.05f, 0.5f,
                            help: "Soft mounds in the cover, so it lights like drifted snow rather than paint. 0 = flat.");
        SnowSparkle = G.Slider("snowsparkle", "Sparkle", 0f, 1f, 0.05f, 0.5f, help: "Glints on lit snow that shift as the view moves. 0 = off.");
        SnowWrap = G.Slider("snowwrap", "Soft lighting", 0f, 1f, 0.05f, 0.4f,
                            help: "Light scatters inside snow, so the terminator wraps past the sun-facing edge. 0 = hard.");
        SnowSky = G.Slider("snowsky", "Sky in the shade", 0f, 2f, 0.1f, 0.6f,
                           help: "Shaded snow takes the sky's colour (the blue in a snow shadow). 0 = off.");
        G.Section("Puddles");
        PuddleThreshold = G.Slider("puddle", "Puddle threshold", 0f, 1f, 0.05f, 0.5f,
                                   help: "Wetness at which puddles start to fill. 1 = never.");
        PuddleCoverage = G.Slider("puddlecover", "Puddle coverage", 0f, 1f, 0.05f, 0.15f,
                                  help: "How much low ground pools at full fill.");
        PuddleEdge = G.Slider("puddleedge", "Puddle edge", 0.02f, 0.5f, 0.01f, 0.12f, tier: Tier.Advanced,
                              help: "Softness of a puddle's shoreline.");
    }

    /// <summary>The integrators, by wall clock. Called from every lit upload; the small deltas between two
    /// uploads of one frame sum to the frame, so nothing needs a frame stamp.</summary>
    private static void TickSurface()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _surfTicks == 0 ? 0f : MathF.Min((float)(now - _surfTicks) / Stopwatch.Frequency, 0.25f);
        _surfTicks = now;
        if (_off || !Enabled.V || FrameInputs.Interior) { _wet = _snow = _puddle = 0f; return; }
        bool rain = CurrentKind == Kind.Rain && Density > 0f, snow = CurrentKind == Kind.Snow && Density > 0f;
        if (_enterTicks != 0 && (rain || snow))
        {
            bool fresh = (float)(now - _enterTicks) / Stopwatch.Frequency < ArriveWindowSec;
            _enterTicks = 0;
            if (fresh && Arrive.V) { _wet = rain ? 1f : 0f; _snow = snow ? 1f : 0f; _puddle = rain && PuddleThreshold.V < 1f ? 1f : 0f; }
        }
        _wet = Math.Clamp(_wet + (rain ? Density / AccumSec.V : -1f / DrySec.V) * dt, 0f, 1f);
        _snow = Math.Clamp(_snow + (snow ? Density / AccumSec.V : -(rain ? 3f : 1f) / MeltSec.V) * dt, 0f, 1f);
        float th = PuddleThreshold.V;
        float fill = th < 1f && _wet > th ? (_wet - th) / (1f - th) / AccumSec.V : -0.5f / DrySec.V;
        _puddle = Math.Clamp(_puddle + fill * dt, 0f, 1f);
    }

    /// <summary>Per lit program bind: the amounts, the look, and the cover view (zero matrix = no map, which the
    /// stage reads as fully exposed). Absent on a stale content override (location -1): GL ignores it.</summary>
    internal static void UploadLit(int locWeather, int locLook, int locCoverVp, int locCoverLayer, int locSnowLook, int locSnowLight, int locSnowPom, bool capturing)
    {
        if (locWeather < 0) return;
        TickSurface();
        bool on = !capturing && !_off && Enabled.V;
        Gl.Uniform4f(locWeather, on ? _wet * WetStrength.V : 0f, on ? _snow * SnowStrength.V : 0f,
                     on ? _puddle : 0f, on ? Viz.V : 0f);
        float radius = MathF.Max(CoverRange.V, 1f);
        Gl.Uniform4f(locLook, 0.45f, SnowAlbedo.V, PuddleEdge.V, PuddleCoverage.V);
        Gl.Uniform4f(locSnowLook, SnowSparkle.V, SnowBump.V, SnowPile.V, MathF.Cos(SnowSlope.V * MathF.PI / 180f));
        // z = the drift width in cover-view uv (the fit is 2 x radius across), w = ground variation
        Gl.Uniform4f(locSnowLight, SnowWrap.V, SnowSky.V, SnowPileWidth.V * 0.5f / radius, SnowVary.V);
        Gl.Uniform4f(locSnowPom, SnowDepth.V, SnowSteps.V, TilesetSkipsSnow ? 1f : 0f, 0f);
        bool cover = on && Cover.V && CasterPass.CoverFresh && CasterPass.CascadesFresh;
        Matrix4x4 vp = cover ? CasterPass.CoverVp : default;
        Gl.UniformMatrix4fv(locCoverVp, 1, 0, (float*)&vp);
        // z = uv per metre in the cover view (the fit is 2 x radius across), for the soft-edge taps
        Gl.Uniform4f(locCoverLayer, CasterPass.CoverLayer, HollowStepM / (CoverDepthRadii * radius), CoverSoft.V * 0.5f / radius, SplashRate(capturing));
    }

    /// <summary>Drops per cell per period, 0..1: the density under the splash knob while it rains.</summary>
    internal static float SplashRate(bool capturing)
        => !capturing && !_off && Enabled.V && CurrentKind == Kind.Rain ? Density * Splash.V : 0f;

    private static void ResetSurface() { _wet = _snow = _puddle = 0f; _enterTicks = Stopwatch.GetTimestamp(); }

    private static string SurfaceStats() => $" wet {_wet:F2} snow {_snow:F2} puddle {_puddle:F2}";

    /// <summary>The rain-impact ripple, its own lib because the WATER stage takes it too (fed <c>uSplash</c>
    /// there, <c>uCoverLayer.w</c> on the lit stage); the hash rides with it.</summary>
    internal const string ImpactLib = """

        float CxWeatherHash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        uniform float uSplash;         // the water stage's rain impact rate (0 = none)

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
                // rings wide, and the slope below is its derivative in x
                float x = (d - age * 0.45 * size) * (28.0 - 10.0 * age);
                float env = exp(-x * x * 0.12);
                float slope = (2.0 * cos(2.0 * x) - 0.24 * x * sin(2.0 * x)) * env;
                r += dv / max(d, 1e-4) * slope * exp(-3.0 * age) * size;
            }
            return r;
        }

        """;

    /// <summary>The lit stage's weather reads, composed after <see cref="AlloyLights.SunShadowLib"/> (they sample
    /// the sun array's cover layer). Exposure is a four-tap PCF like a cascade's; the hollow test is eight
    /// compares against this point's own depth minus a step -- a neighbour standing higher fails it.</summary>
    internal const string WeatherLib = ImpactLib + """

        // ---- #617/#618/#619: weather on surfaces --------------------------------------------------------
        uniform vec4 uWeather;         // x wetness, y snow, z puddle fill (all 0..1), w = /weather viz
        uniform vec4 uWeatherLook;     // x wet darkening, y snow albedo, z puddle edge, w puddle coverage
        uniform mat4 uCoverVp;         // world -> the cover view's clip space; zero = no map (fully exposed)
        uniform vec4 uCoverLayer;      // x = the sun-array layer holding the map, y = the hollow step in depth units, z = the edge feather in uv, w = rain impact rate (0 = none)
        uniform vec4 uSnowLook;        // x sparkle, y mound relief, z drift against walls, w = cos(steepest slope snow holds to)
        uniform vec4 uSnowLight;       // x scatter wrap, y sky in the shade, z drift width in cover uv, w ground variation
        uniform vec4 uSnowPom;         // x relief height in metres (0 = off), y march steps, z = 1 when tileset parts take no cover

        uniform sampler2D uCoverEsm;   // the filtered cover map (Weather.Esm), on the snapshot-depth unit
        uniform vec4 uCoverEsmP;       // x = the variance floor in depth units squared, y = height bias in depth units, z = 1 when the map is bound

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

        """;

    /// <summary>The lit main's splice, between material synthesis and the lighting: exposure, then the three
    /// masks, then the surface rewritten under them. A material hosting its own weather body
    /// (<c>td_ground_fs</c>) keeps it. <c>/weather viz 2</c> paints exposure.</summary>
    internal const string LitSplice = """
            // #618/#619: the weather on exposed surfaces -- exposure is the rain occlusion map, the amounts are
            // the CPU integrators'. A material hosting its own weather body (td_ground_fs) keeps its look.
            float wxWet = 0.0, wxSnow = 0.0, wxPuddle = 0.0, wxPile = 0.0;
            vec2 wxDriftDir = vec2(0.0), wxP = vWorld.xy;
            if (uWeather.w > 2.5) { frag = d.emi.w < -0.5 ? vec4(1.0, 0.0, 0.0, 1.0) : d.emi.w > 0.5 ? vec4(0.0, 0.0, 1.0, 1.0) : vec4(0.0, 1.0, 0.0, 1.0); return; }
            if ((uWeather.x > 0.0 || uWeather.y > 0.0 || uWeather.w > 1.5) && feat != CX_FEAT_GROUNDWEATHER)
            {
                float wxExp = CxWeatherExposure(vWorld, N);
                if (uWeather.w > 1.5) { frag = vec4(vec3(wxExp), 1.0); return; }
                vec3 Ng = normalize(vNrm);
                float up = clamp(Ng.z, 0.0, 1.0);
                float n1 = CxWeatherNoise(vWorld.xy * 0.7), n2 = CxWeatherNoise(vWorld.xy * 3.1);
                wxWet = uWeather.x * wxExp;
                if (uWeather.y > 0.0 && d.emi.w >= -0.5 && !(uSnowPom.z > 0.5 && d.emi.w > 0.5))   // no snow on a creature (emi.w -1), nor on a listed tileset's tiles
                {
                    float wxSlope = smoothstep(uSnowLook.w - 0.15, uSnowLook.w + 0.15, up + (n1 - 0.5) * 0.4);
                    // drift: banked against whatever stands higher, out to the drift width
                    vec3 wxDrift = uSnowLook.z > 0.0 ? CxSnowDrift(vWorld, uSnowLight.z) : vec3(0.0);
                    wxPile = smoothstep(0.0, 0.6, wxDrift.x) * uSnowLook.z * up;
                    wxDriftDir = wxDrift.yz;
                    // ground variation: broad patches of thinner cover across open ground
                    float wxVary = 1.0 - uSnowLight.w * smoothstep(0.35, 0.7, CxWeatherNoise(vWorld.xy * 0.23)) * 0.6;
                    wxSnow = clamp(uWeather.y * wxExp * wxSlope * (0.75 + 0.25 * n2) * wxVary * (1.0 + wxPile), 0.0, 1.0);
                    // parallax relief: the snow surface stands uSnowPom.x x height above the mesh. March the view
                    // ray from where it enters the relief's top down to the mesh, and shade the point where it
                    // meets the surface; the snow is one colour, so only the relief's own terms move, never the uv.
                    // The drift is taken at the entry and at the mesh point and interpolated along the ray.
                    if (uSnowPom.x > 0.0 && wxSnow > 0.4 && up > 0.7)
                    {
                        float amp = uSnowPom.x * wxSnow;
                        float vn = max(dot(V, Ng), 0.15);
                        vec2 top = vWorld.xy + V.xy * (amp / vn);
                        float pileTop = uSnowLook.z > 0.0 ? smoothstep(0.0, 0.6, CxSnowDrift(vec3(top, vWorld.z), uSnowLight.z).x) * uSnowLook.z * up : 0.0;
                        float n = uSnowPom.y, prevH = 1.0, prevS = 1.0, s = 1.0;
                        vec2 p = top;
                        for (int i = 1; i <= int(n); i++)
                        {
                            s = 1.0 - float(i) / n;
                            p = mix(vWorld.xy, top, s);
                            float hs = CxSnowHeight(p, uSnowLight.w, mix(wxPile, pileTop, s));
                            if (s <= hs)
                            {
                                float t = (prevS - prevH) / max((prevS - prevH) - (s - hs), 1e-4);   // where the ray crossed
                                s = mix(prevS, s, t);
                                break;
                            }
                            prevH = hs; prevS = s;
                        }
                        wxP = mix(vWorld.xy, top, s);
                        wxPile = mix(wxPile, pileTop, s);
                    }
                }
                if (uWeather.z > 0.0 && up > 0.5)
                {
                    // concave = the AO says low ground, or the rain view says the neighbours stand higher
                    float conc = clamp((1.0 - occl) * 1.5 + CxWeatherHollow(vWorld), 0.0, 1.0);
                    float wxFlat = smoothstep(0.9, 0.98, up);
                    float m = conc * 0.5 + n1 * 0.6 + n2 * 0.15 + uWeatherLook.w - 0.7;
                    wxPuddle = uWeather.z * wxFlat * wxExp * smoothstep(0.5 - uWeatherLook.z, 0.5 + uWeatherLook.z, m);
                }
                if (wxSnow > 0.0)
                {
                    albedo = mix(albedo, vec3(uWeatherLook.y), wxSnow);
                    rough = mix(rough, 0.75, wxSnow); spec = mix(spec, 0.08, wxSnow);
                    // mounds: the normal tilts down the relief's gradient at the point the ray met it (or under
                    // the pixel with the relief off), more as the cover deepens
                    vec2 wxG = vec2(CxSnowHeight(wxP + vec2(0.05, 0.0), uSnowLight.w, wxPile) - CxSnowHeight(wxP - vec2(0.05, 0.0), uSnowLight.w, wxPile),
                                    CxSnowHeight(wxP + vec2(0.0, 0.05), uSnowLight.w, wxPile) - CxSnowHeight(wxP - vec2(0.0, 0.05), uSnowLight.w, wxPile));
                    // a drift is a slope rising toward the wall: the normal leans away from it, most at its foot.
                    // The cover view's uv axes are its own right/up, not world xy; the tilt is applied in world
                    // xy through the view's inverse rotation -- the map is a top-down orthographic fit, so its
                    // uv is a rotation of world xy and the first two columns carry it.
                    vec2 wxLean = vec2(dot(uCoverVp[0].xy, wxDriftDir), dot(uCoverVp[1].xy, wxDriftDir));
                    wxLean = length(wxLean) > 1e-5 ? normalize(wxLean) : vec2(0.0);
                    N = normalize(mix(N, Ng, wxSnow * 0.7) - vec3(wxG * uSnowLook.y * 3.0 * wxSnow, 0.0) - vec3(wxLean * wxPile * 0.8, 0.0));
                    // deeper cover brightens and rounds off: the mound reads thicker than the blanket
                    albedo = mix(albedo, vec3(uWeatherLook.y) * 1.05, clamp(wxPile, 0.0, 1.0) * 0.5);
                }
                if (wxWet > 0.0)
                {
                    albedo *= 1.0 - uWeatherLook.x * wxWet;
                    rough = mix(rough, 0.35, wxWet); spec = max(spec, 0.25 * wxWet);
                }
                if (wxPuddle > 0.0)
                {
                    albedo *= 1.0 - 0.5 * wxPuddle;
                    rough = mix(rough, 0.12, wxPuddle); spec = max(spec, 0.4 * wxPuddle);
                    N = normalize(mix(N, vec3(0.0, 0.0, 1.0), wxPuddle));
                }
                if (uCoverLayer.w > 0.0 && wxPuddle > 0.0 && up > 0.9)
                    N = normalize(N + vec3(CxWeatherImpact(vWorld.xy, uCoverLayer.w) * wxExp * wxPuddle * 0.3, 0.0));
            }

""";

    /// <summary>The wet reflection, after the material's own probe term, in the ENGINE's form: the probe sample
    /// tints the light already arriving, behind a Schlick Fresnel. Added radiance was tried first and gave the
    /// probe's one capture point away -- a fire's reflection smeared past the range the light itself reaches,
    /// and a whole scene mirrored from the wrong place. Tinting cannot exceed the light that is there, so the
    /// sheen ends where the lamp's range ends, with its shadow. The sample is kept blurry (roughness floor)
    /// because one cube cannot mirror a plaza sharply from every point on it.</summary>
    internal const string LitSpliceProbe = """
            if (uCapture == 0 && uProbeMips > 0.0 && (wxWet > 0.0 || wxPuddle > 0.0))
            {
                float wxFres = 0.04 + 0.96 * pow(1.0 - clamp(dot(N, V), 0.0, 1.0), 5.0);
                vec3 wxEnv = textureLod(uProbe, reflect(-V, N), max(rough, 0.45) * (uProbeMips - 1.0)).rgb;
                specLight += (ambient + diffuse) * wxEnv * wxFres * max(wxWet, wxPuddle) * 3.0;
            }
            if (wxSnow > 0.0)
            {
                // scatter: the terminator wraps past the sun-facing edge, the way light travelling inside snow does
                diffuse += uAreaDiffuse * sunVis * max(dot(N, uAreaDir) + uSnowLight.x, 0.0) * uSnowLight.x * wxSnow;
                // the sky fills the shade: what makes a snow shadow blue
                ambient += uFogSkyColor * uSnowLight.y * (1.0 - sunVis) * occl * wxSnow;
                // sparkle: a 6 mm cell hash re-rolled as the view turns, the brightest 1.5% of cells glint
                if (uSnowLook.x > 0.0)
                {
                    float wxGl = CxWeatherHash(floor(vWorld.xy * 160.0) + floor(vWorld.z * 160.0) * 0.37 + floor(V.xy * 12.0));
                    specLight += (uAreaDiffuse * sunVis + uAreaAmbient) * smoothstep(0.985, 1.0, wxGl) * uSnowLook.x * wxSnow * 4.0;
                }
            }

""";

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string lit = ShaderStages.Source(AlloyRenderer.ShdLitFs);
        check.Expect(lit.IndexOf("uniform sampler2DArrayShadow uSunShadow;", StringComparison.Ordinal) < lit.IndexOf("float CxWeatherExposure(", StringComparison.Ordinal),
          "the weather lib composes after the sun shadow lib it samples");
        check.Expect(lit.Contains(LitSplice) && lit.Contains(LitSpliceProbe), "the lit main carries both splices");
        check.Expect(lit.IndexOf("float spec = pbr.x, rough = pbr.y, metal = pbr.z;", StringComparison.Ordinal) < lit.IndexOf("float wxWet = 0.0", StringComparison.Ordinal)
          && lit.IndexOf("float wxWet = 0.0", StringComparison.Ordinal) < lit.IndexOf("vec3 base = albedo * matDiffuse;", StringComparison.Ordinal),
          "the surface splice sits after synthesis and before the lighting reads albedo/rough/spec");
        check.Expect(lit.Contains("uniform vec3 uFogSkyColor;") && lit.IndexOf("float sunVis = ", StringComparison.Ordinal) < lit.IndexOf("if (wxSnow > 0.0)\n            {\n                // scatter", StringComparison.Ordinal)
          && lit.IndexOf("vec3 ambient = ", StringComparison.Ordinal) < lit.IndexOf("ambient += uFogSkyColor", StringComparison.Ordinal),
          "the snow light block sees the sky colour, the sun visibility and the ambient it adds to");
        check.Expect(lit.Contains("uniform int uOit;") && lit.IndexOf("uniform int uOit;", StringComparison.Ordinal) < lit.IndexOf("uCoverEsmP.z > 0.5 && uOit == 0", StringComparison.Ordinal),
          "the exposure read sees the OIT mode before it picks the filtered map");
        check.Expect(WeatherVs.Contains("uniform float uBack;") && WeatherVs.Contains("CxSunShadow(P, -N, back, back)"), "the flake backscatter reads the sun through its cascades");
        check.Expect(WeatherVs.Contains("uniform sampler2DArrayShadow uCover;") && WeatherVs.Contains("CxCovered(Pc);") && WeatherVs.Contains("shown < 0.5"),
          "the precipitation vertex stage tests its instance against the cover map");
        check.Expect(WeatherVs.Contains("uKind == 3 ? CxLip(P, R, U)") && WeatherVs.Contains("if (CxCovered(P) >= 0.5) return 0.0;"),
          "a drip instance survives only under a lip: covered here, open within the feather");
        check.Expect(ShaderContract.LinearSafe(RainFs) && ShaderContract.LinearSafe(SnowFs), "the precipitation stages stay linear");
        check.Expect(WeatherVs.IndexOf("vec3 CxClusteredDiffuse(", StringComparison.Ordinal) < WeatherVs.IndexOf("if (uLit != 0 && shown >= 0.5)", StringComparison.Ordinal)
          && WeatherVs.Contains("#define CX_CLUSTER_XY cxClusterXY") && WeatherVs.Contains("CxSunShadow(P, N, ndl, ndl)")
          && RainFs.Contains("uTint * vLight") && SnowFs.Contains("uTint * vLight"),
          "rain and snow take the light rig per vertex and both fragment stages multiply it in");
        check.Expect(KindOf(1) == Kind.Rain && KindOf(2) == Kind.Snow && KindOf(0) == Kind.None && KindOf(7) == Kind.None,
          "stock rows 1/2 map to rain/snow, row 0 and the fog row to none");
        float d = Density; Kind k = CurrentKind; bool en = Enabled.V;
        Enabled.V = true; Density = 1f; CurrentKind = Kind.Rain; _wet = 0f; _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10;
        TickSurface(); check.Expect(_wet > 0f && _wet < 0.01f, "one 100ms rain tick soaks a little, not a lot");
        CurrentKind = Kind.None; Density = 0f; float w = _wet; _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10;
        TickSurface(); check.Expect(_wet < w, "a dry tick decays");
        ResetSurface(); Density = 1f; CurrentKind = Kind.Snow; _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10;
        TickSurface(); check.Expect(_snow == 1f && _wet == 0f, "snow already running at the area edge arrives fully settled");
        _enterTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 60; _snow = 0f; _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10;
        TickSurface(); check.Expect(_snow > 0f && _snow < 0.01f, "snow starting a minute after the edge accumulates from nothing");
        Density = d; CurrentKind = k; Enabled.V = en; _wet = _snow = _puddle = 0f; _surfTicks = 0; _enterTicks = 0;
        log.Info(check.Ok ? "Weather.SelfCheck: passed" : "Weather.SelfCheck: FAILED");
    }
}
