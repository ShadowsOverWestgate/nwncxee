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

    /// <summary>How far wet surfaces darken at full wetness: the lit stage's splice and the grass share it.</summary>
    internal const float WetDarkening = 0.45f;

    /// <summary>The wetness the lit stage's splice is handed (0 while the weather is off), for stages that
    /// take no rain occlusion map of their own.</summary>
    internal static float WetShown => !_off && Enabled.V ? _wet * WetStrength.V : 0f;

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
        Gl.Uniform4f(locLook, WetDarkening, SnowAlbedo.V, PuddleEdge.V, PuddleCoverage.V);
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

    /// <summary>The lit main's splice, between material synthesis and the lighting: exposure, then the three
    /// masks, then the surface rewritten under them. A material hosting its own weather body
    /// (<c>td_ground_fs</c>) keeps it. <c>/weather viz 2</c> paints exposure.</summary>
    internal static string LitSplice => ShaderSource.Get("cx_wx_litsplice.glsl");

    /// <summary>The wet reflection, after the material's own probe term, in the ENGINE's form: the probe sample
    /// tints the light already arriving, behind a Schlick Fresnel. Added radiance was tried first and gave the
    /// probe's one capture point away -- a fire's reflection smeared past the range the light itself reaches,
    /// and a whole scene mirrored from the wrong place. Tinting cannot exceed the light that is there, so the
    /// sheen ends where the lamp's range ends, with its shadow. The sample is kept blurry (roughness floor)
    /// because one cube cannot mirror a plaza sharply from every point on it.</summary>
    internal static string LitSpliceProbe => ShaderSource.Get("cx_wx_litprobe.glsl");

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
