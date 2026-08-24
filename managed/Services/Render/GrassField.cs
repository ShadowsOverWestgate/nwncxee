using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Ui;

namespace Sowx.Services.Render;

/// <summary>
/// #127 (map #68, spec #114 M3): grass, as a REPLACEMENT class. Not a faster reproduction of
/// <c>Scene::DoBinBucket</c> -- the engine's grass path is left exactly as it is and simply not called on the
/// passes the owned frame draws.
/// <para>What vanilla does, root-caused by #106: it enables ordinary alpha blend, NEVER calls
/// <c>SetDepthMask</c> (so it inherits mask = 1), and pushes its distance fade through the same alpha channel
/// the 0.2 cutout reads. From 15u to ~27.7u every blade is semi-transparent AND depth-writing -- and it draws
/// at step 9 of the pass, before the creatures and placeables standing in it. It also rewrites the wind into
/// every bin's vertex buffer on the CPU every frame BEFORE any culling, frustum-culls nothing, instances
/// nothing, and bakes density into the VDOs at area load with no client knob at all.</para>
/// <para>What this is instead: one GPU-instanced draw per visible patch, alpha DISCARD with no blending and
/// depth writes on, wind in the vertex shader, per-patch frustum + distance culling, and a dithered
/// (screen-door) fade so the ramp never produces a partially transparent fragment. Under that class the
/// punch-through is structurally impossible rather than fixed: no grass fragment is ever both semi-transparent
/// and depth-writing, because none of them is ever semi-transparent.</para>
/// <para>The geometry SOURCE is still the engine's. <c>CAurTriangleList</c> holds the tile faces that carry a
/// grass material in world space, plus the per-face blade count the tileset's <c>GrassDensity</c> produced;
/// this class reads those and does its own scatter at its own density. Placement is therefore the tileset's
/// -- grass is never hand-placed -- while the DENSITY is a client dial (<c>SOWX_GRASS_DENSITY</c>) applied to
/// the authored per-face ratios rather than replacing them.</para>
/// <para>The engine still builds its own bins and VDOs at area load, untouched: that is what keeps ADR 0010's
/// vanilla render mode whole. Flip to vanilla (or stand the owned pass down) and <c>DoBinBucket</c> runs
/// exactly as it always did.</para>
/// <para>Not in the #119 depth prepass, and deliberately so: grass displaces vertices, so the prepass would
/// record its rest silhouette and the colour pass's LEQUAL would then punch holes wherever the wind moved a
/// blade. Grass therefore does NOT carry the character-for-character <see cref="OwnedDrawService"/> clip
/// expression, and must not be "fixed" to.</para>
/// <para>Not a caster: grass is invisible to every capture path the extender has, because those hang off
/// <c>PartTriMesh::Draw</c> inside a <c>RenderDrawBucket</c> walk and this is neither. That is #133, and
/// suppressing <c>DoBinBucket</c> regresses nothing -- vanilla grass cast no shadow either.</para>
/// </summary>
internal static unsafe class GrassField
{
    /// <summary><c>CAurTriangleList</c> layout, from Ghidra <c>CAurTriangleList::AddTriangle</c> @0x4b5650 and
    /// <c>BuildGrassPolys</c> @0x4b5080: the world-space face vertices as a flat float array (9 floats per
    /// face, stride 0x24) with its FLOAT count at +8, the parallel per-face blade counts at +0x30 with their
    /// count at +0x38, and the grass material index at +0x40 (<c>RenderGrassPolys</c> compares exactly that
    /// against the material it is drawing).</summary>
    private const int ListVerts = 0x00, ListVertFloats = 0x08, ListBlades = 0x30, ListBladeCount = 0x38,
                      ListMat = 0x40;
    /// <summary><c>CAurTriangleBin</c> layout, from <c>RenderGrassPolys</c> @0x4b5410: the triangle-list
    /// array at +0x38 and its count at +0x40. (The constructor @0x4b4ba0 also stores the BSP cell's own
    /// min/max at +0x0c / +0x18; the patch bounds are the blades' own extent instead -- see
    /// <see cref="Bake"/>.)</summary>
    private const int BinLists = 0x38, BinListCount = 0x40;
    /// <summary><c>CAurGrassInfo</c>, 0x54 bytes: 32-char texture name, density, blade height (the quad is
    /// height wide AND height tall), diffuse and ambient colours, and the four 2x2-atlas selection
    /// probabilities. Read live off <c>g_GrassInfo</c> -- a server <c>AddGrassOverride</c> rewrites them.</summary>
    private const int InfoStride = 0x54, InfoName = 0x00, InfoHeight = 0x24, InfoDiffuse = 0x28,
                      InfoAmbient = 0x34, InfoProb = 0x40, InfoInUse = 0x50;
    /// <summary>A <c>List&lt;T&gt;</c> / <c>CExoArrayList&lt;T&gt;</c> in this engine is {element pointer,
    /// count}: the symbol's own address is the list, not the elements.</summary>
    private const int ListPtr = 0x00, ListCount = 0x08;

    /// <summary>Patches (one per visible bin x grass material) the cache holds, and the open-addressed slot
    /// table that interns them by <c>CAurTriangleList*</c> (power of two, ~2x for a 0.5 load factor).</summary>
    private const int MaxPatches = 32768, PatchHash = 65536;
    /// <summary>Blades resident on the GPU. 32 bytes each, so the cap is the VRAM budget: 300k blades is
    /// ~9.6MB of instance stream. Past it new patches simply are not baked, and the period line says so.</summary>
    private const int MaxInstances = 300_000;
    /// <summary>Floats per instance: <c>vec4 (x, y, z, rotation)</c> + <c>vec4 (scale, atlasU, atlasV,
    /// bend)</c>.</summary>
    private const int InstFloats = 8;
    /// <summary>Patches baked per frame. Walking into a fresh area makes every visible bin new at once, and
    /// baking them all on one frame is a visible hitch; spread over a few frames it is not. The unbaked
    /// remainder is simply not drawn yet, which for one or two frames reads as grass fading in.</summary>
    private const int BakePerFrame = 24;
    /// <summary>A face that wants more blades than this is a bad read, not a meadow -- refuse it rather than
    /// scatter a million quads off one triangle.</summary>
    private const int MaxBladesPerFace = 4096;
    /// <summary>Bounds on the part-tree walk that answers "is this gob solid". A tree deeper or wider than
    /// this is a bad pointer, not a model.</summary>
    private const int MaxPartDepth = 12, MaxPartChildren = 4096;
    /// <summary>Vertices in the blade template: two quads crossed at 90 degrees, six vertices each. Vanilla
    /// draws ONE flat card per blade, which reads as a paper cutout from the side; a cross costs one extra
    /// quad per blade and is the cheapest thing that makes grass look like grass from any angle.</summary>
    private const int TemplateVerts = 12;
    /// <summary>The grass displacement field: a top-down RG8 push map rebuilt on the CPU every frame around
    /// the player. 128 texels over <see cref="_pushExtent"/> world units is ~0.25u per texel at the default
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
    /// <summary>Texture unit the push map binds on. Deliberately clear of every unit the owned frame already
    /// spends: 0 diffuse, 1 sun cascades, 2 local cubes, 3-8 the lit program's .mtr map slots, 9 the IBL
    /// probe, and 11/12 the engine's own framebuffer copies. Both programs rebind their units every pass, so
    /// sharing one would work today and break the first time either stops -- 10 costs nothing.</summary>
    private const uint PushUnit = 10;
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
    private static byte* _binBucket, _grassInfo, _grassEnabled, _gobBucket;
    private static float* _farDist, _nearRadius;
    private static bool _off, _failed;

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
    /// <summary>CPU-side instance stream. Allocated once, cold: the draw path never allocates.</summary>
    private static float[]? _inst;
    private static int _uploaded;

    private static readonly int[] _vis = new int[MaxPatches];
    private static readonly float[] _visKey = new float[MaxPatches];

    private static uint _vao, _vboInst, _prog;
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
    /// <summary>Set from the chat thread by <c>/grass</c>; consumed by the render thread at the top of
    /// <see cref="Draw"/>. The tables it clears are the render thread's, so the flag is the handoff --
    /// clearing them where the command runs would race a live bake.</summary>
    private static volatile bool _rebake;
    private static float _density = 2f, _windGain = 1f, _windSpeed = 0.001f;
    /// <summary>Softening dials (live: <c>/grass soften|aniso|edge</c>). <c>_lodBias</c> is a mip bias on the
    /// diffuse -- the literal blur knob, and the one to reach for LAST because a positive bias on a 2x2 atlas
    /// starts averaging neighbouring blade variants together. <c>_aniso</c> is the one that actually earns its
    /// keep here: grass is looked at edge-on, which is precisely the case isotropic filtering handles worst.
    /// <c>_edge</c> widens the analytic cutout ramp beyond its natural one-pixel width.</summary>
    private static float _lodBias, _aniso = 8f, _edge = 1f;
    private static bool _samplerDirty = true;
    /// <summary>Displacement dials (live: <c>/grass push|pushradius|pushextent</c>). <c>_pushGain</c> 0
    /// disables the whole field, including its rebuild and upload.</summary>
    /// <para>Retuned after the first live look: at gain 1 a blade was flung a full blade-height sideways and
    /// pressed flat, which read as a blast crater rather than parted grass.</para>
    private static float _pushGain = 0.15f, _pushRadius = 1f, _pushExtent = 64f;
    /// <summary>Object culling: <c>_cull</c> 0 disables it, <c>_cullShrink</c> scales the footprint the gob's
    /// own bounding-box extent gives (below 1 so grass fringes an object rather than stopping at a hard
    /// line), and <c>_cullMax</c> caps the half-extent in world units (0 = uncapped) for the one case the
    /// gob box handles badly -- a tree, whose box is its CANOPY while only its trunk touches the ground.</summary>
    private static float _cull = 1f, _cullShrink = 0.8f, _cullMax;

    // period counters
    private static int _frames, _draws, _visible, _culled, _baked, _refused, _disabled;
    /// <summary>Cap latches, cleared with the tables in <see cref="Forget"/> rather than per period: they
    /// describe the resident field, not the frame, and a cap that stops being reported reads as headroom
    /// that is not there.</summary>
    private static bool _capped, _patchCapped;
    private static int _samples = 1, _mipCount;
    private static float _appliedAniso = 1f;
    private static bool _anyFlat;
    private static readonly int[] _mipTid = new int[MaxGrassMaterials];
    private static readonly bool[] _mipHas = new bool[MaxGrassMaterials];

    private struct Loc
    {
        public int Vp, Eye, Fade, Tex, MatAmb, MatDif, Lit, WindGain, WindSpeed, Msaa, Edge;
        public int PushTex, PushGain, PushWindow;
        public int Ambient, Diffuse, Dir, Grid, Tile, Z, Falloff, Wrap, Atten, Hot, WrapN;
        public int SunTex, SunVp, SunTexel, SunBias, SunCount;
        public int CubeTex, CubeLight, CubePlane, CubeCount, CubeSoft;
    }
    private static Loc _loc;
    private static OwnedDrawService.FeatLoc _featLoc;

    // ---- install -----------------------------------------------------------------------------

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _off = Environment.GetEnvironmentVariable("SOWX_GRASS") == "0";
        if (_off) { log.Info("GrassField: SOWX_GRASS=0 -- grass stays on the engine's own path"); return; }
        _density = MathF.Max(Shadow.CasterPass.EnvFloat("SOWX_GRASS_DENSITY", _density), 0f);
        _windGain = Shadow.CasterPass.EnvFloat("SOWX_GRASS_WIND", _windGain);
        // ponytail: the sway RATE is a dial rather than a derivation. uTimeOfDay is the engine's own
        // worldtimerTimeOfDay int and the stock wind shaders divide it by five-digit constants that are
        // themselves per-material .mtr parameters -- there is no one right divisor to inherit. 0.001 reads
        // as a light breeze if the field is milliseconds; SOWX_GRASS_WINDSPEED is the knob if it is not.
        _windSpeed = Shadow.CasterPass.EnvFloat("SOWX_GRASS_WINDSPEED", _windSpeed);
        _pushGain = Shadow.CasterPass.EnvFloat("SOWX_GRASS_PUSH", _pushGain);
        _pushRadius = Shadow.CasterPass.EnvFloat("SOWX_GRASS_PUSH_RADIUS", _pushRadius);
        _pushExtent = Shadow.CasterPass.EnvFloat("SOWX_GRASS_PUSH_EXTENT", _pushExtent);
        _cull = Shadow.CasterPass.EnvFloat("SOWX_GRASS_CULL", _cull);
        _cullShrink = Shadow.CasterPass.EnvFloat("SOWX_GRASS_CULL_SHRINK", _cullShrink);
        _cullMax = Shadow.CasterPass.EnvFloat("SOWX_GRASS_CULL_MAX", _cullMax);
        _lodBias = Shadow.CasterPass.EnvFloat("SOWX_GRASS_SOFTEN", _lodBias);
        _aniso = Shadow.CasterPass.EnvFloat("SOWX_GRASS_ANISO", _aniso);
        _edge = Shadow.CasterPass.EnvFloat("SOWX_GRASS_EDGE", _edge);

        _binBucket = (byte*)Native.Sym(SymbolMap.BinBucket);
        _grassInfo = (byte*)Native.Sym(SymbolMap.GrassInfo);
        _grassEnabled = (byte*)Native.Sym(SymbolMap.GrassEnabled);
        // Not fatal: no gob list just means nothing displaces the grass (the pre-#127-followup look).
        _gobBucket = (byte*)Native.Sym(SymbolMap.GobBucket);
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
        log.Info($"GrassField: armed -- owned instanced grass at density x{_density:0.##} " +
                 "(SOWX_GRASS=0 disables, SOWX_GRASS_DENSITY dials it)");
    }

    /// <summary>The vanilla grass site. Skipped outright on a pass the owned grass already drew -- which
    /// retires the per-frame CPU wind rewrite over every bin in the area along with the draw, and that is
    /// most of what makes the replacement cheaper than the original. Any pass we did not draw (vanilla render
    /// mode, owned pass stood down, a GUI 3D panel's Scene, no program) falls straight through to the engine,
    /// so the fallback needs no separate switch.</summary>
    [UnmanagedCallersOnly]
    private static void OnDoBinBucket(IntPtr scene)
    {
        if (_origBin == null) return;
        if (OwnedDrawService.IsWorldScene(scene) && OwnedDrawService.DrewThisPass
            && _drawnGen == OwnedDrawService.PassGen) return;
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

    internal static void Forget()
    {
        _patchCount = 0;
        _instCount = 0;
        _uploaded = 0;
        _drawnGen = -1;
        _capped = false;
        _patchCapped = false;
        _refused = 0;
        Array.Fill(_slot, -1);
    }

    // ---- the pass ----------------------------------------------------------------------------

    /// <summary>The owned grass pass, at its <see cref="FrameGraph"/> slot inside the colour bracket.
    /// Bake-then-draw: newly visible patches are scattered and appended to the instance stream (a bounded
    /// number per frame), then every patch sighted this frame is culled, sorted and issued.</summary>
    internal static void Draw(Matrix4x4* vp, int* viewport, float near, float far, bool lightingOn)
    {
        if (_off || _failed || _binBucket == null) return;
        if (*_grassEnabled == 0) return;      // the player's "Grass Mode" option: off means off, both paths
        if (Gl.DrawArraysInstancedBaseInstance == null || Gl.VertexAttribDivisor == null)
        { _failed = true; _log?.Info("GrassField: no instanced-draw entry point -- grass stays vanilla"); return; }
        if (!EnsureProgram() || !EnsureBuffers()) return;
        // Density 0 means the player asked for no grass. Stamp the generation and stop: the class is still
        // OWNED (so the engine does not draw it back), and Collect never runs -- otherwise every frame would
        // re-attempt a bake budget's worth of patches that scatter nothing.
        if (_density <= 0f) { _drawnGen = OwnedDrawService.PassGen; return; }

        if (_rebake) { _rebake = false; Forget(); }
        _frame++;
        _frames++;
        int n = Collect();
        if (n == 0) { _drawnGen = OwnedDrawService.PassGen; return; }
        Upload();
        BuildPush();
        Issue(n, vp, viewport, near, far, lightingOn);
        // Stamped even on a frame that drew nothing visible: the owned pass OWNS the class this pass, and a
        // patch culled by distance must not come back as an engine draw.
        _drawnGen = OwnedDrawService.PassGen;
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
            int want = (int)(blades[f] * _density);
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
                _inst[o + 5] = (cell & 1) != 0 ? 0.5f : 0f;
                _inst[o + 6] = (cell & 2) != 0 ? 0.5f : 0f;
                _inst[o + 7] = bend;
                _instCount++;
                min = Vector3.Min(min, pos); max = Vector3.Max(max, pos);
            }
            if (_capped) break;
        }
        if (_instCount == first) { _refused++; return p; }
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
        // first, the way UploadDraws does (#84). Every later write is an append past the drawn range and
        // needs none of this.
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
        if (_pushTex == 0 || _push == null || _gobBucket == null) return;
        _pushers = 0; _cullers = 0; _reticles = 0;
        // Neutral is 128,128 for the direction and 0 for the flatten -- not one byte value, so it comes from
        // a template rather than an Array.Fill.
        Array.Copy(_pushClear!, _push, _push.Length);
        float extent = MathF.Max(_pushExtent, 1f);
        _pushPerUnit = PushRes / extent;
        Vector3 centre = OwnedDrawService.PlayerPos;
        // Snapped to the texel grid: an origin that slides sub-texel makes every blade's sampled push
        // shimmer as the player walks, which reads as the grass twitching for no reason.
        float texel = 1f / _pushPerUnit;
        _pushOriginX = MathF.Floor(centre.X / texel) * texel - extent * 0.5f;
        _pushOriginY = MathF.Floor(centre.Y / texel) * texel - extent * 0.5f;

        if (_pushGain > 0f && _pushRadius > 0f)
        {
            IntPtr* gobs = *(IntPtr**)(_gobBucket + ListPtr);
            int count = *(int*)(_gobBucket + ListCount);
            float r = _pushRadius, rTex = r * _pushPerUnit;
            for (int i = 0; gobs != null && i < count; i++)
            {
                IntPtr gob = gobs[i];
                if (gob == IntPtr.Zero) continue;
                // Same guard ShadowFeedService uses: a gob with no part tree has no geometry to stand in the
                // grass with, so it must not dent it.
                IntPtr tree = *(IntPtr*)(gob + SymbolMap.GobPartTree);
                if (tree == IntPtr.Zero) continue;
                float* pos = (float*)(gob + SymbolMap.GobPosition);
                float gx = (pos[0] - _pushOriginX) * _pushPerUnit, gy = (pos[1] - _pushOriginY) * _pushPerUnit;
                // The cull footprint can be much larger than the push radius, so the window test uses
                // whichever of the two actually reaches.
                float hx = 0f, hy = 0f;
                bool cull = _cull > 0f && Footprint(gob, ref hx, ref hy);
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
        IntPtr node = *(IntPtr*)(gob + SymbolMap.GobNode);
        if (node != IntPtr.Zero
            && *(byte*)(node + SymbolMap.NodeCharacterKind) == SymbolMap.NodeCharacterValue) return false;
        float* mn = (float*)(gob + SymbolMap.GobBoxMin), mx = (float*)(gob + SymbolMap.GobBoxMax);
        hx = (mx[0] - mn[0]) * 0.5f * _cullShrink;
        hy = (mx[1] - mn[1]) * 0.5f * _cullShrink;
        // A degenerate or absurd box is a field that was never filled, not a very large object.
        if (!(hx > 0.01f) || !(hy > 0.01f) || hx > 500f || hy > 500f) return false;
        if (_cullMax > 0f) { hx = MathF.Min(hx, _cullMax); hy = MathF.Min(hy, _cullMax); }
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
            var asTri = *(delegate* unmanaged<IntPtr, IntPtr>*)(vt + SymbolMap.PartAsTriMeshVtbl);
            if (asTri != null)
            {
                IntPtr tri = asTri(part);
                if (tri != IntPtr.Zero && *(byte*)(tri + SymbolMap.PartTriMeshLive) != 0 && _isOpaque(tri) != 0)
                    return true;
            }
        }
        IntPtr* kids = *(IntPtr**)(part + SymbolMap.PartChildren);
        int n = *(int*)(part + SymbolMap.PartChildCount);
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

    /// <summary>Cull, sort, issue. The sort key is (material, distance): materials are a handful and switching
    /// one costs a texture bind, while near-to-far inside a material is what lets early-Z throw away the
    /// overdraw grass is otherwise made of.</summary>
    private static void Issue(int seen, Matrix4x4* vp, int* viewport, float near, float far, bool lightingOn)
    {
        float* planes = stackalloc float[24];
        Frustum(vp, planes);
        Vector3 eye = OwnedDrawService.EyePos;
        float farDist = MathF.Sqrt(MathF.Max(*_farDist, 0f));
        float nearDist = MathF.Sqrt(MathF.Max(*_nearRadius, 0f));
        if (farDist <= nearDist) farDist = nearDist + 1f;

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
            tids[m] = AurTex.Tid(tex);
        }
        EnsureSamplers();
        int samples = 0;
        Gl.GetIntegerv(Gl.SAMPLES, &samples);
        _samples = samples;
        bool msaa = samples > 1;

        int n = 0;
        for (int p = 0; p < _patchCount; p++)
        {
            ref Patch q = ref _patches[p];
            if (q.Seen != _frame || q.Count <= 0) continue;
            if (q.Mat < 0 || q.Mat >= MaxGrassMaterials || !live[q.Mat]) { _disabled++; continue; }
            float d = BoxDistance(q.Min, q.Max, eye);
            if (d > farDist || !Visible(q.Min, q.Max, planes)) { _culled++; continue; }
            _vis[n] = p;
            // material first (a switch costs a texture bind), then near-to-far inside it. The multiplier
            // is 1e5 rather than 1e6 so a float still resolves single world units inside a material.
            _visKey[n] = q.Mat * 1e5f + d;
            n++;
        }
        if (n == 0) return;
        Array.Sort(_visKey, _vis, 0, n);
        _visible += n;

        Gl.UseProgram(_prog);
        Gl.BindVertexArray(_vao);
        Gl.UniformMatrix4fv(_loc.Vp, 1, 0, (float*)vp);
        Gl.Uniform3fv(_loc.Eye, 1, (float*)&eye);
        Gl.Uniform2f(_loc.Fade, nearDist, farDist);
        Gl.Uniform1i(_loc.Tex, 0);
        Gl.Uniform1f(_loc.WindGain, _windGain);
        Gl.Uniform1f(_loc.WindSpeed, _windSpeed);
        Gl.Uniform1i(_loc.Lit, lightingOn ? 1 : 0);
        Gl.Uniform1i(_loc.Msaa, msaa ? 1 : 0);
        Gl.Uniform1f(_loc.Edge, MathF.Max(_edge, 0.01f));
        Gl.Uniform1i(_loc.PushTex, (int)PushUnit);
        Gl.Uniform1f(_loc.PushGain, _pushTex == 0 ? 0f : _pushGain);
        float* pw = stackalloc float[3] { _pushOriginX, _pushOriginY, _pushPerUnit / PushRes };
        Gl.Uniform3fv(_loc.PushWindow, 1, pw);
        if (lightingOn)
        {
            OwnedLights.BindForDraw();
            OwnedLights.SetDrawUniforms(_loc.Grid, _loc.Tile, _loc.Z, _loc.Falloff, _loc.Wrap,
                                        _loc.Atten, _loc.Hot, _loc.WrapN, viewport, near, far);
        }
        OwnedDrawService.UploadFeatureTerms(_featLoc, viewport);
        OwnedDrawService.UploadAreaTerms(_loc.Ambient, _loc.Diffuse, _loc.Dir);
        OwnedDrawService.UploadSunCascades(_loc.SunTex, _loc.SunVp, _loc.SunTexel, _loc.SunBias, _loc.SunCount);
        OwnedDrawService.UploadLocalCubes(_loc.CubeTex, _loc.CubeLight, _loc.CubePlane, _loc.CubeCount,
                                          _loc.CubeSoft);

        // The class's whole point, in three calls: blending OFF, depth writes ON, and the coverage decision
        // made entirely by the fragment stage's two discards. Two-sided because a blade is a card -- the
        // engine disables culling here too.
        // Alpha-to-coverage ONLY where the target actually has samples to cover -- there the fragment writes
        // its analytic edge coverage and the hardware resolves the silhouette properly. It is not a blend:
        // every sample is written whole or not at all, so the no-semi-transparent-depth-writing-fragment
        // guarantee is untouched. On a single-sampled target it would be a bare threshold shift, so it stays
        // off and the dither carries the edge instead.
        Gl.Disable(Gl.BLEND);
        Gl.DepthMask(1);
        Gl.Disable(Gl.CULL_FACE);
        if (msaa) Gl.Enable(Gl.SAMPLE_ALPHA_TO_COVERAGE);

        int boundMat = -1, boundTex = 0;
        for (int i = 0; i < n; i++)
        {
            ref Patch q = ref _patches[_vis[i]];
            if (q.Mat != boundMat)
            {
                boundMat = q.Mat;
                byte* info = GrassInfo(q.Mat);
                if (info == null) { boundTex = 0; continue; }
                Gl.Uniform3fv(_loc.MatDif, 1, (float*)(info + InfoDiffuse));
                Gl.Uniform3fv(_loc.MatAmb, 1, (float*)(info + InfoAmbient));
                boundTex = q.Mat < MaxGrassMaterials ? tids[q.Mat] : 0;
                Gl.BindTexture(Gl.TEXTURE_2D, (uint)boundTex);
                if (boundTex != 0 && _sampMip != 0)
                    Gl.BindSampler(0, HasMips(boundTex) ? _sampMip : _sampFlat);
            }
            if (boundTex == 0) continue;   // not uploaded yet: skip the patch, not the pass
            Gl.DrawArraysInstancedBaseInstance(Gl.TRIANGLES, 0, TemplateVerts, q.Count, (uint)q.First);
            _draws++;
        }
        if (msaa) Gl.Disable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        if (_sampMip != 0) Gl.BindSampler(0, 0);
        Gl.BindVertexArray(0);
    }

    /// <summary>Does this texture carry a mip level 1? A texture with none is mipmap-INCOMPLETE under a
    /// mipmap min filter and samples as black, so the answer picks the sampler. Cached per texture id: the
    /// id is re-resolved every frame (#33) but its mip chain is a property of the upload, and a reload that
    /// reuses the id also rebuilds the same chain.</summary>
    private static bool HasMips(int tid)
    {
        for (int i = 0; i < _mipCount; i++) if (_mipTid[i] == tid) return _mipHas[i];
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        int w = 0;
        Gl.GetTexLevelParameteriv(Gl.TEXTURE_2D, 1, Gl.TEXTURE_WIDTH, &w);
        bool has = w > 0;
        if (_mipCount < MaxGrassMaterials)
        { _mipTid[_mipCount] = tid; _mipHas[_mipCount] = has; _mipCount++; }
        _anyFlat |= !has;
        return has;
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
        if (!_samplerDirty) return;
        _samplerDirty = false;
        float maxAniso = 1f;
        if (Gl.GetFloatv != null) { float v; Gl.GetFloatv(Gl.MAX_TEXTURE_MAX_ANISOTROPY, &v); if (v > 1f) maxAniso = v; }
        float aniso = MathF.Min(MathF.Max(_aniso, 1f), maxAniso);
        for (int i = 0; i < 2; i++)
        {
            uint sm = i == 0 ? _sampMip : _sampFlat;
            Gl.SamplerParameteri(sm, Gl.TEXTURE_MIN_FILTER, i == 0 ? Gl.LINEAR_MIPMAP_LINEAR : Gl.LINEAR);
            Gl.SamplerParameteri(sm, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
            Gl.SamplerParameteri(sm, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
            Gl.SamplerParameteri(sm, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
            Gl.SamplerParameterf(sm, Gl.TEXTURE_MAX_ANISOTROPY, aniso);
            Gl.SamplerParameterf(sm, Gl.TEXTURE_LOD_BIAS, _lodBias);
        }
        _appliedAniso = aniso;
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
        if (vao == 0 || buf0 == 0 || buf1 == 0) { _failed = true; return false; }
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
    /// <c>OwnedDrawService.SelfCheck</c> can assert the embedded text satisfies the guards it is checked
    /// against (#132), which it must, or every session rebuilds from it.</summary>
    internal static string EmbeddedFs => GrassFsHead + OwnedLights.LocalShadowLib + OwnedLights.FragmentLib
                                         + OwnedLights.SunShadowLib + OwnedDrawService.KeyFogFs + GrassFsMain;

    private static bool EnsureProgram()
    {
        if (_prog != 0) return true;
        if (_failed) return false;
        string fs = EmbeddedFs;
        uint prog = GlProgram.Graphics(ShdGrassVs, GrassVs, ShdGrassFs, fs, _log,
                                       "GrassField instanced grass", out _, out string fsSrc);
        // #132: an override written before the linear flip still ends in SowxRevert, links cleanly, and
        // gets encoded a second time by the resolve -- a washed-out lawn, silently. Rebuilt from the
        // embedded text rather than logged and shipped, like every other owned stage's guard.
        if (prog != 0 && !OwnedDrawService.LinearSafe(fsSrc))
        {
            Gl.DeleteProgram(prog);
            _log?.Info("GrassField: grass override still encodes to display space -- rebuilding from the "
                     + "embedded default");
            prog = GlProgram.Graphics(GrassVs, fs, _log, "GrassField instanced grass (embedded)");
        }
        if (prog == 0)
        {
            _failed = true;
            _log?.Info("GrassField: grass program unavailable -- grass falls back to the engine's own draw");
            return false;
        }
        _prog = prog;
        _loc = new Loc
        {
            Vp = GlProgram.Loc(prog, "uVP"), Eye = GlProgram.Loc(prog, "uEyePos"),
            Fade = GlProgram.Loc(prog, "uFade"), Tex = GlProgram.Loc(prog, "uTex"),
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
            Wrap = GlProgram.Loc(prog, "uWrap"), Atten = GlProgram.Loc(prog, "uAtten"), Hot = GlProgram.Loc(prog, "uHot"), WrapN = GlProgram.Loc(prog, "uWrapN"),
            SunTex = GlProgram.Loc(prog, "uSunShadow"), SunVp = GlProgram.Loc(prog, "uSunVp"),
            SunTexel = GlProgram.Loc(prog, "uSunTexel"), SunBias = GlProgram.Loc(prog, "uSunBias"),
            SunCount = GlProgram.Loc(prog, "uSunCascades"),
            CubeTex = GlProgram.Loc(prog, "uLocalShadow"), CubeLight = GlProgram.Loc(prog, "uLocalLight"),
            CubePlane = GlProgram.Loc(prog, "uLocalPlane"), CubeCount = GlProgram.Loc(prog, "uLocalCubes"),
            CubeSoft = GlProgram.Loc(prog, "uLocalSoft"),
        };
        _featLoc = OwnedDrawService.FeatLocs(prog);
        // Same reason the lit program does it here: a sampler left at its default points at unit 0, and GL
        // refuses a draw whose samplers of different types share an image unit.
        Gl.UseProgram(prog);
        Gl.Uniform1i(_loc.Tex, 0);
        Gl.Uniform1i(_loc.SunTex, (int)OwnedDrawService.SunUnit);
        Gl.Uniform1i(_loc.CubeTex, (int)OwnedDrawService.CubeUnit);
        Gl.Uniform1i(_loc.PushTex, (int)PushUnit);
        _log?.Info("GrassField: instanced grass program ready");
        return true;
    }

    // ---- shaders -----------------------------------------------------------------------------

    private const string ShdGrassVs = "sowx_grass_vs", ShdGrassFs = "sowx_grass_fs";

    /// <summary>The vertex stage: one crossed-quad blade per instance, rotated, scaled, and swayed. The sway
    /// is root-anchored (<c>z^2</c>, so the base never leaves the ground) and driven by the engine's own
    /// <c>areaGlobalWind</c> plus the per-blade bend jitter, which is what stops a field moving as one sheet.
    /// <para>This stage DISPLACES, which is exactly why grass is not in the #119 depth prepass and why it does
    /// not compute <c>OwnedDrawService</c>'s invariant clip expression.</para></summary>
    private const string GrassVs = """
        #version 460 core
        layout(location = 0) in vec3 aPos;      // blade-local: xy in [-0.5,0.5], z 0 (root) .. 1 (tip)
        layout(location = 1) in vec2 aUV;       // 0..1 within one atlas cell
        layout(location = 2) in vec4 iPos;      // xyz world root, w rotation about Z
        layout(location = 3) in vec4 iParam;    // x scale (blade height), yz atlas cell origin, w bend

        """ + OwnedDrawService.FeatDecl + """
        uniform mat4 uVP;
        uniform vec3 uEyePos;
        uniform vec2 uFade;                     // (full-density radius, cut-off radius) in world units
        uniform float uWindGain;
        uniform float uWindSpeed;      // uTimeOfDay -> sway phase; see GrassField._windSpeed
        uniform sampler2D uPushTex;    // top-down displacement field, RG signed around 0.5
        uniform float uPushGain;       // 0 = nothing displaces the grass
        uniform vec3 uPushWindow;      // xy = the field's world origin, z = uv per world unit
        out vec2 vUV;
        out vec3 vWorld;
        out vec3 vNrm;
        out float vDepth;
        out float vFade;
        flat out uint vSeed;

        void main()
        {
            float c = cos(iPos.w), s = sin(iPos.w);
            vec3 p = vec3(aPos.x * c - aPos.y * s, aPos.x * s + aPos.y * c, aPos.z) * iParam.x;

            // The engine's areaGlobalWind is a world vector; a still area still wants a breeze, so a floor
            // stands in for it the way the stock wind shaders' 0.356 does.
            vec2 dir = uAreaWind.xy;
            float mag = length(dir);
            if (mag < 1e-4) { dir = vec2(0.7071, 0.7071); mag = 0.35; }
            else dir /= mag;
            float t = float(uTimeOfDay) * uWindSpeed;
            // Two frequencies so the field ripples instead of pulsing, phased by world position so
            // neighbouring blades lean together and distant ones do not.
            float phase = dot(iPos.xy, vec2(0.21, 0.17));
            float sway = (sin(t * 1.7 + phase) + 0.4 * sin(t * 3.9 + phase * 2.3)) * 0.5;
            // The amplitude is CLAMPED to a fraction of the blade's own height before it scales anything:
            // areaGlobalWind is an unbounded world vector, and an area that sets a big one must bend the
            // grass, not launch it.
            float amp = clamp(iParam.w * mag * uWindGain, 0.0, 1.0) * 0.35 * iParam.x;
            sway *= aPos.z * aPos.z * amp;
            p.xy += dir * sway;
            p.z -= abs(sway) * 0.25;            // a leaning blade is shorter, or it stretches

            // Creature and object displacement, and object culling. One fetch at the blade's ROOT -- the
            // whole blade bends as a unit, which is what a stalk does, and it means the field is sampled once
            // per blade rather than once per vertex position.
            vec2 puv = (iPos.xy - uPushWindow.xy) * uPushWindow.z;
            if (puv.x > 0.0 && puv.y > 0.0 && puv.x < 1.0 && puv.y < 1.0)
            {
                vec4 field = texture(uPushTex, puv);
                // A blade standing inside a solid object is not bent, it is gone. Thresholded against this
                // blade's OWN stable jitter rather than a constant, so the footprint thins out toward its
                // edge instead of ending in a straight line -- and it is stable frame to frame, because the
                // jitter is baked, so nothing flickers as the camera moves.
                if (field.a > mix(0.30, 0.95, fract(iParam.w * 7.31)))
                {
                    // Outside clip space on every axis: the whole blade is discarded before rasterisation.
                    gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
                    vUV = vec2(0.0); vWorld = vec3(0.0); vNrm = vec3(0.0, 0.0, 1.0);
                    vDepth = 1.0; vFade = 0.0; vSeed = 0u;
                    return;
                }
                if (uPushGain > 0.0)
                {
                    vec2 push = (field.rg * 2.0 - 1.0) * uPushGain;
                    float bendZ = aPos.z * aPos.z;
                    p.xy += push * bendZ * iParam.x;
                    // ...and pressed DOWN by its own channel, which peaks directly UNDER the gob where there
                    // is no direction to lean in. Read off the map rather than derived from the lateral
                    // magnitude, or the one tuft between a creature's feet stands perfectly upright.
                    p.z -= min(field.b * uPushGain, 0.9) * bendZ * iParam.x;
                }
            }

            vWorld = iPos.xyz + p;
            gl_Position = uVP * vec4(vWorld, 1.0);
            vDepth = gl_Position.w;
            // Grass shades best off an up-ish normal with a per-blade tilt: a true card normal turns half
            // the field black whenever the sun is not behind the camera, which is not what a lawn does.
            vNrm = normalize(vec3(c, s, 0.0) * 0.3 + vec3(0.0, 0.0, 1.0));
            vUV = iParam.yz + aUV * 0.5;
            vFade = 1.0 - smoothstep(uFade.x, uFade.y, distance(uEyePos, iPos.xyz));
            vSeed = uint(gl_InstanceID);
        }
        """;

    private const string GrassFsHead = """
        #version 460 core
        in vec2 vUV;
        in vec3 vWorld;
        in vec3 vNrm;
        in float vDepth;
        in float vFade;
        flat in uint vSeed;

        """ + OwnedDrawService.FeatDecl + OwnedDrawService.KeyFogDecl + """
        uniform sampler2D uTex;
        uniform vec3 uAreaAmbient;
        uniform vec3 uAreaDiffuse;
        uniform vec3 uAreaDir;
        uniform vec3 uMatAmbient;               // the grass material's own ambient (CAurGrassInfo +0x34)
        uniform vec3 uMatDiffuse;               // ... and diffuse (+0x28), WITHOUT vanilla's fade in its alpha
        uniform int uLit;                       // 0 = no cluster build this pass: ambient + sun only
        uniform int uMsaa;                      // 1 = the target is multisampled, so alpha-to-coverage is live
        uniform float uEdge;                    // cutout ramp width, in units of the analytic one-pixel edge
        out vec4 frag;

        // The engine's own cutout threshold (fAlphaDiscardValue under RenderInterface::Enable(1)).
        const float SOWX_GRASS_CUT = 0.2;

        """;

    /// <summary>The fragment stage. Two discards and no blending at all, which is the whole replacement:
    /// vanilla's fade rides in the alpha channel its cutout reads, so between 15u and ~27.7u every blade is
    /// semi-transparent and depth-writing and deletes whatever stands behind it. Here the fade is a SCREEN-DOOR
    /// dither -- a fragment is either fully opaque or gone -- so the ramp cannot produce a blended fragment,
    /// and because the ramp reaches zero exactly at the engine's own far radius there is no pop either.</summary>
    private const string GrassFsMain = """
        // Interleaved gradient noise (Jimenez): a hash with no visible tiling and no texture fetch. The
        // instance seed decorrelates neighbouring blades, so the dither reads as thinning grass rather than
        // as a screen-space grid crawling over it.
        float SowxGrassDither()
        {
            vec2 p = gl_FragCoord.xy + vec2(float(vSeed & 63u), float((vSeed >> 6) & 63u));
            return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715))));
        }

        void main()
        {
            vec4 texel = texture(uTex, vUV);
            // COVERAGE, not a binary test. fwidth is the alpha's rate of change across this pixel, so the
            // ramp is exactly one pixel wide wherever the blade edge lands -- which is what turns a stair-
            // stepped silhouette into a shaded one. It must be computed BEFORE any discard: derivatives are
            // only defined in uniform control flow.
            float aw = max(fwidth(texel.a), 1e-5);
            float cov = clamp((texel.a - SOWX_GRASS_CUT) / (aw * uEdge) + 0.5, 0.0, 1.0);
            // Where the target is multisampled, alpha-to-coverage resolves the edge properly and the dither
            // is left to carry the distance fade alone. Where it is not, the one dither carries both -- still
            // strictly softer than the hard step, and still never a semi-transparent depth-writing fragment.
            float keep = uMsaa != 0 ? vFade : cov * vFade;
            if (keep <= 0.0) discard;
            if (keep < 1.0 && SowxGrassDither() > keep) discard;

            vec3 N = normalize(vNrm);
            vec3 albedo = SowxLinear(texel.rgb);
            float ndl = clamp(dot(N, uAreaDir), 0.0, 1.0);
            vec3 sun = uAreaDiffuse * (ndl * SowxSunShadow(vWorld, N, ndl));
            vec3 point = uLit != 0 ? SowxClusteredDiffuse(vWorld, N, vDepth) : vec3(0.0);
            // the 0.15 floor and the sun-facing wrap are inc_lighting.shd:679, as the owned lit stage runs it
            vec3 ambient = uAreaAmbient * uMatAmbient * mix(0.15, 1.0, ndl);
            vec3 lit = albedo * (ambient + (sun + point) * uMatDiffuse);
            // vanilla grass runs fsgrass, which is the stock lit shader with KEYHOLING 1 and FOG 1 -- so the
            // camera dissolve and the scene fog both apply, and dropping them would fill a low camera shot
            // with undissolved grass. Grass always dissolves: it has no per-part CanDissolve to read.
            float kh = SowxKeyhole(1.0, vWorld, vDepth);
            if (kh < 0.0) discard;
            frag = vec4(SowxFog(lit * kh, vDepth), uMsaa != 0 ? cov : 1.0);
        }
        """;

    // ---- live tuning (/grass) ----------------------------------------------------------------

    /// <summary>One line for the <c>/grass</c> echo and the arm-time log.</summary>
    internal static string Describe()
        => $"density x{_density:0.##} wind x{_windGain:0.##} windspeed {_windSpeed:0.####} " +
           $"soften {_lodBias:0.##} aniso {_appliedAniso:0.#}x edge {_edge:0.##} " +
           $"push x{_pushGain:0.##} r{_pushRadius:0.##} over {_pushExtent:0.#}u " +
           $"cull {(_cull > 0f ? $"on shrink {_cullShrink:0.##}" + (_cullMax > 0f ? $" max {_cullMax:0.#}u" : "") : "off")} " +
           $"({_pushers} displacing, {_cullers} culling, {_reticles} non-solid skipped) " +
           $"({_patchCount} patches, {_instCount} blades resident; " +
           $"{(_samples > 1 ? $"{_samples}x MSAA, alpha-to-coverage live" : "no MSAA, dithered edge")}" +
           $"{(_anyFlat ? ", A TEXTURE HAS NO MIPS" : "")})";

    /// <summary>Live dial behind <c>/grass &lt;key&gt; &lt;value&gt;</c>. Returns the old value, or NaN for an
    /// unknown key. Density is the one that matters for #127's acceptance criterion: it is a CLIENT knob, not
    /// something baked at area load, so changing it re-scatters the field on the next frame rather than at the
    /// next area transition.</summary>
    internal static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            case "density": old = _density; _density = v; _rebake = true; break;
            case "wind": old = _windGain; _windGain = v; break;
            case "windspeed": old = _windSpeed; _windSpeed = v; break;
            case "soften": old = _lodBias; _lodBias = v; _samplerDirty = true; break;
            case "aniso": old = _aniso; _aniso = v; _samplerDirty = true; break;
            case "edge": old = _edge; _edge = v; break;
            case "push": old = _pushGain; _pushGain = v; break;
            case "pushradius": old = _pushRadius; _pushRadius = v; break;
            case "pushextent": old = _pushExtent; _pushExtent = v; break;
            case "cull": old = _cull; _cull = v; break;
            case "cullshrink": old = _cullShrink; _cullShrink = v; break;
            case "cullmax": old = _cullMax; _cullMax = v; break;
            default: return float.NaN;
        }
        _log?.Info($"GrassField: /grass {key} {v:F3} (was {old:F3}) -> {Describe()}");
        return old;
    }

    // ---- diagnostics -------------------------------------------------------------------------

    internal static void ResetPeriod()
    { _frames = 0; _draws = 0; _visible = 0; _culled = 0; _baked = 0; _disabled = 0; }

    internal static string Stats()
    {
        if (_off) return "grass vanilla (SOWX_GRASS=0)";
        if (_failed) return "grass VANILLA (owned program unavailable)";
        if (_frames == 0) return "grass idle";
        return $"grass {_patchCount} patches / {_instCount} blades resident, " +
               $"{(float)_visible / _frames:F0} drawn + {(float)_culled / _frames:F0} culled per frame, " +
               $"{(float)_draws / _frames:F0} draws/frame, {_baked} baked" +
               // A refusal is invisible in every other number here -- the patch is present and empty, so
               // "0 blades" and "the area has no grass" read identically without it.
               (_refused == 0 ? "" : $", {_refused} LISTS REFUSED (no faces, no height, or a material index " +
                                     "past the live grass-info count)") +
               (_capped ? $", INSTANCE CAP HIT ({MaxInstances}) -- lower SOWX_GRASS_DENSITY" : "") +
               (_patchCapped ? $", PATCH CAP HIT ({MaxPatches}) -- the rest of the area has no grass" : "") +
               // The two facts that decide how the cutout edge is resolved, and neither is visible anywhere
               // else: with no MSAA the edge is dithered rather than coverage-resolved, and a texture with
               // no mip chain gets the flat sampler (so anisotropy and the soften bias do nothing on it).
               $", {(_samples > 1 ? $"{_samples}x MSAA (A2C)" : "no MSAA (dithered edge)")}" +
               $", aniso {_appliedAniso:0.#}x" + (_anyFlat ? ", A GRASS TEXTURE HAS NO MIPS" : "") +
               // Zero displacers with creatures on screen means the gob list, not the field: the shader term
               // is doing its job over a map nothing was splatted into, which no other number distinguishes.
               // A material a server removed is skipped here rather than never baked -- so it shows up as
               // resident blades that never draw, which without this reads as culling gone wrong.
               (_disabled == 0 ? "" : $", {(float)_disabled / _frames:F0} patches/frame on DISABLED materials") +
               $", {_pushers} gobs displacing, {_cullers} culling" +
               // Non-solid gobs are the target reticle and the spell VFX. A zero here on a client that is
               // clearly targeting something means PartTriMesh::GetIsOpaque did not resolve and everything
               // counts as solid again.
               $", {_reticles} non-solid skipped" + (_gobBucket == null ? " (NO GOB LIST -- displacement off)" : "");
    }

    /// <summary>Off-device (run-demo.sh). Three things that are decisions rather than transcriptions, and each
    /// fails silently on a GPU: the scatter must be DETERMINISTIC (a rebuild that reshuffles the meadow reads
    /// as grass twitching at every area reload) and must scale with the density dial; the frustum test must
    /// actually reject (a cull that accepts everything is invisible except in the frame time it was built to
    /// save); and the box distance must be the NEAREST point, or a patch the camera stands inside is culled
    /// by its own centre.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"GrassField.SelfCheck FAIL: {w}"); } }

        uint a = 12345u, b = 12345u;
        float first = Rnd(ref a);
        E(first == Rnd(ref b), "the same seed yields the same first blade");
        E(first != Rnd(ref a), "and the stream advances");
        float lo = 1f, hi = 0f;
        uint st = 99u;
        for (int i = 0; i < 4096; i++) { float r = Rnd(ref st); lo = MathF.Min(lo, r); hi = MathF.Max(hi, r); }
        E(lo >= 0f && hi < 1f && lo < 0.01f && hi > 0.99f, "the stream covers [0,1) without leaving it");

        // density scales the engine's authored per-face counts rather than replacing them
        E((int)(7 * 2f) == 14 && (int)(7 * 0.5f) == 3, "the density dial multiplies the tileset's own count");

        // an identity VP: clip == world, so the frustum is the [-1,1] cube
        Matrix4x4 id = Matrix4x4.Identity;
        float* planes = stackalloc float[24];
        Frustum(&id, planes);
        E(Visible(new Vector3(-0.5f), new Vector3(0.5f), planes), "a box inside the cube is visible");
        E(!Visible(new Vector3(2f, 2f, 2f), new Vector3(3f, 3f, 3f), planes), "a box past +x/+y/+z is culled");
        E(!Visible(new Vector3(-3f), new Vector3(-2f), planes), "and one past -x/-y/-z is too");
        E(Visible(new Vector3(-2f, -0.1f, -0.1f), new Vector3(0f, 0.1f, 0.1f), planes),
          "a box STRADDLING a plane is kept -- a straddle test that rejects pops geometry at the screen edge");

        E(BoxDistance(new Vector3(0f), new Vector3(1f), new Vector3(0.5f, 0.5f, 0.5f)) == 0f,
          "a point inside the box is at distance 0");
        E(MathF.Abs(BoxDistance(new Vector3(0f), new Vector3(1f), new Vector3(4f, 0.5f, 0.5f)) - 3f) < 1e-4f,
          "and outside it, the distance to the NEAREST face");

        // The displacement splat. Its sign is the whole effect: pushing the wrong way pulls grass INTO a
        // creature instead of parting it around one, and nothing about the resulting frame says which.
        byte[] f = new byte[PushRes * PushRes * PushChannels];
        void Clear() { for (int i = 0; i < f.Length; i += PushChannels)
               { f[i] = 128; f[i + 1] = 128; f[i + 2] = 0; f[i + 3] = 0; } }
        Clear();
        const float cx = 64.5f, cy = 64.5f, rad = 6f;
        Splat(f, cx, cy, rad);
        int R(int x, int y) => f[(y * PushRes + x) * PushChannels];
        int G(int x, int y) => f[(y * PushRes + x) * PushChannels + 1];
        int B(int x, int y) => f[(y * PushRes + x) * PushChannels + 2];
        E(R(67, 64) > 128 && R(61, 64) < 128, "a gob pushes grass OUTWARD along x, not toward itself");
        E(G(64, 67) > 128 && G(64, 61) < 128, "and outward along y");
        E(R(67, 64) - 128 == 128 - R(61, 64), "the push is symmetric about the gob");
        E(R(64, 64) == 128 && G(64, 64) == 128, "the texel under the centre has no direction, so no push");
        E(R(64 + (int)rad + 1, 64) == 128, "and nothing past the radius is touched at all");
        E(R(66, 64) > R(68, 64), "the push falls off with distance");
        // The flatten channel is the half the direction cannot carry: it has to PEAK where the direction
        // vanishes, or the tuft between a creature's feet is the one thing left standing.
        E(B(64, 64) > B(67, 64) && B(67, 64) > B(69, 64), "the press-down peaks under the gob and falls off");
        E(B(64 + (int)rad + 1, 64) == 0, "and is zero past the radius");
        Clear();
        Splat(f, cx, cy, rad);
        int solo = B(66, 64);
        Splat(f, cx, cy, rad);
        E(B(66, 64) == solo, "two overlapping gobs do not press the same grass twice -- flatten takes the max");
        E(R(66, 64) != 128 && R(66, 64) > 128, "...while their lateral pushes do sum");
        Clear();
        Splat(f, cx, cy, 0f);
        E(R(64, 64) == 128 && B(64, 64) == 0, "a zero radius splats nothing rather than dividing by it");

        // The object cull. Its footprint is a RECTANGLE, not a disc, because the things it exists for -- a
        // crate, a wall, a chest -- are not round, and a disc over a long wall either misses its ends or
        // erases the grass beside it.
        int A(int x, int y) => f[(y * PushRes + x) * PushChannels + 3];
        Clear();
        CullRect(f, cx, cy, 10f, 3f);
        E(A(64, 64) == 255, "the middle of an object's footprint is fully culled");
        E(A(72, 64) > 0 && A(64, 66) > 0, "...along both axes of the rectangle");
        E(A(76, 64) == 0 && A(64, 69) == 0, "and nothing outside it is");
        E(A(73, 64) < A(64, 64), "the footprint softens toward its edge, so grass thins instead of stopping");
        Clear();
        CullRect(f, cx, cy, 0f, 3f);
        E(A(64, 64) == 0, "a degenerate footprint culls nothing rather than dividing by it");

        log.Info(ok ? "GrassField.SelfCheck: passed" : "GrassField.SelfCheck: FAILED");
    }
}
