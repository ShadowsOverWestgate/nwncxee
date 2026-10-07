using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The part walk: what <c>Part::Animate</c> @0x49ad00 and its one real override are for.
/// <para>The engine's walk existed to find the parts whose <c>Animate</c> is NOT the plain recursion -- one
/// indirect call per part to discover that nearly none of them do anything (<c>Alloy/Anim/PartWalk.cs</c> has
/// the sweep that proves it: only <c>PartDanglyMesh::Animate</c> @0x49bbb0 overrides it meaningfully, and
/// <c>PartEmitter::Animate</c> is a tail-jump into the base). In DB mode the question is answered by the node's
/// kind, read off a column, so a creature with no dangly part costs one pass over one byte array.</para></summary>
public static unsafe partial class DbAnim
{
    /// <summary>Animate <paramref name="obj"/>'s parts: the dangly solve where a part has one, and nothing
    /// else. The REAL frame time, never a rate-reduced one -- a cloak sampled every other frame stutters where
    /// a limb pose does not.</summary>
    private static void PartWalk(DbId obj)
    {
        int i = obj.Index;
        // The tree fact, scanned once per part range instead of per step: an object with no dangly part --
        // every tile, every placeable, nearly every creature -- used to walk its whole range here every frame
        // to discover that.
        if (!_hasDangly[i] || !Dangly.Enabled.V) return;
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        if (n <= 0 || _danglyDt <= 0f) return;
        for (int k = 0; k < n; k++)
        {
            int p = first + k;
            if (!SceneDb.PartEnabled[p] || SceneDb.PartMerged[p]) continue;
            ModelDef? m = SceneDb.PartModel[p];
            if (m == null) continue;
            int node = SceneDb.PartNode[p];
            if ((uint)node >= (uint)m.Nodes.Length) continue;
            if (m.Nodes[node].Kind != NodeKind.Dangly) continue;
            DanglySolve(p, _danglyDt);
        }
    }

    /// <summary>The frame time the part walk hands the dangly solve. Latched per scene animate, because the
    /// walk is reached from the per-object step and the solve must see one value for the whole frame.</summary>
    private static float _danglyDt;
}
