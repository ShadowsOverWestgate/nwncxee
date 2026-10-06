# 0025 — One knob registry, three views, one file, one panel

Status: accepted (2026-09-14, wayfinder map #556). Amends [0013](0013-tuning-knob-policy.md);
corrects the whole-extender opt-out described in [0014](0014-module-layout.md) and CLAUDE.md.
Spec: [`docs/spec/unified-settings.md`](../spec/unified-settings.md).

## Context

ADR 0013 got the knobs out of the environment and into two buckets, chat command or config
setting, and the count is honest now: `/knobs` can print every one. What it did not give the
player is a place to *find* them. Today a knob is edited by typing `/light gain 0.5` into the
custom chatbox, or by editing `cx.cfg` between launches; the three per-feature panels
(dialog, nameplate, quickbar) each hang a gear off their own window and share a `SetAct` union
enum; look knobs reset every launch because a chat command has no store; camera writes
`cx-camera.cfg`, chat `cx-chat.cfg`, five files in all. The ~150 Alloy dials are strings in
`*Keys` tables with a `Tune(key, float)` switch per pass, so nothing but the switch knows a
dial's range, and nothing at all knows its label.

the research on CX-166 measured the doors. Its recommended door, Path A′
(chain the stock Options window's Nuklear render and append our rows), was rejected here: it
puts every CX knob inside a window whose layout solver we do not control, at the engine's font
and density, with no scroll region of ours, and it earns nothing over a window we already draw
on every frame at the main menu and in game. What the research got right is kept: the
descriptor model is the critical path, and the panel is a view of it.

The user's requirements, verbatim from the map: only two ways to open settings (`/settings`
and a large gear lower-left at the main menu, same panel); one panel for all features; three
columns, categories / submenus / knobs, each scrolling; every knob translated and
user-friendly; every feature can be disabled; a dropdown primitive; UX and readability first.

## Decision

**A knob is one declaration. The chat command, the `cx.cfg` key and the panel row are three
views of it, and nothing else can create a knob.**

1. **One typed registry.** `Knob<T>` over a non-generic `Knob` base carries key, label, kind,
   tier, effect, section, help and a `Changed` hook; the value lives in the knob (`V` live,
   `Boot` immutable from construction for restart readers, `Default`). Placement lives on the
   `KnobGroup`: **feature = group = submenu = chat command = reset unit = key prefix**, so every
   key is two levels, `grass.density`, `chase.fov`. Constructing a knob binds it from the loaded
   file, so a static field is correct on first touch, before any service initialises.
2. **Three views, derived.** `/<group> <knob> <value>`, `/<group> reset` and the bare `/<group>`
   report are generated per group, no aliases. `cx.cfg` is written from a registry walk. The
   panel row *is* the declaration: no action enum, no per-feature layout code.
3. **Tiers and effects are declared, never implied.** User (a row, help text mandatory),
   Advanced (a collapsed section), Chat-only (command and `/knobs`, never a row), Hidden
   (persisted only: per-window chat state). Effect is Live or Restart; only three masters are
   restart, `alloy.enabled`, `shadow.enabled`, `chat.enabled`, because each replaces an engine
   path at install. Everything else that can be live is live.
4. **One file, generated.** `cx.cfg` holds off-default keys only, alphabetically, unknown keys
   preserved verbatim, comments not; read once pre-main, rewritten by a ~1 s trailing debounce
   on a pool thread via tmp + rename. Every `cx-<feature>.cfg` writer is deleted, no migration.
   `cx-hotkeys.cfg` keeps only the consent tokens, which are state.
5. **Commands are gated; the panel is primary.** `general.commands` is off by default because
   servers may own the same names; `/settings` is the sole exemption and dispatches from the
   vanilla prompt through a `CGuiInGameChatPrompt::HandleEnter` chain, so it works with the CX
   chatbox off. `/knobs` and the renderer report become panel content (General → Debug info,
   Renderer's status line).
6. **The panel is the sole settings surface.** One centred window, three scrolling columns,
   two doors. Every per-feature gear, panel and settings subcommand is deleted. Chat's
   per-window state stays on the chat window as Hidden-tier knobs; that is content, not settings.
7. **Every feature has a master toggle; the extender has none.** "Everything off" is every
   master off with the hooks forwarding; "no CX" is an uninstall. `lockchat` dies with no
   legacy read. Value rows grey under an off master; action rows stay live, tree-wide, because
   "turn hotkeys on to forget a malicious server" is backwards.

### What survives from 0013

No environment variables (`CX_MANAGED` stays the bootstrap's one exemption). A knob with no
declaration does not ship. Delete what only ever bisected a closed bug.

### What changes

0013's two buckets collapse into one declaration with a tier. Every knob persists and has a
default; there is no session-only knob. The panel is the primary surface and the command the
gated secondary, the reverse of 0013's assumption that a chat command is the better UX.

## Consequences

- `PassLifecycle.Tune/Describe/Reset`, the `*Keys` constants, `CameraConfig.ToText/Parse`,
  `RenderCommands.Tune`, `ConfigFile.Load/Save/Baseline`, `Settings.Bool/Int/Float` at call
  sites, `SetKind.Cycle`, the global-chrome half of `SetAct`, `DialogSettings`,
  `NameplateSettings`, `QuickbarSettings`, the nuklear hotkey manager and `GameGate.Locked` all
  go. The spec's deletion list is the checklist.
- Four new primitives: scrollable column, dropdown (its list scrolls), tooltip, link row; plus
  the main-menu gear, which needs an exemption from the #451 input yield and a lifted `InGame`
  guard on `SyncUiScale`.
- The `HandleEnter` chain is a build prerequisite of the chat master, and needs its Windows
  `At` row: the class is not exported.
- CLAUDE.md's "Tuning knobs" section and its `lockchat=1` sentence, and 0014's `CX_LOCKCHAT`
  pivot, describe an opt-out that never installed nothing; they are corrected to this ADR.
- Two knobs must never flip together today. The day a pair must, the pair becomes one
  `Knob<T>` with an object `T`; that is the ceiling of the plain-field threading model.
- Search across categories, multi-font, the auto-update consent row and the gear's pixel anchor
  are the map's remaining fog; each is its own ticket when its blocker clears.

### Amendment (CX-692, 2026-09-21): the radial layout is a bag

A rearranged radial menu -- pinned and reordered rows -- is per-install content the way chat's
per-window state is (decision 6), so it lives in `cx.cfg` as a Hidden-tier `KnobBag`
(`radial.p<N>.*`), not in a state file. The `cx-hotkeys.cfg` exemption in decision 4 stays
what it is: a place for secrets, not for dynamic state. Spec §4.2 has the key shape.
