# 0009 — Alloy: wholesale renderer replacement

Status: accepted (direction); supersedes the "patch the engine" framing of 0005–0008, which are
retroactively its foundation layer. #67 campaign, 2026-08-19.

**Its milestone list is superseded by ADR 0018 (#255, 2026-08-29).** M0–M4 are done; M5 — the
wholesale rework of how Alloy operates, now that ownership is complete — is specified there, along
with the bar and the strand sequence. What this document keeps permanently is its *argument*: why a
wholesale renderer at all. Read 0018 for what remains to be built. Amended twice before that: M4
redrawn 2026-08-26 (#203 / ADR 0016), M5 added 2026-08-29.

## Context

The measured frame (ADR 0008) shows the engine's 2002 submission model is the bottleneck
everywhere: per-part state churn makes cost proportional to *draw count* in every pass — 23.7 ms
shadow bakes, 16.6 ms colour pass, and any future feature pays the same tax. Incremental fixes
inside that model each buy one pass. The decision: **stop optimizing the engine's renderer and
replace it.** Target is a 2026-standard real-time renderer; legacy visual compatibility is
explicitly a non-goal.

## Division of labour (end state)

The engine keeps what it is good at and we cannot cheaply replace: simulation, resource loading,
animation/skinning state, and the 2D UI. Everything between "scene state exists" and "pixels
exist" becomes ours:

*(The 2D UI is on that list **for now**, not permanently — ADR 0016 keeps it engine-drawn through
M4 because owning it buys no world performance, but a Vulkan backend would need every pixel,
so owning the UI system is that backend's prerequisite. It belongs to ADR 0004's track.)*

- **Scene extraction, not scene reimplementation.** Geometry lives in engine VDOs (GL buffers we
  can bind directly — ADR 0008 Ghidra work), transforms in part/node matrices, materials in
  `Material` (+ `AurTextureGet` → GL texture id, re-fetched per frame per the borrowed-pointer
  rule), lights in LightManager (tier-1 registry, ADR 0005), camera in ShadowInputs. The engine
  remains the authoritative scene database; Alloy is a reader.
  *(Amended 2026-08-26 by #203/ADR 0016: from M4 the geometry and texture halves are **copied once
  into Alloy-owned buffers and textures**, not bound by engine-assigned name. "Alloy is a reader"
  stands — the engine still owns what exists, where it is, and its animation and fade state. See the
  second amendment below.)*
- **Engine draw suppression.** As each content class becomes owned, its engine bucket submission
  is skipped (the ADR 0006 iterator-ownership trick generalizes: we already know how to make
  `RenderDrawBucket` walk past parts we own).

## Architecture (target)

- **Second GL context, 4.6 core, shared objects** with the engine's 3.3 context (GLX share
  lists on the same drawable). Unlocks DSA, `glMultiDrawElementsIndirect`, SSBOs, persistent
  mapping, `gl_Layer` from VS — without touching the engine's context. Engine UI keeps rendering
  in its own context untouched.
- **Frame graph:** depth prepass → light clustering (32-light UBO today; clustered forward+ as
  the light count grows) → opaque forward+ pass (Alloy programs, SowxLights/SowxCubes UBOs from
  ADR 0007) → shadow system (cube/CSM atlas, Alloy multidraw submission, layered rendering in
  the 4.6 context) → transparents → post (HDR tonemap; the engine already renders to RGB16F).
- **Submission:** persistent caster/drawable cache (part-keyed, ADR 0008 pattern) feeding
  indirect multidraw batches grouped by (VDO, material). Draw count decouples from part count.

## Milestones (each ships alone)

- **M0 — Alloy submission layer** *(in progress, ADR 0008)*: static shadow bakes through owned
  multidraw. Proves capture/cache/multidraw against live content. The moving-torch hotspot
  (every-frame 6-face static rebake at 512px) is the direct beneficiary.
- **M1 — Alloy opaque world colour pass**: tiles/placeables drawn by Alloy (own programs +
  UBO lighting, textures per group), engine buckets 0/1 suppressed in the colour pass. Kills the
  static majority of the 16.6 ms colour pass and unifies lighting in one code path.
- **M2 — unified shadow system**: all casters owned, including creatures by consuming the
  engine's committed skin streams (mode-2 pool buffers); sun CSM through the same submission.
- **M3 — full scene ownership**: creatures/skinned colour pass, remaining buckets (water,
  particles, alpha), engine `RenderScene` submission fully bypassed.
- **M4 — own the frame** *(amended twice, 2026-08-26; scoped by ADR 0016)*: **Alloy draws the entire
  3D world, from its own resources, from its own loop. The engine keeps the 2D UI and the present.**
  Alloy stops being a guest inside `Scene::RenderSinglePass`, which is replaced by a mutate-only
  shim; ADR 0010's vanilla mode is preserved as a branch taken at frame start. Each ownership step
  is chosen so it closes an RHI readiness gate (`docs/research/rhi-abstraction.md` G1–G4) and
  follows its §6 hygiene — never so it adds an abstraction layer.

### Amendment (2026-08-26): M4 was "2026 features"

M4 originally read *"PCSS/contact-hardening shadows, SSAO, proper HDR post stack, layered
single-pass cube rendering, GPU-driven culling"* — technique work on top of an owned pass. That
was written under the assumption that M3 would leave the frame owned. It did not: M3 closed with
Alloy anchored at `Scene::FadeDoors`, inside the engine's own `RenderSinglePass`, with the loop,
the `Clear(7)`, the bucket walk and the present still the engine's. #132 dropped M3's frame-time
gate for exactly that reason, and the RHI readiness research reached the same place independently
— four of its six gates are restatements of frame ownership.

So the engine frame is the bottleneck under every remaining graphics feature, and the feature list
is post-independence work. It returns as ordinary repo issues once the frame is owned, not as a
milestone. Map #68 carries the sequencing.

### Amendment (2026-08-26): what "own the frame" turned out to mean

#203 scoped M4 and reverse-engineered the frame loop above `Scene::RenderSinglePass`, which nobody
had looked at. Two claims in the first amendment did not survive it, and **ADR 0016 is now the
scope of record**:

- **The present and the 2D UI are out.** The engine's UI draws flat quads on top of a finished Alloy
  frame; it shares no buffers, constrains no draw order, and limits no area size. Owning the present
  would mean owning three present paths (`Update`, the nested `UpdateScreen` load pump, and movies)
  and the post-swap `GLRender::ResetRenderStates` resync, for nothing the world renderer can use.
  The main menu, GUI panels and loading screens never enter `RenderSinglePass` at all, so they need
  no handling.
- **"`RenderSinglePass` is not entered" is not achievable.** RSP braids drawing with seven pieces of
  per-frame bookkeeping — door fades, particle animation, grass wind, lens-flare occlusion, focus
  lighting, framebuffer setup, shadow-light lists — four of which have no other caller in the
  binary. The cutover is a **mutate-only shim** that calls the bookkeeping and issues no draws.

What M4 gained in exchange is **G2**: Alloy owns its own buffers and textures, which is the change
recorded against the reader doctrine above. ADR 0011's filed M4 inheritance (planar water
reflection, the post stack, same-frame pose capture) moves to M5.

## Consequences

- 0005–0008 are not throwaway: the light registry, UBO plumbing, Alloy programs, part cache,
  and multidraw submission are M0/M1's load-bearing parts.
- Every milestone must hold the frame budget on live content before the next starts — the
  RenderDoc + `Alloy cast` log measurement loop is the gate.
- Windows support of the Alloy pipeline is deferred until the Linux renderer is complete
  (symbol/RVA story per existing memory).
