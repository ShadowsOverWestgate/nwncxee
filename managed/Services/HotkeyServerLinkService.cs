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

    // A server that asks to track an implausible number of keys is refused wholesale --
    // no toolbar layout binds this many. ponytail: bump if a real layout ever needs more.
    private const int MaxArmed = 16;

    public void Initialize()
    {
        HotkeyConsent.Init(_log!);
        SelfCheck();   // the fail-closed consent policy is security-critical; verify it at startup
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
    // The set is a PROPOSAL, not an order: the server owns what a key does, the player owns
    // whether it may be tracked. Removals (shrinking capture) apply silently; additions
    // (growing capture) require an explicit OK in the client-owned consent modal.
    private static void HandleArmSet(byte* data, uint size)
    {
        var proposed = Parse(data, size);

        var (rejected, retained, additions) = Plan(HotkeyStore.Armed, proposed, MaxArmed);
        if (rejected)
        {
            _log?.Info($"ServerLink: refusing oversized arm set ({proposed.Count} > {MaxArmed})");
            return;
        }

        if (retained != null)                       // some keys were removed -- apply now, no consent needed
            HotkeyStore.Set(retained, _log!);
        if (additions.Count > 0)                    // new keys -- ask the player before arming any of them
            HotkeyConsent.Prompt(additions);
    }

    // Pure consent policy, isolated so SelfCheck can prove the fail-closed defaults:
    //   rejected  -> proposal exceeds the cap; arm nothing.
    //   retained  -> the survivors after silent removals (null when nothing was removed).
    //   additions -> keys the server wants that aren't armed yet; these need consent.
    private static (bool rejected, HashSet<int>? retained, HashSet<int> additions) Plan(
        HashSet<int> current, HashSet<int> proposed, int cap)
    {
        if (proposed.Count > cap) return (true, null, new HashSet<int>());

        var retained = new HashSet<int>(current);
        retained.IntersectWith(proposed);           // current minus anything no longer proposed
        var additions = new HashSet<int>(proposed);
        additions.ExceptWith(current);              // proposed minus what's already armed

        return (false, retained.Count == current.Count ? null : retained, additions);
    }

    private static HashSet<int> Parse(byte* data, uint size)
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
        return set;
    }

    // One runnable check on the security-critical bits: additions never sneak through, the
    // cap rejects wholesale, and a pure add never reports a removal. Logs loudly if broken.
    private static void SelfCheck()
    {
        var cur = new HashSet<int> { 1, 2, 3 };

        // pure addition: 4 must land in additions, nothing retained/removed
        var (r1, ret1, add1) = Plan(cur, new HashSet<int> { 1, 2, 3, 4 }, MaxArmed);
        bool ok = !r1 && ret1 == null && add1.SetEquals(new HashSet<int> { 4 });

        // removal + addition: 3 dropped silently (retained = {1,2}), 9 needs consent
        var (r2, ret2, add2) = Plan(cur, new HashSet<int> { 1, 2, 9 }, MaxArmed);
        ok &= !r2 && ret2 != null && ret2.SetEquals(new HashSet<int> { 1, 2 }) && add2.SetEquals(new HashSet<int> { 9 });

        // over cap: reject wholesale, arm nothing
        var (r3, _, add3) = Plan(cur, new HashSet<int> { 1, 2, 3, 4, 5 }, 4);
        ok &= r3 && add3.Count == 0;

        _log?.Info(ok ? "ServerLink: consent policy self-check passed"
                      : "ServerLink: CONSENT POLICY SELF-CHECK FAILED -- arming logic is unsafe");
    }
}
