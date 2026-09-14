using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Nameplate;

/// <summary>One plate's laid-out boxes, in the same screen-px space the caller draws in. Sizes come out of
/// <see cref="PlateLayout.Measure"/>: the glyph metrics handed in already carry <see cref="Metrics.Scale"/>
/// (the atlas is baked at the effective px), so only the chrome constants are scaled -- do NOT scale these
/// again. All coordinates are relative to the plate's top-left.</summary>
public readonly struct PlateMetrics
{
    public readonly float W, H, PortraitX, PortraitY, PortraitSize, NameX, NameY, BandX, BandY, BarX, BarY, BarW, BarH;
    /// <summary>Width of the text column (the widest of name, band and any override line). The span a centred
    /// line is centred in -- never the whole box, which would slide the text under the portrait.</summary>
    public readonly float TextW;
    /// <summary>Top of the override lines that sit ABOVE the name (prepend mode), and of the ones BELOW the
    /// band and bar (append mode). Both are the text column's x (<see cref="NameX"/>).</summary>
    public readonly float PreY, PostY;

    public PlateMetrics(float w, float h, float portraitX, float portraitY, float portraitSize,
                        float nameX, float nameY, float bandX, float bandY,
                        float barX, float barY, float barW, float barH, float textW,
                        float preY = 0f, float postY = 0f)
    {
        W = w; H = h; PortraitX = portraitX; PortraitY = portraitY; PortraitSize = portraitSize;
        NameX = nameX; NameY = nameY; BandX = bandX; BandY = bandY;
        BarX = barX; BarY = barY; BarW = barW; BarH = barH; TextW = textW;
        PreY = preY; PostY = postY;
    }
}

/// <summary>Which existing plate line an <c>ObjectTextBubbleOverride</c> segment continues:
/// <c>First</c> means it runs in FRONT of that line (prepend), <c>Last</c> means it runs off its end
/// (append). <c>None</c> = the plate has no such line and every segment stands on its own.</summary>
public enum PlateSplice { None, NameFirst, NameLast, BandFirst, BandLast }

/// <summary>Pure layout + fade math for the overhead plate and bubble. No GL, no native reads, and no
/// allocation on the frame: every method here but one is called per entry per frame from the hot draw, and the
/// string/colour tables are static. The exception is <see cref="BreakName"/>, which runs COLD (once per name
/// resolve, on the hook thread) and is documented as such.</summary>
public static class PlateLayout
{
    /// <summary>Inner padding around the plate content, design px.</summary>
    private const float PadPx = 4f;
    /// <summary>Gap above the health bar. Design px.</summary>
    private const float GapPx = 4f;
    /// <summary>Gap between the portrait and the text column, design px. Wider than <see cref="GapPx"/>: a
    /// portrait is a picture butting against a centred text column, and 4px reads as the two touching.</summary>
    private const float PortraitGapPx = 8f;
    /// <summary>Health-bar thickness, design px.</summary>
    private const float BarHPx = 3f;
    /// <summary>NWN portrait aspect (w:h). Mirrors <see cref="TextRig.PortraitAspect"/>; duplicated as a local
    /// const so this file stays pure math and self-checks with no UI state.</summary>
    private const float PortraitAspect = 0.64f;

    /// <summary>Number of health bands (<c>CNWCCreatureStats + 0x8e</c> is 0..5).</summary>
    public const int BandCount = 6;

    private static readonly string[] BandTexts =
        { "Uninjured", "Barely Injured", "Injured", "Badly Wounded", "Near Death", "Dead" };

    /// <summary>Band colours as flat 0..255 (r,g,b) triplets in band order; normalised by
    /// <see cref="BandColor"/>.</summary>
    private static readonly float[] BandRgb =
    {
        128f, 255f, 128f,
        200f, 255f,   1f,
        255f, 200f,   1f,
        255f, 100f,   1f,
        255f,  80f,  64f,
        128f, 128f, 128f,
    };

    /// <summary>How far <c>|guiScale - 1|</c> must stray before <c>Camera::MapToScreen</c> shrinks its viewport
    /// by the GUI scale: the double at <c>.rodata 0x12eeed0</c> (<c>67666676 3d0ab73f</c> = 0.09), compared
    /// <c>ucomisd; ja &lt;skip&gt;</c> at @0x4a3e14. Inside the band the engine leaves the rect alone, so
    /// un-shrinking there would introduce an offset that was never applied.</summary>
    private const float GuiScaleEpsilon = 0.09f;

    /// <summary>
    /// Turn the engine's stored bubble anchor (<see cref="NameplateNative.AnchorRaw"/>) into the pixels a
    /// <c>Window</c> draws in: top-left origin, y down, extents <paramref name="drawW"/>×<paramref name="drawH"/>.
    /// Three terms, each one a line of <c>Camera::MapToScreen</c>:
    /// <list type="number">
    /// <item>the GUI-scale shrink (@0x4a3e1a) centred the projection in a rect <paramref name="guiScale"/>
    /// times smaller, leaving the rect's centre fixed -- undone by scaling back out about that centre, and
    /// only outside the engine's own <see cref="GuiScaleEpsilon"/> dead band;</item>
    /// <item>the value is relative to the camera's viewport origin and in its extents, so it is rebased and
    /// rescaled onto the draw viewport (the engine's render viewport need not be the GL one -- same aspect,
    /// possibly upscaled);</item>
    /// <item>y is measured UP from the bottom (the GL window transform), so it is flipped last.</item>
    /// </list>
    /// A non-positive camera extent means "unknown" and that axis passes through, y still flipped about
    /// <paramref name="drawH"/>. Pure -- self-checked.
    /// </summary>
    public static void ToDrawSpace(float ex, float ey, float vpX, float vpY, float vpW, float vpH,
                                   float guiScale, float drawW, float drawH, out float x, out float y)
    {
        if (guiScale > 0f && (guiScale > 1f + GuiScaleEpsilon || guiScale < 1f - GuiScaleEpsilon)
            && vpW > 0f && vpH > 0f)
        {
            float cx = vpX + vpW * 0.5f, cy = vpY + vpH * 0.5f;
            ex = cx + (ex - cx) * guiScale;
            ey = cy + (ey - cy) * guiScale;
        }

        if (vpW > 0f && drawW > 0f) x = (ex - vpX) * (drawW / vpW);
        else x = ex;

        if (vpH > 0f && drawH > 0f) y = (vpY + vpH - ey) * (drawH / vpH);
        else y = drawH > 0f ? drawH - ey : -ey;
    }

    /// <summary>Linear step for a fade that takes <paramref name="animMs"/> to cross 0..1, from a monotonic
    /// delta. A <paramref name="dtMs"/> of zero or less -- two draws inside one tick, or a clock that ran
    /// backwards -- is no step at all; an interval of a millisecond or less snaps.</summary>
    public static float FadeStep(long dtMs, float animMs)
    {
        if (dtMs <= 0) return 0f;
        if (!(animMs > 1f)) return 1f;      // NaN-safe: an unparseable knob snaps rather than freezing
        float s = dtMs / animMs;
        return s > 1f ? 1f : s;
    }

    /// <summary>Walk <paramref name="v"/> at most <paramref name="step"/> toward <paramref name="target"/>,
    /// never past it. The Dialog panel's shape (<c>DialogService.MoveToward</c>), reused verbatim.</summary>
    public static void MoveToward(ref float v, float target, float step)
    {
        if (v < target) { v += step; if (v > target) v = target; }
        else if (v > target) { v -= step; if (v < target) v = target; }
    }

    /// <summary>Ease a pixel offset toward zero: the exponential residual the dialog body uses,
    /// <c>tau = max(20, animMs * 0.4)</c>, with a half-pixel snap so it lands instead of asymptoting. A
    /// non-positive <paramref name="dtMs"/> leaves it exactly where it was.</summary>
    public static void SettleOffset(ref float v, long dtMs, float animMs)
    {
        if (v == 0f) return;
        if (dtMs <= 0) return;
        if (!(animMs > 0f)) { v = 0f; return; }   // "no animation" means no slide either, not a 20ms one
        float tau = animMs * 0.4f;
        if (!(tau > MinTauMs)) tau = MinTauMs;
        v -= v * (1f - System.MathF.Exp(-dtMs / tau));
        if (v < SnapPx && v > -SnapPx) v = 0f;
    }

    /// <summary>Floor on the ease time constant, ms: below this a slide is a jump at any sane frame rate.</summary>
    private const float MinTauMs = 20f;
    /// <summary>Offset residual under which a slide is finished, px.</summary>
    private const float SnapPx = 0.5f;

    /// <summary>Top-left the caller should draw a (<paramref name="w"/>×<paramref name="h"/>) plate at, given the
    /// engine's projected screen anchor: centred horizontally, sitting directly above the anchor. Clamped into
    /// the live viewport (<see cref="TextRig.ViewportW"/>/<see cref="TextRig.ViewportH"/>).</summary>
    public static void Place(float anchorX, float anchorY, float w, float h, out float x, out float y) =>
        Place(anchorX, anchorY, w, h, TextRig.ViewportW, TextRig.ViewportH, out x, out y);

    /// <summary>As <see cref="Place(float,float,float,float,out float,out float)"/>, with the viewport passed
    /// explicitly (the self-check's door, and the caller's when it already has the size). A zero or negative
    /// viewport extent means "unknown" and that axis is left unclamped.</summary>
    public static void Place(float anchorX, float anchorY, float w, float h, float vpW, float vpH,
                             out float x, out float y)
    {
        x = anchorX - w * 0.5f;
        y = anchorY - h;
        if (vpW > 0f)
        {
            float maxX = vpW - w;
            if (x > maxX) x = maxX;
            if (x < 0f) x = 0f;
        }
        if (vpH > 0f)
        {
            float maxY = vpH - h;
            if (y > maxY) y = maxY;
            if (y < 0f) y = 0f;
        }
    }

    /// <summary>Lay out portrait, name, health band and optional health bar into one plate box, with a
    /// single-line name. Kept as the one-line door so every caller that has no line count reads unchanged.</summary>
    public static PlateMetrics Measure(float nameW, float bandW, bool portrait, bool band, bool bar,
                                       float lineH, float scale) =>
        Measure(nameW, 1, bandW, portrait, band, bar, lineH, scale);

    /// <summary>As above for a name of <paramref name="nameLines"/> lines (<see cref="SplitLines"/>);
    /// <paramref name="nameW"/> is then the WIDEST line. <paramref name="bandW"/> and
    /// <paramref name="lineH"/> are measured glyph metrics and are used as given; <paramref name="scale"/>
    /// scales only the chrome constants.
    /// <para>The portrait spans <c>min(nameLines, 2)</c> name lines plus whatever the band and bar need,
    /// keeping its <see cref="PortraitAspect"/> width. The name-line term is what is capped, at
    /// <see cref="PortraitMaxLines"/>: a header-plus-paragraph sign left uncapped stretched the thumbnail into
    /// a sliver down the left edge, while capping the WHOLE column instead would have shrunk every creature
    /// portrait (a creature's one line + band + bar is already taller than two lines). So a one-line name is
    /// byte-identical to the old rule for creature and placeable alike, a second line grows the portrait, and
    /// a third does not. The portrait is VERTICALLY CENTRED against the whole content column
    /// (<see cref="PortraitTop"/>); at or below the cap the two heights are equal and it sits at the pad, so the
    /// common plate is unchanged.</para></summary>
    public static PlateMetrics Measure(float nameW, int nameLines, float bandW, bool portrait, bool band,
                                       bool bar, float lineH, float scale) =>
        Measure(nameW, nameLines, bandW, portrait, band, bar, lineH, scale, 0, 0, 0f);

    /// <summary>As above with the <c>ObjectTextBubbleOverride</c> bands (docs/re/nameplate.md §8.5) folded in:
    /// <paramref name="preLines"/> override lines above the name, <paramref name="postLines"/> below the band
    /// and bar, and <paramref name="extraW"/> the widest of every override line PLUS the spliced line the
    /// override extends (the caller adds the band or name width to the splice itself).
    /// <para>With all three at their zero the result is byte-identical to the no-override plate, which is why
    /// the shorter overload just delegates. <paramref name="nameLines"/> may be 0 only when there ARE override
    /// lines -- that is replace mode, where the server owns the whole string; otherwise an empty name still
    /// occupies its line as before. The portrait spans the name lines it always did, and sits at the vertical
    /// centre of the WHOLE composed column -- pre-lines, name, band, bar and post-lines alike.</para></summary>
    public static PlateMetrics Measure(float nameW, int nameLines, float bandW, bool portrait, bool band,
                                       bool bar, float lineH, float scale,
                                       int preLines, int postLines, float extraW)
    {
        float pad = PadPx * scale, gap = GapPx * scale, barH = BarHPx * scale, pgap = PortraitGapPx * scale;
        if (preLines < 0) preLines = 0;
        if (postLines < 0) postLines = 0;
        if (nameLines < 1 && preLines + postLines == 0) nameLines = 1;
        if (nameLines < 0) nameLines = 0;

        float textW = nameW;
        if (band && bandW > textW) textW = bandW;
        if (extraW > textW) textW = extraW;
        if (textW < 0f) textW = 0f;

        float preH = lineH * preLines, postH = lineH * postLines;
        float nameH = lineH * nameLines;
        float below = (band ? lineH : 0f) + (bar ? gap + barH : 0f);
        float contentH = preH + nameH + below + postH;

        // The portrait tracks the NAME lines; in replace mode there are none, so the override lines stand in.
        int spanLines = nameLines > 0 ? nameLines : preLines;
        if (spanLines < 1) spanLines = 1;
        int portraitLines = spanLines < PortraitMaxLines ? spanLines : PortraitMaxLines;
        float portraitSize = portrait ? portraitLines * lineH + below : 0f;
        float portraitW = portraitSize * PortraitAspect;
        float colX = pad + (portrait ? portraitW + pgap : 0f);

        float w = colX + textW + pad;
        float h = contentH + pad * 2f;

        float nameY = pad + preH;
        float bandY = nameY + nameH;
        float barY = bandY + (band ? lineH : 0f) + gap;
        float postY = nameY + nameH + below;

        return new PlateMetrics(w, h,
            pad, PortraitTop(pad, contentH, portraitSize), portraitSize,
            colX, nameY,
            colX, bandY,
            colX, barY, bar ? textW : 0f, bar ? barH : 0f, textW,
            pad, postY);
    }

    /// <summary>NAME lines a portrait may grow with (the band and bar are always added on top). Past this a
    /// longer name grows the box downward and the portrait, capped, centres against the taller column.</summary>
    public const int PortraitMaxLines = 2;

    /// <summary>Top edge of the portrait: vertically centred against the content column, never above the pad.
    /// The clamp is defensive -- the size rule (<see cref="PortraitMaxLines"/> name lines plus the same
    /// band/bar term the content carries) cannot exceed <paramref name="contentH"/> today, and where the two
    /// are equal this is exactly <paramref name="pad"/>, which is what keeps the one- and two-line plate
    /// byte-identical to the top-aligned rule it replaces.</summary>
    public static float PortraitTop(float pad, float contentH, float portraitSize)
    {
        float slack = contentH - portraitSize;
        return slack > 0f ? pad + slack * 0.5f : pad;
    }

    /// <summary>Left edge of one text line inside a column of <paramref name="textW"/> starting at
    /// <paramref name="colX"/>: centred in the column, as vanilla centres every plate line. The column, never
    /// the whole box -- centring in the box would slide the text under the portrait. A line wider than the
    /// column pins to the start rather than hanging off the left.</summary>
    public static float LineX(float colX, float textW, float lineW)
    {
        float slack = textW - lineW;
        return slack > 0f ? colX + slack * 0.5f : colX;
    }

    /// <summary>
    /// Normalise every line break a builder can express in a name down to one LF, so the plate can lay it out
    /// as the several lines it was meant to be. Four producers, one product:
    /// <list type="bullet">
    /// <item>a real LF, and a CR or CRLF, each become one break;</item>
    /// <item>the two-character sequence <c>\n</c> becomes one break -- a toolset name field cannot carry a raw
    /// LF, so this is what a builder actually types. The cost is that a name legitimately containing a
    /// backslash followed by an <c>n</c> breaks there instead; judged acceptable;</item>
    /// <item>a run of THREE or more spaces becomes one break -- the padding trick builders already use to fake
    /// a wrap against the vanilla bubble's box. One or two spaces stay literal;</item>
    /// <item>spaces adjacent to a break are dropped, so no line starts or ends indented.</item>
    /// </list>
    /// Two deliberate consequences past the rule: consecutive breaks collapse to ONE (no blank lines), and
    /// leading/trailing whitespace is trimmed outright. COLD -- once per name resolve, never per frame -- and
    /// a name with nothing to rewrite is returned unchanged, allocating nothing.
    /// <para>ponytail: the plate name only. Bubbles wrap on their own width already
    /// (<c>PlateTable.EnsureWrap</c>) and would double-break; the upgrade path is to run this over the bubble
    /// runes before the wrap, once someone wants a builder-authored break there too.</para>
    /// </summary>
    public static string BreakName(string s) => BreakName(s, null, out _);

    /// <summary>As <see cref="BreakName(string)"/>, carrying a per-char colour array through the same rewrite
    /// so the two can never drift out of alignment. <paramref name="colours"/> is indexed by char of
    /// <paramref name="s"/>; the returned array is indexed by char of the returned string. A whitespace char
    /// the rewrite INSERTS takes the colour of the char that follows it, which is invisible but keeps the
    /// index arithmetic honest.</summary>
    public static string BreakName(string s, uint[]? colours, out uint[]? outColours)
    {
        outColours = colours;
        if (!NeedsBreak(s)) return s;

        var sb = new System.Text.StringBuilder(s.Length);
        var cols = colours == null ? null : new System.Collections.Generic.List<uint>(s.Length);
        int spaces = 0;
        bool brk = false, any = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                brk = true; spaces = 0; continue;
            }
            if (c == '\\' && i + 1 < s.Length && s[i + 1] == 'n')
            {
                i++; brk = true; spaces = 0; continue;
            }
            if (c == ' ') { spaces++; continue; }

            uint col = cols == null ? 0u : i < colours!.Length ? colours[i] : Plain;
            if (any)
            {
                if (brk || spaces >= SpacesPerBreak) { sb.Append('\n'); cols?.Add(col); }
                else for (int k = 0; k < spaces; k++) { sb.Append(' '); cols?.Add(col); }
            }
            spaces = 0; brk = false;
            sb.Append(c); cols?.Add(col); any = true;
        }
        outColours = cols?.ToArray();
        return sb.ToString();
    }

    /// <summary>The packed RGB that means "no colour tag covered this char" -- white, i.e. take whatever
    /// colour the caller was going to use anyway.
    /// <para>An explicit <c>&lt;cÿÿÿ&gt;</c> therefore reads as untagged and a hostile creature whose name a
    /// builder deliberately whitened still draws in the red reaction tint. A sentinel outside the 24-bit range
    /// does NOT fix that: <see cref="ColorRunParser"/> hands back the default colour and an explicit white as
    /// the same <c>(1,1,1)</c> run, so the distinction is already gone before this sees it. The chat
    /// renderer's own <c>EmitRuns</c> collides identically, and inventing a second convention here would only
    /// make the two disagree. Fixing it properly means teaching the parser to report tag DEPTH.</para></summary>
    public const uint Plain = 0xffffffu;

    /// <summary>
    /// Strip the Aurora inline colour grammar out of a name and hand back the colour each surviving char was
    /// under, packed 0xRRGGBB. Null out-array means "every char was plain" -- the overwhelmingly common name,
    /// which costs one <c>IndexOf</c> and allocates nothing.
    /// <para>This is what makes a nameplate respect colour codes: without it the tags reach the glyph emit and
    /// draw as literal <c>&lt;c???&gt;</c> text. The grammar itself is not re-implemented here -- the chat
    /// box's <see cref="ColorRunParser"/> is the one parser, and this only flattens its runs.</para>
    /// </summary>
    public static string StripColours(string raw, out uint[]? colours)
    {
        colours = null;
        if (raw.Length == 0 || raw.IndexOf('<') < 0) return raw;

        ColorRun[] runs = ColorRunParser.Parse(raw);
        int len = 0;
        bool any = false;
        for (int i = 0; i < runs.Length; i++)
        {
            len += runs[i].Text.Length;
            if (Pack(runs[i]) != Plain) any = true;
        }
        if (len == 0) return "";

        var sb = new System.Text.StringBuilder(len);
        uint[]? cols = any ? new uint[len] : null;
        int n = 0;
        for (int i = 0; i < runs.Length; i++)
        {
            ColorRun run = runs[i];
            uint c = Pack(run);
            sb.Append(run.Text);
            if (cols != null) for (int k = 0; k < run.Text.Length; k++) cols[n + k] = c;
            n += run.Text.Length;
        }
        colours = cols;
        return sb.ToString();
    }

    private static uint Pack(in ColorRun r) =>
        ((uint)(r.R * 255f + 0.5f) << 16) | ((uint)(r.G * 255f + 0.5f) << 8) | (uint)(r.B * 255f + 0.5f);

    /// <summary>Consecutive spaces that stand in for a line break. Three, per the report: two is a normal
    /// wide gap, three is someone pushing text onto the next line.</summary>
    public const int SpacesPerBreak = 3;

    /// <summary>Whether <see cref="BreakName"/> would change anything -- the pre-scan that keeps the common
    /// name (no breaks, no padding, no stray edge space) allocation-free.</summary>
    private static bool NeedsBreak(string s)
    {
        if (s.Length == 0) return false;
        if (s[0] == ' ' || s[s.Length - 1] == ' ') return true;
        int spaces = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\n' || c == '\r') return true;
            if (c == '\\' && i + 1 < s.Length && s[i + 1] == 'n') return true;
            if (c == ' ') { if (++spaces >= SpacesPerBreak) return true; }
            else spaces = 0;
        }
        return false;
    }

    /// <summary>Index the LF-separated lines of a <see cref="BreakName"/>ed string into caller-owned span
    /// arrays. Pure and allocation-free, so the frame can measure and emit each line without a substring.
    /// Past <paramref name="max"/> lines the remainder is dropped: the last kept line ends at its own break,
    /// never mid-word, and the ceiling is the caller's wrap ceiling rather than a second one.</summary>
    /// <returns>the number of lines written; 0 for an empty string.</returns>
    public static int SplitLines(string s, int[] start, int[] len, int max)
    {
        if (s.Length == 0 || max < 1) return 0;
        int n = 0, from = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i != s.Length && s[i] != '\n') continue;
            start[n] = from; len[n] = i - from; n++;
            from = i + 1;
            if (n >= max) break;
        }
        return n;
    }

    /// <summary>
    /// Split an <c>ObjectTextBubbleOverride</c> payload into its segments, on a real LF / CR / CRLF and
    /// <b>nothing else</b>. Deliberately NOT <see cref="BreakName"/>: this is a wire format the server
    /// composes, not a builder-typed name field, and every one of BreakName's conveniences would corrupt it --
    /// the trim would eat the leading space that glues "Poisoned" onto the injury line, the collapse would eat
    /// the leading break that drops "Sneaking" below it, and the 3-space rule would break a status label that
    /// happens to be padded. Empty segments are preserved for exactly that reason: <c>"\nSneaking"</c> is one
    /// empty splice segment plus one line, and that empty segment is the statement that nothing joins the
    /// existing text.
    /// <para>Only trailing empty segments are dropped -- <c>"abc\n"</c> is one segment, not a segment and a
    /// blank line. Pure, COLD (once per override change), self-checked.</para>
    /// </summary>
    /// <param name="max">segment ceiling; a longer override keeps the first <paramref name="max"/>.</param>
    public static int SplitOverride(string s, int[] start, int[] len, int max)
    {
        if (s.Length == 0 || max < 1) return 0;
        int n = 0, from = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i != s.Length && s[i] != '\n' && s[i] != '\r') continue;
            start[n] = from; len[n] = i - from; n++;
            if (i + 1 < s.Length && s[i] == '\r' && s[i + 1] == '\n') i++;   // CRLF is one break, not two
            from = i + 1;
            if (n >= max) break;
        }
        while (n > 1 && len[n - 1] == 0) n--;
        return n;
    }

    /// <summary>
    /// The mode-to-placement half of the <c>ObjectTextBubbleOverride</c> rule: given the mode, how many
    /// segments there are and what the plate has to glue them to, which segment continues an existing line and
    /// which become lines of their own. Pure and atlas-free so the whole rule is self-checkable; the caller
    /// does the wrapping and the drawing.
    /// <para>The engine builds ONE string, <c>name\nband</c>, and joins the override to its head or tail
    /// (docs/re/nameplate.md §8.5) -- so append's FIRST segment continues the plate's last line and prepend's
    /// LAST segment runs into its first, while replace makes the segments the whole plate.</para>
    /// </summary>
    /// <returns>the line the segment at <paramref name="spliceIdx"/> joins, or <see cref="PlateSplice.None"/>
    /// when there is nothing to join and every segment stands alone.</returns>
    public static PlateSplice PlaceOverride(int mode, int paraCount, int nameLines, bool band,
                                            out bool showName, out bool showBand, out int spliceIdx,
                                            out int preFrom, out int preTo, out int postFrom, out int postTo)
    {
        showName = true; showBand = band;
        spliceIdx = -1;
        preFrom = 0; preTo = 0; postFrom = 0; postTo = 0;
        if (mode is < 1 or > 3 || paraCount < 1) return PlateSplice.None;

        if (mode == 1)   // replace: name and band are the server's to discard
        {
            showName = false; showBand = false;
            preTo = paraCount;
            return PlateSplice.None;
        }

        if (mode == 2)   // prepend: override + text
        {
            PlateSplice t = nameLines > 0 ? PlateSplice.NameFirst
                          : band ? PlateSplice.BandFirst : PlateSplice.None;
            if (t == PlateSplice.None) { preTo = paraCount; return t; }
            spliceIdx = paraCount - 1; preTo = paraCount - 1;
            return t;
        }

        // 3, append: text + override
        PlateSplice a = band ? PlateSplice.BandLast
                      : nameLines > 0 ? PlateSplice.NameLast : PlateSplice.None;
        if (a == PlateSplice.None) { postTo = paraCount; return a; }
        spliceIdx = 0; postFrom = 1; postTo = paraCount;
        return a;
    }

    /// <summary>1.0 nearer than <c>maxDist - fadeDist</c>, linear to 0.0 at <paramref name="maxDist"/>.
    /// <paramref name="maxDist"/> &lt;= 0 means unlimited. 0 =&gt; cull.</summary>
    public static float DistanceFade(float dist, float maxDist, float fadeDist)
    {
        if (maxDist <= 0f) return 1f;
        if (dist >= maxDist) return 0f;
        if (fadeDist <= 0f) return 1f;
        if (fadeDist > maxDist) fadeDist = maxDist;   // else the band starts behind the camera and nothing is ever opaque
        float start = maxDist - fadeDist;
        if (dist <= start) return 1f;
        return (maxDist - dist) / fadeDist;
    }

    /// <summary>Hold at 1 for <paramref name="holdSec"/> then linear to 0 over <paramref name="fadeSec"/>
    /// (the <c>ChatAnim.FadeAlpha</c> shape, on a monotonic ms clock).</summary>
    public static float BubbleAlpha(long nowMs, long postedMs, float holdSec, float fadeSec)
    {
        float age = (nowMs - postedMs) / 1000f;
        if (age <= holdSec) return 1f;
        if (fadeSec <= 0f) return 0f;
        if (age >= holdSec + fadeSec) return 0f;
        return 1f - (age - holdSec) / fadeSec;
    }

    /// <summary>Where a speech-bubble tail hangs off the bottom of its box. The tip tracks the ANCHOR, not the
    /// box centre: <see cref="Place"/> clamps the box into the viewport and <see cref="Separate"/> pushes it
    /// around, so a tail nailed to the box would point at nothing near a screen edge or in a crowd. The base is
    /// clamped to stay inside the box (inset by <paramref name="inset"/>) and the tip to stay within its span,
    /// so a box pushed clear of its speaker gets a tail leaning hard sideways rather than a detached one. A box
    /// narrower than the tail drops the inset, then the tail's own width, rather than inverting.</summary>
    /// <param name="baseL">left end of the tail's base along the box's bottom edge.</param>
    /// <param name="baseR">right end of it; equal to <paramref name="baseL"/> when there is no room at all.</param>
    /// <param name="tipX">where the point sits, <paramref name="tailH"/> px below that edge.</param>
    public static void TailPoints(float anchorX, float boxX, float boxW, float tailW, float inset,
                                  out float baseL, out float baseR, out float tipX)
    {
        float avail = boxW - inset * 2f;
        if (avail < tailW) { inset = 0f; avail = boxW; }
        if (avail < tailW) tailW = avail > 0f ? avail : 0f;

        float half = tailW * 0.5f;
        float lo = boxX + inset + half, hi = boxX + boxW - inset - half;
        if (hi < lo) lo = hi = boxX + boxW * 0.5f;

        float cx = anchorX < lo ? lo : (anchorX > hi ? hi : anchorX);
        baseL = cx - half; baseR = cx + half;
        tipX = anchorX < boxX ? boxX : (anchorX > boxX + boxW ? boxX + boxW : anchorX);
    }

    /// <summary>Our replacement for the engine's <c>BumpAllBubbles</c>, which mode 0 disables: one pass, pushing
    /// every box that still overlaps an already-placed one above it, then re-clamping into the viewport so a
    /// pushed plate can never walk off the top of the screen. Boxes are visited in the caller's array order, so
    /// the result is stable frame to frame. Hot: in-place over the caller's arrays, no allocation.
    /// <para>ponytail: single pass, vertical only, and the viewport clamp can re-introduce the overlap it just
    /// resolved in a dense crowd. The upgrade path is the engine's own shape -- iterate to a fixed point, and pad
    /// horizontally by the portrait width in mode 2.</para></summary>
    /// <param name="vpH">viewport height; &lt;= 0 means "unknown" and leaves the push unclamped.</param>
    public static void Separate(float[] x, float[] y, float[] w, float[] h, int count, float gap, float vpH)
        => Separate(x, y, w, h, null, count, gap, vpH);

    /// <summary>The ordered form: <paramref name="order"/> names which boxes take part and in what sequence, and
    /// <paramref name="count"/> is its length, not the box count. The FIRST index visited keeps its placed
    /// position and every later one is pushed clear ABOVE it, so a recency-ordered list (see
    /// <see cref="OrderByRecency"/>) stacks the newest message nearest its speaker and the older ones above it.
    /// A box whose index is absent takes no part at all: it neither pushes nor is pushed, which is how a box in
    /// its fade-out stops shoving the live ones around. <c>null</c> means "every box, in array order".</summary>
    /// <param name="vpH">viewport height; &lt;= 0 means "unknown" and leaves the push unclamped.</param>
    public static void Separate(float[] x, float[] y, float[] w, float[] h, int[]? order, int count,
                                float gap, float vpH)
    {
        for (int oi = 1; oi < count; oi++)
        {
            int i = order == null ? oi : order[oi];
            for (int oj = 0; oj < oi; oj++)
            {
                int j = order == null ? oj : order[oj];
                if (x[i] + w[i] <= x[j] || x[j] + w[j] <= x[i]) continue;
                if (y[i] + h[i] <= y[j] || y[j] + h[j] <= y[i]) continue;
                y[i] = y[j] - h[i] - gap;
            }
            if (vpH <= 0f) continue;
            float maxY = vpH - h[i];
            if (y[i] > maxY) y[i] = maxY;
            if (y[i] < 0f) y[i] = 0f;
        }
    }

    /// <summary>Fill <paramref name="order"/> with the included box indices, newest <paramref name="postedMs"/>
    /// first, so <see cref="Separate"/> stacks a conversation chronologically instead of in screen order. Ties
    /// break on the ascending object <paramref name="ids"/>, which are unique, so the sequence is a total order
    /// and cannot flicker between two frames with the same input. Hot: an insertion sort over a handful of
    /// boxes, in place, no allocation.</summary>
    /// <returns>how many indices were written -- the <c>count</c> to hand <see cref="Separate"/>.</returns>
    public static int OrderByRecency(int[] order, long[] postedMs, uint[] ids, bool[] include, int count)
    {
        int n = 0;
        for (int i = 0; i < count; i++) if (include[i]) order[n++] = i;
        for (int i = 1; i < n; i++)
        {
            int v = order[i];
            long kp = postedMs[v]; uint kid = ids[v];
            int j = i - 1;
            while (j >= 0 && SortsAfter(postedMs[order[j]], ids[order[j]], kp, kid))
            { order[j + 1] = order[j]; j--; }
            order[j + 1] = v;
        }
        return n;
    }

    /// <summary>True when (<paramref name="ap"/>, <paramref name="aid"/>) belongs BEHIND (<paramref name="bp"/>,
    /// <paramref name="bid"/>): older, or the same instant with a larger id.</summary>
    private static bool SortsAfter(long ap, uint aid, long bp, uint bid)
        => ap < bp || (ap == bp && aid > bid);

    /// <summary>Absorb a change in a box's separation push into its slide residual, so a box that changes stack
    /// slot eases to the new one instead of teleporting. Draw at <c>placed + push + yOff</c>: the residual is set
    /// so that expression is unchanged on the frame the push moves, and <see cref="SettleOffset"/> then closes
    /// it. Pure book-keeping -- it never touches the placement itself.</summary>
    public static void AbsorbPush(ref float yOff, ref float lastPush, float push)
    {
        yOff += lastPush - push;
        lastPush = push;
    }

    /// <summary>Health-band colour, 0..1. Out-of-range indices clamp to the nearest band.</summary>
    public static (float r, float g, float b) BandColor(int band)
    {
        int i = band < 0 ? 0 : band >= BandCount ? BandCount - 1 : band;
        int o = i * 3;
        return (BandRgb[o] / 255f, BandRgb[o + 1] / 255f, BandRgb[o + 2] / 255f);
    }

    /// <summary>Health-band caption. Out-of-range indices clamp to the nearest band; the returned string is a
    /// cached literal, never built (hot path).</summary>
    public static string BandText(int band) =>
        BandTexts[band < 0 ? 0 : band >= BandCount ? BandCount - 1 : band];

    /// <summary>Name colour by reaction byte (<c>CNWCCreature + 0x3a8</c>): 1 friend green, 2 enemy red,
    /// anything else neutral blue. 0..1.</summary>
    public static (float r, float g, float b) ReactionColor(byte reaction) => reaction switch
    {
        1 => (128f / 255f, 255f / 255f, 128f / 255f),
        2 => (255f / 255f, 80f / 255f, 64f / 255f),
        _ => (128f / 255f, 128f / 255f, 255f / 255f),
    };

    /// <summary>Off-device proof of the placement clamp, the measured boxes, both fade curves, the decay clamp
    /// and the band/reaction tables.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"PlateLayout.SelfCheck FAIL: {w}"); } }
        static bool Near(float a, float b) => System.Math.Abs(a - b) < 1e-4f;

        // ---- Place: centred, above, and clamped into the viewport at every corner ----
        Place(640f, 400f, 100f, 40f, 1280f, 800f, out float px, out float py);
        E(Near(px, 590f), "place centres on the anchor");
        E(Near(py, 360f), "place sits above the anchor");
        Place(0f, 0f, 100f, 40f, 1280f, 800f, out px, out py);
        E(px == 0f && py == 0f, "top-left anchor clamps to (0,0)");
        Place(1280f, 800f, 100f, 40f, 1280f, 800f, out px, out py);
        E(Near(px, 1180f) && Near(py, 760f), "bottom-right anchor clamps fully on-screen");
        Place(1280f, 0f, 100f, 40f, 1280f, 800f, out px, out py);
        E(Near(px, 1180f) && py == 0f, "top-right anchor clamps on both axes");
        Place(0f, 800f, 100f, 40f, 1280f, 800f, out px, out py);
        E(px == 0f && Near(py, 760f), "bottom-left anchor clamps on both axes");
        Place(640f, 400f, 2000f, 40f, 1280f, 800f, out px, out py);
        E(px == 0f, "a plate wider than the viewport pins to the left edge");
        Place(640f, 400f, 100f, 2000f, 1280f, 800f, out px, out py);
        E(py == 0f, "a plate TALLER than the viewport pins to the top edge (the multi-line name case)");
        Place(640f, 400f, 100f, 40f, 0f, 0f, out px, out py);
        E(Near(px, 590f) && Near(py, 360f), "an unknown viewport leaves the point unclamped");

        // ---- ToDrawSpace: the engine's bottom-left pixels into the window's top-left ones ----
        // Camera viewport == draw viewport, GUI scale 1: a pure vertical mirror.
        ToDrawSpace(100f, 200f, 0f, 0f, 1280f, 800f, 1f, 1280f, 800f, out float dx, out float dy);
        E(Near(dx, 100f), "x passes through when the viewports match");
        E(Near(dy, 600f), "y flips about the viewport height");
        ToDrawSpace(0f, 0f, 0f, 0f, 1280f, 800f, 1f, 1280f, 800f, out dx, out dy);
        E(dx == 0f && Near(dy, 800f), "the engine's origin is the window's BOTTOM-left");
        ToDrawSpace(1280f, 800f, 0f, 0f, 1280f, 800f, 1f, 1280f, 800f, out dx, out dy);
        E(Near(dx, 1280f) && dy == 0f, "the engine's top-right is the window's top-right");
        ToDrawSpace(640f, 400f, 0f, 0f, 1280f, 800f, 1f, 1280f, 800f, out dx, out dy);
        E(Near(dx, 640f) && Near(dy, 400f), "the centre is a fixed point of the mirror");
        // A world point ABOVE a creature's origin projects HIGHER in engine space, which must be a SMALLER y.
        ToDrawSpace(640f, 500f, 0f, 0f, 1280f, 800f, 1f, 1280f, 800f, out _, out float above);
        ToDrawSpace(640f, 400f, 0f, 0f, 1280f, 800f, 1f, 1280f, 800f, out _, out float below);
        E(above < below, "higher on screen in engine space is a smaller draw y");

        // An offset, upscaled render viewport: rebase then rescale, and the flip is about the camera rect.
        ToDrawSpace(100f, 50f, 100f, 50f, 640f, 400f, 1f, 1280f, 800f, out dx, out dy);
        E(dx == 0f && Near(dy, 800f), "the camera rect's origin maps to the draw viewport's bottom-left");
        ToDrawSpace(740f, 450f, 100f, 50f, 640f, 400f, 1f, 1280f, 800f, out dx, out dy);
        E(Near(dx, 1280f) && dy == 0f, "the camera rect's far corner maps to the draw viewport's top-right");

        // GUI scale: inside the engine's own 0.09 dead band nothing was shrunk, so nothing is un-shrunk.
        ToDrawSpace(340f, 200f, 0f, 0f, 1280f, 800f, 1.05f, 1280f, 800f, out dx, out dy);
        E(Near(dx, 340f), "a GUI scale inside the +-0.09 dead band is not undone");
        ToDrawSpace(340f, 200f, 0f, 0f, 1280f, 800f, 2f, 1280f, 800f, out dx, out dy);
        E(Near(dx, 640f + (340f - 640f) * 2f), "a GUI scale past the dead band scales out about the centre");
        ToDrawSpace(640f, 400f, 0f, 0f, 1280f, 800f, 2f, 1280f, 800f, out dx, out dy);
        E(Near(dx, 640f) && Near(dy, 400f), "the rect centre is fixed under any GUI scale");
        ToDrawSpace(340f, 200f, 0f, 0f, 1280f, 800f, 0f, 1280f, 800f, out dx, out dy);
        E(Near(dx, 340f), "a zero GUI scale is treated as unknown, not as a collapse to the centre");

        ToDrawSpace(100f, 200f, 0f, 0f, 0f, 0f, 1f, 1280f, 800f, out dx, out dy);
        E(Near(dx, 100f) && Near(dy, 600f), "an unknown camera viewport still flips about the draw height");

        // ---- FadeStep / MoveToward / SettleOffset ----
        E(FadeStep(0, 180f) == 0f, "no elapsed time is no step");
        E(FadeStep(-500, 180f) == 0f, "a clock that ran backwards is no step");
        E(Near(FadeStep(90, 180f), 0.5f), "half the interval is half the step");
        E(FadeStep(180, 180f) == 1f, "exactly the interval crosses the whole range");
        E(FadeStep(10000, 180f) == 1f, "far past the interval still only crosses the range once");
        E(FadeStep(16, 0f) == 1f, "a zero interval snaps");

        float v = 0f;
        MoveToward(ref v, 1f, FadeStep(90, 180f));
        E(Near(v, 0.5f), "a fade-in reaches half way at half the interval");
        MoveToward(ref v, 1f, FadeStep(90, 180f));
        E(v == 1f, "the second half lands exactly on the target");
        MoveToward(ref v, 1f, FadeStep(90, 180f));
        E(v == 1f, "stepping past the target never overshoots");
        MoveToward(ref v, 0f, FadeStep(360, 180f));
        E(v == 0f, "a long frame fades all the way out, not past it");
        v = 0.5f;
        MoveToward(ref v, 1f, FadeStep(-5, 180f));
        E(Near(v, 0.5f), "a backwards clock leaves the alpha alone");

        float off = 14f;
        SettleOffset(ref off, 0, 180f);
        E(off == 14f, "no elapsed time leaves the slide where it was");
        SettleOffset(ref off, -100, 180f);
        E(off == 14f, "a backwards clock leaves the slide where it was");
        SettleOffset(ref off, 16, 180f);
        E(off < 14f && off > 0f, "a frame's worth of ease closes part of the slide, toward zero");
        for (int i = 0; i < 200; i++) SettleOffset(ref off, 16, 180f);
        E(off == 0f, "the slide snaps to exactly zero instead of asymptoting");
        off = -14f;
        for (int i = 0; i < 200; i++) SettleOffset(ref off, 16, 180f);
        E(off == 0f, "a slide from above settles the same way");
        off = 14f;
        SettleOffset(ref off, 16, 0f);
        E(off == 0f, "a zero interval is 'no animation': the slide snaps rather than easing at the tau floor");
        off = 14f;
        SettleOffset(ref off, 16, 1f);
        E(off < 14f && off > 0f, "a sub-tau-floor interval still eases at the floor rather than dividing by zero");

        // ---- Measure ----
        PlateMetrics m = Measure(80f, 60f, portrait: true, band: true, bar: true, lineH: 18f, scale: 1f);
        E(Near(m.PortraitSize, 18f + 18f + 4f + 3f),
          "a ONE-line name spans the whole content column, exactly as it always did");
        E(Near(m.PortraitSize * 0.64f, m.NameX - 12f), "and keeps the 0.64 aspect at that height");
        E(Near(m.NameX, 4f + m.PortraitSize * 0.64f + 8f), "name column clears the portrait");
        E(Near(m.BandX, m.NameX) && Near(m.BandY, 4f + 18f), "band sits on the second line, same column");
        E(Near(m.BarW, 80f) && Near(m.BarH, 3f), "bar spans the text column at the bar thickness");
        E(Near(m.BarY, 4f + 18f + 18f + 4f), "bar sits under the band");
        E(Near(m.H, m.PortraitSize + 8f), "height = content + both pads");
        E(Near(m.PortraitY, 4f),
          "a portrait as tall as its content centres to exactly the pad -- unchanged from the top-aligned rule");
        E(Near(m.BarX + m.BarW * 0.5f, m.NameX + m.TextW * 0.5f),
          "the bar spans the whole text column, so it is centred by construction");
        E(Near(m.W, m.NameX + 80f + 4f), "width = column + widest text + pad");

        PlateMetrics tex = Measure(80f, 60f, portrait: false, band: false, bar: false, lineH: 18f, scale: 1f);
        E(tex.PortraitSize == 0f && Near(tex.NameX, 4f), "no portrait: text starts at the pad");
        E(Near(tex.H, 26f), "text-only height is one line plus pads");
        E(tex.BarW == 0f && tex.BarH == 0f, "no bar: zero bar box");

        PlateMetrics sc = Measure(80f, 60f, portrait: false, band: false, bar: false, lineH: 36f, scale: 2f);
        E(Near(sc.NameX, 8f), "chrome pads scale, glyph metrics do not");
        E(Near(sc.H, 36f + 16f), "scaled height = the given line height plus scaled pads");
        E(Near(m.TextW, 80f) && Near(tex.TextW, 80f), "the text column is the widest of name and band");

        // ---- Measure with an N-line name ----
        PlateMetrics m3 = Measure(80f, 3, 60f, portrait: true, band: true, bar: true, lineH: 18f, scale: 1f);
        E(Near(m3.H, m.H + 18f * 2f), "two extra name lines add exactly two line heights to the box");
        E(Near(m3.PortraitSize, 18f * 2f + 18f + 4f + 3f),
          "a creature's portrait grows by ONE more name line and no further, band and bar still on top");
        E(Near(m3.NameX, 4f + m3.PortraitSize * 0.64f + 8f), "the text column clears the WIDER portrait");
        E(Near(m3.W - m.W, m3.NameX - m.NameX), "and the box widens by exactly that, nothing else moving");
        E(Near(m3.BandY, 4f + 18f * 3f), "the band follows the LAST name line");
        E(Near(m3.BarY, m3.BandY + 18f + 4f), "and the bar follows the band");
        E(Near(m3.PortraitY, 4f + (18f * 3f + 18f + 4f + 3f - m3.PortraitSize) * 0.5f) && Near(m3.PortraitY, 13f),
          "a three-line name outgrows the capped portrait, which centres in the taller column");
        E(Near(m3.PortraitY + m3.PortraitSize * 0.5f, m3.H * 0.5f),
          "and the portrait's centre is the box's centre, the pads being equal");
        E(Near(Measure(80f, 1, 60f, true, true, true, 18f, 1f).H, m.H), "one line is the one-line overload");
        E(Near(Measure(80f, 0, 60f, true, true, true, 18f, 1f).H, m.H), "a zero line count is treated as one");

        // ---- the portrait grows with the name, up to two lines ---- (a placeable: no band, no bar)
        PlateMetrics p1 = Measure(80f, 1, 0f, portrait: true, band: false, bar: false, lineH: 18f, scale: 1f);
        E(Near(p1.PortraitSize, 18f), "a one-line plate gets a ONE-line portrait, unchanged");
        PlateMetrics p2 = Measure(80f, 2, 0f, portrait: true, band: false, bar: false, lineH: 18f, scale: 1f);
        E(Near(p2.PortraitSize, 36f), "a second name line grows the portrait to two lines");
        PlateMetrics p5 = Measure(80f, 5, 0f, portrait: true, band: false, bar: false, lineH: 18f, scale: 1f);
        E(Near(p5.PortraitSize, p2.PortraitSize), "a fifth line does not grow it any further");
        E(Near(p5.H, 5f * 18f + 8f), "and the box still grows to the whole name");
        E(Near(Measure(80f, 1, 60f, true, true, true, 18f, 1f).PortraitSize, 18f + 18f + 4f + 3f),
          "a one-line CREATURE portrait is byte-identical to the pre-change rule (never shrunk by the cap)");
        E(Near(p1.NameX, 4f + 18f * 0.64f + 8f) && Near(p2.NameX, 4f + 36f * 0.64f + 8f)
          && Near(p5.NameX, p2.NameX), "the text column clears the portrait at every height, at the 0.64 aspect");
        E(Near(p1.PortraitY, 4f) && Near(p2.PortraitY, 4f),
          "up to the cap the portrait fills its content exactly and stays at the pad");
        E(Near(p5.PortraitY, 4f + (5f * 18f - p5.PortraitSize) * 0.5f) && Near(p5.PortraitY, 31f),
          "past the cap the shorter portrait centres against the full content height");
        E(Near(p5.PortraitY + p5.PortraitSize * 0.5f, p5.H * 0.5f), "which is the box's centre");

        // ---- PortraitTop: the rule itself, including the case Measure cannot reach ----
        E(Near(PortraitTop(4f, 90f, 36f), 31f), "a short portrait centres in the content");
        E(Near(PortraitTop(4f, 36f, 36f), 4f), "an exactly-fitting portrait sits at the pad");
        E(Near(PortraitTop(4f, 20f, 36f), 4f),
          "a portrait TALLER than its content pins to the pad -- no negative top, no empty space above it");

        // ---- Measure with the ObjectTextBubbleOverride bands (docs/re/nameplate.md §8.5) ----
        PlateMetrics ovNone = Measure(80f, 1, 60f, true, true, true, 18f, 1f, 0, 0, 0f);
        E(Near(ovNone.H, m.H) && Near(ovNone.W, m.W) && Near(ovNone.NameY, m.NameY)
          && Near(ovNone.BandY, m.BandY) && Near(ovNone.PortraitY, m.PortraitY),
          "no override lines is byte-identical to the plate that never had the feature");
        PlateMetrics post = Measure(80f, 1, 60f, true, true, true, 18f, 1f, 0, 2, 0f);
        E(Near(post.H, m.H + 36f), "two appended lines add exactly two line heights");
        E(Near(post.NameY, m.NameY) && Near(post.BandY, m.BandY),
          "and nothing above them moves -- append grows the box downward only");
        E(Near(post.PostY, 4f + 18f + 18f + 4f + 3f), "the appended lines start below the band AND the bar");
        PlateMetrics pre = Measure(80f, 1, 60f, true, true, true, 18f, 1f, 2, 0, 0f);
        E(Near(pre.H, m.H + 36f) && Near(pre.PreY, 4f), "prepended lines start at the pad and grow the box");
        E(Near(pre.NameY, m.NameY + 36f) && Near(pre.BandY, m.BandY + 36f) && Near(pre.BarY, m.BarY + 36f),
          "and push the name, band and bar down by exactly their height");
        E(Near(pre.PortraitY, 4f + (79f - pre.PortraitSize) * 0.5f) && Near(pre.PortraitY, 22f),
          "the portrait centres against the WHOLE composed column, pre-lines included");
        E(Near(pre.PortraitSize, m.PortraitSize), "and is not stretched by the extra lines");
        E(Near(post.PortraitY, pre.PortraitY),
          "an append of the same line count centres the portrait identically -- it is the height that counts");
        PlateMetrics wide = Measure(80f, 1, 60f, true, true, true, 18f, 1f, 0, 1, 140f);
        E(Near(wide.TextW, 140f) && Near(wide.W, wide.NameX + 140f + 4f),
          "an override line wider than name and band widens the text column");
        PlateMetrics repl = Measure(0f, 0, 0f, true, false, false, 18f, 1f, 2, 0, 90f);
        E(Near(repl.H, 36f + 8f), "replace mode's lines ARE the plate: no name line and no band survive");
        E(Near(repl.PortraitSize, 36f), "and the portrait spans them exactly as it would name lines");

        // ---- LineX: every line of every plate centres in the text column, as vanilla does ----
        E(Near(LineX(20f, 100f, 40f), 50f), "a line is inset by half the slack");
        E(Near(LineX(20f, 100f, 40f) + 40f * 0.5f, 20f + 100f * 0.5f),
          "so its centre is the column's centre -- name, band and override line alike");
        E(Near(LineX(20f, 100f, 100f), 20f), "the widest line still starts at the column");
        E(Near(LineX(20f, 100f, 140f), 20f), "an over-wide line pins to the column, never left of it");
        float shortX = LineX(20f, 100f, 30f), longX = LineX(20f, 100f, 90f);
        E(shortX > longX, "a short line sits further right than a long one");
        E(shortX >= 20f && shortX + 30f <= 120f && longX + 90f <= 120f, "both stay inside the text column");

        // Every line type, on both plate shapes: a creature (name + band + bar, band the narrower line) and a
        // placeable (two name lines, no band). The layout takes no object type at all -- that is the point.
        PlateMetrics cre = Measure(80f, 1, 60f, true, true, true, 18f, 1f, 0, 1, 50f);
        float creCentre = cre.NameX + cre.TextW * 0.5f;
        E(Near(LineX(cre.NameX, cre.TextW, 80f) + 40f, creCentre), "a creature's name line centres");
        E(Near(LineX(cre.BandX, cre.TextW, 60f) + 30f, creCentre), "its health band centres in the same column");
        E(Near(LineX(cre.NameX, cre.TextW, 50f) + 25f, creCentre), "and so does its override line");
        E(Near(cre.BarX + cre.BarW * 0.5f, creCentre), "the bar spans the column, centred by construction");
        float hitL = LineX(cre.NameX, cre.TextW, 30f);
        E(hitL >= cre.NameX && hitL + 30f <= cre.NameX + cre.TextW,
          "the click-to-tell rect is the whole text column, so a centred short name line still lands inside it");
        PlateMetrics plc = Measure(80f, 2, 0f, true, false, false, 18f, 1f, 1, 0, 40f);
        float plcCentre = plc.NameX + plc.TextW * 0.5f;
        E(Near(LineX(plc.NameX, plc.TextW, 30f) + 15f, plcCentre)
          && Near(LineX(plc.NameX, plc.TextW, 40f) + 20f, plcCentre),
          "a placeable centres its name and override lines the same way -- no per-type rule survives");
        E(Near(plc.PortraitY, 4f + (54f - plc.PortraitSize) * 0.5f) && Near(plc.PortraitY, 13f),
          "and its capped portrait centres against name plus override, not against the name alone");

        // ---- PlaceOverride: which segment joins which plate line, per mode ----
        PlateSplice Placement(int mode, int paras, int nameLines, bool band, out bool sn, out bool sb,
                          out int si, out int pf, out int pt, out int qf, out int qt)
            => PlaceOverride(mode, paras, nameLines, band, out sn, out sb, out si, out pf, out pt, out qf, out qt);

        PlateSplice t = Placement(3, 2, 1, true, out bool sn, out bool sb, out int si,
                              out int pf, out int pt, out int qf, out int qt);
        E(t == PlateSplice.BandLast && si == 0 && qf == 1 && qt == 2 && pt == 0 && sn && sb,
          "append glues its FIRST segment onto the band and drops the rest below it");
        t = Placement(2, 2, 1, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        E(t == PlateSplice.NameFirst && si == 1 && pf == 0 && pt == 1 && qt == 0 && sn && sb,
          "prepend runs its LAST segment into the name and puts the rest above it");
        t = Placement(1, 2, 1, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        E(t == PlateSplice.None && si == -1 && pf == 0 && pt == 2 && !sn && !sb,
          "replace makes the segments the WHOLE plate -- no name line and no band survive");
        t = Placement(0, 2, 1, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        E(t == PlateSplice.None && si == -1 && pt == 0 && qt == 0 && sn && sb,
          "mode 0 leaves the plate untouched, override segments or not");
        t = Placement(3, 0, 1, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        E(t == PlateSplice.None && si == -1 && qt == 0 && sn && sb,
          "an override with no segments is no override");
        t = Placement(3, 2, 1, false, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        E(t == PlateSplice.NameLast && si == 0 && qf == 1 && qt == 2,
          "with the band off, append glues onto the LAST name line instead");
        t = Placement(2, 2, 0, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        E(t == PlateSplice.BandFirst && si == 1 && pt == 1,
          "with no name line, prepend runs into the FRONT of the band -- never off its end");
        t = Placement(3, 2, 0, false, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        E(t == PlateSplice.None && si == -1 && qf == 0 && qt == 2,
          "with nothing to glue to, every append segment stands as its own line");
        t = Placement(2, 1, 1, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        E(t == PlateSplice.NameFirst && si == 0 && pt == 0, "a single-segment prepend is all splice");
        t = Placement(3, 1, 1, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        E(t == PlateSplice.BandLast && si == 0 && qf == 1 && qt == 1, "a single-segment append likewise");

        // ---- SplitOverride: the wire format, NOT a builder-typed name ----
        int[] os = new int[8], ol = new int[8];
        string Seg(string s, int i) => s.Substring(os[i], ol[i]);
        const string vec1 = " Poisoned Diseased\nSneaking Invisible";
        E(SplitOverride(vec1, os, ol, 8) == 2 && Seg(vec1, 0) == " Poisoned Diseased"
          && Seg(vec1, 1) == "Sneaking Invisible",
          "the server's composed override splits into its splice and its own line, space intact");
        const string vec2 = "\nSneaking";
        E(SplitOverride(vec2, os, ol, 8) == 2 && ol[0] == 0 && Seg(vec2, 1) == "Sneaking",
          "a leading break leaves an EMPTY first segment -- nothing joins the existing line");
        const string vec3 = "a\r\nb\rc\nd";
        E(SplitOverride(vec3, os, ol, 8) == 4 && Seg(vec3, 1) == "b" && Seg(vec3, 2) == "c",
          "LF, CR and CRLF are each ONE break");
        const string vec4 = "abc\n";
        E(SplitOverride(vec4, os, ol, 8) == 1 && Seg(vec4, 0) == "abc",
          "a trailing break is not a blank line");
        const string vec5 = " a   b\\nc";
        E(SplitOverride(vec5, os, ol, 8) == 1 && Seg(vec5, 0) == vec5,
          "BreakName's conveniences do NOT apply: no trim, no 3-space rule, no literal backslash-n");
        E(SplitOverride("", os, ol, 8) == 0, "an empty override is no segments at all");
        E(SplitOverride("a\nb\nc", os, ol, 2) == 2, "the segment ceiling truncates rather than overrunning");

        // ---- BreakName ----
        const string plain = "Events Board";
        E(ReferenceEquals(BreakName(plain), plain), "a name with nothing to rewrite is returned as-is");
        E(BreakName("") == "", "an empty name stays empty");
        E(BreakName("   ") == "", "a name that is only spaces collapses to nothing");
        E(BreakName("a\nb") == "a\nb", "a real LF is one break");
        E(BreakName("a\r\nb") == "a\nb", "CRLF collapses to one break");
        E(BreakName("a\rb") == "a\nb", "a bare CR is one break");
        E(BreakName("a\\nb") == "a\nb", "the literal two-character backslash-n is one break");
        E(BreakName("a  b") == "a  b", "exactly two spaces stay literal");
        E(BreakName("a b") == "a b", "one space stays literal");
        E(BreakName("a   b") == "a\nb", "exactly three spaces are one break");
        E(BreakName("a          b") == "a\nb", "a long run of spaces is ONE break, not one per space");
        E(BreakName("a  \n  b") == "a\nb", "spaces either side of a break are trimmed away");
        E(BreakName("a\n\n\nb") == "a\nb", "consecutive breaks collapse to one -- no blank lines");
        E(BreakName("\na") == "a" && BreakName("a\n") == "a", "a break at either end leaves no empty line");
        E(BreakName("   a   ") == "a", "leading and trailing whitespace is trimmed outright");
        E(BreakName("- {Ministry of Culture} -          Events Board!")
          == "- {Ministry of Culture} -\nEvents Board!", "the reported padded name splits into header + line");

        // ---- StripColours + the colour-carrying BreakName ----
        string tagged = "<c" + (char)0x10 + (char)0x20 + (char)0x30 + ">Bob</c> the <c"
                      + (char)0xff + (char)0x00 + (char)0x00 + ">Red</c>";
        string bare = StripColours(tagged, out uint[]? bc);
        E(bare == "Bob the Red", "the colour tags leave the drawable string");
        E(bc != null && bc.Length == bare.Length, "one colour per char of the stripped string");
        E(bc![0] == 0x102030u && bc[3] == Plain && bc[8] == 0xff0000u,
          "each char carries the colour whose tag covered it, and untagged chars stay plain");
        E(ReferenceEquals(StripColours(plain, out uint[]? nc2), plain) && nc2 == null,
          "a name with no '<' is returned as-is and allocates no colour array");
        E(StripColours("<c" + (char)1 + (char)1 + (char)1 + "></c>", out _) == "",
          "a name that is nothing but tags strips to nothing");

        string cbSrc = StripColours("<c" + (char)0x10 + (char)0x20 + (char)0x30 + ">hdr</c>   aaa bbb",
                                    out uint[]? cbCols);
        string cbOut = BreakName(cbSrc, cbCols, out uint[]? cbOutCols);
        E(cbOut == "hdr\naaa bbb", "the stripped name still breaks where its author asked");
        E(cbOutCols != null && cbOutCols.Length == cbOut.Length,
          "the colour array survives the break, index for index");
        E(cbOutCols![0] == 0x102030u && cbOutCols[2] == 0x102030u && cbOutCols[4] == Plain,
          "and every surviving char kept the colour it had before the rewrite");
        E(BreakName(cbSrc, null, out uint[]? cbNone) == cbOut && cbNone == null,
          "a null colour array stays null through the same rewrite");

        // ---- SplitLines ----
        var ls = new int[4]; var ll = new int[4];
        E(SplitLines("", ls, ll, 4) == 0, "an empty name has no lines");
        E(SplitLines("abc", ls, ll, 4) == 1 && ls[0] == 0 && ll[0] == 3, "a break-free name is one whole line");
        int ln3 = SplitLines("ab\ncd\ne", ls, ll, 4);
        E(ln3 == 3 && ls[0] == 0 && ll[0] == 2 && ls[1] == 3 && ll[1] == 2 && ls[2] == 6 && ll[2] == 1,
          "each span points at its own line, the LF in neither");
        E(SplitLines("a\nb\nc\nd\ne", ls, ll, 4) == 4, "the cap is a hard ceiling; the remainder is dropped");
        E(ll[3] == 1, "and the last kept line ends at its own break, not mid-word");
        E(SplitLines("abc", ls, ll, 0) == 0, "a zero cap asks for no lines at all");

        // ---- DistanceFade ----
        E(DistanceFade(500f, 0f, 10f) == 1f, "maxDist 0 is unlimited");
        E(DistanceFade(500f, -1f, 10f) == 1f, "negative maxDist is unlimited");
        E(DistanceFade(10f, 45f, 10f) == 1f, "well inside is fully opaque");
        E(DistanceFade(35f, 45f, 10f) == 1f, "exactly at maxDist-fadeDist is still 1");
        E(Near(DistanceFade(40f, 45f, 10f), 0.5f), "mid-band is half");
        E(DistanceFade(45f, 45f, 10f) == 0f, "exactly at maxDist is 0");
        E(DistanceFade(60f, 45f, 10f) == 0f, "beyond maxDist is 0");
        E(DistanceFade(44.9f, 45f, 0f) == 1f, "zero fade band is a hard step");
        E(DistanceFade(0f, 5f, 10f) == 1f, "a fade band wider than maxDist clamps to it");
        E(Near(DistanceFade(2.5f, 5f, 10f), 0.5f), "a clamped fade band ramps over the whole range");

        // ---- BubbleAlpha ----
        E(BubbleAlpha(1000, 1000, 4f, 2f) == 1f, "fresh bubble fully opaque");
        E(BubbleAlpha(3000, 1000, 4f, 2f) == 1f, "mid-hold still opaque");
        E(BubbleAlpha(5000, 1000, 4f, 2f) == 1f, "exactly at the hold end is still 1");
        E(Near(BubbleAlpha(6000, 1000, 4f, 2f), 0.5f), "mid-fade is half");
        E(BubbleAlpha(7000, 1000, 4f, 2f) == 0f, "exactly at hold+fade is 0");
        E(BubbleAlpha(60000, 1000, 4f, 2f) == 0f, "past the end is 0");
        E(BubbleAlpha(6000, 1000, 4f, 0f) == 0f, "zero fade drops straight to 0 after the hold");
        E(BubbleAlpha(500, 1000, 4f, 2f) == 1f, "a clock that runs backwards never exceeds 1");

        // ---- TailPoints ---- (box spans x 100..200, tail 12 wide, inset 5)
        TailPoints(150f, 100f, 100f, 12f, 5f, out float tl, out float tr, out float tp);
        E(Near(tp, 150f), "the tail tip sits on the anchor when the box is centred under it");
        E(Near(tl, 144f) && Near(tr, 156f), "and its base straddles the anchor");
        E(tl >= 105f && tr <= 195f, "the base stays inside the box, inset from the corners");
        TailPoints(90f, 100f, 100f, 12f, 5f, out tl, out tr, out tp);
        E(Near(tl, 105f) && Near(tr, 117f), "an anchor off the left end pins the base at that end");
        E(Near(tp, 100f), "and the tip leans to the box edge rather than detaching from it");
        TailPoints(300f, 100f, 100f, 12f, 5f, out tl, out tr, out tp);
        E(Near(tr, 195f) && Near(tp, 200f), "an anchor off the right end pins the base and tip at that end");
        TailPoints(104f, 100f, 8f, 12f, 5f, out tl, out tr, out tp);
        E(Near(tl, 100f) && Near(tr, 108f), "a box narrower than the tail gets a tail its own width, no wider");
        TailPoints(100f, 100f, 0f, 12f, 5f, out tl, out tr, out tp);
        E(Near(tl, tr), "a zero-width box produces no tail at all");

        // ---- Separate ----
        // Two boxes at the same anchor: the second is pushed clear above the first, gap included.
        var sx = new float[3]; var sy = new float[3]; var sw = new float[3]; var sh = new float[3];
        sx[0] = 100f; sy[0] = 400f; sw[0] = 80f; sh[0] = 30f;
        sx[1] = 100f; sy[1] = 400f; sw[1] = 80f; sh[1] = 30f;
        Separate(sx, sy, sw, sh, 2, 2f, 800f);
        E(Near(sy[0], 400f), "the first box never moves");
        E(Near(sy[1], 400f - 30f - 2f), "an overlapping box is pushed clear above, gap included");

        sx[0] = 0f; sy[0] = 400f; sw[0] = 80f; sh[0] = 30f;
        sx[1] = 300f; sy[1] = 400f; sw[1] = 80f; sh[1] = 30f;
        Separate(sx, sy, sw, sh, 2, 2f, 800f);
        E(Near(sy[1], 400f), "a box that does not overlap is left alone");

        // The pushed box would leave the top of the screen: it re-clamps into the viewport instead.
        sx[0] = 0f; sy[0] = 0f; sw[0] = 80f; sh[0] = 30f;
        sx[1] = 0f; sy[1] = 0f; sw[1] = 80f; sh[1] = 30f;
        Separate(sx, sy, sw, sh, 2, 2f, 800f);
        E(sy[1] == 0f, "a push past the top edge clamps back on-screen instead of vanishing");
        sx[0] = 0f; sy[0] = 780f; sw[0] = 80f; sh[0] = 30f;
        sx[1] = 0f; sy[1] = 780f; sw[1] = 80f; sh[1] = 30f;
        Separate(sx, sy, sw, sh, 2, 2f, 20f);
        E(sy[1] == 0f, "a box taller than the viewport pins to the top edge");
        sx[0] = 0f; sy[0] = 0f; sw[0] = 80f; sh[0] = 30f;
        sx[1] = 0f; sy[1] = 0f; sw[1] = 80f; sh[1] = 30f;
        Separate(sx, sy, sw, sh, 2, 2f, 0f);
        E(Near(sy[1], -32f), "an unknown viewport leaves the push unclamped");

        // ---- Separate: the order list picks the participants AND the stacking sequence ----
        var so = new int[3];
        sx[0] = 0f; sy[0] = 400f; sw[0] = 80f; sh[0] = 30f;
        sx[1] = 0f; sy[1] = 400f; sw[1] = 80f; sh[1] = 30f;
        sx[2] = 0f; sy[2] = 400f; sw[2] = 80f; sh[2] = 30f;
        // Box 1 sits where box 0 lands once pushed: if it took part, box 0 would be shoved to 328 instead.
        sy[1] = 360f;
        so[0] = 2; so[1] = 0;                                   // box 1 is not in the list at all
        Separate(sx, sy, sw, sh, so, 2, 2f, 800f);
        E(Near(sy[2], 400f), "the first index in the order list keeps its placed position");
        E(Near(sy[0], 368f), "a later index is pushed above the earlier one, not above its array neighbour");
        E(Near(sy[1], 360f), "a box left out of the order list is never pushed");
        E(Near(sy[0], 368f), "and it never pushes either -- an excluded box is simply not there");

        // ---- OrderByRecency ----
        var okey = new long[4]; var oid = new uint[4]; var oin = new bool[4]; var ord = new int[4];
        okey[0] = 100; okey[1] = 300; okey[2] = 200; okey[3] = 50;
        oid[0] = 1; oid[1] = 2; oid[2] = 3; oid[3] = 4;
        for (int i = 0; i < 4; i++) oin[i] = true;
        int on = OrderByRecency(ord, okey, oid, oin, 4);
        E(on == 4 && ord[0] == 1 && ord[1] == 2 && ord[2] == 0 && ord[3] == 3,
          "interleaved timestamps land newest-first");
        oin[2] = false;
        on = OrderByRecency(ord, okey, oid, oin, 4);
        E(on == 3 && ord[0] == 1 && ord[1] == 0 && ord[2] == 3, "an excluded box is absent and off the count");
        oin[2] = true;

        okey[0] = okey[1] = okey[2] = okey[3] = 777;
        on = OrderByRecency(ord, okey, oid, oin, 4);
        E(ord[0] == 0 && ord[1] == 1 && ord[2] == 2 && ord[3] == 3, "equal timestamps tie-break on ascending id");
        int a0 = ord[0], a1 = ord[1], a2 = ord[2], a3 = ord[3];
        OrderByRecency(ord, okey, oid, oin, 4);
        E(ord[0] == a0 && ord[1] == a1 && ord[2] == a2 && ord[3] == a3,
          "the same input orders identically twice -- an equal-timestamp stack cannot flicker");

        okey[2] = long.MaxValue;
        on = OrderByRecency(ord, okey, oid, oin, 4);
        E(ord[0] == 2, "a plate's 'now' sentinel sorts ahead of every real timestamp");
        E(OrderByRecency(ord, okey, oid, oin, 0) == 0, "no boxes is an empty order list, not a fault");

        // ---- AbsorbPush ----
        float yo = 0f, lp = 0f;
        AbsorbPush(ref yo, ref lp, 0f);
        E(yo == 0f && lp == 0f, "a box that was never pushed absorbs nothing");
        AbsorbPush(ref yo, ref lp, -32f);
        E(Near(yo, 32f) && Near(lp, -32f), "a new push is cancelled by the residual, so the box does not jump");
        E(Near(400f + -32f + yo, 400f), "and the drawn position is exactly where it was before the push");
        AbsorbPush(ref yo, ref lp, -32f);
        E(Near(yo, 32f), "an unchanged push leaves the residual for SettleOffset to close");
        AbsorbPush(ref yo, ref lp, 0f);
        E(yo == 0f && lp == 0f, "losing the push absorbs back the other way");

        // ---- Bands + reaction ----
        E(BandText(0) == "Uninjured" && BandText(5) == "Dead", "band table ends are the spec strings");
        E(BandText(2) == "Injured", "band table middle is the spec string");
        E(BandText(-1) == "Uninjured" && BandText(99) == "Dead", "out-of-range band index clamps");
        E(ReferenceEquals(BandText(1), BandText(1)), "band text is a cached string, not built per call");
        var b0 = BandColor(0);
        E(Near(b0.r, 128f / 255f) && Near(b0.g, 1f) && Near(b0.b, 128f / 255f), "band 0 colour");
        var b4 = BandColor(4);
        E(Near(b4.r, 1f) && Near(b4.g, 80f / 255f) && Near(b4.b, 64f / 255f), "band 4 colour");
        var b5 = BandColor(5);
        E(Near(b5.r, 128f / 255f) && Near(b5.g, 128f / 255f) && Near(b5.b, 128f / 255f), "band 5 colour");
        E(BandColor(-3) == BandColor(0) && BandColor(42) == BandColor(5), "out-of-range band colour clamps");

        var friend = ReactionColor(1);
        E(Near(friend.r, 128f / 255f) && Near(friend.g, 1f) && Near(friend.b, 128f / 255f), "reaction 1 is green");
        var enemy = ReactionColor(2);
        E(Near(enemy.r, 1f) && Near(enemy.g, 80f / 255f) && Near(enemy.b, 64f / 255f), "reaction 2 is red");
        E(ReactionColor(0) == ReactionColor(200), "every other reaction is the same neutral");
        var neutral = ReactionColor(0);
        E(Near(neutral.b, 1f) && Near(neutral.r, 128f / 255f), "neutral is blue");

        log.Info(ok ? "PlateLayout.SelfCheck: passed" : "PlateLayout.SelfCheck: FAILED");
    }
}
