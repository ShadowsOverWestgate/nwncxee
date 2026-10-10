using System;
using System.Collections.Generic;
using System.Globalization;

namespace CX.Alloy.Mdl;

/// <summary>The perf reading <b>distinct meshes against placements</b>: the engine stores a mesh per
/// PLACEMENT, so its share is 1.00x however much an area repeats, while ADR 0017 measured ~2.5x at CONTENT
/// identity -- the gap a content-keyed cache buys. <c>PartTree.Build</c>/<c>Free</c> owns the count: one
/// <see cref="NotePlacement"/> per mesh-drawing part, one <see cref="DropPlacement"/> when its range goes.</summary>
public static partial class ModelCache
{
    /// <summary>Placements per distinct mesh. Reference identity: two meshes with the same vertices are still
    /// two meshes unless the parse handed back the same object, which is exactly what the share measures.</summary>
    private static readonly Dictionary<MeshDef, int> _placements = new(ReferenceEqualityComparer.Instance);
    /// <summary>Resident models per distinct mesh: maintained by <see cref="InsertModel"/> and
    /// <see cref="Evict"/>, so the count is a <c>Count</c> and not a walk.</summary>
    private static readonly Dictionary<MeshDef, int> _residentMeshes = new(ReferenceEqualityComparer.Instance);
    private static long _placed;

    /// <summary>One part instance now draws <paramref name="mesh"/>.</summary>
    public static void NotePlacement(MeshDef mesh)
    {
        if (mesh == null) return;
        _placements.TryGetValue(mesh, out int n);
        _placements[mesh] = n + 1;
        _placed++;
    }

    /// <summary>...and no longer does. An unmatched drop is counted, not ignored: it would mean the part
    /// tree's two edges have come apart.</summary>
    public static void DropPlacement(MeshDef mesh)
    {
        if (mesh == null) return;
        if (!_placements.TryGetValue(mesh, out int n) || n <= 0)
        {
            _strayDrops++;
            Say("stray-drop", $"ModelCache: a placement of a mesh that was never noted was dropped "
                            + $"({_placements.Count} meshes held)");
            return;
        }
        _placed--;
        if (n == 1) _placements.Remove(mesh); else _placements[mesh] = n - 1;
    }

    private static long _strayDrops;

    /// <summary>A model entering or leaving the table, <paramref name="delta"/> +1 or -1. One pass over its
    /// nodes at each edge, so the resident-mesh count is a <c>Count</c> and never a walk.</summary>
    private static void NoteResidentMeshes(ModelDef m, int delta)
    {
        NodeDef[] nodes = m.Nodes;
        for (int i = 0; i < nodes.Length; i++)
        {
            MeshDef? mesh = nodes[i].Mesh;
            if (mesh == null) continue;
            _residentMeshes.TryGetValue(mesh, out int n);
            n += delta;
            if (n <= 0) _residentMeshes.Remove(mesh); else _residentMeshes[mesh] = n;
        }
    }

    private static void ClearResidentMeshes() => _residentMeshes.Clear();

    /// <summary>The census clause of the period and area lines: meshes, placements and the share the one
    /// buys over the other. A share of 1.00x is the engine's own figure and means the cache is not being
    /// shared; 0 placements means nothing has built a part tree yet.</summary>
    internal static string CensusClause()
    {
        int meshes = _placements.Count;
        string s = $"meshes={_residentMeshes.Count}";
        if (meshes != 0 || _placed != 0)
        {
            double share = meshes == 0 ? 0 : (double)_placed / meshes;
            s += $" placed={meshes} placements={_placed} share={share.ToString("F2", CultureInfo.InvariantCulture)}x";
        }
        if (_strayDrops != 0) s += $" straydrops={_strayDrops}";
        if (_flatBinds != 0) s += $" flatbind={_flatBinds}";
        if (_partialBinds != 0) s += $" partialbind={_partialBinds}";
        if (Bindings != 0) s += $" bindings={Bindings}";
        if (ResolveHits + ResolveMisses != 0) s += $" animlookup={ResolveHits + ResolveMisses} ({ResolveMisses} walked)";
        return s;
    }

    /// <summary>Placements and distinct placed meshes, for the self-checks.</summary>
    internal static long Placements => _placed;
    internal static int PlacedMeshes => _placements.Count;
    internal static int ResidentMeshes => _residentMeshes.Count;
    internal static long StrayDrops => _strayDrops;

    /// <summary>Forget the placement census. The area edge does not call this -- <c>PartTree</c> drops its
    /// placements as it frees the part ranges -- so it exists for the self-checks and for <see cref="Clear"/>.</summary>
    internal static void ResetCensus()
    {
        _placements.Clear();
        _placed = 0;
        _strayDrops = 0;
        _flatBinds = 0;
        _partialBinds = 0;
    }
}
