# The shader override contract

sow-client-extender draws most of the world with its own GLSL. Every stage of that
pipeline ships **embedded in the extender binary**, and every one of them can be
replaced at runtime by a `.shd` resource of the same name — a customization surface
for people who want the look to be theirs.

This document is the contract that surface comes with: what an override is, what the
seventeen stages are, and precisely what the extender refuses and why.

## What an override is, and what it is not

An override is a **customization**. You take the stage this build compiles, edit it,
and place it where your client can see it. The extender picks it up instead of its own
copy.

An override is **not a delivery channel.** Shipping `sowx_*.shd` inside a hak, an
`override/` directory or an NWSync manifest as a way of getting extender shaders to
players is **not supported** — see [ADR 0012](adr/0012-shaders-ship-embedded-never-as-content.md).
The reason is drift, and it is not hypothetical:

- A shipped `.shd` is a **copy of a layout that lives in C#.** When the per-draw record
  grew in #120, every shipped copy went on reading each draw out of its neighbour's slot
  and the world rendered black and streaked, with nothing in the log.
- **On a server, the hak wins** over a local `override/` directory. A hak that has not
  caught up with the extender is that failure on live content while a singleplayer test
  looks correct.

So: the extender ships the defaults, you replace them locally if you want to. Content
that carries them is content that will one day be older than the client reading it.

## Getting a starting point

Type **`/dumpshaders`** in-game. It writes every overridable stage as an editable `.shd`
into `cx_shader_dump/` inside the client's own user directory (next to `nwn.ini`).

What you get is the stage **this build compiles** — dumped from the same text the
renderer uses, so it is current by construction rather than by somebody remembering to
update it. Each file opens with a short header naming the constraints that stage carries
and pointing back here.

There is also a headless trigger for offline work: put `dumpshaders=1` in
`sowx.cfg` in the same directory, and the dump runs at startup.

### The workflow

1. `/dumpshaders`.
2. Edit `cx_shader_dump/<resref>.shd`.
3. Place it where your client's resource chain will find it — an `override/` directory
   for local play, a hak for a module you control.
4. Restart the client and read `~/sowx.log`. An override that loaded says so:
   `shader override: <resref>.shd from resman (N bytes)`.

### Sizing tokens

Dumped files contain `$CAP$`, `$GROUP$`, `$SRGB$` and `$BAND$` unexpanded. **Leave them
that way.** The extender expands them when it compiles, for an override exactly as for
its own embedded default, so a file that keeps the tokens stays correct when those sizes
change on the C# side. Freezing today's numbers into your copy is how it goes stale.

## The seventeen stages

| resref | stage | what it does |
| --- | --- | --- |
| `cx_own_vs` | vertex | Unlit ("fullbright") Alloy geometry: projects the batch, reads the per-draw record. |
| `cx_own_fs` | fragment | Unlit shading — texture, per-draw tint, alpha cutout. |
| `cx_ownlit_vs` | vertex | Lit Alloy geometry, including the skinning branch for creature parts. |
| `cx_ownlit_fs` | fragment | The main lit shader: clustered local lights, sun cascades, PBR material synthesis, key light and fog. The big one. |
| `cx_water_vs` | vertex | Water surface. |
| `cx_water_fs` | fragment | Water shading: stock `fswater` on Alloy's lights, shadows, keyhole and fog. |
| `cx_grass_vs` | vertex | Instanced grass blades, wind animation. |
| `cx_grass_fs` | fragment | Grass shading. |
| `cx_cluster_cs` | compute | Builds the light cluster grid the lit and water stages read. |
| `cx_cast_vs` | vertex | Shadow-map depth bake: projects casters into a cascade or cube face. |
| `cx_cast_fs` | fragment | The caster pass's fragment stage. Depth-only, and deliberately inert. |
| `cx_post_vs` | vertex | Fullscreen triangle shared by the whole post chain. |
| `cx_bloomdn_fs` | fragment | Bloom downsample. |
| `cx_bloomup_fs` | fragment | Bloom upsample. |
| `cx_resolve_fs` | fragment | The resolve: tonemap, dither, and the one stage that encodes to display space. |

One more file appears in the dump: `cx_ownlit_fs.bindless.shd`. It is **reference
only** — the lit stage after the extender's own bindless rewrite. The dot makes it
unloadable as a resref. Override `cx_ownlit_fs` and the rewrite is applied to whatever
you supply.

## The guards

An override that fails to compile or link costs you a log line and nothing else: the
extender rebuilds from its embedded default.

The dangerous overrides are the ones that build **perfectly** and are still wrong,
because they were written against an older version of a contract the C# side still
holds. Those failures are silent — a black world, missing shadows, a washed-out frame,
and an empty log. The guards exist to turn each of them into a log line.

Every guard works the same way: it inspects the text that **actually compiled**, and if
it disagrees, the program is thrown away and rebuilt from the embedded stage. The frame
is then correct and merely un-restyled, and you get a line naming what to fix.

Guards match **named terms**, not whole files. You can restyle, reformat and rename
freely around them; what you cannot do is drop the term the rest of the pipeline is
built on.

### Record — the per-draw SSBO layout

**Applies to:** `cx_own_vs`, `cx_ownlit_vs`, `cx_ownlit_fs`, both `cx_water_*`
(as `SowxDraw`); `cx_cast_vs` (as `SowxCaster` *and* `SowxDraw`).

**Rejects:** a `struct SowxDraw` declaring a different number of trailing `vec4` fields
than the C# stride stages. Counted, not compared, so renaming or reformatting the fields
is fine. A stage that declares no record at all cannot disagree and passes.

`cx_cast_vs` (#302) carries three terms of its own: `struct SowxCaster` must be exactly
two `vec4`s and no `mat4` (the 2026-08-30 misrender was precisely a leftover `mat4` --
a 96-byte GPU stride over a 32-byte stage, every caster baking at a random part's
matrix); the stage must read Alloy's record through the ownership slot, spelled
`uDraw[int(uCaster[gl_BaseInstance].aux.x)]` verbatim; and it must keep the skinning
branch (`uBone`). Lose the branch and every creature casts its bind pose while it
animates -- it links fine and says nothing.

**Why:** the record is a contract with a C# constant. Declare too few fields and every
draw reads its neighbour's model matrix and material — a black, streaked world, silent.

**You see:** `OwnedDrawService: a content .shd for the <program> program disagrees with
the pass ... Ignoring it and building the embedded stages, which agree.` The caster pass
prints its own equivalent.

### Cutout — per-draw alpha

**Applies to:** the `cx_own_*` and `cx_ownlit_*` pairs.

**Rejects:** a fragment stage without `uAlphaCut`, or one that reads `vAlpha` without a
vertex stage that supplies it.

**Why:** a stage written before per-draw alpha links cleanly — `glUniform1f` on a
missing uniform's `-1` location is a legal no-op — and renders every cutout and blended
surface solid.

**You see:** the same `RebuildEmbedded` line as Record; it names both possibilities.

### Skin — creature skinning

**Applies to:** `cx_own_vs`, `cx_ownlit_vs`, and only while skinned parts are armed.

**Rejects:** a vertex stage with no `SOWX_FEAT_SKIN` branch.

**Why:** a vertex stage predating skinning links perfectly and the missing bone
attributes are simply disabled — so a robe renders in its **bind pose** at the node
matrix, silently.

### Linear — do not encode

**Applies to:** every Alloy fragment stage except the resolve — `cx_own_fs`,
`cx_ownlit_fs`, `cx_water_fs`, `cx_grass_fs`, `cx_bloomdn_fs`, `cx_bloomup_fs`.

**Rejects:** any use of `SowxRevert` or `SowxSoftClip`.

**Why:** the Alloy frame is linear end to end and the resolve encodes it once. A stage
that encodes as well hands display-space values to a target that gets encoded a second
time. The whole frame washes out, and nothing says so.

**You see:** the rebuild line from the owning pass — `PostChain: the <what> override is
refused ...` for the bloom stages, `GrassField: grass override still encodes to display
space ...` for grass.

### Resolve — the same guard from the other end

**Applies to:** `cx_resolve_fs`, the one stage that must encode.

**Rejects:** a stage that calls `SowxLinear` (decoding a target that is already linear —
the frame comes out a gamma dark), or one with no `SowxEncode` at all (the frame stays
linear and washes out).

Tonemap and dither around those terms as you like. The colour-space decision is not
yours to change; the rest of the pipeline is built on it.

### Caster inertness

**Applies to:** `cx_cast_fs`.

**Rejects:** `discard` or a written `gl_FragDepth`.

**Why:** the caster pass renders into a depth-only FBO, so those two are its entire
silent-failure surface — a `discard` means the caster casts no shadow at all, and a
written `gl_FragDepth` displaces the depth the map is compared against into acne or
peter-panning. Neither errors, neither logs.

`cx_cast_vs` additionally must keep `uBone`: without the skinning branch every creature
casts its bind pose.

### Depth — the prepass expression

**Applies to:** `cx_own_vs` and `cx_ownlit_vs`. Water is exempt — it tessellates,
so its evaluation stage is what projects, and it does not take the prepass.

**Requires, character for character:**

```glsl
invariant gl_Position;
...
gl_Position = uVP * d.model * vec4(aPos, 1.0);
```

**Why:** the depth prepass writes these depths and the colour pass re-tests them with
`LEQUAL`. `invariant gl_Position` only guarantees a bit-identical result for the *same
sequence of operations* — `uVP * (d.model * pos)` is a different sequence, and a ULP of
drift the wrong way punches speckled holes in lit surfaces.

**This one does not rebuild.** An override that rewrites the expression is accepted and
the **depth prepass is turned off** for the session, which is correct but slower. You
see:

```
OwnedDrawService: a content vertex .shd does not carry `gl_Position = uVP * d.model *
vec4(aPos, 1.0);` under `invariant gl_Position;` -- no depth prepass
```

If you want the prepass back, keep the expression.

### Not guarded

`cx_post_vs`, `cx_grass_vs` and `cx_cluster_cs` carry no guard.

The first two read no per-draw record and write no colour; an interface break in them
fails the **link**, which the build-failure fallback already refuses with a log line.
`cx_cluster_cs` does read a C#-strided record, but as a bare `vec4 uLights[]` with the
stride open-coded on both sides rather than as a struct — the counting guard cannot reach
it, and a text match on the indexing arithmetic would forbid restyling. It is stated here
rather than guessed at: an override of the cluster build that gets the stride wrong will
fail silently.

## Where this is enforced

The guard predicates live in `managed/Alloy/AlloyRenderer.Shaders.cs`
(`RecordSafe`, `CutoutSafe`, `SkinSafe`, `LinearSafe`, `ResolveSafe`, `DepthSafe`) and
`managed/Alloy/Pass/CasterPass.cs` (`CastVsSafe`, `CastFsSafe`). Each is asserted
in both directions by the SelfCheck suites — the embedded stages pass, and deliberately
broken text is refused — and `run-demo.sh` fails the build if any suite fails. The one
exception is Skin, whose refusing direction cannot be asserted there: it short-circuits
on an arming flag that is set after SelfCheck runs.

`managed/Alloy/ShaderStages.cs` is the authoritative composition table: which
constants concatenate into which resref, and the header each dumped file carries. The
offline checker `check-shaders.py` reads that table rather than restating it.
