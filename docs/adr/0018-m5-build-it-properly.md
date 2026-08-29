# 0018 — M5: build it properly

Status: accepted (#255 grilling with xtul, 2026-08-29). **Supersedes ADR 0009's milestone list**;
0009 remains the record of why a wholesale renderer at all. Amends ADR 0010. Absorbs ADR 0017's
remaining ladder steps.

## Context

M0–M4 were a migration. Each took another piece of the frame from the engine, and every one of them
was shaped by the thing it was replacing: Alloy drew inside the engine's frame, so it learned what
existed by watching the engine draw, inherited the engine's cull, copied the engine's GL objects,
and reproduced the engine's pass order so the two renderers would not seam. M4 (#203, ADR 0016)
finished the migration — Alloy drives the frame — and in doing so removed the constraint every one
of those shapes was justified against.

Three research tickets surveyed what is left.

**#252, the modernization census** (`docs/research/alloy-modernization-census.md`, 18 entries) found
the premise holds but not where it was aimed. The *technique* is already modern — clustered forward+
with no light ceiling, cascaded shadow maps plus a cube array, persistent per-draw records, GPU
grass, one framebuffer copy where the engine takes two. What is vanilla-shaped is the **inputs**.
E1 is the keystone: `RspShim` still walks vanilla's bucket order with both engine draw funnels
swallowed, purely so the capture hooks fire — Alloy discovers a part by watching the engine try to
draw it. E2 (visibility is enrolment cadence, not a visibility test), E3 (135 MB of fixed CPU arrays
rebuilt wholesale every 60 frames), E4 (the ADR 0006 batcher disarmed by construction while its
state machine still runs per part per frame) and E5 (identity is a borrowed pointer, liveness an
inference) are all consequences of E1.

**#253, scene state and the part lifecycle** (`docs/research/scene-state-and-part-lifecycle.md`)
asked whether Alloy could maintain a registry fed by birth/death/rebind notices and answered **no**:
`Gob::SetLODModel` @0x43e6a0 rotates a gob's whole active part subtree every frame, per visible gob,
with no construction, no destruction and no epoch bump; three edges have no notice of any kind. Its
finding is that the authoritative scene state is directly readable — `Scene+0x130` holds every gob,
`Scene+0x188` the tile BSP root, `Gob+0x90` the root `Part*` — and that discovery, classification
and culling all finish *before* `Scene::RenderSinglePass` is dispatched.

**#254, the resource edge** (`docs/research/resource-residency-edge.md`) located the decode window
(`CAurTexture::glImage` @0x48cb60, host pixels freed immediately after upload) and found geometry is
the larger prize with no window at all: `RequestPool` @0x4d1ec0 keeps a permanent host shadow, so
world geometry exists in three copies and every dirty pool uploads twice.

## Decision

**M5 is the wholesale rework of how Alloy operates**, on 2026 practice, unburdened by the vanilla
renderer. It is **two strands, sequenced: data flow → submission.**

### The bar

> **Alloy's frame is built from Alloy's own state — the scene it enumerated, the resources it
> uploaded, and the visibility it computed. Nothing in it is produced by observing the engine
> render.**

**Its instrument is one artifact: `RspShim` no longer calls `_drawBucket`, and `WalkOrder` is
deleted.** The walk exists only for capture, so if it is gone, discovery is Alloy's — and it cannot
go until enumeration, residency and visibility are all Alloy's. Secondary assertions: no hooks
remain on `PartTriMesh::Draw` / `PartSkin::Draw`, and no `OwnedTextures` / `OwnedBuffers` entry is
keyed on an engine GL name.

**No measurement gate**, as with M4. M5 is justified by architecture and by what it deletes. #238
already showed the frame is CPU-bound, so moving work to the GPU pays off by *deleting CPU work*,
not by offloading it. Trace numbers are welcome as evidence and are never why a ticket exists.

### The two strands

**Strand 1 — data flow.** Exits when every Alloy record originates from Alloy's own enumeration of
the world and its own upload of decoded data; per-frame CPU cost is O(what changed); no engine GL
object is the source of truth for anything Alloy draws; and the 60-frame enrolment cadence, the
sighting window and the fixed worst-case tables are all gone.

**Strand 2 — submission.** Exits when selection, culling, LOD, batching, ordering and draw-record
generation all happen on the GPU over those persistent records, and `WalkOrder` is deleted.

**The dependency is hard, not a preference.** #253 §7.1 notes a compute cull's inputs are all
readable today, so strand 2 *could* be built first. That is a trap: a cull over records that are
wholesale-rebuilt every 60 frames is a cull of a snapshot, and it inherits E2's defect in new
clothes. Strand 1 exits, then strand 2 starts.

**Strand 2's exit is the bar**, which is what makes these one milestone rather than two. Strand 1
has no user-visible result on its own; shipping it and stopping would leave Alloy strictly more
complex than today.

### Enumerate the world, not the visible set

Alloy's persistent records cover **every part in the area**, not the visible slice. The rejected
alternative was reading the engine's finished draw buckets (`Scene+0x208`, 48 flat `Part*` arrays):
cheap, and wrong, because that set is the *visible, classified* one — a 90° camera turn replaces
most of it, so the delta machinery would be built against a churning population and then rebuilt
when strand 2's cull makes the population stable. That is the "building it twice" the strand
ordering exists to prevent. The bucket arrays demote to a bring-up validator.

Records are maintained by **re-reading, never by inferring**: `Scene::Sequence` @0x18aa1b0 (a global
membership epoch the engine bumps from `Gob::SetScene`) triggers a re-walk, `Part::~Part` (#145) is
the death notice, and transforms are per-record field reads. This is what makes `Gob::SetLODModel`'s
subtree rotation a field read rather than the registry-killer #253 found it to be.

**Mid-area spawn, move and destroy are a first-class requirement, not an area-load concern** —
module furniture-placing systems and destructible objects are the acceptance case. The epoch is a
single `int` read and is strictly cheaper than any hash of the object list, which would require
walking the list to compute. The measured risk is *bump frequency*: the epoch is global, so a
churning GUI 3D panel bumps it too. If it bumps per-frame, the escalation is
`Scene::ChildAdd` @0x461d50 / `Scene::ChildRemove` @0x462cc0 for exact deltas — not a hash.
`countpart` @0x18da6b8 validates either.

### Strand 1's ownership order

The slicing that makes the milestone executable, the way #107 §3 ordered M3's classes:

1. **Scene enumeration** — the keystone. Retires E1, E2's sighting window, E3's cold rebuild, E5's
   liveness inference.
2. **Frame constants read directly** — camera, lights, fog at frame start; no capture, no
   largest-viewport contest. Retires E7, E9's per-frame queries, E12's feed. Independent of 1.
3. **Delete what has no consumer** — the ADR 0006 batcher, its group registry, matrix probation,
   the dedup registry (E4, E17). `ShadowService` (E14) is untouched: it belongs to the cold branch.
4. **Residency: textures** — attach at the decode window; Alloy uploads into Alloy-minted handles.
   This is #227's structural cause.
5. **Residency: geometry** — Alloy uploads from the engine's host shadow, suppressing the engine's
   world-pool upload. Kills the three-copies problem and the `UnlockPool` ordering hazard.
6. **The producer classes** — sky, beams, particles: own the source data; delete capture-and-replay,
   the colour-masked ghost draw and the one-frame lag (E11, E16's feed). #136 folds in here.
7. **Skinning as data** — bone matrices read as engine state, not produced by re-entering
   `PartSkin::Draw` with the funnel swallowed (E6).
8. **The refusal surface collapses** — vanilla goes restart-only, refusals become a boot decision
   plus hard failure, the per-part hand-back vocabulary is deleted (E18). Strand 1's exit step.

### The boundary widens

M3's rule was *the engine simulates, we draw*. M5's is **the engine reads files and decodes them,
Alloy owns everything after that** — upload format, mip policy, VRAM budget and eviction, atlases
and arrays. Container parsing stays with the engine: Alloy does not parse KEY/BIF/HAK/NWSync or
MDL/MDB/DDS/TGA/PLT/MTR. It owns residency.

### Builder intent is a standing constraint

> A light reaches what the builder made it reach, and what the builder left dark stays dark.

Range and the lit/unlit boundary are a **content contract** and are preserved. Everything about how
a lit surface responds — colour, specular, roughness, bloom, tessellation, shadow softness — is
Alloy's and is free to differ. This is a gameplay constraint that happens to live in the renderer:
builders leave areas unlit deliberately, and a decor light is not meant to extend a sightline. It
admits a physically-based falloff with a range-matched cutoff, which the current bit-match to the
engine's curve does not; it rules out a "more correct" curve that quietly lights what was meant to
be dark. It outlives M5 and binds every future lighting and visibility change. See CONTEXT.md.

This is what "vanilla visual parity was never the goal" does and does not mean. Characters looking
nothing like the original, because the pipeline targets higher-fidelity models in higher numbers;
armour reading more reflective because bloom is not clamped; stone reading bumpier because
tessellation exists — all fine. Re-lighting an area the builder composed is not.

### There is no strand 3

The 2026-08-29 grilling proposed *shading* as a third strand. The census overturned it: the shading
technique was chosen against vanilla, not from it, and what remains splits in two. The **dissolving
items** — `SkyComposite`, whose only job is matching the engine's display-space sky blend; the
tonemap midtone clamp, which assumes engine geometry shares the framebuffer; the hard-coded 0.2
alpha discard and vanilla's pass numbering — all stop being necessary when their strand-1 cause is
fixed, so they are **exit criteria of the strands that dissolve them**, not work of their own. The
**technique upgrades** (#136, #143, #147, #152, #196, and the shadow filter tier) have no dependency
on each other and stay ordinary backlog issues that M5 unblocks.

"The image by 2026 techniques" has no closing sentence, and a bar needs one.

## Consequences

- **ADR 0009's milestone list is superseded.** 0009 keeps a supersession header and remains the
  record of why a wholesale renderer at all; 0018 does not restate the argument.
- **ADR 0010 is amended, not superseded.** The vanilla branch ships permanently. The amendment is
  that it becomes **restart-only** rather than a live toggle, so the two resource worlds are never
  warm at once — which is what makes exclusive residency ownership affordable, and what lets the
  per-frame refusal machinery collapse into a boot decision (strand 1 step 8).
- **ADR 0017's ladder is absorbed.** Step 3 (#243, own visibility) is strand 2's first ticket.
  Step 4 (stop paying for the engine's traversal, never filed) is strand 1 step 1 — and #253 showed
  it is a smaller change than the ADR assumed, needing no new hook.
- **#151 and #153 close as superseded** by strand 2: both describe work M5 does under a different
  name.
- **Pushed out of M5, filed as ordinary issues so they are not forgotten**: streaming, BCn transcode
  of the TGA/raster/PLT path, `numX*numY` de-atlasing into array textures, and the VRAM arena. All
  four are things you do *to* a residency layer once you have one and have measured it; each carries
  #254's named measurement so none is built blind.
- **The four open bugs (#227, #247, #248, #250) are not absorbed and are not M5 tickets.** They are
  a census *input*: a rework that does not make them impossible has not answered them. #227 is
  strand 1 step 4/5's, #247 and #248 are step 1's, #250 is step 1's for the camera-patterned half
  and is an unexplained gap for the grey-noise half.
- **Some of what M5 deletes has no live consumer at all** — the batcher's state machine, the dedup
  registry, five hooks on functions the Alloy path never calls. Deletion is a deliverable here, not
  a side effect.
