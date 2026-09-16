using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Framework;
using CX.Services.Chat.Core;

namespace CX.Services.Camera;

/// <summary>
/// The action camera's two targeting aids, and the crosshair's one engine read. Not a service: it is the
/// action camera's, so <see cref="MouseLookService"/> owns its lifetime (<see cref="Init"/>) and its knobs are
/// the <c>mouselook</c> group's.
/// <para><b>Aim assist</b> (<c>mouselook.assist</c>): once per frame, with the action camera held, the area's
/// own object list (<c>CNWCArea+0x318</c>, the list <c>HiliteAreaObjects</c> walks) is projected against the
/// live camera and the creature / placeable / door nearest the screen centre within <c>mouselook.assistpx</c>
/// wins. Its screen point is then what <see cref="MouseLookService"/> forwards mouse events at instead of the
/// exact centre, so the ENGINE's own pick, hover, cursor, click and targeting all run there -- we bias the
/// question, never answer it, and a candidate behind a wall simply picks nothing, exactly as the crosshair
/// would have. The camera is not moved.</para>
/// <para><b>Auto-target</b> (<c>mouselook.autotarget</c>): <c>CActionTarget::ActivateTargetMode</c> is the one
/// funnel every "now pick a target" mode reaches; post-hooked, it calls <c>HandleLMouseDown</c> straight back
/// so the feat/spell/item resolves on what the crosshair points at with no second click. A mode whose
/// allowed-target list holds only the synthetic ground type (an AoE placement) is left up to be aimed.</para>
/// <para><b>Readiness is split.</b> The crosshair's cursor read stands on the cursor-manager rows alone, so it
/// survives an assist or auto-target refusal: a stood-down assist still leaves a contextual reticle.</para>
/// <para>Per-frame and per-event work allocates nothing: the walk keeps one best candidate in locals, and the
/// result is a single packed <see cref="long"/> so the event path reads the point without tearing.</para>
/// </summary>
internal static unsafe class AimAssist
{
    private static ExtenderLog? _log;
    /// <summary>The walk and the auto-target hook are up. <see cref="_cursorReady"/> is the crosshair's own,
    /// narrower gate: the cursor-manager chain only.</summary>
    private static bool _ready, _cursorReady;

    private static readonly delegate* unmanaged<IntPtr, uint, IntPtr> _getGameObject =
        (delegate* unmanaged<IntPtr, uint, IntPtr>)Native.Sym(SymbolMap.GetGameObject);
    private static delegate* unmanaged<IntPtr, void> _activateOrig, _lmouse;

    private static readonly int ModuleCameraOff = EngineLayout.ModuleCamera;
    private static readonly int ModuleAreaOff = EngineLayout.ModuleCurrentArea;
    private static readonly int AreaIdsOff = EngineLayout.AreaObjectIds;
    private static readonly int AreaCountOff = EngineLayout.AreaObjectCount;
    private static readonly int AsObjSlot = EngineLayout.GameObjAsNWCObjectSlot;
    private static readonly int ObjTypeOff = EngineLayout.ObjType;
    private static readonly int ObjPosOff = EngineLayout.ObjPosition;
    private static readonly int ObjVisibleOff = EngineLayout.ObjVisible;
    private static readonly int ObjHiliteOff = EngineLayout.ObjHighlightable;
    private static readonly int AsPlaceableSlot = EngineLayout.GameObjAsNWCPlaceableSlot;
    private static readonly int CamPosOff = EngineLayout.CamPos;
    private static readonly int CamRotOff = EngineLayout.CamRot;
    private static readonly int CamFovOff = EngineLayout.CamViewAngle;
    private static readonly int CamNearOff = EngineLayout.CamNear;
    private static readonly int CamViewWOff = EngineLayout.CamViewW;
    private static readonly int CamViewHOff = EngineLayout.CamViewH;
    private static readonly int InternalOff = EngineLayout.ExoAppInternal;
    private static readonly int PlayerIdOff = EngineLayout.InternalPlayerCreatureId;
    private static readonly int HoveredOff = EngineLayout.CreatureHoveredOid;
    private static readonly int CursorMgrOff = EngineLayout.InternalCursorManager;
    private static readonly int CursorIdOff = EngineLayout.CursorCurrentId;
    private static readonly int AllowedOff = EngineLayout.ActionTargetAllowed;
    private static readonly int AllowedCountOff = EngineLayout.ActionTargetAllowedCount;

    /// <summary>Object type bytes worth aiming at. The walk's fourth type, item (6, the loose-loot class the
    /// pickup cursor is for), has no constant on purpose: loot is picked by pointing, not by aiming.</summary>
    private const byte TypeCreature = 5, TypePlaceable = 9, TypeDoor = 0xa;
    /// <summary>The synthetic allowed-target type meaning "a point on the ground" (AoE placement).</summary>
    private const int GroundTargetType = 2;
    /// <summary>Metres added to the object's ground position for the aim point -- there is no cheap height
    /// field on CNWCObject, and the crosshair tolerance is pixels wide. ponytail: the engine's own projected
    /// bubble anchor would be exact, but it exists only for objects the nameplate feature has already touched
    /// and lands in a different screen space (bottom-left, camera viewport, GUI-scaled).</summary>
    private const float CreatureLift = 1.0f, PlaceableLift = 0.5f, DoorLift = 1.0f;
    /// <summary>Pixels of screen distance one metre of range is worth when the candidates are scored, so a near
    /// object beats a far one that happens to sit dead centre. ponytail: this IS the whole occlusion story --
    /// nothing tests line of sight, so a target behind a wall still scores (it simply picks nothing when the
    /// engine's own pick runs there); the upgrade path is a SceneRay eye-to-candidate check on the best few.</summary>
    private const float AssistDepthPxPerM = 3f;
    /// <summary>A view angle at or below this is taken as a radian half-angle rather than degrees -- the same
    /// hedge <c>ShadowMath.ViewBasis</c> carries, so a stray build cannot project 57x too wide.</summary>
    private const float FovRadianCeiling = 3.2f;

    /// <summary>No candidate. Not a valid packed point (x = -1).</summary>
    private const long NoPoint = -1L;
    /// <summary>This frame's assist point, packed <c>(x &lt;&lt; 32) | (uint)y</c> so the event path reads both
    /// halves in one aligned load rather than catching a half-updated pair.</summary>
    private static long _point = NoPoint;

    /// <summary>Backing field of <see cref="AssistTarget"/>; written by the frame tick, read from the event
    /// path and the crosshair draw, so both ends go through <see cref="Volatile"/>.</summary>
    private static uint _target;

    /// <summary>The object id the assist picked this frame, 0 when none. Read by the crosshair.</summary>
    internal static uint AssistTarget => Volatile.Read(ref _target);

    private static int _hits, _fires, _scanned;

    /// <summary>Arm the aim assist and the auto-target. From <see cref="MouseLookService"/>'s Initialize, which
    /// has already gated on its own symbols; the offsets and the two CActionTarget symbols are gated here, and
    /// a refusal leaves the action camera itself untouched (plain centre pick, plain crosshair). The cursor read
    /// behind <see cref="CursorId"/> is gated first and on its own rows, so it outlives every refusal below it.</summary>
    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _cursorReady = EngineLayout.Require(log, "MouseLook.crosshair", "CClientExoApp", "CClientExoAppInternal",
                                            "CCachedMouseCursorManager")
                       && SymbolMap.Require(log, "MouseLook.crosshair", SymbolMap.AppManager);
        if (_getGameObject == null) return;
        if (!EngineLayout.Require(log, "MouseLook.assist", "CNWCObject", "CNWCArea", "CNWCCreature", "CGameObject",
                                  "CClientExoAppInternal", "CCachedMouseCursorManager", "CActionTarget",
                                  "Camera", "CNWCModule", "CClientExoApp")) return;
        if (!SymbolMap.Require(log, "MouseLook.assist", SymbolMap.ActivateTargetMode, SymbolMap.ActionTargetLMouseDown,
                               SymbolMap.GetGameObject, SymbolMap.GetModule, SymbolMap.AppManager)) return;

        _lmouse = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.ActionTargetLMouseDown);
        _activateOrig = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.ActivateTargetMode,
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnActivateTargetMode);
        _ready = _activateOrig != null && _lmouse != null;
        if (!_ready) { log.Info("MouseLook.assist: standing down (ActivateTargetMode install refused)"); return; }

        FrameRegistry.Global.Register(FramePhase.PostPresent, 60, Tick, "AimAssist", log);
        log.Info("MouseLook.assist: aim assist ticking per frame; auto-target hooks CActionTarget::ActivateTargetMode");
    }

    /// <summary>This frame's assist point, or false for "aim at the exact centre". One aligned load.</summary>
    internal static bool Point(out int x, out int y)
    {
        long p = Volatile.Read(ref _point);
        if (p == NoPoint) { x = 0; y = 0; return false; }
        x = (int)(p >> 32); y = (int)p;
        return true;
    }

    /// <summary>The live mouse cursor id the engine last set (<c>CCachedMouseCursorManager+0x1cb0</c>), or 0
    /// when the chain is not up. A field read, no hook: the engine's own per-frame
    /// <c>UpdateMouseCursorAppearance</c> keeps it current, including while a targeting mode overrides it. On
    /// <see cref="_cursorReady"/> alone: the crosshair does not want the assist's rows or its hook.</summary>
    internal static int CursorId()
    {
        if (!_cursorReady) return 0;
        IntPtr app = CameraService.App();
        if (app == IntPtr.Zero) return 0;
        IntPtr intern = *(IntPtr*)(app + InternalOff);
        if (intern == IntPtr.Zero) return 0;
        IntPtr mgr = *(IntPtr*)(intern + CursorMgrOff);
        return mgr == IntPtr.Zero ? 0 : *(int*)(mgr + CursorIdOff);
    }

    // ---- the per-frame walk ----

    private static void Clear() { Volatile.Write(ref _target, 0); Volatile.Write(ref _point, NoPoint); }

    /// <summary>One frame of aim assist: project every plausible object in the area against the live camera and
    /// keep the best-scoring one inside <c>mouselook.assistpx</c> -- screen distance from the centre plus range
    /// at <see cref="AssistDepthPxPerM"/>, so the near candidate wins a tie. A hot tick (it runs every frame the
    /// action camera is held) that allocates nothing: one best candidate in locals, two volatile writes out.
    /// <para>The basis is read off the Camera: the stored quaternion is (w, x, y, z) and is the view-&gt;world
    /// rotation, so eye space looks down -Z exactly as Alloy's FrameRead reads it, and the projection is the
    /// engine's own SetPerspectiveTransform terms -- never <c>Camera::MapToScreen</c>, whose by-value Vector
    /// return is the sret order that diverges between the two ABIs (#456). Anything nearer than the near plane
    /// is dropped rather than mirrored onto the screen, and the placeable's hilite flag is read through
    /// <c>AsNWCPlaceable</c> (a null answer = not one) where every other class's sits on the CNWCObject
    /// itself.</para></summary>
    private static void Tick()
    {
        if (!_ready || !MouseLookService.ActionHeld || !MouseLook.Assist.V || MouseLook.AssistPx.V <= 0) { Clear(); return; }

        IntPtr module = CameraService.Module();
        IntPtr app = CameraService.App();
        if (module == IntPtr.Zero || app == IntPtr.Zero) { Clear(); return; }
        IntPtr cam = *(IntPtr*)(module + ModuleCameraOff);
        IntPtr area = *(IntPtr*)(module + ModuleAreaOff);
        IntPtr intern = *(IntPtr*)(app + InternalOff);
        if (cam == IntPtr.Zero || area == IntPtr.Zero || intern == IntPtr.Zero) { Clear(); return; }

        MouseLookService.Centre(out int cx, out int cy);
        float fov = *(float*)(cam + CamFovOff);
        if (cx <= 0 || cy <= 0 || !(fov > 0f)) { Clear(); return; }

        int n = *(int*)(area + AreaCountOff);
        uint* ids = *(uint**)(area + AreaIdsOff);
        if (n <= 0 || ids == null) { Clear(); return; }

        float* p = (float*)(cam + CamPosOff);
        float* q = (float*)(cam + CamRotOff);
        Vector3 eye = new(p[0], p[1], p[2]);
        Quaternion rot = new(q[1], q[2], q[3], q[0]);
        Vector3 fwd = Vector3.Transform(new Vector3(0f, 0f, -1f), rot);
        Vector3 right = Vector3.Transform(new Vector3(1f, 0f, 0f), rot);
        Vector3 up = Vector3.Transform(new Vector3(0f, 1f, 0f), rot);
        float vw = *(float*)(cam + CamViewWOff), vh = *(float*)(cam + CamViewHOff);
        float aspect = vh > 0f ? vw / vh : (float)cx / cy;
        float tanHalf = MathF.Tan(fov * (fov > FovRadianCeiling ? MathF.PI / 360f : 0.5f));
        float near = *(float*)(cam + CamNearOff);
        if (!(near > 0f)) near = 0.1f;

        uint playerId = *(uint*)(intern + PlayerIdOff);
        float radius2 = MouseLook.AssistPx.V * MouseLook.AssistPx.V;
        float best = float.MaxValue;
        long bestPoint = NoPoint;
        uint bestId = 0;

        for (int i = 0; i < n; i++)
        {
            uint id = ids[i];
            if (id == playerId || id == TypingSignal.ObjectInvalid) continue;
            IntPtr go = _getGameObject(app, id);
            if (go == IntPtr.Zero) continue;
            IntPtr obj = ((delegate* unmanaged<IntPtr, IntPtr>)(*(IntPtr*)(*(IntPtr*)go + AsObjSlot)))(go);
            if (obj == IntPtr.Zero || *(byte*)(obj + ObjVisibleOff) == 0) continue;
            byte type = *(byte*)(obj + ObjTypeOff);
            float lift;
            switch (type)
            {
                case TypeCreature: lift = CreatureLift; break;
                case TypePlaceable: lift = PlaceableLift; break;
                case TypeDoor: lift = DoorLift; break;
                default: continue;
            }
            IntPtr hil = type != TypePlaceable ? obj
                : ((delegate* unmanaged<IntPtr, IntPtr>)(*(IntPtr*)(*(IntPtr*)obj + AsPlaceableSlot)))(obj);
            if (hil == IntPtr.Zero || *(int*)(hil + ObjHiliteOff) == 0) continue;
            float* w = (float*)(obj + ObjPosOff);
            Vector3 d = new Vector3(w[0], w[1], w[2] + lift) - eye;
            float ez = Vector3.Dot(d, fwd);
            if (ez <= near) continue;
            float sx = cx + Vector3.Dot(d, right) / (ez * tanHalf * aspect) * cx;
            float sy = cy - Vector3.Dot(d, up) / (ez * tanHalf) * cy;
            float dx = sx - cx, dy = sy - cy, d2 = dx * dx + dy * dy;
            if (d2 >= radius2) continue;
            float score = MathF.Sqrt(d2) + ez * AssistDepthPxPerM;
            if (score >= best) continue;
            best = score; bestId = id; bestPoint = ((long)(int)sx << 32) | (uint)(int)sy;
        }

        _scanned = n;
        // The id first: the event path reads the point, so a torn pair must never name a target the point misses.
        Volatile.Write(ref _target, bestId);
        Volatile.Write(ref _point, bestPoint);
        if (bestId != 0) _hits++;
    }

    // ---- auto-target ----

    /// <summary>Post-hook: the mode is up and registered, so resolving it now takes exactly the path a click
    /// would. Nothing reads the CActionTarget after this returns -- every caller of ActivateTargetMode calls it
    /// last, and HandleLMouseDown touches no member after the fire callback that may delete the subclass.
    /// ponytail: the engine's own <c>s_bJustCastSelfTargetSpells</c>/<c>Items</c> statics would cover the
    /// self-target case without a mode at all; they are unexported data on both builds, so they stay unread.</summary>
    [UnmanagedCallersOnly]
    private static void OnActivateTargetMode(IntPtr self)
    {
        _activateOrig(self);
        if (!_ready || self == IntPtr.Zero || !MouseLook.AutoTarget.V || !MouseLookService.ActionHeld) return;
        uint hovered = HoveredOid();
        if (!TakesAnObject(self) || hovered == 0 || hovered == TypingSignal.ObjectInvalid) return;
        _fires++;
        _lmouse(self);
    }

    /// <summary>Whether this mode accepts an OBJECT and not only a ground point: a spell whose only allowed
    /// target is the synthetic type 2 is an AoE that wants aiming, so it is left up.</summary>
    private static bool TakesAnObject(IntPtr self)
    {
        int n = *(int*)(self + AllowedCountOff);
        int* a = *(int**)(self + AllowedOff);
        if (a == null) return false;
        for (int i = 0; i < n; i++) if (a[i * 2] != GroundTargetType) return true;   // stride 8: {type, cursor}
        return false;
    }

    /// <summary>The id under the cursor, mirrored onto the player creature by the engine's own
    /// <c>UpdateMouseCursorAppearance</c>; <see cref="TypingSignal.ObjectInvalid"/>, or 0 before the field has
    /// ever been written, when nothing is hovered. Under the action camera the engine's cursor IS the crosshair
    /// (or the assist point), so this is what it points at.</summary>
    private static uint HoveredOid()
    {
        IntPtr cre = CameraService.PlayerCreature();
        return cre == IntPtr.Zero ? TypingSignal.ObjectInvalid : *(uint*)(cre + HoveredOff);
    }

    /// <summary>The <c>/mouselook</c> fragment.</summary>
    internal static string Report() => !_ready ? "assist off (stood down)"
        : FormattableString.Invariant($"assist {(MouseLook.Assist.V ? $"{MouseLook.AssistPx.V}px" : "off")}, {_hits} hit(s) over {_scanned} object(s); auto-target {(MouseLook.AutoTarget.V ? "on" : "off")}, {_fires} fire(s)");
}
