# ADR 0029: Deferred point lights -- a G-buffer twin and tiled compute

Status: proposed (2026-10-07, CX-188), live-unverified. Amends ADR 0024 with one new overridable
stage and one override that stands a feature down. ADR 0005 (the lighting reimplementation), ADR 0019
(the shadow data plane) and ADR 0021 (AO is a lighting input) hold unchanged.
Contract: [docs/spec/deferred-lighting.md](../spec/deferred-lighting.md), whose format is
`managed/Alloy/Shaders/cx_gbuf.glsl`.

## Context

Alloy lights forward+: a compute pass bins the area's lights into a 32x18x24 froxel cluster grid, and
every lit fragment walks its froxel's list inside the material uber-shader. That was the right shape
when the loop was a few lights and a hard shadow tap. It is not the shape of the frame we measure now.

Heavy area, 2026-10-07, 196 lights (146 of them static, 24 dynamic): frame 27.7 ms, GPU 26 ms, of
which the lit colour pass is 13.3 ms. The knob A/B says where that goes.

| step | frame delta |
|---|---|
| `shadow.cubedist 5` | -10.7 ms |
| `lightmap.movers 0` | -10.1 ms |
| `shadow.cap 0` | -2.8 ms |
| `light.relevance 0` | +9.6 ms |

The light *bookkeeping* is not the cost: the cluster build is 0.07 ms, and the CPU light select, walk
and upload do not register. What costs is per-fragment shadow work -- the local-shadow filter inlined
once per candidate, on a static and a dynamic cube array both -- the 16-sphere ray test every baked
fragment under a hot light runs, binning that ignores the prepass depth so fragments walk most of
what was binned, and the loop itself being *inside* the uber-shader: its registers are charged to
every material, lit or not, and the same loop is compiled into grass, water, particles and weather.

The classic reasons to stay forward+ do not apply here:

- the game already pays two geometry passes (the depth prepass, then colour at `GL_EQUAL`), so a
  G-buffer costs no extra geometry;
- anti-aliasing is TAA and SMAA, not MSAA, so there is no per-sample shading to lose;
- the screen-space features (SSR, the AO pass) already want normals, so part of the G-buffer is
  work the frame has a use for beyond this pass;
- the camera is top-down and authored light reaches 10-38 m, so per-pixel light overlap is heavy by
  construction -- exactly the case where paying per pixel beats paying per fragment per light.

## Decision

**Point lights move out of the lit stage into a classic G-buffer plus one tiled compute pass. Nothing
else moves.** The split is a hybrid on purpose, and the hybrid is what makes it exact.

### The twin writes weights, not a material

The lit fragment stage compiled with `CX_DEFERRED` is the **deferred twin**. It still does everything
the lit stage does except the point-light walk and the film highlight: sun and cascades, ambient, the
lightmap term, the probe, SSR records, weather, emissive, keyhole, fog, overlays all still land in
the HDR target during the colour pass. What it adds is the G-buffer: the surface parameters the walk
reads (normal, roughness, specularity, Fresnel, coat, sheen, F0, the flag bits that select its
branches) *and* the **transfer weights** -- d(final pixel)/d(`point`), d(final pixel)/d(`pointSpec`),
and the baked term's share of the finished pixel for the hot-light debt.

The deferred pass is then an exact linear correction:

    hdr.rgb += Wd * acc + P * (spec + film) - L * min(lmSub.x / lmLum + lmSub.y, 1)

Weights rather than a material model because the forward stage's composition is *not* a BRDF
evaluation followed by a multiply -- it has the legacy env pattern's `(ambient + diffuse) * pat`
coupling, the coat probe's `(ambient + diffuse) * cenv`, the ghost divide, keyhole, fog. Every one of
those is nonlinear in something, and every one stays in the lit stage, where it already works. The
lit stage is linear in *the loop's results*, so the twin can hand the compute pass the two
derivatives and the debt share and be arithmetically identical to the forward loop on the same
inputs. A deferred renderer that re-derived the composition would have to reproduce all of it, and
would be wrong at every material Alloy gains after this.

That is the whole reason this is not a G-buffer in the textbook sense: it does not describe a
surface for a lighting model to shade, it describes **what the surface does with one more unit of
point light**.

### What stays forward

The probe capture and the planar mirror; the blended class and its OIT twin; water, grass, blades,
particles, weather (step 5 slims their loop); every `/alloy viz` mode, so no debug view is ever
reading a path the frame did not take; and `light.deferred 0`, the A/B. Forward+ is not legacy after
this -- it is the path for everything a screen-space G-buffer cannot hold.

### Frame order and the all-or-nothing rule

`DepthPrepass -> AmbientOcclusion -> OpaqueOwned -> SkyOwned -> CutoutOwned -> DeferredLights ->
GrassOwned -> Reflections -> ...`. The opaque and cutout passes attach G0..G3 at COLOR4..7 and detach
after; `GBuffer.BeginFrame` clears G0 to zero and G0's flag byte gates every read, so the sky, grass
and every forward-lit class are left alone by construction rather than by a test. The pass runs
before grass (whose root blend copies the lit frame under each blade) and before SSR traces, so
reflections see lit surfaces.

The frame is never half-deferred. Either the opaque and cutout batches of the main view are drawn by
the twin and the compute pass lights them, or the forward loop runs and the pass does nothing. The
failure mode a hybrid invites -- a pixel lit twice, or not at all, because one batch took the other
path -- is designed out, not guarded against.

## Considered and rejected

- **A light pre-pass (deferred lighting, Engel's form)**: store only depth and normal, accumulate
  diffuse and specular light buffers, multiply in a second geometry pass. Rejected because that
  second pass *is* the whole material evaluation again -- Alloy's composition is the coupled thing
  above, so the "modulate" step would re-run the uber-shader, keeping its register pressure and
  paying the geometry twice over.
- **Visibility-buffer shading off the existing vis-ID target.** Alloy already writes a per-pixel
  record slot plus primitive ID in the prepass, which is the input a visibility-buffer deferred
  renderer needs, and it was the direction this work was headed before the bench. Rejected: the
  technique pays off where triangles are small enough that per-fragment attribute work dominates,
  and NWN's are big. Worse, resolving attributes from a primitive ID means re-running the vertex work
  the frame already did -- skinning, dangly, wind -- per pixel, for geometry whose vertices are cheap.
- **Stay forward+ and fix the loop** (tighter binning, a shadow cache, fewer candidates). It keeps
  the problem the bench points at: the loop is compiled into every material and charged to every
  fragment. And the shape has been tried: the screen-space shadow mask built and reverted on
  2026-10-07 dispatched *per light* over each light's screen rect, and in this camera and these light
  reaches those rects summed to about six screens -- a net loss. Deferred work is per **pixel**, once,
  whatever the overlap. That is the correction, and it is the same correction the mask attempt was
  missing.

## What the move is for

Steps 1, 2 and 5 are plumbing; the frame time is in 3 and 4. The streams (spec's Streams table,
branch `cx-188-deferred-lighting`):

| step | what |
|---|---|
| 1 | the colour pass's twin writes the G-buffer; `light.deferred`, the `CX_DEFERRED` program variant |
| 2 | the tiled compute pass: depth-bounded 16x16 tile culling, per-pixel accumulation, parity A/B |
| 3 | **shadows in the compute**: per-pixel top-N shadowed lights, one cheap filter, optionally half-res with a depth-aware upsample |
| 4 | **the hot-light debt**: a per-mover K-light budget, pair tiles, a screen-space debt term |
| 5 | the forward consumers keep a slimmed loop |

Step 3 is what `shadow.cubedist`'s -10.7 ms is about. In a tile, the compute pass knows which lights
reach which pixels before it shades any of them, so it can choose the N shadowed lights that matter
*for this pixel* and filter each once -- where the forward loop has to inline the filter per candidate
for every material that might need it. Half-res with a depth-aware upsample is available there and
nowhere else, and it is explicitly not a TAA-dependent technique: the half-res result has to look
right with TAA off, or it does not ship.

Step 4 is `lightmap.movers`' -10.1 ms. The 16-sphere per-fragment ray test goes away entirely,
replaced by a per-mover budget of the K static lights it actually darkens, pair tiles baked for those
pairs, and the debt taken out of the baked term in screen space -- which the G-buffer's `lmShare` lane
exists to make exact.

## Override contract (amends ADR 0024)

- **A new overridable stage, `cx_deferred_cs`.** It is the deferred pass, so it is customizable like
  every other stage: an embedded default ships, a hak or `override/` copy replaces it, and a copy that
  fails to build or trips a guard costs a log line and the embedded stage draws. `cx_gbuf` is a
  library and overridable on its own, with the same caveat every library carries: it is the format,
  and a copy that changes a lane breaks both sides at once.
- **A `cx_ownlit_fs` override that does not call `CxGbufWrite` stands the feature down.** The
  extender checks the compiled text; if the override cannot be a twin, `light.deferred` goes off for
  the session with one loud line naming the override, and the frame is the forward loop. This is the
  no-silent-fallback rule applied to a content-supplied stage: the alternative is a frame whose
  opaque surfaces are missing every point light, and a shader author who gets no explanation.

Step 3 adds two more overridable names: the compute stage `cx_defshadow_cs` and the library `cx_shrec`, which,
like `cx_gbuf`, *is* a format -- a copy that changes a lane breaks writer and reader at once. A shadow record that
fails to link stands `light.deferred` down for the session rather than shipping hard-tap shadows.

## Known deviations

Stated in the contract, repeated here because they are the decision's price:

- **Fog** mixes in display space, so the transmittance folded into the weights is the linear
  approximation `1 - CxFogFactor` -- the same approximation SSR already takes.
- **Display overlays** (noir, racial vision, the targeting shape) apply after the fog and are not
  linear in the lit colour; under an active overlay the deferred contribution misses them. Rare
  states, and the A/B knob is the check.
- **Precision**: the normal is snorm16 octahedral, the small alphas and the sheen colour are
  sqrt-encoded unorm8, the weights are half floats. A highlight off a near-mirror film is where that
  would show first.

## Consequences

- **56 bytes a pixel**, three RGBA32UI words and one RGBA16F: about 187 MiB resident at 2544x1378,
  written once by the colour pass and read once by the compute pass on a fully covered frame. This is
  the cost the decision trades frame time for, and the first thing to measure if a bandwidth-bound
  card does worse than this one.
- **The draw-buffer budget is now fully spent.** GL guarantees eight: COLOR0 the colour, COLOR1 the
  vis-ID, COLOR2-3 SSR's records, COLOR4-7 the G-buffer. Nothing else in the colour pass can take an
  attachment without first taking one back, and a lane added to `cx_gbuf` has to fit the four words
  that exist.
- **The light loop is compiled in two shapes**, a compute one over the G-buffer and the forward one
  the transparents and captures keep. They have to agree, and the thing that holds them to it is the
  parity A/B at step 2 plus `light.deferred` as a standing A/B after that -- not a comment saying they
  match.
- **Every `/alloy viz` mode stays forward**, which is a feature: a debug view shows the forward loop's
  own answer, so a disagreement between the two shapes shows up as the frame differing from the viz,
  not as both being wrong together.
- The twin is a second lit program to link, and the hosted content program (#352) needs its own
  answer before a hosted opaque surface can be deferred.
- `check-shaders.py` gains the deferred program, so the off-device gate compiles and driver-links it
  like every other Alloy program. What it cannot cover is the parity: that needs a live A/B, which is
  why this ADR is live-unverified.
