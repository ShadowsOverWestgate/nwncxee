using System;

namespace Sowx.Alloy;

/// <summary>#82: one part offered to the Alloy world pass at enrollment. Everything the Alloy draw needs
/// that is stable for the lifetime of the ownership -- the per-frame values (texture id, index range, model
/// matrix) are re-read live in the draw loop, never cached here.</summary>
internal struct AlloyCandidate
{
    /// <summary>The PartTriMesh*, and the identity pair the draw loop revalidates against.</summary>
    public IntPtr Tri, Node, Mat;
    public IntPtr Vdo;
    /// <summary>#84 batch sort key: the diffuse CAurTexture* seen at capture. Used ONLY as an opaque grouping
    /// hint -- never dereferenced (borrowed pointer) and never trusted as the live texture, which the draw
    /// loop re-fetches. Material* would be useless here: each part owns its own Material instance, so pointer
    /// identity shatters every group to size 1 (PartTable's group key uses material CONTENT for the same reason).</summary>
    public IntPtr Tex0;
    public int Item, Prim;
    /// <summary>part+0xb0 as raw bits: the texture-rotation angle in degrees, compared for change.</summary>
    public int RotBits;
    /// <summary>Matrix-class part (node+0x80 &amp; 0x1e): needs the model matrix at node+0x18 in its MVP.</summary>
    public bool Mx;
    /// <summary>The engine would draw this part LIT. Ghidra <c>PartTriMesh::MatStatic</c> @0x450560 and
    /// <c>RenderFlat</c> @0x450eb0 agree on the test: lighting cap 5 on (a per-PASS state, checked at draw
    /// time, not here) and <c>mat+0x98</c> clear -- either one false and the engine sends flat white, which
    /// is the fullbright variant. False here means the material itself opted out.</summary>
    public bool Lit;
    /// <summary>#121: the material declares an env map (<c>Material::HasEnvMap</c>, mat+0x40). No texture is
    /// carried with it -- the per-area IBL probe replaces what it held -- but the flag itself is what the
    /// engine's own shaders read as "this is shiny" on 2002 content, and it drives the same specularity and
    /// roughness synthesis here.</summary>
    public bool Env;
    /// <summary>#123: the <see cref="ShaderFeature"/> this part's SharedMaterial asked for by name, 0 for
    /// none. Stable for the material's lifetime -- the .mtr is parsed once at load.</summary>
    public int Feat;
    /// <summary>#126: the <see cref="AlloyClass"/> this part draws in -- which Alloy pass, with what depth
    /// and blend state. Set even when the candidate is then refused, so the refusal can be counted against
    /// the class it would have joined (the blended class is all-owned-or-none).</summary>
    public int Cls;
    /// <summary>The draw bucket this part was captured in. Kept for the one-owner AUDIT, not for the draw:
    /// bucket membership is recomputed every frame, so it is a diagnostic hint rather than a fact -- but
    /// "which class of geometry is going unsuppressed" is not answerable without it.</summary>
    public int Bucket;
    /// <summary>#128: the <c>Gob</c> this part instances off (part+0x50), or zero for static geometry.
    /// Published even when the candidate is REFUSED, and by every gate from the part load onward: creature
    /// colour is all-owned-or-none per instance (#107), so enrollment has to be able to name the instance a
    /// refusal belongs to and drop the rest of it with the refused part.</summary>
    public IntPtr Gob;
    /// <summary>#336: the LOD tree this part belongs to, when its gob has alternates; the route skips the
    /// part while <c>GobPartTree</c> names another. Zero for a part with no LOD question.</summary>
    public IntPtr LodRoot;
    /// <summary>#126: the material's blend pair as <c>Aurora::AuroraBlendModes</c> (mat+0x90/+0x94), which
    /// the blended and additive passes translate to <c>glBlendFunc</c>. Part of the batch key: one multidraw
    /// carries one blend state.</summary>
    public int Src, Dst;
    /// <summary>A wash: a graded-alpha diffuse re-filed from Cutout to Blended (<c>gradedblend</c>). Draws with the
    /// depth mask OFF, because a wash occludes nothing -- vanilla gets the same picture from the model's
    /// <c>transparencyhint</c> child order, which Alloy's distance sort does not reproduce.</summary>
    public bool Wash;
    /// <summary>#292: the SharedMaterial's <c>twosided</c> keyword -- this part draws with face culling OFF.
    /// False (cull back faces) is the engine's default and what a part with no material gets. Part of the
    /// batch key: culling is GL state, and one multidraw cannot vary it.</summary>
    public bool TwoSided;
}
