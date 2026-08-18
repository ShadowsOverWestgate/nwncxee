namespace Sowx.Services.Chat.Core;

/// <summary>What happens when a Tell arrives from a player without an existing pinned tab (#46).</summary>
public enum TellAutoOpen { Off, NewTab, NewWindow }
