using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services;

// Client-owned consent gate. A server ArmSet that ADDS keys never arms them silently;
// it raises the engine's own native yes/no modal -- CPanelOkCancel, the same panel the
// "quit to main menu" dialog uses. The modal is drawn and owned by the client, so a
// malicious server can neither forge it nor auto-confirm it. Only an OK click arms the
// keys; Esc/Cancel/close all deny. This is the difference between "trust the server" and
// "the server cannot": the server proposes semantics, the player authorises capture.
//
// Mechanism, mirrored from the engine's own spawn path (decomp-verified on both platforms --
// Linux COptionsPanel::HandleQuitClick, Windows the equivalent ExitButton handler):
//   p = operator new(sizeof); CPanelOkCancel::CPanelOkCancel(p);
//   p+0x80 = context;  p+0x88 = on-close callback;  (Linux also zeroes p+0x90)
//   CPanelOkCancel::Activate(msg, OK_STRREF, CANCEL_STRREF);
// On close the engine walks CGuiPanel::Deactivate -> the panel's OnDeactivate vtable slot
// -> callback(context, result); result==1 is OK, anything else is deny (Esc routes through
// Cancel, so it lands here as a deny for free). The panel then self-frees via its deleting
// destructor -> operator delete(p); we allocate with the matching operator new (the game's
// own on Windows -- static CRT -- so the free lands on the same heap) and never touch p after
// handing it to Activate. The callback fields sit at the same offsets under both ABIs; only
// the struct size differs (0x3a0 Itanium / 0x360 MSVC) and Linux's extra +0x90=0 write.
//
// The message CExoString is passed by value: Itanium destroys it in the caller (we free it
// after Activate returns); MSVC destroys it in the callee (Activate frees + zeroes it, so our
// later dtor sees a null buffer -> a no-op, not a double-free). FreeExoString is correct either
// way -- see Native.NewExoString.
//
// Cold path: proposals arrive on the inbound-network hook and the answer on a GUI click,
// both on the main thread -- allocation is fine and there is no cross-thread race on _pending.
internal static unsafe class HotkeyConsent
{
    // sizeof(CPanelOkCancel): 0x3a0 under Itanium, 0x360 under MSVC (both from the operator new
    // arg in the engine's own spawn path). Wrong size = the ctor/dtor touch out-of-bounds bytes.
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

    // A fresh session must not inherit a half-answered prompt. Called on login; fail-closed
    // -- any pending (unconfirmed) keys are dropped, never armed.
    public static void Reset() => _pending = null;

    // Ask the player to consent to arming `additions`. Returns true only if the modal was
    // actually shown. Every false path -- missing symbol, not in-game, a prompt already open
    // -- means "not armed"; the caller must not arm on false.
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
        // Linux HandleQuitClick zeroes +0x90; the MSVC spawn path doesn't (the ctor leaves it safe,
        // as its own shipping code proves). Mirror each platform's real code -- don't write blind.
        if (!OperatingSystem.IsWindows())
            *(IntPtr*)(panel + PadOff) = IntPtr.Zero;

        IntPtr msg = Native.NewExoString(Describe(additions));
        _activate(panel, msg, OkStrRef, CancelStrRef);   // Linux overload copies the text; does not own msg
        Native.FreeExoString(msg);

        _pending = additions;
        _log?.Info($"HotkeyConsent: prompting to arm {additions.Count} key(s)");
        return true;
    }

    // Is the GUI manager up? g_pGuiMan is the exact global CGuiModalPanel::Activate itself
    // uses, so non-null == safe to attach a modal. We only READ its value (never deref it
    // as an object), so a wrong resolution can't fault -- unlike the g_pAppManager path,
    // which segfaults on this build (see ClientApi). Null before the GUI loads -> we refuse.
    private static bool GuiReady() => *(IntPtr*)_guiManSym != IntPtr.Zero;

    // The engine's on-close callback: result 1 = OK, anything else = deny. Clear _pending
    // FIRST -- the panel self-frees right after this returns, and a new proposal must be
    // able to prompt again.
    [UnmanagedCallersOnly]
    private static void OnAnswer(IntPtr context, int result)
    {
        var answered = _pending;
        _pending = null;
        if (answered == null) return;
        if (result == 1)
        {
            // Record consent under this server's token (minting one if the server presented none),
            // then hand the token + consented keys back so the server can present it next login.
            string token = HotkeyStore.Grant(answered, _log!);
            SendGrantToken(token);
        }
        else
        {
            // Deny -> tell the server to forget the binding, or it re-pushes these every login
            // and re-prompts forever. This is the client revoking consent it never granted.
            Revoke(answered);
            _log?.Info("HotkeyConsent: player declined; revoked on the server");
        }
    }

    // Send a Revoke for `keys` so the server drops the binding (unbinds the toolbar/metamagic
    // slot's key). Used by deny above and by any client-initiated unbind.
    public static void Revoke(IEnumerable<int> keys)
    {
        string csv = string.Join(",", keys);
        if (csv.Length == 0) return;
        ClientApi.SendAscii(HotkeyProtocol.RevokeMajor, HotkeyProtocol.RevokeMinor, csv);
    }

    // Hand the server the consent receipt: the token this server's consent is stored under, plus the
    // keys the player actually consented to. The server persists the token per player and presents it
    // back on the next login (PresentToken) to restore consent without re-prompting.
    private static void SendGrantToken(string tokenHex)
    {
        string csv = string.Join(",", HotkeyStore.Armed);
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

    // Human-readable label. Armed codes are SDL keycodes (SDLK), which SDL_GetKeyName maps
    // straight to "F", "F1", etc.; mouse codes (MouseBase + button) are ours, so we name them.
    // The mouse range is only [MouseBase, MouseBase+0xFF] (button is a byte) -- SDLK scancode
    // keys sit at 0x40000000+ (bit 30), which is also >= MouseBase, so bound the range or every
    // F-key/arrow gets mislabelled "Mouse". Shared with HotkeyManagerService's list.
    internal static string KeyName(int code)
    {
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
