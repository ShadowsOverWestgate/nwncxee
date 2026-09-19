using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>#643 follow-up: the BAKE LEDGER -- the reason a settled area costs nothing. Every probe capture
/// is read back into a CPU arena keyed by its packed cell, so a cell that re-enters the toroidal window is
/// re-uploaded from the ledger instead of re-rasterised. Once every window slot is filled from the ledger or
/// from a capture the capture budget is zero, and <c>gi.idle</c> (default 0) is the only thing that can raise
/// it again.
/// <para>What makes a bake stale is the LIGHT HASH: a quantized fingerprint of the area terms and the
/// point-light set -- NOT the sun, whose time-of-day walk would re-bake the grid every few minutes, and not
/// the light count, which every creature torch moves. It is re-evaluated every frame, allocation-free, and a
/// new value must hold for <see cref="HashHold"/> frames before it counts. A settled change does not wipe the
/// window: the ledger is dropped and the live probes are re-captured one every <see cref="RefreshEvery"/>
/// frames, so the cost of a change is bounded and the old bake stays on screen until its replacement lands.</para>
/// <para>The ledger outlives the session in one file per fingerprint under the client user directory beside
/// <c>cx.cfg</c> (<c>cx-gi/&lt;hash&gt;.bin</c>), written and read on a background thread and gated by the
/// <c>gi.cache</c> knob. The file is named by the fingerprint rather than by the area, which is what lets the
/// disk layer exist without a single new engine read: no area resref, no module name, no new offset row --
/// the lighting state IS the identity, and a header mismatch (version, grid, spacing) simply misses.</para>
/// <para>Windows: nothing here touches the engine. GL plus file IO, identical on both builds.</para></summary>
internal static unsafe partial class GiProbes
{
    /// <summary>Bumped whenever the tile layout, the atlas formats or the header change; an older file misses.</summary>
    private const int CacheVersion = 1;
    /// <summary>"CXGI" little-endian.</summary>
    private const uint CacheMagic = 0x49475843;   // not-an-offset: "CXGI", the file's first four bytes

    /// <summary>One probe's CPU copy: the RGBA16F irradiance tile then the RG16F depth tile, halfs as bytes,
    /// exactly as <c>glReadPixels</c> hands them back and <c>glTexSubImage2D</c> takes them.</summary>
    internal const int IrrBytes = IrrTile * IrrTile * 4 * 2, DepBytes = DepTile * DepTile * 2 * 2;
    internal const int ProbeBytes = IrrBytes + DepBytes;
    /// <summary>Cells the ledger holds before it stops taking new ones: 4096 x 2096 bytes = 8,6 MiB, one
    /// allocation for the session. A cell past the cap is captured as before and simply not remembered.
    /// ponytail: no eviction -- an area walked end to end past 4096 cells re-captures its far corner.</summary>
    internal const int MaxCells = 4096;
    /// <summary>Ledger restores per frame. Two <c>glTexSubImage2D</c> of 800 + 1296 bytes each, so the whole
    /// window refills in 64 frames without ever being the frame.</summary>
    private const int FillPerFrame = 16;
    /// <summary>Frames a new light fingerprint must hold before it counts as a change. Longer than any
    /// walking light's dwell at one quantized position, shorter than a noticeable stale bake.</summary>
    private const int HashHold = 600;
    /// <summary>Frames per re-capture while a fingerprint change is being absorbed: the whole window over
    /// ~70 s at 60 fps, never a visible cost.</summary>
    internal const int RefreshEvery = 4;
    /// <summary>Probes still owed by the current fingerprint change; 0 = the window matches the hash.</summary>
    private static int _refreshLeft;
    /// <summary>Files kept under <c>cx-gi/</c>; the oldest past this are deleted on every write.</summary>
    private const int MaxCacheFiles = 64;
    private static long[] _saveKeys = Array.Empty<long>();
    private static byte[] _saveCopy = Array.Empty<byte>();

    private static byte[]? _arena;
    private static Dictionary<long, int> _ledger = new(MaxCells);
    /// <summary>The fingerprint each arena cell was baked under: a cell restored under another one is a
    /// stand-in and queues its probe's refresh.</summary>
    private static readonly ulong[] _cellHash = new ulong[MaxCells];
    private static int _cells;
    /// <summary>The fingerprint this ledger was baked under; 0 = not established yet.</summary>
    private static ulong _lightHash;
    private static ulong _candHash;
    private static int _candFor;
    private static bool _needLoad = true;
    private static int _hits, _stores, _stales;
    private static Loaded? _pending;
    private static volatile bool _ioBusy;

    /// <summary>A disk ledger a background read produced, waiting for the frame path to adopt it.</summary>
    private sealed class Loaded
    {
        internal ulong Hash;
        internal byte[] Arena = Array.Empty<byte>();
        internal Dictionary<long, int> Index = new();
        internal int Cells;
    }

    internal static string CacheNote()
        => $"cache {_ledger.Count}/{MaxCells} cells, {_hits} restored, {_stores} baked, {_stales} stale bakes"
         + (Gi.Cache.V ? "" : " (disk off)");

    // ---- the light fingerprint ----

    private const ulong FnvOffset = 14695981039346656037, FnvPrime = 1099511628211;

    private static ulong Mix(ulong h, long v)
    {
        for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= FnvPrime; }
        return h;
    }

    private static long Q(float v, float step) => (long)MathF.Round(v / step);
    private static long Q(Vector3 v, float step)
        => (long)Mix(Mix(Mix(FnvOffset, Q(v.X, step)), Q(v.Y, step)), Q(v.Z, step));

    /// <summary>This frame's lighting fingerprint: the two area terms and the walk's settled static-set
    /// fingerprint (<see cref="LightmapBake.SetHash"/>: an order-independent xor of quantized position,
    /// range and chroma over the uncut list, held 120 frames). 0 until that set settles.</summary>
    private static ulong LightHash()
    {
        if (!FrameInputs.HasAreaAmbient && !FrameInputs.HasSun) return 0;
        ulong h = FnvOffset;
        h = Mix(h, Q(FrameInputs.AreaAmbient, 0.02f));
        h = Mix(h, Q(FrameInputs.AreaDiffuse, 0.02f));
        h = Mix(h, Q(FrameInputs.AreaDiffuseDir, 0.05f));
        // #674: the static set's fingerprint is over the UNCUT, unfaded walk, settled by LightmapBake; the
        // uploaded list is the nearest-N faded, camera-culled cut, and hashing it re-baked every probe each
        // time the player walked. 0 = not settled yet: wait. ponytail: a dynamic placeable light toggled by
        // script leaves the GI stale until the next static edge
        ulong acc = LightmapBake.SetHash;
        if (acc == 0) return 0;
        return Mix(h, (long)acc);
    }

    /// <summary>Once a frame, before the window shifts: settle the fingerprint, drop a ledger the world no
    /// longer matches, and adopt whatever the loader thread finished. Allocation-free in the steady state.</summary>
    internal static void Tick()
    {
        Adopt();
        ulong h = LightHash();
        if (h == 0) return;
        if (h != _candHash) { _candHash = h; _candFor = 0; return; }
        if (++_candFor != HashHold || h == _lightHash) return;
        if (_lightHash != 0)
        {
            // the ledger STAYS: its cells are stand-ins under the new fingerprint (each restore of one queues
            // a refresh) -- dropping it re-baked the whole grid at gi.perframe every time the static set
            // grew by one light on a walk, and GI never finished warming up (#674 live)
            _stales++;
            Save(_lightHash);
            _refreshLeft = Count;
        }
        _lightHash = h;
        Load(h);
    }

    private static void DropLedger()
    {
        _ledger.Clear(); _cells = 0;
        Interlocked.Exchange(ref _pending, null);
    }

    /// <summary>Forget the fingerprint too: the next <see cref="Tick"/> re-establishes it and re-asks the
    /// disk. Called from <see cref="Invalidate"/>, which every edge that voids the window goes through.</summary>
    private static void ResetCache()
    {
        DropLedger();
        _lightHash = 0; _candHash = 0; _candFor = 0; _needLoad = true; _refreshLeft = 0;
    }

    /// <summary>One probe every <see cref="RefreshEvery"/> frames while a fingerprint change is owed, else 0.</summary>
    private static int RefreshBudget()
    {
        if (_refreshLeft <= 0) return 0;
        if (++_refreshTick < RefreshEvery) return 0;
        _refreshTick = 0; _refreshLeft--;
        return 1;
    }
    private static int _refreshTick;

    private static void Adopt()
    {
        Loaded? p = Interlocked.Exchange(ref _pending, null);
        if (p == null) return;
        if (p.Hash != _lightHash) return;   // the world moved on while the read ran
        int merged = 0;
        foreach (var kv in p.Index)
        {
            if (_ledger.TryGetValue(kv.Key, out int have) && _cellHash[have] == p.Hash) continue;
            int at = Reserve(kv.Key);
            if (at < 0) break;
            Buffer.BlockCopy(p.Arena, kv.Value * ProbeBytes, _arena!, at * ProbeBytes, ProbeBytes);
            _cellHash[at] = p.Hash;
            merged++;
        }
        _log?.Info($"GiProbes: {merged} of {p.Cells} probes restored from the disk bake cache ({p.Hash:x16})");
    }

    // ---- the arena ----

    /// <summary>The arena cell that holds <paramref name="key"/>, allocating one if the ledger has room;
    /// -1 when it is full. The arena itself is one lazy allocation, never on a settled frame.</summary>
    private static int Reserve(long key)
    {
        if (_ledger.TryGetValue(key, out int at)) return at;
        if (_cells >= MaxCells) return -1;
        _arena ??= new byte[MaxCells * ProbeBytes];
        at = _cells++;
        _ledger[key] = at;
        return at;
    }

    /// <summary>Read the tile just convolved back into its arena cell. Row pitches are 80 and 72 bytes, both
    /// multiples of 4, so the default PACK_ALIGNMENT holds and no <c>glPixelStorei</c> is needed.
    /// <para>ponytail: a synchronous readback, so each capture costs one pipeline stall. Captures only happen
    /// while the grid is filling; the upgrade is a PBO ring if a fill is ever felt.</para></summary>
    private static void ReadBack(int cell, int x, int y, int tile, uint format, int offset)
    {
        if (cell < 0 || _arena == null || Gl.ReadPixels == null) return;
        fixed (byte* p = _arena) Gl.ReadPixels(x, y, tile, tile, format, Gl.HALF_FLOAT, p + (long)cell * ProbeBytes + offset);
    }

    /// <summary>Fill keyless window slots from the ledger: the whole point of the follow-up -- a cell that has
    /// been baked once never rasterises again. Runs inside the colour bracket, after
    /// <see cref="ClearDropped"/>, and counts as a capture for <see cref="Live"/> and <see cref="Full"/>.</summary>
    internal static void FillFromLedger()
    {
        if (_irr == 0 || _arena == null || _ledger.Count == 0 || !_haveCell0 || Gl.TexSubImage2D == null) return;
        int filled = 0;
        fixed (byte* a = _arena)
            for (int slot = 0; slot < Count && filled < FillPerFrame; slot++)
            {
                if (_key[slot] != NoKey) continue;
                CellOfSlot(slot, out int cx, out int cy, out int cz);
                long key = Key(cx, cy, cz);
                if (!_ledger.TryGetValue(key, out int cell)) continue;
                byte* src = a + (long)cell * ProbeBytes;
                int sx = slot % Nx, row = slot / Nx;
                Gl.ActiveTexture(Gl.TEXTURE0);
                Gl.BindTexture(Gl.TEXTURE_2D, _irr);
                Gl.TexSubImage2D(Gl.TEXTURE_2D, 0, sx * IrrTile, row * IrrTile, IrrTile, IrrTile, Gl.RGBA, Gl.HALF_FLOAT, src);
                Gl.BindTexture(Gl.TEXTURE_2D, _dep);
                Gl.TexSubImage2D(Gl.TEXTURE_2D, 0, sx * DepTile, row * DepTile, DepTile, DepTile, Gl.RG, Gl.HALF_FLOAT, src + IrrBytes);
                Gl.BindTexture(Gl.TEXTURE_2D, 0);
                _key[slot] = key; _holds[slot] = true; _keyless--;
                Captures++; _hits++; filled++;
                if (_cellHash[cell] != _lightHash && _refreshLeft < Count) _refreshLeft++;
            }
    }

    // ---- the disk half ----

    private static string CacheDir() => Path.Combine(Native.UserDirectory() ?? Native.DefaultUserDirectory(), "cx-gi");
    private static string CacheFile(ulong hash) => Path.Combine(CacheDir(), $"{hash:x16}.bin");

    /// <summary>Serialize a ledger snapshot. Pure, so <see cref="SelfCheck"/> can prove the round trip with
    /// no disk: header (magic, version, fingerprint, grid, spacing, cell count) then key + tiles per cell.</summary>
    internal static byte[] Serialize(ulong hash, long[] keys, int cells, byte[] arena, float sxy, float sz)
    {
        var ms = new MemoryStream(cells * ProbeBytes + 64);
        var w = new BinaryWriter(ms);
        w.Write(CacheMagic); w.Write(CacheVersion); w.Write(hash);
        w.Write(Nx); w.Write(Ny); w.Write(Nz); w.Write(IrrRes); w.Write(DepRes);
        w.Write(sxy); w.Write(sz);
        w.Write(cells);
        for (int i = 0; i < cells; i++)
        {
            w.Write(keys[i]);
            w.Write(arena, i * ProbeBytes, ProbeBytes);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The inverse, refusing anything whose header does not describe THIS grid at THIS fingerprint.
    /// Returns false rather than throwing on any malformed file.</summary>
    internal static bool Deserialize(byte[] blob, ulong hash, float sxy, float sz, out Dictionary<long, int> index,
                                     out byte[] arena, out int cells)
    {
        index = new Dictionary<long, int>(); arena = Array.Empty<byte>(); cells = 0;
        try
        {
            var r = new BinaryReader(new MemoryStream(blob, writable: false));
            if (r.ReadUInt32() != CacheMagic || r.ReadInt32() != CacheVersion || r.ReadUInt64() != hash) return false;
            if (r.ReadInt32() != Nx || r.ReadInt32() != Ny || r.ReadInt32() != Nz
                || r.ReadInt32() != IrrRes || r.ReadInt32() != DepRes) return false;
            if (r.ReadSingle() != sxy || r.ReadSingle() != sz) return false;
            int n = r.ReadInt32();
            if (n < 0 || n > MaxCells) return false;
            var a = new byte[n * ProbeBytes];
            for (int i = 0; i < n; i++)
            {
                long key = r.ReadInt64();
                if (r.Read(a, i * ProbeBytes, ProbeBytes) != ProbeBytes) return false;
                index[key] = i;
            }
            arena = a; cells = n;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Write the ledger out under <paramref name="hash"/>, on a background thread over a snapshot --
    /// nothing of this touches the frame but the copy, which happens at a world edge.</summary>
    private static void Save(ulong hash)
    {
        if (!Gi.Cache.V || hash == 0 || _cells == 0 || _arena == null || _ioBusy) return;
        int cells = _cells;
        if (_saveKeys.Length < cells) _saveKeys = new long[MaxCells];
        if (_saveCopy.Length < cells * ProbeBytes) _saveCopy = new byte[MaxCells * ProbeBytes];
        long[] keys = _saveKeys; byte[] copy = _saveCopy;
        foreach (var kv in _ledger) if (kv.Value < cells) keys[kv.Value] = kv.Key;
        Buffer.BlockCopy(_arena, 0, copy, 0, cells * ProbeBytes);
        float sxy = Gi.Spacing.V, sz = Gi.SpacingZ.V;
        ExtenderLog? log = _log;
        _ioBusy = true;
        var t = new Thread(() =>
        {
            try
            {
                byte[] blob = Serialize(hash, keys, cells, copy, sxy, sz);
                Directory.CreateDirectory(CacheDir());
                File.WriteAllBytes(CacheFile(hash), blob);
                Prune();
                log?.Info($"GiProbes: {cells} probes written to the bake cache ({hash:x16})");
            }
            catch (Exception e) { log?.Info($"GiProbes: the bake cache could not be written -- {e.Message}"); }
            finally { _ioBusy = false; }
        })
        { IsBackground = true, Name = "cx-gi-save", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    /// <summary>Keep <c>cx-gi/</c> at <see cref="MaxCacheFiles"/>: oldest first. Runs on the save thread.</summary>
    private static void Prune()
    {
        var files = new DirectoryInfo(CacheDir()).GetFiles("*.bin");
        if (files.Length <= MaxCacheFiles) return;
        Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        for (int i = 0; i < files.Length - MaxCacheFiles; i++) files[i].Delete();
    }

    /// <summary>Ask the disk for this fingerprint's ledger; the frame path adopts it at the next
    /// <see cref="Tick"/>. A miss is silent -- the grid simply bakes as it did before.</summary>
    private static void Load(ulong hash)
    {
        if (!Gi.Cache.V || hash == 0) return;
        float sxy = Gi.Spacing.V, sz = Gi.SpacingZ.V;
        ExtenderLog? log = _log;
        var t = new Thread(() =>
        {
            try
            {
                string f = CacheFile(hash);
                if (!File.Exists(f)) return;
                if (!Deserialize(File.ReadAllBytes(f), hash, sxy, sz, out var index, out byte[] arena, out int cells))
                { log?.Info($"GiProbes: the bake cache for {hash:x16} does not describe this grid -- baking fresh"); return; }
                Interlocked.Exchange(ref _pending, new Loaded { Hash = hash, Arena = arena, Index = index, Cells = cells });
            }
            catch (Exception e) { log?.Info($"GiProbes: the bake cache could not be read -- {e.Message}"); }
        })
        { IsBackground = true, Name = "cx-gi-load", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    /// <summary>The world edge: the ledger describes an area that has gone, so write it out before it goes.
    /// Registered as the pass's <c>Revoke</c>; <see cref="Invalidate"/> stays the plain void.</summary>
    internal static void Revoke()
    {
        Save(_lightHash);
        Invalidate();
    }

    /// <summary>Off-device: the ledger's serialize / deserialize round trip, and the three refusals that keep
    /// a stale file from being believed. Pure -- no GL, no disk, so it runs headless.</summary>
    internal static void LedgerSelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void e(bool c, string w) { if (!c) { ok = false; log.Info($"GiProbes.LedgerSelfCheck FAIL: {w}"); } }
        const int cells = 3;
        var keys = new long[cells] { Key(1, 2, 3), Key(-4, 5, -6), Key(7, 8, 0) };
        var arena = new byte[cells * ProbeBytes];
        for (int i = 0; i < arena.Length; i++) arena[i] = (byte)(i * 31 + 7);
        byte[] blob = Serialize(0xABCDEF0123456789UL, keys, cells, arena, 5f, 3f);

        bool round = Deserialize(blob, 0xABCDEF0123456789UL, 5f, 3f, out var index, out byte[] back, out int n);
        e(round && n == cells, "a ledger round-trips its cell count");
        if (round)
        {
            bool same = index.Count == cells;
            for (int i = 0; i < cells && same; i++) same = index.TryGetValue(keys[i], out int at) && at == i;
            e(same, "every cell key maps back onto its own arena slot");
            bool bytes = true;
            for (int i = 0; i < cells * ProbeBytes && bytes; i++) bytes = back[i] == arena[i];
            e(bytes, "every tile byte survives the round trip");
        }
        e(!Deserialize(blob, 0x1111111111111111UL, 5f, 3f, out _, out _, out _), "a file baked under another light hash is refused");
        e(!Deserialize(blob, 0xABCDEF0123456789UL, 4f, 3f, out _, out _, out _), "a file baked at another probe spacing is refused");
        blob[4]++;
        e(!Deserialize(blob, 0xABCDEF0123456789UL, 5f, 3f, out _, out _, out _), "a file of another format version is refused");
        log.Info(ok ? "GiProbes.LedgerSelfCheck: passed" : "GiProbes.LedgerSelfCheck: FAILED");
    }
}
