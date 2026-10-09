using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-109/CX-110's two A/Bs, in one group (ADR 0025: one declaration is the panel row, the
/// <c>cx.cfg</c> key and the <c>/view &lt;knob&gt; &lt;value&gt;</c> chat form). Each hands one step of the
/// per-frame scene pipeline back to the engine, which is how a live session proves Alloy's own answer before
/// the engine's is deleted. Both default ON -- the point of the tier is ownership.
/// <para>Advanced tier, not User: nothing here changes how the game looks when it is working. What it changes
/// is which code computed the numbers.</para></summary>
internal static class ViewKnobs
{
    internal static readonly KnobGroup G;
    /// <summary>CX-109: Alloy computes each view's viewport, projection, view matrix and depth range from the
    /// camera's own fields, and knows the world camera without the engine's latch.</summary>
    internal static readonly Knob<bool> CameraLoop;
    /// <summary>CX-110: Alloy runs its own wind clock, noise field and point-source list.</summary>
    internal static readonly Knob<bool> Wind;

    static ViewKnobs()
    {
        G = KnobGroup.Declare("view", "Graphics", "View, wind and reflections", parent: "alloy.enabled");
        G.Status = static () => $"camera {AlloyCamera.Describe()} | wind {AlloyWind.Describe()} "
                             + $"| envmap {AlloyEnvMap.Describe()}";

        G.Section("Camera");
        CameraLoop = G.Toggle("cameraloop", "CX computes the view and projection", true, Tier.Advanced,
            help: "CX derives each view's viewport, projection, view matrix and depth range from the camera "
                + "itself. Off, it reads the engine's matrix stacks instead -- the A/B for anything that looks "
                + "geometrically wrong (field of view, aspect, a tilted or mirrored world).");

        G.Section("Wind");
        Wind = G.Toggle("wind", "CX runs the wind", true, Tier.Advanced,
            help: "The wind that moves grass, cloaks and particles is CX's own clock and noise field. Off, the "
                + "engine's WindManager answers instead -- the A/B for wind that gusts too hard, too often or "
                + "not at all.");
    }

    /// <summary>Force the static constructor on the main thread at Initialize, the way every other group is
    /// touched from the code that reads it.</summary>
    internal static void Touch() { _ = CameraLoop; }
}
