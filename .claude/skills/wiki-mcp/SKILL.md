---
name: wiki-mcp
description: Keep the Westgate wiki a mirror of the repo through the `westgate-wiki` MCP server. Use for every change a player, developer or operator could notice (a mechanic, command, system, setup step, data format, third-party asset), whether or not an article covers it yet, to update, create or retire the article in the same effort.
---

# Westgate wiki through MCP

The wiki is a **mirror** of the repo. A change that adds, alters or removes
something a reader could notice updates the wiki in the same effort. The
project is semi open source, so technical documentation is public as well.

The `westgate-wiki` server (`https://westgate.pw/mcp`) acts as the member who
connected it, with that member's own forum privileges. The first call opens a
browser login on the forum; the member picks a tier on the consent page.

| Tier | Allows | Pick it when |
|---|---|---|
| 1 Read | search, list, read, history, revision | checking what the wiki says |
| 2 Read and write | also create, edit, revert | the effort updates documentation (the usual case) |
| 3 Read, write, tombstone and move | also tombstone, restore, move | the effort retires or relocates articles |

Ask for the lowest tier the task needs. A call above the tier comes back as a
`scope` refusal: tell the member to reconnect with a higher tier.

## Workflow

1. **List** what the change adds, alters or removes that a reader could
   notice. A change with nothing observable (a refactor, a test) has an empty
   list and ends here.
2. **Find** the article for each item: `wiki_search` on its in-game or service
   name, then `wiki_list_namespace` on the namespace that owns it (see
   [Where an article lives](#where-an-article-lives)). An item has no article
   only when both come back without it.
3. **Read** each found article with `wiki_read` and keep its `revisionId`.
4. **Write**, one branch per item:
   - found, still true in part → `wiki_edit` with the full HTML body and the
     `revisionId` from step 3;
   - missing → `wiki_create` in the owning namespace, then link it from the
     nearest related article;
   - the thing it describes is gone → edit the article to say so, or
     `wiki_tombstone` it (tier 3) when nothing of it remains.
5. **Verify** with `wiki_read`: each body holds the change.

Done when every item from step 1 is described by an article in the namespace
that owns it, and the PR body lists those articles by title.

## Where an article lives

| Namespace | Holds |
|---|---|
| Documentation | Technical documentation of the server and its content: services, plugins, hooks, console commands, data formats. Public on purpose. |
| For Staff | Hidden, visible to staff (developers and dungeon masters): infrastructure, operations, staff tooling, work in progress, spoilers and DM notes. Credentials and secrets never go on the wiki at all. |
| Guides | Walkthroughs that help a player do something. |
| Systems | Player-facing mechanics that no generated page covers. |
| Lore | The setting's prose. |
| Credits | Attribution, one article per category: Code, Tools, Art, Audio, Fonts, Infrastructure, Contributors, Special Thanks, and Assets split by kind under its own subpages. Every third-party asset, model, script, library or pack the change brings in gets a row in the matching article's table. Add a row, and create no article for a single source. |
| Frequently Asked Questions | One article. Add a question once players ask it repeatedly. |

Root articles: Main Page, Community Charter, Setting Overview, The Team, and
Gameplay Changes. Gameplay Changes summarises how the server differs from
vanilla Neverwinter Nights, so a change to a core rule updates its entry
there too. The others change only when the member asks.

## Generated namespaces

The topdata deployer writes the pages in **Classes**, **Races**, **Feats**,
**Skills**, **Spells** and **Items/Item Types** from the game data; Deities
joins them when its generator ships. The deployer owns their page set:

- Create no article in a generated namespace. A page missing there means a
  missing data row, so change the data. Put hand-written mechanics in
  Systems and link to the generated page.
- A generated body starts with `<!-- sow-topdata-wiki:page=... -->`. That
  marker, wherever it appears, is the test for a generated page.
- The text between `sow-topdata-wiki:managed:start` and `managed:end` is
  regenerated on every deploy: change the topdata source that renders it.
- Edit only inside the two `sow-topdata-wiki:manual:start` / `manual:end`
  regions: **Overview** at the top and **Bottom** after the generated text.
  Every marker comment stays exactly as read.
- **Wiki Status** is generated with no manual region. Leave it alone.

## Writing rules

Everyone can read Documentation, Guides, Systems, Lore, Credits and the FAQ.

- Write for a reader who has only the wiki: name systems by their in-game or
  service names.
- Link to other articles with the `url` a search or read returned.
- Leave out file paths, repository names, `.md` files, and any mention of
  agents, skills, or tooling. For Staff may discuss tooling, and still
  carries no local path or secret.
- Spoilers stay in For Staff. Public articles describe what
  a player can learn in play.
- The body is HTML: `h2`/`h3`, `p`, `ul`/`ol`/`li`, `table`, `pre`/`code`,
  `strong`/`em`, `a`. It is sanitized on save, so re-read after writing when
  markup matters.

## Refusals

| Reason | Do |
|---|---|
| `conflict` | The article changed since your read, or someone has it open in the editor. Read again, redo the change on the new body, retry once. If someone holds it open, tell the member who. |
| `rate-limited` | Wait `retryAfter` seconds, then continue. |
| `privilege` | The member lacks the forum privilege. Report it; a higher tier does not help. |
| `protected-page` | Index pages and the homepage are never tombstoned, moved or renamed by an agent. A person does it in the browser. |
| `scope` | See the tier table above. |
| `not-found`, `validation` | The message names the bad input. Search again for the path or tid, or fix the title or body, and retry. |
