using CX.Graphics;

namespace CX.Alloy;

/// <summary>An owned pass's fixed-function preamble as a value, so "what state does this pass run in?" is a
/// declaration rather than a sequence of <c>Gl.Enable</c>/<c>Disable</c> lines an omission can hide in.
/// Deliberately dumb: <see cref="Apply"/> issues every call every time, because a cache here would be a third
/// copy to disagree with the context after a vanilla draw ran between two Alloy passes.</summary>
internal readonly struct PassState
{
    /// <summary><see cref="Cull"/> is the pass's DEFAULT, not its whole answer: the world batch loops toggle
    /// face culling per batch off the material's <c>twosided</c> keyword.</summary>
    public readonly bool Depth, DepthWrite, Blend, Cull, Scissor;
    /// <summary>Tri-state on purpose: null means "leave sRGB encoding as the caller found it". A pass that
    /// renders into the ENGINE's target inherits that flag from the frame it was entered with, so a preamble
    /// forcing it either way would encode the frame twice or not at all, silently. Only a pass that owns its
    /// own target says so.</summary>
    public readonly bool? Srgb;
    /// <summary>The <c>glBlendFunc</c> factors, issued only when <see cref="Blend"/> is set. The beams pass
    /// is the one caller; every other preamble disables blending.</summary>
    public readonly uint BlendSrc, BlendDst;

    public PassState(bool depth, bool depthWrite, bool blend = false, bool cull = false,
                     bool scissor = false, bool? srgb = null, uint blendSrc = 0, uint blendDst = 0)
    {
        Depth = depth; DepthWrite = depthWrite; Blend = blend; Cull = cull;
        Scissor = scissor; Srgb = srgb; BlendSrc = blendSrc; BlendDst = blendDst;
    }

    /// <summary>Put the GL context into this state. Hot: called once per pass, allocates nothing. It sets
    /// state and never restores it -- each pass hand-rolls its own hand-back.</summary>
    public unsafe void Apply()
    {
        if (Depth) Gl.Enable(Gl.DEPTH_TEST); else Gl.Disable(Gl.DEPTH_TEST);
        Gl.DepthMask(DepthWrite ? (byte)1 : (byte)0);
        if (Blend) { Gl.Enable(Gl.BLEND); Gl.BlendFunc(BlendSrc, BlendDst); } else Gl.Disable(Gl.BLEND);
        if (Cull) Gl.Enable(Gl.CULL_FACE); else Gl.Disable(Gl.CULL_FACE);
        if (Scissor) Gl.Enable(Gl.SCISSOR_TEST); else Gl.Disable(Gl.SCISSOR_TEST);
        if (Srgb == true) Gl.Enable(Gl.FRAMEBUFFER_SRGB); else if (Srgb == false) Gl.Disable(Gl.FRAMEBUFFER_SRGB);
    }
}
