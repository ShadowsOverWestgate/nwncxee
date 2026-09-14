using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>effects</c> group (spec §4.3): the sky, beam and halo passes' storage, one group because
/// each is one toggle. The halo pair that used to be a boot key AND a <c>/halos</c> dial is one live knob
/// each: a chat set persists and is what the next launch reads. Touched from
/// <see cref="RenderCommands.Register"/> so the group declares at Initialize on the main thread.</summary>
internal static class Effects
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Sky, Beams, Halos;
    internal static readonly Knob<float> HaloSize, HaloGain;
    internal static readonly Knob<int> HaloViz;

    static Effects()
    {
        G = KnobGroup.Declare("effects", "Graphics", "Effects", parent: "alloy.enabled");
        G.Status = static () => $"{SkyBox.Stats()} | {CX.Alloy.Beams.Stats()} | halos {CX.Alloy.Halos.Describe()}";

        G.Section("Sky");
        Sky = G.Toggle("sky", "Alloy sky", true,
                       help: "The sky dome drawn through CX (fogged horizon, HDR). Off, the stock sky draws.");
        G.Section("Lights");
        Beams = G.Toggle("beams", "Alloy light beams", true, help: "Light shafts drawn through CX.");
        Halos = G.Toggle("halos", "Light halos", true, help: "A soft glow around every light source.");
        HaloSize = G.Slider("halosize", "Halo size", 0f, 2f, 0.1f, 0.6f,
                            help: "World size of the glow, as a multiple of the light's own flare size.");
        HaloGain = G.Slider("halogain", "Halo brightness", 0f, 4f, 0.1f, 1f,
                            help: "Scales the glow's intensity. 1 is the light's own colour.");
        G.Section("");
        HaloViz = G.Slider("haloviz", "haloviz", 0, 1, 1, 0, tier: Tier.ChatOnly);
    }
}
