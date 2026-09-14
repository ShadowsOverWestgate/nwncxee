using CX.Framework;

namespace CX.Ui;

/// <summary>
/// A vertically scrolling box (#587): a viewport rect, the height of the content laid out inside it, and the
/// scroll <see cref="Offset"/> the owner subtracts from its content y when drawing and adds to a pointer y when
/// hit-testing. Owns the scrollbar (chat's chrome: 8 px gutter at the right edge, always visible while the
/// content overflows), the thumb drag, the track page-jump and the wheel step. The owner draws its content
/// between <see cref="VertexBatch.ClipY"/> / <see cref="VertexBatch.ClearClip"/> on the viewport band, then
/// calls <see cref="Draw"/> for the bar. Pure and zero-alloc: pixels in, offset out.
/// </summary>
public sealed class ScrollColumn
{
    /// <summary>Scrollbar gutter width (chat's <c>ScrollbarW</c>).</summary>
    public static float BarW => 8f * Metrics.Scale;
    /// <summary>Smallest thumb, so it stays grabbable when the content is very tall.</summary>
    public static float MinThumb => 24f * Metrics.Scale;
    /// <summary>Track colour: fixed chrome (chat's <c>SbTrack</c>), not themeable.</summary>
    private static readonly (float r, float g, float b, float a) Track = (0.04f, 0.05f, 0.07f, 0.55f);

    /// <summary>The viewport rect (absolute px).</summary>
    public float X, Y, W, H;
    /// <summary>Total laid-out content height; the owner writes it after its layout pass.</summary>
    public float ContentH;
    /// <summary>Pixels scrolled from the content top, always within 0..<see cref="MaxOffset"/>.</summary>
    public float Offset;

    private bool _drag;
    private float _gy, _goff;   // thumb-grab anchor: pointer y + Offset at the grab

    public float MaxOffset => ContentH > H ? ContentH - H : 0f;
    public bool Overflows => H > 0f && ContentH > H;
    public bool Dragging => _drag;

    public void SetRect(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; Clamp(); }

    public bool Contains(float mx, float my) => mx >= X && mx < X + W && my >= Y && my < Y + H;

    /// <summary>True when the pointer is over the scrollbar gutter (only meaningful while overflowing).</summary>
    public bool OverBar(float mx, float my)
        => Overflows && mx >= X + W - BarW && mx < X + W && my >= Y && my < Y + H;

    /// <summary>Content y under a pointer y (the owner hit-tests its rows against this).</summary>
    public float ToContent(float screenY) => screenY - Y + Offset;
    /// <summary>Screen y of a content y (the owner draws its rows at this).</summary>
    public float ToScreen(float contentY) => contentY - Offset + Y;

    /// <summary>Thumb rect, or false when nothing scrolls. Proportional to the visible fraction, floored at
    /// <see cref="MinThumb"/>; top of the track at Offset 0, bottom at <see cref="MaxOffset"/>.</summary>
    public bool ThumbRect(out float top, out float height)
    {
        top = 0f; height = 0f;
        if (!Overflows) return false;
        height = H * H / ContentH;
        if (height < MinThumb) height = MinThumb;
        if (height > H) height = H;
        float max = MaxOffset;
        top = Y + (max > 0f ? Offset / max : 0f) * (H - height);
        return true;
    }

    public bool OverThumb(float mx, float my)
        => ThumbRect(out float t, out float h) && mx >= X + W - BarW && mx < X + W && my >= t && my < t + h;

    /// <summary>Press: the thumb starts a drag; the rest of the gutter pages toward the pointer. True when the
    /// press landed on the bar (the owner then does not hit-test its content).</summary>
    public bool MouseDown(float mx, float my)
    {
        if (!OverBar(mx, my)) return false;
        if (OverThumb(mx, my)) { _drag = true; _gy = my; _goff = Offset; return true; }
        ThumbRect(out float top, out float _);
        ScrollBy(my < top ? -H : H);
        return true;
    }

    /// <summary>Motion while the thumb is grabbed: absolute-anchor mapping from thumb travel to Offset.</summary>
    public bool MouseMove(float mx, float my)
    {
        if (!_drag) return false;
        if (ThumbRect(out float _, out float th))
        {
            float travel = H - th;
            Offset = travel > 0f ? _goff + (my - _gy) * MaxOffset / travel : 0f;
            Clamp();
        }
        return true;
    }

    /// <summary>Release: ends a thumb drag. True iff one was in progress.</summary>
    public bool MouseUp() { bool d = _drag; _drag = false; return d; }

    /// <summary>Scroll by pixels (positive = content moves up = Offset grows). Returns true if it moved.</summary>
    public bool ScrollBy(float dpx)
    {
        float before = Offset;
        Offset += dpx;
        Clamp();
        return Offset != before;
    }

    /// <summary>Wheel notch in SDL sign (positive = away from the user = toward the top).</summary>
    public bool MouseWheel(int dy, float step) => ScrollBy(-dy * step);

    public void Clamp()
    {
        float max = MaxOffset;
        Offset = Offset < 0f ? 0f : Offset > max ? max : Offset;
    }

    public void Reset() { Offset = 0f; _drag = false; }

    /// <summary>Draw the track + thumb at the right edge (nothing when the content fits).</summary>
    public void Draw(VertexBatch b, (float r, float g, float b, float a) thumb)
    {
        if (!ThumbRect(out float top, out float h)) return;
        float x0 = X + W - BarW;
        b.Rect(x0, Y, X + W, Y + H, Track.r, Track.g, Track.b, Track.a);
        b.Rect(x0, top, X + W, top + h, thumb.r, thumb.g, thumb.b, thumb.a);
    }

    /// <summary>Off-device proof: offset clamp, thumb geometry, wheel + drag + page mapping, and a zero-alloc draw.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"ScrollColumn.SelfCheck FAIL: {w}"); } }

        var sc = new ScrollColumn();
        sc.SetRect(100f, 50f, 200f, 240f);
        sc.ContentH = 100f;
        E(!sc.Overflows && !sc.ThumbRect(out _, out _) && sc.MaxOffset == 0f, "content that fits: no thumb, no travel");
        E(!sc.MouseWheel(-1, 24f) && sc.Offset == 0f, "wheel on fitting content moves nothing");

        sc.ContentH = 60f * 24f;   // 60 rows of 24 px in a 10-row box
        E(sc.Overflows && sc.MaxOffset == 1440f - 240f, "60 rows overflow a 10-row box by 50 rows");
        E(sc.ThumbRect(out float t0, out float h0) && System.Math.Abs(t0 - 50f) < 0.01f && h0 >= MinThumb, "thumb at the track top at Offset 0");
        E(sc.MouseWheel(-1, 24f) && sc.Offset == 24f, "one notch down scrolls one step");
        E(sc.MouseWheel(1, 24f) && sc.Offset == 0f, "one notch up scrolls back");
        E(!sc.MouseWheel(1, 24f) && sc.Offset == 0f, "wheel past the top clamps and reports no move");
        sc.ScrollBy(99999f);
        E(sc.Offset == sc.MaxOffset, "scroll past the end clamps to MaxOffset");
        E(sc.ThumbRect(out float t1, out float h1) && System.Math.Abs(t1 + h1 - (50f + 240f)) < 0.01f, "thumb at the track bottom at MaxOffset");
        E(System.Math.Abs(sc.ToContent(50f + 10f) - (sc.Offset + 10f)) < 0.01f, "ToContent adds the offset");
        E(System.Math.Abs(sc.ToScreen(sc.Offset + 10f) - 60f) < 0.01f, "ToScreen subtracts it");

        // Thumb drag: grab at the bottom, drag up the full travel -> Offset 0.
        float bx = 100f + 200f - 2f;
        E(!sc.MouseDown(150f, 100f), "press off the gutter is not the bar's");
        E(sc.MouseDown(bx, t1 + 2f) && sc.Dragging, "press on the thumb grabs it");
        E(sc.MouseMove(bx, t1 + 2f - (240f - h1)), "drag consumed");
        E(sc.Offset == 0f, "dragging the thumb to the track top scrolls to 0");
        sc.MouseMove(bx, t1 + 2f - (240f - h1) / 2f);
        E(System.Math.Abs(sc.Offset - sc.MaxOffset / 2f) < 1f, "half the travel is half the range");
        E(sc.MouseUp() && !sc.Dragging && !sc.MouseUp(), "release ends the drag once");

        // Track page-jump: below the thumb pages down by one viewport, above pages up.
        sc.Offset = 0f;
        E(sc.MouseDown(bx, 50f + 235f) && sc.Offset == 240f, "press below the thumb pages down one box");
        sc.MouseUp();
        E(sc.MouseDown(bx, 50f + 1f) && sc.Offset == 0f, "press above the thumb pages up");
        sc.MouseUp();

        // Draw allocates nothing (AC: the panel draws per frame).
        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        var b = new VertexBatch(atlas.SolidU, atlas.SolidV);
        sc.Draw(b, (1f, 1f, 1f, 1f));
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { b.Clear(); sc.Draw(b, (1f, 1f, 1f, 1f)); }
        long delta = System.GC.GetAllocatedBytesForCurrentThread() - before;
        E(delta == 0, $"100 draws allocate nothing ({delta} bytes)");

        log.Info(ok ? "ScrollColumn.SelfCheck: passed" : "ScrollColumn.SelfCheck: FAILED");
    }
}
