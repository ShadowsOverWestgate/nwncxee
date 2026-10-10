using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Db;
using CX.Graphics;

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
            if (DbAcoustic(i) || (EngineSlot(i) && IsAcoustic(i))) n++;
        return n;
    }

    private static bool IsAcoustic(int i)
        => StaticAtEnroll(_oCls[i], _oGob[i], _oTileGob[i], _oMx[i], _vSkin[_oVao[i]], _oDangly[i], 0) == 0
           && _primTable != null && _primTable[_oPrim[i]] == Gl.TRIANGLES;

    /// <summary>The same question of a database slot. Every DB draw is TRIANGLES, so all that is left is "does
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
    /// triples and index triples. A (VDO slot, item) is taken once however many slots draw it -- a static part sits
    /// in world space, so a second slot on the same range is the same triangles. Cold (once per area settle), render
    /// thread. Returns the triangle count.</summary>
    internal static int GatherStatic(List<float> verts, List<int> tris, float minArea)
    {
        var seen = new HashSet<long>();
        int count0 = tris.Count;
        for (int i = 0; i < _ownCount; i++)
        {
            if (DbAcoustic(i)) { DbGatherOne(i, verts, tris, minArea); continue; }
            if (!EngineSlot(i) || !IsAcoustic(i)) continue;
            int s = _oVao[i], item = _oItem[i];
            if (!seen.Add(((long)s << 32) | (uint)item)) continue;
            var vdo = new VdoRef(_vVdo[s]);
            if (vdo.IsNull) continue;
            long elem = vdo.ElemItems, vert = vdo.VertItems;
            if (elem == 0 || vert == 0 || item < 0 || item >= vdo.ElemCount || item >= vdo.VertCount) continue;
            int count = *(int*)(elem + item * 8 + 4);
            long first = (uint)*(int*)(elem + item * 8);
            int baseVertex = *(int*)(vert + item * 8);
            byte* elemHost = OwnedBuffers.PoolHost(vdo.ElemToken);
            byte* posHost = OwnedBuffers.PoolHostByName(_vPos[s].Buf, vdo.VertToken, vdo.DynToken);
            ref readonly VertexStream pos = ref _vPos[s];
            if (elemHost == null || posHost == null || count < 3 || pos.Size < 3) continue;
            ushort* idx = (ushort*)(elemHost + first * 2);
            int lo = int.MaxValue, hi = -1;
            for (int k = 0; k < count - count % 3; k++) { lo = Math.Min(lo, idx[k]); hi = Math.Max(hi, idx[k]); }
            if (hi < lo) continue;
            int at = verts.Count / 3;
            for (int v = lo; v <= hi; v++)
            {
                float* p = (float*)(posHost + pos.Off + (long)(baseVertex + v) * pos.Stride);
                verts.Add(p[0]); verts.Add(p[1]); verts.Add(p[2]);
            }
            for (int k = 0; k + 2 < count; k += 3)
            {
                int a = idx[k], b = idx[k + 1], c = idx[k + 2];
                if (a == b || b == c || a == c || TriArea(verts, at + a - lo, at + b - lo, at + c - lo) < minArea) continue;
                tris.Add(at + a - lo); tris.Add(at + b - lo); tris.Add(at + c - lo);
            }
        }
        return (tris.Count - count0) / 3;
    }

    /// <summary>One static database part's triangles, worldized. The arena holds MODEL-space geometry (that is
    /// what lets one range serve every instance), so unlike the engine path -- where a static part's vertices
    /// are already world space under an identity matrix -- each part is transformed by its own world matrix
    /// here. No (range, item) dedup for the same reason: two parts on one range are two different walls.</summary>
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
