namespace Sowx.Services.Chat.Core;

/// <summary>One laid-out settings row: its kind, action, label, computed geometry, and the current value the
/// renderer reads. Built by <see cref="SettingsWindow.Layout"/> from the live window+config each rebuild.</summary>
public sealed class SetRow
{
    public SetKind Kind;
    public SetAct Act;
    public string Label = "";
    public float Top, Height;
    // Render-read state / mutation extras:
    public bool On;          // Toggle
    public string Value = "";// Cycle current text
    public float Norm;       // Slider fill 0..1
    public uint Rgba;        // Swatch colour
    public uint Bit;         // ToggleBit
    public string Key = "";  // colour key (Swatch / ColorChannel)
    public int Channel = -1; // 0=R 1=G 2=B 3=A (ColorChannel)
    public float Min, Max;   // Slider range
}
