using System;
using System.IO;

namespace Sowx.Framework;

/// <summary>
/// One sowx <c>*.cfg</c> file, and the only place that knows how to load or save one (#164). Owns three
/// things every feature used to re-roll: the pre-main deferral rule, read-with-degrade + write-if-changed,
/// and the flat <c>key=value</c> line walk (<see cref="ForEachPair"/>).
/// </summary>
/// <remarks>
/// The deferral rule, encoded structurally so a new caller cannot forget it: our loader runs BEFORE the
/// engine's entry point, so <see cref="Native.UserDirectory"/> is null then and the config path would latch
/// the <c>~/.config</c> fallback. <see cref="Load"/> therefore returns false and does nothing at all until the
/// user dir resolves -- a no-op that stays retryable -- and latches exactly once when it does. Call it from a
/// site that runs repeatedly (a frame, a cursor update); never from Initialize. For the same reason the
/// resolved path is NEVER cached: only the file name is stored, and <see cref="Path"/> resolves per use.
/// </remarks>
public sealed class ConfigFile
{
    private readonly string _fileName;
    private string _savedText = "";
    private bool _loaded;

    public ConfigFile(string fileName) => _fileName = fileName;

    /// <summary>Absolute path, resolved per use (see the deferral note on the type).</summary>
    public string Path => Native.ConfigPath(_fileName);

    /// <summary>True once <see cref="Load"/> has latched.</summary>
    public bool Loaded => _loaded;

    /// <summary>True when the engine user dir is resolvable, i.e. when a config load may latch. The one
    /// expression of the pre-main rule; <see cref="Load"/> is the gate, this is for the odd caller whose
    /// file format cannot use it (HotkeyStore's sectioned cfg).</summary>
    public static bool Ready => Native.UserDirectory() != null;

    /// <summary>
    /// Load once, or do nothing and stay retryable. Returns false while the user dir is unresolved or the
    /// file has already been loaded; returns true exactly once, on the load that latched. <paramref name="applyText"/>
    /// receives the whole file text and is NOT called for a missing or unreadable file (defaults stand), so a
    /// true return means "latched", not "text was applied".
    /// </summary>
    public bool Load(ExtenderLog? log, Action<string> applyText)
    {
        if (_loaded || !Ready) return false;
        _loaded = true;
        try
        {
            string p = Path;
            if (File.Exists(p)) applyText(File.ReadAllText(p));
        }
        catch (Exception e) { log?.Info($"config: {_fileName} load failed: {e.Message}"); }
        return true;
    }

    /// <summary>Write <paramref name="text"/> iff it differs from what this file last held. Creates the
    /// directory; an IO failure is logged and swallowed (a config write must never take the client down).</summary>
    public void Save(string text, ExtenderLog? log)
    {
        if (text == _savedText) return;
        _savedText = text;   // set before the write: a failed write must not re-attempt every frame
        try
        {
            string p = Path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        catch (Exception e) { log?.Info($"config: {_fileName} save failed: {e.Message}"); }
    }

    /// <summary>Declare <paramref name="text"/> to be what is already on disk, so the next identical
    /// <see cref="Save"/> is a no-op. For a loader that wants no write until the user actually changes something.</summary>
    public void Baseline(string text) => _savedText = text;

    /// <summary>Read a cfg's text WITHOUT the deferral gate, "" when absent or unreadable. Only for a
    /// decision that must be made pre-main (ShaderStages' dump trigger); everything else uses <see cref="Load"/>.</summary>
    public static string ReadTextNow(string fileName, ExtenderLog? log)
    {
        try
        {
            string p = Native.ConfigPath(fileName);
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        catch (Exception e) { log?.Info($"config: {fileName} unreadable -- {e.Message}"); return ""; }
    }

    /// <summary>
    /// Walk a flat <c>key=value</c> file: per line, trim, split at the FIRST <c>=</c>, trim both halves, hand
    /// them to <paramref name="onPair"/>. Lines with no <c>=</c>, an empty key, or nothing but whitespace are
    /// skipped -- malformed input degrades to "fewer keys applied", never an exception.
    /// </summary>
    public static void ForEachPair(string text, Action<string, string> onPair)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq < 1) continue;   // -1 = no separator, 0 = empty key; both malformed
            onPair(line.Substring(0, eq).TrimEnd(), line.Substring(eq + 1).TrimStart());
        }
    }

    /// <summary>Off-device proof of the line walker's degrade behaviour and of the deferral gate. Runs at host
    /// start, which is pre-main by construction, so the gate's "not yet" is the expected answer there.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"ConfigFile.SelfCheck FAIL: {w}"); } }

        var seen = new System.Collections.Generic.List<(string, string)>();
        ForEachPair("a=1\nb=two\n  c  =  three  \n", (k, v) => seen.Add((k, v)));
        E(seen.Count == 3, "well-formed file yields one pair per line");
        E(seen.Count == 3 && seen[0] == ("a", "1") && seen[1] == ("b", "two"), "keys and values round-trip");
        E(seen.Count == 3 && seen[2] == ("c", "three"), "surrounding whitespace trimmed from key and value");

        seen.Clear();
        ForEachPair("nosep\n=leadingeq\n\n   \nk=v\r\nempty=\nx=a=b\n", (k, v) => seen.Add((k, v)));
        E(seen.Count == 3, "malformed lines skipped, not thrown on");
        E(seen.Count == 3 && seen[0] == ("k", "v"), "CR stripped alongside the newline");
        E(seen.Count == 3 && seen[1] == ("empty", ""), "empty value is a legal pair");
        E(seen.Count == 3 && seen[2] == ("x", "a=b"), "split at the FIRST separator only");

        ForEachPair("", (_, _) => E(false, "empty text yields no pairs"));

        // The whole point of the module: pre-main, a load is a no-op that stays retryable.
        var f = new ConfigFile("sowx-selfcheck-never-written.cfg");
        bool applied = false;
        bool latched = f.Load(log, _ => applied = true);
        E(Ready || (!latched && !f.Loaded && !applied), "pre-main load is a no-op and stays retryable");

        log.Info(ok ? "ConfigFile.SelfCheck: passed" : "ConfigFile.SelfCheck: FAILED");
    }
}
