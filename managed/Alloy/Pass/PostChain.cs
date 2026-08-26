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
/// <para>The target shares the ENGINE's depth attachment rather than owning one. Depth blits cannot convert
/// format and the engine's attachment may be a renderbuffer of unknown internal format, so copying it is a
/// guess; sharing it is exact -- the prepass keeps publishing the same <see cref="DepthResource"/>, and the
/// engine's own draws after the bracket depth-test against the world the Alloy pass wrote, with no copy at
/// all. Only colour is ours, and only colour is composited back.</para>
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
/// (its depth cannot be attached to an FBO at all), a multisampled attachment beside our single-sample
/// colour texture, or an offset viewport.</para>
/// </summary>
internal static unsafe class PostChain
{
    /// <summary>Bloom chain depth ceiling. Six halvings from 1080p bottoms out at 8px, which is already
    /// wider than any bloom anyone wants; the real chain is the smaller of this and what the size allows.</summary>
    private const int MaxLevels = 6;
    /// <summary>Texture unit the resolve samples the HDR colour and the bloom chain on. Above everything the
    /// Alloy lit stage binds (the probe is the highest, at 9) -- the resolve runs in the same bracket and
    /// leaving a unit shared with a live sampler of a different type is INVALID_OPERATION.</summary>
    private const uint HdrUnit = 10, BloomUnit = 11;

    /// <summary>Tonemap operators. <see cref="Shoulder"/> is the default and the only one that leaves the
    /// midtones alone -- engine-drawn geometry shares this framebuffer and never goes through the resolve,
    /// so a curve that moved the midtones would make owned and engine parts visibly disagree.</summary>
    private const int Off = 0, Shoulder = 1, Aces = 2;

    /// <summary>The Alloy colour passes of THIS pass are drawing into the HDR target. Since #132 the owned
    /// frame does not run at all when this is false -- there is no un-resolved variant of it.</summary>
    internal static bool Armed { get; private set; }

    /// <summary>Resolves this session -- the [alloy] period line prints it. A number that stays 0 while the
    /// world renders is the "HDR never engaged, you are seeing the fallback" case, and the arm-time log line
    /// says which target refused.</summary>
    internal static int Resolves { get; private set; }

    private static bool _on = true;
    private static readonly StandDown _down = new("PostChain");
    private static ExtenderLog? _log;
    private static uint _fbo, _hdrTex, _bloomTex;
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
    private static int _mode = Shoulder;
    private static float _bootBloom, _bootThreshold, _bootKnee, _bootTmKnee, _bootDither;
    private static int _bootMode;

    private static int _dSrc, _dLod, _dTexel, _dFirst, _dThresh;
    private static int _uSrc, _uLod, _uTexel;
    private static int _cHdr, _cBloom, _cStrength, _cMode, _cKnee, _cDither, _cViz;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        // The one boot decision in this class: since #132 there is no Alloy frame without the resolve, so
        // hdr=0 is not a look toggle but a stand-down of the whole Alloy frame -- config, not a /post key.
        _on = Settings.Bool("hdr", true);
        // #152 is the precedent this whole policy generalises (ADR 0013): a debug visualization is a live
        // request, never a boot state -- a forgotten viz setting booted every session into the bloom-debug
        // view until the user typed /post viz 0. Every look dial below is now the same shape.
        _bootBloom = _bloom; _bootThreshold = _threshold; _bootKnee = _knee;
        _bootTmKnee = _tmKnee; _bootMode = _mode; _bootDither = _dither;
        _gammaParam = (delegate* unmanaged<uint, int>)Native.Sym(SymbolMap.RiGetGlobalShaderParami);
        if (_gammaParam == null)
            log.Info("PostChain: RenderInterface::GetGlobalShaderParami unresolved -- the resolve encodes "
                   + "unconditionally, which is what this client measures anyway (#102)");
        log.Info(_on ? $"PostChain: armed -- {Describe()}; /post retunes every dial live"
                     : "PostChain: config hdr=0 -- the Alloy frame stands down; the world renders vanilla");
    }


    internal static string Describe() =>
        $"hdr {(_on ? "on" : "off")}  bloom {_bloom:F3}  threshold {_threshold:F2}  knee {_knee:F2}  " +
        $"tonemap {(_mode == Off ? "off" : _mode == Aces ? "aces" : "shoulder")}  tmknee {_tmKnee:F2}  " +
        $"dither {_dither:F2}" + (_viz > 0f ? $"  VIZ {_viz:F0}" : "");

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
            case "dither":    old = _dither; _dither = v; break;
            case "viz":       old = _viz; _viz = v; break;
            default: return float.NaN;
        }
        _log?.Info($"PostChain: /post {key} {v:F2} (was {old:F2}) -> {Describe()}");
        return old;
    }

    internal static void ResetTuning()
    {
        _bloom = _bootBloom; _threshold = _bootThreshold; _knee = _bootKnee;
        _tmKnee = _bootTmKnee; _mode = _bootMode; _dither = _bootDither;
        // viz has no boot value and never should: it is a debug VIEW, not a look setting, so "reset" means
        // off. Leaving it out is why /post reset could not undo a stray /post viz either.
        _viz = 0f;
        _log?.Info($"PostChain: /post reset -> {Describe()}");
    }

    /// <summary>Drop every GL object (context loss / disarm). Names are meaningless once the context is
    /// gone, so this only forgets them -- it never deletes.</summary>
    internal static void Forget()
    {
        _fbo = 0; _hdrTex = 0; _bloomTex = 0; _w = 0; _h = 0; _levels = 0;
        _progDown = 0; _progUp = 0; _progComposite = 0; _vao = 0;
        _progFailed = false; Armed = false; _down.Forget();
    }

    /// <summary>#132: the refusals that are decidable WITHOUT the 4.6 bracket, asked in ENGINE context
    /// before the Alloy pass pays for its preamble. False stands the whole pass down to vanilla.
    /// <para>Split out of <see cref="Begin"/> for two reasons and both are load-bearing. It saves the
    /// preamble -- <c>AlloyLights.Upload</c>/<c>Build</c> and <c>PoseSkinned</c>, which force-solves every
    /// Alloy skinmesh through the engine -- on a pass that was never going to arm; on a client that renders
    /// to the default framebuffer that is EVERY frame, forever, to produce a vanilla one. And the gamma
    /// question can only be answered here: it gates the Alloy DECODES too, which live in the stages and
    /// cannot be switched per pass.</para>
    /// <para><b>The gamma gate is a refusal, not a switch.</b> Every Alloy stage linearises its texels
    /// unconditionally -- that is what makes the frame linear -- so a client whose <c>GAMMA_CORRECTION</c>
    /// is 0 (the engine passes texels through raw, <c>ApplyColorSpace</c> is the identity) has no
    /// self-consistent Alloy variant: encoding at the resolve would disagree with the engine, and not
    /// encoding would leave every Alloy surface a gamma dark beside it. Standing down is the honest
    /// answer, and it is re-asked per pass, so flipping the setting back recovers.</para></summary>
    internal static bool Precheck(int engFbo, int depthName, int* viewport)
    {
        // config hdr=0 is a deliberate setting and Attach already said so; it is not a refusal to report.
        if (!_on || _down.Failed) return false;
        // An offset viewport would break the resolve's 1:1 UV mapping, and the default framebuffer's depth
        // cannot be an FBO attachment at all. Both are refusals, not workarounds.
        if (engFbo == 0 || depthName == 0) return Refuse("the engine renders to the default framebuffer");
        if (viewport[0] != 0 || viewport[1] != 0) return Refuse("the engine's viewport is offset");
        if (_gammaParam != null && _gammaParam(GammaParam) == 0)
            return Refuse("the engine's gamma pipeline is off (GetGlobalShaderParami(0) == 0), and the "
                        + "Alloy stages have no un-linearised variant");
        return true;
    }

    /// <summary>Point the Alloy colour passes at the HDR target for this pass. #132: a false answer stands
    /// the whole Alloy frame down to vanilla, so this is asked BEFORE anything is staged -- there is no
    /// display-space variant of the Alloy stages left to draw with.
    /// <para><paramref name="mirrorFbo"/> is the caller's engine-FBO mirror, NOT the engine's own name:
    /// framebuffer objects do not share across contexts, which is why that mirror exists at all. It is only
    /// a completeness/refusal witness now; the target's own starting content comes from the engine's clear
    /// COLOUR rather than from a blit of its clear. At the #132 anchor the engine's colour buffer holds
    /// nothing but that clear (Camera::InitializeFrameBufferResources has just run Clear(7) and no draw has
    /// followed), and a blit would copy a display-space value into a linear target -- wrong by a gamma on
    /// every area that clears to something other than black.</para></summary>
    internal static bool Begin(uint mirrorFbo, int depthType, int depthName, int* viewport, float* clearRgba)
    {
        Armed = false;
        if (!_on || _down.Failed) return false;
        if (Gl.ClearColor == null || Gl.Clear == null || Gl.GenFramebuffers == null || Gl.FramebufferTexture2D == null)
            return StandDown("a required GL entry point is missing");
        if (!EnsurePrograms()) return false;
        if (!EnsureTargets(viewport[2], viewport[3])) return false;

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _hdrTex, 0);
        if (depthType == (int)Gl.RENDERBUFFER)
            Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.RENDERBUFFER, (uint)depthName);
        else Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, (uint)depthName, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, mirrorFbo);
            return Refuse(status == Gl.FRAMEBUFFER_INCOMPLETE_MULTISAMPLE
                ? "the engine's depth attachment is multisampled and the HDR colour texture is not"
                : $"the HDR framebuffer is incomplete (0x{status:x})");
        }

        // The engine's own clear colour, linearised: ground truth rather than a guess, and one clear
        // rather than a full-screen blit. Depth is the engine's attachment and the engine has just cleared
        // it -- clearing it again here would throw away the state the Alloy prepass is about to write into.
        // clearRgba is READ IN THE ENGINE CONTEXT by the caller and handed over. A GL share list shares
        // OBJECTS, never per-context state, and glClearColor is per-context: querying it here would return
        // whatever THIS context was last set to -- (0,0,0,0) until the first probe capture, and EnvProbe's
        // own clear colour ever after.
        Gl.ClearColor(MathF.Pow(MathF.Max(clearRgba[0], 0f), AlloyLights.SrgbGamma),
                      MathF.Pow(MathF.Max(clearRgba[1], 0f), AlloyLights.SrgbGamma),
                      MathF.Pow(MathF.Max(clearRgba[2], 0f), AlloyLights.SrgbGamma), clearRgba[3]);
        Gl.Clear(Gl.COLOR_BUFFER_BIT);
        // Left set: the only other consumer of this context's clear colour is EnvProbe, which sets its own
        // every capture (EnvProbe.Begin), so there is nothing here worth restoring.
        Armed = true;
        return true;
    }

    /// <summary>Say why the HDR target is not arming, once per distinct reason, and go on checking. NOT
    /// latched: the engine's target is a per-pass fact -- a reflection subframe or a GUI 3D panel can render
    /// to a different one than the world pass does -- so a session that refused once must still arm on the
    /// passes it can. The whole point of the line is that the fallback is otherwise silent: the frame
    /// renders, just without any of #139 in it.</summary>
    private static bool Refuse(string why)
        => _down.Refuse(_log, why, "the HDR target is not armed; #132: the Alloy frame stands down for those "
                                 + "passes and the engine renders them vanilla");

    /// <summary>...and the latching kind, for a failure that cannot come back: a stage that did not build,
    /// an entry point the driver does not have.</summary>
    private static bool StandDown(string why)
        => _down.Fail(_log, why, "the HDR target cannot arm this session; the engine renders vanilla");

    /// <summary>The bloom chain: progressive downsample, then a tent upsample back up, accumulating. Runs
    /// with the HDR target still bound as the read source and the chain's own framebuffer as the draw
    /// target, so the caller must rebind the engine target before <see cref="Composite"/>.</summary>
    internal static void Bloom()
    {
        _bloomOk = false;
        if (!Armed || _bloom <= 0f || _levels <= 0) return;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.Disable(Gl.DEPTH_TEST); Gl.DepthMask(0);
        Gl.Disable(Gl.BLEND);
        Gl.Disable(Gl.CULL_FACE); Gl.Disable(Gl.SCISSOR_TEST);
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

    /// <summary>Resolve the HDR target into whatever framebuffer the caller has bound: add the bloom,
    /// tonemap, encode. The caller has already rebound the engine target and its viewport.</summary>
    internal static void Composite(int* viewport)
    {
        if (!Armed) return;
        Gl.UseProgram(_progComposite);
        Gl.BindVertexArray(_vao);
        Gl.Disable(Gl.DEPTH_TEST); Gl.DepthMask(0);
        Gl.Disable(Gl.BLEND);
        Gl.Disable(Gl.CULL_FACE); Gl.Disable(Gl.SCISSOR_TEST);
        Gl.ActiveTexture(Gl.TEXTURE0 + HdrUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _hdrTex);
        Gl.ActiveTexture(Gl.TEXTURE0 + BloomUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _bloomOk ? _bloomTex : 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(_cHdr, (int)HdrUnit);
        Gl.Uniform1i(_cBloom, (int)BloomUnit);
        Gl.Uniform1f(_cStrength, _bloomOk ? _bloom : 0f);
        Gl.Uniform1i(_cMode, _mode);
        Gl.Uniform1f(_cKnee, _tmKnee);
        // ...in LSBs of the 8-bit target this writes into, converted here rather than in the shader so the
        // dial reads as "how many quantisation steps of noise", which is the only unit it makes sense in.
        Gl.Uniform1f(_cDither, _dither / 255f);
        Gl.Uniform1i(_cViz, (int)_viz);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        // Hand the depth state back the way the colour passes left it -- the probe capture may still be
        // ahead of us in another pass order, and the engine's own draws follow the bracket.
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthMask(1);
        Gl.BindVertexArray(0);
        Armed = false;
        Resolves++;
    }

    /// <summary>The one term the [alloy] period line carries: did the resolve run, and how often. Resets the
    /// counter, like every other period statistic.</summary>
    internal static string Stats(int frames)
    {
        int n = Resolves;
        Resolves = 0;
        if (!_on) return "off (config hdr=0)";
        return n == 0 ? $"NOT RESOLVING ({(_down.Reason.Length == 0 ? "never reached the pass" : _down.Reason)})"
                      : $"{(float)n / MathF.Max(frames, 1):F2} resolves/frame at {_w}x{_h}, {_levels}-level bloom";
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
        _log?.Info($"PostChain: HDR target {w}x{h} RGBA16F, {_levels}-level bloom chain, sharing the " +
                   "engine's depth attachment");
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
        if (_progComposite != 0 && !ShaderContract.ResolveSafe(resolveSrc))
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
        uniform float uDither;   // one LSB of the output target, or 0
        uniform int uViz;        // 0 = the frame; 1 = bloom only; 2 = HDR only; 3 = bloom x20

        """ + PostColorLib + """

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

        float SowxNoise(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

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

            vec3 c = texture(uHdr, vUV).rgb;
            if (uStrength > 0.0) c += textureLod(uBloom, vUV, 0.0).rgb * uStrength;
            if (uMode == 1) c = SowxShoulder(c, uKnee);
            else if (uMode == 2) c = SowxAces(c);
            vec3 outc = SowxEncode(c);
            // The whole Alloy frame is computed in RGBA16F and then written ONCE into the engine's 8-bit
            // attachment. Every gradient in it therefore quantises here, and a gradient smooth enough and
            // shallow enough -- which is precisely what a broad bloom halo is -- lands its steps as visible
            // contour bands rather than as noise. The bands are straight and evenly spaced because they are
            // iso-luminance contours of a smooth glow, which is why they read as "stripes" and why they
            // cross geometry, particles and empty background alike.
            //
            // TRIANGULAR PDF, not uniform: two independent samples summed. A uniform dither still leaves the
            // noise floor modulated by where the signal sits inside its quantisation step, so bands survive
            // faintly; the triangular sum decorrelates the error from the signal outright, and it is the
            // difference between "much better" and "gone". One LSB peak-to-peak is imperceptible on its own.
            if (uDither > 0.0)
            {
                float r1 = SowxNoise(gl_FragCoord.xy);
                float r2 = SowxNoise(gl_FragCoord.xy + vec2(17.31, 47.13));
                outc += (r1 + r2 - 1.0) * uDither;
            }
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

        float bl = _bloom, th = _threshold, kn = _knee, tk = _tmKnee, di = _dither, vz = _viz;
        int md = _mode; bool on = _on; bool armed = Armed;
        float bb = _bootBloom, bt = _bootThreshold, bk = _bootKnee, btk = _bootTmKnee;
        int bm = _bootMode;
        try
        {
            E(float.IsNaN(Tune("nope", 1f)), "an unknown knob is reported");
            // hdr is CONFIG, not a /post key: it stands the whole Alloy frame down, which is a boot
            // decision. A Tune that still accepted it would be a live half-stand-down.
            E(float.IsNaN(Tune("hdr", 0f)), "hdr is not tunable; it is the boot setting");
            foreach (string key in new[] { "bloom", "threshold", "knee", "tonemap", "tmknee",
                                          "dither", "viz" })
                E(!float.IsNaN(Tune(key, 1f)), $"knob \"{key}\" is reachable");
            E(Describe().Contains("bloom") && Describe().Contains("tonemap") && Describe().Contains("dither"),
              "Describe names every knob group");
            // The resolve is the ONLY place the Alloy frame meets 8-bit, so it is the only place a dither
            // can go. A resolve that lost it would band again, and the symptom (faint contour stripes over
            // smooth gradients) reads as a lighting bug rather than as a missing line here.
            E(ResolveFs.Contains("(r1 + r2 - 1.0) * uDither"),
              "the resolve dithers with a TRIANGULAR pdf -- two samples summed, not one");
            E(ResolveFs.IndexOf("vec3 outc = SowxEncode(c);", StringComparison.Ordinal)
              < ResolveFs.IndexOf("(r1 + r2 - 1.0) * uDither", StringComparison.Ordinal),
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

            _bootBloom = 0.5f; _bootMode = Aces;
            ResetTuning();
            E(_bloom == 0.5f && _mode == Aces, "reset honours the BOOT values, not the hardcoded defaults");
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
            _dither = Sentinel; _viz = Sentinel;
            _bloom = bl; _threshold = th; _knee = kn; _tmKnee = tk; _mode = md; _on = on;
            _dither = di; _viz = vz;
            Armed = armed;
            _bootBloom = bb; _bootThreshold = bt; _bootKnee = bk; _bootTmKnee = btk;
            _bootMode = bm;
            E(_bloom != Sentinel && _threshold != Sentinel && _knee != Sentinel && _tmKnee != Sentinel
              && _dither != Sentinel && _viz != Sentinel,
              "the check restores EVERY knob it drove -- a missed one ships its test value as the boot value");
            _log = quiet;
        }
        log.Info(ok ? "PostChain.SelfCheck: passed" : "PostChain.SelfCheck: FAILED");
        return ok;
    }
}
