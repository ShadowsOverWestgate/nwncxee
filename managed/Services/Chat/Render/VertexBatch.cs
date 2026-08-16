using System.Collections.Generic;

namespace Sowx.Services.Chat.Render;

/// <summary>
/// Accumulates interleaved 8-float vertices (x,y,u,v,r,g,b,a) for the owned GL text/UI pass.
/// One primitive set feeds the whole chatbox: <see cref="Rect"/> for chrome (samples a solid white
/// atlas texel so alpha comes straight from the vertex) and <see cref="Glyph"/> for text. Built cold
/// (ingest / input / resize thread), then handed to <see cref="TextRenderer.Publish"/>; the hot draw
/// only uploads. Pooled per thread via <see cref="WindowRenderer"/> (#53): <see cref="Reset"/> clears
/// + reconfigures the solid UV, so the internal <c>List&lt;float&gt;</c> keeps its capacity across rebuilds.
/// </summary>
public sealed class VertexBatch
{
    public const int Floats = 8;
    private const int VertsPerQuad = 6;   // two triangles; GL_QUADS is gone in core profile

    private readonly List<float> _v = new(1024);
    private float _su, _sv;   // solid (white) texel centre -- rects sample this for flat colour

    public VertexBatch(float solidU, float solidV) { _su = solidU; _sv = solidV; }

    /// <summary>Clear the batch and reconfigure the solid UV. The internal list keeps its capacity.</summary>
    public void Reset(float solidU, float solidV) { _v.Clear(); _su = solidU; _sv = solidV; }
    public void Clear() => _v.Clear();
    public int VertexCount => _v.Count / Floats;
    public float[] ToArray() => _v.ToArray();

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

    private void Quad(float x0, float y0, float x1, float y1,
                      float u0, float v0, float u1, float v1,
                      float r, float g, float b, float a)
    {
        V(x0, y0, u0, v0, r, g, b, a); V(x1, y0, u1, v0, r, g, b, a); V(x1, y1, u1, v1, r, g, b, a);
        V(x0, y0, u0, v0, r, g, b, a); V(x1, y1, u1, v1, r, g, b, a); V(x0, y1, u0, v1, r, g, b, a);
    }

    private void V(float x, float y, float u, float v, float r, float g, float b, float a)
    {
        _v.Add(x); _v.Add(y); _v.Add(u); _v.Add(v); _v.Add(r); _v.Add(g); _v.Add(b); _v.Add(a);
    }
}
