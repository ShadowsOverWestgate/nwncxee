using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>shadow</c> group (spec §4.3 Shadows), and the storage the shadow path reads:
/// <see cref="SceneInputs"/> (the feed, the focus, the diagnostics), <see cref="CasterPass"/> (the bake and
/// its cadence), <see cref="AlloyLights"/> (the receiver's softness terms, respelled from <c>/light</c>),
/// <see cref="AlloyRenderer"/> (the sun bias, from <c>/draw</c>) and
/// <see cref="ShadowService"/> (the stencil suppression mode). <see cref="Enabled"/> collapses the
/// <c>shadows</c> key and the feed's own flag; the allocation-sizing rows are Restart and read through
/// <c>Boot</c>. Touched from <see cref="RenderCommands.Register"/> so it declares on the main thread.
/// <para><c>dyncount</c> defaults to the cube budget rather than the spec's 16: that is today's effective
/// <c>cube_dyn_count</c> (#305 -- a 16-page pool saturated in a 32-cube street and every overflow slot fell
/// back to the combined bake, the churn the split exists to remove).</para></summary>
internal static class Shadows
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, SunDyn, Casters, Gather, Receiver, Rebake, Split, Layered, RefreshAll, DynSrc,
                                        DynEvents, ItemDirty, FlipCollect, MaskH, Diag, LightDiag, TorchDiag, Trace;
    internal static readonly Knob<float> Dist, Soft, Floor, DiscPx, Near, CsmBias, MaskHoles, MaskGraded, FocusDist, DynMin, CubeDist;
    internal static readonly Knob<int> Cascades, Size, Cubes, CubeSize, DynSize, DynCount, Cap, Fade, FadeSlots,
                                       Mask, MaskDither, StaticCap, DynHold, DynFull, SunFull, SunLatch, DynRanks,
                                       Mode, Stage, LightCull, Taps;
    internal static readonly KnobButton Movers;

    static Shadows()
    {
        G = KnobGroup.Declare("shadow", "Graphics", "Shadows", master: "Enable Alloy shadows",
            masterHelp: "Real-time sun cascades and point-light shadow cubes, replacing the stock stencil shadows. "
                      + "Off, the stock shadows draw. Takes effect at the next launch.",
            masterEffect: Effect.Restart, parent: "alloy.enabled");
        Enabled = G.Master!;

        G.Section("Sun");
        Dist = G.Slider("dist", "Sun shadow distance", 20f, 400f, 10f, 120f, "m",
                        help: "How far from you the sun casts shadows. Cost does not depend on area size.",
                        changed: CasterPass.ShadowInvalidate);
        Cascades = G.Dropdown("cascades", "Sun shadow cascades", new[] { 1, 2, 3, 4 }, 4, effect: Effect.Restart,
                              help: "More cascades = sharper shadows near you at the same distance.");
        Size = G.Dropdown("size", "Sun shadow resolution", new[] { 512, 1024, 2048, 4096 }, 2048, effect: Effect.Restart,
                          help: "Per-cascade map size.");
        G.Section("Point lights");
        Cubes = G.Slider("cubes", "Shadow-casting lights", 0, CasterPass.MaxCubes, 4, 32, effect: Effect.Restart,
                         help: "How many nearby lights cast shadows at once. Lower on a small card.");
        CubeSize = G.Dropdown("cubesize", "Point-light shadow resolution", new[] { 128, 256, 512, 1024, 2048 }, 512, effect: Effect.Restart,
                              help: "Per-face size of each light's shadow cube.");
        CubeDist = G.Slider("cubedist", "Point-light shadow distance", 0f, 200f, 5f, 40f, "m",
                            help: "A light farther than this from you casts no shadow, and no fragment farther than this from the camera "
                                + "reads one; shadows fade out over the last quarter. 0 = every light in the budget, at any distance.");
        Soft = G.Slider("soft", "Shadow softness", 0f, 16f, 0.25f, 4f,
                        help: "Widest blur of point-light shadows; the blur narrows toward the caster's contact. 0 is hard-edged and cheapest.");
        Floor = G.Slider("floor", "Shadow floor", 0f, 1f, 0.05f, 0.35f,
                         help: "How much of a point light still reaches its own shadow, tinted by the light. 0 = black cutouts.");
        DynSize = G.Dropdown("dynsize", "Moving-caster resolution", new[] { 64, 128, 256, 512 }, 256, tier: Tier.Advanced, effect: Effect.Restart);
        DynCount = G.Slider("dyncount", "Moving-caster pool", 0, CasterPass.MaxCubes, 1, 32, tier: Tier.Advanced, effect: Effect.Restart,
                            help: "0 = no separate pool.");
        DynMin = G.Slider("dynmin", "Smallest moving caster", 0f, 3f, 0.1f, 0.5f, "m", Tier.Advanced,
                          help: "A moving part smaller than this (a candle flame, a swinging lantern) claims no moving-caster page; creatures always do.");
        G.Section("Sun");
        SunDyn = G.Toggle("sundyn", "Separate moving-caster sun layer", true, Tier.Advanced, Effect.Restart);
        G.Section("Point lights");
        DiscPx = G.Slider("discpx", "Softness cutoff", 0f, 16f, 0.5f, 0f, "px", Tier.Advanced,
                          help: "Below this screen radius the blur stands down. 0 = never (consistent softness at every distance).");
        Taps = G.Slider("taps", "Shadow blur taps", 0, 16, 1, 8, tier: Tier.Advanced,
                        help: "Disc taps per shadowing light. 0 = no blur.");
        Cap = G.Slider("cap", "Shadow taps per pixel", 0, 2, 1, 2, tier: Tier.Advanced, help: "0 = every light, each at a single hardware tap.");
        Near = G.Slider("near", "Self-shadow skip", 0f, 4f, 0.05f, 1f, tier: Tier.Advanced,
                        help: "Carried lights don't shadow their bearer inside this radius.");
        G.Section("Sun");
        CsmBias = G.Slider("csmbias", "Sun depth bias", 0f, 0.5f, 0.01f, 0.03f, tier: Tier.Advanced);
        Fade = G.Slider("fade", "Shadow fade-in", 0, 120, 1, 60, "frames", Tier.Advanced, help: "0 = pop.");
        FadeSlots = G.Slider("fadeslots", "Fade reserve", 0, 16, 1, 4, tier: Tier.Advanced);
        G.Section("Quality");
        Mask = G.Dropdown("mask", "Cut-out shadows", new[] { -1, 0, 1, 2 }, 2, new[] { "skip", "solid", "alpha-tested", "auto" },
                          tier: Tier.Advanced, help: "Auto = alpha-tested where the texture is mostly holes, solid elsewhere.",
                          changed: CasterPass.ListChanged);
        MaskHoles = G.Slider("maskholes", "Auto threshold", 0f, 1f, 0.05f, 0.65f, tier: Tier.Advanced, changed: CasterPass.ListChanged);
        MaskGraded = G.Slider("maskgraded", "Glass threshold", 0f, 1f, 0.05f, 0.15f, tier: Tier.Advanced, changed: CasterPass.ListChanged);
        MaskDither = G.Slider("maskdither", "Translucent shadow dither", 0, CasterPass.MaskDitherMax, 1, 1, tier: Tier.Advanced,
                              changed: () => { CasterPass.ListChanged(); CasterPass.ShadowInvalidate(); });
        G.Section("Sun");
        FocusDist = G.Slider("focusdist", "Cascade look-ahead", 0f, 100f, 1f, 24f, "m", Tier.Advanced);
        G.Section("Performance");
        StaticCap = G.Slider("staticcap", "Static re-bakes per frame", 0, 16, 1, 3, tier: Tier.Advanced, help: "0 = unbounded (hitches).");
        DynHold = G.Slider("dynhold", "Mover hold", 0, 120, 1, 30, "frames", Tier.Advanced);
        DynFull = G.Slider("dynfull", "Full-rate cube ranks", 0, CasterPass.MaxCubes, 1, CasterPass.MaxCubes, tier: Tier.Advanced, help: "Cube ranks past this re-bake movers every other frame.");
        SunFull = G.Slider("sunfull", "Full-rate sun cascades", 0, CasterPass.MaxCascades, 1, CasterPass.MaxCascades, tier: Tier.Advanced, help: "Cascades past this re-bake movers every other frame.");
        SunLatch = G.Slider("sunlatch", "Cascade re-latches per frame", 0, CasterPass.MaxCascades, 1, 0, tier: Tier.Advanced, help: "0 = every drifted cascade re-fits the same frame.");
        DynRanks = G.Slider("dynranks", "Mover pages", 0, CasterPass.MaxCubes, 1, CasterPass.MaxCubes, tier: Tier.Advanced,
                            changed: CasterPass.ShadowInvalidate);

        G.Section("");
        Mode = G.Slider("mode", "mode", 0, ShadowService.ModeAuto, 1, ShadowService.ModeAuto, tier: Tier.ChatOnly, effect: Effect.Restart);
        Casters = G.Toggle("casters", "casters", true, Tier.ChatOnly, Effect.Restart);
        Stage = G.Slider("stage", "stage", 1, 4, 1, 4, tier: Tier.ChatOnly, effect: Effect.Restart);
        LightCull = G.Slider("lightcull", "lightcull", 0, 2, 1, 1, tier: Tier.ChatOnly);
        Gather = G.Toggle("gather", "gather", true, Tier.ChatOnly);
        Receiver = G.Toggle("receiver", "receiver", true, Tier.ChatOnly);
        Rebake = G.Toggle("rebake", "rebake", true, Tier.ChatOnly);
        Split = G.Toggle("split", "split", true, Tier.ChatOnly, changed: CasterPass.ShadowInvalidate);
        Layered = G.Toggle("layered", "layered", true, Tier.ChatOnly);
        RefreshAll = G.Toggle("refreshall", "refreshall", false, Tier.ChatOnly);
        DynSrc = G.Toggle("dynsrc", "dynsrc", true, Tier.ChatOnly);
        DynEvents = G.Toggle("dynevents", "dynevents", true, Tier.ChatOnly, changed: CasterPass.ShadowInvalidate);
        ItemDirty = G.Toggle("itemdirty", "itemdirty", true, Tier.ChatOnly, changed: CasterPass.ShadowInvalidate);
        FlipCollect = G.Toggle("flipcollect", "flipcollect", false, Tier.ChatOnly);
        MaskH = G.Toggle("maskh", "maskh", true, Tier.ChatOnly, changed: CasterPass.ListChanged);
        Diag = G.Toggle("diag", "diag", false, Tier.ChatOnly);
        LightDiag = G.Toggle("lightdiag", "lightdiag", false, Tier.ChatOnly);
        TorchDiag = G.Toggle("torchdiag", "torchdiag", false, Tier.ChatOnly);
        Trace = G.Toggle("trace", "trace", false, Tier.ChatOnly);
        Movers = G.Button("movers", "movers", CasterPass.DumpMoversNext, Tier.ChatOnly);
    }
}
