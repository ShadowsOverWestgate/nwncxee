using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class BehaviorSolvers
{
    private static bool Near(float a, float b, float eps = 2e-4f) => MathF.Abs(a - b) < eps;
    private static bool Near(in Vector3 a, in Vector3 b, float eps = 2e-4f) => (a - b).Length() < eps;

    /// <summary>
    /// The solvers' arithmetic against the decompilation, off-device. A solve itself cannot run here (it
    /// reads engine memory through engine vtables), so what is pinned is every number a solve derives: the
    /// zoom curve, the lag factor both the focus lerp and the orientation slerp share, the rig offset, the
    /// GUI-scale division, the viewport rounding, the navigate basis and the Euler slot order the rotate
    /// attach feeds <c>YawPitchRoll</c>.
    /// </summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        int n = 0;
        void Expect(bool cond, string what) { n++; check.Expect(cond, what); }

        // ---- the camera solve's three integrators ----
        Expect(Zoom(8f, 0f, 1f / 60f) == 8f, "no zoom input leaves the distance alone");
        Expect(Near(Zoom(8f, 2f, 1f), 4f), "zoom velocity 2 for one second halves the distance");
        Expect(Near(Zoom(8f, -2f, 1f), 16f), "a negative zoom velocity doubles it");
        Expect(Near(Zoom(8f, 2f, 0.5f), 8f * MathF.Pow(2f, -0.5f)), "the curve is continuous in dt");

        Expect(Lag(1f, 0f) == 0f, "dt 0 moves nothing");
        Expect(Near(Lag(1f, 1f), 0.5f), "rate 1 closes half the gap in one second");
        Expect(Near(Lag(1f, 2f), 0.75f), "...and three quarters in two -- frame-rate independent by construction");
        Expect(Near(Lag(2f, 0.5f), 0.5f), "rate scales the clock");

        var focus = new Vector3(10f, 20f, 0f);
        var target = new Vector3(14f, 20f, 1f);
        var off = new Vector3(0f, 0f, 1f);                 // the constructor's default: chest height
        Expect(Near(FocusStep(focus, target, off, 0f), focus), "a lag factor of 0 holds the focus");
        Expect(Near(FocusStep(focus, target, off, 1f), target + off), "a lag factor of 1 snaps to target + offset");
        Expect(Near(FocusStep(focus, target, off, 0.5f), new Vector3(12f, 20f, 1f)),
                     "half way is the midpoint of focus and target + offset");

        // ---- the rig: focus + dist * (q * +Z) ----
        Expect(Near(Rig(focus, 8f, BehaviorMath.YawPitchRoll(0f, 90f, 0f)), new Vector3(10f, 12f, 0f)),
                     "pitch 90 puts the camera 8 units south of the focus");
        Expect(Near(Rig(focus, 8f, BehaviorMath.YawPitchRoll(0f, 0f, 0f)), new Vector3(10f, 20f, 8f)),
                     "pitch 0 puts it 8 units above");
        Expect(Near(Rig(focus, 0f, BehaviorMath.YawPitchRoll(33f, 44f, 0f)), focus),
                     "distance 0 is the focus itself");

        // ---- the rotate attach's Euler slots: the field is a Vector and the engine feeds it (z, x, y) ----
        var euler = new Vector3(0f, 0f, 90f);              // z only -> a pure yaw
        Expect(Near(BehaviorMath.Yaw(BehaviorMath.YawPitchRoll(euler.Z, euler.X, euler.Y)), 90f),
                     "m_vRotation.z is the yaw");
        euler = new Vector3(25f, 0f, 0f);
        Expect(Near(BehaviorMath.Pitch(BehaviorMath.YawPitchRoll(euler.Z, euler.X, euler.Y)), 25f),
                     "m_vRotation.x is the pitch");
        euler = new Vector3(0f, 25f, 0f);
        Expect(Near(BehaviorMath.Roll(BehaviorMath.YawPitchRoll(euler.Z, euler.X, euler.Y)), 25f),
                     "m_vRotation.y is the roll");

        // ---- GUI scale: x and y only ----
        var p = new Vector3(100f, 50f, 7f);
        Expect(Near(GuiScaled(p, 1f), p), "a scale of 1 changes nothing");
        Expect(Near(GuiScaled(p, 2f), new Vector3(50f, 25f, 7f)), "x and y are divided, z is not");

        // ---- the subwindow viewport ----
        Expect(ViewportSpan(0f, 6.4f) == 640, "a 6.4-unit span is 640 pixels at 0.01 per unit");
        Expect(ViewportSpan(1.23f, 1.23f) == 1, "a collapsed span is one pixel, never zero");
        Expect(ViewportSpan(5f, 1f) == 1, "an inverted span is one pixel too");
        Expect(ViewportSpan(0f, 0.01005f) == 1 && ViewportSpan(0f, 0.0155f) == 2, "the span rounds half-up");

        // ---- the navigate basis ----
        (Vector3 fwd, Vector3 right) = NavBasis(0f);
        Expect(Near(fwd, new Vector3(0f, 1f, 0f)), "yaw 0 walks +Y");
        Expect(Near(right, new Vector3(1f, 0f, 0f)), "...and strafes +X");
        (fwd, right) = NavBasis(90f);
        Expect(Near(fwd, new Vector3(-1f, 0f, 0f)), "yaw 90 walks -X");
        Expect(Near(right, new Vector3(0f, 1f, 0f)), "...and strafes +Y");
        (fwd, right) = NavBasis(37f);
        Expect(Near(Vector3.Dot(fwd, right), 0f) && Near(fwd.Length(), 1f) && Near(right.Length(), 1f),
                     "the basis stays orthonormal at an arbitrary yaw");
        Expect(Near(fwd, BehaviorMath.Rotate(BehaviorMath.YawPitchRoll(37f, 0f, 0f), new Vector3(0f, 1f, 0f))),
                     "forward is the yaw quaternion applied to +Y -- the same convention as the orientation write");

        // ---- the weak-reference bookkeeping, on synthetic memory ----
        BehaviorCall.SafePtr probe = default;
        Expect((byte*)&probe.Ptr - (byte*)&probe == 0 && (byte*)&probe.Index - (byte*)&probe == 8
               && sizeof(BehaviorCall.SafePtr) == 0x10,
               "a SafePointer is { object at +0, index at +8 } in 0x10 bytes -- the two fields the engine writes");
        Expect(EngineLayout.AurObjBackRefs == 8 && EngineLayout.AurObjBackRefCount == 0x10,
                     "the back-reference list is the CExoArrayList at CAurObject+0x08");
        Expect(EngineLayout.BehaviorDeps == 8 && EngineLayout.BehaviorDepCount == 0x10,
                     "a behaviour's dependency list is the same shape at +0x08");
        Expect(EngineLayout.BehaviorOwner == 0x18 && EngineLayout.BehaviorTarget == 0x20,
                     "owner at +0x18, the parent SafePointer at +0x20");
        Expect(Unregisters(log), "Unregister swaps the last entry in and fixes its stored index");

        // ---- the stand-down path, against a probe standing in for the engine's Control ----
        // The CX-102 review's defect, in two halves: the gate read only the knob (so a stood-down feature
        // solved anyway), and the stand-down threw the chain slots away (so the hand-back answered 1 without
        // calling anything and every attach quietly froze). Both are checkable here.
        //   The class under test is loose-follow on purpose: it is the ONE case whose Solve() never touches
        // `self`, so a regression that routes a stood-down call into the port shows up as a failed check
        // rather than a null dereference. `_solves` is what tells the two routes apart -- Solve() counts,
        // Engine() does not -- and both routes end at the same probe, so the answer alone cannot.
        const int k = (int)BehaviorKind.CameraLooseFollow;
        Expect(!Driving, "nothing is armed yet, so the port is not driving");
        IntPtr link = (IntPtr)(delegate* unmanaged<IntPtr, float, int>)&Probe;
        Wire(k, &link);
        StandDown("");                  // the silent form: covers the path without a spurious log line
        _probeCalls = 0;
        _solves[k] = 0;
        int answer = Dispatch(k, IntPtr.Zero, 1f / 60f);
        Wire(k, null);
        Expect(answer == ProbeAnswer && _probeCalls == 1,
               "stood down, a hooked Control still reaches the engine's own link and returns ITS answer");
        Expect(_solves[k] == 0, "...and is not routed through the port, whatever behav.owned says");
        _solves[k] = 0;
        _looseWarned = false;

        log.Info(check.Ok ? $"BehaviorSolvers.SelfCheck: passed ({n} checks)" : "BehaviorSolvers.SelfCheck: FAILED");
    }

    /// <summary>What the engine's <c>Control</c> stands in as for the stand-down check: a distinctive answer
    /// the caller cannot have fabricated, and a call counter.</summary>
    private const int ProbeAnswer = 0x5107;   // not-an-offset: a distinctive answer only the probe can return
    private static int _probeCalls;

    /// <inheritdoc cref="ProbeAnswer"/>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int Probe(IntPtr self, float dt)
    {
        _probeCalls++;
        return ProbeAnswer;
    }

    /// <summary>The one piece of engine bookkeeping that is testable without the engine: a three-entry
    /// back-reference list, our entry in the middle, removed. The list is a bare
    /// <c>CExoArrayList</c> -- pointer, count, capacity -- so a synthetic one is three fields.</summary>
    private static bool Unregisters(ExtenderLog log)
    {
        // A fake CAurObject: only the list at +0x08 and the count at +0x10 are touched.
        byte* obj = stackalloc byte[0x20];
        BehaviorCall.SafePtr** items = stackalloc BehaviorCall.SafePtr*[3];
        BehaviorCall.SafePtr s0 = new() { Ptr = (IntPtr)obj, Index = 0 };
        BehaviorCall.SafePtr mine = new() { Ptr = (IntPtr)obj, Index = 1 };
        BehaviorCall.SafePtr s2 = new() { Ptr = (IntPtr)obj, Index = 2 };
        items[0] = &s0; items[1] = &mine; items[2] = &s2;
        *(BehaviorCall.SafePtr***)(obj + EngineLayout.AurObjBackRefs) = items;
        *(int*)(obj + EngineLayout.AurObjBackRefCount) = 3;

        BehaviorCall.Unregister(&mine);

        bool ok = *(int*)(obj + EngineLayout.AurObjBackRefCount) == 2
                  && items[0] == &s0 && items[1] == &s2 && s2.Index == 1 && mine.Index == -1;
        // A dead owner must be a no-op, not a write through null.
        BehaviorCall.SafePtr dead = new() { Ptr = IntPtr.Zero, Index = 4 };
        BehaviorCall.Unregister(&dead);
        return ok && dead.Index == -1;
    }
}
