# The Alloy refusal set

#263 (ADR 0018 strand 1 step 8). **The renderer is chosen once per session, not once per frame.** The config
half is `alloy.enabled=0` in `cx.cfg`, read at `AlloyGate.Arm` (`managed/Alloy/Core/AlloyGate.cs`) before anything
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
`DrawFunnelService` both return early on `alloy.enabled=0`, before any Attach and any Install, which is what makes
vanilla mode actually vanilla rather than "the engine drawing from resources Alloy took away".

## Four verdicts, not a bool

ADR 0010's fourth bullet is still paid: **the detected capability tier and the requested mode are separate
pieces of state.** `AlloyGate.OptedOut` carries the request, `AlloyGate.Capable` / `CapabilityReason` the
device side. Bare `/alloy` reports both — so "I set it and nothing changed" is distinguishable from "this machine
cannot run it".

| Verdict | Means | Player sees |
| --- | --- | --- |
| `Alloy` | Alloy draws | — |
| `Choice` | the config asked for the engine | the `/alloy` report |
| `Incapable` | device, driver or engine setting rules it out | an on-screen notice, once |
| `Idle` | no world frame to draw (main menu) | — |

## The set

Asked in this order; the first that answers is the verdict's reason. `Choice` and `Alloy` **freeze** the
decision. `Incapable` does not — before the first Alloy frame it means "not yet" as often as "never" (the 4.6
context is created from a present, programs build on first use), and the cost of asking again is one branch on
a frame that is rendering vanilla anyway. It freezes the moment it stops being true.

| # | Refusal | Verdict |
| --- | --- | --- |
| 1 | `alloy.enabled=0` in `cx.cfg` | Choice |
| 2 | `alloy.drawhooks` not `batch` in `cx.cfg` | Choice |
| 3 | `alloy.hdr=0` in `cx.cfg` — the Alloy stages have no display-space variant | Choice |
| 4 | `alloy.gl46=0` in `cx.cfg` — an opt-out, kept ahead of the capability block so it is not reported as a device verdict | Choice |
| 5 | `AlloyRenderer` never armed (hooks did not install) — **after** every opt-out above, because each of them returns from `Initialize` before `_armed` is set, so asking this first reports the mechanism instead of the request | Idle |
| 5a | the `RenderSinglePass` shim never armed (an engine symbol or draw funnel did not resolve) — same placement, one layer down; since #458 there is no Alloy-inside-the-engine's-frame mode to fall back to | Idle |
| 6 | no module loaded | Idle |
| 7 | no GL 4.6 share-listed context — ADR 0010's macOS/old-driver case; the context creation *is* the check, and it also refuses a context missing the GL 4.5 DSA entry points Alloy creates and edits through (CX-44) | Incapable |
| 8 | a program did not build, `glMultiDrawElementsBaseVertex`/`glBindBufferBase` missing, or the stream ring's entry points missing or its persistent map refused (CX-45) | Incapable |
| 9 | the HDR target latched a stand-down (`PostChain.Failed`) | Incapable |
| 10 | the engine's gamma pipeline is off (`GetGlobalShaderParami(0) == 0`) — it gates the Alloy *decodes*, which live in the stages | Incapable |
| 11 | stock **Enhanced Lighting** is off (`graphics.lighting.enhanced`, read through `StockConfig` at the world edges, #571) — that option is the engine's vertex-lit, static-baked path (`g_bFullDynamicLighting = 0`, `FRAGMENT_LIGHTING 0`), and Alloy lights per fragment by construction; the player asked for a cheaper tier Alloy does not have | Incapable |

**Entries 8 and 9 answer here only in principle.** Programs build on first use, which is inside the first
frame the decision already froze on, so in practice those two flags latch *after* the freeze and reach the
player as a `Fail` (below) rather than as a verdict. Entry 10 is the opposite case and is genuinely live:
`PostChain.Precheck` re-asks the gamma parameter every pass, so a player who turns Gamma Correction off in
Video Options mid-session gets the `Fail` notice instead of a world silently rendering a gamma out.
Entry 11 behaves the same way one step coarser: the snapshot is retaken at every area edge, and a
player who turns Enhanced Lighting off mid-session gets the `Fail` notice at the next area.

**Entry 6 sits where it does for two reasons, and the second is the load-bearing one.** GUI/menu scenes take
`Scene::RenderOnlyGobs` anyway — but more importantly, every capability term below it is only answerable on a
*warm* client. The 4.6 context is created from a present inside a world frame, so asking entry 7 at the main
menu answers "no 4.6 context on this device" on every single boot. The first cut of #263 moved this term to
the end and put a spurious dropout notice on screen at startup; that is what the "an Idle or Incapable verdict
does not freeze" self-check now pins.

## After the decision: hard failure, not fallback

Three checks cannot be asked before the frame is taken — an offset viewport, an incomplete HDR framebuffer,
and an engine depth attachment whose format the owned depth texture cannot be matched to (all `PostChain`) —
plus a `MakeCurrent` that refuses mid-frame (`glXMakeCurrent`, or `wglMakeCurrent` on Windows) (`AlloyRenderer.NoFrameBehindUs`).

All four call `AlloyGate.Fail` — and so does every capability latch that can only be taken *after* the freeze:
`AlloyRenderer` when the multidraw or stream-ring entry points are missing, `StreamRing` when the driver refuses
its persistent map, its program builder when a program does not link, and `PostChain.StandDown`.

A ring that exists but cannot take a push (a region fence past `GpuRing.TimeoutNs`, a frame past the bounds the
region is sized from, a slice rebound after the ring left it) is not a `Fail`: that pass sits the frame out, the
first failure of each kind is logged in full (`[stream] UPLOAD FAILED`), the `[alloy]` line counts the rest, and
a red band marks it on screen -- top edge the draw slots (the colour frame did not draw), left the cull source
(drawn unculled), right the previous bones (no mover motion vectors). There is no fallback upload. Those flags are read by entries 8 and 9 above, but programs build on first use,
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

- **No 4.6 context.** `alloy.gl46=0` in `cx.cfg` takes the same branch the device check takes (verdict `Choice`,
  reason named) and proves the whole fallback lands in vanilla. The `Incapable` wording differs; the path does
  not.
- **Program link failure.** `/alloy shaderdump`, then break one stage in the dump directory (an undeclared
  identifier is enough) and relaunch.

Off-device, `AlloyGate.SelfCheck` covers the decision semantics: the config opt-out is a `Choice` and says
nothing about the device, the decision does not un-freeze, and a `Fail` taken afterwards moves neither this
verdict nor the next.

## Per-class stand-downs are not in this set

`GrassField`, `CasterPass`, `EnvProbe`, `SceneDepth`, `FrameSnapshot` and `PostChain` each own a `StandDown`;
`PostStack` (#375) stands down per STAGE, dropping the one that did not build and running the rest.
Those degrade to "that class does not render", not to "the frame is not drawn" — punch-list territory (#217),
not a frame verdict.

`LightmapBake` (#674) is the same shape one level finer, and every refusal lands on the real-time loop, which
is what an unbaked surface takes anyway: a missing entry point, too few texture units or a failed link latch
the pass off for the context (`StandDown.Fail`, every record stays unbaked); a page that will not allocate
refuses the pages past it (`Refuse`, the rest of the area stays unbaked and is counted as `past the cap`);
a rect whose chart range moved before its bake is dropped and its readers re-enrol; a light whose scratch
cube cannot be rendered this frame (no caster gather) leaves the step for the next frame. The period line
names each: `lightmap pages N/cap, bakes landed/pending/refused (… past the cap)`.

## The per-part census gates are not in this set either, and one is left standing

`AlloyCensus`'s gate chain names why a recorded part is not an Alloy candidate. #263 deleted the one that had
become a boot decision (`inactive` — `alloy.drawhooks=off` now stands the whole service down before the census can
run). The rest are **classification, not hand-back**: `OWNED`, `state`, `bucket` and `scene` say a part is
already taken, dead, in a class Alloy does not draw, or in a different `Scene`, and none of them is expressible
as a device capability.

What is left that genuinely *is* a per-part capability refusal — a part Alloy could draw and does not, which
under #236 is a hole rather than a vanilla draw — is: `stale-identity`, `fb/volumetric`, `vdo`,
`scene-tint`, `item` and `item-range`. Each is a gap in an earlier strand-1 step rather than work step 8
can do, and the `[funnels]` period line counts every one of them. **They are named here so the number is not
lost, not because collapsing them was skipped.**


## Every decline is a hole now, and each one's disposition (#285)

Before #264 an "Alloy declines this part" branch fell through to the engine's bucket walk and the part drew
vanilla. There is no walk. A decline is geometry that nothing renders, so each branch is either **owned** or
**counted under a name on a period line**. Nothing is left implicit.

### Owned since #285

- **The placeable highlight** (`gob+0x18`). `AlloyCensus.GobUnownable` refused it, and #128's reasoning —
  "SKIPPED, not revoked; the engine's own walk draws it highlighted, which is vanilla" — stopped being true
  the moment the walk went. Hovering a placeable made it vanish for the length of the hover while
  `Scene::DoHighlightGob` painted a glow over nothing. Alloy draws the part normally now and the engine's
  overlay composites the glow on top, which *is* vanilla composition. What is given up is only
  `PartTriMesh::Mat` @0x450960's highlight branch — `Disable(5)` plus `SetColor4f(1,1,1,alpha)`, the part
  going flat-white-unlit under the cursor.
- **The per-gob env override** (`gob+0x480`) was already owned by #121 and is *not* a flag:
  `RenderEnvironmentMapped` @0x451040 reads it as a `CAurTexture*` that REPLACES the material's env map
  (falling back to `mat+0x40`, then to `currentscene+0x1e8`), then calls `RenderFlat` anyway. Alloy's IBL
  probe supplies the texture. `AlloyCensus.GobHasEnvMap` sets `AlloyCandidate.Env`, exactly as a material
  env map does.
  *Known approximation, unowned*: that function also passes `part+0xa0 * gob+0x108` to
  `SetPixelShaderAmbientColor` — the reflection's strength. Alloy stages ambient.w as 1.

### Counted, not owned

- **The scene tint** (`gob+0x19`). `Mat`'s tint branch is `SetColor4f(currentscene+0x14c..0x154)` under
  `Disable(5)`: the surface replaced by one unlit colour, which is not arithmetic on any term the CxDraw
  record carries. Rare, and expressible only by a program variant nothing else would use.
  **Counted** as `scene-tint=` on the census line (enrollment) and `instance-state` on the `[alloy]` line
  (draw loop).
- **The enrollment reject tail.** Every gate already had a named total and every post-classification refusal
  a per-class total, and neither said *which gate ate which class* — "87 cutout / 87 water refused after
  classifying" could not be attributed without a Ghidra session. The census line now carries the cross:
  `[by gate: vdo/Water=87 item/Cutout=87 …]`, printed only where a cell is nonzero, over the five gates that
  refuse a classified part (`vdo`, `scene-tint`, `item`, `table-full`, `no-vao-slot`).
- **Unbuilt diffuse textures** (`null-tex … drawn white/frame` on the `[alloy]` line). Not a refusal at all
  any more: `AurTextureGetTID` @0x48e080 returns 0 for the engine's shared `m_pNullTexture`, `CAurTexture::Bind`'s
  IsNull branch binds 0 and the draw is still issued, so vanilla renders such a part blank white. Alloy now
  draws it on a 1x1 white owned texture (`OwnedTextures.WhiteTid`) instead of deleting it from the frame, and
  the record stays stale so a late GL id is picked up the frame it arrives. The retry drip (#284, see
  `docs/owned-resource-lifetimes.md`) is unchanged and still the repair. `no-gl-id` survives for the one case
  that is still a hole — the stand-in itself could not be minted.

### Accepted in writing: `sample_framebuffer 1` / draw bucket 5

`sample_framebuffer` has two values and they are different classes, not degrees of one:
`Scene::AddPartsTreeToDrawBuckets` @0x460880 routes **2** (water) to bucket 6 and **1** (refraction glass) to
bucket 5. #129 gave water an Alloy program that samples the one framebuffer copy Alloy already takes. Bucket 5
has none, and #264 removed the engine draw that used to cover it, so it is a hole.

**Accepted, not fixed.** The reasoning:

1. It is the *only* remaining class with no Alloy program at all, and the shape of the fix is known — the
   water pass is the precedent, since bucket 5 needs the same framebuffer copy and a refraction offset rather
   than a wave model. It is a ticket, not a gap in the frame architecture.
2. SoW content does not appear to use it: `sample_framebuffer 1` is a `.mtr` keyword
   (`SharedMaterial::ParseField` @0x4c6760, field `+0x1f4`), and the test area classified zero parts into it.
3. The hole is **measured, not assumed**. Both ends are instrumented and neither is optional:
   `fb-sampling/volumetric=` on the census line is how many candidates the material gate refused, and
   `sample-class=` on the `[scenewalk]` line is how many parts the engine's own classification put in draw
   bucket 5 this period — i.e. the size of the hole, read off the engine's answer rather than ours.
   `SceneWalk.CrossCheck` computes it against `Scene+0x208`, which holds the finished classification.

**Open acceptance box (needs a live run, cannot be ticked here):** find content that uses
`sample_framebuffer 1` — a refraction-glass window or a magical barrier — stand in front of it and read
`sample-class=` on the `[scenewalk]` line. It read 0 in the test area, which makes the hole *unmeasured*
rather than absent; a nonzero reading on such content is what turns this acceptance into a ticket.
