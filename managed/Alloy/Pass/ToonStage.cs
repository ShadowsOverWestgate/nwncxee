using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Cel shading as a post-stack stage, on the client's own Toon video option
/// (<c>CClientOptions::SetEnableFBEffectToon</c> @0x66ae70 ORs 0x20 into the option mask
/// <c>ConfigureFBEffectPostProcessing</c> publishes as int param 2), and on that option's own three sliders.
/// Nothing of the engine's <c>inc_postpr_toon</c> is carried over -- only the idea, and the dials the player
/// already has.
/// <para>The ink is a plane fit: each neighbour pair PREDICTS this pixel's depth (averaged in raw window z,
/// the space a plane is affine in) and only the residual inks, taken relative to view distance. A flat
/// surface at any grazing angle predicts itself exactly, so the stage cannot pen the floor the way an
/// absolute <c>|dz|</c> threshold does, while a silhouette or a crease -- a break in the depth GRADIENT --
/// fires at any distance. The colour edge is the same shape over display luminance, and both are one
/// residual/threshold ratio, so the sliders mean what they say: Depth Edge Threshold is a fraction of view
/// distance, Colour Edge Threshold a luminance step, and Edge Hardness the width of the ramp either side of
/// the threshold (0 = off, as in the stock stage). Ink darkens the colour underneath rather than painting
/// black, which keeps a lit scene from going flat.</para>
/// <para>Bands are OFF by default (<c>/post toonbands 1</c>). On, they quantise LUMINANCE and rescale the
/// colour, so hue and saturation survive the step (per-channel RGB quantisation is what makes the stock look
/// muddy), with the levels at band CENTRES so shadows land on a dark step instead of crushing to black, and
/// each boundary smoothstepped across one <c>fwidth</c>.</para>
/// <para>The stage sits ahead of taa/aa in the stack, so the ink is what the anti-aliaser smooths.
/// Live-UNVERIFIED: built against the option, not yet seen with it on.</para></summary>
internal static unsafe class ToonStage
{
    /// <summary><c>POSTPROCESSING_TOON 0x0020</c> in <c>inc_postpr.shd</c>; confirmed as the bit the
    /// engine's own setter ORs in (<c>or $0x20</c> @0x66ae7c).</summary>
    internal const int MaskToon = 0x20;   // not-an-offset: engine FB-effect mask bit

    private static ExtenderLog? _log;
    private static Matrix4x4 _proj;
    private static float* _depthEdge, _colorEdge, _hardness;
    private static int _locProj, _locBands, _locInk, _locEdge, _locDepthThresh, _locColorThresh, _locHardness;
    private static uint _prog;
    /// <summary>Stock defaults, and what the stage assumes if a slider's global is unresolved.</summary>
    private const float StockDepthEdge = 0.02f, StockColorEdge = 1f, StockHardness = 0.8f;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _depthEdge = (float*)Native.Sym(SymbolMap.ToonDepthEdge);
        _colorEdge = (float*)Native.Sym(SymbolMap.ToonColorEdge);
        _hardness = (float*)Native.Sym(SymbolMap.ToonEdgeHardness);
        if (_depthEdge == null || _colorEdge == null || _hardness == null)
            log.Info("ToonStage: a Toon slider global is unresolved -- the stage runs on the stock values "
                   + $"({StockDepthEdge:F2} / {StockColorEdge:F2} / {StockHardness:F2}) and the sliders are inert");
    }

    /// <summary>Context loss: the locations belong to a program the stack rebuilds.</summary>
    internal static void Forget() { _prog = 0; }

    /// <summary>From the frame, beside <see cref="PostStack.Begin"/>: the projection this frame drew with.
    /// Only <c>[2].z</c>/<c>[3].z</c> are read, which the TAA jitter does not touch.</summary>
    internal static void SetProj(Matrix4x4* proj) => _proj = *proj;

    private static float DepthEdge => _depthEdge == null ? StockDepthEdge : *_depthEdge;
    private static float ColorEdge => _colorEdge == null ? StockColorEdge : *_colorEdge;
    /// <summary>The stock stage's own off switch: at hardness 0 it returns before inking anything.</summary>
    private static float Hardness => _hardness == null ? StockHardness : *_hardness;

    /// <summary>The dials are <c>post.toonbands / toonink / toonedge</c>; 1 band = no posterise, the option's
    /// own sliders are the default look. No knob for the thresholds or the hardness: those are the player's
    /// own sliders (a knob the client already owns does not get a second home here).</summary>
    internal static string Describe()
        => $"bands {Post.ToonBands.V:F0} ink {(Hardness <= 0f ? "OFF (edge hardness 0)" : Post.ToonInk.V.ToString("F2"))} "
         + $"edge {Post.ToonEdge.V:F1}px | sliders: depth {DepthEdge:F3} colour {ColorEdge:F2} hardness {Hardness:F2}";

    /// <summary>From <see cref="PostStack.Run"/>, ahead of the stage draw: the stage's own uniforms onto its
    /// own program, which the caller then draws with. The sliders are read here, so moving one in the video
    /// options moves the next frame. Binds nothing and leaves no target behind. Hot: allocates nothing.</summary>
    internal static void Prepare(uint src, uint prog, int* viewport)
    {
        if (prog != _prog)
        {
            _prog = prog;
            _locProj = GlProgram.Loc(prog, "uProj"); _locBands = GlProgram.Loc(prog, "uBands");
            _locInk = GlProgram.Loc(prog, "uInk"); _locEdge = GlProgram.Loc(prog, "uEdge");
            _locDepthThresh = GlProgram.Loc(prog, "uDepthThresh");
            _locColorThresh = GlProgram.Loc(prog, "uColorThresh");
            _locHardness = GlProgram.Loc(prog, "uHardness");
        }
        float hardness = Hardness;
        Gl.UseProgram(prog);
        fixed (Matrix4x4* p = &_proj) Gl.UniformMatrix4fv(_locProj, 1, 0, (float*)p);
        Gl.Uniform1f(_locBands, Post.ToonBands.V);
        Gl.Uniform1f(_locInk, hardness <= 0f ? 0f : Post.ToonInk.V);
        Gl.Uniform1f(_locEdge, Post.ToonEdge.V);
        Gl.Uniform1f(_locDepthThresh, MathF.Max(DepthEdge, 1e-4f));
        Gl.Uniform1f(_locColorThresh, MathF.Max(ColorEdge, 1e-4f));
        Gl.Uniform1f(_locHardness, Math.Clamp(hardness, 0f, 1f));
    }

    /// <summary>The stage text. Display space in, display space out; the size comes off the depth texture
    /// (the render target's, which is also the ping-pong's under TAA upsampling) so nothing can drift.</summary>
    internal static string StageFs => ShaderSource.Get("ToonStage.StageFs.glsl");

    /// <summary>Off-device: the maths that makes this better than the stock stage is the maths that is here,
    /// and the three sliders it is the player's copy of.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(MaskToon == 0x20, "POSTPROCESSING_TOON is 0x20 (inc_postpr.shd, and the engine setter's `or $0x20`)");
        check.Expect(StageFs.Contains("uProj[3].z / (depth * 2.0 - 1.0 + uProj[2].z)"), "the linearisation is SSAO's");
        check.Expect(StageFs.Contains("CxViewDist(zx) - d0"), "the ink is a plane fit against the neighbours' prediction");
        // ...and the prediction is made in RAW depth, which is the property the string cannot assert. A plane
        // is affine in window z, so the average of two neighbours IS the centre; average linearised distances
        // instead and the same plane reads as an edge, which is the bug this replaced.
        {
            const float near = 0.1f, far = 400f;
            float m22 = (far + near) / (near - far), m32 = 2f * far * near / (near - far);
            float Dist(float d) => m32 / (d * 2f - 1f + m22);
            float z0 = 0.5f, zl = 0.45f, zr = 0.55f;   // affine in screen space: a plane, seen at an angle
            float fit = MathF.Abs(Dist((zl + zr) * 0.5f) - Dist(z0)) / Dist(z0);
            float linear = MathF.Abs(Dist(zl) + Dist(zr) - 2f * Dist(z0)) / Dist(z0);
            check.Expect(fit < 1e-5f, "the plane fit reads ZERO on a plane at an angle");
            check.Expect(linear > 1e-2f, "...where differencing linearised distance would have inked it");
        }
        check.Expect(StageFs.Contains("uniform float uDepthThresh;") && StageFs.Contains("uniform float uColorThresh;")
          && StageFs.Contains("uniform float uHardness;"), "the three Toon sliders are the stage's dials");
        check.Expect(StageFs.Contains("/ (d0 * uDepthThresh)") && StageFs.Contains("/ uColorThresh"),
          "both thresholds are a residual RATIO, so the sliders mean what they say");
        check.Expect(StageFs.Contains("mix(0.9, 0.02, uHardness)"), "hardness is the width of the ramp at the threshold");
        check.Expect(StageFs.Contains("fwidth(t)"), "the band boundary is antialiased across one fwidth");
        check.Expect(StageFs.IndexOf("uBands >= 2.0", StringComparison.Ordinal) < StageFs.IndexOf("uInk > 0.0", StringComparison.Ordinal),
          "the bands run BEFORE the ink: ink on a banded colour is one line, and it stays out of the fwidth");
        check.Expect(Post.ToonBands.Default == 1f && Post.ToonBands.Min == 1f, "bands are OFF by default -- the sliders are the default look");
        check.Expect(Post.G.Get("toonsens") == null && Post.G.Get("toondepth") == null, "the sliders have no second home under /post");
        check.Expect(Post.ToonEdge.TryParse("99", out float edge) && edge == 8f, "the edge width is clamped");
        log.Info(check.Ok ? "ToonStage.SelfCheck: passed" : "ToonStage.SelfCheck: FAILED");
        return check.Ok;
    }
}
