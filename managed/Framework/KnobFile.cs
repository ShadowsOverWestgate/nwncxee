using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace CX.Framework;

/// <summary>
/// The one <c>cx.cfg</c> writer (spec §2). Every set arms a trailing debounce; when it fires, a pool thread
/// walks the registry, writes <c>cx.cfg.tmp</c> beside <see cref="Settings.Path"/> and renames it over the
/// file. Content: the fixed header, then off-default declared keys ∪ every <see cref="KnobBag"/> pair ∪ keys no
/// declaration claims (verbatim), one alphabetical list. Last writer wins; no write-if-changed compare. The render thread never comes
/// here and the input thread only arms the timer.
/// </summary>
public static class KnobFile
{
    /// <summary>Trailing debounce: a slider drag is one write, after it stops.</summary>
    public const int DebounceMs = 1000;
    /// <summary>The header. No <c>=</c> in it: <see cref="ConfigFile.ForEachPair"/> would read one as a key.</summary>
    public const string Header = "# cx.cfg -- written by CX; comments are not preserved; edit with the client closed";

    private static readonly Timer _timer = new(_ => Write(), null, Timeout.Infinite, Timeout.Infinite);
    private static int _writes;

    /// <summary>Writes so far; the "count it" for a slider drag.</summary>
    public static int Writes => _writes;

    /// <summary>Restart the debounce. Cold: called once per set on the setter's thread.</summary>
    public static void Arm() => _timer.Change(DebounceMs, Timeout.Infinite);

    /// <summary>The file's text from the live registry and the loaded unknown keys.</summary>
    public static string Compose() => Compose(out _);

    private static string Compose(out int keys)
    {
        var lines = new List<string>();
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bags = new List<KnobBag>();
        foreach (KnobGroup g in KnobGroup.All)
        {
            foreach (Knob k in g.Knobs)
            {
                if (!k.Persisted) continue;
                declared.Add(k.Key);
                if (!k.IsDefault) lines.Add(k.Key + "=" + k.Format());
            }
            foreach (KnobBag b in g.Bags)
            {
                bags.Add(b);
                foreach (var kv in b.Pairs) lines.Add(g.Name + "." + kv.Key + "=" + kv.Value);
            }
        }
        foreach (var kv in Settings.Pairs)
            if (!declared.Contains(kv.Key) && !kv.Key.StartsWith('#') && !Bagged(bags, kv.Key)) lines.Add(kv.Key + "=" + kv.Value);
        lines.Sort(string.CompareOrdinal);
        keys = lines.Count;
        var sb = new StringBuilder(Header).Append('\n');
        foreach (string l in lines) sb.Append(l).Append('\n');
        return sb.ToString();
    }

    private static bool Bagged(List<KnobBag> bags, string key)
    {
        for (int i = 0; i < bags.Count; i++) if (bags[i].Owns(key)) return true;
        return false;
    }

    /// <summary>The timer's callback, on a pool thread: tmp + rename. An IO failure is logged and swallowed;
    /// a config write must never take the client down.</summary>
    private static void Write()
    {
        string path = Settings.Path;
        if (path.Length == 0) { KnobGroup.Log?.Info("settings: no file path latched -- write skipped"); return; }
        try
        {
            string text = Compose(out int keys);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
            int n = Interlocked.Increment(ref _writes);
            KnobGroup.Log?.Info($"settings: wrote {path} (write #{n}, {keys} key(s))");
        }
        catch (Exception e) { KnobGroup.Log?.Info($"settings: write of {path} failed: {e.Message}"); }
    }

    /// <summary>Off-device: the composed text carries the header, no declared-at-default key, and every
    /// unknown loaded key verbatim. Never writes: the demo's file is the real user's.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"KnobFile.SelfCheck FAIL: {w}"); } }

        string text = Compose();
        E(text.StartsWith(Header + "\n"), "the header comes first");
        E(!Header.Contains('='), "the header reads back as no key");
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bags = new List<KnobBag>();
        foreach (KnobGroup g in KnobGroup.All)
        {
            foreach (Knob k in g.Knobs)
            {
                if (k.Persisted) declared.Add(k.Key);
                E(k.IsDefault == !text.Contains("\n" + k.Key + "=") || !k.Persisted, $"{k.Key}: present iff off-default");
            }
            foreach (KnobBag b in g.Bags)
            {
                bags.Add(b);
                foreach (var kv in b.Pairs)
                    E(text.Contains("\n" + g.Name + "." + kv.Key + "=" + kv.Value + "\n"), $"bag pair {kv.Key} written verbatim");
            }
        }
        foreach (var kv in Settings.Pairs)
            if (!declared.Contains(kv.Key) && !kv.Key.StartsWith('#') && !Bagged(bags, kv.Key))
                E(text.Contains("\n" + kv.Key + "=" + kv.Value + "\n"), $"unknown key {kv.Key} preserved verbatim");
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 2; i < lines.Length; i++)
            E(string.CompareOrdinal(lines[i - 1], lines[i]) <= 0, "keys are alphabetical");
        E(Writes == 0, "nothing wrote during boot");

        log.Info(ok ? "KnobFile.SelfCheck: passed" : "KnobFile.SelfCheck: FAILED");
    }
}
