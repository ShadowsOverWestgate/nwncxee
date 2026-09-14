using System.Collections.Generic;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Chat.Core;

/// <summary>
/// A movable/resizable chat window: rectangle, tabs, and the pointer drag/resize state machine (all
/// pure -- pixels in, rect out, no native refs, so it self-checks off-device). v1 runs exactly one;
/// every draw/input path takes a <see cref="ChatWindow"/> so multi-window later is a list + a loop.
/// Coordinates are screen pixels, y-down (matching the render ortho and SDL mouse coords).
/// </summary>
public sealed class ChatWindow
{
    public float X, Y, W, H;

    /// <summary>Grid snap: pitch in px (0 = off), and the anchor center for each axis. Set by
    /// <see cref="Quickbar.QuickbarService.Tick"/> each frame from the live config + viewport.</summary>
    public static float SnapPx, SnapCX, SnapCY;
    private static float SnapX(float v) => SnapCX + (float)System.Math.Round((v - SnapCX) / SnapPx) * SnapPx;
    private static float SnapY(float v) => SnapCY + (float)System.Math.Round((v - SnapCY) / SnapPx) * SnapPx;

    /// <summary>Edge snap: magnet threshold in px (0 = off) and viewport bounds. Set by
    /// <see cref="Quickbar.QuickbarService.Tick"/> each frame.</summary>
    public static float EdgeThresh, EdgeVpW, EdgeVpH;
    internal const float EdgeMagnet = 12f;

    /// <summary>Applies the live grid + edge snap to an externally-owned window rect (top-left x/y, size w/h).
    /// Lets other extender windows (e.g. the dialog window) obey the same global snap settings.</summary>
    public static void SnapRect(ref float x, ref float y, float w, float h)
    {
        if (SnapPx > 0) { x = SnapX(x); y = SnapY(y); }
        if (EdgeThresh > 0)
        {
            if (System.Math.Abs(x) < EdgeThresh) x = 0;
            else if (System.Math.Abs(x + w - EdgeVpW) < EdgeThresh) x = EdgeVpW - w;
            if (System.Math.Abs(y) < EdgeThresh) y = 0;
            else if (System.Math.Abs(y + h - EdgeVpH) < EdgeThresh) y = EdgeVpH - h;
        }
    }

    /// <summary>Window-to-window edge snap: pulls the dragged rect (x/y mutated) flush against nearby window
    /// rects, on whichever axis has the closer edge. Runs AFTER grid/screen snap so a flush neighbour wins over
    /// the lattice. Gated by the caller on the edge-snap setting; uses the same <see cref="EdgeMagnet"/>.</summary>
    public static void SnapToRects(ref float x, ref float y, float w, float h, (float X, float Y, float W, float H)[] rects)
    {
        float m = EdgeMagnet;
        float bestDx = m, snapX = x;
        float bestDy = m, snapY = y;
        foreach (var o in rects)
        {
            // Only snap on an axis when the perpendicular ranges overlap (or nearly touch) -- i.e. we're actually
            // beside the other window, not diagonally offset. The +m slack lets flush-stacked edges also align.
            if (y < o.Y + o.H + m && o.Y < y + h + m)
            {
                Consider(x, o.X - w, ref bestDx, ref snapX);          // our right edge to their left (flush before)
                Consider(x, o.X + o.W, ref bestDx, ref snapX);        // our left edge to their right (flush after)
                Consider(x, o.X, ref bestDx, ref snapX);              // left edges aligned
                Consider(x, o.X + o.W - w, ref bestDx, ref snapX);    // right edges aligned
            }
            if (x < o.X + o.W + m && o.X < x + w + m)
            {
                Consider(y, o.Y - h, ref bestDy, ref snapY);          // our bottom edge to their top (flush above)
                Consider(y, o.Y + o.H, ref bestDy, ref snapY);        // our top edge to their bottom (flush below)
                Consider(y, o.Y, ref bestDy, ref snapY);              // top edges aligned
                Consider(y, o.Y + o.H - h, ref bestDy, ref snapY);    // bottom edges aligned
            }
        }
        x = snapX; y = snapY;
    }

    /// <summary>Keeps <paramref name="cand"/> if it is the nearest within-magnet candidate seen so far.</summary>
    private static void Consider(float p, float cand, ref float best, ref float snap)
    {
        float d = System.Math.Abs(p - cand);
        if (d < best) { best = d; snap = cand; }
    }

    /// <summary>This window's tabs, swapped atomically so the ingest thread (which reads <see cref="Active"/>
    /// and iterates <see cref="Tabs"/> during <c>Rebuild</c>) always sees a consistent snapshot even while the
    /// input thread adds/removes tabs. The &gt;=1-tab invariant is enforced by the mutation methods.</summary>
    public IReadOnlyList<Tab> Tabs => _tabs;
    private volatile Tab[] _tabs;
    public volatile int ActiveTab;

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

    // ---- per-window settings (#41): each window can look different (unobtrusive combat log vs prominent chat) ----

    /// <summary>Colour overrides keyed by <see cref="ChatColor"/> (RGBA8). Absent key = renderer default.
    /// Per-window so e.g. a system log can be dim while player chat is vivid.</summary>
    public readonly System.Collections.Generic.Dictionary<string, uint> Colors = new();

    /// <summary>Per-window animation master toggle. When false, lines never fade and slides snap.</summary>
    public bool AnimEnabled = true;
    /// <summary>Per-window hold/fade/ease durations.</summary>
    public float HoldSec = 60f, FadeSec = 6f, TauMs = 70f;
    /// <summary>Per-window fade-clock reset: scrolling this window makes its faded messages reappear.</summary>
    public System.DateTime LastScroll = System.DateTime.MinValue;
    /// <summary>Per-window text outline (dark halo behind body glyphs for readability).</summary>
    public bool Outline;
    /// <summary>Per-window timestamp presentation.</summary>
    public TsFormat TimeFormat = TsFormat.HourMin;
    public TsSource TimeSource = TsSource.Local;
    /// <summary>Per-window message body text alignment.</summary>
    public TextAlign Align = TextAlign.Left;
    /// <summary>Per-window background fill style (solid, gradient, stripes).</summary>
    public BgMode BgMode = BgMode.Solid;
    /// <summary>Hide the tab bar when the pointer is not over the window.</summary>
    public bool AutoHideHeader = true;
    /// <summary>When true, the window cannot be moved, resized, or have its tabs dragged (#43).</summary>
    public bool Locked;
    /// <summary>When true, incoming Tell auto-opens route here instead of the focused window (#46).</summary>
    public bool TellsHere;

    /// <summary>Pass-1 wrap cache: block heights + content height, keyed on (width, content, settings).
    /// Volatile swap so concurrent builds see a consistent snapshot.</summary>
    internal volatile LayoutCache? WrapCache;

    /// <summary>Per-window dirty flag (#53 comment finding #1). When false, <see cref="ChatService.Rebuild"/>
    /// reuses the last-built frame for this window. Cleared before building, so a concurrent ingest-set survives.
    /// The focused window is always built regardless.</summary>
    public volatile bool Dirty = true;

    /// <summary>Clickable portrait rects, written by Build each rebuild. Input-thread hit-testing reads these
    /// to route portrait clicks to the tell flow. Empty when portraits are off or suppressed.</summary>
    // ponytail: stale line PlayerId goes dead after relog; rects die with store.Clear at session boundaries
    public volatile PortraitHit[] PortraitHits = System.Array.Empty<PortraitHit>();

    /// <summary>Index into <see cref="PortraitHits"/> the pointer is over, or -1. Set by the motion path;
    /// Build reads it to highlight the hovered frame.</summary>
    public int HoveredPortrait = -1;

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

    /// <summary>The GUIINGAME_MESSAGE_* flag for a sendable channel (the same bits <see cref="FilterChannel"/>
    /// uses, so the tab mask test is a direct AND).</summary>
    public static uint Bit(Channel ch) => ch switch
    {
        Channel.Talk    => 0x001,
        Channel.Shout   => 0x004,
        Channel.Whisper => 0x008,
        Channel.Tell    => 0x002,
        Channel.Party   => 0x040,
        Channel.DM      => 0x400,
        _ => 0,
    };

    /// <summary>Can the active tab display (and therefore send on) <paramref name="ch"/>?</summary>
    public bool CanSend(Channel ch) => (Active.ChannelMask & Bit(ch)) != 0;

    /// <summary>Can <paramref name="ch"/> appear in the channel dropdown? Tell is hidden when the tab has
    /// other sendable channels — tells are sent via portrait click, /t command, or dedicated tabs (#46).</summary>
    public bool CanSelect(Channel ch)
    {
        if (!CanSend(ch)) return false;
        if (ch != Channel.Tell) return true;
        foreach (Channel o in SendChannels)
            if (o != Channel.Tell && CanSend(o)) return false;
        return true;
    }

    /// <summary>True when the active tab shows at least one sendable channel (else no input line).</summary>
    public bool HasSendable
    {
        get { foreach (Channel ch in SendChannels) if (CanSend(ch)) return true; return false; }
    }

    /// <summary>If <see cref="SendChannel"/> is not visible in the active tab, move it to the first visible
    /// selectable channel (or leave it — no footer will show anyway). Tell-only pinned tabs auto-set Tell.</summary>
    public void ClampSendChannel()
    {
        if (Active.PinnedName.Length > 0 && CanSelect(Channel.Tell)) { SendChannel = Channel.Tell; return; }
        if (CanSelect(SendChannel)) return;
        foreach (Channel ch in SendChannels) if (CanSelect(ch)) { SendChannel = ch; return; }
    }

    // Layout metrics: design pixels at Metrics.Scale==1, multiplied by the live scale so the chrome tracks the
    // font size + game UI scale in lockstep (no clipping at larger sizes). ponytail: no per-metric tuning; one
    // uniform scale keeps every text-to-chrome ratio fixed, which is all the clipping fix needs.
    public static float TabBarH => 22f * Metrics.Scale;
    /// <summary>Maximum tab width (when few tabs). Tabs shrink to share the bar evenly.</summary>
    public static float MaxTabW => 120f * Metrics.Scale;
    /// <summary>Whether this window can be closed (non-Main). Set by the layout owner.</summary>
    public bool Closeable;
    /// <summary>Whether the close button is shown (non-Main and not locked).</summary>
    public bool ShowClose => Closeable && !Locked;
    /// <summary>Total width reserved at the tab bar's right edge for buttons: [+] [⚙] [×] when closeable,
    /// [+] [⚙] when not.</summary>
    public float ButtonsW => (ShowClose ? 3f : 2f) * GearW;
    /// <summary>Actual tab width for this window: bar minus button area, divided by tab count, capped at max.
    /// Takes the snapshotted tab count to avoid a torn read when a concurrent thread swaps _tabs.</summary>
    public float TabWFor(int tabCount) => System.Math.Min(MaxTabW, (W - ButtonsW) / (tabCount < 1 ? 1 : tabCount));
    /// <summary>Convenience: TabW from the live tab count (safe when only one thread is reading).</summary>
    public float TabW => TabWFor(_tabs.Length);
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
    /// <summary>Channel-list item under the pointer while open: returns the <see cref="SendChannels"/>
    /// index of the hit channel, or -1. Only selectable rows are hit-testable (Tell hidden when other
    /// channels exist); the row counter runs over visible entries so the geometry matches the renderer.</summary>
    public int ChannelItemAt(float mx, float my)
    {
        if (mx < X || mx >= X + DropdownW) return -1;
        int row = 0;
        for (int i = 0; i < SendChannels.Length; i++)
        {
            if (!CanSelect(SendChannels[i])) continue;
            float top = ChannelItemTop(row);
            if (my >= top && my < top + FooterH) return i;
            row++;
        }
        return -1;
    }

    private enum Grab { None, Move, Resize, Scroll, TabHold, TabDrag }
    private Grab _grab;
    /// <summary>Other windows' rects captured at move-drag start, for window-to-window snapping.</summary>
    private (float X, float Y, float W, float H)[] _snapRects = System.Array.Empty<(float, float, float, float)>();
    private bool _pressConsumed;        // did we consume the matching mouse-down? (button-pair ownership)
    private float _gx, _gy, _gw, _gh;   // grab anchor: pointer/rect at mouse-down
    private float _gsp;                  // scroll-grab anchor: ScrollPx at the thumb grab
    private int _dragTabIdx;             // tab index being dragged (#42)
    /// <summary>Pixel distance the pointer must travel from the tab press before drag activates.</summary>
    private const float TabDragThreshold = 6f;

    /// <summary>True while a tab is being dragged (for cross-window drop detection).
    /// The hold phase (pre-threshold) is not a drag — no reorder, no cross-window drop.</summary>
    public bool TabDragActive => _grab == Grab.TabDrag;

    public ChatWindow(float x, float y, float w, float h, Tab[] tabs)
    {
        if (tabs.Length == 0) throw new System.ArgumentException("a window needs >=1 tab", nameof(tabs));
        X = x; Y = y; W = w; H = h; _tabs = (Tab[])tabs.Clone();
    }

    /// <summary>The active tab. Bounds-checks against the snapshot so a stale <see cref="ActiveTab"/>
    /// (visible between the two volatile writes on a mutation) falls back to tab 0 instead of throwing.</summary>
    public Tab Active { get { Tab[] t = _tabs; int i = ActiveTab; return t[(uint)i < (uint)t.Length ? i : 0]; } }

    /// <summary>Append a tab (atomic array swap).</summary>
    public void AddTab(Tab t)
    {
        Tab[] old = _tabs;
        var next = new Tab[old.Length + 1];
        old.CopyTo(next, 0);
        next[old.Length] = t;
        _tabs = next;
    }

    /// <summary>Replace the entire tab list (rejects empty, keeping the &gt;=1 invariant). Resets
    /// <see cref="ActiveTab"/> to 0. Used by the persistence layer to restore saved tabs.
    /// ActiveTab assigned first so a concurrent reader on the old (possibly longer) array never sees
    /// the new (possibly shorter) array with a stale index.</summary>
    public void SetTabs(Tab[] tabs)
    {
        if (tabs.Length == 0) return;
        ActiveTab = 0;
        _tabs = (Tab[])tabs.Clone();
        ClampSendChannel();
    }

    /// <summary>Remove tab <paramref name="index"/>, refusing the last one so a window always keeps &gt;=1 tab
    /// (#36); returns false + no change on the last tab or an out-of-range index. Keeps the same tab active --
    /// removing one before it decrements <see cref="ActiveTab"/>, removing it lands on the tab that slid in.
    /// ActiveTab is written first so a concurrent reader never sees an index past the new array's end.</summary>
    public bool RemoveTab(int index)
    {
        Tab[] old = _tabs;
        if (old.Length <= 1 || (uint)index >= (uint)old.Length) return false;
        int newActive = ActiveTab;
        if (index < newActive) newActive--;
        else if (newActive >= old.Length - 1) newActive = old.Length - 2;
        ActiveTab = newActive;
        var next = new Tab[old.Length - 1];
        System.Array.Copy(old, 0, next, 0, index);
        System.Array.Copy(old, index + 1, next, index, old.Length - index - 1);
        _tabs = next;
        ClampSendChannel();
        return true;
    }

    /// <summary>Move a tab from one position to another within this window (atomic array swap).
    /// <see cref="ActiveTab"/> follows: if the active tab was moved, it tracks to <paramref name="to"/>;
    /// if another tab shifted past it, the index adjusts so the same tab stays active.</summary>
    public void MoveTab(int from, int to)
    {
        Tab[] old = _tabs;
        if ((uint)from >= (uint)old.Length || (uint)to >= (uint)old.Length || from == to) return;
        var next = (Tab[])old.Clone();
        Tab t = next[from];
        if (from < to) System.Array.Copy(old, from + 1, next, from, to - from);
        else System.Array.Copy(old, to, next, to + 1, from - to);
        next[to] = t;
        int a = ActiveTab;
        if (a == from) a = to;
        else if (from < to && a > from && a <= to) a--;
        else if (from > to && a >= to && a < from) a++;
        ActiveTab = a;
        _tabs = next;
    }

    /// <summary>Insert a tab at <paramref name="index"/> (atomic array swap). Clamps to [0, Count].</summary>
    public void InsertTab(Tab t, int index)
    {
        Tab[] old = _tabs;
        if (index < 0) index = 0;
        if (index > old.Length) index = old.Length;
        var next = new Tab[old.Length + 1];
        System.Array.Copy(old, 0, next, 0, index);
        next[index] = t;
        System.Array.Copy(old, index, next, index + 1, old.Length - index);
        _tabs = next;
    }

    /// <summary>Drop position in the tab bar for a cross-window tab drag (#42). Returns the insert
    /// index (0..<see cref="Tabs"/>.Count) if the pointer is over the tab bar, or -1 if outside.</summary>
    public int TabBarDropIndex(float mx, float my)
    {
        if (my < Y || my > Y + TabBarH || mx < X || mx >= X + W - ButtonsW) return -1;
        int n = _tabs.Length;
        float tw = TabWFor(n);
        int i = (int)((mx - X) / tw);
        return i < 0 ? 0 : (i > n ? n : i);
    }

    /// <summary>True while dragging, resizing, or scrubbing the scrollbar -- the shell consumes
    /// pointer-motion only then.</summary>
    public bool Dragging => _grab != Grab.None;

    /// <summary>True while the scrollbar thumb is being dragged (the shell resets the fade clock as it moves).</summary>
    public bool ScrollDragging => _grab == Grab.Scroll;

    public bool Contains(float mx, float my) => mx >= X && mx <= X + W && my >= Y && my <= Y + H;

    /// <summary>Full UI hit-test: frame + input strip + open channel dropdown.</summary>
    public bool HitTestFull(float mx, float my)
    {
        if (Contains(mx, my)) return true;
        if (!FooterVisible) return false;
        if (mx >= X && mx < X + W && my >= StripTop && my < StripTop + FooterHeight) return true;
        if (ChannelListOpen)
        {
            int rows = 0;
            foreach (Channel ch in SendChannels) if (CanSelect(ch)) rows++;
            for (int i = 0; i < rows; i++)
            {
                float top = ChannelItemTop(i);
                if (mx >= X && mx < X + DropdownW && my >= top && my < top + FooterH) return true;
            }
        }
        return false;
    }

    /// <summary>True when the pointer is over the bottom-right resize grip (also the hover-handle zone).</summary>
    /// <summary>Grow the rect to the current scaled minimum. Called when Metrics.Scale changes so a rect saved
    /// at a smaller scale (or restored before the UI scale synced) can't leave the chrome overflowing its box.</summary>
    public void ClampToMin() { if (W < MinW) W = MinW; if (H < MinH) H = MinH; }

    public bool OverResizeGrip(float mx, float my)
        => mx >= X + W - ResizeGrip && mx <= X + W && my >= Y + H - ResizeGrip && my <= Y + H;

    /// <summary>True when the pointer is over the [+] button (leftmost slot).</summary>
    public bool OverNewWin(float mx, float my)
        => mx >= X + W - ButtonsW && mx < X + W - ButtonsW + GearW && my >= Y && my <= Y + TabBarH;

    /// <summary>True when the pointer is over the [⚙] button (second from right, or rightmost when close is hidden).</summary>
    public bool OverGear(float mx, float my)
    {
        float right = ShowClose ? X + W - GearW : X + W;
        float left = right - GearW;
        return mx >= left && mx < right && my >= Y && my <= Y + TabBarH;
    }

    /// <summary>True when the pointer is over the [×] button (rightmost slot). False for Main or locked windows.</summary>
    public bool OverCloseWin(float mx, float my)
        => ShowClose && mx >= X + W - GearW && mx <= X + W && my >= Y && my <= Y + TabBarH;

    /// <summary>Width of each ephemeral tab button (pin, close) at the right edge.</summary>
    public static float TabBtnW => 16f * Metrics.Scale;

    /// <summary>True when <paramref name="mx"/> is in the close-X zone of tab <paramref name="i"/>.</summary>
    public bool TabCloseHit(int i, float mx)
    {
        float tw = TabWFor(_tabs.Length);
        float right = X + (i + 1) * tw;
        return mx >= right - TabBtnW && mx < right;
    }

    /// <summary>True when <paramref name="mx"/> is in the pin zone of tab <paramref name="i"/> (left of close).</summary>
    public bool TabPinHit(int i, float mx)
    {
        float tw = TabWFor(_tabs.Length);
        float right = X + (i + 1) * tw;
        return mx >= right - 2f * TabBtnW && mx < right - TabBtnW;
    }

    /// <summary>Tab index under the pointer in the tab bar, or -1 (bar background = the move handle).</summary>
    private int TabAt(float mx, float my)
    {
        if (my < Y || my > Y + TabBarH) return -1;
        int n = _tabs.Length;
        float tw = TabWFor(n);
        int i = (int)((mx - X) / tw);
        return (i >= 0 && i < n && mx >= X && mx < X + n * tw) ? i : -1;
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
        if (OverResizeGrip(mx, my)) { if (!Locked) { _grab = Grab.Resize; _gx = mx; _gy = my; _gw = W; _gh = H; } return true; }
        int tab = TabAt(mx, my);
        if (tab >= 0)
        {
            if (_tabs[tab].Ephemeral)
            {
                // Close-X: right edge. Not on the last tab (window [×] is the dismiss).
                if (_tabs.Length > 1 && TabCloseHit(tab, mx))
                {
                    RemoveTab(tab);
                    ClampSendChannel();
                    return true;
                }
                // Pin: left of close. Keeps the tab (clears ephemeral).
                if (TabPinHit(tab, mx))
                {
                    _tabs[tab].Kept = true;
                    _pressConsumed = true;
                    return true;
                }
            }
            ActiveTab = tab; _tabs[tab].Unread = false; ClampSendChannel();
            if (!Locked) { _grab = Grab.TabHold; _dragTabIdx = tab; _gx = mx; _gy = my; }
            return true;
        }
        if (my <= Y + TabBarH)
        {
            if (!Locked)
            {
                _grab = Grab.Move; _gx = mx - X; _gy = my - Y;
                _snapRects = EdgeThresh > 0 ? Chat.ChatService.GatherSnapRects(this, true)
                                            : System.Array.Empty<(float, float, float, float)>();
            }
            return true;
        }
        return true;   // press inside the body/footer: consume so the world doesn't get it
    }

    /// <summary>Pointer motion. Consumed (true) only mid-drag, where it moves/resizes the window.</summary>
    public bool MouseMove(float mx, float my)
    {
        switch (_grab)
        {
            case Grab.Move:
                X = mx - _gx; Y = my - _gy;
                if (SnapPx > 0) { X = SnapX(X); Y = SnapY(Y); }
                if (EdgeThresh > 0)
                {
                    if (System.Math.Abs(X) < EdgeThresh) X = 0;
                    else if (System.Math.Abs(X + W - EdgeVpW) < EdgeThresh) X = EdgeVpW - W;
                    if (System.Math.Abs(Y) < EdgeThresh) Y = 0;
                    else if (System.Math.Abs(Y + H - EdgeVpH) < EdgeThresh) Y = EdgeVpH - H;
                    SnapToRects(ref X, ref Y, W, H, _snapRects);   // flush-neighbour snap wins over grid/screen
                }
                return true;
            case Grab.Resize:
                if (SnapPx > 0)
                {
                    W = System.Math.Max(MinW, SnapX(X + _gw + (mx - _gx)) - X);
                    H = System.Math.Max(MinH, SnapY(Y + _gh + (my - _gy)) - Y);
                }
                else
                {
                    W = System.Math.Max(MinW, _gw + (mx - _gx));
                    H = System.Math.Max(MinH, _gh + (my - _gy));
                }
                if (EdgeThresh > 0)
                {
                    if (System.Math.Abs(X + W - EdgeVpW) < EdgeThresh) W = EdgeVpW - X;
                    if (System.Math.Abs(Y + H - EdgeVpH) < EdgeThresh) H = EdgeVpH - Y;
                }
                return true;
            case Grab.TabHold:
            {
                float dx = mx - _gx, dy = my - _gy;
                if (dx * dx + dy * dy < TabDragThreshold * TabDragThreshold) return true;
                _grab = Grab.TabDrag;
                goto case Grab.TabDrag;
            }
            case Grab.TabDrag:
            {
                int n = _tabs.Length;
                float tw = TabWFor(n);
                int target = (int)((mx - X) / tw);
                if (target < 0) target = 0;
                if (target >= n) target = n - 1;
                if (target != _dragTabIdx) { MoveTab(_dragTabIdx, target); _dragTabIdx = target; }
                return true;
            }
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
    /// strip pops out <em>below</em> the frame, so the body keeps its full height whether or not it shows.
    /// When <paramref name="headerVisible"/> is false the body expands upward into the header band.</summary>
    public void BodyRect(out float bx, out float by, out float bw, out float bh, bool headerVisible = true)
    {
        float top = headerVisible ? TabBarH : 0f;
        bx = X; by = Y + top; bw = W; bh = H - top;
    }

    /// <summary>Copy all per-window settings (look, not identity) from <paramref name="src"/>. Tabs and rect
    /// are excluded: tabs are content (the user arranges them per window), rect is position. Deep-copies the
    /// colours dict so editing one window's palette later doesn't alias the other's.</summary>
    public void CopySettingsFrom(ChatWindow src)
    {
        Toggles = src.Toggles;
        InputOnTop = src.InputOnTop;
        Colors.Clear();
        foreach (var kv in src.Colors) Colors[kv.Key] = kv.Value;
        AnimEnabled = src.AnimEnabled;
        HoldSec = src.HoldSec; FadeSec = src.FadeSec; TauMs = src.TauMs;
        Outline = src.Outline;
        TimeFormat = src.TimeFormat; TimeSource = src.TimeSource;
        Align = src.Align;
        BgMode = src.BgMode;
        AutoHideHeader = src.AutoHideHeader;
    }

    /// <summary>Off-device proof of the drag/resize/tab/scroll state machine.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"ChatWindow.SelfCheck FAIL: {w}"); } }

        var tabs = new[] { new Tab("All", 0xffffffff), new Tab("Talk", 0x001) };
        var win = new ChatWindow(100, 100, 400, 300, tabs);

        // move: grab the empty tab-bar area (past both tabs), drag +50/+30
        E(win.MouseDown(100 + 2 * win.TabW + 5, 105), "down on move handle");
        E(win.Dragging, "dragging after move-grab");
        win.MouseMove(100 + 2 * win.TabW + 55, 135);
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

        // tab switch: click the second tab region -> switches, drag only after threshold (#42)
        E(win.MouseDown(win.X + win.TabW + 5, win.Y + 5), "down on tab 1");
        E(win.ActiveTab == 1 && !win.TabDragActive, "tab press switches but does not start drag yet");
        win.MouseMove(win.X + win.TabW + 5 + TabDragThreshold + 1, win.Y + 5);
        E(win.TabDragActive, "drag activates after moving past threshold");
        win.MouseUp(0, 0);

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

        // Tab list mutation (#36/#37): add/remove, the always->=1-tab guard, and active-tab identity.
        var tw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("A", 1u), new Tab("B", 1u), new Tab("C", 1u) });
        tw.AddTab(new Tab("D", 1u)); E(tw.Tabs.Count == 4, "AddTab appends");
        tw.ActiveTab = 2; Tab wasActive = tw.Active;               // C active
        E(tw.RemoveTab(0) && tw.Active == wasActive, "removing a tab before the active one keeps it active");
        E(tw.Tabs.Count == 3 && tw.ActiveTab == 1, "index tracked the removed tab");
        E(!tw.RemoveTab(9), "out-of-range remove is a no-op");
        while (tw.RemoveTab(0)) { }                                // drain toward the floor
        E(tw.Tabs.Count == 1 && !tw.RemoveTab(0), "the final tab is undeletable");
        // SetTabs: replace the whole list, reject empty
        tw.SetTabs(new[] { new Tab("X", 1u), new Tab("Y", 2u) });
        E(tw.Tabs.Count == 2 && tw.Tabs[0].Label == "X" && tw.ActiveTab == 0, "SetTabs replaces all tabs");
        tw.SetTabs(new Tab[0]);
        E(tw.Tabs.Count == 2, "SetTabs rejects empty (keeps old tabs)");
        bool threw = false;
        try { _ = new ChatWindow(0, 0, 400, 300, new Tab[0]); } catch (System.ArgumentException) { threw = true; }
        E(threw, "constructing a window with no tabs is rejected");

        // Tab drag reorder (#42): down on tab, exceed threshold, drag across boundaries.
        var dw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("A", 1u), new Tab("B", 2u), new Tab("C", 4u) });
        float dtw = dw.TabWFor(3);
        E(dw.MouseDown(dtw * 0.5f, 5), "drag: down on tab A");
        E(!dw.TabDragActive && dw.ActiveTab == 0, "drag: hold on A, not yet dragging");
        dw.MouseMove(dtw * 1.5f, 5);
        E(dw.Tabs[0].Label == "B" && dw.Tabs[1].Label == "A" && dw.Tabs[2].Label == "C", "drag: A past B reorders");
        E(dw.ActiveTab == 1, "drag: ActiveTab follows A");
        dw.MouseMove(dtw * 2.5f, 5);
        E(dw.Tabs[0].Label == "B" && dw.Tabs[1].Label == "C" && dw.Tabs[2].Label == "A", "drag: A to end");
        E(dw.ActiveTab == 2, "drag: ActiveTab tracks to end");
        dw.MouseUp(0, 0);
        E(!dw.TabDragActive, "drag: release ends it");

        // MoveTab with a non-active tab: ActiveTab adjusts to stay on the same tab.
        var mt = new ChatWindow(0, 0, 400, 300, new[] { new Tab("A", 1u), new Tab("B", 2u), new Tab("C", 4u) });
        mt.ActiveTab = 1;
        mt.MoveTab(0, 2);
        E(mt.Tabs[0].Label == "B" && mt.Tabs[2].Label == "A", "MoveTab forward shifts");
        E(mt.ActiveTab == 0, "MoveTab: active follows shift-down");
        mt.MoveTab(1, 1);
        E(mt.Tabs[1].Label == "C", "MoveTab same-pos is a no-op");

        // InsertTab at a position.
        var it = new ChatWindow(0, 0, 400, 300, new[] { new Tab("A", 1u), new Tab("B", 2u) });
        it.InsertTab(new Tab("Z", 8u), 1);
        E(it.Tabs.Count == 3 && it.Tabs[1].Label == "Z" && it.Tabs[2].Label == "B", "InsertTab at 1");
        it.InsertTab(new Tab("W", 16u), 99);
        E(it.Tabs[it.Tabs.Count - 1].Label == "W", "InsertTab past end appends");

        // TabBarDropIndex.
        var di = new ChatWindow(100, 100, 400, 300, new[] { new Tab("A", 1u), new Tab("B", 2u) });
        E(di.TabBarDropIndex(100 + di.TabW * 0.5f, 105) == 0, "drop over first tab -> 0");
        E(di.TabBarDropIndex(100 + di.TabW * 1.5f, 105) == 1, "drop over second tab -> 1");
        E(di.TabBarDropIndex(50, 105) == -1, "drop outside -> -1");
        E(di.TabBarDropIndex(100 + 400 - GearW + 1, 105) == -1, "drop on button area -> -1");
        E(di.TabBarDropIndex(100, 50) == -1, "drop above tab bar -> -1");

        // a press outside the window is not consumed
        E(!win.MouseDown(-10, -10), "outside press ignored");

        // a release whose press missed the window is NOT consumed (the engine owns that button pair)
        win.MouseDown(win.X - 50, win.Y - 50);
        E(!win.MouseUp(win.X + 5, win.Y + 5), "release after outside press not consumed");

        // Send-channel constraint (#40 / ADR 0003): Bit mapping, CanSend, HasSendable, clamp, hit-test.
        E(Bit(Channel.Talk) == 0x001 && Bit(Channel.Tell) == 0x002 && Bit(Channel.Shout) == 0x004
          && Bit(Channel.Whisper) == 0x008 && Bit(Channel.Party) == 0x040 && Bit(Channel.DM) == 0x400,
          "Bit maps each Channel to its FilterChannel flag");

        // CanSend + HasSendable against a Talk|DM mask (no Shout/Whisper/Tell/Party).
        var cw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("Narrow", 0x001 | 0x400) });
        E(cw.CanSend(Channel.Talk) && cw.CanSend(Channel.DM), "CanSend true for bits in mask");
        E(!cw.CanSend(Channel.Shout) && !cw.CanSend(Channel.Whisper), "CanSend false for bits not in mask");
        E(cw.HasSendable, "HasSendable true when mask covers at least one sendable");

        // Receive-only mask -> no sendable.
        var ro = new ChatWindow(0, 0, 400, 300, new[] { new Tab("Log", 0x010 | 0x020 | 0x080) });
        E(!ro.HasSendable, "HasSendable false for a receive-only mask");

        // Clamp: current selection outside the mask -> first visible sendable.
        cw.SendChannel = Channel.Shout; cw.ClampSendChannel();
        E(cw.SendChannel == Channel.Talk, "clamp moves invalid selection to first visible sendable");
        cw.SendChannel = Channel.DM; cw.ClampSendChannel();
        E(cw.SendChannel == Channel.DM, "clamp keeps a valid selection");

        // Tab switch clamp: switching to a narrow tab invalidates the selection.
        var sw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff), new Tab("DM", 0x400) });
        sw.SendChannel = Channel.Talk;
        sw.MouseDown(sw.X + sw.TabW + 5, sw.Y + 5);   // click tab 1 (DM-only)
        E(sw.ActiveTab == 1 && sw.SendChannel == Channel.DM, "tab switch clamps SendChannel to tab's mask");
        sw.MouseUp(0, 0);

        // CanSelect: Tell hidden from dropdown when other channels exist.
        E(!cw.CanSelect(Channel.Tell), "CanSelect hides Tell when Talk|DM present");
        var allTab = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
        E(!allTab.CanSelect(Channel.Tell), "CanSelect hides Tell on All-channel tab");
        var tellOnly = new ChatWindow(0, 0, 400, 300, new[] { new Tab("Tell", 0x002) });
        E(tellOnly.CanSelect(Channel.Tell), "CanSelect shows Tell when it's the only channel");

        // Tell-pinned tab auto-sets Tell on clamp.
        var pinWin = new ChatWindow(0, 0, 400, 300,
            new[] { new Tab("All", 0xffffffff), new Tab("Tell:Alice", 0x002) { PinnedName = "Alice" } });
        pinWin.ActiveTab = 1; pinWin.SendChannel = Channel.Talk;
        pinWin.ClampSendChannel();
        E(pinWin.SendChannel == Channel.Tell, "clamp auto-sets Tell on pinned tab");

        // Hit-test with constrained dropdown: Talk|DM mask -> only 2 rows (Tell hidden).
        cw.FooterVisible = true; cw.SendChannel = Channel.Talk;
        cw.MouseDown(cw.X + 5, cw.StripTop + 5); E(cw.ChannelListOpen, "constrained dropdown opens");
        // Row 0 = Talk, row 1 = DM (Shout/Whisper/Tell/Party skipped).
        float r1y = cw.ChannelItemTop(1) + 2;
        cw.MouseDown(cw.X + 5, r1y);
        E(cw.SendChannel == Channel.DM, "row 1 of constrained list selects DM, not Shout");
        cw.FooterVisible = false;

        // Scale invariance (#font-clip): chrome must track Metrics.Scale so text never outgrows its box. If a
        // metric is ever reverted to a bare const this catches it. Save/restore -- self-checks run at Scale 1.
        float s0 = Metrics.Scale;
        float h1 = TabBarH; Metrics.Scale = 2f;
        E(System.Math.Abs(TabBarH - 2f * h1) < 1e-3f && System.Math.Abs(FooterH - 2f * 22f) < 1e-3f
          && System.Math.Abs(GearW - TabBarH) < 1e-3f, "tab/footer/gear scale with Metrics.Scale");
        Metrics.Scale = s0;

        // CopySettingsFrom: deep-copies all look settings, NOT tabs/rect/sendChannel.
        var src = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
        src.Toggles = ChatToggle.Timestamp; src.InputOnTop = true; src.AnimEnabled = false;
        src.HoldSec = 99f; src.FadeSec = 11f; src.TauMs = 200f; src.Outline = true;
        src.TimeFormat = TsFormat.HourMinSec; src.TimeSource = TsSource.Game;
        src.Align = TextAlign.Right;
        src.BgMode = BgMode.StripesH;
        src.AutoHideHeader = false;
        src.Colors["bg"] = 0x112233ffu;
        var dst = new ChatWindow(50, 50, 300, 200, new[] { new Tab("Combat", 0x010) });
        dst.CopySettingsFrom(src);
        E(dst.Toggles == ChatToggle.Timestamp && dst.InputOnTop && !dst.AnimEnabled, "copy: toggles+inputOnTop+anim");
        E(dst.HoldSec == 99f && dst.FadeSec == 11f && dst.TauMs == 200f, "copy: hold/fade/tau");
        E(dst.Outline && dst.TimeFormat == TsFormat.HourMinSec && dst.TimeSource == TsSource.Game, "copy: outline+ts");
        E(dst.Align == TextAlign.Right, "copy: align");
        E(dst.BgMode == BgMode.StripesH, "copy: bgmode");
        E(!dst.AutoHideHeader, "copy: autoHideHeader");
        E(dst.Colors.ContainsKey("bg") && dst.Colors["bg"] == 0x112233ffu, "copy: colour override copied");
        // Deep copy: mutating the source after copy must not affect the destination.
        src.Colors["bg"] = 0xAABBCCDDu;
        E(dst.Colors["bg"] == 0x112233ffu, "copy: deep-copied colours (no alias)");
        E(dst.X == 50 && dst.W == 300, "copy: rect untouched");
        E(dst.Tabs.Count == 1 && dst.Tabs[0].Label == "Combat", "copy: tabs untouched");

        // Lock (#43): prevents move, resize, and tab drag; scroll + tab switch still work.
        var lw = new ChatWindow(100, 100, 400, 300, new[] { new Tab("A", 1u), new Tab("B", 2u) });
        lw.Locked = true;
        E(lw.MouseDown(100 + lw.TabW * 2 + 5, 105), "locked: move-handle press consumed");
        E(!lw.Dragging, "locked: no move grab");
        lw.MouseUp(0, 0);
        E(lw.MouseDown(lw.X + lw.W - 2, lw.Y + lw.H - 2), "locked: resize-grip press consumed");
        E(!lw.Dragging, "locked: no resize grab");
        lw.MouseUp(0, 0);
        E(lw.MouseDown(lw.X + lw.TabW * 1.5f, lw.Y + 5), "locked: tab press consumed");
        E(lw.ActiveTab == 1, "locked: tab switch works");
        E(!lw.TabDragActive, "locked: no tab drag");
        lw.MouseUp(0, 0);
        // Scroll still works when locked.
        lw.ViewH = lw.H - TabBarH; lw.ContentH = lw.ViewH * 3f;
        E(lw.ThumbRect(out float lt, out float lh), "locked: thumb shown");
        E(lw.MouseDown(lw.X + lw.W - 3, lt + 2), "locked: scrollbar grab consumed");
        E(lw.ScrollDragging, "locked: scroll still works");
        lw.MouseUp(0, 0);
        lw.ContentH = 0f; lw.ViewH = 0f;

        // Grid snap: move snaps X/Y, resize snaps the edge, MinW beats snap, off = free.
        float spOld = SnapPx; float cxOld = SnapCX, cyOld = SnapCY;
        SnapPx = 50; SnapCX = 0; SnapCY = 0;
        var gs = new ChatWindow(0, 0, 400, 300, tabs);
        gs.MouseDown(250, 5);                    // move handle (past 2 tabs at 120px each)
        gs.MouseMove(297, 28);                   // raw X=47 → snap 50; raw Y=23 → snap 0
        E(gs.X == 50 && gs.Y == 0, "grid snap: move snaps X/Y");
        gs.MouseUp(0, 0);
        var gs2 = new ChatWindow(50, 0, 400, 300, tabs);
        gs2.MouseDown(gs2.X + gs2.W - 2, gs2.Y + gs2.H - 2);   // resize grip
        gs2.MouseMove(gs2.X + gs2.W - 2 + 27, gs2.Y + gs2.H - 2 + 13); // right=477→500 W=450; bottom=313→300 H=300
        E(gs2.W == 450 && gs2.H == 300, "grid snap: resize snaps the edge");
        gs2.MouseUp(0, 0);
        var gsm = new ChatWindow(0, 0, MinW, MinH, tabs);
        gsm.MouseDown(MinW - 2, MinH - 2);
        gsm.MouseMove(-500, -500);
        E(gsm.W == MinW && gsm.H == MinH, "grid snap: MinW/MinH wins over snap");
        gsm.MouseUp(0, 0);
        SnapPx = 0;
        var gf = new ChatWindow(0, 0, 400, 300, tabs);
        gf.MouseDown(250, 5);
        gf.MouseMove(297, 28);
        E(gf.X == 47 && gf.Y == 23, "grid off: free drag");
        gf.MouseUp(0, 0);
        SnapPx = spOld; SnapCX = cxOld; SnapCY = cyOld;

        // Edge snap: move near edge snaps to edge, resize right/bottom snaps to viewport.
        float etOld = EdgeThresh, evwOld = EdgeVpW, evhOld = EdgeVpH;
        EdgeThresh = EdgeMagnet; EdgeVpW = 1920; EdgeVpH = 1080;
        SnapPx = 0;
        var em = new ChatWindow(0, 0, 400, 300, tabs);
        em.MouseDown(250, 5);
        em.MouseMove(255, 13);   // raw X=5, Y=8 → both < 12, snap to 0,0
        E(em.X == 0 && em.Y == 0, "edge snap: move near top-left snaps to 0,0");
        em.MouseUp(0, 0);
        var em2 = new ChatWindow(0, 0, 400, 300, tabs);
        em2.MouseDown(250, 5);
        em2.MouseMove(1770, 788);   // raw X=1520, right=1920 exact; raw Y=783, bot=1083 ~1080
        E(em2.X == 1920 - 400, "edge snap: move near right snaps to vpW-W");
        E(em2.Y == 1080 - 300, "edge snap: move near bottom snaps to vpH-H");
        em2.MouseUp(0, 0);
        var er = new ChatWindow(100, 100, 400, 300, tabs);
        er.MouseDown(er.X + er.W - 2, er.Y + er.H - 2);
        er.MouseMove(er.X + er.W - 2 + (1920 - 100 - 400 - 5), er.Y + er.H - 2 + (1080 - 100 - 300 - 8));
        E(er.W == 1920 - 100, "edge snap: resize right edge snaps to vpW");
        E(er.H == 1080 - 100, "edge snap: resize bottom edge snaps to vpH");
        er.MouseUp(0, 0);
        EdgeThresh = 0;
        var ef = new ChatWindow(0, 0, 400, 300, tabs);
        ef.MouseDown(250, 5);
        ef.MouseMove(255, 13);
        E(ef.X == 5 && ef.Y == 8, "edge off: free drag");
        ef.MouseUp(0, 0);
        EdgeThresh = etOld; EdgeVpW = evwOld; EdgeVpH = evhOld;

        // Window-to-window snap: flush against a neighbour within the magnet; a distant/non-overlapping one = no pull.
        var obst = new (float, float, float, float)[] { (500f, 100f, 200f, 150f) };
        float wx = 500f - 400f + 3f, wy = 120f;   // our right edge 3px shy of their left; Y ranges overlap
        SnapToRects(ref wx, ref wy, 400f, 300f, obst);
        E(System.Math.Abs(wx - 100f) < 0.01f && System.Math.Abs(wy - 120f) < 0.01f, "winsnap: right edge snaps flush");
        float fx = 50f, fy = 600f;                 // far off + no overlap: untouched
        SnapToRects(ref fx, ref fy, 400f, 300f, obst);
        E(fx == 50f && fy == 600f, "winsnap: distant window does not pull");

        // Ephemeral (#46): Tell-only tab is structurally ephemeral.
        var ew = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff), new Tab("Tell:X", 0x002) });
        E(ew.Tabs[1].Ephemeral, "Tell-only tab is ephemeral");
        E(!ew.Tabs[0].Ephemeral, "non-Tell tab is not ephemeral");
        ew.MouseDown(ew.TabW + 5, 5);
        E(ew.ActiveTab == 1 && ew.Tabs[1].Ephemeral, "tab click keeps ephemeral (X stays)");
        ew.MouseUp(0, 0);
        // Pin button: clicking the pin zone sets Kept, clears ephemeral.
        float pinX = ew.TabW * 2 - 2 * TabBtnW + 2;
        ew.MouseDown(pinX, 5);
        E(ew.Tabs[1].Kept && !ew.Tabs[1].Ephemeral, "pin click sets Kept, clears ephemeral");
        ew.MouseUp(0, 0);
        ew.Tabs[1].Kept = false;

        // Ephemeral close-X (#46): clicking the right edge removes the tab.
        var xw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff), new Tab("Tell:Y", 0x002) });
        float xTabRight = xw.TabW * 2;
        E(xw.MouseDown(xTabRight - 2, 5), "close-X press consumed");
        E(xw.Tabs.Count == 1, "close-X removes the ephemeral tab");
        xw.MouseUp(0, 0);
        // Kept tab: same click region is a normal tab switch, not a close.
        var nw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff), new Tab("Tell:Z", 0x002) { Kept = true } });
        nw.MouseDown(nw.TabW * 2 - 2, 5);
        E(nw.Tabs.Count == 2 && nw.ActiveTab == 1, "kept: right-edge click switches, not closes");
        nw.MouseUp(0, 0);
        // Solo ephemeral tab: close-X is dead (can't remove the last tab; window [×] is the dismiss).
        var sw2 = new ChatWindow(0, 0, 400, 300, new[] { new Tab("Tell:Solo", 0x002) });
        sw2.MouseDown(sw2.TabW - 2, 5);
        E(sw2.Tabs.Count == 1, "solo ephemeral: close-X does not remove the last tab");
        sw2.MouseUp(0, 0);

        log.Info(ok ? "ChatWindow.SelfCheck: passed" : "ChatWindow.SelfCheck: FAILED");
    }
}

