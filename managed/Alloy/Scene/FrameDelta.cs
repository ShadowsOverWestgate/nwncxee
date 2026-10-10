using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The engine's own frame delta, in seconds -- the one number every ported integrator steps on, so
/// Alloy's clock and the game's cannot drift.</summary>
internal static unsafe class FrameDelta
{
    /// <summary><c>ControlLightsFade</c>'s own delta floor: <c>max(DeltaT, 0.0001)</c>, so a stalled frame
    /// still advances an integrator.</summary>
    internal const float DtFloor = 0.0001f;

    private static float* _deltaT;

    /// <summary>Resolved? False stands the whole Alloy path down (the shim reports it): an integrator with no
    /// clock would run at whatever rate the frame happens to have.</summary>
    internal static bool Ready { get; private set; }

    /// <summary>The symbol this build could not resolve, for the shim's one-line refusal.</summary>
    internal static string? Missing { get; private set; }

    internal static void Resolve(ExtenderLog log)
    {
        _deltaT = (float*)Native.Sym(SymbolMap.DeltaT);
        Missing = _deltaT == null ? SymbolMap.DeltaT : null;
        Ready = Missing == null;
    }

    /// <summary>The engine's own frame delta in seconds, floored -- every ported integrator steps on the same
    /// number the engine's own do.</summary>
    internal static float Delta(float floor) => _deltaT == null ? 0f : MathF.Max(*_deltaT, floor);

    internal static string Describe() =>
        !Ready ? $"frame delta UNAVAILABLE ({Missing})" : $"frame delta {Delta(DtFloor) * 1000f:F2} ms";
}
