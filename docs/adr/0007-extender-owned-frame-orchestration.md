# 0007 — Extender-owned frame orchestration

Status: phase 1 (the deterministic per-frame pass slot) still live; phases 2-3 **retired** by #87 —
the receiver-uniform upload, SowxLights/SowxCubes injection and the OwnedShade program takeover were
deleted once the owned pass (ADR 0009 M1) became the only custom lighting code. Kept as the record of
why the pass slot exists and what the engine-program route cost.

## Context

An entire session was spent debugging lights that flickered "randomly" per zone. The root cause
was never the data — the CPU light snapshot was correct in every diagnostic dump — it was
*cadence*: our depth pass, cube maintenance, and receiver uniform upload all rode
`Scene::RenderShadows`, which the engine calls only when its own camera-prioritized shadow
selection wants stencil work. Zone- and fade-dependent, so whole frames passed with no upload and
the receiver shaders ran on a stale light snapshot. A present-time self-fire patched it, but the
lesson generalizes: **every extender render stage that fires on an engine-chosen trigger is a
latent zone-dependent bug.**

Direction (user, 2026-08-19): stop debugging legacy cadence; own the rendering process so the
schedule is ours and the engine supplies only geometry and state.

## Decision

The extender owns the frame schedule. Engine callbacks are used only as (a) data taps (captures,
call-throughs) and (b) suppression points (skipping vanilla work we replace) — never as triggers
for our own passes. Our passes fire from fixed, unconditional points in the frame that the engine
executes every frame regardless of content.

## The frame map (Ghidra `Scene::Render` @0x5762d0)

    Scene::Render
      ManageSceneBSP(scene)          -- BSP collect (camera-culled); our caster re-walk augments here
      ...light/wind/uniform ticks...
      AddPartsToDrawBuckets(scene)   -- buckets complete            <== ADR 0007 pass slot
      (vtbl+0x218) render dispatch   -- RenderShadows (conditional!) + bucket draws
    ...
    GLRender::SwapBuffers            -- present; UiHost roll-call (frame bookkeeping, UI)

## Phases

### Phase 1 — deterministic pass slot (implemented)

All extender render passes (light-space caster depth map, cube pass, receiver uniform upload —
`DepthPass` and everything it drives) fire from a hook on `Scene::AddPartsToDrawBuckets`
(@0x4751d0, after original): buckets complete, nothing drawn yet, executes every frame in every
zone. First `Scene::Render` per presented frame only (reflection subframes re-enter; the maps are
light-space/camera-independent). `Scene::RenderShadows` is reduced to vanilla suppression + a
fires-per-frame diagnostic; the present-time self-fire fallback is deleted. The light snapshot
consumed is the previous frame's walk (`GetShadowLights` runs later, inside the render dispatch)
— a constant one-frame latency, identical in every zone, instead of an unbounded zone-dependent
staleness.

### Phase 2 — own the walk (implemented)

The light snapshot self-drives at the pass slot: `OnAddPartsToDrawBuckets` resolves the
LightManager from the Scene (`Scene+0x158`, Ghidra `Scene::RenderSinglePass` @0x56e5f0) and runs
the master-list walk there, immediately before `DepthPass` — zero-frame light latency, one walk
per frame instead of the engine's 2–3. The `GetShadowLights` hook is a pure call-through
diagnostic (`_gslFires`).

### Phase 3 — own the main-pass shading (slice 1 implemented)

Slice 1 (implemented, `OwnedShade`): batched static groups draw under OUR program object -- the
engine's currently-bound program is cloned through GL itself (`glGetShaderSource` reads back the
exact compiled source, hak overrides included), linked under the engine's fixed attribute map
(Ghidra `GLRender::ConnectShaderData` @0x5ce8a0: 0=vPos 1=vTcIn 2=vColor 3=vCustomColor
4=vTangent 5=fHandedness 6=vStaticLightDir 7=vNormal 8=vIndex 9=vWeight 10=fProjectionFrontal
11-13=vTcIn1-3), active uniforms mirrored engine->clone once per frame. Pixel-identical by
construction (`SOWX_OWNSHADE=0` reverts), but the program is now ours to modify.

Slice 2 (implemented): clones register as receiver programs, so the pass-slot upload feeds them
every sowx* lighting/shadow uniform DIRECTLY; the per-frame mirror skips sowx* (one full mirror at
clone time bridges to the first upload). Lighting state on owned programs no longer round-trips
through the engine program at all. Non-lighting uniforms (matrices, fog, wind) stay mirrored --
they are engine *state*, not engine *cadence*, and owning them buys no debuggability yet.

Slice 3 (implemented): link-time TAKEOVER (default, `SOWX_OWNSHADE=2`; `1` = slice-2 behavior,
`0` = off). Key Ghidra fact: `GLRender::ConnectShaderData` @0x4ce8e0 binds the fixed attribute
map, links, and resolves EVERY engine uniform location against the program id at
`PROG_FIXED+0x64`. So at `LoadShader` return we compile a clone of the program's exact readback
source (patches applied), write the clone's id into the record, and call `ConnectShaderData`
again -- the ENGINE re-wires itself onto our program and drives it for every draw: no mirroring,
whole-scene consistency, source owned. Scoped to fragment sources containing `sowxAreaLight`
(scene receivers); failures restore the engine id and re-wire back. Source patches are the
no-hak-deploy shader channel (first knob: `SOWX_LIGHTBOOST` scales the overbright multiplier).
A relink of a taken-over record hands the clone back to the engine (detected + left alone;
re-takeover on next LoadShader).

## Correctness

- The pass slot is strictly before the frame's own draws, so uploads are never mid-scene.
- Rollback: the hook is one call-through; disabling the shadow service restores vanilla whole.
- Diagnostic invariant: the pass fires from `AddPartsToDrawBuckets` and NEVER from
  `Scene::RenderShadows`, whose cadence is the engine's own zone-dependent whim. The #98 cutover
  deleted the counter that used to prove this (`_shadowFires`) along with the last hook on that
  symbol — if a lighting bug ever correlates with engine shadow cadence again, an engine-cadence
  dependency has crept back in and the counter is worth re-adding for the length of the hunt.

### Phase 4 — modern data path (slice 1 implemented: lighting UBO)

Owned programs read the whole area-light snapshot from ONE std140 uniform block (`SowxLights`:
vec4 posRange[128], vec4 colorSlot[128], vec4 meta) filled by a single `glBufferSubData` per
frame at the pass slot -- replacing ~130 uniform calls x ~70 receiver programs per upload and
making extender/shader capacity skew (the 0x502 class) structurally impossible. Enabled by the
`SOWX_UBO` define takeover injects after `#version`; shaders without the branch (old hak) and
non-taken-over programs keep the individual-uniform path, detected per program at resolve
(`glGetUniformBlockIndex`). Slice 2: the per-slot cube state (six 32-element `sowxLocal*` arrays + count/near/bias) moved into
a second block, `SowxCubes` (binding 7) -- declared with IDENTICAL member names/types as the
individual uniforms (std140 pads vec3/float/int array elements to a 16-byte stride, mirrored by
the staging layout; int members are raw int bits), so zero shader-logic changes. The depth-pass
receiver mute becomes one 4-byte buffer write instead of a per-program loop. Remaining
candidates: sun/shadow matrices, then per-draw model matrices via persistent-mapped buffers.

