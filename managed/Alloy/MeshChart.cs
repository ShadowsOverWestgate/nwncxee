using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The <c>lightmap</c> group (#649): baked static lighting. Build 2/4 (#673) declares it and charts;
/// the bake and the lit-stage read are #674, the loading-screen hold #675.</summary>
internal static class Lightmap
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<int> Density, Disk, Pages, Near, Tiers, HoldSeconds;
    internal static readonly Knob<bool> Movers;

    static Lightmap()
    {
        G = KnobGroup.Declare("lightmap", "Graphics", "Baked lighting", master: "Baked static lighting",
            masterHelp: "Static geometry -- tiles and placeables that never move -- gets its lights baked into a "
                      + "lightmap as the area loads, and the frame stops re-shading them. Off is the live light loop.",
            masterDefault: true, parent: "alloy.enabled",
            header: "Charts are cut per mesh as an area loads and cached on disk (cx-charts/, beside cx.cfg).");
        G.Status = MeshChart.Describe;
        Density = G.Slider("density", "Lightmap density", 2, 32, 1, 8, "px/m", tier: Tier.Advanced,
                           help: "Lightmap texels per metre of world. Each instance's rectangle is capped at a quarter page; a change re-bakes the area.",
                           changed: LightmapBake.RekeyAll);
        Pages = G.Slider("pages", "Lightmap pages", 0, LightmapBake.MaxPages, 1, 0, tier: Tier.Advanced,
                         help: "4096x4096 pages the area may hold (128 MiB each), allocated as the area needs them. 0 sizes it from the card's memory: a quarter of it in pages, at most 32. An area that would overflow the cap bakes at a lower density instead.");
        Near = G.Slider("near", "Full-density radius", 10, 200, 5, 40, "m", tier: Tier.Advanced,
                        help: "Metres from the player baked at the full density; each tier out doubles the radius and halves the density. The last tier covers the whole area and bakes first.");
        Tiers = G.Slider("tiers", "Density tiers", 1, LightmapBake.MaxTiers, 1, 3, tier: Tier.Advanced,
                         help: "Density steps between the player and the far area: 1 bakes everything at the full density, 3 is full / half / quarter. A change re-bakes the area.",
                         changed: LightmapBake.RekeyAll);
        HoldSeconds = G.Slider("hold", "Loading hold", 0, 40, 1, 20, "s",
                               help: "Seconds the loading screen may wait for the area's baked lighting before Escape is offered to skip ahead; the bake then finishes in the background. 0 never waits.");
        Movers = G.Toggle("movers", "Creature shadows from baked lights", true,
                          help: "A creature standing under a baked light casts its shadow on the baked surface. Off is cheaper: baked surfaces take no shadow from creatures under static lights.");
        // 2048: one area's bake at density 10 is ~500 MiB of .lmp blobs, and a 512 MiB budget pruned the
        // live files of the area being walked (4890 of 7425 hit, live)
        Disk = G.Slider("disk", "Chart cache size", 64, 8192, 64, 2048, "MiB", tier: Tier.Advanced,
                        help: "Disk the chart and bake caches may hold; the oldest files go first. One area's lightmap is roughly 500 MiB.");
    }
}

/// <summary>
/// The charter (#656/#655): every static mesh gets a lightmap UV chart, cut by the vendored xatlas on a
/// background worker and cached on disk by mesh CONTENT, so the second load of any area is a file read.
/// <para><b>MeshLod's sibling, with its own vertices.</b> A chart splits vertices wherever a seam falls, so
/// a charted mesh cannot ride the engine's vertex pool: the worker emits the whole mesh -- position, normal,
/// UV, chart UV -- into one Alloy VERTEX arena plus its index runs into one Alloy element arena, and a
/// charted part draws from the pair under <see cref="AlloyRenderer.ChartVaoBit"/>. Chart BEFORE simplify:
/// <see cref="MeshSimplify"/> runs on the charted mesh, so every LOD level carries chart UVs and the far
/// field is lightmapped too. The engine's own range stays untouched -- an entry that never lands, a refusal,
/// or the master off simply draws the engine's geometry, which is vanilla.</para>
/// <para><b>Worker rule</b> (MeshLod's): no GL, no engine memory. Everything it reads was copied by
/// <see cref="Offer"/>; its only calls out are xatlas (pure) and the ledger files.</para>
/// <para><b>Windows:</b> xatlas is linked into version.dll exactly as into cx_managed.so; no engine read.</para>
/// </summary>
internal static unsafe partial class MeshChart
{
    /// <summary>The mesh atlas's own density: charts are cut once at unit scale and the per-instance record
    /// (#668's <c>s</c>) folds the density knob and the transform in at bake time. Chart UVs are in these
    /// texel units; changing this invalidates every chart file (see <see cref="LedgerVersion"/>).</summary>
    internal const float AtlasTexelsPerUnit = 8f;
    /// <summary>Texels xatlas leaves between charts inside the mesh atlas (#656: 2-texel gutter).</summary>
    internal const uint Padding = 2;
    /// <summary>Floats per arena vertex: pos3 nrm3 uv2 chart2.</summary>
    internal const int Stride = 10;
    /// <summary>Alloy attribute location of the chart UV stream (3..7 are free; 0/1/2 pos/uv/nrm, 8/9 bones).</summary>
    internal const uint LocChartUv = 3;

    private const int MaxPending = 256;
    private const int MaxEntries = 1 << 16;   // the plaza filled 1 << 14 exactly and the rest stayed magenta
    /// <summary>Vertices the arena may hold (2M x 40 B = 80 MiB). ponytail: no compaction; Revoke rewinds.</summary>
    private const int MaxArenaVerts = 1 << 21;
    private const int MaxArenaIndices = 8 << 20;
    private const int LogCap = 8;

    private static ExtenderLog? _log;

    /// <summary>Why a mesh has no chart. Indexes <see cref="RefusalName"/>; 0 is charted. The first four are
    /// the worker's; <c>Arena</c> is the ceiling; <c>Unlit</c> (no normal stream: nothing to bake against) and
    /// <c>Corners</c> (past <see cref="MeshSimplify.MaxCorners"/>) are refused at the offer, before any copy.</summary>
    internal enum Refusal : byte { None, Input, Xatlas, Uncharted, Overflow, Arena, Unlit, Corners }
    internal static readonly string[] RefusalName = { "none", "input", "xatlas", "uncharted", "overflow", "arena", "unlit", "corners" };

    /// <summary>One charted mesh in the arenas: base vertex, three index runs (BYTE offsets, the LOD 1/2
    /// ones zero-count when the simplifier declined), the atlas size in texels.</summary>
    private struct Entry : ISlotEntry
    {
        internal int Base;
        internal nint Off0, Off1, Off2;
        internal int Cnt0, Cnt1, Cnt2;
        internal ushort W, H;
        internal ulong Content;
        /// <summary>The mesh's bounds in ITS OWN space (world space for a tile part): the engine's part
        /// bbox is unpopulated on tile parts (min == max), so the bake sizes its light reach from these.</summary>
        internal Vector3 Bmin, Bmax;
        internal bool Done;
        internal Refusal Refused;
        /// <summary>The range's signature at offer time (<c>AlloyRenderer.RangeSig</c>): a pool item the engine
        /// freed and refilled keeps its (slot, item) key, and without this the new tenant drew the OLD mesh
        /// out of the arena -- the floating nonsense after an area change.</summary>
        public ulong Sig { get; set; }
        /// <summary>Charted vertices in the arena, for the compaction.</summary>
        internal int NV;
    }

    private static readonly SlotJobTable<Job, Entry> _jobs = new(MaxEntries, MaxPending);
    private static Dictionary<long, Entry> _table => _jobs.Entries;

    /// <summary>Bumped by every edit that can change a <see cref="Range"/> answer (file, drop, compaction, the
    /// world edge): a <see cref="RangeMemo"/> stamped with an older one re-probes (CX-12). Starts at 1 so a
    /// zeroed memo is stale.</summary>
    private static int _epoch = 1;

    /// <summary>One caller slot's cached <see cref="Range"/> answer, valid while the table epoch, the key and
    /// the asked LOD all match: the per-part draw skips the dictionary probe and its Entry copy (CX-12).</summary>
    internal struct RangeMemo
    {
        internal long Key;
        internal nint Off;
        internal int Epoch, Base, Count;
        internal byte Lod, Level;
        internal bool Hit;
    }

    /// <summary>One mesh handed to the worker and back. Input arrays are DENSE: <see cref="Offer"/> remaps
    /// the engine's indices to first-use order, so the content key sees no stale pool bytes and two parts
    /// of one mesh hash the same.</summary>
    internal sealed class Job
    {
        internal long Key;
        internal ulong Sig;
        internal ulong Content;
        internal int IdxCount, VertCount;
        internal bool HasNrm, HasUv;
        internal ushort[] Idx = Array.Empty<ushort>();
        internal float[] Pos = Array.Empty<float>(), Nrm = Array.Empty<float>(), Uv = Array.Empty<float>();
        // the charted result
        internal int OutVerts, Out0N, Out1N, Out2N, Charts, Loose;
        internal ushort W, H;
        internal ushort[] Xref = Array.Empty<ushort>();
        internal float[] ChartUv = Array.Empty<float>();
        internal ushort[] Out0 = Array.Empty<ushort>(), Out1 = Array.Empty<ushort>(), Out2 = Array.Empty<ushort>();
        internal float[] OutPos = Array.Empty<float>();
        /// <summary>Positions rebased to the mesh's min corner and quantised to <see cref="Grid"/> -- what the
        /// key and xatlas see. Tile parts sit in WORLD space in the engine's pools (identity matrix, #655), so
        /// without this every placement of one tile model was its own content and no area shared a chart
        /// with another; a chart is translation-invariant, so the rebased mesh's chart is the placed mesh's.</summary>
        internal float[] Local = Array.Empty<float>();
        internal Vector3 Bmin, Bmax;
        internal Refusal Refused;
        internal bool Hit, Stale;
    }

    private static int[] _remap = Array.Empty<int>();

    private static float[] _vMirror = Array.Empty<float>();
    private static ushort[] _iMirror = Array.Empty<ushort>();
    private static int _vUsed, _vUploaded, _vCapacity, _iUsed, _iUploaded, _iCapacity;
    private static uint _vbo, _ebo, _vao;
    private static bool _arenaFull;
    /// <summary>Arena vertices whose entry is gone (a vacated slot, a refilled item): what a compaction
    /// reclaims. The world edge (<see cref="Revoke"/>) does not fire on an in-module area change, so
    /// without it three areas filled the 2M-vertex arena and the fourth charted nothing (run 29: 11268
    /// refused, viz 43 magenta across the town).</summary>
    private static long _deadVerts;
    private static int _compactions;

    private static int _offered, _hit, _missed, _built, _refused, _stale, _tableFull, _logLeft = LogCap;
    /// <summary>Charted meshes at or past <see cref="MeshLod.MinTris"/>, and those that kept a generated level.</summary>
    private static int _lodBig, _lodKept;
    private static readonly int[] _refusedBy = new int[RefusalName.Length];

    /// <summary>#675: meshes offered, meshes answered (hit or built), and the difference still on the worker.</summary>
    internal static int Offered => _offered;
    internal static int Done => _hit + _missed;
    internal static int InFlight => _offered - _hit - _missed;
    /// <summary>A mesh refused before the worker ever saw it: counted, so the period line's refused total
    /// names every static mesh that has no chart, not only the ones that reached xatlas.</summary>
    internal static void Skip(Refusal why) { _refused++; _refusedBy[(int)why]++; }
    private static int _drawn;

    /// <summary>Is a charted range available to pick this frame? False leaves every draw on the engine's own
    /// geometry -- the stand-down, not a second code path.</summary>
    internal static bool Armed => _vao != 0 && Lightmap.G.Master is { V: true };

    /// <summary>The VAO over both arenas, zero until the first chart lands in this context.</summary>
    internal static uint Vao => _vao;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _ = Lightmap.G;
        if (_jobs.Running) return;
        _jobs.Start("cx-chart", Work);
        log.Info("MeshChart: worker up -- static meshes are charted as they enrol when lightmap.enabled=1 (default off)");
    }

    internal static long KeyOf(int slot, int item) => SlotJobTable<Job, Entry>.KeyOf(slot, item);

    /// <summary>Charts on file, claimed or landed -- the area edge's tally (CX-19).</summary>
    internal static int TableCount => _table.Count;

    /// <summary>Asked before anything is read: a mesh on file, queued or past a budget costs one probe.
    /// <paramref name="later"/> = not now but not refused either: the worker's queue is full (an area's
    /// whole tile set enrols in one walk), and the caller re-offers when it drains -- an offer that fell
    /// through here used to be lost for good, which left most of an area uncharted (#674 live).</summary>
    internal static bool Wanted(int slot, int item, int idxCount, ulong sig, out bool later)
    {
        later = false;
        if (!_jobs.Running || idxCount < 3 || !Lightmap.G.Master!.V) return false;
        if (_table.TryGetValue(KeyOf(slot, item), out Entry have))
        {
            if (have.Sig == sig) return false;
            _table.Remove(KeyOf(slot, item));   // the item was refilled under the same key: chart the new mesh
            _epoch++;
            if (have.Done && have.Refused == Refusal.None) _deadVerts += have.NV;
        }
        if (_table.Count >= MaxEntries) { _tableFull++; return false; }
        // full with dead bytes in it: the next Pump compacts, and the offer comes back through the deferred list
        if (_arenaFull) { if (_deadVerts > 0) { later = true; return false; } Skip(Refusal.Arena); return false; }
        if (idxCount > MeshSimplify.MaxCorners) { Skip(Refusal.Corners); return false; }
        later = !_jobs.QueueHasRoom;
        return !later;
    }

    /// <summary>Room in the worker's queue for a deferred offer?</summary>
    internal static bool HasRoom() => _jobs.QueueHasRoom;

    /// <summary>Copy one part's range and every attribute it addresses off the pools' host shadows, densely
    /// re-indexed, and queue it. Enrollment walk only: reads engine memory and allocates, off every frame path.</summary>
    internal static void Offer(int slot, int item, byte* elemHost, long first, int count,
                               byte* posHost, in VertexStream pos, byte* nrmHost, in VertexStream nrm,
                               byte* uvHost, in VertexStream uv, int baseVertex, ulong sig)
    {
        if (elemHost == null || posHost == null || count < 3 || count % 3 != 0) return;
        if (pos.Type != Gl.FLOAT || pos.Size != 3) return;
        bool hasNrm = nrmHost != null && nrm.Type == Gl.FLOAT && nrm.Size == 3;
        bool hasUv = uvHost != null && uv.Type == Gl.FLOAT && uv.Size == 2;
        ushort* src = (ushort*)(elemHost + first * 2);
        int maxI = -1;
        for (int i = 0; i < count; i++) { int v = src[i]; if (v > maxI) maxI = v; }
        if (maxI < 0) return;
        if (_remap.Length <= maxI) _remap = new int[Math.Max(maxI + 1, MeshSimplify.MaxVerts)];
        Array.Fill(_remap, -1, 0, maxI + 1);

        Job j = _jobs.Rent();
        j.Key = KeyOf(slot, item); j.Sig = sig;
        j.IdxCount = count; j.HasNrm = hasNrm; j.HasUv = hasUv;
        j.Refused = Refusal.None; j.Hit = false; j.Stale = false; j.Out0N = j.Out1N = j.Out2N = 0; j.OutVerts = 0; j.Charts = 0; j.Loose = 0;
        Grow(ref j.Idx, count);
        if (j.Pos.Length < (maxI + 1) * 3) j.Pos = new float[(maxI + 1) * 3];
        if (hasNrm && j.Nrm.Length < (maxI + 1) * 3) j.Nrm = new float[(maxI + 1) * 3];
        if (hasUv && j.Uv.Length < (maxI + 1) * 2) j.Uv = new float[(maxI + 1) * 2];
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            int v = src[i];
            int d = _remap[v];
            if (d < 0)
            {
                d = n++; _remap[v] = d;
                float* p = (float*)(posHost + pos.Off + (long)(baseVertex + v) * pos.Stride);
                j.Pos[d * 3] = p[0]; j.Pos[d * 3 + 1] = p[1]; j.Pos[d * 3 + 2] = p[2];
                if (hasNrm)
                {
                    float* q = (float*)(nrmHost + nrm.Off + (long)(baseVertex + v) * nrm.Stride);
                    j.Nrm[d * 3] = q[0]; j.Nrm[d * 3 + 1] = q[1]; j.Nrm[d * 3 + 2] = q[2];
                }
                if (hasUv)
                {
                    float* t = (float*)(uvHost + uv.Off + (long)(baseVertex + v) * uv.Stride);
                    j.Uv[d * 2] = t[0]; j.Uv[d * 2 + 1] = t[1];
                }
            }
            j.Idx[i] = (ushort)d;
        }
        j.VertCount = n;
        _offered++;
        _jobs.Offer(j, j.Key, sig);
    }

    private static void Grow(ref ushort[] a, int n) { if (a.Length < n) a = new ushort[n]; }
    private static void Grow(ref float[] a, int n) { if (a.Length < n) a = new float[n]; }

    /// <summary>The worker: content key, ledger probe, xatlas on a miss, then the two LOD runs over the
    /// charted mesh. One mesh at a time, byte-identical across runs (xatlas single-threaded, fixed options).</summary>
    private static void Work(Job j)
    {
        try { Chart(j); }
        catch (Exception e) { j.Refused = Refusal.Input; _log?.Info($"MeshChart: worker fault -- {e.Message}"); }
    }

    /// <summary>Quantum of <see cref="Job.Local"/>: 1/1024 m, under any texel at the densest knob, coarse
    /// enough that two placements' float rounding lands on one grid point.</summary>
    private const float Grid = 1024f;

    /// <summary>Chart one job in place: the pure step the self-check drives on a fixture too.</summary>
    internal static void Chart(Job j, bool useLedger = true)
    {
        j.Content = ContentKey(j);
        if (useLedger && LedgerLoad(j)) j.Hit = true;
        else
        {
            j.Refused = Unwrap(j);
            if (j.Refused != Refusal.None) return;
            if (useLedger) LedgerSave(j);
        }
        Expand(j);
        Simplify(j);
    }

    private static void Rebase(Job j)
    {
        int n = j.VertCount * 3;
        Grow(ref j.Local, n);
        float mx = float.MaxValue, my = float.MaxValue, mz = float.MaxValue;
        float hx = float.MinValue, hy = float.MinValue, hz = float.MinValue;
        for (int i = 0; i < n; i += 3)
        {
            mx = MathF.Min(mx, j.Pos[i]); my = MathF.Min(my, j.Pos[i + 1]); mz = MathF.Min(mz, j.Pos[i + 2]);
            hx = MathF.Max(hx, j.Pos[i]); hy = MathF.Max(hy, j.Pos[i + 1]); hz = MathF.Max(hz, j.Pos[i + 2]);
        }
        j.Bmin = new Vector3(mx, my, mz); j.Bmax = new Vector3(hx, hy, hz);
        for (int i = 0; i < n; i += 3)
        {
            j.Local[i] = MathF.Round((j.Pos[i] - mx) * Grid) / Grid;
            j.Local[i + 1] = MathF.Round((j.Pos[i + 1] - my) * Grid) / Grid;
            j.Local[i + 2] = MathF.Round((j.Pos[i + 2] - mz) * Grid) / Grid;
        }
    }

    /// <summary>FNV-1a over the dense mesh: counts, rebased positions, normals, UVs, indices. The disk identity.</summary>
    internal static ulong ContentKey(Job j)
    {
        Rebase(j);
        ulong h = 14695981039346656037UL;
        void Mix(ReadOnlySpan<byte> b) { foreach (byte x in b) { h ^= x; h *= 1099511628211UL; } }
        Span<int> hdr = stackalloc int[4] { j.VertCount, j.IdxCount, j.HasNrm ? 1 : 0, j.HasUv ? 1 : 0 };
        Mix(MemoryMarshal.AsBytes(hdr));
        Mix(MemoryMarshal.AsBytes(j.Local.AsSpan(0, j.VertCount * 3)));
        if (j.HasNrm) Mix(MemoryMarshal.AsBytes(j.Nrm.AsSpan(0, j.VertCount * 3)));
        if (j.HasUv) Mix(MemoryMarshal.AsBytes(j.Uv.AsSpan(0, j.VertCount * 2)));
        Mix(MemoryMarshal.AsBytes(j.Idx.AsSpan(0, j.IdxCount)));
        return h;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChartOut
    {
        internal void* Atlas; internal float* Uv; internal uint* Xref; internal uint* Index;
        internal uint VertexCount, IndexCount, ChartCount, Width, Height, LooseVerts;
    }

    [DllImport("cxatlas")]
    private static extern int cx_chart(float* pos, float* nrm, float* uv, uint vertCount, ushort* idx, uint idxCount,
                                       float texelsPerUnit, uint padding, ChartOut* result);
    [DllImport("cxatlas")]
    private static extern void cx_chart_free(ChartOut* result);

    /// <summary>xatlas over the job's input; fills Xref/ChartUv/Out0 and the atlas size. Refusal codes are
    /// the shim's, with Overflow for a result past 16-bit indexing.</summary>
    private static Refusal Unwrap(Job j)
    {
        ChartOut r;
        int rc;
        fixed (float* p = j.Local) fixed (float* n = j.Nrm) fixed (float* u = j.Uv) fixed (ushort* ix = j.Idx)
            rc = cx_chart(p, j.HasNrm ? n : null, j.HasUv ? u : null, (uint)j.VertCount, ix, (uint)j.IdxCount,
                          AtlasTexelsPerUnit, Padding, &r);
        if (rc != 0) return rc switch { 1 => Refusal.Input, 2 => Refusal.Xatlas, 4 => Refusal.Overflow, _ => Refusal.Uncharted };
        try
        {
            if (r.VertexCount > 65535 || r.IndexCount != (uint)j.IdxCount || r.Width > 65535 || r.Height > 65535)
                return Refusal.Overflow;
            int nv = (int)r.VertexCount, ni = (int)r.IndexCount;
            Grow(ref j.Xref, nv); Grow(ref j.ChartUv, nv * 2); Grow(ref j.Out0, ni);
            for (int i = 0; i < nv; i++)
            {
                if (r.Xref[i] >= (uint)j.VertCount) return Refusal.Xatlas;
                j.Xref[i] = (ushort)r.Xref[i];
                j.ChartUv[i * 2] = r.Uv[i * 2]; j.ChartUv[i * 2 + 1] = r.Uv[i * 2 + 1];
            }
            for (int i = 0; i < ni; i++)
            {
                if (r.Index[i] >= (uint)nv) return Refusal.Xatlas;
                j.Out0[i] = (ushort)r.Index[i];
            }
            j.OutVerts = nv; j.Out0N = ni; j.W = (ushort)r.Width; j.H = (ushort)r.Height; j.Charts = (int)r.ChartCount; j.Loose = (int)r.LooseVerts;
            return Refusal.None;
        }
        finally { cx_chart_free(&r); }
    }

    /// <summary>De-interleaved positions of the charted vertices, for the simplifier.</summary>
    private static void Expand(Job j)
    {
        Grow(ref j.OutPos, j.OutVerts * 3);
        for (int i = 0; i < j.OutVerts; i++)
        {
            int s = j.Xref[i] * 3;
            j.OutPos[i * 3] = j.Pos[s]; j.OutPos[i * 3 + 1] = j.Pos[s + 1]; j.OutPos[i * 3 + 2] = j.Pos[s + 2];
        }
    }

    /// <summary>MeshLod's two levels over the charted mesh: same targets, same error budgets, same "kept only
    /// if it saves enough" rule. Chart seams are attribute discontinuities, which the simplifier locks.</summary>
    private static void Simplify(Job j)
    {
        int tris = j.Out0N / 3;
        j.Out1N = j.Out2N = 0;
        if (tris < MeshLod.MinTris) return;
        Grow(ref j.Out1, j.Out0N); Grow(ref j.Out2, j.Out0N);
        j.Out1N = MeshSimplify.Run(j.Out0, j.Out0N, j.OutPos, j.OutVerts, (int)(tris * MeshLod.Target1), MeshLod.Error1, j.Out1);
        if (j.Out1N > tris * 3 * (1f - MeshLod.MinSaving)) j.Out1N = 0;
        j.Out2N = MeshSimplify.Run(j.Out0, j.Out0N, j.OutPos, j.OutVerts, (int)(tris * MeshLod.Target2), MeshLod.Error2, j.Out2);
        if (j.Out2N > tris * 3 * (1f - MeshLod.MinSaving) || j.Out2N >= j.Out1N && j.Out1N > 0) j.Out2N = 0;
    }

    /// <summary>Collect what the worker finished into the arenas. Render thread, inside the bracket, once a
    /// frame, before <c>BuildBatches</c>: the only place a GL name is touched.</summary>
    internal static void Pump()
    {
        if (!_jobs.Running || Gl.CreateBuffers == null) return;
        if (_deadVerts > MaxArenaVerts / 2 || (_arenaFull && _deadVerts > 0)) Compact();
        int vFrom = _vUsed, iFrom = _iUsed;
        while (_jobs.TryTake(out Job j))
        {
            var e = new Entry { Done = true, Refused = j.Refused, Content = j.Content, Bmin = j.Bmin, Bmax = j.Bmax, Sig = j.Sig };
            if (j.Hit) _hit++; else _missed++;
            if (j.Stale) _stale++;
            if (j.Refused == Refusal.None)
            {
                if (!j.Hit) _built++;
                if (!AppendVerts(j, out e.Base)) e.Refused = Refusal.Arena;
                else
                {
                    Append(j.Out0, j.Out0N, out e.Off0, out e.Cnt0);
                    if (j.Out1N > 0) Append(j.Out1, j.Out1N, out e.Off1, out e.Cnt1);
                    if (j.Out2N > 0) Append(j.Out2, j.Out2N, out e.Off2, out e.Cnt2);
                    if (e.Cnt0 == 0) e.Refused = Refusal.Arena;
                    else
                    {
                        if (e.Cnt0 / 3 >= MeshLod.MinTris) _lodBig++;
                        if (e.Cnt1 > 0 || e.Cnt2 > 0) _lodKept++;
                    }
                    e.W = j.W; e.H = j.H; e.NV = j.OutVerts;
                }
            }
            if (e.Refused != Refusal.None)
            {
                _refused++; _refusedBy[(int)e.Refused]++;
                if (_logLeft-- > 0)
                    _log?.Info($"MeshChart: REFUSED {RefusalName[(int)e.Refused]} -- mesh {j.IdxCount / 3} tris, {j.VertCount} verts, content {j.Content:x16}");
            }
            else if (_logLeft-- > 0)
                _log?.Info($"MeshChart: mesh {j.IdxCount / 3} tris {j.VertCount} verts -> {j.OutVerts} charted verts, atlas {j.W}x{j.H} ({(j.Hit ? "cache hit" : "built")}), lod {e.Cnt1 / 3}/{e.Cnt2 / 3}" + (j.Loose > 0 ? $", {j.Loose} loose verts (no face)" : ""));
            if (_jobs.File(j.Key, j.Sig, e)) _epoch++;
            else if (e.Refused == Refusal.None) _deadVerts += e.NV;   // appended, unclaimed: the compaction takes it back
            _jobs.Recycle(j);
        }
        if (_vUsed != vFrom || _vUploaded != _vUsed) UploadVerts(vFrom);
        if (_iUsed != iFrom || _iUploaded != _iUsed) UploadIndices(iFrom);
        if (_vao == 0 && _vbo != 0 && _ebo != 0) BuildVao();
    }

    /// <summary>Interleave the charted vertices onto the vertex mirror. False = the arena ceiling.</summary>
    private static bool AppendVerts(Job j, out int baseVertex)
    {
        baseVertex = 0;
        int n = j.OutVerts;
        if (n <= 0 || _vUsed + n > MaxArenaVerts) { _arenaFull = true; return false; }
        if (_vMirror.Length < (_vUsed + n) * Stride)
        {
            int cap = _vMirror.Length == 0 ? (1 << 16) * Stride : _vMirror.Length;
            while (cap < (_vUsed + n) * Stride) cap *= 2;
            if (cap > MaxArenaVerts * Stride) cap = MaxArenaVerts * Stride;
            Array.Resize(ref _vMirror, cap);
        }
        for (int i = 0; i < n; i++)
        {
            int o = (_vUsed + i) * Stride, s = j.Xref[i];
            _vMirror[o] = j.Pos[s * 3]; _vMirror[o + 1] = j.Pos[s * 3 + 1]; _vMirror[o + 2] = j.Pos[s * 3 + 2];
            if (j.HasNrm) { _vMirror[o + 3] = j.Nrm[s * 3]; _vMirror[o + 4] = j.Nrm[s * 3 + 1]; _vMirror[o + 5] = j.Nrm[s * 3 + 2]; }
            else _vMirror[o + 3] = _vMirror[o + 4] = _vMirror[o + 5] = 0f;
            if (j.HasUv) { _vMirror[o + 6] = j.Uv[s * 2]; _vMirror[o + 7] = j.Uv[s * 2 + 1]; }
            else _vMirror[o + 6] = _vMirror[o + 7] = 0f;
            _vMirror[o + 8] = j.ChartUv[i * 2]; _vMirror[o + 9] = j.ChartUv[i * 2 + 1];
        }
        baseVertex = _vUsed;
        _vUsed += n;
        return true;
    }

    /// <summary>Reserve element-arena room for one run; a zero count means the ceiling.</summary>
    private static void Append(ushort[] run, int n, out nint off, out int count)
    {
        off = 0; count = 0;
        if (_iUsed + n > MaxArenaIndices) { _arenaFull = true; return; }
        if (_iMirror.Length < _iUsed + n)
        {
            int cap = _iMirror.Length == 0 ? 1 << 16 : _iMirror.Length;
            while (cap < _iUsed + n) cap *= 2;
            if (cap > MaxArenaIndices) cap = MaxArenaIndices;
            Array.Resize(ref _iMirror, cap);
        }
        Array.Copy(run, 0, _iMirror, _iUsed, n);
        off = (nint)((long)_iUsed * 2); count = n;
        _iUsed += n;
    }

    /// <summary>Both uploads are by name, touching no binding. A grow respecifies under the same NAME, so the
    /// VAO built over it stays valid.</summary>
    private static void UploadVerts(int from)
    {
        if (_vbo == 0) { uint b; Gl.CreateBuffers(1, &b); if (b == 0) return; _vbo = b; _vUploaded = 0; _vCapacity = 0; }
        fixed (float* p = _vMirror)
        {
            if (_vUploaded < _vUsed && _vCapacity >= _vUsed)
                Gl.NamedBufferSubData(_vbo, (IntPtr)((long)from * Stride * 4),
                                 (IntPtr)((long)(_vUsed - from) * Stride * 4), p + (long)from * Stride);
            else
            {
                _vCapacity = _vMirror.Length / Stride;
                Gl.NamedBufferData(_vbo, (IntPtr)((long)_vMirror.Length * 4), p, Gl.STATIC_DRAW);
            }
        }
        _vUploaded = _vUsed;
    }

    private static void UploadIndices(int from)
    {
        if (_ebo == 0) { uint b; Gl.CreateBuffers(1, &b); if (b == 0) return; _ebo = b; _iUploaded = 0; _iCapacity = 0; }
        fixed (ushort* p = _iMirror)
        {
            if (_iUploaded < _iUsed && _iCapacity >= _iUsed)
                Gl.NamedBufferSubData(_ebo, (IntPtr)((long)from * 2), (IntPtr)((long)(_iUsed - from) * 2), p + from);
            else
            {
                _iCapacity = _iMirror.Length;
                Gl.NamedBufferData(_ebo, (IntPtr)((long)_iCapacity * 2), p, Gl.STATIC_DRAW);
            }
        }
        _iUploaded = _iUsed;
    }

    /// <summary>ONE VAO for every charted part: the arena carries every stream a stage reads (a program that
    /// wants no normal or UV simply never fetches the location), so there is nothing per VDO slot to vary.
    /// Built by name, so the draw loops' bound-VAO latch is never disturbed.</summary>
    private static void BuildVao()
    {
        uint vao; Gl.CreateVertexArrays(1, &vao);
        const int bytes = Stride * 4;
        VertexArrays.Bind(vao, AlloyRenderer.LocPos, new VertexStream { Buf = _vbo, Size = 3, Type = Gl.FLOAT, Stride = bytes, Off = 0 });
        VertexArrays.Bind(vao, AlloyRenderer.LocNrm, new VertexStream { Buf = _vbo, Size = 3, Type = Gl.FLOAT, Stride = bytes, Off = 12 });
        VertexArrays.Bind(vao, AlloyRenderer.LocUv, new VertexStream { Buf = _vbo, Size = 2, Type = Gl.FLOAT, Stride = bytes, Off = 24 });
        VertexArrays.Bind(vao, LocChartUv, new VertexStream { Buf = _vbo, Size = 2, Type = Gl.FLOAT, Stride = bytes, Off = 32 });
        Gl.VertexArrayElementBuffer(vao, _ebo);
        _vao = vao;
    }

    /// <summary>This mesh's charted range at <paramref name="lod"/> (falling back to the finer level that
    /// exists). False = draw the engine's own geometry, which every caller must be able to do.</summary>
    internal static bool Range(int slot, int item, int lod, out int baseVertex, out nint off, out int count, out int level)
    {
        baseVertex = 0; off = 0; count = 0; level = 0;
        if (_vao == 0 || !_table.TryGetValue(KeyOf(slot, item), out Entry e) || !e.Done || e.Refused != Refusal.None) return false;
        baseVertex = e.Base;
        if (lod >= 2 && e.Cnt2 > 0) { off = e.Off2; count = e.Cnt2; level = 2; return true; }
        if (lod >= 1 && e.Cnt1 > 0) { off = e.Off1; count = e.Cnt1; level = 1; return true; }
        off = e.Off0; count = e.Cnt0;
        return true;
    }

    /// <summary><see cref="Range"/> through the caller's <paramref name="m"/>: one probe per (key, LOD) per
    /// table epoch instead of one per call.</summary>
    internal static bool Range(ref RangeMemo m, int slot, int item, int lod, out int baseVertex, out nint off, out int count, out int level)
    {
        if (_vao == 0) { baseVertex = 0; off = 0; count = 0; level = 0; return false; }
        long key = KeyOf(slot, item);
        if (m.Epoch != _epoch || m.Key != key || m.Lod != lod)
        {
            m.Hit = Range(slot, item, lod, out m.Base, out m.Off, out m.Count, out int lvl);
            m.Key = key; m.Epoch = _epoch; m.Lod = (byte)lod; m.Level = (byte)lvl;
        }
        baseVertex = m.Base; off = m.Off; count = m.Count; level = m.Level;
        return m.Hit;
    }

    /// <summary>The atlas size a charted mesh's UVs are in, its content key and its own-space bounds, for the
    /// bake (#674). False = not charted (yet, or refused).</summary>
    internal static bool Atlas(int slot, int item, out int w, out int h, out ulong content, out Vector3 bmin, out Vector3 bmax)
    {
        w = h = 0; content = 0; bmin = bmax = default;
        if (!_table.TryGetValue(KeyOf(slot, item), out Entry e) || !e.Done || e.Refused != Refusal.None) return false;
        w = e.W; h = e.H; content = e.Content; bmin = e.Bmin; bmax = e.Bmax;
        return true;
    }

    internal static void Drew(int indices) => _drawn += indices / 3;

    /// <summary>A VDO slot was vacated or re-pointed: every range keyed on it names geometry that is no
    /// longer there. Arena bytes stay until <see cref="Revoke"/>.</summary>
    internal static void ForgetSlot(int slot)
    {
        _jobs.ForgetSlot(slot, static e => { if (e.Done && e.Refused == Refusal.None) _deadVerts += e.NV; });
        _epoch++;
    }

    /// <summary>Drop every entry no enrolled part keys (the engine frees a pool item without a word, and an
    /// in-module area change is no world edge: run 31 carried 42k entries over three areas and compacted 5k
    /// vertices out of 2M). Their arena bytes count as dead; the next Pump compacts. Cold: once per area.
    /// An entry a not-yet-enrolled part will want is offered again at its enrolment.</summary>
    internal static void Sweep(HashSet<long> live)
    {
        if (_table.Count == 0) return;
        List<long>? dead = null;
        foreach (var kv in _table)
            if (!live.Contains(kv.Key)) (dead ??= new List<long>()).Add(kv.Key);
        if (dead == null) return;
        _epoch++;
        int verts = 0;
        for (int i = 0; i < dead.Count; i++)
        {
            if (_table.TryGetValue(dead[i], out Entry e) && e.Done && e.Refused == Refusal.None) { _deadVerts += e.NV; verts += e.NV; }
            _table.Remove(dead[i]);
        }
        _log?.Info($"MeshChart: swept {dead.Count} entries nobody enrolled ({verts} vertices) -- {_table.Count} kept");
    }

    /// <summary>Rewrite both arenas with only the live entries, in place under the same buffer names (the VAO
    /// stays valid), and re-upload. Render thread, before the frame's batches are built, so no draw holds an
    /// old offset. Cold: an area change's worth of copying.</summary>
    private static void Compact()
    {
        var keys = new List<long>(_table.Keys);
        _epoch++;
        // ascending by arena place, so every copy slides DOWN over bytes already moved or dead
        keys.Sort((a, b) => _table[a].Base.CompareTo(_table[b].Base));
        int vU = 0, iU = 0;
        for (int k = 0; k < keys.Count; k++)
        {
            Entry e = _table[keys[k]];
            if (!e.Done || e.Refused != Refusal.None) continue;
            if (vU != e.Base) Array.Copy(_vMirror, (long)e.Base * Stride, _vMirror, (long)vU * Stride, (long)e.NV * Stride);
            e.Base = vU; vU += e.NV;
            e.Off0 = Move(e.Off0, e.Cnt0, ref iU);
            e.Off1 = Move(e.Off1, e.Cnt1, ref iU);
            e.Off2 = Move(e.Off2, e.Cnt2, ref iU);
            _table[keys[k]] = e;
        }
        int freed = _vUsed - vU;
        _vUsed = vU; _iUsed = iU; _vUploaded = 0; _iUploaded = 0; _arenaFull = false; _deadVerts = 0; _compactions++;
        // the whole compacted range goes back up NOW: the caller's incremental upload starts at the
        // post-compaction cursor and would have left the GPU copy in the old layout under the new offsets
        if (_vbo != 0 && vU > 0) UploadVerts(0);
        if (_ebo != 0 && iU > 0) UploadIndices(0);
        _log?.Info($"MeshChart: arena compacted -- {freed} of {vU + freed} vertices reclaimed, {keys.Count} entries kept");
    }

    /// <summary>Slide one index run down to the compaction cursor. Runs were appended in the same order as
    /// their vertices, so the Base-sorted visit copies each run to a cursor at or below its old place.</summary>
    private static nint Move(nint off, int count, ref int cursor)
    {
        if (count <= 0) return 0;
        int from = (int)((long)off / 2);
        if (from != cursor) Array.Copy(_iMirror, from, _iMirror, cursor, count);
        nint at = (nint)((long)cursor * 2);
        cursor += count;
        return at;
    }

    /// <summary>The world edge: every (slot, item) key means nothing and both arenas rewind.</summary>
    internal static void Revoke()
    {
        _jobs.Revoke();
        _epoch++;
        _vUsed = _vUploaded = _iUsed = _iUploaded = 0; _arenaFull = false; _deadVerts = 0;
        _offered = _hit = _missed = _built = _refused = _stale = _lodBig = _lodKept = 0; Array.Clear(_refusedBy);
        _logLeft = LogCap;
    }

    /// <summary>The context is gone: forget every name, never delete. The mirrors go back up on the next Pump.</summary>
    internal static void ContextLost()
    {
        _vao = 0; _vbo = 0; _ebo = 0; _vUploaded = 0; _iUploaded = 0; _vCapacity = 0; _iCapacity = 0;
    }

    /// <summary>The <c>lod</c> panel's clause for the charted population, which never reaches <see cref="MeshLod"/>.</summary>
    internal static string LodNote()
        => !Armed ? "" : $"{Done - _refused} charted meshes, {_lodBig} of them at least {MeshLod.MinTris} triangles, {_lodKept} simplified";

    internal static string Describe()
        => !_jobs.Running ? "not running"
         : !Lightmap.G.Master!.V ? "Off — every mesh draws from the game's own geometry"
         : $"{_offered} meshes offered: {_hit} charts from cache, {_built} built, {_refused} refused; "
           + $"{_vUsed} arena vertices ({_vUsed * Stride * 4 >> 20} MiB)" + (_arenaFull ? "; the arena is full" : "")
           + "; " + LightmapBake.Describe();

    /// <summary>One clause of the Alloy period line: charts hit/missed/built/refused, and what drew from them.</summary>
    internal static string Stats(int frames)
    {
        int d = _drawn; _drawn = 0;
        if (_offered == 0 && d == 0) return "";
        if (frames <= 0) frames = 1;
        string why = "";
        for (int k = 1; k < RefusalName.Length; k++) if (_refusedBy[k] > 0) why += $" {RefusalName[k]} {_refusedBy[k]}";
        return $"charts {_hit} hit / {_missed} missed / {_built} built / {_refused} refused{(why.Length > 0 ? " (" + why.Trim() + ")" : "")}"
               + (_tableFull > 0 ? $", {_tableFull} offers PAST THE {MaxEntries}-MESH TABLE" : "")
               + (_stale > 0 ? $", {_stale} stale files replaced" : "")
               + $", {d / frames} charted tris per frame";
    }
}
