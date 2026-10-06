# 0006 — Static-geometry batching: extender-owned draw submission for the static world

Status: **superseded by ADR 0016 (M4) and deleted in #258.** The batcher shipped, then the #215
cutover made `RspShim` own the frame -- and a replayed multidraw is the one draw path its funnel
swallow cannot see, so under the shipped default (`ownframe=1`) this service never submitted. #258
deleted it, its group registry, matrix probation and the 64K submit arrays; what survives is the hook
set, now `DrawFunnelService`. Under `ownframe=0` the parts it used to batch fall back to the engine's
own per-part draws, which is what they did whenever a group had one member. Kept for the RE it
records (the 2002 draw loop's per-item cost, the group-key derivation) and for its own history.

Originally: proposed (plan B of the #67 perf campaign; Ghidra session 2026-08-18)

## Context: what the 2002 draw loop costs

Measured (dense 120-light interior, 2544x1379): the main scene pass is ~1950 individual
`glDrawElementsBaseVertex` calls. RenderDoc shows single-quad draws (`idx=6`) costing 0.3-1.1ms
because every draw pays full per-draw overhead; the campaign's shader work got the frame
25.2 -> ~13.3ms, and most of what remains is this submission architecture, not shading.

Per bucket item, every frame (Ghidra `Scene::RenderDrawBucket` @0x46d060 nm,
`PartTriMesh::Draw` @0x4513d0 — dumps in `scratchpad/ghidra-out/drawchain.c`):

- 2x `RenderInterface::SetShaderParamv`, `SetModelMatrix` + matrix push/pop (matrix-path parts),
  cull-face/dissolve/fog toggles, per-part lighting Enable/Disable(0x11);
- material re-tests in `PartTriMesh::Draw` (HasBumpyShinyMap/HasEnvMap) to pick
  RenderFlat / RenderEnvironmentMapped / RenderBumpyShiny;
- `GLRender::SetUniforms` @0x4ccbf0 runs a linear dirty-scan over a 0x6c-stride uniform record
  table before each draw;
- (fixed by ADR 0005 + 3a4d570) per-draw light re-selection.

No batching, no instancing, no material sort. Bucket order is BSP/type order.

## Engine facts the plan builds on (Ghidra-verified)

1. **Geometry is already pooled.** `RenderFlat(VertexPrimitiveFlat*)` @0x450eb0 resolves
   `VertexDataObject::GetItemElementOffset/Size(vdo, item)` and calls
   `RenderInterface::RenderVertexData(vdo, item, primCount(mdl+0x270), size, offset)`.
   Parts are *items inside shared vertex buffers*. Merging same-VDO parts = concatenating
   index ranges; no vertex copying.
   Mechanics (Ghidra `GLRender::RenderVertexData` @0x4d0570): mode(+0x14)==1 items draw via
   `glDrawElementsBaseVertex(prim, count, GL_UNSIGNED_SHORT, elemOffset*2, itemVertexOffset)` after
   `SetVertexDataObject` + attribute-derived `SetProgram`. So a group needs NO merged index buffer at
   all: `glMultiDrawElementsBaseVertex` submits the whole group from arrays of counts/offsets/
   basevertices. Item table = `{offset,size}` pairs at VDO+0x48 (vertex) / +0x58 (element);
   `VertexDataObject::Repack()` can MOVE item offsets (AddItem triggers it on fragmentation) --
   an invalidation hook point alongside RemoveItem/dtor.
2. **Static tile parts are pre-transformed to world space.** The `RenderDrawBucket` loop only
   pushes a model matrix for parts with `(node+0x80 & 0x1e)` or a skin; plain
   tile geometry draws with identity model transform (this is why the shadow pipeline's
   world-space bbox cull works on them). Merged world-space batches are trivially correct.
3. **Bucket phases.** `RenderDrawBucket(bucket)` runs three sub-phases (0, 1, 2):
   default cull, `Disable(3)` (two-sided), `SetCullFace(0)` — state resets at phase end.
   Opaque phases are depth-tested and order-independent; merged draws can be submitted at the
   position of their first member.
4. **Per-vertex static lighting is already bypassed.** We run `g_bFullDynamicLighting=1`
   (ADR 0005 phase 2) — tiles are lit by the extender's whole-area light loop, so batches
   need no per-tile bake data and lighting fidelity is inherited from the receiver shaders.
5. Tracy is compiled into the client (`g_bTracyEnabled`, exported). A connected Tracy client
   names the engine's own CPU zones — but it is EXPENSIVE while connected (per-zone rdtsc +
   queue, and `RenderDrawBucket` heap-allocates label strings per call), so perf numbers from
   Tracy-attended runs are not comparable.

## Decision

The extender owns *submission* of opaque, pre-transformed static geometry: record each part's
draw once (VDO id, item, index range, and the engine state that draw ran under), group parts by
(GL program, material/texture binds, VDO, per-part shader params), build one merged index buffer
per group, and from then on draw each group once per frame — letting the engine set up state for
the group's first member and early-outing every other member's per-part path. Everything not
recorded (animated, transparent, matrix-path, skinned, emitters) stays 100% vanilla.

This is submission-level, not content-level: the bytes drawn are exactly the bytes the engine
would draw, in the same target, under state the engine itself configured. Fidelity by
construction, not by reimplementation.

## Phases

### Phase 0 — inventory + proof of mechanics (no visual change)

- Instrument the draw path (hook `RenderVertexData` or `PartTriMesh::Draw`): per-frame histogram
  of draws by bucket, phase, VDO, program, material identity, matrix-path vs pre-transformed,
  index count. Log the top groups. Exit criterion: measured % of draws that are opaque +
  pre-transformed + same-VDO groupable (expectation from RenderDoc: the large majority of ~1950).
- Decompile `RenderInterface::RenderVertexData` to confirm bind+draw mechanics and the index
  buffer layout (element offset/size semantics), and `VertexDataObject` lifetime (when items are
  freed — area transition hazard, cf. the AurTexture lifetime lesson).
- One Tracy capture (user-driven) to rank remaining CPU zones and validate the histogram.

### Phase 1 — merged static batches (the payoff)

- Recording: first time a groupable part draws in an area, capture (VDO, item, offset, size)
  keyed by part pointer + a group key = (program, bound textures, SetShaderParamv values,
  RenderHint, cull phase). Parts whose material animates (scrolling/water textures — detect via
  material flags or exclusion list) are never recorded.
- Steady state: when the first member of a completed group reaches its draw, submit the group's
  merged index buffer with the engine's just-configured state (one `glDrawElements` per group);
  all other members early-out before their per-part state churn. Groups draw inside the same
  bucket/phase they came from.
- Invalidation: area change wipes everything (VDO lifetime); any member's re-record (engine
  re-uploaded an item) invalidates its group; `CX_BATCH=0` kill switch; per-group disable for
  bisection.
- Our own shadow depth passes also run `RenderDrawBucket` — the suppression must be gated to the
  camera pass (the shadow pass wants the vanilla per-part walk, or its own batch instance later).

### Phase 2 — static placeables (matrix-path parts whose matrix never changes)

As built: no CPU bake. The model matrix (node+0x18, what the bucket loop feeds SetModelMatrix)
joins the group key -- parts sharing a bit-identical matrix (multi-part placeables under one
transform) batch under their first member, whose own draw applies the matrix. A one-frame
probation (matrix must be bit-stable across two sightings) keeps movers (creatures) from minting
dead one-member groups; any later matrix change permanently demotes the part to vanilla.

### Phase 3 — iteration-level ownership (as built) + optional follow-ups

Phases 1–2 only suppressed the terminal draw: every batched member still paid the full
`RenderDrawBucket` loop body (matrix push, 2x SetShaderParamv, fog/light/cull toggles, keyhole
test, SetUniforms dirty-scan) because the hook sat inside `PartTriMesh::Draw`. Phase 3 hooks
`DrawBucketManager::GetNextItem` @0x47be90 (the walk's only item source; `GetFirstItem` funnels
through it) and loop-skips slots whose PartTriMesh (bucket `Part` vtable+0x60 devirt, exactly the
caller's own) is already covered by this walk's multi-draw — the engine never sees them. The end
sentinel derefs to null, the caller's own exit test, so skipping is transparent. Skipped members
are revalidated in the filter (pointer identity, texture rotation/animation, fog factor,
`Camera::GetCanKeyholeDissolve(CurrentCamera, tri)`); any change falls through to the vanilla
loop body where OnPartDraw demotes as before. The iterator sighting doubles as the liveness
signal (`_pSeen`). Tables sized for outdoor areas (64k parts / 8k groups) with a pressure wipe,
since area transitions leave dead entries (only module unload wipes).

Still optional, after measurement: front-to-back group ordering, sharing batches with the shadow
depth pass, retiring `Scene::AddPartsToDrawBuckets`' per-frame BSP re-walk.

## Correctness strategy

- Opaque + depth-tested + same state + same target =\> draw order within a phase is irrelevant
  except exact-duplicate z (engine draws them in a fixed order today; merged order is the same
  recorded order).
- A/B: `CX_BATCH=0` vs 1 screenshot diffs in a fixed camera save; any pixel diff outside
  temporal effects fails the phase.
- The recorded state snapshot is never *applied* by us — the engine applies it (first member);
  we only suppress redundant re-application. Skew impossible by design.

## Risks

- **Per-part `SetShaderParamv` values** differ within a would-be group -\> params are part of the
  group key; worst case groups shrink (still >>1 part/group for tilesets).
- **VDO/item lifetime** (area transitions, resource eviction) -\> wipe-on-area-change + re-record;
  never cache across `CNWCArea*` change (AurTexture lesson).
- **Dissolve/fade** (SetCanDissolve path) -\> such parts excluded (state differs per part).
- **Texture animation** on tiles (waterfalls) -\> exclusion by material; phase 0 quantifies how
  many draws this forfeits.
- **Reflection subframes** render the same buckets with a mirrored camera -\> batches are
  submission-order agnostic, they just draw again in that pass; no special handling expected,
  verified in phase 1.

## Expected outcome

Opaque static draws collapse from ~1500+ to the number of distinct (program, material, VDO)
groups — typically tens for a tileset area. Per-draw CPU (state churn + SetUniforms dirty scans +
driver submit) scales down by the same factor; walking/torch CPU spikes shrink with it. GPU-side
gain is secondary (fewer state changes, same fragments).
