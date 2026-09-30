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
/// <item><see cref="WalkDone"/> first runs its OWN <c>BSPTraverse</c> for the TILE lights, which no part-tree
/// walk can reach: <c>CollapsePartTree</c> @0x462ff0 flattens a tile's tree and CLEARS every child list, so
/// the only container holding a tile light is the BSP node's <c>CExoArrayList&lt;PartLight*&gt;</c> at
/// <see cref="EngineLayout.BspDataLight"/>.</item>
/// </list></para>
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
    /// <summary>Table size. A dense live area runs a few hundred lights; the plaza benchmark reads 253.
    /// <para>ponytail: fixed, power of two, no growth path. An overflow drops the light from Alloy's set --
    /// a light that does not light -- and is counted as <c>table-full</c> on the period line. If that number
    /// is ever non-zero this is a sizing decision after all.</para></summary>
    private const int Slots = 2048;
    private const int Probe = 8;
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
    /// 2048 slots of which a few hundred are tenanted is a scan nobody needs every frame.</summary>
    private static readonly int[] _live = new int[Slots];
    private static int _liveN;

    private static ExtenderLog? _log;
    private static bool _probed;
    private static BspTraverseFn _bspTraverse;
    private static GetLightAdjustedRadiusFn _adjRadius;
    private static LightMgrGetMaxLightsFn _maxLights;
    private static IntPtr* _onlyGob;

    /// <summary>Period-line counters. Arrivals, departures and deaths are RATES (walk edges); the rest are
    /// populations, read at the last <see cref="Select"/>.</summary>
    private static long _arrived, _left, _died, _spill, _reclaimed;
    private static int _candN, _wantN, _hidden, _inactiveLod, _tile;

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
        _bspTraverse = EngineFn.BspTraverse;
        _adjRadius = EngineFn.GetLightAdjustedRadius;
        _maxLights = EngineFn.LightMgrGetMaxLights;
        _onlyGob = (IntPtr*)Native.Sym(SymbolMap.LightManagerOnlyGob);
        // No silent fallback (memory no-silent-fallbacks): without BSPTraverse the registry cannot reach a
        // tile light at all, and an interior lit only by its gobs is a black room that reads as content.
        if (_bspTraverse == null || _adjRadius == null || _maxLights == null)
        {
            string miss = _bspTraverse == null ? SymbolMap.BspTraverse
                        : _adjRadius == null ? SymbolMap.GetLightAdjustedRadius
                        : SymbolMap.LightMgrGetMaxLights;
            _unavailable = true;
            log.Always($"LightRegistry: {miss} did not resolve -- Alloy's light registry is OFF for the session; "
                     + "the light walk falls back to the engine's LightManager lists and says so on /alloy");
            return;
        }
        log.Info("LightRegistry: armed -- Alloy enumerates its own lights from the scene walk plus a BSP light "
               + $"pass (cap {_maxLights()} from GetMaxLights; light.maxlit overrides, light.registry 0 hands "
               + "the enumeration back to LightManager)");
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
            if (s < 0) { _spill++; return; }
            _arrived++;
            _bsp[s] = bsp;
            _gob[s] = new Part(light).Gob.Ptr;
            _root[s] = RootOf(light);
        }
        _gen[s] = gen;
    }

    /// <summary>The walk of <paramref name="gen"/> finished. Two steps: the tile lights the part-tree walk
    /// cannot reach, then the sweep -- an entry not seen on this generation left the scene.</summary>
    internal static void WalkDone(int gen)
    {
        if (_unavailable) return;
        _tile = 0;
        IntPtr scene = SceneWalk.WorldScene;
        if (scene != IntPtr.Zero)
        {
            IntPtr root = new Scene(scene).BspRoot;
            _bspGen = gen;
            if (root != IntPtr.Zero)
                // OUR callback's address, not an engine symbol -- the cast is the C# way to spell &OnBspNode.
                _bspTraverse(root, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnBspNode, IntPtr.Zero);
        }
        Sweep(gen);
    }

    /// <summary>The generation the BSP light pass files under: <see cref="OnBspNode"/> is an unmanaged callback
    /// and cannot take it as an argument.</summary>
    private static int _bspGen;

    [UnmanagedCallersOnly]
    private static void OnBspNode(IntPtr node, IntPtr ctx)
    {
        var bsp = new BSPNode(node);
        IntPtr* items = bsp.Lights;
        if (items == null) return;
        int n = bsp.LightCount;
        for (int i = 0; i < n; i++)
        {
            IntPtr p = items[i];
            if (p == IntPtr.Zero) continue;
            _tile++;
            Admit(p, true, _bspGen);
        }
    }

    /// <summary>Mark and sweep, and the rebuild of the live list the per-frame pass walks. Separate from
    /// <see cref="WalkDone"/> so the self-check can drive it with no Scene in sight.</summary>
    private static void Sweep(int gen)
    {
        _liveN = 0;
        for (int s = 0; s < Slots; s++)
        {
            if (_id[s] == IntPtr.Zero || _freed[s]) continue;
            if (_gen[s] != gen) { Drop(s, false); _left++; continue; }
            _live[_liveN++] = s;
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
        _freed[s] = true;
        if (!died) { _gob[s] = IntPtr.Zero; _root[s] = IntPtr.Zero; _gen[s] = 0; }
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
        _liveN = 0; _selN = 0; _candN = 0; _wantN = 0;
        AreaAmbient = IntPtr.Zero; AreaDiffuse = IntPtr.Zero;
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
            // Retired since the sweep built this list -- Part::~Part fires whenever the engine frees a part.
            if (_freed[s]) continue;
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

    private static int Find(IntPtr p)
    {
        int h = Hash(p);
        for (int k = 0; k < Probe; k++)
        {
            int s = (h + k) & (Slots - 1);
            if (_id[s] == IntPtr.Zero) return -1;
            if (_id[s] == p && !_freed[s]) return s;
        }
        return -1;
    }

    private static int Insert(IntPtr p)
    {
        int h = Hash(p);
        int free = -1;
        for (int k = 0; k < Probe; k++)
        {
            int s = (h + k) & (Slots - 1);
            if (_id[s] == p && !_freed[s]) return s;
            if (free < 0 && (_id[s] == IntPtr.Zero || _freed[s])) free = s;
        }
        if (free < 0) return -1;
        if (_freed[free] && _id[free] != IntPtr.Zero) _reclaimed++;
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
        return (int)(x >> 53) & (Slots - 1);
    }

    // ================= reporting =================

    /// <summary>The registry's clause on the <c>[alloy]</c> period line -- the only witness this table has.
    /// The populations (<c>lights</c>, <c>in reach</c>, <c>lit</c>) are the last <see cref="Select"/>'s, the
    /// walk-edge counts are the period's, and <c>table-full</c> is the one that must read 0.
    /// <para>Also where the rates are zeroed: <c>PassLifecycle</c> has no reset slot and this runs exactly once
    /// per period.</para></summary>
    internal static string Stats(int frames)
    {
        if (_unavailable) return "lightreg UNAVAILABLE -- the engine's LightManager lists are the source";
        if (!Use) return "lightreg off (light.registry 0) -- the engine's LightManager lists are the source";
        double ms = _selFrames == 0 ? 0
                  : _selTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _selFrames;
        string s = $"lightreg {_selN} lights ({_tile} tile), {_candN} in reach, {_wantN} lit, select {ms:F3}ms, "
                 + $"+{_arrived}/-{_left} walk edges, {_died} died, {_reclaimed} slots reclaimed, "
                 + $"{_hidden} hidden, {_inactiveLod} inactive-LOD, table-full={_spill} (must be 0), "
                 + $"area ambient={(AreaAmbient != IntPtr.Zero ? "yes" : "NONE")} "
                 + $"diffuse={(AreaDiffuse != IntPtr.Zero ? "yes" : "NONE")}";
        _arrived = 0; _left = 0; _died = 0; _selTicks = 0; _selFrames = 0;
        return s;
    }

    // ================= self-check =================

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
        check.Expect(_freed[sa] && Find(a) < 0, "a dead light's slot is freed and stops matching");
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

        Revoke();
        _unavailable = wasUnavailable;
        log.Info(check.Ok ? "LightRegistry.SelfCheck: passed" : "LightRegistry.SelfCheck: FAILED -- see above");
    }
}
