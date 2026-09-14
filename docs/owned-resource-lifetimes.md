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

`OwnedTextures` (#259) is the one row with **no `Revoke` slot at all**, and the reason generalises. Its
resources are keyed on texture identity (a 64-bit hash of the resolved name at `basic+0xa8`, lower-cased,
and the PLT schemes at `+0x48`/`+0xd4`, with the atlas cell in the low 16 bits), so a part-table
wipe or an `alloy.enabled` toggle invalidates nothing in them. More to the point, they are populated *only* at
the engine's upload funnel — so wiping them on an edge the engine does not rebuild across would leave
every texture borrowed for the rest of the session, with no path back.

Its two edges are both **engine funnels rather than Alloy events**. The engine's GL name is only an
*index* onto a resource — the lookup the hot `Draw` path needs, because a material hands out a tid and
nothing else — and it is rewritten at the two moments a tid changes meaning:

* **The upload**, `RenderInterface::CreateTexture` @0x45d200 — the engine's per-cell upload, and the only
  window in which decoded pixels exist. Alloy re-invokes it against a texture of its own, points the index
  at the resource, and retires the previous texture for that resource. This is what makes a re-upload
  visible immediately instead of at the next world edge (#227's structural cause).
* **The delete**, `RenderInterface::DeleteTextures` @0x45d1d0 — the single funnel every engine texture
  name dies through, and **the world edge for the INDEX** (ADR 0023): the tid stops meaning its resource,
  but the resource itself goes idle and stays resident under `texcache_mb`, because the engine WILL ask
  for the same content again and `BuildAndStoreTexture` is hooked to hand the resident texture back
  without a read, decode or upload. Eviction is bracket-side, oldest idle first, past the budget. An **area transition** fires it and none of the world edges
  above (#220 measured one part-table wipe in an entire session). It drops the index entry and QUEUES the
  texture — it runs at a frame edge in the ENGINE context, where a GL delete would go to the wrong context
  entirely, so `OwnedTextures.Drain` issues it inside the next 4.6 bracket. Same split `EnsureVao` makes
  for its retired VAO names.

If you add a table the engine hands out by GL name, these are the edges you need — not `Revoke`.

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
Part::~Part           -> DrawFunnelService.OnPartDtor  (DrawFunnelService.cs:367) per-part death notice
                            -> SkyBox.Revoke(part)      (DrawFunnelService.cs:371)
                            -> PartTable Die(e)         (DrawFunnelService.cs:381)
VDO repack            -> AlloyRenderer.OnVdoRepackHook  (AlloyRenderer.cs:951) per-VDO
                            -> Beams.OnRepack, CasterPass.OnVdoRepack
Scene::ReloadGrass    -> GrassField.OnReloadGrass       (GrassField.cs:305)
module unload         \
part-table wipe        |-> AlloyRenderer.RevokeAll      (AlloyRenderer.cs:1003-1007)
4.6 context lost      /       -> WipeHandles + PassRegistry.Global.Revoke()   (Enroll.cs:26-27)
                              -> CasterPass.Lifecycle -> Release              (CasterPass.cs:1003-1008)
4.6 context lost      -> PassRegistry.Global.ContextLost()                    (AlloyRenderer.cs:1003)
table pressure        -> PartTable.OnTablePressure -> WipeTables              (PartTable.cs:453)
Scene changed         -> SceneWalk.Reset -> WipeTables                        (SceneWalk.cs)
scene walk sweep      -> SceneWalk.Sweep -> Die(e, "unreachable")             (SceneWalk.cs)
```

**#256 adds a second death edge for a record, and it is not a destruction.** `Part::~Part` covers parts
that are *freed*; the sweep covers parts that are merely *no longer in the scene* --
`Scene::ChildRemove` @0x462cc0 and `Gob::Amputate` @0x443fa0 both unlink without freeing, so a gob that
left the area generates no dtor at all. A record now lives exactly as long as the part is reachable from
`Scene+0x130` / `Scene+0x188`, which is a fact the walk establishes rather than a window it infers.

`RevokeAll` runs the world walk **above** its empty-table guard. The ownership count says what *Alloy
took*; it says nothing about what the *passes captured*. Sky is the only pass with a per-part death
notice of its own (`DrawFunnelService.cs:371`); for beams, particles and grass this walk is the **only**
teardown, and even sky's `Scene` pointer and drawn-generation are reachable no other way.

## The table

### Captured world state (`Revoke`)

| Resource | Created by | Revoked by | Caller | Context loss |
|---|---|---|---|---|
| Ownership table `_oTri/_oGob/_oVdo/_oEntry` | `AlloyRenderer.Enroll` | `RevokeAll` | `AlloyRenderer.cs:995,1003,1005,1007` + per-part `Revoke(i)` on repack (`:951`) and identity change (`Batches.cs:234`) | via `RevokeAll` |
| Part table (`PartTable`) | `SceneWalk.Enrol` + `SceneWalk.TryBind` (#256; #258 folded the group intern into the record) | `WipeTables` / `Die` / `SceneWalk.Sweep` | `PartTable.cs`, `OnPartDtor`, `SceneWalk.cs` | none — CPU only |
| VAO cache `_vVdo/_vStale` | `EnsureVao` | `RevokeAll` retires the slots | `Enroll.cs` | GL names zeroed by `AlloyRenderer.ContextLost` |
| Bindless handle cache `_hTid` | `AlloyRenderer.Issue` staging | `WipeHandles` — **only while `alloy.ownedtex=0`** (#220); armed, see the row below | `Enroll.cs:26` (release) and `:62` (enrollment) | `AlloyRenderer.ForgetHandles` |
| Water planes (`WaterBodies`) | plane measurement during enrollment | `WaterBodies.Clear` | registry `Revoke` slot | none — CPU only |
| Sky parts (`SkyBox`) | `OnPartDraw` capture window | `SkyBox.Forget`; per-part `SkyBox.Revoke` | registry `Revoke`; `DrawFunnelService.cs:371`; scene change `SkyBox.cs:136` | none — holds no GL |
| Beam parts + VAO slots (`Beams`) | bucket-15 capture | `Beams.Forget` | registry `Revoke`; scene change `Beams.cs:173`; repack `:405` | `Beams.ContextLost` |
| Emitter table (`Particles`) | emitter-bucket capture | `Particles.Forget` | registry `Revoke`; scene change `Particles.cs:480` | `Particles.ContextLost` (also frees the native stage buffer) |
| Grass patches + slot map (`GrassField`) | bin scatter | `GrassField.Forget` | registry `Revoke`; `Scene::ReloadGrass` `GrassField.cs:305` | `GrassField.ContextLost` |
| Env cube freshness + BSP key (`EnvProbe`) | `EnvProbe` capture | `EnvProbe.Revoke` | registry `Revoke` | `EnvProbe.Forget` |
| Caster set + skin records + texture-alpha verdicts (`CasterPass`) | `CasterPass.Enroll` | `Release` | `CasterPass.cs:1003,1005,1008` | routes through the same `Release` — see below |
| Owned textures (`OwnedTextures`) | the engine's upload funnel, `RenderInterface::CreateTexture` → `Adopt`; and, with `alloy.pltcpu=0`, a GPU-PLT composite queued at `glImageBasic` → `DrainPltCopies` (#360) | a re-upload through the same funnel, or the engine's `RenderInterface::DeleteTextures` → `Retire`. **No registry `Revoke`** — see above | the two hooks (`OwnedTextures.Install`). Both only **queue** — `OwnedTextures.Drain` deletes, off `GlContextService.InBracket` | `OwnedTextures.ContextLost` drops resources, index, owned-set and queue, deletes nothing |
| Owned vertex/index pools (`OwnedBuffers`) | the engine's upload funnel, `GLRender::UnlockPool` → `Take` (#260 — the buffer is *minted* there, into the engine's own pool descriptor) | the engine's `GLRender::DeleteBuffer`, which `DeletePool` @0x4d1d80 routes to for every named pool → `Retire`. **No registry `Revoke` deletion** — the buffer is the pool's and the engine frees it on that same edge; `OwnedBuffers.Revoke` only reports | the delete hook (`OwnedBuffers.Install`). Nothing is queued and nothing is deleted by Alloy — a second `glDeleteBuffers` on a recycled name would take out the pool that inherited it | `OwnedBuffers.ContextLost` drops both tables and the dirty list, deletes nothing |
| Caster VAOs over those pools (`CasterPass._vVao46`) | `CasterPass.EnsureVao`, inside the shadow bracket | slots retired by `CasterPass.Release` (`_vStale`), names deleted by the next `EnsureVao` | `CasterPass.Lifecycle` — the same three edges `RevokeAll` watches, and it runs from the pass slot, ahead of the present that revokes | kept, like the pass's other GL names — see below |
| Extra `.mtr` slots texture11+ (`MaterialExtraSlots`, #275) | `SharedMaterial::ParseField` hook records the name, `SharedMaterial::Init` hook loads it (`LoadTexture` = an ADDREF'd `AurTextureGetReference`) | `SharedMaterial::~SharedMaterial` hook: `AurTextureRelease` per still-live pointer, entry freed. **No registry `Revoke`** -- the reference lives exactly as long as the engine's own eleven do | the three hooks (`MaterialExtraSlots.Initialize`) | none -- holds no GL; the texture is the engine's |
| Bindless handle cache `_hTid/_hVal/_hRes` | `AlloyRenderer.HandleFor` | `DropHandle`, per owned name, from `OwnedTextures.Drain` before the texture is deleted | — the enrollment `WipeHandles` is now a no-op while `ownedtex` is armed (#220) | `AlloyRenderer.ForgetHandles`, via `OwnedTextures.ContextLost` |

Two more pointer-keyed tables are **enrollment-scoped scratch**, listed for completeness rather than
because they have an edge: `AlloyRenderer._mKey/_mVal` (the material→texture-sort-key intern, cleared
at `Enroll.cs:78`) and `AlloyCensus._refGob` (the refused-gob set, cleared by `OwnScanBegin`,
`AlloyCensus.cs:833`). Both are wiped at the top of the enrollment that fills them and are never read
across one, so a recycled pointer has no window to be believed in. They need no `Revoke` — but they
are the shape that does, so if either ever outlives an enrollment it joins the table above.

### What is chased out of a part, and under which edge (#336)

The route walk dereferences an owned `PartTriMesh*` every frame it is on screen: the alpha
(`part+0xa0`), the tile record, the dissolve terms, and on a colour-cadence frame the `MdlNode`
colour triples. Every one of those reads sits behind `PartTable.EntryLive`, and `EntryLive` is the
**only** lifetime guard — `Part::~Part` → `OnPartDtor` marks the entry dead (the #221 tombstone keeps
a recycled address from reviving it), so a pointer read after the death notice is refused, not chased.

The three identity fields Alloy captures at enrolment are, per the #336 Ghidra store scan (every write
to `[reg+0x58|0x88|0xb0]` in the binary; the full tables are on the ticket):

| field | writers after construction | edge |
|---|---|---|
| `part+0x58` `Material*` | **none** — set in `PartTriMesh::PartTriMesh`, freed by `Part::~Part` | the death notice |
| `part+0x88` `VertexPrimitive*` | `PartInitMaterial` @0x43de50, once, on a tree `CreateInstanceTree` created moments before (`NewCAurObject`, `SpawnPartsForTile`, `Gob::Append`); never on a drawn part; the old value is never freed by it | enrolment sees the final value |
| `part+0xb0` texture rotation | `SetPartTextureRotate` @0x461670, once, from `SpawnPartsForTile` | enrolment sees the final value |

So the per-frame compare `IdentityOk` used to run bought nothing; it is now an **audit** on the colour
cadence (every 8 frames per slot, every frame under `/alloy poll 1`) and its hit counter is the
`identity N` figure on the [alloy] line. A non-zero steady value there means the scan missed a writer
(a LEA-then-store, a `memcpy` over the struct) and that writer becomes the edge to hook.

What pointer identity never covered, and still does not: **material content**. `Material::Refresh`
@0x4c7770, `Material::ReplaceTexturePLT` @0x4c7a10 and `SetMaterialParam` rewrite the material in place
(creature re-skin, equip, texture-replace VFX — all cold); Alloy re-fetches the diffuse pointer per
frame and re-mints handles on a changed `CAurTexture*`, which is what makes those visible. `Gob::SetLODModel`
@0x43e6a0, the per-frame LOD swap, rotates the gob's root pointers and writes no part field.

### GL objects (`ContextLost`)

| Resource | Created by | Dropped by | Caller |
|---|---|---|---|
| `_prog46/_progLit46/_progDepth/_progDepthKey/_progWater`, `_drawBuf/_texHBuf/_slotBuf/_boneBuf`, `_vVao46[]` | `EnsureProgram` / `EnsureTarget` / `UploadDraws` / `EnsureVao` | `AlloyRenderer.ContextLost` | registry `ContextLost` (#242: `ContextLost` also zeroes `_recCapGpu` and bumps the record epoch -- the persistent per-draw records lived in `_drawBuf`/`_texHBuf`, so a name-drop alone would leave every slot believing it was still uploaded); `_progLit46`/`_progWater` are additionally *deleted* and rebuilt by `TakeProgramRebuild` (`Programs.cs:114`) when a compile-time dial moves — the one deletion path that is neither edge |
| Water snapshot FBO + colour/depth textures | `FrameSnapshot.Ensure` | `FrameSnapshot.Forget` | registry `ContextLost` |
| Scene-depth FBO + texture | `SceneDepth.Ensure` | `SceneDepth.Forget` | registry `ContextLost` |
| Light SSBOs (`_bufLights/_bufCounts/_bufIndices/_bufStats`) | `AlloyLights` | `AlloyLights.Forget` | registry `ContextLost` |
| Env cube + its FBO/depth | `EnvProbe` capture | `EnvProbe.Forget` | registry `ContextLost` |
| Post targets, FBO, fullscreen VAO, **owned depth attachment** (#214) | `PostChain` / `PostChain.EnsureDepth` | `PostChain.Forget` | registry `ContextLost` |
| Post stack ping-pong pair, FBO, per-stage programs (#375) | `PostStack.EnsureTargets` / `PostStack.EnsurePrograms` | `PostStack.Forget` | registry `ContextLost` |
| SMAA edge/blend/area/search textures, FBO, sampler, edge + weight programs (#377) | `SmaaStage.Size` / `SmaaStage.Build` | `SmaaStage.Forget` (through `PostStack.Forget`) | registry `ContextLost`; `Build` also *deletes* and rebuilds the two programs when the option's preset moves |
| SSAO raw + visibility R8 textures, FBO, visibility + blur programs (#379) | `SsaoPass.EnsureTargets` / `SsaoPass.EnsurePrograms` | `SsaoPass.Forget` | registry `ContextLost` |
| Grass VAO/VBO/program/samplers/push texture | `GrassField.EnsureBuffers` | `GrassField.ContextLost` | registry `ContextLost` |
| Beam program + per-VDO VAOs (`_vVao`) + element buffers | `Beams.EnsureProgram` | `Beams.ContextLost` | registry `ContextLost` |
| Particle program/VAO/VBO/sampler + native stage buffer | `Particles.Ensure*` | `Particles.ContextLost` | registry `ContextLost` |
| Caster depth program, FBO, depth array, persistent-mapped buffers, VAOs | `CasterPass.EnsureGl` | **nothing** — kept | see below |
| Owned textures (`OwnedTextures`) | `OwnedTextures.Adopt`, or `OwnedTextures.Copy` for a GPU-PLT composite (#360) | `OwnedTextures.ContextLost` | registry `ContextLost` |
| Owned pool buffers (`OwnedBuffers`) | `OwnedBuffers.Take`, in the ENGINE's context (buffers share, #70) | `OwnedBuffers.ContextLost` — names dropped, nothing deleted | registry `ContextLost` |

The owned depth texture has no `Revoke` edge: its contents are per-pass (cleared in `PostChain.Begin`, blitted back into the engine's attachment by `ResolveDepth`), so there is no world-edge state in it to invalidate. It *is* reallocated whenever the engine's attachment changes format or the target changes size, which is the same edge a resolution change or an alt-tab device reset arrives on.

### The geometry update path (#213, rewritten by #260)

There is no copy to keep in step any more. `GLRender::UnlockPool` @0x4cfc80 is the engine's sole push
of a pool's host shadow into GL, and Alloy now **performs** that push rather than observing it: the
hook mints the pool's GL buffer (writing the name into the descriptor at pool+0x0c, exactly where
`UnlockPool` would have), replays the engine's own three upload branches off the usage word at
pool+0x14, and never calls the original. So a write is on the GPU at the moment the engine commits it,
and the ordering hazard a bracket-top refresh had — census E10's reading of #227 — does not exist.

- **Dangly meshes** (`PartDanglyMesh::Animate` @0x49c935) and **morph-animated nodes**
  (`MdlNodeAnimMesh::InternalControl` @0x4996b9) commit through
  `VertexDataObject::CommitItemDynamicData` @0x45c7d0, which tail-calls
  `RenderInterface::UnlockPool(token, size, offset)` with **that one item's byte span** — size and
  offset in bytes, straight into `glBufferSubData`. That is the upload; what survives of the old
  machinery is the *signal*, `OwnedBuffers.DirtySince`, which the caster pass reads to decide a
  re-bake and `ClearDirty` closes once a frame. `size == 0xFFFFFFFF` is the engine's "whole pool" and
  substitutes the pool's own byte count, as it does in the engine.
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

## Engine-draw-driven lazy initializers (#284)

`PartTriMesh::Draw` @0x4513d0 was never only a draw. Under #264 the engine's bucket walk is gone, so
anything it used to *initialise on the way past* now has to be driven by Alloy. Two of these were
found the bad way — something was missing on a live run and the log said why afterwards — so this is
the list, read off the binary rather than off a bug report.

The method: take `PartTriMesh::Draw` down and ask of each call whether it mutates state Alloy later
reads. The vtable slots below are dumped from `_ZTV11PartTriMesh` (+0x10 for the Itanium ABI header),
not guessed.

That method has one blind spot, and it cost the third entry: **a skinned part never enters
`PartTriMesh::Draw`.** `PartSkin::Draw` @0x4830b0 goes straight to `RenderSkinned`, so walking the
tri-mesh draw could not have found the skinmesh initializer no matter how carefully it was done. The
list is only as complete as the set of draws it was read off; there are two, and both are below.

| Engine call (from `PartTriMesh::Draw`) | Mutates state Alloy reads? | Alloy-side driver, and its cost |
|---|---|---|
| `IsPartRenderable` @0x450e60 | No — pure read of `part+0x96` / node `+0x118`/`+0x278`/`+0x284`. | none needed |
| vtable +0xc8 `Mat` @0x450960 / +0x100 `MatStatic` @0x450560 | **Fixed-function GL state only** (`SetColor4f`, `SetMaterialv`, `SetBlendingMode`, `Enable/Disable`), plus the three gob modulation terms. Nothing persists on the part. | Alloy's own pass state + `AlloyCensus.GobAlphaMul`/`GobDiffuseMul`/`GobEmissiveAdd`. Per-part, but they are reads. |
| `Material::BindAllStandardTextures` @0x4c6e30 → `CAurTexture::BindInUnit` @0x48e1e0 | **Binds, never builds.** It reads `AurTextureGetTID` and calls `AurTextureBindTIDInUnit`. It does, however, tick the texture CONTROLLER (`AurTextureGetController` + slot 0x10, under `enabletexturecontrollers`) — the procedural/animated-texture update. | Nothing drives controllers for owned parts. The particle pass still routes through `BindInUnit`; the owned colour pass does not. **Accepted, unmeasured**: NWN's controller population is Life/Wave water shaders, which Alloy replaces with its own water pass. Re-open if a procedural texture reads frozen. |
| `Material::CheckTextureControllers` @0x4c5d20 | Only `SetControllerEnabled` under `HasBumpyShinyMap`. | none needed |
| vtable +0x38 `VertexPrimitive::SetupArrays` | **YES — the whole VDO.** It fills `vp+VpVdo`/`vp+VpItem` and latches its verdict. Nothing else calls it, so no record ever got a VDO and the world went black. | `SceneWalk.TryBind`, ONE call per record ever (`PartTable._pSetupTried`), budget 512/frame in the steady state and unbounded on a new `Scene` (an area load is already a hitch). |
| vtable +0x120 `TransformTextureMatrix` / +0x128 `RestoreTextureMatrix` @0x4500b0/0x4500d0 | GL texture-matrix push/pop from `part+0xb0` (the texture angle). | Alloy folds the angle into its own record. No engine state persists. |
| `RenderBumpyShiny` @0x451200 | Lazily creates the `vswater`/`fswater` custom program into `DAT_01aab338`. Engine-only — Alloy never reads it. | none needed |
| `RenderEnvironmentMapped` @0x451040 | Reads `gob+0x480` (a **`CAurTexture*` env-map REPLACEMENT**, not a flag), scales ambient by `gob+0x108` when `gob+0xf6` is set, falls back to `mat+0x40` then `currentscene+0x1e8`. Binds; mutates nothing. | `AlloyCensus.GobHasEnvMap` sets `AlloyCandidate.Env`; #121's IBL probe supplies the texture. |
| `RenderFlat` @0x450eb0 | Sets the render hint and the scene's shadow-map bind, then `RenderVertexData`. | Alloy's own submission. |

And one more from the *other* draw, because a skinmesh never enters `PartTriMesh::Draw` at all —
`PartSkin::Draw` @0x4830b0 goes straight to `RenderSkinned` @0x482c70:

| Engine call (from `RenderSkinned` @0x482c70) | Mutates state Alloy reads? | Alloy-side driver, and its cost |
|---|---|---|
| Part vtable +0xe0 `PartSkin::PostProcess` @0x483120 | **YES — the entire bind pose.** It writes the bone count `node+0x284`, the inverse bind-pose lists `+0x2f0`/`+0x300` and the 64-entry bone map `+0x320` — which is exactly and only what `SkinPose.Solve` reads. `RenderSkinned`'s *first act* is to call it, so in vanilla every skinmesh was prepared by its own first draw. | `SkinPose.NoteUnprepared` at the refusal, `SkinPose.DripPrepares` at the frame anchor: **2 calls/frame, one attempt per record** (`PartTable._pPoseTried`). See below. |

### The one that was missed: the skinmesh bind pose

`SkinPose` already knew this and said the wrong thing about it: "the part is handed back, the engine
draws it once, and the next frame solves." Since #264 there **is** no engine draw, so an unprepared
`MdlNodeSkin` was never prepared — a permanent hole, one whole creature at a time (`RevokeSkinned`
takes the rest of the gob with the cloak).

**The ABI, from the binary and not from the mangled name.** `_ZN8PartSkin11PostProcessEP4Part` mangles
identically whether the member is static or not, so the arbiter is `_ZTV8PartSkin`: slot **+0xe0**
(+0x10 for the Itanium header), i.e. it is a **virtual** override of `Part::PostProcess(Part*)`
@0x436e10. `RenderSkinned` calls it as `(**(code**)(*part + 0xe0))(part, part)` — the part as *both*
`this` and the explicit argument. That argument is the base class's recursion context: `Part::PostProcess`
walks `part+0x38`/`+0x40` and threads it down the children unchanged. `PartSkin`'s override never reads
`rsi` at all (disassembled — it is dead from entry), so passing the part twice is both correct and what
the engine does. The slot must be dispatched off a vptr **proven to be `PartSkin`'s exactly**: on any
other part the same slot is the base version, which recurses the whole subtree.

**It is idempotent, and it latches itself.** Entry guard: `node+0x278` (a `MdlNodeTriMesh_PostProcess*`,
the temporary load-time data) non-null. Tail: node `+0xa8` = `MdlNodeSkinRemoveTemporaryArrays`
@0x437540 → `MdlNodeSkin::InternalRemoveTemporaryArrays` @0x454600 →
`MdlNodeTriMesh::InternalRemoveTemporaryArrays` @0x4542a0, which `operator delete`s that block and
writes **0** to `node+0x278`. So the second call returns immediately — which is why the engine could
afford to call it on every single skinned draw, and why one attempt per record is the whole budget.
Same shape as `SetupArrays` above.

**The corollary is the residual hole**: `node+0x278 == 0` with `node+0x284 == 0` means the temporaries
are gone and *nothing* can ever prepare that node. That is counted separately and reported as
`PERMANENT -- no bind data left`, so a remaining missing creature is attributable rather than silent.

**Alloy-side driver**: `SkinPose.Solve` notes the part at its second refusal branch (cold — a refusal,
which in a settled area is zero; a null `MdlNode` is never noted, because `PostProcess` dereferences it
unguarded). `SkinPose.DripPrepares` spends the queue on the frame anchor beside
`OwnedTextures.DripRebuilds`, **with the engine's context current** — load-bearing for the same reason:
`PostProcess` ends in `RenderInterface::RequestPool`/`LockPool`/`UnlockPool`, and running the engine's
own pool traffic inside Alloy's 4.6 bracket would leave the engine's bind cache naming objects in the
other context. It runs ahead of the owned draws, so a node repaired here poses the same frame, and the
upload goes out through `UnlockPool` — #260's funnel — so the geometry lands in owned residency by
itself.

**Cost**: at most **2** `PostProcess` calls per frame, and **one attempt per record for as long as that
record lives**. Two rather than the texture drip's four because the call is heavier — it rebuilds the
node's vertex indices and tangent basis (`MdlNodeTriMesh::BuildVertexIndices` / `GenerateVertexData` /
`CalculateTangentSpaceBasis`) and takes two pools. Vanilla paid all of it in one frame on the first
draw of every new skinmesh, so this is not a new cost, only a metered one.

**Guards at the drain, not at the enqueue.** A `Part*` is borrowed and its death notice is `Part::~Part`
(#145/#221), so the record is re-proven with `PartTable.EntryLive(e, part)` immediately before the call,
and `PartTable.Skinned(e)` — an exact `_ZTV8PartSkin` compare taken at the walk — is the type proof. The
node is re-read cold: a sibling part sharing the `MdlNodeSkin` may have prepared it already, which
counts as a win and costs no call. One side effect worth stating because it lands on a borrowed object:
`PostProcess` opens with `Gob::Reset` @0x499bd0 on `part+0x50`, so repairing a live creature resets its
gob — exactly what the engine's own first draw did.

**Ledger lifecycle**: `PartTable._pPoseTried` is a record column, not a set of part pointers, so it
clears exactly where the part underneath can have changed — a slot reclaimed by `Insert`, a re-bound
record (`SceneWalk.Rebind`) and `WipeTables`. That is deliberately *not* the `OwnedTextures` ceiling: a
recycled address is never denied its attempt. `WipeTables` also calls `SkinPose.Forget`, because every
queued entry index names a record that no longer exists.

Reported on the `[alloy]` period line as `unprepared skinmeshes:`, session totals — beside the skinmesh
pose counters it belongs with, not on `[rspshim]` where the texture drip reports.

### The one that was open: the texture build

**Nothing on the draw path builds a texture.** The engine builds one exactly twice:

- `AurTextureGet` @0x48cc10 — `CExoArrayList<CAurTextureBasic*>::Add(&RefreshTextures, tex)` on a
  newly created texture (creation edge);
- `AurTextureSetLayered` @0x489bd0 — `AddUnique` on the same list when a PLT scheme changes
  (`Gob::ReplaceTexture` → `Material::ReplaceTexturePLT` → here; the appearance edge).

`AurTextureMakeCurrent` @0x48f560 (sole caller `Scene::Render`, which still runs under the shim)
drains that list through `AurTextureBuildAndStoreAll` @0x48e680 and then sets the count to **0 —
whether or not each build succeeded**. And `CAurTextureBasic::BuildAndStoreTexture` @0x48b320
soft-fails: it runs `ConstructImage` @0x48a5d0 once (latched by `basic+0xe6`), and if the decoded
image at `basic+0x58` is still null it never reaches `glImageBasic` @0x48bcf0, so the GL id at
`basic+0x6c` stays 0. `CResPLT::OnResourceServiced` @0x1136640 only parses the file — **no retry path
exists anywhere**. The one-shot queue can lose its race with residency, permanently.

Vanilla survived it because the engine's own draw bound id 0 and drew a texture-less part nobody
looked at twice. Under #264 an Alloy part with no diffuse id is *skipped*, which reads live as a
player character with no head.

**Alloy-side driver**: `AlloyCensus.TryOwnCandidate` notes a live-but-idless diffuse at enrollment
(cold, once per candidate per scan) into `OwnedTextures.NoteUnbuilt`; `OwnedTextures.DripRebuilds`
spends it on the frame anchor where `Beams.Enroll`/`SkyBox.Enroll` already run — once a frame, before
the Alloy bracket, **with the engine's context current**. That last part is load-bearing rather than
convenient: `BuildAndStore` re-runs the engine's own upload (`SetActiveTexture`, `AurTextureBindTID`,
`RenderInterface::CreateTexture`) and the engine caches what it thinks is bound, so running it inside
Alloy's 4.6 bracket would leave that cache naming objects in the other context — #220's corruption
with a different cause. It is the same reason `Adopt` is safe: the funnel fires where the engine
already is.
**Cost**: at most 4 `CAurTexture::BuildAndStore` @0x48bc80 calls per frame, and **one attempt per
texture for the session** — `BuildAndStore` is `CAurTextureBasic::Reset` @0x4858c0 (which clears
`+0xe6`/`+0xe7`, deletes the GL names and zeroes `+0x6c`, so the rebuild genuinely re-runs
`ConstructImage`) plus a full decode and upload. The pointer is re-proven through `AurTex`'s vtable
guard at the drain, never at the enqueue, because a `CAurTexture*` is borrowed. Because the rebuild
routes through `RenderInterface::CreateTexture`, a successful retry lands in the owned table by
itself. Reported on the `[rspshim]` period line as `unbuilt textures:`.

The ledger is cleared on `ContextLost` — every engine texture is unbuilt again on the far side, so
keeping it would refuse the whole world its single retry.

**Known ceiling** (`ponytail:` in `OwnedTextures`): the ledger keys on the raw `CAurTexture*`, which
the engine frees on an area transition, so a new texture allocated at a recycled address is denied
its one retry — the permanence this fixes, in miniature. Upgrade path is to clear it on the first
`Retire` batch after a world edge, where the pointer population turns over anyway. `RebuildNote`'s
retried-vs-won split is the instrument that would show it: it stops converging.
