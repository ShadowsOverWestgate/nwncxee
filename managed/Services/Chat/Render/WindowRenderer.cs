using System.Collections.Generic;
using System.Text;
using Sowx.Services.Chat.Core;

namespace Sowx.Services.Chat.Render;

/// <summary>A speaker-portrait draw request: a pixel rect, the resref to load it from, and the line's
/// message time (so the portrait fades with its text). Emitted cold by <see cref="WindowRenderer"/>
/// (strings + floats only, no native refs); the GL side (<see cref="TextRenderer"/>) resolves the resref
/// to an engine-resident texture and draws it in a second pass (#26), applying the fade+slide (map #13).</summary>
public readonly record struct PortraitQuad(float X, float Y, float W, float H, string Resref, System.DateTime Ts);

/// <summary>The vertex range [<see cref="Start"/>,<see cref="End"/>) (vertex indices) of one visible chat
/// line's body geometry, plus its message time. The draw walks these to apply the per-line fade and the
/// shared slide/scroll residual (map #13).</summary>
public readonly record struct LineSpanVerts(int Start, int End, System.DateTime Ts);

/// <summary>One built chatbox frame: the interleaved verts + the metadata the animated draw needs. The
/// body slice [<see cref="BodyStart"/>,<see cref="BodyEnd"/>) is the only part that fades/slides and is
/// clipped to the body rect; everything outside it is static chrome. Built cold, drawn hot.</summary>
public sealed class RenderFrame
{
    public float[] Verts = System.Array.Empty<float>();
    public int Count;
    public List<PortraitQuad> Portraits = new();
    public List<LineSpanVerts> Spans = new();
    public int BodyStart, BodyEnd;    // vertex indices bounding the animated body slice
    public float Bx, By, Bw, Bh;      // body rect (px, y-down) -- the scissor for the body draw
    public bool Scrolled;             // active tab scrolled up (ScrollPx != 0): freeze the fade -- reading history
}

/// <summary>
/// Builds one <see cref="VertexBatch"/> for a <see cref="ChatWindow"/> each time something changes
/// (ingest / input / resize -- all cold threads), which <see cref="TextRenderer.Publish"/> then draws.
/// The message body is assembled through an ordered array of stateless line components gated by the
/// window's toggle bitset (architecture #17): a new visual feature = a new component + a new bit.
/// Only the rows that fit the body are walked/wrapped, so a rebuild is cheap even per keystroke.
/// </summary>
public static class WindowRenderer
{
    // Chrome colours (r,g,b,a). Themeable ones are refreshed from ChatConfig by ApplyPalette; the rest are
    // fixed (not surfaced in the settings UI). Text is a uint RGB. Defaults live in Defaults (RGBA8, one
    // source of truth for both the render fields and the picker UI).
    private static (float r, float g, float b, float a) Bg, TabBar, TabActive, Footer, SelBg;
    private static uint TabTextRgb;
    private static readonly (float r, float g, float b, float a) SbTrack = (0.04f, 0.05f, 0.07f, 0.55f);
    private const uint CaretRgb = 0xE8E8E8, EditRgb = 0xF0F0F0;

    /// <summary>Draw a 1px dark halo behind body glyphs (readability over the 3D world). Mirrored from the
    /// (cold) config by the shell. ponytail: 8-way offset copies in the cold batch; an SDF/shader outline is
    /// the ceiling if the vertex count ever bites.</summary>
    public static bool TextOutline;
    /// <summary>8-way (±1px) offsets for the outline halo, as flat (dx,dy) pairs.</summary>
    private static readonly float[] OutlineOffsets = { -1, -1, 0, -1, 1, -1, -1, 0, 1, 0, -1, 1, 0, 1, 1, 1 };

    /// <summary>Default colours (RGBA8, <c>0xRRGGBBAA</c>) for the themeable keys. The one source of truth
    /// for both the render fields (via <see cref="ApplyPalette"/>) and the picker UI (via <see cref="CurrentColor"/>).</summary>
    public static readonly Dictionary<string, uint> Defaults = new()
    {
        [ChatColor.Background] = 0x0d0f17bd, [ChatColor.Header] = 0x1a1f29d9, [ChatColor.Accent] = 0x335785f2,
        [ChatColor.Input] = 0x050508e6, [ChatColor.Selection] = 0x38669eb3, [ChatColor.Text] = 0xd8dee9ff,
    };

    private static (float r, float g, float b, float a) Unpack(uint rgba)
        => (((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);

    /// <summary>The effective colour for a key (RGBA8): the config override, else the default.</summary>
    public static uint CurrentColor(ChatConfig cfg, string key)
        => cfg.Colors.TryGetValue(key, out uint v) ? v : Defaults[key];

    /// <summary>Refresh the render palette from the (cold) config. Called on load and on any colour change;
    /// the draw path only ever reads the resolved fields, never the config.</summary>
    public static void ApplyPalette(ChatConfig cfg)
    {
        Bg = Unpack(CurrentColor(cfg, ChatColor.Background));
        TabBar = Unpack(CurrentColor(cfg, ChatColor.Header));
        TabActive = Unpack(CurrentColor(cfg, ChatColor.Accent));
        Footer = Unpack(CurrentColor(cfg, ChatColor.Input));
        SelBg = Unpack(CurrentColor(cfg, ChatColor.Selection));
        TabTextRgb = CurrentColor(cfg, ChatColor.Text) >> 8;   // text draws opaque RGB; the picker omits Text's A (see ChatColor.Editable)
    }

    // Palette starts at defaults so the renderer is valid before the shell loads a config.
    static WindowRenderer() => ApplyPalette(new ChatConfig());

    /// <summary>The render pipeline: components run in array order; the window's toggle bitset selects which.</summary>
    private static readonly ILineComponent[] Pipeline = { new TimestampComponent(), new MessageBodyComponent() };

    private const uint GripRgb = 0xB8C0CC;
    private const uint GearRgb = 0xB8C0CC, GearActiveRgb = 0xEAF0F8;
    /// <summary>Options-panel checkbox: outer frame + the fill drawn when the option is on.</summary>
    private const float CheckSize = 12f;
    private static readonly (float r, float g, float b, float a) CheckFrame = (0.55f, 0.55f, 0.55f, 1f);

    /// <summary>Portrait width:height. The engine's own portrait pipeline (ConvertPortrait) crops every
    /// portrait to this ratio (0.64 -&gt; taller than wide), so a gutter icon drawn at it is undistorted.</summary>
    private const float PortraitAspect = 0.64f;

    /// <summary>Portrait border, echoing vanilla's framed reply-portrait. A filled rect drawn behind the
    /// portrait (in the chrome batch) so its outer <see cref="PortraitFrameW"/> px show as a ring.</summary>
    private const float PortraitFrameW = 1f;
    private static readonly (float r, float g, float b, float a) PortraitFrame = (0.58f, 0.58f, 0.58f, 1f);

    /// <summary>Vertical gap between messages -- breathing room, and it keeps adjacent portrait frames from
    /// touching (which read as one continuous outline). ponytail: fixed; a density setting is the ceiling.</summary>
    public const float MsgGap = 8f;

    /// <summary>Breathing room below the newest message so its block (notably a portrait's 1px bottom frame)
    /// clears the body's bottom scissor edge instead of being clipped flush against it.</summary>
    public const float BottomPad = 4f;

    /// <summary>Assemble a chatbox frame. <paramref name="showFooter"/> draws the edit line (own-input,
    /// typing, and the tab has a send channel); <paramref name="showGrip"/> draws the corner resize grip.
    /// Returns the verts, the body slice bounds, and the per-line spans the animated draw needs.</summary>
    public static RenderFrame Build(ChatWindow win, SettingsWindow settings, ChatStore store, GlyphAtlas atlas,
                                    bool showFooter, string editText, int caret, bool showGrip,
                                    (int start, int end)? selection = null)
    {
        var b = new VertexBatch(atlas.SolidU, atlas.SolidV);
        var portraits = new List<PortraitQuad>();
        var spans = new List<LineSpanVerts>();
        float lh = atlas.LineHeight;
        float glyphBaseline = atlas.Ascent;             // baseline drop from a row's top
        float descentGap = lh - atlas.Ascent;

        // Panel + tab bar.
        b.Rect(win.X, win.Y, win.X + win.W, win.Y + win.H, Bg.r, Bg.g, Bg.b, Bg.a);
        b.Rect(win.X, win.Y, win.X + win.W, win.Y + ChatWindow.TabBarH, TabBar.r, TabBar.g, TabBar.b, TabBar.a);
        for (int i = 0; i < win.Tabs.Length; i++)
        {
            float tx = win.X + i * ChatWindow.TabW;
            if (i == win.ActiveTab)
                b.Rect(tx, win.Y, tx + ChatWindow.TabW, win.Y + ChatWindow.TabBarH,
                       TabActive.r, TabActive.g, TabActive.b, TabActive.a);
            DrawString(b, atlas, win.Tabs[i].Label, tx + ChatWindow.Pad,
                       win.Y + descentGap + glyphBaseline, TabTextRgb, tx + ChatWindow.TabW - ChatWindow.Pad);
        }

        // Body: one pixel-scrolled column. ScrollPx (px up from the content bottom) drives the layout, so
        // scrolling is smooth rather than message-quantised; partial messages at the top/bottom edges are
        // emitted and clipped by the body scissor. Two passes: pass 1 measures EVERY match to get the content
        // height -- needed to clamp the scroll and size the scrollbar BEFORE placing, so an over-scroll can't
        // leave a stale gap-at-top frame -- then pass 2 re-wraps only the blocks that intersect the viewport.
        win.BodyRect(out float bx, out float by, out float bw, out float bh);
        // Portraits keep the engine's portrait aspect (taller than wide) in a left gutter, only on lines with a
        // resref. A portrait line is one BLOCK, its height padded up to the portrait so it never overlaps its
        // neighbours. Suppressed while the dropdown is open (it draws last over the body; a live icon after it
        // would punch through the open list). The gutter is reserved on resref, before the texture resolves, so
        // the icon appears a frame later WITHOUT the text reflowing.
        // Portraits draw in a second pass over the body (before the tail chrome). The channel dropdown draws
        // INTO the body region, so suppress portraits under it -- else a portrait punches through the list. The
        // settings panel is a tail-drawn floating window (opaque, movable, usually off the body), so it covers
        // portraits normally; don't suppress them for it (they'd vanish even when the panel sits elsewhere).
        bool portraitsOn = (win.Toggles & ChatToggle.Portrait) != 0
                           && !(showFooter && win.ChannelListOpen);
        float portraitW = lh;                       // gutter-width icon...
        float portraitH = lh / PortraitAspect;      // ...grown tall to the real portrait shape (~1.56 rows)
        float gutter = portraitW + ChatWindow.Pad;
        // Reserve the scrollbar column unconditionally (even when the content fits and no track draws): the
        // text width stays stable, so lines don't reflow the moment history grows past one screen.
        float maxTextW = bw - 2 * ChatWindow.Pad - ChatWindow.ScrollbarW;

        int matches = store.CountMatching(win.Active.ChannelMask, win.Active.PlayerId);
        var all = new ChatLine[matches];
        int nAll = store.View(win.Active.ChannelMask, win.Active.PlayerId, all);   // newest-first, every match
        var runes = new List<Rune>();
        var cols = new List<uint>();

        // Pass 1: wrap every match to a block height; sum -> contentH (a gap between messages).
        // ponytail: re-wraps the whole (<=512-line) backlog each cold rebuild, even per keystroke. Cheap at
        // this cap; cache by (line, width) keyed on a store version if a deeper backlog ever makes it show.
        var blockH = new float[nAll];
        float contentH = 0f;
        for (int li = 0; li < nAll; li++)
        {
            bool hasP = BuildRunes(win, all[li], portraitsOn, runes, cols);
            float indent = hasP ? gutter : 0f;
            List<TextWrapper.LineSpan> wr = TextWrapper.WrapRunes(runes, maxTextW - indent, atlas.AdvanceOf);
            blockH[li] = hasP ? System.Math.Max(wr.Count * lh, portraitH) : wr.Count * lh;
            contentH += blockH[li];
        }
        if (nAll > 1) contentH += (nAll - 1) * MsgGap;
        contentH += BottomPad;   // the same breathing room at the top when fully scrolled up (symmetric to the
                                 // newest); folded into contentH so maxScroll/clamp/thumb all stay consistent

        // Clamp the scroll to the measured content, then hand the metrics to the window (scrollbar + drag map).
        float maxScroll = contentH - bh > 0f ? contentH - bh : 0f;
        if (win.Active.ScrollPx > maxScroll) win.Active.ScrollPx = maxScroll;
        if (win.Active.ScrollPx < 0f) win.Active.ScrollPx = 0f;
        win.ContentH = contentH; win.ViewH = bh;

        // Pass 2: place from the bottom (newest block's bottom = body bottom, pushed down by ScrollPx) and emit
        // blocks intersecting the viewport PLUS a margin above and below, so content sliding out under the
        // scroll/slide residual has geometry to slide on instead of popping. The margin equals the residual
        // cap, so a bounded residual can never expose an un-emitted block. The body scissor clips it all.
        float easeMargin = ChatAnim.MaxResidualPx;
        int bodyStart = b.VertexCount;   // the animated (fade/slide/scroll) slice starts here
        float rowBottom = by + bh - BottomPad + win.Active.ScrollPx;
        for (int li = 0; li < nAll; li++)
        {
            float top = rowBottom - blockH[li];
            if (top < by + bh + easeMargin && rowBottom > by - easeMargin)   // viewport + ease margin
            {
                int spanStart = b.VertexCount;   // this line's verts (rows + portrait frame) -> one fade unit
                bool hasPortrait = BuildRunes(win, all[li], portraitsOn, runes, cols);
                float indent = hasPortrait ? gutter : 0f;
                float rowX = bx + ChatWindow.Pad + indent;
                List<TextWrapper.LineSpan> wraps = TextWrapper.WrapRunes(runes, maxTextW - indent, atlas.AdvanceOf);
                for (int w = 0; w < wraps.Count; w++)   // emit every row; the scissor clips the ones off-edge
                    EmitRow(b, atlas, runes, cols, wraps[w], rowX, top + w * lh + glyphBaseline);
                if (hasPortrait)
                {
                    float px = bx + ChatWindow.Pad, py = top;
                    b.Rect(px - PortraitFrameW, py - PortraitFrameW, px + portraitW + PortraitFrameW, py + portraitH + PortraitFrameW,
                           PortraitFrame.r, PortraitFrame.g, PortraitFrame.b, PortraitFrame.a);   // frame behind; portrait covers the centre
                    portraits.Add(new PortraitQuad(px, py, portraitW, portraitH, all[li].PortraitResref, all[li].Timestamp));
                }
                if (b.VertexCount > spanStart) spans.Add(new LineSpanVerts(spanStart, b.VertexCount, all[li].Timestamp));
            }
            rowBottom = top - MsgGap;              // gap before the next (older) message
            if (rowBottom <= by - easeMargin) break;   // past the top margin -> older blocks are all off it
        }
        int bodyEnd = b.VertexCount;   // ...and ends here; chrome (scrollbar/footer/grip/dropdown) follows, unanimated

        // Scrollbar (chrome: after the body slice, so unfaded and unscissored): a track + an Accent-coloured
        // thumb whose size shows how much history there is and whose position shows where in it the view sits.
        // Only when the content overflows AND the pointer is over the window (or mid-scrub) -- it's a hover
        // affordance, kept up during a drag so the thumb doesn't vanish under the grabbing pointer.
        if (contentH > bh && (showGrip || win.ScrollDragging))
        {
            float sbx = bx + bw - ChatWindow.ScrollbarW;
            // Track stops one grip above the body bottom to match the hit zone (ChatWindow.SbTrackH): keeps
            // the thumb clear of the resize-grip corner.
            b.Rect(sbx, by, bx + bw, by + bh - ChatWindow.ResizeGrip, SbTrack.r, SbTrack.g, SbTrack.b, SbTrack.a);
            if (win.ThumbRect(out float tt, out float th))
                b.Rect(sbx, tt, bx + bw, tt + th, TabActive.r, TabActive.g, TabActive.b, TabActive.a);   // Accent
        }

        // Pop-out input strip: below the frame (#25), a channel dropdown at its left + a word-wrapped edit
        // line that grows the strip DOWNWARD one FooterH per wrapped row (so long input never clips).
        if (showFooter)
            EmitInputStrip(b, atlas, win, editText, caret, selection, descentGap + glyphBaseline);

        // Resize grip: three nested parallel diagonal lines in the bottom-right corner (the classic
        // "gripper"), shown while the pointer is over the window.
        if (showGrip)
        {
            float gr = ((GripRgb >> 16) & 0xFF) / 255f, gg = ((GripRgb >> 8) & 0xFF) / 255f, gb = (GripRgb & 0xFF) / 255f;
            float cx = win.X + win.W - 3f, cy = win.Y + win.H - 3f;   // inset so the outer line clears the border
            GripLine(b, cx, cy, 6f, gr, gg, gb);
            GripLine(b, cx, cy, 11f, gr, gg, gb);
            GripLine(b, cx, cy, 16f, gr, gg, gb);
        }

        // Gear icon (top-right of the tab bar): shown on hover, or while the settings window is open (so it
        // survives the pointer wandering off mid-interaction). A single ⚙ glyph, brighter while open.
        if (showGrip || settings.Open)
        {
            uint gc = settings.Open ? GearActiveRgb : GearRgb;
            DrawString(b, atlas, "⚙", win.X + win.W - ChatWindow.GearW + 4f,
                       win.Y + descentGap + glyphBaseline, gc, win.X + win.W);
        }

        // Channel dropdown list (open): stacked over the body (away from the strip), so it draws LAST = on top.
        if (showFooter && win.ChannelListOpen)
        {
            float fh = ChatWindow.FooterH;
            for (int i = 0; i < ChatWindow.SendChannels.Length; i++)
            {
                Channel ch = ChatWindow.SendChannels[i];
                float top = win.ChannelItemTop(i);
                var bg = ch == win.SendChannel ? TabActive : TabBar;
                b.Rect(win.X, top, win.X + ChatWindow.DropdownW, top + fh, bg.r, bg.g, bg.b, 0.98f);
                DrawString(b, atlas, ch.ToString(), win.X + ChatWindow.Pad,
                           top + descentGap + glyphBaseline, TabTextRgb, win.X + ChatWindow.DropdownW - 2);
            }
        }

        // Settings window (open): a titled floating panel with a checkbox row per option, drawn LAST so it
        // sits on top of everything. It grows with the option list (the point of it over the gear dropdown).
        if (settings.Open)
            EmitSettings(b, atlas, settings, descentGap + glyphBaseline);

        return new RenderFrame
        {
            Verts = b.ToArray(), Count = b.VertexCount, Portraits = portraits, Spans = spans,
            BodyStart = bodyStart, BodyEnd = bodyEnd, Bx = bx, By = by, Bw = bw, Bh = bh,
            Scrolled = win.Active.ScrollPx != 0f,
        };
    }

    /// <summary>Off-device proof of the portrait-gutter emission: an icon quad is produced for a line that
    /// carries a resref (anchored to its top visual row, in the left gutter) and none for a line without.
    /// Uses a stub atlas (zero-advance glyphs -&gt; one wrap row); real wrapping needs the native bake.</summary>
    public static void SelfCheck(Sowx.Framework.ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"WindowRenderer.SelfCheck FAIL: {w}"); } }

        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };   // descentGap = 5
        var win = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
        var settings = new SettingsWindow();
        var store = new ChatStore();
        store.Append(new ChatLine(1u, "", 0, "", new[] { new ColorRun("plain", 1, 1, 1) }, System.DateTime.UtcNow, "plain"));
        store.Append(new ChatLine(1u, "", 0, "po_x", new[] { new ColorRun("hi", 1, 1, 1) }, System.DateTime.UtcNow, "hi"));

        var portraits = Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Portraits;
        E(portraits.Count == 1, "exactly one portrait quad (only the resref-bearing line)");
        if (portraits.Count == 1)
        {
            PortraitQuad q = portraits[0];
            float ph = atlas.LineHeight / PortraitAspect;   // portrait grown to the real aspect
            E(q.Resref == "po_x", "quad carries the line's resref");
            E(q.X == ChatWindow.Pad, "quad sits in the left gutter (bx + Pad)");
            E(q.W == atlas.LineHeight, "quad keeps the gutter width");
            E(q.H == ph, "quad is grown tall to the portrait aspect");
            // newest line fills the lowest block: its top = body-bottom - BottomPad - block height (padded to the portrait).
            E(q.Y == (22 + 278) - BottomPad - ph, "quad anchored to the padded block's top");
        }

        // MeasureBlock (the ingest slide/hold distance): a portrait line is padded up to the portrait height;
        // a plain one-row line is one line high. Both drive the exact per-line compensation on ingest.
        float phh = atlas.LineHeight / PortraitAspect;
        var pLine = new ChatLine(1u, "", 0, "po_x", new[] { new ColorRun("hi", 1, 1, 1) }, System.DateTime.UtcNow, "hi");
        var tLine = new ChatLine(1u, "", 0, "", new[] { new ColorRun("plain", 1, 1, 1) }, System.DateTime.UtcNow, "plain");
        E(System.Math.Abs(MeasureBlock(win, pLine, atlas) - phh) < 1e-3, "MeasureBlock pads a portrait line to the portrait height");
        E(System.Math.Abs(MeasureBlock(win, tLine, atlas) - atlas.LineHeight) < 1e-3, "MeasureBlock of a one-row line is one line high");

        // Settings window open -> portraits still emit (the opaque panel is tail-drawn over them; suppressing
        // them made portraits vanish even when the movable panel sits elsewhere).
        settings.Open = true;
        E(Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Portraits.Count == 1,
          "settings window open does not suppress portraits");
        settings.Open = false;

        // Toggle off -> no icons even when a resref is present.
        win.Toggles &= ~ChatToggle.Portrait;
        var off = Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Portraits;
        E(off.Count == 0, "portrait toggle off suppresses the icon");

        log.Info(ok ? "WindowRenderer.SelfCheck: passed" : "WindowRenderer.SelfCheck: FAILED");
    }

    /// <summary>One diagonal grip line: a 45° segment (bottom-left to top-right) at corner-distance
    /// <paramref name="len"/>, drawn as a run of small squares. Three of these nested make the gripper.</summary>
    private static void GripLine(VertexBatch b, float cx, float cy, float len, float r, float g, float bl)
    {
        for (float t = 0f; t <= len; t += 2f)
        {
            float x = cx - len + t, y = cy - t;   // (cx-len,cy) -> (cx,cy-len)
            b.Rect(x, y - 2f, x + 2f, y, r, g, bl, 0.5f);
        }
    }

    /// <summary>Laid-out block height (px) of one message -- the same wrap + portrait padding a body block
    /// gets in <see cref="Build"/>. The ingest path uses it to slide/compensate by the exact new-line height
    /// (block + one <see cref="MsgGap"/>) without re-measuring the whole backlog, so it's correct even under
    /// ring eviction (the read position holds to the bottom-anchored growth, not the net content change).</summary>
    public static float MeasureBlock(ChatWindow win, ChatLine line, GlyphAtlas atlas)
    {
        bool portraitsOn = (win.Toggles & ChatToggle.Portrait) != 0;
        win.BodyRect(out _, out _, out float bw, out _);
        float maxTextW = bw - 2 * ChatWindow.Pad - ChatWindow.ScrollbarW;
        var runes = new List<Rune>();
        var cols = new List<uint>();
        bool hasP = BuildRunes(win, line, portraitsOn, runes, cols);
        float indent = hasP ? atlas.LineHeight + ChatWindow.Pad : 0f;
        float textH = TextWrapper.WrapRunes(runes, maxTextW - indent, atlas.AdvanceOf).Count * atlas.LineHeight;
        return hasP ? System.Math.Max(textH, atlas.LineHeight / PortraitAspect) : textH;
    }

    /// <summary>Run the toggle-gated line pipeline into <paramref name="runes"/>/<paramref name="cols"/>
    /// (cleared first). Returns whether the line should reserve a portrait gutter (portraits on + has resref).</summary>
    private static bool BuildRunes(ChatWindow win, ChatLine line, bool portraitsOn, List<Rune> runes, List<uint> cols)
    {
        runes.Clear(); cols.Clear();
        for (int c = 0; c < Pipeline.Length; c++)
            if ((win.Toggles & Pipeline[c].Bit) != 0) Pipeline[c].Contribute(line, runes, cols);
        return portraitsOn && !string.IsNullOrEmpty(line.PortraitResref);
    }

    /// <summary>Lay one wrapped row (a span into <paramref name="runes"/>) with per-rune colour.</summary>
    private static void EmitRow(VertexBatch b, GlyphAtlas atlas, List<Rune> runes, List<uint> cols,
                                TextWrapper.LineSpan span, float startX, float baseline)
    {
        float penX = startX;
        for (int k = span.Start; k < span.End; k++)
        {
            Rune r = runes[k];
            if (!atlas.TryGet(r.Value, out Glyph g)) { penX += atlas.AdvanceOf(r); continue; }
            uint c = cols[k];
            if (TextOutline)
                for (int o = 0; o < OutlineOffsets.Length; o += 2)
                    b.Glyph(g, penX + OutlineOffsets[o], baseline + OutlineOffsets[o + 1], 0f, 0f, 0f, 1f);   // dark halo behind
            b.Glyph(g, penX, baseline, ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f);
            penX += g.Advance;
        }
    }

    /// <summary>The word-wrapped input strip below the frame: a dropdown button (top row) + the edit text
    /// wrapped to the strip width, the strip growing downward one <see cref="ChatWindow.FooterH"/> per row.
    /// Caret and selection are mapped from UTF-16 indices into wrapped rune rows. <paramref name="baseDrop"/>
    /// is the baseline offset from a row's top.</summary>
    private static void EmitInputStrip(VertexBatch b, GlyphAtlas atlas, ChatWindow win, string editText,
                                       int caret, (int start, int end)? selection, float baseDrop)
    {
        float fh = ChatWindow.FooterH;
        float startX = win.X + ChatWindow.DropdownW + ChatWindow.Pad;   // edit line begins right of the dropdown
        float editW = win.X + win.W - ChatWindow.Pad - startX;          // wrap width for the input text

        var runes = new List<Rune>(editText.Length);
        foreach (Rune r in editText.EnumerateRunes()) runes.Add(r);
        List<TextWrapper.LineSpan> wraps = TextWrapper.WrapRunes(runes, editW, atlas.AdvanceOf);
        if (wraps.Count == 0) wraps.Add(new TextWrapper.LineSpan(0, 0));   // always at least one (empty) row
        int rows = wraps.Count;
        win.FooterRows = rows;   // set BEFORE reading StripTop: in above-mode StripTop depends on FooterHeight
        float fy = win.StripTop;

        // Strip background over all rows; dropdown button on the top row only.
        b.Rect(win.X, fy, win.X + win.W, fy + fh * rows, Footer.r, Footer.g, Footer.b, Footer.a);
        b.Rect(win.X, fy, win.X + ChatWindow.DropdownW, fy + fh, TabBar.r, TabBar.g, TabBar.b, TabBar.a);
        DrawString(b, atlas, win.SendChannel + " ▾", win.X + ChatWindow.Pad, fy + baseDrop, TabTextRgb,
                   win.X + ChatWindow.DropdownW - 2);

        int caretR = RuneIndex(runes, caret);
        (int selA, int selB) = selection is (int a, int c) ? (RuneIndex(runes, a), RuneIndex(runes, c)) : (-1, -1);

        for (int w = 0; w < rows; w++)
        {
            TextWrapper.LineSpan span = wraps[w];
            float ry = fy + fh * w;
            // Selection highlight for this row's slice of [selA,selB) -- drawn first, behind the glyphs.
            if (selA >= 0 && selB > span.Start && selA < span.End)
            {
                int rs = System.Math.Max(selA, span.Start), re = System.Math.Min(selB, span.End);
                float x0 = MeasureRunes(atlas, runes, span.Start, rs, startX);
                float x1 = MeasureRunes(atlas, runes, span.Start, re, startX);
                b.Rect(x0, ry + 3, x1, ry + fh - 3, SelBg.r, SelBg.g, SelBg.b, SelBg.a);
            }
            EmitRunes(b, atlas, runes, span, startX, ry + baseDrop, EditRgb);

            // Caret on this row? At a row's End it belongs to the NEXT row's start only on a contiguous
            // hard-break (no consumed space); when a space was consumed, End stays on this row.
            bool here = caretR >= span.Start && caretR <= span.End
                        && !(caretR == span.End && w < rows - 1 && wraps[w + 1].Start == span.End);
            if (here)
            {
                float cx = MeasureRunes(atlas, runes, span.Start, caretR, startX);
                b.Rect(cx, ry + 3, cx + 2, ry + fh - 3,
                       ((CaretRgb >> 16) & 0xFF) / 255f, ((CaretRgb >> 8) & 0xFF) / 255f, (CaretRgb & 0xFF) / 255f, 1f);
            }
        }
    }

    /// <summary>The settings window: a title bar ("Settings" + a × close button) over the typed rows the
    /// <see cref="SettingsWindow"/> laid out -- checkboxes, cycle buttons, sliders, and expandable RGBA colour
    /// pickers. Anchored + sized by the window; drawn last so it sits over everything. Reads the rows' current
    /// values (the core already resolved them from the config), so it never touches the config itself.</summary>
    private static void EmitSettings(VertexBatch b, GlyphAtlas atlas, SettingsWindow s, float baseDrop)
    {
        float x = s.X, y = s.Y, w = s.W;
        float vpad = (SettingsWindow.RowH - atlas.LineHeight) / 2f;   // vertically centre a label in its row

        // Panel body + title bar (opaque: it's a floating window that can sit over portraits/chat).
        b.Rect(x, y, x + w, y + s.H, Footer.r, Footer.g, Footer.b, 1f);
        b.Rect(x, y, x + w, y + SettingsWindow.TitleH, TabBar.r, TabBar.g, TabBar.b, 1f);
        DrawString(b, atlas, "Settings", x + SettingsWindow.Pad, y + baseDrop, TabTextRgb, x + w - SettingsWindow.CloseW);
        DrawString(b, atlas, "×", x + w - SettingsWindow.CloseW + 5f, y + baseDrop, GearRgb, x + w);

        foreach (SetRow r in s.Rows)
        {
            float labelX = x + SettingsWindow.Pad + (r.Channel >= 0 ? SettingsWindow.ChannelIndent : 0f);
            float labelClip = r.Kind == SetKind.Toggle ? x + w - SettingsWindow.CheckSize - SettingsWindow.Pad
                                                        : x + w * SettingsWindow.ControlX - 2f;
            DrawString(b, atlas, r.Label, labelX, r.Top + baseDrop + vpad, TabTextRgb, labelClip);

            switch (r.Kind)
            {
                case SetKind.Toggle:
                {
                    float cs = SettingsWindow.CheckSize;
                    float cbx = x + w - SettingsWindow.Pad - cs, cby = r.Top + (SettingsWindow.RowH - cs) / 2f;
                    b.Rect(cbx, cby, cbx + cs, cby + cs, CheckFrame.r, CheckFrame.g, CheckFrame.b, CheckFrame.a);
                    var fill = r.On ? TabActive : Footer;
                    b.Rect(cbx + 1, cby + 1, cbx + cs - 1, cby + cs - 1, fill.r, fill.g, fill.b, 1f);
                    break;
                }
                case SetKind.Cycle:
                {
                    float bx0 = x + w * SettingsWindow.ControlX, bx1 = x + w - SettingsWindow.Pad;
                    b.Rect(bx0, r.Top + 3, bx1, r.Top + SettingsWindow.RowH - 3, TabBar.r, TabBar.g, TabBar.b, TabBar.a);
                    DrawString(b, atlas, r.Value, bx0 + 4f, r.Top + baseDrop + vpad, TabTextRgb, bx1 - 2f);
                    break;
                }
                case SetKind.Slider:
                {
                    s.SliderTrack(r, out float sx0, out float sx1, out float cy);
                    b.Rect(sx0, cy - 2f, sx1, cy + 2f, SbTrack.r, SbTrack.g, SbTrack.b, SbTrack.a);
                    float knob = sx0 + r.Norm * (sx1 - sx0);
                    b.Rect(sx0, cy - 2f, knob, cy + 2f, TabActive.r, TabActive.g, TabActive.b, TabActive.a);
                    b.Rect(knob - 2f, r.Top + 4f, knob + 2f, r.Top + SettingsWindow.RowH - 4f,
                           ((GripRgb >> 16) & 0xFF) / 255f, ((GripRgb >> 8) & 0xFF) / 255f, (GripRgb & 0xFF) / 255f, 1f);
                    // Value box: the current number, or the live edit buffer + a caret while it's being typed.
                    s.ValueBox(r, out float vx0, out float vx1, out float vy0, out float vy1);
                    bool editing = s.EditingRow(r);
                    var vbg = editing ? TabActive : TabBar;
                    b.Rect(vx0, vy0, vx1, vy1, vbg.r, vbg.g, vbg.b, vbg.a);
                    string vtext = editing ? s.EditText : SettingsWindow.FormatValue(r);
                    float vend = DrawString(b, atlas, vtext, vx0 + 3f, r.Top + baseDrop + vpad, TabTextRgb, vx1 - 2f);
                    if (editing)
                        b.Rect(vend, vy0 + 2f, vend + 1.5f, vy1 - 2f,
                               ((CaretRgb >> 16) & 0xFF) / 255f, ((CaretRgb >> 8) & 0xFF) / 255f, (CaretRgb & 0xFF) / 255f, 1f);
                    break;
                }
                case SetKind.Swatch:
                {
                    float px0 = x + w - SettingsWindow.Pad - SettingsWindow.SwatchW, px1 = x + w - SettingsWindow.Pad;
                    float py0 = r.Top + 3f, py1 = r.Top + SettingsWindow.RowH - 3f;
                    b.Rect(px0 - 1f, py0 - 1f, px1 + 1f, py1 + 1f, CheckFrame.r, CheckFrame.g, CheckFrame.b, CheckFrame.a);
                    b.Rect(px0, py0, px1, py1, ((r.Rgba >> 24) & 0xFF) / 255f, ((r.Rgba >> 16) & 0xFF) / 255f,
                           ((r.Rgba >> 8) & 0xFF) / 255f, (r.Rgba & 0xFF) / 255f);
                    break;
                }
            }
        }
    }

    /// <summary>Map a UTF-16 index into a rune-list index (they diverge only past the BMP).</summary>
    private static int RuneIndex(List<Rune> runes, int utf16)
    {
        int u = 0;
        for (int k = 0; k < runes.Count; k++) { if (u >= utf16) return k; u += runes[k].Utf16SequenceLength; }
        return runes.Count;
    }

    /// <summary>Pen X after runes <paramref name="from"/>..<paramref name="to"/>, emitting nothing.</summary>
    private static float MeasureRunes(GlyphAtlas atlas, List<Rune> runes, int from, int to, float startX)
    {
        float x = startX;
        for (int k = from; k < to; k++) x += atlas.TryGet(runes[k].Value, out Glyph g) ? g.Advance : atlas.AdvanceOf(runes[k]);
        return x;
    }

    /// <summary>Draw one wrapped rune span in a single colour.</summary>
    private static void EmitRunes(VertexBatch b, GlyphAtlas atlas, List<Rune> runes, TextWrapper.LineSpan span,
                                  float startX, float baseline, uint rgb)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, bl = (rgb & 0xFF) / 255f;
        float penX = startX;
        for (int k = span.Start; k < span.End; k++)
        {
            if (atlas.TryGet(runes[k].Value, out Glyph gl)) { b.Glyph(gl, penX, baseline, r, g, bl, 1f); penX += gl.Advance; }
            else penX += atlas.AdvanceOf(runes[k]);
        }
    }

    /// <summary>Draw an unwrapped string, clipped at <paramref name="clipX"/>. When
    /// <paramref name="caretChar"/> >= 0, returns the pen X after that many chars (for the caret);
    /// otherwise returns the final pen X.</summary>
    private static float DrawString(VertexBatch b, GlyphAtlas atlas, string s, float startX, float baseline,
                                    uint rgb, float clipX, int caretChar = -1)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, bl = (rgb & 0xFF) / 255f;
        float penX = startX, caretX = startX;
        int chars = 0;
        foreach (Rune ru in s.EnumerateRunes())
        {
            if (caretChar >= 0 && chars <= caretChar) caretX = penX;   // pen just before the caret-th char
            if (penX >= clipX) break;
            if (atlas.TryGet(ru.Value, out Glyph gl)) { b.Glyph(gl, penX, baseline, r, g, bl, 1f); penX += gl.Advance; }
            else penX += atlas.AdvanceOf(ru);
            chars += ru.Utf16SequenceLength;
        }
        if (caretChar >= 0 && chars <= caretChar) caretX = penX;       // caret at end of string
        return caretChar >= 0 ? caretX : penX;
    }
}

/// <summary>A stateless render component: contributes glyphs+colours for one line when its
/// <see cref="Bit"/> is set in the window's toggle bitset. Extensibility bought with structure.</summary>
public interface ILineComponent
{
    uint Bit { get; }
    void Contribute(ChatLine line, List<Rune> runes, List<uint> cols);
}

/// <summary>Prepend a grey timestamp to the line, in the configured <see cref="TsFormat"/> and from the
/// configured <see cref="TsSource"/> (game time falls back to local when the line carries none). The style
/// is mirrored onto statics from the (cold) config so this stays a cheap, allocation-light hot component.</summary>
public sealed class TimestampComponent : ILineComponent
{
    private const uint Grey = 0x8A8A8A;

    /// <summary>Mirror of the config timestamp style; set by the shell on load/change, never read from config.</summary>
    public static TsFormat Format = TsFormat.HourMin;
    public static TsSource Source = TsSource.Local;

    public uint Bit => ChatToggle.Timestamp;

    public void Contribute(ChatLine line, List<Rune> runes, List<uint> cols)
    {
        string txt;
        if (Source == TsSource.Game && line.GameTime.Valid)
            txt = Stamp(line.GameTime.Hour, line.GameTime.Minute, line.GameTime.Second);
        else { System.DateTime lt = line.Timestamp.ToLocalTime(); txt = Stamp(lt.Hour, lt.Minute, lt.Second); }
        foreach (Rune r in txt.EnumerateRunes()) { runes.Add(r); cols.Add(Grey); }
    }

    /// <summary>Format one h/m/s (24-hour inputs) into the current preset, e.g. <c>[09:07] </c>.</summary>
    internal static string Stamp(int h, int m, int s) => Format switch
    {
        TsFormat.HourMinSec => $"[{h:D2}:{m:D2}:{s:D2}] ",
        TsFormat.Hour12 => $"[{(h % 12 == 0 ? 12 : h % 12)}:{m:D2} {(h < 12 ? "AM" : "PM")}] ",
        _ => $"[{h:D2}:{m:D2}] ",
    };
}

/// <summary>The message text itself, each colour run in its parsed RGB.</summary>
// ponytail: SenderName is the next text component -- it's "" per #18's deferral (the tap doesn't carry
// it). Portrait is not a text component: it's a texture, emitted as a PortraitQuad in the gutter (#26).
public sealed class MessageBodyComponent : ILineComponent
{
    public uint Bit => ChatToggle.MessageBody;
    public void Contribute(ChatLine line, List<Rune> runes, List<uint> cols)
    {
        foreach (ColorRun run in line.ColorRuns)
        {
            uint rgb = ((uint)(run.R * 255f) << 16) | ((uint)(run.G * 255f) << 8) | (uint)(run.B * 255f);
            foreach (Rune r in run.Text.EnumerateRunes()) { runes.Add(r); cols.Add(rgb); }
        }
    }
}
