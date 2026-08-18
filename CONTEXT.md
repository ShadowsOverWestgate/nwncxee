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
  the vanilla one; reversible via the `SOWX_LOCKCHAT` opt-out.

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
