using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using static Sowx.Alloy.SceneLayout;

namespace Sowx.Alloy;

/// <summary>
/// What feeds the Alloy shadow pipeline: the light walk and the caster walk. It renders nothing --
/// <see cref="CasterPass"/> is the ONE shadow system (#98 cutover), owning the sun cascades and the
/// local-light cubes in the #80 4.6 context on its own submission path. What lives here is everything
/// those views need to be CORRECT rather than merely drawn, all of it gathered from the engine's own
/// structures:
/// <list type="bullet">
/// <item>the LightManager MASTER light list, walked whole every frame into <see cref="FrameInputs"/> --
/// the area-wide, camera-independent snapshot the clustered lighting (#83) and the cubes both read;</item>
/// <item>a BSP sphere re-walk around the shadow focus that pushes off-screen tile parts and gob casters
/// into the engine's draw buckets, so a wall behind the camera still lands in a caster set the camera
/// itself culled;</item>
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
    private static bool _enabled, _diag;

    /// <summary>The one "we own shadows this frame" predicate: armed, in a module, and the frame latched
    /// owned (#86). No GL term any more -- this service stopped touching GL when the maps moved to
    /// CasterPass, and a dead flag in here would read as a live safety gate that can no longer fire.</summary>
    private static bool Owns => _enabled && GameGate.InModule && AlloyGate.Alloy;

    /// <summary>Radius of the off-screen GOB append: dynamic casters this far from the focus are pushed into
    /// the draw buckets even when the camera culled them, so they can still cast. Kept tight -- a creature
    /// far enough out to need the outer cascades casts a shadow nobody looks at. <c>/shadow nearradius</c>.</summary>
    private static float _gobAppendRadius = NearRadiusBoot;

    // Caster completeness (slice 3c): re-walk the scene BSP in a sphere around the player and re-submit the
    // casters the camera frustum culled, so the depth map is camera-independent. ManageSceneBSP hook runs the
    // re-walk (pre-transfer); the engine's own AddPartsToDrawBuckets then moves them into the draw buckets.
    private static delegate* unmanaged<IntPtr, void> _origManageSceneBSP;
    private static delegate* unmanaged<IntPtr, void> _addPartToMeshBuckets;
    private static delegate* unmanaged<IntPtr, Vector3*, float, IntPtr, void> _bspTraverseSphere;
    private static delegate* unmanaged<IntPtr, byte> _partGetIsOpaque;
    private static int _rewalkParts;    // caster parts submitted this frame (diagnostic)
    private static bool _rewalkLogged;
    private static bool _rewalkOn = true;   // config shadow_rewalk=0 disables (falls back to camera-culled set)

    // Dynamic gob casters (slice 3d): the engine's ManageSceneBSP adds ONLY camera-visible gobs (gob+0x1b) to
    // the global gobbucket. We append off-screen in-sphere gobs there so movable objects (placeables, creatures)
    // cast even off-camera; the same Scene::AddPartsToDrawBuckets sorts + routes them into the opaque buckets.
    private static IntPtr _gobBucket;   // the CExoArrayList<Gob*> base (symbol address == list `this`)
    private static delegate* unmanaged<IntPtr, IntPtr, void> _gobBucketAdd;
    private static IntPtr _currentCamera;   // &CurrentCamera global; walk it to the camera-follow target (the PC)
    private static int _rewalkGobs;     // off-screen gobs appended this frame (diagnostic)
    private static bool _gobsLogged;
    private static bool _localLoggedOnce;   // slice 6: one-shot log of the first frame that records point lights

    // The point-light cubes moved to CasterPass (#95): the Alloy multidraw path renders every local light's six
    // faces into one cube ARRAY, and the receiver samples them as a samplerCubeArrayShadow. What is left here is
    // the light WALK that feeds it -- the LightManager master list, the BSP re-walk that carries off-screen
    // casters into the draw buckets, the shared shadow focus and the area's interior flag.
    private static bool _gobsOn = true;   // config shadow_gobs=0 disables (tile casters only)


    // ---- the BSP sphere re-walk's struct layout (#65 slice 3c), all Ghidra-verified against ManageSceneBSP ----
    /// <summary>BSPNode -&gt; nodedata* (the node contents / tile model). Null on empty internal nodes.</summary>
    private const int BspNodeData = 0x70;
    /// <summary>nodedata -&gt; PartTriMesh** tile-part array; count at <see cref="NodeDataPartCount"/>.</summary>
    private const int NodeDataParts = 0x20;
    /// <summary>nodedata -&gt; int tile-part count (pairs with <see cref="NodeDataParts"/>).</summary>
    private const int NodeDataPartCount = 0x28;
    /// <summary>nodedata -&gt; PartTriMesh* single "primary" mesh (fallback when the parts array is empty --
    /// the engine's <c>currentscene+0x1e0 != 0</c> fast branch uses only this).</summary>
    private const int NodeDataPrimary = 0x90;
    /// <summary>PartTriMesh -&gt; mesh data (MdlNode) pointer. The renderable-this-frame flag hangs off it.</summary>
    private const int PartMeshData = 0x8;
    /// <summary>meshdata -&gt; int "renderable/submitted this frame" flag. <c>PartTriMesh::IsPartRenderable</c>
    /// (which <c>Scene::AddPartsToDrawBuckets</c> gates every transfer on) returns false while this is 0, so a
    /// re-walked off-screen caster is dropped unless we set it -- exactly as the engine's own
    /// <c>FUN_00581000</c> does before <c>AddPartToMeshBuckets</c>.</summary>
    private const int MeshRenderableFlag = 0x118;

    // ---- the off-screen gob append (#65 slice 3d) ----
    /// <summary>Scene -&gt; Gob** scene-gob array; count at <see cref="SceneGobCount"/>. The camera-cull loop in
    /// ManageSceneBSP walks exactly this list.</summary>
    private const int SceneGobList = 0x130;
    /// <summary>Scene -&gt; int scene-gob count (pairs with <see cref="SceneGobList"/>).</summary>
    private const int SceneGobCount = 0x138;
    /// <summary>Gob -&gt; cached "in view volume" byte (<c>Gob::IsInViewVolume</c> returns exactly this). Fresh
    /// after orig ManageSceneBSP ran; we append only <c>== 0</c> gobs (the engine already added the visible ones,
    /// so this avoids a double-add). Read raw -- do NOT hook the devirt-guarded IsInViewVolume.</summary>
    private const int GobInView = 0x1b;
    /// <summary>Gob -&gt; "geometry dirty" byte. <c>ProcessGob</c> recomputes the has-geometry flag
    /// (<c>gob+0x1a4</c>, which AddPartsToDrawBuckets gates the gob draw on) only when this is non-zero, then
    /// clears it. Set it on an appended gob so its geometry flag is (re)computed this frame.</summary>
    private const int GobGeometryDirty = 0x1a5;

    // ---- the camera-follow anchor chain (#65) ----
    /// <summary>CAurCamera -&gt; current behavior host (<c>CurrentCamera[0x8b]</c> == +0x458), a <c>CAurBehavior*</c>;
    /// its vtable+<see cref="BehaviorAsFollowVtbl"/> is <c>AsBehaviorCameraFollow()</c>.</summary>
    private const int CameraBehaviorHost = 0x458;
    /// <summary>Behavior-host vtable slot for <c>AsBehaviorCameraFollow()</c> -- pure <c>return this</c> for a follow
    /// cam, null otherwise (a base-class type filter). Replicated exactly from ManageSceneBSP's own devirt call, so
    /// non-follow cameras (free-cam/cutscene) safely yield null.</summary>
    private const int BehaviorAsFollowVtbl = 0x48;
    /// <summary>CAurBehaviorCameraFollow -&gt; <c>SafePointer&lt;CAurObject&gt;</c> to the followed object (the PC);
    /// raw pointer at +0. Its <see cref="SceneLayout.GobPosition"/> (+0xa8) is exactly what
    /// GetTargetWorldPosition returns.</summary>
    private const int BehaviorTargetObj = 0x20;

    // One map centre per presented frame, shared by the re-walk and published as FrameInputs.Focus for
    // CasterPass's cascade fit. Player-anchored when the controlled creature is reachable, else the
    // camera-ground focus -- but ONE value per frame either way.
    /// <summary>BSP re-walk radius on ordinary frames (<c>/shadow cubewalk</c>): enough for the local cubes'
    /// casters; the wide walk that fills the sun cascades runs on enrollment frames only.</summary>
    private static float _cubeWalkRadius = CubeWalkBoot;
    // Interior areas have no sun, so nothing directional is worth rendering there -- the flag is published for
    // CasterPass, which skips its cascades on it. The point-light cubes are independent and keep running.
    // Verified via Ghidra: current area = *(CClientExoApp::GetModule(*g_pAppManager) + 0xf8) (the engine's own
    // HandleServerToPlayerArea chain); CNWCArea+8 = the ARE Flags DWORD (CNWSArea::PackAreaIntoMessage writes
    // m_nFlags@+8 -> CNWCArea::LoadArea reads it into +8), bit 0 = interior.
    private static bool _interior;                    // current area is interior (recomputed once per frame)
    private const int ModuleCurrentArea = 0xf8;       // CNWCModule: current CNWCArea*
    private const int AreaFlags = 0x8;                // CNWCArea: ARE Flags DWORD; bit 0 = interior
    private static IntPtr _appMgr;                    // &g_pAppManager (deref per use)
    private static delegate* unmanaged<IntPtr, IntPtr> _getModule;
    private static bool _interiorLogged;              // log interior/exterior transitions
    /// <summary><c>/shadow trace 1</c>: periodic trace of the light walk -- allocates log strings; diagnosis only.</summary>
    private static bool _trace;
    /// <summary>Boot values of the <c>/shadow</c> dials -- the code defaults now that the env overrides are
    /// gone (ADR 0013), and what <c>/shadow reset</c> restores.</summary>
    private const float FocusDistBoot = 24f, NearRadiusBoot = 25f, CubeWalkBoot = 40f;
    /// <summary>Boot value of <see cref="_lightCullMode"/> (see it for what the modes mean).</summary>
    private const int LightCullBoot = 1;

    /// <summary>The one-time <c>[shadowdiag]</c> capture dumps, off by default. Read by
    /// <see cref="CameraCapture"/>, whose dumps this flag has always claimed to gate.</summary>
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
        if (!_enabled) return;

        _appMgr = Native.Sym(SymbolMap.AppManager);
        _getModule = (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetModule);
        _getGspF = (delegate* unmanaged<uint, float>)Native.Sym(SymbolMap.RiGetGlobalShaderParamf);
        _getAdjRadius = (delegate* unmanaged<IntPtr, float>)Native.Sym(SymbolMap.GetLightAdjustedRadius);

        // Caster completeness (slice 3c): hook ManageSceneBSP (call-through) to re-walk the BSP sphere and
        // re-submit off-screen casters; resolve the submit + traversal + opacity primitives.
        _rewalkOn = Settings.Bool("shadow_rewalk", true);
        _origManageSceneBSP = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.ManageSceneBSP, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnManageSceneBSP);
        _origAddPartsToDrawBuckets = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.AddPartsToDrawBuckets, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnAddPartsToDrawBuckets);
        _addPartToMeshBuckets = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.AddPartToMeshBuckets);
        _bspTraverseSphere = (delegate* unmanaged<IntPtr, Vector3*, float, IntPtr, void>)Native.Sym(SymbolMap.BSPTraverseSphere);
        _partGetIsOpaque = (delegate* unmanaged<IntPtr, byte>)Native.Sym(SymbolMap.PartGetIsOpaque);

        // Dynamic gob casters (slice 3d): the global gobbucket + its Add, appended from the same hook.
        _gobsOn = Settings.Bool("shadow_gobs", true);
        _gobBucket = Native.Sym(SymbolMap.GobBucket);
        _gobBucketAdd = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.GobBucketAdd);

        // Camera-follow anchor: the real PC position, camera- and shadow-path-independent (Scene+0x58 reads null
        // under the vanilla-stencil full kill). CurrentCamera is a global pointer slot -- store its address, deref per frame.
        _currentCamera = Native.Sym(SymbolMap.CurrentCamera);

        // The per-presented-frame edge. Was a zero-size UiHost Window abused as a draw slot (#169); #170 gives
        // it the real thing. Priority 0: it ran inside the window roll-call, i.e. before the #80 context bracket.
        FrameRegistry.Global.Register(FramePhase.PrePresent, 0, Draw, "SceneInputs.frameEdge", log);
        log.Info($"SceneInputs: light walk + caster re-walk armed, feeding CasterPass " +
                 $"(config shadows=0 disables) " +
                 $"(rewalk={(_rewalkOn ? (_origManageSceneBSP != null && _bspTraverseSphere != null && _addPartToMeshBuckets != null ? "ok" : "MISS") : "off")}, " +
                 $"gobs={(_gobsOn ? (_gobBucket != IntPtr.Zero && _gobBucketAdd != null ? "ok" : "MISS") : "off")})");
        log.Info($"SceneInputs: effective tuning: {Describe()}");
    }

    /// <summary>Keys <c>/shadow</c> accepts. The first two are <see cref="CasterPass"/>'s, the rest this
    /// feed's -- one command over the whole shadow path, because a player tuning it is looking at one thing.</summary>
    private const string ShadowKeys = "dist|lod|rebake|nearradius|focusdist|cubewalk|lightcull|diag|lightdiag|torchdiag|trace";

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
            return $"[shadow] reset -> {Describe()}";
        }
        if (a.Length != 2 || !float.TryParse(a[1], System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out float v)
            || v < 0f)
            return $"[shadow] usage: /shadow | /shadow <{ShadowKeys}> <value> | /shadow reset";

        float old = Tune(a[0], v);
        if (float.IsNaN(old)) old = CasterPass.Tune(a[0], v);
        return float.IsNaN(old)
            ? $"[shadow] unknown knob \"{a[0]}\" -- try {ShadowKeys.Replace('|', ' ')}"
            : $"[shadow] {a[0]} {F(v)} (was {F(old)}) -> {Describe()}";
    }

    /// <summary>The live tuning state, feed and caster on one line.</summary>
    internal static string Describe()
        => $"feed {(_enabled ? "on" : "off")} nearradius {_gobAppendRadius:F0} focusdist {_focusDist:F0} " +
           $"cubewalk {_cubeWalkRadius:F0} lightcull {_lightCullMode} " +
           $"diag {B(_diag)}{B(_lightDiag)}{B(_torchDiag)}{B(_trace)} | {CasterPass.Describe()}";

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
            case "nearradius": old = _gobAppendRadius; _gobAppendRadius = MathF.Max(v, 1f); return old;
            case "focusdist": old = _focusDist; _focusDist = v; return old;
            case "cubewalk": old = _cubeWalkRadius; _cubeWalkRadius = MathF.Max(v, 1f); return old;
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
        _gobAppendRadius = NearRadiusBoot;
        _focusDist = FocusDistBoot;
        _cubeWalkRadius = CubeWalkBoot;
        _lightCullMode = LightCullBoot;
        _diag = _lightDiag = _torchDiag = _trace = false;
    }

    private static delegate* unmanaged<IntPtr, void> _origAddPartsToDrawBuckets;

    /// <summary>ADR 0007 phase 1: the ONE deterministic per-frame slot for every extender render pass.
    /// <c>Scene::Render</c> calls this after <c>ManageSceneBSP</c> and before the virtual render dispatch
    /// (Ghidra @0x5762d0), so at return the buckets are complete and nothing has drawn yet. The passes used
    /// to ride <c>Scene::RenderShadows</c>, which the engine calls only when ITS shadow selection wants
    /// stencil work -- zone-dependent, so whole frames passed with the light snapshot left stale (the
    /// "torch randomly off" bug). First fire per presented frame only: reflection
    /// subframes re-run Scene::Render, but the maps are light-space (camera-independent).
    /// <para>#118: the world-caster bake hangs off the end of this, so ADR 0011's shadow bracket opens here
    /// instead of at the present. The bracket count is unchanged -- it moved, it did not multiply.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnAddPartsToDrawBuckets(IntPtr scene)
    {
        if (_origAddPartsToDrawBuckets != null) _origAddPartsToDrawBuckets(scene);
        if (scene == IntPtr.Zero || _passDoneThisFrame) return;
        // Phase 2: self-drive the light walk here (zero-frame-fresh snapshot; the engine's own
        // GetShadowLights calls no longer walk). Unconditional -- the Alloy pass's light SSBO needs
        // the snapshot even when the shadow pipeline itself is off.
        // #238: one zone over the whole ADR 0007 pass slot -- the light walk, the focus and the caster bake
        // are one non-overlapping span of the frame, and it is a SIBLING of the shim rather than nested in it.
        long tShadow = FrameZones.Now;
        WalkMasterList(*(IntPtr*)(scene + SceneLightManager));
        // The shadow focus every Alloy view fits around. Published here rather than at the present because
        // this is the slot where the engine's own camera state is current (ADR 0007 phase 1).
        ShadowFocus();
        // #118: the world-caster bake, HERE rather than at the present, so the cascades and cubes are baked
        // before the colour pass that samples them. Everything it needs is current: the light walk and the
        // focus just ran, and ManageSceneBSP's caster re-walk ran before this hook.
        CasterPass.OnPassSlot();
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

    // LightManager layout (Ghidra: AddLight / GetShadowLights / SetupLightsForThisPositionAndRadius): the MASTER
    // CExoArrayList<PartLight*> sits at this+0x10 (data) / +0x18 (count) -- every registered light in the loaded
    // area, creation order (stable). PartLight fields: castShadows +0x74, type +0x80 (0 = point/directional),
    // enable flags +0x84/+0x88 (either set = active), range +0x70, intensity +0xa4,
    // RGB +0x64, world pos +0xac. Directional (sun/moon) excluded by range, like ShadowService.
    private const int LmMasterData = 0x10, LmMasterCount = 0x18;
    private const int PlCastShadows = 0x74, PlType = 0x80, PlFlagA = 0x84, PlFlagB = 0x88;
    /// <summary>The MDL light node's <c>radius</c> (+0x70) and <c>shadowradius</c> (+0x78) controllers
    /// (Ghidra <c>MdlNodeLight::InternalParseField</c> @0x44baf0 binds both names to those offsets;
    /// <c>PartLight::PartLight</c> @0x43d1d0 defaults 0x78 to -1). Only 0x70 reaches the engine's lighting --
    /// 0x78 is a shadow-only override its STENCIL paths consult, and #186 stopped it sizing the Alloy cubes.
    /// It is read here for the diagnostic dumps and nothing else.</summary>
    private const int PlRange = 0x70, PlRangeAlt = 0x78, PlIntensity = 0xa4, PlColor = 0x64, PlPos = 0xac;
    /// <summary>Engine shadow-fade weight (GetShadowLights decrements it per frame for deprioritized
    /// lights). Diagnostic only -- printed by the masterlight dump.</summary>
    private const int PlFade = 0xa8;
    private const float PlDirectionalRange = 1000f;
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

    /// <summary>Radius slack on the frustum test: covers the mirrored reflection-subframe camera and one
    /// frame of camera-turn latency without re-introducing pop.</summary>
    private const float FrustumCullSlack = 1.25f;
    private static int _lightsCulled;
    private static bool _lightDiag;

    /// <summary>Scene -&gt; LightManager* (Ghidra <c>Scene::RenderSinglePass</c> @0x56e5f0 calls
    /// <c>GetShadowLights(*(this + 0x158))</c>).</summary>
    private const int SceneLightManager = 0x158;

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
            Sowx.Alloy.AlloyLights.LatchAtten(_getGspF(0x13), _getGspF(0x14), radiusMult);
        // BOTH snapshots restart here, not at the frame edge. CasterPass consumes the local-light set later
        // in this same pass slot, and the roll-call this service's frame edge rides runs at the present --
        // resetting there handed the cube pass an empty set every frame. The walk is once-per-frame and
        // idempotent, so restarting it at its own top is the same reset with no ordering to get wrong.
        FrameInputs.BeginAreaLights();
        FrameInputs.ResetLocalLights();
        bool walkDiag = _trace && ++_walkDiagTick >= 900;   // ~15s at 60fps (one walk/frame): dump raw filter fields near the camera
        if (walkDiag) _walkDiagTick = 0;
        for (int i = 0; i < count; i++)
        {
            byte* p = (byte*)*((IntPtr*)data + i);
            if (p == null) continue;
            if (walkDiag && FrameInputs.HasCamera)
            {
                var wp = new Vector3(*(float*)(p + PlPos), *(float*)(p + PlPos + 4), *(float*)(p + PlPos + 8));
                float wd = (wp - FrameInputs.CameraPos).Length();
                if (wd < 40f)
                    _log?.Info($"SceneInputs: masterlight d={wd:F1} pos=({wp.X:F0},{wp.Y:F0},{wp.Z:F1}) type={*(int*)(p + PlType)} cast={*(int*)(p + PlCastShadows)} flags={*(int*)(p + PlFlagA)}/{*(int*)(p + PlFlagB)} range={*(float*)(p + PlRange):F1} alt={*(float*)(p + PlRangeAlt):F1} inten={*(float*)(p + PlIntensity):F2} fade={*(float*)(p + PlFade):F2} rgb=({*(float*)(p + PlColor):F2},{*(float*)(p + PlColor + 4):F2},{*(float*)(p + PlColor + 8):F2})");
            }
            if (*(int*)(p + PlFlagA) == 0 && *(int*)(p + PlFlagB) == 0) continue;
            float range = *(float*)(p + PlRange);
            if (range <= 0f || range >= PlDirectionalRange) continue;
            float inten = *(float*)(p + PlIntensity);
            if (inten <= 0f) continue;
            float r = *(float*)(p + PlColor), g = *(float*)(p + PlColor + 4), b = *(float*)(p + PlColor + 8);
            if (r == 0f && g == 0f && b == 0f) continue;
            if (*(int*)(p + PlType) != 0) continue;
            float alt = *(float*)(p + PlRangeAlt);
            var pos = new Vector3(*(float*)(p + PlPos), *(float*)(p + PlPos + 4), *(float*)(p + PlPos + 8));
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
                float boost = 1f + (maxc - 1f) * Sowx.Alloy.AlloyLights.OverbrightScale;
                if (boost < 0f) boost = 0f;
                float k = MathF.Pow(boost, 1f / Sowx.Alloy.AlloyLights.SrgbGamma) / maxc;
                r *= k; g *= k; b *= k;
            }
            float upW = adjR;
            var color = new Vector3(r, g, b);
            // Receiver light cull (#67 perf): the Alloy fragment loop iterates EVERY uploaded light, so a
            // light whose lit sphere can't touch a drawn fragment still costs every fragment a range test.
            // Mode 1 (default) = camera-distance vs the far plane (orientation-free: immune to the
            // reflection subframe's mirrored camera and needs no view-rotation convention). Mode 2 =
            // frustum sphere test on a camera->focus look-at basis (tighter; opt-in until live-proven).
            // 0 = off. Cube slots below are NOT culled -- off-screen cubes stay warm, no rebake churn.
            bool culled = false;
            if (_lightCullMode > 0 && FrameInputs.HasCamera && FrameInputs.HasPerspective)
            {
                float slackR = adjR * FrustumCullSlack;
                // Last frame's MAX far, never the latest raw capture -- see FrameInputs.FarMaxLastFrame
                float cullFar = FrameInputs.FarMaxLastFrame > 0f ? FrameInputs.FarMaxLastFrame : FrameInputs.CamFar;
                bool vis = _lightCullMode == 1 || _focus == default
                    ? (pos - FrameInputs.CameraPos).LengthSquared() < (cullFar + slackR) * (cullFar + slackR)
                    : ShadowMath.SphereInCameraFrustum(pos, slackR, FrameInputs.CameraPos, _focus,
                                                       FrameInputs.CamFovy, FrameInputs.CamAspect, FrameInputs.CamNear, cullFar);
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
            if (*(int*)(p + PlCastShadows) == 0) continue;
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
        _walkDoneCount = FrameInputs.AreaLightCount;
    }

    /// <summary>CPU-side light-&gt;cube bind: cube slot per AREA light (-1 = no cube), the one thing that tells
    /// the Alloy lit stage which cube layer a light's shadow lives in. Driven from the pass slot AFTER the
    /// bake (#118), so the mapping and the cube contents describe the same frame.</summary>
    private static void BindLightCubes()
    {
        int bound = 0;
        for (int i = 0; i < FrameInputs.AreaLightCount; i++)
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
        // The Alloy path only sees what the froxel build binned, and only the first MaxAreaLights of the
        // snapshot are candidates for a shadow cube -- so the snapshot INDEX decides both.
        string reach = _tSnapIdx < 0
            ? (_tCulled ? "CULLED (not in snapshot at all)" : "DROPPED (snapshot full)")
            : _tSnapIdx < FrameInputs.MaxAreaLights
                ? $"snapshot[{_tSnapIdx}] -- lit and shadow-eligible"
                : $"snapshot[{_tSnapIdx}] -- lit only, PAST the {FrameInputs.MaxAreaLights} shadow-candidate cap";
        int slot = -1;
        for (int i = 0; i < FrameInputs.LightCount && i < FrameInputs.MaxLights; i++)
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
                   $"snapshot {FrameInputs.LightCount}/{FrameInputs.MaxLights} (overflow {FrameInputs.LightOverflow}), " +
                   $"culled/period={_lightsCulled}, cullMode={_lightCullMode} " +
                   $"farLast={FrameInputs.FarMaxLastFrame:F0} camFar={FrameInputs.CamFar:F0}");
    }
    /// <summary>Snapshot count at the end of the last completed master-list walk; the upload comparing its
    /// live count against this detects mid-walk sampling (lights after the cut go dark that frame).</summary>
    private static int _walkDoneCount;

    /// <summary>ManageSceneBSP frame slot: after the engine's (camera-culled) BSP populate, augment the same-frame
    /// draw with the casters the camera frustum dropped -- static tile parts (via a BSP sphere re-walk) and
    /// off-screen movable gobs (appended to the global gobbucket) -- so the shadow map is camera-independent. Runs
    /// BEFORE the same-frame <c>Scene::AddPartsToDrawBuckets</c>, so both get transferred into the draw buckets +
    /// matrix-set by the engine and drawn by our depth pass (and the camera pass, where off-screen geometry is
    /// GPU-clipped). Centred on the shared per-frame focus, so it stays anchored to the player, not the camera.</summary>
    [UnmanagedCallersOnly]
    private static void OnManageSceneBSP(IntPtr scene)
    {
        if (_origManageSceneBSP != null) _origManageSceneBSP(scene);
        if (!Owns) return;
        // Only the camera is required: the per-light cube walks must run in sunless interiors too (gating on
        // HasSun made interior torch shadows depend on whether an exterior was ever visited -- "random").
        if (scene == IntPtr.Zero || !FrameInputs.HasCamera) return;
        // Augment on EVERY scene's populate -- main AND reflection subframes, no scene-identity latch. The depth
        // pass accumulates every RenderShadows fire into one light-space map, so every scene's buckets must carry
        // the sphere set. The engine clears the buckets per populate; extra parts in a reflection render are
        // off-its-frustum -> GPU-clipped, harmless. One shared per-frame focus so the sphere aligns with the map.
        // Tile and gob augmentation are independently gated (config shadow_rewalk / shadow_gobs) for clean
        // attribution.

        Vector3 center = ShadowFocus();
        if (!_interiorDecided)
        {
            _interiorDecided = true;
            bool wasInterior = _interior;
            _interior = AreaIsInterior();
            FrameInputs.Interior = _interior;   // CasterPass skips its sun cascades on it
            if (_interior != wasInterior || !_interiorLogged)
            { _interiorLogged = true; _log?.Info($"SceneInputs: area is {(_interior ? "INTERIOR -- no sun shadows" : "exterior -- sun shadows on")}"); }
        }
        // The wide walk is what fills the OUTER sun cascades: the caster set can only hold parts the engine's
        // bucket walk carried, so on the frames CasterPass rebuilds that set the sphere has to reach its max
        // shadow distance. Every other frame walks only far enough for the torch cubes' statics, which is what
        // keeps the camera pass from re-submitting thousands of off-screen parts sixty times a second.
        // NOT gated on the exterior any more: the local cubes eat from the same enrolled caster set, and an
        // interior is exactly where a torch needs walls 40-60u out to occlude it. One wide walk per
        // enrollment window is what the sun already pays outdoors.
        float walkR = CasterPass.WantsWideWalk
            ? MathF.Max(CasterPass.WalkRadius, _cubeWalkRadius) : _cubeWalkRadius;
        float radiusSq = walkR * walkR;   // BSPTraverse compares against SQUARED plane distances -- pass r*r

        // Tiles (slice 3c): re-walk the static-geometry BSP sphere and re-submit the tile parts the camera culled.
        if (_rewalkOn && _bspTraverseSphere != null && _addPartToMeshBuckets != null)
        {
            IntPtr root = *(IntPtr*)((byte*)scene + SceneLayout.SceneBspRoot);
            if (root != IntPtr.Zero)
            {
                _rewalkParts = 0;
                _bspTraverseSphere(root, &center, radiusSq,
                    (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, float, void>)&OnBspNode);
                if (!_rewalkLogged)
                { _rewalkLogged = true; _log?.Info($"SceneInputs: BSP re-walk submitted {_rewalkParts} caster parts (radius={walkR:F0})"); }

            }
        }

        // Gobs (slice 3d): append the off-screen movable objects the engine culled -- but only within the NEAR
        // radius, since dynamics now cast ONLY into the near map; a gob beyond it would be dead weight in the
        // camera pass (drawn, then GPU-clipped) that never reaches any shadow map.
        if (_gobsOn) AppendOffscreenGobs(scene, center, _gobAppendRadius * _gobAppendRadius);
    }

    /// <summary>Append the scene's off-screen in-sphere gobs (movable objects) to the global gobbucket, so they
    /// cast even when off-camera. The engine's own ManageSceneBSP loop adds ONLY gobs with the in-view byte set
    /// (gob+0x1b), so off-screen movers never reach the draw buckets; we add the ones inside the map sphere. The
    /// same <c>Scene::AddPartsToDrawBuckets</c> then sorts + routes them into the opaque buckets our depth pass
    /// draws. Guards mirror the engine's invariants: gob+0x68 (node) MUST be non-null or the gobbucket sort
    /// comparator null-derefs; gob+0x90 (part tree) non-null so a geometry-less gob is skipped; set gob+0x1a5
    /// (dirty) so ProcessGob recomputes the has-geometry flag the transfer gates on. Alloc-free (raw reads; the
    /// engine's Add reallocs its own native buffer).</summary>
    private static void AppendOffscreenGobs(IntPtr scene, Vector3 center, float radiusSq)
    {
        if (_gobBucket == IntPtr.Zero || _gobBucketAdd == null) return;
        IntPtr list = *(IntPtr*)((byte*)scene + SceneGobList);
        int count = *(int*)((byte*)scene + SceneGobCount);
        if (list == IntPtr.Zero || count <= 0) return;

        int added = 0;
        for (int i = 0; i < count; i++)
        {
            IntPtr gob = ((IntPtr*)list)[i];
            if (gob == IntPtr.Zero) continue;
            if (*((byte*)gob + GobInView) != 0) continue;                       // engine already added the visible ones
            if (*(IntPtr*)((byte*)gob + SceneLayout.GobNode) == IntPtr.Zero) continue;      // sort-comparator would null-deref
            if (*(IntPtr*)((byte*)gob + SceneLayout.GobPartTree) == IntPtr.Zero) continue;  // no part tree -> nothing to cast
            float* p = (float*)((byte*)gob + SceneLayout.GobPosition);
            float dx = p[0] - center.X, dy = p[1] - center.Y, dz = p[2] - center.Z;
            if (dx * dx + dy * dy + dz * dz > radiusSq) continue;
            *((byte*)gob + GobGeometryDirty) = 1;   // force ProcessGob to (re)compute has-geometry this frame
            _gobBucketAdd(_gobBucket, gob);
            added++;
        }
        _rewalkGobs = added;
        // Log the first frame that actually appends something (the first-frame count is ~always 0 -- nothing is
        // off-screen yet -- so a plain one-shot said nothing about whether the feature ever engages).
        if (!_gobsLogged && added > 0)
        { _gobsLogged = true; _log?.Info($"SceneInputs: appended {added} off-screen gob casters (of {count} scene gobs, nearRadius={_gobAppendRadius:F0})"); }
    }

    /// <summary>Per BSP node in the sphere: submit the node's static tile-mesh parts (nodedata+0x20 array, or the
    /// single primary at +0x90 as a fallback) as shadow casters via the engine's own AddPartToMeshBuckets --
    /// exactly what <c>ProcessTriMeshParts</c> does, minus the camera frustum test. Only opaque parts (a
    /// transparent double-submit double-blends). Fires on internal nodes too, so null-check contents.
    /// ponytail: no dedup with the engine's own submit (opaque depth writes are idempotent); animated opaque
    /// tile parts double-advance -- add a per-frame flag filter if either visibly matters.</summary>
    [UnmanagedCallersOnly]
    private static void OnBspNode(IntPtr node, IntPtr center, float radiusSq)
    {
        if (node == IntPtr.Zero) return;
        IntPtr nodedata = *(IntPtr*)((byte*)node + BspNodeData);
        if (nodedata == IntPtr.Zero) return;
        // Submit BOTH the parts array AND the single primary, always: the engine uses one or the other depending
        // on currentscene+0x1e0, and submitting only one leaves the caster's other representation out -> a
        // partial (hollow) footprint. Duplicates are idempotent depth writes; the transfer drops junk.
        IntPtr parts = *(IntPtr*)((byte*)nodedata + NodeDataParts);
        int count = *(int*)((byte*)nodedata + NodeDataPartCount);
        if (parts != IntPtr.Zero && count > 0)
            for (int i = 0; i < count; i++) SubmitPart(((IntPtr*)parts)[i]);
        SubmitPart(*(IntPtr*)((byte*)nodedata + NodeDataPrimary));
    }

    /// <summary>Submit one tile part as a caster if it's non-null and opaque. Sets the mesh's renderable-this-
    /// frame flag first (meshdata+0x118) -- else Scene::AddPartsToDrawBuckets' IsPartRenderable filter drops the
    /// part, silently eating every off-screen caster the camera never submitted (the engine's own submit path
    /// sets the same flag). This is what fills the map for casters the camera can't see.</summary>
    private static void SubmitPart(IntPtr part)
    {
        if (part == IntPtr.Zero) return;
        if (_partGetIsOpaque != null && _partGetIsOpaque(part) == 0) return;
        IntPtr meshData = *(IntPtr*)((byte*)part + PartMeshData);
        if (meshData != IntPtr.Zero && *(int*)((byte*)meshData + MeshRenderableFlag) == 0)
            *(int*)((byte*)meshData + MeshRenderableFlag) = 1;
        _addPartToMeshBuckets(part);
        _rewalkParts++;
    }

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
        var asFollow = (delegate* unmanaged<IntPtr, IntPtr>)(*(IntPtr*)((byte*)vtbl + BehaviorAsFollowVtbl));
        if (asFollow == null) return null;
        IntPtr behavior = asFollow(host);
        if (behavior == IntPtr.Zero) return null;   // non-follow camera -> caller uses sticky/camera-ground
        IntPtr target = *(IntPtr*)((byte*)behavior + BehaviorTargetObj);
        if (target == IntPtr.Zero) return null;
        float* p = (float*)((byte*)target + SceneLayout.GobPosition);
        return new Vector3(p[0], p[1], p[2]);
    }

    /// <summary>Per-presented-frame edge from the UiHost roll-call: roll the far-plane max over, re-arm the
    /// once-per-frame walk and re-read the area flag. The light snapshots are NOT reset here -- they restart
    /// at the top of the walk itself, which since #118 is the same slot that consumes them, one call earlier.
    /// Resetting at this edge instead would be a reset nothing needs, in the frame's other half.</summary>
    private static void Draw()
    {
        if (FrameInputs.FarMaxThisFrame > 0f) FrameInputs.FarMaxLastFrame = FrameInputs.FarMaxThisFrame;
        FrameInputs.FarMaxThisFrame = 0f;
        _haveFocus = false;    // recompute the shared map centre next frame
        _passDoneThisFrame = false; // re-arm the once-per-frame walk (first AddPartsToDrawBuckets fire)
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
        _log.Info($"SceneInputs: {FrameInputs.LocalLightCount} point light(s) recorded (slice 6, overflow {FrameInputs.LocalLightOverflow}):{sb}");
    }

}
