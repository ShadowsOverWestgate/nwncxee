using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>CX-116's dials (ADR 0025: one declaration, three views). The build itself has no toggle -- in
/// Alloy mode the scene database IS the scene (ADR 0028) -- so what is here is the diagnostic that proves it
/// matches the engine, and it is off by default because it costs the engine reads it measures.</summary>
internal static class DbTiles
{
    internal static readonly KnobGroup G;
    /// <summary>Compare CX's tile build against the engine's, tile for tile, while the engine still builds them.</summary>
    internal static readonly Knob<bool> Compare;

    static DbTiles()
    {
        G = KnobGroup.Declare("dbtiles", "Graphics", "CX tile build", parent: "alloy.enabled");
        G.Status = Describe;

        Compare = G.Toggle("compare", "Compare CX's tiles against the engine's", false, Tier.ChatOnly,
            help: "Diagnostic: for every tile the engine spawns, build CX's own from the same inputs and "
                + "compare the placement, the world bounds, the part count and the lights. Reports PASS/FAIL "
                + "counts on one line at the end of each area load. Costs the engine reads it measures, so it "
                + "is off unless you are chasing a difference.");
    }

    private static string Describe()
    {
        string s = TileBuild.Stats();
        string l = DbLightSet.Stats();
        if (l.Length > 0) s += " | " + l;
        return s + (Compare.V ? " | " + TileCompare.Note() : "");
    }
}
