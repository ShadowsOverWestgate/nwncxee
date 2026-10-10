namespace CX.Alloy;

/// <summary>
/// The depth buffer <see cref="PassId.DepthPrepass"/> fills, published rather than kept private to the colour
/// pass because water's screen-space march and the ambient occlusion want exactly this buffer. It is Alloy's
/// own depth texture (<see cref="PostChain.DepthTex"/>), so <see cref="IsTexture"/> is true whenever it is
/// published; sampling it while the colour bracket has it attached is the feedback loop a consumer's copy avoids.
/// </summary>
internal static class DepthResource
{
    /// <summary>GL object name of the depth attachment, 0 for the default framebuffer's.</summary>
    internal static uint Name;
    /// <summary>False = renderbuffer or default framebuffer: complete, but a consumer cannot sample it.</summary>
    internal static bool IsTexture;
    /// <summary>The viewport rect the prepass filled, in the attachment's pixels. This is deliberately NOT
    /// the attachment's own extent -- the two coincide today but nothing enforces it, and a consumer reading
    /// screen-space depth wants the filled region anyway. Whoever needs the allocation size can query
    /// <see cref="Name"/> for it; publishing a guess would be worse than publishing nothing.</summary>
    internal static int FilledX, FilledY, FilledWidth, FilledHeight;
    /// <summary><see cref="AlloyRenderer.PassGen"/> of the pass whose prepass last filled it; -1 = never.</summary>
    internal static int Gen = -1;

    internal static unsafe void Publish(int type, int name, int* viewport, int gen)
    {
        Name = (uint)name;
        IsTexture = type != (int)CX.Graphics.Gl.RENDERBUFFER && name != 0;
        FilledX = viewport[0]; FilledY = viewport[1];
        FilledWidth = viewport[2]; FilledHeight = viewport[3];
        Gen = gen;
    }

    /// <summary>Depth for <paramref name="gen"/> is present and complete. Sampling it additionally needs
    /// <see cref="IsTexture"/>.</summary>
    internal static bool Filled(int gen) => Gen == gen;

    /// <summary>The buffer's contents can no longer be trusted (context loss, the pass standing down).</summary>
    internal static void Revoke() => Gen = -1;
}
