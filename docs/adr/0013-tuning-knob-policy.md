# 0013 — Tuning knobs: three buckets, and the env var is not one of them

Status: accepted (2026-08-24 grilling).

## Context

The extender carries **149 distinct `SOWX_*` environment variables**, read at **78 sites**, parsed by
**four near-identical private `Env(name, fallback)` helpers** (`WaterPass.cs:125`, `PostChain.cs:129`,
`OwnedLights.cs:352`, `CasterPass.cs:787`). Modules reach across subsystems to borrow one:
`GrassField` (14 sites), `OwnedDrawService.cs:851` and `EnvProbe.cs:95` all call
`Shadow.CasterPass.EnvFloat` — a render module calling into a shadow pass to parse a string.

The count is a symptom of the mechanism. An env var is free to add, invisible to discover, requires a
relaunch to change, cannot be listed, and never has to be removed. So none ever were: knobs added to
bisect a shipped bug are still there, indistinguishable from knobs that are real features.

Env vars also fit the deployment badly. They must be set before launch, which for a Steam user means
editing Launch Options — a worse experience than the chat commands the extender already has
(`/light`, `/grass`, `/post`), and one that cannot be changed while looking at the thing being tuned.

## Decision

**Every knob lands in exactly one of three buckets, and "environment variable" is not a bucket.**

1. **Chat command** — anything tunable while looking at its effect: look, colour, debug
   visualisation, per-pass toggles. These join the console-command surface rather than each service
   growing its own dispatch branch.
2. **Config file** — anything that must be decided before the first frame, or that describes the
   machine rather than the look: capability gates, buffer sizes, the headless shader dump. Read
   through the shared config module, honouring the pre-main deferral rule that five separate call
   sites currently re-derive by hand.
3. **Deleted** — anything that existed only to bisect a bug that is now fixed. A kill switch is not a
   feature; if it has no reader outside a closed ticket, it goes.

A knob with no bucket does not ship.

## Consequences

- `SOWX_DUMP_SHADERS` is replaced by `/dumpshaders` (ADR 0012) with a config-file or launcher-flag
  trigger for the headless case. It is the worked example of the policy.
- The four duplicate `Env` parsers collapse into one module, and the cross-subsystem borrowing goes
  with them. This subsumes the `Tunables` item in the architecture review's pass-lifecycle candidate,
  which described consolidation only — this ADR also requires reduction.
- Bucket 1 needs a command surface to exist first. `ChatService.TryWindowCommand` is today an
  eleven-branch if-chain dispatching into Render, Perf, Quickbar and Chat from the chat service; it
  becomes a registry each service registers into. Without that step, bucket 1 just moves the sprawl.
- Bucket 2 depends on the shared config module, which does not exist yet — five hand-rolled
  `key=value` parsers do, one of them inside the framework itself.
- Triage is per-knob and cannot be done in bulk: deciding a knob's bucket requires knowing whether it
  is still read, by whom, and why it was added. Expect this to run across several sessions, and to
  turn up knobs whose originating ticket is long closed.
- A listable knob surface is a new capability, not just a tidier one. Today nothing can enumerate
  what is tunable; after this, both buckets 1 and 2 can print themselves.
