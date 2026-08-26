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
/// matrix -- so there is no static/dynamic split and no kind-based branching anywhere below. #187: there is
/// no motion split either. Every view bakes what it sees, every frame -- the cost is honest and no view can
/// hold depth for a world that has since moved.
/// <para>Submission is <c>glMultiDrawElementsIndirect</c> out of persistent-mapped storage: one immutable
/// command buffer and one per-draw SSBO, each a 3-region ring with a fence per region, staged by the CPU and
/// never re-allocated. Per-draw data is indexed by <c>gl_BaseInstance</c> rather than <c>gl_DrawID</c>
/// deliberately -- M4's GPU culling compacts the command buffer, which renumbers gl_DrawID but leaves each
/// surviving command's baseInstance pointing at its own record. That is the one format decision this ticket
/// cannot get wrong, so <see cref="SelfCheck"/> pins it.</para>
/// <para>The pass runs in the #80 4.6 context over the engine's own shared vertex/index buffers (own VAOs --
/// buffers share across contexts, VAOs do not) and renders every view the Alloy lit stage samples: the sun's
/// concentric cascade array (#94) and the local lights' depth cube array (#95), six faces per light. One
/// <see cref="Collect"/> per frame classifies the world; <see cref="Submit"/> then culls and stages it per
/// view, out of a ring sized for that view class. Nothing about a caster differs between the two -- the sun
/// and a torch draw the same geometry through the same command format.</para>
/// <para>#96 puts CREATURE casters through that same path: same command format, same rings, same views, one
/// extra per-draw field. What differs is where they come from -- a creature has no enrollment, because its
/// pose is only true for the frame it was seen in. Each frame the passive-capture bracket around a
/// creature part's own engine draw records its (VDO, item, prim) and its live model matrix.
/// <para>An NWN creature turns out to be mostly PARTS-BASED (live-verified by dumping the VAOs, 2026-08-20):
/// each body piece is a rigid mesh parented to a bone, carrying position/uv/normal/tangent and NO bone stream,
/// posed entirely by its node matrix -- the engine's own shader runs its <c>skinmesh == 0</c> branch on it. So
/// the animation is already in the matrix and the shadow follows the walk for free. A TRUE skinmesh (cloak,
/// robe, tail) is the minority case: it carries the bone attributes at locations 8/9, and for those the bracket
/// also snapshots the engine's live <c>m_bones</c> into a per-frame SSBO so the depth shader can re-run the
/// engine's skinning. No engine replay either way, and a part not seen this frame simply has no record -- its
/// shadow blinks rather than lying about where it is.</para></para>
/// <para>Gated on <see cref="RenderMode.Alloy"/> so the vanilla toggle reverts it frame-atomically.
/// The <c>casters=0</c> config setting is the kill switch (<c>skincast=0</c> for the skinned half alone), and
/// <c>/shadow</c> carries the look knobs. The frame path
/// allocates nothing.</para>
/// </summary>
public sealed unsafe class CasterPass : IExtenderService
{
    /// <summary>Caster capacity. Surplus parts simply do not cast this enrollment -- never a crash, never a
    /// silent half-submission (the overflow is counted and logged).</summary>
    /// <summary>Caster capacity, and the one number in the shadow pass that has to track
    /// <c>AlloyRenderer.MaxOwned</c>. At 8192 against an Alloy table of 163840 the colour pass would draw
    /// a dense area whole and the shadow pass would bake a twentieth of it -- which reads as "shadows go
    /// missing in big areas", not as a cap. 65536 casters x 262 bytes across the arrays below is ~17MB.</summary>
    private const int MaxCasters = 65536, MaxVdos = 4096;
    private const int EnrollEvery = 60, LogEvery = 900, FirstLogAt = 120;
    /// <summary>Sun ring depth: regions in flight before the CPU has to wait on a fence. Three frames is the
    /// usual "CPU one frame ahead, GPU one behind, one spare" figure -- but a region is consumed per VIEW, and
    /// #94 submits one per cascade, so a 3-region ring would wrap inside a single frame and stall on a fence
    /// the same frame issued. One submission per view since #187, so two frames of cascades is the ceiling.</summary>
    private const int SunRegions = 2 * MaxCascades;
    /// <summary>Casters staged per CUBE face. A local light's view only ever holds geometry inside one torch's
    /// reach, so it needs nothing like the sun's capacity.
    /// <para>This is the one capacity that is NOT free to raise: the cube ring reserves
    /// <see cref="RegionsPerCube"/> regions PER LIGHT, so the mapped footprint is
    /// <c>lights * RegionsPerCube * this * (CmdBytes + DrawBytes)</c> -- 103MB at 24 lights. #149's sweep put
    /// it at 8192 by treating it like a flat table and missed the multiplier, which at #151's raised light
    /// count would have been 410MB of persistently-mapped buffer on top of ~300MB of depth. Overflow is
    /// counted and logged (<c>casters dropped by a full view region</c>), never a silent half-submission, so
    /// the honest move is a sane figure with a visible ceiling rather than a huge one nobody can afford.</para></summary>
    private const int MaxCubeCasters = 2048;
    /// <summary>Faces per cube, and ring regions reserved per cube. Regions per cube must be at least
    /// <see cref="CubeFaces"/> so no face reuses a region another face of the SAME frame is still in; the
    /// doubling is the usual CPU-ahead-of-GPU frame of slack on top.</summary>
    private const int CubeFaces = 6, RegionsPerCube = 3 * CubeFaces;
    /// <summary>A <c>DrawElementsIndirectCommand</c> is five uints -- count, instanceCount, firstIndex,
    /// baseVertex, baseInstance -- and the buffer is tightly packed, so the stride is 20 bytes.
    /// <c>firstIndex</c> is in INDICES, not bytes: the byte-offset APIs elsewhere in this repo multiply the
    /// VDO's element offset by 2 and this one must not.</summary>
    private const int CmdUints = 5, CmdBytes = CmdUints * 4;
    /// <summary>Floats per SowxCaster record: std430 <c>mat4 model + vec4 bounds + vec4 aux</c> = 96 bytes,
    /// whose alignment (16) already divides the size, so the array stride is 96 with no padding. The bounds are
    /// the caster's world bounding sphere (xyz centre, w radius) and they are in the RECORD rather than a
    /// CPU-side array precisely so M4's compute cull can read them by baseInstance without a format change -- a
    /// culler that had to be handed a second, separately-keyed buffer is the reformat this ticket must avoid.
    /// <c>aux.x</c> is #96's bone base: the MATRIX index of this caster's first bone, or &lt; 0 for a rigid
    /// caster. Model and bounds keep their offsets, so the cull M4 drops in still reads the same two fields.</summary>
    private const int DrawFloats = 24, DrawBytes = DrawFloats * 4;
    /// <summary>SSBO binding points of the per-draw block and #96's bone block; must match the literals in
    /// <c>CasterBufDecl</c> / <c>BoneBufDecl</c>. 0..3 belong to <see cref="AlloyLights"/> and 4 to the owned
    /// colour pass's per-draw block.</summary>
    private const uint BindCaster = 5, BindBones = 6;
    /// <summary>Bones per skinned record, fixed by the engine: <c>GLRender::m_bones</c> is a 64 x mat4 static
    /// array and <c>SetUniforms</c> uploads all 64 every time, so a snapshot is a flat 4KB memcpy. The stored
    /// matrices are already in GLSL's column-major order (see <see cref="SymbolMap.GlRenderBones"/>), so the
    /// copy needs no fixup of any kind.</summary>
    private const int BoneCount = 64, BoneFloats = BoneCount * 16;
    /// <summary>Creature-part caster records per frame. Live measurement: a busy exterior showed ~135 part
    /// draws a frame, so this is roughly an eightfold headroom on a crowd. Cheap -- a record is a few dozen
    /// bytes; it is BONE slots that cost 4KB each, and most creature parts need none.</summary>
    private const int MaxSkin = 8192;
    /// <summary>Bone snapshots per frame -- the expensive resource, and needed only by TRUE skinmeshes (a
    /// cloak, a robe, a tail), which are a small minority of the parts on screen. 64 x 4KB x 3 regions =
    /// 768KB. A skinmesh that cannot get a slot does not cast this frame rather than casting a bind pose.</summary>
    private const int MaxBoneSlots = 512;
    /// <summary>Bone-ring regions: the block is written ONCE per frame, before any view submits, and read by
    /// every view of that frame -- so unlike the command rings a region is consumed per FRAME, and three is the
    /// ordinary CPU-ahead-of-GPU depth.</summary>
    private const int BoneRegions = 3;
    /// <summary>Floor on a skinned caster's cull radius, in world units. The part bbox of a skinned mesh is
    /// whatever the engine last computed for it, and an animated pose can reach well past a bind-pose box; a
    /// creature is ~2u across, so this is cheap insurance against culling one that should have shadowed.</summary>
    private const float MinSkinRadius = 3f;
    /// <summary>Slope of the cone that circumscribes a 90deg cube face -- its corners sit at
    /// <c>|perp| = sqrt(2) * depth</c> (#100's per-face cull). Deliberately a HAIR above sqrt(2): a caster
    /// exactly on the diagonal makes the comparison an equality, and in float
    /// <c>MathF.Sqrt(2)^2 == 1.9999999</c>, so the true sqrt(2) rejects the very corner it is meant to keep.
    /// <see cref="SelfCheck"/> caught that; the slack is what makes the test conservative in practice and not
    /// just on paper.</summary>
    private const float FaceConeSlope = 1.4143f;
    private const uint GlUnsignedShort = 0x1403;

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

    /// <summary>Presented frames since arming; the freshness clock for <see cref="CascadesFresh"/>.</summary>
    private static int _frameSeq;
    private static int _cascadeStamp = -1, _cubeStamp = -1;

    /// <summary>May the colour pass sample the cubes right now? Same single test as
    /// <see cref="CascadesFresh"/>: rendered on the most recent frame tick or not at all, which covers the
    /// vanilla toggle, a module unload and a disarm together.</summary>
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
    /// -&gt; no render), a vanilla stretch (the pass is gated on <see cref="RenderMode.Alloy"/>, so the toggle
    /// reverts sun shadows on the very next frame), and a disarm.</summary>
    internal static bool CascadesFresh => CascadeCount > 0 && CascadeTex != 0 && _cascadeStamp == _frameSeq;

    /// <summary>Does the caster set get rebuilt on this frame's enrollment? The set is only ever as complete
    /// as what the engine's bucket walk carried, and <see cref="BatchService"/> only offers parts seen in
    /// the last frame or two -- so the BSP re-walk that feeds it has to reach <see cref="WalkRadius"/> on
    /// the frames whose colour pass an enrollment reads, and can stay tight on the other 59. Since #118 moved
    /// the enrollment to the pass slot it eats the PREVIOUS colour pass's records, which is what the "- 1"
    /// below already covered -- the wide walk fires one frame early and one frame late.
    /// <para>ponytail: a set released mid-window (module unload, table wipe) re-enrolls off the tight walk and
    /// misses distant casters until the next wide frame -- up to a second of missing far-cascade shadows after
    /// an area load. Have Release() force the wide walk if that ever reads as a bug.</para>
    /// <para>Every precondition <see cref="OnPassSlot"/> itself early-outs on has to appear here too. Without the context
    /// test, a machine whose 4.6 context never came up pins this true forever -- Lifecycle's Release re-arms
    /// _sinceEnroll every frame -- so the walk runs at its widest sixty times a second on exactly the
    /// hardware that gets no shadows out of it.</para></summary>
    internal static bool WantsWideWalk =>
        _armed && !_disarmed && RenderMode.Alloy && GameGate.InModule && GlContextService.Available
        && _sinceEnroll >= EnrollEvery - 1;
    /// <summary>How far the caster walk must reach to fill the outermost cascade.</summary>
    internal static float WalkRadius => _maxDist;

    /// <summary>How far every cascade's fit reaches TOWARD the light, past its own extent. The full max
    /// shadow distance for all of them, NOT each one's radius: a caster only has to be up-sun of a fragment to
    /// shade it, at any distance, and innermost-first selection means the fragment takes cascade 0's answer
    /// and never consults a wider one. Scaling this per cascade would give cascade 0 a 30u reach, so a wall
    /// 40u up-sun would drop out of the only cascade the ground at the player's feet ever asks -- long shadows
    /// at a low sun vanishing exactly where the detail is supposed to be best.
    /// <para>It costs a narrow column, not volume: the perpendicular cull still holds at the cascade's own
    /// radius, so cascade 0 admits casters within 15u of the light axis and 135u along it.</para></summary>
    private static float FitMargin() => _maxDist;

    /// <summary>Engine attribute location of the position stream, fixed for every engine program
    /// (GLRender::ConnectShaderData's bind table). A depth draw needs nothing else.</summary>
    private const uint EngPos = 0, LocPos = 0;
    /// <summary>The diffuse UV stream's engine attribute location, and ours. Read only for the masked
    /// casters' 0.2 cutout -- a plain depth draw still needs nothing but position.</summary>
    private const uint EngUv = 1, LocUv = 1;
    /// <summary>Engine attribute locations of the two bone streams (#73: skin nodes add attr bit 0x20 -&gt;
    /// attrib 8, 4x short bone indices, and 0x40 -&gt; attrib 9, vec4 weights). The engine's own shader declares
    /// both as <c>attribute vec4</c>, so they are float-converted streams, not integer ones -- ours declares
    /// them the same way and re-creates the pointer with the harvested format.</summary>
    private const uint EngBoneIdx = 8, EngBoneW = 9, LocBoneIdx = 8, LocBoneW = 9;

    private static ExtenderLog? _log;
    private static bool _armed, _disarmed;
    /// <summary>#96 armed: the engine's bone array resolved and the kill switch off. False leaves the pass
    /// exactly as it was before #96 -- rigid casters only, no bracket, no bone buffer.</summary>
    private static bool _skinned;
    private static uint* _primTable;
    /// <summary>The diffuse lookup for masked casters: <c>Material::GetTexture(mat, 0)</c> then the Aurora
    /// texture's live GL id. Both re-read per frame -- a CAurTexture* is BORROWED and freed on area
    /// transitions, so it is never cached.</summary>
    private static delegate* unmanaged<IntPtr, int, long> _getTexture;

    // --- the caster table (parallel arrays, index = caster slot; _order walks them grouped by VAO)
    private static readonly IntPtr[] _cTri = new IntPtr[MaxCasters];   // Zero = revoked
    private static readonly IntPtr[] _cNode = new IntPtr[MaxCasters];
    private static readonly IntPtr[] _cMat = new IntPtr[MaxCasters];
    /// <summary>The engine's opacity verdict for this caster never consulted its diffuse alpha (env-mapped
    /// material -- Ghidra Material::GetIsOpaque @0x4c6190 short-circuits the texture test). Its depth draw
    /// runs the same 0.2 cutout the colour pass does, so foliage casts leaves instead of its quad.</summary>
    private static readonly float[] _cCut = new float[MaxCasters];
    /// <summary>...and whether its VDO even has a UV stream to sample with. Without one there is nothing to
    /// test, and the caster draws solid.</summary>
    private static readonly bool[] _cUv = new bool[MaxCasters];
    private static readonly IntPtr[] _cVdo = new IntPtr[MaxCasters];
    private static readonly int[] _cItem = new int[MaxCasters];
    private static readonly int[] _cPrim = new int[MaxCasters];
    private static readonly int[] _cVao = new int[MaxCasters];
    private static readonly bool[] _cMx = new bool[MaxCasters];
    /// <summary>Local bbox centre + radius captured at enrollment; the world sphere is this run through the
    /// live model matrix. <para>ponytail: the radius ignores any scale in that matrix. NWN placeables are
    /// placed, not scaled, and the cull already carries a caster-radius slack; if scaled content ever pops a
    /// shadow, take the max row length of the matrix's 3x3 as the scale factor here.</para></summary>
    private static readonly float[] _cCx = new float[MaxCasters], _cCy = new float[MaxCasters],
                                    _cCz = new float[MaxCasters], _cR = new float[MaxCasters];
    private static readonly int[] _order = new int[MaxCasters];
    private static readonly int[] _sortCount = new int[MaxVdos];
    private static int _casterCount;

    // --- #96 skinned casters. No table and no enrollment: a skinned part is re-recorded from scratch
    // every frame out of the passive-capture bracket around its own engine draw, which is the one moment its
    // bone matrices are live. A frame that did not see a part's bones simply has no record for it -- so a
    // skinned shadow can blink, but it can never be a frame stale.
    // ponytail: the bracket sits on the CAMERA's walk, so a creature the camera culled casts nothing -- it can
    // stand inside a torch's radius, behind the camera, and its cube shadow is missing. Exactly the ceiling
    // Enroll() already carries for rigid casters, and it lifts the same way (seed from the BSP sphere walk).
    private static readonly IntPtr[] _sVdo = new IntPtr[MaxSkin];
    private static readonly int[] _sItem = new int[MaxSkin], _sPrim = new int[MaxSkin], _sVao = new int[MaxSkin];
    private static readonly int[] _sBone = new int[MaxSkin];   // bone SLOT (matrix base = slot * BoneCount)
    private static readonly float[] _sModel = new float[MaxSkin * 16];
    private static readonly float[] _sCx = new float[MaxSkin], _sCy = new float[MaxSkin],
                                    _sCz = new float[MaxSkin], _sR = new float[MaxSkin];
    /// <summary>Bone snapshots, staged CPU-side during the colour walk and memcpy'd into the mapped ring once
    /// per frame inside the bracket. Staged rather than written straight through the mapping because the
    /// region's fence can only be waited on inside the bracket, which the colour walk is not.</summary>
    private static readonly float[] _boneStage = new float[MaxBoneSlots * BoneFloats];
    private static int _skinCount, _skinLive, _boneSlots, _boneSlotsLive;
    /// <summary>Open capture bracket: the part being drawn, its bone slot (-1 until its first funnel call
    /// allocates one) and its model matrix. A multi-draw skinned part yields several records off ONE snapshot,
    /// which is why the slot is allocated lazily and shared rather than one-per-record.</summary>
    private static IntPtr _skinPart;
    private static int _skinSlot = -1;
    private static float _skinCx, _skinCy, _skinCz, _skinR;
    private static readonly float[] _skinModel = new float[16];
    private static float* _bones;
    private static IntPtr _skinLastVdo;
    private static int _skinLastSlot = -1;
    private static long _skinSum, _skinOverflow, _skinDropped, _boneStalls, _skinNoSlot, _skinBoned, _boneOverflow,
                      _skinBrackets, _skinNotMode1, _skinBadRange;
    /// <summary>One-shot skin-capture diagnostics left to print (#73's open mode-1/mode-2 question).</summary>
    private static int _skinDiagLeft = 6;
    /// <summary>One-shot VAO dumps left to print for a VDO harvested inside a skin bracket (#96 triage).</summary>
    private static int _vaoDiagLeft = 6;
    /// <summary>Transition lines left for the cube/creature triage, and the state they compare against.</summary>
    private static int _cubeDiagLeft = 40;
    private static bool _cubeHadCreatures;

    // --- the per-frame live list: what Collect resolved, in the order Submit stages it. Separate from the
    // caster table because a view culls out of it while the table stays whole for the next view.
    private static readonly float[] _liveModel = new float[MaxCasters * 16];
    private static readonly float[] _liveCx = new float[MaxCasters], _liveCy = new float[MaxCasters],
                                    _liveCz = new float[MaxCasters], _liveR = new float[MaxCasters];
    private static readonly int[] _liveIdxCount = new int[MaxCasters];
    private static readonly uint[] _liveFirst = new uint[MaxCasters], _liveBaseVert = new uint[MaxCasters];
    private static readonly int[] _liveVao = new int[MaxCasters], _livePrim = new int[MaxCasters];
    /// <summary>This frame's diffuse texture id for a MASKED caster (0 = not masked, or the texture did not
    /// resolve). Re-fetched every collect: a <c>CAurTexture*</c> is borrowed and freed on area transitions,
    /// so it is never cached -- and the per-frame fetch is what keeps an animated diffuse cutting the right
    /// silhouette.</summary>
    private static readonly int[] _liveTex = new int[MaxCasters];
    /// <summary>...and the discard threshold that goes with it, staged into the per-caster record.</summary>
    private static readonly float[] _liveCut = new float[MaxCasters];
    /// <summary>Bone SLOT of this live caster, or -1 for a rigid one. The ring region's base is added at
    /// submit -- Collect runs before the bone upload picks a region, and the record wants the flat index.</summary>
    private static readonly int[] _liveBone = new int[MaxCasters];
    /// <summary>Creature casters occupy the head of the live list (#100 stages them first); the static world
    /// follows. Submit uses the split to report what a view actually took.</summary>
    private static int _liveCreatures;
    private static int _liveCount;
    /// <summary>The whole live list as an index list, so every view stages through one code path. Filled once
    /// per <see cref="Collect"/>; the sun's views take it entire.</summary>
    private static readonly int[] _allIdx = new int[MaxCasters];
    /// <summary>#151: the casters within reach of ONE cube light, filtered once and then walked by all
    /// six of its faces. The sphere test is face-independent, so doing it per face was six times the work.</summary>
    private static readonly int[] _lightIdx = new int[MaxCasters];
    private static long _ownedInstanceCasts;

    /// <summary>Frames where <see cref="Collect"/> lost over half its candidates to a live re-read (a mid-frame
    /// Repack moving the item tables, a mass revoke). #187 deleted the guard that skipped such a frame -- with
    /// no caches to poison there is nothing to hold back -- but the frame is still real, and now bakes
    /// near-empty depth instead: shadows drop out for one frame rather than freezing. The counter stays so that
    /// flicker is attributable rather than mysterious.</summary>
    private static long _degenSum;
    /// <summary>Creature casters the last <see cref="Submit"/> staged -- the number that answers "why did the
    /// shadow vanish": a cube whose faces take zero creature casters cannot show one.</summary>
    private static int _submitCreatures;
    /// <summary>Casters the last <see cref="Submit"/> actually staged (post-cull), for the cube tally.</summary>
    private static int _submitStaged;
    /// <summary>Cutout-caster forensics: draws that ran the 0.2 discard this period, and the three ways a
    /// masked caster can fail to reach the bake -- no UV stream to sample with, no diffuse this frame, or no
    /// cutout program at all. Each of them is a MISSING leaf shadow; none of them is the solid quad.</summary>
    private static long _submitMasked;
    private static int _maskNoUv, _maskNoTex, _maskNoProg;
    /// <summary>Masked casters ENROLLED (as opposed to drawn): the number that separates "the cutout casters
    /// are not being drawn" from "there are none in the caster set to draw", which the draw count alone
    /// cannot.</summary>
    private static int _castMasked;

    // --- per-VDO VAO cache. Formats are harvested in the ENGINE context at enrollment; the 4.6-side VAO is
    // built lazily inside the bracket over the same shared buffers (#70: buffers share, VAOs do not).
    private static readonly IntPtr[] _vVdo = new IntPtr[MaxVdos];
    private static readonly uint[] _vVao46 = new uint[MaxVdos];
    private static readonly uint[] _vElemBuf = new uint[MaxVdos];
    private static readonly VertexStream[] _vPos = new VertexStream[MaxVdos];
    private static readonly bool[] _vStale = new bool[MaxVdos];
    /// <summary>The diffuse UV stream, harvested from the same engine VAO. Present only so a MASKED caster
    /// can sample its cutout; a plain depth draw ignores it.</summary>
    private static readonly VertexStream[] _vUv = new VertexStream[MaxVdos];
    /// <summary>#96: the two bone streams, harvested from the same engine VAO. Their PRESENCE is the skinned
    /// test at the level that matters -- a VDO whose VAO has no bone attributes cannot be skinned by us
    /// whatever the part's node says.</summary>
    private static readonly bool[] _vSkin = new bool[MaxVdos];
    private static readonly VertexStream[] _vBi = new VertexStream[MaxVdos], _vBw = new VertexStream[MaxVdos];
    private static int _vdoCount;

    /// <summary>One persistent-mapped submission ring: <see cref="Regions"/> regions of <see cref="Cap"/>
    /// commands + per-draw records, with a fence per region. The sun and the local cubes own SEPARATE rings
    /// because a region is consumed per VIEW and the two differ by an order of magnitude in both directions --
    /// the sun submits 4 views of up to 8192 casters, the cubes 6 views per light of a few hundred. One shared
    /// ring sized for both would either wrap inside a frame (and stall on a fence issued that same frame) or
    /// cost a hundred megabytes of mapped storage.</summary>
    private struct RingBuf
    {
        public uint Cmd, Draw;
        public byte* CmdMap;
        public float* DrawMap;
        public int Cap, Regions, At;
        public IntPtr[] Fence;
    }

    // --- 4.6-side objects (touched only inside the bracket)
    private static uint _prog, _fbo, _depthTex;
    private static int _locVp = -1;
    /// <summary>The masked (alpha-tested) depth program and its uniforms. Zero = it could not build, and
    /// masked casters then stay out of every bake.</summary>
    private static uint _progMask;
    private static int _locMaskVp = -1, _locMaskTex = -1;
    private static RingBuf _sunRing, _cubeRing;
    private static bool _glFailed, _progFailed;
    private static readonly StandDown _down = new("CasterPass DISARMED");

    // --- lifecycle + diagnostics
    private static int _sinceEnroll = EnrollEvery, _tableGen = -1;
    private static bool _wasInModule, _loggedOnce, _noSunLogged, _reportedHit;
    private static int _frames, _lastEnrolled, _lastCandidates, _overflow;
    private static long _draws, _mdiCalls, _culled, _fenceStalls, _lodCulled;
    /// <summary>Which half of the fit test rejected a caster -- off the light axis, or past the fit's ends.
    /// Without the split, "everything culled" and "nothing enrolled" read identically in the period line.</summary>
    private static long _cullPerp, _cullAlong;
    /// <summary>Casters a VIEW could not stage because its ring region filled (distinct from the enrollment
    /// overflow, which is the whole table being over capacity), and cubes rendered per frame.</summary>
    private static long _viewOverflow, _cubeLightsSum;
    /// <summary>#186: casters STAGED into cube faces this period -- the cube-submission figure the local-light
    /// reach is measured by. Separate from <see cref="_draws"/>, which the sun's four cascades dominate.</summary>
    private static long _cubeStaged;
    private static int _cullDiagLeft = 4;

    /// <summary>Views rendered on the last frame -- every one of them a bake since #187 -- plus the CPU
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

        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _getTexture = (delegate* unmanaged<IntPtr, int, long>)Native.Sym(SymbolMap.MatGetTexture);
        if (_primTable == null)
        { log.Info("CasterPass: primitive-type table unresolved -- Alloy caster submission unavailable"); return; }

        // Repack moves a VDO's item indices and offsets under us. Every other consumer of these tables
        // already invalidates on it (#88 chain: batcher 50, interim cube 40, Alloy colour 30) and this pass
        // must too -- a stale item index does not read as an empty draw, it reads as ANOTHER part's index
        // range, which submits out-of-range indices to the GPU.
        _repackSlot = hooks.Chain(SymbolMap.VdoRepack,
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnVdoRepack, RepackPriority);
        if (_repackSlot == null)
        { log.Info("CasterPass: could not chain VdoRepack -- refusing to arm on stale index ranges"); return; }

        _skinned = Settings.Bool("skincast", true);
        if (_skinned)
        {
            _bones = (float*)Native.Sym(SymbolMap.GlRenderBones);
            if (_bones == null)
            { _skinned = false; log.Info("CasterPass: GLRender::m_bones unresolved -- skinned casters unavailable"); }
        }

        _armed = true;
        log.Info($"CasterPass: armed -- Alloy multidraw-indirect caster submission into a {_cascades}x{_mapSize}" +
                 $" sun cascade array out to {_maxDist:F0}u (config csm_cascades/csm_size, /shadow dist)" +
                 $" and {_cubeLights}x{_cubeSize} local-light shadow cubes (config cube_count/cube_size)" +
                 (_skinned ? ", skinned casters posed from per-frame bone snapshots (config skincast=0 disables)" : "") +
                 "; needs BatchService's capture tables, so a disarmed batcher leaves it with nothing to enroll");
    }

    /// <summary>The caster pass's live dials, for <c>/shadow</c> (ADR 0013 bucket 1). Both are look
    /// judgements made while walking around -- the cascade reach and the sub-texel cull that trades far-away
    /// detail for bake time -- and neither reallocates anything, which is what keeps them out of the config
    /// file next to the map sizes.</summary>
    internal static string Describe()
        => $"dist {_maxDist:F0} lod {_lodTexels:F1} ({_cascades}x{_mapSize} cascades, {_cubeLights}x{_cubeSize} cubes)";

    /// <summary>Set one dial, returning its old value; NaN when the key is not ours, which is how the
    /// dispatcher knows to try the scene feed's dials instead.</summary>
    internal static float Tune(string key, float v)
    {
        switch (key)
        {
            case "dist":
                float od = _maxDist;
                _maxDist = MathF.Max(v, 1f);
                return od;
            case "lod":
                float ol = _lodTexels;
                _lodTexels = MathF.Max(v, 0f);
                return ol;
            default: return float.NaN;
        }
    }

    /// <summary>Back to the boot values.</summary>
    internal static void ResetTuning() { _maxDist = MaxDistBoot; _lodTexels = LodBoot; }

    /// <summary>Chain priority on Repack: invalidation order among observers doesn't matter.</summary>
    private const int RepackPriority = 20;
    private static IntPtr* _repackSlot;

    /// <summary>Repack observer: the VDO's item indices just moved, so every caster on it is describing
    /// someone else's geometry until the next enrollment re-reads it.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoRepack(IntPtr vdo)
    {
        var next = (delegate* unmanaged<IntPtr, void>)*_repackSlot;
        if (next != null) next(vdo);
        for (int i = 0; i < _casterCount; i++)
            if (_cTri[i] != IntPtr.Zero && _cVdo[i] == vdo) _cTri[i] = IntPtr.Zero;
        // the live list was resolved from those same tables earlier this frame: drop it wholesale rather
        // than submit a view from index ranges that have already moved
        _liveCount = 0;
        // same for this frame's skinned captures, and close any open bracket so the part that is drawing right
        // now does not go on filing records against a bone slot the reset just handed back
        _skinCount = 0; _boneSlots = 0;
        SkinEnd();
    }

    /// <summary>#96 passive-capture bracket, opened by <see cref="BatchService"/> around the engine draw
    /// of a part on a skin-flagged node -- which in practice means any part of an animated creature, most of
    /// them rigid pieces posed by their node matrix rather than true skinmeshes. False = do not bracket
    /// (nothing to capture into), which costs the caller one test.
    /// <para>The part's transform is read HERE rather than at submit time: a skinned part is always on the
    /// engine's model-matrix path (ADR 0006 fact 2 -- the bucket loop pushes node+0x18 for a part with a skin),
    /// and by the time the depth pass runs, the node may have been recycled. Everything the submit needs is a
    /// copy by then; no skinned pointer is ever dereferenced outside this bracket.</para></summary>
    internal static bool SkinBegin(IntPtr part, IntPtr node)
    {
        if (!_armed || _disarmed || !_skinned || _bones == null || node == IntPtr.Zero) return false;
        if (!RenderMode.Alloy || !GlContextService.Available) return false;
        if (_skinCount >= MaxSkin) { _skinOverflow++; return false; }
        _skinBrackets++;   // the count that separates "never saw a creature" from "saw one and refused it"
        _skinPart = part; _skinSlot = -1;
        fixed (float* m = _skinModel) *(Matrix4x4*)m = new NodeRef(node).Model;
        PartRef p = new(part);
        float* mn = p.BboxMin;
        float* mx = p.BboxMax;
        // Centre from the model matrix translation, radius from the bbox extent -- the same derivation the
        // rigid matrix-path casters use, and for the same reason (the bbox centre is not object-space).
        _skinCx = _skinModel[12]; _skinCy = _skinModel[13]; _skinCz = _skinModel[14];
        float ex = (mx[0] - mn[0]) * 0.5f, ey = (mx[1] - mn[1]) * 0.5f, ez = (mx[2] - mn[2]) * 0.5f;
        _skinR = MathF.Sqrt(ex * ex + ey * ey + ez * ez);
        if (!(_skinR > MinSkinRadius)) _skinR = MinSkinRadius;   // also catches a NaN/degenerate box
        return true;
    }

    /// <summary>Funnel observation inside an open bracket: one engine draw of the bracketed part, which is one
    /// skinned caster record. True = consumed (the caller must not treat it as a batch capture).
    /// <para>Where a bone snapshot IS needed (a true skinmesh), it is taken on the FIRST record of the bracket
    /// and shared by the rest: a multi-draw part is several index ranges of one posed mesh, so they all skin
    /// from the same 64 matrices -- which is also why multi-draw parts cast here although the rigid path drops
    /// them.</para></summary>
    internal static bool SkinObserve(IntPtr vdo, int item, int prim)
    {
        if (_skinPart == IntPtr.Zero) return false;
        if (_skinDiagLeft > 0)
        {   // #73 closed on an OPEN question -- whether a creature's COLOUR draw is the mode-1 mesh or the
            // mode-2 projected-shadow stream. This pass only accepts mode-1, so that question decides whether
            // it works at all; name the first few draws rather than leave a silent zero to interpret.
            _skinDiagLeft--;
            _log?.Info($"CasterPass: skin-capture diag vdo=0x{(ulong)vdo:x} mode={(vdo == IntPtr.Zero ? -1 : new VdoRef(vdo).Mode)}" +
                       $" item={item} prim={prim} (mode 1 = the indexed mesh this pass can draw)");
        }
        if (_skinCount >= MaxSkin) { _skinOverflow++; return true; }
        if (vdo == IntPtr.Zero || new VdoRef(vdo).Mode != 1) { _skinNotMode1++; return true; }
        if (!TryRange(vdo, item, out _, out _, out _)) { _skinBadRange++; return true; }
        // one-entry memo: VdoSlot is a linear scan of up to MaxVdos, and unlike enrollment this runs per
        // skinned draw per FRAME -- consecutive parts of one creature share a VDO, so it usually hits
        int v;
        if (vdo == _skinLastVdo) v = _skinLastSlot;
        else
        {
            v = VdoSlot(vdo, reserve: true);
            // a miss is never memoised: the reserve that refused it is a table-pressure state, not a property
            // of this VDO, and Release frees slots -- memoising -1 would keep refusing it for the session
            if (v >= 0) { _skinLastVdo = vdo; _skinLastSlot = v; }
        }
        if (v < 0) { _skinNoSlot++; return true; }
        // The live-verified shape of an NWN creature: MOST of its parts are rigid meshes parented to a bone
        // and posed entirely by their node matrix -- their VAO carries position/uv/normal/tangent and no bone
        // stream at all (the engine's own shader runs its `skinmesh == 0` branch on them). Those need no bones
        // to cast a correctly animated shadow; the matrix captured in SkinBegin IS the animation. Only a TRUE
        // skinmesh -- a cloak, a robe, a tail -- carries attribs 8/9 and needs the snapshot.
        int bone = -1;
        if (_vSkin[v])
        {
            if (_skinSlot < 0)
            {
                // a real skinmesh drawn without its bones would shadow in its BIND pose, so drop it instead
                if (_boneSlots >= MaxBoneSlots) { _boneOverflow++; return true; }
                _skinSlot = _boneSlots++;
                fixed (float* dst = _boneStage)
                    Buffer.MemoryCopy(_bones, dst + _skinSlot * BoneFloats, BoneFloats * 4L, BoneFloats * 4L);
            }
            bone = _skinSlot;
            _skinBoned++;
        }
        int i = _skinCount++;
        _sVdo[i] = vdo; _sItem[i] = item; _sPrim[i] = prim; _sVao[i] = v; _sBone[i] = bone;
        fixed (float* src = _skinModel) fixed (float* dst = _sModel) *(Matrix4x4*)(dst + i * 16) = *(Matrix4x4*)src;
        _sCx[i] = _skinCx; _sCy[i] = _skinCy; _sCz[i] = _skinCz; _sR[i] = _skinR;
        return true;
    }

    internal static void SkinEnd() { _skinPart = IntPtr.Zero; _skinSlot = -1; }

    /// <summary>#151: the OWNED rigid instance part's capture. The engine-walk bracket above never sees a
    /// part the iterator suppresses, so once the colour pass owns a creature's rigid pieces (helmet, plates
    /// -- posed entirely by their node matrix), they silently left the shadow pass: the live report was a
    /// knight whose CLOAK cast (a true skinmesh, captured via ForceSkinPose) while his body cast nothing.
    /// AlloyRenderer calls this per Alloy instance part per pass with data it already holds; the record
    /// is exactly a rigid <see cref="SkinObserve"/> minus the funnel -- same arrays, same one-frame lag,
    /// bone -1 because the node matrix IS the animation.</summary>
    internal static void RecordOwnedInstance(IntPtr tri, IntPtr node, IntPtr vdo, int item, int prim)
    {
        if (!_armed || _disarmed || !_skinned || node == IntPtr.Zero || tri == IntPtr.Zero) return;
        if (!RenderMode.Alloy || !GlContextService.Available) return;
        if (_skinCount >= MaxSkin) { _skinOverflow++; return; }
        if (vdo == IntPtr.Zero || new VdoRef(vdo).Mode != 1) { _skinNotMode1++; return; }
        if (!TryRange(vdo, item, out _, out _, out _)) { _skinBadRange++; return; }
        int v;
        if (vdo == _skinLastVdo) v = _skinLastSlot;
        else
        {
            v = VdoSlot(vdo, reserve: true);
            if (v >= 0) { _skinLastVdo = vdo; _skinLastSlot = v; }
        }
        if (v < 0) { _skinNoSlot++; return; }
        int i = _skinCount++;
        _sVdo[i] = vdo; _sItem[i] = item; _sPrim[i] = prim; _sVao[i] = v; _sBone[i] = -1;
        Matrix4x4 model = *(Matrix4x4*)new NodeRef(node).Matrix;
        fixed (float* dst = _sModel) *(Matrix4x4*)(dst + i * 16) = model;
        PartRef p = new(tri);
        float* mn = p.BboxMin, mx = p.BboxMax;
        float ex = (mx[0] - mn[0]) * 0.5f, ey = (mx[1] - mn[1]) * 0.5f, ez = (mx[2] - mn[2]) * 0.5f;
        _sCx[i] = model.M41; _sCy[i] = model.M42; _sCz[i] = model.M43;
        _sR[i] = MathF.Sqrt(ex * ex + ey * ey + ez * ez);
        if (!(_sR[i] > MinSkinRadius)) _sR[i] = MinSkinRadius;
        _ownedInstanceCasts++;
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
        bool ok = CastMaskFs.Contains("if (texture(uTex, vUV).a <= vCut) discard;")
                  && CastMaskVs.Contains("vCut = c.aux.y;")
                  && CastMaskVs.Contains("gl_Position = uVP * c.model * vec4(aPos, 1.0);")
                  && CasterBufDecl.Contains($"binding = {BindCaster}") && DrawBytes == 96 && CmdBytes == 20
                  && CasterBufDecl.Contains("vec4 bounds") && CasterBufDecl.Contains("vec4 aux")
                  && CastVs.Contains("gl_BaseInstance") && !CastVs.Contains("gl_DrawID")
                  // ...and the masked stage indexes its record the same way, for the same M4 reason
                  && CastMaskVs.Contains("gl_BaseInstance") && !CastMaskVs.Contains("gl_DrawID")
                  // #96: the bone block's binding, and the two halves of the rigid/skinned contract -- the
                  // shader must treat a negative aux.x as rigid, and it must guard every weighted lane
                  && BoneBufDecl.Contains($"binding = {BindBones}") && CastVs.Contains("int bb = int(c.aux.x)")
                  && CastVs.Contains("if (bb >= 0)") && CastVs.Contains("aBoneWeight.w > 0.0")
                  && BoneCount == 64
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
                  && !CastVsSafe(CastVs.Replace("vec4 bounds; vec4 aux;", "vec4 bounds;"))
                  && !CastVsSafe(CastVs.Replace("uBone", "uNothing"))
                  && !CastFsSafe("#version 460 core\nvoid main() { discard; }")
                  && !CastFsSafe("#version 460 core\nvoid main() { gl_FragDepth = 0.5; }")
                  // the receiver's uSunVp array must be exactly as long as the cascade ceiling: a shorter one
                  // reads out of bounds on the outermost cascade, a longer one silently never fills
                  && AlloyLights.SunShadowLib.Contains($"uSunVp[{MaxCascades}]")
                  // same pin on the cube side: uLocalLight is indexed by a slot the CPU hands out, so a
                  // shorter array reads out of bounds the moment the count passes it
                  && AlloyLights.LocalShadowLib.Contains($"uLocalLight[{MaxCubes}]")
                  // one region per view, and the CPU runs a frame ahead: a ring that only covered one frame's
                  // views would stall on a fence it issued itself
                  && SunRegions >= MaxCascades * 2 && RegionsPerCube >= CubeFaces * 2;
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
            : $"CasterPass.SelfCheck: FAILED -- indirect/per-draw contract drifted (binding {BindCaster}, " +
              $"record {DrawBytes}B, command {CmdBytes}B, gl_BaseInstance indexing); standing down");
        return ok;
    }

    /// <summary>#118: the world-caster bake, driven from the pass slot (<c>Scene::AddPartsToDrawBuckets</c>,
    /// after-original) so the cascades and cubes are baked AHEAD of the colour pass that samples them. It ran
    /// at the present until #118, i.e. after its own reader -- ADR 0011's unowned one-frame lag.
    /// <para>Creature casters keep a one-frame pose lag, and that is deliberate (#113): their poses are
    /// captured DURING the colour pass (<see cref="SkinBegin"/>/<see cref="SkinEnd"/> bracket
    /// <c>PartTriMesh::Draw</c>), so the records latched below are the previous colour pass's. Retiring it
    /// needs a pose-capture walk ahead of this slot, which is M4.</para>
    /// Enrollment runs in the ENGINE context (it queries engine VAOs); everything else runs inside the #80
    /// bracket.</summary>
    public static void OnPassSlot()
    {
        if (!_armed || _disarmed) return;
        _frameSeq++;   // before the early-outs: a frame that renders nothing must READ as stale, not as fresh
        // Close this frame's skin capture window before ANY early-out. The records stay put -- the next walk
        // overwrites them from index 0, and nothing reads them outside this call -- but the COUNT is what a
        // view submits from, so a frame that captured nothing must submit nothing rather than last frame's pose.
        _skinLive = _skinCount; _skinCount = 0;   // the PREVIOUS colour pass's poses -- #113's named lag
        _boneSlotsLive = _boneSlots; _boneSlots = 0;
        _skinSum += _skinLive;
        // The overlay's view readout describes THIS frame, so it has to be zeroed ahead of every early-out
        // below -- a frozen count left over from the last frame that rendered reads as a pass still baking.
        LastViews = 0;
        Lifecycle();
        // AFTER the period line, which reports it: zeroing the timing ahead of Lifecycle made every period
        // line read 0.00 ms, since the frame's own measurement lands further down.
        LastShadowMs = 0f;
        if (!RenderMode.Alloy || !GameGate.InModule || !GlContextService.Available) return;
        if (_sinceEnroll < EnrollEvery) _sinceEnroll++; else Enroll();
        if (_casterCount == 0 && _skinLive == 0) return;

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
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _qShadowOn = _qShadow.Begin(Gl.TIME_ELAPSED);
        UploadBones();
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
        if (_qShadowOn) { _qShadow.End(Gl.TIME_ELAPSED); _qShadowOn = false; }
        LastShadowMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000f / System.Diagnostics.Stopwatch.Frequency;
        _viewSum += LastViews;
        // One fence per FRAME on the bone region: it was written once before any view and read by all of them.
        // Only a frame that actually WROTE a region fences and advances -- a frame with no skinned casters
        // would otherwise leak a sync object per frame, and a frame that SKIPPED the region on a fence timeout
        // would overwrite the very fence that made it skip, letting the next frame write under a live read.
        if (_boneBuf != 0 && _boneRegionBase >= 0)
        {
            GpuRing.Signal(_boneFence, _boneAt);
            _boneAt = (_boneAt + 1) % BoneRegions;
        }
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
        for (int i = 0; i < _cascades; i++)
        {
            float radius = ShadowMath.CascadeRadius(i, _cascades, _maxDist);
            float margin = FitMargin();
            Matrix4x4 vp = ShadowMath.DirectionalLightViewProj(dir, focus, radius, margin, _mapSize);
            bool viewOk;
            fixed (int* all = _allIdx)
                viewOk = RenderView(ref _sunRing, _depthTex, _mapSize, i, &vp,
                                    dir, focus, radius, margin, point: false, all, _liveCount);
            if (!viewOk) break;
            fixed (float* pub = CascadeVp) *(Matrix4x4*)(pub + i * 16) = vp;
            CascadeTexel[i] = 2f * radius / _mapSize;
            CascadeRange[i] = 2f * radius + margin;
            done++;
        }
        CascadeTex = _depthTex;
        // CascadeCount is the ONLY thing bounding the receiver's read: a stall at cascade i leaves layers
        // >= i holding last frame's depth and CascadeVp[>= i] holding last frame's matrix, and the
        // receiver's loop simply never reaches them. Same invariant covers the uniform upload, which
        // sends only `done` matrices into a mat4[4] and leaves the tail stale on purpose.
        CascadeCount = done;
        if (done > 0) _cascadeStamp = _frameSeq;
    }

    /// <summary>The local-light cubes: six faces per light, nearest to the shadow focus first, all six baked
    /// every frame (#187) -- so a carried torch and a wall sconce cost the same and neither can hold depth
    /// for a world that has moved on.
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
            // Rank by distance to the shadow FOCUS (the PC), not the raw camera. The captured camera is
            // whatever the last SetViewTransform left -- live-observed as (0, 0, 742), a UI/ortho pass, and
            // #118's move to the pass slot was not verified to change it. Sorting by distance to a point 742u overhead ranks lights by
            // ALTITUDE, so which lights get a cube is near-arbitrary and reshuffles as the light set shifts:
            // a carried torch's shadow blinks in and out as the player walks. The sun fit already takes the
            // focus for exactly this reason; the cubes were still reading the bad value.
            float d = (FrameInputs.LocalLightPos[i] - focus).LengthSquared();
            if (have == n && (n == 0 || d >= dist[n - 1])) continue;
            int k = have < n ? have : n - 1;
            while (k > 0 && dist[k - 1] > d) { order[k] = order[k - 1]; dist[k] = dist[k - 1]; k--; }
            order[k] = i; dist[k] = d;
            if (have < n) have++;
        }

        // A slot nobody claims is published INACTIVE (far = 0, id = 0) rather than compacted away: the
        // receiver reads far <= near as "lit", so holes in the slot array are free.
        for (int j = 0; j < _cubeLights; j++) { CubeLight[j * 4 + 3] = 0f; CubeLightId[j] = IntPtr.Zero; }

        int done = 0, frameCreatures = 0, nearestCreatures = -1;
        for (int s = 0; s < have; s++)
        {
            int li = order[s], slot = s;
            int lightCreatures = 0;
            Vector3 pos = FrameInputs.LocalLightPos[li];
            float far = FrameInputs.LocalLightRange[li];
            IntPtr id = FrameInputs.LocalLightId[li];
            if (!(far > CubeNear)) continue;   // a light with no reach records nothing worth sampling
            // #151: the sphere test is the same for all six faces, so it runs ONCE per light instead of six
            // times over the whole live list. Identical predicate to Submit's own off-axis test with
            // reach = far, so the faces cull exactly what they always did -- this only removes repetition.
            int ln = 0;
            for (int q = 0; q < _liveCount; q++)
            {
                float dx = _liveCx[q] - pos.X, dy = _liveCy[q] - pos.Y, dz = _liveCz[q] - pos.Z;
                float rr = far + _liveR[q];
                if (dx * dx + dy * dy + dz * dz <= rr * rr) _lightIdx[ln++] = q;
            }
            bool whole = true;
            fixed (int* near = _lightIdx)
                for (int f = 0; f < CubeFaces && whole; f++)
                {
                    Vector3 fwd = ShadowMath.CubeFaceForward(f);
                    Matrix4x4 vp = ShadowMath.PointLightFaceViewProj(pos, f, CubeNear, far);
                    whole = RenderView(ref _cubeRing, CubeTex, _cubeSize, slot * CubeFaces + f, &vp,
                                       fwd, pos, far, 0f, point: true, near, ln);
                    _cubeStaged += _submitStaged;
                    lightCreatures += _submitCreatures;
                }
            if (!whole) break;   // the ring is behind; the lights after this one would stall too
            frameCreatures += lightCreatures;
            if (s == 0) nearestCreatures = lightCreatures;
            CubeLight[slot * 4] = pos.X; CubeLight[slot * 4 + 1] = pos.Y; CubeLight[slot * 4 + 2] = pos.Z;
            CubeLight[slot * 4 + 3] = far;
            CubeLightId[slot] = id;
            done++;
        }
        CubeCount = done > 0 ? _cubeLights : 0;
        if (done > 0) _cubeStamp = _frameSeq;
        _cubeLightsSum += done;

        // #96 triage: a creature shadow that blinks out is either not STAGED (cull, capture, capacity) or not
        // SAMPLED (receiver). This says which, and only speaks when the answer changes -- so the log line lands
        // on the frame the shadow disappeared instead of being buried in a period average.
        bool any = nearestCreatures > 0;
        if (_cubeDiagLeft > 0 && any != _cubeHadCreatures)
        {
            _cubeDiagLeft--;
            _cubeHadCreatures = any;
            Vector3 c = FrameInputs.HasCamera ? FrameInputs.CameraPos : default;
            _log?.Info($"CasterPass: cube-creature {(any ? "APPEARED" : "VANISHED")} in the NEAREST light -- " +
                       $"{nearestCreatures} stagings there, {frameCreatures} across {done} cubes, " +
                       $"{_liveCreatures} creature casters live, {_liveCount - _liveCreatures} static, " +
                       $"focus=({focus.X:F1},{focus.Y:F1},{focus.Z:F1}) camera=({c.X:F1},{c.Y:F1},{c.Z:F1})");
        }
        else if (any != _cubeHadCreatures) _cubeHadCreatures = any;
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

    /// <summary>Module unload, a BatchService table wipe and a lost 4.6 context all invalidate every recorded
    /// identity wholesale; the period line is the only evidence the pass runs at all.
    /// <para>#118: this runs from the pass slot, which need not fire outside a module at all, so the InModule
    /// edge below is no longer the load-bearing one. TableGen is: BatchService ticks from the present
    /// unconditionally and wipes on module unload, so the first tick back inside a module releases.</para></summary>
    private static void Lifecycle()
    {
        if (!GlContextService.Available) Release("4.6 context lost");
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
                   $", {(float)_cubeLightsSum / _frames:F1}/{_cubeLights} local cubes at " +
                   $"{_cubeSize}px taking {(float)_cubeStaged / _frames:F0} caster submissions/frame" +
                   $", {_lastEnrolled}/{_lastCandidates} enrolled on {_vdoCount} VDOs" +
                   (_overflow > 0 ? $" (+{_overflow} over capacity)" : "") +
                   $", {(float)_viewSum / _frames:F1} views baked per frame ({LastShadowMs:F2} ms cpu last" +
                   (_qShadow.Measured ? $", {_qShadow.Avg / 1e6:F3} ms GPU avg" : "") + ")" +
                   $", {(float)_draws / _frames:F0} depth draws/frame in {(float)_mdiCalls / _frames:F1} " +
                   $"multidraw-indirect calls, " +
                   $"{(float)_culled / _frames:F0} culled/frame (off-axis {_cullPerp}, past-ends {_cullAlong}), " +
                   (_skinned ? $"{(float)_skinSum / _frames:F1} creature casters/frame from " +
                              $"{(float)_skinBrackets / _frames:F1} creature part draws seen" : "creature casters off") +
                   (_skinNotMode1 > 0 ? $", {_skinNotMode1} not the indexed mode-1 mesh (see #73)" : "") +
                   (_skinBadRange > 0 ? $", {_skinBadRange} with no live index range" : "") +
                   (_skinOverflow > 0 ? $" (+{_skinOverflow} over capacity)" : "") +
                   (_skinBoned > 0 ? $" ({(float)_skinBoned / _frames:F1} of them true skinmeshes posed from bones)" : "") +
                   (_boneOverflow > 0 ? $", {_boneOverflow} skinmeshes over the bone-slot budget" : "") +
                   (_skinNoSlot > 0 ? $", {_skinNoSlot} skinned draws refused a VDO slot (table reserved for rigid casters)" : "") +
                   (_skinDropped > 0 ? $", {_skinDropped} skinned draws dropped for want of a bone region" : "") +
                   (_boneStalls > 0 ? $" ({_boneStalls} frames of it on a bone-ring fence timeout)" : "") +
                   $", LOD dropped {_lodCulled / (float)MathF.Max(_frames, 1):F0} sub-texel casters/frame (/shadow lod {_lodTexels}), " +
                   $"{_ownedInstanceCasts / (float)MathF.Max(_frames, 1):F0} Alloy instance parts recast/frame" +
                   (_fenceStalls > 0 ? $", {_fenceStalls} views SKIPPED on a ring-fence timeout" : "") +
                   (_viewOverflow > 0 ? $", {_viewOverflow} casters dropped by a full view region" : "") +
                   (_degenSum > 0 ? $", {_degenSum} DEGENERATE collects baked near-empty" : "") +
                   // The cutout casters: env-mapped materials the engine calls opaque without ever reading
                   // their diffuse alpha, so their depth draw runs the same 0.2 discard the colour pass does.
                   // Zero here with foliage on screen means they are all falling out at one of the three
                   // terms after it.
                   $", {_texWithAlpha}/{_texQueried} caster textures carry alpha -> " +
                   $"{_submitMasked} alpha-tested depth draws this period" +
                   (_maskNoUv + _maskNoTex + _maskNoProg == 0 ? ""
                    : $" ({_maskNoUv} masked casters have no UV stream, {_maskNoTex} no diffuse this frame, " +
                      $"{_maskNoProg} no cutout program -- each casts nothing)"));
        _frames = 0; _draws = 0; _mdiCalls = 0; _culled = 0; _fenceStalls = 0;
        _cullPerp = 0; _cullAlong = 0; _viewOverflow = 0; _cubeLightsSum = 0; _cubeStaged = 0; _viewSum = 0;
        _lodCulled = 0; _ownedInstanceCasts = 0; _degenSum = 0; _qShadow.Reset();
        _skinSum = 0; _skinOverflow = 0; _skinDropped = 0; _boneStalls = 0; _skinNoSlot = 0;
        _skinBrackets = 0; _skinNotMode1 = 0; _skinBadRange = 0; _skinBoned = 0; _boneOverflow = 0;
        _submitMasked = 0; _maskNoTex = 0; _maskNoProg = 0;
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
        bool any = _casterCount > 0;
        for (int v = 0; v < _vdoCount; v++)
            if (_vVdo[v] != IntPtr.Zero) { any = true; _vVdo[v] = IntPtr.Zero; _vStale[v] = true; _vSkin[v] = false; }
        // the skinned records name VDOs that may have just been freed with the module; they cost nothing to
        // rebuild (the next walk re-captures them) and everything to submit stale
        _skinCount = 0; _skinLive = 0; _boneSlots = 0; _boneSlotsLive = 0;
        _skinLastVdo = IntPtr.Zero; _skinLastSlot = -1;   // the memo names a slot the loop above just retired
        SkinEnd();
        if (!any) return;
        for (int i = 0; i < _casterCount; i++) _cTri[i] = IntPtr.Zero;
        _casterCount = 0; _liveCount = 0;
        CascadeCount = 0; CubeCount = 0;   // the maps describe a world that just went away; stop the receiver sampling them
        _sinceEnroll = EnrollEvery;
        _reportedHit = false;
        _log?.Info($"CasterPass: caster set released ({why})");
    }

    /// <summary>#128: re-enroll casters at the next frame boundary rather than waiting out the cadence.
    /// <para>The colour pass and this one keep independent 60-frame counters, and an instance only becomes a
    /// rigid caster candidate once it is StActive in the part table. So when the Alloy pass newly takes a
    /// creature its engine draw stops -- which is also what stops #96's passive bracket firing for it -- and
    /// until this side re-enrolls, that creature casts from NEITHER source. The window is up to a second, at
    /// every spawn and every area transition, and it reads as a shadow blinking out. One poke closes it.</para>
    /// <para>Cheap to over-call: the cost is one table scan.</para></summary>
    internal static void ForceEnroll() => _sinceEnroll = EnrollEvery;

    /// <summary>Cold, once per <see cref="EnrollEvery"/> frames: rebuild the caster set wholesale from
    /// BatchService's capture tables. Tiles and placeables come out of the same scan under the same
    /// predicate -- the only per-caster difference is <see cref="CasterCandidate.Mx"/>, which decides whether
    /// its model matrix is the node's or the identity, and that is geometry, not kind.
    /// <para>The set can only hold what the engine's bucket walk carried, so the walk -- not this scan -- is
    /// what decides whether a caster behind the camera exists at all. <c>SceneInputs</c> widens it on
    /// exactly the frames this runs (<see cref="WantsWideWalk"/>): a BSP sphere re-walk out to
    /// <see cref="WalkRadius"/> for static geometry, plus an off-screen gob append for movers. #101 verified
    /// live that this holds -- no pop-in on approach, and rotating on the spot does not change which objects
    /// cast.
    /// <para>ponytail: the gob append is still capped at <c>/shadow nearradius</c> (25u), a radius named
    /// and sized for the interim two-map scheme's near map, which the #98 cutover deleted. Static geometry
    /// reaches the full 120u; an off-screen MOVER past 25u casts into no cascade. Raise it if a distant
    /// creature is ever seen not casting -- it costs gobs appended per frame, which is why it is not simply
    /// pinned to <see cref="WalkRadius"/>.</para></para></summary>
    private static void Enroll()
    {
        _sinceEnroll = 0;
        _casterCount = 0; _overflow = 0; _maskNoUv = 0; _castMasked = 0;
        Array.Clear(_sortCount, 0, MaxVdos);
        var c = default(CasterCandidate);
        int candidates = 0;
        for (int e = 0, n = PartTable.EntryCount; e < n; e++)
        {
            if (!AlloyCensus.TryCasterCandidate(e, ref c)) continue;
            candidates++;
            if (_casterCount >= MaxCasters) { _overflow++; continue; }
            int v = VdoSlot(c.Vdo);
            if (v < 0) continue;
            int i = _casterCount++;
            _cTri[i] = c.Tri; _cNode[i] = c.Node; _cMat[i] = c.Mat; _cVdo[i] = c.Vdo;
            _cItem[i] = c.Item; _cPrim[i] = c.Prim; _cVao[i] = v; _cMx[i] = c.Mx;
            // A masked caster needs a UV stream to sample with; without one there is nothing to test and the
            // draw would be the solid quad again, so it stays a plain caster and the count says how many.
            // #126 follow-up: WHAT the discard threshold would be if this caster turns out to carry alpha.
            // Whether it does is a question for the texture, asked once per texture inside the bracket
            // (TexHasAlpha) -- never for the engine's opacity flags, which short-circuit three different ways
            // (env map, gob alpha, and the material's own memo) and have sent three fixes to the wrong place.
            _cCut[i] = c.EnvAlpha ? EnvAlphaDiscard : CutoutDiscard;
            _cUv[i] = _vUv[v].Present;
            if (!_vUv[v].Present) _maskNoUv++;
            PartRef p = new(c.Tri);
            float* mn = p.BboxMin;
            float* mx = p.BboxMax;
            _cCx[i] = (mn[0] + mx[0]) * 0.5f; _cCy[i] = (mn[1] + mx[1]) * 0.5f; _cCz[i] = (mn[2] + mx[2]) * 0.5f;
            float ex = mx[0] - _cCx[i], ey = mx[1] - _cCy[i], ez = mx[2] - _cCz[i];
            _cR[i] = MathF.Sqrt(ex * ex + ey * ey + ez * ez);
            _sortCount[v]++;
        }
        // counting sort into _order so the submission walks grouped by VAO: one bind and (primitive changes
        // aside) one multidraw-indirect per VDO
        int at = 0;
        for (int k = 0; k < MaxVdos; k++) { int n = _sortCount[k]; _sortCount[k] = at; at += n; }
        for (int i = 0; i < _casterCount; i++) _order[_sortCount[_cVao[i]]++] = i;

        _lastEnrolled = _casterCount; _lastCandidates = candidates;
        if (_casterCount > 0 && !_reportedHit)
        {
            _reportedHit = true;
            _log?.Info($"CasterPass: enrolled {_casterCount}/{candidates} rigid casters on {_vdoCount} VDOs");
        }
    }

    /// <summary>How far the table may fill before a SKINNED capture stops minting new slots. The cache is
    /// shared and only ever cleared wholesale (<see cref="Release"/>), so creature churn -- which mints per
    /// frame, not per enrollment -- would otherwise fill it and starve the rigid enrollment, whose own
    /// out-of-slots path is a silent <c>continue</c>. Tiles and placeables losing their shadows because a
    /// crowd walked past is the one failure this reserve exists to make impossible; the skinned casters
    /// degrade first, and say so in the period line.</summary>
    private const int SkinVdoCeiling = MaxVdos - 128;

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

    /// <summary>Find (or harvest) the VAO-cache slot for a VDO. Harvesting reads the engine VAO's position
    /// format -- engine context, read-only queries, binding restored. <paramref name="reserve"/> = the caller
    /// may not mint past <see cref="SkinVdoCeiling"/>.</summary>
    private static int VdoSlot(IntPtr vdo, bool reserve = false)
    {
        int s = -1;
        for (int v = 0; v < _vdoCount; v++)
        {
            if (_vVdo[v] == vdo) return v;
            if (_vVdo[v] == IntPtr.Zero && s < 0) s = v;
        }
        if (s < 0)
        {
            if (_vdoCount >= MaxVdos || (reserve && _vdoCount >= SkinVdoCeiling)) return -1;
            s = _vdoCount++;
            _vVao46[s] = 0;
        }
        if (!Harvest(vdo, s)) return -1;
        _vVdo[s] = vdo; _vStale[s] = _vVao46[s] != 0;
        return s;
    }

    /// <summary>Read the engine VAO's position attribute format and element buffer. A depth draw consumes
    /// nothing else, and replicating attributes the program never reads is what crashed the driver's vertex
    /// pull in #81 -- so this binds one stream and no more.</summary>
    private static bool Harvest(IntPtr vdo, int s)
    {
        uint vao = new VdoRef(vdo).Vao;
        if (vao == 0) return false;
        int saved; Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &saved);
        Gl.BindVertexArray(vao);
        int eb; Gl.GetIntegerv(Gl.ELEMENT_ARRAY_BUFFER_BINDING, &eb);
        _vElemBuf[s] = (uint)eb;
        VertexArrays.Read(EngPos, out _vPos[s]);
        // #96: the bone streams. Both or neither -- a half-skinned VAO would skin some vertices and leave the
        // rest at the origin, so it is not a skinned caster as far as this pass is concerned.
        // The UV stream, for the masked casters' cutout. Optional: a VDO without one simply cannot carry a
        // masked caster (see Enroll).
        VertexArrays.Read(EngUv, out _vUv[s]);
        _vSkin[s] = _skinned
                    && VertexArrays.Read(EngBoneIdx, out _vBi[s])
                    && VertexArrays.Read(EngBoneW, out _vBw[s]);
        if (_vaoDiagLeft > 0 && _skinPart != IntPtr.Zero)
        {   // #96 live triage: attribs 8/9 are the right locations (ConnectShaderData binds vIndex/vWeight
            // there), so a creature VAO with nothing on them means the bone data is not where the plan says.
            // Enumerate what the VAO DOES carry rather than guess again. Cold: a few VDOs, once each.
            _vaoDiagLeft--;
            string had = "";
            for (uint loc = 0; loc < 16; loc++)
            {
                int on; Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_ENABLED, &on);
                if (on == 0) continue;
                int asz, aty, ast, abf;
                Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_SIZE, &asz);
                Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_TYPE, &aty);
                Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_STRIDE, &ast);
                Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_BUFFER_BINDING, &abf);
                had += $" {loc}:{asz}x0x{aty:x}/s{ast}/b{abf}";
            }
            _log?.Info($"CasterPass: skin-vao diag vdo=0x{(ulong)vdo:x} vao={vao} enabled ->{(had.Length == 0 ? " NOTHING" : had)}");
        }
        Gl.BindVertexArray((uint)saved);
        return _vElemBuf[s] != 0 && _vPos[s].Present;
    }


    /// <summary>One-shot 4.6-side setup shared by both views: the depth program and the FBO every layer is
    /// attached into. Any failure disarms the pass by name rather than leaving it silently drawing nothing.</summary>
    private static bool EnsureCommon()
    {
        if (_glFailed || _progFailed) return false;
        if (_prog == 0)
        {
            if (Gl.MultiDrawElementsIndirect == null || Gl.BufferStorage == null || Gl.MapBufferRange == null
                || Gl.BindBufferBase == null || Gl.FenceSync == null || Gl.ClientWaitSync == null
                || Gl.TexStorage3D == null || Gl.GenFramebuffers == null || Gl.CopyImageSubData == null)
            { Disarm("a required GL 4.4+ entry point is missing (multidraw-indirect / persistent mapping / image copy)"); return false; }
            uint p = GlProgram.Graphics(ShdCastVs, ShaderStages.Source(ShdCastVs),
                                        ShdCastFs, ShaderStages.Source(ShdCastFs),
                                        _log, "CasterPass depth program",
                                        out string castVs, out string castFs);
            if (p != 0 && !(CastVsSafe(castVs) && CastFsSafe(castFs)))
            {
                Gl.DeleteProgram(p);
                _log?.Info("CasterPass: the caster depth override is refused -- it declares a per-draw record "
                         + $"that is not the {DrawBytes}-byte SowxCaster being staged, has lost the skinning "
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
            if (!CreateRing(ref _sunRing, MaxCasters, SunRegions,
                            $"{_mapSize}x{_mapSize}x{_cascades} cascade array")) return false;
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
            // Global state, not a texture parameter: filtering blends across face borders, which is what
            // makes the receiver's hardware-PCF fetch legitimate right up to a face edge. The interim cubes
            // bought the same thing with an over-scanned face frustum and a matching uv shrink in the shader.
            Gl.Enable(Gl.TEXTURE_CUBE_MAP_SEAMLESS);
            if (!CreateRing(ref _cubeRing, MaxCubeCasters, _cubeLights * RegionsPerCube,
                            $"{_cubeSize}x{_cubeSize}x{_cubeLights} local-light cube array")) return false;
        }
        return true;
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

    // --- #96 bone ring: one region per frame, written before the views and read by all of them
    private static uint _boneBuf;
    private static float* _boneMap;
    private static readonly IntPtr[] _boneFence = new IntPtr[BoneRegions];
    private static int _boneAt;
    /// <summary>Flat bone index of the region this frame's snapshots went into; added to every skinned
    /// caster's bone base so the shader indexes one whole-buffer binding rather than a bound range.</summary>
    private static int _boneRegionBase = -1;

    /// <summary>Copy this frame's bone snapshots into the ring and bind them. Runs once per frame inside the
    /// bracket, before any view submits -- bones are a property of the world, not of a view.
    /// <para>A fence timeout leaves <see cref="_boneRegionBase"/> at -1, which drops every skinned caster from
    /// the frame (the rigid ones carry on): a blinked creature shadow beats overwriting bones the GPU is still
    /// reading out from under a live draw.</para></summary>
    private static void UploadBones()
    {
        _boneRegionBase = -1;
        if (!_skinned || _skinLive == 0 || _boneSlotsLive == 0) return;
        if (_boneBuf == 0)
        {
            if (!EnsureCommon()) return;
            long size = (long)BoneRegions * MaxBoneSlots * BoneFloats * 4;
            _boneMap = (float*)GpuRing.Map(Gl.SHADER_STORAGE_BUFFER, size, out _boneBuf);
            if (_boneMap == null) { Disarm("persistent mapping of the bone buffer was refused"); return; }
            _log?.Info($"CasterPass: {MaxBoneSlots} x {BoneCount}-bone snapshot ring ready " +
                       $"({size} bytes mapped, {BoneRegions} regions)");
        }
        int region = _boneAt;
        // NOT _fenceStalls: that counter reads "views skipped", and a bone stall skips no view -- it costs
        // the frame its skinned casters, which _skinDropped goes on to count per view
        if (!GpuRing.Acquire(_boneFence, region)) { _boneStalls++; return; }
        long at = (long)region * MaxBoneSlots * BoneFloats;
        long bytes = (long)_boneSlotsLive * BoneFloats * 4;
        fixed (float* src = _boneStage) Buffer.MemoryCopy(src, _boneMap + at, bytes, bytes);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindBones, _boneBuf);
        _boneRegionBase = region * MaxBoneSlots * BoneCount;   // in MATRICES: the record's aux.x indexes uBone[]
    }

    private static bool CreateRing(ref RingBuf r, int cap, int regions, string what)
    {
        long cmdSize = (long)regions * cap * CmdBytes;
        long drawSize = (long)regions * cap * DrawBytes;
        r.CmdMap = GpuRing.Map(Gl.DRAW_INDIRECT_BUFFER, cmdSize, out r.Cmd);
        r.DrawMap = (float*)GpuRing.Map(Gl.SHADER_STORAGE_BUFFER, drawSize, out r.Draw);
        if (r.CmdMap == null || r.DrawMap == null)
        { Disarm($"persistent mapping of the {what} indirect/per-draw buffers was refused"); return false; }
        r.Cap = cap; r.Regions = regions; r.At = 0;
        r.Fence = new IntPtr[regions];
        _log?.Info($"CasterPass: {what} + {regions}-region persistent rings ready " +
                   $"({cmdSize + drawSize} bytes mapped)");
        return true;
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
    }

    /// <summary>Once per frame, before any view submits: revalidate every caster and resolve its live world
    /// sphere and index range into the per-frame live list. Everything here is view-independent by
    /// construction, so every view of the frame stages from the one answer. Hot and alloc-free.</summary>
    private static void Collect()
    {
        // #96: this frame's creature casters, as ordinary live casters -- they differ from a rigid one in
        // exactly one field, the bone base, which is what "one Alloy submission path" means here. No
        // revalidation: every field is a copy taken inside the capture bracket this same frame, and the index
        // range is re-read live for the same reason the rigid path re-reads it (Repack moves items).
        // <para>#100: they go FIRST, ahead of the static world. A view that fills its staging region truncates
        // whatever is left, and the thing the player is looking at -- the moving creature at their feet --
        // must not be what gets cut. Losing a distant wall from one cube face is invisible; losing an arm is
        // what the flicker report was.</para>
        int live = 0, dropped = 0;
        for (int i = 0; i < _skinLive; i++)
        {
            if (live >= MaxCasters) { _skinOverflow += _skinLive - i; break; }   // never a silent drop
            if (!TryRange(_sVdo[i], _sItem[i], out int idxCount, out uint first, out uint baseVert)) continue;
            fixed (float* modelAll = _liveModel) fixed (float* src = _sModel)
                *(Matrix4x4*)(modelAll + live * 16) = *(Matrix4x4*)(src + i * 16);
            _liveCx[live] = _sCx[i]; _liveCy[live] = _sCy[i]; _liveCz[live] = _sCz[i]; _liveR[live] = _sR[i];
            _liveIdxCount[live] = idxCount;
            _liveFirst[live] = first;
            _liveBaseVert[live] = baseVert;
            _liveVao[live] = _sVao[i]; _livePrim[live] = _sPrim[i];
            // #96's creature casters are not classified: they come from the skin capture, not from the
            // caster table, so nothing has asked their material the alpha question. They cast solid, as
            // before -- creature parts are not cutout quads.
            _liveTex[live] = 0; _liveCut[live] = CutoutDiscard;
            _liveBone[live] = _sBone[i];   // the ring region's base is added at submit, after the upload
            live++;
        }
        _liveCreatures = live;   // creatures occupy [0, _liveCreatures); the static world follows

        fixed (float* modelAll = _liveModel)
        for (int k = 0; k < _casterCount; k++)
        {
            if (live >= MaxCasters) break;
            int i = _order[k];
            IntPtr tri = _cTri[i];
            if (tri == IntPtr.Zero) { dropped++; continue; }
            // recycled pointer / re-parented part: not ours any more, wait for the next enrollment
            PartRef p = new(tri);
            if (p.Node.Ptr != _cNode[i] || p.Material.Ptr != _cMat[i])
            { _cTri[i] = IntPtr.Zero; dropped++; continue; }

            Matrix4x4 model = _cMx[i] ? *(Matrix4x4*)new NodeRef(_cNode[i]).Matrix : Matrix4x4.Identity;

            // index range re-read live from the VDO item tables (they move under Repack)
            if (!TryRange(_cVdo[i], _cItem[i], out int idxCount, out uint first, out uint baseVert)) { dropped++; continue; }

            // World centre, exactly as the interim cube cull derives it (live-verified there): a matrix-class
            // part's centre is its model matrix TRANSLATION, read fresh because it moves; everything else is
            // world-space geometry whose bbox centre is already the answer. Running the bbox centre through
            // the model matrix -- which is what this did first -- double-transforms the matrix class and
            // throws it out of every fit, and there is no Ghidra evidence that bbox is object-space to begin
            // with. The bbox EXTENT is a world extent either way, so the radius stands.
            *(Matrix4x4*)(modelAll + live * 16) = model;
            _liveCx[live] = _cMx[i] ? model.M41 : _cCx[i];
            _liveCy[live] = _cMx[i] ? model.M42 : _cCy[i];
            _liveCz[live] = _cMx[i] ? model.M43 : _cCz[i];
            _liveR[live] = _cR[i];
            _liveIdxCount[live] = idxCount;
            _liveFirst[live] = first;
            _liveBaseVert[live] = baseVert;
            _liveVao[live] = _cVao[i]; _livePrim[live] = _cPrim[i];
            // The masked caster's diffuse, re-fetched this frame (borrowed pointer). A masked caster whose
            // texture does not resolve casts NOTHING this frame rather than casting its solid quad -- a
            // missing leaf shadow for a frame beats the black slab this fix exists to remove.
            // Every caster's diffuse, not just a suspected one: the texture decides, and a part with no
            // texture (or no UVs) simply casts solid, as it always did.
            _liveTex[live] = _cUv[i] ? MaskTid(_cMat[i]) : 0;
            _liveCut[live] = _cCut[i];
            _liveBone[live] = -1;
            live++;
        }
        _liveCount = live;
        if (_casterCount > 0 && dropped * 2 > _casterCount) _degenSum++;
        for (int i = 0; i < live; i++) _allIdx[i] = i;
    }

    /// <summary>#187: bake one view (a cascade layer, or one face of one cube) from what it sees, this frame.
    /// Stages the live casters this view can see into the next ring region and submits them as one
    /// multidraw-indirect per (VAO, primitive) group, into <paramref name="layer"/> of the depth array. Called
    /// once per sun cascade and once per cube face, over the one <see cref="Collect"/> that already ran this
    /// frame; <paramref name="point"/> picks which of the two culls applies. Hot and alloc-free: commands and
    /// per-draw records are written straight through the persistent mappings.
    /// <para>False = this view produced nothing usable (a ring-fence timeout, an incomplete FBO), and the
    /// caller must not publish it. The layer is cleared even when every caster culls out, because an uncleared
    /// layer is not "empty", it is LAST frame's depth -- shadows frozen where the geometry no longer is.</para></summary>
    private static bool RenderView(ref RingBuf ring, uint tex, int size, int layer,
                                   Matrix4x4* vp, Vector3 lightDir, Vector3 focus, float reach, float margin,
                                   bool point, int* list, int listCount)
    {
        LastViews++;
        return Submit(ref ring, tex, size, layer, vp, lightDir, focus, reach, margin, point, list, listCount);
    }

    private static bool Submit(ref RingBuf ring, uint tex, int size, int layer,
                               Matrix4x4* vp, Vector3 lightDir, Vector3 focus, float reach, float margin, bool point,
                               int* list, int listCount)
    {
        int region = ring.At;
        // zeroed AHEAD of the fence early-out: a view that stalls stages nothing, and a count left standing
        // is the PREVIOUS view's, which the cube tally would add a second time
        _submitCreatures = 0; _submitStaged = 0;
        // the GPU may still be reading this region's commands: overwriting them before its fence signals is
        // silent command corruption, so a wait that TIMES OUT skips the view rather than barging through
        if (!GpuRing.Acquire(ring.Fence, region))
        { _fenceStalls++; return false; }
        int cap = ring.Cap;
        uint* cmd = (uint*)(ring.CmdMap + (long)region * cap * CmdBytes);
        float* rec = ring.DrawMap + (long)region * cap * DrawFloats;
        int baseIndex = region * cap;

        int n = 0, culled = 0;
        // the fit's own extent: anything farther from the light axis than this cannot shade inside it
        float along = reach + margin;
        // #151 shadow LOD: this view's texel in world units -- ortho cascades span 2*reach across `size`
        // texels; a cube face spans 2*d at distance d, bounded by reach. Casters under _lodTexels of it are
        // sub-texel noise and skipped. Creatures are exempt: they sit at the head of the live list, and the
        // player's own shadow flickering out in a far cascade is the one regression this must not have.
        float lodR = _lodTexels * 2f * reach / size;
        if (_cullDiagLeft > 0 && _liveCount > 0)
        {   // a 100%-culled pass is indistinguishable from a dead one in the period line: name the inputs
            _cullDiagLeft--;
            _log?.Info($"CasterPass: cull inputs live={_liveCount} focus=({focus.X:F1},{focus.Y:F1},{focus.Z:F1}) " +
                       $"dir=({lightDir.X:F2},{lightDir.Y:F2},{lightDir.Z:F2}) reach={reach:F0} along={along:F0} " +
                       $"first=({_liveCx[0]:F1},{_liveCy[0]:F1},{_liveCz[0]:F1}) r={_liveR[0]:F1}");
        }
        fixed (float* modelAll = _liveModel)
        for (int w = 0; w < listCount; w++)
        {
            int i = list[w];
            float dx = _liveCx[i] - focus.X, dy = _liveCy[i] - focus.Y, dz = _liveCz[i] - focus.Z;
            float t = dx * lightDir.X + dy * lightDir.Y + dz * lightDir.Z;
            float r = _liveR[i];
            bool offAxis, offEnds;
            if (point)
            {
                // A cube face: the light is a POINT, so range decides what is recorded at all, and the face
                // itself is a 90deg square pyramid. #100: testing only range + "not behind me" admitted most
                // of a HEMISPHERE per face -- six times the geometry a face can see -- which overflowed the
                // per-face capacity and truncated the staging tail. Test the pyramid instead.
                // <para>The test is against the pyramid's CIRCUMSCRIBING cone (its corners sit at
                // |perp| = t*sqrt2), not its four side planes: a cone is rotation-invariant, so it needs no
                // per-face right/up basis and can never clip a corner the way a mis-rotated square would. It
                // admits a little more than the pyramid, which is the safe direction.</para>
                float dSq = dx * dx + dy * dy + dz * dz;
                offAxis = dSq > (reach + r) * (reach + r);
                offEnds = FaceCulls(dSq, t, r);
            }
            else
            {
                // CPU cull against the fit: distance from the light axis through the focus, plus the caster's
                // own radius. Splitting it into perpendicular/along keeps casters standing BETWEEN the light
                // and the fit (the classic near-clip miss) instead of culling them by plain range.
                float px = dx - t * lightDir.X, py = dy - t * lightDir.Y, pz = dz - t * lightDir.Z;
                offAxis = px * px + py * py + pz * pz > (reach + r) * (reach + r);
                offEnds = MathF.Abs(t) > along + r;
            }
            if (offAxis || offEnds)
            {
                culled++;
                if (offAxis) _cullPerp++; else _cullAlong++;
                continue;
            }
            if (r < lodR && i >= _liveCreatures) { _lodCulled++; continue; }
            // A skinned caster with no live bone region would draw in its BIND pose -- a T-posed shadow lying
            // next to the creature, which is worse than none. Drop it; the rigid casters carry on.
            if (_liveBone[i] >= 0 && _boneRegionBase < 0) { _skinDropped++; continue; }
            // #100: continue rather than break, so the count is CASTERS lost and not views that overflowed --
            // the old counter read ~2 a frame while each of those views was silently dropping its whole tail.
            // The remaining iterations only re-run the cull, which is the work this loop was doing anyway.
            if (n >= cap) { _viewOverflow++; continue; }
            if (!EnsureVao(_liveVao[i])) continue;
            // THE mask decision, and the only one that is not an inference: does this caster's diffuse carry
            // an alpha channel at all? Asked of GL, cached per texture id. No alpha -> nothing to test, cast
            // solid. Alpha -> run the discard, at the threshold the class chose.
            int masked = _liveTex[i] != 0 && _progMask != 0 && TexHasAlpha(_liveTex[i]) ? _liveTex[i] : 0;
            if (masked == 0 && _liveTex[i] != 0 && _progMask == 0) _maskNoProg++;

            uint* q = cmd + n * CmdUints;
            q[0] = (uint)_liveIdxCount[i];
            q[1] = 1;
            q[2] = _liveFirst[i];
            q[3] = _liveBaseVert[i];
            q[4] = (uint)(baseIndex + n);            // baseInstance IS the per-draw record index
            float* d = rec + n * DrawFloats;
            *(Matrix4x4*)d = *(Matrix4x4*)(modelAll + i * 16);
            d[16] = _liveCx[i]; d[17] = _liveCy[i]; d[18] = _liveCz[i]; d[19] = r;
            // aux is written on EVERY record, rigid included: ring regions are reused and never cleared, so a
            // rigid draw staged over a slot that last held a skinned one would inherit its bone base and skin
            // garbage. -1 is what makes a record rigid, and it has to be written to mean it.
            d[20] = _liveBone[i] < 0 ? -1f : _boneRegionBase + _liveBone[i] * BoneCount;
            // aux.y is the discard threshold this caster's fragment stage tests against, so one masked batch
            // can span a cutout and an opaque-but-alpha-carrying part without splitting on it.
            d[21] = _liveCut[i]; d[22] = 0f; d[23] = 0f;
            _stagedVao[n] = _liveVao[i]; _stagedPrim[n] = _livePrim[i]; _stagedTex[n] = masked;
            if (masked != 0) _submitMasked++;
            if (i < _liveCreatures) _submitCreatures++;
            n++;
        }
        _culled += culled;
        _submitStaged = n;

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, tex, 0, layer);
        Gl.DrawBuffer(Gl.NONE); Gl.ReadBuffer(Gl.NONE);
        if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
        { Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0); Disarm("the caster depth framebuffer is incomplete"); return false; }
        Gl.Viewport(0, 0, size, size);
        // depth state BEFORE the clear: a clear runs through the write mask and the scissor, so clearing
        // first would leave stale depth wherever a previous pass left either of them set
        new PassState(depth: true, depthWrite: true).Apply();
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.Clear(Gl.DEPTH_BUFFER_BIT);
        if (n == 0)
        {   // cleared == fully lit, which is the truth for a view nothing casts into
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
            return true;
        }
        Gl.UseProgram(_prog);
        Gl.UniformMatrix4fv(_locVp, 1, 0, (float*)vp);
        // unit 0 is where the cutout stage samples; nothing else in this bracket moves the active unit
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCaster, ring.Draw);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, ring.Cmd);

        long regionOff = (long)region * cap * CmdBytes;
        int start = 0, boundVao = -1, boundMask = 0, boundTex = -1;
        for (int j = 1; j <= n; j++)
        {
            bool cut = j == n || _stagedVao[j] != _stagedVao[start] || _stagedPrim[j] != _stagedPrim[start]
                       || _stagedTex[j] != _stagedTex[start];
            if (!cut) continue;
            // The masked batches run the cutout program with their diffuse bound; everything else runs the
            // position-only stage, which is the whole set on ordinary content.
            int cut0 = _stagedTex[start];
            if ((cut0 != 0 ? 1 : 0) != boundMask)
            {
                boundMask = cut0 != 0 ? 1 : 0;
                Gl.UseProgram(cut0 != 0 ? _progMask : _prog);
                Gl.UniformMatrix4fv(cut0 != 0 ? _locMaskVp : _locVp, 1, 0, (float*)vp);
                if (cut0 != 0) Gl.Uniform1i(_locMaskTex, 0);
            }
            if (cut0 != 0 && cut0 != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)cut0); boundTex = cut0; }
            if (_stagedVao[start] != boundVao)
            { Gl.BindVertexArray(_vVao46[_stagedVao[start]]); boundVao = _stagedVao[start]; }
            Gl.MultiDrawElementsIndirect(_primTable[_stagedPrim[start]], GlUnsignedShort,
                                         (void*)(regionOff + (long)start * CmdBytes), j - start, 0);
            _mdiCalls++;
            start = j;
        }
        _draws += n;

        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, 0);
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        GpuRing.Signal(ring.Fence, region);
        ring.At = (region + 1) % ring.Regions;
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

    /// <summary>Per-staged-draw group keys, parallel to the command slots the loop above fills.</summary>
    private static readonly int[] _stagedVao = new int[MaxCasters], _stagedPrim = new int[MaxCasters];
    /// <summary>#126 follow-up: the diffuse this staged draw discards against, 0 for a solid caster. Part of
    /// the batch cut key -- one multidraw carries one texture and one program.</summary>
    private static readonly int[] _stagedTex = new int[MaxCasters];

    /// <summary>Build the 4.6-side VAO over the SHARED engine buffers from the harvested position format.
    /// Position only: a depth draw reads nothing else.</summary>
    private static bool EnsureVao(int v)
    {
        if (_vVao46[v] != 0 && !_vStale[v]) return true;
        if (_vVao46[v] != 0) { uint old = _vVao46[v]; Gl.DeleteVertexArrays(1, &old); _vVao46[v] = 0; }
        uint vao; Gl.GenVertexArrays(1, &vao);
        Gl.BindVertexArray(vao);
        VertexArrays.Bind(LocPos, in _vPos[v]);
        if (_vUv[v].Present) VertexArrays.Bind(LocUv, in _vUv[v]);
        if (_vSkin[v])
        {
            VertexArrays.Bind(LocBoneIdx, in _vBi[v]);
            VertexArrays.Bind(LocBoneW, in _vBw[v]);
        }
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _vElemBuf[v]);
        Gl.BindVertexArray(0);
        _vVao46[v] = vao; _vStale[v] = false;
        return true;
    }

    /// <summary>Resrefs the caster pass looks up before falling back to the GLSL below (#92).</summary>
    internal const string ShdCastVs = "cx_cast_vs", ShdCastFs = "cx_cast_fs";

    /// <summary>#161: the caster vertex stage's half of the override contract -- the per-draw record it reads
    /// (counted, so an override may still reformat the fields) and #96's skinning branch. A stage that lost
    /// the branch links perfectly, the bone attributes simply go unread, and every creature casts its BIND
    /// POSE's shadow while the creature itself animates.</summary>
    private static bool CastVsSafe(string vs)
        => ShaderContract.RecordSafe(vs, "SowxCaster", (DrawFloats - 16) / 4)
           && vs.Contains("uBone");

    /// <summary>#161: the caster fragment stage must stay inert. It runs into a depth-only FBO, so the only
    /// two things it can say that matter are <c>discard</c> -- which writes no depth, i.e. the caster casts no
    /// shadow at all -- and <c>gl_FragDepth</c>, which displaces the depth the map is compared against into
    /// acne or peter-panning. Neither errors and neither shows in the log. An override past both is provably
    /// unable to change what the pass bakes.</summary>
    private static bool CastFsSafe(string fs)
        => !fs.Contains("discard") && !fs.Contains("gl_FragDepth");

    /// <summary>The per-draw block. Indexed by <c>gl_BaseInstance</c>, NOT <c>gl_DrawID</c>: M4's compute
    /// culling compacts the command buffer, which renumbers gl_DrawID but leaves every surviving command
    /// carrying its own record index -- so the layout survives culling without a reformat. <c>bounds</c> is
    /// the caster's world bounding sphere, unread by the depth stage and present so that culler can test it
    /// from the same record. <c>binding = 5</c> must stay in step with <see cref="BindCaster"/>.</summary>
    private const string CasterBufDecl = """
        struct SowxCaster { mat4 model; vec4 bounds; vec4 aux; };
        layout(std430, binding = 5) readonly buffer SowxCasterBuf { SowxCaster uCaster[]; };

        """;
    /// <summary>#96's bone block: one flat <c>mat4[]</c> spanning every ring region, so a draw indexes it with
    /// a single add rather than a bound range. A record's <c>aux.x</c> is the MATRIX index of its first bone;
    /// negative means rigid. std430 gives a <c>mat4</c> array a 64-byte stride, which is exactly the engine's
    /// own <c>m_bones</c> layout, so the snapshot is a straight memcpy.
    /// <c>binding = 6</c> must stay in step with <see cref="BindBones"/>.</summary>
    private const string BoneBufDecl = """
        layout(std430, binding = 6) readonly buffer SowxBoneBuf { mat4 uBone[]; };

        """;
    /// <summary>Depth vertex stage. The skinning branch mirrors the engine's own
    /// <c>TransformMainCoordinates</c> exactly, <c>vWeight &gt; 0.0</c> guards included -- those are not a
    /// micro-optimisation but bounds protection: an unused lane carries a junk bone index, which an SSBO read
    /// tolerates far less gracefully than the engine's fixed-size uniform array did. The model matrix applies
    /// ON TOP of the skinned position, as it does in the engine (<c>m_mvp * vLocalPosition</c>).</summary>
    internal const string CastVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        layout(location = 8) in vec4 aBoneIdx;
        layout(location = 9) in vec4 aBoneWeight;

        """ + CasterBufDecl + BoneBufDecl + """
        uniform mat4 uVP;
        void main()
        {
            SowxCaster c = uCaster[gl_BaseInstance];
            vec4 p = vec4(aPos, 1.0);
            int bb = int(c.aux.x);
            if (bb >= 0)
            {
                vec4 skinned = vec4(0.0);
                if (aBoneWeight.x > 0.0) skinned  = uBone[bb + int(aBoneIdx.x)] * p * aBoneWeight.x;
                if (aBoneWeight.y > 0.0) skinned += uBone[bb + int(aBoneIdx.y)] * p * aBoneWeight.y;
                if (aBoneWeight.z > 0.0) skinned += uBone[bb + int(aBoneIdx.z)] * p * aBoneWeight.z;
                if (aBoneWeight.w > 0.0) skinned += uBone[bb + int(aBoneIdx.w)] * p * aBoneWeight.w;
                p = skinned;
            }
            gl_Position = uVP * c.model * p;
        }
        """;
    /// <summary>Depth-only: the fixed-function depth write is the whole output.</summary>
    internal const string CastFs = """
        #version 460 core
        void main() { }
        """;

    /// <summary>The masked caster's depth stage: the same clip position, plus the diffuse UVs, so the
    /// fragment stage can punch the cutout's holes into the shadow map. No skinning -- a masked caster comes
    /// from the static caster table, never from the #96 skin capture.
    /// <para>No resref, for <see cref="DepthKeyVs"/>'s reason one level up: a content override that drifted
    /// from the 0.2 threshold would write shadow where the colour pass draws nothing, which reads as dirt on
    /// a wall and points at no file.</para></summary>
    private const string CastMaskVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUV;

        """ + CasterBufDecl + """
        uniform mat4 uVP;
        out vec2 vUV;
        flat out float vCut;           // aux.y: this caster's discard threshold
        void main()
        {
            SowxCaster c = uCaster[gl_BaseInstance];
            gl_Position = uVP * c.model * vec4(aPos, 1.0);
            vUV = aUV;
            vCut = c.aux.y;
        }
        """;
    /// <summary>...and the cutout itself, at the engine's own <c>fAlphaDiscardValue</c>.
    /// <para>KNOWN COLLISION, named because it fails the same way this fix was reported and in the opposite
    /// direction: on env-mapped content the diffuse alpha carries TWO authorings. On foliage it is coverage
    /// (transparent between the leaves) and the discard is exactly right; on a polished floor or chrome trim
    /// it is REFLECTIVITY -- <c>fEnvMapLevel = 1 - alpha</c>, inc_standard.shd:248, the same read #121 folded
    /// into the Alloy lit stage -- so solid geometry authored as very shiny sits at alpha &lt;= 0.2 by design
    /// and stops casting a shadow at all. No material flag separates the two authorings, so nothing is
    /// guessed here. The live tell is shiny content losing its shadow while foliage gains leaves; the dial if
    /// it shows is this threshold (coverage alpha is ~0 in the gaps, reflectivity alpha is graded, so
    /// something like 0.05 separates them) -- picked from a capture, not from here.</para>
    /// <para>The texture rotation
    /// the colour pass applies is deliberately absent: it rotates a TILE's diffuse about its own centre, and
    /// a rotated-vs-unrotated leaf silhouette differs by less than a shadow-map texel at any distance this
    /// pass bakes -- carrying it would mean a per-draw record field for the sake of nothing visible.</summary>
    private const string CastMaskFs = """
        #version 460 core
        in vec2 vUV;
        flat in float vCut;
        uniform sampler2D uTex;
        void main() { if (texture(uTex, vUV).a <= vCut) discard; }
        """;
}
