using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>The tile geometry a BSP node holds lives in the node's inventory record
/// (<see cref="EngineLayout.BspNodeData"/>), not in the node: the two list rows are sub-offsets of a record
/// the layout table gives no class of its own, so the chase through it is a concept here rather than two
/// BSPNode fields that would read off the wrong object.</summary>
public readonly unsafe partial struct BSPNode
{
    /// <summary>The <c>CExoArrayList&lt;PartTriMesh*&gt;</c> of tile parts filed in this node
    /// (<see cref="EngineLayout.BspDataTriMesh"/>), or null when the node carries no inventory record.</summary>
    public IntPtr* TileParts => Data == IntPtr.Zero ? null : *(IntPtr**)(Data + EngineLayout.BspDataTriMesh);

    /// <summary>How many of <see cref="TileParts"/> are live
    /// (<see cref="EngineLayout.BspDataTriMeshCount"/>); zero without a record.</summary>
    public int TilePartCount => Data == IntPtr.Zero ? 0 : *(int*)(Data + EngineLayout.BspDataTriMeshCount);

    /// <summary>CX-108: the <c>CExoArrayList&lt;PartLight*&gt;</c> of lights filed in this node
    /// (<see cref="EngineLayout.BspDataLight"/>). Null without a record.
    /// <para><b>Not a liveness list.</b> It holds GOB lights as well as tile ones -- <c>Scene::ChildAdd</c>
    /// @0x461d50 routes a gob's whole tree through <c>ListSpecialPartsR</c> @0x462d50, which files every
    /// <c>AsPartLight</c> here -- and nothing prunes an entry when the part dies, so it can name freed memory.
    /// Alloy never admitted a light from it: nothing prunes it on part death, so it is a list of pointers rather than of lights.</para></summary>
    public IntPtr* Lights => Data == IntPtr.Zero ? null : *(IntPtr**)(Data + EngineLayout.BspDataLight);

    /// <summary>How many entries <see cref="Lights"/> holds (<see cref="EngineLayout.BspDataLightCount"/>) --
    /// its LENGTH, not a live count: see there for why some of them name freed memory.</summary>
    public int LightCount => Data == IntPtr.Zero ? 0 : *(int*)(Data + EngineLayout.BspDataLightCount);

}
