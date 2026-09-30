using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// CX-107: installs the ported <c>CAurBehavior</c> solvers. One funchook CHAIN per concrete engine
/// <c>Control</c>, at the lowest priority in the chain -- i.e. where the trampoline would be -- so the port
/// replaces the engine's solve while every outer layer still runs untouched. That is what keeps
/// <see cref="CX.Services.Camera.CameraService"/> (#536, priority 10) working with the owned solver both on
/// and off: it still chains on <c>CAurBehaviorCameraFollow::Control</c>, still runs after the solve, and
/// still reads the same behaviour fields, because the port writes them.
/// </summary>
/// <remarks>
/// <para><b>The gate is the boot-time renderer choice</b> (<c>alloy.enabled</c>, ADR 0010: restart-only),
/// not the per-frame draw verdict. Solvers have to run on frames Alloy refuses to draw and in GUI scenes --
/// the GUI-scale attach and the subwindow viewport are GUI-only -- so nothing here asks
/// <c>AlloyGate.Alloy</c>.</para>
/// <para><b>The A/B is <c>behav.owned</c></b>, and it is Live rather than Restart: both paths are always
/// installed (the chain link is there either way), so handing a frame back to the engine's solvers costs one
/// static bool read and needs no relaunch. That makes it usable as an in-place comparison, which a
/// restart-only toggle is not. The gate the handlers actually read is
/// <see cref="BehaviorSolvers.Driving"/> -- armed AND the knob -- because a hook the chain accepted is
/// installed whether or not CX then stands the feature down.</para>
/// <para>Thirteen handlers and no table-driven dispatch: an <c>[UnmanagedCallersOnly]</c> entry point cannot
/// be generic or captured, so each class gets its own static and forwards to
/// <see cref="Dispatch"/> with its index.</para>
/// </remarks>
public sealed unsafe class BehaviorService : IExtenderService
{
    /// <summary>Chained BELOW every other subscriber. <see cref="CX.Services.Camera.CameraService"/> takes 10
    /// on the follow solve and post-adjusts the answer; the port has to produce that answer first.</summary>
    private const int Priority = -100;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        BehaviorSolvers.Bind(log);
        BehaviorMath.SelfCheck(log);
        BehaviorSolvers.SelfCheck(log);
        if (!Renderer.Enabled.Boot)
        {
            BehaviorSolvers.StandDown("");   // the plain config choice, not a failure: nothing to say loudly
            return;
        }
        if (!EngineLayout.Require(log, "BehaviorSolvers", "CAurObject", "CAurBehavior", "CAurBehaviorAttach",
                                  "CAurBehaviorCrossSceneAttach", "CAurBehaviorSubwindow", "CAurBehaviorNavigate",
                                  "CAurBehaviorCameraFollow", "CAurBehaviorCameraOnAStick", "Gob"))
        {
            BehaviorSolvers.StandDown("STOOD DOWN -- an engine offset the solvers read has no value for this platform");
            return;
        }
        if (!BehaviorCall.Resolve(log))
        {
            BehaviorSolvers.StandDown("STOOD DOWN -- an engine symbol the solvers call did not resolve");
            return;
        }

        IntPtr[] handlers =
        {
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnBase,
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnAttach,
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnAttachTranslate,
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnAttachRotate,
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnAttachNoOrientation,
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnAttachGuiScale,
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnCrossSceneAttach,
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnCrossSceneAttachOffset,
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnSubwindow,
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnNavigate,
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnCameraFollow,
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnCameraLooseFollow,
            (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&OnCameraOnAStick,
        };
        int wired = 0;
        for (int i = 0; i < BehaviorSolvers.Symbols.Length; i++)
            if (BehaviorSolvers.Wire(i, hooks.Chain(BehaviorSolvers.Symbols[i], handlers[i], Priority))) wired++;
        if (wired != BehaviorSolvers.Symbols.Length)
        {
            // Partly hooked is the one state that must never ship: a behaviour whose Control still runs the
            // engine's solve while its siblings run the port would place attached objects inconsistently.
            // The wiring is NOT undone -- Chain() has already registered every link it accepted and Commit()
            // will install it, so the handlers we asked for run whatever we decide here. Standing down is
            // therefore exactly "every handler forwards to the engine", which is what the kept slots do.
            BehaviorSolvers.StandDown($"STOOD DOWN -- only {wired} of {BehaviorSolvers.Symbols.Length} Control hooks chained");
            return;
        }
        BehaviorSolvers.Arm();
        FrameRegistry.Global.Register(FramePhase.PostPresent, 55, BehaviorSolvers.OnFrameEnd, "BehaviorSolvers", log);
        KnobSurface.Note(BehaviorSolvers.Report);
        log.Info($"BehaviorSolvers: chained {wired} CAurBehavior::Control implementations at priority {Priority}");
    }

    /// <summary>Every handler forwards here; the decision itself lives in
    /// <see cref="BehaviorSolvers.Dispatch"/>, where a self-check can reach it.</summary>
    private static int Dispatch(int k, IntPtr self, float dt) => BehaviorSolvers.Dispatch(k, self, dt);

    [UnmanagedCallersOnly] private static int OnBase(IntPtr s, float dt) => Dispatch(0, s, dt);
    [UnmanagedCallersOnly] private static int OnAttach(IntPtr s, float dt) => Dispatch(1, s, dt);
    [UnmanagedCallersOnly] private static int OnAttachTranslate(IntPtr s, float dt) => Dispatch(2, s, dt);
    [UnmanagedCallersOnly] private static int OnAttachRotate(IntPtr s, float dt) => Dispatch(3, s, dt);
    [UnmanagedCallersOnly] private static int OnAttachNoOrientation(IntPtr s, float dt) => Dispatch(4, s, dt);
    [UnmanagedCallersOnly] private static int OnAttachGuiScale(IntPtr s, float dt) => Dispatch(5, s, dt);
    [UnmanagedCallersOnly] private static int OnCrossSceneAttach(IntPtr s, float dt) => Dispatch(6, s, dt);
    [UnmanagedCallersOnly] private static int OnCrossSceneAttachOffset(IntPtr s, float dt) => Dispatch(7, s, dt);
    [UnmanagedCallersOnly] private static int OnSubwindow(IntPtr s, float dt) => Dispatch(8, s, dt);
    [UnmanagedCallersOnly] private static int OnNavigate(IntPtr s, float dt) => Dispatch(9, s, dt);
    [UnmanagedCallersOnly] private static int OnCameraFollow(IntPtr s, float dt) => Dispatch(10, s, dt);
    [UnmanagedCallersOnly] private static int OnCameraLooseFollow(IntPtr s, float dt) => Dispatch(11, s, dt);
    [UnmanagedCallersOnly] private static int OnCameraOnAStick(IntPtr s, float dt) => Dispatch(12, s, dt);
}
