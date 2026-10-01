using System;

namespace CX.Alloy;

/// <summary>CX-102 seam: every part <see cref="SceneWalk"/> reaches that is not a <c>PartTriMesh</c> (lights,
/// emitters, triggers, beams, dummies, references), handed to the registries that own a class of them. The
/// walk runs on the scene epoch edge, so a registry learns arrivals here, departures from
/// <see cref="WalkDone"/> (a part not visited on generation <c>gen</c> left the scene) and deaths from
/// <see cref="Died"/> (<c>Part::~Part</c>; compare the pointer, never dereference it).</summary>
internal static class NonMeshParts
{
    /// <summary>A non-mesh part reached on walk generation <paramref name="gen"/>; <paramref name="bsp"/> = it
    /// hangs off the tile BSP rather than a gob.</summary>
    internal static void Visit(IntPtr part, bool bsp, int gen)
    {
        LightRegistry.Visit(part, bsp, gen);
        SceneParts.Visit(part, bsp, gen);
    }

    /// <summary>The walk of generation <paramref name="gen"/> finished: everything not visited on it left.</summary>
    internal static void WalkDone(int gen)
    {
        LightRegistry.WalkDone(gen);
        SceneParts.WalkDone(gen);
    }

    /// <summary><c>Part::~Part</c> for <paramref name="part"/>, before the engine frees it.</summary>
    internal static void Died(IntPtr part)
    {
        LightRegistry.Died(part);
        SceneParts.Died(part);
    }

    /// <summary>This part is ALIVE but no longer enumerated. <paramref name="gone"/> says WHY, and the two
    /// registries answer it differently:
    /// <list type="bullet">
    /// <item><paramref name="gone"/> -- its gob left the scene through <c>Gob::SetScene</c>, which unlinks
    /// without freeing. Nothing about the part is in the world any more, so both registries retire it.</item>
    /// <item>otherwise -- an LOD rotation moved its root out of the walk. The part is still in the scene and the
    /// engine's own lists still name it, so what happens next has to match what a FULL walk does, class by
    /// class. <see cref="SceneParts"/>' sweep drops it (nothing restamps an alternate's emitter, and its
    /// consumers have no inactive-LOD filter -- two roots' emitters simulating at once is #397's doubled
    /// shield in another shape), so a delta must drop it too. <see cref="LightRegistry"/>'s survives, because
    /// the tile BSP's node lists restamp it and <c>Select</c>'s own inactive-LOD filter is where that
    /// camera-dependent answer belongs -- so a delta must keep it.</item>
    /// </list>
    /// <para>Retirement IS <see cref="Died"/> in both cases, deliberately and with no difference in effect: the
    /// death notice is the registries' one path that does not wait for a walk generation, it compares pointers
    /// and never dereferences one, and a part that comes back re-registers through <see cref="Visit"/>. The
    /// separate name exists so a reader at the CALL SITE is not told a live part died; it is not a second
    /// mechanism, and the registries' death counters include these.</para></summary>
    internal static void Left(IntPtr part, bool gone)
    {
        if (gone) LightRegistry.Died(part);
        SceneParts.Died(part);
    }

    /// <summary>A per-gob delta finished. No sweep -- a delta knows nothing about what it did not visit -- but
    /// the dense per-class lists the consumers walk are republished, because a spawned emitter that is only in
    /// the intern table is one nothing draws.
    /// <para><see cref="LightRegistry"/> is not called: it maintains its live list on the claim and the
    /// retirement instead, so a delta costs it nothing. This runs on most frames -- a part death, an equip, an
    /// LOD swap or a spawn is all it takes -- so anything O(table) here is the per-frame cost this whole stream
    /// exists to remove.</para></summary>
    internal static void DeltaDone() => SceneParts.DeltaDone();
}
