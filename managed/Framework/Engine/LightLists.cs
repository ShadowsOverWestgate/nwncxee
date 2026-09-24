using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>The master light list's element access. <see cref="EngineLayout.LmMasterData"/> is a
/// <c>PartLight**</c> on the heap, so indexing it is not a field of the object and no row describes it.
/// </summary>
public readonly unsafe partial struct LightManager
{
    /// <summary>Light <paramref name="i"/> of the master list, with no bounds check --
    /// <see cref="MasterCount"/> is the caller's, and <see cref="MasterData"/> may be null while the area
    /// holds none. The pointer is the engine's, freed at the area edge: compared, never retained.</summary>
    public PartLight Master(int i) => new(*((IntPtr*)MasterData + i));
}

/// <summary>The area's placeable-light registry, which is a <c>CNWCPlaceableObjectLight**</c> rather than an
/// array of records: <c>CNWCArea::AddPlaceableObjectLight</c> @0x705120 <c>operator new(0x10)</c>s each entry
/// and stores the POINTER at <c>*(placeableLights + i * 8)</c>, and <c>GetPlaceableObjectLight</c> @0x7057e0
/// walks the same stride. An element pointer is no field of the area, so the accessor is a concept.</summary>
public readonly unsafe partial struct CNWCArea
{
    /// <summary>Registry entry <paramref name="i"/>, or the null view for a hole. Bounds are the caller's
    /// (<see cref="PlaceableLightCount"/>).</summary>
    public CNWCPlaceableObjectLight PlaceableLight(int i) => new(*((IntPtr*)PlaceableLights + i));
}

/// <inheritdoc cref="CNWCArea.PlaceableLight"/>
public readonly unsafe partial struct CNWCPlaceableObjectLight
{
    /// <summary>The placeable's object id, the record's first word -- what
    /// <c>GetPlaceableObjectLight</c> @0x7057e0 matches on before returning <see cref="Gob"/>.</summary>
    public ref uint Id => ref *(uint*)Ptr;
}
