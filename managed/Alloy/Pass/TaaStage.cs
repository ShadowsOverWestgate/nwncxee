using System;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>#389: temporal anti-aliasing as a post-stack stage. The projection is jittered by a sub-pixel
/// Halton(2,3) offset every frame (<see cref="PostChain.Jitter"/>, applied at the one site Alloy reads it),
/// and this stage averages the jittered frames: the display-space history is reprojected through the
/// velocity target (#388/#392), clamped to the 3x3 neighbourhood of the current frame so stale history is
/// rejected rather than ghosted, and blended in with <see cref="PostChain.TaaBlend"/> of the current frame.
/// The resolve happens in <see cref="Prepare"/> into an owned RGBA16F history pair; the stage draw itself
/// only copies the new history out -- when TAA is on it is the LAST stage (SMAA stands aside), so that copy
/// is the dithered write to the engine's target, and the history never carries the dither.
/// <para>Off by default until live-verified (<c>/post taa 1</c>); jitter and accumulation toggle together.</para></summary>
internal static unsafe class TaaStage
{
    private static ExtenderLog? _log;
    private static uint _fbo, _hist0, _hist1, _prog, _sampler, _stageProg;
    private static int _locCur, _locHist, _locVel, _locBlend, _locStageHist;
    private static int _w, _h, _cur;
    private static bool _failed, _built, _valid;

    /// <summary>Latched: a program or target failed on this context; the jitter stands down with the stage.</summary>
    internal static bool Failed => _failed;
    /// <summary>The stage's fragment text: the new history, as-is (display space in, display space out).</summary>
    internal const string StageFs = """
        uniform sampler2D uHist;     // this frame's resolved history (Prepare wrote it)
        vec4 SowxStage(vec4 c)
        {
            return vec4(texture(uHist, vUV).rgb, c.a);
        }
        """;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>History invalid: the next resolve takes the current frame whole. World edge (area transition)
    /// and every /post taa 1.</summary>
    internal static void Reset() => _valid = false;

    /// <summary>Context loss: forget every GL name.</summary>
    internal static void Forget()
    { _fbo = 0; _hist0 = 0; _hist1 = 0; _prog = 0; _sampler = 0; _stageProg = 0; _w = 0; _h = 0; _built = false; _failed = false; _valid = false; }

    /// <summary>From <see cref="PostStack.Begin"/>: can the stage run at this size this frame? Needs the
    /// velocity target to have painted (the first frame after arming has none, and resolves plain).</summary>
    internal static bool Ready(int w, int h)
    {
        if (_failed) return false;
        if (Gl.GenSamplers == null) { _failed = true; _log?.Info("TaaStage: no sampler objects on this context -- the taa stage is DROPPED"); return false; }
        if (!MotionVectors.Painted) return false;
        if (!_built && !Build()) return false;
        return w == _w && h == _h || Size(w, h);
    }

    private static bool Build()
    {
        _built = true;
        _prog = GlProgram.Graphics(PostChain.FullscreenVs, TaaFs, _log, "TAA resolve");
        if (_prog == 0) { _failed = true; _log?.Info("TaaStage: the resolve did not build -- the taa stage is DROPPED; SMAA serves the aa option"); return false; }
        _locCur = GlProgram.Loc(_prog, "uCur"); _locHist = GlProgram.Loc(_prog, "uHist");
        _locVel = GlProgram.Loc(_prog, "uVel"); _locBlend = GlProgram.Loc(_prog, "uBlend");
        return true;
    }

    private static bool Size(int w, int h)
    {
        if (w <= 0 || h <= 0) return false;
        if (_hist0 == 0)
        {
            uint* t = stackalloc uint[2];
            Gl.GenTextures(2, t); _hist0 = t[0]; _hist1 = t[1];
            uint f; Gl.GenFramebuffers(1, &f); _fbo = f;
            uint s; Gl.GenSamplers(1, &s); _sampler = s;
            Gl.SamplerParameteri(s, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
            Gl.SamplerParameteri(s, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
            Gl.SamplerParameteri(s, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
            Gl.SamplerParameteri(s, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        }
        RenderTarget.AllocColor(_hist0, true, w, h, Gl.NEAREST);
        RenderTarget.AllocColor(_hist1, true, w, h, Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _hist0, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _failed = true;
            _log?.Info($"TaaStage: the history target is incomplete (0x{status:x}) -- the taa stage is DROPPED");
            return false;
        }
        _w = w; _h = h; _valid = false;
        _log?.Info($"TaaStage: history pair {w}x{h} RGBA16F");
        return true;
    }

    /// <summary>The resolve, from <see cref="PostStack.Run"/> ahead of the stage draw: the current frame
    /// (<paramref name="src"/>) against the reprojected, clamped history into the other history texture, which
    /// is then left on <see cref="TextureSlots.AaEdges"/> for the stage program. The caller rebinds its own
    /// target and viewport after. Hot: allocates nothing.</summary>
    internal static void Prepare(uint src, uint stageProg, int* viewport)
    {
        uint read = _cur == 0 ? _hist0 : _hist1, write = _cur == 0 ? _hist1 : _hist0;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, write, 0);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.PostHdr); Gl.BindTexture(Gl.TEXTURE_2D, src);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaEdges); Gl.BindTexture(Gl.TEXTURE_2D, read);
        Gl.BindSampler(TextureSlots.AaEdges, _sampler);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaArea); Gl.BindTexture(Gl.TEXTURE_2D, MotionVectors.Tex);
        Gl.BindSampler(TextureSlots.AaArea, _sampler);
        Gl.UseProgram(_prog);
        Gl.Uniform1i(_locCur, (int)TextureSlots.PostHdr);
        Gl.Uniform1i(_locHist, (int)TextureSlots.AaEdges);
        Gl.Uniform1i(_locVel, (int)TextureSlots.AaArea);
        Gl.Uniform1f(_locBlend, _valid ? PostChain.TaaBlend : 1f);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindSampler(TextureSlots.AaEdges, 0);
        Gl.BindSampler(TextureSlots.AaArea, 0);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaArea); Gl.BindTexture(Gl.TEXTURE_2D, 0);
        // the new history on the stage's unit -- same size as the target, so its NEAREST read is exact
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaEdges); Gl.BindTexture(Gl.TEXTURE_2D, write);
        if (stageProg != _stageProg) { _stageProg = stageProg; _locStageHist = GlProgram.Loc(stageProg, "uHist"); }
        Gl.UseProgram(stageProg);
        Gl.Uniform1i(_locStageHist, (int)TextureSlots.AaEdges);
        _cur ^= 1; _valid = true;
    }

    /// <summary>The accumulator. Velocity is <c>prev - cur</c> in RENDER pixels (the target may be smaller
    /// than this display-sized frame, #391), so it is turned into uv through ITS OWN size. The history is
    /// clamped to the current 3x3 min/max -- the neighbourhood clamp that rejects a stale sample without a
    /// depth test -- and a moving pixel takes a little more of the current frame per pixel of motion, so
    /// disocclusions resolve in a few frames instead of the full history length.</summary>
    internal const string TaaFs = """
        #version 460 core
        in vec2 vUV;
        out vec4 frag;
        uniform sampler2D uCur;      // this frame, display space, the stack's size
        uniform sampler2D uHist;     // last frame's history, LINEAR
        uniform sampler2D uVel;      // #388: prev - cur, render pixels, render size
        uniform float uBlend;        // the current frame's share at rest; 1 = no history

        // Catmull-Rom through five bilinear taps (the 9-texel cubic with the corner taps folded): a bilinear
        // history resample every frame is the blur a naive TAA is known for, and this is the standard cure.
        vec3 SowxHistory(vec2 uv, vec2 size)
        {
            vec2 pos = uv * size;
            vec2 center = floor(pos - 0.5) + 0.5;
            vec2 f = pos - center;
            vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
            vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
            vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
            vec2 w3 = f * f * (-0.5 + 0.5 * f);
            vec2 w12 = w1 + w2;
            vec2 tc12 = (center + w2 / w12) / size;
            vec2 tc0 = (center - 1.0) / size, tc3 = (center + 2.0) / size;
            vec3 c = textureLod(uHist, vec2(tc12.x, tc0.y), 0.0).rgb * (w12.x * w0.y)
                   + textureLod(uHist, vec2(tc0.x, tc12.y), 0.0).rgb * (w0.x * w12.y)
                   + textureLod(uHist, tc12, 0.0).rgb * (w12.x * w12.y)
                   + textureLod(uHist, vec2(tc3.x, tc12.y), 0.0).rgb * (w3.x * w12.y)
                   + textureLod(uHist, vec2(tc12.x, tc3.y), 0.0).rgb * (w12.x * w3.y);
            return max(c / ((w12.x * w0.y) + (w0.x * w12.y) + (w12.x * w12.y) + (w3.x * w12.y) + (w12.x * w3.y)), vec3(0.0));
        }

        void main()
        {
            // from vUV, not gl_FragCoord: the stack draws at the DISPLAY viewport, whose origin need not be 0
            ivec2 sz = textureSize(uCur, 0);
            ivec2 pix = clamp(ivec2(vUV * vec2(sz)), ivec2(0), sz - 1);
            vec4 c = texelFetch(uCur, pix, 0);
            vec3 mn = c.rgb, mx = c.rgb;
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    vec3 n = texelFetch(uCur, clamp(pix + ivec2(x, y), ivec2(0), sz - 1), 0).rgb;
                    mn = min(mn, n); mx = max(mx, n);
                }
            vec2 velPx = textureLod(uVel, vUV, 0.0).rg;
            vec2 huv = vUV + velPx / vec2(textureSize(uVel, 0));
            // a moving pixel takes more of the frame -- a walking camera converges in a few frames, not ten
            float a = min(0.5, uBlend + length(velPx) * 0.05);
            if (any(lessThan(huv, vec2(0.0))) || any(greaterThan(huv, vec2(1.0)))) a = 1.0;
            vec3 h = clamp(SowxHistory(huv, vec2(sz)), mn, mx);
            frag = vec4(mix(h, c.rgb, a), c.a);
        }
        """;

    /// <summary>Off-device: the stage composes under the stack's contract, the resolve reprojects through the
    /// velocity target in ITS units and clamps to the neighbourhood, and the gate is one key.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"TaaStage.SelfCheck FAIL: {w}"); } }
        E(StageFs.Contains("vec4 SowxStage(vec4 c)") && ShaderContract.DisplaySafe(StageFs) && ShaderContract.SlotSafe(StageFs, "uHist"),
          "the stage is display space and reads the history");
        E(TaaFs.Contains("velPx / vec2(textureSize(uVel, 0))"), "velocity becomes uv through the VELOCITY target's size, not the frame's (#391)");
        E(TaaFs.Contains("clamp(SowxHistory(huv, vec2(sz)), mn, mx)") && TaaFs.Contains("vec3 SowxHistory(vec2 uv, vec2 size)"),
          "history is fetched Catmull-Rom and clamped to the 3x3 neighbourhood");
        E(TaaFs.Contains("min(0.5, uBlend + length(velPx) * 0.05)"), "a moving pixel converges faster than the resting share");
        E(TaaFs.Contains("a = 1.0;") && TaaFs.Contains("mix(h, c.rgb, a)"), "an off-screen history sample takes the current frame whole");
        E(ShaderContract.SlotSafe(TaaFs, "uCur", "uHist", "uVel"), "the resolve declares the samplers Prepare binds");
        _valid = true; Reset(); E(!_valid, "Reset drops the history");
        log.Info(ok ? "TaaStage.SelfCheck: passed" : "TaaStage.SelfCheck: FAILED");
        return ok;
    }
}
