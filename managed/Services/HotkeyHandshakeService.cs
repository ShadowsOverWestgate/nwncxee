using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services;

// Announces the extender to the server on login, using NWN:EE's own capability
// channel -- CNWCMessage::SendPlayerToServerDeviceAdvertiseProperty(name, value).
// Each property is a (string, int) pair the server stores per-player. A vanilla
// client never advertises our keys, so the server simply lacks them -> treated as
// un-extended. Additive, optional, no packet-format hacking.
//
// We advertise on HandleServerToPlayerLogin(subcmd == 5) -- login accepted, the
// server holds our CNWSPlayer, the link is up. `this` there IS the CNWCMessage, so
// we advertise straight through it.
//
// Encoding (count-based -- the server handler only ever inserts, never removes, so
// a shorter re-advertise must be read by count, not by prefix scan):
//   SOWX_ENABLED  = 1
//   SOWX_HK_COUNT = N
//   SOWX_HK_<i>   = vk        (the armed keycode; the server owns the mapping)
public sealed unsafe class HotkeyHandshakeService : IExtenderService
{
    private const string LoginSym = "_ZN11CNWCMessage25HandleServerToPlayerLoginEh";
    private const string AdvSym   = "_ZN11CNWCMessage41SendPlayerToServerDeviceAdvertisePropertyERK10CExoStringi";

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
        _advertise = (delegate* unmanaged<IntPtr, IntPtr, int, void>)Native.Sym(AdvSym);
        _loginOrig = (delegate* unmanaged<IntPtr, byte, uint>)_hooks.Install(
            LoginSym, (IntPtr)(delegate* unmanaged<IntPtr, byte, uint>)&OnLogin);
    }

    [UnmanagedCallersOnly]
    private static uint OnLogin(IntPtr msg, byte subcmd)
    {
        // `msg` is the live CNWCMessage; cache it for outbound sends (see ClientApi.LiveMessage).
        ClientApi.LiveMessage = msg;
        uint r = _loginOrig != null ? _loginOrig(msg, subcmd) : 1;
        if (subcmd == 5) Advertise(msg);   // 5 = login accepted / entering game
        return r;
    }

    private static void Advertise(IntPtr msg)
    {
        if (_advertise == null) return;
        var armed = HotkeyStore.Armed;

        Adv(msg, "SOWX_ENABLED", 1);
        Adv(msg, "SOWX_HK_COUNT", armed.Count);
        int i = 0;
        foreach (int vk in armed)
            Adv(msg, $"SOWX_HK_{i++}", vk);

        _log?.Info($"Handshake: advertised extender + {armed.Count} armed key(s)");
    }

    private static void Adv(IntPtr msg, string name, int value)
    {
        IntPtr s = Native.NewExoString(name);
        _advertise(msg, s, value);
        Native.FreeExoString(s);
    }
}
