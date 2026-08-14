using Sowx.Framework;

namespace Sowx.Services.Chat.Core;

/// <summary>
/// Parses a leading channel slash-command off a submitted line (#25). Pure -- text in, (channel, rest)
/// out, no native refs -- so it self-checks off-device. The token set mirrors vanilla's ParseChatString
/// (research §4.2). An unrecognized slash is left intact and sent as-is on the current channel.
/// </summary>
public static class ChatCommands
{
    /// <summary>Map a leading <c>/token</c> to a send channel and the remaining message. <c>"/s hi"</c> -&gt;
    /// <c>(Shout, "hi")</c>; <c>"/s"</c> -&gt; <c>(Shout, "")</c>; <c>"hi"</c> or an unknown <c>/x</c> -&gt;
    /// <c>(null, &lt;original&gt;)</c>.</summary>
    public static (Channel? channel, string rest) Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/') return (null, text);
        int sp = text.IndexOfAny(Whitespace);
        string token = (sp < 0 ? text.Substring(1) : text.Substring(1, sp - 1)).ToLowerInvariant();
        Channel? ch = token switch
        {
            "t" or "tell" or "tp" or "tellplayer" => Channel.Tell,
            "s" or "shout"                        => Channel.Shout,
            "w" or "whisper"                      => Channel.Whisper,
            "p" or "party"                        => Channel.Party,
            "dm"                                  => Channel.DM,
            "tk" or "talk"                        => Channel.Talk,
            _                                     => null,
        };
        if (ch is null) return (null, text);                       // unknown slash: send verbatim
        string rest = sp < 0 ? "" : text.Substring(sp + 1).TrimStart();
        return (ch, rest);
    }

    private static readonly char[] Whitespace = { ' ', '\t' };

    /// <summary>Off-device proof of the command grammar (surfaces in run-demo's sowx log).</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"ChatCommands.SelfCheck FAIL: {w}"); } }

        E(Parse("/s hi") == (Channel.Shout, "hi"), "shout with message");
        E(Parse("/s") == (Channel.Shout, ""), "bare switch, no message");
        E(Parse("/W  Hello") == (Channel.Whisper, "Hello"), "case-insensitive + trimmed rest");
        E(Parse("/party lets go") == (Channel.Party, "lets go"), "long alias");
        E(Parse("/dm secret") == (Channel.DM, "secret"), "dm");
        E(Parse("hello") == (null, "hello"), "plain text untouched");
        E(Parse("/unknown x") == (null, "/unknown x"), "unknown slash sent verbatim");
        E(Parse("") == (null, ""), "empty");

        log.Info(ok ? "ChatCommands.SelfCheck PASS" : "ChatCommands.SelfCheck had FAILURES");
    }
}
