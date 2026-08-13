using System.Runtime.InteropServices;
using System.Text;
using Sowx.Framework;

namespace Sowx.Services;

// The Options-menu entry point. Repurposes the in-game Esc-menu's "Save Character"
// button (COptionsPanel, resref OPTIONS_MAIN) -- a local-vault copy that is
// meaningless on a server-vault PW -- into "Hotkeys":
//   * PostAttachmentInitialize (after): relabel the button to "Hotkeys".
//   * HandleSaveCharButton (replace): open the client hotkey manager and close the menu.
//
// Both are done with hooks, not a .gui override, because the panel re-sets the
// button's label in code after importing the layout (so a data edit is clobbered).
public sealed unsafe class HotkeyMenuService : IExtenderService
{
    // CAuroraStringWrapper for SaveCharButton, embedded at this offset in COptionsPanel
    // (from PostAttachmentInitialize: SetText((this + 0x4b0), 0x19a0)).
    private const int SaveCharTextOffset = 0x4b0;
    // Inside a CAuroraStringWrapper, +0x10 is m_pString (the label object); its vtable slot +0xd8 is
    // "set text from a char*". This is the exact primitive CAuroraStringWrapper::SetText(strref) and
    // CPanelOkCancel::Activate both call after fetching the TLK -- we skip the TLK and pass a literal,
    // so "Hotkeys" needs no custom TLK entry.
    private const int WrapperStringOffset = 0x10;
    private const int SetTextFromCharVtblOffset = 0xd8;

    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, void> _postOrig;
    private static delegate* unmanaged<IntPtr, void> _saveOrig;   // unused (we replace), kept for symmetry
    private static delegate* unmanaged<IntPtr, void> _closeMenu;  // COptionsPanel::HandleXButton

    private readonly HookService _hooks;

    public HotkeyMenuService(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _hooks = hooks;
    }

    public void Initialize()
    {
        _closeMenu = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.OptionsHandleXButton);
        _postOrig = (delegate* unmanaged<IntPtr, void>)_hooks.Install(
            SymbolMap.PostAttachmentInitialize, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnPostAttach);
        _saveOrig = (delegate* unmanaged<IntPtr, void>)_hooks.Install(
            SymbolMap.HandleSaveCharButton, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnHotkeysButton);
    }

    [UnmanagedCallersOnly]
    private static void OnPostAttach(IntPtr panel)
    {
        if (_postOrig != null) _postOrig(panel);   // let it import + wire everything first
        SetLabel(panel + SaveCharTextOffset, "Hotkeys");
    }

    // Set a CAuroraStringWrapper's text to a literal string (cold path, once per panel open).
    private static void SetLabel(IntPtr wrapper, string text)
    {
        IntPtr pString = *(IntPtr*)(wrapper + WrapperStringOffset);
        if (pString == IntPtr.Zero) return;
        IntPtr vtbl = *(IntPtr*)pString;
        var setText = (delegate* unmanaged<IntPtr, byte*, void>)*(IntPtr*)(vtbl + SetTextFromCharVtblOffset);

        byte[] buf = new byte[Encoding.ASCII.GetByteCount(text) + 1];   // NUL-terminated char*
        Encoding.ASCII.GetBytes(text, buf);
        fixed (byte* p = buf) setText(pString, p);
    }

    // Replaces HandleSaveCharButton (which saved a local-vault character -- meaningless here).
    // Toggles the client-native hotkey manager and closes the Options menu so the manager is
    // visible over the game. Fallback: if the manager can't run (nuklear missing, e.g. Windows),
    // open the server's binding NUI so the button is never dead.
    [UnmanagedCallersOnly]
    private static void OnHotkeysButton(IntPtr panel)
    {
        if (HotkeyManagerService.Toggle())
            _log?.Info("Hotkeys button: toggled client hotkey manager");
        else
        {
            ClientApi.Send(HotkeyProtocol.OpenUiMajor, HotkeyProtocol.OpenUiMinor, null, 0);
            _log?.Info("Hotkeys button: manager unavailable, requested server NUI");
        }

        if (_closeMenu != null) _closeMenu(panel);   // close the Options menu (same path as Resume)
    }
}
