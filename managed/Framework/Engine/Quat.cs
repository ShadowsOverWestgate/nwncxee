using System.Numerics;

namespace CX.Engine;

/// <summary>An engine quaternion as it sits in memory: (w, x, y, z), where <see cref="Quaternion"/> is (x, y, z, w).
/// <c>Camera::Control</c> reads the camera's in this order.</summary>
public struct Quat
{
    public float W, X, Y, Z;

    public static implicit operator Quaternion(Quat q) => new(q.X, q.Y, q.Z, q.W);
    public static implicit operator Quat(Quaternion q) => new() { W = q.W, X = q.X, Y = q.Y, Z = q.Z };
}

/// <summary>An engine <c>Plane</c> as it sits in memory and as it crosses the ABI: four plain floats, normal
/// then distance. Deliberately NOT <see cref="System.Numerics.Plane"/>, whose <c>Normal</c> is the
/// <c>[Intrinsic]</c> <see cref="System.Numerics.Vector3"/> -- a type the compiler is free to treat as a SIMD
/// value, which is not a promise one wants underneath an <c>[UnmanagedCallersOnly]</c> signature where the
/// classification decides whether the engine reads xmm0:xmm1 or a buffer. <see cref="Vec3"/> and
/// <see cref="Quat"/> exist for the same reason.</summary>
public struct AurPlane
{
    public float X, Y, Z, D;

    public static implicit operator System.Numerics.Plane(AurPlane p)
        => new(p.X, p.Y, p.Z, p.D);

    public static implicit operator AurPlane(System.Numerics.Plane p)
        => new() { X = p.Normal.X, Y = p.Normal.Y, Z = p.Normal.Z, D = p.D };
}
