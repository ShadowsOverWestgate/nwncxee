
namespace CX.Alloy;

/// <summary>The geometry of the engine's finished draw buckets at <c>Scene+0x208</c>, and nothing else.
/// <para>#188 put four hooks here -- <c>Scene::RenderDrawBucket</c>, the <c>GetFirstItem</c>/<c>GetNextItem</c>
/// iterator pair and <c>HasItemsOfType</c> -- to bracket the engine's walk, publish the walk context and skip
/// the items an Alloy pass had already drawn. #264 deleted the walk: <see cref="RspShim"/> no longer calls
/// <c>RenderDrawBucket</c> at all, so there is no engine draw to suppress, no sighting to stamp and no walk
/// context to publish. What survives is the LAYOUT, because <see cref="SceneWalk.CrossCheck"/> still reads
/// the arrays as a bring-up validator (ADR 0018 demotes them to exactly that).</para></summary>
internal static unsafe class BucketGate
{
    /// <summary>Bucket-array geometry, from <c>GetDrawBucketByItemType</c> @0x47bd60 (stride 0x10, three
    /// phases per item type) and <c>GetNextItem</c> @0x47be90 (items at +0, count at +8).</summary>
    internal const int BucketPhases = 3, BucketCount = 8;
}
