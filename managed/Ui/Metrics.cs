namespace CX.Ui;

/// <summary>
/// Global layout scale for EVERY window the UI host draws -- chat, dialog, quickbar, the settings panel and
/// the FPS overlay. (It began as chatbox chrome and the docs said so two refactors after it stopped being
/// true.) Every pixel metric multiplies by <see cref="Scale"/>, so the whole UI grows in lockstep with the
/// font size and the game's UI scaling, keeping the text-to-chrome ratio fixed at every size -- which is what
/// stops text clipping when the font is enlarged. <c>Scale == 1</c> is the design size
/// (<see cref="DesignFontPx"/> font, 1.0 game UI scale).
/// <para><c>UiHostService</c> owns <see cref="GameUiScale"/>, syncing it from the engine each frame (#62).
/// <see cref="Scale"/> is recomputed cold whenever the font size or that scale changes; the glyph atlas is
/// baked at the same effective px, so line height and chrome move together.</para>
/// </summary>
public static class Metrics
{
    /// <summary>Font height the chrome constants were tuned at; the divisor that makes Scale==1 at defaults.</summary>
    public const float DesignFontPx = 18f;

    /// <summary>The engine's UI-scaling option (1.0 = 100%). 1.0 until the game value is resolved and applied.</summary>
    public static float GameUiScale = 1f;

    /// <summary>Combined chrome multiplier = effective font px / <see cref="DesignFontPx"/>. Read by every
    /// scaled metric property; written cold, never on a frame.</summary>
    public static float Scale = 1f;
}
