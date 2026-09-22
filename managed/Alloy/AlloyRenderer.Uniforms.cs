using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>#173: UNIFORM UPLOAD. The per-pass scene terms every Alloy stage shares -- area light, wind, fog, sun cascades, local cubes, the env probe -- pushed to whichever program's locations the caller names, so the lit, water and grass stages upload the same values through one implementation.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Field offsets inside it, from pairing GLRender::ConnectShaderData (uniform name -&gt;
    /// location slot) with GLRender::SetUniforms (location slot -&gt; field).</summary>
    /// <summary>#147: <c>areaGlobalWind</c> in plan, as the CPU sees it -- a world vector whose magnitude is
    /// the speed, which is what <see cref="WaterBodies"/> projects fetch onto and what the wave model scales
    /// by. Zero before the runtime block resolves, and a still area is genuinely zero; the shader decides
    /// what a dead calm looks like, not this.
    /// <para>Deliberately NOT <c>sqrt(dot(w.x, w.y))</c>. That spelling appears in the #123 feature port and
    /// in the stock <c>shd_windgrass_vs.shd</c> it reproduces, where it means <c>sqrt(x*y)</c> and goes NaN
    /// the moment the components differ in sign. Faithful there, wrong here.</para></summary>
    /// <summary>The RAW engine wind magnitude, which is what vanilla's water shader squares into its own
    /// wave amplitude (<c>fAreaWindForce = dot(areaGlobalWind, areaGlobalWind)</c>). No conversion any
    /// more -- #339 deleted the sea-state model that needed metres per second.</summary>
    /// <summary>The engine's own wind vector and world clock, raw. Both feed vanilla's water phase --
    /// <c>fAreaWindForce = dot(areaGlobalWind, areaGlobalWind)</c> and
    /// <c>fTick = worldtimerTimeOfDay * 0.001</c> -- and a wave field that reads flat is one of them being
    /// unlike what the engine's own shader gets, so the line prints them rather than a derived number.
    /// The Z component is printed on purpose: the shader normalises the wind in THREE dimensions, so a large
    /// Z shrinks the horizontal direction and stretches the wavelength past the area.</summary>
    internal static string DescribeWind()
    {
        var w = AreaWind();
        float z = _runtime == null ? 0f : *(float*)(_runtime + RtAreaWind + 8);
        int t = _runtime == null ? 0 : *(int*)(_runtime + RtTimeOfDay);
        return $"wind raw={w.Length():F3} (x={w.X:F3} y={w.Y:F3} z={z:F3}) timeofday={t}";
    }

    /// <summary>The engine's world clock as the shaders see it, for the census. Everything time-varying in
    /// an owned stage derives from this int, so a frozen value is a frozen world.</summary>
    internal static int TimeOfDay => _runtime == null ? 0 : *(int*)(_runtime + RtTimeOfDay);

    private static Vector2 AreaWind()
        => _runtime == null ? default
         : new Vector2(*(float*)(_runtime + RtAreaWind), *(float*)(_runtime + RtAreaWind + 4));

    /// <summary>#132: the scene's fog colour, linearised. The block stores it display-space -- the stock
    /// shaders lerp toward it AFTER RevertColorSpace -- and the Alloy frame is linear end to end, so the
    /// curve comes off here rather than in every stage that fogs.</summary>
    private static void UploadFogColour(int loc)
    {
        Vector3 src = FrameInputs.FogColor;
        float* c = stackalloc float[4];
        c[0] = MathF.Pow(MathF.Max(src.X, 0f), AlloyLights.SrgbGamma);
        c[1] = MathF.Pow(MathF.Max(src.Y, 0f), AlloyLights.SrgbGamma);
        c[2] = MathF.Pow(MathF.Max(src.Z, 0f), AlloyLights.SrgbGamma);
        c[3] = 1f;
        Gl.Uniform4fv(loc, 1, c);
    }

    /// <summary>#257: this frame's fog, packed the way every Alloy stage reads it -- enable, start, end and
    /// 1/(end-start). Read out of <see cref="FrameInputs"/>, which took it straight off <c>Scene+0x140</c>;
    /// nothing asks the engine to fill <c>GLRender::m_fog</c> for us any more, so a frame where the engine
    /// drew nothing still fogs correctly.</summary>
    private static void PackFog(float* px)
    {
        px[0] = FrameInputs.FogOn ? 1f : 0f;
        px[1] = FrameInputs.FogStart; px[2] = FrameInputs.FogEnd;
        float span = px[2] - px[1];
        px[3] = span != 0f ? 1f / span : 0f;
    }

    /// <summary>#131: just the scene fog, for a program that wants it and none of the rest of
    /// <see cref="UploadFeatureTerms"/>. Same source, same bool-byte conversion; split out rather than
    /// copied so the pack stays in one place.
    /// <para>#341: "the scene fog" is five uniforms and an eye position now, not a pair -- the target colour
    /// is built per ray. Every stage that fogs takes all of them from here, or two stages fog the same
    /// fragment differently.</para></summary>
    internal static void UploadFog(in FeatLoc loc)
    {
        float* px = stackalloc float[4];
        PackFog(px);
        Gl.Uniform4fv(loc.Fog, 1, px);
        UploadFogColour(loc.FogColor);
        fixed (Vector3* eye = &_eyePos) Gl.Uniform3fv(loc.Eye, 1, (float*)eye);
        Vector3 origin = SceneFog.Origin(out bool radial);
        Gl.Uniform4f(loc.FogOrigin, origin.X, origin.Y, origin.Z, radial ? 1f : 0f);
        // The height layer is anchored to the player's feet: an area carries no ground plane to read, and
        // the runtime block is where the engine keeps the one position that tracks them.
        float playerZ = _runtime == null ? 0f : ((float*)(_runtime + RtPlayerPos))[2];
        SceneFog.Upload(loc, playerZ, _capturing);
    }

    /// <summary>Feed one program the engine's own live world state -- the same five values its own shaders
    /// read under these names. Once per program per pass; nothing here is per part.</summary>
    internal static void UploadFeatureTerms(in FeatLoc loc, int* viewport)
    {
        // #124: pixelParams is the ONE term not read out of an engine global -- SetUniforms builds it from
        // the window size, and this pass may be rendering into the probe cube instead, so it comes from the
        // viewport actually bound. Everything else below is the engine's own live value.
        float w = viewport[2] > 0 ? viewport[2] : 1f, h = viewport[3] > 0 ? viewport[3] : 1f;
        float* px = stackalloc float[4];
        px[0] = 1f / w; px[1] = 1f / h; px[2] = w / h;
        Gl.Uniform3fv(loc.Pixel, 1, px);
        Gl.Uniform1f(loc.LodBias, PostChain.LodBias);   // #391: -1 where the stage declares none; GL ignores it
        UploadFog(loc);
        if (_shaderConstF != null)
        {
            float a = _shaderConstF[KeyholeDiaA], b = _shaderConstF[KeyholeDiaB];
            px[0] = MathF.Min(a, b); px[1] = MathF.Max(a, b);
            Gl.Uniform2fv(loc.KhDia, 1, px);
        }
        UploadTargetShape(loc);
        UploadVanillaEnv(loc);
        if (_runtime == null) return;
        Gl.Uniform1i(loc.Time, *(int*)(_runtime + RtTimeOfDay));

        Gl.Uniform3fv(loc.Wind, 1, (float*)(_runtime + RtAreaWind));
        Gl.Uniform1i(loc.Flags, *(int*)(_runtime + RtAreaFlags));
        Gl.Uniform3fv(loc.Cam, 1, (float*)(_runtime + RtCamPos));
        Gl.Uniform3fv(loc.CamFocus, 1, (float*)(_runtime + RtCamFocus));
        Gl.Uniform3fv(loc.Player, 1, (float*)(_runtime + RtPlayerPos));
        px[0] = *(float*)(_runtime + RtCamDist); px[1] = *(float*)(_runtime + RtCamPitch);
        Gl.Uniform2fv(loc.KhCam, 1, px);
    }

    /// <summary>#346: the spell-targeting shape's terms, straight out of the engine's global-constant
    /// tables -- the same values <c>GLRender::SetUniforms</c> hands the engine's own copy of the same
    /// shader. Written under the ENGINE's uniform names: the stage compiles <c>inc_target.shd</c> itself
    /// (see <see cref="TargetFs"/>), so these are its declarations, not ours.
    /// <para>Only what that file reads is uploaded. The harms/helps/ignores flags and the spell and feat ids
    /// are declared there and sampled by nothing, and a program that carries no targeting code at all gets
    /// location -1 for every one of these, which GL ignores.</para>
    /// <para>Nothing is converted. The colour is the engine's own display-space vec4 and the fragment stage
    /// mixes with it inside the same encode bracket the fog uses, so a shape drawn on an Alloy tile and one
    /// drawn on a vanilla tile are the same strength.</para></summary>
    private static void UploadTargetShape(in FeatLoc loc)
    {
        // The shape's own "nothing to draw" answer is targetShape 0 AND range 0; the object type is the
        // other half of it, and 0 there is what the engine writes when the cursor is over nothing.
        if (_shaderConstI == null || _shaderConstV == null || _shaderConstV4 == null || _shaderConstF == null)
        { Gl.Uniform1i(loc.TgtObjL, 0); Gl.Uniform1i(loc.TgtShapeL, 0); return; }
        Gl.Uniform1i(loc.TgtShapeL, _shaderConstI[TgtShape]);
        Gl.Uniform1i(loc.TgtSelfL, _shaderConstI[TgtOnSelf]);
        Gl.Uniform1i(loc.TgtObjL, _shaderConstI[TgtObjType]);
        Gl.Uniform1f(loc.TgtSizeXL, _shaderConstF[TgtSizeX]);
        Gl.Uniform1f(loc.TgtSizeYL, _shaderConstF[TgtSizeY]);
        Gl.Uniform1f(loc.TgtRangeL, _shaderConstF[TgtRange]);
        Gl.Uniform4fv(loc.TgtColL, 1, _shaderConstV4 + TgtColour * 4);
        Gl.Uniform3fv(loc.TgtWorldL, 1, _shaderConstV);
    }

    /// <summary>#347: the rest of the shared vanilla environment -- the projection and the scriptable
    /// entries the spliced stock files gate on. Same shape and same reason as
    /// <see cref="UploadTargetShape"/>: written under the ENGINE's uniform names, because the code reading
    /// them is the engine's own, and a program carrying none of them gets -1 for every location.
    /// <para>The noir and vision gates are integers a server script sets. Zero is "off" and is what an
    /// unresolved constant table leaves them at, which is the correct answer for a client that cannot read
    /// them rather than a feature stuck on.</para></summary>
    internal static void UploadVanillaEnv(in FeatLoc loc)
    {
        fixed (Matrix4x4* m = &_projMtx) Gl.UniformMatrix4fv(loc.ProjL, 1, 0, (float*)m);
        if (_shaderConstI == null || _shaderConstF == null || _shaderConstV4 == null) return;
        // #551: the block, not a selection. A content shader that begins reading a slot nobody thought to
        // wire would otherwise compile, link, and read zero -- working under the engine and silently doing
        // nothing under Alloy. sow-topdata#551 (Float13, the sky-light veil fade) is that case exactly.
        // CX-11: only the live slots -- a program that declares none makes no calls at all.
        fixed (FeatLoc* l = &loc)
        {
            for (int m = l->SIntLive; m != 0; m &= m - 1)
            { int i = BitOperations.TrailingZeroCount(m); Gl.Uniform1i(l->SInt[i], _shaderConstI[SIntBase + i]); }
            for (int m = l->SFloatLive; m != 0; m &= m - 1)
            { int i = BitOperations.TrailingZeroCount(m); Gl.Uniform1f(l->SFloat[i], _shaderConstF[SFloatBase + i]); }
            for (int m = l->SVecLive; m != 0; m &= m - 1)
            { int i = BitOperations.TrailingZeroCount(m); Gl.Uniform4fv(l->SVec[i], 1, _shaderConstV4 + (SVecBase + i) * 4); }
        }
    }

    /// <summary>The engine's own area terms, captured from SetLightGL's index -1 path. No capture yet (a
    /// pass before the first EnableLights) leaves the surface on ambient alone rather than unlit black.</summary>
    internal static void UploadAreaTerms(in LitLoc L) => UploadAreaTerms(L.Ambient, L.Diffuse, L.Dir);

    /// <summary>Point the lit program at <see cref="CasterPass"/>'s cascade array, or tell it there is nothing
    /// to sample. Everything the receiver needs comes from that pass's published render-time values -- the
    /// matrices are never re-derived here, and the freshness test is the single gate that covers interiors, a
    /// vanilla stretch and a disarm alike.</summary>
    internal static void UploadSunCascades(in LitLoc L)
        => UploadSunCascades(L.SunTex, L.SunVp, L.SunTexel, L.SunBias, L.SunCount, L.SunDyn,
                             L.SunFocus, L.SunRadius);

    /// <summary>Point the lit program at <see cref="CasterPass"/>'s local-light cube array, or tell it there
    /// is nothing to sample. The per-slot position and far plane are the ones each cube was RENDERED with --
    /// the receiver never re-derives them from the live light, which is what keeps a carried torch's shadow
    /// attached to the torch. Same freshness gate as the cascades.</summary>
    internal static void UploadLocalCubes(in LitLoc L)
        => UploadLocalCubes(L.CubeTex, L.CubeLight, L.CubePlane, L.CubeCount, L.CubeSoft, L.CubePcss,
                            L.CubeDynPage, L.CubeFade, L.CubeFloor);

    /// <summary>#121: point the lit program at the IBL probe, or tell it there is nothing to sample
    /// (<c>uProbeMips</c> 0). Unbinding rather than leaving the cube bound during a capture is not
    /// belt-and-braces: sampling a texture that is an attachment of the framebuffer being written is
    /// undefined, and the capture draws through this very program.</summary>
    internal static void UploadProbe(in LitLoc L) => UploadProbe(L.ProbeMips, L.ProbeGain, L.SceneEnv);

    /// <summary>#127: the same terms, into whichever program's locations the caller names -- the Alloy grass
    /// pass runs its own program over the same lights, so the area terms cannot be a property of the lit
    /// program's uniform cache.</summary>
    internal static void UploadAreaTerms(int locAmbient, int locDiffuse, int locDir)
    {
        float* v = stackalloc float[3];
        v[0] = v[1] = v[2] = FrameInputs.HasAreaAmbient ? 0f : DefaultAmbient;
        if (FrameInputs.HasAreaAmbient)
        {
            // AlloyLights.AmbientScale / SunScale are the live /light dials -- applied here, at the upload,
            // so a retune costs one uniform write and shows on the very next frame.
            var a = FrameInputs.AreaAmbient * AlloyLights.AmbientScale;
            v[0] = a.X; v[1] = a.Y; v[2] = a.Z;
        }
        else if (!Diag._ambientFallbackLogged)
        {
            Diag._ambientFallbackLogged = true;
            _log?.Info("AlloyRenderer: no area ambient captured yet -- Alloy lit parts on the grey stand-in " +
                       "(if this persists, SetLightGL's index -1 path never fired)");
        }
        Gl.Uniform3fv(locAmbient, 1, v);
        var d = FrameInputs.HasAreaDiffuse ? FrameInputs.AreaDiffuse * AlloyLights.SunScale : default;
        v[0] = d.X; v[1] = d.Y; v[2] = d.Z;
        Gl.Uniform3fv(locDiffuse, 1, v);
        var dir = FrameInputs.HasAreaDiffuse ? FrameInputs.AreaDiffuseDir : new Vector3(0f, 0f, 1f);
        v[0] = dir.X; v[1] = dir.Y; v[2] = dir.Z;
        Gl.Uniform3fv(locDir, 1, v);
    }

    /// <summary>#127: the cascade receiver's uniforms, into a named program's locations. Same values, same
    /// freshness gate; only where they land differs.</summary>
    internal static void UploadSunCascades(int locTex, int locVp, int locTexel, int locBias, int locCount,
                                           int locDyn, int locFocus = -1, int locRadius = -1)
    {
        if (locCount < 0) return;
        if (!CasterPass.CascadesFresh) { Gl.Uniform1i(locCount, 0); return; }
        int n = CasterPass.CascadeCount;
        Gl.ActiveTexture(Gl.TEXTURE0 + SunUnit);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, CasterPass.CascadeTex);
        // #306: the dynamic half rides the same publish, on BOTH units the table assigns it (the grass
        // program's unit 10 is its displacement field, so grass reads the array off unit 13 instead).
        Gl.ActiveTexture(Gl.TEXTURE0 + SunDynUnit);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, CasterPass.SunDynTex);
        Gl.ActiveTexture(Gl.TEXTURE0 + SunDynUnitGrass);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, CasterPass.SunDynTex);
        Gl.ActiveTexture(Gl.TEXTURE0);       // leave unit 0 current: the batch loop rebinds diffuse textures
        Gl.Uniform1i(locTex, (int)SunUnit);
        if (locDyn >= 0)
            fixed (float* dm = CasterPass.SunDynMask) Gl.Uniform4fv(locDyn, 1, dm);
        fixed (float* vp = CasterPass.CascadeVp) Gl.UniformMatrix4fv(locVp, n, 0, vp);
        float* pack = stackalloc float[4];
        for (int i = 0; i < 4; i++) pack[i] = i < n ? CasterPass.CascadeTexel[i] : 0f;
        Gl.Uniform4fv(locTexel, 1, pack);
        for (int i = 0; i < 4; i++)
        {
            float range = i < n ? CasterPass.CascadeRange[i] : 0f;
            pack[i] = range > 0f ? _sunWorldBias / range : 0f;
        }
        Gl.Uniform4fv(locBias, 1, pack);
        // #308 follow-up: the cross-fade band's two terms -- the LIVE focus (smooth where the latched fits
        // step) and the per-cascade fit radius. Absent on a stale content override (location -1), which
        // degrades to the hard handoff, never to a wrong blend.
        if (locFocus >= 0)
        {
            var fo = CasterPass.SunFocusLive;
            pack[0] = fo.X; pack[1] = fo.Y; pack[2] = fo.Z;
            Gl.Uniform3fv(locFocus, 1, pack);
        }
        if (locRadius >= 0)
        {
            for (int i = 0; i < 4; i++) pack[i] = i < n ? CasterPass.CascadeFitRadius[i] : 0f;
            Gl.Uniform4fv(locRadius, 1, pack);
        }
        Gl.Uniform1i(locCount, n);
    }

    /// <summary>#642: the no-compare sampler the receiver's blocker search reads the cube arrays through
    /// (<see cref="TextureSlots.LocalShadowRaw"/>). 0 = the driver is short of fragment units or sampler
    /// objects, and the search stands down; asked once.</summary>
    private static uint _rawSampler;
    private static bool _rawProbed;
    private static void ProbeRawSampler()
    {
        _rawProbed = true;
        int units = 0; Gl.GetIntegerv(Gl.MAX_TEXTURE_IMAGE_UNITS, &units);
        if (units <= (int)TextureSlots.LocalShadowDynRaw || Gl.GenSamplers == null || Gl.SamplerParameteri == null || Gl.BindSampler == null)
        { _log?.Info($"AlloyRenderer: shadow blocker search OFF -- {units} fragment texture units, needs {TextureSlots.LocalShadowDynRaw + 1}"); return; }
        uint s; Gl.GenSamplers(1, &s);
        Gl.SamplerParameteri(s, Gl.TEXTURE_COMPARE_MODE, 0);   // GL_NONE: a depth, not a verdict
        Gl.SamplerParameteri(s, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.SamplerParameteri(s, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.SamplerParameteri(s, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.SamplerParameteri(s, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.BindSampler(TextureSlots.LocalShadowRaw, s); Gl.BindSampler(TextureSlots.LocalShadowDynRaw, s);
        _rawSampler = s;
    }

    /// <summary>#127: the local-cube receiver's uniforms, into a named program's locations.</summary>
    internal static void UploadLocalCubes(int locTex, int locLight, int locPlane, int locCount, int locSoft, int locPcss,
                                          int locDynPage, int locFade, int locFloor)
    {
        if (locCount < 0) return;
        if (!_rawProbed) ProbeRawSampler();
        if (locFloor >= 0) Gl.Uniform1f(locFloor, AlloyLights.ShadowFloor);
        // #642: source radius (0 = no raw view, no search), temporal phase (golden-ratio turns per frame, 0
        // with no taa to integrate it), disc taps; w = the view depth past which no cube is read (0 = none)
        if (locPcss >= 0) Gl.Uniform4f(locPcss, _rawSampler != 0 ? AlloyLights.ShadowSource : 0f,
                                       PostStack.TaaLive ? (_frameSeq * 0.618034f) % 1f : 0f, Shadows.Taps.V, AlloyLights.ShadowDist);
        // #139: the softness pair goes out even when there are no cubes -- it is program state, not per-cube
        // state, and a program that only ever sees the no-cubes path would otherwise keep the zeroes.
        // #326: pixels -> world radius per unit view depth, so the shader compares its disc against the
        // screen without knowing the projection. 0 (no perspective this pass) = the disc everywhere.
        float discZ = _lodScale > 0f ? AlloyLights.ShadowDiscPx / _lodScale : 0f;
        if (locSoft >= 0) Gl.Uniform4f(locSoft, AlloyLights.ShadowSoft, AlloyLights.ShadowNearWorld, discZ, AlloyLights.ShadowCap);
        if (!CasterPass.CubesFresh) { Gl.Uniform1i(locCount, 0); return; }
        int n = CasterPass.CubeCount;
        Gl.ActiveTexture(Gl.TEXTURE0 + CubeUnit);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, CasterPass.CubeTex);
        // #305: the dynamic pool rides the same publish -- the per-slot page indices carry PAGE+1, so a slot
        // with no movers (or a session with no pool) reads 0 and the receiver stays static-only there.
        Gl.ActiveTexture(Gl.TEXTURE0 + CubeDynUnit);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, CasterPass.CubeDynTex);
        if (_rawSampler != 0)
        {   // #642: the same two arrays on the raw units; the sampler object bound there strips the compare
            Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.LocalShadowRaw);
            Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, CasterPass.CubeTex);
            Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.LocalShadowDynRaw);
            Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, CasterPass.CubeDynTex);
        }
        Gl.ActiveTexture(Gl.TEXTURE0);       // leave unit 0 current: the batch loop rebinds diffuse textures
        Gl.Uniform1i(locTex, (int)CubeUnit);
        fixed (float* l = CasterPass.CubeLight) Gl.Uniform4fv(locLight, n, l);
        if (locDynPage >= 0)
            fixed (float* p = CasterPass.CubeDynPage) Gl.Uniform1fv(locDynPage, n, p);
        if (locFade >= 0)
            fixed (float* p = CasterPass.CubeFade) Gl.Uniform1fv(locFade, n, p);
        Gl.Uniform2f(locPlane, CasterPass.CubeNearPlane, 1f / CasterPass.CubeFaceSize);
        Gl.Uniform1i(locCount, n);
    }

    /// <summary>#129: the same terms into a named program's locations -- the Alloy water stage samples the
    /// same cube as its reflection fallback, so the probe cannot be a property of the lit program's cache.</summary>
    private static void UploadProbe(int locMips, int locGain, int locSceneEnv)
    {
        if (locMips < 0) return;
        bool live = !_capturing && EnvProbe.Ready && EnvProbe.Tex != 0;
        Gl.ActiveTexture(Gl.TEXTURE0 + EnvProbe.ProbeUnit);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP, live ? EnvProbe.Tex : 0);
        Gl.ActiveTexture(Gl.TEXTURE0);       // leave unit 0 current: the batch loop rebinds diffuse textures
        Gl.Uniform1f(locMips, live ? EnvProbe.Mips : 0f);
        Gl.Uniform1f(locGain, EnvProbe.Gain);
        Gl.Uniform1i(locSceneEnv, _sceneEnv && EnvProbe.AreaWide ? 1 : 0);
    }
}
