using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sowx.Framework;

namespace Sowx.Services.Chat.Core;

/// <summary>Where a line's timestamp is read from. <see cref="Local"/> = the real wall clock (always
/// available). <see cref="Game"/> = the NWN in-game world clock (captured at ingest; falls back to local
/// when the world timer isn't up, e.g. the main menu).</summary>
public enum TsSource { Local, Game }

/// <summary>Timestamp presentation preset. ponytail: a fixed set of presets; a free-form format string is
/// the ceiling (needs a text field in the settings window -- a whole input control for one value).</summary>
public enum TsFormat { HourMin, HourMinSec, Hour12 }

/// <summary>Stable keys for the user-themeable chrome colours. Values live in the config as RGBA8
/// (<c>0xRRGGBBAA</c>) overrides; the defaults + the mapping onto individual widgets live in
/// <see cref="Render.WindowRenderer"/> (the look is a render concern). Only these are surfaced in the
/// settings UI; the renderer routes every chrome colour through the palette so more can be exposed later
/// without touching the draw code.</summary>
public static class ChatColor
{
    public const string Background = "bg";
    public const string Header = "header";
    public const string Accent = "accent";
    public const string Input = "input";
    public const string Selection = "sel";
    public const string Text = "text";

    /// <summary>The colours surfaced as picker rows, in display order. <c>Alpha</c> = the colour's transparency
    /// is honoured by the renderer, so the picker offers an A channel; false for <see cref="Text"/>, which draws
    /// opaque (ponytail: plumbing alpha through the glyph draw is the ceiling), so a Text-A slider would be dead.</summary>
    public static readonly (string Key, string Label, bool Alpha)[] Editable =
    {
        (Background, "Background", true), (Header, "Header", true), (Accent, "Accent", true),
        (Input, "Input bar", true), (Selection, "Selection", true), (Text, "Text", false),
    };
}

/// <summary>
/// The whole persisted chatbox configuration: window rect, render toggles, colour overrides, animation
/// knobs, input-strip side, and timestamp style/source. One flat <c>key=value</c> file (migrates the
/// legacy positional <c>x y w h toggles</c> format on read). Pure -- no native refs -- so it round-trips
/// off-device in <see cref="SelfCheck"/>. The shell (<c>ChatService</c>) is the source of truth cold and
/// mirrors the hot-path knobs (anim) onto plain statics; nothing here is read from the render/draw hot path.
/// </summary>
public sealed class ChatConfig
{
    // Window rect (mirrors the live ChatWindow; the shell syncs on save). 0 => "unset", keep the built-in.
    public float X, Y, W, H;
    public uint Toggles = ChatToggle.Timestamp | ChatToggle.MessageBody | ChatToggle.Portrait;

    /// <summary>Colour overrides keyed by <see cref="ChatColor"/> (RGBA8). Absent key = renderer default.</summary>
    public readonly Dictionary<string, uint> Colors = new();

    public bool AnimEnabled = true;
    /// <summary>Seconds a line stays opaque, the fade ramp length, and the slide/scroll ease time constant.</summary>
    public float HoldSec = 60f, FadeSec = 6f, TauMs = 70f;

    /// <summary>Glyph pixel height the atlas is baked at, and whether body text gets a dark outline (for
    /// readability over the 3D world). ponytail: system-font discovery is a separate feature; only size +
    /// outline here.</summary>
    public float FontPx = 18f;
    public bool Outline;

    /// <summary>Put the input strip above the window instead of below it.</summary>
    public bool InputOnTop;

    public TsFormat TimeFormat = TsFormat.HourMin;
    public TsSource TimeSource = TsSource.Local;

    /// <summary>Serialise to the flat key=value text written to disk.</summary>
    public string ToText()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(ci, $"x={X}\ny={Y}\nw={W}\nh={H}\n");
        sb.Append(ci, $"toggles={Toggles}\n");
        sb.Append(ci, $"anim={(AnimEnabled ? 1 : 0)}\nhold={HoldSec}\nfade={FadeSec}\ntau={TauMs}\n");
        sb.Append(ci, $"font={FontPx}\noutline={(Outline ? 1 : 0)}\n");
        sb.Append(ci, $"inputtop={(InputOnTop ? 1 : 0)}\n");
        sb.Append(ci, $"tsfmt={(int)TimeFormat}\ntssrc={(int)TimeSource}\n");
        foreach (var kv in Colors) sb.Append(ci, $"col.{kv.Key}={kv.Value:x8}\n");
        return sb.ToString();
    }

    /// <summary>Parse a config file's text, applying every recognised key onto a fresh instance. Unknown keys
    /// are ignored (forward-compat). A file with no '=' is the legacy positional <c>x y w h toggles</c>.</summary>
    public static ChatConfig Parse(string text)
    {
        var c = new ChatConfig();
        if (!text.Contains('='))
        {
            // Legacy migration: whitespace-separated x y w h [toggles].
            string[] p = text.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
            var ci = CultureInfo.InvariantCulture;
            if (p.Length >= 4 && float.TryParse(p[0], ci, out float lx) && float.TryParse(p[1], ci, out float ly)
                && float.TryParse(p[2], ci, out float lw) && float.TryParse(p[3], ci, out float lh))
            { c.X = lx; c.Y = ly; c.W = lw; c.H = lh; }
            if (p.Length >= 5 && uint.TryParse(p[4], out uint ltg)) c.Toggles = ltg;
            return c;
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq), val = line.Substring(eq + 1);
            c.Apply(key, val);
        }
        return c;
    }

    private void Apply(string key, string val)
    {
        var ci = CultureInfo.InvariantCulture;
        if (key.StartsWith("col.", System.StringComparison.Ordinal))
        {
            if (uint.TryParse(val, NumberStyles.HexNumber, ci, out uint rgba)) Colors[key.Substring(4)] = rgba;
            return;
        }
        // Every numeric case assigns only on a successful parse: a truncated/garbage value (a crash mid-write,
        // a hand-edit) leaves the field at its safe default rather than zeroing it -- Toggles=0 blanks all chat
        // and MessageBody has no UI to restore it, so an out-param parse straight into the field is a footgun.
        switch (key)
        {
            case "x": if (float.TryParse(val, ci, out float x)) X = x; break;
            case "y": if (float.TryParse(val, ci, out float y)) Y = y; break;
            case "w": if (float.TryParse(val, ci, out float w)) W = w; break;
            case "h": if (float.TryParse(val, ci, out float h)) H = h; break;
            case "toggles": if (uint.TryParse(val, out uint tg)) Toggles = tg; break;
            case "anim": AnimEnabled = val != "0"; break;
            case "hold": if (float.TryParse(val, ci, out float hold)) HoldSec = hold; break;
            case "fade": if (float.TryParse(val, ci, out float fade)) FadeSec = fade; break;
            case "tau": if (float.TryParse(val, ci, out float tau)) TauMs = tau; break;
            case "font": if (float.TryParse(val, ci, out float fp)) FontPx = fp; break;
            case "outline": Outline = val != "0"; break;
            case "inputtop": InputOnTop = val != "0"; break;
            case "tsfmt": if (int.TryParse(val, out int f)) TimeFormat = (TsFormat)f; break;
            case "tssrc": if (int.TryParse(val, out int s)) TimeSource = (TsSource)s; break;
        }
    }

    /// <summary>Off-device proof of the round-trip and the legacy migration.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"ChatConfig.SelfCheck FAIL: {w}"); } }

        var a = new ChatConfig
        {
            X = 150, Y = 120, W = 400, H = 300, Toggles = 5,
            AnimEnabled = false, HoldSec = 42, FadeSec = 3, TauMs = 90, FontPx = 24, Outline = true,
            InputOnTop = true, TimeFormat = TsFormat.Hour12, TimeSource = TsSource.Game,
        };
        a.Colors[ChatColor.Accent] = 0x1122ccffu;
        var b = Parse(a.ToText());
        E(b.X == 150 && b.Y == 120 && b.W == 400 && b.H == 300, "rect round-trips");
        E(b.Toggles == 5, "toggles round-trip");
        E(!b.AnimEnabled && b.HoldSec == 42 && b.FadeSec == 3 && b.TauMs == 90, "anim knobs round-trip");
        E(b.FontPx == 24 && b.Outline, "font size + outline round-trip");
        E(b.InputOnTop, "input side round-trips");
        E(b.TimeFormat == TsFormat.Hour12 && b.TimeSource == TsSource.Game, "timestamp style round-trips");
        E(b.Colors.TryGetValue(ChatColor.Accent, out uint acc) && acc == 0x1122ccffu, "colour override round-trips");

        // a corrupt/blank numeric value keeps the default, never zeroes the field.
        var g = Parse("toggles=\nhold=oops\nx=");
        E(g.Toggles == (ChatToggle.Timestamp | ChatToggle.MessageBody | ChatToggle.Portrait), "bad toggles keeps default");
        E(g.HoldSec == 60f && g.X == 0f, "bad numeric keeps default");

        // legacy positional file migrates (no '=' anywhere).
        var m = Parse("150 120 400 300 7");
        E(m.X == 150 && m.H == 300 && m.Toggles == 7, "legacy positional migrates");
        E(m.Colors.Count == 0 && m.AnimEnabled, "legacy leaves new fields at defaults");

        log.Info(ok ? "ChatConfig.SelfCheck: passed" : "ChatConfig.SelfCheck: FAILED");
    }
}
