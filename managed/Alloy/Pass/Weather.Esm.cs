using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The filtered cover map: the exposure read the lit stage makes is one bilinear fetch of a variance
/// shadow map (depth and depth squared, Gaussian-blurred) rather than a set of compares -- fixed taps band
/// across the feather, rotated ones grain. Shares the snapshot-depth unit, re-bound at every lit program
/// bind; the OIT accumulate takes that unit back, so those draws fall back to a hard compare.</summary>
internal static unsafe partial class Weather
{
    /// <summary>Metres of height a receiver is forgiven -- the blur mixes neighbouring ground heights across the feather.</summary>
    private const float EsmBiasM = 0.3f;
    /// <summary>Metres of depth spread below which the variance is clamped: a flat roof's texels all agree, and a
    /// zero variance would put the whole penumbra on one texel.</summary>
    private const float EsmMinSpreadM = 0.1f;
    private const int EsmSizeMax = 1024;

    private static uint _esmView, _esmViewOf, _esmTex0, _esmTex1, _esmFbo, _esmProg, _esmVao;
    private static int _esmSize, _esmLayer = -1, _esmLocSrc, _esmLocStep, _esmLocC;
    private static bool _esmFailed, _esmValid;
    private static float _esmC, _esmBias;
    internal static int EsmBakes;
    /// <summary>The cover map's side at its last bake: what a knob-driven shelter re-bake sizes the cache by.</summary>
    private static int _coverMapSize;

    /// <summary>After a cover bake landed: filter it. <paramref name="array"/> is the sun array the cover
    /// layer rides; the view onto it is remade when the array or the layer changes.</summary>
    internal static void FilterCover(uint array, int layer, int mapSize, float radius)
    {
        _esmValid = false; _shelValid = false;
        if (_esmFailed || Gl.TextureView == null) return;
        int size = mapSize < EsmSizeMax ? mapSize : EsmSizeMax;
        if (!EnsureEsm(array, layer, size)) return;
        int* save = stackalloc int[6]; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, save); Gl.GetIntegerv(Gl.VIEWPORT, save + 1);
        int prog; Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prog);
        // depth units: the cover view spans CoverDepthRadii x radius; the feather is half the knob each side
        float spread = EsmMinSpreadM / (CoverDepthRadii * radius);
        _esmC = spread * spread;
        _esmBias = EsmBiasM / (CoverDepthRadii * radius);
        float half = CoverSoft.V * 0.25f / radius;   // half the knob, in uv (the fit is 2 x radius across)
        new PassState(depth: false, depthWrite: false).Apply();
        Gl.ColorMask(1, 1, 1, 1);
        Gl.UseProgram(_esmProg);
        Gl.BindVertexArray(_esmVao);
        Gl.Uniform1i(_esmLocSrc, 0);
        Gl.Viewport(0, 0, size, size);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _esmFbo);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _esmTex0, 0);
        Gl.BindTexture(Gl.TEXTURE_2D, _esmView);
        Gl.Uniform1i(_esmLocC, 1);
        Gl.Uniform2f(_esmLocStep, half / 6f, 0f);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _esmTex1, 0);
        Gl.BindTexture(Gl.TEXTURE_2D, _esmTex0);
        Gl.Uniform1i(_esmLocC, 0);
        Gl.Uniform2f(_esmLocStep, 0f, half / 6f);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.BindVertexArray(0);
        new PassState(depth: true, depthWrite: true).Apply();   // the bracket's own state, for whatever bakes next
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)save[0]);
        Gl.Viewport(save[1], save[2], save[3], save[4]);
        Gl.UseProgram((uint)prog);
        _esmValid = true; EsmBakes++;
        // R2: the same bake, latched into the area wetness map -- the depth view is already in hand here
        // and nowhere else, and this is the one call site a new cover bake reaches.
        MapScatter(_esmView, radius);
        _coverMapSize = mapSize;
        ShelterBake(_esmView, mapSize);
        Gl.UseProgram((uint)prog);
    }

    /// <summary>Per lit program bind: the filtered map on its unit (0 = none, and the stage's parameter says so).
    /// Under OIT the unit is NOT touched: the pass bound the opaque depth there and the list program's early-Z
    /// fetches it by pixel -- a top-down map in its place discards in a screen-glued shape. Those draws take
    /// the hard compare.</summary>
    internal static void BindEsm(int locParams, bool oit)
    {
        bool on = !oit && _esmValid && Cover.V && CasterPass.CoverFresh;
        if (!oit)
        {
            Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.CoverEsm);
            Gl.BindTexture(Gl.TEXTURE_2D, on ? _esmTex1 : 0);
            Gl.ActiveTexture(Gl.TEXTURE0);
        }
        if (locParams >= 0) Gl.Uniform4f(locParams, _esmC, _esmBias, on ? 1f : 0f, 0f);
    }

    /// <summary>The cover map is gone with its bake (area edge, flush failure): so is the filtered copy.</summary>
    internal static void EsmRelease() { _esmValid = false; ShelterRelease(); }

    /// <summary>The cover layer as PLAIN depth -- the same <c>glTextureView</c> the filter reads,
    /// compare mode off -- or 0 while there is none. The splash pass reconstructs each impact's landing
    /// point from it; the compare sampler the drop stage uses cannot be read for a depth, only tested.</summary>
    internal static uint CoverDepthView => _esmValid ? _esmView : 0;

    private static bool EnsureEsm(uint array, int layer, int size)
    {
        if (_esmProg == 0)
        {
            _esmProg = GlProgram.Graphics(PostChain.FullscreenVs, EsmFs, _log, "Weather cover filter");
            if (_esmProg == 0) { _esmFailed = true; _log?.Info("Weather: cover filter program failed -- the cover edge falls back to a hard compare"); return false; }
            _esmLocSrc = GlProgram.Loc(_esmProg, "uSrc"); _esmLocStep = GlProgram.Loc(_esmProg, "uStep"); _esmLocC = GlProgram.Loc(_esmProg, "uFirst");
            uint v; Gl.CreateVertexArrays(1, &v); _esmVao = v;
            uint f; Gl.GenFramebuffers(1, &f); _esmFbo = f;
        }
        if (_esmView == 0 || _esmViewOf != array || _esmLayer != layer)
        {
            if (_esmView != 0) { uint t = _esmView; Gl.DeleteTextures(1, &t); }
            uint t2; Gl.GenTextures(1, &t2);   // glTextureView wants a name with no object yet: Gen, not Create
            Gl.DrainErrors();
            Gl.TextureView(t2, Gl.TEXTURE_2D, array, Gl.DEPTH_COMPONENT24, 0, 1, (uint)layer, 1);
            if (Gl.GetError() != 0) { Gl.DeleteTextures(1, &t2); _esmFailed = true; _log?.Info("Weather: no texture view onto the cover layer -- the cover edge falls back to a hard compare"); return false; }
            Gl.TextureParameteri(t2, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
            Gl.TextureParameteri(t2, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
            Gl.TextureParameteri(t2, Gl.TEXTURE_COMPARE_MODE, 0);
            _esmView = t2; _esmViewOf = array; _esmLayer = layer;
        }
        if (_esmSize != size)
        {
            if (_esmTex0 != 0) { uint* ts = stackalloc uint[2]; ts[0] = _esmTex0; ts[1] = _esmTex1; Gl.DeleteTextures(2, ts); }
            uint* n = stackalloc uint[2]; Gl.CreateTextures(Gl.TEXTURE_2D, 2, n);
            for (int i = 0; i < 2; i++)
            {
                Gl.TextureStorage2D(n[i], 1, Gl.RG32F, size, size);
                RenderTarget.Clamp(n[i], Gl.LINEAR);
            }
            _esmTex0 = n[0]; _esmTex1 = n[1]; _esmSize = size;
            _log?.Info($"Weather: cover filter {size}x{size} RG32F");
        }
        return true;
    }

    private static void EsmLost() { _esmView = 0; _esmViewOf = 0; _esmLayer = -1; _esmTex0 = _esmTex1 = 0; _esmSize = 0; _esmFbo = 0; _esmProg = 0; _esmVao = 0; _esmValid = false; _esmFailed = false; }

    /// <summary>One blur axis of the two moments, a Gaussian over 13 taps at uStep. The first axis reads the
    /// depth view (uFirst: r is the depth, its square is made here); the second reads the moment pair.</summary>
    internal static string EsmFs => ShaderSource.Get("cx_esm_f.glsl");
}
