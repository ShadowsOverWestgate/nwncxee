using CX.Alloy.Db;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The per-batch VAO and cull bind. Every draw comes out of the mesh arena, whose VAO is per vertex
/// FORMAT (<see cref="MeshArena.Vao"/>).</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>#213's acceptance signal: the classes that have bound an arena VAO since the last period line,
    /// one bit per <see cref="AlloyClass"/>.</summary>
    private static int _ownedClsMask;

    /// <summary>Which classes drew from Alloy's buffers this period, cleared as it is read.</summary>
    internal static string OwnedClasses()
    {
        int m = _ownedClsMask;
        _ownedClsMask = 0;
        if (m == 0) return "none";
        string s = "";
        for (int c = 0; c < AlloyClasses.Count; c++)
            if ((m & (1 << c)) != 0) s += (s.Length == 0 ? "" : "+") + (AlloyClass)c;
        return s;
    }

    /// <summary>CX-704: the per-batch cull + VAO bind every issue loop shares. Cull reproduces the engine's
    /// per-material decision (#292): sub-list 1 of Scene::RenderDrawBucket @0x46d060 draws under
    /// Disable(CULL_FACE), everything else under the Enable + CullFace(BACK) it leaves behind. The VAO is tracked
    /// by GL NAME (#212) -- the SAME name across depth and colour passes keeps #119's invariant-gl_Position match trivial.</summary>
    internal static void BindBatchState(int g, ref int boundCull, ref uint boundVao)
        => BindBatchState(_gNoCull[g], VaoName(_gVao[g], _gCls[g]), ref boundCull, ref boundVao);

    internal static void BindBatchState(bool noCull, uint vao, ref int boundCull, ref uint boundVao)
    {
        int wantCull = noCull ? 0 : 1;
        if (wantCull != boundCull)
        {
            boundCull = wantCull;
            if (wantCull != 0) { Gl.Enable(Gl.CULL_FACE); Gl.CullFace(Gl.BACK); }
            else Gl.Disable(Gl.CULL_FACE);
        }
        if (vao != boundVao) { Gl.BindVertexArray(vao); boundVao = vao; }
    }

    /// <summary>The arena VAO for a batch key: <see cref="DbArenaVaoBit"/> set, the low bits the vertex format.</summary>
    internal static uint VaoName(int v, int cls)
    {
        _ownedClsMask |= 1 << cls;
        return MeshArena.Vao((MeshArena.Fmt)(v & ~DbArenaVaoBit));
    }
}
