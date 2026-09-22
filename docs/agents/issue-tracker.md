# Issue tracker: Plane

Live work for this repository lives on the SoW Plane at `https://plane.westgate.pw`,
workspace `shadows-over-westgate`, in project **`CX`** (Client Extender) -- a work item
is named `CX-12`. Gitea keeps code, pull requests and releases.

The workspace is shared with `sow-codebase` (`GAME`), NodeBB (`WEB`) and the platform
(`PLAT`); a reference like `GAME-12` is unique across the whole workspace. File against
the project that owns the outcome: client/renderer work is `CX`, the server end of a
packet or a `.shd` in topdata is `GAME`. The credentials story and what `tea` is still
for live once in `sow-platform/docs/agents/plane-tracker.md`.

## The client

`plane` is `bin/plane`, a byte-identical copy of sow-platform's
`infra/nixos/scripts/plane.py` (fix bugs at the source, then copy it out again). It
needs Python 3 and your own `PLANE_API_KEY`, set in the environment or in
`~/.config/sow/plane.env`; base URL and workspace default correctly. `bin/plane --help`
lists the surface: `file CX "..."`, `show CX-12`, `comment`, `close`, `claim`, `parent`,
`block`, `frontier`.

The Plane MCP server (`.mcp.json`, `uvx plane-mcp-server`) points at the same instance
for ad-hoc reads and edits the CLI doesn't cover. It reads `PLANE_API_KEY` from the shell
environment only, not from `plane.env`. The CLI stays the path for wayfinding, since
`frontier` and the rules below live in it.

## Old `CX-<n>` and `#<n>` references

Numbering restarted at `CX-1` on 2026-09-22. Every `CX-<n>` / `#<n>` in commits, ADRs,
docs and memories written before then points at the retired local Plane (Gitea numbers
were preserved there) and was **not** migrated. Treat such a reference as history, not
as a live item on the new instance: a low `CX-<n>` there is a different, newer item.

## Wayfinding

A **map** is a work item labelled `wayfinder:map`. A **ticket** is a real child of it
through the native parent field, labelled `wayfinder:research`, `:prototype`,
`:grilling` or `:task`. **Blocking** is a `blocked_by` relation. The **frontier** is
the map's open children with no assignee and no open blocker, and
`bin/plane frontier CX-<map>` answers it.

The rules from `sow-codebase/docs/agents/issue-tracker.md` hold here unchanged:

1. **A map's tickets are its direct children, never nested** -- `frontier` looks one
   level deep. Sub-structure means a second map.
2. **A ticket has exactly one parent.** A ticket serving two arcs takes the owning map
   as parent and reaches the other through `blocked_by`.
3. **A map and all of its tickets live in one project.** Map-to-map edges are
   `blocked_by`, which does cross projects.
4. **A relation cannot be deleted through the public API.** Write a blocker when sure;
   removing one is a human action in the UI.
5. **`external_id` + `external_source`** is the idempotency key for anything that
   writes into Plane.

## When a skill says "publish to the issue tracker"

`bin/plane file CX "<title>"`.

## When a skill says "fetch the relevant ticket"

`bin/plane show CX-<n>`.
