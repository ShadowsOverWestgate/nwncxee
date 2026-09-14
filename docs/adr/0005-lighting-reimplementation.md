# 0005 — Lighting reimplementation: extender-owned light set over the engine's stencil-era prioritizer

Status: phase 1 (the tier-1 light registry) live and load-bearing for the Alloy pass. Phase 2
(neutralizing the engine's per-draw light binding so receiver shaders own point lighting) was
**retired** by #87 — LightingService is deleted and the engine's staging lighting is untouched
vanilla again; lighting is owned only inside Alloy's own pass (ADR 0009 M1).
(Ghidra session 2026-08-18; #65 follow-up)

## The engine pipeline (Ghidra-verified, nm addresses; Ghidra view = +0x100000)

`Scene+0x158` holds the `LightManager*`. Its vtable (`0x186e2a0`+0x10):
dtors, `PrioritizeLights`@+0x10, `EnableLights`@+0x18, `EnableLightsDynamicOnly`@+0x20,
`AddLight`@+0x28, `RemoveLight`@+0x30, `UpdateLights`@+0x38,
`SetupLightsForThisPositionAndRadius`@+0x40, `CombineLights`@+0x48,
`PrioritizeIllumination`@+0x50, `PrioritizeShadow`@+0x58, `GroupLights`@+0x60 (no-op),
`ControlLightsFade`@+0x68.

LightManager layout: **+0x10/+0x18 master `PartLight*` list/count (the persistent
ground truth — every registered light)**; +0x20 gob-owned sublist; +0x30 flag sublist;
+0x40/+0x48 per-frame "relevant" list; +0x50/+0x58 sorted shadow candidates;
+0x80/+0x88 chosen shadow list; +0x98 sun, +0xa0 second directional; +0xa8/+0xb8
per-mode chosen-list caches. Statics: `m_nMaxLights` (GetMaxLights caps at 128),
`m_nMaxShadowLights`, `m_pOnlyGob`. Toggle: `g_bFullDynamicLighting`.

PartLight fields: +0x64/68/6c color RGB, +0x70 range (+0x78 shadow-range override),
+0x80 directional flag, +0x84/+0x88 dynamic/static-ish flags, +0x8c priority override,
+0x98 cached camera distance, +0x9c priority score, +0xa4 intensity, +0xa8 fade
weight, +0xac..b4 world pos, +0x50 owner gob, +0x74 shadow-enabled.

Per frame (`Scene::RenderSinglePass`):
1. `UpdateLights` — refresh poses, latch sun (+0x98) by huge range.
2. `SetupLightsForThisPositionAndRadius(pos, r)` — master → relevant (+0x40) by
   range-sphere test around the camera focus.
3. `PrioritizeLights(pos)` — score each relevant light into +0x9c:
   `dist * (prio+1) / ((flags+4) * brightness)`; then `PrioritizeIllumination` +
   `PrioritizeShadow` (filter +0x74, introsort by score).
4. `GetShadowLights(mode)` — take ≤ `m_nMaxShadowLights` from the sorted list,
   **gated on cameraDist ≤ range** and smoothed by a per-light DeltaT fade (+0xa8).
   Results → `Scene+0x60`/`+0x70`, iterated into `DoGobShadows` per light (our
   record hook rode this). Mode 2 = single best light for
   `BSPCreateStaticProjections`.
5. `EnableLights`/`EnableLightsDynamicOnly` — `ControlLightsFade(active, relevant)`
   then `SetLightGL(light, i)` for i < 128 → `RenderInterface::SetLightProperties`
   (GL uniform arrays; index -1 = area ambient/diffuse). Per-gob:
   `Scene::RenderOnlyGobs` → `GetNearestLights(pos, r)` re-binds nearest slots.
6. Static geometry is vertex-BAKED: `Scene::SetTileLights` → `UpdateStaticLighting`,
   `LightNodeTile`/`LightNodeMeshes` → `LightPartTriMesh`, buckets via
   `DoStaticLightBuckets` (from `ManageSceneBSP`).

## Why it fails us

Steps 2–4 are a stencil-shadow-era budget optimizer: camera-dependent selection,
distance gating, per-light fades. Under cx cube shadows this made the tracked
light set churn with player movement (the #65 120-light interior: slots
evict/re-bake constantly, cubes sit inert). The per-frame registry hold-down
(tier 1, shipped) masks the churn but the feed is still the prioritizer's output.

## Decision (phased)

**Phase 1 — ground-truth light feed.** Drop the `DoGobShadows` record hook as the
light source. Each frame read `Scene+0x158` → master list (+0x10/+0x18), copy
pos/range/color/flags of every live `PartLight` into `ShadowInputs` directly
(alloc-free walk; guard all derefs). Keep the DoGobShadows/RenderDynamicShadows
hooks solely as stencil-volume suppressors. The tier-1 registry becomes redundant
and is removed. Nearest-K + slot hysteresis stay as-is.

**Phase 2 — extender-owned lighting (the rework).** Bypass the engine's per-draw
light binding for the lit path: the receiver shaders already get our full slot
set; extend the uploads to a complete light buffer (all area lights, not 32 —
UBO-sized arrays or texture buffer) and compute point lighting fully in the
shader (physical attenuation, per-light cube shadow term where a slot exists).
Engine contributions are neutralized by zeroing the colors it binds (hook
`SetLightGL` / `RenderInterface::SetLightProperties`) while keeping sun/ambient
(index -1 path) engine-side initially. Baked static vertex lighting stays as the
ambient/GI base at first; revisiting it (re-bake or replace) is its own ADR.
Per-fragment cost is bounded by a screen-space or per-draw light cull — the
extender can reuse the engine's own relevant-list radius logic per draw bucket.

Phase 2 needs its own spec + issue; this ADR fixes the direction and records the
RE facts so they aren't re-derived.

## Consequences

- Shadow-light tracking becomes deterministic and area-stable (fixes the #65
  churn class for good, no hold-down heuristics).
- Reading engine lists directly couples us to `LightManager`/`PartLight` layout —
  offsets above are the contract; verify on client patches.
- Phase 2 moves light budget from 128 engine slots to shader-side, opening
  >32-shadow scaling later (slot count becomes the only shadow cap).
