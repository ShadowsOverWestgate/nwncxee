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
}
