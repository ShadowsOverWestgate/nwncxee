using System;
using System.Diagnostics;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;

namespace CX.Alloy;

/// <summary>#537: occlusion fade instead of the camera pull-in. The engine's answer to a wall between the
/// camera and the player is <c>UpdateNormalCameraCollision</c> zooming in; this keeps the distance and
/// screen-doors the occluders instead. Per frame, a fan of <see cref="OccRays"/> parallel segments from the
/// camera focus to the camera position is marched through <c>Scene::HitCheck</c> (the same engine test the
/// pull-in uses, <see cref="OccHitsPerRay"/> hits each, so at most 15 calls), and every hit is remembered
/// as a point plus the gob it landed on. The route walk then tags a tile part whose enrolment box holds a
/// hit point, or a gob part whose gob was hit, with a fade that eases its alpha toward <c>walls.floor</c>;
/// only placeables are faded (tiles, doors and creatures draw whole -- a tile hit still stops the ray fan).
/// The faded alpha sends the part into the blended (OIT) run, as a fading tile's does, so it is truly
/// translucent rather than screen-doored; its class is untouched, so the caster pass sees it whole.
/// Creatures are never faded; the player's own gob is on the hit record's ignore list.
/// <para>The engine's pull-in is switched off by <see cref="CameraFlagDisableCollision"/> in
/// <see cref="ModuleCameraFlags"/> -- the same client field the server's <c>SetCameraFlags</c> lands in.
/// Only the bit this session set is ever cleared, and a value that already holds is not rewritten.</para>
/// <para>The knobs are the <see cref="Walls"/> group (spec §4.4), read live; the fade rate stays a const.</para></summary>
public sealed unsafe partial class AlloyRenderer
{
    private const int OccRays = 5, OccHitsPerRay = 3, OccMaxHits = OccRays * OccHitsPerRay;
    private const float OccNudge = SceneRay.Nudge;
    /// <summary>The fade's ease rate per second (a knob would only ever be set to this).</summary>
    private const float OccRate = 8f;
    /// <summary>Seconds a part counts as hit after its last ray hit: a ray grazing an edge flips hit/miss
    /// frame to frame, and without the hold the screen-door re-thresholds every frame (flicker).</summary>
    private const float OccHold = 0.25f;

    private static bool _occArmed;
    private static long _occLastTick;
    private static string _occWhy = "never ran";

    private static IntPtr* _occCamera;
    private static IntPtr _occAppMgr;
    private static GetModuleFn _occGetModule;

    /// <summary>This frame's hits: the placeable gob each ray landed on.</summary>
    private static readonly IntPtr[] _occHitGob = new IntPtr[OccMaxHits];
    private static int _occHitN;
    /// <summary>Per slot: how far toward the floor the part is faded, 0 = not at all. Zero is the array's
    /// default, so a fresh slot needs no seeding and a re-tenanted one is cleared in UnstampChase.</summary>
    private static readonly float[] _occFade = new float[MaxOwned];
    /// <summary>Per slot: seconds of <see cref="OccHold"/> left since the last hit; cleared with the fade.</summary>
    private static readonly float[] _occHeld = new float[MaxOwned];
    private static long _occFrames, _occCalls, _occTagged;

    private static void OccludeInit(ExtenderLog log)
    {
        CameraKnobs.Declare();
        SceneRay.Init();
        _occCamera = (IntPtr*)Native.Sym(SymbolMap.CurrentCamera);
        _occAppMgr = Native.Sym(SymbolMap.AppManager);
        _occGetModule = EngineFn.GetModule;
        _occArmed = SceneRay.Armed && _occCamera != null && _occAppMgr != IntPtr.Zero && _occGetModule != null && _runtime != null;
        _occWhy = _occArmed ? "armed" : "symbols unresolved";
        log.Info($"AlloyRenderer: walls.fade {(Walls.Fade.V ? "on" : "off")} ({_occWhy})");
        Walls.G.Status = () => FormattableString.Invariant(
            $"fade {(Walls.Fade.V ? "on" : "off")} radius {Walls.Radius.V:F2} floor {Walls.Floor.V:F2} ({_occWhy}; ")
            + $"{(_occFrames == 0 ? 0 : _occCalls / _occFrames)} casts, {(_occFrames == 0 ? 0 : _occTagged / _occFrames)} parts tagged per frame)"
            + FormattableString.Invariant($" | arm {CameraService.ArmFraction:0.00} (margin {Walls.Margin.V:0.##} in {Walls.ArmIn.V:0.#} out {Walls.ArmOut.V:0.#} radius {Walls.ArmRadius.V:0.##})");
    }

    /// <summary>The CNWCModule, through the engine's own chain (the UiHostService gate's derefs).</summary>
    private static IntPtr OccludeModule()
    {
        IntPtr mgr = *(IntPtr*)_occAppMgr;
        if (mgr == IntPtr.Zero) return IntPtr.Zero;
        IntPtr app = *(IntPtr*)mgr;
        return app == IntPtr.Zero ? IntPtr.Zero : _occGetModule(app);
    }

    /// <summary>The followed gob (the PC), ManageSceneBSP's chain as <c>SceneInputs</c> walks it.</summary>
    private static IntPtr OccludeTargetGob()
    {
        var cam = new Camera(*_occCamera);
        if (cam.IsNull) return IntPtr.Zero;
        CAurBehavior hv = cam.BehaviorHost;
        if (hv.IsNull) return IntPtr.Zero;
        CAurBehaviorCameraFollow behavior = hv.AsBehaviorCameraFollowFn == IntPtr.Zero ? default : hv.AsBehaviorCameraFollow();
        return behavior.IsNull ? IntPtr.Zero : behavior.Target;
    }

    /// <summary>The fade wants the engine's pull-in off; <see cref="CameraService.SettleCollision"/> is the
    /// one writer of the flag, so a spring-arm session ending cannot hand the pull-in back mid-fade.</summary>
    public static bool OccludeFadeWanted => _occArmed && Walls.Fade.V;

    /// <summary>Once per frame, before the route walk: settle the collision flag, then cast the fan.</summary>
    private static void OccludeFrame()
    {
        _occHitN = 0;
        OccludeTick();
        if (!_occArmed) return;
        CameraService.SettleCollision(OccludeModule());
        if (!Walls.Fade.V) return;
        IntPtr scene = SceneRay.WorldScene();
        if (scene == IntPtr.Zero) return;
        IntPtr player = OccludeTargetGob();
        if (player == IntPtr.Zero) return;
        _occFrames++;
        Vector3 focus = Rt.CamFocus, cam = FrameInputs.CameraPos;
        Vector3 d = cam - focus;
        float len = d.Length();
        if (len < 0.05f) return;
        d /= len;
        Vector3 right = Vector3.Cross(d, Vector3.UnitZ);
        if (right.LengthSquared() < 1e-6f) right = Vector3.UnitX; else right = Vector3.Normalize(right);
        Vector3 up = Vector3.Cross(right, d);
        float radius = Walls.Radius.V;
        for (int r = 0; r < OccRays; r++)
        {
            Vector3 o = r switch
            {
                1 => right * radius, 2 => -right * radius, 3 => up * radius, 4 => -up * radius, _ => Vector3.Zero,
            };
            Vector3 from = focus + o, to = cam + o;
            for (int h = 0; h < OccHitsPerRay; h++)
            {
                _occCalls++;
                if (!SceneRay.Cast(scene, from, to, player, out Vector3 hit, out IntPtr gob)) break;
                if (SceneRay.IsPlaceable(gob))
                {
                    int n = _occHitN++;
                    _occHitGob[n] = gob;
                }
                from = hit + d * OccNudge;
                if (Vector3.Dot(to - from, d) <= 0f) break;
            }
        }
        SceneRay.Release();
    }

    /// <summary>Route-walk verdict for one opaque/cutout part: ease its fade toward "hit this frame" and
    /// return it (0 = solid; the caller multiplies alpha by 1 - fade). Bounded by <see cref="OccMaxHits"/>
    /// compares per part.</summary>
    private static float OccludeVerdict(int i)
    {
        float f = _occFade[i];
        bool hit = false;
        if (_occHitN > 0 && _oLit[i])
        {
            IntPtr gob = _oGob[i];
            if (gob != IntPtr.Zero)
                for (int n = 0; n < _occHitN && !hit; n++) hit = _occHitGob[n] == gob;
        }
        if (hit) _occHeld[i] = OccHold;
        else if (_occHeld[i] > 0f) { _occHeld[i] -= _occDt; hit = true; }
        if (!hit && f == 0f) return 0f;
        float target = hit ? 1f - Walls.Floor.V : 0f;
        f += (target - f) * _occStep;
        if (MathF.Abs(target - f) < 0.005f) f = target;
        _occFade[i] = f;
        if (f > 0f) _occTagged++;
        return f;
    }

    private static float _occStep, _occDt;

    /// <summary>The easing step for this frame, from wall-clock dt (clamped so a hitch does not snap).</summary>
    private static void OccludeTick()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _occLastTick == 0 ? 1f / 60f : (float)((now - _occLastTick) / (double)Stopwatch.Frequency);
        _occLastTick = now;
        _occDt = Math.Min(dt, 0.1f);
        _occStep = Math.Clamp(_occDt * OccRate, 0f, 1f);
    }
}
