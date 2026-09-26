using System;
using System.Globalization;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// #83 (map #68, spec #77 M1, frame graph #75): the clustered forward+ light rig for the Alloy world pass.
/// The Alloy light walk's whole snapshot goes into an SSBO -- no 32-light UBO, no uniform-array ceiling --
/// and a GPU compute pass bins it into a froxel grid that the Alloy lit shader indexes per fragment. Lights
/// past the engine's 32-light budget therefore light Alloy geometry with nothing to pop in or out of.
/// <para>Everything here runs inside <see cref="GlContextService"/>'s 4.6 bracket, driven by
/// <see cref="AlloyRenderer"/>: <see cref="Upload"/> once per frame (the snapshot is per-frame), then
/// <see cref="Build"/> once per pass (main view and each reflection subframe have their own projection, so
/// the froxel bounds differ). Hot and alloc-free -- the staging array is static and the only per-frame CPU
/// work is a memcpy-shaped fill.</para>
/// <para>Costs that are bounded rather than unbounded are COUNTED, never silently truncated: a cluster past
/// <see cref="ClusterCap"/> lights bumps a GPU-side counter that the period log reports, and the build's GPU
/// time comes from a two-deep timer query read a frame late (never a blocking readback).</para>
/// </summary>
internal static unsafe class AlloyLights
{
    /// <summary>Froxel grid. #151: 32x18 tiles (~80px at 1440p), 24 exponential depth slices. The shipped
    /// 16x9 put a 159px tile over a torch-lit plaza with 254 lights, and the per-fragment cost of clustered
    /// shading is the LIGHT LIST LENGTH of the fragment's tile -- the GPU timer read the colour passes at
    /// 12-14ms there. Quartering the tile area roughly quarters how many torches share one list. The build
    /// stays O(clusters * lights) on compute (measured 0,061ms at 3456 clusters; 13824 is still nothing) and
    /// the index SSBO grows 884KB -> 3,5MB, sized once. More clusters = SHORTER per-fragment loops; this
    /// knob, not <see cref="ClusterCap"/>, is the one dense lighting wants.</summary>
    private const int GridX = 32, GridY = 18, GridZ = 24;
    private const int ClusterCount = GridX * GridY * GridZ;
    /// <summary>Lights binned per cluster. A froxel is ~1/144th of the screen over one depth slice; 64 fired
    /// (242 overflows/period in a torch-dense Arelith area, 2026-08-31), so 128. NOT free: the fragment
    /// loop reads the cluster's COUNT, and the count was clamped BY the old cap -- restoring the dropped
    /// lights is the point, and in a formerly-overflowing froxel it costs up to 2x the per-fragment loop,
    /// plus SSBO bytes (3,5MB -> 7MB). <para>ponytail: fixed capacity, no per-cluster compaction pass. Overflow is counted and
    /// logged; if 128 ever fires, the upgrade is a two-pass count-then-fill build with a compacted offset
    /// table.</para></summary>
    /// <summary>Boot-tunable (<c>alloy.clustercap</c>, #402/#404): it sizes the cluster build's per-lane sort
    /// array, the scratch candidate for the radeonsi fault; 128 is the shipped value.</summary>
    internal static readonly int ClusterCap = Renderer.ClusterCap.Boot;
    /// <summary>#410: the cluster build's per-lane sort array size, deliberately NOT <see cref="ClusterCap"/>.
    /// A dynamically-indexed private array past ~64 entries spills to scratch (32 KB/wave at 128), and on
    /// radeonsi that scratch read faulted on area entry (#401). <c>bincap</c> never indexes past 16; 32 is
    /// clear of the spill threshold. Compile-time shape, not a knob.</summary>
    internal const int SortCap = 32;
    /// <summary>Compute workgroup size; the dispatch is 1D over clusters and decodes (x,y,z) from the id.</summary>
    internal const int GroupSize = 64;
    /// <summary>SSBO binding points, shared by the compute and the lit fragment program.</summary>
    private const uint BindLights = 0, BindCounts = 1, BindIndices = 2, BindStats = 3, BindMovers = 25;
    /// <summary>Point-light attenuation, as the ENGINE computes it (#140). Verified against
    /// <c>inc_lighting.shd</c>'s <c>ComputePointLightSource</c> at <c>GAMMA_CORRECTION == 1</c> and against
    /// the two constants <c>GLRender::SetUniforms</c> @0x4cd341 feeds it:
    /// <code>
    ///     f2  = d^2 / R^2                        (R = GetLightAdjustedRadius, which IS what we upload)
    ///     att = (1 - f2) / (mi + ff * f2)
    /// </code>
    /// <para>The curve is range-NORMALISED, so the authored range is the shape: a dim decorative light stays
    /// dim because its colour is dim and its R is small; a torch reaches because its R is large. That is the
    /// behaviour the content was authored against, and why nothing here needs a global gain to compensate --
    /// an absolute <c>1/d^n</c> model is range-blind and serves neither the 2m cave glow nor the torch.</para>
    /// <para><see cref="_gain"/> scales the whole curve (1 = the engine's own peak, <c>1/mi</c>);
    /// <see cref="_falloffPow"/> is an exponent on the <c>(1 - f2)</c> rim fade, 1.0 = vanilla exactly.
    /// <see cref="_srcRadius"/> is not part of this curve: it is the sphere-light wrap radius and the base of
    /// the self-shadow near-field skip (#139).</para>
    /// </summary>
    private static float _gain => Lighting.Gain.V;
    private static float _srcRadius => Lighting.Radius.V;
    private static float _falloffPow => Lighting.Falloff.V;

    /// <summary>#142: the near-field core, and the answer to "no light ever gets intense close up".
    /// <para>The engine's curve is a NEEDLE. With the live constants (mi 0.410, ff 136.334) the denominator
    /// is dominated by <c>ff * f2</c> past <c>f2 = mi/ff</c> -- three thousandths of the range squared, so
    /// about 5% of the range -- and everything beyond that sits on the 1/d^2 tail. A gain low enough to put
    /// the body of that curve at vanilla's level therefore scales the core away with it, and every light
    /// reads flat. <c>x</c> adds the core back without touching the body.</para>
    /// <para>Shape: a COMPACT-SUPPORT bump, <c>(1 - f2/y^2)^2</c>, peaking at <c>x / mi</c> and exactly zero
    /// past <c>y</c> of the light's range. Deliberately not a second hyperbola -- two 1/d^2 terms stay in
    /// fixed proportion at every distance, so widening the core would lift the far field with it, and the
    /// far field is the half the live A/B against vanilla actually calibrated. Compact support makes "the
    /// tail is untouched" a property of the formula rather than a thing to re-check.</para>
    /// <para><c>gain + hot = 1</c> puts the very centre at the engine's own peak while <see cref="_gain"/>
    /// alone owns everything past the core. The shipped pair is gain 1 / hot 0 -- the engine's curve, no
    /// core term at all. The old 0.10/0.90 pair came from a live A/B whose "vanilla" side was the SoW
    /// server's veil shader, which deliberately darkens around the player to fake missing darkvision; it was
    /// calibrating against a darkened frame, not against vanilla, and every placed light paid for it.</para>
    /// <para>0 disables it, which is #140's curve and is also what a content override (#92) that never
    /// declares <c>uHot</c> reads.</para></summary>
    private static float _hot => Lighting.Hot.V;
    private static float _hotSize => Lighting.HotSize.V;

    /// <summary>The engine's own two attenuation constants, latched per capture from
    /// <c>GLRender::m_globalShaderConstantsf</c> through <c>RenderInterface::GetGlobalShaderParamf</c> --
    /// read rather than hardcoded, so a graphics-settings change moves the owned curve with the engine's.
    /// <list type="bullet">
    /// <item><c>x = lightMaxIntensityInv = param(0x14)^-2.2</c>. Its reciprocal is the peak: the
    /// attenuation every light reaches at its own centre.</item>
    /// <item><c>y = lightFalloffFactor = param(0x12)^2 * (param(0x13)^-2.2 - param(0x14)^-2.2)</c>, where
    /// 0x12 is the light-radius multiplier and 0x13/0x14 are the min/max intensity settings. Without the
    /// rim fade the hyperbola runs from <c>param(0x14)^2.2</c> at the centre to <c>param(0x13)^2.2</c> at
    /// the range; the fade then takes it to exactly 0 there.</item>
    /// </list>
    /// The fallback is a peak of 1 falling to 0 at the range -- sane, not a guess at the engine's numbers,
    /// and only ever seen before the render interface is up.</summary>
    private static Vector2 _atten = new(1f, 1f);

    /// <summary>Latch the engine's attenuation constants from the three global shader params behind them.
    /// Called by the light feed, which already reads 0x12 for the radius.</summary>
    internal static void LatchAtten(float minIntensity, float maxIntensity, float cutoffMult)
    {
        if (!(minIntensity > 0f) || !(maxIntensity > 0f) || !(cutoffMult > 0f)) return;
        float mi = MathF.Pow(maxIntensity, -SrgbGamma);
        float ff = cutoffMult * cutoffMult * (MathF.Pow(minIntensity, -SrgbGamma) - mi);
        // A negative falloff factor would make the denominator shrink with distance -- a light that gets
        // BRIGHTER as it fades. It cannot happen with min <= max, so a settings state that produces it is
        // one to leave the last good pair standing for, not to render.
        if (!(mi > 0f) || ff < 0f || float.IsNaN(ff)) return;
        _atten = new Vector2(mi, ff);
    }

    /// <summary>What the shader is running, for the period log and the /light echo.</summary>
    internal static Vector2 Atten => _atten;
    /// <summary>#139 sphere-light wrap: the light is treated as a SPHERE of radius <see cref="_srcRadius"/>
    /// times this, and the diffuse terminator is softened by the half-angle that sphere subtends at the
    /// fragment. 0 disables it and restores the punctual <c>N.L</c>.
    /// <para>This is the fix for the second half of the torch complaint: at arm's length a punctual light
    /// swings N.L across a whole low-poly facet in one polygon, which reads as the model being cut in half.
    /// The wrap converges to plain N.L with distance, so placed lights are untouched.</para></summary>
    private static float _wrap => Lighting.Wrap.V;
    /// <summary>#143 wrapped diffuse: the distance-INDEPENDENT terminator softening, applied to the sun and
    /// as a floor under <see cref="_wrap"/> on every point light. 0 restores the punctual N.L everywhere.
    /// <para>This is what the community's GL 3.3 "light wrapping" shaders do, and the reason they exist:
    /// NWN's meshes are low-poly with smoothed vertex normals, so the terminator crosses a whole facet as
    /// one straight hard crease. Unlike <see cref="_wrap"/> it does NOT fall off with distance -- the
    /// facets are the same size however far away the light is. Ships at 1 (was 0.25, imperceptible live);
    /// it widens the terminator band only -- a per-face normal jump is <see cref="NormalWeld"/>'s.</para></summary>
    private static float _wrapN => Lighting.WrapNdl.V;
    /// <summary>#375 follow-up: the stock SUBSURFACE term's wrap. sow-topdata's inc_lighting.shd at
    /// SHADER_QUALITY_MODE &gt; 1 adds, per light and on top of the Lambert term,
    /// <c>L * att * max(0, dot(Nvertex, L) + w) * 2/(2 + PI*w)</c> (LIGHT_SUBSURFACE_SCATTERING_WRAP 0.38,
    /// fEnergyPreservation / (1 + w)): +86% at normal incidence, more at grazing angles -- the "Alloy is
    /// 2x darker than vanilla at gamma 2.2" gap, which no gamma stage could close. Ships at 0.25, not the
    /// content's 0.38: at the stock wrap the term lights faces past the terminator brightly enough that a
    /// character reads as glossy in the dark (measured live, gone at 0.25). Alloy puts the wrap on the MAPPED
    /// normal, not stock's vertex normal: on the vertex normal it is a wash independent of the normal map,
    /// which halves the map's diffuse contrast ("PBR is gone", live). 0 is Lambert alone. Gated live
    /// on global int param 7, the client's shader-quality option, exactly where the stock preamble gates
    /// it.</summary>
    private static float _sss => Lighting.Sss.V;
    /// <summary><c>GetGlobalShaderParami(7)</c> = the preamble's <c>SHADER_QUALITY_MODE</c>
    /// (LoadAndCompileSingleShader @0x4d34b0); the subsurface term compiles in above 1.</summary>
    private const uint QualityParam = 7;
    private static RiGetGlobalShaderParamiFn _gspI;
    /// <summary>The stock energy factor for the subsurface term at wrap <paramref name="w"/>:
    /// fEnergyPreservation / (1 + w) = 2 / (2 + PI * w).</summary>
    internal static float SssK(float w) => 2f / (2f + MathF.PI * w);
    private static bool SssOn => _sss > 0f && (_gspI == null || _gspI(QualityParam) > 1);
    /// <summary>#139: local-cube PCF radius, in cube texels at the sample distance. 0 = the single hardware
    /// PCF tap this shipped with; anything above widens the filter with four more taps on a disc
    /// perpendicular to the sample ray. Costs 4 extra cube fetches per shadowing light per fragment.</summary>
    private static float _shadowSoft => Shadows.Soft.V;
    /// <summary>#326: the PCF disc's radius on SCREEN, in pixels, below which it stands down to the single
    /// hardware tap. #325 measured the cube taps as the colour pass's one big term and the disc is four of
    /// every five; its width scales with distance to the LIGHT, so the test is per fragment, not a view-depth
    /// line. 0 = the disc everywhere. ponytail: a hard threshold, no fade band across it.</summary>
    private static float _discPx => Shadows.DiscPx.V;
    /// <summary>#139: self-shadow near-field skip, as a multiple of <see cref="_srcRadius"/>. A world point
    /// closer to the light than this is reported lit outright, on creature (mover) fragments only -- a carried torch sits INSIDE the hand and
    /// forearm holding it, and a punctual shadow test there shadows the carrier from its own light.</summary>
    private static float _shadowNear => Shadows.Near.V;
    /// <summary>Scales on the captured area terms, so daylight and interior fill can be dialled apart from
    /// the point lights. 1.0 = the engine's own captured value, untouched.</summary>
    internal static float AmbientScale => Lighting.Ambient.V;
    internal static float SunScale => Lighting.Sun.V;
    /// <summary>The daylight ratio's lamp side, <c>uDayDim</c>: 1 / k, where k eases from 1 to <c>light.daylight</c>
    /// as the area directional's linear luminance crosses <c>daylow</c>..<c>dayhigh</c>. Every lamp term (live,
    /// baked, halo) takes it, so the sun outweighs lamps by day with no rebake and no reliance on HDR or
    /// auto-exposure. An interior has no sun: 1.</summary>
    private static float SunLum
    {
        get { Vector3 d = FrameInputs.AreaDiffuse; return 0.2126f * d.X + 0.7152f * d.Y + 0.0722f * d.Z; }
    }

    /// <summary>The <c>light</c> group's status: the sun luminance <see cref="DayDim"/> reads against
    /// <c>daylow</c>/<c>dayhigh</c>, and the lamp scale it gives -- the numbers the thresholds are tuned from.</summary>
    internal static string DayStatus() =>
        $"sun luminance {SunLum:F3} (daylow {Lighting.DayLow.V:F2}, dayhigh {Lighting.DayHigh.V:F2}) -> lamps x{DayDim:F2}" +
        (FrameInputs.Interior ? " [interior: never dimmed]" : !FrameInputs.HasAreaDiffuse ? " [no area sun read yet]" : "") +
        $" | diffuse {FrameInputs.AreaDiffuse.X:F3},{FrameInputs.AreaDiffuse.Y:F3},{FrameInputs.AreaDiffuse.Z:F3}" +
        $" ambient {FrameInputs.AreaAmbient.X:F3},{FrameInputs.AreaAmbient.Y:F3},{FrameInputs.AreaAmbient.Z:F3}";

    internal static float DayDim
    {
        get
        {
            if (FrameInputs.Interior || !FrameInputs.HasAreaDiffuse) return 1f;
            float lum = SunLum;
            float lo = Lighting.DayLow.V, hi = MathF.Max(Lighting.DayHigh.V, lo + 1e-3f);
            float t = Math.Clamp((lum - lo) / (hi - lo), 0f, 1f);
            return 1f / (1f + (Lighting.Daylight.V - 1f) * t * t * (3f - 2f * t));
        }
    }
    /// <summary>How much of an OVERBRIGHT light's surplus is kept as CENTRE INTENSITY, on top of the reach
    /// the engine already spent it on. Applied by the feed as <c>1 + (maxChannel - 1) * this</c>.
    /// <para>Overbright IS the carried-light discriminator and needs no other: module-placed lights are
    /// authored at max channel &lt;= 1 and have no surplus at all, while a torch ships at
    /// rgb=(1.75, 1.50, 1.00). That matters because seeing is a mechanical advantage -- a torch or a Light
    /// spell has to be worth carrying -- and placed lights are mostly decor.</para>
    /// <para>0 = the engine's own behaviour exactly, and what ships: <c>GetLightAdjustedColor</c> normalises
    /// the hue and <c>GetLightAdjustedRadius</c> spends the whole surplus on radius, so an overbright light
    /// is WIDER and no brighter. 1 = the authored surplus in full at the centre as well. It shipped nonzero
    /// only to lift carried lights out of a scene the 0.10 gain had flattened; with the gain back at the
    /// engine's peak there is nothing to lift them out of, so a carried light is vanilla too. <c>/light
    /// overbright 0.5</c> brings the taste value back.</para>
    /// <para>#140 brought this down from 120. That number was not a lighting decision -- it was propping up
    /// a range-blind <c>1/d^4</c> curve under which no honest value reached anything, and it is what made a
    /// torch-bearer 210x brighter than any placed light at the same distance. The curve is fixed; the cope
    /// goes with it.</para></summary>
    internal static float OverbrightScale => Lighting.Overbright.V;

    /// <summary>The atten pair and the shader-quality gate, for the arm-time log.</summary>
    internal static string DescribeAtten() =>
        $"atten(mi {_atten.X:F3} ff {_atten.Y:F3}, peak {(_atten.X > 0f ? 1f / _atten.X : 0f):F2})  sss {_sss:F2}{(SssOn ? "" : " (off: quality<2)")}";

    /// <summary>Headless check of the lighting maths: the subsurface term is the stock one, the attenuation
    /// constants latch the way the engine computes them, and the curve has the shape the receiver assumes.
    /// Runs off-device; restores whatever it touched.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        // The shader's curve, in C#, so the property checks below are about the SHAPE and not about a
        // second transcription of it: f2 in [0, 1], gain 1, rim-fade exponent 1.
        float Att(float f2, float gain) => gain * (1f - f2) / (_atten.X + _atten.Y * f2);
        var check = new Checker(log);
        // The subsurface term is the stock one or it is nothing: the energy factor is inc_lighting.shd's
        // fEnergyPreservation / (1 + w) and the wrap rides the MAPPED normal (stock: the vertex normal, which washes out normal maps).
        check.Expect(MathF.Abs(SssK(0.38f) - 0.6262f) < 1e-3f, "SssK(0.38) is 2/(2+PI*0.38) = 0.626");
        check.Expect(FragmentLib.Contains("c * (att * max(ndlRaw + uSss.x, 0.0) * uSss.y)"), "the point loop carries the subsurface term on the MAPPED normal");
        check.Expect(FragmentLib.Contains("if (mirror) { spec += spc; continue; }")
          && FragmentLib.Contains("if (mSlot >= 0 && mSlot != aSlot && mSlot != bSlot)") && FragmentLib.Contains("if (l > mLum) { mLum = l; mSlot = slot; mNdl = ndlRaw; mSpc = spc; }")
          && AlloyRenderer.OwnedLitFsMain.Contains("if (!CxGlossy(rough)) pointSpec += lm * CxLoopSpec(CxSpecLobe"),
          "a glossy lobe carries its light's shadow whatever the cap says, in the pages' place too");
        check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("max(dot(N, uAreaDir) + uSss.x, 0.0) * uSss.y"), "...and so does the sun");

        // #140: the engine's attenuation constants, and the property that actually matters -- the curve's
        // peak is the engine's own max-intensity setting in LINEAR space. Get the sign of the exponent
        // wrong and the peak inverts into a near-zero, which renders as "the lights all went out" with
        // nothing in the log to say why.
        Vector2 savedAtten = _atten;
        LatchAtten(0.5f, 1.5f, 1f);
        float peak = _atten.X > 0f ? 1f / _atten.X : 0f;
        check.Expect(MathF.Abs(peak - MathF.Pow(1.5f, SrgbGamma)) < 1e-3f, "the curve's peak is param(0x14) linearised");
        check.Expect(MathF.Abs(_atten.Y - (MathF.Pow(0.5f, -SrgbGamma) - MathF.Pow(1.5f, -SrgbGamma))) < 1e-3f,
          "lightFalloffFactor is cutoff^2 * (min^-2.2 - max^-2.2)");
        // ...and at the range the curve is exactly 0, whatever the constants: that is what keeps a light
        // from popping against its own cull sphere.
        check.Expect(MathF.Abs(Att(1f, 1f)) < 1e-6f, "attenuation is exactly 0 at the authored range");
        check.Expect(Att(0f, 1f) > Att(0.5f, 1f) && Att(0.5f, 1f) > Att(0.9f, 1f), "...and monotonic on the way there");
        // The cutoff multiplier is squared into ff and NOT into mi: it moves the rim, never the peak.
        Vector2 narrow = _atten;
        LatchAtten(0.5f, 1.5f, 2f);
        Vector2 wide = _atten;
        check.Expect(MathF.Abs(wide.X - narrow.X) < 1e-6f, "the radius multiplier does not move the peak");
        check.Expect(MathF.Abs(wide.Y - narrow.Y * 4f) < 1e-3f, "...and enters the falloff factor squared");
        LatchAtten(0f, 1.5f, 1f);
        check.Expect(_atten == wide, "a zero intensity setting leaves the last good pair standing");
        LatchAtten(2f, 1f, 1f);
        check.Expect(_atten == wide, "...and so does a min above max, which would invert the falloff");
        _atten = savedAtten;
        log.Info(check.Ok ? "AlloyLights.SelfCheck: passed" : "AlloyLights.SelfCheck: FAILED");
        return check.Ok;
    }
    /// <summary>What the shader is actually running, for the period log -- read where it is USED, not at
    /// arm time (the engine's globals are still zero then, which is how a live-looking log line reported a
    /// value the frame never saw).</summary>
    internal static Vector3 LastFalloff;
    /// <summary>The curve, for the [alloy] period line. Names the engine's constants and the two dials that
    /// shape what is drawn from them -- the pre-#140 text described a 1/d^n plateau that no longer exists.</summary>
    internal static string DescribeCurve() =>
        $"atten (1-f2)/({_atten.X:F3}+{_atten.Y:F3}*f2) body x{LastFalloff.X:F2} " +
        $"core +x{_hot:F2} over {_hotSize:P0} of range";
    /// <summary>Slice-0 depth floor. The engine's near plane can be tiny, and log(d/near) over a near of 0.01
    /// wastes half the slices on the first metre.</summary>
    private const float MinNear = 0.1f;

    private static ExtenderLog? _log;
    private static uint _prog, _bufLights, _bufCounts, _bufIndices, _bufStats, _bufMovers;
    private static int _moversGen = -1;
    private static bool _failed;
    private static int _locView = -1, _locProjXY = -1, _locZ = -1, _locCount = -1, _locGrid = -1, _locReach = -1, _locBinCap = -1, _locBinAtten = -1;
    private static readonly uint[] _query = new uint[2];
    private static bool _queryPending0, _queryPending1;
    private static int _queryAt;

    /// <summary>Light staging: two vec4 per light (pos+range, colour+pad), filled straight from the walk
    /// snapshot. #257: grown to whatever the snapshot holds and kept, so the per-frame upload allocates
    /// nothing in the steady state without a worst-case array standing behind it.</summary>
    private static float[] _stage = new float[InitialLights * 8];
    /// <summary>Starting light capacity of the staging buffer and the SSBO. Not a ceiling.</summary>
    private const int InitialLights = 256;
    /// <summary>Lights the SSBO is currently sized for; a denser area re-allocates it once.</summary>
    private static int _ssboLights;
    private static int _uploaded;
    private static int _uploadFrame = -1;

    /// <summary>Period counters, read by the Alloy draw log line.</summary>
    internal static int LastLightCount;
    internal static float LastBuildMs;

    /// <summary>True once the rig is usable: compute + SSBO entry points resolved and the program linked.</summary>
    internal static bool Ready => _prog != 0 && !_failed;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _gspI = EngineFn.RiGetGlobalShaderParami;
        log.Info($"AlloyLights: {DescribeAtten()} -- /light retunes every dial live");
    }

    /// <summary>Drop every GL object (context loss / disarm). Safe to call outside the bracket only when the
    /// context is already gone -- the ids are then meaningless anyway.</summary>
    internal static void Forget()
    {
        _prog = 0; _bufLights = 0; _bufCounts = 0; _bufIndices = 0; _bufStats = 0; _bufMovers = 0; _moversGen = -1;
        _query[0] = 0; _query[1] = 0; _queryPending0 = false; _queryPending1 = false;
        _uploadFrame = -1; _uploaded = 0;
    }

    /// <summary>Fill + upload the light SSBO for this frame (idempotent within a frame: reflection subframes
    /// reuse the main frame's snapshot). Colours stay as the walk stored them -- the shader linearizes,
    /// exactly like the staging receiver does.</summary>
    internal static void Upload(int frame)
    {
        if (!Ensure()) return;
        if (_uploadFrame == frame) return;
        _uploadFrame = frame;
        int n = FrameInputs.LightCount;
        if (n * 8 > _stage.Length) _stage = new float[n * 8];
        if (n > _ssboLights) GrowLightBuffer(n);
        float rmin = float.MaxValue, rmax = 0f, rsum = 0f;
        fixed (float* s = _stage)
        {
            for (int i = 0; i < n; i++)
            {
                Vector4 pr = FrameInputs.AreaLightPosRange[i];
                Vector3 c = FrameInputs.AreaLightColor[i];
                rmin = MathF.Min(rmin, pr.W); rmax = MathF.Max(rmax, pr.W); rsum += pr.W;
                float* d = s + i * 8;
                d[0] = pr.X; d[1] = pr.Y; d[2] = pr.Z; d[3] = pr.W;
                d[4] = c.X; d[5] = c.Y; d[6] = c.Z;
                // the record's spare w is this light's shadow-cube slot (#95), -1 for a light with no cube.
                // Riding the existing pad is what keeps the cube receiver from needing a second buffer keyed
                // the same way -- and the walk already resolved the slot by light identity.
                d[7] = FrameInputs.AreaLightAmbient[i] ? FrameInputs.AmbientSlot : FrameInputs.AreaLightSlot[i];
                // #674: the static bit rides the same lane, +CX_LIGHT_STATIC; the shader decodes w >= 128.
                if (FrameInputs.AreaLightStatic[i] && !FrameInputs.AreaLightAmbient[i]) d[7] += LightStatic;
            }
            Gl.NamedBufferSubData(_bufLights, IntPtr.Zero, (IntPtr)(n * 8 * sizeof(float)), s);
        }
        _uploaded = n;
        LastLightCount = n;
        _radMin = n > 0 ? rmin : 0f; _radMax = rmax; _radMean = n > 0 ? rsum / n : 0f;
    }

    /// <summary>Re-size the light SSBO for a denser area than any seen this session. Cold: an area load at
    /// worst, and never in a frame that did not need it. The contents are re-uploaded by the caller, which
    /// is why this only has to allocate.</summary>
    private static void GrowLightBuffer(int lights)
    {
        _ssboLights = lights;
        Gl.NamedBufferData(_bufLights, (IntPtr)((long)lights * 8 * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        _log?.Info($"AlloyLights: light SSBO grown to {lights} lights");
    }

    /// <summary>Bin this pass's lights into the froxel grid. <paramref name="view"/> is the engine's view
    /// matrix as raw bytes (row-major v*M, read column-major by GLSL = the transpose, so
    /// <c>uView * vec4(world,1)</c> is exact -- the same convention as the Alloy draw's uMVP);
    /// <paramref name="p00"/>/<paramref name="p11"/> are the projection's diagonal terms, which are
    /// transpose-invariant and therefore safe to read straight off the stack.</summary>
    /// <summary>Read the froxel grid's entire projection input off the projection matrix itself, so the depth
    /// slicing and the froxel bounds can never disagree about which pass they belong to. #257 made
    /// <c>FrameInputs.CamNear</c>/<c>CamFar</c> the SCENE camera's own, so they no longer belong to an
    /// auxiliary pass -- but this pass may be rendering a probe cube, whose projection is its own.
    /// <para>Raw bytes are row-major and GLSL reads them column-major, so the GLSL-visible element (col c,
    /// row r) is <c>raw[c*4+r]</c>. For a GL perspective projection <c>G[2][2] = -(f+n)/(f-n)</c>,
    /// <c>G[3][2] = -2fn/(f-n)</c> and <c>G[2][3] = -1</c>, which inverts to <c>n = B/(A-1)</c>,
    /// <c>f = B/(A+1)</c>. That last element is also the whole basis for the eye-space convention this rig
    /// assumes (forward is -z, so <c>clip.w == -eye.z</c> and the fragment side's <c>gl_Position.w</c> is a
    /// linear view depth): it is CHECKED here rather than assumed, and a projection that fails the check --
    /// orthographic, reversed-Z, +z-forward -- returns false and hands the pass back to the engine.</para></summary>
    internal static bool TryPerspective(float* proj, out float p00, out float p11, out float near, out float far)
    {
        p00 = proj[0]; p11 = proj[5];
        near = 0f; far = 0f;
        if (!(p00 > 0f) || !(p11 > 0f)) return false;
        if (MathF.Abs(proj[11] + 1f) > WRowEpsilon) return false;
        float a = proj[10], b = proj[14];
        float da = a - 1f, db = a + 1f;
        if (MathF.Abs(da) < 1e-6f || MathF.Abs(db) < 1e-6f) return false;
        near = b / da; far = b / db;
        return near > 0f && far > near;
    }

    /// <summary>Tolerance on the projection's w row. A perspective GL projection puts exactly -1 there; the
    /// slack only absorbs float noise, never a different projection class.</summary>
    private const float WRowEpsilon = 1e-4f;

    internal static bool Build(float* view, float p00, float p11, float near, float far)
    {
        // NOT gated on the light count: with zero point lights the dispatch still writes an all-zero cluster
        // table, which is exactly right -- the lit parts then shade on area ambient + sun alone instead of
        // being dropped from the frame for want of a torch.
        if (!Ensure()) return false;
        if (!(p00 > 0f) || !(p11 > 0f) || !(far > near)) return false;
        if (near < MinNear) near = MinNear;
        if (far <= near) return false;

        int slot = _queryAt;
        bool timing = _query[slot] != 0 && !(slot == 0 ? _queryPending0 : _queryPending1);
        if (timing) Gl.BeginQuery(Gl.TIME_ELAPSED, _query[slot]);

        Gl.UseProgram(_prog);
        Gl.Uniform3i(_locGrid, GridX, GridY, GridZ);
        Gl.UniformMatrix4fv(_locView, 1, 0, view);
        Gl.Uniform2f(_locProjXY, p00, p11);
        Gl.Uniform2f(_locZ, near, far);
        Gl.Uniform1i(_locCount, _uploaded);
        Gl.Uniform1f(_locReach, _binReach);
        Gl.Uniform1i(_locBinCap, _binCap);
        Gl.Uniform2f(_locBinAtten, _atten.X, _atten.Y);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindLights, _bufLights);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCounts, _bufCounts);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindIndices, _bufIndices);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindStats, _bufStats);
        Gl.DispatchCompute((ClusterCount + GroupSize - 1) / GroupSize, 1, 1);
        // BUFFER_UPDATE: TakeClusterOverflow reads _bufStats back with GetBufferSubData, which SHADER_STORAGE alone does not order (GL 4.6 7.13)
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);

        if (timing)
        {
            Gl.EndQuery(Gl.TIME_ELAPSED);
            if (slot == 0) _queryPending0 = true; else _queryPending1 = true;
            _queryAt = 1 - slot;
        }
        // harvest BOTH slots: a slot whose result is never collected stays pending forever and the timer
        // silently stops reporting
        HarvestQuery(0);
        HarvestQuery(1);
        return true;
    }

    /// <summary>Bind the cluster buffers for the lit draw (the compute pass leaves them bound, but a draw can
    /// follow a pass where the build was skipped).</summary>
    internal static void BindForDraw()
    {
        if (!Ready) return;
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindLights, _bufLights);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCounts, _bufCounts);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindIndices, _bufIndices);
        if (_moversGen != CasterPass.CubeDynSphereGen)
        {
            _moversGen = CasterPass.CubeDynSphereGen;
            fixed (float* sp = CasterPass.CubeDynSphere)
                Gl.NamedBufferSubData(_bufMovers, IntPtr.Zero, (IntPtr)(CasterPass.CubeDynSphere.Length * sizeof(float)), sp);
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindMovers, _bufMovers);
    }

    /// <summary>CX-43: the cluster lookup (grid, tile, depth range) and the light curve into the pass block,
    /// for the viewport and depth range this pass draws with. Each curve term is its own member, so a lane
    /// added here never changes what an older one means.</summary>
    internal static void FillDraw(byte* b, int* viewport, float near, float far)
    {
        if (near < MinNear) near = MinNear;
        *(Vector4*)(b + AlloyRenderer.PbTile) = new Vector4(viewport[0], viewport[1],
            MathF.Max((float)viewport[2] / GridX, 1f), MathF.Max((float)viewport[3] / GridY, 1f));
        int* grid = (int*)(b + AlloyRenderer.PbGrid);
        grid[0] = GridX; grid[1] = GridY; grid[2] = GridZ;
        *(float*)(b + AlloyRenderer.PbReach) = ReachUniform;
        LastFalloff = new Vector3(_gain, _srcRadius, _falloffPow);
        *(Vector3*)(b + AlloyRenderer.PbFalloff) = LastFalloff;
        *(float*)(b + AlloyRenderer.PbWrap) = _wrap;
        *(Vector2*)(b + AlloyRenderer.PbZ) = new Vector2(near, far > near ? far : near * 2f);
        *(Vector2*)(b + AlloyRenderer.PbAtten) = new Vector2(_atten.X, _atten.Y);
        *(Vector2*)(b + AlloyRenderer.PbHot) = new Vector2(_hot, MathF.Max(_hotSize, 1e-4f));
        *(Vector2*)(b + AlloyRenderer.PbSss) = new Vector2(_sss, SssOn ? SssK(_sss) : 0f);
        *(float*)(b + AlloyRenderer.PbWrapN) = _wrapN;
        *(float*)(b + AlloyRenderer.PbDayDim) = DayDim;
    }

    /// <summary>#139/#326: the local-cube receiver's three softness terms -- PCF radius in texels, the screen
    /// radius in pixels below which that disc stands down, and the world distance inside which a fragment is
    /// reported lit outright. Read here so <c>/shadow</c> retunes all three live, like every other knob.</summary>
    internal static float ShadowSoft => _shadowSoft;
    /// <summary>#641: <c>uLocalFloor</c> -- the shadow floor scaled by the area ambient's luminance (a mid-grey
    /// ambient gives the full floor, a night area a fraction of it, so darkness stays dark).
    /// ponytail: a luminance scalar, not a per-channel ambient tint; go to a vec3 if a coloured ambient ever reads wrong.</summary>
    internal static float ShadowFloor
    {
        get
        {
            var a = FrameInputs.HasAreaAmbient ? FrameInputs.AreaAmbient * AmbientScale : new Vector3(AlloyRenderer.DefaultAmbient);
            float lum = 0.2126f * a.X + 0.7152f * a.Y + 0.0722f * a.Z;
            return Shadows.Floor.V * Math.Clamp(2f * lum, 0f, 1f);
        }
    }
    internal static float ShadowDiscPx => _discPx;
    /// <summary>#642: the penumbra scale -- the light's source radius (<c>/light radius</c>), in world units.</summary>
    internal static float ShadowSource => _srcRadius;
    /// <summary>#336: cube-shadow taps a fragment may pay, in cluster order; 0 = every shadowing light.</summary>
    internal static float ShadowCap => Shadows.Cap.V;
    /// <summary><c>shadow.cubedist</c>: the view depth past which a fragment reads no cube (uLocalPcss.w; 0 = none).</summary>
    internal static float ShadowDist => Shadows.CubeDist.V;
    /// <summary>EXPERIMENT: the fraction of a light's adjusted radius the cluster build bins it by. The
    /// engine's curve is under 1/255 of its peak past ~0.8R, so lights binned by the full R sit in froxels
    /// they cannot visibly light; every one of them still costs the full per-light body per fragment.
    /// Read with <c>/alloy viz 38</c> (lights per pixel). 1 = the full radius.</summary>
    private static float _binReach => Lighting.BinReach.V;
    /// <summary><c>/light reachfade</c>: 1 = the lit stage windows every light to its binned reach (see
    /// <c>uReach</c>), 0 = the bin cut alone, as #384 shipped it.</summary>
    internal static float ReachUniform => Lighting.ReachFade.V ? _binReach : 0f;
    internal static float BinReach => _binReach;
    /// <summary>EXPERIMENT: the most lights a froxel keeps -- the K strongest by the engine's curve at the
    /// froxel's nearest point times the colour's luma, vanilla's per-object list made per froxel. 0 = all.
    /// The seam risk is the one #336 met with a per-froxel shadow order: a light dropped in one froxel and
    /// kept in its neighbour draws a grid edge, so the weakest kept light must be invisible for the cap to be.</summary>
    private static int _binCap => Lighting.BinCap.V;
    /// <summary>Adjusted radius of the uploaded lights this frame -- min, mean, max -- for the period line.
    /// The plaza heatmap read 32+ lights per pixel; whether that is many lights or huge radii is this number.</summary>
    private static float _radMin, _radMean, _radMax;
    internal static float ShadowNearWorld => _shadowNear * _srcRadius;

    /// <summary>Non-blocking timer + overflow readback: only ever touched when the driver says the result is
    /// already there, so the render thread never stalls on the GPU.</summary>
    private static void HarvestQuery(int slot)
    {
        bool pending = slot == 0 ? _queryPending0 : _queryPending1;
        if (!pending || _query[slot] == 0 || Gl.GetQueryObjectiv == null) return;
        int avail; Gl.GetQueryObjectiv(_query[slot], Gl.QUERY_RESULT_AVAILABLE, &avail);
        if (avail == 0) return;
        ulong ns; Gl.GetQueryObjectui64v(_query[slot], Gl.QUERY_RESULT, &ns);
        LastBuildMs = ns * 1e-6f;
        if (slot == 0) _queryPending0 = false; else _queryPending1 = false;
    }

    /// <summary>Read + reset the GPU-side cluster-overflow counter. Cold: called from the period log only,
    /// where one small readback is affordable.</summary>
    internal static uint TakeClusterOverflow()
    {
        if (!Ready) return 0;
        uint v = 0;
        Gl.GetNamedBufferSubData(_bufStats, IntPtr.Zero, (IntPtr)sizeof(uint), &v);
        uint zero = 0;
        Gl.NamedBufferSubData(_bufStats, IntPtr.Zero, (IntPtr)sizeof(uint), &zero);
        return v;
    }

    /// <summary>One-line description of the rig's state for the Alloy draw period log.</summary>
    internal static string Diag(uint overflow) =>
        $"{DescribeCurve()}, " +
        $"lights {LastLightCount} ({FrameInputs.LocalLightCount} flagged castShadows, " +
        $"{FrameInputs.LightsWithCube} with a cube; adjusted radius {_radMin:F1}/{_radMean:F1}/{_radMax:F1}u min/mean/max)" +
        $", cluster build {LastBuildMs:F3}ms on {GridX}x{GridY}x{GridZ} (binreach {_binReach:F2}, bincap {_binCap})" +
        (overflow > 0 ? $", CLUSTER OVERFLOW {overflow}/period (cap {ClusterCap})" : "");

    private static bool Ensure()
    {
        if (_prog != 0) return true;
        if (_failed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindBufferBase == null
            || Gl.GenQueries == null || Gl.GetQueryObjectui64v == null)
        { _failed = true; _log?.Info("AlloyLights: compute/SSBO entry points unresolved -- Alloy lighting unavailable"); return false; }

        uint prog = GlProgram.Compute(ShdClusterCs, ShaderStages.Source(ShdClusterCs), _log,
                                      "AlloyLights cluster build");
        if (prog == 0) { _failed = true; return false; }
        _locView = GlProgram.Loc(prog, "uView"); _locProjXY = GlProgram.Loc(prog, "uProjXY");
        _locZ = GlProgram.Loc(prog, "uZ"); _locCount = GlProgram.Loc(prog, "uLightCount");
        _locReach = GlProgram.Loc(prog, "uReach");
        _locBinCap = GlProgram.Loc(prog, "uBinCap"); _locBinAtten = GlProgram.Loc(prog, "uAtten");
        _locGrid = GlProgram.Loc(prog, "uGrid");

        uint b;
        Gl.CreateBuffers(1, &b); _bufLights = b;
        Gl.NamedBufferData(_bufLights, (IntPtr)(InitialLights * 8 * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        _ssboLights = InitialLights;
        Gl.CreateBuffers(1, &b); _bufCounts = b;
        Gl.NamedBufferData(_bufCounts, (IntPtr)(ClusterCount * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
        Gl.CreateBuffers(1, &b); _bufIndices = b;
        Gl.NamedBufferData(_bufIndices, (IntPtr)((long)ClusterCount * ClusterCap * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
        Gl.CreateBuffers(1, &b); _bufMovers = b;
        Gl.NamedBufferData(_bufMovers, (IntPtr)(CasterPass.CubeDynSphere.Length * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        _moversGen = -1;
        Gl.CreateBuffers(1, &b); _bufStats = b;
        uint zero = 0;
        Gl.NamedBufferData(_bufStats, (IntPtr)sizeof(uint), &zero, Gl.DYNAMIC_DRAW);

        fixed (uint* q = _query) Gl.GenQueries(2, q);
        _prog = prog;
        _log?.Info($"AlloyLights: clustered forward+ ready -- {GridX}x{GridY}x{GridZ} froxels, " +
                   $"{ClusterCap} lights/cluster, {InitialLights}-light SSBO (grown on demand -- no ceiling)");
        return true;
    }

    /// <summary>GLSL the lit fragment shader includes verbatim: the cluster lookup + the per-light loop. Kept
    /// here so the binning rule (grid dims, exponential slice mapping, SSBO layout) has exactly one source.
    /// The attenuation curves are ported from the staging receiver's Alloy lighting block (inc_lighting.shd,
    /// #67) so a seam between an owned and an engine-drawn part falls off the same way.</summary>
    /// <summary>sRGB -&gt; linear exponent, verified against <c>RenderInterface::GetColorSpaceFromSRGB</c>
    /// (the constant it reads is 2.2). Shared with <see cref="FrameRead"/>, which applies
    /// the same curve CPU-side to the area terms.</summary>
    internal const float SrgbGamma = 2.2f;
    /// <summary>#674: added to a static light's cube slot in the record's w (GLSL <c>CX_LIGHT_STATIC</c>).</summary>
    internal const float LightStatic = 256f;

    /// <summary>The clustered-lighting helpers the Alloy lit fragment stage is built from. Its sizing
    /// tokens are expanded by <see cref="GlProgram"/> at compile time, like any other stage's.</summary>
    /// <summary>The local-light cube receiver (#95). Composed BEFORE <see cref="FragmentLib"/> because the
    /// per-light loop in there calls it. The cubes come from <see cref="CasterPass"/>, which renders
    /// six faces per light into one <c>samplerCubeArrayShadow</c> -- so a light's shadow is one hardware-PCF
    /// fetch, and there is no per-slot sampler to unroll (the interim receiver's ceiling was exactly that
    /// unroll; a cube array has no such limit and the cost is VRAM alone).
    /// <para>Every geometric input comes from <c>uLocalLight</c>, the position and far plane the cube was
    /// actually BAKED with -- never the live light. A carried torch moves between the bake and the sample,
    /// and comparing against where it is NOW rather than where the depth was recorded from detaches its own
    /// shadow by exactly that step.</para>
    /// <para><c>uLocalLight[64]</c> is pinned against <c>CasterPass.MaxCubes</c> by that class's SelfCheck --
    /// GLSL cannot size it from the C# constant, so the check is what keeps the two honest.</para></summary>
    internal static string LocalShadowLib => ShaderSource.Get("cx_localshadow.glsl");

    internal static string FragmentLib => ShaderSource.Get("cx_lightfrag.glsl");

    /// <summary>The sun cascade receiver (#94), included by the lit fragment stage after
    /// <see cref="FragmentLib"/>. The maps come from <see cref="CasterPass"/>, which renders one
    /// concentric cascade per layer around the shadow focus and publishes the matrix each layer was actually
    /// drawn with -- so this only ever projects, never re-derives.
    /// <para>Selection is "the first cascade whose box holds the fragment", innermost first: with concentric
    /// fits every cascade is contained in the next, so the first hit is also the sharpest one available. No
    /// split distances, no view depth, and nothing that depends on the camera pose.</para>
    /// <para><c>uSunVp[4]</c> is pinned against <c>CasterPass.MaxCascades</c> by that class's SelfCheck --
    /// GLSL cannot size it from the C# constant, so the check is what keeps the two honest.</para></summary>
    internal static string SunShadowLib => ShaderSource.Get("cx_sunshadow.glsl");

    /// <summary>Resref the cluster build is looked up under before the embedded stage is used (#92). An override
    /// may carry the same <c>$CAP$</c>/<c>$GROUP$</c> tokens the embedded source does -- see
    /// <see cref="ShaderResource.Tokens"/> -- because the CPU sizes its buffers from those same constants.</summary>
    internal const string ShdClusterCs = "cx_cluster_cs";
}
