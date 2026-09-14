using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;
using static Sowx.Framework.EngineLayout;

namespace Sowx.Services.Camera;

/// <summary>One segment test through the engine's own <c>Scene::HitCheck</c> -- tile BSP parts then gobs,
/// nearest hit wins -- with the one <c>CHitInfo</c> both camera users share (#537 occlusion fade, #538
/// spring arm). The record is allocated once and never moves: a hit links it into the gob's SafePointer
/// list, so <see cref="Release"/> must unlink it before the frame ends. Hot: no allocation per cast.</summary>
public static unsafe class SceneRay
{
    [StructLayout(LayoutKind.Sequential)]
    private struct HitVec { public float X, Y, Z; }

    private static delegate* unmanaged<IntPtr, HitVec, HitVec, HitVec*, IntPtr, int> _hitCheck;
    private static delegate* unmanaged<IntPtr, int, long, int, void> _reset;
    private static IntPtr* _scene;
    /// <summary>The CHitInfo, then one ignore-list slot after it.</summary>
    private static byte* _info;

    /// <summary>The engine's own nudge past a hit before re-casting (UpdateNormalCameraCollision's 0.001).</summary>
    public const float Nudge = 0.001f;

    public static bool Armed => _info != null;

    public static void Init()
    {
        if (_info != null) return;
        _hitCheck = (delegate* unmanaged<IntPtr, HitVec, HitVec, HitVec*, IntPtr, int>)Native.Sym(SymbolMap.SceneHitCheck);
        _reset = (delegate* unmanaged<IntPtr, int, long, int, void>)Native.Sym(SymbolMap.HitInfoReset);
        _scene = (IntPtr*)Native.Sym(SymbolMap.CurrentScene);
        if (_hitCheck == null || _reset == null || _scene == null) return;
        _info = (byte*)NativeMemory.AllocZeroed(SizeOfHitInfo + 8);
    }

    /// <summary>The world scene being animated/rendered, or zero (no area, or a GUI panel's scene).</summary>
    public static IntPtr WorldScene()
    {
        IntPtr s = _scene == null ? IntPtr.Zero : *_scene;
        return s == IntPtr.Zero || *(IntPtr*)(s + SceneBspRoot) == IntPtr.Zero ? IntPtr.Zero : s;
    }

    /// <summary>Nearest hit on <paramref name="from"/>-&gt;<paramref name="to"/>, skipping <paramref name="ignore"/>
    /// (the followed gob). <paramref name="gob"/> is the gob hit, zero for a tile part.</summary>
    public static bool Cast(IntPtr scene, Vector3 from, Vector3 to, IntPtr ignore, out Vector3 hit, out IntPtr gob)
    {
        // mode 0 = BSP parts then gobs; -0x40000001 and box 0 are the camera's own arguments
        _reset((IntPtr)_info, 0, -0x40000001L, 0);
        *(IntPtr*)(_info + SizeOfHitInfo) = ignore;
        *(IntPtr**)(_info + HitInfoIgnoreList) = (IntPtr*)(_info + SizeOfHitInfo);
        *(int*)(_info + HitInfoIgnoreCount) = 1;
        HitVec h;
        int r = _hitCheck(scene, new HitVec { X = from.X, Y = from.Y, Z = from.Z }, new HitVec { X = to.X, Y = to.Y, Z = to.Z }, &h, (IntPtr)_info);
        hit = new Vector3(h.X, h.Y, h.Z);
        gob = r == 0 ? IntPtr.Zero : *(IntPtr*)(_info + HitInfoGob);
        return r != 0;
    }

    /// <summary>True when <paramref name="gob"/> is a creature's (never a camera obstacle, like the engine).</summary>
    public static bool IsCreature(IntPtr gob) => gob != IntPtr.Zero && *(int*)(gob + GobExternalType) == GobKindCreature;

    /// <summary>Unlink the SafePointer the last hit left behind -- now, not when the gob dies mid-frame.</summary>
    public static void Release() => _reset((IntPtr)_info, 0, -0x40000001L, 0);
}
