using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// CX-189: the alpha-to-coverage layer -- the one multisampled target in the Alloy frame, for the cutout parts
/// whose material asks for coverage instead of a hard cut (<c>parameter int NWN_ALPHA_MODE 2</c>, on any
/// <c>.mtr</c>; hair is the first user). Everything else stays single-sample and deferred; only these parts pay for samples.
/// <para>After <see cref="PassId.DeferredLights"/>, so the frame under the parts is finished: <see cref="Begin"/>
/// copies the lit colour and depth into EVERY sample, the parts draw forward with coverage from their alpha, and
/// <see cref="End"/> box-resolves the colour back. An uncovered sample still holds the background, so the plain
/// resolve IS "the covered samples' mean over the background, by coverage" -- no custom colour resolve. The depth
/// goes back where half the samples or more were covered (<c>cx_a2cdepth_f</c>), which is what the passes after
/// it (SSR, OIT, water, particles, fog, the finished-depth motion vectors) test against. The G-buffer, light
/// record and SSR record under such a pixel are the background's; each reader drops a record whose view depth
/// no longer matches the frame's.</para>
/// <para>Scissored to the screen rectangle of the parts it draws, so the seed and the resolve cost the parts'
/// footprint, not the frame. Stands down (logged once, the parts draw as ordinary cutouts) on a driver without
/// multisampled storage or a target that will not complete.</para>
/// </summary>
internal static unsafe class CoverageLayer
{
    /// <summary>The alpha below which a fragment is discarded outright: a coverage of nothing at any sample count.</summary>
    internal const float Cut = 0.01f;

    private static readonly StandDown _down = new("CoverageLayer");
    private static ExtenderLog? _log;
    private static uint _fbo, _resolveFbo, _col, _dep, _progSeed, _progDepth;
    private static int _w, _h, _samples, _maxSamples;
    private static int _uSeedHdr = -1, _uSeedDepth = -1, _uDepthMs = -1, _uDepthN = -1;
    private static int _rx, _ry, _rw, _rh;
    private static long _runs, _pixels, _frameArea, _released;
    private static int _idle;
    private static bool _msWas, _cullWas;
    /// <summary>Frames with nothing for the layer before its targets are freed: they are full-frame and
    /// multisampled (177 MB at 4x and 2560x1440), so an area without coverage content should not keep them.</summary>
    private const int IdleFrames = 600;

    /// <summary>Will the layer try to open this frame? False hands the coverage tail back to the cutout pass.</summary>
    internal static bool Armed => Renderer.A2c.V >= 2 && !_down.Failed;
    /// <summary>The armed layer's sample count, for the mask variant's <c>uA2cSamples</c>.</summary>
    internal static int Samples => _samples;
    /// <summary>The layer shapes its own sample mask this run (<c>alloy.a2cmask</c>, and the variant built).</summary>
    internal static bool Masked { get; private set; }

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Context loss: every name went with the context.</summary>
    internal static void Forget()
    {
        _fbo = 0; _resolveFbo = 0; _col = 0; _dep = 0; _progSeed = 0; _progDepth = 0;
        _w = 0; _h = 0; _samples = 0; _maxSamples = 0;
        _down.Forget();
    }

    /// <summary>Open the layer over the viewport-space rectangle (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="w"/>, <paramref name="h"/>): seed it from the frame and leave it bound for the coverage
    /// draws. False = the layer is off or stood down; the caller draws the parts as cutouts instead.</summary>
    internal static bool Begin(int* viewport, int x, int y, int w, int h)
    {
        int want = Renderer.A2c.V;
        if (want < 2 || _down.Failed || w <= 0 || h <= 0) return false;
        if (Gl.TextureStorage2DMultisample == null || Gl.BlitFramebuffer == null)
            return _down.Fail(_log, "glTextureStorage2DMultisample is missing", "NWN_ALPHA_MODE 2 parts draw as cutouts");
        if (_maxSamples == 0) { int m = 0; Gl.GetIntegerv(Gl.MAX_SAMPLES, &m); _maxSamples = m; }
        int samples = want;
        while (samples > _maxSamples && samples > 2) samples >>= 1;
        if (samples > _maxSamples)
            return _down.Fail(_log, "the driver has no multisampled targets", "NWN_ALPHA_MODE 2 parts draw as cutouts");
        int fw = viewport[0] + viewport[2], fh = viewport[1] + viewport[3];
        if (!EnsureTargets(fw, fh, samples) || !EnsurePrograms()) return false;
        Masked = Renderer.A2cMask.V && Programs.EnsureMaskProgram();

        _rx = x; _ry = y; _rw = w; _rh = h; _idle = 0;
        _msWas = Gl.IsEnabled(Gl.MULTISAMPLE) != 0; _cullWas = Gl.IsEnabled(Gl.CULL_FACE) != 0;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.Enable(Gl.SCISSOR_TEST);
        Gl.Scissor(x, y, w, h);
        Gl.UseProgram(_progSeed);
        Gl.BindTextureUnit(TextureSlots.CoverSeed, PostChain.HdrTex);
        Gl.BindTextureUnit(TextureSlots.CoverSeedDepth, PostChain.DepthTex);
        Gl.Uniform1i(_uSeedHdr, (int)TextureSlots.CoverSeed);
        Gl.Uniform1i(_uSeedDepth, (int)TextureSlots.CoverSeedDepth);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.ALWAYS); Gl.DepthMask(1);
        Gl.Disable(Gl.BLEND); Gl.Disable(Gl.CULL_FACE);
        Gl.BindVertexArray(PostChain.FullscreenVao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindVertexArray(0);
        Gl.BindTextureUnit(TextureSlots.CoverSeed, 0);
        Gl.BindTextureUnit(TextureSlots.CoverSeedDepth, 0);
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.Enable(Gl.MULTISAMPLE);
        if (!Masked) Gl.Enable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        _runs++; _pixels += (long)w * h; _frameArea += (long)viewport[2] * viewport[3];
        return true;
    }

    /// <summary>Close the layer: colour box-resolved into the HDR target, depth written back where the layer
    /// covered half a pixel or more, and the frame's framebuffer bound again with the depth test at LEQUAL.</summary>
    internal static void End(int* viewport)
    {
        Gl.Disable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _fbo);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _resolveFbo);
        // the HDR target is re-made on a resize, and this layer may not be
        Gl.FramebufferTexture2D(Gl.DRAW_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, PostChain.HdrTex, 0);
        Gl.BlitFramebuffer(_rx, _ry, _rx + _rw, _ry + _rh, _rx, _ry, _rx + _rw, _ry + _rh, Gl.COLOR_BUFFER_BIT, Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
        Gl.UseProgram(_progDepth);
        Gl.BindTextureUnit(TextureSlots.CoverSeedDepth, _dep);
        Gl.Uniform1i(_uDepthMs, (int)TextureSlots.CoverSeedDepth);
        Gl.Uniform1i(_uDepthN, _samples);
        Gl.ColorMask(0, 0, 0, 0);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LESS); Gl.DepthMask(1);
        Gl.Disable(Gl.CULL_FACE);
        Gl.BindVertexArray(PostChain.FullscreenVao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindVertexArray(0);
        Gl.ColorMask(1, 1, 1, 1);
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.Disable(Gl.SCISSOR_TEST);
        if (!_msWas) Gl.Disable(Gl.MULTISAMPLE);
        if (_cullWas) Gl.Enable(Gl.CULL_FACE);
        Gl.BindTextureUnit(TextureSlots.CoverSeedDepth, 0);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
    }

    /// <summary>A frame with no coverage content: after <see cref="IdleFrames"/> of them the targets go.</summary>
    internal static void Idle()
    {
        if (_col == 0 || ++_idle < IdleFrames) return;
        Release();
        _released++;
    }

    private static void Release()
    {
        if (_col != 0) { uint c = _col; Gl.DeleteTextures(1, &c); }
        if (_dep != 0) { uint d = _dep; Gl.DeleteTextures(1, &d); }
        _col = 0; _dep = 0; _w = 0; _h = 0; _samples = 0; _idle = 0;
    }

    private static bool EnsureTargets(int w, int h, int samples)
    {
        if (_col != 0 && w == _w && h == _h && samples == _samples) return true;
        Release();
        if (_fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo = f; Gl.GenFramebuffers(1, &f); _resolveFbo = f; }
        uint t;
        Gl.CreateTextures(Gl.TEXTURE_2D_MULTISAMPLE, 1, &t); _col = t;
        Gl.TextureStorage2DMultisample(_col, samples, Gl.RGBA16F, w, h, 1);
        Gl.CreateTextures(Gl.TEXTURE_2D_MULTISAMPLE, 1, &t); _dep = t;
        // 32F holds the seeded D24 value exactly, so the write-back's GL_LESS fails on an untouched pixel
        Gl.TextureStorage2DMultisample(_dep, samples, Gl.DEPTH_COMPONENT32F, w, h, 1);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D_MULTISAMPLE, _col, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D_MULTISAMPLE, _dep, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _resolveFbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, PostChain.HdrTex, 0);
        uint resolve = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
        if (status != Gl.FRAMEBUFFER_COMPLETE || resolve != Gl.FRAMEBUFFER_COMPLETE)
        {
            _log?.Info($"CoverageLayer: {samples}x {w}x{h} layer 0x{status:x}, resolve 0x{resolve:x}");
            Release();
            return _down.Fail(_log, "the multisampled layer is incomplete", "NWN_ALPHA_MODE 2 parts draw as cutouts");
        }
        _w = w; _h = h; _samples = samples;
        _log?.Info($"CoverageLayer: {samples}x RGBA16F + D32F at {w}x{h} ({(long)w * h * samples * 12 / (1024 * 1024)} MB)");
        return true;
    }

    private static bool EnsurePrograms()
    {
        if (_progSeed != 0) return true;
        _progSeed = GlProgram.Graphics(PostChain.FullscreenVs, ShaderSource.Get("cx_a2cseed_f.glsl"), _log, "CoverageLayer seed");
        _progDepth = GlProgram.Graphics(PostChain.FullscreenVs, ShaderSource.Get("cx_a2cdepth_f.glsl"), _log, "CoverageLayer depth");
        if (_progSeed == 0 || _progDepth == 0)
        {
            if (_progSeed != 0) Gl.DeleteProgram(_progSeed);
            if (_progDepth != 0) Gl.DeleteProgram(_progDepth);
            _progSeed = 0; _progDepth = 0;
            return _down.Fail(_log, "a layer program did not build", "NWN_ALPHA_MODE 2 parts draw as cutouts");
        }
        _uSeedHdr = GlProgram.Loc(_progSeed, "uHdr"); _uSeedDepth = GlProgram.Loc(_progSeed, "uDepth");
        _uDepthMs = GlProgram.Loc(_progDepth, "uDepthMs"); _uDepthN = GlProgram.Loc(_progDepth, "uSamples");
        return true;
    }

    internal static string Stats(int frames)
    {
        long n = _runs, px = _pixels, area = _frameArea, rel = _released;
        _runs = 0; _pixels = 0; _frameArea = 0; _released = 0;
        if (_down.Failed) return $"a2c STOOD DOWN ({_down.Reason})";
        if (n == 0) return Renderer.A2c.V < 2 ? "a2c off" : rel > 0 ? "a2c idle, targets released" : "a2c idle";
        return $"a2c {(float)n / System.Math.Max(frames, 1):F2} runs/frame, {_samples}x{(Masked ? " masked" : "")}, " +
               $"scissor {100.0 * px / System.Math.Max(area, 1):F1}% of the frame";
    }
}
