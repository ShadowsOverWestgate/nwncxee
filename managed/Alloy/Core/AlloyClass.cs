using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// #126 (map #68, spec #114 M3, ADR 0011 "Transparency: classify, don't sort everything"): which Alloy pass
/// a part draws in. The engine sorts nothing inside a draw bucket (#103) and survives on a 0.2 alpha cutout,
/// so the Alloy pass PICKS an order rather than reproducing one -- and the pick is this enum.
/// <para>Declaration order IS draw order, and the staging arrays are sorted on it, so each pass owns a
/// contiguous batch range.</para>
/// </summary>
internal enum AlloyClass
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

internal static class AlloyClasses
{
    internal const int Count = 5;

    /// <summary>Classify a part from the same terms the engine's own predicates read -- <c>Material</c>'s
    /// destination blend factor (bucket 4's test), <c>PartTriMesh::GetIsOpaque</c>, and the two remaining
    /// reasons <c>Material::GetIsOpaque</c> @0x4c6190 has for saying "not opaque": the SharedMaterial's
    /// <c>transparency</c> keyword and a sub-1 alpha. Whatever is left is non-opaque because the DIFFUSE
    /// carries alpha, which is the cutout majority.
    /// <para>Deliberately not classified by draw bucket: bucket membership is recomputed every frame
    /// (a tile starts fading and moves to 8, a wall starts dissolving and moves to 1) while these terms are
    /// material state. The one bucket that overrides is the tilefade cap -- see <see cref="AlloyClass.Opaque"/>
    /// -- and the one per-frame overlay is the fade, which the draw loop applies live.</para></summary>
    /// <para>#129: <paramref name="water"/> comes first of all, because it is the engine's own precedence --
    /// <c>AddPartsTreeToDrawBuckets</c> tests bumpy-shiny before it looks at the blend pair, and
    /// <c>sample_framebuffer == 2</c> before it looks at anything else on the SharedMaterial. Water materials
    /// do declare a blend pair and are never opaque, so testing it later would file a sea in the blended
    /// sort.</para>
    internal static AlloyClass Of(bool water, bool additive, bool opaque, bool declaredTransparent, bool alphaBelowOne)
        => water ? AlloyClass.Water
         : additive ? AlloyClass.Additive
         : opaque ? AlloyClass.Opaque
         : declaredTransparent || alphaBelowOne ? AlloyClass.Blended
         : AlloyClass.Cutout;

    /// <summary>Does this class draw with <c>GL_BLEND</c> on, using the material's own blend pair?
    /// <para>The three sorted classes, and NOT cutout. #126 put cutout on the blending side, reasoning that
    /// the engine draws bucket 1 under <c>Enable(0)</c> + <c>Enable(1)</c> (#103's pass table) and that NWN
    /// foliage is not binary -- 45-69% of a shipped ivy texture's texels sit between alpha 0.2 and 0.9, and
    /// drawn unblended those become SOLID. Both halves are true and the conclusion was still wrong, because
    /// cutout also keeps its DEPTH WRITE, and #357 is what those two together look like: a graded texel lets
    /// the destination through AND stamps depth, so every leaf edge is a hole with the sky in it that then
    /// rejects the leaves, branches and trees drawn behind it. The class is unsorted by construction, so
    /// which surface wins each fringe pixel is submission order -- a tree cutting a sky-coloured outline
    /// through itself.</para>
    /// <para>Partial transparency and depth writing cannot both be had at this price. Grass has drawn under
    /// exactly this rule since #127 ("no grass fragment is ever partially transparent AND depth-writing")
    /// and this is the rest of the class catching up: a texel over the discard is opaque, which is what an
    /// alpha test has always meant. The soft vine edge is the thing given up, and ADR 0011 already names
    /// what buys it back -- alpha-to-coverage, which needs the multisampled target the Alloy frame does not
    /// yet bind.
    /// <para>ponytail: hard edges on graded foliage. Upgrade path is A2C on an MSAA target, not blending
    /// this class back on.</para></para></summary>
    internal static bool Blends(int cls) => cls != (int)AlloyClass.Opaque && cls != (int)AlloyClass.Cutout;

    /// <summary>The engine's <c>fAlphaDiscardValue</c> for this class (<c>inc_common.shd:593</c>,
    /// <c>GLRender::SetUniforms</c> @0x4ccbf0: 0.2 when <c>Enable(1)</c> is on, -1.0 when it is not).
    /// The pass list maps onto it exactly: buckets 1 and 8 draw with the discard on, 0/4/7 with it off.
    /// A blended part keeps the cutout too -- bucket 8 is drawn under <c>Enable(1)</c>, and a fading tile
    /// is a bucket-8 part.</summary>
    /// <para>#129: water joins the no-discard side. The engine draws bucket 6 under <c>Disable(1)</c>
    /// (<c>Scene::RenderSinglePass</c> @0x46e5f0), and a water fragment's alpha is coverage the shader
    /// computes rather than anything the diffuse carries -- a 0.2 test on it would punch holes in shallows.</para>
    internal static float Discard(int cls) => cls == (int)AlloyClass.Opaque || cls == (int)AlloyClass.Additive
        || cls == (int)AlloyClass.Water ? -1f : 0.2f;

    /// <summary>Off-device: the classifier's precedence, which is the one thing about it that cannot be read
    /// off a single line. Additive beats opacity (an additive material is never opaque anyway, but the
    /// engine tests the blend factor FIRST and so must we, or additive content lands in the blended sort and
    /// pays one); opacity beats both transparency terms; and a texture-alpha part with nothing declared is a
    /// cutout, which is the majority of NWN content and the reason the missing sort survives at all.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyClasses.SelfCheck");

        check.Expect(Of(true, true, true, true, true) == AlloyClass.Water, "water beats every other term");
        check.Expect(Of(false, true, true, false, false) == AlloyClass.Additive, "additive beats opaque");
        check.Expect(Of(false, true, false, true, true) == AlloyClass.Additive, "additive beats the transparency terms");
        check.Expect(Of(false, false, true, true, true) == AlloyClass.Opaque, "an opaque part is opaque whatever it declares");
        check.Expect(Of(false, false, false, true, false) == AlloyClass.Blended, "declared transparency blends");
        check.Expect(Of(false, false, false, false, true) == AlloyClass.Blended, "a sub-1 part alpha blends");
        check.Expect(Of(false, false, false, false, false) == AlloyClass.Cutout, "texture alpha alone is a cutout");

        check.Expect(!Blends((int)AlloyClass.Opaque), "plain opaque does not blend");
        check.Expect(!Blends((int)AlloyClass.Cutout), "#357: nor does a cutout -- it writes depth, so it must be opaque");
        check.Expect(Blends((int)AlloyClass.Blended) && Blends((int)AlloyClass.Additive), "blended and additive do");
        check.Expect(Blends((int)AlloyClass.Water), "and so does water -- the engine draws bucket 6 under Enable(0)");
        check.Expect(Discard((int)AlloyClass.Cutout) == 0.2f && Discard((int)AlloyClass.Blended) == 0.2f,
          "the engine's 0.2 cutout, on the two classes it draws under Enable(1)");
        check.Expect(Discard((int)AlloyClass.Opaque) < 0f && Discard((int)AlloyClass.Additive) < 0f
          && Discard((int)AlloyClass.Water) < 0f,
          "and no discard at all on the three it does not");
        // Draw order is the enum's own order, and the staging sort is what depends on it: opaque and cutout
        // share a pass, so they must be adjacent AND first.
        check.Expect((int)AlloyClass.Opaque == 0 && (int)AlloyClass.Cutout == 1 && (int)AlloyClass.Blended == 2
          && (int)AlloyClass.Water == 3 && (int)AlloyClass.Additive == 4, "declaration order IS draw order");
        // #129: and water sits BETWEEN them, which is the whole of its scheduling. The framebuffer copy it
        // reads is taken at this boundary: everything below it is in the copy, everything above is not.
        check.Expect((int)AlloyClass.Water > (int)AlloyClass.Blended && (int)AlloyClass.Water < (int)AlloyClass.Additive,
          "water draws after the blended class (fade is in its copy) and before additive (glow is not)");
        check.Expect(Count == System.Enum.GetValues<AlloyClass>().Length, "Count sizes the per-class arrays");

        log.Info(check.Ok ? "AlloyClasses.SelfCheck: passed" : "AlloyClasses.SelfCheck: FAILED");
    }
}
