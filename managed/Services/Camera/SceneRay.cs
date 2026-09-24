using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using static CX.Framework.EngineLayout;

namespace CX.Services.Camera;

/// <summary>One segment test through the engine's own <c>Scene::HitCheck</c> -- tile BSP parts then gobs,
/// nearest hit wins -- with the one <c>CHitInfo</c> its users share (#537 occlusion fade, #538
/// spring arm, CX-29 foot placement from inside <c>Gob::Animate</c>). The record is allocated once and never moves: a hit links it into the gob's SafePointer
/// list, so <see cref="Release"/> must unlink it before the frame ends. Hot: no allocation per cast.</summary>
public static unsafe class SceneRay
{
    [StructLayout(LayoutKind.Sequential)]
    private struct HitVec { public float X, Y, Z; }

    /// <summary>Both <c>Vector</c> arguments cross BY VALUE, an ABI that differs per OS, so this call keeps its
    /// hand-written shape (the declared form would have to name one order for both).</summary>
    private static delegate* unmanaged<IntPtr, HitVec, HitVec, HitVec*, IntPtr, int> _hitCheck;
    /// <summary>Resolved once: a cast runs per creature per frame (CX-29) and up to 15 times per spring arm.</summary>
    private static HitInfoResetFn _reset;
    private static IntPtr* _scene;
    private static int* _statics;
    /// <summary>The CHitInfo, then one ignore-list slot after it.</summary>
    private static byte* _info;

    /// <summary>The engine's own nudge past a hit before re-casting (UpdateNormalCameraCollision's 0.001).</summary>
    public const float Nudge = 0.001f;

    public static bool Armed => _info != null;

    public static void Init()
    {
        if (_info != null) return;
        _hitCheck = (delegate* unmanaged<IntPtr, HitVec, HitVec, HitVec*, IntPtr, int>)Native.Sym(SymbolMap.SceneHitCheck);
        _reset = EngineFn.HitInfoReset;
        _scene = (IntPtr*)Native.Sym(SymbolMap.CurrentScene);
        _statics = (int*)Native.Sym(SymbolMap.HitcheckStaticTrimesh);
        if (_hitCheck == null || _reset == null || _scene == null) return;
        _info = (byte*)NativeMemory.AllocZeroed(SizeOfHitInfo + 8);
    }

    /// <summary>The world scene being animated/rendered, or zero (no area, or a GUI panel's scene).</summary>
    public static IntPtr WorldScene()
    {
        IntPtr s = _scene == null ? IntPtr.Zero : *_scene;
        return s == IntPtr.Zero || new Scene(s).BspRoot == IntPtr.Zero ? IntPtr.Zero : s;
    }

    /// <summary>Nearest hit on <paramref name="from"/>-&gt;<paramref name="to"/>, skipping <paramref name="ignore"/>
    /// (the followed gob). <paramref name="gob"/> is the gob hit, zero for a tile part or a static placeable.
    /// <paramref name="statics"/> also tests static placeables (a boardwalk, a rug), as the engine's own
    /// footstep cast does -- a no-op on a client where the switch did not resolve.</summary>
    public static bool Cast(IntPtr scene, Vector3 from, Vector3 to, IntPtr ignore, out Vector3 hit, out IntPtr gob, bool statics = false)
    {
        var info = new CHitInfo((IntPtr)_info);
        // mode 0 = BSP parts then gobs; -0x40000001 and box 0 are the camera's own arguments
        _reset((IntPtr)_info, 0, -0x40000001L, 0);
        *(IntPtr*)(_info + SizeOfHitInfo) = ignore;
        info.IgnoreList = (IntPtr)(_info + SizeOfHitInfo);
        info.IgnoreCount = 1;
        HitVec h;
        int* sw = statics ? _statics : null;
        int was = sw == null ? 0 : *sw;
        if (sw != null) *sw = 1;
        int r = _hitCheck(scene, new HitVec { X = from.X, Y = from.Y, Z = from.Z }, new HitVec { X = to.X, Y = to.Y, Z = to.Z }, &h, (IntPtr)_info);
        if (sw != null) *sw = was;
        hit = new Vector3(h.X, h.Y, h.Z);
        gob = r == 0 ? IntPtr.Zero : info.Gob;
        return r != 0;
    }

    /// <summary>True when <paramref name="gob"/> is a creature's (never a camera obstacle, like the engine).</summary>
    public static bool IsCreature(IntPtr gob) => gob != IntPtr.Zero && new Gob(gob).ExternalType == GobKindCreature;
    public static bool IsPlaceable(IntPtr gob) => gob != IntPtr.Zero && new Gob(gob).ExternalType == GobKindPlaceable;

    /// <summary>Unlink the SafePointer the last hit left behind -- now, not when the gob dies mid-frame.</summary>
    public static void Release() => _reset((IntPtr)_info, 0, -0x40000001L, 0);
}
