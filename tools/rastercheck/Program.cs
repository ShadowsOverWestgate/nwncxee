using System;
using System.IO;
using CX.Alloy;

/// <summary>rastercheck in.tga|in.dds out.raw: what the game decodes -- RasterReader's verdict and pixels for a TGA
/// (file row order, RGB(A)/L8), or level 0 of a BC1/BC2/BC3/BC7 DDS as RasterReader unpacks one the engine forces
/// uncompressed. Prints "verdict w h px type why"; a DDS reports type "dds".</summary>
static class Program
{
    static int Main(string[] a)
    {
        byte[] raw = File.ReadAllBytes(a[0]);
        if (a[0].EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            if (!DdsReader.Parse(raw, out DdsReader.Info i, out string? bad) || !BcnDecode.Decodes(i.Format))
            {
                Console.WriteLine($"Unchecked {i.W} {i.H} {i.Channels} dds {bad ?? $"{i.Format}: only BC1/BC2/BC3/BC7 are unpacked"}");
                return 0;
            }
            int px = BcnDecode.PixelBytes(i.Format);
            var rgb = new byte[i.W * i.H * px];
            BcnDecode.Unpack(raw.AsSpan(i.DataOffset, DdsReader.LevelSize(i.W, i.H, i.Channels)), i.W, i.H, i.Format, rgb);
            File.WriteAllBytes(a[1], rgb);
            Console.WriteLine($"Pixels {i.W} {i.H} {px} dds {(i.Container == DdsReader.ContainerLegacy ? "legacy" : "microsoft")} {i.Format}{(i.Faces == 6 ? " cube" : "")}{(i.EngineReads ? "" : " beyond")}");
            return 0;
        }
        var v = RasterReader.ParseTga(raw, out RasterReader.Tga t, out string? why);
        if (v == RasterReader.Verdict.Pixels)
        {
            var px = new byte[t.Bytes];
            RasterReader.DecodeTga(raw, t, px);
            File.WriteAllBytes(a[1], px);
        }
        Console.WriteLine($"{v} {t.W} {t.H} {t.Px} {t.Type} {why}");
        return 0;
    }
}
