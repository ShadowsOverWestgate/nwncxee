using System;
using System.Collections.Generic;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>
/// CX-114: one parsed <c>.mtr</c>, the material file a mesh's <c>materialname</c> names. Transcribed from
/// <c>SharedMaterial::ParseField</c> @0x5c6760 and <c>SharedMaterial::ExtractRenderHint</c> @0x5c5410: the
/// eleven texture slots, the render hint, the three custom shader stages, the named float parameters and the
/// two flags. Immutable after the parse and shared by every part whose mesh names it, which is why the part
/// column holds a reference rather than a copy.
/// <para>Engine-free: the bytes come from a provider the resource layer sets
/// (<see cref="Bytes"/>), so this parses identically off-device.</para>
/// </summary>
public sealed class MtrDef
{
    /// <summary>Texture slots the engine's own material carries. Slots 4..10 are past the engine's render
    /// ceiling and are #275's business (<c>MaterialExtraSlots</c>); they are parsed here so nothing is lost.</summary>
    public const int MaxTextures = 11;

    public string Name = "";
    /// <summary><c>bitmap</c>/<c>texture0</c>..<c>texture10</c>; empty for a slot the file does not state.</summary>
    public readonly string[] Textures = new string[MaxTextures];
    /// <summary><c>ExtractRenderHint</c>: 0 absent/unknown, 1 <c>none</c>, 2 <c>normalandspecmapped</c>,
    /// 3 <c>normaltangents</c>.</summary>
    public int RenderHint;
    public string ShaderVs = "", ShaderFs = "", ShaderGs = "";
    /// <summary><c>parameter &lt;name&gt; &lt;float&gt;...</c>, in file order.</summary>
    public readonly List<(string Name, float[] Values)> Parameters = new();
    public bool Transparency, TwoSided, Volumetric;
    /// <summary><c>sample_framebuffer</c>: the engine parses it as an INT, not a flag.</summary>
    public int SampleFramebuffer;

    private MtrDef() { for (int i = 0; i < MaxTextures; i++) Textures[i] = ""; }

    /// <summary>Where a <c>.mtr</c>'s bytes come from. Null (the default) means none can be read, and then a
    /// mesh's own texture names stand -- which is vanilla for a model with no <c>.mtr</c>. The resource layer
    /// sets this; nothing under <c>Alloy/Db/</c> reaches a resource manager itself.</summary>
    public static Func<string, byte[]?>? Bytes;

    private static readonly Dictionary<string, MtrDef?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The material <paramref name="name"/>, parsed once and shared; null when there is no such file
    /// or no provider. A miss is not remembered: the provider may only not have read it YET.</summary>
    public static MtrDef? Resolve(string name)
    {
        if (name.Length == 0) return null;
        lock (_cache)
        {
            if (_cache.TryGetValue(name, out MtrDef? have)) return have;
            byte[]? data = Bytes?.Invoke(name);
            if (data == null) return null;
            MtrDef m = Parse(data, name);
            _cache[name] = m;
            return m;
        }
    }

    /// <summary>The material a mesh draws with: <c>MdlNodeTriMesh::GetMaterialName</c> @0x53c4a0 answers
    /// <c>materialname</c>, or the <c>bitmap</c> when that is empty -- so a bitmap naming a <c>.mtr</c>
    /// (SoW's dynamic skies) IS that material.</summary>
    public static MtrDef? Of(MeshDef mesh)
        => Resolve(mesh.MaterialName.Length > 0 ? mesh.MaterialName : mesh.Textures[0]);

    /// <summary>Forget every parsed material (the self-check, and a content reload).</summary>
    public static void Forget() { lock (_cache) _cache.Clear(); }

    /// <summary>Parse one <c>.mtr</c> text. Never fails: an unknown line is skipped, as the engine's
    /// field walk skips it.</summary>
    public static MtrDef Parse(ReadOnlySpan<byte> data, string name)
    {
        var m = new MtrDef { Name = name };
        int i = 0;
        Span<char> buf = stackalloc char[512];
        while (i < data.Length)
        {
            int end = i;
            while (end < data.Length && data[end] != (byte)'\n' && data[end] != (byte)'\r') end++;
            int n = Math.Min(end - i, buf.Length);
            for (int k = 0; k < n; k++) buf[k] = (char)data[i + k];
            Line(m, buf[..n]);
            i = end;
            while (i < data.Length && (data[i] == (byte)'\n' || data[i] == (byte)'\r')) i++;
        }
        return m;
    }

    private static void Line(MtrDef m, ReadOnlySpan<char> line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] == '#' || line[0] == '/') return;
        int sp = line.IndexOfAny(' ', '\t');
        ReadOnlySpan<char> key = sp < 0 ? line : line[..sp];
        ReadOnlySpan<char> rest = sp < 0 ? default : line[(sp + 1)..].Trim();

        if (Is(key, "bitmap") || Is(key, "texture0")) { m.Textures[0] = Word(rest); return; }
        for (int t = 1; t < MaxTextures; t++)
            if (Is(key, "texture" + t.ToString())) { m.Textures[t] = Word(rest); return; }
        if (Is(key, "renderhint")) { m.RenderHint = Hint(Word(rest)); return; }
        if (Is(key, "customShaderVS")) { m.ShaderVs = Word(rest); return; }
        if (Is(key, "customShaderFS")) { m.ShaderFs = Word(rest); return; }
        if (Is(key, "customShaderGS")) { m.ShaderGs = Word(rest); return; }
        if (Is(key, "transparency")) { Flag(rest, ref m.Transparency); return; }
        if (Is(key, "twosided")) { Flag(rest, ref m.TwoSided); return; }
        if (Is(key, "volumetric")) { Flag(rest, ref m.Volumetric); return; }
        if (Is(key, "sample_framebuffer")) { m.SampleFramebuffer = Int(rest, m.SampleFramebuffer); return; }
        if (Is(key, "parameter"))
        {
            int s2 = rest.IndexOfAny(' ', '\t');
            if (s2 < 0) return;
            string pn = new(rest[..s2]);
            var vals = new List<float>(4);
            ReadOnlySpan<char> tail = rest[(s2 + 1)..];
            while (tail.Length > 0)
            {
                tail = tail.TrimStart();
                if (tail.Length == 0) break;
                int e = tail.IndexOfAny(' ', '\t');
                ReadOnlySpan<char> tok = e < 0 ? tail : tail[..e];
                if (float.TryParse(tok, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out float v)) vals.Add(v);
                tail = e < 0 ? default : tail[(e + 1)..];
            }
            m.Parameters.Add((pn, vals.ToArray()));
        }
    }

    private static bool Is(ReadOnlySpan<char> a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static string Word(ReadOnlySpan<char> s)
    {
        s = s.Trim();
        int e = s.IndexOfAny(' ', '\t');
        return new string(e < 0 ? s : s[..e]);
    }

    /// <summary><c>Parse(char const*, bool&amp;)</c> @0x446900: <c>true</c>/<c>1</c> set it, <c>false</c>/<c>0</c>
    /// clear it, and ANYTHING ELSE leaves the value alone -- an unknown word is not a yes.</summary>
    private static void Flag(ReadOnlySpan<char> s, ref bool v)
    {
        string w = Word(s);
        if (w.Equals("true", StringComparison.OrdinalIgnoreCase) || w == "1") v = true;
        else if (w.Equals("false", StringComparison.OrdinalIgnoreCase) || w == "0") v = false;
    }

    /// <summary><c>Parse(char const*, int&amp;)</c> @0x446790: <c>%d</c> first, then <c>TRUE</c>/<c>FALSE</c>
    /// as 1/0; an unparsable word leaves the value alone.</summary>
    private static int Int(ReadOnlySpan<char> s, int had)
    {
        string w = Word(s);
        if (int.TryParse(w, System.Globalization.NumberStyles.Integer,
                         System.Globalization.CultureInfo.InvariantCulture, out int v)) return v;
        if (w.Equals("true", StringComparison.OrdinalIgnoreCase)) return 1;
        if (w.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0;
        return had;
    }

    /// <summary><c>SharedMaterial::ExtractRenderHint</c> @0x5c5410, exactly: an unrecognised hint is 0, and
    /// the explicit <c>none</c> is 1 -- the engine distinguishes them, so this does too.</summary>
    public static int Hint(string s)
        => s.Equals("normalandspecmapped", StringComparison.OrdinalIgnoreCase) ? 2
         : s.Equals("normaltangents", StringComparison.OrdinalIgnoreCase) ? 3
         : s.Equals("none", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}
