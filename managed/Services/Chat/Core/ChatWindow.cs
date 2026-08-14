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

    /// <summary>The shell sets this each rebuild = own-input && typing. The input strip + dropdown only
    /// exist (render + hit-test) while it's true.</summary>
    public bool FooterVisible;

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

    // Layout metrics (pixels). ponytail: fixed for v1; a skin/theme is the ceiling.
    public const float TabBarH = 22f;
    public const float TabW = 74f;
    public const float FooterH = 22f;
    public const float DropdownW = 92f;   // channel selector at the strip's left edge
    public const float Pad = 6f;
    public const float ResizeGrip = 14f;
    public const float MinW = 200f, MinH = 120f;
    /// <summary>Scrollbar track width (px): a thin gutter at the body's right edge.</summary>
    public const float ScrollbarW = 8f;
    /// <summary>Smallest thumb, so it stays grabbable when the history is very long.</summary>
    public const float SbMinThumb = 24f;

    /// <summary>Total laid-out content height and the body (viewport) height, written by
    /// <see cref="WindowRenderer.Build"/> each rebuild so the scrollbar hit-test and thumb-drag map against
    /// the exact layout the draw used. 0 until the first build.</summary>
    public float ContentH, ViewH;
    /// <summary>How far the content can scroll up before the oldest line reaches the top (0 when it all fits).</summary>
    public float MaxScroll => ContentH > ViewH ? ContentH - ViewH : 0f;

    /// <summary>Thumb height (px), floored at <see cref="SbMinThumb"/>. Only meaningful when the content
    /// overflows the body.</summary>
    private float ThumbHeight()
    {
        float h = ViewH * ViewH / ContentH;
        return h < SbMinThumb ? SbMinThumb : h;
    }

    /// <summary>Thumb rect (<paramref name="top"/>, <paramref name="height"/>) in screen px, or false when
    /// nothing scrolls (content fits). Sits at the bottom (newest) at ScrollPx 0 and rises to the top as
    /// ScrollPx approaches <see cref="MaxScroll"/>.</summary>
    public bool ThumbRect(out float top, out float height)
    {
        top = 0f; height = 0f;
        if (ContentH <= ViewH || ViewH <= 0f) return false;
        height = ThumbHeight();
        float frac = MaxScroll > 0f ? Active.ScrollPx / MaxScroll : 0f;   // 0 = newest/bottom, 1 = oldest/top
        top = Y + TabBarH + (1f - frac) * (ViewH - height);
        return true;
    }

    /// <summary>True when the pointer is over the scrollbar thumb (the grab zone).</summary>
    public bool OverScrollThumb(float mx, float my)
    {
        if (!ThumbRect(out float top, out float h)) return false;
        return mx >= X + W - ScrollbarW && mx <= X + W && my >= top && my <= top + h;
    }

    /// <summary>The pop-out input strip below the frame: <c>[X, Y+H, W, FooterH]</c> (#25). Only live while
    /// <see cref="FooterVisible"/>.</summary>
    public float StripTop => Y + H;
    /// <summary>The dropdown button (closed state) at the strip's left.</summary>
    public bool OverDropdownButton(float mx, float my)
        => mx >= X && mx < X + DropdownW && my >= StripTop && my < StripTop + FooterH;
    /// <summary>Channel-list item under the pointer while open, or -1. Items stack upward from the strip
    /// over the body, one <see cref="FooterH"/> tall each.</summary>
    public int ChannelItemAt(float mx, float my)
    {
        if (mx < X || mx >= X + DropdownW) return -1;
        for (int i = 0; i < SendChannels.Length; i++)
        {
            float top = StripTop - FooterH * (i + 1);
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
    public bool OverResizeGrip(float mx, float my)
        => mx >= X + W - ResizeGrip && mx <= X + W && my >= Y + H - ResizeGrip && my <= Y + H;

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
        // Thumb before the resize grip: the two overlap in the bottom-right (the 8px scrollbar column runs
        // into the 14px grip), and a bottom-pinned thumb must stay grabbable there.
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
                    float travel = ViewH - th;
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

        // scrollbar thumb: geometry + absolute-anchor drag (against renderer-written ContentH/ViewH).
        win.ContentH = 900f; win.ViewH = 300f;   // 3x overflow -> thumb ~1/3 the track, MaxScroll 600
        win.Active.ScrollPx = 0f;
        E(win.ThumbRect(out float tt0, out float th0), "thumb shown when content overflows");
        E(System.Math.Abs(th0 - 100f) < 0.5f, "thumb height = view*view/content");
        E(System.Math.Abs(tt0 - (win.Y + TabBarH + (win.ViewH - th0))) < 0.5f, "thumb pinned to bottom at newest");
        float travel = win.ViewH - th0;
        E(win.MouseDown(win.X + win.W - 3, tt0 + 2), "down on thumb grabs the scrollbar");
        E(win.ScrollDragging, "scroll-dragging after thumb grab");
        win.MouseMove(win.X + win.W - 3, tt0 + 2 - travel);   // drag up the full travel -> oldest
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
        win.FooterVisible = false;

        // a press outside the window is not consumed
        E(!win.MouseDown(-10, -10), "outside press ignored");

        // a release whose press missed the window is NOT consumed (the engine owns that button pair)
        win.MouseDown(win.X - 50, win.Y - 50);
        E(!win.MouseUp(win.X + 5, win.Y + 5), "release after outside press not consumed");

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
