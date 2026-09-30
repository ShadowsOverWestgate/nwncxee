using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-104: the A/B switches for Alloy's own visibility sets. Each one hands ONE class back to the
/// engine's per-frame bucket -- which is the only remaining route to those globals and is there so a live
/// session can bisect a behavioural difference, not as a fallback: nothing here degrades quietly.
/// <para><see cref="Validate"/> is the other half: it keeps reading the engine bucket beside Alloy's set and
/// counts the disagreements on a period line, so "is our set the same population?" is a measurement rather
/// than an argument. Off by default -- it costs the very bucket reads this ticket removed.</para></summary>
internal static class Buckets
{
    internal static readonly KnobGroup G;
    /// <summary>Per class: on = Alloy's own set, off = the engine's bucket.</summary>
    internal static readonly Knob<bool> TileFade, GrassBins, Emitters, Triggers, Beams;
    /// <summary>Read both sets and count where they disagree.</summary>
    internal static readonly Knob<bool> Validate;

    static Buckets()
    {
        G = KnobGroup.Declare("buckets", "Graphics", "Scene visibility", parent: "alloy.enabled");
        G.Status = Describe;

        TileFade = G.Toggle("tilefade", "CX drives the tile fade", true, Tier.Advanced,
            help: "The roof between the camera and your character fades out, driven by CX's own per-tile "
                + "ramp and its own occlusion test. Off, CX reads the fade the engine's BSP walk computes -- "
                + "which is the A/B if a roof fades at the wrong moment.");
        GrassBins = G.Toggle("grassbins", "CX finds the grass patches", true, Tier.Advanced,
            help: "CX enumerates the area's grass bins from the scene's own bin grid and culls them itself. "
                + "Off, it takes the engine's per-frame visible-bin list.");
        Emitters = G.Toggle("emitters", "CX finds the emitters", true, Tier.Advanced,
            help: "The particle simulation walks CX's own registry of the area's emitters. Off, it walks the "
                + "engine's per-frame emitter bucket -- the A/B if an effect stops playing.");
        Triggers = G.Toggle("triggers", "CX finds the area transitions", true, Tier.Advanced,
            help: "The area-transition highlight reads CX's own trigger registry. Off, it reads the engine's "
                + "per-frame trigger bucket.");
        Beams = G.Toggle("beams", "CX finds the light shafts", true, Tier.Advanced,
            help: "The beam pass reads CX's own registry of the area's static projection volumes. Off, it "
                + "reads the engine's draw bucket.");

        Validate = G.Toggle("validate", "Compare against the engine's lists", false, Tier.ChatOnly,
            help: "Diagnostic: keep reading the engine's buckets beside CX's sets and report how often the "
                + "two disagree. Costs the reads it measures, so it is off unless you are chasing one.");
    }

    private static string Describe()
    {
        string s = $"tile fade {On(TileFade)}, grass bins {On(GrassBins)}, emitters {On(Emitters)}, "
                 + $"triggers {On(Triggers)}, beams {On(Beams)}";
        s += " | " + SceneParts.Note();
        s += " | " + TileFadeRamp.Note();
        if (Validate.V) s += " | " + BucketValidator.Note();
        return s;
    }

    private static string On(Knob<bool> k) => k.V ? "CX" : "engine";
}
