using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Services.Camera;
using static Sowx.Framework.EngineLayout;

namespace Sowx.Alloy;

/// <summary>#537: occlusion fade instead of the camera pull-in. The engine's answer to a wall between the
/// camera and the player is <c>UpdateNormalCameraCollision</c> zooming in; this keeps the distance and
/// screen-doors the occluders instead. Per frame, a fan of <see cref="OccRays"/> parallel segments from the
/// camera focus to the camera position is marched through <c>Scene::HitCheck</c> (the same engine test the
/// pull-in uses, <see cref="OccHitsPerRay"/> hits each, so at most 15 calls), and every hit is remembered
/// as a point plus the gob it landed on. The route walk then tags a tile part whose enrolment box holds a
/// hit point, or a gob part whose gob was hit, with a coverage that eases toward <see cref="_occFloor"/>;
/// the record carries it in <c>rot.z</c> (|value| in (0,1), see <c>SowxKeyhole</c>), the part rides the
/// keyhole prepass sweep so its depth has the same holes, and the vis-ID target and the caster pass see it
/// whole. Creatures are never faded; the player's own gob is on the hit record's ignore list.
/// <para>The engine's pull-in is switched off by <see cref="CameraFlagDisableCollision"/> in
/// <see cref="ModuleCameraFlags"/> -- the same client field the server's <c>SetCameraFlags</c> lands in.
/// Only the bit this session set is ever cleared, and a value that already holds is not rewritten.</para></summary>
public sealed unsafe partial class AlloyRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct HitVec { public float X, Y, Z; }

    private const int OccRays = 5, OccHitsPerRay = 3, OccMaxHits = OccRays * OccHitsPerRay;
    /// <summary>Nudge past a hit before re-casting, the engine's own 0.001 in UpdateNormalCameraCollision.</summary>
    private const float OccNudge = 0.001f;
    private const float OccRadiusBoot = 0.5f, OccFloorBoot = 0.25f, OccRateBoot = 8f;

    private static bool _occFadeMode = true, _occArmed;
    private static float _occRadius = OccRadiusBoot, _occFloor = OccFloorBoot, _occRate = OccRateBoot;
    private static bool _occWeSetFlag;
    private static long _occLastTick;
    private static string _occWhy = "never ran";

    private static delegate* unmanaged<IntPtr, HitVec, HitVec, HitVec*, IntPtr, int> _sceneHitCheck;
    private static delegate* unmanaged<IntPtr, int, long, int, void> _hitInfoReset;
    private static IntPtr* _occScene, _occCamera;
    private static IntPtr _occAppMgr;
    private static delegate* unmanaged<IntPtr, IntPtr> _occGetModule;
    /// <summary>The CHitInfo, allocated once: a hit links it into the gob's SafePointer list, so it must
    /// outlive the frame and never move. The ignore array follows it.</summary>
    private static byte* _occInfo;

    /// <summary>This frame's hits: world point and the gob (zero for a tile BSP part).</summary>
    private static readonly float[] _occHitPt = new float[OccMaxHits * 3];
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
        _occFadeMode = Settings.Text("camera.occlude", "fade") != "pullin";
        _sceneHitCheck = (delegate* unmanaged<IntPtr, HitVec, HitVec, HitVec*, IntPtr, int>)Native.Sym(SymbolMap.SceneHitCheck);
        _hitInfoReset = (delegate* unmanaged<IntPtr, int, long, int, void>)Native.Sym(SymbolMap.HitInfoReset);
        _occScene = (IntPtr*)Native.Sym(SymbolMap.CurrentScene);
        _occCamera = (IntPtr*)Native.Sym(SymbolMap.CurrentCamera);
        _occAppMgr = Native.Sym(SymbolMap.AppManager);
        _occGetModule = (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.GetModule);
        _occArmed = _sceneHitCheck != null && _hitInfoReset != null && _occScene != null && _occCamera != null
                    && _occAppMgr != IntPtr.Zero && _occGetModule != null && _runtime != null;
        if (_occArmed) _occInfo = (byte*)NativeMemory.AllocZeroed(SizeOfHitInfo + 8);
        _occWhy = _occArmed ? "armed" : "symbols unresolved";
        log.Info($"AlloyRenderer: /camera occlude {(_occFadeMode ? "fade" : "pullin")} ({_occWhy})");
        CameraCommand.Register("occlude", "fade|pullin, radius N (ray fan half-width), floor N (alpha floor) -- occluders fade instead of the camera pulling in (#537)", OccludeCommand);
        CameraCommand.Report(() => $"[camera] occlude {(_occFadeMode ? "fade" : "pullin")} radius {_occRadius:F2} floor {_occFloor:F2} " +
                                   $"({_occWhy}; {(_occFrames == 0 ? 0 : _occCalls / _occFrames)} casts, {(_occFrames == 0 ? 0 : _occTagged / _occFrames)} parts tagged per frame)");
    }

    private static string OccludeCommand(string args)
    {
        int sp = args.IndexOf(' ');
        string key = sp < 0 ? args : args[..sp];
        string val = sp < 0 ? "" : args[(sp + 1)..].Trim();
        switch (key)
        {
            case "fade": _occFadeMode = true; return "[camera] occlude fade" + (_occArmed ? "" : $" ({_occWhy})");
            case "pullin": _occFadeMode = false; return "[camera] occlude pullin (engine collision)";
            case "radius":
            case "floor":
                if (!float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return $"[camera] occlude {key} <number>";
                if (key == "radius") _occRadius = Math.Clamp(v, 0f, 5f); else _occFloor = Math.Clamp(v, 0f, 1f);
                return $"[camera] occlude {key} {v:F2}";
            default: return "[camera] occlude fade|pullin|radius N|floor N";
        }
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

    /// <summary>Once per frame, before the route walk: settle the collision flag, then cast the fan.</summary>
    private static void OccludeFrame()
    {
        _occHitN = 0;
        OccludeTick();
        if (!_occArmed) return;
        IntPtr module = OccludeModule();
        if (module != IntPtr.Zero)
        {
            int* flags = (int*)(module + ModuleCameraFlags);
            if (_occFadeMode) { if ((*flags & CameraFlagDisableCollision) == 0) { *flags |= CameraFlagDisableCollision; _occWeSetFlag = true; } }
            else if (_occWeSetFlag) { *flags &= ~CameraFlagDisableCollision; _occWeSetFlag = false; }
        }
        if (!_occFadeMode) return;
        IntPtr scene = *_occScene;
        if (scene == IntPtr.Zero || *(IntPtr*)(scene + SceneBspRoot) == IntPtr.Zero) return;
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
        *(IntPtr*)(_occInfo + SizeOfHitInfo) = player;
        for (int r = 0; r < OccRays; r++)
        {
            Vector3 o = r switch
            {
                1 => right * _occRadius, 2 => -right * _occRadius, 3 => up * _occRadius, 4 => -up * _occRadius, _ => Vector3.Zero,
            };
            Vector3 from = focus + o, to = cam + o;
            for (int h = 0; h < OccHitsPerRay; h++)
            {
                // mode 0 = BSP parts then gobs; -0x40000001 and box 0 are the camera's own arguments
                _hitInfoReset((IntPtr)_occInfo, 0, -0x40000001L, 0);
                *(IntPtr**)(_occInfo + HitInfoIgnoreList) = (IntPtr*)(_occInfo + SizeOfHitInfo);
                *(int*)(_occInfo + HitInfoIgnoreCount) = 1;
                HitVec hit;
                _occCalls++;
                if (_sceneHitCheck(scene, new HitVec { X = from.X, Y = from.Y, Z = from.Z }, new HitVec { X = to.X, Y = to.Y, Z = to.Z }, &hit, (IntPtr)_occInfo) == 0) break;
                IntPtr gob = *(IntPtr*)(_occInfo + HitInfoGob);
                if (gob == IntPtr.Zero || *(int*)(gob + GobExternalType) != GobKindCreature)
                {
                    int n = _occHitN++;
                    _occHitPt[n * 3] = hit.X; _occHitPt[n * 3 + 1] = hit.Y; _occHitPt[n * 3 + 2] = hit.Z;
                    _occHitGob[n] = gob;
                }
                from = new Vector3(hit.X, hit.Y, hit.Z) + d * OccNudge;
                if (Vector3.Dot(to - from, d) <= 0f) break;
            }
        }
        // the SafePointer link the last hit left behind: unlinked now, not when the gob dies mid-frame
        _hitInfoReset((IntPtr)_occInfo, 0, -0x40000001L, 0);
    }

    /// <summary>Route-walk verdict for one opaque/cutout part: ease its fade toward "hit this frame" and
    /// say whether it draws faded at all. Bounded by <see cref="OccMaxHits"/> compares per part.</summary>
    private static bool OccludeVerdict(int i)
    {
        float f = _occFade[i];
        if (_occHitN == 0 && f == 0f) return false;
        bool hit = false;
        if (_occHitN > 0 && _oLit[i])
        {
            IntPtr gob = _oGob[i];
            if (gob != IntPtr.Zero)
            {
                for (int n = 0; n < _occHitN && !hit; n++) hit = _occHitGob[n] == gob;
            }
            else
            {
                int b = i * 6;
                for (int n = 0; n < _occHitN && !hit; n++)
                {
                    int p = n * 3;
                    hit = _occHitGob[n] == IntPtr.Zero
                          && _occHitPt[p] >= _oBox[b] - OccNudge && _occHitPt[p] <= _oBox[b + 3] + OccNudge
                          && _occHitPt[p + 1] >= _oBox[b + 1] - OccNudge && _occHitPt[p + 1] <= _oBox[b + 4] + OccNudge
                          && _occHitPt[p + 2] >= _oBox[b + 2] - OccNudge && _occHitPt[p + 2] <= _oBox[b + 5] + OccNudge;
                }
            }
        }
        float target = hit ? 1f - _occFloor : 0f;
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
        _occStep = Math.Clamp(Math.Min(dt, 0.1f) * _occRate, 0f, 1f);
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
