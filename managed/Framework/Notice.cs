using System;

namespace CX.Framework;

/// <summary>
/// #216: a line the PLAYER has to see, not just one the log records. The extender's own log is a file nobody
/// reads mid-session, and an Alloy dropout is exactly the event a bug report has to name -- so the reason goes
/// on screen as well as into the log.
/// </summary>
/// <remarks>
/// ponytail: no queue and no retry -- a notice raised before the sink exists (pre-init, or all session with
/// the CX chatbox off) is logged and dropped. The ceiling is a notice raised during startup, which no player
/// sees; the upgrade is a single-slot pending line replayed on the first <see cref="Sink"/> assignment, and
/// it is worth building only once something raises one that early. Callers pass constant text or build it
/// cold: this is called on transitions, never per frame.
/// </remarks>
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
