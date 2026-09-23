# Unified settings — build spec

The contract for the one settings panel, the knob registry behind it, and the one `cx.cfg`.
Decided on wayfinder map #556 (tickets #557–#568); the decisions are recorded there and in
[ADR 0025](../adr/0025-unified-settings-model.md). This file assembles them so a build
session needs nothing else. Glossary: `CONTEXT.md` → *Settings*.

Research it rests on: [`docs/research/feature-disable-census.md`](../research/feature-disable-census.md)
(live vs restart per feature, with file:line evidence), [`docs/research/main-menu-surface.md`](../research/main-menu-surface.md)
(present, input and layout at the main menu; the `/settings` seam), and
`docs/prototypes/settings-panel-560/index.html?variant=B` on branch `prototype/settings-panel-560`
(the pinned look). Rejected door: `docs/research/settings-panel-ownership.md` §4 (Path A′).

Conventions in the tables: kind **T** toggle · **S** slider · **D** dropdown · **B** button
(action row) · **X** text · **Act** action row. Tier **U** User · **A** Advanced · **C**
Chat-only · **H** Hidden. Effect **L** live · **R** restart. "today" = the current chat key,
`cx.cfg` key or `cx-<feature>.cfg` key. Help is written for U rows (mandatory); an A row gets a
one-liner only where the label is not enough. Chat-only keys keep today's spelling, label =
key, no help.

## 1. The registry

```csharp
public abstract class Knob            // metadata: what the panel, cfg, chat and /knobs see
{
    public string Key;                // "grass.density": cfg key, chat form "/grass density"
    public string Label;              // "Grass density"
    public KnobKind Kind;             // Toggle | Slider | Dropdown | Swatch | Button | Text
    public Tier Tier;                 // User | Advanced | ChatOnly | Hidden
    public Effect Effect;             // Live | Restart
    public string Section;            // optional header inside the submenu ("Wind")
    public string Help;               // row help / tooltip; REQUIRED when Tier == User
    public Action? Changed;           // side effect after a set; a Button's press
    public abstract string Format();  // cfg / chat / /knobs form
    public abstract bool Parse(string s);
}
public sealed class Knob<T> : Knob
{
    public T V;                       // the live value: what the panel edits and the cfg persists
    public T Boot;                    // bound at construction, never changes; Restart readers read THIS
    public T Default;
    public T Min, Max; public T Step; public string Unit;   // Slider (float or int)
    public string[] Choices; public T[] Values;              // Dropdown: labels shown / values stored
}
```

Built-in `T`: `bool` (Toggle), `float`/`int` (Slider, Dropdown), `string` (Text, Dropdown),
`uint` (Swatch, `rrggbbaa`). Any other `T` is allowed and supplies its own Format/Parse; the
panel renders it by `Kind`.

**The knob is the storage.** A feature reads `Grass.Density.V` where it read `_density`: a field
read, hot-path safe. Every `Tune`/`Set` switch and every `|`-joined key list is deleted.

### The group

```csharp
static readonly KnobGroup G = KnobGroup.Declare("grass", category: "Graphics", submenu: "Grass",
    master: "Enable Alloy grass", parent: Renderer.Enabled);   // master -> "grass.enabled", first row
static readonly Knob<float> Density = G.Slider("density", "Grass density", 0f, 4f, 0.25f, def: 2f, help: "…");
static readonly Knob<int>   Size    = Ui.Dropdown("font", "Text size", values: Range(6, 32), layout: true, …);
```

- **Feature = group = submenu = chat command = reset unit = key prefix.** Keys are two-level
  `<group>.<knob>`; `.` is the only structural character, `_` is allowed inside the knob segment.
- `master:` declares `<group>.enabled` as the first row; its effect is the feature's effect.
  Optional. A group with no master says so in its submenu header.
- `parent:` names another group's master; when off, this group's *value* rows grey out.
- `G.Section("Wind")` sets the section for knobs declared after it. Row order = declaration
  order; Advanced collapses below User.
- `KnobGroup.Declare` registers the group for enumeration. A group that stands down this session
  still declares, so its rows exist and read "restart / unavailable" rather than vanish.

### Binding

- `Settings.Load` stays pre-main and unchanged: it loads the raw dictionary from `Settings.Path`.
- Constructing a knob binds it: look up its key, `Parse` per T, clamp to `[Min,Max]`, set `V` and
  `Boot`. Unparseable → `Default`. A static field is therefore correct on first touch.
- Keys no declaration claims this session (a Windows-only knob on Linux) are preserved on write.

### Constraints the registry enforces

- Layout-affecting knobs are dropdowns **by construction**: only the `Dropdown` factory has a
  `layout:` parameter.
- User tier requires help: `Declare` refuses (logs, demotes to Advanced); the SelfCheck fails on
  it so `run-demo.sh` catches it.
- A Restart knob's `Boot` is immutable after a set (SelfCheck).
- A Button has no value, no default, is never persisted; tier decides row vs command.

### Threading

Panel/chat set `V` on the input thread; the render thread reads it. A scalar field write is
atomic on x64/Win64 and no two knobs need to change together, so **plain field**: no
`Volatile`, no store-swap (the `KnobPanel.Publish` precedent stays for row lists).
`Changed` runs on the setter's thread, as `Tune` does today.
Ceiling: the day two knobs must flip together, that pair becomes one `Knob<T>` with an object `T`.

### Derived surfaces

- **Chat:** one generated handler per group. `/<group>` reports (the group's status line where
  it has one, else every row's value), `/<group> <knob> <value>` sets (Toggle accepts
  on/off/0/1, Dropdown a value or an index), `/<group> reset` restores every Default. No
  aliases. Chat still accepts any in-range value for a ladder dropdown.
- **cx.cfg:** Format per T: Toggle `0/1`, Slider invariant float / int, Dropdown the stored
  `Values[i]` (`post.tonemap=aces`, `ui.font=14`), Swatch `rrggbbaa`, Text raw.
- **/knobs:** walks groups in declaration order, `key=value (default d) [tier] [restart]` one
  per knob, `(boot b)` when it differs, then every `KnobSurface.Note` supplier. Same text is
  General → Debug info. Gated command.
- **Panel row = the Knob.** No `SetAct` for anything global.

## 2. The file

- The panel writes **`Settings.Path`**, the file `Settings.Load` latched pre-main; never
  `Native.ConfigPath`. The `Ready`/deferral gate leaves the write path. A `-userdirectory`
  client keeps its cx.cfg under the default dir, as its boot keys already do.
- **Off-default knobs only** (`V != Default`). Reset deletes the keys; an untouched knob follows
  a later release's default.
- **Generated:** one fixed header comment ("written by CX; comments are not preserved; edit
  with the client closed"), then one alphabetical list of (off-default declared keys ∪ unknown
  keys verbatim).
- **Read once, pre-main.** No re-read on panel open, no watcher; a hand edit mid-session is
  overwritten by the next write-back, which the header says.
- **Write:** every set restarts a ~1 s trailing timer; the timer fires on a pool thread, walks
  the registry, `Format()`s each knob, writes `cx.cfg.tmp`, then `File.Move(overwrite)`. Last
  writer wins. No write-if-changed compare. The render thread never touches the disk; the input
  thread only arms the timer.
- **Boot keys** are renamed `<group>.<knob>` (`alloy.gl46`, `shadow.size`, `mouselook.sens`,
  `general.logs`) with **no legacy read** of the flat name. `general.logs` is still consumed
  inside `Settings.Load`.
- **Restart rows** show `V` with the `↻ restart` badge, amber when `V != Boot`.
- **Retired:** `cx-camera/chat/combattext/dialog/nameplate/quickbar/ui.cfg` writers and their
  `ToText`/`Parse` round-trips; `ConfigFile.Load/Save/Baseline`. Surviving: `ForEachPair`,
  `ReadTextNow`, `ReadPath` (the #530 `sowx.cfg` fallback), `Ready` (HotkeyStore's gate).
  `cx-hotkeys.cfg` keeps exactly the `[token=hex]` consent sections.
- No migration code: there are no users yet.

## 3. The panel

Pinned by the #560 prototype, Variant B (Roomy).

- **Geometry:** one centred window ≈70 %×75 % of the screen, draggable, not resizable, Escape
  closes, takes keyboard focus. Columns ≈ 18 / 22 / 60, each scrolling independently, scrollbar
  in the chat window's chrome palette.
- **Header:** title, a dashed *reserved* search slot (empty; future search spans all
  categories), close.
- **Row** = label with **one line of help beneath it**, control right-aligned; ≈3.3 em at 14 px
  base (a row without help, or an Advanced row, is compact at 2.4 em). The `(?)` tooltip marker
  appears only on Advanced rows that have help.
- **Master toggle** = boxed first row, bolder label. **Sections** = small-caps headers.
  **Advanced** = a collapsed `▸ Advanced` disclosure at the bottom of the submenu.
- **Parent off** = a warning strip above the rows plus every *value* row at 42 % opacity.
  **Action rows and link rows stay live** under an off master, tree-wide, no per-row flag.
- **Off-default marker** = blue dot at the row edge + a dot on the submenu name. **Reset to
  defaults** top-right of the knob column, per submenu, covers User + Advanced rows only.
- **Restart badge** `↻ restart`, amber when `V != Boot`.
- **Dropdown:** click opens a list; the list scrolls (font size has 27 entries); past 8 entries a
  search row sits above it and typed text filters by substring, Enter picks a lone match (CX-681). Replaces the
  cycle button everywhere.
- **Slider:** inline value box; applies while dragging.
- **Swatch:** expands to R/G/B/A channel rows in place.
- **Status line:** a read-only panel element at the top of a submenu (Renderer's
  "Running: Alloy / Vanilla — <reason>"; Hotkeys' "Approved on this server: …").
- **Link row:** address text; press copies it to the clipboard
  (`SymbolMap.SdlSetClipboardText`, resolved on both platforms), the row flashes "copied".
- **Face:** the panel draws in its own fixed **20 pt design × GUI scale** (the FPS overlay's
  `FacePx` pattern), never in `ui.font`.
- **Apply model:** instant apply, debounced write.

### Doors

1. **`/settings`** — always dispatches, regardless of `general.commands` and of
   `chat.enabled`. Seam: a `Chain` on `CGuiInGameChatPrompt::HandleEnter` (Linux `0x679c50`
   nm; Windows needs an `At` row, the class is not exported) that reads the prompt's edit text,
   runs `CommandRegistry.TryDispatch`, skips the original on a hit. The hotkey observer already
   sits on the same symbol. Consequence: with the gate on, every `/<group>` command dispatches
   from the vanilla prompt too.
2. **The gear** at the main menu: 64 px at 1080p, 24 px inset lower-left, scales with the game
   UI scale, brightens and turns on hover; a glyph-atlas sprite. Main-menu only by requirement.
   Build facts from the research: present and the roll-call already run at the menu (only
   content gates on `InGame`); `SyncUiScale`'s `InGame` guard lifts; the #451 yield
   (`GuiObjectHitCheck` mask 1) would hand every menu press to the viewport-filling
   `CMainMenuBackgroundPanel`, so the gear needs an exemption from the yield; discriminators
   `CClientExoAppInternal+0x98` (live `CMainMenuPanel*`) and `+0x5f0` (reached-menu latch),
   both `Same` on Windows. Owed live checks before the gear ships: `/keydiag` click over empty
   menu background (`gui=` non-zero?); the pixel anchor via `MapGuiSceneToScreen` (sret) at the
   user's resolution; the Windows menu was not looked at.

## 4. The tree

Categories in order: **General · UI · Graphics · Camera · Hotkeys**. One group per submenu.

### 4.1 General

**General** — group `general`, no master (header says so).

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| general.commands | new | Enable chat commands | T | off | U | L | Lets `/grass`, `/shadow` and the other CX commands work in chat. Off, they go to the server like any other text — some servers use the same names. `/settings` always works. |
| general.logs | logs | Write cx.log diagnostics | T | off | A | L | Writes the extender's diagnostic log to cx.log beside cx.cfg from now on; restart to capture the launch lines too. Attach it to a bug report. |
| general.hookdebug | hookdebug | — | T | off | C | R | funchook trace → funchook.log |
| general.presentprobe | presentprobe | — | S 0–200 | 0 | C | R | black-frame pixel read at the present |
| general.pin | pin | — | S −1–1023 | −1 | C | R | main-thread CPU affinity |

`general.logs` is live because `ExtenderLog.Enabled` is one static bool. No ↻ badge.

**About** — group `about`, zero knobs. Four read-only lines: product name + version
(`AssemblyInformationalVersion`), author, contact, releases URL as a **link row**. Source =
one `CX.Framework.Product` const set (Name "NWNCX:EE", Author, Contact, Url);
`HotkeyProtocol.AbuseContact` reads from it. Bare `/about` prints the same.

**Debug info** — group `knobs`, zero knobs: the `/knobs` walk + the Note suppliers (symbol
rot, EngineLayout, font registry state, VRAM, camera feel, post-stack hosting). Scrolls.
Bare `/knobs` (gated). The `font.<name>=<path>` cfg family (#470) is listed here with its load
state; no row.

### 4.2 UI

**Chat** — group `chat`. Header: "Window colours, animation and tabs are on each window's own gear."

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| chat.enabled | lockchat (chat half) | Enable the CX chatbox | T | on | U | **R** | CX's own chat: multiple windows, tabs, portraits, tells routing. Off, the stock chat window runs. Takes effect at the next launch. |
| chat.tellopen | tellopen | When a tell arrives | D | nothing / open a tab / open a window · nothing | U | L | Where a tell from someone new lands: nowhere special, a new tab in the main window, or its own window. |
| w<N>.* (rect, toggles, inputtop, sendch, anim, hold, fade, tau, outline, font, typeface, tsfmt, tssrc, align, bgmode, autohide, locked, tellshere, col.*, t<M>.*) | same | — | per-window state | | H | | edited on the window; submenu reset leaves it alone |

`/newwin` / `/closewin` stay (actions); `/nw` `/cw` die. Chat's `SettingsWindow` survives as
the per-window editor with its own `SetAct`. Since CX-683 it wears the panel's design with ONE
category column -- *Window settings*, one entry per kept tab, *Add new tab...* -- and since
CX-696 it *is* `KnobPanel` (a row `Source` + `Publish`, `SubFrac = 0`, `Modal`), and the font
rows are the window's own (`w<N>.font` / `w<N>.typeface`, absent = follow `ui.font` /
`ui.typeface`), drawn from a face of its own on the shared sheet. The tab bar keeps only the gear:
a new window is the panel header's *Copy this window*, closing is the window category's *Close
this window* (two presses; Main has no row).

**Radial menu** — group `radial` (CX-689..CX-694). Look and animation rows (CX-694) sit in the group
like every other window's; the chat window's per-window gear is the exception, not the pattern.

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| radial.enabled | — | Enable the CX radial menu | T | on | U | L | Right-click menus as a nested dropdown drawn by CX. Off, the stock radial menu runs. |
| radial.lock | — | Lock the layout | T | off | U | L | Rows cannot be dragged and right-clicking one opens no edit popup. Turn off to rearrange again. |
| radial.mode | — | Submenus | D | cascade / drill · cascade | U | L | Cascade: a submenu opens beside its row, on hover. Drill down: it replaces the list where it is, on click, with a Back row on top -- the pointer never travels. |
| radial.dwell | — | Submenu delay | S | 0..600 ms · 250 | U | L | Cascade only: how long the pointer rests on a row before its submenu opens; 0 opens it at once. |
| radial.opacity | — | Opacity | S | 0.2–1 · 1 | U | L | The whole menu's opacity; each colour's alpha applies on top of it. |
| radial.bg | — | Background | Sw | 05060afa | U | L | The lists' fill. |
| radial.frame | — | Frame | Sw | 8c8c8cff | U | L | The 1 px border around each list. |
| radial.hover | — | Highlight | Sw | 38669eff | U | L | The hovered row and the row whose submenu is open. |
| radial.text | — | Text | Sw | d8dee9ff | U | L | Labels, counts and the submenu marks. |
| radial.font | — | Text size | D | as UI / 6..32 · as UI | U | L | The menu's own text size, or the UI text size. |
| radial.typeface | — | Typeface | D | registry names · default | U | L | The menu's own font; default follows the UI typeface. |
| radial.anim | — | Animate | T | on | U | L | Lists fade and slide in as they open. |
| radial.animms | — | Animation length | S | 40..400 ms · 120 | U | L | How long a list takes to appear. |
| radial.p<N>.* (who, root, path, at, fn, param, hide, label, icon, folder, in) | — | — | pinned/reordered/hidden rows, folders | | H | | edited by drag-and-drop in the menu and its row actions; the group reset leaves it alone |

The rearranged layout (CX-692) is content, not settings, exactly as chat's `w<N>.*`: one
`KnobBag` (`radial.p<N>.*`), in-file, no row, no command, reset-proof. Not a separate state
file: the hotkey-consent exemption exists because a token is a secret, not because it is
dynamic. One layout per (server, character): every row carries `who=<server ip:port>|<character
name>` and the menu shows the rows of the identity it opened under, carrying the others through
each save; a row without `who` (a pre-CX-694 file) is adopted by the first identity that loads.
The server half is the connected `ip:port` (`CNetLayer::GetServerNetworkAddress`): a server
that changes address starts a fresh layout, and through the master-server relay every relayed
server shares the relay's address. The character half is the creature's first + last name off
its stats block, not the display name: a disguise (`SetName`) rewrites the override field and
leaves these alone, so it keeps the layout. A real rename starts a fresh one. The servervault
`.bic` resref would be stabler still, but the client keeps no copy of it after login: `CClientExoApp::SetLocalFileName` is called only by the local/DM login sends
(`SendPlayerToServerLogin_LoginLocalCharacter` / `_LoginDMCharacter`, and
`CChooseCharTypePanel::CheckForDefaultCharacter`), and `_LoginServerCharacter` writes its
by-value `CResRef` straight into the message from `CPlayModulePanel::HandlePlayButton` (Ghidra,
2026-09-21); reading it means hooking those three unexported sends. `ponytail:` swap the half
if renames turn out to matter. Each row `p<N>` is one of four things, told apart by `fn`,
`hide` and `folder`:

- a **hoist** (`fn` set): `root=<RootKind>` (`Creature`, `Placeable`, ...), `path=1/4/3/0`
  (the vanilla slot path from the root, the leaf last), `fn=<the leaf handler's Itanium
  name, e.g. _ZN16CClientSpellMenu14RadialMenuCastEl>` (`vt:<off>:<_ZTV>+<n>` for a virtual
  handler, `sub` for a hoisted submenu, `rva:<hex>` on Windows -- the handlers are not
  exported there, so the symbolic half is Linux-only by design), `param=0x<hex>` (the raw
  packed param, the engine's own quickbar identity), `at=<index among the slot rows of the top
  list>`, `label=<the breadcrumb at pin time, "Cast Spell > Wizard > 3 > Fireball">`;
- a **reorder** (`fn` absent): `root`, `path=<the row's own slot path>`, `at=<index within
  its own list>` -- a top-level row moved, or a row moved inside a submenu;
- a **hide** (CX-694, `hide=1`, `fn` absent): `root`, `path=<the row's own slot path>` -- the
  row is dropped from its list; it claims no index. A list's last slot row cannot be hidden, and
  hides that would empty a list are ignored (every row shows), so *Show hidden* stays reachable.
  *Show hidden (N)* in a row's popup drops the list's hides; *Reset layout* drops them too;
- a **folder** (CX-694, `folder=<name>`, no `path`, no `fn`): `root`, `at=<index in the top
  list>` -- a named submenu row of the top list. A hoist carries `in=<folder name>` to sit in
  it; the folder's members are a list of their own for the position model, so a reorder inside
  it never moves a top-level row. One level only (`ponytail:` a folder in a folder is the
  cascade depth the feature exists to remove). Names are unique per root: a rename onto an
  existing name merges the two. *Remove folder* returns the members to the top list. A member is
  a hoist or a **filed slot**: a top-level vanilla row (*Attack*, *Cast Spell*) has a one-step
  path no hoist can walk, so it files as its reorder row carrying `in=<folder name>` -- the slot
  leaves the top list and the folder shows the root's own row for it (a slot the root has no row
  for shows greyed under the label saved at file time). It is the same row kind as a reorder, so
  on the creature kinds (one vptr, self / other / party) it moves whatever sits in that slot,
  never greys. A hide of the slot drops its filing, as it drops a reorder. A slot that already
  carries a self pin at its path (*Everywhere (as self)*) is that pin's row: its popup edits the
  pin, so *Move to ▸* files the self pin, not the slot.

**Also show on** (CX-694): a hoist copied to another root kind is a row of that kind with the
same `path`, `fn` and `param` -- not a synthetic action: the other root must already build the
same subtree, and then the copy is that root's own deep leaf surfaced at its top. It resolves because every world root builds the same subtree
under the same slot with *its* target: slot 1 on the self, other-creature, placeable, door and
ground roots constructs the caster's own `CClientSpellMenu` (`CGuiCreatureMenu::CreateClientSpellMenu`
@0x73b360, `CGuiPlaceableMenu::CreateMenuByID` @0x7ecc30, `CGuiDoorMenu::CreateMenuByID`
@0x7569f0, `CNWCTile::CreateMenuByID` @0x80c160 -- the placeable's with its position and id,
the ground's with `CNWCTile::m_vSpawnPoint` and no object; Ghidra 2026-09-21), and
`CRadialMenu::CreateSubMenu` @0x584790 runs a slot's creator whether or not the slot is
disabled. So *Fireball* pinned from the self menu and shown on *Placeables* fires the
placeable root's own cast leaf at the placeable, through the engine's handler, with no new
native surface. The target rule is the engine's: the spell creators (`CreateSpellSubRadial`
@0x6f0590, `CreateSequentialSpellMenu` @0x6f1f80) call `CClientSpellMenu::TargetIsValid`
@0x6f0450 -- `spells.2da TargetType` & the clicked object's type (self / creature / ground /
item / door / placeable / trigger) -- and `DisableEntry` the leaf when it fails, so a spell that
cannot target what was clicked shows greyed on that root exactly as it would in the vanilla
cast menu there. Not built: casting at the object's *position* when only the ground bit fits
(vanilla has no such fallback either; the *Ground* root carries the same pin). The popup
offers Creatures, Placeables, Doors and Ground: the item root's slot 1 lists the item's own
spells, and the quickbar and chat roots have no target. A row whose subtree does not exist on
the other root (a special ability shown on Placeables) greys there like any stale hoist.
**Show everywhere (as self)** (CX-694): an action the self menu fires is a self action -- its
target is always the local creature -- so it may sit on every root. The popup offers it on a leaf
whose handler is one of a fixed table (`SelfActions`): `CGuiCreatureMenu::RadialMenuUseFeat` /
`Emote` / `ToggleMode` / `QuickChat` / `UseSkill` / `Rest` / `CharacterSheet` and
`CClientSpellMenu::RadialMenuCast` / `CastSpellLikeAbility` / `UseFeat`, each with its Windows
anchor. Picking it writes one row per world root (Creatures -- every other creature's menu too --,
Placeables, Doors, Items, Ground, Traps) with `fn=self:<key>`, `param`, `label`, `icon` (there
is no live leaf to read one from); a walked hoist already at that path turns into the self pin
where it sits. It is offered on a top-level self leaf too (*Character sheet*), whose one-step
path no walked copy could take. The row is fired on a **stand-in** of
the self menu: the engine's own `CGuiCreatureMenu(CNWCCreature* self, Vector)` /
`CClientSpellMenu(Vector, self id, null, build=0)` ctor on `operator new` memory, ring-less,
`m_bAutoDelete` set, torn down with the held root -- so the handler behaves exactly as on the
self menu (a feat that needs a target opens the targeting cursor there too). A spell-class row is
greyed when `TargetIsValid` refuses the spell at self, which is the self menu's own greying of a
hostile-only spell. No count column (uses left are not read). `ponytail:` the table is the
clonability rule; a self-menu leaf outside it (associate commands, party actions) is not offered.
Validation masks bit 37 of the param on both sides: `CreateSpellSubRadial` sets it only while a
spontaneous caster has no uses of the spell left and `CGuiQuickButton::SetButtonSpellMenuID`
@0x6aac50 clears it as transient.

Editing (CX-694): left-drag a row to a gap of its own list to reorder it; drag a deeper row to
a gap of the top list to hoist it there (a Wizard drops *Fireball* onto the top list instead of
walking *Cast Spell > Wizard > 3* each time). A leaf activates on the release of an undragged
press. The right-click popup keeps the same edits as rows (Move to top / Pin to top, Unpin,
Move up, Move down, Hide, Show hidden, Reset layout) for keyboard-free precision, and adds
*Move to ▸* (the root's folders, *Top level* from inside one, *New folder…*) on every row and
*Also show on ▸* on any hoist or deeper row (a top-level slot only as self); a folder row offers
*Rename…*, Move up / down, *Remove folder*. A folder's open list takes drops of any row -- a hoist,
a top-level slot, a deeper vanilla row -- and gives them back to the top list. Naming is typed into the
popup row itself (`ponytail:` ASCII off the SDL keycodes the cascade already swallows; Enter
commits, Esc cancels -- `SDL_TEXTINPUT` is the upgrade when a non-ASCII name matters).

The packed spell param carries the class *slot*, so on a character where the path no longer
yields a leaf with the same `(fn, param)` (or a step's creator declines) the row shows
greyed -- its leaf label, the parents' breadcrumb in the count column -- and is never invoked.
Positions: a row claims `at` (first free index at or after it), the unclaimed vanilla rows
flow in slot order; no rows is vanilla order, *Reset layout* drops the root kind's rows.

**Dialog** — group `dialog`. The title-bar gear and `DialogSettings` are deleted.

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| dialog.enabled | (none) | Enable the CX dialog window | T | on | U | L | Conversations in CX's own window: resizable, scrollable, portrait, animated. Off, the stock dialog panel runs. |
| dialog.lock | locked | Lock position and size | T | off | U | L | The window can't be dragged or resized. |
| dialog.maxheight | maxh | Height before scrolling | S | 120–800 px step 10 · 320 | U | L | The window grows with the text up to this height, then scrolls. The resize grip sets it too. |
| dialog.justify | justify | Justify text | T | off | U | L | Stretch lines to the window width. Off is ragged right. |
| dialog.anim | anim | Animations | T | on | U | L | Text fades in and out between nodes. Off, it snaps. |
| dialog.fadein | fadein | Fade in | S | 0–500 ms step 10 · 180 | A | L | |
| dialog.fadeout | fadeout | Fade out | S | 0–500 ms step 10 · 140 | A | L | |
| x, y, w (+ Placed sentinel) | same | — | window state | | H | | |

**Nameplates** — group `nameplate`.

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| nameplate.enabled | on | Enable CX nameplates and chat bubbles | T | on | U | L | Names, health colour and speech bubbles drawn by CX: sharper text, portraits, stacked lines. Off, the stock bubbles draw. |
| nameplate.portraits | portraits | Portraits | T | on | U | L | A portrait beside the name. |
| nameplate.background | background | Background | T | on | U | L | A box behind the plate, as in the stock game. |
| nameplate.outline | outline | Text outline | T | on | U | L | A dark edge around the letters so they read over bright ground. |
| nameplate.healthband | healthband | Health colour | T | on | U | L | The name is tinted by the creature's health, as in the stock game. |
| nameplate.healthbar | healthbar | Health bar | T | off | U | L | A thin bar under the name. Not in the stock game. |
| nameplate.maxdist | maxdist | Visible distance | S | 0–100 m step 5 · 45 | U | L | Plates past this distance are not drawn. 0 draws every plate. |
| nameplate.queue | bubblequeue | Bubble lines kept | D | 1 / 2 / 3 / 4 · 2 | U | L | How many recent things someone said stack in their bubble. The stock game keeps 1. |
| nameplate.width | bubblewidth | Text width | S | 120–480 px step 10 · 300 | U | L | The widest a bubble or a name gets before wrapping. |
| nameplate.channelstyle | channelstyle | Colour by channel | T | on | A | L | Shouts, whispers and emotes get their own colour and emphasis. |
| nameplate.clicktotell | clicktotell | Click a name to tell | T | off | A | L | Clicking the name starts a tell. Takes the click the world would have had. |
| nameplate.fadedist | fadedist | Fade band | S | 0–100 m step 1 · 10 | A | L | Plates fade out over this distance before Visible distance. |
| nameplate.lines | bubblelines | Wrapped lines per bubble | S | 1–32 · 32 | A | L | |
| nameplate.anim | animms | Animation | S | 0–1000 ms step 10 · 180 | A | L | 0 = plates pop in. |
| nameplate.probe | probe | — | B | | C | — | anchor dump on the next frame |

Deleted: `mode`, the `settings` subcommand, `NameplateSettings`, `/np`.

**Combat text** — group `combattext`.

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| combattext.enabled | on | Enable CX combat text | T | on | U | L | Damage numbers and combat banners drawn by CX: stacked per target, outlined, animated. Off, the stock floating text draws. |
| combattext.outline | outline | Text outline | T | on | U | L | A dark edge so numbers read over bright ground. |
| combattext.max | max | Numbers per target | S | 1–16 · 6 | U | L | Damage numbers shown on one creature at once; older ones are dropped. |
| combattext.life | risems | Number lifetime | S | 200–6000 ms step 100 · 1200 | U | L | How long a damage number stays. |
| combattext.bannerlife | textms | Banner lifetime | S | 200–6000 ms step 100 · 1500 | U | L | How long a banner (*Sneak Attack!*) stays. |
| combattext.bannersize | textscale | Banner size | S | 1–4 × step 0.1 · 1.3 | U | L | Banner text relative to a damage number. |
| combattext.rise | risepx | Rise distance | S | 0–400 px step 4 · 52 | A | L | How far a number floats up over its life. |
| combattext.stack | stackpx | Stack spacing | S | 0–120 px step 2 · 20 | A | L | |
| combattext.lift | bannerliftpx | Banner lift | S | 0–300 px step 2 · 34 | A | L | Banners start this far above the damage column. |

`/ct` dies.

**Quickbar & compass** — group `quickbar`.

| key | today | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|---|
| quickbar.enabled | (none) | Enable the CX quickbar layout | T | on | U | L | Quickbar | Centres the quickbar correctly and lets it sit left or right with background bars. Off, the stock quickbar position is used. |
| quickbar.align | align | Position | D | left / centre / right · centre | U | L | Quickbar | |
| quickbar.bars | bars | Black bars | T | on | U | L | Quickbar | Full-width bars behind the quickbar and under the chat. |
| quickbar.hidden | hidden | Hide the quickbar | T | off | U | L | Quickbar | Moves the quickbar off-screen. |
| quickbar.compass | compass (inverted) | Show the compass | T | on | U | L | Compass | |

Off keeps *replacing* `Reposition` with the stock centre formula (no `GUI_QBAR_ENDS`, live
both ways); forwarding was rejected because ENDS attaches and can't detach until relog.
Deleted: Quickbar's `/settings`, `/qbar`, `QuickbarSettings`.

**FPS overlay** — group `fps`.

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| fps.enabled | /fps (session-only) | Show FPS and ping | T | off | U | L | Frame rate, frame time and server round-trip in the top-right corner. |
| fps.font | /fps font | — | X | default | C | L | a `font.<name>` typeface; Chat-only until multi-font |

Now persists (amends #182).

**Windows & fonts** — group `ui`.

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| ui.font | font (cx-chat.cfg; dialog's slider edited the same) | Text size | D | 6 … 32 pt, every point · 18 | U | L | Size of the text in every CX window: chat, dialog, nameplates. A chat window may set its own on its gear (CX-683). |
| ui.typeface | — | Typeface | D | `default` + every `font.<name>` + every installed font (full name) · default | U | L | The font every CX window draws with: the built-in DejaVu Sans, a font.<name> from cx.cfg, or one installed on this machine. Type in the list to search. (CX-681: rebinds the registry's `default`; icons stay DejaVu, #425) |
| ui.hoverfocus | hoverToFocus (cx-ui.cfg) | Focus a window by hovering | T | off | U | L | A CX window takes the keyboard when the pointer rests on it, without a click. |
| ui.snap | grid (cx-quickbar.cfg) | Snap windows to a grid | T | off | U | L | Dragged windows land on grid points. |
| ui.gridpx | gridpx | Grid size | D | 25 / 50 / 75 / 100 / 150 / 200 px · 50 | U | L | |
| ui.edgesnap | edgesnap | Snap to screen edges | T | on | U | L | Dragged windows stick to the screen edge and to each other. |

**Units:** "pt" = 1 design px before GUI scale, no DPI maths; the ladder is 6–32 pt everywhere
(`TextRig.MinFontPx` 10 → 6; the atlas ceiling is 96 px effective). Box sizes and distances
stay design px.

### 4.3 Graphics

Every Graphics group has `parent: alloy.enabled` (rows grey when Alloy is off) except
Renderer itself.

**Renderer** — group `alloy`. Status line at the top: "Running: Alloy / Vanilla — <AlloyGate.Reason>"; bare `/alloy` prints it.

| key | today | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|---|
| alloy.enabled | vanilla (inverted) | Enable the Alloy renderer | T | on | U | R | — | Draw the 3D world with CX's own OpenGL 4.6 renderer: real-time shadows, HDR, grass, post-processing. Off, the game's stock renderer draws everything and every Graphics submenu below is inactive. Takes effect at the next launch. |
| alloy.scale | renderscale + /post scale | Render scale | S | 0.25–2.00 step 0.05 · 1.00 | U | L | — | Size of the 3D frame relative to your screen. Below 1 renders fewer pixels and upscales (faster); above 1 supersamples (sharper, slower). The UI is never scaled. |
| alloy.highlight | outlinehl (quickbar) | Selection highlight | D | glow / outline · glow | U | L | Geometry | How a selected or hovered object is marked: the stock glow, or an outline with a light fill. |
| alloy.weld | /draw weld | Smooth-shading crease angle | S | 0–180 ° step 5 · 60 | A | L | Geometry | Faces meeting at a sharper angle than this keep a hard edge. 0 keeps every model's own normals. Applies to models loaded after the change. |
| alloy.lod | /draw lod | Cull models smaller than | S | 0–8 px step 0.5 · 1.0 | A | L | Geometry | Distant models whose projected size is under this many pixels are skipped. |
| alloy.tiledissolve | /draw gobkey | Dissolve tile props hiding the camera | T | on | A | L | Geometry | Off = only walls dissolve, as in the stock game. |
| alloy.border | border | Neighbouring-area ring | S | 5–40 tiles step 5 · 15 | A | R | Geometry | Tiles drawn past the area edge so the horizon is not empty; the stock game draws 5. |
| alloy.oit | /draw oit | Transparency | D | sorted / weighted / linked lists · linked lists | A | L | Transparency | How overlapping transparent surfaces are combined. |
| alloy.oitlayers | oitlayers | Transparency layers per pixel | D | 1 / 2 / 3 / 4 / 6 / 8 · 3 | A | R | Transparency | Memory reserved for linked-list transparency; ~33 MB per layer at 1080p. |
| alloy.texcache | texcache_mb | Texture memory budget | D | auto / 512 / 1024 / 2048 / 4096 / 8192 MB · auto | A | R | Memory | |
| alloy.bindless | bindless | Bindless textures | D | auto / on / off · auto | A | R | Memory | Auto turns them off on Zink. |
| alloy.bcn | bcn | Compress textures on load | T | on | A | R | Memory | Re-encodes uncompressed textures as BC1/BC3: 3–4× less VRAM, a little load-time CPU. |
| alloy.hdr | hdr | — | T | on | C | R | | bisect: 0 = engine draws from Alloy-owned resources |
| alloy.gl46 | gl46 | — | T | on | C | R | | |
| alloy.drawhooks | drawhooks | — | D | batch / stats · batch | C | R | | stats = DrawStatsService histogram |
| alloy.ownedtex / pltcpu / bcndrip / texprefetch / texsingle / texdelete / handledrop / ownedgeom / maxelements / spirv / vanillabridge / hostfs / ownskin / enrollbudget / enrolldelta / clustercap / gradedblend / dumptex / skiptex / dumpshaders / gldebug / swapprobe / glsync / skipbuckets / enginezones | same | — | as today | as today | C | R | | capability gates + bisects |
| alloy.light / prepass / viz / threads / aotest / cull / retest / hldiag / hlchan / hlglow / poll / prepasskey / prepassfeat / idtarget / blenddepth / gpumeter / vislist / washtest / oitviz | /draw … | — | as today | as today | C | L | | |
| alloy.consts, alloy.framedump, alloy.shaderdump | /draw consts, /framedump, /dumpshaders | — | B | | C | — | | |

**Lighting** — group `light`, no master (header: lighting is the lit stage; off is Renderer off).

| key | today | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|---|
| light.gain | gain | Point-light brightness | S | 0–4 step 0.05 · 1.00 | U | L | Lights | Scales every placed and carried light. 1 is the stock brightness. |
| light.ambient | ambient | Ambient light | S | 0–4 step 0.05 · 1.00 | U | L | Lights | Scales the area's ambient fill. 1 is the area author's value. |
| light.sun | sun | Sun and moon | S | 0–4 step 0.05 · 1.00 | U | L | Lights | Scales the area's directional light. 1 is the area author's value. |
| light.overbright | overbright | Carried-light boost | S | 0–1 step 0.05 · 0 | U | L | Lights | How much of a torch's extra intensity shows at its centre instead of only as reach. 0 is the stock game; 0.5 makes carried lights stand out. |
| light.flicker | — | Fire flicker | S | 0–2 step 0.05 · 1.00 | U | L | Lights | Animated lights (torches, braziers) keep a steady reach and flicker in brightness instead of pulsing in size. 1 flickers as deep as the pulse was made; 0 is the stock pulse. |
| light.flickerspeed | — | Flicker speed | S | 0.1–4 step 0.05 · 1.00 | A | L | Lights | Rate of the flicker; 1 is a fireplace. |
| light.sss | sss | Soft skin lighting | S | 0–1 step 0.05 · 0.25 | U | L | Lights | Light wrapping past the shadow line on characters. 0 is flat; the stock game uses 0.38 at shader quality above medium. |
| light.wrap | wrap | Sphere-light wrap | S | 0–4 · 1 | A | L | Lights | Near lights soften the shadow line on low-poly models; 0 = point lights. |
| light.wrapndl | wrapndl | Diffuse wrap | S | 0–2 · 1 | A | L | Lights | Distance-independent softening of every terminator. |
| light.hot | hot | Near-field core | S | 0–2 · 0 | A | L | Lights | Adds intensity at a light's centre only. |
| light.hotsize | hotsize | Core size | S | 0–1 · 0.15 | A | L | Lights | Fraction of the range the core covers. |
| light.radius | radius | Source radius | S | 0–4 · 1 | A | L | Lights | Wrap radius and self-shadow skip base. |
| light.falloff | falloff | Rim fade exponent | S | 0.1–4 · 1 | A | L | Lights | 1 = stock falloff exactly. |
| light.fadeout | fadeout | Departed-light fade | S | 0–120 frames · SceneInputs.FadeFrames | A | L | Lights | Frames a light takes to fade after it leaves. |
| light.probe | probe + /probe on | Reflections from the scene | T | on | U | L | Reflections | Materials with an environment map reflect a capture of the surroundings instead of the stock sphere map. |
| light.probegain | /probe gain | Reflection strength | S | 0–4 · 1 | A | L | Reflections | 1 is the stock weighting. |
| light.probeareawide | /probe areawide | Reflect on every surface | T | off | A | L | Reflections | On, every material reflects the probe, not just those with an env map. |
| light.probesize | probe_size | Reflection capture size | D | 64 / 128 / 256 / 512 px · 128 | A | R | Reflections | |
| light.hidden | — | Hidden lights do not reflect | T | on | U | L | Reflections | A wet or glossy surface mirrors a light only where that light's shadow lets it -- a lamp on a terrace reflects in the street from where the ground sees over the parapet, not from the wall's foot. |
| light.brdf | — | Modern materials | T | on | U | L | Materials | Energy-conserving highlights, reflections that brighten at grazing angles and fade in crevices, and the extra material layers (metal, clear coat, sheen, glow) a .mtr can declare for Alloy. Off is the stock shading exactly. (ADR 0026) |
| light.ambientdir | — | Directional ambient | S | 0–1 step 0.05 · 0.5 | U | L | Materials | Ambient light takes its direction from the surroundings (sky above, ground below) at the area's own brightness. 0 is the stock sun-facing shading. Needs Modern materials and Reflections from the scene. |
| light.specaa | — | Smooth highlights | T | on | A | L | Materials | Widens a highlight that is narrower than a pixel, so glossy and normal-mapped surfaces do not sparkle or crawl in motion. Needs Modern materials. |
| light.binreach / bincap / reachfade | same | — | S | as today | C | L | | cluster-build experiments |

**Shadows** — group `shadow`.

| key | today | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|---|
| shadow.enabled | shadows | Enable Alloy shadows | T | on | U | R | — | Real-time sun cascades and point-light shadow cubes, replacing the stock stencil shadows. Off, the stock shadows draw. Takes effect at the next launch. |
| shadow.dist | /shadow dist | Sun shadow distance | S | 20–400 m step 10 · 120 | U | L | Sun | How far from you the sun casts shadows. Cost does not depend on area size. |
| shadow.cascades | csm_cascades | Sun shadow cascades | D | 1 / 2 / 3 / 4 · 4 | U | R | Sun | More cascades = sharper shadows near you at the same distance. |
| shadow.size | csm_size | Sun shadow resolution | D | 512 / 1024 / 2048 / 4096 · 2048 | U | R | Sun | Per-cascade map size. |
| shadow.cubes | cube_count | Shadow-casting lights | S | 0–64 step 4 · 32 | U | R | Point lights | How many nearby lights cast shadows at once. Lower on a small card. |
| shadow.cubesize | cube_size | Point-light shadow resolution | D | 128 / 256 / 512 / 1024 / 2048 · 512 | U | R | Point lights | |
| shadow.cubedist | — | Point-light shadow distance | S | 0–200 step 5 · 40 m | U | L | Point lights | A light farther than this from you casts no shadow, and no fragment farther than this from the camera reads one; shadows fade out over the last quarter. 0 = every light in the budget, at any distance. |
| shadow.soft | /light shadowsoft | Shadow softness | S | 0–4 step 0.25 · 1.5 | U | L | Point lights | Blur radius of point-light shadows. 0 is hard-edged and cheapest. |
| shadow.dynsize | cube_dyn_size | Moving-caster resolution | D | 64 / 128 / 256 / 512 · 256 | A | R | Point lights | |
| shadow.dyncount | cube_dyn_count | Moving-caster pool | S | 0–64 · 16 | A | R | Point lights | 0 = no separate pool. |
| shadow.sundyn | csm_dyn | Separate moving-caster sun layer | T | on | A | R | Sun | |
| shadow.discpx | /light discpx | Softness cutoff | S | 0–16 px · 3 | A | L | Point lights | Below this screen radius the blur stands down. |
| shadow.cap | /light shadowcap | Shadow taps per pixel | S | 0–2 · 2 | A | L | Point lights | 0 = every light. |
| shadow.near | /light shadownear | Self-shadow skip | S | 0–4 · 1 | A | L | Point lights | Carried lights don't shadow their bearer inside this radius. |
| shadow.csmbias | /draw csmbias | Sun depth bias | S | 0–0.5 · 0.06 | A | L | Sun | |
| shadow.fade | /shadow fade | Shadow fade-in | S | 0–120 frames · 60 | A | L | Point lights | 0 = pop. |
| shadow.fadeslots | /shadow fadeslots | Fade reserve | S | 0–16 · 4 | A | L | Point lights | |
| shadow.mask | /shadow mask | Cut-out shadows | D | skip / solid / alpha-tested / auto · auto | A | L | Quality | Auto = alpha-tested where the texture is mostly holes, solid elsewhere. |
| shadow.maskholes | /shadow maskholes | Auto threshold | S | 0–1 · 0.65 | A | L | Quality | |
| shadow.maskgraded | /shadow maskgraded | Glass threshold | S | 0–1 · 0.15 | A | L | Quality | |
| shadow.maskdither | /shadow maskdither | Translucent shadow dither | S | 0–16 · 1 | A | L | Quality | |
| shadow.focusdist | /shadow focusdist | Cascade look-ahead | S | 0–100 m · 24 | A | L | Sun | |
| shadow.staticcap | /shadow staticcap | Static re-bakes per frame | S | 0–16 · 3 | A | L | Performance | 0 = unbounded (hitches). |
| shadow.dynhold | /shadow dynhold | Mover hold | S | 0–120 frames · 30 | A | L | Performance | |
| shadow.dynfull | /shadow dynfull | Full-rate cube ranks | S | 0–64 · 8 | A | L | Performance | |
| shadow.sunfull | /shadow sunfull | Full-rate sun cascades | S | 0–4 · 2 | A | L | Performance | |
| shadow.sunlatch | /shadow sunlatch | Cascade re-latches per frame | S | 0–4 · 1 | A | L | Performance | |
| shadow.dynranks | /shadow dynranks | Mover pages | S | 0–64 · 64 | A | L | Performance | |
| shadow.mode / casters / stage / lightcull / gather / receiver / rebake / split / layered / refreshall / dynevents / itemdirty / flipcollect / maskh / diag / lightdiag / torchdiag / trace | noshadows, casters, shadowstage, /shadow … | — | as today | as today | C | mode/casters/stage R, rest L | | bisects |
| shadow.movers | /shadow movers | — | B | | C | — | | |

**Grass** — group `grass`.

| key | today | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|---|
| grass.enabled | /grass on | Enable Alloy grass | T | on | U | L | — | CX's own grass: denser, wind-swayed, lit and shadowed. Off, the stock grass draws. |
| grass.density | density | Density | S | 0–4 step 0.25 · 2 | U | L | Field | Blades per patch relative to the stock game. |
| grass.far | far | Distance | S | 0–400 m step 10 · 0 | U | L | Field | How far grass draws. 0 follows the game's Grass Distance option (which stops at 173). |
| grass.wind | wind | Wind strength | S | 0–4 step 0.1 · 1 | U | L | Wind | |
| grass.shadow | shadow | Grass casts shadows | D | none / 1 / 2 / 3 / 4 cascades · 1 | U | L | Shadows | How many sun cascades the blades are drawn into. More is sharper far away and costlier. |
| grass.push | push | Parts around characters | S | 0–1 step 0.05 · 0.15 | U | L | Field | Blades bend away from creatures walking through them. 0 is off. |
| grass.windspeed | windspeed | Wind speed | S | 0–0.01 · 0.001 | A | L | Wind | |
| grass.lod | lod | Full-density radius | S | 5–200 m · 40 | A | L | Distance | Every blade draws inside this. |
| grass.lodmin | lodmin | Far density floor | S | 0.001–1 · 0.02 | A | L | Distance | |
| grass.lodwidth | lodwidth | Far blade widening | S | 0–1 · 0.5 | A | L | Distance | |
| grass.lodheight | lodheight | Far blade growth | S | 0–1 · 0.25 | A | L | Distance | |
| grass.shade | shade | Full-shading radius | S | 5–200 m · 40 | A | L | Distance | Past this blades skip light and shadow taps. |
| grass.castwidth | castwidth | Shadow blade width | S | 0.5–8 · 1 | A | L | Shadows | |
| grass.pushradius | pushradius | Push radius | S | 0–4 · 1 | A | L | Field | |
| grass.pushextent | pushextent | Push field extent | S | 8–128 m · 64 | A | L | Field | |
| grass.cull | cull | Clear grass under objects | T | on | A | L | Field | |
| grass.cullshrink | cullshrink | Object footprint scale | S | 0–2 · 0.8 | A | L | Field | |
| grass.cullmax | cullmax | Footprint cap | S | 0–20 m · 0 (none) | A | L | Field | |
| grass.tuft | tuft | Blades per tuft | S | 1–8 · 3 | A | L | Field | |
| grass.soften | soften | Texture softening | S | 0–2 · 0.5 | A | L | Look | Mip bias on the blade texture. |
| grass.aniso | aniso | Anisotropic filtering | D | 1 / 2 / 4 / 8 / 16 · 8 | A | L | Look | |
| grass.edge | edge | Edge ramp width | S | 0–4 · 2 | A | L | Look | |
| grass.softedge | softedge | Blended silhouettes | T | on | A | L | Look | |
| grass.rootao | rootao | Root darkening | S | 0–1 · 0 | A | L | Look | |
| grass.gtint | gtint | Ground tint | S | 0–1 · 0.15 | A | L | Look | Blade fades into the ground colour under its root. |
| grass.vlight | vlight | Per-vertex lighting | T | on | A | L | Look | Off = per-pixel (costlier). |
| grass.border | bordergrass | Grass past the area edge | T | on | A | R | Field | |
| grass.bordertex | bordergrasstex | Border grass textures | X | "grass" | A | R | Field | Comma-separated texture names. |
| grass.prepass / pushvis / gviz / dumpmips / dumpshadow | same | — | as today | | C | L | | |

**Water** — group `water`.

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| water.enabled | /water on | Enable Alloy water | T | on | U | L | Water drawn through CX's pipeline (the stock water shader, reflections from the finished frame). Off, the stock water draws. |
| water.rect / diag | same | — | T / S | | C | L | |

**Fog** — group `fog`. Master `fog.enabled` "Enable Alloy fog", live: off = the area author's flat fog colour (every dial reads as 0).

| key | today | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|---|
| fog.enabled | new | Enable Alloy fog | T | on | U | L | — | Fog that takes the sky's colour, glows toward the sun and thins with height. Off, the area author's flat fog colour draws. |
| fog.sky | sky | Fog takes the sky colour | S | 0–1 step 0.05 · 0.75 | U | L | Colour | How far the fog colour moves from the area's flat colour toward the sky. 0 is the stock look. |
| fog.sun | sun | Sun glow in fog | S | 0–2 step 0.05 · 0.35 | U | L | Colour | Fog brightens toward the sun. |
| fog.sunexp | sunexp | Sun glow tightness | S | 1–64 · 8 | A | L | Colour | Big = tight glow, small = half-sky wash. |
| fog.skyband | skyband | Horizon band | S | 0–1 · 0.15 | A | L | Colour | How far above the horizon the sky dome is fogged. |
| fog.height | height | Height falloff | S | 0–1 · 0 | A | L | Density | Fog thins with height above the floor; 0 = off. |
| fog.floor | floor | Layer floor | S | 0–50 m · 0 | A | L | Density | Offset above your feet where thinning starts. |
| fog.origin | origin | Measure from the player | T | on | A | L | Density | Off = stock view-depth fog (zooming out pushes the fog). |
| fog.cull | /draw fogcull | Cull fully-fogged geometry | T | on | A | L | Performance | |
| fog.cullz | cullz | — | S | · 30 | C | L | | |

**Post-processing** — group `post`, no master (the resolve is the frame).

| key | today | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|---|
| post.exposure | exposure | Exposure | S | 0.25–4 step 0.05 · 1 | U | L | Exposure | Overall brightness before tone mapping. 1 is neutral. |
| post.auto | auto | Auto exposure | T | on | U | L | Exposure | The image adapts like an eye: brighter in the dark, dimmer in the light. |
| post.bloom | bloom | Bloom | S | 0–0.5 step 0.01 · 0.06 | U | L | Bloom | Glow around bright lights. 0 is off. |
| post.tonemap | tonemap | Tone mapping | D | off / shoulder / aces · aces | U | L | Tone | How bright values above white are compressed. |
| post.taa | taa | Temporal anti-aliasing | T | on | U | L | Anti-aliasing | Smooths edges over several frames. |
| post.automin | automin | Auto exposure minimum | S | 0.01–1 · 0.5 | A | L | Exposure | |
| post.automax | automax | Auto exposure maximum | S | 1–8 · 2 | A | L | Exposure | |
| post.autospeed | autospeed | Adapt to dark speed | S | 0.1–10 /s · 1 | A | L | Exposure | |
| post.autodown | autodown | Adapt to light speed | S | 0.1–10 /s · 3 | A | L | Exposure | |
| post.threshold | threshold | Bloom threshold | S | 0–4 · 1 | A | L | Bloom | |
| post.knee | knee | Bloom knee | S | 0–1 · 0.5 | A | L | Bloom | |
| post.acesmix | acesmix | ACES mix | S | 0–1 · 0.5 | A | L | Tone | |
| post.tmknee | tmknee | Shoulder knee | S | 0–1 · 0.8 | A | L | Tone | |
| post.dither | dither | Output dither | S | 0–2 · 1 | A | L | Tone | |
| post.taablend | taablend | TAA history weight | S | 0.02–0.5 · 0.1 | A | L | Anti-aliasing | |
| post.taau | taau | Temporal upsampling | T | on | A | L | Anti-aliasing | With render scale below 1. |
| post.taasharp | taasharp | TAA sharpness | S | 0–1 · 0.3 | A | L | Anti-aliasing | Undoes the softness the history blend leaves. 0 = the raw resolve. |
| post.halfres | halfres | Half-resolution | T | off | U | L | Screen-space effects | Ambient occlusion is computed at a quarter of the pixels and upsampled. Needs temporal anti-aliasing to look clean. Off: the lit stage's 4-tap upsample costs more than the pass saves on a fragment-bound frame. |
| post.stack | stack | Game post-effects | T | on | A | L | Stock effects | The stock framebuffer effects (gamma, SMAA, toon…) on the Alloy frame. |
| post.fbpost | fbpost | Hosted content post | T | on | A | L | Stock effects | |
| post.toonbands | toonbands | Toon bands | S | 1–32 · 1 | A | L | Stock effects | With the game's Toon option; 1 = no posterise. |
| post.toonink | toonink | Toon ink | S | 0–1 · 1 | A | L | Stock effects | |
| post.toonedge | toonedge | Toon edge | S | 1–8 · 1 | A | L | Stock effects | |
| post.motion / viz / clearviz | same | — | | | C | L | | |

Deleted: `/post scale` (= alloy.scale), `/post aotemporal` (= ao.temporal).

**Ambient occlusion** — group `ao`, no master (header: the game's own Ambient Occlusion option is the switch).

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| ao.radius | radius | Radius | S | 0.1–2 m step 0.05 · 0.6 | U | L | How far occlusion reaches: 0.6 darkens corners and feet; larger is a room-wide gloom. |
| ao.strength | power | Strength | S | 0.5–4 step 0.1 · 2.2 | U | L | Multiplied by the game's AO intensity slider. |
| ao.direct | direct | Darken lit surfaces | S | 0–1 · 1 | A | L | 0 = ambient only (invisible indoors). |
| ao.temporal | temporal | Temporal accumulation | S | 0–0.98 · 0.8 | A | L | 0 = single frame. |
| ao.dirs | dirs | Slices | S | 1–8 · 1 | A | L | |
| ao.steps | steps | Steps | S | 1–8 · 3 | A | L | |
| ao.maxpx | maxpx | Screen radius cap | S | 16–256 px · 96 | A | L | |
| ao.blur | blur | Blur | T | on | A | L | |
| ao.on | on | — | T | | C | L | bypass |

**Effects** — group `effects` (sky, beams, halos).

| key | today | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|---|
| effects.sky | /sky | Alloy sky | T | on | U | L | Sky | The sky dome drawn through CX (fogged horizon, HDR). Off, the stock sky draws. |
| effects.beams | /beams | Alloy light beams | T | on | U | L | Lights | Light shafts drawn through CX. |
| effects.halos | halos + /halos on | Light halos | T | on | U | L | Lights | A soft glow around every light source. |
| effects.halosize | halosize + /halos size | Halo size | S | 0–2 step 0.1 · 0.6 | U | L | Lights | |
| effects.halogain | halogain + /halos gain | Halo brightness | S | 0–4 step 0.1 · 1 | U | L | Lights | |
| effects.haloviz | /halos viz | — | S | | C | L | | |

**Particles** — group `particles`.

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| particles.enabled | on | Enable Alloy particles | T | on | U | L | Spell effects, smoke and fire drawn through CX: soft edges and lighting. Off, the stock particles draw. |
| particles.soft | soft | Soft edges | T | on | U | L | Particles fade where they meet geometry instead of cutting through it. |
| particles.lit | lit | Lit particles | T | on | U | L | Smoke and dust take the scene's light. |
| particles.shadows | shadows | Shadowed particles | T | on | A | L | Lit particles pay the point-light shadow taps. |
| particles.sort | sort | GPU sort | T | on | A | L | |
| particles.grid / guard / batch / sampler / diag / viz | same, partbatch, partsamp | — | | | C | grid,guard,diag,viz L; batch,sampler R | |

**Dangly** — group `dangly`.

| key | today | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|---|
| dangly.enabled | on | Enable Alloy dangly meshes | T | on | U | L | — | Cloaks, hair, banners and other swaying parts move with gravity and wind instead of the stock spring. Off, the stock sway runs. |
| dangly.gravity | gravity | Gravity | S | 0–20 step 0.5 · 3 | U | L | Forces | How much cloth sags. Low by default: rest poses already hang. |
| dangly.wind | wind | Wind response | S | 0–40 step 1 · 14 | U | L | Forces | |
| dangly.breeze | breeze | Base breeze | S | 0–2 step 0.05 · 0.35 | U | L | Forces | Wind when the area sets none. 0 = only what the game reports. |
| dangly.turb | turb | Gustiness | S | 0–1 · 0.7 | A | L | Forces | |
| dangly.turbfreq | turbfreq | Gust size | S | 0–4 · 0.9 | A | L | Forces | |
| dangly.turbspeed | turbspeed | Gust speed | S | 0–8 · 2.6 | A | L | Forces | |
| dangly.vary | vary | Per-object variation | S | 0–1 · 0.15 | A | L | Forces | |
| dangly.body | body | Body collision | S | 0–1 · 0.95 | A | L | Collision | 0 = off. |
| dangly.cage | cage | Freedom of movement | S | 0.5–8 · 3.5 | A | L | Collision | ×stock cage; past ~4 cloaks reach the legs. |
| dangly.hz / steps / still | same | — | S | | C | L | | |

### 4.4 Camera

**Chase / Top-down / Driving** — groups `chase` / `topdown` / `driving` (the game calls mode 2 Driving; nwscript calls it STIFF_CHASE_CAMERA). Identical rows,
per-mode values, each with its own master: off = the stock camera for that mode entirely
(feel, camera-relative movement, recenter all forwarded).

| key | today | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|---|
| `<mode>.enabled` | (none) | Enable the CX camera for this mode | T | on | U | L | — | CX shapes this camera mode: smoothing, field of view, offset, camera-relative movement, auto-recenter. Off, the stock camera runs untouched in this mode. |
| `<mode>.fov` | fov | Field of view | S | 10–120 ° step 1 · 45 | U | L | View | Vertical field of view. The game's own is 45. |
| `<mode>.lagpos` | lagpos | Position smoothing | S | 0–2 s step 0.05 · 0 | U | L | Smoothing | How long the camera takes to catch up with your character. 0 is welded, as in the stock game. |
| `<mode>.lagrot` | lagrot | Rotation smoothing | S | 0–2 s step 0.05 · 0 | U | L | Smoothing | How long a turn takes to settle. 0 is welded. |
| `<mode>.lagzoom` | lagzoom | Zoom smoothing | S | 0–2 s step 0.01 · 0.02 | U | L | Smoothing | Smooths the mouse wheel's stepped zoom. 0 is the stock game's steps. |
| `<mode>.pitchcurve` | pitchcurve | Tilt down when zoomed out | S | −10–10 °/m step 0.5 · 0 | U | L | View | Degrees of extra tilt per metre of zoom past the reference distance. Negative lowers the angle instead. 0 leaves the tilt where you put it. |
| `<mode>.pitchref` | pitchref | Tilt reference distance | S | 1–60 m step 1 · 10 | A | L | View | |
| `<mode>.offsetx` | offsetx | Sideways offset | S | −10–10 m step 0.25 · 0 | U | L | Offset | Moves your character off centre; positive puts them on the left. 0 is the stock orbit. |
| `<mode>.offsety` | offsety | Height offset | S | −10–10 m step 0.25 · 0 | U | L | Offset | Raises the camera's focus above your character. |
| `<mode>.drive` | drive | Move relative to the camera | T | chase on · topdown off · driving on | U | L | Movement | Forward is where the camera looks, not where your character faces. |
| `<mode>.face` | (none) | Face where the camera looks | T | chase on · topdown off · driving off | U | L | Movement | Your character always faces the camera's direction. Forward runs that way, back walks backwards, the strafe keys sidestep, and the turn keys turn you and the camera together. After you look around with the mouse, your character turns to the new direction on the next step. Off: your character turns to face whichever way it moves, and the camera swings behind it. |
| `<mode>.siderun` | (none) | Turn keys run sideways | T | off | U | L | Movement | With Face where the camera looks on: the turn keys run you sideways instead of turning you and the camera (your character faces where it runs, the camera stays). The mouse is the way to look around; back turns you to the camera at once. Locked on a target this is always so, for the strafe keys too. |
| `<mode>.recenter` | recenter | Swing behind you as you move | T | chase on · topdown off · driving on | U | L | Movement | The camera turns to follow your movement direction instead of the stock chase. |
| `<mode>.rate` | move.rate | Recenter speed | S | 1–720 °/s step 5 · 360 | U | L | Movement | Fastest swing behind your movement; it eases in near the target. |
| `<mode>.deadzone` | move.deadzone | Recenter dead zone | S | 0–180 ° step 5 · 0 | U | L | Movement | How far off your movement direction the camera may sit before it starts to swing. |
| `<mode>.idle` | move.idle | Recenter after standing still | S | 0–60 s step 0.5 · 0 | U | L | Movement | Seconds of standing still before the camera drifts behind your character's facing. 0 never does. |
| `<mode>.grace` | move.grace | Orbit grace | S | 0–30 s step 0.1 · 1.5 | A | L | Movement | No recenter this long after you orbit by hand. |
| `<mode>.idlerate` | move.idlerate | Idle recenter speed | S | 1–720 °/s · 30 | A | L | Movement | |
| `<mode>.keyturn` | move.keyturn | Turn-key speed | S | 10–720 °/s step 10 · 180 | U | L | Movement | How fast the turn keys swing you and the camera when facing the camera. |
| `<mode>.hz` | move.hz | Heading updates per second | S | 1–60 step 1 · 10 | A | L | Movement | Most heading packets sent per second; the stock game sends 2. |
| `<mode>.turn` | move.turn | Heading turn rate | S | 0–3600 °/s step 30 · 540 | A | L | Movement | 0 = snap. |
| `<mode>.mouselook` | (none) | Enable CX mouse-look | T | on | U | L | Mouse-look | CX handles the mouse-drag camera: the cursor stays put and the view never stalls at the screen edge. Off, the stock middle-drag orbit runs. |
| `<mode>.action` | (none) | Action camera | T | off | U | L | Mouse-look | Hides the cursor and pins it to a crosshair in the centre of the screen. Moving the mouse turns the camera; whatever the crosshair points at is what you hover, tooltip and click, so aiming replaces pointing. Left-click acts on it, a right-click opens its menu. Clicking the ground does not walk you there (use the movement keys) and the crosshair never picks your own character. Alt frees the cursor for the game's panels (held, or a tap with the toggle option below); the toggle key switches the camera on and off. |
| `<mode>.actionkey` | (none) | Action camera toggle key | X | ] | U | L | Mouse-look | An SDL key name (F8, Tab, Backspace, Z...) or Mouse4 / Mouse5 for the thumb buttons. Blank binds nothing. |
| `<mode>.alttoggle` | (none) | Alt toggles the cursor | T | off | U | L | Mouse-look | Under the action camera, tapping Alt frees the cursor until you tap it again, instead of holding it. |
| `<mode>.assist` | (none) | Aim assist | T | on | U | L | Mouse-look | Under the action camera, the crosshair snaps its aim to the nearest creature, placeable or door within a few pixels of the centre, so you hover and click it without pixel-perfect aiming. The camera never moves; only what you point at changes. Brackets appear around the crosshair while a target is picked. |
| `<mode>.assistpx` | (none) | Aim assist radius | S | 0–200 px step 5 · 80 | A | L | Mouse-look | How far from the centre of the screen aim assist looks for a target. 0 turns it into the plain crosshair. |
| `<mode>.autotarget` | (none) | Cast at the crosshair | T | on | U | L | Mouse-look | Under the action camera, a spell, feat or item that asks you to pick a target fires straight away on whatever the crosshair is pointing at, with no second click. A ground-targeted spell still asks you to aim it. |
| `<mode>.feedback` | (none) | Action feedback on the crosshair | T | on | U | L | Mouse-look | Under the action camera, the crosshair answers what you did: a pulse when an order goes out, a red ring while you are attacking, and a dot underneath while an action is still waiting to happen. |
| `<mode>.headtrack` | (none) | Head follows the crosshair | T | on | U | L | Mouse-look | Under the action camera, your character turns their head toward whatever you are aiming at. The body still faces where it is walking. Only you see it -- other players see your character the way the game has always shown it. |
| `<mode>.headyaw` | (none) | Head turn limit | S | 0–89 ° step 1 · 70 | A | L | Mouse-look | How far the head turns to the side before it stops following the crosshair. 0 stops sideways turning. |
| `<mode>.headpitch` | (none) | Head tilt limit | S | 0–89 ° step 1 · 30 | A | L | Mouse-look | How far the head tilts up or down before it stops following the crosshair. 0 keeps it level. |
| `<mode>.right` | mouselook | Right-drag looks around | T | off | U | L | Mouse-look | Hold the right button and drag to orbit; a still right-click still opens the radial menu. |
| `<mode>.sens` | mouselook_sens | Sensitivity | S | 0.05–20 × step 0.05 · 1 | U | L | Mouse-look | 1 matches the stock middle-drag. |
| `<mode>.invert` | mouselook_invert | Invert vertical | T | off | U | L | Mouse-look | |
| `<mode>.threshold` | mouselook_threshold | Click-to-drag threshold | S | 0–100 px step 1 · 4 | A | L | Mouse-look | Pixels of movement before a right press becomes a look instead of a click. |

Build notes: the master is a ninth `Gate` value consulted at the pre-Control recenter and the
post-Control Adjust/SettleCollision; FOV `Camera+0x4c4` has no engine restorer, so off restores
`_fovFirst`, then `_welded = true` + `CameraCut()`. FOV's magic zero is gone: the layer writes
the slider whenever the mode's master is on.

**Walls** — group `walls`. Header: "The spring arm only acts while a sideways or height offset is set." The three fade rows grey when `alloy.enabled` is off (no strip; the spring arm is renderer-independent).

| key | today | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|---|
| walls.fade | camera.occlude | Fade objects that hide you | T | on | U | L | Fade | Placeables between the camera and your character turn see-through. |
| walls.floor | occlude floor | Faded opacity | S | 0–1 step 0.05 · 0.5 | U | L | Fade | How solid a faded object stays. 0 hides it entirely. |
| walls.radius | occlude radius | Fade ray width | S | 0–5 m step 0.1 · 0.5 | A | L | Fade | Half-width of the ray fan that finds occluders. |
| walls.margin | arm.margin | Arm margin | S | 0–5 m step 0.1 · 0.3 | A | L | Spring arm | How far short of a wall the offset camera stops. |
| walls.armin | arm.in | Arm pull-in rate | S | 0–60 /s · 0 | A | L | Spring arm | 0 = snap. |
| walls.armout | arm.out | Arm ease-out rate | S | 0–60 /s · 4 | A | L | Spring arm | |
| walls.armradius | arm.radius | Arm ray width | S | 0–2 m step 0.1 · 0.3 | A | L | Spring arm | 0 = one ray. |

`_occRate` (8) stays a const.

**Free camera** — group `free`, no master; the first row is an action.

| key | today | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|---|
| free.enter | /camera free | Enter the free camera | B | | U | — | — | Fly the camera freely: move keys fly, Space/C up and down, Z/X roll, Shift is fast, the wheel sets speed, right-drag looks. Escape returns. Closes this panel. |
| free.speed | free.speed | Fly speed | S | 0.1–200 m/s step 0.5 · 8 | U | L | Feel | |
| free.leash | free.leash | Range from your character | S | 1–25 m step 1 · 25 | U | L | Feel | The camera stops this far from your character. A server that sets its own camera limit overrides it. |
| free.fov | free.fov | Field of view | S | 10–120 ° · 0 = keep the current mode's | A | L | Feel | |
| free.sens | free.sens | Look sensitivity | S | 0.05–20 × step 0.05 · 1 | A | L | Feel | On top of Mouse-look's sensitivity. |
| free.turn | free.turn | Keyboard turn speed | S | 10–720 °/s step 10 · 90 | A | L | Feel | |
| free.roll | free.roll | Roll on entry | S | −180–180 ° step 5 · 0 | A | L | Feel | |

Build note: `Leave()` must restore FOV when `free.fov` was set.

**Tab-target** — group `tabtarget` (CX-636). Master = the whole mode; off = stock Tab and stock clicks, the lock and any pending preview cast dropped on the off edge. With it on, its cast mode supersedes the mode's `autotarget`.

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| tabtarget.enabled | (none) | Enable tab-target mode | T | off | U | L | MMO-style targeting: pick one target with Tab or a left-click, then quickbar spells, feats, items and the attack button fire at it with no second click. Off, Tab and clicks are the stock ones. |
| tabtarget.cast | (none) | Cast mode | D | Normal / Quick / Quick with preview · Quick | U | L | Normal: press the button, then click a target, as the game always did. Quick: the button fires at once -- at your target, or with none at what the cursor points at; a ground spell lands at the cursor. Quick with preview: hold the button to see the game's own targeting preview, release to fire; right-click or Escape before release cancels. |
| tabtarget.cyclekey | (none) | Cycle key | X | Tab | U | L | Locks the candidate nearest the centre of the screen, then steps through the others. Shift with it steps back. An SDL key name (Tab, F8, Z...). |
| tabtarget.clearkey | (none) | Clear key | X | Backspace | U | L | Drops the lock. Blank binds nothing. |
| tabtarget.camera | (none) | After picking a target | D | Nothing / Turn the camera to it / Lock the camera on it · Turn | U | L | Turn: the camera swings once to look at the new target (in tilt too, when the target sits off the middle of the view). Lock on: it keeps looking at the target as you both move, until you press the lock key (or pick nothing); the mouse cannot pull it off, and the turn and strafe keys run you sideways around the target instead of orbiting or strafing. |
| tabtarget.lockkey | (none) | Lock-on key | X | F | U | L | Locks the camera on the creature nearest the middle of the screen (it becomes your target); pressed while locked on, lets go. An SDL key name. |
| tabtarget.tilt | (none) | Target within | S | 0–25 ° of the centre step 1 · 12 | U | L | The camera keeps your own tilt while the target sits within this many degrees of the middle of the view; past it the camera tilts just enough to bring it back -- flatter for a far or raised target, steeper for one below. 0 always puts the target dead centre. |
| tabtarget.filter | (none) | Tab cycles | D | Hostile creatures / All creatures · Hostile creatures | U | L | Hostile only keeps allies and townsfolk out of a fight; all creatures lets you lock an ally for a heal. |
| tabtarget.objects | (none) | Include placeables and doors | T | off | U | L | Tab can also lock a lever, chest or door. |
| tabtarget.range | (none) | Maximum range | S | 5–80 m step 1 · 30 | U | L | Tab ignores anything farther from you than this. |

**Walking** — group `walk` (CX-682). Master = the client step guard: every step of the player's walk re-run through the engine's own door-aware line test, a refused step put back. Off = the stock untested walk. The status line is the refusal ledger (steps passed, walk vs drive refusals).

| key | today | label | kind | range · default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| walk.enabled | (none) | Stop at closed doors | T | on | U | L | Every step your character takes is re-tested against the area's walkmesh and its doors, closed doors solid, exactly as the game tests a click. A step that would cross a closed door is refused, so your character can never stand on the far side of one -- on any server. Off, the stock walk runs untouched. |

### 4.5 Hotkeys

**Hotkeys** — group `hotkeys`.

| key | today | label | kind | default | tier | eff | help |
|---|---|---|---|---|---|---|---|
| hotkeys.enabled | none | Enable server hotkeys | T | on | U | L | Lets a server bind keys to its own actions, after you approve each one. Off, no key is sent and no approval is asked; keys reach the game only. |
| hotkeys.bind | "Bind more" (nuklear) | Bind keys on this server… | Act | — | U | — | Opens the server's own key-binding window. Greyed outside a server session. |
| *(status line)* | key list | Approved on this server: F5, Ctrl+Q … / none | element | | U | | |
| hotkeys.revoke | "Revoke all" (nuklear) | Revoke every approved key | Act | — | U | — | Forgets every key you approved on this server and tells the server to unbind them. Click again to confirm. |
| hotkeys.forget | new | Forget every server | Act | — | U | — | Wipes the approvals stored for every server you ever played on; the one you are on now is told to unbind too. Each server asks again the next time it binds a key. Click again to confirm. |
| hotkeys.keydiag | keydiag + /keydiag | — | T | off | C | L | |
| hotkeys.consent | /consent | — | Act | | C | — | native consent-modal allocator probe |

Master off gates `Fire/Emit`, `Prompt` (an open modal is `Reset`) and capture; never the hooks
(`LiveMessage` is shared plumbing) and never the `CX_ENABLED` advertise. Inbound
`PresentToken`/`ArmSet` keep being consumed so re-enable is instant. *Forget every server* =
wipe every `[token=…]` section (no packet; #418's fail-closed rule) plus, in session, today's
Revoke-all. Both revoke rows confirm on a second click within 3 s. `MaxArmed` stays a const.

## 5. Feature disable verdicts

From the census; the caveat is what the build must do for "off" to be vanilla.

| Feature | Master | Effect | What off must do |
|---|---|---|---|
| Alloy | alloy.enabled | restart | `AlloyGate` is frozen at boot; off = never install. Every Graphics group greys. |
| Shadows | shadow.enabled | restart | caster pool + cascades are boot allocations. |
| Chat | chat.enabled | restart | `ChatService.Initialize` returns before any install; the hotkey observer's vanilla-focus path keys on `!chat.enabled`. Off = the stock chat window untouched. Prerequisite: the `HandleEnter` chain, and lifting the quickbar tick, dialog config load and font-scale edge out of `ChatService.OnFrame`. |
| Dialog | dialog.enabled | live | ownership flips per node at `ShowDialogEntry` (engine no-ops replies on null `+0x70`); the other hooks follow; forward while the engine panel is open; 4 trampolines kept. |
| Nameplates | nameplate.enabled | live | restore the engine bubble mode via the `_pendingMode` shape, forward the real mode while off, stop the `+0x14c` clear, `Suppressing` goes live (combat text reads it). Re-enable takes effect on the next area transition until the bubble bit-4 hazard is Ghidra-read. `on 0` today = blank, not vanilla. |
| Combat text | combattext.enabled | live | already (`!cfg.On` forwards); depends on `Suppressing` going live. |
| Quickbar | quickbar.enabled | live | keep replacing `Reposition` with the stock centre formula. |
| Compass, FPS | quickbar.compass, fps.enabled | live | already. `ServerPing` thread never stops. |
| Camera modes | `<mode>.enabled` | live | ninth `Gate` value; restore `_fovFirst`. |
| Movement, Walls | (none) | live | already (drive, recenter, free camera `Leave()` + FOV restore). |
| Mouse-look | mouselook.enabled | live | `Release()` on the off edge; the four boot reads become knobs. |
| Hotkeys | hotkeys.enabled | live | gate the actions, never the hooks or the advertise. |
| Fog, Grass, Water, Effects, Particles, Dangly | `<group>.enabled` | live | already have live twins; the boot twin is deleted. |
| General, Lighting, Post, AO, Free camera, About, Debug info | none | — | header says why. |

Framework facts: the window registry is immutable after frame 1, so every live hide is a
content early-return, never an Unregister. `lockchat` never installed nothing.

## 6. Primitives to build

| Primitive | Where | Notes |
|---|---|---|
| Scrollable column | `CX.Ui` | three per panel; also the dropdown list, Debug info, and #572's tab editor |
| Dropdown | `CX.Ui` | replaces `SetKind.Cycle`; list scrolls; `layout:` knobs use it by construction |
| Tooltip | `CX.Ui` | Advanced rows with help (the chat window's panel wraps help under the label since CX-683) |
| Section header, greyed row, off-default dot, restart badge, warning strip, Advanced disclosure | panel | per the prototype |
| Boxed master row | panel | |
| Status line, link row | panel | knob-less elements |
| Two-click confirm | panel | revoke rows, 3 s |
| Main-menu gear | `UiHostService` | glyph-atlas sprite (#425); yield exemption; `SyncUiScale` guard lifted |
| `HandleEnter` chain | `CommandRegistry` | Linux name symbol; Windows `At` row (`tools/abi/check-win-symbols.py`) |
| `KnobGroup` / `Knob<T>` / registry walk / debounced writer | `CX.Framework` | |

## 7. Build order

Each step ships on its own and leaves the client working.

1. **Registry + file.** `Knob<T>`, `KnobGroup`, binding from `Settings.Load`, the debounced
   writer, the generated `/<group>` handlers, `/knobs` as a walk, the command gate. Migrate one
   group end to end (Grass) to prove the shape; SelfCheck for the constraints.
2. **Migrate every group.** Graphics (`*Keys` → knob tables; the three `PassLifecycle` slots
   go), Camera (`CameraConfig` → six groups; `cx-camera.cfg` writer dies), UI (per-feature cfgs
   die; `ui.font` replaces chat's `font`), General + Hotkeys. Delete as you go (§8).
   `lockchat` dies here; `chat.enabled` arrives with the `HandleEnter` chain.
3. **Primitives:** scrollable column, dropdown, tooltip. Dropdown replaces the cycle button in
   chat's per-window panel too.
4. **The panel:** window, three columns, row kinds, master/parent/Advanced/reset/markers,
   `/settings` door, panel face. Delete the three per-feature panels and gears.
5. **The gear:** the three live checks, the yield exemption, the sprite, the anchor.
6. **Docs:** CLAUDE.md "Tuning knobs" (already points here), `docs/agents`, `/knobs` mention in
   README if any.

## 8. Deleted

- Names: `/vanilla`, `/draw`, `/probe`, `/halos`, `/sky`, `/beams`, `/framedump`, `/dumpshaders`,
  `/camera` and its subcommands and multi-arg forms, `/post scale`, `/post aotemporal`, `/qbar`,
  `/np`, `/ct`, `/nw`, `/cw`, Quickbar's `/settings`, Nameplate's `settings` subcommand,
  `/keydiag` as a separate name, `lockchat`.
- Code: `PassLifecycle.Tune/Describe/Reset` + the `*Keys` constants, `RenderCommands.Tune`,
  `CameraConfig.ToText/Parse` + `Set` switches, `SetKind.Cycle`, the global half of `SetAct`,
  `DialogSettings`, `NameplateSettings`, `QuickbarSettings` and their gears, the nuklear hotkey
  manager + the Options-panel "Hotkeys" button + their `SymbolMap` rows, `GameGate.Locked` /
  `OwnsFramePresent` and every `lockchat` branch (UiHostService, UiInputService, ChatService,
  GlContextService, AlloyGate, CasterPass, AlloyRenderer, Quickbar, Notice, HotkeyInputService),
  `ConfigFile.Load/Save/Baseline`, every `cx-<feature>.cfg` writer, nameplate `mode`, the FOV
  magic zero.
- Files: `cx-camera.cfg`, `cx-chat.cfg`, `cx-combattext.cfg`, `cx-dialog.cfg`,
  `cx-nameplate.cfg`, `cx-quickbar.cfg`, `cx-ui.cfg`. `cx-hotkeys.cfg` shrinks to tokens.
- Boot/live twins collapsed into one knob: `halos`/`halosize`/`halogain`, `probe`,
  `renderscale`, `shadows` (+ the feed's `_enabled`), `partbatch`, `partsamp`, `keydiag`,
  the four `mouselook*` boot keys, `camera.occlude`.

## 9. Not in this spec

Search across categories (header slot reserved), the
auto-update consent row (#31), chat's tab editor (#572), Path A-lite (#571), the hotkey
binding UI (#422, server-side), colour picker redesign, resizable panel / remembered geometry /
per-row reset, migration of `cx-*.cfg`.
