namespace CX.Graphics;

/// <summary>
/// Allocating the textures an Alloy pass renders to or copies into (#174). Three passes wrote this out
/// longhand, and the depth half -- the internal-format-to-(format, type) table -- was byte-identical in two
/// of them.
/// </summary>
public static unsafe class RenderTarget
{
    /// <summary>Does this depth internal format carry stencil? Decides both the upload format and which
    /// attachment point the texture goes on; getting it wrong makes the framebuffer incomplete.</summary>
    public static bool Packed(uint depthFormat)
        => depthFormat == Gl.DEPTH24_STENCIL8 || depthFormat == Gl.DEPTH32F_STENCIL8;

    /// <summary>The pixel type <c>TexImage2D</c> demands for a given depth internal format. GL requires the
    /// two to agree: a 24-bit target uploaded as 32F is an INVALID_OPERATION and a silently empty depth
    /// buffer, which reads downstream as "every ray hits the near plane".</summary>
    public static uint DepthPixelType(uint depthFormat)
        => depthFormat == Gl.DEPTH24_STENCIL8 ? Gl.UNSIGNED_INT_24_8
         : depthFormat == Gl.DEPTH32F_STENCIL8 ? Gl.FLOAT_32_UNSIGNED_INT_24_8_REV
         : depthFormat == Gl.DEPTH_COMPONENT32F ? Gl.FLOAT
         : depthFormat == Gl.DEPTH_COMPONENT16 ? Gl.UNSIGNED_SHORT : Gl.UNSIGNED_INT;

    /// <summary>Clamp-to-edge on the currently bound TEXTURE_2D. Wrapping a screen-sized copy only invents
    /// data at the edges. The filter is the caller's: a copy read 1:1 wants NEAREST, one read at an offset
    /// (refraction, a screen-space march) wants LINEAR.</summary>
    public static void Clamp(int filter = Gl.NEAREST)
    {
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, filter);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, filter);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
    }

    /// <summary>Size <paramref name="tex"/> as a depth target and leave nothing bound. Also clears
    /// TEXTURE_COMPARE_MODE: these copies are SAMPLED, never compared, and a driver that defaults a depth
    /// format to COMPARE_REF_TO_TEXTURE hands back 0 or 1 instead of the stored depth.</summary>
    public static void AllocDepth(uint tex, uint depthFormat, int w, int h, int filter = Gl.NEAREST)
    {
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)depthFormat, w, h, 0,
                      Packed(depthFormat) ? Gl.DEPTH_STENCIL : Gl.DEPTH_COMPONENT,
                      DepthPixelType(depthFormat), null);
        Clamp(filter);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_COMPARE_MODE, (int)Gl.NONE);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
    }

    /// <summary>Size <paramref name="tex"/> as a colour target. <paramref name="hdr"/> picks RGBA16F over
    /// RGBA8, which must track what the copy SOURCE holds: an 8-bit copy of a linear HDR frame clips every
    /// highlight, and those are exactly the pixels a reflection is made of (#139).</summary>
    public static void AllocColor(uint tex, bool hdr, int w, int h, int filter = Gl.NEAREST)
    {
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)(hdr ? Gl.RGBA16F : Gl.RGBA8), w, h, 0, Gl.RGBA,
                      hdr ? Gl.HALF_FLOAT : Gl.UNSIGNED_BYTE, null);
        Clamp(filter);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
    }

    /// <summary>The depth internal format the bound FRAMEBUFFER's depth attachment holds, 0 when it has none.
    /// Asks the OBJECT for its format rather than the attachment for its bit widths (#129's finding): every
    /// owned framebuffer attaches depth at DEPTH_ATTACHMENT alone, so the STENCIL_ATTACHMENT query answers
    /// nothing and a packed D24S8 would read as a plain 24-bit one -- after which a blit mismatches and is
    /// thrown away whole. The default framebuffer (<paramref name="fbo"/> 0) has no object to ask and answers
    /// under GL_DEPTH/GL_STENCIL, so it falls back to the bit widths; a wrong guess there is the blit's to
    /// catch. Probing an absent attachment raises rather than answers, so the error queue is drained.</summary>
    public static uint ProbeDepthFormat(uint fbo)
    {
        int bits = 0, stencil = 0, type = 0, name = 0, fmt = 0;
        Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER,
            fbo == 0 ? Gl.DEPTH : Gl.DEPTH_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_DEPTH_SIZE, &bits);
        Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER,
            fbo == 0 ? Gl.STENCIL : Gl.STENCIL_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_STENCIL_SIZE, &stencil);
        if (fbo != 0)
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
        Gl.DrainErrors(4);
        if (bits <= 0 && fmt == 0) return 0;
        return (uint)fmt switch
        {
            Gl.DEPTH_COMPONENT16 or Gl.DEPTH_COMPONENT24 or Gl.DEPTH_COMPONENT32
                or Gl.DEPTH_COMPONENT32F or Gl.DEPTH24_STENCIL8 or Gl.DEPTH32F_STENCIL8 => (uint)fmt,
            _ => stencil > 0 ? Gl.DEPTH24_STENCIL8
               : bits >= 32 ? Gl.DEPTH_COMPONENT32F
               : bits >= 24 ? Gl.DEPTH_COMPONENT24 : Gl.DEPTH_COMPONENT16,
        };
    }
}
