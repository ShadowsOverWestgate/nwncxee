using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The filtered cover map: the exposure read the lit stage makes is one bilinear fetch of a
/// variance shadow map (VSM: depth and depth squared, Gaussian-blurred) baked from the cover layer, not a
/// set of compares. Fixed compare taps make plateaus across the feather (nine taps, nine visible bands under
/// an awning); rotating them per pixel makes grain only TAA hides, and most players run SMAA. A pre-filtered
/// map has neither: the blur runs once per cover BAKE (held frames pay nothing) and the edge is as wide as
/// the "Cover edge" knob says. VSM and not ESM, which this first shipped as: an exponential map's ramp is
/// 1/(c x roof height) of the feather -- under a 5 m roof the 0.5 m knob drew a 2 cm edge, and 2 m drew
/// 10 cm -- where Chebyshev's ramp spans the kernel whatever stands above. Shares the snapshot-depth unit,
/// re-bound at every lit program bind like the SSAO term; the OIT accumulate takes that unit back, and the
/// stage falls back to a hard compare for those draws (uOit != 0).</summary>
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

    /// <summary>After a cover bake landed: filter it. <paramref name="array"/> is the sun array the cover
    /// layer rides; the view onto it is remade when the array or the layer changes.</summary>
    internal static void FilterCover(uint array, int layer, int mapSize, float radius)
    {
        _esmValid = false;
        if (_esmFailed || Gl.TextureView == null || Gl.TexStorage2D == null) return;
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
    internal static void EsmRelease() => _esmValid = false;

    private static bool EnsureEsm(uint array, int layer, int size)
    {
        if (_esmProg == 0)
        {
            _esmProg = GlProgram.Graphics(PostChain.FullscreenVs, EsmFs, _log, "Weather cover filter");
            if (_esmProg == 0) { _esmFailed = true; _log?.Info("Weather: cover filter program failed -- the cover edge falls back to a hard compare"); return false; }
            _esmLocSrc = GlProgram.Loc(_esmProg, "uSrc"); _esmLocStep = GlProgram.Loc(_esmProg, "uStep"); _esmLocC = GlProgram.Loc(_esmProg, "uFirst");
            uint v; Gl.GenVertexArrays(1, &v); _esmVao = v;
            uint f; Gl.GenFramebuffers(1, &f); _esmFbo = f;
        }
        if (_esmView == 0 || _esmViewOf != array || _esmLayer != layer)
        {
            if (_esmView != 0) { uint t = _esmView; Gl.DeleteTextures(1, &t); }
            uint t2; Gl.GenTextures(1, &t2);
            Gl.DrainErrors();
            Gl.TextureView(t2, Gl.TEXTURE_2D, array, Gl.DEPTH_COMPONENT24, 0, 1, (uint)layer, 1);
            if (Gl.GetError() != 0) { Gl.DeleteTextures(1, &t2); _esmFailed = true; _log?.Info("Weather: no texture view onto the cover layer -- the cover edge falls back to a hard compare"); return false; }
            Gl.BindTexture(Gl.TEXTURE_2D, t2);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_COMPARE_MODE, 0);
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
            _esmView = t2; _esmViewOf = array; _esmLayer = layer;
        }
        if (_esmSize != size)
        {
            if (_esmTex0 != 0) { uint* ts = stackalloc uint[2]; ts[0] = _esmTex0; ts[1] = _esmTex1; Gl.DeleteTextures(2, ts); }
            uint* n = stackalloc uint[2]; Gl.GenTextures(2, n);
            for (int i = 0; i < 2; i++)
            {
                Gl.BindTexture(Gl.TEXTURE_2D, n[i]);
                Gl.TexStorage2D(Gl.TEXTURE_2D, 1, Gl.RG32F, size, size);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
                Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
            }
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
            _esmTex0 = n[0]; _esmTex1 = n[1]; _esmSize = size;
            _log?.Info($"Weather: cover filter {size}x{size} RG32F");
        }
        return true;
    }

    private static void EsmLost() { _esmView = 0; _esmViewOf = 0; _esmLayer = -1; _esmTex0 = _esmTex1 = 0; _esmSize = 0; _esmFbo = 0; _esmProg = 0; _esmVao = 0; _esmValid = false; _esmFailed = false; }

    /// <summary>One blur axis of the two moments, a Gaussian over 13 taps at uStep. The first axis reads the
    /// depth view (uFirst: r is the depth, its square is made here); the second reads the moment pair.</summary>
    internal const string EsmFs = """
        #version 460 core
        in vec2 vUV;
        uniform sampler2D uSrc;
        uniform vec2 uStep;
        uniform int uFirst;
        out vec2 frag;
        void main()
        {
            vec2 s = vec2(0.0); float wsum = 0.0;
            for (int i = -6; i <= 6; i++)
            {
                float w = exp(-float(i * i) / 18.0);
                vec2 m = texture(uSrc, vUV + uStep * float(i)).rg;
                if (uFirst != 0) m.y = m.x * m.x;
                s += w * m;
                wsum += w;
            }
            frag = s / wsum;
        }
        """;
}
