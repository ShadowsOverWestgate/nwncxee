# Issue tracker: Plane (local, trial)

Issues and specs for this repo live as **work items** on a local Plane CE (v1.4.2) at
`http://localhost`: workspace `shadows-over-westgate`, project **SoW Client Extender**,
identifier `CX`. This is a **trial** of Plane as the team tracker. The previous tracker
(Gitea) is documented in `issue-tracker-gitea.md`; every Gitea issue was migrated on
2026-09-20 with its **number preserved**: Gitea `#42` is `CX-42` (PR numbers are gaps).
Migration tooling: `tools/plane/` (re-runnable, idempotent per number). **To revert**:
swap the two files back and delete `.mcp.json`.

## Two ways in

1. **Plane MCP tools** (preferred; `.mcp.json` at the repo root): `workitem`,
   `workitem_comment`, `label`, `state`, `member`. Each takes an `action`; the tool
   description lists the actions and their params. The server reads `PLANE_API_KEY` from
   your shell env.
2. **REST**, for what the MCP server can't reach on this CE version (relations; see
   Wayfinding): `curl -s -H "X-API-Key: $PLANE_API_KEY" http://localhost/api/v1/workspaces/shadows-over-westgate/projects/$P/...`.
   Swagger for this exact version: `http://localhost/api/v1/schema/swagger-ui/`.

Constants (stable; no need to look them up):

| what | id |
|---|---|
| project `P` | `1910279f-42d5-4ad3-ae1c-55ab185f3d68` |
| state Todo (`unstarted`) | `2591bf46-a4a2-4afc-b839-16fdb55cf02a` |
| state In Progress (`started`) | `18f89cc1-54cf-4010-8da7-4dbb1a126714` |
| state Done (`completed`) | `3f5dd86a-6775-4ddd-8558-afe2843506c5` |
| state Cancelled (`cancelled`) | `c42f767f-df1b-428d-aaac-8cb110ac8557` |
| label `wayfinder:map` | `fd1c5714-ea58-45bd-a3f5-ddf63937a95d` |
| label `wayfinder:task` | `840392da-0aa4-4d9a-972d-33594ac80211` |
| label `wayfinder:research` | `46f23d85-dc4d-4f93-9dc8-95c873c0a49e` |
| label `wayfinder:grilling` | `73eeef27-3533-4ff2-9016-6941ee474578` |
| label `wayfinder:prototype` | `21b04555-3283-4c48-bd48-ddac47a8609b` |
| label `ready-for-agent` | `1d7ccfb7-5eeb-4c96-89d0-2dd33f499cce` |

Work items are UUIDs, but humans and the UI say `CX-<n>` (`sequence_id`).
`workitem retrieve_by_identifier` with `workitem_identifier: "CX-<n>"` turns one into the
other. In prose, refer to `CX-<n>` plus the title, never the UUID.

## Conventions

- **Create**: `workitem create` (`project_id`, `name`, `description_html`; optional
  `labels`, `assignees`, `parent`, `state`). Bodies are **HTML**, not Markdown: write
  `<h2>`/`<p>`/`<ul>` yourself (`description_stripped` takes plain text if HTML is overkill).
  New items land in `Backlog`; pass `state` Todo for anything actionable.
- **Read**: `workitem retrieve_by_identifier`, then `workitem_comment list` for the thread.
- **List**: `workitem list` with `project_id`, `expand: "state"`, and a sparse `fields`
  (`id,sequence_id,name,parent,state,assignees,labels`). CE rejects `pql`: filter the rows
  client-side.
- **Comment**: `workitem_comment create` with `comment_html`.
- **Labels**: `workitem manage_label` (`add_label_id` / `remove_label_id`).
- **Close / reopen**: `workitem update` with `state` = Done / Todo. There is no close verb;
  "closed" = state group `completed` or `cancelled`.

## Pull requests as a triage surface

**PRs as a request surface: no.** Code and PRs stay on Gitea. Reference work items from
commits and PR bodies as `CX-<n>` (Gitea will not link them; the text is the pointer).

## When a skill says "publish to the issue tracker"

`workitem create` in project `P`.

## When a skill says "fetch the relevant ticket"

`workitem retrieve_by_identifier` + `workitem_comment list`.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a work item; tickets are its native **sub-work-items**.

- **Map**: a work item labelled `wayfinder:map` holding the Destination / Notes /
  Decisions-so-far / Fog body.
- **Child ticket**: `workitem create` with `parent` = the map's id and label
  `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). No `Part of` line: the UI
  nests children under the map.
- **Blocking**: Plane's native relations, **REST only** (the MCP server's
  `workitem_relation` calls `/dependencies/`, which this CE version lacks -> 404).
  Add an edge on the **child**:
  `curl -s -X POST -H "X-API-Key: $PLANE_API_KEY" -H 'Content-Type: application/json' -d '{"relation_type":"blocked_by","issues":["<blocker uuid>"]}' http://localhost/api/v1/workspaces/shadows-over-westgate/projects/$P/work-items/<child uuid>/relations/`
  `GET` the same URL returns `{"blocked_by":[{"issue_id":...}], "blocking":[...], ...}`:
  ids only, so resolve each blocker's state against the list you already fetched.
- **Frontier query**: `workitem list` (params as above), keep `parent == map`, drop rows
  whose `state.group` is `completed`/`cancelled` or whose `assignees` is non-empty, then
  `GET .../relations/` on each survivor and drop it if any `blocked_by` id is still open.
  Sort by `sequence_id`; first wins.
- **Claim**: `workitem manage_assignee` with `add_user_id` = `member me` -> `id`. The
  session's first write.
- **Resolve**: `workitem_comment create` with the answer, `workitem update` to Done, then
  append a context pointer to the map's Decisions-so-far: `retrieve` the map, edit its
  `description_html`, `update` it back (it is the whole body; Plane adds `class`/`data-id`
  attributes on save, leave them).
