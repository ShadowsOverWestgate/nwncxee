using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services;

// The Options-menu entry point. Repurposes the in-game Esc-menu's "Save Character"
// button (COptionsPanel, resref OPTIONS_MAIN) -- a local-vault copy that is
// meaningless on a server-vault PW -- into "Hotkeys":
//   * PostAttachmentInitialize (after): optionally relabel the button.
//   * HandleSaveCharButton (replace): reload the consent list instead of saving.
//
// Both are done with hooks, not a .gui override, because the panel re-sets the
// button's label in code after importing the layout (so a data edit is clobbered).
public sealed unsafe class HotkeyMenuService : IExtenderService
{
    // A real TLK strref (a custom entry shipped via sow-topdata) to show "Hotkeys"
    // on the button. Left as MaxValue = keep the native label until that entry
    // exists; SetText takes a strref, not a literal, so there is nothing else to use.
    private const uint LabelStrRef = uint.MaxValue;

    // CAuroraStringWrapper for SaveCharButton, embedded at this offset in COptionsPanel
    // (from PostAttachmentInitialize: SetText((this + 0x4b0), 0x19a0)).
    private const int SaveCharTextOffset = 0x4b0;

    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, void> _postOrig;
    private static delegate* unmanaged<IntPtr, void> _saveOrig;   // unused (we replace), kept for symmetry
    private static delegate* unmanaged<IntPtr, uint, void> _setText;

    private readonly HookService _hooks;

    public HotkeyMenuService(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _hooks = hooks;
    }

    public void Initialize()
    {
        _setText = (delegate* unmanaged<IntPtr, uint, void>)Native.Sym(SymbolMap.SetText);
        _postOrig = (delegate* unmanaged<IntPtr, void>)_hooks.Install(
            SymbolMap.PostAttachmentInitialize, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnPostAttach);
        _saveOrig = (delegate* unmanaged<IntPtr, void>)_hooks.Install(
            SymbolMap.HandleSaveCharButton, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnHotkeysButton);
    }

    [UnmanagedCallersOnly]
    private static void OnPostAttach(IntPtr panel)
    {
        if (_postOrig != null) _postOrig(panel);   // let it import + wire everything first
        if (LabelStrRef != uint.MaxValue && _setText != null)
            _setText(panel + SaveCharTextOffset, LabelStrRef);
    }

    // Replaces HandleSaveCharButton -- deliberately does NOT call the original
    // (that saved a local-vault character). Asks the server to open the arming NUI
    // (the server owns the toolbar layout and builds the window; see
    // HotkeyServerLinkService for the reply). No local UI is drawn here.
    [UnmanagedCallersOnly]
    private static void OnHotkeysButton(IntPtr panel)
    {
        ClientApi.Send(HotkeyProtocol.OpenUiMajor, HotkeyProtocol.OpenUiMinor, null, 0);
        _log?.Info("Hotkeys button: requested arming NUI from server");
    }
}
