using System;
using System.Globalization;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #139: the Alloy colour pass's HDR target and the post chain that resolves it -- bloom, tonemap, encode.
/// <para>What it fixes: every Alloy lit fragment is computed in linear and encoded to display space in the
/// shader, then written into the engine's 8-bit colour attachment. A carried torch drives that value far
/// past 1.0, and 8 bits flatten everything above 1.0 to the same white -- which is the "all detail is
/// killed" blowout, not a lighting error. Rendering the Alloy passes into an RGBA16F attachment instead
/// keeps the overbright range, and one full-screen pass at the end of the colour bracket is where it gets
/// spent: as bloom (which is what makes a bright light read as BRIGHT rather than as clipped) and as a
/// tonemap shoulder.</para>
/// <para>#214: the target owns its depth attachment too. It has to -- the engine only builds a world
/// framebuffer from inside <c>RenderSinglePass</c>, so after the M4 cutover there is no attachment left to
/// borrow. The owned texture is allocated to the internal format the engine's attachment reports (probed off
/// the OBJECT, never off the attachment's bit widths), which is what makes the one thing sharing used to give
/// for free legal: <see cref="ResolveDepth"/> blits our depth back into the engine's attachment at the
/// resolve, so the engine's own draws after the bracket still depth-test against the world the Alloy pass
/// wrote. Colour and depth are both ours now, and both are composited back.</para>
/// <para>#132 flipped what the target holds: LINEAR values, unclamped. Every Alloy stage now writes linear
/// and this resolve does the one encode for the whole frame -- one conversion per screen pixel instead of
/// one per fragment, cheaper than the per-shader encode it replaces, and the only order in which blended
/// compositing is correct. The encode is gated on the engine's OWN gamma parameter
/// (<c>GetGlobalShaderParami(0)</c>, the <c>GAMMA_CORRECTION</c> the shader preamble is built from), which
/// the per-shader encodes never were. Fog moved across the encode with it: #124 welded the engine's order
/// (keyhole linear, encode, fog display) and this ticket knowingly unwelds it, so a foggy frame is a
/// deliberate look change.</para>
/// <para>Refusal is now the whole Alloy frame's business, not this class's alone: a linear Alloy frame with
/// no resolve is wrong by a gamma, and there is no display-space variant of the Alloy stages left to fall
/// back to. <see cref="Begin"/> is therefore asked BEFORE anything is staged, and a false answer stands the
/// pass down to vanilla outright. It refuses when the engine target cannot be shared: a default framebuffer
/// (its depth cannot be attached to an FBO at all), an offset viewport, or -- since #214, and only once
/// the driver has said so -- an attachment the owned depth cannot be handed back to.</para>
/// </summary>
internal static unsafe class PostChain
{
    /// <summary>Bloom chain depth ceiling. Six halvings from 1080p bottoms out at 8px, which is already
    /// wider than any bloom anyone wants; the real chain is the smaller of this and what the size allows.</summary>
    private const int MaxLevels = 6;
    /// <summary>Texture units the resolve samples the HDR colour and the bloom chain on -- clear of every
    /// unit the Alloy lit stage binds, because the resolve runs in the same bracket and leaving a unit shared
    /// with a live sampler of a different type is INVALID_OPERATION. See <see cref="TextureSlots"/>.</summary>
    private const uint HdrUnit = TextureSlots.PostHdr, BloomUnit = TextureSlots.PostBloom;

    /// <summary>Tonemap operators. <see cref="Shoulder"/> is the identity below its knee; <see cref="Aces"/>
    /// (the default since the #375 follow-ups, at <see cref="_acesMix"/> against the shoulder) moves the
    /// midtones, which is Alloy's own look decision -- under the M4 cutover nothing the engine draws shares
    /// the resolved world any more, so there is no engine-drawn geometry left to disagree with.</summary>
    private const int Off = 0, Shoulder = 1, Aces = 2;

    /// <summary>The Alloy colour passes of THIS pass are drawing into the HDR target. Since #132 the owned
    /// frame does not run at all when this is false -- there is no un-resolved variant of it.</summary>
    internal static bool Armed { get; private set; }

    /// <summary>Resolves this session -- the [alloy] period line prints it. A number that stays 0 while the
    /// world renders is the "HDR never engaged, you are seeing the fallback" case, and the arm-time log line
    /// says which target refused.</summary>
    internal static int Resolves { get; private set; }

    private static bool _on = true;
    /// <summary>#215: the <c>hdr</c> boot decision, published because it is a REFUSAL the cutover has to know
    /// about at arm time. <c>hdr=0</c> makes <see cref="Precheck"/> refuse every pass forever, and under the
    /// shim there is no engine frame behind that refusal -- the world would be bare clear colour for the
    /// session. Off means the shim must not take the frame.</summary>
    internal static bool On => _on && !_down.Failed;

    /// <summary>#216: the two halves of <see cref="On"/>, split because the frame verdict has to report them
    /// differently -- <c>hdr=0</c> is a CHOICE and a latched stand-down is an INCAPACITY (ADR 0010).</summary>
    internal static bool Configured => _on;

    /// <summary>...and the latched half.</summary>
    internal static bool Failed => _down.Failed;
    private static readonly StandDown _down = new("PostChain");
    private static ExtenderLog? _log;
    private static uint _fbo, _hdrTex, _bloomTex, _depthTex;
    /// <summary>#391: a 1x depth at DISPLAY size for the hand-back into a multisampled target. GL 4.6 18.3.2
    /// makes a blit whose rectangles differ INVALID_OPERATION when either framebuffer is multisampled, so
    /// under a render scale the resolve into an MSAA engine target goes in two: scale into this, then 1:1.</summary>
    private static uint _scratchFbo, _scratchTex;
    private static int _scratchW, _scratchH;
    private static uint _scratchFormat;
    private static uint _depFormat;
    /// <summary>The owned depth format whose hand-back the driver refused -- keyed rather than a global
    /// latch, so a device reset that changes the default framebuffer's depth format gets a fresh attempt.</summary>
    private static uint _handbackBad;
    private static int _depW, _depH;
    /// <summary>The framebuffer the Alloy colour passes draw into, for the one pass that has to leave it and
    /// come back: #222's sky scratch. Read-only on purpose -- binding it is <see cref="Begin"/>'s job.</summary>
    internal static uint Fbo => _fbo;
    /// <summary>#214: the depth attachment the Alloy colour passes write, owned rather than the engine's.
    /// Published as <see cref="DepthResource"/> by the prepass; sampling it while it is attached is the
    /// feedback loop <see cref="SceneDepth"/> exists to avoid, so consumers take a copy.</summary>
    internal static uint DepthTex => _depthTex;
    /// <summary>The HDR colour target, for the sky decode that reads it back through a texture barrier.</summary>
    internal static uint HdrTex => _hdrTex;
    /// <summary>The empty VAO the full-screen triangle draws under.</summary>
    internal static uint FullscreenVao => _vao;
    private static int _w, _h, _levels;
    private static uint _progDown, _progUp, _progComposite;
    private static uint _vao;
    private static bool _progFailed;
    private static bool _bloomOk;

    /// <summary>#132: the engine's own gamma gate, <c>GetGlobalShaderParami(0)</c> -- the value that becomes
    /// <c>#define GAMMA_CORRECTION</c> in every engine shader and makes <c>ApplyColorSpace</c> /
    /// <c>RevertColorSpace</c> the identity when it is 0. Asked once per pass in <see cref="Precheck"/>: it
    /// is a settings-driven global and reading it is one call. Unresolved means "on", which is what this
    /// client measures (#102) and what every shipped .shd is written against.</summary>
    private static delegate* unmanaged<uint, int> _gammaParam;
    private const uint GammaParam = 0;

    // Dials. Tuned live through /post; the values here ARE the boot values (ADR 0013 bucket 1).
    private static float _bloom = 0.06f, _threshold = 1f, _knee = 0.5f, _tmKnee = 0.8f;
    /// <summary>Scene-referred exposure: one linear gain on the resolved frame (bloom included) BEFORE the
    /// tonemap, so blacks stay put and the shoulder takes what it pushes past 1. 1.0 is the identity. This
    /// is Alloy's own brightness decision rather than a port of anyone's chain: the stock SoW frame gets
    /// its lift from two display-space ACES passes and a grade, which is not a scene-referred model at all.
    /// It is also what makes <c>tonemap aces</c> usable -- the Narkowicz fit sits below NWN's radiance
    /// levels unexposed and darkens a dim interior.</summary>
    private static float _exposure = 1f;
    /// <summary>How much of the ACES curve <c>tonemap aces</c> applies, mixed in LINEAR against the shoulder
    /// path. 1 is the full Narkowicz fit; its toe crushes NWN's dim interiors (0.01 linear comes out 2.6x
    /// darker) so that only specular survives on a character in the dark, which reads as "glossy". 0.5 --
    /// xtul's live call -- keeps ACES's highlight roll-off and colour while the shoulder, the identity
    /// below its knee, holds the darks where they are.</summary>
    private static float _acesMix = 0.5f;
    /// <summary>Output dither, in LSBs of the 8-bit target the resolve writes into. 1.0 is the correct
    /// value and not a taste setting: one LSB of triangular-PDF noise is exactly what decorrelates the
    /// quantisation step, and it is the difference between a smooth gradient and a set of contour bands.
    /// 0 disables it, which is only useful for showing what it fixes.</summary>
    private static float _dither = 1f;
    /// <summary><c>/post viz</c>: show one STAGE of the chain instead of the finished frame,
    /// so "is this artifact in the bloom, in the frame the bloom was made from, or in the tonemap?" is a
    /// look rather than an argument. 0 = off, 1 = the bloom contribution alone, 2 = the HDR colour alone
    /// (no bloom, no tonemap), 3 = the bloom contribution amplified 20x, which is what makes faint
    /// structure in a dim halo visible at all.</summary>
    private static float _viz;
    /// <summary><c>/post clearviz 1</c>: clear the HDR target to magenta instead of the engine's clear
    /// colour. Nothing else in the frame can produce that value, so any magenta the finished frame shows is
    /// a pixel NO pass covered -- which is the one question a shading view cannot answer. Debug only, and
    /// deliberately not a boot value: <c>/post reset</c> turns it off with the other views.</summary>
    private static bool _clearViz;
    private static int _mode = Aces;
    /// <summary>#391: the RENDER scale -- the owned 3D frame's size as a fraction of the display target's,
    /// per axis. Below 1 the world renders at fewer pixels and the resolve upscales it (bilinear); above 1 it
    /// is supersampling. The engine's 2D UI never sees it. Boot value from <c>renderscale</c> in sowx.cfg,
    /// live through <c>/post scale</c>; clamped to <see cref="MinScale"/>..<see cref="MaxScale"/> at both
    /// doors because a 0x0 target is a refusal, not a look.</summary>
    private static float _scale = 1f, _bootScale = 1f;
    internal const float MinScale = 0.25f, MaxScale = 2f;
    /// <summary>#388: the camera motion-vector pass armed on its own, <c>/post motion 1</c>. Its consumers (TAA
    /// #389, temporal GTAO #390) run it without this; <c>/post viz 4</c> runs it regardless to paint it.</summary>
    private static bool _motion;
    /// <summary>#389: temporal anti-aliasing, <c>/post taa 1</c>. Arms the sub-pixel jitter at the one site
    /// Alloy reads the projection (Frame.cs) AND the accumulating stage; the two never ship apart. Off by
    /// default until live-verified; SMAA stands aside while it is on.</summary>
    private static bool _taa;
    /// <summary>#389: the current frame's share of the TAA blend at rest (0.1 = a ten-frame history), <c>/post
    /// taablend</c>, 0.02..0.5; motion adds to it in the stage.</summary>
    private static float _taaBlend = 0.1f, _bootTaaBlend = 0.1f;
    private static float _bootBloom, _bootThreshold, _bootKnee, _bootTmKnee, _bootDither, _bootExposure, _bootAcesMix;
    private static int _bootMode;

    private static int _dSrc, _dLod, _dTexel, _dFirst, _dThresh;
    private static int _uSrc, _uLod, _uTexel;
    private static int _cHdr, _cBloom, _cStrength, _cMode, _cKnee, _cDither, _cViz, _cExposure, _cAcesMix;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        // The one boot decision in this class: since #132 there is no Alloy frame without the resolve, so
        // hdr=0 is not a look toggle but a stand-down of the whole Alloy frame -- config, not a /post key.
        _on = Settings.Bool("hdr", true);
        _scale = _bootScale = Settings.Float("renderscale", 1f, MinScale, MaxScale);
        // #152 is the precedent this whole policy generalises (ADR 0013): a debug visualization is a live
        // request, never a boot state -- a forgotten viz setting booted every session into the bloom-debug
        // view until the user typed /post viz 0. Every look dial below is now the same shape.
        _bootBloom = _bloom; _bootThreshold = _threshold; _bootKnee = _knee;
        _bootTmKnee = _tmKnee; _bootMode = _mode; _bootDither = _dither; _bootExposure = _exposure; _bootAcesMix = _acesMix;
        _bootTaaBlend = _taaBlend;
        _gammaParam = (delegate* unmanaged<uint, int>)Native.Sym(SymbolMap.RiGetGlobalShaderParami);
        if (_gammaParam == null)
            log.Info("PostChain: RenderInterface::GetGlobalShaderParami unresolved -- the resolve encodes "
                   + "unconditionally, which is what this client measures anyway (#102)");
        log.Info(_on ? $"PostChain: armed -- {Describe()}; /post retunes every dial live"
                     : "PostChain: config hdr=0 -- the Alloy frame stands down; the world renders vanilla");
    }


    internal static string Describe() =>
        $"hdr {(_on ? "on" : "off")}  bloom {_bloom:F3}  threshold {_threshold:F2}  knee {_knee:F2}  " +
        $"exposure {_exposure:F2}  tonemap {(_mode == Off ? "off" : _mode == Aces ? "aces" : "shoulder")}  acesmix {_acesMix:F2}  tmknee {_tmKnee:F2}  " +
        $"dither {_dither:F2}  scale {_scale:F2} ({(_w > 0 ? $"{_w}x{_h} render, " : "")}lod bias {LodBias:F2})  motion {(_motion ? "on" : "off")}  taa {(_taa ? "on" : "off")} (taablend {_taaBlend:F2}, taau {(_taau ? 1 : 0)}, aotemporal {SsaoPass.Temporal:F2})"
        + (_viz > 0f ? $"  VIZ {_viz:F0}" : "")
        + (_clearViz ? "  CLEARVIZ (target cleared magenta)" : "")
        + " | " + PostStack.Describe();

    /// <summary>The resolve's dither in the unit the shader takes: one LSB of the 8-bit target, or 0.
    /// #375: whichever stage WRITES that target applies it -- the resolve when the stack is empty, the
    /// stack's last stage otherwise -- so the frame is quantised exactly once.</summary>
    internal static float DitherLsb => _dither / 255f;

    /// <summary>#391: a display extent at the render scale, never below one pixel.</summary>
    internal static int RenderSize(int display) => Math.Max((int)MathF.Round(display * _scale), 1);

    /// <summary>#391: the texture LOD bias every owned material sample takes: <c>log2(scale)</c>, so a frame
    /// rendered at fewer pixels picks the mips the DISPLAY footprint would have and the upscale does not
    /// go soft on top of them. Clamped at 0: above 1.0 the point is the extra texture detail the resolve
    /// then averages, and a coarser mip would throw it away. 0 at 1.0, so the default is pixel-identical.</summary>
    internal static float LodBias => MathF.Min(MathF.Log2(_scale), 0f);

    /// <summary>#388: whether the motion-vector pass runs this frame -- armed, the viz that paints it, or a
    /// consumer (TAA #389, temporal GTAO #390) that reprojects through it.</summary>
    internal static bool MotionWanted => _motion || (int)_viz == VizMotion || TaaOn || SsaoPass.TemporalWanted;
    /// <summary>#389: TAA is on and its stage can build -- the gate the jitter and the accumulator share, so
    /// a stage that failed to build never leaves the jitter running with nothing to average it.</summary>
    internal static bool TaaOn => _taa && !TaaStage.Failed && !MotionVectors.Failed;
    internal static float TaaBlend => _taaBlend;
    /// <summary>Temporal upsampling: under a render scale below 1 with TAA on, the accumulator keeps its
    /// history at DISPLAY size and reconstructs from the jittered render-size frames, instead of the resolve
    /// upscaling bilinearly and TAA smoothing the result. <c>/post taau 0</c> is the A/B.</summary>
    internal static bool TaaUpsample => _taau;
    private static bool _taau = true;
    /// <summary>This pass's projection jitter in NDC, as <see cref="Jitter"/> handed it out (0 with TAA off);
    /// the accumulator reads it to place the current frame's samples on the display grid.</summary>
    internal static float JitterX, JitterY;
    /// <summary>#389: the jitter sequence length -- Halton(2,3), eight points, then it repeats.</summary>
    internal const int JitterCount = 8;
    /// <summary>#389: this frame's sub-pixel jitter in NDC for a render target of <paramref name="w"/> x
    /// <paramref name="h"/>: Halton(2,3) over <see cref="JitterCount"/> frames, centred on zero, one render
    /// pixel of spread. Pure, so the self-check can walk the sequence.</summary>
    internal static void Jitter(int frameSeq, int w, int h, out float jx, out float jy)
    {
        int i = (frameSeq & 0x7fffffff) % JitterCount + 1;
        jx = (Halton(i, 2) - 0.5f) * 2f / Math.Max(w, 1);
        jy = (Halton(i, 3) - 0.5f) * 2f / Math.Max(h, 1);
    }
    private static float Halton(int i, int b)
    {
        float f = 1f, r = 0f;
        while (i > 0) { f /= b; r += f * (i % b); i /= b; }
        return r;
    }
    /// <summary><c>/post viz 4</c>: the velocity target, painted by the resolve in place of the frame.</summary>
    internal const int VizMotion = 4;

    /// <summary>Live tuning behind <c>/post</c>; returns the old value, NaN for an unknown key. Same
    /// no-lock reasoning as <see cref="AlloyLights.Tune"/>: independent floats, written from the input
    /// thread, re-read per pass.</summary>
    internal static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            // No "hdr" key: it stands the whole Alloy frame down rather than changing a look, so it is a
            // boot setting (config hdr=0) and Describe reports it read-only.
            case "bloom":     old = _bloom; _bloom = v; break;
            case "threshold": old = _threshold; _threshold = v; break;
            case "knee":      old = _knee; _knee = v; break;
            case "tonemap":   old = _mode; _mode = (int)v; break;
            case "tmknee":    old = _tmKnee; _tmKnee = v; break;
            case "exposure":  old = _exposure; _exposure = v; break;
            case "acesmix":   old = _acesMix; _acesMix = v; break;
            case "dither":    old = _dither; _dither = v; break;
            case "viz":       old = _viz; _viz = v; break;
            case "clearviz":  old = _clearViz ? 1f : 0f; _clearViz = v != 0f; break;
            case "scale":     old = _scale; _scale = Math.Clamp(v, MinScale, MaxScale); break;
            case "motion":    old = _motion ? 1f : 0f; _motion = v != 0f; break;
            case "taa":       old = _taa ? 1f : 0f; _taa = v != 0f; if (_taa) TaaStage.Reset(); break;
            case "taau":      old = _taau ? 1f : 0f; _taau = v != 0f; TaaStage.Reset(); break;
            // Capped at 0.5: above it the accumulator stops averaging the jitter and the frame visibly
            // vibrates (live, at 1.0 -- "an earthquake"). Not an A/B for TAA off; /post taa 0 is.
            case "taablend":  old = _taaBlend; _taaBlend = Math.Clamp(v, 0.02f, 0.5f); break;
            // #390: the ticket's door for the AO history weight; /ao temporal is the same dial.
            case "aotemporal": return SsaoPass.Tune("temporal", v);
            // #375: the display-space stack's own keys, behind the same command.
            default: return PostStack.Tune(key, v);
        }
        _log?.Info($"PostChain: /post {key} {v:F2} (was {old:F2}) -> {Describe()}");
        return old;
    }

    internal static void ResetTuning()
    {
        _bloom = _bootBloom; _threshold = _bootThreshold; _knee = _bootKnee;
        _tmKnee = _bootTmKnee; _mode = _bootMode; _dither = _bootDither; _exposure = _bootExposure; _acesMix = _bootAcesMix;
        // viz has no boot value and never should: it is a debug VIEW, not a look setting, so "reset" means
        // off. Leaving it out is why /post reset could not undo a stray /post viz either.
        _viz = 0f; _clearViz = false;
        _scale = _bootScale; _motion = false; _taa = false; _taaBlend = _bootTaaBlend; _taau = true;
        PostStack.ResetTuning();
        _log?.Info($"PostChain: /post reset -> {Describe()}");
    }

    /// <summary>Drop every GL object (context loss / disarm). Names are meaningless once the context is
    /// gone, so this only forgets them -- it never deletes.</summary>
    internal static void Forget()
    {
        _fbo = 0; _hdrTex = 0; _bloomTex = 0; _depthTex = 0; _w = 0; _h = 0; _levels = 0;
        _scratchFbo = 0; _scratchTex = 0; _scratchW = 0; _scratchH = 0; _scratchFormat = 0;
        _depFormat = 0; _depW = 0; _depH = 0;
        _handbackBad = 0;
        _defaultProbed = false; _defaultFormat = 0; _defaultOk = null;
        _progDown = 0; _progUp = 0; _progComposite = 0; _vao = 0;
        _progFailed = false; Armed = false; _down.Forget();
    }

    /// <summary>#132: the refusals that are decidable WITHOUT the 4.6 bracket, asked in ENGINE context
    /// before the Alloy pass pays for its preamble. False stands the whole pass down to vanilla.
    /// <para>Split out of <see cref="Begin"/> for two reasons and both are load-bearing. It saves the
    /// preamble -- <c>AlloyLights.Upload</c>/<c>Build</c> and <c>PoseSkinned</c>, which solves every Alloy
    /// skinmesh's pose -- on a pass that was never going to arm; on a client that renders
    /// to the default framebuffer that is EVERY frame, forever, to produce a vanilla one. And the gamma
    /// question can only be answered here: it gates the Alloy DECODES too, which live in the stages and
    /// cannot be switched per pass.</para>
    /// <para><b>The gamma gate is a refusal, not a switch.</b> Every Alloy stage linearises its texels
    /// unconditionally -- that is what makes the frame linear -- so a client whose <c>GAMMA_CORRECTION</c>
    /// is 0 (the engine passes texels through raw, <c>ApplyColorSpace</c> is the identity) has no
    /// self-consistent Alloy variant: encoding at the resolve would disagree with the engine, and not
    /// encoding would leave every Alloy surface a gamma dark beside it. Standing down is the honest
    /// answer, and it is re-asked per pass, so flipping the setting back recovers.</para></summary>
    /// <summary>#456: the framebuffer the last composite wrote into -- 0 is the default framebuffer, which
    /// is the case the Windows black frames are about. Published so the probe line can say whether a black
    /// frame is a frame that went somewhere else or a frame that was lost.</summary>
    internal static uint LastInto;

    internal static bool Precheck(int* viewport)
    {
        // config hdr=0 is a deliberate setting and Attach already said so; it is not a refusal to report.
        if (!_on || _down.Failed) return false;
        // #215/#467: the default framebuffer IS the target, every frame and on purpose. Keyed on the shim
        // WINDOW rather than on its setting: with the key on but the shim branch not taken, the engine is
        // still drawing this pass and the refusal is still the right answer.
        if (!RspShim.InWindow) return Refuse("the engine renders to the default framebuffer");
        if (viewport[0] != 0 || viewport[1] != 0) return Refuse("the engine's viewport is offset");
        if (GammaOff())   // #216 asks this at the latch too; kept for the pre-cutover and subframe passes
            return Refuse("the engine's gamma pipeline is off (GetGlobalShaderParami(0) == 0), and the "
                        + "Alloy stages have no un-linearised variant");
        return true;
    }

    /// <summary>The gamma refusal's predicate, published so <see cref="AlloyGate"/> can ask it at frame start
    /// -- where it is a decision about the whole frame rather than about one pass. Unresolved reads as "on",
    /// which is what this client measures (#102).</summary>
    internal static bool GammaOff() => _gammaParam != null && _gammaParam(GammaParam) == 0;

    /// <summary>Point the Alloy colour passes at the HDR target for this pass. #132: a false answer stands
    /// the whole Alloy frame down to vanilla, so this is asked BEFORE anything is staged -- there is no
    /// display-space variant of the Alloy stages left to draw with.
    /// <para>The target's own starting content comes from the engine's clear COLOUR rather than from a blit
    /// of its clear. At the #132 anchor the engine's colour buffer holds nothing but that clear
    /// (Camera::InitializeFrameBufferResources has just run Clear(7) and no draw has followed), and a blit
    /// would copy a display-space value into a linear target -- wrong by a gamma on every area that clears
    /// to something other than black.</para></summary>
    internal static bool Begin(int* viewport, float* clearRgba, double clearDepth)
    {
        Armed = false;
        if (!_on || _down.Failed) return false;
        // BlitFramebuffer is in the list because the hand-back is not optional since #214: without it the
        // Alloy frame draws into a depth buffer the engine never receives, and its residual draws test
        // against nothing but the engine's clear -- geometry through walls, silently.
        if (Gl.ClearColor == null || Gl.Clear == null || Gl.GenFramebuffers == null
            || Gl.FramebufferTexture2D == null || Gl.BlitFramebuffer == null)
            return StandDown("a required GL entry point is missing");
        if (!EnsurePrograms()) return false;
        if (!EnsureTargets(viewport[2], viewport[3])) return false;

        // #214/#467: the owned depth matches FB0's format so the overlay hand-back is a legal blit (a depth
        // blit cannot convert); D24 if FB0 has none. A refused hand-back stands this format down, keyed, so a
        // device reset that changes it gets a fresh attempt.
        uint fb0 = DefaultFormat();
        uint depthFmt = fb0 != 0 ? fb0 : Gl.DEPTH_COMPONENT24;
        if (_handbackBad != 0 && _handbackBad == depthFmt) return false;
        if (!EnsureDepth(depthFmt, viewport[2], viewport[3])) return false;

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _hdrTex, 0);
        // DEPTH_ATTACHMENT alone even for a packed format: nothing owned touches stencil, and every Alloy
        // funnel attaches depth here (see FrameSnapshot's probe, which relies on exactly that).
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, _depthTex, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, PresentTarget.Fbo46);
            return Refuse($"the HDR framebuffer is incomplete (0x{status:x})");
        }

        // The engine's own clear colour, linearised: ground truth rather than a guess, and one clear
        // rather than a full-screen blit. Depth is the engine's attachment and the engine has just cleared
        // it -- clearing it again here would throw away the state the Alloy prepass is about to write into.
        // clearRgba is READ IN THE ENGINE CONTEXT by the caller and handed over. A GL share list shares
        // OBJECTS, never per-context state, and glClearColor is per-context: querying it here would return
        // whatever THIS context was last set to -- (0,0,0,0) until the first probe capture, and EnvProbe's
        // own clear colour ever after.
        if (_clearViz) Gl.ClearColor(1f, 0f, 1f, 1f);
        else Gl.ClearColor(MathF.Pow(MathF.Max(clearRgba[0], 0f), AlloyLights.SrgbGamma),
                      MathF.Pow(MathF.Max(clearRgba[1], 0f), AlloyLights.SrgbGamma),
                      MathF.Pow(MathF.Max(clearRgba[2], 0f), AlloyLights.SrgbGamma), clearRgba[3]);
        // Depth comes with it since #214: the engine cleared ITS attachment at the anchor, not ours.
        // The mask is set beside the clear rather than assumed -- glClear honours it, and a stale
        // DepthMask(0) would leave the prepass writing into last pass's depth. The VALUE is the engine's
        // own, handed over for exactly the reason the clear colour is: glClearDepth is per-context state,
        // and the resolve blits this buffer back over every pixel of the engine's attachment -- including
        // the ones no Alloy pass covered, which would otherwise get a far value the engine never chose.
        if (Gl.ClearDepth != null) Gl.ClearDepth(clearDepth);
        Gl.DepthMask(1);
        Gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        // Left set: the only other consumer of this context's clear colour is EnvProbe, which sets its own
        // every capture (EnvProbe.Begin), so there is nothing here worth restoring.
        Armed = true;
        return true;
    }

    /// <summary>#214: hand the depth Alloy just wrote back to the engine's attachment. Alloy owns the
    /// buffer now, but the engine's own draws after the bracket -- the residual bucket walk, the GUI 3D
    /// panels -- still depth-test against the world this pass rendered, so a frame that skipped this would
    /// show them punching through walls. Legal because <see cref="EnsureDepth"/> matched the format;
    /// NEAREST because a depth blit may not filter.
    /// <para>The caller has the frame target bound as the draw target, which is the whole point of doing
    /// it here rather than in the caller.</para>
    /// <para>Whether the blit is legal for the target in front of us is asked of the DRIVER rather than
    /// pre-refused: it is attempted and the error checked.</para></summary>
    /// <summary>The blit both hand-backs are: drain the error queue so what comes back is ours, copy the
    /// owned depth into <paramref name="dst"/>, restore the draw binding. Returns the GL error, 0 for
    /// success. The two callers differ only in what a failure costs them.</summary>
    private static uint BlitDepthInto(uint dst, int dstW, int dstH)
    {
        if (Gl.GetError != null) for (int i = 0; i < 4 && Gl.GetError() != 0; i++) { }
        uint src = _fbo;
        // #391: a SCALING blit under a render scale -- legal for depth as long as the filter is NEAREST and
        // neither side is multisampled; into an MSAA target it goes through the 1x scratch first.
        if ((dstW != _depW || dstH != _depH) && Multisampled(dst) && EnsureScratch(dstW, dstH))
        {
            Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _fbo);
            Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _scratchFbo);
            Gl.BlitFramebuffer(0, 0, _depW, _depH, 0, 0, dstW, dstH, Gl.DEPTH_BUFFER_BIT, (uint)Gl.NEAREST);
            Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, dst);
            src = _scratchFbo;
        }
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, src);
        int sw = src == _fbo ? _depW : dstW, sh = src == _fbo ? _depH : dstH;
        Gl.BlitFramebuffer(0, 0, sw, sh, 0, 0, dstW, dstH, Gl.DEPTH_BUFFER_BIT, (uint)Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, dst);
        return Gl.GetError == null ? 0 : Gl.GetError();
    }

    /// <summary>Whether the bound draw target carries sample buffers. Asked of the framebuffer, not latched:
    /// the engine's target is a per-pass fact.</summary>
    private static bool Multisampled(uint dst)
    {
        int n = 0;
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, dst);
        Gl.GetIntegerv(Gl.SAMPLE_BUFFERS, &n);
        return n > 0;
    }

    private static bool EnsureScratch(int w, int h)
    {
        if (_scratchTex != 0 && w == _scratchW && h == _scratchH && _scratchFormat == _depFormat) return true;
        if (w <= 0 || h <= 0) return false;
        if (_scratchTex == 0)
        {
            uint t; Gl.GenTextures(1, &t); _scratchTex = t;
            uint f; Gl.GenFramebuffers(1, &f); _scratchFbo = f;
        }
        RenderTarget.AllocDepth(_scratchTex, _depFormat, w, h);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _scratchFbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, _scratchTex, 0);
        bool ok = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) == Gl.FRAMEBUFFER_COMPLETE;
        _scratchW = w; _scratchH = h; _scratchFormat = _depFormat;
        _log?.Info(ok ? $"PostChain: depth hand-back scratch {w}x{h} (multisampled engine target under a render scale)"
                      : "PostChain: the depth hand-back scratch is incomplete -- the scaling blit goes direct and may be refused");
        return ok;
    }

    private static void ResolveDepth(int dstW, int dstH)
    {
        if (Gl.BlitFramebuffer == null || _depthTex == 0) return;
        int dst; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &dst);
        if (dst == 0) { DefaultDepthOk = RspShim.WantsEngineOverlays && BlitDepthToDefault(dstW, dstH); return; }
        // #462: on Windows the frame lands in PresentTarget's texture, and HandBack blits the owned depth
        // into the real default framebuffer after the bracket -- so this IS the default-target case, with the
        // hand-back done by the engine context instead of here. Without this branch DefaultDepthOk never
        // armed on Windows and the engine's highlight glow never drew there.
        if (dst == PresentTarget.Fbo46)
        {
            if (_defaultOk == null) _defaultOk = DefaultFormat() != 0;
            DefaultDepthOk = RspShim.WantsEngineOverlays && _defaultOk == true;
            DefaultStencilOk = DefaultDepthOk && _defaultHasStencil;
            return;
        }
        DefaultDepthOk = false;   // an engine attachment: ResolveDepth's own hand-back, not the overlays'
        uint err = BlitDepthInto((uint)dst, dstW, dstH);
        if (err == 0) return;
        // Stand THIS format down: the frame already drew with depth the engine never received, and a
        // second one would be a choice. A device reset that changes the format is tried on its own merits.
        _handbackBad = _depFormat;
        _ = Refuse($"the depth resolve blit raised 0x{err:x} (owned depth format 0x{_depFormat:x}) -- the "
                 + "owned depth cannot be handed back to the frame target, so the Alloy frame stands down.");
    }

    /// <summary>#232/#233: true when the frame's owned depth reached the DEFAULT framebuffer, so the
    /// engine's dropped highlight overlays may be run after the bracket and still be occluded by the world.
    /// False on any frame that did not blit -- and the overlays are then skipped rather than drawn through
    /// walls.</summary>
    internal static bool DefaultDepthOk;

    /// <summary>#233: and whether it carries STENCIL, which is a separate question with a worse failure.
    /// <c>Scene::DoHighlightTriggers</c> builds a silhouette mask and then draws a FULL-SCREEN quad through
    /// <c>SetStencilFunc(3, 0x80, ~0)</c>; with no stencil buffer the ops are no-ops and the test always
    /// passes, so that quad paints the whole screen instead of the trigger. Depth-only is survivable and the
    /// gob highlight still runs; the trigger half does not.</summary>
    internal static bool DefaultStencilOk;

    /// <summary>Whether the default framebuffer has been found to carry usable depth; null until asked.
    /// Asked once per session -- the window's depth buffer is fixed at context creation.</summary>
    private static bool? _defaultOk;

    /// <summary>The stencil half of that one-shot answer.</summary>
    private static bool _defaultHasStencil;

    /// <summary>The default framebuffer's depth format as its bit widths and component type describe it
    /// (the ladder <see cref="DefaultFormat"/> reads); 0 = no depth. Under the M4 shim the owned depth
    /// is ALLOCATED to this, so the hand-back is the format-matched blit GL 4.6 18.3.1 requires: the author's
    /// driver tolerated D24 into D24S8, and Mesa raises no error for the mismatch either -- it just does
    /// what it likes with it (docs/research/opengl-46-portability-audit.md, E4).</summary>
    private static uint _defaultFormat, _defaultMismatchFmt;
    private static bool _defaultProbed;

    private static uint DefaultFormat()
    {
        if (_defaultProbed) return _defaultFormat;
        _defaultProbed = true;
        int bits = 0, sbits = 0, comp = 0;
        if (Gl.GetFramebufferAttachmentParameteriv != null)
        {
            int prev; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &prev);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.DEPTH, Gl.FRAMEBUFFER_ATTACHMENT_DEPTH_SIZE, &bits);
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.STENCIL, Gl.FRAMEBUFFER_ATTACHMENT_STENCIL_SIZE, &sbits);
            Gl.GetFramebufferAttachmentParameteriv(Gl.FRAMEBUFFER, Gl.DEPTH, Gl.FRAMEBUFFER_ATTACHMENT_COMPONENT_TYPE, &comp);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)prev);
            if (Gl.GetError != null) for (int i = 0; i < 4 && Gl.GetError() != 0; i++) { }
        }
        _defaultHasStencil = sbits > 0;
        _defaultFormat = bits <= 0 ? 0
            : sbits > 0 ? (comp == (int)Gl.FLOAT ? Gl.DEPTH32F_STENCIL8 : Gl.DEPTH24_STENCIL8)
            : bits >= 32 ? (comp == (int)Gl.FLOAT ? Gl.DEPTH_COMPONENT32F : Gl.DEPTH_COMPONENT32)
            : bits >= 24 ? Gl.DEPTH_COMPONENT24 : Gl.DEPTH_COMPONENT16;
        _log?.Info(bits > 0
            ? $"PostChain: default framebuffer carries {bits}-bit depth and {sbits}-bit stencil (0x{_defaultFormat:x}) -- the "
            + $"engine highlight overlays (#232) get the owned depth handed back and are occluded by the "
            + $"world{(sbits > 0 ? ", and the trigger highlight (#233) has its stencil" : "; the TRIGGER highlight (#233) stands down, having no stencil to mask its full-screen quad")}"
            : "PostChain: the default framebuffer has NO depth buffer -- the engine highlight overlays "
            + "(#232/#233) stand down rather than draw through walls");
        return _defaultFormat;
    }

    /// <summary>#232/#233: the same hand-back as <see cref="ResolveDepth"/>, but into the DEFAULT
    /// framebuffer, which under the shim is the only target there is. Nothing else writes that depth buffer
    /// on an Alloy frame -- the engine's world draws are all swallowed -- so the engine overlays the shim
    /// re-runs after the bracket would depth-test against an uncleared buffer and appear through walls.
    /// <para>Gated on the window actually having depth bits, asked of the framebuffer rather than assumed:
    /// a blit whose destination has no depth buffer drops the bit SILENTLY and returns no error, so
    /// glGetError cannot be the check. A failure here stands the OVERLAYS down, never the Alloy frame --
    /// the frame itself does not need this blit.</para></summary>
    private static bool BlitDepthToDefault(int dstW, int dstH)
    {
        if (Gl.BlitFramebuffer == null || _depthTex == 0) return false;
        if (_defaultOk == null) _defaultOk = DefaultFormat() != 0;
        if (_defaultOk != true) return false;
        if (_defaultFormat != _depFormat && _defaultMismatchFmt != _depFormat)
        {
            _defaultMismatchFmt = _depFormat;
            _log?.Info($"PostChain: WARN the depth hand-back blits 0x{_depFormat:x} into a default framebuffer of " +
                       $"0x{_defaultFormat:x} -- GL 4.6 18.3.1 makes a mismatched depth blit INVALID_OPERATION, and Mesa " +
                       "raises no error for it (main/blit.c), so the verdict below cannot see it; overlays may occlude wrong");
        }
        DefaultStencilOk = _defaultHasStencil;
        uint err = BlitDepthInto(0, dstW, dstH);
        if (err == 0) return true;
        _defaultOk = false;
        DefaultStencilOk = false;
        _log?.Info($"PostChain: the depth hand-back to the default framebuffer raised 0x{err:x} -- the "
                 + "engine highlight overlays (#232/#233) stand down for this session");
        return false;
    }

    /// <summary>Alloy's own depth attachment, sized to the target and format-matched to the engine's.
    /// Keyed on the FORMAT as well as the size, because a device reset can hand back a differently formatted
    /// attachment at the same resolution -- and the format is what makes <see cref="ResolveDepth"/> legal.</summary>
    private static bool EnsureDepth(uint fmt, int w, int h)
    {
        if (_depthTex != 0 && fmt == _depFormat && w == _depW && h == _depH) return true;
        if (w <= 0 || h <= 0) return false;
        if (_depthTex != 0 && Gl.DeleteTextures != null)
        { uint old = _depthTex; Gl.DeleteTextures(1, &old); _depthTex = 0; }
        uint tex; Gl.GenTextures(1, &tex);
        if (tex == 0) return Refuse("the owned depth texture could not be created");
        RenderTarget.AllocDepth(tex, fmt, w, h);
        _depthTex = tex; _depFormat = fmt; _depW = w; _depH = h;
        _log?.Info($"PostChain: owned depth attachment {w}x{h}, internal format 0x{fmt:x} "
                 + "(matched to the engine's, which the resolve blits back into)");
        return true;
    }

    /// <summary>Say why the HDR target is not arming, once per distinct reason, and go on checking. #216: a
    /// refusal taken inside the shim window ALSO latches the frame verdict -- the engine's frame is already
    /// gone by then, so the next frame goes vanilla with this reason rather than the world staying empty.
    /// <para>NOT latched otherwise: the engine's target is a per-pass fact -- a reflection subframe or a GUI 3D panel can render
    /// to a different one than the world pass does -- so a session that refused once must still arm on the
    /// passes it can. The whole point of the line is that the fallback is otherwise silent: the frame
    /// renders, just without any of #139 in it.</para></summary>
    private static bool Refuse(string why)
    {
        if (RspShim.InWindow) AlloyGate.Fail(why);   // #263: nothing to hand this pass back to, and no restart-free way to get it
        return _down.Refuse(_log, why, "the HDR target is not armed; #132: the Alloy frame stands down for those "
                                     + "passes and the engine renders them vanilla");
    }

    /// <summary>...and the latching kind, for a failure that cannot come back: a stage that did not build,
    /// an entry point the driver does not have.</summary>
    private static bool StandDown(string why)
    {
        // #263: same argument as Refuse's -- this latches inside a frame the verdict already froze on, and
        // nothing re-reads PostChain.Failed afterwards.
        if (RspShim.InWindow) AlloyGate.Fail(why);
        return _down.Fail(_log, why, "the HDR target cannot arm this session; the engine renders vanilla");
    }

    /// <summary>The bloom chain: progressive downsample, then a tent upsample back up, accumulating. Runs
    /// with the HDR target still bound as the read source and the chain's own framebuffer as the draw
    /// target, so the caller must rebind the engine target before <see cref="Composite"/>.</summary>
    internal static void Bloom()
    {
        _bloomOk = false;
        if (!Armed || _bloom <= 0f || _levels <= 0) return;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        new PassState(depth: false, depthWrite: false).Apply();
        Gl.BindVertexArray(_vao);
        Gl.ActiveTexture(Gl.TEXTURE0 + BloomUnit);

        // Downsample. Level 0 reads the HDR colour (and is the only pass that thresholds);
        // every level after it reads the level above out of the same texture it is writing into -- legal
        // only while BASE/MAX_LEVEL exclude the attached level, which is what the clamp below is for. Skip
        // it and the chain samples its own attachment: undefined, and black on the drivers that enforce it.
        Gl.UseProgram(_progDown);
        Gl.Uniform1i(_dSrc, (int)BloomUnit);
        for (int i = 0; i < _levels; i++)
        {
            int sw = i == 0 ? _w : LevelW(i - 1), sh = i == 0 ? _h : LevelH(i - 1);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _bloomTex, i);
            Gl.Viewport(0, 0, LevelW(i), LevelH(i));
            if (i == 0) { Gl.BindTexture(Gl.TEXTURE_2D, _hdrTex); Gl.Uniform1i(_dLod, 0); }
            else { Gl.BindTexture(Gl.TEXTURE_2D, _bloomTex); Clamp(i - 1); Gl.Uniform1i(_dLod, i - 1); }
            Gl.Uniform1i(_dFirst, i == 0 ? 1 : 0);
            Gl.Uniform2f(_dTexel, 1f / sw, 1f / sh);
            Gl.Uniform2f(_dThresh, _threshold, MathF.Max(_knee, 1e-4f));
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        }

        // ...and back up, adding each level into the one below it. ONE/ONE rather than a second texture:
        // the tent filter's whole job is to be added, and a ping-pong pair would double the allocation to
        // save nothing.
        Gl.UseProgram(_progUp);
        Gl.Uniform1i(_uSrc, (int)BloomUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _bloomTex);
        Gl.Enable(Gl.BLEND);
        Gl.BlendFunc(Gl.ONE, Gl.ONE);
        for (int i = _levels - 1; i > 0; i--)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _bloomTex, i - 1);
            Gl.Viewport(0, 0, LevelW(i - 1), LevelH(i - 1));
            Clamp(i);
            Gl.Uniform1i(_uLod, i);
            Gl.Uniform2f(_uTexel, 1f / LevelW(i), 1f / LevelH(i));
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        }
        Gl.Disable(Gl.BLEND);
        Clamp(0);
        // The HDR colour goes back on the attachment: anything binding this framebuffer afterwards (nothing
        // does today, but the pass list is a list) must not find a bloom mip there.
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _hdrTex, 0);
        _bloomOk = true;
    }

    /// <summary>Resolve the HDR target: add the bloom, tonemap, encode. The caller has already rebound the
    /// engine target and its viewport, which is where the depth hand-back goes. #375: the COLOUR goes to
    /// <paramref name="into"/> when it is non-zero -- <see cref="PostStack.Begin"/>'s framebuffer, when a
    /// display-space stage is live this frame -- and the dither goes with the 8-bit write, so it is the
    /// stack's last stage's when the stack takes the frame.</summary>
    internal static void Composite(int* viewport, uint into) => Composite(viewport, into, viewport);

    /// <summary>...with the colour draw at <paramref name="rect"/>: the render viewport when the accumulator
    /// upsamples (it reads the resolve at render size), the display viewport otherwise. The depth hand-back
    /// is always at the display viewport.</summary>
    internal static void Composite(int* viewport, uint into, int* rect)
    {
        if (!Armed) return;
        ResolveDepth(viewport[2], viewport[3]);
        if (into != 0) { Gl.BindFramebuffer(Gl.FRAMEBUFFER, into); Gl.Disable(Gl.FRAMEBUFFER_SRGB); }
        Gl.UseProgram(_progComposite);
        Gl.BindVertexArray(_vao);
        new PassState(depth: false, depthWrite: false).Apply();
        Gl.ActiveTexture(Gl.TEXTURE0 + HdrUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _hdrTex);
        Gl.ActiveTexture(Gl.TEXTURE0 + BloomUnit);
        // #388: the velocity viz rides the bloom unit -- nothing else the resolve samples is free, and the
        // two views are exclusive by construction.
        Gl.BindTexture(Gl.TEXTURE_2D, (int)_viz == VizMotion ? MotionVectors.Tex : _bloomOk ? _bloomTex : 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(_cHdr, (int)HdrUnit);
        Gl.Uniform1i(_cBloom, (int)BloomUnit);
        Gl.Uniform1f(_cStrength, _bloomOk ? _bloom : 0f);
        Gl.Uniform1i(_cMode, _mode);
        Gl.Uniform1f(_cKnee, _tmKnee);
        Gl.Uniform1f(_cExposure, _exposure);
        Gl.Uniform1f(_cAcesMix, _acesMix);
        // ...in LSBs of the 8-bit target this writes into, converted here rather than in the shader so the
        // dial reads as "how many quantisation steps of noise", which is the only unit it makes sense in.
        Gl.Uniform1f(_cDither, into != 0 ? 0f : DitherLsb);
        Gl.Uniform1i(_cViz, (int)_viz);
        Gl.Viewport(rect[0], rect[1], rect[2], rect[3]);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        // Hand the depth state back the way the colour passes left it -- the probe capture may still be
        // ahead of us in another pass order, and the engine's own draws follow the bracket.
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthMask(1);
        Gl.BindVertexArray(0);
        Armed = false;
        Resolves++;
        LastInto = into;
    }

    /// <summary>The one term the [alloy] period line carries: did the resolve run, and how often. Resets the
    /// counter, like every other period statistic.</summary>
    internal static string Stats(int frames)
    {
        int n = Resolves;
        Resolves = 0;
        if (!_on) return "off (config hdr=0)";
        return n == 0 ? $"NOT RESOLVING ({(_down.Reason.Length == 0 ? "never reached the pass" : _down.Reason)})"
                      : $"{(float)n / MathF.Max(frames, 1):F2} resolves/frame at {_w}x{_h} (scale {_scale:F2}), {_levels}-level bloom";
    }

    private static int LevelW(int i) => Math.Max(_w >> (i + 1), 1);
    private static int LevelH(int i) => Math.Max(_h >> (i + 1), 1);

    /// <summary>Pin the bound bloom texture to exactly one level, so the level currently attached as the
    /// draw buffer is outside the sampleable range.</summary>
    private static void Clamp(int level)
    {
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_BASE_LEVEL, level);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, level);
    }

    private static bool EnsureTargets(int w, int h)
    {
        if (_hdrTex != 0 && w == _w && h == _h) return true;
        if (w <= 0 || h <= 0) return false;
        if (_hdrTex != 0 && Gl.DeleteTextures != null)
        {
            uint* old = stackalloc uint[2] { _hdrTex, _bloomTex };
            Gl.DeleteTextures(2, old);
            _hdrTex = 0; _bloomTex = 0;
        }
        _w = w; _h = h;
        _levels = 0;
        while (_levels < MaxLevels && Math.Min(LevelW(_levels), LevelH(_levels)) >= 8) _levels++;
        // ...but never zero: a texture with no levels is incomplete, and one bound to a declared sampler is
        // worth avoiding even where nothing samples it. A 1-level chain on a tiny target is a no-op bloom.
        if (_levels < 1) _levels = 1;

        uint tex;
        Gl.GenTextures(1, &tex);
        // LINEAR: the resolve and the bloom downsample both read it at an offset, not at texel centres.
        RenderTarget.AllocColor(tex, true, w, h, Gl.LINEAR);
        Gl.ActiveTexture(Gl.TEXTURE0 + HdrUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, 0);
        _hdrTex = tex;

        Gl.GenTextures(1, &tex);
        Gl.ActiveTexture(Gl.TEXTURE0 + BloomUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        for (int i = 0; i < _levels; i++)
            Gl.TexImage2D(Gl.TEXTURE_2D, i, (int)Gl.RGBA16F, LevelW(i), LevelH(i), 0, Gl.RGBA, Gl.HALF_FLOAT, null);
        // LINEAR within a level, never across levels: every read is an explicit textureLod and a mip-filtered
        // minification here would blend in a level the chain has not written yet. Hence MAX_LEVEL rather than
        // a plain shared allocation -- the chain is levelled, so only the clamp half is shared.
        RenderTarget.Clamp(Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, _levels - 1);
        _bloomTex = tex;
        Gl.ActiveTexture(Gl.TEXTURE0);

        if (_fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo = f; }
        _log?.Info($"PostChain: HDR target {w}x{h} RGBA16F, {_levels}-level bloom chain, with its own " +
                   "depth attachment (#214)");
        return true;
    }

    /// <summary>#161: throw away a post program built from an override that breaks the colour-space contract
    /// and rebuild it from the embedded pair -- the same refuse-and-say-so shape as
    /// <c>AlloyRenderer.RebuildEmbedded</c>. Both stages come from the embedded text, never a content
    /// vertex stage over an embedded fragment one: the fall back is to a KNOWN pair.</summary>
    private static uint Rebuild(uint stale, string fs, string what, string why)
    {
        Gl.DeleteProgram(stale);
        _log?.Info($"PostChain: the {what} override is refused -- {why}. Rebuilding from the embedded "
                 + "default (regenerate the override with /dumpshaders to get it back).");
        return GlProgram.Graphics(FullscreenVs, fs, _log, $"PostChain {what} (embedded)");
    }

    private static bool EnsurePrograms()
    {
        if (_progComposite != 0) return true;
        if (_progFailed) return false;
        string post = ShaderStages.Source(ShdPostVs);
        _progDown = GlProgram.Graphics(ShdPostVs, post, ShdBloomDownFs, ShaderStages.Source(ShdBloomDownFs),
                                       _log, "PostChain bloom downsample", out _, out string downSrc);
        if (_progDown != 0 && !ShaderContract.LinearSafe(downSrc))
            _progDown = Rebuild(_progDown, BloomDownFs, "bloom downsample",
                                "it still encodes to display space (pre-#132), and the chain it feeds is linear");
        _progUp = GlProgram.Graphics(ShdPostVs, post, ShdBloomUpFs, ShaderStages.Source(ShdBloomUpFs),
                                     _log, "PostChain bloom upsample", out _, out string upSrc);
        if (_progUp != 0 && !ShaderContract.LinearSafe(upSrc))
            _progUp = Rebuild(_progUp, BloomUpFs, "bloom upsample",
                              "it still encodes to display space (pre-#132), and the chain it feeds is linear");
        _progComposite = GlProgram.Graphics(ShdPostVs, post, ShdResolveFs, ShaderStages.Source(ShdResolveFs),
                                            _log, "PostChain resolve", out _, out string resolveSrc);
        // #208: the resolve's two units come with it -- an override that renamed either samples unit 0.
        if (_progComposite != 0 && !(ShaderContract.ResolveSafe(resolveSrc)
                                     && ShaderContract.SlotSafe(resolveSrc, "uHdr", "uBloom")))
            _progComposite = Rebuild(_progComposite, ResolveFs, "resolve",
                                     "it decodes its input (pre-#132) or performs no encode at all, and this "
                                     + "is the frame's one encode");
        if (_progDown == 0 || _progUp == 0 || _progComposite == 0)
        {
            _progFailed = true;
            StandDown("a post program did not build");
            return false;
        }
        _dSrc = GlProgram.Loc(_progDown, "uSrc"); _dLod = GlProgram.Loc(_progDown, "uLod");
        _dTexel = GlProgram.Loc(_progDown, "uTexel"); _dFirst = GlProgram.Loc(_progDown, "uFirst");
        _dThresh = GlProgram.Loc(_progDown, "uThresh");
        _uSrc = GlProgram.Loc(_progUp, "uSrc"); _uLod = GlProgram.Loc(_progUp, "uLod");
        _uTexel = GlProgram.Loc(_progUp, "uTexel");
        _cHdr = GlProgram.Loc(_progComposite, "uHdr"); _cBloom = GlProgram.Loc(_progComposite, "uBloom");
        _cStrength = GlProgram.Loc(_progComposite, "uStrength"); _cMode = GlProgram.Loc(_progComposite, "uMode");
        _cKnee = GlProgram.Loc(_progComposite, "uKnee");
        _cExposure = GlProgram.Loc(_progComposite, "uExposure");
        _cAcesMix = GlProgram.Loc(_progComposite, "uAcesMix");
        _cDither = GlProgram.Loc(_progComposite, "uDither");
        _cViz = GlProgram.Loc(_progComposite, "uViz");
        // Core profile draws nothing with VAO 0 bound, even from a vertex stage that reads no attributes.
        if (_vao == 0) { uint v; Gl.GenVertexArrays(1, &v); _vao = v; }
        return true;
    }

    /// <summary>Resrefs a content author overrides the post stages at (#92), same rules as every other owned
    /// program. All three share <see cref="FullscreenVs"/>, so overriding <c>cx_post_vs</c> replaces the
    /// vertex stage of all three at once.</summary>
    internal const string ShdPostVs = "cx_post_vs", ShdBloomDownFs = "cx_bloomdn_fs";
    internal const string ShdBloomUpFs = "cx_bloomup_fs", ShdResolveFs = "cx_resolve_fs";

    /// <summary>The full-screen triangle every post stage draws: three vertices, no buffers, no attributes.
    /// A triangle rather than a quad because the diagonal seam of a two-triangle quad costs a second
    /// rasterisation of every pixel along it and buys nothing.</summary>
    internal const string FullscreenVs = """
        #version 460 core
        out vec2 vUV;
        void main()
        {
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            vUV = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>#132: the frame's one encode, and the only colour-space conversion left anywhere in the
    /// Alloy pipeline. Its own text rather than a share of <see cref="AlloyLights.FragmentLib"/> because
    /// that lib declares the light SSBOs and the cluster grid, none of which a full-screen pass has or
    /// wants -- and because the exponent comes from the same <c>$SRGB$</c> token either way.
    /// <para>Unconditional, and that is not the pre-#132 sin coming back: the engine's gamma parameter is
    /// asked in <see cref="Precheck"/>, which stands the whole Alloy pass down when it is off. This stage
    /// only ever runs on a frame whose stages linearised, so it always has an encode to undo.</para></summary>
    private const string PostColorLib = """
        vec3 SowxEncode(vec3 c) { return pow(max(c, vec3(0.0)), vec3(1.0 / $SRGB$)); }
        """;

    /// <summary>The frame's one dither, shared by the resolve and every <see cref="PostStack"/> stage: the
    /// whole Alloy frame is computed in RGBA16F and written ONCE into an 8-bit target, so every gradient in
    /// it quantises there, and a smooth shallow one -- a bloom halo -- lands its steps as contour bands.
    /// TRIANGULAR PDF, two independent samples summed, not uniform: a uniform dither leaves the noise floor
    /// modulated by where the signal sits inside its step, so bands survive faintly; the triangular sum
    /// decorrelates the error outright. <c>lsb</c> is one LSB of the target, or 0 for an intermediate.</summary>
    internal const string PostDitherLib = """
        float SowxNoise(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
        vec3 SowxDither(vec3 c, vec2 px, float lsb)
        {
            if (lsb <= 0.0) return c;
            float r1 = SowxNoise(px);
            float r2 = SowxNoise(px + vec2(17.31, 47.13));
            return c + (r1 + r2 - 1.0) * lsb;
        }
        """;

    /// <summary>Progressive downsample, the 13-tap filter with the four-group Karis average (Jimenez'
    /// "Next Generation Post Processing"). The Karis weighting is not decoration: a plain box filter over a
    /// single very bright texel makes the bloom flicker as that texel crosses a pixel boundary, which on a
    /// carried torch is every frame.
    /// <para>Level 0 is also the only pass that applies the threshold, because it is the only one reading
    /// the HDR colour rather than the chain. #132: nothing to decode any more -- the colour is linear, so
    /// the threshold is linear-referred. Its default does not move with it: the threshold is 1.0 and
    /// pow(1, 2.2) is 1, so the boundary is the same number in either space. Only the soft KNEE's width
    /// changes shape below it.</para></summary>
    internal const string BloomDownFs = """
        #version 460 core
        in vec2 vUV;
        out vec4 frag;
        uniform sampler2D uSrc;
        uniform int uLod;
        uniform vec2 uTexel;    // 1 / size of the SOURCE level
        uniform int uFirst;     // 1 = this pass reads the HDR colour: apply the threshold
        uniform vec2 uThresh;   // x = brightness threshold, y = soft-knee width

        vec3 T(vec2 o) { return textureLod(uSrc, vUV + o * uTexel, float(uLod)).rgb; }

        void main()
        {
            vec3 a = T(vec2(-2.0,  2.0)), b = T(vec2( 0.0,  2.0)), c = T(vec2( 2.0,  2.0));
            vec3 d = T(vec2(-2.0,  0.0)), e = T(vec2( 0.0,  0.0)), f = T(vec2( 2.0,  0.0));
            vec3 g = T(vec2(-2.0, -2.0)), h = T(vec2( 0.0, -2.0)), i = T(vec2( 2.0, -2.0));
            vec3 j = T(vec2(-1.0,  1.0)), k = T(vec2( 1.0,  1.0));
            vec3 l = T(vec2(-1.0, -1.0)), m = T(vec2( 1.0, -1.0));
            // Karis average: each 2x2 group is weighted by 1/(1+luma) before being summed, so one runaway
            // texel cannot dominate the group it lands in.
            vec3 grp[5] = vec3[5]((j + k + l + m) * 0.25, (a + b + d + e) * 0.25, (b + c + e + f) * 0.25,
                                  (d + e + g + h) * 0.25, (e + f + h + i) * 0.25);
            const float w[5] = float[5](0.5, 0.125, 0.125, 0.125, 0.125);
            vec3 sum = vec3(0.0);
            float wsum = 0.0;
            for (int n = 0; n < 5; n++)
            {
                float kw = w[n] / (1.0 + dot(grp[n], vec3(0.2126, 0.7152, 0.0722)));
                sum += grp[n] * kw;
                wsum += kw;
            }
            vec3 col = sum / max(wsum, 1e-6);
            if (uFirst != 0)
            {
                // Unreal's soft-knee curve: nothing below the threshold blooms at all, which is what keeps
                // the effect off engine-drawn geometry's range and off every ordinary lit surface.
                float br = max(col.r, max(col.g, col.b));
                float soft = clamp(br - uThresh.x + uThresh.y, 0.0, 2.0 * uThresh.y);
                soft = soft * soft / (4.0 * uThresh.y);
                col *= max(soft, br - uThresh.x) / max(br, 1e-6);
            }
            frag = vec4(max(col, vec3(0.0)), 1.0);
        }
        """;

    /// <summary>The 3x3 tent back up the chain, added into the level below with ONE/ONE. Tent rather than
    /// bilinear: the upsample is what turns nine discrete mips into one continuous falloff, and a plain
    /// bilinear read leaves each level's own resolution visible as a ring.</summary>
    internal const string BloomUpFs = """
        #version 460 core
        in vec2 vUV;
        out vec4 frag;
        uniform sampler2D uSrc;
        uniform int uLod;
        uniform vec2 uTexel;    // 1 / size of the SOURCE level

        vec3 T(vec2 o) { return textureLod(uSrc, vUV + o * uTexel, float(uLod)).rgb; }

        void main()
        {
            vec3 c = T(vec2(-1.0,  1.0)) + T(vec2(0.0,  1.0)) * 2.0 + T(vec2(1.0,  1.0))
                   + T(vec2(-1.0,  0.0)) * 2.0 + T(vec2(0.0, 0.0)) * 4.0 + T(vec2(1.0, 0.0)) * 2.0
                   + T(vec2(-1.0, -1.0)) + T(vec2(0.0, -1.0)) * 2.0 + T(vec2(1.0, -1.0));
            frag = vec4(c * (1.0 / 16.0), 1.0);
        }
        """;

    /// <summary>The resolve: add the bloom, tonemap, and do the frame's ONE encode -- #132's exit step.
    /// <para><c>uMode</c> 1 (the default) is a shoulder: identity below the knee, asymptotic to 1 above it.
    /// That shape is the requirement -- engine-drawn geometry shares this framebuffer and never passes
    /// through here, so a curve that moved the midtones would split the frame in two. 2 is ACES
    /// (Narkowicz' fit), which does move them: a deliberate whole-frame look, not a default.</para></summary>
    internal const string ResolveFs = """
        #version 460 core
        in vec2 vUV;
        out vec4 frag;
        uniform sampler2D uHdr;
        uniform sampler2D uBloom;
        uniform float uStrength;
        uniform int uMode;
        uniform float uKnee;
        uniform float uExposure;  // scene-referred gain before the tonemap; 1 = identity
        uniform float uAcesMix;   // mode 2: ACES mixed against the shoulder, in linear; 1 = full ACES
        uniform float uDither;   // one LSB of the output target, or 0
        uniform int uViz;        // 0 = the frame; 1 = bloom only; 2 = HDR only; 3 = bloom x20; 4 = motion vectors

        """ + PostColorLib + PostDitherLib + """

        vec3 SowxShoulder(vec3 c, float k)
        {
            if (k <= 0.0) return c;
            vec3 t = max(c - vec3(k), vec3(0.0));
            // The curve is C1 at the knee and asymptotic to 1, so anything the target could already show is
            // untouched and only the range that used to clip moves. k is a LINEAR value here; the encode
            // below puts the result back in display space.
            float m = 1.0 - k;
            return min(c, vec3(k)) + m * t / (m + t);
        }

        vec3 SowxAces(vec3 c)
        {
            const float a = 2.51, b = 0.03, d = 2.43, e = 0.59, f = 0.14;
            return clamp((c * (a * c + b)) / (c * (d * c + e) + f), 0.0, 1.0);
        }

        void main()
        {
            // The stage views. Deliberately BEFORE the tonemap and the dither: the question they answer is
            // "which stage introduced this", and running the answer through two more stages first is how a
            // diagnostic ends up lying about the thing it was added to find.
            if (uViz == 1 || uViz == 3)
            {
                vec3 b = textureLod(uBloom, vUV, 0.0).rgb * uStrength * (uViz == 3 ? 20.0 : 1.0);
                frag = vec4(SowxEncode(b), 1.0);
                return;
            }
            if (uViz == 2) { frag = vec4(SowxEncode(texture(uHdr, vUV).rgb), 1.0); return; }
            // #388: the velocity target on the bloom unit. Mid-grey is zero motion; +-32 render pixels of
            // motion is a full swing of red (x) or green (y), so a pan paints one flat colour over static
            // geometry and standing still paints flat grey.
            if (uViz == 4) { frag = vec4(0.5 + textureLod(uBloom, vUV, 0.0).rg / 64.0, 0.5, 1.0); return; }

            vec3 c = texture(uHdr, vUV).rgb;
            if (uStrength > 0.0) c += textureLod(uBloom, vUV, 0.0).rgb * uStrength;
            c *= uExposure;
            if (uMode == 1) c = SowxShoulder(c, uKnee);
            // ACES against the SHOULDER, not the raw value: the shoulder is the identity below its knee, so
            // the mix keeps the darks the ACES toe would crush and takes only the roll-off and colour.
            else if (uMode == 2) c = mix(SowxShoulder(c, uKnee), SowxAces(c), uAcesMix);
            vec3 outc = SowxEncode(c);
            // Dithered in DISPLAY space, after the encode: this is where the frame meets 8 bits (or, when
            // the post stack takes it, uDither is 0 and the stack's last stage dithers instead).
            outc = SowxDither(outc, gl_FragCoord.xy, uDither);
            // The alpha channel passes through untouched: the Alloy blended classes wrote real coverage
            // there and the engine's own later passes are entitled to find it, tonemapping or not.
            frag = vec4(outc, texture(uHdr, vUV).a);
        }
        """;

    /// <summary>Off-device (run-demo.sh): the dial table is reachable and the shoulder really is the identity
    /// below its knee -- which is the whole parity argument, and the one property no compile check covers.
    /// Restores whatever it touched.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        // #175: the check DRIVES the tuning path, and tuning logs a line each. Silenced for the duration --
        // it used to be quiet only because Attach had not run yet and _log was still null, which is not a
        // property to depend on: the pass roll-call attaches every pass before it checks any of them.
        ExtenderLog? quiet = _log; _log = null;
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"PostChain.SelfCheck FAIL: {w}"); } }

        float bl = _bloom, th = _threshold, kn = _knee, tk = _tmKnee, di = _dither, vz = _viz, sc = _scale, bsc = _bootScale;
        bool mo = _motion, ta = _taa; float tb = _taaBlend;
        int md = _mode; bool on = _on; bool armed = Armed;
        float bb = _bootBloom, bt = _bootThreshold, bk = _bootKnee, btk = _bootTmKnee, bex = _bootExposure, ex = _exposure, bam = _bootAcesMix, am = _acesMix;
        int bm = _bootMode;
        try
        {
            E(float.IsNaN(Tune("nope", 1f)), "an unknown knob is reported");
            // hdr is CONFIG, not a /post key: it stands the whole Alloy frame down, which is a boot
            // decision. A Tune that still accepted it would be a live half-stand-down.
            E(float.IsNaN(Tune("hdr", 0f)), "hdr is not tunable; it is the boot setting");
            foreach (string key in new[] { "bloom", "threshold", "knee", "tonemap", "tmknee", "exposure", "acesmix",
                                          "dither", "viz", "scale", "motion", "taa", "taablend", "taau", "aotemporal" })
                E(!float.IsNaN(Tune(key, 1f)), $"knob \"{key}\" is reachable");
            // #389: TAA arms the motion pass and stands SMAA down; both toggle with the one key. The jitter
            // sequence is centred, one render pixel wide, and repeats -- and is exactly zero-width at no size.
            Tune("motion", 0f); Tune("viz", 0f); Tune("taa", 1f);
            E(TaaOn && MotionWanted && !SmaaStage.Active, "/post taa 1 arms the motion pass and stands SMAA down");
            Tune("taa", 0f); E(!TaaOn && !MotionWanted, "/post taa 0 disarms both together");
            float sx = 0f, sy = 0f, mx = 0f;
            for (int f = 0; f < JitterCount; f++)
            {
                Jitter(f, 1000, 500, out float jx, out float jy);
                sx += jx; sy += jy; mx = MathF.Max(mx, MathF.Max(MathF.Abs(jx) * 500f, MathF.Abs(jy) * 250f));
            }
            Jitter(0, 1000, 500, out float j0x, out float j0y); Jitter(JitterCount, 1000, 500, out float j8x, out float j8y);
            // Halton's first eight points are not exactly balanced (base 2 sums to 3.5625, a mean of -0.055
            // px): a mean under a tenth of a pixel is what this asserts, not zero.
            E(MathF.Abs(sx) * 500f / JitterCount < 0.1f && MathF.Abs(sy) * 250f / JitterCount < 0.1f, "the jitter sequence is centred within 0.1 px");
            E(mx <= 0.5f + 1e-4f, "...and stays within half a render pixel");
            E(j0x == j8x && j0y == j8y, "...and repeats every JitterCount frames");
            Tune("taablend", 0f); E(_taaBlend >= 0.02f, "taablend never reaches 0: a frozen history would never converge");
            Tune("taablend", 1f); E(_taaBlend == 0.5f, "...nor 1: that is the raw jitter with nothing averaging it");
            Tune("aotemporal", 0f); E(SsaoPass.Temporal == 0f, "/post aotemporal reaches the AO history weight");
            // #391: the scale is clamped at the command door too -- RenderCommands only refuses negatives,
            // and a 0x0 target is a refusal wearing a knob's name. And the sizes it produces never hit 0.
            Tune("scale", 0f); E(_scale == MinScale, "scale 0 clamps to the floor");
            Tune("scale", 9f); E(_scale == MaxScale, "scale 9 clamps to the ceiling");
            Tune("scale", 0.67f);
            E(RenderSize(2560) == 1715 && RenderSize(1) == 1, "render size rounds, never below one pixel");
            E(MathF.Abs(LodBias - MathF.Log2(0.67f)) < 1e-6f, "the lod bias is log2(scale) below 1");
            Tune("scale", 1.5f); E(LodBias == 0f, "...and 0 above it: supersampling keeps its detail");
            Tune("scale", 1f); E(RenderSize(2560) == 2560 && LodBias == 0f, "1.0 is pixel-identical");
            // #388: the viz runs the pass without arming it, and arming is what /post motion does.
            Tune("motion", 0f); Tune("viz", 0f); E(!MotionWanted, "motion off and no viz: the pass sits out");
            Tune("viz", VizMotion); E(MotionWanted, "viz 4 runs the pass unarmed");
            Tune("viz", 0f); Tune("motion", 1f); E(MotionWanted, "/post motion 1 arms it");
            E(Describe().Contains("bloom") && Describe().Contains("tonemap") && Describe().Contains("dither")
              && Describe().Contains("exposure") && Describe().Contains("scale") && Describe().Contains("motion") && Describe().Contains("taa"),
              "Describe names every knob group");
            E(ResolveFs.Contains("if (uViz == 4)") && ResolveFs.Contains("textureLod(uBloom, vUV, 0.0).rg"),
              "viz 4 paints the velocity target off the bloom unit");
            // Exposure is scene-referred: it multiplies the linear frame BEFORE the tonemap and the encode,
            // never the display value -- a gain after the encode is a gamma-wrong brightness slider.
            E(ResolveFs.IndexOf("c *= uExposure;", StringComparison.Ordinal)
              < ResolveFs.IndexOf("if (uMode == 1) c = SowxShoulder", StringComparison.Ordinal)
              && ResolveFs.IndexOf("c *= uExposure;", StringComparison.Ordinal)
              < ResolveFs.IndexOf("vec3 outc = SowxEncode(c);", StringComparison.Ordinal),
              "exposure is applied in linear, before the tonemap and the encode");
            E(ResolveFs.Contains("mix(SowxShoulder(c, uKnee), SowxAces(c), uAcesMix)"),
              "ACES is mixed against the shoulder, so the darks below the knee are never crushed past the mix");
            // The resolve is the ONLY place the Alloy frame meets 8-bit, so it is the only place a dither
            // can go. A resolve that lost it would band again, and the symptom (faint contour stripes over
            // smooth gradients) reads as a lighting bug rather than as a missing line here.
            E(PostDitherLib.Contains("(r1 + r2 - 1.0) * lsb") && ResolveFs.Contains("SowxDither(outc, gl_FragCoord.xy, uDither)"),
              "the resolve dithers with a TRIANGULAR pdf -- two samples summed, not one");
            E(ResolveFs.IndexOf("vec3 outc = SowxEncode(c);", StringComparison.Ordinal)
              < ResolveFs.IndexOf("SowxDither(outc, gl_FragCoord.xy, uDither)", StringComparison.Ordinal),
              "...and it dithers in DISPLAY space, after the encode -- dithering before it is shaped by the "
              + "curve and stops matching the quantisation step it exists to break up");
            // #132: this resolve is the frame's ONLY encode, and it is gated on the engine's own
            // GAMMA_CORRECTION. A resolve that read the HDR colour through a decode would be undoing an
            // encode nobody applies any more -- the #102 failure, whole-frame and in the other direction.
            E(!ResolveFs.Contains("SowxLinear") && !BloomDownFs.Contains("SowxLinear"),
              "nothing in the chain decodes: the target is linear");
            // ...and it is UNCONDITIONAL in the stage, because the gate lives in Precheck. A stage that
            // grew its own switch would be half a gate: the Alloy DECODES cannot be switched per pass, so
            // an encode that turned itself off would leave every Alloy surface a gamma dark beside the
            // engine's. The refusal is the only self-consistent answer, and this asserts the pairing.
            E(ResolveFs.Contains("vec3 outc = SowxEncode(c);") && !PostColorLib.Contains("uniform"),
              "the resolve's encode is unconditional -- the gamma gate is Precheck's refusal, not a switch");

            _bootBloom = 0.5f; _bootMode = Aces; _bootScale = 0.75f;
            ResetTuning();
            E(_bloom == 0.5f && _mode == Aces, "reset honours the BOOT values, not the hardcoded defaults");
            E(_scale == 0.75f && !_motion && !_taa, "reset restores the boot scale (renderscale) and disarms motion and TAA");
            // ...and clears the debug VIEW, which has no boot value. The loop above has just set viz to 1,
            // so this is the live case: without it /post reset cannot undo a stray /post viz.
            E(_viz == 0f, "reset turns the debug viz off");

            // The shoulder, in C#, mirroring the GLSL: identity below the knee, and never past 1.
            static float Shoulder(float c, float k)
            {
                if (k <= 0f || k >= 1f) return c;
                float m = 1f - k, t = MathF.Max(c - k, 0f);
                return MathF.Min(c, k) + m * t / (m + t);
            }
            for (float x = 0f; x <= 0.8f; x += 0.1f)
                E(MathF.Abs(Shoulder(x, 0.8f) - x) < 1e-5f, $"shoulder is the identity at {x:F1}");
            E(Shoulder(4f, 0.8f) < 1f && Shoulder(4f, 0.8f) > 0.9f, "shoulder compresses 4.0 into (0.9, 1)");
            E(Shoulder(100f, 0.8f) < 1f, "shoulder never reaches 1 however bright the input");
            E(Shoulder(1.5f, 0.8f) < Shoulder(3f, 0.8f), "shoulder stays monotonic, so detail survives it");
        }
        finally
        {
            // _viz with the rest, and it is the one that BIT: the loop above drives every knob including
            // "viz", nothing restored it, and nothing else on the boot path writes it -- so every session
            // came up in the bloom-debug view and the only way out was typing /post viz 0. The sentinel
            // below is what stops the next knob doing the same silently.
            const float Sentinel = -12345f;
            _bloom = Sentinel; _threshold = Sentinel; _knee = Sentinel; _tmKnee = Sentinel;
            _dither = Sentinel; _viz = Sentinel; _scale = Sentinel;
            _bloom = bl; _threshold = th; _knee = kn; _tmKnee = tk; _mode = md; _on = on;
            _dither = di; _viz = vz; _scale = sc; _bootScale = bsc; _motion = mo; _taa = ta; _taaBlend = tb;
            SsaoPass.ResetTuning();
            Armed = armed;
            _bootBloom = bb; _bootThreshold = bt; _bootKnee = bk; _bootTmKnee = btk; _bootExposure = bex; _exposure = ex; _bootAcesMix = bam; _acesMix = am;
            _bootMode = bm;
            E(_bloom != Sentinel && _threshold != Sentinel && _knee != Sentinel && _tmKnee != Sentinel
              && _dither != Sentinel && _viz != Sentinel && _scale != Sentinel,
              "the check restores EVERY knob it drove -- a missed one ships its test value as the boot value");
            _log = quiet;
        }
        log.Info(ok ? "PostChain.SelfCheck: passed" : "PostChain.SelfCheck: FAILED");
        return ok;
    }
}
