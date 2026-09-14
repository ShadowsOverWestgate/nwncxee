namespace CX.Services.Chat.Core;

/// <summary>GUIINGAME_MESSAGE_* flag values read from the binary's rodata. The channel value from the
/// ingest hook IS the flag, and both <see cref="Tab.Matches"/> and <see cref="ChatStore"/> filter via
/// direct <c>mask &amp; channel</c>. "Combat" maps to TO_HIT; CCMESSAGE (0x80) is feedback text.</summary>
public static class FilterChannel
{
    public static readonly (uint Bit, string Label)[] Editable =
    {
        (0x001, "Talk"),
        (0x002, "Tell"),
        (0x004, "Shout"),
        (0x008, "Whisper"),
        (0x010, "To-Hit"),
        (0x020, "Server"),
        (0x040, "Party"),
        (0x080, "Feedback"),
        (0x400, "DM"),
    };
}
