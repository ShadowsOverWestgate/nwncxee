# 0008 — Extender-owned caster geometry (cube depth passes)

Status: accepted (slice 1 implemented, pending live verification; #67 lighting campaign,
RenderDoc + Ghidra session 2026-08-19)

## Context

RenderDoc GPU counters on a saturated frame (nwn_frame1052) attributed the cost precisely:

| pass | target | draws | GPU |
|---|---|---|---|
| lo-tier cube bakes | 256px ×192 slices | 165 | 2.5 ms |
| **hi-tier static bake** | **1024px ×12 (2 hi cubes)** | **2261** | **23.7 ms** |
| other hi bakes | same array | 28 + 139 | 3.8 ms |
| main colour pass | 2544×1379 RGB16F | 1318 | 16.6 ms |

A 6-index draw in the hi bake cost 0.45 ms; the average draw was 362 indices. The cost is
**per-draw state churn** (the 2002 engine configures program/textures/uniforms around every part
draw), not raster or geometry. Every cube face re-ran the engine's bucket walk, multiplying that
churn ×6 faces ×buckets. CPU-side timers never saw it — the cost is GPU/driver-side.

`CX_LOCALHISLOTS=0` merely removes the biggest instance; the disease scales with any shadow
work routed through the engine's per-part path. The fix is to stop asking the engine to draw.

## Decision

**Own the caster submission, reuse the engine's geometry.** Ghidra (RenderVertexData
@0x4d0570, SetVertexDataObject @0x4cff30, VertexDataObject::Build @0x45bac0) established:

- `VDO+0xc` holds a **VAO** the engine itself builds; position is attribute **location 0**
  (attr bit 4 → `glVertexAttribPointer(0, 3, GL_FLOAT, ...)`). `VDO+0x14` mode (1 = indexed
  static), item tables `{offset,size}` at `+0x48` (vertex) / `+0x58` (element).
- `GLRender::SetVertexDataObject` binds (lazily creating) the VAO + element buffer and keeps the
  engine's current-VDO cache consistent — the Alloy path binds through it, never raw.

Per cube bake (`CasterPass.cs`; the `ShadowMapService.OwnedCast.cs` this ADR was written against is
gone — #93 folded Alloy submission into `CasterPass` and #98 renamed what was left `ShadowFeedService`):

1. **Collect once, not per face**: one bucket walk culls (range/height/size) and resolves each
   kept part to `(VDO, item, prim, model)` via a part-keyed capture cache; unknown parts are
   captured through the RenderVertexData funnel (draw swallowed). Blend parts stay excluded;
   multi-draw parts and mode-2 (streamed/skinned) VDOs become per-face engine fallbacks.
2. **Per face**: attach layer, one `glMultiDrawElementsBaseVertex` per (VDO, prim) group for
   world-space statics under a single depth-only program (uVP + uModel), one tiny draw per
   matrix-path dynamic, then the few fallbacks through the engine path. Face culling is a
   CPU test per record.
3. GL program saved/restored; `SetVertexDataObject(null)` on exit. Repack invalidates cached
   items; module unload wipes the cache; any capacity overflow falls back to the legacy bake
   wholesale. `CX_OWNCAST=0` kill switch.

The hi static bake drops from ~450 engine draws ×5 faces to ~a handful of multidraw calls
per face.

### Layered rendering: evaluated, deferred

Layered rendering (gl_Layer, 6 faces in one submission) was point 2 of the plan. Its purpose —
collapsing per-face submissions — is already achieved by collect-once + per-face multidraw:
face count now multiplies a handful of GL calls, not thousands. `ARB_shader_viewport_layer_array`
is not guaranteed in the client's GL 3.3 core context; a geometry-shader fallback taxes every
vertex. Revisit only if measurement shows the residual per-face loop matters.

## Phase plan

- **Slice 1 (this ADR): cube depth passes owned.** Implemented; verify live via the periodic
  `Alloy cast` log line + fps.
- **Slice 2: sun/near cascade passes** through the same collect/submit machinery (same disease,
  smaller dose).
- **Phase 3: main colour pass.** 16.6 ms with the identical per-draw profile. The seed exists:
  ADR 0006 BatchService (multidraw groups over live engine state) + ADR 0007 OwnedShade
  (Alloy program clones + lighting UBO). Direction: extend group coverage (matrix-path groups
  landed; env-mapped/bumpy-shiny and animated-texture classes remain), then migrate group
  submission from "first member primes engine state" to fully Alloy material state, at which
  point the engine's per-part loop is bypassed for the opaque scene the way slice 1 bypasses it
  for shadow depth.

## Consequences

- Shadow GPU cost becomes proportional to caster geometry, not caster count.
- The capture cache is another part-pointer-keyed identity table (same recycling hazards as
  BatchService; same guards: obj identity check, repack invalidation, module wipe).
- Depth-only program ignores per-material state; correct because blend parts are excluded and
  depth writes carry no material detail. Alpha-tested cutouts shadow as solid — identical to the
  legacy path's behaviour.

## Amendment (first live test, 2026-08-19)

### Passive capture (forensic run 14:02)

The forensic build proved replay capture dead: every capture line showed `draws=0 vdo=0x0` — a
part's `Render()` invoked cold (outside the walk's pushed light VP / matrix-mode bracket) draws
nothing, so the cache never populated and every bake stayed legacy. Capture is now **passive**:
a bracket around the part's *real* engine draw (legacy bake walk + Alloy path fallback loop)
attributes the `RenderVertexData` call to the part — the draw is never swallowed. Consequence:
the first bake of any area runs legacy (all parts unknown → fallback overflow) and self-populates
the cache; every subsequent static bake goes owned. No extra hooks; the bracket lives in code we
already drive.

First cut never submitted an Alloy draw: the 64-group cap aborted every static bake to legacy
(tile statics span more VDOs than that), while dynamic bakes routed skinned mode-2 creatures
through a hand-rolled per-face Render() replay — ~100k engine draws/period outside
RenderDrawBucket's own loop, with live-observed creature corruption in the main pass and extra
casters the engine walk would have skipped. Fixes: Alloy path now covers **static bakes only**
(dyn bakes fully legacy, creatures untouched), group cap 512, and every capacity overflow
degrades per-part to fallback instead of aborting the bake.
