using System;
using System.Collections.Generic;

namespace CX.Framework;

/// <summary>
/// Hidden-tier dynamic state under one name prefix (spec §4.2, chat's <c>w&lt;N&gt;.*</c>): N windows × M tabs
/// is not a fixed declaration, so the group owns the whole <c>&lt;group&gt;.&lt;prefix&gt;*</c> key family as one
/// bag. Persisted to <c>cx.cfg</c> like a knob (every pair, verbatim), no row, no command, not in <c>/knobs</c>,
/// and never touched by the group's reset. The feature <see cref="Replace"/>s the whole set on a save and reads
/// it back through <see cref="Pairs"/>; keys inside the bag are spelled without the group prefix.
/// </summary>
public sealed class KnobBag
{
    /// <summary>The bag's name prefix inside the group (<c>w</c>): no declared knob in the group may start with it.</summary>
    public string Prefix { get; }
    /// <summary>The cfg-key prefix: <c>&lt;group&gt;.&lt;prefix&gt;</c>.</summary>
    public string KeyPrefix { get; }
    public KnobGroup Group { get; }

    private readonly Dictionary<string, string> _pairs = new(StringComparer.OrdinalIgnoreCase);

    internal KnobBag(KnobGroup group, string prefix)
    {
        Group = group; Prefix = prefix.ToLowerInvariant(); KeyPrefix = group.Name + "." + Prefix;
        foreach (var kv in Settings.Pairs)
            if (Owns(kv.Key)) _pairs[kv.Key.Substring(group.Name.Length + 1)] = kv.Value;
    }

    /// <summary>True when <paramref name="cfgKey"/> is one of this bag's: <see cref="KnobFile"/> emits it from
    /// here rather than preserving it as an unknown key.</summary>
    public bool Owns(string cfgKey) => cfgKey.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Snapshot of the bag's pairs, keys spelled inside the group (<c>w0.x</c>).</summary>
    public KeyValuePair<string, string>[] Pairs
    {
        get { lock (_pairs) { var a = new KeyValuePair<string, string>[_pairs.Count]; int i = 0; foreach (var kv in _pairs) a[i++] = kv; return a; } }
    }

    /// <summary>Replace every pair (a save): the bag is the feature's whole persisted state, so a key absent
    /// from <paramref name="pairs"/> leaves the file. Arms the writer.</summary>
    public void Replace(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        lock (_pairs)
        {
            _pairs.Clear();
            foreach (var kv in pairs) _pairs[kv.Key] = kv.Value;
        }
        if (Group.Registered) KnobFile.Arm();
    }
}
