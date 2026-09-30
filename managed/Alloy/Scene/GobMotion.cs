using System;

namespace CX.Alloy;

/// <summary>CX-102 seam: Alloy's own runtimes (the animation runtime, the behaviour solvers) report every gob
/// whose placement or pose they wrote this frame, so the transform owner recomposes exactly those. The body is
/// <see cref="GobXform.Mark"/>: one hash probe, no allocation, safe from any point in the frame.</summary>
internal static class GobMotion
{
    /// <summary><paramref name="gob"/>'s position, orientation, scale or a part local changed this frame.</summary>
    internal static void Mark(IntPtr gob) => GobXform.Mark(gob);
}
