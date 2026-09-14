using CX.Framework;

namespace CX.Ui;

/// <summary>
/// The typefaces the UI may ask for by short name (#470). <c>font.&lt;name&gt;=&lt;path&gt;</c> in
/// <c>cx.cfg</c>; <c>default</c> is the embedded DejaVuSans unless <c>font.default=</c> overrides it. A
/// relative path is looked up beside the extender (the process directory: nwmain sits next to
/// <c>version.dll</c> / the <c>.so</c>, #474) and then in the user directory beside <c>cx.cfg</c> -- the
/// form that survives Proton, where an absolute Linux path names nothing. A name that fails to load is a
/// logged refusal and resolves to the default face; it never yields a missing-text frame.
/// </summary>
public static class FontRegistry
{
    public const string DefaultName = "default";
    /// <summary>The embedded DejaVuSans under its own reserved name: always loaded, never overridable from the
    /// cfg, because it is the chrome's icon source whatever <c>default</c> is bound to (#425).</summary>
    public const string EmbeddedName = "embedded";
    private const string EmbeddedResource = "DejaVuSans";
    private const string Prefix = "font.";

    /// <summary>One declared typeface: where it came from and why it was refused, if it was.</summary>
    public sealed class Entry
    {
        public string Name = "", Source = "";
        public byte[]? Bytes;
        public string? Refusal;
    }

    private static readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    /// <summary>Every declared name, default first.</summary>
    public static IEnumerable<Entry> All => _entries.Values.OrderBy(e => e.Name != DefaultName).ThenBy(e => e.Name);

    /// <summary>Read <c>font.*</c> once (cold) and load every file; the embedded default stands in when unset or refused.</summary>
    public static void Load(ExtenderLog log)
    {
        if (_loaded) return;
        _loaded = true;
        foreach ((string key, string path) in Settings.Prefixed(Prefix))
        {
            string name = key.Substring(Prefix.Length);
            if (name.Length == 0) continue;
            if (name.Equals(EmbeddedName, StringComparison.OrdinalIgnoreCase)) { log.Info($"FontRegistry: {EmbeddedName} is reserved for the embedded font; {key} ignored"); continue; }
            var e = new Entry { Name = name, Source = path };
            string? full = Resolve(path);
            if (full == null) e.Refusal = "not found";
            else
            {
                e.Source = full;
                try { e.Bytes = File.ReadAllBytes(full); } catch (Exception ex) { e.Refusal = ex.GetType().Name; }
            }
            _entries[name] = e;
            log.Info(e.Refusal == null ? $"FontRegistry: {name} = {e.Source} ({e.Bytes!.Length} bytes)"
                                       : $"FontRegistry: {name} = {e.Source} REFUSED ({e.Refusal}); falls back to {DefaultName}");
        }
        var emb = new Entry { Name = EmbeddedName, Source = "embedded " + EmbeddedResource };
        using (Stream? font = typeof(FontRegistry).Assembly.GetManifestResourceStream(EmbeddedResource))
        {
            if (font == null) emb.Refusal = "resource missing from the image";
            else { emb.Bytes = new byte[font.Length]; font.ReadExactly(emb.Bytes); }
        }
        _entries[EmbeddedName] = emb;
        if (!_entries.TryGetValue(DefaultName, out Entry? d) || d.Refusal != null)
        {
            // The default shares the embedded array: Bake tells "default IS the embedded font" by reference.
            var e = new Entry { Name = DefaultName, Source = emb.Source, Bytes = emb.Bytes, Refusal = emb.Refusal };
            if (d != null) { log.Info($"FontRegistry: {DefaultName} override refused ({d.Refusal}); using the embedded font"); e.Source = $"{e.Source} ({d.Source} REFUSED: {d.Refusal})"; }
            _entries[DefaultName] = e;
        }
    }

    /// <summary>Bytes for <paramref name="name"/>, or the default's when unknown or refused. Null only when the
    /// embedded default itself is missing from the image.</summary>
    public static byte[]? Bytes(string name)
    {
        if (_entries.TryGetValue(name, out Entry? e) && e.Bytes != null) return e.Bytes;
        return _entries.TryGetValue(DefaultName, out Entry? d) ? d.Bytes : null;
    }

    /// <summary>The name that actually backs <paramref name="name"/>: itself when loaded, else the default.</summary>
    public static string Effective(string name) =>
        _entries.TryGetValue(name, out Entry? e) && e.Bytes != null ? e.Name : DefaultName;

    private static string? Resolve(string path)
    {
        if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
        string? exe = Path.GetDirectoryName(Environment.ProcessPath);
        if (exe != null && File.Exists(Path.Combine(exe, path))) return Path.GetFullPath(Path.Combine(exe, path));
        string user = Native.ConfigPath(path);
        return File.Exists(user) ? user : null;
    }

    /// <summary>What <c>/knobs</c> prints: each name, its source, and its state.</summary>
    public static string Report(Func<string, int> facesOf)
    {
        var sb = new System.Text.StringBuilder("fonts:");
        foreach (Entry e in All)
            sb.Append($"\n  {e.Name} = {e.Source}")
              .Append(e.Refusal == null ? $" ({facesOf(e.Name)} face(s))" : $" REFUSED: {e.Refusal} -> {DefaultName}");
        return sb.ToString();
    }

    /// <summary>Off-device: unknown names fall to the default, a rooted missing path is refused.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"FontRegistry.SelfCheck FAIL: {w}"); } }
        Load(log);
        E(_entries.ContainsKey(DefaultName), "default is always declared");
        E(Effective("no-such-face") == DefaultName, "an unknown name resolves to the default");
        E(Resolve("/nonexistent/cx-selfcheck.ttf") == null, "a rooted missing path is refused");
        E(Bytes("no-such-face") == Bytes(DefaultName), "unknown-name bytes are the default's");
        E(_entries.ContainsKey(EmbeddedName), "the embedded font is always declared");
        log.Info(ok ? "FontRegistry.SelfCheck OK" : "FontRegistry.SelfCheck FAILED");
    }
}
