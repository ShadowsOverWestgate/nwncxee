namespace CX.Alloy;

/// <summary>The bake's two programs. The bake stage draws a charted mesh with its chart UV as the position
/// (UV2 -> rect offset/scale -> page NDC) and its world position and vertex normal interpolated, then runs
/// the colour pass's own per-light body -- <c>CxPointLight</c> / <c>CxPointDiffuse</c> / <c>CxLocalShadow</c>
/// from <see cref="AlloyLights.FragmentLib"/> and <see cref="AlloyLights.LocalShadowLib"/> -- against the
/// scratch cube in slot 0, so a baked texel is the lit stage's own arithmetic (#666). The finish stage folds a
/// page's scratch pair into the resident pair.</summary>
internal static partial class LightmapBake
{
    internal static string BakeVs => ShaderSource.Get("LightmapBake.BakeVs.glsl");

    /// <summary>Conservative coverage: each triangle's edges are pushed out by <c>CX_LM_PUSH</c> page texels
    /// (mitre-clamped at sharp corners), so a face thinner than a texel still owns the texels it crosses --
    /// at density 4 a window recess a hand deep had no texel centre inside it and baked black (#674 live).
    /// The pushed vertices carry their barycentrics against the ORIGINAL triangle; the fragment stage clamps
    /// those back inside it, so a pushed texel shades the nearest point ON the face, never a point in the air
    /// or inside the wall behind it.</summary>
    internal static string BakeGs => ShaderSource.Get("LightmapBake.BakeGs.glsl");

    internal static string BakeFs => ShaderSource.Get("LightmapBake.BakeFs.glsl");

    /// <summary>Per page texel: a covered texel (alpha > 0) converts as is; an uncovered one within two of a
    /// covered one takes its nearest covered neighbour (the dilation that keeps bilinear reads off the
    /// gutter); anything else discards, so cache-hit rects already resident on the page stay untouched.</summary>
    internal static string FinishFs => ShaderSource.Get("LightmapBake.FinishFs.glsl");
}
