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
**rot**: every feature that reads engine memory stands down as a unit with one
line naming the build that was actually loaded. This is `#48`'s rot policy
widened from Windows-only to both platforms — on Linux that is new, and a client
patch now costs nameplates until the table is re-measured, which is the intended
trade against rendering garbage off a moved field.

There is exactly one row per OS. A second tested build gets its own pinned key
and its own values; hunting offsets for a build nobody has tested is not work.

**Two locks exist today.** `SymbolMap` pins the PE `TimeDateStamp` for its RVAs;
this pins the PDB GUID. They gate different things and agree in practice (same
PE). Collapsing them onto one key is a follow-up, not a reason to rewire a
live-verified gate.

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

`Require` takes the classes a feature reads, so an unmeasured renderer class
cannot stand an unrelated feature down: `NameplateService` asks about
`CNWCObject`/`CNWCCreature`/`CNWCPlaceable`, `DialogService` about `CNWCObject`.
A caller that passes no class still asks about the whole table.

Anchors are handwritten, like `docs/windows-symbols.md`. There is no generator:
#431's extractor produces **candidates**, and a candidate becomes a row here only
once it has been read in a function pair and, where it can, probed.
