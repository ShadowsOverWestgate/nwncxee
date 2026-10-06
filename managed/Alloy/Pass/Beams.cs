using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Volumetric light beams, enumerated by Alloy off the engine's bucket 15 and drawn at the Alloy graph slot
/// (ADR 0011 M3, #130, #261). Vanilla draws them from <c>Scene::RenderBeams</c> @0x46aba0 (Scene vt+0x1d8),
/// step 27 of <c>Scene::RenderSinglePass</c>, immediately after the additive bucket. The population is
/// <c>DrawBucketManager::GetFirstItem(Scene+0x208, 0xf, 0)</c> -- bucket <b>15</b>, which
/// <c>Scene::RenderDrawBucket</c> never touches -- and what is in it are <c>PartBeam</c>s: static geometry
/// built once per area by <c>BSPCreateStaticProjections</c> @0x47e890 (the same bake that makes
/// <c>PartShadow</c>, chosen by the model's own flag at <c>Model+0x114</c>), so a beam is a resident VDO and
/// not a per-frame simulation.
/// <para><b>The source data is ours.</b> <see cref="Enroll"/> reads the bucket arrays directly, once a frame
/// in engine context, exactly as <c>RenderBeams</c> does -- item, then vtable slot 0x90
/// (<c>Part::AsPartBeam</c>), then the VDO at <c>PartBeam+0xa0</c>. No hook on <c>Scene::RenderBeams</c> or
/// <c>PartBeam::Draw</c>, no capture window and no colour mask: the table is rebuilt from the engine's own
/// live classification every frame, so nothing can go stale.</para>
/// <para><b>The model matrix is the IDENTITY, not a read.</b> <c>RenderBeams</c> pushes nothing onto the
/// matrix stack (verified in the disassembly), <c>Scene::RenderDrawBucket</c> @0x46d0b0 brackets every
/// per-part <c>SetModelMatrix</c> with <c>PushCurrentMatrix</c>/<c>PopCurrentMatrix</c>, and RSP's own
/// push/identity/pop pair sits AFTER the beam call (@0x471985, against @0x46f13b) -- so a beam draws on
/// whatever base the frame left in the model stack, and CX-109 established that that base is the identity
/// (<see cref="AlloyCamera.ModelBase"/>, <c>CX-109</c> §2). Beam volumes are baked in
/// world space by <c>BSPCreateStaticProjections</c>, which is why the identity is also the only base that
/// makes sense for them.</para>
/// </summary>
internal static unsafe class Beams
{
    /// <summary>The Aurora primitive id <c>PartBeam::Draw</c> hard-codes.</summary>
    private const int BeamPrim = 4;
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
    private const uint GlUnsignedShort = 0x1403;   // not-an-offset: GL enum
    /// <summary>The engine attribute locations a beam VDO carries, from <c>GLRender::ConnectShaderData</c>'s
    /// bind table @0x4ce8e0: <c>vPos</c> at 0 and <c>fProjectionFrontal</c> at 10. That pair is why beams
    /// cannot ride <c>AlloyRenderer</c>'s VAO cache -- it harvests position/UV/normal and refuses a VDO
    /// with no UV stream, which is every beam.</summary>
    private const uint EngPos = 0, EngFrontal = 10;
    /// <summary>...and ours, which the Alloy VAO binds them at.</summary>
    private const uint LocPos = 0, LocFrontal = 1;
    /// <summary>Aurora blend factors <c>RenderBeams</c> sets: <c>SetBlendFunc(0, 2)</c> =
    /// (GL_SRC_ALPHA, GL_ONE).</summary>
    private const uint GlSrcAlpha = 0x0302, GlOne = 1;   // not-an-offset: GL blend-factor enums
    /// <summary>A handful of static volumes per area; past this a beam is left to the engine.</summary>
    private const int MaxBeams = 2048, MaxVdos = 512;
    /// <summary>The bucket <c>RenderBeams</c> asks the scene's <c>DrawBucketManager</c> for.</summary>
    private const int BeamBucket = 0xf;   // not-an-offset: a draw-bucket index, not a place in memory

    /// <summary>One enrolled beam. Rebuilt from the bucket every frame, so there is no liveness field: an
    /// entry exists exactly while the engine's own classification holds it.</summary>
    internal struct Beam
    {
        public IntPtr Part, Vdo;
        public int Item, Vao;
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
    private static readonly VdoSig[] _vSig = new VdoSig[MaxVdos];
    /// <summary>#260: are the pools behind this slot ones Alloy uploaded? Reported, not chosen between.</summary>
    private static readonly bool[] _vOwned = new bool[MaxVdos];
    private static int _vdoCount;

    private static ExtenderLog? _log;
    /// <summary>The model base, taken once per enrollment. Every beam shares it: the bucket-15 volumes are
    /// baked in one space and <c>RenderBeams</c> sets no per-part transform.</summary>
    private static Matrix4x4 _model = Matrix4x4.Identity;
    private static int* _enable;
    private static float* _alpha;
    private static uint* _primTable;
    private static bool _off, _failed;
    /// <summary>The player's own switch (<c>effects.beams</c>). Distinct from <see cref="_off"/>, which means
    /// the session cannot own beams at all: the hooks arm either way, so the toggle is live.</summary>
    private static bool Disabled => !Effects.Beams.V;
    private static uint _prog;
    private static int _locModel = -1, _locView = -1, _locProj = -1, _locSrc = -1, _locZClip = -1, _locColor = -1;
    private static int _enrolled, _refused, _drawn;
    /// <summary>The Scene the table was enrolled from, and the one <see cref="Direction"/> is asked about:
    /// the volumes were baked against this Scene's own light list.</summary>
    private static IntPtr _scene;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _enable = (int*)Native.Sym(SymbolMap.EnableBeams);
        _alpha = (float*)Native.Sym(SymbolMap.ProjectionAlpha);
        if (_primTable == null)
        { _off = true; log.Info("Beams: prim table unresolved -- light beams stay vanilla"); return; }
        // #261: no hooks. The population is the engine's bucket 15, read at the Alloy slot.
        log.Info("Beams: armed -- bucket-15 volumes enumerated and drawn by Alloy (/effects beams toggles)");
    }

    /// <summary>The population. Once per world frame, in ENGINE context, before the frame anchor --
    /// <see cref="Harvest"/> queries engine VAOs and cannot run anywhere else. Allocation-free; a handful of
    /// items per area.
    /// <para>CX-104: the source is <see cref="SceneParts.Beams"/>, Alloy's own registry of the AREA's beam
    /// parts, and not the engine's draw bucket 15. That bucket is filled by <c>AddPartsToDrawBuckets</c>
    /// @0x4751d0 from <c>staticbeambucket</c>, which the BSP visitor (@0x481000) fills from the nodes the
    /// frustum reached -- so it is the frame's in-view slice, and reading it made this pass's membership a
    /// side effect of the engine's traversal. The registry is a MEMBERSHIP list, so every beam in the area is
    /// enrolled and the GPU clips the ones off screen: a static projection volume is tens of vertices and an
    /// area holds a handful, so the frustum test the engine did here buys nothing worth an offset nobody has
    /// measured on Windows. <c>/buckets beams 0</c> is the A/B back to the bucket.</para>
    /// <para>The table is still rebuilt from scratch each call rather than aged -- the registry has already
    /// done the aging, and a dead entry is a zero the loop skips.</para></summary>
    internal static void Enroll(IntPtr scene)
    {
        if (_off || Disabled || scene == IntPtr.Zero) return;
        if (scene != _scene) { Forget(); _scene = scene; }
        _count = 0;
        _model = AlloyCamera.ModelBase;   // CX-109: the world frame's model base, no longer a stack read
        DrawBucket buckets = new CX.Engine.Scene(scene).DrawBuckets;
        if (Buckets.Beams.V && SceneParts.Ready)
        {
            ReadOnlySpan<IntPtr> beams = SceneParts.Beams;
            _owned = beams.Length;
            if (_owned == 0) SceneParts.WarnEmpty(BucketValidator.KindBeam, 0, BucketCount(buckets));
            if (Buckets.Validate.V)
                BucketValidator.CompareDrawBucket(BucketValidator.KindBeam, beams, buckets, BeamBucket,
                                                  BucketGate.BucketPhases);
            for (int i = 0; i < beams.Length; i++) if (beams[i] != IntPtr.Zero) Capture(beams[i]);
            return;
        }
        _owned = -1;
        for (int phase = 0; phase < BucketGate.BucketPhases; phase++)
        {
            DrawBucket bucket = buckets[BeamBucket * BucketGate.BucketPhases + phase];
            IntPtr* items = bucket.Items;
            int n = bucket.Count;
            if (items == null) continue;
            for (int i = 0; i < n; i++)
            {
                IntPtr item = items[i];
                if (item == IntPtr.Zero) continue;
                PartBeam beam = new CX.Engine.Part(item).AsPartBeam();
                if (!beam.IsNull) Capture(beam.Ptr);
            }
        }
    }

    /// <summary>Beams the registry offered this frame, or -1 when the enrolment read the engine's bucket.
    /// </summary>
    private static int _owned = -1;

    /// <summary>How many parts the engine's own bucket 15 holds across its phases -- for the one-shot
    /// "registry empty while the engine had some" line only.</summary>
    private static int BucketCount(DrawBucket buckets)
    {
        int n = 0;
        for (int phase = 0; phase < BucketGate.BucketPhases; phase++)
        {
            DrawBucket b = buckets[BeamBucket * BucketGate.BucketPhases + phase];
            if (b.Items != null && b.Count > 0) n += b.Count;
        }
        return n;
    }

    private static void Capture(IntPtr part)
    {
        var pb = new PartBeam(part);
        IntPtr vdo = pb.Vdo;
        int item = pb.Item;
        // Only the indexed-static path, which is what every resident VDO in this renderer is. A refusal now
        // costs exactly one missing shaft: under the cutover the engine draws nothing, so there is no
        // whole-class fallback to protect and no reason to stand the pass down for it.
        if (vdo == IntPtr.Zero || new VdoRef(vdo).Mode != 1) { _refused++; return; }
        if (_count >= MaxBeams) { _refused++; return; }
        int v = VdoSlot(vdo);
        if (v < 0) { _refused++; return; }
        ref Beam b = ref _beams[_count++];
        b.Part = part; b.Vdo = vdo; b.Item = item; b.Vao = v;
        _enrolled++;
    }

    /// <summary>Find (or harvest) the VAO-cache slot for a VDO. Harvesting reads the ENGINE VAO's attribute
    /// formats, so it has to run here (inside the engine's own draw) and not at replay time.</summary>
    private static int VdoSlot(IntPtr vdo)
    {
        int s = -1;
        for (int i = 0; i < _vdoCount; i++)
        {
            if (_vVdo[i] != vdo) continue;
            if (_vSig[i] == VdoSig.Of(vdo)) return i;
            // The AlloyRenderer.VdoSlot case: a re-minted VDO at a dead one's address. Re-harvest in place.
            _vVdo[i] = IntPtr.Zero; _vElemBuf[i] = 0; s = i;
            break;
        }
        if (s < 0)
        {
            if (_vdoCount >= MaxVdos) return -1;
            s = _vdoCount++;
        }
        if (!Harvest(vdo, s)) return -1;
        _vVdo[s] = vdo; _vStale[s] = _vVao[s] != 0; _vSig[s] = VdoSig.Of(vdo);
        return s;
    }

    private static bool Harvest(IntPtr vdo, int s)
    {
        uint vao = new VdoRef(vdo).Vao;
        // ponytail: the #284 sibling. A beam VDO minted after the shim armed has no engine VAO either, and
        // this refuses it silently. NOT dripped here: AlloyRenderer's drip runs at the enrollment edge, while
        // this runs INSIDE the engine's own bucket-15 draw, where re-entering SetVertexDataObject would move
        // the current-VDO cache under the caller. Upgrade path if `beams 0 drawn` is ever traced to this:
        // arm the drip at the bucket window's open, not per harvest.
        if (vao == 0 || Gl.GetVertexAttribiv == null) return false;
        int eb; Gl.GetVertexArrayiv(vao, Gl.ELEMENT_ARRAY_BUFFER_BINDING, &eb);
        _vElemBuf[s] = (uint)eb;
        // The attribute READ still binds: no DSA query answers an attribute's buffer name.
        int saved;
        Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &saved);
        Gl.BindVertexArray(vao);
        VertexArrays.Read(EngPos, out _vPos[s]);
        VertexArrays.Read(EngFrontal, out _vFr[s]);
        Gl.BindVertexArray((uint)saved);
        // Both streams or nothing: the extrusion branches on fProjectionFrontal, and a volume drawn as if
        // every vertex were frontal is a flat sliver at the minimum offset rather than a shaft.
        return _vElemBuf[s] != 0 && _vPos[s].Present && _vFr[s].Present;
    }

    /// <summary>#260: one VAO, over the pool buffers Alloy uploads into. The pair this used to build differed
    /// only in which buffers it pulled from, and there is one set of those now.</summary>
    private static bool EnsureVao(int v)
    {
        if (_vVao[v] != 0 && !_vStale[v]) return true;
        if (_vVao[v] != 0) { uint old = _vVao[v]; Gl.DeleteVertexArrays(1, &old); }
        _vVao[v] = BuildVao(v);
        _vOwned[v] = _vVao[v] != 0 && OwnedBuffers.Owns(_vElemBuf[v]) && OwnedBuffers.Owns(_vPos[v].Buf);
        _vStale[v] = false;
        return _vVao[v] != 0;
    }

    private static uint BuildVao(int v)
    {
        uint elem = _vElemBuf[v];
        if (elem == 0) return 0;
        var pos = _vPos[v]; var fr = _vFr[v];
        uint vao; Gl.CreateVertexArrays(1, &vao);
        VertexArrays.Bind(vao, LocPos, in pos);
        VertexArrays.Bind(vao, LocFrontal, in fr);
        Gl.VertexArrayElementBuffer(vao, elem);
        return vao;
    }

    /// <summary>The VAO this slot draws from.</summary>
    private static uint Vao(int v) => _vVao[v];

    /// <summary>The direction <c>RenderBeams</c> extrudes along: the area's DIRECTIONAL light, negated --
    /// which is what makes these sun shafts rather than per-lamp volumes. False when there is none, which is
    /// every interior, and the engine's own routine draws nothing there either.
    /// <para><b>SL5: this used to read the engine's <c>Scene+0x60</c> shadow-light list, and that list is
    /// not maintained on an Alloy frame.</b> MEASURED on both binaries (objdump; Linux
    /// <c>Scene::RenderSinglePass</c> @0x46e5f0+0xb5, Windows rva 0xd0f10+0x213): its sole writer on a live
    /// frame is that function's head, which deep-copies <c>LightManager::GetShadowLights(1)</c> into the
    /// embedded list at <c>+0x60</c> (ptr) / <c>+0x68</c> (count) / <c>+0x6c</c> (capacity). Alloy REPLACES
    /// that head. The only other writers in the client are <c>Scene::Scene</c>, which zeroes the three, and
    /// <c>~Scene</c>, which frees the buffer without clearing the pointer. So the list read 0 on every Scene
    /// vanilla had never drawn -- every area Alloy loads -- and otherwise the last vanilla frame's copy; and
    /// vanilla itself skips the copy while the <c>enableshadows</c> global is 0. Shafts extruded along
    /// whatever that left.</para>
    /// <para>The replacement is the SAME light by the same tests, from a source Alloy does maintain.
    /// <c>GetShadowLights(1)</c>'s own filter admits a light at <c>range &gt;= 1000</c> only as the area
    /// diffuse that casts shadows, so the loop below could never pick anything else -- and that light is
    /// exactly <see cref="LightRegistry.AreaDiffuse"/>, the registry's port of
    /// <c>LightManager::UpdateLights</c>' own pick. Every test the engine's list had already applied is kept
    /// here explicitly rather than assumed.</para></summary>
    internal static bool Direction(IntPtr scene, out Vector3 dir)
        => DirectionOf(AreaDirectional(scene), out dir);

    /// <summary>The area's directional light: Alloy's registry, or -- with <c>light.registry 0</c> -- the
    /// engine's own <c>LightManager+0x98</c>, which <c>UpdateLights</c> still rewrites every frame. The same
    /// pair <c>FrameRead.ReadAreaTerms</c> takes the area terms from, so the shafts and the sun cannot
    /// disagree about which light the sun is.</summary>
    private static PartLight AreaDirectional(IntPtr scene)
    {
        if (LightRegistry.Live) return new PartLight(LightRegistry.AreaDiffuse);
        if (scene == IntPtr.Zero) return new PartLight(IntPtr.Zero);
        LightManager lm = new CX.Engine.Scene(scene).LightManager;
        return lm.IsNull ? new PartLight(IntPtr.Zero) : lm.AreaDiffuse;
    }

    /// <summary>The filter and the negation, apart from where the light came from: what the self-check pins.</summary>
    internal static bool DirectionOf(PartLight l, out Vector3 dir)
    {
        dir = default;
        if (l.IsNull) return false;
        if (l.Range < EngineLayout.DirectionalRange) return false;
        if (l.CastShadows == 0 || l.Kind != 0) return false;
        Vector3 d = l.Pos;
        if (d.LengthSquared() <= 0f) return false;
        dir = -Vector3.Normalize(d);
        return true;
    }

    /// <summary>ADR 0011 (#130): the Alloy beam pass, right after the additive class and before the probe
    /// capture -- vanilla's own position (step 27, straight after bucket 4). Additive
    /// <c>(SRC_ALPHA, ONE)</c>, depth test on, depth mask off, exactly as <c>RenderBeams</c> sets it.
    /// <para>Leaves GL_BLEND off and the depth mask on, which is the state
    /// <see cref="AlloyRenderer"/>'s additive arm hands over and the probe capture after it needs.</para></summary>
    internal static void Draw(Matrix4x4* view, Matrix4x4* proj)
    {
        if (Disabled || _off || _failed || _count == 0 || Gl.MultiDrawElementsBaseVertex == null) return;
        // The player's own switch, honoured the way grass honours "Grass Mode": beams off means the owned
        // pass draws nothing AND suppresses nothing, so the console variable keeps meaning what it says.
        if (_enable != null && *_enable == 0) return;
        // The Scene the table was enrolled from: the volumes were baked against this Scene's own light
        // list, and reading another's would extrude them along a direction the geometry was never built for.
        if (!Direction(_scene, out Vector3 dir)) return;
        if (!EnsureProgram()) return;

        // #261 retired the all-or-none rule with the colour mask that motivated it: there is no engine copy
        // of a beam to delete any more, so a beam that cannot be staged is skipped and its neighbours draw.
        int staged = 0;
        for (int i = 0; i < _count; i++)
            if (Stage(i, staged)) _sBeam[staged++] = i;
            else _refused++;
        if (staged == 0) return;

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
        new PassState(depth: true, depthWrite: false, blend: true,
                      blendSrc: GlSrcAlpha, blendDst: GlOne).Apply();

        fixed (Matrix4x4* m = &_model) Gl.UniformMatrix4fv(_locModel, 1, 0, (float*)m);
        fixed (int* counts = _sCount, bases = _sBase)
        fixed (nint* offs = _sOff)
        for (int k = 0, run; k < staged; k += run)
        {
            int vao = _beams[_sBeam[k]].Vao;
            for (run = 1; k + run < staged && _beams[_sBeam[k + run]].Vao == vao; run++) { }
            Gl.BindVertexArray(Vao(vao));
            Gl.MultiDrawElementsBaseVertex(_primTable[BeamPrim], counts + k, GlUnsignedShort,
                                           (void**)(offs + k), run, bases + k);
        }
        Gl.BindVertexArray(0);
        Gl.Disable(Gl.BLEND);
        Gl.DepthMask(1);
        _drawn += staged;
    }

    /// <summary>Resolve one beam's draw arguments off its VDO's own item tables and park them in slot
    /// <paramref name="k"/>. False means this beam cannot be drawn: #261 retired the all-or-none rule, so it
    /// is skipped and its neighbours draw.</summary>
    private static bool Stage(int i, int k)
    {
        ref Beam b = ref _beams[i];
        var v = new VdoRef(b.Vdo);
        long elem = v.ElemItems, vert = v.VertItems;
        if (elem == 0 || vert == 0 || b.Item < 0 || b.Item >= v.ElemCount || b.Item >= v.VertCount) return false;
        int count = *(int*)(elem + b.Item * 8 + 4);
        if (count <= 0 || !EnsureVao(b.Vao)) return false;
        _sCount[k] = count;
        _sOff[k] = (nint)((long)(uint)*(int*)(elem + b.Item * 8) * 2);
        _sBase[k] = *(int*)(vert + b.Item * 8);
        return true;
    }

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

    /// <summary>The engine deleted a pool buffer a harvested slot points at: same backstop as
    /// <see cref="OnRepack"/>, for the same reason -- the VAO would keep the dead name attached (GL 4.6
    /// 5.1.2) while the next pool inherits it. Wholesale, like the repack.</summary>
    internal static void OnBufferDeleted(uint name)
    {
        if (name == 0) return;
        for (int v = 0; v < _vdoCount; v++)
            if (_vElemBuf[v] == name || _vPos[v].Buf == name || _vFr[v].Buf == name) { Forget(); return; }
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
        for (int v = 0; v < MaxVdos; v++) { _vVao[v] = 0; _vOwned[v] = false; _vElemBuf[v] = 0; }
        Forget();
    }

    /// <summary>VAO-cache slots the harvest has taken -- the area edge's tally (CX-19).</summary>
    internal static int VdoSlots => _vdoCount;

    internal static void Forget()
    {
        _count = 0;
        _scene = IntPtr.Zero;
        for (int v = 0; v < _vdoCount; v++) { _vVdo[v] = IntPtr.Zero; _vStale[v] = true; }
        _vdoCount = 0;
    }

    internal static void ResetPeriod() { _enrolled = 0; _refused = 0; _drawn = 0; }

    internal static string Stats() => Disabled ? "beams off (/effects beams 0)" : _off ? "beams vanilla (unavailable)"
        : $"beams {_count} captured from "
          + (_owned < 0 ? "the engine's bucket 15" : $"CX's own registry ({_owned} in the area)")
          + $", {_enrolled} enrolled, {_drawn} drawn"
          + (_refused == 0 ? "" : $", {_refused} REFUSED (unindexed VDO, no attribute pair, or table full)");

    /// <summary>Off-device: the two pieces of this file that are decisions rather than replays -- the light
    /// scan's predicate (miss it and the shafts point somewhere the volumes were never baked for) and the
    /// extrusion offset the vertex stage computes (which the shader cannot be asked about at run-demo time).
    /// </summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        int SceneBytes = EngineLayout.SceneLightManager + 8;   // a SIZE: the fake Scene must hold its manager slot
        int MgrBytes = EngineLayout.LmAreaDiffuse + 8;          // ...and the manager its area-diffuse slot
        int LightBytes = EngineLayout.LightDir + 12;            // ...and the light its position
        byte* scene = stackalloc byte[SceneBytes];
        for (int i = 0; i < SceneBytes; i++) scene[i] = 0;
        byte* mgr = stackalloc byte[MgrBytes];
        for (int i = 0; i < MgrBytes; i++) mgr[i] = 0;
        byte* b = stackalloc byte[LightBytes];
        for (int i = 0; i < LightBytes; i++) b[i] = 0;
        IntPtr s = (IntPtr)scene;
        var sc = new CX.Engine.Scene(s);
        sc.LightManager = new LightManager((IntPtr)mgr);
        new LightManager((IntPtr)mgr).AreaDiffuse = new PartLight((IntPtr)b);

        void Light(byte* l, float range, int live, int kind, float x, float y, float z)
        {
            var lv = new PartLight((IntPtr)l);
            lv.Range = range; lv.CastShadows = live; lv.Kind = kind;
            lv.Pos = new Vector3(x, y, z);
        }

        // SL5: the filter, over a light handed in directly -- the engine's own GetShadowLights(1) rule, which
        // is the whole of what the deleted Scene+0x60 walk applied.
        Light(b, 10f, 1, 0, 0f, 0f, 1f);
        check.Expect(!DirectionOf(new PartLight((IntPtr)b), out _), "a point light's range is too short to be the sun");
        Light(b, 100000f, 1, 0, 0f, 0f, 2f);
        check.Expect(DirectionOf(new PartLight((IntPtr)b), out Vector3 d) && d == new Vector3(0f, 0f, -1f),
          "the area directional past 1000 range wins, normalised and NEGATED");
        // ...and through the Scene, which is what pins the offset chain the shafts now read
        // (Scene+0x158 -> LightManager+0x98) in place of the stale Scene+0x60 list. Taken with the registry
        // dialled off, so the path under test is the one that does not need a live registry to have run.
        bool regWas = Lighting.Registry.V;
        // try/finally, not a plain restore: run-demo reads the user's real cx.cfg, so a throw between the two
        // would leave their light.registry switched off.
        try
        {
            Lighting.Registry.V = false;
            check.Expect(Direction(s, out Vector3 ds) && ds == new Vector3(0f, 0f, -1f),
              "...and the same answer comes off the area's own light, not the engine's shadow-light list");
        }
        finally { Lighting.Registry.V = regWas; }
        Light(b, 100000f, 0, 0, 0f, 0f, 2f);
        check.Expect(!DirectionOf(new PartLight((IntPtr)b), out _), "a light with the +0x74 flag clear is skipped");
        Light(b, 100000f, 1, 1, 0f, 0f, 2f);
        check.Expect(!DirectionOf(new PartLight((IntPtr)b), out _), "...and so is one whose +0x80 discriminant is non-zero");
        Light(b, 100000f, 1, 0, 0f, 0f, 0f);
        check.Expect(!DirectionOf(new PartLight((IntPtr)b), out _), "a zero direction is refused rather than normalised into a NaN");
        check.Expect(!DirectionOf(new PartLight(IntPtr.Zero), out _), "no area directional draws no beams -- which is every interior");

        // The vertex stage's extrusion, mirrored: a frontal vertex sits at the minimum offset, a back-facing
        // one extrudes until world z reaches the clip plane, and the minimum always wins as a floor. Getting
        // the max() backwards inverts the shaft above the clip height, which is most of an outdoor scene.
        const float MinOffset = 0.1f;
        static float Off(float frontal, float zClip, float wz, float dirZ)
            => frontal > 0f ? MinOffset : MathF.Max(MinOffset, (zClip - wz) / dirZ);
        check.Expect(Off(1f, WorldZClip, 50f, -1f) == MinOffset, "a frontal vertex extrudes by the minimum offset only");
        check.Expect(Off(0f, WorldZClip, 11f, -1f) == 31f, "a back-facing vertex extrudes down to the world z clip");
        check.Expect(Off(0f, WorldZClip, -19.95f, -1f) == MinOffset,
          "...and never less than the minimum, however close it starts");
        // The clip plane is BELOW the world floor, and that is the whole of it: a positive one truncates
        // every shaft in mid-air, which is exactly what reading SetShaderParamf as one-argument produces.
        check.Expect(WorldZClip < 0f && Off(0f, WorldZClip, 0f, -1f) > 1f,
          "the clip plane is under the floor, so a shaft at ground level still has length");

        check.Expect(BeamPrim == 4 && WorldZClip == -20f && EngineLayout.DirectionalRange == 1000f,
          "the three literals RenderBeams pushes: prim 4, projectionWorldZClip -20.0, range threshold 1000");

        log.Info(check.Ok ? "Beams.SelfCheck: passed" : "Beams.SelfCheck: FAILED");
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
    private static string BeamVs => ShaderSource.Get("cx_beam_v.glsl");

    /// <summary>The fragment stage: a flat tint, exactly as <c>fs_beamvol.shd</c> is once its fog term goes.
    /// No encode -- #132 made the Alloy colour target LINEAR and the one whole-frame encode happens at the
    /// resolve; <c>uColor</c> is handed over already linearised.</summary>
    private static string BeamFs => ShaderSource.Get("cx_beam_f.glsl");
}
