using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services;

/// <summary>
/// The runtime pipeline: a bound key press -> a debounced packet to the server's toolbar
/// system, gated on window focus and "not typing". Cold (input) hook, so it may allocate.
/// </summary>
/// <remarks>
/// Order per keydown, first failing gate drops the event silently: (1) consent filter,
/// (2) auto-repeat gate (only the up->down transition), (3) focus, (4) typing (chat prompt
/// or NUI edit box), (5) per-key debounce, (6) send. The original handler always runs -- we
/// observe, we don't consume. Held keys are cleared on SDL KEYUP and on focus loss.
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
    private static bool _typing;
    private static IntPtr _nuklearGlobal;  // address of g_pNuklear (resolved once)
    private static readonly Dictionary<int, long> _lastSent = new();
    private static readonly HashSet<int> _down = new();   // armed keys currently held (repeat gate)

    // SDL event: type at +0x0, keysym.sym at +0x14, mouse button (Uint8) at +0x10 (from
    // WinMessageHandler). Mouse buttons come through the same pump as KEYUP -- no extra hook.
    private const uint SdlKeyUp = 0x301;
    private const int SdlEvSymOff = 0x14;
    private const uint SdlMouseDown = 0x401;
    private const uint SdlMouseUp   = 0x402;
    private const int SdlEvButtonOff = 0x10;

    // SDL_Keymod bits. Ctrl/Shift take both sides; Alt is LALT ONLY -- RALT is AltGr (accented
    // chars; on Windows AltGr = LCTRL+RALT), so binding it would hijack normal typing.
    // ponytail: expose RAlt only if someone genuinely wants an AltGr chord.
    private const int KmodShift = 0x0003;   // LSHIFT | RSHIFT
    private const int KmodCtrl  = 0x00C0;   // LCTRL | RCTRL
    private const int KmodLAlt  = 0x0100;   // LALT only
    private static delegate* unmanaged<int> _modState;   // SDL_GetModState()

    private static delegate* unmanaged<uint, uint, int> _keyOrig;
    private static delegate* unmanaged<IntPtr, int> _winMsgOrig;
    private static delegate* unmanaged<IntPtr, void> _gainOrig, _lostOrig, _chatOnOrig, _clickOrig, _enterOrig, _escOrig, _loseOrig;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        HotkeyStore.Load(_log!);
        HotkeyStore.SelfCheck(_log!);   // fail-closed default is security-critical; verify at startup
        _nuklearGlobal = Native.Sym(SymbolMap.Nuklear);   // stable address; deref live for the current ctx
        _modState = (delegate* unmanaged<int>)Native.Sym(SymbolMap.SdlGetModState);   // live modifier state at capture

        _keyOrig    = (delegate* unmanaged<uint, uint, int>)hooks.Install(SymbolMap.HandleWMKeyDown,   (IntPtr)(delegate* unmanaged<uint, uint, int>)&OnKeyDown);
        _winMsgOrig = (delegate* unmanaged<IntPtr, int>)hooks.Install(SymbolMap.WinMessageHandler,     (IntPtr)(delegate* unmanaged<IntPtr, int>)&OnWinMessage);
        _gainOrig   = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.OnGainFocus,          (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnGain);
        _lostOrig   = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.OnLostFocus,          (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnLost);
        _chatOnOrig = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.SetFocusChatPrompt,   (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatOn);
        _clickOrig  = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.ChatHandleLMouseDown, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatClick);
        _enterOrig  = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.ChatHandleEnter,      (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatEnter);
        _escOrig    = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.ChatHandleEsc,        (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatEsc);
        _loseOrig   = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.ChatHandleLoseFocus,  (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatLose);
    }

    // Opt-in raw-key probe (SOWX_KEYDIAG). OFF by default -- it logs every keydown, so the whole
    // point of the feature (not being a keylogger) forbids it as a default.
    private static readonly bool _keyDiag =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOWX_KEYDIAG"));

    [UnmanagedCallersOnly]
    private static int OnKeyDown(uint key, uint flags)
    {
        if (_keyDiag)
            _log?.Info($"[keydiag] key={key} armed={HotkeyStore.Armed.Contains((int)key)} " +
                       $"focused={_focused} typing={_typing} nui={NuiEditFocused()}");

        Fire((int)key | Mods());
        return _keyOrig != null ? _keyOrig(key, flags) : 0;                    // never consume the key
    }

    /// <summary>
    /// The Ctrl/Shift/Alt bits currently held, in HotkeyProtocol.Mod* encoding, OR'd onto the base
    /// code so a combo (Ctrl+F) matches its own armed entry and nothing else. No allocation.
    /// </summary>
    private static int Mods()
    {
        if (_modState == null) return 0;
        int m = _modState();
        int bits = 0;
        if ((m & KmodCtrl)  != 0) bits |= HotkeyProtocol.ModCtrl;
        if ((m & KmodShift) != 0) bits |= HotkeyProtocol.ModShift;
        if ((m & KmodLAlt)  != 0) bits |= HotkeyProtocol.ModAlt;
        return bits;
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
        if (!_focused || _typing || NuiEditFocused()) return;                  // 3,4 focus + typing
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
                case SdlKeyUp:                                                  // key released
                    _down.Remove(*(int*)(ev + SdlEvSymOff));
                    break;
                case SdlMouseDown:                                             // mouse press: same path as a key
                    Fire(HotkeyProtocol.MouseBase + *(byte*)(ev + SdlEvButtonOff) | Mods());
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

    // Focus gate.
    [UnmanagedCallersOnly] private static void OnGain(IntPtr self) { _focused = true;  if (_gainOrig != null) _gainOrig(self); }
    [UnmanagedCallersOnly] private static void OnLost(IntPtr self) { _focused = false; _down.Clear(); if (_lostOrig != null) _lostOrig(self); }

    // Typing gate: two entries (chat opened by key, or clicked into), three exits (Enter/Esc/
    // click-away). Other edit boxes (rare in-game) are not tracked. ponytail: add them if a real one shows up.
    [UnmanagedCallersOnly] private static void OnChatOn(IntPtr self)   { _typing = true;  if (_chatOnOrig != null) _chatOnOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatClick(IntPtr self){ _typing = true;  if (_clickOrig  != null) _clickOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatEnter(IntPtr self){ _typing = false; if (_enterOrig  != null) _enterOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatEsc(IntPtr self)  { _typing = false; if (_escOrig    != null) _escOrig(self); }
    [UnmanagedCallersOnly] private static void OnChatLose(IntPtr self) { _typing = false; if (_loseOrig   != null) _loseOrig(self); }
}
