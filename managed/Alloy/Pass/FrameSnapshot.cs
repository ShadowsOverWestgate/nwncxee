using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The Alloy frame's ONE framebuffer copy, and the water stage that reads it (ADR 0011 M3, #129). The copy
/// is colour and depth of everything the Alloy frame has drawn up to <see cref="PassId.FbCopy"/> -- the
/// opaque set, grass, the sky, the blended class with fade in it -- snapshotted into this class's own
/// texture pair so the water stage can march them. The engine takes TWO such snapshots per frame
/// (<c>Camera::CopyRenderSubframe</c> @0x4a6b90, before draw bucket 5 and again before bucket 6 in
/// <c>Scene::RenderSinglePass</c> @0x46e5f0) because its bucket order is fixed, not because two are needed;
/// the Alloy frame takes one, and the framebuffer-sampling class (refraction glass) that the bucket-5 copy
/// served has no Alloy program and is not drawn (<see cref="RspShim"/>). Ours holds LINEAR HDR, taken inside
/// the Alloy bracket, so it could not have fed a display-space engine draw anyway.
/// <para><c>water.enabled</c> off stands the whole thing down: no copy, no Alloy water program, and
/// <see cref="BucketGate"/> leaves bucket 6 to the engine. Every look dial here is a <c>/water</c> key; the
/// one boot decision is the tessellation segment count, which is geometry rather than look.</para>
/// </summary>
internal static unsafe class FrameSnapshot
{
    /// <summary>Texture units the copy binds on -- above the probe's cube, and clear of the grass push map
    /// and the #139 resolve even though those are other programs. <see cref="TextureSlots"/> is the ladder
    /// and the only place it is written down; do not paraphrase it here.</summary>
    internal const uint ColorUnit = TextureSlots.SnapshotColor, DepthUnit = TextureSlots.SnapshotDepth;

    /// <summary>#339: the unit the engine's own noise texture binds on. The vanilla wave field IS that
    /// texture at four scales, so the stage cannot draw without it.</summary>
    internal const uint NoiseUnit = TextureSlots.WaterNoise;

    /// <summary>The resref vanilla's water shader samples as <c>texUnitNoise</c>. Not a guess and not a
    /// string-table hunch: <c>Scene::Render</c> @0x5762d0 loads exactly this name into <c>Scene+0x1f8</c>
    /// and binds it with <c>SetActiveTextureAndBind(0xf, 4, tid)</c> -- unit 15, which is the unit
    /// <see cref="TextureSlots.WaterNoise"/> independently landed on.</summary>
    private const string NoiseResref = "solid_noise";

    /// <summary>#339: the unit the SCENE's environment map binds on -- the reflection vanilla's water
    /// carries and this port first shipped without.</summary>
    internal const uint EnvUnit = TextureSlots.WaterEnv;

    /// <summary><c>Scene+0x1e8</c>: the scene's environment map, a <c>SafePointer&lt;CAurTexture&gt;</c> whose
    /// first word is the borrowed texture. <c>Scene::Render</c> @0x5762d0 fills it once per scene from the
    /// TILESET's own resref (<c>CNWTileSet::GetEnvMapResRef</c>) and falls back to
    /// <c>AurTextureGetFallbackEnvironmentMap</c> when the tileset names none -- so it is present even where
    /// the MATERIAL has no env map, which is the case the <c>[water]</c> census line's <c>env=False</c>
    /// reports and which made this term look inapplicable.</summary>
    private static readonly int SceneEnvMap = EngineLayout.SceneEnvMap;

    /// <summary><c>water.enabled</c> off stands the class down; on brings it back at the next pass. Armed
    /// and idling rather than un-installed, so the toggle is live either way.</summary>
    internal static bool On => Water.Enabled.V;

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
    private static ExtenderLog? _log;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _pCurrentScene = (IntPtr*)Native.Sym(SymbolMap.CurrentScene);
        if (_pCurrentScene == null)
            log.Info("FrameSnapshot: `currentscene` unresolved -- the water stage cannot reach the scene's "
                   + "environment map, and its reflection term stays dark (the pre-#339 look)");
    }

    internal static string Describe()
        // #456: the noise texture DESCRIBED, not just named. A bound-but-incomplete texture (a mipmapped
        // min filter with no mips) samples as a constant, and a constant wave field is a flat sea -- which
        // is indistinguishable from "bound and fine" if all the line prints is the id.
        => $"water {(On ? "on" : "off")} (vanilla fswater)  noise tid {_noiseTid} on unit {NoiseUnit} "
         + $"resolved=\"{_noiseName}\" {_noiseEng} "
         + $"{(_noiseTid == 0 ? "" : TexProbe.TexFormat(_noiseTid) + " " + TexProbe.TexTopMip(_noiseTid) + " ")}"
         + $"rect {(Rect ? 1 : 0)} (last copy {LastRectFrac * 100f:F0}% of the frame)" +
           (Water.Diag.V > 0 ? $"  DIAG {Water.Diag.V} left" : "");

    /// <summary>Drop every GL object (context loss / disarm). Names mean nothing once the context is gone, so
    /// this forgets rather than deletes -- same rule as <see cref="PostChain.Forget"/>.</summary>
    internal static void Forget()
    {
        _fbo = 0; _colTex = 0; _depTex = 0; _w = 0; _h = 0;
        _depFormat = 0; _depMask = 0; _hdr = false; Ready = false; _noiseSamp = 0; _down.Forget();
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
    /// <summary>Copy only the rows and columns the water can cover (<c>/water rect 0</c> = whole frame).</summary>
    internal static bool Rect => Water.Rect.V;
    /// <summary>Margin around the water's screen rectangle, as a fraction of the viewport height.</summary>
    internal const float RectMargin = 0.05f;
    /// <summary>The last copy's rectangle as a fraction of the frame; 1 = a whole-frame copy.</summary>
    internal static float LastRectFrac { get; private set; } = 1f;

    internal static bool Copy(int* viewport) => Copy(viewport, 0, 0, viewport[2], viewport[3]);

    /// <summary>The copy over one rectangle of the frame, source and destination at the same coordinates so
    /// the stage's gl_FragCoord read stays 1:1. Texels outside the rectangle keep the previous copy.</summary>
    internal static bool Copy(int* viewport, int x0, int y0, int x1, int y1)
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
        Gl.BlitFramebuffer(x0, y0, x1, y1, x0, y0, x1, y1, Gl.COLOR_BUFFER_BIT | _depMask, (uint)Gl.NEAREST);
        LastRectFrac = (float)(x1 - x0) * (y1 - y0) / ((float)w * h);
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
        // water.diag is a BUDGET of readbacks: a Set, so the file follows the countdown (Timer.Change, no alloc).
        if (Water.Diag.V > 0) { Water.Diag.Set(Water.Diag.V - 1); Diagnose((uint)src, w, h); }
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
        _log?.Info($"[waterdiag] src fbo {src} (post hdr fbo {PostChain.Fbo}, copy fbo {_fbo}) centre pixel ({x},{y}) -- source rgb({sc[0]:F4},{sc[1]:F4},{sc[2]:F4}) " +
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
    /// <summary>The colour copy's GL name for a reader outside the water program (grass), 0 until a copy has landed.</summary>
    internal static uint ColorTex => Ready ? _colTex : 0;

    /// <summary>Diag: a 3x3 grid of colour texels read from the copy and from <paramref name="src"/>, printed
    /// side by side with the scissor state and the source's colour attachment name.</summary>
    internal static void Compare(uint src, int w, int h, string tag)
    {
        if (Gl.ReadPixels == null || Gl.ReadBuffer == null || _fbo == 0) return;
        int sc = Gl.IsEnabled(Gl.SCISSOR_TEST);
        int* box = stackalloc int[4]; Gl.GetIntegerv(Gl.SCISSOR_BOX, box);
        int name = 0;
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, src);
        if (src != 0) Gl.GetFramebufferAttachmentParameteriv(Gl.READ_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME, &name);
        var sb = new System.Text.StringBuilder();
        sb.Append($"[grassground] {tag}: src fbo {src} colour0 = tex {name}, copy tex {_colTex}, scissor {sc} box {box[0]},{box[1]} {box[2]}x{box[3]} |");
        float* a = stackalloc float[4]; float* b = stackalloc float[4];
        for (int j = 1; j <= 3; j++) for (int i = 1; i <= 3; i++)
        {
            int x = w * i / 4, y = h * j / 4;
            Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, src); if (src != 0) Gl.ReadBuffer(Gl.COLOR_ATTACHMENT0);
            Gl.ReadPixels(x, y, 1, 1, Gl.RGBA, Gl.FLOAT, a);
            Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _fbo); Gl.ReadBuffer(Gl.COLOR_ATTACHMENT0);
            Gl.ReadPixels(x, y, 1, 1, Gl.RGBA, Gl.FLOAT, b);
            sb.Append($" ({x},{y}) src {a[0]:F2},{a[1]:F2},{a[2]:F2} copy {b[0]:F2},{b[1]:F2},{b[2]:F2}{(a[0]==b[0]&&a[1]==b[1]&&a[2]==b[2] ? "" : " DIFF")};");
        }
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, src);
        _log?.Info(sb.ToString());
    }

    internal static void Bind()
    {
        Gl.ActiveTexture(Gl.TEXTURE0 + ColorUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _colTex);
        Gl.ActiveTexture(Gl.TEXTURE0 + DepthUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _depTex);
        // #339: re-resolved every bind and never cached -- a CAurTexture* is borrowed and the engine frees
        // it on an area transition (see AurTex). A miss binds nothing, which reads as a dead-flat sea rather
        // than as a wrong one, so it is logged once.
        // #456: the RESOLVED name, not the one we asked for. Windows returns a 512x350 RGBA texture where
        // Linux returns 1024x1024 compressed, so the open question is whether the resman handed back a
        // different RESOURCE or the same one built differently -- and only the object's own name says which.
        IntPtr noisePtr = CX.Framework.AurTex.FindByName(NoiseResref);
        int noise = CX.Framework.AurTex.Tid(noisePtr);
        if (noise != _noiseTid) { _noiseName = AlloyCensus.TexName(noisePtr); _noiseEng = TexScan.Describe(noisePtr); }
        _noiseTid = noise;
        // A zero here is "not uploaded YET" as often as "not there": AurTextureGet's create path queues the
        // upload on the engine's RefreshTextures list, so the first bind after a load legitimately misses.
        // Reported once, and worded as what it is -- a flat sea is the symptom either way.
        if (noise == 0 && !_noiseMissed)
        {
            _noiseMissed = true;
            _log?.Info($"FrameSnapshot: the engine texture \"{NoiseResref}\" has no GL id yet -- if the water "
                     + "reads flat, the vanilla wave field never got its noise");
        }
        Gl.ActiveTexture(Gl.TEXTURE0 + NoiseUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)noise);
        Gl.ActiveTexture(Gl.TEXTURE0);
        // ...under a sampler of our own, for the WRAP MODE. The wave field indexes the noise by world
        // position at four scales, so anything but REPEAT samples one edge texel over a whole harbour --
        // the same flat sea a missing texture gives, and just as silent. The engine's own params on this
        // object are not ours to assume, and not ours to change either.
        if (EnsureNoiseSampler()) Gl.BindSampler(NoiseUnit, _noiseSamp);

        // #339: the scene's environment map, on its own unit. Re-read per bind for AurTex's reason -- the
        // pointer is borrowed and an area transition frees it.
        _envTid = SceneEnvTid();
        Gl.ActiveTexture(Gl.TEXTURE0 + EnvUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)_envTid);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    /// <summary>The scene's environment-map texture id, or 0 when there is none to sample. Read off the LIVE
    /// scene rather than cached: <c>currentscene</c> moves with the area, and the texture behind it is
    /// borrowed.</summary>
    private static int SceneEnvTid()
    {
        if (_pCurrentScene == null) return 0;
        IntPtr scene = *_pCurrentScene;
        if (scene == IntPtr.Zero) return 0;
        return CX.Framework.AurTex.Tid(*(IntPtr*)(scene + SceneEnvMap));
    }

    /// <summary>Is the environment term live this pass? The stage reads it as a switch, because a missing
    /// env map must leave the surface shaded rather than multiplied by black.</summary>
    internal static int EnvOk => _envTid != 0 ? 1 : 0;

    private static IntPtr* _pCurrentScene;
    private static int _envTid;

    /// <summary>Drop the noise sampler off its unit. The unit is shared with the soft-particle scene depth
    /// (<see cref="TextureSlots.SceneDepth"/>), and a sampler object left bound overrides that texture's own
    /// parameters in a later pass.</summary>
    internal static void Unbind()
    {
        if (_noiseSamp != 0 && Gl.BindSampler != null) Gl.BindSampler(NoiseUnit, 0);
    }

    private static bool EnsureNoiseSampler()
    {
        if (_noiseSamp != 0) return true;
        if (Gl.GenSamplers == null || Gl.BindSampler == null || Gl.SamplerParameteri == null) return false;
        uint sm; Gl.GenSamplers(1, &sm);
        if (sm == 0) return false;
        Gl.SamplerParameteri(sm, Gl.TEXTURE_WRAP_S, Gl.REPEAT);
        Gl.SamplerParameteri(sm, Gl.TEXTURE_WRAP_T, Gl.REPEAT);
        // ponytail: LINEAR, not a mip filter. A mip filter on a texture that turns out to have only level 0
        // makes it INCOMPLETE, which samples black -- the one failure that looks exactly like the bug this
        // sampler exists to prevent. Add mips (and the level query behind them) if the far sea aliases.
        Gl.SamplerParameteri(sm, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.SamplerParameteri(sm, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        _noiseSamp = sm;
        return true;
    }

    private static bool _noiseMissed;
    private static uint _noiseSamp;
    /// <summary>The GL id the noise texture last resolved to, on the <c>/water</c> line. Zero with water on
    /// screen is the whole diagnosis of a flat sea: no noise, no wave amplitude.</summary>
    private static int _noiseTid;
    /// <summary>#456: what the resman actually handed back for <c>solid_noise</c>.</summary>
    private static string _noiseName = "?";
    /// <summary>The engine's own account of the noise texture, latched with the name (#456).</summary>
    private static string _noiseEng = "eng=?";

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
                   $"depth 0x{_depFormat:x} -- one copy per frame, /water enabled 0 disables");
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
        // owned framebuffer that can be bound here attaches its depth at
        // DEPTH_ATTACHMENT alone (PostChain.Begin does; nothing ever uses
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
        => _down.Fail(_log, why, "water is NOT DRAWN for the session -- #285: since #264 deleted the engine "
                               + "walk there is nothing behind the refusal");

    /// <summary>Per-pass: this particular pass cannot take the copy. NOT latched -- a GUI 3D panel or a
    /// reflection subframe legitimately refuses while the world pass keeps arming -- but the reason is logged
    /// once per distinct reason, because the fallback is otherwise silent.</summary>
    private static bool Refuse(string why)
        => _down.Refuse(_log, why, "no framebuffer copy this pass; water is not drawn on it (#285: a hole, not a hand-back)");

    /// <summary>The periodic line. <c>tod</c> is the engine clock the whole wave field's ANIMATION runs on
    /// (<c>fTick = worldtimerTimeOfDay * 0.001</c>), printed with its delta over the census period: a delta
    /// of ZERO means the phase never advances and the sea is a still photograph of a wave field, however
    /// correct that field is in space. Cheap, and it is the one thing a screenshot cannot show.</summary>
    internal static string Stats(int frames)
    {
        int tod = AlloyRenderer.TimeOfDay;
        int delta = tod - _todLast;
        _todLast = tod;
        return $"water: {Copies} copies, {Refusals} refused" + (frames > 0 ? $" over {frames} frames" : "")
             + $", tod {tod} ({(delta >= 0 ? "+" : "")}{delta} since last)";
    }

    private static int _todLast;

    /// <summary>The <c>water</c> knobs, off-device: the master stands the class down without touching the
    /// stand-down latch, and the diagnostic budget has no boot value -- a readback is a live request.</summary>
    private static void CheckTuning(Action<bool, string> e)
    {
        bool on = Water.Enabled.V; int diag = Water.Diag.V;
        try
        {
            Water.Diag.V = 1;
            e(Describe().Contains("DIAG 1 left"), "diag is a BUDGET: Describe says how many readbacks are owed while any are");
            Water.Enabled.V = false;
            e(!On && !Usable, "water.enabled off stands the class down without touching the stand-down latch");
            e(Water.Enabled.Default && Water.Diag.Default == 0, "reset re-arms the class and spends no diagnostic readbacks");
        }
        finally
        {
            Water.Enabled.V = on; Water.Diag.V = diag;
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
        // #339's own criterion, as a text check: the shading is stock fswater's, not a look of ours. The
        // march is vanilla's GetRayIntersectCoords, the wave field reads the ENGINE's noise texture, and the
        // reflection's fallback is the shaded surface -- a probe here would be the custom look coming back.
        E(fs.Contains("CxRayIntersect("), "the stage marches with stock's own ray intersection");
        E(fs.Contains("texture(uNoise,"), "the wave field reads the engine's noise texture");
        E(!fs.Contains("uProbe"), "the probe is NOT the reflection's fallback -- vanilla falls back to the surface");
        // Colour space: since #132 the whole Alloy frame is linear, so the copy is linear and a decode on
        // read would be a second linearisation. Sampling it in the wrong space is a whole-image error that
        // reads as "the reflection is washed out" (or "far too dark") rather than as a bug, so it is
        // asserted rather than left to the next reader of the shader text.
        E(fs.Contains("return texture(uFbColor, uv).rgb;") && !fs.Contains("CxLinear(texture(uFbColor"),
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
        // #339: every view the stage implements is REACHABLE. /alloy viz clamps to AlloyRenderer.LightVizMax,
        // silently -- a view above it paints the highest one instead, which looks like a working instrument
        // answering the wrong question. Read off the stage text so adding a view and forgetting the ceiling
        // fails here rather than in a screenshot.
        int highest = 0;
        for (int i = fs.IndexOf("uViz == "); i >= 0; i = fs.IndexOf("uViz == ", i + 1))
        {
            int j = i + 8, n = 0;
            while (j < fs.Length && fs[j] >= '0' && fs[j] <= '9') { n = n * 10 + (fs[j] - '0'); j++; }
            if (n > highest) highest = n;
        }
        E(highest <= AlloyRenderer.LightVizMax,
          $"the stage's highest view ({highest}) is above /alloy viz's ceiling ({AlloyRenderer.LightVizMax})");
        E(NoiseUnit != ColorUnit && NoiseUnit != DepthUnit && NoiseUnit != TextureSlots.Diffuse,
          "the noise texture has a unit of its own");

        log.Info(ok ? "FrameSnapshot.SelfCheck: passed" : "FrameSnapshot.SelfCheck: FAILED");
        return ok;
    }

    /// <summary>The whole fragment stage, composed by <see cref="ShaderStages"/> -- the one place any owned
    /// stage's composition is written down (ADR 0012).</summary>
    internal static string Fs => ShaderStages.Source(AlloyRenderer.ShdWaterFs);

    /// <summary>#339's vertex stage: the lit pass's clip expression and nothing else. Deliberately not the
    /// lit stage itself -- water is never skinned and never a wind-feature part -- and no longer a
    /// non-projecting one, because the tessellation pair that used to project is gone with the custom
    /// displacement. Vanilla water displaces in the FRAGMENT stage (fswater's parallax search over
    /// GetMainWaveOffset), so there is nothing for a tessellator to do.</summary>
    internal static string Vs => ShaderStages.Source(AlloyRenderer.ShdWaterVs);

    internal const string VsHead = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUV;
        layout(location = 2) in vec3 aNrm;

        // #119: the same clip expression, under the same qualifier, as every other Alloy vertex stage. Water
        // is not in the depth prepass, so nothing re-tests these depths -- but the stage no longer
        // displaces, so there is also no reason for it to differ.
        invariant gl_Position;

        """;

    internal const string VsMain = """
        uniform mat4 uVP;
        uniform int uDrawBase;
        out vec2 vUV;
        out vec3 vWorld;
        out vec3 vNrm;
        out float vDepth;
        flat out int vDraw;

        void main()
        {
            int id = CxSlot(uDrawBase + gl_DrawID);
            CxDraw d = uDraw[id];
            vWorld = (d.model * vec4(aPos, 1.0)).xyz;
            vNrm = mat3(d.model) * aNrm;
            vUV = vec2(d.rot.x * aUV.x - d.rot.y * aUV.y, d.rot.y * aUV.x + d.rot.x * aUV.y);
            vDraw = id;
            gl_Position = uVP * d.model * vec4(aPos, 1.0);
            vDepth = gl_Position.w;
        }
        """;

    /// <summary>#339's fragment stage, part one: the declarations, and the stock constants they carry.
    /// <para>Every number here is <c>inc_water.shd</c>'s own <c>#define</c> at the quality mode this client
    /// runs -- <c>settings.tml</c>'s <c>graphics.general.shader-quality = "High Quality"</c>, which the engine
    /// injects as <c>SHADER_QUALITY_MODE 2</c> (the string is <c>#define SHADER_QUALITY_MODE %d</c> in
    /// nwmain). Only that branch is ported: the two lower tiers are different shaders, and reproducing a look
    /// nobody on this client sees is the kind of ladder that rots.</para></summary>
    internal const string FsHead = """
        #version 460 core
        #define CX_FS 1   // #642: gl_FragCoord is in reach for the shadow lib's dither
        in vec2 vUV;
        in vec3 vWorld;
        in vec3 vNrm;
        in float vDepth;
        flat in int vDraw;

        """ + AlloyRenderer.DrawBufDecl + AlloyRenderer.FeatDecl + AlloyRenderer.KeyFogDecl + """
        uniform sampler2D uTex;        // the water material's diffuse -- its AVERAGE is the liquid's colour
        // #347: VanillaEnvFs aliases this as texUnit6. Declared, never bound, never sampled here -- nothing
        // spliced into this stage reads it since #350 (water takes the DISPLAY hook alone). Present so a
        // content file that names texUnit6 still compiles; unused, it is eliminated and its location
        // resolves to -1.
        uniform sampler2D uFlowTex;
        // #129: the one framebuffer copy, taken at PassId.FbCopy. Since #132 its colour is LINEAR -- the
        // copy is of PostChain's own target, every Alloy stage writes linear into it, and the single encode
        // happens later at the resolve. Depth is the raw window-space buffer, which CxSceneZ linearises
        // with this pass's own near/far rather than with an engine uniform.
        uniform sampler2D uFbColor;
        uniform sampler2D uFbDepth;
        // #339: the engine's own texUnitNoise -- the resman texture `solid_noise`, bound by the pass. The
        // wave field IS this texture's red channel at four scales, so a procedural stand-in is a different
        // sea rather than a slightly different one.
        uniform sampler2D uNoise;
        // #339: the SCENE's environment map (Scene+0x1e8), which stock water reflects at FULL strength --
        // see the composite for why. uEnvOk is 0 where the scene has none, and then the term is skipped
        // rather than multiplied by a black texel.
        uniform sampler2D uEnvMap;
        uniform int uEnvOk;
        uniform vec2 uNearFar;
        uniform mat4 uView;            // world -> view, this pass's own (see uVP's row-vector note)
        uniform mat4 uProj;
        uniform vec3 uAreaAmbient;
        uniform vec3 uAreaDiffuse;
        uniform vec3 uAreaDir;
        // uAreaWind (areaGlobalWind), uTimeOfDay (worldtimerTimeOfDay) and uEyePos come from FeatDecl and
        // KeyFogDecl, which every Alloy fragment stage carries (#341 moved the eye into the fog block, since
        // the fog target is a direction now). The clock is deliberately the GAME one: vswater.shd derives
        // every phase from it, so a module running time at 30x real gets vanilla's own fast sea, not a
        // corrected one.
        uniform int uViz;
        uniform int uCapture;
        uniform float uAlphaCut;
        out vec4 frag;

        // inc_water.shd's config block, verbatim.
        const float CX_ANIMATION_SPEED = 1.0;
        const int CX_WAVE_ITERATIONS = 3;
        const float CX_WATER_TRANSPARENCY_FACTOR = 0.25;
        const float CX_WATER_SPECULARITY = 0.02;
        const int CX_WATER_REFRACTION_ITERATIONS = 5;
        const float CX_WATER_REFRACTION_MAX_RANGE = 4.0;
        // fswater.shd's own displacement search, at SHADER_QUALITY_MODE 2.
        const float CX_VECTOR_LIMIT = 0.01;
        const int CX_DISPLACEMENT_ITERATIONS = 4;
        // inc_material.shd's environment LOD ramp, applied as a texture BIAS exactly as stock does
        // (texture2D's third argument is a bias, not a level).
        const float CX_ENV_LOD_MOD = 30.0, CX_ENV_LOD_OFF = -1.0, CX_ENV_LOD_MAX = 10.0;
        // inc_lighting.shd's fAngleMin, which every N.V here is floored at.
        const float CX_ANGLE_MIN_W = 0.01;
        // LIGHT_SUBSURFACE_SCATTERING_WRAP at this quality mode, and inc_lighting.shd:554's energy
        // preservation for it -- 2*(1+w)/(2+PI*w), which is 0.778 at w=1 and is what stops a wrapped
        // diffuse from adding light the surface never received.
        const float CX_SSS_WRAP = 1.0;
        const float CX_SSS_ENERGY = 2.0 * (1.0 + CX_SSS_WRAP) / (2.0 + 3.14159265 * CX_SSS_WRAP);

        """;

    /// <summary>#339's fragment stage, part two: stock <c>fswater.shd</c>, ported onto Alloy's plumbing.
    /// <para><b>What is vanilla here.</b> The wave field (<c>SetupWind</c>, <c>GetMainWaveOffset</c>,
    /// <c>GetWavesOffset</c>, <c>GetSurfaceNormal</c>), the parallax displacement search, the ray march
    /// (<c>GetRayIntersectCoords</c>/<c>GetReflectionCoords</c>), and the composite -- liquid body times the
    /// subsurface light, Beer-Lambert refraction through the copy, a screen-space reflection scaled by
    /// Schlick at water's own F0, and the lit surface standing in wherever the reflection ray misses. Line
    /// for line from <c>base_shaders.bif</c>, with the engine's names swapped for this pass's.</para>
    /// <para><b>What is Alloy.</b> Where stock calls <c>ComputeLighting</c> it gets the engine's forward
    /// light loop; here it gets the froxel walk, the sun cascades and the local cubes -- which is the whole
    /// point of drawing water in the owned pass at all. <c>SubsurfaceLight</c> is inc_lighting's wrapped
    /// diffuse accumulator (<c>inc_lighting.shd:275</c>): the sun gets the wrap term exactly, and the
    /// clustered lights get plain Lambert, because the cluster walk has no wrap parameter and adding one
    /// would fork a lib four other programs share.</para>
    /// <para><b>What is dropped, and it is visible.</b> <c>SetupLocalWind</c> and the
    /// <c>windPointSources</c> array -- the splash rings a creature or a spell leaves on the surface. The
    /// array is engine state with no Alloy feed; it needs its own RE and its own ticket.
    /// ponytail: no ripple sources. Add them when the engine's wind-source array has a reader (#340).</para></summary>
    internal const string FsMain = """

        float CxSqr(float x) { return x * x; }

        /// Window-space depth from the copy, linearised to a POSITIVE view-space distance -- stock's
        /// GetDepth, reconstructed from this pass's own near/far rather than from an engine uniform.
        float CxSceneZ(vec2 uv)
        {
            float d = texture(uFbDepth, uv).x * 2.0 - 1.0;
            float n = uNearFar.x, f = uNearFar.y;
            return (2.0 * n * f) / (f + n - d * (f - n));
        }

        /// The copy's colour. #132: linear already, so NO decode -- stock's ApplyColorSpace on this read is
        /// the one line of it that must not survive the port.
        vec3 CxFbColor(vec2 uv)
        {
            return texture(uFbColor, uv).rgb;
        }

        // ---- the wave field: fswater.shd, unchanged except for where its inputs come from ---------------

        float fTick;
        float fAreaWindForce;
        vec3 vAreaWindDirection;
        vec3 vAreaWindDirectionDistorted;
        float fMainWaveAmplitude;
        float fMainWavePeriod;
        float fMainWaveSpeed;
        float fMainWaveNoiseModifier;
        float fMainWaveNoiseFrequency;
        float fMainWaveNoiseDisplacement;
        float fArealVariation;
        float fSurfaceNdotV;
        vec3 vSurfaceNormalWorld;

        /// vswater.shd's SetupVertexWind. Stock runs it per VERTEX and interpolates the results; every one
        /// of them is a per-frame constant, so computing it here is the same value and one varying fewer.
        void CxSetupVertexWind()
        {
            fTick = float(uTimeOfDay) * (0.001 * CX_ANIMATION_SPEED);
            fAreaWindForce = dot(uAreaWind, uAreaWind);
            vAreaWindDirection = fAreaWindForce > 0.0 ? uAreaWind * inversesqrt(fAreaWindForce)
                                                      : vec3(0.707, 0.707, 0.0);
            fAreaWindForce += 0.05;
        }

        void CxSetupWind(float fFragmentDepthOffset)
        {
            fMainWaveAmplitude = 0.4 * fAreaWindForce;
            fMainWavePeriod = 2.0 / (fAreaWindForce + 1.0);
            fMainWaveSpeed = 0.6 + 0.05 / fAreaWindForce;
            fMainWaveNoiseModifier = 0.1;
            fMainWaveNoiseFrequency = 0.1;
            fMainWaveNoiseDisplacement = 0.25 + 0.5 * fAreaWindForce;

            fArealVariation = texture(uNoise, 0.003 * vWorld.xy).r * 2.0 - 1.0;
            vAreaWindDirectionDistorted = vAreaWindDirection;
            vAreaWindDirectionDistorted.xy += vec2(vAreaWindDirection.x, -vAreaWindDirection.y)
                                            * fArealVariation * 0.05;
            fArealVariation *= fArealVariation;
            fMainWaveAmplitude *= CxSqr(1.0 - fArealVariation);
            // Reduce at vertical.
            fMainWaveAmplitude *= max(CxSqr(vSurfaceNormalWorld.z) * 101.0 - 100.0, 0.0);

            float fAmplitudeNoise = texture(uNoise, fMainWavePeriod * 0.01 * vWorld.xy
                                                  - 0.005 * fTick * fMainWaveSpeed * vAreaWindDirection.xy).r;
            fMainWaveAmplitude *= fAmplitudeNoise;
            // WATER_DYNAMIC_DEPTH 0: the amplitude is faded out where the surface is nearly edge-on or the
            // water is thin, because nothing displaces the DEPTH this fragment writes.
            fMainWaveAmplitude *= min(1.0, fSurfaceNdotV * fFragmentDepthOffset);
        }

        float CxMainWaveOffset(inout vec3 vPos)
        {
            // ponytail: stock calls SetupLocalWind(vPos) here for the windPointSources splash rings. No feed
            // for that array yet (#340); the offset it would add is zero.
            float fWaveOffset = 0.0;

            float fMainWaveNoise = 1.0 - texture(uNoise, fMainWavePeriod * 0.04 * vPos.xy
                                                       - 0.01 * fTick * fMainWaveSpeed * vAreaWindDirection.xy).r;
            vPos.xy -= vAreaWindDirection.xy * (fMainWaveNoise * fMainWaveAmplitude * 2.0 - 5.0 * fArealVariation);
            float fNoise = texture(uNoise, vPos.xy * fMainWaveNoiseFrequency).r * fMainWaveNoiseModifier;

            float fWave = abs(mod(fTick * fMainWaveSpeed
                                  - dot(vPos.xy - fNoise * vAreaWindDirectionDistorted.xy * fMainWaveNoiseDisplacement,
                                        vAreaWindDirectionDistorted.xy) * fMainWavePeriod,
                                  2.0) - 1.0);
            fWave *= fWave;
            fWave = 2.0 * fWave - CxSqr(fWave);
            fWaveOffset += fMainWaveAmplitude * CxSqr(fMainWaveNoise) * (fWave - 0.5);
            return fWaveOffset;
        }

        float CxWavesOffset(vec3 vPos)
        {
            const vec2 vRotate = vec2(-0.5735764363, 0.819152044);
            const mat2 M = mat2(vRotate, vec2(-vRotate.y, vRotate.x));
            vec2 vDirection = -vAreaWindDirection.xy;

            float fWave = CxMainWaveOffset(vPos);

            float fNoiseFrequency = 0.25;
            float fAmplitude = 0.005;
            float fSpeed = 0.14;

            vPos.xy += vPos.z * 0.1;
            for (int i = 0; i < CX_WAVE_ITERATIONS; i++)
            {
                fWave -= fAmplitude
                       * abs(texture(uNoise, vPos.xy * fNoiseFrequency + vDirection.xy * fTick * fSpeed).r * 2.0 - 1.0)
                       * clamp(1.0 - 5.0 * dot(vDirection, vSurfaceNormalWorld.xy), 0.0, 1.0);
                vDirection = M * vDirection;
                fAmplitude *= 0.75;
                fNoiseFrequency *= 1.7;
            }
            return fWave;
        }

        /// The surface normal, in WORLD space -- stock builds it here and converts to view afterwards; the
        /// Alloy lighting libs want world, so the conversion is the one that goes away.
        vec3 CxWaterNormal(vec3 vPos)
        {
            vec3 vTangent = vec3(1.0, 0.0, 0.0);
            vec3 vBitangent = vec3(0.0, 1.0, 0.0);
            // If we are on a tilted plane, translate the normal into this plane.
            if (abs(vSurfaceNormalWorld.z) < 0.99)
            {
                vTangent = normalize(sign(vSurfaceNormalWorld.z) * vec3(-vSurfaceNormalWorld.y, vSurfaceNormalWorld.x, 0.0));
                vBitangent = cross(vSurfaceNormalWorld, vTangent);
            }

            float fOffset = 0.05;
            float w0 = CxWavesOffset(vPos);
            float wx = CxWavesOffset(vPos + vTangent * fOffset);
            float wy = CxWavesOffset(vPos + vBitangent * fOffset);

            vec3 vNormal = cross(vTangent * fOffset + vSurfaceNormalWorld * (wx - w0),
                                 vBitangent * fOffset + vSurfaceNormalWorld * (wy - w0));
            return normalize(vNormal);
        }

        // ---- the marches: inc_common's GetRayIntersectCoords, on the copy ------------------------------

        /// Stock's march, verbatim in method: step along the view-space ray, read the copy's depth at the
        /// projected point, and feed the signed depth difference back as the next step so the iteration
        /// converges on the crossing. Returns (uv, hit view distance, relative error).
        vec4 CxRayIntersect(vec3 vPosViewOrigin, vec3 vRay, float fDepthOffset, float fMaxDistance, int nIterations)
        {
            vec2 vRayIntersectionCoords = vec2(-1.0);

            float fAngleFactor = mix(1.0, abs(vRay.z), 0.9);
            fMaxDistance *= fAngleFactor;
            vRay /= fAngleFactor;

            float fDistanceOffsetMax = fMaxDistance / float(nIterations);
            float fDepthDeltaMin = 4096.0;
            float fRayIntersectionDepth = fMaxDistance + vPosViewOrigin.z;

            vec3 vRayMaxProj = mat3(uProj) * (vPosViewOrigin + fMaxDistance * vRay);
            vec2 vRayCoordsMax = 0.5 * (vRayMaxProj.xy / vRayMaxProj.z) + 0.5;
            vec3 vPosOriginProj = mat3(uProj) * vPosViewOrigin;
            vec2 vPosProjOriginCoords = 0.5 * (vPosOriginProj.xy / vPosOriginProj.z) + 0.5;

            float fInterpolationDenominatorInv =
                1.0 / (1.0 / (vPosViewOrigin.z + vRay.z * fMaxDistance) - 1.0 / vPosViewOrigin.z);
            float fInterpolationOffset = (1.0 / vPosViewOrigin.z) * fInterpolationDenominatorInv;

            float fDepthDelta = clamp(fDepthOffset, 0.0, fDistanceOffsetMax);
            float fDistance = 0.0;
            bool bFoundIntersection = false;

            for (int n = 1; n <= nIterations; n++)
            {
                fDistance += clamp(fDepthDelta, -fDistanceOffsetMax, fDistanceOffsetMax);
                fDistance = max(0.01, fDistance);
                vec3 vRayThis = fDistance * vRay;

                vec2 vRayCoordsThis = mix(vPosProjOriginCoords, vRayCoordsMax,
                                          (1.0 / (vPosViewOrigin.z + vRayThis.z)) * fInterpolationDenominatorInv
                                          - fInterpolationOffset);
                if (vRayCoordsThis.x > 1.0 || vRayCoordsThis.x < 0.0
                    || vRayCoordsThis.y > 1.0 || vRayCoordsThis.y < 0.0) break;

                float fSampleDepth = CxSceneZ(vRayCoordsThis);
                fDepthDelta = fSampleDepth + (vPosViewOrigin.z + vRayThis.z);

                if (abs(fDepthDelta) / (fDistance + 1.0) <= abs(fDepthDeltaMin))
                {
                    vRayIntersectionCoords = vRayCoordsThis;
                    fDepthDeltaMin = fDepthDelta / (fDistance + 1.0);
                    fRayIntersectionDepth = fSampleDepth;
                }
                if (fDepthDelta < 0.0) bFoundIntersection = true;
                if (bFoundIntersection) fDistanceOffsetMax *= 0.5;
            }

            return vec4(vRayIntersectionCoords.x, vRayIntersectionCoords.y, -fRayIntersectionDepth, fDepthDeltaMin);
        }

        vec4 CxReflectionCoords(vec3 vPosViewThis, vec3 vReflect, float fDepthOffset)
        {
            const int nIterations = 10;
            float fDepthMax = mix(mix(uNearFar.y, 0.0, 0.5 + 0.5 * vReflect.z), 100.0, 0.1);
            float fDepthDeltaMax = fDepthMax + vPosViewThis.z;
            float fMaxDistance = clamp(abs(fDepthDeltaMax), 1.0, 30.0);
            vec4 c = CxRayIntersect(vPosViewThis, vReflect, fDepthOffset, fMaxDistance, nIterations);
            return vec4(c.xyz, clamp(abs(c.w / 0.2), 0.0, 1.0));
        }

        /// inc_common's GetNormalVector: the VIEW-space normal of whatever the copy's depth holds at these
        /// coordinates, used only to reject a reflection off a surface facing away from the ray. Central
        /// differences here rather than stock's nearest-neighbour pick -- the projection helpers its version
        /// leans on are a bigger port than the term is worth.
        vec3 CxCopyNormal(vec2 uv)
        {
            vec2 px = uPixel.xy;
            // View position from a depth sample: the ray through this pixel, scaled to the sampled distance.
            vec3 dir = vec3((2.0 * uv.x - 1.0) / uProj[0][0], (2.0 * uv.y - 1.0) / uProj[1][1], -1.0);
            vec3 p  = dir * CxSceneZ(uv);
            vec3 dx = vec3((2.0 * (uv.x + px.x) - 1.0) / uProj[0][0], dir.y, -1.0) * CxSceneZ(uv + vec2(px.x, 0.0)) - p;
            vec3 dy = vec3(dir.x, (2.0 * (uv.y + px.y) - 1.0) / uProj[1][1], -1.0) * CxSceneZ(uv + vec2(0.0, px.y)) - p;
            return normalize(cross(dx, dy));
        }

        /// inc_common.shd's CalculateEnvironmentTexCoords: the sphere-map lookup stock reflects the
        /// environment with, from the VIEW-space fragment normal and the view position. Verbatim -- the
        /// half-offset and the 0.25 in the y term are the engine's own framing of its env textures, and a
        /// "tidier" projection here is a different reflection.
        vec2 CxEnvCoords(vec3 nView, vec3 posView)
        {
            vec2 vEnvReflect = nView.xy / (1.0 + abs(nView.z));
            vEnvReflect += 0.5 * (posView.xy / (-posView.z + 0.2));
            return 0.5 * vEnvReflect + vec2(0.5, 0.25);
        }

        /// The fog AMOUNT alone -- stock scales the refraction's transparency by (1 - GetFogAlpha), so the
        /// riverbed stops showing through where the fog has already swallowed it. Same expression CxFog
        /// uses; only the mix is different.
        float CxFogAlpha(float depth)
        {
            return uFogParams.x == 0.0 ? 0.0 : clamp((depth - uFogParams.y) * uFogParams.w, 0.0, 1.0);
        }

        void main()
        {
            CxDraw d = uDraw[vDraw];
            // /alloy viz 4 is the coverage view: cyan is Alloy lit and magenta owned-fullbright, so
            // water takes BLUE.
            if (uViz == 4) { frag = vec4(0.0, 0.0, 1.0, 1.0); return; }

            vSurfaceNormalWorld = normalize(vNrm);
            CxSetupVertexWind();

            vec3 V = normalize(uEyePos - vWorld);
            vec2 vScreenCoords = gl_FragCoord.xy * uPixel.xy;
            vec3 vPosView = (uView * vec4(vWorld, 1.0)).xyz;
            vec3 vViewToSurface_n = normalize(vPosView);

            // The liquid's own colour is the diffuse's AVERAGE, not a texel -- stock reads it at LOD 20 for
            // exactly this reason: a water material's texture is a ripple pattern, and sampling it per
            // fragment paints that pattern onto a surface whose shape comes from the wave field instead.
            vec4 baseTexel = textureLod(uTex, vec2(0.5), 20.0);
            vec4 WaterBaseColor = vec4(CxLinear(baseTexel.rgb), baseTexel.a);

            float fScreenFBDepth = CxSceneZ(vScreenCoords);
            fSurfaceNdotV = max(dot(vSurfaceNormalWorld, V), CX_ANGLE_MIN_W);
            CxSetupWind(fScreenFBDepth + vPosView.z);

            // The displacement search: where on the (raised) wave surface this pixel's ray actually lands.
            // Stock's iteration, unchanged -- it is what turns a fragment-side wave field into something with
            // parallax instead of a texture painted flat on the plane.
            vec3 vWaterSurfaceCoords = vWorld;
            vec3 vCameraToWorld_n = normalize(vWorld - uEyePos);
            float fDisplaceFactor = 1.0 / (vCameraToWorld_n.z > 0.0 ? vCameraToWorld_n.z + CX_VECTOR_LIMIT
                                                                    : vCameraToWorld_n.z - CX_VECTOR_LIMIT);
            vec2 vWorldOffset = vCameraToWorld_n.xy * fDisplaceFactor;

            float fDistance = CxMainWaveOffset(vWaterSurfaceCoords);
            float fSegmentSize = 1.0;
            vWaterSurfaceCoords.xy = vWorld.xy + vWorldOffset * fDistance;
            for (int n = 1; n < CX_DISPLACEMENT_ITERATIONS; n++)
            {
                float fMainWaveOffset = CxMainWaveOffset(vWaterSurfaceCoords);
                fSegmentSize *= 0.5;
                fDistance += (fMainWaveOffset - fDistance) * fSegmentSize;
                vWaterSurfaceCoords.xy = vWorld.xy + vWorldOffset * fDistance;
            }

            vec3 vPosViewThis = (uView * vec4(vWorld + fDisplaceFactor * vCameraToWorld_n * fDistance, 1.0)).xyz;
            vViewToSurface_n = normalize(vPosViewThis);
            float fDepthOffset = fScreenFBDepth + vPosViewThis.z;

            vec3 N = CxWaterNormal(vWaterSurfaceCoords);
            // rain rings the surface too (weather.splash), on a level one; the same train the puddles carry
            if (uSplash > 0.0 && vSurfaceNormalWorld.z > 0.9)
                N = normalize(N + vec3(CxWeatherImpact(vWaterSurfaceCoords.xy, uSplash) * 0.3, 0.0));
            vec3 vFragmentNormal = normalize(mat3(uView) * N);

            // ---- lighting: stock's ComputeLighting, answered by the froxel walk ----------------------
            // fRoughness = 0.00025 + 0.00025*-vPosView.z under SPECULAR_DISTRIBUTION_MODEL 1, and
            // fSpecularity = 1.0 -- stock sets it to one on purpose and factors water's real F0 in at the
            // composite, through the Fresnel term. Doing it in both places is the double-count to avoid.
            float fRoughness = 0.00025 + 0.00025 * -vPosView.z;
            float NdotV = max(dot(N, V), CX_ANGLE_MIN_W);
            float sunRaw = clamp(dot(N, uAreaDir), 0.0, 1.0);
            float sunVis = CxSunShadow(vWorld, N, sunRaw, sunRaw);

            vec3 pointSpec;
            vec3 pointDiffuse = CxClusteredLight(vWorld, N, V, vDepth, 0.0, fRoughness, 1.0, pointSpec);
            // FragmentColor in stock: lighting on a BLACK albedo, so what survives is the specular alone.
            vec3 ShadedColor = (uAreaDiffuse * (sunRaw * sunVis) * CxSpecLobe(N, V, uAreaDir, fRoughness, 1.0)
                                + pointSpec) * CxSpecBase(NdotV, fRoughness);
            // ...plus the ENVIRONMENT term, which is the largest single thing the first cut of this port was
            // missing (inc_lighting.shd:454-513). Stock adds `(AmbientLight + DiffuseLight) * envTexel *
            // fEnvironmentSpecularity` into the specular light, and for water that last factor is exactly
            // ONE: fSpecularity is 1.0 on this path, and SPECULAR_FRESNEL 1 makes it
            // mix(GetFresnelSpecularity(1.0, NdotV), 1.0, rough), every term of which is 1. So the scene's
            // environment map arrives at full strength, sampled through the WAVE-perturbed normal -- which is
            // what makes a ripple legible at all. Without it the surface is body colour plus a 2%-Fresnel
            // reflection, i.e. flat paint, however correct the wave field underneath is.
            vec3 DiffuseLight = uAreaDiffuse * (sunRaw * sunVis) + pointDiffuse;
            if (uEnvOk != 0)
            {
                float envLod = clamp(fRoughness * CX_ENV_LOD_MOD + CX_ENV_LOD_OFF, 0.0, CX_ENV_LOD_MAX);
                vec3 envTexel = CxLinear(texture(uEnvMap, CxEnvCoords(vFragmentNormal, vPosViewThis), envLod).rgb);
                ShadedColor += (uAreaAmbient + DiffuseLight) * envTexel;
            }
            // inc_lighting's SubsurfaceLight (its assembly at :545-555): the wrapped per-light sum, plus
            // the area ambient OUTSIDE that sum, all scaled by the wrap's energy preservation. The sun gets
            // the wrap exactly (:275 with LIGHT_SUBSURFACE_SCATTERING_WRAP 1.0); the clustered lights get
            // plain Lambert, because the cluster walk has no wrap parameter and four other programs share it.
            // ponytail: no materialFrontDiffuse tint on the sum, which stock applies. The record carries that
            // colour in d.dif -- but only on a LIT batch, and an unlit water batch would read a stale slot of
            // any magnitude. Stock water materials are white there; tint it from d.dif if one ever is not.
            float sunWrap = (sunRaw + CX_SSS_WRAP) / (1.0 + CX_SSS_WRAP);
            vec3 SubsurfaceLight = (uAreaDiffuse * (sunWrap * sunVis) + pointDiffuse + uAreaAmbient)
                                 * CX_SSS_ENERGY;

            // ---- refraction: the floor, seen through the surface -------------------------------------
            vec3 vRefraction = refract(vViewToSurface_n, vFragmentNormal, 1.0 / 1.33333);
            // Only the DELTA against the flat surface, which is stock's own note: it removes refraction's
            // flattening and keeps the distortion.
            vec3 vRefractSurface = refract(vViewToSurface_n, normalize(mat3(uView) * vSurfaceNormalWorld), 1.0 / 1.33333);
            vRefraction = vViewToSurface_n + (vRefraction - vRefractSurface);

            vec4 vRefractionCoords = CxRayIntersect(vPosViewThis, vRefraction, fDepthOffset,
                                                      CX_WATER_REFRACTION_MAX_RANGE,
                                                      CX_WATER_REFRACTION_ITERATIONS);
            float fRefractionModifier = clamp(1.0 + 10.0 * vRefractionCoords.w, 0.0, 1.0);
            vec3 ColorRefraction = CxFbColor(mix(vScreenCoords, vRefractionCoords.xy, fRefractionModifier));

            float fRefractionTransparency = WaterBaseColor.a < 1.0
                ? exp2(-mix(fDepthOffset,
                            clamp(vPosViewThis.z - vRefractionCoords.z, 0.0, CX_WATER_REFRACTION_MAX_RANGE),
                            fRefractionModifier)
                       * CX_WATER_TRANSPARENCY_FACTOR * WaterBaseColor.a / (1.0 - WaterBaseColor.a))
                : 0.0;
            ColorRefraction *= pow(fRefractionTransparency, fSurfaceNdotV);
            fRefractionTransparency *= 1.0 - CxFogAlpha(vDepth);

            // ---- reflection: the same march, out to the horizon --------------------------------------
            vec3 ColorReflection;
            vec3 ColorLiquid = WaterBaseColor.rgb * SubsurfaceLight;
            vec3 vReflect = reflect(vViewToSurface_n, vFragmentNormal);
            vec4 vReflectedCoords = CxReflectionCoords(vPosViewThis, vReflect, fDepthOffset);
            float fReflectionMod = clamp(1.0 - vReflectedCoords.w, 0.0, 1.0);
            if (vReflectedCoords.x >= 0.0)
            {
                ColorReflection = CxFbColor(clamp(vReflectedCoords.xy, vec2(0.001), vec2(0.999)));
                vec3 vReflectedNormal = CxCopyNormal(clamp(vReflectedCoords.xy, vec2(0.001), vec2(0.999)));
                fReflectionMod *= 1.0 - CxFogAlpha(-vReflectedCoords.z);
                ColorReflection = mix(vec3(0.0), ColorReflection,
                                      clamp(0.1 - 4.0 * dot(vReflectedNormal, vReflect), 0.0, 1.0));
                // The miss and the reject both land on the SHADED surface, which is vanilla's flat-horizon
                // look. #129's probe fallback is exactly the custom shading this ticket removes.
                ColorReflection = mix(ShadedColor, ColorReflection, fReflectionMod);
            }
            else
            {
                ColorReflection = ShadedColor;
            }

            // ---- composite ---------------------------------------------------------------------------
            ColorLiquid = mix(ColorLiquid, ColorRefraction, fRefractionTransparency);
            float fFresnelSpecularity = mix(CX_WATER_SPECULARITY, 1.0, CxFresnel(NdotV));
            vec3 lit = ColorLiquid + ColorReflection * fFresnelSpecularity;

            // #129's term views, kept: water composites four things that can each be wrong on their own and
            // look identical in the sum. 9 = what is seen THROUGH the surface, 10 = the reflection,
            // 11 = fresnel as grey, 12 = the copy as sampled, 13 = the copy's linearised depth over 100m,
            // 20 = the wave field's normal.
            if (uViz == 9)  { frag = vec4(ColorLiquid, 1.0); return; }
            if (uViz == 10) { frag = vec4(ColorReflection, 1.0); return; }
            if (uViz == 11) { frag = vec4(vec3(fFresnelSpecularity), 1.0); return; }
            if (uViz == 12) { frag = vec4(CxFbColor(vScreenCoords), 1.0); return; }
            if (uViz == 13) { frag = vec4(vec3(clamp(fScreenFBDepth / 100.0, 0.0, 1.0)), 1.0); return; }
            if (uViz == 20) { frag = vec4(N * 0.5 + 0.5, 1.0); return; }
            // 28 = the environment term alone -- the scene env map through the wave normal, times the light
            // on the water. Black here with a live scene means uEnvOk is 0, i.e. Scene+0x1e8 held nothing.
            if (uViz == 28)
            {
                frag = vec4(uEnvOk != 0
                            ? CxLinear(texture(uEnvMap, CxEnvCoords(vFragmentNormal, vPosViewThis),
                                                 clamp(fRoughness * CX_ENV_LOD_MOD + CX_ENV_LOD_OFF,
                                                       0.0, CX_ENV_LOD_MAX)).rgb)
                            : vec3(0.0), 1.0);
                return;
            }
            // 21 = the noise texture, sampled the way the wave field samples it. A flat colour means it is
            // not bound (every read the same) or not REPEATing (one edge texel over the whole body) -- and
            // either one drives the wave amplitude to exactly zero, which reads as a mirror-flat sea with
            // working reflections. That is the shape of the first cut's bug, so it gets its own view.
            if (uViz == 21) { frag = vec4(vec3(texture(uNoise, vWorld.xy * 0.1).r), 1.0); return; }
            // 22 = the three terms that can each zero the sea on their own, in one view, because a flat
            // surface looks identical whichever it was. RED = the part's own normal .z, which the amplitude
            // is gated on through max(z^2*101-100, 0): anything below 0.995 kills every wave whatever the
            // wind is. GREEN = the wave amplitude over 2 units. BLUE = the water's thickness at this pixel
            // over 10 units, which the amplitude is faded by (stock's WATER_DYNAMIC_DEPTH 0 branch) -- black
            // blue means the depth copy says the floor is AT the surface and the fade takes the sea out.
            if (uViz == 22)
            {
                frag = vec4(clamp(abs(vSurfaceNormalWorld.z), 0.0, 1.0),
                            clamp(fMainWaveAmplitude * 0.5, 0.0, 1.0),
                            clamp((fScreenFBDepth + vPosView.z) * 0.1, 0.0, 1.0), 1.0);
                return;
            }
            // 23 = the wave OFFSET the parallax search converged on, grey around 0.5. Structure here with a
            // black green channel in 22 would mean the amplitude survived after all and the shading is what
            // is hiding the sea; flat grey means the field itself is dead.
            if (uViz == 23) { frag = vec4(vec3(clamp(fDistance * 0.5 + 0.5, 0.0, 1.0)), 1.0); return; }
            // 24/25 = the two halves of the wave FUNCTION, once 22 has shown the amplitude is healthy.
            // 24 is the phase the mod runs on -- banding at a few metres is correct, a flat field means the
            // fTick term has swamped the position term and float precision has eaten the variation. 25 is
            // the advected noise the amplitude is shaped by, whose UV carries the same fTick.
            if (uViz == 24)
            {
                vec3 p = vWorld;
                float fNoise = texture(uNoise, p.xy * fMainWaveNoiseFrequency).r * fMainWaveNoiseModifier;
                float phase = fTick * fMainWaveSpeed
                            - dot(p.xy - fNoise * vAreaWindDirectionDistorted.xy * fMainWaveNoiseDisplacement,
                                  vAreaWindDirectionDistorted.xy) * fMainWavePeriod;
                frag = vec4(vec3(mod(phase, 2.0) * 0.5), 1.0); return;
            }
            // 26 = the phase's INPUTS, after 24 showed the phase itself varying by a tenth of a cycle
            // where it should sweep several. RED and GREEN are the fragment's world x and y in 10-unit
            // bands (one NWN tile): a flat field means vWorld barely moves across a surface that plainly
            // spans tiles, and every world-space term in the shader is then meaningless. BLUE is the
            // spatial half of the phase alone, with the clock left out, so a banded red/green over a flat
            // blue puts the fault in fMainWavePeriod -- i.e. in the wind force the SHADER sees, whatever
            // the census printed on the CPU.
            if (uViz == 26)
            {
                frag = vec4(fract(vWorld.x * 0.1), fract(vWorld.y * 0.1),
                            fract(dot(vWorld.xy, vAreaWindDirectionDistorted.xy) * fMainWavePeriod * 0.5),
                            1.0);
                return;
            }
            // 27 = the two scalars the phase is built from, as a readable ramp rather than a number:
            // RED is fAreaWindForce over 8 (4,05 reads as half), GREEN is fMainWavePeriod over 1 (0,396
            // reads as a dark 0,4). Flat is expected -- what matters is WHICH value, and a shader that
            // disagrees with the census line is a uniform that never arrived.
            if (uViz == 27)
            {
                frag = vec4(clamp(fAreaWindForce * 0.125, 0.0, 1.0),
                            clamp(fMainWavePeriod, 0.0, 1.0),
                            clamp(length(vAreaWindDirectionDistorted.xy), 0.0, 1.0), 1.0);
                return;
            }
            if (uViz == 25)
            {
                frag = vec4(vec3(1.0 - texture(uNoise, fMainWavePeriod * 0.04 * vWorld.xy
                                             - 0.01 * fTick * fMainWaveSpeed * vAreaWindDirection.xy).r), 1.0);
                return;
            }
            if (uCapture != 0) { frag = vec4(lit, 1.0); return; }

            float kh = CxKeyhole(d.rot.z, vWorld, vDepth);
            if (kh < 0.0) discard;
            // Stock's own `FragmentColor.a = 1.0`: everything behind the surface has already been
            // composited in by the refraction term, so letting the framebuffer show through counts the
            // floor twice. rot.w is below 1 only on a fading tile.
            frag = vec4(CxDisplayContent(CxFog(lit * kh, vWorld, vDepth)), d.rot.w);
        }
        """;
}
