# ADR 0022: The stage runs on worker threads, under one rule

Status: accepted (2026-09-05). Live protocol passed on the plaza: audit 0/0/0/0/0, batches/draws/records identical, stage run 0,40ms (wake 0,02), Build 2,6 -> 1,9ms. Boot = 6 (/draw threads N is the dial).

## Context

With the plaza's colour pass fixed (binreach/bincap) the frame is CPU-bound at ~10,4ms: Alloy's
build is 2,6ms of it (Restage 1,8 + Chase 1,4), the shadow submission ~2ms, and the engine's own
AI+GUI ~3,8ms, which no renderer change touches. 120fps needs the build under 1ms.

`BuildBatches` was one loop per routed part: chase the engine state the part depends on (node
matrix, keyhole verdict, textures, gob colour folds), write its record and spheres, then append
it to the batch list. The first two halves touch only the part's own slot arrays; the third is
order-dependent. That is a seam.

Two facts make worker threads viable at all:

- Alloy runs ON the engine's main thread inside its frame. While a stage runs, the engine
  mutates nothing -- every engine read from a worker is a read of a paused world.
- The engine accessors the stage uses are loads, not calls: `Material::GetTexture` was
  transcribed as its two loads (#330), `NodeRef.Model` is a raw matrix, the gob folds are field
  reads, `AurTextureGetTID` is a referenced-texture hop, a null-object compare, a virtual resolve
  and a field load (disassembled 2026-09-05). The ONE engine call a worker makes is
  `Camera::GetCanKeyholeDissolve`, pure in the camera words (#330 RE).

## Decision

**`StagePart` runs on N worker threads plus the main thread over a static partition of the
routed range, then the main thread batches serially.** The worker rule, and the whole design:

> A worker may take only a path whose side effects are its own slot's arrays and its chunk's
> counter struct. Anything that mints a GL object, logs, allocates, or touches a shared
> collection returns `Deferred`; the main thread re-runs that part after the join.

Concretely:

- Read-only halves exist wherever the main-thread call mutates: `HandleLookup` (no mint),
  `VaoReady` (no build), `OwnedTextures.TryDraw` (no borrowed-set write). A miss defers.
- `NoteUnbuilt`, `TexSkipDiag`, `W()` (a watched part) defer at the top of their path.
- Shared appends reserve with `Interlocked` after a main-thread `Begin` (`NoteCasterTouched`,
  `MarkRecordDirty`); `CasterGen` bumps once per frame at the fold, not per change.
- Hot counters live in a per-chunk `StageCounters` struct folded after the join; rare skip
  counters use `Interlocked.Increment`.
- `StagePool`: persistent threads, semaphore wake, spin join, caught faults. A fault stands the
  threading down for the session and the frame re-stages serially. No `Parallel.For`, no
  `ThreadPool`: both allocate per call, and the hot path allocates nothing (CLAUDE.md).

Invariant the pool's summary states: nothing on the main thread writes the texture index, the
handle hash or the ownership tables between `Run` and its return. An async loader would break
exactly this; it must go through the stage's join.

## Consequences

- `/draw threads N` (boot 0). Live protocol on the plaza, still camera: `threads 0` then `6`;
  batches, draws/frame and records written on the `[alloy]` line must match; `/draw poll 1`
  audit reads 0/0/0 in both; then Build on `[zones]`. Expected 2,6 -> ~0,7ms.
- The Chase zone is not sampled on workers; under `threads > 0` it reads near zero and Build is
  the number. `deferred/frame` on the `/draw` describe line must stay near 0 steady-state.
- A deferred part's dissolve counters are counted twice (worker then main). Rare; noted.
- What is NOT parallel: `RouteParts` (the routing walk, main), the batch walk, the uploads.
- Re-examined 2026-10-01 (ownership-cleanup BW5) and left serial, for three reasons beyond scope.
  (1) There is little left in it: the route memo replays 15 of every 16 frozen gobless slots and the
  visible-set trim cuts the candidates to the boxes in view, so the live median (5,650 routed of
  23,063 visited; `Scan` p50 1,90 ms on the mean-frame row, 2,3 ms on the hitch row, over 238
  periods) derives to ~1,3k slots still deriving a verdict — the per-slot
  GATE over ~7k slots is the cost, and packing it into one row (BW7, `RouteGate`) is what that asks
  for. (2) **The routing walk is this pool's wake shadow.** `Prewake` fires at the top of the build
  so the workers are spinning by the time `Run` publishes the stage; making the walk itself the
  first job either pays that wake unhidden or moves `Prewake` to the frame anchor, which is exactly
  the case `StagePool` measured as spinning cores for the rest of every frame. (3) Phase A would
  have to defer `Revoke`, the `[dumpframe]` watch and every shared counter, and phase B would still
  walk every candidate to rebuild `_pass`/`_blend` in order: a ~0,3 ms ceiling on a 14 ms frame for
  a second pool run per pass and a new class of nondeterminism. Revisit if a measured `Scan` stays
  above a millisecond with the memo on and the gate packed.
