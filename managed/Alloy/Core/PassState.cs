using CX.Graphics;

namespace CX.Alloy;

/// <summary>#207: an owned pass's fixed-function preamble, as a value. Every pass used to open with the same
/// six or seven <c>Gl.Enable</c>/<c>Disable</c> lines in its own order, which made "what state does this pass
/// run in?" a question you answered by reading a sequence instead of a declaration -- and made an omission
/// (the one <c>Disable</c> a copied preamble dropped) invisible.
/// <para>Deliberately dumb: <see cref="Apply"/> issues every call, every time. There is NO state cache and no
/// redundant-call elimination behind it. The driver already shadow-caches this state and so does the engine;
/// a third copy is a third thing that can disagree with the GL context after a vanilla draw runs between two
/// of our passes -- which is exactly the situation every Alloy pass is in.</para>
/// <para>#292: <c>Cull</c> is the pass's DEFAULT, not its whole answer. The world batch loops (colour and
/// depth prepass alike) toggle face culling per batch off the material's <c>twosided</c> keyword, which is
/// where the engine keeps the decision -- this states what a pass with no batch of its own draws in. Every
/// pass that draws WORLD geometry now says which it wants rather than inheriting a default; the fullscreen
/// ones (the post chain's quads) have no faces to cull and keep the default.</para>
/// <para>Particles are the one owned pass with no preamble to declare: their blend, depth-mask and cull are
/// per-GROUP state chosen inside the draw loop (punch-through vs blended vs additive), so a declaration
/// hoisted in front of that loop would state something untrue for most of the pass. Every pass's trailing
/// hand-back stays hand-rolled too -- this struct sets state, it does not restore it.</para>
/// <para>It carries only what the preambles actually share. <c>glDepthFunc</c>, viewport, framebuffer and
/// texture-unit setup stay at the call site: they are per-pass decisions, not a common preamble, and growing
/// the struct to hold them would turn a declaration back into a sequence.</para></summary>
internal readonly struct PassState
{
    public readonly bool Depth, DepthWrite, Blend, Cull, Scissor;
    /// <summary>Tri-state on purpose: null means "leave sRGB encoding as the caller found it". A pass that
    /// renders into the ENGINE's target inherits that flag from the frame it was entered with (the post
    /// chain's composite is restored to it by name, one line before it runs), so a preamble that forced it
    /// either way would encode the frame twice or not at all -- silently, which is #132's whole failure
    /// mode. Only a pass that owns its own target says so.</summary>
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

    /// <summary>Put the GL context into this state. Hot: called once per pass, allocates nothing.</summary>
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
