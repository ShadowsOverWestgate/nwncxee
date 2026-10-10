using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Db;

namespace CX.Alloy;

/// <summary>The static world as Steam Audio's scene. The acoustic geometry is the geometry Alloy already owns --
/// tile parts and Static placeables, read off the pools' host shadows -- so sound hits exactly the walls the
/// player sees.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Owned slots in the static set right now: the settle signal the acoustic scene waits on. A linear
    /// scan of the slot table -- called a few times a second, render thread.</summary>
    internal static int StaticSlotCount()
    {
        int n = 0;
        for (int i = 0; i < _ownCount; i++)
            if (DbAcoustic(i)) n++;
        return n;
    }

    /// <summary>Is slot <paramref name="i"/> acoustic geometry? Every DB draw is TRIANGLES, so all that is left is "does
    /// this part hold still": a tile part always does, an object part when its record says Static. Reads no
    /// engine scene memory.</summary>
    private static bool DbAcoustic(int i)
    {
        int part = _oDbPart[i];
        if (part < 0 || _oDbRecipe[i] < 0) return false;
        if (_oTileGob[i]) return true;
        DbId obj = SceneDb.PartObj[part];
        return SceneDb.Objects.Valid(obj) && (SceneDb.ObjFlags[obj.Index] & ObjFlag.Static) != 0;
    }

    /// <summary>Every static triangle of at least <paramref name="minArea"/> square metres, appended as xyz vertex
    /// triples and index triples. Cold (once per area settle), render thread. Returns the triangle count.</summary>
    internal static int GatherStatic(List<float> verts, List<int> tris, float minArea)
    {
        int count0 = tris.Count;
        for (int i = 0; i < _ownCount; i++)
            if (DbAcoustic(i)) DbGatherOne(i, verts, tris, minArea);
        return (tris.Count - count0) / 3;
    }

    /// <summary>One static database part's triangles, worldized. The arena holds MODEL-space geometry (that is
    /// what lets one range serve every instance), so each part is transformed by its own world matrix here. No
    /// range dedup for the same reason: two parts on one range are two different walls.</summary>
    private static void DbGatherOne(int i, List<float> verts, List<int> tris, float minArea)
    {
        int at = verts.Count / 3, kept = tris.Count;
        if (!MeshArena.HostTriangles(DrawRecipe.Range(_oDbRecipe[i]), verts, tris)) return;
        MeshArena.WorldizeFrom(verts, at, SceneDb.PartWorld[_oDbPart[i]]);
        // The area filter runs on the WORLD triangles, so it has to wait for the transform: a scaled part's
        // slivers are not the same size in the two spaces.
        int w = kept;
        for (int k = kept; k + 2 < tris.Count; k += 3)
        {
            if (TriArea(verts, tris[k], tris[k + 1], tris[k + 2]) < minArea) continue;
            tris[w] = tris[k]; tris[w + 1] = tris[k + 1]; tris[w + 2] = tris[k + 2]; w += 3;
        }
        tris.RemoveRange(w, tris.Count - w);
    }

    private static float TriArea(List<float> v, int a, int b, int c)
    {
        float ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
        float wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
        float x = uy * wz - uz * wy, y = uz * wx - ux * wz, z = ux * wy - uy * wx;
        return 0.5f * MathF.Sqrt(x * x + y * y + z * z);
    }
}
