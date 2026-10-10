using System;

namespace CX.Alloy;

/// <summary>A cube DDS's six faces packed into the vertical strip the engine's cube upload cuts.
/// <c>GLRender::CreateCubeTexture</c> uploads GL face <c>i</c> from strip slot <see cref="Slot"/>[i] turned by
/// <see cref="Turns"/>[i] quarters, so <see cref="Place"/> writes each face pre-turned the other way and the cut
/// hands GL exactly the file's face. DDS face order is GL's. Pure: <c>tools/rastercheck</c> compiles it alone.</summary>
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

    /// <summary>Oracle: two golden fixtures written out by hand from what a quarter turn does to a labelled
    /// grid and from the engine's own <see cref="Slot"/>/<see cref="Turns"/> tables -- a round trip through
    /// <see cref="Place"/> and back through the cut cancels a wrong table entry, so it proves nothing on its
    /// own and is only the last of the three steps here.</summary>
    internal static bool SelfCheck()
    {
        // One quarter turn is clockwise -- the left column becomes the top row -- two is a half turn, three
        // is counter-clockwise. Getting the sense backwards swaps +X with -X on every cube in the game.
        ReadOnlySpan<byte> grid = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        ReadOnlySpan<byte> cw = [7, 4, 1, 8, 5, 2, 9, 6, 3];
        ReadOnlySpan<byte> half = [9, 8, 7, 6, 5, 4, 3, 2, 1];
        ReadOnlySpan<byte> ccw = [3, 6, 9, 2, 5, 8, 1, 4, 7];
        var turned = new byte[9];
        Rotate(grid, 3, 1, 0, turned);
        if (!turned.AsSpan().SequenceEqual(grid)) return false;
        Rotate(grid, 3, 1, 1, turned);
        if (!turned.AsSpan().SequenceEqual(cw)) return false;
        Rotate(grid, 3, 1, 2, turned);
        if (!turned.AsSpan().SequenceEqual(half)) return false;
        Rotate(grid, 3, 1, 3, turned);
        if (!turned.AsSpan().SequenceEqual(ccw)) return false;

        // The golden strip: six 2x2 faces labelled 1..24 in DDS/GL face order, placed where the engine's cut
        // will look for them (slot 1, 0, 2, 3, 4, 5) pre-turned against the quarters it will apply (1, 3, 0,
        // 2, 2, 0). Written out by hand from those two engine tables, so a wrong entry in either shows here.
        ReadOnlySpan<byte> golden =
        [
            7, 5, 8, 6,          // slot 0: face 1 (-X), turned clockwise against the engine's three quarters
            2, 4, 1, 3,          // slot 1: face 0 (+X), turned counter-clockwise against its one
            9, 10, 11, 12,       // slot 2: face 2 (+Y), untouched
            16, 15, 14, 13,      // slot 3: face 3 (-Y), half turned
            20, 19, 18, 17,      // slot 4: face 4 (+Z), half turned
            21, 22, 23, 24,      // slot 5: face 5 (-Z), untouched
        ];
        var labelled = new byte[24];
        for (int i = 0; i < 24; i++) labelled[i] = (byte)(i + 1);
        var strip2 = new byte[24];
        for (int i = 0; i < 6; i++) Place(labelled.AsSpan(i * 4, 4), 2, 1, i, strip2);
        if (!strip2.AsSpan().SequenceEqual(golden)) return false;

        // ...and the round trip, which only adds that Place and the cut are each other's inverse at a size
        // and a texel width the golden does not cover.
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
