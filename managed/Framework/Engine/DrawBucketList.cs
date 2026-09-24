using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>A <c>DrawBucket</c> is a bare list head: <c>DrawBucketManager::GetNextItem</c> @0x47be90 reads
/// the item array at +0 and the count at +8, which is the engine's own <c>List&lt;T&gt;</c> shape. Those are
/// the <c>(abi)</c> rows, belonging to no class, so the pair is a concept here rather than two DrawBucket
/// fields.</summary>
public readonly unsafe partial struct DrawBucket
{
    /// <summary>The parts the engine classified into this bucket this frame, or null while it holds none.
    /// </summary>
    public IntPtr* Items => *(IntPtr**)(Ptr + EngineLayout.ListItems);
    /// <summary>How many of <see cref="Items"/> are live.</summary>
    public ref int Count => ref *(int*)(Ptr + EngineLayout.ListItemCount);
}
