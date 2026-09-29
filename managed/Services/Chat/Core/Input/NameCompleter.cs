using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>Tab completion of names in the input line. The first Tab takes the partial word before the caret and
/// the caller's candidates (nearest first); later Tabs cycle the same matches. Inside an open <c>/t "</c> target
/// the pick also closes the quote. Pure: text and candidates in, a range to replace out; the caller applies it
/// with <c>EditBuffer.ReplaceRange</c> and calls <see cref="Reset"/> on any other key.</summary>
public sealed class NameCompleter
{
    private string[] _matches = Array.Empty<string>();
    private int _idx, _start, _end;
    private string _suffix = "";

    /// <summary>A cycle is running: the next Tab steps it instead of starting over.</summary>
    public bool Active { get; private set; }

    public void Reset() { Active = false; _matches = Array.Empty<string>(); }

    /// <summary>Start a cycle at <paramref name="caret"/>: the replacement for the first match, or null when there is
    /// no partial name there or nothing matches it.</summary>
    public (int Start, int End, string With)? Begin(string text, int caret, IReadOnlyList<string> candidates)
    {
        Reset();
        if (!Token(text, caret, out int start, out int end, out string suffix)) return null;
        string[] m = Matches(text.Substring(start, caret - start), candidates);
        if (m.Length == 0) return null;
        _matches = m; _idx = 0; _start = start; _suffix = suffix; Active = true;
        _end = end;
        return Emit();
    }

    /// <summary>Next match (<paramref name="back"/>: the previous one), replacing the last pick.</summary>
    public (int Start, int End, string With)? Step(bool back)
    {
        if (!Active) return null;
        _idx = (_idx + (back ? _matches.Length - 1 : 1)) % _matches.Length;
        return Emit();
    }

    private (int, int, string) Emit()
    {
        string with = _matches[_idx] + _suffix;
        var r = (_start, _end, with);
        _end = _start + with.Length;
        return r;
    }

    /// <summary>The partial name ending at the caret. In a tell target (<c>/t "Pa|</c>) it is everything after the
    /// quote, and the replacement also swallows an auto-closed quote at the caret and ends with <c>" </c>. Elsewhere
    /// it is the run of characters back to whitespace or a markup mark (<c>*nods to Ae|</c> gives <c>Ae</c>).</summary>
    internal static bool Token(string text, int caret, out int start, out int end, out string suffix)
    {
        start = end = caret; suffix = "";
        if (caret < 0 || caret > text.Length) return false;
        int q = TellQuote(text);
        int close = q < 0 ? -1 : text.IndexOf('"', q + 1);
        if (q >= 0 && caret > q && (close < 0 || close >= caret))
        {
            start = q + 1;
            end = close == caret ? caret + 1 : caret;
            suffix = "\" ";
            return caret > start;
        }
        while (start > 0 && !Stop(text[start - 1])) start--;
        return start < caret;
    }

    private static bool Stop(char c) => char.IsWhiteSpace(c) || c is '*' or '"' or '(' or '[' or '/';

    /// <summary>Index of the opening quote of a tell command's target (<c>/t "</c>, <c>/tell "</c>, <c>/tp "</c>,
    /// <c>/tellplayer "</c>), or -1.</summary>
    private static int TellQuote(string text)
    {
        if (text.Length < 4 || text[0] != '/') return -1;
        int sp = text.IndexOf(' ');
        if (sp < 0 || sp + 1 >= text.Length || text[sp + 1] != '"') return -1;
        string cmd = text.Substring(1, sp - 1).ToLowerInvariant();
        return cmd is "t" or "tell" or "tp" or "tellplayer" ? sp + 1 : -1;
    }

    /// <summary>Candidates whose name starts with <paramref name="prefix"/>, then those with a later word that does,
    /// each in the caller's order, case-insensitive, duplicates and colour tags dropped.</summary>
    internal static string[] Matches(string prefix, IReadOnlyList<string> candidates)
    {
        var whole = new List<string>();
        var word = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < candidates.Count; i++)
        {
            string n = Clean(candidates[i]);
            if (n.Length == 0 || !seen.Add(n)) continue;
            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) whole.Add(n);
            else if (n.Contains(" " + prefix, StringComparison.OrdinalIgnoreCase)) word.Add(n);
        }
        whole.AddRange(word);
        return whole.ToArray();
    }

    /// <summary>A name with its <c>&lt;cRGB&gt;</c> / <c>&lt;/c&gt;</c> colour tags removed and spaces trimmed.</summary>
    internal static string Clean(string name)
    {
        if (name.IndexOf('<') < 0) return name.Trim();
        var sb = new System.Text.StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '<' && i + 5 < name.Length && name[i + 1] == 'c' && name[i + 5] == '>') { i += 5; continue; }
            if (string.CompareOrdinal(name, i, "</c>", 0, 4) == 0) { i += 3; continue; }
            sb.Append(name[i]);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Off-device proof of the token rules, matching order, cycling and the tell-target quote.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string[] names = { "Aelar Galanodel", "Aeris", "<cþ  >Borin</c>", "aeris", "Guard Captain" };

        var c = new NameCompleter();
        string t = "*nods to Ae";
        check.Expect(c.Begin(t, t.Length, names) == (9, 11, "Aelar Galanodel"), "partial word after a space completes to the nearest match");
        check.Expect(c.Step(false) == (9, 24, "Aeris"), "Tab again replaces the last pick with the next match");
        check.Expect(c.Step(false) == (9, 14, "Aelar Galanodel"), "the cycle wraps; a case-only duplicate is dropped");
        check.Expect(c.Step(true) == (9, 24, "Aeris"), "Shift+Tab steps back");

        t = "*bows to Cap";
        check.Expect(c.Begin(t, t.Length, names) == (9, 12, "Guard Captain"), "a later word of a name matches too");
        t = "*waves at Bo";
        check.Expect(c.Begin(t, t.Length, names) == (10, 12, "Borin"), "colour tags are stripped from a candidate");
        t = "*waves";
        check.Expect(c.Begin(t, t.Length, names) == null && !c.Active, "no match leaves nothing active");
        t = "hi ";
        check.Expect(c.Begin(t, t.Length, names) == null, "no partial word before the caret is no completion");
        t = "*Ae";
        check.Expect(c.Begin(t, t.Length, names) is (1, 3, "Aelar Galanodel"), "an emote mark ends the partial word");

        t = "/t \"Bor";
        check.Expect(c.Begin(t, t.Length, names) == (4, 7, "Borin\" "), "a tell target closes its quote and adds a space");
        t = "/t \"Bor\"";
        check.Expect(c.Begin(t, 7, names) == (4, 8, "Borin\" "), "an auto-closed quote at the caret is swallowed");
        t = "/tell \"Guard C";
        check.Expect(c.Begin(t, t.Length, names) == (7, 14, "Guard Captain\" "), "a tell target may hold a space");
        t = "/t \"Borin\" hi Ae";
        check.Expect(c.Begin(t, t.Length, names) == (14, 16, "Aelar Galanodel"), "after a closed target, words complete normally");

        log.Info(check.Ok ? "NameCompleter.SelfCheck PASS" : "NameCompleter.SelfCheck had FAILURES");
    }
}
