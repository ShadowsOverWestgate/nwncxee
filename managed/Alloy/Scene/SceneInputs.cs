using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using static CX.Framework.EngineLayout;

namespace CX.Alloy;

/// <summary>
/// What feeds the Alloy shadow pipeline: the light walk and the caster walk. It renders nothing --
/// <see cref="CasterPass"/> is the ONE shadow system (#98 cutover), owning the sun cascades and the
/// local-light cubes in the #80 4.6 context on its own submission path. What lives here is everything
/// those views need to be CORRECT rather than merely drawn, all of it gathered from the engine's own
/// structures:
/// <list type="bullet">
/// <item>the LightManager MASTER light list, walked whole every frame into <see cref="FrameInputs"/> --
/// the area-wide, camera-independent snapshot the clustered lighting (#83) and the cubes both read;</item>
/// <item>the shared per-frame shadow focus, published as <c>FrameInputs.Focus</c>;</item>
/// <item>the area's interior flag, which is what tells CasterPass there is no sun to fit.</item>
/// </list>
/// <para>All of it runs in the one deterministic per-frame slot (ADR 0007 phase 1:
/// <c>Scene::AddPartsToDrawBuckets</c>, after ManageSceneBSP and before any draw), and none of it touches
/// GL -- it is also where this service drives <see cref="CasterPass.OnPassSlot"/> (#118), so the world casters
/// bake ahead of the colour pass that samples them. The <c>shadows=0</c> config setting stands the whole thing down, which
/// since #118 also means CasterPass never ticks -- without this feed it had nothing to bake.</para>
/// </summary>
/// <remarks>
/// Light identities are POINTERS the engine frees on area and module boundaries -- they are compared,
/// never dereferenced outside the walk that read them. Hot: the walk runs once a frame and allocates
/// nothing (the <c>/shadow torchdiag</c> and <c>/shadow lightdiag</c> traces do allocate log strings,
/// deliberately -- they are off by default).
/// </remarks>
public sealed unsafe class SceneInputs : IExtenderService
{
    /// <summary>How far ahead of the camera the map centre marches (<c>/shadow focusdist</c>).</summary>
    private static float _focusDist = FocusDistBoot;
    private static ExtenderLog? _log;
    /// <summary>#456: the CAurBehavior layout probe's one-shot latch.</summary>
    private static bool _probedBehavior;
    private static bool _enabled, _diag;
    /// <summary>#404: how far <see cref="FrameStart"/> runs, for one-restart bisection on radeonsi.
    /// 1 = light walk only, 2 = + focus, 3 = + cube bind, 4 = + caster bake (full). Boot only.</summary>
    private static int _stage = 4;

    /// <summary>The one "we own shadows this frame" predicate: armed, in a module, and the frame latched
    /// owned (#86). No GL term any more -- this service stopped touching GL when the maps moved to
    /// CasterPass, and a dead flag in here would read as a live safety gate that can no longer fire.</summary>
    private static bool Owns => _enabled && GameGate.InModule && AlloyGate.Alloy;


    private static IntPtr _currentCamera;   // &CurrentCamera global; walk it to the camera-follow target (the PC)
    private static bool _localLoggedOnce;   // slice 6: one-shot log of the first frame that records point lights

    // The point-light cubes moved to CasterPass (#95): the Alloy multidraw path renders every local light's six
    // faces into one cube ARRAY, and the receiver samples them as a samplerCubeArrayShadow. What is left here is
    // the light WALK that feeds it -- the LightManager master list, the shared shadow focus and the area's
    // interior flag.
    //
    // #282 took the rest: a BSP sphere re-walk that re-submitted camera-culled tile parts into the engine's
    // mesh buckets, and an off-screen gob append into its gobbucket. Both existed to make the ENGINE draw a
    // caster so the capture path could see it, on every scene populate, out to 120u on enrollment frames --
    // and since M5 strand 1 nothing reads the engine's draw buckets as a caster source: SceneWalk enumerates
    // the area's containers directly, so a part behind the camera is in the table whether or not the engine
    // ever meant to draw it. The `shadow_rewalk` / `shadow_gobs` settings and the `/shadow cubewalk` /
    // `/shadow nearradius` dials went with the work they gated.

    // ---- the camera-follow anchor chain (#65) ----
    /// <summary>Behavior-host vtable slot for <c>AsBehaviorCameraFollow()</c> -- pure <c>return this</c> for a follow
    /// cam, null otherwise (a base-class type filter). Replicated exactly from ManageSceneBSP's own devirt call, so
    /// non-follow cameras (free-cam/cutscene) safely yield null.</summary>
    private static readonly int BehaviorAsFollowVtbl = CX.Framework.EngineLayout.BehaviorAsFollowSlot;

    // One map centre per presented frame, shared by the re-walk and published as FrameInputs.Focus for
    // CasterPass's cascade fit. Player-anchored when the controlled creature is reachable, else the
    // camera-ground focus -- but ONE value per frame either way.
    // Interior areas have no sun, so nothing directional is worth rendering there -- the flag is published for
    // CasterPass, which skips its cascades on it. The point-light cubes are independent and keep running.
    // Verified via Ghidra: current area = *(CClientExoApp::GetModule(*g_pAppManager) + 0xf8) (the engine's own
    // HandleServerToPlayerArea chain); CNWCArea+8 = the ARE Flags DWORD (CNWSArea::PackAreaIntoMessage writes
    // m_nFlags@+8 -> CNWCArea::LoadArea reads it into +8), bit 0 = interior.
    private static bool _interior;                    // current area is interior (recomputed once per frame)
    private static IntPtr _appMgr;                    // &g_pAppManager (deref per use)
    private static delegate* unmanaged<IntPtr, IntPtr> _getModule;
    private static bool _interiorLogged;              // log interior/exterior transitions
    /// <summary><c>/shadow trace 1</c>: periodic trace of the light walk -- allocates log strings; diagnosis only.</summary>
    private static bool _trace;
    /// <summary>Boot values of the <c>/shadow</c> dials -- the code defaults now that the env overrides are
    /// gone (ADR 0013), and what <c>/shadow reset</c> restores.</summary>
    private const float FocusDistBoot = 24f;
    /// <summary>Boot value of <see cref="_lightCullMode"/> (see it for what the modes mean).</summary>
    private const int LightCullBoot = 1;

    /// <summary>The one-time <c>[shadowdiag]</c> dumps, off by default.</summary>
    internal static bool Diag => _diag;

    private static bool _passDoneThisFrame, _interiorDecided;
    private static bool _haveFocus;
    private static Vector3 _focus;
    private static int _focusSource = -1;     // 0=camera-target, 2=camera-ground; log on transition
    private static int _focusFrames;          // frames held on the current source (reported at the next transition)


    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // Registered ahead of the stand-down, exactly as RenderCommands does: a session with the feed off
        // should still answer /shadow with "off" rather than not know the command.
        CommandRegistry.Global.Register("/shadow",
            "report or tune the shadow feed and caster; /shadow reset restores boot values", ShadowCommand);

        // Config bucket (ADR 0013): this arms the feed's hooks, so it is a boot decision. Everything the
        // walk merely LOOKS at -- radii, cull mode, the diagnostics -- is /shadow's.
        _enabled = Settings.Bool("shadows", true);
        _stage = Settings.Int("shadowstage", 4, 1, 4);
        if (_stage < 4) log.Info($"SceneInputs: shadowstage={_stage} -- the shadows feed stops after {(_stage == 1 ? "the light walk" : _stage == 2 ? "the focus" : "the cube bind (no bake)")}");

        ShadowMath.SelfCheck(log);   // pure math, runs headless
        FadeSelfCheck(log);          // ...and so does the fade-out bookkeeping
        // #257: the frame constants are read, not captured -- and ahead of the stand-down, because the
        // camera, the area terms and the fog are inputs to EVERY Alloy pass, shadows or no shadows.
        FrameRead.Resolve(log);

        if (!_enabled) return;

        _appMgr = Native.Sym(SymbolMap.AppManager);
        _getModule = (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetModule);
        _getGspF = (delegate* unmanaged<uint, float>)Native.Sym(SymbolMap.RiGetGlobalShaderParamf);
        _getAdjRadius = (delegate* unmanaged<IntPtr, float>)Native.Sym(SymbolMap.GetLightAdjustedRadius);

        // Camera-follow anchor: the real PC position, camera- and shadow-path-independent (Scene+0x58 reads null
        // under the vanilla-stencil full kill). CurrentCamera is a global pointer slot -- store its address, deref per frame.
        _currentCamera = Native.Sym(SymbolMap.CurrentCamera);

        // The per-presented-frame edge. Was a zero-size UiHost Window abused as a draw slot (#169); #170 gives
        // it the real thing. Priority 0: it ran inside the window roll-call, i.e. before the #80 context bracket.
        FrameRegistry.Global.Register(FramePhase.PrePresent, 0, Draw, "SceneInputs.frameEdge", log);
        log.Info("SceneInputs: light walk armed, feeding CasterPass (config shadows=0 disables)");
        log.Info($"SceneInputs: effective tuning: {Describe()}");
    }

    /// <summary>Keys <c>/shadow</c> accepts. The first two are <see cref="CasterPass"/>'s, the rest this
    /// feed's -- one command over the whole shadow path, because a player tuning it is looking at one thing.</summary>
    private const string ShadowKeys = "dist|lod|rebake|gather|split|layered|refreshall|dynhold|dynevents|dynranks|movers|focusdist|lightcull|diag|lightdiag|torchdiag|trace|mask|maskholes|maskh|fade|fadeslots|staticcap|flipcollect|contact|contactsteps|contactfar";

    /// <summary>ADR 0013 bucket 1, in the shape <c>RenderCommands.Tune</c> established: <c>/shadow</c> reports,
    /// <c>/shadow &lt;key&gt; &lt;value&gt;</c> sets, <c>/shadow reset</c> restores the boot values. Every one
    /// of these is judged while walking around -- a cull radius against pop-in, a diagnostic at the spot where
    /// the bug happens -- which is exactly why none of them is a config setting.</summary>
    private static string? ShadowCommand(string args)
    {
        string[] a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0) return $"[shadow] {Describe()}";
        if (a.Length == 1 && a[0] == "reset")
        {
            ResetTuning();
            CasterPass.ResetTuning();
            ContactShadow.ResetTuning();
            return $"[shadow] reset -> {Describe()}";
        }
        if (a.Length != 2 || !float.TryParse(a[1], System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out float v)
            || (v < 0f && a[0] != "mask"))
            return $"[shadow] usage: /shadow | /shadow <{ShadowKeys}> <value> | /shadow reset";

        float old = Tune(a[0], v);
        if (float.IsNaN(old)) old = CasterPass.Tune(a[0], v);
        if (float.IsNaN(old)) old = ContactShadow.Tune(a[0], v);
        return float.IsNaN(old)
            ? $"[shadow] unknown knob \"{a[0]}\" -- try {ShadowKeys.Replace('|', ' ')}"
            : $"[shadow] {a[0]} {F(v)} (was {F(old)}) -> {Describe()}";
    }

    /// <summary>The live tuning state, feed and caster on one line.</summary>
    internal static string Describe()
        => $"feed {(_enabled ? "on" : "off")} focusdist {_focusDist:F0} " +
           $"lightcull {_lightCullMode} " +
           $"diag {B(_diag)}{B(_lightDiag)}{B(_torchDiag)}{B(_trace)} | {CasterPass.Describe()} | {ContactShadow.Describe()}";

    private static char B(bool f) => f ? '1' : '0';

    /// <summary>Echo a set value in the same invariant form <c>/shadow</c> ACCEPTS, so a reported number can
    /// be typed straight back in on a comma-decimal desktop.</summary>
    private static string F(float v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Set one of this feed's dials, returning its old value; NaN when the key is not ours, which is
    /// how <see cref="ShadowCommand"/> knows to offer it to <see cref="CasterPass"/>.</summary>
    private static float Tune(string key, float v)
    {
        float old;
        switch (key)
        {
            case "focusdist": old = _focusDist; _focusDist = v; return old;
            case "lightcull": old = _lightCullMode; _lightCullMode = (int)MathF.Min(v, 2f); return old;
            case "diag": old = _diag ? 1f : 0f; _diag = v != 0f; return old;
            case "lightdiag": old = _lightDiag ? 1f : 0f; _lightDiag = v != 0f; return old;
            case "torchdiag": old = _torchDiag ? 1f : 0f; _torchDiag = v != 0f; return old;
            case "trace": old = _trace ? 1f : 0f; _trace = v != 0f; return old;
            default: return float.NaN;
        }
    }

    /// <summary>Back to the boot values -- diagnostics off, radii as shipped.</summary>
    private static void ResetTuning()
    {
        _focusDist = FocusDistBoot;
        _lightCullMode = LightCullBoot;
        _diag = _lightDiag = _torchDiag = _trace = false;
    }

    /// <summary>#257: the light feed's frame slot, called by <see cref="AlloyRenderer"/> at the frame
    /// anchor -- Alloy's own moment, not an engine draw hook. It sits after the engine's
    /// <c>LightManager::EnableLights</c> (so the fade weights are this frame's) and before anything draws,
    /// which is the same position in the frame ADR 0007 phase 1 picked; what changed is who decides it.
    /// <para>First fire per presented frame only: a reflection subframe re-renders the scene, and the maps
    /// are light-space (camera-independent), so a second walk would recompute the same answer.</para>
    /// <para>#118: the world-caster bake hangs off the end of this, so ADR 0011's shadow bracket opens here
    /// instead of at the present. The bracket count is unchanged -- it moved, it did not multiply.</para></summary>
    internal static void FrameStart(IntPtr scene)
    {
        if (!_enabled || scene == IntPtr.Zero || _passDoneThisFrame) return;
        // The Alloy pass's light SSBO needs the snapshot even when the shadow pipeline itself is off, so
        // the walk is unconditional inside this slot.
        // #238: one zone over the whole slot -- the light walk, the focus and the caster bake are one
        // non-overlapping span of the frame, and it is a SIBLING of the shim rather than nested in it.
        long tShadow = FrameZones.Now;
        WalkMasterList(*(IntPtr*)(scene + SceneLightManager));
        FrameZones.Add(FrameZones.Zone.Lwalk, tShadow);
        if (_stage < 2) { _passDoneThisFrame = true; return; }
        // The shadow focus every Alloy view fits around.
        ShadowFocus();
        if (_stage < 3) { _passDoneThisFrame = true; return; }
        // #282: the interior flag used to be decided in the ManageSceneBSP hook that carried the re-walk.
        // It is not re-walk work -- it is what tells CasterPass there is no sun to fit -- so it moved here,
        // still once per presented frame and still ahead of the bake that reads it.
        if (!_interiorDecided)
        {
            _interiorDecided = true;
            bool wasInterior = _interior;
            _interior = AreaIsInterior();
            FrameInputs.Interior = _interior;
            if (_interior != wasInterior || !_interiorLogged)
            { _interiorLogged = true; _log?.Info($"SceneInputs: area is {(_interior ? "INTERIOR -- no sun shadows" : "exterior -- sun shadows on")}"); }
        }
        // #118: the world-caster bake, so the cascades and cubes are baked before the colour pass that
        // samples them.
        long tBake = FrameZones.Now;
        if (_stage >= 4) CasterPass.OnPassSlot();
        FrameZones.Add(FrameZones.Zone.Bake, tBake);
        // AFTER the bake, so a light and the cube it points at come from the SAME bake. Binding before it
        // would map this frame's lights onto last frame's slot assignment, and the nearest-first cube sort
        // reshuffles slots -- a light would sample another light's cube for a frame.
        BindLightCubes();
        // AFTER the bind it reports: DumpNearest prints each light's cube SLOT, so run from the end of the
        // walk it named last frame's assignment against this frame's positions -- wrong in exactly the
        // slot-reshuffle case ("the carried torch blinks") the diagnostic exists for.
        FrameZones.Add(FrameZones.Zone.Shadow, tShadow);
        if (_torchDiag) DumpNearest();
        _passDoneThisFrame = true;
    }


    // ---- #336: the fade-out the engine has no edge for ----
    // A light that APPEARS ramps: the engine grows its intensity, and since the lit radius is
    // range * intensity (GetLightAdjustedRadius) the pool grows out of the source. A light that GOES OUT
    // is simply unlinked from the master list, so it pops. This is the missing half, on Alloy's side: the
    // last known state of a light that left the list is re-emitted for a few frames with its intensity
    // scaled down, which shrinks the pool back into the source on the same curve.

    /// <summary>Frames a departed light takes to fade. <c>/light fadeout</c>; 0 = pop, as before.</summary>
    internal static int FadeFrames = FadeFramesBoot;
    internal const int FadeFramesBoot = 20;
    /// <summary>Lights allowed to fade at once. More than this vanishing in ONE frame is a load edge -- an
    /// area transition unlinks the whole list -- and the ghosts are dropped rather than played out.</summary>
    private const int MaxGhosts = 16;

    private static IntPtr[] _prevId = new IntPtr[64];
    private static Vector4[] _prevPosW = new Vector4[64];
    private static Vector3[] _prevCol = new Vector3[64];
    private static int _prevN;

    private static readonly IntPtr[] _ghostId = new IntPtr[MaxGhosts];
    private static readonly Vector4[] _ghostPosW = new Vector4[MaxGhosts];
    private static readonly Vector3[] _ghostCol = new Vector3[MaxGhosts];
    private static readonly int[] _ghostLeft = new int[MaxGhosts];
    private static int _ghostN;

    /// <summary>#336: the fade bookkeeping, headless. It is a set difference over two flat arrays with a
    /// swap-remove in the middle, and every way it can go wrong is silent -- a ghost that never retires is a
    /// light that never goes out, a leaver that is missed is the pop this exists to remove.</summary>
    internal static void FadeSelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"SceneInputs.FadeSelfCheck FAIL: {w}"); } }

        int fadeWas = FadeFrames, prevWas = _prevN, ghostWas = _ghostN;
        FadeFrames = 3; _prevN = 0; _ghostN = 0;

        void Lit(params int[] ids)
        {
            FrameInputs.BeginAreaLights(ids.Length);
            foreach (int id in ids)
                FrameInputs.RecordAreaLight(id, new Vector3(id, 0f, 0f), 10f, Vector3.One);
            FadeDepartedLights();
        }

        Lit(1, 2);
        E(FrameInputs.LightCount == 2 && _ghostN == 0, "two lit lights, nothing fading");
        Lit(1);
        E(_ghostN == 1 && FrameInputs.LightCount == 2, "a departed light is still emitted, once");
        E(FrameInputs.AreaLightPosRange[1].W < 10f, "...at a shrunken radius");
        E(FrameInputs.AreaLightPosRange[1].W > 0f, "...and not to nothing on its first frame");
        Lit(1); Lit(1); Lit(1);
        E(_ghostN == 0 && FrameInputs.LightCount == 1, "and retires after FadeFrames frames");
        Lit(1, 2); Lit(1);
        E(_ghostN == 1, "light 2 fades again");
        Lit(1, 2);
        E(_ghostN == 0 && FrameInputs.LightCount == 2, "a light that comes back drops its ghost");
        // The load edge: more leavers than the cap in one frame is an area transition, not a torch.
        Lit(1, 2, 3, 4); FadeFrames = 3; _ghostN = 0;
        var many = new int[MaxGhosts + 4];
        for (int i = 0; i < many.Length; i++) many[i] = 100 + i;
        Lit(many); Lit(1);
        E(_ghostN == 0, "a whole list vanishing at once fades nothing");
        FadeFrames = 0; Lit(1, 2); Lit(1);
        E(_ghostN == 0 && FrameInputs.LightCount == 1, "fadeout 0 pops, as before");

        FadeFrames = fadeWas; _prevN = prevWas; _ghostN = ghostWas;
        FrameInputs.BeginAreaLights(0);
        log.Info(ok ? "SceneInputs.FadeSelfCheck: passed" : "SceneInputs.FadeSelfCheck: FAILED -- see above");
    }

    /// <summary>Is this light in the set just admitted? The master list keeps its order frame to frame, so
    /// the paired index walks in step and the full scan is the reorder path, not the common one.</summary>
    private static bool StillLit(IntPtr id, int cur, ref int j)
    {
        if (j < cur && FrameInputs.AreaLightId[j] == id) { j++; return true; }
        for (int k = 0; k < cur; k++)
            if (FrameInputs.AreaLightId[k] == id) { j = k + 1; return true; }
        return false;
    }

    /// <summary>Age the fading lights, emit them, and take this frame's set as the next comparison. Called
    /// with <see cref="FrameInputs.LightCount"/> holding the lights the walk actually admitted -- the ghosts
    /// are appended after the snapshot, so a ghost never seeds another ghost.</summary>
    private static void FadeDepartedLights()
    {
        int cur = FrameInputs.LightCount;
        // A light that came back keeps nothing of its ghost; the live entry above is already the real one.
        for (int i = 0; i < _ghostN; i++)
        {
            int j0 = 0;
            if (--_ghostLeft[i] > 0 && !StillLit(_ghostId[i], cur, ref j0)) continue;
            _ghostN--;
            _ghostId[i] = _ghostId[_ghostN]; _ghostPosW[i] = _ghostPosW[_ghostN];
            _ghostCol[i] = _ghostCol[_ghostN]; _ghostLeft[i] = _ghostLeft[_ghostN];
            i--;
        }
        if (FadeFrames > 0)
        {
            int gone = 0, j = 0;
            for (int i = 0; i < _prevN; i++)
            {
                if (StillLit(_prevId[i], cur, ref j)) continue;
                if (++gone > MaxGhosts - _ghostN) { _ghostN = 0; break; }
                _ghostId[_ghostN] = _prevId[i]; _ghostPosW[_ghostN] = _prevPosW[i];
                _ghostCol[_ghostN] = _prevCol[i]; _ghostLeft[_ghostN++] = FadeFrames;
            }
        }

        if (cur > _prevId.Length)
        {
            _prevId = new IntPtr[cur * 2];
            _prevPosW = new Vector4[cur * 2];
            _prevCol = new Vector3[cur * 2];
        }
        _prevN = cur;
        Array.Copy(FrameInputs.AreaLightId, _prevId, cur);
        Array.Copy(FrameInputs.AreaLightPosRange, _prevPosW, cur);
        Array.Copy(FrameInputs.AreaLightColor, _prevCol, cur);

        for (int i = 0; i < _ghostN; i++)
        {
            // Intensity is the engine's own ramp on the way in, and the radius rides it -- so scaling both
            // by the same k is what makes the pool shrink INTO the source instead of dimming in place.
            // +1 so the first ghost frame is already below full: the light was at full strength the frame
            // before, and re-emitting it unchanged would spend one frame of the fade standing still.
            float k = (float)_ghostLeft[i] / (FadeFrames + 1);
            var pw = _ghostPosW[i];
            FrameInputs.RecordAreaLight(_ghostId[i], new Vector3(pw.X, pw.Y, pw.Z), pw.W * k, _ghostCol[i] * k);
        }
    }
    /// <summary>RenderInterface::GetGlobalShaderParamf -- param 0x12 = the engine's light-radius multiplier.</summary>
    private static delegate* unmanaged<uint, float> _getGspF;
    /// <summary>GetLightAdjustedRadius -- the engine's own lit-radius calculation, called rather than
    /// re-derived so the Alloy pass cannot drift from it.</summary>
    private static delegate* unmanaged<IntPtr, float> _getAdjRadius;
    private static int _walkDiagTick;

    /// <summary>Light-cull mode (<c>/shadow lightcull</c>): 0 = keep every area light, 1 = camera-distance
    /// only (default), 2 = camera-frustum sphere test (opt-in until live-proven -- the first frustum cut
    /// used the unverified view quaternion and blacked out every light off the torch). Shrinks the
    /// per-fragment Alloy loop light set.</summary>
    private static int _lightCullMode = LightCullBoot;

    /// <summary>See <see cref="ShadowMath.FrustumCullSlack"/> -- shared with the cube-bake cull (#322).</summary>
    private const float FrustumCullSlack = ShadowMath.FrustumCullSlack;
    private static int _lightsCulled;
    private static bool _lightDiag;


    /// <summary>Record EVERY shadow-worthy point light from the LightManager's master list into
    /// <see cref="FrameInputs"/> -- the area-wide, camera-independent snapshot feeding the Alloy pass's
    /// light SSBO and the cube set. Runs ONCE per frame from the deterministic pass
    /// slot (ADR 0007 phase 2), while the manager is alive inside <c>Scene::Render</c>. Alloc-free;
    /// pointers are only read inside this call, never retained.</summary>
    private static void WalkMasterList(IntPtr self)
    {
        if (self == IntPtr.Zero || !GameGate.InModule) return;
        int count = *(int*)((byte*)self + LmMasterCount);
        IntPtr data = *(IntPtr*)((byte*)self + LmMasterData);
        if (data == IntPtr.Zero) return;
        float radiusMult = _getGspF != null ? _getGspF(0x12) : 1f;
        if (!(radiusMult > 0f)) radiusMult = 1f;
        // #140: the two constants the engine's own point-light attenuation is built from, from the same
        // global-param array the radius multiplier comes out of (GLRender::SetUniforms @0x4cd341 reads
        // 0x12/0x13/0x14 and derives lightFalloffFactor and lightMaxIntensityInv from exactly these).
        // Read every capture rather than once: they are graphics settings and the player can change them.
        if (_getGspF != null)
            CX.Alloy.AlloyLights.LatchAtten(_getGspF(0x13), _getGspF(0x14), radiusMult);
        // BOTH snapshots restart here, not at the frame edge. CasterPass consumes the local-light set later
        // in this same pass slot, and the roll-call this service's frame edge rides runs at the present --
        // resetting there handed the cube pass an empty set every frame. The walk is once-per-frame and
        // idempotent, so restarting it at its own top is the same reset with no ordering to get wrong.
        FrameInputs.BeginAreaLights(count);
        FrameInputs.ResetLocalLights(count);
        bool walkDiag = _trace && ++_walkDiagTick >= 900;   // ~15s at 60fps (one walk/frame): dump raw filter fields near the camera
        if (walkDiag) _walkDiagTick = 0;
        if (walkDiag)
            _log?.Info($"SceneInputs: [lightdiag] radiusMult(0x12)={radiusMult:F3} minI(0x13)={(_getGspF != null ? _getGspF(0x13) : -1f):F3} " +
                       $"maxI(0x14)={(_getGspF != null ? _getGspF(0x14) : -1f):F3} -> atten {CX.Alloy.AlloyLights.Atten.X:F3}/{CX.Alloy.AlloyLights.Atten.Y:F3} " +
                       $"| area ambient=({FrameInputs.AreaAmbient.X:F3},{FrameInputs.AreaAmbient.Y:F3},{FrameInputs.AreaAmbient.Z:F3}) " +
                       $"diffuse=({FrameInputs.AreaDiffuse.X:F3},{FrameInputs.AreaDiffuse.Y:F3},{FrameInputs.AreaDiffuse.Z:F3}) " +
                       $"dir=({FrameInputs.AreaDiffuseDir.X:F3},{FrameInputs.AreaDiffuseDir.Y:F3},{FrameInputs.AreaDiffuseDir.Z:F3}) " +
                       $"hasAmb={FrameInputs.HasAreaAmbient} hasDif={FrameInputs.HasAreaDiffuse} | ENGINE {FrameRead.EngineAreaTerms()}");
        // the fog cut's metric, once per frame (PlayerPos is an engine-memory read)
        float fogOpaque = CullPass.FogCull ? SceneFog.OpaqueDistance() : float.PositiveInfinity;
        Vector3 fogOrigin = SceneFog.Origin(out _);
        for (int i = 0; i < count; i++)
        {
            byte* p = (byte*)*((IntPtr*)data + i);
            if (p == null) continue;
            if (walkDiag && FrameInputs.HasCamera)
            {
                var wp = new Vector3(*(float*)(p + LightDir), *(float*)(p + LightDir + 4), *(float*)(p + LightDir + 8));
                float wd = (wp - FrameInputs.CameraPos).Length();
                if (wd < 40f)
                    _log?.Info($"SceneInputs: masterlight d={wd:F1} pos=({wp.X:F0},{wp.Y:F0},{wp.Z:F1}) type={*(int*)(p + LightKind)} cast={*(int*)(p + LightCastShadows)} flags={*(int*)(p + LightFlagA)}/{*(int*)(p + LightFlagB)} range={*(float*)(p + LightRange):F1} alt={*(float*)(p + LightRangeAlt):F1} inten={*(float*)(p + LightIntensity):F2} fade={*(float*)(p + LightFade):F2} rgb=({*(float*)(p + LightColour):F2},{*(float*)(p + LightColour + 4):F2},{*(float*)(p + LightColour + 8):F2}) adjR={(_getAdjRadius != null ? _getAdjRadius((IntPtr)p) : -1f):F2}");
            }
            if (*(int*)(p + LightFlagA) == 0 && *(int*)(p + LightFlagB) == 0) continue;
            float range = *(float*)(p + LightRange);
            if (range <= 0f || range >= DirectionalRange) continue;
            float inten = *(float*)(p + LightIntensity);
            if (inten <= 0f) continue;
            float r = *(float*)(p + LightColour), g = *(float*)(p + LightColour + 4), b = *(float*)(p + LightColour + 8);
            if (r == 0f && g == 0f && b == 0f) continue;
            if (*(int*)(p + LightKind) != 0) continue;
            float alt = *(float*)(p + LightRangeAlt);
            var pos = new Vector3(*(float*)(p + LightDir), *(float*)(p + LightDir + 4), *(float*)(p + LightDir + 8));
            // Vanilla lit radius (Ghidra GetLightAdjustedRadius @0x5c0c10): range * intensity, an
            // overbright boost by the max color channel (with the color normalized down to match), then
            // the GLOBAL radius multiplier (shader param 0x12). Uploading raw +0x70 shrank every lit
            // sphere vs vanilla -- neighboring lights stopped overlapping ("two lights can't overlay",
            // ground at the fringe went black while raised objects stayed lit, #67).
            // Lit radius and colour come from the engine's OWN pair (Ghidra GetLightAdjustedRadius
            // @0x4c0c10 / GetLightAdjustedColor @0x4c0b60): R = range * intensity, boosted by the max
            // colour channel when it exceeds 1, times the global radius multiplier (shader param 0x12);
            // the colour is then divided back down by that same max, so an overbright light spends its
            // surplus on REACH, not on hue. A torch reads rgb=(1.75,1.50,1.00) -- shipping that raw as a
            // multiplier is what tinted every Alloy surface brown.
            // The radius is the engine's own exported function, so it cannot drift from it.
            float adjR = _getAdjRadius != null ? _getAdjRadius((IntPtr)p) : range * inten * radiusMult;
            float maxc = MathF.Max(r, MathF.Max(g, b));
            if (maxc > 1f)
            {
                // Vanilla (GetLightAdjustedColor @0x4c0b60) divides an overbright colour by its max channel
                // and spends the whole surplus on REACH instead -- which is real, because its attenuation is
                // range-normalised, and since #140 so is ours. So the hue normalisation below is the engine's
                // own and the radius above already carries the surplus.
                //
                // What is NOT the engine's is keeping a fraction of that surplus as centre intensity:
                // OverbrightScale, 0 = vanilla, 1 = the authored surplus in full. Overbright is what tells a
                // carried torch from module decor without a type discriminator -- placed lights have no
                // surplus at all -- and a light you carry ought to read as special.
                //
                // The ^(1/SrgbGamma) pre-compensates the receiver's own pow(c, 2.2), so this lands there as a
                // clean linear-space multiply and the hue comes through exactly.
                float boost = 1f + (maxc - 1f) * CX.Alloy.AlloyLights.OverbrightScale;
                if (boost < 0f) boost = 0f;
                float k = MathF.Pow(boost, 1f / CX.Alloy.AlloyLights.SrgbGamma) / maxc;
                r *= k; g *= k; b *= k;
            }
            float upW = adjR;
            var color = new Vector3(r, g, b);
            // Receiver light cull (#67 perf): the Alloy fragment loop iterates EVERY uploaded light, so a
            // light whose lit sphere can't touch a drawn fragment still costs every fragment a range test.
            // Mode 1 (default) = camera-distance vs the far plane (orientation-free: immune to the
            // reflection subframe's mirrored camera and needs no view-rotation convention). Mode 2 =
            // frustum sphere test on a camera->focus look-at basis (tighter; opt-in until live-proven).
            // 0 = off. Cube BAKES are culled too since #322, by the same predicate and slack (CasterPass
            // CubePass); hidden slots drain one warm-up bake per frame instead of staying warm outright.
            bool culled = false;
            if (_lightCullMode > 0 && FrameInputs.HasCamera && FrameInputs.HasPerspective)
            {
                float slackR = adjR * FrustumCullSlack;
                // #257: the scene camera's own far plane. The FarMax rollover this used to need existed
                // because the far plane was captured per PASS and an auxiliary subframe's small far could
                // win the frame -- which culled the carried torch. There is one camera read now.
                // #336: a light whose whole lit sphere sits past the fog's opaque distance shades nothing
                // visible, and this same predicate gates its cube BAKE -- which is what stopped the fully
                // fogged half of the street from baking six 512 faces apiece.
                float cullFar = FrameInputs.CamFar;
                bool vis = _lightCullMode == 1 || _focus == default
                    ? (pos - FrameInputs.CameraPos).LengthSquared() < (cullFar + slackR) * (cullFar + slackR)
                    : ShadowMath.SphereInCameraFrustum(pos, slackR, FrameInputs.CameraPos, _focus,
                                                       FrameInputs.CamFovy, FrameInputs.CamAspect, FrameInputs.CamNear, cullFar);
                // ...measured the way the fog is, from SceneFog.Origin (the view-depth fallback keeps the
                // old camera-radius approximation).
                if (vis && fogOpaque < float.PositiveInfinity && (pos - fogOrigin).Length() - slackR > fogOpaque) vis = false;
                if (!vis)
                {
                    _lightsCulled++;
                    // a light culled NEAR the camera = cull inputs are garbage; name them
                    if (_lightDiag && (pos - FrameInputs.CameraPos).LengthSquared() < 625f)
                        _log?.Info($"SceneInputs: [lightdiag] NEARCULL pos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) adjR={adjR:F1} cam=({FrameInputs.CameraPos.X:F1},{FrameInputs.CameraPos.Y:F1},{FrameInputs.CameraPos.Z:F1}) far={FrameInputs.CamFar:F1} mode={_lightCullMode}");
                    culled = true;
                }
            }
            int snapIdx = -1;
            if (!culled)
            {
                snapIdx = FrameInputs.LightCount;
                FrameInputs.RecordAreaLight((IntPtr)p, pos, upW, color);
                if (FrameInputs.LightCount == snapIdx) snapIdx = -1;   // snapshot full: the light was dropped
            }
            if (_torchDiag) TrackNearest(p, pos, range, alt, inten, color, adjR, upW, culled, snapIdx);
            if (*(int*)(p + LightCastShadows) == 0) continue;
            // #186: the cube's reach is the LIT radius exactly -- the same sphere the receiver's attenuation
            // has support over, and the same one the engine lights with. Ghidra: SetLightGL @0x4c0e60 uploads
            // GetLightAdjustedRadius(pl)^2 as the light's range (property 4), and inc_lighting.shd's
            // ComputePointLightSource returns outright when that value is under the squared distance
            // (GAMMA_CORRECTION == 1, the live path). Neither the engine nor the Alloy receiver has anything
            // to say past it. It used to take max(this, PartLight+0x78) -- the MDL `shadowradius`, an
            // artist-authored override the ENGINE only ever consults from its stencil paths
            // (Scene::DoGobShadows @0x45fbd3, RenderShadowPlanes @0x463e79, PartProjection::Init @0x450193,
            // all under `f78 > 0 ? f78 : f70`). Honouring it here sized the cube and pulled in casters for
            // world the light cannot reach, at no visible gain.
            FrameInputs.RecordLocalLight((IntPtr)p, pos, adjR, color);
        }
        FadeDepartedLights();
        _walkDoneCount = FrameInputs.LightCount;
    }

    /// <summary>CPU-side light-&gt;cube bind: cube slot per AREA light (-1 = no cube), the one thing that tells
    /// the Alloy lit stage which cube layer a light's shadow lives in. Driven from the pass slot AFTER the
    /// bake (#118), so the mapping and the cube contents describe the same frame.</summary>
    private static void BindLightCubes()
    {
        int bound = 0;
        for (int i = 0; i < FrameInputs.LightCount; i++)
            if ((FrameInputs.AreaLightSlot[i] = CasterPass.CubeSlotOf(FrameInputs.AreaLightId[i])) >= 0) bound++;
        // A cube nothing binds to is written every frame and read by no one, and the receiver's -1 path
        // returns "lit" in silence -- so rendered-cube counts alone cannot tell a working shadow from an
        // absent one. This is the number that can.
        FrameInputs.LightsWithCube = bound;
    }

    // ---- /shadow torchdiag: the carried-torch "shuts off" investigation ----
    // The symptom (a light that dies, or collapses to a small pool, in specific expensive spots) has at
    // least four candidate mechanisms that look alike on screen: the wrong attenuation CLASS (the
    // documented "2.6m pool"), a cull rejection, the snapshot/receiver ordering dropping it past a cap, or
    // the #83 froxel binning overflowing. This dumps, for the point light NEAREST the player, every input
    // and every verdict on the same line -- so one capture at a good spot and one at a bad spot names the
    // mechanism instead of narrowing it.

    /// <summary><c>/shadow torchdiag 1</c>: settable at runtime, because the interesting spot is found by
    /// walking to it -- a boot-time flag meant relaunching in the wrong place.</summary>
    private static bool _torchDiag;
    private static int _torchTick;
    /// <summary>Nearest-so-far distance^2 this walk. MUST start at +inf, not the default 0, or the first
    /// walk's comparison rejects every light.</summary>
    private static float _tBest = float.MaxValue;
    private static IntPtr _tPtr;
    private static Vector3 _tPos, _tColor;
    private static float _tRange, _tAlt, _tInten, _tAdjR, _tUpW;
    private static bool _tCulled;
    private static int _tSnapIdx;

    /// <summary>Keep the walk's nearest point light to the player anchor, with the verdict it just got.</summary>
    private static void TrackNearest(byte* p, Vector3 pos, float range, float alt, float inten,
                                     Vector3 color, float adjR, float upW, bool culled, int snapIdx)
    {
        Vector3 anchor = FrameInputs.HasFocus ? FrameInputs.Focus
                       : FrameInputs.HasCamera ? FrameInputs.CameraPos : default;
        float d2 = (pos - anchor).LengthSquared();
        if (d2 >= _tBest) return;
        _tBest = d2; _tPtr = (IntPtr)p; _tPos = pos; _tColor = color;
        _tRange = range; _tAlt = alt; _tInten = inten; _tAdjR = adjR; _tUpW = upW;
        _tCulled = culled; _tSnapIdx = snapIdx;
    }

    /// <summary>One line per ~2s naming every verdict on the nearest light. Cold (period-gated).</summary>
    private static void DumpNearest()
    {
        float best = _tBest;
        IntPtr ptr = _tPtr;
        _tBest = float.MaxValue; _tPtr = IntPtr.Zero;   // never report a light the latest walk did not see
        if (++_torchTick < 120 || ptr == IntPtr.Zero) return;
        _torchTick = 0;
        _tPtr = ptr;
        // The Alloy path only sees what the froxel build binned, and the snapshot INDEX is where to look.
        string reach = _tSnapIdx < 0 ? "CULLED (not in snapshot at all)"
                                     : $"snapshot[{_tSnapIdx}] -- lit and shadow-eligible";
        int slot = -1;
        for (int i = 0; i < FrameInputs.LightCount; i++)
            if (FrameInputs.AreaLightId[i] == ptr) { slot = FrameInputs.AreaLightSlot[i]; break; }
        // Bake separation is THE number for the "torch shuts off" class: the cube describes the position it
        // was rendered from, the BRDF shades from the live position, and the gap between them is false
        // occlusion. CasterPass re-renders every cube every frame, so this should read as one frame of motion.
        string bake = "n/a";
        if (slot >= 0 && slot < CasterPass.CubeCount)
        {
            var baked = new Vector3(CasterPass.CubeLight[slot * 4], CasterPass.CubeLight[slot * 4 + 1],
                                    CasterPass.CubeLight[slot * 4 + 2]);
            bake = $"cube{slot} bakedAt=({baked.X:F1},{baked.Y:F1},{baked.Z:F1}) sep={(_tPos - baked).Length():F2} " +
                   $"far={CasterPass.CubeLight[slot * 4 + 3]:F1}";
        }
        _log?.Info($"SceneInputs: [torchdiag] nearest light d={MathF.Sqrt(best):F1} " +
                   $"pos=({_tPos.X:F1},{_tPos.Y:F1},{_tPos.Z:F1}) rgb=({_tColor.X:F2},{_tColor.Y:F2},{_tColor.Z:F2}) | " +
                   $"CLASS engine-curve " +
                   $"range={_tRange:F1} alt={_tAlt:F1} inten={_tInten:F2} -> adjR={_tAdjR:F1} upW={_tUpW:F1} | " +
                   $"{reach} | cubeSlot={slot} | BAKE {bake} | " +
                   $"snapshot {FrameInputs.LightCount}, " +
                   $"culled/period={_lightsCulled}, cullMode={_lightCullMode} camFar={FrameInputs.CamFar:F0}");
    }
    /// <summary>Snapshot count at the end of the last completed master-list walk; the upload comparing its
    /// live count against this detects mid-walk sampling (lights after the cut go dark that frame).</summary>
    private static int _walkDoneCount;

    /// <summary>The map centre for this frame -- computed once and reused by both the re-walk and every depth-pass
    /// sub-frame so they align. Prefers the camera-follow target (the real PC, camera-independent); falls back to
    /// the LAST known PC position (sticky, so a momentary non-follow subframe doesn't snap the map to the camera
    /// and shimmer); camera-ground only until the PC is ever seen. Logs source TRANSITIONS with the frame count
    /// held, so any fallback shows in the log instead of being hidden by a one-shot. Reset each presented frame.</summary>
    private static Vector3 ShadowFocus()
    {
        if (!_haveFocus)
        {
            int src;
            // Two LIVE sources only (shadows v2): the camera-follow target when there is one, else
            // camera-ground. The old sticky-last bridge could freeze the focus at a stale point (#67);
            // nothing keyed on focus needs sub-frame stability anymore -- selection/LOD anchor on the camera.
            if (CameraTargetFocus() is Vector3 pp) { _focus = pp; src = 0; }
            else { _focus = ShadowMath.CameraGroundFocus(FrameInputs.CameraPos, FrameInputs.CameraRot, _focusDist); src = 2; }
            if (src != _focusSource)
            {
                _log?.Info($"SceneInputs: focus source -> {FocusName(src)} at ({_focus.X:F1},{_focus.Y:F1},{_focus.Z:F1}) " +
                           $"(prev {FocusName(_focusSource)} held {_focusFrames} frames)");
                _focusSource = src; _focusFrames = 0;
            }
            _focusFrames++;
            _haveFocus = true;
            FrameInputs.Focus = _focus; FrameInputs.HasFocus = true;   // the nearest-K key for light recording
        }
        return _focus;
    }

    /// <summary>True when the current area carries the ARE interior flag. Walks the engine's own chain
    /// (**g_pAppManager -&gt; CClientExoApp::GetModule -&gt; module+0xf8 -&gt; area flags at +8, bit 0; the global
    /// needs TWO derefs -- CAppManager first, its first field is the CClientExoApp*), every deref guarded;
    /// false (exterior, sun maps on) whenever the chain is unavailable. Alloc-free, once per frame.</summary>
    private static bool AreaIsInterior()
    {
        if (_appMgr == IntPtr.Zero || _getModule == null) return false;
        IntPtr mgr = *(IntPtr*)_appMgr;          // CAppManager*
        if (mgr == IntPtr.Zero) return false;
        IntPtr app = *(IntPtr*)mgr;              // CClientExoApp* (mgr's first field -- the UiHostService gate chain)
        if (app == IntPtr.Zero) return false;
        IntPtr module = _getModule(app);
        if (module == IntPtr.Zero) return false;
        IntPtr area = *(IntPtr*)((byte*)module + ModuleCurrentArea);
        if (area == IntPtr.Zero) return false;
        return (*(uint*)((byte*)area + AreaFlags) & 1) != 0;
    }

    private static string FocusName(int s) => s switch { 0 => "camera-target", 2 => "camera-ground", _ => "none" };

    /// <summary>The PC's world position via the camera-follow behavior's target -- camera-INDEPENDENT (the follow
    /// target doesn't move when the camera orbits) and independent of the vanilla shadow path (unlike Scene+0x58,
    /// which reads null under the vanilla-stencil full kill). Replicates ManageSceneBSP's own per-frame chain: CurrentCamera ->
    /// +0x458 host -> AsBehaviorCameraFollow() (vtable+0x48, pure this-or-null) -> target object (+0x20) -> world
    /// pos (+0xa8). Every deref is guarded exactly as the engine guards it; returns null for a non-follow camera
    /// (free-cam/cutscene) so the caller falls back. Safe after the original ManageSceneBSP ran this frame -- it
    /// walked this same chain, so the pointers and the vcall target are known-valid. Alloc-free.</summary>
    private static Vector3? CameraTargetFocus()
    {
        if (_currentCamera == IntPtr.Zero) return null;
        IntPtr cam = *(IntPtr*)_currentCamera;
        if (cam == IntPtr.Zero) return null;
        IntPtr host = *(IntPtr*)((byte*)cam + CameraBehaviorHost);
        if (host == IntPtr.Zero) return null;
        IntPtr vtbl = *(IntPtr*)host;
        if (vtbl == IntPtr.Zero) return null;
        AlloyProbe.Once(ref _probedBehavior, "CAurBehavior", host, _log);
        var asFollow = (delegate* unmanaged<IntPtr, IntPtr>)(*(IntPtr*)((byte*)vtbl + BehaviorAsFollowVtbl));
        if (asFollow == null) return null;
        IntPtr behavior = asFollow(host);
        if (behavior == IntPtr.Zero) return null;   // non-follow camera -> caller uses sticky/camera-ground
        IntPtr target = *(IntPtr*)((byte*)behavior + BehaviorTargetObj);
        if (target == IntPtr.Zero) return null;
        float* p = (float*)((byte*)target + EngineLayout.GobPosition);
        return new Vector3(p[0], p[1], p[2]);
    }

    /// <summary>Per-presented-frame edge from the UiHost roll-call: re-arm the once-per-frame walk and
    /// re-read the area flag. The light snapshots are NOT reset here -- they restart at the top of the walk
    /// itself, which since #118 is the same slot that consumes them, one call earlier. Resetting at this
    /// edge instead would be a reset nothing needs, in the frame's other half.</summary>
    private static void Draw()
    {
        _haveFocus = false;    // recompute the shared map centre next frame
        _passDoneThisFrame = false; // re-arm the once-per-frame walk (first FrameStart of the next frame)
        _interiorDecided = false;   // re-read the area's interior flag next frame
        LogLocalLights();      // one-shot dump of the recorded point lights
    }

    /// <summary>One-shot dump of the shadow-worthy point lights the walk recorded, so the feed is provable
    /// independently of whether CasterPass got cubes out of it. Cold -- the string build runs once.</summary>
    private static void LogLocalLights()
    {
        if (_localLoggedOnce || FrameInputs.LocalLightCount == 0 || _log == null) return;
        _localLoggedOnce = true;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < FrameInputs.LocalLightCount; i++)
        {
            Vector3 p = FrameInputs.LocalLightPos[i];
            sb.Append($" [{i}]=({p.X:F1},{p.Y:F1},{p.Z:F1}) r{FrameInputs.LocalLightRange[i]:F1}");
        }
        _log.Info($"SceneInputs: {FrameInputs.LocalLightCount} point light(s) recorded (slice 6):{sb}");
    }

}
