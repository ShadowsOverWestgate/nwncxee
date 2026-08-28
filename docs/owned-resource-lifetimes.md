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

The one row that is deliberately in BOTH halves is `OwnedTextures` (#211): the copies are keyed on an
engine GL name, which is captured world state, *and* they are GL objects. The world edge retires them and
the context edge forgets them — but the world walk runs at a frame edge in the ENGINE context, so it can
only *queue* the delete; `OwnedTextures.Drain` issues it inside the next 4.6 bracket. That is the same
split `EnsureVao` makes for its retired VAO names, and the reason a `Revoke` slot is still not a GL slot.

`OwnedTextures` also has an edge **no other row has**, and the reason is worth stating: it keys on an
engine GL texture name, so the event that invalidates it is the engine deleting that name — which on an
**area transition** fires none of the world edges above (#220 measured one part-table wipe in an entire
session). It therefore hooks `RenderInterface::DeleteTextures` @0x45d1d0, the single funnel every engine
texture name dies through, and retires the one copy that name produced. If you add a table keyed on
anything the engine hands out by GL name, this is the edge you need too — not `Revoke`.

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
| Bindless handle cache `_hTid` | `AlloyRenderer.Issue` staging | `WipeHandles` — **only while `ownedtex=0`** (#220); armed, see the row below | `Enroll.cs:26` (release) and `:62` (enrollment) | `AlloyRenderer.ForgetHandles` |
| Water planes (`WaterBodies`) | plane measurement during enrollment | `WaterBodies.Clear` | registry `Revoke` slot | none — CPU only |
| Sky parts (`SkyBox`) | `OnPartDraw` capture window | `SkyBox.Forget`; per-part `SkyBox.Revoke` | registry `Revoke`; `BatchService.cs:624`; scene change `SkyBox.cs:136` | none — holds no GL |
| Beam parts + VAO slots (`Beams`) | bucket-15 capture | `Beams.Forget` | registry `Revoke`; scene change `Beams.cs:173`; repack `:405` | `Beams.ContextLost` |
| Emitter table (`Particles`) | emitter-bucket capture | `Particles.Forget` | registry `Revoke`; scene change `Particles.cs:480` | `Particles.ContextLost` (also frees the native stage buffer) |
| Grass patches + slot map (`GrassField`) | bin scatter | `GrassField.Forget` | registry `Revoke`; `Scene::ReloadGrass` `GrassField.cs:305` | `GrassField.ContextLost` |
| Env cube freshness + BSP key (`EnvProbe`) | `EnvProbe` capture | `EnvProbe.Revoke` | registry `Revoke` | `EnvProbe.Forget` |
| Caster set + skin records + texture-alpha verdicts (`CasterPass`) | `CasterPass.Enroll` | `Release` | `CasterPass.cs:1003,1005,1008` | routes through the same `Release` — see below |
| Owned texture copies (`OwnedTextures`) | first in-bracket sight of an engine texture name, `OwnedTextures.Draw` | per-name: the engine's own delete funnel, `RenderInterface::DeleteTextures` → `Retire`. Wholesale: `OwnedTextures.Revoke` | the delete hook (`OwnedTextures.Install`); registry `Revoke`. Both only **queue** — `OwnedTextures.Drain` deletes, off `GlContextService.InBracket` | `OwnedTextures.ContextLost` drops map, owned-set and queue, deletes nothing |
| Owned vertex/index buffer copies (`OwnedBuffers`) | first in-bracket sight of an engine pool buffer name, `OwnedBuffers.Draw` (from `BuildVao`) | per-name: the engine's own delete funnel, `GLRender::DeleteBuffer` → `Retire`. Wholesale: `OwnedBuffers.Revoke` | the delete hook (`OwnedBuffers.Install`); registry `Revoke`. Both only **queue** — `OwnedBuffers.Drain` deletes, at the top of the colour bracket | `OwnedBuffers.ContextLost` drops map, dirty list and queue, deletes nothing |
| Caster VAOs over those copies (`CasterPass._vVaoOwn`) | `CasterPass.EnsureVao`, inside the shadow bracket | slots retired by `CasterPass.Release` (`_vStale`), names deleted by the next `EnsureVao` | `CasterPass.Lifecycle` — the same three edges `RevokeAll` watches, and it runs from the pass slot, ahead of the present that revokes | kept, like the pass's other GL names — see below |
| Bindless handle cache `_hTid/_hVal/_hRes` | `AlloyRenderer.HandleFor` | `DropHandle`, per owned name, from `OwnedTextures.Drain` before the texture is deleted | — the enrollment `WipeHandles` is now a no-op while `ownedtex` is armed (#220) | `AlloyRenderer.ForgetHandles`, via `OwnedTextures.ContextLost` |

Two more pointer-keyed tables are **enrollment-scoped scratch**, listed for completeness rather than
because they have an edge: `AlloyRenderer._mKey/_mVal` (the material→texture-sort-key intern, cleared
at `Enroll.cs:78`) and `AlloyCensus._refGob` (the refused-gob set, cleared by `OwnScanBegin`,
`AlloyCensus.cs:833`). Both are wiped at the top of the enrollment that fills them and are never read
across one, so a recycled pointer has no window to be believed in. They need no `Revoke` — but they
are the shape that does, so if either ever outlives an enrollment it joins the table above.

### GL objects (`ContextLost`)

| Resource | Created by | Dropped by | Caller |
|---|---|---|---|
| `_prog46/_progLit46/_progDepth/_progDepthKey/_progWater`, `_fbo46`, `_drawBuf/_texHBuf/_boneBuf`, `_vVao46[]`/`_vVaoOwn[]` | `EnsureProgram` / `EnsureTarget` / `UploadDraws` / `EnsureVao` | `AlloyRenderer.ContextLost` | registry `ContextLost`; `_progLit46`/`_progWater` are additionally *deleted* and rebuilt by `TakeProgramRebuild` (`Programs.cs:114`) when a compile-time dial moves — the one deletion path that is neither edge |
| Water snapshot FBO + colour/depth textures | `FrameSnapshot.Ensure` | `FrameSnapshot.Forget` | registry `ContextLost` |
| Scene-depth FBO + texture | `SceneDepth.Ensure` | `SceneDepth.Forget` | registry `ContextLost` |
| Light SSBOs (`_bufLights/_bufCounts/_bufIndices/_bufStats`) | `AlloyLights` | `AlloyLights.Forget` | registry `ContextLost` |
| Env cube + its FBO/depth | `EnvProbe` capture | `EnvProbe.Forget` | registry `ContextLost` |
| Post targets, FBO, fullscreen VAO, **owned depth attachment** (#214) | `PostChain` / `PostChain.EnsureDepth` | `PostChain.Forget` | registry `ContextLost` |
| Grass VAO/VBO/program/samplers/push texture | `GrassField.EnsureBuffers` | `GrassField.ContextLost` | registry `ContextLost` |
| Beam program + per-VDO VAO **pairs** (engine buffers and Alloy copies, `_vVao`/`_vVaoOwn`) + element buffers | `Beams.EnsureProgram` | `Beams.ContextLost` | registry `ContextLost` |
| Particle program/VAO/VBO/sampler + native stage buffer | `Particles.Ensure*` | `Particles.ContextLost` | registry `ContextLost` |
| Caster depth program, FBO, depth array, persistent-mapped buffers, VAOs | `CasterPass.EnsureGl` | **nothing** — kept | see below |
| Owned texture copies (`OwnedTextures`) | `OwnedTextures.Copy` | `OwnedTextures.ContextLost` | registry `ContextLost` |
| Owned buffer copies (`OwnedBuffers`) | `OwnedBuffers.Copy` | `OwnedBuffers.ContextLost` | registry `ContextLost` |

The owned depth texture has no `Revoke` edge: its contents are per-pass (cleared in `PostChain.Begin`, blitted back into the engine's attachment by `ResolveDepth`), so there is no world-edge state in it to invalidate. It *is* reallocated whenever the engine's attachment changes format or the target changes size, which is the same edge a resolution change or an alt-tab device reset arrives on.

### The geometry update path (#213)

A copy is not a snapshot. Three classes rewrite their vertices while they are on screen, and each has
a written path rather than a per-frame full re-upload:

- **Dangly meshes** (`PartDanglyMesh::Animate` @0x49c935) and **morph-animated nodes**
  (`MdlNodeAnimMesh::InternalControl` @0x4996b9) commit through
  `VertexDataObject::CommitItemDynamicData` @0x45c7d0, which tail-calls
  `RenderInterface::UnlockPool(token, size, offset)` with **that one item's byte span** — size and
  offset in bytes, straight into `glBufferSubData` (`GLRender::UnlockPool` @0x4cfc80). `MarkDirty`
  coalesces every span seen since the last refresh into one `[Lo, Hi)` range per buffer and
  `OwnedBuffers.Respan` copies exactly that range. A pool that grew (size changed, i.e. `ResizePool`
  then a full `glBufferData`) falls back to a whole-buffer re-blit, because a sub-range copied into a
  copy of the old size is two layouts spliced together. `size == 0xFFFFFFFF` is the engine's
  "whole pool" and promotes the entry to the full path.
- **Grass** (`CAurTriangleList::BuildGrassPolys` @0x4b5286) commits the same way, but Alloy's grass
  pass draws from its **own** instance VBO (`GrassField.EnsureBuffers`) and never reads the engine's.
- **Particles** likewise: `Particles` orphans and re-fills its own `DYNAMIC_DRAW` VBO each frame
  (`Particles.cs:1362`), so no engine pool is involved. **Beams** and the **post/fullscreen** VAOs are
  Alloy-created too.
- **Skinned parts** need no update path at all. The bone streams are indices and weights — static per
  mesh — and the pose is a uniform block (`UploadBones`), so the buffer never changes.

The refresh runs at the top of **both** brackets — the shadow bake (`CasterPass`, first) and the
colour pass (`AlloyRenderer.Frame`) — so a mover's shadow and its mesh are never a frame apart.
A write that lands after the last refresh of a frame is one frame stale on screen, then correct.

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
