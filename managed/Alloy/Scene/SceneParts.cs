using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-104: Alloy's registry of the non-mesh scene parts the engine's per-frame buckets used to hand
/// it -- emitters (<c>emitterbucket</c>), triggers (<c>triggerbucket</c>) and beams (draw bucket 15). It is a
/// MEMBERSHIP list of the area, not a visibility list of the frame: each consumer applies its own in-view
/// rule to the small class list here, so nothing downstream inherits whatever somebody else last pushed into
/// an engine global.
/// <para><b>Two feeds, one generation.</b> <see cref="NonMeshParts"/> reports every non-mesh part
/// <see cref="SceneWalk"/> reaches down a gob's part tree, and <see cref="WalkDone"/> adds the tile side:
/// each BSP node's tile parts name their <c>TileData</c>, and the tile's own part list
/// (<see cref="EngineLayout.TileParts"/>) is where <c>ProcessStaticParts</c> @0x470970 files every part it
/// keeps -- emitter, trigger and beam included. That is the list <c>SceneBSPAddPart</c> @0x47ca10 then splits
/// into the per-class BSP node lists, so walking it reaches exactly the population the engine's own BSP
/// visitor (@0x481000) files into the three buckets, and it needs no node sub-offset beyond the tile-part
/// list Alloy already reads.</para>
/// <para><b>Deaths are O(1) and do not wait for the next walk.</b> <c>Part::~Part</c> punches a zero into the
/// dense list slot the dying part occupies (<see cref="Died"/>); consumers skip zeros. The same tombstone
/// rule as <c>PartTable</c>: a pointer match on a freed entry is a MISS and the slot is reused, so a
/// recycled allocation is never answered for by its predecessor.</para>
/// <para>Hot-path rules: the per-frame side is a span walk with no allocation. Building the registry runs on
/// the walk's own edge (an area load, a scene epoch bump), never per frame.</para></summary>
internal static unsafe class SceneParts
{
    /// <summary>The three classes this registry keeps. A part is at most one of them.</summary>
    internal const byte KindNone = 0, KindEmitter = 1, KindTrigger = 2, KindBeam = 3;

    /// <summary>Ceilings, per class, from the populations they hold: an area's emitters are its torches and
    /// braziers (tens to low hundreds), its triggers the area transitions (a handful), its beams the static
    /// projection volumes. Past a cap the rest of that class is not registered for the area -- counted and
    /// logged once, never silently (CX-45).</summary>
    private const int MaxEmitters = 2048, MaxTriggers = 512, MaxBeams = 2048;
    private const int MaxParts = MaxEmitters + MaxTriggers + MaxBeams;
    /// <summary>Intern table size: a power of two above 2x <see cref="MaxParts"/>, so a probe never walks far
    /// even with the tables full.</summary>
    private const int HashSize = 1 << 14;
    private const int HashMask = HashSize - 1;

    private static readonly IntPtr[] _ptr = new IntPtr[MaxParts];
    private static readonly byte[] _kind = new byte[MaxParts];
    private static readonly bool[] _freed = new bool[MaxParts];
    private static readonly int[] _gen = new int[MaxParts];
    /// <summary>Where this entry sits in its class's dense list, so <see cref="Died"/> is O(1).</summary>
    private static readonly int[] _dense = new int[MaxParts];
    private static readonly int[] _slot = new int[HashSize];
    /// <summary>Entry indices whose part is gone and which are back in the pool. Without this the entry array
    /// is a HIGH-WATER MARK rather than a capacity: <see cref="Insert"/> reclaims a freed entry only if it
    /// happens to lie on the new key's probe chain, so VFX and spell-emitter churn walks <see cref="_count"/>
    /// to <see cref="MaxParts"/> and then every key whose chain reaches an empty hash bucket is refused --
    /// emitters stop registering and particles stop appearing until the area changes.</summary>
    private static readonly int[] _free = new int[MaxParts];
    private static int _freeN;
    /// <summary>Entries ever handed out. Live capacity is this minus <see cref="_freeN"/>, and the
    /// compaction at every walk-end sweep brings the two back together.</summary>
    private static int _count;
    private static long _reused, _compactions;

    private static readonly IntPtr[] _emitters = new IntPtr[MaxEmitters];
    private static readonly IntPtr[] _triggers = new IntPtr[MaxTriggers];
    private static readonly IntPtr[] _beams = new IntPtr[MaxBeams];
    private static int _emitterN, _triggerN, _beamN;

    private static ExtenderLog? _log;
    private static BspTraverseFn _traverse;
    private static bool _ready, _cappedLogged;
    private static int _tileGen;
    private static long _capped, _rebuilds, _deaths, _sweeps;
    /// <summary>Tiles reached by the last rebuild.</summary>
    private static int _lastTiles;

    /// <summary>Resolved enough to classify and to walk the area's BSP? False leaves every list empty, which
    /// each consumer reads as "the registry has nothing" and answers for itself.</summary>
    internal static bool Ready => _ready;

    /// <summary>The first thing that did not resolve, or null.</summary>
    internal static string? Missing { get; private set; }

    internal static void Resolve(ExtenderLog log)
    {
        _log = log;
        _traverse = EngineFn.BspTraverse;
        Array.Fill(_slot, -1);
        Missing = _traverse == null ? SymbolMap.BspTraverse
                : !EngineLayout.Require(log, "SceneParts", "Part", "TileData", "PartTriMesh") ? "Part/TileData layout"
                : null;
        _ready = Missing == null;
        if (!_ready)
            _log.Always($"SceneParts: {Missing} did not resolve -- Alloy has no emitter, trigger or beam "
                      + "registry this session, so those three classes read the engine buckets instead "
                      + "(the same route /buckets emitters 0 takes, deliberately)");
    }

    // ---- the feeds -------------------------------------------------------------------------------

    /// <summary>A non-mesh part reached on walk generation <paramref name="gen"/>.</summary>
    internal static void Visit(IntPtr part, bool bsp, int gen)
    {
        if (!_ready || part == IntPtr.Zero) return;
        int e = Find(part);
        if (e >= 0) { _gen[e] = gen; return; }
        byte kind = Classify(part, out IntPtr key);
        if (kind == KindNone || key == IntPtr.Zero) return;
        e = Insert(key);
        if (e < 0) { Cap("part table"); return; }
        _kind[e] = kind; _gen[e] = gen; _dense[e] = -1;
    }

    /// <summary>The walk of generation <paramref name="gen"/> finished. The tile side is added here -- the
    /// walk reaches a tile's TRIMESH parts and they name their tile, but a tile's emitter, trigger and beam
    /// parts are siblings in the tile's own flat list rather than children of a mesh, so nothing above can
    /// reach them. Then everything not seen on this generation is swept and the dense lists are rebuilt.
    /// <para>The tile-fade registry is rebuilt from the same traversal -- one BSP walk, not two.</para></summary>
    internal static void WalkDone(int gen)
    {
        if (!_ready) return;
        ScanTiles(gen);
        Settle(gen);
        _rebuilds++;
    }

    /// <summary><c>Part::~Part</c> for <paramref name="part"/>, before the engine frees it. Compares the
    /// pointer, never dereferences it -- and takes the part OUT of the dense list now rather than at the next
    /// walk, because a consumer reading it in between would dereference freed memory.</summary>
    internal static void Died(IntPtr part)
    {
        if (!_ready || part == IntPtr.Zero) return;
        // Every dying part is offered to the tile registry first: a tile's TileData is only safe to read while
        // one of its parts is alive (see TileFadeRamp.PartDied), and Part::~Part is the only notice there is.
        TileFadeRamp.PartDied(part);
        int e = Find(part);
        if (e < 0) return;
        _deaths++;
        int d = _dense[e];
        Release(e);
        if (d < 0) return;
        switch (_kind[e])
        {
            case KindEmitter: if (d < _emitterN) _emitters[d] = IntPtr.Zero; break;
            case KindTrigger: if (d < _triggerN) _triggers[d] = IntPtr.Zero; break;
            case KindBeam: if (d < _beamN) _beams[d] = IntPtr.Zero; break;
        }
    }

    /// <summary>A new Scene: every part in the table belongs to the old one and its address will be recycled.
    /// Cheaper and safer than discovering that an entry at a time.</summary>
    internal static void Reset()
    {
        Clear();
        TileFadeRamp.Reset();
    }

    private static void Clear()
    {
        _count = 0; _freeN = 0; _emitterN = 0; _triggerN = 0; _beamN = 0; _lastTiles = 0;
        Array.Fill(_slot, -1);
        Array.Clear(_ptr); Array.Clear(_kind); Array.Clear(_freed); Array.Clear(_gen); Array.Clear(_dense);
    }

    // ---- what the consumers read ----------------------------------------------------------------

    /// <summary>The area's emitter parts (<c>PartEmitter*</c>). An entry may be zero -- a part that died since
    /// the last walk -- and the caller skips those.</summary>
    internal static ReadOnlySpan<IntPtr> Emitters => new(_emitters, 0, _emitterN);
    /// <inheritdoc cref="Emitters"/>
    internal static ReadOnlySpan<IntPtr> Triggers => new(_triggers, 0, _triggerN);
    /// <inheritdoc cref="Emitters"/>
    internal static ReadOnlySpan<IntPtr> Beams => new(_beams, 0, _beamN);

    /// <summary>The one thing a live session must not discover as "the effect stopped playing": the registry
    /// is EMPTY while the engine's own list for the same class is not, which means the two feeds did not reach
    /// a population they were supposed to. Logged in full on the first occurrence per class and counted after
    /// (CX-45) -- and deliberately NOT a fallback: a quiet switch back to the bucket would hide exactly the
    /// gap this line exists to report, and the knob named in it is the A/B. Callers consult the engine's count
    /// only while their own set is empty, so a populated set reads no engine bucket at all.</summary>
    internal static void WarnEmpty(int kind, int mine, int engine)
    {
        if ((uint)kind >= 4 || mine > 0 || engine <= 0) return;
        _emptyHits[kind]++;
        if (_emptyLogged[kind]) return;
        _emptyLogged[kind] = true;
        _log?.Always($"SceneParts: the {_kindNames[kind]} registry is EMPTY while the engine's own list holds "
                   + $"{engine} -- the scene walk and the tile scan between them did not reach that class, so "
                   + $"CX will neither simulate nor draw it. Not a transient: /buckets {_kindKeys[kind]} 0 is "
                   + "the A/B back to the engine's list while this is diagnosed");
    }

    private static readonly bool[] _emptyLogged = new bool[4];
    private static readonly long[] _emptyHits = new long[4];
    private static readonly string[] _kindNames = ["emitter", "trigger", "beam", "grass bin"];
    private static readonly string[] _kindKeys = ["emitters", "triggers", "beams", "grassbins"];

    /// <summary>The clause the <c>buckets</c> knob group prints.</summary>
    internal static string Note()
        => _ready
            ? $"{_emitterN} emitter(s), {_triggerN} trigger(s), {_beamN} beam(s) over {_lastTiles} tile(s)"
              + $"; {_count} entr(ies) live of {MaxParts}, {_rebuilds} rebuild(s), {_sweeps} swept, "
              + $"{_deaths} death notice(s), {_reused} entr(ies) reused from the pool"
              + (_capped > 0 ? $", CAPPED {_capped}x" : "")
              + (_emptyHits[0] + _emptyHits[1] + _emptyHits[2] + _emptyHits[3] > 0
                 ? $", EMPTY-WHILE-ENGINE-HAD-ONE {_emptyHits[0]}/{_emptyHits[1]}/{_emptyHits[2]}/{_emptyHits[3]}"
                 : "")
            : $"stood down ({Missing} unresolved)";

    // ---- the tile side ---------------------------------------------------------------------------

    private static void ScanTiles(int gen)
    {
        IntPtr scene = SceneWalk.WorldScene;
        if (scene == IntPtr.Zero) return;
        IntPtr root = new CX.Engine.Scene(scene).BspRoot;
        if (root == IntPtr.Zero) return;
        _tileGen = gen;
        TileFadeRamp.BeginRebuild();
        _traverse(root, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnBspNode, IntPtr.Zero);
        _lastTiles = TileFadeRamp.EndRebuild();
    }

    /// <summary>One BSP node: its tile parts name the tile, grow the tile's box and declare the tile-fade
    /// class; a tile reached for the first time this rebuild also contributes its non-mesh parts.</summary>
    [UnmanagedCallersOnly]
    private static void OnBspNode(IntPtr node, IntPtr ctx)
    {
        var bsp = new BSPNode(node);
        IntPtr* items = bsp.TileParts;
        if (items == null) return;
        int n = bsp.TilePartCount;
        for (int i = 0; i < n; i++)
        {
            IntPtr tri = items[i];
            if (tri == IntPtr.Zero) continue;
            IntPtr tile = new PartTriMesh(tri).Tile.Ptr;
            if (tile == IntPtr.Zero) continue;
            int t = TileFadeRamp.NoteTile(tile, out bool fresh);
            if (t < 0) continue;
            TileFadeRamp.NoteTilePart(t, tri);
            if (!fresh) continue;
            var td = new TileData(tile);
            IntPtr* parts = (IntPtr*)td.Parts;
            int m = td.PartCount;
            if (parts == null) continue;
            for (int j = 0; j < m; j++) Visit(parts[j], true, _tileGen);
        }
    }

    // ---- the table -------------------------------------------------------------------------------

    /// <summary>Which of the three classes <paramref name="part"/> is, asked the engine's own way: the
    /// <c>AsPart*</c> virtuals <c>SceneBSPAddPart</c> @0x47ca10 discriminates with, each of which returns null
    /// for every other class. A part with no vtable yet is nothing.
    /// <para><paramref name="key"/> is the DOWNCAST pointer, which is what the registry stores -- the engine's
    /// own buckets hold the same downcast, every consumer here wants the derived view, and single inheritance
    /// makes it the same address as the walked <c>Part*</c>, so the <c>Part::~Part</c> notice still finds
    /// it.</para></summary>
    private static byte Classify(IntPtr part, out IntPtr key)
    {
        key = IntPtr.Zero;
        if (*(IntPtr*)part == IntPtr.Zero) return KindNone;
        var p = new CX.Engine.Part(part);
        if (p.AsPartEmitterFn != IntPtr.Zero)
        {
            PartEmitter em = p.AsPartEmitter();
            if (!em.IsNull) { key = em.Ptr; return KindEmitter; }
        }
        if (p.AsPartTriggerFn != IntPtr.Zero)
        {
            PartTrigger tr = p.AsPartTrigger();
            if (!tr.IsNull) { key = tr.Ptr; return KindTrigger; }
        }
        if (p.AsPartBeamFn != IntPtr.Zero)
        {
            PartBeam bm = p.AsPartBeam();
            if (!bm.IsNull) { key = bm.Ptr; return KindBeam; }
        }
        return KindNone;
    }

    private static int Bucket(IntPtr part) => (int)((ulong)part >> 4) & HashMask;

    /// <summary>The entry for <paramref name="part"/>, or -1. A pointer match on a FREED entry is a miss: the
    /// allocator hands addresses back, and a tombstone that still answered would make the new part
    /// permanently unregisterable (the #221 bug class).</summary>
    private static int Find(IntPtr part)
    {
        int h = Bucket(part);
        for (int probe = 0; probe <= HashMask; probe++, h = (h + 1) & HashMask)
        {
            int e = _slot[h];
            if (e < 0) return -1;
            if (_ptr[e] == part && !_freed[e]) return e;
        }
        return -1;
    }

    /// <summary>Claim an entry for <paramref name="part"/>: a freed entry already on the new key's probe
    /// chain first, else a fresh index, else one out of the pool <see cref="_free"/>. -1 = every entry is
    /// LIVE, which is the only state that is actually full.
    /// <para>A pooled entry keeps its old hash slot pointing at it, which costs a longer probe chain and
    /// nothing else: that slot's key no longer matches <c>_ptr[e]</c>, so <see cref="Find"/> walks past it and
    /// <see cref="Insert"/> sees a live entry and continues. <see cref="Compact"/> retires those stale
    /// references at the next sweep, which is what keeps the chains short.</para></summary>
    private static int Insert(IntPtr part)
    {
        int h = Bucket(part);
        for (int probe = 0; probe <= HashMask; probe++, h = (h + 1) & HashMask)
        {
            int e = _slot[h];
            if (e >= 0)
            {
                if (!_freed[e]) continue;
                Claim(e, part);
                return e;
            }
            if (_count < MaxParts) e = _count++;
            else if (_freeN > 0) { e = _free[--_freeN]; _reused++; }
            else return -1;
            _slot[h] = e;
            Claim(e, part);
            return e;
        }
        return -1;
    }

    private static void Claim(int e, IntPtr part)
    {
        _ptr[e] = part; _freed[e] = false; _kind[e] = KindNone; _dense[e] = -1;
    }

    /// <summary>Return an entry to the pool. The #221 rule stands on top of it: the entry stays FREED, so a
    /// pointer match on it is still a miss until it is claimed again.</summary>
    private static void Release(int e)
    {
        _freed[e] = true; _dense[e] = -1;
        if (_freeN < MaxParts) _free[_freeN++] = e;
    }

    /// <summary>Renumber the live entries densely and rebuild the hash over them, once per walk-end sweep.
    /// After this <see cref="_count"/> IS the live count, the pool is empty and no hash slot references an
    /// entry that is not live -- so neither the entry array nor the probe chains can creep across an area.
    /// Nothing outside holds an entry index across a sweep (<c>_dense</c> is rebuilt right after, and
    /// <see cref="TileFadeRamp"/> keeps its own table), so the renumbering is invisible.</summary>
    private static void Compact()
    {
        _compactions++;
        int live = 0;
        for (int e = 0; e < _count; e++)
        {
            if (_freed[e] || _kind[e] == KindNone) continue;
            if (live != e)
            {
                _ptr[live] = _ptr[e]; _kind[live] = _kind[e]; _gen[live] = _gen[e];
                _freed[live] = false;
            }
            _dense[live] = -1;
            live++;
        }
        for (int e = live; e < _count; e++) { _ptr[e] = IntPtr.Zero; _kind[e] = KindNone; _freed[e] = false; }
        _count = live;
        _freeN = 0;
        Array.Fill(_slot, -1);
        for (int e = 0; e < live; e++) Link(e);
    }

    /// <summary>Point the first free hash slot on this entry's probe chain at it. Only ever called on a table
    /// whose slots were just cleared, so it cannot displace a live reference.</summary>
    private static void Link(int e)
    {
        int h = Bucket(_ptr[e]);
        for (int probe = 0; probe <= HashMask; probe++, h = (h + 1) & HashMask)
            if (_slot[h] < 0) { _slot[h] = e; return; }
    }

    /// <summary>The walk-end half of <see cref="WalkDone"/>, without the tile scan: release what the walk did
    /// not reach, compact, refile. Its own method so the SelfCheck drives the production sequence rather than
    /// a copy of it -- the leak this closes was invisible to a check that called the three itself.</summary>
    private static void Settle(int gen)
    {
        Sweep(gen);
        Compact();
        Rebuild();
    }

    /// <summary>Everything not visited on <paramref name="gen"/> left the scene: mark it freed so its slot is
    /// reusable and it drops out of the rebuilt lists. Nothing is dereferenced here either -- a part that left
    /// may already be gone.</summary>
    private static void Sweep(int gen)
    {
        for (int e = 0; e < _count; e++)
        {
            if (_freed[e] || _kind[e] == KindNone || _gen[e] == gen) continue;
            Release(e); _sweeps++;
        }
    }

    private static void Rebuild()
    {
        _emitterN = 0; _triggerN = 0; _beamN = 0;
        for (int e = 0; e < _count; e++)
        {
            if (_freed[e]) continue;
            switch (_kind[e])
            {
                case KindEmitter:
                    if (_emitterN >= MaxEmitters) { Cap("emitters"); break; }
                    _dense[e] = _emitterN; _emitters[_emitterN++] = _ptr[e];
                    break;
                case KindTrigger:
                    if (_triggerN >= MaxTriggers) { Cap("triggers"); break; }
                    _dense[e] = _triggerN; _triggers[_triggerN++] = _ptr[e];
                    break;
                case KindBeam:
                    if (_beamN >= MaxBeams) { Cap("beams"); break; }
                    _dense[e] = _beamN; _beams[_beamN++] = _ptr[e];
                    break;
            }
        }
    }

    private static void Cap(string what)
    {
        _capped++;
        if (_cappedLogged) return;
        _cappedLogged = true;
        _log?.Always($"SceneParts: the {what} ceiling is full in this area -- the rest of that class is not "
                   + "registered, so it neither simulates nor draws through the owned path. That is a real "
                   + "bound being hit: raise the cap against a census, do not ignore the line");
    }

    // ---- the check -------------------------------------------------------------------------------

    /// <summary>Off-device: the intern table's invariants, over synthetic pointers. No engine object is
    /// touched (classification needs a vtable), so the check drives <see cref="Insert"/> directly -- which is
    /// where the tombstone rules that have been wrong twice before actually live.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "SceneParts.SelfCheck");
        bool savedReady = _ready, savedLogged = _cappedLogged;
        long savedCapped = _capped;
        _ready = true;
        Clear();

        IntPtr a = (IntPtr)0x11110, b = (IntPtr)0x22220, c = (IntPtr)0x33330;
        int ea = Insert(a), eb = Insert(b);
        _kind[ea] = KindEmitter; _gen[ea] = 7;
        _kind[eb] = KindTrigger; _gen[eb] = 7;
        check.Expect(ea >= 0 && eb >= 0 && ea != eb, "two parts take two entries");
        check.Expect(Find(a) == ea && Find(b) == eb && Find(c) < 0, "Find answers by pointer and misses the unknown");
        Rebuild();
        check.Expect(_emitterN == 1 && _triggerN == 1 && _beamN == 0, "the rebuild files each entry in its class list");
        check.Expect(_emitters[0] == a && _triggers[0] == b, "...in that class's own list");

        Died(a);
        check.Expect(_emitters[0] == IntPtr.Zero, "a death notice zeroes the dense slot without waiting for a walk");
        check.Expect(Find(a) < 0, "a freed entry no longer answers for its pointer");
        int ea2 = Insert(a);
        check.Expect(ea2 == ea, "the freed slot is reused rather than leaked");
        _kind[ea2] = KindEmitter; _gen[ea2] = 7;
        check.Expect(Find(a) == ea2, "...and the recycled address is registerable again");

        _gen[eb] = 6;
        Sweep(7);
        Rebuild();
        check.Expect(_emitterN == 1 && _triggerN == 0, "the sweep drops what the last walk did not visit");

        // The leak the review found. Before the pool and the compaction, _count was a HIGH-WATER MARK of
        // entries ever handed out, not a capacity: Insert reclaimed a freed entry only when it happened to
        // land on the new key's probe chain, so VFX and spell-emitter churn walked it to MaxParts and then
        // every key whose chain reached an empty hash bucket was refused for the rest of the area -- emitters
        // silently stopped registering and particles stopped appearing. Both routes out are exercised:
        // walk-end sweeps, and death notices with no sweep at all.
        Clear();
        const int ChurnRounds = 8;
        long churned = 0;
        int refused = 0;
        for (int r = 0; r < ChurnRounds; r++)
        {
            for (int i = 0; i < MaxParts; i++, churned++)
            {
                // not-an-offset: synthetic part addresses, never dereferenced (Insert compares pointers)
                IntPtr q = (IntPtr)(0x200000 + churned * 0x40);
                int e = Insert(q);
                if (e < 0) { refused++; continue; }
                _kind[e] = KindEmitter; _gen[e] = r;
            }
            Settle(r + 1);   // nothing carries the next generation, so the whole round is released
        }
        check.Expect(refused == 0,
                     $"{churned} parts of sweep churn register without exhausting the {MaxParts} entries");
        check.Expect(_count == 0, "...and after the sweep the live count is what is live, not the high mark");

        // The same volume with NO sweep between: every entry comes back through the death notice, which is
        // the pool's own path.
        Clear();
        refused = 0;
        for (int i = 0; i < MaxParts * 3; i++)
        {
            IntPtr q = (IntPtr)(0x900000 + (long)i * 0x40);
            int e = Insert(q);
            if (e < 0) { refused++; continue; }
            _kind[e] = KindEmitter; _gen[e] = 1;
            Rebuild();          // so the entry has a dense slot for Died to clear
            Died(q);
        }
        check.Expect(refused == 0, "insert-and-die churn past the cap is served from the pool");

        // ...and the table still works afterwards: a fresh batch registers, is found, and files correctly.
        Clear();
        for (int r = 0; r < ChurnRounds; r++)
        {
            for (int i = 0; i < MaxParts; i++)
            {
                int e = Insert((IntPtr)(0xA00000 + ((long)r * MaxParts + i) * 0x40));
                if (e >= 0) { _kind[e] = KindBeam; _gen[e] = r; }
            }
            Settle(r + 1);
        }
        IntPtr after = (IntPtr)0xF00000;
        int ea3 = Insert(after);
        check.Expect(ea3 >= 0, "a new part still registers after the churn");
        _kind[ea3] = KindTrigger; _gen[ea3] = 9;
        Settle(9);
        check.Expect(Find(after) >= 0 && _triggerN == 1 && _triggers[0] == after,
                     "...and the compaction leaves it findable and filed in its class");

        Clear();
        _ready = savedReady; _capped = savedCapped; _cappedLogged = savedLogged;
        log.Info(check.Ok ? "SceneParts.SelfCheck: passed" : "SceneParts.SelfCheck: FAILED");
        return check.Ok;
    }
}
