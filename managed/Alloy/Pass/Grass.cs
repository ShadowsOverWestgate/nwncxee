using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>grass</c> group (spec §4.3), and the storage <see cref="GrassField"/> reads: one
/// declaration per dial, bound from <c>cx.cfg</c> on first touch, set by <c>/grass</c> or the panel. The
/// Restart rows are read through <c>Boot</c>. Touched from
/// <see cref="RenderCommands.Register"/> so the group declares at Initialize, on the main thread, whether or
/// not the pass arms.
/// <para>Why the defaults: <c>windspeed</c> is a dial rather than a derivation -- uTimeOfDay is the engine's
/// worldtimerTimeOfDay int and the stock wind shaders divide it by five-digit per-material constants, so
/// there is no one divisor to inherit. <c>soften</c> is a mip bias, the knob to reach for LAST (a positive
/// bias on a 2x2 atlas averages neighbouring blade variants); <c>aniso</c> earns its keep because grass is
/// looked at edge-on. <c>push</c> 1 flung a blade a full height sideways, a blast crater rather than parted
/// grass. <c>cullshrink</c> below 1 fringes an object rather than stopping at a hard line; <c>cullmax</c>
/// exists for the tree, whose box is its CANOPY. #355's ladder: inside <c>lod</c> every blade draws; past
/// it a patch draws the <c>Keep</c> prefix of its shuffled blades (never below <c>lodmin</c>), widened by
/// <c>keep^-lodwidth</c> and grown by <c>keep^-lodheight</c>; <c>shade</c> is where the fragment stage
/// stops taking the taps; <c>far</c> overrides the player's grass-distance option (the engine clamps that at
/// 173u). <c>softedge</c> = the masked-core-plus-blended-fringe foliage technique, needs the prepass.</para></summary>
internal static class Grass
{
    /// <summary><see cref="Style"/>'s values: CX-24's procedural blades (default), or the atlas cards.</summary>
    internal const int StyleBlades = 0, StyleTextures = 1;

    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<float> Density, Far, Wind, Push, WindSpeed, Lod, LodMin, LodWidth, LodHeight, Shade,
                                         CastWidth, PushRadius, PushExtent, CullShrink, CullMax, Soften, Edge, RootAo, GTint,
                                         SnowThin, SnowFall, SnowLook, Backlight, Sheen,
                                         BladeDensity, Width, Clump, BladeLod;
    internal static readonly Knob<int> Style, Shadow, Tuft, Aniso, GViz;
    internal static readonly Knob<bool> Cull, SoftEdge, VLight, Prepass, PushVis;
    internal static readonly KnobButton DumpMips, DumpShadow;

    static Grass()
    {
        G = KnobGroup.Declare("grass", "Graphics", "Grass", master: "Enable Alloy grass",
            masterHelp: "CX's own grass: denser, wind-swayed, lit and shadowed. Off, the stock grass draws.",
            parent: "alloy.enabled");
        Enabled = G.Master!;
        G.Status = GrassField.Describe;

        G.Section("Field");
        Style = G.Dropdown("style", "Grass style", new[] { StyleBlades, StyleTextures }, StyleBlades,
                           new[] { "Procedural blades", "Game textures" }, changed: GrassField.Rebake,
                           help: "Procedural blades: dense, individually shaded 3D blades coloured from the area's grass. "
                               + "Game textures: the area's own grass pictures, as the stock game draws them. "
                               + "Grass textures that look like flowers stay as pictures either way.");
        BladeDensity = G.Slider("bladedensity", "Blade density", 1f, 32f, 1f, 8f, changed: GrassField.Rebake,
                                help: "Procedural blades per stock grass tuft, on top of Density.");
        Density = G.Slider("density", "Density", 0f, 4f, 0.25f, 2f, help: "Blades per patch relative to the stock game.",
                           changed: GrassField.Rebake);
        Far = G.Slider("far", "Distance", 0f, 1000f, 10f, 0f, "m",
                       help: "How far grass draws. 0 follows the game's Grass Distance option (which stops at 173).");
        G.Section("Wind");
        Wind = G.Slider("wind", "Wind strength", 0f, 4f, 0.1f, 1f, help: "How far the blades sway. 0 stands them still.");
        G.Section("Snow");
        SnowThin = G.Slider("snowthin", "Thinning under snow", 0f, 1f, 0.05f, 0.85f,
                            help: "Share of the blades that vanish under full snow cover (the rest stand shorter). 0 keeps a summer meadow.");
        SnowFall = G.Slider("snowfall", "Density while snowing", 0f, 1f, 0.05f, 0.1f,
                            help: "Share of the blades that stand while snow is falling, whatever the cover. 1 leaves the field alone.");
        SnowLook = G.Slider("snowlook", "Winter look", 0f, 1f, 0.05f, 1f,
                            help: "Blades left standing go straw-coloured and take snow on their tips. 0 keeps them green.");
        G.Section("Shadows");
        Shadow = G.Dropdown("shadow", "Grass casts shadows", new[] { 0, 1, 2, 3, 4 }, 1, new[] { "none", "1", "2", "3", "4" },
                            help: "How many sun cascades the blades are drawn into. More is sharper far away and costlier.");
        G.Section("Field");
        Push = G.Slider("push", "Parts around characters", 0f, 1f, 0.05f, 0.15f,
                        help: "Blades bend away from creatures walking through them. 0 is off.");
        G.Section("Wind");
        WindSpeed = G.Slider("windspeed", "Wind speed", 0f, 0.01f, 0.0001f, 0.001f, tier: Tier.Advanced);
        G.Section("Distance");
        Lod = G.Slider("lod", "Full-density radius", 5f, 200f, 1f, 40f, "m", Tier.Advanced, help: "Every blade draws inside this.");
        LodMin = G.Slider("lodmin", "Far density floor", 0.001f, 1f, 0.001f, 0.02f, tier: Tier.Advanced);
        LodWidth = G.Slider("lodwidth", "Far blade widening", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced);
        LodHeight = G.Slider("lodheight", "Far blade growth", 0f, 1f, 0.05f, 0.25f, tier: Tier.Advanced);
        BladeLod = G.Slider("bladelod", "Full-detail blade radius", 5f, 200f, 1f, 30f, "m", Tier.Advanced,
                            help: "Procedural blades inside this are smooth curves; past it each is a single triangle.");
        Shade = G.Slider("shade", "Full-shading radius", 5f, 200f, 1f, 40f, "m", Tier.Advanced,
                         help: "Past this blades skip light and shadow taps.");
        G.Section("Shadows");
        CastWidth = G.Slider("castwidth", "Shadow blade width", 0.5f, 8f, 0.1f, 1f, tier: Tier.Advanced);
        G.Section("Field");
        PushRadius = G.Slider("pushradius", "Push radius", 0f, 4f, 0.1f, 1f, tier: Tier.Advanced);
        PushExtent = G.Slider("pushextent", "Push field extent", 8f, 128f, 8f, 64f, "m", Tier.Advanced);
        Cull = G.Toggle("cull", "Clear grass under objects", true, Tier.Advanced);
        CullShrink = G.Slider("cullshrink", "Object footprint scale", 0f, 2f, 0.05f, 0.8f, tier: Tier.Advanced);
        CullMax = G.Slider("cullmax", "Footprint cap", 0f, 20f, 0.5f, 0f, "m", Tier.Advanced);
        Tuft = G.Slider("tuft", "Blades per tuft", 1, 8, 1, 3, tier: Tier.Advanced);
        G.Section("Look");
        Soften = G.Slider("soften", "Texture softening", 0f, 2f, 0.05f, 0.5f, tier: Tier.Advanced,
                          help: "Mip bias on the blade texture.", changed: GrassField.SamplerDirty);
        Aniso = G.Dropdown("aniso", "Anisotropic filtering", new[] { 1, 2, 4, 8, 16 }, 8, tier: Tier.Advanced,
                           changed: GrassField.SamplerDirty);
        Edge = G.Slider("edge", "Edge ramp width", 0f, 4f, 0.1f, 2f, tier: Tier.Advanced);
        SoftEdge = G.Toggle("softedge", "Blended silhouettes", true, Tier.Advanced);
        RootAo = G.Slider("rootao", "Root darkening", 0f, 1f, 0.05f, 0f, tier: Tier.Advanced);
        GTint = G.Slider("gtint", "Ground tint", 0f, 1f, 0.05f, 0.15f, tier: Tier.Advanced,
                         help: "Blade fades into the ground colour under its root.", changed: GrassField.GroundDiag);
        Width = G.Slider("width", "Blade width", 0.02f, 0.4f, 0.01f, 0.08f, tier: Tier.Advanced,
                         help: "Procedural blade width at the root, relative to its height.");
        Clump = G.Slider("clump", "Clumping", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced,
                         help: "Neighbouring procedural blades lean together and share a height, so the field grows in tufts.");
        Backlight = G.Slider("backlight", "Backlit glow", 0f, 2f, 0.05f, 0.5f, tier: Tier.Advanced,
                             help: "How much low sun shines through the grass when you look toward it.");
        Sheen = G.Slider("sheen", "Sheen", 0f, 1f, 0.05f, 0.1f, tier: Tier.Advanced, help: "Specular glint on the grass.");
        VLight = G.Toggle("vlight", "Per-vertex lighting", true, Tier.Advanced, help: "Off = per-pixel (costlier).");
        G.Section("Field");
        G.Section("");
        Prepass = G.Toggle("prepass", "prepass", true, Tier.ChatOnly);
        PushVis = G.Toggle("pushvis", "pushvis", true, Tier.ChatOnly);
        GViz = G.Slider("gviz", "gviz", 0, 4, 1, 0, tier: Tier.ChatOnly, changed: GrassField.GroundDiag);
        DumpMips = G.Button("dumpmips", "dumpmips", GrassField.DumpMipsNext, Tier.ChatOnly);
        DumpShadow = G.Button("dumpshadow", "dumpshadow", GrassField.DumpShadowNext, Tier.ChatOnly);
    }
}
