# 0014 — Module layout: GL is not UI, and the framework names no service

Status: accepted (2026-08-24 grilling). Amends ADR 0004 rather than replacing it.

## Context

The folder tree is a fossil of the order features were built in, not of what the code now is. Chat
came first, so `Ui/` became the home for anything drawn; the renderer arrived later as
`Services/Render`, its shadow half as a top-level peer `Services/Shadow`, and the ADR 0006 batcher as
`Services/Perf`. None of those three seams matches the code today.

Measured on 2026-08-24:

- **`Ui/Gl.cs` is the renderer's OpenGL entry-point table.** Of ~2,000 `Gl.*` call sites, **79% are
  in `Render`/`Shadow`/`Perf`** and 20% in UI. Fourteen renderer files carry `using Sowx.Ui` for no
  reason other than to reach it, while its own siblings `GlProgram.cs` and `GlContextService.cs` sit
  in `Services/Render` — the GL trio split across two folders.
- **Strip `Gl` out and the real Render→Ui edge is two symbols**: `UiHost.InModule` and
  `UiHost.InGame`. Both are game-session predicates, not UI, and they arm the entire 3D pipeline.
- **`UiHostService.OnSwap` hardcodes nine calls into five concrete service types** (lines 31, 64,
  67-69, 77-81). ADR 0004 rejected exactly this shape in its own Considered Options — it declined to
  let the framework delegate capture to `HotkeyInputService` because that "inverts the layering
  (framework depending on a service)" — then fixed the roll-call for *windows* while leaving a second
  hardcoded roll-call for *frame lifecycle* immediately beside it.
- **`Shadow/` is the weakest boundary in the tree.** `CasterPass` is a pass like `WaterPass`;
  `ShadowInputs` and `ShadowFeedService` feed the colour pass as much as the shadow one;
  `ShadowMath` is generic view/projection maths.

## Decision

**Layer by what the code does, and let the lower layer name nothing above it.**

    Gl/            Gl, GlProgram, GlContext, GpuQuery, RingBuffer, RenderTarget
    Render/Core/   FrameGraph, OwnedClass, ShaderFeature, ShaderContract, FrameInputs
    Render/Scene/  ownership census, enrollment, the light + caster walk
    Render/Pass/   Opaque, Blended, Water, Sky, Beams, Particles, Grass,
                   Shadow(Sun/Cube), Probe, Post, FbCopy, SceneDepth
    Ui/            TextRig, GlyphAtlas, UiHost, Window, VertexBatch, SettingsPanel
    Framework/     GameGate (InModule/InGame), Cfg, HookService, SymbolMap
    Perf/          DrawStats, FpsOverlay, StaticBatcher

Two rules carry the weight:

- **`Sowx.Ui` and `Sowx.Render` are peers.** Both depend on `Sowx.Gl` and `Sowx.Framework`; neither
  depends on the other. `Gl` moves out of `Ui`; the session gates move to `Framework`.
- **The framework names no service.** `UiHostService`'s frame roll-call becomes a participant
  registry that services join at `Initialize`, mirroring the window registry beside it. The per-frame
  zero-alloc invariant from ADR 0004 holds: lists are built at init and walked by index.

`Shadow/` folds into `Render/Pass/`; the ownership census and bucket gate leave `Perf/`, which keeps
only what is actually performance instrumentation.

## Windows

Windows support of the Alloy pipeline stays deferred (ADR 0009/0010), but the port is a commitment,
not a maybe, so **nothing in this layout may foreclose it.** Two constraints, both of which this
decision happens to serve:

- **`Sowx.Gl` is the platform seam.** GL entry points are resolved through GLX today and must be
  resolvable through WGL later. Isolating the loader in its own module is what makes that a change in
  one place rather than fourteen. No `Render` or `Ui` module may resolve a GL symbol itself.
- **`SymbolMap` stays one platform surface.** It carries both the Linux mangled names and the Windows
  RVA table, and the RVA path is build-locked against a PE TimeDateStamp. Splitting it per subsystem
  would scatter that pairing; it is explicitly not split. Trimming its redundant
  `= default` entries is safe and separate — `SymbolMap.Resolve`'s first Windows test proves a missing key
  and an explicit default entry take the same branch. Done in #159, which also moved the ~25 struct field
  offsets that were never symbols out to the code that dereferences them (`SceneLayout` for the shared ones,
  private consts for the single-reader ones).

## Consequences

- ADR 0004's framework/service layering rule is upheld rather than revised; only its frame-lifecycle
  half was left unfinished, and this completes it. 0004 gains an amendment note pointing here.
- Renames must follow the module splits, not precede them, so a file is not moved twice. The `Gl`
  move must land before the renaming pass for the same reason.
- Several types are renamed to what they do: `WaterPass` (which does not draw water — it owns the
  frame's one framebuffer snapshot) to `FrameSnapshot`; `ShadowInputs` to `FrameInputs`;
  `ShadowFeedService` (which renders nothing) to `SceneInputs`; `Perf/BatchService` splits into an
  ownership census, an engine bucket gate, and the batcher that kept the name.
- The `Ui/` docs that name Chat (`Metrics`, `VertexBatch`, `TextRenderer`) are stale by two refactors
  and get rewritten as their files move.

## Implementation note (#169, 2026-08-26)

The GL module ships as `Graphics/` / `namespace Sowx.Graphics`, not `Gl/` / `Sowx.Gl`. The sketched name
does not compile: from any namespace nested under `Sowx`, the bare identifier `Gl` binds to the *namespace*
`Sowx.Gl` before any type or `using`, so every one of the ~2,000 `Gl.*` call sites fails with CS0234.
Enclosing-namespace members also outrank alias directives, so a `global using Gl = Sowx.Gl.Gl;` does not
rescue it. Both were verified against the compiler. Renaming the class instead would have meant rewriting
every call site, which this decision explicitly defers to the later renaming pass.

The session gates land as `Framework/GameGate`, which also took `Locked` / `OwnsFramePresent` off
`UiHostService` — the `SOWX_LOCKCHAT` pivot is read by render, shadow and perf code and was the last
`UiHostService` reference outside `Ui/`.

Two `using Sowx.Ui` imports survive outside `Ui/`, both named in place: `Perf/FpsOverlayService` is a genuine
UI client (it draws through the text rig and registers a window), and `Shadow/ShadowFeedService` registers a
zero-size `Window` purely to get a per-frame draw slot — #170's participant registry is what removes that one.
`ShaderResource` stays in `Render/`: `GlProgram` reaches it for override lookup and `$TOKEN$` expansion, and
both are content/render concerns rather than platform ones, so the GLX/WGL seam is unaffected.

## Implementation note (#175/#176, 2026-08-26)

The layout landed with `Render/` carrying the three internal folders and ONE namespace (`Sowx.Services.Render`)
rather than one namespace per folder: the folders are navigation, the namespace is the layer, and splitting the
namespace would have meant a `using` churn across every file for no layering gain. `Shadow/` is gone --
`CasterPass`, `CasterCandidate` and `ShadowService` to `Render/Pass/`, `ShadowMath` and `FrameInputs` (was
`ShadowInputs`) to `Render/Core/`, `SceneInputs` (was `ShadowFeedService`) and `CameraCapture` (was
`ShadowCaptureService`) to `Render/Scene/`. `WaterPass` is `Render/Pass/FrameSnapshot`. `Ui/TextRenderer` is
`Ui/TextRig` -- it is a GL rig, and the name said otherwise -- and `PortraitQuad`, a pure chat concept, left the
shared layer for `Chat/Core/Model`. `Metrics`' documentation, which named the chatbox and the wrong owner, is
rewritten.

Two departures from the sketch above, both deliberate:

- **`Perf/BatchService` moved but did not split.** It is out of `Perf/` (which now holds only `DrawStats` and
  `FpsOverlay`, i.e. instrumentation) and into `Render/Scene/`, and its own doc comment now says the name
  describes a minority of what it does. The three-way split into census / bucket gate / batcher is a
  #173-sized job on ~2,200 lines of shared static tables and is filed as #188 rather than smuggled into a
  build-only rename pass.
- **`CasterPass` is not on the pass lifecycle registry yet.** #175's `PassLifecycle` slots are shaped for the
  colour bracket: `CasterPass` runs in the shadow bracket, installs through its own service, and stands down
  by a latched `Disarm` rather than a context edge. Folding it in is follow-up work, not a folder move.

`FrameRegistry` (#170) and `PassRegistry` (#175) are the same participant-registry rule applied to the frame
and to the passes; both use delegates rather than an interface, because the participants are static classes
whose entry points are reached from `[UnmanagedCallersOnly]` handlers that cannot capture a receiver.

## Implementation note (#188, 2026-08-26)

`BatchService` split four ways, not three: `PartTable`, `OwnershipCensus`, `BucketGate` and the batcher that
kept the name. The fourth is the one this ADR sketched as shared state and #173 already met once -- the part
table and its parallel columns stay ONE struct-of-arrays module, because the hot walks read the columns
directly and a real boundary there costs a copy or an indirection per part per frame. The three behaviour
modules are separate `internal static` classes rather than partials of one, since partials would all still be
named `BatchService`; they reach the columns through `using static`, which is a compile-time alias and emits
the same instruction the same-class read did.

Two boundaries are drawn where the ticket's bullets did not:

- **The capture funnel stayed with the batcher.** `PartTriMesh::Draw` / `PartSkin::Draw` and the `Record` /
  `Complete` bodies hanging off them WRITE the table, but they are the hook body -- they run a vanilla draw
  through a trampoline `BatchService` owns to make the `RenderVertexData` funnel fire. Splitting them from
  their trampoline buys nothing and risks the ordering. `PartTable` owns identity, lifetime and the group
  registry; `BatchService` owns what fills them.
- **`TableGen` is on `PartTable`, not the census.** It is bumped by the wipe, which is table machinery, and
  it is what tells every holder of an entry index that its indices died.

The AC "the bucket gate does not depend on the batcher being armed" was a real coupling, not a naming one:
`EndFrame` cleared the single `_active` flag when `glMultiDrawElementsBaseVertex` turned out to be missing, so
a GL without multidraw took the capture tables, the census, the gate AND the Alloy pass down with the submits.
Arming is now two flags: `PartTable.Active` (the capture hooks are in -- the census and the gate legitimately
ride on it, since no table means nothing to classify or suppress) and `BatchService._batchArmed`, which is
private to the batcher and which nothing else can read. That is the one deliberate behaviour change in the
split; everything else is a move.

## Amendment (#206, 2026-08-27): `Alloy/Scene/` calls no GL, and the build says so

ADR 0016 declined to build an RHI layer for M4. What stands in for one is this: the RE knowledge in
`Alloy/Scene/` -- which engine field holds a fade factor, how a VDO is laid out, where a part's material
sits -- is the part of Alloy a future backend would not rewrite, and keeping it free of GL calls is the
whole of what preserves it. So it is a rule the build enforces rather than one a reader remembers:
`build.sh` greps `managed/Alloy/Scene/` for `Gl.` and refuses to build on a hit, unless the line carries a
`gl-ok: <why>` marker.

The audit found 34 calls across three of nine files, and none of them were extraction:

- `BatchService.cs` (5) is draw submission by its own summary -- misfiled, moved to `Alloy/`.
- `AlloyCensus.cs` (19) were all in two texture readbacks the triage prints. They are pure GL queries
  belonging to no pass, so they became `Alloy/TexProbe.cs`.
- `AlloyRenderer.Enroll.cs` (10) were `Harvest` (query an engine VDO's attribute formats out of the live
  context) and `EnsureVao` (build the 4.6-side VAO over the shared buffers). Both are submission-side work
  the ownership walk merely calls; they became `Alloy/AlloyRenderer.Vao.cs`.

34 before, 0 after, and no written-reason survivors -- so the boundary was real, and what violated it was
three files sitting on the wrong side of it rather than extraction reaching through.
