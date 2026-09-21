using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The bake cache (#666): one blob per page bake under <c>cx-charts/</c> beside the chart ledger --
/// the bake's union readback (uint32 RG11B10F texels, then RGBA8, <c>uw x uh</c> each) deflated behind a table
/// of the rects it holds (key, place inside the union). An in-memory index over every blob's table (built once
/// at thread start, a later bake of a key wins) answers a probe with a reference into the inflated union, so
/// adoption is one row-strided <c>glTexSubImage3D</c> per page and no allocation. Per-rect files were the
/// load: 7425 opens + a utime write each behind an 18k-file sweep took 10 s on a cold box (2026-09-21).
/// Reads and writes on one background thread (<c>cx-lmcache</c>); the render thread hands over keys and
/// takes back answers through two queues. Shares the chart ledger's byte budget (<c>lightmap.disk</c>, one
/// directory, one prune over every file in it): the writer prunes oldest-first every <see cref="PruneEvery"/>
/// blobs -- a pruned blob is a whole page's cache gone, and its index entries miss on the next load.
/// <para>ponytail: the ledger's own running tally does not see these bytes until it re-settles (it counts
/// only its own writes); the prune here keeps the directory under budget either way. Upgrade path if the two
/// ever fight: one shared tally behind a lock in <c>MeshChart.Ledger</c>.</para></summary>
internal sealed class BakeCache
{
    /// <summary>"CXLP" little-endian.</summary>
    private const uint Magic = 0x504c5843;   // not-an-offset: the file's first four bytes
    /// <summary>Bumped with every change to what a baked texel holds or how the file is laid out: a file from
    /// an older bake is stale, whatever its key. 8 = page blobs (7 was one file per rect).</summary>
    internal const int Version = 8;
    /// <summary>Blobs written between prunes (a blob is a page: 32 is a budget's worth).</summary>
    private const int PruneEvery = 32;
    private const string Ext = ".lmp";

    /// <summary>One rect inside a blob's union: its key and place (relative to the union's origin).</summary>
    internal struct Entry { internal ulong Key; internal int X, Y, W, H; }
    /// <summary>A probe (Rect &gt;= 0), its answer (Data = the union or null, X/Y/Uw/Uh place the rect in it),
    /// or a write (Data = the union readback, Table = its rects).</summary>
    private struct Job { internal int Rect; internal ulong Key; internal int W, H; internal byte[]? Data; internal int X, Y, Uw, Uh; internal Entry[]? Table; internal bool Write; internal int Buf; }
    private readonly Queue<Job> _in = new();
    private readonly Queue<Job> _out = new();
    private readonly object _gate = new();
    private Thread? _thread;
    private ExtenderLog? _log;
    private int _writes;
    /// <summary>Key -&gt; where it sits in which blob. Cache thread only.</summary>
    private readonly Dictionary<ulong, (int file, Entry e)> _index = new();
    private readonly List<string> _files = new();
    /// <summary>Probes of one drain, grouped by blob so each is read and inflated once.</summary>
    private readonly Dictionary<int, List<Job>> _byFile = new();
    /// <summary>Refused blobs (version, truncation, gone) this session; the period line prints it.</summary>
    internal int Stale;
    /// <summary>Writes queued and not yet on disk: while it is above zero the render thread's readback
    /// buffer is still being read from and must not be re-used.</summary>
    internal int Outstanding;
    /// <summary>Bytes the cache thread allocated this period (unions, deflate streams).</summary>
    internal long Allocated;
    /// <summary>Per readback buffer: writes still reading from it. The render thread re-uses a buffer at 0.</summary>
    internal readonly int[] Busy = new int[4];

    internal void Attach(ExtenderLog log)
    {
        _log = log;
        if (_thread != null) return;
        _thread = new Thread(Work) { IsBackground = true, Name = "cx-lmcache" };
        _thread.Start();
    }

    /// <summary>Ask for a key's rect; the answer (a union and the rect's place in it, or null for a miss) comes
    /// back through <see cref="TryTake"/>.</summary>
    internal void Probe(int rect, ulong key, int w, int h)
    {
        lock (_gate) { _in.Enqueue(new Job { Rect = rect, Key = key, W = w, H = h }); Monitor.Pulse(_gate); }
    }

    /// <summary>Queue a page bake's union readback (A texels then B texels, <paramref name="uw"/> x
    /// <paramref name="uh"/> each) for the disk as one blob, with the rects it holds.</summary>
    internal void Save(byte[] union, int buf, int uw, int uh, Entry[] table)
    {
        Interlocked.Increment(ref Outstanding); Interlocked.Increment(ref Busy[buf]);
        lock (_gate) { _in.Enqueue(new Job { Rect = -1, Data = union, Buf = buf, Uw = uw, Uh = uh, Table = table, Write = true }); Monitor.Pulse(_gate); }
    }

    internal bool TryTake(out int rect, out ulong key, out byte[]? data, out int x, out int y, out int uw, out int uh)
    {
        lock (_gate)
        {
            if (_out.Count == 0) { rect = -1; key = 0; data = null; x = y = uw = uh = 0; return false; }
            Job j = _out.Dequeue();
            rect = j.Rect; key = j.Key; data = j.Data; x = j.X; y = j.Y; uw = j.Uw; uh = j.Uh;
            return true;
        }
    }

    /// <summary>The world edge: answers in flight name rects that no longer exist.</summary>
    internal void Drain()
    {
        lock (_gate)
        {
            var keep = new Queue<Job>();
            while (_in.Count > 0) { Job j = _in.Dequeue(); if (j.Write) keep.Enqueue(j); }
            while (keep.Count > 0) _in.Enqueue(keep.Dequeue());
            _out.Clear();
        }
    }

    /// <summary>Once, when the thread starts: every blob's table into the index (oldest first, so a later bake
    /// of a key wins), then the dead weight out -- blobs of another format version and the per-rect files of
    /// the old scheme (<c>.lmr</c>), which the prune counted against the budget and never deleted on its own.
    /// The index comes first: on a cold box the delete of 18k files is seconds, and the probes queue behind it.</summary>
    private void BuildIndex()
    {
        try
        {
            string dir = MeshChart.LedgerDir();
            if (!Directory.Exists(dir)) return;
            var blobs = new DirectoryInfo(dir).GetFiles("*" + Ext);
            Array.Sort(blobs, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            int stale = 0; long staleBytes = 0;
            foreach (var f in blobs)
            {
                Entry[]? table = ReadTable(f.FullName, out _, out _);
                if (table == null) { staleBytes += f.Length; f.Delete(); stale++; continue; }
                Index(f.FullName, table);
            }
            int old = 0; long oldBytes = 0;
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.lmr")) { oldBytes += f.Length; f.Delete(); old++; }
            _log?.Info($"LightmapBake: bake cache indexed -- {_index.Count} rects in {_files.Count} page blobs"
                       + (stale > 0 ? $", {stale} blobs of older formats swept ({staleBytes >> 20} MiB)" : "")
                       + (old > 0 ? $", {old} per-rect files of the old scheme swept ({oldBytes >> 20} MiB)" : ""));
        }
        catch (Exception e) { _log?.Info($"LightmapBake: the bake cache index faulted -- {e.Message}"); }
    }

    private void Index(string file, Entry[] table)
    {
        int id = _files.Count; _files.Add(file);
        for (int i = 0; i < table.Length; i++) _index[table[i].Key] = (id, table[i]);
    }

    /// <summary>The blob's table alone (the header, no inflate); null = not a blob of this version.</summary>
    private static Entry[]? ReadTable(string file, out int uw, out int uh)
    {
        uw = uh = 0;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            var r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) return null;
            uw = r.ReadInt32(); uh = r.ReadInt32();
            int n = r.ReadInt32();
            if (n <= 0 || n > 1 << 20) return null;
            var table = new Entry[n];
            for (int i = 0; i < n; i++) table[i] = new Entry { Key = r.ReadUInt64(), X = r.ReadInt32(), Y = r.ReadInt32(), W = r.ReadInt32(), H = r.ReadInt32() };
            return table;
        }
        catch (Exception) { return null; }
    }

    private void Work()
    {
        BuildIndex();
        var batch = new List<Job>();
        while (true)
        {
            batch.Clear();
            lock (_gate)
            {
                while (_in.Count == 0) Monitor.Wait(_gate);
                while (_in.Count > 0) batch.Add(_in.Dequeue());
            }
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            try { Serve(batch); }
            catch (Exception e) { _log?.Info($"LightmapBake: the bake cache faulted -- {e.Message}"); }
            finally { Interlocked.Add(ref Allocated, GC.GetAllocatedBytesForCurrentThread() - a0); }
        }
    }

    /// <summary>One drain of the queue: the writes, then the probes grouped by blob -- each blob read and
    /// inflated once, every rect of it answered off that one union, the union then let go.</summary>
    private void Serve(List<Job> batch)
    {
        _byFile.Clear();
        for (int k = 0; k < batch.Count; k++)
        {
            Job j = batch[k];
            if (j.Write) { try { WriteBlob(j); } catch (Exception e) { _log?.Info($"LightmapBake: a blob write faulted -- {e.Message}"); } finally { Interlocked.Decrement(ref Outstanding); Interlocked.Decrement(ref Busy[j.Buf]); } continue; }
            if (_index.TryGetValue(j.Key, out var hit) && hit.e.W == j.W && hit.e.H == j.H)
            {
                // the place is taken NOW: a write later in this batch can re-point the key at another blob
                j.X = hit.e.X; j.Y = hit.e.Y;
                if (!_byFile.TryGetValue(hit.file, out var list)) _byFile[hit.file] = list = new List<Job>();
                list.Add(j);
            }
            else Answer(j);
        }
        foreach (var (file, jobs) in _byFile)
        {
            byte[]? union = LoadUnion(_files[file], out int uw, out int uh);
            if (union == null) Forget(file);
            for (int k = 0; k < jobs.Count; k++)
            {
                Job j = jobs[k];
                if (union != null) { j.Data = union; j.Uw = uw; j.Uh = uh; }
                Answer(j);
            }
        }
        _byFile.Clear();
    }

    private void Answer(Job j) { lock (_gate) _out.Enqueue(j); }

    /// <summary>A blob that would not load (pruned, cut, or another version): its keys miss from now on.</summary>
    private void Forget(int file)
    {
        Interlocked.Increment(ref Stale);
        var dead = new List<ulong>();
        foreach (var kv in _index) if (kv.Value.file == file) dead.Add(kv.Key);
        for (int i = 0; i < dead.Count; i++) _index.Remove(dead[i]);
        try { if (System.IO.File.Exists(_files[file])) System.IO.File.Delete(_files[file]); } catch (Exception) { }
    }

    /// <summary>Header + table + deflated union; pure.</summary>
    internal static void SerializeTo(Stream s, int uw, int uh, Entry[] table, byte[] union)
    {
        var bw = new BinaryWriter(s);
        bw.Write(Magic); bw.Write(Version); bw.Write(uw); bw.Write(uh); bw.Write(table.Length);
        for (int i = 0; i < table.Length; i++) { bw.Write(table[i].Key); bw.Write(table[i].X); bw.Write(table[i].Y); bw.Write(table[i].W); bw.Write(table[i].H); }
        bw.Flush();
        using var z = new DeflateStream(s, CompressionLevel.Fastest, leaveOpen: true);
        z.Write(union, 0, uw * uh * 8);
    }

    /// <summary>The inverse of the union half; the table is <see cref="ReadTable"/>'s. Null names the refusal
    /// (version, truncation) -- never throws.</summary>
    internal static byte[]? Deserialize(Stream s, out int uw, out int uh, out Entry[]? table, out string? why)
    {
        why = null; uw = uh = 0; table = null;
        try
        {
            var r = new BinaryReader(s);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) { why = "version"; return null; }
            uw = r.ReadInt32(); uh = r.ReadInt32();
            int n = r.ReadInt32();
            if (n <= 0 || n > 1 << 20 || uw <= 0 || uh <= 0 || uw > LightmapBake.PageSize || uh > LightmapBake.PageSize) { why = "truncation"; return null; }
            table = new Entry[n];
            for (int i = 0; i < n; i++) table[i] = new Entry { Key = r.ReadUInt64(), X = r.ReadInt32(), Y = r.ReadInt32(), W = r.ReadInt32(), H = r.ReadInt32() };
            int bytes = uw * uh * 8;
            var union = new byte[bytes];
            using var z = new DeflateStream(s, CompressionMode.Decompress, leaveOpen: true);
            int got = 0;
            while (got < bytes) { int k = z.Read(union, got, bytes - got); if (k <= 0) break; got += k; }
            if (got != bytes) { why = "truncation"; return null; }
            return union;
        }
        catch (Exception) { why = "truncation"; return null; }
    }

    /// <summary>Null on any refusal or I/O fault: a throw here would strand every probe of the batch in Probing.</summary>
    private byte[]? LoadUnion(string file, out int uw, out int uh)
    {
        uw = uh = 0;
        try
        {
            if (!System.IO.File.Exists(file)) return null;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            byte[]? union = Deserialize(fs, out uw, out uh, out _, out string? why);
            if (union == null) _log?.Info($"LightmapBake: blob {Path.GetFileName(file)} refused ({why}) -- its page re-bakes");
            return union;
        }
        catch (Exception e) { _log?.Info($"LightmapBake: blob {Path.GetFileName(file)} unreadable ({e.Message}) -- its page re-bakes"); return null; }
    }

    /// <summary>One blob per page bake, named by the wall clock (the index orders by mtime anyway); its rects
    /// join the index at once so a repack inside the session hits them.</summary>
    private void WriteBlob(Job j)
    {
        string dir = MeshChart.LedgerDir();
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"{DateTime.UtcNow.Ticks:x16}{Ext}");
        using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            SerializeTo(fs, j.Uw, j.Uh, j.Table!, j.Data!);
        Index(file, j.Table!);
        if (++_writes % PruneEvery == 0) MeshChart.Prune(dir, (long)Lightmap.Disk.V << 20);
    }

    /// <summary>Headless: the round trip of a two-rect union and the refusals.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "BakeCache.SelfCheck");
        const int uw = 32, uh = 16;
        var union = new byte[uw * uh * 8];
        for (int i = 0; i < union.Length; i++) union[i] = (byte)(i * 7);
        var table = new[] { new Entry { Key = 0x1234, X = 8, Y = 4, W = 16, H = 8 }, new Entry { Key = 0x5678, X = 0, Y = 12, W = 32, H = 4 } };
        var ms = new MemoryStream();
        SerializeTo(ms, uw, uh, table, union);
        byte[] blob = ms.ToArray();
        byte[]? back = Deserialize(new MemoryStream(blob), out int bw, out int bh, out Entry[]? bt, out string? why);
        check.Expect(back != null && why == null && bw == uw && bh == uh && back.AsSpan().SequenceEqual(union), "the union round-trips byte-identical");
        check.Expect(bt != null && bt.Length == 2 && bt[1].Key == 0x5678 && bt[1].Y == 12 && bt[0].W == 16, "the table round-trips");
        check.Expect(Deserialize(new MemoryStream(blob, 0, blob.Length / 2), out _, out _, out _, out why) == null && why == "truncation", "a cut blob is refused as truncation");
        blob[4] ^= 1;
        check.Expect(Deserialize(new MemoryStream(blob), out _, out _, out _, out why) == null && why == "version", "another version is refused as version");
        // the row-strided upload's address of a rect's texel: plane, row, column inside the union
        check.Expect(LightmapBake.UnionOffset(0, 8, 4, uw, uh) == (4 * uw + 8) * 4 && LightmapBake.UnionOffset(1, 8, 4, uw, uh) == uw * uh * 4 + (4 * uw + 8) * 4, "a rect's first texel is addressed by plane, row and column of the union");
        log.Info(check.Ok ? "BakeCache.SelfCheck: passed" : "BakeCache.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}

internal static partial class LightmapBake
{
    private static readonly BakeCache _cache = new();

    /// <summary>Byte offset of a rect's first texel in a union readback (plane 0 = A, 1 = B; 4 bytes a texel).</summary>
    internal static long UnionOffset(int plane, int x, int y, int uw, int uh) => (long)plane * uw * uh * 4 + ((long)y * uw + x) * 4;
}
