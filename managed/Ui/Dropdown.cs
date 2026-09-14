using System;
using CX.Framework;

namespace CX.Ui;

/// <summary>
/// The enumerated-choice control's open list (#587): click the closed box, a list of choices drops beneath it
/// (above it when it would run past <c>clampBottom</c>), pick one, or click anywhere else to close. The list is
/// a <see cref="ScrollColumn"/> capped at <see cref="MaxVisible"/> items, so a long ladder (font size, 27
/// entries) scrolls by wheel and by thumb. One instance serves every dropdown row of a panel: the owner opens
/// it against the pressed row and keeps <see cref="Tag"/> to know whose it is. Pure and zero-alloc in draw.
/// The closed box is the owner's to draw (<see cref="DrawBox"/> is the shared look).
/// </summary>
public sealed class Dropdown
{
    /// <summary>Rows the open list shows at most before it scrolls.</summary>
    public const int MaxVisible = 8;
    private const uint FrameRgb = 0x8C8C8C;
    private const float FrameA = 1f;

    public bool Open { get; private set; }
    /// <summary>The choices shown; the caller's array, never copied.</summary>
    public string[] Choices = Array.Empty<string>();
    /// <summary>Index drawn as the current value.</summary>
    public int Selected;
    /// <summary>Index under the pointer, or -1.</summary>
    public int Hover = -1;
    /// <summary>The owner's handle on which row this list belongs to (a <see cref="SetRow"/> in the panel).</summary>
    public object? Tag;
    /// <summary>Item height, set at <see cref="OpenAt"/>.</summary>
    public float ItemH { get; private set; }
    /// <summary>The list viewport; its rect is the list rect.</summary>
    public readonly ScrollColumn List = new();

    public float X => List.X;
    public float Y => List.Y;
    public float W => List.W;
    public float H => List.H;

    /// <summary>Open the list under the anchor box (its left x, bottom y and width), flipping above
    /// <paramref name="anchorTop"/> when the list would end past <paramref name="clampBottom"/>. The list
    /// scrolls so the selected item is visible.</summary>
    public void OpenAt(string[] choices, int selected, object? tag, float anchorX, float anchorTop, float anchorBottom,
                       float w, float itemH, float clampBottom = float.MaxValue)
    {
        Choices = choices; Selected = selected; Tag = tag; ItemH = itemH; Hover = -1;
        int n = choices.Length;
        int shown = n < MaxVisible ? n : MaxVisible;
        float h = shown * itemH;
        float y = anchorBottom;
        if (y + h > clampBottom && anchorTop - h >= 0f) y = anchorTop - h;
        List.Reset();
        List.SetRect(anchorX, y, w, h);
        List.ContentH = n * itemH;
        if ((uint)selected < (uint)n) { List.Offset = selected * itemH - h * 0.5f + itemH * 0.5f; List.Clamp(); }
        Open = true;
    }

    public void Close() { Open = false; Hover = -1; Tag = null; List.MouseUp(); }

    public bool Contains(float mx, float my) => Open && List.Contains(mx, my);
    public bool Dragging => List.Dragging;

    /// <summary>Screen top of item <paramref name="i"/> (may lie outside the list while scrolled).</summary>
    public float ItemTop(int i) => List.ToScreen(i * ItemH);

    /// <summary>Item index under the pointer (the gutter excluded), or -1.</summary>
    public int ItemAt(float mx, float my)
    {
        if (!Contains(mx, my) || List.OverBar(mx, my)) return -1;
        int i = (int)(List.ToContent(my) / ItemH);
        return (uint)i < (uint)Choices.Length ? i : -1;
    }

    /// <summary>Press while open: always consumed. An item press picks it (<paramref name="picked"/> = its index)
    /// and closes; the gutter scrolls; anything else closes with nothing picked. False when closed.</summary>
    public bool MouseDown(float mx, float my, out int picked)
    {
        picked = -1;
        if (!Open) return false;
        if (List.MouseDown(mx, my)) return true;
        int i = ItemAt(mx, my);
        if (i >= 0) { picked = i; Selected = i; }
        Close();
        return true;
    }

    /// <summary>Motion while open: the thumb drag, else the hover highlight. True when something to redraw changed.</summary>
    public bool MouseMove(float mx, float my)
    {
        if (!Open) return false;
        if (List.MouseMove(mx, my)) return true;
        int h = ItemAt(mx, my);
        if (h == Hover) return false;
        Hover = h;
        return true;
    }

    public bool MouseUp() => List.MouseUp();

    /// <summary>Wheel while open scrolls the list one item per notch, wherever the pointer is.</summary>
    public bool MouseWheel(int dy)
    {
        if (!Open) return false;
        bool moved = List.MouseWheel(dy, ItemH);
        Hover = -1;   // the rows moved under a still pointer; the next motion re-hovers
        return moved;
    }

    /// <summary>The closed box: a value box with the current choice and a ▾ at its right edge.</summary>
    public static void DrawBox(VertexBatch b, GlyphAtlas atlas, float x0, float y0, float x1, float y1, float baseline,
                               string value, (float r, float g, float b, float a) bg, uint textRgb, float alpha = 1f)
    {
        b.Rect(x0, y0, x1, y1, bg.r, bg.g, bg.b, bg.a * alpha);
        float arrowW = atlas.TryGet(0x25BE, out Glyph ag) ? ag.Advance : 0f;
        UiText.Draw(b, atlas, value, x0 + 4f, baseline, textRgb, x1 - arrowW - 6f, alpha);
        if (arrowW > 0f)
            b.Glyph(ag, x1 - arrowW - 3f, baseline, ((textRgb >> 16) & 0xFF) / 255f, ((textRgb >> 8) & 0xFF) / 255f, (textRgb & 0xFF) / 255f, 0.8f * alpha);
    }

    /// <summary>Draw the open list on top of everything: frame, body, the selected + hovered fills, the
    /// visible items (clipped to the box) and the scrollbar.</summary>
    public void Draw(VertexBatch b, GlyphAtlas atlas, float baseDrop,
                     (float r, float g, float b, float a) bg, (float r, float g, float b, float a) active, uint textRgb)
    {
        if (!Open) return;
        float x0 = List.X, y0 = List.Y, x1 = List.X + List.W, y1 = List.Y + List.H;
        float fr = ((FrameRgb >> 16) & 0xFF) / 255f, fg = ((FrameRgb >> 8) & 0xFF) / 255f, fb = (FrameRgb & 0xFF) / 255f;
        b.Rect(x0 - 1f, y0 - 1f, x1 + 1f, y1 + 1f, fr, fg, fb, FrameA);
        b.Rect(x0, y0, x1, y1, bg.r, bg.g, bg.b, 0.98f);
        float textClip = x1 - (List.Overflows ? ScrollColumn.BarW : 0f) - 2f;
        float vpad = (ItemH - atlas.LineHeight) / 2f;
        b.ClipY(y0, y1);
        for (int i = 0; i < Choices.Length; i++)
        {
            float top = ItemTop(i);
            if (top + ItemH <= y0 || top >= y1) continue;
            if (i == Selected) b.Rect(x0, top, textClip + 2f, top + ItemH, active.r, active.g, active.b, active.a);
            else if (i == Hover) b.Rect(x0, top, textClip + 2f, top + ItemH, active.r, active.g, active.b, active.a * 0.55f);
            UiText.Draw(b, atlas, Choices[i], x0 + 4f, top + baseDrop + vpad, textRgb, textClip);
        }
        b.ClearClip();
        List.Draw(b, active);
    }

    /// <summary>Off-device proof: a 27-entry list opens capped at <see cref="MaxVisible"/> rows, scrolls by wheel
    /// and thumb, picks the item under the pointer offset-correctly, closes on pick and on an outside press,
    /// flips above a short viewport, and draws without allocating.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"Dropdown.SelfCheck FAIL: {w}"); } }

        var sizes = new string[27];
        for (int i = 0; i < 27; i++) sizes[i] = (6 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var d = new Dropdown();
        const float ih = 24f;
        E(!d.MouseDown(0f, 0f, out _) && !d.MouseMove(0f, 0f) && !d.MouseWheel(1), "closed: nothing consumed");

        d.OpenAt(sizes, 0, "font", 100f, 200f, 220f, 120f, ih, 1000f);
        E(d.Open && d.Y == 220f && d.H == MaxVisible * ih, "27 entries open below the anchor at 8 visible rows");
        E(d.List.Overflows && d.List.Offset == 0f, "the list overflows and starts at the top for item 0");
        E(d.ItemAt(110f, 220f + ih * 2 + 1f) == 2, "item hit-test at offset 0");
        E(d.MouseWheel(-1) && d.List.Offset == ih, "one notch scrolls one item");
        E(d.ItemAt(110f, 220f + ih * 2 + 1f) == 3, "item hit-test follows the scroll offset");
        E(d.ItemAt(100f + 120f - 2f, 230f) == -1, "the gutter is not an item");
        // Thumb drag to the bottom -> the last item is in view.
        E(d.List.ThumbRect(out float tt, out float th), "thumb shown");
        E(d.MouseDown(100f + 120f - 2f, tt + 1f, out int p0) && p0 < 0 && d.Open && d.Dragging, "thumb press grabs, picks nothing, stays open");
        d.MouseMove(100f + 120f - 2f, tt + 1f + d.H);
        E(d.MouseUp() && d.List.Offset == d.List.MaxOffset, "thumb dragged to the end shows the tail");
        E(d.ItemAt(110f, d.Y + d.H - 1f) == 26, "the last item sits at the list bottom");
        E(d.MouseMove(110f, d.Y + d.H - 1f) && d.Hover == 26, "hover tracks the item under the pointer");
        E(!d.MouseMove(111f, d.Y + d.H - 1f), "motion within the same item reports no change");
        E(d.MouseDown(110f, d.Y + d.H - 1f, out int picked) && picked == 26 && d.Selected == 26 && !d.Open, "picking the last item closes with its index");

        // Outside press closes, consumed, nothing picked.
        d.OpenAt(sizes, 26, "font", 100f, 200f, 220f, 120f, ih, 1000f);
        E(d.List.Offset == d.List.MaxOffset, "opening on the last item scrolls it into view");
        E(d.MouseDown(5f, 5f, out int none) && none < 0 && !d.Open, "outside press closes, consumed, picks nothing");

        // Short viewport: the list flips above the anchor.
        d.OpenAt(sizes, 0, "font", 100f, 200f, 220f, 120f, ih, 300f);
        E(d.Open && System.Math.Abs(d.Y - (200f - MaxVisible * ih)) < 0.01f, "the list flips above the anchor when it would run past the clamp");
        d.Close();

        // Short list: no scroll, height = n rows.
        var three = new[] { "a", "b", "c" };
        d.OpenAt(three, 1, null, 0f, 0f, 20f, 80f, ih);
        E(d.H == 3 * ih && !d.List.Overflows, "3 entries: 3 rows, no scrollbar");
        E(d.MouseDown(10f, 20f + ih * 2 + 1f, out int c) && c == 2, "pick the third");

        // Draw allocates nothing.
        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        for (char ch = '0'; ch <= '9'; ch++) atlas.SetTestGlyph(ch, 6f);
        var b = new VertexBatch(atlas.SolidU, atlas.SolidV);
        d.OpenAt(sizes, 5, "font", 100f, 200f, 220f, 120f, ih, 1000f);
        d.MouseMove(110f, 230f);
        d.Draw(b, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), (0.2f, 0.3f, 0.5f, 1f), 0xFFFFFF);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { b.Clear(); d.Draw(b, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), (0.2f, 0.3f, 0.5f, 1f), 0xFFFFFF); }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        E(delta == 0, $"100 open-list draws allocate nothing ({delta} bytes)");
        E(b.VertexCount > 0, "the draw emitted geometry");
        d.Close();

        log.Info(ok ? "Dropdown.SelfCheck: passed" : "Dropdown.SelfCheck: FAILED");
    }
}
