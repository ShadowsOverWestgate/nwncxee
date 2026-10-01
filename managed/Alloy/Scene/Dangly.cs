using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>dangly</c> group (spec §4.3): the storage <see cref="DanglyPhysics"/> reads once per
/// dangly part per frame. <see cref="Cage"/> is the load-bearing one -- it widens vanilla's per-axis clamp,
/// the only thing keeping cloaks out of the wearer's legs, so it trades sway for clipping. Touched from
/// <see cref="RenderCommands.Register"/> so the group declares at Initialize on the main thread.</summary>
internal static class Dangly
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<float> Gravity, Wind, Breeze, Turb, TurbFreq, TurbSpeed, Vary, Body, Cage, Hz, Still;
    internal static readonly Knob<int> Steps;
    /// <summary>AN2: solve the queued dangly parts on the StagePool workers. Off (and with
    /// <c>/alloy threads 0</c>) the solve is serial on the main thread, which is the A/B for anything that
    /// looks wrong in a cloak -- the two paths are asserted bit-for-bit identical off-device.</summary>
    internal static readonly Knob<bool> Parallel;

    static Dangly()
    {
        G = KnobGroup.Declare("dangly", "Graphics", "Dangly", master: "Enable Alloy dangly meshes",
            masterHelp: "Cloaks, hair, banners and other swaying parts move with gravity and wind instead of the stock spring. Off, the stock sway runs.",
            parent: "alloy.enabled");
        Enabled = G.Master!;
        G.Status = DanglyPhysics.Describe;

        G.Section("Forces");
        Gravity = G.Slider("gravity", "Gravity", 0f, 20f, 0.5f, 3f,
                           help: "How much cloth sags. Low by default: rest poses already hang.");
        Wind = G.Slider("wind", "Wind response", 0f, 40f, 1f, 14f, help: "How hard the area's wind pushes the cloth.");
        Breeze = G.Slider("breeze", "Base breeze", 0f, 2f, 0.05f, 0.35f,
                          help: "Wind when the area sets none. 0 = only what the game reports.");
        Turb = G.Slider("turb", "Gustiness", 0f, 1f, 0.05f, 0.7f, tier: Tier.Advanced);
        TurbFreq = G.Slider("turbfreq", "Gust size", 0f, 4f, 0.1f, 0.9f, tier: Tier.Advanced);
        TurbSpeed = G.Slider("turbspeed", "Gust speed", 0f, 8f, 0.1f, 2.6f, tier: Tier.Advanced);
        Vary = G.Slider("vary", "Per-object variation", 0f, 1f, 0.05f, 0.15f, tier: Tier.Advanced);
        G.Section("Collision");
        Body = G.Slider("body", "Body collision", 0f, 1f, 0.05f, 0.95f, tier: Tier.Advanced, help: "0 = off.");
        Cage = G.Slider("cage", "Freedom of movement", 0.5f, 8f, 0.1f, 3.5f, tier: Tier.Advanced,
                        help: "×stock cage; past ~4 cloaks reach the legs.");
        G.Section("");
        Hz = G.Slider("hz", "hz", 30f, 480f, 10f, 120f, tier: Tier.ChatOnly);
        Steps = G.Slider("steps", "steps", 1, 16, 1, 4, tier: Tier.ChatOnly);
        Still = G.Slider("still", "still", 0f, 1f, 0.001f, 0.01f, tier: Tier.ChatOnly);
        Parallel = G.Toggle("parallel", "parallel", true, tier: Tier.ChatOnly,
            help: "Solve the frame's swaying parts on CX's worker threads instead of one after another on the "
                + "main thread. The answer is the same either way; off is the A/B.");
    }
}
