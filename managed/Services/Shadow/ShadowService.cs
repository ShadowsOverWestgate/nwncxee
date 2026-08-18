using System.Numerics;
using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services.Shadow;

/// <summary>
/// Slice 1 of the shadow-map replacement (#65): the vanilla-shadow suppression seam.
/// <para><c>SOWX_NOSHADOWS=1</c> (the ship config) is <b>selective</b>: it suppresses only the
/// sun/moon (directional) stencil contribution -- our shadow map replaces that -- while calling
/// through the per-light passes for POINT lights, so torch/lantern creature shadows (up to a few
/// per creature) keep rendering as vanilla stencil volumes. <c>SOWX_NOSHADOWS=2</c> is the
/// <b>full kill</b>: every shadow draw is no-op'd (the original behaviour, kept for A/B).</para>
/// Both modes leave <c>Scene::RenderShadows</c> itself untouched so it keeps managing all
/// surrounding pipeline state; we only skip (or filter) the draws it dispatches. State-safe by
/// construction: the filtered sub-passes read Scene's fields but never write the ones
/// <c>RenderShadows</c> branches on. Off by default: env unset => no hooks, frame path untouched.
/// </summary>
/// <remarks>
/// <para>Selective routing (all Ghidra-verified against the client binary): a point-light creature
/// shadow needs <c>DoGobShadows</c> (builds the volume into shadow buckets) + <c>RenderDynamicShadows</c>
/// (draws the buckets into stencil) + <c>RenderShadowPlanes</c> (the stencil-gated darkening quad).
/// The sun/moon is the light whose range (<c>PartLight+0x70</c>) exceeds
/// <see cref="DirectionalRange"/> (sun ~100000, points ~10). <c>RenderShadowPlanes</c> is always
/// called through because it is stencil-gated (<c>SetStencilFunc</c> before its quad) -- with the
/// directional volume builders suppressed no directional stencil bit is set, so its sun resolve
/// paints nothing.</para>
/// <para>Skipping the wholesale <c>Scene::RenderShadows</c> pass instead is blocked on research doc
/// §6.2 (post-skip net GL state is runtime-dependent, multiple exits). Handlers are hot (render
/// thread) and allocate nothing -- pure pointer reads + trampoline call-through.</para>
/// </remarks>
public sealed unsafe class ShadowService : IExtenderService
{
    private const int ShadowDraws = 5;

    /// <summary>Directional (sun/moon) lights carry range ~100000; point lights ~10. A light whose
    /// range (<c>PartLight+0x70</c>) exceeds this is the sun/moon; its stencil is suppressed in
    /// selective mode (the shadow map owns it). Matches ShadowCaptureService's discriminator.</summary>
    private const float DirectionalRange = 1000f;
    private const int LightRange = 0x70;         // PartLight range field.
    private const int SceneCurrentLight = 0x50;  // Scene's "current shadow light" slot, set per-light by RenderShadows.

    // Point-light fields for slice-6 cube shadows, all Ghidra-verified from Scene::DoGobShadows' dist-cull:
    // world pos = (+0xac,+0xb0,+0xb4); reach = (+0x78>0 ? +0x78 : +0x70); RGB = +0x64. We use the REAL (unlifted)
    // light position as the cube centre so it equals the engine's lightPosition[i] the receiver matches against;
    // vanilla's +0x7c Z-lift was a shadow-cull tweak, not a projection origin, and lifting would break that match.
    private const int LightPos = 0xac;       // point-light world position X (Y=+0xb0, Z=+0xb4).
    private const int LightRangeAlt = 0x78;  // preferred reach when > 0, else LightRange.
    private const int LightColor = 0x64;     // RGB.

    /// <summary>true for <c>=1</c> (keep point-light stencils), false for <c>=2</c> (full kill).</summary>
    private static bool _selective;

    private static delegate* unmanaged<IntPtr, void> _dynOrig;
    private static delegate* unmanaged<IntPtr, IntPtr, void> _gobOrig;
    private static delegate* unmanaged<IntPtr, IntPtr, void> _meshOrig;
    private static delegate* unmanaged<IntPtr, int, int, void> _planesOrig;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        // Default: selective suppression (mode 1) whenever the replacement pipeline is on (SOWX_SHADOWS!=0) --
        // the map owns the sun, vanilla point stencils are replaced by the cubes. SOWX_NOSHADOWS=1/2 remains
        // an explicit A/B override; SOWX_SHADOWS=0 with no override = pure vanilla shadows.
        string? mode = Environment.GetEnvironmentVariable("SOWX_NOSHADOWS");
        if (mode == null && Environment.GetEnvironmentVariable("SOWX_SHADOWS") != "0") mode = "2";
        if (mode != "1" && mode != "2") return;
        _selective = mode == "1";

        int hooked = 0;
        // Static/directional environment stencil: suppressed in BOTH modes (the map replaces sun/moon
        // environment shadows). No per-light context -- a fixed directional pass.
        hooked += hooks.Install(SymbolMap.RenderStaticShadows,
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&SkipSubPass) != IntPtr.Zero ? 1 : 0;

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

        string what = _selective
            ? "SELECTIVE (sun/moon suppressed, point-light stencils kept)"
            : "FULL KILL (all stencil shadows suppressed)";
        log.Info(hooked == ShadowDraws ? $"ShadowService: SOWX_NOSHADOWS={mode} -- {what} (5/5 draws hooked)"
            : hooked > 0 ? $"ShadowService: SOWX_NOSHADOWS={mode} -- PARTIAL ({hooked}/{ShadowDraws} draws hooked); some shadows will render wrong"
            : $"ShadowService: SOWX_NOSHADOWS={mode} but no shadow symbol resolved -- disabled (headless/non-client?)");
    }

    /// <summary>True when this light is the sun/moon (directional): its range exceeds
    /// <see cref="DirectionalRange"/>. Null-safe (a null light is treated as non-directional so the
    /// caller can choose the safe branch).</summary>
    private static bool IsDirectional(IntPtr light)
        => light != IntPtr.Zero && *(float*)((byte*)light + LightRange) > DirectionalRange;

    /// <summary>Skip-original for a shadow sub-pass (<c>this</c>-only ABI). Returning without calling
    /// the trampoline drops the whole draw; alloc-free -- fires on the render thread. State is untouched
    /// because <c>RenderShadows</c> brackets these calls with its own setup/restore.</summary>
    [UnmanagedCallersOnly]
    private static void SkipSubPass(IntPtr self) { }

    /// <summary><c>RenderDynamicShadows()</c>: draws the shadow-volume buckets into stencil for the
    /// current light (<c>Scene+0x50</c>). Selective => draw only for point lights; full kill => drop.</summary>
    [UnmanagedCallersOnly]
    private static void OnRenderDynamicShadows(IntPtr self)
    {
        if (_dynOrig == null || !_selective) return;
        IntPtr light = *(IntPtr*)((byte*)self + SceneCurrentLight);
        if (light != IntPtr.Zero && !IsDirectional(light)) _dynOrig(self);
    }

    /// <summary><c>DoGobShadows(PartLight*)</c>: builds a creature (gob) shadow volume for this light.
    /// Selective => build only for point lights; full kill => drop.</summary>
    [UnmanagedCallersOnly]
    private static void OnDoGobShadows(IntPtr self, IntPtr light)
    {
        // DoGobShadows fires once per engine-prioritized shadow light; record the point lights (both modes) so
        // the cube-shadow pass can rebuild them, then apply the mode's suppression. Recording is copy-only.
        if (light != IntPtr.Zero && !IsDirectional(light)) RecordLocalLight(light);
        if (_gobOrig != null && _selective && !IsDirectional(light)) _gobOrig(self, light);
    }

    /// <summary>Copy this point light's world pos (Z-lifted), reach and RGB into <see cref="ShadowInputs"/> for
    /// the cube-shadow pass. Alloc-free: raw float reads + struct writes; never retains the <c>PartLight*</c>.</summary>
    private static void RecordLocalLight(IntPtr light)
    {
        float* p = (float*)light;
        float alt = p[LightRangeAlt / 4];
        float range = alt > 0f ? alt : p[LightRange / 4];
        var pos = new Vector3(p[LightPos / 4], p[(LightPos + 4) / 4], p[(LightPos + 8) / 4]);
        var color = new Vector3(p[LightColor / 4], p[(LightColor + 4) / 4], p[(LightColor + 8) / 4]);
        ShadowInputs.RecordLocalLight(pos, range, color);
    }

    /// <summary><c>DoDynamicMeshProjections(PartLight*)</c>: dynamic mesh projection for this light.
    /// Selective => build only for point lights; full kill => drop.</summary>
    [UnmanagedCallersOnly]
    private static void OnDoDynamicMeshProjections(IntPtr self, IntPtr light)
    {
        if (_meshOrig != null && _selective && !IsDirectional(light)) _meshOrig(self, light);
    }

    /// <summary><c>RenderShadowPlanes(int, int)</c>: the stencil-gated darkening resolve (draws the
    /// shadow quad where a volume set stencil). Selective => call through (paints only point-light
    /// volumes -- the directional resolve is a no-op with its builder suppressed); full kill => drop.</summary>
    [UnmanagedCallersOnly]
    private static void OnRenderShadowPlanes(IntPtr self, int a, int b)
    {
        if (_planesOrig != null && _selective) _planesOrig(self, a, b);
    }
}
