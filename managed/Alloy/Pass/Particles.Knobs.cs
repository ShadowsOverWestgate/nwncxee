using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>particles</c> group, declared on the pass itself because the pass already owns the name.
/// <see cref="Batch"/> and <see cref="Sampler"/> are the Restart pair, read through <c>Boot</c>; sampler on
/// samples the diffuse through an owned sampler object, because the engine never binds a particle texture raw
/// -- it binds through the texture's vtable slot +0x108, which carries that texture's sampler state.</summary>
internal static unsafe partial class Particles
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Soft, Lit, Shadows, Sort, Grid, Guard, Batch, Sampler, Diag;
    internal static readonly Knob<int> Viz;

    static Particles()
    {
        G = KnobGroup.Declare("particles", "Graphics", "Motion", master: "Particles",
            masterHelp: "Draw spell effects, smoke and fire. Off, no particles are drawn at all -- nothing else "
                      + "draws them any more -- so this is a cfg-only switch.",
            masterTier: Tier.ChatOnly, order: 354, title: "Particles");
        Enabled = G.Master!;
        G.Status = Describe;

        Soft = G.Toggle("soft", "Soft edges", true,
                        help: "Particles fade where they meet geometry instead of cutting through it.")
                .Ladder(Quality.Effects, false, true, true, true);
        Lit = G.Toggle("lit", "Lit particles", true, help: "Smoke and dust take the scene's light.")
               .Ladder(Quality.Effects, false, true, true, true);
        Shadows = G.Toggle("shadows", "Shadowed particles", true, Tier.ChatOnly,
                           help: "Lit particles pay the point-light shadow taps.")
                   .Ladder(Quality.Effects, false, false, true, true);
        Sort = G.Toggle("sort", "GPU sort", true, Tier.ChatOnly);
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
