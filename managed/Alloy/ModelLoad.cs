using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Engine;
using CX.Framework;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy;

/// <summary>CX-55 step 1: Alloy reads a model's <c>.mdl</c> file on a worker thread and hands the bytes back
/// when the engine asks for them. The engine keeps the PARSE -- it allocates a node tree, demands textures and
/// touches GL, none of which may leave the main thread -- so the only thing taken here is the read.
/// <para><b>The seam is <c>CAuroraInterface::GetResource</c> @0x1123fd0.</b> Every model load funnels through
/// <c>FindModel</c> @0x44c2a0, which misses the name cache, calls <c>IODispatcher::ReadSync</c> @0x4bf670, and
/// that calls <c>AurResGet(name, ".mdl")</c> @0x4332c0 -- whose <c>AurGetResource</c> slot @0x18b9aa0 IS this
/// function. It answers a plain pointer plus a size, and <c>AurResGet</c> copies both into its
/// <c>OpenResource</c> record: the read cursor is <c>res+0x58</c> and the line buffer <c>res+0x50</c>, so the
/// binary parse (@0x4bee60, <c>AurResGetDataBytes</c> @0x4330a0) and the ASCII <c>dofile</c> @0x434930
/// (<c>AurResGetNextLine</c> @0x432fa0) both only READ the buffer, and neither outlives
/// <c>AurResFree</c> @0x4335d0 -- which always comes back as
/// <c>CAuroraInterface::ReleaseResource(base)</c> @0x1123b90. That is the whole contract a buffer of ours has
/// to meet.</para>
/// <para><b>The 10-byte header.</b> <c>CAuroraModel::LoadModel</c> @0x1134df0 writes restype <c>0x7d2</c> at
/// <c>ptr-10</c> and the owning <c>CAuroraModel*</c> at <c>ptr-8</c>, and <c>ReleaseResource</c> switches on
/// that ushort. Ours is left ZERO, so if the release chain ever misses one of our buffers the engine's own
/// release falls straight through instead of calling virtuals on a pointer that is not an object.</para>
/// <para><b>Speculative, unlike textures.</b> There is no suspend window: the engine wants the <c>Model*</c>
/// synchronously, so an entry that is not ready yet is a miss (the engine reads it) and a late one is swept.
/// The offers are read out of the area message itself, ahead of the engine's own reads of it: the tile grid
/// in <c>ModelLoad.Tiles.cs</c> and the static placeables in <c>ModelLoad.Objects.cs</c>.</para></summary>
internal static unsafe partial class ModelLoad
{
    /// <summary>Off; serving; serving with every hand-back checked against the engine's own read.</summary>
    internal const int ModeOff = 0, ModeOn = 1, ModeCheck = 2;

    /// <summary>The model restype, as <c>CResHelper&lt;CResMDL, 2002&gt;</c> declares it.</summary>
    private const ushort MdlResType = 2002;

    /// <summary>Bytes of headroom in front of a served buffer. The engine writes ten of them
    /// (<c>ptr-10</c>, <c>ptr-8</c>); sixteen keeps the data itself 16-byte aligned.</summary>
    private const int PrefixBytes = 16;

    /// <summary>The <c>{0, modelDataSize, rawDataSize}</c> wrapper a binary MDL starts with: the model block
    /// begins after it, and a first dword of zero is what <c>ReadSync</c> discriminates binary from ASCII on.</summary>
    private const int WrapperBytes = 12;
    /// <summary>The supermodel name inside the model block: <c>char[64]</c> at <c>modelData+0xa8</c>, which the
    /// parse copies to <c>Model+0xd0</c> and hands straight to <c>FindModel</c> (@0x4bee60's
    /// <c>FindModel(this+0xd0)</c>). <c>"null"</c> or empty means the model has none.</summary>
    private const int SuperModelAt = 0xa8, SuperModelChars = 64;   // not-an-offset: a field of the MDL FILE FORMAT, not of an engine object

    /// <summary>A resref is at most 16 characters.</summary>
    private const int ResRefChars = 16;

    /// <summary>Prepared models held at once, reads waiting, and bytes held. The offers are one area's own
    /// models, not a whole tileset's: ~263 distinct tile models plus supermodels plus the static placeables
    /// measured on the heaviest SoW area, at a few hundred KiB each. 2048 entries is an order of magnitude of
    /// headroom over that, and 64 MiB is the byte bound it implies (the 256 MiB that preceded it was sized for
    /// the whole-tileset superset, which offered 2348 models and read 408 MiB to serve 107).</summary>
    private const int MaxEntries = 2048, MaxPending = 2048;
    /// <summary><c>cx-mdl</c> threads. A read is I/O LATENCY, not CPU: 558 model rows off the live 6.5 GiB
    /// Arelith NWSync shard measured 548 ms cold on one thread and 349 ms on two (385 ms of the one-thread time
    /// was the shard's blob fetch, 62 ms zstd), so the second reader is what lets the read-ahead stay ahead of
    /// <c>LoadArea</c>'s demand order. ponytail: two, not one per core -- the third reader on the box is
    /// <c>cx-load</c>, and the main thread is parsing what these hand back.</summary>
    private const int MdlWorkers = 2;
    private const long MaxHeldBytes = 64L << 20;
    /// <summary>Drips a prepared buffer may wait to be asked for before it is swept. Anything still here long
    /// after the load that offered it was never going to be asked for.</summary>
    private const int MaxAgeDrips = 900;
    /// <summary>Drips between age sweeps: walking the whole table every frame would be the very main-thread
    /// cost this feature exists to remove.</summary>
    private const int SweepEvery = 120;
    private const int LogCap = 8;

    private static ExtenderLog? _log;
    private static int _mode;
    private static bool _armed;
    private static AuroraGetResourceFn* _getSlot;
    private static AuroraReleaseResourceFn* _relSlot;
    private static TileSetGetFn* _tileSetSlot;
    private static readonly SlotJobTable<LoadJob, LoadEntry> _jobs = new(MaxEntries, MaxPending);
    /// <summary>One set of open containers and one read buffer PER <c>cx-mdl</c> thread: <see cref="Handles"/>
    /// is worker-thread-only state, and the pool has more than one worker.</summary>
    [ThreadStatic] private static Handles? _handles;
    /// <summary>Buffers the engine is holding right now, to their entry key: what tells a release of ours from
    /// a release of the engine's without reading memory in front of a pointer we did not allocate.</summary>
    private static readonly Dictionary<IntPtr, long> _out = new();
    private static long _held, _drip;

    /// <summary>Main-thread ticks spent in <see cref="Offer"/>, and ticks the worker has been busy: what the
    /// <c>[load]</c> line samples across <c>LoadArea</c>.</summary>
    internal static long OfferTicks, BusyTicks;
    private static long _offered, _ready, _served, _misses, _late, _cached, _absent, _refused, _resident,
                        _nwsync, _nwsyncOff, _full, _supers, _swept, _bytes, _workTicks, _checked, _mismatched,
                        _tilesPeeked;

    /// <summary>What a worker carries: the location in, the prepared buffer out. Nothing in it points into
    /// engine memory, so nothing it holds can be freed under the worker.</summary>
    private sealed class LoadJob
    {
        public long Key;
        public ulong Sig;
        public TexLocation Loc;
        /// <summary>The file's bytes, <see cref="PrefixBytes"/> into an allocation of our own.</summary>
        public byte* Data;
        public int Size;
        /// <summary>The supermodel this file names, or empty: offered from the main thread at the publish.
        /// Allocated once with the job, which the pool recycles, so the worker allocates nothing for it.</summary>
        public readonly byte[] Super = new byte[ResRefChars + 1];
        /// <summary>The resref this job reads, copied at the offer: what <see cref="MdlCache"/> needs on the
        /// worker to name a compiled model. Fixed with the job, so the pool recycles it too.</summary>
        public readonly byte[] Name = new byte[ResRefChars + 1];
        public string? Why;
        public long Ticks;
        public uint R0, R1, R2, R3;
    }

    /// <summary>A claim while the read is in flight, then the prepared buffer. The <c>RESID</c> is kept so the
    /// hand-back can re-prove identity against a fresh lookup, which is what makes a hash collision a miss
    /// rather than the wrong model.</summary>
    private struct LoadEntry : ISlotEntry
    {
        public ulong Sig { get; set; }
        public IntPtr Data;
        public int Size;
        /// <summary>The engine is holding <see cref="Data"/>: it may not be served again or swept until its
        /// <c>ReleaseResource</c> comes back.</summary>
        public bool Out;
        public uint R0, R1, R2, R3;
        public long Drip;
    }

    // ---- lifecycle ----

    /// <summary>Read the knob, chain the two halves of the seam, start nothing: the worker starts with the
    /// first offer.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _mode = Renderer.ModelLoad.Boot;
        if (_mode == ModeOff) { log.Info("ModelLoad: off (alloy.modelload=0) -- the engine reads every model file on the main thread"); return; }
        if (!ResLocate.Attach(out string? why))
        {
            log.Always($"ModelLoad: REFUSED -- {why}; the engine reads every model file on the main thread");
            return;
        }
        if (EngineFn.BinaryFindModel == null || EngineFn.TileSetGetTileData == null)
        {
            log.Always("ModelLoad: REFUSED -- BinaryFindModel or CNWTileSet::GetTileData unresolved; the engine reads every model file on the main thread");
            return;
        }
        _getSlot = hooks.ChainAuroraGetResource(&OnGetResource, ServePriority);
        _relSlot = hooks.ChainAuroraReleaseResource(&OnReleaseResource, ServePriority);
        _tileSetSlot = hooks.ChainTileSetGet(&OnGetTileSet, ServePriority);
        if (_getSlot == null || _relSlot == null || _tileSetSlot == null)
        {
            log.Always($"ModelLoad: REFUSED -- CAuroraInterface::GetResource {(_getSlot != null ? "ok" : "MISSING")}, "
                       + $"ReleaseResource {(_relSlot != null ? "ok" : "MISSING")}, "
                       + $"CNWTileSetManager::GetTileSet {(_tileSetSlot != null ? "ok" : "MISSING")}; the engine reads every model file on the main thread");
            return;
        }
        _armed = true;
        log.Info($"ModelLoad: armed -- an area's own models are read on the cx-mdl worker during LoadArea and handed back at GetResource"
                 + (_mode == ModeCheck ? ", and every hand-back is checked against the engine's own read (alloy.modelload=1 drops the check)" : ""));
    }

    /// <summary>Outside anything else that may want the same two statics; nothing else chains them today.</summary>
    private const int ServePriority = 0;

    internal static string Note()
        => !_armed ? "" : $"; modelload: {_served} served of {_ready} ready ({_offered} offered in {OfferTicks / (Stopwatch.Frequency / 1000)} ms,"
                          + $" {_supers} supermodels, {_tilesPeeked} tiles peeked, {_bytes >> 20} MiB read ({_nwsync} from NWSync),"
                          + $" {_workTicks / (Stopwatch.Frequency / 1000)} ms on {MdlWorkers} cx-mdl, {_misses} missed, refused: {_cached} cached, {_absent} no key entry,"
                          + $" {_resident} resident, {_nwsyncOff} NWSync unreadable, {_refused} unreadable, {_full} past the cap, {_late} still reading, {_swept} swept"
                          + (_mode == ModeCheck ? $", {_checked} checked, {_mismatched} MISMATCHED" : "") + ")";

    // ---- the offers, main thread ----

    /// <summary>A model name worth reading ahead: a null-terminated resref, no extension. Cold -- once per
    /// distinct name. Skips what the engine already has parsed (<c>BinaryFindModel</c>, the name cache
    /// <c>FindModel</c> tests first) and what is already held or queued here.</summary>
    internal static void Offer(byte* name)
    {
        long t0 = Stopwatch.GetTimestamp();
        OfferOne(name);
        OfferTicks += Stopwatch.GetTimestamp() - t0;
    }

    private static void OfferOne(byte* name)
    {
        if (!_armed || name == null || *name == 0) return;
        long key = (long)ResLocate.Key(name);
        if (_jobs.Entries.ContainsKey(key)) return;
        _offered++;
        if (EngineFn.BinaryFindModel(name) != IntPtr.Zero) { _cached++; return; }
        if (!_jobs.QueueHasRoom || _held >= MaxHeldBytes) { _full++; return; }
        if (!ResLocate.Find(name, MdlResType, out IntPtr table, out IntPtr entry)) { _absent++; return; }
        uint* resId = stackalloc uint[ResLocate.ResIdWords];
        if (!ResLocate.Locate(table, entry, resId, out TexLocation loc, out string? why, out ResLocate.Source src))
        {
            if (src == ResLocate.Source.Resident) _resident++;
            else if (src == ResLocate.Source.NwSync) _nwsyncOff++;
            if (why != null && ++_refused <= LogCap) _log?.Always($"ModelLoad: {Marshal.PtrToStringAnsi((IntPtr)name)}: {why} -- the engine reads it");
            return;
        }
        if (src == ResLocate.Source.NwSync) _nwsync++;
        LoadJob j = _jobs.Rent();
        j.Key = key; j.Sig = loc.TableId; j.Loc = loc; j.Data = null; j.Size = 0; j.Why = null; j.Ticks = 0;
        j.Super[0] = 0;
        int nm = 0;
        for (; nm < ResRefChars && name[nm] != 0; nm++) j.Name[nm] = name[nm];
        j.Name[nm] = 0;
        j.R0 = resId[0]; j.R1 = resId[1]; j.R2 = resId[2]; j.R3 = resId[3];
        _jobs.Start("cx-mdl", Worker, MdlWorkers);
        _jobs.Offer(j, key, loc.TableId);
        _jobs.Entries[key] = new LoadEntry { Sig = loc.TableId };
    }

    /// <summary>Cached: a method group would mint a delegate at every offer, and the worker starts once.</summary>
    private static readonly Action<LoadJob> Worker = Work;

    /// <summary>The area load's entry, before the engine's own <c>LoadArea</c>. There is nothing to walk yet:
    /// <c>CNWCArea::LoadArea</c> @0x70e010 reads the area's width, height, tileset resref and EVERY tile id out
    /// of the area message itself, inside its own loop -- <c>area+0x298</c> (the <c>CNWCTile</c> array) is
    /// allocated in there and <c>area+0x290</c> (the tileset) is set in there, so at this instant the area
    /// object still describes the LAST area. Both read-aheads therefore start later, from the loop's own first
    /// call; all this does is hand them the area and clear the last one's state.</summary>
    internal static void OnLoadArea(IntPtr area)
    {
        if (!_armed) return;
        Publish();
        ResetAreaKey();
        ResetTilesForArea(area);
        OfferObjects(area);
    }

    /// <summary>Placeables, doors and creatures, from whatever the area load names early (CX-55 step 2);
    /// unimplemented here on purpose, and a partial method with no body is simply not called.</summary>
    static partial void OfferObjects(IntPtr area);

    /// <summary>The area load's one ask for its tileset, which <c>LoadArea</c> makes with the message already
    /// decoded far enough to name it and with no tile loaded yet. Nothing is offered here -- the tile records
    /// are not at the cursor, a variable grass block sits between -- only the tileset the tile read-ahead is
    /// about to index. Cold -- once per area.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnGetTileSet(IntPtr mgr, IntPtr resref)
    {
        IntPtr tileset = (*_tileSetSlot)(mgr, resref);
        if (_armed) { _tileSet = tileset; AreaKeyTileset(resref); }
        return tileset;
    }

    // ---- the worker ----

    /// <summary>One read, on <c>cx-mdl</c>. Touches no engine memory and no GL: the location record was copied
    /// for it at the offer, and the buffer it allocates is its own until the main thread files it.</summary>
    private static void Work(LoadJob j)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            if (j.Loc.Kind == KindNwSync) NwSync(j);
            else if ((_handles ??= new Handles()).Read(j.Loc, out ReadOnlySpan<byte> raw, out j.Why)) Keep(j, raw);
        }
        catch (Exception e) { j.Why = e.Message; }
        j.Ticks = Stopwatch.GetTimestamp() - t0;
        Interlocked.Add(ref BusyTicks, j.Ticks);
    }

    /// <summary>The worker's NWSync read: <see cref="NwSyncReader"/>'s decompressed blob, copied and freed.</summary>
    private static void NwSync(LoadJob j)
    {
        Span<byte> name = stackalloc byte[j.Loc.Path.Length];
        System.Text.Encoding.ASCII.GetBytes(j.Loc.Path, name);
        if (!NwSyncReader.TryRead(name, MdlResType, out byte* data, out int size))
        {
            j.Why = "NWSync: no row (counted on the nwsync note)";
            return;
        }
        try { Keep(j, new ReadOnlySpan<byte>(data, size)); }
        finally { NativeMemory.Free(data); }
    }

    /// <summary>Copy the read into a buffer laid out as the engine's own: <see cref="PrefixBytes"/> of zeroed
    /// headroom, then the file. Records the supermodel a binary file names, for the main thread to offer.
    /// <para>CX-55 step 3: an ASCII source with a compiled model in <see cref="MdlCache"/> is swapped for the
    /// compiled bytes HERE, on the worker, so the hit costs the main thread nothing -- and the swapped bytes
    /// then name their supermodel below. Not in check mode: the comparison is against the engine's own read of
    /// the ASCII, which compiled bytes would fail by design.</para></summary>
    private static void Keep(LoadJob j, ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0) { j.Why = "no bytes"; return; }
        if (_mode != ModeCheck) raw = MdlCache.Worker(j.Name, raw);
        byte* block = (byte*)NativeMemory.AllocZeroed((nuint)(PrefixBytes + raw.Length));
        byte* data = block + PrefixBytes;
        raw.CopyTo(new Span<byte>(data, raw.Length));
        j.Data = data;
        j.Size = raw.Length;
        SuperModelOf(raw, j);
    }

    /// <summary>The supermodel a BINARY model names, into <paramref name="j"/>'s fixed buffer. An ASCII model
    /// names its own through <c>setsupermodel</c> at parse time and is not read here.</summary>
    private static void SuperModelOf(ReadOnlySpan<byte> raw, LoadJob j)
    {
        int at = WrapperBytes + SuperModelAt;
        if (raw.Length < at + SuperModelChars) return;
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw) != 0) return;   // ASCII
        ReadOnlySpan<byte> s = raw.Slice(at, SuperModelChars);
        int n = s.IndexOf((byte)0);
        if (n < 0) n = SuperModelChars;
        if (n == 0 || n > ResRefChars) return;
        if (n == 4 && (s[0] | 0x20) == 'n' && (s[1] | 0x20) == 'u' && (s[2] | 0x20) == 'l' && (s[3] | 0x20) == 'l') return;
        for (int i = 0; i < n; i++) j.Super[i] = s[i];
        j.Super[n] = 0;
    }

    // ---- the publish, main thread ----

    /// <summary>Once a frame on the texture drip's anchor: file what the worker finished, offer the
    /// supermodels it found, and sweep what nothing ever asked for.</summary>
    internal static void Drip()
    {
        if (!_armed) return;
        _drip++;
        Publish();
        Sweep();
    }

    /// <summary>File everything the worker finished. Also run from <see cref="Take"/>, because the engine's
    /// first ask can land before the frame that would drip.</summary>
    private static void Publish()
    {
        while (_jobs.TryTake(out LoadJob j))
        {
            if (j.Data == null)
            {
                _jobs.Entries.Remove(j.Key);   // the engine reads it, and a later offer may try again
                if (++_refused <= LogCap) _log?.Always($"ModelLoad: {j.Loc.Path}+{j.Loc.Offset}: {j.Why ?? "no bytes"} -- the engine reads it");
            }
            else
            {
                var e = new LoadEntry { Sig = j.Sig, Data = (IntPtr)j.Data, Size = j.Size, Drip = _drip,
                                        R0 = j.R0, R1 = j.R1, R2 = j.R2, R3 = j.R3 };
                if (_jobs.File(j.Key, j.Sig, e)) { _held += j.Size; _bytes += j.Size; _ready++; }
                else Free(e);
                if (j.Super[0] != 0) { _supers++; fixed (byte* s = j.Super) OfferOne(s); }
            }
            _workTicks += j.Ticks;
            j.Data = null; j.Size = 0; j.Loc = default; j.Why = null; j.Super[0] = 0;
            _jobs.Recycle(j);
        }
    }

    /// <summary>Drop prepared buffers nothing asked for. Bounded work: only when something is held, and never
    /// a buffer the engine is holding right now.</summary>
    private static void Sweep()
    {
        bool overBudget = _held >= MaxHeldBytes;
        if (_held == 0 || _jobs.Entries.Count == 0 || (!overBudget && _drip % SweepEvery != 0)) return;
        List<long>? dead = null;
        foreach (var kv in _jobs.Entries)
        {
            if (kv.Value.Data == IntPtr.Zero || kv.Value.Out) continue;
            if (!overBudget && _drip - kv.Value.Drip < MaxAgeDrips) continue;
            (dead ??= new List<long>()).Add(kv.Key);
        }
        if (dead == null) return;
        for (int i = 0; i < dead.Count; i++)
        {
            if (!_jobs.Entries.TryGetValue(dead[i], out LoadEntry e)) continue;
            Free(e);
            _held -= e.Size;
            _swept++;
            _jobs.Entries.Remove(dead[i]);
        }
    }

    private static void Free(in LoadEntry e)
    {
        if (e.Data != IntPtr.Zero) NativeMemory.Free((byte*)e.Data - PrefixBytes);
    }

    // ---- the serve, main thread, inside the engine's AurResGet ----

    /// <summary>Whether <paramref name="ext"/> names a model, by the engine's own test: <c>GetResource</c>
    /// copies it past a leading dot, uppercases it and compares four bytes against <c>"MDL"</c>.</summary>
    private static bool IsMdl(byte* ext)
    {
        if (ext == null) return false;
        byte* p = *ext == (byte)'.' ? ext + 1 : ext;
        return (p[0] | 0x20) == 'm' && (p[1] | 0x20) == 'd' && (p[2] | 0x20) == 'l' && p[3] == 0;
    }

    [UnmanagedCallersOnly]
    private static byte* OnGetResource(byte* name, byte* ext, int* size)
    {
        if (!_armed || size == null || name == null || *name == 0 || !IsMdl(ext)) return (*_getSlot)(name, ext, size);
        byte* ours = Take(name, out int n);
        if (ours == null) return (*_getSlot)(name, ext, size);
        if (_mode == ModeCheck && !Check(name, ext, ours, n))
        {
            Drop(name, (IntPtr)ours);
            return (*_getSlot)(name, ext, size);
        }
        *size = n;
        _served++;
        return ours;
    }

    [UnmanagedCallersOnly]
    private static void OnReleaseResource(IntPtr data)
    {
        if (_armed && data != IntPtr.Zero && _out.TryGetValue(data, out long key)) { Unserve(key, data); return; }
        (*_relSlot)(data);
    }

    /// <summary>The prepared bytes for <paramref name="name"/>, or null. The entry stays in the table, marked
    /// out, until <c>ReleaseResource</c> brings the buffer back -- the ASCII path reads the same model twice
    /// (<c>ReadSync</c> frees it and <c>dofile</c> re-opens it), so a served buffer is re-servable rather than
    /// spent.
    /// <para>Identity is re-proved, not assumed: the same lookup the engine is about to make must land on the
    /// same key entry with the same <c>RESID</c>.</para></summary>
    private static byte* Take(byte* name, out int size)
    {
        size = 0;
        long key = (long)ResLocate.Key(name);
        if (!_jobs.Entries.TryGetValue(key, out LoadEntry e)) { _misses++; return null; }
        if (e.Out) { _misses++; return null; }
        if (e.Data == IntPtr.Zero)
        {
            Publish();
            if (!_jobs.Entries.TryGetValue(key, out e) || e.Data == IntPtr.Zero) { _misses++; _late++; return null; }
        }
        if (!ResLocate.StillAt(name, MdlResType, e.R0, e.R1, e.R2, e.R3))
        {
            _jobs.Entries.Remove(key);
            _held -= e.Size;
            Free(e);
            _misses++;
            return null;
        }
        e.Out = true;
        _jobs.Entries[key] = e;
        _out[e.Data] = key;
        size = e.Size;
        return (byte*)e.Data;
    }

    /// <summary>The engine has finished with a buffer of ours: it goes back to the table, re-servable and
    /// sweepable, with its age reset.</summary>
    private static void Unserve(long key, IntPtr data)
    {
        _out.Remove(data);
        if (!_jobs.Entries.TryGetValue(key, out LoadEntry e) || e.Data != data)
        {
            NativeMemory.Free((byte*)data - PrefixBytes);   // the entry went while the engine held it
            return;
        }
        e.Out = false;
        e.Drip = _drip;
        _jobs.Entries[key] = e;
    }

    /// <summary>Our entry was wrong: it goes for good, and the caller lets the engine read the model as it
    /// always did.</summary>
    private static void Drop(byte* name, IntPtr data)
    {
        _out.Remove(data);
        long key = (long)ResLocate.Key(name);
        if (_jobs.Entries.TryGetValue(key, out LoadEntry e) && e.Data == data)
        {
            _held -= e.Size;
            _jobs.Entries.Remove(key);
        }
        NativeMemory.Free((byte*)data - PrefixBytes);
    }

    /// <summary>Check mode: the engine's own read of the same model, compared byte for byte with ours and then
    /// released either way. A mismatch means the location resolution picked a different resource than the
    /// engine's own lookup would; it is said loudly (CX-45) and the caller serves the engine's read instead.
    /// True when the two agree.</summary>
    private static bool Check(byte* name, byte* ext, byte* ours, int n)
    {
        _checked++;
        int theirs = 0;
        IntPtr got = (IntPtr)(*_getSlot)(name, ext, &theirs);
        string? bad =
            got == IntPtr.Zero ? "the engine read nothing"
            : theirs != n ? $"engine {theirs} bytes vs ours {n}"
            : !new ReadOnlySpan<byte>((void*)got, n).SequenceEqual(new ReadOnlySpan<byte>(ours, n)) ? $"the {n} bytes differ"
            : null;
        if (got != IntPtr.Zero) (*_relSlot)(got);
        if (bad == null) return true;
        if (++_mismatched <= LogCap)
            _log?.Always($"ModelLoad: CHECK FAILED for {Marshal.PtrToStringAnsi((IntPtr)name)}: {bad} -- the engine's own read is served instead; set alloy.modelload=0 if this repeats");
        return false;
    }
}
