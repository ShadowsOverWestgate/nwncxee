using System.Collections.Generic;
using System.Text;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Chat.Render;

/// <summary>Row alignment for wrapped text: given one laid-out row, returns how far to shift it
/// (<paramref name="xOff"/>) and how much to pad each interior space (<paramref name="spaceExtra"/>).
/// The single home for left/right/center/capped-justify policy -- callers keep their own glyph-emit loop
/// and just consume the two offsets. Shared by the chat window (<see cref="WindowRenderer"/>) and the dialog
/// panel (<c>DialogService</c>); either service laying out text uses this instead of re-rolling justify math.</summary>
public static class RowAlign
{
    /// <summary>A justified space may grow to at most this multiple of its natural width. Lines that would need
    /// more are stretched only to the cap (ending slightly short of the margin) -- never into rivers.</summary>
    private const float JustifyMaxSpaceMul = 2.0f;

    /// <summary>Compute the x-offset and per-space extra width for a text alignment mode over one wrapped row.
    /// Justify applies only on a soft-wrap row (the consumed break char at <c>span.End</c> is a space) -- never
    /// the last row or a forced mid-word split -- and caps per-gap stretch so lines never form rivers.</summary>
    public static void Compute(TextAlign align, GlyphAtlas atlas, List<Rune> runes,
                               TextWrapper.LineSpan span, float availW, bool lastRow,
                               out float xOff, out float spaceExtra)
    {
        xOff = 0f; spaceExtra = 0f;
        if (align == TextAlign.Left) return;
        float lineW = Measure(atlas, runes, span.Start, span.End, 0f);
        float slack = availW - lineW;
        if (slack <= 0f) return;
        if (align == TextAlign.Right) { xOff = slack; return; }
        if (align == TextAlign.Center) { xOff = slack * 0.5f; return; }
        // Over-cap lines stretch only to the cap and end slightly short, rather than left-ragged.
        if (lastRow || span.End >= runes.Count || runes[span.End].Value != ' ') return;
        int spaces = 0;
        for (int k = span.Start; k < span.End; k++)
            if (runes[k].Value == ' ') spaces++;
        if (spaces == 0) return;
        float maxExtra = atlas.AdvanceOf(new Rune(' ')) * (JustifyMaxSpaceMul - 1f);
        spaceExtra = System.Math.Min(slack / spaces, maxExtra);
    }

    /// <summary>Advance of a rune range starting from <paramref name="startX"/> -- glyph advance where the atlas
    /// has it, fallback advance otherwise. The one measurement primitive; caret/selection mapping uses it too.</summary>
    public static float Measure(GlyphAtlas atlas, List<Rune> runes, int from, int to, float startX)
    {
        float x = startX;
        for (int k = from; k < to; k++) x += atlas.TryGet(runes[k].Value, out Glyph g) ? g.Advance : atlas.AdvanceOf(runes[k]);
        return x;
    }

    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"RowAlign.SelfCheck FAIL: {w}"); } }

        var ja = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        ja.SetTestGlyph(' ', 4f);
        foreach (char ch in "abcdef") ja.SetTestGlyph(ch, 6f);
        var ar = new List<Rune>();
        foreach (Rune ru in "ab cd ef".EnumerateRunes()) ar.Add(ru);   // "ab cd" | " ef": break at index 5 = space
        var sp = new TextWrapper.LineSpan(0, 5);                        // covers "ab cd", End points at the break space
        float lineW = 6 + 6 + 4 + 6 + 6;                               // = 28; one interior gap (index 2)
        float avail = 40f;                                             // slack 12, one gap -> want 12 but cap = 4 (2x space)
        Compute(TextAlign.Left, ja, ar, sp, avail, false, out float xL, out float seL);
        E(xL == 0f && seL == 0f, "Left: no offset");
        Compute(TextAlign.Right, ja, ar, sp, avail, false, out float xR, out float seR);
        E(System.Math.Abs(xR - (avail - lineW)) < 0.1f && seR == 0f, "Right: full slack offset");
        Compute(TextAlign.Center, ja, ar, sp, avail, false, out float xC, out float seC);
        E(System.Math.Abs(xC - (avail - lineW) * 0.5f) < 0.1f && seC == 0f, "Center: half slack offset");
        Compute(TextAlign.Justify, ja, ar, sp, avail, false, out float xJ, out float seJ);
        E(xJ == 0f && System.Math.Abs(seJ - 4f) < 0.1f, "Justify: per-gap stretch capped to ~2x space");
        Compute(TextAlign.Justify, ja, ar, sp, 30f, false, out float _xJ2, out float seJ2);
        E(System.Math.Abs(seJ2 - 2f) < 0.1f, "Justify: small slack spread fully (under cap)");
        Compute(TextAlign.Justify, ja, ar, sp, avail, true, out float xJL, out float seJL);
        E(xJL == 0f && seJL == 0f, "Justify last row: falls back to left");
        var spEnd = new TextWrapper.LineSpan(0, ar.Count);             // no trailing break space -> paragraph end
        Compute(TextAlign.Justify, ja, ar, spEnd, avail, false, out float _xJE, out float seJE);
        E(seJE == 0f, "Justify: non-soft-wrap row (no trailing space) not justified");

        log.Info(ok ? "RowAlign.SelfCheck: passed" : "RowAlign.SelfCheck: FAILED");
    }
}
