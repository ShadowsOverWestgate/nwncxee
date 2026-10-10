using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>
/// Client step guard: chains <c>CNWCCreature::WalkUpdateLocation</c>, the per-frame walk step, for the player
/// creature alone -- the desired position is snapshotted, the engine steps it, and the step is re-run through the
/// engine's own door-aware swept test, <c>CNWCArea::TestDirectLine</c> with closed doors solid. A refused step puts
/// the desired position and the model back, so no packet claims the far side of a closed door. Hot, no allocation.
/// </summary>
public sealed unsafe class StepGuardService : IExtenderService
{
    private static ExtenderLog? _log;
    private static WalkUpdateLocationFn* _next;
    private static TestDirectLineFn _testLine;
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);

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
        _testLine = EngineFn.TestDirectLine;
        // chained, not installed: the pose layer's keyboard weight sets the step's rate just outside it (priority 60)
        _next = hooks.ChainWalkUpdateLocation(&OnWalkUpdateLocation, 50);
        if (_next == null) { log.Info("StepGuard: standing down (WalkUpdateLocation chain refused)"); return; }
        KnobSurface.Note(Report);
        log.Info("StepGuard: chained CNWCCreature::WalkUpdateLocation");
    }

    [UnmanagedCallersOnly]
    private static int OnWalkUpdateLocation(IntPtr cre)
    {
        IntPtr mgr = *(IntPtr*)_appMgr;
        IntPtr app = mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
        CClientExoAppInternal intern = app == IntPtr.Zero ? default : new CClientExoApp(app).Internal;
        var creature = new CNWCCreature(cre);
        if (intern.IsNull || new CNWCObject(cre).Id != intern.PlayerCreatureId) return (*_next)(cre);
        if (!_live)
        {
            _live = true;
            _probeOk = EngineLayout.Probe("CNWCCreature", cre, _log);
            _log?.Info(_probeOk ? "StepGuard: first player step -- hook is LIVE" : "StepGuard: layout probe failed -- engine walk untouched");
        }
        IntPtr module = CameraService.Module();
        CNWCArea area = module == IntPtr.Zero ? default : new CNWCModule(module).CurrentArea;
        CPathfindInformation pathfind = creature.Pathfind;
        if (!_probeOk || area.IsNull || pathfind.IsNull) return (*_next)(cre);

        ref Vector3 desired = ref creature.DesiredPos;
        Vector3 before = desired;
        int r = (*_next)(cre);
        Vector3 after = desired;
        if (!IsStep(before, after) || new CNWCModule(module).CurrentArea.Ptr != area.Ptr) return r;
        Span<float> space = pathfind.SpaceArgs;
        // TestDirectLine takes both Vectors BY VALUE: the copies are what VectorArg points Win64 at.
        Vec3 from = Vec(before), to = Vec(after);
        int clear = _testLine(area.Ptr, cre, VectorArg.Of(&from), VectorArg.Of(&to), space[0], space[1], NoCreatures);
        if (clear == 1) { _steps++; return r; }
        desired = before;
        creature.SetPosition(VectorArg.Of(&from));
        if (creature.Driving != 0) _driveRefusals++; else _walkRefusals++;
        return r;
    }

    /// <summary>The engine's own <c>Vector</c> shape, which the by-value call arguments need.</summary>
    private static Vec3 Vec(in Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    /// <summary>A walk step moved the desired point and by less than <see cref="MaxStep"/> in the plane; anything
    /// else (unchanged, or a server rewrite) is not a step to test.</summary>
    private static bool IsStep(in Vector3 a, in Vector3 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, d2 = dx * dx + dy * dy;
        return d2 > 0f && d2 < MaxStep * MaxStep;
    }

    /// <summary>The /knobs note: the refusal ledger split by the driving flag, which answers
    /// whether refusals ever fire on a drive segment or only on waypoint steps.</summary>
    internal static string Report()
        => FormattableString.Invariant(
            $"step guard: {(_live ? _probeOk ? "live" : "probe failed" : "no player step yet")} | {_steps} step(s) passed, {_walkRefusals} walk + {_driveRefusals} drive step(s) refused");

    /// <summary>The step classifier off-device: stationary and teleport-sized moves pass untested, a walk step is tested.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "StepGuard.SelfCheck");
        static Vector3 V(float x, float y, float z) => new(x, y, z);
        check.Expect(!IsStep(V(1, 2, 3), V(1, 2, 3)), "an unchanged desired point is not a step");
        check.Expect(!IsStep(V(1, 2, 3), V(1, 2, 9)), "a height-only change is not a step");
        check.Expect(IsStep(V(1, 2, 3), V(1.05f, 2, 3)), "a centimetre move is a step");
        check.Expect(IsStep(V(0, 0, 0), V(2.8f, 2.8f, 0)), "a 3.96 m move is still a step");
        check.Expect(!IsStep(V(0, 0, 0), V(4, 0, 0)) && !IsStep(V(0, 0, 0), V(50, 50, 0)), "4 m and beyond is a rewrite, not a step");
        log.Info(check.Ok ? "StepGuard.SelfCheck: passed" : "StepGuard.SelfCheck: FAILED");
    }
}
