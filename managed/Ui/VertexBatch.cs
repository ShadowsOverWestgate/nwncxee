using System;
using System.Collections.Generic;

namespace CX.Ui;

/// <summary>
/// Accumulates interleaved 8-float vertices (x,y,u,v,r,g,b,a) for the owned GL text/UI pass.
/// One primitive set feeds the whole chatbox: <see cref="Rect"/> for chrome (samples a solid white
/// atlas texel so alpha comes straight from the vertex) and <see cref="Glyph"/> for text. Built cold
/// (ingest / input / resize thread), then handed to <c>ChatRenderer.Publish</c>; the hot draw
/// only uploads. Pooled per thread via <c>WindowRenderer</c> (#53): <see cref="Reset"/> clears
/// + reconfigures the solid UV, so the internal <c>List&lt;float&gt;</c> keeps its capacity across rebuilds.
/// </summary>
public sealed class VertexBatch
{
    public const int Floats = 8;
    private const int VertsPerQuad = 6;   // two triangles; GL_QUADS is gone in core profile

    private readonly List<float> _v = new(1024);
    private float _su, _sv;   // solid (white) texel centre -- rects sample this for flat colour
    private float _clipY0 = float.NegativeInfinity, _clipY1 = float.PositiveInfinity;

    public VertexBatch(float solidU, float solidV) { _su = solidU; _sv = solidV; }

    /// <summary>Clear the batch and reconfigure the solid UV. The internal list keeps its capacity.</summary>
    public void Reset(float solidU, float solidV) { _v.Clear(); _su = solidU; _sv = solidV; }
    public void Clear() => _v.Clear();

    /// <summary>Vertical clip band for every following <see cref="Rect"/> / <see cref="Glyph"/>: quads are cut
    /// at the band edges with their texcoords re-interpolated, so a scroll column's partially visible top and
    /// bottom rows end exactly at its box. <see cref="GradientRect"/> and <see cref="GlyphItalic"/> ignore it.</summary>
    public void ClipY(float y0, float y1) { _clipY0 = y0; _clipY1 = y1; }
    public void ClearClip() { _clipY0 = float.NegativeInfinity; _clipY1 = float.PositiveInfinity; }
    public int VertexCount => _v.Count / Floats;
    public float[] ToArray() => _v.ToArray();
    /// <summary>The accumulated floats without a copy, for a per-frame owner that uploads straight from the batch
    /// (the settings panel). Valid until the next mutation.</summary>
    public ReadOnlySpan<float> AsSpan() => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_v);

    /// <summary>Solid-colour rect (chrome): both texcoords pin to the white texel so <c>tex.r==1</c>.</summary>
    public void Rect(float x0, float y0, float x1, float y1, float r, float g, float b, float a)
        => Quad(x0, y0, x1, y1, _su, _sv, _su, _sv, r, g, b, a);

    /// <summary>Per-corner coloured rect: the GPU interpolates between the four corners. Corners are
    /// top-left, top-right, bottom-right, bottom-left (matching the winding order of <see cref="Rect"/>).</summary>
    public void GradientRect(float x0, float y0, float x1, float y1,
        (float r, float g, float b, float a) tl, (float r, float g, float b, float a) tr,
        (float r, float g, float b, float a) br, (float r, float g, float b, float a) bl)
    {
        V(x0,y0, _su,_sv, tl.r,tl.g,tl.b,tl.a); V(x1,y0, _su,_sv, tr.r,tr.g,tr.b,tr.a); V(x1,y1, _su,_sv, br.r,br.g,br.b,br.a);
        V(x0,y0, _su,_sv, tl.r,tl.g,tl.b,tl.a); V(x1,y1, _su,_sv, br.r,br.g,br.b,br.a); V(x0,y1, _su,_sv, bl.r,bl.g,bl.b,bl.a);
    }

    /// <summary>One baked glyph at pen <paramref name="penX"/>/<paramref name="baseline"/> (empty glyphs skipped).</summary>
    public void Glyph(in Glyph gl, float penX, float baseline, float r, float g, float b, float a)
    {
        if (gl.W <= 0) return;
        float x0 = penX + gl.XOff, y0 = baseline + gl.YOff, x1 = x0 + gl.W, y1 = y0 + gl.H;
        Quad(x0, y0, x1, y1, gl.U0, gl.V0, gl.U1, gl.V1, r, g, b, a);
    }

    /// <summary>Synthetic italic: shear the top edge of the glyph quad rightward.</summary>
    public void GlyphItalic(in Glyph gl, float penX, float baseline, float r, float g, float b, float a,
                            float shear = 0.2f)
    {
        if (gl.W <= 0) return;
        float x0 = penX + gl.XOff, y0 = baseline + gl.YOff, x1 = x0 + gl.W, y1 = y0 + gl.H;
        float dx = gl.H * shear;
        V(x0+dx,y0, gl.U0,gl.V0, r,g,b,a); V(x1+dx,y0, gl.U1,gl.V0, r,g,b,a); V(x1,y1, gl.U1,gl.V1, r,g,b,a);
        V(x0+dx,y0, gl.U0,gl.V0, r,g,b,a); V(x1,y1, gl.U1,gl.V1, r,g,b,a);    V(x0,y1, gl.U0,gl.V1, r,g,b,a);
    }

    /// <summary>One baked glyph as a sprite (#593): its box centred on (<paramref name="cx"/>, <paramref name="cy"/>)
    /// and rotated by <paramref name="angle"/> radians (clockwise on the y-down screen). Ignores the clip band.</summary>
    public void GlyphSprite(in Glyph gl, float cx, float cy, float angle, float r, float g, float b, float a)
    {
        if (gl.W <= 0) return;
        float hw = gl.W * 0.5f, hh = gl.H * 0.5f;
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        float tlx = cx - hw * c + hh * s, tly = cy - hw * s - hh * c;
        float trx = cx + hw * c + hh * s, try_ = cy + hw * s - hh * c;
        float brx = cx + hw * c - hh * s, bry = cy + hw * s + hh * c;
        float blx = cx - hw * c - hh * s, bly = cy - hw * s + hh * c;
        V(tlx,tly, gl.U0,gl.V0, r,g,b,a); V(trx,try_, gl.U1,gl.V0, r,g,b,a); V(brx,bry, gl.U1,gl.V1, r,g,b,a);
        V(tlx,tly, gl.U0,gl.V0, r,g,b,a); V(brx,bry, gl.U1,gl.V1, r,g,b,a);  V(blx,bly, gl.U0,gl.V1, r,g,b,a);
    }

    /// <summary>A textured quad with explicit UVs (a skin batch; honours the clip band).</summary>
    public void Tex(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1,
                    float r, float g, float b, float a)
        => Quad(x0, y0, x1, y1, u0, v0, u1, v1, r, g, b, a);

    /// <summary>Nine-slice (native-gui-skinning §4.2b): the texture's <paramref name="l"/>/<paramref name="t"/>/
    /// <paramref name="r"/>/<paramref name="bt"/> texel border keeps its size × <paramref name="scale"/> on screen, the
    /// edges stretch along one axis, the centre along both. Engine textures are bottom-up (v = 1 is the top row).
    /// A rect narrower than its borders shrinks them proportionally. <paramref name="tw"/>/<paramref name="th"/>
    /// are the texture's texel size.</summary>
    public void NineSlice(float x0, float y0, float x1, float y1, int tw, int th, int l, int t, int r, int bt,
                          float scale, float cr, float cg, float cb, float ca)
    {
        if (tw <= 0 || th <= 0 || x1 <= x0 || y1 <= y0) return;
        float sl = l * scale, sr = r * scale, st = t * scale, sb = bt * scale;
        float kx = sl + sr > x1 - x0 ? (x1 - x0) / (sl + sr) : 1f, ky = st + sb > y1 - y0 ? (y1 - y0) / (st + sb) : 1f;
        sl *= kx; sr *= kx; st *= ky; sb *= ky;
        float xa = x0 + sl, xb = x1 - sr, ya = y0 + st, yb = y1 - sb;
        float ua = (float)l / tw, ub = 1f - (float)r / tw;
        float vTop = 1f, va = 1f - (float)t / th, vb = (float)bt / th, vBot = 0f;
        Span<float> xs = stackalloc float[] { x0, xa, xb, x1 };
        Span<float> us = stackalloc float[] { 0f, ua, ub, 1f };
        Span<float> ys = stackalloc float[] { y0, ya, yb, y1 };
        Span<float> vs = stackalloc float[] { vTop, va, vb, vBot };
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
                if (xs[i + 1] > xs[i] && ys[j + 1] > ys[j])
                    Quad(xs[i], ys[j], xs[i + 1], ys[j + 1], us[i], vs[j], us[i + 1], vs[j + 1], cr, cg, cb, ca);
    }

    private void Quad(float x0, float y0, float x1, float y1,
                      float u0, float v0, float u1, float v1,
                      float r, float g, float b, float a)
    {
        if (y1 > y0)
        {
            if (y1 <= _clipY0 || y0 >= _clipY1) return;
            if (y0 < _clipY0) { v0 += (v1 - v0) * (_clipY0 - y0) / (y1 - y0); y0 = _clipY0; }
            if (y1 > _clipY1) { v1 -= (v1 - v0) * (y1 - _clipY1) / (y1 - y0); y1 = _clipY1; }
        }
        V(x0, y0, u0, v0, r, g, b, a); V(x1, y0, u1, v0, r, g, b, a); V(x1, y1, u1, v1, r, g, b, a);
        V(x0, y0, u0, v0, r, g, b, a); V(x1, y1, u1, v1, r, g, b, a); V(x0, y1, u0, v1, r, g, b, a);
    }

    private void V(float x, float y, float u, float v, float r, float g, float b, float a)
    {
        _v.Add(x); _v.Add(y); _v.Add(u); _v.Add(v); _v.Add(r); _v.Add(g); _v.Add(b); _v.Add(a);
    }
}
