using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>particles</c> group, declared on the pass itself because the pass already owns the name.
/// <see cref="Batch"/> and <see cref="Sampler"/> are the Restart pair (the batcher shapes every group the pass
/// builds; the sampler is a GL object it owns), read through <c>Boot</c>. Touched from
/// <see cref="RenderCommands.Register"/> so the group declares at Initialize on the main thread.
/// <para>The chat-only dials: <c>diag</c> is a per-pass census by render type, blend class and model flag.
/// <c>batch</c> off draws all three classes in the engine's capture order, merging only ADJACENT runs -- the
/// A/B for the additive-over-blended difference. <c>soft</c> off: no depth snapshot, vanilla's hard
/// intersections. <c>lit</c> off: the vanilla per-emitter CPU tint only (ponytail: lighting runs PER FRAGMENT
/// on the grass template; a screen of smoke pays a cluster walk per layer, and moving it to the vertex is a
/// shader change because the light libs sample shadow maps). <c>viz</c> 1 tints every quad by render type
/// (<see cref="TypeNames"/>), 2 draws flat. <c>sampler</c> on samples the diffuse through an owned sampler
/// object: the engine never binds a particle texture raw (<c>Material::BindAllStandardTextures</c> @0x4c6e30
/// binds through the texture's vtable slot +0x108, which carries its sampler state), so the GL name straight
/// from <c>AurTextureGetTID</c> samples through whatever the last engine pass left. <c>grid</c> off ignores the
/// emitter's UV atlas; <c>guard</c> off samples the atlas with no containment -- <b>both are the live bisect
/// for the OPEN stray-rays bug</b>: rays that persist with the grid off are in the texture, rays that vanish
/// are in the UV maths. <c>sort</c> off builds the draw-order index on the CPU, unordered -- also the fallback
/// a driver whose loader never resolved <c>glDispatchCompute</c> takes on its own (#288).</para></summary>
internal static unsafe partial class Particles
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Soft, Lit, Shadows, Sort, Grid, Guard, Batch, Sampler, Diag;
    internal static readonly Knob<int> Viz;

    static Particles()
    {
        G = KnobGroup.Declare("particles", "Graphics", "Particles", master: "Enable Alloy particles",
            masterHelp: "Spell effects, smoke and fire drawn through CX: soft edges and lighting. Off, the stock particles draw.",
            parent: "alloy.enabled");
        Enabled = G.Master!;
        G.Status = Describe;

        Soft = G.Toggle("soft", "Soft edges", true,
                        help: "Particles fade where they meet geometry instead of cutting through it.");
        Lit = G.Toggle("lit", "Lit particles", true, help: "Smoke and dust take the scene's light.");
        Shadows = G.Toggle("shadows", "Shadowed particles", true, Tier.Advanced,
                           help: "Lit particles pay the point-light shadow taps.");
        Sort = G.Toggle("sort", "GPU sort", true, Tier.Advanced);
        Grid = G.Toggle("grid", "grid", true, Tier.ChatOnly);
        Guard = G.Toggle("guard", "guard", true, Tier.ChatOnly);
        Batch = G.Toggle("batch", "batch", true, Tier.ChatOnly, Effect.Restart);
        Sampler = G.Toggle("sampler", "sampler", false, Tier.ChatOnly, Effect.Restart);
        Diag = G.Toggle("diag", "diag", false, Tier.ChatOnly);
        Viz = G.Slider("viz", "viz", 0, 2, 1, 0, tier: Tier.ChatOnly, changed: VizChanged);
    }

    /// <summary><c>viz 1</c> tints by render type; the colour order is only useful if it is said somewhere.</summary>
    private static void VizChanged()
    {
        if (Viz.V == 1)
            _log?.Info("Particles: viz 1 tints by render type -- " + string.Join(", ", TypeNames)
                       + ", in that colour order: red, green, blue, yellow, magenta, cyan, white");
    }
}
