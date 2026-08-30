using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Alloy;
using Sowx.Graphics;
using static Sowx.Alloy.SceneLayout;

namespace Sowx.Alloy;

/// <summary>
/// #93 (map #68, spec #78 M2): the one Alloy submission path every rigid shadow caster goes through.
/// Tiles and placeables are the same thing here -- a part with an indexed VDO, an index range and a model
/// matrix -- so there is no kind-based branching anywhere below. #305 split the cube POPULATION in two --
/// a static array baked on the predicate and a small per-frame dynamic pool, composed with min() in the
/// receiver (never #187's rejected copy-based composite; <c>/shadow split 0</c> is the combined-bake A/B).
/// There is also a motion split, and #282 finished it: a view re-bakes only when its
/// recorded depth has stopped describing the world -- the view's own key changed, or something moved inside
/// it (<see cref="ViewStale"/>). #241 gave the cubes that predicate and left the sun at full price; both
/// carry it now. Every term errs toward stale, so a wrong answer costs a bake and can never freeze a shadow,
/// and <c>/shadow rebake 0</c> is the A/B back to #187's bake-everything-every-frame.
/// <para>#303: selection and submission are <see cref="CasterGather"/>'s compute dispatch alone -- the CPU
/// per-view walk and its persistent-mapped rings are deleted. The dispatch culls against
/// <see cref="CullPass"/>'s shared <c>SowxCullSphere</c> buffer (one set of bounds, one set of exemptions,
/// for the shadow cull and the colour cull alike) and writes indirect commands whose <c>baseInstance</c> IS
/// the ownership slot, so the depth stages read <c>uDraw[gl_BaseInstance]</c> directly. gl_BaseInstance
/// rather than gl_DrawID because compaction renumbers gl_DrawID and nothing else; <see cref="SelfCheck"/>
/// pins it.</para>
/// <para>The pass runs in the #80 4.6 context -- the SAME context the colour pass draws in, which is why it
/// binds Alloy's VAOs rather than keeping a set of its own -- and renders every view the Alloy lit stage
/// samples: the sun's concentric cascade array (#94) and the local lights' depth cube array (#95), six faces
/// per light. One <see cref="Collect"/> per frame selects the world; <see cref="Submit"/> then culls and
/// stages it per view, out of a ring sized for that view class. Nothing about a caster differs between the
/// two -- the sun and a torch draw the same geometry through the same command format.</para>
/// <para>#302: the caster population and every fact about it are ALLOY's. This pass kept a parallel copy of
/// the ownership table -- its own enrolment, its own VDO slot map and VAO cache, its own motion memo, its own
/// per-frame collect into model matrices and bounding spheres -- all of it written before Alloy existed and
/// all of it a recomputation of state #242/#243/#260/#282 already keep current. What is left is the
/// selection, the fits, the predicate, the cull and the submission; a caster is now an OWNERSHIP SLOT, and
/// the depth stage reads its model matrix, bone base and material out of <c>uDraw[slot]</c> exactly as the
/// colour stage does. ADR 0019 is the decision record.
/// <para>The consequence to know about is a ONE-FRAME LAG: the shadow bracket runs ahead of the colour build,
/// so the records it reads describe the previous frame's world. That is the lag creature poses already
/// carried by design (#113), it is coherent -- the mover signal and the record content describe the same
/// world state, so the predicate cannot miss a bake -- and it now extends to rigid movers, whose shadow
/// trails them by a frame. Retiring it means moving the pass behind the build, which is #176's work.</para>
/// <para>Creature casters ride the same path with no special case at all. An NWN creature is mostly
/// PARTS-BASED (live-verified by dumping the VAOs, 2026-08-20): each body piece is a rigid mesh parented to a
/// bone, posed entirely by its node matrix, so the animation is already in the matrix. A TRUE skinmesh
/// (cloak, robe, tail) carries bone attributes at locations 8/9 and is posed by <c>SkinPose</c> into Alloy's
/// bone block; the depth stage re-runs the same skinning off the same block and the same base.</para></para>
/// <para>Gated on <see cref="AlloyGate.Alloy"/> so the vanilla verdict -- and every other frame-start
/// refusal (#216) -- reverts it frame-atomically, along with the rest of the Alloy frame.
/// The <c>casters=0</c> config setting is the kill switch (#302 retired <c>skincast</c>: the posed set is
/// Alloy's, so <c>skinning</c> is the one dial that decides it), and
/// <c>/shadow</c> carries the look knobs. The frame path
/// allocates nothing.</para>
/// </summary>
public sealed unsafe class CasterPass : IExtenderService
{
    /// <summary>The live-list capacity IS the ownership table's: #302 made a caster an ownership slot, so a
    /// smaller number here would silently bake a fraction of what the colour pass draws -- which reads as
    /// "shadows go missing in big areas", not as a cap.</summary>
    private const int MaxCasters = AlloyRenderer.MaxOwned, MaxVdos = AlloyRenderer.MaxVdos;
    private const int LogEvery = 900, FirstLogAt = 120;
    /// <summary>Faces per cube.</summary>
    private const int CubeFaces = 6;
    /// <summary>A <c>DrawElementsIndirectCommand</c> is five uints -- count, instanceCount, firstIndex,
    /// baseVertex, baseInstance -- and the buffer is tightly packed, so the stride is 20 bytes.
    /// <c>firstIndex</c> is in INDICES, not bytes: the byte-offset APIs elsewhere in this repo multiply the
    /// VDO's element offset by 2 and this one must not.</summary>
    private const int CmdUints = 5, CmdBytes = CmdUints * 4;
    /// <summary>#302: the bake reads the COLOUR path's per-draw records and its bone block, at the colour
    /// path's own binding points. There is no second record buffer and no second bone ring.</summary>
    private const uint BindDraw = AlloyRenderer.BindDraw, BindBones = AlloyRenderer.BindBones;
    /// <summary>Slope of the cone that circumscribes a 90deg cube face -- its corners sit at
    /// <c>|perp| = sqrt(2) * depth</c> (#100's per-face cull). Deliberately a HAIR above sqrt(2): a caster
    /// exactly on the diagonal makes the comparison an equality, and in float
    /// <c>MathF.Sqrt(2)^2 == 1.9999999</c>, so the true sqrt(2) rejects the very corner it is meant to keep.
    /// <see cref="SelfCheck"/> caught that; the slack is what makes the test conservative in practice and not
    /// just on paper.</summary>
    internal const float FaceConeSlope = 1.4143f;
    internal const uint GlUnsignedShort = 0x1403;

    /// <summary>Cascade ceiling. The receiver's <c>uSunVp</c> array is sized from this, so raising it means
    /// raising both -- <see cref="SelfCheck"/> pins them together.</summary>
    internal const int MaxCascades = 4;
    /// <summary>Cascades rendered per frame (config <c>csm_cascades</c>).</summary>
    private static int _cascades = 4;
    /// <summary>Per-cascade face size in texels (config <c>csm_size</c>).</summary>
    private static int _mapSize = 2048;
    /// <summary>World units the outermost cascade reaches (<c>/shadow dist</c>). Sun shadow cost is bound by
    /// this and nothing else: the fit follows the player, so an area ten times the size costs the same.</summary>
    private static float _maxDist = MaxDistBoot;
    /// <summary>Boot value of <see cref="_maxDist"/>, and what <c>/shadow reset</c> restores.</summary>
    private const float MaxDistBoot = 120f;

    /// <summary>The depth array holding the cascades (0 = none yet). Read by the Alloy colour pass, in the
    /// same 4.6 context, on the frame after the render.</summary>
    internal static uint CascadeTex;
    /// <summary>Cascades successfully rendered on the last pass, innermost first.</summary>
    internal static int CascadeCount;
    /// <summary>Row-major light view-projection per cascade, 16 floats each -- the matrix each layer was
    /// actually RENDERED with, snapshotted at submit. Map contents are world-space, so a frame of lag is
    /// invisible; a recomputed matrix that disagreed with the render would not be.</summary>
    internal static readonly float[] CascadeVp = new float[MaxCascades * 16];
    /// <summary>World units per shadow texel per cascade -- the receiver's normal-offset scale.</summary>
    internal static readonly float[] CascadeTexel = new float[MaxCascades];
    /// <summary>Depth range per cascade in world units (<c>2 * radius + margin</c>, per
    /// <see cref="ShadowMath.DirectionalLightViewProj"/>) -- what turns the receiver's world-unit depth bias
    /// into the device units its comparison runs in.</summary>
    internal static readonly float[] CascadeRange = new float[MaxCascades];
    /// <summary>Local-light cube ceiling: how long the receiver's <c>uLocalLight</c> array is, and the most
    /// <c>cube_count</c> may ask for. Raising it costs uniform slots only -- the cube array itself is
    /// sized from the CONFIGURED count, so an unused ceiling is free.</summary>
    internal const int MaxCubes = 64;
    /// <summary>Local lights that get a shadow cube (config <c>cube_count</c>, 0 = off) and the per-face size
    /// (config <c>cube_size</c>). The interim system's receiver could not go past 32 lights at all because it
    /// unrolled a sampler per slot, and a cube ARRAY has no such ceiling -- the cost is VRAM and nothing else.
    /// <para>#151 raised the default from 8 to 24: a torch-lit street routinely declares 60+ shadow-casting
    /// lights, and at 8 the budget's membership churned as the player walked, which is what read as shadows
    /// popping in and out. Since #187 every slot in the budget bakes six faces every frame, so this count IS
    /// the pass's cost. Lower <c>cube_count</c> (or <c>cube_size</c>) on a small card.</para></summary>
    private static int _cubeLights = 32, _cubeSize = 512;
    /// <summary>Near plane shared by every cube face. Bigger is better depth precision; it also means a
    /// caster closer to the light than this is not recorded, so it stays small.</summary>
    private const float CubeNear = 0.3f;
    /// <summary>#151 shadow LOD: a caster whose world radius spans fewer than this many TEXELS of the map it
    /// is baking into is dropped from that view. Sub-texel casters cannot produce a stable shadow -- they
    /// alias in and out as the fit shifts -- so this trades nothing visible for most of a far cascade's
    /// population: cascade 3 covers the whole area with ~12cm texels, and the small props that dominate a
    /// dense area's caster count vanish from exactly the views where they were noise. Per-VIEW, so cascade 0
    /// (1,5cm texels) still bakes everything near the player. <c>/shadow lod</c> sets it; 0 disables.</summary>
    /// <summary>#152: default 0 = OFF at the user's request (parked for the M4 revisit) -- turn on with
    /// <c>/shadow lod 2</c>.</summary>
    private static float _lodTexels = LodBoot;
    /// <summary>Boot value of <see cref="_lodTexels"/>, and what <c>/shadow reset</c> restores.</summary>
    private const float LodBoot = 0f;

    /// <summary>The cube array holding the local-light shadows (0 = none yet), layer <c>6*slot + face</c>.</summary>
    internal static uint CubeTex;
    /// <summary>Cubes fully rendered on the last pass -- a light that lost a face to a ring stall is not in
    /// this count, so the receiver never samples a cube with one stale face in it.</summary>
    internal static int CubeCount;
    /// <summary>Per cube slot: the position the cube was baked from (xyz) and its far plane (w). The receiver
    /// reads the light's geometry from HERE, never from the live light, so its depth comparison stays welded
    /// to the depth it samples.</summary>
    internal static readonly float[] CubeLight = new float[MaxCubes * 4];
    /// <summary>Engine light identity per cube slot (equality only, never dereferenced -- the engine frees
    /// PartLight on area boundaries). This is what binds a light in the walk's snapshot to its cube.</summary>
    internal static readonly IntPtr[] CubeLightId = new IntPtr[MaxCubes];
    /// <summary>Face size of the cube array as rendered -- the receiver's texel scale.</summary>
    internal static int CubeFaceSize => _cubeSize;
    /// <summary>Near plane the cubes were rendered with; welds the receiver's depth reference to the render.</summary>
    internal static float CubeNearPlane => CubeNear;

    // --- #241: the cube re-bake predicate. Every slot's six faces baked unconditionally every frame was
    // #187's deliberate simplification, waived until the renderer owned the pipeline; #238's trace made the
    // shadow pass 51% of the heavy frame and 192 views/frame. This is the INVALIDATION that replaces it --
    // never the composite cache #187 measured and rejected, whose per-view server-side depth copy cost more
    // than the bakes it saved. A stale face is baked exactly as it is baked today; a fresh one is skipped.
    // The failure mode of a wrong answer is a shadow that FREEZES, so every term errs toward stale.

    /// <summary>The bake state of one cube slot, all of it carried across frames. Separate storage from
    /// <see cref="CubeLight"/>/<see cref="CubeLightId"/>, which the publish loop wipes every frame.</summary>
    private static readonly IntPtr[] _cubeKeyId = new IntPtr[MaxCubes];
    private static readonly float[] _cubeKeyPos = new float[MaxCubes * 3];
    private static readonly float[] _cubeKeyFar = new float[MaxCubes];
    /// <summary>Does this slot hold a complete, usable bake? False after a ring-fence timeout, a release, or
    /// any (re)creation of the array -- all of which leave the layers holding depth nobody may sample.</summary>
    private static readonly bool[] _cubeBaked = new bool[MaxCubes];
    /// <summary>Did a mover intersect this slot's light on the PREVIOUS frame? That is what invalidates the
    /// frame a mover LEAVES: it is inside no longer, so no live mover would name the slot it just vacated.
    /// #305: under the split this carries STATIC-EVENT evidence instead (appearances, vanishes, set
    /// transitions) -- toggling <c>/shadow split</c> sets the blanket, so the two meanings never mix.</summary>
    private static readonly bool[] _cubeHadMover = new bool[MaxCubes];

    // --- #305: the static/dynamic split. The predicate above can only SKIP a view; it cannot make a stale
    // one cheaper, and a stale cube re-bakes the whole static world it sees -- one walking creature dirties
    // the room. So the population is split: the STATIC array (the existing CubeTex) bakes on the predicate
    // and is invalidated only by static-set events, while a smaller DYNAMIC pool holds movers only and
    // re-bakes every frame, which is affordable because it is the movers and not the room. The receiver
    // composes the two with min() -- depth is a min-of-blockers, so it composes exactly, and NO COPY exists
    // anywhere in the design (this is not #187's composite cache, whose per-view glCopyImageSubData is what
    // lost). Cubes only; the sun cascades keep the plain predicate.

    /// <summary>Dynamic-pool face size (config <c>cube_dyn_size</c>) and page count (config
    /// <c>cube_dyn_count</c>, 0 = no pool, split permanently off; defaults to the cube budget so the pool
    /// never starves into the combined fallback). A PAGE is one light's six mover-only
    /// faces, claimed only while a mover stands inside that light -- the pool is per mover-bearing light,
    /// never a 1:1 mirror of the static array, which is what keeps its VRAM a fraction of the static
    /// array's. Movers are few, small and close, so reduced resolution is the right spend.</summary>
    private static int _cubeDynSize = 256, _cubeDynPages = 16;
    /// <summary><c>/shadow split 1|0</c>: the A/B. Off = one combined bake per cube on the plain #241
    /// predicate, exactly as before this ticket.</summary>
    private static bool _split = true;
    /// <summary>The dynamic pool's cube array (0 = none yet), layer <c>6*page + face</c>.</summary>
    internal static uint CubeDynTex;
    /// <summary>Per cube slot: the pool PAGE holding its movers, PLUS ONE -- 0 means "no movers near this
    /// light", which is also what a receiver program that never sets the uniform reads, so a stale content
    /// override degrades to static-only rather than to sampling page zero. Published fresh each frame the
    /// page bakes.</summary>
    internal static readonly float[] CubeDynPage = new float[MaxCubes];
    /// <summary>Face size of the dynamic pool as rendered.</summary>
    internal static int CubeDynFaceSize => _cubeDynSize;
    /// <summary>Slot -&gt; claimed pool page (-1 none) and page -&gt; owning slot (-1 free). Sticky, like the
    /// slots themselves: a page churn would be invisible in the depth (it re-bakes every frame) but wastes
    /// nothing to avoid.</summary>
    private static readonly int[] _dynPageOf = new int[MaxCubes];
    private static int[] _dynPageSlot = Array.Empty<int>();
    /// <summary>Slot is in COMBINED mode: it has movers but no pool page (pool exhausted), so it bakes both
    /// halves into the static array every frame -- the per-slot fallback to pre-split behaviour. A mode
    /// change re-keys the slot, or a mover baked in while combined would outlive the mode.</summary>
    private static readonly bool[] _cubeCombined = new bool[MaxCubes];
    /// <summary>Frame each OWNERSHIP slot was last a dynamic caster on -- the set-transition detector. A
    /// caster whose membership flips (starts or stops moving) is a static-set event: the static bake must
    /// shed it or adopt it, within one predicate cycle.</summary>
    private static readonly int[] _dynStamp = new int[MaxCasters];
    private static long _cubeDynBakedPages;
    /// <summary>#282: the sun's half of the same bake state. A cascade's recorded depth is a function of the
    /// fit -- focus, light direction, radius and margin -- and of what stood inside it, so those four are the
    /// key and the mover test is the only other term.
    /// <para>#187 declared no motion split for the sun and CasterPass's own header still said so; that was
    /// written when there was no predicate anywhere and every view baked every frame, and #241 then gave the
    /// cubes one and left this at full price -- 4 x every live caster, every frame, with no staleness test at
    /// all. The decision is re-taken here and it is PREDICATED: the cascades follow the focus, so a walking
    /// player invalidates all four anyway and the skip only pays where it should, in a still frame.</para></summary>
    private static readonly float[] _sunKeyFocus = new float[MaxCascades * 3];
    private static readonly float[] _sunKeyDir = new float[MaxCascades * 3];
    private static readonly float[] _sunKeyFit = new float[MaxCascades * 2];
    private static readonly bool[] _sunBaked = new bool[MaxCascades];
    private static readonly bool[] _sunHadMover = new bool[MaxCascades];
    private static long _sunBakedViews, _sunSkippedViews;

    // --- #306: the sun cascades join the static/dynamic split. The mechanism is the cubes' (#305): the
    // existing cascade array becomes the STATIC half, baked on the predicate; a same-shape dynamic array
    // holds movers only and re-bakes every frame; the receiver composes the two with min(). What makes a
    // cascade reusable at all is the texel snap -- ShadowMath now quantises the whole fit matrix, so the
    // static key is the MATRIX ITSELF and a sub-texel focus move reproduces it bit-identical. Whether the
    // snap actually buys reuse at walking speed is a measurement, not an argument: the period line reports
    // static re-bakes per cascade LAYER, and a layer whose static half is stale essentially every frame
    // degrades itself to the combined bake (today's behaviour, SetAll, no dynamic half) and says so.

    /// <summary>The sun's dynamic depth array (0 = none), one layer per cascade at the SAME face size as the
    /// static array -- the near cascade must carry the player's own shadow at full quality, and a mirror is
    /// the shape with no per-layer resolution bookkeeping. Config <c>csm_dyn</c> 0 disables the sun split
    /// permanently (the cubes keep theirs).</summary>
    internal static uint SunDynTex;
    /// <summary>Per cascade layer: 1 = the dynamic half holds a fresh mover bake this frame, 0 = sample the
    /// static half alone. The receiver's <c>uSunDyn</c>; zero is what a program that never sets it reads.</summary>
    internal static readonly float[] SunDynMask = new float[MaxCascades];
    /// <summary>The static half's bake key under the split: the fit matrix each layer was last baked with.
    /// Fully texel-quantised since #306's ShadowMath snap, so comparing it IS the "snapped centre moved"
    /// test, with the direction and fit folded in for free.</summary>
    private static readonly Matrix4x4[] _sunKeyVp = new Matrix4x4[MaxCascades];
    /// <summary>Which population the recorded static layer holds -- SetAll (combined) or SetStatic. A mode
    /// flip re-keys the layer, or a mover baked in while combined would outlive the mode (#305's cube rule).</summary>
    private static readonly bool[] _sunKeyCombined = new bool[MaxCascades];
    /// <summary>Layer is DEGRADED to the combined bake: its static half was stale on (nearly) every frame of
    /// the last window, so the split was paying two bakes for one. Decided per window, with hysteresis.</summary>
    private static readonly bool[] _sunCombined = new bool[MaxCascades];
    /// <summary>The degrade window: static bakes per layer over <see cref="SunWin"/> frames. Enter combined
    /// at &gt;= 90% of the window, leave at &lt;= 50% -- the gap is what stops a layer flapping at the edge.</summary>
    private const int SunWin = 120;
    private static readonly int[] _sunWinBaked = new int[MaxCascades];
    private static int _sunWinFrames;
    /// <summary>Static re-bakes per cascade LAYER this period -- the number that says what the texel snap's
    /// reuse rate actually is at walking speed (AC: a reading, not an argument).</summary>
    private static readonly long[] _sunStaticL = new long[MaxCascades];
    private static long _sunDynBakedViews;
    /// <summary>Config <c>csm_dyn</c>: 0 = no sun dynamic array, the cascades keep the combined bake.</summary>
    private static bool _sunSplitCfg = true;

    /// <summary>Set by anything that makes every recorded face meaningless at once -- a tuning change, a
    /// release, a frame whose scan never reached every slot. Consumed only by a COMPLETE scan, so the
    /// evidence outlives any frame that could not carry it.</summary>
    private static bool _invalidate = true;
    /// <summary>Live indices of this frame's movers -- what every slot's staleness scan runs over. Small by
    /// construction in a still area, which is the whole point.</summary>
    private static readonly int[] _moverIdx = new int[MaxCasters];
    private static int _moverCount;
    /// <summary>#305: the frame's DYNAMIC casters (live indices) -- skinned, matrix-moved, dangly,
    /// vertex-dirty. What the dynamic pool bakes, and what marks a light mover-bearing. A subset of
    /// <see cref="_moverIdx"/> (which adds appearances, for the sun's unsplit predicate).</summary>
    private static readonly int[] _dMoverIdx = new int[MaxCasters];
    private static int _dMoverCount;
    /// <summary>#305: the frame's STATIC-SET EVENTS (live indices) -- a caster that appeared, or whose
    /// dynamic membership flipped. The only live movers that may invalidate a static page; vanishes ride the
    /// ghost list as they always did.</summary>
    private static readonly int[] _sMoverIdx = new int[MaxCasters];
    private static int _sMoverCount;
    /// <summary>#305: is this live caster dynamic this frame? Staged into the gather source as the set
    /// flag its dispatch filter reads.</summary>
    private static readonly bool[] _liveDyn = new bool[MaxCasters];
    /// <summary>Spheres of the casters that were live last frame and are not live now -- a part that died, a
    /// creature that walked out of the capture, a VDO item range that stopped resolving. A vanished caster
    /// leaves no live mover to name the slot it was standing in, so its LAST KNOWN sphere is the mover: the
    /// slots it shadowed invalidate, and nothing else does. Bounded; an overflow falls back to the blanket
    /// invalidate rather than silently dropping one.</summary>
    private const int MaxGhosts = 1024;
    private static readonly float[] _ghostCx = new float[MaxGhosts], _ghostCy = new float[MaxGhosts],
                                    _ghostCz = new float[MaxGhosts], _ghostR = new float[MaxGhosts];
    private static int _ghostCount;
    /// <summary>Per VDO slot: the engine wrote into its position buffer this frame, so every caster on it has
    /// moved without its matrix moving. Resolved once per slot per frame rather than per caster.</summary>
    private static readonly bool[] _vDirty = new bool[MaxVdos];
    /// <summary><c>/shadow rebake 0</c> turns the predicate off and bakes every slot every frame, as before
    /// #241. The live half of the self-check -- and the escape hatch if a shadow is ever seen to freeze.</summary>
    private static bool _rebake = true;
    /// <summary>#241: did the last frame's slot scan reach every slot? Mover evidence lives in
    /// <see cref="_cubeHadMover"/>, which only the scan writes -- so a frame that collected movers and then
    /// did not scan (a ring stall broke the loop, the bracket was refused, the cubes were off) leaves slots
    /// holding no record that anything moved near them. Consuming the evidence anyway is a shadow frozen
    /// mid-move on a rare stall frame, which is the one failure nobody would ever trace. A blanket re-bake
    /// after such a frame costs one frame and cannot lie.</summary>
    private static bool _cubeScanned;
    /// <summary>#282: the sun's half of the same evidence, and which of the two passes actually ran. The
    /// blanket invalidate is consumed by both and may only be CLEARED once every pass that ran completed its
    /// scan -- a pass that did not run cannot veto, and a frame where neither ran clears nothing.
    /// <para>Both of the failures this shape exists for are real. Clearing it inside CubePass meant a sun
    /// stall could be swallowed by a complete cube scan the same frame, since FrameGraph decides the order
    /// and the mover evidence is gone by the next one; and in a torchless exterior CubePass never runs at
    /// all, so nothing ever cleared it and the sun predicate would have re-baked all four cascades forever.
    /// </para></summary>
    private static bool _sunScanned, _sunRan, _cubeRan;
    /// <summary>#282: this frame's blanket-invalidate verdict, snapshot before either pass runs.</summary>
    private static bool _frameInvalidate;
    private static long _cubeBakedSlots, _cubeSkippedSlots, _moverSum, _ghostSum;
    /// <summary>#241: movers BY REASON. A skip count that stays at zero says the predicate is not paying;
    /// only this says why -- a world whose matrices all jitter, a shared vertex pool marked dirty by one
    /// dangly mesh, or simply a scene full of creatures. Each is a different fix.</summary>
    private static long _mvMatrix, _mvDangly, _mvVertex, _mvBone, _mvAppear;
    private static long _degenSum;
    /// <summary>#302: casters REFUSED this frame because no record has been built for the current tenant of
    /// their slot. They cast nothing this frame and re-appear the next; the vanish sweep covers the views
    /// they were standing in. High and steady after an area settles means the colour build is skipping slots
    /// the bake wants -- which is a missing shadow, so it is on the period line rather than inferred.</summary>
    private static long _noRecordSum;

    /// <summary>#301: bakes BY REASON, [family * 3 + reason] over families sun=0/cube=1 and the first term of
    /// <see cref="ViewStale"/> that fired -- blanket=0, key=1, mover=2. A SKIP needs no such breakdown: it is
    /// the conjunction, every term false at once (still light, still slot, no mover), so the skip counts
    /// already say it. What a sizing pass cannot get from them is which term is paying for the bakes -- a
    /// cascade re-fitting under a walking camera is key, a room with one creature in it is mover, and the two
    /// have different fixes.</summary>
    private static readonly long[] _bakeWhy = new long[6];
    private const int WhySun = 0, WhyCube = 3;

    /// <summary>Charge one bake to the first <see cref="ViewStale"/> term that fired; the order matches the
    /// predicate's own short-circuit, so the charge is the reason a reader would name.</summary>
    private static void ChargeBake(int family, bool invalidate, bool keyChanged)
        => _bakeWhy[family + (invalidate ? 0 : keyChanged ? 1 : 2)]++;

    /// <summary>Presented frames since arming; the freshness clock for <see cref="CascadesFresh"/>.</summary>
    private static int _frameSeq;
    private static int _cascadeStamp = -1, _cubeStamp = -1;

    /// <summary>May the colour pass sample the cubes right now? Same single test as
    /// <see cref="CascadesFresh"/>: rendered on the most recent frame tick or not at all, which covers the
    /// vanilla verdict, a module unload and a disarm together.</summary>
    internal static bool CubesFresh => CubeCount > 0 && CubeTex != 0 && _cubeStamp == _frameSeq;

    /// <summary>Cube slot holding <paramref name="id"/>'s shadow, or -1. Called once per area light from the
    /// light walk, so it is O(lights * cubes) = a few hundred comparisons a frame, alloc-free.</summary>
    internal static int CubeSlotOf(IntPtr id)
    {
        if (!CubesFresh || id == IntPtr.Zero) return -1;
        for (int i = 0; i < CubeCount; i++) if (CubeLightId[i] == id) return i;
        return -1;
    }
    /// <summary>May the colour pass sample the cascades right now? Only when the array was rendered on the
    /// most recent frame tick. That one test covers every way the maps go stale at once: an interior (no sun
    /// -&gt; no render), a vanilla stretch (the pass is gated on <see cref="AlloyGate.Alloy"/>, so the toggle
    /// reverts sun shadows on the very next frame), and a disarm.</summary>
    internal static bool CascadesFresh => CascadeCount > 0 && CascadeTex != 0 && _cascadeStamp == _frameSeq;

    /// <summary>How far every cascade's fit reaches TOWARD the light, past its own extent. The full max
    /// shadow distance for all of them, NOT each one's radius: a caster only has to be up-sun of a fragment to
    /// shade it, at any distance, and innermost-first selection means the fragment takes cascade 0's answer
    /// and never consults a wider one. Scaling this per cascade would give cascade 0 a 30u reach, so a wall
    /// 40u up-sun would drop out of the only cascade the ground at the player's feet ever asks -- long shadows
    /// at a low sun vanishing exactly where the detail is supposed to be best.
    /// <para>It costs a narrow column, not volume: the perpendicular cull still holds at the cascade's own
    /// radius, so cascade 0 admits casters within 15u of the light axis and 135u along it.</para></summary>
    private static float FitMargin() => _maxDist;


    private static ExtenderLog? _log;
    private static bool _armed, _disarmed;
    private static uint* _primTable;
    /// <summary>The diffuse lookup for masked casters: <c>Material::GetTexture(mat, 0)</c> then the Aurora
    /// texture's live GL id. Both re-read per frame -- a CAurTexture* is BORROWED and freed on area
    /// transitions, so it is never cached.</summary>
    private static delegate* unmanaged<IntPtr, int, long> _getTexture;


    private static readonly float[] _liveCx = new float[MaxCasters], _liveCy = new float[MaxCasters],
                                    _liveCz = new float[MaxCasters], _liveR = new float[MaxCasters];
    private static readonly int[] _liveIdxCount = new int[MaxCasters];
    private static readonly uint[] _liveFirst = new uint[MaxCasters], _liveBaseVert = new uint[MaxCasters];
    private static readonly int[] _liveVao = new int[MaxCasters], _livePrim = new int[MaxCasters];
    /// <summary>#302: the ALLOY OWNERSHIP SLOT this caster draws from. The one field the bake stages per
    /// draw that is not geometry -- the model matrix, the bone base and the material live in
    /// <c>uDraw[slot]</c>, which the depth stage reads through exactly the indirection the colour path uses.
    /// </summary>
    private static readonly int[] _liveSlot = new int[MaxCasters];
    /// <summary>The frame each ownership slot was last collected on, and last frame's list. Together they are
    /// the vanish detector: a slot in the previous list whose stamp is not this frame's stopped casting, and
    /// its last known sphere is the mover that invalidates the views it stood in. A stamp rather than a
    /// bitmap so nothing has to be cleared.</summary>
    private static readonly int[] _liveStamp = new int[MaxCasters];
    private static readonly int[] _prevSlots = new int[MaxCasters];
    private static int _prevCount;
    /// <summary>This frame's diffuse texture id for a MASKED caster (0 = not masked, or the texture did not
    /// resolve). Re-fetched every collect: a <c>CAurTexture*</c> is borrowed and freed on area transitions,
    /// so it is never cached -- and the per-frame fetch is what keeps an animated diffuse cutting the right
    /// silhouette.</summary>
    private static readonly int[] _liveTex = new int[MaxCasters];
    /// <summary>...and the discard threshold that goes with it, staged into the per-caster record.</summary>
    private static readonly float[] _liveCut = new float[MaxCasters];
    /// <summary>Creature casters occupy the head of the live list (#100 stages them first); the static world
    /// follows. Submit uses the split to report what a view actually took.</summary>
    private static int _liveCreatures;
    private static int _liveCount;

    /// <summary>Live casters summed over the period. Reported ALONGSIDE the mover count, because the two
    /// being equal is a bug with no other symptom: it means the still world is not in the list the bake
    /// reads, and every shadow of it vanishes the moment nothing moves. That is exactly what #241 shipped,
    /// and "still" being printed is what stops it being shipped twice.</summary>
    private static long _liveSum;
    /// <summary>Creature casters in the frame's staging (pre-cull; the survivors are the GPU's) -- the number
    /// that answers "why did the shadow vanish": a frame that stages zero creature casters cannot show one.</summary>
    private static int _submitCreatures;
    /// <summary>Cutout-caster forensics: draws that ran the 0.2 discard this period, and the three ways a
    /// masked caster can fail to reach the bake -- no UV stream to sample with, no diffuse this frame, or no
    /// cutout program at all. Each of them is a MISSING leaf shadow; none of them is the solid quad.</summary>
    private static long _submitMasked;
    private static int _maskNoTex, _maskNoProg;

    // --- 4.6-side objects (touched only inside the bracket)
    private static uint _prog, _fbo, _depthTex;
    private static int _locVp = -1;
    /// <summary>The masked (alpha-tested) depth program and its uniforms. Zero = it could not build, and
    /// masked casters then stay out of every bake.</summary>
    private static uint _progMask;
    private static int _locMaskVp = -1, _locMaskTex = -1, _locMaskCut = -1;
    private static bool _glFailed, _progFailed;

    // --- #307: the layered bake. With #302/#303 a view is a dispatch and a draw; this collapses the loop:
    // every stale view is QUEUED instead of submitted, and one flush per target texture runs one layered
    // dispatch (all views at once, commands tagged with their view) and one indirect-count draw per batch
    // into a layered framebuffer -- the vertex stage routes by gl_Layer out of the view table. The per-view
    // FBO/clear loop stays (a clear must touch only the stale layers), but the per-view uniform upload, the
    // per-view dispatch and the views x batches MDI storm all go. ARB_shader_viewport_layer_array is NOT
    // core in 4.6, so it is probed at runtime and the per-view path is kept as the fallback -- and as the
    // A/B (/shadow layered 0), which must produce identical shadows.

    /// <summary><c>/shadow layered 1|0</c>: the A/B. Off = one dispatch + one MDI set per view, as before.</summary>
    private static bool _layerDial = true;
    /// <summary>0 = not yet probed, 1 = the extension is present, -1 = absent (said once; per-view path).</summary>
    private static int _layerProbe;
    private static uint _progLayer, _progLayerMask;
    private static int _locLayerMaskTex = -1, _locLayerMaskCut = -1;
    private static bool _layerProgFailed;
    /// <summary>The frame's queued stale views. 1024 covers the ceiling population (4 + 4 cascades and
    /// 64 x 6 x 2 cube faces); an overflow renders inline through the per-view path rather than dropping.</summary>
    private const int MaxBakeViews = 1024;
    /// <summary>Commands the layered buffer may hold per rotation slot (x20 B). Past it the flush chunks its
    /// views -- only a blanket-invalidate frame in a huge area ever needs more than one chunk.</summary>
    private const int MaxLayerCmds = 512 * 1024;
    private static readonly uint[] _qTex = new uint[MaxBakeViews];
    private static readonly int[] _qSize = new int[MaxBakeViews], _qLayer = new int[MaxBakeViews],
                                  _qSet = new int[MaxBakeViews];
    private static readonly bool[] _qPoint = new bool[MaxBakeViews];
    private static readonly Matrix4x4[] _qVp = new Matrix4x4[MaxBakeViews];
    private static readonly Vector3[] _qFocus = new Vector3[MaxBakeViews], _qDir = new Vector3[MaxBakeViews];
    private static readonly float[] _qReach = new float[MaxBakeViews], _qAlong = new float[MaxBakeViews];
    private static int _qN;
    /// <summary>One chunk's staged view table, in <see cref="CasterGather.ViewFloats"/> layout.</summary>
    private static readonly float[] _viewStage = new float[MaxBakeViews * CasterGather.ViewFloats];
    private static long _layerFlushFails;

    /// <summary>Is the layered path taking this frame's views?</summary>
    private static bool LayeredActive => _layerDial && _layerProbe > 0 && _progLayer != 0;
    private static readonly StandDown _down = new("CasterPass DISARMED");

    // --- lifecycle + diagnostics
    private static bool _wasInModule, _loggedOnce, _noSunLogged;
    private static int _tableGen = -1;
    private static int _frames;
    private static long _mdiCalls;
    private static long _cubeLightsSum;

    /// <summary>Views rendered on the last frame -- since #241 that is the views the re-bake predicate found
    /// STALE, not every view the budget holds; <c>/shadow rebake 0</c> is what puts it back to all of them -- plus the CPU
    /// milliseconds the shadow slot took. The milliseconds are CPU SUBMISSION only; <see cref="_qShadow"/> is
    /// what measures the GPU side.</summary>
    internal static int LastViews;
    internal static float LastShadowMs;
    /// <summary>#151: GPU time of the whole shadow bracket. The CPU stopwatch only sees submission, and the
    /// live logs had ~12ms of frame time no CPU timer accounted for -- this query is what convicts or clears
    /// the pass, and it is the number #187's regression is read off.</summary>
    private static GpuQuery _qShadow;
    private static bool _qShadowOn;
    private static long _viewSum;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!Settings.Bool("casters", true) || GameGate.Locked)
        { log.Info("CasterPass: stood down (casters=0 or lockchat)"); return; }
        if (!SelfCheck(log)) return;

        // Config bucket (ADR 0013): every one of these sizes an allocation or gates the arming, so it is a
        // boot decision. The look knobs -- cascade distance and the LOD threshold -- are /shadow's.
        _mapSize = Settings.Int("csm_size", _mapSize, 256, 8192);
        _cascades = Settings.Int("csm_cascades", _cascades, 1, MaxCascades);
        _cubeLights = Settings.Int("cube_count", _cubeLights, 0, MaxCubes);
        _cubeSize = Settings.Int("cube_size", _cubeSize, 64, 2048);
        // #305: the dynamic pool's shape. Sizes an allocation, so config, not /shadow (ADR 0013).
        // The page DEFAULT is the cube budget itself: the first live run saturated a 16-page pool in a
        // 32-cube street and every overflow slot fell back to the combined bake -- the exact static-bake
        // churn the split exists to remove, for 24 MB saved. Full coverage is the right default; shrink
        // cube_dyn_count on a small card.
        _cubeDynSize = Settings.Int("cube_dyn_size", _cubeDynSize, 64, 2048);
        _cubeDynPages = Settings.Int("cube_dyn_count", _cubeLights, 0, MaxCubes);
        // #306: the sun's half of the split sizes an allocation too (a csm_cascades x csm_size mirror).
        _sunSplitCfg = Settings.Int("csm_dyn", 1, 0, 1) != 0;
        _dynPageSlot = new int[Math.Max(_cubeDynPages, 1)];
        Array.Fill(_dynPageSlot, -1);
        Array.Fill(_dynPageOf, -1);

        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _getTexture = (delegate* unmanaged<IntPtr, int, long>)Native.Sym(SymbolMap.MatGetTexture);
        if (_primTable == null)
        { log.Info("CasterPass: primitive-type table unresolved -- Alloy caster submission unavailable"); return; }

        // Repack moves a VDO's item indices and offsets under us. Every other consumer of these tables
        // already invalidates on it (#88 chain: draw funnels 50, interim cube 40, Alloy colour 30) and this pass
        // must too -- a stale item index does not read as an empty draw, it reads as ANOTHER part's index
        // range, which submits out-of-range indices to the GPU.
        _repackSlot = hooks.Chain(SymbolMap.VdoRepack,
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnVdoRepack, RepackPriority);
        if (_repackSlot == null)
        { log.Info("CasterPass: could not chain VdoRepack -- refusing to arm on stale index ranges"); return; }

        CasterGather.Init(log);

        _armed = true;
        log.Info($"CasterPass: armed -- Alloy multidraw-indirect caster submission into a {_cascades}x{_mapSize}" +
                 $" sun cascade array out to {_maxDist:F0}u (config csm_cascades/csm_size, /shadow dist)" +
                 $" and {_cubeLights}x{_cubeSize} local-light shadow cubes (config cube_count/cube_size)" +
                 ", posed casters bake from Alloy's own bone block" +
                 "; needs the part table, so unarmed draw funnels leave it with nothing to enroll");
    }

    /// <summary>The caster pass's live dials, for <c>/shadow</c> (ADR 0013 bucket 1). Both are look
    /// judgements made while walking around -- the cascade reach and the sub-texel cull that trades far-away
    /// detail for bake time -- and neither reallocates anything, which is what keeps them out of the config
    /// file next to the map sizes.</summary>
    internal static string Describe()
        => $"dist {_maxDist:F0} lod {_lodTexels:F1} rebake {(_rebake ? 1 : 0)} " +
           $"gather {CasterGather.Mode} split {(_split ? 1 : 0)} layered {(_layerDial ? 1 : 0)} " +
           $"({_cascades}x{_mapSize} cascades{(_sunSplitCfg ? " + dyn mirror" : "")}, " +
           $"{_cubeLights}x{_cubeSize} cubes, " +
           $"{_cubeDynPages}x{_cubeDynSize} dynamic pool pages)";

    /// <summary>Set one dial, returning its old value; NaN when the key is not ours, which is how the
    /// dispatcher knows to try the scene feed's dials instead.</summary>
    internal static float Tune(string key, float v)
    {
        switch (key)
        {
            case "dist":
                float od = _maxDist;
                _maxDist = MathF.Max(v, 1f);
                _invalidate = true;
                return od;
            case "lod":
                float ol = _lodTexels;
                _lodTexels = MathF.Max(v, 0f);
                _invalidate = true;   // the LOD cull decides what a bake CONTAINS; recorded faces predate it
                return ol;
            case "gather":
                // #303's A/B: 1 = the dispatch culls, 0 = the dispatch admits every staged caster into every
                // view. "A shadow is missing" needs a bisect, and cull-off is the known-good half of it.
                float og = CasterGather.Mode;
                CasterGather.Mode = v != 0f ? 1 : 0;
                return og;
            case "rebake":
                float orb = _rebake ? 1f : 0f;
                _rebake = v != 0f;
                return orb;
            case "split":
                // #305's A/B: 0 = one combined bake per cube on the plain predicate. The blanket is set
                // because the recorded faces (and _cubeHadMover's meaning) belong to the other mode.
                float os = _split ? 1f : 0f;
                _split = v != 0f;
                _invalidate = true;
                return os;
            case "layered":
                // #307's A/B: 0 = one dispatch and one MDI set per view. Same content either way, so no
                // blanket is needed -- the recorded depth does not depend on which path baked it.
                float oly = _layerDial ? 1f : 0f;
                _layerDial = v != 0f;
                return oly;
            default: return float.NaN;
        }
    }

    /// <summary>Back to the boot values.</summary>
    internal static void ResetTuning()
    { _maxDist = MaxDistBoot; _lodTexels = LodBoot; _rebake = true; _split = true; _layerDial = true; _invalidate = true; CasterGather.Mode = 1; }

    /// <summary>Chain priority on Repack: invalidation order among observers doesn't matter.</summary>
    private const int RepackPriority = 20;
    private static IntPtr* _repackSlot;

    /// <summary>Repack observer. #302 left this pass no cached index ranges to invalidate -- Collect re-reads
    /// them from Alloy's own VDO tables every frame -- but the LIVE list and the gather's uploaded staging are
    /// both copies of ranges that have just moved, so both are dropped rather than submitted from.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoRepack(IntPtr vdo)
    {
        var next = (delegate* unmanaged<IntPtr, void>)*_repackSlot;
        if (next != null) next(vdo);
        _liveCount = 0; _liveCreatures = 0;
        CasterGather.Stand();
    }











    /// <summary>#241: the whole re-bake decision for one VIEW, as four independent reasons a recorded depth
    /// layer may no longer describe the world. Pulled out so <see cref="SelfCheck"/> can pin each one.
    /// <para>#282 gave the sun cascades the same predicate. The four reasons are the same four -- a blanket
    /// invalidate, the view's own key changing, a mover in it now, a mover in it last frame -- and what
    /// differs between a cube face and a cascade is only which volume the mover test runs against.</para></summary>
    internal static bool ViewStale(bool invalidate, bool keyChanged, bool hadMover, bool hasMover)
        => invalidate || keyChanged || hadMover || hasMover;

    /// <summary>#302 (review): the enrollment's set-change blanket -- a departed caster's slot is re-tenanted
    /// before the vanish sweep looks, so the set signature is what invalidates on its behalf.</summary>
    internal static void ShadowInvalidate() => _invalidate = true;

    /// <summary>#282: does a sphere at <paramref name="dx"/>/<paramref name="dy"/>/<paramref name="dz"/> from
    /// the focus reach into a directional fit? The walk's own cull, term for term (perpendicular distance
    /// from the light axis against reach, distance ALONG it against the fit's extent) so the sun's staleness
    /// predicate and the sun's cull can only ever disagree in the direction that costs a bake.</summary>
    internal static bool InFit(float dx, float dy, float dz, float r, Vector3 dir, float reach, float along)
    {
        float t = dx * dir.X + dy * dir.Y + dz * dir.Z;
        float px = dx - t * dir.X, py = dy - t * dir.Y, pz = dz - t * dir.Z;
        return px * px + py * py + pz * pz <= (reach + r) * (reach + r) && MathF.Abs(t) <= along + r;
    }

    /// <summary>Resolve a VDO item's index range, live out of the item tables. Every consumer re-reads it
    /// rather than caching it because <c>Repack()</c> MOVES items, and a stale index range does not read as an
    /// empty draw -- it reads as another part's geometry. False = nothing drawable there right now.
    /// <para><paramref name="first"/> is in INDICES, not bytes: the byte-offset APIs elsewhere in this repo
    /// multiply the element offset by 2 and the indirect command must not.</para></summary>
    private static bool TryRange(IntPtr vdo, int item, out int idxCount, out uint first, out uint baseVert)
    {
        idxCount = 0; first = 0; baseVert = 0;
        VdoRef v = new(vdo);
        long elem = v.ElemItems;
        long vert = v.VertItems;
        if (elem == 0 || vert == 0 || item < 0
            || item >= v.ElemCount || item >= v.VertCount) return false;
        int n = *(int*)(elem + item * 8 + 4);
        if (n <= 0) return false;
        idxCount = n;
        first = (uint)*(int*)(elem + item * 8);
        baseVert = (uint)*(int*)(vert + item * 8);
        return true;
    }

    /// <summary>Headless check of the contracts that fail SILENTLY -- a wrong SSBO binding or record stride
    /// reads a neighbour's matrix and misplaces geometry rather than erroring, and swapping
    /// <c>gl_BaseInstance</c> for <c>gl_DrawID</c> would look correct today and break the moment M4's culling
    /// compacts the command buffer. False = do not arm, per the house rule.</summary>
    private static bool SelfCheck(ExtenderLog log)
    {
        // ...and the cutout stage really discards, at the engine's own threshold. A masked batch drawn by a
        // stage that lost the test writes the whole quad into the shadow map, which is the artefact the
        // masked path exists to remove and looks exactly like the pass working.
        bool ok = CastMaskFs.Contains("if (texture(uTex, vUV).a <= uAlphaCut) discard;")
                  && CastMaskFs.Contains("uniform float uAlphaCut;")
                  && CmdBytes == 20
                  && CastVs.Contains("gl_BaseInstance") && !CastVs.Contains("gl_DrawID")
                  // ...and the masked stage indexes its record the same way, for the same M4 reason
                  && CastMaskVs.Contains("gl_BaseInstance") && !CastMaskVs.Contains("gl_DrawID")
                  // #303: BOTH depth stages read the model matrix out of ALLOY's record, at the ownership
                  // slot baseInstance carries -- no shadow-side record block exists at all. A stage that went back to a matrix of its own would bake a world one data
                  // plane out of step with the one being drawn, and nothing would say so.
                  && CastVs.Contains("SowxDraw d = uDraw[gl_BaseInstance];")
                  && CastMaskVs.Contains("SowxDraw d = uDraw[gl_BaseInstance];")
                  && !CastVs.Contains("c.model") && !CastMaskVs.Contains("c.model")
                  && AlloyRenderer.DrawBufDecl.Contains($"binding = {BindDraw}")
                  // #96 through #302: the bone base is the RECORD's, at the colour path's binding, and the
                  // stage must still treat a negative base as rigid and guard every weighted lane.
                  && AlloyRenderer.BoneVs.Contains($"binding = {BindBones}")
                  && CastVs.Contains("int bb = SowxBoneBase(d);")
                  && CastVs.Contains("if (bb >= 0)") && AlloyRenderer.BoneVs.Contains("aBoneWeight.w > 0.0")
                  && AlloyRenderer.BoneCount == 64
                  // #119's standing constraint: the rigid arm is the prepass's clip expression verbatim,
                  // under the invariant. A caster whose depth disagrees with the colour pass's by a bit is
                  // acne no reviewer would trace to a re-spelled multiply.
                  && ShaderContract.DepthSafe(CastVs) && ShaderContract.DepthSafe(CastMaskVs)
                  && ShdCastVs.Length <= ShaderResource.ResRefBytes
                  && ShdCastFs.Length <= ShaderResource.ResRefBytes
                  // #161: the embedded caster stages satisfy the override guards they are checked against --
                  // a guard its own default trips rebuilds from that same default every session, which reads
                  // as the override silently not applying.
                  && CastVsSafe(CastVs) && CastFsSafe(CastFs)
                  // ...and the guards REFUSE each failure they exist for: a record short one vec4 (every
                  // caster bakes at its neighbour's matrix), a vertex stage that lost #96's skinning (every
                  // creature casts its bind pose), a fragment stage that discards (no shadow at all) or
                  // writes gl_FragDepth (acne). All four link cleanly and none says anything.
                  && !CastVsSafe(CastVs.Replace("uDraw[gl_BaseInstance]", "uDraw[0]"))
                  && !CastVsSafe(CastVs.Replace("uBone", "uNothing"))
                  && !CastFsSafe("#version 460 core\nvoid main() { discard; }")
                  && !CastFsSafe("#version 460 core\nvoid main() { gl_FragDepth = 0.5; }")
                  // the receiver's uSunVp array must be exactly as long as the cascade ceiling: a shorter one
                  // reads out of bounds on the outermost cascade, a longer one silently never fills
                  && AlloyLights.SunShadowLib.Contains($"uSunVp[{MaxCascades}]")
                  // same pin on the cube side: uLocalLight is indexed by a slot the CPU hands out, so a
                  // shorter array reads out of bounds the moment the count passes it
                  && AlloyLights.LocalShadowLib.Contains($"uLocalLight[{MaxCubes}]")
                  // #305: the dynamic half. The page array is slot-indexed like uLocalLight; the compose is
                  // min() (depth is a min-of-blockers -- any other operator double-counts or hides one
                  // array); and the page guard reads "none" from the ZERO a program that never sets the
                  // uniform leaves behind, so a stale content override degrades to static-only.
                  && AlloyLights.LocalShadowLib.Contains($"uLocalDynPage[{MaxCubes}]")
                  && AlloyLights.LocalShadowLib.Contains("float v = min(vs, vd);")
                  && AlloyLights.LocalShadowLib.Contains("return min(vs * 0.2, vd);")
                  && AlloyLights.LocalShadowLib.Contains("pg > 0.5")
                  // #306: the sun's half of the split, in the cube's exact shape -- one min() compose, a
                  // per-layer freshness flag that reads "static only" off the zero an unset uniform leaves.
                  && AlloyLights.SunShadowLib.Contains("uniform sampler2DArrayShadow uSunDynShadow;")
                  && AlloyLights.SunShadowLib.Contains("if (uSunDyn[i] > 0.5)")
                  && AlloyLights.SunShadowLib.Contains("return min(vs, vd);")
                  // #307: the layered stages. The pack is pinned on BOTH sides (the dispatch writes
                  // slot | (view << shift), the stage unpacks with the same shift and its mask), the layer
                  // routing exists in both stages, the skinning branch survives, and the clip expression is
                  // still #119's under the invariant. Any of these drifting is a caster baked into the wrong
                  // layer or at the wrong matrix, with nothing in the log.
                  && CastLayerVs.Contains("#extension GL_ARB_shader_viewport_layer_array : require")
                  && CastLayerMaskVs.Contains("#extension GL_ARB_shader_viewport_layer_array : require")
                  && CastLayerVs.Contains("gl_Layer = int(uViewTbl[vi].c.w);")
                  && CastLayerMaskVs.Contains("gl_Layer = int(uViewTbl[vi].c.w);")
                  && CastLayerVs.Contains($"int vi = gl_BaseInstance >> {CasterGather.ViewShift};")
                  && CastLayerMaskVs.Contains($"int vi = gl_BaseInstance >> {CasterGather.ViewShift};")
                  && CastLayerVs.Contains("uDraw[gl_BaseInstance & 0x3ffff]")
                  && CastLayerMaskVs.Contains("uDraw[gl_BaseInstance & 0x3ffff]")
                  && (1 << CasterGather.ViewShift) - 1 == 0x3ffff
                  && (1 << CasterGather.ViewShift) >= AlloyRenderer.MaxOwned
                  && MaxBakeViews <= 1 << (30 - CasterGather.ViewShift)
                  && CastLayerVs.Contains($"binding = {CasterGather.BindView}")
                  && CastLayerMaskVs.Contains($"binding = {CasterGather.BindView}")
                  && CastLayerVs.Contains("int bb = SowxBoneBase(d);")
                  && ShaderContract.DepthSafe(CastLayerVs) && ShaderContract.DepthSafe(CastLayerMaskVs)
                  && !CastLayerVs.Contains("discard")
                  && CasterGather.LayerSafe()
                  // #303: the gather dispatch culls the CPU reference's tests over the SHARED SowxCullSphere
                  // buffer, conservative in the direction that costs a draw -- a cull that rejects a visible
                  // caster deletes a shadow; one that admits an invisible one costs a draw. GLSL cannot be
                  // run headless, so what is pinned is that every term is present and spelled from these same
                  // constants -- the cone slope is interpolated from FaceConeSlope rather than typed twice,
                  // and the NeverCull exemption gates the reach tests and the LOD skip alike.
                  && CasterGather.SourceSafe();
        // #241: the re-bake predicate, each of its four invalidation sources verified RED on its own -- a
        // term silently dropped from ViewStale is a shadow that stops tracking, which reads as correct until
        // something moves.
        if (ok)
        {   // #282: the sun's staleness volume against the sun's CULL volume. The invariant is one-directional
            // -- nothing the cull would ADMIT may test as outside the fit, or a mover the cascade draws never
            // dirties it and its shadow freezes mid-walk. Up-light of the fit is the case a sphere test gets
            // wrong, so it is probed explicitly.
            Vector3 d = Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f));
            bool fit = InFit(0f, 0f, 0f, 0f, d, 10f, 20f)                        // at the focus
                       && InFit(d.X * 19f, d.Y * 19f, d.Z * 19f, 0f, d, 10f, 20f)  // up-light, inside the extent
                       && InFit(-d.X * 19f, -d.Y * 19f, -d.Z * 19f, 0f, d, 10f, 20f)
                       && !InFit(d.X * 30f, d.Y * 30f, d.Z * 30f, 0f, d, 10f, 20f) // ...and past it
                       && InFit(d.X * 30f, d.Y * 30f, d.Z * 30f, 11f, d, 10f, 20f) // ...unless its radius reaches
                       && !InFit(0f, 40f, 0f, 0f, d, 10f, 20f);                    // off the axis entirely
            if (!fit)
            { ok = false; log.Info("CasterPass.SelfCheck FAIL: the sun's staleness fit rejects a caster its cull admits (frozen cascades)"); }
        }
        if (ok && !(ViewStale(true, false, false, false) && ViewStale(false, true, false, false)
                    && ViewStale(false, false, true, false) && ViewStale(false, false, false, true)
                    && !ViewStale(false, false, false, false)))
        { ok = false; log.Info("CasterPass.SelfCheck FAIL: the view re-bake predicate lost an invalidation source"); }
        if (ok)
        {   // #302: the vanish detector, which is the ONLY thing standing between a caster that stops casting
            // and the views it stood in keeping its shadow forever. It is a frame STAMP compared against a
            // previous-frame list -- no hash, no bucket, no eviction -- so what it has to get right is that a
            // slot present in both frames does not ghost and a slot present in only the first one does.
            _frameSeq = 7;
            _prevSlots[0] = 3; _prevSlots[1] = 9; _prevCount = 2;
            _liveStamp[3] = 7; _liveStamp[9] = 6;      // slot 3 collected this frame, slot 9 not
            int ghosts = 0;
            for (int q = 0; q < _prevCount; q++) if (_liveStamp[_prevSlots[q]] != _frameSeq) ghosts++;
            bool sweep = ghosts == 1;
            _frameSeq = 0; _prevCount = 0; _liveStamp[3] = 0; _liveStamp[9] = 0;
            if (!sweep)
            { ok = false; log.Info("CasterPass.SelfCheck FAIL: the vanish sweep misses a caster that stopped casting (frozen shadows)"); }
        }
        if (ok)
        {   // #286's staging, against what the dispatch and the draw stages read back out of it. Not a GL
            // call in sight: this is a memory layout shared with GLSL, and a field one slot over is a caster
            // baking at its neighbour's geometry.
            // #303: the source is an index range, the OWNERSHIP slot (word 3 -- the sphere the dispatch
            // culls and the baseInstance the depth stages read uDraw through) and the batch pair. A slot off
            // by one is a caster baking at another part's matrix, with nothing in the log.
            _liveCreatures = 1; _liveCount = 2;
            for (int i = 0; i < 2; i++)
            {
                _liveIdxCount[i] = 30 + i; _liveFirst[i] = (uint)(100 + i); _liveBaseVert[i] = (uint)(7 + i);
                _liveCut[i] = 0.2f; _liveSlot[i] = 4100 + i;
                _liveDyn[i] = i == 1;   // #305: the set flag, one of each, so a swap fails
            }
            uint* src = stackalloc uint[2 * CasterGather.SrcUints];
            StageOne(src, 0, 0, 0, 0);
            StageOne(src, 1, 1, 1, 1);
            bool pack = src[0] == 30 && src[1] == 100 && src[2] == 7 && src[3] == 4100 && src[4] == 0
                        && src[5] == 0 && src[6] == 0
                        && src[CasterGather.SrcUints] == 31
                        && src[CasterGather.SrcUints + 3] == 4101 && src[CasterGather.SrcUints + 4] == 1
                        && src[CasterGather.SrcUints + 5] == 1 && src[CasterGather.SrcUints + 6] == 1
                        && CasterGather.SrcUints == 7 && CasterGather.CmdBytes == 20;
            _liveCreatures = 0; _liveCount = 0; _liveDyn[0] = false; _liveDyn[1] = false;
            if (!pack)
            { ok = false; log.Info("CasterPass.SelfCheck FAIL: the compute gather's staging drifted from the source the dispatch reads"); }
            // The rotation's write-after-read argument holds only above a floor of 2: at 1 no barrier stands
            // between a view's indirect reads and the next view's dispatch rewriting the same pair.
            if (CasterGather.Rotation < 2)
            { ok = false; log.Info("CasterPass.SelfCheck FAIL: the gather's buffer rotation is below 2, so a view's commands can be rewritten while its draws still source them"); }
        }
        // #100's face cull, brute-forced over the +X face (forward = +X, so t = dx and the pyramid is
        // |dy| <= dx, |dz| <= dx). The invariant that matters is one-directional: NOTHING inside the pyramid
        // may be culled. Admitting a little extra is the deliberate cost of a rotation-invariant test.
        for (int a = 1; a <= 12 && ok; a++)
            for (int b = -12; b <= 12 && ok; b++)
                for (int c = -12; c <= 12 && ok; c++)
                {
                    float dx = a, dy = b * a / 12f, dz = c * a / 12f;   // strictly inside: |dy|,|dz| <= dx
                    if (!FaceCulls(dx * dx + dy * dy + dz * dz, dx, 0f)) continue;
                    ok = false;
                    log.Info($"CasterPass.SelfCheck FAIL: face cull rejects ({dx},{dy},{dz}), inside the 90deg face");
                }
        // and it must still reject the obvious misses, else it is culling nothing at all
        if (ok && (!FaceCulls(100f, -10f, 0f) || !FaceCulls(101f, 1f, 0f)))
        { ok = false; log.Info("CasterPass.SelfCheck FAIL: face cull admits geometry behind or beside the face"); }

        log.Info(ok
            ? "CasterPass.SelfCheck: passed"
            : $"CasterPass.SelfCheck: FAILED -- indirect/per-draw contract drifted (command {CmdBytes}B, " +
              "gl_BaseInstance-as-ownership-slot indexing); standing down");
        return ok;
    }

    /// <summary>The world-caster bake, driven ahead of the colour pass that samples it. Since #118 it runs
    /// at the FadeDoors anchor, BEFORE the colour build -- which is where #302's one-frame record lag comes
    /// from, and why <c>Enroll</c> (the RSP prologue, earlier still) must never move a tenancy stamp for a
    /// slot whose part did not change. Everything below runs inside the #80 bracket.</summary>
    public static void OnPassSlot()
    {
        if (!_armed || _disarmed) return;
        _frameSeq++;   // before the early-outs: a frame that renders nothing must READ as stale, not as fresh
        // The overlay's view readout describes THIS frame, so it has to be zeroed ahead of every early-out
        // below -- a frozen count left over from the last frame that rendered reads as a pass still baking.
        LastViews = 0;
        // #241, ahead of EVERY early-out below: a frame that does not collect leaves the memo's stamps a
        // frame behind, so the sweep would miss whatever vanished while it was not looking. Any stretch of
        // non-Alloy frames proves it -- the main menu and back is enough, and the first Alloy frame after
        // would otherwise republish the shadows the world had before the walk. #282: the flag now simply
        // survives, because only a complete frame below clears it.
        Lifecycle();
        // AFTER the period line, which reports it: zeroing the timing ahead of Lifecycle made every period
        // line read 0.00 ms, since the frame's own measurement lands further down.
        LastShadowMs = 0f;
        if (!AlloyGate.Alloy || !GameGate.InModule || !GlContextService.Available) return;
        // #302: no enrollment of its own. The population is Alloy's ownership table, scanned on Alloy's
        // cadence -- there is one scan now (#304 deleted the ShadowEnroll zone that separated the two).
        // An empty table is not "nothing to do": whatever was casting last frame has stopped, and leaving
        // the recorded views alone would freeze its shadows in place. Collect's own sweep is what says so,
        // so the empty case runs the collect and then has no views to fill.
        if (AlloyRenderer.OrderCount == 0 && _prevCount == 0) return;

        // Collect FIRST and unconditionally: resolving the world is a per-frame fact, not a property of any
        // one view, and #94/#95 submit several views off the one collect.
        Collect();

        // The sun is the only view with an exterior precondition. The cubes light interiors -- which is where
        // the AC lives (torchlight occluded by walls) -- so they must NOT sit behind this gate.
        bool sun = FrameInputs.HasSun && FrameInputs.HasCamera && !FrameInputs.Interior;
        if (!sun && !_noSunLogged)
        {   // interiors have no sun: a normal state, not a fault. HasSun latches for the SESSION, so the
            // interior test is what stops an interior sampling the last exterior's cascades -- the stamp then
            // goes stale and the receiver drops the sun shadow term.
            _noSunLogged = true;
            _log?.Info("CasterPass: no sun captured yet or interior area -- the sun cascades idle, cubes carry on");
        }
        // The published shadow focus (the camera-follow target, i.e. the PC), falling back to the camera
        // itself. NOT ShadowMath.CameraGroundFocus: marching the camera forward needs the SCENE camera's
        // rotation, and the captured rotation is whatever the last SetViewTransform left -- observed as a
        // UI/ortho pass, whose forward marched the focus to (0, 0, 766) and put every caster off-axis. That
        // is the 100%-cull the live log showed. Whether the #118 pass slot changes that is unverified; the
        // published focus is right either way, so nothing here depends on the answer.
        // ponytail: Focus is published by SceneInputs, which the #98 cutover KEPT -- it renders nothing,
        // it is the feed this pass reads. The camera fallback stands if that walk ever moves in here.
        Vector3 focus = FrameInputs.HasFocus ? FrameInputs.Focus : FrameInputs.CameraPos;

        if (!GlContextService.Enter()) return;
        // #260: the bake and the colour pass read the SAME buffer -- the pool's, which Alloy uploaded at the
        // engine's own commit -- so there is nothing to bring into agreement here any more. What closes is
        // the dirty window, and this bracket runs after the sweep has already read _vDirty off it.
        OwnedBuffers.ClearDirty();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _qShadowOn = _qShadow.Begin(Gl.TIME_ELAPSED);
        // #286: ONE staging for the whole frame, after the bones (the skin drop is a per-frame fact) and
        // inside the bracket (the mask resolve issues Alloy's texture copy). Every view below is then a
        // uniform block and a dispatch. Stand it down first: a frame that does not stage must not leave the
        // last frame's records armed, which would bake a world that has since moved.
        CasterGather.Stand();
        _gStaged = false;
        _qN = 0;
        if (sun || (_cubeLights > 0 && FrameInputs.LocalLightCount > 0))
        {
            if (EnsureCommon() && CasterGather.Ensure()) { EnsureLayered(); _gStaged = GatherStage(); }
            // #303: the compute dispatch is the ONLY selection path. A program that can never build would
            // otherwise idle here forever with the engine's stencil shadows already suppressed.
            else if (CasterGather.Failed) Disarm("the caster gather dispatch failed to build");
        }
        // #282: the blanket invalidate is read by BOTH passes and cleared by the cube scan, so it is snapshot
        // once here -- FrameGraph decides the order of the two, and whichever ran second would otherwise miss it.
        _frameInvalidate = _invalidate || !_rebake;
        _sunRan = _cubeRan = false;
        // #117: the shadow bracket's passes come off FrameGraph, not from the order these two lines sit in.
        foreach ((PassId id, Bracket bracket) in FrameGraph.Passes)
        {
            if (bracket != Bracket.Shadow) continue;
            switch (id)
            {
                case PassId.SunCascades:
                    if (sun && EnsureSun()) SunPass(focus);
                    break;
                case PassId.LocalLightCubes:
                    if (_cubeLights > 0 && FrameInputs.LocalLightCount > 0 && EnsureCubes()) CubePass(focus);
                    break;
            }
        }
        // #307: the queued stale views flush in one layered pass per target, HERE -- the publish arrays the
        // passes wrote above assumed the bakes land. A failed flush publishes nothing for the frame instead:
        // the receiver samples no maps (a one-frame blink, never a lie) and the blanket re-bakes from scratch.
        if (!FlushLayered())
        {
            _layerFlushFails++;
            CascadeCount = 0; CubeCount = 0;
            Array.Clear(SunDynMask); Array.Clear(CubeDynPage);
            _invalidate = true; _sunScanned = false; _cubeScanned = false;
        }
        // ...and only now may the frame's invalidation be consumed: every pass that ran reached every one of
        // its views, so each of them recorded this frame's movers against its own slots.
        if ((_sunRan || _cubeRan) && (!_sunRan || _sunScanned) && (!_cubeRan || _cubeScanned))
            _invalidate = false;
        // #303: the culled-vs-drawn sample -- a latent copy of the last view's survivor counts, read back
        // frames later, so the figure reaches the period line without a readback on the draw path.
        CasterGather.Sample();
        if (_qShadowOn) { _qShadow.End(Gl.TIME_ELAPSED); _qShadowOn = false; }
        LastShadowMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000f / System.Diagnostics.Stopwatch.Frequency;
        _viewSum += LastViews;
        GlContextService.Leave();
    }

    /// <summary>The sun cascades: one concentric fit per layer around the shadow focus, every one of them
    /// baked from the live fit every frame (#187). Cascades are rendered innermost first so a fence stall
    /// costs the FAR ones, whose absence is a missing distant shadow rather than a missing one at the
    /// player's feet.</summary>
    private static void SunPass(Vector3 focus)
    {
        Vector3 dir = Vector3.Normalize(FrameInputs.SunDir);
        int done = 0;
        bool invalidate = _frameInvalidate;
        _sunRan = true; _sunScanned = true;
        // #306: the split runs only when its array exists -- csm_dyn 0 or a refused allocation is the
        // combined bake everywhere, exactly as /shadow split 0 spells it.
        bool split = _split && SunDynTex != 0;
        // The degrade window ticks once per sun frame. A layer whose static half re-baked on >= 90% of the
        // window's frames is paying two bakes where one combined bake would do; it degrades to today's
        // behaviour until the rate drops back under half (walking stops), and the period line says so.
        if (split && ++_sunWinFrames >= SunWin)
        {
            for (int c = 0; c < _cascades; c++)
                _sunCombined[c] = _sunWinBaked[c] >= (_sunCombined[c] ? SunWin / 2 : SunWin * 9 / 10);
            Array.Clear(_sunWinBaked);
            _sunWinFrames = 0;
        }
        for (int i = 0; i < _cascades; i++)
        {
            SunDynMask[i] = 0f;
            float radius = ShadowMath.CascadeRadius(i, _cascades, _maxDist);
            float margin = FitMargin();
            Matrix4x4 vp = ShadowMath.DirectionalLightViewProj(dir, focus, radius, margin, _mapSize);
            bool combined = !split || _sunCombined[i];
            // #282 terms 1+2: the fit itself. Combined, the recorded layer is a function of the raw five
            // numbers, exactly as before the split. Under the split the static key is the QUANTISED fit
            // matrix -- ShadowMath snaps every component to the texel grid, so a sub-texel focus move
            // reproduces it bit-identical and the layer is reusable while the player walks within a texel.
            // A population flip (SetAll <-> SetStatic) re-keys the layer either way.
            bool keyChanged = !_sunBaked[i] || _sunKeyCombined[i] != combined
                              || (combined
                                  ? _sunKeyFocus[i * 3] != focus.X || _sunKeyFocus[i * 3 + 1] != focus.Y
                                    || _sunKeyFocus[i * 3 + 2] != focus.Z
                                    || _sunKeyDir[i * 3] != dir.X || _sunKeyDir[i * 3 + 1] != dir.Y
                                    || _sunKeyDir[i * 3 + 2] != dir.Z
                                    || _sunKeyFit[i * 2] != radius || _sunKeyFit[i * 2 + 1] != margin
                                  : vp != _sunKeyVp[i]);
            // #282 term 3: a mover inside the fit. NOT a sphere around the focus -- a cascade admits a
            // CYLINDER along the light axis, and a caster standing up-light of the fit shades into it. Same
            // split the walk's own directional cull makes (perpendicular distance vs distance along), so the
            // predicate can only be stale about geometry the view would have culled anyway.
            // #306: under the split, continuous motion lives in the dynamic layer and must not re-bake the
            // static one -- only STATIC-SET EVENTS (appearances, membership flips) and ghosts invalidate it.
            bool hasMover = false;
            int[] evtIdx = combined ? _moverIdx : _sMoverIdx;
            int evtN = combined ? _moverCount : _sMoverCount;
            for (int q = 0; q < evtN && !hasMover; q++)
            {
                int m = evtIdx[q];
                hasMover = InFit(_liveCx[m] - focus.X, _liveCy[m] - focus.Y, _liveCz[m] - focus.Z,
                                 _liveR[m], dir, radius, radius + margin);
            }
            for (int q = 0; q < _ghostCount && !hasMover; q++)
                hasMover = InFit(_ghostCx[q] - focus.X, _ghostCy[q] - focus.Y, _ghostCz[q] - focus.Z,
                                 _ghostR[q], dir, radius, radius + margin);
            bool stale = ViewStale(invalidate, keyChanged, _sunHadMover[i], hasMover);
            _sunHadMover[i] = hasMover;
            if (!stale)
            {   // The layer still describes this world. Nothing to republish: the key is unchanged, so the
                // matrix and the two ranges below are recomputed bit-identical to the ones already published.
                _sunSkippedViews++;
                done++;
            }
            else
            {
                _sunBakedViews++;
                if (split) { _sunWinBaked[i]++; _sunStaticL[i]++; }
                ChargeBake(WhySun, invalidate, keyChanged);
                bool viewOk = RenderView(_depthTex, _mapSize, i, &vp,
                                         dir, focus, radius, margin, point: false,
                                         set: combined ? CasterGather.SetAll : CasterGather.SetStatic);
                // A cascade that stalled holds last frame's depth under this frame's key, and the cascades
                // after it were never scanned -- so their mover evidence is missing too. One blanket frame
                // covers both.
                if (!viewOk) { _sunBaked[i] = false; _sunScanned = false; break; }
                _sunBaked[i] = true;
                _sunKeyCombined[i] = combined;
                _sunKeyVp[i] = vp;
                _sunKeyFocus[i * 3] = focus.X; _sunKeyFocus[i * 3 + 1] = focus.Y; _sunKeyFocus[i * 3 + 2] = focus.Z;
                _sunKeyDir[i * 3] = dir.X; _sunKeyDir[i * 3 + 1] = dir.Y; _sunKeyDir[i * 3 + 2] = dir.Z;
                _sunKeyFit[i * 2] = radius; _sunKeyFit[i * 2 + 1] = margin;
                fixed (float* pub = CascadeVp) *(Matrix4x4*)(pub + i * 16) = vp;
                CascadeTexel[i] = 2f * radius / _mapSize;
                CascadeRange[i] = 2f * radius + margin;
                done++;
            }
            // #306: the dynamic layer bakes EVERY frame it has movers -- no predicate, its whole content is
            // the movers -- with the SAME fit matrix the published static layer carries (on a skip the
            // recompute above is bit-identical to it, courtesy of the snap), so the receiver's uv/layer/ref
            // carry over unchanged and min() composes the two exactly. No movers in the fit = mask 0, and
            // the receiver never samples the stale layer; a stalled dyn bake leaves the mask 0 too, which is
            // a mover shadow that blinks for a frame, never one that lies.
            if (!combined)
            {
                bool hasDyn = false;
                for (int q = 0; q < _dMoverCount && !hasDyn; q++)
                {
                    int m = _dMoverIdx[q];
                    hasDyn = InFit(_liveCx[m] - focus.X, _liveCy[m] - focus.Y, _liveCz[m] - focus.Z,
                                   _liveR[m], dir, radius, radius + margin);
                }
                if (hasDyn && _sunBaked[i]
                    && RenderView(SunDynTex, _mapSize, i, &vp, dir, focus, radius, margin, point: false,
                                  set: CasterGather.SetDynamic))
                { SunDynMask[i] = 1f; _sunDynBakedViews++; }
            }
        }
        CascadeTex = _depthTex;
        // CascadeCount is the ONLY thing bounding the receiver's read: a stall at cascade i leaves layers
        // >= i holding last frame's depth and CascadeVp[>= i] holding last frame's matrix, and the
        // receiver's loop simply never reaches them. Same invariant covers the uniform upload, which
        // sends only `done` matrices into a mat4[4] and leaves the tail stale on purpose.
        CascadeCount = done;
        if (done > 0) _cascadeStamp = _frameSeq;
    }

    /// <summary>The local-light cubes: six faces per light, budget spent nearest the shadow focus first.
    /// <para>#241: a slot's six faces are baked only when the predicate says its recorded depth no longer
    /// describes the world -- the light moved or changed reach, the slot changed hands, the caster set
    /// changed, or a mover is inside its radius (or was, last frame). Everything else republishes the slot
    /// untouched. This is INVALIDATION, not the composite cache #187 measured and rejected: a stale slot
    /// bakes exactly as it always did, and a fresh one issues no GL at all. #187's own post-mortem is the
    /// reason -- its per-view server-side depth copy stalled where a bake pipelines, and a skip does
    /// neither.</para>
    /// <para>A light is published only once all six of its faces landed. Partial cascades are safe because
    /// selection is innermost-first and a missing outer layer is simply never reached; a cube has no such
    /// ordering, so a light that lost one face to a ring stall would sample five fresh faces and one holding
    /// last frame's depth. Dropping it for the frame turns that into a shadow that blinks, which is
    /// invisible, instead of one that lies.</para></summary>
    private static void CubePass(Vector3 focus)
    {
        int n = FrameInputs.LocalLightCount;
        if (n > _cubeLights) n = _cubeLights;
        // Nearest-first by camera distance: the budget is spent on the lights the player is standing in, and
        // a carried torch is by construction among them. Insertion sort, n <= MaxCubes, alloc-free.
        int* order = stackalloc int[MaxCubes];
        float* dist = stackalloc float[MaxCubes];
        int have = 0;
        for (int i = 0; i < FrameInputs.LocalLightCount; i++)
        {
            // Rank by distance to the shadow FOCUS (the PC), not the camera. Historically the camera was
            // whatever the last SetViewTransform left -- live-observed as (0, 0, 742), a UI/ortho pass --
            // and sorting by distance to a point 742u overhead ranks lights by ALTITUDE: which lights got a
            // cube was near-arbitrary and reshuffled as the light set shifted, so a carried torch's shadow
            // blinked in and out as the player walked. #257 made the camera a direct read and that hazard
            // is gone, but the focus is still the right anchor -- it is the PC, which is what the shadows
            // are for, and the sun fit takes the same point.
            float d = (FrameInputs.LocalLightPos[i] - focus).LengthSquared();
            if (have == n && (n == 0 || d >= dist[n - 1])) continue;
            int k = have < n ? have : n - 1;
            while (k > 0 && dist[k - 1] > d) { order[k] = order[k - 1]; dist[k] = dist[k - 1]; k--; }
            order[k] = i; dist[k] = d;
            if (have < n) have++;
        }

        // #241: STICKY slots. Slot used to be rank in the nearest-first sort, so a walking player reshuffled
        // every light's slot every frame and the predicate's slot term would have fired on all of them -- the
        // exact re-bake storm #151 measured under rank keys. A light keeps the slot it had; newcomers take
        // what is free. The receiver looks slots up by light id (CubeSlotOf), so which number it is never
        // mattered to it -- only that it stops moving.
        int* pick = stackalloc int[MaxCubes];
        bool* seated = stackalloc bool[MaxCubes];
        for (int j = 0; j < _cubeLights; j++) { pick[j] = -1; seated[j] = false; }
        for (int s2 = 0; s2 < have; s2++)
        {
            IntPtr id = FrameInputs.LocalLightId[order[s2]];
            if (id == IntPtr.Zero) continue;
            for (int j = 0; j < _cubeLights; j++)
                if (_cubeKeyId[j] == id && pick[j] < 0) { pick[j] = s2; seated[s2] = true; break; }
        }
        for (int s2 = 0, j = 0; s2 < have; s2++)
        {
            if (seated[s2]) continue;
            while (j < _cubeLights && pick[j] >= 0) j++;
            if (j >= _cubeLights) break;
            pick[j++] = s2;
        }
        // ...and the walk itself stays in RANK order, which is what the ring-stall break below depends on.
        // Iterating by slot index would, under sticky seating, drop an ARBITRARY set of lights on a stall --
        // and the same low slots every frame, so the player's own torch could sit in slot 27 and lose its
        // shadow indefinitely rather than for one frame.
        int* slotOf = stackalloc int[MaxCubes];
        for (int s2 = 0; s2 < have; s2++) slotOf[s2] = -1;
        for (int j = 0; j < _cubeLights; j++)
        {
            if (pick[j] >= 0) { slotOf[pick[j]] = j; continue; }
            // the slot lost its light: whatever depth it holds describes a light no longer in the budget,
            // and the next light to take it must not inherit it -- nor its dynamic page or mode
            _cubeBaked[j] = false; _cubeKeyId[j] = IntPtr.Zero; _cubeHadMover[j] = false;
            _cubeCombined[j] = false;
            if (_dynPageOf[j] >= 0) { _dynPageSlot[_dynPageOf[j]] = -1; _dynPageOf[j] = -1; }
        }

        // A slot nobody claims is published INACTIVE (far = 0, id = 0) rather than compacted away: the
        // receiver reads far <= near as "lit", so holes in the slot array are free.
        for (int j = 0; j < _cubeLights; j++)
        { CubeLight[j * 4 + 3] = 0f; CubeLightId[j] = IntPtr.Zero; CubeDynPage[j] = 0f; }

        int done = 0, frameCreatures = 0, nearestCreatures = -1;
        // #241: the transition below is only meaningful about a slot this frame BAKED -- a skipped nearest
        // light stages nothing and would otherwise read as the creature shadow vanishing every still frame.
        bool nearestBaked = false;
        bool invalidate = _frameInvalidate;
        bool scannedAll = true;
        // #305: the split runs only when its pool exists -- cube_dyn_count 0 or a refused allocation is the
        // combined bake everywhere, exactly as /shadow split 0 spells it.
        bool split = _split && _cubeDynPages > 0 && CubeDynTex != 0;
        for (int rank = 0; rank < have; rank++)
        {
            int slot = slotOf[rank];
            if (slot < 0) continue;   // more lights than slots: the tail of the ranking gets no cube
            int li = order[rank];
            int lightCreatures = 0;
            Vector3 pos = FrameInputs.LocalLightPos[li];
            float far = FrameInputs.LocalLightRange[li];
            IntPtr id = FrameInputs.LocalLightId[li];
            if (!(far > CubeNear)) { _cubeBaked[slot] = false; continue; }   // no reach, nothing worth sampling

            // #241 term 1+2: the light itself. Position, reach and identity are the three things the recorded
            // faces are a function of, so any of them differing is a bake -- as is a slot holding no complete
            // bake at all (a fence timeout, a release, a fresh array).
            bool slotChanged = !_cubeBaked[slot] || _cubeKeyId[slot] != id || _cubeKeyFar[slot] != far
                               || _cubeKeyPos[slot * 3] != pos.X || _cubeKeyPos[slot * 3 + 1] != pos.Y
                               || _cubeKeyPos[slot * 3 + 2] != pos.Z;
            // #305: is a DYNAMIC caster inside this light's reach? That is what claims (and keeps) a pool
            // page -- and, when the pool is dry, what forces the combined fallback below.
            bool hasDyn = false;
            if (split)
                for (int q = 0; q < _dMoverCount && !hasDyn; q++)
                {
                    int m = _dMoverIdx[q];
                    float mx = _liveCx[m] - pos.X, my = _liveCy[m] - pos.Y, mz = _liveCz[m] - pos.Z;
                    float mr = far + _liveR[m];
                    hasDyn = mx * mx + my * my + mz * mz <= mr * mr;
                }
            int page = _dynPageOf[slot];
            if (split && hasDyn && page < 0)
            {
                for (int p = 0; p < _cubeDynPages && page < 0; p++)
                    if (_dynPageSlot[p] < 0) { _dynPageSlot[p] = slot; _dynPageOf[slot] = page = p; }
            }
            else if (page >= 0 && (!split || !hasDyn))
            {   // no movers left near this light: the page returns to the pool and the receiver stops
                // sampling it (the publish wipe already left CubeDynPage[slot] at 0)
                _dynPageSlot[page] = -1; _dynPageOf[slot] = -1; page = -1;
            }
            // Pool exhausted: this slot bakes BOTH halves into the static array every mover frame -- the
            // per-slot fallback to pre-split behaviour. A mode change re-keys the slot, or a mover baked in
            // while combined would outlive the mode.
            bool combined = !split || (hasDyn && page < 0);
            if (_cubeCombined[slot] != combined) { slotChanged = true; _cubeCombined[slot] = combined; }

            // #241 term 3: a mover inside this light's reach -- under the split (#305), a STATIC-SET EVENT:
            // continuous motion lives in the dynamic page and must not re-bake the room. Scanned EVERY frame
            // whatever the verdict, because _cubeHadMover has to be current for the frame the mover leaves.
            bool hasMover = false;
            int[] evtIdx = combined ? _moverIdx : _sMoverIdx;
            int evtN = combined ? _moverCount : _sMoverCount;
            for (int q = 0; q < evtN && !hasMover; q++)
            {
                int m = evtIdx[q];
                float mx = _liveCx[m] - pos.X, my = _liveCy[m] - pos.Y, mz = _liveCz[m] - pos.Z;
                float mr = far + _liveR[m];
                hasMover = mx * mx + my * my + mz * mz <= mr * mr;
            }
            for (int q = 0; q < _ghostCount && !hasMover; q++)
            {
                float mx = _ghostCx[q] - pos.X, my = _ghostCy[q] - pos.Y, mz = _ghostCz[q] - pos.Z;
                float mr = far + _ghostR[q];
                hasMover = mx * mx + my * my + mz * mz <= mr * mr;
            }
            bool stale = ViewStale(invalidate, slotChanged, _cubeHadMover[slot], hasMover);
            _cubeHadMover[slot] = hasMover;

            if (!stale)
            {   // The six layers still describe this world. Republish the slot unchanged -- the receiver
                // reads the light's geometry out of CubeLight, and it must keep matching the depth it samples.
                _cubeSkippedSlots++;
                CubeLight[slot * 4] = _cubeKeyPos[slot * 3]; CubeLight[slot * 4 + 1] = _cubeKeyPos[slot * 3 + 1];
                CubeLight[slot * 4 + 2] = _cubeKeyPos[slot * 3 + 2]; CubeLight[slot * 4 + 3] = _cubeKeyFar[slot];
                CubeLightId[slot] = id;
                done++;
            }
            else
            {
                _cubeBakedSlots++;
                ChargeBake(WhyCube, invalidate, slotChanged);
                bool whole = true;
                for (int f = 0; f < CubeFaces && whole; f++)
                {
                    Vector3 fwd = ShadowMath.CubeFaceForward(f);
                    Matrix4x4 vp = ShadowMath.PointLightFaceViewProj(pos, f, CubeNear, far);
                    whole = RenderView(CubeTex, _cubeSize, slot * CubeFaces + f, &vp,
                                       fwd, pos, far, 0f, point: true,
                                       set: combined ? CasterGather.SetAll : CasterGather.SetStatic);
                    lightCreatures += _submitCreatures;
                }
                // A partial bake is not a bake: leaving the slot unbaked is what forces the whole cube again
                // next frame instead of letting five fresh faces and one stale one be published as fresh
                // forever.
                _cubeBaked[slot] = whole;
                // The ring is behind; the lights after this one would stall too. Leaving the scan INCOMPLETE
                // is load-bearing: the slots past this point never ran their mover test, so this frame's
                // evidence must not be consumed on their behalf.
                if (!whole) { scannedAll = false; break; }
                _cubeKeyId[slot] = id; _cubeKeyFar[slot] = far;
                _cubeKeyPos[slot * 3] = pos.X; _cubeKeyPos[slot * 3 + 1] = pos.Y; _cubeKeyPos[slot * 3 + 2] = pos.Z;
                frameCreatures += lightCreatures;
                if (rank == 0) { nearestCreatures = lightCreatures; nearestBaked = true; }
                CubeLight[slot * 4] = pos.X; CubeLight[slot * 4 + 1] = pos.Y; CubeLight[slot * 4 + 2] = pos.Z;
                CubeLight[slot * 4 + 3] = far;
                CubeLightId[slot] = id;
                done++;
            }
            // #305: the dynamic page bakes EVERY frame it exists -- no predicate, because its whole content
            // is the movers. Published only on a complete bake, for the same five-fresh-faces reason; an
            // unpublished page costs the movers' shadow for one frame, never a lie.
            if (page >= 0)
            {
                bool dynWhole = true;
                for (int f = 0; f < CubeFaces && dynWhole; f++)
                {
                    Matrix4x4 vp = ShadowMath.PointLightFaceViewProj(pos, f, CubeNear, far);
                    dynWhole = RenderView(CubeDynTex, _cubeDynSize, page * CubeFaces + f, &vp,
                                          ShadowMath.CubeFaceForward(f), pos, far, 0f, point: true,
                                          set: CasterGather.SetDynamic);
                }
                if (!dynWhole) { scannedAll = false; break; }
                CubeDynPage[slot] = page + 1;
                _cubeDynBakedPages++;
            }
        }
        // Only a scan that reached every slot may consume the frame's invalidation: the break above leaves
        // the slots after it with no record of this frame's movers.
        _cubeScanned = scannedAll; _cubeRan = true;
        CubeCount = done > 0 ? _cubeLights : 0;
        if (done > 0) _cubeStamp = _frameSeq;
        _cubeLightsSum += done;
    }

    /// <summary>The two live numbers #99's fit decision rests on, both off the LATCHED scene camera and never
    /// the present-time capture: the scene's field of view, and the view DEPTH of the shadow focus -- how far
    /// down the frustum the ground at the player's feet actually sits.
    /// <para>That depth is the whole argument. Concentric cascades centre cascade 0 ON the focus, so the
    /// player's feet sit at the middle of a 15u map whatever the camera does. Any frustum-split fit must spend
    /// cascade 0 on the slab nearest the camera -- which for a chase camera is the empty ground BEHIND the PC
    /// -- and the feet land in whichever split holds this depth. #99 measured that trade analytically across
    /// fov 45-65 and focus depths 8-25u: the split fit only wins at depth &lt;= 8u, and loses by 1.5-3x at
    /// every realistic value. This line is what re-opens the question if live values fall outside that range.
    /// Cold -- the period line only.</para></summary>
    private static string FitReadout()
    {
        if (!FrameInputs.HasSceneCam || !FrameInputs.HasFocus || !(FrameInputs.SceneCamP11 > 0f)) return "";
        float depth = Vector3.Dot(FrameInputs.Focus - FrameInputs.SceneCamPos, FrameInputs.SceneCamFwd);
        float fovy = 2f * MathF.Atan(1f / FrameInputs.SceneCamP11) * 180f / MathF.PI;
        return $" (scene cam fov {fovy:F0}deg, focus at view depth {depth:F1}u, concentric cascade 0 " +
               $"half-extent {ShadowMath.CascadeRadius(0, _cascades, _maxDist):F0}u)";
    }

    /// <summary>Module unload, a PartTable wipe and a lost 4.6 context all invalidate every recorded
    /// identity wholesale; the period line is the only evidence the pass runs at all.
    /// <para>#118: this runs from the pass slot, which need not fire outside a module at all, so the InModule
    /// edge below is no longer the load-bearing one. TableGen is: DrawFunnelService ticks from the present
    /// unconditionally and wipes on module unload, so the first tick back inside a module releases.</para></summary>
    private static void Lifecycle()
    {
        if (!GlContextService.Available) { Release("4.6 context lost"); CasterGather.ContextLost(); }
        bool inModule = GameGate.InModule;
        if (_wasInModule && !inModule) Release("module unload");
        _wasInModule = inModule;
        if (_tableGen != PartTable.TableGen)
        { Release("part table wiped"); _tableGen = PartTable.TableGen; }

        int period = _loggedOnce ? LogEvery : FirstLogAt;
        if (++_frames < period) return;
        _loggedOnce = true;
        _log?.Info($"CasterPass: [casters] {CascadeCount}/{_cascades} sun cascades at {_mapSize}px out to " +
                   $"{_maxDist:F0}u" + FitReadout() +
                   $", {(float)_cubeLightsSum / _frames:F1}/{_cubeLights} local cubes at {_cubeSize}px" +
                   $", casting off {AlloyRenderer.OrderCount} Alloy ownership slots" +
                   $", {(float)_cubeBakedSlots / _frames:F1} cube slots re-baked and " +
                   $"{(float)_cubeSkippedSlots / _frames:F1} skipped per frame " +
                   // #305: static and dynamic bakes SEPARATELY -- static bakes tracking the view count means
                   // the split bought nothing and the invalidation is too coarse; this line is that reading.
                   $"(split {(_split ? "on" : "OFF -- /shadow split 1")}: " +
                   $"{(float)_cubeBakedSlots / _frames:F1} static / {(float)_cubeDynBakedPages / _frames:F1} " +
                   $"dynamic cube bakes per frame, dyn pool {DynPagesUsed()}/{_cubeDynPages} pages at " +
                   $"{_cubeDynSize}px = {_cubeDynPages * CubeFaces * (long)_cubeDynSize * _cubeDynSize * 4 / (1024 * 1024)} MB " +
                   $"beside the static array's {_cubeLights * CubeFaces * (long)_cubeSize * _cubeSize * 4 / (1024 * 1024)} MB) " +
                   $"(sun {(float)_sunBakedViews / _frames:F1} re-baked, " +
                   $"{(float)_sunSkippedViews / _frames:F1} skipped" +
                   // #306: the static re-bake rate PER CASCADE LAYER is the texel snap's measured reuse
                   // rate -- the number the toroidal-escalation verdict reads. A layer marked C degraded
                   // itself to the combined bake (static half stale essentially every frame), which is
                   // today's behaviour and not a wrong shadow.
                   (SunDynTex != 0
                    ? $"; static per layer {SunLayerReadout()} /frame, " +
                      $"{(float)_sunDynBakedViews / _frames:F1} dynamic bakes/frame, " +
                      $"degraded-to-combined [{SunCombinedReadout()}]"
                    : "; sun split OFF (csm_dyn 0)") + ") " +
                   // #307: which submission path the views took, and what it cost.
                   (_layerDial
                    ? _layerProbe > 0 && _progLayer != 0
                      ? $"(layered bake: {(float)CasterGather.LayerDispatches / _frames:F1} dispatches over " +
                        $"{(float)CasterGather.LayerViews / _frames:F1} views/frame" +
                        (_layerFlushFails > 0 ? $", {_layerFlushFails} flush FAILURES" : "") + ") "
                      : _layerProbe < 0
                        ? "(layered bake UNAVAILABLE -- no ARB_shader_viewport_layer_array, per-view path) "
                        : ""
                    : "(layered bake OFF -- /shadow layered 1) ") +
                   $"(bakes charged to -- sun: blanket {_bakeWhy[WhySun]}, key {_bakeWhy[WhySun + 1]}, " +
                   $"mover {_bakeWhy[WhySun + 2]}; cube: blanket {_bakeWhy[WhyCube]}, key {_bakeWhy[WhyCube + 1]}, " +
                   $"mover {_bakeWhy[WhyCube + 2]}) " +
                   $"(predicate {(_rebake ? "on" : "OFF -- /shadow rebake 1")}, " +
                   $"{(float)_moverSum / _frames:F1} movers/frame -- matrix {(float)_mvMatrix / _frames:F1}, " +
                   $"bones {(float)_mvBone / _frames:F1}, dangly {(float)_mvDangly / _frames:F1}, " +
                   $"vertex-dirty {(float)_mvVertex / _frames:F1}, appeared {(float)_mvAppear / _frames:F1}, " +
                   $"{(float)_ghostSum / _frames:F1} vanished, " +
                   $"{(float)_noRecordSum / _frames:F1} refused for want of a record" +
                   (_degenSum > 0 ? $", {(float)_degenSum / _frames:F1} DEGENERATE-admitted movers defeating the predicate" : "") + ")" +
                   // ...of how many live casters. Movers are a SUBSET of the live list, never the whole of
                   // it: a still caster still casts. The two numbers meeting is the #241 regression.
                   $" out of {(float)_liveSum / _frames:F1} live " +
                   $"({(float)(_liveSum - _moverSum) / _frames:F1} still)" +
                   $", caster selection in compute over the shared SowxCullSphere bounds " +
                   $"(cull {(CasterGather.Mode != 0 ? "on" : "OFF -- /shadow gather 1")})" +
                   (CasterGather.Views > 0
                    ? $" -- {(float)CasterGather.Views / _frames:F1} dispatches/frame over " +
                      $"{_gDrawN} staged draws in {_gGroups} batches " +
                      $"({CasterGather.UploadsSkipped}/{CasterGather.UploadsSkipped + CasterGather.Uploads} " +
                      "frame stagings matched the persistent source, nothing uploaded)"
                    : "") +
                   // #303: the culled-vs-drawn figure -- one view per frame, latently copied and read back
                   // frames later, so it costs no stall on the draw path.
                   (CasterGather.Samples > 0
                    ? $", sampled view kept {(float)CasterGather.SampledKept / CasterGather.Samples:F0}/" +
                      $"{(float)CasterGather.SampledStaged / CasterGather.Samples:F0} staged draws " +
                      $"(latent sample, /shadow lod {_lodTexels})"
                    : "") +
                   $", {(float)_viewSum / _frames:F1} views baked per frame ({LastShadowMs:F2} ms cpu last" +
                   (_qShadow.Measured ? $", {_qShadow.Avg / 1e6:F3} ms GPU avg" : "") + ")" +
                   $", {(float)_mdiCalls / _frames:F1} multidraw-indirect calls/frame, " +
                   $"{_liveCreatures} posed casters live" +
                   // The cutout casters: env-mapped materials the engine calls opaque without ever reading
                   // their diffuse alpha, so their depth draw runs the same 0.2 discard the colour pass does.
                   // Zero here with foliage on screen means they are all falling out at one of the three
                   // terms after it.
                   $", {_texWithAlpha}/{_texQueried} caster textures carry alpha -> " +
                   $"{_submitMasked} alpha-tested depth draws this period" +
                   (_maskNoTex + _maskNoProg == 0 ? ""
                    : $" ({_maskNoTex} masked casters have no diffuse this frame, " +
                      $"{_maskNoProg} no cutout program -- each casts nothing)"));
        _frames = 0; _mdiCalls = 0;
        _cubeLightsSum = 0; _viewSum = 0;
        _liveSum = 0; _qShadow.Reset();
        _cubeBakedSlots = 0; _cubeSkippedSlots = 0; _moverSum = 0; _ghostSum = 0; _cubeDynBakedPages = 0;
        _sunBakedViews = 0; _sunSkippedViews = 0;
        Array.Clear(_sunStaticL); _sunDynBakedViews = 0;
        CasterGather.LayerDispatches = 0; CasterGather.LayerViews = 0;
        Array.Clear(_bakeWhy);
        _mvMatrix = 0; _mvDangly = 0; _mvVertex = 0; _mvBone = 0; _mvAppear = 0; _degenSum = 0; _noRecordSum = 0;
        _submitMasked = 0; _maskNoTex = 0; _maskNoProg = 0;
        CasterGather.Views = 0; CasterGather.Batches = 0;
        CasterGather.SampledKept = 0; CasterGather.SampledStaged = 0; CasterGather.Samples = 0;
        CasterGather.UploadsSkipped = 0; CasterGather.Uploads = 0;
    }

    /// <summary>#306: the per-layer static re-bake rates, "L0 0,2/L1 0,1/..." -- cold, period line only.</summary>
    private static string SunLayerReadout()
    {
        string s = "";
        for (int i = 0; i < _cascades; i++)
            s += (i > 0 ? "/" : "") + $"L{i} {(float)_sunStaticL[i] / _frames:F1}";
        return s;
    }

    /// <summary>#306: which layers are currently degraded to the combined bake, e.g. "CC--".</summary>
    private static string SunCombinedReadout()
    {
        string s = "";
        for (int i = 0; i < _cascades; i++) s += _sunCombined[i] ? "C" : "-";
        return s;
    }

    /// <summary>Pool pages currently claimed -- the period line's occupancy figure. Cold.</summary>
    private static int DynPagesUsed()
    {
        int used = 0;
        for (int p = 0; p < _cubeDynPages && p < _dynPageSlot.Length; p++) if (_dynPageSlot[p] >= 0) used++;
        return used;
    }

    private static void Release(string why)
    {
        // The VAO cache keys on VDO pointers that may now be freed; the GL objects can only be deleted inside
        // the bracket, so the rebuild happens there (EnsureVao). Retiring the slots is also what makes this
        // IDEMPOTENT: a lost 4.6 context disables its service permanently, so Lifecycle calls Release every
        // frame thereafter, and a guard on the caster count alone would re-log forever.
        // The texture-alpha verdicts go with it. They are keyed on GL texture IDS, and GL recycles the
        // names of deleted objects -- the same trap the VAO cache re-checks every pass for. A recycled id
        // inheriting the previous texture's verdict is a wasted fetch in one direction and, in the other, a
        // foliage texture classified as having no alpha: the solid slab back again, intermittently and only
        // after an area transition. That is not a bug anyone would trace.
        Array.Clear(_texKey); Array.Clear(_texAlpha);
        _texQueried = 0; _texWithAlpha = 0;
        // #241/#302: the recorded faces describe a world that just went away, and the previous-frame slot
        // list names ownership slots the next enrollment is free to hand to different parts. Both have to go,
        // or the first frame in the new area finds every view fresh -- shadows frozen from the old module.
        Array.Clear(_cubeBaked); Array.Clear(_cubeHadMover); Array.Clear(_cubeKeyId);
        Array.Clear(_sunBaked); Array.Clear(_sunHadMover);
        // #305: the pool pages name lights that just went away, and the published page indices with them.
        Array.Clear(_cubeCombined); Array.Clear(CubeDynPage);
        // #306: the sun's half of the same wipe, degrade window included.
        Array.Clear(SunDynMask); Array.Clear(_sunCombined); Array.Clear(_sunWinBaked);
        _sunWinFrames = 0;
        Array.Fill(_dynPageOf, -1);
        if (_dynPageSlot.Length > 0) Array.Fill(_dynPageSlot, -1);
        // ...and the log is once per RELEASE, not once per frame: a lost 4.6 context disables its service
        // permanently, so Lifecycle calls this every frame thereafter and an unguarded line would fill the log.
        bool any = _prevCount > 0 || _liveCount > 0 || CascadeCount > 0 || CubeCount > 0;
        _invalidate = true; _moverCount = 0; _ghostCount = 0;
        _prevCount = 0;
        _liveCount = 0; _liveCreatures = 0;
        CasterGather.Stand();
        CascadeCount = 0; CubeCount = 0;   // the maps describe a world that just went away; stop the receiver sampling them
        if (any) _log?.Info($"CasterPass: caster set released ({why})");
    }




    /// <summary>The two discard thresholds. Alpha means COVERAGE on everything the engine ships except one
    /// authoring, so the default is the engine's own 0.2 -- the value its colour pass uses, which is what
    /// makes a leaf cast a leaf. The exception is an env-mapped material, where the diffuse alpha is
    /// REFLECTIVITY (<c>fEnvMapLevel = 1 - alpha</c>, inc_standard.shd:248): a polished floor authored at
    /// alpha 0.3 is solid and must keep its shadow, so only essentially-empty texels punch through there.</summary>
    private const float CutoutDiscard = 0.2f, EnvAlphaDiscard = 0.02f;

    /// <summary>Does this texture actually carry coverage -- texels the colour pass discards? Asked of GL,
    /// once per texture id, because nothing else answers it honestly. <c>Material::GetIsOpaque</c> @0x4c6190
    /// short-circuits on an env map, on the gob alpha and on its own memo, and each short-circuit produces an
    /// "opaque" verdict nobody ever checked a texel for. The internal format lies too, and that one cost a
    /// round: the ivy of #126 is <c>tcm02_leaf01</c>, a 128x128 DXT1 the engine uploads to the RGB variant
    /// (0x83F0, ALPHA_SIZE 0) -- no alpha by declaration, 48% of its blocks punchthrough-encoded, and the
    /// engine punches holes in it all the same. So the question is put to the decoder: read the level back
    /// through <c>glGetTexImage</c>, which runs the same decode path the sampler runs, and look for a texel
    /// the engine's own 0.2 cutout would drop.
    /// <para>ponytail: level 0 only, and the whole level rather than a sample -- one cold readback per
    /// texture into a shared buffer, capped at <see cref="MaxReadback"/>; anything larger is assumed to carry
    /// coverage (test rather than cast a possible slab). Sampling every 4th texel would quarter it, but a
    /// sparse cutout -- a railing, a rope bridge -- is exactly what a sparse scan misses.</para>
    /// <para>Open-address cache keyed on the GL id, cleared by <see cref="Release"/> -- an area transition,
    /// a module unload or a lost context all free textures, and GL recycles the names.</para></summary>
    internal static bool TexHasAlpha(int tid)
    {
        if (tid <= 0 || Gl.GetTexLevelParameteriv == null || Gl.GetTexImage == null) return false;
        int h = (int)((uint)tid * 2654435761u % TexCacheSize);
        for (int probe = 0; probe < 8; probe++)
        {
            int k = (h + probe) % TexCacheSize;
            if (_texKey[k] == tid) return _texAlpha[k];
            if (_texKey[k] != 0) continue;
            bool alpha = ScanForCoverage(tid);
            _texKey[k] = tid; _texAlpha[k] = alpha;
            _texQueried++;
            if (alpha) _texWithAlpha++;
            return alpha;
        }
        return true;   // cache row full: assume alpha, i.e. test rather than cast a possible slab
    }

    /// <summary>Alpha below this in ANY texel means the colour pass drops texels here, so the depth pass must
    /// too. The engine's own <c>fAlphaDiscardValue</c> (inc_common.shd:593) -- not a margin below it: a
    /// texture whose minimum alpha sits exactly at the cutout has no dropped texels.</summary>
    private const float CoverageAlpha = 0.2f;
    /// <summary>Widest level-0 readback attempted, in texels. 2048x2048 RGBA8 = 16 MB, one shared buffer.</summary>
    private const int MaxReadback = 2048 * 2048;

    private static bool ScanForCoverage(int tid)
    {
        int saved; Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &saved);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        int w = 0, h = 0, asize = 0;
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_WIDTH, &w);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_HEIGHT, &h);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_ALPHA_SIZE, &asize);
        bool covered = true;
        if (w > 0 && h > 0 && (long)w * h <= MaxReadback)
        {
            _readback ??= new byte[MaxReadback * 4];
            fixed (byte* p = _readback)
            {
                Gl.GetTexImage(Gl.TEXTURE_2D, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
                int cut = (int)(CoverageAlpha * 255f);
                covered = false;
                for (long i = 3, n = (long)w * h * 4; i < n; i += 4)
                    if (p[i] < cut) { covered = true; break; }
            }
        }
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)saved);
        if (_scanDiagLeft > 0)
        {
            _scanDiagLeft--;
            _log?.Info($"CasterPass: coverage scan tid={tid} {w}x{h} declared-alpha-bits={asize} -> cutout={covered}");
        }
        return covered;
    }

    /// <summary>ponytail: one buffer for every scan, never freed once minted. 16 MB resident for the life of
    /// the process against a readback that happens a few hundred times a session; free it in Release() only
    /// if a memory ceiling ever names it.</summary>
    private static byte[]? _readback;
    private static int _scanDiagLeft = 40;

    private const int TexCacheSize = 2048;
    private static readonly int[] _texKey = new int[TexCacheSize];
    private static readonly bool[] _texAlpha = new bool[TexCacheSize];
    private static int _texQueried, _texWithAlpha;

    /// <summary>This frame's GL texture id for a masked caster's diffuse, 0 if it does not resolve. Cold
    /// relative to the draw (once per masked caster per collect), and never cached -- see
    /// <see cref="_liveTex"/>.</summary>
    private static int MaskTid(IntPtr mat)
    {
        if (_getTexture == null) return 0;
        return AurTex.Tid((IntPtr)_getTexture(mat, 0));
    }




    /// <summary>One-shot 4.6-side setup shared by both views: the depth program and the FBO every layer is
    /// attached into. Any failure disarms the pass by name rather than leaving it silently drawing nothing.</summary>
    private static bool EnsureCommon()
    {
        if (_glFailed || _progFailed) return false;
        if (_prog == 0)
        {
            if (Gl.MultiDrawElementsIndirect == null || Gl.BindBufferBase == null
                || Gl.TexStorage3D == null || Gl.GenFramebuffers == null || Gl.CopyImageSubData == null)
            { Disarm("a required GL 4.4+ entry point is missing (multidraw-indirect / image copy)"); return false; }
            uint p = GlProgram.Graphics(ShdCastVs, ShaderStages.Source(ShdCastVs),
                                        ShdCastFs, ShaderStages.Source(ShdCastFs),
                                        _log, "CasterPass depth program",
                                        out string castVs, out string castFs);
            if (p != 0 && !(CastVsSafe(castVs) && CastFsSafe(castFs)))
            {
                Gl.DeleteProgram(p);
                _log?.Info("CasterPass: the caster depth override is refused -- it no longer reads Alloy's "
                         + "record through uDraw[gl_BaseInstance], has lost the skinning "
                         + "branch, or its fragment stage is not inert (a discard or a written gl_FragDepth "
                         + "silently removes or displaces every shadow). Rebuilding from the embedded stages "
                         + "(regenerate the override with /dumpshaders to get it back).");
                p = GlProgram.Graphics(CastVs, CastFs, _log, "CasterPass depth program (embedded)");
            }
            if (p == 0) { _progFailed = true; Disarm("the caster depth program failed to build"); return false; }
            _prog = p;
            _locVp = GlProgram.Loc(p, "uVP");
            // The cutout stage is optional in the sense that everything else still bakes without it: a
            // failure leaves masked casters out of the shadow maps entirely (see the collect), which is a
            // missing leaf shadow rather than the solid quad that made this necessary.
            _progMask = GlProgram.Graphics(CastMaskVs, CastMaskFs, _log, "CasterPass masked depth program");
            if (_progMask != 0)
            {
                _locMaskVp = GlProgram.Loc(_progMask, "uVP");
                _locMaskTex = GlProgram.Loc(_progMask, "uTex");
                _locMaskCut = GlProgram.Loc(_progMask, "uAlphaCut");
            }
            else _log?.Info("CasterPass: the masked depth program failed to build -- cutout casters cast no " +
                            "shadow at all this session (they would otherwise cast their whole quad)");
            uint f; Gl.GenFramebuffers(1, &f); _fbo = f;
        }
        return true;
    }

    private static bool EnsureSun()
    {
        if (!EnsureCommon()) return false;
        if (_depthTex == 0)
        {
            _depthTex = DepthArray(Gl.TEXTURE_2D_ARRAY, _mapSize, _cascades);
            if (_depthTex == 0) return false;   // DepthArray already disarmed by name
            Array.Clear(_sunBaked);   // #282: fresh storage holds no bake, whatever the keys say
        }
        // #306: the sun's dynamic half, allocated whenever configured (the /shadow split dial is live).
        if (_sunSplitCfg && SunDynTex == 0)
        {
            SunDynTex = DepthArray(Gl.TEXTURE_2D_ARRAY, _mapSize, _cascades);
            if (SunDynTex == 0) return false;   // DepthArray already disarmed by name
        }
        return true;
    }

    private static bool EnsureCubes()
    {
        if (!EnsureCommon()) return false;
        if (CubeTex == 0)
        {
            CubeTex = DepthArray(Gl.TEXTURE_CUBE_MAP_ARRAY, _cubeSize, _cubeLights * CubeFaces);
            if (CubeTex == 0) return false;   // DepthArray already disarmed by name
            Array.Clear(_cubeBaked);   // #241: fresh storage holds no bake, whatever the keys say
            // Global state, not a texture parameter: filtering blends across face borders, which is what
            // makes the receiver's hardware-PCF fetch legitimate right up to a face edge. The interim cubes
            // bought the same thing with an over-scanned face frustum and a matching uv shrink in the shader.
            Gl.Enable(Gl.TEXTURE_CUBE_MAP_SEAMLESS);
        }
        // #305: the dynamic pool, allocated whenever it is configured (the /shadow split dial is live, so
        // the array must exist even on a frame the dial is off). A refused allocation disarms by name.
        if (_cubeDynPages > 0 && CubeDynTex == 0)
        {
            CubeDynTex = DepthArray(Gl.TEXTURE_CUBE_MAP_ARRAY, _cubeDynSize, _cubeDynPages * CubeFaces);
            if (CubeDynTex == 0) return false;   // DepthArray already disarmed by name
        }
        return true;
    }

    /// <summary>#307: probe and arm the layered bake, inside the bracket. The extension is a property of the
    /// 4.6 context, asked once; a driver without it (or a program that will not build) takes the per-view
    /// path and says so once. Nothing disarms here -- the fallback IS the shipping path.</summary>
    private static void EnsureLayered()
    {
        if (!_layerDial || _layerProgFailed) return;
        if (_layerProbe == 0)
        {
            _layerProbe = Gl.FramebufferTexture != null
                          && AlloyRenderer.HasExt("GL_ARB_shader_viewport_layer_array"u8) ? 1 : -1;
            if (_layerProbe < 0)
                _log?.Info("CasterPass: ARB_shader_viewport_layer_array is not in this context -- the " +
                           "layered bake is unavailable, every view submits on the per-view path");
        }
        if (_layerProbe < 0 || _progLayer != 0) return;
        if (!CasterGather.EnsureLayer())
        {
            _layerProgFailed = true;
            _log?.Info("CasterPass: the layered gather dispatch failed to build -- per-view path");
            return;
        }
        uint p = GlProgram.Graphics(CastLayerVs, CastFs, _log, "CasterPass layered depth program");
        uint pm = GlProgram.Graphics(CastLayerMaskVs, CastMaskFs, _log, "CasterPass layered masked depth program");
        // Both or neither: a layered pass whose masked batches silently fell back to solid quads would
        // differ from the per-view A/B exactly where the cutout fix lives.
        if (p == 0 || pm == 0)
        {
            if (p != 0) Gl.DeleteProgram(p);
            if (pm != 0) Gl.DeleteProgram(pm);
            _layerProgFailed = true;
            _log?.Info("CasterPass: a layered depth program failed to build -- per-view path");
            return;
        }
        _progLayer = p; _progLayerMask = pm;
        _locLayerMaskTex = GlProgram.Loc(pm, "uTex");
        _locLayerMaskCut = GlProgram.Loc(pm, "uAlphaCut");
        _log?.Info("CasterPass: layered bake armed -- every stale view in one command buffer per target, " +
                   "gl_Layer from the view table (/shadow layered 0 is the per-view A/B)");
    }

    /// <summary>#307: render the frame's queued views, one layered pass per distinct target texture.
    /// False = the flush could not complete; the caller must publish NOTHING this frame.</summary>
    private static bool FlushLayered()
    {
        int n = _qN; _qN = 0;
        if (n == 0) return true;
        // distinct targets in first-seen order -- at most four exist (sun static/dynamic, cube static/pool).
        // A fifth-plus target would be a queue writer this flush does not know about; failing the flush
        // (publish nothing, blanket next frame) is the honest answer, not silently never baking it.
        uint* seen = stackalloc uint[8];
        int nt = 0;
        for (int k = 0; k < n; k++)
        {
            bool dup = false;
            for (int t = 0; t < nt && !dup; t++) dup = seen[t] == _qTex[k];
            if (dup) continue;
            if (nt >= 8) return false;
            seen[nt++] = _qTex[k];
        }
        for (int t = 0; t < nt; t++)
            if (!FlushTarget(seen[t], n)) return false;
        return true;
    }

    private static bool FlushTarget(uint tex, int n)
    {
        int size = 0;
        // The stale layers clear FIRST, one attach per view: a layered attachment clears every layer at
        // once, fresh ones included, so the per-layer loop here is correctness rather than thrift. This is
        // the FBO churn that survives #307; the uniforms, dispatches and the MDI storm are what go.
        for (int k = 0; k < n; k++)
        {
            if (_qTex[k] != tex) continue;
            size = _qSize[k];
            if (!BeginTarget(tex, size, _qLayer[k])) return false;
        }
        Gl.FramebufferTexture(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, tex, 0);
        if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
        {
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
            Disarm("the layered caster depth framebuffer is incomplete");
            return false;
        }
        Gl.Viewport(0, 0, size, size);
        // ponytail: chunking only exists for the blanket-invalidate frame in a huge area -- a settled frame
        // is one chunk. Growing MaxLayerCmds is the upgrade if the chunk count ever shows on the period line.
        int chunk = Math.Max(1, MaxLayerCmds / Math.Max(_gDrawN, 1));
        int k2 = 0;
        while (true)
        {
            int vn = 0;
            fixed (float* vs = _viewStage)
                while (k2 < n && vn < chunk)
                { if (_qTex[k2] == tex) StageView(vs, vn++, k2); k2++; }
            if (vn == 0) break;
            bool ok;
            fixed (float* vs = _viewStage) ok = CasterGather.DispatchLayer(vs, vn);
            if (!ok) { Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0); return false; }
            DrawLayered(vn);
            CasterGather.End();
        }
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        return true;
    }

    /// <summary>One queued view into the chunk's staged table, in the layout the dispatch and the layered
    /// vertex stage both read (mat4 vp; focus/reach; dir/along; lodR/point/set/layer).</summary>
    private static void StageView(float* dst, int vi, int k)
    {
        float* r = dst + (long)vi * CasterGather.ViewFloats;
        fixed (Matrix4x4* m = &_qVp[k]) Buffer.MemoryCopy(m, r, 64, 64);
        r[16] = _qFocus[k].X; r[17] = _qFocus[k].Y; r[18] = _qFocus[k].Z; r[19] = _qReach[k];
        r[20] = _qDir[k].X; r[21] = _qDir[k].Y; r[22] = _qDir[k].Z; r[23] = _qAlong[k];
        // this view's sub-texel radius, the same expression the per-view dispatch spells
        r[24] = _lodTexels * 2f * _qReach[k] / _qSize[k];
        r[25] = _qPoint[k] ? 1f : 0f;
        r[26] = _qSet[k];
        r[27] = _qLayer[k];
    }

    /// <summary>The layered pass's batch loop: <see cref="GatherSubmit"/>'s state walk minus the per-view
    /// uniforms, one indirect-count draw per batch for ALL the chunk's views.</summary>
    private static void DrawLayered(int vn)
    {
        Gl.ActiveTexture(Gl.TEXTURE0);
        int boundVao = -1, boundMask = -1, boundTex = -1;
        float boundCut = -1f;
        for (int g = 0; g < _gGroups; g++)
        {
            int cut0 = _gTex[g];
            if ((cut0 != 0 ? 1 : 0) != boundMask)
            {
                boundMask = cut0 != 0 ? 1 : 0;
                Gl.UseProgram(cut0 != 0 ? _progLayerMask : _progLayer);
                if (cut0 != 0) Gl.Uniform1i(_locLayerMaskTex, 0);
                boundCut = -1f;
            }
            if (cut0 != 0 && _gCut[g] != boundCut)
            { boundCut = _gCut[g]; Gl.Uniform1f(_locLayerMaskCut, boundCut); }
            if (cut0 != 0 && cut0 != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)cut0); boundTex = cut0; }
            if (_gVao[g] != boundVao) { Gl.BindVertexArray(AlloyRenderer.CasterVao(_gVao[g])); boundVao = _gVao[g]; }
            CasterGather.DrawLayer(_primTable[_gPrim[g]], g, _gStart[g], _gLen[g], vn);
        }
        _mdiCalls += _gGroups;
    }

    /// <summary>A depth array sampled with hardware PCF: LINEAR + depth-compare IS the filter, one bilinear
    /// 2x2 comparison per fetch at no more cost than a plain sample. Same setup for the 2D cascade array and
    /// the cube array -- only the target and the layer count differ.</summary>
    /// <summary>0 = the storage was refused, and the pass has already disarmed itself by name.</summary>
    private static uint DepthArray(uint target, int size, int layers)
    {
        uint t; Gl.GenTextures(1, &t);
        Gl.BindTexture(target, t);
        while (Gl.GetError() != 0) { }   // an earlier pass's error must not be read as this allocation's
        Gl.TexStorage3D(target, 1, Gl.DEPTH_COMPONENT24, size, size, layers);
        if (Gl.GetError() != 0)
        {
            Gl.BindTexture(target, 0);
            Gl.DeleteTextures(1, &t);
            Disarm($"a {size}x{size}x{layers} depth array could not be allocated (out of video memory?)");
            return 0;
        }
        Gl.TexParameteri(target, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TexParameteri(target, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TexParameteri(target, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(target, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(target, Gl.TEXTURE_COMPARE_MODE, (int)Gl.COMPARE_REF_TO_TEXTURE);
        Gl.TexParameteri(target, Gl.TEXTURE_COMPARE_FUNC, (int)Gl.LEQUAL);
        Gl.BindTexture(target, 0);
        return t;
    }



    private static void Disarm(string why)
    {
        _disarmed = true;
        _glFailed = true;
        _down.Fail(_log, why, "no Alloy caster submission this session");
        // BOTH counts, and this is the only place the distinction bites: OnPassSlot returns on _disarmed before
        // it ticks _frameSeq, so the freshness clock stops dead and every stamp stays equal to it forever.
        // A count left standing is then permanently "fresh" -- the receiver samples a frozen map for the rest
        // of the session, shadows pinned where the geometry no longer is.
        CascadeCount = 0; CubeCount = 0;
        Array.Clear(_cubeBaked); Array.Clear(CubeDynPage); Array.Clear(SunDynMask);
    }

    /// <summary>Once per frame, before any view submits: revalidate every caster and resolve its live world
    /// sphere and index range into the per-frame live list. Everything here is view-independent by
    /// construction, so every view of the frame stages from the one answer. Hot and alloc-free.</summary>

    /// <summary>#302: this frame's caster list, selected out of ALLOY's ownership table rather than out of a
    /// second table of our own. Every fact a bake needs is already there and already current -- the model
    /// matrix in the persistent record, the bone base in its feat pair, the index range in the VDO item
    /// tables, the world sphere beside the record -- so this walk SELECTS and LOCATES, and recomputes
    /// nothing. What used to happen here (a PartRef deref, an identity compare, a matrix read, a 16-float
    /// hash and a memo probe, per caster per frame) is gone with the table it maintained.
    /// <para>Two passes over the draw order so the SKINNED casters head the list: a view that fills its
    /// staging region truncates the tail, and #100's report was an arm going missing, never a distant wall.
    /// The pass-0 gate is one array read, so no slot is resolved twice.</para></summary>
    private static void Collect()
    {
        int live = 0;
        _moverCount = 0; _ghostCount = 0; _dMoverCount = 0; _sMoverCount = 0;
        // Read BEFORE OwnedBuffers.ClearDirty: dangly and morph-animated nodes both commit through
        // CommitItemDynamicData, so both land in the owned-buffer dirty set. Per VDO slot, not per caster.
        for (int v = 0; v < MaxVdos; v++) _vDirty[v] = AlloyRenderer.VdoDirty(v);
        int n = AlloyRenderer.OrderCount;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int k = 0; k < n; k++)
            {
                int slot = AlloyRenderer.OrderSlot(k);
                if (AlloyRenderer.SlotSkinned(slot) != (pass == 0)) continue;
                if (!AlloyRenderer.TryCaster(slot, out var f)) continue;
                // Re-read live: Repack moves the item tables under everyone.
                if (!TryRange(f.Vdo, f.Item, out int idxCount, out uint first, out uint baseVert)) continue;
                _liveSlot[live] = slot;
                _liveCx[live] = f.Cx; _liveCy[live] = f.Cy; _liveCz[live] = f.Cz;
                _liveR[live] = f.R;
                _liveIdxCount[live] = idxCount;
                _liveFirst[live] = first;
                _liveBaseVert[live] = baseVert;
                _liveVao[live] = f.Vao; _livePrim[live] = f.Prim;
                // A skinned caster is never alpha-tested: the cutout stage has no skinning branch, so a
                // masked skinmesh would punch its holes out of a BIND pose. Same set the capture cast solid.
                _liveTex[live] = !f.Skinned && AlloyRenderer.VdoHasUv(f.Vao) ? MaskTid(f.Mat) : 0;
                // Review: the triage counter lost its writer in the rework -- a masked-eligible caster whose
                // diffuse did not resolve THIS frame casts solid, and this is the number that says how often.
                if (_liveTex[live] == 0 && !f.Skinned && AlloyRenderer.VdoHasUv(f.Vao)) _maskNoTex++;
                _liveCut[live] = f.EnvAlpha ? EnvAlphaDiscard : CutoutDiscard;
                // Counted by REASON, not just totalled: "no improvement" and "every caster is a mover" look
                // identical in a skip count, and which term saturates decides where the next fix goes.
                // A still caster is STILL A CASTER -- it stays in the list and bakes; it merely does not
                // invalidate the views it stands in.
                bool mover = true;
                // Review: a caster ABSENT last frame is a mover on return, whatever its record says -- a
                // transient TryRange or VAO failure longer than the one-frame ghost+hadMover cover would
                // otherwise re-appear into views that never re-bake its shadow back.
                bool appeared = _liveStamp[slot] != _frameSeq - 1;
                if (f.Skinned) _mvBone++;
                else if (appeared) _mvAppear++;
                else if (f.RecMoved) _mvMatrix++;
                else if (f.Dangly) _mvDangly++;
                else if (_vDirty[f.Vao]) _mvVertex++;
                else mover = false;
                if (mover) _moverIdx[_moverCount++] = live;
                // #305: dynamic membership is motion, not appearance -- a prop that pops in still is a
                // static-set event, not a mover. A membership flip in either direction is also a static
                // event: the static bake must shed a caster that starts moving and adopt one that stops,
                // both within one predicate cycle. `appeared` forces the event (err toward stale: the
                // stamp says nothing trustworthy about a caster that was not collected last frame).
                bool dyn = f.Skinned || f.Dangly || f.RecMoved || _vDirty[f.Vao];
                _liveDyn[live] = dyn;
                bool wasDyn = !appeared && _dynStamp[slot] == _frameSeq - 1;
                if (dyn) { _dynStamp[slot] = _frameSeq; _dMoverIdx[_dMoverCount++] = live; }
                if (appeared || dyn != wasDyn) _sMoverIdx[_sMoverCount++] = live;
                _liveStamp[slot] = _frameSeq;
                live++;
            }
            // ...and pass 0 is the whole creature population, so the split #100 needs falls out of the walk.
            if (pass == 0) _liveCreatures = live;
        }
        _liveCount = live;
        _liveSum += live;
        // Whatever stopped casting this frame -- revoked, re-enrolled away, its index range stopped resolving
        // -- is a mover at its LAST KNOWN sphere. That invalidates the views it was standing in, and only
        // those: the alternative is a blanket re-bake every time one item range blinks.
        for (int q = 0; q < _prevCount; q++)
        {
            int slot = _prevSlots[q];
            if (_liveStamp[slot] == _frameSeq) continue;
            if (_ghostCount >= MaxGhosts) { _invalidate = true; break; }
            AlloyRenderer.CasterSphere(slot, out float gx, out float gy, out float gz, out float gr);
            // Review: a departing caster with a degenerate stored sphere (0 or NaN radius) cannot name the
            // views it shadowed -- a zero-radius ghost invalidates nothing, which freezes its baked depth.
            // Mirror the admission's direction: unknown means the blanket, one frame, cannot lie.
            if (!(gr > 0f)) { _invalidate = true; continue; }
            _ghostCx[_ghostCount] = gx; _ghostCy[_ghostCount] = gy; _ghostCz[_ghostCount] = gz;
            _ghostR[_ghostCount] = gr;
            _ghostCount++;
        }
        _noRecordSum += AlloyRenderer.CasterNoRecord;
        AlloyRenderer.CasterNoRecord = 0;
        _degenSum += AlloyRenderer.CasterDegenerate;
        AlloyRenderer.CasterDegenerate = 0;
        RecordProbe();
        _ghostSum += _ghostCount;
        _moverSum += _moverCount;
        Array.Copy(_liveSlot, _prevSlots, live);
        _prevCount = live;
    }

    /// <summary>#302 triage, cold and one-shot: the bake's ONE placement input, checked against the engine's
    /// own answer. A shadow that flickers or does not render can only be a view that bakes nothing, a view
    /// nobody samples, or geometry baked in the wrong place -- and the third is the only one this ticket can
    /// have introduced. Reports the matrix-class casters (the ones a wrong record moves) at the head of the
    /// list, the record's translation beside the node's, plus how many of the frame's casters have no record
    /// at all.</summary>
    private static int _probeLeft = 6;
    private static void RecordProbe()
    {
        if (_probeLeft <= 0 || _liveCount == 0) return;
        _probeLeft--;
        int shown = 0, mxSeen = 0, apart = 0, none = 0;
        for (int i = 0; i < _liveCount; i++)
        {
            AlloyRenderer.CasterProbe(_liveSlot[i], out var lag, out var live,
                                      out bool mx, out bool known, out int feat);
            if (!known) none++;
            if (!mx) continue;
            mxSeen++;
            float dx = lag.X - live.X, dy = lag.Y - live.Y, dz = lag.Z - live.Z;
            bool off = dx * dx + dy * dy + dz * dz > 1f;
            if (off) apart++;
            if (shown >= 4 || (!off && shown >= 2)) continue;
            shown++;
            _log?.Info($"CasterPass: [probe] slot {_liveSlot[i]} record=({lag.X:F2},{lag.Y:F2},{lag.Z:F2}) " +
                       $"node=({live.X:F2},{live.Y:F2},{live.Z:F2}) sphere=({_liveCx[i]:F2},{_liveCy[i]:F2}," +
                       $"{_liveCz[i]:F2}) r={_liveR[i]:F2} feat={feat} known={known}{(off ? "  <-- APART" : "")}");
        }
        _log?.Info($"CasterPass: [probe] {_liveCount} casters, {mxSeen} matrix-class, {apart} placed APART " +
                   $"from their node, {none} with NO RECORD at all");
    }

    /// <summary>#187: bake one view (a cascade layer, or one face of one cube) from what it sees, this frame.
    /// #303: one path -- the gather dispatch selects out of the frame's staging against the shared cull
    /// spheres, and the survivors draw by indirect count; <paramref name="point"/> picks which of the two
    /// view tests the dispatch runs.
    /// <para>False = this view produced nothing usable (nothing staged this frame, an incomplete FBO), and
    /// the caller must not publish it. The layer is cleared even when every caster culls out -- and when the
    /// frame staged ZERO casters -- because an uncleared layer is not "empty", it is LAST frame's depth:
    /// shadows frozen where the geometry no longer is.</para></summary>
    private static bool RenderView(uint tex, int size, int layer,
                                   Matrix4x4* vp, Vector3 lightDir, Vector3 focus, float reach, float margin,
                                   bool point, int set = CasterGather.SetAll)
    {
        // zeroed ahead of every early-out: a view that bakes nothing staged no creatures, and a stale count
        // would be re-added per cube face
        _submitCreatures = 0;
        if (!_gStaged) return false;
        LastViews++;
        if (!CasterGather.Active)
        {   // an EMPTY world still bakes: the clear is the bake
            if (!BeginTarget(tex, size, layer)) return false;
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
            return true;
        }
        // #307: under the layered bake a stale view is QUEUED, not submitted -- the flush after the pass
        // loop renders every queued view of a target in one layered pass. The optimistic true is covered by
        // the flush-failure handling, which publishes nothing for the frame. An overflowing queue renders
        // inline rather than dropping a view.
        if (LayeredActive && _qN < MaxBakeViews)
        {
            _submitCreatures = _gCreatures;
            _qTex[_qN] = tex; _qSize[_qN] = size; _qLayer[_qN] = layer; _qVp[_qN] = *vp;
            _qFocus[_qN] = focus; _qDir[_qN] = lightDir; _qReach[_qN] = reach; _qAlong[_qN] = reach + margin;
            _qPoint[_qN] = point; _qSet[_qN] = set;
            _qN++;
            return true;
        }
        return GatherSubmit(tex, size, layer, vp, lightDir, focus, reach, margin, point, set);
    }

    /// <summary>Attach one depth layer of <paramref name="tex"/> and clear it. Shared by both submission
    /// paths: a view that culls out entirely still clears, because an uncleared layer is not "empty", it is
    /// LAST frame's depth.</summary>
    private static bool BeginTarget(uint tex, int size, int layer)
    {
        // A context loss recreates the record buffer at the next colour build; a bracket that runs first
        // would bind name 0 and the depth stages would read an unbound SSBO. Nothing to bake against yet.
        if (AlloyRenderer.DrawRecordBuf == 0) return false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, tex, 0, layer);
        Gl.DrawBuffer(Gl.NONE); Gl.ReadBuffer(Gl.NONE);
        if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
        { Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0); Disarm("the caster depth framebuffer is incomplete"); return false; }
        Gl.Viewport(0, 0, size, size);
        // #302: the depth stages read ALLOY's per-draw records and ALLOY's bone block, at Alloy's own binding
        // points. Re-bound per view rather than once per frame because the colour pass is entitled to rebind
        // both between frames, and a bake reading a buffer that has since been re-pointed is a world baked
        // from someone else's matrices -- which renders, and renders wrong.
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindDraw, AlloyRenderer.DrawRecordBuf);
        if (AlloyRenderer.BoneBlockBuf != 0)
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindBones, AlloyRenderer.BoneBlockBuf);
        // depth state BEFORE the clear: a clear runs through the write mask and the scissor, so clearing
        // first would leave stale depth wherever a previous pass left either of them set
        // #292: cull OFF, deliberately DIFFERENT from the colour pass, and this is the disposition the
        // ticket asks for. A shadow map is depth from the LIGHT's camera, so "which face is the front one"
        // is the light's question and not the eye's: culling back faces here would drop a single-sided wall
        // whenever the light stands behind it, and the light would pour straight through. Casting from every
        // face is a superset -- for a closed mesh the nearest surface is a front face either way, and for an
        // open sheet the sheet is what should occlude. So the mismatch is one-directional and safe, whereas
        // matching the colour pass would open leaks.
        new PassState(depth: true, depthWrite: true, cull: false).Apply();
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.Clear(Gl.DEPTH_BUFFER_BIT);
        return true;
    }

    /// <summary>#286: the compute gather's per-frame staging. Parallel to the live list but in BATCH order,
    /// which is what lets one dispatch feed one glMultiDrawElementsIndirectCount per batch.</summary>
    private static readonly uint[] _gSrc = new uint[MaxCasters * CasterGather.SrcUints];
    /// <summary>Batch key per staged draw, and the live index it names. The key orders masked draws last (one
    /// program switch instead of one per VAO), then by VAO, primitive and mask texture -- the (VAO, prim,
    /// maskTex) key the multidraws cut on.</summary>
    private static readonly long[] _gKey = new long[MaxCasters];
    private static readonly int[] _gLive = new int[MaxCasters];
    private static readonly int[] _gVao = new int[MaxCasters], _gPrim = new int[MaxCasters],
                                 _gTex = new int[MaxCasters], _gStart = new int[MaxCasters],
                                 _gLen = new int[MaxCasters];
    /// <summary>#302: the batch's cutout threshold, uploaded as a uniform. Part of the sort key, so a batch
    /// carries one value -- the same shape the colour path's <c>uAlphaCut</c> has.</summary>
    private static readonly float[] _gCut = new float[MaxCasters];
    private static int _gDrawN, _gGroups, _gCreatures;
    /// <summary>#303: the frame produced a coherent staging -- a non-empty one the gather accepted, or a
    /// genuinely empty world. False = the gather refused a non-empty staging (no program, no sphere buffer
    /// yet), and the views must NOT bake -- baking would clear live shadows out of the maps.</summary>
    private static bool _gStaged;

    /// <summary>One staged draw, written into the source record. Its own function because
    /// <see cref="SelfCheck"/> runs it: a field landing one slot over is a caster baking at its neighbour's
    /// matrix, and none of it needs a GL context to be wrong.
    /// <para>#303: word 3 is the OWNERSHIP SLOT -- the dispatch culls <c>SowxCullSphere[slot]</c> (the colour
    /// cull's own bounds and exemptions) and writes it as <c>baseInstance</c>, which the depth stages read
    /// <c>uDraw</c> through. There is no per-draw shadow record left at all.</para></summary>
    private static void StageOne(uint* src, int d, int i, int g, int start)
    {
        uint* r = src + (long)d * CasterGather.SrcUints;
        r[0] = (uint)_liveIdxCount[i];
        // in INDICES already, unlike the colour path's byte offsets -- the indirect command takes elements
        r[1] = _liveFirst[i];
        r[2] = _liveBaseVert[i];
        r[3] = (uint)_liveSlot[i];
        r[4] = (uint)g;
        r[5] = (uint)start;
        // #305: the set flag the dispatch filter reads -- which of the two arrays this caster belongs in.
        r[6] = _liveDyn[i] ? 1u : 0u;
    }

    /// <summary>#286: stage the whole frame once, in batch order, and publish it. Everything per-view that
    /// is not the cull itself happens here exactly once -- the VAO lookup, the mask-texture
    /// resolve, the record. Inside the bracket, because the mask resolve issues Alloy's own texture copy.
    /// <para>False = a non-empty staging was REFUSED (the views must idle, not bake); an empty world stages
    /// true with zero draws, and its views bake clears.</para></summary>
    private static bool GatherStage()
    {
        int n = 0, creatures = 0;
        for (int i = 0; i < _liveCount; i++)
        {
            if (AlloyRenderer.CasterVao(_liveVao[i]) == 0) continue;
            int masked = _liveTex[i] != 0 && _progMask != 0 && TexHasAlpha(_liveTex[i])
                       ? OwnedTextures.Draw(_liveTex[i]) : 0;
            if (masked == 0 && _liveTex[i] != 0 && _progMask == 0) _maskNoProg++;
            if (masked != 0) _submitMasked++;
            _gLive[n] = i;
            // #302: the cutout threshold is a per-BATCH uniform now, exactly as the colour path spells it
            // (uAlphaCut), so it joins the key rather than riding in a per-draw record. Two values exist.
            _gKey[n] = ((masked != 0 ? 1L : 0L) << 62) | ((_liveCut[i] == EnvAlphaDiscard ? 1L : 0L) << 61)
                       | ((long)_liveVao[i] << 44) | ((long)_livePrim[i] << 36) | (uint)masked;
            n++;
            if (i < _liveCreatures) creatures++;
        }
        _gDrawN = 0;
        if (n == 0) return true;   // an empty world is a coherent staging: every view bakes a clear
        Array.Sort(_gKey, _gLive, 0, n);
        int g = -1;
        fixed (uint* src = _gSrc)
            for (int d = 0; d < n; d++)
            {
                int i = _gLive[d];
                if (g < 0 || _gKey[d] != _gKey[d - 1])
                {
                    g++;
                    _gStart[g] = d; _gVao[g] = _liveVao[i]; _gPrim[g] = _livePrim[i];
                    _gTex[g] = (int)(_gKey[d] & 0xffffffffL);
                    _gCut[g] = _liveCut[i];
                }
                _gLen[g] = d - _gStart[g] + 1;
                StageOne(src, d, i, g, _gStart[g]);
            }
        _gGroups = g + 1; _gCreatures = creatures;
        bool ok;
        fixed (uint* src = _gSrc)
            ok = CasterGather.Begin(n, _gGroups, src);
        if (ok) _gDrawN = n;
        return ok;
    }

    /// <summary>#286: bake one view without a per-view walk. The dispatch selects out of this frame's staging
    /// and writes the commands; the CPU issues one indirect-count draw per batch and never learns how many
    /// survived.</summary>
    private static bool GatherSubmit(uint tex, int size, int layer, Matrix4x4* vp,
                                     Vector3 lightDir, Vector3 focus, float reach, float margin, bool point,
                                     int set)
    {
        // PRE-cull figures: the survivors are the GPU's, and asking would be the readback this pass exists
        // to not do -- the latent sample on the period line is what reports them.
        _submitCreatures = _gCreatures;
        if (!BeginTarget(tex, size, layer)) return false;
        // this view's texel in world units -- ortho cascades span 2*reach across `size` texels, a cube face
        // 2*d at distance d bounded by reach. Identical to the walk's own lodR.
        CasterGather.Dispatch(focus, lightDir, reach, reach + margin, _lodTexels * 2f * reach / size, point, set);
        // unit 0 is where the cutout stage samples; nothing else in this bracket moves the active unit
        Gl.ActiveTexture(Gl.TEXTURE0);
        int boundVao = -1, boundMask = -1, boundTex = -1;
        float boundCut = -1f;
        // ponytail: every view issues one indirect-count draw per GLOBAL batch, including the batches whose
        // count the dispatch left at zero. It is bounded work with no readback and the period line
        // measures it (_mdiCalls), but at
        // ~200 views x a few hundred batches it is the next thing to look at if the win comes in short. The
        // upgrade path is a second tiny dispatch compacting the non-empty batches into a batch list, which
        // stays on the same no-readback side of the line.
        for (int g = 0; g < _gGroups; g++)
        {
            int cut0 = _gTex[g];
            if ((cut0 != 0 ? 1 : 0) != boundMask)
            {
                boundMask = cut0 != 0 ? 1 : 0;
                Gl.UseProgram(cut0 != 0 ? _progMask : _prog);
                Gl.UniformMatrix4fv(cut0 != 0 ? _locMaskVp : _locVp, 1, 0, (float*)vp);
                if (cut0 != 0) Gl.Uniform1i(_locMaskTex, 0);
                boundCut = -1f;
            }
            if (cut0 != 0 && _gCut[g] != boundCut)
            { boundCut = _gCut[g]; Gl.Uniform1f(_locMaskCut, boundCut); }
            if (cut0 != 0 && cut0 != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)cut0); boundTex = cut0; }
            if (_gVao[g] != boundVao) { Gl.BindVertexArray(AlloyRenderer.CasterVao(_gVao[g])); boundVao = _gVao[g]; }
            CasterGather.Draw(_primTable[_gPrim[g]], g, _gStart[g], _gLen[g]);
        }
        _mdiCalls += _gGroups;
        CasterGather.End();
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        return true;
    }

    /// <summary>#100: is a caster sphere outside this cube face's 90deg pyramid? <paramref name="t"/> is its
    /// depth along the face forward and <paramref name="dSq"/> its squared distance from the light, so the
    /// perpendicular offset is the third side of that right triangle -- no per-face basis needed, which is the
    /// point: a cone is rotation-invariant and cannot clip a corner the way a mis-rotated square would.
    /// Conservative by construction (the cone circumscribes the pyramid, and the radius allowance is doubled),
    /// which is what <see cref="SelfCheck"/> pins -- a cull that rejects a visible caster deletes a shadow,
    /// while one that admits an invisible one only costs a draw.</summary>
    private static bool FaceCulls(float dSq, float t, float r)
    {
        if (t < -r) return true;                          // wholly behind the face
        float lim = t * FaceConeSlope + 2f * r;
        return lim < 0f || dSq - t * t > lim * lim;
    }

    /// <summary>Resrefs the caster pass looks up before falling back to the GLSL below (#92).</summary>
    internal const string ShdCastVs = "cx_cast_vs", ShdCastFs = "cx_cast_fs";

    /// <summary>#161: the caster vertex stage's half of the override contract. #303 retired the SowxCaster
    /// block with the staging that filled it, so what an override has to keep is two things: it reads Alloy's
    /// record through <c>uDraw[gl_BaseInstance]</c> (the ownership slot the gather wrote as baseInstance),
    /// and it keeps #96's skinning branch. A stage that lost the branch links perfectly, the bone attributes
    /// simply go unread, and every creature casts its BIND POSE while it animates.</summary>
    private static bool CastVsSafe(string vs)
        => vs.Contains("uDraw[gl_BaseInstance]") && vs.Contains("uBone")
           && !vs.Contains("SowxCaster");

    /// <summary>#161: the caster fragment stage must stay inert. It runs into a depth-only FBO, so the only
    /// two things it can say that matter are <c>discard</c> -- which writes no depth, i.e. the caster casts no
    /// shadow at all -- and <c>gl_FragDepth</c>, which displaces the depth the map is compared against into
    /// acne or peter-panning. Neither errors and neither shows in the log. An override past both is provably
    /// unable to change what the pass bakes.</summary>
    private static bool CastFsSafe(string fs)
        => !fs.Contains("discard") && !fs.Contains("gl_FragDepth");

    /// <summary>Depth vertex stage. #303: <c>gl_BaseInstance</c> IS the ownership slot (the gather's
    /// dispatch writes it into every surviving command), so the stage reads Alloy's own per-draw record
    /// directly -- the model matrix and #96's bone base are the SAME numbers the colour pass draws
    /// with rather than a second copy of them. gl_BaseInstance and NOT gl_DrawID: compaction renumbers
    /// gl_DrawID and nothing else. So the model matrix and #96's bone base are the SAME numbers the colour pass draws
    /// with rather than a second copy of them. The skinning branch mirrors the colour stage's arm term for
    /// term (<c>SowxBoneBase</c> / <c>SowxSkinMat</c>, shared source, not a re-spelling), and the rigid arm is
    /// <see cref="AlloyRenderer.ClipExpr"/> character-for-character under <c>invariant gl_Position</c> --
    /// #119's standing constraint, which the depth prepass compares against.</summary>
    internal const string CastVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;

        """ + AlloyRenderer.DrawBufDecl + AlloyRenderer.FeatDecl + AlloyRenderer.BoneVs + """
        uniform mat4 uVP;
        invariant gl_Position;
        void main()
        {
            SowxDraw d = uDraw[gl_BaseInstance];
            int bb = SowxBoneBase(d);
            if (bb >= 0) gl_Position = uVP * d.model * (SowxSkinMat(bb) * vec4(aPos, 1.0));
            else gl_Position = uVP * d.model * vec4(aPos, 1.0);
        }
        """;
    /// <summary>Depth-only: the fixed-function depth write is the whole output.</summary>
    internal const string CastFs = """
        #version 460 core
        void main() { }
        """;

    /// <summary>The masked caster's depth stage: the same clip position, plus the diffuse UVs, so the
    /// fragment stage can punch the cutout's holes into the shadow map. No skinning -- <see cref="Collect"/>
    /// keeps a posed caster out of the masked set for exactly that reason, since a bind-pose cutout is worse
    /// than a solid one.
    /// <para>No resref, for <see cref="DepthKeyVs"/>'s reason one level up: a content override that drifted
    /// from the 0.2 threshold would write shadow where the colour pass draws nothing, which reads as dirt on
    /// a wall and points at no file.</para></summary>
    private const string CastMaskVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUV;

        """ + AlloyRenderer.DrawBufDecl + """
        uniform mat4 uVP;
        invariant gl_Position;
        out vec2 vUV;
        void main()
        {
            SowxDraw d = uDraw[gl_BaseInstance];
            gl_Position = uVP * d.model * vec4(aPos, 1.0);
            vUV = aUV;
        }
        """;
    /// <summary>...and the cutout itself, at the engine's own <c>fAlphaDiscardValue</c> -- a per-BATCH
    /// uniform since #302, spelled <c>uAlphaCut</c> exactly as the colour path spells it, rather than a
    /// per-draw record field. Two thresholds exist and the batch key carries which.
    /// <para>KNOWN COLLISION, named because it fails the same way this fix was reported and in the opposite
    /// direction: on env-mapped content the diffuse alpha carries TWO authorings. On foliage it is coverage
    /// (transparent between the leaves) and the discard is exactly right; on a polished floor or chrome trim
    /// it is REFLECTIVITY -- <c>fEnvMapLevel = 1 - alpha</c>, inc_standard.shd:248 -- so solid geometry
    /// authored as very shiny sits at alpha &lt;= 0.2 by design, and only essentially-empty texels may punch
    /// through there.</para></summary>
    private const string CastMaskFs = """
        #version 460 core
        uniform sampler2D uTex;
        uniform float uAlphaCut;
        in vec2 vUV;
        void main() { if (texture(uTex, vUV).a <= uAlphaCut) discard; }
        """;

    /// <summary>#307: the layered depth stage. <c>baseInstance</c> packs the view-table index above the
    /// ownership slot (<see cref="CasterGather.ViewShift"/>); the stage routes to its layer and takes its fit
    /// matrix from the view table, so no per-view uniform exists. <c>uVP</c> is deliberately spelled as a
    /// LOCAL so the clip expression stays #119's character for character under the invariant -- the maths is
    /// identical, only where the matrix comes from moved. Embedded only, never overridable: an override here
    /// could only desync the two bake paths the A/B compares.</summary>
    internal const string CastLayerVs = """
        #version 460 core
        #extension GL_ARB_shader_viewport_layer_array : require
        layout(location = 0) in vec3 aPos;

        """ + AlloyRenderer.DrawBufDecl + AlloyRenderer.FeatDecl + AlloyRenderer.BoneVs + """

        """ + CasterGather.ViewTblDecl + """
        invariant gl_Position;
        void main()
        {
            int vi = gl_BaseInstance >> 18;
            SowxDraw d = uDraw[gl_BaseInstance & 0x3ffff];
            gl_Layer = int(uViewTbl[vi].c.w);
            mat4 uVP = uViewTbl[vi].vp;
            int bb = SowxBoneBase(d);
            if (bb >= 0) gl_Position = uVP * d.model * (SowxSkinMat(bb) * vec4(aPos, 1.0));
            else gl_Position = uVP * d.model * vec4(aPos, 1.0);
        }
        """;

    /// <summary>...and the masked twin, <see cref="CastMaskVs"/> with the same routing. No skinning, for
    /// the same bind-pose-cutout reason.</summary>
    private const string CastLayerMaskVs = """
        #version 460 core
        #extension GL_ARB_shader_viewport_layer_array : require
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUV;

        """ + AlloyRenderer.DrawBufDecl + """

        """ + CasterGather.ViewTblDecl + """
        invariant gl_Position;
        out vec2 vUV;
        void main()
        {
            int vi = gl_BaseInstance >> 18;
            SowxDraw d = uDraw[gl_BaseInstance & 0x3ffff];
            gl_Layer = int(uViewTbl[vi].c.w);
            mat4 uVP = uViewTbl[vi].vp;
            gl_Position = uVP * d.model * vec4(aPos, 1.0);
            vUV = aUV;
        }
        """;
}
