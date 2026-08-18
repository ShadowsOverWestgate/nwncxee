namespace Sowx.Services.Chat.Core;

/// <summary>A clickable portrait rect in screen pixels, carrying the line's PlayerId for tell targeting.</summary>
public readonly record struct PortraitHit(float X, float Y, float W, float H, uint PlayerId);
