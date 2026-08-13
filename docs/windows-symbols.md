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
