# 0010 — Two render modes ship permanently

Status: accepted (#107 grilling, 2026-08-20). Constrains ADR 0009's M3 and everything after it.

## Context

ADR 0009 set the destination as "engine `RenderScene` submission fully bypassed", and #107
settled that M3 reaches it: every 3D class owned, `Scene::RenderSinglePass` suppressed. Read
naively that invites deleting the engine path once the last class is owned.

Three facts say otherwise, and only the first was on the table when 0009 was written:

- **macOS caps at OpenGL 4.1.** Apple deprecated GL after 4.1 in favour of Metal. SowxRender's
  frame graph needs a 4.6 core context (#70), so the owned renderer is structurally
  unavailable there. The affected player count is small but not zero.
- **The extender is more than its renderer.** The chat and dialog UI replacements are the
  features players actually ask for. A device that cannot run the owned renderer must still get
  them; tying the whole extender to a GL 4.6 requirement would trade a large benefit for a
  smaller one.
- **A/B is how rendering bugs get diagnosed.** "Is it the extender?" needs a one-command answer
  from a player who cannot relaunch mid-session. That answer has already earned its keep during
  M0–M2.

## Decision

**The engine's render path is a supported render mode, not scaffolding.** It is retained
indefinitely, and no milestone may remove it.

- The existing `/vanilla` chat command stays, frame-atomic, switching the whole scene at once.
  Ownership is class-granular *within* the owned mode; the mode switch is not a per-class
  affordance.
- "Bypass" therefore means **suppress, never delete**. Every owned class keeps its engine
  counterpart callable, and suppression stays a decision made per frame rather than a
  one-way excision.
- A device unable to create the 4.6 share-listed context **falls back to vanilla mode
  automatically**, logged, rather than failing to start. Context-creation failure is the check —
  not a parsed `GL_VERSION` string, which drivers misreport.
- The **detected capability tier** and the **manual override** are separate pieces of state, so
  "this machine cannot run it" and "this player chose not to" stay distinguishable in a bug
  report.

Out of scope here, and noted only so it is not mistaken for part of this decision: a settings
UI letting players enable and disable sowx modules individually, with the owned renderer gated
behind the device check. That is wanted eventually; nothing in this ADR waits on it.

## Consequences

- M3's definition of done is about *which renderer draws the frame*, not about how much engine
  code remains reachable. A residual engine draw is expected — that is the vanilla mode.
- The suppression mechanism (the `GetNextItem` loop-skip filter from #71, generalised) stays
  load-bearing through M3 and beyond, rather than being retired once ownership is total.
- Every feature outside the renderer must keep working in vanilla mode. That is already true and
  becomes a standing constraint rather than an accident.
- Two visual results exist for the same content, permanently. Legacy parity was already a
  non-goal (#102), so this is not a convergence obligation — but bug reports must name the mode,
  and the gate's fixed comparison shots (#107) are shot against vanilla mode by construction.
- Windows and macOS support of the *owned* pipeline remains deferred (0009); this ADR is what
  makes that deferral survivable rather than blocking.
