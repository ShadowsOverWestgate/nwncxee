using System;
using System.Collections.Generic;
using System.Text;

namespace CX.Framework;

/// <summary>
/// One feature's knobs (ADR 0025): feature = group = submenu = chat command = reset unit = key prefix.
/// <see cref="Declare"/> registers the group for enumeration and registers its generated
/// <c>/&lt;group&gt;</c> chat handler; the factories declare knobs in row order, binding each from the
/// pre-main <see cref="Settings"/> dictionary as it is constructed, so a static field is right on first
/// touch. A group that stands down this session still declares, so its rows exist.
/// </summary>
public sealed class KnobGroup
{
    /// <summary>The key prefix, submenu id and chat command: <c>grass</c>.</summary>
    public string Name { get; }
    public string Category { get; }
    public string Submenu { get; }
    /// <summary>Submenu header text; a group with no master says why here.</summary>
    public string Header { get; }
    /// <summary><c>&lt;group&gt;.enabled</c>, the first row, when the group has a master.</summary>
    public Knob<bool>? Master { get; }
    /// <summary>Another group's master key (<c>alloy.enabled</c>), resolved lazily by <see cref="Parent"/>:
    /// the parent group may declare later than this one.</summary>
    public string? ParentKey { get; }
    /// <summary>The parent's master knob, or null while its group has not declared.</summary>
    public Knob<bool>? Parent => ParentKey == null ? null : Find(ParentKey) as Knob<bool>;
    /// <summary>Every knob in declaration order, master first.</summary>
    public IReadOnlyList<Knob> Knobs => _knobs;
    /// <summary>The group's Hidden dynamic-key bags (<see cref="Bag"/>), persisted beside its knobs.</summary>
    public IReadOnlyList<KnobBag> Bags => _bags;
    /// <summary>The group's status line (Renderer's "Running: …"); bare <c>/&lt;group&gt;</c> prints it
    /// instead of every row's value when set.</summary>
    public Func<string>? Status;
    /// <summary>False for the SelfCheck's throwaway group: not enumerated, no command, sets arm no write.</summary>
    internal bool Registered { get; }

    private readonly List<Knob> _knobs = new();
    private readonly List<KnobBag> _bags = new();
    private string _section = "";

    private static readonly List<KnobGroup> _all = new();
    private static readonly Dictionary<string, Knob> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _violations = new();
    /// <summary>Refusal log. Set by the host right after <see cref="Settings.Load"/>.</summary>
    public static ExtenderLog? Log;

    /// <summary>Every declared group in declaration order.</summary>
    public static IReadOnlyList<KnobGroup> All { get { lock (_all) return _all.ToArray(); } }

    /// <summary>The knob declared under <paramref name="key"/>, or null.</summary>
    public static Knob? Find(string key) { lock (_all) return _byKey.TryGetValue(key, out Knob? k) ? k : null; }

    /// <summary>Declare a group. <paramref name="master"/> is the label of <c>&lt;group&gt;.enabled</c>, the
    /// first row, User tier (so <paramref name="masterHelp"/> is required); <paramref name="parent"/> names
    /// another group's master by KEY (<c>"alloy.enabled"</c>) and may be declared later.</summary>
    public static KnobGroup Declare(string name, string category, string submenu, string? master = null,
                                    string? masterHelp = null, bool masterDefault = true,
                                    Effect masterEffect = Effect.Live, Action? masterChanged = null,
                                    string? parent = null, string header = "")
        => new(name, category, submenu, master, masterHelp, masterDefault, masterEffect, masterChanged, parent, header, register: true);

    internal KnobGroup(string name, string category, string submenu, string? master, string? masterHelp,
                       bool masterDefault, Effect masterEffect, Action? masterChanged, string? parent,
                       string header, bool register)
    {
        Name = name.ToLowerInvariant(); Category = category; Submenu = submenu; ParentKey = parent; Header = header;
        Registered = register;
        if (register)
            lock (_all) _all.Add(this);
        if (master != null)
            Master = Toggle("enabled", master, masterDefault, Tier.User, masterEffect, masterHelp, masterChanged);
        if (register)
            CommandRegistry.Global.Register("/" + Name,
                $"report or set the {Submenu} knobs; /{Name} <knob> <value>, /{Name} reset", Command);
    }

    /// <summary>Knobs declared after this carry <paramref name="name"/> as their section header.</summary>
    public void Section(string name) => _section = name;

    public Knob<bool> Toggle(string key, string label, bool def, Tier tier = Tier.User, Effect effect = Effect.Live,
                             string? help = null, Action? changed = null)
        => Add(new Knob<bool>(this, key, label, KnobKind.Toggle, tier, effect, _section, help ?? "", false,
                              def, false, true, true, "", null, null, null, null), changed);

    public Knob<float> Slider(string key, string label, float min, float max, float step, float def, string unit = "",
                              Tier tier = Tier.User, Effect effect = Effect.Live, string? help = null, Action? changed = null)
        => Add(new Knob<float>(this, key, label, KnobKind.Slider, tier, effect, _section, help ?? "", false,
                               def, min, max, step, unit, null, null, null, null), changed);

    public Knob<int> Slider(string key, string label, int min, int max, int step, int def, string unit = "",
                            Tier tier = Tier.User, Effect effect = Effect.Live, string? help = null, Action? changed = null)
        => Add(new Knob<int>(this, key, label, KnobKind.Slider, tier, effect, _section, help ?? "", false,
                             def, min, max, step, unit, null, null, null, null), changed);

    /// <summary>The one factory with <paramref name="layout"/>: a layout-affecting knob is a dropdown by
    /// construction. <paramref name="choices"/> are the labels shown, index-aligned with
    /// <paramref name="values"/>; null shows each value's own text.</summary>
    public Knob<T> Dropdown<T>(string key, string label, T[] values, T def, string[]? choices = null, bool layout = false,
                               Tier tier = Tier.User, Effect effect = Effect.Live, string? help = null, Action? changed = null,
                               Func<T, string>? format = null, Func<string, (bool, T)>? parse = null)
        => Add(new Knob<T>(this, key, label, KnobKind.Dropdown, tier, effect, _section, help ?? "", layout,
                           def, def, def, def, "", choices, values, format, parse), changed);

    public Knob<uint> Swatch(string key, string label, uint def, Tier tier = Tier.User, Effect effect = Effect.Live,
                             string? help = null, Action? changed = null)
        => Add(new Knob<uint>(this, key, label, KnobKind.Swatch, tier, effect, _section, help ?? "", false,
                              def, 0, uint.MaxValue, 1, "", null, null, null, null), changed);

    public Knob<string> Text(string key, string label, string def, Tier tier = Tier.User, Effect effect = Effect.Live,
                             string? help = null, Action? changed = null)
        => Add(new Knob<string>(this, key, label, KnobKind.Text, tier, effect, _section, help ?? "", false,
                                def, "", "", "", "", null, null, null, null), changed);

    /// <summary><paramref name="confirm"/>: the panel fires it on a second press within
    /// <see cref="KnobButton.ConfirmSec"/> only (spec §4.5's revoke rows).</summary>
    public KnobButton Button(string key, string label, Action press, Tier tier = Tier.User, string? help = null, bool confirm = false)
        => Add(new KnobButton(this, key, label, tier, _section, help ?? "", press, confirm), null);

    /// <summary>A link row (spec §3): <paramref name="url"/> under <paramref name="label"/>, copied to the clipboard
    /// on press. Knob-less by nature -- no value, no key in the file -- but a row in declaration order.</summary>
    public KnobLink Link(string key, string label, string url, Tier tier = Tier.User)
        => Add(new KnobLink(this, key, label, url, tier, _section), null);

    /// <summary>A knob of any other <typeparamref name="T"/> with its own format/parse pair; rendered by
    /// <paramref name="kind"/>.</summary>
    public Knob<T> Custom<T>(string key, string label, KnobKind kind, T def, Func<T, string> format,
                             Func<string, (bool, T)> parse, Tier tier = Tier.Advanced, Effect effect = Effect.Live,
                             string? help = null, Action? changed = null)
        => Add(new Knob<T>(this, key, label, kind, tier, effect, _section, help ?? "", false,
                           def, def, def, def, "", null, null, format, parse), changed);

    /// <summary>Hidden dynamic state under <paramref name="prefix"/> (spec §4.2 <c>w&lt;N&gt;.*</c>): one
    /// <see cref="KnobBag"/> owning every <c>&lt;group&gt;.&lt;prefix&gt;*</c> key. Outside the rows, the
    /// command, <c>/knobs</c> and <see cref="Reset"/>; the prefix must not head any knob name in the group.</summary>
    public KnobBag Bag(string prefix)
    {
        var b = new KnobBag(this, prefix);
        for (int i = 0; i < _knobs.Count; i++)
            if (_knobs[i].Name.StartsWith(b.Prefix, StringComparison.Ordinal)) Violation($"{_knobs[i].Key} collides with bag prefix {b.KeyPrefix}");
        _bags.Add(b);
        return b;
    }

    private K Add<K>(K k, Action? changed) where K : Knob
    {
        k.Changed = changed ?? k.Changed;
        if (k.Tier == Tier.User && k.Help.Length == 0 && k.Kind != KnobKind.Link)
        {
            Violation($"{k.Key} is User tier without help -- demoted to Advanced");
            k.Tier = Tier.Advanced;
        }
        if (k.Layout && k.Kind != KnobKind.Dropdown)
            Violation($"{k.Key} is layout-affecting but not a Dropdown");
        for (int i = 0; i < _bags.Count; i++)
            if (k.Name.StartsWith(_bags[i].Prefix, StringComparison.Ordinal)) Violation($"{k.Key} collides with bag prefix {_bags[i].KeyPrefix}");
        _knobs.Add(k);
        if (Registered)
            lock (_all)
            {
                if (_byKey.ContainsKey(k.Key)) Violation($"{k.Key} declared twice");
                _byKey[k.Key] = k;
            }
        return k;
    }

    private void Violation(string what)
    {
        if (!Registered) return;
        Log?.Info("KnobGroup: " + what);
        lock (_all) _violations.Add(what);
    }

    /// <summary>The knob named <paramref name="name"/> in this group, or null.</summary>
    public Knob? Get(string name)
    {
        for (int i = 0; i < _knobs.Count; i++)
            if (string.Equals(_knobs[i].Name, name, StringComparison.OrdinalIgnoreCase)) return _knobs[i];
        return null;
    }

    /// <summary>Every knob back to its default (a set each): the group's keys leave the file. Hidden state
    /// (a Hidden knob, a <see cref="KnobBag"/>) is not a row and stays as it is.</summary>
    public void Reset()
    {
        for (int i = 0; i < _knobs.Count; i++)
            if (_knobs[i].Tier != Tier.Hidden) _knobs[i].Reset();
    }

    /// <summary>The panel's Reset to defaults (spec §3): the User and Advanced rows only; a ChatOnly key keeps
    /// its value as Hidden state does.</summary>
    public void ResetRows()
    {
        for (int i = 0; i < _knobs.Count; i++)
            if (_knobs[i].Tier == Tier.User || _knobs[i].Tier == Tier.Advanced) _knobs[i].Reset();
    }

    /// <summary>Every row's value on one line, for the bare command when there is no status line.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _knobs.Count; i++)
        {
            if (_knobs[i].Kind == KnobKind.Button || _knobs[i].Kind == KnobKind.Link || _knobs[i].Tier == Tier.Hidden) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(_knobs[i].Name).Append(' ').Append(_knobs[i].Format());
        }
        return sb.ToString();
    }

    /// <summary>The generated handler: <c>/g</c> reports, <c>/g &lt;knob&gt; &lt;value&gt;</c> sets,
    /// <c>/g &lt;button&gt;</c> presses, <c>/g reset</c> restores every default. Arguments arrive lowercased
    /// from <see cref="CommandRegistry"/>, so a Text knob's case is lost on the chat path (the panel keeps it).
    /// A zero-knob group (<c>about</c>, <c>knobs</c>) has nothing to set or reset: every form is the bare one.</summary>
    private string? Command(string args)
    {
        string[] a = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (a.Length == 0 || Settable() == 0) return $"[{Name}] {Bare()}";
        if (a[0] == "reset" && a.Length == 1)
        {
            Reset();
            return $"[{Name}] reset -> {Bare()}";
        }
        Knob? k = Get(a[0]);
        if (k == null || k.Tier == Tier.Hidden) return $"[{Name}] unknown knob \"{a[0]}\" -- try {Names()}";
        if (k is KnobButton b)
        {
            b.Press();
            return $"[{Name}] {k.Name}";
        }
        if (k is KnobLink l) return $"[{Name}] {l.Label}: {l.Url}";
        if (a.Length < 2) return $"[{Name}] {k.Name} {k.Format()} (default {k.FormatDefault()}) -- /{Name} {k.Name} <{k.RangeText()}>";
        string was = k.Format();
        if (!k.Parse(a[1])) return $"[{Name}] {k.Name}: \"{a[1]}\" is not {k.RangeText()}";
        return $"[{Name}] {k.Name} {k.Format()} (was {was})" + (k.Effect == Effect.Restart ? " -- takes effect at the next launch" : "");
    }

    /// <summary>The bare report: the status line (else every row's value), then each link row's address on its
    /// own line, so <c>/about</c> prints the same four lines the About submenu shows.</summary>
    private string Bare()
    {
        var sb = new StringBuilder(Status?.Invoke() ?? Report());
        for (int i = 0; i < _knobs.Count; i++)
            if (_knobs[i] is KnobLink l && l.Tier != Tier.Hidden) sb.Append('\n').Append(l.Label).Append(": ").Append(l.Url);
        return sb.ToString();
    }

    /// <summary>Knobs the command can set or press: a group of links only answers every form with the bare one.</summary>
    private int Settable()
    {
        int n = 0;
        for (int i = 0; i < _knobs.Count; i++) if (_knobs[i].Kind != KnobKind.Link) n++;
        return n;
    }

    private string Names()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _knobs.Count; i++)
            if (_knobs[i].Tier != Tier.Hidden) sb.Append(sb.Length == 0 ? "" : ", ").Append(_knobs[i].Name);
        return sb.ToString();
    }

    /// <summary>The <c>/knobs</c> walk: groups in declaration order, one <see cref="Knob.Describe"/> line each.
    /// Hidden state has no surface, so it is not here either.</summary>
    public static void Walk(StringBuilder sb)
    {
        var all = All;
        int n = 0;
        foreach (KnobGroup g in all)
            for (int i = 0; i < g._knobs.Count; i++) if (g._knobs[i].Tier != Tier.Hidden) n++;
        sb.Append($"[knobs] {n} knob(s) in {all.Count} group(s) (/<group> <knob> <value>, /<group> reset):");
        foreach (KnobGroup g in all)
            for (int i = 0; i < g._knobs.Count; i++)
                if (g._knobs[i].Tier != Tier.Hidden) sb.Append("\n  ").Append(g._knobs[i].Describe());
    }

    /// <summary>Off-device: the three constraints on a throwaway group (each must be caught), the
    /// bind/set/reset/format contract, then a walk of every LIVE declaration -- a real knob that broke one
    /// fails the suite, which is what makes <c>run-demo.sh</c> the gate.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"KnobGroup.SelfCheck FAIL: {w}"); } }

        var g = new KnobGroup("cx-selfcheck", "Test", "Test", "Enable", "help", true, Effect.Live, null, "nosuch.enabled", "", register: false);
        E(g.Master != null && g.Master.Key == "cx-selfcheck.enabled" && g.Master.V, "master declares <group>.enabled, first row, on");
        E(g.Parent == null, "an undeclared parent resolves to null rather than throwing");
        E(g.Knobs.Count == 1 && g.Knobs[0] == g.Master, "the master is the first row");

        var nohelp = g.Toggle("nohelp", "No help", false);
        E(nohelp.Tier == Tier.Advanced, "a User-tier knob without help is refused: demoted to Advanced");
        var withHelp = g.Toggle("help", "Help", false, help: "h");
        E(withHelp.Tier == Tier.User, "a User-tier knob with help keeps its tier");

        var bad = new Knob<int>(g, "badlayout", "Bad", KnobKind.Slider, Tier.Advanced, Effect.Live, "", "", true,
                                1, 0, 4, 1, "", null, null, null, null);
        E(bad.Layout && bad.Kind != KnobKind.Dropdown, "a layout knob that is not a Dropdown is only reachable past the factories");
        E(!Constraints(bad), "...and the constraint walk catches it");

        int changed = 0;
        var dens = g.Slider("density", "Density", 0f, 4f, 0.25f, 2f, help: "h", changed: () => changed++);
        E(dens.V == 2f && dens.Boot == 2f && dens.Default == 2f, "an absent key binds V and Boot to the default");
        E(dens.Parse("3") && dens.V == 3f && changed == 1, "a chat set parses, assigns and runs Changed");
        E(dens.Boot == 2f && dens.BootMoved && !dens.IsDefault, "a set moves V, not Boot");
        E(dens.Parse("9") && dens.V == 4f, "a slider set clamps to the range");
        E(!dens.Parse("abc") && dens.V == 4f, "unparseable text is refused and changes nothing");
        E(!dens.Parse("0,5"), "a comma-decimal float is refused under any locale");
        E(dens.Format() == "4" && dens.FormatDefault() == "2", "invariant float format");
        dens.Reset();
        E(dens.IsDefault && changed == 3, "reset is a set: back to default, Changed ran");

        var restart = g.Slider("cubes", "Cubes", 0, 64, 4, 32, effect: Effect.Restart, tier: Tier.Advanced);
        restart.Set(16);
        E(restart.V == 16 && restart.Boot == 32 && restart.BootIntact, "a Restart knob's Boot survives a set");
        E(restart.Describe() == "cx-selfcheck.cubes=16 (default 32) [advanced] [restart] (boot 32)", $"the /knobs line: {restart.Describe()}");
        restart.Boot = 16;
        E(!Constraints(restart), "a Restart knob whose Boot moved fails the constraint walk");
        restart.Boot = 32;

        var dd = g.Dropdown("shadow", "Shadow", new[] { 0, 1, 2, 4 }, 1, new[] { "none", "1", "2", "4" }, tier: Tier.Advanced);
        E(dd.Parse("4") && dd.V == 4, "a dropdown takes a stored value");
        E(dd.Parse("none") && dd.V == 0, "...or a label");
        E(dd.Parse("3") && dd.V == 3, "...or, on a numeric ladder, any value inside its span (chat is not held to the steps)");
        E(!dd.Parse("7") && dd.V == 3, "a value past the ladder's span is refused");
        E(dd.Format() == "3" && dd.RangeText() == "none, 1, 2, 4", "dropdown formats the stored value");
        var tm = g.Dropdown("tm", "Tm", new[] { "off", "shoulder", "aces" }, "aces", tier: Tier.Advanced);
        E(tm.Parse("1") && tm.V == "shoulder", "a non-numeric dropdown takes an index when nothing else matches");
        E(!tm.Parse("7") && tm.V == "shoulder", "an out-of-range index is refused");
        var tg = g.Toggle("tg", "Tg", true, tier: Tier.ChatOnly);
        E(tg.Parse("off") && !tg.V && tg.Parse("1") && tg.V && !tg.Parse("maybe"), "toggle accepts on/off/0/1 only");
        E(tg.Format() == "1", "toggle formats 0/1");
        var sw = g.Swatch("col", "Col", 0x11223344u, tier: Tier.Hidden);
        E(sw.Format() == "11223344" && sw.Parse("ffeeddcc") && sw.V == 0xffeeddccu && !sw.Parse("fff"), "swatch is rrggbbaa");
        E(g.Command("col ffffffff")!.StartsWith("[cx-selfcheck] unknown knob") && sw.V == 0xffeeddccu, "a Hidden knob has no command");
        var bag = g.Bag("w");
        bag.Replace(new[] { new KeyValuePair<string, string>("w0.x", "60"), new KeyValuePair<string, string>("w0.t0.label", "All") });
        E(bag.Pairs.Length == 2 && bag.Owns("cx-selfcheck.w0.x") && !bag.Owns("cx-selfcheck.col"), "a bag holds its pairs and owns its key family");
        E(g.Bags.Count == 1 && g.Knobs.Count == 9, "a bag is not a knob");
        int pressed = 0;
        var btn = g.Button("dump", "Dump", () => pressed++, Tier.ChatOnly);
        E(!btn.Persisted && btn.IsDefault && !btn.Parse("1"), "a button has no value and is never persisted");
        btn.Press();
        E(pressed == 1 && btn.Describe() == "cx-selfcheck.dump [button] [chatonly]", "a press runs its action");

        var lnk = g.Link("site", "Releases", "https://example.invalid/r");
        E(lnk.Tier == Tier.User && !lnk.Persisted && lnk.IsDefault && lnk.Format() == "https://example.invalid/r" && Constraints(lnk), "a link is a User row without help, never persisted");
        E(g.Command("site") == "[cx-selfcheck] Releases: https://example.invalid/r" && !g.Report().Contains("site"), "a link prints its address from chat and is on no value report");
        E(g.Command("") == $"[cx-selfcheck] {g.Report()}\nReleases: https://example.invalid/r" && g.Report().StartsWith("enabled 1 nohelp 0"), $"bare command reports every row, then the links: {g.Report()}");
        int confirmed = 0;
        var cbtn = g.Button("wipe", "Wipe", () => confirmed++, Tier.Advanced, confirm: true);
        E(cbtn.Confirm && g.Command("wipe") == "[cx-selfcheck] wipe" && confirmed == 1, "a confirming button fires straight away from chat; the two-click rule is the panel's");
        dens.Set(1f); tg.Set(false); nohelp.Set(true);
        g.ResetRows();
        E(dens.IsDefault && nohelp.IsDefault && !tg.V && sw.V == 0xffeeddccu, "ResetRows restores the User and Advanced rows and leaves ChatOnly and Hidden keys alone");
        tg.Set(true);
        E(g.Command("density 1.5") == "[cx-selfcheck] density 1.5 (was 2)", "the set form echoes new and old");
        E(g.Command("nosuch 1")!.StartsWith("[cx-selfcheck] unknown knob"), "an unknown knob is named");
        E(g.Command("dump") == "[cx-selfcheck] dump" && pressed == 2, "a button presses from chat");
        E(g.Command("reset")!.StartsWith("[cx-selfcheck] reset ->") && dens.IsDefault, "reset restores every default");
        E(sw.V == 0xffeeddccu && bag.Pairs.Length == 2, "...and leaves Hidden state alone");
        E(!g.Report().Contains("col ") && !g.Command("nosuch 1")!.Contains("col"), "Hidden state is on no report");
        g.Status = () => "status";
        E(g.Command("") == "[cx-selfcheck] status\nReleases: https://example.invalid/r", "a status line replaces the row report; the links follow it");
        var z = new KnobGroup("cx-selfcheck-zero", "Test", "Zero", null, null, true, Effect.Live, null, null, "", register: false);
        z.Status = () => "four\nlines";
        E(z.Knobs.Count == 0 && z.Command("") == "[cx-selfcheck-zero] four\nlines" && z.Command("reset") == z.Command("")
          && z.Command("nosuch 1") == z.Command(""), "a zero-knob group answers every form with its status line");

        // The live walk: every real declaration so far, against the same three constraints.
        string[] v; lock (_all) v = _violations.ToArray();
        foreach (string w in v) E(false, $"declaration refused: {w}");
        foreach (KnobGroup lg in All)
            for (int i = 0; i < lg._knobs.Count; i++)
                E(Constraints(lg._knobs[i]), $"{lg._knobs[i].Key} breaks a registry constraint");
        // The gate on the LIVE registry (spec §4.1): /settings always, every generated command only when general.commands is on.
        var live = CommandRegistry.Global;
        E(live.Knows("/settings"), "/settings dispatches regardless of general.commands");
        foreach (KnobGroup lg in All)
            E(live.Knows("/" + lg.Name) == General.Commands.V, $"/{lg.Name} is gated by general.commands");

        log.Info(ok ? "KnobGroup.SelfCheck: passed" : "KnobGroup.SelfCheck: FAILED");
    }

    /// <summary>The three constraints the registry enforces, as one predicate over a knob.</summary>
    private static bool Constraints(Knob k)
    {
        if (k.Tier == Tier.User && k.Help.Length == 0 && k.Kind != KnobKind.Link) return false;
        if (k.Layout && k.Kind != KnobKind.Dropdown) return false;
        if (k.Effect == Effect.Restart && !k.BootIntact) return false;
        return true;
    }
}
