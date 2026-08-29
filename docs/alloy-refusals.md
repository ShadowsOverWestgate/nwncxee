# The Alloy refusal set

#263 (ADR 0018 strand 1 step 8). **The renderer is chosen once per session, not once per frame.** The config
half is `vanilla=1` in `sowx.cfg`, read at `AlloyGate.Arm` (`managed/Alloy/Core/AlloyGate.cs`) before anything
installs; the capability half is asked from the present's `Start` phase and frozen the moment it answers.
After that the only term that still moves is whether there is a world frame at all. Every render site reads
`AlloyGate.Alloy`; nothing decides for itself.

`RenderMode` is gone — it carried a live toggle, and there is no longer such a thing.

## Why session-atomic and not frame-atomic

#216 latched the verdict once per frame, on the argument that a frame must be wholly one renderer's. That was
right and is not what changed. What changed is what a *mode* costs.

Through M0–M3 Alloy drew inside the engine's frame from the engine's resources, so both renderers were warm at
all times and a flip was free. Strand 1 of M5 (#259–#262) made Alloy's residency **exclusive**: Alloy performs
the pool upload and the engine's is suppressed (#260), textures are uploaded into Alloy-minted handles at the
decode window (#259), PLT compositing is forced to the CPU so those textures reach the funnel at all (#277),
and skinned parts are posed by Alloy rather than by re-entering the engine's draw (#262). None of that can be
un-done at a frame edge — the content was loaded once, in one world's shape.

So the two resource worlds are never warm at once, and the mode is a **boot decision**. `AlloyRenderer` and
`DrawFunnelService` both return early on `vanilla=1`, before any Attach and any Install, which is what makes
vanilla mode actually vanilla rather than "the engine drawing from resources Alloy took away".

## Four verdicts, not a bool

ADR 0010's fourth bullet is still paid: **the detected capability tier and the requested mode are separate
pieces of state.** `AlloyGate.OptedOut` carries the request, `AlloyGate.Capable` / `CapabilityReason` the
device side. `/vanilla` reports both — so "I set it and nothing changed" is distinguishable from "this machine
cannot run it".

| Verdict | Means | Player sees |
| --- | --- | --- |
| `Alloy` | Alloy draws | — |
| `Choice` | the config asked for the engine | the `/vanilla` report |
| `Incapable` | device, driver or engine setting rules it out | an on-screen notice, once |
| `Idle` | no world frame to draw (main menu) | — |

## The set

Asked in this order; the first that answers is the verdict's reason. `Choice` and `Alloy` **freeze** the
decision. `Incapable` does not — before the first Alloy frame it means "not yet" as often as "never" (the 4.6
context is created from a present, programs build on first use), and the cost of asking again is one branch on
a frame that is rendering vanilla anyway. It freezes the moment it stops being true.

| # | Refusal | Verdict |
| --- | --- | --- |
| 1 | `vanilla=1` in `sowx.cfg` | Choice |
| 2 | `drawhooks` off in `sowx.cfg` | Choice |
| 3 | `hdr=0` in `sowx.cfg` — the Alloy stages have no display-space variant | Choice |
| 4 | `gl46=0` or `lockchat` in `sowx.cfg` — an opt-out, kept ahead of the capability block so it is not reported as a device verdict | Choice |
| 5 | `AlloyRenderer` never armed (hooks did not install) — **after** every opt-out above, because each of them returns from `Initialize` before `_armed` is set, so asking this first reports the mechanism instead of the request | Idle |
| 6 | no module loaded | Idle |
| 7 | no GL 4.6 share-listed context — ADR 0010's macOS/old-driver case; the context creation *is* the check | Incapable |
| 8 | a program did not build, or `glMultiDrawElementsBaseVertex`/`glBindBufferBase` missing | Incapable |
| 9 | the HDR target latched a stand-down (`PostChain.Failed`) | Incapable |
| 10 | the engine's gamma pipeline is off (`GetGlobalShaderParami(0) == 0`) — it gates the Alloy *decodes*, which live in the stages | Incapable |

**Entries 8 and 9 answer here only in principle.** Programs build on first use, which is inside the first
frame the decision already froze on, so in practice those two flags latch *after* the freeze and reach the
player as a `Fail` (below) rather than as a verdict. Entry 10 is the opposite case and is genuinely live:
`PostChain.Precheck` re-asks the gamma parameter every pass, so a player who turns Gamma Correction off in
Video Options mid-session gets the `Fail` notice instead of a world silently rendering a gamma out.

**Entry 6 sits where it does for two reasons, and the second is the load-bearing one.** GUI/menu scenes take
`Scene::RenderOnlyGobs` anyway — but more importantly, every capability term below it is only answerable on a
*warm* client. The 4.6 context is created from a present inside a world frame, so asking entry 7 at the main
menu answers "no 4.6 context on this device" on every single boot. The first cut of #263 moved this term to
the end and put a spurious dropout notice on screen at startup; that is what the "an Idle or Incapable verdict
does not freeze" self-check now pins.

## After the decision: hard failure, not fallback

Three checks cannot be asked before the frame is taken — an offset viewport, an incomplete HDR framebuffer,
and an engine depth attachment whose format the owned depth texture cannot be matched to (all `PostChain`) —
plus a `glXMakeCurrent` that refuses mid-frame (`AlloyRenderer.NoFrameBehindUs`).

All four call `AlloyGate.Fail` — and so does every capability latch that can only be taken *after* the freeze:
`AlloyRenderer` when the multidraw entry points are missing, its program builder when a program does not link,
and `PostChain.StandDown`. Those flags are read by entries 8 and 9 above, but programs build on first use,
inside the very frame the decision froze on, so nothing re-reads them — without the `Fail` call a broken
program is a permanently bare world with no line in the log.

**A `Fail` does not move the verdict.** There is nothing behind an Alloy frame
(#236: under the shim an unowned part is *nothing*) and no way to warm the engine's resource world
mid-session, so the honest report is a failure and not a fallback: one log line, one on-screen notice naming
the reason and the restart, and the mode stays where it booted. First reason wins.

## Who reads the verdict

`AlloyGate.Alloy`, and nothing else, gates: the shim's take-the-frame branch (`AlloyRenderer.Armed4Shim`),
enrollment, the #132 frame anchor, `PartTable.Live`, `CasterPass`, `ShadowService` (which is what puts the
engine's stencil shadows back) and `SceneInputs`.

## Exercising the forced-failure paths

Neither needs fault-injection machinery, and none is shipped:

- **No 4.6 context.** `gl46=0` in `sowx.cfg` takes the same branch the device check takes (verdict `Choice`,
  reason named) and proves the whole fallback lands in vanilla. The `Incapable` wording differs; the path does
  not.
- **Program link failure.** `/dumpshaders`, then break one stage in the dump directory (an undeclared
  identifier is enough) and relaunch.

Off-device, `AlloyGate.SelfCheck` covers the decision semantics: the config opt-out is a `Choice` and says
nothing about the device, the decision does not un-freeze, and a `Fail` taken afterwards moves neither this
verdict nor the next.

## Per-class stand-downs are not in this set

`GrassField`, `CasterPass`, `EnvProbe`, `SceneDepth`, `FrameSnapshot` and `PostChain` each own a `StandDown`.
Those degrade to "that class does not render", not to "the frame is not drawn" — punch-list territory (#217),
not a frame verdict.

## The per-part census gates are not in this set either, and one is left standing

`AlloyCensus`'s gate chain names why a recorded part is not an Alloy candidate. #263 deleted the one that had
become a boot decision (`inactive` — `drawhooks=off` now stands the whole service down before the census can
run). The rest are **classification, not hand-back**: `OWNED`, `state`, `bucket` and `scene` say a part is
already taken, dead, in a class Alloy does not draw, or in a different `Scene`, and none of them is expressible
as a device capability.

What is left that genuinely *is* a per-part capability refusal — a part Alloy could draw and does not, which
under #236 is a hole rather than a vanilla draw — is: `stale-identity`, `fb/volumetric`, `vdo`,
`gob-unownable`, `item` and `item-range`. Each is a gap in an earlier strand-1 step rather than work step 8
can do, and the `[funnels]` period line counts every one of them. **They are named here so the number is not
lost, not because collapsing them was skipped.**
