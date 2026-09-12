# ADR 0021: Ambient occlusion is a lighting input, not a post stage

Status: accepted (2026-09-04). Issue #379.

## Context

The client's SSAO option (`graphics.fbo.ssao.enabled`) is framebuffer effect 0: bit 0 of
`Camera::m_nFramebufferEffectsEnabled`, set by `CClientOptions::SetEnableFBEffectSSAO`, fed by
`ssao_intensity` (global float param 1, 0.5 stock). Vanilla's stage (`inc_postpr_ssao.shd`) runs
over the finished frame and multiplies the composite -- sun, lamps and ambient alike -- by an
occlusion estimate taken from the depth buffer. Under Alloy that effect never ran (the FB-effects
chain went with `Scene::RenderSinglePass`), so the option was inert.

The post stack (ADR 0020) could host it as one more display-space stage, byte for byte what
vanilla does. That would reproduce vanilla's mistake: occlusion darkens light that reaches the
point directly, which is physically wrong and reads as dirt in the corners of a sunlit room.
Alloy already has the right place for the term: the lit stage scales its AMBIENT term by a
material AO map (#146, `occl`) and nothing else.

## Decision

**Screen-space ambient occlusion is computed before the colour pass and consumed by the lit stage
as a scale on the ambient term, beside the material's AO map.** `PassId.AmbientOcclusion` sits
between the depth prepass and the opaque pass; the lit stage reads the R8 result at
`gl_FragCoord` on unit `TextureSlots.Ssao` when `uSsaoOn` is set, and multiplies it into `occl`.

- The estimator is horizon-based (GTAO, Jimenez et al. 2016, the XeGTAO reference's structure):
  per screen-space slice the two maximum horizons, integrated against the normal projected into
  the slice, falloff over the outer 40% of the radius. Normals come from depth. A 4x4 ordered
  pattern rotates slices and steps and a depth-aware 4x4 blur folds it away: no temporal history,
  so nothing to reproject.
- The option is the gate, as for gamma and anti-aliasing: the effect bit under `enableFBeffects`,
  read per frame. `ssao_intensity` scales the visibility exponent, stock 0.5 = 1x. The radius,
  exponent, slice and step counts are Alloy's (`/ao`), because vanilla's `ssao_radius` is in its
  own screen-space units and means nothing to a world-space estimator.
- The probe capture never reads it: `uSsaoOn` is uploaded as 0 while capturing, since a cube face has
  no screen. The lit program rebinds the texture at every program bind, because the frame snapshot
  takes the same unit at `FbCopy` and lit additive draws follow it.
- Stand-down is per pass: a program that fails to build drops the pass with a named line and the
  ambient term keeps the material AO alone. Nothing here can take the frame to vanilla.

## Consequences

- Strictly more than vanilla's: occlusion never touches direct light, the material AO map and the
  screen-space term compose, and the cost is 18 depth fetches a pixel plus a 16-tap blur at
  default quality (`/ao dirs`, `/ao steps` trade it).
- Grass and water do not read it: grass has its own ambient path and water is a later pass with
  a different program. (The grass stage did take the fetch for a while; #478 removed it -- blades
  are not in the prepass depth, so the pixel's occlusion was the surface behind the blade and the
  scene showed through the field. The root blend grounds a blade instead.)
- `/draw viz 8` (the `occl` view) now shows the screen-space term too.
- Half-resolution with a bilateral upsample is the known upgrade if the pass shows in the GPU
  line at 4K; the ceiling is noted on the pass.

## Amendment (2026-09-04): the ambient-only rule is invisible indoors

"Strictly more than vanilla's" was wrong, and live measurement is what said so. The decision above
gives occlusion to `ambient = uAreaAmbient * matAmbient * mix(0.15, 1.0, sunRaw)` alone. In an
interior `uAreaAmbient` is near black -- the room is lit by point lights, which are direct -- so the
term the effect scales is already zero and the frame does not move, at any radius or power.
`/draw viz 34` (the ambient term as it enters `total`) read black across the whole screen, and
`/light ambient 20` was what made the effect appear. Vanilla's post-multiply, physically wrong,
was visible there; this was not.

**`/ao direct` (0..1, default 1) carries the occlusion onto the direct terms as well**, through one
`dirOcc = mix(1.0, occl, uAoDirect)` on `diffuse` and `specLight` in the lit stage. The probe term
picks it up through `diffuse`. `/ao direct 0` restores the original rule for an area bright enough
to show it.

The ambient path is unchanged and still takes the full factor: at the default a fragment's ambient
is occluded once by `occl` and its direct light once by `dirOcc`, which are the same number.
