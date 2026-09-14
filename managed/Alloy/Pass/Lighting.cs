using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>light</c> group (spec §4.3 Lighting), and the storage <see cref="AlloyLights"/> and
/// <see cref="EnvProbe"/> read: the point-light curve's dials, the area-term scales, the wrap terms, the
/// receiver's departed-light fade, and the environment probe (the former <c>/probe</c> plus the
/// <c>probe</c>/<c>probe_size</c> boot keys). No master: lighting is the lit stage, and off is Renderer off.
/// Touched from <see cref="RenderCommands.Register"/> so it declares on the main thread at Initialize.</summary>
internal static class Lighting
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Gain, Ambient, Sun, Overbright, Sss, Wrap, WrapNdl, Hot, HotSize, Radius, Falloff,
                                         ProbeGain, BinReach;
    internal static readonly Knob<int> Fadeout, ProbeSize, BinCap;
    internal static readonly Knob<bool> Probe, ProbeAreaWide, ReachFade;

    static Lighting()
    {
        G = KnobGroup.Declare("light", "Graphics", "Lighting", parent: "alloy.enabled",
            header: "Lighting is the lit stage; off is Renderer off.");

        G.Section("Lights");
        Gain = G.Slider("gain", "Point-light brightness", 0f, 4f, 0.05f, 1f,
                        help: "Scales every placed and carried light. 1 is the stock brightness.");
        Ambient = G.Slider("ambient", "Ambient light", 0f, 4f, 0.05f, 1f,
                           help: "Scales the area's ambient fill. 1 is the area author's value.");
        Sun = G.Slider("sun", "Sun and moon", 0f, 4f, 0.05f, 1f,
                       help: "Scales the area's directional light. 1 is the area author's value.");
        Overbright = G.Slider("overbright", "Carried-light boost", 0f, 1f, 0.05f, 0f,
                              help: "How much of a torch's extra intensity shows at its centre instead of only as reach. "
                                  + "0 is the stock game; 0.5 makes carried lights stand out.");
        Sss = G.Slider("sss", "Soft skin lighting", 0f, 1f, 0.05f, 0.25f,
                       help: "Light wrapping past the shadow line on characters. 0 is flat; the stock game uses 0.38 at shader quality above medium.");
        Wrap = G.Slider("wrap", "Sphere-light wrap", 0f, 4f, 0.05f, 1f, tier: Tier.Advanced,
                        help: "Near lights soften the shadow line on low-poly models; 0 = point lights.");
        WrapNdl = G.Slider("wrapndl", "Diffuse wrap", 0f, 2f, 0.05f, 1f, tier: Tier.Advanced,
                           help: "Distance-independent softening of every terminator.");
        Hot = G.Slider("hot", "Near-field core", 0f, 2f, 0.05f, 0f, tier: Tier.Advanced,
                       help: "Adds intensity at a light's centre only.");
        HotSize = G.Slider("hotsize", "Core size", 0f, 1f, 0.01f, 0.15f, tier: Tier.Advanced,
                           help: "Fraction of the range the core covers.");
        Radius = G.Slider("radius", "Source radius", 0f, 4f, 0.05f, 1f, tier: Tier.Advanced,
                          help: "Wrap radius and self-shadow skip base.");
        Falloff = G.Slider("falloff", "Rim fade exponent", 0.1f, 4f, 0.05f, 1f, tier: Tier.Advanced,
                           help: "1 = stock falloff exactly.");
        Fadeout = G.Slider("fadeout", "Departed-light fade", 0, 120, 1, SceneInputs.FadeFramesBoot, "frames", Tier.Advanced,
                           help: "Frames a light takes to fade after it leaves.");
        G.Section("Reflections");
        Probe = G.Toggle("probe", "Reflections from the scene", true,
                         help: "Materials with an environment map reflect a capture of the surroundings instead of the stock sphere map.");
        ProbeGain = G.Slider("probegain", "Reflection strength", 0f, 4f, 0.05f, 1f, tier: Tier.Advanced,
                             help: "1 is the stock weighting.");
        ProbeAreaWide = G.Toggle("probeareawide", "Reflect on every surface", false, Tier.Advanced,
                                 help: "On, every material reflects the probe, not just those with an env map.");
        ProbeSize = G.Dropdown("probesize", "Reflection capture size", new[] { 64, 128, 256, 512 }, 128, tier: Tier.Advanced,
                               effect: Effect.Restart);
        G.Section("");
        BinReach = G.Slider("binreach", "binreach", 0.1f, 4f, 0.05f, 0.8f, tier: Tier.ChatOnly);
        BinCap = G.Slider("bincap", "bincap", 0, AlloyLights.SortCap, 1, 16, tier: Tier.ChatOnly);
        ReachFade = G.Toggle("reachfade", "reachfade", true, Tier.ChatOnly);
    }
}
