using System.Collections.Generic;
using System.Text;
using Sowx.Services.Chat.Core;
using Sowx.Ui;

namespace Sowx.Services.Chat.Render;

/// <summary>
/// Builds one <see cref="VertexBatch"/> for a <see cref="ChatWindow"/> each time something changes
/// (ingest / input / resize -- all cold threads), which <see cref="ChatRenderer.Publish(RenderFrame[])"/> then draws.
/// The message body is assembled through an ordered array of stateless line components gated by the
/// window's toggle bitset (architecture #17): a new visual feature = a new component + a new bit.
/// Only the rows that fit the body are walked/wrapped, so a rebuild is cheap even per keystroke.
/// </summary>
public static class WindowRenderer
{
    /// <summary>Per-window resolved palette, threaded through Build → EmitInputStrip/EmitSettings.</summary>
    private struct Pal
    {
        public (float r, float g, float b, float a) Bg, Bg2, TabBar, TabActive, Footer, SelBg;
        public uint TabTextRgb;
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

    private static readonly (float r, float g, float b, float a) SbTrack = (0.04f, 0.05f, 0.07f, 0.55f);
    private const uint CaretRgb = 0xE8E8E8, EditRgb = 0xF0F0F0;

    /// <summary>8-way (±1px) offsets for the outline halo, as flat (dx,dy) pairs.</summary>
    private static readonly float[] OutlineOffsets = { -1, -1, 0, -1, 1, -1, -1, 0, 1, 0, -1, 1, 0, 1, 1, 1 };

    /// <summary>Default colours (RGBA8, <c>0xRRGGBBAA</c>) for the themeable keys. The one source of truth
    /// for both the render fields (via <see cref="ApplyPalette"/>) and the picker UI (via <see cref="CurrentColor"/>).</summary>
    public static readonly Dictionary<string, uint> Defaults = new()
    {
        [ChatColor.Background] = 0x0d0f17bd, [ChatColor.Background2] = 0x192040bd,
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
    };


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
    private static readonly (float r, float g, float b, float a) PortraitFrameHover = (1f, 1f, 1f, 1f);

    /// <summary>Vertical gap between messages -- breathing room, and it keeps adjacent portrait frames from
    /// touching (which read as one continuous outline). ponytail: fixed; a density setting is the ceiling.</summary>
    public const float MsgGap = 8f;

    /// <summary>Breathing room below the newest message so its block (notably a portrait's 1px bottom frame)
    /// clears the body's bottom scissor edge instead of being clipped flush against it.</summary>
    public const float BottomPad = 4f;

    /// <summary>Assemble a chatbox frame. <paramref name="showFooter"/> draws the edit line (own-input,
    /// typing, and the tab has a send channel); <paramref name="showGrip"/> draws the corner resize grip.</summary>
    public static RenderFrame Build(ChatWindow win, SettingsWindow settings, ChatStore store, GlyphAtlas atlas,
                                    bool showFooter, string editText, int caret, bool showGrip,
                                    (int start, int end)? selection = null)
    {
        var p = ResolvePalette(win);
        bool outline = win.Outline;

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
        EmitBackground(b, win, p.Bg, p.Bg2);
        bool hasUnread = false;
        for (int i = 0; i < tabs.Count; i++) if (tabs[i].Unread && tabs[i].Ephemeral) { hasUnread = true; break; }
        int blinkStart = 0, blinkEnd = 0;
        bool showHeader = !win.AutoHideHeader || showGrip || settings.Open || hasUnread;
        if (showHeader)
        {
            b.Rect(win.X, win.Y, win.X + win.W, win.Y + ChatWindow.TabBarH, p.TabBar.r, p.TabBar.g, p.TabBar.b, p.TabBar.a);
            float tabW = win.TabWFor(tabs.Count);
            for (int i = 0; i < tabs.Count; i++)
            {
                float tx = win.X + i * tabW;
                if (i == activeTab)
                    b.Rect(tx, win.Y, tx + tabW, win.Y + ChatWindow.TabBarH,
                           p.TabActive.r, p.TabActive.g, p.TabActive.b, p.TabActive.a);
                bool eph = tabs[i].Ephemeral;
                // Unread ephemeral tab: gold border, alpha modulated at draw time for blink.
                if (eph && tabs[i].Unread)
                {
                    if (blinkStart == 0) blinkStart = b.VertexCount;
                    const float bdr = 1.5f;
                    b.Rect(tx, win.Y, tx + tabW, win.Y + bdr, 0.85f, 0.70f, 0.20f, 1f);
                    b.Rect(tx, win.Y + ChatWindow.TabBarH - bdr, tx + tabW, win.Y + ChatWindow.TabBarH, 0.85f, 0.70f, 0.20f, 1f);
                    b.Rect(tx, win.Y, tx + bdr, win.Y + ChatWindow.TabBarH, 0.85f, 0.70f, 0.20f, 1f);
                    b.Rect(tx + tabW - bdr, win.Y, tx + tabW, win.Y + ChatWindow.TabBarH, 0.85f, 0.70f, 0.20f, 1f);
                    blinkEnd = b.VertexCount;
                }
                bool closeX = eph && tabs.Count > 1;
                float btns = (closeX ? ChatWindow.TabBtnW : 0f) + (eph ? ChatWindow.TabBtnW : 0f);
                float labelClip = tx + tabW - ChatWindow.Pad - btns;
                DrawString(b, atlas, tabs[i].Label, tx + ChatWindow.Pad,
                           win.Y + descentGap + glyphBaseline, p.TabTextRgb, labelClip);
                if (eph)
                {
                    float pinX = tx + tabW - btns;
                    DrawString(b, atlas, "▪", pinX, win.Y + descentGap + glyphBaseline, GearRgb, pinX + ChatWindow.TabBtnW);
                    if (closeX)
                        DrawString(b, atlas, "×", tx + tabW - ChatWindow.TabBtnW,
                                   win.Y + descentGap + glyphBaseline, p.TabTextRgb, tx + tabW);
                }
            }
        }

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
            float sbx = bx + bw - ChatWindow.ScrollbarW;
            // Track stops one grip above the body bottom to match the hit zone (ChatWindow.SbTrackH): keeps
            // the thumb clear of the resize-grip corner.
            b.Rect(sbx, by, bx + bw, by + bh - ChatWindow.ResizeGrip, SbTrack.r, SbTrack.g, SbTrack.b, SbTrack.a);   // fixed chrome, not themeable
            if (win.ThumbRect(out float tt, out float th))
                b.Rect(sbx, tt, bx + bw, tt + th, p.TabActive.r, p.TabActive.g, p.TabActive.b, p.TabActive.a);
        }

        // Pop-out input strip: below the frame (#25), a channel dropdown at its left + a word-wrapped edit
        // line that grows the strip DOWNWARD one FooterH per wrapped row (so long input never clips).
        if (showFooter)
            EmitInputStrip(b, atlas, win, editText, caret, selection, descentGap + glyphBaseline, p);

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

        // Tab-bar button group (#41): [+] [⚙] [×] at the right edge, shown on hover (or settings open).
        if (showGrip || settings.Open)
        {
            float btnW = ChatWindow.GearW;
            float btnsW = win.ButtonsW;
            // + (new window) — leftmost button
            DrawString(b, atlas, "+", win.X + win.W - btnsW + 4f,
                       win.Y + descentGap + glyphBaseline, GearRgb, win.X + win.W - btnsW + btnW);
            // ⚙ (settings) — second from right (or rightmost when close is hidden)
            float gearLeft = win.ShowClose ? win.X + win.W - 2f * btnW : win.X + win.W - btnW;
            uint gc = settings.Open ? GearActiveRgb : GearRgb;
            DrawString(b, atlas, "⚙", gearLeft + 4f,
                       win.Y + descentGap + glyphBaseline, gc, gearLeft + btnW);
            // × (close window) — rightmost, hidden for Main and locked windows
            if (win.ShowClose)
                DrawString(b, atlas, "×", win.X + win.W - btnW + 4f,
                           win.Y + descentGap + glyphBaseline, GearRgb, win.X + win.W);
        }

        // Channel dropdown list (open): stacked over the body (away from the strip), so it draws LAST = on top.
        if (showFooter && win.ChannelListOpen)
        {
            float fh = ChatWindow.FooterH;
            int row = 0;
            for (int i = 0; i < ChatWindow.SendChannels.Length; i++)
            {
                Channel ch = ChatWindow.SendChannels[i];
                if (!win.CanSelect(ch)) continue;
                float top = win.ChannelItemTop(row);
                var dbg = ch == win.SendChannel ? p.TabActive : p.TabBar;
                b.Rect(win.X, top, win.X + ChatWindow.DropdownW, top + fh, dbg.r, dbg.g, dbg.b, 0.98f);
                DrawString(b, atlas, ch.ToString(), win.X + ChatWindow.Pad,
                           top + descentGap + glyphBaseline, p.TabTextRgb, win.X + ChatWindow.DropdownW - 2);
                row++;
            }
        }

        // Settings window (open): a titled floating panel with a checkbox row per option, drawn LAST so it
        // sits on top of everything. It grows with the option list (the point of it over the gear dropdown).
        if (settings.Open)
            EmitSettings(b, atlas, settings, descentGap + glyphBaseline, p);

        win.PortraitHits = hits.Count > 0 ? hits.ToArray() : System.Array.Empty<PortraitHit>();

        return new RenderFrame
        {
            Verts = b.ToArray(), Count = b.VertexCount, Portraits = portraits, Spans = spans,
            BodyStart = bodyStart, BodyEnd = bodyEnd, Bx = bx, By = by, Bw = bw, Bh = bh,
            Scrolled = active.ScrollPx != 0f,
            LastScroll = win.LastScroll,
            BlinkStart = blinkStart, BlinkEnd = blinkEnd,
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
        b.Rect(x, y, x + w, y + h, p.TabActive.r, p.TabActive.g, p.TabActive.b, p.TabActive.a * 0.6f);
        DrawString(b, atlas, label, x + ChatWindow.Pad, y + atlas.LineHeight, p.TabTextRgb, x + w - ChatWindow.Pad);
        int vc = b.VertexCount;
        return new RenderFrame { Verts = b.ToArray(), Count = vc, BodyStart = vc, BodyEnd = vc };
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

        // BgMode.StripesH emits StripeCount rects instead of 1 solid.
        {
            var sw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
            var ss = new SettingsWindow();
            int solidVerts = Build(sw, ss, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Count;
            sw.BgMode = BgMode.StripesH; sw.WrapCache = null;
            int stripeVerts = Build(sw, ss, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Count;
            E(stripeVerts - solidVerts == (StripeCount - 1) * 6, "StripesH adds (StripeCount-1)*6 verts over solid");
            sw.BgMode = BgMode.GradientBox; sw.WrapCache = null;
            int boxVerts = Build(sw, ss, store, atlas, showFooter: false, editText: "", caret: 0, showGrip: false).Count;
            E(boxVerts - solidVerts == (RadialGrid * RadialGrid - 1) * 6, "GradientBox grid adds (N*N-1)*6 verts over solid");
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

    /// <summary>Emit the window background according to its <see cref="BgMode"/>: solid, gradient (GPU-
    /// interpolated per-corner colours), or stripes (alternating rects).</summary>
    private static void EmitBackground(VertexBatch b, ChatWindow win,
        (float r, float g, float b, float a) c1, (float r, float g, float b, float a) c2)
    {
        float x0 = win.X, y0 = win.Y, x1 = win.X + win.W, y1 = win.Y + win.H;
        switch (win.BgMode)
        {
            case BgMode.GradientV:
                b.GradientRect(x0, y0, x1, y1, c1, c1, c2, c2); break;
            case BgMode.GradientH:
                b.GradientRect(x0, y0, x1, y1, c1, c2, c2, c1); break;
            case BgMode.GradientD:
                var mid = LerpColor(c1, c2);
                b.GradientRect(x0, y0, x1, y1, c1, mid, c2, mid); break;
            case BgMode.StripesH:
                EmitStripes(b, x0, y0, x1, y1, c1, c2, horizontal: true); break;
            case BgMode.StripesV:
                EmitStripes(b, x0, y0, x1, y1, c1, c2, horizontal: false); break;
            case BgMode.GradientBox:
                EmitGridGradient(b, x0, y0, x1, y1, c1, c2, GridDist.Box); break;
            case BgMode.GradientCircle:
                EmitGridGradient(b, x0, y0, x1, y1, c1, c2, GridDist.Circle); break;
            case BgMode.Conic:
                EmitGridGradient(b, x0, y0, x1, y1, c1, c2, GridDist.Conic2); break;
            case BgMode.Conic4:
                EmitGridGradient(b, x0, y0, x1, y1, c1, c2, GridDist.Conic4); break;
            default:
                b.Rect(x0, y0, x1, y1, c1.r, c1.g, c1.b, c1.a); break;
        }
    }

    // ponytail: fixed 10 bands; a stripe-width setting is the ceiling
    private const int StripeCount = 10;

    private static void EmitStripes(VertexBatch b, float x0, float y0, float x1, float y1,
        (float r, float g, float b, float a) c1, (float r, float g, float b, float a) c2, bool horizontal)
    {
        for (int i = 0; i < StripeCount; i++)
        {
            var c = i % 2 == 0 ? c1 : c2;
            if (horizontal)
            {
                float sh = (y1 - y0) / StripeCount;
                b.Rect(x0, y0 + i * sh, x1, y0 + (i + 1) * sh, c.r, c.g, c.b, c.a);
            }
            else
            {
                float sw = (x1 - x0) / StripeCount;
                b.Rect(x0 + i * sw, y0, x0 + (i + 1) * sw, y1, c.r, c.g, c.b, c.a);
            }
        }
    }

    private enum GridDist { Box, Circle, Conic2, Conic4 }

    // ponytail: 10×10 grid; finer resolution is the ceiling
    private const int RadialGrid = 10;

    /// <summary>Emit an NxN grid of quads with per-vertex colours computed from a distance/angle function.
    /// Box/Circle use distance from centre; Conic uses angle. Edge colour = c1, centre colour = c2.</summary>
    private static void EmitGridGradient(VertexBatch b, float x0, float y0, float x1, float y1,
        (float r, float g, float b, float a) c1, (float r, float g, float b, float a) c2, GridDist dist)
    {
        const int N = RadialGrid;
        float w = x1 - x0, h = y1 - y0;
        for (int row = 0; row < N; row++)
            for (int col = 0; col < N; col++)
                b.GradientRect(
                    x0 + col * w / N, y0 + row * h / N,
                    x0 + (col + 1) * w / N, y0 + (row + 1) * h / N,
                    GridColor(col, row, N, c1, c2, dist),
                    GridColor(col + 1, row, N, c1, c2, dist),
                    GridColor(col + 1, row + 1, N, c1, c2, dist),
                    GridColor(col, row + 1, N, c1, c2, dist));
    }

    private static (float r, float g, float b, float a) GridColor(int col, int row, int n,
        (float r, float g, float b, float a) edge, (float r, float g, float b, float a) center, GridDist dist)
    {
        float sx = 2f * col / n - 1f, sy = 2f * row / n - 1f;
        float t;
        switch (dist)
        {
            case GridDist.Circle:
                t = (float)System.Math.Sqrt(sx * sx + sy * sy);
                if (t > 1f) t = 1f;
                break;
            case GridDist.Conic2:
            {
                float a = (float)System.Math.Atan2(sy, sx);
                t = 1f - (float)System.Math.Abs(System.Math.Cos(a));
                break;
            }
            case GridDist.Conic4:
            {
                float a = (float)System.Math.Atan2(sy, sx);
                t = 1f - (float)System.Math.Abs(System.Math.Cos(2.0 * a));
                break;
            }
            default: // Box
                t = System.Math.Max(System.Math.Abs(sx), System.Math.Abs(sy));
                break;
        }
        return LerpColor(center, edge, t);
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

    /// <summary>The word-wrapped input strip below the frame: a dropdown button (top row) + the edit text
    /// wrapped to the strip width, the strip growing downward one <see cref="ChatWindow.FooterH"/> per row.
    /// Caret and selection are mapped from UTF-16 indices into wrapped rune rows. <paramref name="baseDrop"/>
    /// is the baseline offset from a row's top.</summary>
    private static void EmitInputStrip(VertexBatch b, GlyphAtlas atlas, ChatWindow win, string editText,
                                       int caret, (int start, int end)? selection, float baseDrop, Pal p)
    {
        float fh = ChatWindow.FooterH;
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

        // Strip background over all rows; dropdown button on the top row only.
        b.Rect(win.X, fy, win.X + win.W, fy + fh * rows, p.Footer.r, p.Footer.g, p.Footer.b, p.Footer.a);
        b.Rect(win.X, fy, win.X + ChatWindow.DropdownW, fy + fh, p.TabBar.r, p.TabBar.g, p.TabBar.b, p.TabBar.a);
        DrawString(b, atlas, win.SendChannel + " ▾", win.X + ChatWindow.Pad, fy + baseDrop, p.TabTextRgb,
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
                float x0 = RowAlign.Measure(atlas, runes, span.Start, rs, startX);
                float x1 = RowAlign.Measure(atlas, runes, span.Start, re, startX);
                b.Rect(x0, ry + 3, x1, ry + fh - 3, p.SelBg.r, p.SelBg.g, p.SelBg.b, p.SelBg.a);
            }
            EmitRunes(b, atlas, runes, span, startX, ry + baseDrop, EditRgb);

            // Caret on this row? At a row's End it belongs to the NEXT row's start only on a contiguous
            // hard-break (no consumed space); when a space was consumed, End stays on this row.
            bool here = caretR >= span.Start && caretR <= span.End
                        && !(caretR == span.End && w < rows - 1 && wraps[w + 1].Start == span.End);
            if (here)
            {
                float cx = RowAlign.Measure(atlas, runes, span.Start, caretR, startX);
                b.Rect(cx, ry + 3, cx + 2, ry + fh - 3,
                       ((CaretRgb >> 16) & 0xFF) / 255f, ((CaretRgb >> 8) & 0xFF) / 255f, (CaretRgb & 0xFF) / 255f, 1f);
            }
        }
    }

    /// <summary>The settings window: a title bar ("Settings" + a × close button) over the typed rows the
    /// <see cref="SettingsWindow"/> laid out -- checkboxes, cycle buttons, sliders, and expandable RGBA colour
    /// pickers. Anchored + sized by the window; drawn last so it sits over everything. Reads the rows' current
    /// values (the core already resolved them from the config), so it never touches the config itself.</summary>
    private static void EmitSettings(VertexBatch b, GlyphAtlas atlas, SettingsWindow s, float baseDrop, Pal p)
    {
        float x = s.X, y = s.Y, w = s.W;
        float vpad = (SettingsWindow.RowH - atlas.LineHeight) / 2f;

        b.Rect(x, y, x + w, y + s.H, p.Footer.r, p.Footer.g, p.Footer.b, 1f);
        b.Rect(x, y, x + w, y + SettingsWindow.TitleH, p.TabBar.r, p.TabBar.g, p.TabBar.b, 1f);
        DrawString(b, atlas, "Settings", x + SettingsWindow.Pad, y + baseDrop, p.TabTextRgb, x + w - SettingsWindow.CloseW);
        DrawString(b, atlas, "×", x + w - SettingsWindow.CloseW + 5f, y + baseDrop, GearRgb, x + w);

        foreach (SetRow r in s.Rows)
        {
            float labelX = x + SettingsWindow.Pad + (r.Channel >= 0 ? SettingsWindow.ChannelIndent : 0f);
            float labelClip = r.Kind == SetKind.Toggle ? x + w - SettingsWindow.CheckSize - SettingsWindow.Pad
                                                        : x + w * SettingsWindow.ControlX - 2f;
            DrawString(b, atlas, r.Label, labelX, r.Top + baseDrop + vpad, p.TabTextRgb, labelClip);

            switch (r.Kind)
            {
                case SetKind.Toggle:
                {
                    float cs = SettingsWindow.CheckSize;
                    float cbx = x + w - SettingsWindow.Pad - cs, cby = r.Top + (SettingsWindow.RowH - cs) / 2f;
                    b.Rect(cbx, cby, cbx + cs, cby + cs, CheckFrame.r, CheckFrame.g, CheckFrame.b, CheckFrame.a);
                    var fill = r.On ? p.TabActive : p.Footer;
                    b.Rect(cbx + 1, cby + 1, cbx + cs - 1, cby + cs - 1, fill.r, fill.g, fill.b, 1f);
                    break;
                }
                case SetKind.Cycle:
                {
                    float bx0 = x + w * SettingsWindow.ControlX, bx1 = x + w - SettingsWindow.Pad;
                    bool editing = s.EditingRow(r);
                    var cbg = editing ? p.TabActive : p.TabBar;
                    b.Rect(bx0, r.Top + 3, bx1, r.Top + SettingsWindow.RowH - 3, cbg.r, cbg.g, cbg.b, cbg.a);
                    string ctext = editing ? s.EditText : r.Value;
                    float cend = DrawString(b, atlas, ctext, bx0 + 4f, r.Top + baseDrop + vpad, p.TabTextRgb, bx1 - 2f);
                    if (editing)
                        b.Rect(cend, r.Top + 5f, cend + 1.5f, r.Top + SettingsWindow.RowH - 5f,
                               ((CaretRgb >> 16) & 0xFF) / 255f, ((CaretRgb >> 8) & 0xFF) / 255f, (CaretRgb & 0xFF) / 255f, 1f);
                    break;
                }
                case SetKind.Slider:
                {
                    s.SliderTrack(r, out float sx0, out float sx1, out float cy);
                    b.Rect(sx0, cy - 2f, sx1, cy + 2f, SbTrack.r, SbTrack.g, SbTrack.b, SbTrack.a);
                    float knob = sx0 + r.Norm * (sx1 - sx0);
                    b.Rect(sx0, cy - 2f, knob, cy + 2f, p.TabActive.r, p.TabActive.g, p.TabActive.b, p.TabActive.a);
                    b.Rect(knob - 2f, r.Top + 4f, knob + 2f, r.Top + SettingsWindow.RowH - 4f,
                           ((GripRgb >> 16) & 0xFF) / 255f, ((GripRgb >> 8) & 0xFF) / 255f, (GripRgb & 0xFF) / 255f, 1f);
                    s.ValueBox(r, out float vx0, out float vx1, out float vy0, out float vy1);
                    bool editing = s.EditingRow(r);
                    var vbg = editing ? p.TabActive : p.TabBar;
                    b.Rect(vx0, vy0, vx1, vy1, vbg.r, vbg.g, vbg.b, vbg.a);
                    string vtext = editing ? s.EditText : SettingsWindow.FormatValue(r);
                    float vend = DrawString(b, atlas, vtext, vx0 + 3f, r.Top + baseDrop + vpad, p.TabTextRgb, vx1 - 2f);
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
                case SetKind.Button:
                    break;   // label already drawn above; no control widget
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

