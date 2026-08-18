namespace Sowx.Ui;

/// <summary>A rectangular sub-area (absolute screen px) of a <see cref="Window"/> that requests a
/// <see cref="CursorKind"/> when the pointer is over it -- links, edit fields, and other content affordances.
/// Built-in chrome regions (resize grip, drag handle) are derived from the rect + capability flags instead.</summary>
public readonly struct HitRegion
{
    public readonly float X, Y, W, H;
    public readonly CursorKind Cursor;

    public HitRegion(float x, float y, float w, float h, CursorKind cursor)
    {
        X = x; Y = y; W = w; H = h; Cursor = cursor;
    }

    public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;
}
