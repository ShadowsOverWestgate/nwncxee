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
        r.Register("/light", "report or tune the owned lighting knobs; /light reset restores boot values", Light);
        r.Register("/grass", "report or tune the owned grass field", Grass);
        r.Register("/post", "report or tune the post chain; /post reset restores boot values", Post);
        r.Register("/dumpshaders", "write every overridable shader stage to the dump directory", DumpShaders);
    }

    /// <summary>Live light tuning: <c>/light</c> reports, <c>/light &lt;key&gt; &lt;value&gt;</c> sets,
    /// <c>/light reset</c> restores the values the session launched with. Keys: gain, radius, falloff,
    /// ambient, sun, overbright.
    /// <para>Lighting is a taste judgement made while LOOKING at a scene, so the knobs have to move without a
    /// restart -- the env vars only set the boot values. Each set is one float; the render thread picks it up
    /// on the next upload.</para></summary>
    private static string? Light(string args)
    {
        string[] a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0) return $"[light] {OwnedLights.Describe()}";
        if (a.Length == 1 && a[0] == "reset")
        {
            OwnedLights.ResetTuning();
            return $"[light] reset -> {OwnedLights.Describe()}";
        }
        if (a.Length != 2 || !float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            || v < 0f)
            return "[light] usage: /light | /light <gain|radius|falloff|ambient|sun|overbright|wrap|wrapndl|shadowsoft|shadownear|hot|hotsize> <value> | /light reset";

        float old = OwnedLights.Tune(a[0], v);
        return float.IsNaN(old)
            ? $"[light] unknown knob \"{a[0]}\" -- try gain, radius, falloff, ambient, sun, overbright, wrap, wrapndl, shadowsoft, shadownear, hot or hotsize"
            : $"[light] {a[0]} {v:F2} (was {old:F2}) -> {OwnedLights.Describe()}";
    }

    /// <summary>Live grass tuning (#127): <c>/grass</c> reports, <c>/grass &lt;key&gt; &lt;value&gt;</c> sets.
    /// Keys: density, wind, windspeed. Density is the acceptance criterion, not a convenience -- vanilla bakes
    /// it into the VDOs at area load and gives the player no knob at all, so the owned field re-scatters on
    /// the next frame instead.</summary>
    private static string? Grass(string args)
    {
        string[] a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0) return $"[grass] {GrassField.Describe()}";
        if (a.Length != 2 || !float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            || v < 0f)
            return "[grass] usage: /grass | /grass <density|wind|windspeed|soften|aniso|edge|push|pushradius|pushextent|cull|cullshrink|cullmax> <value>";

        float old = GrassField.Tune(a[0], v);
        return float.IsNaN(old)
            ? $"[grass] unknown knob \"{a[0]}\" -- try density, wind, windspeed, soften, aniso, edge, push, pushradius, pushextent, cull, cullshrink or cullmax"
            : $"[grass] {a[0]} {v:F3} (was {old:F3}) -> {GrassField.Describe()}";
    }

    /// <summary>Live post-chain tuning (#139): <c>/post</c> reports, <c>/post &lt;key&gt; &lt;value&gt;</c>
    /// sets, <c>/post reset</c> restores what the session launched with. Keys: hdr (0/1), bloom, threshold,
    /// knee, tonemap (0 off, 1 shoulder, 2 aces), tmknee, softclip.
    /// <para>Dials rather than constants because every one of them is a taste call, and the same reason
    /// <c>/light</c> exists: they were dialled in live, standing in the scene they change.</para></summary>
    private static string? Post(string args)
    {
        string[] a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0) return $"[post] {PostChain.Describe()}";
        if (a.Length == 1 && a[0] == "reset")
        {
            PostChain.ResetTuning();
            return $"[post] reset -> {PostChain.Describe()}";
        }
        if (a.Length != 2 || !float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            || v < 0f)
            return "[post] usage: /post | /post <hdr|bloom|threshold|knee|tonemap|tmknee|softclip> <value> | /post reset";

        float old = PostChain.Tune(a[0], v);
        return float.IsNaN(old)
            ? $"[post] unknown knob \"{a[0]}\" -- try hdr, bloom, threshold, knee, tonemap, tmknee or softclip"
            : $"[post] {a[0]} {v:F3} (was {old:F3}) -> {PostChain.Describe()}";
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
