using System.Runtime.InteropServices;

namespace CX.Framework;

/// <summary>
/// The one place that knows a symbol's identity and how it resolves per platform. Each
/// engine symbol is a <c>public const</c> Linux (Itanium) mangled name; the table pairs
/// it with its Windows resolution. Linux exports everything by name; Windows name-exports
/// only shared-engine classes, so client GUI/message classes resolve by build-locked RVA.
/// Both HookService.Install and Native.Sym go through <see cref="Resolve"/>, so this file
/// is the whole platform surface. RVA provenance / regen recipe: docs/windows-symbols.md.
/// </summary>
internal static unsafe class SymbolMap
{
    // ---- symbol identity: the Linux mangled name of everything we resolve ----
    // input / frame
    public const string HandleWMKeyDown   = "_Z22HandleWMKeyDownMessagejj";
    public const string WinMessageHandler = "_Z17WinMessageHandlerP9SDL_Event";
    public const string SwapBuffers       = "_ZN8GLRender11SwapBuffersEP10SDL_Window";
    /// <summary>#336 step 5: the engine main loop's four non-render steps, timed by <c>EngineZonesService</c>.
    /// <c>CNetLayer::ProcessReceivedFrames(int)</c> @0x5b92c0, <c>CExoResMan::Update(unsigned)</c> @0x536c60,
    /// <c>CClientAIMaster::UpdateState()</c> @0x991c30, <c>CGuiMan::UpdateAndRender(float)</c> @0x580450.</summary>
    public const string NetProcessReceivedFrames = "_ZN9CNetLayer21ProcessReceivedFramesEi";
    public const string ResManUpdate = "_ZN10CExoResMan6UpdateEj";
    /// <summary>CClientExoApp::GetNetLayer() @0x6361a0 -- <c>*(*(this+8)+0x18)</c>; and
    /// CNetLayer::GetServerNetworkAddress() @0x5ba070, which returns a CExoString BY VALUE
    /// ("a.b.c.d:port"), so the ABI is <c>(CExoString* sret, CNetLayer* this)</c>. Read by
    /// <c>ServerPing</c> for the /fps ping readout. Linux-only: no Windows anchor derived.</summary>
    public const string GetNetLayer = "_ZN13CClientExoApp11GetNetLayerEv";
    public const string GetServerNetworkAddress = "_ZN9CNetLayer23GetServerNetworkAddressEv";
    public const string ClientAiUpdateState = "_ZN15CClientAIMaster11UpdateStateEv";
    public const string GuiUpdateAndRender = "_ZN7CGuiMan15UpdateAndRenderEf";
    // window focus (CClientExoApp). Empty stubs reached by indirect dispatch, so no Windows anchor
    // was derivable -- they skip there, which the OS focus handling already covers.
    public const string OnGainFocus       = "_ZN13CClientExoApp11OnGainFocusEv";
    public const string OnLostFocus       = "_ZN13CClientExoApp11OnLostFocusEv";
    // chat prompt (typing gate)
    public const string SetFocusChatPrompt   = "_ZN10CGuiInGame18SetFocusChatPromptEv";           // chat opened by key
    public const string ChatHandleLMouseDown = "_ZN20CGuiInGameChatPrompt24HandleLMouseDownGuiEventEv"; // clicked into
    public const string ChatHandleEnter      = "_ZN20CGuiInGameChatPrompt11HandleEnterEv";
    public const string ChatHandleEsc        = "_ZN20CGuiInGameChatPrompt9HandleEscEv";
    public const string ChatHandleLoseFocus  = "_ZN20CGuiInGameChatPrompt41HandleLoseKeyboardFocusLMouseDownGuiEventEv";
    /// <summary>CGuiEdit::GetText() @0x592680 -- the prompt's typed line, joined from the edit's CAurString lines.
    /// Returns a CExoString BY VALUE, i.e. <b>sret</b>: Itanium <c>(CExoString* out, CGuiEdit* this)</c>, Win64
    /// <c>(this, out)</c>; the buffer it writes is CALLER-OWNED (zero the struct first, dtor it after). What
    /// <c>HandleEnter</c> itself calls first, so the #580 chain reads exactly what vanilla would have sent.</summary>
    public const string GuiEditGetText       = "_ZN8CGuiEdit7GetTextEv";
    // ---- chat edit control (build slice #20): focus suppression, keyboard-mode gate, SDL
    //      text-input + clipboard. Chatbox-specific symbols are Linux-only (no _win entries -> skip-with-log
    //      on Windows). EXCEPTIONS: ExoInput + GetKeyboardMode carry _win mappings -- the hotkey
    //      keyboard-mode gate (cross-platform) reads them, and both are name-exported on Windows. ----
    /// <summary>CGuiInGame::ShowChatPrompt() -- a chat opener we skip to suppress vanilla focus.</summary>
    public const string ShowChatPrompt = "_ZN10CGuiInGame14ShowChatPromptEv";
    /// <summary>CGuiInGameChatPrompt::CGuiInGameChatPrompt() -- hooked on WINDOWS ONLY (#419): MSVC inlined Show into
    /// it, so the prompt is activated at birth there; Linux's ctor calls Show, which the skip already covers.</summary>
    public const string ChatPromptCtor = "_ZN20CGuiInGameChatPromptC1Ev";
    /// <summary>CGuiPanel::Deactivate(int) -- what vanilla Hide pairs with Activate; undoes the ctor-inlined Show.</summary>
    public const string GuiPanelDeactivate = "_ZN9CGuiPanel10DeactivateEi";
    /// <summary>CGuiInGame::PrepChatPromptForTellToPlayer(CNWCPlayer*) -- (this, player) chat opener; skipped.</summary>
    public const string PrepChatPromptForTell = "_ZN10CGuiInGame29PrepChatPromptForTellToPlayerEP10CNWCPlayer";
    /// <summary>CExoInput* global; deref for the instance to drive KeyboardModeOn/Off.</summary>
    public const string ExoInput = "g_pExoInput";
    /// <summary>CExoInput::KeyboardModeOn() -- engine gameplay-key gate on (text entry).</summary>
    public const string KeyboardModeOn = "_ZN9CExoInput14KeyboardModeOnEv";
    /// <summary>CExoInput::KeyboardModeOff() -- gameplay-key gate off (resume movement).</summary>
    public const string KeyboardModeOff = "_ZN9CExoInput15KeyboardModeOffEv";
    /// <summary>CExoInput::GetKeyboardMode() -- nonzero while the engine is capturing text (chat, console,
    /// any vanilla edit field); the signal to suppress gameplay hotkeys.</summary>
    public const string GetKeyboardMode = "_ZN9CExoInput15GetKeyboardModeEv";
    /// <summary>CAurora::GetGUIScale() -- static float accessor for the engine's live GUI-scale multiplier
    /// (floor 1.0; changes with resolution / the options slider). We fold it into the chatbox scale so our
    /// overlay tracks the game's UI scaling. Name-exported on Windows (CAurora is a shared-engine class).</summary>
    public const string GetGuiScale = "_ZN7CAurora11GetGUIScaleEv";
    /// <summary>TurnOffKeyboardModal() -- the free function vanilla runs from CGuiEdit::SetFocus right after
    /// KeyboardModeOn: resets modal keys, clears the WASD drive-mode latch, and sends AbortDriveControl so the
    /// server stops too. We SkipOriginal the vanilla focus path, so we must call it ourselves or movement
    /// sticks when chat grabs the keyboard mid-walk (#34). Takes no args -- it walks g_pAppManager itself.</summary>
    public const string TurnOffKeyboardModal = "_Z20TurnOffKeyboardModalv";
    public const string SdlStartTextInput   = "SDL_StartTextInput";
    public const string SdlGetClipboardText = "SDL_GetClipboardText";
    public const string SdlSetClipboardText = "SDL_SetClipboardText";
    public const string SdlFree             = "SDL_free";

    // client<->server messages (CNWCMessage)
    public const string HandleServerToPlayerMessage = "_ZN11CNWCMessage27HandleServerToPlayerMessageEPhj";
    public const string HandleServerToPlayerLogin   = "_ZN11CNWCMessage25HandleServerToPlayerLoginEh";
    public const string SendPlayerToServerMessage   = "_ZN11CNWCMessage25SendPlayerToServerMessageEhhPhj";
    public const string SendDeviceAdvertiseProperty = "_ZN11CNWCMessage41SendPlayerToServerDeviceAdvertisePropertyERK10CExoStringi";
    // ---- the area-loaded hold (#672, research docs/research/area-loaded-hold.md). Landed for #674/#675;
    //      nothing hooks them yet. ----
    /// <summary>CNWCMessage::SendPlayerToServerArea_AreaLoaded() @0x7fa0c0 -- major 4 minor 3, no payload; the
    /// send whose arrival makes the server place the player (<c>MovePlayerToArea</c>). Three call sites: the
    /// post-loading-screen tail of <see cref="HandleUpdateWorkRemaining"/> and the two movie exits.</summary>
    public const string SendAreaLoaded = "_ZN11CNWCMessage33SendPlayerToServerArea_AreaLoadedEv";
    /// <summary>CNWCMessage::HandleServerToPlayerUpdate_WorkRemaining() @0x781a30 -- drives the load bar and, on
    /// the last tick, fades the loadscreen and sends AreaLoaded (or latches <c>internal+0x58c</c> behind an intro
    /// movie). The hold's hook point. <c>(this) -> int</c>.</summary>
    public const string HandleUpdateWorkRemaining = "_ZN11CNWCMessage40HandleServerToPlayerUpdate_WorkRemainingEv";
    /// <summary>CClientExoApp::StartLoadBar(int state, char const* image, int rebuild) @0x638a70 -- forwarder to the
    /// internal; disables input, stores <c>state</c> at <c>internal+0x450</c> (1 = local stall, 2 = area transition).</summary>
    public const string StartLoadBar = "_ZN13CClientExoApp12StartLoadBarEiPKci";
    /// <summary>CClientExoApp::EndLoadBar() @0x636980 -- destroys the loadscreen, zeroes +0x450, re-enables input.
    /// The happy path does NOT call it: the bar fades out inside WorkRemaining's tail instead.</summary>
    public const string EndLoadBar = "_ZN13CClientExoApp10EndLoadBarEv";
    /// <summary>CClientExoApp::GetLoadBarState() @0x638240 -- <c>*(int*)(internal+0x450)</c>, 0 when no bar is up.</summary>
    public const string GetLoadBarState = "_ZN13CClientExoApp15GetLoadBarStateEv";
    // chat-render probe (#14) -- Linux-only throwaway; no Windows anchors (skip + log there).
    public const string AppendToMsgBuffer   = "_ZN10CGuiInGame17AppendToMsgBufferE10CExoStringjji7CResRef"; // inbound tap
    public const string SetPriMsgFilter     = "_ZN10CGuiInGame25SetPrimaryMsgWindowFilterEj";               // force backlog replay
    public const string ViewportSetViewport = "_ZN12CGuiViewport11SetViewportEiiii";                        // arbitrary-width test
    public const string ChatDoFullScreen    = "_ZN20CGuiInGameChatWindow12DoFullScreenEi";                  // 720 preset width
    public const string ChatDoSplitScreen   = "_ZN20CGuiInGameChatWindow13DoSplitScreenEi";                 // 360 preset width
    // chat-render suppression (#23): SkipOriginal these so vanilla draws nothing. AppendToChatBuffer stops
    // new lines reaching the docked windows' text model; SetFilter stops any filter/split re-rendering the
    // backlog (and both births of CReplyButton). The by-value CExoString param is freed by the CALLER
    // (AppendToMsgBuffer, kept running) -- verified in the disasm -- so skipping is data- and leak-safe.
    public const string AppendToChatBuffer  = "_ZN20CGuiInGameChatWindow18AppendToChatBufferE10CExoStringjji7CResRef";
    public const string ChatWindowSetFilter = "_ZN20CGuiInGameChatWindow9SetFilterEjPP17CGuiInGameMessagej";
    // ...and the chrome: SkipOriginal Show on the docked window + input prompt so they never activate into
    // the render tree (Show pairs with Activate, Hide with Deactivate). The gate reads module-presence, not
    // the window's IsVisible flag, so keeping the window un-shown doesn't hide our own box.
    public const string ChatWindowShow      = "_ZN20CGuiInGameChatWindow4ShowEv";
    public const string ChatPromptShow      = "_ZN20CGuiInGameChatPrompt4ShowEv";

    // dialog reimplementation (#dialog): SkipOriginal the three GUI-layer entry points so the vanilla
    // CGuiInGameChatDialog panel is never created/populated/activated (its ptr at CGuiInGame+0x70 stays
    // null); we capture the already-decoded data and render our own overlay. The message layer
    // (HandleServerToPlayerDialog) runs unmodified -> VO, camera, speaker tracking preserved. CloseDialog is
    // call-through (panel null -> it guards and just runs ResetGuiState). Replies go out via
    // SendPlayerToServerDialog_Reply (major 0x14, minor 0x03) with ClientApi.Message() as `this`. RVA on
    // Windows (#420); ForceDialogVisible is an empty body on both builds and has no Windows anchor (optional).
    public const string ShowDialogEntry   = "_ZN10CGuiInGame15ShowDialogEntryERK10CExoStringjj";
    public const string ShowDialogReplies = "_ZN10CGuiInGame17ShowDialogRepliesEiP10CExoStringPjiji";
    public const string ShowReplyChosen   = "_ZN10CGuiInGame15ShowReplyChosenEjj10CExoString";
    public const string CloseDialog       = "_ZN10CGuiInGame11CloseDialogEv";
    public const string SendDialogReply   = "_ZN11CNWCMessage30SendPlayerToServerDialog_ReplyEjjhj";
    /// <summary>CGuiInGameChatDialog::ForceDialogVisible(float) -- a timer-driven re-show (arg is likely the VO
    /// duration). SkipOriginal it too: its body only touches the vanilla panel we never create, so if the
    /// message layer calls it against our null panel it would deref garbage. Skipping is always correct.</summary>
    public const string ForceDialogVisible = "_ZN20CGuiInGameChatDialog18ForceDialogVisibleEf";
    /// <summary>CClientExoApp::GetGameObject(uint) -> CGameObject* (or null). Speaker-id -> object for the
    /// dialog portrait; the object's portrait comes from its virtual GetPortrait (vtable+0x1d8), so no extra
    /// symbol is needed for that. Resolved via the same *(*g_pAppManager) chain ShowDialogEntry uses.</summary>
    public const string GetGameObject = "_ZN13CClientExoApp13GetGameObjectEj";

    /// <summary>CClientExoApp::GetObjectName(uint objId, CExoString* out) -- writes the speaker's display name
    /// (creature full name / item / placeable-door StrRef via TLK) into a caller-allocated 16-byte CExoString
    /// {char* buf@0; uint len@8; uint cap@0xc}. The <em>out buffer is caller-owned heap</em> (fresh operator
    /// new[] every call, or a Steal from a temp) -- it MUST be zeroed before the call (the engine frees any
    /// non-null buf first) and freed after via <see cref="ExoStringDtor"/>. Empty name = len==0/buf==NULL. Pass
    /// the CClientExoApp* (the wrapper does the +8 to the internal itself). Confirmed by Ghidra decompile.</summary>
    public const string GetObjectName = "_ZN13CClientExoApp13GetObjectNameEjP10CExoString";
    // Frees GetObjectName's caller-owned CExoString buffer -> the existing ExoStringDtor (in "misc helpers").

    /// <summary>CGuiInGameChatPrompt::SetFocus(CExoString) -- opens the vanilla prompt with a prefill
    /// string. The player-list "Tell Player" button calls this directly (with '/t "Name" '), bypassing
    /// the three opener hooks -- so it gets its own skip-original hook. By-value CExoString is
    /// non-trivial -> lowered to a pointer arg.</summary>
    public const string ChatPromptSetFocus  = "_ZN20CGuiInGameChatPrompt8SetFocusE10CExoString";
    public const string SendChatTalk        = "_ZN11CNWCMessage27SendPlayerToServerChat_TalkE10CExoString"; // outbound verify

    /// <summary>CGuiInGame::NotifyServerAboutGuiEvent(int type, int integer, uint oid, Vector const&amp;) --
    /// the sole caller of SendPlayerToServerGuiEvent_Notify (one xref in the whole binary). It reads
    /// g_pAppManager itself and returns without sending when GetPlayerCreature() or GetNWCMessage() is
    /// null, so it carries the not-in-a-module guard; `this` (rdi) is clobbered before use and may be
    /// null. Chat-bar events: type 1 = focus, 2 = unfocus, integer = the prompt's chat mode,
    /// oid = 0x7f000000, vector = {0,0,0}. Wire: existing major 0x35 minor 0x01, itself gated on
    /// ServerSatisfiesBuild(0x2001, 0x23) -- nothing new is invented.</summary>
    public const string NotifyServerGuiEvent = "_ZN10CGuiInGame25NotifyServerAboutGuiEventEiijRK6Vector";
    // outbound send parity (#22): the other five channel senders. All major 0x09; minor per §4.3.
    // Talk/Shout/Whisper/Party/DM take (this, CExoString); TellName takes (this, const CExoString&, const CExoString&).
    public const string SendChatShout   = "_ZN11CNWCMessage28SendPlayerToServerChat_ShoutE10CExoString";        // minor 0x02
    public const string SendChatWhisper = "_ZN11CNWCMessage30SendPlayerToServerChat_WhisperE10CExoString";      // minor 0x03
    public const string SendChatTell    = "_ZN11CNWCMessage31SendPlayerToServerChat_TellNameERK10CExoStringS2_"; // minor 0x04, (name, text); server resolves by FIRST name only
    public const string SendChatTellId  = "_ZN11CNWCMessage27SendPlayerToServerChat_TellEj10CExoString";         // minor 0x04, (playerId, text) -- vanilla /t path
    public const string SendChatParty   = "_ZN11CNWCMessage28SendPlayerToServerChat_PartyE10CExoString";        // minor 0x06
    public const string SendChatDM      = "_ZN11CNWCMessage36SendPlayerToServerChat_DungeonMasterE10CExoString"; // minor 0x0e

    /// <summary>CClientExoApp::GetPlayerByPlayerName(CExoString) -> CNWCPlayer* or null.
    /// Name→player roster lookup for Tell-pinned tabs (#45, ADR 0002).</summary>
    public const string GetPlayerByPlayerName = "_ZN13CClientExoApp21GetPlayerByPlayerNameE10CExoString";

    /// <summary>CClientExoApp::GetPlayerByCreatureName(CExoString) -> CNWCPlayer* or null.
    /// What vanilla /t resolves with -- pinned tab names are character names, so try this first.</summary>
    public const string GetPlayerByCreatureName = "_ZN13CClientExoApp23GetPlayerByCreatureNameE10CExoString";

    // cursor override: client-internal, no Windows anchor yet -> skip + log there.
    public const string UpdateMouseCursorAppearance = "_ZN21CClientExoAppInternal27UpdateMouseCursorAppearanceEi";
    public const string SetMouseCursor = "_ZN25CCachedMouseCursorManager14SetMouseCursorEiiiii";

    // quickbar positioning (client GUI, Windows = RVA rows, #439)
    public const string QuickbarReposition = "_ZN14CPanelQuickBar10RepositionEv";
    public const string SetPositionScreen  = "_ZN9CGuiPanel17SetPositionScreenEii";
    // compass hide/show (CGuiInGame methods, take `this`)
    public const string HideCompass = "_ZN10CGuiInGame11HideCompassEv";
    public const string ShowCompass = "_ZN10CGuiInGame11ShowCompassEv";

    // misc engine helpers
    public const string GetNWCMessage = "_ZN13CClientExoApp13GetNWCMessageEv";
    public const string SetText       = "_ZN20CAuroraStringWrapper7SetTextEj";
    public const string ExoStringInit = "_ZN10CExoString17InitFromCharArrayEPKcj";
    public const string ExoStringDtor = "_ZN10CExoStringD1Ev";
    // consent modal (CPanelOkCancel, as used by the quit dialog). All name-exported on both
    // platforms except operator new (static CRT on Windows -> RVA; see the _win table).
    public const string OperatorNew     = "_Znwm";                                     // Linux: libstdc++ operator new(size_t); Windows: the game's own by RVA
    public const string OkCancelCtor    = "_ZN14CPanelOkCancelC1Ev";
    public const string OkCancelActivate= "_ZN14CPanelOkCancel8ActivateE10CExoStringjj"; // (CExoString msg, uint ok, uint cancel)
    public const string SdlGetKeyName   = "SDL_GetKeyName";
    public const string SdlGetKeyFromName = "SDL_GetKeyFromName";                        // action-camera toggle key by name
    /// <summary><c>CClientExoAppInternal::WalkPlayerToPoint(float, float, float, int, uint, int)</c> @0x663950: the one
    /// seam every left-click walk goes through (the action camera refuses it). Windows: the only function referencing
    /// "gui_nowalk" on both builds (StringRefs).</summary>
    public const string WalkPlayerToPoint = "_ZN21CClientExoAppInternal17WalkPlayerToPointEfffiji";
    /// <summary><c>CClientExoAppInternal::FeedbackHitCheck{Hostile,NonHostile}(CNWCObject**, Vector*, int*, int)</c>
    /// @0x660a10 / @0x6618f0: the world pick under the cursor (hover, cursor shape, tooltip, both clicks). The
    /// dispatcher between them is inlined on Windows, so both are hooked; paired by callee lists (the hostile one
    /// calls CNWTileSet::GetHeightTransition, the other does not).</summary>
    public const string FeedbackHitCheckHostile = "_ZN21CClientExoAppInternal23FeedbackHitCheckHostileEPP10CNWCObjectP6VectorPii";
    public const string FeedbackHitCheckNonHostile = "_ZN21CClientExoAppInternal26FeedbackHitCheckNonHostileEPP10CNWCObjectP6VectorPii";
    public const string SdlGetModState  = "SDL_GetModState";                            // live Ctrl/Shift/Alt state at capture
    public const string SdlGetMouseState = "SDL_GetMouseState";
    /// <summary><c>CActionTarget::ActivateTargetMode()</c> @0x9652b0 -- the one funnel every "pick a target" mode
    /// reaches (quickbar spell/feat/item, radial, DM tool, script <c>SetupScriptTargetMode</c>); the subclasses only
    /// build the mode and call this last. Takes <c>this</c> and nothing else, returns void, on both builds.</summary>
    public const string ActivateTargetMode = "_ZN13CActionTarget18ActivateTargetModeEv";
    /// <summary><c>CActionTarget::HandleLMouseDown()</c> @0x965840 -- resolve a live targeting mode at the engine's
    /// own mouse position (<c>CGuiMan::GetMousePos</c> + <c>FeedbackHitCheck</c> + the allowed-target scan + the
    /// per-mode validation callback). Called, not hooked: the auto-target fires the same code path a click does.
    /// Safe to call re-entrantly from the <see cref="ActivateTargetMode"/> post-hook -- it touches no member after
    /// the fire callback (which may <c>delete this</c>), and all 45 callers of ActivateTargetMode call it last.</summary>
    public const string ActionTargetLMouseDown = "_ZN13CActionTarget16HandleLMouseDownEv";
    // The SDL glue of the client's nuklear is C++-linkage on this build (mangled), so nk_sdl_render
    // must use its mangled name or the hook never installs. Nothing nuklear is anchored on Windows.
    /// <summary>Nuklear's GL flush, called from <c>CGuiMan::UpdateAndRender</c> @0x5809fa straight after
    /// <c>Nui__Render()</c> (which only fills the command queue and touches no GL). Everything drawn before
    /// it lands UNDER every NUI window and over the vanilla GUI panels -- which is where the framework's own
    /// windows draw (<c>UiHostService</c>), not just where nuklear windows get built.</summary>
    public const string NkSdlRender        = "_Z13nk_sdl_renderf";
    /// <summary><c>int nk_window_is_any_hovered(nk_context*)</c> -- pointer precedence for the layering
    /// above. NUI draws on top of our windows, so it must get the click too.</summary>
    public const string NkWindowIsAnyHovered = "nk_window_is_any_hovered";
    /// <summary><c>void nk_sdl_copy_event(SDL_Event*)</c> -- nuklear's own event feed, normally reached via
    /// <c>CExoInput::AddEvent</c>. Swallowing an event at <c>WinMessageHandler</c> starves it, so a swallowed
    /// pointer motion is handed here directly to keep <see cref="NkWindowIsAnyHovered"/> answering live.</summary>
    public const string NkSdlCopyEvent      = "_Z17nk_sdl_copy_eventP9SDL_Event";
    // in-game game clock (chat game-time timestamps). The SERVER-synced module time lives on CNWCModule
    // (hour at +0x16c); CClientExoApp::GetModule(app) fetches it off the same *g_pAppManager chain the
    // in-game gate walks. NOT CWorldTimer -- that's a free-running local clock never anchored to server time.
    // Linux-only (no _win entry -> skip on Windows).
    public const string GetModule         = "_ZN13CClientExoApp9GetModuleEv";
    /// <summary>CClientExoApp::GetPlayerCreature() @0x637780 -- the controlled creature (client object). Call
    /// with a live CClientExoApp* (ClientApi.LiveExoApp). Used to anchor the shadow map on the PLAYER, not the
    /// camera, so camera yaw/pitch and line-of-sight don't move the shadows. Re-fetch every frame (the object
    /// is freed on area transitions -- never cache). Its world position is a Vector at <c>+0xa4</c> (x,y,z;
    /// z at +0xac per sow-codebase CNWSObject notes).</summary>
    public const string GetPlayerCreature = "_ZN13CClientExoApp17GetPlayerCreatureEv";

    // ---- overhead nameplate / chat bubble (docs/re/nameplate.md). Client-side classes: RVA on Windows (#420). ----
    /// <summary>CGuiInGamePopUpBubble::SetChatBubbleMode(int) @0x6a0be0 -- STATIC (mode in edi, no `this`).
    /// Mode 0 sets every bubble's <c>+0x150</c>, and every path that could activate the panel is guarded on
    /// it: the engine stops drawing both the plate and the bubble while still building the state and
    /// refreshing the screen anchor we read. This is a supported vanilla setting, not a patch.</summary>
    public const string SetChatBubbleMode = "_ZN21CGuiInGamePopUpBubble17SetChatBubbleModeEi";
    /// <summary>CNWCCreature::ShowFeedbackBubbleText() @0x71a800 -- builds the mouse-over/Tab plate string and
    /// sets the creature's own "plate is up" flag. ABI: <c>(this) -&gt; void</c>.</summary>
    public const string CreatureShowFeedbackBubbleText = "_ZN12CNWCCreature22ShowFeedbackBubbleTextEv";
    /// <summary>CNWCPlaceable::ShowFeedbackBubbleText() @0x7ea8a0 -- the placeable twin.</summary>
    public const string PlaceableShowFeedbackBubbleText = "_ZN13CNWCPlaceable22ShowFeedbackBubbleTextEv";
    /// <summary>CNWCItem::ShowFeedbackBubbleText() @0x7cea90 -- the item twin. Hook the PRIMARY only: the
    /// <c>_ZThn72_</c> thunk @0x7ced80 is <c>sub $0x48,%rdi; jmp</c> straight into it, so hooking both
    /// would double-fire.</summary>
    public const string ItemShowFeedbackBubbleText = "_ZN8CNWCItem22ShowFeedbackBubbleTextEv";
    /// <summary>CNWCObject::SetPopUpBubbleText(CExoString, int, float, float) @0x7e8050 -- the ONE sink both
    /// the plate and an overhead chat line pass through, and the only place the speaking/plated object is a
    /// clean <c>CNWCObject*</c>. ABI (verified by disassembly): <c>(this /*rdi*/, CExoString* /*rsi, the
    /// by-value string lowered to a hidden pointer*/, int forceShow /*edx*/, float perChar /*xmm0*/,
    /// float fixedSec /*xmm1*/)</c> -- Ghidra drops the two floats.</summary>
    public const string SetPopUpBubbleText = "_ZN10CNWCObject18SetPopUpBubbleTextE10CExoStringiff";
    /// <summary>CGuiPanel::UnToggleOff() @0x5876a0 -- clears the hide bit (<c>+0xb8 &amp;= ~4</c>) and re-attaches the
    /// model only when that leaves the panel at exactly state 1 (was 5: active AND hidden); on an inactive panel it is
    /// the bit clear and nothing else. #591: the vanilla bubble idles with that bit set (<c>Show</c>/<c>Update</c> →
    /// <c>ToggleOff</c>) and <c>UpdatePopUpBubble</c> early-outs on it, so after a vanilla interlude the mode-0
    /// anchor stays frozen until the bit is cleared -- <c>SetChatBubbleMode(0)</c>'s walk only Deactivates
    /// (<c>&amp;= ~1</c>). Called per bubble from the two cold hooks that read its anchor.</summary>
    public const string GuiPanelUnToggleOff = "_ZN9CGuiPanel11UnToggleOffEv";
    /// <summary>CNWCMessage::HandleServerToPlayerChat(unsigned char) @0x75b280 -- the channel byte arrives in
    /// esi and the SetPopUpBubbleText for that line runs on the same stack, so it latches the channel for
    /// per-channel bubble styling. ABI: <c>(this /*rdi*/, uchar channel /*esi*/)</c>.</summary>
    public const string HandleServerToPlayerChat = "_ZN11CNWCMessage24HandleServerToPlayerChatEh";
    // ---- combat text: damage numbers + floating combat text (docs/re/combat-text.md). RVA on Windows (#420). ----
    /// <summary>CNWCObject::ShowFloatyText(CExoString, Vector, CExoString, int) @0x7e7010 -- the ONE funnel
    /// every damage number and combat-text banner passes through (CC messages, the creature floaty-text
    /// update, whirlwind, and every <c>AddFeedbackMessage</c> site). Skipping it means the engine never
    /// allocates a <c>CGuiInGameFloatyText</c> at all, so its one-at-a-time drip (<c>obj+0x98</c>) retires
    /// with it. ABI (verified by disassembly @0x8e7010): <c>(this /*rdi*/, CExoString* text /*rsi, the
    /// by-value string lowered to a hidden pointer*/, Vector colour /*xmm0 = (r,g), xmm1 = b*/,
    /// CExoString* font /*rdx*/, int background /*ecx*/)</c>. The font resref is the class discriminator:
    /// <c>fnt_floaty_smal</c> = a damage number, "" = a banner.</summary>
    public const string ShowFloatyText = "_ZN10CNWCObject14ShowFloatyTextE10CExoString6VectorS0_i";
    /// <summary>CTlkTable::GetSimpleString(unsigned int) @0x541fe0 -- strref to text. Returns a CExoString by
    /// value, i.e. <b>sret</b>: <c>(CExoString* out /*rdi*/, CTlkTable* this /*rsi*/, uint strref /*edx*/)</c>,
    /// and the buffer it writes is CALLER-OWNED (zero the struct first, free it after). Used once, cold, to
    /// resolve the two critical-hit strrefs the client formats its crit banner from.</summary>
    public const string TlkGetSimpleString = "_ZN9CTlkTable15GetSimpleStringEj";

    /// <summary>RenderInterface::GetViewPort(float*) @0x45d0b0 -- tail-jumps to GLRender::GetViewPort, which
    /// widens the four ints of <c>GLRender::m_viewport</c> into the caller's float[4] {x, y, w, h}. The same
    /// call <c>Camera::MapToScreen</c> falls back to when the camera carries no viewport of its own.</summary>
    public const string GetViewPort = "_ZN15RenderInterface11GetViewPortEPf";

    // data globals
    public const string AppManager = "g_pAppManager";
    public const string GuiMan     = "g_pGuiMan";
    /// <summary><c>CGuiMan::ReleaseKeyboard(CGuiObject*)</c>: clears the keyboard-capture slot (CGuiMan+0x50, same on
    /// both builds) and runs the object's lose-focus virtual (an edit's -> KeyboardModeOff).</summary>
    public const string GuiManReleaseKeyboard = "_ZN7CGuiMan15ReleaseKeyboardEP10CGuiObject";
    /// <summary><c>CGuiObject* CGuiMan::GuiObjectHitCheck(int x, int y, int fresh, int mask)</c>: the engine's own
    /// "which GUI object is under this point" test (#451). fresh=0 returns the object cached at CGuiMan+0x78 when one
    /// is (x, y and mask then ignored); the mask only filters the secondary layer list, not the primary scene hit.
    /// Pass it exactly as the engine's own handlers do: press (mouseX, mouseY, 0, 1), wheel (…, 0, 8). Same on both builds.</summary>
    public const string GuiObjectHitCheck = "_ZN7CGuiMan17GuiObjectHitCheckEiiii";
    /// <summary>CTlkTable* global; the <c>this</c> for <see cref="TlkGetSimpleString"/>.</summary>
    public const string TlkTable   = "g_pTlkTable";
    /// <summary>The client's nuklear context (NUI edit-focus probe). Client global -- no Windows anchor.</summary>
    public const string Nuklear    = "g_pNuklear";
    /// <summary>CExoBase* global; deref for the instance whose CExoString@0x38 is the resolved user directory.</summary>
    public const string ExoBase    = "g_pExoBase";
    /// <summary>#571: <c>CExoConfig::Get&lt;T&gt;(std::string const&amp;, bool)</c> @0x4efce0 / @0x4ef6a0 / @0x4f9120 --
    /// the stock <c>settings.tml</c> reader. <c>this</c> is CExoBase's first member (<see cref="EngineLayout.ExoBaseConfig"/>);
    /// the key is a <c>std::string</c> by const reference (a pointer on both ABIs, forged per platform by
    /// <see cref="StockConfig"/>); the bool picks the committed table over the working one. A key with no
    /// bind, or a bind of another type, answers the type's zero -- so a caller names the bound type, never
    /// guesses it. Allocates (map lookup, cpptoml substrings): cold reads only. Windows exports the same
    /// three instantiations (<c>_N</c>, <c>_J</c> = the 64-bit long, <c>N</c>).</summary>
    public const string ExoConfigGetBool   = "_ZN10CExoConfig3GetIbEET_RKNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEEEb";
    public const string ExoConfigGetLong   = "_ZN10CExoConfig3GetIlEET_RKNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEEEb";
    public const string ExoConfigGetDouble = "_ZN10CExoConfig3GetIdEET_RKNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEEEb";

    // ---- owned-render text (chatbox slice #19). Linux-only throwaway rig; no Windows anchors
    //      (Resolve returns Zero + logs on Windows, so the feature degrades gracefully there). ----
    // stb_truetype: the client statically links stb and exports every entry point as extern "C".
    public const string StbttInitFont              = "stbtt_InitFont";
    public const string StbttScaleForPixelHeight   = "stbtt_ScaleForPixelHeight";
    public const string StbttGetFontVMetrics       = "stbtt_GetFontVMetrics";
    public const string StbttGetCodepointHMetrics  = "stbtt_GetCodepointHMetrics";
    public const string StbttGetCodepointBitmapBox = "stbtt_GetCodepointBitmapBox";
    public const string StbttMakeCodepointBitmap   = "stbtt_MakeCodepointBitmap";
    public const string StbttFindGlyphIndex        = "stbtt_FindGlyphIndex";
    // The client runs an OpenGL 3.3 CORE context (verified: SDL_GL_SetAttribute PROFILE_MASK=CORE),
    // so the owned draw is shader+VAO+VBO. Modern GL (>=1.3) resolves through this exported SDL entry.
    public const string SdlGlGetProcAddress = "SDL_GL_GetProcAddress";
    // RenderInterface blend/depth wrappers (static; args in edi/esi, no `this`). We route only these
    // through the engine so its enable-cache stays in sync; matrices are ours (software stack -> shader
    // uniforms in the engine, useless for a raw draw). Immediates read from PrintStringScreenPos (0x4351f0).
    public const string RiEnable       = "_ZN15RenderInterface6EnableEN6Aurora17AuroraEnableEnumsE";
    public const string RiDisable      = "_ZN15RenderInterface7DisableEN6Aurora17AuroraEnableEnumsE";
    public const string RiSetBlendFunc = "_ZN15RenderInterface12SetBlendFuncEN6Aurora16AuroraBlendModesES1_";
    /// <summary>RenderInterface::IsEnabled(AuroraEnableEnums) @0x45cf30 -- read an Aurora render cap's cached
    /// enable bit. Cap 3 = GL_CULL_FACE (verified: GLRender::Enable(3) -&gt; glEnable(0xb44)). Used to save/
    /// restore cull around the two-sided shadow depth pass.
    /// <para>Cap 5 = LIGHTING, and it is the modern renderer's lit predicate: <c>PartTriMesh::MatStatic</c>
    /// (@0x450560) sends flat white instead of the material ambient/diffuse/emissive when it reads false, and
    /// <c>RenderFlat</c> (@0x450eb0) drops the cap around a <c>mat+0x98</c> material.</para></summary>
    public const string RiIsEnabled    = "_ZN15RenderInterface9IsEnabledEN6Aurora17AuroraEnableEnumsE";
    /// <summary>#210 (G1): RenderInterface::ResetRenderStates() @0x45d330 -- a thin static thunk to
    /// <c>GLRender::ResetRenderStates()</c> @0x4d1810 (no <c>this</c>). It force-applies the engine's default
    /// render state to real GL *and* rewrites every shadow-cache global that mirrors it -- framebuffer binding,
    /// default VAO, the enable set, depth/colour/stencil masks, blend func, fill mode, lights, material, fog,
    /// current program, active texture unit and the bound-texture table. It does NOT touch the viewport.
    /// <para>The engine already calls it once per frame from <c>GLRender::SwapBuffers</c> @0x4d2070, right after
    /// <c>SDL_GL_SwapWindow</c>, so the per-frame cost is proven and the C-heap work inside it is engine-side
    /// (not NativeAOT GC). We call it a second time on hand-back so the resync is OURS rather than incidental
    /// to where the engine happens to have put its own.</para></summary>
    public const string RiResetRenderStates = "_ZN15RenderInterface17ResetRenderStatesEv";
    /// <summary>#210: RenderInterface::BindBuffer(target, name) @0x45d190 -- a static thunk to
    /// <c>GLRender::BindBuffer</c> @0x4d04a0, which is a CACHING wrapper: for GL_ARRAY_BUFFER it compares the
    /// requested name against <c>GLRender::m_nArrayBufferBound</c> (@0x4d04d7) and returns without calling
    /// glBindBuffer on a match. <c>ResetRenderStates</c> does not reset that global -- array and uniform
    /// buffer bindings are the two GL_* bindings outside its write set -- so the hand-back has to route one
    /// bind through this wrapper to bring cache and GL back into agreement.</summary>
    public const string RiBindBuffer = "_ZN15RenderInterface10BindBufferEjj";
    /// <summary>#237: <c>GLRender::m_boundTextures</c> @0x19ad0e0 -- the engine's per-stage bound-texture
    /// CACHE, 16 entries of <c>{AuroraEnableEnums target, GLuint name}</c> (8 bytes each), with
    /// <c>m_activeTextureStage</c> @0x19ad0d8 and <c>m_bEnabledTextureStages</c> @0x19ad0d4 beside it.
    /// <para><b>Why a renderer that draws in its OWN context has to care.</b>
    /// <c>RenderInterface::SetActiveTextureAndBind</c> @0x45ce10 returns WITHOUT calling
    /// <c>glActiveTexture</c> or <c>glBindTexture</c> when the requested (target, name) already matches the
    /// cached entry. GL texture bindings are per-context, so any engine bind wrapper Alloy calls from inside
    /// its 4.6 bracket writes this cache while the real bind lands in the WRONG context -- and the engine's
    /// next matching bind then early-outs and draws with whatever its own context still has. Snapshot these
    /// three across any such call; <c>OwnedTextures</c> avoids the problem the other way, by binding
    /// raw.</para></summary>
    public const string GlBoundTextures = "_ZN8GLRender15m_boundTexturesE";
    public const string GlActiveTextureStage = "_ZN8GLRender20m_activeTextureStageE";
    public const string GlEnabledTextureStages = "_ZN8GLRender23m_bEnabledTextureStagesE";
    /// <summary>#220: RenderInterface::DeleteTextures(n, names) @0x45d1d0 -- the SINGLE funnel through which
    /// every engine GL texture name dies. Its callers are <c>CAurTextureBasic::~CAurTextureBasic</c>,
    /// <c>::Reset</c>, <c>::glImageBasic</c> (the re-upload path), <c>AurTextureFreeAll</c>,
    /// <c>Camera::ClearFrameBufferResources</c> and the movie/nuklear teardowns. #211 keys its owned copies
    /// on the engine name, and this is the exact moment a name stops meaning what was copied from it --
    /// including on an area transition, which fires no other edge Alloy watches.</summary>
    public const string RiDeleteTextures = "_ZN15RenderInterface14DeleteTexturesEjPKj";
    /// <summary>#259: RenderInterface::CreateTexture(CAurTexture*, bool, int) @0x45d200 -- the engine's
    /// per-cell texture UPLOAD, and the only window in which decoded pixels exist. Ghidra: its one caller is
    /// <c>CAurTextureBasic::glImageBasic</c> @0x48bcf0, which binds cell <c>i</c>'s GL name and calls this
    /// once per cell of the <c>numX*numY</c> grid; <c>GLRender::CreateTexture</c> @0x4ca2b0 beneath it does
    /// the sub-image split, the over-MAX_TEXTURE_SIZE box filter, the format-table lookup and the
    /// <c>glTexImage2D</c>/<c>glCompressedTexImage2D</c> upload -- all into whatever is bound to
    /// <c>GL_TEXTURE_2D</c> on unit 0. <c>CAurTextureBasic::FreeImage</c> releases the decoded pixels as
    /// soon as the loop finishes, so this is the whole window.
    /// <para>ABI: <c>(CAurTexture* /*rdi*/, bool useSubImage /*sil*/, int cell /*edx*/)</c>. The bool picks
    /// <c>glTexSubImage2D</c> (assumes storage) over <c>glTexImage2D</c> (allocates); a fresh texture always
    /// needs the allocating form.</para></summary>
    public const string RiCreateTexture = "_ZN15RenderInterface13CreateTextureEP11CAurTexturebi";
    /// <summary><c>g_bGeneratePLTOnGPU</c> @0x19abd7c (B, a 4-byte int) -- does the engine composite PLT
    /// textures on the GPU? PLT is the LAYERED, colourable format every player character's body uses: skin,
    /// hair, metal, cloth and leather channels tinted per character.
    /// <para>Non-zero takes those textures around <see cref="RiCreateTexture"/> entirely, so Alloy never
    /// sees decoded pixels and cannot own them. Both readers agree on the shape:
    /// <c>ConstructImage</c> @0x48a665 runs <c>ImageDecode</c> (the CPU composite, into 4-byte pixels) only
    /// under <c>== 0</c>, and <c>glImageBasic</c> @0x48bd54 gates the ordinary GenTextures/upload path on
    /// <c>(flag == 0 || tex+0x84 != 4)</c> -- <c>+0x84 == 4</c> being the PLT marker. That disjunction is
    /// precisely why non-PLT models are Alloy's and PLT ones are not.</para>
    /// <para>Written by a settings callback @0x8ca140 (<c>g_bGeneratePLTOnGPU = *(byte*)arg2</c>) -- its one
    /// writer, and an options-apply fires it and rebuilds every PLT texture in the same engine step. #358:
    /// which is why the flag is held at the two readers below, not merely re-asserted per frame.</para></summary>
    public const string PltOnGpu = "g_bGeneratePLTOnGPU";
    /// <summary><c>CAurTextureBasic::ConstructImage()</c> @0x48a5d0 -- the first of the two
    /// <see cref="PltOnGpu"/> readers, and the one that decides whether the PLT composite happens on the CPU
    /// at all. Hooked only to hold that flag at zero across the call: the settings callback @0x8ca140 writes
    /// it and the engine rebuilds every PLT texture in the SAME step (options-apply), which a per-frame
    /// re-assert is one frame too late for.
    /// <para>ABI: <c>(CAurTextureBasic* /*rdi*/)</c>, no return.</para></summary>
    public const string AurTextureConstructImage = "_ZN16CAurTextureBasic14ConstructImageEv";
    /// <summary><c>CAurTextureBasic::BuildAndStoreTexture()</c> @0x48b320 -- the inner build: ConstructImage (the
    /// resource read via AurGetImageWrapper and the decode) unless +0xe6, glImageBasic (the upload) unless the
    /// image is null, the alpha mean, FreeImage. Skipped outright when +0xe8 is set. ADR 0023 hooks it to stand a
    /// resident texture in for the whole of that. ABI: <c>(CAurTextureBasic* /*rdi*/)</c>, no return.</summary>
    public const string AurTextureBuildAndStoreTexture = "_ZN16CAurTextureBasic20BuildAndStoreTextureEv";
    /// <summary><c>CAurTextureBasic::glImageBasic(bool)</c> @0x48bcf0 -- the other <see cref="PltOnGpu"/>
    /// reader, and the one that routes a PLT texture (<c>+0x84 == 4</c>) around the upload funnel. Hooked
    /// for the same reason and on the same terms as <see cref="AurTextureConstructImage"/>.
    /// <para>ABI: <c>(CAurTextureBasic* /*rdi*/, bool useSubImage /*sil*/)</c>, no return.</para></summary>
    public const string AurTextureGlImageBasic = "_ZN16CAurTextureBasic12glImageBasicEb";
    /// <summary>The engine's <c>operator new[]</c> -- what ADR 0023's warm hand-back allocates the cell-name
    /// array with, because <c>CAurTextureBasic::Reset</c> frees it with the engine's <c>delete[]</c>. #461:
    /// Windows resolves it to the exported <c>MaxTree::operator new</c> @0x8a060, which is <c>_malloc_base</c>
    /// on the same statically linked UCRT heap that <c>Reset</c>'s <c>_free_base</c> @0xc10370 returns to.
    /// Unresolved here meant every Windows build was cold, which is the churn behind #461.</summary>
    public const string OperatorNewArray = "_Znam";

    // ---- shadow replacement (#65). Linux-only; no _win entries -> skip-with-log on Windows. ----
    // The three self-contained shadow sub-passes Scene::RenderShadows dispatches. No-op'ing these
    // suppresses stencil shadows while RenderShadows keeps managing all pipeline state itself
    // (its post-skip net state is runtime-dependent -- see docs/research/shadow-rendering-impl.md).
    /// <summary>Scene::RenderDynamicShadows() @ 0x4670b0 -- per-frame dynamic shadow-volume sub-pass.</summary>
    public const string RenderDynamicShadows = "_ZN5Scene20RenderDynamicShadowsEv";
    /// <summary>Scene::RenderStaticShadows() @ 0x46a3a0 -- static (world-geometry) shadow-volume sub-pass.</summary>
    public const string RenderStaticShadows  = "_ZN5Scene19RenderStaticShadowsEv";
    /// <summary>CAurScene::ComputeStaticLighting() @0x465590 -- the engine's own "the static lighting changed"
    /// signal (#664 §A.6): called after a script toggles a placeable light. The lightmap's re-classify edge
    /// (#674). <c>(this) -> void</c>, virtual (Scene vtable 0x68 Linux / 0x60 Windows; hooked by name).</summary>
    public const string ComputeStaticLighting = "_ZN9CAurScene21ComputeStaticLightingEv";
    /// <summary>Scene::RenderShadowPlanes(int, int) @ 0x463e20 -- planar/projected shadow fallback sub-pass.</summary>
    public const string RenderShadowPlanes   = "_ZN5Scene18RenderShadowPlanesEii";
    /// <summary>Scene::DoGobShadows(PartLight*) @ 0x45fba0 -- per-light gob volume builder, dispatched
    /// directly by RenderShadows (Scene-vtable +0x1c8) alongside the Render* sub-passes.</summary>
    public const string DoGobShadows         = "_ZN5Scene12DoGobShadowsEP9PartLight";
    /// <summary>Scene::DoDynamicMeshProjections(PartLight*) @ 0x45f2e0 -- per-light dynamic mesh shadow
    /// builder, dispatched directly by RenderShadows (Scene-vtable +0x1e0).</summary>
    public const string DoDynamicMeshProjections = "_ZN5Scene24DoDynamicMeshProjectionsEP9PartLight";

    // ---- shadow-map caster re-drive (#65 slice 3b). The frame slot + the geometry submission. ----
    /// <summary>Scene::RenderDrawBucket(int bucketId) @ 0x46d060 -- draws one geometry bucket (the whole
    /// iterate+SetModelMatrix+RenderVertexData loop, DrawBucketManager @Scene+0x208). Ids 1..8 (RenderSinglePass);
    /// bucket 1 = opaque casters. Called directly to render casters into our depth FBO. ABI: <c>(Scene* /*rdi*/,
    /// int bucketId /*esi*/)</c>.</summary>
    public const string RenderDrawBucket = "_ZN5Scene16RenderDrawBucketEi";
    /// <summary>Scene::RenderSinglePass() @0x46e5f0 -- one whole per-view colour pass (buckets 0..8, sky,
    /// lights); runs for the main view AND each reflection subframe, with view/proj already on the matrix
    /// stacks at entry. The M1 tracer (#81) hooks it to run the owned opaque block before buckets 0/1.</summary>
    public const string RenderSinglePass = "_ZN5Scene16RenderSinglePassEv";
    /// <summary>Scene::FadeDoors() @0x467990 -- vtable slot 0x260, the LAST engine call inside
    /// <c>Scene::RenderSinglePass</c> before <c>RenderDrawBucket(0)</c>. #132's frame anchor.
    /// <para>Why here and not at RenderSinglePass entry, which is what spec #114 asks for: RSP's first act is
    /// <c>Camera::InitializeFrameBufferResources</c> @0x4a6fd0, and its steady-state path (the size compare at
    /// +0x240 falls through to +0xed) is <c>SetRenderSubframeTarget(0)</c> then <c>Clear(7)</c> -- EVERY frame,
    /// not only on resize. So at literal entry the world FBO is neither bound nor cleared, and a resolve
    /// written there is wiped by that clear; deferring the resolve past it costs a second colour bracket,
    /// which ADR 0011 does not have. Anchoring after the clear makes the problem disappear rather than need
    /// suppressing. The two Scene vcalls between the clear and bucket 0 are <c>DoFog</c> (slot 0x280) and this
    /// one, and both write state the owned pass reads -- scene fog (#124) and the per-tile fade factor -- so
    /// the anchor sits after them rather than before. Unconditional in the decompile, and reached only
    /// through the vtable, so no other caller exists to gate against.</para></summary>
    public const string FadeDoors = "_ZN5Scene9FadeDoorsEv";
    /// <summary>GLRender's matrix-stack array <c>m_aurMtxStack</c> @0x19e2f60, indexed by AuroraTransStates
    /// (PROJECTION=0, VIEW=1, ...), stride 0x58 per stack: a std::vector&lt;Matrix4x4&gt; (0x18 bytes of
    /// pointers) then the TOP matrix as row-major float[16] at +0x18 (Ghidra GLRender::PushMatrix /
    /// PopMatrix / aurMatrixStack::SetIdentity). Data symbol -- read the tops, never write.</summary>
    public const string MAurMtxStack = "m_aurMtxStack";
    /// <summary>GLRender::RenderVertexData(VertexDataObject*, uint item, AuroraPrimitiveTypes, uint size,
    /// uint offset) @0x4d0570 -- the single GL draw-submit funnel (glDrawElementsBaseVertex for indexed VDO
    /// modes 1/2, glDrawArrays otherwise; picks + sets the program from VDO attributes first). Static member:
    /// first arg IS the VDO, no <c>this</c>. Hooked by DrawStatsService (ADR 0006 phase 0) for the per-frame
    /// draw histogram.</summary>
    public const string GlRenderVertexData = "_ZN8GLRender16RenderVertexDataEP16VertexDataObjectjN6Aurora20AuroraPrimitiveTypesEjj";
    /// <summary>#215: GLRender::RenderVertexStreamData(VertexStreamDataObject*, AuroraPrimitiveTypes, uint,
    /// uint) @0x4d0ca0 -- the engine's SECOND and last draw funnel. Established by enumerating the binary's
    /// GL draw imports rather than assumed: nwmain imports exactly <c>glDrawArrays</c> and
    /// <c>glDrawElements</c>, and their only call sites are @0x4d05ff (<see cref="GlRenderVertexData"/>),
    /// @0x4d0d13/@0x4d0d46 (here) and @0xe0805a (<c>nk_sdl_render</c>, the nuklear 2D overlay, outside any
    /// Scene pass). So these two symbols ARE the engine's 3D draw surface, which is what lets the #215 shim
    /// counter read zero as a measurement instead of an inspection.
    /// <para>ABI: static, no <c>this</c> -- read off the entry @0x4d0ca0, which tests <c>rdi</c> for null
    /// and hands it straight to <c>GLRender::SetVertexStreamDataObject(VertexStreamDataObject*)</c>, with
    /// prim/size/offset in <c>esi</c>/<c>edx</c>/<c>ecx</c>. Checked rather than inherited from
    /// <see cref="GlRenderVertexData"/>'s: a shifted argument here would fault on the menu's debug boxes,
    /// long before any world frame.</para></summary>
    public const string GlRenderVertexStreamData = "_ZN8GLRender22RenderVertexStreamDataEP22VertexStreamDataObjectN6Aurora20AuroraPrimitiveTypesEjj";
    /// <summary>#215: Gob::CollectLights(List&lt;PartLight*&gt;&amp;) @0x43fa90 -- half of the
    /// <c>focuslighting</c> write the shim has to replicate. ABI: <c>(Gob* /*rdi*/, List* /*rsi*/)</c>.</summary>
    public const string GobCollectLights = "_ZN3Gob13CollectLightsER4ListIP9PartLightE";
    /// <summary>#215: <c>Scene::m_pFocusObject</c> @0x19ab960 -- the Gob whose lighting
    /// <c>focuslighting</c> describes. Data symbol; read only.</summary>
    public const string SceneFocusObject = "_ZN5Scene14m_pFocusObjectE";
    /// <summary>#215: the <c>focuslighting</c> global @0x19ab748 -- three floats.
    /// <c>Scene::RenderSinglePass</c> is its ONLY writer (the two stores at 0x46ec3d and 0x46fe64 are the
    /// same block with and without the Tracy scope), and the exported
    /// <c>Scene::GetLightingValueAtFocus</c> @0x462aa0 reads it -- so the mutate-only shim owes it a write.
    /// Data symbol; the shim writes it exactly where RSP did.</summary>
    public const string FocusLighting = "focuslighting";
    /// <summary>#235: <c>g_bFullDynamicLighting</c> @0x19ab8a4 -- the int that decides which of
    /// <c>LightManager</c>'s two enable walks <c>Scene::RenderSinglePass</c> runs. Non-zero on this client
    /// (FRAGMENT_LIGHTING=1), so <c>EnableLights</c> is the live arm and <c>EnableLightsDynamicOnly</c> is
    /// dead. Read, never written -- the shim replicates RSP's gate rather than assuming its value.</summary>
    public const string FullDynamicLighting = "g_bFullDynamicLighting";
    /// <summary>#233: <c>triggerbucket</c> @0x19abbd0 -- the trigger draw bucket, a
    /// <c>CExoArrayList&lt;Part*&gt;</c> whose COUNT sits at <c>+8</c> (the <c>DAT_01aabbd8</c> that
    /// <c>Scene::DoTriggerBucket</c> and <c>Scene::DoHighlightTriggers</c> both gate on). Refilled by
    /// <c>Scene::AddPartsToDrawBuckets</c> before RSP and reset by <c>ClearBuckets</c> after, so it is
    /// already correct when the shim reads it. Data symbol; read only.</summary>
    public const string TriggerBucket = "triggerbucket";
    /// <summary>#330: <c>gobbucket</c> @0x19abbf0 -- the engine's per-frame VISIBLE gob list, a
    /// <c>CExoArrayList&lt;Gob*&gt;</c> (items at +0, count at +8). <c>ManageSceneBSP</c> @0x47fa80 refills it from
    /// the Scene's gobs through <c>Gob::IsInViewVolume</c>, and <c>Scene::AddPartsToDrawBuckets</c> @0x4751d0
    /// walks it into <c>AddPartsTreeToDrawBuckets</c> @0x460880 -- the ONLY writer of an instanced part's
    /// VertexPrimitive matrix. So at the FadeDoors anchor it is exactly the set of gobs whose parts can have
    /// moved this frame. Data symbol; read only.</summary>
    public const string GobBucket = "gobbucket";

    /// <summary>#336: the seven Tab-highlight colours. <c>HiliteAreaObjects</c> @0x7119f0 picks one of these
    /// by object type and hands it to the object's highlight virtual, which is what ends up in
    /// <c>Scene::AddHighlightGob(gob, Vector&amp;)</c> -- the ONLY two things the highlight list carries. A
    /// highlight arriving in some other colour did not come from the Tab path.</summary>
    /// <summary>#336: <c>CNWCAnimBase::RefreshHilite()</c> @0x9cb260 -- the ONE funnel every highlight
    /// passes through. Tab ("highlight all") and a mouseover ("highlight this object") both reach
    /// <c>CNWCObject::Hilite</c> -&gt; <c>CNWCAnimBase::Hilite</c>, which writes a channel slot and calls this;
    /// so does every VFX that highlights. This recomputes the winner and is what calls
    /// <c>Scene::AddHighlightGob</c>, so an After hook here sees the engine's own verdict rather than
    /// re-deriving it -- and takes a single pointer, dodging the by-value Vector in Hilite's ABI.</summary>
    public const string RefreshHilite = "_ZN12CNWCAnimBase13RefreshHiliteEv";
    /// <summary>#330: <c>fadingoutbucket</c> @0x19abba0 and <c>fadinginbucket</c> @0x19abbb0 -- the tiles whose
    /// fade factor is being stepped this frame. <c>HandleFadingOutBucket</c> @0x47f870 /
    /// <c>HandleFadingInBucket</c> @0x47f670 write <c>TileData+0x74</c> for each member from
    /// <c>ManageSceneBSP</c>, which commits +0x74 into the live +0x70 at its top on the next frame. Same
    /// list layout as <see cref="GobBucket"/>. Data symbols; read only.</summary>
    public const string FadingOutBucket = "fadingoutbucket", FadingInBucket = "fadinginbucket";
    // ---- the engine draw funnel Alloy sits on, Linux-only -> DrawFunnelService stands down on Windows ----
    /// <summary>#125: Scene::RenderSkyBoxes() @0x466910 -- the whole per-area skybox draw, called from
    /// RenderSinglePass between buckets 0 and 1. Three gobs (current, cross-fading, skyfade) rendered under
    /// their own projection with the depth mask off. #261: NOT hooked. The shim calls it for its
    /// bookkeeping -- ProcessGob, node animation, the cross-fade alpha writes and the per-gob
    /// <c>SetPosition(Scene+0x190)</c> -- and Alloy enrols the gobs itself. ABI: <c>(Scene* /*rdi*/)</c>.</summary>
    public const string RenderSkyBoxes = "_ZN5Scene14RenderSkyBoxesEv";
    /// <summary>#261: Part::GetWorld(Vector&amp;, Quaternion&amp;) @0x437930 -- composes a part's world
    /// transform by walking <c>part+0x48</c> to the root and then applying the owning Gob's adjusted
    /// scale/orientation/position. No Part subclass overrides vtable slot 0x20 (checked against every
    /// <c>_ZTV4Part*</c>), so it is safe to call by symbol. OUT PARAMETERS, which is why the sky can call it
    /// at all: the engine's own by-value <c>Vector</c>/<c>Quaternion</c> returns are an ABI this side does
    /// not invent. ABI: <c>(Part* /*rdi*/, Vector* /*rsi*/, Quaternion* /*rdx*/)</c>.</summary>
    public const string PartGetWorld = "_ZN4Part8GetWorldER6VectorR10Quaternion";
    /// <summary>#261: Gob::GetAdjustedScale() @0x437cf0 -- <c>LerpFloat(gob+0x1c0).Lerped() *
    /// *(float*)(gob+0x49c)</c>, the uniform scale <c>Gob::Render</c> pushes and <see cref="PartGetWorld"/>
    /// applies to the position but does not hand back. A plain float return, so it is callable.
    /// ABI: <c>(Gob const* /*rdi*/) -&gt; float</c>.</summary>
    public const string GobGetAdjustedScale = "_ZNK3Gob16GetAdjustedScaleEv";

    // ---- #130/#261: the beam globals. Beams are NOT walked by Scene::RenderDrawBucket -- they live in
    //      DRAW BUCKET 15, which Scene::RenderBeams @0x46aba0 (vt+0x1d8) walks with its own program and its
    //      own state. Alloy enumerates that bucket itself (Beams.Enroll); the vtable slot is the shim's.
    /// <summary>Global <c>enablebeams</c> @0x18aa18c (int, 1 by default) -- the flag
    /// <c>Scene::RenderBeams</c> gates its whole body on. The owned pass honours it: beams off means the
    /// owned draw does nothing AND does not suppress.</summary>
    public const string EnableBeams = "enablebeams";
    /// <summary>Global <c>projectionalpha</c> @0x18aa188 (float, 0.2) -- the alpha
    /// <c>Scene::RenderBeams</c> homogenizes onto a white RGB and pushes as <c>globalColor</c>. Read live so
    /// the console variable keeps meaning what it says.</summary>
    public const string ProjectionAlpha = "projectionalpha";

    // ---- #131: the emitter site. Particles are NOT draw-bucket residents either -- Scene::DoEmitterBucket
    //      @0x476f00 (step 28, dead last in Scene::RenderSinglePass) animates each emitter, depth-sorts the
    //      visible ones back-to-front, then calls each one's own render pointer, which funnels into
    //      PartEmitter::RenderEmitter. There is no bindable buffer: the vertex build is CPU-side into one
    //      SHARED VertexStreamDataObject, committed and drawn once per emitter.
    /// <summary>#131: PartEmitter::RenderEmitter() @0x4ad4c0 -- the whole per-emitter draw. Builds 6 verts
    /// per particle (colour u32, position vec3, uv vec2 -- 24-byte stride) into the shared stream object,
    /// <c>glBufferSubData</c>s the whole range and issues one <c>glDrawArrays(GL_TRIANGLES, 0, 6N)</c>.
    /// <c>RenderStandard</c>/<c>RenderLinked</c>/<c>RenderMotionBlurred</c> are one-line wrappers that all
    /// funnel here; <c>RenderNothing</c> bypasses it, which is correct. Hooked as both the capture point and
    /// the per-emitter suppression point. ABI: <c>(PartEmitter* /*rdi*/)</c>.</summary>
    public const string RenderEmitter = "_ZN11PartEmitter13RenderEmitterEv";
    /// <summary>#131: PartEmitter::~PartEmitter() @0x4af890 (D1 and D2 share the address; the deleting D0
    /// @0x4afa60 tail-calls it, and every emitter subclass's dtor reaches it). #145's lesson applied to
    /// emitters: the owned replay reads a live Particle array off the emitter one frame after the capture,
    /// so the one event that says a captured emitter is GONE has to evict it.
    /// ABI: <c>(PartEmitter* /*rdi*/)</c>.</summary>
    public const string PartEmitterDtor = "_ZN11PartEmitterD2Ev";
    /// <summary>#131: PartEmitter::GetTileAlpha() @0x4ac300 -- one of the two factors the engine folds into
    /// every particle's alpha. Called rather than reproduced. ABI: <c>(PartEmitter* /*rdi*/) -&gt; float</c>.
    /// </summary>
    public const string EmitterGetTileAlpha = "_ZN11PartEmitter12GetTileAlphaEv";
    /// <summary>#131: PartEmitter::GetGobAlpha() @0x4ac2d0 -- the other one. Same treatment.
    /// ABI: <c>(PartEmitter* /*rdi*/) -&gt; float</c>.</summary>
    public const string EmitterGetGobAlpha = "_ZN11PartEmitter11GetGobAlphaEv";
    /// <summary>#131: MdlNodeEmitter::IsSplat() @0x4ab070 -- the model flag that turns an emitter's quads
    /// into ground splats: the billboard basis comes off the particle's own splat normal, size stops
    /// interpolating, and the softness term is suppressed.
    /// ABI: <c>(MdlNodeEmitter* /*rdi*/) -&gt; bool</c>.</summary>
    public const string EmitterIsSplat = "_ZN14MdlNodeEmitter7IsSplatEv";
    /// <summary>#131: MdlNodeEmitter::IsTinted() @0x4aafc0 -- the flag that runs the emitter's start/end
    /// colours through the scene lighting at its own position before interpolation.
    /// ABI: <c>(MdlNodeEmitter* /*rdi*/) -&gt; bool</c>.</summary>
    public const string EmitterIsTinted = "_ZN14MdlNodeEmitter8IsTintedEv";
    /// <summary>#131: LightManager::GetEmitterLighting(Vector, List&lt;PartLight*&gt;*) @0x4c1970 -- the tint
    /// lookup, one call per tinted emitter per frame.
    /// <para>ABI, read off the call site at 0x4af130 rather than off the decompiler (Ghidra renders the

    /// <c>xmm0</c> (x, y) + <c>xmm1</c> (z), the light list in <c>rdi</c>, a zero int in <c>esi</c>, and the
    /// result comes back the same way it went in -- <c>xmm0</c> (r, g) + <c>xmm1</c> (b). That is exactly
    /// SysV's classification of a 12-byte three-float struct, so a plain sequential struct of three floats
    /// matches it on both ends.</para></summary>
    public const string GetEmitterLighting = "_ZN12LightManager18GetEmitterLightingE6VectorP4ListIP9PartLightE";
    /// <summary>LightManager::GetLensFlareLights() @0x4c1d80 -- the list (items at +0, count at +8) of every
    /// light node with <c>lensflares 1</c>; <c>Scene::RenderLensFlares</c> is its one consumer. Read by <see cref="Halos"/>.</summary>
    public const string GetLensFlareLights = "_ZN12LightManager18GetLensFlareLightsEv";
    /// <summary>#131: the <c>currentscene</c> global @0x19ab950 -- <c>RenderEmitter</c> reads the light list
    /// for the tint at <c>*currentscene + 0x158</c>. Read live at build time for the same reason
    /// <see cref="Beams"/> reads its own Scene: an area transition frees the list.</summary>
    public const string CurrentScene = "currentscene";
    /// <summary>#148: CAurTexture::BindInUnit(unsigned int, int) @0x48e1e0 -- the engine's OWN texture bind,
    /// and the reason a raw <c>glBindTexture</c> of <c>AurTextureGetTID</c>'s answer is not equivalent.
    /// <para>It resolves the object TWICE before taking an id: vtable+0x18 hands back a REFERENCED texture
    /// (read straight off <c>this+0x18</c> when that slot is the stock <c>GetReferencedTexture</c>), and
    /// vtable+0x10 resolves that in turn. Only then does it call <c>AurTextureGetTID</c> -- on the resolved
    /// object, not on the one the Material handed out. It also passes vtable+0x20's flag to
    /// <c>AurTextureBindTIDInUnit</c> and ticks the texture's animation controller, neither of which a raw
    /// bind does.</para>
    /// <para>Call it rather than reproducing it: the chain is three virtuals and a NULL-texture sentinel
    /// check, and every one of them is a place for a reproduction to drift.
    /// ABI: <c>(CAurTexture* /*rdi*/, uint unit /*esi*/, int frame /*edx*/)</c>; the engine passes -1 for
    /// the frame, meaning "whatever the controller says".</para></summary>
    public const string AurTextureBindInUnit = "_ZN11CAurTexture10BindInUnitEji";
    // ---- #127: the grass site. Grass is NOT a draw-bucket resident -- Scene::RenderSinglePass calls
    //      Scene::DoBinBucket @0x474c50 (vt+0x1c0) under the engine's own Tracy scope "Grass", between the
    //      sky and RenderDynamicGeometry. That placement, plus a depth mask it never sets and a distance
    //      fade pushed through the alpha channel the 0.2 cutout reads, IS #106's punch-through.
    /// <summary>Scene::DoBinBucket() @0x474c50 -- the whole grass draw: the per-frame CPU wind rewrite over
    /// EVERY bin in the area, an optional bin sort, then one RenderVertexData per (bin, material) triangle
    /// list with no frustum culling. Skipped outright on the passes the owned grass drew, which is what
    /// retires the wind rewrite as well as the draw. ABI: <c>(Scene* /*rdi*/)</c>.</summary>
    public const string DoBinBucket = "_ZN5Scene11DoBinBucketEv";
    /// <summary>Scene::ReloadGrass() @0x469540 -- rebuilds every bin from the tile BSP (area load, grass
    /// option change, a server grass override). Every CAurTriangleBin and CAurTriangleList the owned pass
    /// captured is freed here, so it is the one signal that invalidates the whole capture.
    /// ABI: <c>(Scene* /*rdi*/)</c>.</summary>
    public const string ReloadGrass = "_ZN5Scene11ReloadGrassEv";
    /// <summary>Global <c>binbucket</c> @0x19abbc0 -- the BSP visibility walk's active-bin list. The
    /// symbol's address IS the list: <c>CAurTriangleBin**</c> at +0, count at +8. Rebuilt per frame, so a
    /// bin that stops appearing has simply gone out of view (or been freed) and needs no other liveness
    /// test.</summary>
    public const string BinBucket = "binbucket";
    // ---- #353: the area border ring. CNWCArea::LoadBorder @0x70a5c0 walks four strips of edge tiles
    //      (-5..-1 past each edge) out of <tileset>_edge.2da and spawns each through CAurScene vt+0x38 =
    //      Scene::SpawnTile with the border flag set; ReloadBorder @0x711ff0 deletes (vt+0x58) and respawns
    //      them. LoadArea sizes the tile BSP for exactly that ring: InitBSP(w, h, 5, true, 10.0).
    /// <summary>CNWCArea::LoadBorder(CGuiAreaLoadscreen*, float) @0x70a5c0. ABI: <c>(CNWCArea* /*rdi*/,
    /// CGuiAreaLoadscreen* /*rsi*/, float progress /*xmm0*/) -> int</c>.</summary>
    public const string AreaLoadBorder = "_ZN8CNWCArea10LoadBorderEP18CGuiAreaLoadscreenf";
    /// <summary>CNWCArea::ReloadBorder() @0x711ff0 -- the server's tile-border toggle / tile change path
    /// (HandleServerToPlayerArea). ABI: <c>(CNWCArea* /*rdi*/)</c>.</summary>
    public const string AreaReloadBorder = "_ZN8CNWCArea12ReloadBorderEv";

    // ---- #615: the weather entry points. CNWCArea::SetWeather @0x702b10 is VIRTUAL (vtbl+0x60), and it is the
    //      one that carries state: (type, density 0..100). Its body deletes the six fx CAurObjects at area+0x258
    //      and the lightning one at +0x288 whenever density is 0 or the type changes, and re-creates them from
    //      weathertypes.2da "Model" when density > 0 -- so forwarding density 0 IS the vanilla-emitter silence.
    //      CNWCArea::Update ramps it: (target, (fade-left)/fade*100) up, (current, left/fade*100) down, then
    //      (target, target ? 100 : 0) once at the end. StartWeather/StopWeather @0x704de0/@0x704e20 only set the
    //      target byte (+0x2f1) and the 6000ms ramp (+0x2ec) and cue the rain sound; hooked for the edge log.
    /// <summary>CNWCArea::SetWeather(unsigned char type, float density). ABI: <c>(CNWCArea*, uchar, float)</c>
    /// -- Linux <c>rdi, esi, xmm0</c>; Win64 <c>rcx, dl, xmm2</c>. Returns int (1 = applied).</summary>
    public const string AreaSetWeather = "_ZN8CNWCArea10SetWeatherEhf";
    /// <summary>CNWCArea::StartWeather(unsigned char type). ABI: <c>(CNWCArea*, uchar)</c>.</summary>
    public const string AreaStartWeather = "_ZN8CNWCArea12StartWeatherEh";
    /// <summary>CNWTileSet* CNWTileSetManager::GetTileSet(CResRef const&amp;) -- the area load's one ask for its
    /// tileset, by name. ABI: <c>(CNWTileSetManager*, const CResRef*)</c>, a pointer return, so no sret on either build.</summary>
    public const string TileSetGet = "_ZN17CNWTileSetManager10GetTileSetERK7CResRef";
    /// <summary>CNWCArea::StopWeather(unsigned char). ABI: <c>(CNWCArea*, uchar)</c>; the byte is unused.</summary>
    public const string AreaStopWeather = "_ZN8CNWCArea11StopWeatherEh";
    /// <summary>Scene::InitBSP(int w, int h, int border, bool, float cell) @0x46c330 (Scene vt+0x30) -- the
    /// tile BSP covers <c>(w + 2*border) x (h + 2*border)</c> cells, and <c>SceneBSPGetTileData</c> CLAMPS a
    /// position outside it onto the edge leaf (BSPFitSphere), so a wider ring needs a wider border here.
    /// ABI: <c>(Scene* /*rdi*/, int /*esi*/, int /*edx*/, int /*ecx*/, bool /*r8b*/, float /*xmm0*/)</c>.</summary>
    public const string SceneInitBsp = "_ZN5Scene7InitBSPEiiibf";
    /// <summary>Scene::SpawnTile(char const* model, Vector const&amp; pos, int orientation, int border,
    /// Quaternion*) @0x474470 (Scene vt+0x38) -- every tile, real (border 0, from CNWCTile::LoadModel) or
    /// edge (border 1, from LoadBorder). Position is the tile centre: <c>index*10+5</c>. Returns the Gob*, null
    /// for an edge tile (SpawnPartsForTile builds a bare instance tree for those).
    /// ABI: <c>(Scene* /*rdi*/, char* /*rsi*/, Vector* /*rdx*/, int /*ecx*/, int /*r8d*/, Quaternion* /*r9*/) -> Gob*</c>.</summary>
    public const string SceneSpawnTile = "_ZN5Scene9SpawnTileEPKcRK6VectoriiP10Quaternion";
    /// <summary>Scene::DeleteTile(Vector const&amp;) @0x4636a0 (Scene vt+0x58). Dereferences the tile data at
    /// the position UNGUARDED (its neighbour unlink reads <c>tile+0xb8</c>), so never call it where
    /// <see cref="SceneBspGetTileData"/> is null. ABI: <c>(Scene* /*rdi*/, Vector* /*rsi*/)</c>.</summary>
    public const string SceneDeleteTile = "_ZN5Scene10DeleteTileERK6Vector";
    /// <summary>SceneBSPGetTileData(Scene*, Vector const&amp;) @0x47b480 -- the TileData* at a position, or
    /// null. ABI: <c>(Scene* /*rdi*/, Vector* /*rsi*/) -> TileData*</c>.</summary>
    public const string SceneBspGetTileData = "_Z19SceneBSPGetTileDataP5SceneRK6Vector";
    /// <summary>GLRender::GetPool(uint) @0x4cfb70 -- the pool's HOST shadow pointer (descriptor +0), a plain
    /// table read with no lock/unlock side effect. What <c>RenderInterface::LockPool(pool, 0, 0, 8)</c>
    /// hands the engine's own grass binning, minus the lock byte. ABI: <c>(uint /*edi*/) -> void*</c>.</summary>
    public const string GlGetPool = "_ZN8GLRender7GetPoolEj";
    /// <summary>Global <c>g_GrassInfo</c> @0x19acc90 -- <c>List&lt;CAurGrassInfo&gt;</c>: element pointer at
    /// +0, count at +8. One 0x54-byte record per grass material (name, density, height, diffuse, ambient,
    /// four atlas probabilities). Filled from the tileset's <c>.set</c> and from per-area / server
    /// overrides.</summary>
    public const string GrassInfo = "g_GrassInfo";
    /// <summary>Scene::m_fGrassFarRenderDistance @0x18aa1b4 -- the SQUARED far radius, and the player's own
    /// "Grass Far Render Distance" option (clamped to [900, 30000], i.e. 30..173 world units). The owned
    /// pass dithers to zero exactly at it, so the player's knob still means what it says.</summary>
    public const string GrassFarDistance = "_ZN5Scene25m_fGrassFarRenderDistanceE";
    /// <summary>CAurTriangleBin::m_fNearRadiusSquared @0x18aa3b0 -- squared radius inside which vanilla grass
    /// is at full alpha (225 = 15 units). The owned pass reuses it as the near edge of the dither ramp.</summary>
    public const string GrassNearRadius = "_ZN15CAurTriangleBin20m_fNearRadiusSquaredE";
    /// <summary>GLRender::m_lightAreaDiffuse / m_lightAreaAmbient / m_lightAreaDiffuseDirection
    /// @0x19af5f0 / 0x19af600 / 0x19af5e0 -- the three area-light uniforms as the ENGINE holds them.
    /// <c>SetAreaDiffuseLight</c> @0x4cbfc0 and <c>SetAreaAmbientLight</c> @0x4cbf60 store their argument
    /// into these unmodified (verified in asm: three <c>movss</c> and a dirty-sequence bump, no scale), and
    /// <c>GLRender::SetUniforms</c> uploads them. Alloy re-derives the same values off the PartLight rather
    /// than reading them; these are what that derivation is checked against.</summary>
    public const string LightAreaDiffuse = "_ZN8GLRender18m_lightAreaDiffuseE";
    public const string LightAreaAmbient = "_ZN8GLRender18m_lightAreaAmbientE";
    public const string LightAreaDiffuseDir = "_ZN8GLRender27m_lightAreaDiffuseDirectionE";

    /// <summary>Scene::m_bGrassIsEnabled @0x19ab970 -- the player's "Grass Mode" option, and the flag
    /// <c>DoBinBucket</c> gates its whole body on. The owned pass honours it: grass off means the owned
    /// draw does nothing AND does not suppress, so the option keeps meaning what it says.</summary>
    public const string GrassEnabled = "_ZN5Scene17m_bGrassIsEnabledE";
    /// <summary>#134: <c>PartDanglyMesh</c>'s vtable. Not a hook -- an identity. A dangly part reaches the
    /// <c>PartTriMesh::Draw</c> hook (its own Draw @0x451b89 calls straight into it) and must be told apart
    /// from a rigid one, which no field on the part answers. The Itanium ABI stores the address of the
    /// vtable's function-pointer area, 0x10 past the symbol, in the object's first word.</summary>
    public const string PartDanglyMeshVtbl = "_ZTV14PartDanglyMesh";

    /// <summary><c>PartDanglyMesh::Animate(float)</c> @0x49bbb0 -- vtable slot 27 (+0xd8), the per-frame
    /// dangly SOLVE. Separate from the draw, which is why Alloy's M4 draw takeover never froze the sway.
    /// Chained, not installed: the whole point is to replace the integrator inside it.</summary>
    public const string PartDanglyAnimate = "_ZN14PartDanglyMesh7AnimateEf";
    /// <summary><c>Scene::GetWindVector(Vector const&amp;, float)</c> @0x460c60 -- the AREA's wind at a world
    /// position, and the only honest source of it: a four-instruction forward to
    /// <c>WindManager::GetGlobalWind</c> @0x490ab0, which composes <c>base * noiseBlend + pointSources</c>
    /// and rotates the result by the yaw/pitch variation quaternions.
    /// <para>Reading <c>WindManager+0x20</c> instead does NOT work, and the arithmetic says why: the Scene
    /// builds its manager with the DEFAULT constructor @0x491b90, which zeroes +0x20 (<c>mov QWORD PTR
    /// [rdi+0x20],0x0</c> @0x491bf5), and the only writer in the whole binary is the <c>setglobalwind</c>
    /// console command. In normal play the base is (0,0,0) and every bit of felt wind is point sources.</para>
    /// <para>Returns a <c>Vector</c> BY VALUE with no sret -- verified at the call site: <c>rdi</c> is the
    /// scene and <c>rsi</c> the position (0x49beaf/0x49bea8), so SysV puts x,y in xmm0 and z in xmm1.</para></summary>
    public const string SceneGetWindVector = "_ZN5Scene13GetWindVectorERK6Vectorf";
    /// <summary><c>int Scene::HitCheck(Vector from, Vector to, Vector* hit, CHitInfo* info)</c> @0x462960 --
    /// the engine's own segment test, tile BSP parts first then every gob (<c>info+0</c> == 0), nearest hit
    /// wins; the camera's pull-in is built on it. <c>Vector</c> is BY VALUE and returns <c>int</c>, so no
    /// sret: SysV puts each in xmm pairs, Win64 hands a pointer to a copy -- one blittable struct declaration
    /// lowers to both (#537).</summary>
    public const string SceneHitCheck = "_ZN5Scene8HitCheckE6VectorS0_PS0_P8CHitInfo";
    /// <summary><c>CHitInfo::Reset(int mode, long mask, int box)</c> @0x45fa90 -- (re)initialises a hit
    /// record, unlinking the SafePointer a previous hit left in it (#537).</summary>
    public const string HitInfoReset = "_ZN8CHitInfo5ResetEili";
    /// <summary><c>Part::Animate(float)</c> @0x49ad00 -- walks the part's children (Part+0x38 pointer,
    /// Part+0x40 count) and calls +0xd8 on each. The dangly solve TAIL-CALLS it, so a replacement that
    /// skips the original must call it or the whole subtree freezes.</summary>
    public const string PartAnimate = "_ZN4Part7AnimateEf";
    /// <summary><c>VertexPrimitiveDangly::GenerateVelocityArray()</c> @0x45ad70 -- allocates the zeroed
    /// <c>verts*12</c> velocity array at <c>+0xa0</c>, lazily, on the first Animate. A replacement that
    /// never calls the original has to drive this itself or the array is never born.</summary>
    public const string VertexPrimGenVelocity = "_ZN21VertexPrimitiveDangly21GenerateVelocityArrayEv";
    /// <summary><c>VertexDataObject::GetItemDynamicData(uint)</c> @0x45c6c0 -- the host shadow of one item's
    /// DYNAMIC vertex fork, stride 12, which is where dangly writes its deformed positions AND where it
    /// reads last frame's back. Both the state and the output live here.</summary>
    public const string VdoGetItemDynamicData = "_ZN16VertexDataObject18GetItemDynamicDataEj";
    /// <summary><c>VertexDataObject::CommitItemDynamicData(uint, uint)</c> @0x45c7d0 -- funnels into
    /// <c>GLRender::UnlockPool</c>, which Alloy owns (#260) and which raises the #241 dirty bit the caster
    /// re-bake reads. Writing the verts is not enough; this is what publishes them.</summary>
    public const string VdoCommitItemDynamicData = "_ZN16VertexDataObject21CommitItemDynamicDataEjj";
    /// <summary>#256: <c>PartSkin</c>'s vtable, the same kind of identity as
    /// <see cref="PartDanglyMeshVtbl"/>. The scene walk records a part without ever entering its draw, so
    /// "this is a true skinmesh" can no longer be read off a <c>PartSkin::Draw</c> window -- it is a vtable
    /// compare, which is exact.</summary>
    public const string PartSkinVtbl = "_ZTV8PartSkin";
    /// <summary>#256: <c>Scene::Sequence</c> @0x18aa1b0 -- the global scene-membership epoch,
    /// bumped by <c>Gob::SetScene</c> @0x43e020 on every join and every leave, in ANY scene. One int load
    /// per frame is the whole maintenance signal for the enumerated record set (ADR 0018: re-read, never
    /// infer). Global, so a GUI 3D panel bumps it too -- which is the measurement #256 owes.</summary>
    public const string SceneSequence = "_ZN5Scene8SequenceE";
    /// <summary>#256: <c>countpart</c> @0x18da6b8 -- the engine's own live-part census, incremented in
    /// <c>Part::Part</c> and decremented in <c>Part::~Part</c>. The bring-up validator: it counts every
    /// <c>Part</c> in every scene (GUI panels included), so it is an upper bound on the walk, never an
    /// equality.</summary>
    public const string CountPart = "countpart";
    /// <summary>#256: <c>BSPTraverse(BSPNode*, void(*)(BSPNode*, void*), void*)</c> @0x5b22c0 -- the
    /// UNCULLED variant (the other two take a camera position or a plane list). Visits every node:
    /// <c>node+0x10</c> then iterating <c>node+0x18</c>. Tile geometry has no owning gob, so this is the
    /// only route to it that does not depend on what the camera can see.</summary>
    public const string BspTraverse = "_Z11BSPTraverseP7BSPNodePFvS0_PvES1_";
        /// <summary>Material::HasEnvMap @0x43d1a0-ish -- env-mapped parts take RenderEnvironmentMapped, not
    /// RenderFlat; excluded from batching.</summary>
    public const string MatHasEnvMap = "_ZN8Material9HasEnvMapEv";
    /// <summary>Material::HasBumpyShinyMap -- bumpy-shiny parts take RenderBumpyShiny; excluded.</summary>
    public const string MatHasBumpyShinyMap = "_ZN8Material16HasBumpyShinyMapEv";
        /// <summary>Material::GetTexture(int) @0x4c5810 -- texture slot i (SharedMaterial override at
    /// mat+0xd0 wins, else own slot at mat+i*0x10). Batch group key = material CONTENT (each part owns
    /// its Material instance, so pointer identity shatters groups to size 1).</summary>
    public const string MatGetTexture = "_ZN8Material10GetTextureEi";
    /// <summary>#275: <c>SharedMaterial::ParseField(char const*)</c> @0x4c6760 -- one <c>.mtr</c> line. The
    /// engine's chain ends at <c>texture10</c>; the hook keeps <c>texture11+</c> in a side table.</summary>
    public const string SharedMaterialParseField = "_ZN14SharedMaterial10ParseFieldEPKc";
    /// <summary>#275: <c>SharedMaterial::Init(char const*)</c> @0x4c6ff0 -- after the parse, where the
    /// engine loads its own slots (<c>LoadTextureArray</c>, which MSVC inlines here -- hence Init, not it).</summary>
    public const string SharedMaterialInit = "_ZN14SharedMaterial4InitEPKc";
    /// <summary>#275: <c>SharedMaterial::~SharedMaterial()</c> @0x4c5260 (D1 = D2), the side table's death
    /// notice. Called from <c>RemoveRef</c> (last Material gone) and <c>RefreshSharedMaterial</c>.</summary>
    public const string SharedMaterialDtor = "_ZN14SharedMaterialD2Ev";
    /// <summary>#275: <c>LoadTexture(char const*)</c> @0x4c50f0 -- <c>AurTextureGetReference</c> (an ADDREF'd
    /// reference, unlike <see cref="AurTextureGet"/>), or the NULL texture for an empty/"NULL" name.</summary>
    public const string LoadTexture = "_Z11LoadTexturePKc";
    /// <summary>#275: <c>AurTextureRelease(CAurTexture*)</c> @0x486010 -- the release the destructor pairs
    /// with <see cref="LoadTexture"/>.</summary>
    public const string AurTextureRelease = "_Z17AurTextureReleaseP11CAurTexture";
    /// <summary>Part::~Part() @0x437720 (D1 and D2 share the address) -- the BASE destructor every part
    /// class tail-jumps to: PartSkin and PartDanglyMesh jump to PartTriMesh::~PartTriMesh, which jumps here
    /// with <c>this</c> unchanged (verified in the disassembly, no thunk adjust). #145: the one event that
    /// says a recorded part is GONE. Without it the part table holds the pointer until something happens to
    /// notice, and the owned pass chases it into freed memory.</summary>
    public const string PartDtor = "_ZN4PartD2Ev";
    /// <summary>#397 diagnostic: <c>Gob::Append(char const*, char const*)</c> @0x4422a0 (model, attach
    /// node) and <c>Gob::Amputate(char const*)</c> @0x443fa0 -- the equipment-swap pair. Logged cold to say
    /// whether a swap amputates what it appended. Linux-only: no Windows anchor derived (skip + log there).</summary>
    public const string GobAppend = "_ZN3Gob6AppendEPKcS1_";
    public const string GobAmputate = "_ZN3Gob8AmputateEPKc";
    /// <summary>VertexDataObject::Repack() @0x45c270 -- compacts tombstoned items (RemoveItem only zeroes
    /// sizes), MOVING the surviving items' indices and offsets. Triggered by AddItem on fragmentation, so
    /// streaming fires it mid-play; every batch member of that VDO must recapture its item.</summary>
    public const string VdoRepack = "_ZN16VertexDataObject6RepackEv";
    /// <summary>#212: GLRender::UnlockPool(token, size, offset) @0x4cfc80 -- the SINGLE funnel through which
    /// every byte of engine vertex/index data reaches a GL buffer (glBufferData or glBufferSubData off the
    /// pool's host shadow). <c>VertexDataObject::Build</c>, <c>AddItem</c>'s grow, all three
    /// <c>CommitItem*Data</c> and <c>Repack</c>'s post-compaction re-upload all pass through it, so #260
    /// PERFORMS it rather than observing it. Hooked at the GLRender level rather than the
    /// <c>RenderInterface</c> thunk because that thunk is its only direct caller AND the vtable slot points
    /// here -- the lower funnel catches both.</summary>
    public const string GlUnlockPool = "_ZN8GLRender10UnlockPoolEjjj";
    /// <summary>#212: GLRender::DeleteBuffer(name) @0x4cfec0 -- where an engine GL buffer name dies.
    /// <c>GLRender::DeletePool</c> @0x4d1d80 routes here whenever a pool is released, freeing the buffer
    /// #260 minted for it; this is the exact moment the name (and the pool token behind it) go back into
    /// circulation. The geometry twin of <see cref="RiDeleteTextures"/>, and the same #220 corruption
    /// without it.</summary>
    public const string GlDeleteBuffer = "_ZN8GLRender12DeleteBufferEj";
    /// <summary><c>GLRender::GetVertexBufferMaxElementVertices</c> @0x4cb730 / <c>...Indices</c> @0x4cb700:
    /// <c>glGetIntegerv(GL_MAX_ELEMENTS_VERTICES / _INDICES)</c>, cached. The ONLY reader is
    /// <c>VertexDataObjectManager::GetVertexDataObjectTokenByAttributes</c> @0x45bdc0, which reuses a shared
    /// VDO only while its vertex and index totals stay under them -- so the driver's answer is the shared
    /// pool's capacity. NVIDIA answers 1048576; every Mesa driver answers 3000 (core <c>MaxArrayLockSize</c>,
    /// reproduced under zink and llvmpipe), which fragments an area into hundreds to thousands of VDOs.</summary>
    public const string GlMaxElementVertices = "_ZN8GLRender33GetVertexBufferMaxElementVerticesEv";
    public const string GlMaxElementIndices = "_ZN8GLRender32GetVertexBufferMaxElementIndicesEv";
    /// <summary>#260: <c>noemittervar</c> @0x19e3114 (B) -- the nearest EXPORTED symbol below the GLRender
    /// pool descriptor table's two globals, which have no symbols of their own. The table base pointer sits
    /// at nm 0x19e5480 = <c>noemittervar + 0x236c</c> and the descriptor count at 0x19e5488 = +0x2374;
    /// objdump of <c>GLRender::GetPoolBuffer</c> @0x4cfba0 resolves its RIP-relative load to exactly
    /// <c>noemittervar+0x236c</c>, which is where those two offsets come from.</summary>
    public const string GlPoolTableAnchor = "noemittervar";
    /// <summary>#260: <c>g_bOrphanBuffersBeforeWrite</c> @0x18aa51c (D) -- the engine's driver hint on the
    /// whole-buffer dynamic upload branch of <c>GLRender::UnlockPool</c>: re-specify the store with a null
    /// pointer before writing it, so the driver need not stall on the previous contents. Read, not decided,
    /// because Alloy performs that upload now and a silently dropped orphan is an unexplained stall.</summary>
    public const string GlOrphanBuffers = "g_bOrphanBuffersBeforeWrite";
    /// <summary>#260: <c>totalmempool</c> @0x19ad058 (B) -- the engine's own running total of pool bytes,
    /// maintained by <c>RequestPool</c>/<c>ResizePool</c>/<c>DeletePool</c>. The host-shadow half of the
    /// residency figure, reported beside what Alloy holds in VRAM (#254 left it unmeasured).</summary>
    public const string GlTotalMemPool = "totalmempool";
    /// <summary>GLRender::SetVertexDataObject(VertexDataObject*) @0x4cff30 -- binds (lazily creating) the
    /// VDO's VAO + element buffer and updates the engine's current-VDO cache. The owned caster path (ADR 0008)
    /// binds engine geometry through this so the cache never desyncs; null unbinds to the default VAO.</summary>
    public const string GlSetVertexDataObject = "_ZN8GLRender19SetVertexDataObjectEP16VertexDataObject";
    /// <summary>GLRender::m_nArrayBufferBound @0x19e30e4 (B) -- the shadow copy of the ARRAY_BUFFER binding
    /// that <c>GLRender::BindBuffer</c> @0x4d04a0 early-outs against. Written, not read: Alloy binds that
    /// target with raw GL, so before any call back into engine code that binds an array buffer the shadow
    /// has to be made truthful, or the engine skips a bind it needed and the following
    /// <c>glVertexAttribPointer</c> captures Alloy's buffer instead of the pool's (#284).</summary>
    public const string GlArrayBufferBound = "_ZN8GLRender19m_nArrayBufferBoundE";
    /// <summary>primTypeTranslationTable @0x12f50e0 (R) -- AuroraPrimitiveTypes -> GLenum table indexed by
    /// the prim id RenderVertexData receives.</summary>
    public const string PrimTypeTranslationTable = "primTypeTranslationTable";
    /// <summary>GLRender::m_bones @0x19e1f60 (B) -- the static 64 x mat4 bone array (4KB) the skinning shader
    /// runs on. <c>GLRender::SetBoneMatrix</c> @0x4cbbb0 runs each matrix through
    /// <c>Transpose(const Matrix4x4&amp;)</c> @0x5b5570 before storing it, and <c>GLRender::SetUniforms</c>
    /// @0x4ccbf0 then uploads the array with <c>glUniformMatrix4fv(loc, 64, transpose=0, m_bones)</c> -- so
    /// what SITS in m_bones is already in the column-major order GLSL wants, and a raw memcpy of it into an
    /// std430 <c>mat4[]</c> is byte-identical to what the engine feeds its own shader. The transpose is the
    /// engine's business, not ours: we write the same stored bytes and multiply in the same order its shader
    /// does.
    /// <para>#262: NOT resolved and never read. <c>SkinPose</c> does the bone solve itself and produces this
    /// layout directly, so what survives here is the LAYOUT CONTRACT the Alloy vertex stages inherit -- the
    /// reason a solved matrix can be memcpied into an std430 <c>mat4[]</c> untouched. Reading the array again
    /// would mean an engine draw had to have run, which is exactly what #262 removed.</para></summary>
    public const string GlRenderBones = "_ZN8GLRender7m_bonesE";
    /// <summary>GLRender::m_shaderExposedRuntimeData @0x19e1ea0 (B) -- the struct <c>GLRender::SetUniforms</c>
    /// @0x4ccbf0 uploads the "what is the world doing right now" uniforms straight out of. Field offsets read
    /// off the pairing of <c>ConnectShaderData</c> (name -&gt; PROG_FIXED location slot) with SetUniforms
    /// (location slot -&gt; field): <c>worldtimerTimeOfDay</c> int at +0x18, <c>areaGlobalWind</c> vec3 at
    /// +0x28, <c>areaFlags</c> int at +0x3c, <c>playerPosition</c> vec3 at +0x4c, <c>cameraPosition</c> vec3
    /// at +0x64. Reading it is what lets #123's feature switches be the engine's own values rather than a
    /// reconstruction -- and the camera here is the SCENE's, not the last pass's (the
    /// <c>FrameInputs.CameraPos</c> hazard) because the engine sets it once per frame for its own shaders.</summary>
    public const string GlShaderRuntimeData = "_ZN8GLRender26m_shaderExposedRuntimeDataE";
    /// <summary>GLRender::m_globalShaderConstantsf @0x19af8a0 (B) -- the float side of the engine's global
    /// shader constants. [21] and [22] are the two keyhole diameters; SetUniforms uploads the MIN of the pair
    /// as <c>keyholeDiameterMin</c> and the MAX as <c>keyholeDiameterMax</c>, so the pair is unordered in
    /// storage and composed at upload. [23] is <c>keyholeMinZOffset</c>, which the generation of
    /// <c>inc_keyhole.shd</c> this client runs never reads.</summary>
    public const string GlGlobalShaderConstantsF = "_ZN8GLRender24m_globalShaderConstantsfE";
    /// <summary>GLRender::m_globalShaderConstantsi @0x19af6e0 (B) -- the INTEGER side of the same table.
    /// [27] is <c>targetShape</c>, [32] <c>targetShapeOriginOnSelf</c>, [35] <c>targetObjectType</c>; the
    /// pairing is <c>SetUniforms</c> @0x4ccbf0 (slot 0x1eb0 &lt;- +0x6c, 0x1ec4 &lt;- +0x80, 0x1ee0 &lt;- +0x8c)
    /// against <c>ConnectShaderData</c>'s names, not the ADR. Read, never written.</summary>
    public const string GlGlobalShaderConstantsI = "_ZN8GLRender24m_globalShaderConstantsiE";
    /// <summary>GLRender::m_globalShaderConstantsv @0x19af890 (B) -- ONE Vector entry, and [0] is
    /// <c>targetWorldCoords</c>, the ground point under the mouse (SetUniforms slot 0x1ee4). There is no
    /// [1]: the array is 16 bytes wide.</summary>
    public const string GlGlobalShaderConstantsV = "_ZN8GLRender24m_globalShaderConstantsvE";
    /// <summary>GLRender::m_globalShaderConstantsv4 @0x19af780 (B) -- 17 Vector4 entries. [0..15] are the
    /// scriptable vec4s and [16] is <c>targetShapeColour</c> (SetUniforms slot 0x1edc &lt;- +0x100).</summary>
    public const string GlGlobalShaderConstantsV4 = "_ZN8GLRender25m_globalShaderConstantsv4E";
    /// <summary>RenderInterface::SetMatrixMode(AuroraTransStates) @0x45cd50 -- selects the matrix stack the
    /// subsequent LoadMatrixIdentity/MultMatrix/SetOrtho/SetPerspective operate on. Enum: PROJECTION=0, VIEW=1
    /// (from Camera::RenderScene's <c>xor edi,edi</c> before SetPerspective, and SetViewTransform's edi=1).</summary>
    public const string SetMatrixMode = "_ZN15RenderInterface13SetMatrixModeEN6Aurora17AuroraTransStatesE";
    /// <summary>RenderInterface::LoadMatrixIdentity() @0x45d0d0 -- reset the current matrix stack.</summary>
    public const string LoadMatrixIdentity = "_ZN15RenderInterface18LoadMatrixIdentityEv";
    /// <summary>RenderInterface::MultMatrix(float*) @0x45cd60 -- multiply the current stack by a row-major
    /// float[16] (the layout MakeMatrix4x4 writes; == System.Numerics.Matrix4x4 memory order).</summary>
    public const string MultMatrix = "_ZN15RenderInterface10MultMatrixEPf";
    /// <summary>RenderInterface::PushCurrentMatrix() @0x45cd30 -- duplicate the current stack's top, so a modify
    /// + PopCurrentMatrix restores the EXACT engine matrix (no captured-param staleness). Engine's own
    /// RenderDrawBucket brackets its draws with this pair.</summary>
    public const string PushCurrentMatrix = "_ZN15RenderInterface17PushCurrentMatrixEv";
    /// <summary>RenderInterface::PopCurrentMatrix() @0x45cd20 -- pop back to the pushed matrix.</summary>
    public const string PopCurrentMatrix = "_ZN15RenderInterface16PopCurrentMatrixEv";
    /// <summary>GLRender::FlagCurrentTransformDirty() @0x4c8cc0 -- bump uniform_dirty_seq so the next draw
    /// re-uploads the (restored) matrices. STATIC (no this). Called after the pops.</summary>
    public const string FlagCurrentTransformDirty = "_ZN8GLRender25FlagCurrentTransformDirtyEv";
    /// <summary>GLRender::SetModelMatrix(const Matrix4x4&amp;) @0x4c9360 -- STATIC (no this): rdi = a raw row-major
    /// float[16] model matrix (the layout MultMatrix/MakeMatrix4x4 use). Translation at floats [12,13,14]. Hooked
    /// (call-through) so the cube pass can read each caster part's world position and sphere-cull it per light.</summary>
    public const string SetModelMatrix = "_ZN8GLRender14SetModelMatrixERK9Matrix4x4";
    /// <summary>Global <c>g_nEnableBumpyWater</c> @0x19ace10 (B) -- the int
    /// <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 ANDs with <c>Material::HasBumpyShinyMap</c> before
    /// routing a part to draw bucket 6. Data symbol; read, never write.</summary>
    public const string EnableBumpyWater = "g_nEnableBumpyWater";
    /// <summary>RenderInterface::SetModelMatrix(const Matrix4x4&amp;) @0x45d150 -- the cache-coherent front door
    /// (forwards to GLRender::SetModelMatrix). The lean bucket walker uses this per kept caster.</summary>
    public const string RiSetModelMatrix = "_ZN15RenderInterface14SetModelMatrixERK9Matrix4x4";

    // #136: hosting the engine's own compiled program for a draw Alloy issues. GLRender::SetProgram
    // @0x4cf940 is called from INSIDE RenderVertexData @0x4d05c5 -- the funnel the M4 shim swallows -- so
    // under the cutover a custom-shader material's uniforms are never uploaded. These three are that call,
    // taken apart, so the swallow can make it in the engine's own context.
    /// <summary><c>GLRender::m_pCurrentCustomProgram</c> @0x19af680 (B) -- the PROG_FIXED the material bind
    /// left current. The GL program id is the uint at <c>+0x64</c>, exactly as SetProgram reads it.</summary>
    public const string GlCurrentCustomProgram = "_ZN8GLRender23m_pCurrentCustomProgramE";
    /// <summary><c>GLRender::SetUniforms(PROG_FIXED&amp;)</c> @0x4ccbf0 -- uploads the whole engine uniform
    /// set (matrices from <c>m_aurMtxStack</c>, the global constant tables, the runtime block) into that
    /// program. ENGINE CONTEXT ONLY: see SkyBox.NoteEngineDraw.</summary>
    public const string GlSetUniforms = "_ZN8GLRender11SetUniformsERNS_10PROG_FIXEDE";
    /// <summary><c>GLRender::GlUseProgramConditionally(unsigned int)</c> @0x4ce8b0 -- glUseProgram behind the
    /// engine's own current-program cache. Called on the engine side so that cache stays true.</summary>
    public const string GlUseProgramConditionally = "_ZN8GLRender25GlUseProgramConditionallyEj";
    /// <summary>Global shader param scalar getter -- param 0x12 is the light-radius multiplier
    /// GetLightAdjustedRadius applies (adjR = range * intensity * overbright * this).</summary>
    public const string RiGetGlobalShaderParamf = "_ZN15RenderInterface21GetGlobalShaderParamfEN6Aurora23AurGlobalShaderParamf_tE";
    /// <summary>Global shader param INTEGER getter -- param 0 is the gamma pipeline, the engine's own gate on
    /// the colour-space round trip (it becomes <c>#define GAMMA_CORRECTION %d</c> in every shader preamble,
    /// and <c>ApplyColorSpace</c>/<c>RevertColorSpace</c> in inc_common.shd are identity when it is 0).
    /// #132's resolve reads it so the one remaining encode is gated where the per-shader encodes never
    /// were.</summary>
    public const string RiGetGlobalShaderParami = "_ZN15RenderInterface21GetGlobalShaderParamiEN6Aurora23AurGlobalShaderParami_tE";
    /// <summary>#375: <c>enableFBeffects</c> @0x18aa34c, the engine's console gate on the whole
    /// framebuffer-effect walk -- <c>Camera::DoFrameBufferEffects</c> @0x4a8eb0 returns at once when it is 0.
    /// A plain int global (data symbol, not a function); Alloy's post stack honours it the same way.</summary>
    public const string EnableFBEffects = "enableFBeffects";
    /// <summary>The client's anti-aliasing option as MSAA samples (0/2/4/8/16): what settings.tml
    /// <c>graphics.video.anti-aliasing.mode</c> binds (DefineClientConfiguration @0x9ce7b0, setter @0x9c9330).</summary>
    public const string MsaaSamples = "g_nMSAA";
    /// <summary><c>Camera::m_nFramebufferEffectsEnabled</c>: the FB-effect enable bits (Camera::SetFBEffectEnabled
    /// @0x5a6500); bit 0 = SSAO, what settings.tml <c>graphics.fbo.ssao.enabled</c> toggles.</summary>
    public const string FbEffectsMask = "_ZN6Camera28m_nFramebufferEffectsEnabledE";
    /// <summary>The three Toon video sliders, as the plain float globals the options write and
    /// <c>Camera::SetFrameBufferParameters</c> @0x4a6d10 publishes as global float params 0x0c/0x0d/0x0e
    /// (settings.tml <c>graphics.fbo.toon.depth-edge-threshold</c>, <c>.color-edge-threshold</c>,
    /// <c>.edge-hardness</c>; 0.02 / 1.00 / 0.80 stock). Read directly rather than through the params:
    /// the publish sits in the frame walk Alloy replaced.</summary>
    public const string ToonDepthEdge = "toon_depth_edge_threshold";
    /// <summary>See <see cref="ToonDepthEdge"/>: the colour-edge threshold, global float param 0x0d.</summary>
    public const string ToonColorEdge = "toon_color_edge_threshold";
    /// <summary>See <see cref="ToonDepthEdge"/>: the edge hardness, global float param 0x0e. Zero is the
    /// stock stage's own off switch.</summary>
    public const string ToonEdgeHardness = "toon_edge_hardness";
    /// <summary>The <c>ssao_intensity</c> config float (settings.tml, 0.5 stock). Read directly: the engine
    /// only copies it into global float param 1 inside Camera::InitializeFrameBufferResources, which lives in
    /// the Scene::RenderSinglePass loop Alloy replaced, so under Alloy that param stays 0.</summary>
    public const string SsaoIntensity = "ssao_intensity";
    /// <summary>GetLightAdjustedRadius(PartLight const*) @0x4c0c10 -- the engine's own lit radius:
    /// <c>range * intensity</c>, times the max colour channel when that exceeds 1, times the global radius
    /// multiplier (param 0x12). Returns the raw range unchanged for a directional light (range &gt;= 1000).
    /// Called rather than re-derived. ABI: <c>(PartLight* /*rdi*/) -&gt; float /*xmm0*/</c>.</summary>
    public const string GetLightAdjustedRadius = "_Z22GetLightAdjustedRadiusPK9PartLight";
    /// <summary>GLRender::m_nCurrentMatrix @0x19e30d0 -- the selected matrix-stack index; saved/restored around
    /// the pass so we leave the engine's active matrix mode as we found it. Data symbol (read/write int).</summary>
    public const string MCurrentMatrix = "_ZN8GLRender16m_nCurrentMatrixE";
    /// <summary>RenderInterface::SetOrthoTransform(l,r,b,t,n,f doubles) @0x45d0f0 -- ortho into the current
    /// (PROJECTION) stack via aurMatrixStack::Orthographic (glOrtho arg order).</summary>
    public const string SetOrthoTransform = "_ZN15RenderInterface17SetOrthoTransformEdddddd";

    // ---- #282 deleted the shadow-map caster re-walk (#65 slices 3c/3d) and its four symbols --
    //      ManageSceneBSP, AddPartToMeshBuckets, BSPTraverse (sphere) and CExoArrayList<Gob*>::Add. All of it
    //      existed to push camera-culled casters through the engine's draw buckets so the capture could see
    //      them; SceneWalk enumerates the area's containers directly, and nothing reads those buckets as a
    //      caster source. The two symbols the re-walk shared with live code stay below. ----
    /// <summary>PartTriMesh::GetIsOpaque() @0x43cee0 -- true if the part is opaque. ABI:
    /// <c>(PartTriMesh* /*rdi*/) -> bool</c>.</summary>
    public const string PartGetIsOpaque = "_ZN11PartTriMesh11GetIsOpaqueEv";

    // ---- camera-follow anchor (#65): the real, camera-INDEPENDENT PC position, independent of the vanilla
    //      shadow path. Ghidra-verified; Linux-only (skip on Windows). ----
    /// <summary>Global <c>CurrentCamera</c> (@0x19acbb0, in .dynsym) -- the symbol's 8 bytes hold a live
    /// <c>CAurCamera*</c> (or 0).
    /// <para>#131 also reads the orientation QUATERNION at <c>*CurrentCamera + 0xbc</c>, stored
    /// (w, x, y, z) -- <c>PartEmitter::RenderEmitter</c> builds every billboard basis out of it.</para>
    /// <para>This SUPERSEDED the Scene "shadow gob" at Scene+0x58 as the anchor: that field's setter is
    /// inlined in the vanilla shadow path, so it reads NULL under <c>shadow.mode=0</c> (live-confirmed).</para>
    /// </summary>
    public const string CurrentCamera = "CurrentCamera";

    /// <summary><c>CAurBehaviorCameraFollow::Control(float dt)</c> @0x4a2290 -- the camera solve (#536). Integrates
    /// yaw/pitch/dist, lerps the focus and writes the pose (Camera+0xb0/+0xbc) through the owner Gob's vtable;
    /// every engine reader, Alloy included, is downstream of those two fields. <c>this</c> = the behavior; the
    /// owner at +0x18 is <c>Camera+8</c>. Returns int. Chained by CameraService (post-adjusts the answer).</summary>
    public const string CameraFollowControl = "_ZN24CAurBehaviorCameraFollow7ControlEf";

    /// <summary><c>CNWCModule::TurnCamera(float deg, int impulse)</c> @0x7d8960 / <c>TiltCamera</c> @0x7d88a0 -- the
    /// engine's own orbit input (#539). impulse=1 adds to the behavior's one-shot yaw/pitch impulse (+0x88/+0x8c),
    /// consumed by the next Control; the flag byte (+0x2bc bit 0x10 / 0x20) refuses a server-disabled axis and the
    /// dialog "user turned" bookkeeping (+0x134, +0x280) is kept. Middle-drag calls exactly these.</summary>
    public const string TurnCamera = "_ZN10CNWCModule10TurnCameraEfi";
    public const string TiltCamera = "_ZN10CNWCModule10TiltCameraEfi";
    /// <summary><c>CClientExoAppInternal::UpdateDriveMode()</c> @0x65f780 -- the per-frame drive step (#540). Reads the
    /// key booleans at +0x1b8.. and the "input changed" latch at +0x1b0, derives the wire heading from the player
    /// creature's orientation (+0x44/+0x48, <c>round(atan2 * 10)</c>), sends <c>DriveControl</c> on the latch or the
    /// 500 ms keep-alive, snaps the local facing and runs the prediction. Pre-hooked by CameraDriveService, which
    /// writes the orientation and the key set it wants the engine to act on.</summary>
    public const string UpdateDriveMode = "_ZN21CClientExoAppInternal15UpdateDriveModeEv";
    /// <summary>SDL2 cursor capture for mouse-look (#539): relative mode hides the cursor and streams raw deltas.</summary>
    public const string SdlSetRelativeMouseMode = "SDL_SetRelativeMouseMode";
    public const string SdlWarpMouseInWindow = "SDL_WarpMouseInWindow";
    /// <summary><c>CGuiInGame::ToggleOff()</c> @0x66dda0 / <c>UnToggleOff()</c> @0x66e930 -- the engine's own
    /// hide/show of the whole in-game GUI (every panel, quick chat, the NUI scope), the pair the server's
    /// Cutscene_HideGui message (major 0x33 minor 7) and <c>SetCutsceneMode</c> call. The free camera (#543)
    /// hides the UI through exactly these; <c>this</c> = <c>CClientExoAppInternal+0x90</c>.</summary>
    public const string GuiInGameToggleOff = "_ZN10CGuiInGame9ToggleOffEv";
    public const string GuiInGameUnToggleOff = "_ZN10CGuiInGame11UnToggleOffEv";

    /// <summary><c>CNWCCreature::UpdateLookAt()</c> @0x726f00 -- the engine's own per-frame head-track target
    /// chooser, called once per creature from <c>CNWCCreature::AIUpdate</c> @0x730ad0 (its only caller, a direct
    /// call). Picks a target, rejects anything past 110 deg off the facing, and hands it to
    /// <c>CNWCCreature::LookAt(objId, maxDist)</c>; with the mode word at +0x39c failing <c>&amp; 3 == 3</c> it
    /// calls <c>LookAt(INVALIDOBJID, 0)</c>, which tears the animation down. <c>(CNWCCreature*) -&gt; void</c>.
    /// Hooked by <see cref="Services.Camera.HeadTrackService"/>, which skips it for the local player under the
    /// action camera. Windows: CNWCCreature name-exports nothing, so rva 0x80fa60, anchored through the Windows
    /// <c>LookAt</c> (the one function referencing "HEADTRACK"/"HEAD_ARC_H"/"HEAD_NAME", rva 0x803f20) as its
    /// only caller; body decompiles identical, the mode word reading +0x37c (the class's -0x20 shift).</summary>
    public const string UpdateLookAt = "_ZN12CNWCCreature12UpdateLookAtEv";
    /// <summary><c>Gob::BeginLookAtAnimate(CAurObject* target, char const* targetNode, char const* myNode,
    /// float arcH, float arcV, float blend, float maxDist)</c> @0x49d5b0 -- <c>FindPart</c>s both nodes (silently
    /// returning if either misses), destroys any existing <c>LookAtAnimation</c> at Gob+0x1b0 and builds a fresh
    /// one plus its AnimRun. Virtual (Gob slot +0x250 / Windows +0x248) but there is exactly one implementation,
    /// so it is called by symbol. All-void return, no by-value crossing.</summary>
    public const string BeginLookAtAnimate = "_ZN3Gob18BeginLookAtAnimateEP10CAurObjectPKcS3_ffff";
    /// <summary><c>LookAtAnimation::Control()</c> @0x4973c0 -- one head-aim step, driven by <c>Gob::Animate</c>
    /// @0x49d8c0 through the vtable. Deactivates past the max distance, else writes the clamped look-at
    /// quaternion into the synthetic animation's second keyframe (<c>*(float**)(this+0x58)</c>, x/y/z/w) and the
    /// head's current local rotation into the first (+0x60), so every frame is a fresh 0.25 s ease. Chained, not
    /// installed: the aim is overwritten after the original runs.</summary>
    public const string LookAtControl = "_ZN15LookAtAnimation7ControlEv";

    /// <summary><c>LightManager::m_pOnlyGob</c> (@0x19acdb8, in .dynsym) -- non-null while a single gob is
    /// being previewed in isolation. <c>Scene::DoFog</c> @0x45f1b0 reads it: with a gob set, the fog colour
    /// it pushes is black rather than the area's, which #257 replicates when it reads the fog block direct.</summary>
    public const string LightManagerOnlyGob = "_ZN12LightManager10m_pOnlyGobE";

    /// <summary>Camera::GetCanKeyholeDissolve(PartTriMesh*) @ (member) -- returns whether a caster keyhole-
    /// dissolves (the camera-relative aperture around the PC). Hooked to return 0 during the depth pass so
    /// casters aren't punched out of the shadow map. ABI: <c>(Camera* /*rdi*/, PartTriMesh* /*rsi*/) -> bool</c>.</summary>
    public const string GetCanKeyholeDissolve = "_ZN6Camera21GetCanKeyholeDissolveEPK11PartTriMesh";

    // Aurora texture manager: get a portrait as an engine-resident GL texture. AurTextureGet(name,
    // NULL, NULL, NULL, 0, false) loads/caches a CAurTexture*; AurTextureGetTID(tex) returns its GL
    // texture id (0 until the engine's RefreshTextures pass uploads it). Chat portraits (#26) bind it
    // directly -- no decode, no upload. See ConvertPortrait for the H/L/M/S/T size suffixes.
    public const string AurTextureGet    = "_Z13AurTextureGetPKcP11CAurTextureS0_Piib";
    public const string AurTextureGetTID = "_Z16AurTextureGetTIDP11CAurTexture";
    /// <summary>#331: <c>AurTextureGetController(CAurTexture*)</c> @0x485350 -- resolves ref -> basic and returns
    /// <c>basic+0x38</c>, the texture's animation controller (or null).</summary>
    public const string AurTextureGetController = "_Z23AurTextureGetControllerP11CAurTexture";
    /// <summary>#331: the one controller class whose Control() (vtable slot +0x10) only rewrites a GL name.</summary>
    public const string CycleTidControllerVtbl = "_ZTV25CycleTIDTextureController";
    /// <summary>#331: the engine's int gate on every controller tick (the Bind tail reads it).</summary>
    public const string EnableTextureControllers = "enabletexturecontrollers";
    /// <summary>The two texture vtables, which is the whole of "is this pointer still a texture" --
    /// see <see cref="AurTex"/>. Data symbols, never called: what is read is the value a live object's
    /// first word holds, i.e. the symbol plus the Itanium ABI's 0x10.</summary>
    /// <summary>#284: <c>CAurTexture::BuildAndStore()</c> @0x48bc80 -- the engine's ON-DEMAND texture build,
    /// and the only retry that exists. It resolves the reference/basic pair (vtable 0x18 then 0x10) and calls
    /// <c>CAurTextureBasic::Reset</c> @0x4858c0 followed by <c>BuildAndStoreTexture</c> @0x48b320. Reset is
    /// what makes it a RETRY and not a no-op: it clears the constructed latch at <c>basic+0xe6</c> and the
    /// no-image verdict at <c>+0xe7</c>, deletes the GL names at <c>+0x50</c> and zeroes the id at
    /// <c>+0x6c</c>, so the rebuild re-runs <c>ConstructImage</c> @0x48a5d0 and can decode a resource that
    /// was not serviced the first time round. <c>+0xe7</c> is NOT the null texture -- it is
    /// <c>ConstructImage</c>'s tail finding no decoded image at <c>+0x58</c> and no controller at
    /// <c>+0x38</c>; the null texture is a separate OBJECT (<see cref="AurTextureNullPtr"/>), and #284
    /// shipped a retry that could not tell them apart.
    /// <para>Deleting the GL names is why a caller must first prove there is something to rebuild: called on
    /// a texture that already has an id, or on the shared null texture, Reset is pure destruction.</para>
    /// <para>Expensive -- a decode plus a fresh upload through <c>RenderInterface::CreateTexture</c>, i.e.
    /// the funnel <see cref="CX.Alloy.OwnedTextures"/> adopts from -- so it is dripped, never swept.
    /// ABI: <c>(CAurTexture* /*rdi*/) -> void</c>. Needs a live GL context.</para></summary>
    public const string AurTextureBuildAndStore = "_ZN11CAurTexture13BuildAndStoreEv";
    public const string AurTextureVtbl = "_ZTV11CAurTexture";
    public const string AurTextureBasicVtbl = "_ZTV16CAurTextureBasic";
    /// <summary>#284: <c>CAurTextureBasic::m_pNullTexture</c> @0x19abe90 -- the engine's ONE shared "no
    /// texture" object, and the reason a GL id of 0 has two meanings. <c>AurTextureGetTID</c> @0x48e080
    /// resolves the reference chain and returns 0 outright when the answer is this object, without ever
    /// reading <c>basic+0x6c</c>; so a material naming it reports no id for ever, and no number of rebuilds
    /// can change that. Read as a pointer-sized DATUM rather than through <c>AurTextureGetNULL</c>
    /// @0x48dc20, which LAZILY CREATES the object on first call -- a query must not be the thing that
    /// allocates the answer, and null (not yet created) correctly matches nothing.</summary>
    public const string AurTextureNullPtr = "_ZN16CAurTextureBasic14m_pNullTextureE";

    // Resource manager: read a resource's bytes the way the engine's own CResHelper does (#92, owned-pipeline
    // .shd overrides). Sequence, from CResHelper<CResSHD,2069>::SetResRef / ClearResHelper:
    //   Exists -> GetResObject -> (null? engine operator new(0x60) + CRes::CRes + SetResObject) -> Demand
    //   -> read CRes+0x20 (data) / CRes+0x30 (size) -> Release + ReleaseResObject.
    // SetResObject seeds the object refcount at 1, so both paths release exactly once.
    public const string ExoResMan          = "g_pExoResMan";
    public const string ResManExists       = "_ZN10CExoResMan6ExistsERK7CResReftPj";
    public const string ResManGetResObject = "_ZN10CExoResMan12GetResObjectERK7CResReft";
    public const string ResManSetResObject = "_ZN10CExoResMan12SetResObjectERK7CResReftP4CRes";
    public const string ResManDemand       = "_ZN10CExoResMan6DemandEP4CRes";
    /// <summary>The two pre-dispatch calls of the world scene render, inside the AI update: the BSP
    /// management (every gob, three passes, the frustum traverse) and the draw-bucket build (the sole
    /// writer of the gob model matrix Alloy draws with). Timed only; see
    /// docs/research/engine-scene-walk-bypass.md.</summary>
    public const string ManageSceneBSP         = "_Z14ManageSceneBSPP5Scene";
    public const string AddPartsToDrawBuckets  = "_ZN5Scene21AddPartsToDrawBucketsEv";
    /// <summary>The BSP visit callback's per-frame tile-part lists that AddPartsToDrawBuckets' six flat loops
    /// consume. meshbuckets/fadebuckets are <c>List&lt;Bucket&gt;</c> objects (CExoArrayList: items at +0,
    /// count at +8, alloc at +0xc) whose 16-byte {items, count} pairs live on the heap, one per texture id
    /// (ClearBuckets @0x47f4e0). The shadow and cap lists are single CExoArrayLists (count at +8). The
    /// .bss neighbours are the texture globals and the AurGet*Image function pointers: a write past the
    /// 16-byte object is a decoder crash. staticbeambucket is left alone: Beams.Enroll re-reads bucket 15.</summary>
    public const string MeshBuckets = "meshbuckets", FadeBuckets = "fadebuckets", CapBuckets = "capbuckets",
                        StaticShadowBucket = "staticshadowbucket", MeshShadowBucket = "meshshadowbucket";
    public const string ResManRelease      = "_ZN10CExoResMan7ReleaseEP4CRes";
    public const string ResManReleaseObj   = "_ZN10CExoResMan16ReleaseResObjectEP4CResb";

    // #346: the engine's OWN resource read, which is how a stock .shd must be fetched. CResHelper builds a
    // typed CRes subclass per resource type and the engine reads it back through that type -- registering a
    // bare CRes under someone else's resref is a type confusion that crashes the engine, not a leak. See
    // ShaderResource.Override's warning. GLRender's shader-source loader @0x4d2940 calls exactly these three,
    // in this order, and this replays that: AurResGet -> AurResGetData(size) -> AurResFree(handle).
    /// <summary><c>AurResGet(const char* name, const char* ext, int* size, bool)</c> @0x4332c0 -- opens a
    /// resource and pushes it on the <c>Resources</c> stack. Returns the handle and writes the byte count.
    /// The engine passes the extension WITH its dot (".shd") and true for the bool.</summary>
    public const string AurResGet = "_Z9AurResGetPKcS0_Pib";
    /// <summary><c>AurResGetData(int nBytes)</c> @0x432fe0 -- read that many bytes from the resource on TOP
    /// of the stack into its own buffer and return it. Stack-based, so the trio runs back to back with no
    /// engine call in between, exactly as @0x4d2940 does it.</summary>
    public const string AurResGetData = "_Z13AurResGetDatai";
    /// <summary><c>AurResFree(void* handle)</c> @0x4335d0 -- pop and release. Called only on the path where
    /// the handle AND the size are non-zero; the engine's own loader does not free on the failure path, so
    /// neither do we.</summary>
    public const string AurResFree = "_Z10AurResFreePv";
    public const string CResCtor           = "_ZN4CResC1Ev";

    /// <summary>
    /// PE TimeDateStamp of the nwmain.exe the RVAs were read from. A client patch shifts
    /// addresses; RVA hooks are refused unless the loaded exe matches this (see BuildMatches).
    /// </summary>
    public const uint WindowsBuildStamp = 0x68e408bc;

    /// <summary>A symbol's Windows resolution. Four forms and no fifth -- the point of #447: a missing key
    /// used to mean both "never looked" and "deliberately Linux-only", so the absence could not be counted.
    /// Now every constant has a row, <see cref="Nm"/>/<see cref="At"/> resolve, <see cref="LinuxOnly"/> is a
    /// decision, and <see cref="Hole"/> is the only form that counts against the port.</summary>
    private readonly record struct Win(string? Name, int Rva, string? Why, bool Counted);

    /// <summary>Name-exported on Windows: resolve the MSVC mangling the way Linux resolves Itanium.</summary>
    private static Win Nm(string msvc) => new(msvc, 0, null, false);
    /// <summary>Not exported: absolute = runtime module base + RVA, build-locked by the PE stamp.</summary>
    private static Win At(int rva) => new(null, rva, null, false);
    /// <summary>A decision: this symbol is not wanted on Windows, and <paramref name="why"/> says why. Does
    /// not count -- CLAUDE.md's rule is that the absence be DECLARED, not that it be filled.</summary>
    private static Win LinuxOnly(string why) => new(null, 0, why, false);
    /// <summary>Not yet anchored. Counts, and is printed by <c>/knobs</c> beside EngineLayout's unresolved
    /// offsets: parity is the constraint (#445), so a named instrument is a waypoint, never an exit.</summary>
    private static Win Hole(string why) => new(null, 0, why, true);

    /// <summary>Linux name -> Windows resolution. Only symbols that DO resolve on Windows are listed: a
    /// missing key and an explicit <c>default</c> entry take the same branch in <see cref="Resolve"/> and emit
    /// the same log line, so the Linux-only majority is listed once, as its constant, and nowhere else.
    /// See docs/windows-symbols.md for how each anchor was derived.</summary>
    private static readonly Dictionary<string, Win> _win = new()
    {
        // --- exported on Windows (shared engine): resolve by MSVC name ---
        [HandleWMKeyDown]   = Nm("?HandleWMKeyDownMessage@@YAHII@Z"),
        [WinMessageHandler] = Nm("?WinMessageHandler@@YAHPEATSDL_Event@@@Z"),
        [SwapBuffers]       = Nm("?SwapBuffers@GLRender@@CAXPEAUSDL_Window@@@Z"),
        [SetText]           = Nm("?SetText@CAuroraStringWrapper@@QEAAXI@Z"),
        [ExoStringInit]     = Nm("?InitFromCharArray@CExoString@@AEAAAEAV1@PEBDI@Z"),
        [ExoStringDtor]     = Nm("??1CExoString@@QEAA@XZ"),
        [AppManager]        = Nm("?g_pAppManager@@3PEAVCAppManager@@EA"),
        [ExoBase]           = Nm("?g_pExoBase@@3PEAVCExoBase@@EA"),   // userdir CExoString@0x38 identical on both builds
        [ExoConfigGetBool]   = Nm("??$Get@_N@CExoConfig@@QEAA_NAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@_N@Z"),
        [ExoConfigGetLong]   = Nm("??$Get@_J@CExoConfig@@QEAA_JAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@_N@Z"),
        [ExoConfigGetDouble] = Nm("??$Get@N@CExoConfig@@QEAANAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@_N@Z"),

        // --- not exported (client GUI / CNWCMessage): Windows RVA,
        //     build 0x68e408bc. See docs/windows-symbols.md for how each was anchored. ---
        [HandleServerToPlayerMessage] = At(0x7a41a0),
        [HandleServerToPlayerLogin]   = At(0x7a31b0),
        [SendPlayerToServerMessage]   = At(0x7c25c0),
        [SendDeviceAdvertiseProperty] = At(0x7bbac0),
        // #672: the area-loaded hold; none returns a class by value. Anchors in docs/windows-symbols.md.
        [SendAreaLoaded]              = At(0x7ba050),
        [HandleUpdateWorkRemaining]   = At(0x7b85a0),
        [StartLoadBar]                = At(0x6acd20),
        [EndLoadBar]                  = At(0x6ab770),
        [GetLoadBarState]             = At(0x6abe20),
        [GetNWCMessage]               = At(0x6abf90),
        [SetFocusChatPrompt]          = At(0x6fae30),
        [ChatHandleEnter]             = At(0x87b0d0),
        [ChatHandleEsc]               = At(0x87b390),
        [ChatHandleLMouseDown]        = At(0x87b450),   // Notify event enum 1
        [ChatHandleLoseFocus]         = At(0x87b4b0),   // Notify event enum 2
        [TurnOffKeyboardModal]        = At(0x6e5d80),   // anchored via CGuiEdit::SetFocus (call after KeyboardModeOn)
        // Footgun: nwmain.exe statically links the CRT, so the panel frees via the game's own
        // operator delete -- allocate from the game's own operator new (build-locked RVA) or the
        // cross-heap free corrupts. Scalar operator new(size_t), VS2017/2019 release.
        [OperatorNew]                 = At(0xc10324),

        // --- consent modal + input, name-exported on Windows (verified in nwmain.exe's export table) ---
        [OkCancelCtor]    = Nm("??0CPanelOkCancel@@QEAA@XZ"),
        [OkCancelActivate]= Nm("?Activate@CPanelOkCancel@@QEAAXVCExoString@@II@Z"),
        [GuiMan]          = Nm("?g_pGuiMan@@3PEAVCGuiMan@@EA"),
        [GetGuiScale]     = Nm("?GetGUIScale@CAurora@@SAMXZ"),   // CAurora shared-engine class -> name-exported
        [SdlGetKeyName]   = Nm("SDL_GetKeyName"),
        [SdlGetKeyFromName] = Nm("SDL_GetKeyFromName"),
        [WalkPlayerToPoint] = At(0x6f3650),          // action camera: "gui_nowalk" string, unique on both builds
        [FeedbackHitCheckHostile] = At(0x6eae20),    // action camera: the picker with GetHeightTransition (Callees)
        [FeedbackHitCheckNonHostile] = At(0x6ebcf0), // action camera: the other picker, same caller cluster
        [SdlGetModState]  = Nm("SDL_GetModState"),
        // head tracking: the three Gob/Part/LookAtAnimation symbols are shared-engine exports; only the
        // CNWC* chooser needs an anchor (its Windows caller-of-"HEADTRACK" chain, docs/windows-symbols.md).
        [UpdateLookAt]       = At(0x80fa60),
        [BeginLookAtAnimate] = Nm("?BeginLookAtAnimate@Gob@@UEAAXPEAVCAurObject@@PEBD1MMMM@Z"),
        [LookAtControl]      = Nm("?Control@LookAtAnimation@@UEAAXXZ"),
        // auto-target: CActionTarget is client-side (not name-exported on Windows). Both anchored by the
        // 'AcTg' GetGuiObjType tag cluster and confirmed by decompile -- ActivateTargetMode is the same
        // CaptureMouse/CaptureKeyboard/RegisterUpdate/RegisterActionTargetGUI body, HandleLMouseDown the
        // same GetMousePos -> hit-check -> allowed-list scan -> fire.
        [ActivateTargetMode]      = At(0x83b9e0),
        [ActionTargetLMouseDown]  = At(0x83be00),
        // hotkey suppression while any vanilla text field captures keys (console etc.) -- shared-engine
        // CExoInput, name-exported on Windows. (Chat also uses ExoInput but stays Linux-only for now.)
        [ExoInput]        = Nm("?g_pExoInput@@3PEAVCExoInput@@EA"),
        [GetKeyboardMode] = Nm("?GetKeyboardMode@CExoInput@@QEAAHXZ"),
        // The framework keyboard gate (UiInputService.KbGate): without these the engine mode never toggles, so
        // typed letters drive WASD and a stuck-on mode eats the Enter that reopens the box (#418).
        [KeyboardModeOn]  = Nm("?KeyboardModeOn@CExoInput@@QEAAXXZ"),
        [KeyboardModeOff] = Nm("?KeyboardModeOff@CExoInput@@QEAAXXZ"),
        [GuiManReleaseKeyboard] = Nm("?ReleaseKeyboard@CGuiMan@@QEAAXPEAVCGuiObject@@@Z"),
        [GuiObjectHitCheck] = Nm("?GuiObjectHitCheck@CGuiMan@@QEAAPEAVCGuiObject@@HHHH@Z"),
        // #580: the vanilla prompt's own text read; sret order branches at the call site (PromptCommandService).
        [GuiEditGetText]  = Nm("?GetText@CGuiEdit@@QEAA?AVCExoString@@XZ"),

        // --- the CX window rig (#419): glyph bake, GL loader, engine state hand-back, SDL text/clipboard.
        //     All extern "C" or shared-engine statics, name-exported. Portraits stay Linux-only: the CAurTexture
        //     vtables AurTex keys on resolve by RVA below. ---
        [StbttInitFont]              = Nm("stbtt_InitFont"),
        [StbttScaleForPixelHeight]   = Nm("stbtt_ScaleForPixelHeight"),
        [StbttGetFontVMetrics]       = Nm("stbtt_GetFontVMetrics"),
        [StbttGetCodepointHMetrics]  = Nm("stbtt_GetCodepointHMetrics"),
        [StbttGetCodepointBitmapBox] = Nm("stbtt_GetCodepointBitmapBox"),
        [StbttMakeCodepointBitmap]   = Nm("stbtt_MakeCodepointBitmap"),
        [StbttFindGlyphIndex]        = Nm("stbtt_FindGlyphIndex"),
        [SdlGlGetProcAddress]        = Nm("SDL_GL_GetProcAddress"),
        [SdlGetMouseState]           = Nm("SDL_GetMouseState"),
        [SdlStartTextInput]          = Nm("SDL_StartTextInput"),
        [SdlGetClipboardText]        = Nm("SDL_GetClipboardText"),
        [SdlSetClipboardText]        = Nm("SDL_SetClipboardText"),
        [SdlFree]                    = Nm("SDL_free"),
        [RiResetRenderStates]        = Nm("?ResetRenderStates@RenderInterface@@SAXXZ"),
        [RiBindBuffer]               = Nm("?BindBuffer@RenderInterface@@SAXII@Z"),
        [GuiPanelDeactivate]         = Nm("?Deactivate@CGuiPanel@@QEAAXH@Z"),
        [GuiPanelUnToggleOff]        = Nm("?UnToggleOff@CGuiPanel@@QEAAXXZ"),
        // Portraits: the texture guard keys on the two vftables. Not exported; RVAs from the RTTI type descriptors
        // (`.?AVCAurTexture@@` / `.?AVCAurTextureBasic@@` -> complete-object locator -> vftable). MSVC's symbol
        // IS the object's first word (no Itanium +0x10 RTTI skew) -- AurTex.Attach knows.
        [AurTextureGet]              = Nm("?AurTextureGet@@YAPEAVCAurTexture@@PEBDPEAV1@0PEAHH_N@Z"),
        [AurTextureGetTID]           = Nm("?AurTextureGetTID@@YAIPEAVCAurTexture@@@Z"),
        [AurTextureVtbl]             = At(0xd90c70),
        [AurTextureBasicVtbl]        = At(0xd90d90),

        // --- chat (#419): client GUI + CNWCMessage senders, RVA, build 0x68e408bc. Anchors in docs/windows-symbols.md.
        //     Win64 ABI notes: every by-value CExoString arrives as a pointer the CALLEE destroys; CResRef too. ---
        [AppendToMsgBuffer]      = At(0x6f68d0),
        [AppendToChatBuffer]     = At(0x879080),
        [ChatWindowSetFilter]    = At(0x87a0e0),
        [ChatWindowShow]         = At(0x87a940),   // shown flag +0xaf0 (Linux +0xc68)
        [ChatPromptShow]         = At(0x87cc00),   // shown flag +0x200 (Linux +0x230)
        [ChatPromptSetFocus]     = At(0x87cb30),
        [ChatPromptCtor]         = At(0x87abb0),
        [ShowChatPrompt]         = At(0x6fb480),
        [PrepChatPromptForTell]  = At(0x6f9ec0),
        [NotifyServerGuiEvent]   = At(0x6f9bd0),
        [GetPlayerByPlayerName]  = At(0x6ac120),
        [GetPlayerByCreatureName]= At(0x6ac080),
        [GetModule]              = At(0x6abea0),
        [SendChatTalk]           = At(0x7ba7e0),   // major 9 minor 1
        [SendChatShout]          = At(0x7ba700),   // minor 2
        [SendChatWhisper]        = At(0x7baab0),   // minor 3
        [SendChatTell]           = At(0x7ba9b0),   // minor 4, WriteBOOL(1) + two strings (TellName)
        [SendChatTellId]         = At(0x7ba8c0),   // minor 4, WriteBOOL(0) + WriteDWORD(id)
        [SendChatParty]          = At(0x7ba620),   // minor 6
        [SendChatDM]             = At(0x7ba540),   // minor 0xe

        // --- nameplates / combat text / dialog (#420), build 0x68e408bc. Anchors in docs/windows-symbols.md.
        //     Win64 ABI: sret comes AFTER `this` (GetSimpleString, the GetPortrait virtual); a by-value Vector
        //     arrives as a pointer; a by-value CExoString is the callee's to destroy. ---
        [TlkTable]           = Nm("?g_pTlkTable@@3PEAVCTlkTable@@EA"),
        [TlkGetSimpleString] = Nm("?GetSimpleString@CTlkTable@@QEAA?AVCExoString@@I@Z"),
        [GetViewPort]        = Nm("?GetViewPort@RenderInterface@@SAXPEAM@Z"),
        [GetGameObject]      = At(0x6abb60),
        [GetObjectName]      = At(0x6abfc0),
        [ShowDialogEntry]    = At(0x6fb560),
        [ShowDialogReplies]  = At(0x6fb8e0),
        [ShowReplyChosen]    = At(0x6fbbc0),   // destroys its by-value CExoString: a skip must too
        [CloseDialog]        = At(0x6f6d80),
        [SendDialogReply]    = At(0x7bbc50),
        [SetChatBubbleMode]  = At(0x6e90e0),
        [CreatureShowFeedbackBubbleText]  = At(0x80b240),   // plated flag +0x448
        [PlaceableShowFeedbackBubbleText] = At(0x871b60),   // +0x1f8
        [ItemShowFeedbackBubbleText]      = At(0x83f230),   // `this` = the CNWCObject subobject, flag +0x264
        [SetPopUpBubbleText] = At(0x766450),
        [HandleServerToPlayerChat] = At(0x77c140),   // major 9
        [ShowFloatyText]     = At(0x766a70),

        // --- #446: the three CNWC* anchors Alloy's highlight and border ring hook. No CNWC* class is
        //     name-exported on Windows, so each was anchored by a compiler-invariant string and confirmed
        //     against the named Linux original (docs/windows-symbols.md).
        [RefreshHilite]    = At(0x966170),   // its own ExoLog nwcanimbase.cpp:0x123a; channels +0x9c/+0xb0/+0xc8
        [AreaLoadBorder]   = At(0x7dea10),   // __FUNCSIG__ "int __cdecl CNWCArea::LoadBorder(class CGuiAreaLoadscreen *,float)"
        [AreaReloadBorder] = At(0x7e1e40),   // HandleServerToPlayerArea's flag-bit-2 branch; deletes (vt+0x50) + respawns (vt+0x30)
        // #615: SetWeather anchored by its own "weatherlight"/"ModelLightning" strings (the only user of either);
        //       Start/Stop are the two callees of HandleServerToPlayerArea's case 4 (ReadBOOL ? Start : Stop), bodies
        //       identical to Linux (+0x2f1, +0x2ec = 6000, CNWCAmbientSound::Rain). All field offsets Same.
        [TileSetGet]       = Nm("?GetTileSet@CNWTileSetManager@@QEAAPEAVCNWTileSet@@AEBVCResRef@@@Z"),
        [AreaSetWeather]   = At(0x7e63c0),
        [AreaStartWeather] = At(0x7e69a0),
        [AreaStopWeather]  = At(0x7e69e0),

        // --- #447: the rest of the table. The 142 Nm rows below are MECHANICAL -- MSVC mangling carries the
        //     return type Itanium drops (#433), so llvm-undname over nwmain.exe's 18237 exports pairs on
        //     (qualified name + parameter list) with nothing left ambiguous; the pass reproduces all 39
        //     hand-derived Nm rows above exactly, which is what licenses the other 142. The At rows and the
        //     two declared absences are per-row measurements or decisions; see the ticket.
        [AddPartsToDrawBuckets] = Nm("?AddPartsToDrawBuckets@Scene@@QEAAXXZ"),
        [AurResFree] = Nm("?AurResFree@@YAXPEAX@Z"),
        [AurResGet] = Nm("?AurResGet@@YAPEAXPEBD0PEAH_N@Z"),
        [AurResGetData] = Nm("?AurResGetData@@YAPEADH@Z"),
        [AurTextureBindInUnit] = Nm("?BindInUnit@CAurTexture@@UEAAXIH@Z"),
        [AurTextureBuildAndStore] = Nm("?BuildAndStore@CAurTexture@@QEAAXXZ"),
        [AurTextureBuildAndStoreTexture] = Nm("?BuildAndStoreTexture@CAurTextureBasic@@QEAAXXZ"),
        [AurTextureConstructImage] = Nm("?ConstructImage@CAurTextureBasic@@QEAAXXZ"),
        [AurTextureGetController] = Nm("?AurTextureGetController@@YAPEAVTextureController@@PEAVCAurTexture@@@Z"),
        [AurTextureGlImageBasic] = Nm("?glImageBasic@CAurTextureBasic@@QEAAH_N@Z"),
        [OperatorNewArray] = Nm("??2MaxTree@@SAPEAX_K@Z"),
        [AurTextureNullPtr] = Nm("?m_pNullTexture@CAurTextureBasic@@2V?$SafePointer@VCAurTexture@@@@A"),
        [BinBucket] = Nm("?binbucket@@3V?$List@PEAVCAurTriangleBin@@@@A"),
        [CapBuckets] = Nm("?capbuckets@@3PAVBucket@@A"),
        [FadeBuckets] = Nm("?fadebuckets@@3V?$List@VBucket@@@@A"),
        [FadingInBucket] = Nm("?fadinginbucket@@3V?$List@PEAVTileData@@@@A"),
        [MeshShadowBucket] = Nm("?meshshadowbucket@@3V?$List@PEAVPartTriMesh@@@@A"),
        [StaticShadowBucket] = Nm("?staticshadowbucket@@3V?$List@PEAVPartShadow@@@@A"),
        [BspTraverse] = Nm("?BSPTraverse@@YAXPEAVBSPNode@@P6AX0PEAX@Z1@Z"),
        [CResCtor] = Nm("??0CRes@@QEAA@XZ"),
        [ChatDoFullScreen] = LinuxOnly("#14 chat-render probe, no caller"),
        [ChatDoSplitScreen] = LinuxOnly("#14 chat-render probe, no caller"),
        [ClientAiUpdateState] = Hole("CClientAIMaster is not exported (only CServerAIMaster is)"),
        [CountPart] = Nm("?countpart@@3HA"),
        [CurrentCamera] = Nm("?CurrentCamera@@3PEAVCamera@@EA"),
        [CameraFollowControl] = Nm("?Control@CAurBehaviorCameraFollow@@UEAAHM@Z"),
        // CNWCModule has 0 exports; both found by offset signature (+0x2bc flag, +0x134, +0x88/+0x8c impulse,
        // +0x280) with tools/ghidra/ScanOffsets.java -- bodies decompile identical to Linux, vtable slots -8.
        [TurnCamera] = At(0x7cfe60),
        [TiltCamera] = At(0x7cfdb0),
        [UpdateDriveMode] = At(0x6f1e40),   // #540: the one function carrying both 499999 and 250000 (ScanOffsets)
        [SdlSetRelativeMouseMode] = Nm("SDL_SetRelativeMouseMode"),
        [SdlWarpMouseInWindow] = Nm("SDL_WarpMouseInWindow"),
        // #543: CGuiInGame is not exported; both reached from the anchored HandleServerToPlayerMessage (0x7a41a0)
        // case 0x33 -> cutscene handler 0x786450 -> minor 7's ToggleOff / UnToggleOff arms, bodies identical to Linux.
        [GuiInGameToggleOff] = At(0x6fc6f0),
        [GuiInGameUnToggleOff] = At(0x6fc900),
        [CurrentScene] = Nm("?currentscene@@3PEAVScene@@EA"),
        [CycleTidControllerVtbl] = At(0xd905c0),   // RTTI type descriptor -> COL -> vftable (tools/abi/vtable-slots.py)
        [DoBinBucket] = Nm("?DoBinBucket@Scene@@EEAAXXZ"),
        [DoDynamicMeshProjections] = Nm("?DoDynamicMeshProjections@Scene@@EEAAXPEAVPartLight@@@Z"),
        [DoGobShadows] = Nm("?DoGobShadows@Scene@@EEAAXPEAVPartLight@@@Z"),
        [EmitterGetGobAlpha] = Nm("?GetGobAlpha@PartEmitter@@AEAAMXZ"),
        [EmitterGetTileAlpha] = Nm("?GetTileAlpha@PartEmitter@@AEAAMXZ"),
        [EmitterIsSplat] = Nm("?IsSplat@MdlNodeEmitter@@QEAA_NXZ"),
        [EmitterIsTinted] = Nm("?IsTinted@MdlNodeEmitter@@QEAA_NXZ"),
        [EnableBeams] = Nm("?enablebeams@@3HA"),
        [EnableBumpyWater] = Nm("?g_nEnableBumpyWater@@3HA"),
        [EnableFBEffects] = Nm("?enableFBeffects@@3HA"),
        [EnableTextureControllers] = Nm("?enabletexturecontrollers@@3HA"),
        [ExoResMan] = Nm("?g_pExoResMan@@3PEAVCExoResMan@@EA"),
        [FadeDoors] = Nm("?FadeDoors@Scene@@UEAAXXZ"),
        [FadingOutBucket] = Nm("?fadingoutbucket@@3V?$List@PEAVTileData@@@@A"),
        [FbEffectsMask] = Nm("?m_nFramebufferEffectsEnabled@Camera@@1IA"),
        [FlagCurrentTransformDirty] = Nm("?FlagCurrentTransformDirty@GLRender@@CAXXZ"),
        [FocusLighting] = Nm("?focuslighting@@3VVector@@A"),
        [ForceDialogVisible] = LinuxOnly("empty body on both builds; nothing to skip"),
        [FullDynamicLighting] = Nm("?g_bFullDynamicLighting@@3HA"),
        [GetCanKeyholeDissolve] = Nm("?GetCanKeyholeDissolve@Camera@@QEAA_NPEBVPartTriMesh@@@Z"),
        [GetEmitterLighting] = Nm("?GetEmitterLighting@LightManager@@QEAA?AVVector@@V2@PEAV?$List@PEAVPartLight@@@@@Z"),
        [GetLensFlareLights] = Nm("?GetLensFlareLights@LightManager@@QEAAAEAV?$List@PEAVPartLight@@@@XZ"),
        [GetLightAdjustedRadius] = Nm("?GetLightAdjustedRadius@@YAMPEBVPartLight@@@Z"),
        [GetNetLayer] = Hole("CClientExoApp is not exported (only CServerExoApp is)"),
        [GetPlayerCreature] = LinuxOnly("#14 chat-render probe, no caller"),
        [GetServerNetworkAddress] = Nm("?GetServerNetworkAddress@CNetLayer@@QEAA?AVCExoString@@XZ"),
        [GlActiveTextureStage] = Nm("?m_activeTextureStage@GLRender@@0IA"),
        [GlArrayBufferBound] = Nm("?m_nArrayBufferBound@GLRender@@0IA"),
        [GlBoundTextures] = Nm("?m_boundTextures@GLRender@@0PAUBoundTexture@1@A"),
        [GlCurrentCustomProgram] = Nm("?m_pCurrentCustomProgram@GLRender@@0PEAUPROG_FIXED@1@EA"),
        [GlDeleteBuffer] = Nm("?DeleteBuffer@GLRender@@CAXI@Z"),
        [GlEnabledTextureStages] = Nm("?m_bEnabledTextureStages@GLRender@@0IA"),
        [GlGetPool] = Nm("?GetPool@GLRender@@CAPEBXI@Z"),
        [GlGlobalShaderConstantsF] = Nm("?m_globalShaderConstantsf@GLRender@@0PAMA"),
        [GlGlobalShaderConstantsI] = Nm("?m_globalShaderConstantsi@GLRender@@0PAHA"),
        [GlGlobalShaderConstantsV] = Nm("?m_globalShaderConstantsv@GLRender@@0PAVVector@@A"),
        [GlGlobalShaderConstantsV4] = Nm("?m_globalShaderConstantsv4@GLRender@@0PAVVector4@@A"),
        [GlMaxElementIndices] = Nm("?GetVertexBufferMaxElementIndices@GLRender@@CAHXZ"),
        [GlMaxElementVertices] = Nm("?GetVertexBufferMaxElementVertices@GLRender@@CAHXZ"),
        [GlOrphanBuffers] = Nm("?g_bOrphanBuffersBeforeWrite@@3HA"),
        [GlPoolTableAnchor] = At(0x128ead8),   // the {base,count} pair itself -- GetPoolBuffer loads it, UnlockPool leas it
        [GlRenderBones] = Nm("?m_bones@GLRender@@0PAVMatrix4x4@@A"),
        [GlRenderVertexData] = Nm("?RenderVertexData@GLRender@@CAXPEAVVertexDataObject@@IW4AuroraPrimitiveTypes@Aurora@@II@Z"),
        [GlRenderVertexStreamData] = Nm("?RenderVertexStreamData@GLRender@@CAXPEAVVertexStreamDataObject@@W4AuroraPrimitiveTypes@Aurora@@II@Z"),
        [GlSetUniforms] = Nm("?SetUniforms@GLRender@@CAXAEAUPROG_FIXED@1@@Z"),
        [GlSetVertexDataObject] = Nm("?SetVertexDataObject@GLRender@@CAXPEAVVertexDataObject@@@Z"),
        [GlShaderRuntimeData] = Nm("?m_shaderExposedRuntimeData@GLRender@@0UShaderExposedRuntimeData@1@A"),
        [GlTotalMemPool] = Nm("?totalmempool@@3HA"),
        [GlUnlockPool] = Nm("?UnlockPool@GLRender@@CAXIII@Z"),
        [GlUseProgramConditionally] = Nm("?GlUseProgramConditionally@GLRender@@CAXI@Z"),
        [GobBucket] = Nm("?gobbucket@@3V?$List@PEAVGob@@@@A"),
        [GobCollectLights] = Nm("?CollectLights@Gob@@QEAAXAEAV?$List@PEAVPartLight@@@@@Z"),
        [GobGetAdjustedScale] = Nm("?GetAdjustedScale@Gob@@UEBAMXZ"),
        [GrassEnabled] = Nm("?m_bGrassIsEnabled@Scene@@0_NA"),
        [GrassFarDistance] = Nm("?m_fGrassFarRenderDistance@Scene@@0MA"),
        [GrassInfo] = Nm("?g_GrassInfo@@3V?$List@VCAurGrassInfo@@@@A"),
        [GrassNearRadius] = Nm("?m_fNearRadiusSquared@CAurTriangleBin@@2MA"),
        [GuiUpdateAndRender] = Nm("?UpdateAndRender@CGuiMan@@QEAAXM@Z"),
        [HideCompass] = At(0x6f9060),
        [LightAreaAmbient] = Nm("?m_lightAreaAmbient@GLRender@@0VVector@@A"),
        [LightAreaDiffuse] = Nm("?m_lightAreaDiffuse@GLRender@@0VVector@@A"),
        [LightAreaDiffuseDir] = Nm("?m_lightAreaDiffuseDirection@GLRender@@0VVector@@A"),
        [LightManagerOnlyGob] = Nm("?m_pOnlyGob@LightManager@@0PEAVGob@@EA"),
        [LoadMatrixIdentity] = Nm("?LoadMatrixIdentity@RenderInterface@@SAXXZ"),
        [MAurMtxStack] = Nm("?m_aurMtxStack@@3PAVaurMatrixStack@@A"),
        [MCurrentMatrix] = Nm("?m_nCurrentMatrix@GLRender@@0IA"),
        [ManageSceneBSP] = Nm("?ManageSceneBSP@@YAXPEAVScene@@@Z"),
        [MatGetTexture] = Nm("?GetTexture@Material@@QEAAPEAVCAurTexture@@H@Z"),
        [MatHasBumpyShinyMap] = Nm("?HasBumpyShinyMap@Material@@QEAA_NXZ"),
        [MatHasEnvMap] = Nm("?HasEnvMap@Material@@QEAA_NXZ"),
        [MeshBuckets] = Nm("?meshbuckets@@3V?$List@VBucket@@@@A"),
        [MsaaSamples] = Nm("?g_nMSAA@@3HA"),
        [MultMatrix] = Nm("?MultMatrix@RenderInterface@@SAXPEAM@Z"),
        [NetProcessReceivedFrames] = Nm("?ProcessReceivedFrames@CNetLayer@@QEAAXH@Z"),
        [NkSdlCopyEvent] = Hole("nuklear is not exported by nwmain.exe"),
        [NkSdlRender] = Hole("nuklear is not exported by nwmain.exe"),
        [NkWindowIsAnyHovered] = Hole("nuklear is not exported by nwmain.exe"),
        [Nuklear] = Hole("nuklear is not exported by nwmain.exe"),
        [OnGainFocus] = LinuxOnly("empty stub reached by indirect dispatch; OS focus handling covers it"),
        [OnLostFocus] = LinuxOnly("empty stub reached by indirect dispatch; OS focus handling covers it"),
        [PartAnimate] = Nm("?Animate@Part@@UEAAXM@Z"),
        [PartDanglyAnimate] = Nm("?Animate@PartDanglyMesh@@UEAAXM@Z"),
        [PartDanglyMeshVtbl] = At(0xd88698),   // RTTI type descriptor -> COL -> vftable (tools/abi/vtable-slots.py)
        [PartDtor] = Nm("??1Part@@UEAA@XZ"),
        [GobAppend] = LinuxOnly("#397 diagnostic watch; no swap forensics on Windows yet"),
        [GobAmputate] = LinuxOnly("#397 diagnostic watch; no swap forensics on Windows yet"),
        // #275: ParseField and Init are called (not inlined) on Windows -- CreateSharedMaterial/GetSharedMaterial.
        [SharedMaterialParseField] = Nm("?ParseField@SharedMaterial@@QEAAXPEBD@Z"),
        [SharedMaterialInit] = Nm("?Init@SharedMaterial@@QEAAXPEBD@Z"),
        // The exported ??1SharedMaterial has NO callers: MSVC inlined it into the scalar deleting destructor
        // (this, flags), which RemoveRef and RefreshSharedMaterial call -- the same two callers as Linux D2.
        [SharedMaterialDtor] = At(0x81f80),
        [LoadTexture] = Nm("?LoadTexture@@YAPEAVCAurTexture@@PEBD@Z"),
        [AurTextureRelease] = Nm("?AurTextureRelease@@YAXPEAVCAurTexture@@@Z"),
        [PartEmitterDtor] = Nm("??1PartEmitter@@UEAA@XZ"),
        [PartGetIsOpaque] = Nm("?GetIsOpaque@PartTriMesh@@QEAA_NXZ"),
        [PartGetWorld] = Nm("?GetWorld@Part@@UEAAXAEAVVector@@AEAVQuaternion@@@Z"),
        [PartSkinVtbl] = At(0xd88438),   // RTTI type descriptor -> COL -> vftable (tools/abi/vtable-slots.py)
        [PltOnGpu] = Nm("?g_bGeneratePLTOnGPU@@3HA"),
        [PopCurrentMatrix] = Nm("?PopCurrentMatrix@RenderInterface@@SAXXZ"),
        [PrimTypeTranslationTable] = At(0xd8ad20),   // the {0,1,3,4,5,6,2} run in .rdata; RenderVertexData leas it three times
        [ProjectionAlpha] = Nm("?projectionalpha@@3MA"),
        [PushCurrentMatrix] = Nm("?PushCurrentMatrix@RenderInterface@@SAXXZ"),
        [QuickbarReposition] = At(0x860010),
        [ReloadGrass] = Nm("?ReloadGrass@Scene@@UEAAXXZ"),
        [RenderDrawBucket] = Nm("?RenderDrawBucket@Scene@@QEAAXH@Z"),
        [RenderDynamicShadows] = Nm("?RenderDynamicShadows@Scene@@QEAAXXZ"),
        [RenderEmitter] = Nm("?RenderEmitter@PartEmitter@@QEAAXXZ"),
        [RenderShadowPlanes] = Nm("?RenderShadowPlanes@Scene@@QEAAXHH@Z"),
        [RenderSinglePass] = Nm("?RenderSinglePass@Scene@@UEAAXXZ"),
        [RenderSkyBoxes] = Nm("?RenderSkyBoxes@Scene@@QEAAXXZ"),
        [RenderStaticShadows] = Nm("?RenderStaticShadows@Scene@@EEAAXXZ"),
        [ComputeStaticLighting] = Nm("?ComputeStaticLighting@CAurScene@@UEAAXXZ"),
        [ResManDemand] = Nm("?Demand@CExoResMan@@QEAAPEAXPEAVCRes@@@Z"),
        [ResManExists] = Nm("?Exists@CExoResMan@@QEAAHAEBVCResRef@@GPEAI@Z"),
        [ResManGetResObject] = Nm("?GetResObject@CExoResMan@@QEAAPEAVCRes@@AEBVCResRef@@G@Z"),
        [ResManRelease] = Nm("?Release@CExoResMan@@QEAAHPEAVCRes@@@Z"),
        [ResManReleaseObj] = Nm("?ReleaseResObject@CExoResMan@@QEAAHPEAVCRes@@_N@Z"),
        [ResManSetResObject] = Nm("?SetResObject@CExoResMan@@QEAAXAEBVCResRef@@GPEAVCRes@@@Z"),
        [ResManUpdate] = Nm("?Update@CExoResMan@@QEAAXI@Z"),
        [RiCreateTexture] = Nm("?CreateTexture@RenderInterface@@SAXPEAVCAurTexture@@_NH@Z"),
        [RiDeleteTextures] = Nm("?DeleteTextures@RenderInterface@@SAXIPEBI@Z"),
        [RiDisable] = Nm("?Disable@RenderInterface@@SAXW4AuroraEnableEnums@Aurora@@@Z"),
        [RiEnable] = Nm("?Enable@RenderInterface@@SAXW4AuroraEnableEnums@Aurora@@@Z"),
        [RiGetGlobalShaderParamf] = Nm("?GetGlobalShaderParamf@RenderInterface@@SAMW4AurGlobalShaderParamf_t@Aurora@@@Z"),
        [RiGetGlobalShaderParami] = Nm("?GetGlobalShaderParami@RenderInterface@@SAHW4AurGlobalShaderParami_t@Aurora@@@Z"),
        [RiIsEnabled] = Nm("?IsEnabled@RenderInterface@@SAEW4AuroraEnableEnums@Aurora@@@Z"),
        [RiSetBlendFunc] = Nm("?SetBlendFunc@RenderInterface@@SAXW4AuroraBlendModes@Aurora@@0@Z"),
        [RiSetModelMatrix] = Nm("?SetModelMatrix@RenderInterface@@SAXAEBVMatrix4x4@@@Z"),
        [SceneBspGetTileData] = Nm("?SceneBSPGetTileData@@YAPEAVTileData@@PEAVScene@@AEBVVector@@@Z"),
        [SceneDeleteTile] = Nm("?DeleteTile@Scene@@UEAAXAEBVVector@@@Z"),
        [SceneFocusObject] = Nm("?m_pFocusObject@Scene@@0V?$SafePointer@VCAurObject@@@@A"),
        [SceneGetWindVector] = Nm("?GetWindVector@Scene@@QEAA?AVVector@@AEBV2@M@Z"),
        [SceneHitCheck] = Nm("?HitCheck@Scene@@UEAAHVVector@@0PEAV2@PEAVCHitInfo@@@Z"),
        [HitInfoReset] = Nm("?Reset@CHitInfo@@QEAAXH_JH@Z"),
        [SceneInitBsp] = Nm("?InitBSP@Scene@@UEAAXHHH_NM@Z"),
        [SceneSequence] = Nm("?Sequence@Scene@@2IA"),
        [SceneSpawnTile] = Nm("?SpawnTile@Scene@@UEAAPEAVCAurObject@@PEBDAEBVVector@@HHPEAVQuaternion@@@Z"),
        [SetMatrixMode] = Nm("?SetMatrixMode@RenderInterface@@SAXW4AuroraTransStates@Aurora@@@Z"),
        [SetModelMatrix] = Nm("?SetModelMatrix@GLRender@@CAXAEBVMatrix4x4@@@Z"),
        [SetMouseCursor] = Hole("CCachedMouseCursorManager is not exported"),
        [SetOrthoTransform] = Nm("?SetOrthoTransform@RenderInterface@@SAXNNNNNN@Z"),
        [SetPositionScreen] = Nm("?SetPositionScreen@CGuiPanel@@QEAAXHH@Z"),
        [SetPriMsgFilter] = LinuxOnly("#14 chat-render probe, no caller"),
        [ShowCompass] = At(0x6fb490),
        [SsaoIntensity] = Nm("?ssao_intensity@@3MA"),
        [ToonColorEdge] = Nm("?toon_color_edge_threshold@@3MA"),
        [ToonDepthEdge] = Nm("?toon_depth_edge_threshold@@3MA"),
        [ToonEdgeHardness] = Nm("?toon_edge_hardness@@3MA"),
        [TriggerBucket] = Nm("?triggerbucket@@3V?$List@PEAVPartTrigger@@@@A"),
        [UpdateMouseCursorAppearance] = Hole("CClientExoAppInternal is not exported"),
        [VdoCommitItemDynamicData] = Nm("?CommitItemDynamicData@VertexDataObject@@QEAAXII@Z"),
        [VdoGetItemDynamicData] = Nm("?GetItemDynamicData@VertexDataObject@@QEAAPEAEI@Z"),
        [VdoRepack] = Nm("?Repack@VertexDataObject@@QEAAXXZ"),
        [VertexPrimGenVelocity] = Nm("?GenerateVelocityArray@VertexPrimitiveDangly@@QEAAXXZ"),
        [ViewportSetViewport] = Nm("?SetViewport@CGuiViewport@@QEAAXHHHH@Z"),
    };

    /// <summary>Why a symbol does not resolve on Windows, in the words of its own row. A key that is missing
    /// entirely is the form <c>build.sh</c> forbids, so it reads as the bug it is rather than as a decision.</summary>
    private static string Why(Win w) =>
        w.Why is null ? "no row in the Windows table -- undeclared (build.sh gate)"
        : w.Counted ? $"not yet anchored: {w.Why}"
        : $"Linux-only by design: {w.Why}";

    /// <summary>Symbols declared as holes: an absence that is counted, not decided. The finish line is zero,
    /// exactly as <see cref="EngineLayout.UnresolvedCount"/> is for offsets.</summary>
    public static int HoleCount
    {
        get
        {
            int n = 0;
            foreach (Win w in _win.Values) if (w.Counted) n++;
            return n;
        }
    }

    /// <summary>Join <c>/knobs</c>, beside the layout counter. Once, from the host.</summary>
    public static void Register() => KnobSurface.Note(() =>
    {
        int nm = 0, at = 0, only = 0;
        foreach (Win w in _win.Values)
        {
            if (w.Name is not null) nm++;
            else if (w.Rva != 0) at++;
            else if (!w.Counted) only++;
        }
        return $"windows symbols: {_win.Count} row(s), {nm} exported + {at} anchored, " +
               $"{only} Linux-only by design, {HoleCount} not yet anchored";
    });

    // Runtime base of nwmain (ASLR-relocated); RVAs are added to this, never the static file base.
    private static readonly IntPtr Base = NativeLibrary.GetMainProgramHandle();
    private static bool? _buildOk;

    /// <summary>
    /// Resolve a Linux mangled symbol to an absolute address for the running client.
    /// Linux: the Itanium name directly. Windows: name export, build-locked RVA, or skip.
    /// Zero = unresolved (already logged).
    /// </summary>
    [DllImport("libdl.so.2", EntryPoint = "dlsym")]
    private static extern IntPtr Dlsym(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string symbol);
    [DllImport("libdl.so.2", EntryPoint = "dlopen")]
    private static extern IntPtr Dlopen(IntPtr path, int flags);

    public static IntPtr Resolve(string linux, ExtenderLog? log)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (NativeLibrary.TryGetExport(Base, linux, out IntPtr p)) return p;
            // ponytail: TryGetExport misses symbols under NativeAOT + LD_PRELOAD; dlsym(NULL) searches
            // the global scope. macOS needs "libdl.dylib" — cross that bridge when hardened-runtime unblocks.
            IntPtr alt = Dlsym(Dlopen(IntPtr.Zero, 1), linux);
            if (alt != IntPtr.Zero)
            {
                log?.Info($"resolve: TryGetExport missed {linux}, dlsym hit");
                return alt;
            }
            return IntPtr.Zero;
        }

        if (!_win.TryGetValue(linux, out Win w) || (w.Name is null && w.Rva == 0))
        {
            log?.Info($"symbol: no Windows resolution for {linux} ({Why(w)}); skipping");
            return IntPtr.Zero;
        }
        if (w.Name is not null)
        {
            if (NativeLibrary.TryGetExport(Base, w.Name, out IntPtr p)) return p;
            log?.Info($"symbol: Windows export not found: {w.Name}");
            return IntPtr.Zero;
        }
        return BuildMatches(log) ? Base + w.Rva : IntPtr.Zero;   // RVA path is build-locked
    }

    /// <summary>
    /// Unit gate (#419): a feature names the symbols it cannot run without; if any fails to resolve the WHOLE
    /// feature stands down, with one line naming the first missing symbol and the cause -- a symbol with no
    /// Windows anchor is by design, a stamp mismatch is rot. Optional members are simply not listed. Call at
    /// the top of <c>Initialize</c>, before the first <c>Install</c>: a per-symbol skip after that is exactly
    /// the half-live feature (input hooked, render not) this exists to prevent.
    /// </summary>
    public static bool Require(ExtenderLog log, string feature, params string[] symbols)
    {
        foreach (string sym in symbols)
        {
            if (Resolve(sym, log) != IntPtr.Zero) continue;
            string why = !OperatingSystem.IsWindows() ? "not exported by this client build"
                : !_win.TryGetValue(sym, out Win w) || (w.Name is null && w.Rva == 0) ? Why(w)
                : w.Name is not null ? $"Windows export {w.Name} missing"
                : $"nwmain.exe build 0x{_stamp:x8} != expected 0x{WindowsBuildStamp:x8} (rot: re-derive, docs/windows-symbols.md)";
            log.Always($"{feature}: standing down as a unit -- {sym}: {why}");
            return false;
        }
        return true;
    }

    /// <summary>
    /// The loaded module's PE TimeDateStamp must equal <see cref="WindowsBuildStamp"/>, else the
    /// RVAs address a different build. Checked once; on mismatch all RVA hooks skip.
    /// </summary>
    private static bool BuildMatches(ExtenderLog? log)
    {
        if (_buildOk is bool cached) return cached;
        byte* b = (byte*)Base;
        int lfanew = *(int*)(b + 0x3c);                 // DOS header e_lfanew
        _stamp = *(uint*)(b + lfanew + 4 + 4);          // PE sig(4) + COFF: TimeDateStamp at +4
        bool ok = _stamp == WindowsBuildStamp;
        if (!ok)
        {
            log?.Info($"symbol: nwmain.exe build 0x{_stamp:x8} != expected 0x{WindowsBuildStamp:x8}; " +
                      "RVA hooks disabled -- re-derive (docs/windows-symbols.md)");
            // #457: no RotNotice here. The key gate in ServiceHost.Run refuses the same client before a
            // symbol is ever resolved, so this branch can no longer be the first to notice rot -- the popup
            // belongs where the policy is.
        }
        _buildOk = ok;
        return ok;
    }

    /// <summary>The observed PE TimeDateStamp, so the unit-gate line can name the build even when the first
    /// resolver ran without a log.</summary>
    private static uint _stamp;

    /// <summary>
    /// #421: how a player finds out. Under rot the whole extender stands down (#457), so there is no chatbox
    /// and no engine chat path (AppendToMsgBuffer is itself an RVA) to put a line on; the one surface that
    /// survives is user32. A Steam-launched version.dll proxy has no other surface at all. Once per session,
    /// on its own thread -- a modal parks the thread that raised it, and boot must not wait for a player to
    /// click OK. The game continues vanilla either way.
    /// </summary>
    internal static void RotNotice(string text)
    {
        if (!OperatingSystem.IsWindows()) return;
        // An escaping exception on a bare thread kills the process -- the one outcome the build lock exists
        // to prevent. Nothing here is worth a crash.
        new System.Threading.Thread(() =>
        {
            try
            {
                MessageBoxW(IntPtr.Zero, text, "NWNCX:EE", MB_ICONWARNING);
            }
            catch { }
        }) { IsBackground = true, Name = "cx-rot-notice" }.Start();
    }

    private const uint MB_ICONWARNING = 0x30;

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

}
