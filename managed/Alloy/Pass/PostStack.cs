using System;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #375: the display-space post stack -- the ordered set of full-screen stages that run AFTER
/// <see cref="PostChain.Composite"/> has resolved the linear frame, on the 8-bit-referred image the engine's
/// own <c>fsfbpostpr</c> chain used to run on inside <c>Camera::DoFrameBufferEffects</c>. ADR 0020.
/// <para>Shape: the resolve writes into this class's target instead of the engine's whenever at least one
/// stage is live this frame; each stage reads the previous output on <see cref="TextureSlots.PostHdr"/>
/// (and the frame's depth on <see cref="TextureSlots.PostBloom"/>), draws the full-screen triangle into the
/// other ping-pong texture, and the LAST stage draws into the engine's target with the resolve's dither. A
/// stage that does not build is dropped from the stack with a log line; the frame keeps resolving without
/// it. Nothing here can stand the Alloy frame down -- there is always the resolve to fall back to.</para>
/// <para>The gamma stage is the first citizen because it is a correctness bug rather than a lost look: the
/// engine runs <c>inc_postpr_gam</c>'s <c>pow(c, Gamma/2.2)</c> whenever the client's gamma option is not
/// 2.2, and Alloy simply never applied it. Both the gate and the value are the engine's own globals --
/// <c>CClientOptions::SetGamma</c> @0x66b0f0 stores the option as float param 0x0b (the <c>Gamma</c>
/// uniform, <c>GLRender::ConnectShaderData</c>) and folds bit 0x10 into the option mask that
/// <c>ConfigureFBEffectPostProcessing</c> @0x66ad40 publishes as int param 2, the preamble's
/// <c>POSTPROCESSING_TYPES_ENABLED</c>. Read per frame, so the slider moves the Alloy frame live.</para>
/// </summary>
internal static unsafe class PostStack
{
    /// <summary><c>GetGlobalShaderParami(2)</c>: the client's post-effect option mask, <c>#define
    /// POSTPROCESSING_TYPES_ENABLED</c> in every stock shader preamble (LoadAndCompileSingleShader).</summary>
    private const uint PostMaskParam = 2;
    /// <summary><c>GetGlobalShaderParamf(0x0b)</c>: the gamma option, uploaded to stock programs as the
    /// <c>Gamma</c> uniform. Clamped to [0, 1000] by the setter.</summary>
    private const uint GammaParam = 0x0b;   // not-an-offset: engine shader-param index
    /// <summary><c>POSTPROCESSING_GAMMA 0x0010</c> in <c>inc_postpr.shd</c> -- the same value in both stock
    /// shader generations (base_shaders.bif and ovr.bif) and in sow-topdata's fork; SetGamma sets it iff
    /// |g - 2.2| &gt;= 9e-5.</summary>
    internal const int MaskGamma = 0x10;   // not-an-offset: engine FB-effect mask bit
    /// <summary><c>POSTPROCESSING_TOON 0x0020</c> -- the client's Toon video option; see
    /// <see cref="ToonStage"/> for the engine setter this was read off.</summary>
    internal const int MaskToon = ToonStage.MaskToon;
    /// <summary>The engine's own reference gamma: the stage is the identity at exactly this value.</summary>
    internal const float EngineGamma = 2.2f;

    private static ExtenderLog? _log;
    private static delegate* unmanaged<uint, int> _gspI;
    private static delegate* unmanaged<uint, float> _gspF;
    /// <summary><c>enableFBeffects</c>, the engine's console gate on the whole framebuffer-effect walk
    /// (<c>DoFrameBufferEffects</c> returns at once when it is 0). Null reads as on.</summary>
    private static int* _fbEffects;
    private static bool _on = true;
    /// <summary>#352: <c>Camera::m_nFramebufferEffectsEnabled</c>, whose bit 2 is the engine's own "the post
    /// program runs" answer. Null (unresolved) reads as off.</summary>
    private static uint* _fbMask;
    /// <summary>#352: <c>/post fbpost 0</c>, the A/B for the hosted content chain alone.</summary>
    private static bool _fbPostOn = true;
    /// <summary>Latched: the display-space target could not be completed on this context. Distinct from the
    /// <c>/post stack</c> bypass, which is a live choice; this clears only with the context.</summary>
    private static bool _targetBad;
    private static int _runs;

    /// <summary>One display-space stage. <see cref="Active"/> is asked once per frame from the engine's
    /// own globals; <see cref="Value"/> is the stage's single float uniform, <see cref="Param"/>.
    /// ponytail: one float per stage. A stage that needs more uploads its own in a switch on its name.</summary>
    private sealed class Stage
    {
        public required string Name, Resref, Fs;
        public string? Param;
        public required Func<bool> Active;
        public Func<float>? Value;
        /// <summary>A stage with passes of its own (the aa stage): asked at Begin whether it can run at
        /// this size, and in Run to draw its passes ahead of the stage draw.</summary>
        public delegate*<int, int, bool> Ready;
        public delegate*<uint, uint, int*, void> Prepare;
        public uint Prog;
        public int LocSrc, LocDepth, LocDither, LocParam;
        public bool Failed, Live;
    }

    /// <summary>The stack, in order. Adding a stage is one entry here, one resref in <see cref="ShaderStages"/>,
    /// one program in <c>check-shaders.py</c>.</summary>
    private static readonly Stage[] _stages =
    {
        new()
        {
            Name = "gamma", Resref = ShdGammaFs, Fs = GammaStageFs, Param = "uGamma",
            Active = static () => GammaActive(Mask()),
            Value = static () => Gamma(),
        },
        // The cel stage ahead of taa/aa: the ink and the band boundaries are what the anti-aliaser is
        // there to smooth, and the option's own users reach for it partly for the edges.
        new()
        {
            Name = "toon", Resref = ShdToonFs, Fs = ToonStage.StageFs,
            Active = static () => ToonActive(Mask()),
            Prepare = &ToonStage.Prepare,
        },
        // #352: the content's post chain where vanilla ran it -- after gamma and toon (inc_postpr's own order
        // with those bits stripped) and before the anti-aliaser, which wants the ink and CA in front of it.
        new()
        {
            Name = "fbpost", Resref = FbPostStage.ShdFs, Fs = FbPostStage.FbPostFs,
            Active = static () => FbPostActive(),
            Prepare = &FbPostStage.Prepare,
        },
        // #389: TAA ahead of SMAA in the table, exclusive with it in practice -- SmaaStage.Active is false
        // while TAA is on, so whichever runs is the last stage and writes the engine's target.
        new()
        {
            Name = "taa", Resref = ShdTaaFs, Fs = TaaStage.StageFs,
            Active = static () => PostChain.TaaOn,
            Ready = &TaaStage.Ready, Prepare = &TaaStage.Prepare,
        },
        new()
        {
            Name = "aa", Resref = SmaaStage.ShdBlendFs, Fs = SmaaStage.BlendStageFs,
            Active = static () => SmaaStage.Active,
            Ready = &SmaaStage.Ready, Prepare = &SmaaStage.Prepare,
        },
    };

    /// <summary>#352: the hosted-chain stage, the one the stack drops and rebuilds on its own.</summary>
    private static readonly Stage _fbpost = Array.Find(_stages, static s => s.Name == "fbpost")!;
    private static uint _fbo;
    private static uint _tex0, _tex1;
    private static int _w, _h;
    private static int _pending;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _gspI = (delegate* unmanaged<uint, int>)Native.Sym(SymbolMap.RiGetGlobalShaderParami);
        _gspF = (delegate* unmanaged<uint, float>)Native.Sym(SymbolMap.RiGetGlobalShaderParamf);
        _fbEffects = (int*)Native.Sym(SymbolMap.EnableFBEffects);
        _fbMask = (uint*)Native.Sym(SymbolMap.FbEffectsMask);
        FbPostStage.Attach(log);
        SmaaStage.Attach(log);
        TaaStage.Attach(log);
        ToonStage.Attach(log);
        if (_gspI == null || _gspF == null)
            log.Info("PostStack: the global shader param getters are unresolved -- every stage stays off");
        // ADR 0013: /knobs is where a client option this build serves or takes away has to be visible.
        KnobSurface.Note(static () => _gspI == null ? null
            : $"the client's Gamma video option (settings.tml graphics.gamma = {Gamma():F2}) is the Alloy post "
            + $"stack's (#375): {(_stages[0].Failed ? "the gamma stage did NOT build and is dropped -- the option is inert"
                                : _targetBad ? "the stack's target is incomplete on this device -- the option is inert"
                                : !_on ? "BYPASSED by /post stack 0"
                                : GammaActive(Mask()) ? "pow(c, g/2.2) runs after the resolve"
                                : "at 2.2 the engine skips the stage and so does Alloy")}");
        KnobSurface.Note(static () =>
            $"the client's Anti-Aliasing video option (settings.tml graphics.video.anti-aliasing.mode = {SmaaStage.Samples}) "
            + $"is the Alloy post stack's (#377): {(_stages[^1].Failed ? "the aa stage did NOT build and is dropped -- the option is inert"
                                                   : _targetBad ? "the stack's target is incomplete on this device -- the option is inert"
                                                   : !_on ? "BYPASSED by /post stack 0"
                                                   : SmaaStage.Active ? $"runs after the resolve {SmaaStage.Describe()}"
                                                   : $"off {SmaaStage.Describe()}")}");
        KnobSurface.Note(static () => _gspI == null ? null
            : "the client's Toon video option and its three sliders (settings.tml graphics.fbo.toon.enabled, "
            + ".depth-edge-threshold, .color-edge-threshold, .edge-hardness) are the Alloy post stack's cel "
            + $"stage: {(_stages[1].Failed ? "the toon stage did NOT build and is dropped -- the option is inert"
                         : _targetBad ? "the stack's target is incomplete on this device -- the option is inert"
                         : !_on ? "BYPASSED by /post stack 0"
                         : ToonActive(Mask()) ? "on -- " + ToonStage.Describe()
                         : "off -- " + ToonStage.Describe() + " (/post toonbands, toonink, toonedge)")}");
        KnobSurface.Note(static () => _fbMask == null ? "the engine's post program (fsfbpostpr, the content's inc_postpr chain) "
            + "is NOT hosted: Camera::m_nFramebufferEffectsEnabled is unresolved (#352)"
            : "the engine's post program (fsfbpostpr: the content's inc_postpr chain -- status overlay, grade, noir, "
            + "rays, flare; plus the client's Sharpen and Vibrance options) is the Alloy post stack's (#352): "
            + $"{(_fbpost.Failed ? "the fbpost stage is DOWN -- inc_postpr is not in this module's chain, or the spliced text did not build; the chain is off under Alloy"
                : _targetBad ? "the stack's target is incomplete on this device -- the chain is off"
                : !_on ? "BYPASSED by /post stack 0"
                : !_fbPostOn ? "BYPASSED by /post fbpost 0"
                : FbPostActive() ? $"hosted after the resolve (mask 0x{FbPostStage.BuildMask:x} of the client's 0x{Mask():x}; "
                                   + "depth of field and dynamic contrast are NOT hosted -- their stock text has a vertex half)"
                : "off -- the engine would not run it either (option mask 0 and run-shader-even-with-no-effects off)")}");
        log.Info($"PostStack: attached -- {Describe()}");
    }

    private static int Mask() => _gspI == null ? 0 : _gspI(PostMaskParam);
    private static float Gamma() => _gspF == null ? EngineGamma : _gspF(GammaParam);
    /// <summary>The gamma stage's own bit of the option mask -- pure, so the self-check can assert it.</summary>
    internal static bool GammaBit(int mask) => (mask & MaskGamma) != 0;
    /// <summary>The gamma stage's gate, the engine's: the option-mask bit, under the console gate.</summary>
    private static bool GammaActive(int mask) => GammaBit(mask) && (_fbEffects == null || *_fbEffects != 0);
    /// <summary>The cel stage's gate, the engine's: the Toon option bit, under the same console gate.</summary>
    private static bool ToonActive(int mask) => (mask & MaskToon) != 0 && (_fbEffects == null || *_fbEffects != 0);
    /// <summary>#352: the hosted chain's gate, the engine's: effect 2's bit, under the console gate.</summary>
    private static bool FbPostActive()
        => _fbPostOn && _fbMask != null && (*_fbMask & FbPostStage.EffectPost) != 0 && (_fbEffects == null || *_fbEffects != 0);
    /// <summary>The client's shader quality option (int param 7), the preamble's <c>SHADER_QUALITY_MODE</c>.</summary>
    private static int Quality() => _gspI == null ? 0 : _gspI(FbPostStage.QualityParam);

    internal static string Describe()
    {
        int mask = Mask();
        string s = $"stack {(_targetBad ? "STOOD DOWN (incomplete target)" : _on ? "on" : "BYPASSED")}  mask 0x{mask:x}";
        foreach (Stage st in _stages)
            s += $"  {st.Name} {(st.Failed ? "FAILED" : st.Active() ? "live" : "idle")}"
               + (st.Name == "gamma" ? $" (g {Gamma():F2})" : st.Name == "aa" ? " " + SmaaStage.Describe()
                  : st.Name == "toon" ? " (" + ToonStage.Describe() + ")"
                  : st.Name == "fbpost" ? $" ({(FbPostStage.BuildMask < 0 ? "unbuilt" : $"mask 0x{FbPostStage.BuildMask:x}")}{(_fbPostOn ? "" : ", /post fbpost 0")})" : "");
        return s;
    }

    /// <summary>Behind <c>/post</c>, through <see cref="PostChain.Tune"/>'s fallthrough: <c>stack 0</c> bypasses
    /// every stage for a live A/B against the bare resolve. Returns NaN for an unknown key.</summary>
    internal static float Tune(string key, float v)
    {
        if (key == "fbpost")
        {
            float was = _fbPostOn ? 1f : 0f;
            _fbPostOn = v != 0f;
            _log?.Info($"PostStack: /post fbpost {v:F0} (was {was:F0}) -> {Describe()}");
            return was;
        }
        if (key != "stack") return ToonStage.Tune(key, v);
        float old = _on ? 1f : 0f;
        _on = v != 0f;
        _log?.Info($"PostStack: /post stack {v:F0} (was {old:F0}) -> {Describe()}");
        return old;
    }

    internal static void ResetTuning() { _on = true; _fbPostOn = true; ToonStage.ResetTuning(); }

    /// <summary>Context loss: forget every GL name.</summary>
    internal static void Forget()
    {
        _fbo = 0; _tex0 = 0; _tex1 = 0; _w = 0; _h = 0; _pending = 0; _targetBad = false;
        foreach (Stage st in _stages) { st.Prog = 0; st.Failed = false; st.Live = false; }
        FbPostStage.Forget();
        SmaaStage.Forget();
        TaaStage.Forget();
        ToonStage.Forget();
    }

    /// <summary>Decide this frame's stack and, if any stage is live, hand back the framebuffer the resolve's
    /// colour should go into (its first ping-pong texture attached). 0 = no stage: the resolve draws into
    /// the engine's target as before. Binds its own framebuffer to attach, so the caller rebinds the
    /// engine's target after it -- which the depth hand-back needs anyway.
    /// <para>#352: the hosted chain is built against the option mask as the engine's preamble is, so a moved
    /// option drops it here to rebuild on this frame (the engine reloads its shaders on the same change).</para></summary>
    internal static uint Begin(int w, int h) => Begin(w, h, w, h);

    /// <summary>Is this frame's stack upsampling: the resolve and every stage but the last at render size,
    /// the taa stage's history and its write at display size. Decided in <see cref="Begin"/>.</summary>
    internal static bool Upsampling { get; private set; }

    /// <summary>...with the render size apart from the display size. The stages are asked about the DISPLAY
    /// size (the taa history lives there); the ping-pong is at render size only when the taa stage is live
    /// and the dial is on, so a frame whose taa stage is not ready falls back to the bilinear upscale.</summary>
    internal static uint Begin(int rw, int rh, int dw, int dh)
    {
        _pending = 0; Upsampling = false;
        if (!_on || _targetBad || _gspI == null) return 0;
        if (FbPostStage.Stale(Mask(), Quality())) Drop(_fbpost, "the client's post options changed");
        bool taaLive = false;
        foreach (Stage st in _stages)
        {
            if (st.Prog == 0 && !st.Failed) Build(st);
            st.Live = !st.Failed && st.Prog != 0 && st.Active() && (st.Ready == null || st.Ready(dw, dh));
            if (st.Live) { _pending++; if (st.Name == "taa") taaLive = true; }
        }
        if (_pending == 0) return 0;
        Upsampling = taaLive && PostChain.TaaUpsample && (rw != dw || rh != dh);
        if (!EnsureTargets(Upsampling ? rw : dw, Upsampling ? rh : dh)) { _pending = 0; Upsampling = false; return 0; }
        AttachWrite(_tex0);
        return _fbo;
    }

    /// <summary>Bind the stack's framebuffer with <paramref name="tex"/> as its colour target.</summary>
    private static void AttachWrite(uint tex)
    {
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, tex, 0);
    }

    /// <summary>Run the live stages in order over the resolved frame. Every stage but the last draws into
    /// the other ping-pong texture; the last rebinds the engine's target (which the resolve would have
    /// written into) and carries the resolve's dither, so the frame is quantised exactly once.</summary>
    internal static void Run(int* viewport) => Run(viewport, viewport);

    /// <summary>...with every stage but the last drawing at <paramref name="stageVp"/> (the render viewport
    /// when upsampling) and the last at <paramref name="viewport"/>, the display.</summary>
    internal static void Run(int* viewport, int* stageVp)
    {
        if (_pending == 0) return;
        Gl.BindVertexArray(PostChain.FullscreenVao);
        new PassState(depth: false, depthWrite: false).Apply();
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.PostBloom);
        Gl.BindTexture(Gl.TEXTURE_2D, PostChain.DepthTex);
        uint read = _tex0, write = _tex1;
        int left = _pending;
        foreach (Stage st in _stages)
        {
            if (!st.Live) continue;
            bool last = --left == 0;
            int* v = last ? viewport : stageVp;
            if (st.Prepare != null) st.Prepare(read, st.Prog, v);
            if (last) AlloyRenderer.RebindFrameTarget();
            else AttachWrite(write);
            Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.PostHdr);
            Gl.BindTexture(Gl.TEXTURE_2D, read);
            Gl.UseProgram(st.Prog);
            Gl.Uniform1i(st.LocSrc, (int)TextureSlots.PostHdr);
            Gl.Uniform1i(st.LocDepth, (int)TextureSlots.PostBloom);
            Gl.Uniform1f(st.LocDither, last ? PostChain.DitherLsb : 0f);
            if (st.Value != null) Gl.Uniform1f(st.LocParam, st.Value());
            Gl.Viewport(v[0], v[1], v[2], v[3]);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
            if (st.Prepare != null) Gl.BindSampler(TextureSlots.PostHdr, 0);
            (read, write) = (write, read);
        }
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthMask(1);
        Gl.BindVertexArray(0);
        _pending = 0;
        _runs++;
    }

    /// <summary>The [alloy] period term: how often the stack ran, and which stages are live now.</summary>
    internal static string Stats(int frames)
    {
        int n = _runs; _runs = 0;
        if (_targetBad) return "poststack STOOD DOWN (incomplete target)";
        if (!_on) return "poststack BYPASSED (/post stack 1)";
        if (n == 0) return "poststack idle";
        string live = "";
        foreach (Stage st in _stages) if (st.Live || st.Active()) live += (live.Length == 0 ? "" : "+") + st.Name;
        return $"poststack {(float)n / MathF.Max(frames, 1):F2} runs/frame ({live})";
    }

    private static bool EnsureTargets(int w, int h)
    {
        if (_tex0 != 0 && w == _w && h == _h) return true;
        if (w <= 0 || h <= 0) return false;
        if (_tex0 != 0 && Gl.DeleteTextures != null)
        {
            uint* old = stackalloc uint[2] { _tex0, _tex1 };
            Gl.DeleteTextures(2, old);
            _tex0 = 0; _tex1 = 0;
        }
        uint* t = stackalloc uint[2];
        Gl.GenTextures(2, t);
        // RGBA16F for precision, not headroom (ADR 0020): the frame quantises to 8 bits once, at the last write.
        RenderTarget.AllocColor(t[0], true, w, h, Gl.NEAREST);
        RenderTarget.AllocColor(t[1], true, w, h, Gl.NEAREST);
        _tex0 = t[0]; _tex1 = t[1]; _w = w; _h = h;
        if (_fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _fbo = f; }
        AttachWrite(_tex0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _log?.Info($"PostStack: the display-space target is incomplete (0x{status:x}) -- the stack stands "
                     + "down on this context; the resolve writes straight to the engine's target");
            _targetBad = true;
            return false;
        }
        _log?.Info($"PostStack: display-space target pair {w}x{h} RGBA16F");
        return true;
    }

    /// <summary>Build one stage, on its first frame and again after <see cref="Drop"/>. A stage that fails --
    /// or whose override breaks the display-space contract and whose embedded text then fails too -- is
    /// dropped with a named line until the context or the module changes; the stack runs the rest and the
    /// frame keeps its resolve. That is the whole stand-down.</summary>
    private static void Build(Stage st)
    {
        string vs = ShaderStages.Source(PostChain.ShdPostVs), fs = ShaderStages.Source(st.Resref);
        if (st == _fbpost)
        {
            // #352 AC: a chain with no inc_postpr (any non-SoW module, or vanillabridge=0) would compile the
            // pass-through stub and run a no-op draw every frame while reporting itself hosted. Splice first;
            // an untouched text means every region missed, and that is a stand-down, not a stage.
            FbPostStage.Arm(Mask(), Quality());
            string spliced = VanillaShader.Splice(fs, _log, "PostStack fbpost");
            if (spliced == fs)
            {
                st.Failed = true;
                _log?.Info("PostStack: the fbpost stage stands DOWN -- inc_postpr is not in this module's resource "
                         + "chain (or vanillabridge=0); nothing to host, so the stack runs without it");
                return;
            }
            fs = spliced;
        }
        st.Prog = GlProgram.Graphics(PostChain.ShdPostVs, vs, st.Resref, fs,
                                     _log, $"PostStack {st.Name}", out _, out string src);
        if (st.Prog != 0 && !(ShaderContract.DisplaySafe(src) && ShaderContract.SlotSafe(src, "uSrc")))
        {
            Gl.DeleteProgram(st.Prog);
            _log?.Info($"PostStack: the {st.Name} override is refused -- it converts colour space (the stack "
                     + "is display space end to end) or dropped uSrc. Rebuilding from the embedded default.");
            st.Prog = GlProgram.Graphics(PostChain.FullscreenVs, PostStageHead + st.Fs + PostStageMain, _log,
                                         $"PostStack {st.Name} (embedded)");
        }
        if (st.Prog == 0)
        {
            st.Failed = true;
            _log?.Info($"PostStack: the {st.Name} stage did not build -- DROPPED from the stack; the frame "
                     + "resolves without it (bloom + resolve only, never vanilla)");
            return;
        }
        st.LocSrc = GlProgram.Loc(st.Prog, "uSrc"); st.LocDepth = GlProgram.Loc(st.Prog, "uDepth");
        st.LocDither = GlProgram.Loc(st.Prog, "uDither");
        st.LocParam = st.Param == null ? -1 : GlProgram.Loc(st.Prog, st.Param);
    }

    /// <summary>Forget a stage's program and its failure latch, so the next <see cref="Begin"/> rebuilds it.
    /// Inside the bracket (GL is current); the module-unload edge only sets the renderer's flag.</summary>
    private static void Drop(Stage st, string why)
    {
        if (st.Prog != 0) Gl.DeleteProgram(st.Prog);
        st.Prog = 0; st.Failed = false; st.Live = false;
        if (st == _fbpost) FbPostStage.DropProgram();
        _log?.Info($"PostStack: the {st.Name} stage is dropped to rebuild -- {why}");
    }

    /// <summary>#352: from <c>AlloyRenderer.DropSpliced</c> on the module-unload edge: the hosted chain is
    /// spliced from the resource chain, so it has a module lifetime like the lit program's regions.</summary>
    internal static void DropSpliced()
    {
        if (_fbpost.Prog != 0 || _fbpost.Failed) Drop(_fbpost, "module changed, rebuilds from this module's resource chain");
    }

    /// <summary>The gamma stage's resref (#92 override point), same rules as every post-chain stage.</summary>
    internal const string ShdGammaFs = "cx_postgam_fs";
    /// <summary>The cel stage's resref (#92 override point), same rules as every post-chain stage.</summary>
    internal const string ShdToonFs = "cx_toon_fs";
    /// <summary>#389: the taa stage's resref (#92 override point, like every stack stage). Only the copy-out of
    /// the history is overridable here; the accumulator (<see cref="TaaStage.TaaFs"/>) is embedded.</summary>
    internal const string ShdTaaFs = "cx_taa_fs";

    /// <summary>What every stack stage sees: the previous output, the frame's depth, and the dither the
    /// last stage owes the 8-bit target. Display space in, display space out -- no stage converts.</summary>
    internal const string PostStageHead = """
        #version 460 core
        in vec2 vUV;
        out vec4 frag;
        uniform sampler2D uSrc;      // the previous stage's output (or the resolve's), DISPLAY space
        uniform sampler2D uDepth;    // the frame's depth, for a stage that wants it
        uniform float uDither;       // one LSB of the 8-bit target when this stage writes it, else 0

        """ + PostChain.PostDitherLib + """

        """;

    /// <summary>The engine's <c>inc_postpr_gam.shd</c>, verbatim in its maths: <c>pow(Color.rgb,
    /// vec3(Gamma/2.2))</c>. Alpha passes through, as the stock stage leaves it.</summary>
    internal const string GammaStageFs = """
        uniform float uGamma;        // the client's gamma option, the engine's global float param 0x0b
        vec4 SowxStage(vec4 c)
        {
            c.rgb = pow(max(c.rgb, vec3(0.0)), vec3(uGamma / 2.2));
            return c;
        }
        """;

    /// <summary>The shared main: stage, then the dither -- AFTER the stage so the noise lands on the value
    /// that is about to quantise, which is the only place it breaks banding up.</summary>
    internal const string PostStageMain = """

        void main()
        {
            vec4 c = SowxStage(texture(uSrc, vUV));
            frag = vec4(SowxDither(c.rgb, gl_FragCoord.xy, uDither), c.a);
        }
        """;

    /// <summary>Off-device (run-demo.sh): the stage table composes, the gamma stage IS the engine's maths,
    /// its gate is the engine's mask bit, and the stack's tuning route through <c>/post</c> is reachable.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        ExtenderLog? quiet = _log; _log = null;
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"PostStack.SelfCheck FAIL: {w}"); } }
        try
        {
            foreach (Stage st in _stages)
            {
                string src = ShaderStages.Source(st.Resref);
                E(src.Length > 0 && st.Resref.Length <= ShaderResource.ResRefBytes, $"{st.Name}: resref composes");
                E(src.Contains("vec4 SowxStage(vec4 c)"), $"{st.Name}: declares SowxStage");
                E(ShaderContract.DisplaySafe(src) && ShaderContract.SlotSafe(src, "uSrc"),
                  $"{st.Name}: display space end to end, and reads uSrc");
                if (st.Param != null) E(src.Contains($"uniform float {st.Param};"), $"{st.Name}: declares its param {st.Param}");
                E(src.IndexOf("SowxStage(texture(uSrc, vUV))", StringComparison.Ordinal)
                  < src.IndexOf("SowxDither(c.rgb", StringComparison.Ordinal), $"{st.Name}: dithers AFTER the stage");
            }
            // The whole point: the same expression as inc_postpr_gam.shd, exponent Gamma/2.2, so a client at
            // 2.2 is the identity and every other value moves the Alloy frame exactly as it moves vanilla's.
            E(GammaStageFs.Contains("vec3(uGamma / 2.2)"), "gamma is pow(c, g/2.2), the stock stage's exponent");
            E(MaskGamma == 0x10, "POSTPROCESSING_GAMMA is 0x10 (inc_postpr.shd)");
            E(GammaBit(MaskGamma) && !GammaBit(MaskGamma - 1) && !GammaBit(0), "the gate is the mask bit");
            E(MathF.Abs(MathF.Pow(0.5f, EngineGamma / 2.2f) - 0.5f) < 1e-6f, "2.2 is the identity");
            E(float.IsNaN(Tune("nope", 1f)), "an unknown knob is reported");
            E(!float.IsNaN(PostChain.Tune("stack", 0f)) && !_on, "/post stack reaches the stack through PostChain");
            PostChain.ResetTuning();
            E(_on, "/post reset turns the bypass off");
            E(PostChain.Describe().Contains("stack"), "/post reports the stack");
            E(MaskToon == 0x20, "POSTPROCESSING_TOON is 0x20 (inc_postpr.shd)");
            E(_stages[1].Name == "toon" && !ToonActive(0) && ToonActive(MaskToon), "the toon stage's gate is the option bit");
            E(!float.IsNaN(PostChain.Tune("toonbands", 4f)), "/post toonbands reaches the toon stage through PostChain");
            E(ToonStage.SelfCheck(log), "the toon stage's own checks");
            E(_fbpost == _stages[2] && _fbpost.Prepare != null, "the hosted chain is stage 2, after gamma and toon, with its uploads");
            E(FbPostStage.SelfCheck(log), "the hosted chain's own checks");
            E(VanillaShader.Splice(FbPostStage.FbPostFs, null, "x", fromContent: false) == FbPostStage.FbPostFs,
              "a chain with no inc_postpr leaves the text untouched, which is what Build reads as the stand-down");
            E(!float.IsNaN(PostChain.Tune("fbpost", 0f)) && !_fbPostOn, "/post fbpost reaches the stack through PostChain");
            PostChain.ResetTuning();
            E(_fbPostOn, "/post reset turns the fbpost bypass off");
            E(SmaaStage.SelfCheck(log), "the aa stage's own checks");
            E(TaaStage.SelfCheck(log), "the taa stage's own checks");
            E(_stages[^1].Name == "aa", "aa is the LAST stage: it smooths what the frame actually shows");
            // #389: ...and TAA is exclusive with it, so it is last whenever it is live -- its stage draw is
            // the dithered write and the history never carries the dither.
            E(_stages[^2].Name == "taa", "taa sits right before aa, and the two are never live together");
            float taa = PostChain.Tune("taa", 1f);
            E(!SmaaStage.Active && _stages[^2].Active(), "with /post taa 1 the aa stage is idle and the taa stage live");
            PostChain.Tune("taa", taa);
        }
        finally { _log = quiet; _on = true; }
        log.Info(ok ? $"PostStack.SelfCheck: passed ({_stages.Length} stage(s))" : "PostStack.SelfCheck: FAILED");
        return ok;
    }
}
