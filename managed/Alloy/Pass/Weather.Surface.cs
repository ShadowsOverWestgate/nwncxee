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

    internal static Knob<float> WetStrength = null!, SnowStrength = null!, AccumSec = null!, DrySec = null!, MeltSec = null!,
                                PuddleThreshold = null!, PuddleCoverage = null!, PuddleEdge = null!, Splash = null!;
    private static float _wet, _snow, _puddle;
    private static long _surfTicks;

    internal static float Wet => _wet;
    internal static float Snow => _snow;
    internal static float Puddle => _puddle;

    private static void DeclareSurfaceKnobs()
    {
        G.Section("Surfaces");
        WetStrength = G.Slider("wet", "Wet surfaces", 0f, 1f, 0.05f, 1f,
                               help: "Exposed ground and roofs darken and turn glossy in rain, reflecting the sky. 0 = off.");
        SnowStrength = G.Slider("snow", "Snow cover", 0f, 1f, 0.05f, 1f,
                                help: "Exposed up-facing surfaces whiten in snow. 0 = off.");
        AccumSec = G.Slider("accum", "Soak / settle time", 5f, 600f, 5f, 60f, "s",
                            help: "Seconds of full-density weather to fully wet or snow the ground.");
        DrySec = G.Slider("dry", "Dry-out time", 10f, 1800f, 10f, 180f, "s", help: "Seconds for wet ground to dry after the rain stops.");
        MeltSec = G.Slider("melt", "Melt time", 30f, 7200f, 30f, 900f, "s", help: "Seconds for snow cover to melt after the snowfall ends.");
        Splash = G.Slider("splash", "Rain impacts", 0f, 1f, 0.05f, 1f,
                          help: "Drops splash where they land on wet ground and ring the puddles they hit. 0 = off.");
        G.Section("Puddles");
        PuddleThreshold = G.Slider("puddle", "Puddle threshold", 0f, 1f, 0.05f, 0.5f,
                                   help: "Wetness at which puddles start to fill. 1 = never.");
        PuddleCoverage = G.Slider("puddlecover", "Puddle coverage", 0f, 1f, 0.05f, 0.45f,
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
        _wet = Math.Clamp(_wet + (rain ? Density / AccumSec.V : -1f / DrySec.V) * dt, 0f, 1f);
        _snow = Math.Clamp(_snow + (snow ? Density / AccumSec.V : -(rain ? 3f : 1f) / MeltSec.V) * dt, 0f, 1f);
        float th = PuddleThreshold.V;
        float fill = th < 1f && _wet > th ? (_wet - th) / (1f - th) / AccumSec.V : -0.5f / DrySec.V;
        _puddle = Math.Clamp(_puddle + fill * dt, 0f, 1f);
    }

    /// <summary>Per lit program bind: the amounts, the look, and the cover view (zero matrix = no map, which the
    /// stage reads as fully exposed). Absent on a stale content override (location -1): GL ignores it.</summary>
    internal static void UploadLit(int locWeather, int locLook, int locCoverVp, int locCoverLayer, bool capturing)
    {
        if (locWeather < 0) return;
        TickSurface();
        bool on = !capturing && !_off && Enabled.V;
        Gl.Uniform4f(locWeather, on ? _wet * WetStrength.V : 0f, on ? _snow * SnowStrength.V : 0f,
                     on ? _puddle : 0f, on ? Viz.V : 0f);
        float radius = MathF.Max(CoverRange.V, 1f);
        Gl.Uniform4f(locLook, 0.45f, 0.85f, PuddleEdge.V, PuddleCoverage.V);
        bool cover = on && Cover.V && CasterPass.CoverFresh && CasterPass.CascadesFresh;
        Matrix4x4 vp = cover ? CasterPass.CoverVp : default;
        Gl.UniformMatrix4fv(locCoverVp, 1, 0, (float*)&vp);
        // z = uv per metre in the cover view (the fit is 2 x radius across), for the soft-edge taps
        float rain = on && CurrentKind == Kind.Rain ? Density * Splash.V : 0f;
        Gl.Uniform4f(locCoverLayer, CasterPass.CoverLayer, HollowStepM / (CoverDepthRadii * radius), CoverSoft.V * 0.5f / radius, rain);
    }

    private static void ResetSurface() { _wet = _snow = _puddle = 0f; }

    private static string SurfaceStats() => $" wet {_wet:F2} snow {_snow:F2} puddle {_puddle:F2}";

    /// <summary>The lit stage's weather reads, composed after <see cref="AlloyLights.SunShadowLib"/> (they sample
    /// the sun array's cover layer). Exposure is a four-tap PCF like a cascade's; the hollow test is eight
    /// compares against this point's own depth minus a step -- a neighbour standing higher fails it.</summary>
    internal const string WeatherLib = """

        // ---- #617/#618/#619: weather on surfaces --------------------------------------------------------
        uniform vec4 uWeather;         // x wetness, y snow, z puddle fill (all 0..1), w = /weather viz
        uniform vec4 uWeatherLook;     // x wet darkening, y snow albedo, z puddle edge, w puddle coverage
        uniform mat4 uCoverVp;         // world -> the cover view's clip space; zero = no map (fully exposed)
        uniform vec4 uCoverLayer;      // x = the sun-array layer holding the map, y = the hollow step in depth units, z = the edge feather in uv, w = rain impact rate (0 = none)

        float CxWeatherHash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

        float CxWeatherExposure(vec3 worldPos, vec3 N)
        {
            if (uCoverVp[3][3] == 0.0) return 1.0;
            vec4 clip = uCoverVp * vec4(worldPos + N * 0.15, 1.0);
            vec3 ndc = clip.xyz / clip.w;
            if (any(greaterThan(abs(ndc.xy), vec2(1.0))) || abs(ndc.z) > 1.0) return 1.0;
            vec2 uv = ndc.xy * 0.5 + 0.5;
            float ref = ndc.z * 0.5 + 0.5 - 0.0015;
            float L = uCoverLayer.x;
            // centre, a cross at the feather radius and a diagonal cross at half of it: nine levels across the
            // band, in a FIXED pattern -- a per-cell rotation read as 25 cm blocks on open ground.
            float r = uCoverLayer.z, h = r * 0.5;
            return (texture(uSunShadow, vec4(uv, L, ref))
                  + texture(uSunShadow, vec4(uv + vec2( r, 0.0), L, ref))
                  + texture(uSunShadow, vec4(uv + vec2(-r, 0.0), L, ref))
                  + texture(uSunShadow, vec4(uv + vec2(0.0,  r), L, ref))
                  + texture(uSunShadow, vec4(uv + vec2(0.0, -r), L, ref))
                  + texture(uSunShadow, vec4(uv + vec2( h,  h), L, ref))
                  + texture(uSunShadow, vec4(uv + vec2(-h,  h), L, ref))
                  + texture(uSunShadow, vec4(uv + vec2( h, -h), L, ref))
                  + texture(uSunShadow, vec4(uv + vec2(-h, -h), L, ref))) / 9.0;
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

        // Rain impacts: two offset 25 cm lattices, one drop per cell per period (0.4-1 s, hashed per cell), present
        // with probability = the rate. A ring front runs out from the impact point over the period and the normal
        // tilts along its radial derivative -- the wave; a dot at the point for the first sixth of it -- the splash.
        // Returns xy = tilt, z = splash.
        vec3 CxWeatherImpact(vec2 p, float rate)
        {
            float t = mod(float(uTimeOfDay), 3600000.0) * 0.001;   // hour wrap keeps fract() fine-grained
            vec3 r = vec3(0.0);
            for (int i = 0; i < 2; i++)
            {
                vec2 q = p * 4.0 + float(i) * 0.5;
                vec2 cell = floor(q) + float(i) * 37.0;
                float period = 0.4 + 0.6 * CxWeatherHash(cell);
                float ph = t / period + CxWeatherHash(cell + 11.0);
                float idx = floor(ph), age = fract(ph);
                if (CxWeatherHash(cell + idx) > rate) continue;
                vec2 c = vec2(CxWeatherHash(cell + idx + 3.0), CxWeatherHash(cell + idx + 5.0)) * 0.5 + 0.25;
                vec2 dv = fract(q) - c;
                float d = length(dv);
                float w = (d - age * 0.5) * 24.0;
                r.xy += dv / max(d, 1e-4) * exp(-w * w) * -2.0 * w * (1.0 - age);
                r.z += exp(-d * d * 400.0) * max(0.0, 1.0 - age * 6.0);
            }
            return r;
        }

        float CxWeatherNoise(vec2 p)
        {
            vec2 i = floor(p), f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            return mix(mix(CxWeatherHash(i), CxWeatherHash(i + vec2(1.0, 0.0)), f.x),
                       mix(CxWeatherHash(i + vec2(0.0, 1.0)), CxWeatherHash(i + vec2(1.0, 1.0)), f.x), f.y);
        }

        """;

    /// <summary>The lit main's splice, between material synthesis and the lighting: exposure, then the three
    /// masks, then the surface rewritten under them. A material hosting its own weather body
    /// (<c>td_ground_fs</c>) keeps it. <c>/weather viz 2</c> paints exposure.</summary>
    internal const string LitSplice = """
            // #618/#619: the weather on exposed surfaces -- exposure is the rain occlusion map, the amounts are
            // the CPU integrators'. A material hosting its own weather body (td_ground_fs) keeps its look.
            float wxWet = 0.0, wxSnow = 0.0, wxPuddle = 0.0;
            if (uWeather.w > 2.5) { frag = d.emi.w < -0.5 ? vec4(1.0, 0.0, 0.0, 1.0) : d.emi.w > 0.5 ? vec4(0.0, 0.0, 1.0, 1.0) : vec4(0.0, 1.0, 0.0, 1.0); return; }
            if ((uWeather.x > 0.0 || uWeather.y > 0.0 || uWeather.w > 1.5) && feat != CX_FEAT_GROUNDWEATHER)
            {
                float wxExp = CxWeatherExposure(vWorld, N);
                if (uWeather.w > 1.5) { frag = vec4(vec3(wxExp), 1.0); return; }
                vec3 Ng = normalize(vNrm);
                float up = clamp(Ng.z, 0.0, 1.0);
                float n1 = CxWeatherNoise(vWorld.xy * 0.7), n2 = CxWeatherNoise(vWorld.xy * 3.1);
                wxWet = uWeather.x * wxExp;
                if (uWeather.y > 0.0 && d.emi.w >= -0.5)   // no snow on a creature (emi.w -1)
                    wxSnow = uWeather.y * wxExp * smoothstep(0.3, 0.75, up + (n1 - 0.5) * 0.4) * (0.75 + 0.25 * n2);
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
                    N = normalize(mix(N, Ng, wxSnow * 0.7));
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
                if (uCoverLayer.w > 0.0 && wxWet > 0.0 && up > 0.9)
                {
                    vec3 wxImp = CxWeatherImpact(vWorld.xy, uCoverLayer.w) * wxExp;
                    albedo = mix(albedo, vec3(1.0), clamp(wxImp.z, 0.0, 1.0) * 0.6);
                    N = normalize(N + vec3(wxImp.xy * wxPuddle * 0.5, 0.0));
                }
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

""";

    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"Weather.SelfCheck FAIL: {w}"); } }
        string lit = ShaderStages.Source(AlloyRenderer.ShdLitFs);
        E(lit.IndexOf("uniform sampler2DArrayShadow uSunShadow;", StringComparison.Ordinal) < lit.IndexOf("float CxWeatherExposure(", StringComparison.Ordinal),
          "the weather lib composes after the sun shadow lib it samples");
        E(lit.Contains(LitSplice) && lit.Contains(LitSpliceProbe), "the lit main carries both splices");
        E(lit.IndexOf("float spec = pbr.x, rough = pbr.y, metal = pbr.z;", StringComparison.Ordinal) < lit.IndexOf("float wxWet = 0.0", StringComparison.Ordinal)
          && lit.IndexOf("float wxWet = 0.0", StringComparison.Ordinal) < lit.IndexOf("vec3 base = albedo * matDiffuse;", StringComparison.Ordinal),
          "the surface splice sits after synthesis and before the lighting reads albedo/rough/spec");
        E(WeatherVs.Contains("uniform sampler2DArrayShadow uCover;") && WeatherVs.Contains("CxCovered(Pc);") && WeatherVs.Contains("shown < 0.5"),
          "the precipitation vertex stage tests its instance against the cover map");
        E(WeatherVs.Contains("uKind == 3 ? CxLip(P, R, U)") && WeatherVs.Contains("if (CxCovered(P) >= 0.5) return 0.0;"),
          "a drip instance survives only under a lip: covered here, open within the feather");
        E(ShaderContract.LinearSafe(RainFs) && ShaderContract.LinearSafe(SnowFs), "the precipitation stages stay linear");
        E(KindOf(1) == Kind.Rain && KindOf(2) == Kind.Snow && KindOf(0) == Kind.None && KindOf(7) == Kind.None,
          "stock rows 1/2 map to rain/snow, row 0 and the fog row to none");
        float d = Density; Kind k = CurrentKind; bool en = Enabled.V;
        Enabled.V = true; Density = 1f; CurrentKind = Kind.Rain; _wet = 0f; _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10;
        TickSurface(); E(_wet > 0f && _wet < 0.01f, "one 100ms rain tick soaks a little, not a lot");
        CurrentKind = Kind.None; Density = 0f; float w = _wet; _surfTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10;
        TickSurface(); E(_wet < w, "a dry tick decays");
        Density = d; CurrentKind = k; Enabled.V = en; _wet = _snow = _puddle = 0f; _surfTicks = 0;
        log.Info(ok ? "Weather.SelfCheck: passed" : "Weather.SelfCheck: FAILED");
    }
}
