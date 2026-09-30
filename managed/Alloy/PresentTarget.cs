using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// #456: the surface Alloy's frame lands on when the engine renders to the DEFAULT framebuffer, on a
/// platform where the 4.6 context and the engine's do not agree about what the default framebuffer is.
/// </summary>
/// <remarks>
/// <para>Under GLX both contexts name one drawable and writing framebuffer 0 from either is the same write.
/// Under WGL it is not: measured under Proton, a magenta clear performed in the 4.6 context immediately
/// before the context switch is visible to the engine context on only HALF the frames -- 15 of 30, with the
/// other 15 reading the clear colour or the previous frame's content. A read-after-clear cannot be a race,
/// so the two contexts genuinely address back buffers that fall out of phase across each swap, and Alloy's
/// frame lands in whichever one is not about to be presented. That is the black-every-other-frame report.</para>
/// <para>The fix is to stop writing the presented buffer from the context that does not present it. Alloy
/// composites into a texture instead, and the engine context blits that texture into the real default
/// framebuffer after the bracket closes. Textures are shared across the share group; framebuffer OBJECTS are
/// not, so each side keeps its own container over the same images.</para>
/// <para>CX-84 dropped the depth half of that blit: the engine's two highlight overlays were the only things
/// that ever depth-tested against the default framebuffer after the bracket, and both are Alloy passes now.</para>
/// <para>Windows only, deliberately: on Linux the assumption holds, the blit would be pure cost, and the
/// map's rule is that the Linux frame stays bit-identical.</para>
/// </remarks>
internal static unsafe class PresentTarget
{
    /// <summary>Does this platform need the frame routed through a texture? Folded to a constant.</summary>
    internal static bool Needed => OperatingSystem.IsWindows();

    private static uint _tex, _fbo46, _fboEng;
    private static int _w, _h;
    private static ExtenderLog? _log;
    private static bool _announced, _blitFailed;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The framebuffer the 4.6 context should bind where it would otherwise bind 0. Zero means
    /// "bind the default framebuffer" -- Linux, or a frame whose target could not be built.</summary>
    internal static uint Fbo46 => _fbo46;

    /// <summary>Size the present colour texture to the default framebuffer, once per frame, before anything
    /// binds it.</summary>
    internal static void Ensure(int w, int h)
    {
        if (!Needed || w <= 0 || h <= 0) return;
        if (_tex != 0 && w == _w && h == _h) return;
        // RGBA8: this holds the FINISHED, display-space frame -- the composite has already encoded and
        // dithered it, so anything wider would be storing precision the default framebuffer cannot take.
        RenderTarget.AllocColor(ref _tex, false, w, h, Gl.NEAREST);
        _w = w; _h = h;
        if (_fbo46 == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo46 = f; }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo46);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _tex, 0);
        if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
        {
            _log?.Always("PresentTarget: the present texture target is incomplete -- falling back to writing "
                       + "framebuffer 0 from the 4.6 context, which flickers on Windows (#456)");
            uint t2 = _tex; Gl.DeleteTextures(1, &t2); _tex = 0; _w = _h = 0;
        }
        else if (!_announced)
        {
            _announced = true;
            _log?.Info($"PresentTarget: Alloy composites into a {w}x{h} texture and the ENGINE context blits it "
                     + "to the default framebuffer -- the two contexts do not share a back buffer here (#456)");
        }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
    }

    /// <summary>Blit the finished frame into the real default framebuffer. MUST run in the ENGINE's context,
    /// after the bracket has closed: that is the whole point, and calling it inside the bracket reintroduces
    /// the bug it exists to fix.</summary>
    internal static void HandBack()
    {
        if (!Needed || _tex == 0 || _fbo46 == 0) return;
        if (_fboEng == 0) { uint f; Gl.GenFramebuffers(1, &f); _fboEng = f; }
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _fboEng);
        // Re-attached every frame for the reason BindTarget re-attaches: GL reuses freed names, and a cached
        // attachment can outlive the image it names across a device reset.
        Gl.FramebufferTexture2D(Gl.READ_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _tex, 0);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, 0);
        Gl.BlitFramebuffer(0, 0, _w, _h, 0, 0, _w, _h, Gl.COLOR_BUFFER_BIT, (uint)Gl.NEAREST);
        uint err = Gl.GetError == null ? 0 : Gl.GetError();
        if (err != 0 && !_blitFailed)
        {
            _blitFailed = true;
            _log?.Always($"PresentTarget: the hand-back blit returned GL error 0x{err:x} -- the Alloy frame "
                       + "is finished but may not have reached the presented buffer (#456)");
        }
        Gl.DrainErrors(4);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
    }

    /// <summary>GL names die with the context. Names only -- the images are gone already.</summary>
    internal static void ContextLost() { _tex = 0; _fbo46 = 0; _fboEng = 0; _w = _h = 0; }
}
