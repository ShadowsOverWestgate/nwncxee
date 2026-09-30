using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-104: the grass bins Alloy draws, found where the SCENE owns them instead of where a frame left
/// them. <c>binbucket</c> is a global the engine's BSP visitor (@0x481000) refills every frame from each
/// visible node's bin list -- so reading it made this pass's population a side effect of the engine's
/// traversal, and of anything else that ever appended to it.
/// <para><b>The source.</b> <c>Scene::InitBins</c> @0x472390 sizes <c>Scene+0x1d0</c> as a
/// <c>CExoArrayList&lt;List&lt;CAurTriangleBin*&gt;&gt;</c> -- one 0x10-byte list head per bin column, each
/// holding that column's bins. That grid is the area's whole grass population; the bucket is one frame's slice
/// of it. This walks the grid and applies the engine's own per-bin test, <c>BinOutside</c> @0x47a620:
/// <c>SphereAbovePlane(bin+0x24, bin+0x30, plane)</c> against the camera's planes, frustum only -- the grass
/// far distance is applied downstream, inside <c>Scene::DoBinBucket</c> and by the pass's own LOD ladder, so
/// this must not clip at it (the #355 note).</para>
/// <para>Rebuilt once per <see cref="AlloyRenderer.PassGen"/>, allocation-free. A grid a sane area cannot
/// have stands the owned set down loudly and the pass reads the bucket for that frame, which is the same route
/// <c>/buckets grassbins 0</c> takes.</para></summary>
internal static unsafe partial class GrassField
{
    /// <summary>Bins Alloy will consider in one frame. A 32x32-tile area's grid is a few hundred columns of a
    /// few bins; this is well past any stock tileset and the cap is counted, never silent.</summary>
    private const int MaxOwnedBins = 8192;
    /// <summary>A bin column is a <c>List&lt;CAurTriangleBin*&gt;</c>: items at +0, count at +8, allocation at
    /// +0xc, laid end to end in the grid array.</summary>
    private const int BinColumnStride = 16, BinColumnCount = 8;
    /// <summary>A grid or column longer than this did not read sanely.</summary>
    private const int BinSaneMax = 1 << 16;
    private const int BinPlaneFloats = 24;
    /// <summary>World units a bin's face sphere grows by for the blades standing on it and their sway.</summary>
    private const float BladeSlack = 2f;

    private static readonly IntPtr[] _ownedBins = new IntPtr[MaxOwnedBins];
    private static readonly float[] _binPlanes = new float[BinPlaneFloats];
    private static int _ownedBinN, _ownedBinGen = -1;
    /// <summary>Did the last refresh in <see cref="_ownedBinGen"/> produce an owned list?</summary>
    private static bool _ownedBinOk;
    private static long _binGridFrames, _binGridCulled, _binGridCapped, _binNoOwn;
    private static readonly StandDown _binGridDown = new("GrassField.BinGrid");

    /// <summary>This frame's bins, from the scene's own grid. Empty and <c>false</c> = no owned set for this
    /// frame (no scene, no grid, an unreadable one, or the knob is off), and the caller reads the engine's
    /// bucket instead.</summary>
    private static bool OwnedBins(Matrix4x4* vp, out ReadOnlySpan<IntPtr> bins)
    {
        bins = default;
        if (!Buckets.GrassBins.V || _binGridDown.Failed) return false;
        // The verdict is cached beside the generation, not inferred from it: stamping the generation before
        // the refresh could fail would make a SECOND call in the same generation take the cached path and
        // answer "owned, 0 bins" instead of handing the frame back to the bucket.
        if (_ownedBinGen != AlloyRenderer.PassGen)
        {
            _ownedBinOk = RefreshOwnedBins(vp);
            _ownedBinGen = AlloyRenderer.PassGen;
        }
        if (!_ownedBinOk) return false;
        if (_ownedBinN == 0 && !_binBucket.IsNull) SceneParts.WarnEmpty(BucketValidator.KindBin, 0, _binBucket.Count);
        bins = new ReadOnlySpan<IntPtr>(_ownedBins, 0, _ownedBinN);
        return true;
    }

    /// <summary>Rebuild the owned bin list against the planes of <paramref name="vp"/>, the matrix this pass
    /// draws the grass with. The caller owns the generation stamp and the verdict latch.</summary>
    private static bool RefreshOwnedBins(Matrix4x4* vp)
    {
        _ownedBinN = 0;
        IntPtr scene = SceneWalk.WorldScene;
        if (scene == IntPtr.Zero) { _binNoOwn++; return false; }
        var sc = new CX.Engine.Scene(scene);
        byte* grid = (byte*)sc.BinGrid;
        int columns = sc.BinGridCount;
        if (grid == null || columns <= 0) return columns == 0;   // an area with no grass has no grid
        if (columns > BinSaneMax)
        {
            _log?.Always($"GrassField: the scene's bin grid reads {columns} columns, past the sane bound "
                       + $"{BinSaneMax} -- that is not a busy area, it is a bad read");
            _binGridDown.Fail(_log, "the scene's bin grid did not read sanely",
                              "grass takes the engine's per-frame bin list this session");
            return false;
        }
        // No camera means no world frame has been read yet, which is also a frame with no grass draw in it --
        // so this is "nothing to own yet", not a failure of the owned path. Counted so a live session can see
        // if it ever coincides with a drawing frame.
        if (!FrameInputs.HasCamera || !FrameInputs.HasPerspective) { _binNoOwn++; return false; }
        _binGridFrames++;
        fixed (float* planes = _binPlanes)
        {
            // The DRAW's own view-projection, not one rebuilt from the engine camera's fov/aspect/far: Alloy's
            // projection is its own (CX-109), and a narrower rebuilt frustum popped bins out at the screen edges.
            CullPass.Planes((float*)vp, planes, AlloyRenderer.EyePos);
            for (int c = 0; c < columns; c++)
            {
                byte* column = grid + (long)c * BinColumnStride;
                IntPtr* items = *(IntPtr**)column;
                int n = *(int*)(column + BinColumnCount);
                if (items == null || n <= 0 || n > BinSaneMax) continue;
                for (int i = 0; i < n; i++)
                {
                    IntPtr bin = items[i];
                    if (bin == IntPtr.Zero) continue;
                    var tb = new CAurTriangleBin(bin);
                    if (!SphereVisible(tb.Centre, tb.Radius + BladeSlack, planes)) { _binGridCulled++; continue; }
                    if (_ownedBinN >= MaxOwnedBins)
                    {
                        _binGridCapped++;
                        _log?.Always("GrassField: the owned bin ceiling is full -- the rest of this area's "
                                   + "grass is not drawn. A real bound, not a transient: census the area and "
                                   + "raise MaxOwnedBins");
                        return true;
                    }
                    _ownedBins[_ownedBinN++] = bin;
                }
            }
        }
        return true;
    }

    /// <summary>Sphere against six inward-facing planes: outside if it is wholly behind any one of them, which
    /// is <c>BinOutside</c>'s loop with the engine's outward sign flipped.</summary>
    private static bool SphereVisible(Vector3 centre, float radius, float* planes)
    {
        for (int i = 0; i < 6; i++)
        {
            float d = planes[i * 4] * centre.X + planes[i * 4 + 1] * centre.Y + planes[i * 4 + 2] * centre.Z
                    + planes[i * 4 + 3];
            if (d < -radius) return false;
        }
        return true;
    }

    /// <summary>The clause <see cref="Stats"/> adds for the bin source.</summary>
    private static string BinSourceNote()
        => !Buckets.GrassBins.V ? "bins from the engine bucket (knob)"
         : _binGridDown.Failed ? "bins from the engine bucket (the owned grid STOOD DOWN)"
         : $"{_ownedBinN} bin(s) from the scene grid ({_binGridCulled} culled over {_binGridFrames} frame(s)"
           + (_binGridCapped > 0 ? $", CAPPED {_binGridCapped}x" : "")
           + (_binNoOwn > 0 ? $", {_binNoOwn} frame(s) with nothing to own" : "") + ")";
}
