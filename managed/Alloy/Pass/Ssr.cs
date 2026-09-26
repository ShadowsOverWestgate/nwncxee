using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// CX-51: the lit stage's screen-space reflections. The frame's colour and depth are copied at
/// <see cref="PassId.FbCopy"/> -- after the opaque set, grass, the sky and the blended class -- with the camera
/// they were drawn by, and the depth is reduced into a hi-Z pyramid of inverse view depth (<c>cx_ssrhiz_c</c>)
/// that the NEXT frame's lit and grass fragments trace (<c>cx_ssr.glsl</c>). One frame of lag instead of a
/// G-buffer: the fragment already has the normal, roughness and weight its reflection uses.
/// <para>Its own pair, not <see cref="FrameSnapshot"/>'s: that copy is taken only with water on screen, often
/// over a rectangle, and colour-only for the grass root blend. Shader-only plus this pass on Windows: no
/// symbol, no offset.</para>
/// </summary>
internal static unsafe class Ssr
{
    /// <summary>Last frame's colour, mipmapped for rough reflections, and its hi-Z pyramid. Past the 0..15 budget
    /// on <see cref="TextureSlots.LocalShadowRaw"/>'s argument, bound by <c>layout(binding)</c> in the stage.</summary>
    internal const uint ColorUnit = TextureSlots.SsrColor, HiZUnit = TextureSlots.SsrHiZ;

    private static uint _fbo, _col, _dep, _depMask, _hiz, _progHiZ;
    private static int _w, _h, _levels, _locLevel, _locNf;
    private static bool _hdr, _valid;
    private static Matrix4x4 _view, _proj;
    private static float _near, _far;
    private static readonly StandDown _down = new("Ssr");
    private static ExtenderLog? _log;

    /// <summary>History copies taken this period, for the [alloy] line.</summary>
    private static int _copies;

    /// <summary>Cell visits per ray at each finest level (full, half, quarter): a coarser confirm needs fewer.</summary>
    private static ReadOnlySpan<float> Steps => [40f, 24f, 16f];

    internal static bool On => Lighting.Ssr.V && !_down.Failed;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Context loss: the names died with it.</summary>
    internal static void Forget() { _fbo = _col = _dep = _hiz = _progHiZ = 0; _w = _h = _levels = 0; _valid = false; }

    /// <summary>World edge: the history is another area's.</summary>
    internal static void Revoke() => _valid = false;

    internal static string Stats(int frames)
    {
        string s = $"ssr {(On ? "on" : "off")} ({_copies} copies{(_down.Reason.Length > 0 ? ", last refusal: " + _down.Reason : "")})";
        _copies = 0;
        return s;
    }

    /// <summary>Copy the bound frame target's colour and depth into the history, and remember the camera that
    /// drew them. Restores the framebuffer binding. A refusal leaves no history, which the stage reads as
    /// "probe only".</summary>
    internal static void Take(int* viewport, Matrix4x4* view, Matrix4x4* proj, float near, float far)
    {
        _valid = false;
        if (!On) return;
        if (Gl.BlitFramebuffer == null || Gl.GenerateMipmap == null || Gl.DispatchCompute == null || Gl.BindImageTexture == null)
        { _down.Fail(_log, "glBlitFramebuffer, glGenerateMipmap, compute or image stores are missing", "screen-space reflections are off for the session"); return; }
        if (_progHiZ == 0 && !BuildProgram()) return;
        if (viewport[0] != 0 || viewport[1] != 0) { _down.Refuse(_log, "the pass viewport is offset", "no reflections this frame"); return; }
        int w = viewport[2], h = viewport[3];
        if (w <= 0 || h <= 0) return;
        int src; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &src);
        if (!EnsureTargets((uint)src, w, h)) { Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)src); return; }
        Gl.DrainErrors(4);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, (uint)src);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _fbo);
        Gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, Gl.COLOR_BUFFER_BIT | _depMask, (uint)Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)src);
        uint err = Gl.GetError == null ? 0 : Gl.GetError();
        if (err != 0) { _down.Fail(_log, $"the history blit raised 0x{err:x}", "screen-space reflections are off for the session"); return; }
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, _col);
        Gl.GenerateMipmap(Gl.TEXTURE_2D);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        BuildPyramid(near, far);
        _view = *view; _proj = *proj; _near = near; _far = far;
        _valid = true;
        _copies++;
    }

    /// <summary>The block's three members: the history camera and (roughness cap, cell visits, near, finest
    /// level); left zero (off) with no history.</summary>
    internal static void FillPass(byte* b)
    {
        if (!_valid || !On) return;
        *(Matrix4x4*)(b + AlloyRenderer.PbSsrView) = _view;
        *(Matrix4x4*)(b + AlloyRenderer.PbSsrProj) = _proj;
        int fine = Lighting.SsrDetail.V;
        AlloyRenderer.PassVec(b, AlloyRenderer.PbSsr, Lighting.SsrRough.V, Steps[fine], _near, fine);
    }

    /// <summary>From every lit-program and grass bind: the pair on its units, or nothing without a history.</summary>
    internal static void Bind()
    {
        bool live = _valid && On;
        Gl.ActiveTexture(Gl.TEXTURE0 + ColorUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, live ? _col : 0);
        Gl.ActiveTexture(Gl.TEXTURE0 + HiZUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, live ? _hiz : 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    private static bool BuildProgram()
    {
        _progHiZ = GlProgram.Compute("cx_ssrhiz_c", ShaderSource.Get("cx_ssrhiz_c.glsl"), _log, "Ssr hi-Z pyramid");
        if (_progHiZ == 0) return _down.Fail(_log, "the hi-Z program did not build", "screen-space reflections are off for the session");
        _locLevel = GlProgram.Loc(_progHiZ, "uLevel");
        _locNf = GlProgram.Loc(_progHiZ, "uNf");
        return true;
    }

    /// <summary>Reduce the fresh depth copy into the pyramid, one dispatch per level. Compute, so no raster
    /// state of the mid-frame pass is touched; the program and the two units it borrows are put back.</summary>
    private static void BuildPyramid(float near, float far)
    {
        int prev; Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prev);
        Gl.UseProgram(_progHiZ);
        Gl.Uniform2f(_locNf, near, far);
        Gl.ActiveTexture(Gl.TEXTURE0 + HiZUnit);
        for (int i = 0; i < _levels; i++)
        {
            int w = Math.Max(_w >> i, 1), h = Math.Max(_h >> i, 1);
            if (i == 0) Gl.BindTexture(Gl.TEXTURE_2D, _dep);
            else
            {
                // reading level i-1 while level i is the image: legal, the two never alias
                Gl.BindTexture(Gl.TEXTURE_2D, _hiz);
                Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT);
            }
            Gl.Uniform1i(_locLevel, i);
            Gl.BindImageTexture(0, _hiz, i, 0, 0, Gl.WRITE_ONLY, Gl.R32F);
            Gl.DispatchCompute((uint)(w + 7) / 8, (uint)(h + 7) / 8, 1);
        }
        Gl.MemoryBarrier(Gl.TEXTURE_FETCH_BARRIER_BIT);
        Gl.BindImageTexture(0, 0, 0, 0, 0, Gl.WRITE_ONLY, Gl.R32F);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.UseProgram((uint)prev);
    }

    /// <summary>The pair at the frame's size and formats: the depth half must match the source's for the blit
    /// (<see cref="FrameSnapshot"/>'s finding), the colour half its HDR-ness.</summary>
    private static bool EnsureTargets(uint src, int w, int h)
    {
        bool hdr = PostChain.Armed;
        if (_fbo != 0 && _w == w && _h == h && _hdr == hdr) return true;
        uint fmt = RenderTarget.ProbeDepthFormat(src);
        if (fmt == 0) return _down.Refuse(_log, "the pass target has no depth attachment", "no reflections this frame");
        bool packed = RenderTarget.Packed(fmt);
        _depMask = packed ? Gl.STENCIL_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT : Gl.DEPTH_BUFFER_BIT;
        if (_fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo = f; }
        if (_col == 0) { uint t; Gl.GenTextures(1, &t); _col = t; }
        if (_dep == 0) { uint t; Gl.GenTextures(1, &t); _dep = t; }
        RenderTarget.AllocColor(_col, hdr, w, h, Gl.LINEAR);
        Gl.BindTexture(Gl.TEXTURE_2D, _col);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR_MIPMAP_LINEAR);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        RenderTarget.AllocDepth(_dep, fmt, w, h, Gl.NEAREST);
        // immutable storage: a new size is a new name
        if (_hiz != 0) { uint t = _hiz; Gl.DeleteTextures(1, &t); }
        int levels = 1;
        while ((w >> levels) > 0 || (h >> levels) > 0) levels++;
        { uint t; Gl.GenTextures(1, &t); _hiz = t; }
        Gl.BindTexture(Gl.TEXTURE_2D, _hiz);
        Gl.TexStorage2D(Gl.TEXTURE_2D, levels, Gl.R32F, w, h);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST_MIPMAP_NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        _levels = levels;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _col, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, packed ? Gl.DEPTH_STENCIL_ATTACHMENT : Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, _dep, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _w = _h = 0;
            return _down.Fail(_log, $"the history framebuffer is incomplete (0x{status:x})", "screen-space reflections are off for the session");
        }
        _w = w; _h = h; _hdr = hdr;
        _log?.Info($"Ssr: history {w}x{h}, colour {(hdr ? "RGBA16F" : "RGBA8")}, depth 0x{fmt:x}, hi-Z {_levels} levels");
        return true;
    }
}
