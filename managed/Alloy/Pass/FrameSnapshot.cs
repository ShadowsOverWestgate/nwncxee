using System;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #129 (map #68, spec #114 M3): the Alloy frame's ONE framebuffer copy, and the water stage that reads it.
/// <para>What the copy is: colour and depth of everything the Alloy frame has drawn up to
/// <see cref="PassId.FbCopy"/> -- the opaque set, grass, the sky, the blended class with fade in it --
/// snapshotted into this class's own texture pair so the water stage can march them. The engine takes TWO
/// such snapshots per frame (<c>Camera::CopyRenderSubframe</c> @0x4a6b90, called before draw bucket 5 and
/// again before bucket 6 in <c>Scene::RenderSinglePass</c> @0x46e5f0) because its bucket order is fixed, not
/// because two are needed. The Alloy frame takes one; the engine's water-driven copy disappears with the
/// bucket-6 walk it belongs to, which <see cref="BucketGate"/> suppresses wholesale.</para>
/// <para>What the copy is NOT: the engine's bucket-5 copy for the framebuffer-sampling class (refraction
/// glass). That class is still the engine's -- the Alloy pass has no program for it -- so it keeps taking its
/// own snapshot. Feeding it ours would be wrong twice over: ours is taken inside the Alloy bracket, which
/// runs before the engine's residual walk, and under #139 it holds LINEAR HDR while glass draws into the
/// display-space target after the resolve. "Exactly one copy" therefore means one OWNED copy and zero engine
/// water copies; the bucket-5 one goes when the class does.</para>
/// <para><c>/water on 0</c> stands the whole thing down: no copy, no Alloy water program, and
/// <see cref="BucketGate"/> leaves bucket 6 to the engine exactly as before #129. Every look dial here is
/// a <c>/water</c> key (ADR 0013 bucket 1); the one boot decision is the tessellation segment count, which
/// is geometry rather than look and lives in the config file.</para>
/// </summary>
internal static unsafe class FrameSnapshot
{
    /// <summary>Texture units the copy binds on -- above the probe's cube, and clear of the grass push map
    /// and the #139 resolve even though those are other programs. <see cref="TextureSlots"/> is the ladder
    /// and the only place it is written down; do not paraphrase it here.</summary>
    internal const uint ColorUnit = TextureSlots.SnapshotColor, DepthUnit = TextureSlots.SnapshotDepth;

    /// <summary><c>/water on 0</c> stands the class down; <c>/water on 1</c> brings it back at the next
    /// pass. Armed and idling rather than un-installed, so the toggle is live either way.</summary>
    internal static bool On { get; private set; } = true;

    /// <summary>The class can still work in this context: switched on, and nothing permanent has gone wrong.
    /// Read BEFORE the batch build, which is why it cannot simply be <see cref="Ready"/> -- staging has to
    /// decide whether to enroll the water class one pass phase before the copy is attempted.</summary>
    internal static bool Usable => On && !_down.Failed;

    /// <summary>The copy for THIS pass is taken and sampleable. False whenever the Alloy water stage must not
    /// draw -- which is also the signal <see cref="BucketGate"/>'s bucket-6 gate reads, so the engine
    /// keeps drawing its own water rather than nobody drawing any.</summary>
    internal static bool Ready { get; private set; }

    /// <summary>Alloy copies taken this session, and the number the [alloy] line reports. The acceptance
    /// criterion is one per frame that has water on screen -- see <see cref="BucketGate"/> for the
    /// other half of it, which is that the engine took none.</summary>
    internal static int Copies { get; private set; }

    /// <summary>Frames the copy was wanted and could not be taken. Non-zero means the Alloy water stage stood
    /// down and the engine drew water instead: correct, just not ours.</summary>
    internal static int Refusals => _down.Refusals;

    private static uint _fbo, _colTex, _depTex;
    private static int _w, _h;
    private static uint _depFormat, _depMask;
    private static bool _hdr;
    private static readonly StandDown _down = new("FrameSnapshot");
    /// <summary>Diagnostic readbacks still owed, as <c>/water diag &lt;n&gt;</c> asked for. A BUDGET, not a
    /// boolean -- it counts down, and a session that never reaches the water pass keeps owing them. Ask for a
    /// few rather than one: the first copy after the request can land before the area has anything in it, and
    /// one all-black reading proves nothing.</summary>
    private static int _diagLeft;
    private static ExtenderLog? _log;

    /// <summary><c>/water tint</c>: metres of water the refracted colour survives (Beer-Lambert e-fold). Small
    /// = murky harbour, large = clear tropical shallows.</summary>
    internal static float Tint { get; private set; } = 3f;
    /// <summary><c>/water wave</c>: ripple amplitude multiplier. 0 = a mirror-flat plane, which is also the
    /// cleanest way to tell a reflection bug from a normal-perturbation one.</summary>
    internal static float Wave { get; private set; } = 1f;
    /// <summary><c>/water ssr</c>: how much of the reflection the screen-space march is allowed to supply where
    /// it hits. 0 leaves the probe serving the whole reflection, which is the "is SSR the problem" switch.</summary>
    internal static float Ssr { get; private set; } = 1f;
    /// <summary>#147 <c>/water foam</c>: foam coverage multiplier. 0 removes both the whitecaps and the shore
    /// wash, which is how you tell a foam bug from a wave bug.</summary>
    internal static float Foam { get; private set; } = 1f;
    /// <summary>#147 <c>/water wind</c>: engine wind units per metre/second. <c>areaGlobalWind</c> is a world
    /// vector with no documented scale -- the stock wind shaders only ever feed its magnitude into an
    /// arbitrary sway, so nothing in the engine pins it to a physical speed. The sea-state growth law DOES
    /// need m/s, so the conversion is a named dial rather than a constant buried in the shader, and the
    /// <c>[water]</c> census prints the raw magnitude so it can be calibrated against a known area.
    /// <para>CALIBRATED 2026-08-23 from the census, which is what the line exists for: a becalmed interior
    /// reads raw 0.118 and an open harbour reads raw 2.000 -- and 2.000 exactly, on the nose, in an area with
    /// weather, which says it is the engine's ceiling rather than a measurement. 4 therefore puts a full-wind
    /// coast at 8 m/s (a fresh breeze, Beaufort 5, whitecaps just starting) and a sheltered interior at half
    /// a metre per second. The previous 12 put that harbour in a 24 m/s storm.</para></summary>
    internal static float WindScale { get; private set; } = 4f;
    /// <summary>#147 config <c>water_tess</c>: triangle edges per wavelength the tessellator aims for. A
    /// boot setting rather than a <c>/water</c> key because it sizes the GEOMETRY the patch generates, not
    /// the look of it. Six carries a
    /// sine visibly; below four a swell reads as a faceted ridge. 0 stands displacement down altogether and
    /// leaves the fragment-side normal doing all the work, which is the pre-tessellation look and the way to
    /// tell a displacement bug from a shading one.</summary>
    internal static float TessSegments { get; private set; } = 6f;
    /// <summary>Ceiling on any one patch's tessellation level, clamped to the driver's own
    /// <c>GL_MAX_TESS_GEN_LEVEL</c> at first use. A ten-unit tile at level 32 is ~2k triangles, and the
    /// wavelength term already asks for far less than that on anything but a close-up swell.</summary>
    internal static float TessMax { get; private set; } = 24f;

    /// <summary>#147: seconds of REAL time since the pass first ran. The game clock will not do -- a module
    /// sets its own minutes-per-hour (2 by default, so time-of-day advances 30x real time), and the sea would
    /// run at whatever that multiple happens to be. Monotone and never multiplied by anything live, which is
    /// the other half of the phase-jump rule.</summary>
    internal static float WaveTime => (float)_clock.Elapsed.TotalSeconds * Speed;
    /// <summary>#147 <c>/water speed</c>: how fast the field runs against real time. Deep-water dispersion says
    /// a 10 m wave travels at 4 m/s and that IS the physics -- but NWN's world units are not metres (a tile
    /// is ten units and a person is two), so the honest statement is that the scene's scale and the wave
    /// model's disagree, and this is where the disagreement is absorbed. Halved by observation: at 1.0 the
    /// harbour ran visibly fast for its apparent size.</summary>
    internal static float Speed { get; private set; } = 0.5f;
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private static float _bootTint, _bootWave, _bootSsr, _bootFoam, _bootWind, _bootSpeed;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        TessSegments = Settings.Float("water_tess", TessSegments, 0f, 64f);
        _bootTint = Tint; _bootWave = Wave; _bootSsr = Ssr;
        _bootFoam = Foam; _bootWind = WindScale; _bootSpeed = Speed;
    }

    internal static string Describe()
        => $"water {(On ? "on" : "off")}  tint {Tint:F2}  wave {Wave:F2}  ssr {Ssr:F2}  foam {Foam:F2}" +
           $"  wind x{WindScale:F1}  speed {Speed:F2}  tess {TessSegments:F0}/wave max {TessMax:F0}" +
           (_diagLeft > 0 ? $"  DIAG {_diagLeft} left" : "");

    /// <summary>Live tuning behind <c>/water</c>; returns the old value, NaN for an unknown key. Same
    /// no-lock reasoning as <see cref="PostChain.Tune"/>: independent floats, written from the input
    /// thread, re-read per pass.</summary>
    internal static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            case "on":    old = On ? 1f : 0f; On = v > 0f; break;
            case "tint":  old = Tint; Tint = v; break;
            case "wave":  old = Wave; Wave = v; break;
            case "ssr":   old = Ssr; Ssr = v; break;
            case "foam":  old = Foam; Foam = v; break;
            case "wind":  old = WindScale; WindScale = v; break;
            case "speed": old = Speed; Speed = v; break;
            // A budget of readbacks, not a switch: v is how many copies to diagnose from here.
            case "diag":  old = _diagLeft; _diagLeft = v > 0f ? (int)v : 0; break;
            default: return float.NaN;
        }
        _log?.Info($"FrameSnapshot: /water {key} {v:F2} (was {old:F2}) -> {Describe()}");
        return old;
    }

    /// <summary>Put every <c>/water</c> dial back to its boot value. The diagnostic budget has no boot value
    /// and never should -- a readback is a live request, so reset spends none.</summary>
    internal static void ResetTuning()
    {
        On = true;
        Tint = _bootTint; Wave = _bootWave; Ssr = _bootSsr;
        Foam = _bootFoam; WindScale = _bootWind; Speed = _bootSpeed;
        _diagLeft = 0;
        _log?.Info($"FrameSnapshot: /water reset -> {Describe()}");
    }

    /// <summary>Clamp the tessellation ceiling to what the driver will actually generate. GL guarantees at
    /// least 64, so this is a formality -- but a level above the limit is an error, not a clamp.</summary>
    internal static void ClampTess()
    {
        if (Graphics.Gl.GetIntegerv == null) return;
        int max = 0;
        Graphics.Gl.GetIntegerv(Graphics.Gl.MAX_TESS_GEN_LEVEL, &max);
        if (max > 0 && TessMax > max) TessMax = max;
    }

    /// <summary>Drop every GL object (context loss / disarm). Names mean nothing once the context is gone, so
    /// this forgets rather than deletes -- same rule as <see cref="PostChain.Forget"/>.</summary>
    internal static void Forget()
    {
        _fbo = 0; _colTex = 0; _depTex = 0; _w = 0; _h = 0;
        _depFormat = 0; _depMask = 0; _hdr = false; Ready = false; _down.Forget();
    }

    /// <summary>Nothing is drawn from the copy any more -- called at the top of every Alloy colour pass so a
    /// pass that never reaches <see cref="Copy"/> cannot leave the previous pass's snapshot armed. A GUI 3D
    /// panel drawing from the world's copy would sample the world into a portrait.</summary>
    internal static void Revoke() => Ready = false;

    /// <summary>ADR 0011 <see cref="PassId.FbCopy"/>: snapshot the currently bound draw target into the copy
    /// pair. Blits colour and depth in one call, which is the engine's own mechanism for it
    /// (<c>GLRender::CopyFramebuffer</c> @0x4c9930 is <c>glBlitFramebuffer</c> with a translated bit mask and
    /// GL_NEAREST) -- so the depth half is not a workaround, it is what the engine already relies on working.
    /// <para>Returns false, having logged once, whenever the copy cannot be taken. That is a full stand-down
    /// and not a degraded draw: <see cref="Ready"/> stays false, the Alloy water stage does not run, and the
    /// engine's bucket-6 walk (with its own copy) is left to draw water exactly as before #129.</para></summary>
    internal static bool Copy(int* viewport)
    {
        Ready = false;
        if (!On || _down.Failed) return false;
        if (Gl.BlitFramebuffer == null || Gl.GenFramebuffers == null || Gl.FramebufferTexture2D == null
            || Gl.GetFramebufferAttachmentParameteriv == null)
            return Fail("a required GL entry point is missing");
        // An offset viewport would break the 1:1 mapping between gl_FragCoord and the copy's texels, which
        // every screen-space read in the stage assumes. A refusal, not a workaround.
        if (viewport[0] != 0 || viewport[1] != 0) return Refuse("the pass viewport is offset");
        int w = viewport[2], h = viewport[3];
        if (w <= 0 || h <= 0) return Refuse("the pass viewport is empty");

        int src; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &src);
        // Every exit from here down rebinds src. EnsureTargets leaves the COPY framebuffer bound (it has to,
        // to check completeness), so returning straight out of a failure would leave the rest of the frame --
        // additive, the #139 resolve, and the engine's entire residual bucket walk -- drawing into an
        // incomplete FBO. That is a lost frame, not the stand-down this method promises.
        if (!ProbeDepth((uint)src, w, h) || !EnsureTargets(w, h))
        { Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)src); return false; }

        if (Gl.GetError != null) for (int i = 0; i < 4 && Gl.GetError() != 0; i++) { }
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, (uint)src);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _fbo);
        Gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, Gl.COLOR_BUFFER_BIT | _depMask, (uint)Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)src);
        // ...and the blit is CHECKED, because its whole-or-nothing failure mode is silent and total. A depth
        // blit between mismatched formats raises INVALID_OPERATION and discards the entire call -- colour
        // included -- leaving two never-written textures that read as "the scene is empty and one unit away".
        // Ready would otherwise stay true and the acceptance counter would report success the whole time.
        uint err = Gl.GetError == null ? 0 : Gl.GetError();
        if (err != 0)
        {
            uint chosen = _depFormat;
            _depFormat = 0;                       // re-probe: the format we chose is the prime suspect
            return Fail($"the framebuffer blit raised 0x{err:x} (depth format 0x{chosen:x} mismatched?)");
        }
        Copies++;
        Ready = true;
        if (_diagLeft > 0) { _diagLeft--; Diagnose((uint)src, w, h); }
        return true;
    }

    /// <summary><c>/water diag &lt;n&gt;</c>: read one pixel back out of BOTH framebuffers and say what is actually in
    /// them. Costs a full pipeline stall, so it runs a handful of times per session and never otherwise.
    /// <para>This exists because every cheaper signal about the copy is ambiguous. A raw depth texel displayed
    /// as grey saturates to white for any real geometry (depth is nonlinear -- almost everything sits above
    /// 0.95), and a colour view is filtered through the tonemap before a human sees it. The only unambiguous
    /// answer to "did the blit land" is the same pixel read from the source and from the copy, as numbers.
    /// Equal means the copy is exact and the fault is downstream in the stage; different means it is not.</para></summary>
    private static void Diagnose(uint src, int w, int h)
    {
        if (Gl.ReadPixels == null || Gl.ReadBuffer == null) return;
        int x = w / 2, y = h / 2;
        float* sc = stackalloc float[4];
        float* dc = stackalloc float[4];
        float sd = 0f, dd = 0f;
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, src);
        if (src != 0) Gl.ReadBuffer(Gl.COLOR_ATTACHMENT0);
        Gl.ReadPixels(x, y, 1, 1, Gl.RGBA, Gl.FLOAT, sc);
        Gl.ReadPixels(x, y, 1, 1, Gl.DEPTH_COMPONENT, Gl.FLOAT, &sd);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _fbo);
        Gl.ReadBuffer(Gl.COLOR_ATTACHMENT0);
        Gl.ReadPixels(x, y, 1, 1, Gl.RGBA, Gl.FLOAT, dc);
        Gl.ReadPixels(x, y, 1, 1, Gl.DEPTH_COMPONENT, Gl.FLOAT, &dd);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, src);
        uint err = Gl.GetError == null ? 0 : Gl.GetError();
        _log?.Info($"[waterdiag] centre pixel ({x},{y}) -- source rgb({sc[0]:F4},{sc[1]:F4},{sc[2]:F4}) " +
                   $"depth {sd:F6} | copy rgb({dc[0]:F4},{dc[1]:F4},{dc[2]:F4}) depth {dd:F6} | " +
                   // The COLOUR pair is the verdict. A blit is all-or-nothing, so colour landing means the
                   // whole call landed -- and reading depth back out of the source FBO proved unreliable
                   // (it answers a flat 1.0 while the copy taken FROM it holds a real value, which no blit
                   // can produce). The depth pair is printed for the record, never judged on.
                   $"{(sc[0] == dc[0] && sc[1] == dc[1] && sc[2] == dc[2] ? "COLOUR IDENTICAL -- the copy lands; look downstream in the stage" : "COLOUR DIFFERS -- the copy is not landing")}" +
                   (err == 0 ? "" : $" (readback err 0x{err:x})"));
    }

    /// <summary>Bind the copy on <see cref="ColorUnit"/>/<see cref="DepthUnit"/> and leave unit 0 current --
    /// the diffuse rebinds there, and every other pass in the frame assumes it.</summary>
    internal static void Bind()
    {
        Gl.ActiveTexture(Gl.TEXTURE0 + ColorUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _colTex);
        Gl.ActiveTexture(Gl.TEXTURE0 + DepthUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _depTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    /// <summary>Size the copy pair to the pass. The DEPTH half is allocated from what the engine's own
    /// attachment reports rather than from a guess: <c>glBlitFramebuffer</c> requires the two depth formats
    /// to match, so a 24-bit target copied into a 32F texture is an INVALID_OPERATION and a silently empty
    /// depth buffer -- which reads as "every screen-space ray hits the near plane".</summary>
    private static bool EnsureTargets(int w, int h)
    {
        bool hdr = PostChain.Armed;
        // ...and on the COLOUR FORMAT, not only the size. #139 can arm or disarm mid-session at an unchanged
        // resolution (a /post hdr toggle, a target the pass refuses on one frame and takes on the next), and
        // a cache keyed on size alone would then blit a linear HDR frame into an 8-bit copy and clip every
        // highlight the water was about to reflect.
        if (_fbo != 0 && _w == w && _h == h && _hdr == hdr && _colTex != 0 && _depTex != 0) return true;
        _w = w; _h = h; _hdr = hdr;
        if (_fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo = f; }
        if (_colTex == 0) { uint t; Gl.GenTextures(1, &t); _colTex = t; }
        if (_depTex == 0) { uint t; Gl.GenTextures(1, &t); _depTex = t; }
        if (_fbo == 0 || _colTex == 0 || _depTex == 0) return Fail("could not create the copy targets");

        // #139: RGBA16F while the HDR target is armed, because that is what the copy SOURCE holds -- an 8-bit
        // copy of a linear HDR frame clips every highlight the water would reflect, and those are exactly the
        // pixels a reflection is made of.
        // LINEAR on both: the refraction and the SSR march sample at an offset, not at texel centres.
        RenderTarget.AllocColor(_colTex, hdr, w, h, Gl.LINEAR);
        bool packed = RenderTarget.Packed(_depFormat);
        RenderTarget.AllocDepth(_depTex, _depFormat, w, h, Gl.LINEAR);

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _colTex, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER,
            packed ? Gl.DEPTH_STENCIL_ATTACHMENT : Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, _depTex, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _w = _h = 0;
            return Fail($"the copy framebuffer is incomplete (0x{status:x})");
        }
        _log?.Info($"FrameSnapshot: framebuffer copy {w}x{h}, colour {(hdr ? "RGBA16F" : "RGBA8")}, " +
                   $"depth 0x{_depFormat:x} -- one copy per frame, /water on 0 disables");
        return true;
    }

    /// <summary>What depth format the blit source carries, re-probed whenever the target changes size (the
    /// engine reallocates its attachments on a resolution change, and a cached answer would then size the
    /// copy from the previous mode). The default framebuffer answers under attachment point GL_DEPTH, where
    /// GL_DEPTH_ATTACHMENT is not a legal name.</summary>
    private static bool ProbeDepth(uint src, int w, int h)
    {
        // Keyed on the SIZE, not merely on "have we probed once": the engine reallocates its attachments on
        // a resolution change, and a cached answer would then size the copy from the previous mode.
        if (_depFormat != 0 && _w == w && _h == h) return true;
        int bits = 0, stencil = 0, type = 0, name = 0, fmt = 0;
        Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER,
            src == 0 ? Gl.DEPTH : Gl.DEPTH_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_DEPTH_SIZE, &bits);
        Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER,
            src == 0 ? Gl.STENCIL : Gl.STENCIL_ATTACHMENT, Gl.FRAMEBUFFER_ATTACHMENT_STENCIL_SIZE, &stencil);
        // THE OBJECT'S OWN FORMAT, not the attachment's bit widths -- and this is the whole finding. Every
        // framebuffer that can be bound here when the engine renders to an FBO attaches its depth at
        // DEPTH_ATTACHMENT alone (AlloyRenderer.BindTarget and PostChain.Begin both do; neither ever uses
        // DEPTH_STENCIL_ATTACHMENT). So the STENCIL_ATTACHMENT query answers nothing, and a packed D24S8
        // engine depth reads as a plain 24-bit one. The blit then mismatches and is discarded whole.
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
        // A probe of an absent attachment raises an error rather than answering; drain the queue so the next
        // real GL failure is not read as this one. glGetError reports one error per call.
        if (Gl.GetError != null) for (int i = 0; i < 4 && Gl.GetError() != 0; i++) { }
        if (bits <= 0 && fmt == 0) return Refuse("the pass target has no depth attachment to copy");
        _depFormat = (uint)fmt switch
        {
            Gl.DEPTH_COMPONENT16 or Gl.DEPTH_COMPONENT24 or Gl.DEPTH_COMPONENT32
                or Gl.DEPTH_COMPONENT32F or Gl.DEPTH24_STENCIL8 or Gl.DEPTH32F_STENCIL8 => (uint)fmt,
            // Unrecognised (or the default framebuffer, which has no object to ask): fall back to the bit
            // widths. The blit check in Copy is what catches a wrong guess here.
            _ => stencil > 0 ? Gl.DEPTH24_STENCIL8
               : bits >= 32 ? Gl.DEPTH_COMPONENT32F
               : bits >= 24 ? Gl.DEPTH_COMPONENT24 : Gl.DEPTH_COMPONENT16,
        };
        bool packed = _depFormat == Gl.DEPTH24_STENCIL8 || _depFormat == Gl.DEPTH32F_STENCIL8;
        _depMask = packed ? Gl.STENCIL_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT : Gl.DEPTH_BUFFER_BIT;
        return true;
    }

    /// <summary>Permanent: the class cannot work in this context at all. Water reverts to the engine's own
    /// draw for the session.</summary>
    private static bool Fail(string why)
        => _down.Fail(_log, why, "the engine draws water (and takes its own framebuffer copy) exactly as it "
                               + "did before #129");

    /// <summary>Per-pass: this particular pass cannot take the copy. NOT latched -- a GUI 3D panel or a
    /// reflection subframe legitimately refuses while the world pass keeps arming -- but the reason is logged
    /// once per distinct reason, because the fallback is otherwise silent.</summary>
    private static bool Refuse(string why)
        => _down.Refuse(_log, why, "no framebuffer copy this pass; the engine draws water instead");

    internal static string Stats(int frames)
        => $"water: {Copies} copies, {Refusals} refused" + (frames > 0 ? $" over {frames} frames" : "");

    /// <summary>The <c>/water</c> dial table, off-device: every key reachable, an unknown key reported, and
    /// -- the one that bites -- every knob the check drove restored. A knob left at its test value ships as
    /// the boot value, which is exactly how <c>/post viz</c> once booted every session into a debug view.</summary>
    private static void CheckTuning(Action<bool, string> e)
    {
        ExtenderLog? quiet = _log; _log = null;
        float tint = Tint, wave = Wave, ssr = Ssr, foam = Foam, wind = WindScale, speed = Speed;
        bool on = On; int diag = _diagLeft;
        try
        {
            e(float.IsNaN(Tune("nope", 1f)), "an unknown knob is reported");
            foreach (string key in new[] { "on", "tint", "wave", "ssr", "foam", "wind", "speed", "diag" })
                e(!float.IsNaN(Tune(key, 1f)), $"knob \"{key}\" is reachable");
            e(_diagLeft == 1, "diag is a BUDGET: the value set is the number of readbacks owed");
            e(Describe().Contains("DIAG"), "...and Describe says how many are left while any are");
            e(Tune("on", 0f) == 1f && !On && !Usable,
              "/water on 0 stands the class down without touching the stand-down latch");
            float boot = _bootTint; _bootTint = 0.25f;
            ResetTuning();
            e(Tint == 0.25f, "reset honours the BOOT value, not the hardcoded default");
            e(On && _diagLeft == 0, "reset re-arms the class and spends no diagnostic readbacks");
            _bootTint = boot;
        }
        finally
        {
            // Driven through Tune, so the knobs stamped come from the KEY TABLE rather than from a list
            // written beside the restore -- an assertion built from the restore's own list passes whatever
            // that list forgets, which is how /post viz shipped its test value as a boot value. Describe
            // prints every knob, so a missed one surfaces as its own sentinel.
            const float Sentinel = -12345f;
            foreach (string key in new[] { "on", "tint", "wave", "ssr", "foam", "wind", "speed" })
                Tune(key, Sentinel);
            Tint = tint; Wave = wave; Ssr = ssr; Foam = foam; WindScale = wind; Speed = speed;
            On = on; _diagLeft = diag;
            e(!Describe().Contains("-12345"), "the check restores EVERY knob it drove");
            _log = quiet;
        }
    }

    /// <summary>Off-device (run-demo.sh): the stage's own contracts, the ones a GPU would only report as a
    /// wrong-looking sea. Two are structural -- water reads the copy the pass list takes immediately before
    /// it, and the probe is what the reflection falls back to -- and both are text checks on the source that
    /// is actually compiled, which is the only thing an override cannot silently change out from under.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"FrameSnapshot.SelfCheck FAIL: {w}"); } }

        CheckTuning(E);

        string fs = Fs;
        E(fs.Contains("uniform sampler2D uFbColor") && fs.Contains("uniform sampler2D uFbDepth"),
          "the stage declares the copy pair it marches");
        // The ticket's own criterion, as a text check: SSR survives for near-field contact detail and the
        // probe stands in wherever it misses. A stage that dropped either half would still compile.
        E(fs.Contains("SowxWaterMarch("), "the screen-space march is still in the stage");
        E(fs.Contains("textureLod(uProbe,"), "the probe is still the reflection's fallback");
        E(fs.Contains("uProbeMips > 0.0"), "...and it is not sampled before a capture has completed");
        // Colour space: since #132 the whole Alloy frame is linear, so the copy is linear and a decode on
        // read would be a second linearisation. Sampling it in the wrong space is a whole-image error that
        // reads as "the reflection is washed out" (or "far too dark") rather than as a bug, so it is
        // asserted rather than left to the next reader of the shader text.
        E(fs.Contains("return texture(uFbColor, uv).rgb;") && !fs.Contains("SowxLinear(texture(uFbColor"),
          "the copy is sampled as LINEAR, with no decode");
        // Ordering: FbCopy immediately precedes WaterOwned, and both sit between the blended class and
        // additive. That IS the ticket's "one copy, vanilla's placement" -- it is not an implementation
        // detail of the pass list, it is the design.
        int copy = -1, water = -1, blend = -1, add = -1;
        for (int i = 0; i < FrameGraph.Passes.Length; i++)
        {
            PassId id = FrameGraph.Passes[i].Id;
            if (id == PassId.FbCopy) copy = i;
            if (id == PassId.WaterOwned) water = i;
            if (id == PassId.BlendedOwned) blend = i;
            if (id == PassId.AdditiveOwned) add = i;
        }
        E(copy >= 0 && water == copy + 1, "the water pass reads a copy taken immediately before it");
        E(blend >= 0 && blend < copy, "the blended class (fade included) is IN the copy");
        E(add >= 0 && add > water, "additive is NOT in the copy, and composites over the water");
        E(ColorUnit > EnvProbe.ProbeUnit && DepthUnit > ColorUnit,
          "the copy's units sit above the probe cube's -- two sampler types on one unit drops the draw");

        log.Info(ok ? "FrameSnapshot.SelfCheck: passed" : "FrameSnapshot.SelfCheck: FAILED");
        return ok;
    }

    /// <summary>The whole fragment stage, composed by <see cref="ShaderStages"/> -- the one place any owned
    /// stage's composition is written down (ADR 0012).</summary>
    internal static string Fs => ShaderStages.Source(AlloyRenderer.ShdWaterFs);

    /// <summary>#147's vertex stage. Deliberately NOT the lit pass's: water is never skinned and never a
    /// wind-feature part, and -- the reason it needs its own -- it must NOT project. The clip position is the
    /// tessellation-evaluation stage's job, because the vertices this stage emits are patch corners that no
    /// longer exist by the time anything is rasterised.
    /// <para>No <c>invariant gl_Position</c> and no #119 clip expression, and that is correct rather than an
    /// omission: water is excluded from the depth prepass, so nothing re-tests these depths with LEQUAL. It
    /// is the one Alloy vertex stage the standing invariance constraint does not bind, which is exactly what
    /// makes displacement legal here and nowhere else.</para></summary>
    internal static string Vs => ShaderStages.Source(AlloyRenderer.ShdWaterVs);

    internal const string VsHead = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUV;
        layout(location = 2) in vec3 aNrm;

        """;

    internal const string VsMain = """
        uniform int uDrawBase;
        out vec2 tcUV;
        out vec3 tcWorld;
        out vec3 tcNrm;
        flat out int tcDraw;

        void main()
        {
            int id = SowxSlot(uDrawBase + gl_DrawID);
            SowxDraw d = uDraw[id];
            tcWorld = (d.model * vec4(aPos, 1.0)).xyz;
            tcNrm = mat3(d.model) * aNrm;
            tcUV = vec2(d.rot.x * aUV.x - d.rot.y * aUV.y, d.rot.y * aUV.x + d.rot.x * aUV.y);
            tcDraw = id;
        }
        """;

    /// <summary>#147's tessellation-control stage: how finely to cut this patch. A tile water plane is two
    /// triangles across ten world units, so a displacement applied at its corners moves three points and
    /// interpolates a flat sheet between them -- which is precisely the "the waves are only ripples on a flat
    /// surface" the fragment-only version produced.
    /// <para>The level comes from the WAVELENGTH rather than a constant: an edge is cut into enough segments
    /// to carry <c>uTessDials.x</c> of them per wave, then capped by distance so the horizon is not paying
    /// for geometry smaller than a pixel. A pond whose peak wavelength is 15cm does NOT get a million
    /// triangles -- its amplitude is millimetres, and water that flat is explicitly retired to level 1.</para>
    /// </summary>
    internal static string Tcs => ShaderStages.Source(AlloyRenderer.ShdWaterTcs);

    internal const string TcsHead = """
        #version 460 core
        layout(vertices = 3) out;

        """;

    internal const string TcsMain = """
        uniform vec3 uEyePos;
        uniform vec2 uTessDials;       // (segments per wavelength, max level)
        in vec2 tcUV[];
        in vec3 tcWorld[];
        in vec3 tcNrm[];
        flat in int tcDraw[];
        out vec2 teUV[];
        out vec3 teWorld[];
        out vec3 teNrm[];
        flat out int teDraw[];

        /// One edge's level: its world length in wave-segments, faded out with distance from the eye. Edge i
        /// of a triangle patch is the one OPPOSITE vertex i, which is the pairing gl_TessLevelOuter expects.
        float SowxEdgeLevel(vec3 a, vec3 b, float seg, float maxLevel)
        {
            float len = distance(a, b);
            float dist = distance(uEyePos, (a + b) * 0.5);
            // Past 60 units a wave is a few pixels across and the fragment-side normal carries it perfectly
            // well; there is nothing the silhouette can show that is worth the geometry.
            float near = clamp(1.0 - dist / 60.0, 0.0, 1.0);
            return clamp(len / max(seg, 0.05) * near, 1.0, maxLevel);
        }

        void main()
        {
            teUV[gl_InvocationID] = tcUV[gl_InvocationID];
            teWorld[gl_InvocationID] = tcWorld[gl_InvocationID];
            teNrm[gl_InvocationID] = tcNrm[gl_InvocationID];
            teDraw[gl_InvocationID] = tcDraw[gl_InvocationID];
            if (gl_InvocationID != 0) return;

            SowxDraw d = uDraw[tcDraw[0]];
            bool tracked = d.dif.x > 0.0;
            float fetch = tracked ? d.dif.x : 30.0;
            float bodySize = tracked ? d.dif.y : fetch;
            float bodyMin = tracked ? d.dif.z : fetch;
            float hs, lp;
            SowxSeaState(fetch, bodySize, d.dif.w, hs, lp);
            lp = min(lp, bodyMin * 0.6);
            // Water this flat has no shape to resolve, whatever its wavelength -- a millpond is one triangle.
            // A water_tess of 0 lands here too, which is how displacement is stood down whole.
            float seg = (hs < 0.02 || uTessDials.x < 0.5) ? 1e6 : lp / uTessDials.x;

            gl_TessLevelOuter[0] = SowxEdgeLevel(tcWorld[1], tcWorld[2], seg, uTessDials.y);
            gl_TessLevelOuter[1] = SowxEdgeLevel(tcWorld[2], tcWorld[0], seg, uTessDials.y);
            gl_TessLevelOuter[2] = SowxEdgeLevel(tcWorld[0], tcWorld[1], seg, uTessDials.y);
            // The inner level must not fall below the outer ones or the interior detaches from the shared
            // edges and cracks open between neighbouring patches -- the classic tessellation seam.
            gl_TessLevelInner[0] = max(gl_TessLevelOuter[0],
                                   max(gl_TessLevelOuter[1], gl_TessLevelOuter[2]));
        }
        """;

    /// <summary>#147's tessellation-evaluation stage: where the water actually rises. Displaces along the
    /// patch's own normal by the same field the fragment stage reads for its normals, and only then projects.
    /// <para>The undisplaced horizontal position travels on as <c>vWavePos</c>. That is the parameter the
    /// field is defined over, so the fragment stage evaluating it there gets the gradient at the point this
    /// stage actually moved -- rather than at where it ended up, which is a different point on a different
    /// wave.</para></summary>
    internal static string Tes => ShaderStages.Source(AlloyRenderer.ShdWaterTes);

    internal const string TesHead = """
        #version 460 core
        layout(triangles, fractional_odd_spacing, ccw) in;

        """;

    internal const string TesMain = """
        uniform mat4 uVP;
        uniform vec3 uAreaWind;
        uniform float uWaveTime;
        uniform vec4 uWaterDials;
        in vec2 teUV[];
        in vec3 teWorld[];
        in vec3 teNrm[];
        flat in int teDraw[];
        out vec2 vUV;
        out vec3 vWorld;
        out vec3 vNrm;
        out float vDepth;
        out float vAnchorZ;
        out vec2 vWavePos;
        flat out int vDraw;

        vec3 SowxBary3(vec3 a, vec3 b, vec3 c)
        { return gl_TessCoord.x * a + gl_TessCoord.y * b + gl_TessCoord.z * c; }

        void main()
        {
            vDraw = teDraw[0];
            SowxDraw d = uDraw[vDraw];
            vUV = gl_TessCoord.x * teUV[0] + gl_TessCoord.y * teUV[1] + gl_TessCoord.z * teUV[2];
            vNrm = normalize(SowxBary3(teNrm[0], teNrm[1], teNrm[2]));
            vec3 world = SowxBary3(teWorld[0], teWorld[1], teWorld[2]);
            vAnchorZ = world.z;
            vWavePos = world.xy;

            bool tracked = d.dif.x > 0.0;
            float fetch = tracked ? d.dif.x : 30.0;
            float bodySize = tracked ? d.dif.y : fetch;
            float bodyMin = tracked ? d.dif.z : fetch;
            float hs, lp;
            SowxSeaState(fetch, bodySize, d.dif.w, hs, lp);
            hs *= uWaterDials.y;
            lp = min(lp, bodyMin * 0.6);
            float sharp = mix(1.0, 3.0, smoothstep(SOWX_WHITECAP_U0 * 0.5, SOWX_WHITECAP_U1, d.dif.w));
            vec2 wdir = uAreaWind.xy;
            float wlen = length(wdir);
            wdir = wlen > 1e-4 ? wdir / wlen : vec2(0.7071, 0.7071);

            vec2 grad;
            float msSlope;
            // Zero pixel size = keep every component. The control stage's own distance term already retires
            // the geometry; a second LOD here would fight it.
            float h = SowxSeaField(world.xy, uWaveTime, hs, lp, wdir, sharp, 0.0, grad, msSlope);
            // Along the patch normal rather than straight up, so a tilted water plane raises waves out of
            // its own surface instead of shearing them through it.
            world += vNrm * h;
            // ponytail: vertical only. Gerstner's horizontal term (crests drawn together, troughs spread) is
            // the next increment and needs the same displacement applied in the fragment stage's field
            // lookup, which is what vWavePos is already carrying.
            vWorld = world;
            gl_Position = uVP * vec4(world, 1.0);
            vDepth = gl_Position.w;
        }
        """;

    /// <summary>#147: the sea state, shared verbatim by the tessellation-evaluation stage that DISPLACES the
    /// surface and the fragment stage that shades it. One string, because two copies of a wave field are two
    /// wave fields: the normals would describe a surface the geometry is not on, which reads as lighting that
    /// slides over the waves instead of sticking to them.</summary>
    internal const string WaveLib = """
        // #147's sea state. These are the real fetch-limited-growth constants (SMB/JONSWAP shape), not taste
        // dials, and they are what makes a well and a bay different without a single content-authored switch:
        // at 5 m/s over 4 metres of fetch they give a 1.6 cm ripple, and over a kilometre a 25 cm swell.
        const float SOWX_G = 9.81;
        const float SOWX_TAU = 6.28318531;
        // The wave SPECTRUM is pinned to a reference wind while the AMPLITUDE tracks the live one. Two
        // reasons, one physical and one hard-won: a real sea's wavelength takes hours to answer a change in
        // wind, and inc_clouds.shd documents what happens when a live wind term multiplies accumulated time
        // -- the whole pattern teleports the moment the server nudges it. Amplitude can move instantly and
        // nothing jumps; wavelength cannot.
        const float SOWX_WIND_REF = 6.0;
        // Whitecapping begins around Beaufort 3-4 and needs room to build -- a well never breaks, however
        // hard it blows.
        const float SOWX_WHITECAP_U0 = 4.0, SOWX_WHITECAP_U1 = 11.0;
        const float SOWX_WHITECAP_FETCH = 25.0;
        // SIX fixed WORLD directions, spread wide and deliberately not evenly. Fixed rather than
        // wind-relative because a direction inside a phase term cannot rotate without dragging the whole
        // field with it; the wind weights them instead.
        const int SOWX_SEA_N = 6;
        const vec2 SOWX_SEA_DIR[6] = vec2[6](vec2( 1.000,  0.000), vec2( 0.574,  0.819),
                                             vec2(-0.259,  0.966), vec2( 0.766, -0.643),
                                             vec2(-0.883,  0.469), vec2( 0.174, -0.985));
        // ...at IRRATIONAL fractions of the peak wavelength. Four harmonically-related components repeat on
        // their common period, and at the scale water is viewed that period is a visible corduroy -- the
        // "you can see the wave pattern" complaint. Ratios that share no common multiple cannot close.
        const float SOWX_SEA_SCALE[6] = float[6](1.0, 0.707, 0.437, 0.283, 0.171, 0.109);
        // A slow, large-scale wander applied to each component's PHASE. Six irrational components still
        // correlate locally; drifting their phases across the world with noise means no two patches of sea
        // are ever in the same state, which is what actually kills the sense of a repeating tile.
        const float SOWX_SEA_WANDER = 0.035;

        /// #147: the fetch-limited sea state. Significant wave height and peak wavelength from the wind
        /// speed and the run of open water the wind crosses -- the two numbers the renderer measures
        /// (WaterBodies) and the shader turns into a look. This is the whole small-versus-large answer:
        /// nothing here is branched on "is this a pond", the growth law simply produces a millimetre ripple
        /// at pond fetch and a metre swell at sea fetch.
        /// <para>Two DIFFERENT sizes go in, and the split is load-bearing rather than tidy. Amplitude reads
        /// the directional fetch, so a canal really does chop harder along its length. The wavelength reads
        /// the body's direction-independent size, because a wavelength that answered the wind's DIRECTION
        /// would swing its frequency as the wind turned and teleport the entire field -- the same failure
        /// inc_clouds.shd documents for wind SPEED, arriving through the other term.</para>
        void SowxSeaState(float fetch, float size, float wind, out float hs, out float lp)
        {
            // SMB fetch-limited growth. Both terms matter: a gale over a puddle is still a puddle, and a
            // breeze over an ocean still raises swell.
            hs = 0.0016 * sqrt(max(fetch, 0.0)) * max(wind, 0.0);
            // A dead calm is not a mirror. Real water always carries a few millimetres of disturbance from
            // convection and stray air, and stock floors its own wind at 0.356 for exactly this reason -- a
            // perfectly flat pond reads as a bug, not as weather.
            hs = max(hs, 0.004);
            // Peak period from the REFERENCE wind (see SOWX_WIND_REF), then deep-water dispersion for the
            // wavelength: L = g T^2 / 2pi.
            float tp = 0.286 * pow(max(SOWX_WIND_REF * size, 1e-3), 1.0 / 3.0);
            lp = max(SOWX_G * tp * tp / SOWX_TAU, 0.12);
        }

        /// One component of the sum: a sine sharpened toward Gerstner crests, with its gradient computed
        /// analytically rather than by differencing. Differencing a field this steep at a fixed epsilon
        /// smooths exactly the crests the foam test is looking for.
        /// <para>Returns the height; accumulates the world-space gradient into <paramref name="grad"/>.</para>
        float SowxSeaWave(vec2 p, float t, vec2 dir, float len, float amp, float sharp, float wander,
                          inout vec2 grad)
        {
            float k = SOWX_TAU / max(len, 1e-3);
            // Deep-water dispersion: longer waves genuinely travel faster, which is what stops a multi-scale
            // sum from looking like one texture scrolling.
            float omega = sqrt(SOWX_G * k);
            // The phase wander. Its gradient is deliberately NOT folded into `grad`: it varies over tens of
            // metres while the wave varies over one, so its slope is three orders down and paying a second
            // noise fetch per component to include it would buy nothing visible.
            float ph = dot(dir, p) * k - t * omega + wander;
            float c = 0.5 + 0.5 * sin(ph);
            float shaped = pow(c, sharp);
            // d/dph of (2*c^s - 1), with c = 0.5 + 0.5*sin(ph)
            float dsh = sharp * pow(max(c, 1e-4), sharp - 1.0) * cos(ph);
            grad += amp * dsh * k * dir;
            return amp * (2.0 * shaped - 1.0);
        }

        /// The whole field: four fixed directions weighted by how well each aligns with the wind, at four
        /// fractions of the peak wavelength. Amplitude falls off with distance so the far half of a sea is a
        /// mirror rather than aliased static.
        /// <para><paramref name="pixel"/> is the world size of one screen pixel here, and it is what makes
        /// distance work. A single global amplitude fade flattens far water into painted sheet -- the wrong
        /// remedy for the right worry. Instead each COMPONENT retires as its own wavelength approaches a
        /// pixel, and the slope energy it took with it comes back out in
        /// <paramref name="msSlope"/> as roughness. Nothing is lost, it changes representation: geometry the
        /// screen cannot resolve becomes a wider specular lobe, which is what a distant sea actually looks
        /// like -- glitter, not a mirror.</para>
        float SowxSeaField(vec2 p, float t, float hs, float lp, vec2 wdir, float sharp, float pixel,
                           out vec2 grad, out float msSlope)
        {
            grad = vec2(0.0);
            msSlope = 0.0;
            float h = 0.0;
            float amp = hs * 0.5;
            // One noise fetch, shared by every component at a different offset: the wander only has to be
            // slowly-varying and decorrelated, not independent.
            float wander = SowxNoise3D(vec3(p * SOWX_SEA_WANDER, t * 0.013)) * SOWX_TAU;
            for (int i = 0; i < SOWX_SEA_N; i++)
            {
                // A component running with the wind carries most of the energy; one running across it keeps
                // a quarter, which is what gives the surface cross-chop instead of a corduroy ridge.
                float align = mix(0.25, 1.0, 0.5 + 0.5 * dot(SOWX_SEA_DIR[i], wdir));
                float len = lp * SOWX_SEA_SCALE[i];
                // Energy per component falls with wavelength, so the short chop rides on the swell rather
                // than competing with it.
                float a = amp * align * SOWX_SEA_SCALE[i];
                // Retire below three pixels per wave, gone by one. Anything finer aliases into crawling
                // static however carefully it is filtered.
                float vis = pixel > 0.0 ? smoothstep(1.0, 3.0, len / pixel) : 1.0;
                // Mean square slope of a sine of amplitude a and wavenumber k is (a*k)^2 / 2. What the
                // component no longer contributes as SHAPE, it contributes as roughness.
                float k = SOWX_TAU / max(len, 1e-3);
                float ms = 0.5 * (a * k) * (a * k);
                msSlope += ms * (1.0 - vis * vis);
                if (vis > 0.001)
                    h += SowxSeaWave(p, t, SOWX_SEA_DIR[i], len, a * vis, sharp,
                                     wander * float(i + 1) * 0.37, grad);
            }
            return h;
        }

        """;

    /// <summary>#129's fragment stage, part one: what water reads. Declarations only -- the per-draw record,
    /// the lighting rig and the fog/keyhole block all arrive from the same strings the lit stage composes, so
    /// the two cannot drift on any of them.</summary>
    internal const string FsHead = """
        #version 460 core
        in vec2 vUV;
        in vec3 vWorld;
        in vec3 vNrm;
        in float vDepth;
        in float vAnchorZ;
        // #147: the UNDISPLACED horizontal position, from the evaluation stage. The wave field is defined
        // over this, so evaluating it here reproduces the gradient at the point that stage actually raised --
        // reading vWorld.xy instead would sample a different point on a different wave and the shading would
        // slide across the geometry.
        in vec2 vWavePos;
        flat in int vDraw;

        """ + AlloyRenderer.DrawBufDecl + AlloyRenderer.FeatDecl + AlloyRenderer.KeyFogDecl + """
        uniform sampler2D uTex;        // the water material's diffuse -- its AVERAGE is the liquid's colour
        // #129: the one framebuffer copy, taken at PassId.FbCopy. Since #132 its colour is LINEAR -- the
        // copy is of PostChain's own target, every Alloy stage writes linear into it, and the single encode
        // happens later at the resolve. Depth is the raw window-space buffer, which SowxSceneZ linearises
        // with this pass's own near/far rather than with an engine uniform.
        uniform sampler2D uFbColor;
        uniform sampler2D uFbDepth;
        uniform vec2 uNearFar;
        uniform mat4 uView;            // world -> view, this pass's own (see uVP's row-vector note)
        uniform mat4 uProj;
        uniform vec3 uAreaAmbient;
        uniform vec3 uAreaDiffuse;
        uniform vec3 uAreaDir;
        uniform vec3 uEyePos;
        // #147: seconds of REAL time. Not uTimeOfDay -- that is the game clock, which a module runs at any
        // multiple of real time (2 real minutes an hour by default, so 30x) and which would carry the whole
        // sea at that multiple.
        uniform float uWaveTime;
        uniform vec2 uTessDials;
        uniform int uViz;
        uniform samplerCube uProbe;
        uniform float uProbeMips;
        uniform float uProbeGain;
        uniform int uSceneEnv;
        uniform int uCapture;
        uniform float uAlphaCut;
        // The /water tint, wave, ssr and foam dials: (e-fold depth in metres, wave amplitude scale,
        // screen-space reflection weight, foam scale).
        uniform vec4 uWaterDials;
        out vec4 frag;

        // Water's own material constants, from the stock water shader's own defines (base_shaders.bif,
        // inc_water.shd / fswater.shd): WATER_SPECULARITY 0.02 -- the normal-incidence reflectance of water,
        // which is a real physical number and not a taste dial -- and a near-mirror roughness.
        const float SOWX_WATER_F0 = 0.02;
        const float SOWX_WATER_ROUGH = 0.03;
        // Refraction happens over a short distance and reflection over a long one, which is why stock marches
        // them with different budgets (WATER_REFRACTION_ITERATIONS 5 / MAX_RANGE 4.0, and 10 iterations out
        // to 30 for the reflection). Same split, same reasoning: a refracted ray leaves the surface almost
        // straight down and hits the floor within metres.
        const int SOWX_REFRACT_STEPS = 5;
        const float SOWX_REFRACT_RANGE = 4.0;
        const int SOWX_REFLECT_STEPS = 12;
        const float SOWX_REFLECT_RANGE = 30.0;
        // Index of refraction of water, as the stock shader's own 1.0/1.33333.
        const float SOWX_WATER_IOR = 0.75;

        """;

    /// <summary>#129's fragment stage, part two: the march, the ripple normal and the composite.
    /// <para>The march is the stock <c>GetRayIntersectCoords</c>'s method -- step along the view-space ray,
    /// read the copy's depth at the projected point, and use the signed depth difference as the next step, so
    /// the iteration converges on the crossing rather than sampling a fixed ladder. Two things are different
    /// and both are deliberate. It projects through the FULL projection with a perspective divide, where
    /// stock uses <c>mat3(m_proj)</c> and divides by the third component -- an approximation that is only
    /// exact as far as -(f+n)/(f-n) is -1. And the miss is answered by the PROBE.</para>
    /// <para>That last one is the ticket. Screen-space reflection can only show what is already on screen, so
    /// a sea reflects the shoreline in front of the camera and nothing of the sky above it -- stock falls
    /// back to the water's own shaded colour, which is why vanilla water looks like flat paint at the
    /// horizon. Feeding #121's per-area cube there gives the sky, the horizon and the room back, while the
    /// march keeps doing the thing it is genuinely good at: near-field contact reflections, the boat and the
    /// pier standing in the water right where they meet it.</para>
    /// <para>ponytail: what is NOT ported from stock's 563-line fork -- subsurface scattering, the caustic
    /// veins and ripple sources SoW added, the wind-point-source splash array, and the noise TEXTURE the wave
    /// field is sampled from (this uses the simplex noise the Alloy pass already carries for the camera
    /// dissolve, so the wave shape differs from vanilla's). Each is a visible feature and none is a
    /// reflection feature; file them as follow-ups if the sea reads wrong for their absence, the way #136
    /// carries the sky's own shader port.</para></summary>
    internal const string FsMain = """

        // Window-space depth from the copy, linearised to a POSITIVE view-space distance. Reconstructed from
        // this pass's own near/far rather than from an engine uniform: the Alloy frame already extracts them
        // from its projection (AlloyLights.TryPerspective) and a second source could disagree with the
        // matrix the geometry was actually drawn with.
        float SowxSceneZ(vec2 uv)
        {
            float d = texture(uFbDepth, uv).x * 2.0 - 1.0;
            float n = uNearFar.x, f = uNearFar.y;
            return (2.0 * n * f) / (f + n - d * (f - n));
        }

        /// The copy's colour. #132: linear already -- kept as a named read so the marches and the term
        /// views all go through one place if it ever stops being.
        vec3 SowxFbColor(vec2 uv)
        {
            return texture(uFbColor, uv).rgb;
        }

        /// March a view-space ray against the copy's depth. Returns (uv, hit confidence in z) -- confidence 0
        /// is a clean miss and the caller falls back to the probe, and everything between is the edge fade
        /// that keeps the handover from drawing a seam across the water.
        vec3 SowxWaterMarch(vec3 originView, vec3 rayView, float range, int steps)
        {
            // Marched in WORLD UNITS, deliberately unlike stock. Stock scales its range by an "angle factor"
            // and divides the ray by the same number, which cancels exactly -- neither the step size nor the
            // total distance changes -- while leaving `dist` parameterised in units of |ray| = 1/angle.
            // Its secant step then adds a view-space depth delta straight onto that, overshooting the
            // crossing by up to 10x at grazing angles: precisely the geometry water reflections live in.
            vec3 ray = normalize(rayView);

            float stepMax = range / float(steps);
            float dist = 0.0;
            float delta = stepMax;
            vec2 bestUv = vec2(-1.0);
            float bestErr = 1e9;
            bool crossed = false;

            for (int i = 0; i < steps; i++)
            {
                dist = max(0.01, dist + clamp(delta, -stepMax, stepMax));
                vec3 p = originView + ray * dist;
                vec4 clip = uProj * vec4(p, 1.0);
                if (clip.w <= 0.0) break;
                vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;
                // p.z is negative in view space and SowxSceneZ is positive, so their sum is the SIGNED
                // distance from the sampled surface: negative once the ray has gone behind it.
                delta = SowxSceneZ(uv) + p.z;
                float err = abs(delta) / (dist + 1.0);
                if (err < bestErr) { bestErr = err; bestUv = uv; }
                if (delta < 0.0) crossed = true;
                // Once the ray is behind the surface the crossing is bracketed; halve the step and let the
                // signed delta walk it back. This is a binary search in all but name.
                if (crossed) stepMax *= 0.5;
            }

            if (!crossed || bestUv.x < 0.0) return vec3(0.0, 0.0, 0.0);
            // Thin-object rejection, which the first cut of this march skipped. A depth buffer records a
            // surface, not a solid: a ray that passes BEHIND a piling still registers a crossing, and taking
            // the colour there reflects a pillar the ray actually missed. The classic screen-space artifact,
            // and on a rippled surface it is what turns a reflection into drifting blobs -- so a crossing
            // whose final error is a large fraction of the step is not a hit at all.
            if (bestErr * (dist + 1.0) > stepMax * 4.0 + 0.5) return vec3(0.0, 0.0, 0.0);
            // Confidence: how close the best sample actually came, and how far it is from the screen edge.
            // The edge term is what makes the probe handover a fade rather than a line -- a reflected object
            // sliding off the side of the screen dissolves into the sky instead of being clipped out of it.
            float quality = clamp(1.0 - bestErr / 0.2, 0.0, 1.0);
            vec2 edge = min(bestUv, 1.0 - bestUv);
            float fade = clamp(min(edge.x, edge.y) / 0.08, 0.0, 1.0);
            return vec3(bestUv, quality * fade);
        }

        /// The surface normal from that gradient, re-anchored onto the part's own normal so a tilted water
        /// plane (a canal bank, a waterfall lip) ripples along itself rather than along the world's XY.
        vec3 SowxSeaNormal(vec3 flat_n, vec2 grad)
        {
            vec3 n = normalize(vec3(-grad.x, -grad.y, 1.0));
            return normalize(flat_n + (n - vec3(0.0, 0.0, 1.0)));
        }

        void main()
        {
            SowxDraw d = uDraw[vDraw];
            // /draw viz 4 is the coverage view: cyan is Alloy lit and magenta owned-fullbright, so
            // water takes BLUE -- the whole question that view answers is "which renderer drew this", and a
            // water surface sharing the lit colour would hide the class that is hardest to tell apart.
            if (uViz == 4) { frag = vec4(0.0, 0.0, 1.0, 1.0); return; }

            vec3 flatN = normalize(vNrm);
            vec3 V = normalize(uEyePos - vWorld);
            vec2 screenUv = gl_FragCoord.xy * uPixel.xy;

            // The liquid's own colour is the diffuse's AVERAGE, not a texel: a water material's texture is a
            // ripple pattern, and sampling it per fragment would paint that pattern onto a surface whose
            // shape is coming from the wave field instead. Stock reads it at LOD 20 for the same reason.
            vec4 baseTexel = textureLod(uTex, vec2(0.5), 20.0);
            // NOT multiplied by d.dif. The record's material colours are written only on LIT draws
            // (BuildBatches), so an unlit water batch would read whatever the previous occupant of the slot
            // left there -- a stale value of any magnitude, uniform across the batch. Stock does not tint
            // the water base by materialFrontDiffuse either: WaterBaseColor is the texture alone.
            vec3 liquidColor = SowxLinear(baseTexel.rgb);

            // #147: the body this fragment's water belongs to. WaterBodies measures it on the CPU and
            // BuildBatches writes it into the record's material-colour slot, which the water stage is the
            // only reader of. x = fetch along the wind (drives amplitude), y = the body's
            // direction-independent size (drives the wavelength), z = its smaller footprint dimension
            // (caps it), w = wind speed in m/s. A zero fetch is an untracked plane, not a puddle.
            bool tracked = d.dif.x > 0.0;
            float fetch = tracked ? d.dif.x : 30.0;
            float bodySize = tracked ? d.dif.y : fetch;
            float bodyMin = tracked ? d.dif.z : fetch;
            float windSpeed = d.dif.w;
            vec2 windDir = uAreaWind.xy;
            float windLen = length(windDir);
            windDir = windLen > 1e-4 ? windDir / windLen : vec2(0.7071, 0.7071);

            float hs, lp;
            SowxSeaState(fetch, bodySize, windSpeed, hs, lp);
            hs *= uWaterDials.y;
            // A wave cannot be longer than the water it is standing in. Without this a 4-metre well inherits
            // the peak wavelength its fetch implies and reads as one slab tilting.
            lp = min(lp, bodyMin * 0.6);
            // Crests sharpen as the wind gets ahead of the sea; a glassy calm is a plain sine.
            float sharp = mix(1.0, 3.0, smoothstep(SOWX_WHITECAP_U0 * 0.5, SOWX_WHITECAP_U1, windSpeed));
            // The world size of one pixel here. uProj[1][1] is 1/tan(fovY/2) and uPixel.y is 1/height, so
            // this is the standard footprint: 2*z*tan(fovY/2)/height.
            float pixel = 2.0 * vDepth * uPixel.y / max(uProj[1][1], 1e-4);
            float t = uWaveTime;
            vec2 grad;
            float msSlope;
            float waveH = SowxSeaField(vWavePos, t, hs, lp, windDir, sharp, pixel, grad, msSlope);
            vec3 N = SowxSeaNormal(flatN, grad);
            // The sub-pixel waves, as roughness. For GGX the variance adds in alpha^2 (Toksvig / LEAN
            // mapping), which is what turns the far half of a harbour from a flat mirror into the broad
            // shimmer a big body of water actually shows at range -- and it is the same energy, moved from
            // geometry to the specular lobe rather than thrown away.
            float rough = sqrt(SOWX_WATER_ROUGH * SOWX_WATER_ROUGH + 2.0 * msSlope);
            rough = clamp(rough, SOWX_WATER_ROUGH, 0.45);
            vec3 Nview = normalize(mat3(uView) * N);

            // The sun's terms, hoisted: both the body's own lighting and the specular lobe want them, and
            // SowxSunShadow is a cascade lookup -- paying for two per water fragment to save re-reading one
            // variable is the wrong trade on a surface that can fill the screen.
            float sunRaw = clamp(dot(N, uAreaDir), 0.0, 1.0);
            float sunVis = SowxSunShadow(vWorld, N, sunRaw);

            vec3 viewPos = (uView * vec4(vWorld, 1.0)).xyz;
            float thisZ = -viewPos.z;
            float floorZ = SowxSceneZ(screenUv);
            // How much water the eye is looking through, at this pixel. Negative would mean the depth copy
            // has something in FRONT of the surface -- an Alloy blended draw, say -- and the honest answer
            // there is "no depth at all" rather than a negative thickness.
            float thickness = max(floorZ - thisZ, 0.0);

            // ---- refraction: the floor, seen through the surface -------------------------------------
            vec3 refractView = refract(-normalize(mat3(uView) * V), Nview, SOWX_WATER_IOR);
            vec3 rHit = SowxWaterMarch(viewPos, refractView, SOWX_REFRACT_RANGE, SOWX_REFRACT_STEPS);
            // A missed refraction ray is not a failure worth a fallback: the floor directly under this pixel
            // IS in the copy, and using it is exactly stock's own mix toward vScreenCoords. What the march
            // buys is the DISTORTION, not the colour.
            vec2 refractUv = mix(screenUv, rHit.xy, rHit.z);
            vec3 floorColor = SowxFbColor(refractUv);
            // Beer-Lambert: the deeper the water at this pixel, the less of the floor survives it. The dial
            // is the e-fold depth in metres, which is the one number that separates a harbour from a lagoon.
            float clarity = exp2(-thickness / max(uWaterDials.x, 0.05));
            // The light the water body itself receives. It scales the LIQUID only: floorColor came out of
            // the framebuffer copy, which is a finished, already-shaded image -- lighting it a second time
            // would make every shallow read twice as bright as the riverbed it is standing on.
            vec3 onWater = uAreaAmbient * mix(0.15, 1.0, sunRaw) + uAreaDiffuse * sunRaw * sunVis;
            vec3 through = mix(liquidColor * onWater, floorColor, clarity);

            // ---- reflection: the march for contact, the probe for everything else --------------------
            vec3 R = reflect(-V, N);
            vec3 reflHit = SowxWaterMarch(viewPos, normalize(mat3(uView) * R), SOWX_REFLECT_RANGE, SOWX_REFLECT_STEPS);
            // The probe IS the fallback, and it is also the base the march composites onto -- so a partial
            // hit near the screen edge blends into the sky instead of stepping to it. uProbeMips 0 means no
            // capture has completed yet, and then the water's own colour stands in, which is vanilla's answer.
            vec3 skyColor = (uCapture == 0 && uProbeMips > 0.0)
                          ? textureLod(uProbe, R, rough * (uProbeMips - 1.0)).rgb * uProbeGain
                          : liquidColor;
            vec3 reflection = mix(skyColor, SowxFbColor(reflHit.xy), reflHit.z * uWaterDials.z);

            // ---- lighting: the sun's glitter and every torch the froxel holds ------------------------
            float NdotV = max(dot(N, V), 0.0);
            vec3 pointSpec;
            SowxClusteredLight(vWorld, N, V, vDepth, rough, SOWX_WATER_F0, pointSpec);
            vec3 specLight = (uAreaDiffuse * (sunRaw * sunVis)
                                * SowxSpecLobe(N, V, uAreaDir, rough, SOWX_WATER_F0) + pointSpec)
                             * SowxSpecBase(NdotV, rough);

            // ---- composite ---------------------------------------------------------------------------
            // Schlick at water's real F0. This is the whole reason a sea is a mirror at the horizon and a
            // window at your feet, and it is why the probe fallback matters so much: the grazing angles that
            // reflect hardest are exactly the ones whose rays leave the screen.
            float fresnel = mix(SOWX_WATER_F0, 1.0, SowxFresnel(NdotV));
            vec3 lit = through * (1.0 - fresnel) + reflection * fresnel + specLight;

            // ---- foam: where the sea breaks, and where it meets the land ----------------------------
            // FOAM IS AGITATION, not an outline. Still water has none at all -- froth is entrained air, and
            // nothing entrains air in a millpond. Both terms are gated on the sea state before anything
            // else, so a well or a cistern gets an unbroken surface right up to the stone.
            float agitation = smoothstep(0.015, 0.09, hs);
            float whitecap = smoothstep(SOWX_WHITECAP_U0, SOWX_WHITECAP_U1, windSpeed)
                           * smoothstep(SOWX_WHITECAP_FETCH * 0.4, SOWX_WHITECAP_FETCH, fetch);
            float hNorm = waveH / max(hs, 1e-4);
            // A breaking crest is high AND steep. Height alone foams the whole swell; steepness alone foams
            // every ripple.
            float crestFoam = whitecap * smoothstep(0.35, 0.9, hNorm) * smoothstep(0.25, 0.9, length(grad));

            // The wash. Not a band at a fixed depth -- that traces the water's own polygon and reads as a
            // laser cut, which is exactly what it looked like. The waterline MOVES: a crest runs up the
            // shore and drains back, so the surf is where the LOCAL water level has overtaken the bed.
            // Subtracting the wave height from the thickness is that, and it animates for free because
            // waveH already does.
            float shoreWidth = mix(0.15, 2.0, clamp(hs / 0.35, 0.0, 1.0));
            // Break the line itself up before it is ever thresholded, or every wobble added later is a
            // wobble applied to a straight edge rather than an edge that was never straight.
            float edge = SowxNoise3D(vec3(vWavePos * 1.1, t * 0.23)) * shoreWidth * 0.5;
            float run = thickness - waveH + edge;
            float shore = 1.0 - smoothstep(0.0, shoreWidth, max(run, 0.0));
            // ...and the froth left BEHIND the run-up. Foam outlives the wave that made it, which is most of
            // what tells the eye it is foam and not a highlight: a second, wider, weaker band keyed to where
            // the water was a moment ago.
            // Its own out-params: handing this call `grad` would overwrite the gradient the normal and the
            // crest test were built from, and the two uses are far enough apart that nothing would flag it.
            vec2 lagGrad; float lagMs;
            float lag = thickness - SowxSeaField(vWavePos, t - 1.6, hs, lp, windDir, sharp, pixel,
                                                 lagGrad, lagMs) + edge;
            float trail = (1.0 - smoothstep(0.0, shoreWidth * 1.8, max(lag, 0.0))) * 0.45;
            shore = max(shore, trail) * agitation;

            // Broken up at TWO scales -- a coarse patchiness that decides where foam is at all, and a fine
            // grain inside it. One octave gives a smooth analytic blob, which is the one thing foam is not.
            // Advected along the wind, so the froth drifts instead of shimmering in place.
            vec2 drift = windDir * t * 0.35;
            float foamCoarse = SowxNoise3D(vec3(vWavePos * 0.35 - drift * 0.3, t * 0.11));
            float foamFine = SowxNoise3D(vec3(vWavePos * 2.3 - drift, t * 0.5));
            float foamNoise = clamp(0.35 + 0.5 * foamCoarse + 0.35 * foamFine, 0.0, 1.0);
            float foam = clamp((crestFoam * agitation + shore) * foamNoise * uWaterDials.w, 0.0, 1.0);
            // What foam is LIT by, and the reason the first cut put a blue rim on every pond: foam took
            // `onWater`, which is area ambient plus sun -- at night that is a blue ambient and nothing else,
            // so the froth came out the colour of the sky. Foam is a dense white scattering layer sitting ON
            // the surface: it is a rough Lambertian, it sees every torch the froxel holds, and it is the one
            // part of a night harbour that should read WARM next to a lantern.
            vec3 foamLight = uAreaAmbient * 0.9
                           + uAreaDiffuse * (sunRaw * sunVis)
                           + SowxClusteredDiffuse(vWorld, N, vDepth);
            // Not pure white: real foam is packed bubbles over dark water and reads around 0.8 at its
            // brightest, thinning toward the water's own colour at the edges of the mask.
            vec3 foamColor = mix(liquidColor, vec3(0.8), clamp(foam * 1.4, 0.0, 1.0)) * foamLight;
            // It REPLACES the surface underneath -- froth is opaque, so reflection and refraction both stop
            // where it covers.
            lit = mix(lit, foamColor, foam);
            if (uViz == 6) { frag = vec4(specLight, 1.0); return; }
            // #129's own term views. Water composites four things that can each be wrong on their own and
            // look identical in the sum -- "the water is the wrong colour" is not a diagnosis, and a
            // screenshot cannot become one. 9 = what is seen THROUGH the surface, 10 = the reflection,
            // 11 = fresnel as grey (the wave field's shape is visible in it), 12 = the framebuffer copy
            // exactly as sampled (black here means the blit is not landing), 13 = the copy's linearised
            // depth over 100m as grey (flat white or flat black means the DEPTH half is not landing, which
            // is what makes both marches meaningless).
            if (uViz == 9)  { frag = vec4(through, 1.0); return; }
            if (uViz == 10) { frag = vec4(reflection, 1.0); return; }
            if (uViz == 11) { frag = vec4(vec3(fresnel), 1.0); return; }
            if (uViz == 12) { frag = vec4(SowxFbColor(screenUv), 1.0); return; }
            if (uViz == 13) { frag = vec4(vec3(clamp(floorZ / 100.0, 0.0, 1.0)), 1.0); return; }
            // 14 = the screen UV the two views above sample AT. A smooth red/green ramp is correct; a flat
            // colour means uPixel never reached this stage and every fragment is reading ONE texel, which
            // makes 12 and 13 look like a broken copy when the copy is fine.
            if (uViz == 14) { frag = vec4(screenUv, 0.0, 1.0); return; }
            // 15 = the depth copy's RAW texel, before any linearisation. 0 = never written (the blit is not
            // landing), 1 = cleared to the far plane and nothing drew there, anything between = a real
            // depth, which moves the question to uNearFar rather than to the copy.
            // 15 = the depth copy's RAW texel. Kept, but read it knowing depth is NONLINEAR: every pixel of
            // real geometry sits above 0.95, so this saturates to white whether the copy works or not. It
            // says "is there anything at all here", nothing more.
            if (uViz == 15) { frag = vec4(vec3(texture(uFbDepth, screenUv).x), 1.0); return; }
            // 16 = clarity, the Beer-Lambert term that decides how much of the floor survives the water.
            // WHITE means the water is reading as infinitely shallow (all floor, no body); BLACK means
            // infinitely deep (all body, no floor) -- and black over a shallow pool is the flat, uniform,
            // reflection-free surface this whole hunt is about.
            if (uViz == 16) { frag = vec4(vec3(clarity), 1.0); return; }
            // 17 = the thickness driving it, over 10m. Flat white here with 16 black says the surface and
            // the copy's depth disagree about where the water is -- a view-space problem, not a copy one.
            if (uViz == 17) { frag = vec4(vec3(clamp(thickness / 10.0, 0.0, 1.0)), 1.0); return; }
            // 18 = the SURFACE's own view distance on the same 100m scale as 13, so the two are directly
            // comparable on the same pixels. 13 comes from the depth copy and 18 from uView; if they
            // disagree by more than the water is deep, the copy's linearisation and the view matrix are
            // measuring different spaces, and thickness -- hence clarity -- is meaningless.
            if (uViz == 18) { frag = vec4(vec3(clamp(thisZ / 100.0, 0.0, 1.0)), 1.0); return; }
            // #147's own views. 19 = the body the renderer measured -- red is fetch over 200u, green the
            // body's smaller dimension over 200u, blue the wind speed over 20. A plane WaterBodies never
            // recorded shows as flat MAGENTA, not as a dim reading: the fallbacks above would otherwise make
            // an untracked plane look like a plausible small pond, which is the one mistake this view exists
            // to catch. 20 = the wave field, grey 0.5 at rest. 21 = foam coverage.
            if (uViz == 19)
            {
                if (!tracked) { frag = vec4(1.0, 0.0, 1.0, 1.0); return; }
                frag = vec4(clamp(fetch / 200.0, 0.0, 1.0), clamp(bodyMin / 200.0, 0.0, 1.0),
                            clamp(windSpeed / 20.0, 0.0, 1.0), 1.0); return;
            }
            if (uViz == 20) { frag = vec4(vec3(clamp(waveH / max(hs, 1e-4) * 0.5 + 0.5, 0.0, 1.0)), 1.0); return; }
            // 21 = foam. Red is coverage, green is the AGITATION gate that has to open before any foam
            // exists at all -- a still pond reading black in green with a red rim would mean the gate leaked.
            if (uViz == 21) { frag = vec4(foam, agitation, 0.0, 1.0); return; }
            if (uCapture != 0) { frag = vec4(lit, 1.0); return; }

            float kh = SowxKeyhole(d.rot.z, vWorld, vDepth);
            if (kh < 0.0) discard;
            // Alpha 1 in every ordinary case -- rot.w is the part's own multiplier times the tile fade, and
            // only a fading tile puts it below 1. That is deliberate and it is stock's own
            // `FragmentColor.a = 1.0`: everything behind the surface has already been composited IN by the
            // refraction term, so letting the framebuffer show through would count the floor twice.
            frag = vec4(SowxFog(lit * kh, vDepth), d.rot.w);
        }
        """;
}
