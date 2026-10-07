using CX.Framework;

namespace CX.Engine;

public readonly unsafe partial struct CAurString
{
    /// <summary>The displayed text, its wrapped lines rejoined (<c>StringGob::WrapLines</c> @0x494d80 keeps the break
    /// space on the first piece, so plain concatenation restores what SetText was given), trimmed. Reads through the
    /// vtable, so a DB-mode facade answers too; capped at <paramref name="maxLines"/> lines of 4096 bytes.</summary>
    public string ReadText(int maxLines)
    {
        if (IsNull) return "";
        int n = Math.Min(GetTextLineCount(), maxLines);
        var sb = new System.Text.StringBuilder();
        for (int k = 0; k < n; k++)
        {
            byte* p = (byte*)GetTextLine(k);
            for (int j = 0; p != null && j < 4096 && p[j] != 0; j++) sb.Append((char)p[j]);
        }
        return sb.ToString().Trim();
    }
}
