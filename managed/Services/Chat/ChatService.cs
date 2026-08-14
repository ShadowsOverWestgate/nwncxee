using System.Runtime.InteropServices;
using System.Text;
using Sowx.Framework;
using Sowx.Services.Chat.Core;
using Sowx.Services.Chat.Render;

namespace Sowx.Services.Chat;

/// <summary>
/// Native-C# chatbox (map #13, architecture #17). One <see cref="IExtenderService"/>;
/// concerns split into pure Core classes (<c>Chat/Core/</c>, unit-testable, no native
/// refs) behind this thin shell of hooks/GL/SDL. State is static because the
/// <c>[UnmanagedCallersOnly]</c> handlers must be. Coexists with
/// <c>HotkeyInputService</c>; does not absorb it.
/// </summary>
public sealed unsafe class ChatService : IExtenderService
{
    private static ExtenderLog? _log;

    /// <summary>Shared arbitration flag: <c>HotkeyInputService</c> reads this to
    /// suppress gameplay hotkeys while the chat edit control owns the keyboard.
    /// Only ever set true in own-input mode (<see cref="OwnInput"/>).</summary>
    public static volatile bool IsTyping;

    /// <summary>Chat opt-OUT (#23, the extender-optional guarantee): <c>SOWX_LOCKCHAT=1</c> stands the
    /// whole chatbox down -- no tap, render, input capture, or suppression -- so vanilla chat runs
    /// untouched. <see cref="Initialize"/> early-returns on it.</summary>
    public static bool Locked =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOWX_LOCKCHAT"));

    /// <summary>ChatService owns chat input and suppresses vanilla chat (the default). False under
    /// <c>SOWX_LOCKCHAT</c>, where HotkeyInputService instead observes the vanilla chat-focus hooks so
    /// typing still gates gameplay hotkeys. The single pivot both services read.</summary>
    public static bool OwnInput => !Locked;

    /// <summary>True when ChatService owns the SwapBuffers frame present (the default path). False under
    /// <c>SOWX_LOCKCHAT</c> (stood down) or the probe (which claims that hook itself). FrameTapService reads
    /// this to resume its proof-of-life present when nothing else owns the frame -- funchook single-hooks a
    /// symbol, so exactly one owner of SwapBuffers may install.</summary>
    public static bool OwnsFramePresent =>
        !Locked && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOWX_CHATPROBE"));

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        ChatWindow.SelfCheck(log);     // slice #21 drag/resize/tab/scroll proof (off-device)
        WindowRenderer.SelfCheck(log); // #26 portrait-gutter emission proof (off-device)
        ChatAnim.SelfCheck(log);       // map #13 fade/slide/scroll math proof (off-device)
        TextRenderer.SelfCheck(log);   // #33 session-clear drops cached engine texture pointers (off-device)
        if (Locked)   // SOWX_LOCKCHAT: install nothing -> vanilla chat intact; FrameTapService takes the frame
        {
            log.Info("ChatService: SOWX_LOCKCHAT set -- standing down, vanilla chat left intact");
            return;
        }
        InitMessageCore(log, hooks);   // build slice #18
        InitRender(log, hooks);        // build slice #21: own SwapBuffers -> draw the window (bakes the atlas)
        InitSuppression(log, hooks);   // build slice #23: neutralize vanilla chat rendering
        InitEditControl(log, hooks);   // build slice #20
    }

    /// <summary>Backlog every ingested line lands in; tab views read from it (slice #19).</summary>
    private static readonly ChatStore _store = new();

    private static long _ingestCount;

    /// <summary>Log the first line and every Nth after, so a live run leaves visible tap-&gt;store proof.</summary>
    private const int LogEveryNth = 50;

    /// <summary>16-byte CResRef by value. Only r9 is free when it's passed, so SysV puts the whole
    /// struct on the stack (r9 unused) -- declaring it as a struct makes NativeAOT lower it that way.
    /// Passing it as two ulongs instead splits it r9+stack and corrupts the forwarded call.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct CResRef16 { public ulong Lo; public ulong Hi; }

    // AppendToMsgBuffer(this, CExoString* text, uint channel, uint playerId, int hasPortrait, CResRef).
    private static delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void> _tapOrig;

    /// <summary>Slice #18: cold inbound tap on AppendToMsgBuffer -&gt; parse -&gt; ChatStore.Append.</summary>
    private static void InitMessageCore(ExtenderLog log, HookService hooks)
    {
        ColorRunParser.SelfCheck(log);
        ChatStore.SelfCheck(log);

        // GATE: funchook single-hooks a symbol; ChatProbeService claims AppendToMsgBuffer when armed.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOWX_CHATPROBE")))
        {
            // ponytail: probe owns the tap while armed; the two unify in slice 4 when store->render wires up
            log.Info("ChatService: message core deferred to ChatProbeService (SOWX_CHATPROBE set)");
            return;
        }

        _tapOrig = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void>)hooks.Install(
            SymbolMap.AppendToMsgBuffer,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void>)&OnAppend);
        log.Info(_tapOrig != null
            ? "ChatService: inbound tap installed (AppendToMsgBuffer)"
            : "ChatService: inbound tap not installed (Install returned Zero: symbol unresolved or already hooked)");
    }

    [UnmanagedCallersOnly]
    private static void OnAppend(IntPtr self, IntPtr text, uint channel, uint playerId,
                                 int hasPortrait, CResRef16 portrait)
    {
        if (_tapOrig != null) _tapOrig(self, text, channel, playerId, hasPortrait, portrait); // vanilla chat still needs it
        try
        {
            string raw = ReadExo(text);
            string portraitRef = hasPortrait != 0 ? ResRef(portrait) : "";
            // ponytail: senderName left "" -- the tap doesn't carry it; it is inline in raw. Extract in a later slice.
            var line = new ChatLine(channel, "", playerId, portraitRef,
                ColorRunParser.Parse(raw), DateTime.UtcNow, raw);
            _store.Append(line);

            long n = ++_ingestCount;
            // One-shot thread-model probe (#33 follow-up): is ingest really a separate thread from the frame
            // present? If ingest tid == swap tid the "cold network thread" comments are wrong and the store
            // races don't exist. Compare the two [chatthread] lines in a live sowx.log.
            if (n == 1)
            {
                int itid = Environment.CurrentManagedThreadId;
                _log?.Info($"[chatthread] ingest tid={itid} swap tid={_swapTid} same={(itid == _swapTid)}");
            }
            if (n == 1 || n % LogEveryNth == 0)
                _log?.Info($"[chat] ingest #{n} ch=0x{channel:x} pid={playerId} portrait={portraitRef} " +
                           $"runs={line.ColorRuns.Length} text={raw}");
        }
        // An escaped exception aborts the NativeAOT process (the live client), so swallow ingest
        // faults -- but log the first, or a parse/append bug is permanently silent (no ingest ever).
        catch (Exception e) { if (_faults++ == 0) _log?.Info($"[chat] ingest fault (swallowed): {e}"); }
    }

    private static long _faults;

    /// <summary>Decode a CExoString ({char* buf; uint len}) to a string (Latin1 keeps color bytes intact).</summary>
    private static string ReadExo(IntPtr exo)
    {
        if (exo == IntPtr.Zero) return "";
        byte* buf = *(byte**)exo;
        uint len = *(uint*)(exo + 8);
        if (buf == null || len == 0 || len > 4096) return "";
        var sb = new StringBuilder((int)len);
        for (uint k = 0; k < len; k++) sb.Append((char)buf[k]);
        return sb.ToString();
    }

    /// <summary>Decode a 16-byte CResRef (null-padded ASCII) to its resref string.</summary>
    private static string ResRef(CResRef16 r)
    {
        Span<byte> b = stackalloc byte[16];
        BitConverter.TryWriteBytes(b, r.Lo);
        BitConverter.TryWriteBytes(b.Slice(8), r.Hi);
        int n = 0;
        while (n < 16 && b[n] != 0) n++;
        return Encoding.ASCII.GetString(b.Slice(0, n));
    }

    // ---- slice #21: window + owned render -----------------------------------------------------

    /// <summary>The v1 chatbox. ponytail: one window, fixed default rect (not persisted -- a saved
    /// layout is the ceiling). Placed off vanilla's bottom-left so both are visible until #23 suppresses.</summary>
    private static ChatWindow? _window;
    private static delegate* unmanaged<IntPtr, void> _swapOrig;
    private static volatile int _lastMx, _lastMy;   // latest pointer pos, for wheel hit-testing
    private static bool _mdiag;                      // one-shot: log first click coords + rect
    private static bool _pointerInWindow;            // pointer over the window -> draw the resize grip

    // In-game gate: draw the box when a module is loaded and no full-screen options menu is up. The walk is
    // verified against the disassembly of GetInGameGui and IsVisible -- every offset and the 4-byte width:
    //   mgr  = *g_pAppManager           (CAppManager*)
    //   app  = *mgr                     (CClientExoApp* at CAppManager+0)
    //   gui  = *(*(app+0x8)+0x90)       (CClientExoApp::GetInGameGui -- persists, non-null even on the menu)
    //   win  = *(gui+0x8)               (CGuiInGame's chat window -- NULL until a module loads = the in-area signal)
    //   vis  = *(int*)(win+0xc68)       (CGuiInGameChatWindow::IsVisible -- logged only; see below)
    //   opt  = *(int*)(intern+0x604)    (CClientExoApp::SetOptionsPanelOpen flag -- 1 while the ESC menu is up)
    //   show = win != null && opt == 0
    // NB: #23 SkipOriginals CGuiInGameChatWindow::Show, so the vanilla window never activates and `vis`
    // (0xc68) stays 0 -- we can no longer gate on it (that's why it's dropped from `show`, kept only in the
    // [chatgate] log to confirm the Show-suppression took). Module presence + no-ESC-menu is the in-area
    // signal now; the minor cost is the box also shows while the player would have toggled vanilla chat off
    // (moot once we own chat). A null `win` is the main-menu case (no module) => hidden. Hot (every
    // SwapBuffers) and zero-alloc: all pointer reads, guarded at each hop; the [chatgate] log fires only on
    // a state transition (rare, not per-frame).
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    private const int ExoAppInternalOff = 0x8, InGameGuiOff = 0x90, ChatWindowOff = 0x8,
                      ChatVisibleOff = 0xc68, OptionsPanelOpenOff = 0x604, GuiToggledOffOff = 0x3d4;
    private static (IntPtr win, int vis, bool opt, bool hud) _gateSig;

    private static bool InGame()
    {
        IntPtr mgr = _appMgr != IntPtr.Zero ? *(IntPtr*)_appMgr : IntPtr.Zero;
        IntPtr app = mgr != IntPtr.Zero ? *(IntPtr*)mgr : IntPtr.Zero;
        IntPtr intern = app != IntPtr.Zero ? *(IntPtr*)(app + ExoAppInternalOff) : IntPtr.Zero;
        IntPtr gui = intern != IntPtr.Zero ? *(IntPtr*)(intern + InGameGuiOff) : IntPtr.Zero;
        IntPtr win = gui != IntPtr.Zero ? *(IntPtr*)(gui + ChatWindowOff) : IntPtr.Zero;
        int vis = win != IntPtr.Zero ? *(int*)(win + ChatVisibleOff) : 0;   // Show-suppressed -> stays 0; logged only
        bool opt = intern != IntPtr.Zero && *(int*)(intern + OptionsPanelOpenOff) != 0;   // ESC/game menu up
        // H key hides all HUD: CGuiInGame::ToggleOff sets gui+0x3d4=1, UnToggleOff clears it (verified in disasm).
        bool hud = gui != IntPtr.Zero && *(int*)(gui + GuiToggledOffOff) != 0;
        bool show = win != IntPtr.Zero && !opt && !hud;   // in a module, no options menu, HUD not toggled off

        var sig = (win, vis, opt, hud);
        if (sig != _gateSig)
        {
            // Relog/disconnect (chat window went null after being live) resets the send channel to Talk
            // (#25: slash-switched channel persists across messages, but NOT across relogs).
            if (win == IntPtr.Zero && _gateSig.win != IntPtr.Zero)
                ResetSession();   // module unloaded (clean exit or disconnect): drop cached engine ptrs + stale state (#33)
            _gateSig = sig;
            _log?.Info($"[chatgate] win=0x{win:x} vis={vis} opt={opt} hud={hud} -> show={show}");
        }
        return show;
    }

    /// <summary>Saved window rect. ponytail: one flat file of four numbers; a full layout store (per
    /// tab, multi-window) is the ceiling. A property, not a cached field: our loader runs pre-main, so
    /// the engine user dir isn't resolvable at type-init -- resolve live (LoadRect/SaveRect are post-main).</summary>
    private static string LayoutPath => Native.ConfigPath("sowx-chat.cfg");
    private static string _savedRect = "";

    private static void LoadRect(ChatWindow win)
    {
        try
        {
            if (!File.Exists(LayoutPath)) return;
            string[] p = File.ReadAllText(LayoutPath).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (float.TryParse(p[0], ci, out float x) && float.TryParse(p[1], ci, out float y)
                && float.TryParse(p[2], ci, out float w) && float.TryParse(p[3], ci, out float h))
            {
                win.X = x; win.Y = y;
                win.W = w < ChatWindow.MinW ? ChatWindow.MinW : w;
                win.H = h < ChatWindow.MinH ? ChatWindow.MinH : h;
                if (p.Length >= 5 && uint.TryParse(p[4], out uint tg)) win.Toggles = tg;   // restore gear options
                _savedRect = RectStr(win);
            }
        }
        catch (Exception e) { _log?.Info($"[chat] layout load failed: {e.Message}"); }
    }

    private static void SaveRect(ChatWindow win)
    {
        string cur = RectStr(win);
        if (cur == _savedRect) return;   // only write when the rect actually changed
        _savedRect = cur;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LayoutPath)!);
            File.WriteAllText(LayoutPath, cur);
        }
        catch (Exception e) { _log?.Info($"[chat] layout save failed: {e.Message}"); }
    }

    private static string RectStr(ChatWindow w) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{w.X} {w.Y} {w.W} {w.H} {w.Toggles}");

    /// <summary>Slice #21: bake the atlas, build the window, observe ingest, and own SwapBuffers so the
    /// box renders on a plain launch (no env vars) -- unless the probe owns the frame hook.</summary>
    private static void InitRender(ExtenderLog log, HookService hooks)
    {
        // GATE: funchook single-hooks SwapBuffers; ChatProbeService claims it (and the tap) when armed.
        if (!OwnsFramePresent)
        {
            log.Info("ChatService: render deferred to ChatProbeService (SOWX_CHATPROBE set)");
            return;
        }

        TextRenderer.Init(log);   // cold atlas bake (null under run-demo: stbtt absent -> render no-ops)
        // Tabs are pure receive-filters now (#25); the send channel is the dropdown (window-level). ponytail:
        // real per-channel receive masks wait on the [inferred] inbound channel values (research §2.2 fog),
        // so keep All + Talk -- 1<<1 is the one confirmed value.
        _window = new ChatWindow(60, 40, 520, 300, new[]
        {
            new Tab("All",  0xffffffff),
            new Tab("Talk", 1u << 1),
        });
        // Rect restore is deferred to the first frame (see OnSwap): Initialize runs pre-main, so the
        // engine user dir -- where the cfg lives -- isn't resolvable here yet and LoadRect would read
        // the ~/.config fallback instead of the real path.
        _store.AddObserver(OnIngest);  // new line -> slide-in/hold + rebuild (cold network thread)

        _swapOrig = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.SwapBuffers, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnSwap);
        log.Info(_swapOrig != null
            ? "ChatService: window render installed (SwapBuffers)"
            : "ChatService: window render not installed (Install returned Zero: symbol unresolved or already hooked)");
        Rebuild();   // show the empty box immediately, before any chat arrives
    }

    private static bool _frameAnnounced;
    private static int _swapTid;   // managed thread id of the frame-present thread (thread-model probe, #33 follow-up)

    [UnmanagedCallersOnly]
    private static void OnSwap(IntPtr window)
    {
        if (!_frameAnnounced)
        {
            _frameAnnounced = true;
            _swapTid = Environment.CurrentManagedThreadId;
            _log?.Info($"ChatService: first frame presented -- hook is LIVE [chatthread] swap tid={_swapTid}");
            _log?.Info($"ChatService: cfg dir = {Native.UserDirectory() ?? "(engine dir unresolved -> ~/.config fallback)"}");
            LoadRect(_window);   // restore now, not in Initialize: that runs pre-main so the engine user dir was unresolved
            Rebuild();           // re-layout for the restored width
        }
        if (InGame()) TextRenderer.Draw();            // only when the vanilla chat would show; hot, zero-alloc
        if (_swapOrig != null) _swapOrig(window);     // present
    }

    /// <summary>Slice #23: neutralize vanilla chat so only our box draws. Two SkipOriginal groups:
    /// (1) TEXT -- AppendToChatBuffer (new lines) + SetFilter (backlog re-render / reply buttons); the shared
    /// ring buffer is filled by AppendToMsgBuffer BEFORE these run, and that caller also frees the by-value
    /// CExoString temporary (both verified in the disasm), so skipping loses no data and leaks nothing.
    /// (2) CHROME -- CGuiInGameChatWindow::Show + CGuiInGameChatPrompt::Show, so the docked panels and the
    /// input prompt never activate into the render tree (Show pairs with Activate, Hide with Deactivate).
    /// We do NOT call Hide: it clears the window's IsVisible flag (win+0xc68), and rather than mirror that
    /// flag <see cref="InGame"/> now gates on module-presence, so an un-shown vanilla window doesn't blank
    /// our box. Input-side openers are skipped in <see cref="InitEditControl"/>. Skipped under the probe
    /// (leaves vanilla drawing as its backdrop); never reached under SOWX_LOCKCHAT.</summary>
    private static void InitSuppression(ExtenderLog log, HookService hooks)
    {
        if (!OwnsFramePresent) return;   // probe owns the frame -> leave vanilla chat drawing underneath
        var skip = (IntPtr)(delegate* unmanaged<IntPtr, long>)&OnSuppressed;
        _ = hooks.Install(SymbolMap.AppendToChatBuffer,  skip);   // text: new lines
        _ = hooks.Install(SymbolMap.ChatWindowSetFilter, skip);   // text: backlog re-render + reply buttons
        _ = hooks.Install(SymbolMap.ChatWindowShow,      skip);   // chrome: docked message panels
        _ = hooks.Install(SymbolMap.ChatPromptShow,      skip);   // chrome: the "Talk:" input prompt
        log.Info("ChatService: vanilla chat suppressed (text: AppendToChatBuffer+SetFilter; chrome: Window/Prompt Show)");
    }

    // Skip-original: install the hook, never call the trampoline, so the vanilla path is a no-op. (IntPtr
    // self) ignores any further args -- safe under SysV caller-cleanup since we forward nothing. Return 0:
    // the Itanium mangling hides each real return type, so leave rax = 0 (reads as false/null/nothing-done)
    // in case a caller consumes it. One handler serves every single-`this` suppression target.
    [UnmanagedCallersOnly] private static long OnSuppressed(IntPtr self) => 0;

    /// <summary>New line ingested: react by the appended line's own laid-out height (block + gap), then
    /// publish ONCE. Pinned to the bottom (reading live): slide the fresh block up into view (map #13).
    /// Scrolled up (reading history): push ScrollPx by the same amount so the visible lines hold still. A
    /// line filtered out of the active tab changes nothing, so it neither slides nor jiggles the view.
    /// Measuring just the one line (not a whole-backlog delta) makes the compensation exact even under ring
    /// eviction -- the read position tracks the bottom-anchored growth, not the net content change. Cold
    /// (network thread).</summary>
    private static void OnIngest(ChatLine line)
    {
        ChatWindow? win = _window;
        GlyphAtlas? atlas = TextRenderer.Atlas;
        if (win == null || atlas == null) { Rebuild(); return; }   // run-demo: no atlas -> just publish
        if (win.Active.Matches(line))
        {
            float grow = WindowRenderer.MeasureBlock(win, line, atlas) + WindowRenderer.MsgGap;
            if (win.Active.ScrollPx == 0f) ChatAnim.AddResidual(grow);   // pinned: slide the new block in
            else win.Active.ScrollPx += grow;                            // scrolled up: hold the read position
        }
        Rebuild();   // single publish; ScrollPx already adjusted, so no intermediate stale-offset frame
    }

    /// <summary>Rebuild the vertex batch from the current window + store + edit state and publish it.
    /// Cold (ingest / input / resize threads); the hot <see cref="OnSwap"/> only draws what's published.</summary>
    private static void Rebuild()
    {
        ChatWindow? win = _window;
        GlyphAtlas? atlas = TextRenderer.Atlas;
        if (win == null || atlas == null) return;   // run-demo: no atlas -> nothing to draw
        bool footer = IsTyping;          // window always has a send channel (the dropdown)
        win.FooterVisible = IsTyping;                  // gate the strip/dropdown hit-testing to match the draw
        // Only read the (mutable) edit line when a footer will render it -- avoids racing a concurrent
        // keystroke's StringBuilder on the ingest/render thread.
        RenderFrame frame = WindowRenderer.Build(win, _store, atlas, footer,
                                                  footer ? Edit.Text : "", footer ? Edit.Cursor : 0, _pointerInWindow,
                                                  footer ? Edit.Selection : null);
        TextRenderer.Publish(frame);
    }

    /// <summary>Reset all per-session state at a module boundary (#33), fanned in from the gate's
    /// window-drop-to-zero edge (which fires on clean exit and disconnect alike). Crash-critical clear first:
    /// the engine frees its portrait textures when a module unloads, so a cached <c>CAurTexture*</c> would
    /// dangle into the next session and crash when bound (confirmed via core dump). Then the cosmetic clears
    /// so the new session starts visually clean -- no prior lines, no half-open edit/scroll/panel state.
    /// Idempotent and zero-alloc, so it's safe to fire from the (hot) gate read on the transition frame.</summary>
    private static void ResetSession()
    {
        TextRenderer.ClearSessionState();   // crash-critical: freed engine textures must not carry over
        // ponytail: racing a late-arriving ingest (disconnect teardown) can leave <=1 stale line; indices
        // stay in-bounds so it's memory-safe, just cosmetic. Add a lock only if the [chatthread] log proves
        // ingest really runs on a separate thread.
        _store.Clear();                     // cosmetic: no prior session's lines leak in
        Edit.Clear();
        IsTyping = false;
        KeyboardMode(false);                // drop any keyboard capture left from the old session
        if (_window != null)
        {
            _window.SendChannel = Channel.Talk;
            _window.OptionsOpen = false;
            _window.ChannelListOpen = false;
            foreach (Tab t in _window.Tabs) t.ScrollPx = 0f;
        }
    }

    /// <summary>Pointer press/release. Returns true when it lands on the window (the WM shell then
    /// consumes it, so it doesn't fall through to the 3D world). Called from HotkeyInputService.</summary>
    public static bool OnMouseButton(bool down, int button, int x, int y)
    {
        ChatWindow? win = _window;
        if (win == null || !InGame() || button != 1) return false;   // left only; inert on the menu (box hidden)
        if (down && !_mdiag)
        {
            _mdiag = true;
            _log?.Info($"[chat] first click ({x},{y}) rect=({win.X},{win.Y},{win.W},{win.H})");
        }
        bool consumed = down ? win.MouseDown(x, y) : win.MouseUp(x, y);
        if (!down && consumed) SaveRect(win);   // a move/resize (or click) ended -> persist if the rect changed
        if (consumed) Rebuild();
        return consumed;
    }

    /// <summary>Pointer motion. Consumed (true) only while dragging/resizing; also tracks the hover grip.</summary>
    public static bool OnMouseMotion(int x, int y)
    {
        _lastMx = x; _lastMy = y;
        ChatWindow? win = _window;
        if (win == null || !InGame()) return false;   // inert on the menu (box hidden)
        bool consumed = win.MouseMove(x, y);
        if (win.ScrollDragging) ChatAnim.MarkScroll();   // scrubbing the thumb resets the fade (no slide -- tracks 1:1)
        bool inWin = win.Contains(x, y) || win.Dragging;   // keep the grip up mid-drag even if the pointer strays
        if (consumed || inWin != _pointerInWindow) { _pointerInWindow = inWin; Rebuild(); }
        return consumed;
    }

    /// <summary>Lines a wheel notch scrolls.</summary>
    private const float WheelLines = 3f;

    /// <summary>Wheel notch. Consumed (and scrolls) only when the pointer is over the window. Scrolling
    /// resets the fade clock (the messages reappear) and eases the smooth-scroll residual by the pixels the
    /// rebuild actually applied -- so a wheel at either end (nothing applied) doesn't rubber-band.</summary>
    public static bool OnMouseWheel(int dy)
    {
        ChatWindow? win = _window;
        if (win == null || !InGame() || !win.Contains(_lastMx, _lastMy)) return false;
        GlyphAtlas? atlas = TextRenderer.Atlas;
        if (atlas == null) return true;   // consumed, but nothing to scroll before the atlas bakes
        float before = win.Active.ScrollPx;
        win.ScrollByPx(dy * WheelLines * atlas.LineHeight);
        Rebuild();   // Build clamps ScrollPx to [0, contentH-bh]; read back the applied delta
        float applied = win.Active.ScrollPx - before;
        ChatAnim.MarkScroll();
        if (applied != 0f) ChatAnim.AddResidual(-applied);   // ease the body from its pre-wheel position
        return true;
    }

    /// <summary>Slice #20: EditBuffer + IsTyping + focus suppression. Filled by build slice 3.</summary>
    private static void InitEditControl(ExtenderLog log, HookService hooks)
    {
        EditBuffer.SelfCheck(log);     // off-device proof; surfaces in run-demo's sowx log
        ChatCommands.SelfCheck(log);   // slash-command grammar (#25)

        _exoInput  = Native.Sym(SymbolMap.ExoInput);                  // address of g_pExoInput; deref at open
        _kbOn      = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.KeyboardModeOn);
        _kbOff     = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.KeyboardModeOff);
        _startText = (delegate* unmanaged<void>)Native.Sym(SymbolMap.SdlStartTextInput);
        _getClip   = (delegate* unmanaged<IntPtr>)Native.Sym(SymbolMap.SdlGetClipboardText);
        _setClip   = (delegate* unmanaged<byte*, int>)Native.Sym(SymbolMap.SdlSetClipboardText);
        _sdlFree   = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.SdlFree);

        // Outbound send parity (#22). Each CExoString arg is a non-trivial C++ class -> passed by
        // invisible reference, so the ABI slot is a pointer; the callee copies and we own+free it (#14).
        _sendTalk    = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.SendChatTalk);
        _sendShout   = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.SendChatShout);
        _sendWhisper = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.SendChatWhisper);
        _sendParty   = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.SendChatParty);
        _sendDM      = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.SendChatDM);
        _sendTell    = (delegate* unmanaged<IntPtr, uint, IntPtr, void>)Native.Sym(SymbolMap.SendChatTell);

        // Own the three chat openers with skip-original handlers: install the hook but never call
        // through the trampoline, so the vanilla focus slot stays null and never grabs the keyboard.
        // Whichever opener the Enter path enters is patched at its entry, so all three flip IsTyping.
        _ = hooks.Install(SymbolMap.SetFocusChatPrompt, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnOpenFocus);
        _ = hooks.Install(SymbolMap.ShowChatPrompt,     (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnOpenShow);
        _ = hooks.Install(SymbolMap.PrepChatPromptForTell, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnOpenTell);
    }

    /// <summary>The one chat input line; the native shell drives it, all logic is in Core.</summary>
    public static readonly EditBuffer Edit = new();

    private static IntPtr _exoInput;                          // &g_pExoInput (CExoInput**)
    private static delegate* unmanaged<IntPtr, void> _kbOn, _kbOff, _sdlFree;
    private static delegate* unmanaged<void> _startText;
    private static delegate* unmanaged<IntPtr> _getClip;
    private static delegate* unmanaged<byte*, int> _setClip;

    // Chat senders (#22). Talk/Shout/Whisper/Party/DM: (this CNWCMessage*, CExoString*). Tell also
    // takes the target playerId: (this, uint playerId, CExoString*).
    private static delegate* unmanaged<IntPtr, IntPtr, void> _sendTalk, _sendShout, _sendWhisper, _sendParty, _sendDM;
    private static delegate* unmanaged<IntPtr, uint, IntPtr, void> _sendTell;

    // SDL_Keymod bits (match HotkeyInputService): Ctrl/Shift both sides.
    private const int KmodShift = 0x0003;
    private const int KmodCtrl  = 0x00C0;

    // SDL keycodes. Printable ones are their codepoint; nav keys carry SDLK_SCANCODE_MASK (0x40000000).
    private const uint KeyReturn = 0x0D, KeyEsc = 0x1B, KeyBackspace = 0x08, KeyDelete = 0x7F;
    private const uint KeyA = 0x61, KeyC = 0x63, KeyV = 0x76, KeyX = 0x78;
    private const uint KeyRight = 0x4000004F, KeyLeft = 0x40000050, KeyDown = 0x40000051, KeyUp = 0x40000052;
    private const uint KeyHome = 0x4000004A, KeyEnd = 0x4000004D;

    /// <summary>SDL_TextInputEvent.text is char[32]; scan the null-terminated UTF-8 within it.</summary>
    private const int SdlTextMax = 31;

    // Skip-original openers: never call the trampoline (suppress vanilla focus), just take the keyboard.
    [UnmanagedCallersOnly] private static void OnOpenFocus(IntPtr self) => Open();
    [UnmanagedCallersOnly] private static void OnOpenShow(IntPtr self) => Open();
    [UnmanagedCallersOnly] private static void OnOpenTell(IntPtr self, IntPtr player) => Open();

    /// <summary>Take the keyboard: reset the line, start SDL text input, gate gameplay keys.</summary>
    private static void Open()
    {
        IsTyping = true;
        Edit.Clear();
        if (_startText != null) _startText();   // vanilla started it inside the focus path we skip
        KeyboardMode(true);
        Rebuild();   // show the (now-open) footer edit line
    }

    /// <summary>
    /// Release the keyboard. <paramref name="submit"/> recalls the line into history; either way the
    /// line clears and gameplay input resumes.
    /// </summary>
    public static void CloseChat(bool submit)
    {
        if (submit)
        {
            string text = Edit.Submit();   // pushes history + clears the line; returns what was typed
            ChatWindow? win = _window;
            if (win != null && !string.IsNullOrEmpty(text))
            {
                var (switchTo, rest) = ChatCommands.Parse(text);   // "/s hi" -> switch to Shout, send "hi"
                if (switchTo is Channel nc) win.SendChannel = nc;  // persists across messages (dropdown reflects it)
                if (!string.IsNullOrEmpty(rest)) Send(win.SendChannel, null, rest);
            }
        }
        else Edit.Clear();
        IsTyping = false;
        // Do NOT SDL_StopTextInput: the engine leaves text input globally on (it never toggles it) and the
        // debug console + other text fields depend on TEXTINPUT events firing. KeyboardModeOff below is the
        // real gate that stops chars reaching us. Stopping SDL text input here breaks the console.
        KeyboardMode(false);
        Rebuild();   // hide the footer edit line
    }

    /// <summary>Route submitted text to the server on <paramref name="ch"/> via the vanilla
    /// SendPlayerToServerChat_* senders (major 0x09, wire unchanged -> the extender stays optional).
    /// Cold (input thread) so allocation is fine. The callee copies the CExoString; we own + free it.</summary>
    // ponytail: no content is logged (repo's no-keylog stance) -- the send echoes back through the
    // inbound tap, which logs the delivered line, so a live run still proves each channel end-to-end.
    private static void Send(Channel ch, uint? playerId, string text)
    {
        IntPtr msg = ClientApi.Message();
        if (msg == IntPtr.Zero) { _log?.Info($"[chat] send dropped (ch={ch}): no live CNWCMessage yet"); return; }
        IntPtr exo = Native.NewExoString(text);
        try
        {
            switch (ch)
            {
                case Channel.Talk    when _sendTalk    != null: _sendTalk(msg, exo); break;
                case Channel.Shout   when _sendShout   != null: _sendShout(msg, exo); break;
                case Channel.Whisper when _sendWhisper != null: _sendWhisper(msg, exo); break;
                case Channel.Party   when _sendParty   != null: _sendParty(msg, exo); break;
                case Channel.DM      when _sendDM      != null: _sendDM(msg, exo); break;
                case Channel.Tell:
                    if (playerId is uint pid && _sendTell != null) _sendTell(msg, pid, exo);
                    else _log?.Info("[chat] tell dropped: tab carries no target playerId");   // never reroute to Talk
                    break;
                default: _log?.Info($"[chat] send dropped (ch={ch}): sender symbol unresolved"); break;
            }
        }
        finally { Native.FreeExoString(exo); }
    }

    /// <summary>Insert finished UTF-8 codepoints (SDL_TEXTINPUT payload) at the caret.</summary>
    public static void InsertUtf8(IntPtr text)
    {
        if (text == IntPtr.Zero) return;
        byte* p = (byte*)text;
        int n = 0;
        while (n < SdlTextMax && p[n] != 0) n++;
        Edit.InsertUtf8(new ReadOnlySpan<byte>(p, n));
        Rebuild();   // reflect the new text in the footer
    }

    /// <summary>Dispatch a control/nav key to the edit line while the box owns the keyboard.</summary>
    public static void RouteKey(uint key, int mods)
    {
        bool shift = (mods & KmodShift) != 0;
        bool ctrl  = (mods & KmodCtrl) != 0;
        if (ctrl)
        {
            if (key == KeyA) { Edit.SelectAll(); Rebuild(); return; }
            if (key == KeyC) { Copy(); return; }
            if (key == KeyV) { Paste(); return; }
            if (key == KeyX) { Cut(); return; }
        }
        switch (key)
        {
            case KeyReturn:    CloseChat(true); break;
            case KeyEsc:       CloseChat(false); break;
            case KeyBackspace: Edit.Backspace(); break;
            case KeyDelete:    Edit.Delete(); break;
            case KeyLeft:      if (ctrl) Edit.MoveWordLeft(shift); else Edit.MoveLeft(shift); break;
            case KeyRight:     if (ctrl) Edit.MoveWordRight(shift); else Edit.MoveRight(shift); break;
            case KeyHome:      Edit.Home(shift); break;
            case KeyEnd:       Edit.End(shift); break;
            case KeyUp:        Edit.HistoryPrev(); break;
            case KeyDown:      Edit.HistoryNext(); break;
        }
        Rebuild();   // caret move / edit / history recall all change the footer
    }

    private static void Copy()
    {
        if (_setClip == null || !Edit.HasSelection) return;
        byte[] u = Encoding.UTF8.GetBytes(Edit.SelectedText);
        Span<byte> buf = stackalloc byte[u.Length + 1];
        u.CopyTo(buf);
        buf[u.Length] = 0;
        fixed (byte* b = buf) _setClip(b);
    }

    /// <summary>Ctrl+X: copy the selection to the clipboard, then delete it (Backspace removes a selection).</summary>
    private static void Cut()
    {
        if (!Edit.HasSelection) return;
        Copy();
        Edit.Backspace();
        Rebuild();
    }

    private static void Paste()
    {
        if (_getClip == null) return;
        IntPtr p = _getClip();
        if (p == IntPtr.Zero) return;
        byte* s = (byte*)p;
        int n = 0;
        while (s[n] != 0) n++;
        Edit.InsertUtf8(new ReadOnlySpan<byte>(s, n));
        if (_sdlFree != null) _sdlFree(p);   // SDL_GetClipboardText returns SDL-malloc'd memory
        Rebuild();   // Ctrl+V early-returns from RouteKey before its trailing Rebuild -> redraw the footer here
    }

    /// <summary>Drive the engine's own gameplay-key gate (vanilla set it inside the focus path we skip).</summary>
    private static void KeyboardMode(bool on)
    {
        if (_exoInput == IntPtr.Zero) return;
        IntPtr inst = *(IntPtr*)_exoInput;   // g_pExoInput may be null early in-game
        if (inst == IntPtr.Zero) return;
        if (on) { if (_kbOn != null) _kbOn(inst); }
        else if (_kbOff != null) _kbOff(inst);
    }
}
