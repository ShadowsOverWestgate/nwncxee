using System.Numerics;
using System;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>#206: the two texture readbacks the census triage prints, on the submission side of the
/// extraction boundary. They belong to no pass -- they are pure GL queries -- but they are the reason
/// <c>Alloy/Scene/</c> used to hold nineteen <c>Gl.*</c> calls, so they live out here where GL is allowed.
/// <para>Both are triage only: engine opacity flags and the file header have each been wrong once in this
/// investigation, and glGetTexImage readback is the ground truth that settled it.</para></summary>
internal static unsafe class TexProbe
{
    /// <summary>Triage only: what GL actually holds for this texture. The engine's opacity flags and the
    /// file's own header have each been wrong once in this investigation; the uploaded format is neither.</summary>
    internal static string TexFormat(int tid)
    {
        if (tid <= 0 || !GlContextService.Available) return "fmt=?";
        uint t = (uint)tid;
        int fmt = 0, asize = 0, w = 0, h = 0;
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_ALPHA_SIZE, &asize);
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_WIDTH, &w);
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_HEIGHT, &h);
        return $"fmt=0x{fmt:x} asize={asize} {w}x{h}";
    }

    /// <summary>#129: what <c>textureLod(uTex, vec2(0.5), 20.0)</c> actually returns for this texture -- the
    /// stage's whole liquid colour. LOD 20 CLAMPS to <c>TEXTURE_MAX_LEVEL</c>, so a texture with no mip chain
    /// answers with one centre texel of the ripple pattern instead of the average the shader is asking for,
    /// and the pool takes that texel's colour. Prints the level landed on and its mean, so "the water is the
    /// wrong colour" becomes a number that can be compared between two areas.</summary>
    internal static string TexTopMip(int tid)
    {
        if (tid <= 0 || !GlContextService.Available) return "top=?";
        uint t = (uint)tid;
        int maxLevel = 0;
        Gl.GetTextureParameteriv(t, Gl.TEXTURE_MAX_LEVEL, &maxLevel);
        int lod = maxLevel < 20 ? maxLevel : 20, w = 0, h = 0;
        // ...and MAX_LEVEL is only a cap: the chain may be shorter than it claims. Walk down to a level that
        // actually has storage, which is where the sampler lands too.
        for (; lod > 0; lod--)
        {
            Gl.GetTextureLevelParameteriv(t, lod, Gl.TEXTURE_WIDTH, &w);
            Gl.GetTextureLevelParameteriv(t, lod, Gl.TEXTURE_HEIGHT, &h);
            if (w > 0 && h > 0) break;
        }
        if (lod == 0)
        {
            Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_WIDTH, &w);
            Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_HEIGHT, &h);
        }
        // Only the small end is read back: a level-0 readback of a 256x256 diffuse is a pipeline stall for a
        // diagnostic, and any level big enough to cost that is not the one the shader lands on anyway.
        if (w <= 0 || h <= 0 || w * h > 64) return $"top=L{lod} {w}x{h} (not read)";
        byte* px = stackalloc byte[64 * 4];
        Gl.GetTextureImage(t, lod, Gl.RGBA, Gl.UNSIGNED_BYTE, 64 * 4, px);
        int r = 0, g = 0, b = 0, a = 0, n = w * h;
        for (int i = 0; i < n; i++) { r += px[i * 4]; g += px[i * 4 + 1]; b += px[i * 4 + 2]; a += px[i * 4 + 3]; }
        return $"top=L{lod}/{maxLevel} {w}x{h} rgba({r / n},{g / n},{b / n},{a / n})";
    }
}
