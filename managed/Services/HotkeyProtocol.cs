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
//     KeyPress   : an armed key was pressed.   payload = ASCII decimal VK, e.g. "112"
//     OpenUi     : player asked for the UI.    payload = none
//     Revoke     : player unbound/denied keys. payload = ASCII CSV of VKs, e.g. "112,113"
//                  (server forgets the binding, so it stops re-pushing them)
//     GrantToken : consent receipt.            payload = "<tokenhex>;<CSV of VKs>"
//                  (after the player consents, the client mints a token and hands it here; the
//                   server stores it per player and presents it back on the next login)
//   server -> client
//     ArmSet       : here is the armed set.    payload = ASCII CSV of VKs, e.g. "112,113"
//                    (empty payload = arm nothing)
//     PresentToken : proof of prior consent.   payload = "<tokenhex>"
//                    (the token the client gave last time; the client looks it up to restore that
//                     server's consented keys. A server that holds no token can present none, so it
//                     cannot restore -- and thus cannot inherit -- consent it was never granted.)
internal static class HotkeyProtocol
{
    public const byte KeyPressMajor = 0x36, KeyPressMinor = 0x40;
    public const byte OpenUiMajor   = 0x36, OpenUiMinor   = 0x41;
    public const byte ArmSetMajor   = 0x36, ArmSetMinor   = 0x42;
    // Revoke (client -> server): the player unbound / denied these keys. payload = ASCII CSV of
    // keycodes, e.g. "112,113". The server forgets the binding so it stops pushing them back.
    public const byte RevokeMajor   = 0x36, RevokeMinor   = 0x43;
    // PresentToken (server -> client): the consent token the client granted last time. payload = hex.
    public const byte PresentTokenMajor = 0x36, PresentTokenMinor = 0x44;
    // GrantToken (client -> server): consent receipt after the player OKs. payload = "<hex>;<CSV>".
    public const byte GrantTokenMajor   = 0x36, GrantTokenMinor   = 0x45;

    // Mouse buttons ride the same KeyPress channel as keys: SDL button index N is
    // encoded as MouseBase + N. Bit 28 keeps these clear of every SDLK (ASCII < 128,
    // scancode mask is bit 30), so a mouse code never aliases a key. This constant
    // MUST match sow-codebase HotkeyProtocol.MouseBase byte for byte.
    public const int MouseBase = 1 << 28;
}
