using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>
/// Roleplay markup of the line being typed: <c>*emotes*</c>, <c>"speech"</c> and <c>((out of character))</c>,
/// one class per UTF-16 char in a single left-to-right scan. Pure Core (no native refs, no allocation) so the
/// renderer can classify per keystroke and the whole thing self-checks off-device.
/// An unclosed marker deliberately colours to the end of the line: that IS the feature -- the player sees the
/// forgotten closing <c>*</c> before pressing Enter, instead of after the server has echoed it.
/// </summary>
public static class InputMarkup
{
    /// <summary>The classes <see cref="Classify"/> writes; the renderer maps each to a colour.</summary>
    public const byte Plain = 0, Emote = 1, Speech = 2, Ooc = 3;

    /// <summary>Whitespace that ends a leading slash-command token, mirroring <see cref="ChatCommands"/>.</summary>
    private static readonly char[] Whitespace = { ' ', '\t' };

    /// <summary>Classify every UTF-16 char of <paramref name="text"/> into <paramref name="cls"/>, which must be
    /// at least <c>text.Length</c> long. Inside an emote a <c>"</c> does not switch, inside speech a <c>*</c> does
    /// not, and inside OOC nothing does; a leading slash-command token (plus a tell's quoted target) stays
    /// <see cref="Plain"/> so the command itself is never painted as markup.</summary>
    public static void Classify(string text, Span<byte> cls)
    {
        int n = text.Length;
        for (int i = 0; i < n; i++) cls[i] = Plain;
        byte state = Plain;
        for (int i = n > 0 && text[0] == '/' ? CommandPrefix(text) : 0; i < n; i++)
        {
            char c = text[i];
            if (state == Plain)
            {
                if (c == '(' && i + 1 < n && text[i + 1] == '(') { cls[i] = cls[i + 1] = Ooc; state = Ooc; i++; }
                else if (c == '*') { cls[i] = Emote; state = Emote; }
                else if (c == '"') { cls[i] = Speech; state = Speech; }
                continue;
            }
            cls[i] = state;
            if (state == Ooc)
            {
                if (c == ')' && i + 1 < n && text[i + 1] == ')') { cls[i + 1] = Ooc; state = Plain; i++; }
            }
            else if (c == (state == Emote ? '*' : '"')) state = Plain;
        }
    }

    /// <summary>How much of a leading slash-command line stays <see cref="Plain"/>: the first whitespace-delimited
    /// token, and for a tell command the whitespace plus quoted target that follows it. Classification of the
    /// message resumes at the returned index.</summary>
    private static int CommandPrefix(string text)
    {
        int sp = text.IndexOfAny(Whitespace);
        if (sp < 0) return text.Length;
        if (!IsTell(text.AsSpan(1, sp - 1))) return sp;
        int j = sp;
        while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
        if (j >= text.Length || text[j] != '"') return sp;
        int close = text.IndexOf('"', j + 1);
        return close > j + 1 ? close + 1 : sp;   // empty quotes are not a target (ChatCommands' rule)
    }

    /// <summary>The tell aliases of <see cref="ChatCommands.Parse"/>, the only ones followed by a quoted target.</summary>
    private static bool IsTell(ReadOnlySpan<char> token)
        => token.Equals("t", StringComparison.OrdinalIgnoreCase) || token.Equals("tell", StringComparison.OrdinalIgnoreCase)
        || token.Equals("tp", StringComparison.OrdinalIgnoreCase) || token.Equals("tellplayer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Off-device proof of the markup grammar (surfaces in run-demo's cx.log).</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        static string Classes(string text)
        {
            Span<byte> cls = new byte[text.Length];
            Classify(text, cls);
            var sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++) sb.Append((char)('0' + cls[i]));
            return sb.ToString();
        }

        check.Expect(Classes("*nods* \"Hi.\" ((brb))") == "11111102222203333333", "emote, speech and OOC segments, markers included");
        check.Expect(Classes("*nods") == "11111", "unclosed emote colours to the end of the line");
        check.Expect(Classes("\"hi") == "222", "unclosed speech colours to the end of the line");
        check.Expect(Classes("((brb") == "33333", "unclosed OOC colours to the end of the line");
        check.Expect(Classes("*says \"hi\" loud*") == "1111111111111111", "a quote inside an emote does not switch");
        check.Expect(Classes("\"a *b* c\"") == "222222222", "an asterisk inside speech does not switch");
        check.Expect(Classes("((a *b* \"c\"))") == "3333333333333", "nothing switches inside OOC");
        check.Expect(Classes("/t \"Bob\" *waves*") == "0000000001111111", "tell command and quoted target stay plain, the emote colours");
        check.Expect(Classes("/s hello") == "00000000", "a plain command line is plain");
        check.Expect(Classes("/s *waves*") == "0001111111", "an emote after a command token still colours");
        check.Expect(Classes("/t hi \"x\"") == "000000222", "tell without a quoted target: the quote is speech");
        check.Expect(Classes("") == "", "empty string");

        log.Info(check.Ok ? "InputMarkup.SelfCheck PASS" : "InputMarkup.SelfCheck had FAILURES");
    }
}
