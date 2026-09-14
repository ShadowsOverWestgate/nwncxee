# 0010 — Two render modes ship permanently

Status: accepted (#107 grilling, 2026-08-20). Constrains ADR 0009's M3 and everything after it.
Amended 2026-08-26 (#203 / ADR 0016) — see the amendment at the end.
**Amended again 2026-08-29 (#255 / ADR 0018): both modes still ship permanently, but the vanilla
mode becomes restart-only rather than a live toggle**, so the two resource worlds are never warm at
once. That is what makes M5's exclusive residency ownership affordable, and it lets the per-frame
refusal surface collapse into a boot decision. The decision this ADR records — that the engine path
is supported indefinitely and "bypass" means suppress, never delete — is unchanged.

## Context

ADR 0009 set the destination as "engine `RenderScene` submission fully bypassed", and #107
settled that M3 reaches it: every 3D class owned, `Scene::RenderSinglePass` suppressed. Read
naively that invites deleting the engine path once the last class is owned.

Three facts say otherwise, and only the first was on the table when 0009 was written:

- **macOS caps at OpenGL 4.1.** Apple deprecated GL after 4.1 in favour of Metal. Alloy's
  frame graph needs a 4.6 core context (#70), so the Alloy renderer is structurally
  unavailable there. The affected player count is small but not zero.
- **The extender is more than its renderer.** The chat and dialog UI replacements are the
  features players actually ask for. A device that cannot run the Alloy renderer must still get
  them; tying the whole extender to a GL 4.6 requirement would trade a large benefit for a
  smaller one.
- **A/B is how rendering bugs get diagnosed.** "Is it the extender?" needs a one-command answer
  from a player who cannot relaunch mid-session. That answer has already earned its keep during
  M0–M2.

## Decision

**The engine's render path is a supported render mode, not scaffolding.** It is retained
indefinitely, and no milestone may remove it.

- The existing `/vanilla` chat command stays, frame-atomic, switching the whole scene at once.
  Ownership is class-granular *within* the Alloy mode; the mode switch is not a per-class
  affordance.
- "Bypass" therefore means **suppress, never delete**. Every Alloy class keeps its engine
  counterpart callable, and suppression stays a decision made per frame rather than a
  one-way excision.
- A device unable to create the 4.6 share-listed context **falls back to vanilla mode
  automatically**, logged, rather than failing to start. Context-creation failure is the check —
  not a parsed `GL_VERSION` string, which drivers misreport.
- The **detected capability tier** and the **manual override** are separate pieces of state, so
  "this machine cannot run it" and "this player chose not to" stay distinguishable in a bug
  report.

Out of scope here, and noted only so it is not mistaken for part of this decision: a settings
UI letting players enable and disable cx modules individually, with the Alloy renderer gated
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

## Amendment (2026-08-26, #203 / ADR 0016): what M4 costs this ADR, and the one part still unbuilt

M4 scoping expected vanilla mode to be an M4 cost item, on the assumption that it is implemented as
"do not install these hooks" and would have to become "do not take the branch at frame start". That
assumption was wrong: **it is already the branch.** `RenderMode` latches once per frame at
`FramePhase.Start` (`managed/Alloy/Core/RenderMode.cs:45-57`), every render site reads the latched
bool, and the hooks install unconditionally (`managed/Alloy/AlloyRenderer.cs:565-568`). The genuine
"install nothing" keys are `lockchat`, `gl46`, `shadows` and `hdr`, none of which is `/vanilla`.
**M4 owes this ADR nothing on that front.**

What *is* unbuilt is this ADR's fourth bullet: **the detected capability tier and the manual
override are not separate pieces of state.** `RenderMode` carries only the override; the capability
side lives in the unrelated `GlContextService.Available` / `gl46` path.
**Paid 2026-08-28 by #216**: `AlloyGate` (`managed/Alloy/Core/AlloyGate.cs`) holds the capability state and
`RenderMode` held only the override; the frame verdict combined them once per frame, `/vanilla` reports
both, and a capability dropout raises an on-screen notice naming the reason.
**Collapsed 2026-08-29 by #263**: the amendment at the top of this ADR is paid here — `RenderMode` is deleted,
the request is the `vanilla` key in `cx.cfg`, and `AlloyGate` decides once per session and freezes. The two
pieces of state are still separate (`OptedOut` vs `Capable`), which is what this bullet asked for. See
`docs/alloy-refusals.md`.
The rest of this paragraph is the history that got there. That was tolerable while the
frame belonged to the engine. It stops being tolerable at M4's cutover, where a frame-start branch
has to answer "this machine cannot run it" and "this player chose not to" separately — and where an
Alloy dropout must say which one it was, on screen and in the log. Implementing the split is M4 step
7's work.

Two consequences of ADR 0016 land back here, neither of which weakens this decision:

- **The suppression machinery survives without a redesign.** `BucketGate` has four call sites outside
  its own file, and a walk that is never entered fires no hooks. "Suppress, never delete" is
  satisfied by the engine path remaining callable, which the shim's config key preserves.
- **The three-mode reading of a Vulkan future was wrong.** `docs/research/rhi-abstraction.md` failed
  G5 permanently on the grounds that this ADR keeps vanilla forever, making "GL-and-Vulkan-and-
  vanilla" the only reachable end state. A Vulkan backend will instead be a separate *configuration*
  with no runtime swapping, so this ADR does not create that trap. Nothing here changes; the
  research's G5 verdict does.
