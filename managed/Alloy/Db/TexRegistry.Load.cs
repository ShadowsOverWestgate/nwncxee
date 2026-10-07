using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Framework;
using CX.Graphics;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy.Db;

/// <summary>CX-117: the registry's load pipeline. Four hops, and which thread owns each is the contract:
/// <list type="number">
/// <item><b>Game thread</b> -- <see cref="Enqueue"/>/<see cref="Offer"/> resolve the resref to a
/// <see cref="TexLocation"/> VALUE record (<c>CExoResMan</c> is not thread-safe and a key entry is not a
/// stable handle), so every engine-memory read in the pipeline happens here.</item>
/// <item><b>The <c>cx-tex</c> worker</b> -- <see cref="Work"/> reads the bytes and decodes them. Pure.</item>
/// <item><b>Alloy's 4.6 bracket</b> -- <see cref="Drain"/> uploads, mints the bindless handle and deletes
/// retired names. GL only; it publishes each finished slot to <see cref="_landed"/>.</item>
/// <item><b>Game thread again</b> -- <see cref="Tick"/> files what landed (which writes
/// <see cref="SceneDb"/>'s dirty lists), offers what was waiting, and evicts past the budget.</item>
/// </list>
/// Arrival then marks every dependent part record, which is what replaces the staging walk's per-frame
/// texture re-resolve. Before review r1 the bracket did the game thread's half too, which was safe only
/// while <c>GlContextService</c> made the context current on the game thread -- and
/// <c>Db/RenderThread.cs</c> exists to stop doing that.</summary>
internal static unsafe partial class TexRegistry
{
    /// <summary>Resource types: TGA, DDS and PLT (<c>CResHelper&lt;CResPLT,6&gt;</c>). The registry asks for
    /// the PLT first for a <see cref="TexKind.Plt"/> slot and the DDS/TGA pair otherwise, which is the order
    /// the engine's own texture loader uses.</summary>
    private const ushort TgaResType = 3, PltResType = 6, DdsResType = 0x7f1;   // not-an-offset: restype ids

    /// <summary>Loads in flight at once. The queue IS the budget: past it an acquire stays
    /// <see cref="TexState.Loading"/> and is offered on a later <see cref="Drain"/>.</summary>
    private const int MaxPending = 64;
    /// <summary>Uploads per bracket. An upload is a driver allocation plus a copy, so a whole area's arrival
    /// is spread over frames rather than spent in one.</summary>
    private const int UploadsPerFrame = 8;

    private sealed class Job
    {
        public int Slot;
        public uint Gen;
        public string Resref = "";
        public TexKind Kind;
        public TexLocation Loc;
        public uint ResId0, ResId1, ResId2, ResId3;
        /// <summary>The decoded bytes, native and owned by the job until the upload or the recycle.</summary>
        public byte* Data;
        public DdsReader.Info Dds;
        public int W, H, Levels, Channels;
        /// <summary>The GL internal format and the (format, type) pair of an uncompressed upload.</summary>
        public uint GlFmt, PixFmt, PixType;
        public bool Compressed, GenMips, Clamp, Nearest, Mip;
        public TexCoverage Cov;
        /// <summary>The image's mean colour (its own encoding), for the sky-matched fog; valid when MeanOk.</summary>
        public System.Numerics.Vector3 Mean;
        public bool MeanOk;
        /// <summary>The slot's <c>.txi</c> names a <c>bumpyshinytexture</c>, which is the engine's own route
        /// into the water class. <see cref="TxiNamesBumpyShiny"/>.</summary>
        public bool BumpyShiny;
        /// <summary>What the texture's own <c>.txi</c> RESOURCE said, read on the game thread at the offer
        /// (the worker may not touch the engine's resource tables).</summary>
        public bool BumpyShinyTxi;
        /// <summary>The slot's <c>.txi</c> names an <c>envmaptexture</c>, which is what makes the engine's
        /// material env-MAPPED (<c>Material::ParseField</c> @0x5c5850 stores it at <c>+0xd</c>,
        /// <c>Material::HasEnvMap</c> @0x5c6180 answers for it) -- the "this is shiny 2002 content" flag the
        /// lit stage spends the diffuse alpha as reflectivity on. <see cref="TxiNamesEnvMap"/>.</summary>
        public bool EnvMap;
        /// <inheritdoc cref="BumpyShinyTxi"/>
        public bool EnvMapTxi;
        public long Bytes;
        public string? Why;
    }

    /// <summary>The <c>.txi</c> keyword the water class is decided on, as the engine reads it:
    /// <c>Material::Init</c> @0x5c5b90 parses the diffuse's <c>.txi</c> and <c>Material::ParseField</c>
    /// @0x5c5850 stores a <c>bumpyshinytexture</c> name, which <c>Material::InitializeTextures</c> turns into
    /// the texture <c>Material::HasBumpyShinyMap</c> @0x5c5ca0 answers for -- the term
    /// <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 files a part into draw bucket 6 on.</summary>
    private static ReadOnlySpan<byte> BumpyShinyKey => "bumpyshinytexture"u8;
    /// <summary>The <c>.txi</c> keyword a material's environment map is declared with:
    /// <c>Material::ParseField</c> @0x5c5850 matches <c>envmaptexture</c> and parses it into the field
    /// <c>Material::HasEnvMap</c> @0x5c6180 reads, which is <c>PartTriMesh::Draw</c> @0x4513d0's own routing
    /// condition into the environment-mapped branch.</summary>
    private static ReadOnlySpan<byte> EnvMapKey => "envmaptexture"u8;

    /// <summary>Bytes at the end of a texture resource that the <c>.txi</c> block can live in. The shipped
    /// blocks are a handful of lines; this is the window, not a budget.</summary>
    private const int TxiTailBytes = 64 * 1024;

    /// <summary>Does this texture resource's appended <c>.txi</c> name a <c>bumpyshinytexture</c>?
    /// <para>Scanned out of the resource's own tail because that is where the shipped data is: the stock water
    /// textures carry their <c>.txi</c> inside the TGA, after the image (verified in
    /// <c>data/os_textures.bif</c>: <c>bumpyshinytexture ttr01__env</c> / <c>bumpmaptexture shinywater</c>).
    /// Case-insensitive, like the engine's <c>strcasecmp</c>.</para>
    /// <para><c>ponytail:</c> a texture whose <c>.txi</c> is a SEPARATE resource (the engine's
    /// <c>AurResGet(name, ".txi")</c> would find it, a tail scan does not) is not recognised, so such a part
    /// stays in the class its own alpha gives it. The upgrade is locating the <c>.txi</c> resource on the game
    /// thread at intern -- the worker may not read the engine's resource tables.</para></summary>
    private static bool TxiNamesBumpyShiny(string? txi)
        => txi != null && txi.Contains("bumpyshinytexture", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc cref="TxiNamesBumpyShiny(string)"/>
    private static bool TxiNamesEnvMap(string? txi)
        => txi != null && txi.Contains("envmaptexture", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc cref="TxiNamesBumpyShiny(string)"/>
    private static bool TxiNamesBumpyShiny(ReadOnlySpan<byte> raw) => TxiNames(raw, BumpyShinyKey);

    /// <inheritdoc cref="TxiNamesBumpyShiny(string)"/>
    private static bool TxiNamesEnvMap(ReadOnlySpan<byte> raw) => TxiNames(raw, EnvMapKey);

    /// <summary>Does the resource's appended <c>.txi</c> tail name <paramref name="key"/>? Case-insensitive,
    /// like the engine's <c>strcasecmp</c>.</summary>
    private static bool TxiNames(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> key)
    {
        ReadOnlySpan<byte> tail = raw.Length > TxiTailBytes ? raw[^TxiTailBytes..] : raw;
        for (int i = 0; i + key.Length <= tail.Length; i++)
        {
            int k = 0;
            while (k < key.Length && (tail[i + k] | 0x20) == key[k]) k++;   // the keyword is ASCII lower case
            if (k == key.Length) return true;
        }
        return false;
    }

    /// <summary>What the bracket publishes back to the game thread: the slot it uploaded for, the generation
    /// it uploaded against, and either a name and handle or the reason there is none.</summary>
    private readonly record struct Landed(int Slot, uint Gen, uint Name, ulong Handle, long Bytes,
                                         TexCoverage Cov, bool BumpyShiny, bool EnvMap, string? Why);

    private static readonly Queue<Job> _in = new(), _out = new();
    private static readonly Stack<Job> _pool = new();
    private static readonly Queue<int> _waiting = new();
    private static readonly Queue<Landed> _landed = new();
    /// <summary>Slots the bracket re-minted a handle for (a sampler change, or an idle slot acquired again).
    /// <see cref="Tick"/> marks their dependents: a record staged while the handle was 0 drew black.</summary>
    private static readonly Queue<int> _reminted = new();
    private static readonly object _gate = new();
    private static int _pending;
    private static bool _worker;
    private static readonly Handles _handles = new();
    /// <summary>The worker's one decode buffer for the coverage scan, grown to the largest level 0 it has
    /// met. Worker-thread only, like <see cref="_handles"/>: a managed array per texture would put
    /// large-object allocations on it, and NativeAOT's GC stops the main thread for those.</summary>
    private static byte[] _covScratch = Array.Empty<byte>();

    /// <summary>Offer slot <paramref name="i"/>'s load, or queue it for a later <see cref="Tick"/>. Game
    /// thread; takes no table lock, because <see cref="_pend"/> is the only column it writes and only the game
    /// thread writes it.</summary>
    private static void Enqueue(int i)
    {
        if (_pend[i] != PendNone) return;   // already queued or in flight: one offer per load, ever
        if (!ResLocate.Ready && !ResLocate.Attach(out string? why))
        { Fail(i, $"the resource manager is not attached ({why ?? "no reason given"})"); return; }
        bool room;
        lock (_gate) room = _pending < MaxPending;
        if (!room) { _pend[i] = PendWaiting; lock (_gate) _waiting.Enqueue(i); return; }
        Offer(i);
    }

    private static void Offer(int i)
    {
        string resref = _resref[i] ?? "";
        Job j = Rent();
        j.Slot = i; j.Gen = _gen[i] & 0xFFF; j.Resref = resref; j.Kind = _kind[i];
        j.Data = null; j.Why = null; j.W = j.H = j.Levels = 0; j.Bytes = 0; j.Channels = 0;
        string? txi = _kind[i] != TexKind.Plt ? ReadResourceText(resref, TextFontLoad.ResTxi) : null;
        TxiSampler(i, txi);
        j.Compressed = false; j.GenMips = false; j.Clamp = _clamp[i]; j.Cov = TexCoverage.Unknown; j.MeanOk = false;
        j.Nearest = _nearest[i]; j.Mip = _mipmap[i];
        // The material .txi is its OWN resource for most shipped textures -- ttf01_water01 has one, and the
        // engine's Material::Init @0x5c5b90 reads it by name -- so it is looked up here, on the thread that
        // may read the resource tables. The worker scans the image's tail as well, because some textures
        // carry the block appended to the TGA instead.
        j.BumpyShinyTxi = TxiNamesBumpyShiny(txi);
        j.EnvMapTxi = TxiNamesEnvMap(txi);
        uint* resId = stackalloc uint[ResLocate.ResIdWords];
        if (!LocateFor(resref, j.Kind, resId, out j.Loc, out TexKind found, out string? why))
        { Recycle(j); _pend[i] = PendNone; Fail(i, why ?? "no resource"); return; }
        // Auto resolved to whatever the resource tables hold, and that is the slot's kind from here on: a PLT
        // takes the pair decode and the shader resolve, anything else the ordinary one. The slot KEY does not
        // carry the kind, so this cannot split one file across two slots.
        j.Kind = found;
        lock (_lock) _kind[i] = found;
        j.ResId0 = resId[0]; j.ResId1 = resId[1]; j.ResId2 = resId[2]; j.ResId3 = resId[3];
        _pend[i] = PendFlight;
        StartWorker();
        lock (_gate) { _pending++; _in.Enqueue(j); Monitor.Pulse(_gate); }
    }

    /// <summary>The sampler words <c>CAurTextureBasic::ParseField</c> @0x5881e0 reads from the texture's own
    /// <c>.txi</c> -- <c>clamp</c> (+200), <c>mipmap</c> (+0xe4), <c>filter</c> (+0xe5) -- which
    /// <c>GLRender::SetTextureParameters</c> @0x5ca180 applies. No <c>.txi</c> keeps the ctor's 0/1/1.</summary>
    private static void TxiSampler(int i, string? txi)
    {
        if (txi == null) return;
        foreach (string raw in txi.Split('\n'))
        {
            string line = raw.Trim();
            int sp = line.IndexOfAny([' ', '\t']);
            if (sp < 0 || !int.TryParse(line.AsSpan(sp + 1).Trim(), out int v)) continue;
            ReadOnlySpan<char> key = line.AsSpan(0, sp);
            lock (_lock)
            {
                // ponytail: one bool for both axes, so a one-axis clamp (1 or 2) clamps both; per-axis = a bits column.
                if (key.Equals("clamp", StringComparison.OrdinalIgnoreCase)) _clamp[i] = (v & 3) != 0;
                else if (key.Equals("filter", StringComparison.OrdinalIgnoreCase)) _nearest[i] = v == 0;
                else if (key.Equals("mipmap", StringComparison.OrdinalIgnoreCase)) _mipmap[i] = v != 0;
            }
        }
    }

    /// <summary>Where this resref's bytes live, for the kind asked. A PLT slot takes the PLT restype alone --
    /// a PLT and a DDS of the same name are different images and resolving one as the other is how a head
    /// ends up wearing a wall.</summary>
    private static bool LocateFor(string resref, TexKind kind, uint* resId, out TexLocation loc, out TexKind found, out string? why)
    {
        loc = default; why = null; found = TexKind.Plain;
        Span<byte> name = stackalloc byte[ShaderResource.ResRefBytes + 1];
        TexRegistry.ResRef(resref, name);
        Span<ushort> types = stackalloc ushort[3];
        int nTypes;
        if (kind == TexKind.Plt) { types[0] = PltResType; nTypes = 1; }
        else if (kind == TexKind.Plain) { types[0] = DdsResType; types[1] = TgaResType; nTypes = 2; }
        else { types[0] = DdsResType; types[1] = TgaResType; types[2] = PltResType; nTypes = 3; }
        fixed (byte* p = name)
            for (int t = 0; t < nTypes; t++)
            {
                ushort rt = types[t];
                if (!ResLocate.Find(p, rt, out IntPtr table, out IntPtr entry)) continue;
                if (ResLocate.Locate(table, entry, resId, out loc, out string? w, out ResLocate.Source src))
                { found = rt == PltResType ? TexKind.Plt : TexKind.Plain; return true; }
                why ??= w ?? $"restype {rt} is {src}";
            }
        why ??= kind switch
        {
            TexKind.Plt => "no PLT resource of that name",
            TexKind.Plain => "no DDS or TGA resource of that name",
            _ => "no DDS, TGA or PLT resource of that name",
        };
        return false;
    }

    private static Job Rent()
    {
        lock (_gate) if (_pool.Count > 0) return _pool.Pop();
        return new Job();
    }

    private static void Recycle(Job j)
    {
        if (j.Data != null)
        {
            if (j.Kind == TexKind.Plain && j.Compressed) DdsReader.FreePrepared(j.Data);
            else NativeMemory.Free(j.Data);
            j.Data = null;
        }
        lock (_gate) _pool.Push(j);
    }

    private static void StartWorker()
    {
        if (_worker) return;
        _worker = true;
        new Thread(static () =>
        {
            while (true)
            {
                Job j;
                lock (_gate) { while (_in.Count == 0) Monitor.Wait(_gate); j = _in.Dequeue(); }
                try { Work(j); }
                catch (Exception e) { j.Why = $"{e.GetType().Name}: {e.Message}"; }
                lock (_gate) _out.Enqueue(j);
            }
        }) { IsBackground = true, Name = "cx-tex" }.Start();
    }

    /// <summary>The worker: read and decode. No GL, no engine memory -- everything it reads was copied for it
    /// at the offer, so nothing it holds can outlive a table the engine rebuilt.
    /// <para>NWSync (kind 5) carries no container path -- its <see cref="TexLocation.Path"/> is the bare
    /// resref -- so it is read through <see cref="NwSyncReader"/> and not through a file handle. Without this
    /// branch every texture a NWSync server serves failed as "cannot open &lt;resref&gt;" and drew the white
    /// stand-in: on the user's server that was 82 body-part PLTs, which is armour rendered as pale skin.</para></summary>
    private static void Work(Job j)
    {
        if (j.Loc.Kind == KindNwSync) { NwSyncWork(j); return; }
        if (!_handles.Read(j.Loc, out ReadOnlySpan<byte> raw, out string? why)) { j.Why = why ?? "the read failed"; return; }
        Decode(j, raw);
    }

    /// <summary>The NWSync read: <see cref="NwSyncReader"/> hands over a decompressed blob this thread owns,
    /// so the decode runs inside the lifetime and the blob is freed on every path out.</summary>
    private static void NwSyncWork(Job j)
    {
        if (!NwSyncReader.Armed) { j.Why = "NWSync is not armed, and the location has no container path"; return; }
        Span<byte> name = stackalloc byte[j.Loc.Path.Length];
        System.Text.Encoding.ASCII.GetBytes(j.Loc.Path, name);
        if (!NwSyncReader.TryRead(name, j.Loc.ResType, out byte* data, out int size))
        { j.Why = "NWSync has no row for it (counted on the nwsync note)"; return; }
        try { Decode(j, new ReadOnlySpan<byte>(data, size)); }
        finally { NativeMemory.Free(data); }
    }

    /// <summary>Decode the bytes the read produced, whichever container they came from.</summary>
    private static void Decode(Job j, ReadOnlySpan<byte> raw)
    {
        j.BumpyShiny = j.BumpyShinyTxi || TxiNamesBumpyShiny(raw);
        j.EnvMap = j.EnvMapTxi || TxiNamesEnvMap(raw);
        if (j.Kind == TexKind.Plt)
        {
            byte* rg = PltImage.Decode(raw, out int w, out int h, out int levels, out string? pw);
            if (rg == null) { j.Why = pw ?? "not a PLT"; return; }
            j.Data = rg; j.W = w; j.H = h; j.Levels = levels;
            j.GlFmt = Gl.RG8; j.PixFmt = Gl.RG; j.PixType = Gl.UNSIGNED_BYTE;
            j.Compressed = false; j.GenMips = false;
            j.Bytes = PltImage.ChainBytes(w, h, levels);
            // A PLT carries no alpha of its own: the composite's alpha is the PALETTE texel's, and that
            // channel is NOT coverage -- it is the engine's REFLECTIVITY mask (#121: fEnvMapLevel =
            // 1 - alpha). The shipped palettes say so outright: along pal_armor01's row 44 the alpha ramps
            // DOWN from 219 to 40 as the intensity rises -- the brighter the texel the shinier, which read
            // as opacity makes every highlight a hole. Measured on the stock male body with its own scheme,
            // 33% of pmh0_chest004's texels and 62% of pmh0_legr015's resolve at or under the 0.2 discard,
            // so Cutout punched a third of the armour away and left the dark, green-tinted low-intensity
            // texels: the "wrong shades of black", and a guard green all over rather than green-SHEENED.
            // The engine never tests it either -- Material::GetIsOpaque @0x4c6190 answers with the
            // material's HasEnvMap flag and never looks at the texture, and a creature part is env-mapped
            // (the appearance's ENVMAP column, CreateBodyParts @0x734440), so vanilla draws it in bucket 0
            // with the discard OFF. Opaque is that verdict. Mode 0 never reached this: a PLT-skinned
            // creature is refused by Alloy there and drawn by the engine, so DB mode is the first to draw
            // one. (The sheen itself still waits on an env map in DB mode -- DrawRecipe's ponytail marker.)
            j.Cov = TexCoverage.Opaque;
            return;
        }
        if (j.Loc.ResType == TgaResType)
        {
            byte* px = RasterReader.PrepareTga(raw, out RasterReader.Raster r, out string? tw);
            if (px == null) { j.Why = tw ?? "the TGA was refused"; return; }
            j.Data = px; j.W = r.W; j.H = r.H; j.Levels = Levels(r.W, r.H); j.Channels = r.Px;
            (j.GlFmt, j.PixFmt) = r.Px switch
            {
                1 => (Gl.R8, Gl.RED),
                3 => (Gl.RGB8, Gl.RGB_),
                _ => (Gl.RGBA8, Gl.RGBA_),
            };
            j.PixType = Gl.UNSIGNED_BYTE;
            j.Compressed = false; j.GenMips = true;
            j.Bytes = r.Bytes;
            j.Cov = TexCoverageOf.OfRaster(new ReadOnlySpan<byte>(px, r.Bytes), r.W, r.H, r.Px);
            j.MeanOk = TexScan.MeanOfRaster(px, r.W, r.H, r.Px, out j.Mean);
            return;
        }
        byte* chain = DdsReader.Prepare(raw, out DdsReader.Info info, out string? dw);
        if (chain == null) { j.Why = dw ?? "the DDS was refused"; return; }
        if (info.Faces != 1) { DdsReader.FreePrepared(chain); j.Why = $"{info.Faces} faces -- a cube map is not a registry texture"; return; }
        uint fmt = DdsReader.GlFormat(info.Format);
        if (fmt == 0) { DdsReader.FreePrepared(chain); j.Why = $"{info.Format} has no GL internal format"; return; }
        j.Data = chain; j.Dds = info; j.W = info.W; j.H = info.H; j.Levels = info.Levels;
        j.Channels = info.Channels; j.GlFmt = fmt; j.Compressed = true; j.GenMips = false;
        j.Bytes = info.ChainBytes;
        j.Cov = TexCoverageOf.OfBcn(new ReadOnlySpan<byte>(chain, DdsReader.LevelSize(info.W, info.H, info.Channels)),
                                    info.W, info.H, info.Format, ref _covScratch);
        if (info.Format is DdsReader.Fmt.Bc1 or DdsReader.Fmt.Bc3)
            j.MeanOk = TexScan.MeanOfDxt(chain, info.W, info.H, info.Format == DdsReader.Fmt.Bc3, out j.Mean);
    }

    /// <summary>One small text resource (a <c>.txi</c>), read through the same location record and file
    /// handles a texture is. Game thread -- it reads the engine's resource tables -- and cold: the caller
    /// caches by name. Null when there is no such resource, which the caller reports.</summary>
    internal static string? ReadResourceText(string resref, ushort resType)
    {
        if (!ResLocate.Ready && !ResLocate.Attach(out _)) return null;
        Span<byte> name = stackalloc byte[ShaderResource.ResRefBytes + 1];
        ResRef(resref, name);
        uint* resId = stackalloc uint[ResLocate.ResIdWords];
        TexLocation loc;
        fixed (byte* p = name)
        {
            if (!ResLocate.Find(p, resType, out IntPtr table, out IntPtr entry)) return null;
            if (!ResLocate.Locate(table, entry, resId, out loc, out _, out _)) return null;
        }
        if (loc.Kind == KindNwSync)
        {
            if (!NwSyncReader.Armed) return null;
            Span<byte> res = stackalloc byte[loc.Path.Length];
            System.Text.Encoding.ASCII.GetBytes(loc.Path, res);
            if (!NwSyncReader.TryRead(res, loc.ResType, out byte* blob, out int size)) return null;
            try { return System.Text.Encoding.ASCII.GetString(blob, size); }
            finally { NativeMemory.Free(blob); }
        }
        // The worker's handles belong to the worker; a game-thread read takes its own, which is what the
        // engine's own CExoFile-per-resource did.
        using var h = new Handles();
        if (!h.Read(loc, out ReadOnlySpan<byte> raw, out _)) return null;
        return System.Text.Encoding.ASCII.GetString(raw);
    }

    /// <summary>Levels of the geometric chain down to 1x1.</summary>
    internal static int Levels(int w, int h)
    {
        int n = 1;
        while (w > 1 || h > 1) { w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1); n++; }
        return n;
    }

    // ---- the bracket (GL only) ----

    /// <summary>Alloy's 4.6 bracket: delete what retired, upload what the worker finished and mint its handle.
    /// Nothing else may mint a bindless handle -- residency is per context -- and nothing else here touches
    /// the table or the engine: the result goes to <see cref="_landed"/> for <see cref="Tick"/> to file.</summary>
    internal static void Drain()
    {
        // FIRST, ahead of the deletes: a record still holding a dead handle must stop before anything this
        // bracket does can be followed by a draw. The colour frame enters this bracket before it binds a
        // target, so this is the frame's earliest point at which the renderer's tables may be touched.
        ZeroDyingHandles();
        while (true)
        {
            uint dead;
            lock (_lock) { if (_graveyard.Count == 0) break; dead = _graveyard.Dequeue(); }
            if (dead != 0 && Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dead);
        }
        for (int n = 0; n < UploadsPerFrame; n++)
        {
            Job j;
            lock (_gate) { if (_out.Count == 0) break; j = _out.Dequeue(); _pending--; }
            try { Finish(j); } finally { Recycle(j); }
        }
        ApplySamplerWishes();
        // ...and AGAIN: ApplySamplerWishes drops and re-mints handles of its own, and leaving those to the
        // next frame's drain is a whole frame of records pointing at a handle this one just retired.
        ZeroDyingHandles();
    }

    /// <summary>CX-117: the sampler changes <see cref="RequestSampler"/> recorded. A bindless handle freezes
    /// its sampler state at the mint, so the state is set and the handle re-minted -- which is why this is
    /// the bracket's and not the setter's. A slot that is not resident yet keeps its wish: the upload reads
    /// the same columns, so it lands with the state already right.</summary>
    private static void ApplySamplerWishes()
    {
        while (true)
        {
            int i;
            lock (_lock)
            {
                if (_samplerQ.Count == 0) break;
                i = _samplerQ.Dequeue();
                if (!_samplerWish[i] || _state[i] == TexState.Free) continue;
                if (_name[i] == 0) continue;       // not uploaded yet; Upload reads _clamp/_nearest/_mipmap itself
                _samplerWish[i] = false;
            }
            uint name;
            bool clamp, nearest, mip;
            lock (_lock) { name = _name[i]; clamp = _clamp[i]; nearest = _nearest[i]; mip = _mipmap[i]; }
            if (name == 0) continue;
            int wrap = clamp ? Gl.CLAMP_TO_EDGE : Gl.REPEAT;
            Gl.TextureParameteri(name, Gl.TEXTURE_WRAP_S, wrap);
            Gl.TextureParameteri(name, Gl.TEXTURE_WRAP_T, wrap);
            Gl.TextureParameteri(name, Gl.TEXTURE_MAG_FILTER, nearest ? Gl.NEAREST : Gl.LINEAR);
            Gl.TextureParameteri(name, Gl.TEXTURE_MIN_FILTER,
                !mip ? (nearest ? Gl.NEAREST : Gl.LINEAR)
                     : nearest ? Gl.NEAREST_MIPMAP_NEAREST : Gl.LINEAR_MIPMAP_LINEAR);
            Gl.TextureParameteri(name, Gl.TEXTURE_MAX_ANISOTROPY, mip && !nearest ? Aniso() : 1);
            ulong fresh;
            lock (_lock) { DropHandle(i); }
            fresh = MintHandle(name, _resref[i] ?? "");
            lock (_lock) { _handle[i] = fresh; _samplerDone++; if (fresh != 0) _mints++; }
            lock (_gate) _reminted.Enqueue(i);
        }
    }

    private static float _anisoMax;

    /// <summary>The player's Game Options anisotropy, clamped to the driver's ceiling: the level
    /// <c>GLRender::SetTextureAnisotropicFiltering</c> @0x5ca100 gives every linear mipmapped texture.
    /// ponytail: taken at upload/mint; a level changed mid-area reaches textures loaded after it (the engine
    /// refreshes all on change), re-mint every slot if that matters.</summary>
    private static int Aniso()
    {
        if (_anisoMax == 0f && Gl.GetFloatv != null) { float v; Gl.GetFloatv(Gl.MAX_TEXTURE_MAX_ANISOTROPY, &v); _anisoMax = MathF.Max(v, 1f); }
        return (int)Math.Clamp(StockConfig.Aniso, 1, (long)MathF.Max(_anisoMax, 1f));
    }

    /// <summary>One finished job, uploaded and published. In the bracket.</summary>
    private static void Finish(Job j)
    {
        int i = j.Slot;
        if ((uint)i >= MaxSlots) return;
        // Stale: the slot was evicted (or re-used) while the worker held the job, so this upload has no
        // subject. The generation is the whole test -- no address, no identity probe.
        bool stale;
        lock (_lock) stale = (_gen[i] & 0xFFF) != j.Gen || _state[i] == TexState.Free;
        if (stale) return;
        if (j.Why != null) { Publish(new Landed(i, j.Gen, 0, 0, 0, TexCoverage.Unknown, false, false, j.Why)); return; }
        uint name = Upload(j, out string? why);
        if (name == 0) { Publish(new Landed(i, j.Gen, 0, 0, 0, TexCoverage.Unknown, false, false, why ?? "the driver refused the upload")); return; }
        lock (_lock) if ((_gen[i] & 0xFFF) == j.Gen) { _mean[i] = j.Mean; _meanOk[i] = j.MeanOk; }
        Publish(new Landed(i, j.Gen, name, MintHandle(name, j.Resref), j.Bytes, j.Cov, j.BumpyShiny, j.EnvMap, null));
    }

    private static void Publish(in Landed l) { lock (_gate) _landed.Enqueue(l); }

    // ---- the game thread's half ----

    /// <summary>The game thread's frame hop: file what the bracket uploaded, offer what was waiting, and evict
    /// past the budget. Everything here either writes <see cref="SceneDb"/> or reads the engine's resource
    /// manager, so it belongs on this thread and nowhere else.</summary>
    internal static void Tick()
    {
        // The seven palettes, read where a resource read belongs. DB mode needs them the first time a PLT
        // record draws; PltVerify loads them itself in engine mode, which is why this does not force it.
        if (Renderer.On && !PltPalettes.Loaded) PltPalettes.LoadDefaults();
        while (true)
        {
            int r;
            lock (_gate) { if (_reminted.Count == 0) break; r = _reminted.Dequeue(); }
            lock (_lock) MarkDependents(r);
        }
        while (true)
        {
            Landed l;
            lock (_gate) { if (_landed.Count == 0) break; l = _landed.Dequeue(); }
            if ((uint)l.Slot >= MaxSlots) continue;
            bool stale;
            lock (_lock) stale = (_gen[l.Slot] & 0xFFF) != l.Gen || _state[l.Slot] == TexState.Free;
            if (stale)
            {
                // The slot went while the upload was in flight: the name is ours and nothing references it.
                if (l.Name != 0) lock (_lock) _graveyard.Enqueue(l.Name);
                continue;
            }
            _pend[l.Slot] = PendNone;
            if (l.Why != null) Fail(l.Slot, l.Why);
            else Land(TexId.Make(l.Slot, l.Gen), l.Name, l.Bytes, l.Handle, l.Cov, l.BumpyShiny, l.EnvMap);
        }
        while (true)
        {
            int i;
            lock (_gate) { if (_pending >= MaxPending || _waiting.Count == 0) break; i = _waiting.Dequeue(); }
            if (_pend[i] != PendWaiting) continue;   // released and re-acquired, or retired, while it waited
            _pend[i] = PendNone;
            Enqueue(i);
        }
        EvictPastBudget();
    }

    /// <summary>The upload: immutable storage for the whole chain, then one sub-image per level. Never
    /// respecify -- a mip read after respecification returned undefined pixels on Mesa/radeonsi, which is the
    /// bug behind every-mip-but-the-base-wrong.</summary>
    private static uint Upload(Job j, out string? why)
    {
        why = null;
        if (Gl.CreateTextures == null || Gl.TextureStorage2D == null) { why = "no GL 4.5 direct state access"; return 0; }
        uint dst; Gl.CreateTextures(Gl.TEXTURE_2D, 1, &dst);
        if (dst == 0) { why = "glCreateTextures returned no name"; return 0; }
        int svUnpack; Gl.GetIntegerv(Gl.PIXEL_UNPACK_BUFFER_BINDING, &svUnpack);
        if (svUnpack != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0);
        // GL's default row alignment is 4 bytes. An RG8 mip of odd width (a 2-byte row), an R8 or RGB8 level
        // whose width is not a multiple of 4, and every tail of a PLT chain all have rows that are not
        // 4-aligned: at the default the driver skips padding that is not there, which reads the next row's
        // pixels and runs off the end of the last one.
        int svAlign; Gl.GetIntegerv(Gl.UNPACK_ALIGNMENT, &svAlign);
        if (svAlign != 1) Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        Gl.DrainErrors(8);
        Gl.TextureStorage2D(dst, j.Levels, j.GlFmt, j.W, j.H);
        Gl.TextureParameteri(dst, Gl.TEXTURE_MAX_LEVEL, j.Levels - 1);
        // The texture's own .txi clamp (TxiSampler) or a script's SetTextureAttributes.
        int wrap = j.Clamp ? Gl.CLAMP_TO_EDGE : Gl.REPEAT;
        Gl.TextureParameteri(dst, Gl.TEXTURE_WRAP_S, wrap);
        Gl.TextureParameteri(dst, Gl.TEXTURE_WRAP_T, wrap);
        // A PLT is sampled by texelFetch and resolved per layer; a filtered RG8 read would interpolate LAYER
        // INDICES, which is a different colour, not a blurrier one. Its own bilinear is in cx_plt.glsl, and a
        // .txi filter attribute cannot talk it out of nearest.
        bool nearest = j.Kind == TexKind.Plt || j.Nearest;
        bool mip = j.Kind == TexKind.Plt || j.Mip;
        Gl.TextureParameteri(dst, Gl.TEXTURE_MAG_FILTER, nearest ? Gl.NEAREST : Gl.LINEAR);
        Gl.TextureParameteri(dst, Gl.TEXTURE_MIN_FILTER,
            !mip ? (nearest ? Gl.NEAREST : Gl.LINEAR)
                 : nearest ? Gl.NEAREST_MIPMAP_NEAREST : Gl.LINEAR_MIPMAP_LINEAR);
        if (mip && !nearest) Gl.TextureParameteri(dst, Gl.TEXTURE_MAX_ANISOTROPY, Aniso());
        if (j.Channels == 1 && !j.Compressed)
        {
            Gl.TextureParameteri(dst, Gl.TEXTURE_SWIZZLE_G, (int)Gl.RED);
            Gl.TextureParameteri(dst, Gl.TEXTURE_SWIZZLE_B, (int)Gl.RED);
        }
        if (j.Compressed)
        {
            long off = 0;
            for (int l = 0, w = j.W, h = j.H; l < j.Levels; l++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            {
                int sz = DdsReader.LevelSize(w, h, j.Channels);
                Gl.CompressedTextureSubImage2D(dst, l, 0, 0, w, h, j.GlFmt, sz, j.Data + off);
                off += sz;
            }
        }
        else if (j.Kind == TexKind.Plt)
        {
            long off = 0;
            for (int l = 0, w = j.W, h = j.H; l < j.Levels; l++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            {
                Gl.TextureSubImage2D(dst, l, 0, 0, w, h, j.PixFmt, j.PixType, j.Data + off);
                off += (long)w * h * PltImage.BytesPerPixel;
            }
        }
        else
        {
            Gl.TextureSubImage2D(dst, 0, 0, 0, j.W, j.H, j.PixFmt, j.PixType, j.Data);
            if (j.GenMips && Gl.GenerateTextureMipmap != null) Gl.GenerateTextureMipmap(dst);
        }
        uint err = Gl.GetError();
        if (svAlign != 1) Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, svAlign);
        if (svUnpack != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, (uint)svUnpack);
        if (err == 0) return dst;
        if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dst);
        why = $"the driver refused a {j.W}x{j.H} {j.Levels}-level 0x{j.GlFmt:x} upload (0x{err:x})";
        return 0;
    }

    /// <summary>A texture arrived: the slot takes its name and the handle the bracket minted for it, and every
    /// dependent part record is marked <see cref="Dirty.Texture"/>. Game thread, GL-free -- so the off-device
    /// check drives it with a synthetic name.</summary>
    internal static void Land(TexId id, uint name, long bytes, ulong handle = 0,
                              TexCoverage cov = TexCoverage.Unknown, bool bumpyShiny = false,
                              bool envMap = false)
    {
        lock (_lock)
        {
            if (!Valid(id)) return;
            int i = id.Index;
            if (_name[i] != 0) { _graveyard.Enqueue(name); return; }   // a second upload for one slot: keep the first
            _cov[i] = cov;
            _bumpyShiny[i] = bumpyShiny;
            CoverageSerial++;
            if (bumpyShiny) _bumpyShinySeen++;
            _envMap[i] = envMap;
            if (envMap) _envMapSeen++;
            if (cov == TexCoverage.Unknown) _covUnknown++;
            _name[i] = name;
            _handle[i] = handle;
            _bytes[i] = bytes;
            if (_refs[i] > 0) { _state[i] = TexState.Resident; _residentBytes += bytes; }
            else { _state[i] = TexState.Idle; _idleBytes += bytes; IdlePush(i); }
            if (_residentBytes + _idleBytes > _peakBytes) _peakBytes = _residentBytes + _idleBytes;
            if (handle != 0) _mints++;
            _uploads++;
            MarkDependents(i);
        }
    }

    /// <summary>The load failed. Loud once per resref, counted after that, and the slot keeps the white
    /// stand-in as an explicit state -- never a quiet hole (CX-45). Game thread.</summary>
    internal static void Fail(int i, string why)
    {
        string name; TexKind kind;
        lock (_lock)
        {
            _state[i] = TexState.Failed;
            _pend[i] = PendNone;
            _failed++;
            name = _resref[i] ?? "?"; kind = _kind[i];
            MarkDependents(i);
        }
        if (LoudOnce($"tex:{name}"))
            _log?.Always($"TexRegistry: \"{name}\" ({kind}) did not load -- {why}. "
                + "Every part that names it draws the WHITE STAND-IN for the rest of the session");
    }

    // ---- handles ----

    /// <summary>A resident bindless handle for a freshly uploaded name. Minted in the BRACKET and nowhere
    /// else: a handle belongs to the share group and only Alloy's context is in it. A handled texture is
    /// immutable from that moment, which registry textures are by construction -- a change is a new slot,
    /// never a sub-image. 0 when the device mints nothing, which drops the slot's parts to the bound
    /// sampler path.</summary>
    private static ulong MintHandle(uint name, string resref)
    {
        if (name == 0) return 0;
        if (Gl.GetTextureHandleARB == null || Gl.MakeTextureHandleResidentARB == null) return 0;
        ulong v = Gl.GetTextureHandleARB(name);
        if (v == 0)
        {
            if (LoudOnce("mint"))
                _log?.Always($"TexRegistry: glGetTextureHandleARB minted nothing for \"{resref}\" -- "
                    + "its parts fall to the bound sampler path");
            return 0;
        }
        Gl.MakeTextureHandleResidentARB(v);
        return v;
    }

    /// <summary>Release a slot's handle. Caller holds <see cref="_lock"/>; the non-residency call is legal
    /// from any thread that has the context, and a handle whose context is gone is simply forgotten.
    /// <para>The dying value is queued for <see cref="ZeroDyingHandles"/>, because the renderer's DB records
    /// hold COPIES of it: this is the one funnel every drop goes through (a last release to idle, a retire,
    /// and the re-mint a sampler change forces), so queueing here is what makes the records' handles follow
    /// the registry's.</para></summary>
    private static void DropHandle(int i)
    {
        if (_handle[i] == 0) return;
        if (Gl.MakeTextureHandleNonResidentARB != null) Gl.MakeTextureHandleNonResidentARB(_handle[i]);
        _dyingHandles.Enqueue(_handle[i]);
        _handle[i] = 0;
    }

    // ---- budget ----

    /// <summary>ADR 0023's budget, read the same way: <c>texcache_mb</c> -1 is a quarter of probed VRAM, 0
    /// evicts an idle slot as soon as it is idle.</summary>
    private static long BudgetBytes()
    {
        int mb = Renderer.TexCache.V;
        if (mb > 0) return (long)mb << 20;
        if (mb == 0) return 0;
        long vram = AlloyRenderer.VramTotalMib > 0 ? AlloyRenderer.VramTotalMib : AlloyRenderer.VramFreeMib;
        return vram > 0 ? (vram << 20) / 4 : 512L << 20;
    }

    /// <summary>Trim the idle set back to the budget, oldest first off the LRU head. Game thread; the GL
    /// names go to the graveyard for the bracket. Before review r1 this rescanned 1..<c>_high</c> per victim,
    /// inside the bracket.</summary>
    private static void EvictPastBudget()
    {
        long budget = BudgetBytes();
        lock (_lock)
            while (_idleHead >= 0 && _residentBytes + _idleBytes > budget)
                Retire(_idleHead);
    }

    /// <summary>Terminal per session (<c>docs/owned-resource-lifetimes.md</c>): every GL name and handle this
    /// registry held is gone with the context, so they are FORGOTTEN rather than deleted -- there is no
    /// context left to delete them in, and the graveyard goes with them. A slot something still references
    /// reloads on the next <see cref="Tick"/>; an idle one is retired, because its whole value was the name it
    /// no longer has. The slots keep their resrefs, which is what a content identity buys -- nothing here was
    /// keyed on a name.</summary>
    internal static void ContextLost()
    {
        lock (_lock)
        {
            _graveyard.Clear();
            // Nothing to zero in the records either: the context that owned them is gone, and the renderer's
            // own context-loss path rebuilds its tables. The queue is DROPPED rather than drained, so a
            // handle value the next context mints cannot be matched by a stale entry from the old one.
            _dyingHandles.Clear();
            for (int i = 1; i < _high; i++)
            {
                if (_state[i] == TexState.Free) continue;
                _handle[i] = 0;
                _name[i] = 0;
                if (_refs[i] <= 0) { Retire(i); continue; }   // Retire owns the byte accounting; do not pre-subtract
                _residentBytes -= _bytes[i];
                _bytes[i] = 0;
                // _cov is NOT cleared: it is a property of the content, the reload derives the same verdict,
                // and clearing it would make every draw re-classify as blended for the frames in between.
                _state[i] = TexState.Loading;
                _pend[i] = PendWaiting;
                lock (_gate) _waiting.Enqueue(i);   // lock order is always _lock -> _gate, never the reverse
                _reloads++;
            }
            if (_residentBytes < 0) _residentBytes = 0;
            if (_idleBytes < 0) _idleBytes = 0;
        }
    }
}
