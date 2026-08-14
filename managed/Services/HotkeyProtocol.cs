namespace Sowx.Services;

/// <summary>
/// The wire contract between this client extender and the SoW server (sow-codebase).
/// ALL opcodes are PLACEHOLDERS -- they must be agreed with, and matched by, the server.
/// Payloads are ASCII on purpose (no endianness, trivially debuggable).
/// </summary>
/// <remarks>
/// client -> server: KeyPress (VK), OpenUi (none), Revoke (CSV of VKs),
/// GrantToken ("&lt;hex&gt;;&lt;CSV&gt;"). server -> client: ArmSet (CSV of VKs),
/// PresentToken (hex proof of prior consent -- a server holding no token can restore none,
/// so it cannot inherit consent it was never granted).
/// </remarks>
internal static class HotkeyProtocol
{
    public const byte KeyPressMajor = 0x36, KeyPressMinor = 0x40;
    public const byte OpenUiMajor   = 0x36, OpenUiMinor   = 0x41;
    public const byte ArmSetMajor   = 0x36, ArmSetMinor   = 0x42;
    public const byte RevokeMajor   = 0x36, RevokeMinor   = 0x43;
    public const byte PresentTokenMajor = 0x36, PresentTokenMinor = 0x44;
    public const byte GrantTokenMajor   = 0x36, GrantTokenMinor   = 0x45;

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
