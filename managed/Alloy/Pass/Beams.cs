using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Alloy.SceneLayout;

namespace Sowx.Alloy;

/// <summary>
/// #130 (map #68, spec #114 M3, ADR 0011): volumetric light beams, captured off the engine's own draw and
/// replayed by the Alloy pass at its graph slot -- the last ordinary content class of the colour pass.
/// <para>Vanilla draws them from <c>Scene::RenderBeams</c> @0x46aba0 (Scene vt+0x1d8), step 27 of
/// <c>Scene::RenderSinglePass</c>, immediately after the additive bucket. They are not draw-bucket residents
/// in the sense the Alloy pass knows: the walk is
/// <c>DrawBucketManager::GetFirstItem(Scene+0x208, 0xf, 0)</c> -- bucket <b>15</b>, which
/// <c>Scene::RenderDrawBucket</c> never touches -- so no part of the existing capture machinery has ever seen
/// one. What IS in the bucket are <c>PartBeam</c>s: static geometry built once per area by
/// <c>BSPCreateStaticProjections</c> @0x47e890 (the same bake that makes <c>PartShadow</c>, chosen by the
/// model's own flag at <c>Model+0x114</c>), so a beam is a resident VDO and not a per-frame simulation.</para>
/// <para>The arrangement is <see cref="SkyBox"/>'s and for the same reasons: the engine keeps running its
/// whole routine under <c>glColorMask(0,0,0,0)</c> once the Alloy pass has drawn, so capture stays warm, the
/// state prologue/epilogue still runs for whatever follows, and an area transition needs no code. The engine
/// already draws beams with the depth mask off, so a colour-masked pass writes nothing at all.</para>
/// <para>ponytail: replay is one frame late, because the capture happens at the vanilla site (step 27) and
/// the Alloy pass runs at #132's frame anchor. Beam geometry is static per area, so the only staleness is
/// the first frame after a transition -- which falls back to a visible engine draw anyway, since a beam with
/// no capture is refused. Upgrade path, if it ever matters: walk bucket 15 ourselves at the Alloy slot.</para>
/// </summary>
internal static unsafe class Beams
{
    /// <summary>The only two fields <c>PartBeam::Draw</c> @0x450120 reads -- its whole body is
    /// <c>RenderVertexData(part+0xa0, part+0xa8, 4, 0xffffffff)</c>. So the capture needs no funnel arm: the
    /// draw's arguments are readable straight off the part.</summary>
    private const int PartVdo = 0xa0, PartItem = 0xa8;
    /// <summary>The Aurora primitive id <c>PartBeam::Draw</c> hard-codes.</summary>
    private const int BeamPrim = 4;
    /// <summary>Scene's shadow-light list, the one <c>RenderBeams</c> scans for the beam direction:
    /// <c>PartLight**</c> at +0x60, count at +0x68.</summary>
    private const int SceneLights = 0x60, SceneLightCount = 0x68;
    /// <summary>The PartLight live flag at +0x74 -- the one field of the light <c>RenderBeams</c>'s scan
    /// reads that no other walker does. Range, kind and direction are shared, in <see cref="SceneLayout"/>.</summary>
    private const int LightLive = 0x74;
    /// <summary>The range above which a light is the sun/moon (<c>_DAT_013f04a8</c> = 1000.0f, the same
    /// number <c>ShadowService.DirectionalRange</c> carries). Beams are extruded from the FIRST scene light
    /// that clears it, which is what makes them sun shafts rather than per-lamp volumes.</summary>
    private const float DirectionalRange = 1000f;
    /// <summary><c>projectionWorldZClip</c>: the world z every back-facing beam vertex is extruded down to.
    /// <para><c>RenderBeams</c> @0x46add0 is <c>movss xmm0,[0x12f04a4]; mov edi,1; call SetShaderParamf</c> --
    /// the <c>edi</c> is the shader-constant INDEX and the value is the .rodata float, <b>-20.0</b>. Read the
    /// call as one-argument (Ghidra's decompiler drops the float) and it looks like a literal 1.0, which
    /// truncates every shaft ~21/|dir.z| units short and leaves it hanging in mid-air. The index-1 constant
    /// reaches <c>projectionWorldZClip</c> through <c>GLRender::SetUniforms</c> @0x4ce7ed
    /// (<c>GetShaderParamf(1)</c> -&gt; the location <c>ConnectShaderData</c> caches at PROG_FIXED+0x1d38),
    /// the same way the index-0 vec4 reaches <c>projectionSource</c>.</para></summary>
    private const float WorldZClip = -20f;
    /// <summary>The beam tint. <c>RenderBeams</c> builds <c>globalColor</c> as
    /// <c>Homogenize(DAT_01aab9e8, projectionalpha)</c>, and <c>DAT_01aab9e8</c> is written once by a static
    /// initialiser (<c>_INIT_14</c> @0x51739a) to {1,1,1} and by nothing else. The alpha is read live off
    /// <c>projectionalpha</c>, which IS a console variable.</summary>
    private const float TintRgb = 1f;
    /// <summary>Fallback alpha when <c>projectionalpha</c> does not resolve -- the value the engine's own
    /// initialiser writes.</summary>
    private const float DefaultAlpha = 0.2f;
    private const uint GlUnsignedShort = 0x1403;
    /// <summary>The engine attribute locations a beam VDO carries, from <c>GLRender::ConnectShaderData</c>'s
    /// bind table @0x4ce8e0: <c>vPos</c> at 0 and <c>fProjectionFrontal</c> at 10. That pair is why beams
    /// cannot ride <c>AlloyRenderer</c>'s VAO cache -- it harvests position/UV/normal and refuses a VDO
    /// with no UV stream, which is every beam.</summary>
    private const uint EngPos = 0, EngFrontal = 10;
    /// <summary>...and ours, which the Alloy VAO binds them at.</summary>
    private const uint LocPos = 0, LocFrontal = 1;
    /// <summary>Aurora blend factors <c>RenderBeams</c> sets: <c>SetBlendFunc(0, 2)</c> =
    /// (GL_SRC_ALPHA, GL_ONE).</summary>
    private const uint GlSrcAlpha = 0x0302, GlOne = 1;
    /// <summary>A handful of static volumes per area; past this a beam is left to the engine.</summary>
    private const int MaxBeams = 2048, MaxVdos = 512;
    /// <summary>The matrix stack: model is index 2, stride 0x58, top at +0x18 (same layout
    /// <see cref="SkyBox"/> reads). <c>RenderBeams</c> pushes nothing, so the top at capture IS the transform
    /// the engine's own draw used -- reading it is a replay, not a reconstruction.</summary>
    private const int ModeModel = 2, StackStride = 0x58, StackTop = 0x18;

    /// <summary>One captured beam. Everything but the matrix is re-validated at draw time.</summary>
    internal struct Beam
    {
        public IntPtr Part, Vdo;
        public int Item, Vao, Frame;
        public Matrix4x4 Model;
    }

    private static readonly Beam[] _beams = new Beam[MaxBeams];
    private static int _count;

    // draw arguments resolved by Stage(), one slot per beam issued this pass. Allocated once: the draw path
    // is inside the colour bracket and allocates nothing.
    private static readonly int[] _sBeam = new int[MaxBeams], _sCount = new int[MaxBeams], _sBase = new int[MaxBeams];
    private static readonly nint[] _sOff = new nint[MaxBeams];

    // per-VDO VAO cache, ours alone: two attributes, harvested in the ENGINE context at capture and built in
    // the 4.6 one at draw (VAOs do not share across contexts; buffers do -- #70).
    private static readonly IntPtr[] _vVdo = new IntPtr[MaxVdos];
    private static readonly uint[] _vVao = new uint[MaxVdos];
    private static readonly uint[] _vElemBuf = new uint[MaxVdos];
    private static readonly VertexStream[] _vPos = new VertexStream[MaxVdos], _vFr = new VertexStream[MaxVdos];
    private static readonly bool[] _vStale = new bool[MaxVdos];
    private static int _vdoCount;

    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, void> _origRender, _origDraw;
    private static byte* _stacks;
    private static int* _enable;
    private static float* _alpha;
    private static uint* _primTable;
    private static bool _off, _failed;
    /// <summary>The player's own switch (<c>/beams</c>, ADR 0013 bucket 1). Distinct from <see cref="_off"/>,
    /// which means the session cannot own beams at all: the hooks arm either way, so the toggle is live.
    /// Volatile -- written from the command dispatch, read in the capture window on the render thread.</summary>
    private static volatile bool _disabled;
    private static uint _prog;
    private static int _locModel = -1, _locView = -1, _locProj = -1, _locSrc = -1, _locZClip = -1, _locColor = -1;
    private static int _frame, _drawnGen = -1, _masked, _captured, _foreign, _refused, _drawn, _standdown;
    /// <summary>Bucket-15 beams the engine walked in the current capture window, and in the last one --
    /// refused ones included. The replay compares against it: a beam the table never took is one the engine
    /// would have drawn and the colour mask would have deleted, so the population has to match before
    /// anything is masked. <see cref="Stage"/>'s all-or-none covers the table; this covers what never
    /// reached it.</summary>
    private static int _seen, _lastSeen;

    /// <summary>True between <c>Scene::RenderBeams</c>'s entry and exit: <c>PartBeam::Draw</c> only captures
    /// inside it, so a beam drawn from anywhere else is left alone.</summary>
    private static bool _inWindow;
    /// <summary>The Scene whose beams were captured, so a new area drops the table rather than replaying
    /// volumes whose parts the unload freed.</summary>
    private static IntPtr _scene;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _stacks = (byte*)Native.Sym(SymbolMap.MAurMtxStack);
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _enable = (int*)Native.Sym(SymbolMap.EnableBeams);
        _alpha = (float*)Native.Sym(SymbolMap.ProjectionAlpha);
        if (_stacks == null || _primTable == null)
        { _off = true; log.Info("Beams: matrix stack or prim table unresolved -- light beams stay vanilla"); return; }
        _origRender = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.RenderBeams, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnRenderBeams);
        _origDraw = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.PartBeamDraw, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnPartBeamDraw);
        if (_origRender == null || _origDraw == null)
        { _off = true; log.Info("Beams: Scene::RenderBeams / PartBeam::Draw did not hook -- light beams stay vanilla"); }
        else log.Info("Beams: armed -- bucket-15 volumes replayed at the Alloy additive slot (/beams toggles)");
    }

    /// <summary>The vanilla site. Same shape as <see cref="SkyBox.OnRenderSkyBoxes"/>: let the engine run its
    /// whole routine, colour-masked on the passes the Alloy block already drew the beams, so the capture and
    /// the state prologue/epilogue both keep happening and nothing lands on screen twice.</summary>
    [UnmanagedCallersOnly]
    private static void OnRenderBeams(IntPtr scene)
    {
        if (_origRender == null) return;
        // A GUI 3D panel drives the same Scene virtuals as the world (#106/#125), and RenderSinglePass calls
        // this unconditionally -- so an open inventory reaches here every frame with its own Scene.
        if (!AlloyRenderer.IsWorldScene(scene)) { _origRender(scene); _foreign++; return; }
        // /beams off: idle, never uninstalled. The table goes on the first such frame -- Fresh() is a
        // sighting window and _frame stops advancing here, so a kept table would replay stale volumes.
        if (_disabled)
        {
            if (_count != 0) Forget();
            _inWindow = false;
            _origRender(scene);
            return;
        }
        if (scene != _scene) Forget();
        _scene = scene;
        bool ours = AlloyRenderer.DrewThisPass;
        // Never mask on a stale flag: the Alloy pass stamps the CURRENT pass generation, so a pass that
        // staged nothing falls back to a visible engine beam rather than to none.
        bool mask = ours && _drawnGen == AlloyRenderer.PassGen && Gl.ColorMask != null;
        _inWindow = ours;
        if (ours) _seen = 0;
        if (mask) { Gl.ColorMask(0, 0, 0, 0); _masked++; }
        _origRender(scene);
        if (mask) Gl.ColorMask(1, 1, 1, 1);
        _inWindow = false;
        if (ours) { _lastSeen = _seen; _frame++; }
    }

    /// <summary>One beam is about to draw vanilla: record its VDO, its item and the live model matrix. The
    /// draw arguments are fields on the part, so there is nothing to observe at the funnel.</summary>
    [UnmanagedCallersOnly]
    private static void OnPartBeamDraw(IntPtr part)
    {
        // Counted BEFORE the capture, so a beam Capture() refuses still shows up in the population the
        // replay has to match. A refusal that did not count would read as "nothing to own here".
        if (_inWindow && part != IntPtr.Zero) { _seen++; Capture(part); }
        if (_origDraw != null) _origDraw(part);
    }

    private static void Capture(IntPtr part)
    {
        IntPtr vdo = *(IntPtr*)(part + PartVdo);
        int item = *(int*)(part + PartItem);
        // Only the indexed-static path, which is what every resident VDO in this renderer is. A refusal is
        // not a per-beam fallback -- the colour mask is whole-pass, so there is no such thing here; it makes
        // the TABLE smaller than the population, and Draw() stands the whole class down when those differ.
        if (vdo == IntPtr.Zero || *(int*)(vdo + VdoMode) != 1) { _refused++; return; }
        int s = -1;
        for (int i = 0; i < _count; i++) if (_beams[i].Part == part) { s = i; break; }
        if (s < 0)
        {
            if (_count >= MaxBeams) { _refused++; return; }
            s = _count++;
            _captured++;
        }
        int v = VdoSlot(vdo);
        if (v < 0) { _refused++; return; }
        ref Beam b = ref _beams[s];
        b.Part = part; b.Vdo = vdo; b.Item = item; b.Vao = v;
        b.Model = *(Matrix4x4*)(_stacks + ModeModel * StackStride + StackTop);
        b.Frame = _frame;
    }

    /// <summary>Find (or harvest) the VAO-cache slot for a VDO. Harvesting reads the ENGINE VAO's attribute
    /// formats, so it has to run here (inside the engine's own draw) and not at replay time.</summary>
    private static int VdoSlot(IntPtr vdo)
    {
        for (int i = 0; i < _vdoCount; i++) if (_vVdo[i] == vdo) return i;
        if (_vdoCount >= MaxVdos) return -1;
        int s = _vdoCount;
        if (!Harvest(vdo, s)) return -1;
        _vVdo[s] = vdo; _vStale[s] = _vVao[s] != 0;
        _vdoCount++;
        return s;
    }

    private static bool Harvest(IntPtr vdo, int s)
    {
        uint vao = *(uint*)(vdo + VdoVao);
        if (vao == 0 || Gl.GetVertexAttribiv == null) return false;
        int saved;
        Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &saved);
        Gl.BindVertexArray(vao);
        int eb; Gl.GetIntegerv(Gl.ELEMENT_ARRAY_BUFFER_BINDING, &eb);
        _vElemBuf[s] = (uint)eb;
        VertexArrays.Read(EngPos, out _vPos[s]);
        VertexArrays.Read(EngFrontal, out _vFr[s]);
        Gl.BindVertexArray((uint)saved);
        // Both streams or nothing: the extrusion branches on fProjectionFrontal, and a volume drawn as if
        // every vertex were frontal is a flat sliver at the minimum offset rather than a shaft.
        return _vElemBuf[s] != 0 && _vPos[s].Present && _vFr[s].Present;
    }

    private static bool EnsureVao(int v)
    {
        if (_vVao[v] != 0 && !_vStale[v]) return true;
        if (_vVao[v] != 0) { uint old = _vVao[v]; Gl.DeleteVertexArrays(1, &old); _vVao[v] = 0; }
        uint vao; Gl.GenVertexArrays(1, &vao);
        Gl.BindVertexArray(vao);
        VertexArrays.Bind(LocPos, in _vPos[v]);
        VertexArrays.Bind(LocFrontal, in _vFr[v]);
        Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _vElemBuf[v]);
        Gl.BindVertexArray(0);
        _vVao[v] = vao; _vStale[v] = false;
        return true;
    }

    /// <summary>The direction <c>RenderBeams</c> extrudes along: the first scene light with range >= 1000
    /// that is live and of kind 0, negated. Returns false when no such light exists, which is every interior
    /// -- and the engine's own routine draws nothing then either.</summary>
    internal static bool Direction(IntPtr scene, out Vector3 dir)
    {
        dir = default;
        if (scene == IntPtr.Zero) return false;
        IntPtr* lights = *(IntPtr**)(scene + SceneLights);
        int n = *(int*)(scene + SceneLightCount);
        if (lights == null || n <= 0) return false;
        for (int i = 0; i < n; i++)
        {
            IntPtr l = lights[i];
            if (l == IntPtr.Zero) continue;
            if (*(float*)(l + LightRange) < DirectionalRange) continue;
            if (*(int*)(l + LightLive) == 0 || *(int*)(l + LightKind) != 0) continue;
            var d = *(Vector3*)(l + LightDir);
            if (d.LengthSquared() <= 0f) return false;
            dir = -Vector3.Normalize(d);
            return true;
        }
        return false;
    }

    /// <summary>ADR 0011 (#130): the Alloy beam pass, right after the additive class and before the probe
    /// capture -- vanilla's own position (step 27, straight after bucket 4). Additive
    /// <c>(SRC_ALPHA, ONE)</c>, depth test on, depth mask off, exactly as <c>RenderBeams</c> sets it.
    /// <para>Leaves GL_BLEND off and the depth mask on, which is the state
    /// <see cref="AlloyRenderer"/>'s additive arm hands over and the probe capture after it needs.</para></summary>
    internal static void Draw(Matrix4x4* view, Matrix4x4* proj)
    {
        if (_disabled || _off || _failed || _count == 0 || Gl.MultiDrawElementsBaseVertex == null) return;
        // The player's own switch, honoured the way grass honours "Grass Mode": beams off means the owned
        // pass draws nothing AND suppresses nothing, so the console variable keeps meaning what it says.
        if (_enable != null && *_enable == 0) return;
        // The Scene the CAPTURE ran on, not the one enrollment holds: the volumes were baked against
        // this Scene's own light list, and reading another's would extrude them along a direction the
        // geometry was never built for. Forget() clears it, so an area transition draws nothing until the
        // engine's (unmasked) pass warms the table again.
        if (!Direction(_scene, out Vector3 dir)) return;
        if (!EnsureProgram()) return;

        // All-owned-or-none (#107 section 2): stage EVERY fresh beam before issuing any of them. A beam that
        // fails here while its neighbours draw would be masked out of the engine's pass and simply missing,
        // with the frame looking merely dimmer -- so one failure hands the whole class back for the pass, at
        // the cost of one visible vanilla beam pass and no hole at all.
        int staged = 0;
        for (int i = 0; i < _count; i++)
        {
            if (!Fresh(i)) continue;
            if (!Stage(i, staged)) { _refused++; return; }
            _sBeam[staged++] = i;
        }
        // ...and the same rule over the POPULATION, not just the table. A beam refused at capture (an
        // unindexed VDO, no attribute pair, the table full) is absent from _beams entirely, so the staging
        // loop above cannot see it -- yet the colour mask is whole-pass, so masking would delete the engine's
        // copy of it too. The last window's walk count is what says whether the table is the whole class.
        if (staged == 0 || staged != _lastSeen) { if (staged != 0) _standdown++; return; }

        Gl.UseProgram(_prog);
        Gl.UniformMatrix4fv(_locView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_locProj, 1, 0, (float*)proj);
        float alpha = _alpha != null ? *_alpha : DefaultAlpha;
        // #132: the Alloy target is LINEAR, so the stock tint -- a display-space grey -- is linearised
        // here rather than in the stage. Additive over a linear frame is the whole point of the flip.
        float lin = MathF.Pow(TintRgb, AlloyLights.SrgbGamma);
        float* c = stackalloc float[4] { lin, lin, lin, alpha };
        Gl.Uniform4fv(_locColor, 1, c);
        float* src = stackalloc float[4] { dir.X, dir.Y, dir.Z, -1f };   // w < 0 = the directional branch
        Gl.Uniform4fv(_locSrc, 1, src);
        Gl.Uniform1f(_locZClip, WorldZClip);
        Gl.Enable(Gl.BLEND);
        Gl.BlendFunc(GlSrcAlpha, GlOne);
        Gl.DepthMask(0);

        int boundVao = -1;
        fixed (int* counts = _sCount, bases = _sBase)
        fixed (nint* offs = _sOff)
        for (int k = 0; k < staged; k++)
        {
            ref Beam b = ref _beams[_sBeam[k]];
            if (b.Vao != boundVao) { Gl.BindVertexArray(_vVao[b.Vao]); boundVao = b.Vao; }
            fixed (Matrix4x4* m = &b.Model) Gl.UniformMatrix4fv(_locModel, 1, 0, (float*)m);
            Gl.MultiDrawElementsBaseVertex(_primTable[BeamPrim], counts + k, GlUnsignedShort,
                                           (void**)(offs + k), 1, bases + k);
        }
        Gl.BindVertexArray(0);
        Gl.Disable(Gl.BLEND);
        Gl.DepthMask(1);
        _drawn += staged;
        // Set only after the Alloy beams actually reached the GPU this pass, never on intent -- the vanilla
        // site reads it to decide whether to colour-mask the engine's own copy.
        _drawnGen = AlloyRenderer.PassGen;
    }

    /// <summary>Resolve one beam's draw arguments off its VDO's own item tables and park them in slot
    /// <paramref name="k"/>. False means this beam cannot be drawn, which per the all-or-none rule stands the
    /// whole class down for the pass.</summary>
    private static bool Stage(int i, int k)
    {
        ref Beam b = ref _beams[i];
        long elem = (long)*(IntPtr*)(b.Vdo + VdoElemItems), vert = (long)*(IntPtr*)(b.Vdo + VdoVertItems);
        if (elem == 0 || vert == 0 || b.Item < 0 || b.Item >= *(int*)(b.Vdo + VdoElemCount)
            || b.Item >= *(int*)(b.Vdo + VdoVertCount)) return false;
        int count = *(int*)(elem + b.Item * 8 + 4);
        if (count <= 0 || !EnsureVao(b.Vao)) return false;
        _sCount[k] = count;
        _sOff[k] = (nint)((long)(uint)*(int*)(elem + b.Item * 8) * 2);
        _sBase[k] = *(int*)(vert + b.Item * 8);
        return true;
    }

    /// <summary>Was this beam captured on the frame just gone? A part the area unload freed leaves a stale
    /// entry, and replaying one draws through a dangling VDO.</summary>
    private static bool Fresh(int i) => _beams[i].Vdo != IntPtr.Zero && _beams[i].Frame >= _frame - 1;

    private static bool EnsureProgram()
    {
        if (_prog != 0) return true;
        if (_failed) return false;
        _prog = GlProgram.Graphics(BeamVs, BeamFs, _log, "Beams");
        if (_prog == 0) { _failed = true; _log?.Info("Beams: program build failed -- light beams stay vanilla"); return false; }
        _locModel = GlProgram.Loc(_prog, "uModel");
        _locView = GlProgram.Loc(_prog, "uView");
        _locProj = GlProgram.Loc(_prog, "uProj");
        _locSrc = GlProgram.Loc(_prog, "uProjSrc");
        _locZClip = GlProgram.Loc(_prog, "uZClip");
        _locColor = GlProgram.Loc(_prog, "uColor");
        return true;
    }

    /// <summary>#88's <c>VertexDataObject::Repack</c> reached a VDO. A repack moves item offsets and can
    /// replace the buffers a harvested VAO points at, and this cache holds both -- so the whole table goes.
    /// <para>Wholesale rather than per-slot on purpose: beam volumes are baked once per area by
    /// <c>BSPCreateStaticProjections</c> and should never repack at all, so this is a correctness backstop
    /// on an event that costs one vanilla beam pass when it does fire. Cheap to be blunt about.</para></summary>
    internal static void OnRepack(IntPtr vdo)
    {
        for (int v = 0; v < _vdoCount; v++) if (_vVdo[v] == vdo) { Forget(); return; }
    }

    /// <summary>Every captured identity is void: the parts belong to a Scene whose unload freed them.</summary>
    /// <summary>Context loss: drop every GL name (they mean nothing once the context is gone) and the
    /// captured world with them. <see cref="EnsureProgram"/> short-circuits on a non-zero <c>_prog</c> and
    /// the draw binds <c>_vVao</c> directly, so leaving these set is what made the next context draw through
    /// a dead program and a dead VAO (#175 follow-up).</summary>
    internal static void ContextLost()
    {
        _prog = 0; _failed = false;
        _locModel = -1; _locView = -1; _locProj = -1; _locSrc = -1; _locZClip = -1; _locColor = -1;
        for (int v = 0; v < MaxVdos; v++) { _vVao[v] = 0; _vElemBuf[v] = 0; }
        Forget();
    }

    internal static void Forget()
    {
        _count = 0;
        _drawnGen = -1;
        _scene = IntPtr.Zero;
        for (int v = 0; v < _vdoCount; v++) { _vVdo[v] = IntPtr.Zero; _vStale[v] = true; }
        _vdoCount = 0;
    }

    /// <summary>The <c>/beams</c> body: flip the Alloy beam pass and say where it landed. Cold (input
    /// thread); the render thread picks the flag up in its next capture window.</summary>
    internal static string Toggle()
    {
        if (_off || _origRender == null) return "[beams] unavailable this session -- light beams stay vanilla";
        _disabled = !_disabled;
        return $"[beams] Alloy beams {(_disabled ? "off -- the engine draws them" : "on")}";
    }

    internal static void ResetPeriod()
    { _masked = 0; _captured = 0; _foreign = 0; _refused = 0; _drawn = 0; _standdown = 0; }

    internal static string Stats() => _disabled ? "beams off (/beams)" : _off ? "beams vanilla (unavailable)"
        : $"beams {_count} parts, {_captured} captured, {_drawn} drawn, {_masked} masked"
          + (_refused == 0 ? "" : $", {_refused} REFUSED (unindexed VDO, no attribute pair, or table full)")
          + (_standdown == 0 ? "" : $", {_standdown} passes HANDED BACK whole (owned {_count} of {_lastSeen} walked)")
          + (_foreign == 0 ? "" : $", {_foreign} foreign-Scene calls skipped");

    /// <summary>Off-device: the two pieces of this file that are decisions rather than replays -- the light
    /// scan's predicate (miss it and the shafts point somewhere the volumes were never baked for) and the
    /// extrusion offset the vertex stage computes (which the shader cannot be asked about at run-demo time).
    /// </summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"Beams.SelfCheck FAIL: {w}"); } }

        const int SceneBytes = 0x80, LightBytes = LightDir + 12;
        byte* scene = stackalloc byte[SceneBytes];
        for (int i = 0; i < SceneBytes; i++) scene[i] = 0;
        byte* a = stackalloc byte[LightBytes];
        byte* b = stackalloc byte[LightBytes];
        for (int i = 0; i < LightBytes; i++) { a[i] = 0; b[i] = 0; }
        IntPtr* list = stackalloc IntPtr[2];
        list[0] = (IntPtr)a; list[1] = (IntPtr)b;
        *(IntPtr**)(scene + SceneLights) = list;
        *(int*)(scene + SceneLightCount) = 2;
        IntPtr s = (IntPtr)scene;

        void Light(byte* l, float range, int live, int kind, float x, float y, float z)
        {
            *(float*)(l + LightRange) = range; *(int*)(l + LightLive) = live; *(int*)(l + LightKind) = kind;
            *(Vector3*)(l + LightDir) = new Vector3(x, y, z);
        }

        Light(a, 10f, 1, 0, 0f, 0f, 1f);      // a point light: too short a range
        Light(b, 100000f, 1, 0, 0f, 0f, 2f);  // the sun
        E(Direction(s, out Vector3 d) && d == new Vector3(0f, 0f, -1f),
          "the first light past 1000 range wins, normalised and NEGATED");

        Light(b, 100000f, 0, 0, 0f, 0f, 2f);
        E(!Direction(s, out _), "a light with the +0x74 flag clear is skipped");
        Light(b, 100000f, 1, 1, 0f, 0f, 2f);
        E(!Direction(s, out _), "...and so is one whose +0x80 discriminant is non-zero");
        Light(b, 100000f, 1, 0, 0f, 0f, 0f);
        E(!Direction(s, out _), "a zero direction is refused rather than normalised into a NaN");
        *(int*)(scene + SceneLightCount) = 0;
        E(!Direction(s, out _), "an empty light list draws no beams -- which is every interior");

        // The vertex stage's extrusion, mirrored: a frontal vertex sits at the minimum offset, a back-facing
        // one extrudes until world z reaches the clip plane, and the minimum always wins as a floor. Getting
        // the max() backwards inverts the shaft above the clip height, which is most of an outdoor scene.
        const float MinOffset = 0.1f;
        static float Off(float frontal, float zClip, float wz, float dirZ)
            => frontal > 0f ? MinOffset : MathF.Max(MinOffset, (zClip - wz) / dirZ);
        E(Off(1f, WorldZClip, 50f, -1f) == MinOffset, "a frontal vertex extrudes by the minimum offset only");
        E(Off(0f, WorldZClip, 11f, -1f) == 31f, "a back-facing vertex extrudes down to the world z clip");
        E(Off(0f, WorldZClip, -19.95f, -1f) == MinOffset,
          "...and never less than the minimum, however close it starts");
        // The clip plane is BELOW the world floor, and that is the whole of it: a positive one truncates
        // every shaft in mid-air, which is exactly what reading SetShaderParamf as one-argument produces.
        E(WorldZClip < 0f && Off(0f, WorldZClip, 0f, -1f) > 1f,
          "the clip plane is under the floor, so a shaft at ground level still has length");

        E(BeamPrim == 4 && WorldZClip == -20f && DirectionalRange == 1000f,
          "the three literals RenderBeams pushes: prim 4, projectionWorldZClip -20.0, range threshold 1000");

        log.Info(ok ? "Beams.SelfCheck: passed" : "Beams.SelfCheck: FAILED");
    }

    // ---- shaders -----------------------------------------------------------------------------

    /// <summary>The vertex stage, ported from the client's own <c>vs_beamvol.shd</c>. Only the DIRECTIONAL
    /// branch survives: <c>RenderBeams</c> pushes <c>projectionSource.w = -1</c> unconditionally, so the
    /// point-light path (extrude by radius minus distance) is dead code at this call site.
    /// <para>Two deliberate departures from the stock stage. The fog term is dropped -- the Alloy pass fogs
    /// through <c>AlloyRenderer</c>'s own block and an additive shaft is the one class where losing it is
    /// invisible (ponytail: add the term if a foggy exterior ever shows a shaft standing too proud). And the
    /// stage does NOT carry #119's invariant clip expression, because beams are additive, never enter the
    /// depth prepass, and write no depth -- there is no LEQUAL match to preserve.</para></summary>
    private const string BeamVs = """
        #version 460 core
        layout(location = 0) in vec4 aPos;
        layout(location = 1) in float aFrontal;   // fProjectionFrontal: > 0 = the cap facing the light

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        uniform vec4 uProjSrc;   // xyz = extrusion direction in world space (w < 0: directional)
        uniform float uZClip;    // projectionWorldZClip

        // Constant value inherited from old NWN (fMinOffset, vs_beamvol.shd).
        const float SOWX_BEAM_MIN_OFFSET = 0.1;

        void main()
        {
            vec3 wp = (uModel * aPos).xyz;
            vec3 vp = (uView * vec4(wp, 1.0)).xyz;
            vec3 dir = uProjSrc.xyz;
            float off = SOWX_BEAM_MIN_OFFSET;
            if (aFrontal <= 0.0)
                off = max(SOWX_BEAM_MIN_OFFSET, (uZClip - wp.z) / dir.z);
            vp += off * (mat3(uView) * dir);
            gl_Position = uProj * vec4(vp, 1.0);
        }
        """;

    /// <summary>The fragment stage: a flat tint, exactly as <c>fs_beamvol.shd</c> is once its fog term goes.
    /// No encode -- #132 made the Alloy colour target LINEAR and the one whole-frame encode happens at the
    /// resolve; <c>uColor</c> is handed over already linearised.</summary>
    private const string BeamFs = """
        #version 460 core
        uniform vec4 uColor;
        out vec4 frag;

        void main()
        {
            frag = uColor;
        }
        """;
}
