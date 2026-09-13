namespace Sowx.Services.Hotkeys;

/// <summary>
/// The wire contract between this client extender and the SoW server (sow-codebase).
/// ALL opcodes are PLACEHOLDERS -- they must be agreed with, and matched by, the server.
/// Payloads are ASCII on purpose (no endianness, trivially debuggable).
/// </summary>
/// <remarks>
/// client -> server: KeyPress (VK), OpenUi (none), Revoke (CSV of VKs),
/// GrantToken ("&lt;hex&gt;;&lt;CSV&gt;"), AbuseContact (email, sent once on login). server -> client: ArmSet (CSV of VKs),
/// PresentToken (hex proof of prior consent -- a server holding no token can restore none,
/// so it cannot inherit consent it was never granted).
/// </remarks>
internal static class HotkeyProtocol
{
    /// <summary>The hotkey unit (#419): every symbol the four Hotkey* services cannot run without. Each service
    /// checks it first, so one unresolved member stands the whole feature down. Optional members are NOT here:
    /// OnGainFocus/OnLostFocus (stuck-key clear), g_pNuklear (NUI typing gate), the chat-prompt handlers
    /// (lockchat-only typing observer).</summary>
    public static readonly string[] RequiredSymbols =
    {
        Sowx.Framework.SymbolMap.HandleServerToPlayerMessage, Sowx.Framework.SymbolMap.HandleServerToPlayerLogin,
        Sowx.Framework.SymbolMap.SendPlayerToServerMessage, Sowx.Framework.SymbolMap.SendDeviceAdvertiseProperty,
        Sowx.Framework.SymbolMap.GetNWCMessage, Sowx.Framework.SymbolMap.AppManager,
        Sowx.Framework.SymbolMap.OkCancelCtor, Sowx.Framework.SymbolMap.OkCancelActivate,
        Sowx.Framework.SymbolMap.GuiMan, Sowx.Framework.SymbolMap.OperatorNew,
        Sowx.Framework.SymbolMap.HandleWMKeyDown, Sowx.Framework.SymbolMap.WinMessageHandler,
        Sowx.Framework.SymbolMap.ExoInput, Sowx.Framework.SymbolMap.GetKeyboardMode,
        Sowx.Framework.SymbolMap.SdlGetModState, Sowx.Framework.SymbolMap.SdlGetKeyName,
    };

    public const byte KeyPressMajor = 0x36, KeyPressMinor = 0x40;
    public const byte OpenUiMajor   = 0x36, OpenUiMinor   = 0x41;
    public const byte ArmSetMajor   = 0x36, ArmSetMinor   = 0x42;
    public const byte RevokeMajor   = 0x36, RevokeMinor   = 0x43;
    public const byte PresentTokenMajor = 0x36, PresentTokenMinor = 0x44;
    public const byte GrantTokenMajor   = 0x36, GrantTokenMinor   = 0x45;
    public const byte AbuseContactMajor = 0x36, AbuseContactMinor = 0x46;

    /// <summary>Where a server operator reports abuse of the extender. Sent on every login after SOWX_ENABLED.</summary>
    public const string AbuseContact = "mpiasecki720@protonmail.com";

    /// <summary>
    /// Mouse button N rides the KeyPress channel as MouseBase + N. Bit 28 keeps these clear
    /// of every SDLK (ASCII &lt; 128, scancode mask bit 30). MUST match sow-codebase byte for byte.
    /// </summary>
    public const int MouseBase = 1 << 28;

    // Modifier bits OR'd onto a base code to make a combo (Ctrl+F) a distinct opaque code.
    // Bits 25/26/27 are free of every base (ASCII 0-6, scancode +30, mouse +28). Matching is
    // EXACT -- Ctrl+F never fires F. MUST match sow-codebase byte for byte.
    public const int ModCtrl  = 1 << 25;
    public const int ModShift = 1 << 26;
    public const int ModAlt   = 1 << 27;
    public const int ModMask  = ModCtrl | ModShift | ModAlt;
}
