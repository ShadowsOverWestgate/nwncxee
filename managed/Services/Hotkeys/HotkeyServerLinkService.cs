using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services.Hotkeys;

/// <summary>
/// Receives the armed-key set from the server (a server-authored NUI arms keys; the server
/// sends the result here). Hooks the client's inbound dispatcher and intercepts only our
/// ArmSet/PresentToken opcodes -- every other message passes straight through.
/// </summary>
public sealed unsafe class HotkeyServerLinkService : IExtenderService
{
    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, byte*, uint, uint> _inboundOrig;

    /// <summary>
    /// A server asking to track an implausible number of keys is refused wholesale, to bound a
    /// pathological payload -- NOT to limit the player (additions require consent regardless). The
    /// real ceiling is one key per toolbar button (~80 offered), so 256 is generous headroom.
    /// </summary>
    private const int MaxArmed = 256;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        HotkeyConsent.Init(_log!);
        SelfCheck();   // the fail-closed consent policy is security-critical; verify it at startup
        _inboundOrig = (delegate* unmanaged<IntPtr, byte*, uint, uint>)hooks.Install(
            SymbolMap.HandleServerToPlayerMessage, (IntPtr)(delegate* unmanaged<IntPtr, byte*, uint, uint>)&OnInbound);
    }

    // The engine frames every server->client message as ['P'][major][minor][payload...]
    // (decomp-verified: HandleServerToPlayerMessage gates on data[0]=='P', reads major/minor at
    // [1]/[2]). So we match past the 'P' marker, not from data[0].
    private const byte FrameMarker = (byte)'P';

    [UnmanagedCallersOnly]
    private static uint OnInbound(IntPtr self, byte* data, uint size)
    {
        ClientApi.LiveMessage = self;   // the live CNWCMessage; cache for outbound sends (g_pAppManager segfaults here)

        if (size >= 3 && data[0] == FrameMarker && data[1] == HotkeyProtocol.ArmSetMajor)  // 0x36: our device major
        {
            switch (data[2])
            {
                case HotkeyProtocol.PresentTokenMinor:   // restore this server's prior consent (sent before ArmSet)
                    HandlePresentToken(data, size);
                    return 1;                            // consume: ours, the stock client must not see it
                case HotkeyProtocol.ArmSetMinor:
                    HandleArmSet(data, size);
                    return 1;
            }
        }
        return _inboundOrig != null ? _inboundOrig(self, data, size) : 1;
    }

    /// <summary>
    /// Apply a proposed armed set. The set is a PROPOSAL: removals (shrinking capture) apply
    /// silently; additions (growing capture) require an explicit OK in the client-owned consent modal.
    /// </summary>
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

    /// <summary>
    /// Pure consent policy, isolated so <see cref="SelfCheck"/> can prove the fail-closed defaults.
    /// rejected = over cap (arm nothing); retained = survivors after silent removals (null if none
    /// removed); additions = proposed keys not yet armed (these need consent).
    /// </summary>
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

    /// <summary>
    /// The server presents the token it was granted last time (payload = hex). Recognised -> the
    /// armed set becomes that server's consented keys, so the following ArmSet produces no additions
    /// and no re-prompt. Unrecognised / empty -> armed set stays empty, awaiting consent.
    /// </summary>
    private static void HandlePresentToken(byte* data, uint size)
    {
        string tok = AsciiPayload(data, size);
        if (tok.Length == 0) { HotkeyStore.BeginSession(); return; }   // server holds no token -> nothing to restore
        HotkeyStore.Activate(tok, _log!);
    }

    private static HashSet<int> Parse(byte* data, uint size)
    {
        var set = new HashSet<int>();
        foreach (string part in AsciiPayload(data, size).Split(new[] { ',', ';', ' ', '\n', '\r', '\t' },
                                                               StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, out int vk)) set.Add(vk);
        return set;
    }

    /// <summary>The ASCII payload after the 3-byte ['P'][major][minor] header, trimmed.</summary>
    private static string AsciiPayload(byte* data, uint size)
    {
        if (size <= 3) return "";
        int len = (int)size - 3;
        var bytes = new byte[len];
        Marshal.Copy((IntPtr)(data + 3), bytes, 0, len);
        return System.Text.Encoding.ASCII.GetString(bytes).Trim();
    }

    /// <summary>
    /// One runnable check on the security-critical bits: additions never sneak through, the cap
    /// rejects wholesale, and a pure add never reports a removal. Logs loudly if broken.
    /// </summary>
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
