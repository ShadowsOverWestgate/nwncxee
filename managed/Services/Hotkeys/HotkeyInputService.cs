using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Services.Chat;
using Sowx.Services.Dialog;
using Sowx.Ui;

namespace Sowx.Services.Hotkeys;

/// <summary>
/// The runtime pipeline: a bound key press -> a debounced packet to the server's toolbar
/// system, gated on window focus and "not typing". Cold (input) hook, so it may allocate.
/// </summary>
/// <remarks>
/// Order per keydown, first failing gate drops the event silently: (1) consent filter,
/// (2) auto-repeat gate (only the up->down transition), (3) focus, (4) typing
/// (<see cref="ChatService.IsTyping"/> -- the C# chatbox owns the keyboard, or a NUI edit box
/// holds focus), (5) per-key debounce, (6) send. The original handler otherwise always runs --
/// we observe, we don't consume -- EXCEPT while the chatbox is typing, where we route the key
/// to its edit line and consume it. Held keys are cleared on SDL KEYUP and on focus loss.
/// </remarks>
public sealed unsafe class HotkeyInputService : IExtenderService
{
    /// <summary>Per-key rate limit (keyed by code, so different hotkeys never throttle each other).</summary>
    private const long DebounceMs = 250;

    // NUI keyboard-focus probe. NUI runs its own SDL event pump parallel to the classic key
    // handler, so an armed key pressed while typing in a NUI text field still reaches us. Offsets
    // verified against nk_window_has_focus / nk_edit_focus:
    //   ctx + 0x6730 -> ctx->active (FOCUSED window*, persists between frames; 0 if none)
    //   win + 0x18c  -> win->edit.active (nonzero while an edit box has focus)
    // NB: ctx->current (0x6738) is the *rendering* window -- null when we read here, so use ->active.
    // ponytail: hard-coded offsets from this build; re-derive from nk_window_has_focus if a client
    // update shifts them.
    private const int NkActiveWindowOff = 0x6730;
    private const int NkWinEditActiveOff = 0x18c;

    private static ExtenderLog? _log;
    private static bool _focused = true;   // OS delivers keys only when focused, but gate anyway
    private static bool _typing;           // observer-mode vanilla-chat typing flag (own-input mode uses ChatService.IsTyping)
    private static IntPtr _nuklearGlobal;  // address of g_pNuklear (resolved once)
    private static IntPtr _exoInputGlobal; // address of g_pExoInput (deref live for the instance)
    private static delegate* unmanaged<IntPtr, int> _getKbMode;   // CExoInput::GetKeyboardMode
    private static readonly Dictionary<int, long> _lastSent = new();
    private static readonly HashSet<int> _down = new();   // armed keys currently held (repeat gate)

    // SDL event: type at +0x0, keysym.sym at +0x14, mouse button (Uint8) at +0x10 (from
    // WinMessageHandler). Mouse buttons come through the same pump as KEYUP -- no extra hook.
    private const uint SdlKeyDown = 0x300;
    private const uint SdlKeyUp = 0x301;
    // SDL_TEXTINPUT: finished UTF-8 codepoints. Null-terminated string (SDL_TextInputEvent.text)
    // at ev + 0xC -- full Unicode, unlike the engine's lossy 8-bit HandleWMCharMessage (bypassed).
    private const uint SdlTextInput = 0x303;
    private const int SdlEvTextOff = 0xC;
    private const int SdlEvSymOff = 0x14;
    private const uint SdlMouseMotion = 0x400;
    private const uint SdlMouseDown = 0x401;
    private const uint SdlMouseUp   = 0x402;
    private const uint SdlMouseWheel = 0x403;
    private const int SdlEvButtonOff = 0x10;
    // SDL button/motion events: x@0x14, y@0x18 (Sint32). Wheel event: y@0x14 (Sint32, +=up/into history).
    private const int SdlEvMouseXOff = 0x14;
    private const int SdlEvMouseYOff = 0x18;
    private const int SdlEvWheelYOff = 0x14;

    // SDL_Keymod bits. Ctrl/Shift take both sides; Alt is LALT ONLY -- RALT is AltGr (accented
    // chars; on Windows AltGr = LCTRL+RALT), so binding it would hijack normal typing.
    // ponytail: expose RAlt only if someone genuinely wants an AltGr chord.
    private const int KmodShift = 0x0003;   // LSHIFT | RSHIFT
    private const int KmodCtrl  = 0x00C0;   // LCTRL | RCTRL
    private const int KmodLAlt  = 0x0100;   // LALT only
    private static delegate* unmanaged<int> _modState;   // SDL_GetModState()

    private static delegate* unmanaged<uint, uint, int> _keyOrig;
    private static IntPtr* _winMsg;   // chain slot on the SDL pump (#539 shares it); *_winMsg = the next layer
    private static delegate* unmanaged<IntPtr, void> _gainOrig, _lostOrig;
    // Observer-mode chat-focus trampolines (default mode only; unused when ChatService owns input).
    private static delegate* unmanaged<IntPtr, void> _chatOnOrig, _clickOrig, _enterOrig, _escOrig, _loseOrig;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!SymbolMap.Require(log, "HotkeyInputService", HotkeyProtocol.RequiredSymbols)) return;
        // No HotkeyStore.Load here: our loader runs pre-main, so the engine user dir (where the cfg lives)
        // isn't resolvable yet. The store lazy-loads on the first login-path call. SelfCheck is in-memory.
        HotkeyStore.SelfCheck(_log!);   // fail-closed default is security-critical; verify at startup
        // Registered ahead of every stand-down below: the probe covers both key paths, and the path taken is
        // exactly what a session opening it wants to find out.
        CommandRegistry.Global.Register("/keydiag", "toggle raw-key diagnostics (logs every keydown)",
                                        _ => ToggleKeyDiag());
        if (Settings.Bool("keydiag", false)) ToggleKeyDiag();   // boot switch for when chat itself is the question

        // Default (#64): the framework input layer owns raw capture. Hotkeys become a chord consumer at tier 2 --
        // fired only when no window consumed the key and the chord gate is clear (the arbiter's job now). Install
        // no raw hooks.
        if (GameGate.OwnsFramePresent)
        {
            UiHost.AddChordConsumer(new Consumer());
            log.Info("HotkeyInputService: registered as a chord consumer (framework owns raw capture)");
            return;
        }

        // lockchat below: the framework input layer stood down (single-hooks would otherwise conflict), so
        // own the raw hooks ourselves -- hotkey capture + the vanilla-chat typing observer -- exactly as before.
        // This is the one path that keeps hotkeys live when the whole UI framework is disabled.
        _nuklearGlobal = Native.Sym(SymbolMap.Nuklear);   // stable address; deref live for the current ctx
        _exoInputGlobal = Native.Sym(SymbolMap.ExoInput); // deref live for the CExoInput instance
        _getKbMode = (delegate* unmanaged<IntPtr, int>)Native.Sym(SymbolMap.GetKeyboardMode);
        _modState = (delegate* unmanaged<int>)Native.Sym(SymbolMap.SdlGetModState);   // live modifier state at capture

        _keyOrig    = (delegate* unmanaged<uint, uint, int>)hooks.Install(SymbolMap.HandleWMKeyDown,   (IntPtr)(delegate* unmanaged<uint, uint, int>)&OnKeyDown);
        _winMsg     = hooks.Chain(SymbolMap.WinMessageHandler, (IntPtr)(delegate* unmanaged<IntPtr, int>)&OnWinMessage, 0);
        _gainOrig   = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.OnGainFocus,          (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnGain);
        _lostOrig   = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.OnLostFocus,          (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnLost);
        // Chat typing gate. By default ChatService owns input: it SKIPS the chat-focus openers (suppressing
        // vanilla) and drives ChatService.IsTyping, so we must NOT also install them (funchook single-hooks).
        // Under lockchat (ChatService stood down, OwnInput false) we observe those same hooks (forward to
        // trampoline) to track _typing, so vanilla chat keeps working and hotkeys still suppress while typing.
        if (!ChatService.OwnInput)
        {
            _chatOnOrig = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.SetFocusChatPrompt,   (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatOn);
            _clickOrig  = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.ChatHandleLMouseDown, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatClick);
            _enterOrig  = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.ChatHandleEnter,      (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatEnter);
            _escOrig    = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.ChatHandleEsc,        (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatEsc);
            _loseOrig   = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.ChatHandleLoseFocus,  (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatLose);
        }
    }

    /// <summary>Raw-key probe on the hotkey path. OFF by default -- it logs every keydown, so the whole
    /// point of the feature (not being a keylogger) forbids it as a default. Volatile: <c>/keydiag</c> writes
    /// it from the command dispatch, this reads it in the key pump.</summary>
    private static volatile bool _keyDiag;

    /// <summary>The <c>/keydiag</c> body (#182, ADR 0013 bucket 1): one switch over BOTH raw-key probes --
    /// this service's and the UI input pump's -- because a diagnostic that only covers half the key path
    /// answers half the question it is opened for.</summary>
    private static string ToggleKeyDiag()
    {
        _keyDiag = !_keyDiag;
        Sowx.Ui.UiInputService.KeyDiag = _keyDiag;
        return $"[keydiag] raw-key logging {(_keyDiag ? "ON -- every keydown is logged" : "off")}";
    }

    [UnmanagedCallersOnly]
    private static int OnKeyDown(uint key, uint flags)
    {
        if (_keyDiag)
            _log?.Info($"[keydiag] key={key} armed={HotkeyStore.Armed.Contains((int)key)} " +
                       $"focused={_focused} typing={_typing || ChatService.IsTyping} nui={NuiEditFocused()} dialog={DialogOpen()}");

        // Chatbox owns the keyboard: route the key to its edit line (or a settings value box) and CONSUME it.
        // Calling the original on Enter would re-enter SetFocusChatPrompt and reopen the box we just closed.
        if (ChatService.WantsKey)
        {
            ChatService.RouteKey(key, _modState != null ? _modState() : 0);
            return 0;
        }

        // NPC dialog is modal: 1-9 pick a reply, ESC closes. Consume so the digits don't fire hotkeys and
        // ESC doesn't open the options menu over the conversation. After chat typing, so typing still wins.
        if (DialogService.IsOpen && DialogService.HandleKey(key, _modState != null ? _modState() : 0)) return 0;

        Fire((int)key | Mods());
        return _keyOrig != null ? _keyOrig(key, flags) : 0;                    // never consume the key
    }

    /// <summary>
    /// The Ctrl/Shift/Alt bits currently held, in HotkeyProtocol.Mod* encoding, OR'd onto the base
    /// code so a combo (Ctrl+F) matches its own armed entry and nothing else. No allocation.
    /// </summary>
    private static int Mods() => _modState != null ? ModsFrom(_modState()) : 0;

    /// <summary>Map a raw SDL_Keymod bitset to the HotkeyProtocol.Mod* wire encoding.</summary>
    private static int ModsFrom(int m)
    {
        int bits = 0;
        if ((m & KmodCtrl)  != 0) bits |= HotkeyProtocol.ModCtrl;
        if ((m & KmodShift) != 0) bits |= HotkeyProtocol.ModShift;
        if ((m & KmodLAlt)  != 0) bits |= HotkeyProtocol.ModAlt;
        return bits;
    }

    /// <summary>The chord-consumer face of hotkeys (#64, default path). The framework input layer owns raw
    /// capture, precedence, the auto-repeat gate, and every environmental gate; this consumer only maps a
    /// fallen-through key/button to its wire code and does the consent + debounce + send. Tier 2: below a
    /// window's focused content, above the engine's gameplay keybinds.</summary>
    private sealed class Consumer : IChordConsumer
    {
        public int Priority => 2;

        /// <summary>Arbiter query (#61): true when some armed code shares this base key.</summary>
        public bool WantsKey(int key)
        {
            foreach (int code in HotkeyStore.Armed)
                if ((code & ~HotkeyProtocol.ModMask) == key) return true;
            return false;
        }

        public void Fire(uint key, int sdlMods) => Emit((int)key | ModsFrom(sdlMods));
        public void FireMouse(int button, int sdlMods) => Emit(HotkeyProtocol.MouseBase + button | ModsFrom(sdlMods));
    }

    /// <summary>Consent (armed) + per-key debounce + send. The repeat gate and env gates are the framework's;
    /// this is the shared send tail used by the consumer path.</summary>
    private static void Emit(int code)
    {
        if (!HotkeyStore.Armed.Contains(code)) return;
        long now = Environment.TickCount64;
        _lastSent.TryGetValue(code, out long last);
        if (now - last < DebounceMs) return;
        _lastSent[code] = now;
        Send(code);
    }

    /// <summary>
    /// The gate pipeline, shared by keys and mouse buttons. `code` is the wire code (SDLK, or
    /// MouseBase + button -- disjoint ranges, so one _down/_lastSent map serves both). Sends at most
    /// one packet.
    /// </summary>
    private static void Fire(int code)
    {
        // 1 consent + 2 no-repeat: only the up->down transition of an armed code counts. Track "held"
        // by the BASE key (modifier bits stripped): KEYUP/mouse-up carry no modifier state, so the
        // release must clear the same key it set.
        int baseCode = code & ~HotkeyProtocol.ModMask;
        if (!(HotkeyStore.Armed.Contains(code) && _down.Add(baseCode))) return;
        if (!_focused || _typing || ChatService.WantsKey || NuiEditFocused() || KeyboardModeActive()
            || DialogOpen() || DialogService.IsOpen) return;  // 3,4 focus + typing + dialog (ours or vanilla)
        long now = Environment.TickCount64;                                    // 5 debounce
        _lastSent.TryGetValue(code, out long last);
        if (now - last < DebounceMs) return;
        _lastSent[code] = now;
        Send(code);                                                            // 6 send the code
    }

    // KEYUP arrives here (not at HandleWMKeyDownMessage): clear the key's "held" mark. We only
    // observe; original does the work.
    [UnmanagedCallersOnly]
    private static int OnWinMessage(IntPtr ev)
    {
        if (ev != IntPtr.Zero)
        {
            switch (*(uint*)ev)
            {
                case SdlKeyDown:
                    // Our dialog is modal for the keys it uses (reply digits, ESC, Ctrl+C copy). The engine queues
                    // gameplay keybinds from CExoInput::AddEvent -- run unconditionally by the original for EVERY
                    // SDL_KEYDOWN, matched by scancode with NO modifier check -- so Ctrl+C matches the plain 'C'
                    // binding and opens the character sheet a frame later. HandleWMKeyDownMessage (where OnKeyDown
                    // consumes) runs AFTER that and cannot stop it. So swallow the keydown HERE, before the original
                    // sees it, whenever the dialog consumes the key. Keyups still pass through (inert for keybinds
                    // -> no stuck-key). Chat is left alone: it suppresses keybinds via KeyboardModeOn already.
                    if (DialogService.IsOpen && !ChatService.WantsKey
                        && DialogService.HandleKey(*(uint*)(ev + SdlEvSymOff), _modState != null ? _modState() : 0))
                        return 0;
                    break;
                case SdlKeyUp:                                                  // key released
                    _down.Remove(*(int*)(ev + SdlEvSymOff));
                    break;
                case SdlTextInput:                                             // finished UTF-8 -> chat edit line / settings field
                    if (ChatService.WantsKey) ChatService.InsertUtf8(ev + SdlEvTextOff);
                    break;
                case SdlMouseMotion:
                {
                    int mx = *(int*)(ev + SdlEvMouseXOff), my = *(int*)(ev + SdlEvMouseYOff);
                    if (DialogService.OnMouseMotion(mx, my))   // consumes only mid-drag / settings-modal; else hover
                        return 0;
                    if (Quickbar.QuickbarService.OnMouseMotion(mx, my))
                        return 0;
                    if (ChatService.OnMouseMotion(mx, my))
                        return 0;
                    break;
                }
                case SdlMouseDown:
                {
                    byte btn = *(byte*)(ev + SdlEvButtonOff);
                    int mx = *(int*)(ev + SdlEvMouseXOff), my = *(int*)(ev + SdlEvMouseYOff);
                    if (DialogService.OnMouseButton(true, btn, mx, my))   // modal: dialog wins the click
                        return 0;
                    if (Quickbar.QuickbarService.OnMouseButton(true, btn, mx, my))
                        return 0;
                    if (ChatService.OnMouseButton(true, btn, mx, my))
                        return 0;
                    Fire(HotkeyProtocol.MouseBase + btn | Mods());
                    break;
                }
                case SdlMouseUp:
                {
                    byte btn = *(byte*)(ev + SdlEvButtonOff);
                    int mx = *(int*)(ev + SdlEvMouseXOff), my = *(int*)(ev + SdlEvMouseYOff);
                    if (DialogService.OnMouseButton(false, btn, mx, my))
                        return 0;
                    if (Quickbar.QuickbarService.OnMouseButton(false, btn, mx, my))
                        return 0;
                    if (ChatService.OnMouseButton(false, btn, mx, my))
                        return 0;
                    _down.Remove(HotkeyProtocol.MouseBase + btn);
                    break;
                }
                case SdlMouseWheel:                                            // scrollback when the pointer is over the box
                    if (DialogService.OnMouseWheel(*(int*)(ev + SdlEvWheelYOff)))   // modal: dialog scrolls first
                        return 0;
                    if (ChatService.OnMouseWheel(*(int*)(ev + SdlEvWheelYOff)))
                        return 0;
                    break;
            }
        }
        return _winMsg != null ? ((delegate* unmanaged<IntPtr, int>)(*_winMsg))(ev) : 0;
    }

    private static void Send(int code)
    {
        _log?.Info($"HotkeyInput: sending code {code} to server");
        ClientApi.SendAscii(HotkeyProtocol.KeyPressMajor, HotkeyProtocol.KeyPressMinor, code.ToString());
    }

    /// <summary>
    /// True while a NUI text field holds keyboard focus. Reads only; safe on the input thread. Any
    /// null link -> "not typing", so a missing/uninitialised NUI never blocks hotkeys.
    /// </summary>
    private static bool NuiEditFocused()
    {
        if (_nuklearGlobal == IntPtr.Zero) return false;
        IntPtr ctx = *(IntPtr*)_nuklearGlobal;                  // g_pNuklear -> nk_context*
        if (ctx == IntPtr.Zero) return false;
        IntPtr win = *(IntPtr*)(ctx + NkActiveWindowOff);       // ctx->active window
        if (win == IntPtr.Zero) return false;
        return *(int*)(win + NkWinEditActiveOff) != 0;          // win->edit.active
    }

    // CGuiInGame dialog pointer offset (from HandleDialogNumKey decompilation: this+0x70).
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    private const int ExoAppInternalOff = 0x8, InGameGuiOff = 0x90, DialogPanelOff = 0x70;

    /// <summary>True while an NPC conversation dialog is open: *(gui+0x70) is non-null.</summary>
    private static bool DialogOpen()
    {
        if (_appMgr == IntPtr.Zero) return false;
        IntPtr mgr = *(IntPtr*)_appMgr;
        if (mgr == IntPtr.Zero) return false;
        IntPtr app = *(IntPtr*)mgr;
        if (app == IntPtr.Zero) return false;
        IntPtr intern = *(IntPtr*)(app + ExoAppInternalOff);
        if (intern == IntPtr.Zero) return false;
        IntPtr gui = *(IntPtr*)(intern + InGameGuiOff);
        return gui != IntPtr.Zero && *(IntPtr*)(gui + DialogPanelOff) != IntPtr.Zero;
    }

    /// <summary>True while the engine is capturing text (the debug console, or any vanilla edit field):
    /// CExoInput keyboard mode is ON. The general "not gameplay input" gate -- covers the console, which sets
    /// no chat/NUI flag of ours. Any null link -> false, so a missing CExoInput never blocks hotkeys.</summary>
    private static bool KeyboardModeActive()
    {
        if (_exoInputGlobal == IntPtr.Zero || _getKbMode == null) return false;
        IntPtr inst = *(IntPtr*)_exoInputGlobal;   // g_pExoInput may be null early in-game
        return inst != IntPtr.Zero && _getKbMode(inst) != 0;
    }

    // Focus gate.
    // self is CClientExoApp* -- cache it so ChatService can walk to the live in-game GUI (its visibility
    // gate). A process-lifetime singleton, so capturing on either focus edge is enough.
    [UnmanagedCallersOnly] private static void OnGain(IntPtr self) { _focused = true;  ClientApi.LiveExoApp = self; if (_gainOrig != null) _gainOrig(self); }
    [UnmanagedCallersOnly] private static void OnLost(IntPtr self) { _focused = false; ClientApi.LiveExoApp = self; _down.Clear(); if (_lostOrig != null) _lostOrig(self); }

    // Observer-mode typing gate (default; not installed when ChatService owns input). Two entries
    // (chat opened by key, or clicked into), three exits (Enter/Esc/click-away). We forward to the
    // trampoline -- vanilla chat runs normally; we only track _typing to suppress hotkeys.
    [UnmanagedCallersOnly] private static void OnChatOn(IntPtr self)   { _typing = true;  if (_chatOnOrig != null) _chatOnOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatClick(IntPtr self){ _typing = true;  if (_clickOrig  != null) _clickOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatEnter(IntPtr self){ _typing = false; if (_enterOrig  != null) _enterOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatEsc(IntPtr self)  { _typing = false; if (_escOrig    != null) _escOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatLose(IntPtr self) { _typing = false; if (_loseOrig   != null) _loseOrig(self); }
}
