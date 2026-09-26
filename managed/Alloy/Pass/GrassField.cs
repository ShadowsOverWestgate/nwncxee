using System.Collections.Generic;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Grass, as a REPLACEMENT class (ADR 0011 M3, #127): the engine's <c>Scene::DoBinBucket</c> is left exactly
/// as it is and simply not called on the passes the Alloy frame draws. Vanilla enables ordinary alpha blend,
/// NEVER calls <c>SetDepthMask</c> (so it inherits mask = 1) and pushes its distance fade through the same
/// alpha channel the 0.2 cutout reads, so from 15u to ~27.7u every blade is semi-transparent AND
/// depth-writing, drawn at step 9 before the creatures standing in it; it also rewrites the wind into every
/// bin's vertex buffer on the CPU every frame, culls nothing and instances nothing.
/// <para>CX-24: this is instead GPU-driven. The CPU culls whole patches; a compute scatter regenerates each
/// visible patch's blades from its faces' seeds, culls them one by one (frustum, distance ladder, solids,
/// snow) and appends them to indirect draws: procedural blades (opaque lit strips, 15 vertices near and 3
/// far, no discard, so early-Z works) for atlas cells that look like grass under the default
/// <see cref="Grass.Style"/>, and the atlas's own alpha-cut cards for the rest -- every cell under the texture
/// style. A card discards with no blending and fades by dither, so nothing the field draws is ever
/// semi-transparent and depth-writing at once: vanilla's punch-through stays structurally impossible.</para>
/// <para>The geometry SOURCE is still the engine's. <c>CAurTriangleList</c> holds the tile faces that carry a
/// grass material in world space, plus the per-face blade count the tileset's <c>GrassDensity</c> produced;
/// this class reads those and does its own scatter. Placement is the tileset's, while the DENSITY is a client
/// dial (<c>/grass density</c>) applied to the authored per-face ratios. The engine still builds its own bins
/// and VDOs at area load, untouched, which is what keeps ADR 0010's vanilla render mode whole.</para>
/// <para>Not in the depth prepass, deliberately: grass displaces vertices, so the prepass would record its
/// rest silhouette and the colour pass's LEQUAL would punch holes wherever the wind moved a blade. Grass
/// therefore does NOT carry the character-for-character <see cref="AlloyRenderer"/> clip expression, and
/// must not be "fixed" to (#119). Not a caster either: vanilla grass cast no shadow (#133).</para>
/// </summary>
internal static unsafe partial class GrassField
{
    /// <summary>Patches (one per visible bin x grass material) the cache holds, and the open-addressed slot
    /// table that interns them by <c>CAurTriangleList*</c> (power of two, ~2x for a 0.5 load factor).</summary>
    private const int MaxPatches = 32768, PatchHash = 65536;
    /// <summary>CX-24: grass FACES resident on the GPU, not blades -- the scatter regenerates every blade from
    /// its face's seed each frame, so the resident cost no longer grows with the density dial or the draw
    /// distance. 48 bytes a face (12MB at the cap); a 32x32 outdoor area is well under 100k faces.</summary>
    private const int MaxFaces = 262_144;
    /// <summary>Floats per resident face: <c>vec4 (a, first blade)</c>, <c>vec4 (b, seed)</c>, <c>vec4 (c, blades)</c>,
    /// the integers as their float bit patterns.</summary>
    private const int FaceFloats = 12;
    /// <summary>Blades one patch may carry. A patch over it has every face's count scaled down (and the period
    /// line says so); under it, a big patch is split across up to <see cref="MaxSplit"/> workgroups.</summary>
    internal const int MaxPatchBlades = 1 << 18;
    /// <summary>Workgroups one patch may be split across, and the blades each is sized for.</summary>
    private const int MaxSplit = 64, SplitBlades = 2048;
    /// <summary>Records the scatter may append per frame: 96MB of stream. Each draw takes a region sized on the
    /// CPU to its exact upper bound this frame, so a region never overflows; only the sum can pass this, and
    /// the period line says when it did.</summary>
    private const int RecordCap = 1 << 21;
    /// <summary>Card materials drawn per frame, one indirect draw each (a card draws with its atlas bound).
    /// The tileset's own plus a server's overrides: a handful in practice.</summary>
    private const int Slots = 32;
    /// <summary>Indirect commands: procedural blades near (0) and far (1), then one per card slot.</summary>
    private const int Cmds = 2 + Slots;
    /// <summary>Procedural blade strip pairs: 7 near (15 vertices), 1 far (a single triangle).</summary>
    private const int NearSegments = 7, FarSegments = 1;
    /// <summary>Floats per drawn record: the card stage's two instance vec4s plus the ground's normal.</summary>
    private const int InstFloats = 12;
    /// <summary>SSBO bindings of the scatter. Shared with <see cref="CullPass"/>'s 12..16 on purpose: that pass
    /// rebinds all of them before every dispatch, and nothing but the two compute passes reads them.</summary>
    private const uint BindFaces = 12, BindPatches = 13, BindMats = 14, BindOut = 15, BindCmd = 16;
    /// <summary>vec4s per material in the shared table (<c>cx_gbladebuf.glsl</c>'s <c>CX_GRASS_MATV</c>).</summary>
    private const int MatVec4 = 16;
    /// <summary>Patches baked per frame. Walking into a fresh area makes every visible bin new at once, and
    /// baking them all on one frame is a visible hitch; spread over a few frames it is not. The unbaked
    /// remainder is simply not drawn yet, which for one or two frames reads as grass fading in.</summary>
    private const int BakePerFrame = 48;
    /// <summary>A face that wants more blades than this is a bad read, not a meadow -- refuse it rather than
    /// scatter a million quads off one triangle.</summary>
    private const int MaxBladesPerFace = 4096;
    /// <summary>Bounds on the part-tree walk that answers "is this gob solid". A tree deeper or wider than
    /// this is a bad pointer, not a model.</summary>
    private const int MaxPartDepth = 12, MaxPartChildren = 4096;

    /// <summary>The byte <c>Gob::SetAsCharacter</c> @0x444790 writes into the gob's MdlNode
    /// (<see cref="EngineLayout.NodeOwnerFlags"/>): the only positive "this gob is a creature" mark in the
    /// client. Keeps creatures OUT of the grass cull -- they bend grass, they do not erase it.</summary>
    private const int NodeCharacterValue = 4;
    /// <summary>Vertices in the blade template: two quads crossed at 90 degrees, six vertices each. Vanilla
    /// draws ONE flat card per blade, which reads as a paper cutout from the side; a cross costs one extra
    /// quad per blade and is the cheapest thing that makes grass look like grass from any angle.</summary>
    private const int TemplateVerts = 12;
    /// <summary>The grass displacement field: a top-down RG8 push map rebuilt on the CPU every frame around
    /// the player. 128 texels over <see cref="Grass.PushExtent"/> world units is ~0.25u per texel at the default
    /// 64u window -- fine enough to shape a footfall, small enough (32KB) that the rebuild and the upload are
    /// both noise. Vanilla's "creature wind" is a CPU rewrite of the affected bins' vertex data; this is one
    /// vertex-shader texture fetch instead, and it costs the same whether one creature is walking through the
    /// field or forty.</summary>
    private const int PushRes = 256;
    /// <summary>Bytes per push texel: RG is the signed lateral direction (128 = none), B is how hard this
    /// texel is being pressed DOWN, and A is the CULL mask -- where a solid object stands, grass is not
    /// removed from the scatter (objects move) but collapsed to nothing in the vertex stage. The flatten term needs its own channel because the two peak in different
    /// places -- directly under a gob there is no direction to lean in, which is exactly where the grass
    /// should be most flattened. Folding both into the RG magnitude leaves a tuft standing upright under
    /// every creature's feet.</summary>
    private const int PushChannels = 4;
    /// <summary>Texture unit the push map binds on. It SHARES 10 with the #139 resolve, which is safe only
    /// because they are different programs that never run in the same bracket -- and both rebind their units
    /// every pass. <see cref="TextureSlots"/> is the ladder and the only place it is written down; the
    /// paraphrase that used to sit here had already drifted two units out of date.</summary>
    private const uint PushUnit = TextureSlots.GrassPush;
    /// <summary>Grass material slots the pass resolves textures for. The engine's own <c>g_GrassInfo</c> list
    /// is short -- the tileset default is slot 3 and area/server overrides fill the rest -- and the list's
    /// real count still gates every read; this only bounds the per-frame resolve loop.</summary>
    private const int MaxGrassMaterials = 128;

    /// <summary>The blade template's six corners as (side, z, u): side is -1 or +1 across the quad's own
    /// axis, z is 0 at the root and 1 at the tip, and v equals z.</summary>
    private static readonly float[] Corner =
    {
        -1f, 0f, 0f,   1f, 0f, 1f,   1f, 1f, 1f,
        -1f, 0f, 0f,   1f, 1f, 1f,  -1f, 1f, 0f,
    };

    private static ExtenderLog? _log;
    private static DoBinBucketFn _origBin;
    private static ReloadGrassFn _origReload;
    private static AurTextureGetFn _aurGet;
    /// <summary><c>binbucket</c> (the BSP walk's active bins) and <c>g_GrassInfo</c> (the material records)
    /// are both bare list heads at global addresses.</summary>
    private static EngineList _binBucket, _grassInfo;
    private static byte* _grassEnabled;
    private static float* _farDist, _nearRadius;
    /// <summary>The RESOLUTION latch: a grass global, the texture loader or a hook that did not come up, so
    /// this client cannot run the Alloy pass at all. Separate from <see cref="Grass.Enabled"/> on purpose --
    /// <c>/grass enabled 1</c> must never re-enable a pass that could not arm.</summary>
    private static bool _off;
    private static readonly StandDown _down = new("GrassField");

    /// <summary>Baked patches. <see cref="Patch.List"/> is an IDENTITY, never dereferenced outside the frame
    /// it was sighted in <c>binbucket</c> on -- the bins are freed wholesale by <c>ReloadGrass</c>, which is
    /// what <see cref="Forget"/> hangs off.</summary>
    private struct Patch
    {
        public IntPtr List;
        public Vector3 Min, Max;
        /// <summary><c>First</c>/<c>Count</c> are resident FACES; <c>Blades</c> the patch's blade total.</summary>
        public int Mat, First, Count, Blades, Seen;
    }

    private static readonly Patch[] _patches = new Patch[MaxPatches];
    private static readonly int[] _slot = new int[PatchHash];
    private static int _patchCount, _faceCount;
    private static long _bladesResident;
    /// <summary>#196: <see cref="Grass.Shadow"/> sun cascades (nearest first) the blades are drawn into as
    /// casters; 0 = grass casts nothing. The draw reuses the depth prepass program under the cascade's
    /// matrix on LAST frame's visible list (the caster pass runs ahead of the colour pass), and only into a
    /// cascade the frame re-baked, so the shadow follows the sway at the static cascade's own cadence.</summary>
    private static int _shadowN;
    private static Vector3 _shadowEye;
    private static float _shadowNear, _shadowFar;
    private static long _shadowDraws;
    /// <summary><c>/grass dumpshadow</c>: write the next cast layer back as a PGM beside the shader dump.
    /// The render thread consumes it; the button only raises it.</summary>
    private static bool _dumpShadow;
    private static int _dumpN;
    /// <summary>Lines left for the copy's framebuffer diagnostic (<see cref="Grass.GTint"/> and
    /// <see cref="Grass.GViz"/> re-arm three). GViz: 1 = every blade paints the copy texel behind it (the
    /// scene should show through), 2 = the copy's centre texel, 3 = gl_FragCoord over the copy size as
    /// red/green, 4 = a normalized texture() read.</summary>
    private static int _groundDiag = 3;
    private static int _prepGen = -1, _seen;
    /// <summary>#196: does cascade <paramref name="i"/> take the blades as casters this frame?</summary>
    internal static bool CastsInto(int i) => i < Grass.Shadow.V && _shadowN > 0 && Grass.Enabled.V && !_off && _progDepth != 0;
    /// <summary>CPU-side face stream. Allocated once, cold: the draw path never allocates.</summary>
    private static float[]? _faces;
    private static int _uploaded;

    /// <summary>The frame's visible patch table, two uvec4 each (<c>cx_gblade_c.glsl</c>'s <c>uPatch</c>).</summary>
    private static readonly uint[] _visTable = new uint[MaxPatches * 8];
    private static readonly float[] _matTable = new float[MaxGrassMaterials * MatVec4 * 4];
    /// <summary>Draw slot -> the material drawn in it; <see cref="_slotCount"/> are live this frame.</summary>
    private static readonly int[] _slotMat = new int[Slots];
    private static readonly int[] _slotBase = new int[Slots];
    private static int _slotCount, _slotsOver, _clipped;

    private static uint _vao, _faceBuf, _patchBuf, _matBuf, _outBuf, _cmdBuf, _statBuf, _prog, _progDepth;
    private static uint _progScatter, _progBlade, _progBladeDepth;
    /// <summary>The persistent map of <see cref="_statBuf"/>: the command counts copied after every draw, read
    /// by <see cref="Stats"/> a frame or more late without a sync.</summary>
    private static uint* _stat;
    /// <summary>Two sampler objects because a texture with no mip chain is mipmap-INCOMPLETE under a mipmap
    /// min filter and samples as opaque black -- so the filter has to follow the texture, not the dial. A
    /// sampler object is the right tool either way: it overrides filtering for OUR draw only, where setting
    /// the parameters on the texture would change how the engine's own passes sample it.</summary>
    private static uint _sampMip, _sampFlat;
    private static uint _pushTex;
    /// <summary>The push field, CPU side, RG8 with 128 as "no push". Allocated once.</summary>
    private static byte[]? _push, _pushClear;
    private static float _pushOriginX, _pushOriginY, _pushPerUnit;
    private static int _pushers, _cullers, _reticles;
    private static PartGetIsOpaqueFn _isOpaque;
    private static int _drawnGen = -1, _frame;
    /// <summary>Set from the chat thread by a <see cref="Grass.Density"/> set; consumed by the render thread
    /// at the top of <see cref="Draw"/>. The tables it clears are the render thread's, so the flag is the
    /// handoff -- clearing them where the command runs would race a live bake.</summary>
    private static volatile bool _rebake;
    /// <summary>The dials live in <see cref="Grass"/> (ADR 0025); what stays here is the render-thread state
    /// a set arms (<see cref="SamplerDirty"/>, <see cref="Rebake"/>, <see cref="GroundDiag"/>).</summary>
    private static float _appliedScaleBias;
    private static bool _samplerDirty = true;
    private static long _pushWalked, _pushWalkedAll;

    // period counters
    private static int _frames, _draws, _visible, _culled, _baked, _refused, _disabled;
    private static int _mipsBuilt;
    /// <summary>Records the scatter appended per command this period (off the persistent map), and the blade
    /// indices it was handed -- the difference is the per-blade cull.</summary>
    private static long _drawnNear, _drawnFar, _drawnCards;
    private static int _bladeNear, _bladeFar;
    private static long _candidates;
    private static int _scatters;
    /// <summary>Cap latches, cleared with the tables in <see cref="Forget"/> rather than per period: they
    /// describe the resident field, not the frame, and a cap that stops being reported reads as headroom
    /// that is not there.</summary>
    private static bool _capped, _patchCapped;
    private static int _thinned;
    private static int _samples = 1;
    /// <summary>The DEFAULT framebuffer's sample count, latched for the life of the context (0 = not yet
    /// read). It is the only multisampled surface the grass pass can land on, and it is fixed once the
    /// context exists -- so the query belongs here and not in the frame path (#710).</summary>
    private static int _fb0Samples;
    private static float _appliedAniso = 1f;
    private static bool _anyFlat;

    private struct Loc
    {
        public int Look, Segments, Width, Probe;
        public int Frame, Vp, Eye, Fade, Tex, MatAmb, MatDif, Lit, WindGain, WindSpeed, Msaa, Edge, Lod, Shade, Viz, Tuft, VLit, Cast, CastWidth, RootAo, GroundTint, Ground, GroundOn, GroundViz, SoftEdge, Snow;
        public int PushTex, PushGain, PushWindow;
        public int SunTex, SunDynTex, CubeTex, CubeCount, CubeDynTex;   // CX-43: the rest of the receiver rides CxPass
    }
    private static Loc _loc, _locD, _locB, _locBD;
    private static AlloyRenderer.FeatLoc _featLoc, _featLocD, _featLocB, _featLocBD;
    /// <summary>The scatter's uniforms.</summary>
    private struct ScatterLoc { public int Planes, Eye, Lod, Fade, SnowThin, Clamp, PushTex, PushWindow, Near, Clump, CardKeep, BladeCap; }
    private static ScatterLoc _locS;
    /// <summary>#355: the grass depth prepass (<see cref="Grass.Prepass"/>). With <c>discard</c> in the
    /// stage and depth writes on, the hardware cannot reject a hidden fragment before shading it, so every
    /// blade fragment behind another blade still took the full light and shadow taps. The prepass lays the
    /// field's depth down with an alpha-only stage first; the colour pass then tests LEQUAL with depth writes
    /// OFF, and early-Z discards the hidden fragments before the taps. Both stages run the same vertex text
    /// under <c>invariant gl_Position</c> and make the same keep decision, so the depths are equal.</summary>
    internal static void Rebake() => _rebake = true;
    internal static void SamplerDirty() => _samplerDirty = true;
    internal static void GroundDiag() => _groundDiag = 3;
    internal static void DumpMipsNext() => _dumpMips = true;
    internal static void DumpShadowNext() => _dumpShadow = true;

    // ---- install -----------------------------------------------------------------------------

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _binBucket = new EngineList(Native.Sym(SymbolMap.BinBucket));
        _grassInfo = new EngineList(Native.Sym(SymbolMap.GrassInfo));
        _grassEnabled = (byte*)Native.Sym(SymbolMap.GrassEnabled);
        // Not fatal: no gob list just means nothing displaces the grass (the pre-#127-followup look).
        _farDist = (float*)Native.Sym(SymbolMap.GrassFarDistance);
        _nearRadius = (float*)Native.Sym(SymbolMap.GrassNearRadius);
        _aurGet = EngineFn.AurTextureGet;
        // Not fatal: without it every gob counts as solid, which is the pre-fix behaviour (target reticles
        // and other effect gobs displace grass) rather than a misrender.
        _isOpaque = EngineFn.PartGetIsOpaque;
        if (_binBucket.IsNull || _grassInfo.IsNull || _grassEnabled == null || _farDist == null
            || _nearRadius == null || _aurGet == null || !AurTex.Ready)
        {
            _off = true;
            log.Info("GrassField: grass globals unresolved -- grass stays on the engine's own path");
            return;
        }

        Array.Fill(_slot, -1);
        BorderInstall(log);
        _origBin = hooks.InstallDoBinBucket(&OnDoBinBucket);
        _origReload = hooks.InstallReloadGrass(&OnReloadGrass);
        if (_origBin == null || _origReload == null)
        {
            _off = true;
            log.Info("GrassField: the grass hooks did not install -- grass stays vanilla");
            return;
        }
        log.Info($"GrassField: armed -- Alloy instanced grass, {Describe()} (/grass tunes it)");
    }

    /// <summary>The vanilla grass site. Skipped outright on a pass the Alloy grass already drew -- which
    /// retires the per-frame CPU wind rewrite over every bin in the area along with the draw, and that is
    /// most of what makes the replacement cheaper than the original. Any pass we did not draw (vanilla render
    /// mode, Alloy pass stood down, a GUI 3D panel's Scene, no program) falls straight through to the engine,
    /// so the fallback needs no separate switch.</summary>
    [UnmanagedCallersOnly]
    private static void OnDoBinBucket(IntPtr scene)
    {
        if (_origBin == null) return;
        if (AlloyRenderer.IsWorldScene(scene) && AlloyRenderer.DrewThisPass
            && _drawnGen == AlloyRenderer.PassGen) return;
        _origBin(scene);
    }

    /// <summary>Every bin and triangle list the cache holds is freed inside here (area load, a grass option
    /// change, a server grass override), so every captured identity is void the moment it returns. Forgetting
    /// AFTER the original is what keeps a recycled allocation from serving the previous area's blades.
    /// <para>This is the ONLY rebuild path, verified rather than assumed: the world scene is a
    /// <c>CAurScene</c>, whose vtable patches <c>ReloadAreaGrass</c> @0x465f50 into the area-reload slot, and
    /// that override's single virtual call is Scene vt+0x250 -- which the table at Ghidra 0x196c250 resolves
    /// to <c>Scene::ReloadGrass</c> @0x469540, i.e. straight back through this hook.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnReloadGrass(IntPtr scene)
    {
        if (_origReload == null) return;
        _origReload(scene);
        Forget();
    }

    /// <summary>Context loss: the GL names mean nothing once the context is gone, so forget rather than
    /// delete -- and forget the captured world with them, since a rebuilt context re-scatters anyway. Both
    /// EnsureProgram and EnsureBuffers short-circuit on a non-zero name, so leaving these set is what made
    /// the next context draw through a dead program (#175 follow-up).</summary>
    internal static void ContextLost()
    {
        _vao = 0; _faceBuf = 0; _patchBuf = 0; _matBuf = 0; _outBuf = 0; _cmdBuf = 0; _statBuf = 0; _stat = null;
        _prog = 0; _progDepth = 0; _progScatter = 0; _progBlade = 0; _progBladeDepth = 0;
        _sampMip = 0; _sampFlat = 0; _pushTex = 0;
        _fb0Samples = 0;
        _loc = default;
        _down.Forget();
        Forget();
    }

    internal static void Forget()
    {
        _patchCount = 0; _shadowN = 0;
        _faceCount = 0; _bladesResident = 0;
        _uploaded = 0;
        Array.Fill(_cls, default);
        _drawnGen = -1;
        _capped = false;
        _patchCapped = false;
        _refused = 0;
        Array.Fill(_slot, -1);
    }

    // ---- the pass ----------------------------------------------------------------------------

    /// <summary>The Alloy grass pass, at its <see cref="FrameGraph"/> slot inside the colour bracket.
    /// Bake-then-draw: newly visible patches are scattered and appended to the instance stream (a bounded
    /// number per frame), then every patch sighted this frame is culled, sorted and issued.</summary>
    private static bool Gate()
    {
        if (_off || !Grass.Enabled.V || _down.Failed || _binBucket.IsNull) return false;
        if (*_grassEnabled == 0) return false;
        if (Gl.DrawArraysIndirect == null || Gl.DispatchCompute == null
            || Gl.MemoryBarrier == null || Gl.BindBufferBase == null)
        { _down.Fail(_log, "no indirect-draw or compute entry point", "grass stays vanilla"); return false; }
        if (!EnsureProgram() || !EnsureBuffers()) return false;
        return Grass.Density.V > 0f;
    }

    /// <summary>Once per PassGen: bake, upload, push map. The seen count is kept for a second caller.</summary>
    private static int Prepare()
    {
        if (_prepGen == AlloyRenderer.PassGen) return _seen;
        _prepGen = AlloyRenderer.PassGen;
        if (_rebake) { _rebake = false; Forget(); }
        _frame++;
        _frames++;
        _seen = Collect();
        if (_seen == 0) return 0;
        Upload();
        BuildPush();
        return _seen;
    }

    internal static void Draw(Matrix4x4* vp, int* viewport, float near, float far, bool lightingOn)
    {
        // Toggle-off returns WITHOUT stamping the generation: the class goes back to the ENGINE, which is
        // the opposite of the density-0 branch below (nobody draws it, and the stamp is what keeps it that way).
        if (_off || !Grass.Enabled.V || _down.Failed || _binBucket.IsNull) return;
        if (*_grassEnabled == 0) return;      // the player's "Grass Mode" option: off means off, both paths
        if (Gl.DrawArraysIndirect == null || Gl.DispatchCompute == null
            || Gl.MemoryBarrier == null || Gl.BindBufferBase == null)
        { _down.Fail(_log, "no indirect-draw or compute entry point", "grass stays vanilla"); return; }
        if (!EnsureProgram() || !EnsureBuffers()) return;
        // Density 0 means the player asked for no grass. Stamp the generation and stop: the class is still
        // OWNED (so the engine does not draw it back), and Collect never runs -- otherwise every frame would
        // re-attempt a bake budget's worth of patches that scatter nothing.
        if (Grass.Density.V <= 0f) { _drawnGen = AlloyRenderer.PassGen; return; }

        int n = Prepare();
        if (n == 0) { _drawnGen = AlloyRenderer.PassGen; return; }
        Issue(n, vp, viewport, near, far, lightingOn);
        // Stamped even on a frame that drew nothing visible: the Alloy pass OWNS the class this pass, and a
        // patch culled by distance must not come back as an engine draw.
        _drawnGen = AlloyRenderer.PassGen;
    }

    /// <summary>Walk <c>binbucket</c> -- the BSP visibility walk's own active-bin list, rebuilt every frame --
    /// baking whatever is new and marking everything sighted. A bin that stops appearing has gone out of view
    /// or been freed; either way it simply stops being drawn, and no pointer of it is ever touched again.</summary>
    private static int Collect()
    {
        IntPtr* bins = (IntPtr*)_binBucket.Items;
        int binCount = _binBucket.Count;
        if (bins == null || binCount <= 0) return 0;
        int budget = BakePerFrame, seen = 0;
        for (int b = 0; b < binCount; b++)
        {
            IntPtr bin = bins[b];
            if (bin == IntPtr.Zero) continue;
            var tb = new CAurTriangleBin(bin);
            IntPtr* lists = (IntPtr*)tb.Lists;
            int listCount = tb.ListCount;
            if (lists == null || listCount <= 0) continue;
            for (int l = 0; l < listCount && seen < MaxPatches; l++)
            {
                IntPtr list = lists[l];
                if (list == IntPtr.Zero) continue;
                int p = Find(list);
                if (p < 0)
                {
                    if (budget <= 0) continue;
                    budget--;
                    p = Bake(list);
                    if (p < 0) continue;
                }
                _patches[p].Seen = _frame;
                seen++;
            }
        }
        seen += CollectBorder(ref budget);
        return seen;
    }

    /// <summary>Interned slot of a triangle list, or -1. Open addressing on the pointer; the table is sized
    /// at 2x the patch cap so a probe never walks far.</summary>
    private static int Find(IntPtr list)
    {
        uint h = Hash((uint)((long)list >> 4) ^ (uint)((long)list >> 36)) & (PatchHash - 1);
        for (int probe = 0; probe < PatchHash; probe++)
        {
            int s = _slot[(h + probe) & (PatchHash - 1)];
            if (s < 0) return -1;
            if (_patches[s].List == list) return s;
        }
        return -1;
    }

    private static void Intern(IntPtr list, int p)
    {
        uint h = Hash((uint)((long)list >> 4) ^ (uint)((long)list >> 36)) & (PatchHash - 1);
        for (int probe = 0; probe < PatchHash; probe++)
        {
            int i = (int)((h + probe) & (PatchHash - 1));
            if (_slot[i] < 0) { _slot[i] = p; return; }
        }
    }

    /// <summary>Intern one triangle list as a patch of resident FACES (CX-24): the face vertices and the
    /// per-face blade counts are the ENGINE's -- the tileset's authored density ratios survive, and the client
    /// dial scales them. No blade is made here: the scatter (<c>cx_gblade_c.glsl</c>) regenerates each from its
    /// face's seed every frame. Deterministic: the seed is a hash of the face's own vertices, so a rebuild puts
    /// every blade back where it was rather than reshuffling the meadow.
    /// <para>A list this cannot scatter is interned as an EMPTY patch rather than left unknown. A refusal is
    /// a stable property of the list (a material index past the live grass-info count, no faces, no height,
    /// the face cap), so an un-interned one would be retried every frame forever -- and 24 of them ahead
    /// of the good lists in <c>binbucket</c> order would eat the whole per-frame bake budget and leave the
    /// area with no grass at all, reported as a healthy "0 patches". <see cref="Stats"/> counts them.</para></summary>
    private static int Bake(IntPtr list)
    {
        // The one refusal that CANNOT be interned: there is no slot to intern it in. Counted instead, and
        // loud in the period line -- past this point every list retries, which is the state to see.
        if (_patchCount >= MaxPatches || _faces == null) { _patchCapped = true; return -1; }
        int p = _patchCount++;
        _patches[p] = new Patch { List = list, Seen = _frame };   // Count 0: refused until proven otherwise
        Intern(list, p);
        var tl = new CAurTriangleList(list);
        float* verts = (float*)tl.Verts;
        int floats = tl.VertFloats;
        int* blades = (int*)tl.Blades;
        int bladeFaces = tl.BladeCount;
        int mat = tl.Mat;
        int faces = floats / 9;
        if (verts == null || blades == null || faces <= 0 || bladeFaces < faces) { _refused++; return p; }
        CAurGrassInfo info = GrassInfo(mat);
        if (info.IsNull || mat >= MaxGrassMaterials) { _refused++; return p; }
        float height = info.Height;
        if (!(height > 0f)) { _refused++; return p; }

        // procedural blades are thin: they take the blade multiplier, and the scatter thins a card cell back out
        float per = Grass.Density.V * BladeMul;
        long want = 0;
        for (int f = 0; f < faces; f++) want += Math.Min((int)(blades[f] * per), MaxBladesPerFace);
        // a patch past the cap keeps its shape at a lower density
        float thin = want > MaxPatchBlades ? (float)MaxPatchBlades / want : 1f;
        if (thin < 1f) _thinned++;
        int first = _faceCount, total = 0;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int f = 0; f < faces; f++)
        {
            int n = (int)(Math.Min((int)(blades[f] * per), MaxBladesPerFace) * thin);
            if (n <= 0) continue;
            if (_faceCount >= MaxFaces) { _capped = true; break; }
            float* v = verts + f * 9;
            Vector3 a = new(v[0], v[1], v[2]), b = new(v[3], v[4], v[5]), c = new(v[6], v[7], v[8]);
            uint seed = Hash(BitConverter.SingleToUInt32Bits(a.X)
                           ^ (BitConverter.SingleToUInt32Bits(b.Y) * 2654435761u)
                           ^ (BitConverter.SingleToUInt32Bits(c.Z) * 40503u));
            int o = _faceCount * FaceFloats;
            _faces[o] = a.X; _faces[o + 1] = a.Y; _faces[o + 2] = a.Z; _faces[o + 3] = BitConverter.Int32BitsToSingle(total);
            _faces[o + 4] = b.X; _faces[o + 5] = b.Y; _faces[o + 6] = b.Z; _faces[o + 7] = BitConverter.UInt32BitsToSingle(seed);
            _faces[o + 8] = c.X; _faces[o + 9] = c.Y; _faces[o + 10] = c.Z; _faces[o + 11] = BitConverter.Int32BitsToSingle(n);
            _faceCount++;
            total += n;
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }
        if (total == 0) { _refused++; return p; }
        // Padded by what a blade occupies around its root: its lean and far widening each way, its full
        // height (and the ladder's growth) up.
        min -= new Vector3(height, height, 0f);
        max += new Vector3(height, height, height * 1.5f);

        _patches[p] = new Patch { List = list, Min = min, Max = max, Mat = mat, First = first, Count = _faceCount - first,
                                  Blades = total, Seen = _frame };
        _bladesResident += total;
        _baked++;
        return p;
    }

    private static CAurGrassInfo GrassInfo(int mat)
    {
        var recs = new CAurGrassInfo(_grassInfo.Items);
        if (recs.IsNull || mat < 0 || mat >= _grassInfo.Count) return default;
        return recs[mat];
    }

    /// <summary>Push whatever the bake appended. One <c>BufferSubData</c> over the new tail, never the whole
    /// stream -- the resident faces do not change once interned.</summary>
    private static void Upload()
    {
        if (_faces == null || _faceCount <= _uploaded) return;
        int floats = (_faceCount - _uploaded) * FaceFloats;
        // The first write after a Forget (area load, /grass density) starts at byte 0, over a range the GPU
        // may still be reading from last frame -- orphan it rather than make the driver serialise, at exactly
        // the two moments this class exists to keep smooth. Every later write is an append past the read range.
        if (_uploaded == 0)
            Gl.NamedBufferData(_faceBuf, (IntPtr)((long)MaxFaces * FaceFloats * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        fixed (float* src = &_faces[_uploaded * FaceFloats])
            Gl.NamedBufferSubData(_faceBuf, (IntPtr)(_uploaded * FaceFloats * sizeof(float)),
                                  (IntPtr)(floats * sizeof(float)), src);
        _uploaded = _faceCount;
    }

    /// <summary>Rebuild the displacement field from every visible gob and upload it.
    /// <para>Source is <c>gobbucket</c> -- the engine's own per-frame in-view gob list, which is exactly the
    /// creatures, placeables, doors and items standing in the grass. Each becomes a radial push away from its
    /// centre, summed and clamped, so a creature parts the grass it walks through and anything resting on the
    /// field flattens a dent in it.</para>
    /// <para>ponytail: no persistence, so the field springs back the instant a creature leaves rather than
    /// laying a trail. A trail wants the previous frame's field decayed and REPROJECTED as the window
    /// follows the player, which is a scroll-and-blend the no-history version does not need at all. Add it
    /// here if the springback ever reads too eager.</para>
    /// <para>ponytail: one radius for every gob, not a per-gob bound. A Gob carries no bounding sphere we
    /// have mapped (only its position at +0xa8), and a creature-sized dent under everything is much closer
    /// to right than no dent at all. Scale by the part tree's bbox if a cart ever wants a cart-sized
    /// hole.</para></summary>
    private static void BuildPush()
    {
        if (_pushTex == 0 || _push == null) return;
        _pushers = 0; _cullers = 0; _reticles = 0;
        // Neutral is 128,128 for the direction and 0 for the flatten -- not one byte value, so it comes from
        // a template rather than an Array.Fill.
        Array.Copy(_pushClear!, _push, _push.Length);
        float extent = MathF.Max(Grass.PushExtent.V, 1f);
        _pushPerUnit = PushRes / extent;
        Vector3 centre = AlloyRenderer.PlayerPos;
        // Snapped to the texel grid: an origin that slides sub-texel makes every blade's sampled push
        // shimmer as the player walks, which reads as the grass twitching for no reason.
        float texel = 1f / _pushPerUnit;
        _pushOriginX = MathF.Floor(centre.X / texel) * texel - extent * 0.5f;
        _pushOriginY = MathF.Floor(centre.Y / texel) * texel - extent * 0.5f;

        // #282: the AREA's gobs, not the frame's `gobbucket`. That bucket holds what the engine's camera cull
        // left, and until #282 it ALSO held whatever the shadow feed appended to it -- off-screen movers within
        // 25u of the player, which is exactly the population that dents grass the camera is not facing. Reading
        // the bucket made this pass's behaviour a side effect of somebody else's feature; reading Scene+0x130
        // makes it a fact about the area, and a creature just off-camera bends the grass it is standing in
        // whether or not anything meant to draw it. Scene+0x130 is the same list SceneWalk enumerates.
        IntPtr scene = SceneWalk.WorldScene;
        if (Grass.Push.V > 0f && Grass.PushRadius.V > 0f && scene != IntPtr.Zero)
        {
            // The pushers are the gobs in view: a walk of EVERY gob in the scene (thousands, a part-tree
            // walk for each inside the extent) read 0,5ms a frame with no grass on screen. The engine's own
            // visible list is a few hundred; a pusher behind the camera pushes grass nobody sees.
            IntPtr* gobs; int count;
            if (!Grass.PushVis.V || !AlloyRenderer.VisibleGobs(out gobs, out count))
            {
                gobs = (IntPtr*)new CX.Engine.Scene(scene).Gobs;
                count = new CX.Engine.Scene(scene).GobCount;
                _pushWalkedAll++;
            }
            _pushWalked += count;
            float r = Grass.PushRadius.V, rTex = r * _pushPerUnit;
            // ponytail: no cheap pre-reject before Footprint. `reach` depends on the footprint, and with
            // `cullmax` at its default 0 (uncapped) there is no sound bound to test against first -- a
            // guess that is too tight drops a cart-sized hole. So the whole area's gobs pay one bounded
            // pointer chase each, a few thousand derefs a frame with no allocation. Cap `/grass cullmax`
            // and hoist the window test above Footprint if that ever shows up in a profile.
            for (int i = 0; gobs != null && i < count; i++)
            {
                IntPtr gob = gobs[i];
                if (gob == IntPtr.Zero) continue;
                // Same guard the shadow feed used: a gob with no part tree has no geometry to stand in the
                // grass with, so it must not dent it.
                var gr = new GobRef(gob);
                if (gr.PartTree == IntPtr.Zero) continue;   // no geometry to stand in the grass with
                float* pos = gr.Position;
                float gx = (pos[0] - _pushOriginX) * _pushPerUnit, gy = (pos[1] - _pushOriginY) * _pushPerUnit;
                // The cull footprint can be much larger than the push radius, so the window test uses
                // whichever of the two actually reaches.
                float hx = 0f, hy = 0f;
                bool cull = Grass.Cull.V && Footprint(gob, ref hx, ref hy);
                float reach = MathF.Max(rTex, MathF.Max(hx, hy) * _pushPerUnit);
                if (gx < -reach || gy < -reach || gx > PushRes + reach || gy > PushRes + reach) continue;
                // A gob with no OPAQUE geometry is not standing in the grass, it is drawn over it: target
                // reticles, spell VFX, the ground-target decal. They neither bend nor cull.
                if (!HasOpaque(gr.PartTree, 0)) { _reticles++; continue; }
                _pushers++;
                Splat(_push, gx, gy, rTex);
                if (cull)
                {
                    _cullers++;
                    CullRect(_push, gx, gy, hx * _pushPerUnit, hy * _pushPerUnit);
                }
            }
        }

        fixed (byte* p = _push)
            Gl.TextureSubImage2D(_pushTex, 0, 0, 0, PushRes, PushRes, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
    }

    /// <summary>One gob's radial push, summed into the field in texel space. Linear falloff outward from the
    /// centre; at the centre itself there is no direction to push in and 0 is the honest answer -- a blade
    /// under the middle of a creature is flattened by the Z term, not flung sideways.</summary>
    internal static void Splat(byte[] field, float gx, float gy, float rTex)
    {
        if (rTex <= 0f) return;
        int lo = (int)(rTex + 1f);
        int x0 = Math.Max((int)gx - lo, 0), x1 = Math.Min((int)gx + lo, PushRes - 1);
        int y0 = Math.Max((int)gy - lo, 0), y1 = Math.Min((int)gy + lo, PushRes - 1);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float dx = x + 0.5f - gx, dy = y + 0.5f - gy;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d >= rTex) continue;
            // Smoothstep, not a linear cone: a linear falloff still has a first-derivative step at the rim,
            // and over a field of blades that reads as a visible ring around every creature.
            float t = 1f - d / rTex;
            float m = t * t * (3f - 2f * t);
            // The lateral push SUMS between overlapping gobs (two creatures either side squeeze the grass
            // between them, which is right) ...
            float inv = d > 1e-3f ? m / d : 0f;
            int o = (y * PushRes + x) * PushChannels;
            field[o] = Sat(field[o] + dx * inv * 127f);
            field[o + 1] = Sat(field[o + 1] + dy * inv * 127f);
            // ...while the flatten takes the MAX: grass under two overlapping things is flat, not twice flat.
            byte f = Sat(m * 255f);
            if (f > field[o + 2]) field[o + 2] = f;
        }
    }

    /// <summary>Clamp to a byte, ROUNDING rather than truncating: truncation biases every push toward zero
    /// and makes the two sides of a gob differ by a step, which is a visible asymmetry in the parting.</summary>
    /// <summary>The gob's grass-suppressing footprint as XY half-extents, or false for a gob that must not
    /// suppress. Creatures are excluded by name: they bend grass, they do not erase it, and an erasing
    /// creature would drag a bald patch around with it.
    /// <para>Only the bounding box's EXTENT is used, centred on the gob's world position. The pair at
    /// <c>gob+0x160</c> is a stored field and its space (model or world) is not established -- the extent is
    /// the same in either, and the position is unambiguous.</para>
    /// <para>ponytail: one box for the whole gob. That is right for a chest, a crate or a wall, and wrong for
    /// a TREE, whose box is its canopy while only its trunk touches the ground -- so a tree erases grass to
    /// its drip line. <c>/grass cullmax</c> caps it; the real fix is a per-PART footprint filtered to the
    /// parts that reach the ground, which is the same bbox data one level down.</para></summary>
    private static bool Footprint(IntPtr gob, ref float hx, ref float hy)
    {
        // A creature is marked by Gob::SetAsCharacter @0x444790, which writes node+0x8e = 4. This is exactly
        // as strong as the engine's own writer: nothing else found writes that byte.
        var g = new CX.Engine.Gob(gob);
        if (!g.Node.IsNull
            && g.Node.OwnerFlags == NodeCharacterValue) return false;
        Vector3 mn = g.BoundsMin, mx = g.BoundsMax;
        hx = (mx.X - mn.X) * 0.5f * Grass.CullShrink.V;
        hy = (mx.Y - mn.Y) * 0.5f * Grass.CullShrink.V;
        // A degenerate or absurd box is a field that was never filled, not a very large object.
        if (!(hx > 0.01f) || !(hy > 0.01f) || hx > 500f || hy > 500f) return false;
        if (Grass.CullMax.V > 0f) { hx = MathF.Min(hx, Grass.CullMax.V); hy = MathF.Min(hy, Grass.CullMax.V); }
        return true;
    }

    /// <summary>Does this part tree contain any OPAQUE geometry? The walk is <c>Gob::ContainsNonSolids</c>
    /// @0x43f250's, inverted: its vtable+0x60 trimesh accessor, its <c>+0x96</c> liveness gate, its
    /// <c>PartTriMesh::GetIsOpaque</c>, and its child array at +0x38/+0x40.
    /// <para>Inverted rather than reused because "contains a non-solid" is the wrong question here: a tree
    /// has alpha-cutout leaves AND an opaque trunk, and it very much stands in the grass. What separates a
    /// chest from a target reticle is whether anything about it is opaque at all.</para></summary>
    private static bool HasOpaque(IntPtr part, int depth)
    {
        if (part == IntPtr.Zero || depth > MaxPartDepth || _isOpaque == null) return false;
        var pv = new CX.Engine.Part(part);
        if (*(IntPtr*)part != IntPtr.Zero && pv.AsPartTriMeshFn != IntPtr.Zero)
        {
            PartTriMesh tri = pv.AsPartTriMesh();
            if (!tri.IsNull && tri.RenderFlag != 0 && _isOpaque(tri.Ptr) != 0)
                return true;
        }
        IntPtr* kids = (IntPtr*)pv.Children;
        int n = pv.ChildCount;
        if (kids == null || n <= 0 || n > MaxPartChildren) return false;
        for (int i = 0; i < n; i++) if (HasOpaque(kids[i], depth + 1)) return true;
        return false;
    }

    /// <summary>Stamp an object's footprint into the cull channel: a soft-edged rectangle, because a hard one
    /// ends the grass in a visible straight line no object actually has. The edge softness is what the vertex
    /// stage turns into thinning rather than a cut, using each blade's own stable jitter as its threshold.</summary>
    internal static void CullRect(byte[] field, float gx, float gy, float hx, float hy)
    {
        if (hx <= 0f || hy <= 0f) return;
        int x0 = Math.Max((int)(gx - hx), 0), x1 = Math.Min((int)(gx + hx) + 1, PushRes - 1);
        int y0 = Math.Max((int)(gy - hy), 0), y1 = Math.Min((int)(gy + hy) + 1, PushRes - 1);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            // Normalised distance to the rectangle's edge along whichever axis is closer to it.
            float ex = MathF.Abs(x + 0.5f - gx) / hx, ey = MathF.Abs(y + 0.5f - gy) / hy;
            float e = MathF.Max(ex, ey);
            if (e >= 1f) continue;
            float t = 1f - e;
            byte v = Sat(MathF.Min(t * 3f, 1f) * 255f);
            int o = (y * PushRes + x) * PushChannels;
            if (v > field[o + 3]) field[o + 3] = v;
        }
    }

    private static byte Sat(float v) => (byte)MathF.Round(v < 0f ? 0f : v > 255f ? 255f : v);

    /// <summary>Samples of the surface this pass draws into, without a per-frame <c>glGet</c> (#710).
    /// Every target Alloy binds -- the post chain's HDR target, the present texture -- is built from plain
    /// 2D attachments, so only framebuffer 0 can be multisampled, and its count cannot change under a live
    /// context.</summary>
    private static int TargetSamples()
    {
        if (PostChain.Armed || PresentTarget.Fbo46 != 0) return 1;
        if (_fb0Samples == 0)
        {
            int s = 0;
            Gl.GetIntegerv(Gl.SAMPLES, &s);
            _fb0Samples = s < 1 ? 1 : s;
        }
        return _fb0Samples;
    }

    /// <summary>Cull the patches, scatter their blades on the GPU, and draw what survived (CX-24): procedural
    /// blades in two indirect strips (near full-detail, far one triangle), and one indirect draw of the
    /// engine's own alpha-cut cards per material that has card cells -- every cell under
    /// <see cref="Grass.StyleTextures"/>. The CPU's share is the patch cull and a table of a few thousand
    /// entries; the blade count never reaches it.</summary>
    private static void Issue(int seen, Matrix4x4* vp, int* viewport, float near, float far, bool lightingOn)
    {
        float* planes = stackalloc float[24];
        Frustum(vp, planes);
        Vector3 eye = AlloyRenderer.EyePos;
        float farDist = FarDist();
        // #355: the dither ramp is the CUT-OFF's edge, not the whole range -- the ladder thins the field by
        // count from the lod radius outward, and a blade the ramp would have dithered is simply not drawn instead.
        float nearDist = farDist * 0.85f;
        if (farDist <= nearDist) farDist = nearDist + 1f;
        // A patch wholly past the fog's opaque distance is a fully-fogged pixel the ground already is; the
        // engine's grass option runs to 173u and the fog end is commonly 125u, so it was never cut.
        float opaque = CullPass.FogCull ? SceneFog.OpaqueDistance() : float.PositiveInfinity;
        Vector3 fogOrigin = SceneFog.Origin(out _);

        // Every grass texture resolved BEFORE anything else: AurTextureGet's create path (first sight of a
        // resref) can touch GL and rebind buffers, and it must not do that between our binds. Re-fetched
        // every frame and never cached (#33) -- the engine frees its textures on area transitions, and a
        // stored CAurTexture* dangles into freed heap the moment it is bound.
        // The IN-USE byte comes off the same walk. It is the engine's own AurGrassMaterialInUse @0x4b4730,
        // and it is the ONLY gate on a grass material being drawn: RemoveGrassOverride (server minor 0x16)
        // clears it and forces a reload, but a reload rebuilds the bins for that material anyway -- the
        // engine's DoBinBucket simply refuses to draw them. Ignoring it renders grass a server just removed.
        int* tids = stackalloc int[MaxGrassMaterials];
        bool* live = stackalloc bool[MaxGrassMaterials];
        ResolveTextures(tids, live);
        if (_dumpMips) { _dumpMips = false; DumpMips(tids, live); }
        EnsureSamplers();
        int samples = TargetSamples();
        _samples = samples;
        bool msaa = samples > 1;
        int* bits = stackalloc int[MaxGrassMaterials];
        ClassifyAll(tids, live, bits);

        // each draw's upper bound this frame: the blades its visible patches can keep at their nearest point.
        // A patch straddling the blade detail radius counts toward both blade draws.
        long* bound = stackalloc long[MaxGrassMaterials];
        for (int m = 0; m < MaxGrassMaterials; m++) bound[m] = 0;
        long nearB = 0, farB = 0, most = 0;
        float bladeLod = Grass.BladeLod.V;
        int n = 0;
        for (int p = 0; p < _patchCount; p++)
        {
            ref Patch q = ref _patches[p];
            if (q.Seen != _frame || q.Count <= 0) continue;
            if (q.Mat < 0 || q.Mat >= MaxGrassMaterials || !live[q.Mat]) { _disabled++; continue; }
            float d = BoxDistance(q.Min, q.Max, eye);
            if (d > farDist || BoxDistance(q.Min, q.Max, fogOrigin) > opaque || !Visible(q.Min, q.Max, planes)) { _culled++; continue; }
            // keep(NEAREST point of the patch), so it is never smaller than the keep any blade of the patch
            // computes for itself: the scatter rejects on it before the blade's own test closes the gap
            float keep = Keep(d);
            int o = n * 8;
            _visTable[o] = (uint)q.First; _visTable[o + 1] = (uint)q.Count; _visTable[o + 2] = (uint)q.Blades;
            _visTable[o + 3] = BitConverter.SingleToUInt32Bits(keep); _visTable[o + 4] = (uint)q.Mat;
            _visTable[o + 5] = 0; _visTable[o + 6] = 0; _visTable[o + 7] = 0;
            long upto = (long)MathF.Ceiling(q.Blades * MathF.Min(keep * 1.2f, 1f));
            if (bits[q.Mat] != 0)
            {
                if (d < bladeLod) nearB += upto;
                if (BoxFar(q.Min, q.Max, eye) >= bladeLod) farB += upto;
            }
            if (CardCells(q.Mat, bits[q.Mat])) bound[q.Mat] += upto;
            _candidates += upto;
            most = Math.Max(most, q.Blades);
            n++;
        }
        if (n == 0) return;
        _visible += n;
        _shadowN = n; _shadowEye = eye; _shadowNear = nearDist; _shadowFar = farDist;
        int matN = WriteMaterials(live, bound, bits, nearB, farB);

        float* pw = stackalloc float[3] { _pushOriginX, _pushOriginY, _pushPerUnit / PushRes };
        AlloyRenderer.GpuBegin(AlloyRenderer.GpuTerm.GrassDepth);
        Scatter(n, (int)Math.Clamp((most + SplitBlades - 1) / SplitBlades, 1, MaxSplit), planes, &eye, nearDist, farDist, matN, pw);
        Gl.BindVertexArray(_vao);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _cmdBuf);
        bool prepass = _slotCount > 0 && Grass.Prepass.V && _progDepth != 0;
        bool soft = Grass.SoftEdge.V && prepass && !msaa;
        if (prepass)
        {
            Gl.UseProgram(_progDepth);
            Gl.Uniform1i(_locD.SoftEdge, soft ? 1 : 0);
            SharedUniforms(in _locD, in _featLocD, vp, &eye, nearDist, farDist, msaa, pw, viewport, vlit: false);
            new PassState(depth: true, depthWrite: true, cull: false).Apply();
            Gl.ColorMask(0, 0, 0, 0);
            Gl.DepthFunc(Gl.LESS);
            if (msaa) Gl.Enable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
            DrawSlots(tids, in _locD, depth: true);
            if (msaa) Gl.Disable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
            Gl.ColorMask(1, 1, 1, 1);
            Gl.DepthFunc(Gl.LEQUAL);
        }
        AlloyRenderer.GpuEnd(AlloyRenderer.GpuTerm.GrassDepth);

        // The frame as it stands before the grass, for the root blend and the blades' fade into the ground.
        // One colour-only blit (the shader reads no depth); the water pass takes its own, with depth, later.
        bool blades = _bladeNear + _bladeFar > 0;
        bool ground = (Grass.GTint.V > 0f || blades) && FrameSnapshot.Copy(viewport, depth: false);
        if (ground && _groundDiag > 0) GroundDiagBefore(viewport);
        if (lightingOn) AlloyLights.BindForDraw();
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.GrassGround);
        Gl.BindTexture(Gl.TEXTURE_2D, FrameSnapshot.ColorTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
        // CX-51: a blade reflects the screen and the probe as the ground around it does
        Ssr.Bind();
        AlloyRenderer.BindProbe();
        AlloyRenderer.GpuBegin(AlloyRenderer.GpuTerm.Grass);
        // Procedural blades: opaque, depth-writing, two-sided, and no discard -- early-Z rejects the field's own
        // overdraw with no prepass.
        if (blades)
        {
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindMats, _matBuf);
            ColourUniforms(_progBlade, in _locB, in _featLocB, vp, &eye, nearDist, farDist, pw, viewport, near, far, lightingOn, ground);
            new PassState(depth: true, depthWrite: true, cull: false).Apply();
            DrawBlades(in _locB);
        }
        if (_slotCount > 0)
        {
            ColourUniforms(_prog, in _loc, in _featLoc, vp, &eye, nearDist, farDist, pw, viewport, near, far, lightingOn, ground);
            Gl.Uniform1i(_loc.Msaa, msaa ? 1 : 0);
            Gl.Uniform1i(_loc.SoftEdge, soft ? 1 : 0);
            Gl.Uniform1i(_loc.GroundViz, Grass.GViz.V);
            // The class's whole point, in one declaration: blending OFF (or the soft fringe alone), the coverage
            // decision made by the fragment stage's discards, depth from the prepass when it ran. Alpha-to-coverage
            // only where the target has samples to cover. #292: cull OFF, a card is seen from both faces.
            new PassState(depth: true, depthWrite: !prepass, cull: false, blend: soft,
                          blendSrc: Gl.SRC_ALPHA, blendDst: Gl.ONE_MINUS_SRC_ALPHA).Apply();
            if (msaa) Gl.Enable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
            DrawSlots(tids, in _loc, depth: false);
            if (msaa) Gl.Disable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        }
        AlloyRenderer.GpuEnd(AlloyRenderer.GpuTerm.Grass);
        if (ground && _groundDiag > 0) GroundDiagAfter(viewport);
        // the drawn count, for Stats: copied GPU-side into a persistent map, read a frame or more late, never waited on
        if (_stat != null)
        {
            _drawnNear += _stat[1]; _drawnFar += _stat[5];
            for (int s = 0; s < _slotCount; s++) _drawnCards += _stat[(2 + s) * 4 + 1];
            Gl.CopyNamedBufferSubData(_cmdBuf, _statBuf, IntPtr.Zero, IntPtr.Zero, (IntPtr)(Cmds * 4 * sizeof(uint)));
        }
        if (_sampMip != 0) Gl.BindSampler(0, 0);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, 0);
        Gl.BindVertexArray(0);
    }

    /// <summary>Every live grass material's texture name, for this frame only (#33: never cached).</summary>
    private static void ResolveTextures(int* tids, bool* live)
    {
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            CAurGrassInfo info = GrassInfo(m);
            live[m] = !info.IsNull && info.InUse != 0;
            IntPtr tex = !live[m] ? IntPtr.Zero
                : _aurGet((byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref info.Name), IntPtr.Zero, null, null, 0, 0);
            tids[m] = OwnedTextures.Draw(AurTex.Tid(tex));
        }
    }

    /// <summary>The procedural blade multiplier the bake applies: 1 under the texture style.</summary>
    private static float BladeMul => Grass.Style.V == Grass.StyleBlades ? Grass.BladeDensity.V : 1f;

    /// <summary>Does material <paramref name="m"/> draw any card? Any drawn cell (cell 0 is the engine's
    /// fallback) that is not a BLADE cell.</summary>
    private static bool CardCells(int m, int bits)
    {
        CAurGrassInfo info = GrassInfo(m);
        if (info.IsNull) return false;
        Span<float> prob = info.Prob;
        int used = 1;
        for (int k = 0; k < 4; k++) if (prob[k] > 0f) used |= 1 << k;
        return (used & ~bits) != 0;
    }

    /// <summary>Lay the frame's regions out -- near blades, far blades, then a card slot per material with card
    /// cells and visible blades, each sized to its upper bound -- and fill the material table. Returns one
    /// past the highest material written.</summary>
    private static int WriteMaterials(bool* live, long* bound, int* bits, long nearB, long farB)
    {
        long used = 0;
        bool clipped = false;
        long Take(long want) { long c = Math.Min(want, RecordCap - used); clipped |= c < want; used += c; return c; }
        _bladeNear = (int)Take(nearB);
        _bladeFar = (int)Take(farB);
        _slotCount = 0;
        int top = 0;
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            int o = m * MatVec4 * 4;
            _matTable[o + 5] = -1f;
            CAurGrassInfo info = live[m] ? GrassInfo(m) : default;
            if (info.IsNull) continue;
            top = m + 1;
            Span<float> prob = info.Prob;
            for (int k = 0; k < 4; k++) _matTable[o + k] = prob[k];
            _matTable[o + 4] = info.Height;
            _matTable[o + 7] = bits[m];
            Vector3 dif = info.Diffuse, amb = info.Ambient;
            _matTable[o + 8] = dif.X; _matTable[o + 9] = dif.Y; _matTable[o + 10] = dif.Z;
            _matTable[o + 12] = amb.X; _matTable[o + 13] = amb.Y; _matTable[o + 14] = amb.Z;
            for (int r = 0; r < 12; r++)
            {
                Vector3 c = _ramp[m * 12 + r];
                int q = o + 16 + r * 4;
                _matTable[q] = c.X; _matTable[q + 1] = c.Y; _matTable[q + 2] = c.Z;
            }
            if (bound[m] <= 0) continue;
            if (_slotCount >= Slots) { _slotsOver++; continue; }
            long cap = Take(bound[m]);
            if (cap <= 0) continue;
            int slot = _slotCount++;
            _slotMat[slot] = m;
            _slotBase[slot] = (int)(used - cap);
            _matTable[o + 5] = slot;
            _matTable[o + 6] = cap;
        }
        if (clipped) _clipped++;
        return top;
    }

    /// <summary>Upload the frame's tables, reset the commands, and run the scatter: one workgroup per visible
    /// patch (times <paramref name="split"/> for the biggest), then the clamp. The barrier covers every way its
    /// output is read: as commands, as instance attributes, and by the stat copy.</summary>
    private static void Scatter(int n, int split, float* planes, Vector3* eye, float fadeFrom, float farDist, int matN, float* pw)
    {
        fixed (uint* t = _visTable)
        {
            Gl.NamedBufferData(_patchBuf, (IntPtr)(n * 8 * sizeof(uint)), t, Gl.DYNAMIC_DRAW);
        }
        fixed (float* m = _matTable)
        {
            Gl.NamedBufferData(_matBuf, (IntPtr)(Math.Max(matN, 1) * MatVec4 * 4 * sizeof(float)), m, Gl.DYNAMIC_DRAW);
        }
        uint* cmd = stackalloc uint[Cmds * 4];
        for (int c = 0; c < Cmds; c++)
        {
            cmd[c * 4] = (uint)(c == 0 ? 2 * NearSegments + 1 : c == 1 ? 2 * FarSegments + 1 : TemplateVerts);
            cmd[c * 4 + 1] = 0; cmd[c * 4 + 2] = 0;
            cmd[c * 4 + 3] = (uint)(c == 0 ? 0 : c == 1 ? _bladeNear : c - 2 < _slotCount ? _slotBase[c - 2] : 0);
        }
        Gl.NamedBufferSubData(_cmdBuf, IntPtr.Zero, (IntPtr)(Cmds * 4 * sizeof(uint)), cmd);

        Gl.UseProgram(_progScatter);
        Gl.Uniform4fv(_locS.Planes, 6, planes);
        Gl.Uniform3fv(_locS.Eye, 1, (float*)eye);
        Gl.Uniform4f(_locS.Lod, Grass.Lod.V, Grass.LodMin.V, Grass.LodWidth.V, Grass.LodHeight.V);
        Gl.Uniform2f(_locS.Fade, fadeFrom, farDist);
        Gl.Uniform1f(_locS.SnowThin, SnowThinShare(Weather.Snow, Weather.CurrentKind == Weather.Kind.Snow ? Weather.Density : 0f));
        Gl.Uniform1f(_locS.Near, Grass.BladeLod.V);
        Gl.Uniform2f(_locS.Clump, ClumpCell, Grass.Clump.V);
        Gl.Uniform1f(_locS.CardKeep, 1f / MathF.Max(BladeMul, 1f));
        Gl.Uniform2i(_locS.BladeCap, _bladeNear, _bladeFar);
        Gl.Uniform1i(_locS.Clamp, 0);
        Gl.Uniform1i(_locS.PushTex, (int)PushUnit);
        Gl.Uniform3fv(_locS.PushWindow, 1, pw);
        Gl.ActiveTexture(Gl.TEXTURE0 + PushUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _pushTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindFaces, _faceBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindPatches, _patchBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindMats, _matBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindOut, _outBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindCmd, _cmdBuf);
        Gl.DispatchCompute((uint)n, (uint)split, 1);
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT);
        Gl.Uniform1i(_locS.Clamp, Math.Max(matN, 1));
        Gl.DispatchCompute(1, 1, 1);
        Gl.MemoryBarrier(Gl.COMMAND_BARRIER_BIT | Gl.VERTEX_ATTRIB_ARRAY_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        _scatters++;
    }

    /// <summary>World size of a clump cell: procedural blades inside one share a lean and a height.</summary>
    private const float ClumpCell = 0.6f;

    /// <summary>Distance from a point to the FARTHEST point of the box.</summary>
    internal static float BoxFar(Vector3 min, Vector3 max, Vector3 p)
    {
        float dx = MathF.Max(p.X - min.X, max.X - p.X), dy = MathF.Max(p.Y - min.Y, max.Y - p.Y), dz = MathF.Max(p.Z - min.Z, max.Z - p.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary><c>GL_DRAW_INDIRECT_BUFFER_BINDING</c>.</summary>
    private const uint DrawIndirectBinding = 0x8F43;   // not-an-offset: GL enum

    /// <summary>Everything a colour program reads beyond the shared terms: the light rig, the sun cascades,
    /// the local cubes, the ground copy. Leaves <paramref name="prog"/> bound.</summary>
    private static void ColourUniforms(uint prog, in Loc loc, in AlloyRenderer.FeatLoc feat, Matrix4x4* vp, Vector3* eye,
                                       float nearDist, float farDist, float* pw, int* viewport, float near, float far,
                                       bool lightingOn, bool ground)
    {
        Gl.UseProgram(prog);
        SharedUniforms(in loc, in feat, vp, eye, nearDist, farDist, false, pw, viewport, vlit: Grass.VLight.V && lightingOn);
        Gl.Uniform1f(loc.Shade, Grass.Shade.V);
        Gl.Uniform1i(loc.Viz, AlloyRenderer._lightViz);
        Gl.Uniform1i(loc.Lit, lightingOn ? 1 : 0);
        Gl.Uniform1f(loc.RootAo, Grass.RootAo.V);
        Gl.Uniform1f(loc.GroundTint, Grass.GTint.V);
        Gl.Uniform1i(loc.GroundOn, ground ? 1 : 0);
        Gl.Uniform4f(loc.Look, Grass.Backlight.V, Grass.Sheen.V, Weather.WetShown, Weather.WetDarkening);
        AlloyRenderer.BindPass(loc.CubeCount, viewport, near, far);
    }

    private static void GroundDiagBefore(int* viewport)
    {
        _groundDiag--;
        int fb; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &fb);
        int tw = 0, th = 0, fmt = 0;
        Gl.GetTextureLevelParameteriv(FrameSnapshot.ColorTex, 0, Gl.TEXTURE_WIDTH, &tw);
        Gl.GetTextureLevelParameteriv(FrameSnapshot.ColorTex, 0, Gl.TEXTURE_HEIGHT, &th);
        Gl.GetTextureLevelParameteriv(FrameSnapshot.ColorTex, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        _log?.Info($"[grassground] copy taken from fbo {fb} (post hdr fbo {PostChain.Fbo}) at {viewport[2]}x{viewport[3]}, "
                 + $"copy tex {FrameSnapshot.ColorTex} {tw}x{th} fmt 0x{fmt:x} on unit {TextureSlots.GrassGround} (loc {_loc.Ground}), "
                 + $"copies so far {FrameSnapshot.Copies}, pass gen {AlloyRenderer.PassGen}, capturing {AlloyRenderer.Capturing}");
        FrameSnapshot.Compare((uint)fb, viewport[2], viewport[3], "before draw");
    }

    private static void GroundDiagAfter(int* viewport)
    {
        int bound = 0, samp = 0, uval = -1, act = 0;
        Gl.GetIntegerv(0x84E0 /*ACTIVE_TEXTURE*/, &act);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.GrassGround);
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &bound);
        Gl.GetIntegerv(0x8919 /*SAMPLER_BINDING*/, &samp);
        Gl.ActiveTexture((uint)act);
        if (Gl.GetUniformiv != null) Gl.GetUniformiv(_prog, _loc.Ground, &uval);
        _log?.Info($"[grassground] after draw: unit {TextureSlots.GrassGround} holds tex {bound} sampler obj {samp}, uGround = {uval}, viz {Grass.GViz.V}, active unit was 0x{act:x}");
        int fb2; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &fb2);
        FrameSnapshot.Compare((uint)fb2, viewport[2], viewport[3], "after draw");
    }

    /// <summary>#196: draw last frame's scattered cards into every re-baked sun cascade under
    /// <see cref="Grass.Shadow"/>, as alpha-tested depth. The stream still holds last frame's scatter (the
    /// caster pass runs ahead of this frame's). Called by the caster pass after its layered flush, with its
    /// framebuffer; leaves FBO 0 bound.</summary>
    internal static void DrawShadows(uint fbo, uint tex, int size, bool[] rebaked, int cascades)
    {
        int want = Math.Min(Grass.Shadow.V, cascades);
        bool blades = _bladeNear + _bladeFar > 0 && _progBladeDepth != 0, cards = _slotCount > 0 && _progDepth != 0;
        if (want <= 0 || _shadowN <= 0 || !(blades || cards) || _vao == 0 || _off || !Grass.Enabled.V || _down.Failed) return;
        if (_binBucket.IsNull || *_grassEnabled == 0 || Grass.Density.V <= 0f) return;
        bool any = false;
        for (int i = 0; i < want && !any; i++) any = rebaked[i];
        if (!any) return;
        int* tids = stackalloc int[MaxGrassMaterials];
        bool* live = stackalloc bool[MaxGrassMaterials];
        ResolveTextures(tids, live);
        EnsureSamplers();
        float* pw = stackalloc float[3] { _pushOriginX, _pushOriginY, _pushPerUnit / PushRes };
        int* viewport = stackalloc int[4] { 0, 0, size, size };
        Vector3 eye = _shadowEye;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
        Gl.DrawBuffer(Gl.NONE); Gl.ReadBuffer(Gl.NONE);
        Gl.Viewport(0, 0, size, size);
        Gl.BindVertexArray(_vao);
        // the caster gather leaves its layered commands bound; hand them back as found
        int prevCmd = 0;
        Gl.GetIntegerv(DrawIndirectBinding, &prevCmd);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _cmdBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindMats, _matBuf);
        new PassState(depth: true, depthWrite: true, cull: false).Apply();
        Gl.DepthFunc(Gl.LEQUAL);
        // the push field's unit is the post stack's HDR unit (TextureSlots.GrassPush == PostHdr): by the
        // shadow bracket its 2D binding is last frame's picture, and the blades would be displaced by it
        Gl.ActiveTexture(Gl.TEXTURE0 + PushUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _pushTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
        for (int i = 0; i < want; i++)
        {
            if (!rebaked[i]) continue;
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, tex, 0, i);
            Matrix4x4 vp;
            fixed (float* pub = CasterPass.CascadeVp) vp = *(Matrix4x4*)(pub + i * 16);
            if (blades)
            {
                Gl.UseProgram(_progBladeDepth);
                SharedUniforms(in _locBD, in _featLocBD, &vp, &eye, _shadowNear, _shadowFar, false, pw, viewport, vlit: false);
                Gl.Uniform1i(_locBD.Cast, 1);
                DrawBlades(in _locBD);
            }
            if (cards)
            {
                Gl.UseProgram(_progDepth);
                SharedUniforms(in _locD, in _featLocD, &vp, &eye, _shadowNear, _shadowFar, false, pw, viewport, vlit: false);
                Gl.Uniform1i(_locD.Cast, 1);
                Gl.Uniform1i(_locD.Frame, 0);   // a cascade bake is held across frames: a moving dither would freeze mid-step
                Gl.Uniform1i(_locD.SoftEdge, 0);
                DrawSlots(tids, in _locD, depth: true);
            }
            _shadowDraws++;
            if (_dumpShadow) { _dumpShadow = false; DumpLayer(tex, i, size); }
        }
        if (_sampMip != 0) Gl.BindSampler(0, 0);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, (uint)prevCmd);
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
    }

    /// <summary>Ground truth for #196: the cast layer as an 8-bit PGM (near = bright), read back through
    /// glGetTextureSubImage. Cold, once per command.</summary>
    private static void DumpLayer(uint tex, int layer, int size)
    {
        if (Gl.GetTextureSubImage == null) { _log?.Info("GrassField: no glGetTextureSubImage -- no dump"); return; }
        var depth = new float[size * size];
        fixed (float* p = depth)
            Gl.GetTextureSubImage(tex, 0, 0, 0, layer, size, size, 1, Gl.DEPTH_COMPONENT, Gl.FLOAT, size * size * 4, p);
        float min = 1f, max = 0f; int near = 0;
        foreach (float d in depth) { if (d < min) min = d; if (d > max) max = d; if (d < 0.999f) near++; }
        string path = Native.ConfigPath($"cx_grass_shadow_{_dumpN++}.pgm");
        using var f = new System.IO.FileStream(path, System.IO.FileMode.Create);
        var hdr = System.Text.Encoding.ASCII.GetBytes($"P5\n{size} {size}\n255\n");
        f.Write(hdr, 0, hdr.Length);
        var row = new byte[size * size];
        for (int i = 0; i < depth.Length; i++) row[i] = (byte)Math.Clamp((1f - depth[i]) * 255f * 4f, 0f, 255f);
        f.Write(row, 0, row.Length);
        _log?.Info($"GrassField: cast layer {layer} dumped to {path} -- depth min {min:0.0000} max {max:0.0000}, {near} texels written of {depth.Length}, {_shadowDraws} cascade draws so far");
    }

    /// <summary>Share of the blades gone by rank: the cover's thinning, or while snow falls the
    /// <see cref="Grass.SnowFall"/> cut scaled by the falling density, whichever takes more.</summary>
    internal static float SnowThinShare(float cover, float falling)
        => MathF.Max(cover * Grass.SnowThin.V, falling * (1f - Grass.SnowFall.V));

    /// <summary>The uniforms both grass programs read: camera, ladder, fade, wind, push map, feature terms.</summary>
    private static void SharedUniforms(in Loc loc, in AlloyRenderer.FeatLoc feat, Matrix4x4* vp, Vector3* eye,
                                       float nearDist, float farDist, bool msaa, float* pw, int* viewport, bool vlit)
    {
        Gl.UniformMatrix4fv(loc.Vp, 1, 0, (float*)vp);
        Gl.Uniform1f(loc.Tuft, Grass.Tuft.V);
        Gl.Uniform1f(loc.Width, Grass.Width.V);
        Gl.Uniform1i(loc.VLit, vlit ? 1 : 0);   // the depth program never lights: its vertex stage skips the rig
        Gl.Uniform1i(loc.Cast, 0);
        Gl.Uniform1f(loc.CastWidth, Grass.CastWidth.V);
        // the snow on the ground (Weather's integrator) thins the field by rank and winters the survivors
        Gl.Uniform2f(loc.Snow, SnowThinShare(Weather.Snow, Weather.CurrentKind == Weather.Kind.Snow ? Weather.Density : 0f), Weather.Snow * Grass.SnowLook.V);
        Gl.Uniform3fv(loc.Eye, 1, (float*)eye);
        Gl.Uniform2f(loc.Fade, nearDist, farDist);
        Gl.Uniform1i(loc.Frame, PostChain.TaaOn ? AlloyRenderer.FrameSeq & 63 : 0);
        Gl.Uniform4f(loc.Lod, Grass.Lod.V, Grass.LodMin.V, Grass.LodWidth.V, Grass.LodHeight.V);
        Gl.Uniform1i(loc.Tex, 0);
        Gl.Uniform1f(loc.WindGain, Grass.Wind.V);
        Gl.Uniform1f(loc.WindSpeed, Grass.WindSpeed.V);
        Gl.Uniform1i(loc.Msaa, msaa ? 1 : 0);
        Gl.Uniform1f(loc.Edge, MathF.Max(Grass.Edge.V, 0.01f));
        Gl.Uniform1i(loc.PushTex, (int)PushUnit);
        Gl.Uniform1f(loc.PushGain, _pushTex == 0 ? 0f : Grass.Push.V);
        Gl.Uniform3fv(loc.PushWindow, 1, pw);
        AlloyRenderer.UploadFeatureTerms(feat, viewport);
    }

    /// <summary>The two procedural blade draws: near strips, then far single triangles. The program is bound.</summary>
    private static void DrawBlades(in Loc loc)
    {
        Gl.Uniform1i(loc.Segments, NearSegments);
        if (_bladeNear > 0) Gl.DrawArraysIndirect(Gl.TRIANGLE_STRIP, (void*)0);
        Gl.Uniform1i(loc.Segments, FarSegments);
        if (_bladeFar > 0) Gl.DrawArraysIndirect(Gl.TRIANGLE_STRIP, (void*)(4 * sizeof(uint)));
        _draws += 2;
    }

    /// <summary>Every live slot's indirect draw (CX-24): its material's atlas bound, and in the colour pass its
    /// colours. The depth pass binds the atlas too -- its stage reads the alpha.</summary>
    private static void DrawSlots(int* tids, in Loc loc, bool depth)
    {
        for (int slot = 0; slot < _slotCount; slot++) DrawSlot(slot, tids, in loc, depth);
    }

    private static void DrawSlot(int slot, int* tids, in Loc loc, bool depth)
    {
        int mat = _slotMat[slot];
        CAurGrassInfo info = GrassInfo(mat);
        int tid = tids[mat];
        if (info.IsNull || tid == 0) return;   // not uploaded yet: skip the material, not the pass
        if (!depth)
        {
            Vector3 dif = info.Diffuse, amb = info.Ambient;
            Gl.Uniform3fv(loc.MatDif, 1, (float*)&dif);
            Gl.Uniform3fv(loc.MatAmb, 1, (float*)&amb);
        }
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        if (_sampMip != 0) Gl.BindSampler(0, HasMips(tid) ? _sampMip : _sampFlat);
        Gl.DrawArraysIndirect(Gl.TRIANGLES, (void*)((2 + slot) * 4 * sizeof(uint)));
        _draws++;
    }

    /// <summary><c>/grass dumpmips</c>: one-shot readback of every level of every live grass atlas, as
    /// <c>cx-grassmip-{material}-L{level}.ppm</c> (rgb) + <c>.pgm</c> (alpha) in the user directory. The
    /// ground truth for what the blades sample; cold path, allocates.</summary>
    private static bool _dumpMips;
    private static void DumpMips(int* tids, bool* live)
    {
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            if (!live[m] || tids[m] == 0) continue;
            uint tm = (uint)tids[m];
            for (int l = 0; l < 16; l++)
            {
                int w = 0, h = 0, fmt = 0;
                Gl.GetTextureLevelParameteriv(tm, l, Gl.TEXTURE_WIDTH, &w);
                Gl.GetTextureLevelParameteriv(tm, l, Gl.TEXTURE_HEIGHT, &h);
                Gl.GetTextureLevelParameteriv(tm, l, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
                if (w <= 0 || h <= 0) break;
                var px = new byte[w * h * 4];
                fixed (byte* pp = px) Gl.GetTextureImage(tm, l, Gl.RGBA, Gl.UNSIGNED_BYTE, px.Length, pp);
                var rgb = new byte[w * h * 3]; var a = new byte[w * h];
                for (int i = 0; i < w * h; i++) { rgb[i*3] = px[i*4]; rgb[i*3+1] = px[i*4+1]; rgb[i*3+2] = px[i*4+2]; a[i] = px[i*4+3]; }
                string b = Native.ConfigPath($"cx-grassmip-{m}-L{l}");
                using (var f = File.Create(b + ".ppm")) { var hd = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n"); f.Write(hd); f.Write(rgb); }
                using (var f = File.Create(b + ".pgm")) { var hd = System.Text.Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n"); f.Write(hd); f.Write(a); }
                _log?.Info($"[grass] dumpmips: material {m} tid {tids[m]} level {l} {w}x{h} fmt 0x{fmt:x} -> {b}.ppm/.pgm");
            }
        }
    }

    /// <summary>Every grass atlas samples an Alloy-built chain: level 0 read back through the driver's own
    /// decode, mips averaged alpha-weighted (<see cref="TexScan.BuildMips"/>), the whole texture respecified
    /// RGBA8. Ground truth from <c>/grass dumpmips</c>: a DDS ships a box chain over black transparent
    /// texels (alpha-weighted luminance 59 -> 27 by level 3 on a 256px cell), and the transcode never
    /// touches a stored chain -- so the mid-field blade, a few pixels on that cell, sampled a third of its
    /// colour as an opaque cutout. The rebuild is marked by MAX_LEVEL == levels-1 on an RGBA8 texture, which
    /// a fresh engine upload (same tid after an area transition) never carries, so the per-frame check is two
    /// level queries and the rebuild re-fires exactly when the engine replaced the storage.</summary>
    /// <summary><c>GL_TEXTURE_IMMUTABLE_FORMAT</c>: 1 on a texture whose storage came from <c>glTexStorage2D</c>.</summary>
    private const uint TextureImmutableFormat = 0x912F;   // not-an-offset: GL enum

    private static bool HasMips(int tid)
    {
        uint t = (uint)tid;
        int w0 = 0, h0 = 0, fmt = 0, cap = 0, w1 = 0;
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_WIDTH, &w0);
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_HEIGHT, &h0);
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        Gl.GetTextureParameteriv(t, Gl.TEXTURE_MAX_LEVEL, &cap);
        int* lw = stackalloc int[16], lh = stackalloc int[16]; long* off = stackalloc long[16];
        long total = 0;
        int n = w0 > 0 ? TexScan.Chain(w0, h0, true, 16, lw, lh, off, out total) : 0;
        if (n > 0 && fmt == (int)Gl.RGBA8 && cap == n - 1) return true;   // the marker: already ours
        // The BCn transcode drip (#265) re-creates an uncompressed atlas as IMMUTABLE storage, which TexImage2D
        // cannot respecify: the rebuild below failed silently every frame (a readback sync + the mip loop +
        // ten refused uploads, 22k times a minute on SoW = 144 -> 30 fps). Its chain is already
        // TexScan.BuildMips' alpha-weighted one, so there is nothing to rebuild.
        int immutable = 0;
        Gl.GetTextureParameteriv(t, TextureImmutableFormat, &immutable);
        if (immutable != 0) return n > 0;
        if (n == 0)
        {
            Gl.GetTextureLevelParameteriv(t, 1, Gl.TEXTURE_WIDTH, &w1);
            _anyFlat |= w1 <= 0;
            return w1 > 0;
        }
        // ponytail: cold one-shot per upload, allocates; the AMD GetTexImage trap (#396) was the per-upload
        // coverage scan, not a handful of grass atlases -- revisit if a radeonsi client faults here.
        var px = new byte[total];
        fixed (byte* pp = px)
        {
            Gl.GetTextureImage(t, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, (int)total, pp);
            TexScan.BuildMips(pp, n, lw, lh, off);
            Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
            // no DSA respecify exists and the name is the engine's: this rides DrawSlot's draw bind of tid
            for (int l = 0; l < n; l++)
                Gl.TexImage2D(Gl.TEXTURE_2D, l, (int)Gl.RGBA8, lw[l], lh[l], 0, Gl.RGBA, Gl.UNSIGNED_BYTE, pp + off[l]);
            Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        }
        Gl.TextureParameteri(t, Gl.TEXTURE_MAX_LEVEL, n - 1);
        _mipsBuilt++;
        _log?.Info($"[grass] rebuilt the mip chain of tid {tid}: {w0}x{h0} fmt 0x{fmt:x} -> RGBA8, {n} levels alpha-weighted");
        return true;
    }

    /// <summary>The two samplers, and the dials they carry. Anisotropy is the one that matters for grass:
    /// blades are looked at edge-on, where an isotropic filter has to pick one mip for a footprint that is
    /// long in one axis and short in the other, and picks wrong in both directions -- which is the crawling
    /// noise in the mid-field. Clamped to the driver's own ceiling. Wrap is CLAMP_TO_EDGE because the UVs
    /// address one cell of a 2x2 atlas and must never sample the cell across the seam.</summary>
    private static void EnsureSamplers()
    {
        if (Gl.GenSamplers == null || Gl.SamplerParameteri == null || Gl.SamplerParameterf == null) return;
        if (_sampMip == 0)
        {
            uint* ids = stackalloc uint[2];
            Gl.GenSamplers(2, ids);
            if (ids[0] == 0 || ids[1] == 0) return;
            _sampMip = ids[0]; _sampFlat = ids[1];
            _samplerDirty = true;
        }
        // #391: the render-scale bias rides the soften dial; re-applied when the scale moves
        float scaleBias = PostChain.LodBias;
        if (!_samplerDirty && scaleBias == _appliedScaleBias) return;
        _samplerDirty = false; _appliedScaleBias = scaleBias;
        float maxAniso = 1f;
        if (Gl.GetFloatv != null) { float v; Gl.GetFloatv(Gl.MAX_TEXTURE_MAX_ANISOTROPY, &v); if (v > 1f) maxAniso = v; }
        float aniso = MathF.Min(Grass.Aniso.V, maxAniso);
        for (int i = 0; i < 2; i++)
        {
            uint sm = i == 0 ? _sampMip : _sampFlat;
            Gl.SamplerParameteri(sm, Gl.TEXTURE_MIN_FILTER, i == 0 ? Gl.LINEAR_MIPMAP_LINEAR : Gl.LINEAR);
            Gl.SamplerParameteri(sm, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
            Gl.SamplerParameteri(sm, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
            Gl.SamplerParameteri(sm, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
            Gl.SamplerParameterf(sm, Gl.TEXTURE_MAX_ANISOTROPY, aniso);
            Gl.SamplerParameterf(sm, Gl.TEXTURE_LOD_BIAS, Grass.Soften.V + scaleBias);
        }
        _appliedAniso = aniso;
    }

    // ---- the distance ladder (#355) ----------------------------------------------------------

    /// <summary>The fraction of a patch's blades drawn at distance <paramref name="d"/>: all of them inside
    /// <see cref="Grass.Lod"/>, then <c>(lod/d)^2</c> -- the screen area a patch covers falls with the square of
    /// its distance, so this holds the blades PER PIXEL constant -- floored at <see cref="Grass.LodMin"/>. The
    /// vertex stage's <c>CxGrassKeep</c> is this function character for character.</summary>
    /// <summary>The cut-off: <see cref="Grass.Far"/> when set, else the player's own option.</summary>
    /// <summary>The cut-off radius: <c>/grass far</c> when set, else the engine's option -- but never under
    /// <see cref="MinFar"/>. The option defaults to 30u (900 squared), sized for vanilla's per-blade alpha
    /// fade; under the ladder a far blade is nearly free, and a field that thins from 25u and is gone at 30u
    /// reads as the ground darkening a few steps ahead of the player.</summary>
    private static float FarDist() => Grass.Far.V > 0f ? Grass.Far.V : MathF.Max(MathF.Sqrt(MathF.Max(*_farDist, 0f)), MinFar);
    private const float MinFar = 90f;

    internal static float Keep(float d)
    {
        if (d <= Grass.Lod.V) return 1f;
        float r = Grass.Lod.V / d;
        return MathF.Max(Grass.LodMin.V, r * r);
    }

    // ---- culling maths -----------------------------------------------------------------------

    /// <summary>The six frustum planes of a row-vector <c>clip = v * VP</c> matrix (the convention every owned
    /// stage uses): each plane is a COLUMN of VP added to or subtracted from the w column.</summary>
    internal static void Frustum(Matrix4x4* vp, float* planes)
    {
        float* m = (float*)vp;   // row-major: m[r * 4 + c]
        for (int i = 0; i < 6; i++)
        {
            int c = i >> 1;
            float s = (i & 1) == 0 ? 1f : -1f;
            for (int r = 0; r < 4; r++) planes[i * 4 + r] = m[r * 4 + 3] + s * m[r * 4 + c];
        }
    }

    /// <summary>Is the AABB on the inside of every plane? The "positive vertex" test: only the corner furthest
    /// along each plane's normal can save the box, so one dot product per plane decides it.</summary>
    internal static bool Visible(Vector3 min, Vector3 max, float* planes)
    {
        for (int i = 0; i < 6; i++)
        {
            float nx = planes[i * 4], ny = planes[i * 4 + 1], nz = planes[i * 4 + 2], d = planes[i * 4 + 3];
            float px = nx >= 0f ? max.X : min.X;
            float py = ny >= 0f ? max.Y : min.Y;
            float pz = nz >= 0f ? max.Z : min.Z;
            if (nx * px + ny * py + nz * pz + d < 0f) return false;
        }
        return true;
    }

    /// <summary>Distance from a point to the nearest point of the box, 0 inside it.</summary>
    internal static float BoxDistance(Vector3 min, Vector3 max, Vector3 p)
    {
        float dx = MathF.Max(MathF.Max(min.X - p.X, 0f), p.X - max.X);
        float dy = MathF.Max(MathF.Max(min.Y - p.Y, 0f), p.Y - max.Y);
        float dz = MathF.Max(MathF.Max(min.Z - p.Z, 0f), p.Z - max.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>A cheap integer avalanche (Murmur-style finalizer). Deterministic across runs and platforms,
    /// which is what the scatter needs -- <c>Random</c> is neither, and the engine's <c>srand</c>/<c>rand</c>
    /// is process-global state we must not perturb.</summary>
    internal static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return x;
    }

    internal static float Rnd(ref uint st) { st = Hash(st + 0x9e3779b9u); return (st >> 8) * (1f / 16777216f); }

    // ---- GL resources ------------------------------------------------------------------------

    private static bool EnsureBuffers()
    {
        if (_vao != 0) return true;
        _faces ??= new float[MaxFaces * FaceFloats];

        // The card template: two crossed quads in blade-local space. x/y span [-0.5, 0.5] (one quad along x,
        // one along y), z runs 0 at the root to 1 at the tip, and the UVs are the 0..1 of one atlas cell.
        // Corner order per quad: (-half, root) (+half, root) (+half, tip) then (-half, root) (+half, tip)
        // (-half, tip). v runs 0 at the root to 1 at the tip, matching the engine's own corner order --
        // CreateTriangleGrass puts root corners at the atlas sub-rect origin and tip corners half a cell
        // above it, so a flipped v would show the blades upside down.
        // sized for the longest draw off this VAO (a near blade strip) though only the cards read it: an enabled
        // array shorter than the draw is out of bounds on a strict driver whether or not it is consumed
        const int templateCap = 2 * NearSegments + 1;
        float* t = stackalloc float[templateCap * 5];
        new Span<float>(t, templateCap * 5).Clear();
        int k = 0;
        for (int q = 0; q < 2; q++)
        {
            float ax = q == 0 ? 0.5f : 0f, ay = q == 0 ? 0f : 0.5f;
            for (int i = 0; i < 6; i++)
            {
                float side = Corner[i * 3], z = Corner[i * 3 + 1], u = Corner[i * 3 + 2];
                t[k++] = side * ax; t[k++] = side * ay; t[k++] = z; t[k++] = u; t[k++] = z;
            }
        }

        uint vao;
        uint* b = stackalloc uint[7];
        Gl.CreateVertexArrays(1, &vao);
        Gl.CreateBuffers(7, b);
        for (int i = 0; i < 7; i++)
            if (b[i] == 0) return _down.Fail(_log, "the grass buffers could not be created", "grass stays vanilla");
        if (vao == 0) return _down.Fail(_log, "the grass buffers could not be created", "grass stays vanilla");
        uint template = b[0];
        _faceBuf = b[1]; _patchBuf = b[2]; _matBuf = b[3]; _outBuf = b[4]; _cmdBuf = b[5]; _statBuf = b[6];
        Gl.NamedBufferData(_faceBuf, (IntPtr)((long)MaxFaces * FaceFloats * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        Gl.NamedBufferData(_cmdBuf, (IntPtr)(Cmds * 4 * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
        const uint flags = Gl.MAP_READ_BIT | Gl.MAP_PERSISTENT_BIT | Gl.MAP_COHERENT_BIT;
        Gl.NamedBufferStorage(_statBuf, (IntPtr)(Cmds * 4 * sizeof(uint)), null, flags);
        _stat = (uint*)Gl.MapNamedBufferRange(_statBuf, IntPtr.Zero, (IntPtr)(Cmds * 4 * sizeof(uint)), flags);

        // binding 0 = the template (attribs 0, 1); binding 1 = the scatter's output, read back as a divisor-1
        // instance stream (attribs 2..4): each indirect command's baseInstance is its material's region, so one
        // VAO serves every slot
        Gl.NamedBufferData(template, (IntPtr)(templateCap * 5 * sizeof(float)), t, Gl.STATIC_DRAW);
        Gl.VertexArrayVertexBuffer(vao, 0, template, IntPtr.Zero, 5 * sizeof(float));
        Gl.VertexArrayAttribFormat(vao, 0, 3, Gl.FLOAT, 0, 0);
        Gl.VertexArrayAttribFormat(vao, 1, 2, Gl.FLOAT, 0, 3 * sizeof(float));
        Gl.NamedBufferData(_outBuf, (IntPtr)((long)RecordCap * InstFloats * sizeof(float)), null, Gl.DYNAMIC_DRAW);
        Gl.VertexArrayVertexBuffer(vao, 1, _outBuf, IntPtr.Zero, InstFloats * sizeof(float));
        Gl.VertexArrayBindingDivisor(vao, 1, 1);
        for (uint a = 0; a < 5; a++)
        {
            if (a >= 2) Gl.VertexArrayAttribFormat(vao, a, 4, Gl.FLOAT, 0, (a - 2) * 4 * sizeof(float));
            Gl.VertexArrayAttribBinding(vao, a, a < 2 ? 0u : 1u);
            Gl.EnableVertexArrayAttrib(vao, a);
        }
        _vao = vao;

        // The displacement field. LINEAR so the push varies smoothly between texels (a nearest-sampled one
        // makes blades snap between neighbouring directions as a creature walks), CLAMP_TO_EDGE so a blade
        // just outside the window reads the border rather than wrapping to the far side of it.
        if (_push == null)
        {
            _push = new byte[PushRes * PushRes * PushChannels];
            _pushClear = new byte[_push.Length];
            for (int i = 0; i < _pushClear.Length; i += PushChannels)
            { _pushClear[i] = 128; _pushClear[i + 1] = 128; }
        }
        uint tex;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &tex);
        if (tex != 0)
        {
            Gl.TextureStorage2D(tex, 1, Gl.RGBA8, PushRes, PushRes);
            RenderTarget.Clamp(tex, Gl.LINEAR);
            _pushTex = tex;
        }
        return true;
    }

    /// <summary>The composed embedded fragment stage -- what an override REPLACES. Named so
    /// <c>AlloyRenderer.SelfCheck</c> can assert the embedded text satisfies the guards it is checked
    /// against (#132), which it must, or every session rebuilds from it.</summary>
    internal static string EmbeddedFs => ShaderStages.Source(ShdGrassFs);

    /// <summary>#350: the grass stage splices content too; the module edge drops it for a rebuild.</summary>
    internal static void DropProgram()
    {
        foreach (uint p in (ReadOnlySpan<uint>)[_prog, _progDepth, _progScatter, _progBlade, _progBladeDepth])
            if (p != 0) Gl.DeleteProgram(p);
        _prog = 0; _progDepth = 0; _progScatter = 0; _progBlade = 0; _progBladeDepth = 0;
    }

    private static bool EnsureProgram()
    {
        if (_prog != 0) return true;
        if (_down.Failed) return false;
        string fs = EmbeddedFs, vs = ShaderStages.Source(ShdGrassVs);
        uint prog = GlProgram.Graphics(ShdGrassVs, vs, ShdGrassFs, fs, _log,
                                       "GrassField instanced grass", out _, out string fsSrc);
        // #132: an override written before the linear flip still ends in CxRevert, links cleanly, and
        // gets encoded a second time by the resolve -- a washed-out lawn, silently. Rebuilt from the
        // embedded text rather than logged and shipped, like every other Alloy stage's guard.
        if (prog != 0 && !(ShaderContract.LinearSafe(fsSrc) && ShaderContract.SlotSafe(fsSrc, "uTex")))
        {
            Gl.DeleteProgram(prog);
            _log?.Info("GrassField: grass override still encodes to display space -- rebuilding from the "
                     + "embedded default");
            vs = GrassVs;
            prog = GlProgram.Graphics(vs, fs, _log, "GrassField instanced grass (embedded)", content: false);
        }
        if (prog == 0)
            return _down.Fail(_log, "the grass program is unavailable",
                              "grass falls back to the engine's own draw");
        _prog = prog;
        // The prepass program: the SAME vertex text (an override applies to both), the alpha-only stage.
        // Not fatal: without it the colour pass writes depth itself, the pre-#355 shape.
        _progDepth = GlProgram.Graphics(vs, GrassDepthFs, _log, "GrassField grass depth prepass");
        if (_progDepth == 0) _log?.Info("GrassField: the depth prepass program is unavailable -- grass draws single-pass");
        else
        {
            _locD = Locate(_progDepth);
            _featLocD = Programs.FeatLocs(_progDepth);
            Gl.UseProgram(_progDepth);
            Gl.Uniform1i(_locD.Tex, 0);
            // the per-vertex rig's shadow samplers live in the shared vertex text: unassigned they sit on unit 0
            // beside uTex and GL refuses every depth draw
            ShadowUnits(in _locD);
        }
        if (!EnsureScatter()) return false;
        _loc = Locate(prog);
        _featLoc = Programs.FeatLocs(prog);
        // Same reason the lit program does it here: a sampler left at its default points at unit 0, and GL
        // refuses a draw whose samplers of different types share an image unit.
        Gl.UseProgram(prog);
        Gl.Uniform1i(_loc.Tex, 0);
        return EnsureProgramTail(prog);
    }

    /// <summary>CX-24: the scatter every grass draw reads (without it the pass stands down), and the procedural
    /// blade pair, colour and cascade depth off one vertex text (without it every atlas draws as cards).</summary>
    private static bool EnsureScatter()
    {
        string bvs = ShaderStages.Source(ShdBladeVs), bfs = ShaderStages.Source(ShdBladeFs);
        uint b = GlProgram.Graphics(ShdBladeVs, bvs, ShdBladeFs, bfs, _log, "GrassField procedural blades", out string bvsSrc, out string bfsSrc);
        if (b != 0 && !ShaderContract.LinearSafe(bfsSrc))
        {
            Gl.DeleteProgram(b);
            _log?.Info("GrassField: blade override still encodes to display space -- rebuilding from the embedded default");
            bvsSrc = bvs;
            b = GlProgram.Graphics(bvs, bfs, _log, "GrassField procedural blades (embedded)", content: false);
        }
        _progBlade = b;
        if (b == 0) _log?.Info("GrassField: the blade program is unavailable -- every grass atlas draws as cards");
        else
        {
            _locB = Locate(b);
            _featLocB = Programs.FeatLocs(b);
            Gl.UseProgram(b);
            ShadowUnits(in _locB);
            _progBladeDepth = GlProgram.Graphics(bvsSrc, ShaderSource.Get("cx_gblade_df.glsl"), _log, "GrassField blade cascade depth");
            if (_progBladeDepth != 0)
            {
                _locBD = Locate(_progBladeDepth);
                _featLocBD = Programs.FeatLocs(_progBladeDepth);
                Gl.UseProgram(_progBladeDepth);
                ShadowUnits(in _locBD);
            }
        }
        uint c = GlProgram.Compute(ScatterCs, _log, "GrassField scatter");
        if (c == 0) return _down.Fail(_log, "the grass scatter program is unavailable", "grass falls back to the engine's own draw");
        _progScatter = c;
        _locS = new ScatterLoc
        {
            Planes = GlProgram.Loc(c, "uPlanes"), Eye = GlProgram.Loc(c, "uEyePos"), Lod = GlProgram.Loc(c, "uLod"),
            Fade = GlProgram.Loc(c, "uFade"), SnowThin = GlProgram.Loc(c, "uSnowThin"), Clamp = GlProgram.Loc(c, "uClamp"),
            PushTex = GlProgram.Loc(c, "uPushTex"), PushWindow = GlProgram.Loc(c, "uPushWindow"),
            Near = GlProgram.Loc(c, "uNear"), Clump = GlProgram.Loc(c, "uClump"), CardKeep = GlProgram.Loc(c, "uCardKeep"),
            BladeCap = GlProgram.Loc(c, "uBladeCap"),
        };
        return true;
    }

    internal static string ScatterCs => ShaderSource.Get("cx_grass_c.glsl");

    private static Loc Locate(uint prog)
        => new Loc
        {
            Vp = GlProgram.Loc(prog, "uVP"), Eye = GlProgram.Loc(prog, "uEyePos"), Frame = GlProgram.Loc(prog, "uFrame"),
            Fade = GlProgram.Loc(prog, "uFade"), Tex = GlProgram.Loc(prog, "uTex"),
            Lod = GlProgram.Loc(prog, "uLod"), Shade = GlProgram.Loc(prog, "uShade"), Viz = GlProgram.Loc(prog, "uViz"),
            Look = GlProgram.Loc(prog, "uLook"), Probe = GlProgram.Loc(prog, "uProbe"), Segments = GlProgram.Loc(prog, "uSegments"), Width = GlProgram.Loc(prog, "uWidth"),
            Tuft = GlProgram.Loc(prog, "uTuft"), VLit = GlProgram.Loc(prog, "uVLit"), Cast = GlProgram.Loc(prog, "uCast"), CastWidth = GlProgram.Loc(prog, "uCastWidth"), Snow = GlProgram.Loc(prog, "uSnow"),
            RootAo = GlProgram.Loc(prog, "uRootAo"), GroundTint = GlProgram.Loc(prog, "uGroundTint"), Ground = GlProgram.Loc(prog, "uGround"), GroundOn = GlProgram.Loc(prog, "uGroundOn"), GroundViz = GlProgram.Loc(prog, "uGroundViz"), SoftEdge = GlProgram.Loc(prog, "uSoftEdge"),
            MatAmb = GlProgram.Loc(prog, "uMatAmbient"), MatDif = GlProgram.Loc(prog, "uMatDiffuse"),
            Lit = GlProgram.Loc(prog, "uLit"), WindGain = GlProgram.Loc(prog, "uWindGain"),
            WindSpeed = GlProgram.Loc(prog, "uWindSpeed"),
            Msaa = GlProgram.Loc(prog, "uMsaa"), Edge = GlProgram.Loc(prog, "uEdge"),
            PushTex = GlProgram.Loc(prog, "uPushTex"), PushGain = GlProgram.Loc(prog, "uPushGain"),
            PushWindow = GlProgram.Loc(prog, "uPushWindow"),
            SunTex = GlProgram.Loc(prog, "uSunShadow"), SunDynTex = GlProgram.Loc(prog, "uSunDynShadow"),
            CubeTex = GlProgram.Loc(prog, "uLocalShadow"), CubeDynTex = GlProgram.Loc(prog, "uLocalDynShadow"),
            CubeCount = GlProgram.Loc(prog, "uLocalCubes"),
        };

    private static bool EnsureProgramTail(uint prog)
    {
        ShadowUnits(in _loc);
        _log?.Info("GrassField: instanced grass program ready");
        return true;
    }

    /// <summary>Sampler units for the shadow rig + push texture; both grass programs share the vertex text
    /// and so both carry these samplers.</summary>
    private static void ShadowUnits(in Loc l)
    {
        Gl.Uniform1i(l.SunTex, (int)AlloyRenderer.SunUnit);
        // the grass program's unit 10 is uPushTex, so its sun-dyn sampler reads the second unit the table assigns
        Gl.Uniform1i(l.SunDynTex, (int)AlloyRenderer.SunDynUnitGrass);
        Gl.Uniform1i(l.CubeTex, (int)AlloyRenderer.CubeUnit);
        Gl.Uniform1i(l.CubeDynTex, (int)AlloyRenderer.CubeDynUnit);
        Gl.Uniform1i(l.PushTex, (int)PushUnit);
        Gl.Uniform1i(l.Ground, (int)TextureSlots.GrassGround);
        Gl.Uniform1i(l.Probe, (int)EnvProbe.ProbeUnit);
    }

    // ---- shaders -----------------------------------------------------------------------------

    internal const string ShdGrassVs = "cx_grass_vs", ShdGrassFs = "cx_grass_fs";
    internal const string ShdBladeVs = "cx_gblade_vs", ShdBladeFs = "cx_gblade_fs";

    /// <summary>The vertex stage: one crossed-quad blade per instance, rotated, scaled, and swayed. The sway
    /// is root-anchored (<c>z^2</c>, so the base never leaves the ground) and driven by the engine's own
    /// <c>areaGlobalWind</c> plus the per-blade bend jitter, which is what stops a field moving as one sheet.
    /// <para>This stage DISPLACES, which is exactly why grass is not in the #119 depth prepass and why it does
    /// not compute <c>AlloyRenderer</c>'s invariant clip expression.</para></summary>
    internal static string GrassVs => ShaderSource.Get("cx_grass_v.glsl");

    internal static string GrassFsHead => ShaderSource.Get("cx_grass_fhead.glsl");

    /// <summary>The fragment stage. Two discards and no blending at all, which is the whole replacement:
    /// vanilla's fade rides in the alpha channel its cutout reads, so between 15u and ~27.7u every blade is
    /// semi-transparent and depth-writing and deletes whatever stands behind it. Here the fade is a SCREEN-DOOR
    /// dither -- a fragment is either fully opaque or gone -- so the ramp cannot produce a blended fragment,
    /// and because the ramp reaches zero exactly at the engine's own far radius there is no pop either.</summary>
    internal static string GrassFsMain => ShaderSource.Get("cx_grass_fmain.glsl");

    /// <summary>#355: the depth prepass stage. The colour stage's coverage decision -- alpha cutout, distance
    /// dither, keyhole -- and nothing else, so the depth it writes is exactly the set of fragments the colour
    /// pass will keep. Colour writes are masked off by the pass; the alpha output still feeds
    /// alpha-to-coverage where the target is multisampled, with the same coverage the colour pass writes.</summary>
    internal static string GrassDepthFs => ShaderSource.Get("cx_grassdepth_f.glsl");

    // ---- live tuning (/grass) ----------------------------------------------------------------

    /// <summary>One line for the <c>/grass</c> echo and the arm-time log.</summary>
    internal static string Describe()
        => $"{(_off ? "UNAVAILABLE" : Grass.Enabled.V ? "on" : "off")} density x{Grass.Density.V:0.##} wind x{Grass.Wind.V:0.##} windspeed {Grass.WindSpeed.V:0.####} " +
           $"soften {Grass.Soften.V:0.##} aniso {_appliedAniso:0.#}x edge {Grass.Edge.V:0.##} " +
           $"push x{Grass.Push.V:0.##} r{Grass.PushRadius.V:0.##} over {Grass.PushExtent.V:0.#}u " +
           $"cull {(Grass.Cull.V ? $"on shrink {Grass.CullShrink.V:0.##}" + (Grass.CullMax.V > 0f ? $" max {Grass.CullMax.V:0.#}u" : "") : "off")} " +
           $"lod {Grass.Lod.V:0.#}u min {Grass.LodMin.V:0.###} w{Grass.LodWidth.V:0.##} h{Grass.LodHeight.V:0.##} shade {Grass.Shade.V:0.#}u tuft {Grass.Tuft.V:0} vlight {(Grass.VLight.V ? 1 : 0)} shadow {Grass.Shadow.V} castwidth {Grass.CastWidth.V:0.#} ({_shadowDraws} cascade draws) rootao {Grass.RootAo.V:0.##} gtint {Grass.GTint.V:0.##} softedge {(Grass.SoftEdge.V ? 1 : 0)} far {(Grass.Far.V > 0f ? $"{Grass.Far.V:0.#}u" : "engine")} prepass {(Grass.Prepass.V ? (_progDepth != 0 ? "on" : "UNAVAILABLE") : "off")} " +
           $"({_pushers} displacing, {_cullers} culling, {_reticles} non-solid skipped) " +
           $"style {(Grass.Style.V == Grass.StyleBlades ? (_progBlade != 0 ? $"blades x{Grass.BladeDensity.V:0} width {Grass.Width.V:0.##} clump {Grass.Clump.V:0.##} detail {Grass.BladeLod.V:0}u" : "blades UNAVAILABLE (cards)") : "textures")} " +
           $"backlight {Grass.Backlight.V:0.##} sheen {Grass.Sheen.V:0.##} " +
           $"({_patchCount} patches, {_faceCount} faces / {_bladesResident} blades resident; " +
           $"{(_samples > 1 ? $"{_samples}x MSAA, alpha-to-coverage live" : "no MSAA, dithered edge")}" +
           $"{(_anyFlat ? ", A TEXTURE HAS NO MIPS" : "")}) {DescribeBorder()}";

    // ---- diagnostics -------------------------------------------------------------------------

    internal static void ResetPeriod()
    { _slotsOver = 0; _clipped = 0; _frames = 0; _draws = 0; _visible = 0; _culled = 0; _baked = 0; _disabled = 0; _candidates = 0; _scatters = 0; _drawnNear = 0; _drawnFar = 0; _drawnCards = 0; _pushWalked = 0; _pushWalkedAll = 0; }

    internal static string Stats()
    {
        if (_off) return "grass vanilla (Alloy pass unavailable)";
        if (!Grass.Enabled.V) return "grass vanilla (/grass enabled 0)";
        if (_down.Failed) return "grass VANILLA (Alloy program unavailable)";
        if (_frames == 0) return "grass idle";
        int f = Math.Max(_scatters, 1);
        return $"grass {_patchCount} patches / {_faceCount} faces / {_bladesResident} blades resident, " +
               $"{(float)_visible / _frames:F0} patches drawn + {(float)_culled / _frames:F0} culled per frame, " +
               // the scatter's own count, off the GPU a frame late: what the ladder kept, then what survived
               // the per-blade frustum/solid/snow cull, by draw
               $"{(float)_candidates / f:F0} blades past the ladder -> {(float)_drawnNear / f:F0} near + {(float)_drawnFar / f:F0} far blades + {(float)_drawnCards / f:F0} cards drawn per frame" +
               (_stat == null ? " (NO COUNT READBACK)" : "") +
               $", {(float)_draws / _frames:F0} draws/frame, {_baked} baked" +
               (_thinned == 0 ? "" : $", {_thinned} patches thinned to {MaxPatchBlades} blades") +
               (_slotsOver == 0 ? "" : $", {_slotsOver} MATERIAL-FRAMES PAST THE {Slots} DRAW SLOTS (not drawn)") +
               (_clipped == 0 ? "" : $", {_clipped} FRAMES OVER THE {RecordCap} RECORD CAP (far grass clipped) -- lower /grass density or far") +
               // A refusal is invisible in every other number here -- the patch is present and empty, so
               // "0 blades" and "the area has no grass" read identically without it.
               (_refused == 0 ? "" : $", {_refused} LISTS REFUSED (no faces, no height, or a material index " +
                                     "past the live grass-info count)") +
               (_capped ? $", FACE CAP HIT ({MaxFaces}) -- the rest of the area has no grass" : "") +
               (_patchCapped ? $", PATCH CAP HIT ({MaxPatches}) -- the rest of the area has no grass" : "") +
               // The two facts that decide how the cutout edge is resolved, and neither is visible anywhere
               // else: with no MSAA the edge is dithered rather than coverage-resolved, and a texture with
               // no mip chain gets the flat sampler (so anisotropy and the soften bias do nothing on it).
               $", {(_samples > 1 ? $"{_samples}x MSAA (A2C)" : "no MSAA (dithered edge)")}, mips built {_mipsBuilt}" +
               $", aniso {_appliedAniso:0.#}x" + (_anyFlat ? ", A GRASS TEXTURE HAS NO MIPS" : "") +
               // Zero displacers with creatures on screen means the gob list, not the field: the shader term
               // is doing its job over a map nothing was splatted into, which no other number distinguishes.
               // A material a server removed is skipped here rather than never baked -- so it shows up as
               // resident blades that never draw, which without this reads as culling gone wrong.
               (_disabled == 0 ? "" : $", {(float)_disabled / _frames:F0} patches/frame on DISABLED materials") +
               $", {_pushers} gobs displacing, {_cullers} culling (push field walked {(float)_pushWalked / Math.Max(_frames, 1):F0} gobs/frame" +
               $"{(_pushWalkedAll > 0 ? $", {_pushWalkedAll} frames over the whole scene" : ", visible list")})" +
               // Non-solid gobs are the target reticle and the spell VFX. A zero here on a client that is
               // clearly targeting something means PartTriMesh::GetIsOpaque did not resolve and everything
               // counts as solid again.
               $", {_reticles} non-solid skipped"
               + (SceneWalk.WorldScene == IntPtr.Zero ? " (NO WORLD SCENE -- displacement off)" : "");
    }
}
