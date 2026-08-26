# CONTEXT

Glossary for the client extender. Domain vocabulary only — no implementation
detail, no decisions (those live in the Gitea tracker: wayfinder map #13 and its
tickets). Terms crystallised while charting the native chatbox reimplementation.

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
  the vanilla one; reversible via the `lockchat=1` opt-out in `sowx.cfg`.

## Framework

- **Core vs shell** — *Core* is pure C# logic with no native references (testable
  off-device); the *native shell* is the hooks, GL draw, and SDL calls. Features land
  in the Core behind the shell.
- **Extender-optional** — a stock client plays vanilla; the extender only ever reuses
  existing wire opcodes, so an un-extended client loses nothing.

## UI framework

- **UI framework** (`Sowx.Ui`) — the shared layer every windowed feature builds on. Owns
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

## Rendering (Alloy)

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
- **Shader override** — a `sowx_*.shd` a *user* places where the engine's resman will serve it
  (`development/`, `override/`, a hak), replacing that stage's **embedded stage** at load. A
  first-class customization surface — sowx ships defaults and lets them be replaced — and explicitly
  **not** a delivery channel: no content repo ever ships one, because a shipped copy is a second copy
  of a contract that lives in C#. Refused by the stage guards when it breaks that contract; a refused
  or uncompilable override falls back to the embedded stage with a log line.
- **Shader dump** — the current **embedded stages** written out as editable `.shd` files, one per
  overridable resref, into the client's user directory. The starting point for a **shader override**,
  and current by construction because it dumps what is compiled in. Distinct from vanilla shaders,
  which sowx never modifies.
- **Tuning knob** — a runtime-adjustable value the extender exposes, in exactly one of three kinds: a
  **chat command** (tunable while looking at its effect — look, debug visualisation, per-pass
  toggles), a **config setting** (must be decided before the first frame, or describes the machine
  rather than the look), or *deleted* (a bisect switch whose ticket has closed). Environment
  variables are not a kind; a knob with no kind does not ship.
- **Render mode** — which renderer draws the 3D scene: the *owned* mode (Alloy) or the
  *vanilla* mode (the engine's own path). Both ship permanently; a device that cannot run the
  Alloy mode still runs every other extender feature. The mode is whole-scene — never a
  per-class mixture — and switching it is frame-atomic.
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
