using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CX.Framework;

/// <summary>
/// The client builds CX has been measured against, one embedded <c>Framework/Builds/*.tsv</c> each, and every
/// version-bound number CX reads: each <see cref="EngineLayout"/> row's offset per OS and each
/// <see cref="SymbolMap"/> <c>At()</c> symbol's Windows RVA. Code declares what is read; the running build's
/// file says where. The rot gate is "is the running build one of these", and a client update is a new file
/// written by <c>tools/update/</c> (docs/update-day.md), not an edit to either table.
/// </summary>
/// <remarks>
/// File format, tab-separated, <c>#</c> to end of line is a comment:
/// <code>
/// name    8193.37-17
/// linux   &lt;.note.gnu.build-id&gt;
/// windows &lt;CodeView GUID + age&gt;
/// stamp   0x68e408bc                                (PE TimeDateStamp, the RVA-path guard)
/// offset  CNWCCreature  plated  0x468  0x448       (linux, windows; "-" = never measured there)
/// rva     _ZN11CNWCMessage...  0x7a41a0            (Windows address of a SymbolMap At() symbol)
/// </code>
/// A file is complete or it is refused: <see cref="SelfCheck"/> wants one <c>offset</c> line per declared
/// row and one <c>rva</c> line per <c>At()</c> symbol, and a row with no value for the running OS reads 0
/// and stands its features down (<see cref="EngineLayout.Require"/>).
/// </remarks>
public static class ClientBuilds
{
    /// <summary>One measured client build.</summary>
    public sealed class Build
    {
        public string Name = "", LinuxKey = "", WindowsKey = "", Resource = "";
        public uint Stamp;
        /// <summary>"Class.field" -> (linux, windows); null where the file says <c>-</c>.</summary>
        public readonly Dictionary<string, (int? Linux, int? Windows)> Offsets = new();
        public readonly Dictionary<string, int> Rvas = new();
        public readonly List<string> Errors = new();

        /// <summary>The key for the OS we are running on.</summary>
        public string Key => OperatingSystem.IsWindows() ? WindowsKey : LinuxKey;
    }

    /// <summary>Resource-name prefix the csproj gives every build file.</summary>
    private const string Prefix = "builds/";

    private static List<Build>? _all;
    private static Build? _current;
    private static bool _resolved;

    /// <summary>Every embedded build file, parsed once.</summary>
    public static IReadOnlyList<Build> All => _all ??= LoadAll();

    /// <summary>The build the running client is, or null: rot.</summary>
    public static Build? Current
    {
        get
        {
            if (!_resolved)
            {
                _current = Find(EngineLayout.BuildKey);
                _resolved = true;
            }
            return _current;
        }
    }

    /// <summary>The known build whose key for this OS is <paramref name="key"/>, or null.</summary>
    public static Build? Find(string key)
    {
        foreach (Build b in All)
            if (b.Key.Length > 0 && string.Equals(b.Key, key, StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }

    /// <summary>"8193.37-17 (3e25...), ..." -- what the rot line says we would have accepted.</summary>
    public static string Known
    {
        get
        {
            var parts = new List<string>();
            foreach (Build b in All) parts.Add($"{b.Name} ({b.Key})");
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }
    }

    /// <summary>The running build's (linux, windows) values for an <see cref="EngineLayout"/> row; nulls
    /// when the file says <c>-</c>, lacks the row, or no build is running. Once per row at static init.</summary>
    public static (int? Linux, int? Windows) Offset(string cls, string field) =>
        Current is { } b && b.Offsets.TryGetValue(cls + "." + field, out var v) ? v : (null, null);

    /// <summary>The running build's RVA for a <see cref="SymbolMap"/> <c>At()</c> symbol, 0 when it has none.</summary>
    public static int Rva(string linux) => Current is { } b && b.Rvas.TryGetValue(linux, out int v) ? v : 0;

    private static List<Build> LoadAll()
    {
        var all = new List<Build>();
        var asm = typeof(ClientBuilds).Assembly;
        foreach (string res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            using Stream? s = asm.GetManifestResourceStream(res);
            if (s == null) continue;
            using var r = new StreamReader(s);
            all.Add(Parse(r.ReadToEnd(), res));
        }
        all.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
        return all;
    }

    /// <summary>One build file. Malformed lines are recorded in <see cref="Build.Errors"/>, never thrown:
    /// this runs inside <see cref="EngineLayout"/>'s type initializer.</summary>
    public static Build Parse(string text, string resource)
    {
        var b = new Build { Resource = resource };
        int n = 0;
        foreach (string raw in text.Split('\n'))
        {
            n++;
            int hash = raw.IndexOf('#');
            string line = (hash < 0 ? raw : raw[..hash]).Trim();
            if (line.Length == 0) continue;
            string[] f = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool ok = (f[0], f.Length) switch
            {
                ("name", 2) => Set(ref b.Name, f[1]),
                ("linux", 2) => Set(ref b.LinuxKey, f[1]),
                ("windows", 2) => Set(ref b.WindowsKey, f[1]),
                ("stamp", 2) => uint.TryParse(Bare(f[1]), NumberStyles.HexNumber, null, out b.Stamp),
                ("offset", 5) => Value(f[3], out int? lin) && Value(f[4], out int? win)
                                 && b.Offsets.TryAdd(f[1] + "." + f[2], (lin, win)),
                ("rva", 3) => Hex(f[2], out int rva) && rva > 0 && b.Rvas.TryAdd(f[1], rva),
                _ => false,
            };
            if (!ok) b.Errors.Add($"{resource}:{n}: bad or duplicate line '{line}'");
        }
        if (b.Name.Length == 0) b.Errors.Add($"{resource}: no name");
        if (b.LinuxKey.Length == 0 && b.WindowsKey.Length == 0) b.Errors.Add($"{resource}: no build key");
        return b;
    }

    private static bool Set(ref string slot, string v)
    {
        if (slot.Length > 0) return false;
        slot = v;
        return true;
    }

    private static string Bare(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;

    private static bool Hex(string s, out int v) => int.TryParse(Bare(s), NumberStyles.HexNumber, null, out v);

    /// <summary>An offset cell: hex, or <c>-</c> for "never measured on this OS".</summary>
    private static bool Value(string s, out int? v)
    {
        v = null;
        if (s == "-") return true;
        if (!Hex(s, out int x)) return false;
        v = x;
        return true;
    }

    /// <summary>
    /// The parser on a synthetic file, then every embedded file against the declarations: one line per
    /// row (with a Linux value), one per <c>At()</c> symbol, nothing naming what is not declared, no two
    /// builds sharing a key -- and on the running build, every value actually landed in its row. A typo or a
    /// gap in a build file is refused here rather than read as 0 somewhere in a frame.
    /// </summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        Build t = Parse("name\tt # c\nlinux\tab\nstamp\t0x10\noffset\tC\tf\t0x8\t-\noffset\tC\tg\t0x8\tzz\n" +
                        "rva\t_Zx\t0x20\nrva\t_Zx\t0x30\nbogus\n", "t");
        check.Expect(t.Name == "t" && t.LinuxKey == "ab" && t.Stamp == 0x10, "header fields parse, comments stripped");
        check.Expect(t.Offsets.TryGetValue("C.f", out var tv) && tv == (8, null), "offset parses per OS, '-' is null");
        check.Expect(!t.Offsets.ContainsKey("C.g"), "a malformed offset cell rejects the line");
        check.Expect(t.Rvas.TryGetValue("_Zx", out int tr) && tr == 0x20, "first rva wins");
        check.Expect(t.Errors.Count == 3, "bad cell, duplicate rva and unknown directive are all reported");
        check.Expect(Find("not-a-build-key") == null, "rot gate refuses a key no build file carries");

        var anchored = new HashSet<string>(SymbolMap.Anchored);
        var declared = new HashSet<string>();
        foreach ((string c, string f) in EngineLayout.Declared) declared.Add(c + "." + f);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Build b in All)
        {
            foreach (string e in b.Errors) check.Expect(false, e);
            foreach (string k in new[] { b.LinuxKey, b.WindowsKey })
                if (k.Length > 0) check.Expect(keys.Add(k), $"{b.Name}: key {k} claimed by two build files");
            foreach ((string k, var v) in b.Offsets)
            {
                check.Expect(declared.Contains(k), $"{b.Name}: offset {k} names no EngineLayout row");
                check.Expect(v.Linux != null, $"{b.Name}: offset {k} has no Linux value");
            }
            foreach (string k in declared) check.Expect(b.Offsets.ContainsKey(k), $"{b.Name}: no offset line for {k}");
            foreach (string k in b.Rvas.Keys) check.Expect(anchored.Contains(k), $"{b.Name}: rva {k} is not a SymbolMap At() symbol");
            foreach (string k in anchored) check.Expect(b.Rvas.ContainsKey(k), $"{b.Name}: no rva line for {k}");
            if (b != Current) continue;
            foreach ((string k, var v) in b.Offsets)
            {
                int dot = k.IndexOf('.');
                int? want = OperatingSystem.IsWindows() ? v.Windows : v.Linux;
                int? row = EngineLayout.RowValue(k[..dot], k[(dot + 1)..]);
                if (want != null && row != null)
                    check.Expect(row == want, $"{b.Name}: offset {k} = 0x{want:x} did not land (row reads 0x{row:x})");
            }
        }
        log.Info(check.Ok
            ? $"ClientBuilds.SelfCheck: passed -- {All.Count} build(s), running {Current?.Name ?? "UNKNOWN"}"
            : "ClientBuilds.SelfCheck: FAILED");
    }
}
