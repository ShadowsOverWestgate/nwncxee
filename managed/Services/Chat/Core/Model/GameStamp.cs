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
