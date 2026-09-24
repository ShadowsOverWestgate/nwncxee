namespace CX.Ui;

/// <summary>The settings-panel look (<see cref="KnobPanel"/>): the #560 prototype's palette (the chat window's
/// chrome) and the fill / outline primitives. Brought into scope with <c>using static</c>.</summary>
internal static class PanelChrome
{
    internal const uint PanelBg = 0x0d0f17, Border = 0x2a3350, HeaderBg = 0x1a1f29, CatBg = 0x0a0c14, SubBg = 0x0f121c,  // not-an-offset: a colour
                        ColBorder = 0x1f2740, NavOn = 0x192040, Accent = 0x335785, Accent2 = 0x4a78b4, Input = 0x050508,
                        Selected = 0x38669e, Tx = 0xd8dee9, Tx2 = 0x8a93a6, Tx3 = 0x5a6274, Grip = 0xb8c0cc, Hi = 0xeaf0f8,
                        ToggleOff = 0x2a2f3d, Frame = 0x3a4460, Frame2 = 0x2f3850, Track = 0x1e2333, ListBg = 0x05060a,
                        MasterBg = 0x10141f, StripBg = 0x1a160e, StripBorder = 0x3a3020, Warn = 0xd9a35a, Off = 0x6c7385,
                        BadgeFrame = 0x3a3f4d, Ok = 0x7fb87a;

    internal static (float r, float g, float b, float a) C(uint rgb, float a = 1f)
        => (((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    internal static void Fill(VertexBatch b, float x0, float y0, float x1, float y1, uint rgb, float a = 1f)
    {
        var c = C(rgb, a);
        b.Rect(x0, y0, x1, y1, c.r, c.g, c.b, c.a);
    }

    /// <summary>A 1 px frame just inside the rect.</summary>
    internal static void Outline(VertexBatch b, float x0, float y0, float x1, float y1, uint rgb, float a = 1f)
    {
        Fill(b, x0, y0, x1, y0 + 1f, rgb, a); Fill(b, x0, y1 - 1f, x1, y1, rgb, a);
        Fill(b, x0, y0, x0 + 1f, y1, rgb, a); Fill(b, x1 - 1f, y0, x1, y1, rgb, a);
    }
}
