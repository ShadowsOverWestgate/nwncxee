using System;

namespace Sowx.Services.Chat.Core;

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
    public string RawText { get; }

    public ChatLine(uint channel, string senderName, uint playerId, string portraitResref,
                    ColorRun[] colorRuns, DateTime timestamp, string rawText)
    {
        Channel = channel;
        SenderName = senderName;
        PlayerId = playerId;
        PortraitResref = portraitResref;
        ColorRuns = colorRuns;
        Timestamp = timestamp;
        RawText = rawText;
    }
}
