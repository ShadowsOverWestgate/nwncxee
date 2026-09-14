namespace CX.Ui;

/// <summary>The visual kind of a settings row (how <see cref="SettingsPanel"/> draws it, and -- for
/// <see cref="SetKind.Slider"/> -- how it hit-tests: track drag vs value-box text entry).</summary>
public enum SetKind { Toggle, Cycle, Slider, Swatch, Button }
