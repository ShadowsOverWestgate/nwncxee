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
}
