using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Dials for the CX tile build (ADR 0025: one declaration, three views). The build has no toggle --
/// the scene database IS the scene (ADR 0028) -- so what is here is the diagnostic that proves it matches the
/// engine, off by default because it costs the engine reads it measures.</summary>
internal static class DbTiles
{
    internal static readonly KnobGroup G;
    static DbTiles()
    {
        G = KnobGroup.Declare("dbtiles", "Graphics", "CX tile build");
        G.Status = Describe;

    }

    private static string Describe()
    {
        string s = TileBuild.Stats();
        string l = DbLightSet.Stats();
        if (l.Length > 0) s += " | " + l;
        return s;
    }
}
