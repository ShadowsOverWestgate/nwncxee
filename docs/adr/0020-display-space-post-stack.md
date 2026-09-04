# 0020 — Post-processing is a display-space stage stack after the resolve

Status: accepted (#375, 2026-09-04). Fills the "post stack the resolve plugs into" slot ADR 0016
filed as M5 work. Does not move ADR 0011's frame graph order; it appends one slot.

## Context

Alloy's `PostChain` did bloom and the tonemap resolve and nothing else. The engine's own
post-processing — `Camera::DoFrameBufferEffects`, the `fsfbpostpr` chain the client video options
drive — lived inside `Scene::RenderSinglePass` and went with it at the M4 cutover (#217's punch
list, #352). One of those options is a correctness bug rather than a lost look: `CClientOptions::
SetGamma` runs `inc_postpr_gam`'s `pow(c, Gamma/2.2)` over the finished frame whenever the gamma
option is not 2.2, and Alloy never applied it, so a client with a non-default gamma saw a
constant-factor-wrong world for months and the lighting was retuned against it.

Three facts from the binary shape the design (all decompiled, none guessed):

- The engine's post chain runs on the **display-space** frame, after every world draw and before the
  UI. Its inputs are the finished colour and the depth (`inc_framebuffer`'s `texFBColor` /
  `texFBDepth`).
- The option gate is one global: `ConfigureFBEffectPostProcessing` publishes the option mask as
  global int param 2, which the shader preamble bakes as `POSTPROCESSING_TYPES_ENABLED`; bit 0x10 is
  gamma, set iff |g − 2.2| ≥ 9e-5. The value is global float param 0x0b, uploaded as the `Gamma`
  uniform by `GLRender::ConnectShaderData`/`SetUniforms`. The walk itself is gated on the
  `enableFBeffects` console variable.
- The stages are independent full-screen passes over the previous output. Nothing in the chain needs
  the linear frame.

## Decision

**Post-processing in Alloy is an ordered stack of display-space stages that runs after the resolve,
in its own frame-graph slot (`PassId.PostStack`), on a ping-pong pair of RGBA16F targets.**

- The resolve stays the frame's one colour-space conversion and the frame's one dither. When at
  least one stage is live, the resolve writes into the stack's first target with the dither off, and
  the **last live stage** writes into the engine's target with the dither on. The frame is quantised
  to 8 bits exactly once, wherever that write happens.
- A stage is one table entry: a resref, its embedded fragment text, a per-frame `Active` read from
  the engine's own globals, and its uniform. The stage contract is `uSrc` (previous output, unit
  `PostHdr`), `uDepth` (the frame's depth, unit `PostBloom`), `uDither`, and a `vec4 SowxStage(vec4)`
  the shared main calls. No stage encodes or decodes (`ShaderContract.DisplaySafe`).
- **Stand-down is per stage, never per frame.** A stage that does not build is dropped with a named
  log line and the rest run; an empty stack is the resolve writing straight to the engine, byte for
  byte what shipped before this ADR. Nothing in the stack can take the Alloy frame to vanilla.
- **Gamma is Alloy-owned, not hosted.** The stock stage is one `pow`; Alloy carries the same
  expression with the same exponent, gated on the same mask bit and console variable, reading the
  same param — so the client slider moves the Alloy frame live and 2.2 is the identity, as it is for
  the engine. Hosting `inc_postpr_gam` through the #347 bridge would have bought the same three lines
  at the price of the whole `fsfbpostpr` environment; that price is #352's to pay, for the stages that
  actually need it.
- **A stage may own passes of its own.** The aa stage (#377, SMAA 1x) draws its edge and weight
  passes into its own targets from a `Prepare` hook the stack calls ahead of the stage draw, and
  answers a `Ready` hook at `Begin` so a stage that cannot run this frame drops out before the
  last-stage accounting. The stage program itself keeps the contract; the extra passes are its
  business. This is the shape for every multi-pass stage to come (SSAO's blur, DOF's CoC).
- Intermediates are RGBA16F for **precision**, not headroom: the image is display-space, but an
  8-bit intermediate would quantise every stage and the dither would be applied to already-banded
  input.

## Consequences

- #352 (hosting the engine's `fsfbpostpr` chain) is one more stage in this table, with the
  `inc_framebuffer` inputs it needs already on the two units. SSAO, DOF, sharpen and the SoW status
  overlay all land there; none of them needs a second mechanism.
- The client's Sharpen option stays inert until #352; `/knobs` says so and no longer lists gamma among
  them. SSAO is served too, but not here: it is a lighting input computed before the colour pass
  (ADR 0021, #379).
- The client's Anti-Aliasing option is served the same way as gamma (#377): its sample count picks
  the SMAA preset (2 MEDIUM, 4 HIGH, 8/16 ULTRA) and the stage runs last, so it smooths what the
  frame actually shows. Not MSAA (every owned attachment would multiply; cutouts stay aliased), not
  TAA (jittered projection in every owned vertex stage under `invariant gl_Position`, plus motion
  vectors -- its own ticket).
- `/post stack 0` bypasses the whole stack for a live A/B against the bare resolve; `/post` reports
  the mask, each stage's state and the live gamma value.
- The engine's residual overlays that draw after the Alloy bracket (selection highlight, trigger
  wireframes, lens flares, debug lines) are not gamma-corrected, where under vanilla they were. They
  are UI feedback and debug surfaces, and the highlight is already Alloy's in its second mode.
- Live acceptance for #375 (the percentile transfer against vanilla, the slider moving both frames the
  same way) is still xtul's to run; the offline checks prove the maths and the gate, not the look.
