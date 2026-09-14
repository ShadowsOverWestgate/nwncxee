using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>post</c> group (spec §4.3): the storage <see cref="PostChain"/>, <see cref="PostStack"/>
/// and <see cref="ToonStage"/> read. No master -- the resolve IS the Alloy frame. <c>hdr</c> and the render
/// scale are the Renderer group's (<c>alloy.hdr</c>, <c>alloy.scale</c>), not this one's. Touched from
/// <see cref="RenderCommands.Register"/> so the group declares at Initialize on the main thread.
/// <para>Why the defaults: <c>exposure</c> is scene-referred, one linear gain on the resolved frame (bloom
/// included) BEFORE the tonemap, so blacks stay put and the shoulder takes what it pushes past 1 -- Alloy's
/// own brightness decision, not a port of anyone's chain (the stock SoW frame gets its lift from two
/// display-space ACES passes and a grade), and what makes <c>tonemap aces</c> usable at all: the Narkowicz
/// fit sits below NWN's radiance levels unexposed. <c>auto</c> (#491) is histogram eye adaptation -- a
/// compute histogram of log2 luminance over the HDR target gives a log-average, a one-thread dispatch eases
/// a gain toward <c>AutoKey / average</c>, the resolve multiplies it in AFTER the fixed exposure; off is
/// bit-exact the fixed image. <c>automin/automax</c> bound the AUTO factor, not the total; <c>autospeed</c> is
/// the rate (1/s) when the gain RISES (into the dark, the slow one), <c>autodown</c> when it falls (the brief
/// over-exposure that settles is this rate); settle time is about 3/rate seconds. <c>acesmix</c> mixes the
/// ACES curve in LINEAR against the shoulder: 1 is the full fit, whose toe crushes NWN's dim interiors
/// (0.01 linear comes out 2.6x darker, so only specular survives and a character in the dark reads as
/// "glossy"); 0.5 is xtul's live call. <c>dither</c> 1.0 is the correct value, not taste: one LSB of
/// triangular-PDF noise is exactly what decorrelates the quantisation step, the difference between a smooth
/// gradient and contour bands. <c>taablend</c> is the current frame's share of the blend at rest (0.1 = a
/// ten-frame history); <c>taau</c> keeps the TAA history at DISPLAY size under a render scale below 1.
/// <c>viz</c> shows one STAGE of the chain -- 1 the bloom alone, 2 the HDR colour alone, 3 the bloom at 20x,
/// 4 the velocity target -- and <c>clearviz</c> clears the HDR target to magenta, a value nothing else
/// produces, so magenta in the frame is a pixel NO pass covered. <c>motion</c> (#388) arms the motion-vector
/// pass on its own; TAA and temporal GTAO run it regardless.</para></summary>
internal static class Post
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Exposure, Bloom, AutoMin, AutoMax, AutoSpeed, AutoDown, Threshold, Knee,
                                         AcesMix, TmKnee, Dither, TaaBlend, ToonBands, ToonInk, ToonEdge;
    internal static readonly Knob<bool> Auto, Taa, Taau, Stack, FbPost, Motion, ClearViz;
    internal static readonly Knob<int> Tonemap, Viz;

    /// <summary>The tonemap operators, index-aligned with <see cref="PostChain"/>'s <c>Off</c> /
    /// <c>Shoulder</c> / <c>Aces</c>; the cfg and chat form is the name (<c>post.tonemap=aces</c>).</summary>
    private static readonly string[] TonemapNames = { "off", "shoulder", "aces" };

    static Post()
    {
        G = KnobGroup.Declare("post", "Graphics", "Post-processing", parent: "alloy.enabled",
            header: "The resolve is the frame: there is no off. Renderer off is off.");
        G.Status = PostChain.Describe;

        G.Section("Exposure");
        Exposure = G.Slider("exposure", "Exposure", 0.25f, 4f, 0.05f, 1f,
                            help: "Overall brightness before tone mapping. 1 is neutral.");
        Auto = G.Toggle("auto", "Auto exposure", true,
                        help: "The image adapts like an eye: brighter in the dark, dimmer in the light.",
                        changed: PostChain.AutoRestart);
        G.Section("Bloom");
        Bloom = G.Slider("bloom", "Bloom", 0f, 0.5f, 0.01f, 0.06f, help: "Glow around bright lights. 0 is off.");
        G.Section("Tone");
        Tonemap = G.Dropdown("tonemap", "Tone mapping", new[] { 0, 1, 2 }, 2,
                             help: "How bright values above white are compressed.",
                             format: v => (uint)v < 3u ? TonemapNames[v] : "aces");
        G.Section("Anti-aliasing");
        Taa = G.Toggle("taa", "Temporal anti-aliasing", false, help: "Smooths edges over several frames.");
        Taa.Changed = () => { if (Taa.V) TaaStage.Reset(); };
        G.Section("Exposure");
        AutoMin = G.Slider("automin", "Auto exposure minimum", 0.01f, 1f, 0.01f, 0.5f, tier: Tier.Advanced);
        AutoMax = G.Slider("automax", "Auto exposure maximum", 1f, 8f, 0.1f, 2f, tier: Tier.Advanced);
        AutoSpeed = G.Slider("autospeed", "Adapt to dark speed", 0.1f, 10f, 0.1f, 1f, "/s", Tier.Advanced);
        AutoDown = G.Slider("autodown", "Adapt to light speed", 0.1f, 10f, 0.1f, 3f, "/s", Tier.Advanced);
        G.Section("Bloom");
        Threshold = G.Slider("threshold", "Bloom threshold", 0f, 4f, 0.05f, 1f, tier: Tier.Advanced);
        Knee = G.Slider("knee", "Bloom knee", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced);
        G.Section("Tone");
        AcesMix = G.Slider("acesmix", "ACES mix", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced);
        TmKnee = G.Slider("tmknee", "Shoulder knee", 0f, 1f, 0.05f, 0.8f, tier: Tier.Advanced);
        Dither = G.Slider("dither", "Output dither", 0f, 2f, 0.1f, 1f, tier: Tier.Advanced);
        G.Section("Anti-aliasing");
        // Never 0 (a frozen history would never converge) and never above 0.5 (the raw jitter, "an earthquake").
        TaaBlend = G.Slider("taablend", "TAA history weight", 0.02f, 0.5f, 0.01f, 0.1f, tier: Tier.Advanced);
        Taau = G.Toggle("taau", "Temporal upsampling", true, Tier.Advanced, help: "With render scale below 1.",
                        changed: TaaStage.Reset);
        G.Section("Stock effects");
        Stack = G.Toggle("stack", "Game post-effects", true, Tier.Advanced,
                         help: "The stock framebuffer effects (gamma, SMAA, toon…) on the Alloy frame.");
        FbPost = G.Toggle("fbpost", "Hosted content post", true, Tier.Advanced);
        ToonBands = G.Slider("toonbands", "Toon bands", 1f, 32f, 1f, 1f, tier: Tier.Advanced,
                             help: "With the game's Toon option; 1 = no posterise.");
        ToonInk = G.Slider("toonink", "Toon ink", 0f, 1f, 0.05f, 1f, tier: Tier.Advanced);
        ToonEdge = G.Slider("toonedge", "Toon edge", 1f, 8f, 0.5f, 1f, tier: Tier.Advanced);
        G.Section("");
        Motion = G.Toggle("motion", "motion", false, Tier.ChatOnly);
        Viz = G.Slider("viz", "viz", 0, PostChain.VizMotion, 1, 0, tier: Tier.ChatOnly);
        ClearViz = G.Toggle("clearviz", "clearviz", false, Tier.ChatOnly);
    }
}
