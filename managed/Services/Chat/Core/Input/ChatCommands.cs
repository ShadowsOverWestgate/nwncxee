using Sowx.Framework;

namespace Sowx.Services.Chat.Core;

/// <summary>
/// Parses a leading channel slash-command off a submitted line (#25). Pure -- text in, (channel, rest)
/// out, no native refs -- so it self-checks off-device. The token set mirrors vanilla's ParseChatString
/// (research §4.2) MINUS <c>/dm</c>, which servers claim for their own DM tooling (SoW opens a panel with
/// it); consuming it as a channel switch would swallow the command before the server ever saw it.
/// An unrecognized slash is left intact and sent as-is on the current channel.
/// </summary>
public static class ChatCommands
{
    /// <summary>Map a leading <c>/token</c> to a send channel, an optional quoted tell target, and the
    /// remaining message. <c>/t "Alice" hi</c> → <c>(Tell, "Alice", "hi")</c>;
    /// <c>/s hi</c> → <c>(Shout, null, "hi")</c>; plain text → <c>(null, null, original)</c>.</summary>
    // ponytail: quoted target only (matches the portrait-click prefill); unquoted target is the ceiling
    public static (Channel? channel, string? tellTarget, string rest) Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/') return (null, null, text);
        int sp = text.IndexOfAny(Whitespace);
        string token = (sp < 0 ? text.Substring(1) : text.Substring(1, sp - 1)).ToLowerInvariant();
        Channel? ch = token switch
        {
            "t" or "tell" or "tp" or "tellplayer" => Channel.Tell,
            "s" or "shout"                        => Channel.Shout,
            "w" or "whisper"                      => Channel.Whisper,
            "p" or "party"                        => Channel.Party,
            "tk" or "talk"                        => Channel.Talk,
            _                                     => null,
        };
        if (ch is null) return (null, null, text);
        string rest = sp < 0 ? "" : text.Substring(sp + 1).TrimStart();
        if (ch == Channel.Tell && rest.Length > 1 && rest[0] == '"')
        {
            int close = rest.IndexOf('"', 1);
            if (close > 1)
            {
                string target = rest.Substring(1, close - 1);
                string msg = close + 1 < rest.Length ? rest.Substring(close + 1).TrimStart() : "";
                return (ch, target, msg);
            }
        }
        return (ch, null, rest);
    }

    private static readonly char[] Whitespace = { ' ', '\t' };

    /// <summary>Off-device proof of the command grammar (surfaces in run-demo's sowx log).</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"ChatCommands.SelfCheck FAIL: {w}"); } }

        E(Parse("/s hi") == (Channel.Shout, null, "hi"), "shout with message");
        E(Parse("/s") == (Channel.Shout, null, ""), "bare switch, no message");
        E(Parse("/W  Hello") == (Channel.Whisper, null, "Hello"), "case-insensitive + trimmed rest");
        E(Parse("/party lets go") == (Channel.Party, null, "lets go"), "long alias");
        E(Parse("/dm secret") == ((Channel?)null, null, "/dm secret"), "dm is NOT a channel switch: it passes through whole");
        E(Parse("hello") == (null, null, "hello"), "plain text untouched");
        E(Parse("/unknown x") == (null, null, "/unknown x"), "unknown slash sent verbatim");
        E(Parse("") == (null, null, ""), "empty");

        // Tell with quoted target
        E(Parse("/t \"Alice\" hi") == (Channel.Tell, "Alice", "hi"), "tell with quoted target");
        E(Parse("/tp \"Bob\"") == (Channel.Tell, "Bob", ""), "tell target, no message");
        E(Parse("/tell \"A B\" hey") == (Channel.Tell, "A B", "hey"), "tell target with space");
        E(Parse("/t hi") == (Channel.Tell, null, "hi"), "tell without quotes = no target");
        E(Parse("/t \"\" hi") == (Channel.Tell, null, "\"\" hi"), "empty quotes = no target (close at 1, not >1)");

        log.Info(ok ? "ChatCommands.SelfCheck PASS" : "ChatCommands.SelfCheck had FAILURES");
    }
}
