using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>Weighted blended order-independent transparency (McGuire &amp; Bavoil 2013) for the blended class.
/// The class draws in ONE pass, unsorted, depth-tested against the opaque set and writing no depth, into an
/// accumulation pair -- premultiplied colour times a depth weight (RGBA16F, ONE/ONE) and a revealage
/// product (R16F, ZERO/ONE_MINUS_SRC_COLOR) -- which <see cref="End"/> composites over the frame in one
/// full-screen draw. Any number of layers combine; nothing a translucent surface draws can hide another.
/// <para>Replaces the sorted, depth-writing blended pass and its per-creature one-layer sweep, whose four
/// depth decisions (opaque prepass, run sweep, colour draw, hi-Z) each punched a hole in a different
/// scene -- the floor decal under a light shaft, two ghosted creatures on it. Stands down to the old path
/// when the frame has no owned depth to attach (post not resolving) or a target refused.</para></summary>
internal static unsafe class OitPass
{
    private const uint R16F = 0x822D, RGBA32F = 0x8814;   // not-an-offset: GL internal-format enums
    private static uint _fbo, _accum, _reveal, _prog;
    private static int _w, _h, _uAccum, _uReveal, _uViz;
    /// <summary><c>/draw oitviz</c>: 1 = revealage as grey, 2 = accumulated mean colour, 3 = accumulated weight/10. Unblended.</summary>
    internal static int Viz;
    private static bool _failed;
    private static ExtenderLog? _log;
    private static long _runs;

    internal static void Init(ExtenderLog log) => _log = log;
    /// <summary>The frame's own draw framebuffer, read at <see cref="Begin"/> and restored by <see cref="End"/>.</summary>
    private static int _restoreFbo;

    /// <summary>Open the accumulation pair for the blended range. False = draw the old way this frame.</summary>
    internal static bool Begin(uint depthTex, int* viewport)
    {
        if (_failed || depthTex == 0 || Gl.BlendFunci == null || Gl.DrawBuffers == null) return false;
        int w = viewport[0] + viewport[2], h = viewport[1] + viewport[3];
        if (!EnsureTargets(w, h) || !EnsurePrograms()) return false;
        int cur; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &cur); _restoreFbo = cur;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, depthTex, 0);
        uint* bufs = stackalloc uint[2] { Gl.COLOR_ATTACHMENT0, Gl.COLOR_ATTACHMENT1 };
        Gl.DrawBuffers(2, bufs);
        float* zero = stackalloc float[4] { 0f, 0f, 0f, 0f };
        float* one = stackalloc float[4] { 1f, 1f, 1f, 1f };
        Gl.ClearBufferfv(Gl.COLOR, 0, zero);
        Gl.ClearBufferfv(Gl.COLOR, 1, one);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LEQUAL); Gl.DepthMask(0);
        Gl.Enable(Gl.BLEND);
        Gl.BlendFunci(0, Gl.ONE, Gl.ONE);
        Gl.BlendFunci(1, Gl.ZERO, Gl.ONE_MINUS_SRC_COLOR);
        _runs++;
        return true;
    }

    /// <summary>Composite the pair over the frame and hand the frame's framebuffer back. Leaves GL_BLEND on
    /// with the depth mask off, the state the passes after the blended class expect to find.</summary>
    internal static void End(int* viewport)
    {
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, 0, 0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)_restoreFbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        Gl.UseProgram(_prog);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.OitAccum); Gl.BindTexture(Gl.TEXTURE_2D, _accum);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.OitReveal); Gl.BindTexture(Gl.TEXTURE_2D, _reveal);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(_uAccum, (int)TextureSlots.OitAccum); Gl.Uniform1i(_uReveal, (int)TextureSlots.OitReveal);
        Gl.Uniform1i(_uViz, Viz);
        new PassState(depth: false, depthWrite: false, blend: Viz == 0,
                      blendSrc: Gl.ONE_MINUS_SRC_ALPHA, blendDst: Gl.SRC_ALPHA).Apply();
        Gl.BindVertexArray(PostChain.FullscreenVao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindVertexArray(0);
        // The draw loop tracks the depth test as ON and never re-enables it; the water and additive passes
        // after this one would otherwise draw through every wall.
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthFunc(Gl.LEQUAL);
    }

    internal static string Stats(int frames)
    {
        long n = _runs; _runs = 0;
        return _failed ? "oit STOOD DOWN" : n == 0 ? "oit idle" : $"oit {(float)n / System.Math.Max(frames, 1):F2} runs/frame at {_w}x{_h}";
    }

    internal static void ContextLost()
    {
        _fbo = 0; _accum = 0; _reveal = 0; _prog = 0; _w = 0; _h = 0; _failed = false;
    }

    private static bool EnsureTargets(int w, int h)
    {
        if (_accum != 0 && w == _w && h == _h) return true;
        if (w <= 0 || h <= 0) return false;
        if (_accum == 0)
        {
            uint* t = stackalloc uint[2]; Gl.GenTextures(2, t); _accum = t[0]; _reveal = t[1];
            uint f; Gl.GenFramebuffers(1, &f); _fbo = f;
        }
        // 32-bit: the accumulation is HDR colour times a weight up to 3e3, and a few stacked emissive flame
        // layers overflow fp16 to inf -- which resolved as a white patch over the lamp and the torch.
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, _accum);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)RGBA32F, w, h, 0, Gl.RGBA, Gl.FLOAT, null);
        RenderTarget.Clamp();
        Gl.BindTexture(Gl.TEXTURE_2D, _reveal);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)R16F, w, h, 0, Gl.RED, Gl.HALF_FLOAT, null);
        RenderTarget.Clamp();
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _accum, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT1, Gl.TEXTURE_2D, _reveal, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _failed = true;
            _log?.Info($"OitPass: the accumulation target is incomplete (0x{status:x}) -- blended draws the sorted way on this context");
            return false;
        }
        _w = w; _h = h;
        _log?.Info($"OitPass: accumulation pair {w}x{h} RGBA32F + R16F ready -- blended class is order-independent");
        return true;
    }

    private static bool EnsurePrograms()
    {
        if (_prog != 0) return true;
        _prog = GlProgram.Graphics(PostChain.FullscreenVs, ResolveFs, _log, "OitPass resolve");
        if (_prog == 0) { _failed = true; return false; }
        _uAccum = GlProgram.Loc(_prog, "uAccum"); _uReveal = GlProgram.Loc(_prog, "uReveal"); _uViz = GlProgram.Loc(_prog, "uViz");
        return true;
    }

    /// <summary>Composite: colour = accum.rgb / accum.a (the weighted mean), alpha = revealage, under
    /// ONE_MINUS_SRC_ALPHA / SRC_ALPHA so the frame shows through by exactly what the layers let through.</summary>
    private const string ResolveFs = """
        #version 460 core
        out vec4 frag;
        uniform sampler2D uAccum;
        uniform sampler2D uReveal;
        uniform int uViz;
        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            float reveal = texelFetch(uReveal, p, 0).r;
            if (uViz == 1) { frag = vec4(vec3(reveal), 1.0); return; }
            if (uViz == 2) { vec4 a = texelFetch(uAccum, p, 0); frag = vec4(a.rgb / max(a.a, 1e-5), 1.0); return; }
            if (uViz == 3) { frag = vec4(vec3(texelFetch(uAccum, p, 0).a * 0.1), 1.0); return; }
            if (!(reveal < 1.0)) discard;   // also NaN: nothing well-formed wrote here
            reveal = max(reveal, 0.0);
            vec4 accum = texelFetch(uAccum, p, 0);
            vec3 c = accum.rgb / max(accum.a, 1e-5);
            if (any(isinf(c)) || any(isnan(c))) c = vec3(0.0);
            frag = vec4(c, reveal);
        }
        """;
}
