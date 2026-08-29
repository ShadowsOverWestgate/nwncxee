using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Graphics;
using static Sowx.Alloy.SceneLayout;

namespace Sowx.Alloy;

/// <summary>
/// #125 (map #68, spec #114 M3, ADR 0011 "Skybox draws after opaque, before blended"): the per-area skybox,
/// captured off the engine's own draw and replayed by the Alloy pass at its graph slot.
/// <para>Vanilla draws it from <c>Scene::RenderSkyBoxes</c> @0x466910, between buckets 0 and 1, depth mask
/// off and depth test against a dome that sits AT the camera -- so it passes everywhere and paints over
/// bucket 0. That is #107 §3's ordering hazard, and it is why every Alloy blending class before this ticket
/// carried a named approximation about being painted over.</para>
/// <para>What the engine still does, every frame: the whole of <c>RenderSkyBoxes</c>, under
/// <c>glColorMask(0,0,0,0)</c>. That is deliberate and it is what makes the rest of this file small. The
/// three gob draws keep running, so <c>PartTriMesh::Draw</c> keeps funnelling parts through
/// <see cref="Begin"/> -- capture stays warm, node animation and <c>ProcessGob</c> keep happening, the model
/// matrices are the engine's own live ones, and an area transition needs no code at all. With the depth mask
/// already off inside, a colour-masked sky writes NOTHING, so the ordering hazard is gone by construction
/// rather than by suppression. The cost is three small-model draws the engine paid anyway.</para>
/// <para>The matrices are the reason for the whole arrangement. A sky part is transformed by the engine's
/// SOFTWARE MATRIX STACK, not by <c>node+0x18</c>: <c>Gob::Render</c> @0x452260 pushes the gob's transform
/// and <c>Gob::PartDraw</c> @0x451d50 pushes each node's, then calls the part's Draw vtable slot (+0xd0,
/// <c>PartTriMesh::Draw</c>). So the Alloy pass cannot read a model matrix off the node the way every other
/// Alloy class does -- it reads the stack top at capture, which is only live inside that draw.</para>
/// <para>ponytail: the matrix a pass replays was captured at the PREVIOUS frame's vanilla site, so the dome
/// follows the camera one frame late. On a dome the size of a sky that is centimetres. Upgrade path, if a
/// spinning cloud layer ever reads badly: reconstruct the transform from the gob (+0xa8 position, the node
/// chain's own pos/quat/scale) instead of capturing it.</para>
/// </summary>
internal static unsafe class SkyBox
{
    /// <summary>Scene fields, all Ghidra <c>Scene::RenderSkyBoxes</c> / <c>SetSkyBox</c> @0x45e260 /
    /// <c>SetSkyAlpha</c> @0x45f100: four skybox gobs from +0x18, the cross-fade gob at +0x38, the fade
    /// parameter t at +0x40, the current and desired slot indices (-1 = none) at +0x44/+0x48, and the
    /// "skybox off" byte at +0x4c that <c>TurnOffSkyBox</c> sets.</summary>
    private const int SceneGobs = 0x18, SceneFadeGob = 0x38, SceneT = 0x40, SceneCur = 0x44,
                      SceneDesired = 0x48, SceneOff = 0x4c;
    /// <summary>The alpha multiplier <c>PartTriMesh::Mat</c> reads off the instance, and the field
    /// <c>RenderSkyBoxes</c> writes the cross-fade into. Same offset <c>AlloyCensus.GobAlpha</c> names.</summary>
    private const int GobAlpha = 0x108;
    /// <summary>The model stack: <c>m_aurMtxStack</c> index 2 (PROJECTION=0, VIEW=1), stride 0x58, top at
    /// +0x18 -- <c>RenderSkyBoxes</c> itself calls <c>SetMatrixMode(2)</c> before the gob draws.</summary>
    private const int ModeModel = 2, StackStride = 0x58, StackTop = 0x18;
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

    /// <summary>One captured sky part. Everything but the matrix is re-validated at draw time.</summary>
    internal struct Part
    {
        public IntPtr Tri, Node, Mat, Vdo, Gob;
        public int Item, Prim, Frame;
        /// <summary>#125: can the Alloy pass actually SHADE this part -- i.e. its material either declares
        /// no custom shader, or one the Alloy pass implements? A dynamic skybox is its fragment shader; drawn
        /// with the plain stage it is the diffuse texture alone, which for sow-topdata's night background is
        /// black. False hands the whole sky back to the engine, unmasked, at its vanilla position.</summary>
        public bool Ported;
        /// <summary>The Alloy VAO-cache slot for <see cref="Vdo"/>, resolved on the ENGINE side each pass
        /// (harvesting reads the engine's VAO), -1 for none.</summary>
        public int Vao;
        public Matrix4x4 Model;
    }

    private static readonly Part[] _parts = new Part[MaxParts];
    private static int _count;
    private static int _capSlot = -1;
    private static byte* _stacks;
    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, void> _orig;
    private static int _frame, _drawnGen = -1, _masked, _captured, _foreign;
    private static long _revoked;
    private static bool _off;
    /// <summary>The player's own switch (<c>/sky</c>, ADR 0013 bucket 1). Distinct from <see cref="_off"/>,
    /// which means "this session cannot own the sky at all": the hook is armed either way, so the toggle is
    /// live rather than a relaunch. Volatile -- written from the command dispatch on the input thread, read
    /// in the capture window on the render thread.</summary>
    private static volatile bool _disabled;

    /// <summary>True between the hook's entry and exit: <c>DrawFunnelService.OnPartDraw</c> routes sky parts here
    /// instead of into the general part table. They must stay out of it -- their transform changes every
    /// frame, so the general part table could never hold a stable record for one.</summary>
    internal static bool InWindow;
    /// <summary>The Scene whose skybox was captured, for the Alloy pass to compare against its own.</summary>
    internal static IntPtr Scene;

    internal static int Count => _count;
    internal static ref Part At(int i) => ref _parts[i];

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _stacks = (byte*)Native.Sym(SymbolMap.MAurMtxStack);
        if (_stacks == null) { _off = true; return; }
        _orig = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.RenderSkyBoxes, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnRenderSkyBoxes);
        if (_orig == null) { _off = true; log.Info("SkyBox: Scene::RenderSkyBoxes did not hook -- the sky stays vanilla"); }
    }

    /// <summary>The vanilla site. Opens the capture window and lets the engine run its whole routine --
    /// colour-masked when the Alloy pass already drew the sky this pass, so the draws still happen (capture,
    /// ProcessGob, node animation) and write nothing. The state prologue/epilogue runs either way, which is
    /// what keeps bucket 1 downstream finding the blend/discard/lighting state it expects.</summary>
    [UnmanagedCallersOnly]
    private static void OnRenderSkyBoxes(IntPtr scene)
    {
        if (_orig == null) return;
        // A GUI 3D panel drives the SAME Scene virtuals as the world, and RenderSinglePass calls this
        // unconditionally, so a portrait or inventory preview reaches here every frame with its own Scene.
        // Nothing below may run for one: the Forget/latch pair would wipe the captured table every frame an
        // inventory is open, and the sky would be permanently vanilla with no counter saying why.
        if (!AlloyRenderer.IsWorldScene(scene)) { _orig(scene); _foreign++; return; }
        // /sky off: idle, never uninstalled. The table is dropped on the first such frame so the Alloy pass
        // has nothing to stage -- Fresh() is a sighting window, and _frame stops advancing here.
        if (_disabled)
        {
            if (_count != 0 || Scene != IntPtr.Zero) Forget();
            InWindow = false;
            _orig(scene);
            return;
        }
        // A new world Scene is a new sky population: the old gobs are freed and their entries would sit dead
        // in the table forever, filling it after a handful of area transitions and starving the real ones.
        if (scene != Scene) Forget();
        Scene = scene;
        // Capture only in a pass the Alloy block took. It is the pass whose matrix stack the replay will be
        // read against, and a pass the Alloy block skipped (context down, vanilla mode) is one whose sky the
        // engine is about to draw for real.
        bool ours = AlloyRenderer.DrewThisPass;
        // ...and never mask on the strength of a stale flag: the Alloy pass stamps the CURRENT pass
        // generation, so a pass that staged nothing falls back to a visible engine sky rather than to none.
        bool mask = ours && _drawnGen == AlloyRenderer.PassGen && Gl.ColorMask != null;
        InWindow = ours;
        if (mask) { Gl.ColorMask(0, 0, 0, 0); _masked++; }
        _orig(scene);
        if (mask) Gl.ColorMask(1, 1, 1, 1);
        InWindow = false;
        if (ours) _frame++;
    }

    /// <summary>A sky part is about to draw vanilla: record its identity and the live model matrix, and arm
    /// the funnel for its (VDO, item, prim). Returns the slot to close with <see cref="End"/>, or -1.</summary>
    internal static int Begin(IntPtr tri)
    {
        IntPtr node = *(IntPtr*)(tri + PartNode), mat = *(IntPtr*)(tri + PartMaterial);
        if (node == IntPtr.Zero || mat == IntPtr.Zero) return -1;
        int s = -1, free = -1;
        for (int i = 0; i < _count; i++)
        {
            if (_parts[i].Tri == tri) { s = i; break; }
            if (_parts[i].Tri == IntPtr.Zero && free < 0) free = i;
        }
        if (s < 0)
        {
            // A revoked slot is reused before the table grows: a sky that swaps within one Scene would
            // otherwise creep towards MaxParts one tombstone at a time.
            if (free >= 0) s = free;
            else if (_count < MaxParts) s = _count++;
            else return -1;
            _captured++;
        }
        ref Part p = ref _parts[s];
        p.Tri = tri; p.Node = node; p.Mat = mat; p.Gob = *(IntPtr*)(tri + PartGob);
        p.Ported = !AlloyCensus.HasUnportedShader(mat);
        p.Model = *(Matrix4x4*)(_stacks + ModeModel * StackStride + StackTop);
        p.Vdo = IntPtr.Zero;
        _capSlot = s;
        return s;
    }

    internal static void End(int s)
    {
        _capSlot = -1;
        if (s >= 0 && _parts[s].Vdo != IntPtr.Zero) _parts[s].Frame = _frame;
    }

    /// <summary>The <c>RenderVertexData</c> funnel's sky arm: one indexed submit per part is all a skybox
    /// mesh is. A part that submits twice (multi-draw) or from an unindexed VDO leaves its slot empty and is
    /// simply not replayed -- the engine's own draw is the fallback, and it is still running.</summary>
    internal static void Observe(IntPtr vdo, int item, int prim)
    {
        if (_capSlot < 0) return;
        ref Part p = ref _parts[_capSlot];
        if (p.Vdo != IntPtr.Zero) { p.Vdo = IntPtr.Zero; _capSlot = -1; return; }
        p.Vdo = vdo; p.Item = item; p.Prim = prim;
    }

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
        => gob != IntPtr.Zero && *(byte*)(gob + GobFogTint) != 0;

    /// <summary>The scene's fog colour, the value <c>Mat()</c> tints with. Display space, like every colour
    /// the engine hands out.</summary>
    internal static void FogColor(IntPtr scene, float* rgb)
    {
        float* f = (float*)(scene + SceneFogColor);
        rgb[0] = f[0]; rgb[1] = f[1]; rgb[2] = f[2];
    }

    internal static int Roles(IntPtr scene, Span<Role> into)
    {
        int n = 0;
        if (scene == IntPtr.Zero || *(byte*)(scene + SceneOff) != 0) return 0;
        float t = *(float*)(scene + SceneT);
        int cur = *(int*)(scene + SceneCur), want = *(int*)(scene + SceneDesired);
        if (cur != -1)
            into[n++] = new Role { Gob = *(IntPtr*)(scene + SceneGobs + cur * 8), Alpha = 1f - t,
                                   Src = BlendSrcAlpha, Dst = BlendZero };
        if (t > 0f && want != -1)
            into[n++] = new Role { Gob = *(IntPtr*)(scene + SceneGobs + want * 8), Alpha = t,
                                   Src = BlendSrcAlpha, Dst = BlendOverAlpha };
        IntPtr fade = *(IntPtr*)(scene + SceneFadeGob);
        if (fade != IntPtr.Zero)
            into[n++] = new Role { Gob = fade, Alpha = *(float*)(fade + GobAlpha),
                                   Src = BlendSrcAlpha, Dst = BlendOverAlpha };
        return n;
    }

    /// <summary>#150: <c>Part::~Part</c> reached this table. Same lever as #145, one table over: a sky part
    /// is routed AWAY from the general part table, so the death notice that fix installed never reached the
    /// entries here -- and the sky's own liveness test (<see cref="Fresh"/>) is a sighting window, which a
    /// destroyed part passes for as long as the capture window has not run again. The frame that follows
    /// reads <c>p.Mat</c>, and <c>Material::GetTexture</c> is a plain field read, so a freed material hands
    /// back a wild pointer that <see cref="AurTex"/> then dereferences.
    /// <para>Compares only, never dereferences, and tombstones rather than swap-removes: <see cref="_capSlot"/>
    /// indexes this array, so a compaction between <see cref="Begin"/> and <see cref="End"/> would rebind a
    /// slot mid-capture.</para></summary>
    internal static void Revoke(IntPtr part)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_parts[i].Tri != part) continue;
            _parts[i].Tri = _parts[i].Node = _parts[i].Mat = _parts[i].Vdo = _parts[i].Gob = IntPtr.Zero;
            _parts[i].Vao = -1;
            _revoked++;
            return;
        }
    }

    /// <summary>Was this part captured on the frame just gone? A gob that stopped drawing (area changed, sky
    /// turned off) leaves stale entries, and replaying one paints last area's sky.</summary>
    internal static bool Fresh(int i) => _parts[i].Tri != IntPtr.Zero && _parts[i].Vdo != IntPtr.Zero
                                         && _parts[i].Ported
                                         && _parts[i].Frame >= _frame - 1;

    /// <summary>Diagnostic: sky parts captured whose material names a shader the Alloy pass has no port of.
    /// Non-zero means the sky is deliberately left vanilla, which is a different fact from never seeing
    /// one.</summary>
    internal static int Unported()
    {
        int n = 0;
        for (int i = 0; i < _count; i++) if (!_parts[i].Ported) n++;
        return n;
    }

    /// <summary>The Alloy pass drew the sky in pass generation <paramref name="gen"/> -- the vanilla site
    /// colour-masks itself for exactly that generation.</summary>
    internal static void MarkDrawn(int gen) => _drawnGen = gen;

    /// <summary>The <c>/sky</c> body: flip the Alloy sky and say where it landed. Cold (input thread); the
    /// render thread picks the flag up in its next capture window.</summary>
    internal static string Toggle()
    {
        if (_off || _orig == null) return "[sky] unavailable this session -- the sky stays vanilla";
        _disabled = !_disabled;
        return $"[sky] Alloy sky {(_disabled ? "off -- the engine draws it" : "on")}";
    }

    /// <summary>Every captured identity is void: the parts belong to gobs the area unload freed.</summary>
    internal static void Forget()
    {
        _count = 0;
        _drawnGen = -1;
        Scene = IntPtr.Zero;
    }

    /// <summary>Zeroed with the rest of the period report's counters: a cumulative one would keep printing
    /// a healthy "masked" long after the sky stopped being drawn, which is the opposite of the question.</summary>
    internal static void ResetPeriod() { _masked = 0; _captured = 0; _foreign = 0; }

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

    internal static string Stats() => _disabled ? "sky off (/sky)" : $"sky {_count} parts, {_captured} captured, {_masked} masked" +
        $", roles {_roleN}" + (_roleN == 0 ? "" : " at alpha " +
            string.Join("/", System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Take(_roleAlpha, _roleN), a => a.ToString("F3")))) +
        $", dtor-revoked={_revoked}" +
        (Unported() == 0 ? "" : $", {Unported()} LEFT VANILLA (custom sky shader not ported to the Alloy pass)") +
        (_foreign == 0 ? "" : $", {_foreign} foreign-Scene calls skipped");

    /// <summary>Off-device: <see cref="Roles"/>'s gates and their order, over a stand-in Scene. This is the
    /// one piece of the sky that is a decision rather than a replay -- miss a gate and the Alloy pass paints
    /// a sky into an interior, or paints last area's while the new one fades in. The alphas are the other
    /// half: they must sum to 1 across the cross-fade, or the horizon brightens or darkens mid-transition.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"SkyBox.SelfCheck FAIL: {w}"); } }

        byte* scene = stackalloc byte[0x60];
        for (int i = 0; i < 0x60; i++) scene[i] = 0;
        Span<Role> r = stackalloc Role[3];
        IntPtr s = (IntPtr)scene;
        void Gob(int slot, nint v) => *(nint*)(scene + SceneGobs + slot * 8) = v;

        *(int*)(scene + SceneCur) = -1; *(int*)(scene + SceneDesired) = -1;
        E(Roles(s, r) == 0, "no current sky and no fade gob draws nothing");

        Gob(1, 0x1000); *(int*)(scene + SceneCur) = 1;
        E(Roles(s, r) == 1 && r[0].Gob == 0x1000 && r[0].Alpha == 1f && r[0].Dst == BlendZero,
          "a settled sky is one opaque-alpha replace draw");

        *(byte*)(scene + SceneOff) = 1;
        E(Roles(s, r) == 0, "TurnOffSkyBox wins over everything");
        *(byte*)(scene + SceneOff) = 0;

        Gob(2, 0x2000); *(int*)(scene + SceneDesired) = 2; *(float*)(scene + SceneT) = 0.25f;
        E(Roles(s, r) == 2 && r[0].Alpha == 0.75f && r[1].Gob == 0x2000 && r[1].Alpha == 0.25f
          && r[1].Dst == BlendOverAlpha, "a cross-fade is (1-t) replace then t blended over, in that order");

        *(float*)(scene + SceneT) = 0f;
        E(Roles(s, r) == 1, "t at the epsilon leaves the desired sky out -- the engine's own DAT_013eeeb0 test");

        // the fade gob is read at +0x108, so it needs a buffer that reaches there -- and its alpha is the
        // one Roles output that comes off the gob rather than off the Scene, so assert the value too
        byte* fadeGob = stackalloc byte[GobAlpha + 4];
        for (int i = 0; i < GobAlpha + 4; i++) fadeGob[i] = 0;
        *(float*)(fadeGob + GobAlpha) = 0.5f;
        *(nint*)(scene + SceneFadeGob) = (nint)fadeGob;
        E(Roles(s, r) == 2 && r[1].Gob == (IntPtr)fadeGob && r[1].Alpha == 0.5f && r[1].Dst == BlendOverAlpha,
          "the skyfade gob draws last, over-blended, on its own alpha");

        // #150: the death notice, over the table itself. Begin cannot be exercised off-device (it reads the
        // part and asks the material for its shader), but the half that matters -- a revoked slot stops being
        // Fresh, and a slot that was never captured is untouched -- is pure table work.
        int savedCount = _count; long savedRevokes = _revoked;
        _count = 2;
        _parts[0] = new Part { Tri = 0x1000, Mat = 0x2000, Vdo = 0x3000, Ported = true, Frame = _frame };
        _parts[1] = new Part { Tri = 0x4000, Mat = 0x5000, Vdo = 0x6000, Ported = true, Frame = _frame };
        E(Fresh(0) && Fresh(1), "a captured, ported, current part is fresh");
        Revoke(0x1000);
        E(!Fresh(0) && _parts[0].Mat == IntPtr.Zero && _revoked == savedRevokes + 1,
          "a destroyed part is tombstoned, drops its material, and stops being fresh");
        E(Fresh(1) && _parts[1].Mat == 0x5000, "...and its neighbour is untouched");
        Revoke(0x1000);
        E(_revoked == savedRevokes + 1, "a second death notice for the same part revokes nothing twice");
        _count = savedCount; _revoked = savedRevokes;
        _parts[0] = default; _parts[1] = default;

        log.Info(ok ? "SkyBox.SelfCheck: passed" : "SkyBox.SelfCheck: FAILED");
    }
}
