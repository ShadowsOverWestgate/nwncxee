using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services;

// Announces the extender to the server on login, using NWN:EE's own capability
// channel -- CNWCMessage::SendPlayerToServerDeviceAdvertiseProperty(name, value).
// A vanilla client never advertises, so the server simply treats it as un-extended.
// Additive, optional, no packet-format hacking.
//
// We advertise on HandleServerToPlayerLogin(subcmd == 5) -- login accepted, the
// server holds our CNWSPlayer, the link is up. `this` there IS the CNWCMessage.
//
// We advertise ONLY that the extender is present:
//   SOWX_ENABLED = 1
// We deliberately do NOT advertise the armed key list. Consent is per-server under the
// token model (see HotkeyStore): a fresh session starts with an EMPTY armed set, and the
// server proves prior consent by presenting the token the client gave it (PresentToken),
// which the client looks up to restore that server's consented keys. Advertising the key
// list here was the old global-consent leak -- it told every server, trusted or not, the
// exact keys to harvest. The server owns its bindings and re-pushes them via ArmSet.
public sealed unsafe class HotkeyHandshakeService : IExtenderService
{
    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, byte, uint> _loginOrig;
    private static delegate* unmanaged<IntPtr, IntPtr, int, void> _advertise;

    private readonly HookService _hooks;

    public HotkeyHandshakeService(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _hooks = hooks;
    }

    public void Initialize()
    {
        _advertise = (delegate* unmanaged<IntPtr, IntPtr, int, void>)Native.Sym(SymbolMap.SendDeviceAdvertiseProperty);
        _loginOrig = (delegate* unmanaged<IntPtr, byte, uint>)_hooks.Install(
            SymbolMap.HandleServerToPlayerLogin, (IntPtr)(delegate* unmanaged<IntPtr, byte, uint>)&OnLogin);
    }

    [UnmanagedCallersOnly]
    private static uint OnLogin(IntPtr msg, byte subcmd)
    {
        // `msg` is the live CNWCMessage; cache it for outbound sends (see ClientApi.LiveMessage).
        ClientApi.LiveMessage = msg;
        uint r = _loginOrig != null ? _loginOrig(msg, subcmd) : 1;
        if (subcmd == 5)
        {
            // Fresh session: drop any prior server's active consent (empty armed set) and clear
            // half-answered prompts / stale manager windows. The server now proves consent by
            // presenting its token; nothing is armed until it does and the token is recognised.
            HotkeyStore.BeginSession();
            HotkeyConsent.Reset();
            HotkeyManagerService.Close();
            Advertise(msg);                 // 5 = login accepted / entering game
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
