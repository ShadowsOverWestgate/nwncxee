# Engine layout: struct offsets and vtable slots across platforms

`docs/windows-symbols.md` answers *where is this function*. This file answers
*where is this field*, which the Windows port (#429) turned into a per-platform
question: the Linux client is built by GCC on the Itanium C++ ABI, the Windows
client by MSVC, and the two disagree about where a field sits inside a class.

`managed/Framework/EngineLayout.cs` is the single table. Every offset the extender reads
is declared there and read at the use site as a plain field. Vtable **slots** ride it too;
why they diverge, and the handler-signature audit that goes with them, is
`docs/engine-abi.md`.

## The three forms, and there is no fourth

| Form | Means | Counted |
|---|---|---|
| `Diverges(cls, field, linux:, win:)` | measured on both, they differ | no |
| `Same(cls, field, off)` | measured on both, they agree | no |
| `Unresolved(cls, field, linux)` | Linux value; **nobody has looked on Windows** | yes |

The point is that "verified identical" and "never looked" stop being the same
bare `const`. `Unresolved` is usable on Linux and unreachable on Windows —
`EngineLayout.Require` refuses to let a feature start there while the count is
above zero — so the count is the migration's completion counter. It is counted on
**both** platforms, because the migration is worked on Linux and a counter that
only moves on Windows is invisible where the work happens; only the gate is
Windows-only. `/knobs` prints it.

Every row names the **engine class** it belongs to. That is not decoration: it
is the key #431's Ghidra extractor pairs on, and the unit #430 measures. A class
diverges in 2–3 contiguous segments with class-specific boundaries, so a value
is anchored on a **function pair** — the ctor plus one accessor per segment —
never on a field read in isolation.

## Build key

Offsets are only valid for the build they were measured against, so the table is
keyed `(os, build)` and the key is the **linker's build identity**, not a content
hash: a rewrapped Steam/GOG/Beamdog copy of the same build is the same client.

| | |
|---|---|
| Build revision | `26c6e57364a45246e3b58e4f2c49366df72d15db` (Mon Oct 6 2025) |
| Linux key | `.note.gnu.build-id` of `nwmain-linux` — `3e2500a82e0c32008d24c3e74bbff3021da5e1b7` |
| Windows key | CodeView PDB GUID + age from `nwmain.exe` — `29431F05E0F2461A8BECCFC9E80D8F27` age `1` |

Read at runtime from the running program: `/proc/self/exe` `PT_NOTE` on Linux,
the loaded image's `DataDirectory[6]` debug directory on Windows. A mismatch is
**rot**: the **whole extender** stands down, both platforms, with one
`ExtenderLog.Always` line naming the build that was actually loaded (`#457`).
The gate is `EngineLayout.KeyOk`, asked once in `ServiceHost.Run` **before
discovery** — not before `Initialize`, because constructing a service runs its
type initializer and a type initializer is enough to latch engine state. Zero
services constructed, zero hooks installed, the game runs vanilla. On Windows
the same text also goes to `SymbolMap.RotNotice`'s message box, because a
Steam-launched `version.dll` proxy has no log the player will read.

No partial behaviours: a feature that asks neither gate used to keep running
against a client it was never measured against, which is rot arriving as garbage
in some features instead of as a refusal. On Linux the lock itself is new
(`#432`), so a client patch now costs the chatbox, combat text and Alloy, not
just nameplates — the intended trade against rendering garbage off a moved
field, and the reason adding a tested build's key unblocks every feature at once.

There is exactly one row per OS. A second tested build gets its own pinned key
and its own values; hunting offsets for a build nobody has tested is not work.

**Two checks, one policy.** `SymbolMap` still pins the PE `TimeDateStamp` for its
RVAs; this pins the build key. Since `#457` the stamp can never be the *first*
thing to notice rot — nothing gets as far as resolving a symbol — so the two
locks were settled without collapsing either: `KeyOk` is the policy, the stamp
stays the RVA-path guard.

## Probes

A resolved offset can carry an assertion about live memory, checked once at the
first sighting of an instance, so a wrong value **stands the feature down**
instead of rendering garbage — a probe that only logs is not a stand-down, so the
caller latches the failure and every read below it answers "nothing". `EngineLayout.Probe(cls, ptr, log)`; the caller holds its own
one-shot latch, because a name lookup does not belong in a frame loop.

| Predicate | Asserts |
|---|---|
| `Byte01` | the dword is 0 or 1 — a boolean field. A wrong offset lands in a pointer or a float. |
| `VtblSlot` | `*(*obj + off)` is a code address inside the client image. |

The vocabulary grows as offsets that can carry an assertion arrive; most cannot,
and a probe that cannot fail is not a probe. `EngineLayout.SelfCheck` proves the
failing case by feeding a `Byte01` offset a 2.

## Table

| Class | Field | Linux | Windows | How anchored |
|---|---|---|---|---|
| `CNWCObject` | `type` | `0x0c` | `0x0c` (same) | the byte both `ShowFeedbackBubbleText` overrides switch on |
| `CNWCObject` | `vtbl.GetPortrait` | `0x1d8` | `0x1d0` | #420: slot counted in the Windows class, not reused from Linux |
| `CNWCCreature` | `plated` | `0x468` | `0x448` | top of `ShowFeedbackBubbleText` — Linux @`0x71a81e`, Windows @`0x80b240` |
| `CNWCPlaceable` | `plated` | `0x218` | `0x1f8` | same, Linux @`0x7ea8b8`, Windows @`0x871b60` |
| `Part` | `vtbl.AsPartTriMesh` | `0x60` | `0x58` | #433: both vtables dumped, `tools/abi/vtable-slots.py` |
| `Part` | `vtbl.AsPartBeam` | `0x90` | `0x88` | same |
| `Part` | `vtbl.PostProcess` | `0xe0` | `0xd8` | same |
| `CAurTexture` | `vtbl.AsCAurTextureBasic` | `0x10` | `0x08` | same |
| `CAurTexture` | `vtbl.GetReferencedTexture` | `0x18` | `0x10` | same |
| `VertexPrimitive` | `vtbl.SetupArrays` | `0x38` | `0x30` | same |
| `CAurBehavior` | `vtbl.AsBehaviorCameraFollow` | `0x48` | `0x40` | same |
| `CycleTIDTextureController` | `vtbl.Control` | `0x10` | `0x08` | same |

### Alloy: the Scene walk (#436)

124 rows, 33 of them `Unresolved`. Measured 2026-09-11 by running
`tools/ghidra/FieldDump.java` over both programs and diffing with
`tools/ghidra/field-diff.py`; reproduce with the command block at the end of
`docs/research/windows-divergence-shape.md`, class list

    Scene Gob Part PartTriMesh PartDanglyMesh PartSkin PartLight MdlNode MdlNodeTriMesh
    MdlNodeDanglyMesh MdlNodeSkin VertexPrimitive VertexPrimitiveDangly VertexDataObject
    Material SharedMaterial LightManager CAurTextureBasic TileData BSPNode Camera

Per-row provenance is the XML doc on the row itself. The verdict per class:

| Class | Verdict | Anchor |
|---|---|---|
| `Scene` | identical `0x0..0x1e0` | 32 offsets paired over 38 functions, no conflict. `0x208` is above it and stays `Unresolved` |
| `Gob` | identical `0x0..0x4b8` | 28 offsets over 50 functions |
| `Part` | identical `0x0..0x58` | 8 offsets; `GetWorld`/`GetLocal` touch the same eight on both, ordered differently |
| `MdlNode` | identical `0x58..0x98` | 6 offsets over 11 functions; `0x18` and `0x270` are outside it |
| `Camera` position/orientation | identical | Camera's OWN accessors, not Gob's — Linux has two overloads of each (`+0xb0`/`+0xa8`, `+0xbc`/`+0xb4`) and Windows folds each pair into one reading `+0xb0`/`+0xbc` |
| `Model` | **unresolved** | nothing in the sweep touches `+0x8e` on either build, and the class of `gob+0x68` is itself unconfirmed |
| `MdlNodeTriMesh` / `MdlNodeSkin` | identical where read | `0x118`, `0x278`, `0x280`, `0x284` all paired at delta 0 |
| `MdlNodeDanglyMesh` | identical to `0x284` only | the dangly parameters at `0x2c8..0x2e0` are unmeasured |
| `PartTriMesh` | segmented: `+0x4` from `0x64`, `+0x8` from `0x88` | ctor block `0x64..0x93` → `0x68..0x97`; `WithinRange` reads the six bbox floats at `0x68..0x7c`; `0x88`/`0x96`/`0xa0`/`0xb0` paired at `+0x8` |
| `PartDanglyMesh` | **unresolved** | no validated pair on either build — and a base's delta need not carry into a subclass |
| `PartLight` | segmented: own block `+0x4` | `0x64`/`0x68`/`0x6c` paired; the Windows-only set `0x70 0x78 0x80 0xa8 0xb0 0xb8` is the Linux set shifted `+0x4` |
| `LightManager` | identical `0x0..0xc0` | 6 paired, plus #430's ten-function hand read |
| `Camera` | base region 0, `0x4c4..0x4e4` `+0x4`, above that `+0x8` | 18 offsets over 16 functions, including `0xe50` and `0xe64` at `+0x8` |
| `Material` | identical `0x0..0xd0` | `0x0 0x40 0x50 0xd0` paired |
| `SharedMaterial` | **unresolved** | `0xd8`/`0x1c0`/`0x1c8`/`0x1e8` pair at delta 0, yet Windows touches `0x190 0x1a1 0x1b2` — the resref stride `+0x8` from Linux's `0x188 0x199 0x1aa` — and Linux's `0x1f1` has no Windows counterpart. Unexplained, so nothing is resolved on it |
| `CAurTextureBasic` | identical `0x28..0xec` | #430's thirteen-function hand read; MSVC splitting Linux's SSE stores explains the Windows-only `0x90`/`0xa0` |
| `VertexDataObject` | `0x8` 0, `0x14..0x44` `+0x4`, `0x48` and up 0 | 7 offsets over 6 functions, both ends of each segment |
| `VertexPrimitive`, `VertexPrimitiveDangly`, `BSPNode`, `TileData` | **unresolved** | no validated pair on either build |

Two rules this batch settled, because they are the rules the remaining batches
inherit:

- **A class verdict does not travel to a subclass.** `Part` is identical and
  `PartTriMesh` is not; `PartTriMesh` is `+0x8` above `0x88` and `PartDanglyMesh`'s
  own fields are unmeasured. A row's class is the class that DECLARES the field.
- **A vtable slot is cheap to measure and should be.** `tools/abi/vtable-slots.py`
  prints both tables in seconds, which turned `Scene::IsAnimating` (`0x288` → `0x280`)
  and `VertexPrimitive::AsVertexPrimitiveDangly` (`0x28` → `0x20`) from assumptions
  into rows. No `vtbl.*` row should be `Unresolved` without running it.
- **Set corroboration is not a resolution.** `SharedMaterial` is the reason: its
  offsets are nearly all shared, and the shape of what is not shared says the class
  moved.

### Alloy: the passes (#441)

111 more rows, 22 of them `Unresolved` — 235 rows and 55 unresolved in total.
Measured 2026-09-12 with the same `tools/ghidra/FieldDump.java` over both
programs (`.scratch/441`), class list

    PartEmitter MdlNodeEmitter Particle ChunkyParticle TileData CAurTriangleBin
    CAurTriangleList CAurGrassInfo PartBeam MdlNodeLight Scene PartLight Gob Part
    MdlNode Material aurMatrixStack MdlNodeTriMesh GLRender SharedMaterial

The method changed in one way, and it is the batch's most reusable finding:
**read the function pairs directly rather than through `field-diff.py`.** The
script drops a pair the moment the two decompilers split one 8-byte store
differently (`length-mismatch`, `shape-mismatch`), which is most of them — it
reported *three* validated pairs for `PartEmitter` and two mapped offsets for
`Particle`. The same dumps, read by hand, resolve both classes completely.

Roughly a quarter of the directory's raw hex needed no measurement at all: it was
already a row. `Beams` and `Particles` each carried their own copy of the matrix
stack, `SkyBox` its own `GobAlpha`/`GobPos`/`PartParent`, `GrassField` its own
`PartChildren`/`PartChildCount`/`Part+0x96`, `Halos` and `Particles` their own
`Scene+0x158`, `GrassField.Border` its own `MdlNodeTriMesh+0x280`.

| Class | Verdict | Anchor |
|---|---|---|
| `PartEmitter` | **five segments**: `0x0..0x50` 0, `0x64..0xd8` `+0x4`, `0xf8..0x158` `+0x8`, `0x188..0x1a8` 0, `0x1c0`+ `-0x8` | eight function pairs (`RenderEmitter`, `RenderNothing`, `GetTileAlpha`, `chkRandom`, `chkParticleLife`, `moveParticle`, `moveParticleP2PGrav`, `moveParticleP2PBezier`); the `-0x8` is corroborated by the object's own end (`0x1f8` → `0x1f0`) |
| `Particle` | identical `0x0..0x77` | the ctor touches the same thirteen offsets on both, `initialize` the same twenty-two |
| `PartBeam` | identical at `0xa0`/`0xa8` | its whole `Draw` is two reads, and they pair at delta 0 — it does **not** inherit `PartTriMesh`'s `+0x8` |
| `PartLight` | `+0x4` block confirmed at `0xa0` → `0xa4` | the ctor's run is `0x64..0xb7` → `0x68..0xbb`, uniform |
| `MdlNodeEmitter` | `+0x4` at `0xa4`/`0xb0` and at `0x170` | 22 Enable/Is accessors all pair the flags word `0x170` → `0x174`; the ctor brackets the UV grid. `0x164` stays **unresolved** — the ctor's map is order-preserving everywhere except there |
| `MdlNodeLight` | **unresolved** | none of its nine functions pairs on either build |
| `MdlNodeTriMesh` | identical at `0xb0`/`0xb8` | `InternalGenVertices` pairs both at delta 0 |
| `TileData` | identical `0x0..0x57` and `0x7c..0xcb` | the ctor writes `0x0..0xcb` on both and agrees store for store *outside* `0x58..0x7c`, where MSVC splits three of Linux's 8-byte stores — which is exactly where #436 left `TileFade`/`TilePendingFade` unresolved |
| `CAurTriangleBin` | identical | nine functions pair, every one at delta 0 |
| `CAurTriangleList` | identical `0x0..0x60` | seven paired functions, bracketing `0x40` between `0x3c` and `0x48` |
| `CAurGrassInfo` | identical `0x0..0x50` | its ctor is the class's ONLY function; one anchor rather than a pair, because there is no second function to be had. Its `0x54` stride is the batch's one ABI argument: last member a byte at `0x50`, largest a float, **no base class** — and base tail-padding reuse needs a base |
| `aurMatrixStack` | identical, and so is its SIZE | all seven functions pair, and the union of what they touch is `0x18..0x57` on both and nothing else — which measures the top and the `0x58` stride together |
| `Scene` | four more spans inside #436's identical run | `0x190` paired twice; `0x18`/`0x38`/`0x40`/`0x50`/`0x60` inside it. `0x1e8` is above `0x1e0` and stays **unresolved**, like `0x208` |
| `Gob` | `0x160`/`0x16c` paired by `GetBoundingBox`, `0x450` three times | and `Gob::GetAdjustedScale` `0x2d8` → `0x2d0`, from `tools/abi/vtable-slots.py` |
| `MaxFace`, `VertexPool`, `MaterialParameter`, `GLRender::PROG_FIXED` | **unresolved** | nothing takes one as `this`, so the sweep cannot reach them — the same shape as `DrawBucket`'s stride |
| `CNWCArea` | **unresolved** | no `CNWC*` class is name-exported on Windows; needs #420's RVA path |

One premise this batch came in with was **wrong**, and the correction is worth
keeping: `Halos` read `PartLight+0xa0` as a fade and the table had `LightFade` at
`+0x90`, which looked like one field written down twice. It is two fields.
`Scene::RenderLensFlares` @`0x471860` integrates `+0xa0` toward 1.0 while a light
is unoccluded and multiplies the flare colour by it;
`LightManager::ControlLightsFade` @`0x4c2050` scales the area terms by `+0x90`;
and the `PartLight` constructor stores eight bytes at `0x8c` and again at `0x9c`,
so both dwords exist. The same shape repeated at `TileData`: `PartEmitter`'s fade
owner reads `+0xc8`, `HandleFadingInBucket` drives `+0x70`/`+0x74`, and those are
three distinct floats.

`Require` takes the classes a feature reads, so an unmeasured renderer class
cannot stand an unrelated feature down: `NameplateService` asks about
`CNWCObject`/`CNWCCreature`/`CNWCPlaceable`, `DialogService` about `CNWCObject`.
A caller that passes no class still asks about the whole table.

Anchors are handwritten, like `docs/windows-symbols.md`. There is no generator:
#431's extractor produces **candidates**, and a candidate becomes a row here only
once it clears the bar below.

## The bar: what a candidate has to survive to become a row

Settled in #445, after three batches had answered it by practice. A candidate is
an extractor's ranked guess; it tells you which function to open and is never
itself a resolution. A **row** needs all three of these:

1. **A same-role function on each build.** Same class, same job, identified by
   name or signature — not by ordinal position in a list, because MSVC reverses
   an overload set (#433, and #443 caught it firing on
   `CAurTexture::GetCompressedSize`).
2. **The field's role pinned by evidence other than its position.** What the
   caller does with it: a stride multiply, a constant it is compared against, a
   store run in a constructor, the argument slot it is passed in. Two fields that
   look alike at neighbouring offsets are two fields at least as often as they
   are one mistake (#441's `PartLight` fades, #443's two `CAurTextureBasic` names).
3. **A probe where a predicate exists.** `Expect.Byte01` and `Expect.VtblSlot`
   cost nothing to declare. Where neither fits, the row is still a row — the
   probe is a check on the reading, not the reading itself.

"Function pair" means a pair **you read**, off the raw `FN` lines of both
decompilations. It does not mean a pair a tool validated: a strict
length-and-shape filter rejects a pair the moment the two decompilers split one
store differently, which is why `field-diff.py`'s pairing half was retired in
#445 and only its class histogram kept.

Three things that are never a resolution, each of them a mistake already made
here:

- **Interpolating across a segment boundary.** A class diverges in 2-3 contiguous
  segments (#430); a row that falls in the gap between two measured segments is
  unresolved, full stop. `PartTriMesh.selfIllum` is deliberately unresolved for
  this reason, and the one wrong top-1 answer in #431's ground truth
  (`Camera+0x4d4`) is this mistake.
- **Set corroboration.** "The other six fields of this class paired at delta 0,
  so this one does too" is not a measurement (#436).
- **A verdict inherited from a base or passed to a subclass.** The cause of the
  divergence is base tail-padding reuse, which predicts that sibling subclasses
  get *different* deltas (#430). A class verdict does not travel in either
  direction.


### Alloy: the renderer, textures, buffers and shim (#443)

The last of #429 section 2: **73 new rows, 5 of them `Unresolved`, and six of
#441's `SharedMaterial` rows plus its two `Material`, two `SharedMaterial` and
four `MaterialParameter` rows resolved** — 308 rows and 46 unresolved in total.
Measured 2026-09-12. `build.sh` now refuses a bare hex integer constant under
`managed/Alloy/`, which is what closes the section.

Four instruments, and which rows came off which:

| instrument | what it answered |
|---|---|
| `tools/ghidra/FieldDump.java`, one cycle, classes `CNWCAnimBase CRes CAurTexture Material` (`.scratch/443`) | `CRes` (0x20/0x30, one-line accessors on both), `Material` (0x118/0x120 via `InitSharedMaterial`), `CAurTexture` (`GetReferencedTexture` reads `+0x18` on both), and `CNWCAnimBase` — which dumps **empty** on Windows, so its three channel rows stay unresolved |
| the #436 / #441 dumps, re-read | every `CAurTextureBasic` row, `Scene`'s highlight triple, `Gob+0x1c`, `MdlNodeTriMesh` 0xe8/0xf4, `Material`'s 0x10 slot stride, `SharedMaterial`'s 0xd8 and 0x11 |
| `tools/ghidra/DecompOne.java` on both builds | the three functions no class sweep reaches: `SharedMaterial::BindShaderParameters` (@`0x4c5480` / @`0x82aa0`) for the whole `MaterialParameter` record; `GLRender::UnlockPool` (@`0x4cfc80` / @`0xbbad0`, which forwards to a helper @`0x140bbaf0`) for the pool descriptor; `GLRender::SetUniforms` (@`0x4ccbf0` / @`0xb8380`) for `m_shaderExposedRuntimeData` |
| `tools/abi/vtable-slots.py` | all twenty vtable rows — ten `CAurTexture` accessors, eight `Scene` slots, `LightManager::EnableLights`, `Gob::GetAttachedGobs` |

Three things the batch learned that the offset hunt (#429 section 3) inherits:

- **The overload swap can cancel the −8.** Every vtable row in this batch is −8
  on Windows for #433's virtual-destructor reason *except*
  `CAurTexture::GetCompressedSize()`, which is `0xa8` on **both**: MSVC emits the
  overload set `(int,int)`-then-`()` where Itanium emits `()`-then-`(int,int)`,
  and the swap gives back exactly the pointer the destructor took away. The rule
  is a hypothesis; the table is the measurement.
- **Pair a struct reached through a global by SYMBOL ADDRESS, not by slot.**
  `GLRender::SetUniforms` compares each `PROG_FIXED` cache slot against one field
  of `m_shaderExposedRuntimeData`, and in the scalar block `0x10..0x5c` the slot
  index happens to equal the field offset — above it, it does not. The first
  attempt here paired a `DAT_01aaf6xx` cluster that looked like the struct and is
  a different global. Resolve the exported symbol first (Linux `0x19e1ea0`,
  Windows rva `0x128ea10`; Ghidra's Linux addresses are `nm + 0x100000`), then
  ask whether `base + off` is touched on both.
- **MSVC splits a 17-byte copy into 8 + 8 + 1.** That is the whole of #441's
  unexplained `SharedMaterial` "+0x8": the stores at `0x190`/`0x1a1`/`0x1b2` are
  the second halves of the resrefs at `0x188`/`0x199`/`0x1aa`, whose stride is
  `0x11` on both builds. With that explained the constructor is a pair, and six
  rows #441 refused are now measured.

What is still `Unresolved` here, and why it is the honest answer:

| row | why |
|---|---|
| `CNWCAnimBase` `hiliteActive` / `hiliteGlow` / `hiliteGob` | no `CNWC*` class is name-exported on Windows; needs #420's RVA path. **Resolved in #446** — an export is not an anchor |
| `PartTriMesh` `selfIllum` `+0x7c` | it falls in the GAP between PartTriMesh's two measured segments (`+0x4` across the bbox, `+0x8` from `0x88` up). Windows `WithinRange` reads the bbox at `0x68..0x7c`, so `+0x7c` is bbox there, and nothing measures where the boundary's four bytes land. Interpolating across a segment boundary is the Camera `+0x4d4` mistake. **Resolved in #448 by measuring instead of interpolating** — `PartTriMesh::Mat` reads it |
| `GLRender` `poolTableAnchorOffset` `0x236c` | Windows reaches the `{base, count}` pair as its own static (rva `0x128ead8`) passed *by pointer* into the pool helper, not at a fixed distance from any symbol. What Windows needs is its own anchor in `SymbolMap._win`, not a Windows value for this row |

### The seven CNWC* rows (#446)

Section 3, T1. Seven `Unresolved` rows whose owning class has **no Windows name
export**, so #431's class sweep dumps it empty: `CNWCAnimBase` ×3 (the highlight
channels), `CNWCArea` ×3 (flags, tile width/height), `CNWCModule` ×1 (current area).
All seven are now `Same` — 46 unresolved → 39 — and the three anchors they were read
from are in `docs/windows-symbols.md`.

The instrument was #420's, unchanged: anchor the Windows RVA of a function that
touches the field, `DecompOne` it on both builds, read the offset off it. What made it
cheap was that **`nwmain.exe` keeps its `__FUNCSIG__` and `ExoLog` strings even for
classes it does not export** — `"int __cdecl CNWCArea::LoadBorder(class
CGuiAreaLoadscreen *,float)"` sits in the tracy source-location struct of the one
function that is `LoadBorder`. So "not name-exported" is not the same as "not
anchorable", and T1 was the shortest of the three section-3 tickets rather than the
longest the ticket predicted.

| Class | Verdict | Anchor |
|---|---|---|
| `CNWCAnimBase` | identical at `0x60`/`0x9c`/`0xb0`/`0xc8` | `RefreshHilite`, one body per build, paired on its own `nwcanimbase.cpp:0x123a` `ExoLog` line. Role by the loop: flags stride 4 over 5 channels, colour stride 12, gob the guarded pointer |
| `CNWCArea` | identical at `0x8`, `0xc`, `0x10`, `0x248`, `0x298` | `LoadArea` for `+0x8` (wire order), `LoadBorder` for the grid (`(height-1)*width`), `ReloadBorder` for the scene pointer |
| `CNWCModule` | identical at `0xf8` | `CNWCCreature::UpdatePopUpBubble` dereferences it and compares `area+0x118` |

Three things worth keeping:

- **A string anchor survives a missing export.** The whole "CNWC* is unreachable on
  Windows" premise (`#441`, `#443`, and this ticket's own framing) was about the *class
  sweep*, and it silently became a claim about the binary. It is not: three of the four
  anchors here came straight out of the string table.
- **Wire order is a role pin.** `CNWCArea+0x8` has no arithmetic to read it by — it is
  a flags DWORD that `LoadArea` only stores. What pins it is the *sequence* of
  `CNWMessage::Read*` calls around it, which is the message format and therefore
  identical by construction on any build that talks to the same server.
- **The −8 vtable rule fired three more times, on three classes** — and naming them
  right matters, because `RefreshHilite`'s `this+0xc8` is the **Gob**, not the scene.
  Gob `0x180`/`0x188`/`0x220` → `0x178`/`0x180`/`0x218`; Scene
  `AddHighlightGob`/`RemoveHighlightGob` `0x120`/`0x128` → `0x118`/`0x120`; Scene
  `DeleteTile`/`SpawnTile` `0x58`/`0x38` → `0x50`/`0x30`. All measured in passing, none
  a row here; the two `Scene` readings agree with the eight `Scene` `vtbl.*` rows that
  are.


### The camera layer rows (#536)

Eighteen `Same` rows, measured by decompiling both builds directly (no pairer: the
behavior class is name-exported on both, and `CNWCModule`'s camera functions hang off
the anchored `HandleServerToPlayerMessage` dispatch). Provenance is
`docs/research/custom-camera.md` §9.4; the Windows entry points it names are the
anchors.

| Class | Rows | Anchor |
|---|---|---|
| `CAurBehavior` | `owner 0x18` | both ctors store the argument there |
| `CAurBehaviorCameraFollow` | `focus 0x30`, `dist 0x48`, `yaw 0x54`, `pitch 0x5c`, `yawClamp 0x64`, `pitchClamp 0x70`, `distClamp 0x7c`, `yawMin 0x68`, `pitchMin 0x74`, `distMin 0x80` (max = min + 4) | `Control` (Linux @`0x4a2290`, Windows @`0x140055100`), one body per build reading every field |
| `CNWCModule` | `camera 0xe8`, `cameraAttached 0x124`, `findClearView 0x140` (probed 0/1), `dialogCamera 0x258`, `cutsceneRate 0x298` | `SetCameraFollow` rva `0x7cf6f0`, `SetDialogCamera` rva `0x7cfa90`, `SetCutsceneCamera` rva `0x7cfa00`, reached from the camera (`0x77a7f0`) and dialog (`0x7962f0`) message handlers |
| `CClientExoAppInternal` / `CClientOptions` | `clientOptions 0x0` / `cameraMode 0x1d` | `GetClientOptions` is `**(app+8)` on both (`0x637ea0` / rva `0x6ab9b0`); the byte is read at `+0x1d` by `SetCameraFollow` and by the lock arms of the Windows camera handler |

### The drive rows (#540)

One function per build, `CClientExoAppInternal::UpdateDriveMode` (Linux @`0x65f780`, Windows rva
`0x6f1e40`, found by its cadence constants -- `docs/windows-symbols.md`), decompiled on both and
read side by side. The `CClientExoAppInternal` fields are identical; the two `CNWCCreature` gates
carry the class's known `-0x20` Windows shift (the same delta as `plated`).

| Class | Rows | Anchor |
|---|---|---|
| `CClientExoAppInternal` | `driveChanged 0x1b0` (probed 0/1), `driveAutoRun 0x1b4` (probed), `driveKeys 0x1b8` (probed; six ints: forward, back, turn L/R, strafe L/R), `driveLastSend 0x1a0` | the function's own reads on both builds: the latch tested first, the seven key ints in the keep-alive OR, `+0x1a0` stamped on every send and compared against 499999 |
| `CNWCObject` | `orientation 0x44` | both builds `atan2(+0x48, +0x44)` for the wire angle and copy `+0x44..+0x4c` into the TurnOnSpot rotation |
| `CNWCCreature` | `driveFlags 0x39c / 0x37c` (bit 2), `driveEnabled 0x43c / 0x41c` | the two early-outs at the top of the function on each build (`& 2 == 0` then `== 0`), in the same order |
| `CNWCCreature` | `facingTarget 0x2c8 / 0x2a8`, `facingTargetB 0x2d4 / 0x2b4` | the pair both builds write with the quantized direction right after the DriveControl send and in the TurnOnSpot arm; `DrivePlayer` writes the first alone for its ±30° turn |
| `CNWCModule` | `chaseSuppressed 0x13c` (probed 0/1), `chaseWasIdle 0x27c` (probed) | `UpdateCameraModeChaseCam` / `EQ` (Linux @`0x7dddd0` / `0x7de660`, Windows rva `0x7d0d00` / `0x7d1c90`, found by `ScanOffsets 134 138 13c 27c 144 100`): `+0x13c` set with `+0x138 = 0` when `+0x134` (camera moved) holds, cleared with `+0x27c` on the idle-to-locomotion edge, both builds the same four fields |

### The recenter rows (#541)

No new function: the same two chase drivers and the one solve. Read on both builds.

| Class | Rows | Anchor |
|---|---|---|
| `CAurBehaviorCameraFollow` | `yawVel 0x58`, `yawImpulse 0x88` | `TurnCamera(deg, 0/1)` (Linux @`0x7d8960`, Windows rva `0x7cfe60`) adds into `+0x58` / `+0x88`; the chase drivers write `+0x58`; `Control` integrates and zeroes both at the same offsets on both builds |
| `CNWCObject` | `animation 0x5c` (short) | `GetCurrentAnimation` is `*(short*)(this+0x5c)` on both (Linux @`0x7e3f70`, Windows rva `0x7630d0` -- the chase driver's own callee, nine times per call for the locomotion test) |

### The free-camera rows (#543)

| Class | Rows | Anchor |
|---|---|---|
| `CNWCCreature` | `combatState 0x1f4 / 0x1d4` | `SetCombatState(int)` (Linux @`0x722160`, Windows rva `0x80a230`): the first store in both bodies; the Windows body is the callee right after the `+0x37c/+0x37e/+0x380/+0x388/+0x384` cluster in the Windows `HandleServerToPlayerCreatureUpdate_Update` (`0x781e80`, `ScanOffsets 37c 37e 380 388 384` = 1 hit), the same cluster Linux writes at `+0x39c..` before its call |
| `CNWCObject` | `position 0x38` | `SetPosition(Vector)` Linux @`0x7e9180` writes `+0x38..+0x40`; the orientation row at the same `+0x44` on both builds pins it |
| `CNWCModule` | `cameraMaxDist 0x2b8` | `SetCameraLimits(float,float,float,float)` Linux @`0x7def00` / Windows rva `0x7cf9d0`: four consecutive stores `+0x2ac..+0x2b8`, the ctor seeds `-1` |
| `CClientExoAppInternal` | `inGameGui 0x90` | `CClientExoApp::GetInGameGui` is `*(*(app+8)+0x90)` on both (Linux @`0x637c70`, Windows rva `0x6abbc0`) |
| `CGuiInGame` | `toggledOff 0x3d4 / 0x374` (probed 0/1) | `ToggleOff` / `UnToggleOff` (Linux @`0x66dda0` / `0x66e930`, Windows rva `0x6fc6f0` / `0x6fc900`) test and write it first; reached from the anchored message dispatch, case 0x33 -> cutscene handler -> minor 7 |

### The 38 DecompOne rows (#448)

Section 3, T2 — the biggest and most mechanical of the three. All 38 are now
`Diverges` or `Same`: **39 unresolved → 1**, the one left being #447's
`poolTableAnchorOffset`, which wants a symbol anchor rather than a value. 308 rows in
the table. Measured 2026-09-12 against build `5fe6e02d…`.

The ticket sorted the 38 into three shapes — a measured class with the row above its
measured run (D), a class that pairs on nothing by tool (C), and a record nothing takes
as `this` (B). **All three collapsed into the same instrument**: name the one function
that touches the field, `DecompOne` it on both builds, read the offset. Three headless
runs covered everything — 32 functions, then 9, then 4 — plus one `FieldDump` cycle
over six classes for the constructor store runs.

| rows | verdict | the function that measured them |
|---|---|---|
| `Scene` `drawBuckets` `0x208`, `envMap` `0x1e8` | `Same` | `Scene::Scene` constructs `DrawBucketManager` in place at `this+0x208` on both, with an identical `0x178..0x1f8` store run below it; `Scene::Render` assigns the `SafePointer<CAurTexture>` at `0x1e8` from `GetEnvMapResRef` on both |
| `MdlNode` `matrix` `0x18`, `primType` `0x270` | `Same` | `Scene::RenderDrawBucket` passes `node+0x18` to `SetModelMatrix`; `RenderFlat` passes `node+0x270` as argument 3 of `RenderVertexData` |
| `MdlNodeSkin` `bindRot` `0x2f0`, `bindPos` `0x300` | `Same` | `PartSkin::PostProcess` — the gather loop adds to both lists at those offsets |
| `MdlNodeSkin` `boneMap` `0x320` | `Same` | `ExecuteSkinGPU`, a **free function**: 64 iterations, stride 2, from `node+800` |
| `MdlNodeDanglyMesh` ×5 (`0x2c8`…`0x2e0`) | `Same` | `MdlNodeDanglyMesh::InternalParseField`, each field pinned by its own `.mdl` keyword string; `PartDanglyMesh::Animate` reads all four back. `constraintCount` is the `List<T>`'s own `+8`, measured on `ExecuteSkinGPU`'s typed `List<Vector>*` parameter on both builds |
| `PartDanglyMesh` `prevPos`, `prevRot` | `Diverges` `0xb4`/`0xc0` → `0xc0`/`0xcc` | `PartDanglyMesh::Animate` — same three-float read, same x/y/z-against-zero test, same store-back |
| `VertexPrimitive` `vdo` `0x58`, `item` `0x60` | `Same` | `RenderFlat` passes both as the `RenderVertexData` arguments; `Animate` reads the pair again |
| `VertexPrimitiveDangly` `velocity` `0xa0` | `Same` | `Animate` null-checks it, calls `GenerateVelocityArray`, then indexes it |
| `BSPNode` `data` `0x70`, `triMesh` `0x20`, `triMeshCount` `0x28` | `Same` | `SceneBSPAddPart` pairs on its whole body — seven bucket writes, same sub-offsets. The count is `CExoArrayList`'s own `+8`, read once per build on a container the decompiler types: `GetLensFlareLights`' list on Linux, `CExoArrayList<Vector>::Allocate` at `node+0x300` on Windows |
| `TileData` `fade` `0x70`, `pendingFade` `0x74` | `Same` | `ManageSceneBSP`'s commit is one statement, `tile+0x70 = tile+0x74`, in the same loop over `scene+0x1a8` |
| `DrawBucket` `stride` `0x10` | `Same` | `GetDrawBucketByItemType` and `AddPartToBucket`, one line each, same `*0x10` |
| `CAurBehaviorCameraFollow` `target` `0x20` | `Same` | `GetTargetWorldPosition`, one line each |
| `MdlNodeLight` ×4 (flares) | `Diverges`, **two segments** | `Scene::RenderLensFlares`: radius `0x9c`→`0xa0` (+4), count/sizes/shifts `0xa8`/`0xc0`/`0xd0`→`0xb0`/`0xc8`/`0xd8` (+8) |
| `MdlNodeEmitter` `noCull` | `Diverges` `0x164` → `0x168` | `PartEmitter::RenderEmitter` and `RenderStandard` both branch on it into `Enable(3)`/`Disable(3)` |
| `Model` `ownerFlags` | `Diverges` `0x8e` → `0x92` | `PartTriMesh::Mat`'s `& 8` test on `gob+0x68`; the class pinned by `Model::Model`'s store run and `Model::AddRef`'s count (`0x90`→`0x94`) |
| `PartTriMesh` `selfIllum` | `Diverges` `0x7c` → `0x80` | `PartTriMesh::Mat` loads three floats into the emission scratch and hands them to `SetMaterialv(0, 3)` |
| `VertexDataObject` `vao` `0xc` | `Same` | `GLRender::SetVertexDataObject`: `glGenVertexArrays` when `vdo+0xc` is zero, store back, bind |
| `MaxFace` ×4 (`0x1a`/`0x1c`/`0x1e`, stride `0x20`) | `Same` | `MdlNodeTriMesh::BuildVertexIndices` walks `node+0xb0` in `0x20` steps, three `u16`s per face |
| `VertexPool` `posStride` `0xc` | `Same` | the tangent-basis worker locks `node+0x280` and indexes `* 0xc` on both, and resizes to twelve bytes a vertex on each — Linux `count * 0xc`, Windows the strength-reduced `(count + count * 2) * 4` |
| `GLRender::PROG_FIXED` `programId` `0x64` | `Same` | `GLRender::SetProgram` hands `prog+0x64` to `glUseProgram` |

Five findings the rest of the port inherits:

- **"No validated pair" was never a property of the binary, and neither is "no method
  of its own".** Sixteen Kind-C rows were filed because `field-diff.py` paired the class
  on nothing; every one of them is measured by a function that takes some *other* class
  as `this` — `SceneBSPAddPart` for `BSPNode`, `ManageSceneBSP` for `TileData`,
  `Scene::RenderLensFlares` for `MdlNodeLight`, `PartDanglyMesh::Animate` for
  `VertexPrimitive`. **The unit that pairs is the FUNCTION, not the class.** Kind B
  (records nothing takes as `this`) is the same observation one step further, and it is
  the shape that reached `MaxFace`, `DrawBucket` and `PROG_FIXED`.
- **The three rows the ticket said only #431's unbuilt call-result lever could reach —
  `VertexDataObject.vao`, `PartDanglyMesh.prevPos`/`prevRot` — all came off a named
  function.** The lever stays unbuilt; nothing now wants it.
- **A gap between two segments can be measured rather than interpolated, and then it
  explains itself.** `PartTriMesh.selfIllum` is `+0x4` with the bbox segment; the extra
  four bytes the `+0x8` boundary adds are the **alignment pad** Windows needs between a
  float block ending at `0x8c` and the 8-aligned `node` pointer at `0x90`. The
  prohibition on interpolating stands — what is new is that the boundary itself is
  readable, so a gap row is a *waypoint*, not an exit.
- **Base tail padding is visible in a constructor's initializer VALUES.**
  `VertexDataObject`'s two ctors write the same `3` and `-1` pattern four bytes apart
  from Linux `0x14` up and not below it, which places the boundary exactly at the end of
  `VertexDataObjectBase` — Itanium reuses its tail padding, MSVC rounds the base to its
  alignment. That is #430's mechanism caught in the act, and it is why `vao` at `0xc` is
  `Same` while everything above it in the same class is `+4`.
- **A one-line accessor is a whole measurement.** `GetTargetWorldPosition`,
  `GetDrawBucketByItemType` and `AddPartToBucket` are single expressions on both builds;
  they need no corroboration and they cost one `DecompOne` each.

**No probes.** `Expect.Byte01` fits none of the 38 — the two flag-ish rows are a flags
byte read with `& 8` (`Model.ownerFlags`) and an `int` compared to zero
(`MdlNodeEmitter.noCull`), neither of which a first-sighting `0-or-1` check would
distinguish from a wrong offset — and none of the 38 is a vtable slot. Bar item 3 is
"a probe **where a predicate exists**"; here none does, and saying so is the reading.

**No plaza A/B is owed.** `Same` returns the Linux value unchanged and `Diverges`
returns its `linux:` arm there, so the Linux build is bit-identical: verified
mechanically — every one of the 309 rows resolves to the same Linux value before and
after, and `run-demo.sh` reports `308 offset(s), 1 unresolved`. Alloy still stands down
under Proton, so not one of the 38 is read on Windows yet; that is the map's standing
fog entry, not this ticket's.
