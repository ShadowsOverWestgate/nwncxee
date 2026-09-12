using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Ui;

/// <summary>A face to bake: TTF bytes (keyed by their path) at a pixel height.</summary>
public readonly record struct FaceRequest(string Typeface, byte[] Font, float Px);

/// <summary>
/// The one shared single-channel glyph bitmap every face lives in (#469). Baked cold with the client's
/// stb_truetype exports: every face's glyph boxes are measured, shelf-packed into one square, and only
/// then rasterised; on overflow the side doubles and packing retries up to <see cref="MaxSide"/>. A sheet
/// is immutable once baked -- a new face set is a new sheet, swapped in whole by <c>TextRig</c>.
/// </summary>
public sealed unsafe class AtlasSheet
{
    /// <summary>ponytail: opaque stbtt_fontinfo is ~180B; over-allocate. Ceiling: bind the real struct.</summary>
    private const int FontInfoBytes = 1024;
    /// <summary>1px gutter so bilinear sampling at level 0 never bleeds a neighbour.</summary>
    public const int Pad = 1;
    /// <summary>Side of the solid-white block chrome rects sample. Bilinear at the block centre is 1.0 with
    /// 1.5 texels of margin, so vertex rounding cannot pull in a transparent neighbour (#468).</summary>
    private const int SolidSide = 4;
    /// <summary>Bitmap side ceiling: GL 3.3 guarantees 1024 and every target driver gives 16384; 4096 (16 MB)
    /// holds ~30 faces at 18px. Above it a bake is a refusal, not a partial sheet.</summary>
    public const int MaxSide = 4096;

    public byte[] Pixels = Array.Empty<byte>();
    public int Side;
    private readonly Dictionary<(string, float), GlyphAtlas> _faces = new();

    /// <summary>The face the first request baked -- what <c>TextRig.Atlas</c> hands a caller that asks for nothing.</summary>
    public GlyphAtlas Default = null!;

    public bool TryFace(string typeface, float px, out GlyphAtlas face) => _faces.TryGetValue((typeface, px), out face!);
    public int FaceCount => _faces.Count;

    /// <summary>Row-shelf packer over a <see cref="Side"/>² square. Pure: the headless self-check drives it with
    /// synthetic boxes and the bake drives it with measured glyph boxes.</summary>
    internal struct Shelf
    {
        private readonly int _side;
        private int _x, _y, _rowH;
        public Shelf(int side) { _side = side; _x = 0; _y = 0; _rowH = 0; }
        public bool TryPlace(int w, int h, out int x, out int y)
        {
            if (_x + w > _side) { _x = 0; _y += _rowH + Pad; _rowH = 0; }
            x = _x; y = _y;
            if (w > _side || _y + h > _side) return false;
            _x += w + Pad;
            if (h > _rowH) _rowH = h;
            return true;
        }
    }

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

    /// <summary>One measured glyph awaiting placement: its face, codepoint, box, and (after packing) its texel origin.</summary>
    private struct Box { public int Face, Cp, W, H, X0, Y0, X, Y; public float Advance, Scale; }

    /// <summary>Pack boxes into a square, doubling the side on overflow. Returns the side that fit, or 0 past the ceiling.
    /// The solid block is always the first placement.</summary>
    private static int Pack(Span<Box> boxes, int side, out int solidX, out int solidY)
    {
        for (; side <= MaxSide; side *= 2)
        {
            var shelf = new Shelf(side);
            if (!shelf.TryPlace(SolidSide, SolidSide, out solidX, out solidY)) continue;
            bool ok = true;
            for (int i = 0; i < boxes.Length && ok; i++) ok = shelf.TryPlace(boxes[i].W, boxes[i].H, out boxes[i].X, out boxes[i].Y);
            if (ok) return side;
        }
        solidX = solidY = 0;
        return 0;
    }

    /// <summary>
    /// Bake every request (first = default) over the given codepoint ranges into one sheet, starting at
    /// <paramref name="startSide"/>. Cold. Null if stbtt isn't linked in (headless), a font fails to init, or the
    /// packed side would exceed <see cref="MaxSide"/> -- the caller keeps whatever sheet it had.
    /// </summary>
    public static AtlasSheet? Bake(IReadOnlyList<FaceRequest> reqs, (int lo, int hi)[] ranges, int startSide, ExtenderLog log)
    {
        if (_initFont == null || _scaleForPx == null || _makeBitmap == null)
        {
            log.Info("AtlasSheet: stbtt exports unresolved (headless?); skipping bake");
            return null;
        }
        if (reqs.Count == 0) return null;
        reqs = reqs.DistinctBy(r => (r.Typeface, r.Px)).ToList();   // SetFont may land the default on an extra face's size

        var sheet = new AtlasSheet();
        var faces = new GlyphAtlas[reqs.Count];
        var boxes = new List<Box>(reqs.Count * 512);
        var infos = new IntPtr[reqs.Count];
        var fonts = new IntPtr[reqs.Count];
        try
        {
            for (int f = 0; f < reqs.Count; f++)
            {
                FaceRequest r = reqs[f];
                byte* fi = (byte*)(infos[f] = Marshal.AllocHGlobal(FontInfoBytes));
                new Span<byte>(fi, FontInfoBytes).Clear();   // InitFont fills what it uses; zero the rest to be version-proof
                byte* fn = (byte*)(fonts[f] = Marshal.AllocHGlobal(r.Font.Length));   // stbtt keeps a pointer into this
                Marshal.Copy(r.Font, 0, fonts[f], r.Font.Length);
                if (_initFont(fi, fn, 0) == 0) { log.Info($"AtlasSheet: stbtt_InitFont failed for {r.Typeface}"); return null; }
                float scale = _scaleForPx(fi, r.Px);

                var face = faces[f] = new GlyphAtlas();
                int asc = 0, desc = 0, gap = 0;
                if (_vmetrics != null) _vmetrics(fi, &asc, &desc, &gap);
                face.Ascent = asc * scale;
                face.LineHeight = (asc - desc + gap) * scale;
                int adv = 0, lsb = 0;
                if (_hmetrics != null) { _hmetrics(fi, ' ', &adv, &lsb); face.SpaceAdvance = adv * scale; }

                foreach ((int lo, int hi) in ranges)
                    for (int cp = lo; cp <= hi; cp++)
                    {
                        int ax = 0, al = 0;
                        if (_hmetrics != null) _hmetrics(fi, cp, &ax, &al);
                        float advance = ax * scale;
                        int x0 = 0, y0 = 0, x1 = 0, y1 = 0;
                        if (_bitmapBox != null) _bitmapBox(fi, cp, scale, scale, &x0, &y0, &x1, &y1);
                        int gw = x1 - x0, gh = y1 - y0;
                        if (gw <= 0 || gh <= 0) { face.Store(cp, new Glyph { Advance = advance }); continue; }
                        boxes.Add(new Box { Face = f, Cp = cp, W = gw, H = gh, X0 = x0, Y0 = y0, Advance = advance, Scale = scale });
                    }
            }

            Span<Box> bs = CollectionsMarshal.AsSpan(boxes);
            int side = Pack(bs, startSide, out int sx, out int sy);
            if (side == 0)
            {
                log.Info($"AtlasSheet: REFUSED -- {reqs.Count} face(s) / {boxes.Count} glyphs do not fit {MaxSide}²; previous atlas kept");
                return null;
            }
            sheet.Side = side;
            sheet.Pixels = new byte[side * side];
            for (int y = 0; y < SolidSide; y++) for (int x = 0; x < SolidSide; x++) sheet.Pixels[(sy + y) * side + sx + x] = 255;
            float su = (sx + SolidSide * 0.5f) / side, sv = (sy + SolidSide * 0.5f) / side;

            fixed (byte* px = sheet.Pixels)
                foreach (ref Box b in bs)
                {
                    _makeBitmap((byte*)infos[b.Face], px + b.Y * side + b.X, b.W, b.H, side, b.Scale, b.Scale, b.Cp);
                    faces[b.Face].Store(b.Cp, new Glyph
                    {
                        U0 = b.X / (float)side, V0 = b.Y / (float)side,
                        U1 = (b.X + b.W) / (float)side, V1 = (b.Y + b.H) / (float)side,
                        W = b.W, H = b.H, XOff = b.X0, YOff = b.Y0, Advance = b.Advance,
                    });
                }

            for (int f = 0; f < reqs.Count; f++)
            {
                faces[f].SolidU = su; faces[f].SolidV = sv;
                sheet._faces[(reqs[f].Typeface, reqs[f].Px)] = faces[f];
                log.Info($"AtlasSheet: face {System.IO.Path.GetFileName(reqs[f].Typeface)} @ {reqs[f].Px}px: {faces[f].Count} glyphs (lineH={faces[f].LineHeight:F1})");
            }
            sheet.Default = faces[0];
            log.Info($"AtlasSheet: {reqs.Count} face(s) packed into {side}²");
            return sheet;
        }
        finally
        {
            foreach (IntPtr p in infos) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            foreach (IntPtr p in fonts) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>Test-only: register a face without a native bake.</summary>
    internal void AddTestFace(string typeface, float px, GlyphAtlas face)
    {
        _faces[(typeface, px)] = face;
        Default ??= face;
    }

    /// <summary>#469: pack-and-grow (a box set that overflows 64² fits 128², one past the ceiling is refused,
    /// the solid block is placed first) and the face lookup, with no GL context and no stbtt.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"AtlasSheet.SelfCheck FAIL: {w}"); } }

        var boxes = new Box[100];
        for (int i = 0; i < boxes.Length; i++) { boxes[i].W = 10; boxes[i].H = 10; }
        int side = Pack(boxes, 64, out int sx, out int sy);
        E(side == 128, $"grow: expected 128, got {side}");
        E(sx == 0 && sy == 0, "solid block is the first placement");
        E(boxes[0].X == SolidSide + Pad && boxes[0].Y == 0, "first glyph sits right of the solid block");
        var seen = new HashSet<(int, int)>();
        foreach (Box b in boxes) E(seen.Add((b.X, b.Y)) && b.X + b.W <= side && b.Y + b.H <= side, "placements unique and in bounds");

        var big = new Box[1]; big[0].W = big[0].H = MaxSide + 1;
        E(Pack(big, 64, out _, out _) == 0, "past the ceiling is a refusal");

        var sheet = new AtlasSheet();
        var a = new GlyphAtlas { LineHeight = 18 }; var b2 = new GlyphAtlas { LineHeight = 14 };
        sheet.AddTestFace("ui.ttf", 18f, a); sheet.AddTestFace("ui.ttf", 14f, b2);
        E(sheet.Default == a, "first face is the default");
        E(sheet.TryFace("ui.ttf", 14f, out GlyphAtlas f) && f == b2, "face lookup by (typeface, px)");
        E(!sheet.TryFace("ui.ttf", 20f, out _), "unbaked face misses");

        log.Info(ok ? "AtlasSheet.SelfCheck OK" : "AtlasSheet.SelfCheck FAILED");
    }
}
