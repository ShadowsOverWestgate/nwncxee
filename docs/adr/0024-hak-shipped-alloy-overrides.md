# 0024 — Hak-shipped Alloy overrides are supported

Status: accepted (2026-09-13). Supersedes the shipping ban in [0012](0012-shaders-ship-embedded-never-as-content.md).

## Context

ADR 0012 forbade any content repo from carrying a `cx_*.shd`, on the drift measured that day:
eleven shipped copies, all stale, every guard green. It kept the override read path and added
`/dumpshaders` so a customizer starts from the stage this build compiles.

That left the override path with one legal user: a player editing their own `override/`. But
the reason the stages are `.shd` resources at all, and the reason the dump exists, is so a
**module** can ship its own look -- a hak is the only channel that reaches every player of a
server, and a per-player `override/` cannot. 0012 kept the mechanism and banned its purpose.

## Decision

**A hak or NWSync manifest may ship `cx_*.shd` overrides. The extender still ships every
default embedded.**

- The embedded copy is the fallback, never absent: a hak override that fails to build or
  trips a guard costs a log line and the frame is drawn from the embedded stage.
- Drift is the shipper's to manage, with the tools 0012 built: re-dump after each extender
  update and diff, keep the sizing tokens, read `cx.log` for guard lines. The guards catch
  layout drift; feature drift (a stage that gained code the copy lacks) is caught by the diff.
- The extender does not version-check content. A `cx_` stage that builds and passes the guards
  runs, whatever build it was written against.

## Consequences

- `docs/shader-overrides.md` describes the hak workflow instead of forbidding it.
- The design taxes 0012 called payable (`AoBit` displaced to bit 7, new uniforms degrading to
  location -1) stay: they exist precisely so a stale shipped copy fails soft.
- sow-topdata may carry `cx_*.shd` again. Its validator's `is_cx` special case, if
  restored, should compare against a fresh dump rather than a hand-kept table.
