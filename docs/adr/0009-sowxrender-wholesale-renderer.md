# 0009 — SowxRender: wholesale renderer replacement

Status: accepted (direction); supersedes the "patch the engine" framing of 0005–0008, which are
retroactively its foundation layer. #67 campaign, 2026-08-19.

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

- **Scene extraction, not scene reimplementation.** Geometry lives in engine VDOs (GL buffers we
  can bind directly — ADR 0008 Ghidra work), transforms in part/node matrices, materials in
  `Material` (+ `AurTextureGet` → GL texture id, re-fetched per frame per the borrowed-pointer
  rule), lights in LightManager (tier-1 registry, ADR 0005), camera in ShadowInputs. The engine
  remains the authoritative scene database; SowxRender is a reader.
- **Engine draw suppression.** As each content class becomes owned, its engine bucket submission
  is skipped (the ADR 0006 iterator-ownership trick generalizes: we already know how to make
  `RenderDrawBucket` walk past parts we own).

## Architecture (target)

- **Second GL context, 4.6 core, shared objects** with the engine's 3.3 context (GLX share
  lists on the same drawable). Unlocks DSA, `glMultiDrawElementsIndirect`, SSBOs, persistent
  mapping, `gl_Layer` from VS — without touching the engine's context. Engine UI keeps rendering
  in its own context untouched.
- **Frame graph:** depth prepass → light clustering (32-light UBO today; clustered forward+ as
  the light count grows) → opaque forward+ pass (owned programs, SowxLights/SowxCubes UBOs from
  ADR 0007) → shadow system (cube/CSM atlas, owned multidraw submission, layered rendering in
  the 4.6 context) → transparents → post (HDR tonemap; the engine already renders to RGB16F).
- **Submission:** persistent caster/drawable cache (part-keyed, ADR 0008 pattern) feeding
  indirect multidraw batches grouped by (VDO, material). Draw count decouples from part count.

## Milestones (each ships alone)

- **M0 — owned submission layer** *(in progress, ADR 0008)*: static shadow bakes through owned
  multidraw. Proves capture/cache/multidraw against live content. The moving-torch hotspot
  (every-frame 6-face static rebake at 512px) is the direct beneficiary.
- **M1 — owned opaque world colour pass**: tiles/placeables drawn by SowxRender (own programs +
  UBO lighting, textures per group), engine buckets 0/1 suppressed in the colour pass. Kills the
  static majority of the 16.6 ms colour pass and unifies lighting in one code path.
- **M2 — unified shadow system**: all casters owned, including creatures by consuming the
  engine's committed skin streams (mode-2 pool buffers); sun CSM through the same submission.
- **M3 — full scene ownership**: creatures/skinned colour pass, remaining buckets (water,
  particles, alpha), engine `RenderScene` submission fully bypassed.
- **M4 — 2026 features on the owned pipeline**: PCSS/contact-hardening shadows, SSAO, proper
  HDR post stack, layered single-pass cube rendering, GPU-driven culling.

## Consequences

- 0005–0008 are not throwaway: the light registry, UBO plumbing, owned programs, part cache,
  and multidraw submission are M0/M1's load-bearing parts.
- Every milestone must hold the frame budget on live content before the next starts — the
  RenderDoc + `owned-cast` log measurement loop is the gate.
- Windows support of the owned pipeline is deferred until the Linux renderer is complete
  (symbol/RVA story per existing memory).
