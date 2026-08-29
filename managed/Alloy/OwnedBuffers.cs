using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #260 (map #68, ADR 0018 M5 strand 1 step 5): geometry RESIDENCY. Alloy mints the GL buffer behind every
/// engine vertex/index pool and uploads it from the pool's host shadow; the engine's own upload is
/// suppressed. One buffer, one upload, no copy.
/// <para><b>What this replaces.</b> #212/#213 kept an Alloy-side COPY of every engine pool buffer, keyed on
/// the engine's GL name, made with <c>glCopyBufferSubData</c> on first sight and refreshed once per frame
/// from a coalesced span. So world geometry lived in three places -- the engine's permanent host shadow, the
/// engine's VBO, Alloy's VBO -- and every dirty pool was pushed to GL twice. Worse, the copy's correctness
/// depended on ordering against an engine commit the copier did not control, which is census E10's reading
/// of #227. Both the third copy and that ordering are gone: the hook IS the upload.</para>
/// <para><b>Keyed on the POOL TOKEN.</b> The token is the engine's own identity for the resource --
/// <c>GLRender::RequestPool</c> @0x4d1ec0 hands one out, <c>DeletePool</c> @0x4d1d80 returns it to a free
/// list, and every pool call takes one. The GL name is a FIELD of the pool, minted here, so it is an index
/// onto the table rather than the key: <see cref="Owns"/> and <see cref="DirtySince"/> are asked in terms of
/// a name because that is all a harvested VAO carries. Both edges that change what a name means -- this
/// upload funnel and the engine's delete funnel -- rewrite the index.</para>
/// <para><b>Why the takeover is safe even when Alloy never draws.</b> The bytes uploaded are the bytes the
/// engine would have uploaded, into the buffer the engine's own pool descriptor names. A mid-session stand
/// down (no 4.6 context, a vanilla verdict, a refused frame) therefore degrades to vanilla, not to an empty
/// world -- which is exactly what a copy-based scheme could not promise. It is also why the ticket's
/// world-vs-UI pool question is an instrument here (<see cref="NoteEngineDraw"/>) and not a gate: a pool the
/// UI draws from holds correct bytes whether Alloy or the engine put them there.</para>
/// <para><b>Every pool, not only the world's.</b> ADR 0018 step 5 scopes the suppression to the world's
/// pools, because a copy-based scheme could only be trusted that far. The takeover widens it: the bytes are
/// the engine's own bytes in the engine's own buffer, so there is no world-only carve-out left to make and a
/// per-pool gate would be machinery guarding nothing. What the ADR asked for is a subset of what this
/// does.</para>
/// <para>Residency supersedes ADR 0016's "upload on enroll, free on revoke" for geometry (ADR 0018 step 5):
/// upload on the engine's commit, freed by the engine's own <c>DeletePool</c>, <b>no budget and no LRU</b>,
/// with a VRAM high-water mark reported beside the engine's own host-shadow total. A budget before a
/// measurement is #149's mistake in a new costume.</para>
/// </summary>
internal static unsafe class OwnedBuffers
{
    /// <summary>One pool Alloy has uploaded. <see cref="Name"/> is the GL buffer minted for it (or adopted,
    /// when the engine got there before Alloy's entry points existed).</summary>
    private struct Entry
    {
        internal uint Name;
        internal long Bytes;
        /// <summary>The engine wrote into this pool since the last <see cref="ClearDirty"/> -- the re-bake
        /// predicate's vertex-motion signal.</summary>
        internal bool Dirty;
        /// <summary>Seen in an engine draw outside the Alloy window, i.e. the UI / a GUI 3D panel draws from
        /// it. The ticket's tagging, recorded rather than acted on.</summary>
        internal bool EngineDrawn;
        /// <summary>An Alloy VAO has been built over it.</summary>
        internal bool AlloyDrawn;
    }

    /// <summary>Capacity for the two tables and the dirty list. Pools are engine-wide (world VDOs, UI
    /// primitives, the global element stream), so unlike #212's <c>MaxVdos * 3</c> there is no construction
    /// that bounds them -- <c>RequestPool</c> @0x4d1ec0 doubles its descriptor table on demand.
    /// <para>ponytail: a number, not a derivation. Past it a rehash allocates on the render thread, once,
    /// and the tables keep working. Upgrade path if an area is ever seen past it: the descriptor count is
    /// readable at <c>_table + PoolCountOffset</c>, so <see cref="Install"/> could size from the live figure
    /// and <see cref="HighWater"/> could report crossing it.</para></summary>
    private const int PoolCeiling = 1024;
    private static readonly Dictionary<uint, Entry> _byToken = new(PoolCeiling);
    /// <summary>GL name -> pool token. The index the harvested-VAO callers ask through.</summary>
    private static readonly Dictionary<int, uint> _byName = new(PoolCeiling);
    /// <summary>Tokens written to since the last <see cref="ClearDirty"/>.</summary>
    private static readonly List<uint> _dirty = new(PoolCeiling);

    private static ExtenderLog? _log;
    private static bool _armed;
    /// <summary>Is the geometry takeover live? False leaves every upload to the engine, which is vanilla
    /// residency -- the stand-down, not a second code path.</summary>
    internal static bool Armed => _armed;
    /// <summary>Buffers Alloy DRAWS from and did not upload -- asked at every VAO build, so this is the
    /// geometry twin of <c>OwnedTextures.Borrowed</c> and what #218's exit bar reads. Distinct from
    /// <see cref="_refused"/>, which counts pools handed back to the engine whether or not Alloy ever draws
    /// them: the menu's pools are resident before Alloy's GL entry points exist and are never world
    /// geometry, so counting those on the bar would fail it every session for nothing.</summary>
    internal static int Borrowed => _borrowed;
    private static long _bytes, _peakBytes;
    /// <summary>The high-water figure the <c>/knobs</c> residency line reports, in MiB (#259).</summary>
    internal static long PeakMib => _peakBytes / OneMiB;
    private static int _minted, _adopted, _refused, _borrowed, _retired, _peakCount;
    private static long _uploads, _subUploads, _grows, _repacks, _wholePool;

    /// <summary>Wired once from <see cref="AlloyRenderer"/>'s init, before any frame runs.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _armed = Settings.Bool("ownedgeom", true);
        log.Info(_armed
            ? "OwnedBuffers: armed -- Alloy mints and uploads every engine vertex/index pool from its host "
              + "shadow, and the engine's own upload is suppressed (ownedgeom=0 to leave uploads to the engine)"
            : "OwnedBuffers: stood down (ownedgeom=0) -- the engine uploads its own pools, Alloy draws from them");
    }

    /// <summary>The two engine edges: the upload funnel, which Alloy now PERFORMS rather than observes, and
    /// the delete funnel, where a pool's GL name stops meaning what it meant. Without the second, an area
    /// transition frees the pools, the tokens go back on the free list and the next area's pools inherit both
    /// the tokens and (from <c>glGenBuffers</c>) the names -- stale rows describing the wrong mesh.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!_armed) return;
        IntPtr anchor = Native.Sym(SymbolMap.GlPoolTableAnchor);
        _table = anchor == IntPtr.Zero ? null : (byte*)anchor + PoolTableOffset;
        _orphan = (byte*)Native.Sym(SymbolMap.GlOrphanBuffers);
        _totalMemPool = (int*)Native.Sym(SymbolMap.GlTotalMemPool);
        _origUnlock = (delegate* unmanaged<uint, uint, uint, void>)hooks.Install(
            SymbolMap.GlUnlockPool, (IntPtr)(delegate* unmanaged<uint, uint, uint, void>)&OnUnlockPool);
        _origDelete = (delegate* unmanaged<uint, void>)hooks.Install(
            SymbolMap.GlDeleteBuffer, (IntPtr)(delegate* unmanaged<uint, void>)&OnDeleteBuffer);
        bool ok = _table != null && _origUnlock != null && _origDelete != null;
        if (!ok) _armed = false;
        log.Info(ok
            ? "OwnedBuffers: took GLRender::UnlockPool + GLRender::DeleteBuffer -- Alloy performs the pool "
              + "upload, the engine's is skipped, and a pool's row dies with its buffer"
            : "OwnedBuffers: stood down -- the pool table or the upload/delete funnels did not resolve, and a "
              + "takeover that cannot read the pool descriptor would upload nothing");
    }

    private static delegate* unmanaged<uint, uint, uint, void> _origUnlock;
    private static delegate* unmanaged<uint, void> _origDelete;
    /// <summary>Address of the pool table's base POINTER (not the table). Deliberately not the table itself:
    /// <c>RequestPool</c> @0x4d1ec0 grows the table with <c>calloc</c>+<c>free</c>, so the base moves.</summary>
    private static byte* _table;
    private static byte* _orphan;
    private static int* _totalMemPool;

    /// <summary>The engine is pushing a pool's host shadow into GL. Alloy does it instead, and the original
    /// never runs for a pool it took -- that is the suppression, and it is what makes the copy unnecessary.
    /// <para>Hot-adjacent: dynamic pools (UI primitives, particle streams) unlock every frame. In the steady
    /// state nothing here allocates and nothing logs -- both dictionaries are pre-sized (see
    /// <see cref="PoolCeiling"/>, whose ceiling is the one exception) and the high-water mark is recorded
    /// here but REPORTED from the frame, where a string interpolation is affordable.</para>
    /// <para>Runs wherever the engine happened to be, which is its own 3.3 context. That is fine and in fact
    /// preferable: buffers are share-listed (#70), so the name is visible from Alloy's bracket, and the
    /// upload never touches ARRAY_BUFFER or ELEMENT_ARRAY_BUFFER -- see <see cref="Upload"/>.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnUnlockPool(uint token, uint size, uint offset)
    {
        if (!Take(token, size, offset) && _origUnlock != null) _origUnlock(token, size, offset);
    }

    /// <summary>The handler's body, split out so <see cref="SelfCheck"/> can exercise it -- an
    /// <c>UnmanagedCallersOnly</c> method cannot be called from managed code. False means "not ours", and the
    /// caller then runs the engine's own <c>UnlockPool</c> unchanged.</summary>
    private static bool Take(uint token, uint size, uint offset)
    {
        if (!_armed || size == 0) return false;
        byte* p = Pool(token);
        if (p == null) return false;
        // The engine's own two refusals, replicated: a pool with no bytes, and a pool whose lock flags say
        // the host block is not in a state to be pushed (GLRender::UnlockPool @0x4cfc80 tests +0x11 & 0x18
        // before it does anything). Handing those back costs nothing -- the original refuses them too.
        int bytes = *(int*)(p + PoolSize);
        if (bytes <= 0 || (p[PoolLockFlags] & LockNoUpload) != 0) return false;
        byte* host = *(byte**)p;
        if (host == null) return false;
        if (Gl.GenBuffers == null || Gl.BufferData == null || Gl.BufferSubData == null || Gl.BindBuffer == null)
        { _refused++; return false; }

        uint name = *(uint*)(p + PoolName);
        bool fresh = p[PoolNamed] == 0;
        if (fresh)
        {
            Gl.GenBuffers(1, &name);
            if (name == 0) { _refused++; return false; }
            *(uint*)(p + PoolName) = name;
            p[PoolNamed] = 1;
            p[PoolUploaded] = 0;
        }
        else if (name == 0) { _refused++; return false; }

        Upload(p, name, host, bytes, size, offset);
        Record(token, name, bytes, fresh);
        return true;
    }

    /// <summary>The engine's upload, byte for byte, aimed at the pool's buffer. Three branches, chosen by the
    /// <c>RequestPool</c> usage word at pool+0x14, exactly as <c>GLRender::UnlockPool</c> @0x4cfc80 chooses
    /// them: bit 8 = allocate once then sub-data the named range; bit 1 alone = allocate once from the host
    /// base, thereafter sub-data the whole prefix from offset 0 (the offset argument is IGNORED on that
    /// branch, which is the engine's behaviour and not a simplification); neither bit = a static pool,
    /// uploaded exactly once and silent forever after.
    /// <para>Everything binds to COPY_WRITE_BUFFER rather than the pool's own target, which is the one place
    /// the takeover is deliberately NOT byte-identical: the engine's <c>UnlockPool</c> ends with
    /// <c>RenderInterface::BindBuffer(target, name)</c> and leaves the pool bound. Two things make dropping
    /// that residue safe, and both were checked rather than reasoned about. <c>GLRender::BindBuffer</c>
    /// @0x4d04a0 caches ARRAY_BUFFER against <c>m_nArrayBufferBound</c>, so skipping the bind leaves the
    /// shadow and the real state BOTH unmoved -- a desync needs them to disagree. And no consumer relies on
    /// the residue: <c>GLRender::SetVertexDataObject</c> @0x4cff30 and
    /// <c>SetVertexStreamDataObject</c> both re-read the pool's name out of the descriptor at +0x0c and bind
    /// it explicitly, which the xref of the descriptor table confirms is how every draw path reaches it.
    /// Dropping it also stops an ELEMENT_ARRAY_BUFFER bind -- VAO state -- from landing in whatever VAO the
    /// engine happened to have current.</para></summary>
    private static void Upload(byte* p, uint name, byte* host, int bytes, uint size, uint offset)
    {
        uint usage = *(uint*)(p + PoolUsage);
        bool dynamic = (usage & UsageDynamic) != 0;
        uint hint = dynamic ? Gl.DYNAMIC_DRAW : Gl.STATIC_DRAW;
        long span = size == WholePool ? bytes : size;
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, name);
        if ((usage & UsageSubData) != 0)
        {
            if (p[PoolUploaded] == 0)
            { Gl.BufferData(Gl.COPY_WRITE_BUFFER, (IntPtr)bytes, null, hint); p[PoolUploaded] = 1; }
            // The engine reads its own [offset, offset+size) with no bound check, and would over-read the
            // host block if a caller ever paired a whole-pool size with a non-zero offset. Nothing does --
            // CommitElementStreamData @0x45c9c0 unlocks with `xor %edx,%edx` -- but the clamp costs one
            // compare and the alternative is an out-of-bounds read of the engine's heap.
            if (offset > (uint)bytes) span = 0; else if (offset + span > bytes) span = bytes - offset;
            if (span > 0) Gl.BufferSubData(Gl.COPY_WRITE_BUFFER, (IntPtr)offset, (IntPtr)span, host + offset);
            _subUploads++;
        }
        else if (dynamic)
        {
            if (p[PoolUploaded] == 0)
            { Gl.BufferData(Gl.COPY_WRITE_BUFFER, (IntPtr)bytes, host, hint); p[PoolUploaded] = 1; }
            else
            {
                if (_orphan != null && *_orphan != 0)
                    Gl.BufferData(Gl.COPY_WRITE_BUFFER, (IntPtr)bytes, null, hint);
                Gl.BufferSubData(Gl.COPY_WRITE_BUFFER, IntPtr.Zero, (IntPtr)span, host);
                _subUploads++;
            }
        }
        else if (p[PoolUploaded] == 0)
        { Gl.BufferData(Gl.COPY_WRITE_BUFFER, (IntPtr)bytes, host, hint); p[PoolUploaded] = 1; }
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, 0);
        _uploads++;
        if (size == WholePool) _wholePool++;
    }

    /// <summary>Book the pool: its name (which may have changed under it -- <c>ResizePool</c> keeps the name,
    /// but a pool token recycled through the free list does not), its size, and the dirty mark.</summary>
    private static void Record(uint token, uint name, int bytes, bool fresh)
    {
        if (!_byToken.TryGetValue(token, out Entry e))
        {
            if (fresh) _minted++; else _adopted++;
            _peakCount = Math.Max(_peakCount, _byToken.Count + 1);
        }
        if (e.Name != name)
        {
            if (e.Name != 0) _byName.Remove((int)e.Name);
            e.Name = name;
            _byName[(int)name] = token;
        }
        if (e.Bytes != bytes)
        {
            // #260 owes the grow rate: a pool whose size moved under a live row was reallocated by
            // ResizePool @0x4cfbc0 (AddItem @0x45ca70 doubles capacity), which is the event a VRAM arena
            // would turn into a sub-allocation. Repacks are counted separately -- see NoteRepack, because a
            // Repack that compacts to the same byte count is invisible here. Session totals, never reset;
            // sizing the arena argument is a separate ticket's job.
            if (e.Bytes != 0) _grows++;
            _bytes += bytes - e.Bytes;
            e.Bytes = bytes;
        }
        if (!e.Dirty) { e.Dirty = true; _dirty.Add(token); }
        _byToken[token] = e;
        if (_bytes > _peakBytes) { _peakBytes = _bytes; _peakCount = _byToken.Count; _peakFresh = true; }
    }

    /// <summary>The pool descriptor for a token, or null when the table cannot answer. The base is re-read
    /// every call on purpose: <c>RequestPool</c> reallocates the whole table when it runs out of slots.
    /// <para>The bound check is the engine's own (<c>UnlockPool</c> tests the token against the descriptor
    /// count before it indexes), which is what makes this safe to run on an arbitrary token.</para></summary>
    private static byte* Pool(uint token)
    {
        if (_table == null || token == NoPool) return null;
        byte* table = *(byte**)_table;
        uint count = *(uint*)(_table + PoolCountOffset);
        if (table == null || token >= count) return null;
        return table + (nuint)token * PoolStride;
    }

    /// <summary>The engine is deleting this GL buffer name -- <c>DeletePool</c> @0x4d1d80 routes here for
    /// every named pool it releases, which is the pool's death and ours. The name and the token both go back
    /// into circulation, so the row must go with them.
    /// <para>Nothing is deleted by Alloy: the buffer is the pool's, the engine frees it here, and a second
    /// <c>glDeleteBuffers</c> on a recycled name would take out the pool that inherited it.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnDeleteBuffer(uint name)
    {
        Retire(name);
        if (_origDelete != null) _origDelete(name);
    }

    private static void Retire(uint name)
    {
        if (!_byName.TryGetValue((int)name, out uint token)) return;
        _byName.Remove((int)name);
        if (_byToken.TryGetValue(token, out Entry e)) { _bytes -= e.Bytes; _byToken.Remove(token); }
        _retired++;
    }

    /// <summary>Does Alloy own the pool behind this engine buffer name? The VAO builders ask once per slot
    /// build, not per frame, and stamp the answer -- a name whose pool the engine uploaded (everything before
    /// the first present) still draws correctly, it is just not ours.</summary>
    internal static bool Owns(uint engineBuf)
    {
        if (!_armed || engineBuf == 0) return false;
        if (!_byName.TryGetValue((int)engineBuf, out uint token)) { _borrowed++; return false; }
        if (_byToken.TryGetValue(token, out Entry e) && !e.AlloyDrawn)
        { e.AlloyDrawn = true; _byToken[token] = e; }
        return true;
    }

    /// <summary>#241: has the engine written into this buffer since the last <see cref="ClearDirty"/>? The
    /// re-bake predicate's VERTEX-motion signal -- dangly and morph-animated nodes both commit through
    /// <c>CommitItemDynamicData</c>, so both land in the dirty set with no per-class test.</summary>
    internal static bool DirtySince(uint engineBuf)
        => engineBuf != 0 && _byName.TryGetValue((int)engineBuf, out uint token)
           && _byToken.TryGetValue(token, out Entry e) && e.Dirty;

    /// <summary>Close the dirty window. Called from inside the 4.6 bracket where <c>Refresh</c> used to
    /// re-copy; there is nothing to copy any more, because the upload already happened at the engine's own
    /// commit. What survives is the SIGNAL, and the readers of <see cref="DirtySince"/> run before this.
    /// <para>That the upload moved to the commit also closes #213's one-frame window: a write landing after
    /// the bracket used to be one frame stale on screen. It no longer can be.</para></summary>
    internal static void ClearDirty()
    {
        if (_dirty.Count == 0) return;
        int n = _dirty.Count;
        for (int i = 0; i < n; i++)
            if (_byToken.TryGetValue(_dirty[i], out Entry e)) { e.Dirty = false; _byToken[_dirty[i]] = e; }
        _dirty.Clear();
    }

    /// <summary>The ticket's gate, shipped as an instrument. An engine draw reaching a funnel while the Alloy
    /// window is CLOSED is the UI, the menu or a GUI 3D panel drawing for itself; the VDO it names owns up to
    /// three pool tokens (<c>VertexDataObject::Build</c> @0x45bac0 requests them into +0x18 element, +0x24
    /// static vertex, +0x30 dynamic vertex, and no fourth). Tagging them answers "which pools does the UI
    /// draw from" against the pools Alloy binds, which is what #254 left unverified.
    /// <para>Recorded, not acted on -- see the class remarks on why the takeover does not need the answer.
    /// Costs three dictionary probes per engine-phase draw and allocates nothing.</para></summary>
    internal static void NoteEngineDraw(IntPtr vdo)
    {
        if (!_armed || vdo == IntPtr.Zero) return;
        byte* v = (byte*)vdo;
        Tag(*(uint*)(v + VdoElemToken));
        Tag(*(uint*)(v + VdoStaticToken));
        Tag(*(uint*)(v + VdoDynamicToken));
    }

    /// <summary>The other half of the rate #260 owes. <c>VertexDataObject::Repack</c> @0x45c270 fires when
    /// AddItem finds the item list fragmented past <c>2*used + 0x4000</c>; it compacts in the host shadow and
    /// re-uploads, so it need not change the pool's byte count and would otherwise not be counted at all.
    /// Fed from the repack hook <c>DrawFunnelService</c> already owns rather than a second one.</summary>
    internal static void NoteRepack() => _repacks++;

    private static void Tag(uint token)
    {
        if (token == NoPool) return;
        if (_byToken.TryGetValue(token, out Entry e) && !e.EngineDrawn)
        { e.EngineDrawn = true; _byToken[token] = e; }
    }

    private const long OneMiB = 1024 * 1024;
    /// <summary>The engine's "this VDO has no such pool" token (<c>VertexDataObject::AddItem</c> tests it at
    /// +0x24 and +0x30), and equally <c>UnlockPool</c>'s first refusal.</summary>
    private const uint NoPool = 0xFFFFFFFF;
    /// <summary>The size <c>GLRender::UnlockPool</c> reads as "the whole pool": both of its
    /// <c>glBufferSubData</c> sites substitute the pool's own byte count for it (@0x4cfda1/0x4cfda3 and
    /// @0x4cfe7d/0x4cfe7f).</summary>
    private const uint WholePool = 0xFFFFFFFF;
    /// <summary>Byte offset from <see cref="SymbolMap.GlPoolTableAnchor"/> to the table base pointer, and
    /// from there to the descriptor count -- the two globals <c>GLRender::UnlockPool</c> indexes through.</summary>
    private const int PoolTableOffset = 0x236c, PoolCountOffset = 8;
    /// <summary>The 0x20-byte pool descriptor (<c>GLRender::RequestPool</c> @0x4d1ec0 callocs 0x20 apiece):
    /// host shadow, byte size, GL name, target index, lock flags, usage word, "uploaded once", "named".</summary>
    private const int PoolStride = 0x20, PoolSize = 0x08, PoolName = 0x0c,
                      PoolLockFlags = 0x11, PoolUsage = 0x14, PoolUploaded = 0x18, PoolNamed = 0x19;
    /// <summary>Lock-flag bits that make <c>UnlockPool</c> do nothing at all.</summary>
    private const byte LockNoUpload = 0x18;
    /// <summary>Usage bits from <c>RequestPool</c>'s third argument: 0x100 = the sub-data path (the three
    /// per-VDO pools are 0x100/0x100/0x102), 0x2 = dynamic.</summary>
    private const uint UsageSubData = 0x100, UsageDynamic = 0x2;
    /// <summary>Pool tokens on a <c>VertexDataObject</c>: element, static vertex, dynamic vertex.</summary>
    private const int VdoElemToken = 0x18, VdoStaticToken = 0x24, VdoDynamicToken = 0x30;

    /// <summary>ADR 0016's measurement, and the only VRAM number this ticket ships. Called once a frame from
    /// inside the bracket, NOT from the upload funnel: the mark is RECORDED there (two compares) and the line
    /// -- which interpolates, and would otherwise do so a few hundred times per area load on the render
    /// thread -- is emitted here, and only once the mark has moved by a MiB.</summary>
    internal static void HighWater()
    {
        if (!_peakFresh) return;
        _peakFresh = false;
        if (_peakBytes < _lastReported + OneMiB) return;
        _lastReported = _peakBytes;
        _log?.Info($"OwnedBuffers: [vram] high-water {_peakBytes / OneMiB}MiB across {_peakCount} owned pools " +
                   $"(minted {_minted}, adopted {_adopted}, handed back {_refused}); engine host shadow " +
                   $"{EngineMib}MiB");
    }
    private static bool _peakFresh;
    private static long _lastReported;

    /// <summary>What the engine still holds in HOST memory for the same geometry -- <c>totalmempool</c>
    /// @0x19ad058, its own running total, which #254 listed as unmeasured. The second of the two copies;
    /// there is no third any more.</summary>
    private static long EngineMib => _totalMemPool == null ? 0 : (long)*_totalMemPool / OneMiB;

    /// <summary>The WORLD edge. Nothing to do: a pool's life is the ENGINE's, and the area transition that
    /// revokes Alloy's scene is the same moment <c>DeletePool</c> retires each pool through the delete funnel
    /// this table already watches. Kept as the place that says so, and as the session's tagging report --
    /// an area transition is the natural point to have seen both a world and a UI draw.</summary>
    internal static void Revoke()
    {
        if (_byToken.Count == 0) return;
        int ui = 0, alloy = 0, both = 0;
        foreach (Entry e in _byToken.Values)
        {
            if (e.EngineDrawn) ui++;
            if (e.AlloyDrawn) alloy++;
            if (e.EngineDrawn && e.AlloyDrawn) both++;
        }
        _log?.Info($"OwnedBuffers: [uitag] {_byToken.Count} pools resident -- {alloy} bound by Alloy, {ui} " +
                   $"drawn by the engine outside the Alloy window (UI / GUI panels), {both} by both; " +
                   $"{_bytes / OneMiB}MiB in VRAM against {EngineMib}MiB of engine host shadow");
    }

    /// <summary>The GL-NAME edge: the objects went with the context. Rows are dropped, nothing is deleted --
    /// deleting into a dead context is what #209 calls the terminal edge. The engine's pool descriptors still
    /// hold the dead names, which is true of the engine's own buffers as well; context loss is terminal for
    /// both.</summary>
    internal static void ContextLost()
    {
        _byToken.Clear();
        _byName.Clear();
        _dirty.Clear();
        _bytes = 0;
    }

    internal static string Stats(int frames) =>
        _uploads == 0 ? "" : $"owned geom {_byToken.Count} pools {_bytes / OneMiB}MiB (peak {_peakBytes / OneMiB}MiB, " +
            $"engine host {EngineMib}MiB, uploads {_uploads} = {_subUploads} sub/{_uploads - _subUploads} whole, " +
            $"{_wholePool} whole-pool, grows {_grows}, repacks {_repacks}, minted {_minted}, adopted {_adopted}, handed back " +
            $"{_refused}, borrowed {_borrowed}, retired {_retired})";

    /// <summary>Off-device: the takeover contract, with no GL. A stood-down table takes nothing; a pool the
    /// engine refuses is refused here too; a taken-over pool is indexed by BOTH its token and its name; the
    /// dirty signal opens on the upload and closes on the frame; the delete funnel drops the row without
    /// deleting anything; and the UI tag lands on the tokens a VDO names.
    /// <para>The upload itself is not exercised -- <see cref="Take"/> refuses before it when the GL entry
    /// points are absent, which IS the headless case, so the refusal path runs for real rather than by
    /// injection. What is exercised is everything that decides.</para></summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool wasArmed = _armed;
        byte* savedTable = _table;
        var savedTok = new Dictionary<uint, Entry>(_byToken);
        var savedName = new Dictionary<int, uint>(_byName);
        _byToken.Clear(); _byName.Clear(); _dirty.Clear();
        int refused = _refused, borrowed = _borrowed; _refused = 0; _borrowed = 0;

        // A fake pool table: two 0x20-byte descriptors behind a base pointer, laid out exactly as
        // RequestPool leaves them.
        byte* mem = stackalloc byte[0x20 + 2 * PoolStride];
        byte* pools = mem + 0x20;
        *(byte**)mem = pools;
        *(uint*)(mem + PoolCountOffset) = 2;
        byte* payload = stackalloc byte[64];
        SetPool(pools + 0 * PoolStride, payload, 64, usage: UsageSubData, lockFlags: 0);
        SetPool(pools + 1 * PoolStride, payload, 64, usage: UsageSubData, lockFlags: LockNoUpload);

        _armed = false; _table = mem;
        bool ok = !Take(0, 64, 0) && _byToken.Count == 0;            // stood down: the engine keeps its pool
        _armed = true;
        ok &= !Take(NoPool, 64, 0) && _byToken.Count == 0;           // not a token
        ok &= !Take(5, 64, 0);                                       // past the descriptor count
        ok &= !Take(0, 0, 0);                                        // a zero-length push is the engine's no-op
        ok &= !Take(1, 64, 0);                                       // ...and so is a locked pool
        // Headless: no GL entry points, so a real pool is REFUSED and handed back rather than half-taken.
        ok &= !Take(0, 64, 0) && _byToken.Count == 0 && _refused == 1 && pools[PoolNamed] == 0;

        // From here the table is driven directly -- Record is the half that decides identity, and it is the
        // half that has no GL in it.
        Record(token: 3, name: 77, bytes: 128, fresh: true);
        ok &= _byToken.Count == 1 && _byName[77] == 3 && Owns(77) && !Owns(78);
        ok &= DirtySince(77) && _dirty.Count == 1;
        ClearDirty();
        ok &= !DirtySince(77) && _dirty.Count == 0;
        // A pool that grew keeps its row and its name, and the byte total follows it.
        Record(token: 3, name: 77, bytes: 256, fresh: false);
        ok &= _byToken.Count == 1 && _bytes == 256 && _grows > 0;
        // A recycled token that came back with a NEW name drops the old index rather than leaving it aliased.
        Record(token: 3, name: 78, bytes: 256, fresh: true);
        ok &= !_byName.ContainsKey(77) && _byName[78] == 3 && Owns(78);

        // The UI tag lands on the three tokens a VDO names, and on nothing else.
        byte* vdo = stackalloc byte[0x40];
        for (int i = 0; i < 0x40; i++) vdo[i] = 0xFF;                // every token = NoPool...
        *(uint*)(vdo + VdoStaticToken) = 3;                          // ...but the static one
        NoteEngineDraw((IntPtr)vdo);
        ok &= _byToken[3].EngineDrawn && _byToken[3].AlloyDrawn;

        // The delete funnel drops the row, and the name stops resolving. Nothing is queued: the buffer is
        // the engine's to free, and it is freeing it right now.
        Retire(78);
        ok &= _byToken.Count == 0 && !_byName.ContainsKey(78) && _bytes == 0 && !Owns(78);

        _byToken.Clear(); _byName.Clear(); _dirty.Clear();
        _bytes = 0; _grows = 0; _refused = refused; _borrowed = borrowed;
        _armed = wasArmed; _table = savedTable;
        foreach (var kv in savedTok) _byToken[kv.Key] = kv.Value;
        foreach (var kv in savedName) _byName[kv.Key] = kv.Value;
        log.Info(ok ? "OwnedBuffers.SelfCheck: passed" : "OwnedBuffers.SelfCheck: FAILED");
        return ok;
    }

    private static void SetPool(byte* p, byte* host, int bytes, uint usage, byte lockFlags)
    {
        *(byte**)p = host;
        *(int*)(p + PoolSize) = bytes;
        *(uint*)(p + PoolUsage) = usage;
        p[PoolLockFlags] = lockFlags;
        p[PoolUploaded] = 0;
        p[PoolNamed] = 0;
        *(uint*)(p + PoolName) = 0;
    }
}
