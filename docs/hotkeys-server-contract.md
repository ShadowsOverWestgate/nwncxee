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
- `sowx_hk_<i> = vk`      -- each armed VK code, `i` in `0..N-1`. **Read by count**
  (the map only ever inserts -- a shorter re-advertise leaves stale higher indices).

Server reads these to know the extender is present and the client's current set.

### 2. OpenUi (client -> server) -- "player clicked Hotkeys"

- major/minor: `OpenUiMajor/Minor`, **no payload**.
- Sent when the player clicks the Options-menu "Hotkeys" button.
- Server response: `NuiCreate` the arming window for that player.

### 3. KeyPress (client -> server) -- an armed key was pressed

- major/minor: `KeyPressMajor/Minor`, payload = **ASCII decimal VK**, e.g. `"112"`.
- The client only sends keys in the armed set, at most once per 250 ms per key,
  never while unfocused or typing. Server maps the VK to the toolbar action.

### 4. ArmSet (server -> client) -- here is the armed set

- major/minor: `ArmSetMajor/Minor`, payload = **ASCII CSV of VK codes**, e.g.
  `"112,113,114"` (empty payload = arm nothing).
- Sent whenever the player changes their selection in the NUI (and optionally on
  enter, to restore the vault-persisted set). The client replaces its armed set,
  caches it, and begins/stops capturing those keys immediately.
- Only send this to clients that advertised `sowx_enabled = 1`.

## VK codes

The codes are the client engine's Windows-VK-style codes (what
`HandleWMKeyDownMessage` delivers, same space as the stock key-map):
`F1..F12 = 112..123`, `0..9 = 48..57`, `A..Z = 65..90`. The server should display
key names but store/echo the numeric VK.

## Flow

```
login  -> client advertises sowx_enabled + cached armed set   (msg 1)
click  -> client OpenUi ------------------------------------->  server NuiCreate
NUI    <- player arms keys -> server persists (vault) -------->  server ArmSet (msg 4)
       -> client caches set, starts capturing those keys
press  -> client KeyPress (armed, gated, debounced) --------->  server maps -> action (msg 3)
```
