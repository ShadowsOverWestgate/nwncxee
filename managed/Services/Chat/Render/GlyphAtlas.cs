using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services.Chat.Render;

/// <summary>One baked glyph: atlas tex-coords, pixel size, bearings, and scaled advance.</summary>
public struct Glyph
{
    public float U0, V0, U1, V1;
    public int W, H, XOff, YOff;   // XOff/YOff = box offset from the pen baseline (YOff negative = above)
    public float Advance;
}

/// <summary>
/// A single-channel (alpha) glyph atlas baked once at init (cold) with the client's own
/// stb_truetype exports. Owns the CPU bitmap + per-codepoint <see cref="Glyph"/> table; the GL
/// upload lives in <see cref="TextRenderer"/> because no GL context exists at init time.
/// </summary>
public sealed unsafe class GlyphAtlas
{
    /// <summary>ponytail: opaque stbtt_fontinfo is ~180B; over-allocate. Ceiling: bind the real struct.</summary>
    private const int FontInfoBytes = 1024;
    private const int Pad = 1;   // 1px gutter so GL_NEAREST sampling never bleeds a neighbour

    public byte[] Pixels = Array.Empty<byte>();
    public int Width, Height;
    public float LineHeight, Ascent;

    /// <summary>Centre texcoord of a single fully-opaque texel (bottom-right corner), so chrome rects
    /// can sample the same atlas as text and get <c>coverage==1</c> -- one shader, one draw call.</summary>
    public float SolidU, SolidV;

    private readonly Dictionary<int, Glyph> _glyphs = new();
    private float _spaceAdvance;

    public bool TryGet(int cp, out Glyph g) => _glyphs.TryGetValue(cp, out g);

    /// <summary>Scaled pixel advance for a rune; falls back to the space advance for uncovered codepoints.</summary>
    public float AdvanceOf(System.Text.Rune r) => _glyphs.TryGetValue(r.Value, out Glyph g) ? g.Advance : _spaceAdvance;

    // stbtt exports (extern "C"); resolved once. Any Zero -> Bake returns null (e.g. headless /usr/bin/true).
    private static readonly delegate* unmanaged<byte*, byte*, int, int> _initFont =
        (delegate* unmanaged<byte*, byte*, int, int>)Native.Sym(SymbolMap.StbttInitFont);
    private static readonly delegate* unmanaged<byte*, float, float> _scaleForPx =
        (delegate* unmanaged<byte*, float, float>)Native.Sym(SymbolMap.StbttScaleForPixelHeight);
    private static readonly delegate* unmanaged<byte*, int*, int*, int*, void> _vmetrics =
        (delegate* unmanaged<byte*, int*, int*, int*, void>)Native.Sym(SymbolMap.StbttGetFontVMetrics);
    private static readonly delegate* unmanaged<byte*, int, int*, int*, void> _hmetrics =
        (delegate* unmanaged<byte*, int, int*, int*, void>)Native.Sym(SymbolMap.StbttGetCodepointHMetrics);
    private static readonly delegate* unmanaged<byte*, int, float, float, int*, int*, int*, int*, void> _bitmapBox =
        (delegate* unmanaged<byte*, int, float, float, int*, int*, int*, int*, void>)Native.Sym(SymbolMap.StbttGetCodepointBitmapBox);
    private static readonly delegate* unmanaged<byte*, byte*, int, int, int, float, float, int, void> _makeBitmap =
        (delegate* unmanaged<byte*, byte*, int, int, int, float, float, int, void>)Native.Sym(SymbolMap.StbttMakeCodepointBitmap);

    /// <summary>
    /// Bake the given codepoint ranges [lo,hi] from <paramref name="font"/> TTF bytes at
    /// <paramref name="pixelHeight"/> into a <paramref name="atlasSize"/>² atlas. Cold; returns
    /// null if stbtt isn't linked in (headless) or the atlas overflows.
    /// </summary>
    public static GlyphAtlas? Bake(byte[] font, float pixelHeight, (int lo, int hi)[] ranges, int atlasSize, ExtenderLog log)
    {
        if (_initFont == null || _scaleForPx == null || _makeBitmap == null)
        {
            log.Info("GlyphAtlas: stbtt exports unresolved (headless?); skipping bake");
            return null;
        }

        var a = new GlyphAtlas { Width = atlasSize, Height = atlasSize, Pixels = new byte[atlasSize * atlasSize] };

        IntPtr fi = Marshal.AllocHGlobal(FontInfoBytes);
        new Span<byte>((void*)fi, FontInfoBytes).Clear();   // InitFont fills what it uses; zero the rest to be version-proof
        IntPtr fontNative = Marshal.AllocHGlobal(font.Length);   // stbtt keeps a pointer into this; must outlive every call below
        Marshal.Copy(font, 0, fontNative, font.Length);
        try
        {
            if (_initFont((byte*)fi, (byte*)fontNative, 0) == 0) { log.Info("GlyphAtlas: stbtt_InitFont failed"); return null; }
            float scale = _scaleForPx((byte*)fi, pixelHeight);

            int asc = 0, desc = 0, gap = 0;
            if (_vmetrics != null) _vmetrics((byte*)fi, &asc, &desc, &gap);
            a.Ascent = asc * scale;
            a.LineHeight = (asc - desc + gap) * scale;

            int adv = 0, lsb = 0;
            if (_hmetrics != null) { _hmetrics((byte*)fi, ' ', &adv, &lsb); a._spaceAdvance = adv * scale; }

            int penX = 0, penY = 0, rowH = 0;
            fixed (byte* atlas = a.Pixels)
            {
                foreach ((int lo, int hi) in ranges)
                    for (int cp = lo; cp <= hi; cp++)
                    {
                        int ax = 0, al = 0;
                        if (_hmetrics != null) _hmetrics((byte*)fi, cp, &ax, &al);
                        float advance = ax * scale;

                        int x0 = 0, y0 = 0, x1 = 0, y1 = 0;
                        if (_bitmapBox != null) _bitmapBox((byte*)fi, cp, scale, scale, &x0, &y0, &x1, &y1);
                        int gw = x1 - x0, gh = y1 - y0;
                        if (gw <= 0 || gh <= 0) { a._glyphs[cp] = new Glyph { Advance = advance }; continue; }

                        if (penX + gw + Pad > atlasSize) { penX = 0; penY += rowH + Pad; rowH = 0; }
                        if (penY + gh > atlasSize)
                        {
                            log.Info($"GlyphAtlas: atlas {atlasSize}² full at cp=0x{cp:x}; remaining glyphs dropped");
                            goto done;   // ponytail: static atlas overflow just stops; ceiling = dynamic on-demand atlas
                        }

                        _makeBitmap((byte*)fi, atlas + penY * atlasSize + penX, gw, gh, atlasSize, scale, scale, cp);
                        a._glyphs[cp] = new Glyph
                        {
                            U0 = penX / (float)atlasSize, V0 = penY / (float)atlasSize,
                            U1 = (penX + gw) / (float)atlasSize, V1 = (penY + gh) / (float)atlasSize,
                            W = gw, H = gh, XOff = x0, YOff = y0, Advance = advance,
                        };
                        penX += gw + Pad;
                        if (gh > rowH) rowH = gh;
                    }
            }
            done:
            a.Pixels[atlasSize * atlasSize - 1] = 255;   // bottom-right texel = solid white, for chrome rects
            a.SolidU = (atlasSize - 0.5f) / atlasSize;
            a.SolidV = (atlasSize - 0.5f) / atlasSize;
            log.Info($"GlyphAtlas: baked {a._glyphs.Count} glyphs into {atlasSize}² @ {pixelHeight}px (lineH={a.LineHeight:F1})");
            return a;
        }
        finally { Marshal.FreeHGlobal(fi); Marshal.FreeHGlobal(fontNative); }
    }
}
