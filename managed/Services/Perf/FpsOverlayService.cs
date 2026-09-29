using System;
using CX.Framework;
using CX.Ui;      // a genuine UI client: draws through the text rig and registers a UiHost window
using CX.Alloy;

namespace CX.Services.Perf;

/// <summary>
/// #79 (map #68, spec #77 M1): always-available on-screen FPS + frame-time readout -- the measurement
/// instrument for every M1 A/B gate. One small top-right window drawn through the shared batch rig.
/// <c>fps.enabled</c> (<see cref="FpsKnobs"/>: the panel row, <c>/fps enabled 1</c>, <c>cx.cfg</c>) is its
/// visibility, and persists; it ships off.
/// <para>Hot path (every in-game frame) allocates nothing: dt accumulation is four longs; the text +
/// vertex bake runs every <see cref="UpdateEvery"/> frames and writes into preallocated arrays.</para>
/// </summary>
public sealed class FpsOverlayService : IExtenderService
{
    private const int UpdateEvery = 30;
    private const float Pad = 4f;
    /// <summary>60Hz frame budget (ms): at/under = green readout, over = amber.</summary>
    private const float FrameBudgetMs = 16.7f;

    private static ExtenderLog? _log;

    private static long _last, _sum, _max;
    private static int _n;

    private static readonly char[] _text = new char[96];
    private static int _textLen;
    /// <summary>The backing rect, then per char a body and at most two rings of eight halo quads.</summary>
    private static readonly float[] _verts = new float[(96 * 17 + 1) * 6 * VertexBatch.Floats];
    private static int _vertCount;
    private static float _bakedW, _bakedH;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        UiCategory.Declare();
        FpsKnobs.G.Status = () => $"{FpsKnobs.G.Report()} (typeface -> {FontRegistry.Effective(FpsKnobs.Text.Typeface.V)})";
        ServerPing.SelfCheck(log);
        new Window(0, 0, 0, 0) { Focusable = false, DrawContent = Draw };
        MigrateFont(log);
        if (FpsKnobs.Enabled.V) FpsKnobs.Text.Face(TextRig.Atlas, cold: true);
        log.Info($"FpsOverlayService: registered ({(FpsKnobs.Enabled.V ? "shown" : "hidden")}; fps.enabled)");
    }

    /// <summary><c>fps.font</c> held the Chat-only typeface name before it became the text size: a stored name that is
    /// not a size moves to <c>fps.typeface</c> (a set, so the file is rewritten), or is dropped out loud.</summary>
    private static void MigrateFont(ExtenderLog log)
    {
        if (Settings.Raw(FpsKnobs.Text.Size!.Key) is not string raw || int.TryParse(raw, out _)) return;
        bool moved = FpsKnobs.Text.Typeface.Parse(raw);
        log.Info(moved ? $"FpsOverlayService: {FpsKnobs.Text.Size.Key}={raw} is now {FpsKnobs.Text.Typeface.Key} (the size row took the key)"
                       : $"FpsOverlayService: {FpsKnobs.Text.Size.Key}={raw} is no size and no known typeface; dropped");
    }

    /// <summary>The master's set (cold, the setter's thread): restart the accumulators and, when shown, request
    /// the readout's face (a miss bakes).</summary>
    internal static void VisibilityChanged()
    {
        _last = 0; _sum = 0; _max = 0; _n = 0; _vertCount = 0;
        if (FpsKnobs.Enabled.V) FpsKnobs.Text.Face(TextRig.Atlas, cold: true);
        _log?.Info($"FpsOverlayService: fps.enabled -> {(FpsKnobs.Enabled.V ? "on" : "off")}");
    }

    /// <summary>A text-row set (cold, the setter's thread): bake the new face if the readout is up.</summary>
    internal static void FaceChanged()
    {
        if (FpsKnobs.Enabled.V) FpsKnobs.Text.Face(TextRig.Atlas, cold: true);
    }

    /// <summary>Per-frame draw (render thread, hot). Accumulates present-to-present dt; every
    /// <see cref="UpdateEvery"/> frames re-bakes "FPS nnn  n.nn ms" into the persistent vertex buffer.</summary>
    private static void Draw()
    {
        if (!FpsKnobs.Enabled.V || !GameGate.InGame) { _last = 0; return; }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_last != 0)
        {
            long dt = now - _last;
            _sum += dt; _n++;
            if (dt > _max) _max = dt;
            if (_n >= UpdateEvery)
            {
                float avgMs = _sum * 1000f / System.Diagnostics.Stopwatch.Frequency / _n;
                Bake(avgMs > 0.0001f ? 1000f / avgMs : 0f, avgMs,
                     _max * 1000f / System.Diagnostics.Stopwatch.Frequency);
                _sum = 0; _n = 0; _max = 0;
            }
        }
        _last = now;
        if (_vertCount > 0 && TextRig.ViewportW > 0)
            TextRig.DrawBatch(_verts, _vertCount, 1f,
                TextRig.ViewportW - _bakedW - 6f, 6f);
    }

    /// <summary>Re-bake the readout at origin (drawn via uOffset translation): dark backing rect + one
    /// line of atlas glyphs. Zero-alloc -- digits are written straight into the char buffer.</summary>
    private static void Bake(float fps, float avgMs, float maxMs)
    {
        GlyphAtlas? atlas = FpsKnobs.Text.Face(TextRig.Atlas);
        if (atlas == null) { _vertCount = 0; return; }

        ServerPing.Refresh();

        _textLen = 0;
        Append("FPS "); AppendNum(fps, 0);
        Append("  "); AppendNum(avgMs, 1); Append(" ms");
        Append("  max "); AppendNum(maxMs, 1);
        // #83 acceptance: the Alloy path's clustered light rig has to show its GPU cost and its light count
        // where the frame budget is read, not only in the period log.
        if (AlloyLights.LastLightCount > 0)
        {
            Append("  L"); AppendNum(AlloyLights.LastLightCount, 0);
            Append(" cl "); AppendNum(AlloyLights.LastBuildMs, 2); Append(" ms");
        }
        // #187: every view is a bake, so "sh n" is views baked this frame -- the number the removal's cost is
        // read off. The ms is CPU submission only (see CasterPass.LastShadowMs).
        if (CasterPass.LastViews > 0)
        {
            Append("  sh "); AppendNum(CasterPass.LastViews, 0);
            Append(" "); AppendNum(CasterPass.LastShadowMs, 2); Append(" ms");
        }

        // The client keeps no live latency of its own -- ServerPing measures it (BNLM echo).
        // Hidden until a reply lands, so a single-player or unreachable server shows nothing.
        if (ServerPing.LastMs >= 0)
        {
            Append("  ping "); AppendNum(ServerPing.LastMs, 0); Append(" ms");
        }

        float w = 0f;
        for (int i = 0; i < _textLen; i++)
            w += atlas.TryGet(_text[i], out Glyph g) ? g.Advance : atlas.AdvanceOf(new System.Text.Rune(_text[i]));
        _bakedW = w + Pad * 2f;
        _bakedH = atlas.LineHeight + Pad * 2f;

        _vertCount = 0;
        Quad(0, 0, _bakedW, _bakedH, atlas.SolidU, atlas.SolidV, atlas.SolidU, atlas.SolidV, 0f, 0f, 0f, 0.55f);
        float r = avgMs <= FrameBudgetMs ? 0.55f : 1f, g2 = avgMs <= FrameBudgetMs ? 0.95f : 0.75f, b = 0.55f;
        float baseline = Pad + atlas.Ascent;
        Span<float> o = TextStyle.HaloOffsets(FpsKnobs.Text.HaloPx, stackalloc float[TextStyle.MaxHaloFloats]);
        for (int pass = o.Length > 0 ? 0 : 1; pass < 2; pass++)
        {
            float pen = Pad;
            for (int i = 0; i < _textLen; i++)
            {
                if (!atlas.TryGet(_text[i], out Glyph gl)) { pen += atlas.AdvanceOf(new System.Text.Rune(_text[i])); continue; }
                float x0 = pen + gl.XOff, y0 = baseline + gl.YOff;
                if (gl.W > 0 && pass == 1) Quad(x0, y0, x0 + gl.W, y0 + gl.H, gl.U0, gl.V0, gl.U1, gl.V1, r, g2, b, 1f);
                else if (gl.W > 0)
                    for (int h = 0; h < o.Length; h += 2)
                        Quad(x0 + o[h], y0 + o[h + 1], x0 + gl.W + o[h], y0 + gl.H + o[h + 1], gl.U0, gl.V0, gl.U1, gl.V1, 0f, 0f, 0f, 1f);
                pen += gl.Advance;
            }
        }
    }

    private static void Append(string s)
    {
        for (int i = 0; i < s.Length && _textLen < _text.Length; i++) _text[_textLen++] = s[i];
    }

    /// <summary>Append a non-negative value with <paramref name="decimals"/> fixed decimals, no allocation.</summary>
    private static void AppendNum(float v, int decimals)
    {
        if (v < 0f || v > 999999f) v = 0f;
        int scale = decimals == 0 ? 1 : 10;
        int whole = (int)v, frac = (int)((v - whole) * scale + 0.5f);
        if (frac >= scale) { whole++; frac = 0; }
        Span<char> d = stackalloc char[8];
        int nd = 0;
        do { d[nd++] = (char)('0' + whole % 10); whole /= 10; } while (whole > 0 && nd < 8);
        while (nd > 0 && _textLen < _text.Length) _text[_textLen++] = d[--nd];
        if (decimals > 0 && _textLen + 1 < _text.Length)
        {
            _text[_textLen++] = '.';
            _text[_textLen++] = (char)('0' + frac);
        }
    }

    private static void Quad(float x0, float y0, float x1, float y1,
                             float u0, float v0, float u1, float v1,
                             float r, float g, float b, float a)
    {
        if (_vertCount * VertexBatch.Floats + 48 > _verts.Length) return;
        V(x0, y0, u0, v0, r, g, b, a); V(x1, y0, u1, v0, r, g, b, a); V(x1, y1, u1, v1, r, g, b, a);
        V(x0, y0, u0, v0, r, g, b, a); V(x1, y1, u1, v1, r, g, b, a); V(x0, y1, u0, v1, r, g, b, a);
    }

    private static void V(float x, float y, float u, float v, float r, float g, float b, float a)
    {
        int i = _vertCount++ * VertexBatch.Floats;
        _verts[i] = x; _verts[i + 1] = y; _verts[i + 2] = u; _verts[i + 3] = v;
        _verts[i + 4] = r; _verts[i + 5] = g; _verts[i + 6] = b; _verts[i + 7] = a;
    }
}
