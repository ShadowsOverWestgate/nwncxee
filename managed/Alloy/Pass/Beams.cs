using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
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
internal static unsafe partial class Beams
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
    /// <summary>Fallback alpha when <c>projectionalpha</c> does not resolve.
    /// <para>MEASURED, and it was wrong: this read 0.2, which is four times what the engine writes. The only
    /// writers are <c>CNWCArea</c>'s four time-of-day setters -- <c>SetAsDay</c> and <c>SetAsDusk</c> store
    /// <c>0x3d4ccccd</c> (0.05) and <c>SetAsDawn</c>/<c>SetAsNight</c> store 0 -- so a session whose symbol
    /// did not resolve drew every shaft four stops proud of vanilla's, and drew them at night as well.
    /// 0.05 is the daylight value; the dark half of the day is the engine's own and is not a fallback's to
    /// guess at.</para></summary>
    private const float DefaultAlpha = 0.05f;
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
        public IntPtr Vdo;
        public int Item, Vao;
        /// <summary>CX-120 (DB mode): the database part this beam is, and its mesh-arena range. Both -1 on the
        /// engine path, where the geometry is a baked projection VDO instead.</summary>
        public int Part, Range;
    }

    private static readonly Beam[] _beams = new Beam[MaxBeams];
    private static int _count;

    // draw arguments resolved by Stage(), one slot per beam issued this pass. Allocated once: the draw path
    // is inside the colour bracket and allocates nothing.
    private static readonly int[] _sBeam = new int[MaxBeams], _sCount = new int[MaxBeams], _sBase = new int[MaxBeams];
    private static readonly nint[] _sOff = new nint[MaxBeams];

    // per-VDO VAO cache, ours alone: two attributes, harvested in the ENGINE context at capture and built in
    // the 4.6 one at draw (VAOs do not share across contexts; buffers do -- #70).

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
    private static int _enrolled, _refused, _drawn;

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

    /// <summary>CX-120: the same direction out of the database. <see cref="FrameInputs.SunDir"/> is already the
    /// direction the light TRAVELS (DbLights negates the area diffuse's <c>+0xac</c> for exactly this), and
    /// <c>HasSun</c> carries the same two tests the engine's <c>GetShadowLights(1)</c> filter applied: the
    /// light reaches past <see cref="EngineLayout.DirectionalRange"/> and it points downward. An interior has
    /// no sun and draws no shafts, which is vanilla's behaviour there.</summary>
    private static bool DirectionDb(out Vector3 dir)
    {
        dir = default;
        if (!FrameInputs.HasSun) return false;
        Vector3 d = FrameInputs.SunDir;
        if (d.LengthSquared() <= 0f) return false;
        dir = Vector3.Normalize(d);
        return true;
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
        if (Disabled || _off || _count == 0 || Gl.DrawElementsBaseVertex == null) return;
        // The player's own switch, honoured the way grass honours "Grass Mode": beams off means the owned
        // pass draws nothing AND suppresses nothing, so the console variable keeps meaning what it says.
        if (_enable != null && *_enable == 0) return;
        // The frame's own sun (DbLights' area pick) is the only directional the database carries -- and an
        // interior has none, exactly as vanilla's own routine draws nothing there.
        if (!DirectionDb(out Vector3 dir) || !EnsureDbProgram()) return;
        // #261 retired the all-or-none rule: a beam that cannot be staged is skipped and its neighbours draw.
        int staged = 0;
        for (int i = 0; i < _count; i++)
            if (StageDb(i, staged)) _sBeam[staged++] = i;
            else _refused++;
        if (staged == 0) return;
        DrawDb(view, proj, in dir, _alpha != null ? *_alpha : DefaultAlpha, staged);
        Gl.Disable(Gl.BLEND);
        Gl.DepthMask(1);
        _drawn += staged;
    }

    /// <summary>Every captured identity is void: the parts belong to a Scene whose unload freed them.</summary>
    /// <summary>Context loss: drop every GL name (they mean nothing once the context is gone) and the
    /// captured world with them. <see cref="EnsureProgram"/> short-circuits on a non-zero <c>_prog</c> and
    /// the draw binds <c>_vVao</c> directly, so leaving these set is what made the next context draw through
    /// a dead program and a dead VAO (#175 follow-up).</summary>
    internal static void ContextLost()
    {
        _progDb = 0; _dbFailed = false;
        _locDbModel = -1; _locDbView = -1; _locDbProj = -1; _locDbSrc = -1; _locDbZClip = -1; _locDbColor = -1;
        Forget();
    }

    internal static void Forget()
    {
        _count = 0;
        _dbGen = -1; _dbScene = 0;
    }

    internal static void ResetPeriod()
    {
        _enrolled = 0; _refused = 0; _drawn = 0; _dbSkipped = 0;
    }

    internal static string Stats() => Disabled ? "beams off (/effects beams 0)" : _off ? "beams vanilla (unavailable)"
        : $"beams {_count} captured from "
          + $"the scene database ({_owned} beaming parts in the scene)"
          + $", {_enrolled} enrolled, {_drawn} drawn"
          + (_refused == 0 ? "" : $", {_refused} REFUSED")
          + (_dbSkipped == 0 ? "" : $", {_dbSkipped} with NO RESIDENT GEOMETRY in the arena");

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
