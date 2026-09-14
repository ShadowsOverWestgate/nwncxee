using System.Text;

namespace CX.Ui;

/// <summary>One-line string draw + measure over a <see cref="GlyphAtlas"/>, shared by the settings panel and the
/// primitives (scroll column, dropdown, tooltip). Zero-alloc: a rune walk over the string, no LINQ, no buffers --
/// safe on the render thread.</summary>
public static class UiText
{
    /// <summary>Draw one string clipped at <paramref name="clipX"/>, returning the pen x it ended at.</summary>
    public static float Draw(VertexBatch b, GlyphAtlas atlas, string s, float startX, float baseline,
                             uint rgb, float clipX, float alpha = 1f)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, bl = (rgb & 0xFF) / 255f;
        float penX = startX;
        foreach (Rune ru in s.EnumerateRunes())
        {
            if (penX >= clipX) break;
            if (atlas.TryGet(ru.Value, out Glyph gl)) { b.Glyph(gl, penX, baseline, r, g, bl, alpha); penX += gl.Advance; }
            else penX += atlas.AdvanceOf(ru);
        }
        return penX;
    }

    /// <summary>Advance width of a string (no draw).</summary>
    public static float Measure(GlyphAtlas atlas, string s)
    {
        float w = 0f;
        foreach (Rune ru in s.EnumerateRunes()) w += atlas.AdvanceOf(ru);
        return w;
    }
}
