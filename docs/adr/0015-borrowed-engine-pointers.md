# 0015 — Borrowed engine pointers: check the object, don't trust the field

Status: accepted (2026-08-26). Supersedes the rule #33 left behind ("never cache a `CAurTexture*`,
re-fetch every frame"), which is necessary and not sufficient. Closes #138, which held this material
as an open ticket.

## Context

Every renderer crash this project has had is the same crash. A core dump from 2026-08-22 (SIGSEGV
after drinking a potion, Alloy pipeline armed, #128 + #137 live) is the worked example:

- The faulting call was `AurTextureGetTID` @0x48e080, reached from `OwnedDrawService.BuildBatches` ->
  `Scene::RenderSinglePass`.
- Its argument (0x55ae259ffd50) had a **heap pointer where its vtable belongs** (0x55ae2000d7a8),
  against 2237 live `CAurTexture` and 309 `CAurTextureBasic` objects in the same image all carrying
  the correct vtable value. Freed and reused.
- `AurTextureGetTID` opens by dereferencing that word and calling through slot 0x18, so the jump
  landed on 0xff000000.

The fact that generalises: **`Material::GetTexture` @0x4c5810 is a plain field read** — `this +
slot*0x10`, or the SharedMaterial's table at `this+0xd0`. It returns whatever pointer the material
was left holding, live or dead. So re-fetching every frame does not help: the staleness is in the
engine's field, not in our cache. *A per-frame re-fetch of a stale field is a stale field.*

Underneath it is the structural constraint, and it is not going away: **the Alloy pass reads engine
objects at points in the frame the engine never does** — bucket 0/1, for parts the engine has not
reached yet, and after object lifetimes the engine assumes nobody is observing (a VFX gob lives
seconds). The engine does not null a freed texture out of the materials that point at it; it relies
on nobody reading a material outside its own draw. We do exactly that. So dangling reads are the
normal condition of an Alloy renderer, not a bug to be fixed once.

## Decision

**Any engine pointer the extender holds or re-reads across a frame boundary must be validated at the
point of use, by one of two mechanisms — never by trusting the field it came from.**

1. **Vtable check** (for objects that have one). The engine has exactly two texture vtables, so "is
   this still a texture" is two compares against `_ZTV11CAurTexture` / `_ZTV16CAurTextureBasic`
   (+0x10, Itanium ABI). `Sowx.Framework.AurTex` is the one front door to `AurTextureGetTID`; every
   material-derived call site routes through it (Alloy colour diffuse + five PBR slots, Alloy sky,
   `CasterPass` masked casters, `GrassField`, the `BatchService` census, chat portraits).
   A refusal costs that part its Alloy draw for the frame — the engine draws it, i.e. vanilla — and
   is counted, with the session total on the `[owned]` line.
2. **Death notice** (for objects that have no vtable to check, or where a check would be too late).
   Hook the destructor and revoke the pointer from our table: `Part::~Part` (`_ZN4PartD2Ev`
   @0x437720) for the batch part table (#145) and the sky part table (#150). Where the death notice
   cannot be installed, the feature **stands down** rather than infer liveness — `BatchService`
   already does this if `Part::~Part` fails to hook.

Corollaries that are part of the decision:

- **A vtable guard covers only the object it checks, not the pointer that produced it** (#150). A
  live-looking `Material` can hand out a dead texture; a dead `Material` has no vtable to test at
  all. Guarding the texture does not guard the material.
- **Every owned pointer table needs a death notice**, and a table routed *away* from the general
  part table (the sky's) does not inherit the general table's one.
- **The counter is the diagnostic.** `AurTex.Refused` climbing steadily on live content means a
  persistent stale holder one level up (a dead material) — a different bug from a spike at a VFX or
  area-transition teardown, which is the expected shape.
- **Guards carry an off-device self-check.** `AurTex.SelfCheck` exercises the guard both directions
  against synthetic vtables, so a silent inversion fails on a machine with no client instead of on
  someone's session. Mutation-tested: forcing the guard to admit everything fails the check.

## Consequences

- The first suspect for any renderer SIGSEGV is a borrowed engine pointer read out of the engine's
  own frame order, not GL. Pull the faulting object's first word out of the core and compare it
  against the live population of that class in the same image — that is the whole diagnosis, and it
  has been the answer every time so far.
- New owned surfaces (anything reading `Material`, `Part`, `CAurTexture`, or a table keyed by an
  engine pointer) inherit both obligations up front: route the read through a guard, and take the
  death notice for the table. Neither is optional, and "we re-fetch every frame" is not a substitute
  for either.
- Guarding is cheap enough to be unconditional: two loads and two compares on the hot path.
- `ponytail:` the vtable whitelist is two values, not a mapped-range test. A range test would admit a
  freed object reused as some *other* engine class — a wrong-vtable call rather than a wild one:
  quieter, and no cheaper to check.

## Forensics recipe

Reproduce: drink a potion in a live session with the Alloy pass armed (short-lived VFX gob = the
shortest lifetime the Alloy pass ever holds a pointer into). Then, from the systemd core
(`coredumpctl`), read the faulting argument's first word and scan the image for the live population
of that class's vtable. See also the `addr2line` recipe for NativeAOT frames in #145/#150.

Open questions #138 left unanswered, kept here because they are the next step if the counter ever
climbs steadily: which object holds the dangling pointer (the `Material` at `this + slot*0x10` or a
SharedMaterial's `this+0xd0` table, `+0xd8` for slot 0 — the core's holder sat at 0x55ae27cdded8
inside a 0xe0-stride array whose records begin with a heap pointer, not conclusively identified), and
whether a *dead material* is reachable under a live-looking part.

## Related

#33 (the original `CAurTexture` lifetime crash, chat portraits), #128 (dynamic gob ownership),
#137 (skinmeshes), #138 (this ADR's source), #145 / #150 (the death notice), #141 (a crash that was
*not* ours — engine-side `CExoFileInternal` dtor — the counterexample).
