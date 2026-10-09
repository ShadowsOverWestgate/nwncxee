using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;

    static EmitterSim()
    {
        G = KnobGroup.Declare("emitsim", "Graphics", "Particle simulation",
            master: "Simulate particles in CX",
            masterHelp: "CX moves, spawns and retires particles itself. Off, the engine does it -- which is "
                      + "the A/B for any effect that looks wrong, and the only way to see the engine's own "
                      + "emitter code run inside an Alloy frame again.",
            parent: "particles.enabled");
        Enabled = G.Master!;
        G.Status = Describe;

    }

    /// <summary>What the panel row and <c>/knobs</c> say about this group's live state -- including the two
    /// things a comparison shot needs to know: whether the owned simulation is actually driving, and whether
    /// anything in view has hit one of its named holes.</summary>
    private static string Describe()
    {
        if (!Armed) return "stood down (an engine symbol did not resolve; the engine walks the emitters)";
        if (!Enabled.V) return "off -- Scene::DoEmitterBucket runs, particles are one frame behind";
        string s = "on";
        if (_poolWarned) s += ", pool ceiling hit";
        return s;
    }
}
