using System;
using System.Diagnostics;
using System.Text;
using CX.Framework;

namespace CX.Ui;

/// <summary>
/// The help text behind a faint <c>(?)</c> marker (#587). The owner draws the marker beside a label with
/// <see cref="DrawMarker"/>, tells the tooltip when the pointer rests on one (<see cref="Hover"/>) and when it
/// leaves; after <see cref="DelaySec"/> the text shows in a box under the marker, word-wrapped to
/// <see cref="MaxW"/>. It has no input of its own -- it never consumes a press, never takes focus -- so hovering
/// changes nothing but pixels. Cold-built owners poll <see cref="Tick"/> once a frame to learn the delay elapsed
/// and rebuild; per-frame owners just call <see cref="Draw"/>. Zero-alloc in draw (a rune walk wraps in place).
/// </summary>
public sealed class Tooltip
{
    /// <summary>Hover dwell before the text shows.</summary>
    public const float DelaySec = 0.45f;
    /// <summary>The marker text drawn beside a label.</summary>
    public const string Marker = "(?)";
    /// <summary>Marker alpha: faint, so it reads as an affordance, not a label.</summary>
    public const float MarkerAlpha = 0.45f;
    /// <summary>Widest text box before wrapping.</summary>
    public static float MaxW => 280f * Metrics.Scale;
    public static float Pad => 6f * Metrics.Scale;
    private const uint FrameRgb = 0x8C8C8C;

    /// <summary>Time source; the self-check substitutes a fake clock. Stopwatch ticks.</summary>
    internal static Func<long> Clock = Stopwatch.GetTimestamp;

    private object? _key;
    private string _text = "";
    private float _x, _top, _bottom;
    private long _since;
    private bool _shownAtTick;

    /// <summary>What the pointer rests on, or null.</summary>
    public object? Key => _key;
    public string Text => _text;
    public bool Hovering => _key != null;
    /// <summary>True once the dwell has elapsed on the current key.</summary>
    public bool Visible => _key != null && Clock() - _since >= (long)(DelaySec * Stopwatch.Frequency);

    /// <summary>The pointer rests on <paramref name="key"/>'s marker: left edge <paramref name="x"/>, its row
    /// band <paramref name="top"/>..<paramref name="bottom"/> (the box hangs under the band, or sits above it
    /// when it would run past the draw's clamp). A new key restarts the dwell; the same key just tracks the
    /// anchor. Returns true when the key changed.</summary>
    public bool Hover(object key, string text, float x, float top, float bottom)
    {
        _x = x; _top = top; _bottom = bottom;
        if (ReferenceEquals(key, _key)) return false;
        _key = key; _text = text; _since = Clock(); _shownAtTick = false;
        return true;
    }

    /// <summary>The pointer left the marker (or pressed). Returns true when something was being hovered.</summary>
    public bool Leave()
    {
        bool was = _key != null;
        _key = null; _text = ""; _shownAtTick = false;
        return was;
    }

    /// <summary>Per-frame poll for owners that rebuild on change: true exactly once, on the frame the dwell
    /// elapses and the text becomes due. Zero-alloc.</summary>
    public bool Tick()
    {
        if (_shownAtTick || !Visible) return false;
        _shownAtTick = true;
        return true;
    }

    /// <summary>Advance width of the marker in this face.</summary>
    public static float MarkerW(GlyphAtlas atlas) => UiText.Measure(atlas, Marker);

    /// <summary>Draw the faint marker with its left edge at <paramref name="x"/>.</summary>
    public static void DrawMarker(VertexBatch b, GlyphAtlas atlas, float x, float baseline, uint rgb)
        => UiText.Draw(b, atlas, Marker, x, baseline, rgb, float.MaxValue, MarkerAlpha);

    /// <summary>Draw the text box under the anchor when <see cref="Visible"/>: word-wrapped to <see cref="MaxW"/>,
    /// kept left of <paramref name="clampRight"/>, flipped above the row when it would pass
    /// <paramref name="clampBottom"/>. <paramref name="baseDrop"/> is the baseline offset from a line's top.</summary>
    public void Draw(VertexBatch b, GlyphAtlas atlas, float baseDrop,
                     (float r, float g, float b, float a) bg, uint textRgb,
                     float clampRight = float.MaxValue, float clampBottom = float.MaxValue)
    {
        if (!Visible) return;
        float maxW = MaxW, pad = Pad, lh = atlas.LineHeight;
        int lines = 0; float widest = 0f;
        Wrap(atlas, _text, maxW, null, 0f, 0f, 0f, 0, ref lines, ref widest);
        if (lines == 0) return;
        float w = widest + 2f * pad, h = lines * lh + 2f * pad;
        float x0 = _x, y0 = _bottom + 2f;
        if (y0 + h > clampBottom && _top - 2f - h >= 0f) y0 = _top - 2f - h;
        if (x0 + w > clampRight) x0 = clampRight - w;
        if (x0 < 0f) x0 = 0f;
        float fr = ((FrameRgb >> 16) & 0xFF) / 255f, fg = ((FrameRgb >> 8) & 0xFF) / 255f, fb = (FrameRgb & 0xFF) / 255f;
        b.Rect(x0 - 1f, y0 - 1f, x0 + w + 1f, y0 + h + 1f, fr, fg, fb, 1f);
        b.Rect(x0, y0, x0 + w, y0 + h, bg.r, bg.g, bg.b, 0.98f);
        int drawn = 0; float _ = 0f;
        Wrap(atlas, _text, maxW, b, x0 + pad, y0 + pad + baseDrop, lh, textRgb, ref drawn, ref _);
    }

    /// <summary>Greedy word wrap: one pass counts lines and the widest line (no batch), the same pass with a
    /// batch draws them. Breaks at spaces; a word wider than the box is cut at the box.</summary>
    private static void Wrap(GlyphAtlas atlas, string s, float maxW, VertexBatch? b, float x, float baseline, float lh,
                             uint rgb, ref int lines, ref float widest)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, bl = (rgb & 0xFF) / 255f;
        float lineW = 0f, wordW = 0f;
        int lineStart = 0, wordStart = 0, i = 0;
        // Walk runes; a line is [lineStart, i) in char indices, committed when the next word would overflow.
        foreach (Rune ru in s.EnumerateRunes())
        {
            int len = ru.Utf16SequenceLength;
            float adv = atlas.AdvanceOf(ru);
            if (ru.Value == ' ')
            {
                lineW += wordW + adv; wordW = 0f; wordStart = i + len;
            }
            else
            {
                if (lineW + wordW + adv > maxW && lineW > 0f)
                {
                    Emit(atlas, s, lineStart, wordStart, b, x, baseline + lines * lh, r, g, bl, ref widest, lineW);
                    lines++;
                    lineStart = wordStart; lineW = 0f;
                }
                else if (lineW + wordW + adv > maxW)
                {
                    Emit(atlas, s, lineStart, i, b, x, baseline + lines * lh, r, g, bl, ref widest, wordW);
                    lines++;
                    lineStart = i; wordStart = i; wordW = 0f;
                }
                wordW += adv;
            }
            i += len;
        }
        if (i > lineStart)
        {
            Emit(atlas, s, lineStart, i, b, x, baseline + lines * lh, r, g, bl, ref widest, lineW + wordW);
            lines++;
        }
    }

    private static void Emit(GlyphAtlas atlas, string s, int from, int to, VertexBatch? b, float x, float baseline,
                             float r, float g, float bl, ref float widest, float w)
    {
        while (to > from && s[to - 1] == ' ') { w -= atlas.SpaceAdvance; to--; }
        if (w > widest) widest = w;
        if (b == null) return;
        float pen = x;
        foreach (Rune ru in s.AsSpan(from, to - from).EnumerateRunes())
        {
            if (atlas.TryGet(ru.Value, out Glyph gl)) { b.Glyph(gl, pen, baseline, r, g, bl, 1f); pen += gl.Advance; }
            else pen += atlas.AdvanceOf(ru);
        }
    }

    /// <summary>Off-device proof: the dwell, the key change, Leave, the one-shot Tick, and a zero-alloc draw
    /// that wraps a long help text into several lines.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        long now = 0;
        Func<long> real = Clock;
        Clock = () => now;
        long delay = (long)(DelaySec * Stopwatch.Frequency);
        try
        {
            var t = new Tooltip();
            object a = new object(), bKey = new object();
            check.Expect(!t.Hovering && !t.Visible && !t.Tick(), "idle: nothing hovered, nothing due");
            check.Expect(t.Hover(a, "Help A", 10f, 20f, 40f) && t.Hovering && !t.Visible, "hover starts the dwell, not yet visible");
            check.Expect(!t.Hover(a, "Help A", 12f, 20f, 40f), "same key: no change reported");
            now = delay - 1;
            check.Expect(!t.Visible && !t.Tick(), "just under the dwell: still hidden");
            now = delay;
            check.Expect(t.Visible && t.Tick() && !t.Tick(), "at the dwell: visible, Tick fires exactly once");
            check.Expect(t.Hover(bKey, "Help B", 0f, 0f, 20f) && !t.Visible, "a new key restarts the dwell");
            now += delay;
            check.Expect(t.Visible && t.Text == "Help B", "the new key's text shows after its own dwell");
            check.Expect(t.Leave() && !t.Hovering && !t.Visible && !t.Leave(), "Leave clears once");

            // Wrap + zero-alloc draw.
            var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15, SpaceAdvance = 4f };
            atlas.SetTestGlyph(' ', 4f);
            for (char ch = 'a'; ch <= 'z'; ch++) atlas.SetTestGlyph(ch, 10f);
            var vb = new VertexBatch(atlas.SolidU, atlas.SolidV);
            string help = "the quick brown fox jumps over the lazy dog again and again until the box wraps it";
            t.Hover(a, help, 100f, 100f, 120f); now += delay;
            check.Expect(t.Visible, "visible for the draw");
            int lines = 0; float widest = 0f;
            Wrap(atlas, help, MaxW, null, 0f, 0f, 0f, 0, ref lines, ref widest);
            check.Expect(lines >= 3 && widest <= MaxW, $"the help wraps into several lines within MaxW (lines={lines}, widest={widest})");
            t.Draw(vb, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), 0xFFFFFF, 2000f);
            check.Expect(vb.VertexCount >= 12, "the draw emitted the box");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { vb.Clear(); t.Draw(vb, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), 0xFFFFFF, 2000f); }
            long deltaB = GC.GetAllocatedBytesForCurrentThread() - before;
            check.Expect(deltaB == 0, $"100 tooltip draws allocate nothing ({deltaB} bytes)");
            // Clamp: a box anchored at the right edge slides left to stay on-screen; one at the bottom flips above its row.
            t.Hover(bKey, "abc", 1990f, 10f, 30f); now += delay;
            vb.Clear(); t.Draw(vb, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), 0xFFFFFF, 2000f);
            float[] v = vb.ToArray();
            check.Expect(vb.VertexCount > 0 && v[0] >= 0f && v[1 * VertexBatch.Floats] <= 2000f + 1.01f, "clamped draw stays left of the right edge");
            t.Hover(a, "abc", 100f, 980f, 1000f); now += delay;
            vb.Clear(); t.Draw(vb, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), 0xFFFFFF, 2000f, 1000f);
            v = vb.ToArray();
            check.Expect(vb.VertexCount > 0 && v[1] < 980f, "a box past the bottom clamp flips above its row");
            t.Leave();
        }
        finally { Clock = real; }

        log.Info(check.Ok ? "Tooltip.SelfCheck: passed" : "Tooltip.SelfCheck: FAILED");
    }
}
