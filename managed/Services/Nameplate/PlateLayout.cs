using Sowx.Framework;
using Sowx.Ui;

namespace Sowx.Services.Nameplate;

/// <summary>One plate's laid-out boxes, in the same screen-px space the caller draws in. Sizes come out of
/// <see cref="PlateLayout.Measure"/>: the glyph metrics handed in already carry <see cref="Metrics.Scale"/>
/// (the atlas is baked at the effective px), so only the chrome constants are scaled -- do NOT scale these
/// again. All coordinates are relative to the plate's top-left.</summary>
public readonly struct PlateMetrics
{
    public readonly float W, H, PortraitX, PortraitY, PortraitSize, NameX, NameY, BandX, BandY, BarX, BarY, BarW, BarH;
    /// <summary>Width of the text column (the widest of name and band). The span a centred line is centred
    /// in -- never the whole box, which would slide the text under the portrait.</summary>
    public readonly float TextW;

    public PlateMetrics(float w, float h, float portraitX, float portraitY, float portraitSize,
                        float nameX, float nameY, float bandX, float bandY,
                        float barX, float barY, float barW, float barH, float textW)
    {
        W = w; H = h; PortraitX = portraitX; PortraitY = portraitY; PortraitSize = portraitSize;
        NameX = nameX; NameY = nameY; BandX = bandX; BandY = bandY;
        BarX = barX; BarY = barY; BarW = barW; BarH = barH; TextW = textW;
    }
}

/// <summary>Pure layout + fade math for the overhead plate and bubble. No GL, no native reads, and no
/// allocation on the frame: every method here but one is called per entry per frame from the hot draw, and the
/// string/colour tables are static. The exception is <see cref="BreakName"/>, which runs COLD (once per name
/// resolve, on the hook thread) and is documented as such.</summary>
public static class PlateLayout
{
    /// <summary>Inner padding around the plate content, design px.</summary>
    private const float PadPx = 4f;
    /// <summary>Gap between the portrait and the text column, and above the health bar. Design px.</summary>
    private const float GapPx = 4f;
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
    /// a third does not. The portrait is TOP-aligned at the pad in every case -- it belongs to the first line,
    /// and any other anchor would drift with the line count.</para></summary>
    public static PlateMetrics Measure(float nameW, int nameLines, float bandW, bool portrait, bool band,
                                       bool bar, float lineH, float scale)
    {
        float pad = PadPx * scale, gap = GapPx * scale, barH = BarHPx * scale;
        if (nameLines < 1) nameLines = 1;

        float textW = nameW;
        if (band && bandW > textW) textW = bandW;
        if (textW < 0f) textW = 0f;

        float nameH = lineH * nameLines;
        float below = (band ? lineH : 0f) + (bar ? gap + barH : 0f);
        float contentH = nameH + below;

        int portraitLines = nameLines < PortraitMaxLines ? nameLines : PortraitMaxLines;
        float portraitSize = portrait ? portraitLines * lineH + below : 0f;
        float portraitW = portraitSize * PortraitAspect;
        float colX = pad + (portrait ? portraitW + gap : 0f);

        float w = colX + textW + pad;
        float h = contentH + pad * 2f;

        float bandY = pad + nameH;
        float barY = pad + nameH + (band ? lineH : 0f) + gap;

        return new PlateMetrics(w, h,
            pad, pad, portraitSize,
            colX, pad,
            colX, bandY,
            colX, barY, bar ? textW : 0f, bar ? barH : 0f, textW);
    }

    /// <summary>NAME lines a portrait may grow with (the band and bar are always added on top). Past this a
    /// longer name grows the box downward and the portrait stays put, top-aligned with the first line.</summary>
    public const int PortraitMaxLines = 2;

    /// <summary>Left edge of one text line inside a column of <paramref name="textW"/> starting at
    /// <paramref name="colX"/>: centred when <paramref name="centre"/>, else the column start. A line wider
    /// than the column pins to the start rather than hanging off the left.</summary>
    public static float LineX(float colX, float textW, float lineW, bool centre)
    {
        if (!centre) return colX;
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
    public static string BreakName(string s)
    {
        if (!NeedsBreak(s)) return s;

        var sb = new System.Text.StringBuilder(s.Length);
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

            if (any)
            {
                if (brk || spaces >= SpacesPerBreak) sb.Append('\n');
                else for (int k = 0; k < spaces; k++) sb.Append(' ');
            }
            spaces = 0; brk = false;
            sb.Append(c); any = true;
        }
        return sb.ToString();
    }

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
    /// pushed plate can never walk off the top of the screen. Boxes are visited in the order the table produced,
    /// so the result is stable frame to frame. Hot: in-place over the caller's arrays, no allocation.
    /// <para>ponytail: single pass, vertical only, and the viewport clamp can re-introduce the overlap it just
    /// resolved in a dense crowd. The upgrade path is the engine's own shape -- iterate to a fixed point, and pad
    /// horizontally by the portrait width in mode 2.</para></summary>
    /// <param name="vpH">viewport height; &lt;= 0 means "unknown" and leaves the push unclamped.</param>
    public static void Separate(float[] x, float[] y, float[] w, float[] h, int count, float gap, float vpH)
    {
        for (int i = 1; i < count; i++)
        {
            for (int j = 0; j < i; j++)
            {
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
        E(Near(m.PortraitSize * 0.64f, m.NameX - 8f), "and keeps the 0.64 aspect at that height");
        E(Near(m.NameX, 4f + m.PortraitSize * 0.64f + 4f), "name column clears the portrait");
        E(Near(m.BandX, m.NameX) && Near(m.BandY, 4f + 18f), "band sits on the second line, same column");
        E(Near(m.BarW, 80f) && Near(m.BarH, 3f), "bar spans the text column at the bar thickness");
        E(Near(m.BarY, 4f + 18f + 18f + 4f), "bar sits under the band");
        E(Near(m.H, m.PortraitSize + 8f), "height = content + both pads");
        E(Near(m.PortraitY, 4f), "the portrait is top-aligned, with the first line");
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
        E(Near(m3.NameX, 4f + m3.PortraitSize * 0.64f + 4f), "the text column clears the WIDER portrait");
        E(Near(m3.W - m.W, m3.NameX - m.NameX), "and the box widens by exactly that, nothing else moving");
        E(Near(m3.BandY, 4f + 18f * 3f), "the band follows the LAST name line");
        E(Near(m3.BarY, m3.BandY + 18f + 4f), "and the bar follows the band");
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
        E(Near(p1.NameX, 4f + 18f * 0.64f + 4f) && Near(p2.NameX, 4f + 36f * 0.64f + 4f)
          && Near(p5.NameX, p2.NameX), "the text column clears the portrait at every height, at the 0.64 aspect");
        E(p1.PortraitY == p2.PortraitY && p2.PortraitY == p5.PortraitY,
          "the portrait's top edge does not move with the line count");

        // ---- LineX: creature plates left-align, everything else centres per line ----
        E(Near(LineX(20f, 100f, 40f, centre: false), 20f), "left alignment puts every line at the column start");
        E(Near(LineX(20f, 100f, 40f, centre: true), 50f), "a centred line is inset by half the slack");
        E(Near(LineX(20f, 100f, 100f, centre: true), 20f), "the widest line still starts at the column");
        E(Near(LineX(20f, 100f, 140f, centre: true), 20f), "an over-wide line pins to the column, never left of it");
        float shortX = LineX(20f, 100f, 30f, centre: true), longX = LineX(20f, 100f, 90f, centre: true);
        E(shortX > longX, "a short line sits further right than a long one");
        E(shortX >= 20f && shortX + 30f <= 120f && longX + 90f <= 120f, "both stay inside the text column");

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
