using System;
using System.Globalization;
using System.Numerics;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

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
    /// <summary>Lights binned per cluster. A froxel is ~1/144th of the screen over one depth slice; 64 is far
    /// past what real content puts in one. <para>ponytail: fixed capacity, no per-cluster compaction pass.
    /// Overflow is counted and logged; if it ever fires, the upgrade is a two-pass count-then-fill build with
    /// a compacted offset table.</para></summary>
    internal const int ClusterCap = 64;
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
    private static float _gain = 0.1f, _srcRadius = 1f, _falloffPow = 1f;

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
    /// alone owns everything past the core. The shipped pair is that: body at the 0.10 the live A/B settled
    /// on, core at the full 2.44, over 15% of each light's range (about 3.5 units on a torch).</para>
    /// <para>0 disables it, which is #140's curve and is also what a content override (#92) that never
    /// declares <c>uHot</c> reads.</para></summary>
    private static float _hot = 0.9f, _hotSize = 0.15f;

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
    private static float _wrap = 1f;
    /// <summary>#143 wrapped diffuse: the distance-INDEPENDENT terminator softening, applied to the sun and
    /// as a floor under <see cref="_wrap"/> on every point light. 0 restores the punctual N.L everywhere.
    /// <para>This is what the community's GL 3.3 "light wrapping" shaders do, and the reason they exist:
    /// NWN's meshes are low-poly with smoothed vertex normals, so the terminator crosses a whole facet as
    /// one straight hard crease. Unlike <see cref="_wrap"/> it does NOT fall off with distance -- the
    /// facets are the same size however far away the light is.</para></summary>
    private static float _wrapN = 0.25f;
    /// <summary>#139: local-cube PCF radius, in cube texels at the sample distance. 0 = the single hardware
    /// PCF tap this shipped with; anything above widens the filter with four more taps on a disc
    /// perpendicular to the sample ray. Costs 4 extra cube fetches per shadowing light per fragment.</summary>
    private static float _shadowSoft = 1.5f;
    /// <summary>#139: self-shadow near-field skip, as a multiple of <see cref="_srcRadius"/>. A world point
    /// closer to the light than this is reported lit outright -- a carried torch sits INSIDE the hand and
    /// forearm holding it, and a punctual shadow test there shadows the carrier from its own light.</summary>
    private static float _shadowNear = 1f;
    /// <summary>Scales on the captured area terms, so daylight and interior fill can be dialled apart from
    /// the point lights. 1.0 = the engine's own captured value, untouched.</summary>
    internal static float AmbientScale = 1f, SunScale = 1f;
    /// <summary>How much of an OVERBRIGHT light's surplus is kept as CENTRE INTENSITY, on top of the reach
    /// the engine already spent it on. Applied by the feed as <c>1 + (maxChannel - 1) * this</c>.
    /// <para>Overbright IS the carried-light discriminator and needs no other: module-placed lights are
    /// authored at max channel &lt;= 1 and have no surplus at all, while a torch ships at
    /// rgb=(1.75, 1.50, 1.00). That matters because seeing is a mechanical advantage -- a torch or a Light
    /// spell has to be worth carrying -- and placed lights are mostly decor.</para>
    /// <para>0 = the engine's own behaviour exactly: <c>GetLightAdjustedColor</c> normalises the hue and
    /// <c>GetLightAdjustedRadius</c> spends the whole surplus on radius, so an overbright light is WIDER and
    /// no brighter. 1 = the authored surplus in full at the centre as well. The shipped 0.5 is a taste
    /// value and nothing more: half of it, which reads as "this light is special" without flattening the
    /// carrier into white.</para>
    /// <para>#140 brought this down from 120. That number was not a lighting decision -- it was propping up
    /// a range-blind <c>1/d^4</c> curve under which no honest value reached anything, and it is what made a
    /// torch-bearer 210x brighter than any placed light at the same distance. The curve is fixed; the cope
    /// goes with it.</para></summary>
    internal static float OverbrightScale = 1f;
    /// <summary>Boot values, kept so <c>/light reset</c> returns to "env vars as launched" rather than to the
    /// hardcoded defaults -- otherwise a reset would silently discard the launch config.</summary>
    private static float _bootGain = 0.1f, _bootRadius = 1f, _bootPow = 1f,
                         _bootAmbient = 1f, _bootSun = 1f, _bootOver = 1f;
    private static float _bootHot = 0.9f, _bootHotSize = 0.15f, _bootWrapN = 0.25f;
    private static float _bootWrap = 1f, _bootShadowSoft = 0f, _bootShadowNear = 1f;

    /// <summary>Current tuning, one line, for the <c>/light</c> echo and the arm-time log.</summary>
    internal static string Describe() =>
        $"gain {_gain:F2}  wrapradius {_srcRadius:F2}  rimfade {_falloffPow:F2}  " +
        $"atten(mi {_atten.X:F3} ff {_atten.Y:F3}, peak {(_atten.X > 0f ? 1f / _atten.X : 0f):F2})  " +
        $"ambient x{AmbientScale:F2}  sun x{SunScale:F2}  overbright x{OverbrightScale:F2}  " +
        $"wrap {_wrap:F2}  wrapndl {_wrapN:F2}  shadowsoft {_shadowSoft:F2}  shadownear {_shadowNear:F2}  " +
        $"hot {_hot:F2}  hotsize {_hotSize:F3}";

    /// <summary>Live tuning setter behind <c>/light</c>. Returns the old value, or NaN for an unknown key.
    /// <para>Written from the input thread and read by the render thread next frame. That is safe WITHOUT a
    /// lock only because each is an independent float: the shader re-reads them per upload, and a frame that
    /// catches one changed and one not is a single frame of a value the user is dragging anyway.</para></summary>
    internal static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            case "gain":    old = _gain; _gain = v; break;
            case "radius":  old = _srcRadius; _srcRadius = v; break;
            case "falloff": old = _falloffPow; _falloffPow = v; break;
            case "ambient": old = AmbientScale; AmbientScale = v; break;
            case "sun":     old = SunScale; SunScale = v; break;
            case "overbright": old = OverbrightScale; OverbrightScale = v; break;
            case "wrap":    old = _wrap; _wrap = v; break;
            case "wrapndl": old = _wrapN; _wrapN = v; break;
            case "shadowsoft": old = _shadowSoft; _shadowSoft = v; break;
            case "shadownear": old = _shadowNear; _shadowNear = v; break;
            case "hot":     old = _hot; _hot = v; break;
            case "hotsize": old = _hotSize; _hotSize = v; break;
            default: return float.NaN;
        }
        _log?.Info($"AlloyLights: /light {key} {v:F2} (was {old:F2}) -> {Describe()}");
        return old;
    }

    /// <summary>Headless check of the /light contract: every documented knob is reachable, an unknown one is
    /// reported rather than silently ignored, and reset returns the BOOT values (not the hardcoded defaults,
    /// which would quietly discard a launch config). Runs off-device; restores whatever it touched.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        // #175: the check DRIVES the tuning path, and tuning logs a line each. Silenced for the duration --
        // it used to be quiet only because Attach had not run yet and _log was still null, which is not a
        // property to depend on: the pass roll-call attaches every pass before it checks any of them.
        ExtenderLog? quiet = _log; _log = null;
        // The shader's curve, in C#, so the property checks below are about the SHAPE and not about a
        // second transcription of it: f2 in [0, 1], gain 1, rim-fade exponent 1.
        float Att(float f2, float gain) => gain * (1f - f2) / (_atten.X + _atten.Y * f2);
        float g = _gain, r = _srcRadius, p = _falloffPow, am = AmbientScale, su = SunScale, ov = OverbrightScale;
        float wr = _wrap, ss = _shadowSoft, sn = _shadowNear, ht = _hot, hs = _hotSize, wn = _wrapN;
        float bg = _bootGain, br = _bootRadius, bp = _bootPow, ba = _bootAmbient, bs = _bootSun, bo = _bootOver;

        _bootGain = 40f; _bootRadius = 0.7f; _bootPow = 2f; _bootAmbient = 1f; _bootSun = 1f; _bootOver = 1f;

        bool ok = float.IsNaN(Tune("nope", 1f));                       // unknown knob is reported
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"AlloyLights.SelfCheck FAIL: {w}"); } }
        foreach (var key in new[] { "gain", "radius", "falloff", "ambient", "sun", "overbright",
                                    "wrap", "wrapndl", "shadowsoft", "shadownear", "hot", "hotsize" })
            ok &= !float.IsNaN(Tune(key, 3f));                          // every documented knob is reachable
        ok &= _gain == 3f && _srcRadius == 3f && _falloffPow == 3f && AmbientScale == 3f && SunScale == 3f
              && OverbrightScale == 3f && _wrap == 3f && _shadowSoft == 3f && _shadowNear == 3f
              && _hot == 3f && _hotSize == 3f && _wrapN == 3f;
        _bootGain = 7f;                                                 // reset must honour the BOOT value...
        ResetTuning();
        ok &= _gain == 7f && _srcRadius == 0.7f && AmbientScale == 1f;  // ...not the 40f literal
        ok &= Describe().Contains("gain") && Describe().Contains("sun") && Describe().Contains("overbright")
              && Describe().Contains("wrap") && Describe().Contains("shadowsoft")
              && Describe().Contains("shadownear") && Describe().Contains("hot")
              && Describe().Contains("wrapndl");

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
        // _bootOver with the rest, and NOT because of symmetry: the clobber above used to be repaired by
        // Attach re-latching it, and #175 put Attach FIRST. Miss it and /light reset snaps overbright to 1
        // and silently discards a tuned overbright -- which is the very property this check asserts.
        // The restore is CHECKED, not trusted. #175 turned a missed one into a live bug the moment Attach
        // stopped running after this and quietly repairing it, and a seventh boot knob added later would do
        // it again with nothing to say so. The sentinel is what makes the check bite: clobbering with the
        // DEFAULT (as the block above does for ambient/sun/overbright) is indistinguishable from a correct
        // restore, so the assertion has to start from a value no boot value can be.
        const float Sentinel = -12345f;
        _bootGain = Sentinel; _bootRadius = Sentinel; _bootPow = Sentinel;
        _bootAmbient = Sentinel; _bootSun = Sentinel; _bootOver = Sentinel;
        _bootGain = bg; _bootRadius = br; _bootPow = bp; _bootAmbient = ba; _bootSun = bs; _bootOver = bo;
        // Asked through ResetTuning and Describe, NOT by re-listing the fields: an assertion written from the
        // same list as the restore passes whatever that list forgets. Every boot value reaches a live knob
        // through reset, and Describe prints every live knob, so a missed one surfaces as its own sentinel.
        ResetTuning();
        E(!Describe().Contains("-12345"),
          "the check restores EVERY boot value it clobbered -- a missed one makes /light reset discard a tuned value");

        _gain = g; _srcRadius = r; _falloffPow = p; AmbientScale = am; SunScale = su; OverbrightScale = ov;
        _wrap = wr; _shadowSoft = ss; _shadowNear = sn; _hot = ht; _hotSize = hs; _wrapN = wn;
        _log = quiet;
        log.Info(ok ? "AlloyLights.SelfCheck: passed"
                    : "AlloyLights.SelfCheck: FAILED -- /light knob table drifted from its documented keys");
        return ok;
    }

    /// <summary>Restore the values this session launched with (env vars included).</summary>
    internal static void ResetTuning()
    {
        _gain = _bootGain; _srcRadius = _bootRadius; _falloffPow = _bootPow;
        AmbientScale = _bootAmbient; SunScale = _bootSun; OverbrightScale = _bootOver;
        _wrap = _bootWrap; _shadowSoft = _bootShadowSoft; _shadowNear = _bootShadowNear;
        _hot = _bootHot; _hotSize = _bootHotSize; _wrapN = _bootWrapN;
        _log?.Info($"AlloyLights: /light reset -> {Describe()}");
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
    private static int _locView = -1, _locProjXY = -1, _locZ = -1, _locCount = -1, _locGrid = -1;
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
        // #179 (ADR 0013): the thirteen light dials are pure look, so the code default IS the boot value and
        // /light is the whole surface. The env block that used to sit here only ever set a value the command
        // could already set, and could not be moved without a relaunch.
        _bootHot = _hot; _bootHotSize = _hotSize; _bootWrapN = _wrapN;
        _bootWrap = _wrap; _bootShadowSoft = _shadowSoft; _bootShadowNear = _shadowNear;
        _bootGain = _gain; _bootRadius = _srcRadius; _bootPow = _falloffPow;
        _bootAmbient = AmbientScale; _bootSun = SunScale; _bootOver = OverbrightScale;
        log.Info($"AlloyLights: {Describe()} -- /light retunes every dial live, /light reset restores these");
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
        fixed (float* s = _stage)
        {
            for (int i = 0; i < n; i++)
            {
                Vector4 pr = FrameInputs.AreaLightPosRange[i];
                Vector3 c = FrameInputs.AreaLightColor[i];
                float* d = s + i * 8;
                d[0] = pr.X; d[1] = pr.Y; d[2] = pr.Z; d[3] = pr.W;
                d[4] = c.X; d[5] = c.Y; d[6] = c.Z;
                // the record's spare w is this light's shadow-cube slot (#95), -1 for a light with no cube.
                // Riding the existing pad is what keeps the cube receiver from needing a second buffer keyed
                // the same way -- and the walk already resolved the slot by light identity.
                d[7] = FrameInputs.AreaLightSlot[i];
            }
            Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _bufLights);
            Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, (IntPtr)(n * 8 * sizeof(float)), s);
        }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        _uploaded = n;
        LastLightCount = n;
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
        // being handed back to the engine for want of a torch.
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
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindLights, _bufLights);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCounts, _bufCounts);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindIndices, _bufIndices);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindStats, _bufStats);
        Gl.DispatchCompute((ClusterCount + GroupSize - 1) / GroupSize, 1, 1);
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT);

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
                                         int locAtten, int locHot, int locWrapN, int* viewport,
                                         float near, float far)
    {
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
    }

    /// <summary>#139: the local-cube receiver's two softness terms -- PCF radius in texels, and the world
    /// distance inside which a fragment is reported lit outright. Read here so <c>/light</c> retunes both
    /// live, like every other knob.</summary>
    internal static float ShadowSoft => _shadowSoft;
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
        $"{FrameInputs.LightsWithCube} with a cube)" +
        $", cluster build {LastBuildMs:F3}ms on {GridX}x{GridY}x{GridZ}" +
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
        uniform vec2 uLocalSoft;

        // Sample point pushed along the normal by this many cube texels -- the same acne fight the sun
        // cascades run, scaled by the texel a face actually spans at this distance rather than a constant.
        const float SOWX_LOCAL_NORMAL_OFFSET = 2.0;
        // Depth bias in WORLD units along the face's major axis, applied before the projection so it stays a
        // constant distance whatever the light's reach is.
        const float SOWX_LOCAL_BIAS = 0.05;
        // #148: 0 = this fragment is a SURFACE, and the normal offset above is the acne fight it needs.
        // 1 = it is a point inside a VOLUME -- a particle -- where the offset is not merely useless but
        // actively harmful: a billboard's normal faces the CAMERA, not away from any geometry, so offsetting
        // along it walks the sample point sideways across the shadow map's texel grid and turns a clean
        // lookup into bands. A volume has no surface to lift off; the constant bias below is the whole of
        // what it needs.
        // Unset reads 0, which is the surface behaviour every existing program and every content override
        // already relies on -- the safe default is the one nobody has to declare.
        uniform float uLocalMedia;

        // Visibility of world point `worldPos` from the light in cube slot `slot`, 0 (shadowed) .. 1 (lit).
        // The reference depth is the exact inverse of ShadowMath.LinearizeFaceDepth: for a 90deg face fitted
        // to [near, far], the window depth stored at major-axis distance m is far/(far-near) * (1 - near/m).
        // ShadowMath's SelfCheck welds that round-trip headless, so a convention slip fails there, not here.
        float SowxLocalShadow(int slot, vec3 worldPos, vec3 N)
        {
            if (uLocalCubes <= 0 || slot < 0 || slot >= uLocalCubes) return 1.0;
            vec4 L = uLocalLight[slot];
            float far = L.w, near = uLocalPlane.x;
            vec3 d = worldPos - L.xyz;
            float m = max(max(abs(d.x), abs(d.y)), abs(d.z));
            // outside the recorded shell there is no depth to compare against, and lit is the safe answer
            if (far <= near || m <= near + SOWX_LOCAL_BIAS || m >= far) return 1.0;
            // #139: inside the source sphere there is nothing to occlude. A carried torch sits INSIDE the
            // hand and forearm holding it, so a punctual test there shadows the carrier from its own light
            // -- and at that range one cube texel spans most of the arm, which is the blocky self-shadow.
            if (m < uLocalSoft.y) return 1.0;
            float texel = 2.0 * m * uLocalPlane.y;
            d += N * (texel * SOWX_LOCAL_NORMAL_OFFSET * (1.0 - uLocalMedia));
            // the bias moves the compare point TOWARD the light, never away: the test is `ref <= stored` =
            // lit, and a fragment lying on its own recorded surface stores exactly its own depth. Pushing
            // the reference the other way makes every lit surface shadow itself.
            m = clamp(max(max(abs(d.x), abs(d.y)), abs(d.z)) - SOWX_LOCAL_BIAS, near, far);
            float ref = far / (far - near) * (1.0 - near / m);
            float v = texture(uLocalShadow, vec4(d, float(slot)), ref);
            if (uLocalSoft.x <= 0.0) return v;
            // #139: four more taps on a disc perpendicular to the sample ray. The cube fetch is already
            // hardware PCF, so this WIDENS that filter rather than replacing it. The reference depth is
            // deliberately the centre tap's for all five -- re-deriving it per tap would compare each
            // neighbour against its own surface and dissolve the penumbra it is there to create.
            // ponytail: a fixed 4-tap cross, not a rotated Poisson disc and not contact-hardening. Costs 4
            // cube fetches per shadowing light; go to a jittered kernel if the cross pattern ever shows.
            vec3 axis = abs(d.x) < 0.9 * m ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
            vec3 du = normalize(cross(axis, d)) * (texel * uLocalSoft.x);
            vec3 dv = normalize(cross(d, du)) * (texel * uLocalSoft.x);
            v += texture(uLocalShadow, vec4(d + du, float(slot)), ref);
            v += texture(uLocalShadow, vec4(d - du, float(slot)), ref);
            v += texture(uLocalShadow, vec4(d + dv, float(slot)), ref);
            v += texture(uLocalShadow, vec4(d - dv, float(slot)), ref);
            return v * 0.2;
        }

        """;

    internal const string FragmentLib = """
        layout(std430, binding = 0) readonly buffer SowxLightBuf { vec4 uLights[]; };
        layout(std430, binding = 1) readonly buffer SowxClusterCount { uint uClCount[]; };
        layout(std430, binding = 2) readonly buffer SowxClusterIdx { uint uClIdx[]; };
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
        // #139: sphere-light wrap, as a multiple of uFalloff.y. 0 (the value an override that never declares
        // it leaves behind) is the punctual N.L this shipped with.
        uniform float uWrap;
        // #143: the distance-INDEPENDENT wrap floor -- a separate uniform rather than a second lane in
        // uWrap, so a stale override that only declares `float uWrap` still links and still gets #139's
        // behaviour. 0 = off, which is what such an override leaves behind.
        uniform float uWrapN;
        const uint SOWX_CLUSTER_CAP = $CAP$u;
        // The engine's own sRGB->linear conversion (Ghidra RenderInterface::GetColorSpaceFromSRGB: powf(c,
        // 2.2) per channel, identity at 0 and 1). Textures and material colours both arrive non-linear --
        // the stock shaders apply the same curve via ApplyColorSpace before lighting them.
        // ponytail: unconditional. The engine gates it on GetGlobalShaderParami(0), the gamma pipeline the
        // ship config runs and the shipped .shd assume (GAMMA_CORRECTION 1); plumb that param through if a
        // legacy-pipeline build ever needs the Alloy path.
        vec3 SowxLinear(vec3 c) { return pow(max(c, vec3(0.0)), vec3($SRGB$)); }

        // #143: energy-conserving wrapped diffuse. NWN's meshes are low-poly with smoothed vertex normals,
        // so a punctual terminator lands mid-facet as a straight hard crease -- the artifact the community's
        // GL 3.3 "light wrapping" shaders exist to hide. Widening the band by `w` and dividing the result by
        // (1 + w) is the standard half-Lambert generalisation: the PEAK is unchanged, so a fully lit surface
        // is bit-identical to the punctual one and only the band around the terminator moves.
        // ponytail: one global wrap for every material. The modern refinement is pre-integrated skin
        // (Penner 2011) -- a curvature-driven terminator LUT -- but it is per-material by nature and NWN
        // carries no "this part is skin" tag; on armour and stone it reads wrong. Tag materials first.
        float SowxWrap(float ndl, float w)
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
        // subframes do not all render at (0,0).
        uint SowxCluster(float viewDepth)
        {
            int gx = clamp(int((gl_FragCoord.x - uTile.x) / uTile.z), 0, uGrid.x - 1);
            int gy = clamp(int((gl_FragCoord.y - uTile.y) / uTile.w), 0, uGrid.y - 1);
            float k = log(max(viewDepth, uZ.x) / uZ.x) / log(uZ.y / uZ.x);
            int gz = clamp(int(k * float(uGrid.z)), 0, uGrid.z - 1);
            return uint(gx + uGrid.x * (gy + uGrid.y * gz));
        }

        // fAngleMin (inc_lighting.shd:88): the floor the engine clamps N.V to before the view-side
        // normalisation divides by it.
        const float SOWX_ANGLE_MIN = 0.01;

        // Schlick's Fresnel, the pow-5 form the ship quality mode takes (inc_lighting.shd GetFresnel,
        // SHADER_QUALITY_MODE > 1; the lower modes use pow 4).
        float SowxFresnel(float c)
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
        // normalisation in SowxSpecBase, which is common to every light and applied once (the engine's
        // fSpecularBaseModifier). fSpecularity rides in through the Fresnel term and must NOT be multiplied
        // in again -- inc_lighting.shd:600-602 scales by it only when SPECULAR_FRESNEL != 1.</para>
        // Returns the lobe per unit IRRADIANCE -- the caller has already folded N.L and attenuation into the
        // light term it multiplies this by.
        // ponytail: the low-quality branches (Blinn-Phong, no shadowing, the simplified Fresnel) are not
        // reproduced. Add them if a device that cannot run the ship mode ever needs to match its own vanilla.
        float SowxSpecLobe(vec3 N, vec3 V, vec3 L, float rough, float spec)
        {
            vec3 H = normalize(V + L);
            float NdotH = clamp(dot(N, H), 0.0, 1.0);
            float VdotH = clamp(dot(H, V), 0.0, 1.0);
            float NdotL = clamp(dot(N, L), 0.0, 1.0);
            float a2 = rough * rough;
            // GGX distribution minus its alpha^2 numerator, which lives in SowxSpecBase
            float den = 1.0 - NdotH * NdotH * (1.0 - a2);
            float d = 1.0 / max(den * den, 1e-8);
            d *= 1.0 / mix(NdotL, 1.0, rough);          // geometric shadowing, light side
            return d * mix(spec, 1.0, SowxFresnel(VdotH));
        }

        // The half that does not vary per light: alpha^2/4 and the view-side shadowing term.
        float SowxSpecBase(float NdotV, float rough)
        {
            return rough * rough * 0.25 / mix(max(NdotV, SOWX_ANGLE_MIN), 1.0, rough);
        }

        // Diffuse AND specular contribution of every light binned into this fragment's cluster, in ONE walk:
        // both terms want the same per-light colour, attenuation, N.L and cube-shadow fetch, and running the
        // cluster twice would pay for all four again. World space throughout: the Alloy pass has world
        // positions and normals in hand, so there is no eye-space round trip.
        vec3 SowxClusteredLight(vec3 worldPos, vec3 N, vec3 V, float viewDepth,
                                float rough, float specularity, out vec3 spec)
        {
            spec = vec3(0.0);
            uint cl = SowxCluster(viewDepth);
            uint n = uClCount[cl];
            uint base = cl * SOWX_CLUSTER_CAP;
            vec3 acc = vec3(0.0);
            for (uint k = 0u; k < n; k++)
            {
                uint li = uClIdx[base + k];
                vec4 pr = uLights[li * 2u];
                vec4 cs = uLights[li * 2u + 1u];
                vec3 c0 = cs.rgb;
                // the record's spare w carries this light's cube slot (-1 = it casts no shadow this frame)
                int slot = int(cs.w);
                // pr.w is GetLightAdjustedRadius: the engine's own R, overbright surplus already spent
                // on it. Squared here because the engine's own cull is the same test on the same value
                // (SetLightGL @0x4c1007 uploads exactly R*R as lightQuadraticAtten).
                float rr = pr.w * pr.w;
                vec3 d = pr.xyz - worldPos;
                float dd = dot(d, d);
                if (dd > rr) continue;
                // The walk already divided an overbright colour down by its max channel (spending the
                // surplus on radius, as GetLightAdjustedColor does), so every channel is <= 1 here and the
                // plain sRGB curve is the right linearization.
                vec3 c = SowxLinear(c0);
                // #140: the ENGINE's curve, range-normalised -- inc_lighting.shd's ComputePointLightSource
                // at GAMMA_CORRECTION == 1, verbatim. The authored range IS the shape here, which is the
                // whole reason a dim cave glow and a torch can share one formula: the glow's R is small and
                // its colour dim, the torch's R is large (its overbright surplus went there), and neither
                // needs a global gain to rescue it. The rim fade reaches exactly 0 at f2 = 1, so no separate
                // cutoff window is needed and a light still cannot pop against its own cull sphere.
                float f2 = dd / rr;
                float fade = 1.0 - f2;
                // Uniform-valued branch, so it is coherent across the whole draw and free -- and at the
                // shipped 1.0 the pow is not merely cheap but exactly skipped, which is what makes "vanilla"
                // bit-exact rather than approximately right.
                if (uFalloff.z != 1.0) fade = pow(max(fade, 0.0), uFalloff.z);
                vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
                float att = uFalloff.x * fade / (at.x + at.y * f2);
                // #142: the near-field core -- a compact-support bump reaching uHot.y of the range and
                // exactly nothing past it, so the body above owns the whole tail and the gain that was
                // calibrated against vanilla cannot drift. Peak is uHot.x / at.x, so gain + hot = 1 puts
                // the very centre at the engine's own peak.
                if (uHot.x > 0.0)
                {
                    float t = max(1.0 - f2 / (uHot.y * uHot.y), 0.0);
                    att += uHot.x * fade * t * t / at.x;
                }
                float invD = inversesqrt(max(dd, 1e-8));
                vec3 L = d * invD;
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
                float ndl = SowxWrap(dot(N, L), max(s, uWrapN));
                if (ndl <= 0.0) continue;   // facing away: no light, and no reason to pay for a cube fetch
                float sh = SowxLocalShadow(slot, worldPos, N);
                vec3 lit = c * (max(att, 0.0) * ndl * sh);
                acc += lit;
                spec += lit * SowxSpecLobe(N, V, L, rough, specularity);
            }
            return acc;
        }

        // The diffuse half alone, for <c>/draw viz 1</c> and anything that has no material in hand.
        vec3 SowxClusteredDiffuse(vec3 worldPos, vec3 N, float viewDepth)
        {
            vec3 spec;
            return SowxClusteredLight(worldPos, N, N, viewDepth, 1.0, 0.0, spec);
        }

        /// Darkest local-cube term over this fragment's cluster -- white lit, black fully occluded, and
        /// exactly 1.0 where no light in reach carries a cube. <c>/draw viz 5</c>. The composite cannot answer
        /// "is the cube occluding anything?" because a lit neighbour washes the term out; this can.
        float SowxLocalShadowViz(vec3 worldPos, vec3 N, float viewDepth)
        {
            uint cl = SowxCluster(viewDepth);
            uint n = min(uClCount[cl], SOWX_CLUSTER_CAP);
            uint base = cl * SOWX_CLUSTER_CAP;
            float vis = 1.0;
            for (uint k = 0u; k < n; k++)
            {
                uint li = uClIdx[base + k];
                vec4 pr = uLights[li * 2u];
                int slot = int(uLights[li * 2u + 1u].w);
                if (slot < 0) continue;
                vec3 d = pr.xyz - worldPos;
                if (dot(d, d) > pr.w * pr.w) continue;
                vis = min(vis, SowxLocalShadow(slot, worldPos, N));
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

        // Cascade edge kept clear of the PCF kernel: a fragment whose taps would straddle the border falls
        // through to the next cascade instead of sampling past the map and reading "unshadowed".
        const float SOWX_SUN_BORDER = 0.985;
        // Sample point pushed along the normal by this many texels. Normal offset does what a depth bias
        // cannot: it scales with the texel a slope actually spans, so acne goes without the detached-shadow
        // peter-panning a bias big enough for grazing light would cost.
        const float SOWX_SUN_NORMAL_OFFSET = 2.0;
        // #148: 0 = this fragment is a SURFACE, and the normal offset above is the acne fight it needs.
        // 1 = it is a point inside a VOLUME -- a particle -- where the offset is not merely useless but
        // actively harmful: a billboard's normal faces the CAMERA, not away from any geometry, so offsetting
        // along it walks the sample point sideways across the shadow map's texel grid and turns a clean
        // lookup into bands. A volume has no surface to lift off; the constant bias below is the whole of
        // what it needs.
        // Unset reads 0, which is the surface behaviour every existing program and every content override
        // already relies on -- the safe default is the one nobody has to declare.
        uniform float uSunMedia;

        // Sun visibility at a world point, 0 (fully shadowed) .. 1 (lit). Four hardware-PCF fetches on a
        // half-texel cross: each fetch is itself a bilinear 2x2 comparison, so the kernel is a 4x4 filter for
        // the price of four samples and the edge is soft without any per-pixel loop.
        float SowxSunShadow(vec3 worldPos, vec3 N, float ndl)
        {
            if (uSunCascades <= 0 || ndl <= 0.0) return 1.0;
            vec2 texel = 1.0 / vec2(textureSize(uSunShadow, 0).xy);
            for (int i = 0; i < uSunCascades; i++)
            {
                vec4 clip = uSunVp[i] * vec4(worldPos + N * (uSunTexel[i] * SOWX_SUN_NORMAL_OFFSET
                                                               * (1.0 - uSunMedia)), 1.0);
                vec3 ndc = clip.xyz / clip.w;
                if (any(greaterThan(abs(ndc.xy), vec2(SOWX_SUN_BORDER))) || abs(ndc.z) > 1.0) continue;
                vec2 uv = ndc.xy * 0.5 + 0.5;
                float layer = float(i);
                float ref = ndc.z * 0.5 + 0.5 - uSunBias[i];
                vec2 o = texel * 0.5;
                return 0.25 * (texture(uSunShadow, vec4(uv + vec2(-o.x, -o.y), layer, ref))
                             + texture(uSunShadow, vec4(uv + vec2( o.x, -o.y), layer, ref))
                             + texture(uSunShadow, vec4(uv + vec2(-o.x,  o.y), layer, ref))
                             + texture(uSunShadow, vec4(uv + vec2( o.x,  o.y), layer, ref)));
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
        layout(std430, binding = 2) writeonly buffer ClusterIdx { uint uClIdx[]; };
        layout(std430, binding = 3) buffer Stats { uint uOverflow; };
        uniform mat4 uView;
        uniform vec2 uProjXY;
        uniform vec2 uZ;
        uniform ivec3 uGrid;
        uniform int uLightCount;
        const uint CAP = $CAP$u;

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

            uint n = 0u;
            uint base = id * CAP;
            uint dropped = 0u;
            for (int i = 0; i < uLightCount; i++)
            {
                vec4 pr = uLights[i * 2];
                float r = pr.w;
                vec3 e = (uView * vec4(pr.xyz, 1.0)).xyz;
                vec3 dv = e - clamp(e, lo, hi);
                if (dot(dv, dv) > r * r) continue;
                if (n >= CAP) { dropped = 1u; break; }
                uClIdx[base + n] = uint(i);
                n++;
            }
            uClCount[id] = n;
            if (dropped != 0u) atomicAdd(uOverflow, 1u);
        }
        """;
}
