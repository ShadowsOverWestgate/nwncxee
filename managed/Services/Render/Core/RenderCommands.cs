using System;
using System.Globalization;
using Sowx.Framework;

namespace Sowx.Services.Render;

/// <summary>
/// The renderer's own slash commands (#165): the live-tuning dials and the shader dump, registered into
/// <see cref="CommandRegistry"/> instead of living as branches in the chat service. Registration happens
/// ahead of every renderer stand-down, so <c>/vanilla</c> -- the escape hatch -- exists even in a session
/// that owns nothing.
/// </summary>
internal static class RenderCommands
{
    private static ExtenderLog? _log;

    private const string LightKeys = "gain|radius|falloff|ambient|sun|overbright|wrap|wrapndl|shadowsoft|shadownear|hot|hotsize";
    private const string GrassKeys = "density|wind|windspeed|soften|aniso|edge|push|pushradius|pushextent|cull|cullshrink|cullmax";
    private const string PostKeys = "hdr|bloom|threshold|knee|tonemap|tmknee|softclip";

    /// <summary>Called once from <see cref="OwnedDrawService"/>'s Initialize, before any early return.</summary>
    internal static void Register(ExtenderLog log)
    {
        _log = log;
        var r = CommandRegistry.Global;
        r.Register("/vanilla", "toggle extender rendering off/on at the next frame edge", _ =>
        {
            // The flip is applied at the next frame edge (#86); the render thread logs the transition.
            RenderMode.Toggle();
            return null;
        });
        r.Register("/light", "report or tune the owned lighting knobs; /light reset restores boot values",
                   a => Tune("light", LightKeys, 2, a));
        r.Register("/grass", "report or tune the owned grass field",
                   a => Tune("grass", GrassKeys, 3, a));
        r.Register("/post", "report or tune the post chain; /post reset restores boot values",
                   a => Tune("post", PostKeys, 3, a));
        r.Register("/dumpshaders", "write every overridable shader stage to the dump directory", DumpShaders);
    }

    /// <summary>One tuning command, for every tunable pass (#175). <c>/x</c> reports, <c>/x &lt;key&gt;
    /// &lt;value&gt;</c> sets, <c>/x reset</c> restores the boot values where the pass has a reset.
    /// <para>These knobs are taste judgements made while LOOKING at the scene they change, which is the whole
    /// reason they are commands and not env vars (ADR 0013): the env var only sets the boot value and cannot
    /// move without a relaunch. Each set is one float; the render thread picks it up on its next upload.</para>
    /// <para>One implementation rather than three near-identical ones: what actually differed between
    /// <c>/light</c>, <c>/grass</c> and <c>/post</c> was the key list and a decimal place.</para></summary>
    private static string? Tune(string name, string keys, int decimals, string args)
    {
        PassLifecycle? p = PassRegistry.Global.Find(name);
        if (p?.Describe == null || p.Tune == null) return $"[{name}] not available in this session";

        string[] a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string reset = p.Reset == null ? "" : $" | /{name} reset";
        if (a.Length == 0) return $"[{name}] {p.Describe()}";
        if (a.Length == 1 && a[0] == "reset" && p.Reset != null)
        {
            p.Reset();
            return $"[{name}] reset -> {p.Describe()}";
        }
        if (a.Length != 2 || !float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            || v < 0f)
            return $"[{name}] usage: /{name} | /{name} <{keys}> <value>{reset}";

        float old = p.Tune(a[0], v);
        return float.IsNaN(old)
            ? $"[{name}] unknown knob \"{a[0]}\" -- try {keys.Replace('|', ',').Replace(",", ", ")}"
            : $"[{name}] {a[0]} {v.ToString("F" + decimals, CultureInfo.InvariantCulture)} " +
              $"(was {old.ToString("F" + decimals, CultureInfo.InvariantCulture)}) -> {p.Describe()}";
    }

    /// <summary>#160: write every overridable stage to <c>&lt;user directory&gt;/sowx_shader_dump/</c> as an
    /// editable .shd, plus the bindless variant of the lit stage. Dumping what is compiled in is what makes
    /// the starting point current by construction (ADR 0012); the sizing tokens stay unexpanded, because the
    /// extender expands them on load for an override exactly as for the embedded default.</summary>
    private static string? DumpShaders(string args)
    {
        string dir = ShaderStages.DumpDir;
        int n = ShaderStages.Dump(dir, _log);
        return n < 0
            ? $"[shaders] dump to {dir} failed -- see the log"
            : $"[shaders] {n} stages written to {dir}";
    }
}
