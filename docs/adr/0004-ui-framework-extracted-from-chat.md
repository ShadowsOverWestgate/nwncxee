# Chat's UI concerns extracted into a `CX.Ui` framework

The frame present (`SwapBuffers`), the render toolkit, input capture, focus/keyboard
arbitration, and the cursor were all owned by the Chat service purely because Chat was
the first feature with a UI — leaving other services (Dialog, and a growing list to come)
to reach into `CX.Services.Chat.*` and re-solve input arbitration by hand, which
stalled development more than once. We are lifting those concerns into a first-class
top-level `CX.Ui` framework: a `UiHostService` owns the frame and drives a registry of
first-class `Window`s (chrome/drag/resize/focus/hit-test/cursor shared by the framework,
*content* supplied by each service), and the framework owns raw input capture with a fixed
**input precedence** — focused Window content → registered chord consumers → engine
gameplay keybinds — so a new UI service or a new hotkey both plug in for free.

We chose a full framework (not a toolkit-only extraction) because input/focus arbitration
was the actual repeated pain, and it cannot be shared without the framework owning the
capture and precedence. Chat is the **golden regression gate**: its look, feel, and
live-verified fixes must survive byte-for-byte, while Dialog/Cursor fold in and may shift
to the unified behaviour.

## Considered options

- **Toolkit-only extraction** (move `TextRenderer`/`VertexBatch`/atlas, leave Chat owning
  the frame and hardcoding every service's per-frame draw) — rejected: leaves the real
  coupling (Chat's `OnSwap` roll-call and the duplicated arbitration) in place.
- **A second discovery interface `IUiService`** — rejected in favour of a static `UiHost`
  registry that a `Window` constructor joins, matching the codebase's existing
  static/discovery conventions with the shortest diff.
- **Leave `HotkeyInputService` whole and have the framework delegate capture to it** —
  rejected: inverts the layering (framework depending on a service) and keeps the next
  input consumer from joining cleanly. Instead its capture+arbitration half moves into the
  framework and Hotkeys stays a server-specific chord *consumer*.

## Consequences

- The static `TextRenderer.Publish(RenderFrame[])` channel is retired; each Window draws
  its own content per frame. `WindowRenderer` stays chat-side as content.
- The per-frame path (hit-test, draw roll-call, cursor pick) is a hard **zero-alloc**
  invariant — it runs on the render thread inside `SwapBuffers`; churn there is a
  user-visible stutter.
- A true input-blocking **modal** is deliberately deferred behind a capability flag until a
  consumer needs it.

## Amendment (2026-08-24) — the frame-lifecycle roll-call

This ADR replaced Chat's `OnSwap` roll-call for **windows**, but a second hardcoded roll-call for
**frame lifecycle** survived beside it: `UiHostService.OnSwap` names five concrete service types in
nine calls (lines 31, 64, 67-69, 77-81) across `Services.Render` and `Services.Perf`. That is the
same framework-depends-on-service inversion this ADR rejected in its Considered Options when it
declined to delegate input capture to `HotkeyInputService`.

The rule stands; only its application was incomplete. ADR 0014 finishes it with a frame-participant
registry that services join at `Initialize`, mirroring the window registry. The per-frame zero-alloc
invariant above is preserved — the participant lists are built at init and walked by index.
