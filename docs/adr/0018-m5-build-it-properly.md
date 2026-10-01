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

> **Amendment (ownership-cleanup).** The measured risk came true and the escalation was taken, by a different
> route than the one named above. The epoch bumped on 66 of 900 frames — one per 11-20 — from VFX and creature
> churn, and each bump cost a 9.70 ms whole-area re-walk that the hitch ledger blamed for 47 of 63 hitch frames.
> So "`Scene::Sequence` triggers a re-walk" no longer holds as written: **`Gob::SetScene` @0x43e020 is hooked**
> and a membership move costs ONE gob's tree. It is the sole writer of the epoch — objdump finds exactly two
> references to that global in the whole image, the `addl $0x1` in that function and `ManageSceneBSP`'s read —
> so every bump arrives with the gob and both scenes in hand, which is strictly more than `ChildAdd`/`ChildRemove`
> would have given (they see the gob, not the pair, and the teardown path reaches `ChildRemove` through the
> vtable). Bumps touching neither world scene — the churning GUI 3D panel this section predicted — are answered
> with nothing.
>
> What keeps "re-reading, never inferring" true is the ACCOUNTING, not the hook: the hook counts the bumps it
> answered, the tick compares that count against the epoch's actual movement, and any shortfall stands the full
> walk up. Three more edges that announce nothing of their own are hooked on the same principle rather than left
> to the epoch's accident — `Gob::SetSubtreeEnabled` @0x43c670 (the `part+0x34` hide/show, CX-92),
> `Gob::Append`, and `Scene::SpawnTile`/`DeleteTile` for a tile, which names no gob and so still asks for the
> whole area. `Gob::SetLODModel`'s rotation is `GobLod`'s notice, which also names its gob.
>
> The whole-area walk survives as the AUDIT, every `alloy.walkaudit` frames (300 by default), and
> `[enum] audit misses=` counts what it finds that the deltas did not — it must read 0, and its split
> (`added` / `swept gob` / `swept tile`) names the edge if it ever does not. `alloy.walkdelta 0` restores the
> behaviour this section describes, which is the A/B the numbers above were measured on.

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

### Amendment (CX-103/104/105, 2026-09-30): the world matrices, the area index and the LOD choice are Alloy's

M5 put *transforms* on the engine's side and demoted the `Scene+0x208` bucket arrays to "a validator, not a
source". Tier 3 of CX-102 makes both literal.

**The matrix.** `Scene::AddPartsTreeToDrawBuckets` @0x460880 is the engine's only writer of a gob part's
`VertexPrimitive+0x18`, and it only reaches gobs in `gobbucket` — so a gob it never placed held the identity
its `VertexPrimitive` ctor left and drew at the area's (0,0) corner, and Alloy needed a *bounded chase* over
three engine-owned sets to know when the field could have moved. From CX-103 **Alloy composes the matrix**:
the `part+0x48` parent chain, `Gob::GetAdjustedScale`/`GetAdjustedPosition`/`GetAdjustedOrientation`, and each
attach-chain ancestor's visual-transform scale lerp — the similarity-transform form the engine's own
`Part::GetWorld` @0x437930 accumulates, proved off-device to equal the matrix-stack product
`Transform`/`Scale` build. The chase, the `gobbucket` visited set and the Fault 1 placement latch are gone.
`vp+0x18` is read in exactly one place: the `alloy.xformcheck` validator, which composes AND reads and counts
the disagreements.

**The index.** `AreaGrid` is Alloy's own tile-aligned grid of the STATIC slots — a part with no gob, whose
world box `ComputePartBBox` @0x561370 populated at tile spawn — rebuilt at each enrolment, which is the only
edge that changes which slot holds which part. The per-pass visible set is the grid's frustum query plus the
gob slots (movers by construction) plus what the grid could not bound, radix-sorted back into enrollment
order so the batch key survives. `alloy.bucketcheck` is where the bucket arrays now live: off by default,
because nothing in the frame reads them.

**The LOD.** `Gob::SetLODModel` @0x43e6a0 rotates a gob's three part-tree roots with no notice, which is why
the walk enumerated all three. Alloy hooks it, chooses the band with hysteresis vanilla has none of, and lets
the engine perform its own permutation — so the walk enumerates one root per gob and a swap is the edge it
never had.

The swap is **deferred**, and that is load-bearing rather than an optimisation. `RouteLive`'s active-root gate
refuses a part whose root is not `gob+0x90`, so the instant the engine rotates, every enrolled part of the old
root stops drawing — while the new root's parts are not in the part table until the re-walk and have no
ownership slot until the enrolment after it, which the `EnrollFloor` delays by up to ten frames. The first cut
therefore made a gob **invisible across its own LOD swap**. So Alloy registers the wanted level instead of
performing it, `SceneWalk` enumerates that gob's alternate roots while the registration stands, the enrolment
that seats them bypasses the floor, and the rotation is performed only once every root's parts are bound and
seated — at which point `gob+0x90` and the gate's verdict flip together with both sides' records live. A
candidate that never becomes drawable is cancelled with a cooldown and the gob keeps the LOD it is already
drawing. One root per gob in the steady state, three for the two frames a swap takes.

Two halves of one decision follow from that. **A part of an inactive LOD root vetoes nothing**
(`AlloyCensus.InactiveLodPart`): the engine's draw walks `gob+0x90` alone, so by the census's own rule — the
one the `StHidden` exemption states — a part it never reaches may refuse neither its gob (the
all-owned-or-none sweep would drop the whole creature for the pending window) nor its class (the class-wide
blended drop would flap every blended wall in the area). And **a swap is never made onto a tree Alloy cannot
own whole**: such a candidate's parts stay unseated, so readiness answers no until the timeout and the gob
keeps the LOD it is drawing. The exemption stops the WAIT from costing the creature; the readiness rule stops
the SWAP from costing it. Extra triangles at distance beats an invisible creature, and `lod-stuck=` plus the
census's `N inactive-LOD refusals absorbed` are the two numbers that say it is happening.

### Amendment (CX-86, 2026-09-30): the particle simulation is Alloy's

M3 left particle *movement* on the engine's side of the boundary, and #131/#148/#261 all restated
that: `Scene::DoEmitterBucket` @0x476f00 stayed a replayed vcall whose only job was to call
`PartEmitter::AnimateParticles` @0x4ac660, and Alloy's particle pass built its vertices out of the
`Particle` objects the engine had just stepped. CX-81 tier 1 says no engine draw runs in the world
frame, and that call is one: `AnimateParticles` walks into `RenderStandard`/`RenderLinked`/
`RenderMotionBlurred` and the funnels eat the draws. The replay cannot survive the tier, so the
boundary moves: **from CX-86, Alloy owns particle simulation.**

Alloy owns spawn (birthrate, the accumulator, p2p seeding), the integrator (velocity, gravity,
inherited-velocity decay, wind), lifetime and death, roll spin, bounce, the splat freeze, the
motion-blur previous-position lerp, the lightning fractal, the emitter enumeration and the
camera-distance sort. It holds that state in its own preallocated SoA pool, not in engine
`Particle` objects: the engine never allocates one on an Alloy frame.

**Bounce brings its own collision query.** Vanilla asks `Scene::HitCheck` @0x462960, which is an engine
call made for its side effect on a `CHitInfo` — exactly what this tier removes — and whose by-value
`Vector` arguments give it a different parameter list per OS. So Alloy has its own: `BounceWorld` builds a
per-tile uniform XY grid of world-space triangles at the tile's first use, from the same node faces and rest
vertex pool `Scene::AddFacesToBins` @0x460ef0 reads, and sweeps a particle's step against it with
Möller-Trumbore. The hot query makes no engine call at all; `SceneBSPGetTileData` is a build-time read, and
the set is dropped at the area edge like every other owned table.

It covers **up-facing tile faces** — ground, ramps, steps — and not walls, placeables or creatures. That is
a decision: bounce exists in the content for sparks and debris meeting the floor, and up-facing faces are a
small fraction of a tile's geometry, so the alternative is a per-tile set an order of magnitude larger for
an effect nobody authored. A particle whose step only crosses a wall passes through it, which is what an
emitter declaring `bounce 0` already does.

**One exception stands.** A **chunk** emitter's particles are not particles: each one holds a
`CAurObject` it creates through `NewCAurObject`, the emitter's own draw is `RenderNothing` @0x4ad200 and
what the player sees is an ordinary gob. Those are scene objects, and scene objects are out of this ADR's
scope, so Alloy calls `AnimateParticles` and `RenderNothing` on chunk emitters directly — neither of which
draws.

That exception is also the **carve-out on "the simulation allocates nothing per frame"**, and it is worth
stating rather than leaving to be discovered. Alloy's own step allocates nothing: the pool and the collision
arenas are sized at load edges and bump-allocated thereafter. But a chunk emitter's step is still
`AnimateParticles`, which does `operator new(0x80)` per `ChunkyParticle` on the frames it births — and each
of those constructors also calls `NewCAurObject`. So on a frame a chunk emitter births, the engine
allocates. Closing that means owning `CAurObject` lifetime, which is exactly what tiers 3 and 4 are for; the
`chunk=` counter on the `[emitsim]` line says how many such emitters ran.

The engine keeps everything that decides an emitter *exists* and what it is:

- **emitter lifetime** — `PartEmitter` construction and destruction through the part tree, and
  `PartEmitter::Initialize` @0x4aaaa0, which parses the MDL node's render type, blend class and
  mover selection;
- **the per-frame parameter animation** — `MdlNodeEmitter::InternalControl` @0x4991d0 writes
  birthrate, colours, sizes, lifespan and the rest from the model's controllers;
- **VFX construction, attachment and removal** — the `CNWC*` layer, and the detonation events
  `Gob::DoEvent` raises, which Alloy reads and *consumes* (the byte at `PartEmitter+0x100`) rather
  than redefining;
- **the p2p target attachment** — `SetEmitterTarget` through the Gob's behaviour slot;
- **the wind clock** (`WindManager::updateTimer`) and the collision geometry bounce tests against
  (`Scene` vtable +0x60 / `CHitInfo`);
- **the emitter bucket itself** — `ManageSceneBSP` decides which emitters are in view.

One engine field Alloy must keep writing, because game-layer code reads it and nothing else will once
the engine's walk is gone: the detonate byte at `PartEmitter+0x100`, which has to be cleared or an
explosion re-fires every frame. The bounding radius `PartOutside` culls on was the other candidate and
turned out not to be one — `PartEmitter::CalculateRadius` @0x4ac350 derives it from the emitter's declared
parameters, not from live particles, and its only callers are `MdlNodeEmitter::InternalControl` and
`WindManager::SetGlobalWind`, both outside the render pass. Culling keeps working untouched.

This is narrower than it sounds. It does **not** reopen ADR 0016's or 0018's exclusion of the scene
database: Alloy still does not parse MDL, build part trees, animate hierarchies or own `Gob`
lifetime. A particle is not a scene object — it has no node, no children, no hit test and no
identity outside its emitter — which is exactly why its simulation detaches cleanly and the part
tree does not.

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
