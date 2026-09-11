using System.Runtime.InteropServices;
using Sowx.Framework;
using static Sowx.Framework.EngineLayout;

namespace Sowx.Alloy;

/// <summary>
/// The vanilla-shadow suppression seam (#65), and since #97 the WHOLE of what the extender does to the
/// engine's shadow path: bake timing lives in <see cref="CasterPass"/>'s own frame slot, and nothing here
/// schedules anything.
/// <para>Suppression is data-driven by default and decided one draw at a time: the sun/moon stencil is dropped
/// exactly while the Alloy sun cascades are fresh, and a POINT light's stencil exactly while that light holds
/// an Alloy shadow cube. So the engine's own shadows return by themselves wherever the replacement is not on
/// screen -- a disarmed pass, a vanilla-latched frame, an interior with no sun, a torch past the cube budget --
/// and the two never both darken the same surface.
/// The <c>noshadows</c> config setting overrides it: <c>0</c> leaves vanilla wholly alone, <c>1</c> is the unconditional
/// <b>selective</b> mode (sun/moon suppressed, point-light stencil volumes kept), <c>2</c> the unconditional
/// <b>full kill</b>. Both overrides are kept for A/B.</para>
/// Every mode leaves <c>Scene::RenderShadows</c> itself untouched so it keeps managing all
/// surrounding pipeline state; we only skip (or filter) the draws it dispatches. State-safe by
/// construction: the filtered sub-passes read Scene's fields but never write the ones
/// <c>RenderShadows</c> branches on.
/// </summary>
/// <remarks>
/// <para>Selective routing (all Ghidra-verified against the client binary): a point-light creature
/// shadow needs <c>DoGobShadows</c> (builds the volume into shadow buckets) + <c>RenderDynamicShadows</c>
/// (draws the buckets into stencil) + <c>RenderShadowPlanes</c> (the stencil-gated darkening quad).
/// The sun/moon is the light whose range (<c>PartLight+0x70</c>) exceeds
/// <see cref="EngineLayout.DirectionalRange"/> (sun ~100000, points ~10). <c>RenderShadowPlanes</c> is called through in
/// every mode but the full kill, because it is stencil-gated (<c>SetStencilFunc</c> before its quad) -- a
/// suppressed volume sets no bit, so its resolve paints nothing where it was suppressed and paints normally
/// where it was not, which is exactly what per-light suppression needs.</para>
/// <para>Skipping the wholesale <c>Scene::RenderShadows</c> pass instead is blocked on research doc
/// §6.2 (post-skip net GL state is runtime-dependent, multiple exits). Handlers are hot (render
/// thread) and allocate nothing -- pure pointer reads + trampoline call-through.</para>
/// </remarks>
public sealed unsafe class ShadowService : IExtenderService
{
    private const int ShadowDraws = 5;

    private const int SceneCurrentLight = 0x50;  // Scene's "current shadow light" slot, set per-light by RenderShadows.

    /// <summary>Config <c>noshadows</c>: 0 = never suppress (pure vanilla stencil), 1 = always suppress the
    /// sun/moon and keep point-light stencils, 2 = full kill, <see cref="ModeAuto"/> = the default -- suppress
    /// each half exactly while the owned replacement for it is on screen.</summary>
    private static int _mode = ModeAuto;
    private const int ModeAuto = 3;

    /// <summary>#97: the vanilla sun/moon stencil is suppressed while <see cref="CasterPass"/>'s cascades are
    /// fresh, and the point-light stencils while its cubes are. Data-driven rather than a flag, so the engine's
    /// shadows come back by themselves on any frame the Alloy maps are not there to replace them -- an
    /// interior with no sun keeps its point stencils, a disarmed pass reverts wholesale. Leaving BOTH on is
    /// what double-darkened every shadowed surface once the Alloy receiver started sampling the new maps.</summary>
    private static bool SuppressSun => !Vanilla && (_mode == ModeAuto ? CasterPass.CascadesFresh : _mode > 0);

    /// <summary>Per LIGHT, not per frame: only the nearest few point lights get a shadow cube, and the owned
    /// receiver's no-slot path returns full light in silence -- so suppressing every point stencil the moment
    /// ANY cube is fresh would delete the shadows of every light past the cube budget and replace them with
    /// nothing. A light with a cube is replaced; a light without one keeps its vanilla stencil volume.</summary>
    private static bool SuppressPoint(IntPtr light)
        => !Vanilla && (_mode == ModeAuto ? CasterPass.CubeSlotOf(light) >= 0 : _mode == 2);

    /// <summary>#86: true while the frame is latched vanilla -- every handler then calls straight through, so
    /// the engine's own stencil shadows come back with the rest of vanilla rendering.</summary>
    private static bool Vanilla => !Sowx.Alloy.AlloyGate.Alloy;

    private static delegate* unmanaged<IntPtr, void> _staticOrig;
    private static delegate* unmanaged<IntPtr, void> _dynOrig;
    private static delegate* unmanaged<IntPtr, IntPtr, void> _gobOrig;
    private static delegate* unmanaged<IntPtr, IntPtr, void> _meshOrig;
    private static delegate* unmanaged<IntPtr, int, int, void> _planesOrig;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        // Default AUTO since #97: M2 re-owns shadow receiving, so this seam is live again -- but keyed on
        // whether the Alloy maps are actually on screen, never on a bare flag. noshadows=0 opts out
        // entirely, 1/2 remain the unconditional A/B overrides.
        // Config, not a command: 0 skips the hook install outright, and a mode change is a different set of
        // engine draws being suppressed -- a boot decision on both counts.
        _mode = Settings.Int("noshadows", ModeAuto, 0, ModeAuto);
        if (_mode == 0) { log.Info("ShadowService: noshadows=0 -- vanilla stencil shadows left untouched"); return; }

        int hooked = 0;
        // Static/directional environment stencil: suppressed in BOTH modes (the map replaces sun/moon
        // environment shadows). No per-light context -- a fixed directional pass.
        // The trampoline is KEPT, not discarded: an Idle or Incapable frame has to be able to call it, and a
        // silently missing static-shadow pass reads as "vanilla just looks like that".
        _staticOrig = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.RenderStaticShadows,
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnRenderStaticShadows);
        hooked += _staticOrig != null ? 1 : 0;

        _dynOrig = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.RenderDynamicShadows, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnRenderDynamicShadows);
        hooked += _dynOrig != null ? 1 : 0;
        _gobOrig = (delegate* unmanaged<IntPtr, IntPtr, void>)hooks.Install(
            SymbolMap.DoGobShadows, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnDoGobShadows);
        hooked += _gobOrig != null ? 1 : 0;
        _meshOrig = (delegate* unmanaged<IntPtr, IntPtr, void>)hooks.Install(
            SymbolMap.DoDynamicMeshProjections, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnDoDynamicMeshProjections);
        hooked += _meshOrig != null ? 1 : 0;
        _planesOrig = (delegate* unmanaged<IntPtr, int, int, void>)hooks.Install(
            SymbolMap.RenderShadowPlanes, (IntPtr)(delegate* unmanaged<IntPtr, int, int, void>)&OnRenderShadowPlanes);
        hooked += _planesOrig != null ? 1 : 0;

        string what = _mode switch
        {
            1 => "SELECTIVE (sun/moon suppressed, point-light stencils kept)",
            2 => "FULL KILL (all stencil shadows suppressed)",
            _ => "AUTO (each half suppressed only while the Alloy map replacing it is fresh)",
        };
        string via = _mode == ModeAuto ? "default" : $"noshadows={_mode}";
        log.Info(hooked == ShadowDraws ? $"ShadowService: {via} -- {what} (5/5 draws hooked)"
            : hooked > 0 ? $"ShadowService: {via} -- PARTIAL ({hooked}/{ShadowDraws} draws hooked); some shadows will render wrong"
            : $"ShadowService: {via} but no shadow symbol resolved -- disabled (headless/non-client?)");
    }

    /// <summary>True when this light is the sun/moon (directional): its range exceeds
    /// <see cref="EngineLayout.DirectionalRange"/>. Null-safe (a null light is treated as non-directional so the
    /// caller can choose the safe branch).</summary>
    private static bool IsDirectional(IntPtr light)
        => light != IntPtr.Zero && *(float*)((byte*)light + LightRange) > DirectionalRange;

    /// <summary><c>RenderStaticShadows()</c>: the directional environment stencil, suppressed in both modes
    /// (the shadow map replaces it) -- and called straight through while vanilla. Dropping the draw is
    /// state-safe: <c>RenderShadows</c> brackets these calls with its own setup/restore. Alloc-free (render
    /// thread).</summary>
    [UnmanagedCallersOnly]
    private static void OnRenderStaticShadows(IntPtr self)
    {
        if (_staticOrig != null && !SuppressSun) _staticOrig(self);
    }

    /// <summary><c>RenderDynamicShadows()</c>: draws the shadow-volume buckets into stencil for the
    /// current light (<c>Scene+0x50</c>). Selective => draw only for point lights; full kill => drop.</summary>
    [UnmanagedCallersOnly]
    private static void OnRenderDynamicShadows(IntPtr self)
    {
        if (_dynOrig == null) return;
        IntPtr light = *(IntPtr*)((byte*)self + SceneCurrentLight);
        if (!(IsDirectional(light) ? SuppressSun : SuppressPoint(light))) _dynOrig(self);
    }

    /// <summary><c>DoGobShadows(PartLight*)</c>: builds a creature (gob) shadow volume for this light.
    /// Selective => build only for point lights; full kill => drop. Pure stencil suppressor: the cube-shadow
    /// light feed is the LightManager master-list walk in SceneInputs (ADR 0005 phase 1), not this hook.</summary>
    [UnmanagedCallersOnly]
    private static void OnDoGobShadows(IntPtr self, IntPtr light)
    {
        if (_gobOrig != null && !(IsDirectional(light) ? SuppressSun : SuppressPoint(light))) _gobOrig(self, light);
    }

    /// <summary><c>DoDynamicMeshProjections(PartLight*)</c>: dynamic mesh projection for this light.
    /// Selective => build only for point lights; full kill => drop.</summary>
    [UnmanagedCallersOnly]
    private static void OnDoDynamicMeshProjections(IntPtr self, IntPtr light)
    {
        if (_meshOrig != null && !(IsDirectional(light) ? SuppressSun : SuppressPoint(light))) _meshOrig(self, light);
    }

    /// <summary><c>RenderShadowPlanes(int, int)</c>: the stencil-gated darkening resolve (draws the
    /// shadow quad where a volume set stencil). Selective => call through (paints only point-light
    /// volumes -- the directional resolve is a no-op with its builder suppressed); full kill => drop.</summary>
    [UnmanagedCallersOnly]
    private static void OnRenderShadowPlanes(IntPtr self, int a, int b)
    {
        // Stencil-gated: it only paints where a volume set a bit, so with point suppression decided per
        // light there is no frame-wide way to know nothing built one -- call it through and let an empty
        // stencil make it a no-op. Only the unconditional full kill drops the resolve itself.
        if (_planesOrig != null && _mode != 2) _planesOrig(self, a, b);
    }
}
