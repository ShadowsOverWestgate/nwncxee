using System;
using System.Collections.Generic;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Ui;

/// <summary>The framework text layer's opt-in select-and-copy capability: it owns the selection state (anchor and
/// cursor rune indices), the per-rune edge geometry both glyph-emit and selection measure against, the point-to-rune
/// hit-test and the clipboard copy. Content keeps its own per-line loop -- wrap and <c>RowAlign</c> stay with it, so
/// the framework takes no service dependency -- and feeds each row's <see cref="RowEdges"/> back in. No hot alloc.</summary>
public sealed unsafe class SelectableText
{
    /// <summary>Rune indices into the currently laid-out block; -1 = no selection. Anchor is where the drag began,
    /// Cursor where it is now. Content mutates these cold (on the input thread) and reads them cold in its relayout.</summary>
    public int Anchor = -1, Cursor = -1;

    /// <summary>A real (non-empty) selection exists: both ends set and distinct. A bare click (anchor==cursor) is not.</summary>
    public bool Active => Anchor >= 0 && Cursor >= 0 && Anchor != Cursor;

    /// <summary>Start a selection at a rune index (a press): anchor and cursor collapse there. A following motion
    /// extends the cursor; a release with no motion leaves it collapsed, and <see cref="Active"/> reads false.</summary>
    public void Begin(int index) => Anchor = Cursor = index;

    public void Clear() => Anchor = Cursor = -1;

    /// <summary>The normalized selected rune range [a,b) clamped to <paramref name="runeCount"/>, or false when the
    /// selection is empty. Callers copy / highlight <c>runes[a..b)</c>.</summary>
    public bool Range(int runeCount, out int a, out int b)
    {
        if (!Active) { a = b = 0; return false; }
        a = Math.Clamp(Math.Min(Anchor, Cursor), 0, runeCount);
        b = Math.Clamp(Math.Max(Anchor, Cursor), 0, runeCount);
        return b > a;
    }

    /// <summary>Per-rune left-edge x positions for one wrapped row [<paramref name="start"/>,<paramref name="end"/>),
    /// written into <paramref name="edges"/> (cleared first): edges[i] is the left edge of rune <c>start + i</c>, the
    /// final entry the row's right edge. The single source of truth for glyph x -- content draws glyphs here and the
    /// selection measures against them, so it never drifts (justify too: each space carries <paramref name="extraPerGap"/>).</summary>
    public static void RowEdges(GlyphAtlas atlas, IReadOnlyList<Rune> runes, int start, int end, float x0,
                                float extraPerGap, List<float> edges)
    {
        edges.Clear();
        float penX = x0;
        for (int k = start; k < end; k++)
        {
            edges.Add(penX);
            Rune ru = runes[k];
            penX += atlas.TryGet(ru.Value, out Glyph g) ? g.Advance : atlas.AdvanceOf(ru);
            if (ru.Value == ' ') penX += extraPerGap;
        }
        edges.Add(penX);
    }

    /// <summary>Rune index within one row for a target x, by nearest glyph gap against the row's per-rune
    /// <paramref name="edges"/>. Returns <c>start + i</c> for the nearest gap, or <c>start + count</c> past the row.</summary>
    public static int RowHitIndex(List<float> edges, int start, int count, float x)
    {
        for (int i = 0; i < count; i++)
            if (x < (edges[i] + edges[i + 1]) * 0.5f) return start + i;
        return start + count;
    }

    /// <summary>Copy plain text to the system clipboard (SDL_SetClipboardText). No-op (false) for empty text or an
    /// unresolved symbol (run-demo / non-Linux). Callers pass already color-stripped prose.</summary>
    public static bool Copy(string text)
    {
        if (EngineFn.SdlSetClipboardText == null || text.Length == 0) return false;
        byte[] u = Encoding.UTF8.GetBytes(text);
        Span<byte> buf = stackalloc byte[u.Length + 1];
        u.CopyTo(buf);
        buf[u.Length] = 0;
        fixed (byte* b = buf) EngineFn.SdlSetClipboardText(b);
        return true;
    }

    /// <summary>Off-device proof: the edge geometry (cumulative advances + justify), the point-to-rune
    /// hit-test, and the selection-range normalize/clamp/clear. No GL, no device.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var ta = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        ta.SetTestGlyph(' ', 4f);
        foreach (char ch in "abcdef") ta.SetTestGlyph(ch, 6f);
        var tr = new List<Rune>();
        foreach (Rune ru in "ab cd ef".EnumerateRunes()) tr.Add(ru);
        int n = tr.Count;

        var edges = new List<float>();
        RowEdges(ta, tr, 0, n, 0f, 0f, edges);   // a6 b6 sp4 c6 d6 sp4 e6 f6 -> edges 0,6,12,16,22,28,32,38,44
        check.Expect(edges.Count == n + 1 && edges[0] == 0f && Math.Abs(edges[^1] - 44f) < 0.1f,
          "RowEdges: cumulative advances incl. trailing right edge");
        check.Expect(RowHitIndex(edges, 0, n, 2f) == 0, "hit: left of the first glyph -> index 0");
        check.Expect(RowHitIndex(edges, 0, n, 7f) == 1, "hit: past the first glyph's midpoint -> index 1");
        check.Expect(RowHitIndex(edges, 0, n, 100f) == n, "hit: past the row -> end index");

        var edgesJ = new List<float>();
        RowEdges(ta, tr, 0, n, 0f, 3f, edgesJ);   // justify: +3px per interior space widens the row
        check.Expect(edgesJ[^1] > edges[^1] && edgesJ[1] == edges[1], "RowEdges: justify pushes later edges right, not the first");

        var sel = new SelectableText();
        check.Expect(!sel.Active && !sel.Range(n, out _, out _), "fresh selection is empty");
        sel.Begin(5);
        check.Expect(!sel.Active, "a collapsed selection (anchor==cursor) is inactive");
        sel.Cursor = 1;
        check.Expect(sel.Active && sel.Range(n, out int ra, out int rb) && ra == 1 && rb == 5, "range normalizes min/max");
        sel.Cursor = 999;
        check.Expect(sel.Range(n, out _, out int rc) && rc == n, "range clamps the cursor to the rune count");
        sel.Clear();
        check.Expect(!sel.Active, "clear empties the selection");

        log.Info(check.Ok ? "SelectableText.SelfCheck: passed" : "SelectableText.SelfCheck: FAILED");
    }
}
