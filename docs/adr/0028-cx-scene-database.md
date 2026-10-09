# ADR 0028: CX owns the scene database -- object model, lifetime contract, threading

Status: accepted (2026-10-01, CX-115). Amends ADR 0016 and ADR 0018, which put the scene database out of
scope for M4 and M5; ADR 0010 holds unchanged. Implemented by CX-112..CX-120 on `total-aurora-replacement`.

**2026-10-07: the scene database is how Alloy draws.** `alloy.scenedb` is gone: `alloy.enabled=1` installs the seam
and Alloy draws the database, `alloy.enabled=0` is vanilla, and a seam that cannot install stands Alloy down
(`docs/alloy-refusals.md` row 5c). The engine-scene path ("mode 0") is dead code being deleted; the tables below
predate that.

## Context

Through tier 3 (CX-102) Alloy draws every pixel and owns the per-frame pipeline, but every record it draws is
still an engine object it OBSERVES: a `Part*` it enrolled, a `Gob*` it walked, a `CAurTexture*` it borrowed.
`docs/owned-resource-lifetimes.md` is the cost of that: a death notice per kind (`Part::~Part`, the
`Gob::SetScene` leave, `DeleteTextures`, the VDO repack), a tombstone so a recycled address cannot revive a
record, `VdoSig` to notice a VDO reborn at the same address, the area edge for the identities nothing else
announces, and the census refusals for the engine states Alloy could not express. Every one of those exists
because the engine produces the objects and CX infers their lifetimes.

Tier 4 inverts it. The game layer (`CNWC*`, `CGui*`, `CNWCAnimBase`, VFX/projectiles) reaches the render
layer only through the Aurora API: `NewCAurObject` / `NewCAurScene` / `NewCAurCamera` / `RemoveAuroraObject`,
the `CAurObject` / `CAurScene` / `CAurCamera` / `CAurString` / `CAurBehavior` vtables, and ~130 `Aur*` free
functions (CX-172 section 5). If CX answers that API, the engine's
`Scene`/`Gob`/`Part` never exist in Alloy mode, and CX PRODUCES every lifetime edge instead of observing it.

## Decision

### 1. The object model: struct-of-arrays records behind generation-checked handles

`CX.Alloy.Db.SceneDb` holds four tables -- scenes, objects, parts, cameras -- as parallel column arrays
indexed by a `DbId` (20-bit index, 12-bit generation, 0 = null). Text objects are object records of
`ObjKind.Text` with their layout in a side table (CX-119).

- An **object** is the `Gob` equivalent: model, scene, attachment (object + part), position / orientation /
  scale, the colour folds, visibility, animation state, and a contiguous **part range**.
- A **part** is one instanced node: the `ModelDef` + node it instances, its parent part, its animated local
  pose, its world matrix, its enable and merged-away bits, its animated alpha / self-illumination, and the
  mesh-arena range it draws.
- A **model** (`CX.Alloy.Mdl.ModelDef`) is immutable after the parse and shared by every instance; its
  identity is its content (`ContentHash`), never an address. The model cache (CX-113) owns models and
  refcounts them; a mesh is uploaded to the arena once per distinct `MeshDef` (CX-114).

Struct-of-arrays because every hot walk (transform, cull, stage) reads two or three columns of every live
record; a column walk is a linear prefetchable stream, a pointer-per-object graph is a miss per object.
Handles because a freed slot's generation moves on at the free: a handle taken before it resolves to
nothing, so **no table keyed on a `DbId` needs a death notice to stay correct**.

### 2. What the game layer holds: a facade

The game layer holds `CAurObject*` and friends and calls through their vtables. CX mints one **facade** per
record: a native block whose first word is a CX-built vtable for the interface (an array of
`[UnmanagedCallersOnly]` entry points, one per slot, both OS orders: CX-183),
carrying the record's `DbId`. The game layer is close to vtable-only but not quite
(CX-182): it mutates the SafePointer back-reference list inline at +0x08,
reads a `Gob`'s name/scene/root/position/orientation/behaviour/external-data words and a `Scene`'s object
count, ambient and external-animate counter directly, and reaches a `StringGob`'s `CAurString` at +0x4c0 and a
`Camera`'s `CAurObject` at +8 by literal offsets. So a facade is a **layout-compatible shell**: those words sit
at the engine's offsets and are kept current by the setters that change them; nothing else in the shell is
state. Every slot resolves the handle and reads or writes columns. The out-of-line interface members the game
links non-virtually (`CAurObject::DieWithoutParent`/`AnimateExternal`, `CAurScene::SetAmbient`,
`Gob::SetScene`, `GobRenderLast`, `AurEnable/DisableShadowing`, ...) are hooked and answer for facades.
`CAurBehavior` objects stay real engine-layout objects (the game constructs and subclasses them itself); CX
calls their vtables.

The facade is freed when the game destroys the object (the interface's deleting destructor or
`RemoveAuroraObject`), after a two-frame quarantine in which its vtable is a **poison table** -- every slot
logs the call and the slot name once, loudly, and returns a zero value. A use-after-free in game code is then
a named line in `cx.log`, not a jump through freed memory. (No silent fallback: the poison table is a loud
refusal.)

### 3. The lifetime contract

CX is the producer of every edge, so there are exactly three, all synchronous on the game thread:

| Edge | Fired by | What dies |
|---|---|---|
| **Object destroy** | the facade's destructor slot / `RemoveAuroraObject` | the object record, its part range, its facade (after quarantine); a `Dirty.Destroyed` mark the renderer consumes |
| **Scene destroy** | the scene facade's destructor | every object still in it (each an object destroy), its tile set, its lights |
| **Model release** | the model cache when a model's refcount reaches 0 and its grace expires | the `ModelDef`, its arena ranges (deleted at the next render-side frame edge, never mid-frame) |

Plus the one edge CX does not produce, kept as is: **context loss** (`ContextLost`), terminal per session.

Consequences that ARE the contract:

- A record dies exactly once, when the game says so, and the renderer learns it from the dirty list in the
  same frame. There is no inference: no sweep, no audit walk, no tombstone, no address equality test.
- GPU resources key on content (`ModelDef`, `MeshDef`, texture resref + PLT scheme), never on a record, so an
  area change re-uses every mesh and texture the next area shares with the last.
- A `DbId` held across frames is safe by construction; a facade pointer held by CX code is not (it is the
  game's handle, and only the game decides when it dies).

### 4. Change-driven build

Every seam setter (position, orientation, scale, material, texture, visibility, animation, attachment, tree
change) writes its columns and marks the record dirty with one `Dirty` bit; the first mark of a frame appends
the record to the table's dirty list (`DbTable.Mark`). The per-frame build visits **only the dirty records and
the visible set**:

- the transform step recomposes world matrices for dirty objects and for parts the animation runtime wrote;
- the renderer's persistent per-draw records (`_drawBuf`) are rewritten only for dirty parts;
- the visible set comes from `AreaGrid` over static records (filed once, at creation) plus the movers.

A still scene therefore costs the build nothing but the visible-set query.

### 5. Threading

**The game thread owns the database.** Every seam call arrives on it and writes columns; the by-value getters
(`GetPosition`, `GetPartPosition`, the hit tests) answer there from the columns, without waiting on the
renderer. The animation runtime, the behaviour solvers and the transform step run there too, because the game
reads their output synchronously (attach points, event callbacks, `GetPartPosition`).

**The renderer reads a per-frame snapshot, on its own thread.** At the present boundary (the point the engine
would call `SwapBuffers`), the game thread publishes the frame: the dirty lists are folded into a
`RenderSnapshot` (changed world matrices, materials, bone palettes, visibility; view requests -- each
`RenderScene` the game issued this frame, in order; the GUI and text draws) and handed to the render thread,
which owns Alloy's GL 4.6 context and the present. The game thread then simulates frame N+1 while the render
thread draws N. Two snapshots ping-pong; the publish waits only if the render thread still holds N-1, so
latency is bounded at one frame and a CPU-bound frame costs the larger of the two threads' times, not their sum.

Render-side work that the game never reads moves with the renderer: particle simulation (except chunk emitters,
whose particles are objects), dangly solve, skin palettes, culling, staging.

The engine's own GL context stays on the game thread for what still reaches it (CX-120 lists it; target empty).
A residual entry runs in the engine context between frames, never inside the render thread's bracket.

### 6. Modes

ADR 0010 holds: vanilla mode keeps the engine's scene graph, and the mode is a boot decision. In Alloy mode the
scene database is the only scene: CX-120 installs the seam at boot, and no engine `Scene`/`Gob`/`Part` is
created for the session.

## What the contract deletes

From `docs/owned-resource-lifetimes.md`, once CX-120 is the Alloy path: the `Part::~Part` death notice and its
tombstone, `SceneWalk`'s sweep/audit and the `Gob::SetScene` leave notice, `VdoSig` and the recycled-address
re-harvest, the VDO drip and per-VDO VAOs, the enrolment walk, the census refusals that described engine
states, `OwnedTextures`' borrowed-pointer verdicts, and the area edge's address-keyed revoke list. The lifetimes
doc is rewritten against this contract (CX-115).

## As built (2026-10-02)

CX-115 re-read the decision against what CX-112..CX-120 actually built on `total-aurora-replacement`, and
re-verified it on 2026-10-02 after the streams' second round. The decision stands; these are the places the
build diverges from the text above, recorded here rather than by editing the decision.
`docs/owned-resource-lifetimes.md` part 1 is the full table, with the file and function behind each line, and
its "What writing this down exposed" section is the gap list;
CX-184 is the behaviour list — where DB mode differs from the vanilla
scene, improvement or otherwise.

**Object model.** `SceneDb` holds **six** tables, not four: `Scenes` 256, `Objects` 65536, `Parts` 524288,
`Cameras` 512, plus `Tiles` 16384 (CX-116, one record per tile placement) and `Lights` 16384 (CX-116, open to
gob lights too). `DbId`'s split is as decided (20-bit index, 12-bit generation, 0 = null), with two details
the text omits: bit 31 of the generation word is the allocated flag, which `DbTable.Free`'s `& 0xFFF` clears
with the bump; and the generation wraps, so a stale handle aliases again after 4096 frees of one slot.
Text objects are `ObjKind.Text` object records with a side table, as decided.

**The part table has one allocator** (was: "declared and unused"). `PartTree.Build` allocates through
`SceneDb.Parts.AllocRange`, which marks every slot of the run live and `Dirty.Created` from the same `_high`
cursor `Alloc` uses, and seeds the feed columns through `DbFeed.InitPart`. `PartTree` no longer keeps a second
cursor — the overlap that produced "the part table already holds 8884..8885" at the first GUI build is gone by
construction. `PartTree.Clear` calls `InitPart` too, so a recycled chunk cannot inherit the dead tenant's
recipe or renderer slot.

**Lifetime contract.** Object destroy, scene destroy and model release are the right three and are built as
described, synchronous on the game thread, with the arena's GL free correctly deferred to the render-side
`MeshArena.Pump` — which now runs in the frame fold (`DbFeed.FoldFrame`), not in the world colour frame, so a
GUI-only frame still collects finished work. Three amendments:

- **Texture release is a fourth edge, not part of model release.** A texture dies at refcount zero *plus* the
  `alloy.texcache` budget, six hops after the model (`ModelCache.Evicting` → `DrawRecipe.Forget` →
  `TexRegistry.Release` → idle LRU → `EvictPastBudget` → `Retire` → `TexRegistry.Drain`'s delete). The
  registry's key is **(lower-cased resref, atlas cell)** — not the PLT scheme, which lives per object in
  `SceneDb.ObjPltRow` and is resolved in the shader. "Texture resref + PLT scheme" in section 3 above
  describes the thing CX-117 deletes.
- **The world edge survives in DB mode, and the world scene's destruction is its area trigger.**
  `AlloyRenderer.RevokeAll` → `PassRegistry.Global.Revoke()` is still driven from `AlloyRenderer.EndFrame` on a
  module unload, a context loss and a part-table wipe, and `TileBuild.Revoke`, `ModelCache.AreaEdge` and
  `PartTreeLive.Revoke` hang off it. `TakeAreaEdge` does live in a hook the seam does not install, so
  `DbScene.Destroy` calls `AlloyRenderer.AreaEdge` itself when the dying scene is the world one — before the
  member sweep, so `TileBuild.Revoke` does the tile teardown with its census and its model holds. Tile objects
  are scene members now (`TileBuild.NewObject` → `DbScene.JoinNew`), so the sweep reaches them too.
- **A scene destroy DETACHES its members; it does not destroy them.** This amends the decision, not just the
  build: `Scene::~Scene` @0x472e40 is a loop of `Gob::SetScene(child, NULL)`, so members outlive their scene
  and the game deletes them itself. `DbScene.Destroy` called `ObjectBridge.ObjectDestroy` on each and retired
  shells the game still held — live, that was `AttachToScene`, `GetPosition` and `~Gob` arriving on retired
  facades. `ObjectBridge.ObjectSceneDestroyed` does the detach.

**Facades.** Layout-compatible shells with mirrored words, as decided; `CAurBehavior` objects stay the game's
and CX calls their vtables, as decided. Four amendments:

- **The quarantine IS a vptr swap, as the decision said.** `Facade.Retire` writes the shell's primary vptr —
  and a string's `CAurString` sub-vptr and a camera's `CameraGob` one — to the **poison twin** laid out beside
  each live table (`Facade.Vtables.cs`, same offset-to-top), before the record is released. Every slot is then
  a counted refusal, including the ~20 that answer from the shell's own words and never resolve a handle. The
  header state (`SeamShell.StateRetired`, tested in `Facade.Resolve`) is still there and is now the second
  line, not the only one. What remains a gap is the log latch: `1u << (logical & 31)` over one word, so with
  ~550 logical ids most slots are silenced once by a colliding sibling.
- **The window is 1800 frames, not two.** Two is enough for the renderer to finish with a record, but not for
  a game that deletes an object and keeps the pointer: measured, the client re-attaches such a pointer about
  900 frames later (a panel closed, then reopened). `SeamShell.QuarantineFrames` is 1800, above the measured
  gap, with `RingSize` the hard cap on memory. `SeamShell.Frame` is driven from `SeamService.Frame`, which is
  registered on `FramePhase.PostPresent` — the clock ticks.
- **`Scene+0x168` (ambient) and `Scene+0x1b8` (the external-animate counter) are deliberately not mirrored**,
  because CX owns both the writers and the readers. `Scene+0x160` **is**: the shell owns a real
  `WindManager`, `operator new(0x870)` plus the engine's own default ctor, because that ctor stores pointers
  into itself and `setglobalwind`/`setglobalwindvariation` reach `scene+0x160` with no null check. The fog
  words `Scene+0x140..0x154` are mirrored and seeded with `Scene::Scene`'s defaults, because zero is not "no
  fog yet" and the game overwrites only some of them.
- **`Gob+0x90` (the root `Part*`) is written** (`ObjectFns`, from `PartShell.For` of the object's first part),
  so `AurEnableShadowing`/`AurDisableShadowing` — unhooked on purpose, because they read that word raw and
  tail into the hooked `SetShadows` — reach a part. Only the most-derived destructor retires a shell; a
  second retire is the base half of the chain and is counted, not logged.

**Change-driven build.** The dirty-list scheme is as decided and the folds visit only dirty records. The
visible-set index is `DbGrid`, not `AreaGrid` (`AreaGrid` is the engine-mode class `DbGrid` borrows its
frustum test from), and a static is **not** filed once at creation: the filing is retried every frame until
its world sphere is real, because an object created before the transform step has a zero sphere and a static
filed in that window would be unbounded for the session. `_drawBuf` is the GL SSBO; the persistent per-draw
records the section means are `AlloyRenderer._dData`, and DB mode writes them through `DbStage`/`StageDbPart`.

**Behaviours.** CX-107's own solvers (`BehaviorSolvers`) and `CameraService`'s camera feel are **not
installed** in DB mode. Both chain a behaviour's `Control`, which redirects the *game's* own virtual call into
a port that reaches the engine's `Part::GetWorld` on a facade-interior `CAurPart*` — an immediate crash at
character select, where every GUI control is placed by a `CAurBehaviorCrossSceneAttach`. Unhooked, that
virtual call lands on the seam's `Part` vtable and answers from the database, and `DbAnim.Behavior`'s own
dispatch solves the engine classes over columns. The consequence is named rather than hidden: the ease,
recenter and pitch limits `CameraService` layers on the chase camera have no DB form yet.

**Threading.** The game thread owns the database, every seam call and by-value getter answers there, and the
animation runtime, the behaviour dispatch and the transform step run there — all as decided, and all enforced
by convention: there is no thread check, no assert and no lock over any `SceneDb` column. Three amendments:

- **The render thread does not run, and the reasons are now FIVE named facts — two of them left (CX-188).**
  `RenderThread.Start` refuses while any of `GuiBuffersOwned`, `TextDocsPublished`, `UiOrderSettled`,
  `FrameInputsPublished` or `ScreenHandoffSettled` is unset, and the refusal names *every* unmet one — a
  single flag would let whoever met one silently arm the others' races. CX-188 closed `TextDocsPublished`
  (`TextQuads` publishes a whole list into the slot nothing is holding, and had done so before the ticket
  looked: "nothing sets any of them" was stale) and `UiOrderSettled` (see the fence below), and split the
  framebuffer-0 question out of `GuiBuffersOwned` as `ScreenHandoffSettled`. The two that remain are the two
  the fence cannot cover, because they live in the publish-to-next-fence window where the two threads
  genuinely overlap: `GuiCapture`'s one static bank, written by `NuiFlush` on the game thread at
  `nk_sdl_render` while the renderer appends panels and resets it; and framebuffer 0, which the renderer
  resolves into while the game thread runs `CGuiMan::UpdateAndRender`. Each names its own fix in
  `RenderThread.cs` — a NUI bank ping-ponged off the snapshot frame, and `PresentTarget` turned on for `Live`.
  So `alloy.renderthread=1` still stands down loudly and the single-threaded snapshot path takes every frame.
  The `FrameInputs` *global* race that was once counted here is not the problem: nothing on the game thread
  writes `FrameInputs` in this mode. What `FrameInputsPublished` now holds is the ENGINE reads inside the
  draw, and `RenderThread.cs` carries the audited ledger of what CX-188 hoisted into `FrameHoist` and what is
  left (seven engine writes or engine-list walks, plus about forty small-value reads).
- **The snapshot carries views, not records.** `RenderSnapshot.Frame` holds the per-view camera state and four
  dirty counts; the renderer reads `SceneDb`'s columns live, and the one-frame-in-flight rule is what makes
  that safe (`DbFeed.Records.cs`'s R1-17 block is the authoritative statement). Section 5's list of what the
  snapshot carries describes a copy that does not exist.
- **`RenderSnapshot.Publish` folded before it waited — CX-188 FIXED this**, in the way this bullet asked for.
  `RenderSnapshot.AwaitDrained` is the fence, and `SeamDraw.OnBoundary` calls it *first*, ahead of the fold,
  so with a live render thread the fold for frame N runs with the renderer already parked instead of rewriting
  the draw records while it still drew N-1. The same move closes the `DrawRecipe` table race recorded beside
  it: `Intern`/`Fill`/`Pump` and `DbAnim.Dyn`'s `InternDynamic` run inside the fold, i.e. inside the fence,
  and the 4.6 bracket that reads the table runs outside it, so the two spans are disjoint by construction.

**Dangly solve and skin palettes stayed on the game thread**, against section 5: the dangly solve is inline on
the part walk (as the engine's is) and the skin palette is written at the end of `DbAnim.Finish`, because the
pose, the compose and the palette are ordered. Culling and staging are on the drawing thread and on
`StagePool` workers respectively, as decided.

### CX-120 status (2026-10-02)

Each CX-120 "Done when" bullet against the build, with its evidence. The default is still
`alloy.scenedb=0`: mode 0 is not deleted, so none of the bullets that are about *removing* the engine-scene
path can be met yet.

**Gates on this tip.** `build.sh` 0; `run-demo.sh` 204 self-checks, none failed; `check-shaders.py` 0;
`build-win.sh` 413 `Nm` / 168 `At()` / 10 by-value symbols, 0 bad. Live (`tools/live/run-live.sh`): mode 0 on
the default module PASS at 59% lit; DB default PASS; **DB ladder PASS on every rung with no 0 → 9 focus
jump**; DB in "To Heir is Human" PASS; DB `--menu` with the Exit click test PASS. The mode-0 ladder fails one
rung, combat, and that is the harness rather than the build: `+TestNewModule` does not take the dropped
`.bic`, so it brings its own weak test character and the rung is dice — the harness doc's claim that the
dropped character is used is wrong.

**Final review: 15 findings, 13 fixed.** `TextAtlas.Pump` moved inside Alloy's bracket (its DSA uploads were
running in the engine's 3.3 context); `GuiCapture`'s scene dump stopped allocating a string per scene per
frame in a GL hook, in both modes; `Facade.Shell` tests a vptr for an exact **address point** instead of any
value inside the block's byte range, which had been a wild read in the path whose job is to refuse one;
`TileTexProbe` stops measuring once it has printed; `FrameInputsPublished` became the fourth render-thread
precondition, because it had been claimed done in two places and nothing calls `Publish`; three doc comments
that had been split mid-sentence were rejoined; the live harness signals only its own client through `/proc`
rather than `pkill -x nwmain-linux`, which killed every client on the box; `RenderThread`'s stand-down settle
is latched, so one that finished early no longer leaves the snapshot stopping for the session;
`Part::GetWorld` got a measured `vtbl` row (0x20 Linux, 0x18 Windows) instead of a derived slot;
`Facade.Stale` dedups like `Alien` and `Poison` rather than allocating a line per call; and
`SeamLayout.SceneSafeCount` reads the Scene's own row rather than the Gob's, which happened to be equal.
**One refuted:** the SafePointer array is not freed with the wrong allocator — `realloc` and `free` are both
libc here. **One plausible and not fixed:** hoisting `cx_plt` out of the lit stage. Mode 0's cost is one
`length()` and a coherent branch, so it needs measurement before per-mode shader variants are worth their
maintenance.

**Closed since the review, each with its own commit.** The DB **main menu** now renders and picks like mode
0: the shell quaternion is read `(w,x,y,z)` as `Gob+0xb4` is laid out (the swizzle mirrored every GUI control
about its own origin once anything outside CX wrote that word), the visual-transform record is seeded as
`Gob::Gob` seeds it (`GetAdjustedScale` had answered 0 for every object and every animation ran at rate 0),
the GUI flush brackets and pumps the arena on a frame with no world, a label whose named part does not
resolve falls back to `AttachCornersToObjects` anchored on corner 1 as `CloseLayoutFile` expects, and `DbGui`
draws in the scene's own member order (labels had painted under their plates). The harness gained a `--menu`
mode with a self-grading Exit click, and the engine's ortho pick arm for GUI cameras is implemented. The
**keyhole opens**: its aperture is centred on `CamFollow` and the **rendered** eye (`DbCamera.PosOf`), under
`Camera::RenderScene`'s own camera-state rule. The **+9 focus Z is closed** — it was `ComputeZValue`'s
sentinel standing in for a floor cast that missed, because `HitFeed.Fold` unfiled a recycled slot whose one
dirty entry carried `Destroyed \| Created`; six seam faults were found and fixed on the way to it and the
whole trail is CX-185. And the tile **walkmesh** is loaded from its own `.wok`
(restype 2016), matching vanilla's floor cast byte for byte and no-opping where a tile model embeds its own
`aabb`.

| Done-when | Verdict | Evidence |
|---|---|---|
| No engine `Gob`, `Scene` or `Part` exists in Alloy mode | **MET for DB mode, not for the default** | The seam is all-or-nothing and creates none: `SeamService` stands down unless `NewCAurObject`, `NewCAurString` and `NewCAurScene`/`NewCAurCamera` are all claimed, and `AlloyRenderer.OnRenderSinglePass` logs once and returns if an engine `Scene` ever reaches it. But `alloy.scenedb` defaults to 0, so a stock session is still the engine's graph. |
| The game plays through an area change, combat, a conversation and a GUI session | **MET** | DB mode PASSes **every rung** of the scripted ladder (`tools/live/ladder/`, `--ladder`) — enter, walk, talk, combat, area 2, area 1, done — with no 0 → 9 focus jump, plus the `--menu` Exit click and "To Heir is Human". Kingmaker passes as of `8e23f7a`, having crashed on every run before it. Known gaps that survive the pass are listed in CX-184: the void colour (a composite difference), point-light shadow cubes, and baked lighting. The door helper box (81a6fd4), the `.wok` leak (8fcc25a) and `GlContextService`'s health bracket (16113c0, a `main` bug too) closed after this table was first written; the black planes no longer reproduce. |
| Windows passes the same | **NOT MET** | Cross-build and the ABI checks only (`build-win.sh`, `tools/abi/check-win-symbols.py`); every new row carries both values and the `sret` orders are declared per OS, but **no live Windows run has been made**. |
| **Perf:** the build is change-driven; `Restage`, `Chase`, `Ident`, `Fade` ≈ 0 in a still scene | **PARTIAL** | The database's side is change-driven and measured so: `Build` 0.2–0.4 ms (against 0.8–1.2 ms on 2026-10-01), `Ident` and `Fade` 0.0, `static-restage 0/frame` over 3945 records on the stock area with `records 0/frame written`, pinned off-device by `SettledCheck`. `Restage` is **0.1–0.2 ms, not 0**, because `AlloyRenderer.Batches.cs` re-stages every routed part every frame regardless — the renderer's side of the seam, not the feed's. `Chase` is unjudgeable: `Batches.cs` overwrites `Zone.Chase` with the whole stage phase. Fairness datum: DB draws 379 parts/frame in 42 batches where mode 0 draws 125 in 10, for the same `Build`. The staging double-walk is gone (`ae19544`): deferrals/frame 424.6 → 23.0, worst frame 7.8 → 4.2 ms, hitches 17 → 6. |
| **Perf:** no enrolment and no shim; `Enroll`/`EnWalk`/`EnDrip` and `RspShim` gone, no entry hitch | **NOT MET** | DB mode runs none of them — `DbSeat`/`DbRelease` replace the walk and `RspShim.InWindow` is open by construction — but the code is still in the tree behind `alloy.scenedb=0` and the zones are still declared. The deletion is CX-120's last commit; its inventory is `docs/owned-resource-lifetimes.md` part 2. |
| **Perf:** Alloy reads no engine scene memory per frame; the scene-only `EngineLayout` rows are deleted | **PARTIAL** | Audited: **no per-frame path reads an engine `Scene`, `Gob` or `Part`** — they do not exist. What remains and is deliberate: the game-constructed `CAurBehavior`s ADR 0028 keeps engine-layout (`DbAnim.Behavior`, `DbCamera`), the *game-layer* `CNWCArea` for the interior flag, the fog words and the light palette (re-read every frame, not latched, because the area is allocated before `LoadArea` fills it), and CX's **own facade shells**, which are read through `EngineLayout` rows by design. **37 of those rows are the facade's ABI and cannot be deleted** while the game reads those words by literal offset (`CamViewAngle`, `SceneGobs`, `SceneGobCount`, `Gob{Alpha…WindStrength}` and the rest); the other 36 become unreferenced once mode 0 goes, and 12 are already dead. |
| **Perf:** the chosen threading model is built; a render thread makes a CPU-bound frame cost the max, not the sum | **NOT MET** | The thread is built and refuses to start. `TextDocsPublished` is met now; the refusal names the other three — `GuiBuffersOwned`, `UiOrderSettled`, `FrameInputsPublished` — and nothing sets them. The ceiling it would buy was measured instead, as the Alloy share of a CPU-bound frame: forest 48%, Contest Of Champions 47%, Kingmaker 28%. |
| **Perf:** everything still reaching the engine's GL context is listed | **MET (the list exists; A1 is not met)** | See below. The list is not empty, so the Enter/Leave brackets stay. |

**What still reaches the engine's GL context in a DB frame.** Each is a ticket, not an oversight:

1. **The bracket itself** — `GlContextService` creates the 4.6 core context on the engine's drawable and runs
   two switches a frame with a fence each way. Everything below is why it cannot collapse to one context.
2. **CX's own 2D UI** — `UiHostService.DrawWindows` → `UiHost.DrawFrame` and `TextRig`, drawn in the engine's
   context at the present (in DB mode *after* the world frame it composites over), followed by `HandBack`'s
   `ResetRenderStates` resync. CX-188 ORDERED this rather than moved it: it runs on the game thread inside the
   fence, in both arms of the boundary, and `UiOrderSettled` is now met.
3. **The present** — `ScreenOwner.Present`, and `PresentTarget`'s blit of the composited texture into the real
   default framebuffer, which the engine context must do.
4. **`OwnedBuffers.OnUnlockPool`** — deliberately: it runs wherever the engine was, buffers are share-listed,
   and the upload never touches `ARRAY_BUFFER`/`ELEMENT_ARRAY_BUFFER`.
5. **Engine texture decode and upload**, which `OwnedTextures` adopts at the funnel, and the engine's own
   shader compiles at area load.
6. **Movies** — `MovieService`'s raw GL in the engine's context, handed back through `ScreenOwner`.
7. **Two one-shot probes** — `DdsReader.Formats`' VRAM query, and `GuiPass`' Windows default-depth-format
   probe; plus `LightmapBake.Hold`, which leaves the 4.6 context for the engine's and re-enters around a
   compile notice.

## Considered and rejected

- **Engine-layout objects CX allocates.** Keeping every Alloy reader working by giving CX's objects the
  engine's field offsets. Rejected: it keeps the engine's data model (pointer graphs, per-object virtuals, no
  change tracking), keeps every `EngineLayout` scene row alive, and buys nothing the handle tables don't.
- **One object per class (OOP records).** Rejected for the hot walks above; the facade gives the game its
  object, the tables give the renderer its columns.
- **Render on the game thread, snapshot only.** Simpler, and it was the first step built. Kept as the fallback
  the render thread stands down to (a fault in the render thread's bracket, or `alloy.renderthread 0`), not as
  the decision: on the plaza the frame is CPU-bound at roughly engine + Alloy, and only a second thread turns
  that sum into a max.
