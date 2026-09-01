# 0019 — The shadow pass has no data plane of its own

Status: accepted (#302, 2026-08-30). Decision record for the three-rung rework in
`docs/research/alloy-shadow-rework.md`; the later rungs cite this rather than re-arguing it.
Amends ADR 0017, whose `Moved`-is-consumed hazard this closes. Does not touch ADR 0011's frame
graph or ADR 0018's bar, which it applies.

## Context

`CasterPass` predates Alloy. When it was written (#93, M2) there was no ownership table, no
persistent per-draw record and no owned geometry, so the shadow pass built all three for itself: an
enrolment scan over `PartTable`, a per-VDO slot map and VAO cache, a motion memo keyed on part
pointers with a ghost sweep behind it, and a per-frame `Collect()` that resolved every caster's
model matrix, index range and bounding sphere into staging arrays.

Every one of those now duplicates something Alloy keeps current:

| `CasterPass` had | Alloy has | since |
|---|---|---|
| its own caster table + 60-frame enrolment | the ownership table, scanned on its own cadence | #238 |
| its own motion memo, ghost sweep, `TableMoved`/`TableVanish` | the record's exact previous-value compare | #242 |
| its own `Collect()` into `_liveModel`/`_gRec` | the persistent `SowxDraw` record, written movers-only | #242 |
| its own bounding-sphere derivation | `SowxCullSphere`, per ownership slot | #243 |
| its own VDO slot map, VAO cache and repack hook | `OwnedBuffers` + the colour path's VAOs | #260 |
| its own bone ring and skin capture | `SkinPose` → `_oBone` → the record's `feat.y` | #262 |

ADR 0018's bar — *Alloy's frame is built from Alloy's own state* — was applied to the colour path in
M5 and never to the shadow path. This is that application.

## Decision

**A shadow caster is an ownership slot.** `Collect()` selects out of `AlloyRenderer`'s draw order and
resolves nothing that Alloy already resolved; the depth stages read `uDraw[slot]` through exactly the
indirection the colour stages use. The shadow-only record is reduced to the two things that
genuinely are not in `SowxDraw`, and there is no third format.

Four consequences are load-bearing enough to record.

### 1. What the record carries, and what it does not

`SowxDraw` carries the model matrix and — via `SowxBoneBase(d)`, i.e. `feat.x == SOWX_FEAT_SKIN ?
feat.y : -1` — the bone base. Those come from the record.

The **index range** does not live in the GLSL record on either path; it lives in the VDO item tables,
which move under `Repack`, and both paths re-read it live. The ticket's phrasing ("the record carries
all four") is loose: the honest statement is *the record buffer plus the ownership table's per-slot
state*, and `_oVdo`/`_oItem` are as much Alloy's state as `_dData` is.

The **cutout threshold** is not in `SowxDraw` at all — the colour path spends it as a per-batch
uniform, `uAlphaCut`. The bake does the same: two thresholds exist (0.2 coverage, 0.02 for an
env-mapped material whose diffuse alpha is reflectivity), the batch key carries which, and the
uniform is set per batch. Copying the colour path's own spelling is the point; inventing a per-draw
field for it would be the split being deleted, one field wide.

What is left of `SowxCaster` is `vec4 bounds; vec4 aux;` — the world sphere and the ownership slot,
plus the gather's LOD-exemption flag. The sphere stays because `SowxCullSphere` deliberately exempts
skinned, dangly and displacing parts with an infinite radius: right for a culler, useless for a
re-bake predicate, which has to *locate* a mover to name the views it dirties. `WriteCasterSphere`
writes the geometric sphere on the same edge, from the same terms. `ShaderContract`'s second-stride
guard is retired with the record it policed: there is no parallel material layout left to desync.

### 2. The mover signal is a level, never an edge

ADR 0017 recorded the hazard plainly: `Moved` was stateful and consumed once, the shadow pass runs
first, so whichever pass asked first took the edge and the other saw nothing.

`_recMovedAt[slot]` is stamped beside `_recFrame[slot]` — the first only on a build that changed
something, the second on every build — and the signal is the two being *equal*. Reading it changes
nothing, asking twice gives the same answer, and the colour path's own `_recDirty` consumption
(`UploadDraws` resets it) is untouched. The same shape covers the three signals a record compare
cannot see: bones and dangly are declarations captured at enrolment, and vertex dirt is
`OwnedBuffers.DirtySince` per VDO slot, read before `ClearDirty`.

The stamp is written on **geometry motion, not on any record rewrite**. Tile-fade alpha and the
day-night ambient drift rewrite hundreds of records a frame; charging those as movers re-baked every
cube every frame and the predicate skipped nothing (live-measured: 342 false "matrix movers" per
frame). So the model matrix is compared before the write consumes the difference, and a change of
slot tenant is the other true mover — the new part *appeared* in the views it stands in, which is
what keeps a re-tenanted slot from casting a stale silhouette.

### 3. Unknown BOUNDS mean cast; an unknown RECORD means wait a frame

*Corrected twice after the first live runs. The final state:*

A slot whose **sphere** is degenerate is collected with an infinite radius: it passes every reach test
and fails every sub-texel one, so it is neither culled nor LOD-dropped and simply bakes. That is
`CullPass`'s "unknown bounds mean visible" in the direction that is safe — a wrongly-admitted caster
costs a depth draw, a wrongly-rejected one deletes a shadow.

A slot whose **record was not built for its current tenant** casts nothing this frame. "Cast rather
than disappear" is right about unknown bounds; it cannot mean "bake this geometry through a matrix
that may belong to another part", which is not conservative but wrong — and the first version's
NeverCull-at-origin fallback also read as a permanent mover in every view, which silently defeated
the whole re-bake predicate. Refusal is not a freeze (the vanish sweep ghosts the slot at its last
known sphere, so the views it stood in re-bake), and what stays refused is only what the colour
build itself skips — a part nothing draws should cast nothing. The period line reports it as
`refused for want of a record`.

**Tenancy is its own generation, and it is narrower than the record epoch.** This took three
attempts, so the distinction is spelled out. The hazard is real: an enrollment reshuffles which part
holds which slot while the bake reads records a frame early, so a recycled slot can hold the
previous tenant's matrix. The first correction gated on an enrollment-generation pair; the second
deleted it as redundant with `_recGen != _recEpoch`, since `Enroll` bumps the epoch for exactly this
case. That deletion produced a once-a-second full shadow dropout, because the epoch is bumped by
**more than enrollment**: an animated texture retiring its old copy (`Drain → DropHandle →
InvalidateRecords`, roughly once a second in a torch-lit area) and a `/draw` dial both bump it —
*after* the frame's record build — so the next shadow bracket saw the whole table epoch-stale and
refused all of it: every view baked empty for one frame, every second. The epoch answers "must this
record be re-sent"; the bake's question is "was this record built for this part", and only `Enroll`
changes that answer. So `_oEnrollGen`/`_recEnroll` (bumped in Enroll alone, restamped by the same
colour pass that rebuilds the records) is the gate, and an epoch-stale record with agreeing tenancy
is trusted — its geometry is still right, and geometry is all the depth stages read.

The bug that actually produced "shadows float around and follow the camera" was §1's stride,
half-applied: the CPU stage shrank to 32 bytes while the GLSL struct kept `mat4 model` — a 96-byte
GPU stride, every `uCaster[i]` read landing on a neighbour's floats, every caster baking at a random
part's matrix. The lesson is recorded in §1's last line: the *second-record* guard was rightly
retired, but the *stride pin* under it was load-bearing, and it is restored as `CasterStrideSafe`
(shape, not count: exactly `DrawFloats/4` vec4s **and** no mat4, since a count alone passes the very
layout that broke).

### 4. The bake is one frame behind, and that is accepted

The shadow bracket runs ahead of the colour build, so the records it reads describe the *previous*
frame's world. Three things make that the right trade rather than a regression:

- It is not new. Creature poses already lagged a frame by design (#113), and the skin capture this
  replaces was itself fed from the previous colour pass.
- It is **coherent**. The mover stamp and the record content describe the same world state, so the
  predicate cannot miss a bake — the failure mode is a shadow that trails, never one that freezes.
- The magnitude is one frame of a rigid mover: ~0,07 world units at walking speed.

The fix is not a reordering hack here; it is moving the pass behind the build, which is #176's work.
Until then a swinging door is the case to watch, and it is on the A/B checklist for that reason.

**#119's standing constraint holds through the change.** The rigid arm of both depth stages is
`gl_Position = uVP * d.model * vec4(aPos, 1.0);` character-for-character under `invariant
gl_Position;` — `ShaderContract.DepthSafe` now pins both, which it could not do while the stage
multiplied by a matrix of its own.

### 5. The collect stands across frames (#328)

`Collect()` selecting out of Alloy's table was still a walk of every caster every frame — 13,5k
`TryCaster` + `TryRange` resolves to find ~200 movers, and that walk was the whole of the measured
5ms Bake. The list now **stands** until the world changes, and a frame in between walks the standing
list for its movers (`Refresh()`): the mover level of §2, the per-VDO dirty set, and the spheres.
Nothing in that walk derefs the engine. The `[casters]` line reports how many frames of the period
re-collected: a settled area shows a handful, and a high Bake beside a high count is a churning
edge (a fade in progress, a skip that flaps per frame), not a slow refresh.

"The world changed" is `AlloyRenderer.CasterGen`, bumped where the change happens rather than
discovered by re-walking: the record write publishes a per-slot **admission level** (`_castOk` —
a record landed for this tenant, on a non-fading tile, in a depth-writing class) and every flip of
it bumps the generation; so does a freed or re-tenanted slot, a kept tenant whose class or material
slots moved, a table wipe, and a population change. The admission level replaces the per-caster
tile-fade chase and tenancy compare `TryCaster` used to make, and it is one frame late like the
record it rides with (§4). A repack or a release drops the list outright; a record-epoch bump
(a retired texture copy) re-collects as insurance for the one cached input no slot fact names — the
cutout mask's texture id. Same rule as §2: the generation is compared, never consumed.

What is deliberately not re-read between edges: the index range (both edges that move it drop the
list), the mask texture (an animated cutout's silhouette follows at the epoch's ~1Hz, not per frame),
and a late-resolving texture copy (a masked caster stays solid until the next edge). A bone slot
assigned or reclaimed after the collect is caught by the refresh itself, which re-collects.

## The two rungs this ADR also covers

R1 above is built. R2 and R3 are decided here so they are not re-argued when they are built.

### R2 — static and dynamic compose in the receiver, by `min()`

The scaling cost is *casters-in-view per bake*: one creature walking through a room dirties that
cube, and the cube then re-renders every wall and crate inside its radius. The answer is a cached
static array plus a per-frame dynamic one — **composed in the receiver, `visibility = min(visStatic,
visDynamic)`, never in the texture.** Depth is a min-of-blockers operation, so it composes exactly.

#187 tried a composite and lost to its own per-view `glCopyImageSubData` — *a copy stalls; a bake
pipelines*. **There is no copy anywhere in this design**, which is precisely the mechanism #187
measured and rejected; that is why this is not a repeat.

- Static: baked on the existing #241/#282 predicate, with the mover term restricted to *static*
  casters. Zero bakes on a walking frame in a settled area.
- Dynamic: movers only — posed, dangly, vertex-dirty, matrix-moved — re-baked unconditionally,
  which is affordable because it is ~120 casters and not 56 000 draws.
- Sizing: the dynamic array is a smaller pool, not a mirror. Only pages containing a mover need a
  dynamic counterpart, at reduced resolution.
- Cascades re-fit, so a cascade's static half invalidates on the texel snap. The honest first
  version re-bakes it when the snapped centre moves and *measures how often that is*; the escalation
  if that is every frame is a scrolled (toroidal) cascade, which is a page invalidate and not a copy.
- Instrument: static and dynamic bakes reported separately. Static bakes tracking the view count
  means the split bought nothing.

**The sun half is built (#306).** The mechanism that makes a cascade's static half a cacheable thing
at all is the texel snap, taken one step further: `ShadowMath` now snaps the along-light component of
the fit centre too, so the whole fit matrix is quantised to the texel grid and the static key IS the
matrix — a sub-texel focus move reproduces it bit-identical, and "the snapped centre moved" needs no
second bookkeeping. The dynamic half is a same-size mirror array (`csm_dyn 0` deletes it), baked
movers-only every frame with the *same* matrix the static layer carries, so the receiver's uv/layer/ref
carry over and the compose is the cubes' own `min()` — one composition rule, not two.

**The degrade, and the toroidal verdict.** A cascade whose static half is stale essentially every
frame (>= 90% of a 120-frame window) degrades itself to the combined bake — today's behaviour, SetAll,
no dynamic half — and the period line marks the layer `C`. That is the honest answer to "the split
buys nothing while walking": correct shadows at today's cost, never two bakes for one.

The analytic expectation, recorded so the live number has something to disagree with: at run speed
(~4 u/frame-second, ~0,07 u/frame) the snapped centre moves nearly every frame on cascade 0 (1,5 cm
texels) and every other frame on cascade 3 (~12 cm), so *while moving* most layers should sit in `C`
and the split's win is the **standing** case — ambient movers (creatures, swinging doors) no longer
re-bake the whole visible world, which the plain #282 predicate could not avoid. The toroidal
(scrolled) cascade is therefore **deferred, not declined**: it is the escalation that would extend the
win to walking frames, and it is taken only if the live per-layer readout (`static per layer L0../L1..`
on the `[casters]` line, walking and standing measured separately against #301's numbers) shows the
walking cost matters after #305/#307 land. The measurement is the open item; the decision rule is not.

### R3 — one layered bake pass

Collapse the per-view loop: one compute pass emits layer-tagged commands for all stale views, the
vertex stage writes `gl_Layer` from its record, and the bake becomes a handful of
`glMultiDrawElementsIndirectCount` calls into a layered FBO.

`ARB_shader_viewport_layer_array` **is not core in GL 4.6**. It must be capability-checked at runtime
(`CasterGather.Capable()` is the pattern) with the per-view loop kept as the fallback. The
geometry-shader fallback is not worth taking. R3 is last because its win shrinks after R2, and it may
end up not being worth building at all.

**Built (#307), with the per-view path kept as both fallback and A/B** (`/shadow layered 0`; the
extension is probed once inside the bracket and its absence is said once). The shape as built:

- A stale view is *queued*, not submitted; a flush after the pass loop runs one layered pass per
  distinct target texture (at most four exist: sun static/dynamic, cube static/pool). Per target: the
  stale layers clear through the per-layer attach loop (a layered clear would wipe fresh layers too —
  that FBO churn is correctness and stays), then one layered attachment, one dispatch over
  (draws x views), and one `glMultiDrawElementsIndirectCount` per batch whose region is the batch's
  staged region scaled by the view count.
- Per-view uniforms are gone: a view is 28 floats in a view-table SSBO (fit matrix, focus/reach,
  dir/along, lodR/point/set/layer). The command's `baseInstance` packs the view-table index above the
  ownership slot (shift 18, pinned against `MaxOwned` by SelfCheck); the vertex stage unpacks both,
  routes with `gl_Layer`, and its clip expression is still #119's character for character — the fit
  matrix is read as a local spelled `uVP`.
- A view the predicate skips is never queued, so it contributes no commands — the predicate's saving
  survives the merge by construction.
- A failed flush publishes nothing for the frame (counts zeroed, blanket set): a one-frame blink,
  never five fresh layers and a stale one published as fresh.
- MDI calls/frame become (batches x targets x chunks) instead of (batches x views); the before/after
  against #301's numbers is the live A/B still owed, both paths exercised.

## Considered and rejected

**Virtual shadow maps.** Its two wins over R2 — per-page rather than per-view invalidation, and
resolution that follows the camera — target costs this content does not have: 32 short-range point
lights in rooms, not a 4-km open world, and no resolution complaint was ever made. It needs
`ARB_sparse_texture`, whose Mesa story is uneven, and it would replace the sampling side, which is
the healthy half. Escalation path if a resolution ceiling appears: make R2's static array's
allocation sparse, not restart.

**A slim caster-only record.** Rebuilds the split being deleted. What survives in `SowxCaster` is
bounded by "not in `SowxDraw` and genuinely view-varying", and each field is justified above.

**Keeping the old staging alive behind a dial.** A dual path *is* the second data plane. The A/B is
therefore across commits — `f0723d5` is the "before" build — and there is no runtime toggle to hunt
for. `/shadow rebake 0` and `/shadow gather 0` remain, and are unrelated to this.

## Consequences

- ~980 lines and one whole data plane deleted. `Enroll`, the caster table, the memo, the ghost sweep,
  `TryRange`'s table half, the VDO slot map, the VAO cache, `OnVdoRepack`'s table walk, the bone ring,
  `UploadBones` and `RecordOwnedInstance` all go.
- `FrameZones.ShadowEnroll` is vacuous — there is one enrolment now — and #128's frames of darkness
  between the two independent cadences go with it.
- `skincast` is retired: the posed set is Alloy's, so `skinning` is the one dial that decides it.
- Grass casting no shadow (#196) stops being an integration. Grass is an Alloy producer class; once
  the bake walks the ownership table it is one more class in the set.
- The first live runs found four bugs this record now covers: the missing bucket gate (fading
  roofs and their caps cast), the refuse-vs-NeverCull mistake, the half-applied stride (the
  "shadows float around" cause; its pin is `CasterStrideSafe`), and the epoch-vs-tenancy confusion
  (the once-a-second full dropout) — §2 and §3 carry the corrected shapes.
- Live-unverified at the time of writing. The A/B is an interior with local cubes and an exterior with
  the sun cascades, creature shadows included, plus a swinging door for §4.
