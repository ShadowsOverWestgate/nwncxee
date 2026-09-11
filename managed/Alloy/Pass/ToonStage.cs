using System;
using System.Numerics;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>Cel shading as a post-stack stage, on the client's own Toon video option
/// (<c>CClientOptions::SetEnableFBEffectToon</c> @0x66ae70 ORs 0x20 into the option mask
/// <c>ConfigureFBEffectPostProcessing</c> publishes as int param 2), and on that option's own three
/// sliders. Nothing of the engine's <c>inc_postpr_toon</c> is carried over -- only the idea, and the
/// dials the player already has.
/// <para>The ink is a plane fit: each neighbour pair PREDICTS this pixel's depth (averaged in raw window
/// z, the space a plane is affine in) and only the residual inks, taken relative to view distance. A flat
/// surface at any grazing angle predicts itself exactly, so the stage cannot pen the floor the way an
/// absolute <c>|dz|</c> threshold does, while a silhouette or a crease -- a break in the depth GRADIENT --
/// fires at any distance. The colour edge is the same shape over display luminance, and both are one
/// residual/threshold ratio, so the sliders mean what they say: Depth Edge Threshold is a fraction of view
/// distance, Colour Edge Threshold a luminance step, and Edge Hardness the width of the ramp either side
/// of the threshold (0 = off, as in the stock stage). Ink darkens the colour underneath rather than
/// painting black, which is what keeps a lit scene from going flat.</para>
/// <para>Bands are OFF by default (<c>/post toonbands 1</c>): the ink is the look the option's users are
/// after, and the posterise is there for whoever wants the full comic. When it is on it quantises
/// LUMINANCE and rescales the colour, so hue and saturation survive the step -- per-channel RGB
/// quantisation is what makes the stock look muddy -- with the levels at band CENTRES so shadows land on
/// a dark step instead of crushing to black, and each boundary smoothstepped across one <c>fwidth</c>.</para>
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
    /// <summary>1 = no posterise: the option's own sliders are the default look, bands are opt-in.</summary>
    private const float BootBands = 1f, BootInk = 1f, BootEdge = 1f;
    private static float _bands = BootBands, _ink = BootInk, _edge = BootEdge;

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

    internal static string Describe()
        => $"bands {_bands:F0} ink {(Hardness <= 0f ? "OFF (edge hardness 0)" : _ink.ToString("F2"))} "
         + $"edge {_edge:F1}px | sliders: depth {DepthEdge:F3} colour {ColorEdge:F2} hardness {Hardness:F2}";

    /// <summary>Behind <c>/post</c>, through <see cref="PostStack.Tune"/>. Returns the old value, NaN for a
    /// key that is not ours. No key for the thresholds or the hardness: those are the player's own sliders
    /// (ADR 0013 -- a knob the client already owns does not get a second home here).</summary>
    internal static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            case "toonbands": old = _bands; _bands = Math.Clamp(v, 1f, 32f); break;
            case "toonink":   old = _ink; _ink = Math.Clamp(v, 0f, 1f); break;
            case "toonedge":  old = _edge; _edge = Math.Clamp(v, 1f, 8f); break;
            default: return float.NaN;
        }
        _log?.Info($"ToonStage: /post {key} {v:F2} (was {old:F2}) -> {Describe()}");
        return old;
    }

    internal static void ResetTuning() { _bands = BootBands; _ink = BootInk; _edge = BootEdge; }

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
        Gl.Uniform1f(_locBands, _bands);
        Gl.Uniform1f(_locInk, hardness <= 0f ? 0f : _ink);
        Gl.Uniform1f(_locEdge, _edge);
        Gl.Uniform1f(_locDepthThresh, MathF.Max(DepthEdge, 1e-4f));
        Gl.Uniform1f(_locColorThresh, MathF.Max(ColorEdge, 1e-4f));
        Gl.Uniform1f(_locHardness, Math.Clamp(hardness, 0f, 1f));
    }

    /// <summary>The stage text. Display space in, display space out; the size comes off the depth texture
    /// (the render target's, which is also the ping-pong's under TAA upsampling) so nothing can drift.</summary>
    internal const string StageFs = """
        uniform mat4 uProj;          // this frame's projection, raw engine bytes (see SowxViewDist)
        uniform float uBands;        // luminance steps; at 1 (the default) the posterise is off
        uniform float uInk;          // outline strength, 0..1; 0 when the option's Edge Hardness is 0
        uniform float uEdge;         // outline half-width in pixels
        uniform float uDepthThresh;  // Toon Depth Edge Threshold: the residual as a fraction of view distance
        uniform float uColorThresh;  // Toon Color Edge Threshold: the residual as a luminance step
        uniform float uHardness;     // Toon Edge Hardness: 0 = a wide soft ramp, 1 = a hard line

        float SowxLuma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

        // Positive view distance: z_clip = z_v*M22 + M32, w_clip = -z_v, so z_v = -M32 / (ndc + M22).
        float SowxViewDist(float depth)
        {
            return uProj[3].z / (depth * 2.0 - 1.0 + uProj[2].z);
        }

        vec4 SowxStage(vec4 c)
        {
            if (uBands >= 2.0)
            {
                float lum = SowxLuma(c.rgb);
                // The half shift puts the levels at band CENTRES and the transitions at the boundaries: the
                // darkest band is 0.5/uBands, not 0, so the shadows land on a dark step instead of crushing
                // to black, and pure white still reaches 1.0.
                float t = lum * uBands - 0.5;
                // One fwidth wide: the band boundary is a drawn edge at any resolution, never a stair-step.
                float w = clamp(fwidth(t) * 0.5, 1e-3, 0.5);
                float q = (floor(t) + 0.5 + smoothstep(0.5 - w, 0.5 + w, fract(t))) / uBands;
                c.rgb = clamp(c.rgb * (q / max(lum, 1e-4)), 0.0, 1.0);
            }
            if (uInk > 0.0)
            {
                vec2 o = uEdge / vec2(textureSize(uDepth, 0));
                // LOAD-BEARING: the neighbours are averaged in RAW depth, which is the space a plane is
                // affine in -- window z is linear across a triangle, view distance is not. Averaging
                // linearised distances instead pens the ground at grazing angles, which is exactly the
                // stock stage's failure.
                float z0 = texture(uDepth, vUV).r;
                float zx = (texture(uDepth, vUV - vec2(o.x, 0.0)).r + texture(uDepth, vUV + vec2(o.x, 0.0)).r) * 0.5;
                float zy = (texture(uDepth, vUV - vec2(0.0, o.y)).r + texture(uDepth, vUV + vec2(0.0, o.y)).r) * 0.5;
                // The plane fit: what the two neighbours PREDICT this pixel's distance to be, against what it
                // is. Zero on a plane at any angle; a silhouette or a crease -- a break in the depth gradient
                // -- is the whole residual. Over the threshold as a FRACTION of view distance, so the slider
                // holds from the boots to the horizon.
                float d0 = SowxViewDist(z0);
                float e = max(abs(SowxViewDist(zx) - d0), abs(SowxViewDist(zy) - d0)) / (d0 * uDepthThresh);
                // The colour edge is the same plane fit over display luminance, and it is the half that
                // inks a drawn detail on flat geometry. At the option's stock 1.0 a full-range luminance
                // step is needed, so it stays out of the way until the slider comes down.
                float l0 = SowxLuma(texture(uSrc, vUV).rgb);
                float lx = (SowxLuma(texture(uSrc, vUV - vec2(o.x, 0.0)).rgb) + SowxLuma(texture(uSrc, vUV + vec2(o.x, 0.0)).rgb)) * 0.5;
                float ly = (SowxLuma(texture(uSrc, vUV - vec2(0.0, o.y)).rgb) + SowxLuma(texture(uSrc, vUV + vec2(0.0, o.y)).rgb)) * 0.5;
                e = max(e, max(abs(lx - l0), abs(ly - l0)) / uColorThresh);
                // Hardness is the ramp either side of the threshold: soft and wide at 0, a hard line at 1.
                float soft = mix(0.9, 0.02, uHardness);
                // AFTER the bands: ink on the banded colour is one uniform dark line, and it keeps the
                // step out of the fwidth() the band boundary measures itself with.
                c.rgb *= mix(1.0, 0.12, smoothstep(1.0 - soft, 1.0 + soft, e) * uInk);
            }
            return c;
        }
        """;

    /// <summary>Off-device: the maths that makes this better than the stock stage is the maths that is here,
    /// and the three sliders it is the player's copy of.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        ResetTuning();
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"ToonStage.SelfCheck FAIL: {w}"); } }
        E(MaskToon == 0x20, "POSTPROCESSING_TOON is 0x20 (inc_postpr.shd, and the engine setter's `or $0x20`)");
        E(StageFs.Contains("uProj[3].z / (depth * 2.0 - 1.0 + uProj[2].z)"), "the linearisation is SSAO's");
        E(StageFs.Contains("SowxViewDist(zx) - d0"), "the ink is a plane fit against the neighbours' prediction");
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
            E(fit < 1e-5f, "the plane fit reads ZERO on a plane at an angle");
            E(linear > 1e-2f, "...where differencing linearised distance would have inked it");
        }
        E(StageFs.Contains("uniform float uDepthThresh;") && StageFs.Contains("uniform float uColorThresh;")
          && StageFs.Contains("uniform float uHardness;"), "the three Toon sliders are the stage's dials");
        E(StageFs.Contains("/ (d0 * uDepthThresh)") && StageFs.Contains("/ uColorThresh"),
          "both thresholds are a residual RATIO, so the sliders mean what they say");
        E(StageFs.Contains("mix(0.9, 0.02, uHardness)"), "hardness is the width of the ramp at the threshold");
        E(StageFs.Contains("fwidth(t)"), "the band boundary is antialiased across one fwidth");
        E(StageFs.IndexOf("uBands >= 2.0", StringComparison.Ordinal) < StageFs.IndexOf("uInk > 0.0", StringComparison.Ordinal),
          "the bands run BEFORE the ink: ink on a banded colour is one line, and it stays out of the fwidth");
        E(BootBands == 1f && !(BootBands >= 2f), "bands are OFF by default -- the sliders are the default look");
        E(float.IsNaN(Tune("nope", 1f)) && float.IsNaN(Tune("toonsens", 1f)),
          "a key that is not ours is reported, and the sliders have no second home under /post");
        E(Tune("toonbands", 3f) == BootBands && _bands == 3f, "/post toonbands moves the stage");
        E(Tune("toonedge", 99f) == BootEdge && _edge == 8f, "the edge width is clamped");
        ResetTuning();
        E(_bands == BootBands && _edge == BootEdge, "/post reset restores the boot values");
        log.Info(ok ? "ToonStage.SelfCheck: passed" : "ToonStage.SelfCheck: FAILED");
        return ok;
    }
}
