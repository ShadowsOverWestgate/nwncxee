using System;
using System.Numerics;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #341: the scene fog's TARGET COLOUR, and the terms that shape it. The area author still owns the ramp --
/// <c>Scene+0x140..0x14c</c>'s enable/start/end and colour are read untouched by <see cref="FrameRead"/> and
/// the lerp stays linear in view depth -- so every knob here changes only what a fully-fogged fragment
/// converges TO, or a multiplier on how fast it gets there.
/// <para><b>Why.</b> Vanilla fog converges on one flat colour. The sky does not: an exterior fades its
/// distance to the author's grey while the skybox above it stays blue, and the seam between them is the
/// horizon band the engine paints with the skyfade gob to hide exactly that. Converging on the SKY instead
/// is what removes the seam rather than covering it, and it is what every modern renderer does with distance
/// fog (fog toward a convolved environment, not toward a constant).</para>
/// <para><b>Where the sky colour comes from.</b> The Alloy sky pass already knows the dome's GL texture per
/// role (<see cref="AlloyRenderer.StageSky"/>), so the colour is a readback of that texture's smallest mip
/// -- the sampler's own average -- weighted by the role alpha the cross-fade is running at. That makes it
/// free of time-of-day plumbing: a cross-fade to the night dome changes the texture and the weights, and the
/// fog follows. Not the <see cref="EnvProbe"/> cube, which contains no sky at all: its faces are CLEARED to
/// a sky stand-in and the sky pass does not draw into them.
/// <para>No sky staged -- an interior, or a sky pass standing down -- and BOTH direction terms switch off:
/// the fog is the area author's flat colour, exactly as vanilla. A cave has a fog colour someone chose and
/// nothing overhead to match it to, so there is nothing here to improve and a stand-in would only overrule
/// them.</para>
/// <para>ponytail: one colour for the whole dome, not a gradient by elevation. A dome texture whose lower
/// half is ground darkens it. The upgrade is to read a 4x4 mip and split it by row, which needs the model's
/// UV convention -- worth doing when a specific sky looks wrong, not before.</para></para>
/// </summary>
internal static unsafe class SceneFog
{
    /// <summary>How far the fog target moves from the author's colour toward the sky. 0 = vanilla's flat
    /// colour, which is what every knob here reduces to.</summary>
    internal static float SkyMix = DefSkyMix;
    /// <summary>Sun in-scatter: fog brightens toward the area's directional light. Strength, then the
    /// exponent of the phase lobe -- a big exponent is a tight glow around the sun, a small one is a
    /// half-sky wash.</summary>
    internal static float SunAmount = DefSunAmount, SunExp = DefSunExp;
    /// <summary>Height falloff, per world unit above <see cref="FloorOffset"/>. 0 = off, and off is the
    /// default on purpose: this is a DENSITY the area author did not set, unlike everything else here.
    /// </summary>
    internal static float HeightK = DefHeightK;
    /// <summary>World height the fog layer starts thinning above, as an offset from the player's feet. The
    /// anchor is the player rather than an absolute Z because an area has no ground plane to read.
    /// <para>ponytail: upward only -- the shared <c>/x key value</c> parser refuses a negative, so a floor
    /// BELOW the feet needs that parser widened first.</para></summary>
    internal static float FloorOffset = DefFloorOffset;
    /// <summary>How high above the layer floor <see cref="OpaqueDistance"/> is willing to assume geometry
    /// reaches. Only used while <see cref="HeightK"/> is on: see that method.</summary>
    internal static float CullZ = DefCullZ;

    internal const float DefSkyMix = 0.75f, DefSunAmount = 0.35f, DefSunExp = 8f,
                         DefHeightK = 0f, DefFloorOffset = 0f, DefCullZ = 30f;

    /// <summary>The sky colour the fog converges to, LINEAR. Zero until a sky texture has been read, which
    /// is what <see cref="_haveSky"/> reports and what makes the term stand down to the author's colour.
    /// </summary>
    private static Vector3 _sky;
    private static bool _haveSky;
    private static Vector3 _acc; private static float _accW;
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
        if (Gl.GetTexImage == null || Gl.GetTexParameteriv == null || Gl.GetTexLevelParameteriv == null)
            return default;

        int saved; Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &saved);
        Gl.BindTexture(Gl.TEXTURE_2D, tid);
        int maxLevel = 0;
        Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, &maxLevel);
        int lod = maxLevel < 20 ? maxLevel : 20, w = 0, h = 0;
        for (; lod > 0; lod--)
        {
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, lod, Gl.TEXTURE_WIDTH, &w);
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, lod, Gl.TEXTURE_HEIGHT, &h);
            if (w > 0 && h > 0 && w * h <= 64) break;
        }
        Vector3 c = default;
        if (lod > 0 && w > 0 && h > 0)
        {
            byte* px = stackalloc byte[64 * 4];
            Gl.GetTexImage(Gl.TEXTURE_2D, lod, Gl.RGBA, Gl.UNSIGNED_BYTE, px);
            int n = w * h, r = 0, g = 0, b = 0;
            for (int i = 0; i < n; i++) { r += px[i * 4]; g += px[i * 4 + 1]; b += px[i * 4 + 2]; }
            // Display space in the file, linear in the frame -- the same curve the unlit sky stage applies
            // to the texel it draws.
            c = new Vector3(Lin(r / (255f * n)), Lin(g / (255f * n)), Lin(b / (255f * n)));
            ok = true;
        }
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)saved);
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
    /// <para>With the plain ramp that distance IS <c>FogEnd</c>, which is what #336 culled at. The height
    /// falloff breaks that: fog thinned by height reaches 1.0 further out, and how much further depends on
    /// the fragment's own Z, which a far PLANE cannot ask. So the plane is placed for geometry
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

    internal static string Describe()
    {
        float opaque = OpaqueDistance();
        return $"sky {SkyMix:F2} ({(_haveSky ? "dome texture" : "NO SKY STAGED -- author colour only")} " +
               $"{_sky.X:F2},{_sky.Y:F2},{_sky.Z:F2})  sun {SunAmount:F2} exp {SunExp:F1}  " +
               $"height {HeightK:F3}/unit floor {FloorOffset:F1}  " +
               $"[opaque {(float.IsInfinity(opaque) ? "--" : opaque.ToString("F0"))} " +
               $"cullz {CullZ:F0} camfar {FrameInputs.CamFar:F0}]";
    }

    internal static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            case "sky":    old = SkyMix; SkyMix = MathF.Min(v, 1f); break;
            case "sun":    old = SunAmount; SunAmount = v; break;
            case "sunexp": old = SunExp; SunExp = MathF.Max(v, 1f); break;
            // The one knob that changes DENSITY rather than colour, hence off by default.
            case "height": old = HeightK; HeightK = v; break;
            case "floor":  old = FloorOffset; FloorOffset = v; break;
            case "cullz":  old = CullZ; CullZ = v; break;
            default: return float.NaN;
        }
        _log?.Info($"SceneFog: /fog {key} {v:F3} (was {old:F3}) -> {Describe()}");
        return old;
    }

    internal static void ResetTuning()
    {
        SkyMix = DefSkyMix; SunAmount = DefSunAmount; SunExp = DefSunExp;
        HeightK = DefHeightK; FloorOffset = DefFloorOffset; CullZ = DefCullZ;
    }

    /// <summary>Off-device: the two pieces that are arithmetic rather than GL. The cull distance is the one
    /// that can silently delete a world -- it is a plane the cull shader trusts -- and the tint is the one
    /// that can silently paint a band the wrong colour.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"SceneFog.SelfCheck FAIL: {w}"); } }

        bool onWas = FrameInputs.FogOn; float sWas = FrameInputs.FogStart, eWas = FrameInputs.FogEnd;
        Vector3 cWas = FrameInputs.FogColor;
        float kWas = HeightK, mixWas = SkyMix, zWas = CullZ;
        bool haveWas = _haveSky; Vector3 skyWas = _sky;

        FrameInputs.FogOn = false;
        E(float.IsPositiveInfinity(OpaqueDistance()), "fog off means no cull distance at all");
        FrameInputs.FogOn = true; FrameInputs.FogStart = 25f; FrameInputs.FogEnd = 125f;
        HeightK = 0f;
        E(MathF.Abs(OpaqueDistance() - 125f) < 1e-3f, "the plain ramp is opaque at FogEnd, as #336 culled");
        HeightK = 0.01f; CullZ = 30f;
        // 25 + 100*exp(0.3) = 159.9: further out than FogEnd, never nearer.
        E(OpaqueDistance() > 125f, "height falloff pushes the opaque distance OUT, never in");

        SkyMix = 0f; _haveSky = true; _sky = new Vector3(1f, 1f, 1f);
        FrameInputs.FogColor = new Vector3(0.5f, 0.25f, 0.125f);
        float* rgb = stackalloc float[3];
        SkyfadeTint(rgb);
        E(MathF.Abs(rgb[0] - 0.5f) < 1e-4f && MathF.Abs(rgb[2] - 0.125f) < 1e-4f,
          "sky 0 leaves the skyfade band on the author's own fog colour, byte for byte");
        SkyMix = 1f; _sky = new Vector3(Lin(0.25f), Lin(0.5f), Lin(1f));
        SkyfadeTint(rgb);
        E(MathF.Abs(rgb[2] - 1f) < 1e-3f && MathF.Abs(rgb[0] - 0.25f) < 1e-3f,
          "sky 1 lands the band exactly on the sky colour, round-tripped through the encode");

        SkyMix = 1f; _haveSky = false;
        SkyfadeTint(rgb);
        E(MathF.Abs(rgb[0] - 0.5f) < 1e-4f,
          "no sky staged leaves the band on the author's colour whatever the mix says");

        FrameInputs.FogOn = onWas; FrameInputs.FogStart = sWas; FrameInputs.FogEnd = eWas;
        FrameInputs.FogColor = cWas;
        HeightK = kWas; SkyMix = mixWas; CullZ = zWas; _haveSky = haveWas; _sky = skyWas;
        log.Info(ok ? "SceneFog.SelfCheck: passed" : "SceneFog.SelfCheck: FAILED");
    }
}
