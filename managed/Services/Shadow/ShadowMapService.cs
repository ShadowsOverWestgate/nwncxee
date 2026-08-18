using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Ui;

namespace Sowx.Services.Shadow;

/// <summary>
/// Slice 3b of the shadow-map replacement (#65): the real caster depth pass. Under
/// <c>SOWX_SHADOWDIAG=1</c> it hooks <c>Scene::RenderShadows</c> (call-through) as the render-thread
/// frame slot -- there the <c>this</c> is the live <c>Scene*</c> and the GL/camera transforms are
/// current -- and re-drives the engine's own opaque casters (bucket 1) into an off-screen depth FBO by
/// calling <c>Scene::RenderDrawBucket(scene, 1)</c> with our FBO bound. The depth texture is then blit
/// to the screen corner from the UiHost roll-call so the pass is visible. This is "plan A": we set the
/// engine's transform/FBO state in our own bracket and let its shaders draw -- confirmed by data flow
/// (SetView/Ortho write <c>m_aurMtxStack</c> + bump <c>uniform_dirty_seq</c>; each caster's SetModelMatrix
/// re-marks dirty so SetProgram re-uploads the MVP), so no glUniformMatrix interposition is needed.
/// </summary>
/// <remarks>
/// <para>3b-i (this step) renders with the engine's CURRENT camera view/proj unchanged -- proving the
/// re-drive drives real geometry into our FBO and that bucket 1 is the opaque casters, with zero
/// light-space convention risk. 3b-ii swaps the view to the light.</para>
/// <para>The depth pass runs in the hook (GL context current, Scene* in hand) and fully brackets GL
/// state; the corner blit runs later at SwapBuffers (UiHost roll-call), after the engine's own draws,
/// so it isn't overdrawn. GL objects are created lazily on the first in-module pass. ponytail: FBO
/// lifetime across a video-mode restart isn't handled -- re-create on GL-reset when robustness matters.</para>
/// </remarks>
public sealed unsafe class ShadowMapService : IExtenderService
{
    private static int _mapSize = 2048;   // SOWX_SHADOWMAPSIZE; bigger = less pixelation (2048^2 D24 ~ 16MB)
    private static float _focusDist = 24f; // SOWX_SHADOWFOCUSDIST; march the map centre ahead of the camera
    private static ExtenderLog? _log;
    private static bool _enabled, _diag, _glReady, _glFailed;

    // Two-map cascade (slice 5): a WIDE STATIC map (tiles/buildings, large radius, unit 7) + a NEAR DYNAMIC map
    // (characters/creatures/placeables, small radius -> higher texel density, unit 8). One map over the wide
    // radius is too coarse for a character's edge (~0.08 u/texel at r=80); the near map gives it near-radius-20
    // sharpness while the wide map keeps environment coverage. Static casters (buckets 0,1 -- tiles) go ONLY to
    // the wide map; dynamic casters (2,3,4 -- gobs) go ONLY to the near map, so the receiver's min(wide,near)
    // shows the crisp near edge with no blocky wide-map bulge. SOWX_SHADOWBUCKETS / SOWX_SHADOWNEARBUCKETS tune.
    private static int[] _wideBuckets = { 0, 1 };
    private static int[] _nearBuckets = { 2, 3, 4 };
    private static float _nearRadius = 25f;   // SOWX_SHADOWNEARRADIUS -- tight, high-density dynamic-object map
    private const uint NearShadowUnit = 8;    // unit 7 = wide map; 8 = near map (engine samplers are units 0..6)
    private static uint _fboNear, _depthTexNear;
    private static System.Numerics.Matrix4x4 _lastLightVPNear;
    private static uint _quadVaoNear;         // second corner quad, right of the wide-map blit
    private static readonly int[] _recvNearLightVPLoc = new int[MaxRecv];
    private static readonly int[] _recvNearSamplerLoc = new int[MaxRecv];
    private static readonly int[] _recvNearTexelLoc = new int[MaxRecv];
    private static readonly int[] _recvNearBiasLoc = new int[MaxRecv];

    // GL objects (0 = not yet created). Under plan A the caster pass uses the engine's shaders, so we
    // own only the depth target and the debug blit -- no depth program / test geometry.
    private static uint _fbo, _depthTex, _quadVao, _blitProg;
    private static int _uDepth;

    // Engine entry points: the RenderShadows trampoline (call-through) and the caster-bucket re-drive.
    private static delegate* unmanaged<IntPtr, int, byte, void> _origRenderShadows;
    private static delegate* unmanaged<IntPtr, int, void> _renderDrawBucket;
    private static bool _firesLogged;
    private static int _shadowFires;   // RenderShadows fires per frame (reflection-subframe diagnostic)

    // Caster completeness (slice 3c): re-walk the scene BSP in a sphere around the player and re-submit the
    // casters the camera frustum culled, so the depth map is camera-independent. ManageSceneBSP hook runs the
    // re-walk (pre-transfer); the engine's own AddPartsToDrawBuckets then moves them into the draw buckets.
    private static delegate* unmanaged<IntPtr, void> _origManageSceneBSP;
    private static delegate* unmanaged<IntPtr, void> _addPartToMeshBuckets;
    private static delegate* unmanaged<IntPtr, Vector3*, float, IntPtr, void> _bspTraverseSphere;
    private static delegate* unmanaged<IntPtr, byte> _partGetIsOpaque;
    private static int _rewalkParts;    // caster parts submitted this frame (diagnostic)
    private static bool _rewalkLogged;
    private static bool _rewalkOn = true;   // SOWX_SHADOWNOREWALK=1 disables (falls back to camera-culled set)

    // Dynamic gob casters (slice 3d): the engine's ManageSceneBSP adds ONLY camera-visible gobs (gob+0x1b) to
    // the global gobbucket. We append off-screen in-sphere gobs there so movable objects (placeables, creatures)
    // cast even off-camera; the same Scene::AddPartsToDrawBuckets sorts + routes them into the opaque buckets.
    private static IntPtr _gobBucket;   // the CExoArrayList<Gob*> base (symbol address == list `this`)
    private static delegate* unmanaged<IntPtr, IntPtr, void> _gobBucketAdd;
    private static IntPtr _currentCamera;   // &CurrentCamera global; walk it to the camera-follow target (the PC)
    private static int _rewalkGobs;     // off-screen gobs appended this frame (diagnostic)
    private static bool _gobsLogged;
    private static bool _localLoggedOnce;   // slice 6: one-shot log of the first frame that records point lights

    // Point-light shadow cubes (slice 6): one DEPTH_COMPONENT24 cube per recorded light, re-attached face by face
    // into a shared FBO and drawn from the light with a 90deg perspective per face (plan A: engine draws our
    // casters). Depth-only; the receiver (slice C) reconstructs distance analytically from the major axis + near/
    // far, so no per-face matrices leave this file. Dynamic buckets only (vanilla torch scope; SOWX_LOCALBUCKETS).
    private static bool _localOn = true;              // SOWX_NOLOCAL=1 disables the cube pass
    private static int _cubeSize = 256;               // SOWX_LOCALCUBESIZE (per face). 256 default: live A/B showed no visible quality loss vs 512 but a clear fps win (smaller raster area + cache-friendlier receiver fetches); the shader's bias slope is tuned for 256 anyway.
    private static float _localNear = 0.3f;           // SOWX_LOCALNEAR (>=0.1; bigger = better depth precision)
    private static float _localBias = 0.15f;          // SOWX_LOCALBIAS -- world-unit depth bias (linear space; slope-scaled in the receiver)
    private static float _rangeMult = 2.0f;           // SOWX_LOCALRANGEMULT -- cube far = light range * this. The engine can light a bigger radius than the range field we read (oversized lights), so shadows cut off inside the lit area; extend the shadow reach to cover it. Beyond the true lit radius the surface is unlit, so over-extending is harmless.
    private static readonly float[] _cubeFar = new float[ShadowInputs.MaxLocalLights];  // UPLOADED far per slot: the rendered far while active, 0 while inactive (far=0 makes the slot inert in both shader paths)
    // Static/dynamic cube split: each slot owns a STATIC cube (tile buckets, re-rendered only when its light moves
    // > eps) and the WORKING cube the receiver samples (static copied in via glCopyImageSubData, then the dynamic
    // buckets drawn on top EVERY frame). Placed torches pay ~zero static cost; creatures stay per-frame smooth.
    // Slot identity is STABLE: each recorded light is matched to a slot by position (SlotMatchTol), not by record
    // index -- record-order churn was re-rendering every slot every frame. Both cube layers render at the slot's
    // last-STATIC-rendered pos (uploaded as sowxLocalPos) so sampling stays aligned between static refreshes.
    private static readonly Vector3[] _slotPos = new Vector3[ShadowInputs.MaxLocalLights];   // static-render pos per slot (uploaded; dynamics render here too)
    private static readonly Vector3[] _slotSeenPos = new Vector3[ShadowInputs.MaxLocalLights]; // last-seen recorded pos (the slot-match key; tracks a carried torch between static refreshes)
    private static readonly float[] _slotFar = new float[ShadowInputs.MaxLocalLights];       // rendered far per slot (kept across inactive spells; _cubeFar is the gated upload)
    private static readonly bool[] _slotValid = new bool[ShadowInputs.MaxLocalLights];
    private const float SlotMatchTol = 2f;            // recorded light -> slot match distance (a carried torch moves well under this per frame)
    private static float _updateEps = 0.05f;          // SOWX_LOCALUPDATEEPS -- refresh a slot's STATIC cube when its light moves > this (world u). Small so a carried torch's shadows glide instead of lurching in eps steps; MUST stay < the receiver match tol (SOWX_LOCAL_MINSEP 0.5).
    private static int _slotsStatic, _slotsActive;    // per-pass diag: static cubes re-rendered / slots active
    private static int _staticRefreshAccum;           // static cube re-renders since the last periodic log (churn visibility)
    // A moving light (carried torch) trips the eps test EVERY frame -> its static cube (thousands of parts x 5
    // faces, full material shaders) re-rasterizes per frame. Cooldown caps that at 1 refresh per N frames; the
    // dynamic layer still updates every frame, so only the static silhouette lags ~N frames while moving.
    private static int _staticCooldown = 2;           // SOWX_LOCALSTATICCD (frames); 1 = old behavior. 2: refresh frames got cheap (receiver-muted uniforms + 256px faces), and 4 stepped a carried torch's silhouette at a visible ~15Hz.
    private static readonly int[] _slotCooldown = new int[ShadowInputs.MaxLocalLights];
    // Keep-alive + eviction hysteresis: the engine's shadow-light set (capped at m_nMaxShadowLights) is
    // re-prioritized around the CAMERA, so a slight move swaps which 4 lights it reports -> cubes popped in/out
    // and the whole scene's shadowing flickered with movement. An unclaimed-but-valid slot now keeps its last
    // cube uploaded (the light usually still lights the scene -- only the engine's SHADOW pick dropped it) and
    // can only be evicted for a new light after EvictAfterFrames unclaimed; a light that can't get a slot simply
    // waits. KeepAliveFrames bounds staleness (extinguished light / area change: fade after ~5s).
    private const int EvictAfterFrames = 30;
    private const int KeepAliveFrames = 600;   // ponytail: no light-liveness check; a deleted light's 0.2 stamp lingers up to this
    private static readonly int[] _slotUnclaimedAge = new int[ShadowInputs.MaxLocalLights];
    // Per-light caster walks: a static cube may only be baked from buckets that actually CONTAIN its casters.
    // The per-frame BSP walk is player-centred (radius ~_cubeWalkRadius), so a light farther out baked an EMPTY
    // cube at slot-claim time and never shadowed -- and slot reclaims while moving re-baked cubes from whatever
    // the buckets held, i.e. shadows popped in with movement. Fix: a refresh whose caster sphere isn't covered
    // by this frame's player walk defers and requests a dedicated BSP walk around ITS light (run by the next
    // ManageSceneBSP, up to WalksPerPopulate per populate); the bake then proceeds one frame later.
    private static readonly bool[] _slotWalkPending = new bool[ShadowInputs.MaxLocalLights];
    private static readonly bool[] _slotWalked = new bool[ShadowInputs.MaxLocalLights];
    private static readonly Vector3[] _slotWalkPos = new Vector3[ShadowInputs.MaxLocalLights];
    private static readonly float[] _slotWalkR = new float[ShadowInputs.MaxLocalLights];
    private const int WalksPerPopulate = 3;   // matches the static-bake budget in CubePass
    private static float _lastWalkR;          // radius the player-centred walk ran with this frame (0 = none)
    // Per-slot dynamic-layer gate: with 32 area slots, unconditionally re-copying the static layer + walking the
    // dynamic buckets for EVERY slot EVERY frame (32 glCopyImageSubData + 576 bucket walks) tanked interior fps.
    // A slot renders its dynamic layer only when some scene gob is within its caster range (cheap CPU sweep);
    // when the last caster leaves, ONE wipe copy restores the pure-static cube and the slot then costs nothing.
    private static readonly bool[] _slotDynDirty = new bool[ShadowInputs.MaxLocalLights];
    /// <summary>Position signature + count of the in-range gob set at the slot's last dyn render -- the dyn
    /// layer re-renders only when either differs (a gob moved/entered/left), not merely when gobs are near.</summary>
    private static readonly float[] _slotDynSig = new float[ShadowInputs.MaxLocalLights];
    private static readonly int[] _slotDynCount = new int[ShadowInputs.MaxLocalLights];
    /// <summary>Amortized moved-light rebake: next face (0..5) of an in-flight staged static bake, -1 = idle.
    /// A VALID slot's refresh renders 2 faces/frame at the frozen <see cref="_slotStagePos"/> snapshot into the
    /// (unsampled) static layer, then commits pos/far/copy atomically -- the old full 6-face bake landed as a
    /// single-frame spike every cooldown while carrying a torch (the light-heavy-area stutter).</summary>
    private static readonly int[] _slotStageFace = CreateFilled(-1);
    private static readonly Vector3[] _slotStagePos = new Vector3[ShadowInputs.MaxLocalLights];
    private static readonly float[] _slotStageFar = new float[ShadowInputs.MaxLocalLights];
    /// <summary>Staged static face renders allowed per cube pass (2 = one slot's chunk; a second mover resumes next frame).</summary>
    private const int StageFacesPerPass = 4;

    /// <summary>Low-passed CARRIER-RELATIVE light offset per slot (priority lights only) -- see the EMA in the
    /// render loop: bake pos = focus + this, so carrier motion is lag-free and only the sway wobble is damped.</summary>
    private static readonly Vector3[] _slotSmoothRel = new Vector3[ShadowInputs.MaxLocalLights];
    private static float _smoothTau = 0.2f;    // SOWX_LOCALSMOOTH -- bake-pos smoothing time constant (seconds); 0 = off. Heavy damping is safe: matching keys on the RAW pos (sowxLocalMatchPos), so smoothing lag can never unbind a light.
    private static float _passDt = 1f / 60f;   // real seconds between cube passes (clamped), feeds the EMA alpha
    private static long _lastPassTicks;

    private static int[] CreateFilled(int v)
    { var a = new int[ShadowInputs.MaxLocalLights]; for (int i = 0; i < a.Length; i++) a[i] = v; return a; }

    // Shadow LOD (#65): every slot renders at a per-light RESOLUTION picked from importance -- res ~ K *
    // lightReach / playerDistance, rounded to a power of two -- because a shadow texel's screen size scales with
    // reach/res/distance. Res <= _cubeSize renders into a CORNER SUB-RECT of the base 256 array (viewport +
    // receiver UV scale: distant lights get tiny cheap maps for free); res above it promotes into a small hi
    // array pair (DEPTH16 -- halves the 2048-tier footprint; the ~20u far planes don't need 24 bits). The
    // receiver's kernel is a constant couple of TEXELS, so low res is naturally blurry and high res sharp --
    // resolution is the one sharpness/perf dial. Res/tier changes commit atomically with a rebake (the old map
    // is sampled until then), so LOD flips never blink a shadow.
    private static int _hiSlots = 2;                  // SOWX_LOCALHISLOTS (0 disables the hi tier)
    private static int _cubeSizeHi = 2048;            // SOWX_LOCALHISIZE: hi-array face size = the LOD ceiling
    private static float _lodK = 300f;                // SOWX_LOCALLODK: res target = K * far / focusDist
    private static int _lodMin = 32;                  // SOWX_LOCALLODMIN: res floor for far lights
    private static int _priRes = 1024;                // SOWX_LOCALPRIRES: res cap for priority (every-frame rebake) lights
    private static readonly int[] _slotHiLayer = CreateFilled(-1);   // current hi layer per slot (-1 = base array)
    private static readonly int[] _slotHiWant = CreateFilled(-1);    // desired layer; flips into _slotHiLayer on bake commit
    private static readonly int[] _slotStageHi = CreateFilled(-1);   // tier a staged bake targets (want at stage start)
    private static readonly int[] _slotRes = new int[ShadowInputs.MaxLocalLights];      // committed face res (0 = full array face)
    private static readonly int[] _slotResWant = new int[ShadowInputs.MaxLocalLights];
    private static readonly int[] _slotStageRes = new int[ShadowInputs.MaxLocalLights];
    private static readonly int[] _hiOfSlotPlus1 = new int[ShadowInputs.MaxLocalLights];    // uploaded: hi layer + 1, 0 = base (safe uniform default)
    private static readonly float[] _slotUvScaleArr = new float[ShadowInputs.MaxLocalLights]; // uploaded: res / arrayFaceSize
    private static readonly float[] _slotTexelArr = new float[ShadowInputs.MaxLocalLights];   // uploaded: angular texel = 2*(1+seam)/res
    private const float CubeSeamFactor = 2f * 1.015625f;   // matches SOWX_LOCAL_SEAM in inc_common.shd
    private static uint _cubeHiArrTex, _cubeHiStaticArrTex, _cubeHiRawView;
    private static int _slotsDynActive;       // slots that ran a dynamic pass this frame (diagnostic)
    private static int _localDebugCube;               // SOWX_LOCALCUBE 0..3: which cube the corner atlas thumbnail shows

    // Per-light caster cull (perf): the cube pass re-drives the engine's draw buckets, which submit EVERY dynamic
    // part (all tavern furniture) 6 faces x N lights even though most are nowhere near an 8-15u torch. We hook the
    // bucket loop's own SetModelMatrix (read the part's world pos) + RenderVertexData (skip the draw) and sphere-cull
    // each part against the current light's range. Plan-A-native (engine still drives). Also lets STATIC tile buckets
    // {0,1} cast affordably (only walls near the torch draw) -> tileset shadows. SOWX_LOCALCULL=0 disables (A/B).
    private static bool _cullOn = true;
    private const float LocalCullFudge = 3f;          // world-unit slack on the range test so an edge caster keeps its shadow
    private static delegate* unmanaged<IntPtr, void> _origSetModel;
    private static delegate* unmanaged<IntPtr, uint, uint, uint, uint, void> _origRVD;
    // Lean bucket walker (SOWX_LEANWALK=0 restores the hook path): we iterate DrawBucketManager ourselves and
    // cull BEFORE any GL work, so a skipped part costs one vtable call + a compare instead of the engine's full
    // per-part preamble (matrix, shader params, fog, dissolve, Tracy). Also means the SetModelMatrix /
    // RenderVertexData hooks are NOT installed -> the engine's own camera render sheds their overhead too.
    private static bool _leanWalk = true;
    private static delegate* unmanaged<IntPtr, int, int, IntPtr> _getFirstItem, _getNextItem;
    private static delegate* unmanaged<float*, void> _riSetModel;
    /// <summary>DrawBucketManager lives at Scene+0x208 (Scene::RenderDrawBucket decomp).</summary>
    private const int SceneDrawBucketMgr = 0x208;
    /// <summary>PartTriMesh+0x88 -> part object; +0x18 = row-major world Matrix4x4 (translation floats [12..14]),
    /// +0x80 flag byte (&amp;0x1e) and *(+0x10)+0x50 together select the matrix path vs pre-transformed verts.</summary>
    private const int PartObj = 0x88, PartObjMatrix = 0x18, PartObjFlags = 0x80;
    /// <summary>Bucket-item vtable byte offsets (Scene::RenderDrawBucket decomp): +0x60 GetPart, +0xd0 Render.</summary>
    private const int VtGetPart = 0x60, VtRender = 0xd0;
    /// <summary>PartTriMesh world-space bbox min/max (float3 @+0x64/+0x70) -- the cull key for pre-transformed
    /// (skinned) parts, whose model matrix is identity.</summary>
    private const int PartBboxMin = 0x64, PartBboxMax = 0x70;
    private static bool _inCubePass;                  // true ONLY inside CubePass (not the sun maps) -> gates the cull hooks
    private static bool _skipPart;                    // set by OnSetModelMatrix, read by the very next OnRenderVertexData
    private static bool _curBucketStatic;             // true for STATIC tile buckets {0,1}. Statics get a GENEROUS cull radius (far + LocalStaticFudge) because their parts batch under a tile-corner origin offset from the geometry; the doubled far (range mult) makes this safe -> big buildings kept, but the sun's radius-80 BSP re-walk casters (thousands, sized for the sun not a torch) are culled. Dynamics use the tight fudge (origin ~ geometry).
    private static bool _staticCull = true;           // SOWX_LOCALSTATICCULL=0 restores the old static exemption (draw ALL static casters) if the generous cull ever leaks light.
    private static float _staticFudge = 12f;          // SOWX_LOCALSTATICFUDGE -- static cull slack (~a tile diagonal) so a building whose origin sits off its near wall is still kept; raise if the generous cull ever leaks light
    private static Vector3 _curLightPos;              // current cube light centre + per-bucket range^2 for the sphere cull
    private static float _curRangeSqDyn, _curRangeSqStatic;
    private static float _ceilCut = 4f;               // SOWX_LOCALCEIL: skip casters above lightZ + this. Geometry above the light only occludes UPWARD rays -> shadows land on above-light surfaces the top-down camera never sees. Kills complex-ceiling caster cost. <=0 disables.
    private static bool _skipUpFace = true;           // SOWX_LOCALUPFACE=1 restores it. The +Z (up) cube face only serves fragments above the light (ceiling territory) -> skip its caster draws, 1/6 off every submission.
    private static bool _localOnce = true;            // SOWX_LOCALONCE=0 disables. The cubes are CAMERA-INDEPENDENT, so if RenderShadows fires >1x/frame (reflection subframes) only the first needs the cube pass; the rest are pure waste.
    private static bool _cubeDoneThisFrame;           // gates CubePass to the first RenderShadows fire per presented frame (reset in Draw)
    private static IntPtr _lastCubeScene;             // scene ptr of the previous cube pass -- flip counter diagnoses subframe races
    private static int _cubeSceneFlips;               // periodic diag: how often the cube pass's scene changed between frames
    private static int _cullStaticDrawn, _cullDynDrawn, _cullSkipped, _cullHeightSkipped, _cullFaceSkipped, _cullBlendSkipped; // per-pass diagnostic counters (SOWX_SHADOWDIAG); alloc-free
    // Per-FACE cull: a caster inside the light sphere lands on ~1-2 of the 6 faces, so also test each part
    // against the current face's 90deg frustum (45deg planes; slack = the bucket's cull fudge as a bounding-
    // radius proxy, x1.5 to cover the plane's sqrt2). SOWX_LOCALFACECULL=0 disables (A/B).
    private static bool _faceCull = true;
    private const float FaceSlackMult = 1.5f;
    private static int _curFace;                      // cube face being rendered (0..5 = +X,-X,+Y,-Y,+Z,-Z), read by the cull hook
    private static long _cubeTicks;                   // Stopwatch ticks spent in the last CubePass (perf attribution)
    private static int _cubeFrameTick;                // frames since the last periodic perf log
    private static int[] _localBuckets = { 0, 1, 2, 3, 4 }; // SOWX_LOCALBUCKETS: static tiles {0,1} (walls cast on the floor) + dynamics {2,3,4}; affordable only with the cull
    private static uint _cubeFbo;                     // shared FBO, re-attached per face
    // ALL cube faces live in TWO depth TEXTURE_2D_ARRAYs (6 layers per slot, GL cube face order), sampled with
    // manual face math in the shader. One array = ONE texture unit for any slot count. 16 separate samplerCubes
    // on units 9..24 clobbered units the ENGINE caches bindings on (it skips "redundant" rebinds it doesn't know
    // we invalidated) -> its samplers read our white depth maps -> the whole scene went textureless/unlit.
    private static uint _cubeArrTex;        // WORKING array (receiver samples this)
    private static uint _cubeStaticArrTex;  // STATIC-layer array (copied into the working layers each frame)
    private static bool _haveStaticBuckets, _haveDynBuckets;  // does _localBuckets contain {0,1} / {2,3,4}? (computed once)
    private static bool _copyOk;                      // glCopyImageSubData resolved; false -> statics re-render every frame (old behavior)
    private static bool _cubesReady, _cubesFailed;
    private static uint _cubeBlitProg, _cubeBlitVao;
    private static int _uCube, _uCubeSlot;
    private static bool _cubePassLogged;
    private static bool _gobsOn = true;   // SOWX_SHADOWNOGOBS=1 disables (tile casters only)

    // One map centre per presented frame, shared by the re-walk and the depth pass so they align (and so every
    // RenderShadows sub-frame accumulates into the SAME light-space map). Player-anchored when the controlled
    // creature is reachable, else the camera-ground focus -- but ONE value per frame either way. Was the bug:
    // the light transform used the camera fallback while the re-walk required the player and silently no-op'd.
    // Wide-sun-map cache: a directional light-space depth map of STATIC geometry is a pure function of
    // (geometry, sun dir, focus window) -- its contents do not change as the player walks until the window must
    // move. So the wide map re-renders only when the focus drifts > _wideRefreshDist from the rendered centre or
    // the sun direction rotates; otherwise the kept depth map + _lastLightVP stay valid. The window (radius 80)
    // comfortably covers the visible ground (<~50u from the player), so a 10u drift trigger leaves margin.
    private static bool _wideValid;
    private static Vector3 _wideFocus, _wideSunDir;   // centre + sun dir the kept wide map was rendered with
    private static float _wideRefreshDist = 10f;      // SOWX_SHADOWWIDEREFRESH -- world-u focus drift that forces a wide re-render
    private const float WideSunDot = 0.9998f;         // sun rotated past ~1deg -> re-render
    private static bool _wideRefreshThisFrame, _wideDecided;  // decided at ManageSceneBSP (the buckets must carry the r80 re-walk set that frame), consumed by DepthPass
    private static float _cubeWalkRadius = 40f;       // SOWX_SHADOWCUBEWALK -- BSP re-walk radius on non-wide frames (enough for torch-cube statics; the full _radius walk runs only on wide-refresh frames, so the camera pass stops paying ~2.3k extra part submits every frame)
    // Interior areas never need the sun maps (no directional shadows indoors): skip the wide+near sun passes
    // and the r=_radius re-walk there, and upload sowxShadowEnable=0 so the receiver's sun path is off (stale
    // maps from a previous exterior can't leak). The point-light cubes are independent and keep running.
    // Verified via Ghidra: current area = *(CClientExoApp::GetModule(*g_pAppManager) + 0xf8) (the engine's own
    // HandleServerToPlayerArea chain); CNWCArea+8 = the ARE Flags DWORD (CNWSArea::PackAreaIntoMessage writes
    // m_nFlags@+8 -> CNWCArea::LoadArea reads it into +8), bit 0 = interior.
    private static bool _interior;                    // current area is interior (recomputed once per frame)
    private const int ModuleCurrentArea = 0xf8;       // CNWCModule: current CNWCArea*
    private const int AreaFlags = 0x8;                // CNWCArea: ARE Flags DWORD; bit 0 = interior
    private static IntPtr _appMgr;                    // &g_pAppManager (deref per use)
    private static delegate* unmanaged<IntPtr, IntPtr> _getModule;
    private static bool _interiorLogged;              // log interior/exterior transitions
    private static bool _allFires;                    // SOWX_SHADOWALLFIRES=1 restores the depth pass on every RenderShadows fire (old accumulate behavior); default = first fire only (the maps are camera-independent, so the rest was pure waste)
    private static bool _passDoneThisFrame;
    private static long _depthTicks; private static int _depthFrames, _wideRenders;  // periodic perf attribution
    private static bool _haveFocus;
    private static Vector3 _focus;
    private static Vector3? _lastPlayerPos;   // sticky PC anchor: reused when a frame's shadow gob is momentarily null
    private static int _focusSource = -1;     // 0=player-gob, 1=sticky-last, 2=camera-ground; log on transition
    private static int _focusFrames;          // frames held on the current source (reported at the next transition)
    private static bool _depthCleared;   // NEAR depth FBO cleared once per frame; sub-frames accumulate (last-writer-proof)
    private static bool _wideCleared;    // WIDE depth FBO cleared once per frame -- and only on wide-refresh frames (the kept map must survive off-frames)

    // Two-sided depth pass: casters are drawn from the LIGHT POV, where the engine's per-material back-face
    // cull (calibrated for the camera) can drop light-facing faces -> hollow (gappy) shadow footprints. Cap 3
    // = GL_CULL_FACE (verified: GLRender::Enable(3) -> glEnable(0xb44)). We disable it via the engine setter
    // (cache stays in sync) and swallow RenderInterface::Enable(3) while InDepthPass so RenderDrawBucket can't
    // re-enable it mid-pass; restored after. SOWX_SHADOWCULL=1 keeps the engine's culling (debug).
    private const int CullFaceCap = 3;
    /// <summary>Aurora::AuroraEnableEnums value for GL_BLEND (Ghidra: GLRender::Enable case 0 -> glEnable(0xBE2)).</summary>
    private const int BlendCap = 0;
    private static delegate* unmanaged<int, void> _origRiEnable, _riDisable;
    private static delegate* unmanaged<int, byte> _riIsEnabled;
    private static bool _cullWas, _twoSided = true;

    // Transform setters, to point the engine's own MVP at the light and restore it via the matrix stack.
    private static delegate* unmanaged<int, void> _setMatrixMode;
    private static delegate* unmanaged<void> _loadIdentity, _pushMatrix, _popMatrix, _flagDirty;
    private static delegate* unmanaged<float*, void> _multMatrix;
    private static int* _curMatrix;                     // &GLRender::m_nCurrentMatrix (save/restore the mode)
    private static delegate* unmanaged<IntPtr, IntPtr, byte> _keyholeOrig;

    // Receiver plumbing (slice 4): discover scene shader programs via LoadShader, then each frame bind the
    // shadow map (unit 7) + upload lightVP/enable/sampler so the engine's own lit shaders sample it.
    private const uint ShadowUnit = 7;                    // texUnit0..6 are the engine's; 7 is free
    private const int MaxRecv = 256;
    private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte, IntPtr, void> _origLoadShader;
    private static readonly int[] _recvProg = new int[MaxRecv];
    private static readonly int[] _recvLightVPLoc = new int[MaxRecv];
    private static readonly int[] _recvEnableLoc = new int[MaxRecv];
    private static readonly int[] _recvSamplerLoc = new int[MaxRecv];
    private static readonly int[] _recvTexelLoc = new int[MaxRecv];
    private static readonly int[] _recvBiasLoc = new int[MaxRecv];
    private static readonly bool[] _recvResolved = new bool[MaxRecv];
    private static readonly bool[] _recvIsReceiver = new bool[MaxRecv];
    // Point-light cube receiver uniforms (slice 6C): sowxLocalCount + pos[Max] + far[Max] + near + bias, and Max
    // samplerCube on units 9..12. Old (sun-only) shaders lack these -> Loc -1 -> skipped, the established pattern.
    /// <summary>The ONE unit the cube 2D-array binds on (sun uses 7,8). Units past ~12 belong to the engine's
    /// binding cache -- squatting them with per-slot cubes broke its redundant-rebind elision and wrecked scene
    /// texturing, hence the single-array design.</summary>
    private const uint LocalUnit0 = 9;
    /// <summary>Second unit for the SAME depth array with a RAW (no-compare, NEAREST) sampler object --
    /// the PCSS blocker probe needs actual stored depth while unit 9's sampler does hardware compare.</summary>
    private const uint LocalUnit1 = 10;
    /// <summary>HI-tier array pair (shadow LOD): compare on 11, raw on 12. When the hi tier is off, the LO
    /// arrays bind here too so the declared samplers are never left on a mismatched unit.</summary>
    private const uint LocalUnit2 = 11;
    private const uint LocalUnit3 = 12;
    private static readonly int[] _recvLocalCountLoc = new int[MaxRecv];
    private static readonly int[] _recvLocalPosLoc = new int[MaxRecv];
    private static readonly int[] _recvLocalMatchLoc = new int[MaxRecv];
    private static readonly int[] _recvLocalCubesRawLoc = new int[MaxRecv];   // raw (no-compare) sampler unit for the PCSS blocker probe   // sowxLocalMatchPos: RAW per-frame light pos (match key), decoupled from the smoothed bake pos
    private static readonly int[] _recvLocalFarLoc = new int[MaxRecv];
    private static readonly int[] _recvLocalNearLoc = new int[MaxRecv];
    private static readonly int[] _recvLocalBiasLoc = new int[MaxRecv];
    private static readonly int[] _recvLocalCubesLoc = new int[MaxRecv];  // the ONE sampler2DArray holding all cube faces
    private static readonly int[] _recvLocalHiLoc = new int[MaxRecv];         // sowxLocalHi[j] = hi layer + 1 (0 = lo tier)
    private static readonly int[] _recvLocalCubesHiLoc = new int[MaxRecv];    // hi-tier compare sampler (unit 11)
    private static readonly int[] _recvLocalCubesHiRawLoc = new int[MaxRecv]; // hi-tier raw sampler (unit 12)
    private static readonly int[] _recvLocalUvScaleLoc = new int[MaxRecv];    // sowxLocalUvScale[j] = res / arrayFaceSize
    private static readonly int[] _recvLocalTexelLoc = new int[MaxRecv];      // sowxLocalTexel[j] = angular texel (kernel/bias unit)
    /// <summary>Version-skew guards: sub-rect rendering / hi-tier promotion are only allowed once a live
    /// receiver program proves it has the matching uniforms -- an older hak shader sampling a corner-scaled
    /// (or hi-array) map full-face renders giant nonsense shadows.</summary>
    private static bool _recvHasUvScale, _recvHasHi;
    private static int _recvCount, _recvActive;
    private static bool _recvTranspose, _recvLogged;
    private static int _enableValue = 1;   // 1 = real shadows; 2 = debug tint (SOWX_SHADOWDEBUG=1)
    private static System.Numerics.Matrix4x4 _lastLightVP;

    private const int MatProjection = 0, MatView = 1;   // AuroraTransStates (verified from Camera::RenderScene)
    private const int MatWorld = 2;                     // GLRender::SetModelMatrix writes aurMatrixStack[2] (Ghidra: SetMatrix target 0x1ae3010 = stack obj 2)
    private static float _radius = 80f;                 // ortho half-extent around the camera focus (SOWX_SHADOWRADIUS)
    // Shadow bias in WORLD units (SOWX_SHADOWWORLDBIAS). Uploaded to the shader as worldBias / lightDepthRange so
    // it stays constant in world units as the radius changes -- a fixed clip bias grows with radius and swallows
    // short (character) shadows at large radius. Depth range = 3*radius (near 0, far 2*radius+upstreamMargin,
    // upstreamMargin == radius; see ShadowMath.DirectionalLightViewProj).
    private static float _worldBias = 0.1f;
    private static bool _transpose;                     // SOWX_SHADOWTRANSPOSE: flip lightVP row/col if wrong
    private static int _savedMatrixMode;                // m_nCurrentMatrix at pass entry, restored at exit
    private static bool _passLogged, _lightLogged, _fallbackLogged;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // Default ON: SOWX_SHADOWS=0 is the one kill switch for the whole replacement pipeline (this,
        // ShadowCaptureService, and ShadowService's vanilla suppression). SOWX_SHADOWDIAG=1 now only
        // adds the periodic perf/diagnostic logging.
        _enabled = Environment.GetEnvironmentVariable("SOWX_SHADOWS") != "0";
        _diag = Environment.GetEnvironmentVariable("SOWX_SHADOWDIAG") == "1";
        if (!_enabled) return;
        _wideBuckets = ParseBuckets(Environment.GetEnvironmentVariable("SOWX_SHADOWBUCKETS")) ?? _wideBuckets;
        _nearBuckets = ParseBuckets(Environment.GetEnvironmentVariable("SOWX_SHADOWNEARBUCKETS")) ?? _nearBuckets;

        _origRenderShadows = (delegate* unmanaged<IntPtr, int, byte, void>)hooks.Install(
            SymbolMap.RenderShadows, (IntPtr)(delegate* unmanaged<IntPtr, int, byte, void>)&OnRenderShadows);
        _renderDrawBucket = (delegate* unmanaged<IntPtr, int, void>)Native.Sym(SymbolMap.RenderDrawBucket);
        _setMatrixMode = (delegate* unmanaged<int, void>)Native.Sym(SymbolMap.SetMatrixMode);
        _loadIdentity = (delegate* unmanaged<void>)Native.Sym(SymbolMap.LoadMatrixIdentity);
        _multMatrix = (delegate* unmanaged<float*, void>)Native.Sym(SymbolMap.MultMatrix);
        _pushMatrix = (delegate* unmanaged<void>)Native.Sym(SymbolMap.PushCurrentMatrix);
        _popMatrix = (delegate* unmanaged<void>)Native.Sym(SymbolMap.PopCurrentMatrix);
        _flagDirty = (delegate* unmanaged<void>)Native.Sym(SymbolMap.FlagCurrentTransformDirty);
        _curMatrix = (int*)Native.Sym(SymbolMap.MCurrentMatrix);

        _transpose = Environment.GetEnvironmentVariable("SOWX_SHADOWTRANSPOSE") == "1";
        _recvTranspose = Environment.GetEnvironmentVariable("SOWX_SHADOWRECVT") == "1";
        // 2 = sun green/red debug tint; 3 = local-cube slot-color occlusion overlay (SOWX_SHADOWDEBUG=2)
        string? dbg = Environment.GetEnvironmentVariable("SOWX_SHADOWDEBUG");
        // 1 -> sun tint (2); 2 -> local band overlay (3); 3 -> exact-factor grayscale (4); 4 -> radius heatmap (5)
        _enableValue = dbg == "1" ? 2 : dbg == "2" ? 3 : dbg == "3" ? 4 : dbg == "4" ? 5 : 1;
        // A/B isolator: sun/moon RECEIVER sampling off (sowxShadowEnable=0), point-light cubes untouched --
        // separates which of the two shadow paths an artifact comes from without rebuilding anything.
        if (Environment.GetEnvironmentVariable("SOWX_NOSUNRECV") == "1") _enableValue = 0;
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_SHADOWRADIUS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) && r > 1f) _radius = r;
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_SHADOWNEARRADIUS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float nr) && nr > 1f) _nearRadius = nr;
        if (int.TryParse(Environment.GetEnvironmentVariable("SOWX_SHADOWMAPSIZE"), out int ms) && ms >= 256 && ms <= 8192) _mapSize = ms;
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_SHADOWFOCUSDIST"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fd) && fd >= 0f) _focusDist = fd;
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_SHADOWWORLDBIAS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float wb) && wb >= 0f) _worldBias = wb;

        // Point-light cubes (slice 6).
        _localOn = Environment.GetEnvironmentVariable("SOWX_NOLOCAL") != "1";
        _localBuckets = ParseBuckets(Environment.GetEnvironmentVariable("SOWX_LOCALBUCKETS")) ?? _localBuckets;
        if (int.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALCUBESIZE"), out int cs) && cs >= 128 && cs <= 4096) _cubeSize = cs;
        if (int.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALHISLOTS"), out int hs) && hs >= 0 && hs <= 16) _hiSlots = hs;
        if (int.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALHISIZE"), out int hsz) && hsz >= 256 && hsz <= 4096) _cubeSizeHi = hsz;
        if (_cubeSizeHi <= _cubeSize) _hiSlots = 0;   // hi tier only makes sense above the base res
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALLODK"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lk) && lk > 0f) _lodK = lk;
        if (int.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALLODMIN"), out int lm) && lm >= 2 && lm <= 4096) _lodMin = lm;
        if (int.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALPRIRES"), out int pr) && pr >= 64 && pr <= 4096) _priRes = pr;
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALNEAR"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ln) && ln >= 0.1f) _localNear = ln;
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALBIAS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lb) && lb >= 0f) _localBias = lb;
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALSMOOTH"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float st) && st >= 0f) _smoothTau = MathF.Min(st, 0.5f);
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALRANGEMULT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rm) && rm >= 1f) _rangeMult = rm;
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALCEIL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cc)) _ceilCut = cc > 0f ? cc : float.PositiveInfinity;
        _skipUpFace = Environment.GetEnvironmentVariable("SOWX_LOCALUPFACE") != "1";
        _localOnce = Environment.GetEnvironmentVariable("SOWX_LOCALONCE") != "0";
        _staticCull = Environment.GetEnvironmentVariable("SOWX_LOCALSTATICCULL") != "0";
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALSTATICFUDGE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sf) && sf >= 0f) _staticFudge = sf;
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALUPDATEEPS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ue) && ue >= 0f) _updateEps = MathF.Min(ue, 0.45f);   // keep < receiver match tol
        _faceCull = Environment.GetEnvironmentVariable("SOWX_LOCALFACECULL") != "0";
        _allFires = Environment.GetEnvironmentVariable("SOWX_SHADOWALLFIRES") == "1";
        _appMgr = Native.Sym(SymbolMap.AppManager);
        _getModule = (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetModule);
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_SHADOWWIDEREFRESH"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float wr) && wr >= 0f) _wideRefreshDist = wr;
        if (float.TryParse(Environment.GetEnvironmentVariable("SOWX_SHADOWCUBEWALK"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cw) && cw >= 1f) _cubeWalkRadius = cw;
        foreach (int b in _localBuckets) { if (b == 0 || b == 1) _haveStaticBuckets = true; else _haveDynBuckets = true; }
        if (int.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALCUBE"), out int ldc) && ldc >= 0 && ldc < ShadowInputs.MaxLocalLights) _localDebugCube = ldc;
        if (int.TryParse(Environment.GetEnvironmentVariable("SOWX_LOCALSTATICCD"), out int scd) && scd >= 1) _staticCooldown = scd;

        // Per-light caster cull: hook the bucket loop's SetModelMatrix (read pos) + RenderVertexData (skip draw).
        // Only when the cube feature is armed -- these fire for EVERY draw in the game, so don't install idle.
        _cullOn = Environment.GetEnvironmentVariable("SOWX_LOCALCULL") != "0";
        _leanWalk = Environment.GetEnvironmentVariable("SOWX_LEANWALK") != "0";
        _getFirstItem = (delegate* unmanaged<IntPtr, int, int, IntPtr>)Native.Sym(SymbolMap.BucketGetFirstItem);
        _getNextItem = (delegate* unmanaged<IntPtr, int, int, IntPtr>)Native.Sym(SymbolMap.BucketGetNextItem);
        _riSetModel = (delegate* unmanaged<float*, void>)Native.Sym(SymbolMap.RiSetModelMatrix);
        if (_leanWalk && (_getFirstItem == null || _getNextItem == null || _riSetModel == null
            || _setMatrixMode == null || _pushMatrix == null || _popMatrix == null || _curMatrix == null))
        { _leanWalk = false; _log?.Info("ShadowMapService: lean-walk symbols missing -- falling back to hook path"); }
        if (_localOn && _cullOn && !_leanWalk)
        {
            _origSetModel = (delegate* unmanaged<IntPtr, void>)hooks.Install(
                SymbolMap.SetModelMatrix, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnSetModelMatrix);
            _origRVD = (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)hooks.Install(
                SymbolMap.RenderVertexData, (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)&OnRenderVertexData);
        }

        // Area-wide light enumeration: capture LightManager inside GetShadowLights (this is alive there) and
        // walk its MASTER list -- deterministic cube ownership instead of the engine's camera-prioritized 4.
        // SOWX_LOCALENUM=0 bisect knob: skip the hook, fall back to the DoGobShadows recording (engine's pick).
        if (_localOn && Environment.GetEnvironmentVariable("SOWX_LOCALENUM") != "0")
            _origGetShadowLights = (delegate* unmanaged<IntPtr, int, IntPtr>)hooks.Install(
                SymbolMap.LmGetShadowLights, (IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr>)&OnGetShadowLights);

        // Discover scene shader programs so the per-frame loop can feed them the map + lightVP.
        _origLoadShader = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte, IntPtr, void>)hooks.Install(
            SymbolMap.LoadShader, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte, IntPtr, void>)&OnLoadShader);

        // Casters must not keyhole-dissolve in the shadow map -> force GetCanKeyholeDissolve=0 while InDepthPass.
        _keyholeOrig = (delegate* unmanaged<IntPtr, IntPtr, byte>)hooks.Install(
            SymbolMap.GetCanKeyholeDissolve, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte>)&OnGetCanKeyholeDissolve);

        // Caster completeness (slice 3c): hook ManageSceneBSP (call-through) to re-walk the BSP sphere and
        // re-submit off-screen casters; resolve the submit + traversal + opacity primitives.
        _rewalkOn = Environment.GetEnvironmentVariable("SOWX_SHADOWNOREWALK") != "1";
        _origManageSceneBSP = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.ManageSceneBSP, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnManageSceneBSP);
        _addPartToMeshBuckets = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.AddPartToMeshBuckets);
        _bspTraverseSphere = (delegate* unmanaged<IntPtr, Vector3*, float, IntPtr, void>)Native.Sym(SymbolMap.BSPTraverseSphere);
        _partGetIsOpaque = (delegate* unmanaged<IntPtr, byte>)Native.Sym(SymbolMap.PartGetIsOpaque);

        // Dynamic gob casters (slice 3d): the global gobbucket + its Add, appended from the same hook.
        _gobsOn = Environment.GetEnvironmentVariable("SOWX_SHADOWNOGOBS") != "1";
        _gobBucket = Native.Sym(SymbolMap.GobBucket);
        _gobBucketAdd = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.GobBucketAdd);

        // Camera-follow anchor: the real PC position, camera- and shadow-path-independent (Scene+0x58 reads null
        // under SOWX_NOSHADOWS). CurrentCamera is a global pointer slot -- store its address, deref per frame.
        _currentCamera = Native.Sym(SymbolMap.CurrentCamera);

        // Two-sided depth: swallow Enable(GL_CULL_FACE) while InDepthPass so light-facing faces aren't culled.
        _twoSided = Environment.GetEnvironmentVariable("SOWX_SHADOWCULL") != "1";
        _origRiEnable = (delegate* unmanaged<int, void>)hooks.Install(
            SymbolMap.RiEnable, (IntPtr)(delegate* unmanaged<int, void>)&OnRiEnable);
        _riDisable = (delegate* unmanaged<int, void>)Native.Sym(SymbolMap.RiDisable);
        _riIsEnabled = (delegate* unmanaged<int, byte>)Native.Sym(SymbolMap.RiIsEnabled);

        // Passive debug window: no input, just a per-frame draw slot in the UiHost roll-call (the blit).
        _ = new Window(0, 0, 0, 0) { Focusable = false, DrawContent = Draw };
        log.Info($"ShadowMapService: shadow pipeline armed (SOWX_SHADOWS=0 disables) " +
                 $"(hook={( _origRenderShadows != null ? "ok" : "MISS")}, drawBucket={(_renderDrawBucket != null ? "ok" : "MISS")}, " +
                 $"wide=[{string.Join(",", _wideBuckets)}]@r{_radius:F0}, near=[{string.Join(",", _nearBuckets)}]@r{_nearRadius:F0}, " +
                 $"rewalk={(_rewalkOn ? (_origManageSceneBSP != null && _bspTraverseSphere != null && _addPartToMeshBuckets != null ? "ok" : "MISS") : "off")}, " +
                 $"gobs={(_gobsOn ? (_gobBucket != IntPtr.Zero && _gobBucketAdd != null ? "ok" : "MISS") : "off")})");
        // Every SOWX_* env var actually set: the locale fix made dot-decimal floats parse for the first time,
        // so stale experiment vars in a launch script can silently reshape the maps -- surface them all.
        var sb = new System.Text.StringBuilder();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && k.StartsWith("SOWX_", StringComparison.Ordinal)) sb.Append($" {k}={e.Value}");
        log.Info(sb.Length > 0 ? $"ShadowMapService: env overrides:{sb}" : "ShadowMapService: env overrides: none");
        log.Info($"ShadowMapService: effective config: mapSize={_mapSize} radius={_radius:F0} nearRadius={_nearRadius:F0} " +
                 $"cubeSize={_cubeSize} localNear={_localNear:F2} localBias={_localBias:F2} rangeMult={_rangeMult:F1} smoothTau={_smoothTau:F2}");
    }

    /// <summary>Parse a comma list like "1,2,3,4" into distinct positive bucket ids; null on empty/garbage
    /// (caller keeps the default). Init-time only.</summary>
    private static int[]? ParseBuckets(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var ids = new List<int>();
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out int id) && id >= 0 && id <= 8 && !ids.Contains(id)) ids.Add(id);
        return ids.Count > 0 ? ids.ToArray() : null;
    }

    /// <summary>RenderShadows frame slot: run vanilla shadows, capture <c>Scene*</c>, then render our caster
    /// depth pass. Call-through so the vanilla frame is untouched (our pass is additive into our own FBO).</summary>
    [UnmanagedCallersOnly]
    private static void OnRenderShadows(IntPtr scene, int mode, byte clear)
    {
        if (_origRenderShadows != null) _origRenderShadows(scene, mode, clear);
        if (!_enabled || _glFailed || !UiHost.InModule || scene == IntPtr.Zero || _renderDrawBucket == null) return;
        _shadowFires++;   // how many times RenderShadows fires per presented frame (>1 => reflection subframes)
        // The maps are all light-space (camera-independent) and every scene populate carries the full re-walk
        // set, so the FIRST fire already renders them complete -- reflection subframes would just redraw the
        // same maps. SOWX_SHADOWALLFIRES=1 restores the old every-fire accumulate.
        if (!_allFires && _passDoneThisFrame) return;
        DepthPass(scene);
        _passDoneThisFrame = true;
    }

    /// <summary>Suppress the camera keyhole-dissolve while rendering the shadow map (a caster clipped out by the
    /// camera aperture would punch a hole in its own shadow); pass through for the real camera frame.</summary>
    [UnmanagedCallersOnly]
    private static byte OnGetCanKeyholeDissolve(IntPtr camera, IntPtr mesh)
    {
        if (ShadowInputs.InDepthPass) return 0;

        return _keyholeOrig != null ? _keyholeOrig(camera, mesh) : (byte)0;
    }

    /// <summary>Swallow Enable(GL_CULL_FACE) while the shadow depth pass is running so the caster draw is
    /// two-sided (light-facing faces aren't culled -> no hollow footprints). Pure pass-through otherwise --
    /// this is a very hot function, so the guard is one branch and nothing else.</summary>
    [UnmanagedCallersOnly]
    private static void OnRiEnable(int cap)
    {
        if (_twoSided && cap == CullFaceCap && ShadowInputs.InDepthPass) return;
        if (_origRiEnable != null) _origRiEnable(cap);
    }

    /// <summary>Cube-pass caster cull, read side: the bucket loop calls this per part before its draw. While in the
    /// cube pass, read the part's world translation (row-major model matrix floats [12,13,14]) and sphere-test it
    /// against the current light's range -> <see cref="_skipPart"/>. Always calls through (the matrix must still be
    /// set for parts we keep). One-branch hot when not in the cube pass. Static ABI: rdi = raw Matrix4x4*.</summary>
    [UnmanagedCallersOnly]
    private static void OnSetModelMatrix(IntPtr m)
    {
        if (_inCubePass)
            // refresh the verdict so a matrix-less draw can't inherit the previous part's skip
            _skipPart = m != IntPtr.Zero && CullAt(((float*)m)[12], ((float*)m)[13], ((float*)m)[14]);
        if (_origSetModel != null) _origSetModel(m);
    }

    /// <summary>Sphere + height + per-face cull of one caster's world position against the current cube light
    /// (<see cref="_curLightPos"/> / ranges / <see cref="_curFace"/>). Shared by the legacy hook path and the lean
    /// walker. Height cull applies to ALL buckets: a caster above the light only shadows upward. Statics use the
    /// generous radius (SOWX_LOCALSTATICCULL kill-switch honoured); dynamics the tight one.</summary>
    private static bool CullAt(float wx, float wy, float wz)
    {
        bool tooHigh = wz > _curLightPos.Z + _ceilCut;
        float dx = wx - _curLightPos.X, dy = wy - _curLightPos.Y, dz = wz - _curLightPos.Z;
        float distSq = dx * dx + dy * dy + dz * dz;
        bool outOfRange = _curBucketStatic
            ? (_staticCull && distSq > _curRangeSqStatic)
            : distSq > _curRangeSqDyn;
        bool skip = tooHigh || outOfRange;
        if (tooHigh) _cullHeightSkipped++;
        // Per-face frustum cull: keep only parts that can land on the CURRENT face (45deg planes: axis
        // component + slack >= the other two |components|). Slack = the bucket's cull fudge (bounding-
        // radius proxy) x1.5 (plane sqrt2). Cuts an in-sphere caster from 5-6 faces down to its 1-2.
        if (!skip && _faceCull)
        {
            float ax, o1, o2;
            switch (_curFace)
            {
                case 0: ax = dx; o1 = dy; o2 = dz; break;
                case 1: ax = -dx; o1 = dy; o2 = dz; break;
                case 2: ax = dy; o1 = dx; o2 = dz; break;
                case 3: ax = -dy; o1 = dx; o2 = dz; break;
                case 4: ax = dz; o1 = dx; o2 = dy; break;
                default: ax = -dz; o1 = dx; o2 = dy; break;
            }
            float slack = (_curBucketStatic ? _staticFudge : LocalCullFudge) * FaceSlackMult;
            if (ax + slack < MathF.Abs(o1) || ax + slack < MathF.Abs(o2))
            { skip = true; _cullFaceSkipped++; }
        }
        return skip;
    }

    /// <summary>Cube-pass caster cull, skip side: the terminal per-part draw. Drop it when the cube pass flagged the
    /// part out of the current light's range; otherwise pass through unchanged. First-branch hot -- fires for every
    /// draw in the game, so it is one bool test off the cube path.</summary>
    [UnmanagedCallersOnly]
    private static void OnRenderVertexData(IntPtr vdo, uint a, uint prim, uint cnt, uint e)
    {
        if (_inCubePass)
        {
            if (_skipPart) { _cullSkipped++; return; }   // range OR height cull -> drop the draw (both bucket kinds)
            // Transparent parts (blend on: Aurora enable cap 0 = GL_BLEND, Ghidra GLRender::Enable) must not
            // write shadow depth -- depth writes ignore alpha, so a torch's own flame/glow billboards baked
            // giant polygon-outline shadows onto the walls ("black bands" from powerful/moving lights).
            if (_riIsEnabled != null && _riIsEnabled(BlendCap) != 0) { _cullBlendSkipped++; return; }
            if (_curBucketStatic) _cullStaticDrawn++; else _cullDynDrawn++;
        }
        if (_origRVD != null) _origRVD(vdo, a, prim, cnt, e);
    }

    // LightManager layout (Ghidra: AddLight / GetShadowLights / SetupLightsForThisPositionAndRadius): the MASTER
    // CExoArrayList<PartLight*> sits at this+0x10 (data) / +0x18 (count) -- every registered light in the loaded
    // area, creation order (stable). PartLight fields: castShadows +0x74, type +0x80 (0 = point/directional),
    // enable flags +0x84/+0x88 (either set = active), range +0x70 (+0x78 preferred when > 0), intensity +0xa4,
    // RGB +0x64, world pos +0xac. Directional (sun/moon) excluded by range, like ShadowService.
    private const int LmMasterData = 0x10, LmMasterCount = 0x18;
    private const int PlCastShadows = 0x74, PlType = 0x80, PlFlagA = 0x84, PlFlagB = 0x88;
    private const int PlRange = 0x70, PlRangeAlt = 0x78, PlIntensity = 0xa4, PlColor = 0x64, PlPos = 0xac;
    private const float PlDirectionalRange = 1000f;
    private static delegate* unmanaged<IntPtr, int, IntPtr> _origGetShadowLights;

    /// <summary><c>LightManager::GetShadowLights(int)</c> call-through: while the manager is guaranteed alive,
    /// record EVERY shadow-worthy point light from its master list into <see cref="ShadowInputs"/> -- the
    /// area-wide, camera-independent cube set (the engine's own pick is prioritized around the camera, which
    /// popped shadows on movement). Alloc-free; pointers are only read inside this call, never retained.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnGetShadowLights(IntPtr self, int mode)
    {
        IntPtr ret = _origGetShadowLights != null ? _origGetShadowLights(self, mode) : IntPtr.Zero;
        if (self == IntPtr.Zero || !UiHost.InModule) return ret;
        int count = *(int*)((byte*)self + LmMasterCount);
        IntPtr data = *(IntPtr*)((byte*)self + LmMasterData);
        if (data == IntPtr.Zero) return ret;
        for (int i = 0; i < count; i++)
        {
            byte* p = (byte*)*((IntPtr*)data + i);
            if (p == null) continue;
            if (*(int*)(p + PlCastShadows) == 0 || *(int*)(p + PlType) != 0) continue;
            if (*(int*)(p + PlFlagA) == 0 && *(int*)(p + PlFlagB) == 0) continue;
            float range = *(float*)(p + PlRange);
            if (range <= 0f || range > PlDirectionalRange) continue;
            if (*(float*)(p + PlIntensity) <= 0f) continue;
            float r = *(float*)(p + PlColor), g = *(float*)(p + PlColor + 4), b = *(float*)(p + PlColor + 8);
            if (r == 0f && g == 0f && b == 0f) continue;
            float alt = *(float*)(p + PlRangeAlt);
            ShadowInputs.RecordLocalLight(
                new Vector3(*(float*)(p + PlPos), *(float*)(p + PlPos + 4), *(float*)(p + PlPos + 8)),
                alt > 0f ? alt : range,
                new Vector3(r, g, b));
        }
        return ret;
    }

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
        if (!_enabled || _glFailed || !UiHost.InModule) return;
        // Only the camera is required: the per-light cube walks must run in sunless interiors too (gating on
        // HasSun made interior torch shadows depend on whether an exterior was ever visited -- "random").
        if (scene == IntPtr.Zero || !ShadowInputs.HasCamera) return;
        // Augment on EVERY scene's populate -- main AND reflection subframes, no scene-identity latch. The depth
        // pass accumulates every RenderShadows fire into one light-space map, so every scene's buckets must carry
        // the sphere set. The engine clears the buckets per populate; extra parts in a reflection render are
        // off-its-frustum -> GPU-clipped, harmless. One shared per-frame focus so the sphere aligns with the map.
        // Tile and gob augmentation are independently gated (SOWX_SHADOWNOREWALK / SOWX_SHADOWNOGOBS) for clean
        // attribution.

        Vector3 center = ShadowFocus();
        // Decide ONCE per presented frame whether the wide sun map re-renders (the buckets must carry the full
        // r=_radius re-walk set on exactly those frames). Off-frames walk only _cubeWalkRadius -- enough for the
        // torch-cube statics -- so the camera pass stops re-submitting thousands of off-screen parts every frame.
        if (!_wideDecided)
        {
            _wideDecided = true;
            bool wasInterior = _interior;
            _interior = AreaIsInterior();
            if (_interior != wasInterior || !_interiorLogged)
            { _interiorLogged = true; _log?.Info($"ShadowMapService: area is {(_interior ? "INTERIOR -- sun maps + wide re-walk off" : "exterior -- sun maps on")}"); }
            if (_interior) _wideValid = false;   // force a fresh wide map on the next exterior frame
            _wideRefreshThisFrame = !_interior && ShadowInputs.HasSun && (!_wideValid
                || (center - _wideFocus).LengthSquared() > _wideRefreshDist * _wideRefreshDist
                || Vector3.Dot(ShadowInputs.SunDir, _wideSunDir) < WideSunDot);
        }
        float walkR = _wideRefreshThisFrame ? _radius : _cubeWalkRadius;
        float radiusSq = walkR * walkR;   // BSPTraverse compares against SQUARED plane distances -- pass r*r

        // Tiles (slice 3c): re-walk the static-geometry BSP sphere and re-submit the tile parts the camera culled.
        _lastWalkR = 0f;
        if (_rewalkOn && _bspTraverseSphere != null && _addPartToMeshBuckets != null)
        {
            IntPtr root = *(IntPtr*)((byte*)scene + SymbolMap.SceneBspRoot);
            if (root != IntPtr.Zero)
            {
                _rewalkParts = 0;
                _bspTraverseSphere(root, &center, radiusSq,
                    (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, float, void>)&OnBspNode);
                _lastWalkR = walkR;
                if (!_rewalkLogged)
                { _rewalkLogged = true; _log?.Info($"ShadowMapService: BSP re-walk submitted {_rewalkParts} caster parts (radius={_radius:F0})"); }

                // Per-light cube walks: submit the caster sphere around each light whose static bake is waiting
                // (CubePass marked it pending because the player walk doesn't cover it). The bake runs later this
                // same frame, on these very buckets. Pending persists until the bake happens, so the walk repeats
                // each populate until then (bucket contents are per-frame).
                int walks = WalksPerPopulate;
                for (int j = 0; j < ShadowInputs.MaxLocalLights; j++) _slotWalked[j] = false;   // tokens are per-populate (bucket contents are)
                for (int j = 0; j < ShadowInputs.MaxLocalLights && walks > 0; j++)
                {
                    if (!_slotWalkPending[j]) continue;
                    Vector3 c = _slotWalkPos[j];
                    _bspTraverseSphere(root, &c, _slotWalkR[j] * _slotWalkR[j],
                        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, float, void>)&OnBspNode);
                    _slotWalked[j] = true;
                    walks--;
                }
            }
        }

        // Gobs (slice 3d): append the off-screen movable objects the engine culled -- but only within the NEAR
        // radius, since dynamics now cast ONLY into the near map; a gob beyond it would be dead weight in the
        // camera pass (drawn, then GPU-clipped) that never reaches any shadow map.
        if (_gobsOn) AppendOffscreenGobs(scene, center, _nearRadius * _nearRadius);
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
        IntPtr list = *(IntPtr*)((byte*)scene + SymbolMap.SceneGobList);
        int count = *(int*)((byte*)scene + SymbolMap.SceneGobCount);
        if (list == IntPtr.Zero || count <= 0) return;

        int added = 0;
        for (int i = 0; i < count; i++)
        {
            IntPtr gob = ((IntPtr*)list)[i];
            if (gob == IntPtr.Zero) continue;
            if (*((byte*)gob + SymbolMap.GobInView) != 0) continue;                       // engine already added the visible ones
            if (*(IntPtr*)((byte*)gob + SymbolMap.GobNode) == IntPtr.Zero) continue;      // sort-comparator would null-deref
            if (*(IntPtr*)((byte*)gob + SymbolMap.GobPartTree) == IntPtr.Zero) continue;  // no part tree -> nothing to cast
            float* p = (float*)((byte*)gob + SymbolMap.GobPosition);
            float dx = p[0] - center.X, dy = p[1] - center.Y, dz = p[2] - center.Z;
            if (dx * dx + dy * dy + dz * dz > radiusSq) continue;
            *((byte*)gob + SymbolMap.GobGeometryDirty) = 1;   // force ProcessGob to (re)compute has-geometry this frame
            _gobBucketAdd(_gobBucket, gob);
            added++;
        }
        _rewalkGobs = added;
        // Log the first frame that actually appends something (the first-frame count is ~always 0 -- nothing is
        // off-screen yet -- so a plain one-shot said nothing about whether the feature ever engages).
        if (!_gobsLogged && added > 0)
        { _gobsLogged = true; _log?.Info($"ShadowMapService: appended {added} off-screen gob casters (of {count} scene gobs, nearRadius={_nearRadius:F0})"); }
    }

    /// <summary>True when any scene gob (the superset behind dynamic buckets 2,3,4) sits within
    /// <paramref name="range"/> of <paramref name="pos"/> -- the CPU pre-test that lets a slot skip its
    /// per-frame dynamic pass + static re-copy entirely. Also emits a position signature + count of the
    /// in-range set, so the caller re-renders the dyn layer only when a gob actually MOVED (cave placeables
    /// are gobs that sit in every light's reach forever -- "any gob near" alone kept 31/32 slots hot).
    /// Same distance the render cull uses, so a skipped slot would not have drawn anyway. Alloc-free.</summary>
    private static bool SlotHasDynCasters(IntPtr scene, Vector3 pos, float range, out float sig, out int cnt)
    {
        sig = 0f; cnt = 0;
        IntPtr list = *(IntPtr*)((byte*)scene + SymbolMap.SceneGobList);
        int count = *(int*)((byte*)scene + SymbolMap.SceneGobCount);
        if (list == IntPtr.Zero || count <= 0) return false;
        float rSq = range * range;
        for (int i = 0; i < count; i++)
        {
            IntPtr gob = ((IntPtr*)list)[i];
            if (gob == IntPtr.Zero) continue;
            float* p = (float*)((byte*)gob + SymbolMap.GobPosition);
            float dx = p[0] - pos.X, dy = p[1] - pos.Y, dz = p[2] - pos.Z;
            if (dx * dx + dy * dy + dz * dz <= rSq)
            {
                // ponytail: position-only signature -- an in-place animation (idle sway, attack swing) does not
                // retrigger the dyn layer; its shadow freezes until the gob moves. Add an anim counter if visible.
                sig += p[0] + p[1] * 17f + p[2] * 289f;
                cnt++;
            }
        }
        return cnt > 0;
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
        IntPtr nodedata = *(IntPtr*)((byte*)node + SymbolMap.BspNodeData);
        if (nodedata == IntPtr.Zero) return;
        // Submit BOTH the parts array AND the single primary, always: the engine uses one or the other depending
        // on currentscene+0x1e0, and submitting only one leaves the caster's other representation out -> a
        // partial (hollow) footprint. Duplicates are idempotent depth writes; the transfer drops junk.
        IntPtr parts = *(IntPtr*)((byte*)nodedata + SymbolMap.NodeDataParts);
        int count = *(int*)((byte*)nodedata + SymbolMap.NodeDataPartCount);
        if (parts != IntPtr.Zero && count > 0)
            for (int i = 0; i < count; i++) SubmitPart(((IntPtr*)parts)[i]);
        SubmitPart(*(IntPtr*)((byte*)nodedata + SymbolMap.NodeDataPrimary));
    }

    /// <summary>Submit one tile part as a caster if it's non-null and opaque. Sets the mesh's renderable-this-
    /// frame flag first (meshdata+0x118) -- else Scene::AddPartsToDrawBuckets' IsPartRenderable filter drops the
    /// part, silently eating every off-screen caster the camera never submitted (the engine's own submit path
    /// sets the same flag). This is what fills the map for casters the camera can't see.</summary>
    private static void SubmitPart(IntPtr part)
    {
        if (part == IntPtr.Zero) return;
        if (_partGetIsOpaque != null && _partGetIsOpaque(part) == 0) return;
        IntPtr meshData = *(IntPtr*)((byte*)part + SymbolMap.PartMeshData);
        if (meshData != IntPtr.Zero && *(int*)((byte*)meshData + SymbolMap.MeshRenderableFlag) == 0)
            *(int*)((byte*)meshData + SymbolMap.MeshRenderableFlag) = 1;
        _addPartToMeshBuckets(part);
        _rewalkParts++;
    }

    /// <summary>Point the engine's MVP at the light: VIEW = identity, PROJECTION = our world-&gt;GL-clip lightVP
    /// (so the engine's own vertex shaders draw casters from the light). Pushes each stack first so
    /// <see cref="RestoreLightTransform"/> pops back the EXACT engine matrices (no captured-param staleness).
    /// Returns false if inputs/entry points aren't ready (caller then falls back to the camera POV).</summary>
    private static bool SetLightTransform(Vector3 focus, float radius, out System.Numerics.Matrix4x4 lightVP)
    {
        lightVP = default;
        if (_setMatrixMode == null || _loadIdentity == null || _multMatrix == null
            || _pushMatrix == null || _popMatrix == null || _curMatrix == null) return false;
        if (!ShadowInputs.HasSun || !ShadowInputs.HasCamera) return false;

        lightVP = ShadowMath.DirectionalLightViewProj(ShadowInputs.SunDir, focus, radius, radius, _mapSize);
        PushLightVP(lightVP);

        if (!_lightLogged)
        {
            _lightLogged = true;
            var d = ShadowInputs.SunDir;
            _log?.Info($"ShadowMapService: light pass (sunDir=({d.X:F2},{d.Y:F2},{d.Z:F2}) wide={_radius:F0} near={_nearRadius:F0})");
        }
        return true;
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
            if (CameraTargetFocus() is Vector3 pp) { _lastPlayerPos = pp; _focus = pp; src = 0; }
            else if (_lastPlayerPos is Vector3 lp) { _focus = lp; src = 1; }
            else { _focus = ShadowMath.CameraGroundFocus(ShadowInputs.CameraPos, ShadowInputs.CameraRot, _focusDist); src = 2; }
            if (src != _focusSource)
            {
                _log?.Info($"ShadowMapService: focus source -> {FocusName(src)} at ({_focus.X:F1},{_focus.Y:F1},{_focus.Z:F1}) " +
                           $"(prev {FocusName(_focusSource)} held {_focusFrames} frames)");
                _focusSource = src; _focusFrames = 0;
            }
            _focusFrames++;
            _haveFocus = true;
            ShadowInputs.Focus = _focus; ShadowInputs.HasFocus = true;   // the nearest-K key for light recording
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
        if (area != _lastAreaPtr)
        {   // area transition: old-area light positions must not linger through the registry hold-down
            _lastAreaPtr = area;
            ShadowInputs.ClearLightRegistry();
        }
        return (*(uint*)((byte*)area + AreaFlags) & 1) != 0;
    }

    private static IntPtr _lastAreaPtr;

    private static string FocusName(int s) => s switch { 0 => "camera-target", 1 => "sticky-last", 2 => "camera-ground", _ => "none" };

    /// <summary>The PC's world position via the camera-follow behavior's target -- camera-INDEPENDENT (the follow
    /// target doesn't move when the camera orbits) and independent of the vanilla shadow path (unlike Scene+0x58,
    /// which reads null under SOWX_NOSHADOWS). Replicates ManageSceneBSP's own per-frame chain: CurrentCamera ->
    /// +0x458 host -> AsBehaviorCameraFollow() (vtable+0x48, pure this-or-null) -> target object (+0x20) -> world
    /// pos (+0xa8). Every deref is guarded exactly as the engine guards it; returns null for a non-follow camera
    /// (free-cam/cutscene) so the caller falls back. Safe after the original ManageSceneBSP ran this frame -- it
    /// walked this same chain, so the pointers and the vcall target are known-valid. Alloc-free.</summary>
    private static Vector3? CameraTargetFocus()
    {
        if (_currentCamera == IntPtr.Zero) return null;
        IntPtr cam = *(IntPtr*)_currentCamera;
        if (cam == IntPtr.Zero) return null;
        IntPtr host = *(IntPtr*)((byte*)cam + SymbolMap.CameraBehaviorHost);
        if (host == IntPtr.Zero) return null;
        IntPtr vtbl = *(IntPtr*)host;
        if (vtbl == IntPtr.Zero) return null;
        var asFollow = (delegate* unmanaged<IntPtr, IntPtr>)(*(IntPtr*)((byte*)vtbl + SymbolMap.BehaviorAsFollowVtbl));
        if (asFollow == null) return null;
        IntPtr behavior = asFollow(host);
        if (behavior == IntPtr.Zero) return null;   // non-follow camera -> caller uses sticky/camera-ground
        IntPtr target = *(IntPtr*)((byte*)behavior + SymbolMap.BehaviorTargetObj);
        if (target == IntPtr.Zero) return null;
        float* p = (float*)((byte*)target + SymbolMap.GobPosition);
        return new Vector3(p[0], p[1], p[2]);
    }

    /// <summary>Aim the engine's MVP at a precomputed world-&gt;GL-clip matrix: VIEW = identity, PROJECTION =
    /// <paramref name="vp"/>, pushing both stacks so <see cref="RestoreLightTransform"/> pops the engine's exact
    /// matrices back. Leaves the active matrix mode as the engine expects (so RenderDrawBucket's own Push/Pop
    /// protects its normal stack, not PROJECTION). Shared by the sun ortho pass and the point-light cube faces.</summary>
    private static void PushLightVP(System.Numerics.Matrix4x4 vp)
    {
        _savedMatrixMode = *_curMatrix;
        _setMatrixMode(MatView); _pushMatrix(); _loadIdentity();         // view = I
        _setMatrixMode(MatProjection); _pushMatrix(); _loadIdentity();   // proj = vp (world -> GL clip)
        fixed (float* m = Flatten(vp, _transpose)) _multMatrix(m);
        _setMatrixMode(_savedMatrixMode);
    }

    /// <summary>Pop the VIEW/PROJECTION stacks back to the engine's exact matrices, restore the active matrix
    /// mode, and flag dirty so the next draw re-uploads them. No captured-param reconstruction.</summary>
    private static void RestoreLightTransform()
    {
        _setMatrixMode(MatProjection); _popMatrix();
        _setMatrixMode(MatView); _popMatrix();
        _setMatrixMode(_savedMatrixMode);
        if (_flagDirty != null) _flagDirty();
    }

    /// <summary>Discover scene shader programs at link time (cold path). Records the GL program id (at
    /// PROG_FIXED+0x64) for the per-frame receiver loop; uniform locations resolve lazily on first draw
    /// (the program is linked by then). Non-receiver programs are filtered out at resolve time.</summary>
    [UnmanagedCallersOnly]
    private static void OnLoadShader(IntPtr prog, IntPtr n1, IntPtr n2, byte geo, IntPtr n3)
    {
        if (_origLoadShader != null) _origLoadShader(prog, n1, n2, geo, n3);
        if (!_enabled || prog == IntPtr.Zero || _recvCount >= MaxRecv) return;
        int id = *(int*)(prog + SymbolMap.ProgFixedProgramId);
        if (id == 0) return;
        for (int i = 0; i < _recvCount; i++) if (_recvProg[i] == id) return;   // de-dup relink of same slot
        _recvProg[_recvCount] = id;
        _recvResolved[_recvCount] = false;
        _recvCount++;
    }

    /// <summary>Bind the shadow maps (sun on units 7/8, point-light cubes on 9..) and feed every discovered receiver
    /// program its uniforms. Runs at the end of the depth pass (engine cache == GL, no engine setter inside), raw-
    /// restoring the program + active-texture unit so the engine's state tracking stays consistent (the 3b-i rule).
    /// The SUN uniforms are gated on <paramref name="haveSun"/> (their matrices are stale until the sun/camera are
    /// captured); the POINT-LIGHT uniforms always upload, so torch shadows work indoors where there is no sun.</summary>
    /// <summary>Zero <c>sowxShadowEnable</c> + <c>sowxLocalCount</c> on every receiver program for the duration of
    /// the depth pass: casters render with their full material shaders (plan A), so WITHOUT this every shadow-map
    /// fragment re-runs the receiver's own PCF cube sampling while rendering the maps themselves -- a pure-waste
    /// GPU feedback multiplier. <see cref="UploadShadowUniforms"/> at pass end restores the live values. Raw
    /// UseProgram + restore is safe here for the same reason it is in the upload (inside the pass bracket).</summary>
    private static void MuteReceiversForDepth()
    {
        if (_recvCount == 0 || Gl.Uniform1i == null) return;
        int saved; Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &saved);
        for (int i = 0; i < _recvCount; i++)
        {
            if (!_recvResolved[i] || !_recvIsReceiver[i]) continue;
            if (_recvEnableLoc[i] < 0 && _recvLocalCountLoc[i] < 0) continue;
            Gl.UseProgram((uint)_recvProg[i]);
            if (_recvEnableLoc[i] >= 0) Gl.Uniform1i(_recvEnableLoc[i], 0);
            if (_recvLocalCountLoc[i] >= 0) Gl.Uniform1i(_recvLocalCountLoc[i], 0);
        }
        Gl.UseProgram((uint)saved);
    }

    /// <summary>Lazily creates the shared sampler-object pair: NEAREST raw (_cubeSampler) and LINEAR
    /// depth-compare (_cubeCmpSampler, hardware PCF). Callers guard the GL entry points.</summary>
    private static void EnsureShadowSamplers()
    {
        if (_cubeCmpSampler != 0) return;
        uint so; Gl.GenSamplers(1, &so); _cubeSampler = so;
        Gl.SamplerParameteri(so, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.SamplerParameteri(so, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.SamplerParameteri(so, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.SamplerParameteri(so, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        uint sc; Gl.GenSamplers(1, &sc); _cubeCmpSampler = sc;
        Gl.SamplerParameteri(sc, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.SamplerParameteri(sc, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.SamplerParameteri(sc, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.SamplerParameteri(sc, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.SamplerParameteri(sc, Gl.TEXTURE_COMPARE_MODE, (int)Gl.COMPARE_REF_TO_TEXTURE);
        Gl.SamplerParameteri(sc, Gl.TEXTURE_COMPARE_FUNC, (int)Gl.LEQUAL);
    }

    private static void UploadShadowUniforms(bool haveSun)
    {
        if (_recvCount == 0 || Gl.GetUniformLocation == null || Gl.Uniform1i == null || Gl.UniformMatrix4fv == null) return;
        int savedProg, savedActive;
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &savedProg);
        Gl.GetIntegerv(Gl.ACTIVE_TEXTURE, &savedActive);

        // Sun wide/near maps on 7/8; point-light cubes on 9.. (bind ALL cube slots -- an unbound samplerCube the
        // shader declares is undefined even when the count gates it out of sampling).
        Gl.ActiveTexture(Gl.TEXTURE0 + ShadowUnit);     Gl.BindTexture(Gl.TEXTURE_2D, _depthTex);
        Gl.ActiveTexture(Gl.TEXTURE0 + NearShadowUnit); Gl.BindTexture(Gl.TEXTURE_2D, _depthTexNear);
        // Sun cascades get the same hardware-PCF compare sampler as the cubes: every receiver tap becomes a
        // bilinear-weighted lit fraction instead of a binary NEAREST hit -- the fix for the stair-stepped
        // wide-map silhouettes on walls (#65). The debug quad reads the raw texture on unit 0, unaffected.
        if (Gl.GenSamplers != null && Gl.BindSampler != null && Gl.SamplerParameteri != null)
        {
            EnsureShadowSamplers();
            Gl.BindSampler(ShadowUnit, _cubeCmpSampler);
            Gl.BindSampler(NearShadowUnit, _cubeCmpSampler);
        }
        if (_cubesReady)
        {
            Gl.ActiveTexture(Gl.TEXTURE0 + LocalUnit0);
            // Clobber detector: we bound array+compare sampler on unit 9 LAST frame; if the engine's scene
            // draws changed either, the receiver sampled with broken state all frame (compare off -> binary/
            // undefined taps). Logged on every distinct clobber signature, not just once.
            if (_unitStateLogged && Gl.GetIntegerv != null && _cubeCmpSampler != 0)
            {
                int samp = -1, arr = -1;
                Gl.GetIntegerv(Gl.SAMPLER_BINDING, &samp);
                Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D_ARRAY, &arr);
                if ((samp != (int)_cubeCmpSampler || arr != (int)_cubeArrTex) && (samp != _lastClobberSamp || arr != _lastClobberArr))
                {
                    _lastClobberSamp = samp; _lastClobberArr = arr;
                    _log?.Info($"ShadowMapService: unit {LocalUnit0} CLOBBERED post-scene: samplerObj={samp} (ours={_cubeCmpSampler}) arrayTex={arr} (ours={_cubeArrTex})");
                }
            }
            if (!_unitStateLogged && Gl.GetIntegerv != null)
            {
                _unitStateLogged = true;
                int samp = -1, arr = -1;
                Gl.GetIntegerv(Gl.SAMPLER_BINDING, &samp);
                Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D_ARRAY, &arr);
                _log?.Info($"ShadowMapService: unit {LocalUnit0} post-scene state: samplerObj={samp} arrayTex={arr} (ours={_cubeArrTex}) -- a foreign sampler object would OVERRIDE our texture's filter/wrap state");
            }
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _cubeArrTex);
            // Texture-OBJECT state probe: if the engine resets our filter/compare parameters during its frame,
            // this catches it red-handed (state travels with the object, so a wrong value here means someone
            // wrote to OUR texture). Logs on every distinct wrong signature.
            if (Gl.GetTexParameteriv != null)
            {
                int minf = 0, cmp = 0;
                Gl.GetTexParameteriv(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MIN_FILTER, &minf);
                Gl.GetTexParameteriv(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_COMPARE_MODE, &cmp);
                if ((minf != (int)Gl.LINEAR || cmp != (int)Gl.COMPARE_REF_TO_TEXTURE) && (minf != _lastTexMinf || cmp != _lastTexCmp))
                {
                    _lastTexMinf = minf; _lastTexCmp = cmp;
                    _log?.Info($"ShadowMapService: cube TEXTURE-OBJECT state wrong post-scene: minFilter=0x{minf:x} (want 0x{(int)Gl.LINEAR:x}) compareMode=0x{cmp:x} (want 0x{(int)Gl.COMPARE_REF_TO_TEXTURE:x})");
                }
            }
            Gl.ActiveTexture(Gl.TEXTURE0 + LocalUnit1);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _cubeRawView != 0 ? _cubeRawView : _cubeArrTex);
            // HI-tier pair on 11/12 (shadow LOD); lo arrays stand in when the hi tier is off so the shader's
            // declared samplers never sit on a mismatched unit.
            Gl.ActiveTexture(Gl.TEXTURE0 + LocalUnit2);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _cubeHiArrTex != 0 ? _cubeHiArrTex : _cubeArrTex);
            Gl.ActiveTexture(Gl.TEXTURE0 + LocalUnit3);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _cubeHiRawView != 0 ? _cubeHiRawView : (_cubeRawView != 0 ? _cubeRawView : _cubeArrTex));
            // Hardware-PCF sampler pair on the same array: unit 9 = LINEAR + depth-compare (every receiver
            // tap becomes a bilinear-weighted lit fraction -- the fix for texel-chunk penumbrae); unit 10 =
            // raw NEAREST for the PCSS blocker-distance probe. Sampler objects override texture parameters,
            // and nothing else in the engine binds samplers (verified: virgin state was 0).
            if (Gl.GenSamplers != null && Gl.BindSampler != null && Gl.SamplerParameteri != null)
            {
                EnsureShadowSamplers();
                Gl.BindSampler(LocalUnit0, _cubeCmpSampler);
                Gl.BindSampler(LocalUnit1, _cubeSampler);
                Gl.BindSampler(LocalUnit2, _cubeCmpSampler);
                Gl.BindSampler(LocalUnit3, _cubeSampler);
            }
        }

        byte t = (byte)(_recvTranspose ? 1 : 0);
        float texel = 1f / _mapSize;
        float wideBias = _worldBias / (3f * _radius);
        float nearBias = _worldBias / (3f * _nearRadius);
        float* mpW = stackalloc float[16]; FlattenInto(_lastLightVP, mpW);
        float* mpN = stackalloc float[16]; FlattenInto(_lastLightVPNear, mpN);
        // Slots are stable (position-matched), so active lights can sit at ANY slot index: upload the full slot
        // range and let far=0 inert the unclaimed ones (both shader paths guard dFrag >= far). 0 when no light
        // was recorded this frame, so vanished lights can't leave a stale stamp.
        int localCount = _cubesReady && ShadowInputs.LocalLightCount > 0 ? ShadowInputs.MaxLocalLights : 0;
        for (int j = 0; j < ShadowInputs.MaxLocalLights; j++)
        {
            _hiOfSlotPlus1[j] = _cubeHiArrTex != 0 ? _slotHiLayer[j] + 1 : 0;   // +1: uniform default 0 must mean base array
            int asz = _hiOfSlotPlus1[j] > 0 ? _cubeSizeHi : _cubeSize;
            int res = _slotRes[j] > 0 ? Math.Min(_slotRes[j], asz) : asz;
            _slotUvScaleArr[j] = (float)res / asz;
            _slotTexelArr[j] = CubeSeamFactor / res;
        }
        for (int i = 0; i < _recvCount; i++)
        {
            if (!_recvResolved[i]) ResolveReceiver(i);
            if (!_recvIsReceiver[i]) continue;
            Gl.UseProgram((uint)_recvProg[i]);
            // Interior: sun path hard-off (stale exterior maps must not leak); cubes are gated by sowxLocalCount,
            // not this. Debug modes (>=2) pass through regardless -- the sun sample path only acts on ==1.
            if (_recvEnableLoc[i] >= 0) Gl.Uniform1i(_recvEnableLoc[i], _enableValue >= 2 ? _enableValue : _interior || _enableValue == 0 ? 0 : 1);
            if (haveSun)
            {
                Gl.UniformMatrix4fv(_recvLightVPLoc[i], 1, t, mpW);
                if (_recvSamplerLoc[i] >= 0) Gl.Uniform1i(_recvSamplerLoc[i], (int)ShadowUnit);
                if (_recvTexelLoc[i] >= 0 && Gl.Uniform1f != null) Gl.Uniform1f(_recvTexelLoc[i], texel);
                if (_recvBiasLoc[i] >= 0 && Gl.Uniform1f != null) Gl.Uniform1f(_recvBiasLoc[i], wideBias);
                if (_recvNearLightVPLoc[i] >= 0) Gl.UniformMatrix4fv(_recvNearLightVPLoc[i], 1, t, mpN);
                if (_recvNearSamplerLoc[i] >= 0) Gl.Uniform1i(_recvNearSamplerLoc[i], (int)NearShadowUnit);
                if (_recvNearTexelLoc[i] >= 0 && Gl.Uniform1f != null) Gl.Uniform1f(_recvNearTexelLoc[i], texel);
                if (_recvNearBiasLoc[i] >= 0 && Gl.Uniform1f != null) Gl.Uniform1f(_recvNearBiasLoc[i], nearBias);
            }
            // Point-light cubes: count + world positions + per-slot far + shared near + bias + the per-slot sampler units.
            if (_recvLocalCountLoc[i] >= 0)
            {
                Gl.Uniform1i(_recvLocalCountLoc[i], localCount);
                if (_recvLocalPosLoc[i] >= 0 && Gl.Uniform3fv != null)
                    fixed (System.Numerics.Vector3* pp = _slotPos) Gl.Uniform3fv(_recvLocalPosLoc[i], ShadowInputs.MaxLocalLights, (float*)pp);   // last-RENDERED pos, aligned with the (possibly skipped) cube contents
                if (_recvLocalMatchLoc[i] >= 0 && Gl.Uniform3fv != null)
                    fixed (System.Numerics.Vector3* mp = _slotSeenPos) Gl.Uniform3fv(_recvLocalMatchLoc[i], ShadowInputs.MaxLocalLights, (float*)mp);   // RAW per-frame pos: the match key never lags out of tolerance
                if (_recvLocalFarLoc[i] >= 0 && Gl.Uniform1fv != null)
                    fixed (float* fp = _cubeFar) Gl.Uniform1fv(_recvLocalFarLoc[i], ShadowInputs.MaxLocalLights, fp);
                if (_recvLocalNearLoc[i] >= 0 && Gl.Uniform1f != null) Gl.Uniform1f(_recvLocalNearLoc[i], _localNear);
                if (_recvLocalBiasLoc[i] >= 0 && Gl.Uniform1f != null) Gl.Uniform1f(_recvLocalBiasLoc[i], _localBias);
                if (_recvLocalCubesLoc[i] >= 0) Gl.Uniform1i(_recvLocalCubesLoc[i], (int)LocalUnit0);
                if (_recvLocalCubesRawLoc[i] >= 0) Gl.Uniform1i(_recvLocalCubesRawLoc[i], (int)LocalUnit1);
                if (_recvLocalCubesHiLoc[i] >= 0) Gl.Uniform1i(_recvLocalCubesHiLoc[i], (int)LocalUnit2);
                if (_recvLocalCubesHiRawLoc[i] >= 0) Gl.Uniform1i(_recvLocalCubesHiRawLoc[i], (int)LocalUnit3);
                if (_recvLocalHiLoc[i] >= 0 && Gl.Uniform1iv != null)
                    fixed (int* hp = _hiOfSlotPlus1) Gl.Uniform1iv(_recvLocalHiLoc[i], ShadowInputs.MaxLocalLights, hp);
                if (_recvLocalUvScaleLoc[i] >= 0 && Gl.Uniform1fv != null)
                    fixed (float* sp = _slotUvScaleArr) Gl.Uniform1fv(_recvLocalUvScaleLoc[i], ShadowInputs.MaxLocalLights, sp);
                if (_recvLocalTexelLoc[i] >= 0 && Gl.Uniform1fv != null)
                    fixed (float* tp = _slotTexelArr) Gl.Uniform1fv(_recvLocalTexelLoc[i], ShadowInputs.MaxLocalLights, tp);
            }
        }
        if (localCount > 0 && !_deepDiagDone) DeepUploadDiag(localCount);
        if (!_cubeDumpDone) TryDumpCubeSlot();
        Gl.ActiveTexture((uint)savedActive);
        Gl.UseProgram((uint)savedProg);
        if (!_uploadErrLogged)
        {
            _uploadErrLogged = true;
            uint err = Gl.GetError != null ? Gl.GetError() : 0;
            if (err != 0) _log?.Info($"ShadowMapService: GL error 0x{err:x} after receiver upload (count={localCount}, recv={_recvCount}) -- uniforms may be silently dropped");
        }
    }

    private static bool _deepDiagDone;

    /// <summary>One-shot ground-truth probe, fired on the first upload with live lights: (1) per-call GL error
    /// bracketing on one receiver's uniform uploads, (2) glGetUniform readback of what the program ACTUALLY
    /// holds afterward, (3) a PGM dump of the first valid slot's 6 cube faces so the bake CONTENT can be looked
    /// at directly. Exists because the compiled receiver source was verified current yet shadows render blocky
    /// -- the remaining suspects are the uniform values and the depth data, and both are otherwise invisible.</summary>
    private static void DeepUploadDiag(int localCount)
    {
        _deepDiagDone = true;
        if (Gl.GetUniformfv == null || Gl.GetUniformiv == null || Gl.GetError == null) return;
        try
        {
            int i = -1;
            for (int k = 0; k < _recvCount; k++) if (_recvIsReceiver[k] && _recvLocalCountLoc[k] >= 0) { i = k; break; }
            if (i < 0) { _log?.Info("ShadowMapService: deep diag: no receiver with local count loc"); return; }
            uint p = (uint)_recvProg[i];
            Gl.UseProgram(p);

            Gl.GetError();   // clear accumulated state so brackets below are attributable
            Gl.Uniform1i(_recvLocalCountLoc[i], localCount);
            uint e1 = Gl.GetError();
            fixed (System.Numerics.Vector3* pp = _slotPos) Gl.Uniform3fv(_recvLocalPosLoc[i], ShadowInputs.MaxLocalLights, (float*)pp);
            uint e2 = Gl.GetError();
            fixed (System.Numerics.Vector3* mp = _slotSeenPos) Gl.Uniform3fv(_recvLocalMatchLoc[i], ShadowInputs.MaxLocalLights, (float*)mp);
            uint e3 = Gl.GetError();
            fixed (float* fp = _cubeFar) Gl.Uniform1fv(_recvLocalFarLoc[i], ShadowInputs.MaxLocalLights, fp);
            uint e4 = Gl.GetError();
            Gl.Uniform1f(_recvLocalNearLoc[i], _localNear);
            Gl.Uniform1f(_recvLocalBiasLoc[i], _localBias);
            Gl.Uniform1i(_recvLocalCubesLoc[i], (int)LocalUnit0);
            uint e5 = Gl.GetError();
            _log?.Info($"ShadowMapService: deep diag prog={p}: upload errors count=0x{e1:x} pos=0x{e2:x} match=0x{e3:x} far=0x{e4:x} rest=0x{e5:x}");

            int cnt = 0; Gl.GetUniformiv(p, _recvLocalCountLoc[i], &cnt);
            float near = -1f, bias = -1f;
            Gl.GetUniformfv(p, _recvLocalNearLoc[i], &near);
            Gl.GetUniformfv(p, _recvLocalBiasLoc[i], &bias);
            float* v = stackalloc float[4];
            Gl.GetUniformfv(p, _recvLocalPosLoc[i], v);
            float* m = stackalloc float[4];
            Gl.GetUniformfv(p, _recvLocalMatchLoc[i], m);
            float far0 = -1f; Gl.GetUniformfv(p, _recvLocalFarLoc[i], &far0);
            int sampler = -1; Gl.GetUniformiv(p, _recvLocalCubesLoc[i], &sampler);
            _log?.Info($"ShadowMapService: deep diag readback: count={cnt} near={near:F3} bias={bias:F3} far0={far0:F2} " +
                $"pos0=({v[0]:F1},{v[1]:F1},{v[2]:F1}) match0=({m[0]:F1},{m[1]:F1},{m[2]:F1}) samplerUnit={sampler} " +
                $"expected: count={localCount} near={_localNear:F3} bias={_localBias:F3} far0={_cubeFar[0]:F2} pos0=({_slotPos[0].X:F1},{_slotPos[0].Y:F1},{_slotPos[0].Z:F1})");

        }
        catch (Exception e) { _log?.Info($"ShadowMapService: deep diag failed: {e.Message}"); }
    }

    private static bool _cubeDumpDone;

    /// <summary>One-shot: once any slot is valid, dump its 6 working-array faces to /tmp as PGM images --
    /// the depth CONTENT is the last un-inspected link (code + uniforms are verified faithful).</summary>
    private static void TryDumpCubeSlot()
    {
        if (Gl.GetTextureSubImage == null || Gl.GetError == null || !_cubesReady) return;
        int j = -1;
        for (int k = 0; k < ShadowInputs.MaxLocalLights; k++) if (_slotValid[k]) { j = k; break; }
        if (j < 0) return;
        _cubeDumpDone = true;
        try
        {
            float[] face = new float[_cubeSize * _cubeSize];
            byte[] pgm = new byte[_cubeSize * _cubeSize];
            for (int f = 0; f < 6; f++)
            {
                fixed (float* fp2 = face)
                    Gl.GetTextureSubImage(_cubeArrTex, 0, 0, 0, j * 6 + f, _cubeSize, _cubeSize, 1,
                        Gl.DEPTH_COMPONENT, 0x1406 /*GL_FLOAT*/, face.Length * 4, fp2);
                for (int q = 0; q < face.Length; q++) pgm[q] = (byte)(Math.Clamp(face[q], 0f, 1f) * 255f);
                var hdr = System.Text.Encoding.ASCII.GetBytes($"P5\n{_cubeSize} {_cubeSize}\n255\n");
                using (var fs = System.IO.File.Create($"/tmp/sowx-cube-s{j}-f{f}.pgm"))
                { fs.Write(hdr); fs.Write(pgm); }
                // Full-fidelity copy for offline replay: raw float32, row-major, _cubeSize^2 per face.
                using (var fb = System.IO.File.Create($"/tmp/sowx-cube-s{j}-f{f}.f32"))
                    fb.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(face.AsSpan()));
            }
            uint eDump = Gl.GetError();
            _log?.Info($"ShadowMapService: dumped slot {j} faces to /tmp/sowx-cube-s{j}-f*.pgm (far={_cubeFar[j]:F1}, pos=({_slotPos[j].X:F1},{_slotPos[j].Y:F1},{_slotPos[j].Z:F1}), err=0x{eDump:x})");
        }
        catch (Exception e) { _log?.Info($"ShadowMapService: cube dump failed: {e.Message}"); }
    }

    /// <summary>One-shot: report a GL error surfacing from the receiver upload/bind path (a silently dropped
    /// glUniform* here would desync the shader from the extender and is otherwise invisible).</summary>
    private static bool _uploadErrLogged;
    private static bool _unitStateLogged;
    private static int _lastClobberSamp = -1, _lastClobberArr = -1;
    private static int _lastTexMinf = -1, _lastTexCmp = -1;
    private static uint _cubeSampler;
    private static uint _cubeCmpSampler;

    /// <summary>Resolve a program's sowx uniform locations once; a program with no <c>sowxLightVP</c> (not a
    /// lit receiver, or the uniform was optimized out) is marked non-receiver and skipped thereafter.</summary>
    private static void ResolveReceiver(int i)
    {
        uint p = (uint)_recvProg[i];
        _recvLightVPLoc[i] = Loc(p, "sowxLightVP");
        _recvEnableLoc[i] = Loc(p, "sowxShadowEnable");
        _recvSamplerLoc[i] = Loc(p, "sowxShadowMap");
        _recvTexelLoc[i] = Loc(p, "sowxShadowMapTexel");
        _recvBiasLoc[i] = Loc(p, "sowxShadowBias");
        _recvNearLightVPLoc[i] = Loc(p, "sowxNearLightVP");
        _recvNearSamplerLoc[i] = Loc(p, "sowxNearShadowMap");
        _recvNearTexelLoc[i] = Loc(p, "sowxNearMapTexel");
        _recvNearBiasLoc[i] = Loc(p, "sowxNearBias");
        _recvLocalCountLoc[i] = Loc(p, "sowxLocalCount");
        _recvLocalPosLoc[i] = Loc(p, "sowxLocalPos");
        _recvLocalMatchLoc[i] = Loc(p, "sowxLocalMatchPos");
        _recvLocalFarLoc[i] = Loc(p, "sowxLocalFar");
        _recvLocalNearLoc[i] = Loc(p, "sowxLocalNear");
        _recvLocalBiasLoc[i] = Loc(p, "sowxLocalBias");
        _recvLocalCubesLoc[i] = Loc(p, "sowxLocalCubes");
        _recvLocalCubesRawLoc[i] = Loc(p, "sowxLocalCubesRaw");
        _recvLocalHiLoc[i] = Loc(p, "sowxLocalHi");
        _recvLocalCubesHiLoc[i] = Loc(p, "sowxLocalCubesHi");
        _recvLocalCubesHiRawLoc[i] = Loc(p, "sowxLocalCubesHiRaw");
        _recvLocalUvScaleLoc[i] = Loc(p, "sowxLocalUvScale");
        _recvLocalTexelLoc[i] = Loc(p, "sowxLocalTexel");
        if (_recvLocalUvScaleLoc[i] >= 0 && !_recvHasUvScale)
        { _recvHasUvScale = true; _log?.Info($"ShadowMapService: receiver prog {p} advertises sowxLocalUvScale (loc={_recvLocalUvScaleLoc[i]}) -- sub-rect LOD unlocked"); }
        if (_recvLocalCubesHiLoc[i] >= 0 && !_recvHasHi)
        { _recvHasHi = true; _log?.Info($"ShadowMapService: receiver prog {p} advertises sowxLocalCubesHi -- hi tier unlocked"); }
        _recvIsReceiver[i] = _recvLightVPLoc[i] >= 0 || _recvLocalCountLoc[i] >= 0;
        _recvResolved[i] = true;
        if (_recvIsReceiver[i]) _recvActive++;
        if (_recvIsReceiver[i] && !_recvLogged)
        {
            _recvLogged = true; _log?.Info($"ShadowMapService: receiver programs found (first prog={p}, lightVPLoc={_recvLightVPLoc[i]}, samplerLoc={_recvSamplerLoc[i]})");
            DumpReceiverSource(p);
        }
        if (_recvIsReceiver[i]) FingerprintProgram(p);
    }

    /// <summary>Per-program prelude fingerprint: which .shd pair it is and WHERE lighting runs (a lit
    /// program whose VERTEX stage does the lighting evaluates our shadow factor per mesh vertex -- the
    /// tessellation-sized staircase suspect). Logged once per program.</summary>
    private static void FingerprintProgram(uint prog)
    {
        if (Gl.GetAttachedShaders == null || Gl.GetShaderSource == null) return;
        try
        {
            uint* shaders = stackalloc uint[8];
            int count = 0;
            Gl.GetAttachedShaders(prog, 8, &count, shaders);
            var sb = new System.Text.StringBuilder(128);
            sb.Append("ShadowMapService: prog ").Append(prog);
            for (int s = 0; s < count; s++)
            {
                int len = 0;
                Gl.GetShaderiv(shaders[s], 0x8B88, &len);
                if (len <= 1) continue;
                byte[] buf = new byte[len + 1];   // full source: era markers live deep in inc_common
                int got = 0;
                fixed (byte* bp = buf) Gl.GetShaderSource(shaders[s], buf.Length, &got, bp);
                string full = System.Text.Encoding.UTF8.GetString(buf, 0, got);
                string head = full.Length > 8192 ? full.Substring(0, 8192) : full;
                int type = 0;
                Gl.GetShaderiv(shaders[s], 0x8B4F, &type);
                string name = "?";
                int bi = head.IndexOf(".shd", StringComparison.Ordinal);
                if (bi > 0) { int st = head.LastIndexOf(' ', bi); name = head.Substring(st + 1, bi - st + 3); }
                string flag(string k) { int ix = head.IndexOf("#define " + k + " ", StringComparison.Ordinal); return ix < 0 ? "?" : head.Substring(ix + 9 + k.Length, 1); }
                sb.Append(type == 0x8B31 ? " VS=" : " FS=").Append(name)
                  .Append(" L").Append(flag("LIGHTING")).Append(" FL").Append(flag("FRAGMENT_LIGHTING"))
                  .Append(" FVL").Append(head.Contains("#define FORCE_VERTEX_LIGHTING 1") ? "1" : "0")
                  .Append(" NM").Append(flag("NORMAL_MAP"))
                  // era markers: proves THIS program compiled the current inc_common (stale-program check)
                  .Append(" era:mb35=").Append(full.Contains("MINBLUR 0.035") ? "y" : "n")
                  .Append(",shadow=").Append(full.Contains("sampler2DArrayShadow") ? "y" : "n");
            }
            _log?.Info(sb.ToString());
        }
        catch { }
    }

    /// <summary>One-shot ground-truth diagnostic: read back the ACTUAL fragment source the engine compiled
    /// for the first receiver program, log a fingerprint of its sowx block (which era of inc_common is live),
    /// and dump the full text to /tmp/sowx-recv-<prog>.glsl. Exists because three hak deploys in a row showed
    /// zero in-game effect -- the filesystem stopped being trustworthy evidence of what the GPU runs.</summary>
    private static void DumpReceiverSource(uint prog)
    {
        if (Gl.GetAttachedShaders == null || Gl.GetShaderSource == null || Gl.GetShaderiv == null) return;
        try
        {
            uint* shaders = stackalloc uint[8];
            int count = 0;
            Gl.GetAttachedShaders(prog, 8, &count, shaders);
            for (int s = 0; s < count; s++)
            {
                int len = 0;
                Gl.GetShaderiv(shaders[s], 0x8B88 /*GL_SHADER_SOURCE_LENGTH*/, &len);
                if (len <= 1) continue;
                byte[] buf = new byte[len + 1];
                int got = 0;
                fixed (byte* bp = buf) Gl.GetShaderSource(shaders[s], buf.Length, &got, bp);
                string src = System.Text.Encoding.UTF8.GetString(buf, 0, got);
                int type = 0;
                Gl.GetShaderiv(shaders[s], 0x8B4F /*GL_SHADER_TYPE*/, &type);
                _log?.Info($"ShadowMapService: prog {prog} shader {shaders[s]} type=0x{type:X} len={got} " +
                    $"SOWX={Count(src, "SOWX")} taps={Count(src, "SOWX_LOCAL_TAPS")} vogel={Count(src, "Vogel")} " +
                    $"arr={Count(src, "sampler2DArray")} cube={Count(src, "samplerCube")} match={Count(src, "sowxLocalMatchPos")} " +
                    $"minblur025={Count(src, "MINBLUR 0.025")} normofs={Count(src, "NORMOFS")} pcss={Count(src, "PCSS")}");
                try { System.IO.File.WriteAllText($"/tmp/sowx-recv-{prog}-{shaders[s]}.glsl", src); } catch { }
            }
        }
        catch (Exception e) { _log?.Info($"ShadowMapService: receiver source dump failed: {e.Message}"); }
    }

    private static int Count(string hay, string needle)
    {
        int n = 0;
        for (int i = hay.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = hay.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>Flatten a Matrix4x4 row-major into a caller-owned float[16] (== MakeMatrix4x4 layout, no
    /// transpose). Used for the two receiver matrices at once, where the shared <see cref="Flatten"/> buffer
    /// can't hold both.</summary>
    private static void FlattenInto(System.Numerics.Matrix4x4 m, float* a)
    {
        a[0] = m.M11; a[1] = m.M12; a[2] = m.M13; a[3] = m.M14;
        a[4] = m.M21; a[5] = m.M22; a[6] = m.M23; a[7] = m.M24;
        a[8] = m.M31; a[9] = m.M32; a[10] = m.M33; a[11] = m.M34;
        a[12] = m.M41; a[13] = m.M42; a[14] = m.M43; a[15] = m.M44;
    }

    [ThreadStatic] private static float[]? _mtmp;
    /// <summary>Flatten a Matrix4x4 to a row-major float[16] (== MakeMatrix4x4 / MultMatrix layout). Reused per
    /// thread to stay alloc-free after warmup. <paramref name="transpose"/> emits column-major (convention knob).</summary>
    private static float[] Flatten(System.Numerics.Matrix4x4 m, bool transpose)
    {
        float[] a = _mtmp ??= new float[16];
        if (!transpose)
        {
            a[0] = m.M11; a[1] = m.M12; a[2] = m.M13; a[3] = m.M14;
            a[4] = m.M21; a[5] = m.M22; a[6] = m.M23; a[7] = m.M24;
            a[8] = m.M31; a[9] = m.M32; a[10] = m.M33; a[11] = m.M34;
            a[12] = m.M41; a[13] = m.M42; a[14] = m.M43; a[15] = m.M44;
        }
        else
        {
            a[0] = m.M11; a[1] = m.M21; a[2] = m.M31; a[3] = m.M41;
            a[4] = m.M12; a[5] = m.M22; a[6] = m.M32; a[7] = m.M42;
            a[8] = m.M13; a[9] = m.M23; a[10] = m.M33; a[11] = m.M43;
            a[12] = m.M14; a[13] = m.M24; a[14] = m.M34; a[15] = m.M44;
        }
        return a;
    }

    /// <summary>Render opaque casters into the depth FBO from the light's POV (3b-ii): aim the engine's MVP at
    /// the light, re-drive the caster buckets, restore the camera. Falls back to the camera POV until the sun/
    /// camera/perspective are captured. Fully brackets GL state; alloc-free after the one-time lazy create.</summary>
    private static void DepthPass(IntPtr scene)
    {
        if (!Gl.Init(_log!)) { _glFailed = true; return; }
        if (Gl.GenFramebuffers == null || Gl.FramebufferTexture2D == null || Gl.CheckFramebufferStatus == null
            || Gl.Viewport == null || Gl.Clear == null || Gl.DepthMask == null || Gl.DepthFunc == null
            || Gl.ClearDepth == null || Gl.DrawBuffer == null || Gl.ReadBuffer == null
            || Gl.DepthRange == null || Gl.GetFloatv == null)
        { _log?.Info("ShadowMapService: FBO/depth entry points unresolved -- disabled"); _glFailed = true; return; }

        long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // --- save the ENGINE's GL state BEFORE binding our objects ---
        int* s = stackalloc int[6];
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &s[0]);
        Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &s[1]);
        Gl.GetIntegerv(Gl.ARRAY_BUFFER_BINDING, &s[2]);
        Gl.GetIntegerv(Gl.ACTIVE_TEXTURE, &s[3]);
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &s[4]);
        Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &s[5]);
        int* vp = stackalloc int[4]; Gl.GetIntegerv(Gl.VIEWPORT, vp);
        int* dmask = stackalloc int[1]; Gl.GetIntegerv(Gl.DEPTH_WRITEMASK, dmask);
        int* dfunc = stackalloc int[1]; Gl.GetIntegerv(Gl.DEPTH_FUNC, dfunc);
        int depthOn = Gl.IsEnabled(Gl.DEPTH_TEST), scissorOn = Gl.IsEnabled(Gl.SCISSOR_TEST);
        float* drange = stackalloc float[2]; Gl.GetFloatv(Gl.DEPTH_RANGE, drange);   // engine may leave a non-[0,1] range

        if (!_glReady && !CreateObjects())
        {   // restore what CreateObjects may have dirtied, then give up
            _glFailed = true;
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)s[5]); Gl.BindVertexArray((uint)s[1]);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, (uint)s[2]); Gl.ActiveTexture((uint)s[3]);
            Gl.BindTexture(Gl.TEXTURE_2D, (uint)s[4]); Gl.UseProgram((uint)s[0]);
            return;
        }

        ShadowInputs.InDepthPass = true;
        Gl.Disable(Gl.SCISSOR_TEST);   // the engine's scissor rect is in window space, not our map
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthRange(0.0, 1.0);   // don't inherit the engine's depth range
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.DepthMask(1);
        // Clear the NEAR map once per presented frame (it re-renders every frame); the WIDE map is a cache --
        // it clears only on wide-refresh frames, in its render block below.
        if (!_depthCleared && !_interior)
        {
            Gl.ClearDepth(1.0);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fboNear); Gl.Clear(Gl.DEPTH_BUFFER_BIT);
            _depthCleared = true;
        }

        // Break feedback loops: last frame's upload left the wide map on unit 7 and the near map on unit 8, and a
        // receiver program may still have them bound while we render INTO them. Unbind both (color is discarded on
        // these depth-only FBOs anyway); the upload below rebinds them for next frame's receivers.
        Gl.ActiveTexture(Gl.TEXTURE0 + ShadowUnit);     Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.ActiveTexture(Gl.TEXTURE0 + NearShadowUnit); Gl.BindTexture(Gl.TEXTURE_2D, 0);
        if (_cubesReady)   // same feedback break for the cube-array unit (last upload left it bound on 9)
        { Gl.ActiveTexture(Gl.TEXTURE0 + LocalUnit0); Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0); }
        Gl.ActiveTexture((uint)s[3]);
        MuteReceiversForDepth();

        // Two-sided: drop back-face cull for BOTH caster draws (Enable(3) is swallowed while InDepthPass, so
        // RenderDrawBucket can't re-enable it). Via the engine setter so its cache tracks GL; restored after.
        _cullWas = _twoSided && _riIsEnabled != null && _riDisable != null && _riIsEnabled(CullFaceCap) != 0;
        if (_cullWas) _riDisable(CullFaceCap);

        // One shared centre (player anchor, else camera-ground) for both maps and the re-walk, so they align.
        Vector3 focus = ShadowFocus();
        // NEAR dynamic map first (every frame -- creatures move); it also establishes `light`. SetLightTransform
        // zeroes the out matrix on entry, so on failure _lastLightVPNear stays default and the shader's lc.w<=0
        // guard treats the near cascade as fully lit -> no phantom disc over the cleared near FBO.
        bool light = !_interior && SetLightTransform(focus, _nearRadius, out _lastLightVPNear);   // characters / creatures / gobs; interiors skip ALL sun-map work
        if (light)
        {
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fboNear);
            Gl.Viewport(0, 0, _mapSize, _mapSize);
            foreach (int b in _nearBuckets) WalkBucket(scene, b, cull: false);
            RestoreLightTransform();

            // WIDE static map: a cache -- re-rendered only on wide-refresh frames (focus drifted / sun rotated,
            // decided at ManageSceneBSP so the buckets carry the full-radius re-walk set). Off-frames keep the
            // depth texture AND _lastLightVP from the last render, which stay valid together.
            if ((_wideRefreshThisFrame || !_wideValid) && SetLightTransform(focus, _radius, out _lastLightVP))
            {
                Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
                Gl.Viewport(0, 0, _mapSize, _mapSize);
                if (!_wideCleared) { Gl.ClearDepth(1.0); Gl.Clear(Gl.DEPTH_BUFFER_BIT); _wideCleared = true; }
                foreach (int b in _wideBuckets) WalkBucket(scene, b, cull: false);
                RestoreLightTransform();
                _wideValid = true; _wideFocus = focus; _wideSunDir = ShadowInputs.SunDir; _wideRenders++;
            }
        }
        else if (!_interior)
        {   // pre-capture fallback: draw the wide buckets from the camera POV so the corner map still shows life
            if (!_fallbackLogged)
            { _fallbackLogged = true; _log?.Info("ShadowMapService: light transform inactive (waiting on sun/camera) -- camera-POV fallback"); }
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
            Gl.Viewport(0, 0, _mapSize, _mapSize);
            if (!_wideCleared) { Gl.ClearDepth(1.0); Gl.Clear(Gl.DEPTH_BUFFER_BIT); _wideCleared = true; }
            foreach (int b in _wideBuckets) WalkBucket(scene, b, cull: false);
        }

        // Point-light shadow cubes (slice 6): independent of the sun/camera capture -- runs whenever point lights
        // were recorded this frame. Shares this same InDepthPass + two-sided-cull bracket.
        if (_localOn && ShadowInputs.LocalLightCount > 0 && _setMatrixMode != null && _renderDrawBucket != null
            && !(_localOnce && _cubeDoneThisFrame))   // cubes are camera-independent -> one pass per frame; skip reflection subframes
        {
            CubePass(scene);
            _cubeDoneThisFrame = true;
        }

        // Upload receiver uniforms OUTSIDE the sun gate: the sun set is skipped when !light (stale matrices), but the
        // point-light cubes must reach the shaders indoors, where there is no sun and `light` is false.
        UploadShadowUniforms(light);

        if (_cullWas && _origRiEnable != null) _origRiEnable(CullFaceCap);   // restore (call orig -> bypasses the swallow)

        // --- restore ONLY the raw-changed, per-pass state (FBO/viewport/depth/scissor). Deliberately do NOT
        //     restore program/VAO/buffer/texture: RenderDrawBucket moved the engine's own state *cache* with
        //     GL through its setters, so cache and GL now agree; a raw restore would desync them and the next
        //     engine draw would skip a "redundant" bind and paint with our restored binding (angle-dependent
        //     vanishing geometry). s[0..4] stay saved only for the CreateObjects-failure path above. ---
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)s[5]);
        Gl.Viewport(vp[0], vp[1], vp[2], vp[3]);
        Gl.DepthMask((byte)dmask[0]);
        Gl.DepthFunc((uint)dfunc[0]);
        Gl.DepthRange(drange[0], drange[1]);
        SetCap(Gl.DEPTH_TEST, depthOn); SetCap(Gl.SCISSOR_TEST, scissorOn);
        ShadowInputs.InDepthPass = false;
        _depthTicks += System.Diagnostics.Stopwatch.GetTimestamp() - p0;
        _depthFrames++;

        if (!_passLogged) { _passLogged = true; _log?.Info($"ShadowMapService: caster depth pass ran (buckets [{string.Join(",", _wideBuckets)}])"); }
    }

    /// <summary>Render each recorded point light's depth cube: for every light, attach each of the 6 faces into
    /// the shared FBO in turn, aim the engine's MVP at that face (90&deg; perspective from the light), clear, and
    /// re-drive the dynamic caster buckets. Runs inside DepthPass' state bracket (InDepthPass, two-sided cull,
    /// depth range/mask/func already set). Lazily creates the cubes on first use; alloc-free after that.</summary>
    /// <summary>Shadow-LOD tier accessors: which array pair / layer / face size slot <paramref name="j"/>
    /// currently renders to and samples from. All per-frame plumbing (copies, dyn renders, keep-alive) routes
    /// through these so a tier flip is one field write.</summary>
    private static bool SlotHi(int j) => _slotHiLayer[j] >= 0 && _cubeHiArrTex != 0;
    private static uint WorkTex(int j) => SlotHi(j) ? _cubeHiArrTex : _cubeArrTex;
    private static uint StaticTex(int j) => SlotHi(j) ? _cubeHiStaticArrTex : _cubeStaticArrTex;
    private static int LayerBase(int j) => SlotHi(j) ? _slotHiLayer[j] * 6 : j * 6;
    private static int SlotSize(int j) => SlotHi(j) ? _cubeSizeHi : _cubeSize;   // array FACE size (copies)
    /// <summary>Committed render resolution of slot j (viewport corner sub-rect; &lt;= its array face size).</summary>
    private static int SlotRes(int j) => _slotRes[j] > 0 ? Math.Min(_slotRes[j], SlotSize(j)) : SlotSize(j);

    /// <summary>Lowest hi-tier layer no slot currently holds or wants; -1 when the pool is full.</summary>
    private static int FreeHiLayer()
    {
        for (int l = 0; l < _hiSlots; l++)
        {
            bool used = false;
            for (int j = 0; j < ShadowInputs.MaxLocalLights && !used; j++)
                used = _slotHiLayer[j] == l || _slotHiWant[j] == l;
            if (!used) return l;
        }
        return -1;
    }

    private static void CubePass(IntPtr scene)
    {
        if (_cubesFailed) return;
        if (!_cubesReady && !CreateCubes()) { _cubesFailed = true; return; }

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _passDt = Math.Clamp((float)(t0 - _lastPassTicks) / System.Diagnostics.Stopwatch.Frequency, 0.001f, 0.1f);
        _lastPassTicks = t0;
        // Which scene does the once-per-frame pass bake from? If the first-firing scene ALTERNATES between
        // presented frames (reflection/aux subframes racing the main view), the cubes bake from two different
        // bucket sets alternately -- shadows visibly "flicker between two states" at low fps.
        if (_lastCubeScene != IntPtr.Zero && scene != _lastCubeScene) _cubeSceneFlips++;
        _lastCubeScene = scene;
        int n = Math.Min(ShadowInputs.LocalLightCount, ShadowInputs.MaxLocalLights);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _cubeFbo);
        Gl.Viewport(0, 0, _cubeSize, _cubeSize);
        _inCubePass = _cullOn;   // arm the per-part cull hooks for this pass only (false when SOWX_LOCALCULL=0)
        _skipPart = false;
        _cullStaticDrawn = _cullDynDrawn = _cullSkipped = _cullHeightSkipped = _cullFaceSkipped = _cullBlendSkipped = 0;
        _slotsStatic = 0; _slotsActive = n; _slotsDynActive = 0;

        // STABLE slot assignment: match each recorded light to a slot by position (record order churns frame to
        // frame -- index-keyed slots re-rendered everything every frame). Unmatched lights claim a free slot, then
        // evict. Unclaimed slots go inert for the receiver (far=0) but keep their cube for a returning light.
        int* slotOf = stackalloc int[ShadowInputs.MaxLocalLights];
        bool* claimed = stackalloc bool[ShadowInputs.MaxLocalLights];
        for (int j = 0; j < ShadowInputs.MaxLocalLights; j++) claimed[j] = false;
        for (int i = 0; i < n; i++)
        {
            slotOf[i] = -1;
            float best = SlotMatchTol * SlotMatchTol;
            for (int j = 0; j < ShadowInputs.MaxLocalLights; j++)
            {
                if (claimed[j] || !_slotValid[j]) continue;
                float d = (ShadowInputs.LocalLightPos[i] - _slotSeenPos[j]).LengthSquared();
                if (d < best) { best = d; slotOf[i] = j; }
            }
            if (slotOf[i] >= 0) claimed[slotOf[i]] = true;
        }
        for (int i = 0; i < n; i++)
        {
            if (slotOf[i] >= 0) continue;
            // Free (invalid) slot first; else evict the LONGEST-unclaimed slot past the hysteresis window.
            // No candidate -> this light waits (stable shadows beat a flip-flopping extra one).
            int pick = -1;
            for (int j = 0; j < ShadowInputs.MaxLocalLights; j++)
                if (!claimed[j] && !_slotValid[j]) { pick = j; break; }
            if (pick < 0)
            {
                int bestAge = EvictAfterFrames;
                for (int j = 0; j < ShadowInputs.MaxLocalLights; j++)
                    if (!claimed[j] && _slotUnclaimedAge[j] >= bestAge) { bestAge = _slotUnclaimedAge[j]; pick = j; }
            }
            if (pick < 0) continue;   // slotOf[i] stays -1
            slotOf[i] = pick;
            claimed[pick] = true;
            _slotValid[pick] = false;   // fresh or evicted claim -> force a static render below
            _slotStageFace[pick] = -1;  // abandon any staged bake of the evicted light
            _slotRes[pick] = 0;         // new light re-derives its LOD from scratch
        }
        for (int j = 0; j < ShadowInputs.MaxLocalLights; j++)
        {
            if (claimed[j]) { _slotUnclaimedAge[j] = 0; continue; }
            _slotWalkPending[j] = false; _slotWalked[j] = false;   // no bake coming for an unclaimed slot
            // Abandon an in-flight staged bake -- its static layer is part-old/part-new, so the slot must NOT
            // keep-alive (the copy below would ship the mix); invalidate so a reclaim full-bakes.
            if (_slotStageFace[j] >= 0) { _slotStageFace[j] = -1; _slotValid[j] = false; }
            _slotDynDirty[j] = false;   // the keep-alive copy below leaves the working cube pure static
            if (!_slotValid[j] || ++_slotUnclaimedAge[j] > KeepAliveFrames) { _cubeFar[j] = 0f; continue; }
            // Keep-alive: cube stays uploaded at its last contents. On the first unclaimed frame, drop the
            // stale DYNAMIC layer (a frozen character silhouette) by re-copying the static layer over it.
            if (_slotUnclaimedAge[j] == 1 && _copyOk && _haveStaticBuckets)
                Gl.CopyImageSubData(StaticTex(j), Gl.TEXTURE_2D_ARRAY, 0, 0, 0, LayerBase(j),
                    WorkTex(j), Gl.TEXTURE_2D_ARRAY, 0, 0, 0, LayerBase(j), SlotSize(j), SlotSize(j), 6);
        }

        int staticBudget = 3;   // static builds per pass: area entry seeds up to 16 slots -- stagger so it never hitches one frame
        int stageFaces = StageFacesPerPass;   // staged (amortized) face renders this pass
        // NEAREST-FIRST processing: while moving, set-churn from far swapped-in lights was eating the static
        // budget and postponing the CARRIED torch's rebake -- its stale bake pos then drifted past the receiver
        // match tolerance and its shadows vanished until the next bake (the "jumpy" torch shadow). The nearest
        // light (the torch) now always wins the budget. Insertion sort, n<=32, alloc-free.
        int* order = stackalloc int[ShadowInputs.MaxLocalLights];
        float* fdist = stackalloc float[ShadowInputs.MaxLocalLights];
        for (int i = 0; i < n; i++)
        {
            float d = _haveFocus ? (ShadowInputs.LocalLightPos[i] - _focus).LengthSquared() : i;
            int k = i;
            while (k > 0 && fdist[k - 1] > d) { order[k] = order[k - 1]; fdist[k] = fdist[k - 1]; k--; }
            order[k] = i; fdist[k] = d;
        }
        int hiBudget = _cubeHiArrTex != 0 ? _hiSlots : 0;
        for (int oi = 0; oi < n; oi++)
        {
            int i = order[oi];
            int j = slotOf[i];
            if (j < 0) continue;   // no slot free (eviction hysteresis) -> this light waits
            Vector3 pos = ShadowInputs.LocalLightPos[i];
            _slotSeenPos[j] = pos;
            // The STATIC layer refreshes only when the light moved > eps (or the slot is new). Both layers render
            // at the refresh pos (_slotPos), so cube contents and the uploaded sampling pos stay aligned.
            float mdx = pos.X - _slotPos[j].X, mdy = pos.Y - _slotPos[j].Y, mdz = pos.Z - _slotPos[j].Z;
            if (_slotCooldown[j] > 0) _slotCooldown[j]--;
            float newFar = MathF.Max(ShadowInputs.LocalLightRange[i] * _rangeMult, _localNear + 1f);
            // Flickering lights (torches) animate their range field, so each bake sampled a random phase and
            // the shadow reach pulsed between rebakes (moving-light edge bands, frozen by stopping). Latch far
            // MONOTONIC while the slot is claimed and refresh once when it grows -- converges to the true max
            // reach and stays; oversizing is harmless by the RANGEMULT design.
            if (_slotValid[j] && _slotFar[j] > newFar) newFar = _slotFar[j];
            // PRIORITY: a light riding the player (carried torch, auras -- within 5u of focus) rebakes FULL and
            // EVERY frame it moves: its shadow must glide, and one light's 6 faces/frame is cheap. Cooldown and
            // the staged (amortized) path apply only to the rest -- which are static and rarely refresh at all.
            bool priority = _haveFocus && fdist[oi] < 25f;
            if (priority && _smoothTau > 0f && _slotValid[j] && _haveFocus)
            {
                // Frame-rate-independent low-pass on the bake position, RELATIVE to the carrier (focus). An
                // absolute EMA mostly filtered the character's own run speed -- the bake pos trailed ~v*tau
                // behind and the torch shadow swung IN FRONT of the runner. Smoothing the carrier-relative
                // offset passes the character's motion through with ZERO lag and damps only the flame-sway
                // wobble (the actual 30fps judder).
                float a = 1f - MathF.Exp(-_passDt / _smoothTau);
                Vector3 rel = pos - _focus;
                _slotSmoothRel[j] += (rel - _slotSmoothRel[j]) * a;
                pos = _focus + _slotSmoothRel[j];
                mdx = pos.X - _slotPos[j].X; mdy = pos.Y - _slotPos[j].Y; mdz = pos.Z - _slotPos[j].Z;
            }
            else _slotSmoothRel[j] = _haveFocus ? pos - _focus : default;
            // Priority lights are NOT eps-quantized: eps 0.05 + the EMA (whose per-frame step at 30fps sits just
            // under it) made the bake land every OTHER frame in double-size steps -- "a different shadow every
            // other frame". 2mm floor = rebake on any real motion; the shadow step then equals the light's own
            // per-frame movement, continuous at any fps. Cost: ~6 faces/frame for the carried light. Non-priority
            // slots keep the coarse eps (they are static; a rebake there is churn, not tracking).
            float eps2 = priority ? 4e-6f : _updateEps * _updateEps;
            bool refresh = !_slotValid[j]
                || ((mdx * mdx + mdy * mdy + mdz * mdz > eps2
                     || newFar > _slotFar[j] * 1.1f) && (_slotCooldown[j] <= 0 || priority));   // relative: flicker maxima are noisy, an absolute +0.5 kept re-triggering full rebakes (jitter)
            // Shadow LOD: pick a target resolution from importance (res ~ K * far / focusDist -- a shadow
            // texel's screen size scales with reach/res/distance, so this holds perceived detail roughly
            // constant), power-of-two with a +/-40% hysteresis band so walking doesn't thrash rebakes. Res above
            // the base face promotes into the hi array (nearest-first order caps it at the pool); LOD changes
            // ride the normal refresh machinery and commit atomically with the bake -- no blink.
            {
                float dfoc = _haveFocus ? MathF.Max(MathF.Sqrt(fdist[oi]), 1f) : 10f;
                float target = _lodK * newFar / dfoc;
                if (priority && target > _priRes) target = _priRes;   // every-frame rebakes cap their raster cost
                int lodMax = _cubeHiArrTex != 0 ? _cubeSizeHi : _cubeSize;
                int wantRes = _slotRes[j];
                if (wantRes <= 0)
                { wantRes = _lodMin; while (wantRes < target && wantRes < lodMax) wantRes <<= 1; }
                else
                {
                    // Band edges must straddle the pow2 step with margin: at 0.7/1.4 the promote and demote
                    // thresholds coincided (both at target=1.43*res) and walking ping-ponged 256<->128 bakes.
                    while (wantRes < target * 0.6f && wantRes < lodMax) wantRes <<= 1;
                    while (wantRes > target * 1.5f && wantRes > _lodMin) wantRes >>= 1;
                }
                bool wantHi = wantRes > _cubeSize && _cubeHiArrTex != 0 && hiBudget > 0 && _recvHasHi;
                if (wantHi && _slotHiWant[j] < 0) _slotHiWant[j] = FreeHiLayer();
                if (wantHi && _slotHiWant[j] < 0) wantHi = false;      // pool full -> best the base face offers
                if (!wantHi) { _slotHiWant[j] = -1; wantRes = Math.Min(wantRes, _cubeSize); }
                // Old shader in the hak (no sowxLocalUvScale) samples full faces -> only full-face resolutions
                // are safe until a receiver proves it understands the corner sub-rect.
                if (!_recvHasUvScale) wantRes = wantHi ? _cubeSizeHi : _cubeSize;
                _slotResWant[j] = wantRes;
                if (_slotResWant[j] != _slotRes[j] || _slotHiWant[j] != _slotHiLayer[j]) refresh = true;
                if (_slotHiLayer[j] >= 0 || _slotHiWant[j] >= 0) hiBudget--;
                if (_slotStageFace[j] >= 0 && (_slotStageHi[j] != _slotHiWant[j] || _slotStageRes[j] != _slotResWant[j]))
                    _slotStageFace[j] = -1;   // retargeted mid-stage -> restart
            }
            // A bake is only valid when the buckets contain this light's casters: either the player-centred
            // walk covered its whole caster sphere, or a dedicated per-light walk ran this frame. Otherwise
            // request one and defer -- baking anyway captures whatever happens to be loaded (position-
            // dependent shadows, the pop-in bug).
            if (refresh && _haveStaticBuckets && _rewalkOn && !_slotWalked[j])
            {
                float statRNew = newFar + _staticFudge;
                bool covered = _haveFocus && (pos - _focus).Length() + statRNew <= _lastWalkR;
                if (!covered)
                {
                    _slotWalkPending[j] = true; _slotWalkPos[j] = pos; _slotWalkR[j] = statRNew;
                    if (!_slotValid[j]) { _cubeFar[j] = 0f; continue; }   // fresh slot: inert until its walk lands
                    refresh = false;                                      // moved light: postpone the static refresh
                }
            }
            if (refresh && staticBudget <= 0)
            {
                if (!_slotValid[j]) { _cubeFar[j] = 0f; continue; }   // fresh slot: build next pass, inert till then
                refresh = false;                                      // moved light: postpone the static refresh
            }
            // VALID slot (moved light): amortize -- start/continue a staged bake instead of a full one. The
            // stage renders at the frozen snapshot pos; the buckets on later staged frames come from the
            // per-populate player walk (a moved light passed the covered check to get here).
            if (refresh && !priority && _slotValid[j] && _copyOk && _haveStaticBuckets)
            {
                if (_slotStageFace[j] < 0)
                { _slotStagePos[j] = pos; _slotStageFar[j] = newFar; _slotStageFace[j] = 0; _slotStageHi[j] = _slotHiWant[j]; _slotStageRes[j] = _slotResWant[j]; _slotWalkPending[j] = false; }
                refresh = false;
            }
            if (refresh && priority) _slotStageFace[j] = -1;   // full bake supersedes any staged one in flight
            bool committed = false;
            if (_slotStageFace[j] >= 0 && stageFaces > 0)
            {
                stageFaces -= 2;
                _curLightPos = _slotStagePos[j];
                float sr = _slotStageFar[j] + _staticFudge;
                _curRangeSqStatic = sr * sr;
                bool stageHi = _slotStageHi[j] >= 0 && _cubeHiStaticArrTex != 0;
                RenderCubeFaces(scene, stageHi ? _cubeHiStaticArrTex : _cubeStaticArrTex,
                    stageHi ? _slotStageHi[j] * 6 : j * 6, _slotStageRes[j] > 0 ? _slotStageRes[j] : _cubeSize,
                    _slotStageFar[j], clear: true, statics: true, dynamics: false,
                    _slotStageFace[j], _slotStageFace[j] + 2);
                if ((_slotStageFace[j] += 2) >= 6)
                {
                    _slotStageFace[j] = -1;
                    _slotPos[j] = _slotStagePos[j];
                    _slotFar[j] = _slotStageFar[j];
                    _slotCooldown[j] = _staticCooldown;
                    if (_slotRes[j] != _slotStageRes[j] || _slotHiLayer[j] != (stageHi ? _slotStageHi[j] : -1))
                        _log?.Info($"ShadowMapService: lod commit (staged) slot {j}: {_slotRes[j]}px hi{_slotHiLayer[j]} -> {_slotStageRes[j]}px hi{(stageHi ? _slotStageHi[j] : -1)} (far={_slotStageFar[j]:F1})");
                    _slotHiLayer[j] = stageHi ? _slotStageHi[j] : -1;   // tier + res commit with the bake
                    _slotRes[j] = _slotStageRes[j];
                    committed = true; _slotsStatic++;
                }
            }
            if (refresh)
            {
                staticBudget--;
                _slotValid[j] = true;
                _slotCooldown[j] = _staticCooldown;
                _slotPos[j] = pos;
                _slotFar[j] = newFar;
                int hiNew = _cubeHiArrTex != 0 ? _slotHiWant[j] : -1;
                if (_slotRes[j] != _slotResWant[j] || _slotHiLayer[j] != hiNew)
                    _log?.Info($"ShadowMapService: lod commit (full) slot {j}: {_slotRes[j]}px hi{_slotHiLayer[j]} -> {_slotResWant[j]}px hi{hiNew} (far={newFar:F1}, dfoc={MathF.Sqrt(fdist[oi]):F1}, pri={priority})");
                _slotHiLayer[j] = hiNew;   // tier + res commit with the bake (renders below target them)
                _slotRes[j] = _slotResWant[j];
                _slotWalkPending[j] = false; _slotWalked[j] = false;   // walk consumed by this bake
            }
            _cubeFar[j] = _slotFar[j];   // receiver linearizes against exactly what was rendered
            _curLightPos = _slotPos[j];
            float dynR = _slotFar[j] + LocalCullFudge;   // dynamics: tight (origin ~ geometry)
            float statR = _slotFar[j] + _staticFudge;    // statics: generous (origin offset from geometry)
            _curRangeSqDyn = dynR * dynR;
            _curRangeSqStatic = statR * statR;

            if (_copyOk && _haveStaticBuckets)
            {
                // Split path: static layer only on refresh; copy it into the working cube; dynamics on top --
                // but ONLY when a dynamic caster is actually within reach (dyn-gate above), else the working
                // cube keeps (or gets one wipe copy back to) the pure static layer and the slot costs nothing.
                float dsig = 0f; int dcnt = 0;
                bool dynNear = _haveDynBuckets && SlotHasDynCasters(scene, _slotPos[j], dynR, out dsig, out dcnt);
                // Re-render the dyn layer only when the in-range gob set actually CHANGED (moved/entered/left)
                // or the static layer under it was rebaked -- stationary placeables no longer keep 31/32 slots
                // paying a copy + 6-face render every frame (the light-heavy-area stutter).
                bool staticFresh = refresh || committed;   // the static layer (or its LOD) changed under the working cube this frame
                bool dynChanged = dynNear && (staticFresh || dsig != _slotDynSig[j] || dcnt != _slotDynCount[j]);
                if (refresh) { RenderCubeFaces(scene, StaticTex(j), LayerBase(j), SlotRes(j), _slotFar[j], clear: true, statics: true, dynamics: false); _slotsStatic++; }
                if (staticFresh || dynChanged || (!dynNear && _slotDynDirty[j]))
                    Gl.CopyImageSubData(StaticTex(j), Gl.TEXTURE_2D_ARRAY, 0, 0, 0, LayerBase(j),
                        WorkTex(j), Gl.TEXTURE_2D_ARRAY, 0, 0, 0, LayerBase(j), SlotSize(j), SlotSize(j), 6);
                if (dynChanged)
                { RenderCubeFaces(scene, WorkTex(j), LayerBase(j), SlotRes(j), _slotFar[j], clear: false, statics: false, dynamics: true); _slotsDynActive++; _slotDynSig[j] = dsig; _slotDynCount[j] = dcnt; }
                _slotDynDirty[j] = dynNear;
            }
            else
            {   // no copy support (or no static buckets): everything into the working cube every frame
                RenderCubeFaces(scene, WorkTex(j), LayerBase(j), SlotRes(j), _slotFar[j], clear: true, statics: _haveStaticBuckets, dynamics: _haveDynBuckets);
            }
        }
        _inCubePass = false;   // disarm before the engine's normal draws resume
        _staticRefreshAccum += _slotsStatic;
        _cubeTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        // Re-emit every ~300 frames so a busy-area cost is visible, not just the first (load-time) pass.
        bool periodic = ++_cubeFrameTick >= 300;
        if (!_cubePassLogged || periodic)
        {
            _cubePassLogged = true; if (periodic) _cubeFrameTick = 0;
            float ms = _cubeTicks * 1000f / System.Diagnostics.Stopwatch.Frequency;
            float depthAvg = _depthFrames > 0 ? _depthTicks * 1000f / System.Diagnostics.Stopwatch.Frequency / _depthFrames : 0f;
            var lod = new System.Text.StringBuilder(64);   // cold path (once/~300 frames) -- alloc is fine
            for (int j = 0; j < ShadowInputs.MaxLocalLights; j++)
                if (_cubeFar[j] > 0f) lod.Append(lod.Length > 0 ? " " : "").Append(j).Append(':').Append(SlotRes(j)).Append(_slotHiLayer[j] >= 0 ? "H" : "").Append('@').Append(_slotUvScaleArr[j].ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            _log?.Info($"ShadowMapService: lod [{lod}] (shaderUvScale={_recvHasUvScale}, shaderHi={_recvHasHi})");
            _log?.Info($"ShadowMapService: cube pass {ms:F2}ms ({n} light(s), static cubes refreshed {_slotsStatic}/{_slotsActive}, dyn-active {_slotsDynActive}/{_slotsActive}, {_cubeSize}px, buckets [{string.Join(",", _localBuckets)}]); casters: static {_cullStaticDrawn}, dynamic {_cullDynDrawn}, culled {_cullSkipped} (height {_cullHeightSkipped}, face {_cullFaceSkipped}, blend {_cullBlendSkipped}); depth pass avg {depthAvg:F2}ms/frame incl cubes, wide renders {_wideRenders}/{_depthFrames} frames, static refreshes {_staticRefreshAccum}/period, scene flips {_cubeSceneFlips}/period"); _cubeSceneFlips = 0;
            _depthTicks = 0; _depthFrames = 0; _wideRenders = 0; _staticRefreshAccum = 0;
        }
    }

    /// <summary>Render one cube's 6 faces from <paramref name="pos"/>=<see cref="_curLightPos"/> into
    /// <paramref name="tex"/>: attach each face to the shared FBO, optionally clear it, and re-drive the selected
    /// bucket kinds (statics {0,1} / dynamics {2,3,4} of <see cref="_localBuckets"/>). <paramref name="clear"/> is
    /// false for the dynamic-on-top pass so the copied static depths survive. Sets <see cref="_curFace"/> for the
    /// per-face cull. Caller holds the FBO/viewport/InDepthPass bracket.</summary>
    private static void RenderCubeFaces(IntPtr scene, uint tex, int layerBase, int size, float far, bool clear, bool statics, bool dynamics,
        int faceStart = 0, int faceEnd = 6)
    {
        Gl.Viewport(0, 0, size, size);   // per-call: hi- and lo-tier faces differ in size
        for (int face = faceStart; face < faceEnd && face < 6; face++)
        {
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, tex, 0, layerBase + face);
            if (clear) Gl.Clear(Gl.DEPTH_BUFFER_BIT);
            if (_skipUpFace && face == 4) continue;   // +Z (up) face only serves above-light fragments -> skip its draws
            _curFace = face;
            PushLightVP(ShadowMath.PointLightFaceViewProj(_curLightPos, face, _localNear, far));
            foreach (int b in _localBuckets)
            {
                bool isStatic = b == 0 || b == 1;
                if (isStatic ? !statics : !dynamics) continue;
                _curBucketStatic = isStatic;   // statics -> generous cull radius; dynamics -> tight
                _skipPart = false;             // fresh verdict at every bucket boundary
                WalkBucket(scene, b, cull: _cullOn);
            }
            RestoreLightTransform();
        }
    }

    /// <summary>Lean replacement for <c>Scene::RenderDrawBucket</c> in the depth passes: iterate the
    /// DrawBucketManager (Scene+0x208) ourselves and cull BEFORE any GL work, so a skipped caster costs one
    /// vtable call + a float compare instead of the engine's full per-part preamble (matrix, shader params, fog,
    /// keyhole dissolve, Tracy zone, debug bbox). Kept parts get exactly the engine's draw: Push +
    /// RenderInterface::SetModelMatrix(part world matrix) for matrix-path parts, base matrix for pre-transformed
    /// (skinned) parts, then the item's Render virtual. Phase cull-state toggles are deliberately dropped -- the
    /// depth pass forces two-sided rendering anyway. ponytail: SetShaderParamv pairs skipped (depth-only; verts
    /// come from vertex data x model matrix) -- restore them if a danglymesh caster ever renders undeformed.</summary>
    private static void WalkBucket(IntPtr scene, int bucket, bool cull)
    {
        if (!_leanWalk) { _renderDrawBucket(scene, bucket); return; }
        IntPtr mgr = (IntPtr)((byte*)scene + SceneDrawBucketMgr);
        int savedMode = *_curMatrix;
        _setMatrixMode(MatWorld);   // Push/Pop act on m_nCurrentMatrix; the model matrix is stack 2
        for (int phase = 0; phase < 3; phase++)
        {
            IntPtr slot = _getFirstItem(mgr, bucket, phase);
            IntPtr item = slot != IntPtr.Zero ? *(IntPtr*)slot : IntPtr.Zero;
            bool pushed = false;
            while (item != IntPtr.Zero)
            {
                IntPtr vt = *(IntPtr*)item;
                IntPtr part = ((delegate* unmanaged<IntPtr, IntPtr>)*(IntPtr*)(vt + VtGetPart))(item);
                bool skip = false;
                if (part != IntPtr.Zero)
                {
                    IntPtr obj = *(IntPtr*)((byte*)part + PartObj);
                    bool matrixPath = obj != IntPtr.Zero
                        && ((*((byte*)obj + PartObjFlags) & 0x1e) != 0
                            || *(IntPtr*)(*(IntPtr*)((byte*)obj + 0x10) + 0x50) != IntPtr.Zero);
                    if (matrixPath)
                    {
                        float* fm = (float*)((byte*)obj + PartObjMatrix);
                        if (cull) skip = CullAt(fm[12], fm[13], fm[14]);
                        if (!skip)
                        {
                            if (!pushed) { _pushMatrix(); pushed = true; }
                            _riSetModel(fm);
                        }
                    }
                    else
                    {
                        if (cull)
                        {
                            float* mn = (float*)((byte*)part + PartBboxMin);
                            float* mx = (float*)((byte*)part + PartBboxMax);
                            skip = CullAt((mn[0] + mx[0]) * 0.5f, (mn[1] + mx[1]) * 0.5f, (mn[2] + mx[2]) * 0.5f);
                        }
                        if (!skip && pushed) { _popMatrix(); pushed = false; }   // world-space verts render under the base matrix
                    }
                }
                if (skip) _cullSkipped++;
                else
                {
                    ((delegate* unmanaged<IntPtr, void>)*(IntPtr*)(vt + VtRender))(item);
                    if (cull) { if (_curBucketStatic) _cullStaticDrawn++; else _cullDynDrawn++; }
                }
                slot = _getNextItem(mgr, bucket, phase);
                item = slot != IntPtr.Zero ? *(IntPtr*)slot : IntPtr.Zero;
            }
            if (pushed) _popMatrix();
        }
        _setMatrixMode(savedMode);
    }

    /// <summary>Create the depth cubes (one per light slot) + the shared FBO + the cube-face debug blit program/
    /// quad. Dirties GL bindings -- the DepthPass bracket covers it. Returns false (and logs) if any FBO is
    /// incomplete or the blit shader fails.</summary>
    private static bool CreateCubes()
    {
        if (Gl.FramebufferTextureLayer == null || Gl.TexImage3D == null)
        { _log?.Info("ShadowMapService: glFramebufferTextureLayer/glTexImage3D missing -- point-light shadows disabled"); return false; }

        _copyOk = Gl.CopyImageSubData != null;
        uint f; Gl.GenFramebuffers(1, &f); _cubeFbo = f;
        _cubeArrTex = CreateDepthArray();
        SetupCubeSamplingState();
        if (_copyOk && _haveStaticBuckets) _cubeStaticArrTex = CreateDepthArray();   // static layer
        if (_hiSlots > 0)
        {
            // DEPTH16: the hi tier's short far planes (~2x light range) don't need 24 bits, and it halves the
            // 2048-tier footprint (a 2048 face is ~50MB/slot/array at 16-bit).
            _cubeHiArrTex = CreateDepthArraySized(_cubeSizeHi, _hiSlots, Gl.DEPTH_COMPONENT16);
            _cubeHiRawView = SetupSamplingState(_cubeHiArrTex, _hiSlots, Gl.DEPTH_COMPONENT16);
            if (_copyOk && _haveStaticBuckets) _cubeHiStaticArrTex = CreateDepthArraySized(_cubeSizeHi, _hiSlots, Gl.DEPTH_COMPONENT16);
        }

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _cubeFbo);
        Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, _cubeArrTex, 0, 0);
        Gl.DrawBuffer(Gl.NONE); Gl.ReadBuffer(Gl.NONE);   // depth-only
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        { _log?.Info($"ShadowMapService: cube FBO incomplete (0x{status:x}) -- point-light shadows disabled"); return false; }

        _cubeBlitProg = BuildProgram(BlitVs, CubeBlitFs);
        if (_cubeBlitProg == 0) { _log?.Info("ShadowMapService: cube blit shader build failed -- point-light shadows disabled"); return false; }
        _uCube = Loc(_cubeBlitProg, "uCube"); _uCubeSlot = Loc(_cubeBlitProg, "uSlot");
        _cubeBlitVao = BuildQuad(0.0f);   // third corner quad, right of the near-map blit

        _cubesReady = true;
        _log?.Info($"ShadowMapService: {ShadowInputs.MaxLocalLights} depth cube(s) ready ({_cubeSize}px/face, " +
                   $"static split {(_copyOk && _haveStaticBuckets ? "on" : "OFF (no glCopyImageSubData or no static buckets)")}, " +
                   $"LOD {_lodMin}..{(_cubeHiArrTex != 0 ? _cubeSizeHi : _cubeSize)}px (K={_lodK:F0}, hi pool {(_cubeHiArrTex != 0 ? $"{_hiSlots}x{_cubeSizeHi}px depth16" : "off")}))");
        return true;
    }

    private static uint CreateDepthArray() => CreateDepthArraySized(_cubeSize, ShadowInputs.MaxLocalLights, Gl.DEPTH_COMPONENT24);

    /// <summary>One depth TEXTURE_2D_ARRAY with 6 layers per cube slot (NEAREST, clamped).</summary>
    private static uint CreateDepthArraySized(int size, int slots, uint fmt)
    {
        uint t; Gl.GenTextures(1, &t);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, t);
        // Immutable storage when available: required for the raw texture VIEW below (glTextureView only
        // accepts TexStorage textures). TexImage3D fallback keeps everything working minus the view.
        if (Gl.TexStorage3D != null)
            Gl.TexStorage3D(Gl.TEXTURE_2D_ARRAY, 1, fmt, size, size, 6 * slots);
        else
            Gl.TexImage3D(Gl.TEXTURE_2D_ARRAY, 0, (int)fmt,
                size, size, 6 * slots, 0, Gl.DEPTH_COMPONENT, Gl.UNSIGNED_INT, null);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        return t;
    }

    /// <summary>Raw (NEAREST, no-compare) texture VIEW of _cubeArrTex for the PCSS blocker probe (unit 10).</summary>
    private static uint _cubeRawView;

    /// <summary>Move the receiver-facing filter/compare state onto the TEXTURE OBJECTS themselves. Sampler
    /// objects bound at hook time proved unreliable at scene-draw time (live taps were binary NEAREST -- the
    /// #65 "chunks + faint dither"); texture-object state travels with the texture no matter what the engine
    /// does to sampler bindings. The working array becomes LINEAR + COMPARE (hardware PCF for
    /// sampler2DArrayShadow); the raw probe gets a NEAREST/no-compare VIEW of the same storage.</summary>
    private static void SetupCubeSamplingState() => _cubeRawView = SetupSamplingState(_cubeArrTex, ShadowInputs.MaxLocalLights, Gl.DEPTH_COMPONENT24);

    private static uint SetupSamplingState(uint tex, int slots, uint fmt)
    {
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, tex);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_COMPARE_MODE, (int)Gl.COMPARE_REF_TO_TEXTURE);
        Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_COMPARE_FUNC, (int)Gl.LEQUAL);
        if (Gl.TextureView != null && Gl.TexStorage3D != null)
        {
            uint v; Gl.GenTextures(1, &v);
            Gl.TextureView(v, Gl.TEXTURE_2D_ARRAY, tex, fmt, 0, 1, 0, (uint)(6 * slots));
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, v);
            Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
            Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
            Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
            Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
            Gl.TexParameteri(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_COMPARE_MODE, (int)Gl.NONE);
            uint e = Gl.GetError != null ? Gl.GetError() : 0;
            _log?.Info($"ShadowMapService: cube compare state on texture object; raw view={v} (err=0x{e:x})");
            return v;
        }
        _log?.Info("ShadowMapService: glTextureView/glTexStorage3D missing -- raw probe shares the compare texture (blocker search degraded)");
        return 0;
    }

    /// <summary>Blit the depth texture to a bottom-left corner quad (UiHost roll-call, after the engine's
    /// draws). Brackets the little state it touches so it can't disturb the other UiHost windows.</summary>
    private static void Draw()
    {
        if (_shadowFires > 1 && !_firesLogged)   // RenderShadows fired more than once this frame = reflection subframes
        { _firesLogged = true; _log?.Info($"ShadowMapService: RenderShadows fired {_shadowFires}x/frame (reflection subframes) -- depth pass uses the FIRST; verify it's the main view"); }
        _shadowFires = 0;
        _haveFocus = false;    // recompute the shared map centre next frame
        _depthCleared = false; _wideCleared = false;   // re-arm the once-per-frame depth clears
        _cubeDoneThisFrame = false; // re-arm the once-per-frame cube pass
        _passDoneThisFrame = false; // re-arm the once-per-frame depth pass (first RenderShadows fire)
        _wideDecided = false;       // re-decide the wide-map refresh next frame
        LogLocalLights();      // slice 6: one-shot dump of the recorded point lights
        ShadowInputs.ResetLocalLights();   // re-arm the per-frame point-light record
        if (!_enabled || !_glReady || _glFailed) return;
        if (!_diag) return;   // the corner map blits are debug-only -- SOWX_SHADOWDIAG=1 shows them
        int prog, vao, atex, tex2d, depthOn = Gl.IsEnabled(Gl.DEPTH_TEST);
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prog);
        Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &vao);
        Gl.GetIntegerv(Gl.ACTIVE_TEXTURE, &atex);
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &tex2d);

        Gl.Disable(Gl.DEPTH_TEST);
        Gl.UseProgram(_blitProg);
        Gl.ActiveTexture(Gl.TEXTURE0);
        // The depth textures now carry compare state; the quad wants raw depth -- override with the NEAREST
        // no-compare sampler object for the duration of the blit.
        if (Gl.BindSampler != null && Gl.GenSamplers != null && Gl.SamplerParameteri != null)
        { EnsureShadowSamplers(); Gl.BindSampler(0, _cubeSampler); }
        Gl.Uniform1i(_uDepth, 0);
        Gl.BindTexture(Gl.TEXTURE_2D, _depthTex);      // wide/static map (left corner quad)
        Gl.BindVertexArray(_quadVao);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
        Gl.BindTexture(Gl.TEXTURE_2D, _depthTexNear);  // near/dynamic map (quad to its right)
        Gl.BindVertexArray(_quadVaoNear);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);

        if (_cubesReady)   // point-light cube 0, face SOWX_LOCALFACE (third quad); sweep the face knob to find casters
        {
            int arrBind; Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D_ARRAY, &arrBind);
            Gl.UseProgram(_cubeBlitProg);
            Gl.ActiveTexture(Gl.TEXTURE0);
            Gl.Uniform1i(_uCube, 0);
            Gl.Uniform1i(_uCubeSlot, _localDebugCube);   // SOWX_LOCALCUBE selects the slot shown
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _cubeRawView != 0 ? _cubeRawView : _cubeArrTex);
            Gl.BindVertexArray(_cubeBlitVao);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, (uint)arrBind);
        }

        if (Gl.BindSampler != null) Gl.BindSampler(0, 0);   // drop the quad's raw-sampler override
        Gl.BindVertexArray((uint)vao);
        Gl.ActiveTexture((uint)atex);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tex2d);
        Gl.UseProgram((uint)prog);
        SetCap(Gl.DEPTH_TEST, depthOn);
    }

    private static void SetCap(uint cap, int on) { if (on == 0) Gl.Disable(cap); else Gl.Enable(cap); }

    /// <summary>Slice 6 diagnostic: on the first frame that records any point light, log the set (pos/range) so
    /// the recorder is provable before any cube pass exists. One-shot -- the string build is cold.</summary>
    private static void LogLocalLights()
    {
        if (_localLoggedOnce || ShadowInputs.LocalLightCount == 0 || _log == null) return;
        _localLoggedOnce = true;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < ShadowInputs.LocalLightCount; i++)
        {
            Vector3 p = ShadowInputs.LocalLightPos[i];
            sb.Append($" [{i}]=({p.X:F1},{p.Y:F1},{p.Z:F1}) r{ShadowInputs.LocalLightRange[i]:F1}");
        }
        _log.Info($"ShadowMapService: {ShadowInputs.LocalLightCount} point light(s) recorded (slice 6, overflow {ShadowInputs.LocalLightOverflow}):{sb}");
    }

    /// <summary>Create both depth FBO/textures (wide + near) + the blit shader/quads (context current). Dirties
    /// GL bindings -- the caller brackets it. Returns false (and logs) if a FBO is incomplete or a shader fails.</summary>
    private static bool CreateObjects()
    {
        if (!CreateDepthTarget(out _depthTex, out _fbo)) return false;
        if (!CreateDepthTarget(out _depthTexNear, out _fboNear)) return false;

        _quadVao = BuildQuad(-0.98f);      // wide map, far-left corner
        _quadVaoNear = BuildQuad(-0.49f);  // near map, just to its right
        _blitProg = BuildProgram(BlitVs, BlitFs);
        if (_blitProg == 0) { _log?.Info("ShadowMapService: blit shader build failed -- disabled"); return false; }
        _uDepth = Loc(_blitProg, "uDepth");

        _glReady = true;
        _log?.Info("ShadowMapService: depth FBOs (wide+near) + blit shader ready");
        return true;
    }

    /// <summary>Create one DEPTH_COMPONENT24 depth-only FBO + its texture at the current map size. Returns false
    /// (and logs) if the framebuffer is incomplete.</summary>
    private static bool CreateDepthTarget(out uint tex, out uint fbo)
    {
        uint t; Gl.GenTextures(1, &t); tex = t;
        Gl.BindTexture(Gl.TEXTURE_2D, tex);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.DEPTH_COMPONENT24, _mapSize, _mapSize, 0, Gl.DEPTH_COMPONENT, Gl.UNSIGNED_INT, null);
        // Receiver-facing state on the TEXTURE object (sampler objects proved unreliable at scene-draw time):
        // LINEAR + depth-compare = hardware PCF for the sampler2DShadow sun taps. The debug quad binds the raw
        // sampler object on unit 0 to keep reading actual depth.
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_COMPARE_MODE, (int)Gl.COMPARE_REF_TO_TEXTURE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_COMPARE_FUNC, (int)Gl.LEQUAL);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);

        uint f; Gl.GenFramebuffers(1, &f); fbo = f;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, tex, 0);
        Gl.DrawBuffer(Gl.NONE); Gl.ReadBuffer(Gl.NONE);   // depth-only FBO
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        { _log?.Info($"ShadowMapService: FBO incomplete (0x{status:x}) -- disabled"); return false; }
        return true;
    }

    // A small debug quad in the bottom-left, its left edge at NDC x0 (pos.xy, uv.xy), two triangles. Returns VAO.
    private static uint BuildQuad(float x0)
    {
        float x1 = x0 + 0.48f, y0 = -0.98f, y1 = -0.50f;
        float* q = stackalloc float[24]
        {
            x0, y0, 0f, 0f,   x1, y0, 1f, 0f,   x1, y1, 1f, 1f,
            x0, y0, 0f, 0f,   x1, y1, 1f, 1f,   x0, y1, 0f, 1f,
        };
        uint a; Gl.GenVertexArrays(1, &a);
        uint b; Gl.GenBuffers(1, &b);
        Gl.BindVertexArray(a);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, b);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(24 * sizeof(float)), q, Gl.STATIC_DRAW);
        Gl.VertexAttribPointer(0, 2, Gl.FLOAT, (byte)Gl.FALSE, 4 * sizeof(float), (void*)0);
        Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(1, 2, Gl.FLOAT, (byte)Gl.FALSE, 4 * sizeof(float), (void*)(2 * sizeof(float)));
        Gl.EnableVertexAttribArray(1);
        Gl.BindVertexArray(0);
        return a;
    }

    private const string BlitVs =
        "#version 330 core\nlayout(location=0) in vec2 aPos;\nlayout(location=1) in vec2 aUV;\nout vec2 vUV;\n" +
        "void main(){ vUV = aUV; gl_Position = vec4(aPos,0.0,1.0); }\n";
    private const string BlitFs =
        "#version 330 core\nin vec2 vUV;\nuniform sampler2D uDepth;\nout vec4 o;\n" +
        "void main(){ float d = texture(uDepth, vUV).r; o = vec4(vec3(d),1.0); }\n";   // greyscale depth

    // Cube-face debug blit: pack the selected slot's 6 array layers into a 3x2 atlas (row0=+X,-X,+Y;
    // row1=-Y,+Z,-Z) -- raw face textures, no direction math needed with the 2D-array layout.
    private const string CubeBlitFs =
        "#version 330 core\nin vec2 vUV;\nuniform sampler2DArray uCube;\nuniform int uSlot;\nout vec4 o;\n" +
        "void main(){\n vec2 g = vUV*vec2(3.0,2.0);\n int col = min(int(g.x),2), row = min(int(g.y),1);\n" +
        " int face = row*3+col;\n vec2 f = fract(g);\n" +
        " o = vec4(vec3(texture(uCube, vec3(f, float(uSlot*6+face))).r),1.0);\n}\n";

    private static uint BuildProgram(string vsSrc, string fsSrc)
    {
        uint vs = Compile(Gl.VERTEX_SHADER, vsSrc), fs = Compile(Gl.FRAGMENT_SHADER, fsSrc);
        if (vs == 0 || fs == 0) return 0;
        uint prog = Gl.CreateProgram();
        Gl.AttachShader(prog, vs); Gl.AttachShader(prog, fs); Gl.LinkProgram(prog);
        int ok; Gl.GetProgramiv(prog, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        return ok != 0 ? prog : 0;
    }

    private static uint Compile(uint type, string src)
    {
        uint sh = Gl.CreateShader(type);
        Span<byte> b = stackalloc byte[src.Length + 1];
        for (int i = 0; i < src.Length; i++) b[i] = (byte)src[i];
        b[src.Length] = 0;
        fixed (byte* p = b)
        {
            byte* pp = p;
            Gl.ShaderSource(sh, 1, &pp, null);
        }
        Gl.CompileShader(sh);
        int ok; Gl.GetShaderiv(sh, Gl.COMPILE_STATUS, &ok);
        if (ok == 0) { _log?.Info($"ShadowMapService: shader compile failed (type=0x{type:x})"); return 0; }
        return sh;
    }

    private static int Loc(uint prog, string name)
    {
        Span<byte> b = stackalloc byte[name.Length + 1];
        for (int i = 0; i < name.Length; i++) b[i] = (byte)name[i];
        b[name.Length] = 0;
        fixed (byte* p = b) return Gl.GetUniformLocation(prog, p);
    }
}
