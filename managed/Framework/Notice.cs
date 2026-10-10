using System;

namespace CX.Framework;

/// <summary>
/// A line the PLAYER has to see, not just one the log records: the extender's log is a file nobody reads
/// mid-session, and an Alloy dropout is exactly the event a bug report has to name. Called on transitions, never
/// per frame. ponytail: no queue and no retry, so a notice raised before the sink exists is logged and dropped;
/// the upgrade is one pending slot replayed on the first <see cref="Sink"/> assignment, if that ever matters.
/// </summary>
public static class Notice
{
    /// <summary>Where an on-screen notice goes. Set once, by the service that owns a text surface.</summary>
    public static Action<string>? Sink;

    /// <summary>Log it and show it. Safe before any sink exists.</summary>
    public static void Show(ExtenderLog? log, string text)
    {
        log?.Info($"[notice] {text}");
        Sink?.Invoke(text);
    }
}
