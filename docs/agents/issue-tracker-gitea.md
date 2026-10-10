# Issue tracker: Gitea (self-hosted)

Issues and specs for this repo live as issues on the self-hosted Gitea at
`git.westgate.pw`, repo `ShadowsOverWestgate/sow-client-extender-archive` (private since the public release). Use the
[`tea`](https://gitea.com/gitea/tea) CLI for all operations. Run inside a clone
so `tea` auto-detects the repo and login (`westgate` / user `xtul`).

## Conventions

- **Create an issue**: `tea issues create --title "..." --description "..."`. Use a heredoc for multi-line bodies.
- **Read an issue**: `tea issues <index>` (detail view). Add `tea comments <index>` for the thread.
- **List issues**: `tea issues list --state open --fields index,title,labels,assignees` (add `--labels <name>` to filter).
- **Comment**: `tea comments <index> "..."`
- **Apply / remove labels**: `tea issues edit <index> --add-labels "..."` / `--remove-labels "..."`
- **Close / reopen**: `tea issues close <index>` / `tea issues reopen <index>`

For anything `tea` doesn't cover (issue dependencies, database ids), hit the
Gitea REST API directly at `https://git.westgate.pw/api/v1` with a token.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external PRs as feature requests.)_ When `yes`, run PRs through the same labels/states via `tea pulls`.

## When a skill says "publish to the issue tracker"

Create a Gitea issue with `tea issues create`.

## When a skill says "fetch the relevant ticket"

Run `tea issues <index>` plus `tea comments <index>`.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets.

- **Map**: one issue labelled `wayfinder:map` holding the Notes / Decisions-so-far / Fog body. `tea issues create --labels wayfinder:map`.
- **Child ticket**: an issue with `Part of #<map>` at the top of its body and a checklist entry in the map body (Gitea has no native sub-issues). Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Assign to the driving dev once claimed.
- **Blocking**: Gitea's native issue dependencies via the REST API. Add an edge with
  `POST /api/v1/repos/{owner}/{repo}/issues/{child}/dependencies` and a JSON body naming the blocker.
  **Gotcha**: the body's `index` is the blocker's issue number, and you must pass the blocker's `owner`/`repo` fields even for same-repo edges or the API 404s. Fall back to a `Blocked by: #<n>` line in the child body where the API is unavailable. A ticket is unblocked when every blocker is closed.
- **Frontier query**: list the map's open children, drop any with an open blocker or an assignee; first in map order wins.
- **Claim**: `tea issues edit <n> --add-assignees @me` — the session's first write.
- **Resolve**: `tea comments <n> "<answer>"`, then `tea issues close <n>`, then append a context pointer to the map's Decisions-so-far.
