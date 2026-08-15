# Windows symbol resolution

The Linux client (`nwmain-linux`) is an unstripped ELF with default-visibility
exports, so every hook/call target resolves by its Itanium-mangled name at runtime
(`./sym.sh -m`). The Windows client (`nwmain.exe`) is a PE whose exports are
**opt-in**: Beamdog marks only shared-engine classes for export and omits the
client-only GUI / message classes. So on Windows some symbols resolve by name and
the rest resolve by **RVA** (address = module base + RVA).

`managed/Framework/SymbolMap.cs` is the single source of truth pairing each Linux
name with its Windows resolution. This file explains where the RVAs came from and
how to regenerate them when Beamdog patches the client.

## Build lock

RVAs are only valid for the exact build they were read from. `SymbolMap` pins the
PE `TimeDateStamp` and refuses RVA-based hooks if the loaded client differs
(fail-loud, degrade to no-op — never corrupt).

| | |
|---|---|
| Build revision | `26c6e57364a45246e3b58e4f2c49366df72d15db` (Mon Oct 6 2025) |
| PE TimeDateStamp | `0x68e408bc` |
| Image base | `0x140000000` |

Linux and Windows binaries were confirmed same-revision (`bin/*/build.txt`) before
deriving anything — required, or the RVAs point into a different build.

## Resolution table

**By name (exported on both platforms):**

| Linux (Itanium) | Windows (MSVC) |
|---|---|
| `HandleWMKeyDownMessage(uint,uint)` | `?HandleWMKeyDownMessage@@YAHII@Z` |
| `WinMessageHandler(SDL_Event*)` | `?WinMessageHandler@@YAHPEATSDL_Event@@@Z` |
| `GLRender::SwapBuffers(SDL_Window*)` | `?SwapBuffers@GLRender@@CAXPEAUSDL_Window@@@Z` |
| `CAuroraStringWrapper::SetText(uint)` | `?SetText@CAuroraStringWrapper@@QEAAXI@Z` |
| `CExoString::InitFromCharArray` | `?InitFromCharArray@CExoString@@AEAAAEAV1@PEBDI@Z` |
| `CExoString::~CExoString` | `??1CExoString@@QEAA@XZ` |
| `g_pAppManager` (data) | `?g_pAppManager@@3PEAVCAppManager@@EA` |

**By RVA (not exported; build `0x68e408bc`):**

| Linux symbol | Windows RVA | How anchored |
|---|---|---|
| `CNWCMessage::HandleServerToPlayerMessage` | `0x7a41a0` | unique strings `"HandleServerToPlayerMessage"` + `"[%s] Discarding unknown message: %s"` |
| `CNWCMessage::HandleServerToPlayerLogin` | `0x7a31b0` | unique strings `"HandleServerToPlayerLogin"` + `"nNumClasses <= 8"` |
| `CNWCMessage::SendPlayerToServerMessage` | `0x7c25c0` | tracy zone string `"SendPlayerToServerMessage"` |
| `CNWCMessage::SendPlayerToServerDeviceAdvertiseProperty` | `0x7bbac0` | caller of SendMsg; write-sequence + `SendMsg(major 0x36)` |
| `CClientExoApp::GetNWCMessage` | `0x6abf90` | decompile of HandleSaveCharButton (first call) |
| `COptionsPanel::PostAttachmentInitialize` | `0x830b50` | button-name string cluster (`SaveCharButton`, `ResumeGameButton`, …) |
| `COptionsPanel::HandleSaveCharButton` | `0x82fb60` | address-taken in PostAttach; `AddFeedbackMessage(0xd7)` + `Deactivate` |
| `CGuiInGame::SetFocusChatPrompt` | `0x6fae30` | decompile: reads `+0x18`, forwards to chat-prompt SetFocus |
| `CGuiInGameChatPrompt::HandleEnter` | `0x87b0d0` | caller of `CGuiEdit::LoseFocus`; GetText+Find(`a`,`d`)+SetCaretPos |
| `CGuiInGameChatPrompt::HandleEsc` | `0x87b390` | caller of LoseFocus, 4 callees, no GetText |
| `CGuiInGameChatPrompt::HandleLMouseDownGuiEvent` | `0x87b450` | Notify event enum **1** (matches Linux) |
| `CGuiInGameChatPrompt::HandleLoseKeyboardFocusLMouseDownGuiEvent` | `0x87b4b0` | Notify event enum **2** (matches Linux) |
| `TurnOffKeyboardModal()` | `0x6e5d80` | decompile of `CGuiEdit::SetFocus` (exported): the call right after `CExoInput::KeyboardModeOn`; body reads `*(*g_pAppManager+8)`, resets modal keys, clears the WASD drive latch + sends `AbortDriveControl` (#34) |
| `operator new(size_t)` (consent modal alloc) | `0xc10324` | `_malloc_base` + `_callnewh` loop (VS2017/2019 release); the exact allocator the ExitButton `CPanelOkCancel` spawn calls |

**By name — client-native features (verified in the `0x68e408bc` export table):**

| Linux symbol | Windows (MSVC) |
|---|---|
| `CPanelOkCancel::CPanelOkCancel()` | `??0CPanelOkCancel@@QEAA@XZ` |
| `CPanelOkCancel::Activate(CExoString,uint,uint)` | `?Activate@CPanelOkCancel@@QEAAXVCExoString@@II@Z` |
| `g_pGuiMan` (data) | `?g_pGuiMan@@3PEAVCGuiMan@@EA` |
| `CAurora::GetGUIScale()` → float (GUI-scale multiplier) | `?GetGUIScale@CAurora@@SAMXZ` (export ord. 8543) |
| `SDL_GetKeyName` | `SDL_GetKeyName` |

## Consent modal — PORTED to Windows (decomp-verified, build `0x68e408bc`)

The client-owned yes/no consent panel (`HotkeyConsent`) runs on Windows. The four symbols
above resolve by name; `operator new` resolves by the RVA in the table. Everything was verified
against the Windows spawn path (the ExitButton `CPanelOkCancel` handler = the MSVC twin of Linux
`COptionsPanel::HandleQuitClick`). The real Linux→Windows deltas turned out small:

| Aspect | Linux | Windows | Note |
|---|---|---|---|
| `sizeof(CPanelOkCancel)` (operator new arg) | `0x3a0` | `0x360` | `HotkeyConsent.PanelSize`, per-platform |
| context field | `+0x80` | `+0x80` | **same** |
| on-close callback field | `+0x88` | `+0x88` | **same** |
| `+0x90` zero-write | present | absent | Linux-only; MSVC ctor leaves it safe (its own code omits it) |
| `Activate(msg, ok, cancel)` strrefs | `0x2052/0x2053` | `0x2052/0x2053` | **same** |
| allocator | libstdc++ `operator new` | game's own `operator new` @ RVA `0xc10324` | static CRT — see below |

Two things were the actual work, not the field offsets:

- **Allocator = the game's own `operator new`, by RVA.** `nwmain.exe` statically links the CRT
  (no CRT DLL in its import table at all — `objdump -x` shows none), so `operator new`/`delete`
  and `malloc`/`free` are internal, non-exported. The panel self-frees via the game's `operator
  delete`, so allocating from *any other* heap (the .NET runtime's, a separately-loaded CRT)
  would corrupt on free. We resolve the game's own scalar `operator new(size_t)` by RVA `0xc10324`
  — build-locked like every other RVA, so a mismatched client degrades to fail-closed, never a
  wrong-heap crash.
- **By-value `CExoString` destruction flips ABI.** `Activate` takes the message `CExoString` by
  value. Itanium destroys by-value params in the **caller**; MSVC in the **callee**. The Windows
  `?Activate@CPanelOkCancel@@QEAAXVCExoString@@II@Z` (RVA `0x200b40`) frees *and zeroes* the passed
  `CExoString` before returning, so `Native.FreeExoString`'s later `~CExoString` sees a null buffer
  → `free(null)` no-op, not a double-free. Same call site is correct on both platforms unchanged.

Earlier notes in this doc claimed Windows offsets `+0x188/+0x270/+0x278` and that `malloc`/`free`
were imported — **both were wrong** (the `0x270` was a `CClientExoApp` field in the ExitButton
handler's player-loop, misattributed; there is no CRT DLL import). Corrected here from the decomps.

**Client-native manager (nuklear) — NOT ported (a genuine RE session, not a table fill-in):**

- **Nuklear widgets.** The `nk_sdl_*` backend IS anchorable — the exe carries Tracy signature
  strings (`void __cdecl nk_sdl_render(float)`, …) and `…\lib\nuklear\nuklear.cpp`. But the six
  **core widgets** (`nk_begin`, `nk_end`, `nk_label`, `nk_button_label`, `nk_layout_row_dynamic`,
  `nk_window_is_closed`) have **no string anchors** → structural / call-graph matching against the
  Linux versions. `g_pNuklear` is data (xref-anchored).
- **`COptionsPanel::HandleXButton`** (menu close on the Hotkeys button): no export (COptionsPanel
  exports nothing); RVA derivable from `PostAttachmentInitialize`'s handler-wiring cluster (like
  SaveChar was). Without it the Options menu just stays open behind the manager — cosmetic.

The manager is Linux-only for now; on Windows the Hotkeys button falls back to opening the server's
binding NUI (`HotkeyMenuService.OnHotkeysButton`), so the button is never dead.

**Unresolved on Windows (skip + log, graceful):**

| Linux symbol | Why skipped | Cost |
|---|---|---|
| `CClientExoApp::OnGainFocus` | empty stub, dispatched indirectly — no string/callee/caller anchor | focus gate lost; OS only delivers keys to the focused window, so redundant |
| `CClientExoApp::OnLostFocus` | same | stuck-key clear on focus loss lost; self-heals on next full press |
| `g_pNuklear` (data) | client global, not exported; data symbols aren't string-anchorable | NUI-typing gate lost; a hotkey pressed while typing in a NUI field may fire |

## Regenerating after a client patch

Both binaries are analyzed in the one Ghidra project `~/ghidra/nwn-linux.rep`
(programs `nwmain-linux`, `nwmain.exe`). The derivation scripts (Java GhidraScripts,
run via `analyzeHeadless … -process nwmain.exe -postScript`) live in this repo's
history under the scratchpad; the method is:

1. Confirm the two binaries are the same build (`build.txt`), update the build lock.
2. **String anchors** — a target referencing a unique string maps to the lone
   Windows function referencing the same string. Compiler-invariant.
3. **Anchor chaining** — from a resolved/exported neighbor (`CGuiEdit::LoseFocus`,
   `SendPlayerToServerMessage`), enumerate callers/callees; match by structure.
4. **Immediate constants** — compiler-invariant. Distinguish structural twins by the
   enum/major they pass (e.g. the chat handlers' `NotifyServerAboutGuiEvent(…,1/2,…)`).
5. **Decompile-diff** — confirm each candidate against the named Linux original.

Names resolve directly on the Windows side after Ghidra applies the ~16k exports;
only the ~12 client-class targets need the steps above.
