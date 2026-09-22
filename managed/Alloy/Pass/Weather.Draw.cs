using System;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>#616: the precipitation field. One instanced attribute-less draw per frame: the instance id hashes
/// to a lattice point in a fall-aligned box, the copy of that point nearest the camera is the one drawn, and the
/// fall phase slides the lattice along the fall axis with <c>mod</c>, so the field wraps for ever and the CPU
/// stages nothing (the shape is <see cref="Halos"/>'). Rain is a streak along the fall vector, billboarded about
/// it; snow a camera-facing disc with a slow wobble. The fall vector is gravity plus the area wind the renderer
/// already uploads. Contact against geometry is the hardware depth test plus the particle pass's soft fade.
/// <para>The vertex stage is shared (<c>cx_weather_vs</c>); the look is the two fragment stages a hak can
/// override, <c>cx_rain_fs</c> / <c>cx_snow_fs</c> (ADR 0024).</para>
/// <para>#620 roof drips are the same draw with <c>uKind = 3</c>: an instance survives only where the cover map says
/// covered here and open within the feather distance -- under a lip, not mid-roof, not out in the rain -- so the
/// eave line drips with no edge-detect pass and no drip buffer. The reservoir is the wetness integrator (#618):
/// it fills under rain and drains over the dry-out time, so drips carry on and taper after the stop edge.</para></summary>
internal static unsafe partial class Weather
{
    internal const string ShdWeatherVs = "cx_weather_vs", ShdRainFs = "cx_rain_fs", ShdSnowFs = "cx_snow_fs";
    private const uint GlTriangleStrip = 0x0005;   // not-an-offset: GL primitive-mode enum
    /// <summary>Speed knobs are in m/s; the phase is kept in metres and wrapped at this many box lengths so
    /// the shader's <c>mod</c> never sees a large float, and the per-instance speed steps of 1/8 land on a
    /// box multiple at the wrap (1024/8 = 128 lengths).</summary>
    private const int PhaseWrapLengths = 1024;
    /// <summary>The wobble clock wraps where both wobble frequencies (1.0 and 1.5 rad/s) complete whole turns.</summary>
    private const float WobbleWrap = 400f * MathF.PI;

    internal static Knob<float> DensityScale = null!, StreakLen = null!, FlakeSize = null!, Brightness = null!, Opacity = null!, Range = null!,
                                RainSpeed = null!, SnowSpeed = null!, WindLean = null!, Soft = null!, Drips = null!, Backscatter = null!, SnowAngle = null!;
    internal static Knob<int> MaxCount = null!, Viz = null!;

    private static uint _progRain, _progSnow, _vao;
    private static Loc _locRain, _locSnow;
    private static AlloyRenderer.LitLoc _litRain, _litSnow;
    private static bool _failed;
    private static double _phase;
    private static float _wobble;
    private static long _ticks;
    private static int _drawn, _count, _drips;
    private static Vector3 _fall;

    private struct Loc { public int View, Proj, Eye, Centre, Fall, Phase, Time, Speed, Box, Size, Kind, Drift, Depth, Tint, Alpha, Soft, Viz, Cover, CoverVp, CoverLayer, CoverSoft, Lit, Back; }

    private static void DeclareKnobs()
    {
        G.Section("Precipitation");
        DensityScale = G.Slider("density", "Density", 0f, 3f, 0.1f, 1f, help: "Scales how much falls at the engine's own weather density.");
        StreakLen = G.Slider("streak", "Rain streak length", 0.1f, 2f, 0.05f, 0.35f, "m", help: "Mean length of one rain streak (each varies 0.5x-1.3x).");
        FlakeSize = G.Slider("flake", "Snowflake size", 0.01f, 0.2f, 0.01f, 0.05f, "m", help: "Diameter of one flake.");
        Brightness = G.Slider("bright", "Brightness", 0f, 3f, 0.1f, 0.6f, help: "Streaks and flakes take the light where they fall (sun, shadow, lamps); this scales it.");
        Opacity = G.Slider("opacity", "Rain opacity", 0.02f, 1f, 0.02f, 0.06f, help: "How solid a streak is. Real rain is faint.");
        Range = G.Slider("range", "Range", 6f, 40f, 1f, 20f, "m", Tier.Advanced, help: "Size of the box around the camera the field fills.");
        RainSpeed = G.Slider("rainspeed", "Rain speed", 2f, 30f, 0.5f, 12f, "m/s", Tier.Advanced);
        SnowSpeed = G.Slider("snowspeed", "Snow speed", 0.2f, 6f, 0.1f, 1.2f, "m/s", Tier.Advanced);
        WindLean = G.Slider("windlean", "Wind lean", 0f, 3f, 0.05f, 0.6f, tier: Tier.Advanced,
                            help: "How far the area wind tilts the fall. 0 falls straight down.");
        SnowAngle = G.Slider("snowangle", "Snow settle angle", 0f, 45f, 1f, 3f, "°", Tier.Advanced,
                             help: "Tilt of the settle direction from vertical, into the wind: how far snow reaches under an eave. Rain settles along its own fall.");
        Backscatter = G.Slider("backscatter", "Flake glow", 0f, 2f, 0.1f, 0.5f,
                               help: "A flake between you and a light glows, lit from behind. 0 = off.");
        Soft = G.Slider("soft", "Contact fade", 0f, 20f, 0.5f, 6f, tier: Tier.Advanced,
                        help: "How sharply a streak fades where it meets geometry (1/m).");
        Drips = G.Slider("drips", "Roof drips", 0f, 1f, 0.05f, 0.25f,
                         help: "Drops fall from roof edges and overhangs while the roofs are wet, and keep falling for the dry-out time after the rain stops. 0 = off.");
        MaxCount = G.Slider("count", "count", 500, 40000, 500, 16000, tier: Tier.ChatOnly);
        Viz = G.Slider("viz", "viz", 0, VizMax, 1, 0, tier: Tier.ChatOnly, help: "1 streak contact fade, 2 exposure on the world, 3 record class (red creature, green object, blue tileset)");
    }

    private static void InstallDraw(ExtenderLog log) => InstallCover(log);

    /// <summary>The fall vector this frame: gravity leaning into the area wind (a raw engine vector, see
    /// <see cref="AlloyRenderer.DescribeWind"/>), normalised. Shared with the occlusion map.</summary>
    internal static Vector3 Fall => _fall;

    /// <summary>The cover map's bake direction: rain's wind-leaning fall vector, but a fixed small tilt into
    /// the wind while snow falls or lies -- the leaning vector snowed the lee side of every wall and left the
    /// windward roof edge bare. Settled snow sits near where the sky is overhead; the tilt is the reach under
    /// an eave.</summary>
    internal static Vector3 CoverDir
    {
        get
        {
            if (!(CurrentKind == Kind.Snow || (CurrentKind == Kind.None && _snow > 0f))) return _fall;
            Vector2 h = new(_fall.X, _fall.Y);
            if (h.LengthSquared() < 1e-6f) h = Vector2.UnitX;
            h = Vector2.Normalize(h);
            float a = SnowAngle.V * MathF.PI / 180f;
            return new Vector3(h.X * MathF.Sin(a), h.Y * MathF.Sin(a), -MathF.Cos(a));
        }
    }

    private static void Tick(Vector2 wind)
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _ticks == 0 ? 0f : MathF.Min((float)(now - _ticks) / Stopwatch.Frequency, 0.1f);
        _ticks = now;
        Vector3 f = new(wind.X * WindLean.V, wind.Y * WindLean.V, -1f);
        _fall = Vector3.Normalize(f);
        float speed = CurrentKind == Kind.Snow ? SnowSpeed.V : RainSpeed.V;
        double wrap = Range.V * PhaseWrapLengths;
        _phase = (_phase + dt * speed) % wrap;
        _wobble = (_wobble + dt) % WobbleWrap;
    }

    /// <summary>The weather pass, after the halos: alpha over, depth-tested against the finished frame, no
    /// depth write. Precipitation while the engine's density is up; drips while the roofs are wet and there
    /// is a cover map to find their edges in. Hands back blend off, depth test on, depth mask on.</summary>
    internal static void Draw(Matrix4x4* view, Matrix4x4* proj, int* viewport, Vector3 eye, Vector2 wind, float near, float far, bool lightingOn)
    {
        _count = 0; _drips = 0;
        // an interior has no sky: nothing falls and nothing soaks, whatever weather the area was given
        if (_off || _failed || !Enabled.V || FrameInputs.Interior) return;
        bool snow = CurrentKind == Kind.Snow;
        int count = CurrentKind != Kind.None && Density > 0f ? (int)(MaxCount.V * Density * DensityScale.V * (snow ? 0.5f : 1f)) : 0;
        int drips = !snow && Cover.V && CasterPass.CoverFresh ? (int)(MaxCount.V * Drips.V * _wet) : 0;
        if (count <= 0 && drips <= 0) return;
        Tick(wind);
        if (!SceneDepth.Take(viewport)) return;
        SceneDepth.Bind();
        new PassState(depth: true, depthWrite: false, blend: true,
                      blendSrc: AlloyRenderer.GlSrcAlpha, blendDst: Gl.ONE_MINUS_SRC_ALPHA).Apply();
        if (_vao == 0) { uint v; Gl.GenVertexArrays(1, &v); _vao = v; }
        Gl.BindVertexArray(_vao);
        if (count > 0 && Submit(snow ? 2 : 1, count, view, proj, eye, viewport, near, far, lightingOn)) { _count = count; _drawn += count; }
        if (drips > 0 && Submit(3, drips, view, proj, eye, viewport, near, far, lightingOn)) { _drips = drips; _drawn += drips; }
        Gl.BindVertexArray(0);
        Gl.Disable(Gl.BLEND);
        Gl.DepthMask(1);
    }

    /// <summary>One instanced draw of <paramref name="kind"/> (1 rain, 2 snow, 3 drips: rain's program).</summary>
    private static bool Submit(int kind, int count, Matrix4x4* view, Matrix4x4* proj, Vector3 eye, int* viewport, float near, float far, bool lightingOn)
    {
        bool snow = kind == 2;
        if (!EnsureProgram(snow)) return false;
        ref Loc l = ref (snow ? ref _locSnow : ref _locRain);
        ref AlloyRenderer.LitLoc L = ref (snow ? ref _litSnow : ref _litRain);
        Gl.UseProgram(snow ? _progSnow : _progRain);
        Gl.UniformMatrix4fv(l.View, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(l.Proj, 1, 0, (float*)proj);
        Gl.Uniform3f(l.Eye, eye.X, eye.Y, eye.Z);
        // the box sits on the camera's follow target, not the camera: a top-down camera is 10-20 m up, and a
        // box about it would leave the ground -- and every eave between a roof and it -- outside the field
        Vector3 centre = FrameInputs.HasFocus ? FrameInputs.Focus : eye;
        Gl.Uniform3f(l.Centre, centre.X, centre.Y, centre.Z);
        Gl.Uniform3f(l.Fall, _fall.X, _fall.Y, _fall.Z);
        Gl.Uniform1f(l.Phase, (float)_phase);
        Gl.Uniform1f(l.Time, _wobble);
        Gl.Uniform2f(l.Box, Range.V, Range.V);
        // a drip is a short fat drop: a quarter streak, wider, twice as solid as the rain around it
        Gl.Uniform2f(l.Size, snow ? FlakeSize.V : kind == 3 ? StreakLen.V * 0.25f : StreakLen.V, snow ? FlakeSize.V : kind == 3 ? 0.03f : 0.02f);
        Gl.Uniform1i(l.Kind, kind);
        Gl.Uniform1f(l.Drift, snow ? 0.4f : 0f);
        Gl.Uniform1i(l.Depth, (int)SceneDepth.Unit);
        // the light itself is the vertex stage's (sun + shadow + clustered lamps at the instance); this is
        // the scalar on it -- flakes are near-opaque white, streaks faint, so the flake takes more of it
        float c = snow ? Brightness.V * 1.67f : Brightness.V;
        Gl.Uniform3f(l.Tint, c, c, c);
        bool lit = lightingOn && AlloyLights.Ready;
        Gl.Uniform1i(l.Lit, lit ? 1 : 0);
        Gl.Uniform1f(l.Back, snow ? Backscatter.V : 0f);
        AlloyRenderer.UploadAreaTerms(in L);
        if (lit)
        {
            AlloyLights.BindForDraw();
            AlloyLights.SetDrawUniforms(L.Grid, L.Tile, L.Z, L.Falloff, L.Wrap, L.Atten, L.Hot, L.WrapN, L.Sss, viewport, near, far, L.Reach);
            AlloyRenderer.UploadSunCascades(in L);
            AlloyRenderer.UploadLocalCubes(in L);
        }
        Gl.Uniform1f(l.Alpha, snow ? 0.9f : kind == 3 ? MathF.Min(Opacity.V * 2f, 1f) : Opacity.V);
        Gl.Uniform1f(l.Soft, Soft.V);
        Gl.Uniform1i(l.Viz, Viz.V);
        BindCover(ref l);
        Gl.DrawArraysInstancedBaseInstance(GlTriangleStrip, 0, 4, count, 0);
        return true;
    }

    private static bool EnsureProgram(bool snow)
    {
        // The GL entry points load at the first bracket, after Install -- so this is asked per draw, as the halos do.
        if (_failed || Gl.DrawArraysInstancedBaseInstance == null) return false;
        ref uint prog = ref (snow ? ref _progSnow : ref _progRain);
        if (prog != 0) return true;
        string fs = snow ? ShdSnowFs : ShdRainFs;
        prog = GlProgram.Graphics(ShdWeatherVs, ShaderStages.Source(ShdWeatherVs), fs, ShaderStages.Source(fs), _log, "Weather");
        if (prog == 0) { _failed = true; _log?.Info("Weather: program build failed -- nothing falls this session"); return false; }
        ref Loc l = ref (snow ? ref _locSnow : ref _locRain);
        l.View = GlProgram.Loc(prog, "uView"); l.Proj = GlProgram.Loc(prog, "uProj"); l.Eye = GlProgram.Loc(prog, "uEye"); l.Centre = GlProgram.Loc(prog, "uCentre");
        l.Fall = GlProgram.Loc(prog, "uFall"); l.Phase = GlProgram.Loc(prog, "uPhase"); l.Time = GlProgram.Loc(prog, "uTime");
        l.Box = GlProgram.Loc(prog, "uBox"); l.Size = GlProgram.Loc(prog, "uSize"); l.Kind = GlProgram.Loc(prog, "uKind");
        l.Drift = GlProgram.Loc(prog, "uDrift"); l.Depth = GlProgram.Loc(prog, "uSceneDepth"); l.Tint = GlProgram.Loc(prog, "uTint");
        l.Alpha = GlProgram.Loc(prog, "uAlpha"); l.Soft = GlProgram.Loc(prog, "uSoft"); l.Viz = GlProgram.Loc(prog, "uViz");
        l.Cover = GlProgram.Loc(prog, "uCover"); l.CoverVp = GlProgram.Loc(prog, "uCoverVp"); l.CoverLayer = GlProgram.Loc(prog, "uCoverLayer"); l.CoverSoft = GlProgram.Loc(prog, "uCoverSoft");
        l.Lit = GlProgram.Loc(prog, "uLit"); l.Back = GlProgram.Loc(prog, "uBack");
        (snow ? ref _litSnow : ref _litRain) = Programs.LitLocs(prog);
        return true;
    }

    internal static void ContextLost() { _progRain = 0; _progSnow = 0; _vao = 0; EsmLost(); }
    internal static void ResetPeriod() => _drawn = 0;
    internal static string Stats() => _off ? "weather unavailable" : !Enabled.V ? "weather off"
        : $"weather {CurrentKind} {Density * 100f:F0}% {_count} instances, {_drips} drips{CoverStats()}{SurfaceStats()}, records {AlloyRenderer.RecCreature} creature / {AlloyRenderer.RecObject} object / {AlloyRenderer.RecTile} tile";
    private static string DescribeDraw() => $" fall ({_fall.X:F2} {_fall.Y:F2} {_fall.Z:F2}) count {_count} drips {_drips}{CoverStats()}";

    /// <summary>One instanced quad per drop or flake; every position is a function of the instance id, the
    /// camera and the fall phase, so a hak override of this stage can reshape the field but never grow a
    /// buffer. The instance's lattice point is tested against the rain occlusion map (#617) once, here, and a
    /// covered instance is sent off-screen; a drip (kind 3) is kept only under a lip (#620).
    /// <para>The light is taken here, once per vertex at the instance's own centre (the particle pass's
    /// #336 shape): ambient with the sun-facing floor, the sun through its cascades, the clustered lamps.
    /// <c>uLit = 0</c> (rig down, lighting off) falls back to the flat area terms.</para></summary>
    internal static string WeatherVs => WeatherVsHead + AlloyLights.LocalShadowLib + AlloyLights.FragmentLib + AlloyLights.SunShadowLib + WeatherVsMain;

    private const string WeatherVsHead = """
        #version 460 core
        uniform mat4 uView;
        uniform mat4 uProj;
        uniform vec3 uEye;
        uniform vec3 uCentre;    // the field's centre: the camera's follow target
        uniform vec3 uFall;      // unit fall direction: gravity leaning into the area wind
        uniform float uPhase;    // metres fallen, wrapped at 1024 box lengths
        uniform float uTime;     // wobble clock, seconds
        uniform vec2 uBox;       // (width across the fall axis, length along it)
        uniform vec2 uSize;      // (extent along the fall axis, width) of one quad
        uniform int uKind;       // 1 rain, 2 snow, 3 roof drips
        uniform float uDrift;    // snow wobble amplitude, metres
        uniform sampler2DArrayShadow uCover;   // #617: the sun array; the cover map is layer uCoverLayer
        uniform mat4 uCoverVp;   // world -> the cover view's clip space; zero matrix = no map
        uniform float uCoverLayer;
        uniform float uCoverSoft;    // metres of feather at a cover edge
        uniform int uLit;        // 1 = take the light rig; 0 = flat area terms
        uniform float uBack;     // snow: light from behind the flake glows through it
        uniform vec3 uAreaAmbient;
        uniform vec3 uAreaDiffuse;
        uniform vec3 uAreaDir;
        out vec2 vUV;
        out float vViewZ;
        out float vSeed;
        out vec3 vLight;         // linear light at this drop or flake

        vec2 cxClusterXY;
        #define CX_CLUSTER_XY cxClusterXY

        """;

    private const string WeatherVsMain = """

        vec3 CxHash3(uint n)
        {
            n ^= n << 13u; n ^= n >> 17u; n ^= n << 5u;
            uvec3 k = uvec3(n, n * 16807u, n * 48271u);
            k ^= k >> 16u; k *= 0x7feb352du; k ^= k >> 15u; k *= 0x846ca68bu; k ^= k >> 16u;
            return vec3(k) / 4294967296.0;
        }

        // 1 = open sky above this point along the fall vector, 0 = covered. No map = open.
        float CxCovered(vec3 p)
        {
            if (uCoverVp[3][3] == 0.0) return 1.0;
            vec4 c = uCoverVp * vec4(p, 1.0);
            vec3 n = c.xyz / c.w * 0.5 + 0.5;
            if (any(lessThan(n.xy, vec2(0.0))) || any(greaterThan(n.xy, vec2(1.0)))) return 1.0;
            return texture(uCover, vec4(n.xy, uCoverLayer, n.z - 0.002));
        }

        // 1 = under a lip: covered here, open somewhere within the feather distance. Mid-roof and open sky are 0.
        float CxLip(vec3 P, vec3 R, vec3 U)
        {
            float e = max(uCoverSoft, 0.15);
            if (CxCovered(P) >= 0.5) return 0.0;
            return max(max(CxCovered(P + R * e), CxCovered(P - R * e)), max(CxCovered(P + U * e), CxCovered(P - U * e)));
        }

        void main()
        {
            uint id = uint(gl_InstanceID);
            vec3 h = CxHash3(id * 2654435761u + 1u);
            vec3 F = uFall;
            vec3 R = normalize(cross(F, abs(F.z) < 0.99 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0)));
            vec3 U = cross(R, F);
            vec3 size = vec3(uBox.x, uBox.x, uBox.y);
            float speed = 0.75 + float(id & 3u) * 0.125;    // eighths: continuous at the phase wrap
            vec3 home = h * size;
            home.z = mod(home.z + uPhase * speed, size.z);
            vec3 c = vec3(dot(uCentre, R), dot(uCentre, U), dot(uCentre, F));
            vec3 q = home + size * floor((c - home) / size + 0.5);
            if (uKind == 2) q.xy += vec2(sin(uTime + h.y * 6.2832), cos(uTime * 1.5 + h.z * 6.2832)) * uDrift;
            vec3 P = R * q.x + U * q.y + F * q.z;
            vec2 corner = vec2(float((gl_VertexID & 1) * 2 - 1), float((gl_VertexID >> 1) * 2 - 1));
            vec3 toCam = uEye - P;
            vec3 axis, side;
            if (uKind != 2) { axis = F; side = normalize(cross(F, toCam)); }
            else { side = normalize(cross(vec3(0.0, 0.0, 1.0), toCam)); axis = normalize(cross(toCam, side)); }
            float len = uKind != 2 ? uSize.x * (0.5 + 0.8 * h.y) : uSize.x;
            vec3 pos = P + axis * (corner.y * len * 0.5) + side * (corner.x * uSize.y * 0.5);
            vUV = corner;
            // brightness: hashed 0.3..1.0
            vSeed = 0.3 + 0.7 * fract(h.x * 7.31);
            // no edge: the field thins RADIALLY from 40% of the half-width out, by drop COUNT (a hashed keep)
            // and not by a brightness ramp -- a ramp on a square box read as a band with a corner in it,
            // fewer drops reads as distance
            vec2 rel = (q.xy - c.xy) / (size.xy * 0.5);
            float edge = 1.0 - smoothstep(0.4, 1.0, length(rel));
            vec4 vp = uView * vec4(pos, 1.0);
            vViewZ = vp.z;
            // the cover test at a hashed offset in the fall-normal plane: wind and splash feather a roof's edge
            vec3 Pc = P + (R * (h.y - 0.5) + U * (h.z - 0.5)) * uCoverSoft * 2.0;
            float shown = fract(h.z * 91.7) > edge ? 0.0 : uKind == 3 ? CxLip(P, R, U) : CxCovered(Pc);
            gl_Position = shown < 0.5 ? vec4(2.0, 2.0, 2.0, 1.0) : uProj * vp;
            // a drop has no surface: the camera-facing direction stands in for its normal, so a lamp behind the
            // camera lights it and one behind the drop does not -- the cheapest read of "wet glints towards you"
            vLight = uAreaAmbient + uAreaDiffuse * (uKind == 2 ? 1.0 : 0.5);
            if (uLit != 0 && shown >= 0.5)
            {
                vec3 N = normalize(toCam);
                vec4 cv = uView * vec4(P, 1.0);
                vec4 cc = uProj * cv;
                cxClusterXY = (cc.xy / max(cc.w, 1e-3) * 0.5 + 0.5) * vec2(uGrid.xy);
                float ndl = clamp(dot(N, uAreaDir), 0.0, 1.0);
                vec3 sun = uAreaDiffuse * (ndl * CxSunShadow(P, N, ndl, ndl));
                vec3 point = CxClusteredDiffuse(P, N, max(-cv.z, 1e-3));
                vLight = uAreaAmbient * mix(0.15, 1.0, ndl) + sun + point;
                if (uBack > 0.0)
                {
                    float back = pow(max(dot(-N, uAreaDir), 0.0), 4.0);
                    vLight += uBack * (uAreaDiffuse * back * CxSunShadow(P, -N, back, back) + CxClusteredDiffuse(P, -N, max(-cv.z, 1e-3)));
                }
            }
        }
        """;

    /// <summary>A rain streak: a thin bright core tapered at both ends, faded where it meets geometry.
    /// Linear, alpha over. <c>/weather viz 1</c> paints the contact fade alone. The colour is the vertex stage's light times the brightness scalar.</summary>
    internal const string RainFs = """
        #version 460 core
        in vec2 vUV;
        in float vViewZ;
        in float vSeed;
        in vec3 vLight;
        uniform vec3 uTint;
        uniform float uAlpha;
        uniform float uSoft;

        """ + AlloyRenderer.SceneDepthLib + """
        void main()
        {
            float a = (1.0 - vUV.x * vUV.x) * (1.0 - vUV.y * vUV.y);
            a = a * a;
            if (a <= 0.01) discard;
            float gap = CxSceneViewZ(gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0))) + vViewZ;
            float k = clamp(gap * uSoft, 0.0, 1.0);
            if (uViz == 1) { frag = vec4(vec3(k), 1.0); return; }
            frag = vec4(uTint * vLight, a * k * k * uAlpha * vSeed);
        }
        """;

    /// <summary>A snowflake: a soft white disc, faded where it meets geometry. Linear, alpha over.</summary>
    internal const string SnowFs = """
        #version 460 core
        in vec2 vUV;
        in float vViewZ;
        in float vSeed;
        in vec3 vLight;
        uniform vec3 uTint;
        uniform float uAlpha;
        uniform float uSoft;

        """ + AlloyRenderer.SceneDepthLib + """
        void main()
        {
            float r = length(vUV);
            if (r >= 1.0) discard;
            float a = smoothstep(1.0, 0.5, r);
            float gap = CxSceneViewZ(gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0))) + vViewZ;
            float k = clamp(gap * uSoft, 0.0, 1.0);
            if (uViz == 1) { frag = vec4(vec3(k), 1.0); return; }
            frag = vec4(uTint * vLight, a * k * k * uAlpha * (0.5 + 0.5 * vSeed));
        }
        """;
}
