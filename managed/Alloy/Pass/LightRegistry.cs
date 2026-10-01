using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-108: Alloy's own light registry. Every light the renderer shades, halos, bakes or tints from is
/// enumerated HERE, out of the engine's own containers, instead of being read off <c>LightManager</c>'s
/// per-frame lists -- Alloy reads no <c>LightManager</c> field.
/// <para><b>Two feeds, both authoritative</b> (docs/research/light-registry.md §3):
/// <list type="bullet">
/// <item><see cref="Visit"/> -- the <see cref="NonMeshParts"/> seam, i.e. every part <see cref="SceneWalk"/>
/// reaches that is not a <c>PartTriMesh</c>. A light is told by the engine's own cast
/// <c>Part::AsPartLight()</c> (<see cref="EngineLayout.PartAsLightSlot"/>), exactly as
/// <c>SceneBSPAddPart</c> @0x47ca10 tells one. This reaches every GOB light -- and all three LOD roots plus
/// the append roots, where the engine's own <c>ListSpecialPartsR</c> @0x462d50 walks <c>Gob+0x90</c>
/// alone.</item>
/// <item>...and the same seam again for the TILE lights, which no part-TREE walk can reach --
/// <c>CollapsePartTree</c> @0x462ff0 flattens a tile's tree and CLEARS every child list. They come from the
/// tile's own part list (<c>TileData+0x30</c>), where <c>ProcessStaticParts</c> @0x470970 files every part
/// whose <c>AsPartLight</c> is non-null; <see cref="SceneParts.TileMesh"/> walks it through
/// <see cref="NonMeshParts"/> during <see cref="SceneWalk"/>'s one BSP traversal.</item>
/// </list></para>
/// <para><b>And one read-only confirmation.</b> The BSP node's <c>CExoArrayList&lt;PartLight*&gt;</c> at
/// <see cref="EngineLayout.BspDataLight"/> holds the same tile lights (<c>SceneBSPAddTileData</c> @0x47cd00
/// routes every <c>TileData+0x30</c> part through <c>SceneBSPAddPart</c> @0x47ca10) AND every gob light
/// (<c>Scene::ChildAdd</c> @0x461d50 -&gt; <c>ListSpecialPartsR</c>), and nothing prunes it on part death --
/// so it is a list of pointers, not of lights. <see cref="NoteBspNode"/> reads it to CONFIRM and never to
/// admit; see there for why an admission would be a use-after-free.</para>
/// <para><b>Three lifetime edges, no inference.</b> Arrival is a walk generation; departure is the sweep at
/// the end of a walk (a part the walk did not reach left the scene -- <c>Scene::ChildRemove</c> @0x462cc0
/// unlinks without freeing); death is <c>Part::~Part</c> through <see cref="Died"/>, which COMPARES the
/// pointer and never dereferences it, and marks the slot freed so a recycled address is re-recorded instead of
/// landing on a tombstone (memories <c>owned-pass-freed-part</c>, <c>dead-entry-tombstone</c>).</para>
/// <para><b>Selection is Alloy's</b> (<see cref="Select"/>): the candidate filter of
/// <c>SetupLightsForThisPositionAndRadius</c> @0x4c1df0, the score of <c>PrioritizeLights</c> @0x4c0940 and
/// the cap of <c>GetMaxLights</c>, ported as arithmetic. <see cref="Wanted"/> is what <see cref="LightFade"/>
/// fades toward.</para>
/// <para>Hot: <see cref="Select"/> runs once per world frame over the live set and allocates nothing; the
/// registry itself only changes on walk edges.</para></summary>
internal static unsafe class LightRegistry
{
    /// <summary>Table size, as a power-of-two shift. Sized from the AREA's own bound, not from a busy plaza:
    /// a 32x32 tile area (the engine's maximum) at the 2,4 lights per tile a lamp-lit city tileset measures
    /// is ~2500 tile lights before a single gob light, and the first live read of a city exterior held 1422
    /// lit of 1464 tile lights. 16384 is ~6x that ceiling, and <see cref="Stats"/> prints the occupancy so
    /// the headroom is a number rather than a hope.
    /// <para>The probe is the WHOLE table (<see cref="Find"/>, <see cref="Insert"/>), so
    /// <see cref="_overflow"/> can only mean 16384 lights are live at once -- a real bug, loudly, per CX-45.
    /// The predecessor of this pair was <c>Slots = 2048, Probe = 8</c>, which spilled every light whose
    /// 8-slot window was full: at 1422 lights in 2048 slots that was <c>table-full=148502</c> in one session,
    /// and every one of them a lamp that silently never lit.</para></summary>
    private const int SlotBits = 14;
    private const int Slots = 1 << SlotBits;
    /// <summary>The bound every table keyed on the SAME population has to be sized from -- see
    /// <c>SceneInputs</c>' animated-light peak table, which was sized for a busy plaza and silently stopped
    /// stabilising past it.</summary>
    internal const int Capacity = Slots;
    /// <summary>Guard on the parent walks below. A part tree is wide and shallow.</summary>
    private const int DepthMax = 64;

    /// <summary><c>SetupLightsForThisPositionAndRadius</c>' radius floor, read off the instructions at
    /// <c>ManageSceneBSP</c>+0x613 (<c>movss 0x12f1d14</c> = 45.0, then <c>maxss</c> the scene's fog end).</summary>
    private const float SetupRadiusFloor = 45f;
    /// <summary>The score <c>PrioritizeLights</c> gives a light that cannot contribute -- sorts last.</summary>
    private const float ScoreNone = 100000f;
    /// <summary>...and the one it gives the single gob being previewed in isolation -- sorts first.</summary>
    private const float ScoreOnlyGob = -1f;
    /// <summary>The score's denominator term for a light node that does not fade (<c>fadingLight 0</c>); the
    /// engine spells it <c>20 + 1</c>.</summary>
    private const float ScoreFlatTerm = 21f;

    // ---- the table: open addressing on the PartLight pointer ----
    private static readonly IntPtr[] _id = new IntPtr[Slots];
    private static readonly bool[] _freed = new bool[Slots];
    private static readonly int[] _gen = new int[Slots];
    private static readonly bool[] _bsp = new bool[Slots];
    private static readonly IntPtr[] _gob = new IntPtr[Slots];
    /// <summary>The part-tree ROOT this light hangs off, walked up once at arrival. What tells a light in a
    /// LOD alternate the gob is not showing from one in the active tree -- the same question
    /// <c>AlloyRenderer.IdentityOk</c> asks of geometry (#336).</summary>
    private static readonly IntPtr[] _root = new IntPtr[Slots];
    /// <summary>Who holds this slot, counting from 1: bumped every time <see cref="Insert"/> CLAIMS it, and
    /// never otherwise. A slot index alone does not name a light -- <see cref="Insert"/> reclaims a tombstone
    /// and clears its freed mark, and the allocator hands the same address back -- so this is what the
    /// selection carries beside the index, and a mismatch is a gap rather than a different light's fields.</summary>
    private static readonly long[] _tenant = new long[Slots];
    /// <summary>...counting from 1, and 64-bit so the question of it wrapping does not arise: a wrapped
    /// counter could hand the same tenancy to two successive tenants of one slot, which is exactly the
    /// wrong-light read this pair exists to stop.</summary>
    private static long _tenantSeq;

    // ---- this frame's view of it ----
    /// <summary>Live slots in registry order: area-wide, visible, LOD-active. What the light walk and the
    /// lightmap bake enumerate -- the bake's static set has to cover the AREA, not the near-camera slice.</summary>
    private static readonly int[] _sel = new int[Slots];
    /// <summary><see cref="_tenant"/> of each <see cref="_sel"/> entry when the selection was taken.</summary>
    private static readonly long[] _selTenant = new long[Slots];
    private static int _selN;
    private static readonly float[] _score = new float[Slots];
    private static readonly bool[] _cand = new bool[Slots];
    private static readonly bool[] _want = new bool[Slots];
    /// <summary>Live slots in table order, rebuilt by the sweep. The per-frame pass walks THIS, not the table:
    /// a table sized for the area's ceiling, of which a fraction is tenanted, is a scan nobody needs every
    /// frame. An entry here can have been retired since -- see <see cref="SelectInner"/>'s guard.</summary>
    private static readonly int[] _live = new int[Slots];
    private static int _liveN;
    /// <summary>Where slot <c>s</c> sits in <see cref="_live"/>, or -1. The list is MAINTAINED -- a claim lists,
    /// a retirement unlists, both O(1) -- rather than republished by scanning all <see cref="Slots"/> slots.
    /// It used to be rebuilt on every frame a per-gob delta drained, which is most frames, in the stream whose
    /// whole purpose was removing an O(table) walk from the frame.
    /// <para><see cref="Sweep"/> still rebuilds wholesale: it is the pass that retires, it already walks every
    /// slot to find the stale generations, and the two must agree -- which the self-check pins by driving a
    /// delta sequence and then a full rebuild and comparing the sets.</para></summary>
    private static readonly int[] _liveAt = NewLiveAt();
    private static int[] NewLiveAt() { var a = new int[Slots]; Array.Fill(a, -1); return a; }

    /// <summary>Put a freshly claimed slot in the live list.</summary>
    private static void ListLive(int s)
    {
        if (_liveAt[s] >= 0 || _liveN >= Slots) return;
        _liveAt[s] = _liveN; _live[_liveN++] = s;
    }

    /// <summary>...and take a retired one out, by swapping the last member into its place.</summary>
    private static void UnlistLive(int s)
    {
        int k = _liveAt[s];
        if (k < 0) return;
        _liveAt[s] = -1;
        int last = --_liveN;
        if (k == last) return;
        int moved = _live[last];
        _live[k] = moved; _liveAt[moved] = k;
    }

    private static ExtenderLog? _log;
    private static bool _probed;
    private static GetLightAdjustedRadiusFn _adjRadius;
    private static LightMgrGetMaxLightsFn _maxLights;
    private static IntPtr* _onlyGob;

    /// <summary>Period-line counters. Arrivals, departures and deaths are RATES (walk edges); the rest are
    /// populations, read at the last <see cref="Select"/>.</summary>
    private static long _arrived, _left, _died, _overflow, _reclaimed;
    private static int _candN, _wantN, _hidden, _inactiveLod, _tile;
    /// <summary>Lights the last walk's own feeds did not reach and the tile BSP's node lists KEPT alive -- gob
    /// lights on an inactive LOD root, which the engine still lists and <c>Select</c>'s own inactive-LOD filter
    /// answers for. A per-walk population, reset by <see cref="BspScanBegin"/>, not a rate.</summary>
    private static int _nodeKept;
    /// <summary>Slots ever claimed (live plus tombstone) and the tombstone half of that -- the table's
    /// OCCUPANCY, which is what says how far the fixed size is from binding. <see cref="_overflow"/> can only
    /// rise once <see cref="_used"/> reads <see cref="Slots"/>.</summary>
    private static int _used, _tombs;
    /// <summary>The overflow has been said out loud once this session (CX-45: first failure per site).</summary>
    private static bool _overflowLogged;

    /// <summary>A symbol the registry needs did not resolve. Kept apart from <see cref="Use"/> so the knob's
    /// meaning ("the player asked for the engine's lists") does not double as "this client is broken".</summary>
    private static bool _unavailable;

    /// <summary>Is the registry Alloy's light source this session? <c>light.registry 0</c> hands the
    /// enumeration back to the engine's <c>LightManager</c> lists -- the A/B for anything that looks wrong.</summary>
    internal static bool Use => Lighting.Registry.V;

    /// <summary>The registry can be the light source: armed, and the knob is on.</summary>
    internal static bool Live => !_unavailable && Use;

    /// <summary>Lights in this frame's set: area-wide, visible, in an active LOD tree.</summary>
    internal static int Count => _selN;

    /// <summary>Light <paramref name="i"/> of <see cref="Count"/>, or the NULL view if that entry is no longer
    /// the light the last <see cref="Select"/> named. The pointer is the engine's and is freed at the area
    /// edge: read inside the frame that asked, never retained.
    /// <para>The null case is load-bearing, not defensive. <see cref="Select"/> runs once a world frame and
    /// <see cref="Halos"/> reads this set EARLIER in the frame than that, so it reads the PREVIOUS frame's
    /// selection -- across a window in which <c>Part::~Part</c> can have retired any entry in it AND a walk can
    /// have handed the slot to a different light. Both are caught by the <see cref="_tenant"/> compare, and the
    /// freed mark alone caught only the first: <see cref="Insert"/> reclaims a tombstone and clears that mark,
    /// so a stale index then resolved to an unrelated live <c>PartLight</c> -- a flare staged at another lamp's
    /// position and colour for a frame. Every caller skips a null light.</para></summary>
    internal static PartLight Light(int i)
    {
        int s = _sel[i];
        return new(_freed[s] || _tenant[s] != _selTenant[i] ? IntPtr.Zero : _id[s]);
    }

    /// <summary>Does the frame WANT light <paramref name="i"/> lit -- the ported
    /// <c>SetupLights</c> + <c>PrioritizeLights</c> + <c>GetMaxLights</c> verdict? <see cref="LightFade"/>
    /// integrates toward 1 for true and 0 for false, which is what <c>ControlLightsFade</c> did with the
    /// engine's own two lists. False for a gap, by the same tenancy test <see cref="Light"/> makes: a slot that
    /// has changed hands is not carrying last frame's verdict for its new tenant.</summary>
    internal static bool Wanted(int i)
    {
        int s = _sel[i];
        return _tenant[s] == _selTenant[i] && _want[s];
    }

    /// <summary>The two index -1 lights, picked the way <c>LightManager::UpdateLights</c> @0x4c06c0 picks them:
    /// a light with <c>range &gt;= 1000</c>, <c>kind == 0</c> the directional diffuse and <c>kind != 0</c> the
    /// ambient, last of each wins.
    /// <para>STICKY inside an area, exactly as the engine's two fields are -- <c>UpdateLights</c> never clears
    /// them, so a directional whose gob leaves the scene (which is how an interior ends up without a sun:
    /// <c>CNWCArea::SetGlobalLightDiffuse</c> @0x702510 calls <c>SetScene(NULL)</c> on the whole sun object)
    /// keeps lighting until the area edge. A light that DIES is dropped, which vanilla does not do and cannot:
    /// its pointer then dangles.</para></summary>
    internal static IntPtr AreaAmbient { get; private set; }
    /// <inheritdoc cref="AreaAmbient"/>
    internal static IntPtr AreaDiffuse { get; private set; }

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _adjRadius = EngineFn.GetLightAdjustedRadius;
        _maxLights = EngineFn.LightMgrGetMaxLights;
        _onlyGob = (IntPtr*)Native.Sym(SymbolMap.LightManagerOnlyGob);
        // No silent fallback (memory no-silent-fallbacks). BSPTraverse itself is SceneWalk's to resolve now --
        // the tile cross-check rides its one traversal (NoteBspNode) instead of starting a second -- and it
        // stands the whole walk down if it is missing, so an interior lit only by its gobs cannot happen
        // quietly on that account either.
        if (_adjRadius == null || _maxLights == null)
        {
            string miss = _adjRadius == null ? SymbolMap.GetLightAdjustedRadius
                        : SymbolMap.LightMgrGetMaxLights;
            _unavailable = true;
            log.Always($"LightRegistry: {miss} did not resolve -- Alloy's light registry is OFF for the session; "
                     + "the light walk falls back to the engine's LightManager lists and says so on /alloy");
            return;
        }
        log.Info("LightRegistry: armed -- Alloy enumerates its own lights from the scene walk (a gob's down its "
               + "part tree, a tile's from the tile's own part list) and cross-checks the tile BSP's light lists "
               + $"against them (cap {_maxLights()} from GetMaxLights; light.maxlit overrides, light.registry 0 "
               + "hands the enumeration back to LightManager)");
    }

    // ================= the seam =================

    /// <summary>A non-trimesh part the walk reached. Everything that is not a light falls straight through --
    /// the cast is the engine's own and returns null for every other part class.</summary>
    internal static void Visit(IntPtr part, bool bsp, int gen)
    {
        if (_unavailable) return;
        IntPtr light = new Part(part).AsPartLight().Ptr;
        if (light == IntPtr.Zero) return;
        Admit(light, bsp, gen);
    }

    /// <summary>Record or refresh one light. <c>PartLight::AsPartLight</c> returns <c>this</c>, so the light
    /// pointer and the walked part are the same address and one root walk serves both.</summary>
    private static void Admit(IntPtr light, bool bsp, int gen)
    {
        AlloyProbe.Once(ref _probed, "Part", light, _log);
        int s = Find(light);
        if (s < 0)
        {
            s = Insert(light);
            if (s < 0)
            {
                _overflow++;
                if (!_overflowLogged)
                {
                    _overflowLogged = true;
                    _log?.Always($"LightRegistry: the light table is FULL -- all {Slots} slots hold a live light, "
                               + "so this light is not registered and will not light, halo, tint or cast. This is "
                               + "a sizing bug, not a limit: raise LightRegistry.SlotBits. Counted as overflow "
                               + "on the [alloy] lightreg clause for the rest of the session");
                }
                return;
            }
            _arrived++;
            ListLive(s);
            // The allocator hands addresses back: this pointer may be on the death ledger as the debris of an
            // older light. Registering it again is the proof that it is not, and leaving the entry would make a
            // LATER failure to register read as debris rather than as the hole it would be.
            ForgetDead(light);
            _bsp[s] = bsp;
            _gob[s] = new Part(light).Gob.Ptr;
            _root[s] = RootOf(light);
        }
        _gen[s] = gen;
    }

    /// <summary>The walk of <paramref name="gen"/> finished: the sweep -- an entry not seen on this
    /// generation left the scene. A tile's lights arrived during the walk's own BSP traversal, from the tile's
    /// own part list through the <see cref="NonMeshParts"/> seam; this class no longer starts a second traversal
    /// of that tree, and what it does with the node light lists is <see cref="NoteBspNode"/>.</summary>
    internal static void WalkDone(int gen)
    {
        if (_unavailable) return;
        Sweep(gen);
    }

    /// <summary>The walk's BSP traversal is starting: the node-list gauges are this pass's.</summary>
    internal static void BspScanBegin() { if (!_unavailable) { _tile = 0; _nodeKept = 0; _residueN = 0; } }

    /// <summary>One BSP node's light list, on <paramref name="gen"/> -- as a CROSS-CHECK, never as a source.
    /// <para><b>Admission from this list is forbidden, and that is the whole point of the method.</b> Its
    /// entries are not pruned on part death: <c>Scene::ChildAdd</c> @0x461d50 routes a gob's whole tree through
    /// <c>ListSpecialPartsR</c> @0x462d50, which files every <c>AsPartLight</c> into it, and the only unlink is
    /// <c>ChildRemove</c> -&gt; <c>UnlistSpecialParts</c> over the tree active at that moment -- which an Alloy
    /// LOD rotation of <c>Gob+0x90</c> outlives, and which never happens at all for a part that merely dies (no
    /// part destructor reaches <c>SceneBSPRemovePart</c>; <c>Scene::RemoveFromSceneManager</c> @0x462b90 only
    /// touches the global buckets). <see cref="Find"/> misses a retired or trimmed entry, so an
    /// <see cref="Admit"/> here would <see cref="Insert"/> a FREED <c>PartLight*</c> and the per-frame
    /// selection would dereference it every frame after.</para>
    /// <para>Both real populations arrive elsewhere, from containers that ARE maintained: a gob's lights down
    /// its part tree (<see cref="Visit"/>, including the lights-only descent into a pruned subtree), and a
    /// tile's from the tile's own part list, where <c>ProcessStaticParts</c> @0x470970 files every part whose
    /// <c>AsPartLight</c> is non-null and which dies with its tile. So this list can only confirm, and what it
    /// names that no feed has is counted: see <see cref="BspScanEnd"/>.</para></summary>
    internal static void NoteBspNode(IntPtr node, int gen)
    {
        if (_unavailable) return;
        var bsp = new BSPNode(node);
        IntPtr* items = bsp.Lights;
        if (items == null) return;
        int n = bsp.LightCount;
        for (int i = 0; i < n; i++)
        {
            IntPtr p = items[i];
            if (p == IntPtr.Zero) continue;
            int s = Find(p);
            // LOAD-BEARING, not bookkeeping: a slot this walk's feeds did not reach -- a gob light hanging off
            // an inactive LOD root, which the walk stops enumerating once GobLod is armed -- survives the
            // generation sweep only because of this restamp. Counted apart from the tile confirmations for that
            // reason: the two mean opposite things, one that the feeds agree with the engine and one that only
            // the engine still lists the light, and lumping them made `tile` read as a tile population it was
            // not and `walk edges` read 0 for a light the restamp had quietly kept.
            if (s >= 0)
            {
                // EXACT, not "gob vs tile": every gob light sits in some node list, so splitting on _bsp made
                // this read the whole gob-light population. What the restamp KEEPS is a slot that does not
                // already carry this generation -- the gob loop runs before this traversal, so a light either
                // feed reached is stamped by now, and one that is not is exactly the inactive-LOD case.
                if (_bsp[s]) _tile++;
                else if (_gen[s] != gen) _nodeKept++;
                _gen[s] = gen;
                continue;
            }
            // A miss may only be ORDER: the node holding the light can be visited before the node holding its
            // tile's first mesh part, which is where the tile's own list is read. Resolved after the traversal.
            if (_residueN < ResidueCap) _residue[_residueN++] = p; else _residueOverflow++;
        }
    }

    /// <summary>The traversal is over: anything the node lists named that STILL has no record is either a
    /// dangling pointer they were never pruned of -- the expected case, and the reason nothing is admitted from
    /// them -- or a live light both feeds missed, which would be a hole. The two are indistinguishable from
    /// here without dereferencing the pointer, which is exactly what must not happen, so the number is reported
    /// and the first occurrence is said out loud (CX-45).</summary>
    internal static void BspScanEnd()
    {
        if (_unavailable) return;
        for (int k = 0; k < _residueN; k++)
        {
            IntPtr p = _residue[k];
            if (Find(p) >= 0) continue;                   // a later node's tile had not been read yet: not residue
            if (KnownDead(p)) { _residueDebris++; continue; }
            _residueSeen++;
        }
        _residueN = 0;
        // DEBRIS is expected and is a number; a HOLE is the event. Those lists keep every light that ever died
        // in them, so a city with creature torches accumulates dangling pointers and an Always on the total
        // would fire every session and mean nothing. What cannot be explained that way is a pointer Alloy was
        // never told the death of: something is in the engine's own light list that neither feed reached, which
        // is the shape an under-lit area takes.
        if (!_residueLogged && _residueSeen > 0 && _deadFull == 0)
        {
            _residueLogged = true;
            _log?.Always($"LightRegistry: {_residueSeen} light(s) in the tile BSP's node lists have no record and "
                       + "no death notice -- neither the gob walk nor the tiles' own part lists reached them, and "
                       + "nothing is admitted from those lists because they are never pruned on part death and so "
                       + "name freed memory. This is not debris: the area may be genuinely under-lit. Counted as "
                       + "node-residue on the [alloy] lightreg clause, beside the debris it is separated from");
        }
    }

    /// <summary>Node-list lights with no record, held only until <see cref="BspScanEnd"/> re-asks. Small on
    /// purpose: what does not fit is counted, and an overflow of pointers with no record at all is itself the
    /// finding.</summary>
    private const int ResidueCap = 256;
    private static readonly IntPtr[] _residue = new IntPtr[ResidueCap];
    private static int _residueN;
    /// <summary><see cref="_residueSeen"/> = node-list pointers with no record and NO death notice, which is the
    /// number that can mean a hole; <see cref="_residueDebris"/> = the ones Alloy was told died, which those
    /// lists were simply never pruned of.</summary>
    private static long _residueSeen, _residueDebris, _residueOverflow;
    private static bool _residueLogged;

    /// <summary>Every light this area was told the death of, so a node list naming one is KNOWN debris rather
    /// than a suspected hole. Kept apart from the table's own tombstone because <see cref="Trim"/> retires a
    /// tombstone as soon as it can -- "nearly every retirement" by its own doc -- which would otherwise leave
    /// almost all debris indistinguishable from a light nobody reached.
    /// <para>Linear-probed, cleared with the table (<see cref="Revoke"/>), and sized well past an area's light
    /// population. A full set cannot classify, so it SUPPRESSES the verdict rather than guessing: while
    /// <see cref="_deadFull"/> is nonzero the residue is still printed and the Always is not fired.</para></summary>
    private const int DeadBits = 13;
    private static readonly IntPtr[] _dead = new IntPtr[1 << DeadBits];
    private static int _deadUsed;
    private static long _deadFull;

    private static void NoteDead(IntPtr p)
    {
        int mask = (1 << DeadBits) - 1;
        int free = -1;
        // BOUNDED, and not by _deadUsed: that counts live entries, while a tombstone still occupies its slot,
        // so a ledger churned enough times can hold no IntPtr.Zero at all and an unbounded probe would spin.
        for (int k = 0, h = Hash(p) & mask; k <= mask; k++, h = (h + 1) & mask)
        {
            if (_dead[h] == p) return;
            if (free < 0 && _dead[h] == Tombstone) free = h;
            if (_dead[h] == IntPtr.Zero) { _dead[free >= 0 ? free : h] = p; _deadUsed++; return; }
        }
        if (free >= 0) { _dead[free] = p; _deadUsed++; return; }
        _deadFull++;   // every slot live: the split stops being decidable and the verdict is suppressed
    }

    /// <summary>Take <paramref name="p"/> off the ledger. Tombstoned rather than emptied, for the same reason
    /// the light table's slots are: emptying it would truncate the probe chain of every other pointer that
    /// hashes before it, and <see cref="KnownDead"/> would then miss real debris. The tombstone is
    /// <see cref="Tombstone"/>, which no real pointer can be, and <see cref="NoteDead"/> reclaims it.</summary>
    private static void ForgetDead(IntPtr p)
    {
        int mask = (1 << DeadBits) - 1;
        for (int k = 0, h = Hash(p) & mask; k <= mask; k++, h = (h + 1) & mask)
        {
            if (_dead[h] == p) { _dead[h] = Tombstone; _deadUsed--; return; }
            if (_dead[h] == IntPtr.Zero) return;
        }
    }

    /// <summary>not-an-offset: the ledger's tombstone, an address no part can live at.</summary>
    private static readonly IntPtr Tombstone = (IntPtr)(-1);

    private static bool KnownDead(IntPtr p)
    {
        int mask = (1 << DeadBits) - 1;
        for (int k = 0, h = Hash(p) & mask; k <= mask; k++, h = (h + 1) & mask)
        {
            if (_dead[h] == p) return true;
            if (_dead[h] == IntPtr.Zero) return false;   // a Tombstone is walked over, never a chain end
        }
        return false;
    }

    /// <summary>A per-gob scene delta finished (<see cref="SceneWalk"/> answers a membership change with one
    /// <summary>Mark and sweep, and the rebuild of the live list the per-frame pass walks. Separate from
    /// <see cref="WalkDone"/> so the self-check can drive it with no Scene in sight.</summary>
    private static void Sweep(int gen)
    {
        // Emptied FIRST, so the UnlistLive inside Drop below has nothing to swap and cannot reorder the list
        // this loop is rebuilding. Wholesale rather than over the old members: this pass already walks every
        // slot, so the fill is the same order of work and cannot leave one behind.
        Array.Fill(_liveAt, -1);
        _liveN = 0;
        for (int s = 0; s < Slots; s++)
        {
            if (_id[s] == IntPtr.Zero || _freed[s]) continue;
            if (_gen[s] != gen) { Drop(s, false); _left++; continue; }
            _liveAt[s] = _liveN; _live[_liveN++] = s;
        }
    }

    /// <summary>Empty a retired slot IF that cannot truncate anyone's probe chain -- which is exactly when the
    /// slot after it was never claimed, because then no key of any hash was ever stored past this point.
    /// Emptying it can make its predecessor a chain end in turn, so the trim walks back.
    /// <para>Why it exists: a tombstone is mandatory (see <see cref="Drop"/>) and is reclaimed only by a later
    /// key that happens to probe over it, so churn inside one area -- a creature's torch arriving and leaving
    /// -- would otherwise leave one behind for good and lengthen a chain for good. At the table's real load
    /// (~1500 lights in 16384 slots) runs are barely longer than one slot, so nearly every retirement trims
    /// here and the tombstone population stays near zero; <see cref="Stats"/> prints it.</para></summary>
    private static void Trim(int s)
    {
        int mask = Slots - 1;
        while (_freed[s] && _id[s] != IntPtr.Zero && _id[(s + 1) & mask] == IntPtr.Zero)
        {
            _id[s] = IntPtr.Zero; _tenant[s] = 0; _freed[s] = false;
            // ...and everything derived from the pointer, so a slot this emptied cannot be read as a live
            // light by anything that reached it before the next sweep rebuilt the live list.
            _gob[s] = IntPtr.Zero; _root[s] = IntPtr.Zero; _gen[s] = 0;
            _want[s] = false; _cand[s] = false;
            _used--; _tombs--;
            s = (s - 1) & mask;
        }
    }

    /// <summary><c>Part::~Part</c> for this pointer, before the engine frees it. The pointer is COMPARED, and
    /// the slot is marked freed rather than emptied: <see cref="Find"/> then misses it and <see cref="Insert"/>
    /// reclaims it, so a recycled address becomes a new light instead of landing on a tombstone (#221).</summary>
    internal static void Died(IntPtr part)
    {
        if (_unavailable) return;
        int s = Find(part);
        if (s < 0) return;
        // AFTER the early-out, so the ledger holds registered LIGHTS and not every part that ever died: this
        // notice arrives for all ~14.5k part deaths a session through NonMeshParts.Died, which would fill the
        // ledger and permanently suppress the verdict it exists to make possible.
        NoteDead(part);   // survives Trim, so a node list naming this pointer later reads as debris, not a hole
        Drop(s, true);
        _died++;
    }

    /// <summary>Retire one entry -- left the scene or died, which this table treats alike. A dying light also
    /// stops being an area term; vanilla keeps the dangling pointer there, which is a read Alloy will not make.
    /// <para><b>A TOMBSTONE, never an empty slot</b> (#221's rule, and PartTable's shape). Zeroing
    /// <c>_id[s]</c> would truncate the probe chain of every other key that passed through this slot on the way
    /// to its own, so <see cref="Find"/> would miss a light that is still in the table and
    /// <see cref="Insert"/> would record it a SECOND time -- one light lighting twice, and one of the two
    /// surviving its own death notice. <see cref="Insert"/> reclaims a tombstone, so the table does not creep
    /// toward its cap either.</para></summary>
    private static void Drop(int s, bool died)
    {
        if (AreaAmbient == _id[s]) AreaAmbient = IntPtr.Zero;
        if (AreaDiffuse == _id[s]) AreaDiffuse = IntPtr.Zero;
        _want[s] = false; _cand[s] = false;
        if (!_freed[s]) { _freed[s] = true; _tombs++; }
        UnlistLive(s);
        if (!died) { _gob[s] = IntPtr.Zero; _root[s] = IntPtr.Zero; _gen[s] = 0; }
        Trim(s);
    }

    /// <summary>Area edge, part-table wipe or module unload: every pointer in here named a world that is gone,
    /// and the two area terms with them (a new area re-picks them on its first walk).</summary>
    internal static void Revoke()
    {
        Array.Clear(_id); Array.Clear(_freed); Array.Clear(_gen); Array.Clear(_bsp);
        Array.Clear(_gob); Array.Clear(_root); Array.Clear(_want); Array.Clear(_cand);
        // ...and the tenancies with them: a cleared slot is untenanted, so its 0 must not match a selection
        // entry that named the tenant it held before the wipe. _tenantSeq keeps counting, which is what makes
        // every claim after this compare unequal to every selection taken before it.
        Array.Clear(_tenant);
        _liveN = 0; Array.Fill(_liveAt, -1);
        _selN = 0; _candN = 0; _wantN = 0; _used = 0; _tombs = 0;
        AreaAmbient = IntPtr.Zero; AreaDiffuse = IntPtr.Zero;
        // ...and the death ledger, which names pointers from the area that is gone.
        Array.Clear(_dead); _deadUsed = 0; _deadFull = 0;
        _residueN = 0;
    }

    // ================= selection =================

    /// <summary>Once per world frame, from <see cref="LightFade.Tick"/> -- the first Alloy code in the frame
    /// that touches a light, so the fade, the area terms, the light walk and the halos all read one verdict.
    /// <para>One pass over the live set for membership (visible, LOD-active, and the two area terms) and the
    /// candidate filter, then the cap. Every term is a field read.</para></summary>
    internal static void Select(IntPtr scene)
    {
        // Gated on Live, not on _unavailable: with the knob off nothing reads this verdict, and the TABLE is
        // maintained either way (the seam is unconditional), so flipping the knob back on costs one frame.
        if (!Live || scene == IntPtr.Zero) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        SelectInner(scene);
        _selTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        _selFrames++;
    }

    /// <summary>Ticks spent selecting, and frames spent doing it -- the instrument behind
    /// <see cref="Cap"/>'s ponytail note. Reported as ms/frame and zeroed with the period.</summary>
    private static long _selTicks, _selFrames;

    private static void SelectInner(IntPtr scene)
    {
        var sc = new Scene(scene);
        Vector3 refPos = sc.SkyPos;
        // ManageSceneBSP's own argument: max(45, the scene's fog end), read whether or not fog is enabled --
        // exactly as the instruction reads it.
        float radius = MathF.Max(SetupRadiusFloor, sc.FogEnd);
        IntPtr onlyGob = _onlyGob != null ? *_onlyGob : IntPtr.Zero;

        _selN = 0; _candN = 0; _hidden = 0; _inactiveLod = 0;
        for (int k = 0; k < _liveN; k++)
        {
            int s = _live[k];
            // Retired since the sweep built this list -- Part::~Part fires whenever the engine frees a part,
            // and no walk need follow it. BOTH shapes of a retirement, exactly as Sweep spells them: a
            // tombstone (freed, pointer kept so the probe chain stands) and a slot Trim gave back (pointer
            // cleared, NOT freed). Testing the freed mark alone dereferenced the null: Range reads +0x70.
            if (_id[s] == IntPtr.Zero || _freed[s]) continue;
            _want[s] = false; _cand[s] = false;
            if (!_bsp[s] && Hidden(s)) { _hidden++; continue; }
            if (InactiveLod(s)) { _inactiveLod++; continue; }
            var light = new PartLight(_id[s]);
            // UpdateLights' own rule for the two index -1 lights, and the only place a directional is looked
            // at: everything below is the POINT-light filter, which excludes them by the same threshold.
            if (light.Range >= EngineLayout.DirectionalRange)
            {
                if (light.Kind == 0) AreaDiffuse = _id[s]; else AreaAmbient = _id[s];
                continue;
            }
            _selTenant[_selN] = _tenant[s];
            _sel[_selN++] = s;
            if (!Candidate(light, refPos, radius)) continue;
            // ControlLightsFade @0x4c2050's m_pOnlyGob prologue caps the lit run at the LEADING entries
            // belonging to that gob -- and those are exactly the ones Score sorts first, so restricting
            // candidacy to them is the same verdict without a lead-run count.
            if (onlyGob != IntPtr.Zero && _gob[s] != onlyGob) continue;
            _cand[s] = true; _candN++;
            _score[s] = Score(light, refPos, onlyGob);
        }
        Cap();
    }

    /// <summary><c>SetupLightsForThisPositionAndRadius</c> @0x4c1df0's filter, term for term. Its directional
    /// test (<c>range &lt; 1000</c>) is the caller's, above.</summary>
    internal static bool Candidate(PartLight light, Vector3 refPos, float radius)
    {
        if (light.FlagA == 0 && light.FlagB == 0) return false;
        if (!(light.Range > 0f)) return false;
        Vector3 c = light.Colour;
        if (c.X == 0f && c.Y == 0f && c.Z == 0f) return false;
        if (!(light.Intensity > 0f)) return false;
        if (_adjRadius == null) return true;   // headless: the filter's engine leaf, asserted by its callers
        float reach = _adjRadius(light.Ptr) + radius;
        return (light.Pos - refPos).LengthSquared() < reach * reach;
    }

    /// <summary><c>PrioritizeLights</c> @0x4c0940's score -- LOWER sorts first. The abs() the engine takes in
    /// double (<c>cvtps2pd</c>/<c>andps</c>/<c>cvtpd2ps</c>) is taken in float here; the difference is below
    /// anything that could reorder two lights.</summary>
    internal static float Score(PartLight light, Vector3 refPos, IntPtr onlyGob)
    {
        Vector3 c = light.Colour;
        float maxc = MathF.Max(MathF.Abs(c.X), MathF.Max(MathF.Abs(c.Y), MathF.Abs(c.Z)));
        float dist = (light.Pos - refPos).Length();
        if (!(maxc > 0f) || !(light.Range > 0f) || !(light.Intensity > 0f)) return ScoreNone;
        IntPtr gob = new Part(light.Ptr).Gob.Ptr;
        if (gob != IntPtr.Zero && gob == onlyGob) return ScoreOnlyGob;
        var node = new MdlNodeLight(new Part(light.Ptr).Node.Ptr);
        int pri = light.Priority;
        if (pri == 0 && !node.IsNull) pri = node.LightPriority;
        float flat = !node.IsNull && node.FadingLight != 0
                   ? maxc * light.Range * light.Intensity + 1f
                   : ScoreFlatTerm;
        return dist * (pri + 1) / ((light.FlagB + 4) * flat);
    }

    /// <summary>How many lights the frame may have LIT at once, and which. <c>GetMaxLights()</c> is
    /// <c>min(m_nMaxLights, 128)</c> and <c>m_nMaxLights</c> is a client graphics option, so the stock cap is
    /// the PLAYER's; a non-zero <c>light.maxlit</c> overrides it, and 0 there follows that option.</summary>
    private static void Cap()
    {
        int cap = Lighting.MaxLit.V;
        bool capped = cap > 0;
        if (!capped && _maxLights != null) { cap = _maxLights(); capped = cap > 0; }
        // A lit head light occupies GL slots 0 and 1 (SetHeadLightProperties @0x4c12e0), so it costs the area
        // two of its slots -- which is what ControlLightsFade's own cap subtracted.
        if (capped && LightFade.HeadLightOn) cap = Math.Max(0, cap - LightFade.HeadLightCost);
        // Self-contained: the selection below reads _want as "already taken", so it has to start from a clear
        // slate rather than trust the caller to have cleared it.
        for (int k = 0; k < _selN; k++) _want[_sel[k]] = false;
        _wantN = 0;
        if (capped && cap == 0) return;   // the head light took every slot there was
        if (!capped || _candN <= cap)
        {
            for (int k = 0; k < _selN; k++) if (_cand[_sel[k]]) { _want[_sel[k]] = true; _wantN++; }
            return;
        }
        // Partial selection, `cap` passes. The cap only binds in a dense area, and a full sort would be a
        // sort nobody reads past its head.
        // ponytail: O(cap * candidates) -- ~8k float compares a frame at the stock cap of 32 over a 250-light
        // area, and `lit=` against `cand=` on the period line says how often it runs at all. A persistent
        // order array plus an insertion sort is the upgrade if that pair ever says it is worth it.
        for (int n = 0; n < cap; n++)
        {
            int best = -1;
            float bs = 0f;
            for (int k = 0; k < _selN; k++)
            {
                int s = _sel[k];
                if (!_cand[s] || _want[s]) continue;
                if (best < 0 || _score[s] < bs) { best = s; bs = _score[s]; }
            }
            if (best < 0) break;
            _want[best] = true; _wantN++;
        }
    }

    /// <summary>Is this light, or any part above it, hidden? <c>SceneWalk.Tree</c> prunes a hidden subtree of
    /// an APPENDED tree before descending, so such a light is never visited -- but hiding a subtree bumps no
    /// epoch (<c>Gob::SetSubtreeEnabled</c> @0x43c670 announces nothing), so between walks the byte is
    /// the only notice there is. Read live, from the light upward: <c>Part::~Part</c> deletes its children, so
    /// every ancestor of a live light is itself live.
    /// <para>Tile parts are exempt, as they are in the walk -- <c>SceneBSPAddPart</c> never consults the
    /// byte -- and so is a gob's OWN tree (<c>Gob+0x90</c>): <c>Scene::ListSpecialPartsR</c> @0x462d50
    /// registers every light on it without reading the byte, and a placeable's lamp often hangs under a hidden
    /// node. What this still drops is a light in an appended tree that was hidden, e.g. the old torch an
    /// equipment swap leaves appended.</para></summary>
    private static bool Hidden(int s)
    {
        IntPtr gob = _gob[s];
        if (gob != IntPtr.Zero && _root[s] == new Gob(gob).PartTree.Ptr) return false;
        IntPtr p = _id[s];
        for (int d = 0; d < DepthMax && p != IntPtr.Zero; d++)
        {
            var part = new Part(p);
            if (part.Visible == 0) return true;
            p = part.Parent.Ptr;
        }
        return false;
    }

    /// <summary>Is this light in a LOD tree its gob is not showing? The engine draws <c>Gob+0x90</c> alone and
    /// <c>Gob::SetLODModel</c> @0x43e6a0 rotates the three roots with no notice of any kind, so the test is a
    /// live read of the two ALTERNATE roots. Asking it that way round is also what keeps an APPENDED tree (a
    /// carried torch) from being mistaken for an alternate, which comparing against <c>PartTree</c> would
    /// do.</summary>
    private static bool InactiveLod(int s)
    {
        IntPtr gob = _gob[s], root = _root[s];
        if (gob == IntPtr.Zero || root == IntPtr.Zero) return false;
        var g = new Gob(gob);
        return root == g.LodRoot1.Ptr || root == g.LodRoot2.Ptr;
    }

    /// <summary>The part-tree root this part hangs off, walked once at arrival.</summary>
    private static IntPtr RootOf(IntPtr part)
    {
        IntPtr p = part;
        for (int d = 0; d < DepthMax; d++)
        {
            IntPtr up = new Part(p).Parent.Ptr;
            if (up == IntPtr.Zero) return p;
            p = up;
        }
        return p;
    }

    // ================= the table =================

    /// <summary>Open addressing, linear probe over the WHOLE table: the chain ends at the first slot that was
    /// never claimed, which is what a tombstone (a slot kept non-empty on retirement) preserves. A bounded
    /// probe window here is what silently dropped lights -- it cannot be reintroduced without reintroducing
    /// that bug.</summary>
    private static int Find(IntPtr p)
    {
        int h = Hash(p);
        for (int k = 0; k < Slots; k++)
        {
            int s = (h + k) & (Slots - 1);
            if (_id[s] == IntPtr.Zero) return -1;
            if (_id[s] == p && !_freed[s]) return s;
        }
        return -1;
    }

    /// <summary>...and the same walk for a claim: the first reclaimable slot in the chain if there is one,
    /// otherwise the slot that ends it. -1 only when all <see cref="Slots"/> slots hold a LIVE light.</summary>
    private static int Insert(IntPtr p)
    {
        int h = Hash(p);
        int free = -1;
        for (int k = 0; k < Slots; k++)
        {
            int s = (h + k) & (Slots - 1);
            if (_id[s] == p && !_freed[s]) return s;
            if (free < 0 && _freed[s]) free = s;
            // The chain ends here: past it no key of this hash was ever recorded, so the search is done and
            // this slot is the claim if no tombstone before it was reclaimable.
            if (_id[s] == IntPtr.Zero) { if (free < 0) free = s; break; }
        }
        if (free < 0) return -1;
        if (_freed[free] && _id[free] != IntPtr.Zero) { _reclaimed++; _tombs--; }
        else if (_id[free] == IntPtr.Zero) _used++;
        // A CLAIM, so the tenancy moves -- and only here: the early return above is the same tenant asking
        // again, and bumping there would make every selection stale the moment a walk re-visited a light.
        _tenant[free] = ++_tenantSeq;
        _id[free] = p; _freed[free] = false; _gen[free] = 0; _bsp[free] = false;
        _gob[free] = IntPtr.Zero; _root[free] = IntPtr.Zero;
        _want[free] = false; _cand[free] = false; _score[free] = ScoreNone;
        return free;
    }

    /// <summary>Fibonacci hash of a heap pointer with the four constant low bits dropped -- the same key
    /// <see cref="LightFade"/>'s wanted set uses, and for the same reason: malloc hands back aligned blocks,
    /// so hashing those bits clusters every key into a sixteenth of the table.</summary>
    private static int Hash(IntPtr p)
    {
        ulong x = ((ulong)(long)p >> 4) * 0x9E3779B97F4A7C15UL;   // not-an-offset: the 64-bit golden-ratio odd constant
        // The shift takes the TOP SlotBits bits -- the only ones a multiplicative hash has mixed. Spelling it
        // as a fixed shift plus a mask (it was >> 53, which is exactly right at 2048 slots and nowhere else)
        // confines every key to the first 2048 slots of a larger table.
        return (int)(x >> (64 - SlotBits));
    }

    // ================= reporting =================

    /// <summary>The registry's clause on the <c>[alloy]</c> period line -- the only witness this table has.
    /// The populations (<c>lights</c>, <c>in reach</c>, <c>lit</c>) are the last <see cref="Select"/>'s, the
    /// walk-edge counts are the period's, and <c>overflow</c> is the one that must read 0.
    /// <para>Also where the rates are zeroed: <c>PassLifecycle</c> has no reset slot and this runs exactly once
    /// per period.</para></summary>
    internal static string Stats(int frames)
    {
        if (_unavailable) return "lightreg UNAVAILABLE -- the engine's LightManager lists are the source";
        if (!Use) return "lightreg off (light.registry 0) -- the engine's LightManager lists are the source";
        double ms = _selFrames == 0 ? 0
                  : _selTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _selFrames;
        string s = $"lightreg {_selN} lights ({_tile} tile-confirmed, {_nodeKept} kept by the node list -- the "
                 + $"engine still lists them), {_candN} in reach, {_wantN} lit, select {ms:F3}ms, "
                 + $"+{_arrived}/-{_left} walk edges, {_died} died, {_reclaimed} slots reclaimed, "
                 + $"{_hidden} hidden, {_inactiveLod} inactive-LOD, "
                 + $"table {_used}/{Slots} slots ({_tombs} retired), overflow={_overflow} (must be 0), "
                 + $"node-residue={_residueSeen} unexplained (+{_residueDebris} known dead, "
                 + $"{_residueOverflow} past the pass cap{(_deadFull > 0 ? ", DEATH LEDGER FULL so the split is unreliable" : "")}), "
                 + $"area ambient={(AreaAmbient != IntPtr.Zero ? "yes" : "NONE")} "
                 + $"diffuse={(AreaDiffuse != IntPtr.Zero ? "yes" : "NONE")}, "
                 // The peak table is keyed on THIS population, so its own overflow belongs on this line.
                 + SceneInputs.FlickerClause();
        _arrived = 0; _left = 0; _died = 0; _selTicks = 0; _selFrames = 0;
        return s;
    }

    // ================= self-check =================

    /// <summary><paramref name="n"/> distinct pointers that all hash to the SAME slot -- the adversarial input
    /// the table has to survive, constructed rather than searched for. <see cref="Hash"/> is the top
    /// <see cref="SlotBits"/> bits of <c>(p &gt;&gt; 4) * K</c>, so fixing those bits means fixing the product's
    /// high end: walk the products upward inside one slot's interval and invert each through K (odd, hence
    /// invertible mod 2^64) to get the pointer. Only products whose pre-image fits the 60 bits a
    /// <c>&lt;&lt; 4</c> pointer can carry are usable, which is one in sixteen.</summary>
    private static IntPtr[] ClusterKeys(int n)
    {
        const ulong K = 0x9E3779B97F4A7C15UL;   // not-an-offset: LightRegistry.Hash's own constant
        ulong inv = K;
        for (int i = 0; i < 5; i++) inv *= 2UL - K * inv;   // Newton: doubles the correct bits each step
        var keys = new IntPtr[n];
        int got = 0;
        for (ulong product = 1UL << (64 - SlotBits); got < n; product++)
        {
            ulong x = product * inv;
            if (x == 0 || x >= 1UL << 60) continue;
            keys[got++] = (IntPtr)(long)(x << 4);
        }
        return keys;
    }

    /// <summary>The registry's contract, headless, over synthetic lights in unmanaged scratch: arrival,
    /// departure, death, slot reclaim, the candidate filter, the score and the cap. None of it has another
    /// witness -- a light that never arrives is a dark room, one that never departs is a light that will not
    /// go out, and a wrong cap is the whole area at the wrong brightness.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        bool wasUnavailable = _unavailable;
        _unavailable = false;

        // The table, on raw pointers: Find/Insert/Drop/Sweep touch no engine memory.
        Revoke();
        var a = (IntPtr)0x11000; var b = (IntPtr)0x12000;
        int sa = Insert(a), sb = Insert(b);
        check.Expect(sa >= 0 && sb >= 0 && sa != sb, "two lights take two slots");
        check.Expect(Find(a) == sa && Find(b) == sb, "...and each is found by its own pointer");
        _gen[sa] = 7; _gen[sb] = 6;
        Sweep(7);
        check.Expect(_liveN == 1 && _live[0] == sa, "a light the walk missed leaves the set");
        check.Expect(Find(b) < 0, "...and its slot stops answering");
        // Death marks the slot freed, so the SAME address is re-recorded rather than landing on a tombstone.
        Died(a);
        // ...which stops it matching whether it was left a tombstone or trimmed back to empty (see Trim); the
        // clustered case below is where the trim itself is pinned.
        check.Expect(Find(a) < 0, "a dead light's slot stops matching");
        check.Expect(Insert(a) == sa && !_freed[sa], "...and the recycled address reclaims that very slot");
        // An area term that dies is dropped; vanilla keeps the dangling pointer.
        AreaDiffuse = a;
        Died(a);
        check.Expect(AreaDiffuse == IntPtr.Zero, "a dead area light stops being the area term");
        Revoke();
        check.Expect(Find(a) < 0 && _liveN == 0 && AreaAmbient == IntPtr.Zero, "the area edge empties the table");

        // WHY a retired slot is a tombstone and not an empty one: three keys in one probe window, drop the
        // middle. An emptied slot truncates the chain, so Find misses the third light and Insert records it a
        // SECOND time -- one light lighting twice, and one of the two outliving its own death notice.
        var keys = new IntPtr[3];
        int kn = 0, h0 = -1;
        for (long v = 0x20000; kn < 3 && v < 0x8000000; v += 0x10)
        {
            int h = Hash((IntPtr)v);
            if (h0 < 0) { h0 = h; keys[kn++] = (IntPtr)v; }
            else if (h == h0) keys[kn++] = (IntPtr)v;
        }
        check.Expect(kn == 3, "three colliding keys found for the probe-chain check");
        if (kn == 3)
        {
            int s0 = Insert(keys[0]), s1 = Insert(keys[1]), s2 = Insert(keys[2]);
            check.Expect(s0 >= 0 && s1 >= 0 && s2 >= 0 && s0 != s1 && s1 != s2, "three colliding keys, three slots");
            _gen[s0] = 3; _gen[s1] = 3; _gen[s2] = 3;
            Sweep(3);
            check.Expect(_liveN == 3, "...all three live after their own walk");
            _gen[s1] = 2;
            Sweep(3);
            check.Expect(Find(keys[2]) == s2, "a retired slot does not truncate the probe chain");
            check.Expect(Insert(keys[2]) == s2, "...so its light is not recorded a second time");
            check.Expect(Insert(keys[1]) == s1, "...and the tombstone is reclaimed by its own address");
        }
        Revoke();

        // ...and WHY a selection carries the tenancy and not just the slot index. Halos reads the previous
        // frame's selection, so between the two a light can die AND a walk can hand its slot to another light
        // -- which clears the freed mark. The index alone then resolves to an unrelated LIVE PartLight, and a
        // flare gets staged at another lamp's position and colour. Both shapes of the reuse are pinned:
        if (kn == 3)
        {
            // (a) a different light reclaims the slot.
            int t0 = Insert(keys[0]), t1 = Insert(keys[1]);
            check.Expect(t0 >= 0 && t1 >= 0 && t0 != t1, "two colliding keys for the tenancy check");
            _selN = 1; _sel[0] = t0; _selTenant[0] = _tenant[t0]; _want[t0] = true;
            check.Expect(Light(0).Ptr == keys[0] && Wanted(0), "the selection names the light it was taken over");
            Died(keys[0]);
            check.Expect(Light(0).IsNull, "a light that died since the selection reads as a gap");
            Died(keys[1]);
            int re = Insert(keys[1]);
            check.Expect(re == t0, "a different light reclaims the freed slot (the setup for the defect)");
            check.Expect(Light(0).IsNull && !Wanted(0),
                         "...and the stale selection entry stays a gap rather than naming the NEW light");
            // (b) the allocator hands the very same address back.
            Revoke();
            int u = Insert(keys[0]);
            _selN = 1; _sel[0] = u; _selTenant[0] = _tenant[u]; _want[u] = true;
            Died(keys[0]);
            check.Expect(Insert(keys[0]) == u, "the recycled address reclaims its own slot");
            check.Expect(Light(0).IsNull && !Wanted(0), "...and a new light at an old address is a gap too");
        }
        _selN = 0; Array.Clear(_want);
        Revoke();

        // W2-LR: the BSP node light lists are a CROSS-CHECK and never a source. They hold GOB lights
        // (Scene::ChildAdd -> ListSpecialPartsR files every AsPartLight) and are pruned by nothing on part
        // death, so admitting from one re-registers a FREED PartLight* that the per-frame selection then
        // dereferences every frame. The node is hand-built in unmanaged scratch -- a light list of one,
        // naming a pointer that has just died.
        {
            IntPtr scratch = (IntPtr)System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x400);
            try
            {
                IntPtr dead = (IntPtr)0x41000;
                int sd = Insert(dead);
                _gen[sd] = 11;
                Died(dead);
                check.Expect(Find(dead) < 0, "the light died, so the table stops answering for it");
                IntPtr node = scratch, record = scratch + 0x100, items = scratch + 0x200;
                *(IntPtr*)(node + EngineLayout.BspNodeData) = record;
                *(IntPtr*)(record + EngineLayout.BspDataLight) = items;
                *(int*)(record + EngineLayout.BspDataLightCount) = 1;
                *(IntPtr*)items = dead;
                long seenBefore = _residueSeen, debrisBefore = _residueDebris;
                int usedBefore = _used;
                BspScanBegin();
                NoteBspNode(node, 12);
                BspScanEnd();
                check.Expect(Find(dead) < 0 && _used == usedBefore,
                             "a node list naming a freed light does NOT re-admit it");
                // ...and it is DEBRIS, not a hole: the death ledger outlives the tombstone Trim retires, which
                // is what keeps the loud line for a pointer nobody was ever told the death of.
                check.Expect(_residueDebris == debrisBefore + 1 && _residueSeen == seenBefore,
                             "...a light Alloy was told died counts as known debris");
                // A pointer with no record and no death notice is the other bucket -- the one worth a line.
                IntPtr stranger = (IntPtr)0x43000;
                *(IntPtr*)items = stranger;
                BspScanBegin();
                NoteBspNode(node, 12);
                BspScanEnd();
                check.Expect(_residueSeen == seenBefore + 1 && _residueDebris == debrisBefore + 1,
                             "...and one with no death notice counts as unexplained");
                // ...and the ledger forgets an address the allocator handed back: registering it again is the
                // proof it is not debris, so a LATER failure to register reads as the hole it would be.
                check.Expect(KnownDead(dead), "the dead light is on the ledger");
                int again = Insert(dead);
                check.Expect(again >= 0, "the recycled address registers again");
                ForgetDead(dead);   // Admit does this beside _arrived++; Insert alone is the raw table call
                check.Expect(!KnownDead(dead), "...and comes off the ledger with the claim");
                // The tombstone must not end a probe chain, or real debris behind it would read as a hole.
                check.Expect(KnownDead(stranger) == false, "a pointer never noted dead is not on the ledger");
                NoteDead(stranger);
                check.Expect(KnownDead(stranger) && KnownDead(dead) == false,
                             "...and a reclaimed ledger slot answers for its new tenant only");
                Died(dead);
                *(IntPtr*)items = dead;
                // ...and a light the feeds DID admit is confirmed rather than counted.
                IntPtr live = (IntPtr)0x42000;
                int sl = Insert(live);
                *(IntPtr*)items = live;
                seenBefore = _residueSeen;
                BspScanBegin();
                NoteBspNode(node, 13);
                BspScanEnd();
                check.Expect(_gen[sl] == 13 && _residueSeen == seenBefore,
                             "a node list naming a light the feeds have restamps it and counts nothing");
            }
            finally { System.Runtime.InteropServices.NativeMemory.Free((void*)scratch); }
        }
        _residueSeen = 0; _residueDebris = 0; _residueOverflow = 0; _residueLogged = false; _tile = 0;
        Revoke();

        // The hidden rule, on hand-built parts: root -> hidden dummy -> light. On the gob's OWN tree the light
        // stays in (vanilla's ListSpecialPartsR never reads part+0x34 -- the placeable lamp under a hidden node);
        // in an appended tree it is dropped (the old torch an equipment swap leaves appended and hidden).
        IntPtr hb = Marshal.AllocHGlobal(0x400);
        try
        {
            for (int i = 0; i < 0x400; i++) ((byte*)hb)[i] = 0;
            IntPtr gobP = hb, rootP = hb + 0x100, dummyP = hb + 0x200, lightP = hb + 0x300;
            *(IntPtr*)(gobP + EngineLayout.GobPartTree) = rootP;
            *(IntPtr*)(dummyP + EngineLayout.PartParent) = rootP;
            *(IntPtr*)(lightP + EngineLayout.PartParent) = dummyP;
            ((byte*)rootP)[EngineLayout.PartVisible] = 1;
            ((byte*)dummyP)[EngineLayout.PartVisible] = 0;
            ((byte*)lightP)[EngineLayout.PartVisible] = 1;
            Revoke();
            int hs = Insert(lightP);
            _gob[hs] = gobP; _root[hs] = rootP; _bsp[hs] = false;
            check.Expect(!Hidden(hs), "a light under a hidden node of the gob's own tree stays lit, as in vanilla");
            _root[hs] = dummyP;
            check.Expect(Hidden(hs), "...and one in a hidden appended tree is dropped");
        }
        finally { Marshal.FreeHGlobal(hb); Revoke(); }

        // The cap, over synthetic scores -- no engine memory and no PartLight.
        int maxWas = Lighting.MaxLit.V;
        _selN = 4; _candN = 4;
        for (int k = 0; k < 4; k++) { _sel[k] = k; _cand[k] = true; _want[k] = false; }
        _score[0] = 9f; _score[1] = 1f; _score[2] = 5f; _score[3] = 3f;
        Lighting.MaxLit.V = 2;
        Cap();
        check.Expect(_wantN == 2 && _want[1] && _want[3] && !_want[0] && !_want[2],
                     "the cap keeps the two LOWEST scores, which is the engine's own order");
        Lighting.MaxLit.V = 99;
        Cap();
        check.Expect(_wantN == 4, "a cap above the candidate count lights every one of them");
        _cand[2] = false; _candN = 3;
        Lighting.MaxLit.V = 2;
        Cap();
        check.Expect(_wantN == 2 && !_want[2], "a light out of reach is never lit, capped or not");
        Lighting.MaxLit.V = maxWas;
        _selN = 0; _candN = 0; _wantN = 0; Array.Clear(_cand); Array.Clear(_want);

        // The score and the filter, on a hand-built PartLight. The score is the engine's arithmetic rather
        // than ours, and it decides which lights survive the cap.
        IntPtr blk = Marshal.AllocHGlobal(0x400);
        try
        {
            for (int i = 0; i < 0x400; i++) ((byte*)blk)[i] = 0;
            var light = new PartLight(blk);
            light.Colour = new Vector3(0.5f, 0.25f, 0.125f);
            light.Pos = new Vector3(6f, 8f, 0f);        // |pos| = 10 from the origin
            light.Range = 10f; light.Intensity = 2f; light.FlagB = 0; light.Priority = 4;
            // No MDL node, so the node priority and fadingLight read as absent: flat stays 21.
            check.Expect(MathF.Abs(Score(light, Vector3.Zero, IntPtr.Zero) - 10f * 5f / (4f * 21f)) < 1e-5f,
                         "score = dist * (priority+1) / ((flagB+4) * 21)");
            light.FlagB = 1;
            check.Expect(MathF.Abs(Score(light, Vector3.Zero, IntPtr.Zero) - 10f * 5f / (5f * 21f)) < 1e-5f,
                         "...and a dynamic light divides by flagB+4 = 5");
            light.Colour = Vector3.Zero;
            check.Expect(Score(light, Vector3.Zero, IntPtr.Zero) == ScoreNone, "a black light scores last");
            light.Colour = new Vector3(0.5f, 0.25f, 0.125f);
            light.Range = 0f;
            check.Expect(Score(light, Vector3.Zero, IntPtr.Zero) == ScoreNone, "...and so does a zero-range one");
            light.Range = 10f;
            // The lit-gob preview outranks everything, and it is read off the light's own gob.
            var part = new Part(blk);
            part.Gob = new Gob((IntPtr)0x9000);
            check.Expect(Score(light, Vector3.Zero, (IntPtr)0x9000) == ScoreOnlyGob,
                         "the previewed gob's light sorts first");
            check.Expect(Score(light, Vector3.Zero, (IntPtr)0x9001) != ScoreOnlyGob, "...and no other gob's does");
            part.Gob = new Gob(IntPtr.Zero);
            // The candidate filter's terms, one at a time. Its last one calls the engine's own
            // GetLightAdjustedRadius, which is not resolvable headless, so the pointer test is skipped there.
            light.FlagA = 0; light.FlagB = 0;
            check.Expect(!Candidate(light, Vector3.Zero, 0f), "flags clear: not a candidate");
            light.FlagA = 1;
            check.Expect(Candidate(light, Vector3.Zero, 0f), "one flag set is enough");
            light.Intensity = 0f;
            check.Expect(!Candidate(light, Vector3.Zero, 0f), "zero intensity: not a candidate");
            light.Intensity = 2f;
            light.Colour = Vector3.Zero;
            check.Expect(!Candidate(light, Vector3.Zero, 0f), "black: not a candidate");
            light.Colour = new Vector3(0.5f, 0.25f, 0.125f);
            light.Range = 0f;
            check.Expect(!Candidate(light, Vector3.Zero, 0f), "zero range: not a candidate");
        }
        finally { Marshal.FreeHGlobal(blk); }

        // The maintained live list must agree with the wholesale rebuild, or a delta and a walk light the area
        // differently. Driven as a session does it: admissions, deaths and retirements with NO walk between,
        // then the walk's own Sweep over the same generation -- the two sets have to be identical.
        Revoke();
        {
            var lk = new IntPtr[24];
            for (int i = 0; i < lk.Length; i++) lk[i] = (IntPtr)(0x60000 + i * 0x30);
            foreach (IntPtr k in lk) { int t = Insert(k); if (t >= 0) { ListLive(t); _gen[t] = 5; } }
            check.Expect(_liveN == lk.Length, "every claimed slot is in the live list");
            // Retirements of both shapes, and one re-claim of a recycled address.
            Died(lk[3]); Died(lk[11]); Died(lk[12]);
            int re = Insert(lk[11]); if (re >= 0) { ListLive(re); _gen[re] = 5; }
            check.Expect(_liveN == lk.Length - 2, "...and a retirement takes its slot straight back out");
            // No duplicate can survive the round trip: the list is a set, and the same slot listed twice would
            // select its light twice.
            var seen = new System.Collections.Generic.HashSet<int>();
            bool dupe = false;
            for (int k = 0; k < _liveN; k++) if (!seen.Add(_live[k])) dupe = true;
            check.Expect(!dupe, "...with no slot listed twice");
            // Now the wholesale rebuild, on the generation every survivor carries.
            var before = new System.Collections.Generic.HashSet<int>(seen);
            Sweep(5);
            var after = new System.Collections.Generic.HashSet<int>();
            for (int k = 0; k < _liveN; k++) after.Add(_live[k]);
            check.Expect(before.SetEquals(after), "the maintained list and the rebuild name the same slots");
            bool mapped = true;
            for (int k = 0; k < _liveN; k++) if (_liveAt[_live[k]] != k) mapped = false;
            check.Expect(mapped, "...and the position map agrees with the list after either route");
        }
        Revoke();

        // BUG-LIGHTS, the defect this pins: at Slots 2048 / Probe 8 a light whose 8-slot window was full was
        // DROPPED -- 1422 live lights spilled 148502 times in one session, each one a lamp that never lit. The
        // table must take its whole capacity, whatever the keys do, so the adversarial case is the test: every
        // key on ONE hash, more of them than the old probe window by three orders of magnitude.
        Revoke();
        const int Clustered = 2048;   // above the 1422 a live city read, and 256x the probe window that spilled
        IntPtr[] cluster = ClusterKeys(Clustered);
        long overflowWas = _overflow;
        int got = 0;
        foreach (IntPtr key in cluster) if (Insert(key) >= 0) got++;
        check.Expect(got == Clustered, $"{Clustered} lights on ONE hash all take a slot (got {got})");
        check.Expect(_overflow == overflowWas, "...with no overflow, because the probe is the whole table");
        int found = 0;
        foreach (IntPtr key in cluster) if (Find(key) >= 0) found++;
        check.Expect(found == Clustered, $"...and every one is found again by its own pointer (got {found})");
        // The hash must reach the WHOLE table, or a bigger table is a bigger table in name only (the old
        // `>> 53` confined every key to the first 2048 slots).
        int lo = 0, hi = 0;
        for (long v = 0x40000; v < 0x40000 + 0x20000; v += 0x10)
        { if (Hash((IntPtr)v) < Slots / 2) lo++; else hi++; }
        check.Expect(lo > 0 && hi > 0, "the hash reaches both halves of the table");
        // ...and the table gives its slots back: retiring the chain from the tail empties it rather than
        // leaving 4096 tombstones behind (a chain-end tombstone cannot truncate anyone).
        for (int k = Clustered - 1; k >= 0; k--) Died(cluster[k]);
        check.Expect(_used == 0 && _tombs == 0, $"a retired chain is trimmed back to empty (used {_used}, retired {_tombs})");
        Revoke();

        // ...and the crash that trim opened: a light dies BETWEEN walks, so the live list still names its slot
        // when the next frame selects. Part::~Part fires for a light part with no walk behind it (a carried
        // torch's creature, a destroyed placeable, a BSP tile light), and the trim leaves the slot pointer
        // NULL with the freed mark clear -- so a guard on the mark alone walked into `new PartLight(0).Range`,
        // a read at +0x70. The selection has to reject both shapes of a retirement.
        int d0 = Insert((IntPtr)0x31000), d1 = Insert((IntPtr)0x32000);
        check.Expect(d0 >= 0 && d1 >= 0, "two lights for the retire-between-sweeps check");
        _gen[d0] = 11; _gen[d1] = 11;
        Sweep(11);
        check.Expect(_liveN == 2, "...both live after their walk");
        Died((IntPtr)0x31000);
        Died((IntPtr)0x32000);
        // The death takes the slot out of the live list at once now (the list is maintained, not republished),
        // so the selection does not even reach it -- but the guard STAYS, and the check goes on proving it,
        // because it is what stands between a slot that got back in by any route and a read at +0x70 on a null.
        check.Expect(_liveN == 0, "a death takes its slot out of the live list without waiting for a walk");
        _live[0] = d0; _live[1] = d1; _liveN = 2;   // put both retirements back in by hand: the guard's job only
        // Would SIGSEGV before the fix. The Scene is a zeroed block: the guard sits ahead of every light read,
        // so the selection reaches nothing but the two retired slots.
        int sceneBytes = Math.Max(EngineLayout.SceneSkyPos + 12, EngineLayout.SceneFogEnd + 4);
        IntPtr fakeScene = Marshal.AllocHGlobal(sceneBytes);
        try
        {
            for (int i = 0; i < sceneBytes; i++) ((byte*)fakeScene)[i] = 0;
            SelectInner(fakeScene);
        }
        finally { Marshal.FreeHGlobal(fakeScene); }
        check.Expect(_selN == 0, "a light retired since the sweep is not selected, trimmed slot or tombstone");
        Revoke();

        _unavailable = wasUnavailable;
        log.Info(check.Ok ? "LightRegistry.SelfCheck: passed" : "LightRegistry.SelfCheck: FAILED -- see above");
    }
}
