using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services;

// The runtime pipeline: a bound key press -> a debounced packet to the server's
// toolbar system, gated on window focus and "not typing". This is a cold (input)
// hook, so it may allocate -- no NativeAOT GC concern here.
//
// Order per keydown; the first failing gate drops the event silently:
//   1. consent filter  -- unregistered keys return immediately, never inspected
//   2. focus gate      -- window must have focus
//   3. typing gate     -- chat prompt must not be focused
//   4. debounce        -- >= 250 ms since this key last fired
//   5. send            -- one action packet, carrying the toolbar slot
// The original key handler is always called -- we observe, we don't consume.
public sealed unsafe class HotkeyInputService : IExtenderService
{
    private const long DebounceMs = 250;

    private const string KeySym    = "_Z22HandleWMKeyDownMessagejj";
    private const string GainSym   = "_ZN13CClientExoApp11OnGainFocusEv";
    private const string LostSym   = "_ZN13CClientExoApp11OnLostFocusEv";
    private const string ChatOnSym = "_ZN10CGuiInGame18SetFocusChatPromptEv";           // chat opened by key
    private const string ClickSym  = "_ZN20CGuiInGameChatPrompt24HandleLMouseDownGuiEventEv"; // chat clicked into
    private const string EnterSym  = "_ZN20CGuiInGameChatPrompt11HandleEnterEv";
    private const string EscSym    = "_ZN20CGuiInGameChatPrompt9HandleEscEv";
    private const string LoseSym   = "_ZN20CGuiInGameChatPrompt41HandleLoseKeyboardFocusLMouseDownGuiEventEv";

    private static ExtenderLog? _log;
    private static bool _focused = true;   // OS delivers keys only when focused, but gate anyway
    private static bool _typing;
    private static readonly Dictionary<int, long> _lastSent = new();

    private static delegate* unmanaged<uint, uint, void> _keyOrig;
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

        _keyOrig    = (delegate* unmanaged<uint, uint, void>)_hooks.Install(KeySym,    (IntPtr)(delegate* unmanaged<uint, uint, void>)&OnKeyDown);
        _gainOrig   = (delegate* unmanaged<IntPtr, void>)_hooks.Install(GainSym,   (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnGain);
        _lostOrig   = (delegate* unmanaged<IntPtr, void>)_hooks.Install(LostSym,   (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnLost);
        _chatOnOrig = (delegate* unmanaged<IntPtr, void>)_hooks.Install(ChatOnSym, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatOn);
        _clickOrig  = (delegate* unmanaged<IntPtr, void>)_hooks.Install(ClickSym,  (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatClick);
        _enterOrig  = (delegate* unmanaged<IntPtr, void>)_hooks.Install(EnterSym,  (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatEnter);
        _escOrig    = (delegate* unmanaged<IntPtr, void>)_hooks.Install(EscSym,    (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatEsc);
        _loseOrig   = (delegate* unmanaged<IntPtr, void>)_hooks.Install(LoseSym,   (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnChatLose);
    }

    [UnmanagedCallersOnly]
    private static void OnKeyDown(uint key, uint flags)
    {
        if (HotkeyStore.Armed.Contains((int)key) && _focused && !_typing)      // 1,2,3
        {
            long now = Environment.TickCount64;                                 // 4. debounce
            _lastSent.TryGetValue((int)key, out long last);
            if (now - last >= DebounceMs)
            {
                _lastSent[(int)key] = now;
                Send((int)key);                                                // 5. send the keycode
            }
        }
        if (_keyOrig != null) _keyOrig(key, flags);                            // never consume the key
    }

    private static void Send(int vk) =>
        ClientApi.SendAscii(HotkeyProtocol.KeyPressMajor, HotkeyProtocol.KeyPressMinor, vk.ToString());

    // Focus gate.
    [UnmanagedCallersOnly] private static void OnGain(IntPtr self) { _focused = true;  if (_gainOrig != null) _gainOrig(self); }
    [UnmanagedCallersOnly] private static void OnLost(IntPtr self) { _focused = false; if (_lostOrig != null) _lostOrig(self); }

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
