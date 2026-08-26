namespace Sowx.Services.Render;

/// <summary>
/// Engine struct offsets that more than one scene walker dereferences. These are not symbols -- nothing
/// resolves them -- so they live with the code that reads them, not in <c>SymbolMap</c> (#159). An offset
/// with exactly one reader stays private in that reader; only the shared ones are here, written once so
/// the RE provenance cannot drift between copies. All Ghidra-verified against the Linux client.
/// </summary>
internal static class SceneLayout
{
    /// <summary>Scene -&gt; BSPNode* root (the static-geometry BSP). Linux-verified in ManageSceneBSP.</summary>
    public const int SceneBspRoot = 0x188;
    /// <summary>Gob -&gt; node pointer. MUST be non-null before appending to the gobbucket: the sort comparator
    /// <c>FUN_0055e070</c> blindly derefs <c>*(gob+0x68)+0x8e</c> (the render path null-checks it, the sort
    /// does not) -- a gob with 0x68==0 in the bucket crashes the sort.</summary>
    public const int GobNode = 0x68;
    /// <summary>Gob -&gt; Part* part-tree root. <c>ProcessGob</c>/<c>AddPartsTreeToDrawBuckets</c> walk it; guard
    /// non-null so a geometry-less gob is skipped, not dereferenced.</summary>
    public const int GobPartTree = 0x90;
    /// <summary>Gob -&gt; world position (float x@+0xa8, y@+0xac, z@+0xb0).</summary>
    public const int GobPosition = 0xa8;
}
