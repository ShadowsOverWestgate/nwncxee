using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>#389: temporal anti-aliasing as a post-stack stage. The projection is jittered by a sub-pixel
/// Halton(2,3) offset every frame (<see cref="PostChain.Jitter"/>, applied at the one site Alloy reads it),
/// and this stage averages the jittered frames: the display-space history is reprojected through the
/// DILATED velocity target (#388/#392), variance-clipped in YCoCg against the 3x3 neighbourhood of the
/// current frame so stale history is rejected rather than ghosted, and blended in with
/// <see cref="PostChain.TaaBlend"/> of the current frame under inverse-luma weights.
/// The resolve happens in <see cref="Prepare"/> into an owned RGBA16F history pair; the stage draw itself
/// copies the new history out with the <c>post.taasharp</c> unsharp mask -- when TAA is on it is the LAST
/// stage (SMAA stands aside), so that copy is the dithered write to the engine's target, and the history
/// never carries either the dither or the sharpen.
/// <para>ON by default: TAA is the default anti-aliaser and <c>/post taa 0</c> hands the option back to
/// SMAA. Jitter and accumulation toggle together.</para></summary>
internal static unsafe class TaaStage
{
    private static ExtenderLog? _log;
    private static uint _fbo, _hist0, _hist1, _prog, _sampler, _stageProg;
    private static int _locCur, _locHist, _locVel, _locDepth, _locBlend, _locStageHist, _locJitterPx, _locUpsample;
    private static int _w, _h, _cur;
    private static bool _failed, _built, _valid;

    /// <summary>Latched: a program or target failed on this context; the jitter stands down with the stage.</summary>
    internal static bool Failed => _failed;
    /// <summary>The stage's fragment text: the new history, with the post-resolve sharpen (display space in,
    /// display space out). A temporal average is softer at an edge than a spatial filter is, which is the one
    /// thing TAA gets held against SMAA for; <c>post.taasharp</c> is a cross-tap unsharp mask that puts the
    /// contrast back. The taps are texel-exact fetches: the history is NEAREST at this stage's own size.</summary>
    internal const string StageFs = """
        uniform sampler2D uHist;     // this frame's resolved history (Prepare wrote it)
        uniform float uSharp;        // post.taasharp: the unsharp mask's amount, 0 = the raw resolve
        vec4 CxStage(vec4 c)
        {
            ivec2 sz = textureSize(uHist, 0);
            ivec2 p = clamp(ivec2(vUV * vec2(sz)), ivec2(0), sz - 1);
            vec3 h = texelFetch(uHist, p, 0).rgb;
            if (uSharp > 0.0)
            {
                vec3 n = texelFetch(uHist, clamp(p + ivec2(1, 0), ivec2(0), sz - 1), 0).rgb
                       + texelFetch(uHist, clamp(p - ivec2(1, 0), ivec2(0), sz - 1), 0).rgb
                       + texelFetch(uHist, clamp(p + ivec2(0, 1), ivec2(0), sz - 1), 0).rgb
                       + texelFetch(uHist, clamp(p - ivec2(0, 1), ivec2(0), sz - 1), 0).rgb;
                h = max(h + (h * 4.0 - n) * (uSharp * 0.25), vec3(0.0));
            }
            return vec4(h, c.a);
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
        _locDepth = GlProgram.Loc(_prog, "uDepth");
        _locJitterPx = GlProgram.Loc(_prog, "uJitterPx"); _locUpsample = GlProgram.Loc(_prog, "uUpsample");
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
            _sampler = Gl.LinearClampSampler();
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
    /// <summary>The current frame's (render) size, read off the texture each Prepare for the jitter scale.</summary>
    private static int _curW, _curH;

    internal static void Prepare(uint src, uint stageProg, int* viewport)
    {
        uint read = _cur == 0 ? _hist0 : _hist1, write = _cur == 0 ? _hist1 : _hist0;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, write, 0);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.PostHdr); Gl.BindTexture(Gl.TEXTURE_2D, src);
        // on the unit that already holds it -- any other unit's binding is a stage's input
        fixed (int* w = &_curW) Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_WIDTH, w);
        fixed (int* h = &_curH) Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_HEIGHT, h);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaEdges); Gl.BindTexture(Gl.TEXTURE_2D, read);
        Gl.BindSampler(TextureSlots.AaEdges, _sampler);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaArea); Gl.BindTexture(Gl.TEXTURE_2D, MotionVectors.Tex);
        Gl.BindSampler(TextureSlots.AaArea, _sampler);
        Gl.UseProgram(_prog);
        Gl.Uniform1i(_locCur, (int)TextureSlots.PostHdr);
        Gl.Uniform1i(_locHist, (int)TextureSlots.AaEdges);
        Gl.Uniform1i(_locVel, (int)TextureSlots.AaArea);
        // PostStack.Run bound the frame depth on this unit before the stage loop; it is the velocity
        // target's own size, which is what the dilation walks in.
        Gl.Uniform1i(_locDepth, (int)TextureSlots.PostBloom);
        Gl.Uniform1f(_locBlend, _valid ? PostChain.TaaBlend : 1f);
        // Upsampling: the current frame is render-sized and read bilinearly at the jitter-shifted position
        // of each display pixel, so it takes the history's LINEAR sampler for this draw (Run unbinds it).
        bool up = PostStack.Upsampling;
        if (up) Gl.BindSampler(TextureSlots.PostHdr, _sampler);
        Gl.Uniform1i(_locUpsample, up ? 1 : 0);
        // NDC jitter to render pixels: +j in NDC moved the image +j * size/2 pixels (Frame.cs, proj.M31)
        Gl.Uniform2f(_locJitterPx, PostChain.JitterX * 0.5f * _curW, PostChain.JitterY * 0.5f * _curH);
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
    /// than this display-sized frame, #391), so it is turned into uv through ITS OWN size, and it is DILATED
    /// -- the closest depth in the 3x3 names the velocity the whole neighbourhood reprojects with, so a thin
    /// silhouette carries its own motion instead of the background's. The history is fetched Catmull-Rom and
    /// VARIANCE-CLIPPED in YCoCg (mean +- gamma * stddev over the current 3x3, clipped along the line to the
    /// current sample): a min/max box over the same nine texels is wide enough to pass the ghost that made it,
    /// and clipping in a luma/chroma basis keeps the clamp off the hue. The blend is inverse-luma weighted
    /// (Karis), so one firefly texel cannot outvote ten frames of history, and a moving pixel takes a little
    /// more of the current frame per pixel of motion, so disocclusions resolve in a few frames instead of the
    /// full history length. An off-screen reprojection takes the current frame whole.</summary>
    internal const string TaaFs = """
        #version 460 core
        in vec2 vUV;
        out vec4 frag;
        uniform sampler2D uCur;      // this frame, display space, the stack's size
        uniform sampler2D uHist;     // last frame's history, LINEAR
        uniform sampler2D uVel;      // #388: prev - cur, render pixels, render size
        uniform sampler2D uDepth;    // the frame depth, render size: the dilation's closest-depth pick
        uniform float uBlend;        // the current frame's share at rest; 1 = no history
        uniform vec2 uJitterPx;      // this frame's projection jitter in RENDER pixels
        uniform int uUpsample;       // 1: uCur is render-sized, the history and this draw are display-sized

        // The clip box's half-width in standard deviations. Below 1 the history is yanked on every noisy
        // surface; above 1.5 a ghost fits inside it.
        const float CX_TAA_GAMMA = 1.25;

        vec3 CxToYCoCg(vec3 c)
        {
            return vec3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b, 0.5 * (c.r - c.b), -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
        }
        vec3 CxFromYCoCg(vec3 c)
        {
            float t = c.x - c.z;
            return max(vec3(t + c.y, c.x + c.z, t - c.y), vec3(0.0));
        }

        // Catmull-Rom through five bilinear taps (the 9-texel cubic with the corner taps folded): a bilinear
        // history resample every frame is the blur a naive TAA is known for, and this is the standard cure.
        vec3 CxHistory(vec2 uv, vec2 size)
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

        // The velocity of the CLOSEST fragment in the 3x3: the neighbour standing in front of this pixel is
        // the one whose motion its edge follows. An unresolved depth target reads all-equal and keeps the
        // centre tap, so the dilation degrades to the plain fetch rather than to a corner.
        vec2 CxDilatedVel(vec2 uv, vec2 vsz)
        {
            vec2 texel = 1.0 / vsz, best = vec2(0.0);
            float bestD = textureLod(uDepth, uv, 0.0).r;
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    vec2 o = vec2(x, y) * texel;
                    float d = textureLod(uDepth, uv + o, 0.0).r;
                    if (d < bestD) { bestD = d; best = o; }
                }
            return textureLod(uVel, uv + best, 0.0).rg;
        }

        // Clip q toward the box centre rather than clamping it per channel: a clamp moves the sample onto a
        // face of the box and shifts its hue, a clip keeps it on the line to the current colour.
        vec3 CxClipToBox(vec3 mn, vec3 mx, vec3 q)
        {
            vec3 center = 0.5 * (mx + mn), extent = max(0.5 * (mx - mn), vec3(1e-5));
            vec3 v = q - center;
            vec3 a = abs(v / extent);
            float m = max(max(a.x, a.y), a.z);
            return m > 1.0 ? center + v / m : q;
        }

        void main()
        {
            // from vUV, not gl_FragCoord: the stack draws at the DISPLAY viewport, whose origin need not be 0
            ivec2 sz = textureSize(uCur, 0);
            vec2 hsz = vec2(textureSize(uHist, 0));
            ivec2 pix;
            vec4 c;
            float conf = 1.0;
            if (uUpsample != 0)
            {
                // this display pixel's clean render position, shifted to where THIS frame drew it: a texel
                // at render position t holds the scene at t - jitter, so the clean point sits at t + jitter
                vec2 sp = vUV * vec2(sz) + uJitterPx;
                pix = clamp(ivec2(floor(sp)), ivec2(0), sz - 1);
                c = textureLod(uCur, sp / vec2(sz), 0.0);
                // how close the frame's sample fell to this pixel: at a texel centre the frame speaks fully,
                // between two texels the bilinear read is a blur and the history keeps more of its say
                conf = 1.0 - smoothstep(0.0, 0.7071, length(fract(sp) - 0.5));
            }
            else
            {
                pix = clamp(ivec2(vUV * vec2(sz)), ivec2(0), sz - 1);
                c = texelFetch(uCur, pix, 0);
            }
            vec3 cy = CxToYCoCg(c.rgb);
            vec3 m1 = vec3(0.0), m2 = vec3(0.0);
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    vec3 n = CxToYCoCg(texelFetch(uCur, clamp(pix + ivec2(x, y), ivec2(0), sz - 1), 0).rgb);
                    m1 += n; m2 += n * n;
                }
            vec3 mean = m1 / 9.0;
            vec3 sigma = sqrt(max(m2 / 9.0 - mean * mean, vec3(0.0)));
            // the current sample is always inside its own box: a lone bright texel would otherwise clip
            // itself away and the resolve would never converge on it
            vec3 mn = min(mean - CX_TAA_GAMMA * sigma, cy), mx = max(mean + CX_TAA_GAMMA * sigma, cy);
            vec2 vsz = vec2(textureSize(uVel, 0));
            vec2 velPx = CxDilatedVel(vUV, vsz);
            vec2 huv = vUV + velPx / vsz;
            // a moving pixel takes more of the frame -- a walking camera converges in a few frames, not ten
            float a = uBlend >= 1.0 ? 1.0 : min(0.5, uBlend + length(velPx) * 0.05);
            if (uUpsample != 0) a = mix(a * 0.25, a, conf);
            if (any(lessThan(huv, vec2(0.0))) || any(greaterThan(huv, vec2(1.0)))) a = 1.0;
            vec3 hy = CxClipToBox(mn, mx, CxToYCoCg(CxHistory(huv, hsz)));
            vec3 h = CxFromYCoCg(hy);
            // inverse-luma weights (Karis): the brighter of the two speaks quieter, so a firefly that lives
            // one frame cannot outvote the history it lands on
            float wc = a / (1.0 + max(cy.x, 0.0)), wh = (1.0 - a) / (1.0 + max(hy.x, 0.0));
            frag = vec4((c.rgb * wc + h * wh) / max(wc + wh, 1e-5), c.a);
        }
        """;

    /// <summary>Off-device: the stage composes under the stack's contract, the resolve dilates and reprojects
    /// through the velocity target in ITS units, variance-clips the history in YCoCg, and the gate is one key.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(StageFs.Contains("vec4 CxStage(vec4 c)") && ShaderContract.DisplaySafe(StageFs) && ShaderContract.SlotSafe(StageFs, "uHist"),
          "the stage is display space and reads the history");
        check.Expect(StageFs.Contains("uniform float uSharp;") && StageFs.Contains("h * 4.0 - n") && StageFs.Contains("if (uSharp > 0.0)"),
          "the copy-out carries the unsharp mask and is the raw history at taasharp 0");
        check.Expect(Post.TaaSharp.Default > 0f && Post.TaaSharp.Min == 0f, "post.taasharp ships sharpening, and 0 is the A/B back to the raw resolve");
        check.Expect(Post.Taa.Default, "TAA is the DEFAULT anti-aliaser: a fresh cx.cfg resolves temporally and SMAA stands aside");
        check.Expect(TaaFs.Contains("vec2 velPx = CxDilatedVel(vUV, vsz);") && TaaFs.Contains("vec2 huv = vUV + velPx / vsz;")
          && TaaFs.Contains("vec2 vsz = vec2(textureSize(uVel, 0));"),
          "velocity becomes uv through the VELOCITY target's size, not the frame's (#391)");
        check.Expect(TaaFs.Contains("vec2 CxDilatedVel(vec2 uv, vec2 vsz)") && TaaFs.Contains("if (d < bestD) { bestD = d; best = o; }")
          && TaaFs.Contains("float bestD = textureLod(uDepth, uv, 0.0).r;"),
          "the velocity is dilated to the CLOSEST depth in the 3x3, and an all-equal depth keeps the centre tap");
        check.Expect(TaaFs.Contains("vec3 CxHistory(vec2 uv, vec2 size)") && TaaFs.Contains("CxToYCoCg(CxHistory(huv, hsz))")
          && TaaFs.Contains("vec2 hsz = vec2(textureSize(uHist, 0));"),
          "history is fetched Catmull-Rom at ITS OWN size");
        check.Expect(TaaFs.Contains("vec3 sigma = sqrt(max(m2 / 9.0 - mean * mean, vec3(0.0)));")
          && TaaFs.Contains("vec3 mn = min(mean - CX_TAA_GAMMA * sigma, cy), mx = max(mean + CX_TAA_GAMMA * sigma, cy);")
          && TaaFs.Contains("vec3 CxClipToBox(vec3 mn, vec3 mx, vec3 q)"),
          "the clamp is a VARIANCE clip (mean +- gamma * stddev) along the line to the current sample, not a min/max box");
        check.Expect(TaaFs.Contains("const float CX_TAA_GAMMA = 1.25;"), "...at the width one standard TAA ships");
        check.Expect(TaaFs.Contains("vec3 CxToYCoCg(vec3 c)") && TaaFs.Contains("vec3 CxFromYCoCg(vec3 c)"), "the clip happens in a luma/chroma basis");
        // #391: the upsampling arm reads the render-sized frame at the jitter-shifted position, with the
        // same sign the velocity pass takes the jitter back out with (ndc - uJitter: the frame is at +j).
        check.Expect(TaaFs.Contains("vec2 sp = vUV * vec2(sz) + uJitterPx;") && TaaFs.Contains("c = textureLod(uCur, sp / vec2(sz), 0.0);"),
          "upsampling samples the frame at clean position + jitter");
        check.Expect(TaaFs.Contains("if (uUpsample != 0) a = mix(a * 0.25, a, conf);"), "a between-texel sample leans on the history");
        check.Expect(ShaderContract.SlotSafe(TaaFs, "uCur", "uHist", "uVel", "uDepth"), "the resolve declares the samplers Prepare binds");
        check.Expect(TaaFs.Contains("uBlend >= 1.0 ? 1.0 : min(0.5, uBlend + length(velPx) * 0.05)"), "a moving pixel converges faster than the resting share");
        check.Expect(TaaFs.Contains("float wc = a / (1.0 + max(cy.x, 0.0)), wh = (1.0 - a) / (1.0 + max(hy.x, 0.0));"),
          "the blend is inverse-luma weighted: a one-frame firefly cannot outvote the history");
        check.Expect(TaaFs.Contains("a = 1.0;") && TaaFs.Contains("(c.rgb * wc + h * wh) / max(wc + wh, 1e-5)"),
          "an off-screen history sample takes the current frame whole");
        _valid = true; Reset(); check.Expect(!_valid, "Reset drops the history");
        log.Info(check.Ok ? "TaaStage.SelfCheck: passed" : "TaaStage.SelfCheck: FAILED");
        return check.Ok;
    }
}
