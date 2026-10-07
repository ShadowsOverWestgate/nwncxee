using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The grass pass's frustum test, <c>BinOutside</c> @0x47a620's sphere-against-planes loop.</summary>
internal static unsafe partial class GrassField
{
    private const int BinPlaneFloats = 24;
    /// <summary>The six frustum planes the database's patch cull tests against.</summary>
    private static readonly float[] _binPlanes = new float[BinPlaneFloats];

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
}
