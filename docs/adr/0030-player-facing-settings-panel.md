# 0030 — The settings panel is the player's: tiers by audience, shared submenus, quality presets

Status: accepted (2026-10-09). Amends [0025](0025-unified-settings-model.md). Spec:
[`docs/spec/unified-settings.md`](../spec/unified-settings.md) §1, §3, §4.

## Context

ADR 0025 made one declaration the panel row, the `cx.cfg` key and the chat command. It did not say who a
row is for. Feature = group = submenu turned every new Alloy pass into its own submenu (Graphics reached 20),
every "CX vs engine" comparison switch from the render-ownership work became a boxed master at the top of
one, and the group's status line -- counters written for whoever was debugging that pass -- was drawn at
the top of the panel. A submenu could exist only to show diagnostics. After CX-111 most of those
comparisons have no engine side left: "Off, the stock grass draws" is no longer true of anything.

## Decision

1. **Tier is decided by audience.** User and Advanced rows are for players. An engine-path comparison, a
   bisect, a budget, a per-frame cap, a shader constant whose right value belongs to whoever tuned the
   shader, and the master of a core feature nobody should turn off are **ChatOnly**: still a `cx.cfg` key
   and a `/<group>` command, never a row. A knob nothing reads is deleted. `Declare(masterTier:)` hides a
   master; while such a master is off its rows grey under a strip that names the cfg key and turns it back
   on when pressed.
2. **Two status lines.** `Status` is the diagnostic one: bare `/<group>` and `/knobs` print it. `PanelStatus`
   is the panel's, written for a player, and nearly every group has none. A group with no User/Advanced row
   and no `PanelStatus` has no submenu.
3. **Submenus are shared.** Groups naming the same `submenu` share it, each under its `title` heading,
   in `order` (one global sort key; submenus appear by their lowest). Keys and commands do not change.
   Categories: General · Interface · Graphics · Audio · Controls.
4. **Quality presets are ladder defaults.** `knob.Ladder(driver, rungs…)` makes the default the rung of a
   dropdown driver's current choice; an unset key follows it and a driver set moves the member to its new
   rung. `quality.preset` drives one dropdown per area (`quality.shadows`, …), which drives the real knobs.
   High is the shipped defaults. `cx.cfg` keeps only what differs from the preset.
5. **The registry enforces it.** The live SelfCheck fails a submenu with more than 14 User rows and any panel
   row whose label or help carries developer words (`A/B`, debug, diagnostic, bisect).
6. **`alloy.enabled` stays, as a cfg-only key**: the way to run without CX's renderer short of uninstalling,
   and the key `AlloyGate`'s failure notice names. No group is parented on it.

## Consequences

- The panel shrinks to what a player tunes; everything removed is still reachable from chat and `cx.cfg`.
- A new knob defaults to the question "would a player change this?" -- the lint catches the obvious no.
- Presets ship with High = today's defaults; Low/Medium/Ultra rungs are reasoned from the perf notes and
  owe an `/alloy bench` pass on real hardware.
