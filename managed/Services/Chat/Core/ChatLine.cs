using System;

namespace Sowx.Services.Chat.Core;

/// <summary>The in-game world clock captured when a line was ingested. <see cref="Valid"/> is false when the
/// world timer wasn't up (e.g. main menu) -- the timestamp then falls back to local. Captured at ingest, not
/// read at render, so a line keeps the game time it arrived at (matching <see cref="ChatLine"/>'s immutability).</summary>
public readonly struct GameStamp
{
    public readonly bool Valid;
    public readonly int Hour, Minute, Second;
    public GameStamp(int hour, int minute, int second) { Valid = true; Hour = hour; Minute = minute; Second = second; }
}

/// <summary>
/// One ingested chat line. A <c>class</c> (not struct): ingest is a cold network-thread path where
/// per-line allocation is fine, and reference identity lets the ring alias entries cheaply.
/// Immutable after construction -- a hard design rule so a data-bearing toggle (e.g. timestamps)
/// changes only lines ingested afterward, never rewrites the backlog.
/// </summary>
public sealed class ChatLine
{
    public uint Channel { get; }
    public string SenderName { get; }
    public uint PlayerId { get; }
    public string PortraitResref { get; }
    public ColorRun[] ColorRuns { get; }
    public DateTime Timestamp { get; }
    /// <summary>In-game world time at ingest (for the Game timestamp source); default/invalid = use local.</summary>
    public GameStamp GameTime { get; }
    public string RawText { get; }
    /// <summary>For outbound Tell echoes: the recipient's name parsed from the "[To Name]" prefix.
    /// Empty for incoming Tells and all other channels. Enables two-sided conversation tabs.</summary>
    public string TellRecipient { get; }

    public ChatLine(uint channel, string senderName, uint playerId, string portraitResref,
                    ColorRun[] colorRuns, DateTime timestamp, string rawText, GameStamp gameTime = default,
                    string tellRecipient = "")
    {
        Channel = channel;
        SenderName = senderName;
        PlayerId = playerId;
        PortraitResref = portraitResref;
        ColorRuns = colorRuns;
        Timestamp = timestamp;
        GameTime = gameTime;
        RawText = rawText;
        TellRecipient = tellRecipient;
    }
}
