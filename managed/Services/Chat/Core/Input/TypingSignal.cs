using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>The chat-bar focus/unfocus signal vanilla sends the server, as pure logic: a single latch
/// that decides whether an open/close of our chat entry is a state CHANGE worth putting on the wire.
/// <para>Vanilla emits <c>CGuiInGame::NotifyServerAboutGuiEvent(1|2, chatMode, OBJECT_INVALID, {0,0,0})</c>
/// from five <c>CGuiInGameChatPrompt</c> sites; servers read it as ChatBarFocus/ChatBarUnFocus (a "player
/// is typing" indicator). Our window skip-originals every one of those sites, so we re-emit it here.</para>
/// <para>Send-nothing rules live in the engine call, not here: the native
/// <c>NotifyServerAboutGuiEvent</c> already drops the packet when there is no player creature or no
/// message object (i.e. not in a module).</para></summary>
public static class TypingSignal
{
    /// <summary>ChatBarFocus -- the entry took the keyboard.</summary>
    public const int EventFocus = 1;

    /// <summary>ChatBarUnFocus -- the entry gave the keyboard back (sent, cancelled, or lost focus).</summary>
    public const int EventUnfocus = 2;

    /// <summary>OBJECT_INVALID: vanilla passes it as the event's object id at every chat-bar site.</summary>
    public const uint ObjectInvalid = 0x7f000000;

    // volatile to match ChatService.IsTyping: Open/CloseChat run on the input thread while the session
    // edge (Reset) can fire from the frame thread's gate read.
    private static volatile bool _focused;

    /// <summary>True while the latch believes the server has been told we are typing.</summary>
    public static bool Focused => _focused;

    /// <summary>The entry opened. Returns true exactly once per open -- a repeat while already focused
    /// is suppressed.</summary>
    public static bool Focus()
    {
        if (_focused) return false;
        _focused = true;
        return true;
    }

    /// <summary>The entry closed. Returns true exactly once per close -- an unfocus while not focused
    /// is suppressed.</summary>
    public static bool Unfocus()
    {
        if (!_focused) return false;
        _focused = false;
        return true;
    }

    /// <summary>Drop the latch WITHOUT deciding to send. For the session edge (module unload /
    /// disconnect), where the connection that owned the "typing" state is already gone.</summary>
    public static void Reset() => _focused = false;

    /// <summary>Vanilla chat-prompt mode for a channel, as the event's integer payload. Read off the
    /// <c>ParseChatString</c> dispatch on <c>prompt+0x940</c>: 1 Whisper, 2 Shout, 3 Party, 4 DM, and
    /// anything else Talk. Tell has no mode of its own -- vanilla reaches it by typing "/tell" in Talk
    /// mode (<c>PrepChatPromptForTell</c> only prefills the text), so it reports Talk.</summary>
    public static int ModeOf(Channel ch) => ch switch
    {
        Channel.Whisper => 1,
        Channel.Shout   => 2,
        Channel.Party   => 3,
        Channel.DM      => 4,
        _               => 0,
    };

    /// <summary>Off-device proof of the latch's state machine and the channel-to-mode map.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        Reset();
        check.Expect(!Focused, "starts unfocused");
        check.Expect(Unfocus() == false, "unfocus while not typing sends nothing");
        check.Expect(Focus(), "first focus sends");
        check.Expect(Focused, "latched after focus");
        check.Expect(Focus() == false, "repeat focus sends nothing");
        check.Expect(Unfocus(), "first unfocus sends");
        check.Expect(!Focused, "unlatched after unfocus");
        check.Expect(Unfocus() == false, "repeat unfocus sends nothing");
        check.Expect(Focus(), "re-open after a close sends again");

        // Reset is the session edge: it drops the latch silently, and the next open still sends.
        Reset();
        check.Expect(!Focused, "reset drops the latch");
        check.Expect(Focus(), "focus after reset sends");
        Reset();

        check.Expect(ModeOf(Channel.Talk) == 0 && ModeOf(Channel.Tell) == 0, "talk and tell both report mode 0");
        check.Expect(ModeOf(Channel.Whisper) == 1, "whisper is mode 1");
        check.Expect(ModeOf(Channel.Shout) == 2, "shout is mode 2");
        check.Expect(ModeOf(Channel.Party) == 3, "party is mode 3");
        check.Expect(ModeOf(Channel.DM) == 4, "dm is mode 4");

        log.Info(check.Ok ? "TypingSignal.SelfCheck OK" : "TypingSignal.SelfCheck had failures");
    }
}
