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
- **ChatWindow** — an identifiable chatbox window (`rect`, `tabs`, `activeTab`,
  `editBuffer`). v1 runs exactly one; the object exists so a later windowing system
  can orchestrate many.
- **Tab** — a view inside a window: a `label`, a **filter** (channel-mask + optional
  `playerId`), a scroll offset, and a **sendChannel**. Every window has at least one.
- **Channel** — a chat stream. **Sendable** (Talk, Shout, Whisper, Tell, Party, DM)
  or **receive-only** (System, Combat, Server).
- **sendChannel** — the channel a tab's input line targets. `null` on a receive-only
  tab → that tab shows **no input line** (you cannot type into a combat log).
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
