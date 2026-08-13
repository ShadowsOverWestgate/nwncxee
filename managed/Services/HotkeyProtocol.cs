namespace Sowx.Services;

// The wire contract between this client extender and the SoW server (sow-codebase).
// ALL opcodes here are PLACEHOLDERS -- they must be agreed with, and matched by,
// the server end. Reuse existing majors the server already routes; the server only
// ever sends the server->client message to clients that advertised SOWX_ENABLED
// (see HotkeyHandshakeService), so vanilla clients are never affected.
//
// Payloads are ASCII text on purpose -- no endianness to get wrong across repos,
// and trivially debuggable.
//
//   client -> server
//     KeyPress : an armed key was pressed.   payload = ASCII decimal VK, e.g. "112"
//     OpenUi   : player asked for the UI.    payload = none
//   server -> client
//     ArmSet   : here is the armed set.      payload = ASCII CSV of VKs, e.g. "112,113"
//                (empty payload = arm nothing)
internal static class HotkeyProtocol
{
    public const byte KeyPressMajor = 0x36, KeyPressMinor = 0x40;
    public const byte OpenUiMajor   = 0x36, OpenUiMinor   = 0x41;
    public const byte ArmSetMajor   = 0x36, ArmSetMinor   = 0x42;
}
