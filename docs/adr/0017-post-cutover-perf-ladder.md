# 0017 — The post-cutover perf ladder

Status: accepted (#238, 2026-08-29). Follows ADR 0016, which owns the frame and explicitly declines
to gate on perf. This ADR is what perf work is ordered by now that the frame is ours.

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
