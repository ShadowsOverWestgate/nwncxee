using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using static CX.Framework.EngineLayout;

namespace CX.Alloy;

/// <summary>
/// The per-area skybox, drawn by the Alloy pass at its graph slot from the engine's own sky gobs (ADR 0011
/// "Skybox draws after opaque, before blended", #125). Vanilla draws it from <c>Scene::RenderSkyBoxes</c>
/// @0x466910 between buckets 0 and 1, depth mask off, against a dome that sits AT the camera -- so it passes
/// everywhere and paints over bucket 0, the ordering hazard this slot exists to avoid.
/// <para><b>The source data is ours.</b> <see cref="Enroll"/> walks the three sky gobs <see cref="Roles"/>
/// reads off the Scene, down each one's part tree, once a frame in engine context. No hook on
/// <c>Scene::RenderSkyBoxes</c>, no capture window, no colour mask. The engine's own routine still runs from
/// the shim, its draws swallowed, because it is the sky's SIMULATION tick: <c>Gob::ProcessGob</c>, node
/// animation, the cross-fade writes to <c>gob+0x108</c>, and the per-frame
/// <c>Gob::SetPosition(Scene+0x190)</c> that parks each dome at the camera (#261).</para>
/// <para><b>The matrix is composed, not captured.</b> A sky part is transformed by the engine's SOFTWARE
/// matrix stack (<c>Gob::Render</c> @0x452260 applies the gob's transform, <c>Gob::PartDraw</c> @0x451d50
/// each node's), so <c>node+0x18</c> means nothing here. <c>Part::GetWorld(Vector&amp;, Quaternion&amp;)</c>
/// @0x437930 composes exactly that chain and hands it back through OUT PARAMETERS, an ABI this side can call.
/// Three field reads finish it: the uniform scale it does not return (<c>Gob::GetAdjustedScale</c> times
/// every <c>part+0x2c</c> up the chain), and the camera-parking correction.</para>
/// <para><b>Why the position is corrected.</b> <c>RenderSkyBoxes</c> calls <c>Gob::SetPosition</c> with
/// <c>Scene+0x190</c> immediately before each <c>Gob::Render</c>, and enrollment runs BEFORE the shim reaches
/// that call, so <c>gob+0xa8</c> still holds the previous frame's camera. <c>Gob::GetAdjustedPosition</c>
/// @0x439900 is <c>gob[0xa8..0xb0] + Q(gob+0xb4)*lerpedOffsets</c>, purely additive, so adding
/// <c>Scene+0x190 - gob+0xa8</c> to the composed world position is exact: the dome is at THIS frame's camera.</para>
/// <para><b>The matrix convention, derived not assumed.</b> <c>MakeMatrix4x4</c> @0x5b8ce0 writes the
/// translation at float indices 12..14 and <c>2(xy+wz)</c> at index 1, so the engine's 16 floats are
/// byte-identical to a <c>System.Numerics.Matrix4x4</c> of the same transform (quaternions stored (w,x,y,z)).
/// <c>operator*(Matrix4x4, Vector4)</c> @0x5b5320 dots ROWS of the column-major array, so
/// <c>operator*(A,B)</c> @0x5b54c0 is the column-vector product and <c>aurMatrixStack::ApplyMatrix</c>
/// @0x4db450's <c>top = top * M</c> becomes <c>top = M * top</c> on this side; <c>aurMatrixStack::Scale</c>
/// @0x4db060 scales the array's columns, the same left-multiply.</para>
/// </summary>
internal static unsafe class SkyBox
{
    /// <summary>The model stack: <c>m_aurMtxStack</c> index 2 (PROJECTION=0, VIEW=1), stride 0x58, top at
    /// +0x18. <c>RenderSkyBoxes</c> replaces the PROJECTION (push/identity/SetPerspectiveTransform @0x466aa2)
    /// and leaves the model stack alone, so this top is the base every composed sky matrix sits on -- and it
    /// is the same value at the Alloy slot, because every engine walk brackets its own model writes with
    /// Push/PopCurrentMatrix.</summary>
    private const int ModeModel = 2;
    /// <summary>A STRIDE, not a field: the byte distance between one <c>m_aurMtxStack</c> entry and the
    /// next, which is what picks the model stack out of the global array.</summary>
    private static readonly int StackStride = EngineLayout.MtxStackStride;
    /// <summary>A skybox model is a handful of parts and there are at most three gobs in play -- 11 was the
    /// most ever logged. Sized as a ceiling anyway (one Part struct is small), because a sky handed back for
    /// want of a slot is a whole-screen artifact.</summary>
    private const int MaxParts = 1024;
    /// <summary>The engine's blend pairs, as <c>Aurora::AuroraBlendModes</c> -- the indices into
    /// <c>blendTranslationTable</c> @0x12f5040, which reads
    /// <c>{SRC_ALPHA, ONE_MINUS_SRC_ALPHA, ONE, ZERO, DST_COLOR, SRC_COLOR, ONE_MINUS_DST_ALPHA}</c>.
    /// <c>RenderSkyBoxes</c> issues <c>SetBlendFunc(0,3)</c> for the current sky -- SRC_ALPHA/ZERO, a
    /// replace scaled by (1-t) -- and <c>SetBlendFunc(0,1)</c> for the incoming one and for the skyfade gob,
    /// which is SRC_ALPHA/ONE_MINUS_SRC_ALPHA: an ordinary over-blend, NOT an add. Aurora 1 and 2 are easy
    /// to read the wrong way round and the difference is a sky that brightens through every cross-fade.</summary>
    private const int BlendSrcAlpha = 0, BlendZero = 3, BlendOverAlpha = 1;

    /// <summary>One enrolled sky part. Rebuilt from the gob's own part tree every frame, so there is no
    /// liveness field: an entry exists exactly while the engine's own tree holds it.</summary>
    internal struct Part
    {
        public IntPtr Tri, Node, Mat, Vdo, Gob;
        public int Item, Prim;
        /// <summary>#125: can the Alloy pass actually SHADE this part -- i.e. its material either declares
        /// no custom shader, or one the Alloy pass implements? A dynamic skybox is its fragment shader; drawn
        /// with the plain stage it is the diffuse texture alone, which for sow-topdata's night background is
        /// black. False hands the whole sky back to the engine, unmasked, at its vanilla position.</summary>
        public bool Ported;
        /// <summary>#136: the ENGINE's compiled program for this part's material, captured at the swallow
        /// while the sky sim tick ran, and 0 for a part the engine drew with a stock stage. Non-zero is what
        /// lets an UNPORTED sky draw at all: Alloy binds this program rather than one of its own.</summary>
        public uint EngProg;
        /// <summary>The Alloy VAO-cache slot for <see cref="Vdo"/>, resolved on the ENGINE side each pass
        /// (harvesting reads the engine's VAO), -1 for none.</summary>
        public int Vao;
        public Matrix4x4 Model;
    }

    private static readonly Part[] _parts = new Part[MaxParts];
    private static int _count;
    private static byte* _stacks;
    private static ExtenderLog? _log;
    /// <summary><c>Part::GetWorld(Vector&amp;, Quaternion&amp;)</c> -- no Part subclass overrides vtable slot
    /// 0x20, so this is called by symbol rather than through a vptr.</summary>
    private static PartGetWorldFn _getWorld;
    /// <summary><c>Gob::GetAdjustedScale</c> -- a plain float return, which is why the scale comes from here
    /// while the position and orientation come from <see cref="_getWorld"/>'s out parameters.</summary>
    private static GobGetAdjustedScaleFn _gobScale;
    /// <summary>#136: <c>GLRender::m_pCurrentCustomProgram</c> -- the PROG_FIXED the material bind left
    /// current, and the program id at <c>+0x64</c> inside it. Read at the swallow, in engine context.</summary>
    private static IntPtr* _curCustom;
    /// <summary><c>GLRender::SetUniforms(PROG_FIXED&amp;)</c> and
    /// <c>GLRender::GlUseProgramConditionally(uint)</c> -- between them, the custom-program branch of
    /// <c>GLRender::SetProgram</c> @0x4cf9c8. See <see cref="NoteEngineDraw"/> for why we make that call
    /// ourselves.</summary>
    private static GlSetUniformsFn _setUniforms;
    private static GlUseProgramConditionallyFn _useProgram;

    /// <summary>#136: true only while <see cref="RspShim"/> is running the sky's SIMULATION vcall. The
    /// swallow reads it to tell a sky draw from every other engine draw in the window -- there is no other
    /// discriminator at that depth, and the vcall is the engine's own bracket around exactly these parts.</summary>
    internal static bool InTick;
    /// <summary>The model stack top, read once per enrollment; every composed matrix ends on it.</summary>
    private static Matrix4x4 _base = Matrix4x4.Identity;
    private static int _enrolled, _attached, _hosted;
    private static bool _off;
    /// <summary>The player's own switch (<c>effects.sky</c>). Distinct from <see cref="_off"/>, which means
    /// "this session cannot own the sky at all": the hook is armed either way, so the toggle is live rather
    /// than a relaunch.</summary>
    private static bool Disabled => !Effects.Sky.V;

    /// <summary>The Scene whose sky was enrolled, for the Alloy pass to compare against its own.</summary>
    internal static IntPtr Scene;

    internal static int Count => _count;
    internal static ref Part At(int i) => ref _parts[i];

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _stacks = (byte*)Native.Sym(SymbolMap.MAurMtxStack);
        _getWorld = EngineFn.PartGetWorld;
        _gobScale = EngineFn.GobGetAdjustedScale;
        _curCustom = (IntPtr*)Native.Sym(SymbolMap.GlCurrentCustomProgram);
        _setUniforms = EngineFn.GlSetUniforms;
        _useProgram = EngineFn.GlUseProgramConditionally;
        if (_curCustom == null || _setUniforms == null || _useProgram == null)
            log.Info("SkyBox: GLRender::m_pCurrentCustomProgram/SetUniforms/GlUseProgramConditionally "
                   + "unresolved -- a dynamic sky's own program cannot be hosted, so an unported sky stays "
                   + "undrawn (#136)");
        if (_stacks == null || _getWorld == null || _gobScale == null)
        { _off = true; log.Info("SkyBox: matrix stack, Part::GetWorld or Gob::GetAdjustedScale unresolved -- the sky stays vanilla"); }
    }

    /// <summary>#261: the sky, read off the Scene's own gobs. Once per world frame, in ENGINE context,
    /// before the frame anchor -- <c>AlloyRenderer.VdoSlot</c> harvests engine VAOs off the entries this
    /// leaves. Allocation-free; three gobs of a handful of parts each.
    /// <para>Rebuilt from scratch each call. The gobs are the ones <see cref="Roles"/> names, the trees are
    /// the ones <c>Gob::ProxyPartDraw</c> would have walked, and the gates are <c>Gob::Render</c>'s own --
    /// so there is nothing here that can go stale, and an area transition needs no tombstone.</para></summary>
    internal static void Enroll(IntPtr scene)
    {
        _count = 0;
        if (_off || Disabled || scene == IntPtr.Zero) { Scene = IntPtr.Zero; return; }
        Scene = scene;
        _base = new aurMatrixStack((IntPtr)(_stacks + ModeModel * StackStride)).Top;
        Vector3 cam = new CX.Engine.Scene(scene).SkyPos;
        Span<Role> roles = stackalloc Role[3];
        int n = Roles(scene, roles);
        for (int r = 0; r < n; r++)
        {
            var g = new CX.Engine.Gob(roles[r].Gob);
            if (g.IsNull || g.Visible == 0) continue;
            // ponytail: a gob with an attachment chain has a further lerped scale factor folded in by
            // Gob::Render @0x45235b, and this side does not walk it. A skybox gob is created free-standing
            // by Scene::SetSkyBox, so the chain is expected to be null -- refuse the whole sky rather than
            // draw it at a scale we did not measure. `sky attached=` on the period line is the trip-wire;
            // if it ever moves, replicate the @0x452370 loop (LerpFloat::Lerped is exported).
            if (!g.Attach.IsNull) { _attached++; _count = 0; return; }
            float gs = _gobScale(g.Ptr);
            Tree(new GobRef(g.Ptr).PartTree, g.Ptr, gs, cam);
        }
    }

    /// <summary>One gob's part tree, iteratively, mirroring <c>Gob::PartDraw</c>: a part whose +0x34 is
    /// clear draws nothing AND stops the recursion, exactly as the engine's early return does.</summary>
    private static void Tree(IntPtr root, IntPtr gob, float gobScale, Vector3 cam)
    {
        if (root == IntPtr.Zero) return;
        int sp = 0;
        _stack[sp++] = root;
        while (sp > 0)
        {
            var part = new CX.Engine.Part(_stack[--sp]);
            if (part.Visible == 0) continue;
            PartTriMesh tri = part.AsPartTriMesh();
            if (!tri.IsNull) Capture(tri.Ptr, gob, gobScale, cam);
            IntPtr* kids = (IntPtr*)part.Children;
            int k = part.ChildCount;
            if (kids == null) continue;
            for (int i = 0; i < k; i++)
            {
                if (kids[i] == IntPtr.Zero) continue;
                if (sp >= StackMax) break;
                _stack[sp++] = kids[i];
            }
        }
    }

    private static void Capture(IntPtr tri, IntPtr gob, float gobScale, Vector3 cam)
    {
        if (_count >= MaxParts) return;
        var pr = new PartRef(tri);
        IntPtr vp = pr.Node.Ptr, mat = pr.Material.Ptr;
        if (vp == IntPtr.Zero || mat == IntPtr.Zero) return;
        var vprim = new VertexPrimitive(vp);
        IntPtr vdo = vprim.Vdo.Ptr;
        int item = vprim.Item;
        // The same three refusals SceneWalk.TryBind makes: no resident geometry yet, or a VDO this renderer
        // cannot draw from its own tables. Reached without a draw, which is the whole point of #261.
        if (vdo == IntPtr.Zero || item < 0 || new VdoRef(vdo).Mode != 1) return;
        IntPtr mdl = pr.Mdl.Ptr;
        if (mdl == IntPtr.Zero) return;

        Vector3 pos; float* q = stackalloc float[4];
        _getWorld(tri, &pos, q);
        // The camera-parking correction. See the class remarks: gob+0xa8 enters GetAdjustedPosition purely
        // additively, so this replaces the previous frame's parked position with this frame's.
        pos += cam - new CX.Engine.Gob(gob).Position;
        // Quaternions are stored (w, x, y, z); System.Numerics takes (x, y, z, w).
        var rot = new Quaternion(q[1], q[2], q[3], q[0]);
        float scale = gobScale;
        for (var p = new CX.Engine.Part(tri); !p.IsNull; p = p.Parent) scale *= p.Scale;

        ref Part e = ref _parts[_count++];
        e.Tri = tri; e.Node = vp; e.Mat = mat; e.Gob = gob;
        e.Vdo = vdo; e.Item = item; e.Prim = new MdlNode(mdl).PrimType;
        e.Ported = !AlloyCensus.HasUnportedShader(mat);
        e.EngProg = 0;   // filled by NoteEngineDraw when the sim tick reaches this part's draw
        e.Vao = -1;
        e.Model = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot)
                  * Matrix4x4.CreateTranslation(pos) * _base;
        _enrolled++;
    }

    /// <summary>A sky model is a root and a few dozen leaves; this is a runaway guard, not a sizing
    /// decision.</summary>
    private const int StackMax = 256;
    private static readonly IntPtr[] _stack = new IntPtr[StackMax];

    /// <summary>One gob of the sky, in draw order, with the alpha and blend pair the engine would give it.
    /// The alphas come off the Scene live rather than off <c>gob+0x108</c>, which the engine writes at the
    /// vanilla site and would therefore be a frame behind.</summary>
    internal struct Role { public IntPtr Gob; public float Alpha; public int Src, Dst; }

    /// <summary>The engine's own gates, mirrored: the skybox off byte, "no current sky", the fade epsilon
    /// (<c>DAT_013eeeb0</c> is 0.0f) on the desired sky, and a null cross-fade gob. Miss one of these and the
    /// Alloy pass paints a sky into an interior.</summary>
    /// <summary>#222: is this gob painted in the scene's FOG COLOUR? <c>PartTriMesh::Mat</c> @0x450960:
    /// with <c>gob+0x19</c> set and a live scene it calls <c>SetColor4f(fog.rgb, 1.0)</c> -- the part is
    /// drawn in the fog colour at FULL alpha, not white at its own. The skyfade gob is exactly that, which
    /// is why vanilla's horizon fade goes dark at night while ours drew the raw grey texture.
    /// <para>Reads the gob only to compare a byte; the pointer is the one <see cref="Roles"/> just took off
    /// the Scene, so it is live for the length of the staging walk.</para></summary>
    internal static bool FogTinted(IntPtr gob)
        => gob != IntPtr.Zero && *(byte*)(gob + GobSceneTint) != 0;

    internal static int Roles(IntPtr scene, Span<Role> into)
    {
        int n = 0;
        if (scene == IntPtr.Zero) return 0;
        var sc = new CX.Engine.Scene(scene);
        if (sc.SkyOff != 0) return 0;
        float t = sc.SkyT;
        int cur = sc.SkyCur, want = sc.SkyDesired;
        Span<IntPtr> sky = sc.SkyGobs;
        if (cur != -1)
            into[n++] = new Role { Gob = sky[cur], Alpha = 1f - t,
                                   Src = BlendSrcAlpha, Dst = BlendZero };
        if (t > 0f && want != -1)
            into[n++] = new Role { Gob = sky[want], Alpha = t,
                                   Src = BlendSrcAlpha, Dst = BlendOverAlpha };
        var fade = sc.SkyFadeGob;
        if (!fade.IsNull)
            into[n++] = new Role { Gob = fade.Ptr, Alpha = fade.Alpha,
                                   Src = BlendSrcAlpha, Dst = BlendOverAlpha };
        return n;
    }

    /// <summary>#136: the engine is about to draw one sky part and <see cref="RspShim"/> is about to
    /// swallow it. Runs in ENGINE context, at the exact point <c>GLRender::RenderVertexData</c> @0x4d05c5
    /// would have called <c>SetProgram</c> -- which is the whole reason a dynamic sky is black today: that
    /// call sits INSIDE the funnel the shim swallows, so the sky program's uniforms are never uploaded.
    /// <para>So we make the call the swallowed original would have made. <c>SetProgram</c>'s custom branch
    /// @0x4cf9c8 is <c>GlUseProgramConditionally(prog+0x64)</c> then <c>SetUniforms(*prog)</c>, and this is
    /// those two, in that order, on the engine's side of the context fence. Uniform values are program-OBJECT
    /// state and the 4.6 context is share-listed (#80), so what is uploaded here is what Alloy's own draw
    /// sees. Doing it from inside the Alloy bracket instead would be #237 exactly: the engine's cached idea
    /// of the current program reflects the ENGINE's context, so it would skip the bind and scatter ~500
    /// uniforms over whatever Alloy had bound.</para>
    /// <para>ponytail: the per-part uniforms (the model matrix among them) are left where the LAST captured
    /// draw put them, which is exact for the one-custom-part sky this was built against and wrong for a
    /// second one. The upgrade is to re-set the matrix uniforms per part at replay from
    /// <see cref="Part.Model"/>; the count is on the period line so it is a number, not a guess.</para></summary>
    internal static void NoteEngineDraw(IntPtr vdo, uint item)
    {
        if (_curCustom == null || _setUniforms == null || _useProgram == null) return;
        IntPtr prog = *_curCustom;
        if (prog == IntPtr.Zero) return;
        uint id = new GLRender__PROG_FIXED(prog).ProgramId;
        if (id == 0) return;
        // Match FIRST. The global is whatever the last material bind left there, and a part the Alloy pass
        // can shade for itself must never inherit a neighbour's program -- so only a part that actually
        // needs hosting takes it, and only that part's draw pays for the upload.
        int at = -1;
        for (int i = 0; i < _count; i++)
            if (_parts[i].Vdo == vdo && _parts[i].Item == (int)item && !_parts[i].Ported) { at = i; break; }
        if (at < 0) return;
        _useProgram(id);
        _setUniforms(prog);
        _parts[at].EngProg = id;
        _hosted++;
    }

    /// <summary>Can this entry actually be drawn? Geometry resolved, and a material whose shader the Alloy
    /// pass has a port of. A gob that stopped drawing simply is not in the table any more (#261), so there
    /// is no staleness question left here.</summary>
    internal static bool Drawable(int i)
        => _parts[i].Vdo != IntPtr.Zero && (_parts[i].Ported || _parts[i].EngProg != 0);

    /// <summary>Diagnostic: sky parts captured whose material names a shader the Alloy pass has no port of.
    /// Non-zero means the sky is deliberately left vanilla, which is a different fact from never seeing
    /// one.</summary>
    internal static int Unported()
    {
        int n = 0;
        for (int i = 0; i < _count; i++) if (!_parts[i].Ported) n++;
        return n;
    }

    /// <summary>Every enrolled identity is void: the parts belong to gobs the area unload freed.</summary>
    internal static void Forget()
    {
        _count = 0;
        Scene = IntPtr.Zero;
    }

    /// <summary>Zeroed with the rest of the period report's counters: a cumulative one would keep printing
    /// a healthy "enrolled" long after the sky stopped being drawn, which is the opposite of the question.</summary>
    internal static void ResetPeriod() { _enrolled = 0; _attached = 0; _hosted = 0; }

    /// <summary>The role alphas the last staged pass actually used, for the period line. #132 made the Alloy
    /// frame LINEAR and the resolve does the one encode, so a role drawn at partial alpha composites
    /// BRIGHTER than the engine's display-space blend of the same value -- by alpha^(1/2.2)/alpha, which is
    /// 1.5x at 0.5 and 4x at 0.1. Whether that is happening is a question about these three numbers, and
    /// nothing else in the log carries them.</summary>
    internal static void NoteRoles(Span<Role> r, int n)
    {
        _roleN = n;
        for (int i = 0; i < n && i < 3; i++) _roleAlpha[i] = r[i].Alpha;
    }
    private static int _roleN;
    private static readonly float[] _roleAlpha = new float[3];

    internal static string Stats() => Disabled ? "sky off (/effects sky 0)" : _off ? "sky vanilla (unavailable)"
        : $"sky {_count} parts, {_enrolled} enrolled" +
        $", roles {_roleN}" + (_roleN == 0 ? "" : " at alpha " +
            string.Join("/", System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Take(_roleAlpha, _roleN), a => a.ToString("F3")))) +
        (Unported() == 0 ? "" : $", {Unported()} on a custom shader (#136: {_hosted} engine-program draws " +
                                $"hosted this period, so they draw; 0 would mean they do not)") +
        (_attached == 0 ? "" : $", {_attached} passes REFUSED (sky gob has an attachment chain, scale unmeasured)");

    /// <summary>Off-device: <see cref="Roles"/>'s gates and their order, over a stand-in Scene. This is the
    /// one piece of the sky that is a decision rather than a replay -- miss a gate and the Alloy pass paints
    /// a sky into an interior, or paints last area's while the new one fades in. The alphas are the other
    /// half: they must sum to 1 across the cross-fade, or the horizon brightens or darkens mid-transition.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        byte* scene = stackalloc byte[0x60];
        for (int i = 0; i < 0x60; i++) scene[i] = 0;
        Span<Role> r = stackalloc Role[3];
        IntPtr s = (IntPtr)scene;
        var sc = new CX.Engine.Scene(s);
        void Gob(int slot, nint v) => sc.SkyGobs[slot] = v;

        sc.SkyCur = -1; sc.SkyDesired = -1;
        check.Expect(Roles(s, r) == 0, "no current sky and no fade gob draws nothing");

        Gob(1, 0x1000); sc.SkyCur = 1;
        check.Expect(Roles(s, r) == 1 && r[0].Gob == 0x1000 && r[0].Alpha == 1f && r[0].Dst == BlendZero,
          "a settled sky is one opaque-alpha replace draw");

        sc.SkyOff = 1;
        check.Expect(Roles(s, r) == 0, "TurnOffSkyBox wins over everything");
        sc.SkyOff = 0;

        Gob(2, 0x2000); sc.SkyDesired = 2; sc.SkyT = 0.25f;
        check.Expect(Roles(s, r) == 2 && r[0].Alpha == 0.75f && r[1].Gob == 0x2000 && r[1].Alpha == 0.25f
          && r[1].Dst == BlendOverAlpha, "a cross-fade is (1-t) replace then t blended over, in that order");

        sc.SkyT = 0f;
        check.Expect(Roles(s, r) == 1, "t at the epsilon leaves the desired sky out -- the engine's own DAT_013eeeb0 test");

        // the fade gob is read at +0x108, so it needs a buffer that reaches there -- and its alpha is the
        // one Roles output that comes off the gob rather than off the Scene, so assert the value too
        // a SIZE, not a field read: the fake Gob has to reach past its own alpha field
        int fadeGobBytes = EngineLayout.GobAlpha + 4;
        byte* fadeGob = stackalloc byte[fadeGobBytes];
        for (int i = 0; i < fadeGobBytes; i++) fadeGob[i] = 0;
        new CX.Engine.Gob((IntPtr)fadeGob).Alpha = 0.5f;
        sc.SkyFadeGob = new CX.Engine.Gob((IntPtr)fadeGob);
        check.Expect(Roles(s, r) == 2 && r[1].Gob == (IntPtr)fadeGob && r[1].Alpha == 0.5f && r[1].Dst == BlendOverAlpha,
          "the skyfade gob draws last, over-blended, on its own alpha");

        int savedCount = _count;
        _count = 2;
        _parts[0] = new Part { Tri = 0x1000, Mat = 0x2000, Vdo = 0x3000, Ported = true };
        _parts[1] = new Part { Tri = 0x4000, Mat = 0x5000, Vdo = 0x6000, Ported = false };
        check.Expect(Drawable(0) && !Drawable(1), "an unported part is not drawable -- #136, and it is not a fallback");
        _parts[0].Vdo = IntPtr.Zero;
        check.Expect(!Drawable(0), "...and neither is one whose VertexPrimitive has no VDO yet");
        _count = savedCount;
        _parts[0] = default; _parts[1] = default;

        // #261: the matrix convention, which is the one piece of the composition that is a derivation rather
        // than a transcription. Getting the multiply order or the (w,x,y,z) quaternion backwards puts the
        // dome somewhere no counter would ever mention.
        var rot = new Quaternion(0f, 0f, MathF.Sqrt(0.5f), MathF.Sqrt(0.5f));   // +90 deg about z
        Matrix4x4 m = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateFromQuaternion(rot)
                      * Matrix4x4.CreateTranslation(new Vector3(10f, 0f, 0f));
        Vector3 got = Vector3.Transform(new Vector3(1f, 0f, 0f), m);
        check.Expect(MathF.Abs(got.X - 10f) < 1e-4f && MathF.Abs(got.Y - 2f) < 1e-4f && MathF.Abs(got.Z) < 1e-4f,
          "scale, then rotate, then translate -- a point at x=1 lands at (10, 2, 0), not (11, 0, 0)");

        log.Info(check.Ok ? "SkyBox.SelfCheck: passed" : "SkyBox.SelfCheck: FAILED");
    }
}
