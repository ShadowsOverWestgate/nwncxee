using System;
using System.Buffers;
using System.Numerics;
using System.Threading;
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
/// refused by #211 too), and anything uploaded through <c>DirectCreateTexture</c> -- Ghidra says that is the
/// movie player and <c>Camera::RefreshRenderSubframeResources</c>, neither of which Alloy draws.</para>
/// <para><b>The one resource adopted by COPY</b> (#360): the <c>g_bGeneratePLTOnGPU</c> path. It composites
/// by RENDERING the layers into an FBO, so there are no decoded pixels and no second upload to aim -- the
/// finished GL texture is all there is. <see cref="DrainPltCopies"/> takes it with
/// <c>glTexStorage2D</c> + <c>glCopyImageSubData</c> at the bracket. That is #211's mechanism and NOT its
/// bug: #211 keyed the copy on the engine's GL NAME, so a re-upload under a copied name served the stale
/// image until the next world edge. This keys on the same content identity as an upload and re-copies at
/// the engine's own rebuild funnel. Only reached with <c>pltcpu=0</c>; the default forces the CPU composite
/// and this queue stays empty.</para>
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
        /// <summary>ADR 0023: the drip frame this resource lost its last index entry on; 0 = indexed (in use).</summary>
        internal readonly int Idle;
        internal Entry(uint owned, long bytes, uint gen, int idle = 0) { Owned = owned; Bytes = bytes; Gen = gen; Idle = idle; }
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
        TranscodeBudget = Settings.Int("bcndrip", 1, 1, 64);
        _budgetCfgMb = Settings.Int("texcache_mb", -1, -1, 1 << 20);
        PrefetchBudget = Settings.Int("texprefetch", 2, 0, 64);
        _single = Settings.Bool("texsingle", true);
        // Zink bisect knobs: a handled texture's delete (auto = never on zink, whose bindless teardown faults on
        // a worker thread there) and the non-resident call ahead of it
        _texDelete = Settings.Int("texdelete", -1, -1, 1);
        _handleDrop = Settings.Bool("handledrop", true);
        if (_armed)
            log.Info(_bcn
                ? "OwnedTextures: BCn transcode armed -- uncompressed uploads are re-encoded BC1/BC3 (bcn=0 in sowx.cfg to keep them raw)"
                : "OwnedTextures: BCn transcode off (bcn=0) -- uncompressed uploads stay raw");
        // #360: RESOLVED whenever the table is armed, FORCED only when the knob says so. The flag is now
        // read as well as written -- it is what says whether a finished build went down the GPU branch and
        // therefore needs adopting by copy rather than by upload.
        if (_armed) _pltOnGpu = (int*)Native.Sym(SymbolMap.PltOnGpu);
        if (_armed && _pltWanted)
        {
            log.Info(_pltOnGpu != null
                ? "OwnedTextures: PLT compositing forced to the CPU -- player-character and tinted-creature "
                + "textures reach the upload funnel and become ownable (pltcpu=0 adopts the GPU composite by copy instead)"
                : "OwnedTextures: g_bGeneratePLTOnGPU unresolved -- PLT textures stay GPU-composited, "
                + "adopted by copy where that path is available and BORROWED where it is not (#360)");
            KeepPltOnCpu();
        }
        else if (_armed)
            log.Info("OwnedTextures: PLT compositing left on the GPU (pltcpu=0) -- the composite is adopted "
                   + "by a GPU-side copy at the bracket, keyed on the same content identity as an upload (#360)");
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
        // ADR 0023: the build itself, so a warm resource skips the read, the decode and the upload.
        _origBuildAndStoreTexture = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.AurTextureBuildAndStoreTexture, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnBuildAndStoreTexture);
        _znam = (delegate* unmanaged<nuint, IntPtr>)Native.Sym("_Znam");   // the engine's operator new[]: Reset frees the cell array with delete[]
        log.Info(_origBuildAndStoreTexture != null && _znam != null
            ? $"OwnedTextures: watching CAurTextureBasic::BuildAndStoreTexture -- a resident texture is handed back without a read, decode or upload (texcache_mb {(_budgetCfgMb < 0 ? "auto" : _budgetCfgMb.ToString())})"
            : "OwnedTextures: BuildAndStoreTexture not hooked or operator new[] unresolved -- textures are kept across the world edge but every build is cold");
        log.Info(_origDelete != null
            ? "OwnedTextures: watching RenderInterface::DeleteTextures -- a tid stops resolving the moment it stops meaning its resource"
            : "OwnedTextures: DeleteTextures not installed -- a recycled engine name will resolve the previous area's resource");

        // #360: glImageBasic is taken whenever the table is armed, because it is now TWO things -- half of
        // #358's hold, and the edge that says a PLT build has finished so its composite can be adopted.
        // ConstructImage is the hold alone, so it stays gated on the flag actually being there to hold.
        // Attach ran first (AlloyRenderer wires the pass table before it installs it), so a null _pltOnGpu
        // means the symbol did not resolve -- already logged there, with its reason.
        _origGlImageBasic = (delegate* unmanaged<IntPtr, byte, int>)hooks.Install(
            SymbolMap.AurTextureGlImageBasic, (IntPtr)(delegate* unmanaged<IntPtr, byte, int>)&OnGlImageBasic);
        if (_pltOnGpu != null)
            _origConstructImage = (delegate* unmanaged<IntPtr, void>)hooks.Install(
                SymbolMap.AurTextureConstructImage, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnConstructImage);
        if (_pltWanted)
            log.Info(_origConstructImage != null && _origGlImageBasic != null
                ? "OwnedTextures: GPU-PLT held at zero on both readers -- an options-apply that re-enables it "
                + "cannot rebuild a character outside the upload funnel any more (#358)"
                : "OwnedTextures: a GPU-PLT reader did not install -- the flag is only re-asserted per frame, so "
                + "an options-apply can still rebuild PLT textures GPU-side and leave them BORROWED (#358)");
        else
            log.Info(_origGlImageBasic != null
                ? "OwnedTextures: watching CAurTextureBasic::glImageBasic -- a GPU-composited PLT build is "
                + "queued for adoption by copy on the next bracket (#360)"
                : "OwnedTextures: glImageBasic not installed -- a GPU-composited PLT texture has no edge to "
                + "adopt it on and stays BORROWED (#360)");
    }

    private static delegate* unmanaged<IntPtr, void> _origConstructImage;
    private static delegate* unmanaged<IntPtr, byte, int> _origGlImageBasic;

    /// <summary>#358: the engine is about to decide whether to composite this texture's PLT layers on the
    /// CPU. Cold -- once per texture build -- and the whole body is one predicted compare.</summary>
    [UnmanagedCallersOnly]
    private static void OnConstructImage(IntPtr tex)
    {
        KeepPltOnCpu();
        if (_origConstructImage != null) _origConstructImage(tex);
    }

    /// <summary>#358: ...and the same hold on the reader that routes a built PLT texture around the upload
    /// funnel. Both readers, because either one alone leaves half the decision to a stale flag.
    /// <para>#360: and, with the hold off, the edge that says a GPU composite has FINISHED. The flag is read
    /// before the original runs -- it is what that call is about to branch on -- and the queue is filled
    /// after, when the cells carry names. Cold: once per texture build.</para></summary>
    [UnmanagedCallersOnly]
    private static int OnGlImageBasic(IntPtr tex, byte useSubImage)
    {
        if (_pltWanted) KeepPltOnCpu();
        bool gpu = _pltOnGpu != null && *_pltOnGpu != 0;
        // glImageBasic returns int (#433: the MSVC export encodes the return type Itanium drops), and
        // the caller propagates it -- so the trampoline's value must be carried, not dropped into work.
        int r = _origGlImageBasic != null ? _origGlImageBasic(tex, useSubImage) : 0;
        if (gpu) QueuePltCopy(tex);
        return r;
    }

    /// <summary><c>CAurTextureBasic+0x84</c>: the texture's own format/kind word. <c>glImageBasic</c>
    /// @0x48bd67 takes the GPU-composite branch on <c>== 4</c> and nothing else, so 4 is the PLT marker and
    /// this is the ONLY test that separates a build Alloy must copy from one it has already uploaded.</summary>
    private static readonly int OffPltKind = EngineLayout.TexBasicPltKind;
    private const int PltKind = 4;

    /// <summary>One finished GPU composite, waiting for a context that may copy it. Deliberately holds NO
    /// <c>CAurTexture*</c>: the identity is already resolved to a content key here, so nothing in the queue
    /// can be a pointer into a world that moved on before the bracket (#145's lesson, avoided rather than
    /// guarded).</summary>
    private readonly struct PltCopy
    {
        public readonly uint Src; public readonly ulong Key;
        public PltCopy(uint src, ulong key) { Src = src; Key = key; }
    }
    private static readonly List<PltCopy> _pltQ = new();
    private static long _pltCopied, _pltCopyRefused, _pltCopyDied;
    private static int _pltCopyDiagLeft = 8;
    private static bool _pltNoCopyLogged;

    /// <summary>#360: enrol every cell of a finished GPU-PLT build for adoption by copy. Queued rather than
    /// copied here for two reasons, both hard: <c>glCopyImageSubData</c> is GL 4.3 and this runs in the
    /// ENGINE's context, and the two contexts are share-listed, so the source is only guaranteed visible
    /// after the flush the frame's own present provides.
    /// <para>No dedup. The engine builds a texture exactly twice -- creation, and <c>AurTextureSetLayered</c>
    /// when the PLT scheme changes -- and BOTH drain through here, which is precisely the cadence at which a
    /// re-composite must produce a new copy. Skipping a build because its key is already in the table is how
    /// #211's name-keyed copy came to serve a stale skin.</para>
    /// <para>The rebuild sequence, read at 0x48c630 rather than assumed: the branch frees the cell-names
    /// array, nulls <c>+0x50</c>, calls <c>RenderInterface::DeleteTextures</c> on the old name, then falls
    /// into the first-build path for a FRESH <c>GenTextures</c>. So the delete lands inside the original --
    /// before this runs -- and <see cref="Retire"/> has already dropped the old row and any entry still
    /// queued against the old name. What is queued here always names the texture that was just composited,
    /// even where the driver hands the freed name straight back.</para>
    /// <para>Always ONE cell: the branch writes <c>0x10001</c> to <c>+0xca</c>, i.e. numX = numY = 1. The
    /// loop is over the grid anyway, because the grid is what the key folds in.</para></summary>
    private static void QueuePltCopy(IntPtr tex)
    {
        if (!_armed || !AurTex.IsBasic(tex)) return;
        if (*(int*)((byte*)tex + OffPltKind) != PltKind) return;
        uint* cells = *(uint**)((byte*)tex + OffCellNames);
        if (cells == null) return;
        int n = *(short*)((byte*)tex + OffNumX) * *(short*)((byte*)tex + OffNumY);
        for (int cell = 0; cell < n; cell++)
        {
            int etid = (int)cells[cell];
            if (etid <= 0) continue;
            _pltQ.Add(new PltCopy((uint)etid, Key(tex, cell)));
        }
    }

    /// <summary>The engine is retiring this GL name; anything queued against it describes a texture that no
    /// longer exists. Dropped rather than copied-and-discarded, because the copy would read a recycled name.
    /// <para>The queue holds at most a few entries between brackets, so the scan is over nothing.</para></summary>
    private static void DropQueued(uint name)
    {
        for (int i = _pltQ.Count - 1; i >= 0; i--)
            if (_pltQ[i].Src == name) { _pltQ.RemoveAt(i); _pltCopyDied++; }
    }

    private static delegate* unmanaged<IntPtr, byte, int, void> _origCreate;
    private static delegate* unmanaged<uint, uint*, void> _origDelete;
    private static long _retired;

    /// <summary><c>CAurTextureBasic+0x50</c>: <c>uint*</c>, the <c>numX*numY</c> GL names
    /// <c>glImageBasic</c> @0x48bcf0 generates and binds one per cell.</summary>
    private static readonly int OffCellNames = EngineLayout.TexBasicCellNames;
    /// <summary><c>CAurTextureBasic+0xa8</c>: <c>char[0x20]</c>, the resolved texture name
    /// <c>LoadImage</c> @0x487980 fills and <c>CAurTexture::Restore</c> @0x48e300 re-acquires by. Stable
    /// across a delete/rebuild, which is what makes it an identity.</summary>
    private static readonly int OffName = EngineLayout.TexBasicResolvedName;
    /// <summary><c>CAurTextureBasic+0x48</c> / <c>+0xd4</c>: the PLT scheme array (<c>int*</c>) and its
    /// <c>short</c> count -- the other half of the engine's own texture identity.</summary>
    private static readonly int OffSchemes = EngineLayout.TexBasicSchemes,
                               OffSchemeCount = EngineLayout.TexBasicSchemeCount;
    /// <summary><c>CAurTextureBasic+0xca</c> / <c>+0xcc</c>: the <c>short</c> atlas grid the engine
    /// de-atlases into one GL texture per cell.</summary>
    private static readonly int OffNumX = EngineLayout.TexBasicNumX, OffNumY = EngineLayout.TexBasicNumY;

    /// <summary>The engine is about to upload cell <paramref name="cell"/> of <paramref name="tex"/> into the
    /// GL name it has bound. Let that happen, then do it again into a texture of ours.
    /// <para>Cold -- once per texture per (re)build, never per frame -- so the allocations a Dictionary
    /// insert makes are not on a hot path. Runs in the ENGINE's context; see the class remarks.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnCreateTexture(IntPtr tex, byte useSubImage, int cell)
    {
        long t0 = FrameZones.Now;
        // #386: ONE upload. Adopt uploads into Alloy's name and hands that name to the engine's cell array;
        // only when it declines (or texsingle=0) does the engine's own copy get built.
        bool handled = _single && Adopt(tex, cell, single: true);
        if (!handled)
        {
            if (_origCreate != null) _origCreate(tex, useSubImage, cell);
            Adopt(tex, cell, single: false);
        }
        FrameZones.Add(FrameZones.Zone.Tex, t0);
        FrameZones.Count(FrameZones.Ev.TexUpload);
    }

    /// <summary>The upload handler's body, split out so <see cref="SelfCheck"/> can exercise it -- an
    /// <c>UnmanagedCallersOnly</c> method cannot be called from managed code.</summary>
    /// <summary>Returns true when Alloy's upload stands in for the engine's (<paramref name="single"/>): the
    /// engine's cell entry then names Alloy's texture and the engine's own name is deleted unused. False =
    /// the caller must run the engine's upload; in the double path (<paramref name="single"/> false) the
    /// engine's copy already exists and the return value is ignored.</summary>
    private static bool Adopt(IntPtr tex, int cell, bool single)
    {
        if (!_armed || _origCreate == null) return false;
        // The BASIC vtable specifically: every field below belongs to the subclass, and #138/#221 say a
        // texture pointer is only ever as good as its vtable.
        if (!AurTex.IsBasic(tex)) return false;

        uint* cells = *(uint**)((byte*)tex + OffCellNames);
        int n = *(short*)((byte*)tex + OffNumX) * *(short*)((byte*)tex + OffNumY);
        if (cells == null || cell < 0 || cell >= n) return false;
        int etid = (int)cells[cell];
        if (etid <= 0) return false;
        // A cell already naming an owned texture is the engine re-uploading INTO our name (a Restore after a
        // warm hand-back, or a refresh): fresh storage under a new name, exactly as a re-upload always was.

        // ponytail: before the first present there is no 4.6 context and no entry points, so a texture
        // uploaded that early stays borrowed for the session -- nothing re-drives this funnel for a texture
        // the engine already holds. The window is the main menu, which Alloy does not draw, and the count
        // says so out loud rather than hiding it. Upgrade path: re-drive AurTextureRefreshAll @0x48f050 from
        // the engine's context once the bracket exists.
        if (Gl.GenTextures == null || Gl.BindTexture == null || Gl.GetIntegerv == null)
        {
            if (!single) _borrowed.Add(etid);
            return false;
        }

        ulong key = Key(tex, cell);
        uint dst;
        Gl.GenTextures(1, &dst);
        if (dst == 0) { if (!single) _borrowed.Add(etid); return false; }

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
        bool wantBcn = err == 0 && _bcn && *(int*)((byte*)tex + OffCompressedSize) == 0;
        TexScan.Result scan = default;
        bool scanned = err == 0 && TexScan.Scan(tex, cell, CoverageAlpha, wantBcn, out scan);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)savedBind);

        if (err != 0)
        {
            if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dst);
            if (_diagLeft > 0) { _diagLeft--; _log?.Info($"OwnedTextures: tid {etid} {(single ? "left to the engine" : "borrowed")} -- the engine's upload into our name gave 0x{err:x}"); }
            if (!single) _borrowed.Add(etid);
            return false;   // single: the caller builds the engine's copy, and the double-path Adopt borrows it
        }

        if (single)
        {
            // The engine's cell now names OUR texture; its own name never received pixels and dies here.
            // The engine's bind cache still says the old name is bound on unit 0 -- a name that no longer
            // exists, so the next AurTextureBindTID of ours compares unequal and binds. Deleting a bound name
            // resets the binding to 0, which is the state that cache should read as.
            cells[cell] = dst;
            if (cell == 0) *(int*)((byte*)tex + OffGlId) = (int)dst;
            uint old = (uint)etid;
            if (!_owned.Contains(old) && Gl.DeleteTextures != null) Gl.DeleteTextures(1, &old);
            else _engineHeld.Remove(old);   // a refresh INTO our name: the engine will never return the old one, so Discard deletes it
            _engineHeld.Add(dst);
            _borrowed.Remove(etid);
            _singleUploads++;
            etid = (int)dst;
        }
        Replace(key, etid, dst, bytes);
        Census(tex, bytes, 0, n, cell);
        HighWater();
        if (scanned) _stats[dst] = new Stat { Holes = scan.Holes, Graded = scan.Graded, Mean = scan.Mean, MeanOk = scan.MeanOk };
        else _stats.Remove(dst);
        // A graded diffuse arriving AFTER its part enrolled was classed Cutout without it: re-walk so the
        // class is asked again. ponytail: a full walk per such texture; a per-part dirty list if it shows in Enroll.
        if (scanned && scan.Graded >= AlloyCensus.GradedBlend) SceneWalk.Dirty = true;
        if (AlloyCensus._dumpTex != null && AlloyCensus.TexName(tex).Contains(AlloyCensus._dumpTex, StringComparison.OrdinalIgnoreCase))
            _log?.Info($"[dumpframe] SCAN tex={AlloyCensus.TexName(tex)} cell={cell} scanned={scanned} compressed={*(int*)((byte*)tex + OffCompressedSize) != 0} refusedFmt={TexScan.LastRefusedFormat} holes={scan.Holes:F2} graded={scan.Graded:F2} {scan.W}x{scan.H} owned={dst}");
        if (wantBcn && scan.Rgba != null) EnqueueTranscode(key, etid, dst, bytes, scan.Rgba, scan.W, scan.H, scan.Mips);
        return true;
    }

    /// <summary>#386: <c>texsingle</c> in sowx.cfg (boot 1) -- one upload per texture instead of the engine's
    /// copy plus Alloy's. 0 is the double path as #259 shipped it.</summary>
    private static bool _single = true;
    private static long _singleUploads;
    /// <summary>Owned names the ENGINE holds in a cell array (a single upload or a warm hand-back). Such a name
    /// must not be GL-deleted by Alloy while the engine may still bind it for its own 2D use (icons, portraits,
    /// TTF glyph sheets all come through this funnel and all transcode): a swap or eviction drops its handle and
    /// leaves the delete to the engine's own DeleteTextures, which <see cref="FilterRetire"/> then passes to the
    /// driver because the name is no longer in <see cref="_owned"/>. The VRAM note undercounts these zombies.</summary>
    private static readonly HashSet<uint> _engineHeld = new();

    /// <summary>What the funnel-side scan (<see cref="TexScan"/>) learned about each owned name: the cutout
    /// hole fraction and the mean colour. Replaces every GPU readback the consumers used to make. Keyed by
    /// the owned name; removed with the name.</summary>
    private struct Stat { public float Holes; public float Graded; public Vector3 Mean; public bool MeanOk; }
    private static readonly Dictionary<uint, Stat> _stats = new();
    /// <summary>The hole fraction of an owned name (or of the owned copy behind an engine tid). False = the
    /// name never came through the funnel; the caller tests rather than casts a possible slab.</summary>
    internal static bool TryHoles(int tid, out float holes)
    {
        holes = 1f;
        if (!TryStat(tid, out Stat st)) return false;
        holes = st.Holes;
        return true;
    }
    /// <summary>The graded-alpha fraction (see <see cref="TexScan.Result.Graded"/>) of the owned texture behind an
    /// engine texture object. False = not scanned yet; the caller keeps its default class.</summary>
    internal static bool TryGraded(IntPtr tex, out float graded)
    {
        graded = 0f;
        // The engine's own getter: the census hands the CAurTexture wrapper, and the GL id lives on its basic.
        if (tex == IntPtr.Zero || !TryStat(AurTex.Tid(tex), out Stat st)) return false;
        graded = st.Graded;
        return true;
    }
    /// <summary>The mean colour of an owned name, 0..1 in the texture's own encoding.</summary>
    internal static bool TryMean(int tid, out Vector3 mean)
    {
        mean = default;
        if (!TryStat(tid, out Stat st) || !st.MeanOk) return false;
        mean = st.Mean;
        return true;
    }
    private static bool TryStat(int tid, out Stat st)
    {
        if (tid > 0 && _stats.TryGetValue((uint)tid, out st)) return true;
        if (tid > 0 && _index.TryGetValue(tid, out ulong key) && _res.TryGetValue(key, out Entry e) && _stats.TryGetValue(e.Owned, out st)) return true;
        st = default;
        return false;
    }

    // ---- #386: PREFETCH -- build a texture when its part ENROLS, not when the camera first sees it ----

    /// <summary>Textures the enrolment walk saw unbuilt on an accepted part. The drip builds them through
    /// the engine's own <c>BuildAndStore</c> (the #284 retry path, with its liveness guards), a few per
    /// frame, so the burst a turn used to pay -- 11-41 demands on one frame -- is spread over the frames
    /// after enrolment, before the camera gets there. A build lands in the cache, so the engine's own
    /// first-sight build is then a warm hit. <c>texprefetch</c> in sowx.cfg is the per-frame budget (0 = off).
    /// The seen-set is keyed on the BORROWED pointer and cleared when it grows large: a recycled address
    /// costs one texture its prefetch (it builds at first sight, as before), never a wild call -- the drip
    /// re-proves the pointer through <see cref="AurTex"/> before touching it.</summary>
    private const int PrefetchQueue = 8192;
    private static readonly IntPtr[] _prefetchQ = new IntPtr[PrefetchQueue];
    private static int _prefetchN, _prefetchHead;
    private static readonly HashSet<IntPtr> _prefetchSeen = new();
    private static int PrefetchBudget = 2;
    private static long _prefetched, _prefetchDropped;

    internal static void Prefetch(IntPtr tex)
    {
        if (tex == IntPtr.Zero || PrefetchBudget == 0 || _buildAndStore == null || !_armed) return;
        // A material hands out a per-material REFERENCE object; thousands of them name one basic texture. The
        // first run dedupe'd on the reference and drowned the queue (12612 dropped, 5 built): resolve to the
        // basic, and only an UNBUILT one is worth a slot. Tid() re-proves the pointer first (#138).
        if (AurTex.Tid(tex) != 0) return;
        IntPtr real = ((delegate* unmanaged<IntPtr, IntPtr>)*(IntPtr*)(*(IntPtr*)tex + AlloyCensus.TexVtblReferenced))(tex);
        if (real == IntPtr.Zero) real = tex;
        if (_nullTex != null && real == *_nullTex) return;
        IntPtr basic = ((delegate* unmanaged<IntPtr, IntPtr>)*(IntPtr*)(*(IntPtr*)real + AlloyCensus.TexVtblAsBasic))(real);
        if (basic == IntPtr.Zero || *(int*)((byte*)basic + OffGlId) != 0 || *((byte*)basic + FlagNeverBuild) != 0) return;
        if (_prefetchSeen.Count > 65536) _prefetchSeen.Clear();
        if (!_prefetchSeen.Add(basic)) return;
        if (_prefetchN >= PrefetchQueue) { _prefetchDropped++; return; }
        _prefetchQ[(_prefetchHead + _prefetchN++) % PrefetchQueue] = tex;
    }

    private static void DripPrefetch()
    {
        int built = 0;
        // ponytail: up to 8x the budget in already-built or dead entries per frame, which cost a field read each
        for (int looked = 0; built < PrefetchBudget && looked < PrefetchBudget * 8 && _prefetchN > 0; looked++)
        {
            IntPtr tex = _prefetchQ[_prefetchHead];
            _prefetchHead = (_prefetchHead + 1) % PrefetchQueue; _prefetchN--;
            long before = _rebuilds;
            Rebuild(tex);   // already built = a free return; freed = refused by the vtable re-proof
            if (_rebuilds != before) { built++; _prefetched++; FrameZones.Count(FrameZones.Ev.Prefetch); }
        }
    }

    internal static string PrefetchNote()
        => $"prefetch: {_prefetched} built ahead, {_prefetchN} queued, {_prefetchDropped} dropped, budget {PrefetchBudget}/frame; single upload {(_single ? "on" : "off")} ({_singleUploads} textures)";

    // ---- ADR 0023: the texture CACHE -- the delete funnel is the world edge for the INDEX, not the resource ----

    /// <summary>VRAM the cache may hold, in bytes. 0 = the pre-ADR-0023 behaviour (a retired index entry
    /// discards its resource). <c>texcache_mb</c> in sowx.cfg; -1 (the default) = a quarter of the probe's
    /// reported VRAM, 1024 MiB when the device answers no memory query. Resolved lazily: the probe runs on
    /// the first presented frame, after this table is attached.</summary>
    private static long _budgetBytes = -1;
    private static int _budgetCfgMb = -1;
    private const int BudgetFallbackMib = 1024;
    private static long Budget()
    {
        if (_budgetBytes >= 0) return _budgetBytes;
        if (_budgetCfgMb >= 0) return _budgetBytes = (long)_budgetCfgMb * OneMiB;
        if (!AlloyRenderer.VramProbed) return -1;   // not probed yet: "unknown", not the fallback
        // a quarter of the card: NVX reports the total, Mesa's ATI_meminfo only the free pool (most of the
        // card at probe time), and a driver with neither gets the fallback
        long total = AlloyRenderer.VramTotalMib, free = AlloyRenderer.VramFreeMib;
        return _budgetBytes = (total > 0 ? total / 4 : free > 0 ? free / 4 : BudgetFallbackMib) * OneMiB;
    }
    private static long _idleBytes, _cacheHits, _cacheMisses, _cacheEvicted, _cacheKept;
    private static bool _evictDue;
    private static int _texDelete = -1;
    private static bool _handleDrop = true, _deleteNoted;
    private static readonly List<ulong> _unidle = new();
    /// <summary>Handles to release for resources that went idle -- GL, so drained in the bracket like <see cref="_pending"/>.</summary>
    private static readonly HashSet<uint> _pendingHandles = new();

    /// <summary>Post-build scalars of a plain texture, keyed on its cell-0 content key: what the warm path
    /// writes back into a fresh <c>CAurTextureBasic</c> so the engine believes it built the texture. Captured
    /// from the object after its cold build -- nothing here is derived, every field is what the engine's own
    /// loader left (numX/numY included; their writer is outside the class and was not chased).</summary>
    private struct TexMeta { public int W, H, Bpp, Compressed, NumX, NumY; public float Alpha; }
    private static readonly Dictionary<ulong, TexMeta> _meta = new();
    /// <summary>Content keys with a transcode queued or in flight: not warm-eligible (the pipeline is about
    /// to retire the name the engine would be handed).</summary>
    private static readonly HashSet<ulong> _inFlight = new();
    private static delegate* unmanaged<IntPtr, void> _origBuildAndStoreTexture;
    private static delegate* unmanaged<nuint, IntPtr> _znam;
    private static readonly int OffImage = EngineLayout.TexBasicImage, OffGlId = EngineLayout.TexBasicGlId,
        OffWidth = EngineLayout.TexBasicWidth, OffHeight = EngineLayout.TexBasicHeight,
        OffBpp = EngineLayout.TexBasicBpp, OffAlphaMean = EngineLayout.TexBasicAlphaMean,
        OffController = EngineLayout.TexBasicController, OffPltMarker = EngineLayout.TexBasicPltKind,
        FlagCube = EngineLayout.TexBasicFlagCube, FlagImageBuilt = EngineLayout.TexBasicFlagBuilt,
        FlagNoImage = EngineLayout.TexBasicFlagNoImage, FlagNeverBuild = EngineLayout.TexBasicFlagNeverBuild;
    private const int PltMarker = 4;

    /// <summary>Is this a texture the cache may stand in for? Plain 2D, no PLT scheme, no animation
    /// controller, not the engine's never-build case. Everything else takes the cold path unchanged.</summary>
    private static bool Plain(IntPtr tex)
        => *((byte*)tex + FlagNeverBuild) == 0 && *((byte*)tex + FlagCube) == 0
           && *(IntPtr*)((byte*)tex + OffSchemes) == IntPtr.Zero && *(int*)((byte*)tex + OffPltMarker) != PltMarker
           && *(IntPtr*)((byte*)tex + OffController) == IntPtr.Zero;

    [UnmanagedCallersOnly]
    private static void OnBuildAndStoreTexture(IntPtr tex)
    {
        if (_armed && Budget() > 0 && Plain(tex) && TryWarm(tex)) return;
        if (_origBuildAndStoreTexture != null) _origBuildAndStoreTexture(tex);
        if (_armed && Budget() > 0) Capture(tex);
    }

    /// <summary>The cold build just finished: remember what it produced, if every cell came through the
    /// funnel as an owned resource.</summary>
    private static void Capture(IntPtr tex)
    {
        if (!AurTex.IsBasic(tex) || !Plain(tex) || *(IntPtr*)((byte*)tex + OffImage) != IntPtr.Zero) return;
        uint* cells = *(uint**)((byte*)tex + OffCellNames);
        int nx = *(short*)((byte*)tex + OffNumX), ny = *(short*)((byte*)tex + OffNumY);
        if (cells == null || nx <= 0 || ny <= 0 || *((byte*)tex + FlagImageBuilt) == 0) return;
        for (int c = 0; c < nx * ny; c++)
            if (!_index.TryGetValue((int)cells[c], out ulong k) || !_res.ContainsKey(k)) return;
        _meta[Key(tex, 0)] = new TexMeta
        {
            W = *(int*)((byte*)tex + OffWidth), H = *(int*)((byte*)tex + OffHeight), Bpp = *(int*)((byte*)tex + OffBpp),
            Compressed = *(int*)((byte*)tex + OffCompressedSize), NumX = nx, NumY = ny,
            Alpha = *(float*)((byte*)tex + OffAlphaMean),
        };
    }

    /// <summary>The warm path: every cell of this texture is resident, so hand the engine Alloy's names and
    /// the scalars its build would have produced, and skip the read, the decode and the upload. The engine
    /// then binds our names for its own 2D use (share-listed) and, on its next Reset, hands them to
    /// DeleteTextures -- where <see cref="OnDeleteTextures"/> keeps them. Refuses on any doubt.</summary>
    private static bool TryWarm(IntPtr tex)
    {
        if (_znam == null || !AurTex.IsBasic(tex)) return false;
        ulong k0 = Key(tex, 0);
        if (!_meta.TryGetValue(k0, out TexMeta m)) { _cacheMisses++; return false; }
        int cells = m.NumX * m.NumY;
        if (cells <= 0 || cells > 4096) return false;
        uint* names = stackalloc uint[cells > 64 ? 1 : cells];
        uint[]? big = cells > 64 ? new uint[cells] : null;
        for (int c = 0; c < cells; c++)
        {
            ulong k = (k0 & ~0xFFFFFFFFUL) | (uint)c;
            if (_inFlight.Contains(k) || !_res.TryGetValue(k, out Entry e)) { _cacheMisses++; return false; }
            if (big != null) big[c] = e.Owned; else names[c] = e.Owned;
        }
        IntPtr arr = _znam((nuint)cells * 4);
        if (arr == IntPtr.Zero) return false;
        for (int c = 0; c < cells; c++)
        {
            uint name = big != null ? big[c] : names[c];
            ((uint*)arr)[c] = name;
            _engineHeld.Add(name);
            ulong k = (k0 & ~0xFFFFFFFFUL) | (uint)c;
            _index[(int)name] = k;
            Entry e = _res[k];
            if (e.Idle != 0) { _idleBytes -= e.Bytes; _res[k] = new Entry(e.Owned, e.Bytes, e.Gen, 0); _pendingHandles.Remove(e.Owned); }   // live again: its handle must not be dropped under a record
        }
        FillWarm(tex, arr, in m);
        _cacheHits++;
        FrameZones.Count(FrameZones.Ev.TexWarm);
        return true;
    }

    /// <summary>The field writes alone, so the self-check can run them over a stand-in object.</summary>
    private static void FillWarm(IntPtr tex, IntPtr names, in TexMeta m)
    {
        byte* p = (byte*)tex;
        *(IntPtr*)(p + OffCellNames) = names;
        *(int*)(p + OffGlId) = *(int*)names;
        *(short*)(p + OffNumX) = (short)m.NumX; *(short*)(p + OffNumY) = (short)m.NumY;
        *(int*)(p + OffWidth) = m.W; *(int*)(p + OffHeight) = m.H; *(int*)(p + OffBpp) = m.Bpp;
        *(int*)(p + OffCompressedSize) = m.Compressed;
        *(float*)(p + OffAlphaMean) = m.Alpha;
        *(IntPtr*)(p + OffImage) = IntPtr.Zero;
        p[FlagImageBuilt] = 1; p[FlagNoImage] = 0;
    }

    /// <summary>Evict idle resources, oldest first, until the cache fits its budget. Bracket-side (it
    /// discards GL names); cold -- runs only after a retire or a swap raised <see cref="_evictDue"/>.</summary>
    private static void Evict()
    {
        _evictDue = false;
        long budget = Budget();
        if (budget <= 0 || _bytes <= budget) return;
        var referenced = new HashSet<ulong>(_index.Values);
        var idle = new List<KeyValuePair<ulong, Entry>>();
        foreach (var kv in _res)
        {
            if (kv.Value.Idle == 0) continue;
            if (referenced.Contains(kv.Key)) { _idleBytes -= kv.Value.Bytes; _pendingHandles.Remove(kv.Value.Owned); _unidle.Add(kv.Key); continue; }
            idle.Add(kv);
        }
        foreach (ulong k in _unidle) { Entry e = _res[k]; _res[k] = new Entry(e.Owned, e.Bytes, e.Gen, 0); }
        _unidle.Clear();
        idle.Sort((a, b) => a.Value.Idle.CompareTo(b.Value.Idle));
        int n = 0;
        foreach (var kv in idle)
        {
            if (_bytes <= budget) break;
            _res.Remove(kv.Key);
            _meta.Remove(kv.Key & ~0xFFFFFFFFUL);
            _idleBytes -= kv.Value.Bytes;
            Discard(kv.Value);   // the record marks land in Drain, once for the whole batch
            n++;
        }
        _cacheEvicted += n;
        FrameZones.Count(FrameZones.Ev.TexEvict, n);
    }

    internal static string CacheNote()
    {
        long b = Budget();
        return $"texcache: {_bytes / OneMiB} MiB resident ({_idleBytes / OneMiB} idle) of {(b > 0 ? (b / OneMiB).ToString() : b == 0 ? "off" : "unprobed")} MiB, " +
               $"{_cacheHits} warm builds, {_cacheMisses} misses, {_cacheKept} kept across the world edge, {_cacheEvicted} evicted, {_meta.Count} keys known";
    }

    /// <summary>Keep-then-evict, the delete filter, and the warm fill over a stand-in object.</summary>
    internal static void SelfCheckCache(ExtenderLog log)
    {
        bool ok = true;
        long svBudget = _budgetBytes; int svCfg = _budgetCfgMb; long svBytes = _bytes, svIdle = _idleBytes;
        var svRes = new Dictionary<ulong, Entry>(_res); var svIdx = new Dictionary<int, ulong>(_index);
        var svOwned = new HashSet<uint>(_owned); int svPend = _pending.Count, svPendH = _pendingHandles.Count;
        var svMeta = new Dictionary<ulong, TexMeta>(_meta);
        _res.Clear(); _index.Clear(); _owned.Clear(); _meta.Clear(); _bytes = 0; _idleBytes = 0;
        _budgetCfgMb = 1; _budgetBytes = OneMiB;   // 1 MiB budget

        int svDrip = _dripFrame;
        ulong kA = 0xA00000000UL, kB = 0xB00000000UL;
        Replace(kA, 11, 101, 600 * 1024); _dripFrame = 10;
        Replace(kB, 12, 102, 600 * 1024); _index[13] = kB; _dripFrame = 20;   // B under two engine names
        uint* dead = stackalloc uint[3] { 11, 102, 12 };
        // the engine retires its own name 11 (A idle, kept) and 12 (B marked idle though 13 still points at it),
        // and hands back OWNED name 102 (kept, filtered from the driver call)
        uint kept = FilterRetire(3, dead, out uint passN);
        ok &= passN == 2 && kept == 1 && _res.ContainsKey(kA) && _res[kA].Idle == 20 && !_index.ContainsKey(11)
              && _res.ContainsKey(kB) && _res[kB].Idle == 20 && _pendingHandles.Count == svPendH + 2;
        // over budget: the referenced one is un-idled (handle drop withdrawn), the idle one goes
        Evict();
        ok &= !_res.ContainsKey(kA) && _res.ContainsKey(kB) && _res[kB].Idle == 0 && _bytes == 600 * 1024
              && _idleBytes == 0 && !_pendingHandles.Contains(102);
        _dripFrame = svDrip;
        // the warm fill over a stand-in object
        byte* obj = stackalloc byte[0x100];
        new Span<byte>(obj, 0x100).Clear();
        uint* arr = stackalloc uint[2] { 7, 8 };
        var m = new TexMeta { W = 64, H = 32, Bpp = 4, Compressed = 0, NumX = 2, NumY = 1, Alpha = 0.5f };
        FillWarm((IntPtr)obj, (IntPtr)arr, in m);
        ok &= *(int*)(obj + OffGlId) == 7 && *(short*)(obj + OffNumX) == 2 && *(short*)(obj + OffNumY) == 1
              && *(int*)(obj + OffWidth) == 64 && *(int*)(obj + OffHeight) == 32 && obj[FlagImageBuilt] == 1
              && *(IntPtr*)(obj + OffImage) == IntPtr.Zero && *(float*)(obj + OffAlphaMean) == 0.5f;

        _res.Clear(); foreach (var kv in svRes) _res[kv.Key] = kv.Value;
        _index.Clear(); foreach (var kv in svIdx) _index[kv.Key] = kv.Value;
        _owned.Clear(); foreach (uint o in svOwned) _owned.Add(o);
        _meta.Clear(); foreach (var kv in svMeta) _meta[kv.Key] = kv.Value;
        if (_pending.Count > svPend) _pending.RemoveRange(svPend, _pending.Count - svPend);
        _pendingHandles.Remove(101); _pendingHandles.Remove(102);
        _bytes = svBytes; _idleBytes = svIdle; _budgetBytes = svBudget; _budgetCfgMb = svCfg;
        log.Info(ok ? "OwnedTextures.SelfCheckCache: passed" : "OwnedTextures.SelfCheckCache: FAILED -- keep/evict/filter/fill drifted");
    }

    /// <summary>The TRANSCODE DRIP. The BCn transcode used to run inline in the upload funnel -- a readback
    /// of every level, a CPU block encode and a respecify, on the frame the engine first drew the texture --
    /// and the hitch ledger read it at 2-5ms per texture: the readback is a pipeline sync against a GPU that
    /// is a frame behind, and the encode is a few ms of CPU. It is now a four-stage pipeline, one texture
    /// per frame per stage, none of it waiting on the GPU: (1) the levels are read back into a PBO behind a
    /// fence; (2) frames later, once the fence signals, the PBO is mapped and copied out; (3) an encoder
    /// THREAD encodes the levels; (4) the blocks are uploaded into a NEW name that <see cref="Replace"/>
    /// swaps in. The raw upload draws meanwhile, the old name retires through the ordinary discard path,
    /// and a transcode that fails leaves the raw one standing. <c>bcndrip</c> in sowx.cfg is the per-frame
    /// budget for stage 1; <c>bcn=0</c> still disables the transcode.
    /// ponytail: PBOs and fences are not re-minted across a context loss; the jobs in flight are dropped there
    /// and the textures stay raw.</summary>
    private struct Trans { public ulong Key; public int Etid; public uint Src; public long Bytes; public byte[]? Pixels; public int W, H; public bool Mips; }
    /// <summary>RGBA copies waiting in the queue; past this the texture stays raw rather than holding the
    /// area's whole decoded set in memory at load.</summary>
    private const long TransPixelCap = 256L << 20;
    private static long _transPixelBytes;
    /// <summary>The caster pass's cutout threshold, the alpha under which a texel is a hole.</summary>
    internal const float CoverageAlpha = 0.2f;
    private const int TransQueue = 1024;
    private static readonly Trans[] _transQ = new Trans[TransQueue];
    private static int _transN, _transHead;
    private static long _transDone, _transDropped, _transStale, _transFailed;
    private static int TranscodeBudget = 1;

    private const int JobFree = 0, JobEncoding = 2, JobEncoded = 3;
    private sealed class TransJob
    {
        public ulong Key; public int Etid; public uint Src; public long Bytes;
        public int Levels; public readonly int[] Lw = new int[MaxLevels], Lh = new int[MaxLevels];
        public readonly long[] Off = new long[MaxLevels];
        public byte[] Pixels = Array.Empty<byte>(), Blocks = Array.Empty<byte>();
        public bool Bc3; public long Compressed;
        public int MinF, MagF, WrapS, WrapT, MaxLevel, Aniso;
        public volatile int State;
    }
    private const int TransJobs = 4;
    private static readonly TransJob[] _jobs = new TransJob[TransJobs];
    private static Thread? _encoder;
    private static readonly SemaphoreSlim _encodeWake = new(0, int.MaxValue);

    private static void ReturnPixels(in Trans t) { if (t.Pixels != null) ArrayPool<byte>.Shared.Return(t.Pixels); }

    private static void EnqueueTranscode(ulong key, int etid, uint src, long bytes, byte[] pixels, int w, int h, bool mips)
    {
        if (_transN >= TransQueue || _transPixelBytes + pixels.Length > TransPixelCap) { _transDropped++; ArrayPool<byte>.Shared.Return(pixels); return; }   // stays raw, as bcn=0 would leave it
        _transQ[(_transHead + _transN++) % TransQueue] = new Trans { Key = key, Etid = etid, Src = src, Bytes = bytes, Pixels = pixels, W = w, H = h, Mips = mips };
        _transPixelBytes += pixels.Length;
        _inFlight.Add(key);
    }

    /// <summary>One frame of the pipeline: uploads first (they free jobs), then fence checks, then new
    /// readbacks up to the budget. Engine context, on the <see cref="DripRebuilds"/> anchor; the engine's
    /// bind cache and pixel-store state are restored around every stage.</summary>
    private static void DripTranscodes()
    {
        if (Gl.GenTextures == null || Gl.BindTexture == null || Gl.GetIntegerv == null || Gl.CompressedTexImage2D == null) return;
        if (_transN == 0 && !AnyJob()) return;
        long t0 = FrameZones.Now;
        for (int j = 0; j < TransJobs; j++)
        {
            _jobs[j] ??= new TransJob();
            var job = _jobs[j];
            long ts = FrameZones.Now;
            if (job.State == JobEncoded) { UploadJob(job); FrameZones.Add(FrameZones.Zone.BcnUp, ts); }
        }
        for (int spent = 0; spent < TranscodeBudget && _transN > 0; spent++)
        {
            TransJob? job = null;
            for (int j = 0; j < TransJobs && job == null; j++) if (_jobs[j].State == JobFree) job = _jobs[j];
            if (job == null) break;
            Trans t = _transQ[_transHead];
            _transQ[_transHead] = default;   // the queue holds no pixels past this point
            _transHead = (_transHead + 1) % TransQueue; _transN--;
            _transPixelBytes -= t.Pixels?.Length ?? 0;
            if (!_res.TryGetValue(t.Key, out Entry e) || e.Owned != t.Src) { _transStale++; _inFlight.Remove(t.Key); spent--; ReturnPixels(t); continue; }
            long tr = FrameZones.Now;
            StartJob(job, t);
            ReturnPixels(t);
            FrameZones.Add(FrameZones.Zone.BcnRead, tr);
            if (job.State == JobFree) _inFlight.Remove(t.Key);   // refused or failed at start: raw, and warm-eligible again
        }
        FrameZones.Add(FrameZones.Zone.Bcn, t0);
    }

    private static bool AnyJob()
    {
        for (int j = 0; j < TransJobs; j++) if (_jobs[j] != null && _jobs[j].State != JobFree) return true;
        return false;
    }

    /// <summary>Stage the job from the funnel's RGBA copy: lay the chain out, copy level 0, take the filter
    /// state off the source name, and hand the encoder the mip build with the encode. No readback.</summary>
    private static void StartJob(TransJob job, in Trans t)
    {
        job.Key = t.Key; job.Etid = t.Etid; job.Src = t.Src; job.Bytes = t.Bytes;
        if (t.Pixels == null || t.W < TranscodeFloor || t.H < TranscodeFloor) { _transStale++; return; }
        long total;
        fixed (int* lw = job.Lw, lh = job.Lh) fixed (long* off = job.Off)
            job.Levels = TexScan.Chain(t.W, t.H, t.Mips, MaxLevels, lw, lh, off, out total);
        int savedBind; Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &savedBind);
        Gl.BindTexture(Gl.TEXTURE_2D, t.Src);
        fixed (int* mn = &job.MinF, mg = &job.MagF, ws = &job.WrapS, wt = &job.WrapT, ml = &job.MaxLevel, an = &job.Aniso)
        {
            Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, mn);
            Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, mg);
            Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, ws);
            Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, wt);
            Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, ml);
            Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_ANISOTROPY, an);
        }
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)savedBind);
        if (job.Pixels.Length < total) job.Pixels = new byte[total];   // cold: grows to the largest texture seen
        Buffer.BlockCopy(t.Pixels, 0, job.Pixels, 0, t.W * t.H * 4);
        long blk = 0;
        for (int i = 0; i < job.Levels; i++) blk += BcnEncode.LevelSize(job.Lw[i], job.Lh[i], true);
        if (job.Blocks.Length < blk) job.Blocks = new byte[blk];
        job.State = JobEncoding;
        if (_encoder == null)
        {
            _encoder = new Thread(EncodeLoop) { IsBackground = true, Name = "sowx-bcn", Priority = ThreadPriority.BelowNormal };
            _encoder.Start();
        }
        _encodeWake.Release();
    }
    private static void EncodeLoop()
    {
        Affinity.ClearThis();
        while (true)
        {
            _encodeWake.Wait();
            for (int j = 0; j < TransJobs; j++)
            {
                var job = _jobs[j];
                if (job == null || job.State != JobEncoding) continue;
                fixed (byte* px = job.Pixels)
                fixed (byte* blocks = job.Blocks)
                fixed (int* lw = job.Lw, lh = job.Lh) fixed (long* offs = job.Off)
                {
                    TexScan.BuildMips(px, job.Levels, lw, lh, offs);
                    bool bc3 = false;
                    for (long i = 3, end = (long)job.Lw[0] * job.Lh[0] * 4; i < end; i += 4)
                        if (px[i] != 255) { bc3 = true; break; }
                    long off = 0;
                    for (int i = 0; i < job.Levels; i++)
                    {
                        BcnEncode.EncodeLevel(px + job.Off[i], job.Lw[i], job.Lh[i], bc3, blocks + off);
                        job.Off[i] = off;   // re-used: the level's offset in the BLOCK buffer from here on
                        off += BcnEncode.LevelSize(job.Lw[i], job.Lh[i], bc3);
                    }
                    job.Bc3 = bc3; job.Compressed = off;
                }
                job.State = JobEncoded;
            }
        }
    }

    /// <summary>Stage 4: a new name, the source's sampler state, the compressed levels, and the swap.</summary>
    private static void UploadJob(TransJob job)
    {
        job.State = JobFree;   // whatever happens below, the job is spent
        _inFlight.Remove(job.Key);
        if (!_res.TryGetValue(job.Key, out Entry e) || e.Owned != job.Src) { _transStale++; return; }
        uint dst; Gl.GenTextures(1, &dst);
        if (dst == 0) { _transFailed++; return; }
        int savedBind; Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &savedBind);
        int svUnpackBuf; Gl.GetIntegerv(Gl.PIXEL_UNPACK_BUFFER_BINDING, &svUnpackBuf);
        if (svUnpackBuf != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0);
        DrainErrors();
        Gl.BindTexture(Gl.TEXTURE_2D, dst);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, job.MinF);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, job.MagF);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, job.WrapS);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, job.WrapT);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, job.MaxLevel);
        if (job.Aniso > 0) Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_ANISOTROPY, job.Aniso);
        uint fmt = job.Bc3 ? Gl.COMPRESSED_RGBA_S3TC_DXT5 : Gl.COMPRESSED_RGB_S3TC_DXT1;
        // Immutable storage for the whole chain, then one sub-image per level. Defining mutable levels one by
        // one read as BcnUp 2,5-4,7ms per texture: the driver re-creates the miptree on every level it learns about.
        bool immutable = Gl.TexStorage2D != null && Gl.CompressedTexSubImage2D != null;
        if (immutable)
        {
            Gl.TexStorage2D(Gl.TEXTURE_2D, job.Levels, fmt, job.Lw[0], job.Lh[0]);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, Math.Min(job.MaxLevel, job.Levels - 1));
        }
        fixed (byte* blocks = job.Blocks)
            for (int i = 0; i < job.Levels; i++)
            {
                int size = BcnEncode.LevelSize(job.Lw[i], job.Lh[i], job.Bc3);
                if (immutable) Gl.CompressedTexSubImage2D(Gl.TEXTURE_2D, i, 0, 0, job.Lw[i], job.Lh[i], fmt, size, blocks + job.Off[i]);
                else Gl.CompressedTexImage2D(Gl.TEXTURE_2D, i, fmt, job.Lw[i], job.Lh[i], 0, size, blocks + job.Off[i]);
            }
        uint err = Gl.GetError();
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)savedBind);
        if (svUnpackBuf != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, (uint)svUnpackBuf);
        if (err != 0)
        {
            if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dst);
            _transFailed++;
            if (++_bcnFails >= BcnFailCap)
            {
                _bcn = false;
                _log?.Info($"OwnedTextures: BCn transcode DISABLED -- {_bcnFails} driver refusals (last 0x{err:x}); uploads stay raw until the next context");
            }
            return;
        }
        Replace(job.Key, job.Etid, dst, job.Compressed);
        FrameZones.Count(FrameZones.Ev.Transcode);
        _transDone++;
        _censusTrans++; _censusSaved += job.Bytes > job.Compressed ? job.Bytes - job.Compressed : 0;
    }

    internal static string TranscodeNote()
        => $"transcode drip: {_transDone} done, {_transN} queued, {_transStale} stale, {_transFailed} failed, {_transDropped} dropped past the queue, budget {TranscodeBudget}/frame";

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

    private static bool _bcn, _bcnWanted;
    private static int _bcnFails;
    /// <summary>A driver that refuses one S3TC upload refuses them all; three strikes covers a transient.</summary>
    private const int BcnFailCap = 3;

    /// <summary>#265's number, measured before anything is built: per area, how many textures (and how many
    /// bytes) come down the engine's UNCOMPRESSED upload path -- <c>basic+0x70</c> == 0, the
    /// <c>GetCompressedSize</c> @0x484b00 verdict that picks <c>glTexImage2D</c> over
    /// <c>glCompressedTexImage2D</c> -- versus the already-BCn path. The uncompressed share is the whole
    /// BCn-transcode prize; the issue forbids building the transcode without this line.</summary>
    private static readonly int OffCompressedSize = EngineLayout.TexBasicCompressedSize;
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
                                      : _pltForced == 0 ? "" : $", GPU-PLT re-cleared {_pltForced}x")
                   // #360: the copy path is never silent -- a session that adopted nothing and a session
                   // that has no GPU composites to adopt must not read the same.
                   + (_pltCopied + _pltCopyRefused + _pltCopyDied == 0 ? ""
                      : $", GPU-PLT copied {_pltCopied} (refused {_pltCopyRefused}, source died {_pltCopyDied})")
                   + ")");
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
        // #358: the [vram] line carries the count, but it only prints when the high-water GROWS -- so after
        // an area has settled the count reads stale, and "re-cleared 1x" looked like proof the flag never
        // came back when it was only proof nobody had looked since. The re-clears that matter are the ones
        // AFTER Attach's, they are cold, and there are a handful of them: say each one out loud.
        if (_pltForced++ > 0 && _pltDiagLeft > 0)
        {
            _pltDiagLeft--;
            _log?.Info($"OwnedTextures: g_bGeneratePLTOnGPU was set back to 1 -- re-cleared ({_pltForced}x). "
                     + (_origConstructImage != null && _origGlImageBasic != null
                        ? "Held at the readers, so no PLT texture is rebuilt outside the upload funnel (#358)"
                        : "The readers are NOT hooked, so a rebuild in this same engine step is GPU-composited "
                        + "and its character stays BORROWED until something re-creates it (#358)"));
        }
    }

    /// <summary>A small budget of named re-clears: the population is what matters and it is small.</summary>
    private static int _pltDiagLeft = 8;

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
        DripTranscodes();
        if (_buildAndStore == null) return;
        long tp = FrameZones.Now;
        DripPrefetch();
        FrameZones.Add(FrameZones.Zone.Prebuild, tp);
        for (int spent = 0; spent < RebuildBudget && _rebuildN > 0; spent++)
        {
            IntPtr tex = _rebuildQ[--_rebuildN];
            _rebuildQ[_rebuildN] = IntPtr.Zero;
            FrameZones.Count(FrameZones.Ev.TexBuild);
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
    private static readonly int BasicScheme = EngineLayout.TexBasicSchemes, BasicImage = EngineLayout.TexBasicImage,
        BasicBuilt = EngineLayout.TexBasicFlagBuilt, BasicInvalid = EngineLayout.TexBasicFlagNoImage;
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
        _owned.Remove(e.Owned); _stats.Remove(e.Owned);
        if (_engineHeld.Contains(e.Owned)) _pendingHandles.Add(e.Owned);   // the engine deletes it; we only drop the handle
        else { _pending.Add(e.Owned); _pendingMarked = false; }
        _bytes -= e.Bytes;
    }

    /// <summary>Adopt a freshly uploaded texture as the resource behind <paramref name="key"/>, retiring
    /// whatever held that key before. Split out from <see cref="Adopt"/>, whose other half is GL, so
    /// <see cref="SelfCheck"/> can assert the table mutation rather than a copy of it.</summary>
    private static void Replace(ulong key, int etid, uint owned, long bytes)
    {
        uint gen = 0; int idle = 0;
        if (_res.TryGetValue(key, out Entry old))
        {
            gen = old.Gen + 1; idle = old.Idle;
            if (idle != 0) _idleBytes += bytes - old.Bytes;
            Discard(old);
            _reuploads++;
            AlloyRenderer.NoteOwnedNameSwapped(old.Owned, owned);   // #386: records take the new name in place, no re-stage
        }
        _res[key] = new Entry(owned, bytes, gen, idle);
        _evictDue = true;
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
        if (Budget() > 0 && names != null && n > 0 && n <= 4096)
        {
            // ADR 0023: names the engine holds that are OURS (a warm build handed them out) must survive its
            // Reset -- they are the cache. Only the rest reach the driver.
            uint* pass = stackalloc uint[(int)n];
            FilterRetire(n, names, out uint passN, pass);
            if (passN > 0 && _origDelete != null) _origDelete(passN, pass);
            return;
        }
        Retire(n, names);
        if (_origDelete != null) _origDelete(n, names);
    }

    /// <summary>The cache-mode delete: owned names are kept (and counted), engine names retire their index
    /// entry and put the resource to IDLE rather than discarding it. Returns the kept count; <paramref name="pass"/>
    /// (if given) receives the names the driver may still delete.</summary>
    private static uint FilterRetire(uint n, uint* names, out uint passN, uint* pass = null)
    {
        uint kept = 0; passN = 0;
        for (uint i = 0; i < n; i++)
        {
            uint name = names[i];
            _engineHeld.Remove(name);
            bool ours = _owned.Contains(name);
            if (ours) { kept++; _cacheKept++; }   // the GL name survives; only its INDEX entry retires below
            else
            {
                if (pass != null) pass[passN] = name;
                passN++;
                _borrowed.Remove((int)name);
                DropQueued(name);
            }
            if (!_index.TryGetValue((int)name, out ulong key)) continue;
            _index.Remove((int)name);
            _retired++;
            if (!_res.TryGetValue(key, out Entry e) || e.Idle != 0) continue;
            // Marked idle without asking whether another tid still points at it: that is an O(index) question
            // per name on the transition frame. Evict asks it once per pass and un-idles the ones still referenced.
            _res[key] = new Entry(e.Owned, e.Bytes, e.Gen, Math.Max(_dripFrame, 1));
            _idleBytes += e.Bytes;
            _pendingHandles.Add(e.Owned);   // #153's residency is per name; an idle resource holds no handle slot
            _evictDue = true;
        }
        return kept;
    }

    /// <summary>The delete handler's body, split out so <see cref="SelfCheck"/> can exercise it.</summary>
    private static void Retire(uint n, uint* names)
    {
        if (names == null) return;
        for (uint i = 0; i < n; i++)
        {
            int name = (int)names[i];
            _engineHeld.Remove((uint)name);
            _borrowed.Remove(name);
            DropQueued((uint)name);   // #360: a queued copy of a name the engine just freed reads a recycle
            if (!_index.TryGetValue(name, out ulong key)) continue;
            _index.Remove(name);
            _retired++;
            if (!_res.TryGetValue(key, out Entry e)) continue;
            _res.Remove(key);
            // The engine is deleting this very name NOW (its cell was ours): a second delete from Drain lands
            // on whatever the driver has since given that name to -- immediate on Mesa, which is the
            // corrupted-UI-texture class. The handle is forgotten without GL: its texture is already gone.
            if (e.Owned == (uint)name) { _owned.Remove(e.Owned); _stats.Remove(e.Owned); _bytes -= e.Bytes; _pendingForget.Add(e.Owned); }
            else Discard(e);
        }
    }
    /// <summary>Owned names the engine deleted itself: records marked, handle tombstoned, no GL.</summary>
    private static readonly HashSet<uint> _pendingForget = new();
    /// <summary>Names whose delete is held for <see cref="DeferFrames"/> drains: the last draw that sampled
    /// their handle may still be in flight, and a driver that resolves bindless descriptors at batch
    /// completion (zink) faults on a destroyed image there. The records already carry the new name.</summary>
    private static readonly List<uint>[] _deferred = { new(), new(), new(), new() };
    private const int DeferFrames = 3;
    private static int _drainSeq;

    /// <summary>#220: is this name one of ours? The bindless path mints a handle only when it is -- a handle
    /// on an engine texture is a handle on an object the engine may delete or re-upload under us, which is
    /// the whole of #220.</summary>
    internal static bool IsOwned(int name) => name > 0 && _owned.Contains((uint)name);

    /// <summary><see cref="Draw"/> without the borrowed-set write: false when Draw would have recorded a
    /// borrow (a mutation, possibly an allocation), so a worker defers that part to the main thread.</summary>
    internal static bool TryDraw(int engineTid, out int draw)
    {
        draw = engineTid;
        if (!_armed || engineTid <= 0) return true;
        if (_index.TryGetValue(engineTid, out ulong key) && _res.TryGetValue(key, out Entry e)) { draw = (int)e.Owned; return true; }
        return false;
    }
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
        DrainPltCopies();
        // Evict FIRST: its discards join this frame's batch, and its un-idle of a referenced key keeps that
        // handle (it used to remove from a set already dropped and cleared). Then the per-slot marks for the
        // whole batch, then the drops -- no epoch bump anywhere on this path.
        if (_evictDue) Evict();
        DropDeadHandles();
        // this drain's names are held; the ones queued DeferFrames drains ago go now
        int slot = _drainSeq++ % _deferred.Length;
        List<uint> due = _deferred[(slot + _deferred.Length - DeferFrames) % _deferred.Length];
        if (_pending.Count > 0) { _deferred[slot].AddRange(_pending); _pending.Clear(); }
        if (due.Count == 0) return;
        int n = due.Count;
        // auto: deletes are only unsafe where zink's bindless bookkeeping is live -- vetoed bindless = no handles = safe
        bool delete = _texDelete == 1 || (_texDelete < 0 && !(GlContextService.Zink && AlloyRenderer.Bindless));
        if (!delete && !_deleteNoted) { _deleteNoted = true; _log?.Info("OwnedTextures: owned textures are NEVER deleted this session (zink with bindless forced, or texdelete=0) -- names leak until the context goes"); }
        for (int i = 0; i < n; i++)
        {
            uint name = due[i];
            if (_handleDrop) AlloyRenderer.DropHandle(name);   // residency first: a resident handle outlives its texture
            else AlloyRenderer.ForgetHandle(name);
            if (delete && Gl.DeleteTextures != null) Gl.DeleteTextures(1, &name);
        }
        due.Clear();
        _log?.Info($"OwnedTextures: {(delete ? "released" : "retired (kept alive)")} {n} owned textures (engine names retired so far: {_retired})");
    }

    /// <summary>The handle half of <see cref="Drain"/>, callable ahead of it: the caster bake runs BEFORE the
    /// colour bracket's drain in the same frame, and the engine deletes textures on area load before either --
    /// so a bake that samples the handle table must first drop (and zero out of the records) every handle
    /// whose texture died since the last drain. Idempotent within a frame: the sets are cleared as they are
    /// processed, and the deferred names are marked once per addition. Inside a 4.6 bracket.</summary>
    internal static void DropDeadHandles()
    {
        if (_pendingForget.Count == 0 && _pendingHandles.Count == 0 && _pendingMarked) return;
        if (_pendingForget.Count > 0)
        {
            foreach (uint f in _pendingForget) _pendingHandles.Add(f);   // for the record marks below
            AlloyRenderer.NoteOwnedNamesRetired(_pending, _pendingHandles);
            foreach (uint f in _pendingForget) { _pendingHandles.Remove(f); AlloyRenderer.ForgetHandle(f); }
            _pendingForget.Clear();
        }
        else AlloyRenderer.NoteOwnedNamesRetired(_pending, _pendingHandles);
        _pendingMarked = true;
        if (_pendingHandles.Count > 0)
        {
            foreach (uint h in _pendingHandles) AlloyRenderer.DropHandle(h);
            _pendingHandles.Clear();
        }
    }
    /// <summary>Every name in <c>_pending</c> has had its records marked; cleared by the one adder.</summary>
    private static bool _pendingMarked;

    /// <summary>#360: adopt every queued GPU composite. Runs from <see cref="Drain"/>, i.e. inside the 4.6
    /// bracket, which is the only place <c>glCopyImageSubData</c> is legal and the first place the source is
    /// guaranteed published across the share-list.
    /// <para><b>Why this is not #211's copy path.</b> That one keyed on the ENGINE's GL name, so a texture
    /// re-uploaded under a name already copied served the previous image until the next world edge. This
    /// keys on the same content identity every upload does and is re-taken at the engine's own rebuild
    /// funnel, so a re-composite mints a new owned name and bumps the generation exactly as a re-upload
    /// does. The mechanism is the same three GL calls; the correctness is entirely in the key.</para>
    /// <para>Sampler state does NOT ride storage. Filter, wrap, the mip range and the component swizzle are
    /// carried across explicitly -- a copy that samples differently from the original is not a copy, and the
    /// swizzle in particular is what the engine sets for its single-channel formats.</para></summary>
    private static void DrainPltCopies()
    {
        if (_pltQ.Count == 0) return;
        if (Gl.CopyImageSubData == null || Gl.TexStorage2D == null || Gl.GenTextures == null
            || Gl.BindTexture == null || Gl.GetIntegerv == null || Gl.GetTexLevelParameteriv == null
            || Gl.GetTexParameteriv == null || Gl.TexParameteri == null)
        {
            foreach (PltCopy q in _pltQ) { _borrowed.Add((int)q.Src); _pltCopyRefused++; }
            _pltQ.Clear();
            // Its own latch, not the per-texture budget: a driver with no copy path refuses EVERY composite,
            // and sharing the budget would spend it all on the first character and hide the second class of
            // failure behind the first.
            if (!_pltNoCopyLogged)
            {
                _pltNoCopyLogged = true;
                _log?.Info("OwnedTextures: GPU-PLT copy unavailable -- this driver has no glCopyImageSubData "
                         + "or no glTexStorage2D, so every GPU composite stays BORROWED (#360)");
            }
            return;
        }

        int savedBind; Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &savedBind);
        for (int i = 0; i < _pltQ.Count; i++) Copy(_pltQ[i]);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)savedBind);
        _pltQ.Clear();
    }

    /// <summary>Texture parameters that must survive the copy. Read off the source, written to the
    /// destination, in this order -- the four sampler dials, the mip range, and the four swizzle
    /// channels.</summary>
    private static readonly uint[] CarriedParams =
    {
        Gl.TEXTURE_MIN_FILTER, Gl.TEXTURE_MAG_FILTER, Gl.TEXTURE_WRAP_S, Gl.TEXTURE_WRAP_T,
        Gl.TEXTURE_BASE_LEVEL, Gl.TEXTURE_MAX_LEVEL,
        Gl.TEXTURE_SWIZZLE_R, Gl.TEXTURE_SWIZZLE_G, Gl.TEXTURE_SWIZZLE_B, Gl.TEXTURE_SWIZZLE_A,
    };

    /// <summary>One queued composite. Every refusal is counted and the source is left BORROWED, which is the
    /// state it was already in -- a refused copy costs the character nothing it had.</summary>
    private static void Copy(PltCopy q)
    {
        Gl.BindTexture(Gl.TEXTURE_2D, q.Src);
        DrainErrors();   // the verdict below is ours to read

        int levels = 0, fmt = 0;
        int* lw = stackalloc int[MaxLevels], lh = stackalloc int[MaxLevels];
        for (; levels < MaxLevels; levels++)
        {
            int w = 0, h = 0;
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, levels, Gl.TEXTURE_WIDTH, &w);
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, levels, Gl.TEXTURE_HEIGHT, &h);
            if (w <= 0 || h <= 0) break;
            lw[levels] = w; lh[levels] = h;
        }
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        // An UNSIZED answer cannot be handed to glTexStorage2D, and guessing a promotion is what changed the
        // sampler swizzle under #211. Refused, named, and left borrowed instead.
        if (levels == 0 || fmt == 0 || fmt == (int)Gl.RGBA || fmt == (int)Gl.RGB)
        {
            _borrowed.Add((int)q.Src); _pltCopyRefused++;
            if (_pltCopyDiagLeft-- > 0)
                _log?.Info($"OwnedTextures: GPU-PLT tid {q.Src} not copied -- levels={levels} "
                         + $"internalformat=0x{fmt:x} (unsized or unreadable), left BORROWED (#360)");
            return;
        }

        int* saved = stackalloc int[CarriedParams.Length];
        for (int i = 0; i < CarriedParams.Length; i++)
            Gl.GetTexParameteriv(Gl.TEXTURE_2D, CarriedParams[i], saved + i);

        uint dst;
        Gl.GenTextures(1, &dst);
        if (dst == 0) { _borrowed.Add((int)q.Src); _pltCopyRefused++; return; }
        Gl.BindTexture(Gl.TEXTURE_2D, dst);
        Gl.TexStorage2D(Gl.TEXTURE_2D, levels, (uint)fmt, lw[0], lh[0]);
        for (int i = 0; i < CarriedParams.Length; i++)
            Gl.TexParameteri(Gl.TEXTURE_2D, CarriedParams[i], saved[i]);
        for (int l = 0; l < levels; l++)
            Gl.CopyImageSubData(q.Src, Gl.TEXTURE_2D, l, 0, 0, 0, dst, Gl.TEXTURE_2D, l, 0, 0, 0,
                                lw[l], lh[l], 1);

        uint err = Gl.GetError();       // read BEFORE Measure, whose own queries would mask it
        if (err != 0)
        {
            if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dst);
            _borrowed.Add((int)q.Src); _pltCopyRefused++;
            if (_pltCopyDiagLeft-- > 0)
                _log?.Info($"OwnedTextures: GPU-PLT tid {q.Src} not copied -- the copy gave 0x{err:x} "
                         + $"({lw[0]}x{lh[0]}, {levels} levels, 0x{fmt:x}), left BORROWED (#360)");
            return;
        }

        Replace(q.Key, (int)q.Src, dst, Measure());
        _stats[dst] = new Stat { Holes = 0f };   // PLT composites are opaque; unscanned would read as masked
        _pltCopied++;
        HighWater();
    }

    /// <summary>The GL-NAME edge: the objects went with the context. Names are dropped, nothing is deleted --
    /// deleting into a dead context is what #209 calls the terminal edge.</summary>
    internal static void ContextLost()
    {
        _res.Clear();
        _index.Clear();
        _owned.Clear();
        _borrowed.Clear();
        _pending.Clear(); _pendingForget.Clear(); foreach (var d in _deferred) d.Clear();   // the names died with the context
        _pltQ.Clear();   // #360: queued sources are names in a context that no longer exists
        for (int i = 0; i < _transN; i++) ReturnPixels(_transQ[(_transHead + i) % TransQueue]);
        _transN = 0; _transHead = 0; _transPixelBytes = 0; Array.Clear(_transQ);   // the queue names owned textures of the dead context
        _stats.Clear();
        _meta.Clear(); _inFlight.Clear(); _pendingHandles.Clear(); _idleBytes = 0; _evictDue = false;
        _prefetchN = 0; _prefetchHead = 0; _prefetchSeen.Clear(); _engineHeld.Clear();
        for (int j = 0; j < TransJobs; j++)
            if (_jobs[j] != null)
            {   // a job still encoding finishes on its thread; its upload then finds no live key and drops out
                if (_jobs[j].State != JobEncoding) _jobs[j].State = JobFree;
            }
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
        if (!TexScan.SelfCheck(log)) return false;
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
        const ulong k0 = 0x5AF300000000UL, k1 = k0 | 1;   // not-an-offset: splitmix64 mixing constants
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
        Retire(3, dead);   // the budget-0 edge: Retire discards (FilterRetire is the cache edge, checked in SelfCheckCache)
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
        // #360: the queue's two edges, off-device. A copy that outlives the name it was queued against
        // reads a recycled texture, which is the whole of what killed #211's copy path -- so the drop is
        // asserted here, and asserted to be exact rather than a wipe.
        int svQDied = (int)_pltCopyDied;
        var svQ = new List<PltCopy>(_pltQ);
        _pltQ.Clear();
        _pltQ.Add(new PltCopy(41, 100)); _pltQ.Add(new PltCopy(42, 200)); _pltQ.Add(new PltCopy(41, 300));
        uint deadName = 41;
        Retire(1, &deadName);
        ok &= _pltQ.Count == 1 && _pltQ[0].Src == 42 && _pltCopyDied == svQDied + 2;
        _pltQ.Clear();
        foreach (PltCopy q in svQ) _pltQ.Add(q);
        _pltCopyDied = svQDied;

        // #358: the hold itself, over a stand-in flag. A re-clear that does not actually zero the global is
        // the whole bug wearing a fixed label, and the readers' trampolines are two lines around this call.
        int stand = 1;
        int* svPlt = _pltOnGpu; long svForced = _pltForced; int svDiag = _pltDiagLeft;
        _pltOnGpu = &stand; _pltForced = 1; _pltDiagLeft = 0;      // budget spent: no log line from a check
        KeepPltOnCpu();
        ok &= stand == 0 && _pltForced == 2;
        KeepPltOnCpu();
        ok &= _pltForced == 2;                                      // already zero: nothing to re-clear
        _pltOnGpu = svPlt; _pltForced = svForced; _pltDiagLeft = svDiag;

        _armed = wasArmed;
        _retired = savedRetired;   // the check's own synthetic deletes are not a session's
        log.Info(ok ? "OwnedTextures.SelfCheck: passed" : "OwnedTextures.SelfCheck: FAILED");
        return ok;
    }
}
