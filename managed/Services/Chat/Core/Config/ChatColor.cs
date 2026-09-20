namespace CX.Services.Chat.Core;

/// <summary>Stable keys for the user-themeable chrome colours. Values live in the config as RGBA8
/// (<c>0xRRGGBBAA</c>) overrides; the defaults + the mapping onto individual widgets live in
/// <see cref="Render.WindowRenderer"/> (the look is a render concern). Only these are surfaced in the
/// settings UI; the renderer routes every chrome colour through the palette so more can be exposed later
/// without touching the draw code.</summary>
public static class ChatColor
{
    public const string Background = "bg";
    public const string Background2 = "bg2";
    public const string Header = "header";
    public const string Accent = "accent";
    public const string Input = "input";
    public const string Selection = "sel";
    public const string Text = "text";
    /// <summary>The window frame: outline + shadow, the bar divider, the input strip's edge.</summary>
    public const string Frame = "frame";

    /// <summary>The colours surfaced as picker rows, in display order. <c>Alpha</c> = the colour's transparency
    /// is honoured by the renderer, so the picker offers an A channel; false for <see cref="Text"/>, which draws
    /// opaque (ponytail: plumbing alpha through the glyph draw is the ceiling), so a Text-A slider would be dead.</summary>
    public static readonly (string Key, string Label, bool Alpha)[] Editable =
    {
        (Background, "Background", true), (Frame, "Outline", true), (Header, "Header", true), (Accent, "Accent", true),
        (Input, "Input bar", true), (Selection, "Selection", true), (Text, "Text", false),
    };
}
