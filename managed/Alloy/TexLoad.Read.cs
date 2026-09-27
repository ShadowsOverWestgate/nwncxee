using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace CX.Alloy;

/// <summary>CX-54 step 1, the pure half: a resource's <b>location</b> and the read of it. No engine memory, no
/// engine call, no GL -- everything here runs on the <c>cx-load</c> worker off a record the main thread filled
/// in for it, which is what makes the read safe to take off the main thread at all (<c>CExoResMan</c> is not
/// thread-safe: its <c>Malloc</c> evicts from a shared budget and every container shares one seekable
/// <c>CExoFile*</c>).</summary>
internal static class TexLoadRead
{
    /// <summary><c>RESID[0]</c>, the source kind the engine's own <c>Demand</c> and <c>ReadRaw</c> switch on.
    /// Written by the four <c>AddKey</c> literals in <c>CExoKeyTable::Add*Contents</c>.</summary>
    internal const byte KindBif = 1, KindErf = 2, KindLoose = 3, KindNwSync = 5;

    /// <summary>Largest resource this reader will pull in one go: past any shipped texture, and a wall against a
    /// corrupt size field turning into a multi-gigabyte allocation on a worker.</summary>
    internal const int MaxBytes = 256 << 20;

    /// <summary>Where a resource's bytes live, resolved from the ResMan tables without reading them. A VALUE
    /// record by construction: a <c>CKeyTableEntry*</c> is not a stable handle (<c>AllocateTable</c> reallocates
    /// the array on any rebuild), so nothing here may point into engine memory.</summary>
    internal readonly struct TexLocation
    {
        /// <summary><see cref="KindBif"/>, <see cref="KindErf"/>, <see cref="KindLoose"/>, or <see cref="KindNwSync"/>
        /// (then <see cref="Path"/> is the lowercase resref).</summary>
        public readonly byte Kind;
        /// <summary>The owning table's id (<c>RESID[2]</c>), re-checked before a finished read is published.</summary>
        public readonly uint TableId;
        /// <summary>Absolute filename, from <c>CExoAliasList::ResolveFileName</c> on the main thread.</summary>
        public readonly string Path;
        /// <summary>Byte offset of the stored resource; 0 for a loose file.</summary>
        public readonly long Offset;
        /// <summary>Bytes on disk, compressed if <see cref="Compressed"/>; -1 means "the whole file".</summary>
        public readonly int StoredSize;
        /// <summary>Bytes after decompression -- what <c>GetResourceSize</c> answers.</summary>
        public readonly int PlainSize;
        /// <summary>The entry is XRES/zstd-packed, which this reader refuses (see <see cref="Read"/>).</summary>
        public readonly bool Compressed;
        /// <summary>The restype actually found, off the key entry.</summary>
        public readonly ushort ResType;

        public TexLocation(byte kind, uint tableId, string path, long offset, int storedSize, int plainSize, bool compressed, ushort resType)
        {
            Kind = kind; TableId = tableId; Path = path; Offset = offset;
            StoredSize = storedSize; PlainSize = plainSize; Compressed = compressed; ResType = resType;
        }

        public bool IsNull => Path == null;
    }

    /// <summary>One worker's open files: one long-lived handle per BIF and per hak, which is what turns the
    /// engine's <c>fopen</c>/<c>fclose</c> pair per resource into a single positional read. A loose file is
    /// opened and closed per read, as the engine opens a fresh <c>CExoFile</c> for one. Worker-thread only.</summary>
    internal sealed class Handles : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle?> _open = new(StringComparer.Ordinal);
        /// <summary>The one read buffer, grown to the largest resource seen: a managed array per texture would put
        /// large-object allocations on the worker, and NativeAOT's GC stops the main thread for them.</summary>
        private byte[] _buf = Array.Empty<byte>();

        /// <summary>Cap on long-lived handles: a session sees a few dozen containers, and a table rebuild can
        /// walk the set. ponytail: past it ALL are closed and reopened on demand; an LRU if that ever churns.</summary>
        private const int MaxOpen = 128;

        private SafeFileHandle? Open(string path, bool keep)
        {
            if (keep && _open.TryGetValue(path, out SafeFileHandle? h)) return h;
            SafeFileHandle? fresh = null;
            try { fresh = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            if (!keep || fresh == null) return fresh;   // a failed open is retried next time, not cached
            if (_open.Count >= MaxOpen) Dispose();
            _open[path] = fresh;
            return fresh;
        }

        /// <summary>The resource's bytes (valid until the next read), or false with <paramref name="why"/> set.
        /// Every refusal is a reason a caller logs and counts: nothing here falls back quietly (CX-45).</summary>
        internal bool Read(in TexLocation loc, out ReadOnlySpan<byte> bytes, out string? why)
        {
            bytes = default;
            byte[]? got = ReadInto(loc, out int n, out why);
            if (got == null) return false;
            bytes = new ReadOnlySpan<byte>(got, 0, n);
            return true;
        }

        private byte[]? ReadInto(in TexLocation loc, out int want, out string? why)
        {
            why = null; want = 0;
            if (loc.IsNull) { why = "no location"; return null; }
            if (loc.Compressed)
            {
                // ponytail: XRES (ReadNWCompressedBuffer @0x533a90) refused; no shipped BIF/hak sets the flag.
                why = "XRES/zstd-packed";
                return null;
            }
            bool keep = loc.Kind != KindLoose;
            SafeFileHandle? h = Open(loc.Path, keep);
            if (h == null || h.IsInvalid) { why = $"cannot open {loc.Path}"; return null; }
            long len;
            try { len = RandomAccess.GetLength(h); }
            catch (IOException e) { why = e.Message; if (!keep) h.Dispose(); return null; }
            want = loc.StoredSize >= 0 ? loc.StoredSize : (int)Math.Min(len, MaxBytes + 1L);
            if (want <= 0 || want > MaxBytes) { why = $"stored size {want}"; if (!keep) h.Dispose(); return null; }
            if (loc.Offset < 0 || loc.Offset + want > len)
            { why = $"{loc.Path} is {len} bytes, entry wants {want} at {loc.Offset}"; if (!keep) h.Dispose(); return null; }
            if (_buf.Length < want) _buf = new byte[want];
            int got;
            try { got = RandomAccess.Read(h, _buf.AsSpan(0, want), loc.Offset); }
            catch (IOException e) { why = e.Message; if (!keep) h.Dispose(); return null; }
            if (!keep) h.Dispose();
            if (got != want) { why = $"read {got} of {want} bytes at {loc.Offset}"; return null; }
            return _buf;
        }

        public void Dispose()
        {
            foreach (var kv in _open) kv.Value?.Dispose();
            _open.Clear();
        }
    }
}
