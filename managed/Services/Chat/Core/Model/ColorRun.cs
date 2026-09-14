namespace CX.Services.Chat.Core;

/// <summary>A span of plain text with its active Aurora color (channels normalised to 0..1).</summary>
public readonly struct ColorRun
{
    public readonly string Text;
    public readonly float R, G, B;
    public ColorRun(string text, float r, float g, float b) { Text = text; R = r; G = g; B = b; }
}
