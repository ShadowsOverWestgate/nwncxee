namespace CX.Ui;

/// <summary>
/// Global layout scale for EVERY window the UI host draws -- chat, dialog, quickbar, settings, FPS overlay.
/// Every pixel metric multiplies by <see cref="Scale"/>, so chrome keeps a fixed ratio to the text at every
/// font size and game UI scale, which is what stops text clipping when the font is enlarged. <c>Scale == 1</c>
/// is the design size (<see cref="DesignFontPx"/> font, 1.0 game UI scale).
/// </summary>
public static class Metrics
{
    /// <summary>Font height the chrome constants were tuned at; the divisor that makes Scale==1 at defaults.</summary>
    public const float DesignFontPx = 18f;

    /// <summary>The engine's UI-scaling option (1.0 = 100%), owned by <c>UiHostService</c> and synced from the
    /// engine each frame. 1.0 until the game value is resolved and applied.</summary>
    public static float GameUiScale = 1f;

    /// <summary>Combined chrome multiplier = effective font px / <see cref="DesignFontPx"/>. Read by every
    /// scaled metric property; recomputed cold when the font size or <see cref="GameUiScale"/> changes, never
    /// on a frame. The glyph atlas bakes at the same effective px, so line height and chrome move together.</summary>
    public static float Scale = 1f;
}
