using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>A torch keeps a steady reach and flickers in brightness instead of the engine's linear radius pulse.
/// Which lights are animated comes off the model: a light node whose animation keys its radius or multiplier as a
/// PULSE -- three or more keys, both rising and falling, never reaching zero. A day/night ramp is monotonic and a
/// blink touches zero, so neither is a pulse, and a window light still goes dark by day.</summary>
internal static partial class DbInputs
{
    /// <summary>One channel's authored pulse: the lowest and highest key across every pulse track the node has.</summary>
    internal struct PulseRange { public float Lo, Hi; public bool Any => Hi > 0f; }

    /// <summary>Per model, per node: weak on the model, so an area's models are not kept alive by this cache.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ModelDef, Dictionary<int, (PulseRange R, PulseRange M)>> _pulse = new();
    private static readonly object _pulseLock = new();
    private static readonly long _flkT0 = Stopwatch.GetTimestamp();
    /// <summary>How far outside the authored range an animated value may sit and still be read as the pulse
    /// (a blend between two runs lands a hair past a key).</summary>
    private const float PulseSlack = 0.02f;
    /// <summary>Supermodel hops searched for the node's animations.</summary>
    private const int MaxSuper = 8;

    /// <summary>Steady the reach and flicker the colour of light <paramref name="l"/> when an animation is
    /// driving it through an authored pulse. Untouched otherwise, and when <c>light.flicker</c> is 0.</summary>
    private static void Flicker(ref DbLight l, int part)
    {
        float amp = Lighting.Flicker.V;
        if (amp <= 0f || part < 0) return;
        byte anim = SceneDb.PartLitAnim[part];
        if ((anim & (DbAnim.LitRadius | DbAnim.LitMul)) == 0) return;
        ModelDef? m = SceneDb.PartModel[part];
        if (m == null) return;
        (PulseRange r, PulseRange mu) = PulseOf(m, SceneDb.PartNode[part]);
        float keep = 1f;
        if ((anim & DbAnim.LitRadius) != 0 && In(r, l.Radius)) { keep *= r.Lo / r.Hi; l.Radius = r.Hi; }
        if ((anim & DbAnim.LitMul) != 0 && In(mu, l.Multiplier)) { keep *= mu.Lo / mu.Hi; l.Multiplier = mu.Hi; }
        if (keep >= 1f) return;
        float t = (float)((Stopwatch.GetTimestamp() - _flkT0) / (double)Stopwatch.Frequency);
        float phase = (uint)(long)l.Id * 2654435761u * (MathF.Tau / 4294967296f);
        l.Colour *= Wave(t * Lighting.FlickerSpeed.V, phase, MathF.Min(1f, (1f - keep) * amp));
    }

    private static bool In(in PulseRange p, float v) => p.Any && v >= p.Lo * (1f - PulseSlack) && v <= p.Hi * (1f + PulseSlack);

    /// <summary>A fire's unevenness: slow breathing under faster crackle, three incommensurate sines whose sum
    /// stays in [-1,1], mapped to a multiplier in [1-depth, 1]. The phase desyncs neighbouring torches.</summary>
    internal static float Wave(float t, float phase, float depth)
    {
        float n = 0.5f * MathF.Sin(t * 7.3f + phase) + 0.3f * MathF.Sin(t * 13.7f + phase * 2.1f) + 0.2f * MathF.Sin(t * 23.1f + phase * 3.3f);
        return 1f - depth * 0.5f * (1f - n);
    }

    /// <summary>The radius and multiplier pulses of node <paramref name="node"/> of <paramref name="m"/>, once per
    /// (model, node). ponytail: one lock -- a few hundred lights a rebuild; a concurrent table if it ever shows.</summary>
    private static (PulseRange R, PulseRange M) PulseOf(ModelDef m, int node)
    {
        lock (_pulseLock)
        {
            Dictionary<int, (PulseRange R, PulseRange M)> byNode = _pulse.GetOrCreateValue(m);
            if (byNode.TryGetValue(node, out var hit)) return hit;
            string name = (uint)node < (uint)m.Nodes.Length ? m.Nodes[node].Name : "";
            var v = (Scan(m, name, CtlRadius), Scan(m, name, CtlMultiplier));
            byNode[node] = v;
            return v;
        }
    }

    /// <summary>Every pulse track for controller <paramref name="ctl"/> on the node named <paramref name="name"/>,
    /// across the model's animations and its supermodels', folded into one range.</summary>
    internal static PulseRange Scan(ModelDef m, string name, int ctl)
    {
        var p = new PulseRange { Lo = float.MaxValue, Hi = 0f };
        int hops = 0;
        for (ModelDef? s = m; s != null && hops <= MaxSuper; s = s.Super, hops++)
            foreach (AnimDef a in s.Anims)
                foreach (AnimNode an in a.Nodes)
                {
                    if (!string.Equals(an.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (ControllerTrack c in an.Controllers)
                        if (c.Type == ctl && IsPulse(c, out float lo, out float hi))
                        { p.Lo = MathF.Min(p.Lo, lo); p.Hi = MathF.Max(p.Hi, hi); }
                }
        return p.Hi > 0f ? p : default;
    }

    /// <summary>Three or more keys, both a rise and a fall, and never zero: a flame, not a switch or a ramp.</summary>
    internal static bool IsPulse(ControllerTrack c, out float lo, out float hi)
    {
        lo = float.MaxValue; hi = 0f;
        int n = c.Times.Length, stride = Math.Max(1, c.Columns) * (c.Bezier ? 3 : 1);
        if (n < 3 || c.Values.Length < n * stride) return false;
        bool up = false, down = false;
        float prev = c.Values[0];
        for (int k = 0; k < n; k++)
        {
            float v = c.Values[k * stride];
            if (!(v > 0f)) return false;
            lo = MathF.Min(lo, v); hi = MathF.Max(hi, v);
            if (k > 0) { up |= v > prev; down |= v < prev; }
            prev = v;
        }
        return up && down;
    }

    /// <summary>The pulse test and the wave's bounds, headless.</summary>
    private static void FlickerChecks(Checker check)
    {
        ControllerTrack T(params float[] v) => new() { Type = CtlRadius, Columns = 1, Times = new float[v.Length], Values = v };
        check.Expect(IsPulse(T(5f, 6f, 5.2f, 6f), out float lo, out float hi) && lo == 5f && hi == 6f,
                     "a torch's rising-and-falling radius track is a pulse, its range the lowest and highest key");
        check.Expect(!IsPulse(T(0f, 3f, 6f), out _, out _), "a day-to-night ramp is not a pulse");
        check.Expect(!IsPulse(T(1f, 3f, 6f), out _, out _), "a monotonic ramp that starts lit is not a pulse either");
        check.Expect(!IsPulse(T(6f, 0f, 6f), out _, out _), "a blink through zero is a switch, not a flame");
        check.Expect(!IsPulse(T(5f, 6f), out _, out _), "two keys are a line, not a pulse");

        var m = new ModelDef { Name = "cxtest_torch" };
        m.Nodes = new[] { new NodeDef { Name = "torchlight", Kind = NodeKind.Light } };
        var night = new AnimDef { Name = "night", Nodes = new[] { new AnimNode { Name = "TorchLight", Controllers = new[] { T(0f, 4f, 8f) } } } };
        var loop = new AnimDef { Name = "animloop01", Nodes = new[] { new AnimNode { Name = "TorchLight", Controllers = new[] { T(7f, 8f, 7.5f) } } } };
        m.Anims = new[] { night, loop };
        PulseRange r = Scan(m, "torchlight", CtlRadius);
        check.Expect(r.Any && r.Lo == 7f && r.Hi == 8f, "the scan takes only the pulse track, matched by name without case");
        check.Expect(!In(r, 0f) && In(r, 7.4f), "a light at zero (dark by day) is outside the pulse and passes through raw");
        bool bounded = true;
        for (int k = 0; k < 200; k++) { float w = Wave(k * 0.037f, 1.3f, 0.4f); bounded &= w >= 0.6f - 1e-4f && w <= 1f + 1e-4f; }
        check.Expect(bounded && Wave(0f, 0f, 0f) == 1f, "the wave stays in [1-depth, 1], and depth 0 is no flicker");
    }
}
