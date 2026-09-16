# Missing parts under Alloy: what to do when you see one

A creature with an arm, a shin or a head gone; a bench that was there and is not (#317). It is random,
it heals when the creature moves or is re-created (out of the server's render range and back), and the
bench version comes and goes while you stand still. Nothing in the period line names it yet, so the
protocol below is how one incident becomes a log worth reading. Every step is a live chat knob: no
restart, no `cx.cfg` edit.

## The incident protocol (~30 s)

1. **Do not move.** Note which creature and which part.
2. `/alloy framedump` -- writes `cx_frame_dump/frame.txt` (in the user directory beside `cx.cfg`): one
   `D` row per draw Alloy SUBMITTED this frame, texture names on the row. A body part's texture is its
   PLT name (`pmh0_forel001` = human male left forearm, `..._shinl..`, `..._head..`); a bench is its
   diffuse.
3. `/alloy poll 1` -- and watch: **did the part come back at once?** This rewrites every record
   (`InvalidateRecords`) without touching enrolment or the engine, so an instant heal means a stale
   per-slot record or chase cache and nothing else.
4. Leave `poll` on. The next `[alloy]` period line carries
   `POLL AUDIT misses (must be 0): matrix N keyhole N fade N box N alpha N` -- the cache that disagreed
   with a live read is the one that lost the part. Does the bug recur at all with `poll` on?
5. `/alloy poll 0` when done. Keep `~/cx.log` and `frame.txt`.

The bench plaza is the higher-rate repro: stand beside them with `poll 1` on from the start and see
whether they still vanish.

## Reading it

| observation | it is |
|---|---|
| part returns on step 3 | a stale record/cache; the audit clause names which (matrix / fade / alpha / box) |
| no return, its `D` row IS in the dump | GPU-side: hi-Z occlusion or the shader. Next A/B: `/alloy cull 1` (frustum only, no hi-Z); if it stops, hi-Z |
| no return, its `D` row is NOT in the dump | CPU-side: an enrolment gate or engine state. Next: `alloy.dumptex=<texture>` in `cx.cfg` + restart, read the `[dumpframe] ENROLL ... -> <gate>` line |
| `NOT DRAWN ... fog-out 0 tile + N gob` with N > 0 | the `TileFade <= 0` route skip took a creature part (`AlloyRenderer.Batches.cs`, `RouteParts`) -- its filed tile is wrong |

## Hypotheses, ranked (2026-09-16)

1. **Stale chase cache on an idle creature.** #330 reads matrices / spheres / alpha on a budget and
   re-reads what moved; a part whose bone matrix stops being re-read keeps a stale sphere and fails the
   CPU route gate or the GPU hi-Z test, or a stale alpha routes it blended at 0. Predicts: step 3 heals,
   no recurrence with `poll` on, audit misses nonzero. Fits "heals on movement" and benches returning.
2. **Hi-Z occlusion false positive**: a small limb sphere inside/behind the torso from a rear camera.
   Predicts: `D` row present while invisible; `/alloy cull 1` stops it; benches go as the camera drifts.
3. **`TileFade <= 0` skip** at route -- was the one hole with no counter; `fog-out` on the `NOT DRAWN`
   clause is that counter now, split gob/tile. Predicts: `gob` nonzero beside a missing limb.
4. **Enrolment gate** (`dup-sibling`, `unrenderable`, the +0x34 hidden byte): model-static, so it does
   not fit random-and-healing. Only if 1--3 read empty.

## What is refuted, so nobody re-chases it

- **Texture / the #284 rebuild ledger.** In the 2026-09-16 session the only textures that ever reached
  `[unbuilt]` were `"2"`, `NullTex` and `Standardmaterial` -- junk material names on placeables, the #317
  bench class -- with the 8-line diag budget only 3 spent. `no-gl-id 31` is that population and the
  climbing `skips refused by the ledger` is #627 doing its job on it. Read the full
  `unbuilt textures:` clause including the `UNBUILDABLE:` split after the `;`.
- **The per-instance sweep** (`revoked by the per-instance sweep`) drops a gob WHOLE, so it cannot take
  the hands and leave the robe.
- **`[unbound=N]`** on the census line is the structural whole-creature figure #317 already called a
  red herring.
