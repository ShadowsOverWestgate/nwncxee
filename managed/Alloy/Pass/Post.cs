using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>post</c> group: the storage <see cref="PostChain"/>, <see cref="PostStack"/> and
/// <see cref="ToonStage"/> read. No master -- the resolve IS the Alloy frame. <c>hdr</c> and the render scale
/// are the Renderer group's (<c>alloy.hdr</c>, <c>alloy.scale</c>). Touched from
/// <see cref="RenderCommands.Register"/> so the group declares at Initialize on the main thread.
/// <para>Why the defaults: <c>exposure</c> is scene-referred, one linear gain on the resolved frame (bloom
/// included) BEFORE the tonemap, so blacks stay put and the shoulder takes what it pushes past 1 -- what
/// makes <c>tonemap aces</c> usable at all, since the Narkowicz fit sits below NWN's radiance levels
/// unexposed. There is no auto exposure: eye adaptation re-graded every area away from the lighting its
/// builder chose, so the exposure is fixed. <c>acesmix</c> mixes the
/// ACES curve in LINEAR against the shoulder: 1 is the full fit, whose toe crushes NWN's dim interiors (0.01
/// linear comes out 2.6x darker, so only specular survives); 0.5 is the live call. <c>tonemap neutral</c> is
/// Khronos PBR Neutral (CX-47), no knee or mix: a 0.04 offset below 0.76 whose toe darkens 0.01 linear 16x,
/// so the dim interior is the A/B leg to watch.
/// <c>dither</c> 1.0 is the
/// correct value, not taste: one LSB of triangular-PDF noise is exactly what decorrelates the quantisation
/// step. <c>taablend</c> is the current frame's share of the blend at rest (0.1 = a ten-frame history);
/// <c>taau</c> keeps the TAA history at DISPLAY size under a render scale below 1. <c>taa</c> ships ON and is
/// the default anti-aliaser; SMAA stands aside while it runs (<see cref="SmaaStage.Active"/>), so
/// <c>/post taa 0</c> is the A/B back to it. <c>taasharp</c> is the unsharp mask the copy-out applies to the
/// resolved history; 0.3 puts the resolve beside SMAA rather than behind it. <c>halfres</c> runs the
/// screen-space ambient occlusion at half the render extent, upsampled depth-aware in the lit stage; the
/// temporal accumulator hides the coarser estimate. <c>viz</c> shows one STAGE of the chain -- 1 the bloom, 2
/// the HDR colour, 3 the bloom at 20x, 4 the velocity target -- and <c>clearviz</c> clears the HDR target to
/// magenta, a value nothing else produces, so magenta in the frame is a pixel NO pass covered. <c>motion</c>
/// arms the motion-vector pass on its own; TAA and temporal GTAO run it regardless (#388). <c>skyraw</c> is
/// the A/B for the resolve's sky exemption: 0 exposes and tonemaps the sky like the rest (CX-374).</para></summary>
internal static class Post
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Exposure, Bloom, Threshold, Knee,
                                         AcesMix, TmKnee, Dither, TaaBlend, TaaSharp, ToonBands, ToonInk, ToonEdge;
    internal static readonly Knob<bool> Taa, Taau, HalfRes, Stack, FbPost, Motion, ClearViz, SkyRaw;
    internal static readonly Knob<int> Tonemap, Viz;

    static Post()
    {
        G = KnobGroup.Declare("post", "Graphics", "Post-processing", parent: "alloy.enabled",
            header: "The resolve is the frame: there is no off. Renderer off is off.");
        G.Status = PostChain.Describe;

        G.Section("Exposure");
        Exposure = G.Slider("exposure", "Exposure", 0.25f, 4f, 0.05f, 1f,
                            help: "Overall brightness before tone mapping. 1 is neutral.");
        G.Section("Bloom");
        Bloom = G.Slider("bloom", "Bloom", 0f, 0.5f, 0.01f, 0.06f, help: "Glow around bright lights. 0 is off.");
        G.Section("Tone");
        Tonemap = G.Dropdown("tonemap", "Tone mapping", new[] { PostChain.TonemapShoulder, PostChain.TonemapAces, PostChain.TonemapNeutral },
                             PostChain.TonemapAces,
                             help: "How bright values above white are compressed.",
                             format: v => v switch { PostChain.TonemapShoulder => "shoulder", PostChain.TonemapNeutral => "neutral", _ => "aces" });
        G.Section("Anti-aliasing");
        Taa = G.Toggle("taa", "Temporal anti-aliasing", true, help: "Smooths edges over several frames.");
        Taa.Changed = () => { if (Taa.V) TaaStage.Reset(); };
        G.Section("Bloom");
        Threshold = G.Slider("threshold", "Bloom threshold", 0f, 4f, 0.05f, 1f, tier: Tier.Advanced);
        Knee = G.Slider("knee", "Bloom knee", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced);
        G.Section("Tone");
        AcesMix = G.Slider("acesmix", "ACES mix", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced, help: "Only affects ACES tone mapping.");
        TmKnee = G.Slider("tmknee", "Shoulder knee", 0f, 1f, 0.05f, 0.8f, tier: Tier.Advanced, help: "Only affects shoulder and ACES tone mapping.");
        Dither = G.Slider("dither", "Output dither", 0f, 2f, 0.1f, 1f, tier: Tier.Advanced);
        G.Section("Anti-aliasing");
        // Never 0 (a frozen history would never converge) and never above 0.5 (the raw jitter, "an earthquake").
        TaaBlend = G.Slider("taablend", "TAA history weight", 0.02f, 0.5f, 0.01f, 0.1f, tier: Tier.Advanced);
        Taau = G.Toggle("taau", "Temporal upsampling", true, Tier.Advanced, help: "With render scale below 1.",
                        changed: TaaStage.Reset);
        TaaSharp = G.Slider("taasharp", "TAA sharpness", 0f, 1f, 0.05f, 0.3f, tier: Tier.Advanced,
                            help: "Undoes the softness the history blend leaves. 0 = the raw resolve.");
        G.Section("Screen-space effects");
        HalfRes = G.Toggle("halfres", "Half-resolution", false,
                           help: "Ambient occlusion is computed at a quarter of the pixels and upsampled. Needs temporal anti-aliasing to look clean.",
                           changed: SsaoPass.DropHistory);
        G.Section("Stock effects");
        Stack = G.Toggle("stack", "Game post-effects", true, Tier.Advanced,
                         help: "The stock framebuffer effects (gamma, sharpen, vibrance, SMAA, toon…) on the Alloy frame.");
        FbPost = G.Toggle("fbpost", "Hosted content post", true, Tier.Advanced);
        ToonBands = G.Slider("toonbands", "Toon bands", 1f, 32f, 1f, 1f, tier: Tier.Advanced,
                             help: "With the game's Toon option; 1 = no posterise.");
        ToonInk = G.Slider("toonink", "Toon ink", 0f, 1f, 0.05f, 1f, tier: Tier.Advanced);
        ToonEdge = G.Slider("toonedge", "Toon edge", 1f, 8f, 0.5f, 1f, tier: Tier.Advanced);
        G.Section("");
        Motion = G.Toggle("motion", "motion", false, Tier.ChatOnly);
        Viz = G.Slider("viz", "viz", 0, PostChain.VizMotion, 1, 0, tier: Tier.ChatOnly);
        ClearViz = G.Toggle("clearviz", "clearviz", false, Tier.ChatOnly);
        SkyRaw = G.Toggle("skyraw", "skyraw", true, Tier.ChatOnly);
    }
}
