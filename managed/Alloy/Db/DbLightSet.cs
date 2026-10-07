using System.Numerics;
using CX.Alloy.Seam;

namespace CX.Alloy.Db;

/// <summary>CX-116: the two questions the Aurora seam asks of the DB <b>light set</b> -- the largest radius an
/// object's lights reach, and a recolour of the light one named part owns. Both are cold (a script call, a
/// placeable's lamp colour), never per-frame, and both go through <see cref="DbInputs"/> so they see the one
/// light list however a light arrived: a gob's from its part tree, a tile's from
/// <see cref="TileBuild"/>'s registration.</summary>
internal static class DbLightSet
{
    /// <summary><c>Gob::GetMaximumLightRadius</c> @0x43fba0, transcribed: <c>CollectLights</c> over the
    /// object, then the largest <c>PartLight+0x70</c> (<c>range</c>) among them, starting from 0 -- so an
    /// object with no lights answers 0, as the engine's does.
    /// <para>The radius read is the LIVE one, which is why it goes through <see cref="DbInputs.Light"/>: that
    /// folds the animated column over the tile record over the node's rest key, in that order, exactly as the
    /// engine's field would have been overwritten by a controller.</para></summary>
    internal static float MaxRadius(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return 0f;
        float max = 0f;
        int n = DbInputs.LightCount;
        for (int k = 0; k < n; k++)
            if (DbInputs.Light(k, out DbLight l) && l.Obj == obj && l.Radius > max) max = l.Radius;
        // ...and the Lit rows of this object that no part backs. A tile light registered before its part
        // record existed is in that table and nowhere else, and the feed's light walk reads it from there.
        for (int i = 1; i < SceneDb.Lights.High; i++)
            if (SceneDb.Lights.LiveAt(i) && SceneDb.LitObj[i] == obj && SceneDb.LitPart[i] < 0
                && SceneDb.LitRange[i] > max) max = SceneDb.LitRange[i];
        return max;
    }

    /// <summary><c>Gob::ControlPartLightColor</c> @0x4436d0: recolour the light the named part owns. The
    /// engine resolves the part with <c>Gob::GetPart(name)</c> (vtable +0x238), casts it with
    /// <c>AsPartLight</c> and then installs a controller rather than writing the colour field --
    /// see <see cref="DbAnim.ControlLightColour"/> for the transcription and why the value lands in the
    /// animated column.
    /// <para>A name that resolves to no part, or to a part that is not a light, does nothing: the engine's
    /// own path would have dereferenced a null <c>AsPartLight</c> result, so "nothing" is the only safe
    /// reading of it, and the miss is counted rather than logged per call.</para></summary>
    internal static void SetPartColour(DbId obj, string? part, Vector3 colour)
    {
        int p = ObjectSlots.PartByName(obj, part);
        if (p < 0) { Missed++; return; }
        if (!DbInputs.IsLight(p)) { NotALight++; return; }
        DbAnim.ControlLightColour(p, colour);
        // BOTH places the renderer reads a colour from: the animated column (an object light's path, through
        // DbInputs.Light) and the Lit record (a TILE light's, which the feed's light walk enumerates
        // directly). A recolour that wrote only one of them would take effect for one kind and not the other.
        int lit = DbInputs.LitOf(p);
        if (lit >= 0 && SceneDb.Lights.LiveAt(lit))
        {
            SceneDb.LitColour[lit] = colour;
            SceneDb.Lights.Mark(lit, Dirty.Material);
        }
        Recoloured++;
    }

    /// <summary>Recolours applied, names that resolved to nothing, and names that resolved to a part with no
    /// light -- the last two are content questions, so they are counted and reported, not refused.</summary>
    internal static long Recoloured { get; private set; }
    /// <inheritdoc cref="Recoloured"/>
    internal static long Missed { get; private set; }
    /// <inheritdoc cref="Recoloured"/>
    internal static long NotALight { get; private set; }

    /// <summary><see cref="Stats"/> as a period-line clause: empty, or prefixed with a separator.</summary>
    internal static string StatsClause()
    {
        string s = Stats();
        return s.Length == 0 ? "" : " | " + s;
    }

    internal static string Stats()
        => Recoloured == 0 && Missed == 0 && NotALight == 0
            ? ""
            : $"lightset {Recoloured} recoloured"
              + (Missed > 0 ? $", {Missed} part names not found" : "")
              + (NotALight > 0 ? $", {NotALight} named parts are not light nodes" : "")
;
}
