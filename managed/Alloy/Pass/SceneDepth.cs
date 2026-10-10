using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// A depth-only snapshot of the Alloy frame, taken where the caller asks for it, because the frame's ONE
/// framebuffer copy (<see cref="FrameSnapshot"/>) is taken before water and only when there is water on
/// screen. Sampling the bound depth attachment instead is undefined in GL -- depth writes being off is not
/// the attachment being read-only -- so: a copy, allocated by <see cref="RenderTarget.AllocDepth"/>.
/// </summary>
internal static unsafe class SceneDepth
{
    /// <summary>Texture unit the copy binds on. Above <see cref="FrameSnapshot.DepthUnit"/> so the two can be
    /// live at once -- water samples its own copy earlier in the same frame.</summary>
    internal const uint Unit = TextureSlots.SceneDepth;

    /// <summary>True when the copy taken this pass is usable. False is a clean stand-down: the caller draws
    /// without softness rather than not drawing.</summary>
    internal static bool Ready { get; private set; }
    internal static int Takes;

    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so no name or probed
    /// format from the dead context survives as a field nobody remembered to zero.</summary>
    private sealed class DepthCopyState
    {
        public uint Fbo, Tex;
        /// <summary>The source attachment's depth format, and the blit mask it implies.</summary>
        public uint DepFormat, DepMask;
        public int W, H;
        /// <summary>The (PassGen, source) the current copy was taken for; a repeat Take with the same key
        /// and viewport reuses it. -1 = no reusable copy.</summary>
        public int TakenGen = -1, TakenSrc;
    }

    private static DepthCopyState _gl = new();
    private static readonly StandDown _down = new("SceneDepth");
    private static ExtenderLog? _log;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Snapshot the bound draw target's depth. Every exit rebinds the source framebuffer: leaving
    /// the copy's FBO bound would send the rest of the frame -- the probe capture, the resolve and the
    /// engine's whole residual bucket walk -- into an incomplete target, which is a lost frame rather than
    /// the stand-down this promises.</summary>
    internal static bool Take(int* viewport)
    {
        long tBlit = FrameZones.Now;
        try { return TakeInner(viewport); }
        finally { FrameZones.Add(FrameZones.Zone.Blit, tBlit); }
    }

    private static bool TakeInner(int* viewport)
    {
        int src; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &src);
        if (Ready && _gl.TakenGen == AlloyRenderer.PassGen && _gl.TakenSrc == src
            && viewport[0] == 0 && viewport[1] == 0 && viewport[2] == _gl.W && viewport[3] == _gl.H) return true;
        Ready = false;
        _gl.TakenGen = -1;
        if (_down.Failed) return false;
        if (Gl.BlitFramebuffer == null || Gl.GenFramebuffers == null || Gl.FramebufferTexture2D == null
            || Gl.GetFramebufferAttachmentParameteriv == null) return Fail("a required GL entry point is missing");
        // An offset viewport breaks the 1:1 gl_FragCoord-to-texel mapping the depth fetch assumes. Refuse
        // rather than sample the wrong texels -- softness off is invisible, softness against a shifted depth
        // buffer is a halo that moves with the camera.
        if (viewport[0] != 0 || viewport[1] != 0) return false;
        int w = viewport[2], h = viewport[3];
        if (w <= 0 || h <= 0) return false;

        if (!Probe((uint)src, w, h) || !Ensure(w, h))
        { Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)src); return false; }

        Gl.DrainErrors(4);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, (uint)src);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _gl.Fbo);
        Gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, _gl.DepMask, (uint)Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)src);
        // Checked, because the failure mode is silent and total: a depth blit between mismatched formats
        // raises INVALID_OPERATION and discards the whole call, leaving a texture that reads as "everything
        // is one unit away" -- which softens nothing and haloes everything.
        uint err = Gl.GetError == null ? 0 : Gl.GetError();
        if (err != 0)
        {
            uint chosen = _gl.DepFormat;
            _gl.DepFormat = 0;                       // re-probe: the format we chose is the prime suspect
            return Fail($"the depth blit raised 0x{err:x} (depth format 0x{chosen:x} mismatched?)");
        }
        Takes++;
        Ready = true;
        _gl.TakenGen = AlloyRenderer.PassGen; _gl.TakenSrc = src;
        return true;
    }

    /// <summary>Something wrote depth since the last <see cref="Take"/>: the next one blits afresh.</summary>
    internal static void Invalidate() => _gl.TakenGen = -1;

    /// <summary>Bind the copy on <see cref="Unit"/> and leave unit 0 current -- the diffuse rebinds there and
    /// every draw after this assumes it.</summary>
    internal static void Bind()
    {
        if (!Ready) return;
        Gl.ActiveTexture(Gl.TEXTURE0 + Unit);
        Gl.BindTexture(Gl.TEXTURE_2D, _gl.Tex);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    /// <summary>The 4.6 context went away (or the module did): the names are stale, not merely unused.
    /// </summary>
    internal static void Forget()
    {
        _gl = new DepthCopyState();
        Ready = false;
    }

    /// <summary>The format the source's depth attachment actually holds. Keyed on SIZE as well as "probed
    /// once", because the engine reallocates its attachments on a resolution change and a cached answer
    /// would size the copy from the previous mode.</summary>
    private static bool Probe(uint src, int w, int h)
    {
        if (_gl.DepFormat != 0 && _gl.W == w && _gl.H == h) return true;
        uint fmt = RenderTarget.ProbeDepthFormat(src);
        if (fmt == 0) return false;
        _gl.DepFormat = fmt;
        bool packed = RenderTarget.Packed(_gl.DepFormat);
        _gl.DepMask = packed ? Gl.STENCIL_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT : Gl.DEPTH_BUFFER_BIT;
        return true;
    }

    private static bool Ensure(int w, int h)
    {
        if (_gl.Fbo != 0 && _gl.Tex != 0 && _gl.W == w && _gl.H == h) return true;
        _gl.W = w; _gl.H = h;
        if (_gl.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _gl.Fbo = f; }
        bool packed = RenderTarget.Packed(_gl.DepFormat);
        RenderTarget.AllocDepth(ref _gl.Tex, _gl.DepFormat, w, h);
        if (_gl.Fbo == 0 || _gl.Tex == 0) return Fail("could not create the depth copy target");

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        // Colour-less: glDrawBuffer/glReadBuffer NONE is what makes a depth-only FBO complete.
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER,
            packed ? Gl.DEPTH_STENCIL_ATTACHMENT : Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, _gl.Tex, 0);
        if (Gl.DrawBuffer != null) Gl.DrawBuffer(Gl.NONE);
        if (Gl.ReadBuffer != null) Gl.ReadBuffer(Gl.NONE);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        { _gl.W = _gl.H = 0; return Fail($"the depth copy framebuffer is incomplete (0x{status:x})"); }
        return true;
    }

    /// <summary>A permanent stand-down, said once. Anything that reaches here is a property of the machine
    /// or the target, not of the frame, so retrying it every frame would only repeat the log line.</summary>
    private static bool Fail(string why)
    {
        Ready = false;
        return _down.Fail(_log, why, "soft particles are off for this session");
    }
}
