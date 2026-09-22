using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using static CX.Framework.EngineLayout;

namespace CX.Alloy;

/// <summary>
/// Particles (ADR 0011 M3, #131). The engine keeps spawn, movement and life; the owned pass takes the vertex
/// build, which is the only part of an emitter that was ever a draw. Vanilla builds them in
/// <c>PartEmitter::RenderEmitter</c> @0x4ad4c0: 0x24 dwords a particle into ONE shared
/// <c>VertexStreamDataObject</c>, a full-range <c>glBufferSubData</c> and one <c>glDrawArrays</c> per emitter,
/// so nothing survives the call to bind and this is a REBUILD, not a replay -- every input the build reads is
/// a live <c>Particle</c> struct plus a handful of emitter fields, all state the engine maintains.
/// <para><b>Why it is worth taking.</b> Vanilla depth-sorts emitters individually, so every visible one costs
/// a program bind, texture binds, an upload and a draw. Punch-through (writes depth, discards) and additive
/// commute, so those batch by texture across the whole screen -- twenty torches become one draw. Only the
/// alpha-blended remainder keeps the engine's order, merging adjacent runs.</para>
/// <para><b>Suppression is per emitter, not per pass.</b> The engine's routine IS the cost, so it is not
/// colour-masked the way <see cref="Beams"/> and <see cref="SkyBox"/> mask theirs. An emitter the Alloy pass
/// issued this pass is marked and the hook declines its original; an emitter we cannot build (custom material
/// shader, no texture, table full) is not marked and, under the shim's vertex funnels, is NOT DRAWN --
/// <c>suppressed</c> on the [particles] line counts them. No all-owned-or-none rule (#264, #285).</para>
/// <para><b>Simulation runs before the anchor.</b> <c>Scene::DoEmitterBucket</c> @0x476f00 is the only
/// caller of <c>PartEmitter::AnimateParticles</c> @0x4ac660 and RSP runs it at step 28, after the frame
/// anchor; <see cref="RspShim"/> runs that walk BEFORE the anchor instead, so population, sort and positions
/// are this frame's. Simulation stays engine-side per ADR 0018's boundary (#261).</para>
/// <para><b>The feed</b> is a persistent RECORD buffer updated by delta. One 96-byte record a particle
/// (<see cref="RecFloats"/>) lives in a block owned by its emitter's table slot; the build writes a CPU shadow,
/// hashes the block, and sends it only when the simulation moved it. The six vertices a particle expand from
/// <c>gl_VertexID</c> in the vertex stage. The CPU stages one SPAN a placed emitter (record base, particles,
/// index offset) and one descriptor a draw group, and <see cref="ParticleCs"/> writes the draw-order index
/// list in one dispatch of one workgroup per group, depth-sorting the ALPHA-BLENDED groups back to front
/// (#261, #288).</para>
/// <para><b>Blend classes stay separable structurally:</b> a draw group is single-class by construction
/// (<see cref="Group"/>), one workgroup owns one group, and groups occupy disjoint index ranges issued in
/// group order, so no dispatch can move a particle out of its class. Punch-through and additive are not
/// sorted at all, deliberately. <c>/particles sort 0</c> puts the index build back on the CPU, unsorted.</para>
/// </summary>
internal static unsafe partial class Particles
{
    // ---- PartEmitter, from PartEmitter::RenderEmitter @0x4ad4c0 and PartEmitter::Initialize @0x4aaaa0 ----

    /// <summary>The MdlNodeEmitter this emitter instantiates -- the model-side flags live there.</summary>
    private static readonly int EmNode = EngineLayout.PartNodeLink;
    /// <summary>The owning Gob. Null is legal (a free-standing emitter); when set, its vtable slot +0x2d8
    /// supplies the scale every size term is multiplied by.</summary>
    private static readonly int EmGob = EngineLayout.PartGob;
    /// <summary>Blend mode: 1 = punch-through, 0 = normal alpha, anything else = additive. This is the field
    /// the ticket's "preserve the punch-through mode" is about, and unlike grass its depth write is
    /// deliberate.</summary>
    private static readonly int EmBlend = EngineLayout.EmBlend;
    /// <summary>Alpha at the end and the start of life (in that order in memory).</summary>
    private static readonly int EmAlphaEnd = EngineLayout.EmAlphaEnd, EmAlphaStart = EngineLayout.EmAlphaStart;
    /// <summary>Colour at the end and the start of life, three floats each.</summary>
    private static readonly int EmColorEnd = EngineLayout.EmColorEnd, EmColorStart = EngineLayout.EmColorStart;
    /// <summary>The second fade owner the early-out tests, alongside the Gob's own (see
    /// <see cref="GobFadeOwner"/>).</summary>
    private static readonly int EmTileOwner = EngineLayout.EmTileOwner;
    /// <summary>UV-grid animation: frames per second, then the end and start frame indices. A zero fps or
    /// equal ends means the frame index never advances.</summary>
    private static readonly int EmFps = EngineLayout.EmFps, EmFrameEnd = EngineLayout.EmFrameEnd,
                               EmFrameStart = EngineLayout.EmFrameStart;
    /// <summary>Particle lifetime. Zero draws nothing at all -- the engine's own early-out.</summary>
    private static readonly int EmLife = EngineLayout.EmLife;
    /// <summary>Quad half-extents before the 0.5 and the Gob scale: X at start/end, then Y at start/end.
    /// </summary>
    private static readonly int EmSizeStartX = EngineLayout.EmSizeStartX, EmSizeEndX = EngineLayout.EmSizeEndX,
                               EmSizeStartY = EngineLayout.EmSizeStartY, EmSizeEndY = EngineLayout.EmSizeEndY;
    /// <summary><c>Particle**</c>, and the count. The array is dense: index i is drawn as quad i.</summary>
    private static readonly int EmParts = EngineLayout.EmParts, EmCount = EngineLayout.EmCount;
    /// <summary>Render type -- which billboard basis the quads get. See <see cref="Basis"/>.</summary>
    private static readonly int EmType = EngineLayout.EmType;
    /// <summary>The Material whose slot-0 texture every quad samples.</summary>
    private static readonly int EmMaterial = EngineLayout.EmMaterial;
    /// <summary>Interpolation domain (a SHORT, not an int): 0 interpolates over the particle's INDEX in the
    /// array, non-zero over its age. Reading it as an int folds in the two bytes above and turns every
    /// index-interpolated emitter into an age-interpolated one.</summary>
    private static readonly int EmLifeInterp = EngineLayout.EmLifeInterp;
    /// <summary>The emitter's own orientation quaternion, stored (w, x, y, z) -- the basis render type 1
    /// billboards against.</summary>
    private static readonly int EmOrient = EngineLayout.EmOrient;
    /// <summary>Emitter world position, the point the tint is sampled at.</summary>
    private static readonly int EmPos = EngineLayout.EmPos;

    // ---- Material, for #148's emissive parameter. Same records AlloyRenderer.ReadMaterial walks. ----

    /// <summary>The two <c>List&lt;MaterialParameter&gt;</c> the .mtr's
    /// declared values reach. Shared first, instance second, so a per-part override still wins -- the
    /// instance list is only SIZED to the shared count and carries defaults until something overrides.
    /// </summary>
    private static readonly int MatParams = EngineLayout.MatParams, MatParamCount = EngineLayout.MatParamCount,
                               SharedParams = EngineLayout.SharedParams, SharedParamCount = EngineLayout.SharedParamCount;
    /// <summary>MaterialParameter: name at +0, value at +0x44, type at +0x5c (0 = float), component count at
    /// +0x60 as a ushort, 100 bytes a record.</summary>
    private static readonly int ParamValue = EngineLayout.ParamValue, ParamType = EngineLayout.ParamType,
                               ParamComps = EngineLayout.ParamComps, ParamStride = EngineLayout.ParamStride;
    /// <summary>A list longer than this is a bad pointer, not a .mtr.</summary>
    private const int MaxParams = 64;
    /// <summary>#148: the .mtr parameter an effect declares to say how bright it really is. Named the way
    /// sow-topdata already names its own (<c>noirExempt</c> on <c>fsparticle</c>), and absent means 1.0.
    /// </summary>
    private const string EmissiveParam = "fEmissive";
    /// <summary>Refuse an absurd declaration rather than hand the bloom a value that blows the whole frame
    /// out. Ten is already four stops over white.</summary>
    private const float MaxEmissive = 10f;

    /// <summary>The fade chain <c>RenderEmitter</c> opens with: a Gob's fade owner sits at Gob+0x1a8 and the
    /// factor itself at owner+0xc8. Below 1.0 the engine draws the emitter not at all -- so an emitter in a
    /// fading tile is DECLINED rather than owned, and the engine's own early-out draws the same nothing.
    /// </summary>
    private static readonly int GobFadeOwner = EngineLayout.GobTileData, FadeFactor = EngineLayout.TileGeomFade;
    /// <summary>Gob vtable slot for the scale every size term is multiplied by.</summary>
    private static readonly int GobScaleSlot = EngineLayout.GobAdjustedScaleSlot;

    // ---- MdlNodeEmitter ----

    /// <summary>UV grid: columns then rows. Zero means one, and a grid of 1x1 skips the animation entirely.
    /// </summary>
    private static readonly int NodeGridX = EngineLayout.EmitterGridX, NodeGridY = EngineLayout.EmitterGridY;
    /// <summary>Non-zero DISABLES back-face culling (the engine reads it as
    /// <c>if (node+0x164 == 0) Enable(CULL_FACE) else Disable(CULL_FACE)</c>). Reproduced rather than assumed
    /// off, because the quad winding below is the engine's own.</summary>
    private static readonly int NodeNoCull = EngineLayout.EmitterNoCull;

    // ---- Particle ----

    /// <summary>Position, and the position the previous simulation step left. The previous one is only read
    /// by the motion-blurred type, where it is the far edge of a stretched quad.</summary>
    private static readonly int PPos = EngineLayout.ParticlePos, PPrevPos = EngineLayout.ParticlePrevPos;
    /// <summary>Roll about the billboard normal, in radians (the engine feeds it to <c>sincosf</c> after
    /// halving, i.e. as a quaternion half-angle).</summary>
    private static readonly int PRoll = EngineLayout.ParticleRoll;
    /// <summary>The particle's own orientation quaternion, (w, x, y, z) -- render type 5's whole basis.
    /// </summary>
    private static readonly int PQuat = EngineLayout.ParticleQuat;
    /// <summary>UV-grid frame index, and the age at which it last advanced. Both are WRITTEN by the vertex
    /// build: owning the build means owning that advance, or every animated particle freezes.</summary>
    private static readonly int PFrame = EngineLayout.ParticleFrame, PFrameTime = EngineLayout.ParticleFrameTime;
    /// <summary>The splat normal, used as the billboard's reference axis on a splat emitter.</summary>
    private static readonly int PSplatNormal = EngineLayout.ParticleSplatNormal;
    /// <summary>Splat flag (one byte) and age.</summary>
    private static readonly int PSplat = EngineLayout.ParticleSplat, PAge = EngineLayout.ParticleAge;

    // ---- Camera / Scene ----

    /// <summary>The camera's orientation quaternion at <c>*CurrentCamera + 0xbc</c>, stored (w, x, y, z).
    /// Read at the Alloy slot, which runs inside the world Scene's own render, so it is that Scene's camera
    /// and not the UI frame's (the trap #99 named applies at PRESENT time, not here).</summary>
    private static readonly int CamQuat = EngineLayout.CamRot;
    /// <summary>The Scene's LightManager, whose master list the tint lookup scans -- a
    /// <c>LightManager*</c>, not a light list (#441 corrected the name).</summary>
    private static readonly int SceneLights = EngineLayout.SceneLightManager;

    // ---- the engine's own literals, read out of .rodata rather than guessed ----

    /// <summary>Colour scale: <c>DAT_013f3780</c> = 255.99, not 255. The build multiplies a clamped 0..1
    /// channel by it and truncates, so 255.99 is what makes a 1.0 channel reach 255 instead of stopping one
    /// short of it on every particle in the game.</summary>
    private const float ByteScale = 255.99f;
    /// <summary>Half, in the two places the engine uses it: the quaternion half-angle on the roll, and the
    /// half-extent every size term is scaled by.</summary>
    private const float Half = 0.5f;
    /// <summary>The interpolation rate an age-domain emitter with no lifetime falls back to
    /// (<c>DAT_013eee18</c> = 0.01).</summary>
    private const float NoLifeRate = 0.01f;

    /// <summary>Aurora blend factors: normal alpha is <c>SetBlendFunc(0, 1)</c>, additive
    /// <c>SetBlendFunc(0, 2)</c>. Both int arguments come from the DISASSEMBLY -- the decompiler drops the
    /// second and renders both arms as <c>SetBlendFunc(0)</c>, the int-valued twin of the trap
    /// <see cref="Beams"/>'s <c>projectionWorldZClip</c> fell into.</summary>
    private const uint GlSrcAlpha = 0x0302, GlOneMinusSrcAlpha = 0x0303, GlOne = 1;   // not-an-offset: GL blend-factor enums
    /// <summary>The alpha-discard threshold a punch-through emitter draws under. It is
    /// <c>fAlphaDiscardValue</c> under <c>RenderInterface::Enable(1)</c>, the same 0.2 every other cutout
    /// class in the Alloy pass uses (<see cref="AlloyClasses.Discard"/>); the other two blend classes draw
    /// under <c>Disable(1)</c> and get -1, the engine's own "no discard" sentinel.</summary>
    private const float PunchDiscard = 0.2f, NoDiscard = -1f;

    /// <summary>#148: the engine's own <c>particleSoftness</c>, from the disassembly at 0x4ae641 (the
    /// decompiler renders it as a bare <c>SetShaderParamf(0)</c> -- the float is invisible there, the same
    /// trap as everywhere else in this function). It is <c>5.0</c> when <c>0.2 * S &gt; 0.2</c> and
    /// <c>1 / (0.2 * S)</c> otherwise, over the largest half-extent S the emitter can reach; the two
    /// branches meet at S = 1, so the whole of it is <c>5 / min(S, 1)</c>.
    /// <para>Read it as a RECIPROCAL FADE DISTANCE: the fade spans <c>1 / softness</c> world units, so it is
    /// capped at 0.2u for anything smaller than a unit across and grows with the quad after that. A big
    /// smoke card dissolves over a fifth of its own width; a spark does not soften at all.</para></summary>
    private const float SoftScale = 5f;
    /// <summary>Softness is suppressed outright on splat emitters and on render types 1 and 2 -- the engine
    /// does the same, and the reason is geometric: those quads are pinned to a plane rather than facing the
    /// camera, so the depth gap under them is their own orientation and fading on it would eat the decal.
    /// </summary>
    private static bool SoftSuppressed(bool splat, int type) => splat || type == 1 || type == 2;

    /// <summary>Blend classes, in the order the Alloy pass draws them -- which is NOT the engine's. Vanilla
    /// sorts every emitter into ONE back-to-front order regardless of blend mode; this splits that order in
    /// three. There are two departures in that, and they are not the same size.
    /// <para><b>Punch-through first is free.</b> It writes depth and discards rather than blending, so where
    /// it draws at all it is opaque -- a punch emitter behind a blended one produces the same pixel either
    /// way. Moving it ahead is strictly better, because the blended classes then get to depth-test against
    /// it, which vanilla's single order cannot offer.</para>
    /// <para><b>Additive last is a real change, and it is visible.</b> Addition commutes with ITSELF, which
    /// is what makes the additive class internally re-orderable and therefore batchable across the screen.
    /// It does not commute with alpha-over. Vanilla draws an additive flame standing BEHIND an alpha smoke
    /// plume first, so the smoke then attenuates it; here the smoke composites first and the flame is added
    /// at full strength over the top, so it reads through the smoke. Every additive-behind-blended pair --
    /// a glow behind a dust cloud, a spell effect behind a fog emitter -- brightens by that difference.
    /// #126 sanctions picking an order rather than reproducing one, so this is a decision, not a defect; but
    /// it is the thing to look for in a comparison shot, and <c>particles.batch=0</c> turns it off (see
    /// <see cref="Batch"/>) so the two can be held side by side.</para></summary>
    private const int ClsPunch = 0, ClsBlend = 1, ClsAdd = 2, ClsCount = 3;

    /// <summary>Vertex layout. The first 24 bytes are the engine's own -- colour as 4 normalised bytes, then
    /// position, then UV, matching the <c>0x805</c> attribute token <c>RenderEmitter</c> asks the stream
    /// object for -- so the build stays a port rather than a re-derivation. #148 appends two fields the
    /// engine has no equivalent for.
    /// <para>Both of them are PER EMITTER, and they live in the RECORD rather than in a uniform for one
    /// reason: #131 batches emitters into a single draw, so anything that varies per emitter and is uploaded
    /// as a uniform would split the groups it took the whole ticket to merge.</para></summary>
    /// <summary>#261: one particle RECORD, in floats. Six <c>vec4</c>s, std430:
    /// <c>{pos.xyz, col} {far.xyz, colFar} {right.xyz, soft} {up.xyz, type} {centre.xyz, -} {u0,u1,v0,v1}</c>,
    /// the two colours as <c>uintBitsToFloat</c>. Everything is WORLD space -- the build applies the
    /// emitter's model matrix to the centre and its two axes, which is exact for an affine matrix and four
    /// transforms cheaper per particle than transforming the corners.</summary>
    private const int RecFloats = 24;
    /// <summary>The engine's software matrix stack: model is index 2, stride 0x58, top at +0x18. Named
    /// rather than open-coded because <see cref="Beams"/> and <see cref="SkyBox"/> name the same trio, and a
    /// layout correction that misses one site is the kind that is found by looking at the screen.</summary>
    private const int ModeModel = 2;
    private static readonly int StackStride = EngineLayout.MtxStackStride, StackTop = EngineLayout.MtxStackTop;
    /// <summary>The SSBO binding points this pass owns. 4/9 are the world draw's, 0-3 the light rig's,
    /// 5-7 the caster's, 12-16 <see cref="CullPass"/>'s; 10/11 are the record and index buffers the vertex
    /// stage names, and #288's two descriptor buffers take the next free pair above the cull's.</summary>
    internal const uint BindRecs = 10, BindIdx = 11;
    /// <summary>#288: the compute stage's inputs. <see cref="BindSpans"/> is one entry per emitter the pass
    /// placed, <see cref="BindGroups"/> one per draw group.</summary>
    private const uint BindSpans = 17, BindGroups = 18;

    /// <summary>#288: the index-build workgroup size, and the largest group the compute sort will order.
    /// One workgroup per draw group, one shared-memory bitonic network per blended group.
    /// <para>ponytail: the cap is what the shared arrays hold -- 2048 * (float + uint) is 16KB, half the
    /// 32KB GL guarantees, and a blended group past it keeps fill order exactly as it did before this
    /// ticket. The upgrade path is a global key sort over the whole index list rather than a bigger
    /// workgroup; <c>sort-skipped=</c> on the period line is when that becomes worth writing.</para></summary>
    private const int SortLocal = 256, SortCap = 2048;
    /// <summary>#288: the top bit of a group descriptor's particle count, set by the CPU on exactly the
    /// groups the sort may reorder. The count itself is bounded by <see cref="MaxRecs"/>, so the bit is
    /// free -- and it is the ONLY thing that decides a reorder, which is what keeps the classes separable.
    /// </summary>
    private const uint SortFlag = 0x80000000u;   // not-an-offset: a sort-key bit of ours

    /// <summary>Emitters tracked at once. Past this an emitter is declined and, since #264 deleted the
    /// engine walk, does not draw at all (#285) -- which is the whole cost of the cap.</summary>
    private const int MaxEmitters = 8192;
    /// <summary>Draw groups per pass. One group is one <c>glDrawArrays</c>; overflowing it declines the
    /// remaining emitters rather than merging groups that must not merge.</summary>
    private const int MaxGroups = 4096;
    /// <summary>Record-buffer growth, in records: start here, double until the frame fits, stop at the cap
    /// (96 bytes each, so the cap is 48MB) and stand the pass down rather than truncating.</summary>
    private const int MinRecs = 1024, MaxRecs = 1 << 19;

    /// <summary>One captured emitter. Only the identity and the transform are held; everything the build
    /// reads is re-read off the live emitter at draw time.</summary>
    private struct Emit
    {
        public IntPtr Ptr;
        public Matrix4x4 Model;
        public bool Ident;
        public int CapFrame;
        /// <summary><see cref="AlloyRenderer.PassGen"/> at the pass this emitter was actually ISSUED.
        /// The engine-side hook suppresses on exactly this, so an emitter the Alloy pass declined -- or one
        /// that spawned after the last capture and has therefore never been built -- keeps its vanilla
        /// draw.</summary>
        public int IssuedGen;
        /// <summary>#148: the material's <c>fEmissive</c>, read ONCE when the slot is allocated. The walk is
        /// a string compare over up to 64 records across two lists, which is cold work -- doing it in the
        /// per-frame resolve would put it on the hot path for every emitter on screen, every frame.</summary>
        public float Emissive;
        /// <summary>#261: this emitter's slice of the persistent record buffer, in RECORDS. Stable across
        /// frames -- that is what makes the upload a delta rather than a rebuild -- and reassigned only when
        /// the emitter needs more room than it holds.</summary>
        public int RecBase, RecCap;
        /// <summary>FNV-1a over the block as it was last UPLOADED. Equal means the engine's simulation
        /// produced the same records this frame, so the block is not re-sent. Zero means "never uploaded",
        /// which is why the allocator sets it to zero on every reassignment.</summary>
        public ulong Hash;
    }

    private static readonly Emit[] _tab = new Emit[MaxEmitters];
    private static int _tabN;
    /// <summary>Capture order = the engine's own back-to-front sort, which is the order the blended class
    /// has to keep. Rebuilt each capture window.</summary>
    private static readonly int[] _order = new int[MaxEmitters];
    private static int _orderN, _orderGen = -1;

    // per-pass staging: resolved once in Classify, consumed by the group builder and the writer.
    private static readonly int[] _sSlot = new int[MaxEmitters], _sTex = new int[MaxEmitters],
                                  _sCls = new int[MaxEmitters], _sCull = new int[MaxEmitters],
                                  _sCount = new int[MaxEmitters];
    private static readonly float[] _sEmis = new float[MaxEmitters];
    /// <summary>The <c>CAurTexture*</c> itself, not its GL id. It is the batch key AND what gets bound --
    /// see <see cref="SymbolMap.AurTextureBindInUnit"/> for why the id the Material hands out is not the id
    /// the engine draws with.</summary>
    private static readonly IntPtr[] _sTexObj = new IntPtr[MaxEmitters];
    private static readonly bool[] _sTaken = new bool[MaxEmitters];
    // _gFirst/_gCount index into _draw (emitters); _gStart/_gParts are the range of the draw-order INDEX
    // list the writer actually produced, in particles. Two pairs on purpose: a particle array can hand back
    // a null slot, so what is written is a result, not the product of the counts read at classify time.
    private static readonly int[] _gFirst = new int[MaxGroups], _gCount = new int[MaxGroups],
                                  _gStart = new int[MaxGroups], _gParts = new int[MaxGroups],
                                  _gTex = new int[MaxGroups], _gCls = new int[MaxGroups],
                                  _gCull = new int[MaxGroups];
    private static readonly float[] _gEmis = new float[MaxGroups];
    private static readonly IntPtr[] _gTexObj = new IntPtr[MaxGroups];
    /// <summary>Vertices each entry of <see cref="_draw"/> actually wrote.</summary>
    private static readonly int[] _wrote = new int[MaxEmitters];
    /// <summary>#288: one span descriptor per entry of <see cref="_draw"/> -- (record base, particles, index
    /// offset, unused), std430 <c>uvec4</c>. This is the whole of what replaced the per-particle CPU index
    /// loop: three numbers an emitter instead of four bytes a particle.</summary>
    private static readonly uint[] _span = new uint[MaxEmitters * 4];
    /// <summary>#288: one descriptor per draw group -- (first span, spans, index offset, particles with
    /// <see cref="SortFlag"/>). One workgroup consumes one of these, and a group is single-class by
    /// construction (see <see cref="Group"/>), so no workgroup can ever move a particle across a class.
    /// </summary>
    private static readonly uint[] _grp = new uint[MaxGroups * 4];
    /// <summary>Emitters in draw order, grouped -- the permutation of <see cref="_order"/> the writer walks.
    /// </summary>
    private static readonly int[] _draw = new int[MaxEmitters];
    /// <summary>How many of the classified emitters <see cref="Group"/> actually placed. It can be short of
    /// the classified count when the group cap bites, and the difference matters: an emitter that never got
    /// into a group is one the engine still has to draw, so it must not be stamped as issued.</summary>
    private static int _drawN;

    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, void> _origRender, _origDtor;
    private static delegate* unmanaged<IntPtr, int, long> _getTexture;
    private static delegate* unmanaged<IntPtr, uint, int, void> _bindInUnit;
    /// <summary>#237: the engine's bound-texture cache, snapshot-restored across the draw loop. See
    /// <see cref="SymbolMap.GlBoundTextures"/> for why a pass that draws in its own context has to.</summary>
    private static ulong* _engBoundTex;
    private static uint* _engActiveStage, _engStageEnabled;
    /// <summary>Entries in <c>GLRender::m_boundTextures</c>, from the 0x10-iteration clear loop in
    /// <c>GLRender::ResetRenderStates</c> @0x4d1810.</summary>
    private const int EngTexStages = 16;
    private static delegate* unmanaged<IntPtr, float> _tileAlpha, _gobAlpha;
    private static delegate* unmanaged<IntPtr, byte> _isSplat, _isTinted;
    private static IntPtr* _currentScene, _currentCamera;
    private static byte* _stacks;

    /// <summary><see cref="_off"/> is the RESOLUTION latch -- a helper, a global or a hook that did not
    /// come up, so this client cannot run the Alloy pass at all. It is not the user toggle
    /// (<see cref="Enabled"/>) and <c>particles.enabled</c> must never clear it.</summary>
    private static bool _off, _failed;
    private static uint _prog, _vao, _recBuf, _idxBuf;
    /// <summary>#288: the index-build/sort compute program and its two descriptor buffers.</summary>
    private static uint _csProg, _spanBuf, _grpBuf;
    private static int _csEye = -1;
    private static bool _csFailed;
    private static int _spanCap, _grpCap;
    private static int _locEye = -1;
    /// <summary>#341: the fog block's locations, as one <see cref="AlloyRenderer.FeatLoc"/> -- the
    /// stage fogs through the world pass's own uploader, so it takes the world pass's own location set.</summary>
    private static AlloyRenderer.FeatLoc _fogLoc;

    private static int _locView = -1, _locProj = -1, _locTex = -1, _locCut = -1,
                       _locAdditive = -1,
                       _locSoftOn = -1, _locDepthTex = -1, _locEmissive = -1, _locLit = -1, _locViz = -1;
    /// <summary>The light rig's own uniform locations, in the order <see cref="AlloyLights"/> and
    /// <see cref="AlloyRenderer"/> want them handed back.</summary>
    private static int _lAmbient = -1, _lDiffuse = -1, _lDir = -1, _lGrid = -1, _lTile = -1, _lZ = -1,
                       _lFalloff = -1, _lWrap = -1, _lAtten = -1, _lHot = -1, _lWrapN = -1, _lSss = -1, _lReach = -1,
                       _lSunTex = -1, _lSunVp = -1, _lSunTexel = -1, _lSunBias = -1, _lSunCount = -1,
                       _lSunDynTex = -1, _lSunDyn = -1, _lSunFocus = -1, _lSunRadius = -1,
                       _lCubeTex = -1, _lCubeLight = -1, _lCubePlane = -1, _lCubeCount = -1, _lCubeSoft = -1, _lCubePcss = -1, _lCubeFloor = -1,
                       _lCubeDynTex = -1, _lCubeDynPage = -1, _lCubeFade = -1,
                       _lSunMedia = -1, _lLocalMedia = -1;
    /// <summary>The CPU shadow of the record buffer, addressed by <see cref="Emit.RecBase"/>. The build
    /// writes here; only the blocks whose hash moved are sent to GL.</summary>
    private static float* _recs;
    private static int _recsCap, _recTop;
    /// <summary>Record indices in DRAW order, one per particle. #288 moved the WRITE of this list onto the
    /// GPU, so the CPU shadow is now only the <c>/particles sort 0</c> path's staging area -- kept because
    /// that dial is the escape hatch for a driver where the compute program does not build.</summary>
    private static uint* _idx;
    private static int _idxCap;

    private static int _frame;
    private static int _captured, _declined, _emitters, _groups, _foreign, _suppressed, _overflow;
    /// <summary>#261's own numbers: record blocks re-sent this period, blocks the hash said were unchanged,
    /// and times the block allocator ran out and started over (which costs one frame of vanilla emitters).</summary>
    private static int _blockUploads, _blockSkips, _recResets;
    /// <summary>#288's numbers: groups the compute pass ordered, and blended groups it left in fill order
    /// because they were larger than <see cref="SortCap"/>.</summary>
    private static int _sorted, _sortSkipped;
    // Why an emitter was declined, split -- "particles are still vanilla" has five different fixes behind
    // it and only the split says which.
    private static int _decFade, _decEmpty, _decShader, _decTex, _decFull, _reclaimed, _softPasses,
                       _emissive, _litGroups;
    /// <summary>How far inside its own cell a quad's UV range is pulled, as a fraction of the cell.
    /// <para>An atlas cell has no border. Bilinear filtering at its edge reaches into the NEIGHBOURING cell,
    /// and a mip level of an atlas averages cells together outright -- so a frame whose neighbour holds a
    /// star or a spark shows that neighbour's rays along its own boundary, at whatever LOD the derivatives
    /// ask for. The engine gets away with sampling the exact cell because it binds through
    /// <c>CAurTexture::BindInUnit</c>, which applies the texture's own filtering; this pass cannot rely on
    /// inheriting that, so it contains the read instead.</para>
    /// <para>ponytail: a fraction of the cell rather than a true half-texel, which would mean querying every
    /// texture's dimensions and caching them per <c>CAurTexture*</c>. 1/128 of a cell is half a pixel at the
    /// 64px cells NWN particle atlases actually use, and erring large costs a sliver of the frame's own
    /// artwork rather than a ray from the frame beside it. Query the real size if an atlas ever ships cells
    /// small enough for this to visibly crop.</para></summary>
    private const float CellInset = 1f / 128f;
    private static uint _samp;
    private static readonly int[] _cType = new int[7], _cCls = new int[ClsCount];
    private static int _cSplat, _cTinted, _cGrid;
    /// <summary>The engine's own names for the render types, from <c>PartEmitter::Initialize</c> @0x4aaaa0 --
    /// the same strings the model's emitter node carries, so a census line names something greppable in the
    /// model data rather than an index.</summary>
    private static readonly string[] TypeNames =
    {
        "Normal", "Billboard_to_Local_Z", "Billboard_to_World_Z", "Linked",
        "Aligned_to_World_Z", "Aligned_to_Particle_Dir", "Motion_Blur",
    };

    /// <summary>The Scene the capture belongs to, so an area transition drops the table rather than reading
    /// particle arrays whose emitters the unload freed.</summary>
    private static IntPtr _scene;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        SceneDepth.Attach(log);

        _getTexture = (delegate* unmanaged<IntPtr, int, long>)Native.Sym(SymbolMap.MatGetTexture);
        _bindInUnit = (delegate* unmanaged<IntPtr, uint, int, void>)Native.Sym(SymbolMap.AurTextureBindInUnit);
        _engBoundTex = (ulong*)Native.Sym(SymbolMap.GlBoundTextures);
        _engActiveStage = (uint*)Native.Sym(SymbolMap.GlActiveTextureStage);
        _engStageEnabled = (uint*)Native.Sym(SymbolMap.GlEnabledTextureStages);
        _tileAlpha = (delegate* unmanaged<IntPtr, float>)Native.Sym(SymbolMap.EmitterGetTileAlpha);
        _gobAlpha = (delegate* unmanaged<IntPtr, float>)Native.Sym(SymbolMap.EmitterGetGobAlpha);
        _isSplat = (delegate* unmanaged<IntPtr, byte>)Native.Sym(SymbolMap.EmitterIsSplat);
        _isTinted = (delegate* unmanaged<IntPtr, byte>)Native.Sym(SymbolMap.EmitterIsTinted);
        EmitterLighting.Resolve();
        _currentScene = (IntPtr*)Native.Sym(SymbolMap.CurrentScene);
        _currentCamera = (IntPtr*)Native.Sym(SymbolMap.CurrentCamera);
        _stacks = (byte*)Native.Sym(SymbolMap.MAurMtxStack);

        if (_getTexture == null || _bindInUnit == null || _tileAlpha == null || _gobAlpha == null || _isSplat == null
            || _isTinted == null || !EmitterLighting.Ready || _currentScene == null
            || _currentCamera == null || _stacks == null || !AurTex.Ready)
        {
            _off = true;
            log.Info("Particles: an emitter helper, a global or AurTex did not resolve -- emitters stay vanilla");
            return;
        }

        _origRender = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.RenderEmitter, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnRenderEmitter);
        _origDtor = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.PartEmitterDtor, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnEmitterDtor);
        if (_origRender == null || _origDtor == null)
        {
            _off = true;
            log.Info("Particles: PartEmitter::RenderEmitter / ~PartEmitter did not hook -- emitters stay vanilla");
            return;
        }
        log.Info($"Particles: armed and {(Enabled.V ? "ON" : "IDLE")} -- {Describe()} (/particles enabled 1 arms it)");
    }

    /// <summary>The engine's own walk, once per emitter per frame, in its own sorted order. Two jobs: learn
    /// the population (and its transform), and decline to run the original for an emitter the owned pass
    /// already drew this pass.
    /// <para>#261 moved that walk ahead of the frame anchor, so the stamp it tests is one pass old.
    /// Getting that wrong is not cosmetic: the funnels swallow
    /// the engine's DRAW, not its build, and the build is what this class costs. Skipping the original is
    /// safe by precedent, since the pre-#261 order skipped it on every steady-state frame.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnRenderEmitter(IntPtr em)
    {
        if (_origRender == null) return;
        if (_off || !Enabled.V || em == IntPtr.Zero) { _origRender(em); return; }

        // A GUI 3D panel drives the same Scene virtuals as the world (#106/#125). Emitters reached from one
        // belong to that panel's Scene, and replaying them into the world frame would draw an inventory
        // model's sparks over the map.
        IntPtr scene = *_currentScene;
        if (!AlloyRenderer.IsWorldScene(scene)) { _origRender(em); _foreign++; return; }
        if (scene != _scene) Forget();
        _scene = scene;

        int slot = Capture(em);
        // #261: the decline is what skips the ENGINE's own vertex build, which is this class's whole cost --
        // the funnels only swallow the draw. The shim runs this walk ahead of the anchor, so the stamp it
        // tests is one pass old: "drawn in the pass just gone".
        if (slot >= 0 && _tab[slot].IssuedGen == AlloyRenderer.PassGen - 1) { _suppressed++; return; }
        _origRender(em);
    }

    /// <summary>An emitter is being destroyed. #145's rule, applied here: the Alloy draw reads this
    /// emitter's live particle array one frame after the capture, so the table cannot outlive it.</summary>
    [UnmanagedCallersOnly]
    private static void OnEmitterDtor(IntPtr em)
    {
        // Tombstone rather than swap-remove: the sort order holds table INDICES, so compacting would leave
        // every entry past the hole naming a different emitter -- which draws a live one at a dead one's
        // place in the depth order for a frame.
        for (int i = 0; i < _tabN; i++)
            if (_tab[i].Ptr == em) { _tab[i].Ptr = IntPtr.Zero; break; }
        if (_origDtor != null) _origDtor(em);
    }

    /// <summary>Record this emitter and its place in the sort. Returns its table slot, or -1 when the table
    /// is full -- which is not a failure, it just leaves the emitter vanilla.</summary>
    private static int Capture(IntPtr em)
    {
        // The capture window opens on the first emitter of a new pass: DoEmitterBucket walks the sorted list
        // in one go, so "the pass changed" is the same event as "a new walk started" without a second hook.
        if (_orderGen != AlloyRenderer.PassGen) { _orderGen = AlloyRenderer.PassGen; _orderN = 0; }

        // One scan finds the emitter, the first tombstone, and the first slot whose emitter is alive but has
        // not been walked for two windows. That last one is what keeps the cap from being permanent: an
        // emitter left behind (walked away from, culled) never destructs, so without reclaiming, one busy
        // area would fill the table and every emitter after it would stay vanilla until a transition -- the
        // feature silently degrading to nothing with only the decline counter to say so.
        int s = -1, free = -1, stale = -1;
        for (int i = 0; i < _tabN; i++)
        {
            if (_tab[i].Ptr == em) { s = i; break; }
            if (_tab[i].Ptr == IntPtr.Zero) { if (free < 0) free = i; }
            else if (stale < 0 && _tab[i].CapFrame < _frame - 1) stale = i;
        }
        if (s < 0)
        {
            if (free >= 0) s = free;
            else if (_tabN < MaxEmitters) s = _tabN++;
            else if (stale >= 0) { s = stale; _reclaimed++; }
            else { _declined++; _decFull++; return -1; }
            _tab[s].IssuedGen = -1;
            // #261: a slot is reused after a tombstone, a stale reclaim or a Forget(), and the block
            // allocator's bump counter is reset by the last of those. Carrying the old RecBase/RecCap into
            // a new emitter would hand it a slice the allocator has already handed to someone else -- two
            // emitters writing one block, with the hash layer making the wrong one sticky.
            _tab[s].RecBase = 0; _tab[s].RecCap = 0; _tab[s].Hash = 0;
            _tab[s].Emissive = ReadEmissive(*(IntPtr*)(em + EmMaterial));
            _captured++;
        }
        ref Emit e = ref _tab[s];
        e.Ptr = em;
        e.CapFrame = _frame;
        // RenderEmitter pushes nothing, so the top of the model stack IS the transform the engine's own
        // draw ran under. Almost always identity -- particles simulate in world space -- but reading it
        // costs nothing and an emitter under a transform would otherwise land at the origin.
        e.Model = *(Matrix4x4*)(_stacks + ModeModel * StackStride + StackTop);
        e.Ident = e.Model.IsIdentity;
        if (_orderN < MaxEmitters) _order[_orderN++] = s;
        return s;
    }

    // ---- the Alloy pass -------------------------------------------------------------------------------

    /// <summary>ADR 0011 (#131): the Alloy emitter pass, after the beams -- vanilla's own placement (step 28,
    /// straight after <c>Scene::RenderBeams</c>). Leaves GL_BLEND off, the depth mask on and culling off,
    /// which is the state the beams handed over and the probe capture after this needs.</summary>
    internal static void Draw(Matrix4x4* view, Matrix4x4* proj, int* viewport, float near, float far,
                              bool lightingOn)
    {
        _frame++;
        if (_off || !Enabled.V || _failed || _orderN == 0 || Gl.DrawArrays == null) return;
        if (!EnsureProgram()) return;

        IntPtr cam = *_currentCamera;
        if (cam == IntPtr.Zero) return;
        Basis(cam, out Vector3 camFwd, out Vector4 camQ, out Vector3 axisY, out Vector3 axisX);
        // The eye, from the view matrix rather than from the camera object: the sphere normal has to bulge
        // TOWARDS the viewer and the engine's quaternion does not say by itself which way its own Z points.
        // Inverting the matrix we are about to upload cannot disagree with the matrix we are about to upload.
        Vector3 eye = default;
        if (Matrix4x4.Invert(*view, out Matrix4x4 vi)) eye = new Vector3(vi.M41, vi.M42, vi.M43);

        int n = Classify();
        if (n == 0) return;
        int groups = Group(n);
        if (groups == 0 || _drawN == 0) return;

        int parts = 0;
        for (int i = 0; i < _drawN; i++) parts += _sCount[_draw[i]];
        if (!EnsureBuffers(parts)) return;

        // #148: only the alpha-blended class is lit. Additive is self-illuminated by definition, and
        // punch-through in this game's content is Turn Undead auras and a holy strike -- lighting those
        // would dim the effect in exactly the dark crypt it is cast in. Decided BEFORE the build, because
        // the build reads it: an emitter drawn lit must not also get the CPU tint.
        bool lit = Lit.V && lightingOn && AlloyLights.Ready;

        // #261: build each emitter's records into its OWN persistent block. #288: what this loop stages for
        // the index list is one SPAN an emitter -- the list itself is written by the compute dispatch below.
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _recBuf);
        int at = 0;
        for (int i = 0; i < _drawN; i++)
        {
            int k = _draw[i];
            ref Emit e = ref _tab[_sSlot[k]];
            // Every entry gets a span, so an emitter that produced nothing is an EMPTY span rather than a
            // stale one -- the compute pass walks spans by group membership, not by what was written.
            _span[i * 4] = 0; _span[i * 4 + 1] = 0; _span[i * 4 + 2] = (uint)at; _span[i * 4 + 3] = 0;
            if (!EnsureBlock(ref e, _sCount[k])) { _wrote[i] = 0; continue; }
            int wrote = BuildEmitter(e.Ptr, ref e, _recs + (long)e.RecBase * RecFloats, _sCount[k],
                                     camFwd, camQ, axisY, axisX, eye,
                                     lit && _sCls[k] == ClsBlend);
            _wrote[i] = wrote;
            if (wrote == 0) continue;
            // The delta. An emitter whose particles did not move -- a frozen splat field, an emitter the
            // simulation skipped -- costs a hash of its own memory and no bus traffic at all.
            ulong h = Fnv(_recs + (long)e.RecBase * RecFloats, (long)wrote * RecFloats);
            if (h != e.Hash)
            {
                e.Hash = h;
                Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER,
                                 (IntPtr)((long)e.RecBase * RecFloats * sizeof(float)),
                                 (IntPtr)((long)wrote * RecFloats * sizeof(float)),
                                 _recs + (long)e.RecBase * RecFloats);
                _blockUploads++;
            }
            else _blockSkips++;
            // #288: the span, not the particles. The index list itself is filled by the compute pass below
            // -- or, under /particles sort 0, from this same descriptor on the CPU.
            _span[i * 4] = (uint)e.RecBase; _span[i * 4 + 1] = (uint)wrote; _span[i * 4 + 2] = (uint)at;
            at += wrote;
        }
        if (at == 0) return;

        // groups are contiguous in _draw and written in that order, so their PARTICLE ranges follow directly
        int cursor = 0;
        for (int gi = 0; gi < groups; gi++)
        {
            int gn = 0;
            for (int j = 0; j < _gCount[gi]; j++) gn += _wrote[_gFirst[gi] + j];
            _gStart[gi] = cursor; _gParts[gi] = gn;
            cursor += gn;
        }

        // #148: the depth snapshot, here rather than at a pass slot of its own. It is not an ordering
        // decision -- nothing else in the frame reads it and nothing draws between it and the particles --
        // so it does not belong in the graph, where it would only make FrameGraph.SelfCheck churn.
        // Taken after the build so the frame's cost is paid only when there is something to soften.
        bool soft = Soft.V && SceneDepth.Take(viewport);

        if (!Order(groups, eye))
        {
            for (int i = 0; i < _drawN; i++)
                for (int j = 0; j < _wrote[i]; j++)
                    _idx[_span[i * 4 + 2] + j] = _span[i * 4] + (uint)j;
            Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _idxBuf);
            Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, 0, (IntPtr)((long)at * sizeof(uint)), _idx);
        }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindRecs, _recBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindIdx, _idxBuf);
        Gl.UseProgram(_prog);
        Gl.UniformMatrix4fv(_locView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_locProj, 1, 0, (float*)proj);
        Gl.Uniform3fv(_locEye, 1, (float*)&eye);
        Gl.Uniform1i(_locTex, 0);
        AlloyRenderer.UploadFog(_fogLoc);
        if (lit)
        {
            AlloyLights.BindForDraw();
            AlloyLights.SetDrawUniforms(_lGrid, _lTile, _lZ, _lFalloff, _lWrap, _lAtten, _lHot, _lWrapN, _lSss,
                                        viewport, near, far, _lReach);
            AlloyRenderer.UploadAreaTerms(_lAmbient, _lDiffuse, _lDir);
            AlloyRenderer.UploadSunCascades(_lSunTex, _lSunVp, _lSunTexel, _lSunBias, _lSunCount, _lSunDyn,
                                            _lSunFocus, _lSunRadius);
            AlloyRenderer.UploadLocalCubes(_lCubeTex, _lCubeLight, _lCubePlane, _lCubeCount, _lCubeSoft, _lCubePcss,
                                           _lCubeDynPage, _lCubeFade, _lCubeFloor);
            // #336: /particles shadows 0 -- no cube taps on particle fragments (uLocalCubes 0 makes the
            // receiver answer lit at once). The pass read 1,2-1,8ms GPU at overdraw x0,6-0,9, so the term
            // is per-fragment cost, and the lit path's cube taps are the one heavy thing in it.
            if (!Shadows.V && _lCubeCount >= 0) Gl.Uniform1i(_lCubeCount, 0);
            // A particle is a point in a volume, not a point on a surface: the shadow lookups' normal
            // offset is a surface remedy and applying it to a camera-facing billboard normal is what put
            // bands across the smoke. Both lookups keep their constant biases; only the offset goes.
            Gl.Uniform1f(_lSunMedia, 1f);
            Gl.Uniform1f(_lLocalMedia, 1f);
        }
        Gl.Uniform1i(_locViz, Viz.V);
        Gl.Uniform1i(_locSoftOn, soft ? 1 : 0);
        Gl.Uniform1i(_locDepthTex, (int)SceneDepth.Unit);
        if (soft) { SceneDepth.Bind(); _softPasses++; }
        Gl.ActiveTexture(Gl.TEXTURE0);
        if (Sampler.Boot && EnsureSampler()) Gl.BindSampler(0, _samp);
        Gl.BindVertexArray(_vao);

        // #237: _bindInUnit below is the ENGINE's bind wrapper, and it writes the engine's per-stage cache
        // while the glBindTexture it performs lands in ALLOY's context. Bindings are per-context, so every
        // entry it leaves is a lie about the engine's own context -- and the engine's next matching bind
        // early-outs on it (SetActiveTextureAndBind @0x45ce10 returns before glActiveTexture/glBindTexture on
        // a hit), drawing the 2D UI with whatever its context still held. Snapshot the three globals and put
        // them back: Alloy never touched the engine's context, so the values found here stay true across the
        // pass. The alternative -- binding raw, as OwnedTextures does -- would skip the resolve and the
        // animation-controller tick this pass needs.
        bool engCache = _engBoundTex != null && _engActiveStage != null && _engStageEnabled != null;
        ulong* engSaved = stackalloc ulong[EngTexStages];
        uint engStage = 0, engEnabled = 0;
        if (engCache)
        {
            new ReadOnlySpan<ulong>(_engBoundTex, EngTexStages).CopyTo(new Span<ulong>(engSaved, EngTexStages));
            engStage = *_engActiveStage; engEnabled = *_engStageEnabled;
        }

        int cls = -1, cull = -1;
        IntPtr texObj = IntPtr.Zero;
        float emis = float.NaN;
        for (int gi = 0; gi < groups; gi++)
        {
            if (_gParts[gi] == 0) continue;
            if (_gCls[gi] != cls)
            {
                cls = _gCls[gi];
                if (cls == ClsPunch)
                {
                    Gl.Disable(Gl.BLEND); Gl.DepthMask(1);
                    Gl.Uniform1f(_locCut, PunchDiscard); Gl.Uniform1i(_locAdditive, 0);
                }
                else
                {
                    Gl.Enable(Gl.BLEND); Gl.DepthMask(0);
                    Gl.BlendFunc(GlSrcAlpha, cls == ClsAdd ? GlOne : GlOneMinusSrcAlpha);
                    Gl.Uniform1f(_locCut, NoDiscard);
                    Gl.Uniform1i(_locAdditive, cls == ClsAdd ? 1 : 0);
                }
                Gl.Uniform1i(_locLit, lit && cls == ClsBlend ? 1 : 0);
                if (lit && cls == ClsBlend) _litGroups++;
            }
            if (_gCull[gi] != cull)
            { cull = _gCull[gi]; if (cull != 0) Gl.Enable(Gl.CULL_FACE); else Gl.Disable(Gl.CULL_FACE); }
            if (_gTexObj[gi] != texObj)
            {
                texObj = _gTexObj[gi];
                // The engine's own bind, on unit 0, frame -1 ("whatever the controller says") -- exactly the
                // call Material::BindAllStandardTextures makes. It resolves the referenced texture, takes the
                // id off the RESOLVED object, and ticks the animation controller. A raw glBindTexture of the
                // Material's own id skips all three, which is how a smoke emitter ended up drawing another
                // effect's artwork.
                _bindInUnit(texObj, 0, -1);
            }
            if (_gEmis[gi] != emis) { emis = _gEmis[gi]; Gl.Uniform1f(_locEmissive, emis); }
            // Six vertices a particle, expanded from gl_VertexID -- there is no vertex buffer any more.
            Gl.DrawArrays(Gl.TRIANGLES, _gStart[gi] * 6, _gParts[gi] * 6);
        }

        if (engCache)
        {
            new ReadOnlySpan<ulong>(engSaved, EngTexStages).CopyTo(new Span<ulong>(_engBoundTex, EngTexStages));
            *_engActiveStage = engStage; *_engStageEnabled = engEnabled;
        }

        if (_samp != 0) Gl.BindSampler(0, 0);
        Gl.BindVertexArray(0);
        Gl.Disable(Gl.BLEND);
        Gl.Disable(Gl.CULL_FACE);
        Gl.DepthMask(1);

        // Stamped only on emitters whose records actually reached the GPU -- the engine-side hook reads
        // this to decide whether to skip its own draw, so an emitter that produced nothing (every particle
        // slot null, or a linked pair with no partner) has to keep its vanilla one rather than vanish.
        for (int i = 0; i < _drawN; i++)
            if (_wrote[i] > 0) { _tab[_sSlot[_draw[i]]].IssuedGen = AlloyRenderer.PassGen; _emitters++; }
        _groups += groups;
    }

    /// <summary>Resolve every captured emitter into (texture, class, cull, particle count), dropping the
    /// ones this pass cannot build. Returns how many made it into the staging arrays.</summary>
    private static int Classify()
    {
        bool hdr = PostChain.Armed;
        int n = 0;
        for (int i = 0; i < _orderN; i++)
        {
            int s = _order[i];
            if (s >= _tabN) continue;
            ref Emit e = ref _tab[s];
            // A dead capture: the emitter stopped being walked (culled, finished, or its Gob went away
            // between the walk and here). Replaying it would read a particle array nobody maintains.
            if (e.Ptr == IntPtr.Zero || e.CapFrame < _frame - 1) continue;
            if (!Resolve(e.Ptr, out int tex, out IntPtr texObj, out int cls, out int cull, out int count))
            { _declined++; continue; }
            _sSlot[n] = s; _sTex[n] = tex; _sCls[n] = cls; _sCull[n] = cull; _sCount[n] = count;
            // Emissive means nothing without headroom to put it in: on an 8-bit target everything above 1.0
            // clips to white, so a declared 4.0 would turn a warm flame into a flat disc. #139 off, or
            // refusing to arm, forces the neutral value.
            _sTexObj[n] = texObj;
            _sEmis[n] = hdr ? _tab[s].Emissive : 1f;
            if (_sEmis[n] != 1f) _emissive++;
            _sTaken[n] = false;
            n++;
        }
        return n;
    }

    /// <summary>Everything about an emitter that decides whether -- and how -- the Alloy pass draws it.
    /// False means DECLINE, which costs one vanilla emitter draw and nothing else.</summary>
    private static bool Resolve(IntPtr em, out int tex, out IntPtr texObj, out int cls, out int cull,
                                out int count)
    {
        tex = 0; texObj = IntPtr.Zero; cls = 0; cull = 0; count = 0;

        // RenderEmitter's own opening early-out: a fading Gob or tile draws NO particles at all. Declining
        // is the cheapest reproduction -- the engine takes the same exit.
        IntPtr gob = *(IntPtr*)(em + EmGob);
        if (gob != IntPtr.Zero)
        {
            IntPtr owner = *(IntPtr*)(gob + GobFadeOwner);
            if (owner != IntPtr.Zero && *(float*)(owner + FadeFactor) < 1f) { _decFade++; return false; }
        }
        IntPtr tile = *(IntPtr*)(em + EmTileOwner);
        if (tile != IntPtr.Zero && *(float*)(tile + FadeFactor) < 1f) { _decFade++; return false; }

        int type = *(int*)(em + EmType);
        // Type 3 pairs each particle with the NEXT one, so the last has no partner and the loop runs one
        // short. The engine does the same subtraction.
        count = *(int*)(em + EmCount) - (type == 3 ? 1 : 0);
        if (count < 1 || *(float*)(em + EmLife) == 0f) { _decEmpty++; return false; }
        if (*(IntPtr*)(em + EmParts) == IntPtr.Zero) { _decEmpty++; return false; }
        if (*(IntPtr*)(em + EmNode) == IntPtr.Zero) { _decEmpty++; return false; }

        IntPtr mat = *(IntPtr*)(em + EmMaterial);
        if (mat == IntPtr.Zero) { _decEmpty++; return false; }
        // A material naming its own program is not this shader's to draw -- and unlike world geometry, where
        // an unported shader costs a small shading difference on one part, a particle effect authored around
        // a custom fragment stage IS that stage. Per-emitter fallback makes refusing free.
        if (AlloyCensus.HasUnportedShader(mat)) { _decShader++; return false; }
        texObj = (IntPtr)_getTexture(mat, 0);
        // AurTex's vtable check is the LIVENESS gate and nothing more here: the id it hands back belongs to
        // the object the Material is holding, which is not necessarily the object the engine draws with. It
        // still answers "is this pointer safe to call into", which is the question that matters before
        // handing it to BindInUnit.
        // #211 survivor: the ENGINE id, on purpose. The particle draw binds through the engine's
        // CAurTexture::BindInUnit with the texture OBJECT (see the bind in Draw), so an owned copy is not
        // reachable from there -- copying one would be VRAM nothing samples. Particles keep the borrowed
        // pointer behind AurTex's vtable guard, which is what they had before #211.
        tex = AurTex.Tid(texObj);
        if (tex == 0) { _decTex++; return false; }

        int blend = *(int*)(em + EmBlend);
        cls = blend == 1 ? ClsPunch : blend == 0 ? ClsBlend : ClsAdd;
        cull = *(int*)(*(IntPtr*)(em + EmNode) + NodeNoCull) == 0 ? 1 : 0;
        return true;
    }

    /// <summary>Build the draw order and its groups. Punch-through and additive gather by (texture, cull)
    /// across the whole screen -- that is the batching AC 4 asks for, and it is legal because neither class
    /// depends on order. The blended remainder keeps the engine's back-to-front sequence and merges only
    /// ADJACENT runs, which is the most that can be done without changing what the frame looks like.</summary>
    /// <summary>Can these two staged emitters share a draw? Texture and cull are GL state; emissive is a
    /// uniform, and a uniform that varies inside a draw is one that has to cut it. Exact float compare is
    /// right here -- both values came out of the same parsed record or are both the literal 1.0.</summary>
    private static bool SameGroup(int a, int b)
        => _sTexObj[a] == _sTexObj[b] && _sCull[a] == _sCull[b] && _sEmis[a] == _sEmis[b];

    private static int Group(int n)
    {
        int g = 0, at = 0;
        _drawN = 0;
        // particles.batch=0 walks the capture order ONCE, merging only adjacent runs -- so the three classes
        // interleave exactly as the engine sorted them and the additive-over-blended reorder is genuinely
        // gone. Splitting by class here regardless of the dial would have left that reorder in place and
        // made the A/B answer "no change" to a question it never asked.
        if (!Batch.Boot)
        {
            for (int i = 0; i < n && g < MaxGroups; i++)
            {
                if (_sTaken[i]) continue;
                int first0 = at;
                _draw[at++] = i; _sTaken[i] = true;
                for (int j = i + 1; j < n && !_sTaken[j] && _sCls[j] == _sCls[i] && SameGroup(j, i); j++)
                { _draw[at++] = j; _sTaken[j] = true; }
                _gFirst[g] = first0; _gCount[g] = at - first0;
                _gTex[g] = _sTex[i]; _gTexObj[g] = _sTexObj[i];
                _gCls[g] = _sCls[i]; _gCull[g] = _sCull[i]; _gEmis[g] = _sEmis[i];
                g++;
            }
            if (at < n) _overflow++;
            _drawN = at;
            return g;
        }
        for (int cls = 0; cls < ClsCount && g < MaxGroups; cls++)
        {
            for (int i = 0; i < n && g < MaxGroups; i++)
            {
                if (_sTaken[i] || _sCls[i] != cls) continue;
                int first = at;
                _draw[at++] = i; _sTaken[i] = true;
                if (cls == ClsBlend)
                {
                    // adjacent-run merge, in capture order
                    for (int j = i + 1; j < n && _sCls[j] == cls && !_sTaken[j]
                                        && SameGroup(j, i); j++)
                    { _draw[at++] = j; _sTaken[j] = true; }
                }
                else
                {
                    // ponytail: O(n^2) gather over a list capped at 512, run once a frame. A key-sorted
                    // pass is the upgrade if an area ever puts hundreds of emitters on screen at once.
                    for (int j = i + 1; j < n; j++)
                        if (!_sTaken[j] && _sCls[j] == cls && SameGroup(j, i))
                        { _draw[at++] = j; _sTaken[j] = true; }
                }
                _gFirst[g] = first; _gCount[g] = at - first;
                _gTex[g] = _sTex[i]; _gTexObj[g] = _sTexObj[i];
                _gCls[g] = cls; _gCull[g] = _sCull[i]; _gEmis[g] = _sEmis[i];
                g++;
            }
        }
        // Only a real drop counts. Landing exactly ON the cap with every emitter placed is a full table,
        // not an overflow, and counting it would put a permanent warning on a frame that lost nothing.
        if (at < n) _overflow++;
        _drawN = at;
        return g;
    }

    // ---- the vertex build -----------------------------------------------------------------------------

    /// <summary>The camera-derived bases <c>RenderEmitter</c> computes once per emitter, hoisted to once per
    /// pass because every term in them is the camera's.
    /// <para>The quaternion at <c>CurrentCamera+0xbc</c> is stored (w, x, y, z), which is what makes these
    /// read as the standard rotations of the unit axes: <paramref name="fwd"/> is q(0,0,1),
    /// <paramref name="axisY"/> is q(0,1,0) and <paramref name="axisX"/> is q(1,0,0).</para></summary>
    private static void Basis(IntPtr cam, out Vector3 fwd, out Vector4 q, out Vector3 axisY, out Vector3 axisX)
    {
        float* c = (float*)(cam + CamQuat);
        float w = c[0], x = c[1], y = c[2], z = c[3];
        q = new Vector4(w, x, y, z);
        fwd = AxisZ(w, x, y, z);
        axisY = new Vector3(2f * (x * y - z * w), 1f - 2f * (x * x + z * z), 2f * (y * z + x * w));
        axisX = AxisX(w, x, y, z);
    }

    private static Vector3 AxisX(float w, float x, float y, float z)
        => new(1f - 2f * (y * y + z * z), 2f * (x * y + w * z), 2f * (x * z - w * y));

    private static Vector3 AxisZ(float w, float x, float y, float z)
        => new(2f * (x * z + w * y), 2f * (y * z - w * x), 1f - 2f * (x * x + y * y));

    /// <summary>Hamilton product, (w, x, y, z) in and out -- the engine's <c>Quaternion::operator*</c>.
    /// </summary>
    private static Vector4 QMul(in Vector4 a, in Vector4 b)
        => new(a.X * b.X - a.Y * b.Y - a.Z * b.Z - a.W * b.W,
               a.X * b.Y + a.Y * b.X + a.Z * b.W - a.W * b.Z,
               a.X * b.Z - a.Y * b.W + a.Z * b.X + a.W * b.Y,
               a.X * b.W + a.Y * b.Z - a.Z * b.Y + a.W * b.X);

    /// <summary>Port of <c>PartEmitter::RenderEmitter</c>'s build loop: six vertices per particle, written
    /// in the engine's own order and layout. Returns the vertex count written.
    /// <para>Allocation-free by construction -- this runs inside the colour bracket.</para></summary>
    private static int BuildEmitter(IntPtr em, ref Emit e, float* dst, int count,
                                    Vector3 camFwd, Vector4 camQ, Vector3 axisY, Vector3 axisX,
                                    Vector3 eye, bool drawsLit)
    {
        IntPtr node = *(IntPtr*)(em + EmNode);
        int type = *(int*)(em + EmType);
        float life = *(float*)(em + EmLife);
        bool splat = _isSplat(node) != 0;
        // #148: the CPU tint and the fragment's clustered lighting are the SAME quantity computed two ways --
        // one sample at the emitter's origin against the whole scene's lights, versus a per-fragment walk of
        // the cluster this fragment sits in. Applying both multiplies the scene lighting into itself, which
        // reads as a smoke plume that goes dark the moment it is properly lit. The fragment wins; the tint
        // stays for every path the fragment does not cover.
        bool tinted = !drawsLit && _isTinted(node) != 0;

        // scale: the Gob's, through the vtable slot RenderEmitter calls. No Gob means 1.
        IntPtr gob = *(IntPtr*)(em + EmGob);
        float scale = 1f;
        if (gob != IntPtr.Zero)
        {
            IntPtr* vt = *(IntPtr**)gob;
            var getScale = (delegate* unmanaged<IntPtr, float>)vt[GobScaleSlot / 8];
            scale = getScale(gob);
        }

        // ---- per-emitter terms, all hoisted exactly as the engine hoists them ----

        // the interpolation parameter's rate: over the particle's INDEX, or over its age
        bool byAge = *(short*)(em + EmLifeInterp) != 0;
        float rate = !byAge ? 1f / *(int*)(em + EmCount)
                            : life > 0f ? 1f / life : NoLifeRate;

        // colour, optionally run through the scene lighting at the emitter's position first
        Vector3 cStart = *(Vector3*)(em + EmColorStart), cEnd = *(Vector3*)(em + EmColorEnd);
        if (tinted)
        {
            EmitterLighting.V3 wp = *(EmitterLighting.V3*)(em + EmPos);
            IntPtr scene = *_currentScene;
            IntPtr lights = scene != IntPtr.Zero ? *(IntPtr*)(scene + SceneLights) : IntPtr.Zero;
            // The engine does not null-check the list because at its call site there always is one. Ours
            // runs a pass later, where an area transition can have taken it -- and the scan walks it
            // unconditionally, so a null would be a wild read rather than a black tint.
            if (lights != IntPtr.Zero)
            {
                EmitterLighting.V3 t = EmitterLighting.At(wp, lights, IntPtr.Zero);
                cStart = new Vector3(cStart.X * t.X, cStart.Y * t.Y, cStart.Z * t.Z);
                cEnd = new Vector3(cEnd.X * t.X, cEnd.Y * t.Y, cEnd.Z * t.Z);
            }
        }
        cStart = Vector3.Clamp(cStart, Vector3.Zero, Vector3.One) * ByteScale;
        cEnd = Vector3.Clamp(cEnd, Vector3.Zero, Vector3.One) * ByteScale;
        bool lerpColor = cStart != cEnd;

        // alpha: the emitter's own pair, folded with the tile's and the Gob's
        float aScene = _tileAlpha(em) * _gobAlpha(em);
        float aStart = Sat(*(float*)(em + EmAlphaStart) * aScene) * ByteScale;
        float aEnd = Sat(*(float*)(em + EmAlphaEnd) * aScene) * ByteScale;
        bool lerpAlpha = *(float*)(em + EmAlphaStart) != *(float*)(em + EmAlphaEnd);

        // half-extents; the engine only interpolates a dimension whose two ends actually differ
        float sx0 = *(float*)(em + EmSizeStartX), sx1 = *(float*)(em + EmSizeEndX);
        float sy0 = *(float*)(em + EmSizeStartY), sy1 = *(float*)(em + EmSizeEndY);
        float wStart = sx0 * scale * Half, wEnd = sx1 * scale * Half;
        float hStart = sy0 * scale * Half, hEnd = sy1 * scale * Half;
        bool lerpW = !splat && sx0 != sx1;
        bool lerpH = !splat && sy0 != sy1 && (sy0 != 0f || sy1 != 0f);

        // UV grid
        int gx = *(int*)(node + NodeGridX), gy = *(int*)(node + NodeGridY);
        bool grid = Grid.V && (gx > 1 || gy > 1);
        if (Diag.V)
        {
            if ((uint)type < 7) _cType[type]++;
            int blend = *(int*)(em + EmBlend);
            _cCls[blend == 1 ? ClsPunch : blend == 0 ? ClsBlend : ClsAdd]++;
            if (splat) _cSplat++;
            if (tinted) _cTinted++;
            if (grid) _cGrid++;
        }
        if (gx == 0) gx = 1;
        if (gy == 0) gy = 1;
        float du = 1f / gx, dv = 1f / gy;
        float fps = *(float*)(em + EmFps);
        float period = fps > 0f ? 1f / fps : 0f;
        float frameStart = *(float*)(em + EmFrameStart), frameEnd = *(float*)(em + EmFrameEnd);

        // #148: softness, hoisted with everything else the engine hoists. S is the largest half-extent the
        // emitter can REACH, so an end size only counts when the engine would actually interpolate to it --
        // the same two predicates the size lerp uses, not a blind max over all four.
        float sMax = MathF.Max(wStart, hStart);
        if (splat || sx0 != sx1) sMax = MathF.Max(sMax, wEnd);
        if (splat || sy0 != 0f || sy1 != 0f) sMax = MathF.Max(sMax, hEnd);
        float soft = SoftSuppressed(splat, type) || sMax <= 0f ? 0f : SoftScale / MathF.Min(sMax, 1f);

        // the world-Z flip render type 4 stands its quads up with: the engine picks it off the sign of the
        // camera's own Y axis in Z, once per emitter
        float flip = axisY.Z >= 0f ? 1f : -1f;

        // types 1 and 2 replace the camera basis with one of their own
        Vector3 fwd = camFwd; Vector4 q = camQ;
        if (type == 1)
        {
            float* o = (float*)(em + EmOrient);
            q = new Vector4(o[0], o[1], o[2], o[3]);
            fwd = AxisZ(o[0], o[1], o[2], o[3]);
        }
        else if (type == 2)
        {
            q = new Vector4(1f, 0f, 0f, 0f);
            fwd = new Vector3(0f, 0f, 1f);
        }

        // The render type, carried per vertex in the one byte the packed normal leaves spare. It costs
        // nothing when the viz dial is off and it is the difference between "particles are wrong" and
        // "Motion_Blur is wrong", which are questions of very different size.
        float vizType = type;
        IntPtr* parts = *(IntPtr**)(em + EmParts);
        float u0 = 0f, u1 = 1f, v0 = 0f, v1 = 1f;
        float* p = dst;

        for (int i = 0; i < count; i++)
        {
            IntPtr pt = parts[i];
            if (pt == IntPtr.Zero) continue;
            bool pSplat = *(byte*)(pt + PSplat) != 0;

            float t = 0f;
            float halfW = wStart, halfH = hStart;
            float af = aStart;
            uint cr = (uint)(long)cStart.X, cg = (uint)(long)cStart.Y, cb = (uint)(long)cStart.Z;
            if (life > 0f)
            {
                t = MathF.Min(1f, (byAge ? *(float*)(pt + PAge) : i) * rate);
                float it = 1f - t;
                if (lerpColor)
                {
                    cr = (uint)(long)(cEnd.X * t + cStart.X * it);
                    cg = (uint)(long)(cEnd.Y * t + cStart.Y * it);
                    cb = (uint)(long)(cEnd.Z * t + cStart.Z * it);
                }
                if (lerpAlpha) af = aStart * it + aEnd * t;
                if (lerpW) halfW = wStart * it + wEnd * t;
                if (lerpH) halfH = hStart * it + hEnd * t;
            }
            // An emitter that declared no Y size at all is SQUARE, not flat: the engine copies the X extent
            // across. Miss this and every single-size emitter -- which is most of them -- collapses to a
            // zero-height line.
            if (!lerpH && sy0 == 0f) halfH = halfW;
            // A landed splat freezes at the END size in both dimensions, and takes the X end for Y when no
            // Y end was declared.
            if (splat && pSplat) { halfW = wEnd; halfH = sy1 == 0f ? wEnd : hEnd; }

            uint ca = (uint)(long)af;
            uint col = (cr & 0xff) | ((cg & 0xff) << 8) | ((cb & 0xff) << 16) | ((ca & 0xff) << 24);
            uint colFar = col;

            Vector3 pos = *(Vector3*)(pt + PPos);
            // The particle's OWN position, kept aside: the linked type reassigns `pos` to the next particle
            // in the chain, and taking the view direction from that would anchor the normal one segment
            // along the chain from the quad it shades.
            Vector3 centre = pos;
            Vector3 far = pos;
            Vector3 right, up;

            if (type == 6 || type == 3)
            {
                // The stretched pair. Type 6 (motion-blurred) runs from the particle back to where the
                // previous simulation step left it; type 3 (linked) runs from the NEXT particle in the array
                // back to this one -- so the near edge of a linked quad is the next particle, not this one,
                // and reading it the other way round shifts a lightning bolt one segment along itself.
                Vector3 d;
                if (type == 6) { far = *(Vector3*)(pt + PPrevPos); d = pos - far; }
                else
                {
                    IntPtr nxt = parts[i + 1];
                    if (nxt == IntPtr.Zero) continue;
                    far = pos; pos = *(Vector3*)(nxt + PPos); d = pos - far;
                }
                float s = Vector3.Dot(axisX, d), c = Vector3.Dot(axisY, d);
                float inv = Rsqrt(s * s + c * c);
                s *= inv; c *= inv;
                right = axisX * c - axisY * s;
                up = axisY * c + axisX * s;
                halfW *= 2f; halfH *= Half;
                // ...and on the motion-blurred type only, the far end fades with how far the streak is
                // stretched. The linked type carries the same colour end to end.
                if (type == 6)
                {
                    float stretch = inv * halfW;
                    float fa = stretch <= 1f ? stretch * af : af;
                    colFar = (col & 0x00ffffffu) | (((uint)(long)fa & 0xff) << 24);
                }
            }
            else if (type == 5)
            {
                float* pq = (float*)(pt + PQuat);
                right = AxisX(pq[0], pq[1], pq[2], pq[3]);
                up = AxisZ(pq[0], pq[1], pq[2], pq[3]);
            }
            else
            {
                // The ordinary billboard: roll the quad about its normal, compose that onto the reference
                // orientation, and take the rotated X axis.
                // The two axes come from DIFFERENT flags and it matters: the ROLL follows the particle's own
                // splat state (a landed splat rolls about its own contact normal, against its own
                // quaternion), while the CROSS follows the emitter's, because a splat emitter's quads lie in
                // the ground plane whether or not this particular particle has landed yet.
                float roll = *(float*)(pt + PRoll) * Half;
                float sn = MathF.Sin(roll), cs = MathF.Cos(roll);
                Vector3 spinAxis = pSplat ? *(Vector3*)(pt + PSplatNormal) : fwd;
                // Field by field, not one Vector4 load: the quaternion sits at a 4-byte-aligned engine
                // offset and a 16-byte vector load off it is a fault waiting for the wrong allocation.
                float* pq2 = (float*)(pt + PQuat);
                Vector4 spinOn = pSplat ? new Vector4(pq2[0], pq2[1], pq2[2], pq2[3]) : q;
                Vector4 spin = QMul(new Vector4(cs, sn * spinAxis.X, sn * spinAxis.Y, sn * spinAxis.Z), spinOn);
                right = AxisX(spin.X, spin.Y, spin.Z, spin.W);
                // type 4 stands the quad up against world Z instead of against the camera
                up = type == 4 ? new Vector3(0f, 0f, flip)
                               : Vector3.Cross(splat ? *(Vector3*)(pt + PSplatNormal) : fwd, right);
            }

            right *= halfW;
            up *= halfH;

            if (grid) Frame(pt, period, frameStart, frameEnd, du, dv, gx, gx * gy,
                            out u0, out u1, out v0, out v1);

            // #261: the record is WORLD space. Transforming the centre and the two axes is exactly
            // equivalent to transforming the four corners for an affine matrix -- and the vertex stage's
            // hemispherical normal, which used to be four CPU normalisations per particle, falls out of the
            // same two axes there. See RecFloats for the layout.
            if (!e.Ident)
            {
                pos = Vector3.Transform(pos, e.Model);
                far = Vector3.Transform(far, e.Model);
                centre = Vector3.Transform(centre, e.Model);
                right = Vector3.TransformNormal(right, e.Model);
                up = Vector3.TransformNormal(up, e.Model);
            }
            Put(ref p, pos, col, far, colFar, right, soft, up, vizType, centre, u0, u1, v0, v1);
        }
        return (int)(p - dst) / RecFloats;
    }

    /// <summary>One record, in the <see cref="RecFloats"/> layout. The two colours ride the <c>w</c> slots
    /// as raw bits, which is what <c>uintBitsToFloat</c> undoes in the stage.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Put(ref float* p, in Vector3 pos, uint col, in Vector3 far, uint colFar,
                            in Vector3 right, float soft, in Vector3 up, float type,
                            in Vector3 centre, float u0, float u1, float v0, float v1)
    {
        p[0] = pos.X; p[1] = pos.Y; p[2] = pos.Z; *(uint*)(p + 3) = col;
        p[4] = far.X; p[5] = far.Y; p[6] = far.Z; *(uint*)(p + 7) = colFar;
        p[8] = right.X; p[9] = right.Y; p[10] = right.Z; p[11] = soft;
        p[12] = up.X; p[13] = up.Y; p[14] = up.Z; p[15] = type;
        p[16] = centre.X; p[17] = centre.Y; p[18] = centre.Z; p[19] = 0f;
        p[20] = u0; p[21] = u1; p[22] = v0; p[23] = v1;
        p += RecFloats;
    }

    /// <summary>FNV-1a over a record block, eight bytes at a time. The delta's whole change detector: the
    /// engine simulates particles and tells nobody, so "did this emitter move" is a question about its own
    /// bytes and nothing else.</summary>
    private static ulong Fnv(float* f, long floats)
    {
        ulong h = 14695981039346656037UL;
        ulong* q = (ulong*)f;
        for (long i = 0; i < floats / 2; i++) { h ^= q[i]; h *= 1099511628211UL; }
        if ((floats & 1) != 0) { h ^= *(uint*)(f + floats - 1); h *= 1099511628211UL; }
        return h;
    }

    /// <summary>#148: this material's <c>fEmissive</c>, or 1.0. Cold -- once per emitter per table slot.
    /// <para>Both lists, shared first then instance, exactly as <c>AlloyRenderer.ReadMaterial</c> walks
    /// them: the .mtr's declared values are parsed onto the SHARED material's list, while the instance list
    /// is merely sized to match and carries defaults, so reading the instance one alone would answer 1.0 for
    /// every effect this parameter exists for.</para>
    /// <para>ponytail: an emitter this pass DECLINES draws through the engine and sow-topdata's own
    /// <c>fsparticle</c>, which knows nothing about this parameter -- so an effect that flips between owned
    /// and declined flips brightness with it. Rare (declining is a custom shader, a missing texture or a
    /// fading tile, none of which toggle frame to frame) and not worth a mechanism; named because the
    /// symptom would otherwise read as a flickering bug.</para></summary>
    private static float ReadEmissive(IntPtr mat)
    {
        if (mat == IntPtr.Zero) return 1f;
        float v = 1f;
        IntPtr shared = *(IntPtr*)(mat + MatShared);
        if (shared != IntPtr.Zero)
            Scan(*(IntPtr*)(shared + SharedParams), *(int*)(shared + SharedParamCount), ref v);
        Scan(*(IntPtr*)(mat + MatParams), *(int*)(mat + MatParamCount), ref v);
        return v <= 0f ? 1f : MathF.Min(v, MaxEmissive);
    }

    private static void Scan(IntPtr list, int count, ref float v)
    {
        if (list == IntPtr.Zero || count <= 0 || count > MaxParams) return;
        for (int k = 0; k < count; k++)
        {
            byte* rec = (byte*)list + (long)k * ParamStride;
            if (*(int*)(rec + ParamType) != 0) continue;         // 1 = int-valued; this one is a float
            if (*(ushort*)(rec + ParamComps) < 1) continue;
            if (AlloyRenderer.NameIs(rec, EmissiveParam)) v = *(float*)(rec + ParamValue);
        }
    }

    private static float Sat(float x) => x < 0f ? 0f : x > 1f ? 1f : x;

    /// <summary>Normalise, or hand back zero. A degenerate axis (a zero-size emitter, a quad whose up-vector
    /// came out parallel to its right) would otherwise put a NaN in the packed normal and, through the
    /// shading, in the fragment.</summary>
    private static Vector3 Norm(Vector3 v)
    {
        float len = v.LengthSquared();
        return len > 0f ? v * (1f / MathF.Sqrt(len)) : Vector3.Zero;
    }

    /// <summary>Guard on the motion normalisation: a zero-length delta would otherwise divide by zero and
    /// make every corner of the quad NaN, which takes the WHOLE emitter's draw with it rather than one
    /// particle. Zero instead collapses that one quad to a point -- degenerate, invisible, and harmless.
    /// <para>That is not a fix for a vanilla defect: the engine's own <c>fast_rsqrt</c> path is degenerate
    /// at zero too, so a stationary motion-blurred particle is invisible in vanilla as well. The guard is
    /// here to keep the failure THAT small.</para></summary>
    private static float Rsqrt(float x) => x > 0f ? 1f / MathF.Sqrt(x) : 0f;

    /// <summary>UV-grid animation, including the frame ADVANCE -- which is a WRITE back into the particle
    /// (<c>+0x44</c> frame index, <c>+0x48</c> the age it last stepped at). Owning the vertex build means
    /// owning that write: suppress the engine's draw without it and every animated particle freezes on
    /// whatever frame it was showing.
    /// <para>Ascending (start &lt;= end) counts up and wraps back to start past the end; descending counts
    /// down and wraps past it the other way. Both come straight off the engine's two branches.</para>
    /// </summary>
    private static void Frame(IntPtr pt, float period, float frameStart, float frameEnd,
                              float du, float dv, int gx, int cells,
                              out float u0, out float u1, out float v0, out float v1)
    {
        int idx = *(int*)(pt + PFrame);
        float f = idx;
        if (period != 0f && frameStart != frameEnd)
        {
            float elapsed = *(float*)(pt + PAge) - *(float*)(pt + PFrameTime);
            if (elapsed >= period)
            {
                if (frameStart <= frameEnd)
                {
                    idx += (int)(elapsed / period);
                    if (idx > frameEnd) idx = (int)frameStart;
                }
                else
                {
                    idx += (int)(-elapsed / period);
                    if (idx < frameEnd) idx = (int)frameStart;
                }
                *(int*)(pt + PFrame) = idx;
                *(float*)(pt + PFrameTime) = *(float*)(pt + PAge);
                f = idx;
            }
        }
        if (Guard.V)
        {
            // CONTAINMENT, and the engine does not do it. The engine does not have to: it advances this
            // index every frame of every emitter's life, from the frame the particle is born. This pass
            // starts advancing an index it inherited mid-life, from a capture, after an area change or a
            // table reclaim -- so an index outside the frames the emitter actually declared is reachable
            // here and unreachable there, and the cell it lands on is some other effect's artwork.
            // The atlas bound always; the DECLARED range only when the emitter declared one. Equal ends
            // mean "this emitter does not animate", and its per-particle index is then a free choice the
            // model made -- a random frame per particle is a real authoring idiom. Clamping to the range
            // there would collapse every particle onto one cell, which is a different bug of my own making.
            float lo = 0f, hi = cells - 1;
            if (frameStart != frameEnd)
            {
                lo = MathF.Max(lo, MathF.Min(frameStart, frameEnd));
                hi = MathF.Min(hi, MathF.Max(frameStart, frameEnd));
            }
            if (hi < lo) hi = lo;
            f = f < lo ? lo : f > hi ? hi : f;
        }
        float row = MathF.Floor(f * du);
        u0 = (f - gx * row) * du;
        u1 = du + u0;
        v1 = 1f - row * dv;
        v0 = v1 - dv;
        if (Guard.V)
        {
            float iu = du * CellInset, iv = dv * CellInset;
            u0 += iu; u1 -= iu; v0 += iv; v1 -= iv;
        }
    }

    // ---- GL resources ---------------------------------------------------------------------------------

    private static bool EnsureProgram()
    {
        if (_prog != 0) return true;
        if (_failed) return false;
        string libs = AlloyLights.LocalShadowLib + AlloyLights.FragmentLib + AlloyLights.SunShadowLib;
        // #341: the fog half of KeyFogFs on its own -- this stage fogs and never dissolves.
        string fsLibs = libs + AlloyRenderer.FogFs;
        _prog = GlProgram.Graphics(ParticleVsHead + libs + ParticleVsMain,
            ParticleFsHead + fsLibs + ParticleFsMain, _log, "Particles");
        if (_prog == 0)
        { _failed = true; _log?.Info("Particles: program build failed -- emitters stay vanilla"); return false; }
        _locView = GlProgram.Loc(_prog, "uView");
        _locProj = GlProgram.Loc(_prog, "uProj");
        _locEye = GlProgram.Loc(_prog, "uEye");
        _locTex = GlProgram.Loc(_prog, "uTex");
        _locCut = GlProgram.Loc(_prog, "uAlphaCut");
        _fogLoc = Programs.FeatLocs(_prog);
        _locAdditive = GlProgram.Loc(_prog, "uAdditive");
        _locSoftOn = GlProgram.Loc(_prog, "uSoftOn");
        _locDepthTex = GlProgram.Loc(_prog, "uSceneDepth");
        _locEmissive = GlProgram.Loc(_prog, "uEmissive");
        _locLit = GlProgram.Loc(_prog, "uLit");
        _locViz = GlProgram.Loc(_prog, "uViz");
        _lAmbient = GlProgram.Loc(_prog, "uAreaAmbient");
        _lDiffuse = GlProgram.Loc(_prog, "uAreaDiffuse");
        _lDir = GlProgram.Loc(_prog, "uAreaDir");
        _lGrid = GlProgram.Loc(_prog, "uGrid"); _lTile = GlProgram.Loc(_prog, "uTile");
        _lZ = GlProgram.Loc(_prog, "uZ"); _lFalloff = GlProgram.Loc(_prog, "uFalloff");
        _lWrap = GlProgram.Loc(_prog, "uWrap"); _lAtten = GlProgram.Loc(_prog, "uAtten");
        _lHot = GlProgram.Loc(_prog, "uHot"); _lWrapN = GlProgram.Loc(_prog, "uWrapN"); _lSss = GlProgram.Loc(_prog, "uSss"); _lReach = GlProgram.Loc(_prog, "uReach");
        _lSunTex = GlProgram.Loc(_prog, "uSunShadow"); _lSunVp = GlProgram.Loc(_prog, "uSunVp");
        _lSunDynTex = GlProgram.Loc(_prog, "uSunDynShadow"); _lSunDyn = GlProgram.Loc(_prog, "uSunDyn");
        _lSunFocus = GlProgram.Loc(_prog, "uSunFocus"); _lSunRadius = GlProgram.Loc(_prog, "uSunRadius");
        _lSunTexel = GlProgram.Loc(_prog, "uSunTexel"); _lSunBias = GlProgram.Loc(_prog, "uSunBias");
        _lSunCount = GlProgram.Loc(_prog, "uSunCascades");
        _lCubeTex = GlProgram.Loc(_prog, "uLocalShadow"); _lCubeLight = GlProgram.Loc(_prog, "uLocalLight");
        _lCubePlane = GlProgram.Loc(_prog, "uLocalPlane"); _lCubeCount = GlProgram.Loc(_prog, "uLocalCubes");
        _lCubeSoft = GlProgram.Loc(_prog, "uLocalSoft"); _lCubeFloor = GlProgram.Loc(_prog, "uLocalFloor"); _lCubePcss = GlProgram.Loc(_prog, "uLocalPcss");
        _lCubeDynTex = GlProgram.Loc(_prog, "uLocalDynShadow"); _lCubeDynPage = GlProgram.Loc(_prog, "uLocalDynPage"); _lCubeFade = GlProgram.Loc(_prog, "uLocalFade");
        _lSunMedia = GlProgram.Loc(_prog, "uSunMedia");
        _lLocalMedia = GlProgram.Loc(_prog, "uLocalMedia");
        // The sampler UNITS, once, at link time rather than per pass. uSunShadow and uLocalShadow are a
        // sampler2DArrayShadow and a samplerCubeArrayShadow; leaving them at their default unit 0 puts three
        // different sampler types on one unit, which GL 4.6 makes an INVALID_OPERATION that discards the
        // whole draw. The per-pass uploads set them too -- but only inside `if (lit)`, so `/particles lit 0`
        // would otherwise mean "no particles at all" on a strict driver rather than "unlit particles".
        Gl.UseProgram(_prog);
        Gl.Uniform1i(_lSunTex, (int)AlloyRenderer.SunUnit);
        Gl.Uniform1i(_lSunDynTex, (int)AlloyRenderer.SunDynUnit);
        Gl.Uniform1i(_lCubeTex, (int)AlloyRenderer.CubeUnit);
        Gl.Uniform1i(_lCubeDynTex, (int)AlloyRenderer.CubeDynUnit);
        return true;
    }

    /// <summary>Grow the vertex buffer (and its CPU staging copy) until the frame fits, doubling the way the
    /// engine's own stream object does. Past the cap the pass stands down for the frame rather than
    /// truncating -- half an emitter is worse than a vanilla one.</summary>
    /// <summary>The sampler this pass reads every particle diffuse through. Deliberately un-mipmapped:
    /// every emitter on screen animates through a UV ATLAS (the #148 census reports uvgrid on all of them),
    /// and a mip level of an atlas averages neighbouring CELLS together -- so the frame a particle is meant
    /// to be showing gets blended with the frames beside it, along the cell borders, at whatever LOD the
    /// derivatives ask for. LINEAR without mips is also what makes the texture's completeness irrelevant,
    /// which is the trap <see cref="GrassField"/> needed two samplers to dodge.</summary>
    private static bool EnsureSampler()
    {
        if (_samp != 0) return true;
        if (Gl.BindSampler == null) return false;
        _samp = Gl.LinearClampSampler();
        return _samp != 0;
    }

    /// <summary>#288: build the index-build/sort program. Not an overridable stage -- ADR 0012's table is
    /// the set a content author can restyle, and there is no look here to restyle. A driver whose loader
    /// never produced the compute entry points latches <see cref="_csFailed"/> and the CPU path stands in.
    /// </summary>
    private static bool EnsureSort()
    {
        if (_csProg != 0) return true;
        if (_csFailed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindBufferBase == null)
        { _csFailed = true; return false; }
        _csProg = GlProgram.Compute(ParticleCs, _log, "Particles index build + depth sort");
        if (_csProg == 0) { _csFailed = true; return false; }
        _csEye = GlProgram.Loc(_csProg, "uEye");
        _log?.Info($"Particles: #288 armed -- index build and depth sort in compute, "
                   + $"{SortLocal} threads a group, sort cap {SortCap} particles");
        return true;
    }

    /// <summary>#288: fill the draw-order index list, and order the blended groups back-to-front, on the
    /// GPU. False means the caller does it on the CPU instead, unsorted -- which is what this pass did
    /// before the ticket.
    /// <para><b>The separability guarantee is structural, not arithmetic.</b> One workgroup owns one draw
    /// group; a draw group is single-class by construction (see <see cref="Group"/>); and the index ranges
    /// the groups occupy are disjoint and issued in group order. So no dispatch can move a particle out of
    /// its group, and #131's rule -- that additive and alpha-over do not commute with each other -- is
    /// preserved by the shape of the dispatch rather than by anything the shader is careful about.</para>
    /// </summary>
    /// <summary>#288: may the compute pass reorder this group? The whole of the ordering rule, in one place
    /// so that <see cref="SelfCheck"/> pins the SAME predicate <see cref="Order"/> stages rather than a
    /// restatement of it that can drift away from it.</summary>
    private static bool WillSort(int cls, int parts) => cls == ClsBlend && parts > 1 && parts <= SortCap;

    private static bool Order(int groups, Vector3 eye)
    {
        if (!Sort.V || !EnsureSort()) return false;

        // Only the alpha-blended class gets ordered. Punch-through writes depth and discards, so it is
        // already order-independent; addition commutes with itself, which is exactly why the additive class
        // could be batched across the screen in the first place -- reordering it would cost a sort and buy
        // nothing.
        for (int gi = 0; gi < groups; gi++)
        {
            uint count = (uint)_gParts[gi];
            bool sort = WillSort(_gCls[gi], _gParts[gi]);
            if (_gCls[gi] == ClsBlend && count > SortCap) _sortSkipped++;
            if (sort) _sorted++;
            _grp[gi * 4] = (uint)_gFirst[gi];
            _grp[gi * 4 + 1] = (uint)_gCount[gi];
            _grp[gi * 4 + 2] = (uint)_gStart[gi];
            _grp[gi * 4 + 3] = sort ? count | SortFlag : count;
        }

        Stage(ref _spanBuf, ref _spanCap, _span, _drawN * 4, BindSpans);
        Stage(ref _grpBuf, ref _grpCap, _grp, groups * 4, BindGroups);
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, 0);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindRecs, _recBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindIdx, _idxBuf);
        Gl.UseProgram(_csProg);
        Gl.Uniform3fv(_csEye, 1, (float*)&eye);
        Gl.DispatchCompute((uint)groups, 1, 1);
        // The vertex stage reads the index list as shader storage; BUFFER_UPDATE is for the other consumer,
        // which is a later frame's glBufferSubData onto the same buffer the moment /particles sort 0 is
        // typed. An incoherent shader write is not ordered against a client update without it.
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        return true;
    }

    /// <summary>Upload one descriptor array, growing its store by doubling. Both are small enough that the
    /// whole array goes up every pass: the spans are three numbers an emitter and the groups four, against
    /// the four bytes a PARTICLE this replaced.</summary>
    private static void Stage(ref uint buf, ref int cap, uint[] src, int uints, uint binding)
    {
        if (buf == 0) { uint b; Gl.GenBuffers(1, &b); buf = b; }
        Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, buf);
        if (cap < uints)
        {
            int want = cap > 0 ? cap : 256;
            while (want < uints) want *= 2;
            Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)((long)want * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
            cap = want;
        }
        fixed (uint* p = src)
            Gl.BufferSubData(Gl.SHADER_STORAGE_BUFFER, IntPtr.Zero, (IntPtr)((long)uints * sizeof(uint)), p);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, binding, buf);
    }

    /// <summary>#261: the persistent record buffer and the per-frame index list. Growth is by doubling and
    /// a grow REBUILDS the GL store, so every block's hash is void -- <see cref="EnsureBlock"/> is what
    /// re-sends them, one emitter at a time, on the next frame each is drawn.
    /// <para>There is no vertex buffer and no vertex format: the stage expands six vertices a particle from
    /// <c>gl_VertexID</c>, so the VAO is empty and exists only because core profile requires one bound.</para>
    /// </summary>
    private static bool EnsureBuffers(int parts)
    {
        if (parts <= 0 || parts > MaxRecs) { if (parts > MaxRecs) _overflow++; return false; }
        if (_vao == 0) { uint v; Gl.GenVertexArrays(1, &v); _vao = v; }
        if (_idxCap < parts)
        {
            int want = _idxCap > 0 ? _idxCap : MinRecs;
            while (want < parts) want *= 2;
            if (_idx != null) NativeMemory.Free(_idx);
            _idx = (uint*)NativeMemory.Alloc((nuint)((long)want * sizeof(uint)));
            _idxCap = want;
            if (_idxBuf == 0) { uint b; Gl.GenBuffers(1, &b); _idxBuf = b; }
            Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _idxBuf);
            Gl.BufferData(Gl.SHADER_STORAGE_BUFFER, (IntPtr)((long)want * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
        }
        // The record store is sized off the ALLOCATOR's high-water mark, not off this frame's particle
        // count: blocks are persistent, so an emitter that went off screen still owns its slice.
        if (_recsCap < _recTop + parts)
        {
            int want = _recsCap > 0 ? _recsCap : MinRecs;
            while (want < _recTop + parts && want < MaxRecs) want *= 2;
            if (want < _recTop + parts) { ResetBlocks(); return false; }
            float* grown = (float*)NativeMemory.Alloc((nuint)((long)want * RecFloats * sizeof(float)));
            if (_recs != null) NativeMemory.Free(_recs);
            _recs = grown;
            _recsCap = want;
            if (_recBuf == 0) { uint b; Gl.GenBuffers(1, &b); _recBuf = b; }
            Gl.BindBuffer(Gl.SHADER_STORAGE_BUFFER, _recBuf);
            Gl.BufferData(Gl.SHADER_STORAGE_BUFFER,
                          (IntPtr)((long)want * RecFloats * sizeof(float)), null, Gl.DYNAMIC_DRAW);
            // The new store holds nothing, so every block is stale by construction.
            for (int i = 0; i < _tabN; i++) _tab[i].Hash = 0;
        }
        return _recBuf != 0 && _idxBuf != 0;
    }

    /// <summary>Give this emitter a block big enough for <paramref name="count"/> records, bump-allocated
    /// and then left alone. Reassignment voids the hash, which is what re-sends the block.
    /// <para>ponytail: growing an emitter's block LEAKS its old one -- there is no free list, because an
    /// emitter's particle count is declared in its model and effectively never grows. The allocator's
    /// backstop is <see cref="ResetBlocks"/>: run out, start over, and pay one frame of vanilla emitters.
    /// <c>rec-resets=</c> on the period line is the trip-wire; a non-zero rate is when a free list becomes
    /// worth writing.</para></summary>
    private static bool EnsureBlock(ref Emit e, int count)
    {
        if (count <= 0) return false;
        if (e.RecCap >= count) return true;
        // Unreachable in practice -- EnsureBuffers already grew the store to _recTop + this frame's whole
        // particle count before the loop, which bounds every bump below. A decline rather than a reset
        // because a reset here would rebase blocks the loop has already written into the index list.
        if (_recTop + count > _recsCap) { _declined++; _decFull++; return false; }
        e.RecBase = _recTop; e.RecCap = count; e.Hash = 0;
        _recTop += count;
        return true;
    }

    /// <summary>The block allocator's only recovery: hand every slice back at once. Every emitter re-bumps
    /// on its next draw, so the cost is bounded by one frame.</summary>
    private static void ResetBlocks()
    {
        _recTop = 0;
        _recResets++;
        for (int i = 0; i < _tabN; i++) { _tab[i].RecBase = 0; _tab[i].RecCap = 0; _tab[i].Hash = 0; }
    }

    /// <summary>Every captured identity is void: the emitters belong to a Scene whose unload freed them.
    /// The GL objects are deliberately untouched -- this fires on every area transition, and rebuilding a
    /// program per area would be a stall for nothing.</summary>
    internal static void Forget()
    {
        _tabN = 0; _orderN = 0; _orderGen = -1; _scene = IntPtr.Zero;
        // The table went, so every block it named went with it. Not a reset: nothing is left to re-bump.
        _recTop = 0;
    }

    /// <summary>The 4.6 context went away. Every GL NAME this class holds belongs to it, so they are not
    /// merely unused -- they are stale, and there is nothing to delete because the objects went with the
    /// context.
    /// <para>This is separate from <see cref="Forget"/> for a reason with teeth. <c>EnsureProgram</c> returns
    /// true on <c>_prog != 0</c> and <c>EnsureBuffers</c> on its own caps, so a table-only reset would
    /// leave both believing they were ready, every GL call in the draw would fail silently, and the
    /// suppression stamp -- which keys on vertices STAGED, not on vertices drawn -- would go on suppressing
    /// the engine's own draw. The result is no particles anywhere, for the rest of the process, with nothing
    /// in the log to say so.</para></summary>
    internal static void ContextLost()
    {
        Forget();
        _prog = 0; _vao = 0; _recBuf = 0; _idxBuf = 0; _samp = 0; _failed = false;
        _csProg = 0; _spanBuf = 0; _grpBuf = 0; _csEye = -1; _csFailed = false; _spanCap = 0; _grpCap = 0;
        _locView = _locProj = _locTex = _locCut = _locAdditive = -1;
        _fogLoc = default;
        _locSoftOn = _locDepthTex = _locEmissive = _locLit = _locEye = -1;
        if (_recs != null) { NativeMemory.Free(_recs); _recs = null; }
        if (_idx != null) { NativeMemory.Free(_idx); _idx = null; }
        _recsCap = 0; _idxCap = 0;
    }

    /// <summary>One line for the <c>/particles</c> status and the arm-time log.</summary>
    internal static string Describe()
        => $"{(_off ? "UNAVAILABLE" : Enabled.V ? "on" : "off")} soft {(Soft.V ? 1 : 0)} lit {(Lit.V ? 1 : 0)} shadows {(Shadows.V ? 1 : 0)} " +
           $"viz {Viz.V} grid {(Grid.V ? 1 : 0)} guard {(Guard.V ? 1 : 0)} diag {(Diag.V ? 1 : 0)} " +
           $"sort {(Sort.V ? 1 : 0)}{(_csFailed ? " (NO COMPUTE -- CPU index build)" : "")} " +
           $"(batch {(Batch.Boot ? 1 : 0)}, sampler {(Sampler.Boot ? "owned" : "inherited")} -- both restart)";

    internal static void ResetPeriod()
    {
        _captured = 0; _declined = 0; _emitters = 0; _groups = 0; _foreign = 0; _suppressed = 0;
        _decFade = 0; _decEmpty = 0; _decShader = 0; _decTex = 0; _decFull = 0;
        _reclaimed = 0; _overflow = 0; _softPasses = 0; _emissive = 0; _litGroups = 0;
        for (int i = 0; i < 7; i++) _cType[i] = 0;
        for (int i = 0; i < ClsCount; i++) _cCls[i] = 0;
        _cSplat = 0; _cTinted = 0; _cGrid = 0;
        _blockUploads = 0; _blockSkips = 0; _recResets = 0; _sorted = 0; _sortSkipped = 0;
    }

    /// <summary>AC 4's number: emitters owned against draws issued. Vanilla's ratio is 1:1 by construction --
    /// it depth-sorts emitters individually and cannot merge two -- so anything above 1.0 here is a
    /// draw-call reduction the engine could not have made.</summary>
    internal static string Stats() => _off ? "particles vanilla (Alloy pass unavailable)"
        : !Enabled.V ? "particles vanilla (/particles enabled 0)"
        : $"particles {_tabN} emitters, {_emitters} owned in {_groups} draws"
          + (_emitters == 0 ? "" : $" ({(float)_emitters / MathF.Max(1, _groups):F1}x batched)")
          + $", {_suppressed} engine draws suppressed"
          + (_declined == 0 ? "" : $", {_declined} declined ({_decFade} fading, {_decEmpty} empty, " +
                                    $"{_decShader} custom shader, {_decTex} no texture, {_decFull} table full)")
          + (!Soft.V ? ", soft OFF"
                    : $", {_softPasses} passes softened" + (SceneDepth.Ready || _softPasses > 0 ? "" : " (NO DEPTH COPY)"))
          + (!Lit.V ? ", unlit" : $", {_litGroups} lit draws")
          + (_emissive == 0 ? "" : $", {_emissive} emissive")
          + (_reclaimed == 0 ? "" : $", {_reclaimed} table slots reclaimed from unwalked emitters")
          + $", {_blockUploads}/{_blockUploads + _blockSkips} record blocks re-sent"
          + (!Sort.V ? ", index built on the CPU, unsorted (/particles sort 0)"
             : _csFailed ? ", index built on the CPU, unsorted (NO COMPUTE)"
             : $", {_sorted} groups depth-sorted in compute"
               + (_sortSkipped == 0 ? "" : $", {_sortSkipped} sort-skipped (over {SortCap})"))
          + (_recResets == 0 ? "" : $", {_recResets} BLOCK-ALLOCATOR RESETS (see EnsureBlock)")
          + (_overflow == 0 ? "" : $", {_overflow} passes OVERFLOWED (group or record cap)")
          + (_foreign == 0 ? "" : $", {_foreign} foreign-Scene calls skipped")
          + Census();

    /// <summary><c>/particles diag 1</c> only: which of the seven billboard bases and which blend classes this period
    /// actually exercised. A zero against a render type is the useful reading -- it says that base is
    /// UNTESTED, not that it works.</summary>
    private static string Census()
    {
        if (!Diag.V) return "";
        var sb = new System.Text.StringBuilder(" [census");
        for (int i = 0; i < 7; i++) sb.Append($" {TypeNames[i]}={_cType[i]}");
        sb.Append($" | punch={_cCls[ClsPunch]} blended={_cCls[ClsBlend]} additive={_cCls[ClsAdd]}");
        sb.Append($" | splat={_cSplat} tinted={_cTinted} uvgrid={_cGrid}]");
        return sb.ToString();
    }

    /// <summary>#288: the compute stage's bitonic network, transcribed. It exists ONLY for
    /// <see cref="SelfCheck"/> -- the shader is the one that runs -- and it is written line for line against
    /// <see cref="ParticleCs"/> so that a change to one and not the other shows up as a failing check
    /// rather than as reordered smoke on somebody's screen.</summary>
    private static void SortModel(float[] key, uint[] val)
    {
        int count = key.Length;
        int n = 1;
        while (n < count) n <<= 1;
        var k = new float[n];
        var v = new uint[n];
        for (int i = 0; i < n; i++) { k[i] = i < count ? key[i] : 3.0e38f; v[i] = i < count ? val[i] : 0u; }
        for (int step = 2; step <= n; step <<= 1)
            for (int j = step >> 1; j > 0; j >>= 1)
                for (int i = 0; i < n; i++)
                {
                    int l = i ^ j;
                    if (l > i && (k[i] > k[l]) == ((i & step) == 0))
                    { (k[i], k[l]) = (k[l], k[i]); (v[i], v[l]) = (v[l], v[i]); }
                }
        for (int i = 0; i < count; i++) { key[i] = k[i]; val[i] = v[i]; }
    }

    // ---- shaders ---------------------------------------------------------------------------------------

    /// <summary>#288: the index build and the depth sort, in one dispatch of one workgroup per draw group.
    /// <para><b>What moved.</b> #261 already expanded the six vertices a particle from <c>gl_VertexID</c>
    /// against the persistent record buffer, so no per-quad geometry has been staged on the CPU since. What
    /// remained on the CPU was the DRAW-ORDER INDEX LIST -- one write and four bytes a particle, every
    /// frame, uploaded whole. That is now three numbers an emitter uploaded as a span descriptor, and this
    /// stage writes the list.</para>
    /// <para><b>The sort.</b> Alpha-over is the only class whose result depends on the order two of its own
    /// particles arrive in, so it is the only class ordered: a shared-memory bitonic network over squared
    /// distance to the eye, back to front. The other two are left alone deliberately -- punch-through writes
    /// depth and discards, and addition commutes with itself. #131's rule is that additive does NOT commute
    /// with alpha-over, and this dispatch cannot break it, because a workgroup only ever touches the index
    /// range of its own single-class group.</para>
    /// <para>The network is the textbook one: for each <c>k</c>, for each <c>j</c>, the element at <c>i</c>
    /// pairs with <c>i ^ j</c> and only the LOWER of the two swaps, so within one step every element belongs
    /// to exactly one pair and the stride loop cannot race itself. The tail past the particle count is
    /// padded with <c>+inf</c>, which sorts to the end and is never written back.</para></summary>
    /// <summary>The record and its buffer, as the vertex stage and the sort read it -- one text, so the
    /// two cannot disagree about the layout <c>RecFloats</c> stages.</summary>
    private const string ParticleRecDecl = """
        // #261: one particle. Six vec4, std430 -- see Particles.RecFloats for the C# side of this layout.
        struct CxParticle
        {
            vec4 posCol;      // xyz = near centre (world), w = RGBA8 colour as raw bits
            vec4 farColFar;   // xyz = far centre (world),  w = the far end's colour
            vec4 rightSoft;   // xyz = scaled right axis (world), w = particleSoftness
            vec4 upType;      // xyz = scaled up axis (world),    w = render type
            vec4 centre;      // xyz = the particle's OWN centre, which the facing direction is taken from
            vec4 uv;          // u0, u1, v0, v1
        };
        layout(std430, binding = 10) readonly buffer CxParticleBuf { CxParticle particles[]; };

        """;

    private const string ParticleCs = """
        #version 460 core
        layout(local_size_x = 256) in;

        """ + ParticleRecDecl + """
        layout(std430, binding = 11) buffer CxParticleIdx { uint partIdx[]; };
        // (record base, particles, index offset, -), one per emitter the pass placed
        layout(std430, binding = 17) readonly buffer CxSpanBuf { uvec4 spans[]; };
        // (first span, spans, index offset, particles | 0x80000000 when this group may be reordered)
        layout(std430, binding = 18) readonly buffer CxGroupBuf { uvec4 groups[]; };

        uniform vec3 uEye;

        const uint CX_SORT_CAP = 2048u;
        shared float sKey[CX_SORT_CAP];
        shared uint  sVal[CX_SORT_CAP];

        void main()
        {
            uvec4 g = groups[gl_WorkGroupID.x];
            uint tid = gl_LocalInvocationID.x;
            uint lanes = gl_WorkGroupSize.x;
            uint dst = g.z;
            uint count = g.w & 0x7fffffffu;

            for (uint s = 0u; s < g.y; ++s)
            {
                uvec4 sp = spans[g.x + s];
                for (uint j = tid; j < sp.y; j += lanes) partIdx[sp.z + j] = sp.x + j;
            }
            // The whole workgroup takes this branch together -- g is workgroup-uniform -- so leaving here
            // cannot strand anyone at a barrier below.
            if ((g.w & 0x80000000u) == 0u) return;
            memoryBarrierBuffer();
            barrier();

            uint n = 1u;
            while (n < count) n <<= 1u;
            for (uint i = tid; i < n; i += lanes)
            {
                if (i < count)
                {
                    uint r = partIdx[dst + i];
                    vec3 d = particles[r].centre.xyz - uEye;
                    sKey[i] = -dot(d, d);
                    sVal[i] = r;
                }
                else { sKey[i] = 3.0e38; sVal[i] = 0u; }
            }
            memoryBarrierShared();
            barrier();

            for (uint k = 2u; k <= n; k <<= 1u)
            {
                for (uint j = k >> 1u; j > 0u; j >>= 1u)
                {
                    for (uint i = tid; i < n; i += lanes)
                    {
                        uint l = i ^ j;
                        if (l > i && (sKey[i] > sKey[l]) == ((i & k) == 0u))
                        {
                            float fk = sKey[i]; sKey[i] = sKey[l]; sKey[l] = fk;
                            uint fv = sVal[i]; sVal[i] = sVal[l]; sVal[l] = fv;
                        }
                    }
                    memoryBarrierShared();
                    barrier();
                }
            }

            for (uint i = tid; i < count; i += lanes) partIdx[dst + i] = sVal[i];
        }
        """;

    /// <summary>The vertex stage. Everything the stock <c>vsparticle</c> does is already done by the time a
    /// vertex exists -- it is <c>VertexColor = vColor; TransformMainCoordinates();</c> and nothing else.
    /// <para>#261: attributeless. There is no vertex buffer; six vertices a particle are expanded from
    /// <c>gl_VertexID</c> against the persistent record buffer, through the draw-order index list. The
    /// corner signs reproduce the CPU build's own winding exactly -- (A, B, C) then (C, B, D), with A/B on
    /// the NEAR centre and C/D on the far one, which is what makes a motion-blurred streak fade along its
    /// length rather than across it.</para>
    /// <para>The hemispherical shading normal moved here with the expansion: it is
    /// <c>normalize(sx*rn + sy*un + facing)</c>, four CPU normalisations a particle before and one cheap
    /// vertex expression now. A degenerate quad (a zero half-extent) normalises a zero vector, which is why
    /// the axes go through a guarded normalise -- the quad has no area either way, but a NaN normal is not
    /// something to leave to chance.</para>
    /// <para>No #119 invariant clip expression: particles never enter the depth prepass (two of the three
    /// classes do not write depth at all), so there is no LEQUAL match to preserve.</para>
    /// <para>#336: the light rig runs HERE, six times a particle, not once a fragment. A torch's smoke at
    /// the camera covers the screen more than once over, and the cube taps per fragment were the whole
    /// 5ms that equipping one cost; a media particle has no detail a per-vertex term loses. The cluster
    /// lookup takes its screen tile from this vertex's own NDC (CX_CLUSTER_XY) since there is no
    /// gl_FragCoord to read; the libraries compose between the head and the main.</para></summary>
    private const string ParticleVsHead = """
        #version 460 core

        """ + ParticleRecDecl + """
        layout(std430, binding = 11) readonly buffer CxParticleIdx { uint partIdx[]; };

        uniform mat4 uView;
        uniform mat4 uProj;
        uniform vec3 uEye;

        out vec4 vColor;
        out vec2 vUv;
        out float vViewZ;
        out vec3 vWorld;
        out vec3 vNrm;
        out float vSoft;
        out vec3 vLight;            // #336: ambient + sun + point at this vertex; 1.0 when the draw is unlit
        flat out int vType;

        uniform int uLit;           // 0 = this draw is self-illuminated (additive, punch-through) or unlit
        uniform vec3 uAreaAmbient;
        uniform vec3 uAreaDiffuse;
        uniform vec3 uAreaDir;

        vec2 cxClusterXY;
        #define CX_CLUSTER_XY cxClusterXY

        """;

    /// <summary>The vertex main, composed after <see cref="AlloyLights"/>'s three libraries.</summary>
    private const string ParticleVsMain = """

        vec3 CxSafeNorm(vec3 v)
        {
            float l = dot(v, v);
            return l > 0.0 ? v * inversesqrt(l) : vec3(0.0);
        }

        void main()
        {
            int corner = gl_VertexID % 6;
            CxParticle p = particles[partIdx[gl_VertexID / 6]];

            // (A, B, C, C, B, D) as sign pairs: sx picks the right axis, sy picks the up axis AND which
            // end of a stretched pair the vertex sits on.
            const vec2 SIGNS[6] = vec2[6](vec2( 1.0,  1.0), vec2(-1.0,  1.0), vec2( 1.0, -1.0),
                                          vec2( 1.0, -1.0), vec2(-1.0,  1.0), vec2(-1.0, -1.0));
            vec2 sg = SIGNS[corner];
            bool near = sg.y > 0.0;

            vec3 base = near ? p.posCol.xyz : p.farColFar.xyz;
            vec3 world = base + sg.x * p.rightSoft.xyz + sg.y * p.upType.xyz;

            vec3 rn = CxSafeNorm(p.rightSoft.xyz);
            vec3 un = CxSafeNorm(p.upType.xyz);
            vec3 facing = CxSafeNorm(uEye - p.centre.xyz);

            vColor = unpackUnorm4x8(floatBitsToUint(near ? p.posCol.w : p.farColFar.w));
            vUv = vec2(sg.x > 0.0 ? p.uv.y : p.uv.x, near ? p.uv.w : p.uv.z);
            vWorld = world;
            vNrm = CxSafeNorm(sg.x * rn + sg.y * un + facing);
            vSoft = p.rightSoft.w;
            vType = int(p.upType.w);
            vec4 vp = uView * vec4(world, 1.0);
            vViewZ = -vp.z;
            gl_Position = uProj * vp;

            // #148's terms, the ones grass runs, on the hemispherical billboard normal -- per vertex (#336).
            // Only the alpha-blended class is lit: smoke, dust and fog are media a torch should pick out,
            // while additive and punch-through emit their own light and were never lit in vanilla either.
            vLight = vec3(1.0);
            if (uLit != 0)
            {
                // The cluster is the particle CENTRE's, for all six corners: a quad at the camera has corners
                // behind the eye and off the screen, and a corner clamped to an edge tile reads a different
                // light list from its neighbours -- a vertex popping dark as the smoke drifts, i.e. flicker.
                vec4 cv = uView * vec4(p.centre.xyz, 1.0);
                vec4 cc = uProj * cv;
                cxClusterXY = (cc.xy / max(cc.w, 1e-3) * 0.5 + 0.5) * vec2(uGrid.xy);
                float ndl = clamp(dot(vNrm, uAreaDir), 0.0, 1.0);
                vec3 sun = uAreaDiffuse * (ndl * CxSunShadow(world, vNrm, ndl, ndl));
                vec3 point = CxClusteredDiffuse(world, vNrm, max(-cv.z, 1e-3));
                // the 0.15 floor and the sun-facing wrap are inc_lighting.shd:679, as every other owned
                // stage runs them
                vLight = uAreaAmbient * mix(0.15, 1.0, ndl) + sun + point;
            }
        }
        """;

    /// <summary>The fragment stage, ported from the client's <c>fsparticle</c>: modulate, discard, fog.
    /// <para>The fog split is the stock one -- an additive fragment cannot be lerped towards the fog colour
    /// (adding fog colour BRIGHTENS a distant effect), so it loses alpha instead, which is what fading into
    /// fog means for something that only ever adds. The fog term itself is
    /// <see cref="AlloyRenderer"/>'s, so the Alloy frame fogs particles the same way it fogs everything
    /// else.</para>
    /// <para>#148 adds the soft-particle fade the stock stage has, in the stock stage's own PLACE: after the
    /// alpha discard, before fog. That ordering is why punch-through needs no special case -- its post-discard
    /// alpha goes nowhere with blending off, which is exactly vanilla's behaviour.</para>
    /// <para><b>Noir desaturation</b> is still absent, for the same reason it is absent from every other
    /// Alloy class -- the Alloy pass does not implement it at all, so particles stay consistent with the
    /// world around them rather than becoming the one coloured thing in a grey frame.</para>
    /// <para>No colour-space encode: since #139 the Alloy colour target holds DISPLAY-SPACE values (RGBA16F
    /// is headroom, not a colour space), so writing what vanilla wrote IS the match.</para></summary>
    internal const string ParticleFsHead = """
        #version 460 core
        #define CX_FS 1   // #642: gl_FragCoord is in reach for the shadow lib's dither
        in vec4 vColor;
        in vec2 vUv;
        in float vViewZ;
        in vec3 vWorld;
        in vec3 vNrm;
        in float vSoft;
        in vec3 vLight;
        flat in int vType;

        uniform sampler2D uTex;
        uniform float uAlphaCut;    // fAlphaDiscardValue: 0.2 punch-through, -1.0 (off) otherwise
        uniform int uAdditive;
        uniform float uEmissive;    // .mtr fEmissive: >1 pushes the effect into #139's HDR headroom
        uniform int uSoftOn;        // 0 = no depth copy this pass, so no fade
        // uViz: /particles viz: 1 = tint by render type, 2 = flat, no texture. #336: the light rig's terms
        // are the VERTEX stage's now; this stage only applies vLight. The libraries still compose in below
        // for CxLinear.

        """ + AlloyRenderer.SceneDepthLib + AlloyRenderer.FogDecl + """

        """;

    /// <summary>The body, composed after <see cref="AlloyLights"/>'s three libraries.</summary>
    private const string ParticleFsMain = """
        void main()
        {
            vec4 c = vColor * texture(uTex, vUv);
            if (uViz == 2)
            {
                // Flat and opaque-ish: the quads as QUADS. A soft smoke texture hides everything about the
                // geometry that could be wrong with it.
                frag = vec4(0.6, 0.6, 0.6, 0.35);
                return;
            }
            if (uViz == 1)
            {
                const vec3 tint[7] = vec3[7](vec3(1,0,0), vec3(0,1,0), vec3(0,0,1), vec3(1,1,0),
                                             vec3(1,0,1), vec3(0,1,1), vec3(1,1,1));
                frag = vec4(tint[clamp(vType, 0, 6)], c.a);
                return;
            }
            if (uAlphaCut >= 0.0 && c.a <= uAlphaCut) discard;

            // #132: the Alloy frame is LINEAR end to end, so the texel's own encode comes off here -- for
            // every particle, lit or not. An additive plume that stayed display-space would add the wrong
            // curve into a linear frame, which is exactly the compositing error the whole-frame resolve
            // exists to make impossible.
            vec3 albedo = CxLinear(c.rgb);
            // #336: lit per vertex; vLight is 1.0 on an unlit draw
            albedo *= vLight;
            c.rgb = albedo;

            // Soft particles, exactly where fsparticle puts them: after the discard, before fog. The fade is
            // squared because a linear ramp still shows its own leading edge as a line -- the square is what
            // the stock stage uses and what makes the contact read as a dissolve.
            if (uSoftOn != 0 && vSoft > 0.0)
            {
                float gap = CxSceneViewZ(gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0))) - vViewZ;
                float k = clamp(gap * vSoft, 0.0, 1.0);
                c.a *= k * k;
            }

            // #148: past 1.0 this leaves display range and lands in #139's headroom, where the bloom
            // threshold catches it because the effect IS bright -- not because a threshold happened to sit
            // where its texture did. Before fog: fog attenuates what is there, and emission is what is there.
            c.rgb *= uEmissive;

            // #341: the SAME fog the world pass runs -- ramp, height layer, sky-matched target and all.
            // An emitter that fogged toward a different colour than the terrain behind it is a smoke plume
            // that reads as a different weather.
            float f = CxFogFactor(vWorld, vViewZ);
            if (uAdditive != 0) c.a *= 1.0 - f;
            else c.rgb = CxFog(c.rgb, vWorld, vViewZ);
            frag = c;
        }
        """;
}
