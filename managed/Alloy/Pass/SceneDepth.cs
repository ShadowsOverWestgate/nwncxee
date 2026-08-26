using System;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #148: a depth-only snapshot of the Alloy frame, taken at the point the caller asks for it.
/// <para>It exists because the Alloy frame's ONE framebuffer copy (<see cref="FrameSnapshot"/>, ADR 0011
/// <see cref="PassId.FbCopy"/>) is taken before water and only when there is water on screen, so by the time
/// the last content classes draw there is nothing readable left. Sampling the bound depth attachment instead
/// is what most engines do here and it is undefined in GL -- depth writes being off is not the same as the
/// attachment being read-only, which is a Vulkan concept and not one this renderer has. So: a copy.</para>
/// <para>The texture allocation is <see cref="RenderTarget.AllocDepth"/>, shared with <see cref="FrameSnapshot"/>'s
/// copy since #174 -- it carries the two live-verified #129 findings that cost real debugging: the depth
/// format must come from the ATTACHED OBJECT (a packed D24S8 reads as a plain 24-bit one through the
/// attachment query, and the blit is then discarded whole and silently), and the sampled copy must have
/// <c>TEXTURE_COMPARE_MODE</c> forced to NONE or a driver hands back a comparison result instead of a depth.
/// Probing the source's format stays here: that half is genuinely per-caller.</para>
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

    private static uint _fbo, _tex, _depFormat, _depMask;
    private static int _w, _h;
    private static readonly StandDown _down = new("SceneDepth");
    private static ExtenderLog? _log;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Snapshot the bound draw target's depth. Every exit rebinds the source framebuffer: leaving
    /// the copy's FBO bound would send the rest of the frame -- the probe capture, the #139 resolve and the
    /// engine's whole residual bucket walk -- into an incomplete target, which is a lost frame rather than
    /// the stand-down this promises.</summary>
    internal static bool Take(int* viewport)
    {
        Ready = false;
        if (_down.Failed) return false;
        if (Gl.BlitFramebuffer == null || Gl.GenFramebuffers == null || Gl.FramebufferTexture2D == null
            || Gl.GetFramebufferAttachmentParameteriv == null) return Fail("a required GL entry point is missing");
        // An offset viewport breaks the 1:1 gl_FragCoord-to-texel mapping the depth fetch assumes. Refuse
        // rather than sample the wrong texels -- softness off is invisible, softness against a shifted depth
        // buffer is a halo that moves with the camera.
        if (viewport[0] != 0 || viewport[1] != 0) return false;
        int w = viewport[2], h = viewport[3];
        if (w <= 0 || h <= 0) return false;

        int src; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &src);
        if (!Probe((uint)src, w, h) || !Ensure(w, h))
        { Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)src); return false; }

        if (Gl.GetError != null) for (int i = 0; i < 4 && Gl.GetError() != 0; i++) { }
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, (uint)src);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _fbo);
        Gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, _depMask, (uint)Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)src);
        // Checked, because the failure mode is silent and total: a depth blit between mismatched formats
        // raises INVALID_OPERATION and discards the whole call, leaving a texture that reads as "everything
        // is one unit away" -- which softens nothing and haloes everything.
        uint err = Gl.GetError == null ? 0 : Gl.GetError();
        if (err != 0)
        {
            uint chosen = _depFormat;
            _depFormat = 0;                       // re-probe: the format we chose is the prime suspect
            return Fail($"the depth blit raised 0x{err:x} (depth format 0x{chosen:x} mismatched?)");
        }
        Takes++;
        Ready = true;
        return true;
    }

    /// <summary>Bind the copy on <see cref="Unit"/> and leave unit 0 current -- the diffuse rebinds there and
    /// every draw after this assumes it.</summary>
    internal static void Bind()
    {
        if (!Ready) return;
        Gl.ActiveTexture(Gl.TEXTURE0 + Unit);
        Gl.BindTexture(Gl.TEXTURE_2D, _tex);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    /// <summary>The 4.6 context went away (or the module did): the names are stale, not merely unused.
    /// </summary>
    internal static void Forget()
    {
        _fbo = 0; _tex = 0; _w = 0; _h = 0; _depFormat = 0; Ready = false;
    }

    /// <summary>The format the source's depth attachment actually holds. Keyed on SIZE as well as "probed
    /// once", because the engine reallocates its attachments on a resolution change and a cached answer
    /// would size the copy from the previous mode.</summary>
    private static bool Probe(uint src, int w, int h)
    {
        if (_depFormat != 0 && _w == w && _h == h) return true;
        int bits = 0, stencil = 0, type = 0, name = 0, fmt = 0;
        Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER,
            src == 0 ? Gl.DEPTH : Gl.DEPTH_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_DEPTH_SIZE, &bits);
        Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER,
            src == 0 ? Gl.STENCIL : Gl.STENCIL_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_STENCIL_SIZE, &stencil);
        // #129's finding, reproduced: ask the OBJECT for its format, not the attachment for its bit widths.
        // Every framebuffer bindable here attaches depth at DEPTH_ATTACHMENT alone, so the STENCIL query
        // answers nothing and a packed D24S8 reads as a plain 24-bit one -- after which the blit mismatches
        // and is thrown away whole.
        if (src != 0)
        {
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT,
                Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE, &type);
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT,
                Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME, &name);
            if (name != 0 && type == (int)Gl.RENDERBUFFER && Gl.GetRenderbufferParameteriv != null
                && Gl.BindRenderbuffer != null)
            {
                Gl.BindRenderbuffer(Gl.RENDERBUFFER, (uint)name);
                Gl.GetRenderbufferParameteriv(Gl.RENDERBUFFER, Gl.RENDERBUFFER_INTERNAL_FORMAT, &fmt);
                Gl.BindRenderbuffer(Gl.RENDERBUFFER, 0);
            }
            else if (name != 0 && Gl.GetTexLevelParameteriv != null)
            {
                Gl.ActiveTexture(Gl.TEXTURE0);
                Gl.BindTexture(Gl.TEXTURE_2D, (uint)name);
                Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
                Gl.BindTexture(Gl.TEXTURE_2D, 0);
            }
        }
        // Probing an absent attachment raises rather than answers; drain so the next real failure is not
        // read as this one.
        if (Gl.GetError != null) for (int i = 0; i < 4 && Gl.GetError() != 0; i++) { }
        if (bits <= 0 && fmt == 0) return false;
        _depFormat = (uint)fmt switch
        {
            Gl.DEPTH_COMPONENT16 or Gl.DEPTH_COMPONENT24 or Gl.DEPTH_COMPONENT32
                or Gl.DEPTH_COMPONENT32F or Gl.DEPTH24_STENCIL8 or Gl.DEPTH32F_STENCIL8 => (uint)fmt,
            _ => stencil > 0 ? Gl.DEPTH24_STENCIL8
               : bits >= 32 ? Gl.DEPTH_COMPONENT32F
               : bits >= 24 ? Gl.DEPTH_COMPONENT24 : Gl.DEPTH_COMPONENT16,
        };
        bool packed = RenderTarget.Packed(_depFormat);
        _depMask = packed ? Gl.STENCIL_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT : Gl.DEPTH_BUFFER_BIT;
        return true;
    }

    private static bool Ensure(int w, int h)
    {
        if (_fbo != 0 && _tex != 0 && _w == w && _h == h) return true;
        _w = w; _h = h;
        if (_fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo = f; }
        if (_tex == 0) { uint t; Gl.GenTextures(1, &t); _tex = t; }
        if (_fbo == 0 || _tex == 0) return Fail("could not create the depth copy target");

        bool packed = RenderTarget.Packed(_depFormat);
        RenderTarget.AllocDepth(_tex, _depFormat, w, h);

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        // Colour-less: glDrawBuffer/glReadBuffer NONE is what makes a depth-only FBO complete.
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER,
            packed ? Gl.DEPTH_STENCIL_ATTACHMENT : Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, _tex, 0);
        if (Gl.DrawBuffer != null) Gl.DrawBuffer(Gl.NONE);
        if (Gl.ReadBuffer != null) Gl.ReadBuffer(Gl.NONE);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        { _w = _h = 0; return Fail($"the depth copy framebuffer is incomplete (0x{status:x})"); }
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
