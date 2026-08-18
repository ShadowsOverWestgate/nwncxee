namespace Sowx.Services.Chat.Core;

/// <summary>Background fill style for a chat window. Solid = single colour; gradient and stripe modes
/// blend or alternate the primary (<see cref="ChatColor.Background"/>) and secondary
/// (<see cref="ChatColor.Background2"/>) colours.</summary>
public enum BgMode { Solid, GradientV, GradientH, GradientD, StripesH, StripesV, GradientBox, GradientCircle, Conic, Conic4 }
