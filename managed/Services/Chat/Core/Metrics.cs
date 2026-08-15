namespace Sowx.Services.Chat.Core;

/// <summary>
/// Global layout scale for the chatbox chrome. Every pixel metric (tab/row/title heights, widths, padding)
/// multiplies by <see cref="Scale"/>, so the whole UI grows in lockstep with the font size and the game's UI
/// scaling -- keeping the text-to-chrome ratio fixed at every size, which is what stops text clipping when the
/// font is enlarged. <c>Scale == 1</c> is the design size (<see cref="DesignFontPx"/> font, 1.0 game UI scale).
/// <see cref="ChatService"/> recomputes <see cref="Scale"/> cold whenever the config or UI scale changes; the
/// atlas is baked at the same effective px, so <c>LineHeight</c> and the chrome move together.
/// </summary>
public static class Metrics
{
    /// <summary>Font height the chrome constants were tuned at; the divisor that makes Scale==1 at defaults.</summary>
    public const float DesignFontPx = 18f;

    /// <summary>The engine's UI-scaling option (1.0 = 100%). 1.0 until the game value is resolved and applied.</summary>
    public static float GameUiScale = 1f;

    /// <summary>Combined chrome multiplier = effective font px / <see cref="DesignFontPx"/>. Read by every
    /// scaled metric property; written cold by <see cref="ChatService"/>.</summary>
    public static float Scale = 1f;
}
