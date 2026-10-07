using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>CX-120 seam-anim: the arena's DYNAMIC half -- a range whose vertices one instance owns, because a
/// dangly mesh and an animmesh move the vertices themselves and so cannot share a content-keyed range with
/// another instance of the same model.
/// <para>Two additions, and no change to how a draw reads a range: the range is built by the arena's own
/// <c>Build</c> under a caller-chosen key (so nothing dedups onto it), and a solve patches the POSITIONS in
/// the host mirror and queues the range; the patch reaches the GPU from <see cref="MeshArena.Pump"/>, on the
/// render thread, inside the same bracket and under the same lock as every other upload. Positions only, which
/// is also all the engine's two producers write.</para>
/// <para>NEW FILE in the parts stream's pattern (CX-114 owns <c>MeshArena.cs</c>): the only edits to that file
/// are the <c>partial</c> keyword and the one <c>DynUpload()</c> call in <c>Pump</c>.</para></summary>
public static unsafe partial class MeshArena
{
    /// <summary>Ranges whose vertices changed since the last <see cref="Pump"/>. Small: one entry per dangly or
    /// animmesh part in view, and a settled cloak does not enter it at all.</summary>
    private static readonly int[] _dynDirty = new int[DynDirtyCap];
    private static int _dynDirtyN;
    private const int DynDirtyCap = 4096;
    private static long _dynUploads, _dynBytes;
    private static int _dynOverflow;

    /// <summary>A PRIVATE range holding <paramref name="mesh"/>'s rest vertices, keyed on
    /// <paramref name="unique"/> so no other instance can share it. -1 on a refusal (counted by the arena's own
    /// refusal ledger, like every other).</summary>
    internal static int AcquireDynamic(MeshDef mesh, ulong unique)
    {
        if (mesh == null) return -1;
        lock (_gate)
        {
            if (_byContent.TryGetValue(unique, out int have) && Alive(have))
            {
                _ranges[have].Refs++;
                return have;
            }
            if (mesh.Indices.Length < 3 || mesh.Positions.Length == 0) return Refuse(Refusal.Empty);
            if (mesh.Positions.Length > MaxMeshVerts) return Refuse(Refusal.TooManyVerts);
            return Build(mesh, null, unique, 0, Indices16(mesh), null, 0, null, 0);   // per-instance: no LOD runs, no second key
        }
    }

    /// <summary>Patch <paramref name="range"/>'s positions from <paramref name="pos"/> (<c>3 * n</c> floats,
    /// model space) and, when <paramref name="uv"/> is given, its diffuse UV lane from it (<c>2 * n</c>
    /// floats); queue the range for the next upload. Game thread. False = no such range, or its vertex count
    /// does not match (a mesh and a solve that disagree is a refusal, never a partial write).</summary>
    internal static bool WriteDynamic(int range, float* pos, float* uv, int n)
    {
        if (pos == null || n <= 0) return false;
        lock (_gate)
        {
            if (!Alive(range)) return false;
            ref Range rr = ref _ranges[range];
            if (n > rr.NV) return false;
            int f = (int)rr.Format, stride = StrideOf[f];
            Section s = _sect[f];
            float[] mirror = s.Mirror;
            int bas = rr.Base;
            if ((long)(bas + n) * stride > mirror.Length) return false;
            var bmin = new Vector3(float.MaxValue);
            var bmax = new Vector3(float.MinValue);
            for (int i = 0; i < n; i++)
            {
                int o = (bas + i) * stride, k = i * 3;
                float x = pos[k], y = pos[k + 1], z = pos[k + 2];
                mirror[o] = x; mirror[o + 1] = y; mirror[o + 2] = z;
                // The UVs ride the same slice, so an animmesh that scrolls its texture needs no second write.
                if (uv != null) { mirror[o + 6] = uv[i * 2]; mirror[o + 7] = uv[i * 2 + 1]; }
                if (x < bmin.X) bmin.X = x;
                if (y < bmin.Y) bmin.Y = y;
                if (z < bmin.Z) bmin.Z = z;
                if (x > bmax.X) bmax.X = x;
                if (y > bmax.Y) bmax.Y = y;
                if (z > bmax.Z) bmax.Z = z;
            }
            // The bound moves with the vertices, or a flapping cloak is culled at the edge of a pose it no
            // longer holds. The centre and radius are what the cull and the caster bake read.
            rr.BMin = bmin; rr.BMax = bmax;
            rr.Center = (bmin + bmax) * 0.5f;
            rr.Radius = (bmax - rr.Center).Length();
            Queue(range);
            return true;
        }
    }

    /// <summary>Under <see cref="_gate"/>: note the range as dirty, once.</summary>
    private static void Queue(int range)
    {
        for (int i = 0; i < _dynDirtyN; i++) if (_dynDirty[i] == range) return;
        if (_dynDirtyN >= DynDirtyCap)
        {
            _dynOverflow++;
            return;
        }
        _dynDirty[_dynDirtyN++] = range;
    }

    /// <summary>Render thread, inside the GL bracket, under <see cref="_gate"/> (called from
    /// <see cref="Pump"/>): send each dirty range's vertex slice. A range whose section has not reached the GPU
    /// yet is skipped -- the append upload that follows carries its bytes anyway.</summary>
    private static void DynUpload()
    {
        for (int i = 0; i < _dynDirtyN; i++)
        {
            int range = _dynDirty[i];
            if (!Alive(range)) continue;
            ref Range rr = ref _ranges[range];
            int f = (int)rr.Format, stride = StrideOf[f];
            Section s = _sect[f];
            if (s.Vbo == 0 || s.Uploaded < rr.Base + rr.NV) continue;
            long bytes = (long)rr.NV * stride * 4;
            fixed (float* p = s.Mirror)
                Gl.NamedBufferSubData(s.Vbo, (IntPtr)((long)rr.Base * stride * 4), (IntPtr)bytes,
                                      p + (long)rr.Base * stride);
            _dynUploads++;
            _dynBytes += bytes;
        }
        _dynDirtyN = 0;
    }

    /// <summary>One clause for the arena's period line: how much the moving vertices cost.</summary>
    internal static string DynStats()
    {
        if (_dynUploads == 0 && _dynOverflow == 0) return "";
        string s = $", dynamic: {_dynUploads} upload(s) / {_dynBytes >> 10} KiB"
                 + (_dynOverflow == 0 ? "" : $", {_dynOverflow} DROPPED past the dirty cap (a frozen cloak)");
        _dynUploads = 0; _dynBytes = 0; _dynOverflow = 0;
        return s;
    }
}

/// <summary>CX-120 seam-anim: a recipe whose geometry is ONE instance's. Interned outside
/// <c>DrawRecipe</c>'s per-(model, node) table, so it is never handed to another part, and carrying a range
/// the pose step owns.
/// <para>This is what lets a dangly part draw its solved vertices with no change at all to the record feed:
/// the feed reads the part's recipe, and the part's recipe is this one.</para></summary>
internal static partial class DrawRecipe
{
    /// <summary>Retired dynamic recipe slots, waiting to be handed out again. Only recipes
    /// <see cref="InternDynamic"/> made ever enter it: the per-(model, node) ones are interned for the model's
    /// lifetime and <c>_byNode</c> names them.</summary>
    private static readonly System.Collections.Generic.Stack<int> _dynFree = new();

    /// <summary>Give a private recipe back, with the arena range it named. The slot is left INERT -- no mesh,
    /// no range -- so <see cref="Pump"/>'s "re-ask for a range that has not landed" pass skips it, and nothing
    /// holds the <see cref="ModelDef"/> it pinned.</summary>
    internal static void RetireDynamic(int r, int range)
    {
        if (range >= 0) MeshArena.Release(range);
        if ((uint)r >= (uint)_n) return;
        // Only a PRIVATE recipe may be retired: a shared one is still every other instance's.
        if (_byNode.ContainsValue(r)) return;
        _mesh[r] = null;
        _model[r] = null;
        _range[r] = -1;
        _dynFree.Push(r);
    }

    /// <summary>A private copy of node <paramref name="node"/>'s recipe pointing at <paramref name="range"/>.
    /// -1 when the node draws nothing or the table is full.
    /// <para>CX-120: this is the one interning call made from the GAME thread (the dangly solve's first frame)
    /// rather than from the feed's fold, so it was on the render-thread brief as "move into the fold". It does
    /// NOT have to move. Both the fold and this run on the game thread, so there is no second WRITER; the
    /// table is fixed-size and never reallocates, so a reader of another recipe cannot be torn by an append;
    /// and a fresh recipe reaches the renderer only through <c>SceneDb.PartRecipe</c> + the dirty fold, which
    /// is published under <c>RenderSnapshot</c>'s lock -- the happens-before edge this needs. What IS left is
    /// RECYCLING: a recipe released and re-interned for a different mesh while the renderer still holds a
    /// frame referencing it draws the new recipe against the old range for one frame. Bounded indices into
    /// fixed arrays, so a glitch and not a corruption. ponytail: fix it by deferring the free to the frame
    /// after the last one that referenced it, if it is ever seen.</para></summary>
    internal static int InternDynamic(ModelDef model, int node, int range)
    {
        if ((uint)node >= (uint)model.Nodes.Length || range < 0) return -1;
        NodeDef nd = model.Nodes[node];
        MeshDef? mesh = nd.Mesh;
        if (mesh == null || !mesh.Render || mesh.Indices.Length == 0) return -1;
        int r;
        if (_dynFree.Count > 0) r = _dynFree.Pop();
        else if (_n < Max) r = _n++;
        else { Spilled++; return -1; }
        // Deliberately NOT filed in _byNode: another instance of this model must get its own.
        _model[r] = model; _node[r] = node; _mesh[r] = mesh;
        Fill(r, nd, mesh);
        // Fill took a reference on the SHARED range; hand it back and take the private one instead.
        if (_range[r] >= 0 && _range[r] != range) MeshArena.Release(_range[r]);
        _range[r] = range;
        return r;
    }
}
