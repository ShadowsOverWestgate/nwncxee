using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>What a registry texture turned out to be, which decides its decode and its sampling. The kind is
/// a property of the RESREF, not of the asking: a name resolves to exactly one resource in the engine's
/// tables, so it is resolved at the locate step and is not part of the slot key.</summary>
internal enum TexKind : byte
{
    /// <summary>The caller names a texture and does not know what it is -- which is the usual case, because a
    /// model's <c>bitmap</c> line is just a resref: a creature body part's is a PLT, a wall's is a DDS. The
    /// locate step tries DDS, then TGA, then PLT, exactly as the engine's own loader and
    /// <c>Material::ReplaceTexturePLT</c>'s <c>CExoResMan::Exists(resref, 6)</c> gate do between them, and
    /// the slot takes the kind it found (<see cref="TexRegistry.KindOf"/>). A caller that passes
    /// <see cref="Plain"/> or <see cref="Plt"/> only narrows that search -- it does not make a second slot.</summary>
    Auto,
    /// <summary>A DDS or TGA: decoded to its own GL texture and sampled directly.</summary>
    Plain,
    /// <summary>A PLT: uploaded as RG8 (intensity, layer) and resolved against
    /// <see cref="PltPalettes"/> in the shader, per instance (CX-117). One copy serves every creature
    /// wearing it, whatever colours they chose.</summary>
    Plt,
}

/// <summary>Where a registry slot is in its life. Every state is explicit and counted: a texture that is
/// not <see cref="TexRegistry.Resident"/> draws the white stand-in, and <see cref="Failed"/> says so on the
/// period line rather than leaving a hole nobody can name (CX-45).</summary>
internal enum TexState : byte { Free, Loading, Resident, Failed, Idle }

/// <summary>A generation-checked handle into <see cref="TexRegistry"/>, the same shape as
/// <see cref="DbId"/>: low 20 bits index, high 12 bits generation, 0 is null. A handle to an evicted slot
/// resolves to nothing, so a material that outlived its texture reads a refusal rather than another
/// texture's image.</summary>
internal readonly record struct TexId(uint Value)
{
    public int Index => (int)(Value & DbId.IndexMask);
    public uint Gen => Value >> DbId.IndexBits;
    public bool IsNull => Value == 0;
    public static TexId Make(int index, uint gen) => new(((gen & 0xFFF) << DbId.IndexBits) | (uint)index);
}

/// <summary>CX-117 / ADR 0028: <b>CX's texture registry</b> -- the table that decides which textures exist in
/// Alloy mode, in place of the engine's <c>CAurTexture</c> population. Identity is the resolved resref plus
/// the atlas cell, never an address and never the engine's anagram-blind hash, so two areas that share a
/// texture share one upload and an area change re-uses everything the next area wants (ADR 0023's lifetime,
/// now without the engine's delete funnel to work around). Refcounted by the materials that name it.
/// <para>Nothing here holds a <c>CAurTexture*</c>: a slot is a resref, a GL name and a bindless handle. That
/// is the whole of CX-117's first "done when" -- the borrowed-pointer verdicts of ADR 0015 have no subject
/// left in DB mode.</para>
/// <para><b>PLT is not composited.</b> A PLT slot holds the file's (intensity, layer) pairs as RG8; the
/// per-creature colour rows live on the object record (<see cref="PltColors"/>) and the shader resolves
/// colour = master[row[layer]][intensity]. So one copy of <c>pmh0_head001</c> serves every human male head in
/// the area, a colour change is a record write, and there is no per-creature texture.</para>
///
/// <para><b>Threading (ADR 0028 section 5).</b> Three threads touch this, and which one may do what is the
/// contract, not an accident of today's single-threaded frame -- <c>Db/RenderThread.cs</c> exists to make the
/// bracket a different thread:</para>
/// <list type="bullet">
/// <item><b>Game thread</b>: <see cref="Acquire"/>, <see cref="Release"/>, <see cref="Swap"/>,
/// <see cref="AddDependent"/> and <see cref="Tick"/>. Everything that reads the engine's resource manager
/// (<c>CExoResMan</c> is not thread-safe) or writes <see cref="SceneDb"/> is here and nowhere else.</item>
/// <item><b>The <c>cx-tex</c> worker</b>: the file read and the decode. Pure -- it sees only a
/// <c>TexLocation</c> value record copied for it at the offer.</item>
/// <item><b>Alloy's 4.6 bracket</b>: <see cref="Drain"/> and <see cref="ContextLost"/>. GL only: the upload,
/// the bindless mint (a handle belongs to the share group, so it may be minted nowhere else) and the deletes.
/// It publishes finished slots to a queue the game thread's <see cref="Tick"/> files.</item>
/// </list>
/// <para>Every mutation of the table takes <see cref="_lock"/>, which is uncontended in practice: acquires
/// and releases happen at a material change, uploads at a texture's arrival. The per-draw READS
/// (<see cref="Handle"/>, <see cref="Resident"/>, <see cref="StateOf"/>, <see cref="KindOf"/>) are lock-free
/// on purpose, and are safe because a record that draws a slot holds a REFERENCE on it and eviction only ever
/// touches a slot whose reference count is zero: the one value a draw must not read stale -- a resident
/// handle for a retired texture -- cannot arise.</para></summary>
internal static unsafe partial class TexRegistry
{
    /// <summary>Slots held at once. A heavy SoW area measured ~1,200 distinct textures and the resident cache
    /// holds several areas' worth; 16k is past any module's whole texture set.</summary>
    internal const int MaxSlots = 1 << 14;
    /// <summary>Open-address index over <see cref="_key"/>, power of two and at least 2x <see cref="MaxSlots"/>
    /// so the chains stay short.</summary>
    private const int HashBits = 16, HashSize = 1 << HashBits, HashMask = HashSize - 1;

    /// <summary>The one lock. Guards every column, the index, the free list and the queues; never held across
    /// a GL call or a resource read.</summary>
    private static readonly object _lock = new();

    private static ExtenderLog? _log;
    private static bool _attached;

    // ---- slot columns (SoA: every walk reads two or three of them) ----
    private static readonly ulong[] _key = new ulong[MaxSlots];
    private static readonly uint[] _gen = new uint[MaxSlots];
    private static readonly TexState[] _state = new TexState[MaxSlots];
    private static readonly TexKind[] _kind = new TexKind[MaxSlots];
    private static readonly int[] _refs = new int[MaxSlots];
    /// <summary>The GL name and its resident bindless handle; 0 until the upload lands.</summary>
    private static readonly uint[] _name = new uint[MaxSlots];
    private static readonly ulong[] _handle = new ulong[MaxSlots];
    private static readonly long[] _bytes = new long[MaxSlots];
    /// <summary>The resref, lower-cased, kept for the log, for the identity compare at a hash hit, and for
    /// the reload after a context loss.</summary>
    private static readonly string?[] _resref = new string?[MaxSlots];
    /// <summary>The atlas cell this slot holds, which is part of its identity (a cycle texture's frames are
    /// cells of one resref).</summary>
    private static readonly int[] _cell = new int[MaxSlots];
    /// <summary>What the slot's alpha channel MEANS (CX-120's draw classification, feed review r1 finding 8).
    /// Derived from the DECODED pixels on the worker, once, and kept across a context loss because it is a
    /// property of the content and not of a GL name.</summary>
    private static readonly TexCoverage[] _cov = new TexCoverage[MaxSlots];
    /// <summary>Each slot's mean colour as decoded (its own encoding), and whether one was taken: the sky
    /// texture's average that the sky-matched fog converges on (SceneFog), which mode 0 reads off the
    /// engine's upload funnel and DB mode has only here.</summary>
    private static readonly System.Numerics.Vector3[] _mean = new System.Numerics.Vector3[MaxSlots];
    private static readonly bool[] _meanOk = new bool[MaxSlots];

    /// <summary>The slot's mean colour, 0..1 in the texture's own (display) encoding; false until it landed
    /// with one (a TGA, a BC1/BC3 DDS).</summary>
    internal static bool TryMean(TexId id, out System.Numerics.Vector3 mean)
    {
        lock (_lock)
        {
            if (Valid(id) && _meanOk[id.Index] && _name[id.Index] != 0) { mean = _mean[id.Index]; return true; }
        }
        mean = default;
        return false;
    }
    /// <summary>The slot's <c>.txi</c> named a <c>bumpyshinytexture</c>, which is the engine's own route into
    /// the water class (<c>Material::HasBumpyShinyMap</c>). Content, like <see cref="_cov"/>, so it survives a
    /// context loss.</summary>
    private static readonly bool[] _bumpyShiny = new bool[MaxSlots];
    /// <summary>Per slot: the texture's <c>.txi</c> declared an <c>envmaptexture</c>, which is the engine's
    /// own "this material is environment-mapped" (<c>Material::HasEnvMap</c> @0x5c6180).</summary>
    private static readonly bool[] _envMap = new bool[MaxSlots];
    /// <summary>1 while the slot's sampler CLAMPS rather than repeats (a <c>.txi clamp</c>); staged into the
    /// PLT flag word, because <c>cx_plt.glsl</c> does its own addressing and cannot see a sampler.</summary>
    private static readonly bool[] _clamp = new bool[MaxSlots];
    /// <summary>The slot's name and handle are its PREVIEW (the mip tail from 64 px down, <see cref="LandPreview"/>):
    /// drawable, but the slot is still <see cref="TexState.Loading"/> and the full chain replaces it in <see cref="Land"/>.</summary>
    private static readonly bool[] _preview = new bool[MaxSlots];
    /// <summary>Left the name index at a module edge while still referenced (<see cref="ModuleEdge"/>): no acquire
    /// finds it, and its last release retires it instead of leaving it idle.</summary>
    private static readonly bool[] _detached = new bool[MaxSlots];
    /// <summary>Where this slot is in the load pipeline: <see cref="PendNone"/>, <see cref="PendWaiting"/>
    /// (queued for an offer the game thread will make) or <see cref="PendFlight"/> (the worker has it). The
    /// state machine's missing half before review r1: a Release during a load left the slot Idle with no
    /// name, and the next Acquire set it Loading again WITHOUT re-offering -- loading for ever, white for
    /// ever, nothing logged.</summary>
    private static readonly byte[] _pend = new byte[MaxSlots];
    private const byte PendNone = 0, PendWaiting = 1, PendFlight = 2;

    /// <summary>CX-188: bumped (under <see cref="_lock"/>) wherever a slot ENTERS
    /// <see cref="TexState.Loading"/> -- an acquire that seats a new slot, an acquire that re-offers a
    /// name-less one, an idle slot taken back with nothing uploaded, and the context reload. It is the only
    /// edge that can make a settled set of parts start streaming again, which is what lets a reader latch
    /// "nothing of mine is waiting" instead of re-scanning every frame (<c>DbGui</c>'s panel prescan).
    /// <para>A reader off the game thread may see the previous value for a frame -- an <c>int</c> read is
    /// atomic, so the cost is one extra scan or one frame's delay, never a wrong answer.</para></summary>
    internal static int LoadSerial = 1;

    /// <summary>Head of this slot's dependent-part edge list, -1 for none. Kept only while the slot is
    /// <see cref="TexState.Loading"/>: once it has arrived (or failed) there is nothing left to tell.</summary>
    private static readonly int[] _depHead = new int[MaxSlots];

    /// <summary>The idle set as a doubly-linked LRU, oldest at <see cref="_idleHead"/>. Eviction takes the
    /// head; before review r1 it rescanned 1..<see cref="_high"/> per victim, inside the bracket.</summary>
    private static readonly int[] _idleNext = new int[MaxSlots], _idlePrev = new int[MaxSlots];
    private static int _idleHead = -1, _idleTail = -1;

    private static readonly int[] _hash = new int[HashSize];
    private static int _high = 1, _freeHead = -1;
    private static readonly int[] _freeNext = new int[MaxSlots];
    private static int _live;

    // ---- dependents: an intrusive edge pool, so arrival is a walk and not a scan of every part ----
    private const int MaxDeps = 1 << 16;
    private static readonly int[] _depPart = new int[MaxDeps], _depNext = new int[MaxDeps];
    private static int _depFree, _depN;
    private static long _depOverflow, _depRefused;

    // ---- counters: every refusal is a number on the period line ----
    private static long _acquires, _hits, _uploads, _failed, _evicted, _reloads, _marks, _full, _mints;
    private static long _residentBytes, _idleBytes, _peakBytes;
    private static readonly HashSet<string> _loudOnce = new(StringComparer.Ordinal);

    /// <summary>Wire the log, seed the index and join the frame. Idempotent; no GL and no engine memory, so
    /// run-demo runs it.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        if (_attached) return;
        _attached = true;
        Array.Fill(_hash, 0);
        Array.Fill(_depHead, -1);
        _depFree = -1;
        PltPalettes.Attach(log);
        // The game thread's half of the pipeline: the offers (which read the engine's resource manager) and
        // the filing of what the bracket uploaded (which writes SceneDb's dirty lists). Both are the game
        // thread's by contract, so neither may ride the bracket.
        FrameRegistry.Global.Register(FramePhase.Start, 10, Tick, "TexRegistry.tick", log);
    }

    // ---- identity ----

    /// <summary>The slot key: a 64-bit FNV-1a over the lower-cased resref, with the atlas cell in the low 16
    /// bits. NOT the engine's <c>AurTextureCalculateHashID</c>, which is a per-character product modulo a
    /// prime and therefore blind to anagrams (<c>tex_core_0145_d</c> and <c>_0154_d</c> hash equal; 2,202 such
    /// values across the base game and the SoW haks), and which the engine only survives by
    /// <c>strcasecmp</c>-ing after the match. A table keyed on it hands the second texture of a family the
    /// first one's image, which is what PR 523 was -- and why <see cref="Probe"/> compares the resref too.
    /// <para>The KIND is deliberately not folded in: a resref resolves to one resource, so the kind is a
    /// function of the name, and keying on the kind ASKED made <c>Acquire(n)</c> and <c>Acquire(n, Plt)</c>
    /// two slots and two uploads of one file.</para></summary>
    internal static ulong KeyOf(string resref, int cell)
    {
        const ulong Prime = 0x100000001B3UL;   // not-an-offset: FNV-1a 64 prime
        ulong h = 0xCBF29CE484222325UL;        // not-an-offset: FNV-1a 64 basis
        for (int i = 0; i < resref.Length; i++)
        {
            uint c = resref[i];
            if (c - 'A' < 26) c += 32;
            h = (h ^ c) * Prime;
        }
        return (h & ~CellMask) | ((ulong)(uint)cell & CellMask);
    }
    private const ulong CellMask = 0xFFFF;   // not-an-offset: key bit field
    internal const int MaxCells = (int)CellMask + 1;

    /// <summary><paramref name="resref"/> as the null-padded, lower-cased ASCII a <c>CResRef</c> is, ready for
    /// <c>CExoResMan::GetKeyEntry</c>. The fold is done in UNSIGNED arithmetic on purpose: <c>c - 'A'</c> on a
    /// signed char is negative for every digit, so a signed test admits '0'..'9' and adds 32 to them --
    /// "pmh0_head005" becomes "pmhP_headPPU" and every numbered resref in the game misses.</summary>
    internal static void ResRef(string resref, Span<byte> name)
    {
        name.Clear();
        int n = Math.Min(resref.Length, Math.Min(name.Length - 1, ShaderResource.ResRefBytes));
        for (int i = 0; i < n; i++)
        {
            uint c = resref[i];
            if (c - 'A' < 26) c += 32;
            name[i] = (byte)c;
        }
    }

    /// <summary>Lock-free: a per-draw read. See the class remarks for why that is safe.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Valid(TexId id)
    {
        int i = id.Index;
        uint g = (uint)i < (uint)MaxSlots ? _gen[i] : 0;
        return i != 0 && _state[i] != TexState.Free && (g & 0xFFF) == id.Gen;
    }

    /// <summary>Walk the open-addressed chain for <paramref name="key"/>: true with <paramref name="hit"/> at
    /// the slot holding it, false with <paramref name="free"/> at the bucket an insert takes. The resref is
    /// compared at the hit as well as the key -- 48 bits of hash is a bucket, not an identity, and the whole
    /// point of this table is that a hash collision must not hand one texture another's image.</summary>
    private static bool Probe(ulong key, string? resref, int cell, out int hit, out int free)
    {
        int h = (int)((key ^ (key >> 32)) & HashMask);
        free = -1;
        for (int step = 0; step < HashSize; step++)
        {
            int b = (h + step) & HashMask;
            int slot = _hash[b];
            if (slot == 0) { if (free < 0) free = b; hit = -1; return false; }
            if (slot > 0 && _key[slot] == key && _state[slot] != TexState.Free
                && _cell[slot] == cell
                && (resref == null || string.Equals(_resref[slot], resref, StringComparison.OrdinalIgnoreCase)))
            { hit = slot; return true; }
            if (slot < 0 && free < 0) free = b;   // a tombstone is reusable but must not end the chain
        }
        hit = -1; return false;
    }

    // ---- acquire / release (game thread) ----

    /// <summary>The texture named <paramref name="resref"/>, one reference added, loading if it was not
    /// already here. Game thread. Never null-handled quietly: a slot that cannot be allocated logs once and
    /// answers a null <see cref="TexId"/>, which draws the white stand-in and counts on the period line.</summary>
    internal static TexId Acquire(string resref, TexKind kind = TexKind.Auto, int cell = 0)
    {
        TexId id = Alloc(resref, kind, cell, out bool offer);
        if (offer) Enqueue(id.Index);
        return id;
    }

    /// <summary>The table half of <see cref="Acquire"/>, with no load started: the slot and whether it wants
    /// an offer. Split out so the off-device check drives the real allocator instead of a hand copy of it
    /// that could drift (review r1 finding 19), and so the offer -- which reads the engine's resource
    /// manager -- happens outside the lock.</summary>
    private static TexId Alloc(string resref, TexKind kind, int cell, out bool offer)
    {
        offer = false;
        _acquires++;
        if (string.IsNullOrEmpty(resref) || (uint)cell >= MaxCells)
        {
            if (LoudOnce($"resref:{resref}:{cell}"))
                _log?.Always($"TexRegistry: refuses \"{resref}\" cell {cell} -- "
                    + (string.IsNullOrEmpty(resref) ? "no name" : $"past the {MaxCells}-cell field in the key"));
            return default;
        }
        ulong key = KeyOf(resref, cell);
        lock (_lock)
        {
            if (Probe(key, resref, cell, out int hit, out int free))
            {
                _hits++;
                if (_refs[hit]++ == 0 && _state[hit] == TexState.Idle)
                {
                    IdleUnlink(hit);
                    _idleBytes -= _bytes[hit]; _residentBytes += _bytes[hit];
                    _state[hit] = _name[hit] != 0 && !_preview[hit] ? TexState.Resident : TexState.Loading;
                    if (_state[hit] == TexState.Loading) LoadSerial++;
                    // Going idle dropped the bindless handle and nothing else re-mints it: a re-equipped
                    // armour drew handle 0 -- black, untextured -- until the slot was evicted and reloaded.
                    if (_name[hit] != 0 && _handle[hit] == 0 && !_samplerWish[hit])
                    { _samplerWish[hit] = true; _samplerQ.Enqueue(hit); }
                    TableSerial++;
                }
                // The Release-during-a-load fix: a slot with no name and nothing in flight is not loading,
                // whatever its state says, so re-asking for it has to re-offer it.
                if ((_name[hit] == 0 || _preview[hit]) && _state[hit] != TexState.Failed && _pend[hit] == PendNone)
                { _state[hit] = TexState.Loading; offer = true; LoadSerial++; TableSerial++; }
                // CX-188 (review): NOT on the plain cache hit. A hit changes nothing a record reads -- the
                // name, handle, kind and env bit are all where they were -- and callers acquire every frame
                // (EmitterSim's per-emitter texture, the text atlas, the radial's PLT), so bumping here busted
                // every slot's texture memo on every frame and the skip bought nothing. Only the two branches
                // above move state: the idle resurrection and the re-offer.
                return TexId.Make(hit, _gen[hit] & 0xFFF);
            }
            int i;
            if (_freeHead >= 0) { i = _freeHead; _freeHead = _freeNext[i]; }
            else if (_high < MaxSlots) i = _high++;
            else
            {
                _full++;
                if (LoudOnce("slots"))
                    _log?.Always($"TexRegistry: the {MaxSlots}-slot table is FULL -- \"{resref}\" draws the white "
                        + "stand-in. Every texture past this one does too until something is evicted");
                return default;
            }
            _key[i] = key; _state[i] = TexState.Loading; _kind[i] = kind; _refs[i] = 1;
            LoadSerial++;
            _name[i] = 0; _handle[i] = 0; _bytes[i] = 0; _preview[i] = false;
            _resref[i] = resref; _cell[i] = cell; _depHead[i] = -1; _pend[i] = PendNone; _clamp[i] = false;
            _cov[i] = TexCoverage.Unknown; _bumpyShiny[i] = false; _envMap[i] = false; _meanOk[i] = false;
            _txiBlend[i] = 0; _txiDecal[i] = false; _cycle[i] = null;
            CoverageSerial++; TableSerial++;
            _nearest[i] = false; _mipmap[i] = true; _samplerWish[i] = false;
            _idleNext[i] = _idlePrev[i] = -1;
            if (free >= 0) _hash[free] = i;
            _live++;
            offer = true;
            return TexId.Make(i, _gen[i] & 0xFFF);
        }
    }

    /// <summary>Drop the cached slot for <paramref name="resref"/> so the NEXT acquire reads the file again,
    /// and answer whether one went. Only an unreferenced slot is dropped: anything still held keeps its
    /// pixels, because a texture under a draw must not change under it.
    /// <para>The engine never needs this -- a <c>CAurTexture</c> dies at its last reference and the next
    /// <c>AurTextureGet</c> rebuilds it from whatever the resource chain now answers. CX keeps the slot idle
    /// instead (ADR 0023), so a resref whose FILE has been swapped underneath -- the load panel adds and
    /// removes a save directory per selection, and every save's preview is called <c>Screen</c> -- would
    /// otherwise hand the next acquire the previous one's pixels. Game thread.</para></summary>
    internal static bool Forget(string resref, int cell = 0)
    {
        if (string.IsNullOrEmpty(resref) || (uint)cell >= MaxCells) return false;
        ulong key = KeyOf(resref, cell);
        lock (_lock)
        {
            if (!Probe(key, resref, cell, out int hit, out _) || _refs[hit] > 0) return false;
            Retire(hit);
            _forgotten++;
            return true;
        }
    }

    /// <summary>Slots dropped by <see cref="Forget"/> this session -- for <c>/alloy</c>.</summary>
    internal static long Forgotten => _forgotten;
    private static long _forgotten;

    /// <summary>Drop one reference. The last one leaves the slot <see cref="TexState.Idle"/> -- resident, GL
    /// name and bytes kept, bindless handle released -- which is ADR 0023's decision, now without an engine
    /// delete funnel to filter. Eviction is <see cref="Tick"/>'s, oldest idle first, only past the budget.
    /// Game thread.</summary>
    internal static void Release(TexId id)
    {
        lock (_lock)
        {
            if (!Valid(id)) return;
            int i = id.Index;
            if (--_refs[i] > 0) return;
            _refs[i] = 0;
            if (_state[i] == TexState.Failed || _detached[i]) { Retire(i); return; }
            _state[i] = TexState.Idle;
            _residentBytes -= _bytes[i]; _idleBytes += _bytes[i];
            IdlePush(i);
            DropHandle(i);
        }
    }

    /// <summary>A material's texture changed (the game's <c>ReplaceTexture</c>, a material slot write, or a
    /// cycle controller stepping to the next cell): acquire the new slot, release the old one. One call, so a
    /// swap cannot leave a reference behind. Game thread.</summary>
    internal static TexId Swap(TexId old, string resref, TexKind kind = TexKind.Auto, int cell = 0)
    {
        TexId now = Acquire(resref, kind, cell);
        Release(old);
        return now;
    }

    // ---- the idle LRU ----

    private static void IdlePush(int i)
    {
        _idleNext[i] = -1; _idlePrev[i] = _idleTail;
        if (_idleTail >= 0) _idleNext[_idleTail] = i; else _idleHead = i;
        _idleTail = i;
    }

    private static void IdleUnlink(int i)
    {
        int p = _idlePrev[i], n = _idleNext[i];
        if (p >= 0) _idleNext[p] = n; else if (_idleHead == i) _idleHead = n;
        if (n >= 0) _idlePrev[n] = p; else if (_idleTail == i) _idleTail = p;
        _idleNext[i] = _idlePrev[i] = -1;
    }

    /// <summary>Unlink a slot from the table: its index entry, its dependents, its generation and its free-list
    /// place. The GL name goes to the graveyard for the bracket to delete -- a delete is GL and this runs on
    /// the game thread. The generation moves on here, so every outstanding <see cref="TexId"/> for it is dead,
    /// which is why no holder needs a death notice. Caller holds <see cref="_lock"/>.</summary>
    /// <summary>Bumped (under the lock) wherever a slot's coverage or bumpy-shiny answer changes: acquire, land,
    /// retire. <see cref="DrawRecipe.Reclassify"/> re-asks its classes only when this moved.</summary>
    internal static int CoverageSerial;

    /// <summary>CX-188: bumped (under <see cref="_lock"/>) wherever ANY answer a record feed reads off a slot
    /// can change -- its GL name, its bindless handle, its kind, its env-map bit, or its validity. The
    /// renderer stamps it per ownership slot (<c>_recTexStamp</c>) and skips re-resolving a part's eight
    /// texture slots on a frame it did not move, so a write that forgets to bump it leaves records sampling a
    /// dead handle: the complete writer set is Acquire, Retire, Land, Fail, DropHandle, the kind discovery in
    /// Begin, the idle re-acquire and ContextLost. Starts at 1 so a never-stamped slot (0) always disagrees.
    /// <para>Deliberately coarser than per slot: a texture event is rare (a transcode drip, an area load) and
    /// one full re-resolve pass per event is cheaper than a per-slot dependency table to maintain.</para></summary>
    internal static int TableSerial = 1;

    private static void Retire(int i)
    {
        DropHandle(i);
        if (_name[i] != 0) { _graveyard.Enqueue(_name[i]); _name[i] = 0; }
        _preview[i] = false;
        if (_state[i] == TexState.Idle) { IdleUnlink(i); _idleBytes -= _bytes[i]; }
        else _residentBytes -= _bytes[i];
        if (!_detached[i]) Unhash(i);
        _detached[i] = false;
        FreeDeps(i);
        _gen[i] = (_gen[i] + 1) & 0xFFF;
        if (_gen[i] == 0) _gen[i] = 1;
        _state[i] = TexState.Free;
        _bytes[i] = 0; _resref[i] = null; _refs[i] = 0; _pend[i] = PendNone; _cov[i] = TexCoverage.Unknown;
        _bumpyShiny[i] = false;
        _envMap[i] = false;
        _txiBlend[i] = 0; _txiDecal[i] = false; _cycle[i] = null;
        CoverageSerial++; TableSerial++;
        _freeNext[i] = _freeHead; _freeHead = i;
        _live--;
        _evicted++;
    }

    /// <summary>Take slot <paramref name="i"/> out of the name index. Caller holds <see cref="_lock"/>.</summary>
    private static void Unhash(int i)
    {
        int h = (int)((_key[i] ^ (_key[i] >> 32)) & HashMask);
        for (int step = 0; step < HashSize; step++)
        {
            int b = (h + step) & HashMask;
            if (_hash[b] == i) { _hash[b] = -1; break; }   // tombstone: the chain must not end here
            if (_hash[b] == 0) break;
        }
    }

    /// <summary>The module edge (DB mode): the next module resolves every texture through its own haks and NWSync
    /// manifest, so no slot read for the previous one may answer an acquire -- the stock pixels kept idle under
    /// ADR 0023 were what a server's retexture lost to. Idle slots retire; a referenced one leaves the index and
    /// retires at its last release, so nothing changes under a live draw. Game thread. Returns (retired, detached).</summary>
    internal static (int Retired, int Detached) ModuleEdge()
    {
        int retired = 0, detached = 0;
        lock (_lock)
        {
            while (_idleHead >= 0) { Retire(_idleHead); retired++; }
            for (int i = 1; i < _high; i++)
            {
                if (_state[i] == TexState.Free || _detached[i]) continue;
                Detach(i);
                detached++;
            }
        }
        return (retired, detached);
    }

    /// <summary>One held slot out of the index: retired at its last release. Caller holds <see cref="_lock"/>.</summary>
    private static void Detach(int i)
    {
        Unhash(i);
        _detached[i] = true;
    }

    /// <summary>GL names whose slot is gone, waiting for a context to delete them in.</summary>
    private static readonly Queue<uint> _graveyard = new();

    /// <summary>Bindless handle VALUES this registry has stopped keeping resident, waiting for the bracket to
    /// clear them out of the renderer's record blocks (<see cref="AlloyRenderer.ZeroHandle"/>). A record
    /// copies its handles at staging and holds them across frames, so a drop the records never hear about is
    /// a non-resident handle under a live draw -- garbage on NVIDIA, a GPU reset on radeonsi.
    /// <para>Sized for an area change's eviction burst so the steady state never grows it: this runs on the
    /// game thread under <see cref="_lock"/>, which is cold, but it is the same no-allocation discipline the
    /// rest of the drop path keeps.</para></summary>
    private static readonly Queue<ulong> _dyingHandles = new(256);

    /// <summary>Dying handle values the bracket has handed to <see cref="AlloyRenderer.ZeroHandle"/>. The
    /// entries that answered to them are counted by <see cref="AlloyRenderer.HandlesZeroed"/>; both are on
    /// the <c>[casters]</c> line, because a non-zero drop count with zero entries means the hazard window
    /// closed before any record had staged the handle.</summary>
    internal static long HandlesDropped => _handlesDropped;
    private static long _handlesDropped;

    /// <summary>Clear every queued dying handle out of the renderer's record blocks. GL-context thread, from
    /// <see cref="Drain"/> only -- <see cref="AlloyRenderer.ZeroHandle"/> walks renderer tables and must not
    /// run on the game thread beside a staging.</summary>
    private static void ZeroDyingHandles()
    {
        while (true)
        {
            ulong v;
            lock (_lock) { if (_dyingHandles.Count == 0) break; v = _dyingHandles.Dequeue(); }
            AlloyRenderer.ZeroHandle(v);
            _handlesDropped++;
        }
    }

    // ---- what a draw reads (lock-free; see the class remarks) ----

    /// <summary>The resident bindless handle for <paramref name="id"/>, or 0 -- which the caller draws the
    /// white stand-in for. The one thing the record feed asks per draw; a plain array read.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Handle(TexId id) => Valid(id) ? _handle[id.Index] : 0UL;

    /// <summary>This slot's GL name and resident bindless handle, or false -- the caller then draws the white
    /// stand-in, which is an explicit, counted state and not a hole. A consumer that binds by NAME as well as
    /// by handle wants both (CX-119's bitmap font atlases do).</summary>
    internal static bool TryResident(TexId id, out uint name, out ulong handle)
    {
        if (Valid(id) && _name[id.Index] != 0 && !_preview[id.Index])
        { name = _name[id.Index]; handle = _handle[id.Index]; return true; }
        name = 0; handle = 0; return false;
    }

    /// <summary>As <see cref="TryResident"/>, and a PREVIEW answers too: for a consumer that re-resolves every frame
    /// or is told on arrival (a world part, an emitter, a caster mask, the sky), never for one that keeps the name --
    /// the preview's is deleted when the full chain lands.</summary>
    internal static bool TryDrawable(TexId id, out uint name, out ulong handle)
    {
        if (Valid(id) && _name[id.Index] != 0)
        { name = _name[id.Index]; handle = _handle[id.Index]; return true; }
        name = 0; handle = 0; return false;
    }

    /// <summary>Has this slot's texture arrived?</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Resident(TexId id) => Valid(id) && _name[id.Index] != 0 && !_preview[id.Index];

    /// <summary>What the slot turned out to be. <see cref="TexKind.Plt"/> is what tells a record feed to stage
    /// the per-instance palette rows (<see cref="PltColors.Stage"/>) instead of clearing them.</summary>
    internal static TexKind KindOf(TexId id) => Valid(id) ? _kind[id.Index] : TexKind.Plain;

    /// <summary>CX-188: is this slot's answer FINAL -- will it stay what it is until something bumps
    /// <see cref="TableSerial"/>? True for a slot that can never become resident on its own: a null id (the
    /// caller declares no texture) and a FAILED load, which keeps the white stand-in as an explicit state for
    /// the rest of the session (CX-45). A slot still LOADING is not final: its arrival is what a memo keyed on
    /// the serial is waiting for. The renderer's per-slot texture memo is the caller -- without the failed and
    /// null cases a texture-less part (a junk material name on a placeable) would re-resolve every frame for
    /// ever, which is the whole cost the memo removes.</summary>
    internal static bool Final(TexId id) => id.IsNull || StateOf(id) == TexState.Failed;
    internal static TexState StateOf(TexId id) => Valid(id) ? _state[id.Index] : TexState.Free;
    /// <summary>Does this slot's sampler clamp rather than repeat? Staged into the PLT flag word.</summary>
    internal static bool Clamps(TexId id) => Valid(id) && _clamp[id.Index];

    /// <summary>CX-117: change this texture's SAMPLER state -- a <c>.txi</c> <c>clamp</c>/<c>filter</c>/
    /// <c>mipmap</c> through <c>Gob::SetTextureAttributes</c>, or the nearest-filter flag through
    /// <c>Gob::SetOverrideTextureFilter</c>. A null leaves that axis alone. True when something changed, so
    /// the caller knows to mark the records that read it.
    /// <para>It cannot be applied here: a bindless handle freezes its sampler state at the mint, which is why
    /// "a handled texture is immutable from this moment" is the registry's rule. So the wish is recorded and
    /// the BRACKET applies it -- <c>glTextureParameteri</c>, then drop and re-mint the handle -- and the
    /// caller's dirty marks are what make the records take the new one. Game thread; cold (a script call).</para></summary>
    internal static bool RequestSampler(TexId id, bool? clamp, bool? nearest, bool? mipmap)
    {
        lock (_lock)
        {
            if (!Valid(id)) return false;
            int i = id.Index;
            bool changed = false;
            if (clamp != null && _clamp[i] != clamp.Value) { _clamp[i] = clamp.Value; changed = true; }
            if (nearest != null && _nearest[i] != nearest.Value) { _nearest[i] = nearest.Value; changed = true; }
            if (mipmap != null && _mipmap[i] != mipmap.Value) { _mipmap[i] = mipmap.Value; changed = true; }
            if (!changed) return false;
            _samplerWish[i] = true;
            _samplerQ.Enqueue(i);
            _samplerAsks++;
            return true;
        }
    }

    /// <summary>The sampler state a <c>.txi</c> or a filter override asked for, and whether the bracket still
    /// owes it. <see cref="_mipmap"/> starts true because that is <c>CAurTextureBasic</c>'s own default
    /// (<c>+0xe4</c>, set in its constructor).</summary>
    private static readonly bool[] _nearest = new bool[MaxSlots];
    private static readonly bool[] _mipmap = new bool[MaxSlots];
    private static readonly bool[] _samplerWish = new bool[MaxSlots];
    private static readonly Queue<int> _samplerQ = new();
    private static long _samplerAsks, _samplerDone;

    /// <summary>What this texture's alpha MEANS, so a draw can be classified opaque, alpha-tested or blended
    /// without asking the engine's memoised <c>GetIsOpaque</c> verdicts or the GL internal format, both of
    /// which lie (<c>texture-coverage-is-a-decode-question</c>). <see cref="TexCoverage.Unknown"/> until the
    /// decode has run: a caller keeps its conservative guess and is told again at arrival, because the
    /// landing marks every dependent part <see cref="Dirty.Texture"/> through the same edge that replaces the
    /// white stand-in. Lock-free, like the other per-draw reads.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TexCoverage Coverage(TexId id) => Valid(id) ? _cov[id.Index] : TexCoverage.Unknown;
    /// <summary>Did this texture's <c>.txi</c> name a <c>bumpyshinytexture</c>? False until the decode has
    /// run, and the arrival re-asks through the same dependent edge <see cref="Coverage"/> is re-asked on.
    /// <para>This is the engine's own water term: <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 tests
    /// <c>Material::HasBumpyShinyMap</c> (under <c>g_nEnableBumpyWater</c>) before it looks at the blend pair,
    /// and that map comes from the diffuse's <c>.txi</c> and nothing else.</para></summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool BumpyShiny(TexId id) => Valid(id) && _bumpyShiny[id.Index];

    /// <summary>Does this texture's <c>.txi</c> declare an environment map? The engine's material would
    /// answer <c>HasEnvMap</c> for it, and <c>PartTriMesh::Draw</c> @0x4513d0 would route the part into the
    /// environment-mapped branch, where the diffuse alpha is REFLECTIVITY rather than coverage.</summary>
    internal static bool EnvMapped(TexId id) => Valid(id) && _envMap[id.Index];
    /// <summary>The slot's resref, for a log or a diagnostic line.</summary>
    /// <summary>The resref resident under GL texture <paramref name="name"/>, "" if none: a linear scan, for a diagnostic.</summary>
    internal static string ResrefOfGl(uint name)
    {
        if (name != 0) for (int i = 0; i < MaxSlots; i++) if (_name[i] == name) return _resref[i] ?? "";
        return "";
    }

    internal static string ResrefOf(TexId id) => Valid(id) ? _resref[id.Index] ?? "" : "";

    // ---- dependents: arrival dirties the records that drew the stand-in ----

    /// <summary>Note that part record <paramref name="part"/> draws this texture, so its arrival marks the
    /// record <see cref="Dirty.Texture"/>. Only recorded while the slot is still loading: a resident or failed
    /// slot has nothing left to announce, which is what bounds the list to the parts enrolled before the
    /// upload landed. This is the whole of CX-117's second perf bullet -- the staging walk never re-resolves a
    /// texture, because arrival comes to it. Game thread.</summary>
    internal static void AddDependent(TexId id, int part)
    {
        if ((uint)part >= SceneDb.MaxParts)
        {
            _depRefused++;
            if (LoudOnce("deppart"))
                _log?.Always($"TexRegistry: refuses part {part} as a texture dependent -- the part table holds "
                    + $"{SceneDb.MaxParts}. A caller is handing out indices that are not part records");
            return;
        }
        lock (_lock)
        {
            if (!Valid(id) || _state[id.Index] != TexState.Loading) return;
            int i = id.Index;
            int e;
            if (_depFree >= 0) { e = _depFree; _depFree = _depNext[e]; }
            else if (_depN < MaxDeps) e = _depN++;
            else
            {
                _depOverflow++;
                if (LoudOnce("deps"))
                    _log?.Always($"TexRegistry: the {MaxDeps}-edge dependent pool is FULL -- part {part} will not "
                        + "be told when its texture arrives and keeps the white stand-in until something else "
                        + "dirties it");
                return;
            }
            _depPart[e] = part; _depNext[e] = _depHead[i]; _depHead[i] = e;
        }
    }

    /// <summary>Tell every dependent part and drop the list. Game thread (it writes <see cref="SceneDb"/>);
    /// caller holds <see cref="_lock"/>. Called once per slot, at arrival or at failure.</summary>
    private static void MarkDependents(int i)
    {
        for (int e = _depHead[i]; e >= 0;)
        {
            int next = _depNext[e];
            int part = _depPart[e];
            if ((uint)part < SceneDb.MaxParts && SceneDb.Parts.LiveAt(part))
            { SceneDb.Parts.Mark(part, Dirty.Texture); _marks++; }
            _depNext[e] = _depFree; _depFree = e;
            e = next;
        }
        _depHead[i] = -1;
    }

    private static void FreeDeps(int i)
    {
        for (int e = _depHead[i]; e >= 0;)
        { int next = _depNext[e]; _depNext[e] = _depFree; _depFree = e; e = next; }
        _depHead[i] = -1;
    }

    // ---- loud, once per cause ----

    /// <summary>Is this the first time <paramref name="cause"/> has been seen? A gate rather than a
    /// message-taking helper so the interpolated string is never BUILT for a cause already logged -- which,
    /// on the table-full path, was two allocations per acquire for the rest of the session.</summary>
    private static bool LoudOnce(string cause)
    {
        lock (_loudOnce) return _loudOnce.Add(cause);
    }

    /// <summary>The <c>[alloy]</c> period line.</summary>
    internal static string Note()
        => $"texreg: {_live} textures ({_residentBytes / (1 << 20)} MiB held, {_idleBytes / (1 << 20)} MiB idle"
         + $", {_peakBytes / (1 << 20)} MiB peak), {_acquires} acquires ({_hits} shared), {_uploads} uploaded"
         + $", {_mints} handles, {_marks} arrival marks, {_evicted} evicted, {_reloads} reloaded"
         + $", {_previews} previews first ({Workers} decode workers), {CycleNote()}"
         + (_failed == 0 ? "" : $", {_failed} FAILED (white stand-in)")
         + (_full == 0 ? "" : $", {_full} REFUSED (table full)")
         + (_samplerAsks == 0 ? "" : $", {_samplerDone}/{_samplerAsks} sampler change(s) applied")
         + (_covUnknown == 0 ? "" : $", {_covUnknown} landed with coverage UNDECIDED")
         + (_bumpyShinySeen == 0 ? "" : $", {_bumpyShinySeen} name a bumpyshiny map (water)")
         + (_envMapSeen == 0 ? "" : $", {_envMapSeen} name an env map")
         + (_depOverflow + _depRefused == 0 ? "" : $", {_depOverflow} dependents LOST, {_depRefused} REFUSED");

    /// <summary>Resident slots whose coverage the decode could not decide: a draw classified against them
    /// keeps the conservative (blended) guess for ever, so the number has to be visible.</summary>
    private static long _covUnknown;

    /// <summary>Textures whose <c>.txi</c> named a bumpy-shiny map, i.e. the ones that make their parts
    /// water. Visible because "the water shader does not run" is a question about this number first.</summary>
    private static long _bumpyShinySeen, _envMapSeen;
}
