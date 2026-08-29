using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #211 (map #68, spec #203 M4, RHI gate G2): the textures Alloy DRAWS FROM, which are Alloy's own.
/// <para>The engine's are borrowed: a <c>CAurTexture*</c> is freed on an area transition and on module
/// unload, and every owned draw so far worked around that by re-fetching the id through
/// <c>AurTextureGetTID</c> every frame behind the <see cref="AurTex"/> vtable guard. Re-fetching times
/// around the failure; owning removes it. A texture Alloy created cannot be freed under Alloy.</para>
/// <para>The copy is GPU-side and pays nothing per frame: on first sight of an engine texture name,
/// <c>glTexStorage2D</c> the same format and level count and <c>glCopyImageSubData</c> each level across.
/// No Aurora decode, no CPU staging, no readback -- the engine already put the decoded image in a GL
/// texture and the 4.6 context shares its objects, so the image is already reachable. Sampler state is
/// NOT part of a copy, so filter and wrap are read off the engine name and set on ours (which also closes
/// the gap Particles.cs documents: <c>AurTextureGetTID</c> skips the engine's own filter/wrap setup, so an
/// owned draw used to sample through whatever the last binder left behind).</para>
/// <para><b>The key is the engine's GL name.</b> Not the <c>CAurTexture*</c> -- that is a per-material
/// reference whose address the allocator recycles (#221) -- and not the resref, which cannot be built on a
/// hot path without allocating. While a GL name is live it identifies exactly one image; it is only
/// recycled after a delete, and deletes come with the world edges this table is wiped on.</para>
/// <para>ponytail: an engine texture re-uploaded UNDER a name we already copied (AurTextureRefreshAll, the
/// TTF atlas rebuild, AurTextureSetLayered) leaves us serving the old image until the next world edge.
/// The upgrade path is a per-entry content stamp, which nothing cheap exists for -- so the ceiling is a
/// boot gate instead: <c>ownedtex=0</c> in sowx.cfg draws from engine names exactly as before #211.</para>
/// <para>Lifecycle (docs/owned-resource-lifetimes.md): <see cref="Revoke"/> is the WORLD edge -- it drops
/// the map and QUEUES the copies for deletion, because the world walk runs at a frame edge in the ENGINE
/// context and a GL delete there would be issued against the wrong context entirely. <see cref="Drain"/>
/// does the deleting on the next frame that enters the 4.6 bracket, the same split
/// <c>EnsureVao</c> makes for its retired VAO names. <see cref="ContextLost"/> is the GL-name edge -- the
/// objects went with the context, so map and queue are dropped and nothing is deleted.</para>
/// </summary>
internal static unsafe class OwnedTextures
{
    /// <summary>An engine name we have already looked at. <c>Owned</c> 0 means "copy refused" -- cached so a
    /// texture we cannot copy is not re-attempted every frame; that name draws from the engine's id.</summary>
    private readonly struct Entry
    {
        internal readonly uint Owned;
        internal readonly long Bytes;
        internal Entry(uint owned, long bytes) { Owned = owned; Bytes = bytes; }
    }

    private static readonly Dictionary<int, Entry> _map = new();
    /// <summary>The reverse question the bindless path has to ask: is this name a copy of ours? A handle must
    /// never be minted on an engine texture (that IS #220), and a refused copy hands the engine id straight
    /// back, so "armed" is not the same as "this name is owned".</summary>
    private static readonly HashSet<uint> _owned = new();
    /// <summary>Names retired and waiting for a bracket to delete them in. Pre-sized: entries are added from
    /// the engine's delete funnel, which runs on the render thread, and a List growth there would allocate
    /// on a hot-adjacent path.</summary>
    private static readonly List<uint> _pending = new(1024);
    private static ExtenderLog? _log;
    private static bool _armed;
    /// <summary>Is the owned-texture path live? Read by the handle cache, which changes its invalidation
    /// discipline entirely on the answer (see <c>AlloyRenderer.WipeHandles</c>).</summary>
    internal static bool Armed => _armed;

    /// <summary>Engine textures we tried to copy and could not, and therefore sample BORROWED. #218's exit
    /// bar reads it: "all textures Alloy-created" is false the moment this is non-zero.</summary>
    internal static int Refused => _refused;
    private static long _bytes, _peakBytes;
    private static int _copies, _refused, _peakCount;

    /// <summary>Wired once from <see cref="AlloyRenderer"/>'s init, before any frame runs.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _armed = Settings.Bool("ownedtex", true);
        log.Info(_armed
            ? "OwnedTextures: armed -- owned draws sample Alloy-created copies (ownedtex=0 to draw from engine ids)"
            : "OwnedTextures: stood down (ownedtex=0) -- owned draws sample engine texture ids");
    }

    /// <summary>#220: chain the engine's texture-delete funnel. This is the edge the first cut of #211 did
    /// not have -- an AREA TRANSITION deletes the engine's textures and fires none of the world edges Alloy
    /// watches (the part table is not wiped on a transition; #220 measured one wipe in a whole session), so
    /// a map keyed on engine names would serve the previous area's copy for a recycled name. Stable
    /// wrong-texture corruption, with no bindless involved.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!_armed) return;
        _origDelete = (delegate* unmanaged<uint, uint*, void>)hooks.Install(
            SymbolMap.RiDeleteTextures, (IntPtr)(delegate* unmanaged<uint, uint*, void>)&OnDeleteTextures);
        log.Info(_origDelete != null
            ? "OwnedTextures: watching RenderInterface::DeleteTextures -- copies retire with the name they were made from"
            : "OwnedTextures: DeleteTextures not installed -- copies only retire on a world edge (area transitions will serve stale textures)");
    }

    private static delegate* unmanaged<uint, uint*, void> _origDelete;
    private static long _retired;

    /// <summary>The engine is deleting these GL names. Anything we copied from one of them is retired: the
    /// name is about to mean something else, and our copy is about to be the wrong picture rather than a
    /// stale one. Hot-adjacent (fires in bursts on transitions, never per frame) and allocation-free in the
    /// steady state -- Dictionary/HashSet removal allocates nothing and <see cref="_pending"/> is pre-sized.
    /// <para>Issues no GL: this runs wherever the engine happened to be, which is not the 4.6 context.
    /// <see cref="Drain"/> does the deleting.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnDeleteTextures(uint n, uint* names)
    {
        Retire(n, names);
        if (_origDelete != null) _origDelete(n, names);
    }

    /// <summary>The handler's body, split out so <see cref="SelfCheck"/> can exercise it -- an
    /// <c>UnmanagedCallersOnly</c> method cannot be called from managed code (AurTex splits <c>Live</c> out
    /// of <c>Tid</c> for the same reason).</summary>
    private static void Retire(uint n, uint* names)
    {
        if (names == null) return;
        for (uint i = 0; i < n; i++)
        {
            int key = (int)names[i];
            if (!_map.TryGetValue(key, out Entry e)) continue;
            _map.Remove(key);
            if (e.Owned == 0) continue;
            _owned.Remove(e.Owned);
            _pending.Add(e.Owned);
            _bytes -= e.Bytes;
            _retired++;
        }
    }

    /// <summary>#220: is this name one of ours? The bindless path mints a handle only when it is -- a handle
    /// on an engine texture is a handle on an object the engine may delete or re-upload under us, which is
    /// the whole of #220.</summary>
    internal static bool IsOwned(int name) => name > 0 && _owned.Contains((uint)name);

    /// <summary>The name an owned draw should sample for this engine texture id: our copy, or
    /// <paramref name="engineTid"/> itself when the table is stood down or the copy refused. Hot -- called
    /// per Alloy part per frame -- but allocation-free and, after first sight, one dictionary probe.
    /// Must be called inside the 4.6 bracket: the copy issues GL.</summary>
    internal static int Draw(int engineTid)
    {
        if (!_armed || engineTid <= 0) return engineTid;
        if (_map.TryGetValue(engineTid, out Entry e)) return e.Owned != 0 ? (int)e.Owned : engineTid;
        uint owned = Copy(engineTid, out long bytes);
        _map[engineTid] = new Entry(owned, bytes);
        if (owned == 0) { _refused++; return engineTid; }
        _owned.Add(owned);
        _copies++;
        _bytes += bytes;
        if (_map.Count > _peakCount) _peakCount = _map.Count;
        // Every MiB, not every texture. The count climbs on every single insert, so testing it here made the
        // "high-water" line fire once per texture -- a few hundred interpolated log lines while an area
        // loads, allocating inside the 4.6 bracket. Entry count runs ABOVE "textures in the area": an
        // animated texture cycles one engine name per animation frame and each is its own resource. That is
        // correct and bounded, not a leak.
        if (_bytes > _peakBytes + OneMiB)
        {
            _peakBytes = _bytes;
            _log?.Info($"OwnedTextures: [vram] high-water {_peakBytes / OneMiB}MiB across {_peakCount} " +
                       $"owned textures (copies {_copies}, refused {_refused})");
        }
        return (int)owned;
    }

    /// <summary>One engine name -> one owned copy. Cold: runs on first sight of a texture and never again.
    /// Returns 0 (and leaves nothing allocated) on any refusal -- a missing entry point, a level-0 query
    /// that answers nothing, or a driver without <c>glCopyImageSubData</c>.</summary>
    private static uint Copy(int src, out long bytes)
    {
        bytes = 0;
        if (Gl.CopyImageSubData == null || Gl.TexStorage2D == null || Gl.GetTexLevelParameteriv == null
            || Gl.GenTextures == null || Gl.GetTexParameteriv == null) return 0;

        int saved; Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &saved);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)src);
        while (Gl.GetError() != 0) { }   // ours to read below, so nobody else's error may be sitting here
        int w0 = 0, h0 = 0, fmt = 0;
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_WIDTH, &w0);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_HEIGHT, &h0);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        fmt = Sized(fmt);
        if (w0 <= 0 || h0 <= 0 || fmt == 0) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)saved); return 0; }

        // TEXTURE_MAX_LEVEL is a cap, not a count -- walk until a level has no storage (TexProbe learned the
        // same thing the hard way). Every level of an immutable texture must exist, so the count is what we
        // find, not what the engine claims.
        int levels = 1;
        while (levels < MaxLevels)
        {
            int lw = 0; Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, levels, Gl.TEXTURE_WIDTH, &lw);
            if (lw <= 0) break;
            levels++;
        }
        for (int l = 0; l < levels; l++) bytes += LevelBytes(l);

        // filter/wrap, read here while the SOURCE is bound
        int minf = 0, magf = 0, wrapS = 0, wrapT = 0, maxLevel = 0;
        Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, &minf);
        Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, &magf);
        Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, &wrapS);
        Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, &wrapT);
        Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, &maxLevel);

        uint dst; Gl.GenTextures(1, &dst);
        if (dst == 0) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)saved); return 0; }
        Gl.BindTexture(Gl.TEXTURE_2D, dst);
        Gl.TexStorage2D(Gl.TEXTURE_2D, levels, (uint)fmt, w0, h0);
        // The storage decides everything after it. A format glTexStorage2D will not take leaves the texture
        // with NO storage, every copy into it fails, and the part draws BLACK -- which is what shipped, and
        // what a refusal here turns back into the engine's own texture.
        uint err = Gl.GetError();
        if (err != 0)
        {
            Gl.BindTexture(Gl.TEXTURE_2D, (uint)saved);
            Gl.DeleteTextures(1, &dst);
            if (_fmtDiagLeft > 0) { _fmtDiagLeft--; _log?.Info($"OwnedTextures: refused tid {src} -- glTexStorage2D(fmt=0x{fmt:x}, {levels} levels, {w0}x{h0}) gave 0x{err:x}"); }
            bytes = 0;
            return 0;
        }
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, minf);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, magf);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, wrapS);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, wrapT);
        // A min filter that mips against one stored level samples nothing; clamp the cap to what we copied.
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, maxLevel < levels - 1 ? maxLevel : levels - 1);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)saved);

        for (int l = 0, lw = w0, lh = h0; l < levels; l++, lw = lw > 1 ? lw >> 1 : 1, lh = lh > 1 ? lh >> 1 : 1)
            Gl.CopyImageSubData((uint)src, Gl.TEXTURE_2D, l, 0, 0, 0, dst, Gl.TEXTURE_2D, l, 0, 0, 0, lw, lh, 1);
        err = Gl.GetError();
        if (err != 0)
        {
            Gl.DeleteTextures(1, &dst);
            if (_fmtDiagLeft > 0) { _fmtDiagLeft--; _log?.Info($"OwnedTextures: refused tid {src} -- glCopyImageSubData(fmt=0x{fmt:x}, {levels} levels, {w0}x{h0}) gave 0x{err:x}"); }
            bytes = 0;
            return 0;
        }
        return dst;
    }

    /// <summary>The sized internal format to allocate for an engine texture, or 0 for one we will not try.
    /// <para>The engine uploads through <c>glTexImage2D</c> with an UNSIZED internal format (@0x4cad3d passes
    /// GL_RGB, GL_RGBA and GL_RED verbatim), and a driver reports that enum straight back from
    /// <c>TEXTURE_INTERNAL_FORMAT</c>. <c>glTexStorage2D</c> takes sized formats only, so the unsized answers
    /// have to be promoted or nothing is allocated at all. Everything else is passed through as-is and stands
    /// or falls on the error check at the allocation -- guessing at a format we have not seen is how this bug
    /// happened once already.</para>
    /// <para>The single-channel legacy formats (ALPHA, LUMINANCE, LUMINANCE_ALPHA) are deliberately NOT
    /// mapped: their sized equivalents changed which channel the sampler reads, so a promotion would draw
    /// the right image through the wrong swizzle. Those textures stay the engine's.</para></summary>
    private static int Sized(int fmt) => fmt switch
    {
        (int)Gl.RGBA_ => (int)Gl.RGBA8,
        (int)Gl.RGB_ => (int)Gl.RGB8,
        (int)Gl.RG => (int)Gl.RG8,
        (int)Gl.RED => (int)Gl.R8,
        _ => fmt,
    };

    /// <summary>Capped: a format the driver refuses refuses for every texture that uses it, and the first few
    /// lines say which format as well as any number of them would.</summary>
    private static int _fmtDiagLeft = 8;

    /// <summary>How much VRAM one level of the currently bound source texture occupies. A block format
    /// answers directly; anything else is width*height*(sum of the channel widths).</summary>
    private static long LevelBytes(int level)
    {
        int compressed = 0;
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_COMPRESSED, &compressed);
        if (compressed != 0)
        {
            int size = 0;
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_COMPRESSED_IMAGE_SIZE, &size);
            return size > 0 ? size : 0;
        }
        int w = 0, h = 0, r = 0, g = 0, b = 0, a = 0;
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_WIDTH, &w);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_HEIGHT, &h);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_RED_SIZE, &r);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_GREEN_SIZE, &g);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_BLUE_SIZE, &b);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_ALPHA_SIZE, &a);
        return (long)w * h * (r + g + b + a) / 8;
    }

    /// <summary>A 16384-wide texture has 15 mips; the cap only bounds a driver that answers a nonzero width
    /// forever.</summary>
    private const int MaxLevels = 16;

    private const long OneMiB = 1024 * 1024;

    /// <summary>The WORLD edge: the engine names we keyed on are gone and their addresses will be reused, so
    /// every copy is dropped. The 4.6 context is alive here, so the copies are DELETED -- that is the VRAM
    /// coming back, and the reason this is not just a map clear. Also fires on the vanilla toggle and on
    /// table pressure, where the whole set is simply re-copied on the next frames.</summary>
    internal static void Revoke()
    {
        if (_map.Count == 0) return;
        foreach (Entry e in _map.Values) if (e.Owned != 0) _pending.Add(e.Owned);
        _owned.Clear();
        _log?.Info($"OwnedTextures: revoked {_map.Count} owned textures ({_bytes / 1024 / 1024}MiB queued for release)");
        _map.Clear();
        _bytes = 0;
    }

    /// <summary>Delete what <see cref="Revoke"/> retired. Called once per frame from inside the 4.6 bracket,
    /// where the names actually live. Nothing to do is the normal case and costs one count check.
    /// <para>Residency first: #153 makes a handle resident for every texture the lit path draws, and a
    /// resident handle outlives the texture it names. <c>AlloyRenderer.DropHandle</c> looks the name up in
    /// the handle cache and drops it only if we actually made one resident -- asking the driver instead
    /// would MINT a handle for the textures that never had one and then fail to make a non-resident handle
    /// non-resident, one GL_INVALID_OPERATION per copy.</para></summary>
    internal static void Drain()
    {
        if (_pending.Count == 0) return;
        int n = _pending.Count;
        for (int i = 0; i < n; i++)
        {
            uint name = _pending[i];
            AlloyRenderer.DropHandle(name);   // residency first: a resident handle outlives its texture
            if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &name);
        }
        _pending.Clear();
        _log?.Info($"OwnedTextures: released {n} owned textures (retired by the engine so far: {_retired})");
    }


    /// <summary>The GL-NAME edge: the objects went with the context. Names are dropped, nothing is deleted --
    /// deleting into a dead context is what #209 calls the terminal edge.</summary>
    internal static void ContextLost()
    {
        _map.Clear();
        _owned.Clear();
        _pending.Clear();
        _bytes = 0;
        AlloyRenderer.ForgetHandles();   // the names went with the context; nothing to make non-resident
    }

    /// <summary>Off-device: the caching contract, with no GL. An unarmed table is a pass-through, and a
    /// refused copy is remembered as a refusal rather than retried -- the two behaviours that decide whether
    /// a driver without <c>glCopyImageSubData</c> costs one query or one per part per frame.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool wasArmed = _armed;
        var saved = new Dictionary<int, Entry>(_map);
        _map.Clear();

        _armed = false;
        bool ok = Draw(7) == 7 && _map.Count == 0;          // stood down: pass-through, nothing remembered
        _armed = true;
        ok &= Draw(0) == 0 && Draw(-1) == -1 && _map.Count == 0;   // no id is not a texture

        // Copy() refuses without GL entry points, which is exactly the headless case -- so this exercises
        // the refusal path for real rather than by injection.
        ok &= Draw(11) == 11 && _map.Count == 1;             // refused...
        ok &= Draw(11) == 11 && _map.Count == 1 && _refused == 1;   // ...and remembered, not retried
        _map[13] = new Entry(99, 0);
        ok &= Draw(13) == 99;                                // a copy that exists is what the draw samples

        // #220: the ownership question the bindless path asks. A refused entry is NOT owned even though it
        // has a map entry -- getting that backwards is how a handle lands on an engine texture.
        _owned.Add(99);
        ok &= IsOwned(99) && !IsOwned(11) && !IsOwned(0) && !IsOwned(-1);
        // ...and the engine's delete funnel retires the copy made from that name, queueing OUR name (not
        // the engine's) for the deleting Drain does inside the bracket.
        int before = _pending.Count;
        uint* names = stackalloc uint[2] { 13, 4242 };
        Retire(2, names);
        ok &= !_map.ContainsKey(13) && !IsOwned(99)
              && _pending.Count == before + 1 && _pending[before] == 99;
        _pending.Clear(); _owned.Clear();

        _map.Clear(); _refused = 0; _armed = wasArmed;
        foreach (var kv in saved) _map[kv.Key] = kv.Value;
        log.Info(ok ? "OwnedTextures.SelfCheck: passed" : "OwnedTextures.SelfCheck: FAILED");
        return ok;
    }
}
