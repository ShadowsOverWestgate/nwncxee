using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services;

// Receives the armed-key set from the server. The arming UI is a server-authored
// NUI (sow-codebase owns the toolbar layout and builds many NUIs); when the player
// arms keys there, the server sends the resulting set here. We update the consent
// set and cache it locally so armed keys work immediately and survive a restart.
//
// Hooks the client's inbound dispatcher and intercepts only our ArmSet opcode --
// every other message passes straight through to the original.
public sealed unsafe class HotkeyServerLinkService : IExtenderService
{
    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, byte*, uint, uint> _inboundOrig;

    private readonly HookService _hooks;

    public HotkeyServerLinkService(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _hooks = hooks;
    }

    public void Initialize()
    {
        _inboundOrig = (delegate* unmanaged<IntPtr, byte*, uint, uint>)_hooks.Install(
            SymbolMap.HandleServerToPlayerMessage, (IntPtr)(delegate* unmanaged<IntPtr, byte*, uint, uint>)&OnInbound);
    }

    // The engine frames every server->client message as ['P'][major][minor][payload...]; the stock
    // CNWCMessage::HandleServerToPlayerMessage gates on data[0]=='P' and reads the major/minor at [1]/[2]
    // (decomp-verified: it asserts nBufferSize>=3 and its own debug labels name data[1]=nMajor,
    // data[2]=nMinor). So we match past the 'P' marker, not from data[0].
    private const byte FrameMarker = (byte)'P';

    [UnmanagedCallersOnly]
    private static uint OnInbound(IntPtr self, byte* data, uint size)
    {
        // `self` IS the live CNWCMessage the engine uses; cache it so outbound sends go through a
        // known-good pointer instead of resolving g_pAppManager (which segfaulted on this build).
        ClientApi.LiveMessage = self;

        if (size >= 3 && data[0] == FrameMarker
            && data[1] == HotkeyProtocol.ArmSetMajor && data[2] == HotkeyProtocol.ArmSetMinor)
        {
            HandleArmSet(data, size);
            return 1;   // consume: this message is ours, the stock client should not see it
        }
        return _inboundOrig != null ? _inboundOrig(self, data, size) : 1;
    }

    // payload (after the 'P' marker + 2 opcode bytes) = ASCII CSV of VK codes, e.g. "112,113,114".
    private static void HandleArmSet(byte* data, uint size)
    {
        var set = new HashSet<int>();
        if (size > 3)
        {
            int len = (int)size - 3;
            var bytes = new byte[len];
            Marshal.Copy((IntPtr)(data + 3), bytes, 0, len);
            string csv = System.Text.Encoding.ASCII.GetString(bytes);
            foreach (string part in csv.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' },
                                              StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out int vk)) set.Add(vk);
        }

        HotkeyStore.Armed = set;
        HotkeyStore.Save(_log!);
        _log?.Info($"ServerLink: server armed {set.Count} key(s)");
    }
}
