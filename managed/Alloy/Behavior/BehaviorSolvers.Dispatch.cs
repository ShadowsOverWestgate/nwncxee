using System;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class BehaviorSolvers
{
    /// <summary>How many concrete <c>Control</c> implementations there are -- the width of every per-class
    /// table here.</summary>
    internal const int Kinds = 13;

    /// <summary>The Linux symbol per class, index-aligned with <see cref="BehaviorKind"/>.</summary>
    internal static readonly string[] Symbols =
    {
        SymbolMap.BehaviorControl, SymbolMap.AttachControl, SymbolMap.AttachTranslateControl,
        SymbolMap.AttachRotateControl, SymbolMap.AttachNoOrientationControl, SymbolMap.AttachGuiScaleControl,
        SymbolMap.CrossSceneAttachControl, SymbolMap.CrossSceneAttachOffsetControl, SymbolMap.SubwindowControl,
        SymbolMap.NavigateControl, SymbolMap.CameraFollowControl, SymbolMap.CameraLooseFollowControl,
        SymbolMap.CameraOnAStickControl,
    };

    /// <summary>Short names for the period line and the <c>/knobs</c> note, same order.</summary>
    internal static readonly string[] Names =
    {
        "base", "attach", "translate", "rotate", "noorient", "guiscale", "crossscene", "crossoffset",
        "subwindow", "navigate", "follow", "loosefollow", "onastick",
    };

    /// <summary>Per class: the next link in that symbol's chain -- the engine's own <c>Control</c>, since the
    /// port is the innermost layer. Filled by <c>HookService.Commit</c>; never dereferenced before.
    /// <para>Once <see cref="Wire"/> has taken a non-null slot the entry is kept FOR GOOD, stand-down
    /// included. <c>HookService.Chain</c> has already registered that link by the time it returns, and
    /// <c>Commit</c> installs it whatever CX decides afterwards -- so the handler WILL be called, and this
    /// pointer is the only way it can reach the engine's own solve. Dropping the table on a stand-down (the
    /// CX-102 review's finding) left the handler installed with nowhere to forward to: every attach and the
    /// chase camera silently stopped solving.</para></summary>
    private static readonly IntPtr*[] _next = new IntPtr*[Kinds];

    private static bool _armed;
    private static string _standDown = "";
    private static bool _looseWarned;
    private static bool _orphanWarned;
    private static ExtenderLog? _log;

    /// <summary>Is the port installed at all? False means the engine's solvers run whatever the knob
    /// says.</summary>
    internal static bool Armed => _armed;

    internal static bool Wire(int k, IntPtr* slot)
    {
        _next[k] = slot;
        return slot != null;
    }

    internal static void Arm() => _armed = true;

    /// <summary>The visible stand-down. An empty <paramref name="why"/> is the plain configuration choice
    /// (the engine renderer was asked for), which is not a failure and says nothing loudly; anything else is
    /// a failure of our own mechanism and is said once, at <c>Always</c> level, and repeated on
    /// <c>/knobs</c> (memory <c>no-silent-fallbacks.md</c>).
    /// <para>It clears <see cref="Armed"/> and nothing else. Whatever the chain already wired stays wired,
    /// so every installed handler keeps forwarding to the engine's <c>Control</c> -- see
    /// <see cref="_next"/>.</para></summary>
    internal static void StandDown(string why)
    {
        _armed = false;
        _standDown = why;
        if (why.Length != 0) _log?.Always($"BehaviorSolvers: {why}; the engine's CAurBehavior::Control runs instead");
    }

    /// <summary>The log every path here uses, including the stand-downs -- so it is bound before the first
    /// gate, not at <see cref="Arm"/>.</summary>
    internal static void Bind(ExtenderLog log) => _log = log;

    /// <summary>The one branch, per call: the port, or the engine's own <c>Control</c>. Allocation-free --
    /// no closure, no boxing, two static reads. The gate is <see cref="Driving"/>, i.e. armed AND the A/B on:
    /// testing only the knob would let a stood-down feature solve anyway, because the hooks the chain
    /// accepted are installed regardless of what CX decided afterwards.</summary>
    internal static int Dispatch(int k, IntPtr self, float dt) =>
        Driving ? Solve(k, self, dt) : Engine(k, self, dt);

    /// <summary>Hands one call to the engine's own <c>Control</c> through the chain slot.</summary>
    internal static int Engine(int k, IntPtr self, float dt)
    {
        IntPtr* slot = _next[k];
        if (slot != null && *slot != IntPtr.Zero) return ((BehaviorControlFn)(*slot))(self, dt);
        // Unreachable: a handler is installed by exactly the Chain() call that returns its slot, and no
        // handler can fire before Commit() fills it. If it ever happens there is no engine solve to reach
        // and no port to run, so say so once rather than answer quietly (no-silent-fallbacks.md). 1 is
        // CrossSceneAttach's and Subwindow's own answer: "I am still the owner's behaviour".
        if (!_orphanWarned)
        {
            _orphanWarned = true;
            _log?.Always($"BehaviorSolvers: {Names[k]} fired with no chain link -- its Control is now a no-op. "
                       + "Attached objects on it will not move; please report this.");
        }
        return 1;
    }

    /// <summary>
    /// The owned solve for class <paramref name="k"/>. A jump table over thirteen cases, one per concrete
    /// engine class; <c>loosefollow</c> is the one that hands the call back (it is unreachable in this build
    /// and its engine body is unfinished debug scaffolding -- see
    /// <c>CX-107</c> §7), and it says so loudly the first time, because if it
    /// ever fires the premise was wrong.
    /// </summary>
    internal static int Solve(int k, IntPtr self, float dt)
    {
        _solves[k]++;
        switch (k)
        {
            case (int)BehaviorKind.Base: return Base(self, dt, out _);
            case (int)BehaviorKind.Attach: return Attach(self, dt);
            case (int)BehaviorKind.AttachTranslate: return AttachTranslate(self, dt);
            case (int)BehaviorKind.AttachRotate: return AttachRotate(self, dt);
            case (int)BehaviorKind.AttachNoOrientation: return AttachNoOrientation(self, dt);
            case (int)BehaviorKind.AttachGuiScale: return AttachGuiScale(self, dt);
            case (int)BehaviorKind.CrossSceneAttach: return CrossSceneAttach(self, dt, withOffset: false);
            case (int)BehaviorKind.CrossSceneAttachOffset: return CrossSceneAttach(self, dt, withOffset: true);
            case (int)BehaviorKind.Subwindow: return Subwindow(self, dt);
            case (int)BehaviorKind.Navigate: return Navigate(self, dt);
            case (int)BehaviorKind.CameraFollow: return CameraFollow(self, dt);
            case (int)BehaviorKind.CameraOnAStick: return CameraOnAStick(self, dt);
            case (int)BehaviorKind.CameraLooseFollow:
                if (!_looseWarned)
                {
                    _looseWarned = true;
                    _log?.Always("BehaviorSolvers: CAurBehaviorCameraLooseFollow::Control FIRED -- CX-107 has no port "
                               + "for it (nothing in this client build constructs the class, and the engine body is "
                               + "unfinished debug code). Handing the solve back to the engine; please report this.");
                }
                return Engine(k, self, dt);
            default: return Engine(k, self, dt);
        }
    }

    // ---- the instrument -------------------------------------------------------------------------

    private static int _frames;
    /// <summary>Classes that solved anything on the frame just ended.</summary>
    private static int _live;
    private static readonly int[] _peak = new int[Kinds];
    private static string _last = "no frame measured yet";

    /// <summary>Period line: solves per class per frame, as the peak seen over the period. Zero for every
    /// class is a legitimate answer (nothing attached, no camera behaviour installed), so the line prints the
    /// classes that fired and names the count of those that did not.</summary>
    internal static void OnFrameEnd()
    {
        int live = 0;
        for (int i = 0; i < _solves.Length; i++)
        {
            if (_solves[i] > _peak[i]) _peak[i] = _solves[i];
            if (_solves[i] != 0) live++;
            _solves[i] = 0;
        }
        _live = live;
        if (++_frames < 600) return;
        var sb = new System.Text.StringBuilder(160);
        int quiet = 0;
        for (int i = 0; i < _peak.Length; i++)
        {
            if (_peak[i] == 0) { quiet++; continue; }
            if (sb.Length != 0) sb.Append(", ");
            sb.Append(Names[i]).Append(' ').Append(_peak[i]);
            _peak[i] = 0;
        }
        if (sb.Length == 0) sb.Append("nothing solved");
        _last = $"peak/frame over {_frames} presents: {sb} ({quiet} of {Kinds} class(es) never fired; "
              + $"{_live} solving on the last frame)";
        if (ExtenderLog.Enabled) _log?.Info($"[behav] {_last}");
        _frames = 0;
    }

    /// <summary>The <c>/knobs</c> line: what the port is doing, and what it last measured.</summary>
    internal static string Report() =>
        !_armed ? $"behaviours: {(_standDown.Length == 0 ? "the engine's solvers (alloy.enabled=0)" : _standDown)}"
                : $"behaviours: {(Owned.V ? "owned" : "handed to the engine (behav.owned=0)")} | {_last}";
}
