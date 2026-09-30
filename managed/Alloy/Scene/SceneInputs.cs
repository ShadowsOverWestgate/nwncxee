using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// What feeds the Alloy shadow pipeline: the light walk and the caster walk. It renders nothing --
/// <see cref="CasterPass"/> is the ONE shadow system (#98 cutover), owning the sun cascades and the
/// local-light cubes in the #80 4.6 context on its own submission path. What lives here is everything
/// those views need to be CORRECT rather than merely drawn, all of it gathered from the engine's own
/// structures:
/// <list type="bullet">
/// <item>ALLOY's light set (CX-108: <see cref="LightRegistry"/>, not the engine's master list), walked whole
/// every frame into <see cref="FrameInputs"/> -- the area-wide, camera-independent snapshot the clustered
/// lighting (#83) and the cubes both read;</item>
/// <item>the shared per-frame shadow focus, published as <c>FrameInputs.Focus</c>;</item>
/// <item>the area's interior flag, which is what tells CasterPass there is no sun to fit.</item>
/// </list>
/// <para>All of it runs at the #132 frame anchor (<c>AlloyRenderer.FrameAnchor</c>, after the engine's
/// <c>EnableLights</c> has advanced the fade weights and before any draw), and none of it touches
/// GL -- it is also where this service drives <see cref="CasterPass.OnPassSlot"/> (#118), so the world casters
/// bake ahead of the colour pass that samples them. The <c>shadow.enabled=0</c> config setting stands the whole thing down, which
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
    private static float _focusDist => Shadows.FocusDist.V;
    private static ExtenderLog? _log;
    /// <summary>#456: the CAurBehavior layout probe's one-shot latch.</summary>
    private static bool _probedBehavior;
    private static bool _enabled;
    private static bool _diag => Shadows.Diag.V;
    /// <summary>#404: how far <see cref="FrameStart"/> runs, for one-restart bisection on radeonsi.
    /// 1 = light walk only, 2 = + focus, 3 = + cube bind, 4 = + caster bake (full). <c>shadow.stage</c>, Boot.</summary>
    private static int _stage = 4;

    /// <summary>The one "we own shadows this frame" predicate: armed, in a module, and the frame latched
    /// owned (#86). No GL term any more -- this service stopped touching GL when the maps moved to
    /// CasterPass, and a dead flag in here would read as a live safety gate that can no longer fire.</summary>
    private static bool Owns => _enabled && GameGate.InModule && AlloyGate.Alloy;


    private static IntPtr _currentCamera;   // &CurrentCamera global; walk it to the camera-follow target (the PC)
    private static bool _localLoggedOnce;   // slice 6: one-shot log of the first frame that records point lights

    // The point-light cubes moved to CasterPass (#95): the Alloy multidraw path renders every local light's six
    // faces into one cube ARRAY, and the receiver samples them as a samplerCubeArrayShadow. What is left here is
    // the light WALK that feeds it -- Alloy's own light set (CX-108), the shared shadow focus and the area's
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
    private static GetModuleFn _getModule;
    private static bool _interiorLogged;              // log interior/exterior transitions
    /// <summary><c>/shadow trace 1</c>: periodic trace of the light walk -- allocates log strings; diagnosis only.</summary>
    private static bool _trace => Shadows.Trace.V;

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
        // Restart knobs (shadow.enabled, shadow.stage): this arms the feed's hooks, so it is a boot decision,
        // read through Boot. Everything the walk merely LOOKS at -- radii, cull mode, the diagnostics -- is
        // a Live knob of the same group, read per frame.
        _enabled = Shadows.Enabled.Boot;
        _stage = Shadows.Stage.Boot;
        if (_stage < 4) log.Info($"SceneInputs: shadow.stage={_stage} -- the shadows feed stops after {(_stage == 1 ? "the light walk" : _stage == 2 ? "the focus" : "the cube bind (no bake)")}");

        ShadowMath.SelfCheck(log);   // pure math, runs headless
        FadeSelfCheck(log);          // ...and so does the fade-out bookkeeping
        FlickerSelfCheck(log);       // ...and the animated-light latch
        // #257: the frame constants are read, not captured -- and ahead of the stand-down, because the
        // camera, the area terms and the fog are inputs to EVERY Alloy pass, shadows or no shadows.
        FrameRead.Resolve(log);

        if (!_enabled) return;

        _appMgr = Native.Sym(SymbolMap.AppManager);
        _getModule = EngineFn.GetModule;
        _getGspF = EngineFn.RiGetGlobalShaderParamf;
        // Resolved once into a static: asked per area light per frame.
        _getAdjRadius = EngineFn.GetLightAdjustedRadius;

        // Camera-follow anchor: the real PC position, camera- and shadow-path-independent (Scene+0x58 reads null
        // under the vanilla-stencil full kill). CurrentCamera is a global pointer slot -- store its address, deref per frame.
        _currentCamera = Native.Sym(SymbolMap.CurrentCamera);

        // The per-presented-frame edge. Was a zero-size UiHost Window abused as a draw slot (#169); #170 gives
        // it the real thing. Priority 0: it ran inside the window roll-call, i.e. before the #80 context bracket.
        FrameRegistry.Global.Register(FramePhase.PrePresent, 0, Draw, "SceneInputs.frameEdge", log);
        log.Info("SceneInputs: light walk armed, feeding CasterPass (shadow.enabled=0 disables)");
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
        WalkLights(scene);
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
            CNWCArea a = CurrentArea();
            _interior = !a.IsNull && (a.Flags & 1) != 0;
            FrameInputs.Interior = _interior;
            FrameInputs.AreaPtr = a.Ptr;
            // R2: the area's world XY extent, for the wetness map's fit. Tile (0,0) spans [0,10).
            FrameInputs.AreaExtent = a.IsNull ? default
                : new Vector2(a.TileWidth * AreaBorder.TileSize, a.TileHeight * AreaBorder.TileSize);
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
    internal static int FadeFrames => Lighting.Fadeout.V;
    internal const int FadeFramesBoot = 20;
    /// <summary>Lights allowed to fade at once. More than this vanishing in ONE frame is a load edge -- an
    /// area transition unlinks the whole list -- and the ghosts are dropped rather than played out.</summary>
    private const int MaxGhosts = 16;

    private static IntPtr[] _prevId = new IntPtr[64];
    private static Vector4[] _prevPosW = new Vector4[64];
    private static Vector3[] _prevCol = new Vector3[64];
    private static bool[] _prevAmb = new bool[64];
    private static int _prevN;

    private static readonly IntPtr[] _ghostId = new IntPtr[MaxGhosts];
    private static readonly Vector4[] _ghostPosW = new Vector4[MaxGhosts];
    private static readonly Vector3[] _ghostCol = new Vector3[MaxGhosts];
    private static readonly bool[] _ghostAmb = new bool[MaxGhosts];
    private static readonly int[] _ghostLeft = new int[MaxGhosts];
    private static int _ghostN;

    /// <summary>#336: the fade bookkeeping, headless. It is a set difference over two flat arrays with a
    /// swap-remove in the middle, and every way it can go wrong is silent -- a ghost that never retires is a
    /// light that never goes out, a leaver that is missed is the pop this exists to remove.</summary>
    internal static void FadeSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "SceneInputs.FadeSelfCheck");

        // Plain field writes on the knob, not Sets: nothing runs and nothing is written to the file.
        int fadeWas = FadeFrames, prevWas = _prevN, ghostWas = _ghostN;
        Lighting.Fadeout.V = 3; _prevN = 0; _ghostN = 0;

        void Lit(params int[] ids)
        {
            FrameInputs.BeginAreaLights(ids.Length);
            foreach (int id in ids)
                FrameInputs.RecordAreaLight(id, new Vector3(id, 0f, 0f), 10f, Vector3.One);
            FadeDepartedLights();
        }

        Lit(1, 2);
        check.Expect(FrameInputs.LightCount == 2 && _ghostN == 0, "two lit lights, nothing fading");
        Lit(1);
        check.Expect(_ghostN == 1 && FrameInputs.LightCount == 2, "a departed light is still emitted, once");
        check.Expect(FrameInputs.AreaLightPosRange[1].W < 10f, "...at a shrunken radius");
        check.Expect(FrameInputs.AreaLightPosRange[1].W > 0f, "...and not to nothing on its first frame");
        Lit(1); Lit(1); Lit(1);
        check.Expect(_ghostN == 0 && FrameInputs.LightCount == 1, "and retires after FadeFrames frames");
        Lit(1, 2); Lit(1);
        check.Expect(_ghostN == 1, "light 2 fades again");
        Lit(1, 2);
        check.Expect(_ghostN == 0 && FrameInputs.LightCount == 2, "a light that comes back drops its ghost");
        // The load edge: more leavers than the cap in one frame is an area transition, not a torch.
        Lit(1, 2, 3, 4); Lighting.Fadeout.V = 3; _ghostN = 0;
        var many = new int[MaxGhosts + 4];
        for (int i = 0; i < many.Length; i++) many[i] = 100 + i;
        Lit(many); Lit(1);
        check.Expect(_ghostN == 0, "a whole list vanishing at once fades nothing");
        Lighting.Fadeout.V = 0; Lit(1, 2); Lit(1);
        check.Expect(_ghostN == 0 && FrameInputs.LightCount == 1, "fadeout 0 pops, as before");

        Lighting.Fadeout.V = fadeWas; _prevN = prevWas; _ghostN = ghostWas;
        FrameInputs.BeginAreaLights(0);
        log.Info(check.Ok ? "SceneInputs.FadeSelfCheck: passed" : "SceneInputs.FadeSelfCheck: FAILED -- see above");
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
            _ghostCol[i] = _ghostCol[_ghostN]; _ghostAmb[i] = _ghostAmb[_ghostN]; _ghostLeft[i] = _ghostLeft[_ghostN];
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
                _ghostCol[_ghostN] = _prevCol[i]; _ghostAmb[_ghostN] = _prevAmb[i]; _ghostLeft[_ghostN++] = FadeFrames;
            }
        }

        if (cur > _prevId.Length)
        {
            _prevId = new IntPtr[cur * 2];
            _prevPosW = new Vector4[cur * 2];
            _prevCol = new Vector3[cur * 2];
            _prevAmb = new bool[cur * 2];
        }
        _prevN = cur;
        Array.Copy(FrameInputs.AreaLightId, _prevId, cur);
        Array.Copy(FrameInputs.AreaLightPosRange, _prevPosW, cur);
        Array.Copy(FrameInputs.AreaLightColor, _prevCol, cur);
        Array.Copy(FrameInputs.AreaLightAmbient, _prevAmb, cur);

        for (int i = 0; i < _ghostN; i++)
        {
            // Intensity is the engine's own ramp on the way in, and the radius rides it -- so scaling both
            // by the same k is what makes the pool shrink INTO the source instead of dimming in place.
            // +1 so the first ghost frame is already below full: the light was at full strength the frame
            // before, and re-emitting it unchanged would spend one frame of the fade standing still.
            float k = (float)_ghostLeft[i] / (FadeFrames + 1);
            var pw = _ghostPosW[i];
            FrameInputs.RecordAreaLight(_ghostId[i], new Vector3(pw.X, pw.Y, pw.Z), pw.W * k, _ghostCol[i] * k, _ghostAmb[i]);
        }
    }
    // ---- animated lights: steady reach, flickering colour ----
    // A torch's pulse is its light node's `radius` key track (Ghidra NewController::Control @0x598f20 writes
    // Part+offset straight from GetFloatValue's key+alpha, so it is piecewise-linear between the MDL keys).
    // The engine keeps no un-animated radius anywhere -- PartLight's ctor zeroes +0x70 and the controller
    // overwrites it -- so an animated light is told at runtime: the same PartLight* reporting a SMALLER
    // range*intensity than its running peak. Only a pulse ever goes down; a light that appears ramps UP.

    /// <summary>Pointer-keyed table of every light's running peak. Open-addressed, fixed, alloc-free; an entry
    /// the walk did not refresh last frame is dead (the engine frees PartLight* at area edges and malloc hands
    /// the address back), so a lookup that finds a stale entry starts over.</summary>
    private const int FlkSlots = 512;   // ponytail: master lists run to a few hundred; past this the tail keeps the stock pulse
    private const int FlkProbe = 8;
    /// <summary>Frames the latch outlives the last decrease, so a key plateau does not flip a torch back to raw.</summary>
    private const int FlkHold = 180;
    /// <summary>Hysteresis on the peak, so the sampled triangle wave's creep does not re-key the cube every cycle.</summary>
    private const float FlkEps = 0.002f;
    private static readonly IntPtr[] _flkId = new IntPtr[FlkSlots];
    private static readonly float[] _flkPeak = new float[FlkSlots], _flkPeakAdj = new float[FlkSlots], _flkMin = new float[FlkSlots];
    private static readonly int[] _flkSeen = new int[FlkSlots], _flkUntil = new int[FlkSlots];
    private static int _flkSeq;
    private static IntPtr _flkArea;
    private static readonly long _flkT0 = Stopwatch.GetTimestamp();

    /// <summary>Replace an animated light's pulse: <paramref name="adjR"/> becomes the engine's own value at the
    /// running peak (frozen, so the cube it keys stays baked), and the returned colour multiplier flickers as
    /// deep as the pulse was authored. Returns exactly 1 for a light that is not animated or when the knob is 0.</summary>
    internal static float Stabilise(IntPtr id, float cur, ref float adjR, float t)
    {
        float amp = Lighting.Flicker.V;
        if (amp <= 0f || cur <= 0f) return 1f;
        int h = (int)((ulong)id >> 4) * -1640531527 & (FlkSlots - 1);
        int slot = -1, free = -1;
        for (int k = 0; k < FlkProbe; k++)
        {
            int j = (h + k) & (FlkSlots - 1);
            if (_flkId[j] == id && _flkSeq - _flkSeen[j] <= 2) { slot = j; break; }
            if (free < 0 && (_flkId[j] == IntPtr.Zero || _flkSeq - _flkSeen[j] > 2)) free = j;
        }
        if (slot < 0)
        {
            if (free < 0) return 1f;   // every probe live: this light keeps the stock pulse
            slot = free;
            _flkId[slot] = id; _flkPeak[slot] = cur; _flkPeakAdj[slot] = adjR; _flkUntil[slot] = 0;
        }
        _flkSeen[slot] = _flkSeq;
        if (cur > _flkPeak[slot] * (1f + FlkEps)) { _flkPeak[slot] = cur; _flkPeakAdj[slot] = adjR; }
        else if (cur < _flkPeak[slot] * (1f - FlkEps))
        {
            // The trough is measured within one latched run only: a carried torch ramps in from nothing
            // before it ever pulses, and that ramp is not the pulse's depth.
            if (_flkSeq >= _flkUntil[slot] || cur < _flkMin[slot]) _flkMin[slot] = cur;
            _flkUntil[slot] = _flkSeq + FlkHold;
        }
        if (_flkSeq >= _flkUntil[slot]) return 1f;
        adjR = _flkPeakAdj[slot];
        float depth = MathF.Min(1f, (1f - _flkMin[slot] / _flkPeak[slot]) * amp);
        float phase = (uint)((ulong)id >> 4) * 2654435761u * (MathF.Tau / 4294967296f);
        return Flicker(t * Lighting.FlickerSpeed.V, phase, depth);
    }

    /// <summary>A fire's unevenness: slow breathing under faster crackle, three incommensurate sines whose sum
    /// stays in [-1,1], mapped to a multiplier in [1-depth, 1]. The phase desyncs neighbouring torches.</summary>
    private static float Flicker(float t, float phase, float depth)
    {
        float n = 0.5f * MathF.Sin(t * 7.3f + phase) + 0.3f * MathF.Sin(t * 13.7f + phase * 2.1f) + 0.2f * MathF.Sin(t * 23.1f + phase * 3.3f);
        return 1f - depth * 0.5f * (1f - n);
    }

    /// <summary>The latch and the peak, headless: a pulse settles at its peak and stays there, a ramp does not latch.</summary>
    internal static void FlickerSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "SceneInputs.FlickerSelfCheck");
        float ampWas = Lighting.Flicker.V; Lighting.Flicker.V = 1f;
        var torch = (IntPtr)0x1000; var lamp = (IntPtr)0x2000;
        var zero = (IntPtr)((long)FlkSlots << 4);   // hashes to slot 0: the slot whose index is falsy
        float Step(IntPtr id, float cur, out float m) { float r = cur * 2f; _flkSeq++; m = Stabilise(id, cur, ref r, _flkSeq * 0.016f); return r; }
        float r0 = Step(torch, 13f, out float m0); float r1 = Step(torch, 15f, out float m1);
        check.Expect(r0 == 26f && m0 == 1f && r1 == 30f && m1 == 1f, "no pulse seen yet: raw passes through");
        float r2 = Step(torch, 13f, out float m2);
        check.Expect(r2 == 30f && m2 < 1f && m2 >= 1f - (2f / 15f) - 1e-4f, "the first decrease latches: peak reach, dimmed colour");
        float r3 = Step(torch, 15f, out float m3);
        check.Expect(r3 == 30f && m3 <= 1f && m3 != m2, "back at the peak: reach unchanged, colour still flickering");
        check.Expect(Step(torch, 15.01f, out _) == 30f, "a hair over the peak is hysteresis, not a new peak");
        check.Expect(Step(torch, 16f, out _) == 32f, "a real new peak re-keys the reach");
        check.Expect(Step(lamp, 10f, out _) == 20f && Step(lamp, 12f, out _) == 24f && Step(lamp, 14f, out float ml) == 28f && ml == 1f,
          "a light ramping up never latches");
        for (int i = 0; i < FlkHold + 1; i++) Step(torch, 16f, out _);
        check.Expect(Step(torch, 16f, out float mh) == 32f && mh == 1f, "the latch expires after FlkHold frames at the peak");
        Step(zero, 15f, out _);
        check.Expect(Step(zero, 13f, out float m0s) == 30f && m0s < 1f, "a light in table slot 0 latches too");
        Lighting.Flicker.V = 0f;
        check.Expect(Step(torch, 13f, out float mz) == 26f && mz == 1f, "knob 0 is the stock pulse");
        Lighting.Flicker.V = ampWas; Array.Clear(_flkId);
        log.Info(check.Ok ? "SceneInputs.FlickerSelfCheck: passed" : "SceneInputs.FlickerSelfCheck: FAILED -- see above");
    }

    /// <summary>RenderInterface::GetGlobalShaderParamf -- param 0x12 = the engine's light-radius multiplier.</summary>
    private static RiGetGlobalShaderParamfFn _getGspF;

    /// <summary>#140: latch the two constants the engine's point-light attenuation is built from, out of the
    /// global-param array the radius multiplier comes from (<c>GLRender::SetUniforms</c> @0x4cd341 reads
    /// 0x12/0x13/0x14 and derives lightFalloffFactor and lightMaxIntensityInv from exactly these). Read on
    /// every walk -- they are graphics settings the player can change -- and by the area key at
    /// <c>LoadArea</c>'s return, which on the login load runs before any walk. Returns the radius multiplier.</summary>
    internal static float LatchAtten()
    {
        float radiusMult = _getGspF != null ? _getGspF(0x12) : 1f;
        if (!(radiusMult > 0f)) radiusMult = 1f;
        if (_getGspF != null)
            CX.Alloy.AlloyLights.LatchAtten(_getGspF(0x13), _getGspF(0x14), radiusMult);
        return radiusMult;
    }
    /// <summary>GetLightAdjustedRadius -- the engine's own lit-radius calculation, called rather than
    /// re-derived so the Alloy pass cannot drift from it.</summary>
    private static GetLightAdjustedRadiusFn _getAdjRadius;
    private static int _walkDiagTick;

    /// <summary>Light-cull mode (<c>/shadow lightcull</c>): 0 = keep every area light, 1 = camera-distance
    /// only (default), 2 = camera-frustum sphere test (opt-in until live-proven -- the first frustum cut
    /// used the unverified view quaternion and blacked out every light off the torch). Shrinks the
    /// per-fragment Alloy loop light set.</summary>
    private static int _lightCullMode => Shadows.LightCull.V;

    /// <summary>See <see cref="ShadowMath.FrustumCullSlack"/> -- shared with the cube-bake cull (#322).</summary>
    private const float FrustumCullSlack = ShadowMath.FrustumCullSlack;
    private static int _lightsCulled;
    private static bool _lightDiag => Shadows.LightDiag.V;


    /// <summary>Record EVERY point light of the area into <see cref="FrameInputs"/> -- the area-wide,
    /// camera-independent snapshot feeding the Alloy pass's light SSBO and the cube set. Runs ONCE per frame
    /// from the deterministic pass slot (ADR 0007 phase 2). Alloc-free; pointers are only read inside this
    /// call, never retained.
    /// <para>CX-108: the ENUMERATION is <see cref="LightRegistry"/>'s -- Alloy's own set, built on the scene
    /// walk's edges and a BSP light pass. <c>light.registry 0</c> reads the engine's <c>LightManager</c> master
    /// list instead, which is where this walk read it from #257 to CX-108; everything below the enumeration is
    /// shared between the two, so the A/B compares the source and nothing else.</para></summary>
    private static void WalkLights(IntPtr scene)
    {
        if (scene == IntPtr.Zero || !GameGate.InModule) return;
        bool owned = LightRegistry.Live;
        LightManager lm = owned ? default : new Scene(scene).LightManager;
        if (!owned && lm.IsNull) return;
        // The area edge frees every PartLight* and malloc hands the addresses back: a peak table keyed on
        // them must not outlive the area, or a new light inherits a stranger's reach for FlkHold frames.
        CNWCArea area = CurrentArea();
        if (area.Ptr != _flkArea) { _flkArea = area.Ptr; Array.Clear(_flkId); }
        int count;
        if (owned) count = LightRegistry.Count;
        else
        {
            count = lm.MasterCount;
            if (lm.MasterData == IntPtr.Zero) count = 0;   // an empty list still walks: the lightmap set closes on "no light"
        }
        float radiusMult = LatchAtten();
        // BOTH snapshots restart here, not at the frame edge. CasterPass consumes the local-light set later
        // in this same pass slot, and the roll-call this service's frame edge rides runs at the present --
        // resetting there handed the cube pass an empty set every frame. The walk is once-per-frame and
        // idempotent, so restarting it at its own top is the same reset with no ordering to get wrong.
        FrameInputs.BeginAreaLights(count);
        FrameInputs.ResetLocalLights(count);
        LightmapBake.BeginWalk(area);   // #674: the static snapshot restarts with the walk
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
        float flickerT = (float)((Stopwatch.GetTimestamp() - _flkT0) / (double)Stopwatch.Frequency);
        _flkSeq++;
        for (int i = 0; i < count; i++)
        {
            PartLight light = owned ? LightRegistry.Light(i) : lm.Master(i);
            if (light.IsNull) continue;
            if (walkDiag && FrameInputs.HasCamera)
            {
                Vector3 wp = light.Pos;
                float wd = (wp - FrameInputs.CameraPos).Length();
                if (wd < 40f)
                    _log?.Info($"SceneInputs: masterlight d={wd:F1} pos=({wp.X:F0},{wp.Y:F0},{wp.Z:F1}) type={light.Kind} cast={light.CastShadows} flags={light.FlagA}/{light.FlagB} range={light.Range:F1} alt={light.RangeAlt:F1} inten={light.Intensity:F2} fade={light.Fade:F2} rgb=({light.Colour.X:F2},{light.Colour.Y:F2},{light.Colour.Z:F2}) adjR={(_getAdjRadius != null ? _getAdjRadius(light.Ptr) : -1f):F2}");
            }
            if (_lightDiag && AlloyRenderer.FrameSeq % 300 == 0 && FrameInputs.HasCamera) LightProbe(light, area);
            if (light.FlagA == 0 && light.FlagB == 0) { LightmapBake.CensusSkipped++; continue; }
            float range = light.Range;
            if (range >= EngineLayout.DirectionalRange) continue;
            float inten = light.Intensity;
            Vector3 rgb = light.Colour;
            float r = rgb.X, g = rgb.Y, b = rgb.Z;
            if (range <= 0f || inten <= 0f || (r == 0f && g == 0f && b == 0f)) { LightmapBake.CensusSkipped++; continue; }
            // Ghidra SetLightGL: type@0x80 != 0 uploads -adjR^2 and inc_lighting.shd adds c*att to AmbientLight
            bool ambient = light.Kind != 0;
            float alt = light.RangeAlt;
            Vector3 pos = light.Pos;
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
            float adjR = _getAdjRadius != null ? _getAdjRadius(light.Ptr) : range * inten * radiusMult;
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
            // #674: the bake's snapshot takes the light HERE -- before the engine's nearest-N fade (a light
            // faded out by distance is still in the area) and before the flicker: the fingerprint is over the
            // uncut, steady list, or walking the camera re-keys every bake (#654).
            bool isStatic = LightmapBake.Record(light, area, pos, adjR, new Vector3(r, g, b), ambient, out int setIndex);
            // fade@0x90 (ControlLightsFade) scales the uploaded colour only, never the radius (SetLightGL)
            float lfade = light.Fade;
            if (lfade <= 0f) continue;
            if (lfade < 1f) { r *= lfade; g *= lfade; b *= lfade; }
            var color = new Vector3(r, g, b);
            // An animated light (a torch's `radius` key track) gets a steady reach and a flickering colour
            // instead of the engine's linear pulse -- and a steady reach is also what keeps its cube baked.
            float flk = Stabilise(light.Ptr, range * inten, ref adjR, flickerT);
            if (flk != 1f) color *= flk;
            upW = adjR;
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
                FrameInputs.RecordAreaLight(light.Ptr, pos, upW, color, ambient, isStatic, setIndex);
                if (FrameInputs.LightCount == snapIdx) snapIdx = -1;   // snapshot full: the light was dropped
            }
            if (_torchDiag) TrackNearest(light, pos, range, alt, inten, color, adjR, upW, flk, culled, snapIdx);
            if (ambient || light.CastShadows == 0) continue;
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
            FrameInputs.RecordLocalLight(light.Ptr, pos, adjR, color);
        }
        FadeDepartedLights();
        LightmapBake.EndWalk();
        _walkDoneCount = FrameInputs.LightCount;
    }

    /// <summary>#659 probe (<c>/shadow lightdiag</c>, every 300 frames, lights within 40 m): the static-light rule's
    /// three inputs read off a running client -- the light's gob and its tile data, the MdlNode name, the
    /// placeable-light registry hit, and every controller id with its key count. Cold diag path: allocates.</summary>
    private static void LightProbe(PartLight light, CNWCArea area)
    {
        Vector3 pos = light.Pos;
        float d = (pos - FrameInputs.CameraPos).Length();
        if (d >= 40f) return;
        Gob gob = new Part(light.Ptr).Gob;
        bool tile = !gob.IsNull && !gob.TileData.IsNull;
        MdlNode node = new Part(light.Ptr).Node;
        string name = node.IsNull ? "?" : node.NameText ?? "?";
        uint regId = 0; bool reg = false;
        int regCount = area.IsNull ? 0 : area.PlaceableLightCount;
        if (!gob.IsNull && !area.IsNull && area.PlaceableLights != IntPtr.Zero && regCount > 0 && regCount < 65536)
            for (int i = 0; i < regCount && !reg; i++)
            {
                CNWCPlaceableObjectLight rec = area.PlaceableLight(i);
                if (!rec.IsNull && rec.Gob.Ptr == gob.Ptr) { reg = true; regId = rec.Id; }
            }
        var ctl = new System.Text.StringBuilder();
        int cnt = node.IsNull ? -1 : node.ControllerCount;
        NewController recs = node.IsNull ? default : node.Controller;
        bool animated = false;
        if (!recs.IsNull && cnt > 0 && cnt < 256)
            for (int i = 0; i < cnt; i++)
            {
                NewController c = recs[i];
                int id = c.Id;
                short keys = c.KeyCount;
                if ((id == EngineLayout.LightRange || id == EngineLayout.LightColour) && keys > 1) animated = true;
                ctl.Append(ctl.Length == 0 ? "" : ",").Append(id.ToString("X")).Append('x').Append(keys);
            }
        string cls = gob.IsNull ? "TILE(gob=0)" : tile ? "TILE(tileData)" : reg ? "PLACEABLE-LIGHT" : "OTHER";
        var on = AlloyRenderer.SlotsOnGob(gob.Ptr);
        _log?.Info($"SceneInputs: [lightdiag] #659 light={light.Ptr:X} d={d:F1} pos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) node={name} gob={gob.Ptr:X} tile={tile} reg={reg} regId={regId:X} regCount={regCount} slotsOnGob={on.slots}({on.tile} tile) ctl[{cnt}]={ctl} animated={animated} => {cls}{(animated ? "+ANIM" : "")} flags={light.FlagA}/{light.FlagB} range={light.Range:F2} rgb=({light.Colour.X:F2},{light.Colour.Y:F2},{light.Colour.Z:F2})");
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
    private static bool _torchDiag => Shadows.TorchDiag.V;
    private static int _torchTick;
    /// <summary>Nearest-so-far distance^2 this walk. MUST start at +inf, not the default 0, or the first
    /// walk's comparison rejects every light.</summary>
    private static float _tBest = float.MaxValue;
    private static IntPtr _tPtr;
    private static Vector3 _tPos, _tColor;
    private static float _tRange, _tAlt, _tInten, _tAdjR, _tUpW, _tFlk;
    private static bool _tCulled;
    private static int _tSnapIdx;

    /// <summary>Keep the walk's nearest point light to the player anchor, with the verdict it just got.</summary>
    private static void TrackNearest(PartLight light, Vector3 pos, float range, float alt, float inten,
                                     Vector3 color, float adjR, float upW, float flk, bool culled, int snapIdx)
    {
        Vector3 anchor = FrameInputs.HasFocus ? FrameInputs.Focus
                       : FrameInputs.HasCamera ? FrameInputs.CameraPos : default;
        float d2 = (pos - anchor).LengthSquared();
        if (d2 >= _tBest) return;
        _tBest = d2; _tPtr = light.Ptr; _tPos = pos; _tColor = color;
        _tRange = range; _tAlt = alt; _tInten = inten; _tAdjR = adjR; _tUpW = upW; _tFlk = flk;
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
                   $"range={_tRange:F1} alt={_tAlt:F1} inten={_tInten:F2} -> adjR={_tAdjR:F1} upW={_tUpW:F1} flicker={(_tFlk == 1f ? "off" : _tFlk.ToString("F2"))} | " +
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

    /// <summary>The current CNWCArea, or the null view while the chain is unavailable. Walks the engine's own
    /// chain (**g_pAppManager -&gt; CClientExoApp::GetModule -&gt; module+0xf8; the global needs TWO derefs --
    /// CAppManager first, its first field is the CClientExoApp*), every deref guarded. Compared, never
    /// retained. Alloc-free, once per frame.</summary>
    private static CNWCArea CurrentArea()
    {
        if (_appMgr == IntPtr.Zero || _getModule == null) return default;
        IntPtr mgr = *(IntPtr*)_appMgr;          // CAppManager*
        if (mgr == IntPtr.Zero) return default;
        IntPtr app = *(IntPtr*)mgr;              // CClientExoApp* (mgr's first field -- the UiHostService gate chain)
        if (app == IntPtr.Zero) return default;
        var module = new CNWCModule(_getModule(app));
        return module.IsNull ? default : module.CurrentArea;
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
        var cam = new Camera(AlloyCamera.World);
        if (cam.IsNull) return null;
        CAurBehavior hv = cam.BehaviorHost;
        if (hv.IsNull) return null;
        if (*(IntPtr*)hv.Ptr == IntPtr.Zero) return null;   // no vtable yet: AsBehaviorCameraFollow is a vcall
        AlloyProbe.Once(ref _probedBehavior, "CAurBehavior", hv.Ptr, _log);
        if (hv.AsBehaviorCameraFollowFn == IntPtr.Zero) return null;
        CAurBehaviorCameraFollow behavior = hv.AsBehaviorCameraFollow();
        if (behavior.IsNull) return null;   // non-follow camera -> caller uses sticky/camera-ground
        IntPtr target = behavior.Target;
        if (target == IntPtr.Zero) return null;
        return new Gob(target).Position;
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
