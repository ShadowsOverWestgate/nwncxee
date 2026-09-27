using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The scene fog's TARGET COLOUR, and the terms that shape it (#341). The area author still owns the ramp --
/// <c>Scene+0x140..0x14c</c>'s enable/start/end and colour are read untouched by <see cref="FrameRead"/> and
/// the lerp stays linear in view depth -- so every knob here changes only what a fully-fogged fragment
/// converges TO, or a multiplier on how fast it gets there.
/// <para><b>Why.</b> Vanilla fog converges on one flat colour while the skybox above it stays blue, and the
/// seam between them is the horizon band the engine paints with the skyfade gob to hide exactly that.
/// Converging on the SKY removes the seam rather than covering it.</para>
/// <para><b>Where the sky colour comes from.</b> The Alloy sky pass already knows the dome's GL texture per
/// role (<see cref="AlloyRenderer.StageSky"/>), so the colour is a readback of that texture's smallest mip
/// -- the sampler's own average -- weighted by the role alpha the cross-fade is running at, so a cross-fade
/// to the night dome carries the fog with it. Not the <see cref="EnvProbe"/> cube, which contains no sky:
/// its faces are CLEARED to a sky stand-in. No sky staged (an interior, or a sky pass standing down) and BOTH
/// direction terms switch off: the fog is the area author's flat colour, exactly as vanilla.</para>
/// <para>ponytail: one colour for the whole dome, not a gradient by elevation; a dome texture whose lower
/// half is ground darkens it. The upgrade is to read a 4x4 mip and split it by row, which needs the model's
/// UV convention -- worth doing when a specific sky looks wrong, not before.</para>
/// </summary>
internal static unsafe class SceneFog
{
    /// <summary>The dials are <see cref="Fog"/>'s, read through these so <c>fog.enabled</c> off makes every
    /// one read as 0 -- vanilla's flat colour, view-depth metric and no cull -- without a branch at each site.
    /// <c>sky</c>: how far the target moves from the author's colour toward the sky; 0 = vanilla, which is what
    /// every knob here reduces to. <c>sun</c>/<c>sunexp</c>: in-scatter strength and the phase lobe's
    /// exponent (big = a tight glow, small = a half-sky wash). <c>height</c>: falloff per world unit above
    /// <c>floor</c>, a DENSITY the area author did not set, hence 0 by default. <c>floor</c> is an offset from
    /// the player's feet, upward only: an area has no ground plane to read. <c>cullz</c>: how high above the
    /// layer floor <see cref="OpaqueDistance"/> assumes geometry reaches while the height falloff is on.
    /// <c>origin</c>: fog measured radially from the player's feet rather than in view depth, so zooming out
    /// does not push the fog; the cull, the light cut and the grass cut measure the same way through
    /// <see cref="Origin"/>. <c>skyband</c>: the sky stage's horizon band in ray elevation -- rays at or
    /// below the horizon fully fogged, fading to the raw dome this far above; 0 brings the #341 seam back.</summary>
    internal static float SkyMix => Fog.Enabled.V ? Fog.Sky.V : 0f;
    internal static float SunAmount => Fog.Enabled.V ? Fog.Sun.V : 0f;
    internal static float SunExp => Fog.SunExp.V;
    internal static float HeightK => Fog.Enabled.V ? Fog.Height.V : 0f;
    internal static float FloorOffset => Fog.Floor.V;
    internal static float CullZ => Fog.CullZ.V;
    internal static bool OriginPlayer => Fog.Enabled.V && Fog.Origin.V;
    internal static float SkyBand => Fog.Enabled.V ? Fog.SkyBand.V : 0f;
    /// <summary>The cull's fog gate (<see cref="CullPass.FogCull"/>): off with the master, like every dial.</summary>
    internal static bool Cull => Fog.Enabled.V && Fog.Cull.V;

    /// <summary>The sky colour the fog converges to, LINEAR. Zero until a sky texture has been read, which
    /// is what <see cref="_haveSky"/> reports and what makes the term stand down to the author's colour.
    /// </summary>
    // _haveSky FIRST: ilc 10.0.11 fails codegen (recordRelocation InvalidCastException) on a Vector3 static at offset 0.
    private static bool _haveSky;
    private static Vector3 _sky;
    private static Vector3 _acc; private static float _accW;

    /// <summary>The sky average, LINEAR, once one has been read: <see cref="EnvProbe"/>'s clear where the capture
    /// draws no dome -- a hosted content sky (SoW's <c>inc_clouds</c>) is never in it.</summary>
    internal static bool TrySky(out Vector3 sky) { sky = _sky; return _haveSky; }
    private static uint _lastTid;

    /// <summary>The texture-average cache: a GL readback per sky texture, once. Sixteen slots is far more
    /// than the four dome slots plus a cross-fade partner, and a linear scan of sixteen is cheaper on the
    /// staging path than anything with a hash.</summary>
    private const int CacheN = 16;
    private static readonly uint[] _cacheTid = new uint[CacheN];
    private static readonly Vector3[] _cacheCol = new Vector3[CacheN];
    /// <summary>...and a REFUSAL is cached too. A texture with no small mip fails the walk below every time,
    /// and this runs on the staging path once per part -- an uncached miss is a bind plus twenty level
    /// queries per frame, forever, with nothing in the log to say so.</summary>
    private static readonly bool[] _cacheOk = new bool[CacheN];
    private static int _cacheUsed;

    private static ExtenderLog? _log;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>A new area, or a wiped part table: the cached sky averages belong to textures that may not
    /// exist any more. The GL names are the engine's, so this is a Revoke and not a ContextLost.</summary>
    internal static void Revoke()
    {
        _cacheUsed = 0; _haveSky = false; _sky = default; _lastTid = 0;
    }

    /// <summary>Fold one staged sky part into this frame's average. Called from the sky staging loop for
    /// every part that is NOT the fog-tinted fade band -- the band is painted in the fog colour, so counting
    /// it would feed the fog its own output.</summary>
    internal static void NoteSky(uint tid, float alpha)
    {
        if (tid == 0 || alpha <= 0f) return;
        // Every part of one role shares a texture; skip the cache scan for the run of them.
        if (tid != _lastTid)
        {
            _lastTid = tid;
            _noteCol = Average(tid, out _noteOk);
        }
        if (!_noteOk) return;
        _acc += _noteCol * alpha; _accW += alpha;
    }

    private static Vector3 _noteCol; private static bool _noteOk;

    /// <summary>Close the frame's sky accounting. A frame that staged no sky at all (an interior, or a sky
    /// pass standing down) leaves the last known colour in place rather than snapping the fog: the roles are
    /// re-staged every pass and one refused frame is not a new sky.</summary>
    internal static void EndSky()
    {
        _lastTid = 0;
        if (_accW <= 0f) { _acc = default; return; }
        _sky = _acc / _accW; _haveSky = true;
        _acc = default; _accW = 0f;
    }

    /// <summary>The sky texture's own average, LINEAR -- <c>glGetTexImage</c> of the smallest mip that has
    /// storage, which is the value <c>textureLod(sky, uv, 20)</c> would return. Cached per texture name.
    /// <para>Same walk <see cref="TexProbe.TexTopMip"/> uses and for the same reason: TEXTURE_MAX_LEVEL is a
    /// cap, not a promise, so the chain is walked down to a level that actually exists. A texture with no
    /// usable small level is refused rather than read at level 0 -- a full-size readback mid-frame is a
    /// pipeline stall, and this runs on the staging path.</para></summary>
    private static Vector3 Average(uint tid, out bool ok)
    {
        ok = false;
        for (int i = 0; i < _cacheUsed; i++)
            if (_cacheTid[i] == tid) { ok = _cacheOk[i]; return _cacheCol[i]; }
        // no readback: the mean was taken from the decoded image at the upload funnel (TexScan)
        Vector3 c = default;
        if (OwnedTextures.TryMean((int)tid, out Vector3 m))
        { c = new Vector3(Lin(m.X), Lin(m.Y), Lin(m.Z)); ok = true; }
        if (_cacheUsed < CacheN)
        { _cacheTid[_cacheUsed] = tid; _cacheCol[_cacheUsed] = c; _cacheOk[_cacheUsed] = ok; _cacheUsed++; }
        return c;
    }

    private static float Lin(float v) => MathF.Pow(MathF.Max(v, 0f), AlloyLights.SrgbGamma);
    private static float Disp(float v) => MathF.Pow(MathF.Max(v, 0f), 1f / AlloyLights.SrgbGamma);

    /// <summary>Push this pass's fog terms at one program. <paramref name="playerZ"/> anchors the height
    /// layer, and <paramref name="capture"/> stands the sky term down for an <see cref="EnvProbe"/> face:
    /// the capture is a scene draw like any other, so fogging it toward the sky and then reading that cube
    /// back as the sky would be the renderer sampling its own output.</summary>
    internal static void Upload(in AlloyRenderer.FeatLoc loc, float playerZ, bool capture)
    {
        // NO SKY, NO SKY TERM. An interior has a fog colour its author picked and nothing overhead to
        // match it against -- converging a cave's fog on some stand-in would be this feature overruling the
        // author exactly where it has no evidence. The sun lobe goes with it, because AreaDiffuse is the
        // directional light indoors too and a cave does not have a sun in its fog.
        bool useSky = _haveSky && !capture;
        float* v = stackalloc float[4];
        v[0] = useSky ? SkyMix : 0f; v[1] = HeightK; v[2] = playerZ + FloorOffset;
        Gl.Uniform3fv(loc.FogSky, 1, v);
        Vector3 sky = _sky;
        v[0] = sky.X; v[1] = sky.Y; v[2] = sky.Z;
        Gl.Uniform3fv(loc.FogSkyColor, 1, v);

        Vector3 dir = FrameInputs.AreaDiffuseDir;
        float len = dir.Length();
        v[0] = len > 0f ? dir.X / len : 0f; v[1] = len > 0f ? dir.Y / len : 0f;
        v[2] = len > 0f ? dir.Z / len : 1f; v[3] = MathF.Max(SunExp, 1f);
        Gl.Uniform4fv(loc.FogSun, 1, v);
        Vector3 sun = FrameInputs.HasAreaDiffuse && len > 0f && useSky
            ? FrameInputs.AreaDiffuse * (AlloyLights.SunScale * SunAmount) : default;
        v[0] = sun.X; v[1] = sun.Y; v[2] = sun.Z;
        Gl.Uniform3fv(loc.FogSunColor, 1, v);
    }

    /// <summary>The display-space colour the skyfade gob is painted in (#222's <c>Mat()</c> branch). Vanilla
    /// uses the raw fog colour; under a sky-matched fog that band is the seam it was invented to hide, so it
    /// takes the same fully-fogged colour the geometry under it converges to -- evaluated looking at the
    /// HORIZON, which is where the band is.
    /// <para>ponytail: elevation only. The band is one constant colour per part, so a sun in-scatter that
    /// varies with azimuth cannot be represented in it; at a low sun the band matches the fog on the sun's
    /// side and is a little flat on the other. Fixing that means a per-fragment tint, i.e. a world varying on
    /// the unlit stage.</para></summary>
    internal static void SkyfadeTint(float* rgb)
    {
        Vector3 c = FrameInputs.FogColor;   // display space, as the Scene block stores it
        if (SkyMix > 0f && _haveSky)
        {
            Vector3 lin = new Vector3(Lin(c.X), Lin(c.Y), Lin(c.Z));
            // The shader's own horizon weight at dir.z == 0 is 1.0 (smoothstep(-0.35, 0.0, 0.0)), and the
            // in-scatter lobe is dropped with the azimuth it needs.
            lin = Vector3.Lerp(lin, _sky, SkyMix);
            c = new Vector3(Disp(lin.X), Disp(lin.Y), Disp(lin.Z));
        }
        rgb[0] = c.X; rgb[1] = c.Y; rgb[2] = c.Z;
    }

    /// <summary>The distance past which a fragment is FULLY fogged, and therefore the distance the cull may
    /// throw geometry away at (<see cref="CullPass.FogCull"/>). Infinity means "nothing is provably opaque",
    /// which stands the fog cull down.
    /// <para>With the plain ramp that distance IS <c>FogEnd</c>, which is what #336 culled at, measured the
    /// way the fog is (<see cref="Origin"/>). The height falloff breaks that: fog thinned by height reaches
    /// 1.0 further out, and how much further depends on the fragment's own Z, which a distance cannot ask.
    /// So the cut is placed for geometry
    /// <see cref="CullZ"/> above the layer floor and anything higher than that is the ceiling this trades
    /// against -- a tower top past that distance can be culled while still a shade short of opaque. The
    /// number is on <see cref="Describe"/> next to the camera's own far plane, because past that the whole
    /// question is moot and the cull simply stops applying.</para></summary>
    internal static float OpaqueDistance()
    {
        if (!FrameInputs.FogOn || FrameInputs.FogEnd <= 0f) return float.PositiveInfinity;
        float end = FrameInputs.FogEnd;
        if (HeightK <= 0f) return end;
        float span = end - FrameInputs.FogStart;
        if (span <= 0f) return end;
        return FrameInputs.FogStart + span * MathF.Exp(HeightK * MathF.Max(CullZ, 0f));
    }

    /// <summary>Where the fog is measured from. <paramref name="radial"/> is true when it is the player's
    /// feet (a sphere); false means the engine's view depth and the point returned is the pass eye, which is
    /// what a radial test against it degrades to.</summary>
    internal static Vector3 Origin(out bool radial)
    {
        Vector3 p = AlloyRenderer.PlayerPos;
        radial = OriginPlayer && p != default;
        return radial ? p : AlloyRenderer.EyePos;
    }

    internal static string Describe()
    {
        float opaque = OpaqueDistance();
        return (Fog.Enabled.V ? "" : "OFF (author's flat fog; every dial reads 0)  ") + $"sky {SkyMix:F2} ({(_haveSky ? "dome texture" : "NO SKY STAGED -- author colour only")} " +
               $"{_sky.X:F2},{_sky.Y:F2},{_sky.Z:F2})  sun {SunAmount:F2} exp {SunExp:F1}  " +
               $"height {HeightK:F3}/unit floor {FloorOffset:F1}  " +
               $"[opaque {(float.IsInfinity(opaque) ? "--" : opaque.ToString("F0"))} " +
               $"cullz {CullZ:F0} camfar {FrameInputs.CamFar:F0}]  origin {(OriginPlayer ? "player" : "camera")} skyband {SkyBand:F2}";
    }

    /// <summary>Off-device: the two pieces that are arithmetic rather than GL. The cull distance is the one
    /// that can silently delete a world -- it is a plane the cull shader trusts -- and the tint is the one
    /// that can silently paint a band the wrong colour.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        bool onWas = FrameInputs.FogOn; float sWas = FrameInputs.FogStart, eWas = FrameInputs.FogEnd;
        Vector3 cWas = FrameInputs.FogColor;
        float kWas = Fog.Height.V, mixWas = Fog.Sky.V, zWas = Fog.CullZ.V, sunWas = Fog.Sun.V, bandWas = Fog.SkyBand.V;
        bool haveWas = _haveSky, enWas = Fog.Enabled.V, cullWas = Fog.Cull.V; Vector3 skyWas = _sky;
        Fog.Enabled.V = true;

        FrameInputs.FogOn = false;
        check.Expect(float.IsPositiveInfinity(OpaqueDistance()), "fog off means no cull distance at all");
        FrameInputs.FogOn = true; FrameInputs.FogStart = 25f; FrameInputs.FogEnd = 125f;
        Fog.Height.V = 0f;
        check.Expect(MathF.Abs(OpaqueDistance() - 125f) < 1e-3f, "the plain ramp is opaque at FogEnd, as #336 culled");
        Fog.Height.V = 0.01f; Fog.CullZ.V = 30f;
        // 25 + 100*exp(0.3) = 159.9: further out than FogEnd, never nearer.
        check.Expect(OpaqueDistance() > 125f, "height falloff pushes the opaque distance OUT, never in");

        Fog.Sky.V = 0f; _haveSky = true; _sky = new Vector3(1f, 1f, 1f);
        FrameInputs.FogColor = new Vector3(0.5f, 0.25f, 0.125f);
        float* rgb = stackalloc float[3];
        SkyfadeTint(rgb);
        check.Expect(MathF.Abs(rgb[0] - 0.5f) < 1e-4f && MathF.Abs(rgb[2] - 0.125f) < 1e-4f,
          "sky 0 leaves the skyfade band on the author's own fog colour, byte for byte");
        Fog.Sky.V = 1f; _sky = new Vector3(Lin(0.25f), Lin(0.5f), Lin(1f));
        SkyfadeTint(rgb);
        check.Expect(MathF.Abs(rgb[2] - 1f) < 1e-3f && MathF.Abs(rgb[0] - 0.25f) < 1e-3f,
          "sky 1 lands the band exactly on the sky colour, round-tripped through the encode");

        Fog.Sky.V = 1f; _haveSky = false;
        SkyfadeTint(rgb);
        check.Expect(MathF.Abs(rgb[0] - 0.5f) < 1e-4f,
          "no sky staged leaves the band on the author's colour whatever the mix says");

        // /fog origin 0 is the engine's metric: view depth, measured from the pass eye -- not a sphere
        // around wherever the player happens to stand.
        bool origWas = Fog.Origin.V; Fog.Origin.V = false;
        check.Expect(!(Origin(out bool radial) != AlloyRenderer.EyePos) && !radial, "origin 0 is view depth from the pass eye");
        Fog.Origin.V = true;

        // The master: off, every dial reads as 0 -- the author's flat colour, view depth, no cull -- whatever
        // the knobs hold, and the skyfade band is the author's colour byte for byte.
        _haveSky = true; Fog.Sky.V = 1f; Fog.Sun.V = 1f; Fog.Height.V = 0.01f; Fog.SkyBand.V = 0.5f; Fog.Cull.V = true;
        Fog.Enabled.V = false;
        check.Expect(SkyMix == 0f && SunAmount == 0f && HeightK == 0f && SkyBand == 0f && !OriginPlayer && !Cull && !CullPass.FogCull,
          "fog.enabled off: every dial reads 0 and the cull stands down");
        check.Expect(MathF.Abs(OpaqueDistance() - 125f) < 1e-3f, "...and the opaque distance is the plain ramp's");
        SkyfadeTint(rgb);
        check.Expect(MathF.Abs(rgb[0] - 0.5f) < 1e-4f && MathF.Abs(rgb[2] - 0.125f) < 1e-4f, "...and the skyfade band is the author's colour");
        Fog.Enabled.V = true;
        check.Expect(SkyMix == 1f && SunAmount == 1f && HeightK == 0.01f && SkyBand == 0.5f && OriginPlayer && Cull, "fog.enabled on restores the dials");
        Fog.Origin.V = origWas; Fog.Sun.V = sunWas; Fog.SkyBand.V = bandWas; Fog.Cull.V = cullWas;

        FrameInputs.FogOn = onWas; FrameInputs.FogStart = sWas; FrameInputs.FogEnd = eWas;
        FrameInputs.FogColor = cWas;
        Fog.Height.V = kWas; Fog.Sky.V = mixWas; Fog.CullZ.V = zWas; Fog.Enabled.V = enWas; _haveSky = haveWas; _sky = skyWas;
        log.Info(check.Ok ? "SceneFog.SelfCheck: passed" : "SceneFog.SelfCheck: FAILED");
    }
}
