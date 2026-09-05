# ADR 0023: Textures are cached across the world edge, and a resident texture is never rebuilt

Status: accepted (2026-09-05). Live-UNVERIFIED. `texcache_mb` in sowx.cfg (-1 = a quarter of probed VRAM, 0 = off).

## Context

The hitch ledger (PR 385) named first-sight loading as the stutter: `Demand` (the engine's
resource read and decode) and `Tex` (the upload funnel) inside the engine's step, at ~2ms per
texture, 11-41 demands per hitch frame. The engine's texture budget is a 2002 design: every
`CAurTextureBasic` dies with its area, and re-entering an area, or meeting a creature whose
skin every district uses, reads, decodes and uploads the same texture again. An 8 GB card holds
the whole module many times over; the geometry pools sit at 96 MiB.

`OwnedTextures` already keyed its resources on CONTENT (`AurTextureCalculateHashID` over the
resolved name and PLT schemes, folded with the atlas cell), not on a GL name. Only its lifetime
was wrong: the engine's `DeleteTextures` funnel, the area transition, discarded the resource
along with the index entry that pointed at it.

Decompiled (Ghidra, headless, 2026-09-05): `CAurTexture::BuildAndStore` @0x48bc80 is `Reset`
(DeleteTextures the cell names at +0x50, delete[] the array, +0x6c = 0) then
`CAurTextureBasic::BuildAndStoreTexture` @0x48b320, which is: skip if +0xe8; `ConstructImage`
unless +0xe6 (LoadImage -> AurGetImageWrapper is the resource read and decode, into +0x58);
if an image exists, vcall +0xf0 = `glImageBasic` (GenTextures into +0x50, +0x6c = names[0],
`CreateTexture` per cell) and the alpha mean into +0x30; vcall +0xf8 = FreeImage. So the
demand IS inside the build, and a build that never runs saves read, decode and upload together.

## Decision

1. **The delete funnel retires the INDEX, not the resource.** A resource whose last index entry
   retired goes IDLE (stamped) and keeps its GL name and bytes. Its bindless handle is released
   (the handle table holds 32k names; a cache does not). Eviction runs in the bracket, oldest
   idle first, only past `texcache_mb`. `ContextLost` still drops everything.
2. **A resident texture is handed back instead of built.** `BuildAndStoreTexture` is hooked. For
   a PLAIN texture (no cube flag +0xe3, no PLT schemes +0x48 / marker +0x84, no animation
   controller +0x38, +0xe8 clear) whose cell-0 key has captured meta and whose every cell is
   resident and not in the transcode pipeline: allocate the cell array with the engine's own
   `operator new[]` (Reset frees it with delete[]), fill it with Alloy's names, write +0x6c,
   numX/numY, width/height/bpp, the compressed size, the alpha mean, +0xe6 = 1, +0xe7 = 0,
   +0x58 = 0, index the names, and skip the original. The meta is CAPTURED after each cold
   build from the object itself -- nothing is derived; numX/numY's writer lives outside the
   class and was not chased.
3. **Alloy's names in the engine's hands survive its Reset.** `OnDeleteTextures` filters owned
   names out of the array handed to the driver; their index entries retire like any other.
4. A resource with a transcode queued or in flight is not warm-eligible: the pipeline is about
   to retire the very name the engine would be handed.

## Consequences

- Second sight of any plain texture costs nothing: no demand, no decode, no upload, no
  transcode. First sight is unchanged (and still double-uploads: engine copy + Alloy copy).
- The engine now binds Alloy's names for its own 2D use of a warm texture (portraits, GUI).
  Share-listed contexts make that legal; `AurTextureBindTID`'s cache sees a name it never
  bound before and binds it.
- `Draw(tid)` is an identity for warm tids; `IsOwned` is true for them, so handles mint.
- Excluded on purpose: cube maps, PLT skins (per-character composites), animated textures.
- Read on `[alloy]`: `texcache: N MiB resident (M idle) of B MiB, hits, misses, kept, evicted`;
  events `TexWarm`/`TexEvict` on `[zones]`. Protocol: plaza, leave, return -- the second entry
  should read `TexUpload` and `Demands` near zero and `TexWarm` near the first entry's
  `TexUpload`.
- The step after this is #386: off-thread decode and prefetch, so FIRST sight leaves the
  frame too, and the single-upload cold path.
- A texture-quality option change rebuilds every texture through Reset + BuildAndStoreTexture,
  and the warm path hands back the OLD decode, so the option is defeated until eviction. If it
  matters, `/texcache flush` is the ADR 0013 shape; not built.
- A DeleteTextures call past 4096 names falls to the pre-ADR path and discards. Transitions
  delete per object, so it should not arise; `kept` on the cache line going quiet would say it did.
- The two-tids-one-resource case is decided at eviction, not at retire: a retire marks idle in
  O(1) and Evict un-idles anything the index still references, once per pass.

## Addendum: #386, first sight (2026-09-05, same branch, live-UNVERIFIED)

Built:
- **Single upload** (`texsingle`, boot 1). `Adopt` uploads into Alloy's name FIRST and hands that
  name to the engine's cell array (+0x50, and +0x6c for cell 0); the engine's own name never
  receives pixels and is deleted on the spot. Halves `Tex`. 0 = the double path.
- **Prefetch** (`texprefetch`, boot 2 per frame). The enrolment walk hands every unbuilt texture of
  an accepted part (diffuse + material slots) to a queue; the drip builds them through the engine's
  own `BuildAndStore` (the #284 retry path with its pointer re-proof), a budget per frame, on the
  frame anchor. First sight then finds the texture warm. The cost moves from the turn to the frames
  after enrolment, as a bounded per-frame tax; read `Prefetch` on `[zones]` and `prefetch:` on `[alloy]`.
- **Swap in place.** A `Replace` that retires a name (transcode swap, re-upload) no longer re-stages
  every record on it (Restage 3,3-4,9 vs 1,1 in hitch rows); records take the new tid and
  `ApplySwaps` rewrites their handle block in the next bracket. `_recTexStale` is now the EVICTION path only.
- `Bcn` split into `BcnRead`/`BcnMap`/`BcnUp` to name which stage of the pipeline costs main-thread time.

Not built, and why:
- A loader thread with its own GL context (decode + upload off the engine thread). The engine's
  decoder is not thread-safe and DDS/TGA/PLT readers would have to be reimplemented; decide after
  the Bcn split and `Tex` under single upload say how much main-thread upload cost is left.
- A hot list across sessions. Prefetch already builds every texture of an area within seconds of
  enrolment; a boot prewarm would need texture objects that do not exist before the area loads.
