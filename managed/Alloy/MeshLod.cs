using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The <c>lod</c> group: automatic mesh detail. Touched from <see cref="RenderCommands.Register"/>
/// so the group declares at Initialize on the main thread, as every other Alloy group does.</summary>
internal static class Lod
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Px1, Px2, Bias;

    static Lod()
    {
        G = KnobGroup.Declare("lod", "Graphics", "Mesh detail", master: "Automatic mesh detail",
            masterHelp: "Distant walls and floors are drawn from a simplified copy of their own mesh. "
                      + "Off draws every model at full detail at every distance, which is what the game does.",
            parent: "alloy.enabled",
            header: "NWN models ship no detail levels; these are generated as an area loads.");
        G.Status = MeshLod.Describe;
        Px1 = G.Slider("px1", "Full detail above", 50f, 2000f, 10f, 500f, "px",
                       help: "A mesh wider than this on screen is drawn exactly as authored.");
        Px2 = G.Slider("px2", "Lowest detail below", 10f, 1000f, 10f, 150f, "px",
                       help: "Below this a mesh is drawn from its smallest copy. Between the two it takes the middle one.");
        Bias = G.Slider("bias", "Detail bias", 0.25f, 4f, 0.05f, 1f, tier: Tier.Advanced,
                        help: "Scales both thresholds' effect: above 1 keeps detail further away, below 1 drops it sooner.");
    }
}

/// <summary>
/// Automatic mesh LOD. NWN models carry no LOD chain, so every mesh has always drawn its full triangle count
/// at every distance and both GPU towers scale with area-in-view. This generates two simplified INDEX RUNS
/// per eligible mesh -- roughly half and a fifth of its triangles -- and the staging walk picks one by the
/// part's projected screen radius.
/// <para><b>Reindex only.</b> Nothing here touches a vertex. The simplified runs hold the same index values
/// the engine's own run held, so the vertex pools, the base vertex, the attribute formats and every VAO
/// stream are the engine's, unchanged; the LOD runs live in one Alloy-owned element buffer and the only
/// difference at draw time is which <c>ELEMENT_ARRAY_BUFFER</c> the VAO carries and which range the draw
/// names. That is why a stand-down is free: drop back to the engine's range and the mesh is whole again.</para>
/// <para><b>Generated off the frame.</b> Enrollment is a cold edge, but simplifying every tile of an area on
/// it would be a multi-second hitch, so the enrollment walk only COPIES the range (indices plus the positions
/// they address) and a single background worker does the quadric work. Results are collected on the render
/// thread in <see cref="Pump"/>, which is the one place a GL buffer may be touched; until a mesh's runs land
/// there it simply draws at full detail.</para>
/// <para><b>Windows:</b> no new engine symbol and no new engine-memory read -- the two host shadows and the
/// index range are the ones <c>NormalWeld</c> and the staging walk already read on both platforms.</para>
/// </summary>
internal static unsafe class MeshLod
{
    /// <summary>Detail levels: the engine's own run, then the two generated ones.</summary>
    internal const int Levels = 3;
    /// <summary>Triangle fraction each generated level aims at.</summary>
    internal const float Target1 = 0.5f, Target2 = 0.2f;
    /// <summary>Error budget per level as a fraction of the mesh's squared bounding-box diagonal. A collapse
    /// past it is refused, so a mesh whose shape cannot survive thinning keeps the triangles it has.</summary>
    internal const float Error1 = 0.0004f, Error2 = 0.004f;
    /// <summary>Triangles under which a mesh is not worth a level of its own: two extra runs and a batch cut
    /// cost more than the triangles they save.</summary>
    internal const int MinTris = 200;
    /// <summary>A generated run has to retire this fraction of the triangles to be kept at all; otherwise the
    /// level is left absent and the draw stays on the engine's range.</summary>
    internal const float MinSaving = 0.15f;

    /// <summary>Meshes waiting on the worker. The queue IS the budget: a full queue refuses the offer without
    /// creating a table entry, so the next enrollment offers the same mesh again.</summary>
    private const int MaxPending = 256;
    /// <summary>Meshes the table may hold. An area's tileset is a few thousand distinct ranges.</summary>
    private const int MaxEntries = 1 << 14;
    /// <summary>Indices the shared element buffer may hold before the arena stops accepting runs (16 MiB).</summary>
    private const int ArenaCeiling = 8 << 20;

    private static ExtenderLog? _log;

    /// <summary>One mesh's generated runs. <see cref="Off1"/>/<see cref="Off2"/> are BYTE offsets into
    /// <see cref="_ebo"/>, which is what <c>glMultiDrawElementsBaseVertex</c> takes; a zero count is a level
    /// the simplifier declined and the selection falls through to the next coarser one, or to the engine's.</summary>
    private struct Entry
    {
        internal nint Off1, Off2;
        internal int Cnt1, Cnt2;
        internal bool Done;
    }

    /// <summary>Every mesh on file. Touched ONLY from the render thread -- the enrollment walk that offers a
    /// mesh, <see cref="Pump"/> that files its runs, the staging walk that reads them, and both lifetime
    /// edges all run there -- so it takes no lock and the per-part read on the staging walk costs a probe.
    /// The worker never sees it; <see cref="_gate"/> guards the three queues, which is the whole of the
    /// cross-thread surface.</summary>
    private static readonly Dictionary<long, Entry> _table = new(MaxEntries);
    private static readonly object _gate = new();

    /// <summary>One mesh handed to the worker and back. The arrays are pooled through
    /// <see cref="_free"/> -- an area's enrollment offers thousands of these.</summary>
    private sealed class Job
    {
        internal long Key;
        internal int IdxCount, VertCount, Out1N, Out2N;
        internal ushort[] Idx = Array.Empty<ushort>();
        internal float[] Pos = Array.Empty<float>();
        internal ushort[] Out1 = Array.Empty<ushort>();
        internal ushort[] Out2 = Array.Empty<ushort>();
    }

    private static readonly Queue<Job> _in = new();
    private static readonly Queue<Job> _out = new();
    private static readonly Stack<Job> _free = new();
    private static Thread? _worker;
    private static bool _quit;

    private static ushort[] _mirror = Array.Empty<ushort>();
    private static int _used, _uploaded;
    private static uint _ebo;
    private static bool _arenaFull;

    private static int _offered, _generated, _declined, _dropped;
    private static int _logLeft = 8;
    private static long _srcTris, _lod1Tris, _lod2Tris;
    private static readonly long[] _drawnTris = new long[Levels];

    /// <summary>Is a generated level available to pick this frame? False leaves every draw on the engine's own
    /// range, which is vanilla detail -- the stand-down, not a second code path.</summary>
    internal static bool Armed => _ebo != 0 && Lod.G.Master is { V: true };

    /// <summary>The element buffer the LOD VAOs bind. Zero until the first run lands.</summary>
    internal static uint Ebo => _ebo;

    /// <summary>Start the worker and read the boot knob. Once, from the pass roll-call.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _ = Lod.G;
        if (_worker != null) return;
        _quit = false;
        _worker = new Thread(Work) { IsBackground = true, Name = "cx-meshlod" };
        _worker.Start();
        log.Info("MeshLod: armed -- eligible static meshes get two generated index runs (lod.enabled=0 draws every mesh as authored)");
    }

    private static long KeyOf(int slot, int item) => ((long)slot << 32) | (uint)item;

    /// <summary>Should the enrollment walk copy this range out for the worker? Asked before anything is read,
    /// so a mesh already on file, already queued, or past a budget costs one dictionary probe.</summary>
    internal static bool Wanted(int slot, int item, int idxCount)
    {
        if (_worker == null || _arenaFull || idxCount < MinTris * 3
            || idxCount > MeshSimplify.MaxCorners || !Lod.G.Master!.V) return false;
        if (_table.Count >= MaxEntries || _table.ContainsKey(KeyOf(slot, item))) return false;
        lock (_gate) return _in.Count < MaxPending;
    }

    /// <summary>Copy one part's index range and the positions it addresses off the pools' host shadows and
    /// queue it. Called from the enrollment walk, on whatever thread that runs -- it reads engine memory and
    /// allocates, and does neither on a frame path.</summary>
    internal static void Offer(int slot, int item, byte* elemHost, long first, int count,
                               byte* vertHost, in VertexStream pos, int baseVertex)
    {
        if (elemHost == null || vertHost == null || count < 3 || count % 3 != 0) return;
        if (pos.Type != Gl.FLOAT || pos.Size != 3) return;
        ushort* src = (ushort*)(elemHost + first * 2);
        int maxI = -1;
        for (int i = 0; i < count; i++) { int v = src[i]; if (v > maxI) maxI = v; }
        int verts = maxI + 1;
        if (verts <= 0 || verts > MeshSimplify.MaxVerts) return;

        Job j = Rent();
        j.Key = KeyOf(slot, item);
        j.IdxCount = count; j.VertCount = verts; j.Out1N = 0; j.Out2N = 0;
        Grow(ref j.Idx, count); Grow(ref j.Out1, count); Grow(ref j.Out2, count);
        if (j.Pos.Length < verts * 3) j.Pos = new float[verts * 3];
        for (int i = 0; i < count; i++) j.Idx[i] = src[i];
        for (int i = 0; i < count; i++)
        {
            int v = src[i];
            float* p = (float*)(vertHost + pos.Off + (long)(baseVertex + v) * pos.Stride);
            j.Pos[v * 3] = p[0]; j.Pos[v * 3 + 1] = p[1]; j.Pos[v * 3 + 2] = p[2];
        }
        _table[j.Key] = default;   // claimed: a later enrollment must not offer the same range again
        _offered++;
        lock (_gate) { _in.Enqueue(j); Monitor.Pulse(_gate); }
    }

    private static Job Rent()
    {
        lock (_gate) if (_free.Count > 0) return _free.Pop();
        return new Job();
    }

    private static void Grow(ref ushort[] a, int n) { if (a.Length < n) a = new ushort[n]; }

    /// <summary>The worker. One mesh at a time, no GL, no engine memory -- everything it reads was copied for
    /// it by <see cref="Offer"/>, so nothing here can outlive a pool the engine freed.</summary>
    private static void Work()
    {
        while (true)
        {
            Job j;
            lock (_gate)
            {
                while (_in.Count == 0 && !_quit) Monitor.Wait(_gate);
                if (_quit) return;
                j = _in.Dequeue();
            }
            int tris = j.IdxCount / 3;
            j.Out1N = MeshSimplify.Run(j.Idx, j.IdxCount, j.Pos, j.VertCount,
                                       (int)(tris * Target1), Error1, j.Out1);
            if (j.Out1N > tris * 3 * (1f - MinSaving)) j.Out1N = 0;
            j.Out2N = MeshSimplify.Run(j.Idx, j.IdxCount, j.Pos, j.VertCount,
                                       (int)(tris * Target2), Error2, j.Out2);
            if (j.Out2N > tris * 3 * (1f - MinSaving) || j.Out2N >= j.Out1N && j.Out1N > 0) j.Out2N = 0;
            lock (_gate) _out.Enqueue(j);
        }
    }

    /// <summary>Collect whatever the worker finished and append it to the shared element buffer. Render
    /// thread, inside the Alloy bracket, once a frame: this is the only place a GL name is touched.</summary>
    internal static void Pump()
    {
        if (_worker == null || Gl.GenBuffers == null || Gl.BufferData == null || Gl.BufferSubData == null) return;
        int dirtyFrom = _used;
        while (true)
        {
            Job j;
            lock (_gate)
            {
                if (_out.Count == 0) break;
                j = _out.Dequeue();
            }
            var e = new Entry { Done = true };
            if (j.Out1N > 0 && Append(j.Out1, j.Out1N, out e.Off1)) { e.Cnt1 = j.Out1N; _lod1Tris += j.Out1N / 3; }
            if (j.Out2N > 0 && Append(j.Out2, j.Out2N, out e.Off2)) { e.Cnt2 = j.Out2N; _lod2Tris += j.Out2N / 3; }
            if (e.Cnt1 > 0 || e.Cnt2 > 0)
            {
                _generated++; _srcTris += j.IdxCount / 3;
                // Bounded: the first few are the acceptance signal, and a whole area's worth would be a flood.
                if (_logLeft-- > 0)
                    _log?.Info($"MeshLod: mesh {j.IdxCount / 3} tris -> {e.Cnt1 / 3} / {e.Cnt2 / 3} at lod 1 / 2");
            }
            else _declined++;
            // Revoke may have wiped the table while this job was in flight; its arena bytes went with it.
            if (_table.ContainsKey(j.Key)) _table[j.Key] = e;
            lock (_gate) _free.Push(j);
        }
        if (_used == dirtyFrom && _uploaded == _used) return;
        Upload(dirtyFrom);
    }

    /// <summary>Reserve arena room for one run. False means the ceiling is reached and the level is dropped.</summary>
    private static bool Append(ushort[] run, int n, out nint off)
    {
        off = 0;
        if (_used + n > ArenaCeiling) { _arenaFull = true; _dropped++; return false; }
        if (_mirror.Length < _used + n)
        {
            int cap = _mirror.Length == 0 ? 1 << 16 : _mirror.Length;
            while (cap < _used + n) cap *= 2;
            if (cap > ArenaCeiling) cap = ArenaCeiling;
            Array.Resize(ref _mirror, cap);
        }
        Array.Copy(run, 0, _mirror, _used, n);
        off = (nint)((long)_used * 2);
        _used += n;
        return true;
    }

    /// <summary>Push the arena to GL. A grow respecifies the buffer -- the NAME is unchanged, so every LOD VAO
    /// built over it stays valid -- and re-sends everything; otherwise only the new tail goes up.</summary>
    private static void Upload(int dirtyFrom)
    {
        if (_ebo == 0) { uint b; Gl.GenBuffers(1, &b); if (b == 0) return; _ebo = b; _uploaded = 0; }
        // VAO 0 first: binding an element buffer while one of ours is bound would rewrite ITS element binding.
        int savedVao; Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &savedVao);
        Gl.BindVertexArray(0);
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _ebo);
        fixed (ushort* p = _mirror)
        {
            if (_uploaded < _used && _capacity >= _used)
                Gl.BufferSubData(Gl.ELEMENT_ARRAY_BUFFER, (IntPtr)((long)dirtyFrom * 2),
                                 (IntPtr)((long)(_used - dirtyFrom) * 2), p + dirtyFrom);
            else
            {
                _capacity = _mirror.Length;
                Gl.BufferData(Gl.ELEMENT_ARRAY_BUFFER, (IntPtr)((long)_capacity * 2), p, Gl.STATIC_DRAW);
            }
        }
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, 0);
        Gl.BindVertexArray((uint)savedVao);
        _uploaded = _used;
    }

    private static int _capacity;

    /// <summary>The detail level a sphere of world radius <paramref name="r"/> at <paramref name="dist"/> from
    /// the eye earns. <paramref name="scale"/> is the cull pass's pixels-per-unit-radius-at-unit-depth; the
    /// distance stands in for the clip w the GPU test uses, which is exact on the view axis and conservative
    /// (one level sooner at most) off it.</summary>
    internal static int Level(float r, float dist, float scale)
    {
        if (scale <= 0f || dist <= 0f || r <= 0f) return 0;
        float px = 2f * r * scale / dist * MathF.Max(Lod.Bias.V, 0.01f);
        if (px >= Lod.Px1.V) return 0;
        return px >= MathF.Min(Lod.Px2.V, Lod.Px1.V) ? 1 : 2;
    }

    /// <summary>This mesh's generated range at <paramref name="lod"/>, falling back to the coarser level's
    /// neighbour when one was declined. False means "draw the engine's own range", which every caller must be
    /// able to do -- there is no level a mesh is obliged to have.</summary>
    internal static bool Range(int slot, int item, int lod, out nint off, out int count)
    {
        off = 0; count = 0;
        if (lod <= 0 || _ebo == 0) return false;
        if (!_table.TryGetValue(KeyOf(slot, item), out Entry e) || !e.Done) return false;
        if (lod >= 2 && e.Cnt2 > 0) { off = e.Off2; count = e.Cnt2; return true; }
        if (e.Cnt1 > 0) { off = e.Off1; count = e.Cnt1; return true; }
        if (e.Cnt2 > 0) { off = e.Off2; count = e.Cnt2; return true; }
        return false;
    }

    /// <summary>Period-line tally: triangles submitted at each level. Called from the batch walk, which is
    /// single-threaded by the time it runs.</summary>
    internal static void Drew(int lod, int indices) => _drawnTris[lod] += indices / 3;

    /// <summary>A VDO slot was vacated or re-pointed: every range keyed on it names geometry that is no
    /// longer there. The arena bytes are left where they are -- the next <see cref="Revoke"/> reclaims
    /// them -- because compacting it would move every other mesh's offsets.</summary>
    internal static void ForgetSlot(int slot)
    {
        if (_table.Count == 0) return;
        List<long>? dead = null;
        foreach (var kv in _table)
            if ((int)(kv.Key >> 32) == slot) (dead ??= new List<long>()).Add(kv.Key);
        if (dead == null) return;
        for (int i = 0; i < dead.Count; i++) _table.Remove(dead[i]);
    }

    /// <summary>The world edge: the part table is gone, so every (slot, item) key means nothing. The arena is
    /// rewound with it, which is the only point its bytes are ever reclaimed.</summary>
    internal static void Revoke()
    {
        _table.Clear();
        lock (_gate)
        {
            while (_in.Count > 0) _free.Push(_in.Dequeue());
            while (_out.Count > 0) _free.Push(_out.Dequeue());
        }
        _used = 0; _uploaded = 0; _arenaFull = false;
        Array.Clear(_drawnTris);
        _srcTris = _lod1Tris = _lod2Tris = 0;
        _generated = _declined = _offered = _dropped = 0;
    }

    /// <summary>The context is gone, so the buffer name means nothing -- forgotten, never deleted (the number
    /// is already back in circulation). The arena's bytes survive in the mirror and go back up on the first
    /// <see cref="Pump"/> of the next context.</summary>
    internal static void ContextLost()
    {
        _ebo = 0; _uploaded = 0; _capacity = 0;
    }

    /// <summary>The settings panel's status block.</summary>
    internal static string Describe()
        => _worker == null
           ? "not running"
           : $"{_offered} meshes offered, {_generated} simplified, {_declined} declined; {_srcTris} source triangles -> "
             + $"{_lod1Tris} at half / {_lod2Tris} at a fifth"
             + (_arenaFull ? "; the index arena is full and further levels are dropped" : "");

    /// <summary>One clause of the Alloy period line: what the frame actually drew at each level.</summary>
    internal static string Stats(int frames)
    {
        long t0 = _drawnTris[0], t1 = _drawnTris[1], t2 = _drawnTris[2];
        Array.Clear(_drawnTris);
        if (frames <= 0) frames = 1;
        if (t0 + t1 + t2 == 0) return "";
        return $"lod {t0 / frames}/{t1 / frames}/{t2 / frames} tris per frame at lod 0/1/2"
               + $" ({_generated} meshes simplified, {_declined} declined, {_dropped} dropped)";
    }

    /// <summary>Off-device: the arena hands out non-overlapping byte offsets, a level the simplifier declined
    /// is absent rather than empty, and a coarse request falls back to the level that exists.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        if (!MeshSimplify.SelfCheck(log)) return false;
        bool ok = true;
        void E(bool c, string what)
        { if (!c) { ok = false; log.Info($"MeshLod.SelfCheck: FAILED -- {what}"); } }

        int savedUsed = _used;
        uint savedEbo = _ebo;
        _used = 0; _ebo = 1;   // a stand-in name: nothing below binds it
        var run = new ushort[] { 0, 1, 2, 0, 2, 3 };
        E(Append(run, 6, out nint a) && a == 0, "the first run does not start at offset 0");
        E(Append(run, 6, out nint b) && b == 12, "the second run does not follow the first");
        _table[KeyOf(7, 3)] = new Entry { Done = true, Off1 = a, Cnt1 = 6 };
        E(Range(7, 3, 1, out nint o1, out int c1) && o1 == a && c1 == 6, "a level-1 request does not find its run");
        E(Range(7, 3, 2, out nint o2, out int c2) && o2 == a && c2 == 6, "a missing level 2 does not fall back to level 1");
        E(!Range(7, 3, 0, out _, out _), "level 0 is a generated range");
        E(!Range(7, 4, 1, out _, out _), "a mesh with no entry answered a range");
        _table.Remove(KeyOf(7, 3));
        _used = savedUsed; _ebo = savedEbo;

        log.Info(ok ? "MeshLod.SelfCheck: passed" : "MeshLod.SelfCheck: FAILED");
        return ok;
    }
}
