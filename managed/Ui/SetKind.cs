namespace CX.Ui;

/// <summary>The visual kind of a settings row (how <see cref="SettingsPanel"/> draws it, and -- for
/// <see cref="SetKind.Slider"/> -- how it hit-tests: track drag vs value-box text entry). <see cref="Dropdown"/>
/// is the enumerated choice (a press opens the list; #587); <see cref="Text"/> is a value box the consumer may
/// start an inline edit on.</summary>
public enum SetKind { Toggle, Slider, Swatch, Button, Dropdown, Text }
