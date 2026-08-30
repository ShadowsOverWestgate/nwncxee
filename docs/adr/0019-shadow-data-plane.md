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

### R3 — one layered bake pass

Collapse the per-view loop: one compute pass emits layer-tagged commands for all stale views, the
vertex stage writes `gl_Layer` from its record, and the bake becomes a handful of
`glMultiDrawElementsIndirectCount` calls into a layered FBO.

`ARB_shader_viewport_layer_array` **is not core in GL 4.6**. It must be capability-checked at runtime
(`CasterGather.Capable()` is the pattern) with the per-view loop kept as the fallback. The
geometry-shader fallback is not worth taking. R3 is last because its win shrinks after R2, and it may
end up not being worth building at all.

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
