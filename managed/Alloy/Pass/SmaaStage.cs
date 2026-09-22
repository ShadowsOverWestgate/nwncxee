using System.Reflection;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Anti-aliasing as a post-stack stage (ADR 0020). The client's anti-aliasing option
/// (settings.tml <c>graphics.video.anti-aliasing.mode</c>, nwn.ini "AntiAliasing Mode", values 0/2/4/8/16 =
/// MSAA samples, global <c>g_nMSAA</c>) sizes the ENGINE's subframe renderbuffers
/// (Camera::RefreshRenderSubframeResources @0x5a6610 -> CreateRenderBuffer(fmt, w, h, samples) from the
/// copy at Camera+0xe3c), which the Alloy frame never lands in -- under Alloy the option was inert. Alloy
/// answers it with SMAA 1x on the resolved display-space frame: luma edges, blend weights against the
/// precomputed area/search tables, then the neighbourhood blend, which is the stack stage proper. The
/// option's sample count picks the SMAA preset: 2 MEDIUM, 4 HIGH, 8 and 16 ULTRA.
/// <para>Not MSAA: it multiplies every owned attachment and leaves the alpha-tested cutouts (grass, foliage)
/// aliased. Not TAA: it needs a jittered projection in every owned vertex stage under the invariant
/// gl_Position constraint, plus motion vectors -- its own ticket.</para></summary>
internal static unsafe class SmaaStage
{
    private static ExtenderLog? _log;
    /// <summary><c>g_nMSAA</c>, the option's live value.</summary>
    private static int* _msaa;
    /// <summary>The preset compiled in (0 none, 1 MEDIUM, 2 HIGH, 3 ULTRA): the <c>$AA$</c> token
    /// <see cref="ShaderResource.Tokens"/> expands, so an override reads the same preset as the embedded text.</summary>
    internal static int Level { get; private set; }
    private static uint _fbo, _edges, _blend, _area, _search, _progEdge, _progWeight, _stageProg;
    /// <summary>LINEAR/clamp sampler for uSrc in the aa passes: the stack's ping-pong textures are NEAREST,
    /// and SMAA's searches and final blend both lean on bilinear fetches at half-texel offsets.</summary>
    private static uint _sampler;
    private static int _locEdgeSrc, _locEdgeRt, _locWgtRt, _locWgtEdges, _locWgtArea, _locWgtSearch, _locStageRt, _locStageBlend;
    private static int _w, _h, _builtLevel = -1;
    private static bool _failed;

    /// <summary>The three passes' resrefs (#92 override points), same rules as every post-chain stage.</summary>
    internal const string ShdEdgeFs = "cx_smaaedge_fs", ShdWeightFs = "cx_smaawgt_fs", ShdBlendFs = "cx_smaablend_fs";
    /// <summary>The reference tables, RG8 160x560 and R8 64x16, as the repo ships them (Textures/*.h upstream).</summary>
    internal const int AreaW = 160, AreaH = 560, SearchW = 64, SearchH = 16;

    /// <summary>From <see cref="PostStack.Attach"/>: resolve the option's global.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _msaa = (int*)Native.Sym(SymbolMap.MsaaSamples);
        if (_msaa == null) log.Info("SmaaStage: g_nMSAA is unresolved -- the anti-aliasing option stays inert");
    }

    /// <summary>The option's sample count -> preset level; pure, so the self-check can assert it.</summary>
    internal static int LevelFor(int samples) => samples >= 8 ? 3 : samples >= 4 ? 2 : samples >= 2 ? 1 : 0;
    /// <summary>The option as set right now, in MSAA samples; 0 when the global is unresolved.</summary>
    internal static int Samples => _msaa == null ? 0 : *_msaa;
    /// <summary>The stage's per-frame gate: the option is on and nothing of ours failed to build.</summary>
    /// <para>#389: off whenever TAA is on -- the two are exclusive; TAA smooths what SMAA smooths and more.</para>
    internal static bool Active => !_failed && LevelFor(Samples) > 0 && !PostChain.TaaOn;
    internal static string PresetName(int level) => level switch { 3 => "ULTRA", 2 => "HIGH", 1 => "MEDIUM", _ => "off" };
    /// <summary>The /post and /knobs term: option, preset, and whether the stage is dropped.</summary>
    internal static string Describe()
        => $"(aa option {Samples}x -> SMAA {PresetName(LevelFor(Samples))}{(_failed ? ", FAILED" : PostChain.TaaOn ? ", replaced by TAA" : "")})";

    /// <summary>Context loss: forget every GL name; the next frame rebuilds.</summary>
    internal static void Forget()
    {
        _fbo = 0; _edges = 0; _blend = 0; _area = 0; _search = 0; _sampler = 0; _progEdge = 0; _progWeight = 0;
        _stageProg = 0; _w = 0; _h = 0; _builtLevel = -1; _failed = false;
    }

    /// <summary>Asked by <see cref="PostStack.Begin"/>: targets at this size and programs at this preset,
    /// (re)built when either moved. False drops the stage for the frame (and, on a build failure, for the
    /// context). Cold after the first frame at a size and level.</summary>
    internal static bool Ready(int w, int h)
    {
        if (_failed) return false;
        if (Gl.GenSamplers == null || Gl.ClearBufferfv == null)
        {
            _failed = true;
            _log?.Info("SmaaStage: no sampler objects or glClearBufferfv on this context -- the aa stage is DROPPED");
            return false;
        }
        int level = LevelFor(Samples);
        if (level != _builtLevel && !Build(level)) return false;
        return w == _w && h == _h || Size(w, h);
    }

    private static bool Build(int level)
    {
        if (_progEdge != 0) { Gl.DeleteProgram(_progEdge); Gl.DeleteProgram(_progWeight); }
        Level = level;
        string vs = ShaderStages.Source(PostChain.ShdPostVs);
        _progEdge = GlProgram.Graphics(PostChain.ShdPostVs, vs, ShdEdgeFs, ShaderStages.Source(ShdEdgeFs), _log, "SMAA edges");
        _progWeight = GlProgram.Graphics(PostChain.ShdPostVs, vs, ShdWeightFs, ShaderStages.Source(ShdWeightFs), _log, "SMAA weights");
        if (_progEdge == 0 || _progWeight == 0)
        {
            _failed = true;
            _log?.Info("SmaaStage: a pass did not build -- the aa stage is DROPPED on this context; the frame "
                     + "resolves without anti-aliasing");
            return false;
        }
        _locEdgeSrc = GlProgram.Loc(_progEdge, "uSrc"); _locEdgeRt = GlProgram.Loc(_progEdge, "uRt");
        _locWgtRt = GlProgram.Loc(_progWeight, "uRt"); _locWgtEdges = GlProgram.Loc(_progWeight, "uEdges");
        _locWgtArea = GlProgram.Loc(_progWeight, "uArea"); _locWgtSearch = GlProgram.Loc(_progWeight, "uSearch");
        _builtLevel = level;
        _log?.Info($"SmaaStage: built at SMAA {PresetName(level)} (option {Samples}x)");
        return true;
    }

    private static bool Size(int w, int h)
    {
        if (w <= 0 || h <= 0) return false;
        if (_edges == 0)
        {
            uint* t = stackalloc uint[4];
            Gl.GenTextures(4, t);
            _edges = t[0]; _blend = t[1]; _area = t[2]; _search = t[3];
            uint f; Gl.GenFramebuffers(1, &f); _fbo = f;
            _sampler = Gl.LinearClampSampler();
            if (!Table(_area, "SmaaArea", Gl.RG8, Gl.RG, AreaW, AreaH, Gl.LINEAR)
                || !Table(_search, "SmaaSearch", Gl.R8, Gl.RED, SearchW, SearchH, Gl.NEAREST))
            {
                _failed = true;
                _log?.Info("SmaaStage: the area/search tables are missing from the build -- the aa stage is DROPPED");
                return false;
            }
        }
        RenderTarget.AllocColor(_edges, false, w, h, Gl.LINEAR);
        RenderTarget.AllocColor(_blend, false, w, h, Gl.LINEAR);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _edges, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _failed = true;
            _log?.Info($"SmaaStage: the edge/weight target is incomplete (0x{status:x}) -- the aa stage is DROPPED");
            return false;
        }
        _w = w; _h = h;
        _log?.Info($"SmaaStage: edge + weight targets {w}x{h}");
        return true;
    }

    /// <summary>Upload one embedded table. The one allocation this class makes, once per context.</summary>
    private static bool Table(uint tex, string resource, uint internalFormat, uint format, int w, int h, int filter)
    {
        using Stream? s = typeof(SmaaStage).Assembly.GetManifestResourceStream(resource);
        if (s == null || s.Length != w * h * (format == Gl.RG ? 2 : 1)) return false;
        byte[] bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        fixed (byte* p = bytes) Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)internalFormat, w, h, 0, format, Gl.UNSIGNED_BYTE, p);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, filter);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, filter);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
        return true;
    }

    /// <summary>The two passes ahead of the blend stage, from <see cref="PostStack.Run"/>: edges of
    /// <paramref name="src"/>, then weights. Leaves the weights on <see cref="TextureSlots.AaBlend"/>, the
    /// linear sampler on uSrc's unit, and the stage program's own uniforms set; the caller rebinds its
    /// target and viewport after. Hot: allocates nothing.</summary>
    internal static void Prepare(uint src, uint stageProg, int* viewport)
    {
        float* zero = stackalloc float[4];
        float w = _w, h = _h;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _edges, 0);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        // the edge pass DISCARDS non-edges (the reference's stencil hook), so last frame's edges must go
        Gl.ClearBufferfv(Gl.COLOR, 0, zero);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.PostHdr);
        Gl.BindTexture(Gl.TEXTURE_2D, src);
        Gl.BindSampler(TextureSlots.PostHdr, _sampler);
        Gl.UseProgram(_progEdge);
        Gl.Uniform1i(_locEdgeSrc, (int)TextureSlots.PostHdr);
        Gl.Uniform4f(_locEdgeRt, 1f / w, 1f / h, w, h);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);

        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _blend, 0);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaEdges); Gl.BindTexture(Gl.TEXTURE_2D, _edges);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaArea); Gl.BindTexture(Gl.TEXTURE_2D, _area);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaSearch); Gl.BindTexture(Gl.TEXTURE_2D, _search);
        Gl.UseProgram(_progWeight);
        Gl.Uniform1i(_locWgtEdges, (int)TextureSlots.AaEdges);
        Gl.Uniform1i(_locWgtArea, (int)TextureSlots.AaArea);
        Gl.Uniform1i(_locWgtSearch, (int)TextureSlots.AaSearch);
        Gl.Uniform4f(_locWgtRt, 1f / w, 1f / h, w, h);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);

        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaBlend); Gl.BindTexture(Gl.TEXTURE_2D, _blend);
        if (stageProg != _stageProg)
        {
            _stageProg = stageProg;
            _locStageRt = GlProgram.Loc(stageProg, "uRt"); _locStageBlend = GlProgram.Loc(stageProg, "uBlend");
        }
        Gl.UseProgram(stageProg);
        Gl.Uniform1i(_locStageBlend, (int)TextureSlots.AaBlend);
        Gl.Uniform4f(_locStageRt, 1f / w, 1f / h, w, h);
    }

    /// <summary>What the two SMAA passes and the blend stage share: the GLSL 4 port, the preset the option
    /// picked, and the reference library. Goes after a head that opened the file and before the pass body.</summary>
    internal const string SmaaDefs = """
        uniform vec4 uRt;            // (1/w, 1/h, w, h) of the frame -- SMAA_RT_METRICS
        #define SMAA_RT_METRICS uRt
        #define SMAA_GLSL_4 1
        #define CX_AA_LEVEL $AA$
        #if CX_AA_LEVEL >= 3
        #define SMAA_PRESET_ULTRA 1
        #elif CX_AA_LEVEL == 2
        #define SMAA_PRESET_HIGH 1
        #else
        #define SMAA_PRESET_MEDIUM 1
        #endif

        """ + SmaaLib.Text + """

        """;

    /// <summary>Pass 1: luma edges of the resolved frame, RG = (left, top) edge flags.</summary>
    internal const string EdgeFs = """
        #version 460 core
        in vec2 vUV;
        out vec4 frag;
        uniform sampler2D uSrc;      // the resolved frame, DISPLAY space -- SMAA wants perceptual luma

        """ + SmaaDefs + """
        void main()
        {
            vec4 off[3];
            SMAAEdgeDetectionVS(vUV, off);
            frag = vec4(SMAALumaEdgeDetectionPS(vUV, off, uSrc), 0.0, 1.0);
        }
        """;

    /// <summary>Pass 2: blend weights from the edges, through the area and search tables.</summary>
    internal const string WeightFs = """
        #version 460 core
        in vec2 vUV;
        out vec4 frag;
        uniform sampler2D uEdges;
        uniform sampler2D uArea;
        uniform sampler2D uSearch;

        """ + SmaaDefs + """
        void main()
        {
            vec4 off[3];
            vec2 pix;
            SMAABlendingWeightCalculationVS(vUV, pix, off);
            frag = SMAABlendingWeightCalculationPS(vUV, pix, off, uEdges, uArea, uSearch, vec4(0.0));
        }
        """;

    /// <summary>Pass 3, the stack stage: the neighbourhood blend of uSrc by the weights. Display space in
    /// and out, like every stage; the incoming <c>c</c> is re-fetched bilinearly by the reference. Carries
    /// the shared defs for the library's sake only -- no preset macro reaches this pass, which is why a
    /// live preset change rebuilds the other two programs and not this one.</summary>
    internal const string BlendStageFs = """
        uniform sampler2D uBlend;    // pass 2's weights

        """ + SmaaDefs + """
        vec4 CxStage(vec4 c)
        {
            vec4 off;
            SMAANeighborhoodBlendingVS(vUV, off);
            return SMAANeighborhoodBlendingPS(vUV, off, uSrc, uBlend);
        }
        """;

    /// <summary>Off-device: the option maps to the presets, the three passes compose on the port, and the
    /// tables are in the build at their reference sizes.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(LevelFor(0) == 0 && LevelFor(2) == 1 && LevelFor(4) == 2 && LevelFor(8) == 3 && LevelFor(16) == 3,
          "0/2/4/8/16 samples -> off/MEDIUM/HIGH/ULTRA/ULTRA");
        foreach (string r in new[] { ShdEdgeFs, ShdWeightFs, ShdBlendFs })
            check.Expect(ShaderStages.Source(r).Contains("#define SMAA_GLSL_4 1") && ShaderStages.Source(r).Contains("float2 SMAALumaEdgeDetectionPS("),
              $"{r} carries the port + library");
        foreach (string r in new[] { ShdEdgeFs, ShdWeightFs })
            check.Expect(ShaderResource.Tokens(ShaderStages.Source(r)).Contains($"#define CX_AA_LEVEL {Level}\n"), $"{r} takes the preset from $AA$");
        check.Expect(ShaderContract.SlotSafe(ShaderStages.Source(ShdWeightFs), "uEdges", "uArea", "uSearch"), "the weight pass binds its three tables");
        using (Stream? a = typeof(SmaaStage).Assembly.GetManifestResourceStream("SmaaArea"))
        using (Stream? s = typeof(SmaaStage).Assembly.GetManifestResourceStream("SmaaSearch"))
            check.Expect(a?.Length == AreaW * AreaH * 2 && s?.Length == SearchW * SearchH, "the area and search tables are embedded at 160x560 RG8 and 64x16 R8");
        log.Info(check.Ok ? "SmaaStage.SelfCheck: passed" : "SmaaStage.SelfCheck: FAILED");
        return check.Ok;
    }
}
