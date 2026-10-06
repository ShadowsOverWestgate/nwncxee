using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Particles (ADR 0011 M3, #131). The vertex build, which is the only part of an emitter that was ever a
/// draw. Since CX-86 the particles it reads are <see cref="EmitterSim"/>'s, whose pool records are laid out
/// as engine <c>Particle</c>s so this file cannot tell the difference -- which is what makes
/// <c>emitsim.enabled=0</c> an A/B of the simulation alone. Vanilla builds them in
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
/// <para><b>Simulation runs before the anchor, and it is ours.</b> <c>Scene::DoEmitterBucket</c> @0x476f00
/// was the only caller of <c>PartEmitter::AnimateParticles</c> @0x4ac660 and RSP ran it at step 28, after
/// the frame anchor; #261 moved the walk ahead of the anchor and CX-86 replaced it with
/// <see cref="EmitterSim.Step"/>, which is also where the population, the sort and
/// <see cref="Enroll"/> now come from. ADR 0018's CX-86 amendment moved that boundary.</para>
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
    // Every emitter, particle, node and material field this pass reads is a member of its CX.Engine view
    // (PartEmitter, Particle, MdlNodeEmitter, Material); the RE provenance lives on the EngineLayout rows
    // those are generated from, and PartEmitter::RenderEmitter @0x4ad4c0 is the function they all came off.

    /// <summary>A list longer than this is a bad pointer, not a .mtr.</summary>
    private const int MaxParams = 64;
    /// <summary>#148: the .mtr parameter an effect declares to say how bright it really is. Named the way
    /// sow-topdata already names its own (<c>noirExempt</c> on <c>fsparticle</c>), and absent means 1.0.
    /// </summary>
    private const string EmissiveParam = "fEmissive";
    /// <summary>Refuse an absurd declaration rather than hand the bloom a value that blows the whole frame
    /// out. Ten is already four stops over white.</summary>
    private const float MaxEmissive = 10f;

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
        /// <summary>CX-86: this emitter's particle array and its count, as the enroller handed them in. The
        /// pass is indifferent to who stepped them -- <see cref="EmitterSim"/>'s pool records are laid out as
        /// engine <c>Particle</c>s, so both sources are a <c>Particle*</c> array -- which is what lets one
        /// build loop serve the owned simulation and the engine's own walk.</summary>
        public IntPtr* Parts;
        public int PartCount;
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
    private static RenderEmitterFn _origRender;
    private static PartEmitterDtorFn _origDtor;
    private static MatGetTextureFn _getTexture;
    private static AurTextureBindInUnitFn _bindInUnit;
    /// <summary>#237: the engine's bound-texture cache, snapshot-restored across the draw loop. See
    /// <see cref="SymbolMap.GlBoundTextures"/> for why a pass that draws in its own context has to.</summary>
    private static ulong* _engBoundTex;
    private static uint* _engActiveStage, _engStageEnabled;
    /// <summary>Entries in <c>GLRender::m_boundTextures</c>, from the 0x10-iteration clear loop in
    /// <c>GLRender::ResetRenderStates</c> @0x4d1810.</summary>
    private const int EngTexStages = 16;
    private static EmitterGetTileAlphaFn _tileAlpha;
    private static EmitterGetGobAlphaFn _gobAlpha;
    private static EmitterIsSplatFn _isSplat;
    private static EmitterIsTintedFn _isTinted;
    private static IntPtr* _currentScene;

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
    private static int _lSunTex = -1, _lSunDynTex = -1, _lCubeTex = -1, _lCubeCount = -1, _lCubeDynTex = -1,
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

        _getTexture = EngineFn.MatGetTexture;
        _bindInUnit = EngineFn.AurTextureBindInUnit;
        _engBoundTex = (ulong*)Native.Sym(SymbolMap.GlBoundTextures);
        _engActiveStage = (uint*)Native.Sym(SymbolMap.GlActiveTextureStage);
        _engStageEnabled = (uint*)Native.Sym(SymbolMap.GlEnabledTextureStages);
        _tileAlpha = EngineFn.EmitterGetTileAlpha;
        _gobAlpha = EngineFn.EmitterGetGobAlpha;
        _isSplat = EngineFn.EmitterIsSplat;
        _isTinted = EngineFn.EmitterIsTinted;
        EmitterLighting.Resolve();
        _currentScene = (IntPtr*)Native.Sym(SymbolMap.CurrentScene);

        if (_getTexture == null || _bindInUnit == null || _tileAlpha == null || _gobAlpha == null || _isSplat == null
            || _isTinted == null || !EmitterLighting.Ready || _currentScene == null
            || !AurTex.Ready)
        {
            _off = true;
            log.Info("Particles: an emitter helper, a global or AurTex did not resolve -- emitters stay vanilla");
            return;
        }

        _origRender = hooks.InstallRenderEmitter(&OnRenderEmitter);
        _origDtor = hooks.InstallPartEmitterDtor(&OnEmitterDtor);
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

        var ev = new PartEmitter(em);
        int slot = Capture(em, (IntPtr*)ev.Parts, ev.Count);
        // #261: the decline is what skips the ENGINE's own vertex build, which is this class's whole cost --
        // the funnels only swallow the draw. The shim runs this walk ahead of the anchor, so the stamp it
        // tests is one pass old: "drawn in the pass just gone".
        if (slot >= 0 && _tab[slot].IssuedGen == AlloyRenderer.PassGen - 1) { _suppressed++; return; }
        _origRender(em);
    }

    /// <summary>CX-86: enrolment from <see cref="EmitterSim"/>, which walks <c>emitterbucket</c> itself now
    /// that <c>Scene::DoEmitterBucket</c> is not called. Same two jobs <see cref="OnRenderEmitter"/> had --
    /// learn the population and its transform -- minus the suppression, because there is no original left to
    /// decline. The caller walks in its own back-to-front order, so capture order still IS draw order.
    /// </summary>
    internal static void Enroll(IntPtr em, IntPtr scene, IntPtr* parts, int count)
    {
        if (_off || !Enabled.V || em == IntPtr.Zero || parts == null || count <= 0) return;
        if (!AlloyRenderer.IsWorldScene(scene)) { _foreign++; return; }
        if (scene != _scene) Forget();
        _scene = scene;
        Capture(em, parts, count);
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
        // CX-86: the simulation keys a pool block by the same pointer the engine is about to free and reuse.
        EmitterSim.Forget(em);
        if (_origDtor != null) _origDtor(em);
    }

    /// <summary>Record this emitter and its place in the sort. Returns its table slot, or -1 when the table
    /// is full -- which is not a failure, it just leaves the emitter vanilla.</summary>
    private static int Capture(IntPtr em, IntPtr* parts, int count)
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
            _tab[s].Emissive = ReadEmissive(new PartEmitter(em).Material);
            _captured++;
        }
        ref Emit e = ref _tab[s];
        e.Ptr = em;
        e.CapFrame = _frame;
        e.Parts = parts; e.PartCount = count;
        // RenderEmitter pushes nothing and Scene::DoEmitterBucket does no matrix work either, so an emitter
        // draws on whatever base the frame left in the model stack -- which CX-109 established is the identity
        // (AlloyCamera.ModelBase, CX-109 §2). Particles simulate in world space, so this
        // is also the only base that would place them correctly.
        e.Model = AlloyCamera.ModelBase;
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

        IntPtr cam = AlloyCamera.World;
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
            // ponytail: a camera-derived basis in record slots 8..14 can never match across a rotation.
            // Upgrade: build the basis in cx_particle_v instead. See Fnv for the measurement that parks it.
            ulong h = Fnv(_recs + (long)e.RecBase * RecFloats, (long)wrote * RecFloats);
            if (h != e.Hash)
            {
                e.Hash = h;
                Gl.NamedBufferSubData(_recBuf,
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
            Gl.NamedBufferSubData(_idxBuf, 0, (IntPtr)((long)at * sizeof(uint)), _idx);
        }
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
            AlloyRenderer.BindPass(_lCubeCount, viewport, near, far);
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
                    SceneDepth.Invalidate();   // cutouts write depth: halos/weather must not reuse the pre-particle copy
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
            if (!Resolve(e.Ptr, e.Parts, e.PartCount, out int tex, out IntPtr texObj, out int cls,
                         out int cull, out int count))
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
    private static bool Resolve(IntPtr em, IntPtr* parts, int rawCount, out int tex, out IntPtr texObj,
                                out int cls, out int cull, out int count)
    {
        tex = 0; texObj = IntPtr.Zero; cls = 0; cull = 0; count = 0;

        // RenderEmitter's own opening early-out: a fading Gob or tile draws NO particles at all. Declining
        // is the cheapest reproduction -- the engine takes the same exit.
        var e = new PartEmitter(em);
        var g = new CX.Engine.Part(em).Gob;
        if (!g.IsNull)
        {
            TileData owner = g.TileData;
            if (!owner.IsNull && TileFadeRamp.Explored(owner.Ptr) < 1f) { _decFade++; return false; }
        }
        TileData tile = e.TileOwner;
        if (!tile.IsNull && TileFadeRamp.Explored(tile.Ptr) < 1f) { _decFade++; return false; }

        int type = e.Type;
        // Type 3 pairs each particle with the NEXT one, so the last has no partner and the loop runs one
        // short. The engine does the same subtraction.
        count = rawCount - (type == 3 ? 1 : 0);
        if (count < 1 || e.Life == 0f) { _decEmpty++; return false; }
        if (parts == null) { _decEmpty++; return false; }
        IntPtr node = new CX.Engine.Part(em).Node.Ptr;
        if (node == IntPtr.Zero) { _decEmpty++; return false; }

        IntPtr mat = e.Material.Ptr;
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

        int blend = e.Blend;
        cls = blend == 1 ? ClsPunch : blend == 0 ? ClsBlend : ClsAdd;
        cull = new MdlNodeEmitter(node).NoCull == 0 ? 1 : 0;
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
        Quat c = new CX.Engine.Camera(cam).Rot;
        float w = c.W, x = c.X, y = c.Y, z = c.Z;
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
        var emv = new PartEmitter(em);
        IntPtr node = new CX.Engine.Part(em).Node.Ptr;
        int type = emv.Type;
        float life = emv.Life;
        bool splat = _isSplat(node) != 0;
        // #148: the CPU tint and the fragment's clustered lighting are the SAME quantity computed two ways --
        // one sample at the emitter's origin against the whole scene's lights, versus a per-fragment walk of
        // the cluster this fragment sits in. Applying both multiplies the scene lighting into itself, which
        // reads as a smoke plume that goes dark the moment it is properly lit. The fragment wins; the tint
        // stays for every path the fragment does not cover.
        bool tinted = !drawsLit && _isTinted(node) != 0;

        // scale: the Gob's, through the vtable slot RenderEmitter calls. No Gob means 1.
        var gob = new CX.Engine.Part(em).Gob;
        float scale = gob.IsNull ? 1f : gob.GetAdjustedScale();

        // ---- per-emitter terms, all hoisted exactly as the engine hoists them ----

        // the interpolation parameter's rate: over the particle's INDEX, or over its age
        bool byAge = emv.LifeInterp != 0;
        float rate = !byAge ? 1f / MathF.Max(1, e.PartCount)
                            : life > 0f ? 1f / life : NoLifeRate;

        // colour, optionally run through the scene lighting at the emitter's position first
        Vector3 cStart = emv.ColorStart, cEnd = emv.ColorEnd;
        if (tinted)
        {
            Vector3 p3 = emv.Pos;
            EmitterLighting.V3 wp = new() { X = p3.X, Y = p3.Y, Z = p3.Z };
            IntPtr scene = *_currentScene;
            // CX-108: the registry's tint takes no manager; only the light.registry 0 path reads Scene+0x158.
            bool owned = LightRegistry.Live;
            IntPtr lights = owned || scene == IntPtr.Zero ? IntPtr.Zero : new CX.Engine.Scene(scene).LightManager.Ptr;
            // The engine does not null-check the list because at its call site there always is one. Ours
            // runs a pass later, where an area transition can have taken it -- and the scan walks it
            // unconditionally, so a null would be a wild read rather than a black tint.
            if (owned || lights != IntPtr.Zero)
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
        float aScene = _tileAlpha(em) * (PlaceableHold.Held(new Part(em).Gob.Ptr) ? 1f : _gobAlpha(em));
        float aStart = Sat(emv.AlphaStart * aScene) * ByteScale;
        float aEnd = Sat(emv.AlphaEnd * aScene) * ByteScale;
        bool lerpAlpha = emv.AlphaStart != emv.AlphaEnd;

        // half-extents; the engine only interpolates a dimension whose two ends actually differ
        float sx0 = emv.SizeStartX, sx1 = emv.SizeEndX;
        float sy0 = emv.SizeStartY, sy1 = emv.SizeEndY;
        float wStart = sx0 * scale * Half, wEnd = sx1 * scale * Half;
        float hStart = sy0 * scale * Half, hEnd = sy1 * scale * Half;
        bool lerpW = !splat && sx0 != sx1;
        bool lerpH = !splat && sy0 != sy1 && (sy0 != 0f || sy1 != 0f);

        // UV grid
        var ne = new MdlNodeEmitter(node);
        int gx = ne.GridX, gy = ne.GridY;
        bool grid = Grid.V && (gx > 1 || gy > 1);
        if (Diag.V)
        {
            if ((uint)type < 7) _cType[type]++;
            int blend = emv.Blend;
            _cCls[blend == 1 ? ClsPunch : blend == 0 ? ClsBlend : ClsAdd]++;
            if (splat) _cSplat++;
            if (tinted) _cTinted++;
            if (grid) _cGrid++;
        }
        if (gx == 0) gx = 1;
        if (gy == 0) gy = 1;
        float du = 1f / gx, dv = 1f / gy;
        float fps = emv.Fps;
        float period = fps > 0f ? 1f / fps : 0f;
        float frameStart = emv.FrameStart, frameEnd = emv.FrameEnd;

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
            Quat o = emv.Orient;
            q = new Vector4(o.W, o.X, o.Y, o.Z);
            fwd = AxisZ(o.W, o.X, o.Y, o.Z);
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
        IntPtr* parts = e.Parts;
        float u0 = 0f, u1 = 1f, v0 = 0f, v1 = 1f;
        float* p = dst;

        for (int i = 0; i < count; i++)
        {
            var pt = new Particle(parts[i]);
            if (pt.IsNull) continue;
            bool pSplat = pt.Splat != 0;

            float t = 0f;
            float halfW = wStart, halfH = hStart;
            float af = aStart;
            uint cr = (uint)(long)cStart.X, cg = (uint)(long)cStart.Y, cb = (uint)(long)cStart.Z;
            if (life > 0f)
            {
                t = MathF.Min(1f, (byAge ? pt.Age : i) * rate);
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

            Vector3 pos = pt.Pos;
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
                if (type == 6) { far = pt.PrevPos; d = pos - far; }
                else
                {
                    var nxt = new Particle(parts[i + 1]);
                    if (nxt.IsNull) continue;
                    far = pos; pos = nxt.Pos; d = pos - far;
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
                Quat pq = pt.Quat;
                right = AxisX(pq.W, pq.X, pq.Y, pq.Z);
                up = AxisZ(pq.W, pq.X, pq.Y, pq.Z);
            }
            else
            {
                // The ordinary billboard: roll the quad about its normal, compose that onto the reference
                // orientation, and take the rotated X axis.
                // The two axes come from DIFFERENT flags and it matters: the ROLL follows the particle's own
                // splat state (a landed splat rolls about its own contact normal, against its own
                // quaternion), while the CROSS follows the emitter's, because a splat emitter's quads lie in
                // the ground plane whether or not this particular particle has landed yet.
                float roll = pt.Roll * Half;
                float sn = MathF.Sin(roll), cs = MathF.Cos(roll);
                Vector3 spinAxis = pSplat ? pt.SplatNormal : fwd;
                // Through Quat, not one Vector4 load: the quaternion sits at a 4-byte-aligned engine offset
                // and Vector4's 16-byte alignment is a fault waiting for the wrong allocation.
                Quat pq2 = pt.Quat;
                Vector4 spinOn = pSplat ? new Vector4(pq2.W, pq2.X, pq2.Y, pq2.Z) : q;
                Vector4 spin = QMul(new Vector4(cs, sn * spinAxis.X, sn * spinAxis.Y, sn * spinAxis.Z), spinOn);
                right = AxisX(spin.X, spin.Y, spin.Z, spin.W);
                // type 4 stands the quad up against world Z instead of against the camera
                up = type == 4 ? new Vector3(0f, 0f, flip)
                               : Vector3.Cross(splat ? pt.SplatNormal : fwd, right);
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
    /// bytes and nothing else.
    /// <para>It cannot match for an emitter whose billboard basis is the CAMERA's: slots 8..14 carry `right`
    /// and `up`, and render types 0, 3, 4 and 6 derive them from the camera's axes, so a rotation dirties even
    /// an emitter whose particles did not move. Left that way deliberately -- measured at 2,1 blocks and about
    /// 8KB a frame, and the case it would win does not exist, since a settled emitter here is a landed SPLAT
    /// whose right and up come from the particle's own contact normal and quaternion and already match, while
    /// the emitters that do re-send are live effects whose particles are moving anyway.</para></summary>
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
    private static float ReadEmissive(CX.Engine.Material mat)
    {
        if (mat.IsNull) return 1f;
        float v = 1f;
        var mr = new MaterialRef(mat.Ptr);
        SharedRef shared = mr.Shared;
        if (!shared.IsNull) Scan(shared.Params, shared.ParamCount, ref v);
        Scan(mr.Params, mr.ParamCount, ref v);
        return v <= 0f ? 1f : MathF.Min(v, MaxEmissive);
    }

    private static void Scan(IntPtr list, int count, ref float v)
    {
        if (list == IntPtr.Zero || count <= 0 || count > MaxParams) return;
        var first = new MaterialParameter(list);
        for (int k = 0; k < count; k++)
        {
            MaterialParameter mp = first[k];
            if (mp.Type != 0) continue;                          // 1 = int-valued; this one is a float
            if (mp.Comps < 1) continue;
            if (AlloyRenderer.NameIs((byte*)mp.Ptr, EmissiveParam)) v = mp.Value;
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
    private static void Frame(Particle pt, float period, float frameStart, float frameEnd,
                              float du, float dv, int gx, int cells,
                              out float u0, out float u1, out float v0, out float v1)
    {
        int idx = pt.Frame;
        float f = idx;
        if (period != 0f && frameStart != frameEnd)
        {
            float elapsed = pt.Age - pt.FrameTime;
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
                pt.Frame = idx;
                pt.FrameTime = pt.Age;
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
        _prog = GlProgram.Graphics(ShaderSource.Get("cx_part_v.glsl"),
            ShaderSource.Get("cx_part_f.glsl"), _log, "Particles");
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
        _lSunTex = GlProgram.Loc(_prog, "uSunShadow"); _lSunDynTex = GlProgram.Loc(_prog, "uSunDynShadow");
        _lCubeTex = GlProgram.Loc(_prog, "uLocalShadow"); _lCubeDynTex = GlProgram.Loc(_prog, "uLocalDynShadow");
        _lCubeCount = GlProgram.Loc(_prog, "uLocalCubes");
        _lSunMedia = GlProgram.Loc(_prog, "uSunMedia");
        _lLocalMedia = GlProgram.Loc(_prog, "uLocalMedia");
        // The sampler UNITS, once, at link time rather than per pass. uSunShadow and uLocalShadow are a
        // sampler2DArrayShadow and a samplerCubeArrayShadow; leaving them at their default unit 0 puts three
        // different sampler types on one unit, which GL 4.6 makes an INVALID_OPERATION that discards the
        // whole draw, so `/particles lit 0` would otherwise mean "no particles at all" on a strict driver.
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
        if (buf == 0) { uint b; Gl.CreateBuffers(1, &b); buf = b; }
        if (cap < uints)
        {
            int want = cap > 0 ? cap : 256;
            while (want < uints) want *= 2;
            Gl.NamedBufferData(buf, (IntPtr)((long)want * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
            cap = want;
        }
        fixed (uint* p = src)
            Gl.NamedBufferSubData(buf, IntPtr.Zero, (IntPtr)((long)uints * sizeof(uint)), p);
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
        if (_vao == 0) { uint v; Gl.CreateVertexArrays(1, &v); _vao = v; }
        if (_idxCap < parts)
        {
            int want = _idxCap > 0 ? _idxCap : MinRecs;
            while (want < parts) want *= 2;
            if (_idx != null) NativeMemory.Free(_idx);
            _idx = (uint*)NativeMemory.Alloc((nuint)((long)want * sizeof(uint)));
            _idxCap = want;
            if (_idxBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _idxBuf = b; }
            Gl.NamedBufferData(_idxBuf, (IntPtr)((long)want * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
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
            if (_recBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _recBuf = b; }
            Gl.NamedBufferData(_recBuf,
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
    internal static string Stats(int frames) => _off ? "particles vanilla (Alloy pass unavailable)"
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
          // Per FRAME, like everything around it: as a period total this read "1927/1927 record blocks
          // re-sent" for what is two blocks and 8KB a frame, which is an alarm where there is no bus traffic.
          + $", {(float)_blockUploads / MathF.Max(1, frames):F1} of {(float)(_blockUploads + _blockSkips) / MathF.Max(1, frames):F1} record blocks re-sent per frame"
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

    private static string ParticleCs => ShaderSource.Get("cx_part_c.glsl");

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
    private static string ParticleVsHead => ShaderSource.Get("cx_part_vhead.glsl");

    /// <summary>The vertex main, composed after <see cref="AlloyLights"/>'s three libraries.</summary>
    private static string ParticleVsMain => ShaderSource.Get("cx_part_vmain.glsl");

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
    internal static string ParticleFsHead => ShaderSource.Get("cx_part_fhead.glsl");

    /// <summary>The body, composed after <see cref="AlloyLights"/>'s three libraries.</summary>
    private static string ParticleFsMain => ShaderSource.Get("cx_part_fmain.glsl");
}
