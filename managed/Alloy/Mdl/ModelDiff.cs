using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace CX.Alloy.Mdl;

/// <summary>CX-112: compares two <see cref="ModelDef"/> trees node for node and counts the mismatches by
/// category. The ORACLE of the parse: an ASCII source and the engine's own compiled binary of that same source
/// must agree, so every category that is not zero is a bug in one of the two readers. Engine-free, so
/// <c>tools/mdlcheck</c> and the <c>run-demo</c> self-check share it.</summary>
public sealed class ModelDiff
{
    /// <summary>What kind of thing disagreed. One bucket per category the ticket asks to drive to zero.</summary>
    public enum Cat
    {
        NodeCount, NodeKind, NodeName, Hierarchy, PartNumber, PartNumberSuper, RestPose, ContentFlags,
        Controllers, ControllerKeys, ControllerValues,
        VertexCount, Vertex, FaceCount, Face, Uv, Color, Normal, Tangent, Adjacency, SurfaceArea,
        Bounds, Index, Plane, Material, Texture, TangentExternal, Skin, Dangly, AnimMesh, Aabb, Light, Emitter, Reference,
        AnimCount, Anim, ModelFields,
    }

    private readonly int[] _counts = new int[Enum.GetValues<Cat>().Length];
    private readonly List<string> _first = new();
    /// <summary>How many example lines to keep -- enough to debug, bounded so a bad parse cannot eat memory.</summary>
    private const int MaxExamples = 24;
    /// <summary>Raised by a caller that wants every example (the off-device harness in verbose mode).</summary>
    public int Examples2Keep = MaxExamples;

    /// <summary>Absolute tolerance for a float. The sources carry five decimals and the payload carries the
    /// float the engine computed from them, so a real agreement is well inside this; a relative tolerance
    /// scaled by the magnitude would have let a whole millimetre pass at ten units.</summary>
    public float Eps = 2e-5f;

    /// <summary>The pair being compared has a supermodel, so its node numbers depend on the chain state at
    /// compile time -- which <c>MdlCache</c>'s key does not capture. Such a mismatch goes to its own
    /// category instead of looking like a parser bug.</summary>
    public bool SuperModelled;

    /// <summary>Nodes compared, by kind: the oracle's coverage, which a count of models does not show.</summary>
    private readonly int[] _kinds = new int[Enum.GetValues<NodeKind>().Length];

    public int Total { get; private set; }
    /// <summary>Models, nodes and mesh nodes the comparison walked: the denominators a residual needs.</summary>
    public int Models, Nodes, Meshes;
    /// <summary>Fields the oracle cannot speak for because the payload carries a RUNTIME mutation of the
    /// shared node, not a parse result: the mesh radius and the rotate-texture flag.</summary>
    public int Contaminated;
    public int Count(Cat c) => _counts[(int)c];
    public IReadOnlyList<string> Examples => _first;

    private void Bad(Cat c, string what)
    {
        _counts[(int)c]++;
        Total++;
        if (_first.Count < Examples2Keep) _first.Add($"{c}: {what}");
    }

    private bool Near(float a, float b) => a == b || MathF.Abs(a - b) <= Eps;

    private bool Near(Vector3 a, Vector3 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
    private bool Near(Vector2 a, Vector2 b) => Near(a.X, b.X) && Near(a.Y, b.Y);
    private bool Near(Vector4 a, Vector4 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z) && Near(a.W, b.W);
    private bool Near(Quaternion a, Quaternion b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z) && Near(a.W, b.W);

    /// <summary>Which node kinds the comparison actually walked, biggest first -- so "the oracle is clean"
    /// cannot stand in for kinds it never saw.</summary>
    public string KindCoverage()
    {
        var sb = new StringBuilder();
        foreach (NodeKind k in Enum.GetValues<NodeKind>())
        {
            if (sb.Length != 0) sb.Append(' ');
            sb.Append(k).Append(' ').Append(_kinds[(int)k]);
        }
        return sb.ToString();
    }

    /// <summary>Compare <paramref name="b"/> (the candidate) against <paramref name="a"/> (the oracle).</summary>
    public void Compare(ModelDef a, ModelDef b)
    {
        // Counted BEFORE any early return: a denominator that shrinks on the worst pairs flatters the result.
        Models++;
        Nodes += Math.Max(a.Nodes.Length, b.Nodes.Length);
        foreach (NodeDef n in a.Nodes) if (n.Mesh != null) Meshes++;
        if (a.Nodes.Length != b.Nodes.Length)
        {
            Bad(Cat.NodeCount, $"{a.Name}: {a.Nodes.Length} vs {b.Nodes.Length}");
            return;
        }
        if (!Near(a.Radius, b.Radius) || !Near(a.BMin, b.BMin) || !Near(a.BMax, b.BMax)
            || a.Classification != b.Classification || !Near(a.AnimScale, b.AnimScale) || a.Fog != b.Fog
            || !string.Equals(a.SuperName, b.SuperName, StringComparison.OrdinalIgnoreCase))
            Bad(Cat.ModelFields, $"{a.Name}: class {a.Classification}/{b.Classification} super '{a.SuperName}'/'{b.SuperName}'"
                                 + $" r {a.Radius}/{b.Radius} scale {a.AnimScale}/{b.AnimScale} fog {a.Fog}/{b.Fog}"
                                 + $" bmin {a.BMin}/{b.BMin} bmax {a.BMax}/{b.BMax}");

        for (int i = 0; i < a.Nodes.Length; i++) Node(a.Name, a.Nodes[i], b.Nodes[i]);

        if (a.Anims.Length != b.Anims.Length)
            Bad(Cat.AnimCount, $"{a.Name}: {a.Anims.Length} vs {b.Anims.Length}");
        else
            for (int i = 0; i < a.Anims.Length; i++) Anim(a.Name, a.Anims[i], b.Anims[i]);
    }

    private void Anim(string model, AnimDef a, AnimDef b)
    {
        if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) || !Near(a.Length, b.Length)
            || !Near(a.TransTime, b.TransTime) || a.Nodes.Length != b.Nodes.Length
            || a.Events.Length != b.Events.Length
            || !string.Equals(a.AnimRoot, b.AnimRoot, StringComparison.OrdinalIgnoreCase))
        {
            Bad(Cat.Anim, $"{model}/{a.Name}: len {a.Length}/{b.Length} trans {a.TransTime}/{b.TransTime} root "
                          + $"'{a.AnimRoot}'/'{b.AnimRoot}' nodes {a.Nodes.Length}/{b.Nodes.Length} events "
                          + $"{a.Events.Length}/{b.Events.Length}");
            return;
        }
        for (int i = 0; i < a.Events.Length; i++)
            if (!Near(a.Events[i].Time, b.Events[i].Time)
                || !string.Equals(a.Events[i].Name, b.Events[i].Name, StringComparison.OrdinalIgnoreCase))
            { Bad(Cat.Anim, $"{model}/{a.Name}: event[{i}] {a.Events[i].Time} '{a.Events[i].Name}' vs {b.Events[i].Time} '{b.Events[i].Name}'"); break; }
        // Paired by (name, parent name) and then by name, each track consumed once -- NOT by slot, which
        // would hide a reordered tree. An animation may legitimately declare the same node name twice under
        // different parents (the binding is parent-scoped), which is why the parent is part of the key.
        var free = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < b.Nodes.Length; i++)
        {
            string k = Key(b, i);
            if (!free.TryGetValue(k, out List<int>? l)) free[k] = l = new List<int>();
            l.Add(i);
        }
        for (int i = 0; i < a.Nodes.Length; i++)
        {
            int j = Take(free, Key(a, i));
            if (j < 0) j = Take(free, a.Nodes[i].Name);
            if (j < 0)
            {
                Bad(Cat.Anim, $"{model}/{a.Name}: no track for '{a.Nodes[i].Name}' under "
                              + $"'{(a.Nodes[i].Parent >= 0 ? a.Nodes[a.Nodes[i].Parent].Name : "NULL")}'");
                continue;
            }
            Controllers($"{model}/{a.Name}/{a.Nodes[i].Name}", a.Nodes[i].Controllers, b.Nodes[j].Controllers);
        }

        static string Key(AnimDef d, int i) =>
            d.Nodes[i].Name + "<" + (d.Nodes[i].Parent >= 0 ? d.Nodes[d.Nodes[i].Parent].Name : "");

        static int Take(Dictionary<string, List<int>> free, string key)
        {
            if (!free.TryGetValue(key, out List<int>? l) || l.Count == 0) return -1;
            int j = l[0];
            l.RemoveAt(0);
            return j;
        }
    }

    private void Node(string model, NodeDef a, NodeDef b)
    {
        string at = $"{model}/{a.Name}";
        _kinds[(int)a.Kind]++;
        if (a.Kind != b.Kind) { Bad(Cat.NodeKind, $"{at}: {a.Kind} vs {b.Kind}"); return; }
        if (a.ContentFlags != b.ContentFlags)
            Bad(Cat.ContentFlags, $"{at}: node-type bits {a.ContentFlags:x} vs {b.ContentFlags:x}");
        if (a.InheritColor != b.InheritColor || !Near(a.Position, b.Position)
            || !Near(a.Orientation, b.Orientation) || !Near(a.Scale, b.Scale))
            Bad(Cat.RestPose, $"{at}: inheritcolor {a.InheritColor}/{b.InheritColor} pos {a.Position}/{b.Position}"
                              + $" rot {a.Orientation}/{b.Orientation} scale {a.Scale}/{b.Scale}");
        if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
            Bad(Cat.NodeName, $"{model}: '{a.Name}' vs '{b.Name}'");
        if (a.Parent != b.Parent || a.Children.Length != b.Children.Length)
            Bad(Cat.Hierarchy, $"{at}: parent {a.Parent}/{b.Parent} children {a.Children.Length}/{b.Children.Length}");
        if (a.PartNumber != b.PartNumber)
            Bad(SuperModelled ? Cat.PartNumberSuper : Cat.PartNumber, $"{at}: {a.PartNumber} vs {b.PartNumber}");
        Controllers(at, a.Controllers, b.Controllers);

        if ((a.Mesh == null) != (b.Mesh == null)) { Bad(Cat.VertexCount, $"{at}: mesh presence"); return; }
        if (a.Mesh != null && b.Mesh != null) Mesh(at, a.Mesh, b.Mesh);
        if (a.Skin != null && b.Skin != null) Skin(at, a.Skin, b.Skin);
        else if ((a.Skin == null) != (b.Skin == null)) Bad(Cat.Skin, $"{at}: skin presence");
        if (a.Dangly != null && b.Dangly != null)
        {
            if (a.Dangly.Constraints.Length != b.Dangly.Constraints.Length
                || !Near(a.Dangly.Displacement, b.Dangly.Displacement)
                || !Near(a.Dangly.Tightness, b.Dangly.Tightness) || !Near(a.Dangly.Period, b.Dangly.Period))
                Bad(Cat.Dangly, $"{at}: {a.Dangly.Constraints.Length}/{b.Dangly.Constraints.Length} constraints, "
                                + $"displacement {a.Dangly.Displacement}/{b.Dangly.Displacement}, tightness "
                                + $"{a.Dangly.Tightness}/{b.Dangly.Tightness}, period {a.Dangly.Period}/{b.Dangly.Period}");
            else
                for (int i = 0; i < a.Dangly.Constraints.Length; i++)
                    if (!Near(a.Dangly.Constraints[i], b.Dangly.Constraints[i]))
                    { Bad(Cat.Dangly, $"{at}: constraint[{i}] {a.Dangly.Constraints[i]} vs {b.Dangly.Constraints[i]}"); break; }
        }
        else if ((a.Dangly == null) != (b.Dangly == null)) Bad(Cat.Dangly, $"{at}: dangly presence");
        if (a.AnimMesh != null && b.AnimMesh != null)
        {
            AnimMeshDef x = a.AnimMesh, y = b.AnimMesh;
            if (x.Positions.Length != y.Positions.Length || x.Uvs.Length != y.Uvs.Length
                || !Near(x.SamplePeriod, y.SamplePeriod))
                Bad(Cat.AnimMesh, $"{at}: {x.Positions.Length}/{y.Positions.Length} vertex frames, "
                                  + $"{x.Uvs.Length}/{y.Uvs.Length} uv frames, period {x.SamplePeriod}/{y.SamplePeriod}");
            else if (!Frames(at, x.Positions, y.Positions) ) { }
            else
                for (int f = 0; f < x.Uvs.Length; f++)
                {
                    if (x.Uvs[f].Length != y.Uvs[f].Length)
                    { Bad(Cat.AnimMesh, $"{at}: uv frame {f} {x.Uvs[f].Length}/{y.Uvs[f].Length}"); break; }
                    bool bad = false;
                    for (int i = 0; i < x.Uvs[f].Length && !bad; i++)
                        if (!Near(x.Uvs[f][i], y.Uvs[f][i]))
                        { Bad(Cat.AnimMesh, $"{at}: uv frame {f}[{i}] {x.Uvs[f][i]} vs {y.Uvs[f][i]}"); bad = true; }
                    if (bad) break;
                }
        }
        else if ((a.AnimMesh == null) != (b.AnimMesh == null)) Bad(Cat.AnimMesh, $"{at}: animmesh presence");
        if (a.Aabb != null && b.Aabb != null)
        {
            if (a.Aabb.Tree.Length != b.Aabb.Tree.Length)
                Bad(Cat.Aabb, $"{at}: {a.Aabb.Tree.Length}/{b.Aabb.Tree.Length} aabb nodes");
            else
                for (int i = 0; i < a.Aabb.Tree.Length; i++)
                {
                    AabbNode x = a.Aabb.Tree[i], y = b.Aabb.Tree[i];
                    if (x.Left == y.Left && x.Right == y.Right && x.Face == y.Face && x.Plane == y.Plane
                        && Near(x.BMin, y.BMin) && Near(x.BMax, y.BMax)) continue;
                    Bad(Cat.Aabb, $"{at}: aabb[{i}] {x.BMin}..{x.BMax} l{x.Left} r{x.Right} f{x.Face} p{x.Plane}"
                                  + $" vs {y.BMin}..{y.BMax} l{y.Left} r{y.Right} f{y.Face} p{y.Plane}");
                    break;
                }
        }
        else if ((a.Aabb == null) != (b.Aabb == null)) Bad(Cat.Aabb, $"{at}: aabb presence");
        if (a.Light != null && b.Light != null)
        {
            LightDef x = a.Light, y = b.Light;
            if (x.FlareTextures.Length != y.FlareTextures.Length || x.FlareSizes.Length != y.FlareSizes.Length
                || x.FlarePositions.Length != y.FlarePositions.Length || x.FlareColors.Length != y.FlareColors.Length
                || x.AmbientOnly != y.AmbientOnly || x.Shadow != y.Shadow || x.Priority != y.Priority
                || x.Dynamic != y.Dynamic || x.AffectDynamic != y.AffectDynamic
                || x.GenerateFlare != y.GenerateFlare || x.Fading != y.Fading
                || !Near(x.FlareRadius, y.FlareRadius))
                Bad(Cat.Light, $"{at}: flares {x.FlareTextures.Length}/{y.FlareTextures.Length} r "
                               + $"{x.FlareRadius}/{y.FlareRadius} prio {x.Priority}/{y.Priority} amb "
                               + $"{x.AmbientOnly}/{y.AmbientOnly} shadow {x.Shadow}/{y.Shadow} dyn "
                               + $"{x.Dynamic}/{y.Dynamic} affect {x.AffectDynamic}/{y.AffectDynamic} flare "
                               + $"{x.GenerateFlare}/{y.GenerateFlare} fading {x.Fading}/{y.Fading}");
            else
            {
                for (int i = 0; i < x.FlareSizes.Length; i++)
                    if (!Near(x.FlareSizes[i], y.FlareSizes[i]))
                    { Bad(Cat.Light, $"{at}: flaresize[{i}] {x.FlareSizes[i]} vs {y.FlareSizes[i]}"); break; }
                for (int i = 0; i < x.FlareTextures.Length; i++)
                    if (!string.Equals(x.FlareTextures[i], y.FlareTextures[i], StringComparison.OrdinalIgnoreCase))
                    { Bad(Cat.Light, $"{at}: flaretex[{i}] '{x.FlareTextures[i]}' vs '{y.FlareTextures[i]}'"); break; }
            }
        }
        else if ((a.Light == null) != (b.Light == null)) Bad(Cat.Light, $"{at}: light presence");
        if (a.Emitter != null && b.Emitter != null)
        {
            EmitterDef x = a.Emitter, y = b.Emitter;
            if (x.Flags != y.Flags || x.Update != y.Update || x.Render != y.Render || x.Blend != y.Blend
                || x.Texture != y.Texture || x.ChunkName != y.ChunkName || x.XGrid != y.XGrid
                || x.YGrid != y.YGrid || x.SpawnType != y.SpawnType || x.RenderOrder != y.RenderOrder
                || x.TwoSidedTex != y.TwoSidedTex || x.Loop != y.Loop || !Near(x.DeadSpace, y.DeadSpace)
                || !Near(x.BlastRadius, y.BlastRadius) || !Near(x.BlastLength, y.BlastLength))
                Bad(Cat.Emitter, $"{at}: flags {x.Flags:x}/{y.Flags:x} '{x.Update}'/'{y.Update}' "
                                 + $"'{x.Render}'/'{y.Render}' '{x.Blend}'/'{y.Blend}' '{x.Texture}'/'{y.Texture}' "
                                 + $"chunk '{x.ChunkName}'/'{y.ChunkName}' grid {x.XGrid}x{x.YGrid}/{y.XGrid}x{y.YGrid} "
                                 + $"spawn {x.SpawnType}/{y.SpawnType} order {x.RenderOrder}/{y.RenderOrder} "
                                 + $"2side {x.TwoSidedTex}/{y.TwoSidedTex} loop {x.Loop}/{y.Loop} dead "
                                 + $"{x.DeadSpace}/{y.DeadSpace} blast {x.BlastRadius}/{x.BlastLength} vs "
                                 + $"{y.BlastRadius}/{y.BlastLength}");
        }
        else if ((a.Emitter == null) != (b.Emitter == null)) Bad(Cat.Emitter, $"{at}: emitter presence");
        if (a.Reference != null && b.Reference != null)
        {
            if (!string.Equals(a.Reference.Model, b.Reference.Model, StringComparison.OrdinalIgnoreCase)
                || a.Reference.Reattachable != b.Reference.Reattachable)
                Bad(Cat.Reference, $"{at}: '{a.Reference.Model}'/'{b.Reference.Model}' reattachable "
                                   + $"{a.Reference.Reattachable}/{b.Reference.Reattachable}");
        }
        else if ((a.Reference == null) != (b.Reference == null)) Bad(Cat.Reference, $"{at}: reference presence");
    }

    private void Controllers(string at, ControllerTrack[] a, ControllerTrack[] b)
    {
        if (a.Length != b.Length)
        {
            Bad(Cat.Controllers, $"{at}: {a.Length} vs {b.Length} ({Ids(a)} vs {Ids(b)})");
            return;
        }
        for (int i = 0; i < a.Length; i++)
        {
            ControllerTrack x = a[i], y = b[i];
            if (x.Type != y.Type || x.Columns != y.Columns || x.Bezier != y.Bezier)
            {
                Bad(Cat.Controllers, $"{at}: #{i} id {x.Type}/{y.Type} cols {x.Columns}/{y.Columns} bez {x.Bezier}/{y.Bezier}");
                continue;
            }
            if (x.Times.Length != y.Times.Length || x.Values.Length != y.Values.Length)
            {
                Bad(Cat.ControllerKeys, $"{at}: id {x.Type} keys {x.Times.Length}/{y.Times.Length} vals {x.Values.Length}/{y.Values.Length}");
                continue;
            }
            for (int k = 0; k < x.Times.Length; k++)
                if (!Near(x.Times[k], y.Times[k])) { Bad(Cat.ControllerKeys, $"{at}: id {x.Type} t[{k}] {x.Times[k]}/{y.Times[k]}"); break; }
            for (int k = 0; k < x.Values.Length; k++)
                if (!Near(x.Values[k], y.Values[k])) { Bad(Cat.ControllerValues, $"{at}: id {x.Type} v[{k}] {x.Values[k]}/{y.Values[k]}"); break; }
        }
    }

    private static string Ids(ControllerTrack[] t)
    {
        var sb = new StringBuilder();
        foreach (ControllerTrack c in t) { if (sb.Length != 0) sb.Append(','); sb.Append(c.Type); }
        return sb.ToString();
    }

    private void Mesh(string at, MeshDef a, MeshDef b)
    {
        // A mesh that states no renderhint of its own gets its tangent frame from the material it NAMES,
        // which the engine resolved through its own chain at compile time. The corpus cannot reconstruct that
        // chain, so a tangent-presence difference on such a mesh is the harness's limit, not the parser's.
        bool external = a.RenderHintId == 0 && b.RenderHintId == 0
                        && (a.MaterialName.Length != 0 || a.Textures[0].Length != 0)
                        && (a.Tangents.Length == 0) != (b.Tangents.Length == 0);
        if (a.Positions.Length != b.Positions.Length)
        {
            // A mesh whose hint lives in a .mtr: whichever side has the tangent frame also has the
            // handedness split, so its vertex count is higher. That is the external material, not the weld.
            Bad(external ? Cat.TangentExternal : Cat.VertexCount,
                $"{at}: {a.Positions.Length} vs {b.Positions.Length} vertices"
                + (external ? $" (tangents {a.Tangents.Length}/{b.Tangents.Length}, material "
                              + $"'{(a.MaterialName.Length != 0 ? a.MaterialName : a.Textures[0])}')" : ""));
            // Everything downstream of the vertex list is indexed by it, so only the per-face and material
            // halves can still be compared -- do those rather than returning.
            Faces(at, a, b);
            MaterialFields(at, a, b);
            return;
        }
        for (int i = 0; i < a.Positions.Length; i++)
            if (!Near(a.Positions[i], b.Positions[i])) { Bad(Cat.Vertex, $"{at}: v[{i}] {a.Positions[i]} vs {b.Positions[i]}"); break; }
        if (!Faces(at, a, b)) return;
        for (int s = 0; s < 4; s++)
        {
            if (a.Uv[s].Length != b.Uv[s].Length) { Bad(Cat.Uv, $"{at}: uv{s} {a.Uv[s].Length} vs {b.Uv[s].Length}"); continue; }
            for (int i = 0; i < a.Uv[s].Length; i++)
                if (!Near(a.Uv[s][i], b.Uv[s][i])) { Bad(Cat.Uv, $"{at}: uv{s}[{i}] {a.Uv[s][i]} vs {b.Uv[s][i]}"); break; }
        }
        if (a.Colors.Length != b.Colors.Length) Bad(Cat.Color, $"{at}: colors {a.Colors.Length} vs {b.Colors.Length}");
        else
            for (int i = 0; i < a.Colors.Length; i++)
                if (a.Colors[i] != b.Colors[i]) { Bad(Cat.Color, $"{at}: c[{i}] {a.Colors[i]:x8} vs {b.Colors[i]:x8}"); break; }
        if (a.Normals.Length != b.Normals.Length) Bad(Cat.Normal, $"{at}: normals {a.Normals.Length} vs {b.Normals.Length}");
        else
            for (int i = 0; i < a.Normals.Length; i++)
                if (!Near(a.Normals[i], b.Normals[i])) { Bad(Cat.Normal, $"{at}: n[{i}] {a.Normals[i]} vs {b.Normals[i]}"); break; }
        if (a.Tangents.Length != b.Tangents.Length)
            Bad(external ? Cat.TangentExternal : Cat.Tangent,
                $"{at}: tangents {a.Tangents.Length} vs {b.Tangents.Length}"
                + (external ? $" (material '{(a.MaterialName.Length != 0 ? a.MaterialName : a.Textures[0])}')" : ""));
        else
            for (int i = 0; i < a.Tangents.Length; i++)
            {
                Vector4 x = a.Tangents[i], y = b.Tangents[i];
                // A vertex no triangle reached (an orphan a degenerate face left behind) never gets a
                // tangent: the engine's handedness pool is memset but its TANGENT pool is not, so the
                // payload carries whatever was in that memory. NaN there is an uninitialised read, not a
                // value to compare -- CX's Normalize turns the zero vector into (1,0,0) as the engine's own
                // Normalize would have.
                if (float.IsNaN(x.X) || float.IsNaN(x.Y) || float.IsNaN(x.Z) || float.IsNaN(x.W))
                { Contaminated++; continue; }
                if (!Near(x.X, y.X) || !Near(x.Y, y.Y) || !Near(x.Z, y.Z) || !Near(x.W, y.W))
                { Bad(Cat.Tangent, $"{at}: t[{i}] {x} vs {y}"); break; }
            }
        if (a.Indices.Length != b.Indices.Length) Bad(Cat.Index, $"{at}: {a.Indices.Length} indices vs {b.Indices.Length}");
        else
            for (int i = 0; i < a.Indices.Length; i++)
                if (a.Indices[i] != b.Indices[i]) { Bad(Cat.Index, $"{at}: i[{i}] {a.Indices[i]} vs {b.Indices[i]}"); break; }
        if (!Near(a.SurfaceArea, b.SurfaceArea)) Bad(Cat.SurfaceArea, $"{at}: {a.SurfaceArea} vs {b.SurfaceArea}");
        if (!Near(a.BMin, b.BMin) || !Near(a.BMax, b.BMax) || !Near(a.Average, b.Average))
            Bad(Cat.Bounds, $"{at}: bmin {a.BMin}/{b.BMin} bmax {a.BMax}/{b.BMax} avg {a.Average}/{b.Average}");
        // The engine's stored per-node radius is sometimes larger than its own bounding box allows, i.e. not
        // computable from the node's own vertices; CX computes it as ProcessVerts says to. Counted, not hidden.
        // NOT COMPARED, and not a residual either: a mesh's RADIUS and ROTATETEXTURE are mutated at RUNTIME
        // on the shared MdlNode -- CombineParts @0x473d30 grows the radius to cover a merged part, and
        // SetPartTextureRotate @0x461670 sets rotatetexture under a bumpy-shiny map. A payload compiled after
        // the model was instantiated carries both, so they are not what any parse produced. Counted only, as
        // oracle contamination the cache stream is fixing at the producer.
        if (!Near(a.Radius, b.Radius)) Contaminated++;
        if (a.RotateTexture != b.RotateTexture) Contaminated++;
        MaterialFields(at, a, b);
    }

    /// <summary>The per-face records: corners, surface id, adjacency and the plane the engine stores with
    /// them. False once the face lists cannot be walked together.</summary>
    private bool Faces(string at, MeshDef a, MeshDef b)
    {
        if (a.Faces.Length != b.Faces.Length) { Bad(Cat.FaceCount, $"{at}: {a.Faces.Length} vs {b.Faces.Length}"); return false; }
        for (int i = 0; i < a.Faces.Length; i++)
        {
            FaceDef x = a.Faces[i], y = b.Faces[i];
            // A compiled MaxFace record has no smoothing-group field -- the group only exists in the
            // authored face line and in the normals it produced -- so it is not comparable.
            if (x.V0 != y.V0 || x.V1 != y.V1 || x.V2 != y.V2 || x.Surface != y.Surface)
            { Bad(Cat.Face, $"{at}: f[{i}] {x.V0},{x.V1},{x.V2}/{x.Surface} vs {y.V0},{y.V1},{y.V2}/{y.Surface}"); break; }
        }
        for (int i = 0; i < a.Faces.Length; i++)
        {
            FaceDef x = a.Faces[i], y = b.Faces[i];
            if (x.Adj0 != y.Adj0 || x.Adj1 != y.Adj1 || x.Adj2 != y.Adj2)
            { Bad(Cat.Adjacency, $"{at}: f[{i}] adj {x.Adj0},{x.Adj1},{x.Adj2} vs {y.Adj0},{y.Adj1},{y.Adj2}"); break; }
        }
        for (int i = 0; i < a.Faces.Length; i++)
        {
            FaceDef x = a.Faces[i], y = b.Faces[i];
            if (Near(x.PlaneNormal, y.PlaneNormal) && Near(x.PlaneDistance, y.PlaneDistance)) continue;
            Bad(Cat.Plane, $"{at}: f[{i}] plane {x.PlaneNormal}/{x.PlaneDistance} vs {y.PlaneNormal}/{y.PlaneDistance}");
            break;
        }
        return true;
    }

    /// <summary>The material half: colours, the flags, the hint, the names.</summary>
    private void MaterialFields(string at, MeshDef a, MeshDef b)
    {
        if (!Near(a.Diffuse, b.Diffuse) || !Near(a.Ambient, b.Ambient) || !Near(a.Specular, b.Specular)
            || !Near(a.Shininess, b.Shininess) || a.TransparencyHint != b.TransparencyHint
            || a.TileFade != b.TileFade || a.Render != b.Render || a.Shadow != b.Shadow
            || a.Beaming != b.Beaming || a.RenderHintId != b.RenderHintId
            || !string.Equals(a.MaterialName, b.MaterialName, StringComparison.OrdinalIgnoreCase))
            Bad(Cat.Material, $"{at}: diffuse {a.Diffuse}/{b.Diffuse} ambient {a.Ambient}/{b.Ambient} spec "
                              + $"{a.Specular}/{b.Specular} shiny {a.Shininess}/{b.Shininess} thint "
                              + $"{a.TransparencyHint}/{b.TransparencyHint} tilefade {a.TileFade}/{b.TileFade} "
                              + $"r{a.Render}/{b.Render} s{a.Shadow}/{b.Shadow} b{a.Beaming}/{b.Beaming} "
                              + $"hint {a.RenderHintId}/{b.RenderHintId} mat '{a.MaterialName}'/'{b.MaterialName}'");
        for (int t = 0; t < 4; t++)
            if (!string.Equals(a.Textures[t], b.Textures[t], StringComparison.OrdinalIgnoreCase))
            { Bad(Cat.Texture, $"{at}: tex{t} '{a.Textures[t]}' vs '{b.Textures[t]}'"); break; }
        if (a.Lightmapped != b.Lightmapped)
            Bad(Cat.Texture, $"{at}: lightmapped {a.Lightmapped} vs {b.Lightmapped}");
    }

    private void Skin(string at, SkinDef a, SkinDef b)
    {
        if (a.Weights.Length != b.Weights.Length || a.Bones.Length != b.Bones.Length
            || a.BoneNodes.Length != b.BoneNodes.Length)
        {
            Bad(Cat.Skin, $"{at}: {a.Weights.Length}/{b.Weights.Length} weights, {a.BoneNodes.Length}/{b.BoneNodes.Length} bones");
            return;
        }
        for (int i = 0; i < a.Weights.Length; i++)
        {
            Vector4 x = a.Weights[i], y = b.Weights[i];
            if (Near(x.X, y.X) && Near(x.Y, y.Y) && Near(x.Z, y.Z) && Near(x.W, y.W) && a.Bones[i] == b.Bones[i])
                continue;
            Bad(Cat.Skin, $"{at}: w[{i}] {x}/{y} bones {a.Bones[i]:x8}/{b.Bones[i]:x8}");
            break;
        }
    }

    /// <summary>Every frame of a per-frame array, element by element. False once something was reported.</summary>
    private bool Frames(string at, Vector3[][] x, Vector3[][] y)
    {
        for (int f = 0; f < x.Length; f++)
        {
            if (x[f].Length != y[f].Length)
            { Bad(Cat.AnimMesh, $"{at}: vertex frame {f} {x[f].Length}/{y[f].Length}"); return false; }
            for (int i = 0; i < x[f].Length; i++)
                if (!Near(x[f][i], y[f][i]))
                { Bad(Cat.AnimMesh, $"{at}: vertex frame {f}[{i}] {x[f][i]} vs {y[f][i]}"); return false; }
        }
        return true;
    }

    /// <summary>One line per non-zero category, in declaration order.</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        foreach (Cat c in Enum.GetValues<Cat>())
            if (_counts[(int)c] != 0) { if (sb.Length != 0) sb.Append(' '); sb.Append(c).Append('=').Append(_counts[(int)c]); }
        return sb.Length == 0 ? "clean" : sb.ToString();
    }
}
