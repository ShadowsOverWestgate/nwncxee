using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Services.Camera;
using static CX.Framework.EngineLayout;

namespace CX.Alloy;

/// <summary>#537: occlusion fade instead of the camera pull-in. The engine's answer to a wall between the
/// camera and the player is <c>UpdateNormalCameraCollision</c> zooming in; this keeps the distance and
/// screen-doors the occluders instead. Per frame, a fan of <see cref="OccRays"/> parallel segments from the
/// camera focus to the camera position is marched through <c>Scene::HitCheck</c> (the same engine test the
/// pull-in uses, <see cref="OccHitsPerRay"/> hits each, so at most 15 calls), and every hit is remembered
/// as a point plus the gob it landed on. The route walk then tags a tile part whose enrolment box holds a
/// hit point, or a gob part whose gob was hit, with a coverage that eases toward <c>walls.floor</c>;
/// only placeables are faded (tiles, doors and creatures draw whole -- a tile hit still stops the ray fan);
/// the record carries it in <c>rot.z</c> (|value| in (0,1), see <c>CxKeyhole</c>), the part rides the
/// keyhole prepass sweep so its depth has the same holes, and the vis-ID target and the caster pass see it
/// whole. Creatures are never faded; the player's own gob is on the hit record's ignore list.
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

    private static bool _occArmed;
    private static long _occLastTick;
    private static string _occWhy = "never ran";

    private static IntPtr* _occCamera;
    private static IntPtr _occAppMgr;
    private static delegate* unmanaged<IntPtr, IntPtr> _occGetModule;

    /// <summary>This frame's hits: the placeable gob each ray landed on.</summary>
    private static readonly IntPtr[] _occHitGob = new IntPtr[OccMaxHits];
    private static int _occHitN;
    /// <summary>Per slot: how far toward the floor the part is faded, 0 = not at all. Zero is the array's
    /// default, so a fresh slot needs no seeding and a re-tenanted one is cleared in UnstampChase.</summary>
    private static readonly float[] _occFade = new float[MaxOwned];
    /// <summary>Per slot, this pass: the keyhole verdict the route walk took before the occluder tag joined
    /// it, so the staged <c>rot.z</c> can keep the aperture's own gate (its sign).</summary>
    private static readonly bool[] _occKey = new bool[MaxOwned];
    private static long _occFrames, _occCalls, _occTagged;

    private static void OccludeInit(ExtenderLog log)
    {
        CameraKnobs.Declare();
        SceneRay.Init();
        _occCamera = (IntPtr*)Native.Sym(SymbolMap.CurrentCamera);
        _occAppMgr = Native.Sym(SymbolMap.AppManager);
        _occGetModule = (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetModule);
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
        IntPtr cam = *_occCamera;
        if (cam == IntPtr.Zero) return IntPtr.Zero;
        IntPtr host = *(IntPtr*)(cam + CameraBehaviorHost);
        if (host == IntPtr.Zero) return IntPtr.Zero;
        var asFollow = (delegate* unmanaged<IntPtr, IntPtr>)(*(IntPtr*)(*(IntPtr*)host + BehaviorAsFollowSlot));
        IntPtr behavior = asFollow == null ? IntPtr.Zero : asFollow(host);
        return behavior == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(behavior + BehaviorTargetObj);
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
        Vector3 focus = *(Vector3*)(_runtime + RtCamFocus), cam = FrameInputs.CameraPos;
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
    /// say whether it draws faded at all. Bounded by <see cref="OccMaxHits"/> compares per part.</summary>
    private static bool OccludeVerdict(int i)
    {
        float f = _occFade[i];
        bool hit = false;
        if (_occHitN > 0 && _oLit[i])
        {
            IntPtr gob = _oGob[i];
            if (gob != IntPtr.Zero)
                for (int n = 0; n < _occHitN && !hit; n++) hit = _occHitGob[n] == gob;
        }
        if (!hit && f == 0f) return false;
        float target = hit ? 1f - Walls.Floor.V : 0f;
        f += (target - f) * _occStep;
        if (MathF.Abs(target - f) < 0.005f) f = target;
        _occFade[i] = f;
        if (f > 0f) _occTagged++;
        return f > 0f;
    }

    private static float _occStep;

    /// <summary>The easing step for this frame, from wall-clock dt (clamped so a hitch does not snap).</summary>
    private static void OccludeTick()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _occLastTick == 0 ? 1f / 60f : (float)((now - _occLastTick) / (double)Stopwatch.Frequency);
        _occLastTick = now;
        _occStep = Math.Clamp(Math.Min(dt, 0.1f) * OccRate, 0f, 1f);
    }

    /// <summary>The <c>rot.z</c> the record carries: the occluder's coverage when faded (signed by the keyhole
    /// verdict), otherwise the plain dissolve flag.</summary>
    private static float OccludeDissolve(int i, bool keyhole)
    {
        float f = _occFade[i];
        if (f <= 0f) return keyhole ? 1f : 0f;
        float cov = Math.Clamp(1f - f, 0.01f, 0.99f);
        return keyhole ? cov : -cov;
    }
}
