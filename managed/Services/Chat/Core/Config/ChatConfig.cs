using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>
/// The whole persisted chatbox configuration: global chrome (font, colours, animation, timestamps) plus
/// per-window layout (rect, toggles, input side, send channel, tabs). Flat <c>key=value</c> file; window
/// keys use <c>w&lt;N&gt;.</c> prefixes, tab keys <c>w&lt;N&gt;.t&lt;M&gt;.</c>. Migrates two legacy
/// formats: v1 (key=value with bare rect/toggles) and v0 (positional <c>x y w h toggles</c>). Pure -- no
/// native refs -- so it round-trips off-device in <see cref="SelfCheck"/>.
/// </summary>
public sealed class ChatConfig
{
    /// <summary>Glyph pixel height the atlas is baked at. Global (one shared atlas across all windows);
    /// per-window font requires per-window atlas + scale, a deeper change.</summary>
    public float FontPx = 18f;

    /// <summary>Tell auto-open routing mode (#46). Off = no auto-open (default).</summary>
    public TellAutoOpen TellOpen = TellAutoOpen.Off;

    /// <summary>Per-window configs, indexed by window number. Index 0 = Main. Empty on a fresh config (the
    /// shell's hardcoded defaults apply).</summary>
    public readonly List<WindowConfig> Windows = new();

    // ponytail: 64 windows/tabs is well past any sane use; caps a hand-edited w999999.x=5 from OOMing.
    private const int MaxIndex = 64;

    /// <summary>Get or create the <see cref="WindowConfig"/> at <paramref name="i"/>, filling gaps with
    /// default instances. Returns null for out-of-range indices (corrupt-file guard).</summary>
    private WindowConfig? EnsureWindow(int i)
    {
        if (i < 0 || i >= MaxIndex) return null;
        while (Windows.Count <= i) Windows.Add(new WindowConfig());
        return Windows[i];
    }

    /// <summary>Serialise to the flat key=value text written to disk.</summary>
    public string ToText()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(ci, $"font={FontPx}\n");
        if (TellOpen != TellAutoOpen.Off) sb.Append(ci, $"tellopen={(int)TellOpen}\n");
        for (int wi = 0; wi < Windows.Count; wi++)
        {
            var w = Windows[wi];
            sb.Append(ci, $"w{wi}.x={w.X}\nw{wi}.y={w.Y}\nw{wi}.w={w.W}\nw{wi}.h={w.H}\n");
            sb.Append(ci, $"w{wi}.toggles={w.Toggles}\nw{wi}.inputtop={(w.InputOnTop ? 1 : 0)}\n");
            sb.Append(ci, $"w{wi}.sendch={w.SendChannel}\n");
            if (w.AnimEnabled is bool ae) sb.Append(ci, $"w{wi}.anim={(ae ? 1 : 0)}\n");
            if (w.HoldSec is float hs) sb.Append(ci, $"w{wi}.hold={hs}\n");
            if (w.FadeSec is float fs) sb.Append(ci, $"w{wi}.fade={fs}\n");
            if (w.TauMs is float tm) sb.Append(ci, $"w{wi}.tau={tm}\n");
            if (w.Outline is bool ol) sb.Append(ci, $"w{wi}.outline={(ol ? 1 : 0)}\n");
            if (w.TimeFormat is int tf) sb.Append(ci, $"w{wi}.tsfmt={tf}\n");
            if (w.TimeSource is int ts) sb.Append(ci, $"w{wi}.tssrc={ts}\n");
            if (w.Align is int al) sb.Append(ci, $"w{wi}.align={al}\n");
            if (w.BgMode is int bgm) sb.Append(ci, $"w{wi}.bgmode={bgm}\n");
            if (w.AutoHideHeader is bool ahh) sb.Append(ci, $"w{wi}.autohide={(ahh ? 1 : 0)}\n");
            if (w.Locked is bool lk) sb.Append(ci, $"w{wi}.locked={(lk ? 1 : 0)}\n");
            if (w.TellsHere is bool th) sb.Append(ci, $"w{wi}.tellshere={(th ? 1 : 0)}\n");
            foreach (var kv in w.Colors) sb.Append(ci, $"w{wi}.col.{kv.Key}={kv.Value:x8}\n");
            for (int ti = 0; ti < w.Tabs.Count; ti++)
            {
                var (label, mask, pin, kept) = w.Tabs[ti];
                string safe = label.Replace("\n", "").Replace("\r", "");
                sb.Append(ci, $"w{wi}.t{ti}.label={safe}\nw{wi}.t{ti}.mask={mask:x8}\n");
                if (pin.Length > 0)
                {
                    string safePin = pin.Replace("\n", "").Replace("\r", "");
                    sb.Append(ci, $"w{wi}.t{ti}.pin={safePin}\n");
                }
                if (kept) sb.Append(ci, $"w{wi}.t{ti}.kept=1\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>Parse a config file's text. Handles three formats: the multi-window <c>w0.*</c> format,
    /// the legacy v1 flat <c>key=value</c> (bare rect/toggles → window 0), and the legacy v0 positional
    /// <c>x y w h toggles</c>. Unknown keys are ignored (forward-compat).</summary>
    public static ChatConfig Parse(string text)
    {
        var c = new ChatConfig();
        if (!text.Contains('='))
        {
            // Legacy v0: whitespace-separated x y w h [toggles].
            string[] p = text.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
            var ci = CultureInfo.InvariantCulture;
            var w0 = c.EnsureWindow(0)!;
            if (p.Length >= 4 && float.TryParse(p[0], ci, out float lx) && float.TryParse(p[1], ci, out float ly)
                && float.TryParse(p[2], ci, out float lw) && float.TryParse(p[3], ci, out float lh))
            { w0.X = lx; w0.Y = ly; w0.W = lw; w0.H = lh; }
            if (p.Length >= 5 && uint.TryParse(p[4], out uint ltg)) w0.Toggles = ltg;
            return c;
        }

        ConfigFile.ForEachPair(text, c.Apply);
        return c;
    }

    private void Apply(string key, string val)
    {
        var ci = CultureInfo.InvariantCulture;

        // Multi-window keys: w<N>. or w<N>.t<M>.
        if (key.Length > 1 && key[0] == 'w' && char.IsDigit(key[1]))
        {
            ApplyWindow(key, val, ci);
            return;
        }

        // Legacy global colour overrides -> migrate to window 0.
        if (key.StartsWith("col.", System.StringComparison.Ordinal))
        {
            if (uint.TryParse(val, NumberStyles.HexNumber, ci, out uint rgba))
                EnsureWindow(0)!.Colors[key.Substring(4)] = rgba;
            return;
        }

        // Legacy v1 bare window keys (no w0. prefix) -> window 0.
        switch (key)
        {
            case "x": case "y": case "w": case "h": case "toggles": case "inputtop":
                ApplyWindowField(EnsureWindow(0), key, val, ci);
                return;
        }

        // Legacy global chrome keys -> migrate to window 0 per-window settings.
        var w0 = EnsureWindow(0);
        switch (key)
        {
            case "anim": if (w0 != null) w0.AnimEnabled = val != "0"; break;
            case "hold": if (float.TryParse(val, ci, out float hold) && w0 != null) w0.HoldSec = hold; break;
            case "fade": if (float.TryParse(val, ci, out float fade) && w0 != null) w0.FadeSec = fade; break;
            case "tau": if (float.TryParse(val, ci, out float tau) && w0 != null) w0.TauMs = tau; break;
            case "outline": if (w0 != null) w0.Outline = val != "0"; break;
            case "tsfmt": if (int.TryParse(val, out int f) && w0 != null) w0.TimeFormat = f; break;
            case "tssrc": if (int.TryParse(val, out int s) && w0 != null) w0.TimeSource = s; break;
            case "align": if (int.TryParse(val, out int al) && w0 != null) w0.Align = al; break;
            case "autohide": if (w0 != null) w0.AutoHideHeader = val != "0"; break;
            case "font": if (float.TryParse(val, ci, out float fp)) FontPx = fp; break;
            case "tellopen": if (int.TryParse(val, out int to) && to >= 0 && to <= 2) TellOpen = (TellAutoOpen)to; break;
        }
    }

    /// <summary>Route a <c>w&lt;N&gt;.*</c> key to the right window/tab slot.</summary>
    private void ApplyWindow(string key, string val, CultureInfo ci)
    {
        // Parse: w<N>.field or w<N>.t<M>.field
        int dot1 = key.IndexOf('.');
        if (dot1 <= 1) return;
        if (!int.TryParse(key.Substring(1, dot1 - 1), out int wi)) return;
        var w = EnsureWindow(wi);
        if (w == null) return;
        string rest = key.Substring(dot1 + 1);

        // Tab key: t<M>.field
        if (rest.Length > 1 && rest[0] == 't' && char.IsDigit(rest[1]))
        {
            int dot2 = rest.IndexOf('.');
            if (dot2 <= 1) return;
            if (!int.TryParse(rest.Substring(1, dot2 - 1), out int ti)) return;
            if (ti < 0 || ti >= MaxIndex) return;
            string field = rest.Substring(dot2 + 1);
            if (field != "label" && field != "mask" && field != "pin" && field != "kept") return;
            while (w.Tabs.Count <= ti) w.Tabs.Add(("", 0xffffffff, "", false));
            var (label, mask, pin, kept) = w.Tabs[ti];
            switch (field)
            {
                case "label": w.Tabs[ti] = (val, mask, pin, kept); break;
                case "mask":
                    if (uint.TryParse(val, NumberStyles.HexNumber, ci, out uint m)) w.Tabs[ti] = (label, m, pin, kept);
                    break;
                case "pin": w.Tabs[ti] = (label, mask, val, kept); break;
                case "kept": w.Tabs[ti] = (label, mask, pin, val != "0"); break;
            }
            return;
        }

        // Per-window colour: w<N>.col.<key>=<hex>
        if (rest.StartsWith("col.", System.StringComparison.Ordinal))
        {
            if (uint.TryParse(val, NumberStyles.HexNumber, ci, out uint rgba))
                w.Colors[rest.Substring(4)] = rgba;
            return;
        }

        // Per-window settings (look).
        switch (rest)
        {
            case "anim": w.AnimEnabled = val != "0"; return;
            case "hold": if (float.TryParse(val, ci, out float hold)) w.HoldSec = hold; return;
            case "fade": if (float.TryParse(val, ci, out float fade)) w.FadeSec = fade; return;
            case "tau": if (float.TryParse(val, ci, out float tau)) w.TauMs = tau; return;
            case "outline": w.Outline = val != "0"; return;
            case "tsfmt": if (int.TryParse(val, out int f)) w.TimeFormat = f; return;
            case "tssrc": if (int.TryParse(val, out int s)) w.TimeSource = s; return;
            case "align": if (int.TryParse(val, out int a)) w.Align = a; return;
            case "bgmode": if (int.TryParse(val, out int bgm) && bgm >= 0 && bgm <= 9) w.BgMode = bgm; return;
            case "autohide": w.AutoHideHeader = val != "0"; return;
            case "locked": w.Locked = val != "0"; return;
            case "tellshere": w.TellsHere = val != "0"; return;
        }

        ApplyWindowField(w, rest, val, ci);
    }

    private static void ApplyWindowField(WindowConfig? w, string field, string val, CultureInfo ci)
    {
        if (w == null) return;
        switch (field)
        {
            case "x": if (float.TryParse(val, ci, out float x)) w.X = x; break;
            case "y": if (float.TryParse(val, ci, out float y)) w.Y = y; break;
            case "w": if (float.TryParse(val, ci, out float wv)) w.W = wv; break;
            case "h": if (float.TryParse(val, ci, out float h)) w.H = h; break;
            case "toggles": if (uint.TryParse(val, out uint tg)) w.Toggles = tg; break;
            case "inputtop": w.InputOnTop = val != "0"; break;
            case "sendch": if (int.TryParse(val, out int sc) && sc >= 0 && sc <= 5) w.SendChannel = sc; break;
        }
    }

    /// <summary>Off-device proof of the round-trip, legacy migrations, and edge cases.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"ChatConfig.SelfCheck FAIL: {w}"); } }

        // ---- new-format round-trip: per-window settings + tabs ----
        var a = new ChatConfig { FontPx = 24, TellOpen = TellAutoOpen.NewTab };
        var w0 = a.EnsureWindow(0)!;
        w0.X = 150; w0.Y = 120; w0.W = 400; w0.H = 300; w0.Toggles = 5;
        w0.InputOnTop = true; w0.SendChannel = 2;
        w0.AnimEnabled = false; w0.HoldSec = 42f; w0.FadeSec = 3f; w0.TauMs = 90f;
        w0.Outline = true; w0.TimeFormat = (int)TsFormat.Hour12; w0.TimeSource = (int)TsSource.Game;
        w0.Align = (int)TextAlign.Center;
        w0.BgMode = (int)BgMode.GradientV;
        w0.AutoHideHeader = false;
        w0.Locked = true;
        w0.TellsHere = true;
        w0.Colors[ChatColor.Accent] = 0x1122ccffu;
        w0.Tabs.Add(("All channels", 0xffffffff, "", false));
        w0.Tabs.Add(("mask=test", 0x00000002, "Alice", true));

        var b = Parse(a.ToText());
        E(b.FontPx == 24, "font size round-trips");
        E(b.TellOpen == TellAutoOpen.NewTab, "tellopen round-trips");
        E(b.Windows.Count >= 1, "window 0 present");
        var bw0 = b.Windows[0];
        E(bw0.X == 150 && bw0.Y == 120 && bw0.W == 400 && bw0.H == 300, "window rect round-trips");
        E(bw0.Toggles == 5, "window toggles round-trip");
        E(bw0.InputOnTop, "window input side round-trips");
        E(bw0.SendChannel == 2, "window send channel round-trips");
        E(bw0.AnimEnabled == false && bw0.HoldSec == 42f && bw0.FadeSec == 3f && bw0.TauMs == 90f, "per-window anim round-trips");
        E(bw0.Outline == true, "per-window outline round-trips");
        E(bw0.TimeFormat == (int)TsFormat.Hour12 && bw0.TimeSource == (int)TsSource.Game, "per-window ts round-trips");
        E(bw0.Align == (int)TextAlign.Center, "per-window align round-trips");
        E(bw0.BgMode == (int)BgMode.GradientV, "per-window bgmode round-trips");
        E(bw0.AutoHideHeader == false, "per-window autohide round-trips");
        E(bw0.Locked == true, "per-window locked round-trips");
        E(bw0.TellsHere == true, "per-window tellshere round-trips");
        E(bw0.Colors.TryGetValue(ChatColor.Accent, out uint acc) && acc == 0x1122ccffu, "per-window colour round-trips");
        E(bw0.Tabs.Count == 2, "two tabs round-trip");
        E(bw0.Tabs[0].Label == "All channels" && bw0.Tabs[0].Mask == 0xffffffff && bw0.Tabs[0].Pin == "" && !bw0.Tabs[0].Kept, "tab 0 round-trips (label with space, no pin, not kept)");
        E(bw0.Tabs[1].Label == "mask=test" && bw0.Tabs[1].Mask == 0x00000002 && bw0.Tabs[1].Pin == "Alice" && bw0.Tabs[1].Kept, "tab 1 round-trips (label with =, pin, kept)");

        // Nullable fields: absent = null (new windows get hardcoded defaults, not stale zeros).
        var a2 = new ChatConfig();
        var w1 = a2.EnsureWindow(0)!;
        w1.X = 100; w1.Y = 100; w1.W = 400; w1.H = 300;
        var b2 = Parse(a2.ToText());
        E(b2.Windows[0].AnimEnabled == null, "unset anim stays null after round-trip");
        E(b2.Windows[0].HoldSec == null, "unset hold stays null after round-trip");
        E(b2.Windows[0].Locked == null, "unset locked stays null after round-trip");
        E(b2.Windows[0].BgMode == null, "unset bgmode stays null after round-trip");
        E(b2.Windows[0].TellsHere == null, "unset tellshere stays null after round-trip");

        // TellAutoOpen defaults to Off when absent.
        E(b2.TellOpen == TellAutoOpen.Off, "unset tellopen stays Off after round-trip");

        // ---- legacy v1 migration (bare global keys -> window 0 per-window settings) ----
        var v1 = Parse("x=150\ny=120\nw=400\nh=300\ntoggles=5\nanim=1\nhold=60\nfade=6\ntau=70\n" +
                        "font=18\noutline=0\ninputtop=1\ntsfmt=0\ntssrc=0\ncol.accent=1122ccff\n");
        E(v1.Windows.Count >= 1, "v1 creates window 0");
        E(v1.Windows[0].X == 150 && v1.Windows[0].H == 300 && v1.Windows[0].Toggles == 5, "v1 rect+toggles migrate");
        E(v1.Windows[0].InputOnTop, "v1 inputtop migrates");
        E(v1.Windows[0].Tabs.Count == 0, "v1 has no tabs (shell uses defaults)");
        E(v1.FontPx == 18, "v1 font migrates as global");
        E(v1.Windows[0].AnimEnabled == true, "v1 anim=1 migrates to window 0");
        E(v1.Windows[0].HoldSec == 60f, "v1 hold migrates to window 0");
        E(v1.Windows[0].Outline == false, "v1 outline=0 migrates to window 0");
        E(v1.Windows[0].Colors.TryGetValue(ChatColor.Accent, out uint vacc) && vacc == 0x1122ccffu, "v1 colour migrates to window 0");

        // ---- legacy v0 positional ----
        var m = Parse("150 120 400 300 7");
        E(m.Windows.Count >= 1 && m.Windows[0].X == 150 && m.Windows[0].H == 300 && m.Windows[0].Toggles == 7,
            "v0 positional migrates to window 0");
        E(m.Windows[0].Tabs.Count == 0 && m.Windows[0].AnimEnabled == null, "v0 leaves look fields null");

        // ---- corrupt/blank values keep defaults ----
        var g = Parse("w0.toggles=\nw0.h=oops\nw0.x=\nw0.sendch=99\n");
        E(g.Windows.Count >= 1, "corrupt keys still create window slot");
        E(g.Windows[0].Toggles == (ChatToggle.Timestamp | ChatToggle.MessageBody | ChatToggle.Portrait), "bad toggles keeps default");
        E(g.Windows[0].H == 0f && g.Windows[0].X == 0f, "bad numeric keeps default (0)");
        E(g.Windows[0].SendChannel == 0, "out-of-range sendch keeps default");

        // ---- unknown keys ignored (forward-compat) ----
        var u = Parse("w0.t0.zork=1\nw3.x=10\nbogus=1\nw0.x=50\n");
        E(u.Windows.Count >= 4 && u.Windows[3].X == 10, "sparse window index created");
        E(u.Windows[0].X == 50, "known key alongside unknown parses fine");
        E(u.Windows[0].Tabs.Count == 0, "unknown tab field does not create phantom tabs");

        // ---- index cap ----
        var cap = Parse("w100.x=5\n");
        E(cap.Windows.Count == 0, "over-cap window index rejected");

        // ---- newline in label stripped on write ----
        var nl = new ChatConfig();
        var nlw = nl.EnsureWindow(0)!;
        nlw.Tabs.Add(("line\none", 1, "pin\nbreak", false));
        E(!nl.ToText().Contains("line\none"), "newline stripped from label on write");
        var nlr = Parse(nl.ToText());
        E(nlr.Windows[0].Tabs[0].Label == "lineone", "stripped label round-trips");
        E(nlr.Windows[0].Tabs[0].Pin == "pinbreak", "stripped pin round-trips");

        // Pin with special chars (space, =) round-trips
        var pc = new ChatConfig();
        var pcw = pc.EnsureWindow(0)!;
        pcw.Tabs.Add(("Tell", 2, "Name With Spaces", false));
        pcw.Tabs.Add(("Tell2", 2, "name=equals", false));
        var pcr = Parse(pc.ToText());
        E(pcr.Windows[0].Tabs[0].Pin == "Name With Spaces", "pin with spaces round-trips");
        E(pcr.Windows[0].Tabs[1].Pin == "name=equals", "pin with = round-trips");

        log.Info(ok ? "ChatConfig.SelfCheck: passed" : "ChatConfig.SelfCheck: FAILED");
    }
}
