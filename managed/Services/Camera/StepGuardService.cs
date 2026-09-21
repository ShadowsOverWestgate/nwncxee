using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Services.CombatText;

namespace CX.Services.Camera;

/// <summary>
/// Client step guard (CX-682; docs/research/drive-door-clipping.md §4 fix A). Pre-hooks
/// <c>CNWCCreature::WalkUpdateLocation</c>, the per-frame walk step, for the player creature alone: the desired
/// position (<see cref="EngineLayout.CreatureDesiredPos"/>) is snapshotted, the engine steps it, and the step is
/// re-run through the engine's own door-aware swept test, <c>CNWCArea::TestDirectLine</c> with closed doors solid
/// (the test a click and a drive segment already get -- only from wherever the avatar already is, which is why a
/// waypoint walk crosses a door untested). A refused step puts the desired position and the model back where they
/// were, so no drive packet (or anything else reading the displayed position) can ever claim the far side of a
/// closed door, on any server, with no wire change. A step longer than <see cref="MaxStep"/>, or one across which the
/// module's current area changed, is a teleport or transition rewrite, not a walk, and passes untested. The area is
/// the module's current one (<see cref="EngineLayout.ModuleCurrentArea"/>), the same pointer ClientSideWalkCommand
/// hands the engine's own drive-segment test. The pending waypoints are left alone on a refusal (research §6.5): the
/// avatar pushes at the door until the server's update ends the walk, as the server's own creature does.
/// <para>Hot: once per frame while the player walks, one line test over centimetres (one tile, one door-AABB
/// prefilter, no allocation on pass or on door-blocked), pointer reads otherwise. Other creatures pass straight
/// through on the id compare.</para>
/// </summary>
public sealed unsafe class StepGuardService : IExtenderService
{
    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, int> _orig;
#if WINDOWS
    /// <summary>Win64 passes a 12-byte Vector by value as a pointer to a caller-made copy (#420).</summary>
    private static delegate* unmanaged<IntPtr, IntPtr, CombatTextService.Vec3*, CombatTextService.Vec3*, float, float, int, int> _testLine;
#else
    /// <summary>SysV: each Vector is xmm(x, y) + xmm(z), the pair then the two floats fill xmm0..xmm5.</summary>
    private static delegate* unmanaged<IntPtr, IntPtr, CombatTextService.Vec3, CombatTextService.Vec3, float, float, int, int> _testLine;
#endif
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);

    private static readonly int IdOff = EngineLayout.ObjId;
    private static readonly int InternalOff = EngineLayout.ExoAppInternal;
    private static readonly int PlayerIdOff = EngineLayout.InternalPlayerCreatureId;
    private static readonly int AreaOff = EngineLayout.ModuleCurrentArea;
    private static readonly int DesiredOff = EngineLayout.CreatureDesiredPos;
    private static readonly int DrivingOff = EngineLayout.CreatureDriving;
    private static readonly int PathfindOff = EngineLayout.CreaturePathfind;
    private static readonly int SpaceArgsOff = EngineLayout.PathfindSpaceArgs;
    private static readonly int SetPositionSlot = EngineLayout.CreatureSetPositionSlot;

    /// <summary>The update handler snaps rather than lerps from 4 m; a desired-position jump past it is not a step.</summary>
    private const float MaxStep = 4f;
    /// <summary>TestDirectLine's bCheckCreatures: doors are always solid to it; creatures in the way are not a wall.</summary>
    private const int NoCreatures = 0;

    private static bool _live, _probeOk;
    private static int _steps, _walkRefusals, _driveRefusals;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        CameraKnobs.Declare();
        SelfCheck(log);
        if (!EngineLayout.Require(log, "StepGuard", "CClientExoApp", "CClientExoAppInternal", "CNWCObject", "CNWCCreature", "CNWCModule", "CPathfindInformation")) return;
        if (!SymbolMap.Require(log, "StepGuard", SymbolMap.WalkUpdateLocation, SymbolMap.TestDirectLine, SymbolMap.AppManager, SymbolMap.GetModule)) return;
        _testLine = (delegate* unmanaged<IntPtr, IntPtr,
#if WINDOWS
            CombatTextService.Vec3*, CombatTextService.Vec3*,
#else
            CombatTextService.Vec3, CombatTextService.Vec3,
#endif
            float, float, int, int>)Native.Sym(SymbolMap.TestDirectLine);
        _orig = (delegate* unmanaged<IntPtr, int>)hooks.Install(SymbolMap.WalkUpdateLocation,
                                                               (IntPtr)(delegate* unmanaged<IntPtr, int>)&OnWalkUpdateLocation);
        if (_orig == null) { log.Info("StepGuard: standing down (WalkUpdateLocation hook refused)"); return; }
        KnobSurface.Note(Report);
        log.Info("StepGuard: pre-hooked CNWCCreature::WalkUpdateLocation");
    }

    [UnmanagedCallersOnly]
    private static int OnWalkUpdateLocation(IntPtr cre)
    {
        if (!Walk.Enabled.V) return _orig(cre);
        IntPtr mgr = *(IntPtr*)_appMgr;
        IntPtr app = mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
        IntPtr intern = app == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(app + InternalOff);
        if (intern == IntPtr.Zero || *(uint*)(cre + IdOff) != *(uint*)(intern + PlayerIdOff)) return _orig(cre);
        if (!_live)
        {
            _live = true;
            _probeOk = EngineLayout.Probe("CNWCCreature", cre, _log);
            _log?.Info(_probeOk ? "StepGuard: first player step -- hook is LIVE" : "StepGuard: layout probe failed -- engine walk untouched");
        }
        IntPtr module = CameraService.Module();
        IntPtr area = module == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(module + AreaOff);
        IntPtr pathfind = *(IntPtr*)(cre + PathfindOff);
        if (!_probeOk || area == IntPtr.Zero || pathfind == IntPtr.Zero) return _orig(cre);

        CombatTextService.Vec3* desired = (CombatTextService.Vec3*)(cre + DesiredOff);
        CombatTextService.Vec3 before = *desired;
        int r = _orig(cre);
        CombatTextService.Vec3 after = *desired;
        if (!IsStep(before, after) || *(IntPtr*)(module + AreaOff) != area) return r;
        float* space = (float*)(pathfind + SpaceArgsOff);
#if WINDOWS
        int clear = _testLine(area, cre, &before, &after, space[0], space[1], NoCreatures);
#else
        int clear = _testLine(area, cre, before, after, space[0], space[1], NoCreatures);
#endif
        if (clear == 1) { _steps++; return r; }
        *desired = before;
        SetPosition(cre, before);
        if (*(int*)(cre + DrivingOff) != 0) _driveRefusals++; else _walkRefusals++;
        return r;
    }

    /// <summary>A walk step moved the desired point and by less than <see cref="MaxStep"/> in the plane; anything
    /// else (unchanged, or a server rewrite) is not a step to test.</summary>
    private static bool IsStep(CombatTextService.Vec3 a, CombatTextService.Vec3 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, d2 = dx * dx + dy * dy;
        return d2 > 0f && d2 < MaxStep * MaxStep;
    }

    private static void SetPosition(IntPtr cre, CombatTextService.Vec3 pos)
    {
        IntPtr fn = *(IntPtr*)(*(IntPtr*)cre + SetPositionSlot);
#if WINDOWS
        ((delegate* unmanaged<IntPtr, CombatTextService.Vec3*, void>)fn)(cre, &pos);
#else
        ((delegate* unmanaged<IntPtr, CombatTextService.Vec3, void>)fn)(cre, pos);
#endif
    }

    /// <summary>The /knobs note and the group's status: the refusal ledger split by the driving flag, which answers
    /// whether refusals ever fire on a drive segment (§6.2) or only on waypoint steps.</summary>
    internal static string Report()
        => FormattableString.Invariant(
            $"step guard: {(Walk.Enabled.V ? "on" : "off")} | {(_live ? _probeOk ? "live" : "probe failed" : "no player step yet")} | {_steps} step(s) passed, {_walkRefusals} walk + {_driveRefusals} drive step(s) refused");

    /// <summary>The step classifier off-device: stationary and teleport-sized moves pass untested, a walk step is tested.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "StepGuard.SelfCheck");
        static CombatTextService.Vec3 V(float x, float y, float z) => new() { X = x, Y = y, Z = z };
        check.Expect(!IsStep(V(1, 2, 3), V(1, 2, 3)), "an unchanged desired point is not a step");
        check.Expect(!IsStep(V(1, 2, 3), V(1, 2, 9)), "a height-only change is not a step");
        check.Expect(IsStep(V(1, 2, 3), V(1.05f, 2, 3)), "a centimetre move is a step");
        check.Expect(IsStep(V(0, 0, 0), V(2.8f, 2.8f, 0)), "a 3.96 m move is still a step");
        check.Expect(!IsStep(V(0, 0, 0), V(4, 0, 0)) && !IsStep(V(0, 0, 0), V(50, 50, 0)), "4 m and beyond is a rewrite, not a step");
        log.Info(check.Ok ? "StepGuard.SelfCheck: passed" : "StepGuard.SelfCheck: FAILED");
    }
}
