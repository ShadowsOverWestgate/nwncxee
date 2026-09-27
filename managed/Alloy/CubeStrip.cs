using System;

namespace CX.Alloy;

/// <summary>CX-52 step 4: a cube DDS's six faces packed into the vertical strip the engine's cube upload cuts.
/// <c>GLRender::CreateCubeTexture</c> @0x4caab0 (Windows RVA 0xBE930, inlined, same tables) uploads GL face
/// <c>i</c> from strip slot <see cref="Slot"/>[i] turned by <c>ImageQuarterRotated</c> @0x485e00 through
/// <see cref="Turns"/>[i] quarters; <see cref="Place"/> writes each face pre-turned the other way, so the
/// engine's cut hands GL exactly the file's face. DDS face order is GL's (+X, -X, +Y, -Y, +Z, -Z). Pure:
/// <c>tools/rastercheck</c> compiles it alone.</summary>
internal static class CubeStrip
{
    /// <summary>The strip slot the engine reads GL face i from.</summary>
    internal static ReadOnlySpan<byte> Slot => [1, 0, 2, 3, 4, 5];
    /// <summary>The quarter turns the engine applies to GL face i.</summary>
    internal static ReadOnlySpan<byte> Turns => [1, 3, 0, 2, 2, 0];

    /// <summary><c>ImageQuarterRotated</c> for a square <paramref name="n"/> x <paramref name="n"/> image: source
    /// texel (x, y) lands at row/column (y, x), (x, n-1-y), (n-1-y, n-1-x), (n-1-x, y) for 0-3 turns.</summary>
    internal static void Rotate(ReadOnlySpan<byte> src, int n, int px, int turns, Span<byte> dst)
    {
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                (int row, int col) = (turns & 3) switch { 0 => (y, x), 1 => (x, n - 1 - y), 2 => (n - 1 - y, n - 1 - x), _ => (n - 1 - x, y) };
                src.Slice((y * n + x) * px, px).CopyTo(dst.Slice((row * n + col) * px, px));
            }
    }

    /// <summary>GL face <paramref name="face"/> (<paramref name="n"/> square, <paramref name="px"/> bytes per
    /// texel) into its slot of <paramref name="strip"/> (n wide, 6n tall).</summary>
    internal static void Place(ReadOnlySpan<byte> face, int n, int px, int face_, Span<byte> strip)
        => Rotate(face, n, px, 4 - Turns[face_], strip.Slice(Slot[face_] * n * n * px, n * n * px));

    /// <summary>Six random faces through <see cref="Place"/> and back through the engine's cut: each must come
    /// out as it went in.</summary>
    internal static bool SelfCheck()
    {
        const int n = 5, px = 3, face = n * n * px;
        var faces = new byte[6 * face];
        new Random(52).NextBytes(faces);
        var strip = new byte[6 * face];
        for (int i = 0; i < 6; i++) Place(faces.AsSpan(i * face, face), n, px, i, strip);
        var cut = new byte[face];
        for (int i = 0; i < 6; i++)
        {
            Rotate(strip.AsSpan(Slot[i] * face, face), n, px, Turns[i], cut);
            if (!cut.AsSpan().SequenceEqual(faces.AsSpan(i * face, face))) return false;
        }
        return true;
    }
}
