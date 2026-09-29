using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>
/// Eased animation crossfades (CX-27, <c>anim.crossfade</c>). The engine fades one animation into the next with a weight
/// that rises in a straight line (<see cref="SymbolMap.AnimateHierarchy"/>), so every change starts and stops with a
/// jolt in velocity. The weight is remapped by smoothstep on its way into the hierarchy, and only there: the run keeps
/// its own linear weight, so the engine's timing and its removal of the old runs at 1 are unchanged.
/// </summary>
public sealed unsafe class CrossfadeService : IExtenderService
{
    private static AnimateHierarchyFn* _next;
    /// <summary>Inside an outermost call: the recursion re-enters the hook once per child part, and a weight eased at
    /// every depth would be eased again at each. Main thread only (<c>Scene::Animate</c>), as the engine's own depth count.</summary>
    private static bool _inside;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _ = AnimKnobs.G;
        var check = new Checker(log);
        check.Expect(SelfCheck(), "a crossfade weight eases in and out: 0 and 1 kept, rising, level at both ends, anything outside (0, 1) untouched");
        log.Info(check.Ok ? "CrossfadeService.SelfCheck: passed" : "CrossfadeService.SelfCheck: FAILED");
        if (!SymbolMap.Require(log, "Crossfade", SymbolMap.AnimateHierarchy)) return;
        _next = hooks.ChainAnimateHierarchy(&OnAnimateHierarchy, 50);
        log.Info(_next == null ? "Crossfade: AnimateHierarchy chain refused -- crossfades stay linear"
                               : $"Crossfade: chained AnimateHierarchy (crossfade {(AnimKnobs.Crossfade.V ? "on" : "off")})");
    }

    [UnmanagedCallersOnly]
    private static void OnAnimateHierarchy(IntPtr part, IntPtr node, float from, float to, float weight)
    {
        AnimateHierarchyFn next = *_next;
        if (_inside) { next(part, node, from, to, weight); return; }
        _inside = true;
        next(part, node, from, to, AnimKnobs.Crossfade.V ? Ease(weight) : weight);
        _inside = false;
    }

    /// <summary>Smoothstep inside (0, 1); the weights the engine sets outright (0, 1) pass unchanged.</summary>
    internal static float Ease(float w) => w > 0f && w < 1f ? w * w * (3f - 2f * w) : w;

    /// <summary>The remap keeps the ends, never falls, starts and ends level (a step at either end smaller than the
    /// linear step squared, times a margin), and leaves 0, 1, outside values and NaN alone.</summary>
    internal static bool SelfCheck()
    {
        const int N = 100;
        float prev = 0f;
        bool rising = true;
        for (int i = 1; i <= N; i++)
        {
            float e = Ease(i / (float)N);
            rising &= e >= prev;
            prev = e;
        }
        float h = 1f / N, first = Ease(h), last = 1f - Ease(1f - h);
        bool level = first < 4f * h * h && last < 4f * h * h;
        return rising && level && Ease(0f) == 0f && Ease(1f) == 1f && Ease(0.5f) == 0.5f
               && Ease(-0.25f) == -0.25f && Ease(1.5f) == 1.5f && float.IsNaN(Ease(float.NaN));
    }
}
