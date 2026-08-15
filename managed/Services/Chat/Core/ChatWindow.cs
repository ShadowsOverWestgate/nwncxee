using Sowx.Framework;

namespace Sowx.Services.Chat.Core;

/// <summary>The six sendable chat channels (map #13 floor). Each maps to a vanilla
/// <c>CNWCMessage::SendPlayerToServerChat_*</c> sender (major 0x09; see ChatService.Send).
/// <see cref="Tell"/> alone carries a target playerId; the rest are proximity/context routed.</summary>
public enum Channel { Talk, Shout, Whisper, Tell, Party, DM }

/// <summary>
/// One chat tab: a pure <em>receive</em> filter into the shared <see cref="ChatStore"/> plus its own
/// scrollback offset. The send channel is window-level (a dropdown, not a tab) since #25. A <c>class</c>:
/// <see cref="ScrollPx"/> mutates as the user scrolls.
/// </summary>
public sealed class Tab
{
    public string Label;
    public uint ChannelMask;      // bit i set => show channel i (channel value masked to <32, see ChatStore)
    public uint? PlayerId;        // when set, only this player's lines
    public float ScrollPx;        // pixels scrolled up from the bottom of the content; 0 = pinned to newest

    public Tab(string label, uint channelMask, uint? playerId = null)
    { Label = label; ChannelMask = channelMask; PlayerId = playerId; }

    /// <summary>Does a line show in this tab? (same predicate as <see cref="ChatStore.View"/>: channel bit
    /// set, and -- when the tab pins a player -- the ids match). ponytail: channels assumed &lt; 32.</summary>
    public bool Matches(ChatLine line)
        => (ChannelMask & (1u << (int)(line.Channel & 31))) != 0
           && (!PlayerId.HasValue || line.PlayerId == PlayerId.Value);
}

/// <summary>
/// A movable/resizable chat window: rectangle, tabs, and the pointer drag/resize state machine (all
/// pure -- pixels in, rect out, no native refs, so it self-checks off-device). v1 runs exactly one;
/// every draw/input path takes a <see cref="ChatWindow"/> so multi-window later is a list + a loop.
/// Coordinates are screen pixels, y-down (matching the render ortho and SDL mouse coords).
/// </summary>
public sealed class ChatWindow
{
    public float X, Y, W, H;
    public readonly Tab[] Tabs;
    public int ActiveTab;

    /// <summary>Current send channel (the dropdown selection, #25). Window-level, not per-tab. Dropdown
    /// and slash commands both write this; reset to <see cref="Channel.Talk"/> on relog by the shell.</summary>
    public Channel SendChannel = Channel.Talk;

    /// <summary>True while the channel dropdown list is expanded (over the body). Set by hit-testing.</summary>
    public bool ChannelListOpen;

    /// <summary>Render options the settings window edits, as (toggle bit, label). Data-driven: a new option is
    /// one entry -- the window grows to fit. <see cref="ChatToggle.MessageBody"/> is deliberately excluded -- a
    /// checkbox that blanks all chat is a trap.</summary>
    public static readonly (uint Bit, string Label)[] Options =
        { (ChatToggle.Timestamp, "Timestamps"), (ChatToggle.Portrait, "Portraits") };

    /// <summary>The shell sets this each rebuild = own-input && typing. The input strip + dropdown only
    /// exist (render + hit-test) while it's true.</summary>
    public bool FooterVisible;

    /// <summary>Put the input strip above the window instead of below it (settings window). Flips
    /// <see cref="StripTop"/> and the channel-list stack direction; the renderer + hit-tests follow.</summary>
    public bool InputOnTop;

    /// <summary>Wrapped input-line count (>=1). The renderer measures the word-wrap and sets this so the
    /// strip (which grows downward one <see cref="FooterH"/> per row) hit-tests at its true height.</summary>
    public int FooterRows = 1;
    /// <summary>Full height of the pop-out input strip, grown by word-wrap.</summary>
    public float FooterHeight => FooterH * (FooterRows < 1 ? 1 : FooterRows);

    /// <summary>Render-component toggle bitset (see the render pipeline). Default: timestamp + body + portrait.</summary>
    public uint Toggles = ChatToggle.Timestamp | ChatToggle.MessageBody | ChatToggle.Portrait;

    /// <summary>The six sendable channels, in dropdown order.</summary>
    public static readonly Channel[] SendChannels =
        { Channel.Talk, Channel.Shout, Channel.Whisper, Channel.Tell, Channel.Party, Channel.DM };

    // Layout metrics: design pixels at Metrics.Scale==1, multiplied by the live scale so the chrome tracks the
    // font size + game UI scale in lockstep (no clipping at larger sizes). ponytail: no per-metric tuning; one
    // uniform scale keeps every text-to-chrome ratio fixed, which is all the clipping fix needs.
    public static float TabBarH => 22f * Metrics.Scale;
    public static float TabW => 74f * Metrics.Scale;
    public static float FooterH => 22f * Metrics.Scale;
    public static float DropdownW => 92f * Metrics.Scale;   // channel selector at the strip's left edge
    public static float Pad => 6f * Metrics.Scale;
    public static float ResizeGrip => 14f * Metrics.Scale;
    /// <summary>Gear icon hit box: a square at the tab bar's right edge (opens the settings window).</summary>
    public static float GearW => TabBarH;
    public static float MinW => 200f * Metrics.Scale;
    public static float MinH => 120f * Metrics.Scale;
    /// <summary>Scrollbar track width (px): a thin gutter at the body's right edge.</summary>
    public static float ScrollbarW => 8f * Metrics.Scale;
    /// <summary>Smallest thumb, so it stays grabbable when the history is very long.</summary>
    public static float SbMinThumb => 24f * Metrics.Scale;

    /// <summary>Total laid-out content height and the body (viewport) height, written by
    /// <see cref="WindowRenderer.Build"/> each rebuild so the scrollbar hit-test and thumb-drag map against
    /// the exact layout the draw used. 0 until the first build.</summary>
    public float ContentH, ViewH;
    /// <summary>How far the content can scroll up before the oldest line reaches the top (0 when it all fits).</summary>
    public float MaxScroll => ContentH > ViewH ? ContentH - ViewH : 0f;

    /// <summary>Scrollbar track height: the body minus the bottom <see cref="ResizeGrip"/>, so the thumb
    /// never enters the resize-grip corner (they used to overlap and steal each other's clicks).</summary>
    private float SbTrackH => System.Math.Max(0f, ViewH - ResizeGrip);

    /// <summary>Thumb height (px), proportional to the (grip-reserved) track and floored at
    /// <see cref="SbMinThumb"/>. Only meaningful when the content overflows the body.</summary>
    private float ThumbHeight()
    {
        float h = SbTrackH * ViewH / ContentH;
        return h < SbMinThumb ? SbMinThumb : h;
    }

    /// <summary>Thumb rect (<paramref name="top"/>, <paramref name="height"/>) in screen px, or false when
    /// nothing scrolls (content fits). Sits at the track bottom (newest, just above the resize grip) at
    /// ScrollPx 0 and rises to the top as ScrollPx approaches <see cref="MaxScroll"/>.</summary>
    public bool ThumbRect(out float top, out float height)
    {
        top = 0f; height = 0f;
        if (ContentH <= ViewH || ViewH <= 0f) return false;
        height = ThumbHeight();
        float frac = MaxScroll > 0f ? Active.ScrollPx / MaxScroll : 0f;   // 0 = newest/bottom, 1 = oldest/top
        top = Y + TabBarH + (1f - frac) * (SbTrackH - height);
        return true;
    }

    /// <summary>True when the pointer is over the scrollbar thumb (the grab zone).</summary>
    public bool OverScrollThumb(float mx, float my)
    {
        if (!ThumbRect(out float top, out float h)) return false;
        return mx >= X + W - ScrollbarW && mx <= X + W && my >= top && my <= top + h;
    }

    /// <summary>Top y of the input strip (#25). Below the frame by default; above it (top grows up as the
    /// input wraps) when <see cref="InputOnTop"/>. Only live while <see cref="FooterVisible"/>.</summary>
    public float StripTop => InputOnTop ? Y - FooterHeight : Y + H;
    /// <summary>The dropdown button (closed state) on the strip's top row (where the renderer draws it).</summary>
    public bool OverDropdownButton(float mx, float my)
        => mx >= X && mx < X + DropdownW && my >= StripTop && my < StripTop + FooterH;
    /// <summary>Top y of channel-list item <paramref name="i"/>. The list stacks over the body (away from the
    /// world): up from the strip's top when the strip is below the frame, down from the frame top when above.
    /// ponytail: in above-mode with a multi-row (wrapped) input the list anchors to the frame, not the button
    /// row; the button sits at the strip top. Fine for the common single-row input; revisit if multi-row lands.</summary>
    public float ChannelItemTop(int i) => InputOnTop ? Y + FooterH * i : StripTop - FooterH * (i + 1);
    /// <summary>Channel-list item under the pointer while open, or -1.</summary>
    public int ChannelItemAt(float mx, float my)
    {
        if (mx < X || mx >= X + DropdownW) return -1;
        for (int i = 0; i < SendChannels.Length; i++)
        {
            float top = ChannelItemTop(i);
            if (my >= top && my < top + FooterH) return i;
        }
        return -1;
    }

    private enum Grab { None, Move, Resize, Scroll }
    private Grab _grab;
    private bool _pressConsumed;        // did we consume the matching mouse-down? (button-pair ownership)
    private float _gx, _gy, _gw, _gh;   // grab anchor: pointer/rect at mouse-down
    private float _gsp;                  // scroll-grab anchor: ScrollPx at the thumb grab

    public ChatWindow(float x, float y, float w, float h, Tab[] tabs)
    { X = x; Y = y; W = w; H = h; Tabs = tabs; }

    public Tab Active => Tabs[ActiveTab];

    /// <summary>True while dragging, resizing, or scrubbing the scrollbar -- the shell consumes
    /// pointer-motion only then.</summary>
    public bool Dragging => _grab != Grab.None;

    /// <summary>True while the scrollbar thumb is being dragged (the shell resets the fade clock as it moves).</summary>
    public bool ScrollDragging => _grab == Grab.Scroll;

    public bool Contains(float mx, float my) => mx >= X && mx <= X + W && my >= Y && my <= Y + H;

    /// <summary>True when the pointer is over the bottom-right resize grip (also the hover-handle zone).</summary>
    /// <summary>Grow the rect to the current scaled minimum. Called when Metrics.Scale changes so a rect saved
    /// at a smaller scale (or restored before the UI scale synced) can't leave the chrome overflowing its box.</summary>
    public void ClampToMin() { if (W < MinW) W = MinW; if (H < MinH) H = MinH; }

    public bool OverResizeGrip(float mx, float my)
        => mx >= X + W - ResizeGrip && mx <= X + W && my >= Y + H - ResizeGrip && my <= Y + H;

    /// <summary>True when the pointer is over the gear icon (top-right of the tab bar). The shell opens the
    /// settings window on a press here; ChatWindow itself no longer owns any options state.</summary>
    public bool OverGear(float mx, float my)
        => mx >= X + W - GearW && mx <= X + W && my >= Y && my <= Y + TabBarH;

    /// <summary>Tab index under the pointer in the tab bar, or -1 (bar background = the move handle).</summary>
    private int TabAt(float mx, float my)
    {
        if (my < Y || my > Y + TabBarH) return -1;
        int i = (int)((mx - X) / TabW);
        return (i >= 0 && i < Tabs.Length && mx >= X && mx < X + Tabs.Length * TabW) ? i : -1;
    }

    /// <summary>
    /// Pointer press. Returns true if it lands on the window (the shell then consumes it, so the click
    /// doesn't fall through to the 3D world). Grabs move/resize, or switches tab.
    /// </summary>
    public bool MouseDown(float mx, float my)
    {
        // The gear icon (opens the settings window) is claimed by the shell before this runs -- see
        // ChatService.OnMouseButton. ChatWindow owns no options state now.

        // Pop-out strip + channel dropdown (below the frame, so outside Contains) -- only while typing.
        if (FooterVisible)
        {
            if (ChannelListOpen)
            {
                int item = ChannelItemAt(mx, my);
                if (item >= 0) SendChannel = SendChannels[item];   // pick a channel
                ChannelListOpen = false;                           // any click closes the list
                _pressConsumed = true;
                return true;
            }
            if (OverDropdownButton(mx, my)) { ChannelListOpen = true; _pressConsumed = true; return true; }
            if (mx >= X && mx < X + W && my >= StripTop && my < StripTop + FooterHeight)
            { _pressConsumed = true; return true; }                // click in the input strip: consume, no-op
        }

        if (!Contains(mx, my)) { _pressConsumed = false; return false; }
        _pressConsumed = true;
        // Scrollbar track stops one ResizeGrip above the bottom (see SbTrackH), so thumb and grip no longer
        // share the bottom-right corner -- both stay grabbable regardless of test order.
        if (OverScrollThumb(mx, my)) { _grab = Grab.Scroll; _gy = my; _gsp = Active.ScrollPx; return true; }
        if (OverResizeGrip(mx, my)) { _grab = Grab.Resize; _gx = mx; _gy = my; _gw = W; _gh = H; return true; }
        int tab = TabAt(mx, my);
        if (tab >= 0) { ActiveTab = tab; return true; }
        if (my <= Y + TabBarH) { _grab = Grab.Move; _gx = mx - X; _gy = my - Y; return true; }
        return true;   // press inside the body/footer: consume so the world doesn't get it
    }

    /// <summary>Pointer motion. Consumed (true) only mid-drag, where it moves/resizes the window.</summary>
    public bool MouseMove(float mx, float my)
    {
        switch (_grab)
        {
            case Grab.Move: X = mx - _gx; Y = my - _gy; return true;
            case Grab.Resize:
                W = System.Math.Max(MinW, _gw + (mx - _gx));
                H = System.Math.Max(MinH, _gh + (my - _gy));
                return true;
            case Grab.Scroll:
            {
                // Absolute anchor: map the pointer's travel from the grab straight to ScrollPx (drag DOWN ->
                // toward newest -> ScrollPx down). Incremental deltas would accumulate error past the clamp.
                if (ThumbRect(out _, out float th))
                {
                    float travel = SbTrackH - th;
                    if (travel > 0f)
                    {
                        float sp = _gsp - (my - _gy) * MaxScroll / travel;
                        Active.ScrollPx = sp < 0f ? 0f : (sp > MaxScroll ? MaxScroll : sp);
                    }
                }
                return true;
            }
            default: return false;
        }
    }

    /// <summary>Pointer release. Consumed iff we consumed the matching press -- otherwise the engine
    /// owns this button pair (e.g. a world hold-drag that happens to release over the box), and eating
    /// the up would leave it thinking the button is still held.</summary>
    public bool MouseUp(float mx, float my)
    {
        bool c = _grab != Grab.None || _pressConsumed;
        _grab = Grab.None;
        _pressConsumed = false;
        return c;
    }

    /// <summary>Scroll by a pixel delta: positive = up, into history. Clamps the low end (bottom) here; the
    /// renderer clamps the high end once it has measured the content height.</summary>
    public void ScrollByPx(float dpx)
    {
        float o = Active.ScrollPx + dpx;
        Active.ScrollPx = o < 0f ? 0f : o;
    }

    /// <summary>Body rectangle: everything between the tab bar and the frame's bottom. Since #25 the input
    /// strip pops out <em>below</em> the frame, so the body keeps its full height whether or not it shows.</summary>
    public void BodyRect(out float bx, out float by, out float bw, out float bh)
    {
        bx = X; by = Y + TabBarH; bw = W; bh = H - TabBarH;
    }

    /// <summary>Off-device proof of the drag/resize/tab/scroll state machine.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"ChatWindow.SelfCheck FAIL: {w}"); } }

        var tabs = new[] { new Tab("All", 0xffffffff), new Tab("Talk", 1u << 1) };
        var win = new ChatWindow(100, 100, 400, 300, tabs);

        // move: grab the empty tab-bar area (past both tabs), drag +50/+30
        E(win.MouseDown(100 + 2 * TabW + 5, 105), "down on move handle");
        E(win.Dragging, "dragging after move-grab");
        win.MouseMove(100 + 2 * TabW + 55, 135);
        E(win.X == 150 && win.Y == 130, "moved by drag delta");
        E(win.MouseUp(0, 0) && !win.Dragging, "release ends move");

        // resize: grab bottom-right grip, drag out +100/+80
        float rx = win.X + win.W, ry = win.Y + win.H;
        E(win.MouseDown(rx - 2, ry - 2), "down on resize grip");
        win.MouseMove(rx + 98, ry + 78);
        E(win.W == 500 && win.H == 380, "resized by drag delta");
        win.MouseUp(0, 0);

        // resize honours the minimum size
        var small = new ChatWindow(0, 0, MinW, MinH, tabs);
        small.MouseDown(MinW - 2, MinH - 2);
        small.MouseMove(-500, -500);
        E(small.W == MinW && small.H == MinH, "resize clamped to minimum");

        // tab switch: click the second tab region (no grab)
        E(win.MouseDown(win.X + TabW + 5, win.Y + 5), "down on tab 1");
        E(win.ActiveTab == 1 && !win.Dragging, "tab switched, no grab");

        // scroll: pixel offset up, then over-scroll down clamps at the bottom (low end clamped here; the
        // renderer clamps the high end once it knows the content height)
        win.ActiveTab = 0; win.Active.ScrollPx = 0f;
        win.ScrollByPx(50f); E(win.Active.ScrollPx == 50f, "scroll up by pixels");
        win.ScrollByPx(-80f); E(win.Active.ScrollPx == 0f, "scroll clamps at bottom");

        // scrollbar thumb: geometry + absolute-anchor drag (against renderer-written ContentH/ViewH). ViewH
        // is the real body height (frame minus tab bar) so the thumb/resize-grip separation is exercised.
        win.ViewH = win.H - TabBarH; win.ContentH = win.ViewH * 3f;   // 3x overflow, MaxScroll = 2*ViewH
        win.Active.ScrollPx = 0f;
        E(win.ThumbRect(out float tt0, out float th0), "thumb shown when content overflows");
        // Bug fix: at ScrollPx 0 (bottom-pinned) the thumb must clear the resize grip -- they used to share
        // the bottom-right corner and steal each other's clicks.
        E(tt0 + th0 <= win.Y + win.H - ResizeGrip + 0.5f, "bottom-pinned thumb clears the resize grip");
        E(win.MouseDown(win.X + win.W - 3, win.Y + win.H - 3), "grip-corner press consumed");
        E(!win.ScrollDragging, "grip-corner press is a resize, not a scroll");
        win.MouseUp(0, 0);
        // absolute-anchor drag: grab the thumb, drag up the full track travel -> oldest.
        win.Active.ScrollPx = 0f;
        E(win.ThumbRect(out float tt1, out float th1), "thumb still shown");
        float travel = win.SbTrackH - th1;
        E(win.MouseDown(win.X + win.W - 3, tt1 + 2), "down on thumb grabs the scrollbar");
        E(win.ScrollDragging, "scroll-dragging after thumb grab");
        win.MouseMove(win.X + win.W - 3, tt1 + 2 - travel);
        E(System.Math.Abs(win.Active.ScrollPx - win.MaxScroll) < 0.5f, "drag up maps to MaxScroll (oldest)");
        win.MouseUp(0, 0);
        win.ContentH = 100f; win.ViewH = 300f;   // content fits -> no thumb, no divide blow-up
        E(!win.ThumbRect(out _, out _), "no thumb when content fits");
        win.ContentH = 0f; win.ViewH = 0f; win.Active.ScrollPx = 0f;   // leave state clean

        // channel dropdown (#25): strip lives below the frame, only while typing.
        win.FooterVisible = true;
        float sy = win.StripTop;                                  // top of the pop-out strip
        E(win.MouseDown(win.X + 5, sy + 5), "click dropdown button consumes");
        E(win.ChannelListOpen, "dropdown opens");
        // list stacks upward: item 2 (Whisper) sits at StripTop - 3*FooterH .. -2*FooterH
        float item2Y = sy - FooterH * 3 + 2;
        E(win.MouseDown(win.X + 5, item2Y), "click list item consumes");
        E(!win.ChannelListOpen && win.SendChannel == Channel.Whisper, "item selected + list closed");
        // opening then clicking off the list dismisses without changing the channel
        win.MouseDown(win.X + 5, sy + 5); E(win.ChannelListOpen, "reopen");
        win.MouseDown(win.X + win.W - 5, win.Y + win.H - 5); E(!win.ChannelListOpen, "click-off dismisses");
        E(win.SendChannel == Channel.Whisper, "channel unchanged on dismiss");

        // Input-on-top: the strip sits just above the frame; the button is at StripTop, the list stacks
        // DOWNWARD from the frame top over the body (the opposite of below-mode).
        win.SendChannel = Channel.Talk; win.InputOnTop = true; win.FooterRows = 1;
        float syT = win.StripTop;
        E(System.Math.Abs(syT - (win.Y - FooterH)) < 0.001f, "above-mode strip sits one row above the frame");
        E(win.MouseDown(win.X + 5, syT + 5), "above: dropdown button consumes");
        E(win.ChannelListOpen, "above: dropdown opens");
        float item2YT = win.ChannelItemTop(2) + 2;             // Whisper, stacked downward
        E(win.MouseDown(win.X + 5, item2YT), "above: list item consumes");
        E(!win.ChannelListOpen && win.SendChannel == Channel.Whisper, "above: item selected + list closed");
        win.InputOnTop = false;
        win.FooterVisible = false;
        win.ActiveTab = 0;

        // Gear/options moved to SettingsWindow (its own SelfCheck); the gear press is claimed by the shell.

        // a press outside the window is not consumed
        E(!win.MouseDown(-10, -10), "outside press ignored");

        // a release whose press missed the window is NOT consumed (the engine owns that button pair)
        win.MouseDown(win.X - 50, win.Y - 50);
        E(!win.MouseUp(win.X + 5, win.Y + 5), "release after outside press not consumed");

        // Scale invariance (#font-clip): chrome must track Metrics.Scale so text never outgrows its box. If a
        // metric is ever reverted to a bare const this catches it. Save/restore -- self-checks run at Scale 1.
        float s0 = Metrics.Scale;
        float h1 = TabBarH; Metrics.Scale = 2f;
        E(System.Math.Abs(TabBarH - 2f * h1) < 1e-3f && System.Math.Abs(FooterH - 2f * 22f) < 1e-3f
          && System.Math.Abs(GearW - TabBarH) < 1e-3f, "tab/footer/gear scale with Metrics.Scale");
        Metrics.Scale = s0;

        log.Info(ok ? "ChatWindow.SelfCheck: passed" : "ChatWindow.SelfCheck: FAILED");
    }
}

/// <summary>Render-component toggle bits (see the render pipeline in WindowRenderer).</summary>
public static class ChatToggle
{
    public const uint Timestamp = 1u << 0;
    public const uint MessageBody = 1u << 1;
    /// <summary>Draw a speaker portrait in a left gutter on lines that carry a portrait resref (#26).</summary>
    public const uint Portrait = 1u << 2;
    // ponytail: SenderName is the next component (post-floor); add a bit + a component.
}
