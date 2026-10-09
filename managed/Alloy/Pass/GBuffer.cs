using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>CX-188 (ADR 0029): the deferred G-buffer -- four render-size textures (three RGBA32UI, one RGBA16F)
/// the colour pass's <c>CX_DEFERRED</c> twin writes on draw buffers 4..7 and <see cref="DeferredLights"/> reads.
/// The lane format is <c>cx_gbuf.glsl</c>'s; this class owns the GL objects, the per-frame clear of G0 and
/// the attachment.
/// <para>It also owns the colour pass's whole draw-buffer list (<see cref="ApplyDrawBuffers"/>): that list is one
/// piece of framebuffer state, and <see cref="Ssr"/>'s record pair at 2..3 lives in it too -- whichever class
/// wrote it last would otherwise drop the other's attachments.</para></summary>
internal static unsafe class GBuffer
{
    /// <summary>Draw-buffer index of G0; G1..G3 follow it. 0..3 are the colour, the vis-ID and Ssr's record pair.</summary>
    internal const int FirstAttachment = 4;
    /// <summary>Targets, G0..G3.</summary>
    internal const int Targets = 4;
    /// <summary>Bytes a pixel the four targets cost together, for the allocation line.</summary>
    private const int BytesPerPixel = 3 * 16 + 8 + 16;

    private static ExtenderLog? _log;
    private static readonly StandDown _down = new("GBuffer");

    /// <summary>The four texture names, 0 until <see cref="Ensure"/> has allocated them.</summary>
    internal static uint Tex0, Tex1, Tex2, Tex3;
    /// <summary>The light record (cx_gbuf's <c>CxLightRecPack</c>): written by <see cref="DeferredLights"/> over
    /// the whole rect, debited by <see cref="PairDebt"/>, read by the blended class's lit fragments.</summary>
    internal static uint TexLight;
    /// <summary>This frame's deferred pass wrote <see cref="TexLight"/>. Reset by <see cref="BeginFrame"/>.</summary>
    internal static bool LightLive;

    private static int _w, _h;
    /// <summary>Attached to the frame FBO right now, so <see cref="ApplyDrawBuffers"/> routes 4..7 at it.</summary>
    private static bool _armed;
    /// <summary>The frame FBO is carrying a draw-buffer list of ours and has not been handed its single default
    /// back. Without it a pass that arms nothing would still issue the restore on whatever FBO is bound --
    /// the probe capture's, during a capture.</summary>
    private static bool _wrote;
    private static bool _checked;
    private static int _clears;

    /// <summary>Allocated at the current render size.</summary>
    internal static bool Ready => Tex0 != 0 && !_down.Failed;

    /// <summary>Attached to the frame FBO right now -- the opaque and cutout passes. Read by <see cref="Issue"/>,
    /// which masks the four draw buffers per program while it is true.</summary>
    internal static bool Armed => _armed;

    /// <summary>Latched: the G-buffer cannot work on this context, so there is no deferred pass this session.
    /// Read by <see cref="DeferredLights"/>, which must then leave <c>Active</c> false.</summary>
    internal static bool Failed => _down.Failed;

    /// <summary>A twin draw wrote this frame: the deferred pass has work. Reset by <see cref="BeginFrame"/>.</summary>
    internal static bool Written;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>(Re)allocate at the render size. False = allocation failed (said loudly, once).</summary>
    internal static bool Ensure(int w, int h)
    {
        if (_down.Failed) return false;
        if (Tex0 != 0 && _w == w && _h == h) return true;
        if (w <= 0 || h <= 0) return false;
        if (Gl.DrawBuffers == null || Gl.ClearTexImage == null || Gl.ColorMaski == null)
            return _down.Fail(_log, "glDrawBuffers, glClearTexImage or glColorMaski is unresolved",
                              "deferred point lights are off for the session -- the forward loop walks every light");
        // immutable storage, one level: a new size is a new name, and an integer texture under LINEAR is
        // incomplete and samples zero
        RenderTarget.Create(ref Tex0, Gl.RGBA32UI, w, h, Gl.NEAREST);
        RenderTarget.Create(ref Tex1, Gl.RGBA32UI, w, h, Gl.NEAREST);
        RenderTarget.Create(ref Tex2, Gl.RGBA32UI, w, h, Gl.NEAREST);
        RenderTarget.Create(ref Tex3, Gl.RGBA16F, w, h, Gl.NEAREST);
        RenderTarget.Create(ref TexLight, Gl.RGBA32UI, w, h, Gl.NEAREST);
        _w = w; _h = h; _checked = false;
        _log?.Info($"GBuffer: {w}x{h}, G0..G2 RGBA32UI + G3 RGBA16F + the light record RGBA32UI ({(long)w * h * BytesPerPixel >> 20} MiB)");
        return true;
    }

    /// <summary>Before the opaque pass: clear G0 to zero (flags 0 = "not mine") and reset <see cref="Written"/>.
    /// G1..G3 are never cleared -- G0's flag byte gates every read of them.</summary>
    internal static void BeginFrame()
    {
        Written = false; LightLive = false;
        if (!Ready) return;
        Gl.ClearTexImage(Tex0, 0, Gl.RGBA_INTEGER, Gl.UNSIGNED_INT, null);
        _clears++;
    }

    /// <summary>Attach G0..G3 at COLOR4..7 of the bound frame FBO for the opaque and cutout passes.</summary>
    internal static void AttachColourPass()
    {
        if (!Ready || _armed) return;
        for (uint i = 0; i < Targets; i++)
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT4 + i, Gl.TEXTURE_2D, Tex(i), 0);
        _armed = true;
        ApplyDrawBuffers();
        // once per allocation, not per frame: driver-side validation is a CPU cost inside the Opaque term
        if (_checked) return;
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            Detach();
            _down.Fail(_log, $"the frame target is incomplete with the G-buffer attached (0x{status:x})",
                       "deferred point lights are off for the session -- the forward loop walks every light");
            return;
        }
        _checked = true;
    }

    /// <summary>After the cutout pass: detach, so no later pass writes into the G-buffer. Every frame that
    /// reached <see cref="AttachColourPass"/> must come here -- the draw-buffer state is the FBO's own and
    /// outlives the frame.</summary>
    internal static void Detach()
    {
        if (!_armed) return;
        _armed = false;
        ApplyDrawBuffers();
        MaskColour(true);   // the four masks are FBO-independent GL state: hand them back write-enabled
        for (uint i = 0; i < Targets; i++)
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT4 + i, Gl.TEXTURE_2D, 0, 0);
    }

    /// <summary>Write-enable the four G-buffer draw buffers for the twin, mask them OFF for every other program
    /// drawing in the same passes (the fullbright stage, a hosted lit batch). Only the twin declares outputs
    /// there, and a draw buffer no output is written to is left UNDEFINED, not left alone -- masked, a forward
    /// draw over a twin's pixel leaves the twin's record standing, which is what the reader's own depth test is
    /// there to reject. Called on each program switch inside the bracket, so a handful of calls a frame.</summary>
    internal static void MaskColour(bool twin)
    {
        byte b = twin ? (byte)1 : (byte)0;
        for (uint i = 0; i < Targets; i++) Gl.ColorMaski((uint)FirstAttachment + i, b, b, b, b);
    }

    /// <summary>The frame FBO's draw-buffer list, composed from both armed states: the colour at 0, the OIT
    /// reveal slot at 1 (NONE in these passes), Ssr's records at 2..3 and the G-buffer at 4..7.
    /// <paramref name="suspend"/> is the sky, which writes neither -- its pixels keep G0 = 0 and no record.</summary>
    internal static void ApplyDrawBuffers(bool suspend = false)
    {
        bool ssr = Ssr.RecordsArmed && !suspend, gb = _armed && !suspend;
        if (!ssr && !gb && !_wrote) return;
        if (Gl.DrawBuffers == null) return;
        uint* bufs = stackalloc uint[FirstAttachment + Targets];
        bufs[0] = Gl.COLOR_ATTACHMENT0;
        int n = 1;
        if (ssr || gb)
        {
            bufs[1] = Gl.NONE;
            bufs[2] = ssr ? Gl.COLOR_ATTACHMENT2 : Gl.NONE;
            bufs[3] = ssr ? Gl.COLOR_ATTACHMENT3 : Gl.NONE;
            n = FirstAttachment;
            if (gb)
            {
                for (uint i = 0; i < Targets; i++) bufs[FirstAttachment + i] = Gl.COLOR_ATTACHMENT4 + i;
                n = FirstAttachment + Targets;
            }
        }
        Gl.DrawBuffers(n, bufs);
        _wrote = n > 1 || Ssr.RecordsArmed || _armed;
    }

    /// <summary>Context loss: the names are gone.</summary>
    internal static void Forget()
    {
        Tex0 = Tex1 = Tex2 = Tex3 = TexLight = 0; Written = false; LightLive = false;
        _w = _h = 0; _armed = false; _wrote = false; _checked = false;
        _down.Forget();
    }

    internal static string Stats(int frames)
    {
        int n = _clears; _clears = 0;
        return _down.Failed ? "gbuf STOOD DOWN" : !Lighting.Deferred.V ? "gbuf off" : n == 0 ? "gbuf idle"
             : $"gbuf {_w}x{_h} ({(float)n / MathF.Max(frames, 1):F2} clears/frame, {(long)_w * _h * BytesPerPixel >> 20} MiB)";
    }

    private static uint Tex(uint i) => i switch { 0 => Tex0, 1 => Tex1, 2 => Tex2, _ => Tex3 };
}
