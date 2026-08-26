using System;
using System.Globalization;
using Sowx.Framework;

namespace Sowx.Alloy;

/// <summary>
/// The renderer's own slash commands (#165): the live-tuning dials and the shader dump, registered into
/// <see cref="CommandRegistry"/> instead of living as branches in the chat service. Registration happens
/// ahead of every renderer stand-down, so <c>/vanilla</c> -- the escape hatch -- exists even in a session
/// that owns nothing.
/// </summary>
internal static class RenderCommands
{
    private static ExtenderLog? _log;

    private const string LightKeys = "gain|radius|falloff|ambient|sun|overbright|wrap|wrapndl|shadowsoft|shadownear|hot|hotsize|shadeband";
    private const string GrassKeys = "on|density|wind|windspeed|soften|aniso|edge|push|pushradius|pushextent|cull|cullshrink|cullmax";
    private const string ParticleKeys = "on|diag|soft|lit|viz|grid|guard";
    // hdr is NOT here: it stands the whole Alloy frame down, which is a boot decision (config hdr=0).
    private const string PostKeys = "bloom|threshold|knee|tonemap|tmknee|softclip";
    private const string DrawKeys = "light|prepass|lodpx|viz|aotest|csmbias";
    private const string WaterKeys = "on|tint|wave|ssr|foam|wind|speed|diag";
    private const string ProbeKeys = "on|gain|areawide";

    /// <summary>Called once from <see cref="AlloyRenderer"/>'s Initialize, before any early return.</summary>
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
        r.Register("/light", "report or tune the Alloy lighting knobs; /light reset restores boot values",
                   a => Tune("light", LightKeys, 2, a));
        r.Register("/grass", "report or tune the Alloy grass field; /grass reset restores boot values",
                   a => Tune("grass", GrassKeys, 3, a));
        r.Register("/particles", "report or tune the Alloy particle pass; /particles on 1 arms it",
                   a => Tune("particles", ParticleKeys, 0, a));
        r.Register("/post", "report or tune the post chain; /post reset restores boot values",
                   a => Tune("post", PostKeys, 3, a));
        r.Register("/draw", "report or tune the Alloy world pass (#179); /draw reset restores boot values",
                   a => Tune("draw", DrawKeys, 2, a));
        r.Register("/water", "report or tune the Alloy water; /water reset restores boot values",
                   a => Tune("water", WaterKeys, 2, a));
        r.Register("/probe", "report or tune the environment probe; /probe reset restores boot values",
                   a => Tune("probe", ProbeKeys, 2, a));
        r.Register("/dumpshaders", "write every overridable shader stage to the dump directory", DumpShaders);
        // #192: a one-shot ACTION, so a command and not a config setting (ADR 0013). The next colour pass
        // serves it; the reply is what the previous one wrote, because the render thread is the only place
        // the answer exists.
        r.Register("/framedump", "dump what the Alloy colour pass submits, for diffing two runs", FrameDump_);
        // #194: a measurement toggle, off by default -- the timer costs more than what it measures.
        r.Register("/batchtime", "toggle the PartDraw entry-read timer on the [batch] line", _ => BatchService.BatchTime());
        // #182: a per-pass on/off is one flag and one line -- it needs neither the float dial nor a
        // Describe/Tune slot, and both passes stay armed so the flip lands on the next frame.
        r.Register("/sky", "toggle the Alloy sky pass", _ => SkyBox.Toggle());
        r.Register("/beams", "toggle the Alloy light-beam pass", _ => Beams.Toggle());
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
        if (a.Length != 2 || !TuneValue(a[0], a[1], out float v) || v < 0f)
            return $"[{name}] usage: /{name} | /{name} <{keys}> <value>{reset}";

        float old = p.Tune(a[0], v);
        return float.IsNaN(old)
            ? $"[{name}] unknown knob \"{a[0]}\" -- try {keys.Replace('|', ',').Replace(",", ", ")}"
            : $"[{name}] {a[0]} {v.ToString("F" + decimals, CultureInfo.InvariantCulture)} " +
              $"(was {old.ToString("F" + decimals, CultureInfo.InvariantCulture)}) -> {p.Describe()}";
    }

    /// <summary>The value token, as the float every <see cref="PassLifecycle.Tune"/> takes. One key is not a
    /// magnitude -- the tonemap is a named operator -- so its three names are accepted alongside the indices
    /// they stand for, and nothing else inherits the words.</summary>
    private static bool TuneValue(string key, string token, out float v)
    {
        if (key == "tonemap")
            switch (token)
            {
                case "off": v = 0f; return true;
                case "shoulder": v = 1f; return true;
                case "aces": v = 2f; return true;
            }
        return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>#192: arm the frame-diff dump. The command cannot report the file it produces -- the dump
    /// happens on the render thread some frames later -- so it reports the PREVIOUS one, which is what turns
    /// "run it twice and diff" into a loop a human can drive without watching the log.</summary>
    private static string? FrameDump_(string args)
    {
        FrameDump.Armed = true;
        string prev = FrameDump.LastDraws == 0
            ? ""
            : $" (last: {FrameDump.LastDraws} draws, {FrameDump.LastDistinct} distinct keys, " +
              $"{FrameDump.LastGroups} groups minted)";
        return $"[framedump] armed -- writing to {FrameDump.Dir} on the next Alloy pass{prev}";
    }

    /// <summary>#160: write every overridable stage to <c>&lt;user directory&gt;/cx_shader_dump/</c> as an
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
