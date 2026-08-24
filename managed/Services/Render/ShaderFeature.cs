using System;

namespace Sowx.Services.Render;

/// <summary>
/// #123: which SoW custom shader a part's SharedMaterial asks for, recognised by name so the owned pass can
/// reproduce it as a switch inside its own program instead of porting seven forks.
/// <para>The seven are community forks of the stock <c>vslit_nm</c>/<c>fslit_nm</c> pair (self-credited in
/// their headers, absent from both stock EE shader generations -- the correction carried from #107). Every
/// one of them differs from stock by a handful of lines: a sine displacement, a camera-height branch, a
/// keyhole define, an RG-map UV offset. The ids below are those lines, not those files.</para>
/// <para>The value IS the shader-side switch: it is staged into the per-draw record verbatim, so the GLSL
/// <c>SOWX_FEAT_*</c> constants and this enum are one contract (<c>SelfCheck</c> asserts it).</para>
/// </summary>
internal enum ShaderFeature
{
    /// <summary>No custom program, or one this pass does not recognise -- drawn with the plain owned lit
    /// shading, which is what every custom-shader part got before #123.</summary>
    None = 0,
    /// <summary>Plane-based mesh grass: sine sway anchored at the roots, tileset-mesh Z lift.</summary>
    WindGrass = 1,
    /// <summary>Plane-based mesh leaves: the same sway with a flat multiplier instead of a root anchor.</summary>
    WindLeaf = 2,
    /// <summary>3D mesh plants: the grass sway at double frequency and a thousandth of the strength.</summary>
    WindPlant = 3,
    /// <summary>Roof/ceiling toggle, no keyholing: raised to the ceiling while the camera is below the
    /// tileset's clip height, dropped 500m under otherwise, with the toolset vertex anchor discarded.</summary>
    RoofKn = 4,
    /// <summary>The same toggle with keyholing on, the clip height measured from the PLAYER, and no anchor
    /// discard.</summary>
    RoofKnM = 5,
    /// <summary>Stock lit with <c>KEYHOLING 0</c> -- tileset architecture that must never dissolve.</summary>
    NoKeyhole = 6,
    /// <summary>Diffuse UVs slid along an RG flow map in texture slot 6, two half-offset phases blended.</summary>
    Flowmap = 7,
    /// <summary>#137: not a content shader at all -- the TRUE skinmesh (a cloak, a robe, a tail), whose
    /// vertices are posed by the engine's 64-matrix bone array rather than by the node matrix alone. It rides
    /// in the same field for two reasons: the per-draw record already carries it to both vertex stages, and
    /// the field is part of the batch cut key, which is the granularity the depth prepass drops at.
    /// <para>The record's <c>feat.y</c> then carries this draw's bone base. ponytail: that makes the two
    /// mutually exclusive -- a skinmesh whose material also named a wind or roof fork loses the fork, not the
    /// pose. No shipped robe declares one; if one ever does, the fix is a bit in <c>feat.z</c> rather than a
    /// second field.</para></summary>
    Skin = 8,
}

internal static class ShaderFeatures
{
    /// <summary>The <c>customShaderVS</c> resrefs, indexed by <see cref="ShaderFeature"/>. The VS name alone
    /// separates all seven -- flowmap's vertex fork is stock code, but the .mtr still names it.</summary>
    /// <para>#137's <see cref="ShaderFeature.Skin"/> deliberately has NO entry: it is not a content shader
    /// and no material names it, so this array stays the seven forks plus the empty <c>None</c>.</para>
    internal static readonly string[] VsNames =
    {
        "", "shd_windgrass_vs", "shd_windleaf_vs", "shd_windplant_vs",
        "shd_roofkn_vs", "shd_roofknm_vs", "shd_nokeyhole_vs", "shd_flowmap_vs",
    };

    /// <summary>Does this feature move vertices? Such a part must stay out of the #119 depth prepass: the
    /// prepass stage does not displace, so its depth is the part's UNDISPLACED silhouette and the colour
    /// pass's LEQUAL then kills real fragments (a roof offset by ±500 fails everywhere; geometry behind a
    /// swayed plant dies at the plant's rest outline). Silent holes, exactly the failure #119 named.</summary>
    internal static bool Displaces(int feat)
        => feat >= (int)ShaderFeature.WindGrass && feat <= (int)ShaderFeature.RoofKnM;

    /// <summary>#137: must this batch stay OUT of the #119 depth prepass? Everything <see cref="Displaces"/>
    /// names, plus the skinmesh class -- and the skinned case is the ticket's "prepass decision", made
    /// explicitly here rather than left implicit.
    /// <para>The reason is #119's constraint itself. The prepass and the colour pass must compute
    /// <c>gl_Position</c> from a character-for-character identical expression under <c>invariant</c>, or
    /// LEQUAL drops real fragments as silent holes. A skinned position is a weighted sum of four bone
    /// matrices -- it cannot be that expression, and giving the prepass its own skinning branch would mean
    /// two copies of a sum whose floating-point ASSOCIATION would have to match, which <c>invariant</c> does
    /// not promise across programs. So skinned draws leave the prepass, exactly as displacing ones do. The
    /// cost is the overdraw of a cloak, which is a handful of parts; the alternative is holes in them.</para>
    /// </summary>
    internal static bool OutOfPrepass(int feat)
        => Displaces(feat) || feat == (int)ShaderFeature.Skin;

    /// <summary>Does this feature switch keyholing OFF? Those parts keep their ownership when the camera
    /// would dissolve them, because their vanilla shader ignores the dissolve too.
    /// <para>Read off each fork's FRAGMENT stage, which is where the dissolve actually happens
    /// (<c>inc_standard.shd:392</c>, <c>ApplyKeyhole(FragmentColor)</c> under <c>KEYHOLING == 1</c>); the
    /// vertex define only gates <c>SetupKeyholeFactors</c>. That distinction is not academic -- flowmap
    /// declares <c>KEYHOLING 1</c> in its vertex stage and <c>0</c> in its fragment stage, so reading the
    /// vertex define would put it on the wrong side. The three wind forks and roofknm keep the dissolve;
    /// nokeyhole, roofkn and flowmap drop it.</para></summary>
    internal static bool NoKeyholing(int feat)
        => feat == (int)ShaderFeature.NoKeyhole || feat == (int)ShaderFeature.RoofKn
           || feat == (int)ShaderFeature.Flowmap;
}
