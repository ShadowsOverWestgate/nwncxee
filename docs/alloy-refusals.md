# The Alloy refusal set

#216 (ADR 0016 step 7). **A frame is wholly Alloy's or wholly the engine's.** The verdict is latched once per
frame by `AlloyGate.OnFrameStart` (`managed/Alloy/Core/AlloyGate.cs`), from the present's `Start` phase, one
priority behind `RenderMode`'s override latch. Every render site reads `AlloyGate.Alloy`; nothing decides for
itself.

Before the cutover a mid-frame refusal stood the Alloy pass down and the engine's own frame was still there to
fall through to. After #215 there is nothing behind it: take `Scene::RenderSinglePass` away, then refuse, and
the world is bare clear colour under the 2D UI.

## Four verdicts, not a bool

ADR 0010's fourth bullet is paid here: **the detected capability tier and the manual override are separate
pieces of state.** `RenderMode` carries the override and only the override. `AlloyGate.Capable` /
`CapabilityReason` carry the device side. `/vanilla` reports both, so "I toggled and nothing changed" is
distinguishable from "this machine cannot run it".

| Verdict | Means | Player sees |
| --- | --- | --- |
| `Alloy` | Alloy draws this frame | — |
| `Choice` | the player or the config asked for the engine | the `/vanilla` echo |
| `Incapable` | device, driver or engine setting rules it out | an on-screen notice, once per transition |
| `Idle` | no world frame to draw (main menu, un-armed) | — |

## The set, recomputed for the post-cutover world

Asked in this order; the first that answers is the verdict's reason.

| # | Refusal | Verdict | Latched | Why here |
| --- | --- | --- | --- | --- |
| 1 | `AlloyRenderer` never armed (hooks did not install) | Idle | boot | nothing to take the frame with |
| 2 | `/vanilla` | Choice | per frame | the override, frame-atomic since #86 |
| 3 | `drawhooks` off in `sowx.cfg` | Choice | boot | nothing enrolls, so the world would be empty |
| 4 | `hdr=0` in `sowx.cfg` | Choice | boot | the Alloy stages have no display-space variant |
| 5 | no module loaded | Idle | per frame | GUI/menu scenes take `Scene::RenderOnlyGobs` anyway |
| 5b | `gl46=0` or `lockchat` in `sowx.cfg` | Choice | boot | an opt-out, not a device verdict — kept apart from `Fail`, which sets the same flag |
| 6 | no GL 4.6 share-listed context | Incapable | session | ADR 0010's macOS/old-driver case; the context creation *is* the check |
| 7 | a program did not build, or `glMultiDrawElementsBaseVertex`/`glBindBufferBase` missing | Incapable | session | `_progFailed` / `_batchUnavailable`, deliberately not cleared by a context loss |
| 8 | the HDR target latched a stand-down (`PostChain.Failed`) | Incapable | session | a post program that did not build, a missing entry point |
| 9 | the engine's gamma pipeline is off (`GetGlobalShaderParami(0) == 0`) | Incapable | **per frame** | it gates the Alloy *decodes*, which live in the stages; a live engine setting, so turning it back on recovers at the next latch |
| 10 | anything `AlloyGate.Incapable` latched mid-frame (below) | Incapable | until context loss | the residual, and it costs one frame |

## Who reads the verdict

`AlloyGate.Alloy`, and nothing else, gates: the shim's take-the-frame branch (`AlloyRenderer.Armed4Shim`),
enrollment, the #132 frame anchor, `PartTable.Live` (record/suppress), `CasterPass`, `ShadowService` (which is
what puts the engine's stencil shadows back) and `SceneInputs`. A frame the gate refused therefore records
nothing, suppresses nothing and bakes no shadow — the engine's own frame is intact, which is the whole point.

## Deleted by the cutover

- **"the engine renders to the default framebuffer"** (`PostChain.Precheck`). Under the shim the default
  framebuffer *is* the resolve target, every frame and on purpose — `Camera::InitializeFrameBufferResources`
  lives inside RSP and went with it. Keyed on `RspShim.InWindow` rather than on the `ownframe` setting: with
  the key on but the shim branch not taken, the engine is still drawing and the refusal is still right.

## The residual, and why it is not zero

Four refusals cannot be asked before the frame is taken. Three need *this frame's* viewport and attachments —
an offset viewport, an incomplete HDR framebuffer, and an engine depth attachment whose format the owned depth
texture cannot be matched to — and go through `PostChain.Refuse`. The fourth is a `glXMakeCurrent` that
refuses mid-frame, which `AlloyRenderer.NoFrameBehindUs` latches at the `GlContextService.Enter` call site;
without it a persistent switch failure would be a permanently bare world with no reason given.

All four call `AlloyGate.Incapable`, which does **not** move the current verdict — that frame is already
Alloy's — and makes the next one vanilla, with the reason on screen and in the log.

So the cost of a first discovery is **one clear-coloured frame, once per session**, instead of a permanently
empty world. Pre-cutover the same refusals stay per-pass and un-latched, which is what a GUI 3D panel and a
reflection subframe need (`StandDown`'s two kinds are still both load-bearing). `AlloyRenderer.ContextLost`
calls `AlloyGate.Forget`: those latches are facts about a target that no longer exists.

## Exercising the forced-failure paths

Neither needs fault-injection machinery, and none is shipped:

- **No 4.6 context.** `gl46=0` in `sowx.cfg` takes the same branch the device check takes (verdict `Choice`,
  reason named) and proves the whole fallback lands in vanilla. The `Incapable` wording differs; the path
  does not. A real device verdict is the same branch with `OptedOut` false.
- **Program link failure.** `/dumpshaders`, then break one stage in the dump directory (an undeclared
  identifier is enough) and relaunch. `PostChain.EnsurePrograms` latches `StandDown`, `PostChain.Failed`
  reads at the next latch, and the frame goes vanilla with the reason on screen.

Both are **live** checks. Off-device, `AlloyGate.SelfCheck` covers only the latch semantics: first reason
wins, `Forget` clears, and a latch taken mid-frame moves the next verdict rather than this one.

## Per-class stand-downs are not in this set

`GrassField`, `CasterPass`, `EnvProbe`, `SceneDepth` and `FrameSnapshot` each own a `StandDown`. Post-cutover
those degrade to "that class does not render", not to "the frame is not drawn" — punch-list territory (#217),
not a frame verdict.
