using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Screen-space ambient occlusion as a LIGHTING INPUT, not a post stage (ADR 0021). The client's
/// SSAO option (settings.tml <c>graphics.fbo.ssao.enabled</c>) is bit 0 of the static
/// <c>Camera::m_nFramebufferEffectsEnabled</c> (Camera::SetFBEffectEnabled @0x5a6500, FramebufferEffect 0);
/// vanilla runs its pass over the finished frame and darkens everything in it, direct light included.
/// Alloy computes visibility from the depth prepass BEFORE the colour pass, horizon-based (GTAO, Jimenez
/// 2016: per screen-space slice the two max horizons, integrated against the projected normal), and the lit
/// stage scales its AMBIENT term by it -- beside the material's own AO map (#146). <c>/ao direct</c> (1 by
/// default) carries the same factor onto the direct terms: ambient-only is the physically right rule and is
/// invisible in every dark-ambient area, which is every interior (ADR 0021, amended).
/// The option's <c>ssao_intensity</c> (float param 1, default 0.5) scales the strength.</summary>
internal static unsafe class SsaoPass
{
    private static ExtenderLog? _log;
    /// <summary><c>Camera::m_nFramebufferEffectsEnabled</c>: bit 0 is SSAO.</summary>
    private static uint* _effects;
    /// <summary>The <c>enableFBeffects</c> console gate, the engine's outer condition on every FB effect.</summary>
    private static int* _fbEffects;
    /// <summary>The <c>ssao_intensity</c> config global, 0.5 stock. Not global float param 1: that copy is
    /// made by Camera::SetFrameBufferParameters @0x5a6d10 inside the frame loop Alloy owns, so under Alloy it
    /// reads 0 (live: "x0,00 from ssao_intensity", no occlusion at all).</summary>
    private static float* _intensity;
    internal const uint EffectSsao = 1u << 0;
    private const float StockIntensity = 0.5f;

    private static uint _fbo, _raw, _out, _prog, _progBlur;
    /// <summary>#390: the visibility history pair, RG16F (visibility, view distance), and its stage.</summary>
    private static uint _hist0, _hist1, _progTemporal;
    private static int _locDepth, _locProj, _locRt, _locRadius, _locPower, _locDirs, _locSteps, _locMaxPx, _locFrame,
                       _locBlurSrc, _locBlurDepth, _locBlurProj,
                       _locTRaw, _locTHist, _locTVel, _locTWeight, _locTDepth, _locTProj;
    private static int _w, _h, _histCur;
    /// <summary>The DISPLAY-space extent the targets were sized against, for the arm-time line and the stats
    /// term: <see cref="_w"/> x <see cref="_h"/> is half of it under <c>post.halfres</c>.</summary>
    private static int _srcW, _srcH;
    private static bool _built, _failed, _histValid;
    private static int _runs, _temporalRuns;
    /// <summary>True from this frame's pass until the next: the lit stage may read the texture.</summary>
    internal static bool Live { get; private set; }
    private static uint _liveOut;

    /// <summary>The lit stage's <c>uAoDirect</c>, read at every lit program bind.</summary>
    internal static float Direct => Ao.Direct.V;
    /// <summary><c>post.halfres</c>: the pass runs at half the frame's extent (a quarter of the pixels) and
    /// the lit stage upsamples it depth-aware. The estimate is coarser per frame, which is exactly what the
    /// temporal accumulator and TAA are there to average. Ships OFF: on a fragment-bound frame the lit
    /// stage's 4-tap upsample costs more than the quarter-size pass saves (review 2026-09-18).</summary>
    internal static bool Wanted => Post.HalfRes.V;
    /// <summary>...as it actually resolved THIS frame: what the lit stage's <c>uSsaoHalf</c> carries.</summary>
    internal static bool Half { get; private set; }
    /// <summary>#390: does this pass want the velocity target this frame? The motion pass runs ahead of it when so.</summary>
    internal static bool TemporalWanted => Active && Ao.Temporal.V > 0f;
    /// <summary><c>ao.temporal</c>'s Changed: the history was accumulated at another weight.</summary>
    internal static void DropHistory() => _histValid = false;

    internal const string ShdSsaoFs = "cx_ssao_fs", ShdSsaoBlurFs = "cx_ssaoblur_fs";

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _effects = (uint*)Native.Sym(SymbolMap.FbEffectsMask);
        _fbEffects = (int*)Native.Sym(SymbolMap.EnableFBEffects);
        _intensity = (float*)Native.Sym(SymbolMap.SsaoIntensity);
        if (_effects == null) log.Info("SsaoPass: Camera::m_nFramebufferEffectsEnabled is unresolved -- the SSAO option stays inert");
        KnobSurface.Note(static () => _effects == null ? null
            : $"the client's SSAO video option (settings.tml graphics.fbo.ssao.enabled = {(OptionOn ? 1 : 0)}) is the Alloy "
            + $"lit pass's (#379): {(_failed ? "the pass did NOT build -- the option is inert"
                                     : !Ao.On.V ? "BYPASSED by /ao on 0"
                                     : OptionOn ? "horizon occlusion from the depth prepass scales the ambient term"
                                     : "off")}");
        log.Info($"SsaoPass: attached -- {Describe()}");
    }

    /// <summary>The option's bit under the console gate, the engine's own condition for the effect.</summary>
    private static bool OptionOn => _effects != null && (*_effects & EffectSsao) != 0 && (_fbEffects == null || *_fbEffects != 0);
    internal static bool Active => Ao.On.V && !_failed && OptionOn;
    /// <summary>The option's intensity as a multiplier on the exponent: stock 0.5 is 1x; an unresolved or
    /// zeroed config reads as stock rather than as "no occlusion".</summary>
    private static float Strength
    {
        get { float i = _intensity == null ? StockIntensity : *_intensity; return (i > 0f ? i : StockIntensity) / StockIntensity; }
    }

    internal static string Describe()
        => $"ao {(_failed ? "FAILED" : !Ao.On.V ? "BYPASSED" : OptionOn ? "live" : "idle (option off)")}  radius {Ao.Radius.V:F2}  "
         + $"strength {Ao.Strength.V:F2} (x{Strength:F2} from ssao_intensity)  dirs {Ao.Dirs.V}  steps {Ao.Steps.V}  maxpx {Ao.MaxPx.V:F0}  blur {(Ao.Blur.V ? 1 : 0)}  direct {Ao.Direct.V:F2}  "
         + $"temporal {Ao.Temporal.V:F2}{(Ao.Temporal.V > 0f ? "" : " (single-frame; today's look is temporal 0 dirs 3 steps 3)")}  "
         + $"halfres {(Post.HalfRes.V ? "on" : "off")}";

    internal static string Stats(int frames)
    {
        int n = _runs, t = _temporalRuns; _runs = 0; _temporalRuns = 0;
        return _failed ? "ao FAILED" : n == 0 ? "ao idle"
             : $"ao {(float)n / MathF.Max(frames, 1):F2} runs/frame at {_w}x{_h}{(Half ? $" (half of {_srcW}x{_srcH})" : "")}"
             + (t == 0 ? "" : $" ({(float)t / MathF.Max(frames, 1):F2} temporal)");
    }

    /// <summary>Context loss: forget every GL name.</summary>
    internal static void Forget()
    {
        _fbo = 0; _raw = 0; _out = 0; _liveOut = 0; _prog = 0; _progBlur = 0; _hist0 = 0; _hist1 = 0; _progTemporal = 0;
        _w = 0; _h = 0; _srcW = 0; _srcH = 0; _built = false; _failed = false; _histValid = false; Live = false; Half = false;
    }

    /// <summary>World edge: the history is another area's; the next temporal pass takes the frame whole.</summary>
    internal static void Revoke() => _histValid = false;

    /// <summary>The pass, between the depth prepass and the colour pass: visibility into the raw target, the
    /// depth-aware 4x4 blur into the one the lit stage reads on <see cref="TextureSlots.Ssao"/>. Restores the
    /// frame's framebuffer and viewport. Hot: allocates nothing after the first frame at a size.</summary>
    /// <para>#390: with <c>temporal</c> above 0 and a velocity target painted this frame, a third stage follows
    /// the blur: the blurred estimate is blended with last frame's history reprojected through the velocity
    /// target, clamped to the estimate's 3x3 range, into the other history texture -- which is then the
    /// frame's output. The history stores the view distance beside the visibility, and a sample whose distance
    /// disagrees with this pixel's (a disocclusion) or lands off screen falls back to the current estimate.
    /// The raw stage rotates its noise phase per frame only on this path, so <c>temporal 0</c> is the
    /// single-frame pass unchanged.</para></summary>
    internal static void Run(uint depthTex, bool depthFilled, Matrix4x4* proj, int* viewport, uint restoreFbo, int frameSeq)
    {
        Live = false;
        Half = false;
        if (!Active || !depthFilled || depthTex == 0) return;
        if (!_built) EnsurePrograms();
        bool half = Wanted;
        if (_failed || !EnsureTargets(viewport[2], viewport[3], half)) return;
        Half = half;
        float w = _w, h = _h;
        bool temporal = Ao.Temporal.V > 0f && _progTemporal != 0 && _hist0 != 0 && MotionVectors.Painted && MotionVectors.Tex != 0;
        Gl.BindVertexArray(PostChain.FullscreenVao);
        new PassState(depth: false, depthWrite: false).Apply();
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        bool blur = Ao.Blur.V && _progBlur != 0;
        uint histRead = _histCur == 0 ? _hist0 : _hist1, histWrite = _histCur == 0 ? _hist1 : _hist0;
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, blur || temporal ? _raw : _out, 0);
        Gl.Viewport(half ? viewport[0] / 2 : viewport[0], half ? viewport[1] / 2 : viewport[1], _w, _h);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, depthTex);
        Gl.UseProgram(_prog);
        Gl.Uniform1i(_locDepth, 0);
        Gl.UniformMatrix4fv(_locProj, 1, 0, (float*)proj);
        Gl.Uniform4f(_locRt, 1f / w, 1f / h, w, h);
        Gl.Uniform1f(_locRadius, Ao.Radius.V);
        Gl.Uniform1f(_locPower, Ao.Strength.V * Strength);
        Gl.Uniform1i(_locDirs, Ao.Dirs.V);
        Gl.Uniform1i(_locSteps, Ao.Steps.V);
        // the cap is a SCREEN reach, so it halves with the target: 96 half-res texels would be 192 of the frame's
        Gl.Uniform1f(_locMaxPx, half ? Ao.MaxPx.V * 0.5f : Ao.MaxPx.V);
        Gl.Uniform1i(_locFrame, temporal ? frameSeq & 63 : 0);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        // #390: the blur FIRST -- it folds the Bayer pattern away, and the accumulator's 3x3 clamp needs a
        // neighbourhood that means something, which four fetches a pixel do not give it.
        uint blurred = _raw;
        if (blur)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _out, 0);
            Gl.ActiveTexture(Gl.TEXTURE0 + 1);
            Gl.BindTexture(Gl.TEXTURE_2D, _raw);
            Gl.UseProgram(_progBlur);
            Gl.Uniform1i(_locBlurDepth, 0);
            Gl.Uniform1i(_locBlurSrc, 1);
            Gl.UniformMatrix4fv(_locBlurProj, 1, 0, (float*)proj);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
            blurred = _out;
        }
        _liveOut = _out;
        if (temporal)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, histWrite, 0);
            Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, blurred);
            Gl.ActiveTexture(Gl.TEXTURE0 + 2); Gl.BindTexture(Gl.TEXTURE_2D, histRead);
            Gl.ActiveTexture(Gl.TEXTURE0 + 3); Gl.BindTexture(Gl.TEXTURE_2D, MotionVectors.Tex);
            Gl.UseProgram(_progTemporal);
            Gl.Uniform1i(_locTDepth, 0);
            Gl.Uniform1i(_locTRaw, 1);
            Gl.Uniform1i(_locTHist, 2);
            Gl.Uniform1i(_locTVel, 3);
            Gl.UniformMatrix4fv(_locTProj, 1, 0, (float*)proj);
            Gl.Uniform1f(_locTWeight, _histValid ? Ao.Temporal.V : 0f);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
            Gl.ActiveTexture(Gl.TEXTURE0 + 3); Gl.BindTexture(Gl.TEXTURE_2D, 0);
            Gl.ActiveTexture(Gl.TEXTURE0 + 2); Gl.BindTexture(Gl.TEXTURE_2D, 0);
            _histCur ^= 1; _histValid = true; _temporalRuns++;
            // the history IS this frame's output: the lit stage reads its red channel
            _liveOut = histWrite;
        }
        else _histValid = false;
        Gl.ActiveTexture(Gl.TEXTURE0 + 1);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthMask(1);
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, restoreFbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        Live = true;
        _runs++;
    }

    /// <summary>From every lit-program bind: the visibility texture on its unit, or nothing when the pass
    /// did not run. Per bind and not once per frame because the frame snapshot takes the same unit at
    /// FbCopy, and lit additive draws follow it. Leaves unit 0 current for the batch loop.</summary>
    internal static void BindForDraw(bool on, uint sceneDepth)
    {
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.Ssao);
        Gl.BindTexture(Gl.TEXTURE_2D, on ? _liveOut : 0);
        // ...and the depth it was computed FROM, so the lit stage can refuse a pixel that is not its own.
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.SsaoDepth);
        Gl.BindTexture(Gl.TEXTURE_2D, on ? sceneDepth : 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    /// <summary>The visibility targets at the extent this frame wants them: the frame's, or half of it
    /// under <c>post.halfres</c>. A size change reallocates and drops the history, which was accumulated on
    /// the other grid.</summary>
    private static bool EnsureTargets(int srcW, int srcH, bool half)
    {
        int w = half ? (srcW + 1) / 2 : srcW, h = half ? (srcH + 1) / 2 : srcH;
        if (_out != 0 && w == _w && h == _h) return true;
        if (w <= 0 || h <= 0) return false;
        _srcW = srcW; _srcH = srcH;
        if (_out == 0)
        {
            uint* t = stackalloc uint[4];
            Gl.GenTextures(4, t);
            _raw = t[0]; _out = t[1]; _hist0 = t[2]; _hist1 = t[3];
            uint f; Gl.GenFramebuffers(1, &f); _fbo = f;
        }
        AllocR8(_raw, w, h);
        AllocR8(_out, w, h);
        // #390: LINEAR -- the reprojected history lands between texels; the blur reads it by texelFetch.
        foreach (uint hs in stackalloc uint[2] { _hist0, _hist1 })
        {
            Gl.BindTexture(Gl.TEXTURE_2D, hs);
            Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RG16F, w, h, 0, Gl.RG, Gl.HALF_FLOAT, null);
            RenderTarget.Clamp(Gl.LINEAR);
        }
        _histValid = false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _out, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _failed = true;
            _log?.Info($"SsaoPass: the R8 target is incomplete (0x{status:x}) -- the pass stands down on this context");
            return false;
        }
        _w = w; _h = h;
        _log?.Info($"SsaoPass: visibility targets {w}x{h} R8"
                 + (half ? $" -- HALF of the {srcW}x{srcH} frame (post.halfres), upsampled depth-aware in the lit stage"
                         : $" -- the {srcW}x{srcH} frame's own extent"));
        return true;
    }

    private static void AllocR8(uint tex, int w, int h)
    {
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.R8, w, h, 0, Gl.RED, Gl.UNSIGNED_BYTE, null);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
    }

    /// <summary>Both programs once per context. The visibility program failing drops the pass with a named
    /// line; the blur failing only loses the blur.</summary>
    private static void EnsurePrograms()
    {
        _built = true;
        string vs = ShaderStages.Source(PostChain.ShdPostVs);
        _prog = GlProgram.Graphics(PostChain.ShdPostVs, vs, ShdSsaoFs, ShaderStages.Source(ShdSsaoFs), _log, "SSAO visibility");
        if (_prog == 0)
        {
            _failed = true;
            _log?.Info("SsaoPass: the visibility program did not build -- the pass stands down; the ambient term keeps the material AO alone");
            return;
        }
        _locDepth = GlProgram.Loc(_prog, "uDepth"); _locProj = GlProgram.Loc(_prog, "uProj"); _locRt = GlProgram.Loc(_prog, "uRt");
        _locRadius = GlProgram.Loc(_prog, "uRadius"); _locPower = GlProgram.Loc(_prog, "uPower");
        _locDirs = GlProgram.Loc(_prog, "uDirs"); _locSteps = GlProgram.Loc(_prog, "uSteps"); _locMaxPx = GlProgram.Loc(_prog, "uMaxPx");
        _locFrame = GlProgram.Loc(_prog, "uFrame");
        _progBlur = GlProgram.Graphics(PostChain.ShdPostVs, vs, ShdSsaoBlurFs, ShaderStages.Source(ShdSsaoBlurFs), _log, "SSAO blur");
        if (_progBlur == 0) { _log?.Info("SsaoPass: the blur did not build -- visibility ships unblurred (the 4x4 pattern will show)"); }
        else
        {
            _locBlurSrc = GlProgram.Loc(_progBlur, "uSrc"); _locBlurDepth = GlProgram.Loc(_progBlur, "uDepth");
            _locBlurProj = GlProgram.Loc(_progBlur, "uProj");
        }
        // #390: embedded only, like MotionVectors -- not an override point.
        _progTemporal = GlProgram.Graphics(PostChain.FullscreenVs, TemporalFs, _log, "SSAO temporal");
        if (_progTemporal == 0) { _log?.Info("SsaoPass: the temporal stage did not build -- visibility is single-frame (/ao dirs 3 steps 3 restores today's density)"); return; }
        _locTDepth = GlProgram.Loc(_progTemporal, "uDepth"); _locTProj = GlProgram.Loc(_progTemporal, "uProj");
        _locTRaw = GlProgram.Loc(_progTemporal, "uRaw"); _locTHist = GlProgram.Loc(_progTemporal, "uHist");
        _locTVel = GlProgram.Loc(_progTemporal, "uVel"); _locTWeight = GlProgram.Loc(_progTemporal, "uWeight");
    }

    /// <summary>The 4x4 depth-aware box that folds the pattern away: one full period of the Bayer tile,
    /// weighted by view-distance similarity so occlusion never bleeds across a silhouette.
    /// <para>The depth is addressed by UV, never by texel: under <c>post.halfres</c> this stage runs at half
    /// the depth target's extent, and a <c>texelFetch</c> at the visibility's own coordinates would read the
    /// top-left quarter of the frame's depth for the whole screen. At equal sizes the two are identical.</para></summary>
    internal static string SsaoBlurFs => ShaderSource.Get("cx_ssaoblur_f.glsl");

    /// <summary>#390: the accumulator. Velocity is <c>prev - cur</c> in this target's pixels, so it is uv
    /// through its own size; the history's stored view distance against this pixel's is the disocclusion test
    /// (10% relative), and an off-screen or sky sample takes the current estimate whole.</summary>
    internal static string TemporalFs => ShaderSource.Get("cx_ssaotemp_f.glsl");

    /// <summary>Off-device: the two stages compose on the shared reconstruction, the gate is the engine's
    /// effect bit, and the lit stage takes the result on the ambient term only.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        ExtenderLog? quiet = _log; _log = null;
        var check = new Checker(log);
        try
        {
            string fs = ShaderStages.Source(ShdSsaoFs), bl = ShaderStages.Source(ShdSsaoBlurFs);
            check.Expect(fs.Contains("float CxViewDist(float depth)") && bl.Contains("float CxViewDist(float depth)"), "both stages carry the reconstruction");
            check.Expect(fs.Contains("uProj[3].z / (depth * 2.0 - 1.0 + uProj[2].z)"), "the linearisation is the particle fade's (uProj[3].z / (ndc + uProj[2].z))");
            check.Expect(ShaderContract.SlotSafe(fs, "uDepth") && ShaderContract.SlotSafe(bl, "uSrc", "uDepth"), "the samplers the C# side binds are declared");
            check.Expect(EffectSsao == 1u, "FramebufferEffect 0 is SSAO (CClientOptions::SetEnableFBEffectSSAO -> SetFBEffectEnabled(0, on))");
            check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("occl *= mix(1.0, CxSsaoAt(ivec2(gl_FragCoord.xy), aoDepth, aoSlope), uAlphaCut >= 0.0 ? uAoCutout : 1.0);")
              && AlloyRenderer.OwnedLitFsMain.Contains("* mix(0.15, 1.0, sunRaw) * occl"), "the lit stage folds it into occl, which scales the AMBIENT term");
            // post.halfres: the pass is a quarter of the pixels and the LIT stage is the upsampler -- there
            // is no apply pass to put a bilateral filter in, the consumer fetches per fragment.
            check.Expect(AlloyRenderer.OwnedLitFsHead.Contains("float CxSsaoAt(ivec2 pix, float myDepth, float slope)")
              && AlloyRenderer.OwnedLitFsHead.Contains("if (uSsaoHalf == 0) return texelFetch(uSsao, pix, 0).r;"),
              "the half-res upsample is the lit stage's, and at full resolution it is the same single fetch as before");
            check.Expect(AlloyRenderer.OwnedLitFsHead.Contains("return wsum > 0.0 ? sum / wsum : near;")
              && AlloyRenderer.OwnedLitFsHead.Contains("if (dz < best) { best = dz; near = v; }")
              && AlloyRenderer.OwnedLitFsHead.Contains("max(1.0 - dz / tol, 0.0)"),
              "...four taps weighted by depth proximity, falling back to the nearest where none agrees");
            check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("float aoSlope = fwidth(gl_FragCoord.z);"),
              "the tolerance is the surface's own depth slope, taken outside every branch (fwidth wants a defined neighbourhood)");
            check.Expect(!SsaoBlurFs.Contains("texelFetch(uDepth"), "the blur addresses the DEPTH by uv: at half res its own texel coordinates are another quarter of the screen");
            check.Expect(!Post.HalfRes.Default, "post.halfres ships OFF -- the 4-tap upsample costs every lit fragment more than the quarter-size pass saves");
            check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("float dirOcc = mix(1.0, occl, uAoDirect);")
              && AlloyRenderer.OwnedLitFsMain.Contains("vec3 diffuse = (sun + sunSss + point) * dirOcc;"),
              "/ao direct is the only route to the DIRECT terms, and 0 leaves them exactly as ADR 0021 had them");
            check.Expect(Ao.Direct.Default == 1f, "...and it ships at 1: ambient-only is invisible in a dark-ambient area (ADR 0021, amended)");
            check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("bool aoMine = uSsaoOn > 0 && aoDz < CX_AO_DEPTH_EPS;")
              && AlloyRenderer.OwnedLitFsMain.Contains("if (aoMine) occl *="),
              "the fetch is per FRAGMENT: the pixel's occlusion must have been computed from this surface's own depth");
            check.Expect(Strength > 0f && Ao.Strength.Min > 0f, "the strength never reads 0 -- a zero exponent is pow(v, 0) = 1, no occlusion at all");
            check.Expect(Describe().Contains("radius") && Describe().Contains("strength") && Describe().Contains("temporal"), "/ao reports its dials");
            // #390: the accumulator reprojects in the velocity target's own units, tests the history's depth,
            // and at temporal 0 the raw stage's noise phase is the single-frame one exactly.
            check.Expect(TemporalFs.Contains("/ vec2(textureSize(uVel, 0))") && TemporalFs.Contains("abs(hd - dist)") && TemporalFs.Contains("texelFetch(uHist,")
              && TemporalFs.Contains("clamp(h.r, mn - PAD, mx + PAD)")
              && ShaderContract.SlotSafe(TemporalFs, "uRaw", "uHist", "uVel", "uDepth"), "the temporal stage reprojects through the velocity target, rejects a depth mismatch and clamps to the neighbourhood");
            check.Expect(fs.Contains("fract(b / 16.0 + float(uFrame) * 0.618034)"), "the raw stage's noise phase is uFrame-rotated, 0 = today's pattern");
            float tp = Ao.Temporal.V; bool hv = _histValid;
            _histValid = true; DropHistory(); check.Expect(!_histValid, "a temporal change drops the history");
            Ao.Temporal.V = 0f; check.Expect(!TemporalWanted, "temporal 0 asks for no velocity target");
            Ao.Temporal.V = tp; _histValid = hv;
            check.Expect(Ao.Temporal.TryParse("1", out float tw) && tw == 0.98f, "the history weight never reaches 1: a frozen history");
            check.Expect(Ao.Dirs.Default == 1 && Ao.Steps.Default == 3 && Ao.Temporal.Default > 0f, "slices are what the accumulator averages; the per-frame horizon keeps its full step count");
        }
        finally { _log = quiet; }
        log.Info(check.Ok ? "SsaoPass.SelfCheck: passed" : "SsaoPass.SelfCheck: FAILED");
        return check.Ok;
    }
}
