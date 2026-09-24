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
