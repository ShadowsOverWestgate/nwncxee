namespace CX.Ui;

/// <summary>One baked glyph: atlas tex-coords, pixel size, bearings, and scaled advance.</summary>
public struct Glyph
{
    public float U0, V0, U1, V1;
    public int W, H, XOff, YOff;   // XOff/YOff = box offset from the pen baseline (YOff negative = above)
    public float Advance;
}
