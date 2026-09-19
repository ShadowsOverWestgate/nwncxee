using System;
using System.Globalization;
using System.Numerics;
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
    private const uint BindLights = 0, BindCounts = 1, BindIndices = 2, BindStats = 3;
    /// <summary>Point-light attenuation, as the ENGINE computes it (#140). Verified against
    /// <c>inc_lighting.shd</c>'s <c>ComputePointLightSource</c> at <c>GAMMA_CORRECTION == 1</c> and against
    /// the two constants <c>GLRender::SetUniforms</c> @0x4cd341 feeds it:
    /// <code>
    ///     f2  = d^2 / R^2                        (R = GetLightAdjustedRadius, which IS what we upload)
    ///     att = (1 - f2) / (mi + ff * f2)
    /// </code>
    /// <para>This replaced an absolute <c>1/d^n</c> plateau model, and the replacement is the whole point of
    /// #140. That model was RANGE-BLIND: a light's authored range only fed the cutoff, so the curve was
    /// identical for a decorative 2m cave glow and a torch, and no single exponent can serve both. At
    /// exponent 4 it was blinding inside a metre and gone by five -- which is exactly "flat, covered in
    /// blinding light, and still can't see anything".</para>
    /// <para>The engine's curve is range-NORMALISED, so the authored range is the shape. A dim decorative
    /// light stays dim because its colour is dim and its R is small; a torch reaches because its R is large.
    /// That is the behaviour the content was authored against, and it is why nothing here needs a global
    /// gain to compensate for it.</para>
    /// <para><see cref="_gain"/> scales the whole curve (1 = the engine's own peak, <c>1/mi</c>);
    /// <see cref="_falloffPow"/> is an exponent on the <c>(1 - f2)</c> rim fade, 1.0 = vanilla exactly.
    /// <see cref="_srcRadius"/> is no longer part of this curve at all -- since #139 it is the sphere-light
    /// wrap radius and the base of the self-shadow near-field skip.</para>
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
    private static delegate* unmanaged<uint, int> _gspI;
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
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"AlloyLights.SelfCheck FAIL: {w}"); } }
        // The subsurface term is the stock one or it is nothing: the energy factor is inc_lighting.shd's
        // fEnergyPreservation / (1 + w) and the wrap rides the MAPPED normal (stock: the vertex normal, which washes out normal maps).
        E(MathF.Abs(SssK(0.38f) - 0.6262f) < 1e-3f, "SssK(0.38) is 2/(2+PI*0.38) = 0.626");
        E(FragmentLib.Contains("c * (att * max(ndlRaw + uSss.x, 0.0) * uSss.y)"), "the point loop carries the subsurface term on the MAPPED normal");
        E(AlloyRenderer.OwnedLitFsMain.Contains("max(dot(N, uAreaDir) + uSss.x, 0.0) * uSss.y"), "...and so does the sun");

        // #140: the engine's attenuation constants, and the property that actually matters -- the curve's
        // peak is the engine's own max-intensity setting in LINEAR space. Get the sign of the exponent
        // wrong and the peak inverts into a near-zero, which renders as "the lights all went out" with
        // nothing in the log to say why.
        Vector2 savedAtten = _atten;
        LatchAtten(0.5f, 1.5f, 1f);
        float peak = _atten.X > 0f ? 1f / _atten.X : 0f;
        E(MathF.Abs(peak - MathF.Pow(1.5f, SrgbGamma)) < 1e-3f, "the curve's peak is param(0x14) linearised");
        E(MathF.Abs(_atten.Y - (MathF.Pow(0.5f, -SrgbGamma) - MathF.Pow(1.5f, -SrgbGamma))) < 1e-3f,
          "lightFalloffFactor is cutoff^2 * (min^-2.2 - max^-2.2)");
        // ...and at the range the curve is exactly 0, whatever the constants: that is what keeps a light
        // from popping against its own cull sphere.
        E(MathF.Abs(Att(1f, 1f)) < 1e-6f, "attenuation is exactly 0 at the authored range");
        E(Att(0f, 1f) > Att(0.5f, 1f) && Att(0.5f, 1f) > Att(0.9f, 1f), "...and monotonic on the way there");
        // The cutoff multiplier is squared into ff and NOT into mi: it moves the rim, never the peak.
        Vector2 narrow = _atten;
        LatchAtten(0.5f, 1.5f, 2f);
        Vector2 wide = _atten;
        E(MathF.Abs(wide.X - narrow.X) < 1e-6f, "the radius multiplier does not move the peak");
        E(MathF.Abs(wide.Y - narrow.Y * 4f) < 1e-3f, "...and enters the falloff factor squared");
        LatchAtten(0f, 1.5f, 1f);
        E(_atten == wide, "a zero intensity setting leaves the last good pair standing");
        LatchAtten(2f, 1f, 1f);
        E(_atten == wide, "...and so does a min above max, which would invert the falloff");
        _atten = savedAtten;
        log.Info(ok ? "AlloyLights.SelfCheck: passed" : "AlloyLights.SelfCheck: FAILED");
        return ok;
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
    private static uint _prog, _bufLights, _bufCounts, _bufIndices, _bufStats;
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
        _gspI = (delegate* unmanaged<uint, int>)Native.Sym(SymbolMap.RiGetGlobalShaderParami);
        log.Info($"AlloyLights: {DescribeAtten()} -- /light retunes every dial live");
    }

    /// <summary>Drop every GL object (context loss / disarm). Safe to call outside the bracket only when the
    /// context is already gone -- the ids are then meaningless anyway.</summary>
    internal static void Forget()
    {
        _prog = 0; _bufLights = 0; _bufCounts = 0; _bufIndices = 0; _bufStats = 0;
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
            Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _bufLights);
            Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, (IntPtr)(n * 8 * sizeof(float)), s);
        }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
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
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _bufLights);
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)((long)lights * 8 * sizeof(float)), null, Gl.DYNAMIC_DRAW);
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
    }

    /// <summary>Grid + depth-range uniforms the lit fragment shader needs to find its own cluster. Called once
    /// per pass right after the program is bound.</summary>
    internal static void SetDrawUniforms(int locGrid, int locTile, int locZ, int locFalloff, int locWrap,
                                         int locAtten, int locHot, int locWrapN, int locSss, int* viewport,
                                         float near, float far, int locReach = -1)
    {
        if (locReach >= 0) Gl.Uniform1f(locReach, ReachUniform);
        if (near < MinNear) near = MinNear;
        if (locGrid >= 0) Gl.Uniform3i(locGrid, GridX, GridY, GridZ);
        if (locTile >= 0)
        {
            float* t = stackalloc float[4];
            t[0] = viewport[0]; t[1] = viewport[1];
            t[2] = MathF.Max((float)viewport[2] / GridX, 1f);
            t[3] = MathF.Max((float)viewport[3] / GridY, 1f);
            Gl.Uniform4fv(locTile, 1, t);
        }
        if (locZ >= 0) Gl.Uniform2f(locZ, near, far > near ? far : near * 2f);
        if (locFalloff >= 0)
        {
            LastFalloff = new Vector3(_gain, _srcRadius, _falloffPow);
            float* f = stackalloc float[3] { _gain, _srcRadius, _falloffPow };
            Gl.Uniform3fv(locFalloff, 1, f);
        }
        // A separate uniform rather than a fourth uFalloff lane on purpose: a content .shd override (#92)
        // still declaring `vec3 uFalloff` would take a Uniform4fv as INVALID_OPERATION, drop the whole
        // upload and render every point light black. An added uniform an override does not declare is
        // location -1 here and a no-op, so the stale override degrades to "no wrap" instead.
        if (locWrap >= 0) Gl.Uniform1f(locWrap, _wrap);
        // #140: the engine's own attenuation constants. Same additive-safety reasoning as uWrap -- a
        // content override (#92) that predates them is location -1 here and keeps whatever curve it shipped
        // with, rather than being handed a uniform it cannot read.
        if (locAtten >= 0) Gl.Uniform2f(locAtten, _atten.X, _atten.Y);
        if (locHot >= 0) Gl.Uniform2f(locHot, _hot, MathF.Max(_hotSize, 1e-4f));
        if (locWrapN >= 0) Gl.Uniform1f(locWrapN, _wrapN);
        if (locSss >= 0) Gl.Uniform2f(locSss, _sss, SssOn ? SssK(_sss) : 0f);
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
        if (!Ready || Gl.GetBufferSubData == null) return 0;
        uint v = 0;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _bufStats);
        Gl.GetBufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, (IntPtr)sizeof(uint), &v);
        uint zero = 0;
        Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, (IntPtr)sizeof(uint), &zero);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
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
        Gl.GenBuffers(1, &b); _bufLights = b;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _bufLights);
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)(InitialLights * 8 * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        _ssboLights = InitialLights;
        Gl.GenBuffers(1, &b); _bufCounts = b;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _bufCounts);
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)(ClusterCount * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
        Gl.GenBuffers(1, &b); _bufIndices = b;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _bufIndices);
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)((long)ClusterCount * ClusterCap * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
        Gl.GenBuffers(1, &b); _bufStats = b;
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _bufStats);
        uint zero = 0;
        Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)sizeof(uint), &zero, Gl.DYNAMIC_DRAW);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);

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
    internal const string LocalShadowLib = """
        uniform samplerCubeArrayShadow uLocalShadow;
        uniform vec4 uLocalLight[64];   // xyz = the position the cube was baked from, w = its far plane
        uniform vec2 uLocalPlane;       // x = the near plane every cube shares, y = 1 / face size in texels
        uniform int uLocalCubes;        // 0 = no cubes to sample this frame (vanilla, no lights, not yet drawn)
        // #139: x = PCF disc radius in cube texels, y = the world distance inside which a fragment is lit
        // outright. Both default to 0 on a program that never sets them, which IS the pre-#139 behaviour --
        // that is deliberate, so a stale content override degrades to a hard single tap rather than to
        // something wrong.
        uniform float uLocalSoftWorld;   // #674 bake only: the disc is at least this wide in world units (one lightmap texel), else its cube-texel edge aliases against the page
        uniform vec4 uLocalSoft;   // x = disc texels, y = near skip, z = disc stand-down: world radius per unit view depth (0 = none), w = cube taps per fragment (0 = every light)

        // #305: the DYNAMIC pool -- movers only, re-baked every frame at reduced resolution, composed with
        // the static cube by min(): depth visibility is a min-of-blockers, so the two arrays compose exactly
        // and NO COPY exists anywhere on the path (#187's composite cache lost to its own per-view copy;
        // this is not that design). uLocalDynPage[slot] carries the pool PAGE PLUS ONE (0 = no movers near
        // that light) -- a program that never sets it reads 0 everywhere, so a stale content override
        // degrades to static-only rather than to sampling page zero.
        uniform samplerCubeArrayShadow uLocalDynShadow;
        // #642: the same two arrays through a sampler object with NO compare, so the blocker search reads a
        // depth rather than a verdict. Bound by layout past the 0..15 table (TextureSlots.LocalShadowRaw);
        // the driver's unit count is asked once and where it is short uLocalPcss.x stays 0, which stands the
        // search down (a fixed disc at the ceiling) -- see CasterPass.RawSearch.
        layout(binding = 16) uniform samplerCubeArray uLocalShadowRaw;
        layout(binding = 17) uniform samplerCubeArray uLocalDynShadowRaw;
        uniform float uLocalDynPage[64];
        uniform float uLocalFade[64];   // per-slot shadow strength: a slot fading in after its bake or out after its light left the budget

        // Shadow-accuracy pass: every offset/bias below is HALF its #308 value (the stacked push reached 8
        // texels = 0.5-1u of peter-panning on the outer cascades). Creature self-shadowing stays off
        // (CX_LOCAL_DYN_NEAR), so the low-poly acne these were sized against no longer needs covering.
        // Sample point pushed along the normal by this many cube texels -- the same acne fight the sun
        // cascades run, scaled by the texel a face actually spans at this distance rather than a constant.
        const float CX_LOCAL_NORMAL_OFFSET = 1.0;
        // Depth bias in WORLD units along the face's major axis, applied before the projection so it stays a
        // constant distance whatever the light's reach is.
        const float CX_LOCAL_BIAS = 0.02;
        // #308 follow-up (acne): the sun receiver's slope terms, same reasoning -- a face lit at grazing
        // incidence needs offset and bias that grow with tan(theta), or the terminator stair-steps.
        const float CX_LOCAL_SLOPE_OFFSET = 0.75;
        const float CX_LOCAL_SLOPE_MAX = 2.0;
        // #308 follow-up: fragments ON A CREATURE (skinmesh or dangly -- exactly the classes the caster
        // walk puts in the dynamic set every frame) skip the dyn-pool tap when they stand within this many
        // times the near-field skip (uLocalSoft.y) of the light. In that band the pool's blocker is the
        // carrier's own body, whose pose-vs-bake mismatch is a flicker that cannot be made accurate at pool
        // resolution -- so creature self-shadowing from a carried light is OFF, while the creature's shadow
        // on the GROUND (a static fragment, never gated) and every static-map shadow stay untouched.
        // Scales with /shadow near, so the cutoff is tunable live.
        const float CX_LOCAL_DYN_NEAR = 3.0;
        // #148: 0 = this fragment is a SURFACE, and the normal offset above is the acne fight it needs.
        // 1 = it is a point inside a VOLUME -- a particle -- where the offset is not merely useless but
        // actively harmful: a billboard's normal faces the CAMERA, not away from any geometry, so offsetting
        // along it walks the sample point sideways across the shadow map's texel grid and turns a clean
        // lookup into bands. A volume has no surface to lift off; the constant bias below is the whole of
        // what it needs.
        // Unset reads 0, which is the surface behaviour every existing program and every content override
        // already relies on -- the safe default is the one nobody has to declare.
        uniform float uLocalMedia;
        // #641: the shadow floor -- the fraction of a point light's DIFFUSE that still arrives inside its own
        // shadow, standing in for the indirect light Alloy has none of. Pre-scaled by the area ambient on the
        // CPU; the light's own colour rides in the diffuse term it multiplies, so a torch shadow stays warm.
        // Unset reads 0, which is the full-black cutout this shipped with.
        uniform float uLocalFloor;

        // #642: x = the light's source radius in world units (the penumbra scale; 0 = no blocker search, the
        // disc sits at its ceiling), y = the temporal rotation phase 0..1 (0 when the taa stage is not live,
        // so the dither is a fixed pattern rather than a shimmer), z = disc taps. All zero on a program that
        // never sets it, which is a fixed disc.
        uniform vec4 uLocalPcss;
        // Below this radius (in texels) the disc stands down to the hardware tap: a fragment in contact.
        const float CX_LOCAL_DISC_MIN = 0.25;
        const float CX_LOCAL_DISC_WORLD_MAX = 64.0;   // #674 bake: the world-minimum disc's ceiling in cube texels
        const int CX_LOCAL_SEARCH = 4;
        // #642: the per-fragment dither the disc spins by. Interleaved gradient noise on the pixel where a
        // fragment head declares CX_FS; a vertex stage (grass, weather, particles compile this lib there
        // too) has no gl_FragCoord, and a head that forgets the define gets the coarser hash, not a
        // compile failure.
        #ifdef CX_FS
        float CxShadowNoise(vec3 worldPos) { return fract(52.9829189 * fract(0.06711056 * gl_FragCoord.x + 0.00583715 * gl_FragCoord.y)); }
        #else
        float CxShadowNoise(vec3 worldPos) { return fract(sin(dot(floor(worldPos * 64.0), vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
        #endif
        // Tap i of n on a unit Vogel disc, spun by `rot` turns.
        vec2 CxVogel(int i, int n, float rot)
        {
            float r = sqrt((float(i) + 0.5) / float(n));
            float a = float(i) * 2.399963 + rot * 6.283185;
            return r * vec2(cos(a), sin(a));
        }
        // The stored window depth back to a major-axis distance: the inverse of the `ref` line below.
        float CxCubeDist(float z, float near, float far) { return near / max(1.0 - z * (far - near) / far, 1e-4); }
        // #642: the penumbra radius in texels, 0 .. uLocalSoft.x -- PCSS: the mean depth of the blockers found
        // by CX_LOCAL_SEARCH raw reads on a fixed ring at the ceiling radius (plus `mc`, the centre read the
        // caller already has), then (receiver - blocker) / blocker scaled by the source radius. The ring is
        // NOT dithered: neighbouring fragments must agree on the penumbra, or the edge speckles where one
        // fragment blurs and the next takes the hard tap (seen live). A search that finds no blocker is a
        // hard tap; uLocalPcss.x = 0 (no raw view on this driver) is the ceiling everywhere.
        float CxPenumbra(samplerCubeArray raw, float layer, vec3 d, vec3 u1, vec3 v1,
                         float m, float mc, float near, float far, float texel)
        {
            float src = uLocalPcss.x;
            if (src <= 0.0) return uLocalSoft.x;
            float sum = 0.0, n = 0.0;
            if (mc < m) { sum += mc; n += 1.0; }
            for (int k = 0; k < CX_LOCAL_SEARCH; k++)
            {
                vec2 o = CxVogel(k, CX_LOCAL_SEARCH, 0.0) * (texel * uLocalSoft.x);
                float mk = CxCubeDist(texture(raw, vec4(d + u1 * o.x + v1 * o.y, layer)).r, near, far);
                if (mk < m) { sum += mk; n += 1.0; }
            }
            if (n <= 0.0) return 0.0;
            float mb = sum / n;
            return clamp((m - mb) / mb * src / texel, 0.0, uLocalSoft.x);
        }

        // Visibility of world point `worldPos` from the light in cube slot `slot`, 0 (shadowed) .. 1 (lit).
        // The reference depth is the exact inverse of ShadowMath.LinearizeFaceDepth: for a 90deg face fitted
        // to [near, far], the window depth stored at major-axis distance m is far/(far-near) * (1 - near/m).
        // ShadowMath's SelfCheck welds that round-trip headless, so a convention slip fails there, not here.
        // `ndlRaw` is the caller's geometric dot(N, L) -- already in hand at every lit call site -- for the
        // slope terms; recomputing it here cost a normalize per fragment per shadowing light.
        float CxLocalShadow(int slot, vec3 worldPos, vec3 N, float ndlRaw, float onMover, float viewDepth)
        {
            if (uLocalCubes <= 0 || slot < 0 || slot >= uLocalCubes) return 1.0;
            vec4 L = uLocalLight[slot];
            float far = L.w, near = uLocalPlane.x;
            vec3 d = worldPos - L.xyz;
            float m = max(max(abs(d.x), abs(d.y)), abs(d.z));
            // outside the recorded shell there is no depth to compare against, and lit is the safe answer
            if (far <= near || m <= near + CX_LOCAL_BIAS || m >= far) return 1.0;
            // #139: inside the source sphere there is nothing to occlude. A carried torch sits INSIDE the
            // hand and forearm holding it, so a punctual test there shadows the carrier from its own light
            // -- and at that range one cube texel spans most of the arm, which is the blocky self-shadow.
            // CARRIER ONLY: on the ground this box (Chebyshev, world-aligned) drew as a lit diamond around
            // every fire placeable -- the sun's shadow apparently cut out in a square.
            if (onMover > 0.5 && m < uLocalSoft.y) return 1.0;
            float texel = 2.0 * m * uLocalPlane.y;
            // The acne offset/bias must clear the COARSEST map that will answer this fragment: a light with
            // an occupied dynamic page samples that page too, at (typically) half the static resolution.
            // Queried off the array itself so a stale content override merely lacks the fix. The PCF disc
            // below keeps the STATIC texel -- it only filters the static map, and widening it whenever a
            // mover claims the page would pop the penumbra on an unrelated event.
            float pg = uLocalDynPage[slot];
            float biasTexel = 2.0 * m * max(uLocalPlane.y,
                                            pg > 0.5 ? 1.0 / float(textureSize(uLocalDynShadow, 0).x) : 0.0);
            // tan of the light-to-normal angle, media-gated exactly as the sun receiver's slope is and for
            // the same billboard reason -- and zero on a light-backfacing fragment, where there is no acne
            // to fight and a pinned-at-max slope would only punch the bias through thin casters.
            float slope = ndlRaw <= 0.0 ? 0.0
                          : min(sqrt(max(1.0 - ndlRaw * ndlRaw, 0.0)) / max(ndlRaw, 0.1),
                                CX_LOCAL_SLOPE_MAX) * (1.0 - uLocalMedia);
            d += N * (biasTexel * (CX_LOCAL_NORMAL_OFFSET * (1.0 - uLocalMedia)
                                   + CX_LOCAL_SLOPE_OFFSET * slope));
            // the bias moves the compare point TOWARD the light, never away: the test is `ref <= stored` =
            // lit, and a fragment lying on its own recorded surface stores exactly its own depth. Pushing
            // the reference the other way makes every lit surface shadow itself.
            m = clamp(max(max(abs(d.x), abs(d.y)), abs(d.z)) - CX_LOCAL_BIAS * (1.0 + slope), near, far);
            float ref = far / (far - near) * (1.0 - near / m);
            // #642: the centre's raw depth, the blocker search's first sample
            float mc = uLocalPcss.x > 0.0 ? CxCubeDist(texture(uLocalShadowRaw, vec4(d, float(slot))).r, near, far) : m;
            float vs = texture(uLocalShadow, vec4(d, float(slot)), ref);
            // #308 follow-up: the dyn tap stands down for a CREATURE fragment inside the carrier band --
            // see CX_LOCAL_DYN_NEAR. Ground and static receivers pass onMover 0 and are never gated.
            bool dyn = pg > 0.5 && !(onMover > 0.5 && m < CX_LOCAL_DYN_NEAR * uLocalSoft.y);
            float vd = dyn ? texture(uLocalDynShadow, vec4(d, pg - 1.0), ref) : 1.0;
            float v = min(vs, vd);
            // #326: one tap when the disc is under its pixel budget on screen (z = px / lod scale) -- four of
            // every five cube fetches
            if (uLocalSoft.x <= 0.0 || (uLocalSoft.z > 0.0 && texel * uLocalSoft.x < viewDepth * uLocalSoft.z)) return mix(1.0, v, uLocalFade[slot]);
            // #642: contact-hardening. The disc radius is not uLocalSoft.x but a per-fragment penumbra
            // estimate with uLocalSoft.x as its ceiling -- see CxPenumbra.
            vec3 axis = abs(d.x) < 0.9 * m ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
            vec3 u1 = normalize(cross(axis, d));
            vec3 v1 = normalize(cross(d, u1));
            float rot = fract(CxShadowNoise(worldPos) + uLocalPcss.y);
            float rs = max(CxPenumbra(uLocalShadowRaw, float(slot), d, u1, v1, m, mc, near, far, texel),
                           min(uLocalSoftWorld / texel, CX_LOCAL_DISC_WORLD_MAX));
            float rd = 0.0, dtex = 0.0;
            if (dyn)
            {
                dtex = 2.0 * m / float(textureSize(uLocalDynShadow, 0).x);
                float md = uLocalPcss.x > 0.0 ? CxCubeDist(texture(uLocalDynShadowRaw, vec4(d, pg - 1.0)).r, near, far) : m;
                rd = CxPenumbra(uLocalDynShadowRaw, pg - 1.0, d, u1, v1, m, md, near, far, dtex);
            }
            // #642: a rotated Vogel disc at the estimated radius. The cube fetch is already hardware PCF, so
            // this WIDENS that filter rather than replacing it. The reference depth is deliberately the
            // centre tap's for every tap -- re-deriving it per tap would compare each neighbour against its
            // own surface and dissolve the penumbra it is there to create.
            // ponytail: one disc for both maps at its own radius each; no per-tap weighting, no bilateral term.
            int taps = int(uLocalPcss.z);
            if (rs >= CX_LOCAL_DISC_MIN && taps > 0)
            {
                for (int i = 0; i < taps; i++)
                {
                    vec2 o = CxVogel(i, taps, rot) * (texel * rs);
                    vec3 dt = d + u1 * o.x + v1 * o.y;
                    float rt = ref;
                    if (uLocalSoftWorld > 0.0)
                    {   // bake: next to a floor-level light this disc spans many cube texels, and against the
                        // centre's depth the receiver's own plane reads as a blocker (a dark disc round every
                        // brazier) -- each tap compares against the plane's depth along its own ray instead
                        float dn = dot(dt, N);
                        float s = abs(dn) > 1e-4 ? clamp(dot(d, N) / dn, 0.5, 2.0) : 1.0;
                        float mt = clamp(max(max(abs(dt.x), abs(dt.y)), abs(dt.z)) * s - CX_LOCAL_BIAS * (1.0 + slope), near, far);
                        rt = far / (far - near) * (1.0 - near / mt);
                    }
                    vs += texture(uLocalShadow, vec4(dt, float(slot)), rt);
                }
                vs /= float(taps + 1);
            }
            if (dyn && rd >= CX_LOCAL_DISC_MIN && taps > 0)
            {
                for (int i = 0; i < taps; i++)
                {
                    vec2 o = CxVogel(i, taps, rot) * (dtex * rd);
                    vd += texture(uLocalDynShadow, vec4(d + u1 * o.x + v1 * o.y, pg - 1.0), ref);
                }
                vd /= float(taps + 1);
            }
            return mix(1.0, min(vs, vd), uLocalFade[slot]);
        }

        // #674: the visibility a baked receiver still owes a hot static light: the mover's occlusion off the
        // DYNAMIC page, scaled by the STATIC cube's visibility -- where the bake already holds this light's
        // shadow (a fence's) the mover adds nothing, else the creature's shadow stacked on the fence's from
        // the same light. Same offset and bias as the full receiver; 1.0 where the light holds no page.
        float CxLocalDynShadow(int slot, vec3 worldPos, vec3 N, float ndlRaw)
        {
            if (uLocalCubes <= 0 || slot < 0 || slot >= uLocalCubes) return 1.0;
            float pg = uLocalDynPage[slot];
            if (pg < 0.5) return 1.0;
            vec4 L = uLocalLight[slot];
            float far = L.w, near = uLocalPlane.x;
            vec3 d = worldPos - L.xyz;
            float m = max(max(abs(d.x), abs(d.y)), abs(d.z));
            if (far <= near || m <= near + CX_LOCAL_BIAS || m >= far) return 1.0;
            float biasTexel = 2.0 * m / float(textureSize(uLocalDynShadow, 0).x);
            float slope = ndlRaw <= 0.0 ? 0.0
                          : min(sqrt(max(1.0 - ndlRaw * ndlRaw, 0.0)) / max(ndlRaw, 0.1), CX_LOCAL_SLOPE_MAX);
            d += N * (biasTexel * (CX_LOCAL_NORMAL_OFFSET + CX_LOCAL_SLOPE_OFFSET * slope));
            m = clamp(max(max(abs(d.x), abs(d.y)), abs(d.z)) - CX_LOCAL_BIAS * (1.0 + slope), near, far);
            float ref = far / (far - near) * (1.0 - near / m);
            // a baked receiver has no static cube to min() this against, so the one hardware tap the live
            // path gets away with shows its texel here: a 4-tap disc one dyn texel wide
            float t = 2.0 * m / float(textureSize(uLocalDynShadow, 0).x);
            vec3 axis = abs(d.x) < 0.9 * m ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
            vec3 u1 = normalize(cross(axis, d)) * t, v1 = normalize(cross(d, u1)) * t;
            float v = texture(uLocalDynShadow, vec4(d + u1 + v1, pg - 1.0), ref) + texture(uLocalDynShadow, vec4(d - u1 + v1, pg - 1.0), ref)
                    + texture(uLocalDynShadow, vec4(d + u1 - v1, pg - 1.0), ref) + texture(uLocalDynShadow, vec4(d - u1 - v1, pg - 1.0), ref);
            // the static half through the same disc: one hard tap against the bake's soft edge cut the
            // creature's silhouette out of the fence's penumbra
            float vs = texture(uLocalShadow, vec4(d + u1 + v1, float(slot)), ref) + texture(uLocalShadow, vec4(d - u1 + v1, float(slot)), ref)
                     + texture(uLocalShadow, vec4(d + u1 - v1, float(slot)), ref) + texture(uLocalShadow, vec4(d - u1 - v1, float(slot)), ref);
            return mix(1.0, 1.0 - vs * 0.25 * (1.0 - v * 0.25), uLocalFade[slot]);
        }

        """;

    internal const string FragmentLib = """
        layout(std430, binding = 0) readonly buffer CxLightBuf { vec4 uLights[]; };
        layout(std430, binding = 1) readonly buffer CxClusterCount { uint uClCount[]; };
        layout(std430, binding = 2) readonly buffer CxClusterIdx { uint uClIdx[]; };
        uniform ivec3 uGrid;
        uniform vec4 uTile;            // xy = viewport origin in window pixels, zw = pixels per tile
        uniform vec2 uZ;
        // x = peak gain at the source, y = source radius in world units, z = falloff exponent.
        // x = master gain (1 = the engine's own peak), y = sphere-light wrap radius (#139),
        // z = exponent on the rim fade (1 = vanilla exactly).
        uniform vec3 uFalloff;
        // #140: (lightMaxIntensityInv, lightFalloffFactor) -- the engine's own two, read off its global
        // shader params rather than copied. Zero on a stage that never gets them, which the guard below
        // reads as "fall back to the shipped pair" rather than as a divide by nothing.
        uniform vec2 uAtten;
        // #142: (near-field weight, core size as a fraction of the range). Zero on a stage that never gets
        // it, which is #140's curve unchanged.
        uniform vec2 uHot;
        // The cluster bins a light out to uReach * R (/light binreach). Without a matching window here a
        // fragment inside a binned froxel but past the reach got the full curve while its neighbour in the
        // next froxel got nothing: a light popping on along the froxel grid at one distance. 0 = no window.
        uniform float uReach;
        // #139: sphere-light wrap, as a multiple of uFalloff.y. 0 (the value an override that never declares
        // it leaves behind) is the punctual N.L this shipped with.
        uniform float uWrap;
        // #143: the distance-INDEPENDENT wrap floor -- a separate uniform rather than a second lane in
        // uWrap, so a stale override that only declares `float uWrap` still links and still gets #139's
        // behaviour. 0 = off, which is what such an override leaves behind.
        uniform float uWrapN;
        // #375 follow-up: the stock subsurface term as (wrap, energy) -- AlloyLights._sss. (0, 0) on a stage
        // that never declares it, which is the Lambert term alone.
        uniform vec2 uSss;
        const uint CX_CLUSTER_CAP = $CAP$u;
        const int CX_CAP_MAX = 2;   // #336: the shadow cap's ceiling -- two NAMED candidates, no arrays
        // The engine's own sRGB->linear conversion (Ghidra RenderInterface::GetColorSpaceFromSRGB: powf(c,
        // 2.2) per channel, identity at 0 and 1). Textures and material colours both arrive non-linear --
        // the stock shaders apply the same curve via ApplyColorSpace before lighting them.
        // ponytail: unconditional. The engine gates it on GetGlobalShaderParami(0), the gamma pipeline the
        // ship config runs and the shipped .shd assume (GAMMA_CORRECTION 1); plumb that param through if a
        // legacy-pipeline build ever needs the Alloy path.
        vec3 CxLinear(vec3 c) { return pow(max(c, vec3(0.0)), vec3($SRGB$)); }

        // #143: energy-conserving wrapped diffuse. NWN's meshes are low-poly with smoothed vertex normals,
        // so a punctual terminator lands mid-facet as a straight hard crease -- the artifact the community's
        // GL 3.3 "light wrapping" shaders exist to hide. Widening the band by `w` and dividing the result by
        // (1 + w) is the standard half-Lambert generalisation: the PEAK is unchanged, so a fully lit surface
        // is bit-identical to the punctual one and only the band around the terminator moves.
        // ponytail: one global wrap for every material. The modern refinement is pre-integrated skin
        // (Penner 2011) -- a curvature-driven terminator LUT -- but it is per-material by nature and NWN
        // carries no "this part is skin" tag; on armour and stone it reads wrong. Tag materials first.
        float CxWrap(float ndl, float w)
        {
            return w <= 0.0 ? clamp(ndl, 0.0, 1.0) : clamp((ndl + w) / (1.0 + w), 0.0, 1.0);
        }

        // #132: there is no encode here any more, and no highlight shoulder either. Every Alloy stage
        // writes LINEAR into PostChain's own target and the resolve does the one encode for the whole frame
        // -- one conversion per screen pixel instead of one per fragment, gated on the engine's own gamma
        // parameter, and the only order in which blended compositing is correct. A stage that re-encoded
        // here would be encoded twice. The pass stands down to vanilla outright on the frames PostChain
        // cannot arm (see PostChain.Begin): a linear Alloy frame with no resolve is wrong by a gamma, and
        // there is no display-space variant of these stages left to fall back to.

        // Cluster of this fragment: screen tile from gl_FragCoord, depth slice from the interpolated view
        // depth (gl_Position.w, which for a perspective projection IS the linear view depth -- no dependence
        // on the projection's handedness convention). Must mirror the compute pass's slice mapping exactly.
        // gl_FragCoord is WINDOW-relative, so the viewport origin comes out first -- the engine's reflection
        // subframes do not all render at (0,0). A VERTEX stage composing this library (#336: particles light
        // per vertex) has no gl_FragCoord and defines CX_CLUSTER_XY itself, in tile units, before the lib.
        #ifndef CX_CLUSTER_XY
        #define CX_CLUSTER_XY ((gl_FragCoord.xy - uTile.xy) / uTile.zw)
        #endif
        uint CxCluster(float viewDepth)
        {
            vec2 tile = CX_CLUSTER_XY;
            int gx = clamp(int(tile.x), 0, uGrid.x - 1);
            int gy = clamp(int(tile.y), 0, uGrid.y - 1);
            float k = log(max(viewDepth, uZ.x) / uZ.x) / log(uZ.y / uZ.x);
            int gz = clamp(int(k * float(uGrid.z)), 0, uGrid.z - 1);
            return uint(gx + uGrid.x * (gy + uGrid.y * gz));
        }

        // fAngleMin (inc_lighting.shd:88): the floor the engine clamps N.V to before the view-side
        // normalisation divides by it.
        const float CX_ANGLE_MIN = 0.01;

        // Schlick's Fresnel, the pow-5 form the ship quality mode takes (inc_lighting.shd GetFresnel,
        // SHADER_QUALITY_MODE > 1; the lower modes use pow 4).
        float CxFresnel(float c)
        {
            float f = 1.0 - c;
            float sq = f * f;
            return clamp(f * sq * sq, 0.0, 1.0);
        }

        // #120, corrected by the live A/B on #121: the specular lobe every lit term goes through, as the
        // ENGINE computes it (inc_lighting.shd:150-265 at the ship quality mode -- inc_config.shd's
        // SPECULAR_DISTRIBUTION_MODEL 1 = GGX, SPECULAR_GEOMETRIC_SHADOWING 2 = Schlick, SPECULAR_FRESNEL 1
        // = full). This replaced a textbook GGX, which differed in the one place that decides how a surface
        // reads: it remapped alpha = rough^2, while the engine feeds fRoughness_sq = rough^2 as alpha^2 --
        // alpha = rough. Every authored and synthesised roughness in the content is tuned for the engine's
        // convention, so the remap shaded a 0.24-roughness stone as a 0.06 one: a far tighter, far brighter
        // highlight on every part carrying a specular map, which is ~100% of SoW's.
        // <para>Two halves, split the way the engine splits them: this per-LIGHT one, and the view-side
        // normalisation in CxSpecBase, which is common to every light and applied once (the engine's
        // fSpecularBaseModifier). fSpecularity rides in through the Fresnel term and must NOT be multiplied
        // in again -- inc_lighting.shd:600-602 scales by it only when SPECULAR_FRESNEL != 1.</para>
        // Returns the lobe per unit IRRADIANCE -- the caller has already folded N.L and attenuation into the
        // light term it multiplies this by.
        // ponytail: the low-quality branches (Blinn-Phong, no shadowing, the simplified Fresnel) are not
        // reproduced. Add them if a device that cannot run the ship mode ever needs to match its own vanilla.
        float CxSpecLobe(vec3 N, vec3 V, vec3 L, float rough, float spec)
        {
            vec3 H = normalize(V + L);
            float NdotH = clamp(dot(N, H), 0.0, 1.0);
            float VdotH = clamp(dot(H, V), 0.0, 1.0);
            float NdotL = clamp(dot(N, L), 0.0, 1.0);
            float a2 = rough * rough;
            // GGX distribution minus its alpha^2 numerator, which lives in CxSpecBase
            float den = 1.0 - NdotH * NdotH * (1.0 - a2);
            float d = 1.0 / max(den * den, 1e-8);
            d *= 1.0 / mix(NdotL, 1.0, rough);          // geometric shadowing, light side
            return d * mix(spec, 1.0, CxFresnel(VdotH));
        }

        // The half that does not vary per light: alpha^2/4 and the view-side shadowing term.
        float CxSpecBase(float NdotV, float rough)
        {
            return rough * rough * 0.25 / mix(max(NdotV, CX_ANGLE_MIN), 1.0, rough);
        }

        // #674: one light's colour, attenuation and geometry -- the per-light body, shared by the cluster loop
        // below and the bake stage (LightmapBake.BakeFs), so a baked texel and a live one are ONE code path.
        // False = out of reach (nothing to add). `c` is the linearised colour, `att` the engine's curve (#140,
        // #142) already floored at 0, `f2` the range-normalised distance squared (the cap's ranking term),
        // `ndlRaw` the geometric dot and `ndl` the wrapped one (#139 sphere wrap, #143 floor).
        bool CxPointLight(vec4 pr, vec3 c0, vec3 worldPos, vec3 N,
                          out vec3 c, out vec3 L, out float att, out float f2, out float ndlRaw, out float ndl)
        {
            c = vec3(0.0); L = vec3(0.0); att = 0.0; f2 = 0.0; ndlRaw = 0.0; ndl = 0.0;
            // pr.w is GetLightAdjustedRadius: the engine's own R, overbright surplus already spent
            // on it. Squared here because the engine's own cull is the same test on the same value
            // (SetLightGL @0x4c1007 uploads exactly R*R as lightQuadraticAtten).
            float rr = pr.w * pr.w;
            vec3 d = pr.xyz - worldPos;
            float dd = dot(d, d);
            if (dd > rr) return false;
            // The walk already divided an overbright colour down by its max channel (spending the
            // surplus on radius, as GetLightAdjustedColor does), so every channel is <= 1 here and the
            // plain sRGB curve is the right linearization.
            c = CxLinear(c0);
            // #140: the ENGINE's curve, range-normalised -- inc_lighting.shd's ComputePointLightSource
            // at GAMMA_CORRECTION == 1, verbatim. The authored range IS the shape here, which is the
            // whole reason a dim cave glow and a torch can share one formula: the glow's R is small and
            // its colour dim, the torch's R is large (its overbright surplus went there), and neither
            // needs a global gain to rescue it. The rim fade reaches exactly 0 at f2 = 1, so no separate
            // cutoff window is needed and a light still cannot pop against its own cull sphere.
            f2 = dd / rr;
            float fade = 1.0 - f2;
            if (uReach > 0.0)
            {
                float r2 = uReach * uReach;
                if (f2 > r2) return false;
                fade *= min((r2 - f2) / (r2 * 0.3), 1.0);   // to zero over the last 30% of the reach (in f2)
            }
            // Uniform-valued branch, so it is coherent across the whole draw and free -- and at the
            // shipped 1.0 the pow is not merely cheap but exactly skipped, which is what makes "vanilla"
            // bit-exact rather than approximately right.
            if (uFalloff.z != 1.0) fade = pow(max(fade, 0.0), uFalloff.z);
            vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
            att = uFalloff.x * fade / (at.x + at.y * f2);
            // #142: the near-field core -- a compact-support bump reaching uHot.y of the range and
            // exactly nothing past it, so the body above owns the whole tail and the gain that was
            // calibrated against vanilla cannot drift. Peak is uHot.x / at.x, so gain + hot = 1 puts
            // the very centre at the engine's own peak.
            if (uHot.x > 0.0)
            {
                float t = max(1.0 - f2 / (uHot.y * uHot.y), 0.0);
                att += uHot.x * fade * t * t / at.x;
            }
            att = max(att, 0.0);
            float invD = inversesqrt(max(dd, 1e-8));
            L = d * invD;
            ndlRaw = dot(N, L);
            // #139: the light is a SPHERE of radius uFalloff.y -- the same radius the attenuation
            // plateau above already models it with -- not a point. `s` is the sine of the half-angle it
            // subtends here, and wrapping N.L by it softens the terminator by exactly the amount the
            // source's own size would. The (1 + s) divide is what keeps this a redistribution rather
            // than free energy. Far away s -> 0 and the whole expression IS max(dot(N, L), 0.0) again,
            // so placed lights read exactly as they did; only a light held at arm's length differs --
            // which is the case where a punctual terminator cuts a low-poly facet in half.
            float s = uWrap <= 0.0 ? 0.0 : clamp(uWrap * uFalloff.y * invD, 0.0, 1.0);
            // max, not sum: at arm's length the sphere term is already at full strength and adding the
            // #143 floor on top of it would wrap past the point of looking lit at all.
            ndl = CxWrap(ndlRaw, max(s, uWrapN));
            return true;
        }
        // The unshadowed diffuse of one light: the wrapped Lambert term plus the stock subsurface leak.
        vec3 CxPointDiffuse(vec3 c, float att, float ndlRaw, float ndl)
        {
            return c * (att * ndl) + c * (att * max(ndlRaw + uSss.x, 0.0) * uSss.y);
        }

        // #674: a static light's record carries its cube slot PLUS this when it is in the lightmap pages
        // (AlloyLights.LightStatic). Decode: w >= 128 is static, slot = w - 256 (-1 survives as 255).
        const float CX_LIGHT_STATIC = 256.0;
        // #674: lightmap.movers -- 1 = a HOT static light (a mover under it this frame) takes its mover's
        // shadow back out of the baked term; 0 = baked receivers skip static lights outright. Unset reads
        // 0, the off position, which is what a stale content override degrades to.
        uniform int uLmMovers;

        // Diffuse AND specular contribution of every light binned into this fragment's cluster, in ONE walk:
        // both terms want the same per-light colour, attenuation, N.L and cube-shadow fetch, and running the
        // cluster twice would pay for all four again. World space throughout: the Alloy pass has world
        // positions and normals in hand, so there is no eye-space round trip.
        // #674: `baked` > 0.5 = this fragment reads its static light from the lightmap pages, so every static
        // light is skipped here -- except a HOT one under lightmap.movers, whose mover shadow (the dyn page
        // alone, never the static half: the bake already holds that) accumulates into `lmSub` (x occluded, y total) as the
        // contribution to take back out of the baked term (#666: exact wherever the bake had the light
        // unshadowed; over-darkens only down to the floor where static geometry already shadowed the texel).
        vec3 CxClusteredLightLm(vec3 worldPos, vec3 N, vec3 V, float viewDepth, float onMover,
                                float rough, float specularity, float baked, out vec3 spec, out vec3 lmSub)
        {
            spec = vec3(0.0); lmSub = vec3(0.0);
            uint cl = CxCluster(viewDepth);
            uint n = min(uClCount[cl], CX_CLUSTER_CAP);
            uint base = cl * CX_CLUSTER_CAP;
            vec3 acc = vec3(0.0);
            // #336: /shadow cap -- the cube taps a fragment may pay, chosen PER FRAGMENT by reach
            // (f2 = dd/rr, the attenuation's own term): the capN lights reaching this fragment most
            // strongly get a cube fetch, the rest draw unshadowed. Per fragment, never per froxel -- a
            // per-froxel choice put the seam on the cluster grid, squares glued to the screen wherever four
            // cube lights overlapped. The candidates are kept sorted; an evicted one is added unshadowed.
            // EXPERIMENT: the candidates used to be five local arrays indexed by a runtime variable in a
            // while loop -- what a shader compiler spills to scratch memory. Two named slots, A the strongest.
            int capN = int(min(uLocalSoft.w, float(CX_CAP_MAX)));
            float aF2 = 1e30, bF2 = 1e30; int aSlot = -1, bSlot = -1; float aNdl = 0.0, bNdl = 0.0;
            vec3 aLit = vec3(0.0), aSpc = vec3(0.0), bLit = vec3(0.0), bSpc = vec3(0.0);
            for (uint k = 0u; k < n; k++)
            {
                uint li = uClIdx[base + k];
                vec4 pr = uLights[li * 2u];
                vec4 cs = uLights[li * 2u + 1u];
                // the record's spare w carries this light's cube slot (-1 = it casts no shadow this frame),
                // and the #674 static bit above it
                int w = int(cs.w);
                bool isStatic = w >= 128;
                int slot = isStatic ? w - 256 : w;
                // a static light on a baked receiver is in the pages already: a cold one costs nothing here,
                // a HOT one (a mover under it) owes its mover's shadow and nothing else
                bool hot = isStatic && baked > 0.5;
                if (hot && uLmMovers == 0) continue;
                vec3 c, L; float att, f2, ndlRaw, ndl;
                if (!CxPointLight(pr, cs.rgb, worldPos, N, c, L, att, f2, ndlRaw, ndl)) continue;
                // slot -2 = ambient class: vanilla adds c*att to the ambient term, no N.L, no shadow
                if (slot == -2) { acc += c * att; continue; }
                if (ndl <= 0.0) continue;   // facing away: no light, and no reason to pay for a cube fetch
                if (hot)
                {
                    // lmSub.x = occluded, lmSub.y = total (luminance of the unshadowed diffuse) over EVERY
                    // static light reaching here, cube or not: the caller scales the bake by 1 - x/y, so a
                    // mover's shadow from one light darkens only that light's share, and the others still lit
                    float lum = dot(CxPointDiffuse(c, att, ndlRaw, ndl), vec3(0.2126, 0.7152, 0.0722));
                    lmSub.y += lum;
                    if (slot >= 0 && uLocalDynPage[slot] > 0.5) lmSub.x += lum * (1.0 - CxLocalDynShadow(slot, worldPos, N, ndlRaw));
                    continue;
                }
                vec3 dif = c * (att * ndl);
                vec3 spc = dif * CxSpecLobe(N, V, L, rough, specularity);
                // ponytail: a light behind the surface (ndl <= 0, continued above) skips its subsurface leak
                // rather than being fetched unshadowed; stock has no shadow to lose there, Alloy does
                vec3 unsh = CxPointDiffuse(c, att, ndlRaw, ndl);
                if (slot < 0) { acc += unsh; spec += spc; continue; }
                if (capN <= 0)
                {
                    float sh = CxLocalShadow(slot, worldPos, N, ndlRaw, onMover, viewDepth);
                    acc += unsh * mix(uLocalFloor, 1.0, sh); spec += spc * sh;   // floor on diffuse only: a floored highlight is a wrong highlight
                    continue;
                }
                if (f2 < aF2)
                {   // new strongest: A moves to B (capN 2) or is evicted unshadowed (capN 1); old B is evicted
                    if (capN >= 2) { if (bSlot >= 0) { acc += bLit; spec += bSpc; }
                                     bF2 = aF2; bSlot = aSlot; bNdl = aNdl; bLit = aLit; bSpc = aSpc; }
                    else if (aSlot >= 0) { acc += aLit; spec += aSpc; }
                    aF2 = f2; aSlot = slot; aNdl = ndlRaw; aLit = unsh; aSpc = spc;
                }
                else if (capN >= 2 && f2 < bF2)
                {
                    if (bSlot >= 0) { acc += bLit; spec += bSpc; }
                    bF2 = f2; bSlot = slot; bNdl = ndlRaw; bLit = unsh; bSpc = spc;
                }
                else { acc += unsh; spec += spc; }
            }
            if (aSlot >= 0)
            {
                float sh = CxLocalShadow(aSlot, worldPos, N, aNdl, onMover, viewDepth);
                acc += aLit * mix(uLocalFloor, 1.0, sh); spec += aSpc * sh;
            }
            if (bSlot >= 0)
            {
                float sh = CxLocalShadow(bSlot, worldPos, N, bNdl, onMover, viewDepth);
                acc += bLit * mix(uLocalFloor, 1.0, sh); spec += bSpc * sh;
            }
            return acc;
        }
        // The pre-#674 signature: an unbaked receiver, every light live.
        vec3 CxClusteredLight(vec3 worldPos, vec3 N, vec3 V, float viewDepth, float onMover,
                                float rough, float specularity, out vec3 spec)
        {
            vec3 sub;
            return CxClusteredLightLm(worldPos, N, V, viewDepth, onMover, rough, specularity, 0.0, spec, sub);
        }

        // The diffuse half alone, for <c>/alloy viz 1</c> and anything that has no material in hand.
        vec3 CxClusteredDiffuse(vec3 worldPos, vec3 N, float viewDepth)
        {
            vec3 spec;
            return CxClusteredLight(worldPos, N, N, viewDepth, 0.0, 1.0, 0.0, spec);
        }

        /// Darkest local-cube term over this fragment's cluster -- white lit, black fully occluded, and
        /// exactly 1.0 where no light in reach carries a cube. <c>/alloy viz 5</c>. The composite cannot answer
        /// "is the cube occluding anything?" because a lit neighbour washes the term out; this can.
        float CxLocalShadowViz(vec3 worldPos, vec3 N, float viewDepth)
        {
            uint cl = CxCluster(viewDepth);
            uint n = min(uClCount[cl], CX_CLUSTER_CAP);
            uint base = cl * CX_CLUSTER_CAP;
            float vis = 1.0;
            for (uint k = 0u; k < n; k++)
            {
                uint li = uClIdx[base + k];
                vec4 pr = uLights[li * 2u];
                int slot = int(uLights[li * 2u + 1u].w);
                if (slot >= 128) slot -= 256;   // #674: the static bit
                if (slot < 0) continue;
                vec3 d = pr.xyz - worldPos;
                if (dot(d, d) > pr.w * pr.w) continue;
                vis = min(vis, CxLocalShadow(slot, worldPos, N, dot(N, normalize(d)), 0.0, viewDepth));
            }
            return vis;
        }
        """;

    /// <summary>The sun cascade receiver (#94), included by the lit fragment stage after
    /// <see cref="FragmentLib"/>. The maps come from <see cref="CasterPass"/>, which renders one
    /// concentric cascade per layer around the shadow focus and publishes the matrix each layer was actually
    /// drawn with -- so this only ever projects, never re-derives.
    /// <para>Selection is "the first cascade whose box holds the fragment", innermost first: with concentric
    /// fits every cascade is contained in the next, so the first hit is also the sharpest one available. No
    /// split distances, no view depth, and nothing that depends on the camera pose.</para>
    /// <para><c>uSunVp[4]</c> is pinned against <c>CasterPass.MaxCascades</c> by that class's SelfCheck --
    /// GLSL cannot size it from the C# constant, so the check is what keeps the two honest.</para></summary>
    internal const string SunShadowLib = """
        uniform sampler2DArrayShadow uSunShadow;
        uniform mat4 uSunVp[4];
        uniform vec4 uSunTexel;      // world units per shadow texel, per cascade
        uniform vec4 uSunBias;       // depth bias in DEVICE units, per cascade (world bias / that fit's range)
        uniform int uSunCascades;    // 0 = no cascades to sample this frame (interior, vanilla, not yet drawn)

        // #306: the sun's DYNAMIC half -- movers only, re-baked every frame with the SAME per-cascade fit
        // matrix as the static layer, composed by min(): the cube pool's own rule (#305), so the receiver
        // has one composition rule, not two. uSunDyn[i] is 1 when layer i holds a fresh dynamic bake this
        // frame and 0 otherwise -- a program that never sets it reads 0 everywhere and degrades to
        // static-only, exactly as uLocalDynPage does.
        uniform sampler2DArrayShadow uSunDynShadow;
        uniform vec4 uSunDyn;

        // Cascade edge kept clear of the PCF kernel: a fragment whose taps would straddle the border falls
        // through to the next cascade instead of sampling past the map and reading "unshadowed".
        const float CX_SUN_BORDER = 0.985;
        // #308 follow-up (the "scanline"): the cascade handoff is a 4x jump in texel size, offset and bias,
        // so the shadow visibly re-draws along the selection boundary -- and under the latched fits that
        // boundary leaps several units at every re-latch. The fix is a cross-fade to the next cascade whose
        // weight is a pure function of the fragment's distance from the LIVE fit focus -- never anything
        // latched: a weight keyed on the latched fit jumps at every re-latch, which reads as the whole band
        // flickering while walking. Fade runs from CX_SUN_FADE_LO to CX_SUN_FADE_HI of the cascade's
        // radius; full handover at 90% keeps the weight saturated before the latched fit (drift <= 5% of
        // the radius, CasterPass.SunFitSlack) can run out under the fragment. Both uniforms unset (a stale
        // content override) read 0, and a zero radius disables the blend -- the pre-fade hard handoff.
        const float CX_SUN_FADE_LO = 0.70;
        const float CX_SUN_FADE_HI = 0.90;
        uniform vec3 uSunFocus;      // the LIVE fit focus this frame
        uniform vec4 uSunRadius;     // per-cascade fit radius
        // Shadow-accuracy pass: offsets halved from #308 (see LocalShadowLib); csmbias default halved with them.
        // Sample point pushed along the normal by this many texels. Normal offset does what a depth bias
        // cannot: it scales with the texel a slope actually spans, so acne goes without the detached-shadow
        // peter-panning a bias big enough for grazing light would cost.
        const float CX_SUN_NORMAL_OFFSET = 1.0;
        // #308 follow-up (acne): the constant offset above is sized for a face the light hits square-on. A
        // face lit at grazing incidence spans tan(theta) texels of DEPTH per texel of map, so the terminator
        // marches across it in texel-sized stair steps unless the offset grows with the slope. Extra texels
        // of push per unit of tan(theta), and the clamp that keeps a fully edge-on face (tan -> inf) from
        // being lifted into the next caster over.
        const float CX_SUN_SLOPE_OFFSET = 0.75;
        const float CX_SUN_SLOPE_MAX = 2.0;
        // #148: 0 = this fragment is a SURFACE, and the normal offset above is the acne fight it needs.
        // 1 = it is a point inside a VOLUME -- a particle -- where the offset is not merely useless but
        // actively harmful: a billboard's normal faces the CAMERA, not away from any geometry, so offsetting
        // along it walks the sample point sideways across the shadow map's texel grid and turns a clean
        // lookup into bands. A volume has no surface to lift off; the constant bias below is the whole of
        // what it needs.
        // Unset reads 0, which is the surface behaviour every existing program and every content override
        // already relies on -- the safe default is the one nobody has to declare.
        uniform float uSunMedia;

        // The fragment in cascade i's NDC, sampled from a point pushed along the normal by that cascade's
        // texel-scaled acne offset -- so each cascade tests the surface it can actually resolve.
        vec3 CxSunNdc(int i, vec3 worldPos, vec3 N, float slope)
        {
            vec4 clip = uSunVp[i] * vec4(worldPos + N * (uSunTexel[i]
                            * (CX_SUN_NORMAL_OFFSET * (1.0 - uSunMedia)
                               + CX_SUN_SLOPE_OFFSET * slope)), 1.0);
            return clip.xyz / clip.w;
        }

        // One cascade's answer: the four-tap half-texel cross on the static layer, min-composed with the
        // same kernel on the dynamic layer when that layer is fresh (#306) -- matching kernels, because the
        // dynamic half is the player's own shadow and a harder edge on exactly that shadow is the
        // regression the split must not have. The depth bias grows on the slope: the stored depth inside
        // one grazing texel varies by tan(theta) texels' worth of world depth, so the flat bias under-covers it.
        float CxSunTaps(int i, vec3 ndc, float slope, vec2 texel)
        {
            vec2 uv = ndc.xy * 0.5 + 0.5;
            float layer = float(i);
            float ref = ndc.z * 0.5 + 0.5 - uSunBias[i] * (1.0 + slope);
            vec2 o = texel * 0.5;
            float vs = 0.25 * (texture(uSunShadow, vec4(uv + vec2(-o.x, -o.y), layer, ref))
                             + texture(uSunShadow, vec4(uv + vec2( o.x, -o.y), layer, ref))
                             + texture(uSunShadow, vec4(uv + vec2(-o.x,  o.y), layer, ref))
                             + texture(uSunShadow, vec4(uv + vec2( o.x,  o.y), layer, ref)));
            float vd = 1.0;
            if (uSunDyn[i] > 0.5)
                vd = 0.25 * (texture(uSunDynShadow, vec4(uv + vec2(-o.x, -o.y), layer, ref))
                           + texture(uSunDynShadow, vec4(uv + vec2( o.x, -o.y), layer, ref))
                           + texture(uSunDynShadow, vec4(uv + vec2(-o.x,  o.y), layer, ref))
                           + texture(uSunDynShadow, vec4(uv + vec2( o.x,  o.y), layer, ref)));
            return min(vs, vd);
        }

        // Sun visibility at a world point, 0 (fully shadowed) .. 1 (lit). Four hardware-PCF fetches on a
        // half-texel cross: each fetch is itself a bilinear 2x2 comparison, so the kernel is a 4x4 filter for
        // the price of four samples and the edge is soft without any per-pixel loop.
        // `ndl` gates: the lit stage passes its WRAPPED value so a wrap-lit terminator fragment can still be
        // shadowed. `ndlRaw` is the geometric dot for the slope terms -- the wrap inflates it exactly where
        // grazing incidence needs the extra bias, so the two must stay separate arguments.
        float CxSunShadow(vec3 worldPos, vec3 N, float ndl, float ndlRaw)
        {
            if (uSunCascades <= 0 || ndl <= 0.0) return 1.0;
            vec2 texel = 1.0 / vec2(textureSize(uSunShadow, 0).xy);
            // tan of the light-to-normal angle, the media gate folded in: a particle's camera-facing normal
            // makes the slope meaningless, so a volume keeps the constant bias alone as before. Zero where
            // the raw dot is not positive: those fragments are lit only by the WRAP, have no sun-facing
            // surface to grow acne on, and a slope pinned at max there punches the bias through doors and
            // thin casters exactly inside the band the wrapped gate exists to keep shadowable.
            float slope = ndlRaw <= 0.0 ? 0.0
                          : min(sqrt(max(1.0 - ndlRaw * ndlRaw, 0.0)) / max(ndlRaw, 0.1), CX_SUN_SLOPE_MAX)
                            * (1.0 - uSunMedia);
            for (int i = 0; i < uSunCascades; i++)
            {
                vec3 ndc = CxSunNdc(i, worldPos, N, slope);
                if (any(greaterThan(abs(ndc.xy), vec2(CX_SUN_BORDER))) || abs(ndc.z) > 1.0) continue;
                float v = CxSunTaps(i, ndc, slope, texel);
                // The handoff gradient: fade toward the next cascade's answer over a world-space band tied
                // to the live focus, so the 4x resolution/bias jump never draws a line and never jumps.
                float R = uSunRadius[i];
                float f = R > 0.0
                          ? clamp((distance(worldPos, uSunFocus) - CX_SUN_FADE_LO * R)
                                  / ((CX_SUN_FADE_HI - CX_SUN_FADE_LO) * R), 0.0, 1.0)
                          : 0.0;
                if (f > 0.0 && i + 1 < uSunCascades)
                {
                    vec3 n2 = CxSunNdc(i + 1, worldPos, N, slope);
                    if (all(lessThan(abs(n2.xy), vec2(CX_SUN_BORDER))) && abs(n2.z) <= 1.0)
                        v = mix(v, CxSunTaps(i + 1, n2, slope, texel), f);
                }
                return v;
            }
            return 1.0;   // past the outermost cascade: no shadow information, and lit is the safe answer
        }

        """;

    /// <summary>Resref the cluster build is looked up under before the GLSL below is used (#92). An override
    /// may carry the same <c>$CAP$</c>/<c>$GROUP$</c> tokens the embedded source does -- see
    /// <see cref="ShaderResource.Tokens"/> -- because the CPU sizes its buffers from those same constants.</summary>
    internal const string ShdClusterCs = "cx_cluster_cs";

    /// <summary>The cluster build. One invocation per froxel: reconstruct the froxel's eye-space AABB from the
    /// projection diagonal (x = ndc.x * depth / P00), then a sphere-vs-AABB test per light. O(clusters *
    /// lights) with no CPU involvement -- 3456 * ~120 tests is nothing for the GPU and, unlike the engine's
    /// CPU prioritizer, its cost does not grow with draw count.</summary>
    internal const string BuildSrc = """
        #version 460 core
        layout(local_size_x = $GROUP$) in;
        layout(std430, binding = 0) readonly buffer LightBuf { vec4 uLights[]; };
        layout(std430, binding = 1) writeonly buffer ClusterCount { uint uClCount[]; };
        layout(std430, binding = 2) buffer ClusterIdx { uint uClIdx[]; };
        layout(std430, binding = 3) buffer Stats { uint uOverflow; };
        uniform mat4 uView;
        uniform vec2 uProjXY;
        uniform vec2 uZ;
        uniform ivec3 uGrid;
        uniform int uLightCount;
        uniform float uReach;
        uniform int uBinCap;
        uniform vec2 uAtten;
        const uint CAP = $CAP$u;
        const uint SORT = $SORT$u;

        void main()
        {
            uint id = gl_GlobalInvocationID.x;
            if (id >= uint(uGrid.x * uGrid.y * uGrid.z)) return;
            int gx = int(id) % uGrid.x;
            int gy = (int(id) / uGrid.x) % uGrid.y;
            int gz = int(id) / (uGrid.x * uGrid.y);

            float lz = log(uZ.y / uZ.x);
            float d0 = uZ.x * exp(lz * (float(gz) / float(uGrid.z)));
            float d1 = uZ.x * exp(lz * (float(gz + 1) / float(uGrid.z)));
            float x0 = -1.0 + 2.0 * float(gx) / float(uGrid.x), x1 = -1.0 + 2.0 * float(gx + 1) / float(uGrid.x);
            float y0 = -1.0 + 2.0 * float(gy) / float(uGrid.y), y1 = -1.0 + 2.0 * float(gy + 1) / float(uGrid.y);

            vec3 lo = vec3(1e30), hi = vec3(-1e30);
            for (int c = 0; c < 8; c++)
            {
                float d = (c & 4) == 0 ? d0 : d1;
                vec3 p = vec3(((c & 1) == 0 ? x0 : x1) * d / uProjXY.x,
                              ((c & 2) == 0 ? y0 : y1) * d / uProjXY.y,
                              -d);
                lo = min(lo, p); hi = max(hi, p);
            }

            // #336: walk order, unsorted. A per-froxel order for the receiver's tap cap put the cap's seam
            // on the cluster grid (screen-aligned squares wherever four cube lights overlapped); the cap
            // chooses per FRAGMENT now, so the list needs no order.
            uint n = 0u;
            uint base = id * CAP;
            uint dropped = 0u;
            // EXPERIMENT (/light bincap): keep only the K strongest, by the engine's curve at the froxel's
            // nearest point times luma -- the ordering the per-fragment body would find, made once per froxel.
            uint K = uBinCap <= 0 ? CAP : min(uint(uBinCap), SORT);
            float sc[SORT];
            vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
            for (int i = 0; i < uLightCount; i++)
            {
                vec4 pr = uLights[i * 2];
                float r = pr.w * uReach;
                vec3 e = (uView * vec4(pr.xyz, 1.0)).xyz;
                vec3 dv = e - clamp(e, lo, hi);
                float dd = dot(dv, dv);
                if (dd > r * r) continue;
                if (uBinCap <= 0)
                {
                    if (n >= CAP) { dropped = 1u; break; }
                    uClIdx[base + n] = uint(i);
                    n++;
                    continue;
                }
                float f2 = dd / (pr.w * pr.w);
                float s = (1.0 - f2) / (at.x + at.y * f2) * dot(uLights[i * 2 + 1].rgb, vec3(0.299, 0.587, 0.114));
                uint j;
                if (n < K) j = n++;
                else if (s > sc[K - 1u]) { j = K - 1u; dropped = 1u; }
                else { dropped = 1u; continue; }
                while (j > 0u && sc[j - 1u] < s)
                {
                    sc[j] = sc[j - 1u]; uClIdx[base + j] = uClIdx[base + j - 1u];
                    j--;
                }
                sc[j] = s; uClIdx[base + j] = uint(i);
            }
            uClCount[id] = n;
            if (dropped != 0u && uBinCap <= 0) atomicAdd(uOverflow, 1u);
        }
        """;
}
