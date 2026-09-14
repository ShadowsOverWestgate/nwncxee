using System;
using System.Collections.Generic;
using System.Globalization;

namespace CX.Framework;

/// <summary>
/// Bucket 2 of ADR 0013: the boot config. One flat <c>key=value</c> file holding every knob that must be
/// decided before the first frame or that describes the machine rather than the look -- capability gates,
/// buffer sizes, arming decisions, headless dumps. Look knobs are bucket 1 and live on
/// <see cref="CommandRegistry"/> instead; neither bucket is an environment variable.
/// </summary>
/// <remarks>
/// <para><b>Path, and why it is latched.</b> <see cref="Native.ConfigPath"/> resolves to the engine user
/// directory once the engine exists and to <see cref="Native.DefaultUserDirectory"/> before it does. Boot
/// settings are read at both moments -- <c>gl46</c> at Initialize, <c>bindless</c> at the first frame -- so a
/// lazy latch would pick its file by whichever read fired first, and a reorder would silently move the file.
/// <see cref="Load"/> is therefore called once from the host, pre-main by construction, which pins the file
/// next to <c>nwn.ini</c> and the per-feature cfgs in the ordinary case. That is the same moment (and the
/// same fallback) <c>ShaderStages.DumpDir</c> already resolves at.</para>
/// <para><b>Enumeration.</b> Every read records its key, default and effective value, so the config surface
/// can print itself. Complete by the first frame because every read is a boot read.</para>
/// </remarks>
public static class Settings
{
    /// <summary>The boot config file. Feature preference files are <c>cx-&lt;feature&gt;.cfg</c> and are
    /// written by their features; this one is hand-edited and never written back.</summary>
    public const string FileName = "cx.cfg";

    /// <summary>One setting as it was read: what was asked for, what the code would have used, what it used.</summary>
    public readonly record struct Known(string Key, string Default, string Value);

    private static readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private static string _path = "";
    private static readonly List<Known> _read = new();

    /// <summary>The file <see cref="Load"/> actually read, resolved at that one pre-main moment. Reported
    /// rather than re-resolved: <see cref="Native.ConfigPath"/> answers differently once the engine exists,
    /// so asking it again at command time would name a file nothing reads.</summary>
    public static string Path => _path;

    /// <summary>Every setting read so far, in read order. The listable half of bucket 2.</summary>
    public static IReadOnlyList<Known> All => _read;

    /// <summary>True once <see cref="Load"/> has run. A read before that gets defaults, which is a wiring
    /// bug rather than a user error -- hence the log line.</summary>
    public static bool Loaded { get; private set; }

    /// <summary>Read the file. Once, from the host, before any service is constructed. Missing or unreadable
    /// file means "every default stands"; a malformed line is skipped, per <see cref="ConfigFile.ForEachPair"/>.</summary>
    public static void Load(ExtenderLog? log)
    {
        if (Loaded) return;
        Loaded = true;
        // ReadTextNow, not ConfigFile.Load: the deferral gate exists for files read from a live frame, and
        // waiting for the engine user dir here would mean the capability gates never see their own settings.
        ConfigFile.ForEachPair(ConfigFile.ReadTextNow(FileName, log, out _path), (k, v) => _values[k] = v);
        if (_values.Count > 0) log?.Info($"settings: {_values.Count} key(s) from {_path}");
        // Off-device (run-demo: no engine symbols resolve) the log stays open -- it is the self-check gate.
        bool logs = Bool("logs", false) || Native.Sym(SymbolMap.AppManager) == IntPtr.Zero;
        if (!logs) log?.Info($"settings: logs=0 -- silent from here; set logs=1 in {_path} for diagnostics");
        ExtenderLog.Enabled = logs;
    }

    /// <summary>True for <c>1/true/yes/on</c>, false for <c>0/false/no/off</c>, else <paramref name="fallback"/>.
    /// Case-folded, like the keys: a hand-edited file that says <c>True</c> must not fall silently back to the
    /// default.</summary>
    public static bool Bool(string key, bool fallback)
    {
        string? v = Raw(key)?.ToLowerInvariant();
        bool r = v switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
        return Record(key, fallback ? "1" : "0", r ? "1" : "0", r);
    }

    /// <summary>Integer setting, accepted only inside [<paramref name="min"/>, <paramref name="max"/>].
    /// Out-of-range and unparseable both degrade to <paramref name="fallback"/> -- a typo must not size a
    /// buffer.</summary>
    public static int Int(string key, int fallback, int min = int.MinValue, int max = int.MaxValue)
    {
        int r = int.TryParse(Raw(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                && v >= min && v <= max ? v : fallback;
        return Record(key, Str(fallback), Str(r), r);
    }

    /// <summary>Invariant-culture float setting. Invariant parsing is not cosmetic: a dot-decimal value read
    /// under a comma-decimal desktop locale was silently ignored.</summary>
    public static float Float(string key, float fallback, float min = float.MinValue, float max = float.MaxValue)
    {
        float r = float.TryParse(Raw(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                  && v >= min && v <= max ? v : fallback;
        return Record(key, Str(fallback), Str(r), r);
    }

    /// <summary>The setting case-folded, or <paramref name="fallback"/> when unset or empty. Folded because
    /// every caller compares it against a lowercase constant, and a capitalised mode name that matches nothing
    /// stands its whole subsystem down with only "standing down" in the log.</summary>
    public static string Text(string key, string fallback)
    {
        string? v = Raw(key)?.ToLowerInvariant();
        return Record(key, fallback, string.IsNullOrEmpty(v) ? fallback : v!,
                      string.IsNullOrEmpty(v) ? fallback : v!);
    }

    private static string? Raw(string key) => _values.TryGetValue(key, out string? v) ? v : null;

    /// <summary>Every set key starting with <paramref name="prefix"/> (case-folded), with its value RAW --
    /// not lowercased, because the values are paths. Each is recorded for <c>/knobs</c> like any read.</summary>
    public static List<(string Key, string Value)> Prefixed(string prefix)
    {
        var r = new List<(string, string)>();
        foreach (var kv in _values)
            if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && kv.Value.Length > 0)
                r.Add((Record(kv.Key.ToLowerInvariant(), "", kv.Value, kv.Key.ToLowerInvariant()), kv.Value));
        r.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return r;
    }

    /// <summary>Note the read for <c>/knobs</c>. Replaces rather than appends when a key is read twice --
    /// two subsystems legitimately read the same gate (<c>shadows</c> does), and the surface should say what
    /// is set, not how many places looked.</summary>
    private static T Record<T>(string key, string def, string val, T result)
    {
        var k = new Known(key, def, val);
        for (int i = 0; i < _read.Count; i++)
            if (string.Equals(_read[i].Key, key, StringComparison.OrdinalIgnoreCase)) { _read[i] = k; return result; }
        _read.Add(k);
        return result;
    }

    private static string Str(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Str(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Parse degrade, range rejection and enumeration, off-device. Runs against the live table, so
    /// it uses keys no real setting claims.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"Settings.SelfCheck FAIL: {w}"); } }

        int before = _read.Count;
        E(Bool("cx-selfcheck-absent", true), "an absent bool falls back");
        E(Int("cx-selfcheck-absent", 7) == 7, "an absent int falls back");
        E(Float("cx-selfcheck-absent", 1.5f) == 1.5f, "an absent float falls back");
        E(Text("cx-selfcheck-absent", "d") == "d", "an absent string falls back");
        E(_read.Count == before + 1, "every read is enumerable, present or not (one key, four reads)");

        _values["cx-selfcheck-probe"] = "on";
        E(Bool("cx-selfcheck-probe", false), "\"on\" reads as true");
        _values["cx-selfcheck-probe"] = "off";
        E(!Bool("cx-selfcheck-probe", true), "\"off\" reads as false");
        _values["cx-selfcheck-probe"] = "maybe";
        E(Bool("cx-selfcheck-probe", true), "an unrecognised bool falls back rather than reading false");

        _values["cx-selfcheck-probe"] = "9";
        E(Int("cx-selfcheck-probe", 2, 0, 4) == 2, "an out-of-range int is refused, not clamped");
        E(Int("cx-selfcheck-probe", 2, 0, 16) == 9, "an in-range int is taken");
        _values["cx-selfcheck-probe"] = "0,5";
        E(Float("cx-selfcheck-probe", 1f) == 1f, "a comma-decimal float is refused under any locale");
        _values["cx-selfcheck-probe"] = "0.5";
        E(Float("cx-selfcheck-probe", 1f) == 0.5f, "a dot-decimal float is taken under any locale");
        _values["cx-selfcheck-probe"] = "-1";
        E(Float("cx-selfcheck-probe", 1f, 0f) == 1f, "a negative below the floor is refused");

        E(_read[^1].Key == "cx-selfcheck-probe" && _read[^1].Default == "1" && _read[^1].Value == "1",
            "enumeration carries the key, the default and the effective value");
        E(_read.Count == before + 2, "a key read repeatedly is enumerated once, not once per reader");

        _values["cx-selfcheck-pfx.Mixed"] = "/Case/Kept.ttf";
        var pf = Prefixed("cx-selfcheck-pfx.");
        E(pf.Count == 1 && pf[0].Key == "cx-selfcheck-pfx.mixed" && pf[0].Value == "/Case/Kept.ttf",
            "a prefixed read keeps the value's case and folds the key");
        _values.Remove("cx-selfcheck-pfx.Mixed");
        _values.Remove("cx-selfcheck-probe");
        // The check reads against the LIVE table, so its probes have to leave it -- /knobs is the surface
        // whose whole claim is that everything it lists is real.
        _read.RemoveRange(before, _read.Count - before);
        E(_read.Count == before, "the check leaves no probe behind in the enumeration");

        log.Info(ok ? "Settings.SelfCheck: passed" : "Settings.SelfCheck: FAILED");
    }
}
