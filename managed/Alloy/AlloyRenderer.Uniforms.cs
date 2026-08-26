using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

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
    /// <summary>#147: the RAW engine wind magnitude alongside the metres/second the sea state is fed. The
    /// conversion (<see cref="FrameSnapshot.WindScale"/>) is an admitted guess, and this line is how it gets
    /// calibrated -- read it in an area whose weather you know and set /water wind from it.</summary>
    internal static string DescribeWind()
    {
        var w = AreaWind();
        float raw = w.Length();
        return $"wind raw={raw:F3} -> {raw * FrameSnapshot.WindScale:F1}m/s";
    }

    private static Vector2 AreaWind()
        => _runtime == null ? default
         : new Vector2(*(float*)(_runtime + RtAreaWind), *(float*)(_runtime + RtAreaWind + 4));

    /// <summary>#132: the engine's fog colour, linearised. <c>m_fog</c> stores it display-space -- the stock
    /// shaders lerp toward it AFTER RevertColorSpace -- and the Alloy frame is linear end to end, so the
    /// curve comes off here rather than in every stage that fogs.</summary>
    private static void UploadFogColour(int loc)
    {
        float* c = stackalloc float[4];
        float* src = _fog + FogColor / 4;
        for (int i = 0; i < 3; i++) c[i] = MathF.Pow(MathF.Max(src[i], 0f), AlloyLights.SrgbGamma);
        c[3] = src[3];
        Gl.Uniform4fv(loc, 1, c);
    }

    /// <summary>#131: just the scene-fog pair, for a program that wants fog and none of the rest of
    /// <see cref="UploadFeatureTerms"/>. Same source, same bool-byte conversion; split out rather than
    /// copied so <see cref="GlFog"/>'s layout stays known in one place.</summary>
    internal static void UploadFog(int fogLoc, int fogColorLoc)
    {
        float* px = stackalloc float[4];
        if (_fog != null)
        {
            px[0] = ((byte*)_fog)[FogEnabled] != 0 ? 1f : 0f;
            px[1] = _fog[FogStart / 4]; px[2] = _fog[FogEnd / 4];
            float span = px[2] - px[1];
            px[3] = span != 0f ? 1f / span : 0f;
            Gl.Uniform4fv(fogLoc, 1, px);
            UploadFogColour(fogColorLoc);
        }
        else { px[0] = px[1] = px[2] = px[3] = 0f; Gl.Uniform4fv(fogLoc, 1, px); }
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
        if (_fog != null)
        {
            // fogEnabled is a BOOL BYTE, and SetUniforms @0x4cdb33 converts it: movzbl 0x18(%r12) then
            // cvtsi2ss. Reading those 4 bytes as a float instead gives bit pattern 1 -- a denormal, which
            // GLSL is free to flush to zero, so `uFogParams.x == 0.0` would go TRUE on an FTZ driver and
            // the Alloy pass would render unfogged on that machine and only that machine.
            px[0] = ((byte*)_fog)[FogEnabled] != 0 ? 1f : 0f;
            px[1] = _fog[FogStart / 4]; px[2] = _fog[FogEnd / 4];
            float span = px[2] - px[1];
            px[3] = span != 0f ? 1f / span : 0f;
            Gl.Uniform4fv(loc.Fog, 1, px);
            UploadFogColour(loc.FogColor);
        }
        else { px[0] = px[1] = px[2] = px[3] = 0f; Gl.Uniform4fv(loc.Fog, 1, px); }
        if (_shaderConstF != null)
        {
            float a = _shaderConstF[KeyholeDiaA], b = _shaderConstF[KeyholeDiaB];
            px[0] = MathF.Min(a, b); px[1] = MathF.Max(a, b);
            Gl.Uniform2fv(loc.KhDia, 1, px);
        }
        if (_runtime == null) return;
        Gl.Uniform1i(loc.Time, *(int*)(_runtime + RtTimeOfDay));

        Gl.Uniform3fv(loc.Wind, 1, (float*)(_runtime + RtAreaWind));
        Gl.Uniform1i(loc.Flags, *(int*)(_runtime + RtAreaFlags));
        Gl.Uniform3fv(loc.Cam, 1, (float*)(_runtime + RtCamPos));
        Gl.Uniform3fv(loc.Player, 1, (float*)(_runtime + RtPlayerPos));
        px[0] = *(float*)(_runtime + RtCamDist); px[1] = *(float*)(_runtime + RtCamPitch);
        Gl.Uniform2fv(loc.KhCam, 1, px);
    }

    /// <summary>The engine's own area terms, captured from SetLightGL's index -1 path. No capture yet (a
    /// pass before the first EnableLights) leaves the surface on ambient alone rather than unlit black.</summary>
    private static void UploadAreaTerms() => UploadAreaTerms(_litAmbient, _litDiffuse, _litDir);

    /// <summary>Point the lit program at <see cref="CasterPass"/>'s cascade array, or tell it there is nothing
    /// to sample. Everything the receiver needs comes from that pass's published render-time values -- the
    /// matrices are never re-derived here, and the freshness test is the single gate that covers interiors, a
    /// vanilla stretch and a disarm alike.</summary>
    private static void UploadSunCascades()
        => UploadSunCascades(_litSunTex, _litSunVp, _litSunTexel, _litSunBias, _litSunCount);

    /// <summary>Point the lit program at <see cref="CasterPass"/>'s local-light cube array, or tell it there
    /// is nothing to sample. The per-slot position and far plane are the ones each cube was RENDERED with --
    /// the receiver never re-derives them from the live light, which is what keeps a carried torch's shadow
    /// attached to the torch. Same freshness gate as the cascades.</summary>
    private static void UploadLocalCubes()
        => UploadLocalCubes(_litCubeTex, _litCubeLight, _litCubePlane, _litCubeCount, _litCubeSoft);

    /// <summary>#121: point the lit program at the IBL probe, or tell it there is nothing to sample
    /// (<c>uProbeMips</c> 0). Unbinding rather than leaving the cube bound during a capture is not
    /// belt-and-braces: sampling a texture that is an attachment of the framebuffer being written is
    /// undefined, and the capture draws through this very program.</summary>
    private static void UploadProbe() => UploadProbe(_litProbeMips, _litProbeGain, _litSceneEnv);

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
        else if (!_ambientFallbackLogged)
        {
            _ambientFallbackLogged = true;
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
    internal static void UploadSunCascades(int locTex, int locVp, int locTexel, int locBias, int locCount)
    {
        if (locCount < 0) return;
        if (!CasterPass.CascadesFresh) { Gl.Uniform1i(locCount, 0); return; }
        int n = CasterPass.CascadeCount;
        Gl.ActiveTexture(Gl.TEXTURE0 + SunUnit);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, CasterPass.CascadeTex);
        Gl.ActiveTexture(Gl.TEXTURE0);       // leave unit 0 current: the batch loop rebinds diffuse textures
        Gl.Uniform1i(locTex, (int)SunUnit);
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
        Gl.Uniform1i(locCount, n);
    }

    /// <summary>#127: the local-cube receiver's uniforms, into a named program's locations.</summary>
    internal static void UploadLocalCubes(int locTex, int locLight, int locPlane, int locCount, int locSoft)
    {
        if (locCount < 0) return;
        // #139: the softness pair goes out even when there are no cubes -- it is program state, not per-cube
        // state, and a program that only ever sees the no-cubes path would otherwise keep the zeroes.
        if (locSoft >= 0) Gl.Uniform2f(locSoft, AlloyLights.ShadowSoft, AlloyLights.ShadowNearWorld);
        if (!CasterPass.CubesFresh) { Gl.Uniform1i(locCount, 0); return; }
        int n = CasterPass.CubeCount;
        Gl.ActiveTexture(Gl.TEXTURE0 + CubeUnit);
        Gl.BindTexture(Gl.TEXTURE_CUBE_MAP_ARRAY, CasterPass.CubeTex);
        Gl.ActiveTexture(Gl.TEXTURE0);       // leave unit 0 current: the batch loop rebinds diffuse textures
        Gl.Uniform1i(locTex, (int)CubeUnit);
        fixed (float* l = CasterPass.CubeLight) Gl.Uniform4fv(locLight, n, l);
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
