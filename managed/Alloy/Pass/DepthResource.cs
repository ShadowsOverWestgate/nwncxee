namespace CX.Alloy;

/// <summary>
/// #119 (map #68, spec #114 M3): the depth buffer <see cref="PassId.DepthPrepass"/> fills, published rather
/// than kept private to the colour pass. ADR 0011 makes this a contract because two future readers -- water's
/// screen-space depth march and M4's ambient occlusion -- want exactly this buffer, and each building its own
/// means building it three times.
/// <para>Since #214 the buffer is ALLOY's own depth texture (<see cref="PostChain.DepthTex"/>), so
/// <see cref="IsTexture"/> is true whenever it is published at all -- it stays published beside the name
/// rather than assumed by consumers, because the pre-#214 shape (the engine's attachment, which could be a
/// renderbuffer and so complete but not sampleable) is what the flag was written for and nothing about a
/// consumer's side of the contract changed. Sampling it while the colour bracket has it attached is still
/// the feedback loop <see cref="SceneDepth"/> exists to avoid: consumers take a copy.</para>
/// <para>Read it as <c>DepthResource.Filled(AlloyRenderer.PassGen)</c>: the generation is what says the
/// prepass ran for the pass you are in, not merely that it ran once. Reflection subframes and GUI 3D panels
/// are their own passes and mostly do not fill it.</para>
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
