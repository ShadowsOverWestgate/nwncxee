using System;
using System.Collections.Generic;
using Sowx.Framework;

namespace Sowx.Services.Chat.Core;

/// <summary>Pure send-decision for Tell tabs (ADR 0002): typed target beats pin; pin resolves via
/// <see cref="IPlayerResolver"/>; unresolvable targets drop (never rerouted).</summary>
public static class TellTarget
{
    /// <summary>Resolve the Tell target for a tab. <paramref name="typedName"/> (from a <c>/t "Name"</c>
    /// command) beats the tab pin; either resolves via <paramref name="roster"/>. Returns a PlayerId, or
    /// null (= drop).</summary>
    public static uint? Resolve(Tab tab, string? typedName, IPlayerResolver? roster)
    {
        if (!string.IsNullOrEmpty(typedName))
            return roster?.Resolve(typedName);
        if (tab.PinnedName.Length > 0)
            return roster?.Resolve(tab.PinnedName);
        return null;
    }

    /// <summary>Same precedence as <see cref="Resolve"/> but returns the effective player name
    /// (for name-based send). No client-side presence gate: the server validates the name,
    /// so stale tabs and cross-area players are not silently dropped.</summary>
    public static string? ResolveName(Tab tab, string? typedName, IPlayerResolver? roster)
    {
        return !string.IsNullOrEmpty(typedName) ? typedName
            : tab.PinnedName.Length > 0 ? tab.PinnedName : null;
    }

    /// <summary>Off-device proof of the three AC cases + edge cases.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"TellTarget.SelfCheck FAIL: {w}"); } }

        var roster = new FakeRoster();
        roster.Players["Alice"] = 42u;
        roster.Players["Bob"] = 99u;

        // Pinned + online -> resolves to the live id
        var pinned = new Tab("Tell:Alice", 0x002) { PinnedName = "Alice" };
        E(Resolve(pinned, null, roster) == 42u, "pinned online resolves");

        // Pinned + offline -> null (drop, never reroute)
        var offline = new Tab("Tell:Eve", 0x002) { PinnedName = "Eve" };
        E(Resolve(offline, null, roster) == null, "pinned offline drops");

        // Typed target beats pin: /t "Bob" on an Alice-pinned tab sends to Bob
        E(Resolve(pinned, "Bob", roster) == 99u, "typed target beats pin");

        // Typed target offline -> drop (not rerouted to pin)
        E(Resolve(pinned, "Eve", roster) == null, "typed offline drops even with valid pin");

        // Unpinned + no typed target -> null
        var unpinned = new Tab("Tell", 0x002);
        E(Resolve(unpinned, null, roster) == null, "unpinned with no target is null");

        // Unpinned + typed target -> resolves
        E(Resolve(unpinned, "Alice", roster) == 42u, "unpinned with typed target resolves");

        // Case-sensitive: engine uses strcmp, so "alice" != "Alice" -> drop
        var casePin = new Tab("Tell:alice", 0x002) { PinnedName = "alice" };
        E(Resolve(casePin, null, roster) == null, "case-sensitive: wrong case drops");

        // Null resolver -> drops safely
        E(Resolve(pinned, null, null) == null, "null resolver drops pinned");
        E(Resolve(unpinned, "Alice", null) == null, "null resolver drops typed");

        // NameOf: reverse lookup (portrait click-to-tell)
        E(roster.NameOf(42u) == "Alice", "NameOf resolves a live id");
        E(roster.NameOf(99u) == "Bob", "NameOf resolves another id");
        E(roster.NameOf(999u) == null, "NameOf returns null for unknown id");

        // Empty PinnedName = unpinned
        var emptyPin = new Tab("Tell", 0x002) { PinnedName = "" };
        E(Resolve(emptyPin, null, roster) == null, "empty pin = unpinned");

        // ResolveName: same precedence, no client-side presence gate (server validates)
        E(ResolveName(pinned, null, roster) == "Alice", "ResolveName pinned online");
        E(ResolveName(pinned, "Bob", roster) == "Bob", "ResolveName typed beats pin");
        E(ResolveName(offline, null, roster) == "Eve", "ResolveName offline still returns name");
        E(ResolveName(unpinned, null, roster) == null, "ResolveName unpinned no target");

        log.Info(ok ? "TellTarget.SelfCheck: passed" : "TellTarget.SelfCheck: FAILED");
    }

    /// <summary>In-memory fake for off-device tests. Case-sensitive (matching the engine's strcmp).</summary>
    private sealed class FakeRoster : IPlayerResolver
    {
        public readonly Dictionary<string, uint> Players = new();
        public uint? Resolve(string name) => Players.TryGetValue(name, out uint id) ? id : null;
        public string? NameOf(uint playerId)
        {
            foreach (var kv in Players) if (kv.Value == playerId) return kv.Key;
            return null;
        }
    }
}
