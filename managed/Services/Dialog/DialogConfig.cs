using System.Globalization;
using System.Text;
using Sowx.Framework;

namespace Sowx.Services.Dialog;

/// <summary>
/// Persisted dialog-window state: rect (position + width), lock, and the animation knobs. Flat
/// <c>key=value</c>, its own file (<c>sowx-dialog.cfg</c>) -- the dialog is not a chat window, so it doesn't
/// share <see cref="Chat.Core.ChatConfig"/>'s per-window model. Font size stays global (one shared atlas), so
/// it is NOT stored here. Pure -- no native refs -- so it round-trips off-device in <see cref="SelfCheck"/>.
/// </summary>
public sealed class DialogConfig
{
    /// <summary>User-set position + width. <see cref="Placed"/> is false until the user first moves/resizes, so
    /// a fresh install falls back to the auto default layout instead of a stale (0,0) box. Height is not stored:
    /// the panel auto-stretches to content, capped by <see cref="MaxBodyH"/>.</summary>
    public bool Placed;
    public float X, Y, W;

    /// <summary>Breakpoint (px): the content height past which the window stops stretching and scrolls. Also the
    /// window's vertical resize (the grip drags it). Persisted independently of <see cref="Placed"/>.</summary>
    public float MaxBodyH = 320f;

    /// <summary>When true the window can't be moved or resized (settings toggle).</summary>
    public bool Locked;

    /// <summary>Master animation toggle + the two fade durations (ms). Off = content snaps in/out.</summary>
    public bool AnimEnabled = true;
    public float FadeInMs = 180f, FadeOutMs = 140f;

    /// <summary>Justify body + reply text to the panel width (settings toggle). Off = ragged right (default).</summary>
    public bool Justify;

    public string ToText()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        if (Placed) sb.Append(ci, $"x={X}\ny={Y}\nw={W}\n");
        sb.Append(ci, $"maxh={MaxBodyH}\n");
        sb.Append(ci, $"locked={(Locked ? 1 : 0)}\n");
        sb.Append(ci, $"anim={(AnimEnabled ? 1 : 0)}\n");
        sb.Append(ci, $"fadein={FadeInMs}\nfadeout={FadeOutMs}\n");
        sb.Append(ci, $"justify={(Justify ? 1 : 0)}\n");
        return sb.ToString();
    }

    /// <summary>Parse a config file's text; unknown keys are ignored (forward-compat), bad values keep defaults.</summary>
    public static DialogConfig Parse(string text)
    {
        var c = new DialogConfig();
        var ci = CultureInfo.InvariantCulture;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq), val = line.Substring(eq + 1);
            switch (key)
            {
                case "x": if (float.TryParse(val, ci, out float x)) { c.X = x; c.Placed = true; } break;
                case "y": if (float.TryParse(val, ci, out float y)) { c.Y = y; c.Placed = true; } break;
                case "w": if (float.TryParse(val, ci, out float w)) { c.W = w; c.Placed = true; } break;
                case "maxh": if (float.TryParse(val, ci, out float mh)) c.MaxBodyH = mh; break;
                case "locked": c.Locked = val != "0"; break;
                case "anim": c.AnimEnabled = val != "0"; break;
                case "fadein": if (float.TryParse(val, ci, out float fi)) c.FadeInMs = fi; break;
                case "fadeout": if (float.TryParse(val, ci, out float fo)) c.FadeOutMs = fo; break;
                case "justify": c.Justify = val != "0"; break;
            }
        }
        return c;
    }

    /// <summary>Off-device proof of the round-trip + the Placed sentinel + bad-value tolerance.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"DialogConfig.SelfCheck FAIL: {w}"); } }

        var a = new DialogConfig { Placed = true, X = 120, Y = 640, W = 560, MaxBodyH = 420, Locked = true,
                                   AnimEnabled = false, FadeInMs = 90, FadeOutMs = 60, Justify = true };
        var b = Parse(a.ToText());
        E(b.Placed && b.X == 120 && b.Y == 640 && b.W == 560, "rect round-trips");
        E(b.MaxBodyH == 420, "breakpoint round-trips");
        E(b.Locked && !b.AnimEnabled && b.FadeInMs == 90 && b.FadeOutMs == 60, "flags + knobs round-trip");
        E(b.Justify, "justify flag round-trips");

        // A never-placed config writes no rect; parsing it back stays unplaced (default layout wins).
        var fresh = Parse(new DialogConfig().ToText());
        E(!fresh.Placed, "unplaced config stays unplaced after round-trip");
        E(fresh.AnimEnabled && fresh.FadeInMs == 180f, "defaults survive round-trip");

        // Corrupt numerics keep defaults; unknown keys ignored.
        var g = Parse("x=oops\nfadein=\nzork=1\nlocked=1\n");
        E(!g.Placed && g.FadeInMs == 180f && g.Locked, "bad numerics keep defaults, known flag still applies");

        log.Info(ok ? "DialogConfig.SelfCheck: passed" : "DialogConfig.SelfCheck: FAILED");
    }
}
