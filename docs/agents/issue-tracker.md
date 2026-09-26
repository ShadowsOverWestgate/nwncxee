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

`plane` is `bin/plane`, a copy of sow-platform's `infra/nixos/scripts/plane.py`. Fix a
bug at the source, never here: a change to the source opens a pull request on the branch
`chore/plane-client-sync` that replaces this copy (sow-platform ADR-0062).

It needs Python 3 and your own `PLANE_API_KEY`, nothing else. Create the key in Plane
(sign in with Gitea, then profile -> API tokens) and put it in the environment or in
`~/.config/sow/plane.env` as `PLANE_API_KEY=<key>`. Base URL and workspace are baked in.
`bin/plane whoami` confirms the key.

| Task | Command |
| --- | --- |
| Read a whole thread | `bin/plane show CX-12` |
| Search titles | `bin/plane find CX "<text>"` |
| File work | `bin/plane file CX "<title>" --body-file body.md --label Kind/Bug` |
| Comment | `bin/plane comment CX-12 "<text>"`, or `--body-file comment.md` |
| Replace a body | `bin/plane edit CX-12 --body-file body.md --expect <token>` (the `body:` line `show` printed) |
| Take an item | `bin/plane claim CX-12` |
| Close | `bin/plane close CX-12` |
| Answer, index on the map, close | `bin/plane resolve CX-12 --body-file answer.md --gist "<one line>"` |
| Nest / block | `bin/plane parent CX-12 CX-1`, `bin/plane block CX-12 CX-9` |
| A map's children / frontier | `bin/plane children CX-1`, `bin/plane frontier CX-1` |

Bodies and comments are Markdown; a bare `GAME-12` becomes a link. Every command takes
`--json` for the raw object. `bin/plane <command> --help` has the flags. The full
reference is `docs/agents/plane-tracker.md` in sow-platform.

The Plane MCP server (`.mcp.json`, `uvx plane-mcp-server`) points at the same instance
for reads and edits the CLI doesn't cover. It reads `PLANE_API_KEY` from the shell
environment only, not from `plane.env`.

## Closing a work item

Plane knows nothing about pull requests: merging one closes nothing, whatever its body
says. Close the work item yourself once the change it asked for has landed -- `bin/plane
close CX-12`, or `bin/plane resolve CX-12` when it carries an answer. Name the item in the
pull request body (`CX-12`) so the link exists for readers.

## Old `CX-<n>` and `#<n>` references

Numbering restarted at `CX-1` on 2026-09-22. Every `CX-<n>` / `#<n>` in commits, ADRs,
docs and memories written before then points at the retired local Plane (Gitea numbers
were preserved there) and was **not** migrated. Treat such a reference as history, not
as a live item on the new instance: a low `CX-<n>` there is a different, newer item.
For the same reason `bin/plane show sow-client-extender#700` finds nothing: that lookup
works only for issues the org-wide importer carried over, and this repository's were not.

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
