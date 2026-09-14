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
| `CAurBehaviorCameraFollow::Control(float)` | `?Control@CAurBehaviorCameraFollow@@UEAAHM@Z` (#536; returns `int`, no by-value crossing) |
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
| `CNWCMessage::HandleServerToPlayerChat` | `0x77c140` | `case 9` of the mapped HandleServerToPlayerMessage dispatch (`0x7a41a0`); also references the `"HandleServerToPlayerChat"` string directly |
| `CNWCMessage::HandleServerToPlayerDialog` (not hooked; the anchor for the four below) | `0x7962f0` | `case 0x14` of the same dispatch |
| `CGuiInGame::CloseDialog` | `0x6f6d80` | the Dialog handler's minor-5 branch: `GetInGameGui` (`0x6abbc0`) + `Initialized` (`0x6f9a90`) guard, then this. Body: panel at `gui+0x70` (same), `ResetGuiState(0x298)` |
| `CGuiInGame::ShowDialogEntry` | `0x6fb560` | minor-1 branch, `(gui, &str, oid, oid)` after `ReadCExoLocStringClient` |
| `CGuiInGame::ShowDialogReplies` | `0x6fb8e0` | minor-2 branch; body writes `panel+0x134` and `panel+0xbc0` (Linux `+0x144` / `+0xd08`) then `FixDialogSize` |
| `CGuiInGame::ShowReplyChosen` | `0x6fbbc0` | minor-3 branch; **destroys its by-value `CExoString` at the tail** -- the skip must too |
| `CNWCMessage::SendPlayerToServerDialog_Reply` | `0x7bbc50` | caller of SendPlayerToServerMessage with major `0x14` minor `3`; `CreateWriteMessage(9)`, OBJECTID + DWORD + BYTE + DWORD |
| `CClientExoApp::GetGameObject` | `0x6abb60` | forwarder to `Internal::GetGameObject` `0x6d81f0`; the Dialog handler calls it with `GetPlayerCreature()+0xdc` exactly like Linux |
| `CClientExoApp::GetObjectName` | `0x6abfc0` | forwarder to `Internal::GetObjectName` `0x6d83f0` = the one `Internal` caller of `C2DA::GetINTEntry(int, CExoString const&, int*)` (exported, `0x1a7010`) with the `"StrRef"` column; out-param order `(this, id, CExoString*)` unchanged |
| `CGuiInGamePopUpBubble::SetChatBubbleMode(int)` | `0x6e90e0` | callee of the console wrapper anchored by `"SetChatBubbleMode 0/1/2: Off, text-only, or full background"` (`0x6e9060`); walks the bubble list writing `+0x114` / `+0x148` (Linux `+0x11c` / `+0x150`) |
| `CNWCObject::SetPopUpBubbleText(CExoString, int, float, float)` | `0x766450` | the common callee of the three ShowFeedbackBubbleText bodies and the chat handler; checks `this+0x104 == 1` (retired) first; **frees its by-value `CExoString`** (forwarded, never skipped) |
| `CNWCCreature::ShowFeedbackBubbleText` | `0x80b240` | caller of exported `CTlkTable::GetSimpleString` building the `"STRING_REF"` 2DA column; sets `this+0x448 = 1` (Linux `+0x468`) |
| `CNWCPlaceable::ShowFeedbackBubbleText` | `0x871b60` | SetPopUpBubbleText caller with the `+0x1f8 = 1` / `bubble+0x144 = 0` shape (Linux `+0x218`) |
| `CNWCItem::ShowFeedbackBubbleText` | `0x83f230` | SetPopUpBubbleText caller with `+0x264 = 1`; **MSVC passes the `CNWCObject` subobject as `this`** (the body does `this-0x48` for `CNWCItem::GetName`), so no thunk to avoid |
| `CNWCObject::ShowFloatyText(CExoString, Vector, CExoString, int)` | `0x766a70` | callee of `HandleServerToPlayerCreatureUpdate_FloatyText` (`0x781a10`, tracy source-location struct at `0xe3e988` referencing the `__FUNCSIG__` string). **Win64: the `Vector` arrives as a pointer** (12-byte aggregate); both strings are callee-owned |
| `CNWCAnimBase::RefreshHilite` | `0x966170` | its own `ExoLog::Emit` call — file `game\\clientcore\\nwcanimbase.cpp`, line `0x123a`, `"Attempting to unhighlight a gob in a null scene"` — plus the tracy zone string `"RefreshHilite"`. Structure confirmed against Linux `0x9cb260`: the same 5-channel loop, lowest-active-wins, then `Scene::AddHighlightGob` |
| `CNWCArea::LoadBorder` | `0x7dea10` | `__FUNCSIG__` string `"int __cdecl CNWCArea::LoadBorder(class CGuiAreaLoadscreen *,float)"` via its tracy srcloc struct. **Win64 ABI: the `float` is the 3rd arg slot = `xmm2`**, not `xmm0` |
| `CNWCArea::ReloadBorder` | `0x7e1e40` | no string of its own; anchored through its ONE caller, `CNWCMessage::HandleServerToPlayerArea` (`0x76d910`, `__FUNCSIG__`-anchored), whose tail is three flag-bit branches in the same order on both builds — bit 1, bit 4, then bit 2 → this. Body confirmed: walks the scene at `area+0x248` calling `DeleteTile` then `SpawnTile` |
| `CPanelQuickBar::Reposition()` | `0x860010` | #439: RTTI vftable `0xe52900` (`tools/abi/vtable-slots.py`) slot `+0x120` = Linux `+0x128` − 8 (#433). Body confirmed: reads `g_pGuiMan+0xb8`, spawns `"GUI_QBAR_ENDS"` once, ends in `SetPositionScreen((w-800)/2, 0)`; the CAurObject vcall inside shows the same skew (Linux `+0xa0`, Windows `+0x98`) |
| `CGuiInGame::ShowCompass()` | `0x6fb490` | #439: the one user of `"pnl_compass"` + `"Viewport_Compass"`; builds the `CGuiCompassPanel` into `gui+0x68` (same), then `ResetGuiState(0x1000)` (`0x6fa7b0`) |
| `CGuiInGame::HideCompass()` | `0x6f9060` | #439: the `'0'` branch of the `"usage: GuiCompass [0|1]"` console handler (`0x6f3f80`, whose `'1'` branch is ShowCompass above). Body: deleting-dtor vcall on `gui+0x68`, null it, `ResetGuiState(0x1000)` |
| `SharedMaterial::~SharedMaterial` (#275) | `0x81f80` | the exported `??1SharedMaterial@@QEAA@XZ` has NO callers -- MSVC inlined it into the scalar deleting destructor `(this, flags)`, called from `RemoveRef` and `RefreshSharedMaterial`, the same two callers Linux D2 has. Decompiled: `(this, uint flags)`, the eleven SafePointer releases + `AurTextureRelease`, `delete` when `flags & 1`, returns `this`; both callers pass 1 |
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
| `stbtt_InitFont` … `stbtt_FindGlyphIndex` (7) | same C names |
| `g_pTlkTable` (data) | `?g_pTlkTable@@3PEAVCTlkTable@@EA` |
| `CTlkTable::GetSimpleString(uint)` -> `CExoString` | `?GetSimpleString@CTlkTable@@QEAA?AVCExoString@@I@Z` -- sret pointer is the 2nd arg on Win64 |
| `RenderInterface::GetViewPort(float*)` | `?GetViewPort@RenderInterface@@SAXPEAM@Z` |

**Not anchored, optional:** `CGuiInGameChatDialog::ForceDialogVisible(float)` is an empty body on both builds
(MSVC folds/inlines it); `CClientExoApp::GetModuleCamera()` is inlined at every Windows call site, so both
platforms read `GetModule()+0xe8` directly.
| `SDL_GL_GetProcAddress`, `SDL_GetMouseState`, `SDL_StartTextInput`, `SDL_Get/SetClipboardText`, `SDL_free` | same C names |

## The four forms, and how the table was filled (#447)

Every `public const string` in `SymbolMap.cs` has a row in `_win`. There is no fifth
form and no absence — `build.sh` refuses a constant without one, because a missing key
used to mean *both* "never looked" and "deliberately Linux-only".

| Form | Meaning | Counted? |
|---|---|---|
| `Nm("?Foo@Bar@@…")` | name-exported; resolved like Linux | no |
| `At(0x…)` | not exported; module base + RVA, build-locked | no |
| `LinuxOnly("why")` | a decision: not wanted on Windows | no |
| `Hole("why")` | not yet anchored | **yes** |

`/knobs` prints the tally beside `EngineLayout`'s unresolved offsets, and `Resolve`'s skip
line quotes the row's own words, so a log reader can tell a decision from a gap.

**The 142 `Nm` rows are mechanical, not hand-derived.** MSVC mangling encodes the return
type Itanium drops (#433), so `llvm-undname` over the 18237 PE exports and `llvm-cxxfilt`
over the Linux names pair on one key — qualified name plus parameter list, with return type,
`__cdecl`, `class`/`struct`, `__ptr64` and the `__int64`/`long long` spellings normalised
away. Nothing came back ambiguous. The licence for trusting it: run the same pass over the
39 rows already derived by hand and it reproduces **all 39 exactly**, wrong on none.

`tools/abi/check-win-symbols.py` is the standing check — every `Nm` row is a real export,
every `At` RVA lands inside the image (181 / 58, 0 bad). Alloy stands down under Proton, so
nothing exercises an Alloy `Nm` row at runtime yet; this is the only instrument that does.

**Measured per row, not paired:**

| Symbol | Windows | How |
|---|---|---|
| `PartSkin::vftable` | `0xd88438` | RTTI type descriptor → complete-object locator → vftable, via `tools/abi/vtable-slots.py` (which re-derives the checked-in `CAurTexture` `0xd90c70` independently) |
| `PartDanglyMesh::vftable` | `0xd88698` | same |
| `CycleTIDTextureController::vftable` | `0xd905c0` | same |
| `primTypeTranslationTable` | `0xd8ad20` | the `{0,1,3,4,5,6,2}` dword run in `.rdata`, one hit in the image; `GLRender::RenderVertexData` @`0xb59b0` `lea`s it three times |
| `GLRender` pool `{base,count}` pair | `0x128ead8` | `GetPoolBuffer` @`0xb3cb0` loads it with `mov rax,[rip+0x11dae19]` then indexes `pool*0x20+0xc`; `UnlockPool` @`0xbbad0` `lea`s the same address into the helper. Linux has no symbol on the pair and steps `noemittervar+0x236c`, so `EngineLayout.PoolTableAnchorOffset` is `Diverges(linux: 0x236c, win: 0x0)` — the row wanted an *anchor*, not a value |

**The 15 declared holes** are every symbol that still has none, and not one of them is
Alloy's: nuklear (10 — the library is linked but exports nothing), `CCachedMouseCursorManager::SetMouseCursor`
and `CClientExoAppInternal::UpdateMouseCursorAppearance` (2), `COptionsPanel::HandleXButton`,
`CClientAIMaster::UpdateState` and `CClientExoApp::GetNetLayer`. The last two are worth a
note for anyone hunting them: the *server* twins **are** exported (`CServerAIMaster`,
`CServerExoApp`) because the client exe carries the single-player server, so a name search
finds a function with the right name and the wrong class.

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
| `CClientExoApp` internal / `CClientExoAppInternal` in-game gui | `+0x8` / `+0x90` | same | `CClientExoApp::GetInGameGui` (`0x737c70` / rva `0x6abbc0`), one line on both (#439) |
| `CGuiInGame` quickbar panel | `+0x60` | same | `CGuiInGame::Initialize` (`0x772600` / rva `0x6f9190`): the CPanelQuickBar ctor's one caller stores to `+0x60` then `+0x28 = 1` (#439) |
| `CGuiMan` viewport width / height | `+0xb8` / `+0xbc` | same | `CGuiMan::SetGuiSize` (exported; `0x67efc0` / rva `0x1f6640`) stores its two args there (#439) |

## CNWC\* layout rows Alloy reads (#446) -- decomp-diffed against `0x68e408bc`

The seven `EngineLayout` rows whose owning class has no Windows name export, so the
class sweep dumps it empty. Each was read off a function on **both** builds, using the
three anchors above.

| Struct / field | Linux | Windows | Where seen |
|---|---|---|---|
| `CNWCModule` current area | `+0xf8` | same | `CNWCCreature::UpdatePopUpBubble` (`0x72e600` / rva `0x8107a0`): dereferenced, then `area+0x118` compared against the creature's own area id |
| `CNWCArea` flags (bit 0 = interior) | `+0x8` | same | `CNWCArea::LoadArea` (`0x70e010` / rva `0x7dba70`): three `ReadINT`s into `0x2a0`/`0x2a4`/`0x2a8`, then the next `ReadDWORD` into `+8`. **Wire order is the pin** — no compiler may reorder it |
| `CNWCArea` tile width / height | `+0xc` / `+0x10` | same | `CNWCArea::LoadBorder` (`0x70a5c0` / rva `0x7dea10`): `+0xc` is the row-index multiplier, `+0x10` the row loop bound; `(height-1)*width` appears in both |
| `CNWCArea` tile array (0x60 stride) / scene | `+0x298` / `+0x248` | same | the same `LoadBorder`; `ReloadBorder` reaches the scene virtuals through `+0x248` on both |
| `CNWCAnimBase` hilite active / glow / gob | `+0x9c[ch]` / `+0xb0[ch]` / `+0xc8` | same | `RefreshHilite` (`0x9cb260` / rva `0x966170`): flags stride 4 over 5 channels, colour `+0x60[ch]` stride 12, gob the guarded pointer |

**No probe on the hilite rows.** `CNWCAnimBase::Hilite` writes the global `HiliteEnabled`
into the active flag rather than a literal `1`, and a fresh `CNWCAnimBase` has every
channel zeroed — an `Expect.Byte01` would pass on any offset that happens to read zero,
and a probe that cannot fail is not a probe.

#433's −8 virtual-destructor rule fires three more times in passing, on three
different classes: `RefreshHilite` calls **Gob** slots `0x180`/`0x188`/`0x220` (the
highlight gob at `+0xc8`, not the scene) where Windows calls `0x178`/`0x180`/`0x218`;
it calls **Scene** `AddHighlightGob`/`RemoveHighlightGob` at `0x120`/`0x128` where
Windows calls `0x118`/`0x120`; and `ReloadBorder` calls **Scene** `DeleteTile`/`SpawnTile`
at `0x58`/`0x38` where Windows calls `0x50`/`0x30`. None is a row here, but the two
`Scene` readings land on the same class as the eight measured `Scene` `vtbl.*` rows and
agree with them.

## Nameplate / combat text / dialog raw offsets (#420) -- decomp-diffed against `0x68e408bc`

| Struct / field | Linux | Windows | Where seen |
|---|---|---|---|
| `CNWCObject` id / type / pos / bubble / retired / override mode+text+len | `+0x8` / `+0xc` / `+0x38` / `+0xb8` / `+0x104` / `+0x124`,`+0x128`,`+0x130` | same | `SetPopUpBubbleText` `0x766450`, `Creature::ShowFeedbackBubbleText` `0x80b240`, `Object::UpdatePopUpBubble` `0x766ea0`, `Internal::GetObjectName` `0x6d83f0` |
| `CNWCCreature` reaction / stats / plated / area | `+0x3a8` / `+0x3d0` / `+0x468` / `+0x478` | **`+0x388` / `+0x3b0` / `+0x448` / `+0x458`** | `0x80b240`; `Creature::UpdatePopUpBubble` `0x8107a0` (`this+0x458 == module[0xf8][0x118]`) |
| `CNWCPlaceable` plated | `+0x218` | **`+0x1f8`** | `0x871b60` |
| `CNWCItem` plated (from the `CNWCObject` subobject) | `+0x2cc-0x48` | **`+0x264`** | `0x83f230` |
| `CNWCCreatureStats` first / last / override name (+len) / damage byte | `+0x20`,`+0x28` / `+0x30`,`+0x38` / `+0x40`,`+0x48` / `+0x8e` | same | `CNWCCreatureStats::GetFullName` `0x7f3f00`; `0x80b240` |
| `CGuiInGamePopUpBubble` live-text / mode / anchor x,y | `+0x14c` / `+0x150` / `+0x154`,`+0x158` | **`+0x144` / `+0x148` / `+0x14c`,`+0x150`** | `SetChatBubbleMode` `0x6e90e0`; `Show(Vector)` `0x6e9820` |
| `CNWCModule` area / camera | `+0xf8` / `+0xe8` | same | every `GetModule()` reader |
| `CNWCArea` id | `+0x118` | same | `0x8107a0` |
| `Camera` position / orientation / viewport rect | `+0xb0` / `+0xbc` / `+0x4d4` | same / same / **`+0x4d8`** | exported `Camera::GetPosition` `0x58e10`, `SetOrientation` `0x5b6d0`, `SetViewPort` `0x5b810` |
| `CClientExoAppInternal` mouse-over feedback word | `+0x180` | same | the `ui.mouseover-feedback` setter lambda `0x6bdb20` |
| `GetPortrait(uchar)` virtual slot | `+0x1d8` | **`+0x1d0`** | the chat handler `0x77c140` and `0x766450` call it `(this, sret, 1)` -- **sret is the 2nd arg on Win64** |

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

## Reading the tester's log (#421)

No debugger on the tester's side: `sowx.log` (in `Documents\Neverwinter Nights\`, beside `nwn.ini`)
has to answer "which symbol is wrong" on its own. `sowx.cfg` goes beside `nwn.ini` (the engine may use a
`Neverwinter Nights.0` instance folder; `sowx.log` always lands in the unsuffixed one). `logs=1` must be set **before launch**
-- the gate closes at `settings:` and everything after is silent except the stand-down lines.

| Line | Meaning |
|---|---|
| `host: N service(s) discovered (Alloy stood down: Windows, #48)` | the DLL loaded and Windows was detected |
| `<Feature>: standing down as a unit -- <symbol>: nwmain.exe build 0x… != expected 0x68e408bc` | **rot** reaching the RVA guard. Unreachable in practice since `#457`: the build-key gate in `ServiceHost.Run` refuses the same client before discovery, so the player sees `host: client build changed, extender standing down WHOLE` and the message box instead. This line survives as the second check, not the first |
| `<Feature>: standing down as a unit -- <symbol>: no Windows anchor (by design)` | a Linux-only feature; expected, not a bug |
| `<Feature>: standing down as a unit -- <symbol>: Windows export ?…@@… missing` | the export table changed: a "By name" row is stale |
| `ChatService: inbound tap installed (AppendToMsgBuffer, Win64 CResRef* ABI)`, `NameplateService: armed`, `CombatTextService: armed`, `DialogService: hooks installed` | each tier-B unit passed its gate |
| `UiHostService: first frame presented -- hook is LIVE` | the present hook fires; the port is live |
| a crash with no line after `armed` | a hook that installs clean and faults on first call: an **offset**, not a symbol. Set `hookdebug=1` (funchook's trace lands in `funchook.log`) and look up the feature's row in the offset tables above |
| in-game `/nameplate probe` | one line per plate with every term of the anchor conversion (viewport source, offsets read) -- the offset probe for the plate/bubble rows |
| in-game `/consent probe` | opens the consent panel on the game's own allocator and logs the answer, granting nothing -- the probe for the `operator new` RVA |

## Regenerating after a client patch

Both binaries are analyzed in the one Ghidra project `~/ghidra/nwn-linux.rep`
(programs `nwmain-linux`, `nwmain.exe`). The tables above ARE the regen path: each RVA
row's "How anchored" column is the recipe for that symbol, and each offset row names
the function it was read from. There is no automation (decision, #3: Beamdog patches
about once every three years). The Ghidra scripts used (`~/ghidra_scripts`: TracyFn
= tracy string -> srcloc struct -> function, Qwords = MSVC vtable dump, GrepDecomp =
regex over a decompiled name range) run via `analyzeHeadless … -process nwmain.exe
-postScript`; the method is:

1. Confirm the two binaries are the same build (`build.txt`), update the build lock.
2. **String anchors** — a target referencing a unique string maps to the lone
   Windows function referencing the same string. Compiler-invariant.
3. **Anchor chaining** — from a resolved/exported neighbor (`CGuiEdit::LoseFocus`,
   `SendPlayerToServerMessage`), enumerate callers/callees; match by structure.
4. **Immediate constants** — compiler-invariant. Distinguish structural twins by the
   enum/major they pass (e.g. the chat handlers' `NotifyServerAboutGuiEvent(…,1/2,…)`).
5. **Decompile-diff** — confirm each candidate against the named Linux original.

6. Update `WindowsBuildStamp`, then the boot log under Proton (`./build-win.sh &&
   ./run-proton.sh`) must show every unit `armed` and none standing down.

Names resolve directly on the Windows side after Ghidra applies the ~16k exports;
only the 50 RVA rows above need the steps.
