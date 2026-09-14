using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy;

/// <summary>#353: grass on the area border ring. Vanilla never grows any there, and not by a flag: the engine
/// bins grass from WALKMESH faces only (<c>Scene::AddFacesToBins</c> @0x460ef0 gates on <c>AsPartAABB</c>),
/// and the tileset's <c>*_z*</c> edge models carry no aabb node. This half of the class builds the missing
/// lists itself, from the edge tiles' RENDER faces -- the up-facing ones whose diffuse texture is a grass
/// ground texture -- laid out exactly as <see cref="Bake"/> reads an engine <c>CAurTriangleList</c>, so the
/// scatter, the culling and the draw are the same code for both.
/// <para>ponytail: "grass ground texture" is a name-substring test (<c>grass.bordertex</c>, default
/// <c>grass</c>); every stock tileset with grass names its ground <c>*grass*</c>. The principled source is
/// the real tiles' walkmesh -- correlate each grass walk face with the render face beneath it and learn the
/// texture set per area -- if a custom tileset ever names its meadow something else.</para></summary>
internal static unsafe partial class GrassField
{
    /// <summary>Size of the synthetic list block's header: OUR allocation, not an engine offset, but it has
    /// to clear every <c>CAurTriangleList</c> field Bake reads (<see cref="ListVerts"/> .. <see cref="ListMat"/>),
    /// so it is derived from the last of them rather than written down twice.</summary>
    private static readonly int BorderHeader = (EngineLayout.ListMat + 4 + 7) & ~7;
    private const int MaxBorderLists = 16384, MaxBorderFaces = 4096;
    /// <summary>TileData -&gt; its surviving parts (<c>CExoArrayList&lt;Part*&gt;</c>, items +0x30, count +0x38):
    /// the array <c>Scene::SpawnTile</c> @0x474470 walks and <c>ProcessStaticParts</c> @0x470970 fills.</summary>
    private static readonly int TileDataParts = EngineLayout.TileParts,
                               TileDataPartCount = EngineLayout.TilePartCount;
    /// <summary>MdlNodeTriMesh -&gt; faces (+0xb0, count +0xb8, 0x20-byte <c>MaxFace</c>: material int at +0x10,
    /// vertex indices as three u16 at +0x1a/+0x1c/+0x1e) and its vertex pool id (+0x280), whose host shadow
    /// holds positions 12 bytes apart. All read off <c>Scene::AddFacesToBins</c> @0x460ef0 and
    /// <c>CAurTriangleList::AddTriangle</c> @0x4b5650, which bin the engine's own grass from exactly these.</summary>
    private static readonly int NodeFaces = EngineLayout.MeshFaces, NodeFaceCount = EngineLayout.MeshFaceCount,
                               NodePool = EngineLayout.MdlRestPool, FaceStride = EngineLayout.FaceStride,
                               FaceA = EngineLayout.FaceVertA, FaceB = EngineLayout.FaceVertB,
                               FaceC = EngineLayout.FaceVertC, PoolStride = EngineLayout.PoolPosStride;
    /// <summary>The tileset's own grass material: <c>CNWCArea::LoadArea</c> registers the <c>.set</c>'s
    /// <c>[GRASS]</c> block as <c>AurGrassAddInfo(3, ...)</c>; server overrides take other slots.</summary>
    private const int TilesetGrassMaterial = 3;
    /// <summary>CAurGrassInfo -&gt; density (+0x20): blades per face in <c>AddTriangle</c> are
    /// <c>|cross| * 0.5 * density</c>, i.e. face area times it.</summary>
    private static readonly int InfoDensity = EngineLayout.GrassInfoDensity;
    /// <summary>A face is ground when its unit normal's |z| clears this. Grass stands upright from wherever
    /// its root is, so a wall-ish face with a grass texture (a raised tile's skirt) would sprout sideways.</summary>
    private const float MinUpNormal = 0.5f;

    private static readonly IntPtr[] _bl = new IntPtr[MaxBorderLists];
    private static readonly Vector3[] _blMin = new Vector3[MaxBorderLists], _blMax = new Vector3[MaxBorderLists];
    private static readonly float[] _blX = new float[MaxBorderLists], _blY = new float[MaxBorderLists];
    private static int _blCount, _borderFaces, _borderTiles, _borderOver;
    private static bool _borderGrass;
    private static string[] _borderTex = Array.Empty<string>();
    private static readonly float[] _scratchV = new float[MaxBorderFaces * 9];
    private static readonly int[] _scratchB = new int[MaxBorderFaces];
    private static delegate* unmanaged<IntPtr, Vector3*, IntPtr> _bspTile;
    private static delegate* unmanaged<uint, byte*> _getPool;
    private static delegate* unmanaged<IntPtr, Vector3*, float*, void> _getWorld;

    private static void BorderInstall(ExtenderLog log)
    {
        // Restart knobs: Boot, not V -- the ring is binned at area load from what this session started with.
        _borderGrass = Grass.Border.Boot;
        _borderTex = Grass.BorderTex.Boot.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _bspTile = (delegate* unmanaged<IntPtr, Vector3*, IntPtr>)Native.Sym(SymbolMap.SceneBspGetTileData);
        _getPool = (delegate* unmanaged<uint, byte*>)Native.Sym(SymbolMap.GlGetPool);
        _getWorld = (delegate* unmanaged<IntPtr, Vector3*, float*, void>)Native.Sym(SymbolMap.PartGetWorld);
        if (_bspTile == null || _getPool == null || _getWorld == null || _borderTex.Length == 0)
        {
            _borderGrass = false;
            log.Info("GrassField: border grass symbols unresolved -- the ring stays bare");
        }
    }

    /// <summary>A border tile was spawned at <paramref name="pos"/>: bin its grass faces. Cold (area load).</summary>
    internal static void BorderSpawn(IntPtr scene, Vector3 pos)
    {
        if (_off || !_borderGrass) return;
        if (_blCount >= MaxBorderLists) { _borderOver++; return; }
        IntPtr td = _bspTile(scene, &pos);
        if (td == IntPtr.Zero) return;
        byte* info = GrassInfo(TilesetGrassMaterial);
        if (info == null) return;
        float density = *(float*)(info + InfoDensity);
        if (!(density > 0f)) return;

        int faces = 0;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        IntPtr* parts = *(IntPtr**)(td + TileDataParts);
        int partCount = *(int*)(td + TileDataPartCount);
        if (parts == null) return;
        for (int i = 0; i < partCount && faces < MaxBorderFaces; i++)
        {
            IntPtr part = parts[i];
            if (part == IntPtr.Zero || !Renderable(part)) continue;
            IntPtr tri = ((delegate* unmanaged<IntPtr, IntPtr>)*(IntPtr*)(*(IntPtr*)part + PartAsTriMeshVtbl))(part);
            if (tri == IntPtr.Zero) continue;
            if (!IsGrassTexture(AlloyCensus.TexName(new PartRef(tri).Material.Texture(0).Ptr))) continue;
            IntPtr node = *(IntPtr*)(tri + AlloyRenderer.PartMdlNode);
            if (node == IntPtr.Zero) continue;
            byte* host = _getPool(*(uint*)(node + NodePool));
            byte* face = *(byte**)(node + NodeFaces);
            int faceCount = *(int*)(node + NodeFaceCount);
            if (host == null || face == null || faceCount <= 0) continue;
            Vector3 wp; float* q = stackalloc float[4];
            _getWorld(tri, &wp, q);
            // Quaternions are stored (w, x, y, z); System.Numerics takes (x, y, z, w).
            var rot = new Quaternion(q[1], q[2], q[3], q[0]);
            for (int f = 0; f < faceCount && faces < MaxBorderFaces; f++, face += FaceStride)
            {
                Vector3 a = wp + Vector3.Transform(*(Vector3*)(host + *(ushort*)(face + FaceA) * PoolStride), rot);
                Vector3 b = wp + Vector3.Transform(*(Vector3*)(host + *(ushort*)(face + FaceB) * PoolStride), rot);
                Vector3 c = wp + Vector3.Transform(*(Vector3*)(host + *(ushort*)(face + FaceC) * PoolStride), rot);
                Vector3 n = Vector3.Cross(b - a, c - a);
                float len = n.Length();
                if (!(len > 0f) || MathF.Abs(n.Z) < MinUpNormal * len) continue;
                int blades = (int)(len * 0.5f * density);
                if (blades <= 0) continue;
                int o = faces * 9;
                _scratchV[o] = a.X; _scratchV[o + 1] = a.Y; _scratchV[o + 2] = a.Z;
                _scratchV[o + 3] = b.X; _scratchV[o + 4] = b.Y; _scratchV[o + 5] = b.Z;
                _scratchV[o + 6] = c.X; _scratchV[o + 7] = c.Y; _scratchV[o + 8] = c.Z;
                _scratchB[faces++] = blades;
                min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
                max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
            }
        }
        if (faces == 0) return;

        byte* block = (byte*)NativeMemory.AllocZeroed((nuint)(BorderHeader + faces * 9 * sizeof(float) + faces * sizeof(int)));
        float* verts = (float*)(block + BorderHeader);
        int* blades2 = (int*)(verts + faces * 9);
        *(float**)(block + ListVerts) = verts;
        *(int*)(block + ListVertFloats) = faces * 9;
        *(int**)(block + ListBlades) = blades2;
        *(int*)(block + ListBladeCount) = faces;
        *(int*)(block + ListMat) = TilesetGrassMaterial;
        _scratchV.AsSpan(0, faces * 9).CopyTo(new Span<float>(verts, faces * 9));
        _scratchB.AsSpan(0, faces).CopyTo(new Span<int>(blades2, faces));
        float height = *(float*)(info + InfoHeight);
        int k = _blCount++;
        _bl[k] = (IntPtr)block;
        _blMin[k] = min - new Vector3(height, height, 0f);
        _blMax[k] = max + new Vector3(height, height, height * 1.5f);
        _blX[k] = pos.X; _blY[k] = pos.Y;
        _borderFaces += faces;
        _borderTiles++;
    }

    /// <summary>The border tile at <paramref name="pos"/> is being deleted (ReloadBorder): free its list. The
    /// patch that interned it goes at the next Draw's <see cref="Forget"/>, before any allocation could reuse
    /// the address and be found as the old one.</summary>
    internal static void BorderDrop(Vector3 pos)
    {
        for (int i = 0; i < _blCount; i++)
        {
            if (MathF.Abs(_blX[i] - pos.X) > 1f || MathF.Abs(_blY[i] - pos.Y) > 1f) continue;
            NativeMemory.Free((void*)_bl[i]);
            _blCount--;
            _bl[i] = _bl[_blCount]; _blMin[i] = _blMin[_blCount]; _blMax[i] = _blMax[_blCount];
            _blX[i] = _blX[_blCount]; _blY[i] = _blY[_blCount];
            _rebake = true;
            return;
        }
    }

    /// <summary>A new area's LoadBorder: every list belongs to the previous area.</summary>
    internal static void BorderClear()
    {
        for (int i = 0; i < _blCount; i++) NativeMemory.Free((void*)_bl[i]);
        _blCount = 0;
        _borderFaces = 0; _borderTiles = 0; _borderOver = 0;
        Forget();
    }

    /// <summary>The border lists' turn in <see cref="Collect"/>: the engine's <c>binbucket</c> never lists
    /// them, so distance to the eye stands in for the BSP walk; <see cref="Issue"/> frustum-culls as usual.</summary>
    private static int CollectBorder(ref int budget)
    {
        if (_blCount == 0) return 0;
        Vector3 eye = AlloyRenderer.EyePos;
        float farDist = FarDist();
        int seen = 0;
        for (int i = 0; i < _blCount; i++)
        {
            if (BoxDistance(_blMin[i], _blMax[i], eye) > farDist) continue;
            int p = Find(_bl[i]);
            if (p < 0)
            {
                if (budget <= 0) continue;
                budget--;
                p = Bake(_bl[i]);
                if (p < 0) continue;
            }
            _patches[p].Seen = _frame;
            seen++;
        }
        return seen;
    }

    /// <summary>The #338 gate, <c>PartTriMesh::IsPartRenderable</c> @0x450e60 as <see cref="EngineLayout"/>
    /// spells it: a merged-away part (CombineParts) fails it, and its geometry lives on in the part that
    /// absorbed it -- reading both would scatter every face twice.</summary>
    private static bool Renderable(IntPtr part)
    {
        if (*(byte*)(part + EngineLayout.PartRenderFlag) == 0) return false;
        IntPtr n = *(IntPtr*)(part + AlloyRenderer.PartMdlNode);
        if (n == IntPtr.Zero || *(int*)(n + EngineLayout.MdlRenderGate) == 0) return false;
        if (*(short*)(n + EngineLayout.MdlRenderKind) == 0) return *(IntPtr*)(n + EngineLayout.MdlRenderGeom) != IntPtr.Zero;
        return true;
    }

    internal static bool IsGrassTexture(string name)
    {
        foreach (string t in _borderTex)
            if (name.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static string DescribeBorder()
        => !_borderGrass ? "border grass off"
         : $"border grass {_borderTiles} tiles / {_borderFaces} faces ({_blCount} lists" +
           (_borderOver > 0 ? $", {_borderOver} TILES PAST THE LIST CAP" : "") + ")";
}
