namespace CX.Alloy.Db;

/// <summary>The four slots <c>StringGob</c> declares as NEW virtuals rather than <c>CAurString</c> overrides,
/// so they sit in the primary table (Linux 104-107, Windows 93-96) and not in the <c>CAurString</c> one. They
/// are plain reads of state the setters above already keep; added by the CX-120 seam-obj stream when a live
/// DB-mode run reached one, and the text stream is welcome to fold them into <c>TextSlots.cs</c>.</summary>
public static partial class TextSlots
{
    /// <summary><c>StringGob::GetAlignment()</c>.</summary>
    public static float GetAlignment(DbId id) => TextObjects.Of(id)?.AlignH ?? 0f;

    /// <summary><c>StringGob::GetAlignmentVertical()</c>.</summary>
    public static float GetAlignmentVertical(DbId id) => TextObjects.Of(id)?.AlignV ?? 0f;

    /// <summary><c>StringGob::GetFontName()</c>: the token the game last asked for, or
    /// <see cref="NoFont"/> when it has none -- which is the engine's own answer for a fontless string.</summary>
    public static string GetFontName(DbId id) => TextObjects.Of(id)?.Font?.Name ?? NoFont;

    /// <summary>Colour <paramref name="index"/> of the document, the read half of
    /// <see cref="SetIndexedColor"/>. White when the index is past the table, as an unset slot reads.</summary>
    public static void GetIndexedColor(DbId id, int index, out float r, out float g, out float b, out float a)
    {
        r = g = b = a = 1f;
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return;
        int at = index * 4;
        if (at < 0 || at + 3 >= d.Colours.Length) return;
        r = d.Colours[at];
        g = d.Colours[at + 1];
        b = d.Colours[at + 2];
        a = d.Colours[at + 3];
    }
}
