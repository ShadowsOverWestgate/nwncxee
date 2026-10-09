using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;

    static EmitterSim()
    {
        G = KnobGroup.Declare("emitsim", "Graphics", "Motion",
            master: "Simulate particles in CX",
            masterHelp: "CX moves, spawns and retires every particle. Off, nothing steps them: they freeze where "
                      + "they are. Since CX draws the whole scene there is no engine simulation left to hand the "
                      + "work back to, so this is a cfg-only switch.",
            masterTier: Tier.ChatOnly, parent: "particles.enabled", order: 356, title: "Particle simulation");
        Enabled = G.Master!;
        G.Status = Describe;

    }

    /// <summary>What <c>/emitsim</c> and <c>/knobs</c> say about this group's live state: whether the owned
    /// simulation is actually driving, and whether anything in view has hit one of its named holes.</summary>
    private static string Describe()
    {
        if (!Armed) return "stood down (an engine symbol did not resolve; the whole Alloy path stands down with it)";
        if (!Enabled.V) return "off -- nothing steps the emitters, so every particle is frozen";
        string s = "on";
        if (_poolWarned) s += ", pool ceiling hit";
        return s;
    }
}
