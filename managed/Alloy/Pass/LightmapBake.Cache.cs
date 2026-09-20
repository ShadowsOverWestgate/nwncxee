using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The bake cache (#666): one file per bake key under <c>cx-charts/</c> beside the chart ledger,
/// holding the instance's rect from both pages as raw packed bits (uint32 RG11B10F, then RGBA8), deflated --
/// so adoption is one <c>glTexSubImage3D</c> per page at the packed offset. Reads and writes on one
/// background thread (<c>cx-lmcache</c>); the render thread hands over keys and takes back answers through
/// two queues. Shares the chart ledger's byte budget (<c>lightmap.disk</c>, one directory, one prune over
/// every file in it): the writer prunes oldest-first every <see cref="PruneEvery"/> files.
/// <para>ponytail: the ledger's own running tally does not see these bytes until it re-settles (it counts
/// only its own writes); the prune here keeps the directory under budget either way. Upgrade path if the two
/// ever fight: one shared tally behind a lock in <c>MeshChart.Ledger</c>.</para></summary>
internal sealed class BakeCache
{
    /// <summary>"CXLM" little-endian.</summary>
    private const uint Magic = 0x4d4c5843;   // not-an-offset: the file's first four bytes
    /// <summary>Bumped with every change to what a baked texel holds: a file from an older bake is stale, whatever its key.</summary>
    internal const int Version = 7;
    private const int HeaderBytes = 4 + 4 + 8 + 4 + 4;
    /// <summary>Every 16 was a directory enumeration of 40k files on the cache thread 1500 times a period (9 s CPU,
    /// 1.5 GB of FileInfo garbage), which starved the readback ring: 18k of 23k landings went uncached (run 13).</summary>
    private const int PruneEvery = 1024;

    /// <summary>A probe (Rect &gt;= 0) or a write: the rect's raw bits, or where it sits in a union readback.</summary>
    private struct Job { internal int Rect; internal ulong Key; internal int W, H; internal byte[]? Data; internal int Ux, Uy, Uw, Uh, X, Y; internal bool Write; internal int Buf; }
    private readonly Queue<Job> _in = new();
    private readonly Queue<Job> _out = new();
    private readonly object _gate = new();
    private Thread? _thread;
    private ExtenderLog? _log;
    private int _writes;
    /// <summary>Refused files replaced this session; the period line prints it.</summary>
    internal int Stale;
    /// <summary>Writes queued and not yet on disk: while it is above zero the render thread's readback
    /// buffer is still being read from and must not be re-used.</summary>
    internal int Outstanding;
    /// <summary>Bytes the cache thread allocated this period (slices, blobs, deflate streams).</summary>
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

    private static string File(ulong key) => Path.Combine(MeshChart.LedgerDir(), $"{key:x16}.lmr");

    /// <summary>Ask for a key's rect; the answer (data, or null for a miss) comes back through <see cref="TryTake"/>.</summary>
    internal void Probe(int rect, ulong key, int w, int h)
    {
        lock (_gate) { _in.Enqueue(new Job { Rect = rect, Key = key, W = w, H = h }); Monitor.Pulse(_gate); }
    }

    /// <summary>Queue a landed rect for the disk: it sits at (x, y) inside the union readback <paramref name="data"/>
    /// (A texels then B texels, <paramref name="uw"/> x <paramref name="uh"/> each); the thread slices it out.</summary>
    internal void Save(ulong key, int w, int h, byte[] data, int buf, int ux, int uy, int uw, int uh, int x, int y)
    {
        Interlocked.Increment(ref Outstanding); Interlocked.Increment(ref Busy[buf]);
        lock (_gate) { _in.Enqueue(new Job { Rect = -1, Key = key, W = w, H = h, Data = data, Buf = buf, Ux = ux, Uy = uy, Uw = uw, Uh = uh, X = x, Y = y, Write = true }); Monitor.Pulse(_gate); }
    }

    /// <summary>The rect's raw bits (A then B, w x h each) out of a union readback. Pure.</summary>
    internal static byte[] Slice(byte[] union, int ux, int uy, int uw, int uh, int x, int y, int w, int h)
    {
        var raw = new byte[w * h * 8];
        SliceInto(raw, union, ux, uy, uw, uh, x, y, w, h);
        return raw;
    }

    /// <summary><see cref="Slice"/> into a rented buffer (at least <c>w*h*8</c> long).</summary>
    internal static void SliceInto(byte[] raw, byte[] union, int ux, int uy, int uw, int uh, int x, int y, int w, int h)
    {
        int row = w * 4;
        for (int plane = 0; plane < 2; plane++)
            for (int j = 0; j < h; j++)
                Buffer.BlockCopy(union, plane * uw * uh * 4 + ((y - uy + j) * uw + (x - ux)) * 4, raw, plane * w * h * 4 + j * row, row);
    }

    internal bool TryTake(out int rect, out ulong key, out byte[]? data)
    {
        lock (_gate)
        {
            if (_out.Count == 0) { rect = -1; key = 0; data = null; return false; }
            Job j = _out.Dequeue();
            rect = j.Rect; key = j.Key; data = j.Data;
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

    /// <summary>Once, when the thread starts: a file from another format version is dead weight the prune
    /// counts against the budget and never deletes on its own (only a probe deletes a stale file, and no
    /// key from an older version is ever probed). 18851 files, 565 MiB, of which a third were live (run 5).</summary>
    private void SweepStale()
    {
        try
        {
            string dir = MeshChart.LedgerDir();
            if (!Directory.Exists(dir)) return;
            int n = 0; long bytes = 0;
            var head = new byte[8];
            foreach (string f in Directory.EnumerateFiles(dir, "*.lmr"))
            {
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                    if (fs.Read(head, 0, 8) == 8 && BitConverter.ToUInt32(head, 0) == Magic && BitConverter.ToInt32(head, 4) == Version) continue;
                bytes += new FileInfo(f).Length;
                System.IO.File.Delete(f); n++;
            }
            if (n > 0) _log?.Info($"LightmapBake: swept {n} bake files from older formats ({bytes >> 20} MiB)");
        }
        catch (Exception e) { _log?.Info($"LightmapBake: the stale-file sweep faulted -- {e.Message}"); }
    }

    private void Work()
    {
        SweepStale();
        while (true)
        {
            Job j;
            lock (_gate)
            {
                while (_in.Count == 0) Monitor.Wait(_gate);
                j = _in.Dequeue();
            }
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                if (j.Write) { try { WriteFile(j); } finally { Interlocked.Decrement(ref Outstanding); Interlocked.Decrement(ref Busy[j.Buf]); } continue; }
                j.Data = ReadFile(j.Key, j.W, j.H);
            }
            catch (Exception e) { j.Data = null; _log?.Info($"LightmapBake: the bake cache faulted -- {e.Message}"); }
            finally { Interlocked.Add(ref Allocated, GC.GetAllocatedBytesForCurrentThread() - a0); }
            lock (_gate) _out.Enqueue(j);
        }
    }

    /// <summary>Pure: header + deflated bits.</summary>
    internal static byte[] Serialize(ulong key, int w, int h, byte[] raw)
    {
        var ms = new MemoryStream(HeaderBytes + raw.Length / 2);
        SerializeTo(ms, key, w, h, raw, raw.Length);
        return ms.ToArray();
    }

    /// <summary><see cref="Serialize"/> straight onto a stream: the first <paramref name="n"/> bytes of <paramref name="raw"/>.</summary>
    private static void SerializeTo(Stream s, ulong key, int w, int h, byte[] raw, int n)
    {
        var bw = new BinaryWriter(s);
        bw.Write(Magic); bw.Write(Version); bw.Write(key); bw.Write(w); bw.Write(h);
        bw.Flush();
        using var z = new DeflateStream(s, CompressionLevel.Fastest, leaveOpen: true);
        z.Write(raw, 0, n);
    }

    /// <summary>The inverse; null names the refusal (version, key, truncation) -- never throws.</summary>
    internal static byte[]? Deserialize(byte[] blob, ulong key, int w, int h, out string? why)
    {
        why = null;
        try
        {
            if (blob.Length < HeaderBytes) { why = "truncation"; return null; }
            var r = new BinaryReader(new MemoryStream(blob, writable: false));
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) { why = "version"; return null; }
            if (r.ReadUInt64() != key || r.ReadInt32() != w || r.ReadInt32() != h) { why = "key"; return null; }
            int n = w * h * 8;
            var raw = new byte[n];
            using var z = new DeflateStream(new MemoryStream(blob, HeaderBytes, blob.Length - HeaderBytes, writable: false), CompressionMode.Decompress);
            int got = 0;
            while (got < n) { int k = z.Read(raw, got, n - got); if (k <= 0) break; got += k; }
            if (got != n) { why = "truncation"; return null; }
            return raw;
        }
        catch (Exception) { why = "truncation"; return null; }
    }

    private byte[]? ReadFile(ulong key, int w, int h)
    {
        string f = File(key);
        if (!System.IO.File.Exists(f)) return null;
        byte[]? raw = Deserialize(System.IO.File.ReadAllBytes(f), key, w, h, out string? why);
        if (raw != null) { System.IO.File.SetLastWriteTimeUtc(f, DateTime.UtcNow); return raw; }
        Interlocked.Increment(ref Stale);
        System.IO.File.Delete(f);
        return null;
    }

    private void WriteFile(Job j)
    {
        string dir = MeshChart.LedgerDir();
        Directory.CreateDirectory(dir);
        // rented + streamed: a fresh slice, blob and ToArray per file was 3 GB of garbage a period (run 8)
        int n = j.W * j.H * 8;
        byte[] raw = System.Buffers.ArrayPool<byte>.Shared.Rent(n);
        try
        {
            SliceInto(raw, j.Data!, j.Ux, j.Uy, j.Uw, j.Uh, j.X, j.Y, j.W, j.H);
            using var fs = new FileStream(File(j.Key), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            SerializeTo(fs, j.Key, j.W, j.H, raw, n);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(raw); }
        if (++_writes % PruneEvery == 0) MeshChart.Prune(dir, (long)Lightmap.Disk.V << 20);
    }

    /// <summary>Headless: the round trip and the three refusals.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"BakeCache.SelfCheck: FAILED -- {w}"); } }
        var raw = new byte[16 * 8 * 8];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i * 7);
        byte[] blob = Serialize(0x1234, 16, 8, raw);
        var union = new byte[32 * 16 * 8];
        for (int plane = 0; plane < 2; plane++)
            for (int j = 0; j < 8; j++) Buffer.BlockCopy(raw, plane * 16 * 8 * 4 + j * 64, union, plane * 32 * 16 * 4 + ((4 + j) * 32 + 8) * 4, 64);
        E(Slice(union, 0, 0, 32, 16, 8, 4, 16, 8).AsSpan().SequenceEqual(raw), "a rect slices out of its union readback intact");
        byte[]? back = Deserialize(blob, 0x1234, 16, 8, out string? why);
        E(back != null && why == null && back.AsSpan().SequenceEqual(raw), "the round trip is byte-identical");
        E(Deserialize(blob, 0x1235, 16, 8, out why) == null && why == "key", "another key is refused as key");
        E(Deserialize(blob, 0x1234, 8, 16, out why) == null && why == "key", "other dimensions are refused as key");
        E(Deserialize(blob.AsSpan(0, blob.Length / 2).ToArray(), 0x1234, 16, 8, out why) == null && why == "truncation", "a cut file is refused as truncation");
        blob[4] ^= 1;
        E(Deserialize(blob, 0x1234, 16, 8, out why) == null && why == "version", "another version is refused as version");
        log.Info(ok ? "BakeCache.SelfCheck: passed" : "BakeCache.SelfCheck: FAILED -- see above");
        return ok;
    }
}

internal static partial class LightmapBake
{
    private static readonly BakeCache _cache = new();
}
