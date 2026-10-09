using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The one Alloy submission path every rigid shadow caster goes through (ADR 0019, #93). Tiles and
/// placeables are the same thing here -- a part with an indexed VDO, an index range and a model matrix -- so
/// there is no kind-based branching anywhere below. The cube POPULATION is split in two, a static array
/// baked on the predicate and a small per-frame dynamic pool, composed with min() in the receiver
/// (<c>/shadow split 0</c> is the combined-bake A/B, #305). A view re-bakes only when its recorded depth has
/// stopped describing the world -- the view's own key changed, or something moved inside it
/// (<see cref="ViewStale"/>); every term errs toward stale, so a wrong answer costs a bake and can never
/// freeze a shadow, and <c>/shadow rebake 0</c> is the bake-everything A/B (#241, #282).
/// <para>Selection and submission are <see cref="CasterGather"/>'s compute dispatch alone. It culls against
/// <see cref="CullPass"/>'s shared <c>CxCullSphere</c> buffer (one set of bounds, one set of exemptions, for
/// the shadow cull and the colour cull alike) and writes indirect commands whose <c>baseInstance</c> IS the
/// ownership slot, so the depth stages read <c>uDraw[gl_BaseInstance]</c> directly -- gl_BaseInstance rather
/// than gl_DrawID because compaction renumbers gl_DrawID and nothing else; <see cref="SelfCheck"/> pins it
/// (#303).</para>
/// <para>The pass runs in the 4.6 context -- the SAME context the colour pass draws in, which is why it binds
/// Alloy's VAOs rather than keeping a set of its own -- and renders every view the Alloy lit stage samples:
/// the sun's concentric cascade array and the local lights' depth cube array, six faces per light. One
/// <see cref="Collect"/> per frame selects the world; the sun and a torch then draw the same geometry through
/// the same command format.</para>
/// <para>The caster population and every fact about it are ALLOY's: a caster is an OWNERSHIP SLOT, and the
/// depth stage reads its model matrix, bone base and material out of <c>uDraw[slot]</c> exactly as the colour
/// stage does (#302). The consequence to know about is a ONE-FRAME LAG: the shadow bracket runs ahead of the
/// colour build, so the records it reads describe the previous frame's world. It is coherent -- the mover
/// signal and the record content describe the same world state, so the predicate cannot miss a bake -- and a
/// rigid mover's shadow trails it by a frame. Retiring it means moving the pass behind the build (#176).</para>
/// <para>Creature casters ride the same path with no special case. An NWN creature is mostly PARTS-BASED
/// (live-verified by dumping the VAOs): each body piece is a rigid mesh parented to a bone, posed entirely by
/// its node matrix. A TRUE skinmesh (cloak, robe, tail) carries bone attributes at locations 8/9 and is posed
/// by <c>SkinPose</c> into Alloy's bone block; the depth stage re-runs the same skinning off the same block
/// and the same base.</para>
/// <para>Gated on <see cref="AlloyGate.Alloy"/> so the vanilla verdict -- and every other frame-start refusal
/// -- reverts it frame-atomically with the rest of the Alloy frame. <c>shadow.casters=0</c> is the kill
/// switch, <c>skinning</c> the one dial that decides the posed set, and <c>/shadow</c> carries the look knobs.
/// The frame path allocates nothing.</para>
/// </summary>
public sealed unsafe partial class CasterPass : IExtenderService
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
    internal const uint GlUnsignedShort = 0x1403;   // not-an-offset: GL enum

    /// <summary>Cascade ceiling. The receiver's <c>uSunVp</c> array is sized from this, so raising it means
    /// raising both -- <see cref="SelfCheck"/> pins them together.</summary>
    internal const int MaxCascades = 4;
    /// <summary>Cascades rendered per frame (<c>shadow.cascades</c>, Boot).</summary>
    private static int _cascades = 4;
    /// <summary>Per-cascade face size in texels (<c>shadow.size</c>, Boot).</summary>
    private static int _mapSize = 2048;
    /// <summary>World units the outermost cascade reaches (<c>/shadow dist</c>). Sun shadow cost is bound by
    /// this and nothing else: the fit follows the player, so an area ten times the size costs the same.</summary>
    private static float _maxDist => Shadows.Dist.V;

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
    /// <summary>Depth range per cascade in world units (<c>2 * radius</c>: the upstream margin is depth-clamped,
    /// not ranged, see <see cref="ShadowMath.DirectionalLightViewProj"/>) -- what turns the receiver's world-unit depth bias
    /// into the device units its comparison runs in.</summary>
    internal static readonly float[] CascadeRange = new float[MaxCascades];
    /// <summary>Local-light cube ceiling: how long the receiver's <c>uLocalLight</c> array is, and the most
    /// <c>shadow.cubes</c> may ask for. Raising it costs uniform slots only -- the cube array itself is
    /// sized from the CONFIGURED count, so an unused ceiling is free.</summary>
    internal const int MaxCubes = 64;
    /// <summary>Local lights that get a shadow cube (<c>shadow.cubes</c>, 0 = off) and the per-face size
    /// (<c>shadow.cubesize</c>). The interim system's receiver could not go past 32 lights at all because it
    /// unrolled a sampler per slot, and a cube ARRAY has no such ceiling -- the cost is VRAM and nothing else.
    /// <para>#151 raised the default from 8 to 24: a torch-lit street routinely declares 60+ shadow-casting
    /// lights, and at 8 the budget's membership churned as the player walked, which is what read as shadows
    /// popping in and out. Since #187 every slot in the budget bakes six faces every frame, so this count IS
    /// the pass's cost. Lower <c>shadow.cubes</c> (or <c>shadow.cubesize</c>) on a small card.</para></summary>
    private static int _cubeLights = 32, _cubeSize = 512;
    /// <summary>Near plane shared by every cube face. Bigger is better depth precision; it also means a
    /// caster closer to the light than this is not recorded, so it stays small.</summary>
    private const float CubeNear = 0.3f;

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
    /// <summary>Every point cube's near plane: the self-shadow skip radius (<c>shadow.near</c> x <c>light.radius</c>),
    /// never under <see cref="CubeNear"/>. The geometry a light node sits in (a brazier's bowl) casts nothing,
    /// on live receivers exactly as in the bake -- the two disagreeing cut a creature's shadow off near it.</summary>
    internal static float CubeNearPlane => MathF.Max(CubeNear, AlloyLights.ShadowNearWorld);
    private static float _cubeNearSeen;

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
    /// <summary>Frames in a row the static cap has held this slot; at <see cref="StaticHoldMax"/> it bakes
    /// regardless, so far ranks are not starved by near ones under sustained churn.</summary>
    private static readonly byte[] _cubeHeldRun = new byte[MaxCubes];
    private const int StaticHoldMax = 8;
    /// <summary>A ROAMING light: one whose position changed on consecutive scans (a carried torch). Its
    /// static pages are cleared and everything in reach bakes into its dynamic page every frame -- six
    /// 256px faces instead of six 512px static faces PLUS the page. The last-seen position is the motion
    /// term (never the bake key: a demoted slot keys nothing, so the key would read as moving forever);
    /// the count saturates at 2 so a scan that breaks early cannot lose the level.</summary>
    private static readonly float[] _cubeLastPos = new float[MaxCubes * 3];
    /// <summary>The scans of the last two registered moves. Roaming = both within <c>dynhold</c> frames: the
    /// same hold the casters' dynamic set has, so a light that pauses for a frame does not flap between a
    /// 512px static bake and six clears. Registered = <see cref="RoamEpsilon"/> or more from the latched last
    /// position: torch jitter at the ULP is not motion, a crawl is.</summary>
    private static readonly int[] _cubeMovedAt = new int[MaxCubes], _cubeMovedPrev = new int[MaxCubes];
    /// <summary>World units a light must move from its latched last position to register a move. 1mm.</summary>
    private const float RoamEpsilon = 1e-3f;
    /// <summary>The "never moved" stamp. Half of <c>int.MinValue</c> so <c>_frameSeq - stamp</c> cannot wrap
    /// negative and pass the recency term.</summary>
    private const int NeverMoved = int.MinValue / 2;
    private static readonly bool[] _cubeRoam = new bool[MaxCubes];
    private static long _cubeRoamSlots;

    // --- #305: the static/dynamic split. The predicate above can only SKIP a view; it cannot make a stale
    // one cheaper, and a stale cube re-bakes the whole static world it sees -- one walking creature dirties
    // the room. So the population is split: the STATIC array (the existing CubeTex) bakes on the predicate
    // and is invalidated only by static-set events, while a smaller DYNAMIC pool holds movers only and
    // re-bakes every frame, which is affordable because it is the movers and not the room. The receiver
    // composes the two with min() -- depth is a min-of-blockers, so it composes exactly, and NO COPY exists
    // anywhere in the design (this is not #187's composite cache, whose per-view glCopyImageSubData is what
    // lost). Cubes only; the sun cascades keep the plain predicate.

    /// <summary>Dynamic-pool face size (<c>shadow.dynsize</c>) and page count (config
    /// <c>shadow.dyncount</c>, 0 = no pool, split permanently off; defaults to the cube budget so the pool
    /// never starves into the combined fallback). A PAGE is one light's six mover-only
    /// faces, claimed only while a mover stands inside that light -- the pool is per mover-bearing light,
    /// never a 1:1 mirror of the static array, which is what keeps its VRAM a fraction of the static
    /// array's. Movers are few, small and close, so reduced resolution is the right spend.</summary>
    private static int _cubeDynSize = 256, _cubeDynPages = 16;
    /// <summary><c>/shadow split 1|0</c>: the A/B. Off = one combined bake per cube on the plain #241
    /// predicate, exactly as before this ticket.</summary>
    private static bool _split => Shadows.Split.V;
    /// <summary>The dynamic pool's cube array (0 = none yet), layer <c>6*page + face</c>.</summary>
    internal static uint CubeDynTex;
    /// <summary>Per cube slot: the pool PAGE holding its movers, PLUS ONE -- 0 means "no movers near this
    /// light", which is also what a receiver program that never sets the uniform reads, so a stale content
    /// override degrades to static-only rather than to sampling page zero. Published fresh each frame the
    /// page bakes.</summary>
    internal static readonly float[] CubeDynPage = new float[MaxCubes];
    /// <summary>Spheres per slot in <see cref="CubeDynSphere"/>. One union sphere round every mover in a
    /// 36u reach covered the whole plaza, so the hot path's ray test rejected nothing -- and so did four, once a
    /// busy plaza put twenty-odd movers under every lamp: a folded box spans the square between them, and every
    /// baked fragment under the lamp paid a live mover tap (`/lightmap movers 0` read 2,2 ms of the opaque pass).
    /// A miss is a few ALU ops on a broadcast read; a false hit is the point-light body and a cube tap.</summary>
    internal const int DynSpheres = 16;
    /// <summary>Per slot: <see cref="DynSpheres"/> spheres (xyz, r; r &lt; 0 = unused) round the dynamic movers
    /// in the light's reach -- the only thing its page can shadow. The lit stage skips the hot path wherever the
    /// light-to-fragment ray misses all of them. Sphere 0 = the light's reach while it roams (the page then
    /// holds everything).</summary>
    internal static readonly float[] CubeDynSphere = new float[MaxCubes * DynSpheres * 4];
    /// <summary>Bumped whenever <see cref="CubeDynSphere"/> is rewritten, so the upload runs once per frame.</summary>
    internal static int CubeDynSphereGen;
    /// <summary>Face size of the dynamic pool as rendered.</summary>
    internal static int CubeDynFaceSize => _cubeDynSize;
    /// <summary>Slot -&gt; claimed pool page (-1 none) and page -&gt; owning slot (-1 free). Sticky, like the
    /// slots themselves: a page churn would be invisible in the depth (it re-bakes every frame) but wastes
    /// nothing to avoid.</summary>
    private static readonly int[] _dynPageOf = new int[MaxCubes];
    private static int[] _dynPageSlot = Array.Empty<int>();
    /// <summary>#336: per pool page, the faces an EVENT (a mover, a membership flip, a ghost) sat in last
    /// frame -- a mover's previous face needs the re-bake as much as its current one. <see cref="AllFaces"/>
    /// on claim, on a blanket and on a light that moved.</summary>
    private static int[] _dynPageMask = Array.Empty<int>();
    /// <summary>The page holds a complete bake the receiver may keep sampling through a skipped frame.</summary>
    private static bool[] _dynPageValid = Array.Empty<bool>();
    /// <summary>The cadence LADDER, not a blanket rate: cube ranks below <c>/shadow dynfull</c> and sun cascades
    /// below <c>/shadow sunfull</c> bake their dynamic half every frame; the rest every other frame, parity
    /// staggered so the skipped half alternates. Near = the player's own shadow and the torches around them,
    /// at full rate; far = an animated prop under a distant torch, whose shadow is a few texels wide and
    /// where a one-frame hold is invisible. A roam, an invalidation, a re-key or an unbaked page never skips;
    /// events on a skipped frame are carried in the page's pending mask and baked the next.</summary>
    private static int _dynFull => Shadows.DynFull.V;
    private static int _sunFull => Shadows.SunFull.V;
    /// <summary><c>/shadow sunlatch N</c>: static cascade fit RE-LATCHES per frame (0 = unbounded). The fit
    /// centre is the look focus, 14u ahead of the camera, so a turn drifts every cascade past its slack in
    /// a few degrees and re-bakes all four static layers (2048px, the whole caster world) on one frame.
    /// A layer whose re-latch waits keeps its published fit, which still contains the player by the slack's
    /// margin; the receiver selects by that fit, so nothing visible changes while the four spread out.
    /// The outermost is exempt: past it there is no shadow at all, and under a sustained turn cascade 0
    /// would take the whole budget every frame.
    /// ponytail: a per-frame count, not a time budget; raise it if a cascade ever lags a fast turn.</summary>
    private static int _sunLatch => Shadows.SunLatch.V;
    /// <summary><c>/shadow staticcap N</c>: static cube slot re-bakes per frame (0 = unbounded). A held slot
    /// republishes its standing page and stays stale -- its mover evidence carries in <c>_cubeHadMover</c> --
    /// so it bakes on a later frame, nearest rank first. Re-keys and blanket invalidates never hold. Six
    /// 512px faces of the whole static world per slot is the single biggest hitch term the ledger read
    /// (CubeStatic 22/frame in hitch rows against a 16 mean).</summary>
    private static int _staticCap => Shadows.StaticCap.V;
    /// <summary><c>/shadow flipcollect 1</c>: the A/B -- every stage cast-admission flip re-collects the caster
    /// list, as before the membership-neutral entries.</summary>
    internal static bool FlipCollect => Shadows.FlipCollect.V;
    /// <summary>Masked shadow depth (<c>/shadow mask</c>): 1 = alpha-tested, 0 = the solid program for masked
    /// batches (prices the fetch + discard), -1 = skip them (prices their raster), 2 = alpha-tested only where
    /// the texture's hole fraction reaches <see cref="_maskHoles"/> (a gate, a railing), solid elsewhere (a
    /// leaf card). Default 2: xtul read the solid canopy as the nicer look at no cost, then the solid gate as
    /// a wall (2026-09-05).</summary>
    private static int _maskMode => Shadows.Mask.V;
    /// <summary><c>/shadow maskholes X</c>: the hole fraction (texels under the cutout) from which mode 2 masks.</summary>
    private static float _maskHoles => Shadows.MaskHoles.V;
    /// <summary>#363 <c>/shadow maskgraded X</c>: the graded-alpha fraction from which a diffuse counts as
    /// glass and takes the masked stage regardless of its holes. Default = the census's blend threshold, so a
    /// soft-edged leaf card stays on the hole gate.</summary>
    private static float _maskGraded => Shadows.MaskGraded.V;
    /// <summary>#363 <c>/shadow maskdither N</c>: the shadow-texel block the masked stage's ordered discard
    /// steps on (1 = per texel, the receiver's PCF averages it; 2 = 2x2 clumps read softer but blotchier);
    /// 0 = off. Never per-frame: the maps are cached, and a jittering pattern is a permanent mover.</summary>
    private static int _maskDither => Shadows.MaskDither.V;
    /// <summary>Past this the blocks read as a pattern, not a shadow.</summary>
    internal const int MaskDitherMax = 16;
    /// <summary>Frames a cube slot's shadow takes to fade in after its first bake and out after its light
    /// drops from the budget; 0 = pop. A draining slot keeps its depth and its light id and takes no newcomer
    /// until it reaches zero, so the receiver keeps finding it through CubeSlotOf.</summary>
    private static int _fadeFrames => Shadows.Fade.V;
    /// <summary>Slots held back from the ranking while the fade is on, so a light leaving the budget has a
    /// slot to drain in. Without it a full budget (the plaza's 32/32) swaps every departure into a newcomer
    /// the same frame and the fade-out never runs. <c>/shadow fadeslots N</c>.</summary>
    private static int _fadeSlots => Shadows.FadeSlots.V;
    private static readonly float[] _cubeFade = new float[MaxCubes];
    /// <summary>Per-slot shadow strength for the receiver (uLocalFade), 0..1.</summary>
    internal static readonly float[] CubeFade = new float[MaxCubes];
    private static int _cubeTurnovers;
    private static long _cubeHeldSlots;
    /// <summary><c>/shadow itemdirty 0</c>: the A/B back to the per-pool-buffer vertex-dirty signal. See
    /// <c>AlloyRenderer.ItemDirty</c> for what the item-level one fixes.</summary>
    private static bool _itemDirty => Shadows.ItemDirty.V;
    /// <summary>An arena caster has no VDO buffer to be dirty: the arena republishes a moved range through
    /// <c>AlloyRenderer.DbRangesMoved</c> instead, so the per-vertex signal does not apply to it.</summary>
    private static bool VertexDirty(int vao, int item)
        => !AlloyRenderer.IsArenaVao(vao) && _vDirty[vao] && (!_itemDirty || AlloyRenderer.ItemMoved(vao, item));
    private static long _cubeDynSkipped, _sunDynSkipped;
    private const int AllFaces = (1 << CubeFaces) - 1;
    /// <summary>#336: the sun's dynamic layer holds a bake for the fit matrix beside it, so a frame with
    /// dynamic casters in the fit but no event among them samples it without re-baking.</summary>
    private static readonly bool[] _sunDynValid = new bool[MaxCascades];
    private static readonly Matrix4x4[] _sunDynVp = new Matrix4x4[MaxCascades];
    /// <summary>#336's A/B: 0 = every dynamic view re-bakes every frame it has dynamic casters, as #305/#306
    /// shipped it.</summary>
    private static bool _dynEvents => Shadows.DynEvents.V;
    /// <summary>#336: dynamic pages for the nearest <c>/shadow dynranks</c> cube lights only (rank = the
    /// nearest-first cube order). A far torch keeps its static cube and shadows no mover; with every face
    /// of every page stale every frame (the tileset's animated props), the page count is the view count.
    /// Boot = every cube.</summary>
    private static int _dynRanks => Shadows.DynRanks.V;
    private static long _cubeDynBakedFaces;
    /// <summary>CX-188 step 4: pair tiles this period's frames baked, for the period line's clause.</summary>
    private static long _pairBakedTiles;
    /// <summary>Slot is in COMBINED mode: it has movers but no pool page (pool exhausted), so it bakes both
    /// halves into the static array every frame -- the per-slot fallback to pre-split behaviour. A mode
    /// change re-keys the slot, or a mover baked in while combined would outlive the mode.</summary>
    private static readonly bool[] _cubeCombined = new bool[MaxCubes];
    /// <summary>Frame each OWNERSHIP slot was last a dynamic caster on -- the set-transition detector. A
    /// caster whose membership flips (starts or stops moving) is a static-set event: the static bake must
    /// shed it or adopt it, within one predicate cycle.</summary>
    private static readonly int[] _dynStamp = new int[MaxCasters];
    private static long _cubeDynBakedPages;
    /// <summary>#282: the sun's half of the same bake state -- the snapped fit MATRIX is the key (a pure
    /// function of direction, latched centre, radius and margin), the mover test the only other term.
    /// <para>The LATCHED fit centre per cascade, and the fix for the crawling cascade rings: the fit
    /// used to re-centre on the live focus every frame, and the snap quantum (one texel, 1.5cm on cascade 0)
    /// is smaller than one frame of walking, so every cascade re-keyed and re-baked every frame, the
    /// static/dyn split degraded to combined on all four, and the hard cascade handover swept across static
    /// geometry as a re-resolving ring. The fit now keeps its centre until the focus drifts a fraction of
    /// the cascade's radius away; the receiver selects by the fit itself, so nothing else must know.</para></summary>
    private static readonly float[] _sunFitC = new float[MaxCascades * 3];
    private static readonly bool[] _sunFitValid = new bool[MaxCascades];
    /// <summary>The sun's <see cref="ShadowMath.LatchUpX"/> latch.</summary>
    private static bool _sunUpX;
    /// <summary>Re-latch when the focus has drifted this fraction of the cascade radius from the latched
    /// centre. Small on purpose: the receiver's cross-fade weights are computed from the LIVE focus
    /// (<see cref="SunFocusLive"/>), and its full-handover point (90% of the fit radius) is only guaranteed
    /// to lie inside the latched fit while the drift stays under ~5%. The bakes a tighter slack buys back
    /// are cheap -- the snap keeps the texel grid absolute, so a re-latch reproduces every overlapping texel
    /// bit-identical and nothing visible changes.</summary>
    private const float SunFitSlack = 0.05f;
    /// <summary>The LIVE fit focus of the current sun pass -- what the receiver's cascade cross-fade keys
    /// on, precisely because it moves smoothly where the latched centres step.</summary>
    internal static Vector3 SunFocusLive;
    /// <summary>Per-cascade fit radius, published for the receiver's cross-fade band.</summary>
    internal static readonly float[] CascadeFitRadius = new float[MaxCascades];
    private static readonly bool[] _sunBaked = new bool[MaxCascades];
    /// <summary>#196: cascades whose static layer this frame re-baked -- the grass casters go in after the flush.</summary>
    private static readonly bool[] _sunRebaked = new bool[MaxCascades];
    private static readonly bool[] _sunDynRebaked = new bool[MaxCascades];
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
    /// the shape with no per-layer resolution bookkeeping. <c>shadow.sundyn</c> 0 disables the sun split
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
    /// <summary><c>shadow.sundyn</c>: 0 = no sun dynamic array, the cascades keep the combined bake.</summary>
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
    /// <summary>Per live caster: a skinmesh, or any part rooted on a creature gob. Always a mover and never
    /// under the <c>dynmin</c> floor: a still character is rigid parts a few decimetres across whose matrices
    /// stop, and run 17 lost its shadow to both rules (dyn pool 0/32, 0 posed casters, 4 dangly movers).</summary>
    private static readonly bool[] _liveCreature = new bool[MaxCasters];
    /// <summary>The entry's slot stopped casting since the collect (a tile fade, a skip): it stays in the list
    /// with a zero-count draw until it turns back on or the next collect drops it.</summary>
    private static readonly bool[] _liveOff = new bool[MaxCasters];
    /// <summary>The tenant the entry was collected under; a slot re-seated since is not "in the list".</summary>
    private static readonly int[] _liveGenOf = new int[MaxCasters];
    /// <summary>Does the standing list hold this slot under its current tenant? Read by the stage (workers);
    /// every field is written only by the caster pass, which never overlaps the stage.</summary>
    internal static bool InList(int slot)
        => _liveIdxSeq[slot] == _collectSeq && _liveGenOf[_liveIdx[slot]] == AlloyRenderer.SlotEnrollGen(slot);

    /// <summary>Slots whose admission went off outside the stage (a freed slot, an inactive LOD): the refresh
    /// visits them so their entries turn off. Main thread only. Past the cap the next refresh re-collects.</summary>
    private static readonly int[] _offQ = new int[4096];
    private static int _offN;
    private static bool _offOverflow;
    internal static void NoteSlotOff(int slot)
    {
        // The LIVE INDEX, resolved now: a freed slot is commonly re-seated in the same pass, and by the time
        // the refresh reads the queue _liveIdx[slot] names the new tenant's entry, not the one going off.
        if (_liveIdxSeq[slot] != _collectSeq) return;
        if (_offN < _offQ.Length) _offQ[_offN++] = _liveIdx[slot]; else _offOverflow = true;
    }
    /// <summary>Entries appended since the collect. They are outside the per-VAO buckets, so the refresh
    /// visits them every frame until the next collect folds them in.</summary>
    private static readonly int[] _appended = new int[4096];
    private static int _appN;
    /// <summary>Bumped by an append: the gather's staging must be rebuilt even though the collect stood.</summary>
    private static int _listVer, _gListVer;

    /// <summary>Take a freshly seated slot into the standing list without a re-collect: an OFF entry (its
    /// record, and so its admission, has not landed) that the stage's on-flip turns into an appearance.
    /// False = the list cannot take it (no list standing, skinned -- the creature half is contiguous --, out
    /// of room, or no geometry), and the caller re-collects.</summary>
    internal static bool Append(int slot)
    {
        if (_collectedAt < 0 || _liveCount == 0 || AlloyRenderer.SlotSkinned(slot)) return false;
        if (_liveCount >= MaxCasters || _appN >= _appended.Length) return false;
        if (!AlloyRenderer.TryCasterGeom(slot, out var f)) return false;
        if (!CasterRange(in f, out int idxCount, out uint first, out uint baseVert)) return false;
        int live = _liveCount++;
        _liveSlot[live] = slot;
        _liveCx[live] = f.Cx; _liveCy[live] = f.Cy; _liveCz[live] = f.Cz; _liveR[live] = f.R;
        _liveIdxCount[live] = idxCount; _liveFirst[live] = first; _liveBaseVert[live] = baseVert;
        _liveVao[live] = f.Vao; _livePrim[live] = f.Prim; _liveItem[live] = f.Item;
        _liveTex[live] = AlloyRenderer.VdoHasUv(f.Vao) ? MaskTid(in f) : 0;
        _liveCut[live] = f.EnvAlpha ? EnvAlphaDiscard : CutoutDiscard; _liveBlend[live] = f.Blended;
        _liveDyn[live] = false; _liveVisit[live] = 0;
        _liveOff[live] = true; _liveGenOf[live] = AlloyRenderer.SlotEnrollGen(slot);
        _liveIdx[slot] = live; _liveIdxSeq[slot] = _collectSeq;
        _appended[_appN++] = live; _listVer++;
        FrameZones.Count(FrameZones.Ev.CasterAppend);
        return true;
    }
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
    private static bool _rebake => Shadows.Rebake.V;
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

    /// <summary>#488: the receiver epoch. The frustum a per-frame view was culled against goes stale as the
    /// camera moves, and a dyn face with no event in it HOLDS -- so the planes are built from the EPOCH's
    /// camera pose, pushed out by the pose's worst-case drift (<see cref="RxPosStep"/> + far x
    /// <see cref="RxAngle"/>); the frame the live camera has moved past that step or turned past that angle
    /// opens a new epoch and forces every dyn view. Like the blanket invalidate, the force stands until a
    /// frame whose scans reached every view -- a stalled scan leaves the views after it un-forced.</summary>
    private const float RxPosStep = 4f, RxAngle = 5f * MathF.PI / 180f;
    private static Vector3 _rxPos, _rxFwd;
    private static float _rxFar, _rxFov, _rxAspect;
    private static bool _rxValid, _rxForce, _rxPending;
    /// <summary>Receiver epochs this period -- forced dyn re-bakes charged to the camera.</summary>
    private static long _rxEpochs;

    /// <summary>#488: once per frame, ahead of the passes -- publish this frame's receiver planes and decide
    /// whether the epoch moved.</summary>
    private static void ReceiverFrame()
    {
        Vector3 pos = FrameInputs.SceneCamPos, fwd = FrameInputs.SceneCamFwd;
        float far = FrameInputs.CamFar, fov = FrameInputs.CamFovy, aspect = FrameInputs.CamAspect;
        if (!FrameInputs.HasSceneCam || !FrameInputs.HasPerspective || fwd.LengthSquared() < 1e-8f) fwd = Vector3.Zero;
        else fwd = Vector3.Normalize(fwd);
        bool moved = !_rxValid || (pos - _rxPos).LengthSquared() > RxPosStep * RxPosStep
                     || Vector3.Dot(fwd, _rxFwd) < MathF.Cos(RxAngle) || far != _rxFar || fov != _rxFov || aspect != _rxAspect;
        if (moved) { _rxPos = pos; _rxFwd = fwd; _rxFar = far; _rxFov = fov; _rxAspect = aspect; _rxValid = true; _rxEpochs++; _rxPending = true; }
        _rxForce = _rxPending;
        // a degenerate (no-camera) forward writes planes that keep everything
        ShadowMath.ReceiverPlanes(_rxPos, _rxFwd, _rxFov, _rxAspect, FrameInputs.CamNear, _rxFar, RxPosStep + _rxFar * RxAngle, CasterGather.Planes);
    }
    private static long _cubeBakedSlots, _cubeSkippedSlots, _cubeOffSlots, _moverSum, _ghostSum;
    /// <summary>#241: movers BY REASON. A skip count that stays at zero says the predicate is not paying;
    /// only this says why -- a world whose matrices all jitter, a shared vertex pool marked dirty by one
    /// dangly mesh, or simply a scene full of creatures. Each is a different fix.</summary>
    private static long _mvMatrix, _mvDangly, _mvVertex, _mvBone, _mvAppear;
    /// <summary>Matrix movers' sphere radii this period (sum, largest): what the still scene's movers ARE.</summary>
    private static float _mvMatrixR, _mvMatrixRMax;
    private static int _mvMatrixRMaxSlot = -1;
    private static long _degenSum;
    /// <summary>Mover samples this period with a non-finite sphere, refused before the fold (one would poison the box
    /// it joined and drop that light's hot answer with it).</summary>
    private static long _nanMovers;
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
    /// <summary>The slot lost its light: whatever depth it holds describes a light no longer in the budget,
    /// and the next light to take it must not inherit it -- nor its dynamic page, mode or fade.</summary>
    private static void FreeCubeSlot(int j)
    {
        if (_cubeKeyId[j] != IntPtr.Zero) _cubeTurnovers++;
        _cubeFade[j] = 0f;
        _cubeBaked[j] = false; _cubeKeyId[j] = IntPtr.Zero; _cubeHadMover[j] = false;
        _cubeCombined[j] = false;
        if (_dynPageOf[j] >= 0) { _dynPageSlot[_dynPageOf[j]] = -1; _dynPageOf[j] = -1; }
    }

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
    private static MatGetTextureFn _getTexture;

    private static readonly float[] _liveCx = new float[MaxCasters], _liveCy = new float[MaxCasters],
                                    _liveCz = new float[MaxCasters], _liveR = new float[MaxCasters];
    private static readonly int[] _liveIdxCount = new int[MaxCasters];
    private static readonly uint[] _liveFirst = new uint[MaxCasters], _liveBaseVert = new uint[MaxCasters];
    private static readonly int[] _liveVao = new int[MaxCasters], _livePrim = new int[MaxCasters], _liveItem = new int[MaxCasters];
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
    /// <summary>#336: the frame the standing list was COLLECTED on (<c>_collectedAt</c> is the frame it was
    /// last collected OR refreshed). With the refresh no longer stamping every caster per frame, "present
    /// last frame" is membership of a list that stood through last frame: a stamp at or after this collect,
    /// while <c>_collectedAt</c> chains.</summary>
    private static int _lastCollectFrame = -1;
    /// <summary>#336: ownership slot -&gt; live index, valid while its seq is the current collect's. The
    /// refresh resolves the colour build's touched slots through it without a walk.</summary>
    private static readonly int[] _liveIdx = new int[MaxCasters], _liveIdxSeq = new int[MaxCasters];
    /// <summary>#336: the live list bucketed by VAO (CSR built at collect), so a dirty VDO names its casters
    /// without a walk over the rest.</summary>
    /// <summary>Indexed by the DENSE caster VAO id, so it spans the arena sections past
    /// <c>AlloyRenderer.DbVaoBase</c> as well as the VDO slots.</summary>
    private static readonly int[] _vaoStart = new int[AlloyRenderer.CasterVaoIds + 1], _vaoMembers = new int[MaxCasters];
    /// <summary>#336: the frame each live index was visited by the refresh, so the three visit sources
    /// (dirty VDO, touched record, dynamic last frame) admit a caster once.</summary>
    private static readonly int[] _liveVisit = new int[MaxCasters];
    /// <summary>#336: the frame's visit list, gathered (and bone-checked) BEFORE any admission so a refresh
    /// that has to bail leaves no half-written stamps for the same-frame collect to misread.</summary>
    private static readonly int[] _visitIdx = new int[MaxCasters];
    private static int _visitN;
    /// <summary>#336: last frame's dynamic set, copied at <see cref="Account"/> -- the refresh revisits it
    /// to see a caster STOP moving, which no colour-side edge reports.</summary>
    private static readonly int[] _dPrevIdx = new int[MaxCasters];
    private static int _dPrevCount;
    /// <summary>#336's A/B: 1 = the refresh walks every caster as #328 shipped it.</summary>
    private static bool _refreshAll => Shadows.RefreshAll.V;
    /// <summary>Casters the refresh visited, and records the colour build changed, summed over the period.</summary>
    private static long _visitSum, _touchedSum;
    /// <summary>#336: frames a caster stays in the DYNAMIC set after it last moved. The live read that
    /// named it: ~20 static cube bakes a frame charged to "mover", with vertex-dirty flapping 120-260
    /// casters between the two sets each frame -- every flip a static-set event re-baking the cube it stood
    /// in. A held caster costs nothing extra (the dynamic layer re-bakes every frame anyway); the flip back
    /// happens once, after the hold. <c>/shadow dynhold 0</c> is the old behaviour.</summary>
    private static int _dynHold => Shadows.DynHold.V;   // default 30, was 8: vertex-dirty items committing intermittently flipped sets and re-baked static cubes 15/frame
    /// <summary>This frame's diffuse texture id for a MASKED caster (0 = not masked, or the texture did not
    /// resolve). Re-fetched every collect: a <c>CAurTexture*</c> is borrowed and freed on area transitions,
    /// so it is never cached -- and the per-frame fetch is what keeps an animated diffuse cutting the right
    /// silhouette.</summary>
    private static readonly int[] _liveTex = new int[MaxCasters];
    /// <summary>...and the discard threshold that goes with it, staged into the per-caster record.</summary>
    private static readonly float[] _liveCut = new float[MaxCasters];
    /// <summary>#363: a blended-class caster; routed to the masked stage so its record alpha dithers.</summary>
    private static readonly bool[] _liveBlend = new bool[MaxCasters];
    /// <summary>Creature casters occupy the head of the live list (#100 stages them first); the static world
    /// follows. Submit uses the split to report what a view actually took.</summary>
    private static int _liveCreatures;
    private static int _liveCount;
    /// <summary>#328: what the standing list was collected under -- Alloy's caster generation and record
    /// epoch, and the frame. While all three hold, a frame walks the list (<see cref="Refresh"/>) instead of
    /// the world; <c>_collectSeq</c> counts collects so the gather's staging knows when its copy went stale.
    /// -1 = no list (a repack or release dropped it).</summary>
    private static int _liveGen = -1, _liveEpoch = -1, _collectedAt = -1, _collectSeq;
    /// <summary>The collect the gather staging was built from, its staged count, and its masked count (a
    /// period counter the standing staging re-adds per frame).</summary>
    private static int _gCollectSeq = -1, _gN, _gMasked;
    /// <summary>Collects this period, for the [casters] line.</summary>
    private static int _collects;

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
    private static int _locMaskVp = -1, _locMaskTex = -1, _locMaskCut = -1, _locMaskDither = -1, _locMaskEnv = -1;
    /// <summary>The masked depth program that samples the mask through the record's bindless handle
    /// (<c>uTexH</c> slot 0) instead of a bound unit, so masked batches no longer split per texture.
    /// Built only when the renderer's bindless path is armed; <c>/shadow maskh 0</c> is the A/B.</summary>
    private static uint _progMaskH, _progLayerMaskH;
    private static int _locMaskHVp = -1, _locMaskHCut = -1, _locLayerMaskHCut = -1, _locMaskHDither = -1, _locLayerMaskHDither = -1, _locMaskHEnv = -1, _locLayerMaskHEnv = -1;
    private static bool _maskH => Shadows.MaskH.V;
    private static bool HandleMaskReady => _maskH && _progMaskH != 0 && (_progLayer == 0 || _progLayerMaskH != 0);
    private static bool _maskHFailed;

    /// <summary>Build the handle-sampling pair once the renderer's bindless path is armed. Lazy because the
    /// bindless probe runs in the first COLOUR frame and this pass's programs are built in the shadow
    /// bracket before it; a pass built on that frame would otherwise never get the pair. Re-stages once
    /// when the pair becomes usable, so the standing key picks it up.</summary>
    private static void EnsureHandleMask()
    {
        if (_maskHFailed || _progMask == 0 || !AlloyRenderer.Bindless || CastMaskHFs == CastMaskHFsSrc) return;
        if (_progMaskH == 0)
        {
            _progMaskH = GlProgram.Graphics(CastMaskHVs, CastMaskHFs, _log, "CasterPass handle-masked depth program");
            if (_progMaskH == 0) { _maskHFailed = true; return; }
            _locMaskHVp = GlProgram.Loc(_progMaskH, "uVP"); _locMaskHCut = GlProgram.Loc(_progMaskH, "uAlphaCut"); _locMaskHDither = GlProgram.Loc(_progMaskH, "uDither"); _locMaskHEnv = GlProgram.Loc(_progMaskH, "uEnvAlpha");
            _listVer++;
            _log?.Info("CasterPass: handle-sampled masks armed -- masked batches merge per VAO (/shadow maskh 0 is the A/B)");
        }
        if (_progLayer != 0 && _progLayerMaskH == 0)
        {
            _progLayerMaskH = GlProgram.Graphics(CastLayerMaskHVs, CastMaskHFs, _log, "CasterPass layered handle-masked depth program");
            if (_progLayerMaskH == 0) { _maskHFailed = true; return; }
            _locLayerMaskHCut = GlProgram.Loc(_progLayerMaskH, "uAlphaCut"); _locLayerMaskHDither = GlProgram.Loc(_progLayerMaskH, "uDither"); _locLayerMaskHEnv = GlProgram.Loc(_progLayerMaskH, "uEnvAlpha");
            _listVer++;
        }
    }
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
    private static bool _layerDial => Shadows.Layered.V;
    /// <summary>0 = not yet probed, 1 = the extension is present, -1 = absent (said once; per-view path).</summary>
    private static int _layerProbe;
    private static uint _progLayer, _progLayerMask;
    private static int _locLayerMaskTex = -1, _locLayerMaskCut = -1, _locLayerMaskDither = -1, _locLayerMaskEnv = -1;
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
    /// <summary>#488: this queued view is re-baked under THIS frame's camera, so the receiver-frustum cull
    /// may apply. Never set on a held (static-set) view: it would be culled against a frustum the camera
    /// has since left.</summary>
    private static readonly bool[] _qRx = new bool[MaxBakeViews];
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
    /// <summary>S3-4(c): CPU ticks the bake's own 4.6 context bracket costs -- both switches, each a fence +
    /// <c>glFlush</c> + <c>MakeCurrent</c> + a server-side wait. Printed on the period line as
    /// <c>own 4.6 bracket N.NNN ms/frame</c>.
    /// <para><b>Why the bake has a bracket of its own, and not the colour pass's.</b> The windows on BOTH
    /// sides of it are the ENGINE's context by requirement. Before: <c>RspShim</c>'s
    /// env-map tick (gone: the env map is the texture registry's now) could BUILD an engine texture, and building one inside Alloy's 4.6
    /// bracket uploads it into the wrong context. After: <c>AlloyRenderer.OnFrameAnchor</c> harvests each sky
    /// part's VAO through <c>VdoSlot</c> -- a read of the engine's own vertex-array state -- and then runs
    /// <c>SampleEngineSets</c>, both commented engine-context-only. So the bake cannot simply extend into the
    /// colour bracket; folding the two means MOVING the bake past that preamble to just inside the colour
    /// <c>Enter</c>, which is the <see cref="CasterPass"/> refactor ADR 0017 / the portability audit's A1
    /// names and leaves unmeasured -- and which also reshuffles <c>ShadowPose</c>/<c>PosedByShadow</c>,
    /// <c>DropDeadHandles</c> against the colour drain, the GPU query and the zone nesting.</para>
    /// <para>This counter is the gate on that refactor, so the decision is a number rather than an argument:
    /// under ~0,05 ms/frame the frame's shape is not worth changing, above it the ticket writes itself.
    /// <c>[gl46] N brackets</c> remains the count of them.</para></summary>
    private static long _ctxTicks, _ctxFrames;
    private static long _cubeLightsSum;

    /// <summary>Views rendered on the last frame -- since #241 that is the views the re-bake predicate found
    /// STALE, not every view the budget holds; <c>/shadow rebake 0</c> is what puts it back to all of them -- plus the CPU
    /// milliseconds the shadow slot took. The milliseconds are CPU SUBMISSION only; <see cref="_qShadow"/> is
    /// what measures the GPU side.</summary>
    internal static int LastViews;
    internal static float LastShadowMs;
    /// <summary>The period's average bake GPU time, for the [alloy] gpu total.</summary>
    internal static double ShadowGpuMs => _qShadow.Measured ? _qShadow.Avg / 1e6 : 0.0;
    /// <summary>The same meter's running totals, for a caller taking its OWN window rather than the period's
    /// -- <c>/alloy bench</c>, whose steps are far shorter than a period line. The bake is the one GPU cost
    /// that is not a <see cref="AlloyRenderer.GpuTerm"/>, and <c>lightmap.movers</c> moves it (the pair
    /// tiles), so a bench step blind to it cannot split what that knob removes.</summary>
    internal static ulong ShadowGpuSum => _qShadow.Sum;
    internal static int ShadowGpuN => _qShadow.N;
    /// <summary>#151: GPU time of the whole shadow bracket. The CPU stopwatch only sees submission, and the
    /// live logs had ~12ms of frame time no CPU timer accounted for -- this query is what convicts or clears
    /// the pass, and it is the number #187's regression is read off.</summary>
    private static GpuQuery _qShadow;
    private static bool _qShadowOn;
    private static long _viewSum;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!Shadows.Casters.Boot)
        { log.Info("CasterPass: stood down (shadow.casters=0)"); return; }
        if (!SelfCheck(log)) return;

        // Restart knobs, read through Boot: every one of these sizes an allocation or gates the arming, so
        // it is a boot decision. The look knobs -- cascade distance and friends -- are the group's Live rows.
        _mapSize = Shadows.Size.Boot;
        _cascades = Shadows.Cascades.Boot;
        _cubeLights = Shadows.Cubes.Boot;
        _cubeSize = Shadows.CubeSize.Boot;
        // #305: the dynamic pool's shape. The page default is the cube budget itself: the first live run
        // saturated a 16-page pool in a 32-cube street and every overflow slot fell back to the combined bake
        // -- the exact static-bake churn the split exists to remove, for 24 MB saved. Shrink shadow.dyncount
        // on a small card.
        _cubeDynSize = Shadows.DynSize.Boot;
        _cubeDynPages = Shadows.DynCount.Boot;
        // #306: the sun's half of the split sizes an allocation too (a cascades x size mirror).
        _sunSplitCfg = Shadows.SunDyn.Boot;
        _dynPageSlot = new int[Math.Max(_cubeDynPages, 1)];
        _dynPageMask = new int[_dynPageSlot.Length];
        _dynPageValid = new bool[_dynPageSlot.Length];
        Array.Fill(_dynPageSlot, -1);
        Array.Fill(_dynPageOf, -1);

        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _getTexture = EngineFn.MatGetTexture;
        if (_primTable == null)
        { log.Info("CasterPass: primitive-type table unresolved -- Alloy caster submission unavailable"); return; }

        // Repack moves a VDO's item indices and offsets under us. Every other consumer of these tables
        // already invalidates on it (#88 chain: draw funnels 50, interim cube 40, Alloy colour 30) and this pass
        // must too -- a stale item index does not read as an empty draw, it reads as ANOTHER part's index
        // range, which submits out-of-range indices to the GPU.
        _repackSlot = hooks.ChainVdoRepack(&OnVdoRepack, RepackPriority);
        if (_repackSlot == null)
        { log.Info("CasterPass: could not chain VdoRepack -- refusing to arm on stale index ranges"); return; }

        CasterGather.Init(log);

        _armed = true;
        log.Info($"CasterPass: armed -- Alloy multidraw-indirect caster submission into a {_cascades}x{_mapSize}" +
                 $" sun cascade array out to {_maxDist:F0}u (shadow.cascades/shadow.size, /shadow dist)" +
                 $" and {_cubeLights}x{_cubeSize} local-light shadow cubes (shadow.cubes/shadow.cubesize)" +
                 ", posed casters bake from Alloy's own bone block" +
                 "; needs the part table, so unarmed draw funnels leave it with nothing to enroll");
    }

    /// <summary>The <c>shadow.movers</c> button: dump one frame's movers on the next bake.</summary>
    internal static void DumpMoversNext() => _dumpMovers = true;

    /// <summary>A mask dial moved: the caster list's per-record mask routing is stale.</summary>
    internal static void ListChanged() => _listVer++;

    /// <summary>Chain priority on Repack: invalidation order among observers doesn't matter.</summary>
    private const int RepackPriority = 20;
    private static VdoRepackFn* _repackSlot;

    /// <summary>Repack observer. The live list and the gather's uploaded staging are both copies of index
    /// ranges that have just moved, so both are dropped rather than submitted from -- and #328 makes the
    /// drop the re-collect edge, since the list otherwise stands across frames.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoRepack(IntPtr vdo)
    {
        VdoRepackFn next = *_repackSlot;
        if (next != null) next(vdo);
        _liveCount = 0; _liveCreatures = 0; _liveGen = -1;
        CasterGather.Stand();
    }

    /// <summary>#241: the whole re-bake decision for one VIEW, as four independent reasons a recorded depth
    /// layer may no longer describe the world. Pulled out so <see cref="SelfCheck"/> can pin each one.
    /// <para>#282 gave the sun cascades the same predicate. The four reasons are the same four -- a blanket
    /// invalidate, the view's own key changing, a mover in it now, a mover in it last frame -- and what
    /// differs between a cube face and a cascade is only which volume the mover test runs against.</para></summary>
    internal static bool ViewStale(bool invalidate, bool keyChanged, bool hadMover, bool hasMover)
        => invalidate || keyChanged || hadMover || hasMover;

    /// <summary>#308: the part-table-wipe blanket -- the one edge with no per-slot story, because every slot
    /// dies at once over dead pointers. Ordinary arrivals and departures need no blanket any more: a slot is
    /// never re-tenanted within the scan that vacates it, so a departure lapses in place for the vanish
    /// sweep to ghost, and an arrival announces itself as an appeared mover when its record lands.</summary>
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

    /// <summary>One caster's index range, from whichever geometry source its slot draws out of: the mesh
    /// arena in DB mode, the VDO item tables in the engine path.</summary>
    private static bool CasterRange(in AlloyRenderer.CasterFacts f, out int idxCount, out uint first, out uint baseVert)
        => AlloyRenderer.DbSlot(f.Slot)
            ? AlloyRenderer.DbCasterRange(f.Slot, out idxCount, out first, out baseVert)
            : TryRange(f.Vdo, f.Item, out idxCount, out first, out baseVert);

    /// <summary>Resolve a VDO item's index range, live out of the item tables. Every consumer re-reads it
    /// rather than caching it because <c>Repack()</c> MOVES items, and a stale index range does not read as an
    /// empty draw -- it reads as another part's geometry. False = nothing drawable there right now.
    /// <para><paramref name="first"/> is in INDICES, not bytes: the byte-offset APIs elsewhere in this repo
    /// multiply the element offset by 2 and the indirect command must not.</para></summary>
    private static bool TryRange(IntPtr vdo, int item, out int idxCount, out uint first, out uint baseVert)
    {
        idxCount = 0; first = 0; baseVert = 0;
        // CX-120: a DB slot has no VDO at all -- its geometry is the mesh arena's, and the columns carry
        // IntPtr.Zero / item -1. Constructing a VdoRef over that read a null pointer and took the client
        // down on the first DB frame that reached the bake.
        if (vdo == IntPtr.Zero || item < 0) return false;
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

    /// <summary>The world-caster bake, driven ahead of the colour pass that samples it. Since #118 it runs
    /// at the frame anchor, BEFORE the colour build -- which is where #302's one-frame record lag comes
    /// from, and why <c>Enroll</c> (the RSP prologue, earlier still) must never move a tenancy stamp for a
    /// slot whose part did not change. Everything below runs inside the #80 bracket.</summary>
    public static void OnPassSlot()
    {
        if (!_armed || _disarmed) return;
        _frameSeq++;   // before the early-outs: a frame that renders nothing must READ as stale, not as fresh
        // The overlay's view readout describes THIS frame, so it has to be zeroed ahead of every early-out
        // below -- a frozen count left over from the last frame that rendered reads as a pass still baking.
        LastViews = 0;
        if (_coverStamp == _frameSeq - 1) _coverFreshFrames++;
        if (_cascadeStamp == _frameSeq - 1 && CascadeCount > 0) _cascFreshFrames++;
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

        // #308 follow-up: the pose solve moves to the FRONT of the frame, so the bake below skins with the
        // same bones the colour pass will draw with -- self-shadow flicker was the two disagreeing by one
        // frame. Before Collect, which reads the bone slots this assigns.
        AlloyRenderer.ShadowPose();
        // Collect FIRST: resolving the world is a per-frame fact, not a property of any one view, and #94/#95
        // submit several views off the one collect. #328: the WORLD is re-collected only on an edge -- a
        // caster-generation or record-epoch bump, a repack or release (both reset the generation), or a
        // frame the bracket skipped (every stamp would read as an appearance). Every other frame walks the
        // standing list for its movers, which is two orders of magnitude fewer than the casters.
        bool fresh = _liveGen == AlloyRenderer.CasterGen && _liveEpoch == AlloyRenderer.RecordEpoch
                     && _collectedAt == _frameSeq - 1;
        if (!fresh)
        {
            FrameZones.Count(_liveGen != AlloyRenderer.CasterGen ? FrameZones.Ev.CollectGen
                           : _liveEpoch != AlloyRenderer.RecordEpoch ? FrameZones.Ev.CollectEpoch : FrameZones.Ev.CollectSkip);
            Collect();
        }
        else if (!Refresh()) { FrameZones.Count(FrameZones.Ev.CollectRefresh); Collect(); }
        if (_dumpMovers) { _dumpMovers = false; DumpMovers(fresh); }

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

        // An Enter failure must not leave the flag promising an upload that never happened -- the colour
        // build would then skip BOTH the solve and the upload and skin against last frame's GPU block.
        // own bracket: engine-context work on both sides (EnvMap build before, sky VAO harvest after) -- see _ctxTicks
        long tCtx = System.Diagnostics.Stopwatch.GetTimestamp();
        bool entered = GlContextService.Enter();
        _ctxTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tCtx;
        if (!entered) { AlloyRenderer.PosedByShadow = false; return; }
        AlloyRenderer.ShadowPoseUpload();   // the GL half of ShadowPose above: this frame's bones, uploaded
        OwnedTextures.DropDeadHandles();        // the colour drain is AFTER this bracket; textures die on area load before both
        Issue.UploadHandlesForBake();   // a handle dropped since the last colour upload must not be sampled below
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
        ReceiverFrame();
        if (sun || (_cubeLights > 0 && FrameInputs.LocalLightCount > 0) || (sun && Weather.CoverWanted))
        {
            if (EnsureCommon() && CasterGather.Ensure()) { EnsureLayered(); EnsureHandleMask(); _gStaged = GatherStage(); }
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
                case PassId.RainCover:
                    CoverPass(focus, sun);
                    break;
            }
        }
        // #307: the queued stale views flush in one layered pass per target, HERE -- the publish arrays the
        // passes wrote above assumed the bakes land. A failed flush publishes nothing for the frame instead:
        // the receiver samples no maps (a one-frame blink, never a lie) and the blanket re-bakes from scratch.
        if (!FlushLayered())
        {
            _layerFlushFails++;
            CascadeCount = 0; CubeCount = 0; CoverRelease();
            Array.Clear(SunDynMask); Array.Clear(CubeDynPage);
            PairShadows.Stand();   // the pair tiles never landed either: no light may be read as paired this frame
            _invalidate = true; _sunScanned = false; _cubeScanned = false;
        }
        else if (_gStaged && _fbo != 0)
        {   // #196: the combined (no-split) static layer, or the dynamic mirror -- never both, or the frozen
            // static copy and the live one compose into a double shadow
            Gl.Enable(Gl.DEPTH_CLAMP);
            GrassField.DrawShadows(_fbo, _depthTex, _mapSize, _sunRebaked, CascadeCount);
            GrassField.DrawShadows(_fbo, SunDynTex, _mapSize, _sunDynRebaked, CascadeCount);
        }
        CoverFiltered();
        Array.Clear(_sunRebaked); Array.Clear(_sunDynRebaked);
        // ...and only now may the frame's invalidation be consumed: every pass that ran reached every one of
        // its views, so each of them recorded this frame's movers against its own slots.
        if ((_sunRan || _cubeRan) && (!_sunRan || _sunScanned) && (!_cubeRan || _cubeScanned))
        { _invalidate = false; _rxPending = false; }
        // #303: the culled-vs-drawn sample -- a latent copy of the last view's survivor counts, read back
        // frames later, so the figure reaches the period line without a readback on the draw path.
        CasterGather.Sample();
        if (_qShadowOn) { _qShadow.End(Gl.TIME_ELAPSED); _qShadowOn = false; }
        LastShadowMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000f / System.Diagnostics.Stopwatch.Frequency;
        _viewSum += LastViews;
        Gl.Disable(Gl.DEPTH_CLAMP);
        tCtx = System.Diagnostics.Stopwatch.GetTimestamp();
        GlContextService.Leave();
        _ctxTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tCtx;
        _ctxFrames++;
    }

    /// <summary>The sun cascades: one concentric fit per layer around the shadow focus, every one of them
    /// baked from the live fit every frame (#187). Cascades are rendered innermost first so a fence stall
    /// costs the FAR ones, whose absence is a missing distant shadow rather than a missing one at the
    /// player's feet.</summary>
    private static void SunPass(Vector3 focus)
    {
        Vector3 dir = Vector3.Normalize(FrameInputs.SunDir);
        SunFocusLive = focus;
        int done = 0, latched = 0;
        bool invalidate = _frameInvalidate;
        _sunRan = true; _sunScanned = true;
        // #306: the split runs only when its array exists -- shadow.sundyn 0 or a refused allocation is the
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
        bool upX = ShadowMath.LatchUpX(ref _sunUpX, dir);
        for (int i = 0; i < _cascades; i++)
        {
            SunDynMask[i] = 0f;
            float radius = ShadowMath.CascadeRadius(i, _cascades, _maxDist);
            CascadeFitRadius[i] = radius;
            float margin = FitMargin();
            // The latched centre, re-latched only when the focus has drifted past the slack. The receiver
            // selects a cascade by testing the published fit itself, so a latched fit needs no other
            // coordination -- the player's own shadow stays in cascade 0 as long as the drift stays well
            // inside the acceptance border, which the slack guarantees by an order of magnitude.
            Vector3 c = new Vector3(_sunFitC[i * 3], _sunFitC[i * 3 + 1], _sunFitC[i * 3 + 2]);
            // Inner cascades are contained in the next one up, so their drift only shifts which RESOLUTION
            // answers -- but past the outermost there is no shadow at all (the receiver returns lit), so a
            // trailing outermost fit opens an unshadowed band ahead of a walking player. It latches on a
            // quarter of the slack: a few extra full-view bakes per minute of walking, no band worth seeing.
            float slack = radius * (i == _cascades - 1 ? SunFitSlack * 0.25f : SunFitSlack);
            if (!_sunFitValid[i]) c = focus;
            else if ((focus - c).LengthSquared() > slack * slack && (_sunLatch == 0 || latched < _sunLatch || i == _cascades - 1))
            { c = focus; latched++; FrameZones.Count(FrameZones.Ev.SunLatch); }
            // depth range only across the fit sphere: the upstream margin is the CULL's (RenderView, InFit), and
            // DEPTH_CLAMP pancakes what stands in it onto the near plane
            Matrix4x4 vp = ShadowMath.DirectionalLightViewProj(dir, c, radius, 0f, _mapSize, upX);
            bool combined = !split || _sunCombined[i];
            // #282 terms 1+2: the fit itself, as ONE compare on the snapped matrix for both modes. The
            // matrix is a pure function of (dir, latched centre, radius, margin, map size), so it covers
            // every raw term the old combined-mode key compared -- and comparing those raw floats is what
            // kept a degraded layer degraded forever: the raw focus moves every frame the snap would have
            // absorbed, so a combined layer could never stop baking and never re-earn the split.
            // A population flip (SetAll <-> SetStatic) re-keys the layer either way.
            bool keyChanged = !_sunBaked[i] || _sunKeyCombined[i] != combined || vp != _sunKeyVp[i];
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
                hasMover = InFit(_liveCx[m] - c.X, _liveCy[m] - c.Y, _liveCz[m] - c.Z,
                                 _liveR[m], dir, radius, radius + margin);
            }
            for (int q = 0; q < _ghostCount && !hasMover; q++)
                hasMover = InFit(_ghostCx[q] - c.X, _ghostCy[q] - c.Y, _ghostCz[q] - c.Z,
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
                _sunBakedViews++; FrameZones.Count(FrameZones.Ev.SunStatic);
                if (split) { _sunWinBaked[i]++; _sunStaticL[i]++; }
                ChargeBake(WhySun, invalidate, keyChanged);
                bool viewOk = RenderView(_depthTex, _mapSize, i, &vp,
                                         dir, c, radius, margin, point: false,
                                         set: combined ? CasterGather.SetAll : CasterGather.SetStatic);
                // A cascade that stalled holds last frame's depth under this frame's key, and the cascades
                // after it were never scanned -- so their mover evidence is missing too. One blanket frame
                // covers both.
                if (!viewOk) { _sunBaked[i] = false; _sunScanned = false; break; }
                _sunBaked[i] = true; _sunRebaked[i] = combined;
                _sunKeyCombined[i] = combined;
                _sunKeyVp[i] = vp;
                // The latch commits only beside a successful bake: a stalled view must not leave the next
                // frame fitting against a centre no map was baked at.
                _sunFitC[i * 3] = c.X; _sunFitC[i * 3 + 1] = c.Y; _sunFitC[i * 3 + 2] = c.Z;
                _sunFitValid[i] = true;
                fixed (float* pub = CascadeVp) *(Matrix4x4*)(pub + i * 16) = vp;
                CascadeTexel[i] = 2f * radius / _mapSize;
                CascadeRange[i] = 2f * radius;
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
                    hasDyn = InFit(_liveCx[m] - c.X, _liveCy[m] - c.Y, _liveCz[m] - c.Z,
                                   _liveR[m], dir, radius, radius + margin);
                }
                // #336: the layer's content is the dynamic SET, which changes on an EVENT in the fit -- a
                // mover, a membership flip, a ghost -- and on a fit matrix it was not baked under. A held-
                // still caster keeps the layer SAMPLED, not re-baked.
                // #196: grass casts into the dynamic layer (its sway is continuous motion), so a grass
                // cascade has movers by definition and re-bakes every frame
                bool grass = GrassField.CastsInto(i);
                hasDyn |= grass;
                if (!hasDyn || !_sunBaked[i]) _sunDynValid[i] = false;
                else if (_dynEvents && !invalidate && !_rxForce && !grass && _sunDynValid[i] && _sunDynVp[i] == vp
                         && !EventInFit(c, dir, radius, margin))
                    SunDynMask[i] = 1f;
                // The cadence ladder: a far cascade's dynamic layer holds one frame under the same fit.
                else if (i >= _sunFull && !invalidate && !_rxForce && _sunDynValid[i] && _sunDynVp[i] == vp && ((_frameSeq + i) & 1) != 0)
                { SunDynMask[i] = 1f; _sunDynSkipped++; }
                else if (RenderView(SunDynTex, _mapSize, i, &vp, dir, c, radius, margin, point: false,
                                    set: CasterGather.SetDynamic, receiver: true))
                { SunDynMask[i] = 1f; _sunDynBakedViews++; _sunDynValid[i] = true; _sunDynVp[i] = vp; _sunDynRebaked[i] = grass; }
                else _sunDynValid[i] = false;
            }
        }
        TraceFit(focus, dir, latched);
        CascadeTex = _depthTex;
        // CascadeCount is the ONLY thing bounding the receiver's read: a stall at cascade i leaves layers
        // >= i holding last frame's depth and CascadeVp[>= i] holding last frame's matrix, and the
        // receiver's loop simply never reaches them. Same invariant covers the uniform upload, which
        // sends only `done` matrices into a mat4[4] and leaves the tail stale on purpose.
        CascadeCount = done;
        if (done > 0) _cascadeStamp = _frameSeq;
    }

    /// <summary>The cascade fit's inputs, logged only when one of them MOVES (<c>shadow.trace</c>). A still
    /// scene changes none of them, so a line here per frame names what the shadows are following; the
    /// <c>still</c> count is the cadence, which is what separates "it alternates every other frame" from "it
    /// drifts".</summary>
    private static void TraceFit(Vector3 focus, Vector3 dir, int latched)
    {
        if (!Shadows.Trace.V || _traceLines >= TraceCap) return;
        var c0 = new Vector3(_sunFitC[0], _sunFitC[1], _sunFitC[2]);
        if ((focus - _traceFocus).LengthSquared() < 1e-10f && (dir - _traceDir).LengthSquared() < 1e-12f
            && (c0 - _traceC0).LengthSquared() < 1e-10f && _traceMovers == _moverCount + _dMoverCount)
        { _traceStill++; return; }
        _traceLines++;
        _log?.Always($"CasterPass: [fit] f={_frameSeq} still={_traceStill} focus=({focus.X:F3},{focus.Y:F3},{focus.Z:F3}) "
                   + $"dir=({dir.X:F4},{dir.Y:F4},{dir.Z:F4}) c0=({c0.X:F3},{c0.Y:F3},{c0.Z:F3}) latched={latched} "
                   + $"cam=({FrameInputs.CameraPos.X:F2},{FrameInputs.CameraPos.Y:F2},{FrameInputs.CameraPos.Z:F2}) "
                   + $"fwd=({FrameInputs.SceneCamFwd.X:F3},{FrameInputs.SceneCamFwd.Y:F3},{FrameInputs.SceneCamFwd.Z:F3}) "
                   + $"fovy={FrameInputs.CamFovy:F1} movers={_moverCount}/{_sMoverCount}/{_dMoverCount} ghosts={_ghostCount} "
                   + $"dyn0={SunDynMask[0]:F0} baked0={_sunBaked[0]} comb0={_sunCombined[0]}");
        _traceFocus = focus; _traceDir = dir; _traceC0 = c0; _traceMovers = _moverCount + _dMoverCount;
        _traceStill = 0;
    }

    private const int TraceCap = 600;
    private static int _traceLines, _traceStill, _traceMovers;
    private static Vector3 _traceFocus, _traceDir, _traceC0;

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
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
        // #322 review: the SAME predicate and slack the receiver-side light cull runs
        // (ShadowMath.SphereInCameraFrustum, self-checked) -- not a second hand-rolled cone. Two divergent
        // answers to "is this light visible" starve a light one of them keeps; one answer cannot drift.
        // A light whose LIT SPHERE misses the frustum shades no visible fragment, so neither its static
        // bake nor its dynamic page can be sampled this frame. The one consumer this lies to is the probe
        // capture (it renders all six directions), which is rare and env-mapped-only.
        bool cull = FrameInputs.HasPerspective && FrameInputs.HasSceneCam;
        Vector3 camPos = FrameInputs.SceneCamPos;
        Vector3 camFwd = cull ? Vector3.Normalize(FrameInputs.SceneCamFwd) : Vector3.UnitX;
        float camFar = FrameInputs.CamFar;
        // Nearest-first by camera distance: the budget is spent on the lights the player is standing in, and
        // a carried torch is by construction among them. Insertion sort, n <= MaxCubes, alloc-free.
        int* order = stackalloc int[MaxCubes];
        float* dist = stackalloc float[MaxCubes];
        int have = 0;
        // shadow.cubedist: a light past it claims no slot (no bake, no page, no fetch); its shadow fades over
        // the last quarter of the way there so the budget's edge never pops
        float cubeDist = Shadows.CubeDist.V, cubeDist2 = cubeDist > 0f ? cubeDist * cubeDist : float.PositiveInfinity;
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
            if (d > cubeDist2 || (have == n && (n == 0 || d >= dist[n - 1]))) continue;
            int k = have < n ? have : n - 1;
            while (k > 0 && dist[k - 1] > d) { order[k] = order[k - 1]; dist[k] = dist[k - 1]; k--; }
            order[k] = i; dist[k] = d;
            if (have < n) have++;
        }

        // the drain reserve: the ranking's tail gives up its slots to lights on the way out
        int reserve = _fadeFrames > 0 ? Math.Min(_fadeSlots, _cubeLights - 1) : 0;
        if (have > _cubeLights - reserve) have = _cubeLights - reserve;
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
        // a slot still fading its last light out is taken (-2): its depth and id stay published until it is dark
        for (int j = 0; j < _cubeLights; j++)
            if (pick[j] < 0 && _cubeFade[j] > 0f && _cubeKeyId[j] != IntPtr.Zero && _cubeBaked[j]) pick[j] = -2;
        for (int s2 = 0, j = 0; s2 < have; s2++)
        {
            if (seated[s2]) continue;
            while (j < _cubeLights && pick[j] != -1) j++;
            if (j >= _cubeLights)
            {   // full budget (the plaza's steady state): a newcomer takes the dimmest draining slot rather
                // than waiting it out, which under 32/32 would leave every arriving light dark for 2x fade.
                // The departing light pops out at its residual strength -- the one pop the fade leaves.
                int steal = -1;
                for (int k = 0; k < _cubeLights; k++)
                    if (pick[k] == -2 && (steal < 0 || _cubeFade[k] < _cubeFade[steal])) steal = k;
                if (steal < 0) break;
                FreeCubeSlot(steal); pick[steal] = s2;
                continue;
            }
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
            if (pick[j] == -2)
            {
                _cubeFade[j] -= _fadeFrames > 0 ? 1f / _fadeFrames : 1f;
                if (_cubeFade[j] > 0f) continue;
            }
            FreeCubeSlot(j);
        }

        // A slot nobody claims is published INACTIVE (far = 0, id = 0) rather than compacted away: the
        // receiver reads far <= near as "lit", so holes in the slot array are free.
        for (int j = 0; j < _cubeLights; j++)
        {
            CubeLight[j * 4 + 3] = 0f; CubeLightId[j] = IntPtr.Zero; CubeDynPage[j] = 0f;
            if (pick[j] != -2 || !_cubeBaked[j]) continue;
            CubeLight[j * 4] = _cubeKeyPos[j * 3]; CubeLight[j * 4 + 1] = _cubeKeyPos[j * 3 + 1];
            CubeLight[j * 4 + 2] = _cubeKeyPos[j * 3 + 2]; CubeLight[j * 4 + 3] = _cubeKeyFar[j];
            CubeLightId[j] = _cubeKeyId[j];
        }

        int done = 0, frameCreatures = 0, nearestCreatures = -1, staticBaked = 0;
        // #241: the transition below is only meaningful about a slot this frame BAKED -- a skipped nearest
        // light stages nothing and would otherwise read as the creature shadow vanishing every still frame.
        bool nearestBaked = false;
        bool invalidate = _frameInvalidate;
        if (CubeNearPlane != _cubeNearSeen) { _cubeNearSeen = CubeNearPlane; invalidate = true; }   // a knob moved the near plane: every cube re-bakes
        bool scannedAll = true;
        // #305: the split runs only when its pool exists -- shadow.dyncount 0 or a refused allocation is the
        // combined bake everywhere, exactly as /shadow split 0 spells it.
        bool split = _split && _cubeDynPages > 0 && CubeDynTex != 0;
        // #322 review: one hidden UNBAKED slot per frame bakes anyway, nearest first -- the warm-up drain.
        // Without it a camera turn finds every hidden slot unbaked at once and the ring smears the burst
        // as visible shadow pop-in; with it the churn is paid gradually while hidden, which is what the
        // pre-cull behaviour paid continuously.
        int warmBudget = 1;
        float* dynBox = stackalloc float[DynSpheres * MoverBoxF];
        CubeDynSphereGen++;

        // #322 review's visibility verdict, hoisted: ONE SphereInCameraFrustum per ranked light, read below
        // where it used to be called inline. The hoist is what lets the pair plan (CX-188 step 4) ask the same
        // question -- a hidden light must not be paired, for the same reason it must not hold a pool page.
        bool* culled = stackalloc bool[MaxCubes];
        for (int rank = 0; rank < have; rank++)
        {
            culled[rank] = false;
            int slot0 = slotOf[rank];
            if (slot0 < 0 || !cull) continue;
            int li0 = order[rank];
            culled[rank] = !ShadowMath.SphereInCameraFrustum(FrameInputs.LocalLightPos[li0],
                                                            FrameInputs.LocalLightRange[li0] * ShadowMath.FrustumCullSlack,
                                                            camPos, camPos + camFwd, FrameInputs.CamFovy,
                                                            FrameInputs.CamAspect, FrameInputs.CamNear, camFar);
        }
        // CX-188 step 4: the PER-MOVER light budget. The candidate lights come in rank order (so the atlas
        // fills from the nearest), every dynamic mover then ranks them at its own position and keeps the
        // lightmap.moverlights strongest, and Commit turns the survivors into pair tiles grouped by slot.
        // Off unless the deferred pass is live, and then a paired light publishes no pool page below.
        PairShadows.Begin(_frameSeq);
        if (PairShadows.On && split)
        {
            for (int rank = 0; rank < have; rank++)
            {
                int slot0 = slotOf[rank];
                if (slot0 < 0 || culled[rank] || rank >= _dynRanks) continue;
                int li0 = order[rank];
                PairShadows.AddLight(slot0, FrameInputs.LocalLightId[li0], FrameInputs.LocalLightPos[li0],
                                     FrameInputs.LocalLightRange[li0]);
            }
            // the same mover population the page claim walks, filtered the same way: a NaN sphere is no
            // mover's, and a still lantern under dyn.min is not worth a tile any more than a page
            for (int q = 0; q < _dMoverCount; q++)
            {
                int m = _dMoverIdx[q];
                if (!float.IsFinite(_liveCx[m] + _liveCy[m] + _liveCz[m] + _liveR[m])) continue;
                if (!_liveCreature[m] && _liveR[m] < Shadows.DynMin.V) continue;
                // ...with the caster's SLOT as its identity: stable while it is enrolled, which is what lets
                // a light's run keep its order and a tile's layer key still match on the next frame.
                PairShadows.AddMover(_liveCx[m], _liveCy[m], _liveCz[m], _liveR[m], _liveSlot[m]);
            }
            PairShadows.Commit();
        }

        for (int rank = 0; rank < have; rank++)
        {
            int slot = slotOf[rank];
            if (slot < 0) continue;   // more lights than slots: the tail of the ranking gets no cube
            int li = order[rank];
            int lightCreatures = 0;
            Vector3 pos = FrameInputs.LocalLightPos[li];
            float far = FrameInputs.LocalLightRange[li];
            IntPtr id = FrameInputs.LocalLightId[li];
            if (!(far > CubeNearPlane)) { _cubeBaked[slot] = false; continue; }   // no reach, nothing worth sampling

            // #241 term 1+2: the light itself. Position, reach and identity are the three things the recorded
            // faces are a function of, so any of them differing is a bake -- as is a slot holding no complete
            // bake at all (a fence timeout, a release, a fresh array).
            // Exact on purpose: a cube face is a 90-degree perspective, so a world-space slack of one texel at
            // reach is far/d texels for a caster at distance d. A light that moves is a roamer (below).
            bool slotChanged = !_cubeBaked[slot] || _cubeKeyId[slot] != id || _cubeKeyFar[slot] != far
                               || _cubeKeyPos[slot * 3] != pos.X || _cubeKeyPos[slot * 3 + 1] != pos.Y
                               || _cubeKeyPos[slot * 3 + 2] != pos.Z;
            // #322 review: the visibility verdict, BEFORE any page claim. Hidden lights must not hold pool
            // pages (they never bake them, so holding one only starves a visible light into the combined
            // fallback -- a six-face re-bake every mover frame), and their split mode must not flap.
            // ...the verdict itself taken in the hoisted pre-loop above, so the pair plan and this share one
            // answer to "is this light visible" (two divergent ones starve a light the other keeps).
            bool off = false, culledHere = false;
            if (culled[rank])
            {
                off = culledHere = true;
                if (_cubeBaked[slot] || warmBudget <= 0) _cubeOffSlots++;
                else { warmBudget--; off = false; }   // the warm-up drain: bake this hidden slot after all
            }
            // A light in motion is a mover by definition, and the same rule applies: continuous motion lives
            // in the dynamic page and must not re-bake the static world every frame. Measured before the
            // page claim, because a roaming light needs a page whether or not any caster near it moves.
            // Off the CULLED verdict, not the drained one: a roaming slot is never baked, so the drain would
            // pick it every frame and bake six page faces nobody sees while every other hidden slot starved.
            float lx = _cubeLastPos[slot * 3] - pos.X, ly = _cubeLastPos[slot * 3 + 1] - pos.Y, lz = _cubeLastPos[slot * 3 + 2] - pos.Z;
            if (_cubeKeyId[slot] != id) _cubeMovedAt[slot] = _cubeMovedPrev[slot] = NeverMoved;
            if (lx * lx + ly * ly + lz * lz > RoamEpsilon * RoamEpsilon || _cubeKeyId[slot] != id)
            {
                _cubeLastPos[slot * 3] = pos.X; _cubeLastPos[slot * 3 + 1] = pos.Y; _cubeLastPos[slot * 3 + 2] = pos.Z;
                if (_cubeKeyId[slot] == id) { _cubeMovedPrev[slot] = _cubeMovedAt[slot]; _cubeMovedAt[slot] = _frameSeq; }
            }
            // The gap term at dynhold 0 would be unsatisfiable (two stamps are never equal): 1 is the floor.
            bool roam = split && !culledHere && rank < _dynRanks && _frameSeq - _cubeMovedAt[slot] <= _dynHold
                        && _cubeMovedAt[slot] - _cubeMovedPrev[slot] <= Math.Max(_dynHold, 1);
            // #305: is a DYNAMIC caster inside this light's reach? That is what claims (and keeps) a pool
            // page -- and, when the pool is dry, what forces the combined fallback below.
            bool hasDyn = roam;
            int boxes = 0;
            if (split && !off && rank < _dynRanks)
                for (int q = 0; q < _dMoverCount; q++)
                {
                    int m = _dMoverIdx[q];
                    if (!float.IsFinite(_liveCx[m] + _liveCy[m] + _liveCz[m] + _liveR[m])) { _nanMovers++; continue; }
                    // a still scene's 20 matrix movers a frame were flames and swaying lanterns (run 9): each
                    // claimed a page under its light and turned that light HOT on every baked fragment in reach
                    if (!_liveCreature[m] && _liveR[m] < Shadows.DynMin.V) continue;
                    float mx = _liveCx[m] - pos.X, my = _liveCy[m] - pos.Y, mz = _liveCz[m] - pos.Z;
                    float mr = far + _liveR[m];
                    if (mx * mx + my * my + mz * mz > mr * mr) continue;
                    hasDyn = true;
                    boxes = AddMoverBox(dynBox, boxes, _liveCx[m], _liveCy[m], _liveCz[m], _liveR[m]);
                }
            WriteDynSpheres(slot, roam ? 0 : boxes, dynBox, pos, far);
            // CX-188 step 4: a light the pair plan claimed needs NO pool page -- its movers are in tiles of
            // their own, and the lane published below says so with a negative. A ROAMING light keeps its page:
            // the pair views were framed on its movers from a position the roamer's own motion invalidates, and
            // a roamer's static faces are cleared, so min() has nothing else to compose with.
            int pairBase = PairShadows.Pairs(slot);
            if (pairBase >= 0 && (off || roam)) { PairShadows.Drop(slot); pairBase = -1; }
            if (pairBase >= 0) hasDyn = false;
            int page = _dynPageOf[slot];
            if (off && page >= 0)
            {   // hidden: the page returns to the pool now; re-entry reclaims one
                _dynPageSlot[page] = -1; _dynPageOf[slot] = -1; page = -1;
            }
            else if (split && hasDyn && page < 0)
            {
                for (int p = 0; p < _cubeDynPages && page < 0; p++)
                    if (_dynPageSlot[p] < 0) { _dynPageSlot[p] = slot; _dynPageOf[slot] = page = p; _dynPageMask[p] = AllFaces; _dynPageValid[p] = false; }
            }
            else if (page >= 0 && (!split || !hasDyn))
            {   // no movers left near this light: the page returns to the pool and the receiver stops
                // sampling it (the publish wipe already left CubeDynPage[slot] at 0)
                _dynPageSlot[page] = -1; _dynPageOf[slot] = -1; page = -1;
            }
            if (page < 0) roam = false;   // pool dry: the light falls through to today's static re-bake
            // Pool exhausted: this slot bakes BOTH halves into the static array every mover frame -- the
            // per-slot fallback to pre-split behaviour. A mode change re-keys the slot, or a mover baked in
            // while combined would outlive the mode. A HIDDEN slot keeps its stored mode: the state nobody
            // sampled must not mark the slot changed.
            bool combined = !split || (hasDyn && page < 0);
            if (off) combined = _cubeCombined[slot];
            else if (_cubeCombined[slot] != combined) { slotChanged = true; _cubeCombined[slot] = combined; }

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

            // Off-screen: skip every bake for this light, static and dynamic. The mover scan above still
            // ran, so a stale verdict is remembered by unbaking the slot -- re-entry then re-bakes once
            // (slotChanged), or sooner through the warm-up drain above. A slot still holding a good bake
            // republishes it; one that does not stays at the INACTIVE wipe.
            if (off)
            {
                if (stale) _cubeBaked[slot] = false;
                if (_cubeBaked[slot])
                {
                    CubeLight[slot * 4] = _cubeKeyPos[slot * 3];
                    CubeLight[slot * 4 + 1] = _cubeKeyPos[slot * 3 + 1];
                    CubeLight[slot * 4 + 2] = _cubeKeyPos[slot * 3 + 2];
                    CubeLight[slot * 4 + 3] = _cubeKeyFar[slot];
                    CubeLightId[slot] = id;
                }
                done++;
                continue;
            }

            if (roam)
            {   // Demoted: the static faces hold NOTHING (min(vs, vd) collapses to the page), the slot is left
                // unbaked so the first still frame re-keys and re-bakes it, and the page below bakes everything.
                _cubeRoamSlots++;
                bool cleared = true;
                for (int f = 0; f < CubeFaces && !_cubeRoam[slot] && cleared; f++)
                    cleared = ClearLayer(CubeTex, _cubeSize, slot * CubeFaces + f);
                _cubeBaked[slot] = false;
                if (!cleared) { scannedAll = false; break; }   // _cubeRoam stays false: the clear is retried
                _cubeRoam[slot] = true;
                CubeLight[slot * 4] = pos.X; CubeLight[slot * 4 + 1] = pos.Y; CubeLight[slot * 4 + 2] = pos.Z;
                CubeLight[slot * 4 + 3] = far;
                CubeLightId[slot] = id;
                done++;
            }
            else if (!stale)
            {   // The six layers still describe this world. Republish the slot unchanged -- the receiver
                _cubeRoam[slot] = false;
                // reads the light's geometry out of CubeLight, and it must keep matching the depth it samples.
                _cubeSkippedSlots++;
                CubeLight[slot * 4] = _cubeKeyPos[slot * 3]; CubeLight[slot * 4 + 1] = _cubeKeyPos[slot * 3 + 1];
                CubeLight[slot * 4 + 2] = _cubeKeyPos[slot * 3 + 2]; CubeLight[slot * 4 + 3] = _cubeKeyFar[slot];
                CubeLightId[slot] = id;
                done++;
            }
            else if (_staticCap > 0 && staticBaked >= _staticCap && _cubeBaked[slot] && !slotChanged && !invalidate && _cubeHeldRun[slot] < StaticHoldMax)
            {   // Held for the cap: the standing page stays published; hadMover = true is what carries the staleness.
                _cubeRoam[slot] = false; _cubeHadMover[slot] = true; _cubeHeldRun[slot]++;
                _cubeHeldSlots++; FrameZones.Count(FrameZones.Ev.CubeHeld);
                CubeLight[slot * 4] = _cubeKeyPos[slot * 3]; CubeLight[slot * 4 + 1] = _cubeKeyPos[slot * 3 + 1];
                CubeLight[slot * 4 + 2] = _cubeKeyPos[slot * 3 + 2]; CubeLight[slot * 4 + 3] = _cubeKeyFar[slot];
                CubeLightId[slot] = id;
                done++;
            }
            else
            {
                _cubeRoam[slot] = false; _cubeHeldRun[slot] = 0;
                _cubeBakedSlots++; staticBaked++; FrameZones.Count(FrameZones.Ev.CubeStatic);
                ChargeBake(WhyCube, invalidate, slotChanged);
                bool whole = true;
                for (int f = 0; f < CubeFaces && whole; f++)
                {
                    Vector3 fwd = ShadowMath.CubeFaceForward(f);
                    Matrix4x4 vp = ShadowMath.PointLightFaceViewProj(pos, f, CubeNearPlane, far);
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
            // #336: ...per FACE, and only the faces an event sits in -- now, or last frame (a mover's old
            // face still shows it). A face with no event in either keeps its depth: a held-still caster's
            // shadow stands, an empty face stays cleared. The page is republished whatever was baked.
            if (page >= 0)
            {
                // From the position the receiver SAMPLES with (CubeLight): the published key, which the texel
                // slack lets differ from pos by under a texel. A roamer publishes pos itself.
                Vector3 dpos = roam ? pos : new Vector3(_cubeKeyPos[slot * 3], _cubeKeyPos[slot * 3 + 1], _cubeKeyPos[slot * 3 + 2]);
                int now = _dynEvents && !roam ? EventFaceMask(dpos, far) : AllFaces;
                bool forced = invalidate || slotChanged || roam || _rxForce;
                int bake = forced ? AllFaces : now | _dynPageMask[page];
                // The cadence ladder: a far light's page holds one frame, carrying its events forward. Keyed on
                // the FORCING terms, not on the mask -- with animated props on every face the mask is all six
                // nearly every frame, and the first cut read only 3,8 of 28 pages held.
                if (!forced && bake != 0 && rank >= _dynFull && _dynPageValid[page] && ((_frameSeq + slot) & 1) != 0)
                {
                    _dynPageMask[page] = bake;
                    CubeDynPage[slot] = page + 1;
                    _cubeDynSkipped++;
                    continue;
                }
                bool dynWhole = true;
                for (int f = 0; f < CubeFaces && dynWhole; f++)
                {
                    if ((bake >> f & 1) == 0) continue;
                    Matrix4x4 vp = ShadowMath.PointLightFaceViewProj(dpos, f, CubeNearPlane, far);
                    dynWhole = RenderView(CubeDynTex, _cubeDynSize, page * CubeFaces + f, &vp,
                                          ShadowMath.CubeFaceForward(f), dpos, far, 0f, point: true,
                                          set: roam ? CasterGather.SetAll : CasterGather.SetDynamic, receiver: true);
                    _cubeDynBakedFaces++;
                }
                _dynPageMask[page] = dynWhole ? now : bake;
                if (!dynWhole) { scannedAll = false; break; }
                _dynPageValid[page] = true;
                CubeDynPage[slot] = page + 1;
                if (bake != 0) { _cubeDynBakedPages++; FrameZones.Count(FrameZones.Ev.CubeDyn); }
            }
            // CX-188 step 4: the PAIR TILES, in place of that page -- whatever the static branch above did,
            // because their whole content is one mover each. The cull is the DIRECTIONAL one round the mover's
            // own sphere (focus = its centre, reach = along = its radius), which is what keeps a tile to the
            // one caster it frames, and the dynamic set is the only population it may admit. The lane goes
            // NEGATIVE once they are queued: that is what makes every `uLocalDynPage[slot] > 0.5` reader treat
            // this light as pageless (it is) and what tells CxPairBase and cx_pairdebt where its run starts.
            // CX-188 CPU wave: ...and NOT every frame any more. A tile whose layer already holds this pair's
            // depth is held exactly as a cube's dynamic face is: keyed on the pair, and re-baked on an EVENT
            // inside its own cull volume -- which is why the event population here is the dynamic page's and
            // not just this tile's own mover (a second mover walking through the tile, a mover leaving the
            // dynamic set, a caster switched off) and why it unions this frame's with the baked frame's.
            if (pairBase >= 0)
            {
                int pn = PairShadows.PairCountOf(slot);
                bool pairWhole = true;
                bool pairForce = invalidate || slotChanged || _rxForce;
                for (int q = 0; q < pn && pairWhole; q++)
                {
                    int pi = pairBase + q;
                    PairShadows.PairView(pi, out Matrix4x4 pvp, out Vector3 at, out float r);
                    Vector3 dir = at - pos;
                    float dl = dir.Length();
                    Vector3 fwd = dl > 1e-4f ? dir / dl : new Vector3(0f, 0f, 1f);
                    // the SAME predicate the sun's fits take, against the same (focus, reach, along) this
                    // tile's own RenderView passes the gather -- so the tile's cull and its staleness test
                    // cannot disagree except in the direction that costs a bake
                    bool evt = EventInFit(at, fwd, r, 0f);
                    if (!PairShadows.TileStale(pi, evt, pairForce)) { PairShadows.TileHeld(pi); continue; }
                    pairWhole = RenderView(PairShadows.Tex, PairShadows.TileSize, pi, &pvp,
                                           fwd, at, r, 0f, point: false, set: CasterGather.SetDynamic);
                    if (pairWhole) PairShadows.TileBaked(pi, evt);
                    _pairBakedTiles++;
                }
                if (!pairWhole) { scannedAll = false; break; }
                CubeDynPage[slot] = -(pairBase + 1);
            }
        }
        // Only a scan that reached every slot may consume the frame's invalidation: the break above leaves
        // the slots after it with no record of this frame's movers.
        _cubeScanned = scannedAll; _cubeRan = true;
        // a slot published under its ranked light this frame fades in; one that is not (unbaked, hidden with
        // no bake) holds, so a newcomer starts dark and rises only once there is depth to show
        for (int j = 0; j < _cubeLights; j++)
        {
            float distFade = 1f;
            if (pick[j] >= 0 && CubeLightId[j] != IntPtr.Zero)
            {
                _cubeFade[j] = MathF.Min(1f, _cubeFade[j] + (_fadeFrames > 0 ? 1f / _fadeFrames : 1f));
                if (cubeDist > 0f) distFade = 1f - SmoothStep(cubeDist * 0.75f, cubeDist, MathF.Sqrt(dist[pick[j]]));
            }
            CubeFade[j] = _cubeFade[j] * distFade;
        }
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

    /// <summary>Cold diagnostic, once per period: the six widest live casters by stored sphere, and how many live
    /// casters belong to a scene that is not a world scene -- the population a whole-map shadow would come from.</summary>
    private static void LogLargestCasters()
    {
        Span<int> top = stackalloc int[6];
        top.Fill(-1);
        int foreign = 0;
        for (int q = 0; q < _liveCount; q++)
        {
            if (AlloyRenderer.CasterNotWorld(_liveSlot[q])) foreign++;
            float r = _liveR[q];
            for (int t = 0; t < top.Length; t++)
            {
                if (top[t] >= 0 && _liveR[top[t]] >= r) continue;
                for (int u = top.Length - 1; u > t; u--) top[u] = top[u - 1];
                top[t] = q;
                break;
            }
        }
        var sb = new System.Text.StringBuilder($"CasterPass: [largest] {_liveCount} live, {foreign} outside a world scene, cover fresh {_coverFreshFrames}/{_frames} frames, cascades fresh {_cascFreshFrames}/{_frames}");
        _coverFreshFrames = 0; _cascFreshFrames = 0;
        foreach (int q in top)
            if (q >= 0) sb.Append("\n    r=").Append(_liveR[q].ToString("F1")).Append(' ').Append(AlloyRenderer.DescribeCasterSlot(_liveSlot[q]));
        _log?.Info(sb.ToString());
    }
    private static int _coverFreshFrames, _cascFreshFrames;

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

        int period = _loggedOnce ? LogEvery : FirstLogAt;
        if (++_frames < period) return;
        _loggedOnce = true;
        LogLargestCasters();
        _log?.Info($"CasterPass: [casters] {CascadeCount}/{_cascades} sun cascades at {_mapSize}px out to " +
                   $"{_maxDist:F0}u" + FitReadout() +
                   $", {(float)_cubeLightsSum / _frames:F1}/{_cubeLights} local cubes at {_cubeSize}px" +
                   $", casting off {AlloyRenderer.OrderCount} Alloy ownership slots" +
                   $", {(float)_cubeBakedSlots / _frames:F1} cube slots re-baked and " +
                   $"{(float)_cubeSkippedSlots / _frames:F1} skipped and " +
                   $"{(float)_cubeOffSlots / _frames:F1} off-screen and {(float)_cubeHeldSlots / _frames:F1} held by the static cap and " +
                   $"{(float)_cubeRoamSlots / _frames:F1} roaming per frame " +
                   $"({(float)_cubeTurnovers / _frames:F2} slot turnovers/frame, fade {_fadeFrames}f) " +
                   // #305: static and dynamic bakes SEPARATELY -- static bakes tracking the view count means
                   // the split bought nothing and the invalidation is too coarse; this line is that reading.
                   $"(split {(_split ? "on" : "OFF -- /shadow split 1")}: " +
                   $"{(float)_cubeBakedSlots / _frames:F1} static / {(float)_cubeDynBakedPages / _frames:F1} " +
                   $"dynamic cube bakes ({(float)_cubeDynBakedFaces / _frames:F1} faces, {(float)_cubeDynSkipped / _frames:F1} pages held by cadence) per frame, dyn pool {DynPagesUsed()}/{_cubeDynPages} pages at " +
                   $"{_cubeDynSize}px = {_cubeDynPages * CubeFaces * (long)_cubeDynSize * _cubeDynSize * 4 / (1024 * 1024)} MB " +
                   $"beside the static array's {_cubeLights * CubeFaces * (long)_cubeSize * _cubeSize * 4 / (1024 * 1024)} MB) " +
                   // CX-188 step 4: the tiles that stood in for those pages; PairShadows.Stats says over how
                   // many lights, and PairDebt.Stats what the screen-space debt then cost.
                   (_pairBakedTiles > 0 ? $"({(float)_pairBakedTiles / _frames:F1} pair tiles BAKED/frame) " : "") +
                   $"(sun {(float)_sunBakedViews / _frames:F1} re-baked, " +
                   $"{(float)_sunSkippedViews / _frames:F1} skipped" +
                   // #306: the static re-bake rate PER CASCADE LAYER is the texel snap's measured reuse
                   // rate -- the number the toroidal-escalation verdict reads. A layer marked C degraded
                   // itself to the combined bake (static half stale essentially every frame), which is
                   // today's behaviour and not a wrong shadow.
                   (SunDynTex != 0
                    ? $"; static per layer {SunLayerReadout()} /frame, " +
                      $"{(float)_sunDynBakedViews / _frames:F1} dynamic bakes/frame ({(float)_sunDynSkipped / _frames:F1} held by cadence), " +
                      $"degraded-to-combined [{SunCombinedReadout()}]"
                    : "; sun split OFF (shadow.sundyn 0)") + ") " +
                   // #307: which submission path the views took, and what it cost.
                   (_layerDial
                    ? _layerProbe > 0 && _progLayer != 0
                      ? $"(layered bake: {(float)CasterGather.LayerDispatches / _frames:F1} dispatches over " +
                        $"{(float)CasterGather.LayerViews / _frames:F1} views/frame, " +
                        $"{_gDrawN} staged draws in {_gGroups} batches ({MaskedBatches()} masked)" +
                        (_layerFlushFails > 0 ? $", {_layerFlushFails} flush FAILURES" : "") + ") "
                      : _layerProbe < 0
                        ? "(layered bake UNAVAILABLE -- no ARB_shader_viewport_layer_array, per-view path) "
                        : ""
                    : "(layered bake OFF -- /shadow layered 1) ") +
                   $"(bakes charged to -- sun: blanket {_bakeWhy[WhySun]}, key {_bakeWhy[WhySun + 1]}, " +
                   $"mover {_bakeWhy[WhySun + 2]}; cube: blanket {_bakeWhy[WhyCube]}, key {_bakeWhy[WhyCube + 1]}, " +
                   $"mover {_bakeWhy[WhyCube + 2]}) " +
                   $"(predicate {(_rebake ? "on" : "OFF -- /shadow rebake 1")}, " +
                   $"{(float)_moverSum / _frames:F1} movers/frame -- matrix {(float)_mvMatrix / _frames:F1} (r mean {(_mvMatrix > 0 ? _mvMatrixR / _mvMatrix : 0f):F2} max {_mvMatrixRMax:F2} m = '{(_mvMatrixRMaxSlot >= 0 ? AlloyRenderer.SlotTexNameDb(_mvMatrixRMaxSlot) : "")}', dynmin {Shadows.DynMin.V:F1}), " +
                   $"bones {(float)_mvBone / _frames:F1}, dangly {(float)_mvDangly / _frames:F1}, " +
                   $"vertex-dirty {(float)_mvVertex / _frames:F1}, appeared {(float)_mvAppear / _frames:F1}, " +
                   $"{(float)_ghostSum / _frames:F1} vanished, " +
                   $"{(float)_noRecordSum / _frames:F1} refused for want of a record" +
                   (_degenSum > 0 ? $", {(float)_degenSum / _frames:F1} DEGENERATE-admitted movers defeating the predicate" : "") +
                   (_nanMovers > 0 ? $", {_nanMovers} NON-FINITE mover sphere(s) kept out of the hot path (must be 0)" : "") + ")" +
                   // ...of how many live casters. Movers are a SUBSET of the live list, never the whole of
                   // it: a still caster still casts. The two numbers meeting is the #241 regression.
                   $" out of {(float)_liveSum / _frames:F1} live " +
                   $"({(float)(_liveSum - _moverSum) / _frames:F1} still)" +
                   // #328: the list is re-collected on a world edge and walked in between. This is the one
                   // number that tells a slow refresh from a churning edge (a fade, a flapping skip) when
                   // Bake reads high: it should be a few per period in a settled area.
                   $", the world re-collected {_collects} of {_frames} frames" +
                   // #336: what the refresh visited against the list it stands over. Tracks the mover count
                   // plus the colour build's changed records; a number near "live" means a source is firing
                   // on every caster (a dirty VDO the whole world shares, the overflow fallback, refreshall 1).
                   $", refresh visited {(float)_visitSum / _frames:F0} of {(float)_liveSum / _frames:F0} casters/frame " +
                   $"({(float)_touchedSum / _frames:F0} records touched by the colour build, dynamic hold {_dynHold} frames)" +
                   (_refreshAll ? " (refreshall 1: every caster)" : "") +
                   $", caster selection in compute over the shared CxCullSphere bounds " +
                   $"(cull {(CasterGather.Mode != 0 ? "on" : "OFF -- /shadow gather 1")})" +
                   (CasterGather.Views > 0
                    ? $" -- {(float)CasterGather.Views / _frames:F1} dispatches/frame over " +
                      $"{_gDrawN} staged draws in {_gGroups} batches ({MaskedBatches()} masked) " +
                      $"({CasterGather.UploadsSkipped}/{CasterGather.UploadsSkipped + CasterGather.Uploads} " +
                      "frame stagings matched the persistent source, nothing uploaded)" +
                      // S8: what a dynamic view's dispatch actually covered, against the staging it used to walk.
                      (_dSrcFrames > 0
                       ? $", dynamic source {(float)_dSrcDraws / _dSrcFrames:F0} of {_gDrawN} draws on " +
                         $"{CasterGather.DynDispatches} of {CasterGather.Views + CasterGather.LayerDispatches} dispatches" +
                         (_dSrcOver > 0 ? $", {_dSrcOver} frames over the {MaxDynSrc} cap (full source)" : "") +
                         (_dSrcHalf > 0 ? $", {_dSrcHalf} frames the movers were over half the staging" : "") +
                         $", audit {_dSrcAudits} frames {_dSrcMiss} miss (must be 0)" +
                         (_dSrcExtra > 0 ? $", {_dSrcExtra} still casters copied into the moving set" : "")
                       : Shadows.DynSrc.V ? ", dynamic source unused this period" : ", dynamic source OFF (/shadow dynsrc 1)")
                    : "") +
                   // #303: the culled-vs-drawn figure -- one view per frame, latently copied and read back
                   // frames later, so it costs no stall on the draw path.
                   (CasterGather.Samples > 0
                    ? $", sampled view kept {(float)CasterGather.SampledKept / CasterGather.Samples:F0}/" +
                      $"{(float)CasterGather.SampledStaged / CasterGather.Samples:F0} staged draws, " +
                      $"{(float)CasterGather.SampledRx / CasterGather.Samples:F0} receiver-culled " +
                      $"(latent sample; {_rxEpochs} camera epochs forced the dyn views)"
                    : "") +
                   // S8: the dynamic source's samples on their own -- averaging ~438 staged draws with ~37k
                   // would describe neither population.
                   (CasterGather.SamplesDyn > 0
                    ? $", on the dynamic source kept {(float)CasterGather.SampledKeptDyn / CasterGather.SamplesDyn:F0}/" +
                      $"{(float)CasterGather.SampledStagedDyn / CasterGather.SamplesDyn:F0} staged draws, " +
                      $"{(float)CasterGather.SampledRxDyn / CasterGather.SamplesDyn:F0} receiver-culled"
                    : "") +
                   // S3-4(c): the bake's own 4.6 bracket, both switches, inside the ms-cpu figure beside it.
                   (_ctxFrames > 0
                    ? $", own 4.6 bracket {(double)_ctxTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _ctxFrames:F3} ms/frame " +
                      "(2 x fence+flush+MakeCurrent; it is separate because the sky VAO harvest either side of it is engine-context)"
                    : "") +
                   $", {(float)_viewSum / _frames:F1} views baked per frame ({LastShadowMs:F2} ms cpu last" +
                   (_qShadow.Measured ? $", {_qShadow.Avg / 1e6:F3} ms GPU avg" : "") + ")" +
                   $", {(float)_mdiCalls / _frames:F1} multidraw-indirect calls/frame " +
                   $"({(float)_mdiSkipped / _frames:F1} skipped: the batch holds nothing the view's set admits)" +
                   // SL1: the per-frame staged-word rewrite walks the refresh's visited set, and one frame in
                   // StageAuditEvery re-reads every staged draw to prove it missed nothing.
                   $", staged-word audit {_gAudits} of {_frames} frames, {_gStaleWords} stale (must be 0)" +
                   $", {_liveCreatures} posed casters live" +
                   // The cutout casters: env-mapped materials the engine calls opaque without ever reading
                   // their diffuse alpha, so their depth draw runs the same 0.2 discard the colour pass does.
                   // Zero here with foliage on screen means they are all falling out at one of the three
                   // terms after it.
                   $", {_texWithAlpha}/{_texQueried} caster textures carry alpha -> " +
                   $"{_submitMasked} alpha-tested depth draws this period" +
                   $", {AlloyRenderer.HandlesZeroed} handle entries zeroed under a dying texture " +
                   $"({Db.TexRegistry.HandlesDropped} of the handles were the DB registry's own) and " +
                   $"{AlloyRenderer.BakeHandleUploads} bakes uploaded the handle table ahead of the colour bracket (since boot)" +
                   (_maskNoTex + _maskNoProg == 0 ? ""
                    : $" ({_maskNoTex} masked casters have no diffuse this frame, " +
                      $"{_maskNoProg} no cutout program -- each casts nothing)") +
                   SunRxReadout());
        _frames = 0; _mdiCalls = 0; _mdiSkipped = 0; _gAudits = 0; _gStaleWords = 0;
        _dSrcFrames = 0; _dSrcDraws = 0; _dSrcOver = 0; _dSrcAudits = 0; _dSrcMiss = 0; _dSrcHalf = 0; _dSrcExtra = 0;
        _ctxTicks = 0; _ctxFrames = 0;
        CasterGather.DynDispatches = 0;
        _cubeLightsSum = 0; _viewSum = 0;
        _liveSum = 0; _visitSum = 0; _touchedSum = 0; _qShadow.Reset();
        _cubeBakedSlots = 0; _cubeSkippedSlots = 0; _cubeOffSlots = 0; _cubeHeldSlots = 0; _cubeRoamSlots = 0; _cubeTurnovers = 0; _moverSum = 0; _ghostSum = 0; _cubeDynBakedPages = 0; _cubeDynBakedFaces = 0; _pairBakedTiles = 0;
        _sunBakedViews = 0; _sunSkippedViews = 0;
        Array.Clear(_sunStaticL); _sunDynBakedViews = 0; _sunDynSkipped = 0; _cubeDynSkipped = 0;
        CasterGather.LayerDispatches = 0; CasterGather.LayerViews = 0;
        Array.Clear(_bakeWhy);
        _mvMatrix = 0; _mvMatrixR = 0; _mvMatrixRMax = 0; _mvMatrixRMaxSlot = -1; _mvDangly = 0; _mvVertex = 0; _mvBone = 0; _mvAppear = 0; _degenSum = 0; _nanMovers = 0; _noRecordSum = 0;
        _submitMasked = 0; _maskNoTex = 0; _maskNoProg = 0; _collects = 0;
        CasterGather.Views = 0; CasterGather.Batches = 0;
        CasterGather.SampledKeptDyn = 0; CasterGather.SampledStagedDyn = 0;
        CasterGather.SamplesDyn = 0; CasterGather.SampledRxDyn = 0;
        CasterGather.SampledKept = 0; CasterGather.SampledStaged = 0; CasterGather.Samples = 0; CasterGather.SampledRx = 0; _rxEpochs = 0;
        CasterGather.UploadsSkipped = 0; CasterGather.Uploads = 0;
    }

    /// <summary>CX-120: the sun RECEIVER's side of the publish, cold and once per period. The one clause that
    /// separates "no cascade was baked" from "the cascades were published and the receiver read none of
    /// them": <see cref="CascadesFresh"/>'s terms spelled out, cascade 0's fit as published, and the
    /// <c>uSunCascades</c>/<c>uSunVp[0]</c> the last pass block actually carried. A publication the receiver
    /// did not take is a gate fault; one it took against an empty map is a bake fault, and nothing else
    /// tells the two apart.
    /// <para><c>published</c> rather than <see cref="CascadesFresh"/>: this runs from <see cref="Lifecycle"/>,
    /// which is past this frame's <c>_frameSeq++</c> and before this frame's bake, so the stamp to compare
    /// against here is the PREVIOUS frame's.</para></summary>
    private static string SunRxReadout()
        => $" | [sunrx] published={_cascadeStamp == _frameSeq - 1} count={CascadeCount} tex={CascadeTex} " +
           $"stamp={_cascadeStamp} seq={_frameSeq} interior={FrameInputs.Interior} hasSun={FrameInputs.HasSun} " +
           $"| c0 baked={_sunBaked[0]} fit={_sunFitValid[0]} mover={_sunHadMover[0]} " +
           $"c=({_sunFitC[0]:F1},{_sunFitC[1]:F1},{_sunFitC[2]:F1}) r={CascadeFitRadius[0]:F1} " +
           $"vpcol3=({CascadeVp[12]:F3},{CascadeVp[13]:F3},{CascadeVp[14]:F3},{CascadeVp[15]:F3}) " +
           $"dyn={SunDynMask[0]:F0} | {AlloyRenderer.SunRxNote()}";

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

    /// <summary>Fold one mover's box into at most <see cref="DynSpheres"/> AABBs (min xyz, max xyz): a new box
    /// while there is room, else the one whose squared extent grows least (which favours small boxes).
    /// Returns the box count.</summary>
    /// <summary>Floats per mover box: min xyz, max xyz, and the lone mover's own radius (-1 once merged).</summary>
    internal const int MoverBoxF = 7;

    private static int AddMoverBox(float* b, int n, float x, float y, float z, float r)
    {
        int at = n;
        if (n == DynSpheres)
        {
            at = 0;   // a NaN mover compares false everywhere: without this it would write one box past the array
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float* e = b + i * MoverBoxF;
                float ux = MathF.Max(e[3], x + r) - MathF.Min(e[0], x - r), uy = MathF.Max(e[4], y + r) - MathF.Min(e[1], y - r),
                      uz = MathF.Max(e[5], z + r) - MathF.Min(e[2], z - r);
                float ex = e[3] - e[0], ey = e[4] - e[1], ez = e[5] - e[2];
                float d = ux * ux + uy * uy + uz * uz - (ex * ex + ey * ey + ez * ez);
                if (d < best) { best = d; at = i; }
            }
            float* t = b + at * MoverBoxF;
            t[6] = -1f;
            t[0] = MathF.Min(t[0], x - r); t[1] = MathF.Min(t[1], y - r); t[2] = MathF.Min(t[2], z - r);
            t[3] = MathF.Max(t[3], x + r); t[4] = MathF.Max(t[4], y + r); t[5] = MathF.Max(t[5], z + r);
            return n;
        }
        float* w = b + at * MoverBoxF;
        w[0] = x - r; w[1] = y - r; w[2] = z - r; w[3] = x + r; w[4] = y + r; w[5] = z + r; w[6] = r;
        return n + 1;
    }

    /// <summary>Metres the hot-path ray test forgives round every mover sphere, before the angular share below.</summary>
    private const float HotPadM = 0.05f;
    /// <summary>How far, in dyn-page texels, the mover-visibility read (cx_localshadow <c>CxMoverVisQ</c>) can
    /// sample off the fragment's own ray: the soft disc's diagonal (sqrt 2), the hardware tap's bilinear texel
    /// (1), the normal offset at its slope ceiling (CX_LOCAL_NORMAL_OFFSET 1 + CX_LOCAL_SLOPE_OFFSET 0.75 x
    /// CX_LOCAL_SLOPE_MAX 2). A texel at major-axis distance m is 2 m / face size, so this is an ANGLE.</summary>
    internal const float HotPadTexels = 1.4143f + 1f + 1f + 0.75f * 2f;

    /// <summary>The slack a mover sphere <paramref name="d"/> metres from its light is published with: everything
    /// the shadow read can reach past the sphere's own cone. It was a flat 0.3 m, three times a rat's radius --
    /// a rat under a torch turned ten times its own shadow's area hot.</summary>
    internal static float HotPad(float d) => HotPadM + 2f * HotPadTexels / _cubeDynSize * d;

    /// <summary>Hot-path census since boot: slots publishing mover spheres, the spheres, the spheres whose light
    /// stands inside their own cone's apex reach (every fragment in the light's range is hot), roaming slots.
    /// Read by the period line and by <c>/alloy bench</c> per step.</summary>
    internal static long HotSlots, HotSpheres, HotEngulf, HotRoam;
    /// <summary>Casters admitted as moving, summed since boot (the bench's per-step "what the scene did").</summary>
    internal static long MoverFrames;

    /// <summary>Publish a slot's mover spheres, each padded by <see cref="HotPad"/> for its distance from the
    /// light: the boxes' bounding spheres, or -- with no box (roaming, or no mover found) -- the light's whole
    /// reach in sphere 0, which is the conservative answer.</summary>
    private static void WriteDynSpheres(int slot, int boxes, float* b, Vector3 pos, float far)
    {
        if (boxes == 0) HotRoam++; else HotSlots++;
        for (int i = 0; i < DynSpheres; i++)
        {
            int k = (slot * DynSpheres + i) * 4;
            if (boxes == 0)
            {
                CubeDynSphere[k] = pos.X; CubeDynSphere[k + 1] = pos.Y; CubeDynSphere[k + 2] = pos.Z;
                CubeDynSphere[k + 3] = i == 0 ? far : -1f;
                continue;
            }
            if (i >= boxes) { CubeDynSphere[k + 3] = -1f; continue; }
            float* e = b + i * MoverBoxF;
            float hx = (e[3] - e[0]) * 0.5f, hy = (e[4] - e[1]) * 0.5f, hz = (e[5] - e[2]) * 0.5f;
            CubeDynSphere[k] = e[0] + hx; CubeDynSphere[k + 1] = e[1] + hy; CubeDynSphere[k + 2] = e[2] + hz;
            // a lone mover's box is its sphere's cube: the corner sphere (r x 1.73) tripled the shadow cone every
            // fragment under a hot light ray-tests, and with it the fragments that pay the hot path
            float r = e[6] >= 0f ? e[6] : MathF.Sqrt(hx * hx + hy * hy + hz * hz);
            float dx = e[0] + hx - pos.X, dy = e[1] + hy - pos.Y, dz = e[2] + hz - pos.Z, dd = dx * dx + dy * dy + dz * dz;
            r += HotPad(MathF.Sqrt(dd));
            CubeDynSphere[k + 3] = r;
            HotSpheres++;
            if (dd <= 2f * r * r) HotEngulf++;
        }
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
        Array.Clear(_texKey); Array.Clear(_texHoles); Array.Clear(_texGraded);
        _texQueried = 0; _texWithAlpha = 0;
        // #241/#302: the recorded faces describe a world that just went away, and the previous-frame slot
        // list names ownership slots the next enrollment is free to hand to different parts. Both have to go,
        // or the first frame in the new area finds every view fresh -- shadows frozen from the old module.
        Array.Clear(_cubeBaked); Array.Clear(_cubeHadMover); Array.Clear(_cubeKeyId);
        Array.Clear(_sunBaked); Array.Clear(_sunHadMover); Array.Clear(_sunFitValid);
        CoverRelease();
        // #305: the pool pages name lights that just went away, and the published page indices with them.
        Array.Clear(_cubeCombined); Array.Clear(CubeDynPage);
        // #306: the sun's half of the same wipe, degrade window included.
        Array.Clear(SunDynMask); Array.Clear(_sunCombined); Array.Clear(_sunWinBaked); Array.Clear(_sunDynValid);
        _sunWinFrames = 0;
        Array.Fill(_dynPageOf, -1);
        if (_dynPageSlot.Length > 0) Array.Fill(_dynPageSlot, -1);
        // ...and the log is once per RELEASE, not once per frame: a lost 4.6 context disables its service
        // permanently, so Lifecycle calls this every frame thereafter and an unguarded line would fill the log.
        bool any = _prevCount > 0 || _liveCount > 0 || CascadeCount > 0 || CubeCount > 0;
        _invalidate = true; _moverCount = 0; _ghostCount = 0;
        _prevCount = 0;
        _liveCount = 0; _liveCreatures = 0; _liveGen = -1;
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

    /// <summary>Does this texture carry coverage? Once answered by a level-0 readback, because
    /// <c>Material::GetIsOpaque</c> @0x4c6190 short-circuits on an env map, the gob alpha and its own memo,
    /// and the internal format lies (the ivy of #126, <c>tcm02_leaf01</c>, is a DXT1 with ALPHA_SIZE 0 and 48%
    /// punchthrough blocks). The same full-resolution question is now put to the decoded image at the upload
    /// funnel, where a sparse cutout -- a railing, a rope bridge -- is still counted texel by texel.</summary>
    internal static bool TexHasAlpha(int tid) => TexHoles(tid) > 0f;
    /// <summary>#363: fraction of level-0 texels that are neither cutout nor opaque -- a glass-imitating
    /// diffuse at alpha ~0.5. At or above <see cref="_maskGraded"/> the caster takes the masked stage whatever
    /// its hole fraction, and the stage's stochastic discard turns that alpha into a partial shadow.</summary>
    internal static float TexGraded(int tid) { TexStat(tid, out float g); return g; }

    /// <summary>Fraction of level-0 texels the cutout drops; 0 = a slab. 1 for a texture the funnel never
    /// scanned: test rather than cast a possible slab. No readback: the answer was computed from the engine's
    /// decoded image at upload (<see cref="TexScan"/>), which is what keeps a radeonsi engine context's
    /// descriptors of the same texture honest.</summary>
    internal static float TexHoles(int tid) => TexStat(tid, out _);
    private static float TexStat(int tid, out float graded)
    {
        graded = 0f;
        if (tid <= 0) return 0f;
        int h = (int)((uint)tid * 2654435761u % TexCacheSize);
        for (int probe = 0; probe < 8; probe++)
        {
            int k = (h + probe) % TexCacheSize;
            if (_texKey[k] == tid) { graded = _texGraded[k]; return _texHoles[k]; }
            if (_texKey[k] != 0) continue;
            float holes = OwnedTextures.TryHoles(tid, out float f, out graded) ? f : 1f;
            _texKey[k] = tid; _texHoles[k] = holes; _texGraded[k] = graded;
            _texQueried++;
            if (holes > 0f) _texWithAlpha++;
            return holes;
        }
        return 1f;
    }

    private const int TexCacheSize = 2048;
    private static readonly int[] _texKey = new int[TexCacheSize];
    private static readonly float[] _texHoles = new float[TexCacheSize], _texGraded = new float[TexCacheSize];
    private static int _texQueried, _texWithAlpha;

    /// <summary>This frame's GL texture id for a masked caster's diffuse, 0 if it does not resolve. Cold
    /// relative to the draw (once per masked caster per collect), and never cached -- see
    /// <see cref="_liveTex"/>.</summary>
    /// <summary>The caster's alpha-mask texture id. A DB caster has no engine material at all
    /// (<c>_oMat</c> is <c>IntPtr.Zero</c>), so its id comes off the draw recipe instead -- calling the
    /// engine's GetTexture on a null material took the client down the moment arena casters started
    /// staging.</summary>
    private static int MaskTid(in AlloyRenderer.CasterFacts f)
    {
        if (AlloyRenderer.IsArenaVao(f.Vao)) return AlloyRenderer.DbCasterTid(f.Slot);
        if (_getTexture == null || f.Mat == IntPtr.Zero) return 0;
        return AurTex.Tid((IntPtr)_getTexture(f.Mat, 0));
    }

    /// <summary>One-shot 4.6-side setup shared by both views: the depth program and the FBO every layer is
    /// attached into. Any failure disarms the pass by name rather than leaving it silently drawing nothing.</summary>
    private static bool EnsureCommon()
    {
        if (_glFailed || _progFailed) return false;
        if (_prog == 0)
        {
            if (Gl.MultiDrawElementsIndirect == null || Gl.BindBufferBase == null
                || Gl.GenFramebuffers == null || Gl.CopyImageSubData == null)
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
                         + "(regenerate the override with /alloy shaderdump to get it back).");
                p = GlProgram.Graphics(CastVs, CastFs, _log, "CasterPass depth program (embedded)", content: false);
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
                _locMaskCut = GlProgram.Loc(_progMask, "uAlphaCut"); _locMaskDither = GlProgram.Loc(_progMask, "uDither"); _locMaskEnv = GlProgram.Loc(_progMask, "uEnvAlpha");
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
            _depthTex = DepthArray(Gl.TEXTURE_2D_ARRAY, _mapSize, _cascades + CoverLayers);   // #617: the cover map rides the last layer
            if (_depthTex == 0) return false;   // DepthArray already disarmed by name
            Array.Clear(_sunBaked);   // #282: fresh storage holds no bake, whatever the keys say
            _coverBaked = false;
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
        _locLayerMaskCut = GlProgram.Loc(pm, "uAlphaCut"); _locLayerMaskDither = GlProgram.Loc(pm, "uDither"); _locLayerMaskEnv = GlProgram.Loc(pm, "uEnvAlpha");
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
        bool first = true;
        // The stale layers clear FIRST, one attach per view: a layered attachment clears every layer at
        // once, fresh ones included, so the per-layer loop here is correctness rather than thrift. The
        // target's state (FBO, viewport, buffers, depth state) is set once on the first view; the rest
        // attach and clear, two calls each instead of a dozen.
        for (int k = 0; k < n; k++)
        {
            if (_qTex[k] != tex) continue;
            size = _qSize[k];
            if (first) { if (!BeginTarget(tex, size, _qLayer[k])) return false; first = false; continue; }
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, tex, 0, _qLayer[k]);
            Gl.Clear(Gl.DEPTH_BUFFER_BIT);
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
            int vn = 0, sets = 0;
            fixed (float* vs = _viewStage)
                while (k2 < n && vn < chunk)
                { if (_qTex[k2] == tex) { sets |= 1 << _qSet[k2]; StageView(vs, vn++, k2); } k2++; }
            if (vn == 0) break;
            bool ok;
            fixed (float* vs = _viewStage)
                ok = CasterGather.DispatchLayer(vs, vn, sets == 1 << CasterGather.SetDynamic);
            if (!ok) { Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0); return false; }
            DrawLayered(vn, sets);
            CasterGather.End();
        }
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        return true;
    }

    /// <summary>One queued view into the chunk's staged table, in the layout the dispatch and the layered
    /// vertex stage both read (mat4 vp; focus/reach; dir/along; spare/point/set/layer).</summary>
    private static void StageView(float* dst, int vi, int k)
    {
        float* r = dst + (long)vi * CasterGather.ViewFloats;
        fixed (Matrix4x4* m = &_qVp[k]) Buffer.MemoryCopy(m, r, 64, 64);
        r[16] = _qFocus[k].X; r[17] = _qFocus[k].Y; r[18] = _qFocus[k].Z; r[19] = _qReach[k];
        r[20] = _qDir[k].X; r[21] = _qDir[k].Y; r[22] = _qDir[k].Z; r[23] = _qAlong[k];
        r[24] = _qRx[k] ? 1f : 0f;   // #488: the receiver cull applies to this view
        r[25] = _qPoint[k] ? 1f : 0f;
        r[26] = _qSet[k];
        r[27] = _qLayer[k];
    }

    /// <summary>The layered pass's batch loop: <see cref="GatherSubmit"/>'s state walk minus the per-view
    /// uniforms, one indirect-count draw per batch for ALL the chunk's views.</summary>
    /// <param name="sets">The union of the chunk's view SETS, a bit per <c>CasterGather.Set*</c>: a batch none
    /// of them can admit a caster from is not issued at all (SL2).</param>
    private static void DrawLayered(int vn, int sets)
    {
        Gl.ActiveTexture(Gl.TEXTURE0);
        int boundVao = -1, boundMode = -1, boundTex = -1;
        float boundCut = -1f;
        for (int g = 0; g < _gGroups; g++)
        {
            if (BatchIdle(g, sets)) { _mdiSkipped++; continue; }
            int cut0 = _gTex[g], mode = _gH[g] ? 2 : cut0 != 0 ? 1 : 0;
            if (mode != boundMode)
            {
                boundMode = mode;
                Gl.UseProgram(mode == 0 ? _progLayer : mode == 1 ? _progLayerMask : _progLayerMaskH);
                if (mode == 1) Gl.Uniform1i(_locLayerMaskTex, 0);
                boundCut = -1f;
            }
            if (mode != 0 && _gCut[g] != boundCut)
            {
                boundCut = _gCut[g]; Gl.Uniform1f(mode == 1 ? _locLayerMaskCut : _locLayerMaskHCut, boundCut);
                Gl.Uniform1i(mode == 1 ? _locLayerMaskDither : _locLayerMaskHDither, _maskDither);
                Gl.Uniform1i(mode == 1 ? _locLayerMaskEnv : _locLayerMaskHEnv, boundCut == EnvAlphaDiscard ? 1 : 0);
            }
            if (mode == 1 && cut0 != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)cut0); boundTex = cut0; }
            if (_gVao[g] != boundVao) { Gl.BindVertexArray(AlloyRenderer.CasterVao(_gVao[g])); boundVao = _gVao[g]; }
            CasterGather.DrawLayer(BatchMode(g), g, _gStart[g], _gLen[g], vn);
            _mdiCalls++;
        }
    }

    /// <summary>A depth array sampled with hardware PCF: LINEAR + depth-compare IS the filter, one bilinear
    /// 2x2 comparison per fetch at no more cost than a plain sample. Same setup for the 2D cascade array and
    /// the cube array -- only the target and the layer count differ.</summary>
    /// <summary>0 = the storage was refused, and the pass has already disarmed itself by name.</summary>
    private static uint DepthArray(uint target, int size, int layers)
    {
        uint t; Gl.CreateTextures(target, 1, &t);
        Gl.DrainErrors();   // an earlier pass's error must not be read as this allocation's
        Gl.TextureStorage3D(t, 1, Gl.DEPTH_COMPONENT24, size, size, layers);
        if (Gl.GetError() != 0)
        {
            Gl.DeleteTextures(1, &t);
            Disarm($"a {size}x{size}x{layers} depth array could not be allocated (out of video memory?)");
            return 0;
        }
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_COMPARE_MODE, (int)Gl.COMPARE_REF_TO_TEXTURE);
        Gl.TextureParameteri(t, Gl.TEXTURE_COMPARE_FUNC, (int)Gl.LEQUAL);
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
                if (!CasterRange(in f, out int idxCount, out uint first, out uint baseVert)) continue;
                _liveSlot[live] = slot;
                _liveCx[live] = f.Cx; _liveCy[live] = f.Cy; _liveCz[live] = f.Cz;
                _liveR[live] = f.R;
                _liveCreature[live] = f.Skinned || AlloyRenderer.SlotCreature(slot);
                _liveIdxCount[live] = idxCount;
                _liveFirst[live] = first;
                _liveBaseVert[live] = baseVert;
                _liveVao[live] = f.Vao; _livePrim[live] = f.Prim; _liveItem[live] = f.Item;
                // A skinned caster is never alpha-tested: the cutout stage has no skinning branch, so a
                // masked skinmesh would punch its holes out of a BIND pose. Same set the capture cast solid.
                _liveTex[live] = !f.Skinned && AlloyRenderer.VdoHasUv(f.Vao) ? MaskTid(in f) : 0;
                // Review: the triage counter lost its writer in the rework -- a masked-eligible caster whose
                // diffuse did not resolve THIS frame casts solid, and this is the number that says how often.
                if (_liveTex[live] == 0 && !f.Skinned && AlloyRenderer.VdoHasUv(f.Vao)) _maskNoTex++;
                _liveCut[live] = f.EnvAlpha ? EnvAlphaDiscard : CutoutDiscard; _liveBlend[live] = f.Blended;
                // Review: a caster ABSENT last frame is a mover on return, whatever its record says -- a
                // transient TryRange or VAO failure longer than the one-frame ghost+hadMover cover would
                // otherwise re-appear into views that never re-bake its shadow back.
                // #336: present last frame = in a list that stood through last frame (see _listFrame).
                bool appeared = _collectedAt != _frameSeq - 1 || _liveStamp[slot] < _lastCollectFrame;
                Admit(live, slot, f.Skinned, appeared, f.RecMoved, f.Dangly, VertexDirty(f.Vao, f.Item), _liveCreature[live]);
                _liveIdx[slot] = live; _liveIdxSeq[slot] = _collectSeq + 1;
                _liveOff[live] = false; _liveGenOf[live] = AlloyRenderer.SlotEnrollGen(slot);
                live++;
            }
            // ...and pass 0 is the whole creature population, so the split #100 needs falls out of the walk.
            if (pass == 0) _liveCreatures = live;
        }
        _liveCount = live;
        _visitSum += live;
        _appN = 0; _offN = 0; _offOverflow = false; _listVer++;
        BucketByVao();
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
            Ghost(gx, gy, gz, gr);
        }
        _noRecordSum += AlloyRenderer.CasterNoRecord;
        AlloyRenderer.CasterNoRecord = 0;
        RecordProbe();
        _ghostSum += _ghostCount;
        Account();
        Array.Copy(_liveSlot, _prevSlots, live);
        _prevCount = live;
        _liveGen = AlloyRenderer.CasterGen; _liveEpoch = AlloyRenderer.RecordEpoch;
        _lastCollectFrame = _frameSeq; FrameZones.Count(FrameZones.Ev.Collect);
        _collectSeq++; _collects++;
    }

    /// <summary>A mover at a last-known sphere: invalidates the views it stood in. Past the cap, or with a
    /// degenerate sphere, the blanket takes over for the frame.</summary>
    private static void Ghost(float gx, float gy, float gz, float gr)
    {
        if (_ghostCount >= MaxGhosts || !(gr > 0f)) { _invalidate = true; return; }
        _ghostCx[_ghostCount] = gx; _ghostCy[_ghostCount] = gy; _ghostCz[_ghostCount] = gz;
        _ghostR[_ghostCount] = gr;
        _ghostCount++;
    }

    /// <summary>#336: counting sort of the live indices by VAO -- three passes over the list, paid once per
    /// collect so a dirty VDO's casters are a range rather than a walk.</summary>
    private static void BucketByVao()
    {
        Array.Clear(_vaoStart, 0, _vaoStart.Length);
        for (int i = 0; i < _liveCount; i++) _vaoStart[_liveVao[i] + 1]++;
        for (int v = 0; v < MaxVdos; v++) _vaoStart[v + 1] += _vaoStart[v];
        for (int i = 0; i < _liveCount; i++) _vaoMembers[_vaoStart[_liveVao[i]]++] = i;
        for (int v = MaxVdos; v > 0; v--) _vaoStart[v] = _vaoStart[v - 1];
        _vaoStart[0] = 0;
    }

    /// <summary>The per-caster predicate, ONE function for the full collect and the standing-list refresh so
    /// the two cannot classify a caster differently. Counted by REASON, not just totalled: "no improvement"
    /// and "every caster is a mover" look identical in a skip count, and which term saturates decides where
    /// the next fix goes. A still caster is STILL A CASTER -- it stays in the list and bakes; it merely does
    /// not invalidate the views it stands in.
    /// <para>#305: dynamic membership is motion, not appearance -- a prop that pops in still is a static-set
    /// event, not a mover. A membership flip in either direction is also a static event: the static bake
    /// must shed a caster that starts moving and adopt one that stops, both within one predicate cycle.
    /// <paramref name="appeared"/> forces the event (err toward stale: the stamp says nothing trustworthy
    /// about a caster that was not collected last frame).</para></summary>
    private static void Admit(int live, int slot, bool skinned, bool appeared, bool recMoved, bool dangly,
                              bool vDirty)
        => Admit(live, slot, skinned, appeared, recMoved, dangly, vDirty, creature: skinned);
    /// <summary><paramref name="creature"/>: in the dynamic set whether or not it moved this frame (its page
    /// stays claimed, so a still character keeps its shadow on a baked floor), but a mover -- a face
    /// re-bake -- only on real motion: run 18 admitted every creature part as a mover every frame and the
    /// pool re-baked 20-40 faces a frame with nothing moving (shadows GPU 1.1-1.8 ms, 60 fps).</summary>
    private static void Admit(int live, int slot, bool skinned, bool appeared, bool recMoved, bool dangly,
                              bool vDirty, bool creature)
    {
        // A dangly's VDO is committed every frame the solve runs, so vertex-dirty says nothing about it;
        // its own measured verdict (dangly) does. Here, so both Collect and Refresh agree.
        if (AlloyRenderer.SlotIsDangly(slot)) vDirty = false;
        bool mover = true;
        if (skinned) _mvBone++;
        else if (appeared) { _mvAppear++; FrameZones.Count(FrameZones.Ev.Appeared); }
        else if (recMoved) { _mvMatrix++; _mvMatrixR += _liveR[live]; if (_liveR[live] > _mvMatrixRMax) { _mvMatrixRMax = _liveR[live]; _mvMatrixRMaxSlot = slot; } }
        else if (dangly) _mvDangly++;
        else if (vDirty) _mvVertex++;
        else mover = false;
        if (mover) { _moverIdx[_moverCount++] = live; MoverFrames++; }
        // #336: _dynStamp is the frame the caster last MOVED; it stays dynamic for _dynHold frames after.
        bool moving = skinned || dangly || recMoved || vDirty;
        int last = _dynStamp[slot];
        // the same rule as dyn below, or a creature part with a stale stamp reads as a set flip EVERY frame:
        // run 19 re-baked 4.8 static cube slots a frame off those events (shadows GPU 7.9 ms, 47 fps)
        bool wasDyn = !appeared && (creature || _frameSeq - 1 - last <= _dynHold);
        if (moving) { _dynStamp[slot] = _frameSeq; last = _frameSeq; }
        bool dyn = creature || _frameSeq - last <= _dynHold;
        _liveDyn[live] = dyn;
        if (dyn) _dMoverIdx[_dMoverCount++] = live;
        if (appeared || dyn != wasDyn) { _sMoverIdx[_sMoverCount++] = live; if (!appeared) FrameZones.Count(FrameZones.Ev.DynFlip); }
        _liveStamp[slot] = _frameSeq;
    }

    /// <summary>#336: <c>/shadow movers</c> -- one frame's movers and static-set events grouped by diffuse
    /// texture and reason, so "200 matrix movers a frame" has names. Cold and one-shot; allocates.</summary>
    private static bool _dumpMovers;
    private static void DumpMovers(bool refreshed)
    {
        var groups = new Dictionary<string, int>();
        void Count(string k) { groups[k] = groups.TryGetValue(k, out int c) ? c + 1 : 1; }
        for (int q = 0; q < _moverCount; q++)
        {
            int m = _moverIdx[q], slot = _liveSlot[m];
            string why = m < _liveCreatures ? "bones" : AlloyRenderer.SlotRecMoved(slot) ? "matrix"
                         : AlloyRenderer.SlotDangly(slot) ? "dangly" : AlloyRenderer.VdoDirty(_liveVao[m]) ? "vertex-dirty" : "appeared";
            Count($"{why} {AlloyRenderer.SlotTexNameDb(slot)}");
        }
        for (int q = 0; q < _sMoverCount; q++) Count($"STATIC-EVENT {AlloyRenderer.SlotTexNameDb(_liveSlot[_sMoverIdx[q]])}");
        var top = new List<KeyValuePair<string, int>>(groups);
        top.Sort((a, b) => b.Value.CompareTo(a.Value));
        var sb = new System.Text.StringBuilder();
        sb.Append($"CasterPass: [movers] frame {_frameSeq} ({(refreshed ? "refresh" : "collect")}): {_moverCount} movers, " +
                  $"{_sMoverCount} static-set events, {_dMoverCount} dynamic, {_ghostCount} ghosts; by texture:");
        for (int i = 0; i < top.Count && i < 24; i++) sb.Append($"\n    {top[i].Value,5}  {top[i].Key}");
        _log?.Info(sb.ToString());
    }

    /// <summary>#328: the frame's collect when the world did not change -- the list stands, and only what can
    /// move WITHOUT a <see cref="AlloyRenderer.CasterGen"/> edge is re-read: the mover levels, the per-VDO
    /// dirty set, and the spheres. Nothing here derefs the engine; the index ranges stand because
    /// both edges that move them (a repack, a re-seated enrollment) drop the list. False = a bone slot was
    /// assigned or reclaimed since the collect, which moves a caster between the two halves of the list; the
    /// caller re-collects; the visit list is gathered before any admission, so nothing was written.
    /// <para>No appearances and no ghosts by construction: the membership is the collect's, and a membership
    /// change is a generation bump.</para></summary>
    /// <para>#336: ...and only the casters whose answer CAN differ from last frame's are visited: those on a
    /// VDO the engine wrote, those whose record changed in the last colour build (the sphere and the mover
    /// level are written on that edge and no other -- <c>AlloyRenderer.CasterTouchedSlot</c>), and last
    /// frame's dynamic set, which is where a caster is seen to stop. Everything else keeps last frame's
    /// verdict untouched: not a mover, not dynamic, in the list. The visit list is gathered and bone-checked
    /// in full before the first admission, so a bail leaves no stamp behind. <c>/shadow refreshall 1</c> is
    /// the A/B.</para></summary>
    private static bool Refresh()
    {
        _visitN = 0;
        if (_refreshAll || AlloyRenderer.CasterTouchedOverflow)
        {
            for (int i = 0; i < _liveCount; i++) if (!Mark(i)) return false;
        }
        else
        {
            for (int v = 0; v < MaxVdos; v++)
                if (AlloyRenderer.VdoDirty(v))
                    for (int m = _vaoStart[v]; m < _vaoStart[v + 1]; m++) if (!Mark(_vaoMembers[m])) return false;
            for (int k = 0, n = AlloyRenderer.CasterTouchedCount; k < n; k++)
            {
                int slot = AlloyRenderer.CasterTouchedSlot(k);
                if (_liveIdxSeq[slot] == _collectSeq && !Mark(_liveIdx[slot])) return false;
            }
            for (int q = 0; q < _dPrevCount; q++) if (!Mark(_dPrevIdx[q])) return false;
        }
        if (_offOverflow) return false;
        for (int q = 0; q < _offN; q++) if (!Mark(_offQ[q])) return false;
        _offN = 0;
        // An appended entry is outside the VAO buckets, so the vertex-dirty visit is the one it would miss;
        // its on-flip and its dynamic set reach it through the touched list and last frame's set like any other.
        for (int q = 0; q < _appN; q++) { int i = _appended[q]; if (AlloyRenderer.VdoDirty(_liveVao[i]) && !Mark(i)) return false; }
        _moverCount = 0; _ghostCount = 0; _dMoverCount = 0; _sMoverCount = 0;
        for (int v = 0; v < MaxVdos; v++) _vDirty[v] = AlloyRenderer.VdoDirty(v);
        _visitSum += _visitN; _touchedSum += AlloyRenderer.CasterTouchedCount;
        for (int k = 0; k < _visitN; k++)
        {
            int i = _visitIdx[k], slot = _liveSlot[i];
            // SL1: the visited set IS the set whose staged words can move -- both of them are written below
            // and nowhere else on a standing list.
            NoteStagedWord(i);
            if (!AlloyRenderer.CastOk(slot) || _liveGenOf[i] != AlloyRenderer.SlotEnrollGen(slot))
            {   // turned off since the collect: a ghost at its last sphere, and the entry stays, drawing nothing
                if (!_liveOff[i]) { _liveOff[i] = true; Ghost(_liveCx[i], _liveCy[i], _liveCz[i], _liveR[i]); }
                continue;
            }
            bool back = _liveOff[i];
            _liveOff[i] = false;
            // The sphere is written on any record change -- and, for a matrix-less part, from bbox terms a
            // kept enrollment may rewrite without a generation bump.
            AlloyRenderer.MoverSphere(slot, out _liveCx[i], out _liveCy[i], out _liveCz[i], out _liveR[i]);
            Admit(i, slot, i < _liveCreatures, appeared: back, AlloyRenderer.SlotRecMoved(slot),
                  AlloyRenderer.SlotDangly(slot), VertexDirty(_liveVao[i], _liveItem[i]), _liveCreature[i]);
        }
        Account();
        return true;
    }

    /// <summary>Put one caster on the frame's visit list, once, whichever source named it. False = its bone
    /// slot was assigned or reclaimed since the collect, which moves it between the two halves of the list
    /// -- the one flip the touched list does report (feat.x changes), and a skinned caster is dynamic and so
    /// revisited every frame besides.</summary>
    private static bool Mark(int i)
    {
        if (_liveVisit[i] == _frameSeq) return true;
        _liveVisit[i] = _frameSeq;
        if (AlloyRenderer.SlotSkinned(_liveSlot[i]) != (i < _liveCreatures)) return false;
        _visitIdx[_visitN++] = i;
        return true;
    }

    /// <summary>The period sums a collect or a refresh leaves behind, and the frame stamp that says the
    /// list is current.</summary>
    private static void Account()
    {
        _liveSum += _liveCount;
        _moverSum += _moverCount;
        _degenSum += AlloyRenderer.CasterDegenerate;
        AlloyRenderer.CasterDegenerate = 0;
        _collectedAt = _frameSeq;
        Array.Copy(_dMoverIdx, _dPrevIdx, _dMoverCount);
        _dPrevCount = _dMoverCount;
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
                                   bool point, int set = CasterGather.SetAll, bool receiver = false)
    {
        // zeroed ahead of every early-out: a view that bakes nothing staged no creatures, and a stale count
        // would be re-added per cube face
        _submitCreatures = 0;
        if (!_gStaged) return false;
        LastViews++;
        if (!CasterGather.Active) return ClearLayer(tex, size, layer);   // an EMPTY world still bakes: the clear is the bake
        // #307: under the layered bake a stale view is QUEUED, not submitted -- the flush after the pass
        // loop renders every queued view of a target in one layered pass. The optimistic true is covered by
        // the flush-failure handling, which publishes nothing for the frame. An overflowing queue renders
        // inline rather than dropping a view.
        if (LayeredActive && _qN < MaxBakeViews)
        {
            _submitCreatures = _gCreatures;
            _qTex[_qN] = tex; _qSize[_qN] = size; _qLayer[_qN] = layer; _qVp[_qN] = *vp;
            _qFocus[_qN] = focus; _qDir[_qN] = lightDir; _qReach[_qN] = reach; _qAlong[_qN] = reach + margin;
            _qPoint[_qN] = point; _qSet[_qN] = set; _qRx[_qN] = receiver;
            _qN++;
            return true;
        }
        return GatherSubmit(tex, size, layer, vp, lightDir, focus, reach, margin, point, set, receiver);
    }

    /// <summary>#674: the lightmap bake's scratch cube -- the STATIC set's depth from one light, six views
    /// submitted synchronously into <paramref name="tex"/> (a one-cube cube array, <paramref name="size"/> a
    /// side) through the same gather the frame's cubes use, filtered to still casters. Every static light is
    /// baked shadowed this way, the ones past the cube ranking included (#666). False when the shadow bracket
    /// staged nothing this frame or a view refused; the caller leaves the light for a later frame.</summary>
    internal static bool BakeStaticCube(uint tex, int size, Vector3 pos, float near, float far)
    {
        near = MathF.Max(near, CubeNear);
        if (!_gStaged || tex == 0 || !(far > near)) return false;
        for (int f = 0; f < CubeFaces; f++)
        {
            Matrix4x4 vp = ShadowMath.PointLightFaceViewProj(pos, f, near, far);
            bool ok = CasterGather.Active
                ? GatherSubmit(tex, size, f, &vp, ShadowMath.CubeFaceForward(f), pos, far, 0f, true, CasterGather.SetStatic, false)
                : ClearLayer(tex, size, f);
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>The target textures <see cref="BeginTarget"/> has verified complete -- the sun array, the
    /// static cubes and the dyn pool interleave per view, so one latch would re-check nearly every attach.
    /// A context loss recreates every name; a stale entry costs one extra check. Ring-replaced when full.</summary>
    private static readonly uint[] _fboChecked = new uint[4];
    private static int _fboCheckedAt;
    private static bool FboChecked(uint tex)
    {
        for (int i = 0; i < _fboChecked.Length; i++) if (_fboChecked[i] == tex) return true;
        _fboChecked[_fboCheckedAt] = tex; _fboCheckedAt = (_fboCheckedAt + 1) % _fboChecked.Length;
        return false;
    }

    /// <summary>Clear one depth layer to "nothing casts" without a bake: the static faces of a roaming light.</summary>
    private static bool ClearLayer(uint tex, int size, int layer)
    {
        if (!BeginTarget(tex, size, layer)) return false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        return true;
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
        // Completeness is a property of the attachment's texture, not of the layer: checked on the first
        // attach of each target (and every view under alloy.gldebug=1), not ~300 times a frame. Mesa charged
        // 'about twelve calls per view including CheckFramebufferStatus' (mesa-amd-render-defects.md).
        if ((!FboChecked(tex) || GlContextService.Debug)
            && Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
        { Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0); Disarm("the caster depth framebuffer is incomplete"); return false; }
        Gl.Viewport(0, 0, size, size);
        // clamp only the directional maps: a point cube's near plane IS the self-shadow skip the lightmap bake clips at
        if (tex == _depthTex || tex == SunDynTex) Gl.Enable(Gl.DEPTH_CLAMP); else Gl.Disable(Gl.DEPTH_CLAMP);
        // #302: the depth stages read ALLOY's per-draw records and ALLOY's bone block, at Alloy's own binding
        // points. Re-bound per view (per flush target on the layered path) rather than once per frame because
        // the colour pass is entitled to rebind both between frames, and a bake reading a buffer that has
        // since been re-pointed is a world baked from someone else's matrices -- which renders, and renders wrong.
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindDraw, AlloyRenderer.DrawRecordBuf);
        if (AlloyRenderer.BoneBlockBuf != 0)
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindBones, AlloyRenderer.BoneBlockBuf);
        if (AlloyRenderer.TexHandleBuf != 0)
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, AlloyRenderer.BindTexH, AlloyRenderer.TexHandleBuf);
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
    /// <summary>SL1: per LIVE entry, its slot in the standing staging, or -1 when the staging does not hold it
    /// (no VAO, a blended caster the mask stage cannot take, <c>/shadow mask -1</c>). The reverse of
    /// <see cref="_gLive"/>, which is what lets the per-frame word rewrite visit the CHANGED entries instead of
    /// all 37k staged ones.</summary>
    private static readonly int[] _gAt = new int[MaxCasters];
    /// <summary>SL1: live entries whose two per-frame staged words may differ from the staging. Fed by the
    /// refresh's visited set -- the only walk that can move them -- and ACCUMULATED, because a frame with no
    /// light to bake for refreshes without re-staging. Deduped by <see cref="_gDirtyMark"/>, so it cannot
    /// exceed the live count and has no overflow case.</summary>
    private static readonly int[] _gDirty = new int[MaxCasters];
    private static readonly bool[] _gDirtyMark = new bool[MaxCasters];
    private static int _gDirtyN;
    /// <summary>SL1's audit: 1 frame in <see cref="StageAuditEvery"/> re-reads every staged entry and counts
    /// the ones the changed-set walk left out of date (and fixes them). MUST read 0 -- a miss is a caster
    /// baking at another frame's index count, or in the wrong half of the static/dynamic split.
    /// <c>/shadow refreshall 1</c> marks every entry, which is the whole-list A/B over the same machinery.</summary>
    private static long _gStaleWords, _gAudits;
    private const int StageAuditEvery = 64;

    /// <summary>SL1: note that this live entry's staged words may have moved. Hot (per visited caster per
    /// frame), allocates nothing.</summary>
    private static void NoteStagedWord(int i)
    {
        if (_gDirtyMark[i]) return;
        _gDirtyMark[i] = true;
        _gDirty[_gDirtyN++] = i;
    }

    /// <summary>SL1: the two staged words that can move without a re-staging -- the index count (an entry
    /// turned off draws nothing) and the #305 set flag. True = they differed, i.e. the source buffer needs
    /// re-uploading. One function so the changed-set walk and its audit cannot diverge.</summary>
    private static bool WriteStagedWords(uint* src, int d, int i)
    {
        uint* r = src + (long)d * CasterGather.SrcUints;
        uint cnt = _liveOff[i] ? 0u : (uint)_liveIdxCount[i], set = _liveDyn[i] ? 1u : 0u;
        if (r[0] == cnt && r[CasterGather.SetFlagWord] == set) return false;
        // The batch's dynamic population follows the set flag, and the batch is the word the staging wrote
        // beside it -- no second lookup.
        if (r[CasterGather.SetFlagWord] != set) _gDynN[(int)r[4]] += set != 0 ? 1 : -1;
        r[0] = cnt; r[CasterGather.SetFlagWord] = set;
        return true;
    }

    /// <summary>S8: the DYNAMIC source -- a verbatim copy of the staged records whose set flag is 1. Each
    /// record carries its own batch id and output base, so a survivor out of this source lands in exactly the
    /// region and the batch it would have landed in from the full one; the command buffer, the count buffer,
    /// the batch table and the draw loop are all untouched. What changes is the thread count: a dynamic view
    /// dispatched ~37k threads to find ~438 casters, and dynamic faces are most of the frame's views.</summary>
    private const int MaxDynSrc = 8192;
    private static readonly uint[] _dSrc = new uint[MaxDynSrc * CasterGather.SrcUints];
    /// <summary>Frames the dynamic source stood, the draws it covered, frames past <see cref="MaxDynSrc"/>
    /// (which fall back to the full source -- the same set by a longer road), and the audit's misses.</summary>
    private static long _dSrcFrames, _dSrcDraws, _dSrcOver, _dSrcAudits, _dSrcMiss;
    /// <summary>Frames the movers were more than half the staging, so the short source would have saved
    /// nothing. Its own number: a steady value here means the split has stopped being worth having.</summary>
    private static long _dSrcHalf;
    /// <summary>Records the copy took that are NOT in the dynamic set -- a still caster in the moving layer.
    /// The half of the audit that <c>want != dyn</c> cannot see, so it carries its own number.</summary>
    private static long _dSrcExtra;

    /// <summary>Build and publish this frame's dynamic source. Inside the bracket, after the staging stands:
    /// the membership is <c>_dMoverIdx</c>, which Collect and Refresh rebuild whole every frame.</summary>
    private static void StageDynamicSource()
    {
        if (!Shadows.DynSrc.V || _gN <= 0 || _dMoverCount <= 0) { CasterGather.StandDynamic(); return; }
        fixed (uint* src = _gSrc)
        fixed (uint* dst = _dSrc)
        {
            int m = CopyDynamicSource(src, dst);
            if (m < 0) { _dSrcOver++; CasterGather.StandDynamic(); return; }
            // Past half the staging there is nothing left to save, and the upload would cost more than the
            // threads: the full source IS the dynamic source at that point.
            if (m == 0 || m * 2 > _gN) { if (m > 0) _dSrcHalf++; CasterGather.StandDynamic(); return; }
            if ((_frameSeq & (StageAuditEvery - 1)) == 0) AuditDynamicSource(src, dst, m);
            if (CasterGather.DynamicSource(m, dst)) { _dSrcFrames++; _dSrcDraws += m; }
        }
    }

    /// <summary>The copy itself, apart from the GL upload so the self-check can drive it: every staged mover's
    /// record, verbatim. -1 = more movers than <see cref="MaxDynSrc"/> holds.</summary>
    private static int CopyDynamicSource(uint* src, uint* dst)
    {
        const long RecBytes = CasterGather.SrcUints * sizeof(uint);
        int m = 0;
        for (int q = 0; q < _dMoverCount; q++)
        {
            int d = _gAt[_dMoverIdx[q]];
            if (d < 0) continue;   // this mover is not in the staging at all (no VAO, blended, mask -1)
            if (m >= MaxDynSrc) return -1;
            Buffer.MemoryCopy(src + (long)d * CasterGather.SrcUints,
                              dst + (long)m * CasterGather.SrcUints, RecBytes, RecBytes);
            m++;
        }
        return m;
    }

    /// <summary>1 frame in <see cref="StageAuditEvery"/>: the dynamic source has to be EXACTLY the staged
    /// draws that a dynamic view would DRAW -- a missing one is a mover with no shadow in its own page, an
    /// extra one is a still caster baked into the moving layer. Counted, must read 0.
    /// <para>"Would draw" and not "has the set flag": an entry that turns off keeps its flag. The refresh's
    /// off branch continues before <c>Admit</c>, so a dynamic caster that stops casting (a tile mid-fade, a
    /// re-tenanted slot) leaves <c>_dMoverIdx</c> -- and <c>_dPrevIdx</c> with it, so it is not revisited --
    /// while its staged word 6 still reads 1. Its index count is 0 by then, so it draws nothing from either
    /// source and the two sets agree on everything that matters; counting the flag alone made this benign
    /// state read as a miss for as long as the entry stayed off.</para>
    /// <para>Both directions are counted, and they are not the same test. <c>want != dyn</c> is the MISSING
    /// one -- a mover the short source left out, which is a shadow absent from its own page. <c>extra</c> is
    /// the other -- a copied record whose flag is 0, i.e. a still caster in the moving layer, which the
    /// shader's own set filter would still reject but which means the copy walked the wrong set. Counting
    /// "would draw" on both sides made the second unreachable, so it is asked for separately.</para></summary>
    private static void AuditDynamicSource(uint* src, uint* dst, int m)
    {
        _dSrcAudits++;
        int want = 0;
        for (int d = 0; d < _gN; d++)
        {
            uint* r = src + (long)d * CasterGather.SrcUints;
            if (r[CasterGather.SetFlagWord] != 0u && r[0] != 0u) want++;
        }
        int dyn = 0, extra = 0;
        for (int k = 0; k < m; k++)
        {
            uint* r = dst + (long)k * CasterGather.SrcUints;
            if (r[CasterGather.SetFlagWord] == 0u) { extra++; continue; }
            if (r[0] != 0u) dyn++;
        }
        if (want != dyn) _dSrcMiss++;
        if (extra > 0) { _dSrcExtra += extra; _dSrcMiss++; }
    }

    /// <summary>SL2/SL6: dynamic casters per batch. A view baking the DYNAMIC set can skip a batch holding
    /// none of them, and one baking the STATIC set can skip a batch holding nothing else -- exactly, from the
    /// CPU, with no readback: the set flag is the CPU's own word. At 438 movers over 37k staged draws most
    /// batches hold no mover at all, and the dynamic pool's faces are most of the frame's views.</summary>
    private static readonly int[] _gDynN = new int[MaxCasters];
    /// <summary>Indirect calls the set-occupancy skip did not issue -- the period line's witness for it.</summary>
    private static long _mdiSkipped;
    /// <summary>Per batch: the chunk's view sets, as a bit per <c>CasterGather.Set*</c>. A batch may be
    /// skipped only when NO view in the chunk could admit one of its casters.</summary>
    private static bool BatchIdle(int g, int sets)
        => sets == 1 << CasterGather.SetDynamic ? _gDynN[g] == 0
         : sets == 1 << CasterGather.SetStatic ? _gDynN[g] >= _gLen[g]
         : false;

    /// <summary>CX-120: the GL primitive one batch draws. An ARENA batch is TRIANGLES by construction -- the
    /// mesh arena holds one topology, and a DB slot carries no engine primitive enum to translate. Putting it
    /// through the engine's <c>PrimTypeTranslationTable</c> anyway read index 0 for every draw recipe, which
    /// that table answers <c>GL_POINTS</c>: every sun cascade was baked out of a few hundred single-pixel
    /// depth samples, so the bake issued its draws, counted its survivors and left a map with nothing in it
    /// to shadow with. The colour path already spells this same rule on its batch row.
    /// <para>A batch cannot straddle the two: the VAO id is part of the staging key.</para></summary>
    private static uint BatchMode(int g)
        => AlloyRenderer.IsArenaVao(_gVao[g]) ? Gl.TRIANGLES : _primTable[_gPrim[g]];

    /// <summary>Per batch: the mask is sampled through the record's handle (no texture in the key).</summary>
    private static readonly bool[] _gH = new bool[MaxCasters];
    /// <summary>Batches that bind a mask texture (one indirect call per flush each) and batches that sample
    /// it through the handle block (merged per VAO and primitive).</summary>
    private static string MaskedBatches()
    {
        int b = 0, h = 0;
        for (int g = 0; g < _gGroups; g++) { if (_gH[g]) h++; else if (_gTex[g] != 0) b++; }
        return $"{b} masked bound, {h} by handle";
    }
    /// <summary>#303: the frame produced a coherent staging -- a non-empty one the gather accepted, or a
    /// genuinely empty world. False = the gather refused a non-empty staging (no program, no sphere buffer
    /// yet), and the views must NOT bake -- baking would clear live shadows out of the maps.</summary>
    private static bool _gStaged;

    /// <summary>One staged draw, written into the source record. Its own function because
    /// <see cref="SelfCheck"/> runs it: a field landing one slot over is a caster baking at its neighbour's
    /// matrix, and none of it needs a GL context to be wrong.
    /// <para>#303: word 3 is the OWNERSHIP SLOT -- the dispatch culls <c>CxCullSphere[slot]</c> (the colour
    /// cull's own bounds and exemptions) and writes it as <c>baseInstance</c>, which the depth stages read
    /// <c>uDraw</c> through. There is no per-draw shadow record left at all.</para></summary>
    private static void StageOne(uint* src, int d, int i, int g, int start)
    {
        uint* r = src + (long)d * CasterGather.SrcUints;
        r[0] = _liveOff[i] ? 0u : (uint)_liveIdxCount[i];   // an entry turned off draws nothing
        // in INDICES already, unlike the colour path's byte offsets -- the indirect command takes elements
        r[1] = _liveFirst[i];
        r[2] = _liveBaseVert[i];
        r[3] = (uint)_liveSlot[i];
        r[4] = (uint)g;
        r[5] = (uint)start;
        // #305: the set flag the dispatch filter reads -- which of the two arrays this caster belongs in.
        r[CasterGather.SetFlagWord] = _liveDyn[i] ? 1u : 0u;
    }

    /// <summary>#286: stage the whole frame once, in batch order, and publish it. Everything per-view that
    /// is not the cull itself happens here exactly once -- the VAO lookup, the mask-texture
    /// resolve, the record. Inside the bracket, because the mask resolve issues Alloy's own texture copy.
    /// <para>False = a non-empty staging was REFUSED (the views must idle, not bake); an empty world stages
    /// true with zero draws, and its views bake clears.</para></summary>
    private static bool GatherStage()
    {
        if (_gCollectSeq == _collectSeq && _gListVer == _listVer)
        {   // #328: the list stood, so the staging stands. The set flag is the one word that moves per frame
            // (a caster starting or stopping); the gather skips the upload when nothing did.
            fixed (uint* src = _gSrc)
            {
                bool changed = false;
                // SL1: the CHANGED entries, not all of them. r[0] follows _liveOff and _liveIdxCount and
                // r[SetFlagWord] follows _liveDyn; all three move only for an entry the refresh visited (a
                // collect or an append bumps the list version and re-stages instead), and the visited set is
                // ~1100 of 37k. The pre-SL1 walk read four scattered arrays per staged draw every frame.
                for (int q = 0; q < _gDirtyN; q++)
                {
                    int i = _gDirty[q];
                    _gDirtyMark[i] = false;
                    int d = _gAt[i];
                    if (d < 0) continue;   // this entry is not in the staging at all
                    if (WriteStagedWords(src, d, i)) changed = true;
                }
                _gDirtyN = 0;
                if (_refreshAll || (_frameSeq & (StageAuditEvery - 1)) == 0)
                {   // ...and the audit: a missed word bakes a caster at another frame's index count or in the
                    // wrong half of the split, so the full walk still runs -- one frame in 64.
                    _gAudits++;
                    for (int d = 0; d < _gN; d++)
                        if (WriteStagedWords(src, d, _gLive[d])) { _gStaleWords++; changed = true; }
                }
                bool stood = _gN == 0 || CasterGather.Begin(_gN, _gGroups, src, changed);
                _gDrawN = stood ? _gN : 0;
                if (stood) { _submitMasked += _gMasked; StageDynamicSource(); }
                else CasterGather.StandDynamic();
                return stood;
            }
        }
        int n = 0, creatures = 0, maskedN = 0;
        _gCollectSeq = _collectSeq; _gListVer = _listVer; _gN = 0; _gMasked = 0;
        // SL1: the staging is being rebuilt, so every entry's words are about to be correct and the standing
        // changed set is spent. Live indices mean something different after a collect, so the marks go too.
        for (int q = 0; q < _gDirtyN; q++) _gDirtyMark[_gDirty[q]] = false;
        _gDirtyN = 0;
        Array.Fill(_gAt, -1, 0, _liveCount);
        for (int i = 0; i < _liveCount; i++)
        {
            if (AlloyRenderer.CasterVao(_liveVao[i]) == 0) continue;
            // #265: resolve the OWNED name first and put the coverage question to IT -- the mask and colour
            // passes sample the owned copy, which may be a BCn transcode whose block-quantized alpha sits on
            // the other side of the cutout from the engine's raw original.
            // CX-120: a DB caster's diffuse lives in the scene database's own texture registry -- another id
            // space and another coverage memo -- so BOTH questions go there instead (DbCasterMask). Asked
            // through OwnedTextures they came back as the registry slot NUMBER to bind as a GL name and
            // "never scanned" for every texture, which is how 813 arena casters alpha-tested against
            // someone else's texture and the sun cascades baked empty.
            bool db = AlloyRenderer.DbSlot(_liveSlot[i]);
            int dbTex = 0; float dbHoles = 0f, dbGraded = 0f;
            if (db) AlloyRenderer.DbCasterMask(_liveSlot[i], out dbTex, out dbHoles, out dbGraded);
            int drawTex = db ? dbTex : _liveTex[i] != 0 ? OwnedTextures.Draw(_liveTex[i]) : 0;
            // mode 0 never masks, so it never reads a texture back either (the scan is a glGetTexImage of
            // the owned name in the 4.6 context; /shadow mask skip is the A/B for it)
            // #363: a translucent diffuse (glass) has no holes at all; it qualifies on its graded fraction
            // instead and skips the mode-2 hole gate, which would hand it straight back to the solid stage.
            bool glass = drawTex != 0 && _maskDither > 0
                         && (_liveBlend[i] || (db ? dbGraded : TexGraded(drawTex)) >= _maskGraded);
            int masked = drawTex != 0 && _progMask != 0 && _maskMode != 0
                         && (glass || (db ? dbHoles > 0f : TexHasAlpha(drawTex))) ? drawTex : 0;
            if (masked == 0 && _liveTex[i] != 0 && _progMask == 0) _maskNoProg++;
            // The mask MODE is decided here, in the key, not at the draw: a masked texture the mode draws
            // solid merges into its (VAO, prim) batch instead of costing one indirect call per view per
            // texture. The knobs bump _listVer so a change re-stages.
            if (masked != 0 && (_maskMode == 0
                                || (_maskMode == 2 && !glass && (db ? dbHoles : TexHoles(masked)) < _maskHoles))) masked = 0;
            if (masked != 0 && _maskMode < 0) continue;
            // a blended caster the mask stage cannot take (no diffuse, no UVs, mask off) casts nothing, as before #363
            if (_liveBlend[i] && masked == 0) continue;
            if (masked != 0) maskedN++;
            // A record with a resident handle samples its mask through the handle block, so the texture
            // leaves the key. Handle 0 is a BLACK texture (every fragment discards), hence the guard: such a
            // record keeps the bound-texture batch.
            bool hmask = masked != 0 && HandleMaskReady && AlloyRenderer.RecordHandle0(_liveSlot[i]) != 0;
            _gLive[n] = i;
            // #302: the cutout threshold is a per-BATCH uniform now, exactly as the colour path spells it
            // (uAlphaCut), so it joins the key rather than riding in a per-draw record. Two values exist.
            _gKey[n] = ((masked != 0 ? 1L : 0L) << 62) | ((_liveCut[i] == EnvAlphaDiscard ? 1L : 0L) << 61)
                       | ((hmask ? 1L : 0L) << 60)
                       | ((long)_liveVao[i] << 44) | ((long)_livePrim[i] << 36) | (hmask ? 0u : (uint)masked);
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
                    _gH[g] = (_gKey[d] & (1L << 60)) != 0;
                    _gCut[g] = _liveCut[i];
                    _gDynN[g] = 0;
                }
                _gLen[g] = d - _gStart[g] + 1;
                _gAt[i] = d;
                if (_liveDyn[i]) _gDynN[g]++;
                StageOne(src, d, i, g, _gStart[g]);
            }
        _gGroups = g + 1; _gCreatures = creatures; _gN = n; _gMasked = maskedN;
        bool ok;
        fixed (uint* src = _gSrc)
            ok = CasterGather.Begin(n, _gGroups, src, true);
        if (ok) { _gDrawN = n; _submitMasked += maskedN; StageDynamicSource(); }
        else CasterGather.StandDynamic();
        return ok;
    }

    /// <summary>#286: bake one view without a per-view walk. The dispatch selects out of this frame's staging
    /// and writes the commands; the CPU issues one indirect-count draw per batch and never learns how many
    /// survived.</summary>
    private static bool GatherSubmit(uint tex, int size, int layer, Matrix4x4* vp,
                                     Vector3 lightDir, Vector3 focus, float reach, float margin, bool point,
                                     int set, bool receiver)
    {
        // PRE-cull figures: the survivors are the GPU's, and asking would be the readback this pass exists
        // to not do -- the latent sample on the period line is what reports them.
        _submitCreatures = _gCreatures;
        if (!BeginTarget(tex, size, layer)) return false;
        CasterGather.Dispatch(focus, lightDir, reach, reach + margin, point, set, receiver,
                              dynSrc: set == CasterGather.SetDynamic);
        // unit 0 is where the cutout stage samples; nothing else in this bracket moves the active unit
        Gl.ActiveTexture(Gl.TEXTURE0);
        int boundVao = -1, boundMode = -1, boundTex = -1;
        float boundCut = -1f;
        // ponytail: every view issues one indirect-count draw per GLOBAL batch. SL2 removes the batches the
        // view's SET provably empties -- the set flag is the CPU's own word, so that much needs no readback --
        // and what is left is the batches whose count the CULL may have left at zero, which the CPU cannot
        // know without the readback this pass exists to not do. Fewer batches (the staging key) is the lever
        // for those.
        int sets = 1 << set;
        for (int g = 0; g < _gGroups; g++)
        {
            if (BatchIdle(g, sets)) { _mdiSkipped++; continue; }
            int cut0 = _gTex[g], mode = _gH[g] ? 2 : cut0 != 0 ? 1 : 0;
            if (mode != boundMode)
            {
                boundMode = mode;
                Gl.UseProgram(mode == 0 ? _prog : mode == 1 ? _progMask : _progMaskH);
                Gl.UniformMatrix4fv(mode == 0 ? _locVp : mode == 1 ? _locMaskVp : _locMaskHVp, 1, 0, (float*)vp);
                if (mode == 1) Gl.Uniform1i(_locMaskTex, 0);
                boundCut = -1f;
            }
            if (mode != 0 && _gCut[g] != boundCut)
            {
                boundCut = _gCut[g]; Gl.Uniform1f(mode == 1 ? _locMaskCut : _locMaskHCut, boundCut);
                Gl.Uniform1i(mode == 1 ? _locMaskDither : _locMaskHDither, _maskDither);
                Gl.Uniform1i(mode == 1 ? _locMaskEnv : _locMaskHEnv, boundCut == EnvAlphaDiscard ? 1 : 0);
            }
            if (mode == 1 && cut0 != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)cut0); boundTex = cut0; }
            if (_gVao[g] != boundVao) { Gl.BindVertexArray(AlloyRenderer.CasterVao(_gVao[g])); boundVao = _gVao[g]; }
            CasterGather.Draw(BatchMode(g), g, _gStart[g], _gLen[g]);
            _mdiCalls++;
        }
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

    /// <summary>#336: the faces of a cube at <paramref name="pos"/> a sphere sits in, as a bit per face in
    /// <c>CubeFaceForward</c> order; 0 when it is out of the light's reach. The same cone test the caster
    /// submission culls by, so a face this says is empty is one the gather would have found empty.</summary>
    private static int FacesOf(Vector3 pos, float far, float cx, float cy, float cz, float r)
    {
        float dx = cx - pos.X, dy = cy - pos.Y, dz = cz - pos.Z;
        float dd = dx * dx + dy * dy + dz * dz, mr = far + r;
        if (dd > mr * mr) return 0;
        int m = 0;
        if (!FaceCulls(dd, dx, r)) m |= 1; if (!FaceCulls(dd, -dx, r)) m |= 2;
        if (!FaceCulls(dd, dy, r)) m |= 4; if (!FaceCulls(dd, -dy, r)) m |= 8;
        if (!FaceCulls(dd, dz, r)) m |= 16; if (!FaceCulls(dd, -dz, r)) m |= 32;
        return m;
    }

    /// <summary>#336: the faces of this light's cube that hold an EVENT this frame -- a mover, a static-set
    /// flip, a ghost. What a dynamic page's content can differ by from last frame.
    /// <para><b>SL6: this IS the per-page "is anything in it moving" gate</b> S1(b) asks for, one step finer
    /// (per FACE, not per page), and it saturates because the movers really are everywhere: the 388 dangly
    /// movers of a city's 438 are a MEASUREMENT, not a declaration -- <c>DanglyPhysics</c> reports the largest
    /// per-vertex displacement and <c>AlloyRenderer.Accumulate</c> integrates it against <c>dangly.still</c>
    /// (1 cm of path), which the breeze and the turbulence clear every frame for every banner in the wind.
    /// Raising that threshold would freeze a swinging banner's shadow, which ADR 0019 calls the wrong
    /// direction. What SL6 cut instead is the COST of a saturated mask: a dynamic view no longer issues an
    /// indirect draw for a batch holding no mover (<see cref="BatchIdle"/>). The structural step left is S8 --
    /// staging the ~438 dynamic casters separately, so a dynamic view's dispatch covers 438 draws and not
    /// 37k.</para></summary>
    private static int EventFaceMask(Vector3 pos, float far)
    {
        int mask = 0;
        for (int q = 0; q < _moverCount && mask != AllFaces; q++)
        { int m = _moverIdx[q]; mask |= FacesOf(pos, far, _liveCx[m], _liveCy[m], _liveCz[m], _liveR[m]); }
        for (int q = 0; q < _sMoverCount && mask != AllFaces; q++)
        { int m = _sMoverIdx[q]; mask |= FacesOf(pos, far, _liveCx[m], _liveCy[m], _liveCz[m], _liveR[m]); }
        for (int q = 0; q < _ghostCount && mask != AllFaces; q++)
            mask |= FacesOf(pos, far, _ghostCx[q], _ghostCy[q], _ghostCz[q], _ghostR[q]);
        return mask;
    }

    /// <summary>#336: is an event (a mover, a static-set flip, a ghost) inside this cascade's fit?</summary>
    private static bool EventInFit(Vector3 c, Vector3 dir, float radius, float margin)
    {
        for (int q = 0; q < _moverCount; q++)
        { int m = _moverIdx[q]; if (InFit(_liveCx[m] - c.X, _liveCy[m] - c.Y, _liveCz[m] - c.Z, _liveR[m], dir, radius, radius + margin)) return true; }
        for (int q = 0; q < _sMoverCount; q++)
        { int m = _sMoverIdx[q]; if (InFit(_liveCx[m] - c.X, _liveCy[m] - c.Y, _liveCz[m] - c.Z, _liveR[m], dir, radius, radius + margin)) return true; }
        for (int q = 0; q < _ghostCount; q++)
            if (InFit(_ghostCx[q] - c.X, _ghostCy[q] - c.Y, _ghostCz[q] - c.Z, _ghostR[q], dir, radius, radius + margin)) return true;
        return false;
    }

    /// <summary>Resrefs the caster pass looks up before falling back to the embedded stage (#92).</summary>
    internal const string ShdCastVs = "cx_cast_vs", ShdCastFs = "cx_cast_fs";

    /// <summary>#161: the caster vertex stage's half of the override contract. #303 retired the CxCaster
    /// block with the staging that filled it, so what an override has to keep is two things: it reads Alloy's
    /// record through <c>uDraw[gl_BaseInstance]</c> (the ownership slot the gather wrote as baseInstance),
    /// and it keeps #96's skinning branch. A stage that lost the branch links perfectly, the bone attributes
    /// simply go unread, and every creature casts its BIND POSE while it animates.</summary>
    private static bool CastVsSafe(string vs)
        => vs.Contains("uDraw[gl_BaseInstance]") && vs.Contains("uBone")
           && !vs.Contains("CxCaster");

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
    /// term (<c>CxBoneBase</c> / <c>CxSkinMat</c>, shared source, not a re-spelling), and the rigid arm is
    /// <see cref="AlloyRenderer.ClipExpr"/> character-for-character under <c>invariant gl_Position</c> --
    /// #119's standing constraint, which the depth prepass compares against.</summary>
    internal static string CastVs => ShaderSource.Get("cx_cast_v.glsl");
    /// <summary>Depth-only: the fixed-function depth write is the whole output.</summary>
    internal static string CastFs => ShaderSource.Get("cx_cast_f.glsl");

    /// <summary>The masked caster's depth stage: the same clip position, plus the diffuse UVs, so the
    /// fragment stage can punch the cutout's holes into the shadow map. No skinning -- <see cref="Collect"/>
    /// keeps a posed caster out of the masked set for exactly that reason, since a bind-pose cutout is worse
    /// than a solid one.
    /// <para>No resref, for <see cref="DepthKeyVs"/>'s reason one level up: a content override that drifted
    /// from the 0.2 threshold would write shadow where the colour pass draws nothing, which reads as dirt on
    /// a wall and points at no file.</para></summary>
    private static string CastMaskVs => ShaderSource.Get("cx_castmask_v.glsl");
    private static string CastMaskFs => ShaderSource.Get("cx_castmask_f.glsl");

    /// <summary>The handle-sampling pair: <see cref="CastMaskVs"/> carrying the record index out, and
    /// <see cref="CastMaskFs"/> with <c>uTex</c> redirected through the handle block by
    /// <see cref="AlloyRenderer.BindlessFs"/>. Text only; the programs exist only under bindless.</summary>
    private static readonly string CastMaskHVs = CastMaskVs
        .Replace("out vec2 vUV;", "out vec2 vUV;\n        flat out int vDraw;")
        .Replace("vUV = aUV;", "vUV = aUV;\n            vDraw = gl_BaseInstance;");
    private static string CastMaskHFsSrc => ShaderSource.Get("cx_castmaskh_f.glsl");
    private static readonly string CastMaskHFs = AlloyRenderer.BindlessFs(CastMaskHFsSrc);
    private static readonly string CastLayerMaskHVs = CastLayerMaskVs
        .Replace("out vec2 vUV;", "out vec2 vUV;\n        flat out int vDraw;")
        .Replace("vUV = aUV;", "vUV = aUV;\n            vDraw = gl_BaseInstance & 0x3ffff;");

    /// <summary>#307: the layered depth stage. <c>baseInstance</c> packs the view-table index above the
    /// ownership slot (<see cref="CasterGather.ViewShift"/>); the stage routes to its layer and takes its fit
    /// matrix from the view table, so no per-view uniform exists. <c>uVP</c> is deliberately spelled as a
    /// LOCAL so the clip expression stays #119's character for character under the invariant -- the maths is
    /// identical, only where the matrix comes from moved. Embedded only, never overridable: an override here
    /// could only desync the two bake paths the A/B compares.</summary>
    internal static string CastLayerVs => ShaderSource.Get("cx_castlayer_v.glsl");

    /// <summary>...and the masked twin, <see cref="CastMaskVs"/> with the same routing. No skinning, for
    /// the same bind-pose-cutout reason.</summary>
    private static string CastLayerMaskVs => ShaderSource.Get("cx_castlaymask_v.glsl");
}
