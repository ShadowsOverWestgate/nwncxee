using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services.Hotkeys;

/// <summary>
/// Announces the extender to the server on login via NWN:EE's own capability channel
/// (SendPlayerToServerDeviceAdvertiseProperty). A vanilla client never advertises, so the
/// server treats it as un-extended -- additive, optional, no packet-format hacking.
/// </summary>
/// <remarks>
/// Advertises ONLY SOWX_ENABLED=1, never the armed key list: consent is per-server under
/// the token model, and advertising the list was the old global-consent leak. The server
/// owns its bindings and re-pushes them via ArmSet.
/// </remarks>
public sealed unsafe class HotkeyHandshakeService : IExtenderService
{
    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, byte, uint> _loginOrig;
    private static delegate* unmanaged<IntPtr, IntPtr, int, void> _advertise;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!SymbolMap.Require(log, "HotkeyHandshakeService", HotkeyProtocol.RequiredSymbols)) return;
        _advertise = (delegate* unmanaged<IntPtr, IntPtr, int, void>)Native.Sym(SymbolMap.SendDeviceAdvertiseProperty);
        _loginOrig = (delegate* unmanaged<IntPtr, byte, uint>)hooks.Install(
            SymbolMap.HandleServerToPlayerLogin, (IntPtr)(delegate* unmanaged<IntPtr, byte, uint>)&OnLogin);
    }

    [UnmanagedCallersOnly]
    private static uint OnLogin(IntPtr msg, byte subcmd)
    {
        ClientApi.LiveMessage = msg;                        // live CNWCMessage; cache for outbound sends
        uint r = _loginOrig != null ? _loginOrig(msg, subcmd) : 1;
        if (subcmd == 5)                                    // 5 = login accepted / entering game
        {
            // Fresh session: drop any prior server's active consent and clear half-answered
            // prompts / stale manager windows. Nothing is armed until the server presents a
            // recognised token.
            HotkeyStore.BeginSession();
            HotkeyConsent.Reset();
            HotkeyManagerService.Close();
            Advertise(msg);
        }
        return r;
    }

    private static void Advertise(IntPtr msg)
    {
        if (_advertise == null) return;
        Adv(msg, "SOWX_ENABLED", 1);
        _log?.Info("Handshake: advertised extender (no key list -- consent is per-server via token)");
    }

    private static void Adv(IntPtr msg, string name, int value)
    {
        IntPtr s = Native.NewExoString(name);
        _advertise(msg, s, value);
        Native.FreeExoString(s);
    }
}
