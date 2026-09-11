using System.Runtime.InteropServices;
using System.Text;
using Sowx.Framework;

namespace Sowx.Services.Hotkeys;

/// <summary>
/// The Options-menu entry point. Repurposes the in-game Esc-menu's "Save Character" button
/// (a local-vault copy, meaningless on a server-vault PW) into "Hotkeys":
/// PostAttachmentInitialize relabels it; HandleSaveCharButton opens the client hotkey manager.
/// </summary>
/// <remarks>
/// Done with hooks, not a .gui override, because the panel re-sets the button's label in code
/// after importing the layout (a data edit is clobbered).
/// </remarks>
public sealed unsafe class HotkeyMenuService : IExtenderService
{
    // CAuroraStringWrapper for SaveCharButton, embedded here in COptionsPanel (from
    // PostAttachmentInitialize: SetText((this + 0x4b0), 0x19a0)).
    private const int SaveCharTextOffset = 0x4b0;
    // Wrapper +0x10 is m_pString; its vtable slot +0xd8 is "set text from char*" -- the exact
    // primitive SetText(strref) / Activate call after fetching the TLK. We pass a literal, so
    // "Hotkeys" needs no custom TLK entry.
    private const int WrapperStringOffset = 0x10;
    private const int SetTextFromCharVtblOffset = 0xd8;

    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, void> _postOrig;
    private static delegate* unmanaged<IntPtr, void> _closeMenu;  // COptionsPanel::HandleXButton

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // Retired by #422; until that lands it stays Linux-only: the +0x4b0 wrapper and the Itanium vtable slot
        // below are not the MSVC layout, and the RVA hook fires on Windows (ESC crashed the client, #418).
        if (OperatingSystem.IsWindows()) { log.Info("HotkeyMenuService: standing down on Windows (Options-panel button retired, #422)"); return; }
        _closeMenu = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.OptionsHandleXButton);
        _postOrig = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.PostAttachmentInitialize, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnPostAttach);
        // Replace, never call through: install the hook, discard the trampoline.
        hooks.Install(SymbolMap.HandleSaveCharButton, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnHotkeysButton);
    }

    [UnmanagedCallersOnly]
    private static void OnPostAttach(IntPtr panel)
    {
        if (_postOrig != null) _postOrig(panel);   // let it import + wire everything first
        SetLabel(panel + SaveCharTextOffset, "Hotkeys");
    }

    /// <summary>Set a CAuroraStringWrapper's text to a literal string (cold, once per panel open).</summary>
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

    /// <summary>
    /// Replaces HandleSaveCharButton: toggles the client hotkey manager and closes the menu.
    /// Fallback if the manager can't run (nuklear missing): open the server's binding NUI so the
    /// button is never dead.
    /// </summary>
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
