using CX.Framework;

namespace CX.Alloy.Mdl;

/// <summary>CX-113's knobs (ADR 0025): the resident-set budget and the live comparison against the engine's own
/// animation tables. One declaration each -- the settings row, the <c>cx.cfg</c> key and
/// <c>/models &lt;knob&gt; &lt;value&gt;</c> are three views of it.</summary>
internal static class ModelKnobs
{
    internal static readonly KnobGroup G;

    /// <summary>MiB of parsed models CX keeps after the last object has let them go.</summary>
    internal static readonly Knob<int> Budget;
    /// <summary>Parse every model the area message names, ahead of whatever asks for it.</summary>
    internal static readonly Knob<bool> Prefetch;
    /// <summary>Compare CX's resolved animation set against the engine's, per model, and report PASS/FAIL.</summary>
    internal static readonly Knob<bool> Verify;

    static ModelKnobs()
    {
        G = KnobGroup.Declare("models", "Graphics", "Model cache", parent: "alloy.enabled");
        G.Status = Describe;

        Budget = G.Slider("budget", "Keep unused models (MiB)", 0, 2048, 32, 192, " MiB", Tier.Advanced,
            help: "Parsed models stay in memory after the last thing using them is gone, so walking back into "
                + "an area -- or into a second area built from the same tileset -- costs no re-parse. This is "
                + "how much memory that is allowed to hold. 0 frees each model the moment nothing uses it, "
                + "which makes every return trip a fresh read.");

        Prefetch = G.Toggle("prefetch", "Parse models ahead of the area load", false, Tier.Advanced,
            help: "CX parses an area's tile and placeable models on its own worker as the area message "
                + "arrives, so whatever asks for one later gets it without waiting. Nothing draws from CX's "
                + "own models yet (that is the scene-ownership work), so until it does this only costs the "
                + "parse -- which is why it is off. Turn it on to measure the cache: /models reports the hit "
                + "count, the mesh-to-placement share and the wait time.");

        Verify = G.Toggle("verify", "Compare animation sets with the engine", false, Tier.ChatOnly,
            help: "Diagnostic: for every model the engine also has loaded, walk its supermodel chain the "
                + "engine's way and check that CX resolved the same animation set. Reports PASS/FAIL counts "
                + "to cx.log. Costs a walk per model per area, so it is off unless you are chasing one.");
    }

    private static string Describe() => ModelCache.Stats(0);
}
