using System.Runtime.InteropServices;

namespace CX.Engine;

/// <summary>An engine <c>Vector</c> as it sits in memory: three floats, x, y, z.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vec3
{
    public float X, Y, Z;
}

/// <summary>
/// An engine <c>Vector</c> crossing BY VALUE, in the one shape both ABIs lower correctly: SysV puts a
/// 12-byte all-float aggregate in xmm0 (x, y) + xmm1 (z), Win64 passes any by-value aggregate over 8 bytes as a
/// pointer to a caller-made copy. Because the C# TYPE NAME is the same on both, one declared <c>[Fn]</c> or
/// <c>sig:</c> signature names the parameter for both platforms.
/// </summary>
public unsafe struct VectorArg
{
#if WINDOWS
    /// <summary>Win64: the caller-made copy, which the callee owns -- a hook reads it BEFORE calling on.</summary>
    public Vec3* P;
#else
    /// <summary>SysV: the vector itself.</summary>
    public Vec3 V;
#endif

    /// <summary>The argument for a vector the caller holds. On Win64 the pointer IS the argument, so
    /// <paramref name="p"/> must outlive the call.</summary>
    public static VectorArg Of(Vec3* p) =>
#if WINDOWS
        new() { P = p };
#else
        new() { V = *p };
#endif

    /// <summary>The vector itself, whichever way it arrived.</summary>
    public Vec3 Value =>
#if WINDOWS
        *P;
#else
        V;
#endif
}
