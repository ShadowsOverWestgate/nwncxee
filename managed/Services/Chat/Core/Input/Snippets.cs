using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>Chat shortcuts: the word before the caret, typed then ended with a space or Enter, becomes its
/// expansion. Each <see cref="SnippetKnobs.List"/> entry is <c>abbreviation expansion</c>, split at the first
/// space. Pure over the entries, so it self-checks off-device; cold (the input thread, once per space).</summary>
public static class Snippets
{
    /// <summary>Split one entry into its abbreviation and expansion, or null for an empty or one-word entry.</summary>
    public static (string Abbr, string Expansion)? Parse(string slot)
    {
        string s = slot.Trim();
        int sp = s.IndexOf(' ');
        if (sp <= 0) return null;
        string exp = s.Substring(sp + 1).TrimStart();
        return exp.Length == 0 ? null : (s.Substring(0, sp), exp);
    }

    /// <summary>The expansion of <paramref name="token"/> among <paramref name="slots"/> (case-insensitive, first entry
    /// wins), or null.</summary>
    public static string? Lookup(string token, IReadOnlyList<string> slots)
    {
        if (token.Length == 0) return null;
        for (int i = 0; i < slots.Count; i++)
            if (Parse(slots[i]) is var (abbr, exp) && string.Equals(abbr, token, StringComparison.OrdinalIgnoreCase))
                return exp;
        return null;
    }

    /// <summary>The live list's expansion of <paramref name="token"/>, or null when the submenu's master is off.</summary>
    public static string? Lookup(string token) => SnippetKnobs.Enabled.V ? Lookup(token, SnippetKnobs.List.V) : null;

    /// <summary>Off-device proof of the row grammar and the lookup.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(Parse(";bow *bows deeply*") == (";bow", "*bows deeply*"), "first space splits abbreviation from expansion");
        check.Expect(Parse("  ;nod    *nods*  ") == (";nod", "*nods*"), "surrounding and separating spaces trimmed");
        check.Expect(Parse("") == null && Parse(";lonely") == null && Parse(";x   ") == null, "empty and one-word rows are no shortcut");
        string[] slots = { "", ";bow *bows deeply*", ";BOW shadowed", "brb ((be right back))" };
        check.Expect(Lookup(";bow", slots) == "*bows deeply*", "lookup finds the row");
        check.Expect(Lookup(";Bow", slots) == "*bows deeply*", "lookup ignores case, first row wins");
        check.Expect(Lookup("brb", slots) == "((be right back))", "a shortcut needs no leading mark");
        check.Expect(Lookup(";bo", slots) == null && Lookup("", slots) == null, "a prefix or an empty token is no match");
        log.Info(check.Ok ? "Snippets.SelfCheck PASS" : "Snippets.SelfCheck had FAILURES");
    }
}
