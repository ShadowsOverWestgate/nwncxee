using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Services.Hotkeys;

/// <summary>
/// Client-owned consent gate. A server ArmSet that ADDS keys raises the engine's own native
/// yes/no modal (CPanelOkCancel, as used by "quit to main menu"). The modal is drawn and owned
/// by the client, so a malicious server can neither forge nor auto-confirm it -- only an OK click
/// arms the keys; Esc/Cancel/close all deny.
/// </summary>
/// <remarks>
/// Spawn mirrors the engine's own path (decomp-verified both platforms): operator new; ctor;
/// p+0x80=context, p+0x88=on-close callback (Linux also zeroes p+0x90); Activate(msg, OK, CANCEL).
/// On close the engine calls callback(context, result); 1 = OK, anything else = deny. The panel then
/// self-frees via its deleting dtor -> operator delete, so we allocate from the matching operator new
/// (the game's own on Windows -- static CRT) or the free lands on the wrong heap. The message
/// CExoString is by value: Itanium frees it in the caller, MSVC in the callee; FreeExoString is
/// correct either way (see Native.NewExoString). Cold path -- both proposal and answer arrive on the
/// main thread, so no race on _pending.
/// </remarks>
internal static unsafe class HotkeyConsent
{
    // sizeof(CPanelOkCancel): 0x3a0 Itanium / 0x360 MSVC (from the operator new arg in the engine's
    // own spawn path). Wrong size = the ctor/dtor touch out-of-bounds bytes.
    private static readonly int PanelSize = OperatingSystem.IsWindows() ? 0x360 : 0x3a0;
    private const int CtxOff = 0x80, CbOff = 0x88, PadOff = 0x90;
    // OK / Cancel button label strrefs -- the exact pair HandleQuitClick passes to Activate.
    private const uint OkStrRef = 0x2052, CancelStrRef = 0x2053;

    private static ExtenderLog? _log;
    private static delegate* unmanaged<nuint, IntPtr> _new;
    private static delegate* unmanaged<IntPtr, void> _ctor;
    private static delegate* unmanaged<IntPtr, IntPtr, uint, uint, void> _activate;   // (this, &CExoString, ok, cancel)
    private static delegate* unmanaged<int, byte*> _keyName;
    private static IntPtr _guiManSym;                    // &g_pGuiMan -- read its value, never deref as an object
    private static bool _ready;

    // The keys awaiting the player's answer. Non-null == a modal is currently open.
    private static HashSet<int>? _pending;
    // /consent probe open: the answer is logged, never granted/revoked (no cfg write, no packet).
    private static bool _probe;

    public static void Init(ExtenderLog log)
    {
        _log = log;
        _new       = (delegate* unmanaged<nuint, IntPtr>)Native.Sym(SymbolMap.OperatorNew);
        _ctor      = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.OkCancelCtor);
        _activate  = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, void>)Native.Sym(SymbolMap.OkCancelActivate);
        _keyName   = (delegate* unmanaged<int, byte*>)Native.Sym(SymbolMap.SdlGetKeyName);
        _guiManSym = Native.Sym(SymbolMap.GuiMan);

        _ready = _new != null && _ctor != null && _activate != null && _guiManSym != IntPtr.Zero;
        if (!_ready)
            _log?.Info("HotkeyConsent: native modal unavailable on this client -- new binds will be refused");
    }

    /// <summary>Called on login; fail-closed -- any pending (unconfirmed) keys are dropped, never armed.</summary>
    public static void Reset() { _pending = null; _probe = false; }

    /// <summary>The <c>/consent</c> body (#418): spawn the modal with a fake key set so the whole native round
    /// trip (operator new, ctor, Activate, on-close callback, self-free) runs without a server. The answer is
    /// only logged. The one place the port touches the engine allocator, so it gets its own probe.</summary>
    public static string Probe()
    {
        var fake = new HashSet<int> { 'f' | HotkeyProtocol.ModCtrl, HotkeyProtocol.MouseBase + 3 };
        if (!Prompt(fake)) return "[consent] modal not shown -- see the log";
        _probe = true;
        return "[consent] modal up: answer it, then read the log";
    }

    /// <summary>
    /// Ask the player to consent to arming `additions`. Returns true only if the modal was shown.
    /// Every false path (missing symbol, not in-game, a prompt already open) means "not armed"; the
    /// caller must not arm on false.
    /// </summary>
    public static bool Prompt(HashSet<int> additions)
    {
        if (!_ready || additions.Count == 0) return false;
        if (_pending != null)
        {
            _log?.Info("HotkeyConsent: a consent prompt is already open; dropping this proposal");
            return false;
        }
        if (!GuiReady())
        {
            _log?.Info("HotkeyConsent: GUI not up yet; refusing bind proposal");
            return false;
        }

        IntPtr panel = _new((nuint)PanelSize);
        if (panel == IntPtr.Zero) return false;
        _ctor(panel);
        *(IntPtr*)(panel + CtxOff) = panel;              // benign non-null context; our callback ignores it
        *(IntPtr*)(panel + CbOff)  = (IntPtr)(delegate* unmanaged<IntPtr, int, void>)&OnAnswer;
        // Mirror each platform's real code: Linux HandleQuitClick zeroes +0x90; the MSVC path doesn't.
        if (!OperatingSystem.IsWindows())
            *(IntPtr*)(panel + PadOff) = IntPtr.Zero;

        IntPtr msg = Native.NewExoString(Describe(additions));
        _activate(panel, msg, OkStrRef, CancelStrRef);   // Linux overload copies the text; does not own msg
        Native.FreeExoString(msg);

        _pending = additions;
        _log?.Info($"HotkeyConsent: prompting to arm {additions.Count} key(s)");
        return true;
    }

    /// <summary>
    /// Is the GUI manager up? g_pGuiMan is the exact global CGuiModalPanel::Activate uses, so non-null
    /// == safe to attach a modal. We only READ its value (never deref as an object), so a wrong
    /// resolution can't fault -- unlike the g_pAppManager path, which segfaults on this build.
    /// </summary>
    private static bool GuiReady() => *(IntPtr*)_guiManSym != IntPtr.Zero;

    // The engine's on-close callback: 1 = OK, anything else = deny. Clear _pending FIRST -- the panel
    // self-frees right after this returns, and a new proposal must be able to prompt again.
    [UnmanagedCallersOnly]
    private static void OnAnswer(IntPtr context, int result)
    {
        var answered = _pending;
        _pending = null;
        // The engine's modal leaves keyboard mode ON after it closes, which eats the next Enter (#418 trace);
        // release it unless one of our windows is typing.
        if (!CX.Ui.UiHost.AnyKeyboardOwner) CX.Ui.UiHost.KeyboardGate?.Invoke(false);
        if (answered == null) return;
        if (_probe)
        {
            _probe = false;
            _log?.Info($"HotkeyConsent: probe answered result={result} ({(result == 1 ? "OK" : "deny")}); nothing granted");
            return;
        }
        if (result == 1)
        {
            // Record consent under this server's token (minting one if none), then hand the token +
            // consented keys back so the server can present it next login.
            string token = HotkeyStore.Grant(answered, _log!);
            SendGrantToken(token);
        }
        else
        {
            // Deny -> tell the server to forget the binding, or it re-pushes these every login and
            // re-prompts forever. The client revoking consent it never granted.
            Revoke(answered);
            _log?.Info("HotkeyConsent: player declined; revoked on the server");
        }
    }

    /// <summary>Send a Revoke for `keys` so the server drops the binding. Used by deny and by client-initiated unbind.</summary>
    public static void Revoke(IEnumerable<int> keys)
    {
        string csv = string.Join(",", keys);
        if (csv.Length == 0) return;
        ClientApi.SendAscii(HotkeyProtocol.RevokeMajor, HotkeyProtocol.RevokeMinor, csv);
    }

    // Hand the server the consent receipt: the token this consent is stored under + the consented
    // keys. The server persists it per player and presents it back next login (PresentToken).
    private static void SendGrantToken(string tokenHex)
    {
        string csv = string.Join(",", HotkeyStore.Consented);
        ClientApi.SendAscii(HotkeyProtocol.GrantTokenMajor, HotkeyProtocol.GrantTokenMinor, $"{tokenHex};{csv}");
    }

    private static string Describe(HashSet<int> keys)
    {
        var names = new List<string>(keys.Count);
        foreach (int code in keys) names.Add(KeyName(code));
        return "This server wants permission to track these keys as hotkeys:\n\n"
             + string.Join(", ", names)
             + "\n\nAllow?";
    }

    /// <summary>
    /// Human-readable label. SDLK codes go through SDL_GetKeyName; mouse codes (MouseBase + button)
    /// and modifier prefixes are ours. The consent prompt is a security surface -- it MUST read
    /// "Ctrl+F", not "key ?", so the player knows what they are allowing.
    /// </summary>
    internal static string KeyName(int code)
    {
        int mods = code & HotkeyProtocol.ModMask;
        if (mods != 0)
        {
            string prefix = "";
            if ((mods & HotkeyProtocol.ModCtrl) != 0) prefix += "Ctrl+";
            if ((mods & HotkeyProtocol.ModShift) != 0) prefix += "Shift+";
            if ((mods & HotkeyProtocol.ModAlt) != 0) prefix += "Alt+";
            return prefix + KeyName(code & ~HotkeyProtocol.ModMask);
        }
        // Bound the mouse range: button is a byte, but SDLK scancode keys (bit 30) are also >= MouseBase,
        // so an unbounded check would mislabel every F-key/arrow "Mouse".
        if (code >= HotkeyProtocol.MouseBase && code <= HotkeyProtocol.MouseBase + 0xFF)
        {
            int btn = code - HotkeyProtocol.MouseBase;   // SDL button index
            return btn switch
            {
                1 => "Mouse Left",
                2 => "Mouse Middle",
                3 => "Mouse Right",
                4 => "Mouse X1",
                5 => "Mouse X2",
                _ => $"Mouse {btn}",
            };
        }
        if (_keyName != null)
        {
            byte* p = _keyName(code);
            string s = p != null ? Marshal.PtrToStringUTF8((IntPtr)p) ?? "" : "";
            if (s.Length > 0) return s;
        }
        return $"key {code}";
    }
}
