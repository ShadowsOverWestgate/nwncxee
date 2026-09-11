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
| `CGuiInGame::AppendToMsgBuffer` | `0x6f68d0` | unique strings `"[CHAT WINDOW TEXT] [%s] %s\n"` + `"AppendToMsgBuffer"` (guiingame.cpp:0x2a3). **Win64 ABI: 6th arg is `CResRef*`** (17-byte aggregate -> pointer to a caller copy) |
| `CGuiInGameChatWindow::AppendToChatBuffer` | `0x879080` | the 6-arg callee AppendToMsgBuffer calls twice (once per window); shares `"BUT_CHAT_REPLY"` with SetFilter |
| `CGuiInGameChatWindow::SetFilter` | `0x87a0e0` | the other `"BUT_CHAT_REPLY"` user; 3 args, writes the filter mask `this+0xae0` first (Linux `+0xc58`) |
| `CGuiInGameChatWindow::Show` | `0x87a940` | caller of `CGuiPanel::Activate` (exported) with the `if(+0xaf0) return; Activate; +0xaf0=1` shape (Linux flag `+0xc68`); called from ShowMsgWindow only |
| `CGuiInGameChatPrompt::Show` | `0x87cc00` | same shape on flag `+0x200` (Linux `+0x230`); its lone caller is ShowChatPrompt |
| `CGuiInGameChatPrompt::CGuiInGameChatPrompt()` | `0x87abb0` | the `+0x200` Activate shape that RETURNS `this` and is stored to `gui+0x18` by its caller. **MSVC inlined Show here**, so Windows hooks the ctor and Deactivates after it |
| `CGuiInGameChatPrompt::SetFocus` | `0x87cb30` | callee of PrepChatPromptForTell (below) and of the mapped SetFocusChatPrompt; calls `CGuiEdit::SetFocus` (exported) then NotifyServerAboutGuiEvent(…,1,…,0x7f000000). Embedded edit at `+0xf8` (Linux `+0x100`) |
| `CGuiInGame::ShowChatPrompt` | `0x6fb480` | the one caller of ChatPrompt::Show, forwarding `*(this+0x18)` |
| `CGuiInGame::PrepChatPromptForTellToPlayer` | `0x6f9ec0` | unique string `"/tp \"%s\" "`; reads `player+0x10` (name) |
| `CGuiInGame::NotifyServerAboutGuiEvent` | `0x6f9bd0` | 5-arg callee of ChatPrompt::SetFocus; GetPlayerCreature + GetNWCMessage (mapped `0x6abf90`) guards |
| `CClientExoApp::GetPlayerByPlayerName` | `0x6ac120` | ParseChatString (`"tellplayer"` string, `0x87b590`) calls it once between the two ByCreatureName calls, matching the Linux order; body copies the string and walks `intern+0x38/+0x40` comparing `player+0x10/+0x18` |
| `CClientExoApp::GetPlayerByCreatureName` | `0x6ac080` | ParseChatString's first and third lookup; inner walk compares `player+0x40/+0x48` |
| `CClientExoApp::GetModule` | `0x6abea0` | forwarder to `*(intern+0x30)`; ConnectToServer (caller of exported `CGuiMan::EscapeAllModalPanels`) reads `GetModule()+0xf8` through it, and the tracy-anchored `HandleServerToPlayerModuleUpdate_Time` (`0x7a58e0`) writes `+0x160..+0x16c` on its result |
| `CNWCMessage::SendPlayerToServerChat_Talk` | `0x7ba7e0` | caller of SendPlayerToServerMessage with major 9 minor 1 |
| `…Chat_Shout` | `0x7ba700` | major 9 minor 2 |
| `…Chat_Whisper` | `0x7baab0` | major 9 minor 3 |
| `…Chat_Tell(uint, CExoString)` | `0x7ba8c0` | major 9 minor 4 after `WriteBOOL(0)` + `WriteDWORD(id)` |
| `…Chat_TellName(CExoString const&, CExoString const&)` | `0x7ba9b0` | major 9 minor 4 after `WriteBOOL(1)`, two strings |
| `…Chat_Party` | `0x7ba620` | major 9 minor 6 |
| `…Chat_DungeonMaster` | `0x7ba540` | major 9 minor 0xe |
| `CAurTexture::vftable` / `CAurTextureBasic::vftable` (data) | `0xd90c70` / `0xd90d90` | RTTI type descriptors `.?AVCAurTexture@@` / `.?AVCAurTextureBasic@@` -> complete-object locator -> vftable (Ghidra names them). MSVC's symbol is the object's first word: no Itanium `+0x10` skew (`AurTex.Attach`) |

**By name — client-native features (verified in the `0x68e408bc` export table):**

| Linux symbol | Windows (MSVC) |
|---|---|
| `CPanelOkCancel::CPanelOkCancel()` | `??0CPanelOkCancel@@QEAA@XZ` |
| `CPanelOkCancel::Activate(CExoString,uint,uint)` | `?Activate@CPanelOkCancel@@QEAAXVCExoString@@II@Z` |
| `g_pGuiMan` (data) | `?g_pGuiMan@@3PEAVCGuiMan@@EA` |
| `CAurora::GetGUIScale()` → float (GUI-scale multiplier) | `?GetGUIScale@CAurora@@SAMXZ` (export ord. 8543) |
| `SDL_GetKeyName` | `SDL_GetKeyName` |
| `CGuiPanel::Deactivate(int)` | `?Deactivate@CGuiPanel@@QEAAXH@Z` |
| `RenderInterface::ResetRenderStates()` / `BindBuffer(uint,uint)` | `?ResetRenderStates@RenderInterface@@SAXXZ` / `?BindBuffer@RenderInterface@@SAXII@Z` |
| `stbtt_InitFont` … `stbtt_MakeCodepointBitmap` (6) | same C names |
| `SDL_GL_GetProcAddress`, `SDL_GetMouseState`, `SDL_StartTextInput`, `SDL_Get/SetClipboardText`, `SDL_free` | same C names |

## Chat raw offsets (#419) -- decomp-diffed against `0x68e408bc`

| Struct / field | Linux | Windows | Where seen |
|---|---|---|---|
| `CClientExoAppInternal` player list ptr / count | `+0x38` / `+0x40` | same | `Internal::GetPlayerBy*` walks (`0x6d8a20`, `0x6d8910`, `0x6d89c0`, `0x6d89f0`) |
| `CClientExoAppInternal` local creature id | `+0x48` | same | `Internal::GetPlayerCreature` `0x6d8ad0` |
| `CClientExoAppInternal` module | `+0x30` | same | `Internal::GetModule` `0x6d8300` via `CClientExoApp::GetModule` |
| `CNWCPlayer` id / name / name len / creature id | `+0x8` / `+0x10` / `+0x18` / `+0x34` | same | `GetPlayerByPlayerID` `0x6d89f0`, `GetPlayerByGameObjectID` `0x6d89c0`, name walk `0x6d8a20` |
| `CNWCModule` hour / minutes-per-hour | `+0x16c` / `+0x15c` | same (`+0x15c` by adjacent layout, `+0x160..+0x170` identical) | `ModuleUpdate_Time` `0x7a58e0` |
| `CGuiInGameChatWindow` filter / shown | `+0xc58` / `+0xc68` | **`+0xae0` / `+0xaf0`** | SetFilter / Show above. Nothing reads them on Windows today |
| `CGuiInGameChatPrompt` shown / edit | `+0x230` / `+0x100` | **`+0x200` / `+0xf8`** | Show / SetFocus above |

**Win64 by-value conventions that differ from SysV** (every hook or call on this page was checked against them):
a non-trivial class (`CExoString`) arrives as a pointer to a copy **the callee destroys** -- a skipped body must run the
dtor or it leaks (`Native.DestroyExoString`); a call site that passed a `NewExoString` sees it zeroed on return, and the
later `FreeExoString` is a null-safe no-op. A 17-byte `CResRef` arrives as a pointer to a copy (SysV: 16 bytes in memory).

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
