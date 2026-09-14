using System;
using System.Collections.Generic;
using System.Globalization;
using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>
/// The per-window chat state as key/value pairs: rect, toggles, input side, send channel, look (colours,
/// animation, outline, timestamps) and the ordered tab list, one <see cref="WindowConfig"/> per window.
/// Window keys use <c>w&lt;N&gt;.</c> prefixes, tab keys <c>w&lt;N&gt;.t&lt;M&gt;.</c>; the pairs live in the
/// <c>chat</c> group's Hidden bag (<see cref="ChatKnobs.Windows"/>) and reach <c>cx.cfg</c> through it. The
/// globals that used to sit beside them are knobs now (<c>ui.font</c>, <c>chat.tellopen</c>). Pure -- no native
/// refs -- so it round-trips off-device in <see cref="SelfCheck"/>.
/// </summary>
public sealed class ChatConfig
{
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

    /// <summary>Every persisted pair, in window order.</summary>
    public List<KeyValuePair<string, string>> Pairs()
    {
        var ci = CultureInfo.InvariantCulture;
        var o = new List<KeyValuePair<string, string>>();
        void Put(string k, string v) => o.Add(new KeyValuePair<string, string>(k, v));
        for (int wi = 0; wi < Windows.Count; wi++)
        {
            var w = Windows[wi];
            string p = "w" + wi.ToString(ci) + ".";
            Put(p + "x", w.X.ToString(ci)); Put(p + "y", w.Y.ToString(ci));
            Put(p + "w", w.W.ToString(ci)); Put(p + "h", w.H.ToString(ci));
            Put(p + "toggles", w.Toggles.ToString(ci));
            Put(p + "inputtop", w.InputOnTop ? "1" : "0");
            Put(p + "sendch", w.SendChannel.ToString(ci));
            if (w.AnimEnabled is bool ae) Put(p + "anim", ae ? "1" : "0");
            if (w.HoldSec is float hs) Put(p + "hold", hs.ToString(ci));
            if (w.FadeSec is float fs) Put(p + "fade", fs.ToString(ci));
            if (w.TauMs is float tm) Put(p + "tau", tm.ToString(ci));
            if (w.Outline is bool ol) Put(p + "outline", ol ? "1" : "0");
            if (w.TimeFormat is int tf) Put(p + "tsfmt", tf.ToString(ci));
            if (w.TimeSource is int ts) Put(p + "tssrc", ts.ToString(ci));
            if (w.Align is int al) Put(p + "align", al.ToString(ci));
            if (w.BgMode is int bgm) Put(p + "bgmode", bgm.ToString(ci));
            if (w.AutoHideHeader is bool ahh) Put(p + "autohide", ahh ? "1" : "0");
            if (w.Locked is bool lk) Put(p + "locked", lk ? "1" : "0");
            if (w.TellsHere is bool th) Put(p + "tellshere", th ? "1" : "0");
            foreach (var kv in w.Colors) Put(p + "col." + kv.Key, kv.Value.ToString("x8", ci));
            for (int ti = 0; ti < w.Tabs.Count; ti++)
            {
                var (label, mask, pin, kept) = w.Tabs[ti];
                string tp = p + "t" + ti.ToString(ci) + ".";
                Put(tp + "label", label.Replace("\n", "").Replace("\r", ""));
                Put(tp + "mask", mask.ToString("x8", ci));
                if (pin.Length > 0) Put(tp + "pin", pin.Replace("\n", "").Replace("\r", ""));
                if (kept) Put(tp + "kept", "1");
            }
        }
        return o;
    }

    /// <summary>Build from pairs. Unknown keys are ignored (forward-compat); a bad value keeps the default.</summary>
    public static ChatConfig From(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var c = new ChatConfig();
        foreach (var kv in pairs) c.Apply(kv.Key, kv.Value);
        return c;
    }

    /// <summary>Route a <c>w&lt;N&gt;.*</c> key to the right window/tab slot.</summary>
    private void Apply(string key, string val)
    {
        var ci = CultureInfo.InvariantCulture;
        if (key.Length < 2 || key[0] != 'w' || !char.IsDigit(key[1])) return;
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
        if (rest.StartsWith("col.", StringComparison.Ordinal))
        {
            if (uint.TryParse(val, NumberStyles.HexNumber, ci, out uint rgba))
                w.Colors[rest.Substring(4)] = rgba;
            return;
        }

        switch (rest)
        {
            case "x": if (float.TryParse(val, ci, out float x)) w.X = x; break;
            case "y": if (float.TryParse(val, ci, out float y)) w.Y = y; break;
            case "w": if (float.TryParse(val, ci, out float wv)) w.W = wv; break;
            case "h": if (float.TryParse(val, ci, out float h)) w.H = h; break;
            case "toggles": if (uint.TryParse(val, out uint tg)) w.Toggles = tg; break;
            case "inputtop": w.InputOnTop = val != "0"; break;
            case "sendch": if (int.TryParse(val, out int sc) && sc >= 0 && sc <= 5) w.SendChannel = sc; break;
            case "anim": w.AnimEnabled = val != "0"; break;
            case "hold": if (float.TryParse(val, ci, out float hold)) w.HoldSec = hold; break;
            case "fade": if (float.TryParse(val, ci, out float fade)) w.FadeSec = fade; break;
            case "tau": if (float.TryParse(val, ci, out float tau)) w.TauMs = tau; break;
            case "outline": w.Outline = val != "0"; break;
            case "tsfmt": if (int.TryParse(val, out int f)) w.TimeFormat = f; break;
            case "tssrc": if (int.TryParse(val, out int s)) w.TimeSource = s; break;
            case "align": if (int.TryParse(val, out int a)) w.Align = a; break;
            case "bgmode": if (int.TryParse(val, out int bgm) && bgm >= 0 && bgm <= 9) w.BgMode = bgm; break;
            case "autohide": w.AutoHideHeader = val != "0"; break;
            case "locked": w.Locked = val != "0"; break;
            case "tellshere": w.TellsHere = val != "0"; break;
        }
    }

    /// <summary>Off-device proof of the pair round-trip and the edge cases.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"ChatConfig.SelfCheck FAIL: {w}"); } }
        static ChatConfig Text(string t)
        {
            var l = new List<KeyValuePair<string, string>>();
            ConfigFile.ForEachPair(t, (k, v) => l.Add(new KeyValuePair<string, string>(k, v)));
            return From(l);
        }

        // ---- round-trip: per-window settings + tabs ----
        var a = new ChatConfig();
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

        var b = From(a.Pairs());
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
        E(a.Pairs().TrueForAll(kv => kv.Key.StartsWith("w0.")), "every pair sits under its window prefix");

        // Nullable fields: absent = null (new windows get hardcoded defaults, not stale zeros).
        var a2 = new ChatConfig();
        var w1 = a2.EnsureWindow(0)!;
        w1.X = 100; w1.Y = 100; w1.W = 400; w1.H = 300;
        var b2 = From(a2.Pairs());
        E(b2.Windows[0].AnimEnabled == null, "unset anim stays null after round-trip");
        E(b2.Windows[0].HoldSec == null, "unset hold stays null after round-trip");
        E(b2.Windows[0].Locked == null, "unset locked stays null after round-trip");
        E(b2.Windows[0].BgMode == null, "unset bgmode stays null after round-trip");
        E(b2.Windows[0].TellsHere == null, "unset tellshere stays null after round-trip");

        // ---- corrupt/blank values keep defaults ----
        var g = Text("w0.toggles=\nw0.h=oops\nw0.x=\nw0.sendch=99\n");
        E(g.Windows.Count >= 1, "corrupt keys still create window slot");
        E(g.Windows[0].Toggles == (ChatToggle.Timestamp | ChatToggle.MessageBody | ChatToggle.Portrait), "bad toggles keeps default");
        E(g.Windows[0].H == 0f && g.Windows[0].X == 0f, "bad numeric keeps default (0)");
        E(g.Windows[0].SendChannel == 0, "out-of-range sendch keeps default");

        // ---- unknown keys ignored (forward-compat); no bare legacy keys ----
        var u = Text("w0.t0.zork=1\nw3.x=10\nbogus=1\nw0.x=50\nx=7\nfont=24\n");
        E(u.Windows.Count >= 4 && u.Windows[3].X == 10, "sparse window index created");
        E(u.Windows[0].X == 50, "known key alongside unknown parses fine");
        E(u.Windows[0].Tabs.Count == 0, "unknown tab field does not create phantom tabs");

        // ---- index cap ----
        var cap = Text("w100.x=5\n");
        E(cap.Windows.Count == 0, "over-cap window index rejected");

        // ---- newline in label stripped on write ----
        var nl = new ChatConfig();
        var nlw = nl.EnsureWindow(0)!;
        nlw.Tabs.Add(("line\none", 1, "pin\nbreak", false));
        E(nl.Pairs().TrueForAll(kv => !kv.Value.Contains('\n')), "newline stripped from label on write");
        var nlr = From(nl.Pairs());
        E(nlr.Windows[0].Tabs[0].Label == "lineone", "stripped label round-trips");
        E(nlr.Windows[0].Tabs[0].Pin == "pinbreak", "stripped pin round-trips");

        // Pin with special chars (space, =) round-trips
        var pc = new ChatConfig();
        var pcw = pc.EnsureWindow(0)!;
        pcw.Tabs.Add(("Tell", 2, "Name With Spaces", false));
        pcw.Tabs.Add(("Tell2", 2, "name=equals", false));
        var pcr = From(pc.Pairs());
        E(pcr.Windows[0].Tabs[0].Pin == "Name With Spaces", "pin with spaces round-trips");
        E(pcr.Windows[0].Tabs[1].Pin == "name=equals", "pin with = round-trips");

        log.Info(ok ? "ChatConfig.SelfCheck: passed" : "ChatConfig.SelfCheck: FAILED");
    }
}
