using System.Collections.Generic;
using System.Text;
using CX.Services.Chat.Core;
using CX.Ui;
using static CX.Ui.PanelChrome;

using CX.Services.Chat.Core.Model;

namespace CX.Services.Chat.Render;

/// <summary>
/// Builds one <see cref="VertexBatch"/> for a <see cref="ChatWindow"/> each time something changes
/// (ingest / input / resize -- all cold threads), which <see cref="ChatRenderer.Publish(RenderFrame[])"/> then draws.
/// The message body is assembled through an ordered array of stateless line components gated by the
/// window's toggle bitset (architecture #17): a new visual feature = a new component + a new bit.
/// Only the rows that fit the body are walked/wrapped, so a rebuild is cheap even per keystroke.
/// </summary>
public static class WindowRenderer
{
    /// <summary>Per-window resolved palette, threaded through Build → EmitInputStrip.</summary>
    private struct Pal
    {
        public (float r, float g, float b, float a) Bg, Bg2, TabBar, TabActive, Footer, SelBg, Frame;
        public uint TabTextRgb, FrameRgb;
    }

    /// <summary>Per-thread reusable scratch for the ring walk (#53).</summary>
    [System.ThreadStatic] private static ChatLine[]? _ringScratch;
    private static ChatLine[] RingScratch() => _ringScratch ??= new ChatLine[ChatStore.Capacity];

    /// <summary>Per-thread pooled VertexBatch + rune/colour lists (#53 finding #4). The internal
    /// <c>List&lt;float&gt;</c> keeps its capacity across rebuilds, avoiding per-build heap churn.
    /// Portraits/spans/blockH escape into published frames and must NOT be pooled.</summary>
    [System.ThreadStatic] private static VertexBatch? _batchScratch;
    [System.ThreadStatic] private static List<Rune>? _runesScratch;
    [System.ThreadStatic] private static List<uint>? _colsScratch;

    private const uint EditRgb = 0xF0F0F0;
    /// <summary>Unread ephemeral tab: label + pulsing underline.</summary>
    private const uint UnreadRgb = Warn;
    /// <summary>Soft drop shadow outside the frame: rings of 1 px, outermost last, alpha scaled by the background's.</summary>
    private static readonly float[] ShadowAlpha = { 0.18f, 0.10f, 0.04f };
    /// <summary>Alpha of the hovered tab's Accent plate, relative to the colour's own (the active tab sits at 0.35).</summary>
    private const float HoverWash = 0.18f;
    /// <summary>Inset of the channel chip / thumb / caret inside their bands (px at scale 1).</summary>
    private const float Inset = 3f;

    /// <summary>8-way (±1px) offsets for the outline halo, as flat (dx,dy) pairs.</summary>
    private static readonly float[] OutlineOffsets = { -1, -1, 0, -1, 1, -1, -1, 0, 1, 0, -1, 1, 0, 1, 1, 1 };

    /// <summary>Default colours (RGBA8, <c>0xRRGGBBAA</c>) for the themeable keys. The one source of truth
    /// for both the render fields (via <see cref="ApplyPalette"/>) and the picker UI (via <see cref="CurrentColor"/>).</summary>
    public static readonly Dictionary<string, uint> Defaults = new()
    {
        [ChatColor.Background] = 0x0d0f17bd, [ChatColor.Background2] = 0x192040bd,
        [ChatColor.Frame] = (Border << 8) | 0xbd,
        [ChatColor.Header] = 0x1a1f29d9, [ChatColor.Accent] = 0x335785f2,
        [ChatColor.Input] = 0x050508e6, [ChatColor.Selection] = 0x38669eb3, [ChatColor.Text] = 0xd8dee9ff,
    };

    private static (float r, float g, float b, float a) Unpack(uint rgba)
        => (((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);

    /// <summary>The effective colour for a key (RGBA8): the per-window override, else the default.</summary>
    public static uint CurrentColor(ChatWindow win, string key)
        => win.Colors.TryGetValue(key, out uint v) ? v : Defaults[key];

    private static Pal ResolvePalette(ChatWindow win) => new()
    {
        Bg = Unpack(CurrentColor(win, ChatColor.Background)),
        Bg2 = Unpack(CurrentColor(win, ChatColor.Background2)),
        TabBar = Unpack(CurrentColor(win, ChatColor.Header)),
        TabActive = Unpack(CurrentColor(win, ChatColor.Accent)),
        Footer = Unpack(CurrentColor(win, ChatColor.Input)),
        SelBg = Unpack(CurrentColor(win, ChatColor.Selection)),
        TabTextRgb = CurrentColor(win, ChatColor.Text) >> 8,
        Frame = Unpack(CurrentColor(win, ChatColor.Frame)), FrameRgb = CurrentColor(win, ChatColor.Frame) >> 8,
    };


    /// <summary>The render pipeline: components run in array order; the window's toggle bitset selects which.</summary>
    private static readonly ILineComponent[] Pipeline = { new TimestampComponent(), new MessageBodyComponent() };

    private const uint GripRgb = Grip;

    /// <summary>Portrait width:height. The engine's own portrait pipeline (ConvertPortrait) crops every
    /// portrait to this ratio (0.64 -&gt; taller than wide), so a gutter icon drawn at it is undistorted.</summary>
    private const float PortraitAspect = 0.64f;

    /// <summary>Portrait border, echoing vanilla's framed reply-portrait. A filled rect drawn behind the
    /// portrait (in the chrome batch) so its outer <see cref="PortraitFrameW"/> px show as a ring.</summary>
    private const float PortraitFrameW = 1f;
    private static readonly (float r, float g, float b, float a) PortraitFrame = (0.58f, 0.58f, 0.58f, 1f);
    private static readonly (float r, float g, float b, float a) PortraitFrameHover = (1f, 1f, 1f, 1f);

    /// <summary>Vertical gap between messages -- breathing room, and it keeps adjacent portrait frames from
    /// touching (which read as one continuous outline). ponytail: fixed; a density setting is the ceiling.</summary>
    public const float MsgGap = 8f;

    /// <summary>Breathing room below the newest message so its block (notably a portrait's 1px bottom frame)
    /// clears the body's bottom scissor edge instead of being clipped flush against it.</summary>
    public const float BottomPad = 4f;

    /// <summary>Assemble a chatbox frame. <paramref name="atlas"/> is the face the body text draws in (the
    /// window's own, CX-683); <paramref name="chrome"/> the UI face the tab bar, buttons, input strip and channel
    /// list draw in (they are sized by <see cref="Metrics.Scale"/>, which follows the UI face) -- null = the body
    /// face. <paramref name="showFooter"/> draws the edit line (own-input, typing, and the tab has a send channel);
    /// <paramref name="showGrip"/> draws the corner resize grip.</summary>
    public static RenderFrame Build(ChatWindow win, SettingsWindow settings, ChatStore store, GlyphAtlas atlas,
                                    bool showFooter, string editText, int caret, bool showGrip,
                                    (int start, int end)? selection = null, GlyphAtlas? chrome = null)
    {
        var p = ResolvePalette(win);
        bool outline = win.Outline;
        chrome ??= atlas;
        float chromeBase = (ChatWindow.TabBarH - chrome.LineHeight) * 0.5f + chrome.Ascent;   // baseline drop that centres a line in a band (TabBarH == FooterH)

        VertexBatch b = _batchScratch ??= new VertexBatch(0, 0);
        b.Reset(atlas.SolidU, atlas.SolidV);
        var portraits = new List<PortraitQuad>();
        var hits = new List<PortraitHit>();
        var spans = new List<LineSpanVerts>();
        float lh = atlas.LineHeight;
        float glyphBaseline = atlas.Ascent;
        float descentGap = lh - atlas.Ascent;

        var tabs = win.Tabs;
        int activeTab = win.ActiveTab;
        if ((uint)activeTab >= (uint)tabs.Count) activeTab = 0;
        Tab active = tabs[activeTab];

        // Panel + tab bar. The tab bar hides when auto-hide is on and the pointer is outside the window
        // (and the settings panel is closed), leaving just the background in the header band.
        // Unread ephemeral tabs force the header visible so the blink is seen.
        // Frame: a soft shadow outside (alpha scaled by the frame colour's), the 1 px outline on the edge.
        bool hasUnread = false;
        for (int i = 0; i < tabs.Count; i++) if (tabs[i].Unread && tabs[i].Ephemeral) { hasUnread = true; break; }
        bool inside = showGrip || settings.Open || hasUnread;
        bool showHeader = !win.AutoHideHeader || inside, showFrame = !win.AutoHideFrame || inside;
        float fa = p.Frame.a;
        if (showFrame)
            for (int k = ShadowAlpha.Length; k >= 1; k--)
                Outline(b, win.X - k, win.Y - k, win.X + win.W + k, win.Y + win.H + k, 0x000000, ShadowAlpha[k - 1] * fa);
        EmitBackground(b, win, p.Bg, p.Bg2);
        int blinkStart = 0, blinkEnd = 0, hoverStart = 0, hoverEnd = 0, leftStart = 0, leftEnd = 0;
        if (showHeader)
        {
            float barBot = win.Y + ChatWindow.TabBarH;
            b.Rect(win.X, win.Y, win.X + win.W, barBot, p.TabBar.r, p.TabBar.g, p.TabBar.b, p.TabBar.a);
            Fill(b, win.X, barBot - 1f, win.X + win.W, barBot, p.FrameRgb, p.TabBar.a);
            float tabW = win.TabWFor(tabs.Count);
            float ul = 2f * Metrics.Scale;   // the active / unread underline
            for (int i = 0; i < tabs.Count; i++)
            {
                float tx = win.X + i * tabW;
                bool on = i == activeTab, eph = tabs[i].Ephemeral;
                if (on)
                {
                    b.Rect(tx, win.Y, tx + tabW, barBot - 1f, p.TabActive.r, p.TabActive.g, p.TabActive.b, p.TabActive.a * 0.35f);
                    b.Rect(tx + 1f, barBot - ul, tx + tabW - 1f, barBot, p.TabActive.r, p.TabActive.g, p.TabActive.b, p.TabActive.a);
                }
                else if (i == win.HoveredTab || i == win.LeftTab)
                {
                    // Hover plate: a lighter Accent wash the draw eases in (entered) or out (left).
                    if (i == win.HoveredTab) hoverStart = b.VertexCount; else leftStart = b.VertexCount;
                    b.Rect(tx, win.Y, tx + tabW, barBot - 1f, p.TabActive.r, p.TabActive.g, p.TabActive.b, p.TabActive.a * HoverWash);
                    if (i == win.HoveredTab) hoverEnd = b.VertexCount; else leftEnd = b.VertexCount;
                }
                // Unread ephemeral tab: gold label + an underline whose alpha the draw pulses.
                bool unread = eph && tabs[i].Unread;
                if (unread)
                {
                    if (blinkStart == 0) blinkStart = b.VertexCount;
                    Fill(b, tx + 1f, barBot - ul, tx + tabW - 1f, barBot, UnreadRgb);
                    blinkEnd = b.VertexCount;
                }
                bool closeX = eph && tabs.Count > 1;
                float btns = (closeX ? ChatWindow.TabBtnW : 0f) + (eph ? ChatWindow.TabBtnW : 0f);
                float avail = tabW - 2f * ChatWindow.Pad - btns;
                float lw = UiText.Measure(chrome, tabs[i].Label);
                float lx = lw < avail ? tx + ChatWindow.Pad + (avail - lw) * 0.5f : tx + ChatWindow.Pad;   // centred when it fits
                UiText.Draw(b, chrome, tabs[i].Label, lx, win.Y + chromeBase, unread ? UnreadRgb : p.TabTextRgb,
                            tx + ChatWindow.Pad + avail, on || unread ? 1f : 0.62f);
                if (eph)
                {
                    float pinX = tx + tabW - btns;
                    UiText.Draw(b, chrome, "▪", pinX, win.Y + chromeBase, Tx2, pinX + ChatWindow.TabBtnW);
                    if (closeX)
                        UiText.Draw(b, chrome, "×", tx + tabW - ChatWindow.TabBtnW, win.Y + chromeBase, Tx2, tx + tabW);
                }
            }
        }

        if (showFrame) Outline(b, win.X, win.Y, win.X + win.W, win.Y + win.H, p.FrameRgb, fa);   // over the bar, so the frame is unbroken

        // Body: one pixel-scrolled column. ScrollPx (px up from the content bottom) drives the layout, so
        // scrolling is smooth rather than message-quantised; partial messages at the top/bottom edges are
        // emitted and clipped by the body scissor. Two passes: pass 1 measures EVERY match to get the content
        // height -- needed to clamp the scroll and size the scrollbar BEFORE placing, so an over-scroll can't
        // leave a stale gap-at-top frame -- then pass 2 re-wraps only the blocks that intersect the viewport.
        win.BodyRect(out float bx, out float by, out float bw, out float bh, showHeader);
        // Portraits keep the engine's portrait aspect (taller than wide) in a left gutter, only on lines with a
        // resref. A portrait line is one BLOCK, its height padded up to the portrait so it never overlaps its
        // neighbours. The gutter is reserved on resref, before the texture resolves, so the icon appears a
        // frame later WITHOUT the text reflowing. Portraits draw in a second pass over the body (before the
        // tail chrome); the settings panel is tail-drawn so it naturally covers them.
        bool portraitsOn = (win.Toggles & ChatToggle.Portrait) != 0;
        float portraitW = lh;                       // gutter-width icon...
        float portraitH = lh / PortraitAspect;      // ...grown tall to the real portrait shape (~1.56 rows)
        float gutter = portraitW + ChatWindow.Pad;
        // Reserve the scrollbar column unconditionally (even when the content fits and no track draws): the
        // text width stays stable, so lines don't reflow the moment history grows past one screen.
        float maxTextW = bw - 2 * ChatWindow.Pad - ChatWindow.ScrollbarW;

        // Snapshot the store version BEFORE the ring walk: a concurrent Append between View and this read
        // would pair the new version with stale blockH, giving a persistent false hit. Reading first means
        // a mid-build Append stores the old version → next build misses → self-heals in one frame.
        int storeVer = store.Version;
        ChatLine[] all = RingScratch();
        string? pin = active.PinnedName.Length > 0 ? active.PinnedName : null;
        int nAll = store.View(active.ChannelMask, active.PlayerId, all, pin);
        List<Rune> runes = _runesScratch ??= new List<Rune>();
        List<uint> cols = _colsScratch ??= new List<uint>();

        // Pass 1: wrap every match to a block height; sum -> contentH (a gap between messages).
        // Cached per window: the wrap is stable as long as width, content, and text-affecting settings
        // haven't changed, so a move drag (only X/Y change) skips the entire 512-line pass.
        LayoutCache? lc = win.WrapCache;
        float[] blockH;
        float contentH;
        if (lc != null && lc.Hit(maxTextW, active.ChannelMask, active.PlayerId, active.PinnedName,
                win.Toggles, storeVer, (int)win.TimeFormat, (int)win.TimeSource, atlas, nAll))
        {
            blockH = lc.BlockH;
            contentH = lc.ContentH;
        }
        else
        {
            blockH = new float[nAll];
            contentH = 0f;
            for (int li = 0; li < nAll; li++)
            {
                bool hasP = BuildRunes(win, all[li], portraitsOn, runes, cols);
                float indent = hasP ? gutter : 0f;
                List<TextWrapper.LineSpan> wr = TextWrapper.WrapRunes(runes, maxTextW - indent, atlas.AdvanceOfCached);
                blockH[li] = hasP ? System.Math.Max(wr.Count * lh, descentGap + portraitH) : wr.Count * lh;
                contentH += blockH[li];
            }
            if (nAll > 1) contentH += (nAll - 1) * MsgGap;
            contentH += BottomPad;
            win.WrapCache = new LayoutCache(blockH, contentH, nAll, maxTextW, active.ChannelMask,
                active.PlayerId, active.PinnedName, win.Toggles, storeVer, (int)win.TimeFormat, (int)win.TimeSource, atlas);
        }

        // Clamp the scroll to the measured content, then hand the metrics to the window (scrollbar + drag map).
        float maxScroll = contentH - bh > 0f ? contentH - bh : 0f;
        if (active.ScrollPx > maxScroll) active.ScrollPx = maxScroll;
        if (active.ScrollPx < 0f) active.ScrollPx = 0f;
        win.ContentH = contentH; win.ViewH = bh;

        // Pass 2: place from the bottom (newest block's bottom = body bottom, pushed down by ScrollPx) and emit
        // blocks intersecting the viewport PLUS a margin above and below, so content sliding out under the
        // scroll/slide residual has geometry to slide on instead of popping. The margin equals the residual
        // cap, so a bounded residual can never expose an un-emitted block. The body scissor clips it all.
        float easeMargin = ChatAnim.MaxResidualPx;
        int bodyStart = b.VertexCount;   // the animated (fade/slide/scroll) slice starts here
        float rowBottom = by + bh - BottomPad + active.ScrollPx;
        for (int li = 0; li < nAll; li++)
        {
            float top = rowBottom - blockH[li];
            if (top < by + bh + easeMargin && rowBottom > by - easeMargin)   // viewport + ease margin
            {
                int spanStart = b.VertexCount;   // this line's verts (rows + portrait frame) -> one fade unit
                bool hasPortrait = BuildRunes(win, all[li], portraitsOn, runes, cols);
                float indent = hasPortrait ? gutter : 0f;
                float availW = maxTextW - indent;
                bool gutterRight = hasPortrait && win.Align == TextAlign.Right;
                float rowX = bx + ChatWindow.Pad + (gutterRight ? 0f : indent);
                List<TextWrapper.LineSpan> wraps = TextWrapper.WrapRunes(runes, availW, atlas.AdvanceOfCached);
                for (int w = 0; w < wraps.Count; w++)
                {
                    var span = wraps[w];
                    RowAlign.Compute(win.Align, atlas, runes, span, availW, w == wraps.Count - 1,
                             out float xOff, out float spaceExtra);
                    EmitRow(b, atlas, runes, cols, span, rowX + xOff, top + w * lh + glyphBaseline, outline, spaceExtra);
                }
                if (hasPortrait)
                {
                    float px = gutterRight
                        ? bx + ChatWindow.Pad + maxTextW - portraitW
                        : bx + ChatWindow.Pad;
                    float py = top + descentGap;
                    bool hovered = win.HoveredPortrait == hits.Count;
                    var fc = hovered ? PortraitFrameHover : PortraitFrame;
                    b.Rect(px - PortraitFrameW, py - PortraitFrameW, px + portraitW + PortraitFrameW, py + portraitH + PortraitFrameW,
                           fc.r, fc.g, fc.b, fc.a);
                    portraits.Add(new PortraitQuad(px, py, portraitW, portraitH, all[li].PortraitResref, all[li].Timestamp));
                    hits.Add(new PortraitHit(px - PortraitFrameW, py - PortraitFrameW,
                        portraitW + 2 * PortraitFrameW, portraitH + 2 * PortraitFrameW, all[li].PlayerId));
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
            float sbx = bx + bw - ChatWindow.ScrollbarW, ins = Inset * Metrics.Scale;
            // Track stops one grip above the body bottom to match the hit zone (ChatWindow.SbTrackH): keeps
            // the thumb clear of the resize-grip corner.
            Fill(b, sbx + ins, by + ins, bx + bw - ins, by + bh - ChatWindow.ResizeGrip, Track, 0.6f);   // fixed chrome, not themeable
            if (win.ThumbRect(out float tt, out float th))
                b.Rect(sbx + ins, tt, bx + bw - ins, tt + th, p.TabActive.r, p.TabActive.g, p.TabActive.b, 1f);
        }

        // Pop-out input strip: below the frame (#25), a channel dropdown at its left + a word-wrapped edit
        // line that grows the strip DOWNWARD one FooterH per wrapped row (so long input never clips).
        if (showFooter)
            EmitInputStrip(b, chrome, win, editText, caret, selection, p);

        // Resize grip: three nested parallel diagonal lines in the bottom-right corner (the classic
        // "gripper"), shown while the pointer is over the window.
        if (showGrip && !win.Locked)
        {
            float gr = ((GripRgb >> 16) & 0xFF) / 255f, gg = ((GripRgb >> 8) & 0xFF) / 255f, gb = (GripRgb & 0xFF) / 255f;
            float cx = win.X + win.W - 3f, cy = win.Y + win.H - 3f;   // inset so the outer line clears the border
            GripLine(b, cx, cy, 6f, gr, gg, gb);
            GripLine(b, cx, cy, 11f, gr, gg, gb);
            GripLine(b, cx, cy, 16f, gr, gg, gb);
        }

        // Tab-bar [⚙] (#41) at the right edge, shown on hover (or settings open).
        if (showGrip || settings.Open)
        {
            float gearLeft = win.X + win.W - ChatWindow.GearW;
            float gx = gearLeft + (ChatWindow.GearW - UiText.Measure(chrome, "⚙")) * 0.5f;
            UiText.Draw(b, chrome, "⚙", gx, win.Y + chromeBase, settings.Open ? Hi : Tx2, win.X + win.W);
        }

        // Channel dropdown list (open): stacked over the body (away from the strip), so it draws LAST = on top.
        // The chosen channel wears the panel's nav look: a lit row with an accent bar at its left.
        if (showFooter && win.ChannelListOpen)
        {
            float fh = ChatWindow.FooterH, lx0 = win.X, lx1 = win.X + ChatWindow.DropdownW;
            float minTop = float.MaxValue, maxBot = float.MinValue;
            int row = 0;
            for (int i = 0; i < ChatWindow.SendChannels.Length; i++)
            {
                Channel ch = ChatWindow.SendChannels[i];
                if (!win.CanSelect(ch)) continue;
                float top = win.ChannelItemTop(row);
                bool sel = ch == win.SendChannel;
                Fill(b, lx0, top, lx1, top + fh, sel ? NavOn : HeaderBg, 0.98f);
                if (sel) b.Rect(lx0 + 1f, top, lx0 + 1f + 3f * Metrics.Scale, top + fh, p.TabActive.r, p.TabActive.g, p.TabActive.b, 1f);
                UiText.Draw(b, chrome, ch.ToString(), lx0 + ChatWindow.Pad + 3f * Metrics.Scale, top + chromeBase,
                            sel ? Hi : Tx2, lx1 - 2f);
                if (top < minTop) minTop = top;
                if (top + fh > maxBot) maxBot = top + fh;
                row++;
            }
            if (row > 0) Outline(b, lx0, minTop, lx1, maxBot, Border);
        }


        win.PortraitHits = hits.Count > 0 ? hits.ToArray() : System.Array.Empty<PortraitHit>();

        return new RenderFrame
        {
            Verts = b.ToArray(), Count = b.VertexCount, Portraits = portraits, Spans = spans,
            BodyStart = bodyStart, BodyEnd = bodyEnd, Bx = bx, By = by, Bw = bw, Bh = bh,
            Scrolled = active.ScrollPx != 0f,
            LastScroll = win.LastScroll,
            BlinkStart = blinkStart, BlinkEnd = blinkEnd,
            HoverStart = hoverStart, HoverEnd = hoverEnd, HoverSince = win.HoverSince,
            LeftStart = leftStart, LeftEnd = leftEnd, LeftSince = win.LeftSince,
        };
    }

    /// <summary>A chrome-only frame for the ghost tab floating at the pointer during a tab drag (#42).
    /// Draws on top of everything (appended as the last frame in the publish array).</summary>
    public static RenderFrame BuildGhostTab(GlyphAtlas atlas, ChatWindow paletteWin,
                                            float x, float y, float w, string label)
    {
        var p = ResolvePalette(paletteWin);
        var b = new VertexBatch(atlas.SolidU, atlas.SolidV);
        float h = ChatWindow.TabBarH;
        Fill(b, x, y, x + w, y + h, NavOn, 0.9f);
        Outline(b, x, y, x + w, y + h, Border);
        b.Rect(x + 1f, y + h - 2f * Metrics.Scale, x + w - 1f, y + h, p.TabActive.r, p.TabActive.g, p.TabActive.b, 1f);
        float lw = UiText.Measure(atlas, label), avail = w - 2f * ChatWindow.Pad;
        UiText.Draw(b, atlas, label, x + ChatWindow.Pad + (lw < avail ? (avail - lw) * 0.5f : 0f),
                    y + (h - atlas.LineHeight) * 0.5f + atlas.Ascent, Hi, x + w - ChatWindow.Pad);
        int vc = b.VertexCount;
        return new RenderFrame { Verts = b.ToArray(), Count = vc, BodyStart = vc, BodyEnd = vc };
    }

    /// <summary>Off-device proof of the portrait-gutter emission: an icon quad is produced for a line that
    /// carries a resref (anchored to its top visual row, in the left gutter) and none for a line without.
    /// Uses a stub atlas (zero-advance glyphs -&gt; one wrap row); real wrapping needs the native bake.</summary>
    public static void SelfCheck(CX.Framework.ExtenderLog log)
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
            E(q.Y == win.Y + win.H - BottomPad - ph, "quad anchored to the padded block's top");
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

        // Right-aligned: portrait flips to the right edge of the text area.
        win.Align = TextAlign.Right;
        var rPortraits = Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Portraits;
        if (rPortraits.Count == 1)
        {
            float mtw = win.W - 2 * ChatWindow.Pad - ChatWindow.ScrollbarW;
            E(System.Math.Abs(rPortraits[0].X - (ChatWindow.Pad + mtw - atlas.LineHeight)) < 0.1f,
              "right-aligned portrait sits at the right edge");
        }
        win.Align = TextAlign.Left;

        // Toggle off -> no icons even when a resref is present.
        win.Toggles &= ~ChatToggle.Portrait;
        var off = Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Portraits;
        E(off.Count == 0, "portrait toggle off suppresses the icon");

        // Row alignment moved to RowAlign (its own SelfCheck).

        // Layout cache (#53): second build with unchanged content is a cache hit; verts match cold build.
        win.Toggles = ChatToggle.Timestamp | ChatToggle.MessageBody | ChatToggle.Portrait;
        win.WrapCache = null;
        var cold = Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false);
        E(win.WrapCache != null, "first build populates the wrap cache");
        var lcRef = win.WrapCache;
        var cached = Build(win, settings, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false);
        E(ReferenceEquals(win.WrapCache, lcRef), "second build reuses the cache (not a silent miss)");
        E(cold.Count == cached.Count, "cache-hit build produces the same vertex count");
        E(cold.Verts.Length == cached.Verts.Length, "cache-hit build produces same-length verts");
        bool vertsMatch = true;
        for (int vi = 0; vi < cold.Verts.Length && vertsMatch; vi++)
            vertsMatch = System.Math.Abs(cold.Verts[vi] - cached.Verts[vi]) < 1e-3f;
        E(vertsMatch, "cache-hit verts match cold-build verts");

        // GradientRect corner mapping: 4 distinct colours → verify the winding order.
        {
            var gb = new VertexBatch(0, 0);
            var tl = (1f, 0f, 0f, 1f); var tr = (0f, 1f, 0f, 1f);
            var br = (0f, 0f, 1f, 1f); var bl = (1f, 1f, 0f, 1f);
            gb.GradientRect(0, 0, 10, 10, tl, tr, br, bl);
            float[] gv = gb.ToArray();
            E(gb.VertexCount == 6, "GradientRect emits 6 vertices");
            E(gv[4] == 1f && gv[5] == 0f, "V0 carries TL colour");
            E(gv[12] == 0f && gv[13] == 1f, "V1 carries TR colour");
            E(gv[20] == 0f && gv[21] == 0f && gv[22] == 1f, "V2 carries BR colour");
            E(gv[44] == 1f && gv[45] == 1f && gv[46] == 0f, "V5 carries BL colour");
        }

        // A gradient background is the BgGrid² quad grid; BgT is the mapping the grid samples.
        {
            var sw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
            var ss = new SettingsWindow();
            int solidVerts = Build(sw, ss, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Count;
            sw.BgMode = BgMode.Linear; sw.WrapCache = null;
            int gradVerts = Build(sw, ss, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Count;
            E(gradVerts - solidVerts == (BgGrid * BgGrid - 1) * 6, "a gradient's grid adds (N*N-1)*6 verts over solid");
            static bool Near(float a, float b) => System.Math.Abs(a - b) < 1e-4f;
            E(Near(BgT(sw, 0f, -1f, 400, 300), 0f) && Near(BgT(sw, 0f, 1f, 400, 300), 1f) && Near(BgT(sw, -1f, 0f, 400, 300), 0.5f),
              "Linear at the default 90° runs top (0) to bottom (1)");
            sw.BgAngle = 0f;
            E(Near(BgT(sw, -1f, 0f, 400, 300), 0f) && Near(BgT(sw, 1f, 0f, 400, 300), 1f), "Linear at 0° runs left to right");
            sw.BgAngle = 45f;
            E(Near(BgT(sw, -1f, -1f, 400, 300), 0f) && Near(BgT(sw, 1f, 1f, 400, 300), 1f), "Linear at 45° pins the corners to 0 and 1");
            sw.BgStart = 0.5f; sw.BgEnd = 0.5f; sw.BgAngle = 90f;
            E(Near(BgT(sw, 0f, -0.2f, 400, 300), 0f) && Near(BgT(sw, 0f, 0.2f, 400, 300), 1f), "End <= Start is a hard edge at Start");
            sw.BgStart = 0.25f; sw.BgEnd = 0.75f;
            E(Near(BgT(sw, 0f, -0.9f, 400, 300), 0f) && Near(BgT(sw, 0f, 0f, 400, 300), 0.5f) && Near(BgT(sw, 0f, 0.9f, 400, 300), 1f),
              "stops hold the ends and remap the middle");
            sw.BgStart = 0f; sw.BgEnd = 1f; sw.BgMode = BgMode.Radial;
            E(Near(BgT(sw, 0f, 0f, 400, 300), 0f) && Near(BgT(sw, 1f, 0f, 400, 300), 1f), "Radial runs centre to edge");
            sw.BgMode = BgMode.Conic; sw.BgAngle = 0f;
            E(Near(BgT(sw, 1f, 0f, 400, 300), 0f) && Near(BgT(sw, -1f, 0f, 400, 300), 1f)
              && Near(BgT(sw, 0f, 1f, 400, 300), 0.5f) && Near(BgT(sw, 0f, -1f, 400, 300), 0.5f), "Conic is mirrored about the angle (no seam)");
        }

        // Frame auto-hide drops the shadow rings + outline (4 fills each); a hovered tab adds one eased plate.
        {
            var fw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff), new Tab("B", 0xffffffff) }) { AutoHideHeader = false };
            var fs = new SettingsWindow();
            int framed = Build(fw, fs, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Count;
            fw.AutoHideFrame = true; fw.WrapCache = null;
            int bare = Build(fw, fs, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Count;
            E(framed - bare == (ShadowAlpha.Length + 1) * 24, "auto-hide frame removes the shadow rings and the outline when the pointer is away");
            int gripped = Build(fw, fs, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: true).Count;
            fw.AutoHideFrame = false; fw.WrapCache = null;
            E(Build(fw, fs, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: true).Count == gripped, "the frame is back while the pointer is inside");
            fw.AutoHideFrame = true; fw.WrapCache = null;
            var t0 = new System.DateTime(2026, 1, 1);
            E(fw.SetHoverTab(fw.X + fw.TabWFor(2) + 2f, fw.Y + 2f, t0) && fw.HoveredTab == 1 && fw.LeftTab == -1, "hovering tab 1 records it");
            E(!fw.SetHoverTab(fw.X + fw.TabWFor(2) + 3f, fw.Y + 2f, t0), "staying on the tab changes nothing");
            RenderFrame hf = Build(fw, fs, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: true);
            E(hf.HoverEnd - hf.HoverStart == 6 && hf.LeftEnd == hf.LeftStart && hf.HoverSince == t0, "the hovered tab has a 6-vert plate stamped with the hover time");
            E(fw.SetHoverTab(float.NaN, fw.Y + 2f, t0.AddMilliseconds(10)) && fw.HoveredTab == -1 && fw.LeftTab == 1, "leaving hands the tab to the fade-out");
            hf = Build(fw, fs, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: true);
            E(hf.HoverEnd == hf.HoverStart && hf.LeftEnd - hf.LeftStart == 6, "the left tab keeps a plate for the fade-out");
            E(RenderFrame.HoverT(t0.AddMilliseconds(RenderFrame.HoverMs * 0.5), t0) == 0.5f && RenderFrame.HoverT(t0.AddSeconds(1), t0) == 1f, "HoverT eases over HoverMs then clamps");
        }

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

    /// <summary>Emit the window background: a solid rect, or a <see cref="BgGrid"/>² quad grid whose vertex
    /// colours come from <see cref="BgT"/> (GPU-interpolated between them).</summary>
    private static void EmitBackground(VertexBatch b, ChatWindow win,
        (float r, float g, float b, float a) c1, (float r, float g, float b, float a) c2)
    {
        float x0 = win.X, y0 = win.Y, x1 = win.X + win.W, y1 = win.Y + win.H;
        if (win.BgMode == BgMode.Solid) { b.Rect(x0, y0, x1, y1, c1.r, c1.g, c1.b, c1.a); return; }
        const int N = BgGrid;
        float w = x1 - x0, h = y1 - y0;
        (float, float, float, float) At(int col, int row)
            => LerpColor(c1, c2, BgT(win, 2f * col / N - 1f, 2f * row / N - 1f, w, h));
        for (int row = 0; row < N; row++)
            for (int col = 0; col < N; col++)
                b.GradientRect(
                    x0 + col * w / N, y0 + row * h / N,
                    x0 + (col + 1) * w / N, y0 + (row + 1) * h / N,
                    At(col, row), At(col + 1, row), At(col + 1, row + 1), At(col, row + 1));
    }

    // ponytail: 20×20 grid; a hard stop blurs over one cell (5% of the window) -- a shader is the upgrade
    private const int BgGrid = 20;

    /// <summary>Gradient position of a point, 0 = Background .. 1 = Background 2, after the window's stops.
    /// <paramref name="sx"/>/<paramref name="sy"/> are the point in [-1,1] window space; <paramref name="w"/>/
    /// <paramref name="h"/> the window size (Linear measures along the angle in pixels, like CSS, so the
    /// corners land exactly on 0 and 1).</summary>
    internal static float BgT(ChatWindow win, float sx, float sy, float w, float h)
    {
        double th = win.BgAngle * System.Math.PI / 180.0;
        float dx = (float)System.Math.Cos(th), dy = (float)System.Math.Sin(th);
        float t;
        switch (win.BgMode)
        {
            case BgMode.Radial:
                t = (float)System.Math.Sqrt(sx * sx + sy * sy);
                break;
            case BgMode.Conic:
            {
                double a = System.Math.Atan2(sy, sx) - th;
                a = System.Math.Abs(System.Math.IEEERemainder(a, 2.0 * System.Math.PI));
                t = (float)(a / System.Math.PI);
                break;
            }
            default:
            {
                float p = sx * w * dx + sy * h * dy;
                float max = w * System.Math.Abs(dx) + h * System.Math.Abs(dy);
                t = max > 0f ? (p / max + 1f) * 0.5f : 0f;
                break;
            }
        }
        float s0 = win.BgStart, s1 = win.BgEnd;
        if (s1 <= s0) return t < s0 ? 0f : 1f;
        t = (t - s0) / (s1 - s0);
        return t < 0f ? 0f : t > 1f ? 1f : t;
    }

    private static (float r, float g, float b, float a) LerpColor(
        (float r, float g, float b, float a) a, (float r, float g, float b, float a) b, float t = 0.5f)
        => (a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);

    /// <summary>Laid-out block height (px) of one message -- the same wrap + portrait padding a body block
    /// gets in <see cref="Build"/>. The ingest path uses it to slide/compensate by the exact new-line height
    /// (block + one <see cref="MsgGap"/>) without re-measuring the whole backlog, so it's correct even under
    /// ring eviction (the read position holds to the bottom-anchored growth, not the net content change).</summary>
    public static float MeasureBlock(ChatWindow win, ChatLine line, GlyphAtlas atlas)
    {
        bool portraitsOn = (win.Toggles & ChatToggle.Portrait) != 0;
        win.BodyRect(out _, out _, out float bw, out _);
        float maxTextW = bw - 2 * ChatWindow.Pad - ChatWindow.ScrollbarW;
        List<Rune> runes = _runesScratch ??= new List<Rune>();
        List<uint> cols = _colsScratch ??= new List<uint>();
        bool hasP = BuildRunes(win, line, portraitsOn, runes, cols);
        float indent = hasP ? atlas.LineHeight + ChatWindow.Pad : 0f;
        float textH = TextWrapper.WrapRunes(runes, maxTextW - indent, atlas.AdvanceOfCached).Count * atlas.LineHeight;
        return hasP ? System.Math.Max(textH, atlas.LineHeight / PortraitAspect) : textH;
    }

    /// <summary>Run the toggle-gated line pipeline into <paramref name="runes"/>/<paramref name="cols"/>
    /// (cleared first). Returns whether the line should reserve a portrait gutter (portraits on + has resref).</summary>
    private static bool BuildRunes(ChatWindow win, ChatLine line, bool portraitsOn, List<Rune> runes, List<uint> cols)
    {
        runes.Clear(); cols.Clear();
        for (int c = 0; c < Pipeline.Length; c++)
            if ((win.Toggles & Pipeline[c].Bit) != 0) Pipeline[c].Contribute(win, line, runes, cols);
        return portraitsOn && !string.IsNullOrEmpty(line.PortraitResref);
    }

    /// <summary>Lay one wrapped row (a span into <paramref name="runes"/>) with per-rune colour.</summary>
    private static void EmitRow(VertexBatch b, GlyphAtlas atlas, List<Rune> runes, List<uint> cols,
                                TextWrapper.LineSpan span, float startX, float baseline,
                                bool outline = false, float spaceExtra = 0f)
    {
        float penX = startX;
        for (int k = span.Start; k < span.End; k++)
        {
            Rune r = runes[k];
            if (!atlas.TryGet(r.Value, out Glyph g)) { penX += atlas.AdvanceOf(r); continue; }
            uint c = cols[k];
            if (outline)
                for (int o = 0; o < OutlineOffsets.Length; o += 2)
                    b.Glyph(g, penX + OutlineOffsets[o], baseline + OutlineOffsets[o + 1], 0f, 0f, 0f, 1f);
            b.Glyph(g, penX, baseline, ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f);
            penX += g.Advance;
            if (r.Value == ' ') penX += spaceExtra;
        }
    }

    /// <summary>The word-wrapped input strip below the frame: a channel chip (top row) + the edit text
    /// wrapped to the strip width, the strip growing downward one <see cref="ChatWindow.FooterH"/> per row.
    /// Caret and selection are mapped from UTF-16 indices into wrapped rune rows.</summary>
    private static void EmitInputStrip(VertexBatch b, GlyphAtlas atlas, ChatWindow win, string editText,
                                       int caret, (int start, int end)? selection, Pal p)
    {
        float fh = ChatWindow.FooterH, ins = Inset * Metrics.Scale;
        float baseDrop = (fh - atlas.LineHeight) * 0.5f + atlas.Ascent;
        float startX = win.X + ChatWindow.DropdownW + ChatWindow.Pad;   // edit line begins right of the dropdown
        float editW = win.X + win.W - ChatWindow.Pad - startX;          // wrap width for the input text

        List<Rune> runes = _runesScratch ??= new List<Rune>();
        runes.Clear();
        foreach (Rune r in editText.EnumerateRunes()) runes.Add(r);
        List<TextWrapper.LineSpan> wraps = TextWrapper.WrapRunes(runes, editW, atlas.AdvanceOfCached);
        if (wraps.Count == 0) wraps.Add(new TextWrapper.LineSpan(0, 0));   // always at least one (empty) row
        int rows = wraps.Count;
        win.FooterRows = rows;   // set BEFORE reading StripTop: in above-mode StripTop depends on FooterHeight
        float fy = win.StripTop;

        // Strip background over all rows, framed like the window; the channel chip on the top row only.
        b.Rect(win.X, fy, win.X + win.W, fy + fh * rows, p.Footer.r, p.Footer.g, p.Footer.b, p.Footer.a);
        Outline(b, win.X, fy, win.X + win.W, fy + fh * rows, p.FrameRgb, p.Footer.a);
        float cx0 = win.X + ins, cx1 = win.X + ChatWindow.DropdownW - ins;
        Fill(b, cx0, fy + ins, cx1, fy + fh - ins, NavOn);
        Outline(b, cx0, fy + ins, cx1, fy + fh - ins, Frame2);
        UiText.Draw(b, atlas, win.SendChannel.ToString(), cx0 + ChatWindow.Pad, fy + baseDrop, Hi, cx1 - ChatWindow.Pad - UiText.Measure(atlas, "▾"));
        UiText.Draw(b, atlas, "▾", cx1 - ChatWindow.Pad * 0.5f - UiText.Measure(atlas, "▾"), fy + baseDrop, Tx2, cx1);

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
                float x0 = RowAlign.Measure(atlas, runes, span.Start, rs, startX);
                float x1 = RowAlign.Measure(atlas, runes, span.Start, re, startX);
                b.Rect(x0, ry + ins, x1, ry + fh - ins, p.SelBg.r, p.SelBg.g, p.SelBg.b, p.SelBg.a);
            }
            EmitRunes(b, atlas, runes, span, startX, ry + baseDrop, EditRgb);

            // Caret on this row? At a row's End it belongs to the NEXT row's start only on a contiguous
            // hard-break (no consumed space); when a space was consumed, End stays on this row.
            bool here = caretR >= span.Start && caretR <= span.End
                        && !(caretR == span.End && w < rows - 1 && wraps[w + 1].Start == span.End);
            if (here)
            {
                float cx = RowAlign.Measure(atlas, runes, span.Start, caretR, startX);
                b.Rect(cx, ry + ins + 1f, cx + 2f, ry + fh - ins - 1f, p.TabActive.r, p.TabActive.g, p.TabActive.b, 1f);
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
}
