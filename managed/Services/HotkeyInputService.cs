using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services;

// The runtime pipeline: a bound key press -> a debounced packet to the server's
// toolbar system, gated on window focus and "not typing". This is a cold (input)
// hook, so it may allocate -- no NativeAOT GC concern here.
//
// Order per keydown; the first failing gate drops the event silently:
//   1. consent filter  -- unregistered keys return immediately, never inspected
//   2. auto-repeat gate -- only the first press counts; holding fires nothing more
//   3. focus gate      -- window must have focus
//   4. typing gate     -- chat prompt not focused AND no NUI text field focused
//   5. debounce        -- >= 1 s since THIS key last fired (per-key, not global)
//   6. send            -- one action packet, carrying the toolbar slot
// The original key handler is always called -- we observe, we don't consume.
//
// Auto-repeat: a held key emits KEYDOWN over and over. We remember which armed keys
// are physically down (_down) and only act on the transition up->down; the key is
// cleared on its SDL KEYUP (via a WinMessageHandler hook -- HandleWMKeyDownMessage
// never sees key-ups) and on focus loss (so a release swallowed while unfocused
// cannot wedge a key "down" forever).
public sealed unsafe class HotkeyInputService : IExtenderService
{
    // Per-key rate limit: the same hotkey may fire at most once per this interval.
    // It is keyed by keycode (_lastSent below), so different hotkeys never throttle
    // each other -- 1,2,3 pressed in quick succession all fire.
    private const long DebounceMs = 250;

    // All hook targets live in SymbolMap (the Linux name + its Windows resolution).
    // HandleWMKeyDown/WinMessageHandler are shared-engine free functions, exported on
    // both platforms; the rest are client-only classes that resolve by RVA on Windows,
    // except the two CClientExoApp focus stubs, which skip there (no anchor).

    // NUI (nuklear) keyboard-focus probe. NUI runs on its own SDL event pump
    // (nk_sdl_process_events) parallel to the classic key handler, so an armed key
    // pressed while typing in a NUI text field still reaches us -- we must gate it
    // out too. g_pNuklear holds the live nk_context*; the offsets come from the
    // client's own nuklear (verified against nk_window_has_focus / nk_edit_focus):
    //   ctx + 0x6730 -> ctx->active            (the FOCUSED window*, persists between
    //                                           frames; 0 if none)
    //   win + 0x18c  -> win->edit.active (int)  (nonzero while an edit box has focus)
    // NB: ctx->current (0x6738) is the *rendering* window -- it is null outside the
    // begin/end pass, i.e. exactly when we read here, so we must use ->active (0x6730).
    // ponytail: hard-coded struct offsets from this build's binary; if a client
    // update shifts them, re-derive from nk_window_has_focus (./sym.sh nk_window_has_focus).
    private const int NkActiveWindowOff = 0x6730;
    private const int NkWinEditActiveOff = 0x18c;

    private static ExtenderLog? _log;
    private static bool _focused = true;   // OS delivers keys only when focused, but gate anyway
    private static bool _typing;
    private static IntPtr _nuklearGlobal;  // address of g_pNuklear (resolved once)
    private static readonly Dictionary<int, long> _lastSent = new();
    private static readonly HashSet<int> _down = new();   // armed keys currently held (repeat gate)

    // SDL event: type at +0x0, keysym.sym at +0x14 (both proven from WinMessageHandler).
    private const uint SdlKeyUp = 0x301;
    private const int SdlEvSymOff = 0x14;
    // SDL_MouseButtonEvent: type at +0x0, button (Uint8) at +0x10. Mouse buttons come
    // through the same WinMessageHandler pump as KEYUP -- no extra hook needed.
    private const uint SdlMouseDown = 0x401;
    private const uint SdlMouseUp   = 0x402;
    private const int SdlEvButtonOff = 0x10;

    private static delegate* unmanaged<uint, uint, int> _keyOrig;
    private static delegate* unmanaged<IntPtr, int> _winMsgOrig;
    private static delegate* unmanaged<IntPtr, void> _gainOrig, _lostOrig, _chatOnOrig, _clickOrig, _enterOrig, _escOrig, _loseOrig;

    private readonly HookService _hooks;

    public HotkeyInputService(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _hooks = hooks;
    }

    public void Initialize()
    {
        HotkeyStore.Load(_log!);
        _nuklearGlobal = Native.Sym(SymbolMap.Nuklear);   // stable address; deref live for the current ctx

        _keyOrig    = (delegate* unmanaged<uint, uint, int>)_hooks.Install(SymbolMap.HandleWMKeyDown,   (IntPtr)(delegate* unmanaged<uint, uint, int>)&OnKeyDown);
        _winMsgOrig = (delegate* unmanaged<IntPtr, int>)_hooks.Install(SymbolMap.WinMessageHandler,     (IntPtr)(delegate* unmanaged<IntPtr, int>)&OnWinMessage);
        _gainOrig   = (delegate* unmanaged<IntPtr, void>)_hooks.Install(SymbolMap.OnGainFocus,          (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnGain);
        _lostOrig   = (delegate* unmanaged<IntPtr, void>)_hooks.Install(SymbolMap.OnLostFocus,          (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnLost);
        _chatOnOrig = (delegate* unmanaged<IntPtr, void>)_hooks.Install(SymbolMap.SetFocusChatPrompt,   (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatOn);
        _clickOrig  = (delegate* unmanaged<IntPtr, void>)_hooks.Install(SymbolMap.ChatHandleLMouseDown, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatClick);
        _enterOrig  = (delegate* unmanaged<IntPtr, void>)_hooks.Install(SymbolMap.ChatHandleEnter,      (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatEnter);
        _escOrig    = (delegate* unmanaged<IntPtr, void>)_hooks.Install(SymbolMap.ChatHandleEsc,        (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatEsc);
        _loseOrig   = (delegate* unmanaged<IntPtr, void>)_hooks.Install(SymbolMap.ChatHandleLoseFocus,  (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatLose);
    }

    // Opt-in raw-key probe for debugging the keycode space. OFF unless SOWX_KEYDIAG
    // is set in the environment -- this logs every keydown, so it is deliberately not
    // a default (the whole point of the feature is to NOT be a keylogger). Run once
    // with it on, press the key you want, read the exact code + armed/gate state.
    private static readonly bool _keyDiag =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOWX_KEYDIAG"));

    [UnmanagedCallersOnly]
    private static int OnKeyDown(uint key, uint flags)
    {
        if (_keyDiag)
            _log?.Info($"[keydiag] key={key} armed={HotkeyStore.Armed.Contains((int)key)} " +
                       $"focused={_focused} typing={_typing} nui={NuiEditFocused()}");

        Fire((int)key);
        return _keyOrig != null ? _keyOrig(key, flags) : 0;                    // never consume the key
    }

    // The gate pipeline, shared by keys and mouse buttons. `code` is the wire code
    // (SDLK for keys, MouseBase + button for mouse -- disjoint ranges, so one _down/
    // _lastSent map serves both). Sends at most one packet. The up->down transition
    // (_down) disables holding; the per-code debounce rate-limits. Steps 1-6 as above.
    private static void Fire(int code)
    {
        // 1 consent + 2 no-repeat: only the up->down transition of an armed code counts.
        // _down.Add returns false if already held (a key repeat, or a stray extra down).
        if (!(HotkeyStore.Armed.Contains(code) && _down.Add(code))) return;
        if (!_focused || _typing || NuiEditFocused()) return;                  // 3,4 focus + typing
        long now = Environment.TickCount64;                                    // 5 debounce
        _lastSent.TryGetValue(code, out long last);
        if (now - last < DebounceMs) return;
        _lastSent[code] = now;
        Send(code);                                                           // 6 send the code
    }

    // KEYUP arrives here (not at HandleWMKeyDownMessage): clear the key's "held" mark
    // so the next genuine press can fire again. We only observe; original does the work.
    [UnmanagedCallersOnly]
    private static int OnWinMessage(IntPtr ev)
    {
        if (ev != IntPtr.Zero)
        {
            switch (*(uint*)ev)
            {
                case SdlKeyUp:                                                  // key released
                    _down.Remove(*(int*)(ev + SdlEvSymOff));
                    break;
                case SdlMouseDown:                                             // mouse press: same path as a key
                    Fire(HotkeyProtocol.MouseBase + *(byte*)(ev + SdlEvButtonOff));
                    break;
                case SdlMouseUp:                                              // mouse release: clear the held mark
                    _down.Remove(HotkeyProtocol.MouseBase + *(byte*)(ev + SdlEvButtonOff));
                    break;
            }
        }
        return _winMsgOrig != null ? _winMsgOrig(ev) : 0;
    }

    private static void Send(int code)
    {
        _log?.Info($"HotkeyInput: sending code {code} to server");   // diag: confirm client emits the right code (SDLK, or MouseBase+button)
        ClientApi.SendAscii(HotkeyProtocol.KeyPressMajor, HotkeyProtocol.KeyPressMinor, code.ToString());
    }

    // True while a NUI text field holds keyboard focus (see the offset notes above).
    // Reads only; safe on the input thread. Any null link -> "not typing", so a
    // missing/uninitialised NUI never blocks hotkeys.
    private static bool NuiEditFocused()
    {
        if (_nuklearGlobal == IntPtr.Zero) return false;
        IntPtr ctx = *(IntPtr*)_nuklearGlobal;                  // g_pNuklear -> nk_context*
        if (ctx == IntPtr.Zero) return false;
        IntPtr win = *(IntPtr*)(ctx + NkActiveWindowOff);       // ctx->active window
        if (win == IntPtr.Zero) return false;
        return *(int*)(win + NkWinEditActiveOff) != 0;          // win->edit.active
    }

    // Focus gate.
    [UnmanagedCallersOnly] private static void OnGain(IntPtr self) { _focused = true;  if (_gainOrig != null) _gainOrig(self); }
    [UnmanagedCallersOnly] private static void OnLost(IntPtr self) { _focused = false; _down.Clear(); if (_lostOrig != null) _lostOrig(self); }

    // Typing gate: two entries (chat opened by key, or clicked into), three exits
    // (Enter/Esc/click-away) -- each resumes its own original. Covers the chat
    // prompt; other edit boxes (rare in-game) are not tracked -- ponytail: add
    // them if a real one shows up.
    [UnmanagedCallersOnly] private static void OnChatOn(IntPtr self)   { _typing = true;  if (_chatOnOrig != null) _chatOnOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatClick(IntPtr self){ _typing = true;  if (_clickOrig  != null) _clickOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatEnter(IntPtr self){ _typing = false; if (_enterOrig  != null) _enterOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatEsc(IntPtr self)  { _typing = false; if (_escOrig    != null) _escOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatLose(IntPtr self) { _typing = false; if (_loseOrig   != null) _loseOrig(self); }
}
