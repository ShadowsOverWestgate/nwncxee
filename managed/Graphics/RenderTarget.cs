namespace Sowx.Graphics;

/// <summary>
/// Allocating the textures an owned pass renders to or copies into (#174). Three passes wrote this out
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
}
