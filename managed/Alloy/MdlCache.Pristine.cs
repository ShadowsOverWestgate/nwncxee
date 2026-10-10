using System;
using System.Collections.Generic;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>What makes a cached compile the PARSE's output and not a session's render state. Instantiation MUTATES
/// the shared <c>MdlNodeTriMesh</c> every instance points at -- <c>CombineParts</c> grows
/// <see cref="EngineLayout.MdlRadius"/>, <c>SetPartTextureRotate</c> latches <see cref="EngineLayout.MdlTextureRotate"/>
/// to 1 and never back -- so a payload serialised after it freezes that state on disk. No snapshot, no cache.</summary>
internal static unsafe class MdlCachePristine
{
    /// <summary>One node's pristine pair. The pointer is only ever dereferenced after
    /// <see cref="Owner"/> has been re-proved, so a freed tree cannot be written through.</summary>
    private readonly struct Node
    {
        internal Node(IntPtr at, float radius, byte rotate) { At = at; Radius = radius; Rotate = rotate; }
        internal readonly IntPtr At;
        internal readonly float Radius;
        internal readonly byte Rotate;
    }

    /// <summary>What was read off one freshly parsed model.</summary>
    private sealed class Shot
    {
        internal IntPtr Owner;
        internal readonly List<Node> Nodes = new();
    }

    /// <summary>A node tree deeper than this is not one: the walk stops rather than recursing on a cycle.</summary>
    private const int MaxDepth = 256;
    /// <summary>Snapshots held at once. An area names a few hundred models and each one is dropped at its
    /// compile, so this is a wall against a session that never reaches a cold point, not a budget.</summary>
    private const int MaxShots = 4096;

    private static readonly Dictionary<string, Shot> _shots = new(StringComparer.Ordinal);
    private static ExtenderLog? _log;
    private static bool _armed;
    private static long _taken, _nodes, _dropped, _stale, _applied;

    /// <summary>Resolve the layout this needs. False means no snapshot can be taken, and
    /// <see cref="MdlCache"/> then caches nothing -- a loud refusal, not a contaminated payload.</summary>
    internal static bool Attach(ExtenderLog log)
    {
        _log = log;
        _armed = EngineLayout.MdlParseModelRoot != 0 && EngineLayout.MdlNodeChildren != 0
              && EngineLayout.MdlNodeKind != 0 && EngineLayout.MdlRadius != 0
              && EngineLayout.MdlTextureRotate != 0;
        if (!_armed)
            log.Always("MdlCache: the MdlNodeTriMesh layout did not resolve -- no compile can be proved "
                     + "pristine, so nothing is cached this session");
        return _armed;
    }

    /// <summary>A model the engine has just finished parsing -- the one point it is provably pristine, because
    /// <c>IODispatcher::ReadSync</c> returns before the caller that will instantiate it. Records the two mutable
    /// fields of every trimesh node, keyed by the resref the compile will ask for.</summary>
    internal static void Took(string resref, IntPtr model)
    {
        if (!_armed || model == IntPtr.Zero || resref.Length == 0) return;
        if (_shots.Count >= MaxShots) { _dropped++; return; }
        var shot = new Shot { Owner = model };
        Walk(new Model(model).Root.Ptr, shot, 0);
        _shots[resref] = shot;
        _taken++;
        _nodes += shot.Nodes.Count;
    }

    private static void Walk(IntPtr node, Shot shot, int depth)
    {
        if (node == IntPtr.Zero || depth >= MaxDepth) return;
        var n = new MdlNode(node);
        if ((n.Kind & EngineLayout.MdlNodeKindTriMesh) == EngineLayout.MdlNodeKindTriMesh)
            shot.Nodes.Add(new Node(node, Radius(node), Rotate(node)));
        int count = n.ChildCount;
        IntPtr* kids = (IntPtr*)n.Children;
        if (kids == null) return;
        for (int i = 0; i < count; i++) Walk(kids[i], shot, depth + 1);
    }

    private static ref float Radius(IntPtr node) => ref new MdlNodeTriMesh(node).Radius;
    private static ref byte Rotate(IntPtr node) => ref new MdlNodeTriMesh(node).TextureRotate;

    /// <summary>Is there a snapshot for <paramref name="resref"/> whose model is still the one the engine has?
    /// <paramref name="live"/> comes back holding the values to put back after the compile.</summary>
    internal static bool Begin(string resref, IntPtr model, out float[] live)
    {
        live = Array.Empty<float>();
        if (!_armed || !_shots.TryGetValue(resref, out Shot? shot)) return false;
        // The model pointer alone is not proof: a freed Model can be re-read into the same block with a
        // different tree. Re-walk it and require the SAME trimesh nodes in the same order, which a rebuilt
        // tree cannot produce -- nothing is written through a pointer that has not been re-proved.
        if (model == IntPtr.Zero || shot.Owner != model || !SameTree(model, shot))
        {
            _stale++;
            _shots.Remove(resref);
            return false;
        }
        List<Node> nodes = shot.Nodes;
        live = new float[nodes.Count * 2];
        for (int i = 0; i < nodes.Count; i++)
        {
            IntPtr at = nodes[i].At;
            live[i * 2] = Radius(at);
            live[i * 2 + 1] = Rotate(at);
            Radius(at) = nodes[i].Radius;
            Rotate(at) = nodes[i].Rotate;
        }
        _applied++;
        return true;
    }

    /// <summary>Does <paramref name="model"/> still have exactly the trimesh nodes the snapshot recorded, in
    /// the walk's own order?</summary>
    private static bool SameTree(IntPtr model, Shot shot)
    {
        _probe.Clear();
        WalkPointers(new Model(model).Root.Ptr, 0);
        if (_probe.Count != shot.Nodes.Count) return false;
        for (int i = 0; i < _probe.Count; i++) if (_probe[i] != shot.Nodes[i].At) return false;
        return true;
    }

    /// <summary>Scratch for <see cref="SameTree"/>: the walk runs on the game thread, one model at a time.</summary>
    private static readonly List<IntPtr> _probe = new();

    private static void WalkPointers(IntPtr node, int depth)
    {
        if (node == IntPtr.Zero || depth >= MaxDepth) return;
        var n = new MdlNode(node);
        if ((n.Kind & EngineLayout.MdlNodeKindTriMesh) == EngineLayout.MdlNodeKindTriMesh) _probe.Add(node);
        int count = n.ChildCount;
        IntPtr* kids = (IntPtr*)n.Children;
        if (kids == null) return;
        for (int i = 0; i < count; i++) WalkPointers(kids[i], depth + 1);
    }

    /// <summary>Put the live values back, whatever the compile did. Always paired with a true
    /// <see cref="Begin"/>, in a finally.</summary>
    internal static void End(string resref, float[] live)
    {
        if (!_shots.TryGetValue(resref, out Shot? shot)) return;
        List<Node> nodes = shot.Nodes;
        for (int i = 0; i < nodes.Count && i * 2 + 1 < live.Length; i++)
        {
            IntPtr at = nodes[i].At;
            Radius(at) = live[i * 2];
            Rotate(at) = (byte)live[i * 2 + 1];
        }
        _shots.Remove(resref);   // the compile is done with it either way
    }

    /// <summary>Forget a model's snapshot: its compile was refused, or the queue dropped it.</summary>
    internal static void Forget(string resref) => _shots.Remove(resref);

    /// <summary>The clause <see cref="MdlCache"/>'s period line carries.</summary>
    internal static string Note()
    {
        if (_taken == 0 && _dropped == 0) return "";
        string s = $"pristine {_applied}/{_taken} ({_nodes} trimesh nodes, {_shots.Count} held)";
        if (_stale != 0) s += $", {_stale} stale";
        if (_dropped != 0) s += $", {_dropped} dropped";
        return s;
    }
}
