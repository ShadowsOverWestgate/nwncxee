# CONTEXT

Glossary for the client extender. Domain vocabulary only — no implementation
detail, no decisions (those live in the Gitea tracker: wayfinder map #13 and its
tickets). Terms crystallised while charting the native chatbox reimplementation.

## Product

- **NWNCX:EE** — the product's full name; **CX** is the shorthand (#530). Every user- and
  operator-visible surface says one of the two: `cx.cfg`, `cx.log`, `cx-launch`, `cx_managed.so`,
  the `cx_*.shd` stages, the `cx-<os>-x64` release zips, the `CX.*` C# namespaces. The
  `CX*`/`CX_*` identifiers in C# and GLSL are internal glue — never shown, never renamed.
  **cx** survives only as that glue and as the pre-rename config stem the read-fallback honours.

## Chatbox

- **ChatLine** — one retained chat message: `channel`, `senderName`, `playerId`,
  `portraitResref`, `colorRuns`, `timestamp`, `rawText`. **Immutable after ingest**:
  a line never mutates once received, so toggling a data-bearing feature (e.g.
  timestamps) affects new lines only, never old ones.
- **Ingest** — the moment a `ChatLine` is received and frozen (the inbound engine
  tap). *Cold* path (main-thread network), distinct from *draw* (the hot frame path).
- **ChatStore** — the retained model: a fixed-capacity **ring** of `ChatLine`
  (oldest overwritten) plus filtered/paged views. The single source every feature reads.
- **ChatWindow** — a movable/resizable chat window: a `rect`, an ordered **list** of
  tabs, an active tab, and a **lock**. Several can exist at once (a small cap); every
  draw/input path takes a window, so the set is a list + a loop.
- **Main window** — the first window (index 0). Undeletable, always has ≥1 tab, and is
  the fallback keyboard owner when no other window is focused.
- **Focused window** — the one window that owns the keyboard and shows the input strip
  + send dropdown. Clicking a window focuses it; the chat-open key focuses the
  last-focused window (the **Main window** if none).
- **Tab** — a *receive* view inside a window: a `label`, a channel-mask **filter**, an
  optional **player pin**, and a scroll offset. A window always has ≥1 tab; tabs can be
  added, removed, reordered, and dragged between windows.
- **Channel** — a chat stream. **Sendable** (Talk, Shout, Whisper, Tell, Party, DM)
  or **receive-only** (System, Combat, Server).
- **Player pin** — a tab bound to one player by **name** (stable across relog), never by
  `playerId` (connection-scoped, changes each session). Sending resolves the live
  `playerId` from the name at send time.
- **Tell tab** — a Tab that is player-pinned and shows the Tell channel: your private
  thread with that player. Sending on Tell from it auto-targets the pinned player.
- **Send channel** — the channel the focused window's input targets, picked from a
  dropdown **constrained to the active tab's visible channels** (you cannot send on a
  channel the active tab hides). Active-tab-scoped, superseding the earlier per-window
  dropdown (#25).
- **Tells-here window** — the window flagged to receive auto-opened Tell tabs (the Tell
  auto-open setting is Off / new-tab / new-window, default Off); defaults to the focused
  window when none is flagged.
- **Chrome** — the appearance shared by *every* window: font size, colours, animation,
  timestamp style. Global, not per-window.
- **Clone** — copying one window's look-and-behaviour (size, render toggles, input side,
  lock) onto a chosen **target** window. Never copies tabs, position, or **Chrome**.
- **Lock** — a per-window flag that disables moving, resizing, and tab drag-drop for
  that window.
- **Color run** — a span of text with one color, parsed once at ingest from the
  engine's inline `<c…>`…`</c>` markup.
- **Render component** — a stateless unit that draws one aspect of a line
  (timestamp, portrait, sender name, message body). The ordered set of them is the
  **render pipeline**; each is individually toggleable.
- **Ingest observer** — a subscriber notified when a line is stored (audio cue,
  whisper flash, auto-open a whisper tab). Reads the line, never mutates it.
- **Typing state** (`IsTyping`) — whether the chatbox owns the keyboard. While true,
  gameplay hotkeys are suppressed and keys route to the edit control.
- **Suppression set** — the engine hooks skipped so the extender's chatbox replaces
  the vanilla one; reversible via the `lockchat=1` opt-out in `cx.cfg`.

## Framework

- **Core vs shell** — *Core* is pure C# logic with no native references (testable
  off-device); the *native shell* is the hooks, GL draw, and SDL calls. Features land
  in the Core behind the shell.
- **Extender-optional** — a stock client plays vanilla; the extender only ever reuses
  existing wire opcodes, so an un-extended client loses nothing.

## Platform port

- **Resolution** — how a Linux symbol lands on Windows: by *Name* (an MSVC export), by *RVA*
  (a build-locked offset from the module base), or *Skip* (no Windows anchor). Avoid: mapping.
- **Build lock** — the one PE stamp the RVAs were read from. A different stamp turns every RVA
  resolution into Skip. Avoid: version pin.
- **Stand down** — a feature whose required symbols do not all resolve runs vanilla, logged with
  the cause (Skip by design, or Skip by rot). Never half-live: a feature stands down as a unit,
  the same word the post stack uses. Avoid: degrade, fall back.
- **Anchor** — the compiler-invariant evidence (a unique string, an immediate constant, an exported
  neighbour) that ties a Windows RVA to its Linux symbol.
- **Engine layout** — where a *field* sits inside an engine class, as opposed to where a *function*
  sits in the image (that is Resolution, above). One table, `EngineLayout`, keyed (os, build).
  Avoid: offsets, struct map.
- **Build key** — the linker's identity for a client build: the ELF `.note.gnu.build-id`, the PE
  CodeView PDB GUID. Not a content hash, so a rewrapped Steam/GOG copy of one build is one key.
  Distinct from the older **Build lock**, which is the PE TimeDateStamp guarding SymbolMap's RVAs.
- **Diverges / Same / Unresolved** — the three, and only three, resolutions an offset can carry.
  *Same* means measured on both and equal; *Unresolved* means nobody looked on Windows yet, and is
  counted on both platforms (only the gate is Windows-only). The pair exists so "verified identical" and "never looked" stop being one bare `const`.
- **Nm / At / LinuxOnly / Hole** — the four, and only four, Windows resolutions a *symbol* can
  carry, the same argument one step up from offsets. *Nm* is a name export, *At* a build-locked RVA,
  *LinuxOnly* a decision with its reason, *Hole* an absence with its reason. Only *Hole* counts, and
  a constant with no row at all is what `build.sh` refuses — before this, a missing key was both
  "never looked" and "decided".
- **Candidate** — an extractor's ranked guess at where a field sits on the other platform. It names
  the function to open next; it is never a resolution on its own. A candidate becomes a row only by
  clearing the bar in `docs/engine-layout.md`. Avoid: match, hit, result.
- **Probe** — an assertion a resolved offset makes about live memory (the dword is 0 or 1; the
  vtable slot holds a code address), checked once at the first instance so a wrong offset stands
  the feature down instead of rendering garbage. Avoid: validation, sanity check.

## UI framework

- **UI framework** (`CX.Ui`) — the shared layer every windowed feature builds on. Owns
  the frame present, the render toolkit, input/focus arbitration, and the cursor. A
  feature supplies **content**; the framework supplies everything a window needs but
  shouldn't reinvent. Formerly all of this lived under the **Chatbox**; Chat is now just
  one consumer among several.
- **Frame host** — the single owner of the frame present (`SwapBuffers`), the in-game
  gate, and UI-scale sync. Drives every registered **Window** once per frame. (Was
  Chat's `OnSwap` roll-call.)
- **Window** (framework) — a retained UI window the framework owns: rect, z-order,
  focus, drag/resize, hit-regions, cursor, and input dispatch. A service fills only its
  **content**; the shared behaviour is done for it. Chat's tabs/tells and Dialog's panel
  are *content* inside such a window, not windows of their own making.
- **Chrome vs content** — *chrome* is the shared, framework-owned window behaviour
  (frame, drag/resize, focus, cursor, input routing); *content* is the service-specific
  interior (chat lines, dialog rows). The split between them is the framework's core seam.
  (Distinct from the Chatbox **Chrome** term, which is chat's shared per-window *look*.)
- **Selectable text** — an opt-in capability of the framework's text layer: any content
  can mark its text select-and-copyable. Lifted from the Dialog service.
- **Cursor (as UI concern)** — the pointer shape is a byproduct of the per-frame
  hit-test: whichever Window hit-region is under the pointer names the cursor. No
  separate cursor subsystem. Absorbs the former `CursorService`.
- **Golden regression gate** — the Chat service is the reference the migration must not
  regress: the most-polished consumer, carrying live-verified fixes (AurTexture lifetime,
  drive-latch, keybind gating). Its look, feel, and fixes survive the extraction
  byte-for-byte; other consumers may shift to adopt the unified behaviour.
- **Focus** — the one **Window** that owns the keyboard at a time, across every service.
  Acquired per the **focus mode**: *click-to-focus* (default) or *hover-to-focus* (opt-in,
  user-configured).
- **Input precedence** — the fixed order the **frame host** resolves every input event:
  (1) the focused Window's content, (2) registered **input consumers** (chords),
  (3) engine gameplay keybinds. Each layer consumes the event or passes it down. A global
  hotkey fires only when no window wants the key.
- **Input consumer** — a service that registers to receive input at a defined priority
  *without* owning a Window (e.g. the Hotkeys service's chords). A Window, by contrast,
  receives input by being focused or hit-tested.
- **Keyboard-owned gate** — the single framework signal that some UI owns the keyboard;
  while set, engine gameplay keybinds are suppressed. The drive-latch and keybind-dispatch
  fixes live here once, for all consumers.
- **Capability flags** — per-Window switches (`Focusable`, `Draggable`, `Resizable`,
  default on) that let one Window type cover docked bars and read-only panels without a
  second abstraction. A non-focusable Window is skipped by focus hit-testing but still
  drawn and still drives the cursor.

## Settings

- **Knob** — one user-adjustable value the extender exposes, declared once by the feature that owns
  it: what it is called, what kind of control edits it, its range or choices, its default, its
  **tier**, its **effect**, and its **placement**. A knob that is not declared does not exist —
  nothing can list, edit, or persist it. Avoid: option, setting, tunable (when the declaration is meant).
- **Settings panel** — the one window every knob is edited in. Three columns: **category**,
  **submenu**, the submenu's knobs. Reached two ways only: the `/settings` chat command in game,
  and the **gear** at the main menu. Every feature's former settings icon and per-feature panel is
  gone; the panel is the single place.
- **Knob group** — the unit a feature declares its knobs in. One identity wears five hats: the
  group is the **submenu** the knobs appear in, the chat command that edits them, the reset unit,
  and the first segment of every knob's key (`grass.density`, `chase.fov`, `chat.font` — keys are
  two levels, group then knob). A group may carry a **feature master toggle** and may name a
  parent group whose master greys it out. Avoid: feature (when the declaration unit is meant), pass.
- **Category / submenu** — the two-level tree the panel navigates: a category (UI, Graphics,
  Camera, General, Hotkeys) holds submenus (UI → Chat, Camera → Chase); a submenu is one knob
  group, its knobs under optional section headers.
- **Boot value** — what a knob was when the client launched, as distinct from its current value.
  A **restart**-effect feature reads its boot value; the panel edits the current one, and the two
  differing is what "restart to apply" means.
- **Tier** — how visible a knob is: **User** (shown, with help text), **Advanced** (shown under a
  collapsed *Advanced* section of its submenu; expert dials), **Chat-only** (diagnostics and
  bisect switches: reachable by chat command, listed by `/knobs`, never a panel row). Absence from
  the panel is a declared tier, never an omission.
- **Effect** — when a change takes hold: **live** (immediately, the default and the goal) or
  **restart** (written now, read at the next launch — boot decisions and engine-path replacements).
  The row says which. Live wins wherever it can without costing the frame.
- **Feature master toggle** — the first row of a feature's submenu: "Enable <feature>". Off means
  the feature stands aside and the client behaves vanilla for that feature; the rows below grey out.
  A feature's effect (live / restart) is its master toggle's effect.
- **Layout-affecting knob** — a knob whose change re-lays-out the UI that edits it (font size, UI
  scale). Always a stepped choice (a **dropdown**), never a slider: a slider that moves the
  slider under the pointer defeats the user's intent.
- **Command gate** — the one config knob that makes the extender's chat commands live at all.
  Off by default: a server may use the same command names for its own ends, so commands fall
  through to the server unless the user opts in. `/settings` is the sole exemption.
- **Settings file** — `cx.cfg`, the one file every knob persists to, dotted keys sorted
  alphabetically, written by the panel and hand-editable. The per-feature `cx-<feature>.cfg` files
  are retired into it. The hotkey consent store is state, not settings, and stays its own file.
- **Dropdown** — the enumerated-choice control: click to open a list, pick one. Replaces the
  cycle button, which hid the choices and needed N clicks to reach one.
- **Tooltip** — the help text behind a faint "(?)" beside a row's label, shown on hover. Every
  User-tier knob has one.
- **Off-default marker** — the visible sign on a row that its value differs from the declaration's
  default; the submenu's **Reset to defaults** clears every one of them at once.

## Rendering (Alloy)

- **Builder intent** — the part of an area's authored lighting that Alloy must preserve regardless
  of how it shades: *a light reaches what the builder made it reach, and what the builder left dark
  stays dark*. Range and the lit/unlit boundary are a content contract, because builders use them
  for gameplay — an area left unlit is meant to be unlit, and a decor light is not meant to extend a
  sightline. How a lit surface *responds* — colour, specular, roughness, bloom, tessellation, shadow
  softness — is Alloy's and is free to differ from the engine's. Distinct from visual parity with
  the engine's renderer, which is not a goal anywhere.
- **Parasitic input** — Alloy state produced by observing the engine render rather than by reading
  what the engine simulates: a part discovered because the engine tried to draw it, a camera
  harvested off a transform call site, a pose produced by re-entering a draw function. The
  contrast is a **direct read** — the same fact taken from the engine's own state at a moment Alloy
  chooses. Retiring parasitic input is what M5 means by *build it properly*; it is a statement about
  Alloy's inputs, never about the image.
- **Scene enumeration** — Alloy's own walk of the world's objects, covering everything in the area
  rather than the visible slice, producing the persistent records every pass reads. Contrast the
  **draw traversal**, the engine's per-frame walk of its draw buckets, which Alloy inherited
  discovery and visibility from through M4.
- **Owned** vs **staged** — a scene part is *owned* when Alloy draws it (engine
  submission suppressed for it) and *staged* when the engine still draws it during the
  migration. Every part is drawn every frame by exactly one owner, never both, never neither.
- **Cache classification** — a caster is cached or re-baked based on whether its
  transform/pose changed since its last bake, not on a static/dynamic *kind*. A tile is
  simply a caster that never moves; a creature is one that changes every frame.
- **Embedded stage** — the GLSL for an Alloy pass, compiled into the extender as a C# `const string`.
  This is the shader that ships: it travels with the renderer it agrees with, so the per-draw record,
  the uniform blocks and the clip expression can never disagree with the CPU side. Every overridable
  stage has one.
- **Shader override** — a `cx_*.shd` a *user* places where the engine's resman will serve it
  (`development/`, `override/`, a hak), replacing that stage's **embedded stage** at load. A
  first-class customization surface — CX ships defaults and lets them be replaced — and explicitly
  **not** a delivery channel: no content repo ever ships one, because a shipped copy is a second copy
  of a contract that lives in C#. Refused by the stage guards when it breaks that contract; a refused
  or uncompilable override falls back to the embedded stage with a log line.
- **Shader dump** — the current **embedded stages** written out as editable `.shd` files, one per
  overridable resref, into the client's user directory. The starting point for a **shader override**,
  and current by construction because it dumps what is compiled in. Distinct from vanilla shaders,
  which CX never modifies.
- **Tuning knob** — see **Knob** under *Settings*. (Formerly: a value in exactly one of three
  kinds — chat command, config setting, deleted. Superseded by the unified settings model, where a
  knob is one declaration and the chat command and the settings panel are two views of it.)
- **Render mode** — which renderer draws the 3D scene: the *owned* mode (Alloy) or the
  *vanilla* mode (the engine's own path). Both ship permanently; a device that cannot run the
  Alloy mode still runs every other extender feature. The mode is whole-scene — never a
  per-class mixture — and it is **chosen once per session** (`vanilla=1` in `cx.cfg`,
  restart required), because Alloy's residency is exclusive and the two resource worlds
  cannot both be warm. `/vanilla` reports which one is running and why.
- **Permanent engine resident** — a responsibility the engine keeps for good, not one staged
  back to it during a migration: the 2D UI, resource loading, simulation state, and
  animation/bone state. Rendering is not on the list. The rule that draws the line is *the
  engine simulates, we draw* — a class whose motion the engine computes can still have its
  geometry submitted by Alloy.
- **Class-granular ownership** — the unit at which a content class may be *owned* or *staged*.
  Opaque classes may stage part-by-part; classes whose draw order is load-bearing (transparent,
  additive, fade, water) are all-owned-or-none, because a half-owned class has no coherent
  order. The one-owner rule holds at whichever granularity a class uses: drawn every frame by
  exactly one owner, never both, never neither.
- **Material synthesis** — deriving material parameters for content that declares none, rather
  than requiring every model to be re-authored. Content that *does* declare parameters is
  always honoured over synthesised ones.
- **Shader generation** — which vintage of the engine's stock shader set a `.shd` belongs to.
  Two exist and they are not interchangeable: an older set where the transform and fog live
  inline, and a newer split-include set where they are separate headers. A shader claim is
  meaningless without naming the generation it was read from, and the generation the running
  client loads is the one that counts.
- **Pass slot** — the fixed, unconditional point in the engine's frame where extender passes that
  must run *before* anything is drawn fire: after `Scene::AddPartsToDrawBuckets`, buckets complete,
  nothing drawn. Chosen because it executes every frame in every zone, unlike the engine's own
  shadow cadence. The light walk and the world-caster shadow bakes live here.
- **Frame anchor** — the point the Alloy frame's draw work fires from. Distinct from the **pass
  slot**: the anchor is where we *draw*, the pass slot is where we *prepare*. An anchor must fire
  unconditionally once per scene render; whether it also supplies usable engine frame state
  (viewport, framebuffer, matrices) is a separate property, and the one the Alloy pipeline sheds as
  it matures. Since #132 it is `Scene::FadeDoors`, after-original — the last engine call inside
  `Scene::RenderSinglePass` before the first bucket walk, and after the clear the engine's own frame
  setup does. It cannot be RSP *entry*: the world FBO is bound and cleared inside RSP, so a resolve
  written at entry is wiped.
- **Cutout vs blended** — the two classes every non-opaque part is sorted into. *Cutout* is
  alpha-discard geometry: it draws with the opaque set, writes depth, and needs no sort. *Blended*
  is genuine alpha: it draws after, sorted back-to-front, and never writes depth. The split is what
  makes a missing sort harmless for the majority of content and makes alpha-blended geometry
  incapable of occluding what it does not draw.
- **Resolve stage** — the single fullscreen pass that encodes the linear Alloy frame to display
  space at the end of the frame. Distinct from a *post stack*: the resolve is a colour-space
  correction that makes Alloy blending mathematically right, not a place for tonemapping, exposure,
  or bloom — those plug in later, at the same point.
- **Ambient occlusion pass** — screen-space visibility (GTAO) computed from the depth prepass BEFORE the
  colour pass and read by the lit stage as a scale on its ambient term, beside the material's AO map
  (ADR 0021, #379). A lighting input, not a post stage: vanilla's SSAO darkens the finished frame.
- **Post stack** — the ordered display-space stages that run after the resolve, where the engine ran
  `Camera::DoFrameBufferEffects` (ADR 0020, #375; the aa stage = SMAA, #377). Each stage reads the previous output and hands
  display space on; the last one writes the engine's target and carries the frame's one dither. A
  stage stands down alone; the stack never takes the frame down. Gamma is the first stage.
- **Replacement class** — a content class the Alloy frame reimplements rather than reproduces: the engine's
  own path is left untouched and simply not called, and the Alloy one is built to the technique the class
  should have had. Distinct from *owned*, which only says who submits the same geometry. Grass (#127) is the
  first: its punch-through is not fixed, it is made structurally impossible, because nothing in the owned
  class is ever semi-transparent and depth-writing at once.
- **Patch** — the Alloy grass unit of culling and drawing: one engine triangle list (a bin's faces sharing one
  grass material) scattered into a contiguous run of blade instances. Frustum- and distance-culled whole,
  drawn as one instanced call.
- **Screen-door fade** — fading geometry out by discarding a growing share of its fragments against a
  per-pixel dither, instead of ramping its alpha. Every surviving fragment stays fully opaque, so a fade
  cannot introduce a blended fragment — which is what lets a fading class keep writing depth.
- **Bracket** — one entry into the owned GL context and back (fence, flush, two `glXMakeCurrent`,
  error drain). Every bracket is a GPU pipeline bubble, so the count per frame is a **budget**, not
  an implementation detail: Alloy passes are grouped to fit inside existing brackets rather than
  taking their own.
