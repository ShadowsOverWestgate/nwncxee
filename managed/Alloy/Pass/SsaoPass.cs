using System.Numerics;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

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
    private static int _locDepth, _locProj, _locRt, _locRadius, _locPower, _locDirs, _locSteps, _locMaxPx,
                       _locBlurSrc, _locBlurDepth, _locBlurProj;
    private static int _w, _h;
    private static bool _built, _failed, _on = true;
    private static int _runs;
    /// <summary>True from this frame's pass until the next: the lit stage may read the texture.</summary>
    internal static bool Live { get; private set; }

    /// <summary>World-space radius (NWN units are metres: a creature stands two of them). 0.6 is contact
    /// occlusion -- corners, feet, the underside of furniture -- not a room-scale gloom.</summary>
    private static float _radius = 0.6f;
    /// <summary>The visibility exponent at stock intensity (GTAO's finalValuePower); the option scales it.</summary>
    private static float _power = 2.2f;
    /// <summary>Slices and steps per side: 3x3 is 18 depth fetches a pixel, the fast-and-pretty point.</summary>
    private static float _dirs = 3f, _steps = 3f;
    /// <summary>Screen-radius clamp in pixels: bounds the cost when the camera is against a wall.</summary>
    private static float _maxPx = 96f;
    /// <summary>The blur stage as a dial, 0 to see the raw pattern.</summary>
    private static float _blur = 1f;
    /// <summary>How much of the occlusion the DIRECT terms take (<c>/ao direct</c>). ADR 0021 gave it to the
    /// ambient alone, which is invisible wherever the area ambient is dark -- every interior, where the light
    /// is lamps and the corners are exactly what wants occluding. Measured live: with ambient at its area
    /// value the effect did not reach the frame at all, and /light ambient 20 was what made it appear. 1 (the
    /// default, vanilla's post-multiply) is the only setting the effect is visible indoors at; 0 restores the
    /// ADR's original ambient-only rule.</summary>
    private static float _direct = 1f;
    private static float _bootRadius = 0.6f, _bootPower = 2.2f, _bootDirs = 3f, _bootSteps = 3f, _bootMaxPx = 96f, _bootBlur = 1f, _bootDirect = 1f;

    /// <summary>The lit stage's <c>uAoDirect</c>, read at every lit program bind.</summary>
    internal static float Direct => _direct;

    internal const string ShdSsaoFs = "cx_ssao_fs", ShdSsaoBlurFs = "cx_ssaoblur_fs";

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _effects = (uint*)Native.Sym(SymbolMap.FbEffectsMask);
        _fbEffects = (int*)Native.Sym(SymbolMap.EnableFBEffects);
        _intensity = (float*)Native.Sym(SymbolMap.SsaoIntensity);
        if (_effects == null) log.Info("SsaoPass: Camera::m_nFramebufferEffectsEnabled is unresolved -- the SSAO option stays inert");
        _bootRadius = _radius; _bootPower = _power; _bootDirs = _dirs; _bootSteps = _steps; _bootMaxPx = _maxPx; _bootBlur = _blur; _bootDirect = _direct;
        KnobSurface.Note(static () => _effects == null ? null
            : $"the client's SSAO video option (settings.tml graphics.fbo.ssao.enabled = {(OptionOn ? 1 : 0)}) is the Alloy "
            + $"lit pass's (#379): {(_failed ? "the pass did NOT build -- the option is inert"
                                     : !_on ? "BYPASSED by /ao on 0"
                                     : OptionOn ? "horizon occlusion from the depth prepass scales the ambient term"
                                     : "off")}");
        log.Info($"SsaoPass: attached -- {Describe()}");
    }

    /// <summary>The option's bit under the console gate, the engine's own condition for the effect.</summary>
    private static bool OptionOn => _effects != null && (*_effects & EffectSsao) != 0 && (_fbEffects == null || *_fbEffects != 0);
    internal static bool Active => _on && !_failed && OptionOn;
    /// <summary>The option's intensity as a multiplier on the exponent: stock 0.5 is 1x; an unresolved or
    /// zeroed config reads as stock rather than as "no occlusion".</summary>
    private static float Strength
    {
        get { float i = _intensity == null ? StockIntensity : *_intensity; return (i > 0f ? i : StockIntensity) / StockIntensity; }
    }

    internal static string Describe()
        => $"ao {(_failed ? "FAILED" : !_on ? "BYPASSED" : OptionOn ? "live" : "idle (option off)")}  radius {_radius:F2}  "
         + $"power {_power:F2} (x{Strength:F2} from ssao_intensity)  dirs {_dirs:F0}  steps {_steps:F0}  maxpx {_maxPx:F0}  blur {_blur:F0}  direct {_direct:F2}";

    internal static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            case "on":     old = _on ? 1f : 0f; _on = v != 0f; break;
            case "radius": old = _radius; _radius = v; break;
            case "power":  old = _power; _power = v; break;
            case "dirs":   old = _dirs; _dirs = MathF.Max(1f, MathF.Min(8f, v)); break;
            case "steps":  old = _steps; _steps = MathF.Max(1f, MathF.Min(8f, v)); break;
            case "maxpx":  old = _maxPx; _maxPx = v; break;
            case "blur":   old = _blur; _blur = v; break;
            case "direct": old = _direct; _direct = MathF.Max(0f, MathF.Min(1f, v)); break;
            default: return float.NaN;
        }
        _log?.Info($"SsaoPass: /ao {key} {v:F2} (was {old:F2}) -> {Describe()}");
        return old;
    }

    internal static void ResetTuning()
    { _on = true; _radius = _bootRadius; _power = _bootPower; _dirs = _bootDirs; _steps = _bootSteps; _maxPx = _bootMaxPx; _blur = _bootBlur; _direct = _bootDirect; }

    internal static string Stats(int frames)
    {
        int n = _runs; _runs = 0;
        return _failed ? "ao FAILED" : n == 0 ? "ao idle" : $"ao {(float)n / MathF.Max(frames, 1):F2} runs/frame";
    }

    /// <summary>Context loss: forget every GL name.</summary>
    internal static void Forget()
    { _fbo = 0; _raw = 0; _out = 0; _prog = 0; _progBlur = 0; _w = 0; _h = 0; _built = false; _failed = false; Live = false; }

    /// <summary>The pass, between the depth prepass and the colour pass: visibility into the raw target, the
    /// depth-aware 4x4 blur into the one the lit stage reads on <see cref="TextureSlots.Ssao"/>. Restores the
    /// frame's framebuffer and viewport. Hot: allocates nothing after the first frame at a size.</summary>
    internal static void Run(uint depthTex, bool depthFilled, Matrix4x4* proj, int* viewport, uint restoreFbo)
    {
        Live = false;
        if (!Active || !depthFilled || depthTex == 0) return;
        if (!_built) EnsurePrograms();
        if (_failed || !EnsureTargets(viewport[2], viewport[3])) return;
        float w = _w, h = _h;
        Gl.BindVertexArray(PostChain.FullscreenVao);
        new PassState(depth: false, depthWrite: false).Apply();
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        bool blur = _blur != 0f && _progBlur != 0;
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, blur ? _raw : _out, 0);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, depthTex);
        Gl.UseProgram(_prog);
        Gl.Uniform1i(_locDepth, 0);
        Gl.UniformMatrix4fv(_locProj, 1, 0, (float*)proj);
        Gl.Uniform4f(_locRt, 1f / w, 1f / h, w, h);
        Gl.Uniform1f(_locRadius, _radius);
        Gl.Uniform1f(_locPower, _power * Strength);
        Gl.Uniform1i(_locDirs, (int)_dirs);
        Gl.Uniform1i(_locSteps, (int)_steps);
        Gl.Uniform1f(_locMaxPx, _maxPx);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
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
            Gl.ActiveTexture(Gl.TEXTURE0 + 1);
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
        }
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
        Gl.BindTexture(Gl.TEXTURE_2D, on ? _out : 0);
        // ...and the depth it was computed FROM, so the lit stage can refuse a pixel that is not its own.
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.SsaoDepth);
        Gl.BindTexture(Gl.TEXTURE_2D, on ? sceneDepth : 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    private static bool EnsureTargets(int w, int h)
    {
        if (_out != 0 && w == _w && h == _h) return true;
        if (w <= 0 || h <= 0) return false;
        if (_out == 0)
        {
            uint* t = stackalloc uint[2];
            Gl.GenTextures(2, t);
            _raw = t[0]; _out = t[1];
            uint f; Gl.GenFramebuffers(1, &f); _fbo = f;
        }
        AllocR8(_raw, w, h);
        AllocR8(_out, w, h);
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
        _log?.Info($"SsaoPass: visibility targets {w}x{h} R8");
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
        _progBlur = GlProgram.Graphics(PostChain.ShdPostVs, vs, ShdSsaoBlurFs, ShaderStages.Source(ShdSsaoBlurFs), _log, "SSAO blur");
        if (_progBlur == 0) { _log?.Info("SsaoPass: the blur did not build -- visibility ships unblurred (the 4x4 pattern will show)"); return; }
        _locBlurSrc = GlProgram.Loc(_progBlur, "uSrc"); _locBlurDepth = GlProgram.Loc(_progBlur, "uDepth");
        _locBlurProj = GlProgram.Loc(_progBlur, "uProj");
    }

    /// <summary>Shared by both stages: view-space reconstruction off the frame depth and the pass's own
    /// projection -- the same two coefficients the particle fade linearises with, so nothing can drift.</summary>
    internal const string SsaoLib = """
        uniform sampler2D uDepth;    // the frame depth after the prepass
        uniform mat4 uProj;          // this pass's projection, raw engine bytes (see SowxViewDist)

        // Positive view distance: z_clip = z_v*M22 + M32, w_clip = -z_v, so z_v = -M32 / (ndc + M22).
        float SowxViewDist(float depth)
        {
            return uProj[3].z / (depth * 2.0 - 1.0 + uProj[2].z);
        }
        // View-space position (camera at the origin looking down -z) of the pixel at uv.
        vec3 SowxViewPos(vec2 uv)
        {
            float dist = SowxViewDist(textureLod(uDepth, uv, 0.0).r);
            vec2 n = uv * 2.0 - 1.0;
            return vec3(n.x * dist / uProj[0].x, n.y * dist / uProj[1].y, -dist);
        }

        """;

    /// <summary>GTAO on the frame depth (Jimenez et al. 2016, the XeGTAO reference's structure): per slice the
    /// two horizons, cosine-weighted against the normal projected into the slice, falloff over the outer
    /// 40% of the radius. Normals come from depth (the smaller-difference neighbour on each axis), a 4x4
    /// pattern rotates slices and steps, and the blur stage folds that pattern away.</summary>
    internal const string SsaoFs = """
        #version 460 core
        in vec2 vUV;
        out float frag;
        uniform vec4 uRt;            // (1/w, 1/h, w, h)
        uniform float uRadius;       // world units
        uniform float uPower;        // visibility exponent, the option's intensity folded in
        uniform int uDirs;           // slices
        uniform int uSteps;          // steps per side
        uniform float uMaxPx;        // screen-radius clamp

        """ + SsaoLib + """
        const float PI = 3.14159265, HALF_PI = 1.57079633;
        // 4x4 ordered pattern (Bayer), so the blur's 4x4 window sees every slice and step phase once.
        const float BAYER[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);

        vec3 SowxViewNormal(vec2 uv, vec3 P)
        {
            vec3 Pr = SowxViewPos(uv + vec2(uRt.x, 0.0)), Pl = SowxViewPos(uv - vec2(uRt.x, 0.0));
            vec3 Pu = SowxViewPos(uv + vec2(0.0, uRt.y)), Pd = SowxViewPos(uv - vec2(0.0, uRt.y));
            vec3 dx = abs(Pr.z - P.z) < abs(P.z - Pl.z) ? Pr - P : P - Pl;
            vec3 dy = abs(Pu.z - P.z) < abs(P.z - Pd.z) ? Pu - P : P - Pd;
            return normalize(cross(dx, dy));
        }

        void main()
        {
            float depth = textureLod(uDepth, vUV, 0.0).r;
            if (depth >= 1.0) { frag = 1.0; return; }
            vec3 P = SowxViewPos(vUV);
            vec3 N = SowxViewNormal(vUV, P);
            vec3 V = normalize(-P);
            float dist = -P.z;
            // world radius -> pixels: ndc spans 2 over h pixels, and x_ndc = x_v * M11 / dist
            float rPx = min(uRadius * uProj[1].y / dist * 0.5 * uRt.w, uMaxPx);
            if (rPx < 1.0) { frag = 1.0; return; }
            ivec2 pix = ivec2(gl_FragCoord.xy);
            float b = BAYER[(pix.x & 3) + ((pix.y & 3) << 2)];
            float noiseSlice = b / 16.0, noiseStep = fract(b * 0.618034);
            float falloffRange = 0.4 * uRadius, falloffMul = -1.0 / falloffRange, falloffAdd = 0.6 * uRadius / falloffRange + 1.0;
            float minS = 1.3 / rPx;
            float vis = 0.0;
            for (int s = 0; s < uDirs; s++)
            {
                float phi = (float(s) + noiseSlice) / float(uDirs) * PI;
                vec2 omega = vec2(cos(phi), sin(phi));
                vec3 dirVec = vec3(omega, 0.0);
                vec3 ortho = dirVec - dot(dirVec, V) * V;
                vec3 axis = normalize(cross(ortho, V));
                vec3 projN = N - axis * dot(N, axis);
                float projLen = length(projN);
                float signN = sign(dot(ortho, projN));
                float cosN = clamp(dot(projN, V) / max(projLen, 1e-5), 0.0, 1.0);
                float n = signN * acos(cosN);
                float low0 = cos(n + HALF_PI), low1 = cos(n - HALF_PI);
                float hc0 = low0, hc1 = low1;
                for (int j = 0; j < uSteps; j++)
                {
                    float t = (float(j) + fract(noiseStep + float(s) * 0.618034)) / float(uSteps);
                    t = t * t + minS;
                    vec2 off = omega * (t * rPx) * uRt.xy;
                    vec3 S0 = SowxViewPos(vUV + off) - P, S1 = SowxViewPos(vUV - off) - P;
                    float l0 = max(length(S0), 1e-5), l1 = max(length(S1), 1e-5);
                    float w0 = clamp(l0 * falloffMul + falloffAdd, 0.0, 1.0), w1 = clamp(l1 * falloffMul + falloffAdd, 0.0, 1.0);
                    hc0 = max(hc0, mix(low0, dot(S0, V) / l0, w0));
                    hc1 = max(hc1, mix(low1, dot(S1, V) / l1, w1));
                }
                float h0 = n + clamp(-acos(hc1) - n, -HALF_PI, HALF_PI);
                float h1 = n + clamp(acos(hc0) - n, -HALF_PI, HALF_PI);
                float a0 = (cosN + 2.0 * h0 * sin(n) - cos(2.0 * h0 - n)) * 0.25;
                float a1 = (cosN + 2.0 * h1 * sin(n) - cos(2.0 * h1 - n)) * 0.25;
                vis += projLen * (a0 + a1);
            }
            vis = clamp(vis / float(uDirs), 0.0, 1.0);
            frag = max(pow(vis, uPower), 0.03);
        }
        """;

    /// <summary>The 4x4 depth-aware box that folds the pattern away: one full period of the Bayer tile,
    /// weighted by view-distance similarity so occlusion never bleeds across a silhouette.</summary>
    internal const string SsaoBlurFs = """
        #version 460 core
        in vec2 vUV;
        out float frag;
        uniform sampler2D uSrc;      // the raw visibility

        """ + SsaoLib + """
        void main()
        {
            ivec2 pix = ivec2(gl_FragCoord.xy);
            float d0 = SowxViewDist(texelFetch(uDepth, pix, 0).r);
            float sum = 0.0, wsum = 0.0;
            for (int y = -1; y <= 2; y++)
                for (int x = -1; x <= 2; x++)
                {
                    ivec2 p = clamp(pix + ivec2(x, y), ivec2(0), textureSize(uSrc, 0) - 1);
                    float d = SowxViewDist(texelFetch(uDepth, p, 0).r);
                    float w = clamp(1.0 - abs(d - d0) / (0.05 * d0), 0.0, 1.0);
                    sum += texelFetch(uSrc, p, 0).r * w;
                    wsum += w;
                }
            frag = wsum > 0.0 ? sum / wsum : texelFetch(uSrc, pix, 0).r;
        }
        """;

    /// <summary>Off-device: the two stages compose on the shared reconstruction, the gate is the engine's
    /// effect bit, and the lit stage takes the result on the ambient term only.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        ExtenderLog? quiet = _log; _log = null;
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"SsaoPass.SelfCheck FAIL: {w}"); } }
        try
        {
            string fs = ShaderStages.Source(ShdSsaoFs), bl = ShaderStages.Source(ShdSsaoBlurFs);
            E(fs.Contains("float SowxViewDist(float depth)") && bl.Contains("float SowxViewDist(float depth)"), "both stages carry the reconstruction");
            E(fs.Contains("uProj[3].z / (depth * 2.0 - 1.0 + uProj[2].z)"), "the linearisation is the particle fade's (uProj[3].z / (ndc + uProj[2].z))");
            E(ShaderContract.SlotSafe(fs, "uDepth") && ShaderContract.SlotSafe(bl, "uSrc", "uDepth"), "the samplers the C# side binds are declared");
            E(EffectSsao == 1u, "FramebufferEffect 0 is SSAO (CClientOptions::SetEnableFBEffectSSAO -> SetFBEffectEnabled(0, on))");
            E(AlloyRenderer.OwnedLitFsMain.Contains("occl *= texelFetch(uSsao, ivec2(gl_FragCoord.xy), 0).r;")
              && AlloyRenderer.OwnedLitFsMain.Contains("* mix(0.15, 1.0, sunRaw) * occl"), "the lit stage folds it into occl, which scales the AMBIENT term");
            E(AlloyRenderer.OwnedLitFsMain.Contains("float dirOcc = mix(1.0, occl, uAoDirect);")
              && AlloyRenderer.OwnedLitFsMain.Contains("vec3 diffuse = (sun + sunSss + point) * dirOcc;"),
              "/ao direct is the only route to the DIRECT terms, and 0 leaves them exactly as ADR 0021 had them");
            E(_bootDirect == 1f, "...and it ships at 1: ambient-only is invisible in a dark-ambient area (ADR 0021, amended)");
            E(AlloyRenderer.OwnedLitFsMain.Contains("bool aoMine = uSsaoOn > 0 && aoDz < SOWX_AO_DEPTH_EPS;")
              && AlloyRenderer.OwnedLitFsMain.Contains("if (aoMine) occl *="),
              "the fetch is per FRAGMENT: the pixel's occlusion must have been computed from this surface's own depth");
            E(float.IsNaN(Tune("nope", 1f)), "an unknown knob is reported");
            E(Strength > 0f, "the strength never reads 0 -- a zero exponent is pow(v, 0) = 1, no occlusion at all");
            float r = _radius; Tune("radius", 1.5f); E(_radius == 1.5f, "/ao radius sets"); _radius = r;
            E(Describe().Contains("radius") && Describe().Contains("power"), "/ao reports its dials");
        }
        finally { _log = quiet; }
        log.Info(ok ? "SsaoPass.SelfCheck: passed" : "SsaoPass.SelfCheck: FAILED");
        return ok;
    }
}
