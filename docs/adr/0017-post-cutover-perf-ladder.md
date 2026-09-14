# 0017 — The post-cutover perf ladder

Status: accepted (#238, 2026-08-29). Follows ADR 0016, which owns the frame and explicitly declines
to gate on perf. This ADR is what perf work is ordered by now that the frame is ours.

**Its remaining steps are absorbed by ADR 0018 (#255, 2026-08-29).** Step 3 (own visibility, #243)
is M5 strand 2's first ticket; step 4 (stop paying for the engine's bucket traversal, never filed)
is M5 strand 1 step 1 — and #253 found it is a *smaller* change than this ADR assumed, needing no
new hook, because discovery and classification finish before `RenderSinglePass` is dispatched.
Step 5's de-duplication machinery is deleted by M5 rather than completed: its useful half becomes a
property of the residency system. Steps 0, 1, 2 and 6 shipped and stand.

## Context

M4 closed the cutover: Alloy draws the 3D world from its own resources, in its own loop, into its
own target. ADR 0016 and map #68 both deferred every perf claim until that was true, on the grounds
that a vanilla comparison against an engine-owned frame measures the engine. It is true now, and the
first measurement taken after it is a regression:

    ownframe=0   avg 32.7-37.1ms
    ownframe=1   avg 32.6ms ... 150.5ms, max 1183ms

Not a flat cost — fine, and then it is 150ms. The instrumented brackets accounted for roughly 13ms
of that: ~7ms of GPU colour passes and ~6ms reported as "in the shim".

**The 6ms was not the shim.** `DrawOwned` runs inside the `Scene::FadeDoors` anchor, which runs
inside `RspShim.Run`, so the shim's published per-frame figure contained the entire Alloy CPU frame.
Corrected in #238; the [rspshim] number is exclusive now. What that revision means is that the
accounted fraction of a 150ms frame was *smaller* than the ticket assumed, not larger.

A frame that is ~90% uninstrumented is a measurement problem before it is an optimisation problem.
Every technique below would otherwise be chosen blind.

## Decision

### Step 0 — instrument the frame end to end, and re-measure. Done (#238).

`FrameZones` (managed/Framework) splits the frame into six non-overlapping top-level spans —
`Enroll`, `Shadow`, `Shim`, `Alloy`, `Ui`, `Present` — plus nested breakdowns inside them, and
reports the residual: present-to-present time minus everything claimed. A large residual is itself
a finding, and names engine simulation/input/2D UI as the cost rather than leaving it unattributed.

Two design points are load-bearing:

- **Nested brackets subtract.** A parent passes its child's ticks so it reports its own work. That
  is what the shim reading above got wrong, and the instrument must not be able to repeat it.
- **The period line reports the worst frame's own split, not just the average.** One 150ms frame in
  900 moves an average by 0.16ms. The spike is the thing being hunted, so it is snapshotted when it
  happens. This is what makes AC2 ("the spike cause identified, not inferred") answerable.

`Present` is the discriminator the rest of the list cannot supply: time spent there is GPU- or
vsync-bound and belongs to a different ladder than anything below.

**Nothing further on this list is authorised before that trace is retaken live.** The ordering
below is a hypothesis with its reasoning shown, not a verdict. *(It was retaken the same day, and it
overturned the ordering — see "The trace, retaken" below.)*

### Step 1 — bind `glMultiDrawElementsIndirectCount`. Done (#238).

Everything a GPU-driven path needs was already bound in `Gl.cs`: compute, SSBOs, `glBufferStorage`
with persistent coherent mapping (`GpuRing`), `glMultiDrawElementsIndirect`, bindless (#153). This
one entry point was missing, and without it a GPU-culled draw list has to bring its count back to
the CPU — a stall on the very frame the cull exists to make cheaper. Bound ahead of its first caller
because binding is free and being blocked on it later is not.

### Step 2 — make per-frame CPU work O(what changed), not O(what is owned).

**Submission is not the problem, and a ticket framed as "reduce draw calls" would optimise a
non-problem.** Measured: 2897 draws in 27 batches — 27 GL calls for the whole world. The cost is in
*building* those records. `BuildBatches` revalidates every owned part every frame (a hash probe, a
handful of pointer chases, a live texture re-fetch, a slot loop, each), and `UploadDraws` then
re-uploads the entire draw-record SSBO and the entire bindless handle table wholesale.

Per-part records are stable for the overwhelming majority of parts: tiles do not move, placeables do
not move. The change is to make the record buffer persistent GPU state — written when a part enrols
and again only when it mutates — so the per-frame CPU write is movers only.

**This is a design job, not a rename, and the reason is a split that has to be got right.** The
record today mixes genuinely stable fields with per-frame facts:

| stable at enrolment | per-frame, by construction |
|---|---|
| model matrix (static parts) | model matrix (creatures, doors, dangly) |
| material params, PBR slot mask | diffuse texture id — animated textures cycle **engine** names per frame, and `CAurTexture*` is borrowed (ADR 0015) |
| VDO / index range | tile fade factor, gob alpha/colour/emissive modulation |
| class, blend pair, feature | keyhole dissolve, `SightedRecently` |

A design that writes the whole record once would freeze an animated texture and stick a fading tile
at full opacity. The split is the work.

Needs no new GL capability, and #236 is what made it correct: before the widened sighting window,
ownership churned on every camera turn and "stable records" was false.

### Step 3 — own visibility.

Alloy has no culling of its own. There is no frustum test anywhere in the colour path; its only
visibility input is `SightedRecently` — "did the engine's bucket walk touch this last frame". That
is why #236 existed at all, and why its fix is a *window* rather than an answer.

Replacement: frustum cull in compute over step 2's persistent record buffer, writing indirect
commands and a count; occlusion via a hi-Z pyramid built from #119's depth prepass, which already
produces the depth; `glMultiDrawElementsIndirectCount` consuming the count without a readback.

Pop-in stops being a tuned number and becomes structurally impossible: there is no enrolment latency
in the visibility path because visibility is no longer an enrolment property.

**Trap (#119's standing constraint).** Any change to where the vertex stage sources its matrix must
still compute `gl_Position = uVP * d.model * vec4(aPos, 1.0);` character-for-character under
`invariant gl_Position;`, or the colour pass loses fragments to LEQUAL as silent holes.

#### What shipped (#243, `managed/Alloy/Pass/CullPass.cs`), and the decisions it had to make

**Same-frame hi-Z, not reprojected.** The ticket asked for the choice to be written down rather than
discovered live. The pyramid is built *inside* the frame that consumes it, by splitting the cull
across the two passes that already exist: dispatch A tests the frustum only and feeds the depth
prepass; the prepass depth is reduced to a max-pyramid; dispatch B tests frustum **and** pyramid and
feeds the colour pass. Reprojection was rejected because it buys nothing here — the prepass depth is
already paid for (#119), and a pyramid one frame stale is exactly the silhouette popping the ticket
names as the classic artefact. Everything the prepass leaves out (cutouts, dropped discarding
classes, all engine-drawn geometry) makes the pyramid *shallower*, and a shallower pyramid culls
*less*. Every approximation in this pass errs toward drawing.

**The vertex stages did not move**, so #119's constraint is a no-op here rather than a risk taken.
The cull compacts each batch into *its own* source region, so `_gStart[g]` stays both the indirect
byte offset and `uDrawBase`; the compacted per-draw index array binds at the same
`BindDrawIdx`, and `uDraw[uDrawIdx[uDrawBase + gl_DrawID]]` resolves exactly as before. No
`.shd` override needs re-emitting.

**The opaque range only.** Compaction is an `atomicAdd`, so order *inside* a batch is not the
staging order. Opaque draws are order-independent; the blended and water ranges are sorted
back-to-front and stay on the `BaseVertex` path untouched. Cutout batches blend and *do* ride the
indirect path — overlapping graded edges can therefore composite differently frame to frame. Named,
not hidden; `/draw cull 0` is the A/B.

**Bounds are a separate buffer, not a wider record.** The 176-byte `CxDraw` record is pinned by
`ShaderContract` and copied into every override, so the world sphere (centre + radius per ownership
slot) rides its own SSBO, written on the same edge the record is. Three classes are exempted with an
infinite radius rather than bounded: posed skinmeshes, dangly meshes, and displacing features — each
moves vertices out from under the node matrix the sphere is derived from. Unknown bounds mean
VISIBLE, always: the failure mode of a cull is invisible geometry.

**The CPU gate is gone, and that costs before it pays.** `PartTable.InBucketsRecently` no longer
gates staging while the cull is armed, which is AC1 — but it also means the staging walk now
revalidates every owned part rather than the ~56% the engine's walk had sighted. The cull's win is
*GPU* work removed on a frame #238 measured as CPU-bound. That is the honest reading, and it is why
this is step 3 and not the last one: the CPU saving is step 4's, when the walk itself retires.

**The count never comes back on the path.** `glMultiDrawElementsIndirectCount` reads it from the
parameter buffer. The culled-vs-drawn figure in the period line is a `glCopyBufferSubData` into a
three-deep ring, read three frames behind the dispatch that produced it.

### Step 4 — stop paying for the engine's traversal.

`RspShim.Run` splits cleanly in two. The scalar bookkeeping is cheap and stays: `ControlLightsFade`,
`DoFog`, `FadeDoors`, `DoBinBucket`, `FocusLighting`, `DoEmitterBucket`, `RenderLensFlares`. The
bucket traversal is expensive and exists *only* for capture — a vtable call and a `Find()` probe per
part so that Alloy learns a part exists.

After steps 2 and 3 the walk is needed for neither capture nor visibility. What remains is **part
discovery**, and that is a research item, not a guess: `Part::~Part` (#145) is already the death
notice; the birth notice is missing, and the honest candidates are a `Part` constructor hook plus a
direct walk of the tile BSP (`Scene+0x188`) and the gob lists.

Largest single number on the table, and last for a reason: only safe to remove once nothing depends
on it.

### Step 5 — instancing across the tileset.

    7047 parts owned on 6 VDOs / owned geom 15 bufs 42MiB

Six vertex-data objects for the whole scene: the geometry is already shared and consolidated, and
what repeats across an NWN area is the transform, not the mesh. This collapses draw *records* —
step 2's cost — rather than draw calls, which are the non-problem. Cheap because the data is already
in the right shape.

**Measured (#244, 2026-08-29), and the premise does not hold.** The census keys every owned part by
(VDO, matrix-class, item) and measures the equal runs. Two independent live areas, one of them #238's
heavy exterior:

    mesh sharing 1,00x (10798 meshes, max 1 instances, 0 parts share a mesh of which 0 object-space)
    mesh sharing 1,00x (10664 meshes, max 1 instances, 0 parts share a mesh of which 0 object-space)

Distinct meshes equals the part count exactly, and the largest instance count on any mesh is one. Six
VDOs is **buffer consolidation, not mesh sharing**: the engine hands each placement its own item range in
a shared buffer. Two numbers on the same line corroborate it independently — `shared-material=10 exact`
out of ~10 800 parts, and 10 816 textures for 10 798 parts. Geometry, material and texture are all
per-placement.

So "what repeats across an NWN area is the transform, not the mesh" is false **as the engine stores it** --
no two parts share a buffer range. That is not the same as "nothing repeats", and reading it that way was
the first of several wrong turns on this step. What actually holds is stronger and more useful: **the engine
bakes each placement into its own copy of the vertices**, so repetition is real and is duplicated rather
than shared. The measurement that settles it is the content one below, not this one.

**The shape tier, measured by vertex content (#244, 2026-08-29).** The range tier above says only that no
two parts share a buffer range. It cannot see repeated geometry the engine *copied* per placement, which is
the case that matters on real content -- so a second key was added, and then a content hash to settle it.

    signature only:   shape repeats 2,47x (1237 shapes, max 180)
    vertex content:   shape repeats 1,00x (1152 shapes, max 3, 9 parts on a repeated shape,
                      all 9 placeable/creature and object-space; 94 gobs and 94 object-space
                      of 1157 censused parts; 1157 of 1157 hashed)

**The signature over-counted by more than two to one, and the content hash is what caught it.** A key of
(primitive, texture, index count) cannot separate fifty copies of one model from fifty terrain patches that
share a topology and a texture while their vertex positions differ, and on tileset content the second is
what it was actually finding -- one signature group held 262 of 386 "repeats" in a 1199-part area. Coverage
is total (every censused part hashed), so the collapse is not an artifact of unhashed parts grouping on a
zero hash.

Three structural facts fall out of it, and they are worth more than the number:

- **Gob parts and object-space parts are the same set, exactly, in every run** -- 32/32, 76/76, 94/94,
  109/109, 112/112, 123/123. Placeables and creatures carry a model matrix; the tileset does not. So roughly
  92% of an area's parts are **world-space baked**: their placement lives in their vertices, and no
  de-duplication or instancing can collapse them without un-baking the transform first. That is a far
  stronger statement than "they do not share a range".
- **Every genuinely repeated part is a placeable or creature** -- 5 of 5, 9 of 9, 10 of 10, 11 of 11. The
  intuition that repetition lives in the decor is right about the mechanism.
- **The magnitude is ~1%**: 5-11 parts out of 1150-1230, at 3 to 5 copies each, in areas where gobs are only
  8% of the population. These are not decor-heavy areas, and a modern one would shift the share -- but see
  below for why that does not revive the rung.

**The rung's PROMISE still does not follow, even though its premise holds.** Step 5 was budgeted as
"collapse draw *records*, cutting the 3,7ms Build". A record is per-placement by construction -- its own
transform, alpha, fade factor and texture handles -- and the validation that builds it is per-placement too.
De-duplication cuts geometry, not records: it does not reduce record count, Build, draw count or caster
count, and draw calls were already a non-problem at 27 GL calls. What it can buy is vertex-fetch locality
and (with a packed store) VRAM. That distinction is the single most important thing on this step and it was
restated at every stage, because the number that motivates the work is not the number the ladder wanted.

### What was measured, after three instruments were wrong

The final, proven figure is **2,5x on the complex area** -- ~9 500 parts backed by ~3 800 meshes, every
membership verified vertex, UV and normal against the representative it would share a buffer with. Getting
there took four corrections, and each one moved the answer by more than the last:

| instrument | said | why it was wrong |
|---|---|---|
| range key (item identity) | 1,00x | the engine copies; identity was never going to show sharing |
| shape signature (texture + index count) | 2,47x | collided on tileset topology -- one group held 262 of 386 "repeats" |
| exact position hash | 1,00x | baked placement makes every copy's bytes differ; saw only the 9% object-space slice |
| rigid hash, one canonical per run | 26% verified | absolute epsilon below float32's ULP at world scale, and a prefix-screened run holds several shapes |

The pattern is worth keeping: **every one of those was found by disbelieving a result, not by a failure.**
A census that reports a number is not the same as a census that reports the right number, and on this step
the difference was consistently a factor of two or more.

### What shipped, and what did not

Shipped behind dials, both default off: the census and its content/rigid hashes (`/draw meshhash`), the
verifier and the de-duplication registry, and the colour-path draw that sources geometry from a
representative under a verified placement (`/draw dedup`). The vertex stage is untouched -- the placement
rides in `d.model`, so #119's `gl_Position = uVP * d.model * vec4(aPos, 1.0)` holds without a shader edit
and the depth prepass agrees by construction.

Not done, and deliberately: the caster pass still submits each part's own range (correct, just not shared);
matrix-class parts are registered but not followed; there is no packed canonical store, so no VRAM is
saved; and **the registry is rebuilt per enrollment and only while the diagnostic is armed, so this cannot
ship enabled until that cost is measured** (`verify Xms` on the `[alloy]` line) or the registry is cached
across enrollments by part identity. No before/after frame-time claim is made: the A/B was never run under
controlled conditions, and the frame is CPU-bound while this is a GPU-side win.

**What the census does and does not see, stated so the number is not over-read.** There are three tiers of
mesh sharing, and they are not equally cheap to detect:

1. two parts on the same item entry — the same index range by identity;
2. two parts on *different* item entries that name the same (offset, count) — the same range, aliased;
3. two parts on different ranges holding equal vertices — the same geometry, duplicated.

The census keys on the **index range** rather than on the item index, so it covers tiers 1 and 2 for one
dereference per part at enrollment cadence — the same element-pool read the staging walk already does,
guarded the same way, with parts whose range will not resolve left out and counted rather than silently
dropped. Tier 3 is content comparison, is genuinely expensive, and is not attempted; the nearest ticket
that would collapse records here is a geometry de-duplication pass, which is not proposed.

*The two live numbers above were taken with an earlier key that covered tier 1 only.* Tier 2 is
instrumented and unmeasured. The corroborating evidence argues it is empty — aliased ranges would very
likely alias materials too, and `shared-material` reads 10 exact out of ~10 800 — but that is an argument,
which is the thing this ADR is trying not to close on. Re-run and confirm before quoting the figure as
covering both tiers.

The census stays in the renderer as the standing instrument: one sorted pass over the ownership table per
enrollment, reported on the `[alloy]` line, so a tileset or a content set where this stops being true says
so without anyone re-deriving the question. **It answers "how many parts share a mesh", not the ticket's
second half, "how many distinct transforms each mesh carries"** — with a maximum of one instance per mesh
that half is vacuous (one instance cannot carry two transforms), and folding a transform hash into the key
is work worth doing on the first content where sharing is not 1,00x, not before.

### Step 6 — the shadow pass, which may be step 2 by measurement.

#151 established that frame time tracked shadow re-bake count, and the shadow pass is not inside the
shim bracket. **If the step 0 trace finds the unaccounted majority in `CasterPass`, this ladder is
wrong and #151 goes first.** The ordering above is stated as a hypothesis precisely so that the
measurement can overturn it. `Shadow` and `ShadowEnroll` are separate zones for that reason — and
because `CasterPass.Enroll` and `AlloyRenderer.Enroll` share `EnrollEvery`, so both scans can land
on the same frame, which is a compound-spike shape a single total would hide.

## The trace, retaken (2026-08-29). The ladder is overturned by its own escape hatch.

    [zones] 38,5ms = Enroll:0,2 Shadow:19,6 Shim:2,0 Alloy:5,7 Ui:0,2 Present:0,9 other:9,8
                     (ShadowEnroll:0,1 Build:3,7 Upload:0,2 Issue:0,1 Pose:0,1 Own:0,0 Probe:0,0)

**`Shadow` is 51% of the frame.** Step 6's condition — "if the trace finds the unaccounted majority in
`CasterPass`, this ladder is wrong and #151 goes first" — is met. It goes first.

Everything else the trace settles, in one pass:

- **Not GPU-bound at the present.** `Present:0,9`. The frame is CPU-bound, and the ~24ms GPU shadow
  average overlaps it rather than gating it.
- **Step 2's target is 3,7ms of 38,5.** `Build` is real but it is not the frame. Persistent records
  are still correct work; they are not the first work.
- **The shim is 2,25ms, not 6.** The subtraction landed. The pre-#238 figure was mostly the Alloy
  frame.
- **`other:9,8`** — a quarter of the frame is engine simulation, input and 2D UI, which no owned
  bracket can reach and no technique on this list addresses.

### Per-area, from the same session (AC4)

| profile | frame | `Shadow` | `Alloy` | `other` | parts | lights | creature casters | water copies | sun |
|---|---|---|---|---|---|---|---|---|---|
| interior | 14,2ms (70fps) | 5,7 | 1,5 | 5,1 | 6 319 | 93 | 19,8 | 298 | idle |
| exterior / water | 13,7ms (73fps) | 6,9 | 2,4 | 3,0 | 6 044 | 93 | 20,5 | 1 198 | 4/4 |
| exterior / heavy | 38,5ms (26fps) | 19,6 | 5,7 | 9,8 | 12 053 | 260 | 122,3 | 509 | 4/4 |

Water is not a cost — the water-heavy period is the *fastest* of the three, and the GPU split puts
water at 0,001ms; #129's copy gate is working. Interior is not cheaper than exterior at equal light
count, so the sun cascades are not the exterior penalty. The heavy area is the whole story, and
`Shadow` alone is 13,9ms of its 24,3ms increase. `other` roughly doubles too, which nothing on this
ladder addresses.

**The scaling law, and the design input for the predicate:** views/frame go 116,9 -> 174,4 -> 192,7
(1,6x, saturating against the 32-slot budget) while depth draws go 7 887 -> 18 843 -> 56 361 (7,1x)
and `Shadow` goes 3,4x. **The cost is casters-in-view per bake, not view count.** That is why the
answer is a skip — skipping a still cube removes its draws entirely, the term that is actually
scaling — and not a bigger budget or a smaller map.

### The spike is two populations, not one

The worst-frame row separates them, and they have nothing to do with each other:

    worst 2263,9ms = Enroll:0,0 Shadow:0,0 Shim:0,0 Alloy:0,0 Ui:0,3 Present:1,3 other:2262,3
    worst   36,2ms = Enroll:7,3 Shadow:11,4 Shim:8,7 Alloy:3,7 Ui:0,2 Present:0,4 other:4,5

The multi-second frames are **entirely `other`, with every owned zone at zero** — those frames never
reached the scene render at all. That is engine-side asset loading and area streaming. It is not a
renderer spike and no work on this ladder will move it; the ticket body's "plausible source of the
150ms spikes" reading of ownership churn is disproved for this population.

The ~36ms frames are the renderer's, and they are **compound**: `Enroll`, `Shadow` and `Shim` all
spike together. That is why `ShadowEnroll` is its own zone — though here it reads 0,0, so the
collision is Alloy's enrolment against the caster *bake*, not against the caster scan.

### What the shadow number actually is

    196,0 views baked per frame, 69818 depth draws/frame in 10114 multidraw-indirect calls,
    32,0/32 local cubes at 512px, 25,12 ms cpu last, 20,6 ms GPU avg

32 cube lights x 6 faces + 4 cascades = 196, baked **unconditionally, every frame**. There is no
re-bake predicate in `CubePass` at all — and that is not a regression to hunt down. #187 deleted the
still/mover composite cache deliberately (-649/+92: the motion classification, both cache textures,
every `glCopyImageSubData`, the invalidation set) and **waived its own before/after AC in terms that
name this ticket**:

> The number was wanted so the honest cost of a per-frame re-bake could be argued against — but that
> argument cannot be had until the renderer fully owns the pipeline (M4, #114). A before number today
> decides nothing.

M4 is done, and the trace above is that number. #187 measured its deletion at **10,0 views/frame,
0,910ms GPU, 9,23ms frame (108fps)** — and was right at that scale. The cube budget has since gone
8 -> 24 -> 32, so the same unconditional bake is now 196 views and 19,6ms. **The reasoning did not
rot; the scaling did.** #151's identity-keyed slots removed the re-*ranking* that caused cache
misses, but there is no longer a cache to miss.

So the item is not "restore the cache". It is a **re-bake predicate**, now that a still cube is 6 of
196 views rather than 6 of 48: a cube face is stale only if its light moved, its slot assignment
changed, or a caster inside its radius moved or appeared. Everything else is a skip.

**Invalidation, not composition** — and #187's post-mortem is the reason. The composite lost to its
own per-view server-side depth copy: *"a copy stalls; a bake pipelines."* A skip does neither, so it
is not the design that was measured and rejected.

**Revised order: the shadow re-bake predicate first (#241). Step 2 second. Steps 3-5 unchanged.**

## Consequences

**The model being retired is a parasitic one.** Alloy inherits visibility, liveness and part
discovery from a traversal whose output it then discards. That was the right *migration*
architecture — incremental ownership with a vanilla fallback is how a renderer is cut over without a
flag day, and ADR 0009/0016 chose it deliberately. The cutover is what removes the fallback's reason
to exist. #236 was the first bug where the migration scaffolding was itself the defect; steps 2-4
are the plan for it not being the last.

Costs: `FrameZones` adds one `Stopwatch` read per bracket per frame (zero allocation, fixed arrays)
and one log line per 900 frames. The `[rspshim]` figure changed meaning; older numbers are not
comparable with newer ones.

Not decided here: #151, #152 and #153 remain their own tickets. This ADR owns the *order* and the
measurement that justifies the first item.

## Step 2, as built (#242) — the split, written down before the code assumed it

The record buffer is persistent GPU state indexed by **ownership slot**, not by draw index. Draw
order is per pass (a fading tile joins the blended run; the blended run is sorted from this pass's
eye), so the two cannot be the same number: the pass uploads a small `draw -> slot` index array
(4 bytes per draw) and every stage reads `uDraw[uDrawIdx[uDrawBase + gl_DrawID]]`. That indirection
is what buys the record itself the right to stay where it is.

### What is stable, what is per-frame, and how each per-frame field is DETECTED

| record field | when it is written | the live input compared, per frame |
|---|---|---|
| `model` (mat4) | rebuild | the node matrix itself, compared against the record's own copy — matrix-class parts only; the rest staged identity once |
| `rot.xy` (texture rotation) | enrolment | never re-read: a rotation change fails `IdentityOk` and revokes |
| `rot.z` (keyhole dissolve) | rebuild | `PartTable.KeyholeDissolves`, compared against the record |
| `rot.w` (alpha multiplier) | rebuild | `LiveAlpha` — already computed by `RouteParts`, compared against the record |
| `amb` | enrolment | mdl node ambient; only a material swap changes it, and that fails `IdentityOk` |
| `dif`, `emi` | rebuild | the mdl node's colours folded with the gob's colour multiply and self-illum add, compared against the record |
| `pbr.xyz`, `spc` | enrolment | .mtr parameters; a .mtr does not animate |
| `pbr.w` (live slot mask) | rebuild | falls out of the texture re-fetch below |
| `feat` | enrolment | .mtr feature and its three parameters — except `feat.y`, the bone base, which a posed skinmesh rewrites every frame (such a part is unconditionally dirty) |
| bindless handles (8) | rebuild | the borrowed `CAurTexture*` of the diffuse and of each declared slot, compared against `_recAur`/`_recAurS` — an animated texture cycles ENGINE names, so its pointer changes and it is a permanent mover, which is correct |

Everything **not** in the record stays O(routed) and is re-read every frame for every part:
`SightedRecently`, `IdentityOk`, `InstanceOwnable`, the VDO index range (`_dCount`/`_dOff`/`_dBase`),
the VAO, and the batch key. Those are the liveness gates; caching them would be caching the answer
to "is this part still ours", which is the one question that must be asked fresh.

**Dangly and morph-animated meshes need no entry above.** They move VERTICES under a matrix that does
not move, and no vertex ever reaches the record — the VAO reads the (owned, `OwnedBuffers.Refresh`ed)
buffer directly, and the index range is re-read per frame regardless. `OwnedBuffers.DirtySince` is
therefore *not* a record signal; it stays what #241 made it, a shadow re-bake signal. This is the one
place where the ticket's trap list over-specifies: the mechanism it warns about cannot reach a field
this buffer holds.

**`CasterPass.Moved` is deliberately NOT called from here.** It is stateful and consumed: the shadow
pass runs before Alloy, so it has already taken this frame's "moved" edge and rewritten the memo's
hash. A second ask would read *still* on a genuine mover — exactly the frozen record the ticket
warns about — and its eviction path fires a `Ghost`, i.e. spurious bakes. What is reused is the
*mechanism*: a conservative per-part memo whose only permitted error is in the direction that costs
work. Here it is exact rather than hashed, because the record already holds the previous matrix.

### Blanket invalidation (an epoch bump), for the edges no per-part compare can see

`_recEpoch` is bumped — every slot rebuilds on the next frame that draws — on: enrolment (slots
reshuffle under the table), any bindless handle wipe (`ForgetHandles`, `WipeHandles`,
`OwnedTextures.Drain` → `DropHandle`)
and 4.6 context loss, and any `/draw` dial (`aotest` fills a slot the record's mask names). A persistent
record that outlived its handles is a wall sampling a dead texture (#220's shape); dirty-all on a
once-per-area event is the cheap correct answer.

One slot-level invalidation sits with them, and it is not an edge but an ordering: the texture caches
are written where the tids are resolved, and the walk can still skip out (index range, VAO) before the
record is committed. Such a slot is *un-stamped* at the skip site, because on its return the diffuse
pointer would compare equal to a cache its record never caught up with — and it would then be served
as clean, holding the previous texture's handles for as long as it is owned.

Water is re-derived every frame (its record carries per-frame wind and fetch) but still only re-sent
when one of those four floats actually moves. Its body shares the diffuse triple with the lit terms,
so `WritePerFrame` writes one or the other and never both — writing both would leave every water
record dirty every frame while rendering perfectly, which is the one way this scheme can be defeated
silently.

### Auditable

The `[alloy]` period line reports `records N/frame written (M stable)`. Written should be the mover
count — creatures, doors, animated textures, fading tiles — and *not* the owned count. If it reads
as the owned count, the split has a permanent mover in it and step 2 bought nothing.
