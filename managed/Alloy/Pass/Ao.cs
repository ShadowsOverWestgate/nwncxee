using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>ao</c> group (spec §4.3): the storage <see cref="SsaoPass"/> reads. No master -- the
/// game's own Ambient Occlusion video option is the switch; <see cref="On"/> is the chat-only bypass for an
/// A/B. Touched from <see cref="RenderCommands.Register"/> so the group declares at Initialize on the main
/// thread.
/// <para>Why the defaults: <c>radius</c> is world-space (NWN units are metres: a creature stands two of them)
/// and 0.6 is contact occlusion -- corners, feet, the underside of furniture -- not a room-scale gloom.
/// <c>strength</c> is the visibility exponent at stock intensity (GTAO's finalValuePower); the option scales
/// it. #390: 1 slice x 3 steps (6 depth fetches a pixel) under the temporal accumulator converges on what 3x3
/// (18 fetches) drew in one frame; only SLICES average across frames -- the horizon is a max over steps, so
/// fewer steps per frame is biased low, not noisy (the lost-power regression). <c>temporal 0 dirs 3 steps 3</c>
/// is the single-frame look exactly; at <c>temporal</c> 0 there is no history target, no per-frame noise phase
/// and no reprojection, and 0.98 is the ceiling because 1 is a frozen history. <c>maxpx</c> bounds the cost
/// when the camera is against a wall. <c>direct</c>: ADR 0021 gave the occlusion to the ambient alone, which
/// is invisible wherever the area ambient is dark -- every interior, where the light is lamps and the corners
/// are exactly what wants occluding; measured live, with ambient at its area value the effect did not reach
/// the frame at all. 1 (vanilla's post-multiply) is the only setting the effect is visible indoors at; 0
/// restores the ADR's original ambient-only rule.</para></summary>
internal static class Ao
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Radius, Strength, Direct, Temporal, MaxPx;
    internal static readonly Knob<int> Dirs, Steps;
    internal static readonly Knob<bool> Blur, On;

    static Ao()
    {
        G = KnobGroup.Declare("ao", "Graphics", "Ambient occlusion", parent: "alloy.enabled",
            header: "The game's own Ambient Occlusion video option is the switch.");
        G.Status = SsaoPass.Describe;

        Radius = G.Slider("radius", "Radius", 0.1f, 2f, 0.05f, 0.6f, "m",
                          help: "How far occlusion reaches: 0.6 darkens corners and feet; larger is a room-wide gloom.");
        Strength = G.Slider("strength", "Strength", 0.5f, 4f, 0.1f, 2.2f,
                            help: "Multiplied by the game's AO intensity slider.");
        Direct = G.Slider("direct", "Darken lit surfaces", 0f, 1f, 0.05f, 1f, tier: Tier.Advanced,
                          help: "0 = ambient only (invisible indoors).");
        Temporal = G.Slider("temporal", "Temporal accumulation", 0f, 0.98f, 0.02f, 0.8f, tier: Tier.Advanced,
                            help: "0 = single frame.", changed: SsaoPass.DropHistory);
        Dirs = G.Slider("dirs", "Slices", 1, 8, 1, 1, tier: Tier.Advanced);
        Steps = G.Slider("steps", "Steps", 1, 8, 1, 3, tier: Tier.Advanced);
        MaxPx = G.Slider("maxpx", "Screen radius cap", 16f, 256f, 8f, 96f, "px", Tier.Advanced);
        Blur = G.Toggle("blur", "Blur", true, Tier.Advanced);
        On = G.Toggle("on", "on", true, Tier.ChatOnly);
    }
}
