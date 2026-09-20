namespace CX.Ui;

/// <summary>One settings row: its kind, action, label, help, the current value the renderer reads, and the
/// geometry <see cref="SettingsPanel.Publish"/> computes (<see cref="Top"/> / <see cref="Height"/> in list-content
/// px, <see cref="CtlW"/> the control's width, <see cref="HelpLines"/> the help wrapped to the label column).
/// Built by the consumer's layout pass; the panel hit-tests and draws it but never interprets <see cref="Act"/>
/// (that is the consumer's vocabulary).</summary>
public sealed class SetRow
{
    public SetKind Kind;
    public SetAct Act;
    public string Label = "";
    public float Top, Height, CtlW;
    // Render-read state / mutation extras:
    public bool On;          // Toggle
    public string Value = "";// Text current text
    public float Norm;       // Slider fill 0..1
    /// <summary>The value box text, formatted when <see cref="Norm"/> last moved (the draw allocates nothing while idle).</summary>
    public string ValueText = "";
    public float ValueNorm = float.NaN;
    public uint Rgba;        // Swatch colour
    public uint Bit;         // ToggleBit
    public string Key = "";  // colour key (Swatch / ColorChannel), tab index (Tab*), window index (CopyFrom)
    public int Channel = -1; // 0=R 1=G 2=B 3=A (ColorChannel)
    public float Min, Max;   // Slider range
    /// <summary>Dropdown: the choices (the consumer's static array, never copied) and the current index. The
    /// panel writes <see cref="Selected"/> on a pick before <see cref="SettingsPanel.RowPressed"/>.</summary>
    public string[]? Choices;
    public int Selected;
    /// <summary>Help drawn under the label in the small face; empty = none.</summary>
    public string Help = "";
    /// <summary>The help, word-wrapped at layout.</summary>
    public string[] HelpLines = System.Array.Empty<string>();
}
