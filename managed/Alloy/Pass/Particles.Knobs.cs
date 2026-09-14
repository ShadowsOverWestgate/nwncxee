using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>particles</c> group (spec §4.3), declared on the pass itself because the pass already
/// owns the name: the storage the other half of this class reads. <see cref="Batch"/> and
/// <see cref="Sampler"/> are the Restart pair (the batcher shapes every group the pass builds; the sampler is
/// a GL object it owns), read through <c>Boot</c>. Touched from <see cref="RenderCommands.Register"/> so the
/// group declares at Initialize on the main thread.
/// <para>What each one is for: <c>enabled</c> is the per-pass toggle, on by default -- #183's bar is that the default
/// frame is unchanged, and the pass was armed on a plain launch; the stray-rays bug is still open, and
/// off is the way to compare against vanilla in the same session. <c>diag</c>: a per-pass census of what
/// the pass built, by render type, blend class and model flag -- live-verifying this class means knowing
/// which of the seven billboard bases a scene exercised. <c>batch</c> (Restart) off draws all three
/// classes in the engine's own capture order, merging only ADJACENT runs: most of the batching goes, and
/// the only remaining departure from vanilla's order is punch-through-first, which is provably free; the
/// A/B for the additive-over-blended difference. <c>soft</c> off: no depth snapshot and no fade, vanilla's
/// hard intersections -- the copy is the only per-frame GL cost here that is not a draw. <c>lit</c> off: no
/// clustered lighting, the vanilla per-emitter CPU tint only (ponytail: the lighting runs PER FRAGMENT on
/// the grass template; a screen of smoke pays a cluster walk per layer; moving it to the vertex is a
/// shader change because the light libs sample shadow maps). <c>viz</c> 1 tints every quad by render type
/// (<see cref="TypeNames"/>), 2 drops the texture and draws flat -- the only way to see the quads as quads.
/// <c>sampler</c> (Restart) on samples the diffuse through an owned sampler object instead of whatever
/// state the texture OBJECT carries: the engine never binds a particle texture raw
/// (<c>Material::BindAllStandardTextures</c> @0x4c6e30 binds through the texture's own vtable slot +0x108,
/// which carries its sampler state), so binding the GL name straight from <c>AurTextureGetTID</c> samples
/// through whatever the last engine pass left, and a sampler object overrides it for this draw alone.
/// <c>grid</c> off ignores the emitter's UV atlas and samples the whole texture per quad; <c>guard</c> off
/// samples the atlas exactly as the engine's arithmetic says, with no containment -- <b>both are the live
/// bisect for the OPEN stray-rays bug, keep until it closes</b>: rays that persist with the grid off are in
/// the texture, rays that vanish are in the UV maths. <c>sort</c> off builds the draw-order index on the
/// CPU, one entry a particle, unordered -- exactly the pre-#288 pass, and the escape hatch a driver whose
/// loader never resolved <c>glDispatchCompute</c> falls back to without the dial being touched.</para></summary>
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
