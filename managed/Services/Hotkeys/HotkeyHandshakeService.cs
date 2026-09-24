using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Hotkeys;

/// <summary>
/// Announces the extender to the server on login via NWN:EE's own capability channel
/// (SendPlayerToServerDeviceAdvertiseProperty), then sends the abuse-report contact
/// as an ASCII device message (AbuseContact). A vanilla client never advertises, so the
/// server treats it as un-extended -- additive, optional, no packet-format hacking.
/// </summary>
/// <remarks>
/// Advertises ONLY CX_ENABLED=1, never the armed key list: consent is per-server under
/// the token model, and advertising the list was the old global-consent leak. The server
/// owns its bindings and re-pushes them via ArmSet. Advertises whatever <c>hotkeys.enabled</c>
/// says (spec §4.5): the master gates the actions, never the handshake.
/// </remarks>
public sealed unsafe class HotkeyHandshakeService : IExtenderService
{
    private static ExtenderLog? _log;
    private static HandleServerToPlayerLoginFn _loginOrig;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!SymbolMap.Require(log, "HotkeyHandshakeService", HotkeyProtocol.RequiredSymbols)) return;
        _loginOrig = hooks.InstallHandleServerToPlayerLogin(&OnLogin);
    }

    [UnmanagedCallersOnly]
    private static uint OnLogin(IntPtr msg, byte subcmd)
    {
        ClientApi.LiveMessage = msg;                        // live CNWCMessage; cache for outbound sends
        uint r = _loginOrig != null ? _loginOrig(msg, subcmd) : 1;
        if (subcmd == 5)                                    // 5 = login accepted / entering game
        {
            // Fresh session: drop any prior server's active consent and clear half-answered
            // prompts. Nothing is armed until the server presents a recognised token.
            HotkeyStore.BeginSession();
            HotkeyConsent.Reset();
            Advertise(msg);
        }
        return r;
    }

    private static void Advertise(IntPtr msg)
    {
        if (EngineFn.SendDeviceAdvertiseProperty == null) return;
        Adv(msg, "CX_ENABLED", 1);
        ClientApi.SendAscii(HotkeyProtocol.AbuseContactMajor, HotkeyProtocol.AbuseContactMinor, HotkeyProtocol.AbuseContact);
        _log?.Info("Handshake: advertised extender + abuse contact (no key list -- consent is per-server via token)");
    }

    private static void Adv(IntPtr msg, string name, int value)
    {
        IntPtr s = Native.NewExoString(name);
        EngineFn.SendDeviceAdvertiseProperty(msg, s, value);
        Native.FreeExoString(s);
    }
}
