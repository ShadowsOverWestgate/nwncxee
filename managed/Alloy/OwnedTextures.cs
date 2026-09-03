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
/// Content-keyed resources survive a part-table wipe unchanged, and since they
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
    /// <summary>#262 follow-up: the engine's GPU-PLT switch, held at zero so PLT textures come through the
    /// upload funnel as decoded pixels. Null when the symbol did not resolve, which leaves PLT content
    /// exactly where it was -- borrowed.</summary>
    private static int* _pltOnGpu;
    private static bool _pltWanted;
    private static long _pltForced;

    /// <summary>Wired once from <see cref="AlloyRenderer"/>'s init, before any frame runs.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _hash = (delegate* unmanaged<IntPtr, short, int*, int>)Native.Sym(SymbolMap.AurTextureCalcHash);
        // #284: resolved whatever `ownedtex` says. A texture with no GL id is a hole in the owned draw under
        // either key -- the retry is about the ENGINE's build having never run, not about who owns the copy.
        _buildAndStore = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.AurTextureBuildAndStore);
        _nullTex = (IntPtr*)Native.Sym(SymbolMap.AurTextureNullPtr);
        if (_nullTex == null)
            log.Info("OwnedTextures: CAurTextureBasic::m_pNullTexture unresolved -- a material naming the "
                   + "engine's NULL texture cannot be told from an unbuilt one, so it will be retried and "
                   + "written off as spent rather than named (#284)");
        if (_buildAndStore == null)
            log.Info("OwnedTextures: CAurTexture::BuildAndStore unresolved -- a texture whose build lost its "
                   + "race with the resource loader stays without a GL id, and its part is not drawn (#284)");
        bool wanted = Settings.Bool("ownedtex", true);
        _armed = wanted && _hash != null;
        // ADR 0013 config bucket: a capability decision about how the ENGINE loads content, not a look dial.
        // Costs a CPU composite per character texture at load -- which is what the engine did for years --
        // and buys every PLT-skinned creature a place in the owned set.
        _pltWanted = Settings.Bool("pltcpu", true);
        // ADR 0013 config bucket: a capability decision (spend load-time CPU to shrink VRAM 3-4x on the
        // uncompressed share the #265 census measured at ~2/3), not a look dial. Settings read FIRST so the
        // knob prints in /knobs even when ownedtex=0 stands the table down.
        _bcnWanted = Settings.Bool("bcn", true);
        _bcn = _bcnWanted && _armed;
        if (_armed)
            log.Info(_bcn
                ? "OwnedTextures: BCn transcode armed -- uncompressed uploads are re-encoded BC1/BC3 (bcn=0 in sowx.cfg to keep them raw)"
                : "OwnedTextures: BCn transcode off (bcn=0) -- uncompressed uploads stay raw");
        if (_armed && _pltWanted)
        {
            _pltOnGpu = (int*)Native.Sym(SymbolMap.PltOnGpu);
            log.Info(_pltOnGpu != null
                ? "OwnedTextures: PLT compositing forced to the CPU -- player-character and tinted-creature "
                + "textures reach the upload funnel and become ownable (pltcpu=0 in sowx.cfg leaves them borrowed)"
                : "OwnedTextures: g_bGeneratePLTOnGPU unresolved -- PLT textures stay GPU-composited and BORROWED, "
                + "so under bindless every lit part wearing one is NOT DRAWN (#285: nothing draws what Alloy declines)");
            KeepPltOnCpu();
        }
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

        int savedBind; Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &savedBind);
        DrainErrors();   // the verdict below is ours to read, so nobody else's error may be sitting here
        // Raw glBindTexture, NOT AurTextureBindTID: the engine caches what it thinks is bound, and routing
        // our name through its wrapper would leave that cache naming a texture the engine does not own.
        Gl.BindTexture(Gl.TEXTURE_2D, dst);
        // ...and always the ALLOCATING form. `true` is the glTexSubImage2D path, which assumes storage a
        // freshly generated name does not have.
        _origCreate(tex, 0, cell);
        uint err = Gl.GetError();          // read BEFORE Measure, whose own queries would mask it
        long bytes = err == 0 ? Measure() : 0;
        long transSaved = 0;
        if (err == 0 && _bcn && *(int*)((byte*)tex + OffCompressedSize) == 0)
        {
            long after = Transcode(tex, cell);
            if (after == Broken)
            {
                // Both the compressed respecify AND the raw recovery failed: the texture is garbage and
                // must not enter the owned table. Borrow the engine's copy instead.
                Gl.BindTexture(Gl.TEXTURE_2D, (uint)savedBind);
                if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dst);
                _borrowed.Add(etid);
                if (_diagLeft > 0) { _diagLeft--; _log?.Info($"OwnedTextures: tid {etid} borrowed -- transcode failed and its raw recovery failed too"); }
                return;
            }
            // Record the POST state unconditionally: the texture is already compressed, so a driver that
            // answers 0 for the compressed size costs a zero in the ledger, not a raw-sized lie in it.
            if (after >= 0) { transSaved = bytes > after ? bytes - after : 0; bytes = after; }
        }
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)savedBind);

        if (err != 0)
        {
            if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dst);
            _borrowed.Add(etid);
            if (_diagLeft > 0) { _diagLeft--; _log?.Info($"OwnedTextures: tid {etid} borrowed -- the engine's upload into our name gave 0x{err:x}"); }
            return;
        }

        Replace(key, etid, dst, bytes);
        Census(tex, bytes, transSaved, n, cell);
        HighWater();
    }

    /// <summary>#265: the transcode. The engine's raw upload into our texture just finished (it is still
    /// bound); read each level back, encode it BC1 (no alpha) or BC3, and respecify the level compressed.
    /// The readback inherits everything the engine's path already did -- the de-atlas, the
    /// over-MAX_TEXTURE_SIZE box filter, the mip walk -- which is why nothing here parses an image.
    /// <para>Cold, and the readback's pipeline sync is against an upload this call just issued.
    /// Returns the compressed byte total, or -1 for a refusal: single-channel levels (their sampler swizzle
    /// is the engine's contract with the RAW channel layout), tiny textures, or a driver that errored -- in
    /// which case the engine's upload is RE-RUN so the texture is never left half-respecified.</para>
    /// <para>ponytail: readback-encode-reupload costs one extra bus round trip per texture at load; encoding
    /// straight from the decoded pixels at basic+0x58 would skip it but must reimplement the de-atlas and
    /// the mip chain. Upgrade if area loads measurably slow.</para></summary>
    /// <summary>#336: smallest side worth transcoding. A BCn block IS 4x4 and carries two endpoint colours
    /// for its sixteen texels, so a 4x4 image is ONE block -- every texel in it averaged down to a line
    /// between two colours. That is not a rounding error on a small texture, it is the texture: the client's
    /// GUI markers pick their colour out of tiny swatch sheets (<c>gui_rgbc</c>, 4x4, the walk-to marker's
    /// green among them), and BC1 turned that sheet into a uniform 127,127,127 -- grey chevrons on the
    /// ground where vanilla draws green ones. The old floor was 4, which admitted exactly the case it was
    /// meant to exclude.
    /// <para>16 rather than 8 because the saving is what stops mattering first: a 16x16 RGBA level is 1 KiB,
    /// so everything under this floor put together cannot move the number #265 measured, while every one of
    /// them is a sheet whose texels were each chosen on purpose.</para></summary>
    private const int TranscodeFloor = 16;

    /// <summary>Re-encode the bound texture's whole mip chain to BC1/BC3 in place. Every level is read and
    /// encoded before ANY of it is uploaded, because respecifying level 0 with a different internal format
    /// reallocates the texture object: on a driver that backs an object with one miptree the other levels'
    /// contents do not survive that, so a read interleaved with the uploads returns undefined pixels, which
    /// encode and upload cleanly with no GL error to catch them. Inferred from the failure, not from driver
    /// source -- what was observed is an AMD/Mesa client whose textures were right at the base level and
    /// wrong at every mip below it (correct up close, corrupt at distance, snapping right as the camera
    /// closed in) while the same build on NVIDIA, which allocates levels independently, showed nothing.
    /// <c>bcn=0</c> cleared it. Returns the new size, <see cref="Broken"/>, or -1 to leave the texture raw.
    /// </summary>
    private static long Transcode(IntPtr tex, int cell)
    {
        if (Gl.GetTexImage == null || Gl.CompressedTexImage2D == null || Gl.PixelStorei == null
            || Gl.GetTexLevelParameteriv == null || Gl.BindBuffer == null) return -1;
        int w0 = 0, h0 = 0, green = 0;
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_WIDTH, &w0);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_HEIGHT, &h0);
        if (w0 < TranscodeFloor || h0 < TranscodeFloor) return -1;
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_GREEN_SIZE, &green);
        if (green == 0) return -1;

        if (_readback == null || _readback.Length < w0 * h0 * 4) _readback = new byte[w0 * h0 * 4];

        // Every level's dimensions up front, and scratch for the WHOLE chain at the BC3 block size -- BC1
        // needs half, so the encode below can never outrun this no matter which the texels pick. A ratio
        // over level 0 would not do: once the short axis bottoms out at one block the tail stops quartering.
        int* lw = stackalloc int[MaxLevels], lh = stackalloc int[MaxLevels], loff = stackalloc int[MaxLevels];
        int levels = 0;
        long blkMax = 0;
        for (; levels < MaxLevels; levels++)
        {
            int w = 0, h = 0;
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, levels, Gl.TEXTURE_WIDTH, &w);
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, levels, Gl.TEXTURE_HEIGHT, &h);
            if (w <= 0 || h <= 0) break;
            lw[levels] = w; lh[levels] = h;
            blkMax += BcnEncode.LevelSize(w, h, true);
        }
        if (levels == 0) return -1;
        if (_blocks == null || _blocks.Length < blkMax) _blocks = new byte[blkMax];

        // The whole pixel-store surface the readback and the compressed upload depend on, not just the
        // alignment: a stale PACK_ROW_LENGTH strides the readback past the buffer, and a bound pack/unpack
        // PBO silently turns the client pointer into a buffer offset.
        int svAlign, svRow, svSkipR, svSkipP, svPackBuf, svUnpackBuf;
        Gl.GetIntegerv(Gl.PACK_ALIGNMENT, &svAlign);
        Gl.GetIntegerv(Gl.PACK_ROW_LENGTH, &svRow);
        Gl.GetIntegerv(Gl.PACK_SKIP_ROWS, &svSkipR);
        Gl.GetIntegerv(Gl.PACK_SKIP_PIXELS, &svSkipP);
        Gl.GetIntegerv(Gl.PIXEL_PACK_BUFFER_BINDING, &svPackBuf);
        Gl.GetIntegerv(Gl.PIXEL_UNPACK_BUFFER_BINDING, &svUnpackBuf);
        Gl.PixelStorei(Gl.PACK_ALIGNMENT, 1);
        Gl.PixelStorei(Gl.PACK_ROW_LENGTH, 0);
        Gl.PixelStorei(Gl.PACK_SKIP_ROWS, 0);
        Gl.PixelStorei(Gl.PACK_SKIP_PIXELS, 0);
        if (svPackBuf != 0) Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, 0);
        if (svUnpackBuf != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0);

        fixed (byte* read = _readback)
        fixed (byte* blocks = _blocks)
        {
            // BC1 vs BC3 from the TEXELS, not the declared format -- the repo's standing lesson is that
            // format flags lie about alpha (#126's ivy). A 32-bit source with constant-255 alpha is BC1 at
            // half the size, and an opaque level 0 cannot grow transparency in the mips derived from it.
            Gl.GetTexImage(Gl.TEXTURE_2D, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, read);
            bool bc3 = false;
            for (long i = 3, end = (long)w0 * h0 * 4; i < end; i += 4)
                if (read[i] != 255) { bc3 = true; break; }
            uint fmt = bc3 ? Gl.COMPRESSED_RGBA_S3TC_DXT5 : Gl.COMPRESSED_RGB_S3TC_DXT1;

            // read every level BEFORE respecifying any -- redefining level 0 reallocates the object
            int off = 0;
            for (int i = 0; i < levels; i++)
            {
                if (i > 0) Gl.GetTexImage(Gl.TEXTURE_2D, i, Gl.RGBA, Gl.UNSIGNED_BYTE, read);
                BcnEncode.EncodeLevel(read, lw[i], lh[i], bc3, blocks + off);
                loff[i] = off; off += BcnEncode.LevelSize(lw[i], lh[i], bc3);
            }
            for (int i = 0; i < levels; i++)
                Gl.CompressedTexImage2D(Gl.TEXTURE_2D, i, fmt, lw[i], lh[i], 0,
                    BcnEncode.LevelSize(lw[i], lh[i], bc3), blocks + loff[i]);
        }

        Gl.PixelStorei(Gl.PACK_ALIGNMENT, svAlign);
        Gl.PixelStorei(Gl.PACK_ROW_LENGTH, svRow);
        Gl.PixelStorei(Gl.PACK_SKIP_ROWS, svSkipR);
        Gl.PixelStorei(Gl.PACK_SKIP_PIXELS, svSkipP);
        if (svPackBuf != 0) Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, (uint)svPackBuf);
        if (svUnpackBuf != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, (uint)svUnpackBuf);

        uint err = Gl.GetError();
        if (err != 0)
        {
            // Never leave a half-respecified texture behind: the engine's own upload restores every level
            // raw -- and that recovery is itself VERIFIED, because the moment both uploads fail (VRAM
            // pressure hits both) the texture is garbage and must not be published as owned.
            _origCreate(tex, 0, cell);
            uint recoveryErr = Gl.GetError();
            if (++_bcnFails >= BcnFailCap)
            {
                _bcn = false;
                _log?.Info($"OwnedTextures: BCn transcode DISABLED -- {_bcnFails} driver refusals (last 0x{err:x}); the uncompressed path stays raw until the next context");
            }
            return recoveryErr != 0 ? Broken : -1;
        }
        return Measure();
    }

    /// <summary>Transcode's "the texture is garbage" verdict: the compressed respecify failed AND the raw
    /// recovery failed, so <see cref="Adopt"/> must route to the borrow path instead of publishing it.</summary>
    private const long Broken = -2;

    /// <summary>Level-readback and block scratch, grown to the largest texture seen and reused -- the upload
    /// funnel is serialized on the engine's GL thread, so one of each is enough.</summary>
    private static byte[]? _readback, _blocks;
    private static bool _bcn, _bcnWanted;
    private static int _bcnFails;
    /// <summary>A driver that refuses one S3TC upload refuses them all; three strikes covers a transient.</summary>
    private const int BcnFailCap = 3;

    /// <summary>#265's number, measured before anything is built: per area, how many textures (and how many
    /// bytes) come down the engine's UNCOMPRESSED upload path -- <c>basic+0x70</c> == 0, the
    /// <c>GetCompressedSize</c> @0x484b00 verdict that picks <c>glTexImage2D</c> over
    /// <c>glCompressedTexImage2D</c> -- versus the already-BCn path. The uncompressed share is the whole
    /// BCn-transcode prize; the issue forbids building the transcode without this line.</summary>
    private const int OffCompressedSize = 0x70;
    private static int _censusRaw, _censusBcn, _censusTrans;
    private static long _censusRawBytes, _censusBcnBytes, _censusSaved;
    /// <summary>#266's number, measured before anything is built: per area, how many textures are
    /// <c>numX*numY &gt; 1</c> grids the engine de-atlased into one GL texture per cell (the array-texture
    /// prize), how many cells they expand into, and their bytes. The issue forbids building the
    /// array-texture path without this line.</summary>
    private static int _censusGrid, _censusGridCells;
    private static long _censusGridBytes;
    private static int _censusLastUpload;
    private static bool _censusReported;
    /// <summary>Frames of upload silence that count as "the area settled" -- ~5s at 60fps, same reasoning as
    /// <see cref="RebuildRearm"/>.</summary>
    private const int CensusSettle = 300;

    private static void Census(IntPtr tex, long bytes, long transSaved, int gridN, int cell)
        => CensusTally(*(int*)((byte*)tex + OffCompressedSize) > 0, bytes, transSaved, gridN, cell);

    /// <summary>The tally half, split from the field read so <see cref="SelfCheck"/> can exercise the
    /// window: report-then-upload resets, silence settles, one line per window. A texture is charged to the
    /// PATH it came down (+0x70), with its transcode savings alongside -- so once #265's transcode is on, the
    /// raw share reads as the post-transcode residual and the saved term says what the transcode took off.</summary>
    private static void CensusTally(bool bcn, long bytes, long transSaved, int gridN = 1, int cell = 0)
    {
        if (_censusReported)   // first upload after a settled report opens the next area's window
        {
            _censusRaw = _censusBcn = _censusTrans = 0;
            _censusRawBytes = _censusBcnBytes = _censusSaved = 0;
            _censusGrid = _censusGridCells = 0; _censusGridBytes = 0;
            _censusReported = false;
        }
        if (bcn) { _censusBcn++; _censusBcnBytes += bytes; }
        else { _censusRaw++; _censusRawBytes += bytes; }
        if (transSaved > 0) { _censusTrans++; _censusSaved += transSaved; }
        if (gridN > 1)
        {
            // Cell 0 counts the texture; every cell counts toward the object/byte total the de-atlas costs.
            if (cell == 0) { _censusGrid++; _censusGridCells += gridN; }
            _censusGridBytes += bytes;
        }
        _censusLastUpload = _dripFrame;
    }

    /// <summary>Emit the settle line once per upload window. Rides the <see cref="DripRebuilds"/> anchor:
    /// once a frame, engine context, no GL.</summary>
    private static void CensusSettleCheck()
    {
        if (_censusReported || _censusRaw + _censusBcn == 0 || _dripFrame - _censusLastUpload < CensusSettle)
            return;
        _censusReported = true;
        int total = _censusRaw + _censusBcn;
        long totalBytes = _censusRawBytes + _censusBcnBytes;
        _log?.Info($"OwnedTextures: [census] area settled -- {_censusRaw}/{total} textures "
                 + $"({100 * _censusRaw / total}%) came down the UNCOMPRESSED path (basic+0x70==0), "
                 + $"{_censusRawBytes / OneMiB}MiB of {totalBytes / OneMiB}MiB resident "
                 + $"({(totalBytes == 0 ? 0 : 100 * _censusRawBytes / totalBytes)}% of VRAM)"
                 + (_censusTrans == 0 ? " -- #265: this share is the BCn-transcode prize"
                    : $"; #265 transcoded {_censusTrans} of them to BCn, saving {_censusSaved / OneMiB}MiB"));
        _log?.Info($"OwnedTextures: [census] #266 grids -- {_censusGrid} textures are numX*numY>1, "
                 + $"de-atlased into {_censusGridCells} GL textures ({_censusGridBytes / OneMiB}MiB)"
                 + (_censusGrid == 0 ? "; the array-texture path buys nothing here" : ""));
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
                   $"owned textures (uploads {_uploads}, re-uploads {_reuploads}, borrowed {_borrowed.Count}" +
                   (_pltOnGpu == null ? _pltWanted ? ", GPU-PLT NOT FORCED OFF (symbol unresolved)" : ", pltcpu=0"
                                      : _pltForced == 0 ? "" : $", GPU-PLT re-cleared {_pltForced}x") + ")");
    }

    /// <summary>Hold <c>g_bGeneratePLTOnGPU</c> at zero. Re-asserted per frame rather than stored once: the
    /// engine writes it from a settings callback (<see cref="SymbolMap.PltOnGpu"/>) that can run after our
    /// install and again whenever the option is applied, and a single frame with it set is a character whose
    /// texture is created outside the upload funnel and stays borrowed until something re-creates it.
    /// <para>One predicted int compare in the common case. The count is reported because forcing an engine
    /// global is exactly the kind of thing that should never be silent.</para></summary>
    internal static void KeepPltOnCpu()
    {
        if (_pltOnGpu == null || *_pltOnGpu == 0) return;
        *_pltOnGpu = 0;
        _pltForced++;
    }

    /// <summary>#284: textures whose GL id is still 0 -- a live <c>CAurTexture*</c> whose build has never
    /// produced a name. The engine builds a texture EXACTLY twice: when it is first created
    /// (<c>AurTextureGet</c> @0x48cc10 <c>Add</c>s it to the <c>RefreshTextures</c> list) and when its PLT
    /// scheme changes (<c>AurTextureSetLayered</c> @0x489bd0 <c>AddUnique</c>s it), and
    /// <c>AurTextureBuildAndStoreAll</c> @0x48e680 drains that list to empty whether or not each build
    /// succeeded. <c>BuildAndStoreTexture</c> soft-fails -- if the decoded image at <c>basic+0x58</c> is
    /// still null it never reaches <c>glImageBasic</c>, so <c>basic+0x6c</c> stays 0 -- and nothing
    /// re-enqueues on resource arrival (<c>CResPLT::OnResourceServiced</c> @0x1136640 only parses the file).
    /// The one-shot queue can therefore lose a race with residency, permanently.
    /// <para>Vanilla never showed it because the engine's own draw kept re-binding id 0 and nobody noticed a
    /// texture-less part; under #264 an Alloy part with no id is SKIPPED, which live reads as a player
    /// character with no head. The retry is Alloy's, on Alloy's edge.</para></summary>
    private static readonly IntPtr[] _rebuildQ = new IntPtr[RebuildQueue];
    private static int _rebuildN;
    /// <summary>What one texture has cost, and whether asking again can ever pay: the attempts spent, the
    /// drip frame the last one ran on, and a verdict of NEVER for the two states the binary says no build can
    /// leave.
    /// <para>#284's first attempt was ONE try per texture, forever, and the live run says that is wrong for
    /// the case it was built for. The failure it fixes is a race with residency -- the resource is not there
    /// yet -- so the single try is spent at the worst possible moment, during the load, and the permanence it
    /// removed comes straight back. Re-armed every <see cref="RebuildRearm"/> drip frames up to
    /// <see cref="RebuildAttempts"/> times instead: bounded, self-terminating, and it converges on the one
    /// answer that matters, which is whether waiting was all it needed.</para>
    /// <para>ponytail: still keyed on the raw <c>CAurTexture*</c>, which the engine frees on an area
    /// transition -- so a NEW texture at a recycled address inherits the old one's ledger, and a
    /// <c>Never</c> verdict makes that inheritance permanent rather than merely early. Cleared only on
    /// <see cref="ContextLost"/>. Upgrade path unchanged: clear it on the first <see cref="Retire"/> batch
    /// after a world edge, where the pointer population turns over anyway.</para></summary>
    private struct Try { internal int Tries, Frame; internal bool Never; }
    private static readonly Dictionary<IntPtr, Try> _rebuildTried = new();
    private static int _dripFrame;
    private static long _rebuilds, _rebuildWon, _rebuildDead, _rebuildDropped;
    /// <summary>The residual, split by the reason a build cannot help -- see <see cref="Rebuild"/> for what
    /// each one is in the binary. These are POPULATIONS of distinct textures, not attempt counts: a texture
    /// is charged to exactly one of them, at its FINAL disposition, once.</summary>
    private static long _rebuildNullTex, _rebuildNoBasic, _rebuildNoImage, _rebuildSpent;
    /// <summary>How many unbuilt textures may be waiting at once, how many rebuilds a frame may pay for, how
    /// many attempts one texture is worth, and how long it waits between them. The budget is the stated cost:
    /// at most four engine texture builds per frame, on frames where anything is queued at all.
    /// <para>The re-arm interval is a WAIT, not a cadence -- roughly five seconds at 60fps -- and the attempt
    /// cap is what stops it becoming one. Three attempts across ~15 seconds outlives any plausible resource
    /// fetch; a texture still without an id after that is not waiting on anything.</para></summary>
    private const int RebuildQueue = 64, RebuildBudget = 4, RebuildAttempts = 3, RebuildRearm = 300;

    /// <summary>Note a texture that resolved to GL id 0, for <see cref="DripRebuilds"/> to retry. Cold --
    /// called from the enrollment scan, not from the draw loop -- and it allocates only on the first sighting
    /// of a given pointer. Nothing here dereferences <paramref name="tex"/>; the liveness guard is at the
    /// drain, where the call actually happens.</summary>
    internal static void NoteUnbuilt(IntPtr tex)
    {
        if (tex == IntPtr.Zero || _buildAndStore == null) return;
        if (_rebuildTried.TryGetValue(tex, out Try t)
            && (t.Never || t.Tries >= RebuildAttempts || _dripFrame - t.Frame < RebuildRearm)) return;
        if (_rebuildN >= RebuildQueue) { _rebuildDropped++; return; }
        for (int i = 0; i < _rebuildN; i++) if (_rebuildQ[i] == tex) return;
        _rebuildQ[_rebuildN++] = tex;
    }

    /// <summary>Spend at most <see cref="RebuildBudget"/> engine texture builds, on the frame anchor where
    /// <c>Beams.Enroll</c> and <c>SkyBox.Enroll</c> already run: once a frame, before the Alloy bracket, with
    /// the ENGINE's context current. That last part is not incidental -- <c>BuildAndStore</c> re-runs the
    /// engine's own upload (<c>SetActiveTexture</c>, <c>AurTextureBindTID</c>,
    /// <c>RenderInterface::CreateTexture</c>) and the engine caches what it thinks is bound, so running it
    /// inside Alloy's 4.6 bracket would leave that cache naming objects in the other context. It is the same
    /// reason <see cref="Adopt"/> is safe: the funnel fires where the engine already is.
    /// <para>Ahead of the owned draws, so a texture that builds here is sampled the same frame; and it routes
    /// through the upload funnel, so a successful retry lands in the owned table by itself.</para></summary>
    internal static void DripRebuilds()
    {
        _dripFrame++;
        CensusSettleCheck();
        if (_buildAndStore == null) return;
        for (int spent = 0; spent < RebuildBudget && _rebuildN > 0; spent++)
        {
            IntPtr tex = _rebuildQ[--_rebuildN];
            _rebuildQ[_rebuildN] = IntPtr.Zero;
            Rebuild(tex);
        }
    }

    /// <summary>One retry, guarded, and the two refusals that come BEFORE the call rather than after it.
    /// <para>A <c>CAurTexture*</c> is BORROWED (#138), so the pointer is re-proven through
    /// <see cref="AurTex"/> first: a nonzero id means the race resolved itself, and a REFUSED read means the
    /// material handed out a freed texture, which a build would follow into a wild vtable call.</para>
    /// <para><b>Then the identity question, which #284 shipped without and which made the retry an active
    /// hazard.</b> <c>AurTextureGetTID</c> @0x48e080 resolves the reference chain and returns 0 OUTRIGHT when
    /// the answer is <c>m_pNullTexture</c> -- it never reads <c>basic+0x6c</c>. So a material naming the
    /// engine's shared null texture reports no id no matter what, and the old code took that for an unbuilt
    /// texture and called <c>BuildAndStore</c> on it: <c>Reset</c> @0x4858c0 would then
    /// <c>RenderInterface::DeleteTextures</c> the GL names of the object EVERY other material falls back to.
    /// Damage, in service of a build whose result the caller cannot observe.</para>
    /// <para>And the pair the build itself resolves to: <c>AsCAurTextureBasic</c> (vtable 0x10) answering
    /// null, on which <c>BuildAndStore</c> @0x48bc80 returns without doing anything; and a build that ran and
    /// left <c>basic+0xe7</c> set, which is <c>ConstructImage</c> @0x48a5d0 reaching its tail with no decoded
    /// image at <c>+0x58</c> and no texture controller at <c>+0x38</c> -- the resource was not there. Only
    /// the last of those is worth asking again.</para></summary>
    private static void Rebuild(IntPtr tex)
    {
        long refusedBefore = AurTex.Refused;
        if (AurTex.Tid(tex) != 0) { _rebuildWon++; _rebuildTried.Remove(tex); return; }
        if (AurTex.Refused != refusedBefore) { _rebuildDead++; Give(tex, true); return; }

        IntPtr real = ((delegate* unmanaged<IntPtr, IntPtr>)*(IntPtr*)(*(IntPtr*)tex + AlloyCensus.TexVtblReferenced))(tex);
        if (real == IntPtr.Zero) real = tex;
        if (_nullTex != null && real == *_nullTex)
        { _rebuildNullTex++; Give(tex, true); UnbuiltDiag(tex, "the engine's shared NULL texture", IntPtr.Zero, 0); return; }
        IntPtr basic = ((delegate* unmanaged<IntPtr, IntPtr>)*(IntPtr*)(*(IntPtr*)real + AlloyCensus.TexVtblAsBasic))(real);
        if (basic == IntPtr.Zero)
        { _rebuildNoBasic++; Give(tex, true); UnbuiltDiag(tex, "no CAurTextureBasic behind it", IntPtr.Zero, 0); return; }

        // The PLT composite runs on the CPU only while the engine's global says so, and ConstructImage reads
        // it at the moment of the build -- so it is asserted here rather than trusted from Attach.
        KeepPltOnCpu();
        _rebuilds++;
        _buildAndStore(tex);
        int tid = AurTex.Tid(tex);
        if (tid != 0) { _rebuildWon++; _rebuildTried.Remove(tex); return; }
        // Counted at FINAL disposition only. Charged per attempt these would be attempt tallies wearing a
        // population's label -- two textures at three tries each reading as six -- and a texture that wins on
        // its last attempt would sit in the residual and in `now have an id` at once.
        if (Give(tex, false))
        {
            if (*(byte*)(basic + BasicInvalid) != 0) _rebuildNoImage++;
            else _rebuildSpent++;
        }
        UnbuiltDiag(tex, "built, still no id", basic, tid);
    }

    /// <summary>Charge one attempt against a texture, or write it off for good. Returns true when this was
    /// the last attempt it will get, so the caller can count a residual exactly once.</summary>
    private static bool Give(IntPtr tex, bool never)
    {
        _rebuildTried.TryGetValue(tex, out Try t);
        t.Tries++; t.Frame = _dripFrame; t.Never = never;
        _rebuildTried[tex] = t;
        return never || t.Tries >= RebuildAttempts;
    }

    /// <summary>Name what a still-unbuilt texture actually IS, once per distinct resource. The counters say
    /// how many; only these fields say which of the two states the binary allows, and #284 shipped a fix for
    /// the wrong one because nothing in the log could tell them apart. <c>scheme</c> is <c>basic+0x48</c>,
    /// the layer colours <c>SetScheme</c> @0x485f90 allocates -- nonzero is a PLT texture, which is what a
    /// player character's head, torso and thighs are.</summary>
    private static void UnbuiltDiag(IntPtr tex, string what, IntPtr basic, int tid)
    {
        if (_unbuiltDiagLeft <= 0) return;
        string nm = AlloyCensus.TexName(tex);
        if (!_unbuiltDiagSeen.Add(nm)) return;
        _unbuiltDiagLeft--;
        _log?.Info($"OwnedTextures: [unbuilt] \"{nm}\" -- {what}"
                 + (basic == IntPtr.Zero ? "" :
                    $"; scheme={(*(IntPtr*)(basic + BasicScheme) == IntPtr.Zero ? "none (file-backed)" : "PLT")}"
                  + $" image={(*(IntPtr*)(basic + BasicImage) == IntPtr.Zero ? "none" : "decoded")}"
                  + $" built={*(byte*)(basic + BasicBuilt)} invalid={*(byte*)(basic + BasicInvalid)} tid={tid}")
                 + $" (#284: {_rebuildTried[tex].Tries}/{RebuildAttempts} attempts)");
    }

    /// <summary>The fields <see cref="UnbuiltDiag"/> reads off a <c>CAurTextureBasic</c>, all four of them
    /// written by <c>ConstructImage</c> @0x48a5d0 and cleared by <c>Reset</c> @0x4858c0: the PLT layer scheme,
    /// the decoded image, the constructed latch, and the "no image and no controller" verdict.</summary>
    private const int BasicScheme = 0x48, BasicImage = 0x58, BasicBuilt = 0xe6, BasicInvalid = 0xe7;
    private static int _unbuiltDiagLeft = 8;
    private static readonly HashSet<string> _unbuiltDiagSeen = new();

    private static delegate* unmanaged<IntPtr, void> _buildAndStore;
    private static IntPtr* _nullTex;

    /// <summary>#284's number, for the period line: how many unbuilt textures Alloy has retried, how many of
    /// those now have an id, and how many it refused to touch. A rebuild count that keeps climbing is a
    /// texture population arriving late, not a leak; a nonzero drop count means the queue is too small for
    /// the area and the ceiling wants raising.
    /// <para>The residual is ATTRIBUTED rather than implied. Three of its four terms are textures no build
    /// can help and Alloy has stopped asking -- the parts wearing them are holes for the session, and the
    /// [unbuilt] lines name the content. <c>no image</c> is the one that was still waiting on something --
    /// a resource that did not arrive inside the attempt window.</para>
    /// </summary>
    internal static string RebuildNote()
    {
        long residual = _rebuildNullTex + _rebuildNoBasic + _rebuildNoImage + _rebuildSpent;
        if (_rebuilds == 0 && _rebuildN == 0 && _rebuildDropped == 0 && residual == 0) return "";
        return $", unbuilt textures: {_rebuilds} retried ({_rebuildWon} now have an id, {_rebuildDead} refused as "
             + $"freed), {_rebuildN} queued"
             + (_rebuildDropped == 0 ? "" : $", {_rebuildDropped} DROPPED (queue full)")
             + (residual == 0 ? "" : $"; UNBUILDABLE: {_rebuildNullTex} are the NULL texture, {_rebuildNoBasic} "
                 + $"have no basic, {_rebuildNoImage} found no image (resource not resident), {_rebuildSpent} "
                 + "out of attempts (all four are holes for the session; `no image` is the one whose resource "
                 + "never arrived inside the attempt window)");
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
        // #284: every engine texture is unbuilt again on the far side of a context loss, so the one-attempt
        // ledger has to go with the names it was a ledger about -- keeping it would refuse the whole world
        // its single retry. The queue holds pointers into a world that may not survive either.
        _rebuildTried.Clear(); Array.Clear(_rebuildQ); _rebuildN = 0; _dripFrame = 0;
        _censusRaw = _censusBcn = _censusTrans = 0;
        _censusRawBytes = _censusBcnBytes = _censusSaved = 0;
        _censusLastUpload = 0; _censusReported = false;
        // The refusal ledger was about the OLD context's driver state; the new context answers fresh.
        _bcnFails = 0; _bcn = _bcnWanted && _armed;
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

        // #284: the re-arm ledger, which is pure bookkeeping and therefore the half of the retry that CAN be
        // asserted without a client -- the build itself needs one. What it pins is the correction the live run
        // forced: a texture gets another attempt after waiting, and stops getting them at the cap.
        var savedTried = new Dictionary<IntPtr, Try>(_rebuildTried);
        int savedFrame = _dripFrame, savedQ = _rebuildN;
        var savedQArr = (IntPtr[])_rebuildQ.Clone();
        var savedBuild = _buildAndStore;
        _rebuildTried.Clear(); _rebuildN = 0; _dripFrame = 0;
        _buildAndStore = (delegate* unmanaged<IntPtr, void>)1;   // armed, never called: nothing here drains
        IntPtr probe = (IntPtr)0x5A1;
        NoteUnbuilt(probe);
        ok &= _rebuildN == 1;
        NoteUnbuilt(probe); ok &= _rebuildN == 1;                 // already queued: one entry, not two
        _rebuildN = 0; Give(probe, false);
        NoteUnbuilt(probe); ok &= _rebuildN == 0;                 // just tried -- waits out the re-arm
        _dripFrame += RebuildRearm;
        NoteUnbuilt(probe); ok &= _rebuildN == 1;                 // ...and comes back when the wait is over
        _rebuildN = 0;
        while (!Give(probe, false)) { }
        _dripFrame += RebuildRearm;
        NoteUnbuilt(probe); ok &= _rebuildN == 0;                 // out of attempts: never again
        _rebuildTried.Clear(); _rebuildN = 0;
        Give(probe, true); _dripFrame += RebuildRearm;
        NoteUnbuilt(probe); ok &= _rebuildN == 0;                 // written off outright: same, on one attempt
        // #265: the census window -- tallies split by path, silence settles into ONE report, and the next
        // upload opens a fresh window. The +0x70 read itself needs a client; what is pinned is the window.
        int svRaw = _censusRaw, svBcn = _censusBcn, svLast = _censusLastUpload;
        long svRawB = _censusRawBytes, svBcnB = _censusBcnBytes;
        bool svRep = _censusReported; int svDrip2 = _dripFrame;
        ExtenderLog? svLog = _log; _log = null;   // the check's synthetic report is not a session's line
        int svTrans = _censusTrans; long svSaved = _censusSaved;
        _censusRaw = _censusBcn = _censusTrans = 0;
        _censusRawBytes = _censusBcnBytes = _censusSaved = 0;
        _censusReported = false; _dripFrame = 0;
        int svGrid = _censusGrid, svGridCells = _censusGridCells; long svGridB = _censusGridBytes;
        _censusGrid = _censusGridCells = 0; _censusGridBytes = 0;
        CensusTally(false, 100, 300); CensusTally(true, 300, 0); CensusTally(false, 100, 0);
        ok &= _censusRaw == 2 && _censusBcn == 1 && _censusRawBytes == 200 && _censusBcnBytes == 300
           && _censusTrans == 1 && _censusSaved == 300;
        // #266: a 2x2 grid -- cell 0 counts the texture and its cell total, every cell counts bytes.
        CensusTally(true, 10, 0, 4, 0); CensusTally(true, 10, 0, 4, 1);
        CensusTally(true, 10, 0, 4, 2); CensusTally(true, 10, 0, 4, 3);
        ok &= _censusGrid == 1 && _censusGridCells == 4 && _censusGridBytes == 40;
        CensusSettleCheck(); ok &= !_censusReported;              // still uploading: not settled
        _dripFrame += CensusSettle;
        CensusSettleCheck(); ok &= _censusReported;               // quiet long enough: one report
        CensusTally(true, 50, 0);                                 // next area's first upload resets the window
        ok &= !_censusReported && _censusRaw == 0 && _censusBcn == 1 && _censusBcnBytes == 50 && _censusTrans == 0
           && _censusGrid == 0 && _censusGridCells == 0 && _censusGridBytes == 0;
        _censusGrid = svGrid; _censusGridCells = svGridCells; _censusGridBytes = svGridB;
        _censusRaw = svRaw; _censusBcn = svBcn; _censusRawBytes = svRawB; _censusBcnBytes = svBcnB;
        _censusTrans = svTrans; _censusSaved = svSaved;
        _censusLastUpload = svLast; _censusReported = svRep; _dripFrame = svDrip2; _log = svLog;

        _rebuildTried.Clear();
        foreach (var kv in savedTried) _rebuildTried[kv.Key] = kv.Value;
        Array.Copy(savedQArr, _rebuildQ, RebuildQueue);
        _dripFrame = savedFrame; _rebuildN = savedQ; _buildAndStore = savedBuild;

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
