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
                                         ProbeGain, BinReach, Flicker, FlickerSpeed, AmbientDir,
                                         Daylight, DayLow, DayHigh, SsrRough, SsrPlanarCull, Relevance;
    internal static readonly Knob<int> Fadeout, ProbeSize, BinCap, SsrDetail, SsrPlanarRes, MaxLit;
    internal static readonly Knob<bool> Probe, ReachFade, HiddenLights, Brdf, SpecAa, Ssr, SsrPlanar, Registry;

    static Lighting()
    {
        G = KnobGroup.Declare("light", "Graphics", "Lighting", parent: "alloy.enabled",
            header: "Lighting is the lit stage; off is Renderer off.");
        G.Status = AlloyLights.DayStatus;

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
        Flicker = G.Slider("flicker", "Fire flicker", 0f, 2f, 0.05f, 1f,
                           help: "Animated lights (torches, braziers) keep a steady reach and flicker in brightness instead of pulsing in size. "
                               + "1 flickers as deep as the pulse was made; 0 is the stock pulse.");
        Daylight = G.Slider("daylight", "Daylight outshines lamps", 1f, 10f, 0.25f, 3f,
                            help: "How much brighter full daylight is than a lamp. Lamps and their glow fade as the day brightens "
                                + "and come back at dusk. 1 is the stock game: a torch looks the same at noon as at midnight.");
        DayLow = G.Slider("daylow", "Daylight starts at", 0f, 1f, 0.01f, 0.1f, tier: Tier.Advanced,
                          help: "Sun brightness (linear) below which lamps are at full strength.");
        DayHigh = G.Slider("dayhigh", "Full daylight at", 0f, 2f, 0.01f, 0.6f, tier: Tier.Advanced,
                           help: "Sun brightness (linear) at which lamps are dimmed the full Daylight amount.");
        FlickerSpeed = G.Slider("flickerspeed", "Flicker speed", 0.1f, 4f, 0.05f, 1f, tier: Tier.Advanced,
                                help: "Rate of the flicker; 1 is a fireplace.");
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
        // CX-108: the A/B for the light registry. Default ON -- owning the enumeration is the point of the
        // ticket -- and 0 puts the light walk back on the engine's own LightManager lists, which is the one
        // question worth asking first if lighting looks wrong after this.
        Registry = G.Toggle("registry", "Track lights ourselves", true, Tier.Advanced,
                            help: "Alloy keeps its own list of the area's lights, so a light appears, fades and dies with "
                                + "the thing that carries it. Off reads the game's own per-frame light list instead.");
        MaxLit = G.Slider("maxlit", "Lights lit at once", 0, 256, 1, 0, tier: Tier.Advanced,
                          help: "0 follows the game's own Max Lights video option (it caps at 128); a higher number lights "
                              + "more of a dense area at once, and 256 effectively removes the limit.");
        G.Section("Reflections");
        Probe = G.Toggle("probe", "Reflections from the scene", true,
                         help: "Materials with an environment map reflect a capture of the surroundings instead of the stock sphere map.");
        ProbeGain = G.Slider("probegain", "Reflection strength", 0f, 4f, 0.05f, 1f, tier: Tier.Advanced,
                             help: "1 is the stock weighting. Fainter reflections are also cheaper: too faint a one traces no ray.");
        ProbeSize = G.Dropdown("probesize", "Reflection capture size", new[] { 64, 128, 256, 512 }, 64, tier: Tier.Advanced,
                               effect: Effect.Restart);
        Ssr = G.Toggle("ssr", "Screen-space reflections", true,
                       help: "Glossy and rain-wet surfaces also reflect what is on screen -- grass, creatures, anything drawn -- "
                           + "and keep the capture where a reflection leaves the screen.");
        SsrRough = G.Slider("ssrrough", "Screen-space reflection roughness", 0.05f, 1f, 0.05f, 0.5f, tier: Tier.Advanced,
                            help: "Surfaces rougher than this keep the capture alone. Lower is cheaper.");
        SsrDetail = G.Dropdown("ssrdetail", "Screen-space reflection detail", new[] { 0, 1, 2 }, 2, new[] { "full", "half", "quarter" },
                               tier: Tier.Advanced,
                               help: "The resolution a reflection finds what it hits at. Lower is cheaper and blockier.");
        SsrPlanar = G.Toggle("ssrplanar", "Mirror reflections on flat ground", true,
                             help: "Wet ground at the player's height reflects a second, mirrored draw of the scene: no holes behind "
                                 + "figures, and undersides the camera never sees. Costs a low-resolution redraw of the opaque set.");
        SsrPlanarRes = G.Dropdown("ssrplanarres", "Mirror reflection detail", new[] { 2, 3, 4, 6, 8 }, 8,
                                  new[] { "half", "third", "quarter", "sixth", "eighth" },
                                  tier: Tier.Advanced,
                                  help: "The resolution the mirrored scene is drawn at. Lower is cheaper and softer.");
        SsrPlanarCull = G.Slider("ssrplanarcull", "Mirror reflection small-object cull", 0f, 16f, 0.5f, 2f, "px", Tier.Advanced,
                                 help: "Objects smaller than this many pixels of the mirrored draw are left out of it. Higher is cheaper.");
        HiddenLights = G.Toggle("hidden", "Hidden lights do not reflect", true,
                                help: "A wet or glossy surface mirrors a light only where that light's shadow lets it -- a lamp on a terrace reflects in the street from where the ground sees over the parapet, not from the wall's foot.");
        G.Section("Materials");
        Brdf = G.Toggle("brdf", "Modern materials", true,
                        help: "Energy-conserving highlights, reflections that brighten at grazing angles and fade in crevices, and the "
                            + "extra material layers (metal, clear coat, sheen, glow) a .mtr can declare for Alloy. Off is the stock shading exactly.");
        AmbientDir = G.Slider("ambientdir", "Directional ambient", 0f, 1f, 0.05f, 0.5f,
                              help: "Ambient light takes its direction from the surroundings (sky above, ground below) at the area's own "
                                  + "brightness. 0 is the stock sun-facing shading. Needs Modern materials and Reflections from the scene.");
        SpecAa = G.Toggle("specaa", "Smooth highlights", true, Tier.Advanced,
                          help: "Widens a highlight that is narrower than a pixel, so glossy and normal-mapped surfaces do not sparkle "
                              + "or crawl in motion. Needs Modern materials.");
        G.Section("");
        BinReach = G.Slider("binreach", "Light reach", 0.1f, 4f, 0.05f, 0.8f, tier: Tier.Advanced,
                            help: "Fraction of each light's authored radius that lights the scene. 1 is the stock radius; lower is cheaper.");
        BinCap = G.Slider("bincap", "Lights per cluster", 0, AlloyLights.SortCap, 1, 0, tier: Tier.Advanced,
                          help: "0 keeps every light and lets the faint-light cutoff thin them per pixel. A cap keeps the N strongest per screen cluster: cheaper, but it draws the cluster grid where more lights overlap.");
        Relevance = G.Slider("relevance", "Faint-light cutoff", 0f, 0.1f, 0.005f, 0.01f, tier: Tier.Advanced,
                             help: "A light dimmer than this share of the brightest light at a pixel fades out and is skipped. Decided per pixel, so it never draws a grid. 0 = every light.");
        ReachFade = G.Toggle("reachfade", "reachfade", true, Tier.ChatOnly);
    }
}
