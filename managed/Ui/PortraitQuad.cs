namespace Sowx.Ui;

/// <summary>A speaker-portrait draw request: a pixel rect, the resref to load it from, and the line's
/// message time (so the portrait fades with its text). Emitted cold by <c>WindowRenderer</c>
/// (strings + floats only, no native refs); the GL side (<see cref="TextRenderer"/>) resolves the resref
/// to an engine-resident texture and draws it in a second pass (#26), applying the fade+slide (map #13).</summary>
public readonly record struct PortraitQuad(float X, float Y, float W, float H, string Resref, System.DateTime Ts);
