using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #212 (map #68, spec #203 M4 step 4, RHI gate G2): the vertex and index buffers Alloy DRAWS FROM, which
/// are Alloy's own. The geometry twin of <see cref="OwnedTextures"/>, and it borrows that table's whole
/// shape -- key on the engine's GL name, copy GPU-side on first sight, retire on the engine's own delete
/// funnel, queue the delete for a frame that is inside the 4.6 bracket.
/// <para><b>Keyed on the engine GL BUFFER name, not on the part.</b> The ticket says part-keyed; the engine
/// says otherwise. Ghidra <c>VertexDataObject::Build</c> @0x45bac0 allocates its storage as
/// <c>RenderInterface::RequestPool</c> POOLS -- one element pool, one static-vertex pool, one dynamic -- and
/// <c>VertexDataObjectManager::GetVertexDataObjectTokenByAttributes</c> hands the same VDO (so the same
/// pools) to every mesh with matching attributes. Hundreds of parts share one buffer. A part-keyed copy
/// would duplicate that buffer per part, multiply VRAM by the sharing factor, and split the multidraw
/// batches Alloy's submission is built on -- the copy would cost more than the ownership is worth. The
/// buffer is the resource, so the buffer is the key; the part-lifetime edges still apply, one level up.</para>
/// <para><b>The staleness edge is <c>GLRender::UnlockPool</c>.</b> This is what a texture copy does not need.
/// The engine keeps a host-side shadow of every pool and pushes it to GL through exactly one function
/// (Ghidra @0x4cfc80: <c>glBufferData</c> or <c>glBufferSubData</c>, depending on the pool's usage flags).
/// Every writer reaches it -- <c>Build</c>, <c>AddItem</c>'s grow, the three <c>CommitItem*Data</c>, and
/// <c>Repack</c>'s post-compaction full re-upload. So one hook marks the copy dirty and <see cref="Refresh"/>
/// re-copies it at the next bracket, which is also what makes the ticket's "VDO repack is handled" true for
/// the DATA (the index/offset half was already handled by revoking every part on a repacked VDO).</para>
/// <para>Scope: <see cref="AlloyRenderer.EnsureVao"/> builds a SECOND VAO per VDO over these copies, and
/// #213 widened its use to EVERY class Alloy draws -- opaque, cutout, blended, water, additive, the sky dome,
/// the skinned parts, and the shadow pass's own VDO table. What still draws from an engine buffer is a slot
/// whose copy was refused, and that falls back whole rather than per stream.</para>
/// <para>Residency is ADR 0016's: upload on enroll, free on revoke, <b>no budget and no LRU</b>, with a
/// logged VRAM high-water mark. A budget before a measurement is #149's mistake in a new costume.</para>
/// </summary>
internal static unsafe class OwnedBuffers
{
    /// <summary>An engine buffer name we have already looked at. <c>Owned</c> 0 means "copy refused" --
    /// cached, so a buffer we cannot copy is not re-attempted; that VDO draws from the engine's buffers.</summary>
    private struct Entry
    {
        internal uint Owned;
        internal long Bytes;
        internal bool Dirty;
        /// <summary>#213: the byte range the engine has written since the last <see cref="Refresh"/>, in
        /// buffer coordinates -- the coalesced union of every <c>UnlockPool</c> span seen. Meaningful only
        /// while <see cref="Dirty"/> and not <see cref="Full"/>.</summary>
        internal long Lo, Hi;
        /// <summary>The engine asked for a whole-pool push (<see cref="WholePool"/>), so the span is the
        /// buffer and the refresh is a re-blit rather than a sub-range copy.</summary>
        internal bool Full;
    }

    private static readonly Dictionary<int, Entry> _map = new();
    /// <summary>Names the engine has written to since the last <see cref="Refresh"/>, and copies retired and
    /// waiting for a bracket to delete them in -- the same split <c>OwnedTextures</c> makes, for the same
    /// reason: both funnels fire in the ENGINE context, where a GL delete would go to the wrong context.
    /// <para>Both are appended from an engine hook, so both are sized past their real bound rather than past
    /// a guess: an entry can only exist for a buffer we copied, and a VDO carries at most three pools, so
    /// <see cref="AlloyRenderer.MaxVdos"/> * 3 cannot be exceeded however an area transition bursts. A
    /// <c>List</c> growth here would allocate on the render thread, which is what the size is for.</para></summary>
    private static readonly List<int> _dirty = new(PoolCeiling);
    private static readonly List<uint> _pending = new(PoolCeiling);
    /// <summary>Element, static-vertex and dynamic-vertex, per VDO -- <c>VertexDataObject::Build</c>
    /// @0x45bac0 requests no fourth pool.</summary>
    private const int PoolCeiling = AlloyRenderer.MaxVdos * 3;
    private static ExtenderLog? _log;
    private static bool _armed;
    /// <summary>Is the owned-geometry path live? Read by the VAO builder, which only builds the second VAO
    /// when it is.</summary>
    internal static bool Armed => _armed;
    /// <summary>How far ownership reaches, so a live session can bisect a suspected owned-geometry fault
    /// without a rebuild. 0 = off, 1 = the colour passes only (the shadow bake keeps the engine's pools),
    /// 2 = everything. Read by the shadow pass, which is the only caller that can be excluded on its own.
    /// <para>ponytail: a bisect knob, not a feature. It goes when #227 -- the intermittent shadow artifact
    /// it exists to chase -- closes.</para></summary>
    internal static bool ArmedForShadows => _armed && _level >= 2;
    /// <summary>Refresh a dirty copy over the sub-range the engine named (1) or re-blit the whole buffer (0).
    /// The second is #212's behaviour, kept as the other half of the bisect.</summary>
    internal static bool SpanRefresh => _span;
    /// <summary>Engine buffers we tried to copy and could not; the VAO builder then keeps the ENGINE's.
    /// #218's exit bar reads it, same as <c>OwnedTextures.Refused</c>.</summary>
    internal static int Refused => _refused;
    private static int _level = 2;
    private static bool _span = true;
    private static long _bytes, _peakBytes;
    private static int _copies, _refused, _recopies, _retired, _peakCount;
    /// <summary>#213's measurement of the per-frame movers: pool uploads seen against copies we hold, and how
    /// many of the resulting refreshes were a sub-range copy rather than a whole-buffer re-blit. A dynamic
    /// class that is being kept current shows up here as spans, every frame.</summary>
    private static long _unlocks, _spanRefresh, _fullRefresh, _spanBytes;

    /// <summary>Wired once from <see cref="AlloyRenderer"/>'s init, before any frame runs.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _level = Settings.Int("ownedgeom", 2, 0, 2);
        _armed = _level > 0;
        _span = Settings.Bool("ownedgeomspan", true);
        log.Info(_armed
            ? $"OwnedBuffers: armed at level {_level} -- {(_level >= 2 ? "every Alloy class including the shadow bake" : "the colour passes only; the shadow bake keeps the engine's pools")} " +
              $"draws from Alloy-created vertex/index buffers, refreshing {(_span ? "the sub-range the engine named" : "the whole buffer (ownedgeomspan=0)")} " +
              "(ownedgeom=1 to exclude the shadow bake, 0 to draw from engine pools throughout)"
            : "OwnedBuffers: stood down (ownedgeom=0) -- every class draws from the engine's own pool buffers");
    }

    /// <summary>The two engine edges: the upload funnel (a copy has gone stale) and the delete funnel (a
    /// copy's name is about to mean something else). Without the second, an area transition frees the pools
    /// and the next area's <c>glGenBuffers</c> hands the same names back -- stable wrong-geometry corruption,
    /// which is #220 with vertices instead of texels.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!_armed) return;
        _getPoolBuffer = (delegate* unmanaged<uint, uint>)Native.Sym(SymbolMap.RiGetPoolBuffer);
        _origUnlock = (delegate* unmanaged<uint, uint, uint, void>)hooks.Install(
            SymbolMap.GlUnlockPool, (IntPtr)(delegate* unmanaged<uint, uint, uint, void>)&OnUnlockPool);
        _origDelete = (delegate* unmanaged<uint, void>)hooks.Install(
            SymbolMap.GlDeleteBuffer, (IntPtr)(delegate* unmanaged<uint, void>)&OnDeleteBuffer);
        bool ok = _getPoolBuffer != null && _origUnlock != null && _origDelete != null;
        if (!ok) _armed = false;
        log.Info(ok
            ? "OwnedBuffers: watching GLRender::UnlockPool + GLRender::DeleteBuffer -- copies refresh on upload and retire with their name"
            : "OwnedBuffers: stood down -- the pool upload/delete funnels did not resolve, and a copy nobody can invalidate is stale geometry");
    }

    private static delegate* unmanaged<uint, uint, uint, void> _origUnlock;
    private static delegate* unmanaged<uint, void> _origDelete;
    private static delegate* unmanaged<uint, uint> _getPoolBuffer;

    /// <summary>The engine just pushed a pool's host shadow into its GL buffer. AFTER the original, because
    /// the buffer name may be created inside it (UnlockPool <c>glGenBuffers</c> on first use).
    /// <para>Hot-adjacent: dynamic pools (UI primitives, particle streams) unlock every frame. Those names
    /// are never in the map, so the steady-state cost is one failed dictionary probe, and nothing here
    /// allocates -- both lists are pre-sized and the dirty rewrite is an indexer store on a present key.
    /// Issues no GL: this runs wherever the engine happened to be.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnUnlockPool(uint token, uint size, uint offset)
    {
        if (_origUnlock != null) _origUnlock(token, size, offset);
        MarkDirty(token, size, offset);
    }

    /// <summary>The handler's body, split out so <see cref="SelfCheck"/> can exercise it -- an
    /// <c>UnmanagedCallersOnly</c> method cannot be called from managed code.</summary>
    private static void MarkDirty(uint token, uint size, uint offset)
    {
        if (_map.Count == 0 || _getPoolBuffer == null || token == NoPool) return;
        // ponytail: only HALF the engine's own guard. GLRender::UnlockPool @0x4cfc80 tests the token against
        // the pool table's capacity as well, and GetPoolBuffer @0x4cfba0 does not -- but that count global
        // has no symbol to resolve, and every token reaching here came from RequestPool and is bounded by
        // construction. Upgrade path if a wild token ever appears: hook RequestPool/DeletePool and keep our
        // own token validity set. A garbage NAME would be harmless either way -- the worst it can do is
        // dirty a buffer of ours, which re-copies from the same real source.
        int name = (int)_getPoolBuffer(token);
        if (!_map.TryGetValue(name, out Entry e) || e.Owned == 0) return;
        _unlocks++;
        bool full = size == WholePool;
        if (!e.Dirty)
        {
            e.Dirty = true; e.Full = full; e.Lo = offset; e.Hi = (long)offset + size;
            _dirty.Add(name);
        }
        else if (full) e.Full = true;
        else
        {
            // ponytail: ONE coalesced span, not a set of them -- two writes to opposite ends of a pool copy
            // the pool. A span list would allocate on this hook; upgrade path is a fixed-size span array.
            if (offset < e.Lo) e.Lo = offset;
            if ((long)offset + size > e.Hi) e.Hi = (long)offset + size;
        }
        _map[name] = e;
    }

    /// <summary>The engine is deleting this GL buffer name. Anything copied from it is retired: the name is
    /// about to be handed to some other pool, and our copy is about to be the wrong mesh.</summary>
    [UnmanagedCallersOnly]
    private static void OnDeleteBuffer(uint name)
    {
        Retire(name);
        if (_origDelete != null) _origDelete(name);
    }

    private static void Retire(uint name)
    {
        if (!_map.TryGetValue((int)name, out Entry e)) return;
        _map.Remove((int)name);
        if (e.Owned == 0) return;
        _pending.Add(e.Owned);
        _bytes -= e.Bytes;
        _retired++;
    }

    /// <summary>Point one harvested vertex stream at Alloy's copy of its buffer, for a VAO being built over
    /// the copies. Offsets and strides are untouched: the copy is byte-identical, so every item offset the
    /// VDO tables hand the draw still lands. False (leaving <paramref name="s"/> alone) when there is no
    /// copy, which costs the caller its whole owned VAO rather than half of one.</summary>
    internal static bool Rebase(ref VertexStream s)
    {
        uint own = Draw(s.Buf);
        if (own == 0) return false;
        s.Buf = own;
        return true;
    }

    /// <summary>The name a VAO should bind for this engine buffer: our copy, or 0 when the table is stood
    /// down or the copy refused (the caller then binds nothing owned and keeps the engine VAO). Cold -- it
    /// runs from the per-VDO VAO build, not per frame. Must be called inside the 4.6 bracket: it issues
    /// GL.</summary>
    internal static uint Draw(uint engineBuf)
    {
        if (!_armed || engineBuf == 0) return 0;
        if (_map.TryGetValue((int)engineBuf, out Entry e)) return e.Owned;
        uint owned = Copy(engineBuf, out long bytes);
        _map[(int)engineBuf] = new Entry { Owned = owned, Bytes = bytes };
        if (owned == 0) { _refused++; return 0; }
        _copies++;
        _bytes += bytes;
        if (_map.Count > _peakCount) _peakCount = _map.Count;
        HighWater();
        return owned;
    }

    /// <summary>#241: has the engine written into this buffer since the last <see cref="Refresh"/>? The
    /// re-bake predicate's VERTEX-motion signal -- dangly and morph-animated nodes both commit through
    /// <c>CommitItemDynamicData</c>, so both land in the dirty set with no per-class test. Only ever asked
    /// of a buffer this table OWNS: an unowned VDO's writes are invisible here, which is why the dangly
    /// class still carries its own flag.</summary>
    internal static bool DirtySince(uint engineBuf)
        => engineBuf != 0 && _map.TryGetValue((int)engineBuf, out Entry e) && e.Dirty;

    /// <summary>Re-copy every buffer the engine has written to since the last frame. Once per frame from
    /// inside the 4.6 bracket; nothing to do is the normal case and costs one count check.
    /// <para>#213: the sub-range the engine named, where it named one -- see <see cref="Respan"/>. That is
    /// the update path the per-frame movers need: dangly (<c>PartDanglyMesh::Animate</c> @0x49c935) and
    /// morph-animated nodes (<c>MdlNodeAnimMesh::InternalControl</c> @0x4996b9) both commit through
    /// <c>CommitItemDynamicData</c>, which tail-calls <c>UnlockPool</c> with that one item's byte span.</para>
    /// <para>A write that lands AFTER this call is one frame stale on screen, then correct. The engine
    /// commits geometry outside the Alloy bracket, so the window is the tail of the same frame's load.</para>
    /// </summary>
    internal static void Refresh()
    {
        if (_dirty.Count == 0) return;
        int n = _dirty.Count;
        for (int i = 0; i < n; i++)
        {
            int name = _dirty[i];
            if (!_map.TryGetValue(name, out Entry e) || e.Owned == 0) continue;
            e.Dirty = false;
            // ponytail: a recopy that fails leaves the PREVIOUS copy bound and one upload stale. Retiring it
            // instead would delete a buffer the VAO still references (GL keeps the storage alive, so the draw
            // would be stale forever anyway) for strictly more machinery. Upgrade path if a driver ever fails
            // here routinely: mark the slot's VAO stale so it rebuilds on the engine's buffers.
            long now = e.Full || !_span
                ? Recopy((uint)name, e.Owned)
                : Respan((uint)name, e.Owned, e.Lo, e.Hi, e.Bytes);
            if (now != 0) { _bytes += now - e.Bytes; e.Bytes = now; }
            _map[name] = e;
            _recopies++;
        }
        _dirty.Clear();
        HighWater();
    }

    /// <summary>#213: refresh only the bytes the engine said it wrote. <c>UnlockPool</c>'s second and third
    /// arguments ARE the sub-range -- Ghidra @0x4cfc80 passes them straight to <c>glBufferSubData</c> as size
    /// and offset -- so a dangly mesh or a morph-animated node costs one item-sized copy per frame instead of
    /// its whole shared pool, which on a static-vertex pool is every mesh that shares the VDO.
    /// <para>A size change means the pool was reallocated (<c>ResizePool</c>, then a full
    /// <c>glBufferData</c>), and a sub-range copy into a copy of the old size would be geometry from two
    /// different layouts. That falls back to the whole-buffer path, which reallocates ours to match.</para>
    /// </summary>
    /// <remarks>The span is the engine's, and it has TWO upload branches. Which one a pool takes is the
    /// <c>RequestPool</c> flags word at pool+0x14: bit 8 set (@0x4cfd43) honours the offset and uploads
    /// <c>[offset, offset+size)</c>; bit 8 clear with bit 1 set (@0x4cfe66) ignores the offset entirely and
    /// uploads <c>[0, size)</c> from the host base. The span below is right under both, because the only
    /// pool of the second kind is the global element stream -- <c>VertexDataObject::Build</c> @0x45bac0
    /// requests its three per-VDO pools with 0x102 (dynamic), 0x100 (element) and 0x100 (static vertex), all
    /// bit 8, while the element-stream pool is 0x2 and <c>CommitElementStreamData</c> @0x45c9c0 unlocks it
    /// with <c>xor %edx,%edx</c>, i.e. offset 0, where the two branches agree.</remarks>
    private static long Respan(uint src, uint dst, long lo, long hi, long known)
    {
        long size = SizeOf(src);
        if (size != known || size <= 0) { _fullRefresh++; return size <= 0 ? 0 : Blit(src, dst, size); }
        if (lo < 0) lo = 0;
        if (hi > size) hi = size;
        if (hi <= lo) return known;
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, src);
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, dst);
        Gl.CopyBufferSubData(Gl.COPY_READ_BUFFER, Gl.COPY_WRITE_BUFFER, (IntPtr)lo, (IntPtr)lo, (IntPtr)(hi - lo));
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, 0);
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, 0);
        _spanRefresh++; _spanBytes += hi - lo;
        return known;
    }

    /// <summary>One engine buffer -> one owned copy, GPU-side. Returns 0 (leaving nothing allocated) on any
    /// refusal -- a missing entry point, a zero-sized source, or a driver that will not take the
    /// allocation.</summary>
    private static uint Copy(uint src, out long bytes)
    {
        bytes = 0;
        if (Gl.CopyBufferSubData == null || Gl.GetBufferParameteriv == null || Gl.GenBuffers == null
            || Gl.BufferData == null || Gl.DeleteBuffers == null) return 0;
        long size = SizeOf(src);
        if (size <= 0) return 0;
        uint dst; Gl.GenBuffers(1, &dst);
        if (dst == 0) return 0;
        bytes = Blit(src, dst, size);
        if (bytes != 0) return dst;
        Gl.DeleteBuffers(1, &dst);
        return 0;
    }

    /// <summary>The engine wrote to <paramref name="src"/>; bring <paramref name="dst"/> back into agreement,
    /// reusing the name so every VAO built over it stays valid even when the pool GREW (ResizePool reallocs
    /// the host shadow and the next UnlockPool is a full glBufferData, so a size change is normal). Returns
    /// the new byte count, or 0 if the refresh failed and the copy has to be dropped.</summary>
    private static long Recopy(uint src, uint dst)
    {
        long size = SizeOf(src);
        _fullRefresh++;
        return size <= 0 ? 0 : Blit(src, dst, size);
    }

    /// <summary>Allocate-and-copy, on the COPY_READ/COPY_WRITE targets. Those two exist precisely so a buffer
    /// can be moved without disturbing a draw binding: nothing here touches ARRAY_BUFFER or
    /// ELEMENT_ARRAY_BUFFER, so <c>GLRender::m_nArrayBufferBound</c> -- the one buffer binding the engine
    /// still shadow-caches after #210 -- cannot desync behind us.</summary>
    private static long Blit(uint src, uint dst, long size)
    {
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, dst);
        while (Gl.GetError() != 0) { }   // ours to read below, so nobody else's error may be sitting here
        Gl.BufferData(Gl.COPY_WRITE_BUFFER, (IntPtr)size, null, Gl.STATIC_DRAW);
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, src);
        Gl.CopyBufferSubData(Gl.COPY_READ_BUFFER, Gl.COPY_WRITE_BUFFER, IntPtr.Zero, IntPtr.Zero, (IntPtr)size);
        uint err = Gl.GetError();
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, 0);
        Gl.BindBuffer(Gl.COPY_WRITE_BUFFER, 0);
        if (err == 0) return size;
        if (_diagLeft > 0) { _diagLeft--; _log?.Info($"OwnedBuffers: refused buffer {src} -- {size} bytes gave 0x{err:x}"); }
        return 0;
    }

    private static long SizeOf(uint buf)
    {
        int size = 0;
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, buf);
        Gl.GetBufferParameteriv(Gl.COPY_READ_BUFFER, Gl.BUFFER_SIZE, &size);
        Gl.BindBuffer(Gl.COPY_READ_BUFFER, 0);
        return size;
    }

    /// <summary>Capped: a driver that refuses one allocation refuses them all, and the first few lines say
    /// which size as well as any number of them would.</summary>
    private static int _diagLeft = 8;

    private const long OneMiB = 1024 * 1024;
    /// <summary>The engine's "this VDO has no such pool" token (<c>VertexDataObject::AddItem</c> tests it at
    /// +0x24 and +0x30).</summary>
    private const uint NoPool = 0xFFFFFFFF;
    /// <summary>The size <c>GLRender::UnlockPool</c> reads as "the whole pool": both of its
    /// <c>glBufferSubData</c> sites substitute the pool's own byte count for it (@0x4cfda1/0x4cfda3 and
    /// @0x4cfe7d/0x4cfe7f).</summary>
    private const uint WholePool = 0xFFFFFFFF;

    /// <summary>ADR 0016's measurement, and the only VRAM number this ticket ships. Every MiB, not every
    /// buffer: the count climbs on every insert, and per-copy logging inside the bracket is what made the
    /// texture table's first high-water line allocate a few hundred times per area load.</summary>
    private static void HighWater()
    {
        if (_bytes <= _peakBytes + OneMiB) return;
        _peakBytes = _bytes;
        _log?.Info($"OwnedBuffers: [vram] high-water {_peakBytes / OneMiB}MiB across {_peakCount} owned " +
                   $"buffers (copies {_copies}, refreshes {_recopies}, refused {_refused})");
    }

    /// <summary>The WORLD edge: the engine names we keyed on are going away and will be handed back out, so
    /// every copy is dropped. The 4.6 context is alive here, so the copies are queued for DELETION -- that is
    /// the VRAM coming back, and ADR 0016's "free on revoke".</summary>
    internal static void Revoke()
    {
        if (_map.Count == 0) return;
        foreach (Entry e in _map.Values) if (e.Owned != 0) _pending.Add(e.Owned);
        _log?.Info($"OwnedBuffers: revoked {_map.Count} owned buffers ({_bytes / OneMiB}MiB queued for release)");
        _map.Clear();
        _dirty.Clear();
        _bytes = 0;
    }

    /// <summary>Delete what <see cref="Revoke"/> and the delete funnel retired, in the context the names
    /// live in. Called once per frame from inside the 4.6 bracket, beside <c>OwnedTextures.Drain</c>.
    /// <para>The VAOs built over these names are retired by the same edge that revokes (every VDO slot is
    /// marked stale), so no VAO outlives a buffer it references.</para></summary>
    internal static void Drain()
    {
        if (_pending.Count == 0 || Gl.DeleteBuffers == null) return;
        int n = _pending.Count;
        for (int i = 0; i < n; i++) { uint name = _pending[i]; Gl.DeleteBuffers(1, &name); }
        _pending.Clear();
        _log?.Info($"OwnedBuffers: released {n} owned buffers (retired by the engine so far: {_retired})");
    }

    /// <summary>The GL-NAME edge: the objects went with the context. Names are dropped, nothing is deleted --
    /// deleting into a dead context is what #209 calls the terminal edge.</summary>
    internal static void ContextLost()
    {
        _map.Clear();
        _dirty.Clear();
        _pending.Clear();
        _bytes = 0;
    }

    internal static string Stats(int frames) =>
        _copies == 0 ? "" : $"owned geom {_map.Count} bufs {_bytes / OneMiB}MiB (peak {_peakBytes / OneMiB}MiB, " +
            $"refresh {_recopies} = {_spanRefresh} span/{_fullRefresh} full, {_spanBytes / 1024}KiB spanned, " +
            $"unlocks {_unlocks}, refused {_refused})";

    /// <summary>Off-device: the caching contract, with no GL. An unarmed table is a pass-through, a refused
    /// copy is remembered rather than retried, the upload funnel dirties exactly the names we hold, and the
    /// delete funnel queues OUR name rather than the engine's.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool wasArmed = _armed;
        var saved = new Dictionary<int, Entry>(_map);
        var savedPool = _getPoolBuffer;
        _map.Clear(); _dirty.Clear(); _pending.Clear(); _refused = 0; _unlocks = 0;

        _armed = false;
        bool ok = Draw(7) == 0 && _map.Count == 0;          // stood down: nothing owned, nothing remembered
        _armed = true;
        ok &= Draw(0) == 0 && _map.Count == 0;              // no name is not a buffer

        // Copy() refuses without GL entry points, which IS the headless case -- so the refusal path is
        // exercised for real rather than by injection.
        ok &= Draw(11) == 0 && _map.Count == 1;             // refused...
        ok &= Draw(11) == 0 && _map.Count == 1 && _refused == 1;   // ...and remembered, not retried

        _map[13] = new Entry { Owned = 99, Bytes = 64 };
        ok &= Draw(13) == 99;                                // a copy that exists is what the VAO binds

        // the upload funnel dirties the copy behind the name the token resolves to, and a refused entry has
        // nothing to dirty (a Refresh of one would try to copy into buffer 0).
        _getPoolBuffer = &FakePool;
        MarkDirty(13, 16, 32); MarkDirty(13, 16, 8); MarkDirty(11, 16, 0);
        ok &= _dirty.Count == 1 && _dirty[0] == 13 && _map[13].Dirty;
        // #213: two disjoint writes coalesce to the span that covers both, and neither is a full re-blit.
        ok &= !_map[13].Full && _map[13].Lo == 8 && _map[13].Hi == 48;
        // ...and a whole-pool unlock (size 0xFFFFFFFF) promotes the entry past the span arithmetic, which
        // would otherwise compute a 4GiB range from it.
        MarkDirty(13, WholePool, 0);
        ok &= _map[13].Full && _dirty.Count == 1;

        // ...and the delete funnel retires the copy made from that name, queueing OURS.
        Retire(13);
        ok &= !_map.ContainsKey(13) && _pending.Count == 1 && _pending[0] == 99;

        _map.Clear(); _dirty.Clear(); _pending.Clear(); _refused = 0; _bytes = 0; _unlocks = 0;
        _armed = wasArmed; _getPoolBuffer = savedPool;
        foreach (var kv in saved) _map[kv.Key] = kv.Value;
        log.Info(ok ? "OwnedBuffers.SelfCheck: passed" : "OwnedBuffers.SelfCheck: FAILED");
        return ok;
    }

    /// <summary>SelfCheck's stand-in for the engine's pool table: token IS the buffer name.</summary>
    [UnmanagedCallersOnly]
    private static uint FakePool(uint token) => token;
}
