# Owned-resource lifetimes

What creates each thing Alloy owns, what revokes it, and what a lost GL context does to it. Written
for #209 (M4 step 1, ADR 0016). It is a crash-prevention document: the gaps at the bottom were found
by writing the table, not by playing.

Line references are to the commit that introduced this file; treat them as a starting point, not as
an address.

## The two edges, and why they are not one

Alloy holds two unrelated kinds of thing, and they die for unrelated reasons.

**`Revoke` — the world edge.** The world underneath a table went away: the module unloaded, the part
table was wiped, the area changed, enrollment re-ran. What dies is everything **captured from the
engine**: part pointers, gob pointers, VDO pointers, Scene pointers, BSP roots, GL texture ids
harvested from `AurTextureGetTID`. All of these are **bare pointers used as identity**, and the
allocator recycles addresses — so "the pointer still compares equal" is not evidence the thing is the
same thing. The GL objects are untouched: they belong to the context, and rebuilding a program per
area transition would be a stall for nothing.

**`ContextLost` — the context edge.** The 4.6 context went away. What dies is every **GL name**.
Nothing is deleted, because the objects went with the context and the name now belongs to whatever
the driver recycles it for. They are *dropped*, because every `EnsureX` in the renderer
short-circuits on a non-zero handle — leaving them set is what makes the next context draw through a
dead program. Captured world state is not this slot's business (though several passes drop both,
which is fine: a rebuilt context re-captures anyway).

Conflating them is how a pass ends up half stood-down. `PassLifecycle` (`Alloy/Core/PassRegistry.cs`)
declares both slots per pass; `RenderPasses.cs` is the one table that fills them.

### Terminality — the invariant that makes the "orphaned" rows safe

**`GlContextService.Fail` sets `_disabled = true` permanently** (`Graphics/GlContextService.cs:87`).
There is no recreation path. So context loss today is a **per-session terminal edge**: after it,
`Enter()` returns false, the whole Alloy bracket returns at `AlloyRenderer.Frame.cs:99`, and no
owned GL name is ever used again. That is why the rows below that say "names orphaned, nothing
deleted" are safe rather than leaks that matter.

If context recreation ever lands, **this document is the checklist**. Two consequences bite
immediately: `EnsureVao` (`AlloyRenderer.Vao.cs:56`) *deletes* a stale non-zero VAO name before
rebuilding, and `CasterPass.Release` deliberately keeps its GL names — both are correct only while
the edge is terminal.

One related fact, recorded because it is otherwise only in the code: after a `_progFailed` /
`_batchUnavailable` disarm, `AlloyRenderer.EndFrame` latches `_armed = false` and returns at its
first line forever (`AlloyRenderer.cs:988`), so it stops driving the `ContextLost` walk. Safe by the
same terminality — the bracket is already off for the session — but it is a real "this walk has no
caller from here on" and it should not be a surprise.

### Two names that are not this

`FrameSnapshot.Revoke` and `DepthResource.Revoke` are **per-frame content-freshness latches**, not
world edges. `FrameSnapshot.Revoke()` runs at the top of every frame (`AlloyRenderer.Frame.cs:23`)
to say "the copy in the snapshot is last frame's"; `DepthResource.Revoke()` says the prepass depth
can no longer be trusted. Same word, different meaning, and neither is wired to the registry's
`Revoke` walk.

## The edges, and who fires them

```
Part::~Part           -> BatchService.OnPartDtor        (BatchService.cs:620)  per-part death notice
                            -> SkyBox.Revoke(part)      (BatchService.cs:624)
                            -> PartTable Die(e)         (BatchService.cs:633)
VDO repack            -> AlloyRenderer.OnVdoRepackHook  (AlloyRenderer.cs:951) per-VDO
                            -> Beams.OnRepack, CasterPass.OnVdoRepack
Scene::ReloadGrass    -> GrassField.OnReloadGrass       (GrassField.cs:305)
module unload         \
part-table wipe        |-> AlloyRenderer.RevokeAll      (AlloyRenderer.cs:1003-1007)
4.6 context lost      /       -> WipeHandles + PassRegistry.Global.Revoke()   (Enroll.cs:26-27)
                              -> CasterPass.Lifecycle -> Release              (CasterPass.cs:1003-1008)
4.6 context lost      -> PassRegistry.Global.ContextLost()                    (AlloyRenderer.cs:1003)
vanilla toggle        -> PartTable.OnRenderModeEdge -> WipeTables             (PartTable.cs:481)
                            (which bumps TableGen, so it arrives as "part table wiped" above)
table pressure        -> PartTable.OnTablePressure -> WipeTables              (PartTable.cs:453)
```

`RevokeAll` runs the world walk **above** its empty-table guard. The ownership count says what *Alloy
took*; it says nothing about what the *passes captured*. Sky is the only pass with a per-part death
notice of its own (`BatchService.cs:624`); for beams, particles and grass this walk is the **only**
teardown, and even sky's `Scene` pointer and drawn-generation are reachable no other way.

## The table

### Captured world state (`Revoke`)

| Resource | Created by | Revoked by | Caller | Context loss |
|---|---|---|---|---|
| Ownership table `_oTri/_oGob/_oVdo/_oEntry` | `AlloyRenderer.Enroll` | `RevokeAll` | `AlloyRenderer.cs:995,1003,1005,1007` + per-part `Revoke(i)` on repack (`:951`) and identity change (`Batches.cs:234`) | via `RevokeAll` |
| Part/group tables (`PartTable`) | `BatchService.Complete` | `WipeTables` / `Die` | `PartTable.cs:453,481`, `BatchService.cs:717`, `OnPartDtor` `:633` | none — CPU only |
| VAO cache `_vVdo/_vStale` | `EnsureVao` | `RevokeAll` retires the slots | `Enroll.cs` | GL names zeroed by `AlloyRenderer.ContextLost` |
| Bindless handle cache `_hTid` | `AlloyRenderer.Issue` staging | `WipeHandles` | `Enroll.cs:26` (release) and `:62` (enrollment) | — |
| Water planes (`WaterBodies`) | plane measurement during enrollment | `WaterBodies.Clear` | registry `Revoke` slot | none — CPU only |
| Sky parts (`SkyBox`) | `OnPartDraw` capture window | `SkyBox.Forget`; per-part `SkyBox.Revoke` | registry `Revoke`; `BatchService.cs:624`; scene change `SkyBox.cs:136` | none — holds no GL |
| Beam parts + VAO slots (`Beams`) | bucket-15 capture | `Beams.Forget` | registry `Revoke`; scene change `Beams.cs:173`; repack `:405` | `Beams.ContextLost` |
| Emitter table (`Particles`) | emitter-bucket capture | `Particles.Forget` | registry `Revoke`; scene change `Particles.cs:480` | `Particles.ContextLost` (also frees the native stage buffer) |
| Grass patches + slot map (`GrassField`) | bin scatter | `GrassField.Forget` | registry `Revoke`; `Scene::ReloadGrass` `GrassField.cs:305` | `GrassField.ContextLost` |
| Env cube freshness + BSP key (`EnvProbe`) | `EnvProbe` capture | `EnvProbe.Revoke` | registry `Revoke` | `EnvProbe.Forget` |
| Caster set + skin records + texture-alpha verdicts (`CasterPass`) | `CasterPass.Enroll` | `Release` | `CasterPass.cs:1003,1005,1008` | routes through the same `Release` — see below |

Two more pointer-keyed tables are **enrollment-scoped scratch**, listed for completeness rather than
because they have an edge: `AlloyRenderer._mKey/_mVal` (the material→texture-sort-key intern, cleared
at `Enroll.cs:78`) and `AlloyCensus._refGob` (the refused-gob set, cleared by `OwnScanBegin`,
`AlloyCensus.cs:833`). Both are wiped at the top of the enrollment that fills them and are never read
across one, so a recycled pointer has no window to be believed in. They need no `Revoke` — but they
are the shape that does, so if either ever outlives an enrollment it joins the table above.

### GL objects (`ContextLost`)

| Resource | Created by | Dropped by | Caller |
|---|---|---|---|
| `_prog46/_progLit46/_progDepth/_progDepthKey/_progWater`, `_fbo46`, `_drawBuf/_texHBuf/_boneBuf`, `_vVao46[]` | `EnsureProgram` / `EnsureTarget` / `UploadDraws` / `EnsureVao` | `AlloyRenderer.ContextLost` | registry `ContextLost`; `_progLit46`/`_progWater` are additionally *deleted* and rebuilt by `TakeProgramRebuild` (`Programs.cs:114`) when a compile-time dial moves — the one deletion path that is neither edge |
| Water snapshot FBO + colour/depth textures | `FrameSnapshot.Ensure` | `FrameSnapshot.Forget` | registry `ContextLost` |
| Scene-depth FBO + texture | `SceneDepth.Ensure` | `SceneDepth.Forget` | registry `ContextLost` |
| Light SSBOs (`_bufLights/_bufCounts/_bufIndices/_bufStats`) | `AlloyLights` | `AlloyLights.Forget` | registry `ContextLost` |
| Env cube + its FBO/depth | `EnvProbe` capture | `EnvProbe.Forget` | registry `ContextLost` |
| Post targets, FBO, fullscreen VAO | `PostChain` | `PostChain.Forget` | registry `ContextLost` |
| Grass VAO/VBO/program/samplers/push texture | `GrassField.EnsureBuffers` | `GrassField.ContextLost` | registry `ContextLost` |
| Beam program + per-VDO VAOs/element buffers | `Beams.EnsureProgram` | `Beams.ContextLost` | registry `ContextLost` |
| Particle program/VAO/VBO/sampler + native stage buffer | `Particles.Ensure*` | `Particles.ContextLost` | registry `ContextLost` |
| Caster depth program, FBO, depth array, persistent-mapped buffers, VAOs | `CasterPass.EnsureGl` | **nothing** — kept | see below |

### Deliberately outside the table

- **`CasterPass`** is not in `PassRegistry` at all (folding the shadow pass in is #176). Its
  `Lifecycle()` watches all three edges itself, and context loss routes through
  `Release("4.6 context lost")` — the **world** teardown. Its GL names are deliberately kept, because
  `Release` also fires on module unload with the context alive, and zeroing them there would rebuild
  the program and the depth array at every area transition. Safe only while context loss is terminal.
- **`ShadowService`** owns no table. It holds trampolines and a mode int, and its suppression is
  derived per draw from `CasterPass.CascadesFresh` / cube membership — which `Release` and `Disarm`
  zero (`CasterPass.cs:1080,1492`), so vanilla shadows come back by themselves.
- **`TexProbe`** is stateless: two `glGet*` readbacks for the census triage, no cache, nothing to
  revoke. It exists only because `Alloy/Scene/` may not touch GL (#206).
- **Engine-owned pointers Alloy reads but never owns** — `CAurTexture*`, materials, nodes — are ADR
  0015's subject, not this document's.

## What the writing-down exposed, and what was done about it

1. **The world walk sat below `RevokeAll`'s empty-table guard.** `if (_ownCount == 0) return;` came
   before `PassRegistry.Global.Revoke()`, so a module unload arriving during a stretch where Alloy
   owned nothing left sky, beams, particles and grass holding freed parts. Only sky has a
   `Part::~Part` notice to catch any of it, and that one covers its part entries, not its `Scene`. This is #145/#150 with a different owner. **Fixed**: the
   walk moved above the guard, where #147 had already put water's plane table for the same reason.
   Water's bespoke call was deleted and it became an ordinary registry `Revoke` entry.
2. **`EnvProbe` had no world edge at all.** Its re-bake test is `bspRoot != _bsp` — a bare pointer
   used as an equality key — so a new area whose BSP landed on the freed one's address kept `Ready`
   set and lit the new interior with the previous one's env map. **Fixed**: `EnvProbe.Revoke` drops
   `Ready` and the key (and keeps the GL objects), wired to the registry, asserted in
   `EnvProbe.SelfCheck`.
3. **`AlloyRenderer` — the largest GL holder — had no `ContextLost` slot**, and dropped only
   `_progWater` inline. Every pass it *calls* drops its names (#175); the pass driving the walk did
   not. **Fixed**: `AlloyRenderer.ContextLost` drops the five programs, the FBO, the three SSBOs and
   the VAO names, and the "draw" entry fills the slot.
4. **`WipeHandles`'s own doc comment said "called at each enrollment and on release"** and had no
   release caller. Harmless today (nothing can submit between a release and the next enrollment,
   which wipes first), but the comment was a claim about a caller that did not exist. **Fixed**: it
   is called from `RevokeAll`.
