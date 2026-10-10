using System;
using System.Collections.Generic;
using System.Globalization;

namespace CX.Alloy.Db;

/// <summary>The loaded-font table, CX's <c>CAurFont::s_LoadedFontList</c>. A font is keyed on the name the game
/// asks for -- for a TTF font that name IS the token string, e.g. <c>"ttf fnt_default 14"</c> -- and its index
/// is its position here, which is what <c>GetCurrentFontIndex</c> returns. A font is registered by whoever owns
/// it; an unregistered name resolves to nothing, as the engine does for a failed <c>SetFont</c>.</summary>
public static class TextFonts
{
    private static readonly List<TextFont> _fonts = new(8);
    private static readonly Dictionary<string, int> _byName = new(8, StringComparer.Ordinal);

    public static int Count => _fonts.Count;

    /// <summary>GL names of atlases no font refers to any more -- a replaced font's, or every font's after a
    /// context loss. Drained by <c>TextAtlas.Pump</c>, which is the only place that may call GL.</summary>
    public static readonly System.Collections.Generic.List<uint> Orphans = new(4);
    /// <summary>Add or replace a font by name; returns its index.</summary>
    public static int Register(TextFont f)
    {
        if (_byName.TryGetValue(f.Name, out int at))
        {
            // Re-registering a name replaces the font; its atlas would otherwise be leaked for the session.
            uint old = _fonts[at].Atlas;
            if (old != 0 && old != f.Atlas) Orphans.Add(old);
            _fonts[at] = f;
            return at;
        }
        _fonts.Add(f);
        at = _fonts.Count - 1;
        _byName[f.Name] = at;
        return at;
    }

    /// <summary>The font for the name the game asked for, after <see cref="Remap"/>; null when nothing holds it.</summary>
    public static TextFont? Get(string name)
        => _byName.TryGetValue(Remap(name), out int at) ? _fonts[at] : null;

    public static int IndexOf(TextFont f)
        => _byName.TryGetValue(f.Name, out int at) ? at : -1;

    public static TextFont? At(int index) => (uint)index < (uint)_fonts.Count ? _fonts[index] : null;

    /// <summary>Forget every font. <paramref name="orphanAtlases"/> false is the context-loss case: the names
    /// died with the context, so deleting them would be a use of a dead name.</summary>
    internal static void Clear(bool orphanAtlases = true)
    {
        if (orphanAtlases)
            foreach (TextFont f in _fonts) if (f.Atlas != 0) Orphans.Add(f.Atlas);
        _fonts.Clear();
        _byName.Clear();
    }

    /// <summary>The default font every unknown name becomes, and the dialog font <c>fnt_floaty_smal</c> becomes.
    /// <c>CTlkTable::SetUseLargeDialogFont</c> swaps the second between these two.</summary>
    public const string Default = "ttf fnt_default 14";
    public const string DialogLarge = "ttf fnt_default_hr 16";

    /// <summary>True when the large dialog font is selected (<c>CTlkTable::SetUseLargeDialogFont</c>).</summary>
    public static bool LargeDialogFont;

    /// <summary><c>CTlkTable::RemapFontName</c> @0x53fb60: a name starting <c>"ttf"</c> is used as given, three
    /// legacy bitmap names are redirected, and everything else becomes the default. Case-insensitive on the three,
    /// case-SENSITIVE on the <c>"ttf"</c> prefix, like the engine's <c>strncmp</c>/<c>strcasecmp</c> pair.</summary>
    public static string Remap(string name)
    {
        if (name.StartsWith("ttf", StringComparison.Ordinal)) return name;
        if (string.Equals(name, "fnt_galahad14", StringComparison.OrdinalIgnoreCase)) return "ttf fnt_maintext 17";
        if (string.Equals(name, "fnt_console", StringComparison.OrdinalIgnoreCase)) return Default;
        if (string.Equals(name, "fnt_floaty_smal", StringComparison.OrdinalIgnoreCase))
            return LargeDialogFont ? DialogLarge : Default;
        return Default;
    }

    /// <summary>A password variant: the engine deep-copies the font's info and replaces glyphs 1..numchars-1 with
    /// <paramref name="mask"/>'s, leaving index 0 alone.</summary>
    public static TextFont? Password(string name, byte mask)
    {
        TextFont? src = Get(name);
        if (src == null) return null;
        string key = src.Name + "\u0000pw" + (char)mask;
        if (_byName.TryGetValue(key, out int at)) return _fonts[at];
        var f = new TextFont { Name = key };
        Mask(f, src, mask);
        Register(f);
        return f;
    }

    private static void Mask(TextFont f, TextFont src, byte mask)
    {
        f.CopyFrom(src);
        for (int i = 1; i < 256; i++)
        {
            f.UlU[i] = src.UlU[mask]; f.UlV[i] = src.UlV[mask]; f.UlZ[i] = src.UlZ[mask];
            f.LrU[i] = src.LrU[mask]; f.LrV[i] = src.LrV[mask]; f.LrZ[i] = src.LrZ[mask];
            f.TopTrim[i] = src.TopTrim[mask]; f.BotTrim[i] = src.BotTrim[mask];
        }
    }

    /// <summary>A re-baked atlas lands: <paramref name="font"/> takes <paramref name="fresh"/>'s glyphs and atlas,
    /// its old atlas is orphaned, and its password variants are re-masked off it. Cold: one UI-scale change.</summary>
    internal static void Swap(TextFont font, TextFont fresh)
    {
        if (font.Atlas != 0 && font.Atlas != fresh.Atlas) Orphans.Add(font.Atlas);
        font.CopyFrom(fresh);
        string prefix = font.Name + "\u0000pw";
        foreach (TextFont v in _fonts)
            if (v.Name.Length == prefix.Length + 1 && v.Name.StartsWith(prefix, StringComparison.Ordinal))
                Mask(v, font, (byte)v.Name[^1]);
    }

    /// <summary>Parse a font's <c>.txi</c> the way <c>CAurTextureBasic::Init</c> @0x488940 and
    /// <c>CAurFontInfo::ParseField</c> @0x487e00 do: a key then its value, matched case-insensitively, with
    /// the two coordinate lists as <c>&lt;key&gt; &lt;count&gt;</c>, that many <c>x y z</c> lines and an
    /// <c>endlist</c>. Unknown keys are ignored; false means no glyphs -- "this texture is not a font".</summary>
    public static bool ParseTxi(string name, string txi, out TextFont font)
    {
        font = new TextFont { Name = name };
        float a = 0f, b = 0f, c = 0f;
        return ParseTxiInto(font, txi, ref a, ref b, ref c);
    }

    /// <summary>Parse into an existing font, also handing back the three TTF outline-filter terms the
    /// <c>.txi</c> may override (<c>ttfContrast</c>, <c>ttfOutlineBrightness</c>, <c>ttfOutlineOpacity</c>).
    /// A TTF font's <c>fontheight</c> comes from its NAME, so this never writes one.</summary>
    public static bool ParseTxiInto(TextFont font, string txi,
                                    ref float contrast, ref float outlineBrightness, ref float outlineOpacity)
    {
        string[] lines = txi.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            int sp = line.IndexOf(' ');
            string key = sp < 0 ? line : line[..sp];
            string val = sp < 0 ? "" : line[(sp + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "numchars": font.NumChars = Int(val); break;
                case "fontheight": font.FontHeight = Flt(val); break;
                case "baselineheight": font.BaselineHeight = Flt(val); break;
                case "texturewidth": font.TextureWidth = Flt(val); break;
                case "spacingr": font.SpacingR = Flt(val); break;
                case "spacingb": font.SpacingB = Flt(val); break;
                case "caretindent": font.CaretIndent = Flt(val); break;
                case "isdoublebyte": font.DoubleByte = Int(val) != 0; break;
                case "ttfcontrast": contrast = Flt(val); break;
                case "ttfoutlinebrightness": outlineBrightness = Flt(val); break;
                case "ttfoutlineopacity": outlineOpacity = Flt(val); break;
                case "upperleftcoords": i = Coords(lines, i, Int(val), font.UlU, font.UlV, font.UlZ); break;
                case "lowerrightcoords": i = Coords(lines, i, Int(val), font.LrU, font.LrV, font.LrZ); break;
            }
        }
        return font.NumChars > 0;
    }

    private static int Coords(string[] lines, int at, int count, float[] x, float[] y, float[] z)
    {
        int i = at + 1;
        for (int k = 0; k < count && i < lines.Length; k++, i++)
        {
            string s = lines[i].Trim();
            if (s.StartsWith("endlist", StringComparison.OrdinalIgnoreCase)) break;
            string[] p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || k >= 256) continue;
            x[k] = Flt(p[0]); y[k] = Flt(p[1]);
            if (p.Length > 2) z[k] = Flt(p[2]);
        }
        return i - 1;
    }

    private static int Int(string s) => int.TryParse(s.Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
    private static float Flt(string s) => float.TryParse(s.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
}
