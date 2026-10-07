using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace CX.Alloy.Db;

/// <summary>CX-117: the PLT file, decoded to the two channels the shader resolves from -- and nothing else.
/// A PLT pixel is (intensity, layer); vanilla turns that into RGBA per character by looking each texel up in
/// the master palette (<c>ImageDecode</c> @0x4b6580, called from <c>ConstructImage</c> @0x48a5d0 when
/// <c>g_bGeneratePLTOnGPU</c> is clear). Alloy uploads the pairs AS a texture (RG8) and does the lookup in
/// the fragment shader, so the file is loaded once however many creatures wear it and a colour change costs
/// no pixels at all.
/// <para>Format, read off <c>CResPLT::OnResourceServiced</c> @0x1136640 and
/// <c>CAuroraLayeredTexture::LoadTexture</c> @0x1134930: a 24-byte header -- the magic (the engine compares
/// the first dword against <c>'PLT '</c> alone), the layer count at +8, a compression flag at +0x0c, width at
/// +0x10 and height at +0x14 -- then <c>width*height</c> pairs of (intensity, layer), one byte each, no
/// padding and no vertical flip. A non-zero flag means the pairs are RLE-packed
/// (<c>CResPLT::ReadRLEPLT</c> @0x1136560). The engine refuses a side past 0x2000 or a layer count past
/// 0x20. No stock file sets the flag, and the engine's own compressed branch never writes the decoded size
/// before it unpacks, so a compressed PLT decodes into a zero-length buffer there and still reports
/// success; this reader decodes it properly.</para>
/// <para>Engine-free by contract: nothing here reads engine memory or calls GL, which is what lets the
/// off-device check (<c>tools/pltcheck</c>) run it over every PLT in the base BIFs.</para></summary>
internal static unsafe class PltImage
{
    /// <summary>Bytes per PLT pixel on the GPU: R = intensity, G = layer, as RG8.</summary>
    internal const int BytesPerPixel = 2;
    internal const int HeaderBytes = 24;
    /// <summary><c>'PLT '</c>, the dword <c>OnResourceServiced</c> compares. The shipped files carry
    /// <c>"PLT V1  "</c>; the engine never looks past the first four bytes.</summary>
    internal const uint Magic = 0x20544c50;   // not-an-offset: the file's magic dword
    /// <summary>Header fields: layer count, compression flag, width, height.</summary>
    internal const int OffLayers = 8, OffCompressed = 12, OffWidth = 16, OffHeight = 20;
    /// <summary>What the engine itself refuses past (<c>LoadTexture</c>: side &lt; 0x2001, layers &lt;
    /// 0x21).</summary>
    internal const int MaxSide = 0x2000, MaxFileLayers = 0x20;   // not-an-offset: the engine's own limits, LoadTexture @0x1134930
    /// <summary>Layers Alloy's per-instance row block carries (<see cref="SceneDb.PltLayers"/>). Ten is not a
    /// budget: <c>CNWCAnimBaseParts::ReplaceTexture</c> @0x9d8c80 takes the PLT branch <b>only</b> when the
    /// count is exactly 10 and otherwise logs "Bad palette (scheme) info." and drops the call, so ten is what
    /// the game layer can ever ask for. (The engine's scheme array is fifteen ints -- <c>SetScheme</c>
    /// @0x485f90 allocates 0x3c -- and <c>ImageDecode</c>'s stack cache fifteen rows; neither is reachable
    /// past ten.) A file declaring more layers is loaded and counted, and its upper layers resolve against
    /// layer 9's row -- where the engine reads stale stack.</summary>
    internal const int Layers = SceneDb.PltLayers;

    /// <summary>PLT files seen declaring more than <see cref="Layers"/> layers, and files with a pixel that
    /// actually NAMES one past the last row the per-instance block carries. The second number is the one that
    /// matters: it is how often the clamp in <c>cx_plt.glsl</c> changes a colour. Reported by
    /// <see cref="Note"/>; <c>tools/pltcheck</c> says both are 0 across every shipped PLT.</summary>
    internal static long FilesOverLayers, PixelsOverLayers;

    /// <summary>The <c>[alloy]</c> clause, empty while the ten-layer block has cost nothing.</summary>
    internal static string Note()
        => FilesOverLayers + PixelsOverLayers == 0 ? ""
           : $"plt: {FilesOverLayers} file(s) declare more than {Layers} layers"
             + (PixelsOverLayers == 0 ? " (no pixel uses one)"
                                      : $", {PixelsOverLayers} with a pixel past layer {Layers - 1} -- CLAMPED");

    /// <summary>Bytes of an RG8 mip chain of <paramref name="levels"/> levels from
    /// <paramref name="w"/>x<paramref name="h"/>.</summary>
    internal static long ChainBytes(int w, int h, int levels)
    {
        long n = 0;
        for (int l = 0; l < levels; l++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            n += (long)w * h * BytesPerPixel;
        return n;
    }

    /// <summary>Parse the header. False names why; nothing is allocated.</summary>
    internal static bool Parse(ReadOnlySpan<byte> raw, out int w, out int h, out int layers, out bool rle, out string? why)
    {
        w = h = layers = 0; rle = false; why = null;
        if (raw.Length < HeaderBytes) { why = $"{raw.Length} bytes, shorter than the 24-byte header"; return false; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw) != Magic) { why = "the first dword is not 'PLT '"; return false; }
        layers = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[OffLayers..]);
        rle = BinaryPrimitives.ReadUInt32LittleEndian(raw[OffCompressed..]) != 0;
        w = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[OffWidth..]);
        h = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[OffHeight..]);
        if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide) { why = $"{w}x{h} is outside the engine's 1..{MaxSide}"; return false; }
        if (layers <= 0 || layers > MaxFileLayers) { why = $"{layers} layers, outside the engine's 1..{MaxFileLayers}"; return false; }
        long want = (long)w * h * BytesPerPixel;
        if (!rle && raw.Length - HeaderBytes < want)
        { why = $"{raw.Length - HeaderBytes} bytes of pairs for a {w}x{h} image, which needs {want}"; return false; }
        return true;
    }

    /// <summary>The file as an RG8 mip chain in one native buffer, levels packed base-first. Null with
    /// <paramref name="why"/> set on any refusal. Worker thread; the caller frees the buffer with
    /// <see cref="NativeMemory.Free"/>.</summary>
    internal static byte* Decode(ReadOnlySpan<byte> raw, out int w, out int h, out int levels, out string? why)
    {
        levels = 0;
        if (!Parse(raw, out w, out h, out int layers, out bool rle, out why)) return null;
        if (layers > Layers) FilesOverLayers++;
        levels = TexRegistry.Levels(w, h);
        long bytes = ChainBytes(w, h, levels);
        byte* dst = (byte*)NativeMemory.Alloc((nuint)bytes);
        long want = (long)w * h * BytesPerPixel;
        if (rle)
        {
            if (!Unrle(raw[HeaderBytes..], new Span<byte>(dst, (int)want), out why))
            { NativeMemory.Free(dst); return null; }
        }
        else raw.Slice(HeaderBytes, (int)want).CopyTo(new Span<byte>(dst, (int)want));
        int top = 0;
        for (long i = 1; i < want; i += 2) if (dst[i] > top) top = dst[i];
        if (top >= Layers) PixelsOverLayers++;
        BuildMips(dst, w, h, levels);
        return dst;
    }

    /// <summary><c>CResPLT::ReadRLEPLT</c> @0x1136560, byte for byte: a control byte whose low seven bits are
    /// <c>count - 1</c>; its top bit set means one byte repeated, clear means <c>count</c> literal bytes. The
    /// stream is the interleaved pair stream, so a run may straddle a pixel -- which is why this decodes bytes
    /// and not pixels.</summary>
    internal static bool Unrle(ReadOnlySpan<byte> src, Span<byte> dst, out string? why)
    {
        why = null;
        int s = 0, d = 0;
        while (d < dst.Length)
        {
            if (s >= src.Length) { why = $"the RLE stream ends after {d} of {dst.Length} bytes"; return false; }
            byte ctl = src[s++];
            int count = (ctl & 0x7f) + 1;
            if (count > dst.Length - d) count = dst.Length - d;
            if ((ctl & 0x80) != 0)
            {
                if (s >= src.Length) { why = "the RLE stream ends on a run byte"; return false; }
                dst.Slice(d, count).Fill(src[s++]);
            }
            else
            {
                if (s + count > src.Length) { why = $"an RLE literal run of {count} overruns the stream"; return false; }
                src.Slice(s, count).CopyTo(dst.Slice(d, count));
                s += count;
            }
            d += count;
        }
        return true;
    }

    /// <summary>The PLT mip chain, built once at load. <b>The choice:</b> intensity is AVERAGED, the layer is
    /// the MAJORITY of the four children, and the average is taken only over the children that carry the
    /// winning layer. Averaging the layer index would be meaningless -- halfway between hair and skin is
    /// metal, not a blend -- and taking the top-left child alone shimmers on a seam. Majority keeps a layer
    /// boundary crisp at every level and never invents a layer that is not there, which is why the sampler
    /// is NEAREST on a PLT and the bilinear is done in <c>cx_plt.glsl</c> over RESOLVED colours.
    /// <para>A 2-2 TIE is broken by the destination texel's parity, not by the layer index. Taking the
    /// lower index looks neutral and is not: every boundary 2x2 is a tie, so the lower-numbered layer wins
    /// half of each boundary and loses none, and a region grows one texel per level. Measured on
    /// <c>pmh0_chest004</c> that walked the metal layer from 63% of the texels at level 0 to 79% at level 5,
    /// which is a trim becoming a surface as the creature walks away. Alternating along the boundary leaves
    /// each layer its share, and the shader's four taps over resolved colours turn the alternation into the
    /// blend vanilla's composite mips would have had.</para>
    /// <para>Vanilla has no equivalent: it mips the COMPOSITE, so its lower levels blend colours across a
    /// layer boundary. Alloy's blend across such a boundary happens in the shader's four taps instead, at
    /// full resolution.</para></summary>
    internal static void BuildMips(byte* chain, int w, int h, int levels)
    {
        long srcOff = 0;
        for (int l = 1; l < levels; l++)
        {
            int sw = w, sh = h;
            w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1);
            long dstOff = srcOff + (long)sw * sh * BytesPerPixel;
            byte* s = chain + srcOff, d = chain + dstOff;
            for (int y = 0; y < h; y++)
            {
                int y0 = y * 2, y1 = Math.Min(y0 + 1, sh - 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = x * 2, x1 = Math.Min(x0 + 1, sw - 1);
                    Reduce(s, sw, x0, y0, x1, y1, (x ^ y) & 1, out byte inten, out byte layer);
                    byte* o = d + ((long)y * w + x) * BytesPerPixel;
                    o[0] = inten; o[1] = layer;
                }
            }
            srcOff = dstOff;
        }
    }

    /// <summary>One 2x2 reduction: majority layer, mean intensity over the children holding it. A tie is
    /// decided by <paramref name="parity"/> -- the destination texel's <c>(x ^ y) &amp; 1</c> -- which takes
    /// the lowest tied layer on one texel and the next one along on its neighbour, so neither layer grows
    /// at the other's expense down the chain.</summary>
    private static void Reduce(byte* s, int sw, int x0, int y0, int x1, int y1, int parity, out byte inten, out byte layer)
    {
        byte* a = s + ((long)y0 * sw + x0) * BytesPerPixel;
        byte* b = s + ((long)y0 * sw + x1) * BytesPerPixel;
        byte* c = s + ((long)y1 * sw + x0) * BytesPerPixel;
        byte* e = s + ((long)y1 * sw + x1) * BytesPerPixel;
        int best = a[1], bestN = 0;
        for (int k = 0; k < 4; k++)
        {
            int cand = (k == 0 ? a : k == 1 ? b : k == 2 ? c : e)[1];
            int n = (a[1] == cand ? 1 : 0) + (b[1] == cand ? 1 : 0) + (c[1] == cand ? 1 : 0) + (e[1] == cand ? 1 : 0);
            if (n > bestN) { best = cand; bestN = n; }
            else if (n == bestN && cand != best)
            {
                if (cand < best ? parity == 0 : parity != 0) best = cand;
            }
        }
        int sum = 0, cnt = 0;
        if (a[1] == best) { sum += a[0]; cnt++; }
        if (b[1] == best) { sum += b[0]; cnt++; }
        if (c[1] == best) { sum += c[0]; cnt++; }
        if (e[1] == best) { sum += e[0]; cnt++; }
        inten = (byte)((sum + (cnt >> 1)) / cnt);
        layer = (byte)best;
    }

    /// <summary>The shader's resolve, on the CPU: <c>colour = master[row[layer]][intensity]</c>, which is
    /// <c>ImageDecode</c>'s <c>palette[(rows[layer] &lt;&lt; 10) + intensity*4]</c> one lookup at a time. The
    /// off-device check compares this against the engine's own composite arithmetic, and the live check
    /// compares it against a readback. Returns RGBA8 packed little-endian, exactly as
    /// <c>RasterReader.DecodePlt</c> reads it.</summary>
    internal static uint Resolve(ReadOnlySpan<byte> master, ReadOnlySpan<ushort> rows, byte intensity, byte layer)
    {
        int l = layer < rows.Length ? layer : rows.Length - 1;
        int at = (rows[l] << 10) + intensity * 4;
        return (uint)at + 4 <= (uint)master.Length ? BinaryPrimitives.ReadUInt32LittleEndian(master[at..]) : 0;
    }
}
