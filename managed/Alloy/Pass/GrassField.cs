using System.Collections.Generic;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
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
/// <para>This is instead one GPU-instanced draw per visible patch, alpha DISCARD with no blending and depth
/// writes on, wind in the vertex shader, per-patch frustum + distance culling, and a dithered (screen-door)
/// fade so the ramp never produces a partially transparent fragment. The punch-through is structurally
/// impossible: no grass fragment is ever semi-transparent.</para>
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
    /// <summary><c>CAurTriangleList</c> layout, from Ghidra <c>CAurTriangleList::AddTriangle</c> @0x4b5650 and
    /// <c>BuildGrassPolys</c> @0x4b5080: the world-space face vertices as a flat float array (9 floats per
    /// face, stride 0x24) with its FLOAT count at +8, the parallel per-face blade counts at +0x30 with their
    /// count at +0x38, and the grass material index at +0x40 (<c>RenderGrassPolys</c> compares exactly that
    /// against the material it is drawing).</summary>
    private static readonly int ListVerts = EngineLayout.ListVerts, ListVertFloats = EngineLayout.ListVertFloats,
                               ListBlades = EngineLayout.ListBlades, ListBladeCount = EngineLayout.ListBladeCount,
                               ListMat = EngineLayout.ListMat;
    /// <summary><c>CAurTriangleBin</c> layout, from <c>RenderGrassPolys</c> @0x4b5410: the triangle-list
    /// array at +0x38 and its count at +0x40. (The constructor @0x4b4ba0 also stores the BSP cell's own
    /// min/max at +0x0c / +0x18; the patch bounds are the blades' own extent instead -- see
    /// <see cref="Bake"/>.)</summary>
    private static readonly int BinLists = EngineLayout.BinLists, BinListCount = EngineLayout.BinListCount;
    /// <summary><c>CAurGrassInfo</c>, 0x54 bytes: 32-char texture name, density, blade height (the quad is
    /// height wide AND height tall), diffuse and ambient colours, and the four 2x2-atlas selection
    /// probabilities. Read live off <c>g_GrassInfo</c> -- a server <c>AddGrassOverride</c> rewrites them.</summary>
    private static readonly int InfoStride = EngineLayout.GrassInfoStride, InfoName = EngineLayout.GrassInfoName,
                               InfoHeight = EngineLayout.GrassInfoHeight, InfoDiffuse = EngineLayout.GrassInfoDiffuse,
                               InfoAmbient = EngineLayout.GrassInfoAmbient, InfoProb = EngineLayout.GrassInfoProb,
                               InfoInUse = EngineLayout.GrassInfoInUse;
    /// <summary>A <c>List&lt;T&gt;</c> / <c>CExoArrayList&lt;T&gt;</c> in this engine is {element pointer,
    /// count}: the symbol's own address is the list, not the elements.</summary>
    private static readonly int ListPtr = EngineLayout.ListItems, ListCount = EngineLayout.ListItemCount;

    /// <summary>Patches (one per visible bin x grass material) the cache holds, and the open-addressed slot
    /// table that interns them by <c>CAurTriangleList*</c> (power of two, ~2x for a 0.5 load factor).</summary>
    private const int MaxPatches = 32768, PatchHash = 65536;
    /// <summary>Blades resident on the GPU. 32 bytes each, so the cap is the VRAM budget: 1M blades is 32MB
    /// of instance stream (and the same again as the CPU copy). #355 raised it from 300k: the field now
    /// reaches the whole visible area, and a 32x32 outdoor area at the tileset's density is ~250k blades
    /// BEFORE the density dial. Past it new patches simply are not baked, and the period line says so.</summary>
    private const int MaxInstances = 1_000_000;
    /// <summary>Floats per instance: <c>vec4 (x, y, z, rotation)</c> + <c>vec4 (scale, atlasU, atlasV,
    /// bend)</c>.</summary>
    private const int InstFloats = 8;
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

    // ---- the gob-footprint struct layout (#127); every offset below is a row in EngineLayout (#441) ----
    /// <summary>Gob -&gt; its cached bounding box, min at +0x160 and max at +0x16c. <c>Gob::GetBoundingBox</c>
    /// @0x436ee0 is a plain read of exactly these two, no recompute. Only the EXTENT is used (the pair's
    /// space -- model or world -- is not established, and the extent is the same either way); it is centred
    /// on <see cref="EngineLayout.GobPosition"/>, which is unambiguously world.</summary>
    private static readonly int GobBoxMin = EngineLayout.GobBoundsMin, GobBoxMax = EngineLayout.GobBoundsMax;
    /// <summary>The MdlNode <c>Gob::SetAsCharacter</c> writes through (<see cref="EngineLayout.GobNode"/>), and
    /// the byte it writes: <c>node+0x8e = 4</c>. The only positive "this gob is a creature" mark found in the
    /// client. Used to keep creatures OUT of the grass cull -- they bend grass, they do not erase it.</summary>
    private const int NodeCharacterValue = 4;
    private static readonly int NodeCharacterKind = EngineLayout.NodeOwnerFlags;
    /// <summary>A Part's child array (+0x38) and its count (+0x40), and the vtable slot (+0x60) that yields the
    /// part's <c>PartTriMesh*</c> or null. Straight out of <c>Gob::ContainsNonSolids</c> @0x43f250 and
    /// <c>Gob::ContainsWater</c> @0x43f2f0, which walk exactly this shape. <see cref="PartTriMeshLive"/> is the
    /// byte ContainsNonSolids gates the trimesh on before testing its opacity.</summary>
    private static readonly int PartAsTriMeshVtbl = CX.Framework.EngineLayout.PartAsTriMeshSlot;
    private static readonly int PartChildren = EngineLayout.PartChildren, PartChildCount = EngineLayout.PartChildCount,
                               PartTriMeshLive = EngineLayout.PartRenderFlag;
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
    /// <summary>The engine's own blade scale jitter, from <c>FUN_0x4b3a00</c>:
    /// <c>(rand%10000/10000 - 0.5) * 0.4 + 1.0</c>.</summary>
    private const float ScaleJitter = 0.4f;
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
    private static delegate* unmanaged<IntPtr, void> _origBin, _origReload;
    private static delegate* unmanaged<byte*, IntPtr, byte*, int*, int, byte, IntPtr> _aurGet;
    private static byte* _binBucket, _grassInfo, _grassEnabled;
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
        public int Mat, First, Count, Seen;
    }

    private static readonly Patch[] _patches = new Patch[MaxPatches];
    private static readonly int[] _slot = new int[PatchHash];
    private static int _patchCount, _instCount;
    /// <summary>#196: <see cref="Grass.Shadow"/> sun cascades (nearest first) the blades are drawn into as
    /// casters; 0 = grass casts nothing. The draw reuses the depth prepass program under the cascade's
    /// matrix on LAST frame's visible list (the caster pass runs ahead of the colour pass), and only into a
    /// cascade the frame re-baked, so the shadow follows the sway at the static cascade's own cadence.</summary>
    private static int _shadowN;
    private static Vector3 _shadowEye;
    private static float _shadowNear, _shadowFar;
    private static long _shadowDraws, _shadowPatches, _shadowBlades;
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
    /// <summary>CPU-side instance stream. Allocated once, cold: the draw path never allocates.</summary>
    private static float[]? _inst;
    private static int _uploaded;

    private static readonly int[] _vis = new int[MaxPatches];
    private static readonly int[] _visCount = new int[MaxPatches];
    private static readonly float[] _visKey = new float[MaxPatches];

    private static uint _vao, _vboInst, _prog, _progDepth;
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
    private static delegate* unmanaged<IntPtr, byte> _isOpaque;
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
    private static long _blades;
    /// <summary>Cap latches, cleared with the tables in <see cref="Forget"/> rather than per period: they
    /// describe the resident field, not the frame, and a cap that stops being reported reads as headroom
    /// that is not there.</summary>
    private static bool _capped, _patchCapped;
    private static int _samples = 1;
    /// <summary>The DEFAULT framebuffer's sample count, latched for the life of the context (0 = not yet
    /// read). It is the only multisampled surface the grass pass can land on, and it is fixed once the
    /// context exists -- so the query belongs here and not in the frame path (#710).</summary>
    private static int _fb0Samples;
    private static float _appliedAniso = 1f;
    private static bool _anyFlat;

    private struct Loc
    {
        public int Vp, Eye, Fade, Tex, MatAmb, MatDif, Lit, WindGain, WindSpeed, Msaa, Edge, Lod, Shade, Tuft, VLit, Cast, CastWidth, RootAo, GroundTint, Ground, GroundOn, GroundViz, SoftEdge, Snow;
        public int PushTex, PushGain, PushWindow;
        public int Ambient, Diffuse, Dir, Grid, Tile, Z, Falloff, Wrap, Atten, Hot, WrapN, Sss, Reach;
        public int SunTex, SunVp, SunTexel, SunBias, SunCount, SunDynTex, SunDyn, SunFocus, SunRadius;
        public int CubeTex, CubeLight, CubePlane, CubeCount, CubeSoft, CubePcss, CubeDynTex, CubeDynPage, CubeFade, CubeFloor;
    }
    private static Loc _loc;
    private static AlloyRenderer.FeatLoc _featLoc, _featLocD;
    private static Loc _locD;
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
        _binBucket = (byte*)Native.Sym(SymbolMap.BinBucket);
        _grassInfo = (byte*)Native.Sym(SymbolMap.GrassInfo);
        _grassEnabled = (byte*)Native.Sym(SymbolMap.GrassEnabled);
        // Not fatal: no gob list just means nothing displaces the grass (the pre-#127-followup look).
        _farDist = (float*)Native.Sym(SymbolMap.GrassFarDistance);
        _nearRadius = (float*)Native.Sym(SymbolMap.GrassNearRadius);
        _aurGet = (delegate* unmanaged<byte*, IntPtr, byte*, int*, int, byte, IntPtr>)
            Native.Sym(SymbolMap.AurTextureGet);
        // Not fatal: without it every gob counts as solid, which is the pre-fix behaviour (target reticles
        // and other effect gobs displace grass) rather than a misrender.
        _isOpaque = (delegate* unmanaged<IntPtr, byte>)Native.Sym(SymbolMap.PartGetIsOpaque);
        if (_binBucket == null || _grassInfo == null || _grassEnabled == null || _farDist == null
            || _nearRadius == null || _aurGet == null || !AurTex.Ready)
        {
            _off = true;
            log.Info("GrassField: grass globals unresolved -- grass stays on the engine's own path");
            return;
        }

        Array.Fill(_slot, -1);
        BorderInstall(log);
        _origBin = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.DoBinBucket, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnDoBinBucket);
        _origReload = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.ReloadGrass, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnReloadGrass);
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
        _vao = 0; _vboInst = 0; _prog = 0; _progDepth = 0; _sampMip = 0; _sampFlat = 0; _pushTex = 0;
        _fb0Samples = 0;
        _loc = default;
        _down.Forget();
        Forget();
    }

    internal static void Forget()
    {
        _patchCount = 0; _shadowN = 0;
        _instCount = 0;
        _uploaded = 0;
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
        if (_off || !Grass.Enabled.V || _down.Failed || _binBucket == null) return false;
        if (*_grassEnabled == 0) return false;
        if (Gl.DrawArraysInstancedBaseInstance == null || Gl.VertexAttribDivisor == null)
        { _down.Fail(_log, "no instanced-draw entry point", "grass stays vanilla"); return false; }
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
        if (_off || !Grass.Enabled.V || _down.Failed || _binBucket == null) return;
        if (*_grassEnabled == 0) return;      // the player's "Grass Mode" option: off means off, both paths
        if (Gl.DrawArraysInstancedBaseInstance == null || Gl.VertexAttribDivisor == null)
        { _down.Fail(_log, "no instanced-draw entry point", "grass stays vanilla"); return; }
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
        IntPtr* bins = *(IntPtr**)(_binBucket + ListPtr);
        int binCount = *(int*)(_binBucket + ListCount);
        if (bins == null || binCount <= 0) return 0;
        int budget = BakePerFrame, seen = 0;
        for (int b = 0; b < binCount; b++)
        {
            IntPtr bin = bins[b];
            if (bin == IntPtr.Zero) continue;
            IntPtr* lists = *(IntPtr**)(bin + BinLists);
            int listCount = *(int*)(bin + BinListCount);
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

    /// <summary>Scatter one triangle list into the instance stream. The face vertices and the per-face blade
    /// counts are the ENGINE's -- the tileset's authored density ratios survive, and the client dial scales
    /// them. Deterministic: the seed is a hash of the face's own vertices, so a rebuild puts every blade back
    /// where it was rather than reshuffling the meadow.
    /// <para>A list this cannot scatter is interned as an EMPTY patch rather than left unknown. A refusal is
    /// a stable property of the list (a material index past the live grass-info count, no faces, no height,
    /// the instance cap), so an un-interned one would be retried every frame forever -- and 24 of them ahead
    /// of the good lists in <c>binbucket</c> order would eat the whole per-frame bake budget and leave the
    /// area with no grass at all, reported as a healthy "0 patches". <see cref="Stats"/> counts them.</para></summary>
    private static int Bake(IntPtr list)
    {
        // The one refusal that CANNOT be interned: there is no slot to intern it in. Counted instead, and
        // loud in the period line -- past this point every list retries, which is the state to see.
        if (_patchCount >= MaxPatches || _inst == null) { _patchCapped = true; return -1; }
        int p = _patchCount++;
        _patches[p] = new Patch { List = list, Seen = _frame };   // Count 0: refused until proven otherwise
        Intern(list, p);
        float* verts = *(float**)(list + ListVerts);
        int floats = *(int*)(list + ListVertFloats);
        int* blades = *(int**)(list + ListBlades);
        int bladeFaces = *(int*)(list + ListBladeCount);
        int mat = *(int*)(list + ListMat);
        int faces = floats / 9;
        if (verts == null || blades == null || faces <= 0 || bladeFaces < faces) { _refused++; return p; }
        byte* info = GrassInfo(mat);
        if (info == null) { _refused++; return p; }
        float height = *(float*)(info + InfoHeight);
        if (!(height > 0f)) { _refused++; return p; }
        float* prob = (float*)(info + InfoProb);

        int first = _instCount;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int f = 0; f < faces; f++)
        {
            int want = (int)(blades[f] * Grass.Density.V);
            if (want <= 0) continue;
            if (want > MaxBladesPerFace) want = MaxBladesPerFace;
            float* v = verts + f * 9;
            Vector3 a = new(v[0], v[1], v[2]), b = new(v[3], v[4], v[5]), c = new(v[6], v[7], v[8]);
            uint st = Hash(BitConverter.SingleToUInt32Bits(a.X)
                         ^ (BitConverter.SingleToUInt32Bits(b.Y) * 2654435761u)
                         ^ (BitConverter.SingleToUInt32Bits(c.Z) * 40503u));
            for (int i = 0; i < want; i++)
            {
                if (_instCount >= MaxInstances) { _capped = true; break; }
                // uniform barycentric point, the engine's own three-random normalise
                float r0 = Rnd(ref st), r1 = Rnd(ref st), r2 = Rnd(ref st);
                float sum = r0 + r1 + r2;
                if (sum <= 0f) { r0 = 1f; sum = 1f; }
                r0 /= sum; r1 /= sum; r2 /= sum;
                Vector3 pos = a * r0 + b * r1 + c * r2;
                float rot = Rnd(ref st) * MathF.Tau;
                float scale = height * ((Rnd(ref st) - 0.5f) * ScaleJitter + 1f);
                // one of the four 2x2 atlas sub-rects, against the tileset's own probabilities
                // Cell 0 is the fallback, as it is in the engine (FUN_0x4b3a00 falls out of its own
                // probability walk with both uv offsets at 0): a degenerate table must not silently render
                // a different quadrant of the atlas.
                float pick = Rnd(ref st), acc = 0f;
                int cell = 0;
                for (int k = 0; k < 4; k++) { acc += prob[k]; if (pick < acc) { cell = k; break; } }
                float bend = 0.7f + Rnd(ref st) * 0.6f;

                int o = _instCount * InstFloats;
                _inst[o] = pos.X; _inst[o + 1] = pos.Y; _inst[o + 2] = pos.Z; _inst[o + 3] = rot;
                _inst[o + 4] = scale;
                _inst[o + 5] = cell;
                _inst[o + 6] = 0f;                  // rank, assigned once the patch is shuffled below
                _inst[o + 7] = bend;
                _instCount++;
                min = Vector3.Min(min, pos); max = Vector3.Max(max, pos);
            }
            if (_capped) break;
        }
        if (_instCount == first) { _refused++; return p; }
        // #355: the patch's blades in a deterministic shuffled order, each carrying its rank, so that ANY
        // prefix of the patch is a uniform sample of it -- which is what lets a far patch draw a fraction of
        // its blades by count alone. Seeded off the first face, like the scatter, so a rebuild reshuffles
        // identically.
        Shuffle(_inst, first, _instCount - first, Hash(BitConverter.SingleToUInt32Bits(verts[0]) ^ 0x51ed27u));
        for (int i = first; i < _instCount; i++) _inst[i * InstFloats + 6] = (i - first + 0.5f) / (_instCount - first);
        // The blades' own extent, padded by what a blade occupies around its root: half its width each way
        // plus the lean, and its full height up. NOT intersected with the bin's cell bounds -- a face
        // straddling its cell puts blades outside it, and clipping the box there would pop them at the
        // frustum edge.
        min -= new Vector3(height, height, 0f);
        max += new Vector3(height, height, height * 1.5f);

        _patches[p] = new Patch { List = list, Min = min, Max = max, Mat = mat,
                                  First = first, Count = _instCount - first, Seen = _frame };
        _baked++;
        return p;
    }

    private static byte* GrassInfo(int mat)
    {
        byte* recs = *(byte**)(_grassInfo + ListPtr);
        int count = *(int*)(_grassInfo + ListCount);
        if (recs == null || mat < 0 || mat >= count) return null;
        return recs + mat * InfoStride;
    }

    /// <summary>Push whatever the bake appended. One <c>BufferSubData</c> over the new tail, never the whole
    /// stream -- the resident blades do not change once scattered.</summary>
    private static void Upload()
    {
        if (_inst == null || _instCount <= _uploaded) return;
        int floats = (_instCount - _uploaded) * InstFloats;
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _vboInst);
        // The first write after a Forget (area load, /grass density) starts at byte 0, over a range the GPU
        // may still be reading from last frame's draws -- a bare BufferSubData there makes the driver
        // serialise, at exactly the two moments this class exists to keep smooth. Orphan the old allocation
        // first. (#84's UploadDraws did the same until #242 made those records persistent -- a persistent
        // buffer cannot orphan, and this one still can because a Forget really does discard its contents.)
        // Every later write is an append past the drawn range and needs none of this.
        if (_uploaded == 0)
            Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)((long)MaxInstances * InstFloats * sizeof(float)), null,
                          Gl.DYNAMIC_DRAW);
        fixed (float* src = &_inst[_uploaded * InstFloats])
            Gl.BufferSubData(Gl.ARRAY_BUFFER, (IntPtr)(_uploaded * InstFloats * sizeof(float)),
                             (IntPtr)(floats * sizeof(float)), src);
        _uploaded = _instCount;
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
                gobs = *(IntPtr**)(scene + EngineLayout.SceneGobs);
                count = *(int*)(scene + EngineLayout.SceneGobCount);
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
                IntPtr tree = *(IntPtr*)(gob + EngineLayout.GobPartTree);
                if (tree == IntPtr.Zero) continue;
                float* pos = (float*)(gob + EngineLayout.GobPosition);
                float gx = (pos[0] - _pushOriginX) * _pushPerUnit, gy = (pos[1] - _pushOriginY) * _pushPerUnit;
                // The cull footprint can be much larger than the push radius, so the window test uses
                // whichever of the two actually reaches.
                float hx = 0f, hy = 0f;
                bool cull = Grass.Cull.V && Footprint(gob, ref hx, ref hy);
                float reach = MathF.Max(rTex, MathF.Max(hx, hy) * _pushPerUnit);
                if (gx < -reach || gy < -reach || gx > PushRes + reach || gy > PushRes + reach) continue;
                // A gob with no OPAQUE geometry is not standing in the grass, it is drawn over it: target
                // reticles, spell VFX, the ground-target decal. They neither bend nor cull.
                if (!HasOpaque(tree, 0)) { _reticles++; continue; }
                _pushers++;
                Splat(_push, gx, gy, rTex);
                if (cull)
                {
                    _cullers++;
                    CullRect(_push, gx, gy, hx * _pushPerUnit, hy * _pushPerUnit);
                }
            }
        }

        Gl.ActiveTexture(Gl.TEXTURE0 + PushUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _pushTex);
        fixed (byte* p = _push)
            Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RGBA8, PushRes, PushRes, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
        Gl.ActiveTexture(Gl.TEXTURE0);
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
        IntPtr node = *(IntPtr*)(gob + EngineLayout.GobNode);
        if (node != IntPtr.Zero
            && *(byte*)(node + NodeCharacterKind) == NodeCharacterValue) return false;
        float* mn = (float*)(gob + GobBoxMin), mx = (float*)(gob + GobBoxMax);
        hx = (mx[0] - mn[0]) * 0.5f * Grass.CullShrink.V;
        hy = (mx[1] - mn[1]) * 0.5f * Grass.CullShrink.V;
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
        IntPtr vt = *(IntPtr*)part;
        if (vt != IntPtr.Zero)
        {
            var asTri = *(delegate* unmanaged<IntPtr, IntPtr>*)(vt + PartAsTriMeshVtbl);
            if (asTri != null)
            {
                IntPtr tri = asTri(part);
                if (tri != IntPtr.Zero && *(byte*)(tri + PartTriMeshLive) != 0 && _isOpaque(tri) != 0)
                    return true;
            }
        }
        IntPtr* kids = *(IntPtr**)(part + PartChildren);
        int n = *(int*)(part + PartChildCount);
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

    /// <summary>Cull, sort, issue. The sort key is (material, distance): materials are a handful and switching
    /// one costs a texture bind, while near-to-far inside a material is what lets early-Z throw away the
    /// overdraw grass is otherwise made of.</summary>
    private static void Issue(int seen, Matrix4x4* vp, int* viewport, float near, float far, bool lightingOn)
    {
        float* planes = stackalloc float[24];
        Frustum(vp, planes);
        Vector3 eye = AlloyRenderer.EyePos;
        float farDist = FarDist();
        // #355: the dither ramp is the CUT-OFF's edge now, not the whole range -- the ladder thins the field
        // by count from the lod radius outward, and a blade the ramp would have dithered is simply not drawn instead.
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
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            byte* info = GrassInfo(m);
            live[m] = info != null && *(info + InfoInUse) != 0;
            IntPtr tex = !live[m] ? IntPtr.Zero : _aurGet(info + InfoName, IntPtr.Zero, null, null, 0, 0);
            tids[m] = OwnedTextures.Draw(AurTex.Tid(tex));
        }
        if (_dumpMips) { _dumpMips = false; DumpMips(tids, live); }
        EnsureSamplers();
        int samples = TargetSamples();
        _samples = samples;
        bool msaa = samples > 1;

        int n = 0;
        for (int p = 0; p < _patchCount; p++)
        {
            ref Patch q = ref _patches[p];
            if (q.Seen != _frame || q.Count <= 0) continue;
            if (q.Mat < 0 || q.Mat >= MaxGrassMaterials || !live[q.Mat]) { _disabled++; continue; }
            float d = BoxDistance(q.Min, q.Max, eye);
            if (d > farDist || BoxDistance(q.Min, q.Max, fogOrigin) > opaque || !Visible(q.Min, q.Max, planes)) { _culled++; continue; }
            _vis[n] = p;
            // material first (a switch costs a texture bind), then near-to-far inside it. The multiplier
            // is 1e5 rather than 1e6 so a float still resolves single world units inside a material.
            _visKey[n] = q.Mat * 1e5f + d;
            n++;
        }
        if (n == 0) return;
        Array.Sort(_visKey, _vis, 0, n);
        _visible += n;
        _shadowN = n; _shadowEye = eye; _shadowNear = nearDist; _shadowFar = farDist;

        // The prefix count per patch, once: the prepass and the colour pass must draw the SAME blades.
        // keep(NEAREST point of the patch), so it is never smaller than the keep any blade of the patch
        // computes for itself -- the vertex stage's own fade then closes the gap without a pop.
        for (int i = 0; i < n; i++)
        {
            ref Patch q = ref _patches[_vis[i]];
            int count = Math.Max(1, (int)MathF.Ceiling(q.Count * Keep(BoxDistance(q.Min, q.Max, eye))));
            _visCount[i] = count > q.Count ? q.Count : count;
        }
        float* pw = stackalloc float[3] { _pushOriginX, _pushOriginY, _pushPerUnit / PushRes };
        Gl.BindVertexArray(_vao);
        bool prepass = Grass.Prepass.V && _progDepth != 0;
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
            AlloyRenderer.GpuBegin(AlloyRenderer.GpuTerm.GrassDepth);
            DrawPatches(n, tids, in _locD, depth: true);
            AlloyRenderer.GpuEnd(AlloyRenderer.GpuTerm.GrassDepth);
            Gl.ColorMask(1, 1, 1, 1);
            Gl.DepthFunc(Gl.LEQUAL);
        }

        Gl.UseProgram(_prog);
        SharedUniforms(in _loc, in _featLoc, vp, &eye, nearDist, farDist, msaa, pw, viewport, vlit: Grass.VLight.V && lightingOn);
        Gl.Uniform1f(_loc.Shade, Grass.Shade.V);
        Gl.Uniform1i(_loc.Lit, lightingOn ? 1 : 0);
        Gl.Uniform1f(_loc.RootAo, Grass.RootAo.V);
        Gl.Uniform1f(_loc.GroundTint, Grass.GTint.V);
        // The frame as it stands before the grass, for the root blend. One colour-only blit (the shader reads
        // no depth); the water pass takes its own, with depth, later. Skipped outright when the dial is off.
        bool ground = Grass.GTint.V > 0f && FrameSnapshot.Copy(viewport, depth: false);
        Gl.Uniform1i(_loc.GroundOn, ground ? 1 : 0);
        Gl.Uniform1i(_loc.GroundViz, Grass.GViz.V);
        Gl.Uniform1i(_loc.SoftEdge, soft ? 1 : 0);
        if (ground && _groundDiag > 0)
        {
            _groundDiag--;
            int fb; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &fb);
            int saved; Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &saved);
            Gl.BindTexture(Gl.TEXTURE_2D, FrameSnapshot.ColorTex);
            int tw = 0, th = 0, fmt = 0;
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_WIDTH, &tw);
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_HEIGHT, &th);
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, 0x1003 /*TEXTURE_INTERNAL_FORMAT*/, &fmt);
            Gl.BindTexture(Gl.TEXTURE_2D, (uint)saved);
            _log?.Info($"[grassground] copy taken from fbo {fb} (post hdr fbo {PostChain.Fbo}) at {viewport[2]}x{viewport[3]}, "
                     + $"copy tex {FrameSnapshot.ColorTex} {tw}x{th} fmt 0x{fmt:x} on unit {TextureSlots.GrassGround} (loc {_loc.Ground}), "
                     + $"copies so far {FrameSnapshot.Copies}, pass gen {AlloyRenderer.PassGen}, capturing {AlloyRenderer.Capturing}");
            FrameSnapshot.Compare((uint)fb, viewport[2], viewport[3], "before draw");
        }
        if (lightingOn)
        {
            AlloyLights.BindForDraw();
            AlloyLights.SetDrawUniforms(_loc.Grid, _loc.Tile, _loc.Z, _loc.Falloff, _loc.Wrap,
                                        _loc.Atten, _loc.Hot, _loc.WrapN, _loc.Sss, viewport, near, far, _loc.Reach);
        }
        AlloyRenderer.UploadAreaTerms(_loc.Ambient, _loc.Diffuse, _loc.Dir);
        AlloyRenderer.UploadSunCascades(_loc.SunTex, _loc.SunVp, _loc.SunTexel, _loc.SunBias, _loc.SunCount,
                                        _loc.SunDyn, _loc.SunFocus, _loc.SunRadius);
        AlloyRenderer.UploadLocalCubes(_loc.CubeTex, _loc.CubeLight, _loc.CubePlane, _loc.CubeCount,
                                          _loc.CubeSoft, _loc.CubePcss, _loc.CubeDynPage, _loc.CubeFade, _loc.CubeFloor);

        // The class's whole point, in one declaration: blending OFF, and the coverage decision made entirely
        // by the fragment stage's two discards. Depth writes come from the prepass when it ran (the colour
        // pass then tests LEQUAL against its own depth, and early-Z rejects the hidden fragments before the
        // taps), and from this pass alone when it did not. Two-sided because a blade is a card -- the engine
        // disables culling here too.
        // Alpha-to-coverage ONLY where the target actually has samples to cover -- there the fragment writes
        // its analytic edge coverage and the hardware resolves the silhouette properly. It is not a blend:
        // every sample is written whole or not at all, so the no-semi-transparent-depth-writing-fragment
        // guarantee is untouched. On a single-sampled target it would be a bare threshold shift, so it stays
        // off and the dither carries the edge instead.
        // #292: cull OFF, stated rather than inherited from the default. Grass is the one owned pass whose
        // geometry is genuinely two-sided -- a blade is a single card seen from both faces -- so this
        // reproduces the engine, it does not miss it. There is no material behind a blade to read `twosided`
        // off; the class IS the decision.
        new PassState(depth: true, depthWrite: !prepass, cull: false, blend: soft,
                      blendSrc: Gl.SRC_ALPHA, blendDst: Gl.ONE_MINUS_SRC_ALPHA).Apply();
        if (msaa) Gl.Enable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        AlloyRenderer.GpuBegin(AlloyRenderer.GpuTerm.Grass);
        DrawPatches(n, tids, in _loc, depth: false);
        AlloyRenderer.GpuEnd(AlloyRenderer.GpuTerm.Grass);
        if (ground && _groundDiag > 0)
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
        if (msaa) Gl.Disable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        if (_sampMip != 0) Gl.BindSampler(0, 0);
        Gl.BindVertexArray(0);
    }

    /// <summary>#196: draw last frame's visible blades into every re-baked sun cascade under
    /// <see cref="Grass.Shadow"/>, as alpha-tested depth. Called by the caster pass after its layered
    /// flush, with its framebuffer; leaves FBO 0 bound.</summary>
    internal static void DrawShadows(uint fbo, uint tex, int size, bool[] rebaked, int cascades)
    {
        int want = Math.Min(Grass.Shadow.V, cascades);
        if (want <= 0 || _shadowN <= 0 || _progDepth == 0 || _vao == 0 || _off || !Grass.Enabled.V || _down.Failed) return;
        if (_binBucket == null || *_grassEnabled == 0 || Grass.Density.V <= 0f) return;
        bool any = false;
        for (int i = 0; i < want && !any; i++) any = rebaked[i];
        if (!any) return;
        int* tids = stackalloc int[MaxGrassMaterials];
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            byte* info = GrassInfo(m);
            bool live = info != null && *(info + InfoInUse) != 0;
            IntPtr t = !live ? IntPtr.Zero : _aurGet(info + InfoName, IntPtr.Zero, null, null, 0, 0);
            tids[m] = OwnedTextures.Draw(AurTex.Tid(t));
        }
        EnsureSamplers();
        float* pw = stackalloc float[3] { _pushOriginX, _pushOriginY, _pushPerUnit / PushRes };
        int* viewport = stackalloc int[4] { 0, 0, size, size };
        Vector3 eye = _shadowEye;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
        Gl.DrawBuffer(Gl.NONE); Gl.ReadBuffer(Gl.NONE);
        Gl.Viewport(0, 0, size, size);
        Gl.BindVertexArray(_vao);
        Gl.UseProgram(_progDepth);
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
            SharedUniforms(in _locD, in _featLocD, &vp, &eye, _shadowNear, _shadowFar, false, pw, viewport, vlit: false);
            Gl.Uniform1i(_locD.Cast, 1);
            Gl.Uniform1i(_locD.SoftEdge, 0);
            DrawPatches(_shadowN, tids, in _locD, depth: true, cast: true);
            _shadowDraws++;
            if (_dumpShadow) { _dumpShadow = false; DumpLayer(tex, i, size); }
        }
        if (_sampMip != 0) Gl.BindSampler(0, 0);
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
        _log?.Info($"GrassField: cast layer {layer} dumped to {path} -- depth min {min:0.0000} max {max:0.0000}, {near} texels written of {depth.Length}, {_shadowBlades} blades cast so far");
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
        Gl.Uniform1i(loc.VLit, vlit ? 1 : 0);   // the depth program never lights: its vertex stage skips the rig
        Gl.Uniform1i(loc.Cast, 0);
        Gl.Uniform1f(loc.CastWidth, Grass.CastWidth.V);
        // the snow on the ground (Weather's integrator) thins the field by rank and winters the survivors
        Gl.Uniform2f(loc.Snow, SnowThinShare(Weather.Snow, Weather.CurrentKind == Weather.Kind.Snow ? Weather.Density : 0f), Weather.Snow * Grass.SnowLook.V);
        Gl.Uniform3fv(loc.Eye, 1, (float*)eye);
        Gl.Uniform2f(loc.Fade, nearDist, farDist);
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

    /// <summary>One pass over the visible patches, material-sorted: a texture bind per material change and
    /// one prefix draw per patch. The depth pass binds textures too -- its stage reads the alpha -- but
    /// skips the material colours the colour pass alone shades with.</summary>
    private static void DrawPatches(int n, int* tids, in Loc loc, bool depth, bool cast = false)
    {
        // Bound LAST, after the light rig's own binds, on a unit the rig never touches.
        if (!depth)
        {
            Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.GrassGround);
            Gl.BindTexture(Gl.TEXTURE_2D, FrameSnapshot.ColorTex);
            Gl.ActiveTexture(Gl.TEXTURE0);
        }
        int boundMat = -1, boundTex = 0;
        for (int i = 0; i < n; i++)
        {
            ref Patch q = ref _patches[_vis[i]];
            if (q.Mat != boundMat)
            {
                boundMat = q.Mat;
                byte* info = GrassInfo(q.Mat);
                if (info == null) { boundTex = 0; continue; }
                if (!depth)
                {
                    Gl.Uniform3fv(loc.MatDif, 1, (float*)(info + InfoDiffuse));
                    Gl.Uniform3fv(loc.MatAmb, 1, (float*)(info + InfoAmbient));
                }
                boundTex = q.Mat < MaxGrassMaterials ? tids[q.Mat] : 0;
                Gl.BindTexture(Gl.TEXTURE_2D, (uint)boundTex);
                if (boundTex != 0 && _sampMip != 0)
                    Gl.BindSampler(0, HasMips(boundTex) ? _sampMip : _sampFlat);
            }
            if (boundTex == 0) continue;   // not uploaded yet: skip the patch, not the pass
            Gl.DrawArraysInstancedBaseInstance(Gl.TRIANGLES, 0, TemplateVerts, _visCount[i], (uint)q.First);
            _draws++;
            if (!depth) _blades += _visCount[i];
            if (cast) { _shadowPatches++; _shadowBlades += _visCount[i]; }
        }
    }

    /// <summary><c>/grass dumpmips</c>: one-shot readback of every level of every live grass atlas, as
    /// <c>cx-grassmip-{material}-L{level}.ppm</c> (rgb) + <c>.pgm</c> (alpha) in the user directory. The
    /// ground truth for what the blades sample; cold path, allocates.</summary>
    private static bool _dumpMips;
    private static void DumpMips(int* tids, bool* live)
    {
        if (Gl.GetTexImage == null) { _log?.Info("[grass] dumpmips: glGetTexImage unavailable"); return; }
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            if (!live[m] || tids[m] == 0) continue;
            Gl.BindTexture(Gl.TEXTURE_2D, (uint)tids[m]);
            for (int l = 0; l < 16; l++)
            {
                int w = 0, h = 0, fmt = 0;
                Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, l, Gl.TEXTURE_WIDTH, &w);
                Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, l, Gl.TEXTURE_HEIGHT, &h);
                Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, l, 0x1003 /*TEXTURE_INTERNAL_FORMAT*/, &fmt);
                if (w <= 0 || h <= 0) break;
                var px = new byte[w * h * 4];
                fixed (byte* pp = px) Gl.GetTexImage(Gl.TEXTURE_2D, l, Gl.RGBA, Gl.UNSIGNED_BYTE, pp);
                var rgb = new byte[w * h * 3]; var a = new byte[w * h];
                for (int i = 0; i < w * h; i++) { rgb[i*3] = px[i*4]; rgb[i*3+1] = px[i*4+1]; rgb[i*3+2] = px[i*4+2]; a[i] = px[i*4+3]; }
                string b = Native.ConfigPath($"cx-grassmip-{m}-L{l}");
                using (var f = File.Create(b + ".ppm")) { var hd = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n"); f.Write(hd); f.Write(rgb); }
                using (var f = File.Create(b + ".pgm")) { var hd = System.Text.Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n"); f.Write(hd); f.Write(a); }
                _log?.Info($"[grass] dumpmips: material {m} tid {tids[m]} level {l} {w}x{h} fmt 0x{fmt:x} -> {b}.ppm/.pgm");
            }
        }
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
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
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        int w0 = 0, h0 = 0, fmt = 0, cap = 0, w1 = 0;
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_WIDTH, &w0);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, Gl.TEXTURE_HEIGHT, &h0);
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 0, 0x1003 /*TEXTURE_INTERNAL_FORMAT*/, &fmt);
        Gl.GetTexParameteriv(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, &cap);
        int* lw = stackalloc int[16], lh = stackalloc int[16]; long* off = stackalloc long[16];
        long total = 0;
        int n = w0 > 0 ? TexScan.Chain(w0, h0, true, 16, lw, lh, off, out total) : 0;
        if (n > 0 && fmt == (int)Gl.RGBA8 && cap == n - 1) return true;   // the marker: already ours
        // The BCn transcode drip (#265) re-creates an uncompressed atlas as IMMUTABLE storage, which TexImage2D
        // cannot respecify: the rebuild below failed silently every frame (a readback sync + the mip loop +
        // ten refused uploads, 22k times a minute on SoW = 144 -> 30 fps). Its chain is already
        // TexScan.BuildMips' alpha-weighted one, so there is nothing to rebuild.
        int immutable = 0;
        Gl.GetTexParameteriv(Gl.TEXTURE_2D, TextureImmutableFormat, &immutable);
        if (immutable != 0) return n > 0;
        if (n == 0 || Gl.GetTexImage == null || Gl.TexImage2D == null)
        {
            Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 1, Gl.TEXTURE_WIDTH, &w1);
            _anyFlat |= w1 <= 0;
            return w1 > 0;
        }
        // ponytail: cold one-shot per upload, allocates; the AMD GetTexImage trap (#396) was the per-upload
        // coverage scan, not a handful of grass atlases -- revisit if a radeonsi client faults here.
        var px = new byte[total];
        fixed (byte* pp = px)
        {
            Gl.GetTexImage(Gl.TEXTURE_2D, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, pp);
            TexScan.BuildMips(pp, n, lw, lh, off);
            Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
            for (int l = 0; l < n; l++)
                Gl.TexImage2D(Gl.TEXTURE_2D, l, (int)Gl.RGBA8, lw[l], lh[l], 0, Gl.RGBA, Gl.UNSIGNED_BYTE, pp + off[l]);
            Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        }
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, n - 1);
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

    /// <summary>Fisher-Yates over <paramref name="count"/> instance records of <see cref="InstFloats"/> floats
    /// starting at record <paramref name="first"/>, driven by the pass's own hash stream.</summary>
    internal static void Shuffle(float[] a, int first, int count, uint seed)
    {
        for (int i = count - 1; i > 0; i--)
        {
            int j = (int)(Rnd(ref seed) * (i + 1));
            if (j > i) j = i;
            if (j == i) continue;
            int x = (first + i) * InstFloats, y = (first + j) * InstFloats;
            for (int k = 0; k < InstFloats; k++) (a[x + k], a[y + k]) = (a[y + k], a[x + k]);
        }
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

    private static float Rnd(ref uint st) { st = Hash(st + 0x9e3779b9u); return (st >> 8) * (1f / 16777216f); }

    // ---- GL resources ------------------------------------------------------------------------

    private static bool EnsureBuffers()
    {
        if (_vao != 0) return true;
        if (Gl.GenVertexArrays == null || Gl.GenBuffers == null) return false;
        _inst ??= new float[MaxInstances * InstFloats];

        // The blade template: two crossed quads in blade-local space. x/y span [-0.5, 0.5] (one quad along
        // x, one along y), z runs 0 at the root to 1 at the tip, and the UVs are the 0..1 of one atlas cell
        // -- v = 0 at the root, matching the engine's own corner order (CreateTriangleGrass builds root
        // corners at the sub-rect origin and tip corners half a cell above it).
        // Two crossed quads. Corner order per quad: (-half, root) (+half, root) (+half, tip) then
        // (-half, root) (+half, tip) (-half, tip). v runs 0 at the root to 1 at the tip, matching the
        // engine's own corner order -- CreateTriangleGrass puts root corners at the atlas sub-rect origin
        // and tip corners half a cell above it, so a flipped v would show the blades upside down.
        float* t = stackalloc float[TemplateVerts * 5];
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

        uint vao, buf0, buf1;
        Gl.GenVertexArrays(1, &vao);
        Gl.GenBuffers(1, &buf0);
        Gl.GenBuffers(1, &buf1);
        if (vao == 0 || buf0 == 0 || buf1 == 0)
            return _down.Fail(_log, "the grass buffers could not be created", "grass stays vanilla");
        Gl.BindVertexArray(vao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, buf0);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(TemplateVerts * 5 * sizeof(float)), t, Gl.STATIC_DRAW);
        Gl.VertexAttribPointer(0, 3, Gl.FLOAT, 0, 5 * sizeof(float), (void*)0);
        Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(1, 2, Gl.FLOAT, 0, 5 * sizeof(float), (void*)(3 * sizeof(float)));
        Gl.EnableVertexAttribArray(1);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, buf1);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)((long)MaxInstances * InstFloats * sizeof(float)), null,
                      Gl.DYNAMIC_DRAW);
        Gl.VertexAttribPointer(2, 4, Gl.FLOAT, 0, InstFloats * sizeof(float), (void*)0);
        Gl.EnableVertexAttribArray(2);
        Gl.VertexAttribDivisor(2, 1);
        Gl.VertexAttribPointer(3, 4, Gl.FLOAT, 0, InstFloats * sizeof(float), (void*)(4 * sizeof(float)));
        Gl.EnableVertexAttribArray(3);
        Gl.VertexAttribDivisor(3, 1);
        Gl.BindVertexArray(0);
        _vao = vao; _vboInst = buf1;

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
        Gl.GenTextures(1, &tex);
        if (tex != 0)
        {
            Gl.ActiveTexture(Gl.TEXTURE0 + PushUnit);
            Gl.BindTexture(Gl.TEXTURE_2D, tex);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
            Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
            Gl.ActiveTexture(Gl.TEXTURE0);
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
        if (_prog != 0) Gl.DeleteProgram(_prog);
        if (_progDepth != 0) Gl.DeleteProgram(_progDepth);
        _prog = 0; _progDepth = 0;
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
            prog = GlProgram.Graphics(vs, fs, _log, "GrassField instanced grass (embedded)");
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
        _loc = Locate(prog);
        _featLoc = Programs.FeatLocs(prog);
        // Same reason the lit program does it here: a sampler left at its default points at unit 0, and GL
        // refuses a draw whose samplers of different types share an image unit.
        Gl.UseProgram(prog);
        Gl.Uniform1i(_loc.Tex, 0);
        return EnsureProgramTail(prog);
    }

    private static Loc Locate(uint prog)
        => new Loc
        {
            Vp = GlProgram.Loc(prog, "uVP"), Eye = GlProgram.Loc(prog, "uEyePos"),
            Fade = GlProgram.Loc(prog, "uFade"), Tex = GlProgram.Loc(prog, "uTex"),
            Lod = GlProgram.Loc(prog, "uLod"), Shade = GlProgram.Loc(prog, "uShade"),
            Tuft = GlProgram.Loc(prog, "uTuft"), VLit = GlProgram.Loc(prog, "uVLit"), Cast = GlProgram.Loc(prog, "uCast"), CastWidth = GlProgram.Loc(prog, "uCastWidth"), Snow = GlProgram.Loc(prog, "uSnow"),
            RootAo = GlProgram.Loc(prog, "uRootAo"), GroundTint = GlProgram.Loc(prog, "uGroundTint"), Ground = GlProgram.Loc(prog, "uGround"), GroundOn = GlProgram.Loc(prog, "uGroundOn"), GroundViz = GlProgram.Loc(prog, "uGroundViz"), SoftEdge = GlProgram.Loc(prog, "uSoftEdge"),
            MatAmb = GlProgram.Loc(prog, "uMatAmbient"), MatDif = GlProgram.Loc(prog, "uMatDiffuse"),
            Lit = GlProgram.Loc(prog, "uLit"), WindGain = GlProgram.Loc(prog, "uWindGain"),
            WindSpeed = GlProgram.Loc(prog, "uWindSpeed"),
            Msaa = GlProgram.Loc(prog, "uMsaa"), Edge = GlProgram.Loc(prog, "uEdge"),
            PushTex = GlProgram.Loc(prog, "uPushTex"), PushGain = GlProgram.Loc(prog, "uPushGain"),
            PushWindow = GlProgram.Loc(prog, "uPushWindow"),
            Ambient = GlProgram.Loc(prog, "uAreaAmbient"), Diffuse = GlProgram.Loc(prog, "uAreaDiffuse"),
            Dir = GlProgram.Loc(prog, "uAreaDir"),
            Grid = GlProgram.Loc(prog, "uGrid"), Tile = GlProgram.Loc(prog, "uTile"),
            Z = GlProgram.Loc(prog, "uZ"), Falloff = GlProgram.Loc(prog, "uFalloff"),
            Wrap = GlProgram.Loc(prog, "uWrap"), Atten = GlProgram.Loc(prog, "uAtten"), Hot = GlProgram.Loc(prog, "uHot"), WrapN = GlProgram.Loc(prog, "uWrapN"), Sss = GlProgram.Loc(prog, "uSss"), Reach = GlProgram.Loc(prog, "uReach"),
            SunTex = GlProgram.Loc(prog, "uSunShadow"), SunVp = GlProgram.Loc(prog, "uSunVp"),
            SunDynTex = GlProgram.Loc(prog, "uSunDynShadow"), SunDyn = GlProgram.Loc(prog, "uSunDyn"),
            SunTexel = GlProgram.Loc(prog, "uSunTexel"), SunBias = GlProgram.Loc(prog, "uSunBias"),
            SunCount = GlProgram.Loc(prog, "uSunCascades"),
            SunFocus = GlProgram.Loc(prog, "uSunFocus"), SunRadius = GlProgram.Loc(prog, "uSunRadius"),
            CubeTex = GlProgram.Loc(prog, "uLocalShadow"), CubeLight = GlProgram.Loc(prog, "uLocalLight"),
            CubePlane = GlProgram.Loc(prog, "uLocalPlane"), CubeCount = GlProgram.Loc(prog, "uLocalCubes"),
            CubeSoft = GlProgram.Loc(prog, "uLocalSoft"), CubeFloor = GlProgram.Loc(prog, "uLocalFloor"), CubePcss = GlProgram.Loc(prog, "uLocalPcss"),
            CubeDynTex = GlProgram.Loc(prog, "uLocalDynShadow"), CubeDynPage = GlProgram.Loc(prog, "uLocalDynPage"), CubeFade = GlProgram.Loc(prog, "uLocalFade"),
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
    }

    // ---- shaders -----------------------------------------------------------------------------

    internal const string ShdGrassVs = "cx_grass_vs", ShdGrassFs = "cx_grass_fs";

    /// <summary>The vertex stage: one crossed-quad blade per instance, rotated, scaled, and swayed. The sway
    /// is root-anchored (<c>z^2</c>, so the base never leaves the ground) and driven by the engine's own
    /// <c>areaGlobalWind</c> plus the per-blade bend jitter, which is what stops a field moving as one sheet.
    /// <para>This stage DISPLACES, which is exactly why grass is not in the #119 depth prepass and why it does
    /// not compute <c>AlloyRenderer</c>'s invariant clip expression.</para></summary>
    internal static string GrassVs => ShaderSource.Get("GrassField.GrassVs.glsl");

    internal static string GrassFsHead => ShaderSource.Get("GrassField.GrassFsHead.glsl");

    /// <summary>The fragment stage. Two discards and no blending at all, which is the whole replacement:
    /// vanilla's fade rides in the alpha channel its cutout reads, so between 15u and ~27.7u every blade is
    /// semi-transparent and depth-writing and deletes whatever stands behind it. Here the fade is a SCREEN-DOOR
    /// dither -- a fragment is either fully opaque or gone -- so the ramp cannot produce a blended fragment,
    /// and because the ramp reaches zero exactly at the engine's own far radius there is no pop either.</summary>
    internal static string GrassFsMain => ShaderSource.Get("GrassField.GrassFsMain.glsl");

    /// <summary>#355: the depth prepass stage. The colour stage's coverage decision -- alpha cutout, distance
    /// dither, keyhole -- and nothing else, so the depth it writes is exactly the set of fragments the colour
    /// pass will keep. Colour writes are masked off by the pass; the alpha output still feeds
    /// alpha-to-coverage where the target is multisampled, with the same coverage the colour pass writes.</summary>
    internal static string GrassDepthFs => ShaderSource.Get("GrassField.GrassDepthFs.glsl");

    // ---- live tuning (/grass) ----------------------------------------------------------------

    /// <summary>One line for the <c>/grass</c> echo and the arm-time log.</summary>
    internal static string Describe()
        => $"{(_off ? "UNAVAILABLE" : Grass.Enabled.V ? "on" : "off")} density x{Grass.Density.V:0.##} wind x{Grass.Wind.V:0.##} windspeed {Grass.WindSpeed.V:0.####} " +
           $"soften {Grass.Soften.V:0.##} aniso {_appliedAniso:0.#}x edge {Grass.Edge.V:0.##} " +
           $"push x{Grass.Push.V:0.##} r{Grass.PushRadius.V:0.##} over {Grass.PushExtent.V:0.#}u " +
           $"cull {(Grass.Cull.V ? $"on shrink {Grass.CullShrink.V:0.##}" + (Grass.CullMax.V > 0f ? $" max {Grass.CullMax.V:0.#}u" : "") : "off")} " +
           $"lod {Grass.Lod.V:0.#}u min {Grass.LodMin.V:0.###} w{Grass.LodWidth.V:0.##} h{Grass.LodHeight.V:0.##} shade {Grass.Shade.V:0.#}u tuft {Grass.Tuft.V:0} vlight {(Grass.VLight.V ? 1 : 0)} shadow {Grass.Shadow.V} castwidth {Grass.CastWidth.V:0.#} ({_shadowDraws} cascade draws, {_shadowPatches} patches, {_shadowBlades} blades) rootao {Grass.RootAo.V:0.##} gtint {Grass.GTint.V:0.##} softedge {(Grass.SoftEdge.V ? 1 : 0)} far {(Grass.Far.V > 0f ? $"{Grass.Far.V:0.#}u" : "engine")} prepass {(Grass.Prepass.V ? (_progDepth != 0 ? "on" : "UNAVAILABLE") : "off")} " +
           $"({_pushers} displacing, {_cullers} culling, {_reticles} non-solid skipped) " +
           $"({_patchCount} patches, {_instCount} blades resident; " +
           $"{(_samples > 1 ? $"{_samples}x MSAA, alpha-to-coverage live" : "no MSAA, dithered edge")}" +
           $"{(_anyFlat ? ", A TEXTURE HAS NO MIPS" : "")}) {DescribeBorder()}";

    // ---- diagnostics -------------------------------------------------------------------------

    internal static void ResetPeriod()
    { _frames = 0; _draws = 0; _visible = 0; _culled = 0; _baked = 0; _disabled = 0; _blades = 0; _pushWalked = 0; _pushWalkedAll = 0; }

    internal static string Stats()
    {
        if (_off) return "grass vanilla (Alloy pass unavailable)";
        if (!Grass.Enabled.V) return "grass vanilla (/grass enabled 0)";
        if (_down.Failed) return "grass VANILLA (Alloy program unavailable)";
        if (_frames == 0) return "grass idle";
        return $"grass {_patchCount} patches / {_instCount} blades resident, " +
               $"{(float)_visible / _frames:F0} drawn + {(float)_culled / _frames:F0} culled per frame, " +
               $"{(float)_draws / _frames:F0} draws/frame, {(float)_blades / _frames:F0} blades/frame, {_baked} baked" +
               // A refusal is invisible in every other number here -- the patch is present and empty, so
               // "0 blades" and "the area has no grass" read identically without it.
               (_refused == 0 ? "" : $", {_refused} LISTS REFUSED (no faces, no height, or a material index " +
                                     "past the live grass-info count)") +
               (_capped ? $", INSTANCE CAP HIT ({MaxInstances}) -- lower /grass density" : "") +
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
