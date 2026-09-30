using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class BehaviorSolvers
{
    internal static readonly KnobGroup G;
    /// <summary>CX-107's A/B: off, every hooked <c>Control</c> hands the call to the engine's own solver, so
    /// the whole port switches out in place -- no restart, because both paths are always installed.</summary>
    internal static readonly Knob<bool> Owned;
    /// <summary>The ported solvers are driving this frame: armed at boot and the live A/B on.</summary>
    internal static bool Driving => Armed && Owned.V;

    static BehaviorSolvers()
    {
        G = KnobGroup.Declare("behav", "Graphics", "Object behaviours",
            master: "Solve attach and camera behaviours in CX",
            masterHelp: "CX runs the engine's placement solvers itself: visual effects attached to hands and heads, "
                      + "GUI-scaled objects, the 3D panels' viewports and the chase camera. Off, the game's own "
                      + "solvers run -- which is the A/B for anything attached that sits in the wrong place.",
            parent: "alloy.enabled");
        Owned = G.Master!;
        G.Status = Describe;
    }

    /// <summary>What <c>/knobs</c> and the panel row say: whether the port is driving, and what it last
    /// measured. Every class at zero is a legitimate answer -- nothing attached and no camera behaviour
    /// installed -- not a broken port.</summary>
    private static string Describe()
    {
        if (!_armed)
            return _standDown.Length == 0
                ? "stood down (the engine renderer was asked for; the engine's solvers run)"
                : _standDown;
        if (!Owned.V) return "off -- the engine's CAurBehavior::Control runs for every behaviour";
        return $"on for 12 of the 13 classes (loose-follow is handed back, CX-107); {_last}";
    }
}
