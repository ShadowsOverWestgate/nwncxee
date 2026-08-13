# Hotkeys: client <-> server contract (for sow-codebase)

The "Hotkeys" feature is split across the two repos. This client extender does the
parts only a client can; **the arming UI is a server-authored NUI** (sow-codebase
owns the toolbar layout and already builds many NUIs). This file is the contract
the server side must implement to match the client.

## Split of responsibilities

Client extender (this repo, done):
- Options-menu "Hotkeys" button (repurposed SaveCharButton).
- Runtime capture: a pressed **armed** key -> a packet to the server, gated on
  window focus + "not typing in chat", debounced 250 ms.
- Handshake: advertises extender presence + the armed set on login.
- Persists/caches the armed set client-side (`~/.config/sowx-hotkeys.cfg`).

Server (sow-codebase, to build):
- Builds the arming **NUI** (it knows the toolbar/ability layout to show).
- Owns the key -> toolbar-action **mapping** (the client never sees it).
- Persists the armed set per character (server vault).
- Pushes the armed set to the client when it changes.

## The four messages

Opcodes below are **placeholders** in `managed/Services/HotkeyProtocol.cs`
(`0x36 / 0x40..0x42`). Pick real ones the server already routes and match them on
both ends. All custom payloads are **ASCII text** (no endianness).

### 1. Handshake (client -> server) -- device advertisement, already implemented

On login accepted, the client advertises via the stock device channel
(`SendPlayerToServerDeviceAdvertiseProperty`, major `0x36` / minor `1`). The server
stores each as `player.propertyMap[lowercased name] = int` (on `CNWSPlayer`,
offset `0xe0`). Properties:

- `sowx_enabled = 1`      -- the extender is present; only now offer the feature.
- `sowx_hk_count = N`     -- number of armed keys the client already had cached.
- `sowx_hk_<i> = vk`      -- each armed SDLK code, `i` in `0..N-1`. **Read by count**
  (the map only ever inserts -- a shorter re-advertise leaves stale higher indices).

Server reads these to know the extender is present and the client's current set.

### 2. OpenUi (client -> server) -- "player clicked Hotkeys"

- major/minor: `OpenUiMajor/Minor`, **no payload**.
- Sent when the player clicks the Options-menu "Hotkeys" button.
- Server response: `NuiCreate` the arming window for that player.

### 3. KeyPress (client -> server) -- an armed key was pressed

- major/minor: `KeyPressMajor/Minor`, payload = **ASCII decimal SDLK**, e.g. `"102"` (f).
- The client only sends keys in the armed set, at most once per 250 ms per key,
  never while unfocused or typing. Server maps the keycode to the toolbar action.

### 4. ArmSet (server -> client) -- here is the armed set

- major/minor: `ArmSetMajor/Minor`, payload = **ASCII CSV of SDLK codes**, e.g.
  `"102,1073741882"` (f, F1) (empty payload = arm nothing).
- Sent whenever the player changes their selection in the NUI (and optionally on
  enter, to restore the vault-persisted set). The client computes the delta:
  **removals** (armed now, not in the new set) apply silently; **additions** raise
  the client-owned consent modal and only arm on the player's OK.
- Only send this to clients that advertised `sowx_enabled = 1`.

### 5. Revoke (client -> server) -- forget these keys

- major/minor: `RevokeMajor/Minor` (`0x36 / 0x43`), payload = **ASCII CSV of SDLK
  codes**, e.g. `"102,1073741882"`.
- Sent when the player **denies** the consent modal (so the server does not keep the
  binding and re-push/re-prompt it every login) or unbinds from a client-side review.
- Server response: drop those keys from the character's map (unbinding whatever
  toolbar/metamagic slot they pointed at) and re-push the shrunk ArmSet. A revoke for
  an unbound key is a no-op.

## Keycodes (SDLK, not Windows VK)

The codes are **SDL keycodes (SDLK)** -- what `HandleWMKeyDownMessage` actually
delivers. They are NOT Windows VK codes (an earlier draft said "VK"; that was
wrong). Reference values:
`a..z = 97..122` (e.g. `f = 102`), `0..9 = 48..57`,
`F1 = 1073741882 .. F12 = 1073741893` (F1 + n-1), `Space = 32`, `Return = 13`.
Digits coincide with VK; letters (lowercase) and F-keys (SDL's `0x40000000`
range) do not -- so a "VK" value like `70` for F will silently never match.
The number is opaque to the server: display key names, but store/echo whatever
SDLK the client sent. Full list: SDL's `SDL_keycode.h` (`SDLK_*`).

### Confirmed failure mode (runtime-verified)

If the server's key table uses VK codes, the client receives them, arms them
verbatim, and **nothing matches except digits**. Observed live: server pushed
`{F=70, G=71, F1=112, F2=113, 2=50}`; the client delivered `{102,103,
1073741882,1073741883,50}` on those presses, so only `50` (`2`) fired. Fix the
server's key list to SDLK.

Conversions for the common keys a toolbar UI offers:

| Keys        | VK (wrong)   | SDLK (correct)                | Formula                 |
|-------------|--------------|-------------------------------|-------------------------|
| `A..Z`      | `65..90`     | `97..122`  (lowercase ASCII)  | `sdlk = vk + 32`        |
| `0..9`      | `48..57`     | `48..57`   (identical)        | `sdlk = vk`             |
| `F1..F12`   | `112..123`   | `1073741882..1073741893`      | `sdlk = 1073741882+(vk-112)` |
| Space       | `32`         | `32`                          | same                    |
| Everything else: look it up in `SDL_keycode.h` -- do not assume it maps.     |||

Better than converting: define the server's key list in SDLK from the start
(the value is opaque anyway) and drop VK entirely.

## Flow

```
login  -> client advertises sowx_enabled + cached armed set   (msg 1)
click  -> client OpenUi ------------------------------------->  server NuiCreate
NUI    <- player arms keys -> server persists (vault) -------->  server ArmSet (msg 4)
       -> client caches set, starts capturing those keys
press  -> client KeyPress (armed, gated, debounced) --------->  server maps -> action (msg 3)
deny   -> client Revoke ------------------------------------->  server forgets binding (msg 5)
```
