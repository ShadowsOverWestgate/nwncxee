namespace Sowx.Services.Chat.Core;

/// <summary>Where a line's timestamp is read from. <see cref="Local"/> = the real wall clock (always
/// available). <see cref="Game"/> = the NWN in-game world clock (captured at ingest; falls back to local
/// when the world timer isn't up, e.g. the main menu).</summary>
public enum TsSource { Local, Game }
