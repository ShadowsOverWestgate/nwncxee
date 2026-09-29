using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The one hook on <see cref="SymbolMap.KiwiOptimize"/>. It throws C++ exceptions, so funchook installs native/cxthunk.c's
/// C wrapper, which calls a before and an after callback and the original in between: a throw unwinds only C frames.
/// <see cref="NuiSolver"/> takes the before slot (Windows), <see cref="NuiLayoutErrors"/> the after slot.
/// </summary>
internal static unsafe class KiwiOptimizeHook
{
    private static bool _tried, _hooked;
    private static delegate* unmanaged<IntPtr, IntPtr, void> _before, _after;

    /// <summary>Sets a callback slot (null keeps the other's), installing the wrapper on first use. False when optimize did not hook.</summary>
    internal static bool Add(HookService hooks, delegate* unmanaged<IntPtr, IntPtr, void> before, delegate* unmanaged<IntPtr, IntPtr, void> after)
    {
        if (before != null) _before = before;
        if (after != null) _after = after;
        IntPtr thunk = cx_optimize_thunk(_before, _after);
        if (_tried) return _hooked;
        _tried = true;
        KiwiOptimizeFn orig = hooks.InstallKiwiOptimize((KiwiOptimizeFn)thunk);
        cx_optimize_set_orig(orig);
        return _hooked = orig != null;
    }

    [DllImport("cxthunk")] private static extern IntPtr cx_optimize_thunk(delegate* unmanaged<IntPtr, IntPtr, void> before, delegate* unmanaged<IntPtr, IntPtr, void> after);
    [DllImport("cxthunk")] private static extern void cx_optimize_set_orig(KiwiOptimizeFn orig);
}
