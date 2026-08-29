using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>
/// #259 (map #68, ADR 0018 M5 strand 1): the textures Alloy DRAWS FROM, which are Alloy's own -- uploaded
/// by Alloy from the engine's DECODED PIXELS, not copied off the engine's finished GL texture.
/// <para>M5's boundary is "the engine reads files and decodes them, Alloy owns everything after that".
/// #211 could not reach the decoded form, so it copied GPU-side (<c>glTexStorage2D</c> +
/// <c>glCopyImageSubData</c>) keyed on the engine's GL name, and inherited that key's ceiling: a texture
/// re-uploaded under a name already copied served the old image until the next world edge. That ceiling is
/// the structural cause of #227, and it is gone -- the copy path is deleted.</para>
/// <para><b>The window.</b> <c>CAurTextureBasic::BuildAndStoreTexture</c> @0x48b320 decodes into
/// <c>basic+0x58</c>, uploads, then <c>FreeImage</c>s. The upload is <c>glImageBasic</c> @0x48bcf0 looping
/// <c>RenderInterface::CreateTexture(tex, sub, cell)</c> once per cell of the <c>numX*numY</c> grid, having
/// bound that cell's GL name. Everything underneath -- <c>ImageGetSubImage</c>, the over-MAX_TEXTURE_SIZE
/// box filter, the format table, <c>glTexImage2D</c>/<c>glCompressedTexImage2D</c>, the mip walk, the
/// single-channel swizzle -- writes into whatever is bound to <c>GL_TEXTURE_2D</c> on unit 0.</para>
/// <para><b>So Alloy's upload IS that code, aimed at Alloy's texture.</b> The hook lets the engine's own
/// call finish, then binds an Alloy-minted name and calls the trampoline a second time. Nothing here
/// reimplements a decode, a sub-image split or a format decision, and the single-channel legacy formats
/// #211 had to refuse (its <c>Sized()</c> promotion changed the sampler swizzle) upload correctly because
/// the engine sets the swizzle on our texture too. It costs a second sub-image + box filter per texture,
/// on the cold load path only.</para>
/// <para><b>Context.</b> The hook fires with the ENGINE's context current, not Alloy's 4.6 bracket -- which
/// is exactly why re-invoking the engine's own upload is safe where hand-rolled 4.x calls would not be. The
/// two contexts are share-listed (<c>GlContextService</c>), so the object is visible to Alloy; the engine's
/// own <c>SwapBuffers</c> between a load and the first draw is the flush that publishes it. Deletes still
/// belong to the bracket -- see <see cref="Drain"/>.</para>
/// <para><b>Identity is content, not a GL name.</b> A resource is keyed on the texture's resolved name
/// (<c>basic+0xa8</c>, the field <c>CAurTexture::Restore</c> re-acquires by) folded with the cell index,
/// read COLD at the upload. The engine GL name survives only as an <see cref="_index"/> entry -- a lookup
/// the hot <see cref="Draw"/> path needs because a material hands out a tid and nothing else, refreshed at
/// BOTH edges that change what a tid means: this upload funnel and the delete funnel. A re-upload mints a
/// NEW owned name and bumps the resource's generation, so a resident bindless handle can never be
/// respecified under the driver and a recycled engine name cannot alias a stale resource.</para>
/// <para><b>What stays borrowed</b> (counted by <see cref="Borrowed"/>, never silently): cube textures
/// (<c>glImageBasic</c> routes those to <c>CreateCubeTexture</c>, which is not this funnel -- they were
/// refused by #211 too), the <c>g_bGeneratePLTOnGPU</c> path (composited on the GPU, there are no decoded
/// pixels to upload), and anything uploaded through <c>DirectCreateTexture</c> -- Ghidra says that is the
/// movie player and <c>Camera::RefreshRenderSubframeResources</c>, neither of which Alloy draws.</para>
/// <para><b>Lifecycle</b> (docs/owned-resource-lifetimes.md), and this table has NO <c>Revoke</c> slot.
/// Content-keyed resources survive a part-table wipe and a <c>/vanilla</c> toggle unchanged, and since they
/// are populated ONLY at the upload funnel, wiping them on an edge the engine does not rebuild across would
/// leave every texture borrowed for the rest of the session. The world edge is the engine's own
/// <see cref="OnDeleteTextures"/>, which QUEUES rather than deletes -- it runs wherever the engine happened
/// to be, which is not the 4.6 context -- and <see cref="Drain"/> does the deleting on the next frame that
/// enters the bracket. <see cref="ContextLost"/> is the GL-name edge: the objects went with the context, so
/// everything is dropped and nothing is deleted.</para>
/// </summary>
internal static unsafe class OwnedTextures
{
    /// <summary>One Alloy-owned texture: the resource behind a content key. <c>Gen</c> counts how many times
    /// the engine has re-uploaded this resource -- the instrument #227 never had.</summary>
    private readonly struct Entry
    {
        internal readonly uint Owned;
        internal readonly long Bytes;
        internal readonly uint Gen;
        internal Entry(uint owned, long bytes, uint gen) { Owned = owned; Bytes = bytes; Gen = gen; }
    }

    /// <summary>Content key -> Alloy's texture. The resource table; nothing in it is keyed on a GL name.</summary>
    private static readonly Dictionary<ulong, Entry> _res = new();
    /// <summary>Engine GL name -> content key. An INDEX, not a resource: it says which resource a tid means
    /// right now, and it is rewritten at the upload edge and erased at the delete edge.</summary>
    private static readonly Dictionary<int, ulong> _index = new();
    /// <summary>The reverse question the bindless path has to ask: is this name a copy of ours? A handle must
    /// never be minted on an engine texture (that IS #220), and a borrowed texture draws from the engine's
    /// id, so "armed" is not the same as "this name is owned".</summary>
    private static readonly HashSet<uint> _owned = new();
    /// <summary>Engine names Alloy draws BORROWED -- distinct tids, so the count is textures rather than
    /// frames. #218's exit bar reads it: "all textures Alloy-created" is false the moment this is non-zero.</summary>
    private static readonly HashSet<int> _borrowed = new();
    /// <summary>Names retired and waiting for a bracket to delete them in. Pre-sized: entries are added from
    /// the engine's delete and upload funnels, which run on the render thread, and a List growth there would
    /// allocate on a hot-adjacent path.</summary>
    private static readonly List<uint> _pending = new(1024);
    private static ExtenderLog? _log;
    private static bool _armed;
    /// <summary>Is the owned-texture path live? Read by the handle cache, which changes its invalidation
    /// discipline entirely on the answer (see <c>AlloyRenderer.WipeHandles</c>).</summary>
    internal static bool Armed => _armed;

    /// <summary>Engine textures Alloy samples BORROWED, distinct by GL name. Not a refusal any more --
    /// nothing is attempted and declined; these are the names that never came through the upload funnel at
    /// all (see the class remarks for which, and why).</summary>
    internal static int Borrowed => _borrowed.Count;

    private static long _bytes, _peakBytes;

    /// <summary>The high-water figure the <c>/knobs</c> residency line reports, in MiB (#259).</summary>
    internal static long PeakMib => _peakBytes / OneMiB;
    private static int _uploads, _reuploads, _peakCount;

    /// <summary>Wired once from <see cref="AlloyRenderer"/>'s init, before any frame runs.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _hash = (delegate* unmanaged<IntPtr, short, int*, int>)Native.Sym(SymbolMap.AurTextureCalcHash);
        bool wanted = Settings.Bool("ownedtex", true);
        _armed = wanted && _hash != null;
        log.Info(_armed
            ? "OwnedTextures: armed -- Alloy uploads from the engine's decoded pixels (ownedtex=0 to draw from engine ids)"
            : wanted
              ? "OwnedTextures: stood down -- AurTextureCalculateHashID unresolved, so there is no identity to key resources on and owned draws sample engine texture ids"
              : "OwnedTextures: stood down (ownedtex=0) -- owned draws sample engine texture ids");
    }

    /// <summary>Chain the two engine funnels this table lives on: the UPLOAD (#259, where decoded pixels
    /// exist) and the DELETE (#220, the area-transition edge that fires no other edge Alloy watches).</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!_armed) return;
        _origCreate = (delegate* unmanaged<IntPtr, byte, int, void>)hooks.Install(
            SymbolMap.RiCreateTexture, (IntPtr)(delegate* unmanaged<IntPtr, byte, int, void>)&OnCreateTexture);
        log.Info(_origCreate != null
            ? "OwnedTextures: watching RenderInterface::CreateTexture -- Alloy uploads every engine texture from its decoded pixels"
            : "OwnedTextures: CreateTexture not installed -- every texture is BORROWED (owned draws sample engine ids)");

        _origDelete = (delegate* unmanaged<uint, uint*, void>)hooks.Install(
            SymbolMap.RiDeleteTextures, (IntPtr)(delegate* unmanaged<uint, uint*, void>)&OnDeleteTextures);
        log.Info(_origDelete != null
            ? "OwnedTextures: watching RenderInterface::DeleteTextures -- a tid stops resolving the moment it stops meaning its resource"
            : "OwnedTextures: DeleteTextures not installed -- a recycled engine name will resolve the previous area's resource");
    }

    private static delegate* unmanaged<IntPtr, byte, int, void> _origCreate;
    private static delegate* unmanaged<uint, uint*, void> _origDelete;
    private static long _retired;

    /// <summary><c>CAurTextureBasic+0x50</c>: <c>uint*</c>, the <c>numX*numY</c> GL names
    /// <c>glImageBasic</c> @0x48bcf0 generates and binds one per cell.</summary>
    private const int OffCellNames = 0x50;
    /// <summary><c>CAurTextureBasic+0xa8</c>: <c>char[0x20]</c>, the resolved texture name
    /// <c>LoadImage</c> @0x487980 fills and <c>CAurTexture::Restore</c> @0x48e300 re-acquires by. Stable
    /// across a delete/rebuild, which is what makes it an identity.</summary>
    private const int OffName = 0xa8;
    /// <summary><c>CAurTextureBasic+0x48</c> / <c>+0xd4</c>: the PLT scheme array (<c>int*</c>) and its
    /// <c>short</c> count -- the other half of the engine's own texture identity.</summary>
    private const int OffSchemes = 0x48, OffSchemeCount = 0xd4;
    /// <summary><c>CAurTextureBasic+0xca</c> / <c>+0xcc</c>: the <c>short</c> atlas grid the engine
    /// de-atlases into one GL texture per cell.</summary>
    private const int OffNumX = 0xca, OffNumY = 0xcc;

    /// <summary>The engine is about to upload cell <paramref name="cell"/> of <paramref name="tex"/> into the
    /// GL name it has bound. Let that happen, then do it again into a texture of ours.
    /// <para>Cold -- once per texture per (re)build, never per frame -- so the allocations a Dictionary
    /// insert makes are not on a hot path. Runs in the ENGINE's context; see the class remarks.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnCreateTexture(IntPtr tex, byte useSubImage, int cell)
    {
        if (_origCreate != null) _origCreate(tex, useSubImage, cell);
        Adopt(tex, cell);
    }

    /// <summary>The upload handler's body, split out so <see cref="SelfCheck"/> can exercise it -- an
    /// <c>UnmanagedCallersOnly</c> method cannot be called from managed code.</summary>
    private static void Adopt(IntPtr tex, int cell)
    {
        if (!_armed || _origCreate == null) return;
        // The BASIC vtable specifically: every field below belongs to the subclass, and #138/#221 say a
        // texture pointer is only ever as good as its vtable.
        if (!AurTex.IsBasic(tex)) return;

        uint* cells = *(uint**)((byte*)tex + OffCellNames);
        int n = *(short*)((byte*)tex + OffNumX) * *(short*)((byte*)tex + OffNumY);
        if (cells == null || cell < 0 || cell >= n) return;
        int etid = (int)cells[cell];
        if (etid <= 0) return;

        // ponytail: before the first present there is no 4.6 context and no entry points, so a texture
        // uploaded that early stays borrowed for the session -- nothing re-drives this funnel for a texture
        // the engine already holds. The window is the main menu, which Alloy does not draw, and the count
        // says so out loud rather than hiding it. Upgrade path: re-drive AurTextureRefreshAll @0x48f050 from
        // the engine's context once the bracket exists.
        if (Gl.GenTextures == null || Gl.BindTexture == null || Gl.GetIntegerv == null)
        {
            _borrowed.Add(etid);
            return;
        }

        ulong key = Key(tex, cell);
        uint dst;
        Gl.GenTextures(1, &dst);
        if (dst == 0) { _borrowed.Add(etid); return; }

        int saved; Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &saved);
        DrainErrors();   // the verdict below is ours to read, so nobody else's error may be sitting here
        // Raw glBindTexture, NOT AurTextureBindTID: the engine caches what it thinks is bound, and routing
        // our name through its wrapper would leave that cache naming a texture the engine does not own.
        Gl.BindTexture(Gl.TEXTURE_2D, dst);
        // ...and always the ALLOCATING form. `true` is the glTexSubImage2D path, which assumes storage a
        // freshly generated name does not have.
        _origCreate(tex, 0, cell);
        uint err = Gl.GetError();          // read BEFORE Measure, whose own queries would mask it
        long bytes = err == 0 ? Measure() : 0;
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)saved);

        if (err != 0)
        {
            if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dst);
            _borrowed.Add(etid);
            if (_diagLeft > 0) { _diagLeft--; _log?.Info($"OwnedTextures: tid {etid} borrowed -- the engine's upload into our name gave 0x{err:x}"); }
            return;
        }

        Replace(key, etid, dst, bytes);
        HighWater();
    }

    /// <summary>The residency line, every MiB rather than every texture -- the count climbs on every insert,
    /// so testing it per texture fired a few hundred interpolated lines while an area loaded.
    /// <para>The resource count runs ABOVE "textures in the area": an animated texture cycles one resource
    /// per animation frame, and the engine de-atlases a numX*numY grid into one texture per cell. Correct
    /// and bounded, not a leak.</para></summary>
    private static void HighWater()
    {
        if (_bytes <= _peakBytes + OneMiB) return;
        _peakBytes = _bytes;
        _log?.Info($"OwnedTextures: [vram] high-water {_peakBytes / OneMiB}MiB across {_peakCount} " +
                   $"owned textures (uploads {_uploads}, re-uploads {_reuploads}, borrowed {_borrowed.Count})");
    }

    /// <summary>Stop drawing from a texture and queue it for the bracket to delete. Shared by the delete
    /// funnel and the re-upload path, which retire for different reasons and by the same mechanics.</summary>
    private static void Discard(Entry e)
    {
        if (e.Owned == 0) return;
        _owned.Remove(e.Owned);
        _pending.Add(e.Owned);
        _bytes -= e.Bytes;
    }

    /// <summary>Adopt a freshly uploaded texture as the resource behind <paramref name="key"/>, retiring
    /// whatever held that key before. Split out from <see cref="Adopt"/>, whose other half is GL, so
    /// <see cref="SelfCheck"/> can assert the table mutation rather than a copy of it.</summary>
    private static void Replace(ulong key, int etid, uint owned, long bytes)
    {
        uint gen = 0;
        if (_res.TryGetValue(key, out Entry old))
        {
            gen = old.Gen + 1;
            Discard(old);
            _reuploads++;
        }
        _res[key] = new Entry(owned, bytes, gen);
        _owned.Add(owned);
        _index[etid] = key;
        _borrowed.Remove(etid);
        _uploads++;
        _bytes += bytes;
        if (_res.Count > _peakCount) _peakCount = _res.Count;
    }

    /// <summary>A resource's identity: the ENGINE's own hash for the texture, folded with the cell index.
    /// <c>CAurTextureBasic::CalculateHashID</c> @0x485890 hashes the resolved name at <c>+0xa8</c> together
    /// with the PLT scheme array at <c>+0x48</c>/<c>+0xd4</c>, and <c>AurTextureFind</c> looks textures up by
    /// it -- so it is exactly "which texture is this" as the engine itself answers it, and two PLT skins
    /// sharing one resref are two resources rather than one. The cell occupies the low half whole, so an
    /// atlas cell can never collide with another texture's hash.
    /// <para>Cold: once per cell per (re)upload, and it costs one engine call rather than a string walk.</para></summary>
    private static ulong Key(IntPtr tex, int cell) => ((ulong)(uint)_hash(tex + OffName,
        *(short*)((byte*)tex + OffSchemeCount), *(int**)((byte*)tex + OffSchemes)) << 32) | (uint)cell;

    private static delegate* unmanaged<IntPtr, short, int*, int> _hash;

    /// <summary>How much VRAM the texture currently bound to <c>GL_TEXTURE_2D</c> occupies, over every level
    /// that has storage. A block format answers directly; anything else is width*height*(sum of the channel
    /// widths). Accounting only -- a driver that answers nothing costs a zero, not a refusal.</summary>
    private static long Measure()
    {
        if (Gl.GetTexLevelParameteriv == null) return 0;
        long total = 0;
        for (int level = 0; level < MaxLevels; level++)
        {
            int w = 0, h = 0;
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_WIDTH, &w);
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_HEIGHT, &h);
            if (w <= 0 || h <= 0) break;
            int compressed = 0;
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_COMPRESSED, &compressed);
            if (compressed != 0)
            {
                int size = 0;
                Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_COMPRESSED_IMAGE_SIZE, &size);
                total += size > 0 ? size : 0;
                continue;
            }
            int r = 0, g = 0, b = 0, a = 0;
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_RED_SIZE, &r);
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_GREEN_SIZE, &g);
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_BLUE_SIZE, &b);
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, level, Gl.TEXTURE_ALPHA_SIZE, &a);
            total += (long)w * h * (r + g + b + a) / 8;
        }
        return total;
    }

    /// <summary>Empty the error queue without trusting it to end. A context the driver has given up on
    /// answers an error forever, and an unbounded drain there is a hang rather than a diagnostic.</summary>
    private static void DrainErrors() { for (int i = 0; i < 8 && Gl.GetError() != 0; i++) { } }

    /// <summary>Capped: a driver that refuses one upload refuses every texture like it, and the first few
    /// lines say which as well as any number of them would.</summary>
    private static int _diagLeft = 8;

    /// <summary>A 16384-wide texture has 15 mips; the cap only bounds a driver that answers a nonzero width
    /// forever.</summary>
    private const int MaxLevels = 16;

    private const long OneMiB = 1024 * 1024;

    /// <summary>The engine is deleting these GL names. This is THE world edge for a texture: a resource the
    /// engine no longer holds is one Alloy can never be handed a tid for again, so the index entry goes and
    /// the texture behind it is queued. An area transition fires this and nothing else Alloy watches (#220
    /// measured one part-table wipe in an entire session); a rebuild re-adopts at the upload funnel.
    /// <para>Issues no GL: this runs wherever the engine happened to be, which is not the 4.6 context.
    /// <see cref="Drain"/> does the deleting.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnDeleteTextures(uint n, uint* names)
    {
        Retire(n, names);
        if (_origDelete != null) _origDelete(n, names);
    }

    /// <summary>The delete handler's body, split out so <see cref="SelfCheck"/> can exercise it.</summary>
    private static void Retire(uint n, uint* names)
    {
        if (names == null) return;
        for (uint i = 0; i < n; i++)
        {
            int name = (int)names[i];
            _borrowed.Remove(name);
            if (!_index.TryGetValue(name, out ulong key)) continue;
            _index.Remove(name);
            _retired++;
            if (!_res.TryGetValue(key, out Entry e)) continue;
            _res.Remove(key);
            Discard(e);
        }
    }

    /// <summary>#220: is this name one of ours? The bindless path mints a handle only when it is -- a handle
    /// on an engine texture is a handle on an object the engine may delete or re-upload under us, which is
    /// the whole of #220.</summary>
    internal static bool IsOwned(int name) => name > 0 && _owned.Contains((uint)name);

    /// <summary>The name an owned draw should sample for this engine texture id: Alloy's, or
    /// <paramref name="engineTid"/> itself when the table is stood down or the texture never came through the
    /// upload funnel. Hot -- called per Alloy part per frame -- and in the normal case two dictionary probes
    /// and nothing else. Issues no GL, so it is not bracket-bound.
    /// <para>The one allocation it can make is the borrowed set growing, which happens once per DISTINCT
    /// engine name Alloy never adopted and never again for that name. Zero of them is the #218 bar.</para></summary>
    internal static int Draw(int engineTid)
    {
        if (!_armed || engineTid <= 0) return engineTid;
        if (_index.TryGetValue(engineTid, out ulong key) && _res.TryGetValue(key, out Entry e))
            return (int)e.Owned;
        _borrowed.Add(engineTid);
        return engineTid;
    }

    /// <summary>Delete what <see cref="Revoke"/> and the re-upload path retired. Called once per frame from
    /// inside the 4.6 bracket, where the names actually live. Nothing to do is the normal case and costs one
    /// count check.
    /// <para>Residency first: #153 makes a handle resident for every texture the lit path draws, and a
    /// resident handle outlives the texture it names. <c>AlloyRenderer.DropHandle</c> looks the name up in
    /// the handle cache and drops it only if we actually made one resident -- asking the driver instead
    /// would MINT a handle for the textures that never had one and then fail to make a non-resident handle
    /// non-resident, one GL_INVALID_OPERATION per texture.</para></summary>
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
        _log?.Info($"OwnedTextures: released {n} owned textures (engine names retired so far: {_retired})");
    }

    /// <summary>The GL-NAME edge: the objects went with the context. Names are dropped, nothing is deleted --
    /// deleting into a dead context is what #209 calls the terminal edge.</summary>
    internal static void ContextLost()
    {
        _res.Clear();
        _index.Clear();
        _owned.Clear();
        _borrowed.Clear();
        _pending.Clear();
        _bytes = 0;
        AlloyRenderer.ForgetHandles();   // the names went with the context; nothing to make non-resident
    }

    /// <summary>Off-device: the residency contract, with no GL and no engine. What it pins is everything
    /// #259 changed about the keying -- that identity is content, that the engine's GL name is an index
    /// rewritten at both of its edges, and that a re-upload retires the old texture instead of respecifying
    /// it. The upload itself needs a client; #227 can only be re-tested live.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool wasArmed = _armed;
        long savedRetired = _retired;
        var savedRes = new Dictionary<ulong, Entry>(_res);
        var savedIdx = new Dictionary<int, ulong>(_index);
        var savedOwned = new HashSet<uint>(_owned);
        var savedBorrowed = new HashSet<int>(_borrowed);
        int savedPending = _pending.Count;
        _res.Clear(); _index.Clear(); _owned.Clear(); _borrowed.Clear();

        _armed = false;
        bool ok = Draw(7) == 7 && _borrowed.Count == 0;             // stood down: pass-through, nothing noticed
        _armed = true;
        ok &= Draw(0) == 0 && Draw(-1) == -1 && _borrowed.Count == 0;   // no id is not a texture

        // Never uploaded through our funnel -> BORROWED, counted once per distinct name rather than per frame.
        ok &= Draw(11) == 11 && Draw(11) == 11 && Borrowed == 1;

        // The content key is the identity; the engine name is an index onto it. Two names may resolve one
        // resource, and one name resolves a different resource after a re-upload. The key itself is the
        // engine's hash, so it needs a client -- what is asserted here is the table it drives.
        const ulong k0 = 0x5AF300000000UL, k1 = k0 | 1;
        Replace(k0, 13, 99, 4096);
        _index[14] = k0;                                             // a second live name onto one resource
        ok &= Draw(13) == 99 && Draw(14) == 99 && Borrowed == 1;

        // #220: the ownership question the bindless path asks. A borrowed name is NOT owned.
        ok &= IsOwned(99) && !IsOwned(11) && !IsOwned(13) && !IsOwned(0) && !IsOwned(-1);

        // The re-upload path -- a NEW texture rather than a respecified one, because a resident bindless
        // handle makes respecifying storage undefined -- with the old one queued and the generation bumped.
        Replace(k0, 14, 100, 4096);
        ok &= Draw(14) == 100 && Draw(13) == 100 && !IsOwned(99) && IsOwned(100)
              && _res[k0].Gen == 1 && _pending.Count == savedPending + 1 && _pending[savedPending] == 99;

        // The delete funnel is the world edge: index entry gone, resource queued, borrowed mark cleared
        // (a recycled name is a new question), and the tid resolves nothing until an upload re-adopts it.
        uint* dead = stackalloc uint[3] { 13, 14, 11 };
        Retire(3, dead);
        ok &= !_index.ContainsKey(13) && !_index.ContainsKey(14) && !_res.ContainsKey(k0)
              && !IsOwned(100) && _pending.Count == savedPending + 2 && Borrowed == 0;
        ok &= Draw(13) == 13 && Draw(14) == 14 && Borrowed == 2 && !_res.ContainsKey(k1);
        _pending.RemoveRange(savedPending, 2);

        _res.Clear(); _index.Clear(); _owned.Clear(); _borrowed.Clear();
        foreach (var kv in savedRes) _res[kv.Key] = kv.Value;
        foreach (var kv in savedIdx) _index[kv.Key] = kv.Value;
        foreach (uint o in savedOwned) _owned.Add(o);
        foreach (int b in savedBorrowed) _borrowed.Add(b);
        _armed = wasArmed;
        _retired = savedRetired;   // the check's own synthetic deletes are not a session's
        log.Info(ok ? "OwnedTextures.SelfCheck: passed" : "OwnedTextures.SelfCheck: FAILED");
        return ok;
    }
}
