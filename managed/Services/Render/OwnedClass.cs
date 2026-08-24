using Sowx.Framework;

namespace Sowx.Services.Render;

/// <summary>
/// #126 (map #68, spec #114 M3, ADR 0011 "Transparency: classify, don't sort everything"): which owned pass
/// a part draws in. The engine sorts nothing inside a draw bucket (#103) and survives on a 0.2 alpha cutout,
/// so the owned pass PICKS an order rather than reproducing one -- and the pick is this enum.
/// <para>Declaration order IS draw order, and the staging arrays are sorted on it, so each pass owns a
/// contiguous batch range.</para>
/// </summary>
internal enum OwnedClass
{
    /// <summary>Genuinely opaque, and the tilefade caps -- the engine draws bucket 7 with blending and the
    /// alpha discard both off, so a cap is an opaque draw whatever its material declares.</summary>
    Opaque = 0,
    /// <summary>The 0.2-discard majority: non-opaque only because the diffuse texture carries an alpha
    /// channel. Draws WITH the opaque set -- depth-writing, unsorted, alpha-tested -- which is what makes it
    /// order-independent by construction rather than by luck.</summary>
    Cutout = 1,
    /// <summary>Genuine alpha: the material declares <c>transparency</c>, or the part carries an alpha
    /// multiplier below 1 (which is also what a fading tile becomes for the frames it fades). Sorted
    /// back-to-front per part, depth writes OFF.</summary>
    Blended = 2,
    /// <summary>#129: water. Not a blend mode at all -- a SHADING class, the one the engine routes on
    /// <c>Material::HasBumpyShinyMap</c> (with <c>g_nEnableBumpyWater</c>) or the <c>.mtr</c>'s
    /// <c>sample_framebuffer 2</c>, both of which land in draw bucket 6
    /// (<c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880). It sits between blended and additive because
    /// that is where its INPUT is ready: it samples a copy of the finished frame, and ADR 0011 puts the one
    /// copy after the blended class (so a fading tile is in it) and before additive (so torch glow is not,
    /// exactly as in vanilla -- see <see cref="PassId.FbCopy"/>).
    /// <para>Depth writes stay ON, which is what separates it from <see cref="Blended"/>: the engine draws
    /// bucket 6 under <c>Enable(0)</c>/<c>Disable(1)</c> and leaves the depth mask alone, and additive drawn
    /// after it must be able to sit in front of the surface rather than be painted over.</para></summary>
    Water = 3,
    /// <summary>Destination blend <c>GL_ONE</c> -- the engine's own bucket-4 test. No depth write, and no
    /// sort needed: addition commutes.</summary>
    Additive = 4,
}

internal static class OwnedClasses
{
    internal const int Count = 5;

    /// <summary>Classify a part from the same terms the engine's own predicates read -- <c>Material</c>'s
    /// destination blend factor (bucket 4's test), <c>PartTriMesh::GetIsOpaque</c>, and the two remaining
    /// reasons <c>Material::GetIsOpaque</c> @0x4c6190 has for saying "not opaque": the SharedMaterial's
    /// <c>transparency</c> keyword and a sub-1 alpha. Whatever is left is non-opaque because the DIFFUSE
    /// carries alpha, which is the cutout majority.
    /// <para>Deliberately not classified by draw bucket: bucket membership is recomputed every frame
    /// (a tile starts fading and moves to 8, a wall starts dissolving and moves to 1) while these terms are
    /// material state. The one bucket that overrides is the tilefade cap -- see <see cref="OwnedClass.Opaque"/>
    /// -- and the one per-frame overlay is the fade, which the draw loop applies live.</para></summary>
    /// <para>#129: <paramref name="water"/> comes first of all, because it is the engine's own precedence --
    /// <c>AddPartsTreeToDrawBuckets</c> tests bumpy-shiny before it looks at the blend pair, and
    /// <c>sample_framebuffer == 2</c> before it looks at anything else on the SharedMaterial. Water materials
    /// do declare a blend pair and are never opaque, so testing it later would file a sea in the blended
    /// sort.</para>
    internal static OwnedClass Of(bool water, bool additive, bool opaque, bool declaredTransparent, bool alphaBelowOne)
        => water ? OwnedClass.Water
         : additive ? OwnedClass.Additive
         : opaque ? OwnedClass.Opaque
         : declaredTransparent || alphaBelowOne ? OwnedClass.Blended
         : OwnedClass.Cutout;

    /// <summary>Does this class draw with <c>GL_BLEND</c> on, using the material's own blend pair?
    /// <para>Everything except plain opaque, CUTOUT INCLUDED -- and that inclusion is not a detail. The
    /// engine draws bucket 1 under <c>Enable(0)</c> + <c>Enable(1)</c> (#103's pass table): blending on AND
    /// the 0.2 discard, together. On binary-alpha content the blend is a no-op and the discard does all the
    /// work, which is why "cutout = alpha test" reads as the whole story -- but NWN foliage is not binary.
    /// Measured on the shipped ivy textures: 45-69% of texels sit between alpha 0.2 and 0.9, i.e. above the
    /// discard and below opaque. Drawn without blending those become SOLID, and a soft-edged vine turns into
    /// a slab of whatever its dim texels shade to.</para>
    /// <para>What the split still means, and what ADR 0011 actually decided: cutout keeps DEPTH WRITE and
    /// needs no sort. That is the order-independence -- not the absence of blending.</para></summary>
    internal static bool Blends(int cls) => cls != (int)OwnedClass.Opaque;

    /// <summary>The engine's <c>fAlphaDiscardValue</c> for this class (<c>inc_common.shd:593</c>,
    /// <c>GLRender::SetUniforms</c> @0x4ccbf0: 0.2 when <c>Enable(1)</c> is on, -1.0 when it is not).
    /// The pass list maps onto it exactly: buckets 1 and 8 draw with the discard on, 0/4/7 with it off.
    /// A blended part keeps the cutout too -- bucket 8 is drawn under <c>Enable(1)</c>, and a fading tile
    /// is a bucket-8 part.</summary>
    /// <para>#129: water joins the no-discard side. The engine draws bucket 6 under <c>Disable(1)</c>
    /// (<c>Scene::RenderSinglePass</c> @0x46e5f0), and a water fragment's alpha is coverage the shader
    /// computes rather than anything the diffuse carries -- a 0.2 test on it would punch holes in shallows.</para>
    internal static float Discard(int cls) => cls == (int)OwnedClass.Opaque || cls == (int)OwnedClass.Additive
        || cls == (int)OwnedClass.Water ? -1f : 0.2f;

    /// <summary>Off-device: the classifier's precedence, which is the one thing about it that cannot be read
    /// off a single line. Additive beats opacity (an additive material is never opaque anyway, but the
    /// engine tests the blend factor FIRST and so must we, or additive content lands in the blended sort and
    /// pays one); opacity beats both transparency terms; and a texture-alpha part with nothing declared is a
    /// cutout, which is the majority of NWN content and the reason the missing sort survives at all.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"OwnedClasses.SelfCheck FAIL: {w}"); } }

        E(Of(true, true, true, true, true) == OwnedClass.Water, "water beats every other term");
        E(Of(false, true, true, false, false) == OwnedClass.Additive, "additive beats opaque");
        E(Of(false, true, false, true, true) == OwnedClass.Additive, "additive beats the transparency terms");
        E(Of(false, false, true, true, true) == OwnedClass.Opaque, "an opaque part is opaque whatever it declares");
        E(Of(false, false, false, true, false) == OwnedClass.Blended, "declared transparency blends");
        E(Of(false, false, false, false, true) == OwnedClass.Blended, "a sub-1 part alpha blends");
        E(Of(false, false, false, false, false) == OwnedClass.Cutout, "texture alpha alone is a cutout");

        E(!Blends((int)OwnedClass.Opaque), "plain opaque does not blend");
        E(Blends((int)OwnedClass.Cutout), "a cutout DOES blend -- the engine draws bucket 1 with both");
        E(Blends((int)OwnedClass.Blended) && Blends((int)OwnedClass.Additive), "blended and additive do");
        E(Blends((int)OwnedClass.Water), "and so does water -- the engine draws bucket 6 under Enable(0)");
        E(Discard((int)OwnedClass.Cutout) == 0.2f && Discard((int)OwnedClass.Blended) == 0.2f,
          "the engine's 0.2 cutout, on the two classes it draws under Enable(1)");
        E(Discard((int)OwnedClass.Opaque) < 0f && Discard((int)OwnedClass.Additive) < 0f
          && Discard((int)OwnedClass.Water) < 0f,
          "and no discard at all on the three it does not");
        // Draw order is the enum's own order, and the staging sort is what depends on it: opaque and cutout
        // share a pass, so they must be adjacent AND first.
        E((int)OwnedClass.Opaque == 0 && (int)OwnedClass.Cutout == 1 && (int)OwnedClass.Blended == 2
          && (int)OwnedClass.Water == 3 && (int)OwnedClass.Additive == 4, "declaration order IS draw order");
        // #129: and water sits BETWEEN them, which is the whole of its scheduling. The framebuffer copy it
        // reads is taken at this boundary: everything below it is in the copy, everything above is not.
        E((int)OwnedClass.Water > (int)OwnedClass.Blended && (int)OwnedClass.Water < (int)OwnedClass.Additive,
          "water draws after the blended class (fade is in its copy) and before additive (glow is not)");
        E(Count == System.Enum.GetValues<OwnedClass>().Length, "Count sizes the per-class arrays");

        log.Info(ok ? "OwnedClasses.SelfCheck: passed" : "OwnedClasses.SelfCheck: FAILED");
    }
}
