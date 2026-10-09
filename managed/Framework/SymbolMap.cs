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
    [Fn("int(uint key, uint flags)")]
    public const string HandleWMKeyDown   = "_Z22HandleWMKeyDownMessagejj";
    [Fn("int(IntPtr ev)")]
    public const string WinMessageHandler = "_Z17WinMessageHandlerP9SDL_Event";
    [Fn("void(IntPtr window)")]
    public const string SwapBuffers       = "_ZN8GLRender11SwapBuffersEP10SDL_Window";
    /// <summary>#336 step 5: the engine main loop's four non-render steps, timed by <c>EngineZonesService</c>.
    /// <c>CNetLayer::ProcessReceivedFrames(int)</c> @0x5b92c0, <c>CExoResMan::Update(unsigned)</c> @0x536c60,
    /// <c>CClientAIMaster::UpdateState()</c> @0x991c30, <c>CGuiMan::UpdateAndRender(float)</c> @0x580450.</summary>
    [Fn("long(CNetLayer this, int frames)")]
    public const string NetProcessReceivedFrames = "_ZN9CNetLayer21ProcessReceivedFramesEi";
    [Fn("long(IntPtr resman, uint arg)")]
    public const string ResManUpdate = "_ZN10CExoResMan6UpdateEj";
    /// <summary>CNetLayer::GetServerNetworkAddress() @0x5ba070 returns a CExoString BY VALUE ("a.b.c.d:port"):
    /// Itanium <c>(CExoString* sret, CNetLayer* this)</c>, Win64 <c>(this, sret)</c>. The net layer itself is the
    /// <see cref="EngineLayout.InternalNetLayer"/> field (what <c>CClientExoApp::GetNetLayer</c> returns). Read by
    /// <see cref="PlayerIdentity"/> for the radial layout's key and by <c>ServerPing</c> for the /fps readout.</summary>
    [Fn("sret(CNetLayer this)")]
    public const string GetServerNetworkAddress = "_ZN9CNetLayer23GetServerNetworkAddressEv";
    [Fn("long(IntPtr master)")]
    public const string ClientAiUpdateState = "_ZN15CClientAIMaster11UpdateStateEv";
    [Fn("long(CGuiMan this, float dt)")]
    public const string GuiUpdateAndRender = "_ZN7CGuiMan15UpdateAndRenderEf";
    // window focus (CClientExoApp). Empty stubs reached by indirect dispatch, so no Windows anchor
    // was derivable -- they skip there, which the OS focus handling already covers.
    [Fn("void(CClientExoApp this)")]
    public const string OnGainFocus       = "_ZN13CClientExoApp11OnGainFocusEv";
    [Fn("void(CClientExoApp this)")]
    public const string OnLostFocus       = "_ZN13CClientExoApp11OnLostFocusEv";
    // chat prompt (typing gate)
    [Fn("void(CGuiInGame this)")]
    public const string SetFocusChatPrompt   = "_ZN10CGuiInGame18SetFocusChatPromptEv";           // chat opened by key
    [Fn("void(CGuiInGameChatPrompt this)")]
    public const string ChatHandleLMouseDown = "_ZN20CGuiInGameChatPrompt24HandleLMouseDownGuiEventEv"; // clicked into
    [Fn("void(CGuiInGameChatPrompt this)")]
    public const string ChatHandleEnter      = "_ZN20CGuiInGameChatPrompt11HandleEnterEv";
    [Fn("void(CGuiInGameChatPrompt this)")]
    public const string ChatHandleEsc        = "_ZN20CGuiInGameChatPrompt9HandleEscEv";
    [Fn("void(CGuiInGameChatPrompt this)")]
    public const string ChatHandleLoseFocus  = "_ZN20CGuiInGameChatPrompt41HandleLoseKeyboardFocusLMouseDownGuiEventEv";
    /// <summary>CGuiEdit::GetText() @0x592680 -- the prompt's typed line, joined from the edit's CAurString lines.
    /// Returns a CExoString BY VALUE, i.e. <b>sret</b>: Itanium <c>(CExoString* out, CGuiEdit* this)</c>, Win64
    /// <c>(this, out)</c>; the buffer it writes is CALLER-OWNED (zero the struct first, dtor it after). What
    /// <c>HandleEnter</c> itself calls first, so the #580 chain reads exactly what vanilla would have sent.</summary>
    [Fn("sret(CGuiEdit this)")]
    public const string GuiEditGetText       = "_ZN8CGuiEdit7GetTextEv";
    // ---- chat edit control (build slice #20): focus suppression, keyboard-mode gate, SDL
    //      text-input + clipboard. Chatbox-specific symbols are Linux-only (no _win entries -> skip-with-log
    //      on Windows). EXCEPTIONS: ExoInput + GetKeyboardMode carry _win mappings -- the hotkey
    //      keyboard-mode gate (cross-platform) reads them, and both are name-exported on Windows. ----
    /// <summary>CGuiInGame::ShowChatPrompt() -- a chat opener we skip to suppress vanilla focus.</summary>
    [Fn("void(CGuiInGame this)")]
    public const string ShowChatPrompt = "_ZN10CGuiInGame14ShowChatPromptEv";
    /// <summary>CGuiInGameChatPrompt::CGuiInGameChatPrompt() -- hooked on WINDOWS ONLY (#419): MSVC inlined Show into
    /// it, so the prompt is activated at birth there; Linux's ctor calls Show, which the skip already covers.</summary>
    [Fn("IntPtr(CGuiInGameChatPrompt this)")]
    public const string ChatPromptCtor = "_ZN20CGuiInGameChatPromptC1Ev";
    /// <summary>CGuiPanel::Deactivate(int) -- what vanilla Hide pairs with Activate; undoes the ctor-inlined Show.</summary>
    [Fn("void(CGuiPanel this, int arg)")]
    public const string GuiPanelDeactivate = "_ZN9CGuiPanel10DeactivateEi";
    /// <summary>CGuiInGame::PrepChatPromptForTellToPlayer(CNWCPlayer*) -- (this, player) chat opener; skipped.</summary>
    [Fn("void(CGuiInGame this, IntPtr player)")]
    public const string PrepChatPromptForTell = "_ZN10CGuiInGame29PrepChatPromptForTellToPlayerEP10CNWCPlayer";
    /// <summary>CExoInput* global; deref for the instance to drive KeyboardModeOn/Off.</summary>
    public const string ExoInput = "g_pExoInput";
    /// <summary>CExoInput::KeyboardModeOn() -- engine gameplay-key gate on (text entry).</summary>
    [Fn("void(IntPtr input)")]
    public const string KeyboardModeOn = "_ZN9CExoInput14KeyboardModeOnEv";
    /// <summary>CExoInput::KeyboardModeOff() -- gameplay-key gate off (resume movement).</summary>
    [Fn("void(IntPtr input)")]
    public const string KeyboardModeOff = "_ZN9CExoInput15KeyboardModeOffEv";
    /// <summary>CExoInput::GetKeyboardMode() -- nonzero while the engine is capturing text (chat, console,
    /// any vanilla edit field); the signal to suppress gameplay hotkeys.</summary>
    [Fn("int(IntPtr input)")]
    public const string GetKeyboardMode = "_ZN9CExoInput15GetKeyboardModeEv";
    /// <summary>CAurora::GetGUIScale() -- static float accessor for the engine's live GUI-scale multiplier
    /// (floor 1.0; changes with resolution / the options slider). We fold it into the chatbox scale so our
    /// overlay tracks the game's UI scaling. Name-exported on Windows (CAurora is a shared-engine class).</summary>
    [Fn("float()")]
    public const string GetGuiScale = "_ZN7CAurora11GetGUIScaleEv";
    /// <summary>TurnOffKeyboardModal() -- the free function vanilla runs from CGuiEdit::SetFocus right after
    /// KeyboardModeOn: resets modal keys, clears the WASD drive-mode latch, and sends AbortDriveControl so the
    /// server stops too. We SkipOriginal the vanilla focus path, so we must call it ourselves or movement
    /// sticks when chat grabs the keyboard mid-walk (#34). Takes no args -- it walks g_pAppManager itself.</summary>
    [Fn("void()")]
    public const string TurnOffKeyboardModal = "_Z20TurnOffKeyboardModalv";
    [Fn("void()")]
    public const string SdlStartTextInput   = "SDL_StartTextInput";
    [Fn("IntPtr()")]
    public const string SdlGetClipboardText = "SDL_GetClipboardText";
    [Fn("int(byte* text)")]
    public const string SdlSetClipboardText = "SDL_SetClipboardText";
    [Fn("void(IntPtr block)")]
    public const string SdlFree             = "SDL_free";

    // client<->server messages (CNWCMessage)
    [Fn("uint(IntPtr msg, byte* data, uint len)")]
    public const string HandleServerToPlayerMessage = "_ZN11CNWCMessage27HandleServerToPlayerMessageEPhj";
    [Fn("uint(IntPtr msg, byte subcmd)")]
    public const string HandleServerToPlayerLogin   = "_ZN11CNWCMessage25HandleServerToPlayerLoginEh";
    /// <summary>Returns the net layer's send verdict (0 = nothing went out): the tail is
    /// <c>mov %r13d,%eax</c> off <c>CNetLayer::SendMessageToPlayer</c> (@0x7f9a85).</summary>
    [Fn("int(IntPtr self, byte major, byte minor, byte* buf, uint len)")]
    public const string SendPlayerToServerMessage   = "_ZN11CNWCMessage25SendPlayerToServerMessageEhhPhj";
    [Fn("void(IntPtr msg, IntPtr name, int value)")]
    public const string SendDeviceAdvertiseProperty = "_ZN11CNWCMessage41SendPlayerToServerDeviceAdvertisePropertyERK10CExoStringi";
    // ---- the area-loaded hold (#672, research CX-132). Landed for #674/#675;
    //      nothing hooks them yet. ----
    /// <summary>CNWCMessage::SendPlayerToServerArea_AreaLoaded() @0x7fa0c0 -- major 4 minor 3, no payload; the
    /// send whose arrival makes the server place the player (<c>MovePlayerToArea</c>). Three call sites: the
    /// post-loading-screen tail of <see cref="HandleUpdateWorkRemaining"/> and the two movie exits.</summary>
    [Fn("void(IntPtr msg)")]
    public const string SendAreaLoaded = "_ZN11CNWCMessage33SendPlayerToServerArea_AreaLoadedEv";
    /// <summary>CNWCArea::SetTile(CNWCTile*, int id, int orientation, int height) @0x708e60 -- the server tile-change
    /// message's one callee that re-spawns a tile's model and lights (HandleServerToPlayerArea case 0x14). The
    /// lightmap's static set reopens on it. <c>(this, tile, int, int, int) -> void</c>, no sret.</summary>
    [Fn("void(CNWCArea this, IntPtr tile, int id, int orientation, int height)")]
    public const string AreaSetTile = "_ZN8CNWCArea7SetTileEP8CNWCTileiii";
    /// <summary>CNWCMessage::HandleServerToPlayerUpdate_WorkRemaining() @0x781a30 -- drives the load bar and, on
    /// the last tick, fades the loadscreen and sends AreaLoaded (or latches <c>internal+0x58c</c> behind an intro
    /// movie). The hold's hook point. <c>(this) -> int</c>.</summary>
    public const string HandleUpdateWorkRemaining = "_ZN11CNWCMessage40HandleServerToPlayerUpdate_WorkRemainingEv";
    /// <summary>CClientExoApp::StartLoadBar(int state, char const* image, int rebuild) @0x638a70 -- forwarder to the
    /// internal; disables input, stores <c>state</c> at <c>internal+0x450</c> (1 = local stall, 2 = area transition).</summary>
    [Fn("IntPtr(CClientExoApp this, int state, IntPtr image, int rebuild)")]
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
    /// <summary>Declared as the SKIP shape, not the full ABI: nothing calls these, only installs a handler that
    /// returns without forwarding, so the trailing arguments are never read and <c>long</c> leaves rax readable as
    /// false/null (the Itanium mangling hides each real return type).</summary>
    [Fn("long(IntPtr self, IntPtr text)")]
    public const string AppendToChatBuffer  = "_ZN20CGuiInGameChatWindow18AppendToChatBufferE10CExoStringjji7CResRef";
    /// <inheritdoc cref="AppendToChatBuffer"/>
    [Fn("long(IntPtr self)")]
    public const string ChatWindowSetFilter = "_ZN20CGuiInGameChatWindow9SetFilterEjPP17CGuiInGameMessagej";
    // ...and the chrome: SkipOriginal Show on the docked window + input prompt so they never activate into
    // the render tree (Show pairs with Activate, Hide with Deactivate). The gate reads module-presence, not
    // the window's IsVisible flag, so keeping the window un-shown doesn't hide our own box.
    /// <inheritdoc cref="AppendToChatBuffer"/>
    [Fn("long(IntPtr self)")]
    public const string ChatWindowShow      = "_ZN20CGuiInGameChatWindow4ShowEv";
    /// <inheritdoc cref="AppendToChatBuffer"/>
    [Fn("long(IntPtr self)")]
    public const string ChatPromptShow      = "_ZN20CGuiInGameChatPrompt4ShowEv";

    // dialog reimplementation (#dialog): SkipOriginal the three GUI-layer entry points so the vanilla
    // CGuiInGameChatDialog panel is never created/populated/activated (its ptr at CGuiInGame+0x70 stays
    // null); we capture the already-decoded data and render our own overlay. The message layer
    // (HandleServerToPlayerDialog) runs unmodified -> VO, camera, speaker tracking preserved. CloseDialog is
    // call-through (panel null -> it guards and just runs ResetGuiState). Replies go out via
    // SendPlayerToServerDialog_Reply (major 0x14, minor 0x03) with ClientApi.Message() as `this`. RVA on
    // Windows (#420); ForceDialogVisible is an empty body on both builds and has no Windows anchor (optional).
    [Fn("long(CGuiInGame this, IntPtr text, uint speakerId, uint arg)")]
    public const string ShowDialogEntry   = "_ZN10CGuiInGame15ShowDialogEntryERK10CExoStringjj";
    [Fn("long(CGuiInGame this, int count, IntPtr texts, IntPtr ids, int arg4, uint arg5, int arg6)")]
    public const string ShowDialogReplies = "_ZN10CGuiInGame17ShowDialogRepliesEiP10CExoStringPjiji";
    [Fn("long(CGuiInGame this, uint arg1, uint arg2, IntPtr text)")]
    public const string ShowReplyChosen   = "_ZN10CGuiInGame15ShowReplyChosenEjj10CExoString";
    [Fn("void(CGuiInGame this)")]
    public const string CloseDialog       = "_ZN10CGuiInGame11CloseDialogEv";
    [Fn("void(IntPtr msg, uint dialogId, uint replyIndex, byte isAbort, uint pageId)")]
    public const string SendDialogReply   = "_ZN11CNWCMessage30SendPlayerToServerDialog_ReplyEjjhj";
    /// <summary>CGuiInGameChatDialog::ForceDialogVisible(float) -- a timer-driven re-show (arg is likely the VO
    /// duration). SkipOriginal it too: its body only touches the vanilla panel we never create, so if the
    /// message layer calls it against our null panel it would deref garbage. Skipping is always correct.</summary>
    [Fn("long(IntPtr chatDialog, float arg)")]
    public const string ForceDialogVisible = "_ZN20CGuiInGameChatDialog18ForceDialogVisibleEf";
    /// <summary>CClientExoApp::GetGameObject(uint) -> CGameObject* (or null). Speaker-id -> object for the
    /// dialog portrait; the object's portrait comes from its virtual GetPortrait (vtable+0x1d8), so no extra
    /// symbol is needed for that. Resolved via the same *(*g_pAppManager) chain ShowDialogEntry uses.</summary>
    [Fn("CGameObject(CClientExoApp this, uint id)")]
    public const string GetGameObject = "_ZN13CClientExoApp13GetGameObjectEj";

    /// <summary>CClientExoApp::GetObjectName(uint objId, CExoString* out) -- writes the speaker's display name
    /// (creature full name / item / placeable-door StrRef via TLK) into a caller-allocated 16-byte CExoString
    /// {char* buf@0; uint len@8; uint cap@0xc}. The <em>out buffer is caller-owned heap</em> (fresh operator
    /// new[] every call, or a Steal from a temp) -- it MUST be zeroed before the call (the engine frees any
    /// non-null buf first) and freed after via <see cref="ExoStringDtor"/>. Empty name = len==0/buf==NULL. Pass
    /// the CClientExoApp* (the wrapper does the +8 to the internal itself). Confirmed by Ghidra decompile.</summary>
    [Fn("void(CClientExoApp this, uint objId, IntPtr name)")]
    public const string GetObjectName = "_ZN13CClientExoApp13GetObjectNameEjP10CExoString";
    // Frees GetObjectName's caller-owned CExoString buffer -> the existing ExoStringDtor (in "misc helpers").

    /// <summary>CGuiInGameChatPrompt::SetFocus(CExoString) -- opens the vanilla prompt with a prefill
    /// string. The player-list "Tell Player" button calls this directly (with '/t "Name" '), bypassing
    /// the three opener hooks -- so it gets its own skip-original hook. By-value CExoString is
    /// non-trivial -> lowered to a pointer arg.</summary>
    [Fn("void(CGuiInGameChatPrompt this, IntPtr text)")]
    public const string ChatPromptSetFocus  = "_ZN20CGuiInGameChatPrompt8SetFocusE10CExoString";
    /// <summary>The outbound chat senders (#22), all on <c>CNWCMessage</c>. Each <c>CExoString</c> argument is a
    /// non-trivial C++ class, so it travels by invisible reference on both ABIs: the slot is a pointer, the callee
    /// copies, and the caller owns and frees it (#14).</summary>
    [Fn("void(IntPtr msg, IntPtr text)")]
    public const string SendChatTalk        = "_ZN11CNWCMessage27SendPlayerToServerChat_TalkE10CExoString"; // outbound verify

    /// <summary>CGuiInGame::NotifyServerAboutGuiEvent(int type, int integer, uint oid, Vector const&amp;) --
    /// the sole caller of SendPlayerToServerGuiEvent_Notify (one xref in the whole binary). It reads
    /// g_pAppManager itself and returns without sending when GetPlayerCreature() or GetNWCMessage() is
    /// null, so it carries the not-in-a-module guard; `this` (rdi) is clobbered before use and may be
    /// null. Chat-bar events: type 1 = focus, 2 = unfocus, integer = the prompt's chat mode,
    /// oid = 0x7f000000, vector = {0,0,0}. Wire: existing major 0x35 minor 0x01, itself gated on
    /// ServerSatisfiesBuild(0x2001, 0x23) -- nothing new is invented.</summary>
    [Fn("void(IntPtr self, int eventType, int mode, uint objectId, IntPtr pos)")]
    public const string NotifyServerGuiEvent = "_ZN10CGuiInGame25NotifyServerAboutGuiEventEiijRK6Vector";
    // outbound send parity (#22): the other five channel senders. All major 0x09; minor per §4.3.
    // Talk/Shout/Whisper/Party/DM take (this, CExoString); TellName takes (this, const CExoString&, const CExoString&).
    /// <inheritdoc cref="SendChatTalk"/>
    [Fn("void(IntPtr msg, IntPtr text)")]
    public const string SendChatShout   = "_ZN11CNWCMessage28SendPlayerToServerChat_ShoutE10CExoString";        // minor 0x02
    /// <inheritdoc cref="SendChatTalk"/>
    [Fn("void(IntPtr msg, IntPtr text)")]
    public const string SendChatWhisper = "_ZN11CNWCMessage30SendPlayerToServerChat_WhisperE10CExoString";      // minor 0x03
    /// <inheritdoc cref="SendChatTalk"/>
    [Fn("void(IntPtr msg, IntPtr name, IntPtr text)")]
    public const string SendChatTell    = "_ZN11CNWCMessage31SendPlayerToServerChat_TellNameERK10CExoStringS2_"; // minor 0x04, (name, text); server resolves by FIRST name only
    /// <inheritdoc cref="SendChatTalk"/>
    [Fn("void(IntPtr msg, uint playerId, IntPtr text)")]
    public const string SendChatTellId  = "_ZN11CNWCMessage27SendPlayerToServerChat_TellEj10CExoString";         // minor 0x04, (playerId, text) -- vanilla /t path
    /// <inheritdoc cref="SendChatTalk"/>
    [Fn("void(IntPtr msg, IntPtr text)")]
    public const string SendChatParty   = "_ZN11CNWCMessage28SendPlayerToServerChat_PartyE10CExoString";        // minor 0x06
    /// <inheritdoc cref="SendChatTalk"/>
    [Fn("void(IntPtr msg, IntPtr text)")]
    public const string SendChatDM      = "_ZN11CNWCMessage36SendPlayerToServerChat_DungeonMasterE10CExoString"; // minor 0x0e

    /// <summary>CClientExoApp::GetPlayerByPlayerName(CExoString) -> CNWCPlayer* or null.
    /// Name→player roster lookup for Tell-pinned tabs (#45, ADR 0002).</summary>
    [Fn("IntPtr(CClientExoApp this, IntPtr name)")]
    public const string GetPlayerByPlayerName = "_ZN13CClientExoApp21GetPlayerByPlayerNameE10CExoString";

    /// <summary>CClientExoApp::GetPlayerByCreatureName(CExoString) -> CNWCPlayer* or null.
    /// What vanilla /t resolves with -- pinned tab names are character names, so try this first.</summary>
    [Fn("IntPtr(CClientExoApp this, IntPtr name)")]
    public const string GetPlayerByCreatureName = "_ZN13CClientExoApp23GetPlayerByCreatureNameE10CExoString";

    // cursor override: client-internal, no Windows anchor yet -> skip + log there.
    [Fn("void(CClientExoAppInternal this, int param1)")]
    public const string UpdateMouseCursorAppearance = "_ZN21CClientExoAppInternal27UpdateMouseCursorAppearanceEi";
    [Fn("void(CCachedMouseCursorManager this, int cursorId, int hotspot, int p3, int p4, int p5)")]
    public const string SetMouseCursor = "_ZN25CCachedMouseCursorManager14SetMouseCursorEiiiii";

    // quickbar positioning (client GUI, Windows = RVA rows, #439)
    [Fn("void(IntPtr panel)")]
    public const string QuickbarReposition = "_ZN14CPanelQuickBar10RepositionEv";
    [Fn("void(CGuiPanel this, int x, int y)")]
    public const string SetPositionScreen  = "_ZN9CGuiPanel17SetPositionScreenEii";
    // compass hide/show (CGuiInGame methods, take `this`)
    [Fn("void(CGuiInGame this)")]
    public const string HideCompass = "_ZN10CGuiInGame11HideCompassEv";
    [Fn("void(CGuiInGame this)")]
    public const string ShowCompass = "_ZN10CGuiInGame11ShowCompassEv";

    // misc engine helpers
    [Fn("IntPtr(CClientExoApp this)")]
    public const string GetNWCMessage = "_ZN13CClientExoApp13GetNWCMessageEv";
    public const string SetText       = "_ZN20CAuroraStringWrapper7SetTextEj";
    [Fn("void(IntPtr exo, byte* s, uint len)")]
    public const string ExoStringInit = "_ZN10CExoString17InitFromCharArrayEPKcj";
    [Fn("void(IntPtr self)")]
    public const string ExoStringDtor = "_ZN10CExoStringD1Ev";
    /// <summary><c>Encoding::FromUTF8(const char*, Encoding::Locale)</c> @0x5fd710 -- the engine's UTF-8 -> codepage
    /// transcode, what SDL_TEXTINPUT runs before any vanilla edit field sees a byte (WinMessageHandler case 0x303;
    /// CX-50). Locale 0 = the configured default (settings.tml <c>game.language.codepage</c>, else the game
    /// language, else cp1252); a module <c>encoding.2da</c> overrides it. Unmappable -> <c>'?'</c>. Returns a
    /// <c>std::string</c> BY VALUE from a FREE function, so the sret buffer is the first arg on both ABIs:
    /// <c>(std::string* out, const char*, int locale)</c>. The string layout differs per CRT: <see cref="Native.StdString"/>.</summary>
    [Fn("byte*(byte* ret, byte* s, int locale)")]
    public const string EncodingFromUtf8 = "_ZN8Encoding8FromUTF8B5cxx11EPKcNS_6LocaleE";
    /// <summary><c>operator delete(void*)</c> -- frees a heap <c>std::string</c> buffer the engine handed us. Linux:
    /// libstdc++'s, through the global scope; Windows: the game's own (static CRT: the <c>_free_base</c> thunk).</summary>
    [Fn("void(IntPtr block)")]
    public const string OperatorDelete = "_ZdlPv";
    // consent modal (CPanelOkCancel, as used by the quit dialog). All name-exported on both
    // platforms except operator new (static CRT on Windows -> RVA; see the _win table).
    [Fn("IntPtr(nuint size)")]
    public const string OperatorNew     = "_Znwm";                                     // Linux: libstdc++ operator new(size_t); Windows: the game's own by RVA
    [Fn("void(IntPtr panel)")]
    public const string OkCancelCtor    = "_ZN14CPanelOkCancelC1Ev";
    [Fn("void(IntPtr panel, IntPtr msg, uint ok, uint cancel)")]
    public const string OkCancelActivate= "_ZN14CPanelOkCancel8ActivateE10CExoStringjj"; // (CExoString msg, uint ok, uint cancel)
    [Fn("byte*(int key)")]
    public const string SdlGetKeyName   = "SDL_GetKeyName";
    [Fn("int(byte* name)")]
    public const string SdlGetKeyFromName = "SDL_GetKeyFromName";                        // action-camera toggle key by name
    /// <summary><c>CClientExoAppInternal::WalkPlayerToPoint(float, float, float, int, uint, int)</c> @0x663950: the one
    /// seam every left-click walk goes through (the action camera refuses it). Windows: the only function referencing
    /// "gui_nowalk" on both builds (StringRefs).</summary>
    [Fn("int(CClientExoAppInternal this, float x, float y, float z, int flags, uint obj, int extra)")]
    public const string WalkPlayerToPoint = "_ZN21CClientExoAppInternal17WalkPlayerToPointEfffiji";
    /// <summary><c>CClientExoAppInternal::FeedbackHitCheck{Hostile,NonHostile}(CNWCObject**, Vector*, int*, int)</c>
    /// @0x660a10 / @0x6618f0: the world pick under the cursor (hover, cursor shape, tooltip, both clicks). The
    /// dispatcher between them is inlined on Windows, so both are hooked; paired by callee lists (the hostile one
    /// calls CNWTileSet::GetHeightTransition, the other does not).</summary>
    [Fn("int(CClientExoAppInternal this, IntPtr* obj, IntPtr point, int* ground, int mask)")]
    public const string FeedbackHitCheckHostile = "_ZN21CClientExoAppInternal23FeedbackHitCheckHostileEPP10CNWCObjectP6VectorPii";
    /// <inheritdoc cref="FeedbackHitCheckHostile"/>
    [Fn("int(CClientExoAppInternal this, IntPtr* obj, IntPtr point, int* ground, int mask)")]
    public const string FeedbackHitCheckNonHostile = "_ZN21CClientExoAppInternal26FeedbackHitCheckNonHostileEPP10CNWCObjectP6VectorPii";
    [Fn("int()")]
    public const string SdlGetModState  = "SDL_GetModState";                            // live Ctrl/Shift/Alt state at capture
    [Fn("uint(int* x, int* y)")]
    public const string SdlGetMouseState = "SDL_GetMouseState";
    /// <summary><c>SDL_SetWindowSize(SDL_Window*, int, int)</c>: the one trigger CX has for the engine's own
    /// resize path. SDL posts <c>SDL_WINDOWEVENT_SIZE_CHANGED</c> for a size it set itself, and
    /// <c>WinMessageHandler</c> @0x62f960 (event type <c>0x200</c>, code 5) answers it with
    /// <c>CGuiMan::SetGuiSize</c> @0x77efc0 -- every panel re-laid out, which is the path
    /// <c>alloy.resizeat</c> exists to pull under a harness that cannot resize a window.</summary>
    [Fn("void(IntPtr window, int w, int h)")]
    public const string SdlSetWindowSize = "SDL_SetWindowSize";
    /// <inheritdoc cref="SdlSetWindowSize"/>
    [Fn("void(IntPtr window, int* w, int* h)")]
    public const string SdlGetWindowSize = "SDL_GetWindowSize";
    /// <summary><c>CActionTarget::ActivateTargetMode()</c> @0x9652b0 -- the one funnel every "pick a target" mode
    /// reaches (quickbar spell/feat/item, radial, DM tool, script <c>SetupScriptTargetMode</c>); the subclasses only
    /// build the mode and call this last. Takes <c>this</c> and nothing else, returns void, on both builds.</summary>
    [Fn("void(CActionTarget this)")]
    public const string ActivateTargetMode = "_ZN13CActionTarget18ActivateTargetModeEv";
    /// <summary><c>CActionTarget::HandleLMouseDown()</c> @0x965840 -- resolve a live targeting mode at the engine's
    /// own mouse position (<c>CGuiMan::GetMousePos</c> + <c>FeedbackHitCheck</c> + the allowed-target scan + the
    /// per-mode validation callback). Called, not hooked: the auto-target fires the same code path a click does.
    /// Safe to call re-entrantly from the <see cref="ActivateTargetMode"/> post-hook -- it touches no member after
    /// the fire callback (which may <c>delete this</c>), and all 45 callers of ActivateTargetMode call it last.</summary>
    [Fn("void(CActionTarget this)")]
    public const string ActionTargetLMouseDown = "_ZN13CActionTarget16HandleLMouseDownEv";
    /// <summary>Tab-target (CX-636). <c>CClientExoAppInternal::PerformDefaultActionOnObject(CNWCObject*)</c>
    /// @0x660830: the one world-click seam -- <c>PerformLButtonDownAction</c> calls it when the pick returned an
    /// object (verdict 2) and walks to the point when it answers 0, so a non-zero answer is "handled, no walk".
    /// Dispatches on the type byte to the creature / item / placeable / door defaults on both builds.</summary>
    [Fn("int(CClientExoAppInternal this, IntPtr obj)")]
    public const string PerformDefaultActionOnObject = "_ZN21CClientExoAppInternal28PerformDefaultActionOnObjectEP10CNWCObject";
    /// <summary>SDL: the live scancode array (<c>const Uint8*</c>, <c>SDL_NUM_SCANCODES</c> entries). The
    /// preview cast snapshots which keys were down when a target mode opened, to fire on their release.</summary>
    [Fn("byte*(int* numkeys)")]
    public const string SdlGetKeyboardState = "SDL_GetKeyboardState";
    /// <summary>The Tab-highlight colour globals <c>HiliteAreaObjects</c> @0x7119f0 picks from (Vector, 16-byte
    /// stride, plain C names). Linux 0x19fac00/0x19fabf0/0x19fabe0/0x19faba0/0x19fabc0; the Windows body
    /// (rva 0x7db2f0) reads them in a different order, so each has its own RVA.</summary>
    public const string HiliteEnemy = "g_vCreatureEnemyHighlight";
    public const string HiliteFriend = "g_vCreatureFriendHighlight";
    public const string HiliteNeutral = "g_vCreatureNeutralHighlight";
    public const string HilitePlaceable = "g_vPlaceableHighlight";
    public const string HiliteDoor = "g_vDoorHighlight";
    /// <summary>CX-99: <c>CExoMoviePlayerInternal::ShowFrame(int frameW, int frameH, int texW, int texH)</c>
    /// @0x1165e30 -- the movie's whole DRAW, and nothing else. Its one caller is <c>UpdateMovie</c> @0x1168210,
    /// which has just decoded a WebM/VP8 frame, converted it from I420 and uploaded it into the player's own
    /// GL texture (<see cref="EngineLayout.MovieTexture"/>) on stage 0; <c>RenderFrame</c> @0x11694b0 owns the
    /// pacing (<c>SDL_Delay</c> off <c>GetFrameTimeOffset</c>) and the present, both OUTSIDE this call.
    /// <para>The body is a colour clear plus one letterboxed textured quad in an ortho matched to the movie's
    /// aspect (<c>Clear(1)</c>, four identity matrices, <c>SetOrthoTransform</c>, a cached 4-vertex VDO,
    /// <c>RenderVertexData</c>). Swallowing it is therefore the narrowest possible "no engine draw" for a
    /// movie: the decode, the upload, the A/V clock and the swap all stay the engine's.</para></summary>
    [Fn("void(CExoMoviePlayerInternal this, int frameW, int frameH, int texW, int texH)")]
    public const string MovieShowFrame = "_ZN23CExoMoviePlayerInternal9ShowFrameEiiii";
    // The SDL glue of the client's nuklear is C++-linkage on this build (mangled), so nk_sdl_render
    // must use its mangled name or the hook never installs. Nothing nuklear is anchored on Windows.
    /// <summary>Nuklear's GL flush, called from <c>CGuiMan::UpdateAndRender</c> @0x5809fa straight after
    /// <c>Nui__Render()</c> (which only fills the command queue and touches no GL). Everything drawn before
    /// it lands UNDER every NUI window and over the vanilla GUI panels -- which is where the framework's own
    /// windows draw (<c>UiHostService</c>), not just where nuklear windows get built.</summary>
    [Fn("void(float aa)")]
    public const string NkSdlRender        = "_Z13nk_sdl_renderf";
    /// <summary><c>int nk_window_is_any_hovered(nk_context*)</c> -- pointer precedence for the layering
    /// above. NUI draws on top of our windows, so it must get the click too.</summary>
    [Fn("int(IntPtr ctx)")]
    public const string NkWindowIsAnyHovered = "nk_window_is_any_hovered";
    /// <summary><c>void nk_sdl_copy_event(SDL_Event*)</c> -- nuklear's own event feed, normally reached via
    /// <c>CExoInput::AddEvent</c>. Swallowing an event at <c>WinMessageHandler</c> starves it, so a swallowed
    /// pointer motion is handed here directly to keep <see cref="NkWindowIsAnyHovered"/> answering live.</summary>
    [Fn("void(IntPtr ev)")]
    public const string NkSdlCopyEvent      = "_Z17nk_sdl_copy_eventP9SDL_Event";
    /// <summary><c>Nui::Layout::Widget::Render(nk_context*)</c> @0x84a610 (Windows rva 0x9528e0, vftable slot 4 of
    /// <c>Widget</c> and <c>StatefulWidget</c>) -- every NUI leaf widget's per-frame draw, dispatched from the parent's
    /// child loop in <c>Layoutable::Render</c> right after <c>nk_layout_space_push</c> put this child's rect in
    /// <c>panel-&gt;row.item</c>. Containers, <c>movieplayer</c> and <c>SelectList</c> have their own Render and never
    /// arrive here. CX-154 §9.</summary>
    [Fn("void(IntPtr self, IntPtr ctx)")]
    public const string NuiWidgetRender = "_ZN3Nui6Layout6Widget6RenderEP10nk_context";
    /// <summary><c>Nui::Layout::StatefulWidget</c>'s vtable. Not a hook -- an identity: <c>textedit</c> is built on it
    /// and owns keyboard focus, so it is never culled. The object's vptr is the symbol + <see cref="EngineLayout.VtblSymbolHeader"/>.</summary>
    public const string NuiStatefulWidgetVtbl = "_ZTVN3Nui6Layout14StatefulWidgetE";
    // in-game game clock (chat game-time timestamps). The SERVER-synced module time lives on CNWCModule
    // (hour at +0x16c); CClientExoApp::GetModule(app) fetches it off the same *g_pAppManager chain the
    // in-game gate walks. NOT CWorldTimer -- that's a free-running local clock never anchored to server time.
    // Linux-only (no _win entry -> skip on Windows).
    [Fn("CNWCModule(CClientExoApp this)")]
    public const string GetModule         = "_ZN13CClientExoApp9GetModuleEv";
    /// <summary>CClientExoApp::GetPlayerCreature() @0x637780 -- the controlled creature (client object). Call
    /// with a live CClientExoApp* (ClientApi.LiveExoApp). Used to anchor the shadow map on the PLAYER, not the
    /// camera, so camera yaw/pitch and line-of-sight don't move the shadows. Re-fetch every frame (the object
    /// is freed on area transitions -- never cache). Its world position is a Vector at <c>+0xa4</c> (x,y,z;
    /// z at +0xac per sow-codebase CNWSObject notes).</summary>
    public const string GetPlayerCreature = "_ZN13CClientExoApp17GetPlayerCreatureEv";

    // ---- overhead nameplate / chat bubble (CX-179). Client-side classes: RVA on Windows (#420). ----
    /// <summary>CGuiInGamePopUpBubble::SetChatBubbleMode(int) @0x6a0be0 -- STATIC (mode in edi, no `this`).
    /// Mode 0 sets every bubble's <c>+0x150</c>, and every path that could activate the panel is guarded on
    /// it: the engine stops drawing both the plate and the bubble while still building the state and
    /// refreshing the screen anchor we read. This is a supported vanilla setting, not a patch.</summary>
    [Fn("void(int mode)")]
    public const string SetChatBubbleMode = "_ZN21CGuiInGamePopUpBubble17SetChatBubbleModeEi";
    /// <summary>CNWCCreature::ShowFeedbackBubbleText() @0x71a800 -- builds the mouse-over/Tab plate string and
    /// sets the creature's own "plate is up" flag. ABI: <c>(this) -&gt; void</c>.</summary>
    [Fn("void(CNWCCreature this)")]
    public const string CreatureShowFeedbackBubbleText = "_ZN12CNWCCreature22ShowFeedbackBubbleTextEv";
    /// <summary>CNWCPlaceable::ShowFeedbackBubbleText() @0x7ea8a0 -- the placeable twin.</summary>
    [Fn("void(CNWCPlaceable this)")]
    public const string PlaceableShowFeedbackBubbleText = "_ZN13CNWCPlaceable22ShowFeedbackBubbleTextEv";
    /// <summary>CNWCItem::ShowFeedbackBubbleText() @0x7cea90 -- the item twin. Hook the PRIMARY only: the
    /// <c>_ZThn72_</c> thunk @0x7ced80 is <c>sub $0x48,%rdi; jmp</c> straight into it, so hooking both
    /// would double-fire.</summary>
    [Fn("void(CNWCItem this)")]
    public const string ItemShowFeedbackBubbleText = "_ZN8CNWCItem22ShowFeedbackBubbleTextEv";
    /// <summary>CNWCObject::SetPopUpBubbleText(CExoString, int, float, float) @0x7e8050 -- the ONE sink both
    /// the plate and an overhead chat line pass through, and the only place the speaking/plated object is a
    /// clean <c>CNWCObject*</c>. ABI (verified by disassembly): <c>(this /*rdi*/, CExoString* /*rsi, the
    /// by-value string lowered to a hidden pointer*/, int forceShow /*edx*/, float perChar /*xmm0*/,
    /// float fixedSec /*xmm1*/)</c> -- Ghidra drops the two floats.</summary>
    [Fn("void(CNWCObject this, IntPtr text, int forceShow, float perChar, float fixedSec)")]
    public const string SetPopUpBubbleText = "_ZN10CNWCObject18SetPopUpBubbleTextE10CExoStringiff";
    /// <summary>CGuiPanel::UnToggleOff() @0x5876a0 -- clears the hide bit (<c>+0xb8 &amp;= ~4</c>) and re-attaches the
    /// model only when that leaves the panel at exactly state 1 (was 5: active AND hidden); on an inactive panel it is
    /// the bit clear and nothing else. #591: the vanilla bubble idles with that bit set (<c>Show</c>/<c>Update</c> →
    /// <c>ToggleOff</c>) and <c>UpdatePopUpBubble</c> early-outs on it, so after a vanilla interlude the mode-0
    /// anchor stays frozen until the bit is cleared -- <c>SetChatBubbleMode(0)</c>'s walk only Deactivates
    /// (<c>&amp;= ~1</c>). Called per bubble from the two cold hooks that read its anchor.</summary>
    [Fn("void(CGuiPanel this)")]
    public const string GuiPanelUnToggleOff = "_ZN9CGuiPanel11UnToggleOffEv";
    /// <summary>CNWCMessage::HandleServerToPlayerChat(unsigned char) @0x75b280 -- the channel byte arrives in
    /// esi and the SetPopUpBubbleText for that line runs on the same stack, so it latches the channel for
    /// per-channel bubble styling. ABI: <c>(this /*rdi*/, uchar channel /*esi*/)</c>.</summary>
    [Fn("void(IntPtr msg, byte channel)")]
    public const string HandleServerToPlayerChat = "_ZN11CNWCMessage24HandleServerToPlayerChatEh";
    /// <summary><c>CNWCMessage::HandleServerToPlayerPlaceableUpdate_Delete()</c> @0x75f960 -- the server took a
    /// placeable out of this player's view: fades it out, and expires it after 45 s if it is still in the area
    /// (its BOOL 0) or at once if not. One caller on each build (<c>HandleServerToPlayerUpdate_Delete</c>; Windows
    /// <c>0x7b35b0</c>). Returns the parse verdict.</summary>
    [Fn("int(IntPtr msg)")]
    public const string PlaceableUpdateDelete = "_ZN11CNWCMessage42HandleServerToPlayerPlaceableUpdate_DeleteEv";
    // ---- combat text: damage numbers + floating combat text (CX-174). RVA on Windows (#420). ----
    /// <summary>CNWCObject::ShowFloatyText(CExoString, Vector, CExoString, int) @0x7e7010 -- the ONE funnel
    /// every damage number and combat-text banner passes through (CC messages, the creature floaty-text
    /// update, whirlwind, and every <c>AddFeedbackMessage</c> site). Skipping it means the engine never
    /// allocates a <c>CGuiInGameFloatyText</c> at all, so its one-at-a-time drip (<c>obj+0x98</c>) retires
    /// with it. ABI (verified by disassembly @0x8e7010): <c>(this /*rdi*/, CExoString* text /*rsi, the
    /// by-value string lowered to a hidden pointer*/, Vector colour /*xmm0 = (r,g), xmm1 = b*/,
    /// CExoString* font /*rdx*/, int background /*ecx*/)</c>. The font resref is the class discriminator:
    /// <c>fnt_floaty_smal</c> = a damage number, "" = a banner.</summary>
    [Fn("void(CNWCObject this, IntPtr text, VectorArg colour, IntPtr font, int background)")]
    public const string ShowFloatyText = "_ZN10CNWCObject14ShowFloatyTextE10CExoString6VectorS0_i";
    /// <summary>CTlkTable::GetSimpleString(unsigned int) @0x541fe0 -- strref to text. Returns a CExoString by
    /// value, i.e. <b>sret</b>: <c>(CExoString* out /*rdi*/, CTlkTable* this /*rsi*/, uint strref /*edx*/)</c>,
    /// and the buffer it writes is CALLER-OWNED (zero the struct first, free it after). Used once, cold, to
    /// resolve the two critical-hit strrefs the client formats its crit banner from.</summary>
    public const string TlkGetSimpleString = "_ZN9CTlkTable15GetSimpleStringEj";

    /// <summary>RenderInterface::GetViewPort(float*) @0x45d0b0 -- tail-jumps to GLRender::GetViewPort, which
    /// widens the four ints of <c>GLRender::m_viewport</c> into the caller's float[4] {x, y, w, h}. The same
    /// call <c>Camera::MapToScreen</c> falls back to when the camera carries no viewport of its own.</summary>
    [Fn("void(float* out4)")]
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
    [Fn("IntPtr(CGuiMan this, int x, int y, int fresh, int mask)")]
    public const string GuiObjectHitCheck = "_ZN7CGuiMan17GuiObjectHitCheckEiiii";
    /// <summary><c>CGuiMan::HandleGuiTimer(int dt)</c>: counts the tooltip delay down and, at zero, hit-tests the mouse
    /// and calls the hit object's <c>HandleTriggerTooltip</c>. alloy.hitcheck's tooltip probe chains it.</summary>
    [Fn("void(IntPtr self, int dt)")]
    public const string GuiManHandleGuiTimer = "_ZN7CGuiMan14HandleGuiTimerEi";
    /// <summary><c>CGuiMan::ShowTooltipText(CExoString const&amp;, int instant)</c>: every tooltip's one funnel -- sets the
    /// text bubble's text, positions it by the mouse and activates it. alloy.hitcheck's tooltip probe chains it.</summary>
    [Fn("void(IntPtr self, IntPtr text, int instant)")]
    public const string GuiManShowTooltipText = "_ZN7CGuiMan15ShowTooltipTextERK10CExoStringi";
    /// <summary><c>CGuiMan::HandleKeyPress(int)</c> @0x57e060: hands an engine key code to the keyboard-capture
    /// object's own <c>HandleKeyPress</c> virtual. <see cref="CX.Alloy.Db.EditSelect"/> deletes a selection through it,
    /// one backspace at a time, so every edit subclass's own key handling runs.</summary>
    [Fn("int(IntPtr self, int key)")]
    public const string GuiManHandleKeyPress = "_ZN7CGuiMan14HandleKeyPressEi";
    /// <summary><c>CGuiMan::HandleMouseMove(int, int)</c> @0x57e140: stores the mouse, hit-tests it into the hit point
    /// at +0x108 and forwards to the mouse-capture object. Text selection chains it for the drag.</summary>
    [Fn("void(IntPtr self, int x, int y)")]
    public const string GuiManHandleMouseMove = "_ZN7CGuiMan15HandleMouseMoveEii";
    /// <summary><c>CGuiEdit::HandleLMouseDown()</c> @0x593270: a click in an edit box places the caret at the hit point
    /// and takes the keyboard. Text selection chains it for the click, shift-click and double-click.</summary>
    [Fn("void(IntPtr self)")]
    public const string GuiEditHandleLMouseDown = "_ZN8CGuiEdit16HandleLMouseDownEv";
    /// <summary><c>CGuiEdit::SetFocus()</c> @0x592b60: Tab, or a panel, moves the keyboard into an edit box.</summary>
    [Fn("void(IntPtr self)")]
    public const string GuiEditSetFocus = "_ZN8CGuiEdit8SetFocusEv";
    /// <summary>CTlkTable* global; the <c>this</c> for <see cref="TlkGetSimpleString"/>.</summary>
    public const string TlkTable   = "g_pTlkTable";
    /// <summary>The client's nuklear context, a <c>nk_context*</c> (Linux @0x1a039e0 -> the context itself at
    /// @0x1a12b00; Windows rva 0x14299a0 -> 0x1429ab0). The NUI edit-focus probe reads it; CX-101's flush
    /// converts and clears through it.</summary>
    public const string Nuklear    = "g_pNuklear";
    // ---- CX-101: nuklear's flush, drawn by Alloy. All three take pointers and ints only, so ONE declaration
    //      fits both ABIs -- none of native-gui-skinning.md §5.3's by-value nk_rect hazard is on this path.
    //      CX-97 §4 records where every address was read.
    /// <summary><c>nk_sdl_refresh_config()</c> @0xe066a0 (Windows rva 0xa70d90) -- the skin/font reload edge.
    /// Early-outs on a clear dirty byte at <c>g_pNuklearConfig</c>, so calling it every frame is free.</summary>
    [Fn("void()")]
    public const string NkSdlRefreshConfig = "_Z21nk_sdl_refresh_configv";
    /// <summary><c>nk_flags nk_convert(ctx, cmds, vertices, elements, config)</c> @0xded460 (Windows rva
    /// 0xa7c340) -- nuklear's command queue into a vertex/index pair and a draw-command list. Returns a byte of
    /// <c>NK_CONVERT_*</c> flags: 1 invalid param, 2 command buffer full, 4 vertex buffer full, 8 element buffer
    /// full (read off the accumulator at the tail of the Linux body). Re-runnable: it resets the draw list first.</summary>
    [Fn("byte(IntPtr ctx, IntPtr cmds, IntPtr vertices, IntPtr elements, IntPtr config)")]
    public const string NkConvert = "nk_convert";
    /// <summary><c>nk_clear(ctx)</c> @0xde60d0 (Windows rva 0xa77df0) -- nuklear's per-frame lifecycle: the
    /// command queue, the draw list and the window seq/cull. Skipping it breaks NUI within a frame or two, so
    /// a flush that draws nothing still calls it.</summary>
    [Fn("void(IntPtr ctx)")]
    public const string NkClear = "nk_clear";
    /// <summary>DATA: the SDL backend's <c>nk_buffer</c> of draw commands (Linux @0x1a12a28, Windows rva
    /// 0x14299d8). A file-static with no name on either binary, so it is anchored on both.</summary>
    public const string NkCmds = "nk_sdl.ogl.cmds";
    /// <summary>DATA: the backend's <c>nk_draw_null_texture</c> (16 bytes: the atlas handle and the white
    /// texel's uv) that every solid fill is drawn with (Linux @0x1a12aa0, Windows rva 0x1429a50). Re-read every
    /// frame -- <see cref="NkSdlRefreshConfig"/> rewrites it whenever the atlas is rebaked.</summary>
    public const string NkTexNull = "nk_sdl.ogl.tex_null";
    /// <summary>CExoBase* global; deref for the instance whose CExoString@0x38 is the resolved user directory.</summary>
    public const string ExoBase    = "g_pExoBase";
    /// <summary>#571: <c>CExoConfig::Get&lt;T&gt;(std::string const&amp;, bool)</c> @0x4efce0 / @0x4ef6a0 / @0x4f9120 --
    /// the stock <c>settings.tml</c> reader. <c>this</c> is CExoBase's first member (<see cref="EngineLayout.ExoBaseConfig"/>);
    /// the key is a <c>std::string</c> by const reference (a pointer on both ABIs, forged per platform by
    /// <see cref="StockConfig"/>); the bool picks the committed table over the working one. A key with no
    /// bind, or a bind of another type, answers the type's zero -- so a caller names the bound type, never
    /// guesses it. Allocates (map lookup, cpptoml substrings): cold reads only. Windows exports the same
    /// three instantiations (<c>_N</c>, <c>_J</c> = the 64-bit long, <c>N</c>).</summary>
    [Fn("byte(CExoConfig this, byte* key, byte committed)")]
    public const string ExoConfigGetBool   = "_ZN10CExoConfig3GetIbEET_RKNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEEEb";
    [Fn("long(CExoConfig this, byte* key, byte committed)")]
    public const string ExoConfigGetLong   = "_ZN10CExoConfig3GetIlEET_RKNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEEEb";
    [Fn("double(CExoConfig this, byte* key, byte committed)")]
    public const string ExoConfigGetDouble = "_ZN10CExoConfig3GetIdEET_RKNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEEEb";

    // ---- owned-render text (chatbox slice #19). Linux-only throwaway rig; no Windows anchors
    //      (Resolve returns Zero + logs on Windows, so the feature degrades gracefully there). ----
    // stb_truetype: the client statically links stb and exports every entry point as extern "C".
    [Fn("int(byte* info, byte* data, int offset)")]
    public const string StbttInitFont              = "stbtt_InitFont";
    [Fn("float(byte* info, float px)")]
    public const string StbttScaleForPixelHeight   = "stbtt_ScaleForPixelHeight";
    [Fn("void(byte* info, int* ascent, int* descent, int* lineGap)")]
    public const string StbttGetFontVMetrics       = "stbtt_GetFontVMetrics";
    [Fn("void(byte* info, int codepoint, int* advance, int* leftSideBearing)")]
    public const string StbttGetCodepointHMetrics  = "stbtt_GetCodepointHMetrics";
    [Fn("void(byte* info, int codepoint, float scaleX, float scaleY, int* x0, int* y0, int* x1, int* y1)")]
    public const string StbttGetCodepointBitmapBox = "stbtt_GetCodepointBitmapBox";
    [Fn("void(byte* info, byte* output, int outW, int outH, int outStride, float scaleX, float scaleY, int codepoint)")]
    public const string StbttMakeCodepointBitmap   = "stbtt_MakeCodepointBitmap";
    [Fn("int(byte* info, int codepoint)")]
    public const string StbttFindGlyphIndex        = "stbtt_FindGlyphIndex";
    // ---- CX-119: the engine's OWN stb_truetype pack API and codepage mapper. CX bakes a font atlas by
    //      calling the same statically linked rasteriser at the same parameters, so glyph pixels match by
    //      construction rather than by matching a library version. All name-exported on BOTH OSes (verified
    //      in nwmain.exe's export table), so one Nm() row each. ----
    [Fn("void(byte* spc, byte* pixels, int width, int height, int strideInBytes, int padding, void* allocContext)")]
    public const string Cx119StbttPackBegin           = "stbtt_PackBegin";
    [Fn("void(byte* spc)")]
    public const string Cx119StbttPackEnd             = "stbtt_PackEnd";
    [Fn("void(byte* spc, int hOversample, int vOversample)")]
    public const string Cx119StbttPackSetOversampling = "stbtt_PackSetOversampling";
    [Fn("int(byte* spc, byte* fontdata, int fontIndex, byte* ranges, int numRanges)")]
    public const string Cx119StbttPackFontRanges      = "stbtt_PackFontRanges";
    /// <summary><c>Encoding::SingleCharToUTF8(char, Encoding::Locale)</c> @0x5fcef0 -- one codepage byte to its
    /// Unicode codepoint, honouring the client's locale and a module's <c>encoding.2da</c> override. Returns
    /// <c>int</c>, which the MSVC export name carries and the Itanium one does not.</summary>
    [Fn("int(byte b, int locale)")]
    public const string Cx119EncodingSingleCharToUtf8 = "_ZN8Encoding16SingleCharToUTF8EcNS_6LocaleE";

    // The client runs an OpenGL 3.3 CORE context (verified: SDL_GL_SetAttribute PROFILE_MASK=CORE),
    // so the owned draw is shader+VAO+VBO. Modern GL (>=1.3) resolves through this exported SDL entry.
    public const string SdlGlGetProcAddress = "SDL_GL_GetProcAddress";
    // RenderInterface blend/depth wrappers (static; args in edi/esi, no `this`). We route only these
    // through the engine so its enable-cache stays in sync; matrices are ours (software stack -> shader
    // uniforms in the engine, useless for a raw draw). Immediates read from PrintStringScreenPos (0x4351f0).
    [Fn("void(int cap)")]
    public const string RiEnable       = "_ZN15RenderInterface6EnableEN6Aurora17AuroraEnableEnumsE";
    [Fn("void(int cap)")]
    public const string RiDisable      = "_ZN15RenderInterface7DisableEN6Aurora17AuroraEnableEnumsE";
    public const string RiSetBlendFunc = "_ZN15RenderInterface12SetBlendFuncEN6Aurora16AuroraBlendModesES1_";
    /// <summary>RenderInterface::IsEnabled(AuroraEnableEnums) @0x45cf30 -- read an Aurora render cap's cached
    /// enable bit. Cap 3 = GL_CULL_FACE (verified: GLRender::Enable(3) -&gt; glEnable(0xb44)). Used to save/
    /// restore cull around the two-sided shadow depth pass.
    /// <para>Cap 5 = LIGHTING, and it is the modern renderer's lit predicate: <c>PartTriMesh::MatStatic</c>
    /// (@0x450560) sends flat white instead of the material ambient/diffuse/emissive when it reads false, and
    /// <c>RenderFlat</c> (@0x450eb0) drops the cap around a <c>mat+0x98</c> material.</para></summary>
    [Fn("byte(int cap)")]
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
    [Fn("void()")]
    public const string RiResetRenderStates = "_ZN15RenderInterface17ResetRenderStatesEv";
    /// <summary>CX-120: <c>RenderInterface::CheckAndUpdateUniformDirtyDataForFrame(float)</c> @0x45de90 -- a
    /// static thunk to <c>GLRender::CheckAndUpdateUniformDirtyDataForFrame</c> @0x4d4e50, which refills
    /// <see cref="GlShaderRuntimeData"/> from the game: the world clock (<c>worldtimerTimeOfDay</c>, which is
    /// every owned stage's <c>CxClock()</c>), the area wind, the area flags, the player and camera positions
    /// and the module date. It reads game state and writes a struct -- no GL, no allocation.
    /// <para><c>Scene::Render</c> @0x5762d0 is its ONLY caller, with <c>DeltaT</c>, right beside
    /// <c>WindManager::updateTimer</c>. DB mode's <c>Scene::Render</c> draws nothing, so without this call the
    /// struct keeps its zero-initialised boot value: a frozen clock and a dead wind for every stage that
    /// animates.</para></summary>
    [Fn("void(float dt)")]
    public const string RiCheckUniformData = "_ZN15RenderInterface38CheckAndUpdateUniformDirtyDataForFrameEf";
    /// <summary>#210: RenderInterface::BindBuffer(target, name) @0x45d190 -- a static thunk to
    /// <c>GLRender::BindBuffer</c> @0x4d04a0, which is a CACHING wrapper: for GL_ARRAY_BUFFER it compares the
    /// requested name against <c>GLRender::m_nArrayBufferBound</c> (@0x4d04d7) and returns without calling
    /// glBindBuffer on a match. <c>ResetRenderStates</c> does not reset that global -- array and uniform
    /// buffer bindings are the two GL_* bindings outside its write set -- so the hand-back has to route one
    /// bind through this wrapper to bring cache and GL back into agreement.</summary>
    [Fn("void(uint target, uint name)")]
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
    /// <summary>CX-95: <c>GLRender::m_bEnabledStates</c> @0x19ad0d0 -- the enable-cap CACHE, one bit per
    /// <c>AuroraEnableEnums</c> value (<c>GLRender::IsEnabled</c> @0x4cae20 is <c>m_bEnabledStates &amp; 1 &lt;&lt; cap</c>).
    /// The GUI capture reads the caps an engine draw would have run under.</summary>
    public const string GlEnabledStates = "_ZN8GLRender16m_bEnabledStatesE";
    /// <summary>CX-95: <c>GLRender::m_globalColor</c> @0x19ad0c0 -- the <c>globalColor</c> uniform's four floats,
    /// written by <c>SetColorv</c> @0x4d1230 / <c>SetColor4f</c> @0x4d1290.</summary>
    public const string GlGlobalColor = "_ZN8GLRender13m_globalColorE";
    /// <summary>CX-95: the <c>AuroraBlendModes</c> pair <c>GLRender::SetBlendFunc</c> @0x4cb0c0 caches and early-outs
    /// against -- indices into <c>blendTranslationTable</c>.</summary>
    public const string GlSrcBlendMode = "_ZN8GLRender20cm_nPrevSrcBlendModeE";
    /// <inheritdoc cref="GlSrcBlendMode"/>
    public const string GlDstBlendMode = "_ZN8GLRender20cm_nPrevDstBlendModeE";
    /// <summary>CX-95: <c>GLRender::bDepthMask</c> -- the depth-write byte <c>SetDepthMask</c> @0x4cb580 caches.</summary>
    public const string GlDepthMask = "_ZN8GLRender10bDepthMaskE";
    /// <summary>CX-95: <c>GLRender::m_viewport</c> -- four ints {x, y, w, h} that <c>SetViewPort</c> @0x4cb3f0 writes
    /// beside every glViewport; <c>Camera::RenderScene</c> sets each view's here before <c>Scene::Render</c>.</summary>
    public const string GlViewport = "_ZN8GLRender10m_viewportE";
    /// <summary>CX-95: <c>GLRender::m_material</c> @0x19ad180 -- the lit shader's MATERIAL cache
    /// (<see cref="EngineLayout.GlMatAmbient"/> for its layout).</summary>
    public const string GlMaterial = "_ZN8GLRender10m_materialE";
    /// <summary>CX-95: <c>RenderInterface::SetColorv(float*)</c> @0x45cfa0 -- static, a thunk to
    /// <c>GLRender::SetColorv</c>. <c>Scene::RenderOnlyGobs</c> opens every GUI scene with white.</summary>
    [Fn("void(float* rgba)")]
    public const string RiSetColorv = "_ZN15RenderInterface9SetColorvEPf";
    /// <summary>CX-95: <c>Scene::RenderOnlyGobs()</c> @0x4682e0 -- the draw of every scene with no tile BSP (every
    /// GUI panel, the menus, the minimap, the cursor overlay): <c>Scene::Render</c> reaches it through
    /// <c>call *0x278</c> @0x4767d9. Alloy replaces it with its own loop over the same gobs, whose draws it
    /// captures at the two funnels (<see cref="CX.Alloy.GuiCapture"/>).</summary>
    [Fn("void(Scene this)")]
    public const string SceneRenderOnlyGobs = "_ZN5Scene14RenderOnlyGobsEv";
    /// <summary>CX-95: the window size the engine lays its GUI out in (ints; <c>GLRender::SetViewPortLight</c> @0x4cb4b0
    /// clamps every view to it) -- the size of the default framebuffer a GUI flush draws into.</summary>
    public const string ScreenWidth = "g_nScreenWidth";
    /// <inheritdoc cref="ScreenWidth"/>
    public const string ScreenHeight = "g_nScreenHeight";
    /// <summary>CX-100: <c>CGuiStatusBar::SetStatus(float percent)</c> @0x594be0 -- stores the fill (clamped to 0..100)
    /// and plays the bar's state animation at percent/30. The load screen's bar is one of these; its load and the
    /// lightmap bake hold share it through this call.</summary>
    [Fn("void(CGuiStatusBar this, float percent)")]
    public const string StatusBarSetStatus = "_ZN13CGuiStatusBar9SetStatusEf";
    /// <summary>CX-95: <c>GLRender::DrawReset()</c> @0x4cfa00 -- the tail of both draw funnels: <c>Disable(0x12)</c>,
    /// <c>Disable(0x15)</c>, <c>m_nRenderHint = 0</c>, <c>m_pCurrentCustomProgram = 0</c>. A captured draw never reaches the
    /// funnel, so the capture calls it itself, or a material's custom program stays latched for every later draw.</summary>
    [Fn("void()")]
    public const string GlDrawReset = "_ZN8GLRender9DrawResetEv";
    /// <summary>CX-101: SDL's present -- what <c>GLRender::SwapBuffers</c> @0x4d2070 calls between
    /// <c>BindDefaultFramebuffer</c> and <c>ResetRenderStates</c>. Alloy's present calls it directly. C linkage.</summary>
    [Fn("void(IntPtr window)")]
    public const string SdlGlSwapWindow = "SDL_GL_SwapWindow";
    /// <summary>CX-101: <c>RenderInterface::Clear(uint mask)</c> @0x45d2a0 -- every engine clear (the frame's
    /// <c>Clear(3)</c> in <c>Update</c>, each GUI view's <c>Clear(2)</c>, snapshots) funnels through this static thunk
    /// to <c>GLRender::Clear</c>. Bits: 1 colour, 2 depth+stencil, 4 stencil.</summary>
    [Fn("void(uint mask)")]
    public const string RiClear = "_ZN15RenderInterface5ClearEj";
    /// <summary>CX-101: <c>RenderInterface::SetClearColor(float, float, float, float)</c> @0x45d290 -- a thunk to a
    /// bare <c>glClearColor</c>. Observed (not swallowed) so Alloy's own frame clear uses the engine's colour.</summary>
    [Fn("void(float r, float g, float b, float a)")]
    public const string RiSetClearColor = "_ZN15RenderInterface13SetClearColorEffff";
    /// <summary>CX-101: the framebuffer <c>GLRender::BindDefaultFramebuffer</c> binds (0 unless the engine renders
    /// through one of its own), and the draw-framebuffer binding cache <c>GLRender::BindFramebuffer</c> keeps.</summary>
    public const string GlDefaultFramebuffer = "_ZN8GLRender22cm_nDefaultFramebufferE";
    /// <inheritdoc cref="GlDefaultFramebuffer"/>
    public const string GlDrawFramebufferBound = "_ZN8GLRender23m_nDrawFrameBufferBoundE";
    /// <summary>#220: RenderInterface::DeleteTextures(n, names) @0x45d1d0 -- the SINGLE funnel through which
    /// every engine GL texture name dies. Its callers are <c>CAurTextureBasic::~CAurTextureBasic</c>,
    /// <c>::Reset</c>, <c>::glImageBasic</c> (the re-upload path), <c>AurTextureFreeAll</c>,
    /// <c>Camera::ClearFrameBufferResources</c> and the movie/nuklear teardowns. #211 keys its owned copies
    /// on the engine name, and this is the exact moment a name stops meaning what was copied from it --
    /// including on an area transition, which fires no other edge Alloy watches.</summary>
    [Fn("void(uint count, uint* names)")]
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
    [Fn("void(IntPtr tex, byte useSubImage, int cell)")]
    public const string RiCreateTexture = "_ZN15RenderInterface13CreateTextureEP11CAurTexturebi";
    /// <summary><c>RenderInterface::CreateCubeTexture(CAurTexture*, bool, int)</c> @0x45d1f0 -> <c>GLRender</c>'s
    /// @0x4caab0 (Windows RVA 0xBE930, the GLRender body inlined; <c>glImageBasic</c> calls it on both): the
    /// cube twin of <see cref="RiCreateTexture"/>. <c>glImageBasic</c> routes a <c>flagCube</c> texture here
    /// after <c>AurTextureBindTID(name, true)</c> binds the cell to <c>GL_TEXTURE_CUBE_MAP</c>; it cuts the
    /// image as a vertical strip of six faces (remap {1,0,2,3,4,5}, quarter turns {1,3,0,2,2,0}), writes the
    /// cube sampler state, uploads each face into whatever is bound there on unit 0, and generates mips when
    /// the texture asks. Same ABI as <see cref="RiCreateTexture"/>.</summary>
    [Fn("void(IntPtr tex, byte useSubImage, int cell)")]
    public const string RiCreateCubeTexture = "_ZN15RenderInterface17CreateCubeTextureEP11CAurTexturebi";
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
    [Fn("void(IntPtr tex)")]
    public const string AurTextureConstructImage = "_ZN16CAurTextureBasic14ConstructImageEv";
    /// <summary><c>CAurTextureBasic::BuildAndStoreTexture()</c> @0x48b320 -- the inner build: ConstructImage (the
    /// resource read via AurGetImageWrapper and the decode) unless +0xe6, glImageBasic (the upload) unless the
    /// image is null, the alpha mean, FreeImage. Skipped outright when +0xe8 is set. ADR 0023 hooks it to stand a
    /// resident texture in for the whole of that. ABI: <c>(CAurTextureBasic* /*rdi*/)</c>, no return.</summary>
    [Fn("void(IntPtr tex)")]
    public const string AurTextureBuildAndStoreTexture = "_ZN16CAurTextureBasic20BuildAndStoreTextureEv";
    /// <summary>CX-52 step 5: <c>AurTextureBuildAndStoreAll()</c> @0x48e680 (Windows RVA 0xe6910, same body) -- the
    /// drain of <see cref="RefreshTextures"/>: builds every queued entry whose <c>+0xeb</c> is clear, newest first,
    /// sets the count to 0, then re-sorts <c>m_StaticTextureList</c> and rebuilds <c>TextureIndices</c>. Called by
    /// <c>AurTextureMakeCurrent</c> (every <c>UpdateScreen</c> tick and <c>Scene::Render</c>), <c>RefreshAll</c>, <c>AurResetTexture</c>.</summary>
    [Fn("void()")]
    public const string AurTextureBuildAndStoreAll = "_Z26AurTextureBuildAndStoreAllv";
    /// <summary>CX-52 step 5: the engine's texture build queue, a DATA symbol: <c>List&lt;CAurTextureBasic*&gt;</c> --
    /// array pointer at +0, int count at +8 (@0x19abe20 / Windows RVA 0x140b328, count read at +8 by both drains).
    /// <c>AurTextureGet</c> appends on a miss; <c>~CAurTextureBasic</c> @0x486c30 removes itself.</summary>
    public const string RefreshTextures = "RefreshTextures";
    /// <summary>CX-52 step 5: <c>CNWCArea::LoadArea(CAurCamera*, CGuiAreaLoadscreen*)</c> @0x70e010 (Windows RVA
    /// 0x7dba70, anchored by its "CNWCArea::LoadArea() failed" log strings; its one caller tests the int) -- the
    /// straight-line area parse, run inside <c>HandleServerToPlayerArea</c> with the loadscreen up.</summary>
    [Fn("int(IntPtr area, IntPtr camera, IntPtr loadscreen)")]
    public const string AreaLoadArea = "_ZN8CNWCArea8LoadAreaEP10CAurCameraP18CGuiAreaLoadscreen";
    /// <summary>CX-55 step 2: <c>CNWCArea::AddPlaceableObjectStatic(uint id, ushort appearance, Vector pos,
    /// Vector orient)</c> @0x7091c0 (Windows RVA 0x7d95a0, docs/windows-symbols.md) -- one call per record of
    /// <c>LoadArea</c>'s "Static Placeables" block. It reads the appearance row's <c>ModelName</c> out of
    /// placeables.2da and hands it straight to the scene's add-static-object slot, so the model is read and
    /// parsed inside the call: an offer for THIS record buys nothing, an offer for the records still sitting in
    /// the message buys the whole rest of the block.
    /// <para>ABI: <c>(CNWCArea* this, uint, ushort, Vector, Vector) -> int</c>; the two vectors are a SysV xmm
    /// pair and a Win64 pointer, which is <see cref="CX.Engine.VectorArg"/>.</para></summary>
    [Fn("int(IntPtr area, uint id, ushort appearance, VectorArg pos, VectorArg orient)")]
    public const string AreaAddPlaceableStatic = "_ZN8CNWCArea24AddPlaceableObjectStaticEjt6VectorS0_";
    /// <summary>CX-55 step 2: <c>C2DA::GetCExoStringEntry(int row, CExoString const&amp; column, CExoString* out)</c>
    /// @0x543b00 (exported on Windows) -- the table read and nothing else: <c>GetColumnIndex</c>, then the cell,
    /// no file touched and no resource demanded (an out-of-range row yields the table's default and returns 0).
    /// The out CExoString is the caller's and engine-heap: zero it before the call (the engine frees a non-null
    /// buffer first) and free it with <see cref="ExoStringDtor"/>.</summary>
    [Fn("int(IntPtr twoda, int row, IntPtr column, IntPtr result)")]
    public const string C2DAGetCExoStringEntry = "_ZN4C2DA18GetCExoStringEntryEiRK10CExoStringPS0_";
    /// <summary>CX-55 step 2: <c>g_pRules</c> @0x18b9680, a <c>CNWRules*</c>. placeables.2da is two hops off it
    /// (<see cref="EngineLayout.RulesTwoDimArrays"/> then <see cref="EngineLayout.TwoDimPlaceables"/>) -- the
    /// same walk <c>AddPlaceableObjectStatic</c> and <c>RespawnStaticPlaceablesForTile</c> make, identical on
    /// both builds (Windows <c>AddPlaceableObjectStatic</c> rva 0x7d95a0).</summary>
    public const string Rules = "g_pRules";
    /// <summary>CX-52 step 5: <c>Material::InitializeTextures(char const*, char const*, char const*, char const*)</c>
    /// @0x4c73f0 (Windows RVA 0x83c90, exported) -- a model material's first texture requests, four
    /// <c>AurTextureGetReference</c> calls. What it appends to <see cref="RefreshTextures"/> is world content; the 2D
    /// GUI never comes through it. ABI: <c>(Material* this, four char*)</c>, no return.</summary>
    [Fn("void(IntPtr mat, IntPtr t0, IntPtr t1, IntPtr t2, IntPtr t3)")]
    public const string MaterialInitializeTextures = "_ZN8Material18InitializeTexturesEPKcS1_S1_S1_";
    /// <summary><c>CAurTextureBasic::glImageBasic(bool)</c> @0x48bcf0 -- the other <see cref="PltOnGpu"/>
    /// reader, and the one that routes a PLT texture (<c>+0x84 == 4</c>) around the upload funnel. Hooked
    /// for the same reason and on the same terms as <see cref="AurTextureConstructImage"/>.
    /// <para>ABI: <c>(CAurTextureBasic* /*rdi*/, bool useSubImage /*sil*/)</c>, no return.</para></summary>
    [Fn("int(IntPtr tex, byte useSubImage)")]
    public const string AurTextureGlImageBasic = "_ZN16CAurTextureBasic12glImageBasicEb";
    /// <summary>The engine's <c>operator new[]</c> -- what ADR 0023's warm hand-back allocates the cell-name
    /// array with, because <c>CAurTextureBasic::Reset</c> frees it with the engine's <c>delete[]</c>. #461:
    /// Windows resolves it to the exported <c>MaxTree::operator new</c> @0x8a060, which is <c>_malloc_base</c>
    /// on the same statically linked UCRT heap that <c>Reset</c>'s <c>_free_base</c> @0xc10370 returns to.
    /// Unresolved here meant every Windows build was cold, which is the churn behind #461.</summary>
    [Fn("IntPtr(nuint size)")]
    public const string OperatorNewArray = "_Znam";

    // ---- shadow replacement (#65). Linux-only; no _win entries -> skip-with-log on Windows. ----
    // The three self-contained shadow sub-passes Scene::RenderShadows dispatches. No-op'ing these
    // suppresses stencil shadows while RenderShadows keeps managing all pipeline state itself
    // (its post-skip net state is runtime-dependent -- see CX-167).
    /// <summary>Scene::RenderDynamicShadows() @ 0x4670b0 -- per-frame dynamic shadow-volume sub-pass.</summary>
    [Fn("void(Scene this)")]
    public const string RenderDynamicShadows = "_ZN5Scene20RenderDynamicShadowsEv";
    /// <summary>Scene::RenderStaticShadows() @ 0x46a3a0 -- static (world-geometry) shadow-volume sub-pass.</summary>
    [Fn("void(Scene this)")]
    public const string RenderStaticShadows  = "_ZN5Scene19RenderStaticShadowsEv";
    /// <summary>CAurScene::ComputeStaticLighting() @0x465590 -- the engine's own "the static lighting changed"
    /// signal (#664 §A.6): called after a script toggles a placeable light. The lightmap's re-classify edge
    /// (#674). <c>(this) -> void</c>, virtual (Scene vtable 0x68 Linux / 0x60 Windows; hooked by name).</summary>
    [Fn("void(IntPtr scene)")]
    public const string ComputeStaticLighting = "_ZN9CAurScene21ComputeStaticLightingEv";
    /// <summary>Scene::RenderShadowPlanes(int, int) @ 0x463e20 -- planar/projected shadow fallback sub-pass.</summary>
    [Fn("void(Scene this, int pass, int flag)")]
    public const string RenderShadowPlanes   = "_ZN5Scene18RenderShadowPlanesEii";
    /// <summary>Scene::DoGobShadows(PartLight*) @ 0x45fba0 -- per-light gob volume builder, dispatched
    /// directly by RenderShadows (Scene-vtable +0x1c8) alongside the Render* sub-passes.</summary>
    [Fn("void(Scene this, PartLight light)")]
    public const string DoGobShadows         = "_ZN5Scene12DoGobShadowsEP9PartLight";
    /// <summary>Scene::DoDynamicMeshProjections(PartLight*) @ 0x45f2e0 -- per-light dynamic mesh shadow
    /// builder, dispatched directly by RenderShadows (Scene-vtable +0x1e0).</summary>
    [Fn("void(Scene this, PartLight light)")]
    public const string DoDynamicMeshProjections = "_ZN5Scene24DoDynamicMeshProjectionsEP9PartLight";

    // ---- shadow-map caster re-drive (#65 slice 3b). The frame slot + the geometry submission. ----
    /// <summary>Scene::RenderDrawBucket(int bucketId) @ 0x46d060 -- draws one geometry bucket (the whole
    /// iterate+SetModelMatrix+RenderVertexData loop, DrawBucketManager @Scene+0x208). Ids 1..8 (RenderSinglePass);
    /// bucket 1 = opaque casters. Called directly to render casters into our depth FBO. ABI: <c>(Scene* /*rdi*/,
    /// int bucketId /*esi*/)</c>.</summary>
    [Fn("void(Scene this, int bucketId)")]
    public const string RenderDrawBucket = "_ZN5Scene16RenderDrawBucketEi";
    /// <summary>Scene::RenderSinglePass() @0x46e5f0 -- one whole per-view colour pass (buckets 0..8, sky,
    /// lights); runs for the main view AND each reflection subframe, with view/proj already on the matrix
    /// stacks at entry. The M1 tracer (#81) hooks it to run the owned opaque block before buckets 0/1.</summary>
    [Fn("void(IntPtr scene)")]
    public const string RenderSinglePass = "_ZN5Scene16RenderSinglePassEv";
    /// <summary>Scene::FadeDoors() @0x467990 -- vtable slot 0x260, the LAST engine call inside
    /// <c>Scene::RenderSinglePass</c> before <c>RenderDrawBucket(0)</c>, and #132's original frame anchor.
    /// <para>CX-89 RETIRED it: the fade is <see cref="CX.Alloy.DoorFade"/>'s (same field, same rule) and the
    /// anchor is a direct call out of <c>RspShim.Run</c>, so on an Alloy frame this function is neither called
    /// nor hooked. The symbol stays declared -- it is still what the vanilla path runs, and it names the step
    /// the anchor holds.</para>
    /// <para>Why that step and not RenderSinglePass entry, which is what spec #114 asks for: RSP's first act was
    /// <c>Camera::InitializeFrameBufferResources</c> @0x4a6fd0, whose steady-state path (the size compare at
    /// +0x240 falls through to +0xed) is <c>SetRenderSubframeTarget(0)</c> then <c>Clear(7)</c> -- EVERY frame,
    /// not only on resize -- so a resolve written at literal entry was wiped by that clear. #215 replaced RSP
    /// wholesale and none of that runs any more; what keeps the step is the other reason: it is the last point
    /// at which every piece of per-frame bookkeeping the pass reads is current and nothing has drawn.</para></summary>
    [Fn("void(IntPtr scene)")]
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
    [Fn("void(IntPtr vdo, uint item, uint prim, uint size, uint offset)")]
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
    [Fn("void(IntPtr vsdo, uint prim, uint size, uint offset)")]
    public const string GlRenderVertexStreamData = "_ZN8GLRender22RenderVertexStreamDataEP22VertexStreamDataObjectN6Aurora20AuroraPrimitiveTypesEjj";
    /// <summary>#215: Gob::CollectLights(List&lt;PartLight*&gt;&amp;) @0x43fa90 -- half of the
    /// <c>focuslighting</c> write the shim has to replicate. ABI: <c>(Gob* /*rdi*/, List* /*rsi*/)</c>.</summary>
    [Fn("void(IntPtr gob, IntPtr list)")]
    public const string GobCollectLights = "_ZN3Gob13CollectLightsER4ListIP9PartLightE";
    /// <summary>#235: <c>g_bFullDynamicLighting</c> @0x19ab8a4 -- the int that decides which of
    /// <c>LightManager</c>'s two enable walks <c>Scene::RenderSinglePass</c> runs. Non-zero on this client
    /// (FRAGMENT_LIGHTING=1), so <c>EnableLights</c> is the live arm and <c>EnableLightsDynamicOnly</c> is
    /// dead. Read, never written -- the shim replicates RSP's gate rather than assuming its value.</summary>
    public const string FullDynamicLighting = "g_bFullDynamicLighting";
    /// <summary>#233: <c>triggerbucket</c> @0x19abbd0 -- the trigger draw bucket, a
    /// <c>CExoArrayList&lt;Part*&gt;</c> whose COUNT sits at <c>+8</c> (the <c>DAT_01aabbd8</c> that
    /// <c>Scene::DoTriggerBucket</c> and <c>Scene::DoHighlightTriggers</c> both gate on).
    /// <para>CX-104 correction: its writer is <b>the BSP visitor @0x481000</b>, not
    /// <c>Scene::AddPartsToDrawBuckets</c> -- the visitor appends every <c>PartTrigger</c> of a visited node
    /// that <c>PartOutside</c> keeps, and <c>ClearBuckets</c> @0x47f4e0 (also inside <c>ManageSceneBSP</c>)
    /// zeroes the count. The string <c>triggerbucket</c> appears nowhere in the bucket build's decompilation.
    /// So the bucket is filled and cleared entirely inside the walk <c>alloy.skipbuckets=3</c> skips, which is
    /// what made <see cref="CX.Alloy.SceneParts"/>' own trigger registry the population rather than a
    /// preference. Data symbol; read only.</para></summary>
    public const string TriggerBucket = "triggerbucket";
    /// <summary><c>AurPartTriggerCreate(scene, points, n, snap, colour, data)</c> @0x4da840: an area transition's
    /// prism, filed into the scene's BSP -- which a DB-mode scene does not have, so <see cref="CX.Alloy.Db.DbTriggers"/>
    /// chains it to keep its own list.</summary>
    [Fn("IntPtr(IntPtr scene, IntPtr points, int count, int snap, IntPtr colour, IntPtr data)")]
    public const string AurPartTriggerCreate = "_Z20AurPartTriggerCreateP9CAurSceneP6VectoriiPfPv";
    /// <summary><c>AurPartTriggerDelete(scene, trigger)</c> @0x4d9fc0: out of the scene (the object lives on).</summary>
    [Fn("void(IntPtr scene, IntPtr trigger)")]
    public const string AurPartTriggerDelete = "_Z20AurPartTriggerDeleteP9CAurSceneP11PartTrigger";
    /// <summary><c>AurPartTriggerReleaseMemory(trigger)</c> @0x4d9fd0: the virtual delete.</summary>
    [Fn("void(IntPtr trigger)")]
    public const string AurPartTriggerReleaseMemory = "_Z27AurPartTriggerReleaseMemoryP11PartTrigger";
    /// <summary>#330: <c>gobbucket</c> @0x19abbf0 -- the engine's per-frame VISIBLE gob list, a
    /// <c>CExoArrayList&lt;Gob*&gt;</c> (items at +0, count at +8). <c>ManageSceneBSP</c> @0x47fa80 refills it from
    /// the Scene's gobs through <c>Gob::IsInViewVolume</c>, and <c>Scene::AddPartsToDrawBuckets</c> @0x4751d0
    /// walks it into <c>AddPartsTreeToDrawBuckets</c> @0x460880 -- the ONLY writer of an instanced part's
    /// VertexPrimitive matrix. So at the frame anchor it is exactly the set of gobs whose parts can have
    /// moved this frame. Data symbol; read only, and the population CX-89's door fade walks.</summary>
    public const string GobBucket = "gobbucket";
    /// <summary>CX-89: <c>mindooralpha</c> @0x18aa15c (0.25f as shipped) -- the floor a door's fade alpha is
    /// clamped to, and the value <c>Gob::HitCheck</c> @0x4381d0 compares <c>Gob+0x10c</c> against before it will
    /// hit-test a door. <see cref="CX.Alloy.DoorFade"/> reads it per frame rather than caching it, exactly as
    /// <c>Scene::FadeDoors</c> did. Data symbol; read only.</summary>
    public const string MinDoorAlpha = "mindooralpha";
    /// <summary>CX-89: <c>DeltaT</c> @0x18aa028 -- the frame's length in SECONDS, written once per frame in
    /// <c>CClientExoAppInternal::MainLoop</c> @0x658250 as
    /// <c>CWorldTimer::GetSnapshotTimeDifference(this+0x50) / 1e6</c>. The engine's own time source for every
    /// per-frame integrator, including the door fade. Data symbol; read only.</summary>
    public const string DeltaT = "DeltaT";
    /// <summary>CX-82: the four <c>Scene</c> statics <c>Scene::DoHighlightGob</c> @0x470fc0 shapes the glow
    /// with: pulse <c>((sin*0.5 + 0.5) * 0.25*amplitude + 1) * size</c> at <c>frequency</c> Hz, quad alpha
    /// <c>dot(n, eye)^2 * intensity</c>. <c>CNWCArea::LoadArea</c> sets 0.5 / 1 / 2.5 / 0.3 on every area
    /// load and a console command can change them, so they are read per frame. Data symbols; read only.</summary>
    public const string GobHighlightAmplitude = "_ZN5Scene24m_fGobHighlightAmplitudeE";
    /// <inheritdoc cref="GobHighlightAmplitude"/>
    public const string GobHighlightFrequency = "_ZN5Scene24m_fGobHighlightFrequencyE";
    /// <inheritdoc cref="GobHighlightAmplitude"/>
    public const string GobHighlightIntensity = "_ZN5Scene24m_fGobHighlightIntensityE";
    /// <inheritdoc cref="GobHighlightAmplitude"/>
    public const string GobHighlightSize = "_ZN5Scene19m_fGobHighlightSizeE";

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
    [Fn("void(IntPtr obj)")]
    public const string RefreshHilite = "_ZN12CNWCAnimBase13RefreshHiliteEv";
    /// <summary>#330: <c>fadingoutbucket</c> @0x19abba0 and <c>fadinginbucket</c> @0x19abbb0 -- the tiles whose
    /// fade factor is being stepped this frame. <c>HandleFadingOutBucket</c> @0x47f870 /
    /// <c>HandleFadingInBucket</c> @0x47f670 read <c>TileData+0x70</c> and write <c>TileData+0x74</c> for each
    /// member from <c>ManageSceneBSP</c>, which commits +0x74 into the live +0x70 at its top on the next frame
    /// -- and, in the same inlined <c>ResetTileFading</c> loop, sets the <c>+0xc0</c> fade-state enum back to
    /// its unclassified 4 and zeroes <c>Scene+0x1b0</c>. So the whole ramp, both ends of it, lives inside the
    /// walk <c>alloy.skipbuckets=3</c> skips (see <see cref="CX.Alloy.TileFadeRamp"/>). Same list layout as
    /// <see cref="GobBucket"/>. Data symbols; read only.</summary>
    public const string FadingOutBucket = "fadingoutbucket", FadingInBucket = "fadinginbucket";
    // ---- the engine draw funnel Alloy sits on, Linux-only -> DrawFunnelService stands down on Windows ----
    /// <summary>#125: Scene::RenderSkyBoxes() @0x466910 -- the whole per-area skybox draw, called from
    /// RenderSinglePass between buckets 0 and 1. Three gobs (current, cross-fading, skyfade) rendered under
    /// their own projection with the depth mask off. #261: NOT hooked. The shim calls it for its
    /// bookkeeping -- ProcessGob, node animation, the cross-fade alpha writes and the per-gob
    /// <c>SetPosition(Scene+0x190)</c> -- and Alloy enrols the gobs itself. ABI: <c>(Scene* /*rdi*/)</c>.</summary>
    [Fn("void(IntPtr scene)")]
    public const string RenderSkyBoxes = "_ZN5Scene14RenderSkyBoxesEv";
    /// <summary>#261: Part::GetWorld(Vector&amp;, Quaternion&amp;) @0x437930 -- composes a part's world
    /// transform by walking <c>part+0x48</c> to the root and then applying the owning Gob's adjusted
    /// scale/orientation/position. No Part subclass overrides vtable slot 0x20 (checked against every
    /// <c>_ZTV4Part*</c>), so it is safe to call by symbol. OUT PARAMETERS, which is why the sky can call it
    /// at all: the engine's own by-value <c>Vector</c>/<c>Quaternion</c> returns are an ABI this side does
    /// not invent. ABI: <c>(Part* /*rdi*/, Vector* /*rsi*/, Quaternion* /*rdx*/)</c>.</summary>
    [Fn("void(Part this, System.Numerics.Vector3* pos, float* rot)")]
    public const string PartGetWorld = "_ZN4Part8GetWorldER6VectorR10Quaternion";
    /// <summary>#261: Gob::GetAdjustedScale() @0x437cf0 -- <c>LerpFloat(gob+0x1c0).Lerped() *
    /// *(float*)(gob+0x49c)</c>, the uniform scale <c>Gob::Render</c> pushes and <see cref="PartGetWorld"/>
    /// applies to the position but does not hand back. A plain float return, so it is callable.
    /// ABI: <c>(Gob const* /*rdi*/) -&gt; float</c>.</summary>
    [Fn("float(Gob this)")]
    public const string GobGetAdjustedScale = "_ZNK3Gob16GetAdjustedScaleEv";
    /// <summary>CX-87: Gob::ProcessGob() @0x43e920 -- the whole of the sky simulation
    /// <c>Scene::RenderSkyBoxes</c> was kept alive for, and four lines of it: with the dirty byte
    /// <c>gob+0x1a5</c> set it writes <c>gob+0x1a4 = TreeHasGeometry(gob+0x90)</c>, runs
    /// <c>TreeSetGeometryFlags</c> over the same tree, and clears the byte. Not a draw and not animation --
    /// scene bookkeeping the engine keeps (ADR 0016), which is why the owned tick calls it rather than
    /// reimplementing the two tree walks. <c>Gob::Render</c> @0x452260 is its only caller and does it first,
    /// before reading the visibility byte it just wrote, so the owned tick does too.
    /// ABI: <c>(Gob* /*rdi*/)</c>.</summary>
    [Fn("void(Gob this)")]
    public const string GobProcessGob = "_ZN3Gob10ProcessGobEv";
    // ---- CX-104: the in-view-volume byte, gob+0x1b. ManageSceneBSP @0x47fa80 runs it in TWO phases --
    //      step 3 clears it on EVERY gob in Scene+0x130 (_global.c:809816, the vtable +0x2b8 call the
    //      bypass research had down as a "bounds refresh"; it is Gob::DisableInViewVolume), then the BSP
    //      visitor @0x481000 sets it on every gob a visited node holds that survives GobOutside. The two
    //      phases are not interchangeable: Enable PROPAGATES to the gob's attachments, so setting and
    //      clearing in one pass would let a later clear undo a propagated set. CX.Alloy.ScenePrep keeps
    //      the two phases when alloy.skipbuckets=3 stands that walk down.
    /// <summary><c>Gob::DisableInViewVolume()</c> @0x436f30 -- the whole body is <c>gob+0x1b = 0</c>. Vtable
    /// slot +0x2b8, and the call <c>ManageSceneBSP</c> makes over every gob in the scene before it traverses.
    /// Called rather than written directly so an override, if one ever appears, still runs.
    /// ABI: <c>(Gob* /*rdi*/)</c>.</summary>
    [Fn("void(Gob this)")]
    public const string GobDisableInViewVolume = "_ZN3Gob19DisableInViewVolumeEv";
    /// <summary><c>Gob::EnableInViewVolume()</c> @0x436f10 -- <c>gob+0x1b = 1</c> and then
    /// <c>this-&gt;vt[0x2a0]("propagateviewvolume", 0)</c>, i.e. <c>Gob::DoEvent</c>, which is how an attached
    /// gob inherits its host's verdict. That propagation is the reason this is CALLED and not a byte store.
    /// Vtable slot +0x2b0; the BSP visitor @0x481000 is its only engine caller.
    /// ABI: <c>(Gob* /*rdi*/)</c>.</summary>
    [Fn("void(Gob this)")]
    public const string GobEnableInViewVolume = "_ZN3Gob18EnableInViewVolumeEv";
    /// <summary><c>GobOutside(Gob*, List&lt;Plane&gt;&amp;)</c> @0x47a4f0 -- the engine's own per-gob frustum
    /// test, and the one the BSP visitor gates <see cref="GobEnableInViewVolume"/> on. Builds the gob's world
    /// bounding sphere from its own virtuals -- centre = <c>GetAdjustedPosition() + GetAdjustedOrientation() *
    /// (gob+0x17c)</c>, radius = <c>GetAdjustedScale() * (gob+0x178)</c> -- and returns 1 as soon as
    /// <c>SphereAbovePlane</c> is positive against any plane in the list, i.e. OUTSIDE. The centre offset is NOT
    /// scaled, so a scaled gob tests a sphere anchored by its pivot; <c>ScenePrep.Outside</c> is this with that
    /// fixed and no longer calls it. ABI: <c>(Gob* /*rdi*/, List&lt;Plane&gt;* /*rsi*/) -&gt; int /*eax*/</c>.</summary>
    [Fn("int(Gob this, IntPtr planes)")]
    public const string GobOutside = "_Z10GobOutsideP3GobR4ListI5PlaneE";
    /// <summary><c>CExoArrayList&lt;Gob*&gt;::Add(Gob*)</c> @0x445f50 -- the append <c>ManageSceneBSP</c> step 11
    /// builds <see cref="GobBucket"/> with. Called rather than written, because the list GROWS: the count at
    /// <c>+8</c> against the allocation at <c>+0xc</c> is the engine's bookkeeping and a hand-rolled store past
    /// the allocation is a heap overrun. Idempotent in the sense that matters here -- the caller clears the
    /// count first, exactly as <c>ClearBuckets</c> @0x47f4e0 does.
    /// ABI: <c>(CExoArrayList&lt;Gob*&gt;* /*rdi*/, Gob* /*rsi*/)</c>.</summary>
    [Fn("void(IntPtr list, IntPtr gob)")]
    public const string GobListAdd = "_ZN13CExoArrayListIP3GobE3AddES1_";


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

    // ---- CX-86: what the simulation needs once Scene::DoEmitterBucket is no longer called ----
    /// <summary>CX-86: <c>emitterbucket</c> @0x19abc00 -- the BSP walk's in-view emitter list, a
    /// <c>List&lt;PartEmitter*&gt;</c> with the items at +0 and the count at +8, exactly like
    /// <see cref="GobBucket"/>. <c>Scene::DoEmitterBucket</c> @0x476f00 is its only reader and this is the
    /// list <see cref="EmitterSim"/> walks in its place. Data symbol; read only -- <c>ManageSceneBSP</c>
    /// refills it before RSP, so it is already this frame's when the shim runs.</summary>
    public const string EmitterBucket = "emitterbucket";
    /// <summary>CX-86: <c>WindManager::AddPointSourceWind(Vector, float, float, float)</c> @0x491dc0 -- the
    /// gust a detonation pushes into the wind field, and the one side effect of
    /// <c>ExplosionEmitter::Update</c> @0x4b23a0 that leaves the emitter. Dropped, an explosion would stop
    /// disturbing grass and other emitters around it.
    /// <para>ABI: the by-value <c>Vector</c> is an ARGUMENT here, so it is xmm0/xmm1 on Linux and a pointer
    /// on Windows -- again two argument lists, again <see cref="CX.Alloy.AurXform"/>.</para></summary>
    public const string AddPointSourceWind = "_ZN11WindManager18AddPointSourceWindE6Vectorfff";
    /// <summary>CX-86: <c>PartEmitter::AnimateParticles(float)</c> @0x4ac660 and
    /// <c>PartEmitter::RenderNothing()</c> @0x4ad200 -- the engine's own step and the CHUNK emitter's
    /// "draw". Alloy calls both, for chunk emitters only: a chunk particle holds a real
    /// <c>CAurObject</c> built by <c>NewCAurObject</c>, so it is a scene object and stays the engine's (ADR
    /// 0018's CX-86 amendment). Neither issues a draw -- <c>RenderNothing</c> only pushes each particle's
    /// position and orientation onto its gob, and its one GL call sits behind the
    /// <c>g_nRenderEmitterSpheres</c> debug global.</summary>
    [Fn("void(PartEmitter this, float dt)")]
    public const string AnimateParticles = "_ZN11PartEmitter16AnimateParticlesEf";
    /// <inheritdoc cref="AnimateParticles"/>
    [Fn("void(PartEmitter this)")]
    public const string RenderNothing = "_ZN11PartEmitter13RenderNothingEv";

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
    [Fn("void(PartEmitter this)")]
    public const string RenderEmitter = "_ZN11PartEmitter13RenderEmitterEv";
    /// <summary>#131: PartEmitter::~PartEmitter() @0x4af890 (D1 and D2 share the address; the deleting D0
    /// @0x4afa60 tail-calls it, and every emitter subclass's dtor reaches it). #145's lesson applied to
    /// emitters: the owned replay reads a live Particle array off the emitter one frame after the capture,
    /// so the one event that says a captured emitter is GONE has to evict it.
    /// ABI: <c>(PartEmitter* /*rdi*/)</c>.</summary>
    [Fn("void(PartEmitter this)")]
    public const string PartEmitterDtor = "_ZN11PartEmitterD2Ev";
    /// <summary>#131: PartEmitter::GetTileAlpha() @0x4ac300 -- one of the two factors the engine folds into
    /// every particle's alpha. Called rather than reproduced. ABI: <c>(PartEmitter* /*rdi*/) -&gt; float</c>.
    /// </summary>
    [Fn("float(PartEmitter this)")]
    public const string EmitterGetTileAlpha = "_ZN11PartEmitter12GetTileAlphaEv";
    /// <summary>#131: PartEmitter::GetGobAlpha() @0x4ac2d0 -- the other one. Same treatment.
    /// ABI: <c>(PartEmitter* /*rdi*/) -&gt; float</c>.</summary>
    [Fn("float(PartEmitter this)")]
    public const string EmitterGetGobAlpha = "_ZN11PartEmitter11GetGobAlphaEv";
    /// <summary>#131: MdlNodeEmitter::IsSplat() @0x4ab070 -- the model flag that turns an emitter's quads
    /// into ground splats: the billboard basis comes off the particle's own splat normal, size stops
    /// interpolating, and the softness term is suppressed.
    /// ABI: <c>(MdlNodeEmitter* /*rdi*/) -&gt; bool</c>.</summary>
    [Fn("byte(MdlNodeEmitter this)")]
    public const string EmitterIsSplat = "_ZN14MdlNodeEmitter7IsSplatEv";
    /// <summary>#131: MdlNodeEmitter::IsTinted() @0x4aafc0 -- the flag that runs the emitter's start/end
    /// colours through the scene lighting at its own position before interpolation.
    /// ABI: <c>(MdlNodeEmitter* /*rdi*/) -&gt; bool</c>.</summary>
    [Fn("byte(MdlNodeEmitter this)")]
    public const string EmitterIsTinted = "_ZN14MdlNodeEmitter8IsTintedEv";
    /// <summary>#131: LightManager::GetEmitterLighting(Vector, List&lt;PartLight*&gt;*) @0x4c1970 -- the tint
    /// lookup, one call per tinted emitter per frame.
    /// <para>ABI, read off the call site at 0x4af130 rather than off the decompiler (Ghidra renders the
    /// by-value Vector as one 8-byte word and drops the third argument): the position arrives in
    /// <c>xmm0</c> (x, y) + <c>xmm1</c> (z), the light list in <c>rdi</c>, a zero int in <c>esi</c>, and the
    /// result comes back the same way it went in -- <c>xmm0</c> (r, g) + <c>xmm1</c> (b). That is exactly
    /// SysV's classification of a 12-byte three-float struct, so a plain sequential struct of three floats
    /// matches it on both ends.</para></summary>
    public const string GetEmitterLighting = "_ZN12LightManager18GetEmitterLightingE6VectorP4ListIP9PartLightE";
    /// <summary>LightManager::GetLensFlareLights() @0x4c1d80 -- the list (items at +0, count at +8) of every
    /// light node with <c>lensflares 1</c>; <c>Scene::RenderLensFlares</c> is its one consumer. Read by <see cref="Halos"/>.</summary>
    [Fn("IntPtr(LightManager this)")]
    public const string GetLensFlareLights = "_ZN12LightManager18GetLensFlareLightsEv";
    /// <summary>#131: the <c>currentscene</c> global @0x19ab950 -- <c>RenderEmitter</c> reads the light list
    /// for the tint at <c>*currentscene + 0x158</c>. Read live at build time for the same reason
    /// <see cref="Beams"/> reads its own Scene: an area transition frees the list.</summary>
    public const string CurrentScene = "currentscene";
    /// <summary>CX-29: <c>int hitcheck_static_trimesh_parts</c> @0x19abaf8 -- while non-zero,
    /// <c>HitPartOnBSPNode</c> @0x47b790 also tests the static placeables' parts baked into the scene BSP (node
    /// +0x20); they are in no gob list, so <c>Scene::HitCheck</c> misses them otherwise. Zero by default; the
    /// engine sets it to 1 around its own floor casts and restores it (<c>CNWCCreature::DoFootstepVisual</c>
    /// @0x72fb19, <c>CNWCItem::UpdatePosition</c> @0x7d001a). Data symbol; written only around a cast.</summary>
    public const string HitcheckStaticTrimesh = "hitcheck_static_trimesh_parts";
    /// <summary>CX-118: <c>int counthitcheckscenebspnode</c> -- the engine's own counter, bumped once per BSP
    /// node <c>HitPartOnBSPNode</c> @0x47b790 visits. Read-only, and the one honest measure of what a vanilla
    /// cast walked: the live comparison the ticket asks for is this against Alloy's own candidate count, not
    /// two wall clocks. Data symbol.</summary>
    public const string HitCheckBspNodeCount = "counthitcheckscenebspnode";
    /// <summary>CX-118 probe: <c>CNWCCreature::ComputeZValue(CNWCArea*, Vector*, float, Vector*)</c>
    /// @0x61d4f0. It writes the literal 9.0f into the caller's position vector BEFORE its floor raycast and
    /// leaves it there on any early-out -- a null area, a null <c>CNWCArea+0x248</c> (the area's
    /// <c>CAurScene</c>), a <c>GetTile</c> miss, or two casts that both answer 0. That sentinel is the +9
    /// camera, so this is where the question "why was no cast issued" is answered.
    /// Linux-only: the <c>CNWC*</c> game layer is not name-exported by nwmain.exe and this is a diagnostic
    /// behind <c>alloy.hitcheck</c>, not a shipped hook (no <c>_win</c> row -> skipped with a log there).</summary>
    [Fn("void(IntPtr self, IntPtr area, IntPtr pos, float f, IntPtr outv)")]
    public const string ComputeZValue = "_ZN12CNWCCreature13ComputeZValueEP8CNWCAreaP6VectorfS3_";
    /// <summary>CX-118 probe: <c>CNWCArea::GetTile(Vector)</c> @0x703160 -- the game layer's own tile
    /// lookup, and <c>ComputeZValue</c>'s last early-out before it casts. It answers 0 when the area's tile
    /// array (<c>CNWCArea+0x298</c>) is null or the position falls outside the grid, and a 0 there is why a
    /// floor cast is never issued at all. Read-only, called by the diagnostic. Linux-only, as above.</summary>
    [Fn("IntPtr(IntPtr area, VectorArg at)")]
    public const string AreaGetTile = "_ZN8CNWCArea7GetTileE6Vector";
    /// <summary>CX-118: <c>SafePointer&lt;CAurObject&gt;&amp; SafePointer&lt;CAurObject&gt;::operator=(CAurObject*)</c>
    /// @0x477d70 -- the ONLY correct way to put an object into <c>CHitInfo+0x48</c>. It unregisters the record
    /// from the object it held (<c>FUN_0055f5b0</c>, whose <c>array[index]-&gt;index = index</c> branch is
    /// UNGUARDED, so a hand-written -1 back-index reads <c>array[-1]</c>), stores the pointer, and pushes the
    /// record onto the new object's back-reference array (<c>CAurObject+0x08</c> data, <c>+0x10</c> count,
    /// <c>+0x14</c> capacity) taking its index. Hand-writing the pair is a SIGSEGV or a heap corruption at the
    /// next <c>ClearOutput</c> or <c>~CHitInfo</c>, on both OSes.</summary>
    [Fn("IntPtr(IntPtr self, IntPtr obj)")]
    public const string SafePointerObjectAssign = "_ZN11SafePointerI10CAurObjectEaSEPS0_";
    /// <summary>CX-118: <c>int hitcheck_static_bsp_parts</c> -- the other half of the static gate: while
    /// non-zero, <c>HitPartOnBSPNode</c> also tests the tile's walkmesh part (node bin +0x90), which is the
    /// ground a click-to-move pick lands on. Non-zero by default, unlike its trimesh sibling.</summary>
    public const string HitcheckStaticBsp = "hitcheck_static_bsp_parts";
    /// <summary>CX-120: <c>int enablenonbspgroundhit</c> @0x18aba88 -- the last arm of
    /// <c>HitPartOnBSPNode</c> @0x47b790. For a tile whose BSP node carries NO walkmesh part
    /// (<c>TileData+0x90</c> null -- which is MOST tiles), a downward segment that crosses z=0 and whose
    /// <c>CHitInfo</c> carries no ignore list (<c>+0x10</c> and <c>+0x18</c> both zero) hits the <b>z=0
    /// plane</b>, normal (0,0,1). That is the floor a client-side ground query actually lands on, and
    /// <c>CNWCCreature::ComputeZValue</c>'s is one of them.</summary>
    public const string EnableNonBspGroundHit = "enablenonbspgroundhit";
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
    [Fn("void(CAurTexture this, uint unit, int stage)")]
    public const string AurTextureBindInUnit = "_ZN11CAurTexture10BindInUnitEji";
    /// <summary>CX-52 step 5: <c>CAurTexture::Bind(int)</c> @0x48e0f0 -- <see cref="AurTextureBindInUnit"/>'s twin on
    /// the active unit, same resolution (vtable+0x18 then +0x10). Both are virtual and no subclass overrides either
    /// (slots 33/34 of <c>CAurTexture</c> and <c>CAurTextureBasic</c> alike). ABI: <c>(CAurTexture* /*rdi*/, int frame /*esi*/)</c>.</summary>
    [Fn("void(CAurTexture this, int stage)")]
    public const string AurTextureBind = "_ZN11CAurTexture4BindEi";
    // ---- #127: the grass site. Grass is NOT a draw-bucket resident -- Scene::RenderSinglePass calls
    //      Scene::DoBinBucket @0x474c50 (vt+0x1c0) under the engine's own Tracy scope "Grass", between the
    //      sky and RenderDynamicGeometry. That placement, plus a depth mask it never sets and a distance
    //      fade pushed through the alpha channel the 0.2 cutout reads, IS #106's punch-through.
    /// <summary>Scene::DoBinBucket() @0x474c50 -- the whole grass draw: the per-frame CPU wind rewrite over
    /// EVERY bin in the area, an optional bin sort, then one RenderVertexData per (bin, material) triangle
    /// list with no frustum culling. Skipped outright on the passes the owned grass drew, which is what
    /// retires the wind rewrite as well as the draw. ABI: <c>(Scene* /*rdi*/)</c>.</summary>
    // Not `Scene this`: the view method Scene.DoBinBucket() is the virtual call through its vtbl row.
    [Fn("void(IntPtr scene)")]
    public const string DoBinBucket = "_ZN5Scene11DoBinBucketEv";
    /// <summary>Scene::ReloadGrass() @0x469540 -- rebuilds every bin from the tile BSP (area load, grass
    /// option change, a server grass override). Every CAurTriangleBin and CAurTriangleList the owned pass
    /// captured is freed here, so it is the one signal that invalidates the whole capture.
    /// ABI: <c>(Scene* /*rdi*/)</c>.</summary>
    [Fn("void(Scene this)")]
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
    [Fn("int(CNWCArea this, IntPtr loadscreen, float progress)")]
    public const string AreaLoadBorder = "_ZN8CNWCArea10LoadBorderEP18CGuiAreaLoadscreenf";
    /// <summary>CNWCArea::ReloadBorder() @0x711ff0 -- the server's tile-border toggle / tile change path
    /// (HandleServerToPlayerArea). ABI: <c>(CNWCArea* /*rdi*/)</c>.</summary>
    [Fn("void(CNWCArea this)")]
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
    [Fn("int(CNWCArea this, byte kind, float density)")]
    public const string AreaSetWeather = "_ZN8CNWCArea10SetWeatherEhf";
    /// <summary>CNWCArea::StartWeather(unsigned char type). ABI: <c>(CNWCArea*, uchar)</c>.</summary>
    [Fn("void(CNWCArea this, byte kind)")]
    public const string AreaStartWeather = "_ZN8CNWCArea12StartWeatherEh";
    /// <summary>CNWTileSet* CNWTileSetManager::GetTileSet(CResRef const&amp;) -- the area load's one ask for its
    /// tileset, by name. ABI: <c>(CNWTileSetManager*, const CResRef*)</c>, a pointer return, so no sret on either build.</summary>
    [Fn("IntPtr(IntPtr manager, IntPtr resref)")]
    public const string TileSetGet = "_ZN17CNWTileSetManager10GetTileSetERK7CResRef";
    /// <summary>CNWCArea::StopWeather(unsigned char). ABI: <c>(CNWCArea*, uchar)</c>; the byte is unused.</summary>
    [Fn("void(CNWCArea this, byte kind)")]
    public const string AreaStopWeather = "_ZN8CNWCArea11StopWeatherEh";
    /// <summary>Scene::InitBSP(int w, int h, int border, bool, float cell) @0x46c330 (Scene vt+0x30) -- the
    /// tile BSP covers <c>(w + 2*border) x (h + 2*border)</c> cells, and <c>SceneBSPGetTileData</c> CLAMPS a
    /// position outside it onto the edge leaf (BSPFitSphere), so a wider ring needs a wider border here.
    /// ABI: <c>(Scene* /*rdi*/, int /*esi*/, int /*edx*/, int /*ecx*/, bool /*r8b*/, float /*xmm0*/)</c>.</summary>
    [Fn("void(Scene this, int width, int height, int depth, byte flag, float size)")]
    public const string SceneInitBsp = "_ZN5Scene7InitBSPEiiibf";
    /// <summary>Scene::SpawnTile(char const* model, Vector const&amp; pos, int orientation, int border,
    /// Quaternion*) @0x474470 (Scene vt+0x38) -- every tile, real (border 0, from CNWCTile::LoadModel) or
    /// edge (border 1, from LoadBorder). Position is the tile centre: <c>index*10+5</c>. Returns the Gob*, null
    /// for an edge tile (SpawnPartsForTile builds a bare instance tree for those).
    /// ABI: <c>(Scene* /*rdi*/, char* /*rsi*/, Vector* /*rdx*/, int /*ecx*/, int /*r8d*/, Quaternion* /*r9*/) -> Gob*</c>.</summary>
    [Fn("IntPtr(Scene this, byte* resref, System.Numerics.Vector3* pos, int id, int orientation, IntPtr rot)")]
    public const string SceneSpawnTile = "_ZN5Scene9SpawnTileEPKcRK6VectoriiP10Quaternion";
    /// <summary>Scene::DeleteTile(Vector const&amp;) @0x4636a0 (Scene vt+0x58). Dereferences the tile data at
    /// the position UNGUARDED (its neighbour unlink reads <c>tile+0xb8</c>), so never call it where
    /// <see cref="SceneBspGetTileData"/> is null. ABI: <c>(Scene* /*rdi*/, Vector* /*rsi*/)</c>.</summary>
    [Fn("void(Scene this, System.Numerics.Vector3* pos)")]
    public const string SceneDeleteTile = "_ZN5Scene10DeleteTileERK6Vector";
    /// <summary>SceneBSPGetTileData(Scene*, Vector const&amp;) @0x47b480 -- the TileData* at a position, or
    /// null. ABI: <c>(Scene* /*rdi*/, Vector* /*rsi*/) -> TileData*</c>.</summary>
    [Fn("TileData(Scene this, System.Numerics.Vector3* pos)")]
    public const string SceneBspGetTileData = "_Z19SceneBSPGetTileDataP5SceneRK6Vector";
    /// <summary>GLRender::GetPool(uint) @0x4cfb70 -- the pool's HOST shadow pointer (descriptor +0), a plain
    /// table read with no lock/unlock side effect. What <c>RenderInterface::LockPool(pool, 0, 0, 8)</c>
    /// hands the engine's own grass binning, minus the lock byte. ABI: <c>(uint /*edi*/) -> void*</c>.</summary>
    [Fn("byte*(uint token)")]
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
    [Fn("void(IntPtr part, float dt)")]
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
    /// scene and <c>rsi</c> the position (0x49beaf/0x49bea8), so SysV puts x,y in xmm0 and z in xmm1. Win64
    /// still passes a buffer, so the two argument lists live in <see cref="CX.Alloy.AurXform"/>.</para>
    /// <para>CX-86's second caller: an <c>affectedByWind</c> emitter's three movers each add this to the
    /// integrated position, through that same pair rather than a third copy of the ABI.</para></summary>
    public const string SceneGetWindVector = "_ZN5Scene13GetWindVectorERK6Vectorf";
    /// <summary><c>int Scene::HitCheck(Vector from, Vector to, Vector* hit, CHitInfo* info)</c> @0x462960 --
    /// the engine's own segment test, tile BSP parts first then every gob (<c>info+0</c> == 0), nearest hit
    /// wins; the camera's pull-in is built on it. <c>Vector</c> is BY VALUE and returns <c>int</c>, so no
    /// sret: SysV puts each in xmm pairs, Win64 hands a pointer to a copy -- one blittable struct declaration
    /// lowers to both (#537).</summary>
    [Fn("int(Scene this, VectorArg from, VectorArg to, Vec3* hit, CHitInfo info)")]
    public const string SceneHitCheck = "_ZN5Scene8HitCheckE6VectorS0_PS0_P8CHitInfo";
    /// <summary><c>CHitInfo::Reset(int mode, long mask, int box)</c> @0x45fa90 -- (re)initialises a hit
    /// record, unlinking the SafePointer a previous hit left in it (#537).</summary>
    [Fn("void(CHitInfo this, int mode, long flags, int box)")]
    public const string HitInfoReset = "_ZN8CHitInfo5ResetEili";
    /// <summary><c>Part::Animate(float)</c> @0x49ad00 -- walks the part's children (Part+0x38 pointer,
    /// Part+0x40 count) and calls +0xd8 on each. The dangly solve TAIL-CALLS it, so a replacement that
    /// skips the original must call it or the whole subtree freezes.</summary>
    [Fn("void(IntPtr part, float dt)")]
    public const string PartAnimate = "_ZN4Part7AnimateEf";
    /// <summary><c>PartEmitter::Animate(float)</c> @0x4b3310 -- an IDENTITY, not a call target. It is a pure
    /// tail-jump into <see cref="PartAnimate"/> (<c>push rbp; mov rbp,rsp; pop rbp; jmp 0x49ad00</c>), so a
    /// walk that owns the recursion may treat an emitter part exactly as a plain one instead of dispatching
    /// through it. Resolved so the owned walk can tell it apart from an override it does not know.</summary>
    public const string PartEmitterAnimate = "_ZN11PartEmitter7AnimateEf";
    /// <summary><c>AnimateStaticLight(CAurObject*, char const*, void*)</c> @0x47d820 -- an IDENTITY. It is the
    /// one handler <c>ProcessStaticObject</c> @0x4619e0 registers for that event name, and it is
    /// <c>UpdateStaticLighting</c> @0x47d6a0, whose whole output is the <c>staticlightbucket</c> that only
    /// <c>ManageSceneBSP</c> drains. Resolved so "is the registered handler THIS function" is a pointer
    /// compare rather than a name compare.</summary>
    public const string AnimateStaticLight = "_Z18AnimateStaticLightP10CAurObjectPKcPv";
    /// <summary><c>VertexPrimitiveDangly::GenerateVelocityArray()</c> @0x45ad70 -- allocates the zeroed
    /// <c>verts*12</c> velocity array at <c>+0xa0</c>, lazily, on the first Animate. A replacement that
    /// never calls the original has to drive this itself or the array is never born.</summary>
    [Fn("void(IntPtr vp)")]
    public const string VertexPrimGenVelocity = "_ZN21VertexPrimitiveDangly21GenerateVelocityArrayEv";
    /// <summary><c>VertexDataObject::GetItemDynamicData(uint)</c> @0x45c6c0 -- the host shadow of one item's
    /// DYNAMIC vertex fork, stride 12, which is where dangly writes its deformed positions AND where it
    /// reads last frame's back. Both the state and the output live here.</summary>
    [Fn("byte*(IntPtr vdo, uint item)")]
    public const string VdoGetItemDynamicData = "_ZN16VertexDataObject18GetItemDynamicDataEj";
    /// <summary><c>VertexDataObject::CommitItemDynamicData(uint, uint)</c> @0x45c7d0 -- funnels into
    /// <c>GLRender::UnlockPool</c>, which Alloy owns (#260) and which raises the #241 dirty bit the caster
    /// re-bake reads. Writing the verts is not enough; this is what publishes them.</summary>
    [Fn("void(IntPtr vdo, uint item, uint flags)")]
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
    /// <summary>CX-104: <c>Scene::geomFadeEnabled</c> @0x18aa19c -- the "Hide second-story tiles" option as the
    /// tile fade reads it. <c>graphics.second-story-tiles.mode</c> (0/1/2, default 1) reaches it through
    /// <c>CNWCArea::SetHideFadeGeometryMode</c> @0x704ef0 -> Scene vtable +0xb8, i.e.
    /// <c>Scene::SetGeomFadeProperties</c> @0x45e1d0, as 1 (automatic), 2 (never) and 4 (always). Data symbol;
    /// read only.</summary>
    public const string GeomFadeEnabled = "_ZN5Scene15geomFadeEnabledE";
    /// <summary>#256: <c>countpart</c> @0x18da6b8 -- the engine's own live-part census, incremented in
    /// <c>Part::Part</c> and decremented in <c>Part::~Part</c>. The bring-up validator: it counts every
    /// <c>Part</c> in every scene (GUI panels included), so it is an upper bound on the walk, never an
    /// equality.</summary>
    public const string CountPart = "countpart";
    /// <summary>#256: <c>BSPTraverse(BSPNode*, void(*)(BSPNode*, void*), void*)</c> @0x5b22c0 -- the
    /// UNCULLED variant (the other two take a camera position or a plane list). Visits every node:
    /// <c>node+0x10</c> then iterating <c>node+0x18</c>. Tile geometry has no owning gob, so this is the
    /// only route to it that does not depend on what the camera can see.</summary>
    [Fn("void(IntPtr root, IntPtr visit, IntPtr user)")]
    public const string BspTraverse = "_Z11BSPTraverseP7BSPNodePFvS0_PvES1_";
        /// <summary>Material::HasEnvMap @0x43d1a0-ish -- env-mapped parts take RenderEnvironmentMapped, not
    /// RenderFlat; excluded from batching.</summary>
    [Fn("byte(IntPtr mat)")]
    public const string MatHasEnvMap = "_ZN8Material9HasEnvMapEv";
    /// <summary>Material::HasBumpyShinyMap -- bumpy-shiny parts take RenderBumpyShiny; excluded.</summary>
    [Fn("byte(IntPtr mat)")]
    public const string MatHasBumpyShinyMap = "_ZN8Material16HasBumpyShinyMapEv";
        /// <summary>Material::GetTexture(int) @0x4c5810 -- texture slot i (SharedMaterial override at
    /// mat+0xd0 wins, else own slot at mat+i*0x10). Batch group key = material CONTENT (each part owns
    /// its Material instance, so pointer identity shatters groups to size 1).</summary>
    [Fn("long(Material this, int slot)")]
    public const string MatGetTexture = "_ZN8Material10GetTextureEi";
    /// <summary>#275: <c>SharedMaterial::ParseField(char const*)</c> @0x4c6760 -- one <c>.mtr</c> line. The
    /// engine's chain ends at <c>texture10</c>; the hook keeps <c>texture11+</c> in a side table.</summary>
    [Fn("void(IntPtr shared, byte* line)")]
    public const string SharedMaterialParseField = "_ZN14SharedMaterial10ParseFieldEPKc";
    /// <summary>#275: <c>SharedMaterial::Init(char const*)</c> @0x4c6ff0 -- after the parse, where the
    /// engine loads its own slots (<c>LoadTextureArray</c>, which MSVC inlines here -- hence Init, not it).</summary>
    [Fn("void(IntPtr shared, byte* line)")]
    public const string SharedMaterialInit = "_ZN14SharedMaterial4InitEPKc";
    /// <summary>#275: <c>SharedMaterial::~SharedMaterial()</c> @0x4c5260 (D1 = D2), the side table's death
    /// notice. Called from <c>RemoveRef</c> (last Material gone) and <c>RefreshSharedMaterial</c>.</summary>
    [Fn("IntPtr(IntPtr shared, uint flags)")]
    public const string SharedMaterialDtor = "_ZN14SharedMaterialD2Ev";
    /// <summary>#275: <c>LoadTexture(char const*)</c> @0x4c50f0 -- <c>AurTextureGetReference</c> (an ADDREF'd
    /// reference, unlike <see cref="AurTextureGet"/>), or the NULL texture for an empty/"NULL" name.</summary>
    [Fn("IntPtr(byte* resref)")]
    public const string LoadTexture = "_Z11LoadTexturePKc";
    /// <summary>#275: <c>AurTextureRelease(CAurTexture*)</c> @0x486010 -- the release the destructor pairs
    /// with <see cref="LoadTexture"/>.</summary>
    [Fn("void(IntPtr tex)")]
    public const string AurTextureRelease = "_Z17AurTextureReleaseP11CAurTexture";
    /// <summary>Part::~Part() @0x437720 (D1 and D2 share the address) -- the BASE destructor every part
    /// class tail-jumps to: PartSkin and PartDanglyMesh jump to PartTriMesh::~PartTriMesh, which jumps here
    /// with <c>this</c> unchanged (verified in the disassembly, no thunk adjust). #145: the one event that
    /// says a recorded part is GONE. Without it the part table holds the pointer until something happens to
    /// notice, and the owned pass chases it into freed memory.</summary>
    [Fn("void(IntPtr part)")]
    public const string PartDtor = "_ZN4PartD2Ev";
    /// <summary>#397 diagnostic: <c>Gob::Append(char const*, char const*)</c> @0x4422a0 (model, attach
    /// node) and <c>Gob::Amputate(char const*)</c> @0x443fa0 -- the equipment-swap pair. Logged cold to say
    /// whether a swap amputates what it appended. Linux-only: no Windows anchor derived (skip + log there).</summary>
    [Fn("int(IntPtr gob, IntPtr model, IntPtr node)")]
    public const string GobAppend = "_ZN3Gob6AppendEPKcS1_";
    [Fn("int(IntPtr gob, IntPtr node)")]
    public const string GobAmputate = "_ZN3Gob8AmputateEPKc";
    /// <summary><c>Gob::SetScene(Scene*)</c> @0x43e020 -- the SOLE writer of <see cref="SceneSequence"/>, the
    /// engine's global scene-membership epoch. Verified, not inferred: the whole image holds exactly two
    /// references to that global, the <c>addl $0x1</c> at 0x43e0aa in this function and
    /// <c>ManageSceneBSP</c>'s read at 0x47fb68. It bumps only when <c>gob+0x88</c> actually changes, and it
    /// has the gob and BOTH scenes in hand -- which is what lets <see cref="CX.Alloy.SceneWalk"/> answer a
    /// membership change with one gob's tree instead of the whole area's.</summary>
    [Fn("void(IntPtr gob, IntPtr scene)")]
    public const string GobSetScene = "_ZN3Gob8SetSceneEP5Scene";
    /// <summary><c>Gob::SetSubtreeEnabled(char const*, int)</c> @0x43c670 -- the sole DYNAMIC writer of
    /// <c>part+0x34</c>, the engine's own draw gate. Its whole body is
    /// <c>p = FindPart(gob+0x90, name); if (p) p[0x34] = (v != 0);</c>: no epoch bump, no part created, none
    /// destroyed, so hiding or showing a subtree announces itself nowhere else. The only other writer is
    /// <c>Gob::Append</c> @0x4422a0, which sets the byte on the new subtree's ancestors and is hooked already.
    /// Without this, a part hidden between two full walks keeps drawing until one happens to run (#264/CX-92).
    /// The search is over <c>Gob+0x90</c>, the ACTIVE tree, so the gob is the right unit to re-walk.</summary>
    [Fn("byte(IntPtr gob, IntPtr name, int on)")]
    public const string GobSetSubtreeEnabled = "_ZN3Gob17SetSubtreeEnabledEPKci";
    /// <summary>VertexDataObject::Repack() @0x45c270 -- compacts tombstoned items (RemoveItem only zeroes
    /// sizes), MOVING the surviving items' indices and offsets. Triggered by AddItem on fragmentation, so
    /// streaming fires it mid-play; every batch member of that VDO must recapture its item.</summary>
    [Fn("void(VertexDataObject this)")]
    public const string VdoRepack = "_ZN16VertexDataObject6RepackEv";
    /// <summary>#212: GLRender::UnlockPool(token, size, offset) @0x4cfc80 -- the SINGLE funnel through which
    /// every byte of engine vertex/index data reaches a GL buffer (glBufferData or glBufferSubData off the
    /// pool's host shadow). <c>VertexDataObject::Build</c>, <c>AddItem</c>'s grow, all three
    /// <c>CommitItem*Data</c> and <c>Repack</c>'s post-compaction re-upload all pass through it, so #260
    /// PERFORMS it rather than observing it. Hooked at the GLRender level rather than the
    /// <c>RenderInterface</c> thunk because that thunk is its only direct caller AND the vtable slot points
    /// here -- the lower funnel catches both.</summary>
    [Fn("void(uint pool, uint offset, uint size)")]
    public const string GlUnlockPool = "_ZN8GLRender10UnlockPoolEjjj";
    /// <summary>#212: GLRender::DeleteBuffer(name) @0x4cfec0 -- where an engine GL buffer name dies.
    /// <c>GLRender::DeletePool</c> @0x4d1d80 routes here whenever a pool is released, freeing the buffer
    /// #260 minted for it; this is the exact moment the name (and the pool token behind it) go back into
    /// circulation. The geometry twin of <see cref="RiDeleteTextures"/>, and the same #220 corruption
    /// without it.</summary>
    [Fn("void(uint name)")]
    public const string GlDeleteBuffer = "_ZN8GLRender12DeleteBufferEj";
    /// <summary><c>GLRender::GetVertexBufferMaxElementVertices</c> @0x4cb730 / <c>...Indices</c> @0x4cb700:
    /// <c>glGetIntegerv(GL_MAX_ELEMENTS_VERTICES / _INDICES)</c>, cached. The ONLY reader is
    /// <c>VertexDataObjectManager::GetVertexDataObjectTokenByAttributes</c> @0x45bdc0, which reuses a shared
    /// VDO only while its vertex and index totals stay under them -- so the driver's answer is the shared
    /// pool's capacity. NVIDIA answers 1048576; every Mesa driver answers 3000 (core <c>MaxArrayLockSize</c>,
    /// reproduced under zink and llvmpipe), which fragments an area into hundreds to thousands of VDOs.</summary>
    [Fn("int()")]
    public const string GlMaxElementVertices = "_ZN8GLRender33GetVertexBufferMaxElementVerticesEv";
    [Fn("int()")]
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
    [Fn("void(IntPtr vdo)")]
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
    [Fn("void(IntPtr prog)")]
    public const string GlSetUniforms = "_ZN8GLRender11SetUniformsERNS_10PROG_FIXEDE";
    /// <summary><c>GLRender::GlUseProgramConditionally(unsigned int)</c> @0x4ce8b0 -- glUseProgram behind the
    /// engine's own current-program cache. Called on the engine side so that cache stays true.</summary>
    [Fn("void(uint program)")]
    public const string GlUseProgramConditionally = "_ZN8GLRender25GlUseProgramConditionallyEj";
    /// <summary>Global shader param scalar getter -- param 0x12 is the light-radius multiplier
    /// GetLightAdjustedRadius applies (adjR = range * intensity * overbright * this).</summary>
    [Fn("float(uint param)")]
    public const string RiGetGlobalShaderParamf = "_ZN15RenderInterface21GetGlobalShaderParamfEN6Aurora23AurGlobalShaderParamf_tE";
    /// <summary>Global shader param INTEGER getter -- param 0 is the gamma pipeline, the engine's own gate on
    /// the colour-space round trip (it becomes <c>#define GAMMA_CORRECTION %d</c> in every shader preamble,
    /// and <c>ApplyColorSpace</c>/<c>RevertColorSpace</c> in inc_common.shd are identity when it is 0).
    /// #132's resolve reads it so the one remaining encode is gated where the per-shader encodes never
    /// were.</summary>
    [Fn("int(uint param)")]
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
    [Fn("float(IntPtr light)")]
    public const string GetLightAdjustedRadius = "_Z22GetLightAdjustedRadiusPK9PartLight";
    /// <summary>CX-85: <c>GetLightFadeSpeed(PartLight const*)</c> @0x4c0cb0 -- how fast one light's weight
    /// travels, in weight per second: <c>m = maxChannel(GetLightAdjustedColor) * GetLightAdjustedRadius</c>,
    /// then <c>m &lt; 1 -&gt; 10</c>, <c>m &gt; 10 -&gt; 1</c>, else <c>10 / m</c> (disassembled at 0x4c0cee;
    /// the decompiler drops the colour's third channel and the two constants are 1.0f and 10.0f).
    /// <para>Called rather than transcribed: it is a pure leaf of two other pure leaves, it issues no draw and
    /// touches no engine state, and re-deriving it would put a second copy of the overbright curve in the
    /// repo. CX-85's <c>LightFade</c> owned the integrator that used it.</para>
    /// ABI: <c>(PartLight* /*rdi*/) -&gt; float /*xmm0*/</c>.</summary>
    [Fn("float(IntPtr light)")]
    public const string GetLightFadeSpeed = "_Z17GetLightFadeSpeedPK9PartLight";
    /// <summary>CX-85: <c>GetIsLightInViewVolume(PartLight const*)</c> @0x4c0d30 -- the frustum test
    /// <c>ControlLightsFade</c> uses to decide whether an unlit light fades out gracefully or snaps to zero.
    /// Sphere-vs-plane over <c>CurrentCamera+0x4e8</c>/<c>+0x4f0</c> with a fog-end special case on plane 1,
    /// and always true for a directional (adjusted radius &gt;= 1000). Called, not transcribed: it reads the
    /// engine's own plane array, which is the camera Alloy's frame is drawing from anyway. Not pure: it
    /// memoises the last rejecting plane index in a global, which nothing else reads.
    /// ABI: <c>(PartLight* /*rdi*/) -&gt; bool /*al*/</c>.</summary>
    [Fn("byte(IntPtr light)")]
    public const string GetIsLightInViewVolume = "_Z22GetIsLightInViewVolumePK9PartLight";
    /// <summary>CX-85: <c>LightManager::GetMaxLights()</c> @0x4c15d0 -- <c>min(m_nMaxLights, 128)</c>, the cap
    /// on how many lights may be lit at once and so on how far down the per-frame list
    /// CX-85's <c>LightFade</c> faded IN. Static, no <c>this</c>.</summary>
    [Fn("int()")]
    public const string LightMgrGetMaxLights = "_ZN12LightManager12GetMaxLightsEv";

    // ---- CX-104: the three LightManager steps ManageSceneBSP @0x47fa80 makes around its BSP walk
    //      (vtable +0x38, +0x40 then +0x10, decompiled at _global.c:809916/809917/809988). Under
    //      alloy.skipbuckets=3 that walk does not run, so CX.Alloy.ScenePrep makes them itself, in the
    //      engine's order. Called DIRECTLY and not through the vtable: _ZTV12LightManager is the only
    //      LightManager vtable in the binary (no derived class), so the concrete function IS what the
    //      vcall dispatches to. All three are name-exported on both builds.
    /// <summary><c>LightManager::UpdateLights()</c> @0x4c06c0 -- walks the master light list and refreshes
    /// every <c>PartLight</c>'s WORLD position (<c>+0xac</c>) through <c>Part::GetWorld</c>, then re-picks the
    /// area diffuse (<c>LightManager+0x98</c>) and ambient (<c>+0xa0</c>) from the two lights whose range is
    /// &gt;= 1000. <b>The only per-frame writer of <c>PartLight+0xac</c> anywhere in the client</b> -- the other
    /// two are <c>AddLight</c> (registration) and <c>BSPCreateStaticProjections</c> (the per-area bake) -- and
    /// Alloy reads that field as <c>light.Pos</c> in five places (the registry's candidacy and score, the fade
    /// accumulation behind <c>focuslighting</c>, halo placement, the light snapshot). So this one is CALLED,
    /// never skipped: dropping it freezes every light welded to a moving part at its last position and takes
    /// <c>CNWCArea::GetIsDark</c> stale with it. ABI: <c>(LightManager* /*rdi*/)</c>.</summary>
    [Fn("void(LightManager this)")]
    public const string LightMgrUpdateLights = "_ZN12LightManager12UpdateLightsEv";
    /// <summary><c>LightManager::SetupLightsForThisPositionAndRadius(Vector const&amp;, float)</c> @0x4c1df0 --
    /// refills the per-frame candidate list (<c>LightManager+0x40</c>/<c>+0x48</c>) with the master lights that
    /// are enabled, coloured, positive-intensity, under range 1000 and within
    /// <c>GetLightAdjustedRadius + radius</c> of the point. <c>ManageSceneBSP</c> passes
    /// <c>Scene::m_vSceneFocus</c> (<c>Scene+0x190</c>) and <c>max(45.0f, Scene+0x148 /*fog end*/)</c> -- the
    /// float is invisible in the decompilation and was read off the disassembly at 0x480093.
    /// <para>Alloy enumerates the scene database instead, so filling this list is dead work;
    /// it is called only for the <c>light.registry 0</c> A/B, whose fallbacks read the engine list directly.
    /// </para>ABI: <c>(LightManager* /*rdi*/, Vector const* /*rsi*/, float /*xmm0*/)</c>.</summary>
    [Fn("void(LightManager this, IntPtr pos, float radius)")]
    public const string LightMgrSetupLights = "_ZN12LightManager35SetupLightsForThisPositionAndRadiusERK6Vectorf";
    /// <summary><c>LightManager::PrioritizeLights(Vector const&amp;)</c> @0x4c0940 -- scores each candidate
    /// (<c>PartLight+0x98</c> distance, <c>+0x9c</c> score), sorts the candidate list by it
    /// (<c>PrioritizeIllumination</c>) and rebuilds the shadow list <c>LightManager+0x50</c>/<c>+0x58</c>
    /// (<c>PrioritizeShadow</c>). Alloy's own cap and score replace the ordering, but the shadow list is what
    /// <c>BSPCreateStaticProjections</c> reads on the AREA-LOAD bake, and whether a skipped frame can ever
    /// precede that bake was not established -- so this is called too rather than dropped on an argument.
    /// ABI: <c>(LightManager* /*rdi*/, Vector const* /*rsi*/)</c>.</summary>
    [Fn("void(LightManager this, IntPtr pos)")]
    public const string LightMgrPrioritizeLights = "_ZN12LightManager16PrioritizeLightsERK6Vector";
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
    [Fn("byte(PartTriMesh this)")]
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
    [Fn("int(CAurBehaviorCameraFollow this, float dt)")]
    public const string CameraFollowControl = "_ZN24CAurBehaviorCameraFollow7ControlEf";

    /// <summary>CX-120: <c>CAurBehaviorCameraFollow::CAurBehaviorCameraFollow(CAurObject* owner, CAurObject*
    /// target)</c> @0x5a33f0 -- the constructor <c>Camera::SetFollow</c> @0x5aa500 runs on
    /// <c>operator_new(0x90)</c>, with the camera's OWN <c>CAurObject</c> sub-object (<c>Camera+8</c>) as the
    /// owner. ADR 0028 keeps <c>CAurBehavior</c> objects real engine-layout objects that the game constructs
    /// and CX calls through their vtables; this is the one the GAME would have constructed here, so the seam
    /// runs the same constructor rather than inventing a stand-in.</summary>
    [Fn("void(nint self, nint owner, nint target)")]
    public const string CameraFollowCtor = "_ZN24CAurBehaviorCameraFollowC2EP10CAurObjectS1_";

    /// <summary><c>CNWCModule::TurnCamera(float deg, int impulse)</c> @0x7d8960 / <c>TiltCamera</c> @0x7d88a0 -- the
    /// engine's own orbit input (#539). impulse=1 adds to the behavior's one-shot yaw/pitch impulse (+0x88/+0x8c),
    /// consumed by the next Control; the flag byte (+0x2bc bit 0x10 / 0x20) refuses a server-disabled axis and the
    /// dialog "user turned" bookkeeping (+0x134, +0x280) is kept. Middle-drag calls exactly these.</summary>
    [Fn("void(CNWCModule this, float deg, int impulse)")]
    public const string TurnCamera = "_ZN10CNWCModule10TurnCameraEfi";
    /// <inheritdoc cref="TurnCamera"/>
    [Fn("void(CNWCModule this, float deg, int impulse)")]
    public const string TiltCamera = "_ZN10CNWCModule10TiltCameraEfi";
    /// <summary><c>CClientExoAppInternal::UpdateDriveMode()</c> @0x65f780 -- the per-frame drive step (#540). Reads the
    /// key booleans at +0x1b8.. and the "input changed" latch at +0x1b0, derives the wire heading from the player
    /// creature's orientation (+0x44/+0x48, <c>round(atan2 * 10)</c>), sends <c>DriveControl</c> on the latch or the
    /// 500 ms keep-alive, snaps the local facing and runs the prediction. Pre-hooked by CameraDriveService, which
    /// writes the orientation and the key set it wants the engine to act on.</summary>
    [Fn("void(CClientExoAppInternal this)")]
    public const string UpdateDriveMode = "_ZN21CClientExoAppInternal15UpdateDriveModeEv";
    /// <summary><c>CNWCCreature::WalkUpdateLocation()</c> @0x725830 -- the per-frame walk step (CX-682): picks the rate off
    /// the animation, steps the desired position (<see cref="EngineLayout.CreatureDesiredPos"/>) along the waypoints and
    /// moves the model to it, testing no walkmesh and no door. <c>(this) -> int</c>, always 1. Pre-hooked by
    /// StepGuardService for the player creature.</summary>
    [Fn("int(CNWCCreature this)")]
    public const string WalkUpdateLocation = "_ZN12CNWCCreature18WalkUpdateLocationEv";
    /// <summary><c>CNWCArea::TestDirectLine(CNWCCreature*, Vector from, Vector to, float, float, int bCheckCreatures)</c>
    /// @0x703850: the engine's own door-aware swept walkability test (tile meshes, placeable walkmeshes, then every
    /// door's DWK with closed and mid-animation doors solid). Returns 1 on a clear line. The two floats are the pair
    /// at <see cref="EngineLayout.PathfindSpaceArgs"/> in that order (disassembly of ClientSideWalkCommand @0x72043e:
    /// xmm4 = +0xe0, xmm5 = +0xe4). Both Vectors by value: SysV xmm0/xmm1 + xmm2/xmm3, Win64 a pointer each.</summary>
    [Fn("int(CNWCArea this, CNWCCreature creature, VectorArg from, VectorArg to, float space, float height, int checkCreatures)")]
    public const string TestDirectLine = "_ZN8CNWCArea14TestDirectLineEP12CNWCCreature6VectorS2_ffi";
    /// <summary>SDL2 cursor capture for mouse-look (#539): relative mode hides the cursor and streams raw deltas.</summary>
    [Fn("int(int enabled)")]
    public const string SdlSetRelativeMouseMode = "SDL_SetRelativeMouseMode";
    /// <inheritdoc cref="SdlSetRelativeMouseMode"/>
    [Fn("void(IntPtr window, int x, int y)")]
    public const string SdlWarpMouseInWindow = "SDL_WarpMouseInWindow";
    /// <summary><c>CGuiInGame::ToggleOff()</c> @0x66dda0 / <c>UnToggleOff()</c> @0x66e930 -- the engine's own
    /// hide/show of the whole in-game GUI (every panel, quick chat, the NUI scope), the pair the server's
    /// Cutscene_HideGui message (major 0x33 minor 7) and <c>SetCutsceneMode</c> call. The free camera (#543)
    /// hides the UI through exactly these; <c>this</c> = <c>CClientExoAppInternal+0x90</c>.</summary>
    [Fn("void(CGuiInGame this)")]
    public const string GuiInGameToggleOff = "_ZN10CGuiInGame9ToggleOffEv";
    /// <inheritdoc cref="GuiInGameToggleOff"/>
    [Fn("void(CGuiInGame this)")]
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
    [Fn("void(CNWCCreature this)")]
    public const string UpdateLookAt = "_ZN12CNWCCreature12UpdateLookAtEv";
    /// <summary><c>CNWCCreature::PlayFootstep()</c> @0x72ff90 -- one footstep: the surface material under the
    /// creature picks the sound (footstepsounds.2da by FootstepType) and the water visual, played as a 3D one-shot
    /// at its position. Callers: <c>FootstepEvent</c>, the model's <c>snd_Footstep</c> animation event bound in
    /// <c>LoadModel</c> (the walk/run footfalls); <c>AnimationUpdate</c> for fire-and-forget 0x18-0x1b; and
    /// <c>AIUpdate</c>'s random idle shuffle. Hooked by <see cref="Services.Anim.Footfall"/>. Windows: rva 0x8057c0,
    /// the one function outside the NWScript/GFF commands reading "FootstepType"; body decompiles identical
    /// (SIZECATEGORY, Play3DOneShotSound, IsWater), and LoadModel (rva 0x802cc0) binds "snd_Footstep" to a thunk.</summary>
    [Fn("void(CNWCCreature this)")]
    public const string PlayFootstep = "_ZN12CNWCCreature12PlayFootstepEv";
    /// <summary><c>Gob::BeginLookAtAnimate(CAurObject* target, char const* targetNode, char const* myNode,
    /// float arcH, float arcV, float blend, float maxDist)</c> @0x49d5b0 -- <c>FindPart</c>s both nodes (silently
    /// returning if either misses), destroys any existing <c>LookAtAnimation</c> at Gob+0x1b0 and builds a fresh
    /// one plus its AnimRun. Virtual (Gob slot +0x250 / Windows +0x248) but there is exactly one implementation,
    /// so it is called by symbol. All-void return, no by-value crossing.</summary>
    [Fn("void(Gob this, IntPtr target, byte* targetNode, byte* myNode, float arcH, float arcV, float blend, float maxDist)")]
    public const string BeginLookAtAnimate = "_ZN3Gob18BeginLookAtAnimateEP10CAurObjectPKcS3_ffff";
    /// <summary><c>LookAtAnimation::Control()</c> @0x4973c0 -- one head-aim step, driven by <c>Gob::Animate</c>
    /// @0x49d8c0 through the vtable. Deactivates past the max distance, else writes the clamped look-at
    /// quaternion into the synthetic animation's second keyframe (<c>*(float**)(this+0x58)</c>, x/y/z/w) and the
    /// head's current local rotation into the first (+0x60), so every frame is a fresh 0.25 s ease. Chained, not
    /// installed: the aim is overwritten after the original runs.</summary>
    [Fn("void(LookAtAnimation this)")]
    public const string LookAtControl = "_ZN15LookAtAnimation7ControlEv";
    /// <summary>CX-29: <c>Gob::Animate(float dt)</c> @0x49d8c0 -- one gob's whole animation step, called for
    /// every gob in the scene every frame by <c>Scene::Animate</c> (vtable slot +0x30 over <c>Scene+0x130</c>,
    /// with <c>currentscene</c> set to that scene for the duration). Advances the AnimRun list at
    /// <c>Gob+0xe0</c> and writes every part's local pos/rot; nothing composes world transforms here, so a
    /// local written after it reaches every draw. Chained. ABI: <c>(Gob* /*rdi*/, float dt /*xmm0*/)</c>.</summary>
    [Fn("void(Gob gob, float dt)")]
    public const string GobAnimate = "_ZN3Gob7AnimateEf";
    /// <summary>CX-27: <c>AnimateHierarchy(Part*, MdlNode*, float from, float to, float weight)</c> @0x499990 -- one
    /// AnimRun applied to a part and, through a direct self-call, to each child. <c>Gob::Animate</c> passes the run's times
    /// (+0x10, +0x14) and its crossfade weight (+0x18: raised by dt / +0x1c to 1, a fading run lowered to 0), which every
    /// controller blends by (<c>NewController::Control</c>: <c>slerp(current, key, weight)</c>). Chained. ABI: Itanium
    /// <c>(rdi, rsi, xmm0, xmm1, xmm2)</c>; Win64 <c>(rcx, rdx, xmm2, xmm3, [rsp+0x28])</c> -- the weight on the stack.</summary>
    [Fn("void(IntPtr part, IntPtr node, float from, float to, float weight)")]
    public const string AnimateHierarchy = "_Z16AnimateHierarchyP4PartP7MdlNodefff";
    /// <summary>CX-28: <c>NewController::GetQuaternionValue(float t, MdlNode*, Quaternion&amp;)</c> @0x498640 -- a
    /// rotation track's value at animation time <c>t</c>: the bracketing keys slerped, the end key held past either
    /// end. 0 (nothing written) unless the track is 4 wide. Called by <c>NewController::Control</c> and
    /// <c>ReportKey</c>; the out value is w-first (<see cref="CX.Engine.Quat"/>). Returns int in eax on both
    /// builds (the MSVC name says <c>H</c>).</summary>
    [Fn("int(NewController self, float t, IntPtr node, Quat* rot)")]
    public const string CtrlGetQuaternion = "_ZN13NewController18GetQuaternionValueEfP7MdlNodeR10Quaternion";
    /// <summary>CX-28: <c>NewController::GetVectorValue(float t, MdlNode*, Vector&amp;)</c> @0x498620 -- a vector
    /// track's value (a position, or a colour), linear or Bezier; a position track (id <see cref="EngineLayout.PartPos"/>)
    /// comes out times <see cref="PositionScaleFactor"/>. Same callers and return as <see cref="CtrlGetQuaternion"/>.</summary>
    [Fn("int(NewController self, float t, IntPtr node, System.Numerics.Vector3* v)")]
    public const string CtrlGetVector = "_ZN13NewController14GetVectorValueEfP7MdlNodeR6Vector";
    /// <summary>CX-28: the float <c>GetVectorValue</c> multiplies a position track by (both builds). Data symbol; read only.</summary>
    public const string PositionScaleFactor = "position_scale_factor";

    // ---- CX-106 anim: the owned animation runtime (CX-106) ----
    // Every symbol in this block is NAME-EXPORTED by nwmain.exe, so the block carries no RVA row.

    /// <summary><c>Scene::Animate(float dt)</c> @0x469810 -- the whole per-frame animation walk: latch the
    /// <c>currentscene</c> global, dispatch <see cref="EngineLayout.GobAnimateSlot"/> over
    /// <c>Scene+0x130</c> from the END of the list to the front, clear the latch. Virtual (Scene vtbl +0x28 /
    /// Windows +0x20) with one implementation, so hooking the symbol catches every caller --
    /// <c>CGuiMan::UpdateAndRender</c> runs it once per <c>CSceneView</c> (the world viewport and every GUI
    /// scene are entries in the same list) plus two overlay views, and <c>CGuiPanel::AnimateScene</c> adds
    /// ad-hoc ones. It does NOT touch <see cref="EngineLayout.SceneAnimFrame"/> -- <c>Scene::Render</c>
    /// @0x4762d0 does.</summary>
    [Fn("void(Scene this, float dt)")]
    public const string SceneAnimate = "_ZN5Scene7AnimateEf";
    /// <summary><c>NewController::GetFloatValue(float t, MdlNode*, float&amp;)</c> @0x498480 -- a scalar
    /// track's value. The owned runtime evaluates linear tracks itself and calls this only for a BEZIER one,
    /// whose control-point arithmetic differs from the vector overload's in a way that looks like an engine
    /// bug (CX-106 section 8); reproducing that is pure risk.</summary>
    [Fn("int(NewController self, float t, IntPtr node, float* v)")]
    public const string CtrlGetFloat = "_ZN13NewController13GetFloatValueEfP7MdlNodeRf";
    /// <summary><c>NewController::ReportKeys(Part*, MdlNode*, float from, float to)</c> @0x498b20 -- the
    /// per-key TRACE channel, run after every controller of a node while <c>AnimRun::m_pTraceRun</c> is set
    /// (<c>AnimRun</c> flag 0x100 only). It reaches game code through <c>DoEvent("ReportKey", ...)</c>, so
    /// the owned runtime calls the engine's own rather than port a path nothing in ordinary play arms.</summary>
    [Fn("void(NewController self, IntPtr part, IntPtr node, float from, float to)")]
    public const string CtrlReportKeys = "_ZN13NewController10ReportKeysEP4PartP7MdlNodeff";
    /// <summary><c>PartEmitter::CalculateRadius()</c> @0x4ac350 -- what
    /// <c>MdlNodeEmitter::InternalControl</c> does AFTER its controller pass, and the only work any of the
    /// six subclass controllers adds beyond a discarded type check.</summary>
    [Fn("void(IntPtr emitter)")]
    public const string EmitterCalcRadius = "_ZN11PartEmitter15CalculateRadiusEv";
    /// <summary><c>CExoArrayList&lt;AnimRun*&gt;::Remove(AnimRun*)</c> @0x49f290 -- linear find then
    /// <c>DelIndex</c>; order-preserving. The owned queue advance removes exactly where the engine does.</summary>
    [Fn("void(IntPtr list, IntPtr run)")]
    public const string AnimRunListRemove = "_ZN13CExoArrayListIP7AnimRunE6RemoveES1_";
    /// <summary><c>CExoArrayList&lt;AnimRun*&gt;::DelIndex(int)</c> @0x49f240 -- the look-at teardown's
    /// removal, which unlinks WITHOUT freeing the run.</summary>
    [Fn("void(IntPtr list, int index)")]
    public const string AnimRunListDelIndex = "_ZN13CExoArrayListIP7AnimRunE8DelIndexEi";
    /// <summary><c>CExoArrayList&lt;SafePointer&lt;CAurObject&gt;*&gt;::Add</c> @0x49f3f0 -- registers the
    /// death notice <c>Gob::Animate</c> takes on itself before it runs any game code. Called rather than
    /// reimplemented because the growth path is the engine's allocator.</summary>
    [Fn("void(IntPtr list, IntPtr safePointer)")]
    public const string SafePointerAdd = "_ZN13CExoArrayListIP11SafePointerI10CAurObjectEE3AddES3_";
    /// <summary><c>CNWCAnimBase::GetAnimationName(ushort)</c> @0x9ccd80 -- a creature's anim id to its animation name
    /// (vtable +0x60), then <c>GetAnimationReplace</c>. <c>SetAnimation</c>, <c>GetAnimationLength</c> and
    /// <c>GetAnimationTime</c> all resolve through it, so an id swapped here swaps the length and pacing too. Returns
    /// a <c>CExoString</c> BY VALUE: Itanium <c>(sret, this, id)</c>, Win64 <c>(this, sret, id)</c>, the buffer
    /// back in rax on both. No <c>[Fn]</c>: an sret symbol is hooked by hand (<see cref="CX.Services.Anim.Diagonal"/>).</summary>
    public const string AnimGetName = "_ZN12CNWCAnimBase16GetAnimationNameEt";

    /// <summary><c>CNWCCreature::SpawnHitVisuals(uchar flag, CNWCCombatAttackData*, Vector, CExoString)</c>: the one
    /// funnel a landed melee swing (<c>SwingHitEvent</c>) and a projectile impact (<c>CNWCProjectile::AIUpdate</c>)
    /// both call on the TARGET, flag 1; a parry calls it on the attacker with flag 0. Chained cold by the pose
    /// layer's hit reactions. ABI: <c>(this /*rdi*/, flag /*esi*/, ad /*rdx*/, Vector /*xmm0 = (x,y), xmm1 = z*/,
    /// CExoString* /*rcx, invisible reference*/)</c>; Win64 passes both aggregates as pointers. Returns nothing,
    /// so no sret order (CX-158 §3.1).</summary>
    [Fn("void(CNWCCreature this, byte flag, IntPtr attackData, VectorArg push, IntPtr text)")]
    public const string SpawnHitVisuals = "_ZN12CNWCCreature15SpawnHitVisualsEhP20CNWCCombatAttackData6Vector10CExoString";

    /// <summary>Camera::GetCanKeyholeDissolve(PartTriMesh*) @ (member) -- returns whether a caster keyhole-
    /// dissolves (the camera-relative aperture around the PC). Hooked to return 0 during the depth pass so
    /// casters aren't punched out of the shadow map. ABI: <c>(Camera* /*rdi*/, PartTriMesh* /*rsi*/) -> bool</c>.</summary>
    public const string GetCanKeyholeDissolve = "_ZN6Camera21GetCanKeyholeDissolveEPK11PartTriMesh";

    // Aurora texture manager: get a portrait as an engine-resident GL texture. AurTextureGet(name,
    // NULL, NULL, NULL, 0, false) loads/caches a CAurTexture*; AurTextureGetTID(tex) returns its GL
    // texture id (0 until the engine's RefreshTextures pass uploads it). Chat portraits (#26) bind it
    // directly -- no decode, no upload. See ConvertPortrait for the H/L/M/S/T size suffixes.
    // CX-110: AlloyEnvMap's per-frame env-map lookup is this same call. It returns a BORROWED pointer and
    // takes no reference (AurTextureGetReference @0x48e480 is this plus a second call that passes the result
    // back in argument 2), which is the whole reason the env map keeps the resref and re-fetches every world
    // frame rather than holding a pointer -- the engine frees AND RELOADS its textures on an area transition
    // (agent memory aurtexture-lifetime), and a reference taken over that cannot be released safely.
    [Fn("IntPtr(byte* name, IntPtr tex, byte* plt, int* scheme, int count, byte byName)")]
    public const string AurTextureGet    = "_Z13AurTextureGetPKcP11CAurTextureS0_Piib";
    [Fn("int(IntPtr tex)")]
    public const string AurTextureGetTID = "_Z16AurTextureGetTIDP11CAurTexture";
    /// <summary>#331: <c>AurTextureGetController(CAurTexture*)</c> @0x485350 -- resolves ref -> basic and returns
    /// <c>basic+0x38</c>, the texture's animation controller (or null).</summary>
    [Fn("IntPtr(IntPtr tex)")]
    public const string AurTextureGetController = "_Z23AurTextureGetControllerP11CAurTexture";
    /// <summary>CX-25: <c>CAurTexture::~CAurTexture()</c> @0x486080 (D1 = D2), the death notice for every
    /// texture. <c>CAurTextureBasic</c>'s D1 tail-jumps here and both deleting dtors call it, on Linux and
    /// Windows alike (verified in both disassemblies).</summary>
    [Fn("void(IntPtr tex)")]
    public const string AurTextureDtor = "_ZN11CAurTextureD2Ev";
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
    [Fn("void(IntPtr tex)")]
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
    [Fn("int(IntPtr resman, byte* resref, ushort type, uint* id)")]
    public const string ResManExists       = "_ZN10CExoResMan6ExistsERK7CResReftPj";
    [Fn("IntPtr(IntPtr resman, byte* resref, ushort type)")]
    public const string ResManGetResObject = "_ZN10CExoResMan12GetResObjectERK7CResReft";
    [Fn("void(IntPtr resman, byte* resref, ushort type, IntPtr res)")]
    public const string ResManSetResObject = "_ZN10CExoResMan12SetResObjectERK7CResReftP4CRes";
    [Fn("long(IntPtr resman, IntPtr res)")]
    public const string ResManDemand       = "_ZN10CExoResMan6DemandEP4CRes";
    /// <summary>CX-54 step 1: the one PURE lookup -- "where does this resref live" with no I/O, no allocation
    /// and no <c>CRes</c>. It applies the server's <c>GetOverride</c> alias itself (so the RAW resref goes in),
    /// walks the priority-sorted tables skipping any mid-rebuild, and hands back the winning table and key
    /// entry. <c>logMissing: false</c> makes it side-effect free; <c>CExoResMan::Exists</c> is this plus a read
    /// of <see cref="EngineLayout.KeyTableKind"/>. Returns an int through out-params, so there is no
    /// <c>sret</c> on either OS -- unlike <c>GetResID</c> and <c>GetOverride</c>, which must never be called
    /// directly (#456).</summary>
    [Fn("int(IntPtr resman, byte* resref, ushort type, IntPtr* table, IntPtr* entry, byte logMissing)")]
    public const string ResManGetKeyEntry  = "_ZN10CExoResMan11GetKeyEntryERK7CResReftPP12CExoKeyTablePP14CKeyTableEntryb";
    /// <summary>CX-54 step 1: alias name plus restype to an absolute filename -- what <c>CExoFileInternal</c>
    /// itself calls (Linux @0x516dc0, Windows RVA 0x166D20). It finds the <c>':'</c>, resolves the prefix
    /// through the alias list, appends the remainder and then <c>'.' + GetResourceExtension(restype)</c>.
    /// Returns a <c>CExoString</c> BY VALUE, so <b>sret</b>, and the buffer is the CALLER's to free
    /// (<see cref="ExoStringDtor"/>).</summary>
    [Fn("sret(CExoAliasList this, IntPtr name, ushort resType)")]
    public const string AliasResolveFileName = "_ZNK13CExoAliasList15ResolveFileNameERK10CExoStringt";
    /// <summary>CX-52: two of the twelve decoder slots <c>CAuroraInterface::CAuroraInterface()</c> @0x1124ac0
    /// stores at boot -- DATA symbols holding function pointers, swapped by a store, not a hook. The getter
    /// (live target <c>GetCompressedRaster</c> @0x1125990) is
    /// <c>uchar*(const char* name, int* container, int* w, int* h, int* channels, int* level0Size, float* alphaMean)</c>
    /// and returns level 0 inside the demanded resource; the release (<c>ReleaseRaster</c> @0x1123180) is
    /// <c>void(void* data, int w, int h, int channelsOrBpp, int level0Size, int container)</c>. Both pointer/int
    /// only, so one signature serves both ABIs.</summary>
    public const string AurGetCompressedImage = "AurGetCompressedImage", AurReleaseRaster = "AurReleaseRaster";
    /// <summary>CX-54: the raster (TGA) slot beside them, live target <c>CAuroraInterface::GetRaster</c> @0x1123080:
    /// <c>uchar*(const char* name, int* w, int* h, int* bytesPerPixel)</c>. Released through
    /// <see cref="AurReleaseRaster"/> with level-0 size 0.</summary>
    public const string AurGetRaster = "AurGetRaster";
    /// <summary>CX-52 step 0: <c>AurGetImageWrapper</c> @0x484cc0, the one caller of every decoder slot
    /// (resource read + decode for one texture build). Timed only. Out params: w, h, bpp, level-0 size,
    /// container, format, alpha mean; the int is the TXI-derived allow-compressed flag.</summary>
    [Fn("IntPtr(IntPtr name, int* w, int* h, int* bpp, int* size, int* container, int* format, float* alpha, int allowCompressed)")]
    public const string AurGetImageWrapper = "_Z18AurGetImageWrapperPKcPiS1_S1_S1_PN6Aurora22AuroraContainerFormatsEPNS2_20AuroraTextureFormatsEPfi";
    /// <summary>CX-52 step 2: the whole TGA decode -- header walk, RLE expand, R/B swap -- runs in this virtual,
    /// inside <c>CRes::Demand</c>'s servicing (@0x1154a60, Windows RVA 0x1A4730, identical). 1 parsed, 0 refused.</summary>
    [Fn("int(IntPtr res)")]
    public const string TgaServiced = "_ZN7CResTGA18OnResourceServicedEv";
    /// <summary>CX-52 step 2: the PLT composite (@0x4b6580, Windows RVA 0x71360, identical): a fresh
    /// <c>new[](w*h*4)</c> where each 2-byte {palette index, layer} pixel becomes the 4-byte entry
    /// <c>palette + (layerRows[layer] &lt;&lt; 10) + index*4</c>. The palette height is unused.</summary>
    [Fn("IntPtr(byte* px, int w, int h, byte* palette, int paletteHeight, int* layerRows, int layers)")]
    public const string ImageDecode = "_Z11ImageDecodePhiiS_iPii";
    /// <summary>A PLT's raw raster, two bytes a pixel <c>{palette index, layer}</c>, bottom-up (@0x485040, Windows rva
    /// 0xe5ef0): a synchronous private copy through <c>CAuroraInterface::GetLayeredRaster</c>, freed through
    /// <see cref="AurReleaseLayeredRaster"/>. Writes <c>*bpp = 2</c> unconditionally.</summary>
    /// <summary>Static functions the Linux client has no symbol for (nm lists nothing): resolved from the running
    /// build's <c>lva</c> line (<see cref="ClientBuilds.Lva"/>), as an <c>At()</c> symbol is from its <c>rva</c>
    /// line on Windows. The constant is the function's source name, a key, never looked up as an export.</summary>
    internal static readonly HashSet<string> LinuxAnchored = new() { NkScrollbarBehavior, NkDrawScrollbar, NkDoScrollbarV, NkDoScrollbarH,
                                                                     NkCmds, NkTexNull };
    /// <summary>nuklear's <c>nk_scrollbar_behavior</c> (static: Linux lva 0xdf5ba0, Windows rva 0xa6f320) -- the one
    /// function that turns input into a scrollbar's new offset: the thumb drag (and the one <c>nk_input</c> write,
    /// <c>clicked_pos</c>), the page clicks on the two empty rects, the wheel, the scroll keys. Called only by
    /// <c>nk_do_scrollbarv/h</c>, with every rect by pointer, so one signature fits both ABIs. <c>axis</c> 0 =
    /// vertical. CX-154, the scrollbar facts.</summary>
    [Fn("float(IntPtr state, IntPtr input, int hasScrolling, IntPtr track, IntPtr thumb, IntPtr empty0, IntPtr empty1, float offset, float target, float step, int axis)")]
    public const string NkScrollbarBehavior = "nk_scrollbar_behavior";
    /// <summary>nuklear's <c>nk_draw_scrollbar(out, state, style, &amp;track, &amp;thumb)</c> (static: Linux lva 0xddf6b0,
    /// Windows rva 0xa68af0): the pixels, from the thumb rect it is handed. Called only by <c>nk_do_scrollbarv/h</c>,
    /// right after <see cref="NkScrollbarBehavior"/>.</summary>
    [Fn("void(IntPtr output, uint state, IntPtr style, IntPtr track, IntPtr thumb)")]
    public const string NkDrawScrollbar = "nk_draw_scrollbar";
    /// <summary>nuklear's <c>nk_do_scrollbarv</c> / <c>nk_do_scrollbarh</c> (static: Linux lva 0xdf6100 / 0xdf65c0, Windows rva
    /// 0xa65d10 / 0xa65700): one whole scrollbar -- its buttons, <see cref="NkScrollbarBehavior"/>, <see cref="NkDrawScrollbar"/> --
    /// returning the new offset. Callers: <c>nk_panel_end</c> (a window or group) and <c>nk_do_edit</c>, which use only
    /// the return. No <c>[Fn]</c>: the track rect is by value on Linux (xmm0/xmm1) and by pointer on Windows (r8), so
    /// <c>NuiScrollbarHide</c> declares both. CX-154, the scrollbar hide facts.</summary>
    public const string NkDoScrollbarV = "nk_do_scrollbarv", NkDoScrollbarH = "nk_do_scrollbarh";
    [Fn("byte*(byte* name, int* w, int* h, int* bpp)")]
    public const string AurGetLayeredRasterWrapper = "_Z26AurGetLayeredRasterWrapperPKcPiS1_S1_";
    /// <summary>DATA slot <c>void(*)(void* data, int w, int h)</c> that frees a <see cref="AurGetLayeredRasterWrapper"/>
    /// raster (<c>AurTextureFreeImage</c> @0x4850f0 calls it for a PLT). The master palette the PLT composite indexes:
    /// <c>paletteimage</c> (<c>uchar*</c>, 256 RGBA entries a row, palette k owning rows k*256..; REALLOCATED by
    /// <c>AurSetPalette</c> mid-session, so re-read at every use) and <c>paletteheight</c> (its row count).</summary>
    public const string AurReleaseLayeredRaster = "AurReleaseLayeredRaster", PaletteImage = "paletteimage", PaletteHeight = "paletteheight";
    /// <summary><c>nk_loadimg(char const*, ushort)</c> @0xe1a820 (Windows rva 0xa95e30) -- the one funnel under every NUI
    /// image kind that turns a resref string into a <c>shared_ptr&lt;NkImage&gt;</c>, returned BY VALUE: a free function, so
    /// the hidden return slot is first on both ABIs. Type 0xFFFF probes six image types; any other type goes straight
    /// to <see cref="ExoResManGet"/>. Returns the slot, as the ABI has it in rax. CX-155.</summary>
    [Fn("IntPtr(IntPtr ret, byte* resref, ushort type)")]
    public const string NkLoadImg = "_Z10nk_loadimgPKct";
    /// <summary><c>shared_ptr&lt;DataBlock&gt; CExoResMan::Get(CResRef const&amp;, ushort)</c> @0x53e620 (Windows rva 0x18f920):
    /// a resource's raw bytes, null when the key table has no entry. A member returning by value, so the hidden slot
    /// sits (ret, this, ...) on Itanium and (this, ret, ...) on Win64 (#456); no <c>[Fn]</c>, because no one signature
    /// describes it -- <c>NuiItemIcons</c> declares both orders. Cold: 14 callers, none per frame.</summary>
    public const string ExoResManGet = "_ZN10CExoResMan3GetERK7CResReft";
    /// <summary><c>static shared_ptr&lt;DataBlock&gt; DataBlock::FromBase64(std::string const&amp;)</c> @0x61f0e0 (Windows rva
    /// 0x309040): decodes and ADOPTS the decoded buffer, null on a bad string. Static, so the return slot is first on
    /// both ABIs. The string is only read.</summary>
    [Fn("IntPtr(IntPtr ret, IntPtr str)")]
    public const string DataBlockFromBase64 = "_ZN9DataBlock10FromBase64ERKNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEEE";
    /// <summary><c>CNWCMessage::CreateItem(uint baseItem, ushort* models, uchar* colors, uchar* partColors, char gender,
    /// uchar visualEffect, ObjectVisualTransformData const&amp;)</c> @0x7577f0 (Windows rva 0x769130, anchored: its own .pdata
    /// entry, and the only function citing "gifp" / "gi_armor01"): a whole client item, icon and ground model, from the
    /// appearance an engine store sends (<c>HandleServerToPlayerItemAppearance</c> @0x79b730 decodes exactly these
    /// buffers). Never reads its <c>this</c>; returns a <c>CNWCItem*</c> registered nowhere, which its caller deletes.</summary>
    [Fn("IntPtr(IntPtr self, uint baseItem, ushort* models, byte* colors, byte* partColors, sbyte gender, byte visualEffect, IntPtr visualTransform)")]
    public const string NwcCreateItem = "_ZN11CNWCMessage10CreateItemEjPtPhS1_chRK25ObjectVisualTransformData";
    /// <summary>DATA: <c>ObjectVisualTransformData::s_IdentityVisualTransformData</c>, the constructed identity transform
    /// (its std::map is a live empty tree), for <see cref="NwcCreateItem"/>'s last argument.</summary>
    public const string IdentityVisualTransform = "_ZN25ObjectVisualTransformData29s_IdentityVisualTransformDataE";
    /// <summary><c>kiwi::impl::SolverImpl::substitute(Symbol const&amp;, Row const&amp;)</c> @0x85b890 (Windows rva 0x3458e0): replaces
    /// a symbol by a row in every tableau row, the objective and the artificial row -- the full row walk that makes a
    /// NUI layout solve quadratic (CX-154 §2). Never throws. Its callers are addConstraint,
    /// addWithArtificialVariable, optimize (Linux only), dualOptimize and removeConstraint, all by call.</summary>
    [Fn("void(IntPtr self, IntPtr symbol, IntPtr row)")]
    public const string KiwiSubstitute = "_ZN4kiwi4impl10SolverImpl10substituteERKNS0_6SymbolERKNS0_3RowE";
    /// <summary><c>kiwi::impl::SolverImpl::optimize(Row const&amp;)</c> @0x85cc50 (Windows rva 0x3433d0): the primal pivot loop.
    /// MSVC inlined <see cref="KiwiSubstitute"/> into it, so on Windows its pivots bypass the substitute hook; GCC calls
    /// it. Throws InternalSolverError("The objective is unbounded."), so it is hooked only through native/cxthunk.c's
    /// C wrapper (<c>KiwiOptimizeHook</c>), never by a managed handler.</summary>
    [Fn("void(IntPtr self, IntPtr objective)")]
    public const string KiwiOptimize = "_ZN4kiwi4impl10SolverImpl8optimizeERKNS0_3RowE";
    /// <summary><c>CrashReporter_Linux::ReportCrashToUser(char const*, char const*)</c> @0xf075b0 (Windows
    /// <c>CrashReporter_Windows</c>'s): the crash handler's last call (vtable +0x40), after the report file is closed, with
    /// the dialog's title and text. Runs inside a signal handler, so it is hooked only through native/cxthunk.c
    /// (<c>CrashReport</c>), never by a managed handler.</summary>
    [Fn("void(IntPtr self, byte* title, byte* text)")]
    public const string ReportCrashToUser = "_ZN19CrashReporter_Linux17ReportCrashToUserEPKcS1_";
    /// <summary><c>CrashHandlerExpose(char const* key, char const* value, bool append)</c> @0xf056c0: files a
    /// <c>key = 'value'</c> line under the crash report's <c>=== Props</c> (the engine's own: GL_VENDOR, GL_RENDERER).
    /// Copies both strings.</summary>
    [Fn("void(byte* key, byte* value, bool append)")]
    public const string CrashHandlerExpose = "_Z18CrashHandlerExposePKcS0_b";
    /// <summary><c>kiwi::impl::SolverImpl::createRow(Constraint const&amp;, Tag&amp;)</c> @0x85eb40 (Windows rva 0x340e00):
    /// the constraint rewritten over the tableau's parametric symbols, returned as a <c>unique_ptr&lt;Row&gt;</c> by value
    /// (Itanium <c>(sret, this, constraint, tag)</c>, Win64 <c>(this, sret, constraint, tag)</c>, so hooked per OS by
    /// <c>NuiLayoutErrors</c>). Allocates but throws nothing of kiwi's; <c>addConstraint</c> calls it first and throws
    /// UnsatisfiableConstraint straight after when the row is all dummies with a nonzero constant.</summary>
    [Fn("sret(kiwi__impl__SolverImpl this, IntPtr constraint, IntPtr tag)")]
    public const string KiwiCreateRow = "_ZN4kiwi4impl10SolverImpl9createRowERKNS_10ConstraintERNS1_3TagE";
    /// <summary><c>Nui::JSON::JsonWindow::HandleError(char const* tag, char const* message)</c> @0x86d480 (Windows rva
    /// 0x746650): vanilla's only NUI error surface. Retitles the window "Error " + tag and replaces its whole layout with
    /// one text widget holding a copy of message. Called by Initialize and Render inside their catch and by Update after
    /// it (CX-153 §1).</summary>
    [Fn("void(IntPtr self, IntPtr tag, IntPtr message)")]
    public const string NuiHandleError = "_ZN3Nui4JSON10JsonWindow11HandleErrorEPKcS3_";
    /// <summary><c>Nui::Layout::LayoutGroup</c>'s and <c>SingleGroup</c>'s vtables: the only layout classes that embed a kiwi
    /// solver. Identities, not hooks; an object's vptr is the symbol + <see cref="EngineLayout.VtblSymbolHeader"/>.</summary>
    public const string NuiLayoutGroupVtbl = "_ZTVN3Nui6Layout11LayoutGroupE";
    /// <inheritdoc cref="NuiLayoutGroupVtbl"/>
    public const string NuiSingleGroupVtbl = "_ZTVN3Nui6Layout11SingleGroupE";
    /// <summary><c>Nui::Layout::Span</c>'s vtable: a JSON <c>row</c> or <c>col</c>. An identity for naming elements.</summary>
    public const string NuiSpanVtbl = "_ZTVN3Nui6Layout4SpanE";
    /// <summary>The two pre-dispatch calls of the world scene render, inside the AI update: the BSP
    /// management (every gob, three passes, the frustum traverse) and the draw-bucket build (the sole
    /// writer of the gob model matrix Alloy draws with). Timed only; see
    /// CX-141.</summary>
    [Fn("long(Scene scene)")]
    public const string ManageSceneBSP         = "_Z14ManageSceneBSPP5Scene";
    [Fn("long(Scene this)")]
    public const string AddPartsToDrawBuckets  = "_ZN5Scene21AddPartsToDrawBucketsEv";
    // ---- CX-103/104/105: the scene pipeline's own symbols ----
    /// <summary>CX-103: <c>LerpFloat::Lerped() const</c> @0x5f8fc0 -- the engine's interpolator, evaluated
    /// against its own <c>CWorldTimer</c> clock. Alloy calls it on <see cref="EngineLayout.GobScaleLerp"/> for
    /// each ancestor of an attached gob, which is the scale term the gob loop of
    /// <c>Scene::AddPartsToDrawBuckets</c> @0x4751d0 folds in and <c>Part::GetWorld</c> does not.
    /// Windows: name-exported <c>?Lerped@LerpFloat@@QEBAMXZ</c>.</summary>
    [Fn("float(LerpFloat this)")]
    public const string LerpFloatLerped = "_ZNK9LerpFloat6LerpedEv";
    /// <summary>CX-105: <c>Gob::SetLODModel()</c> @0x43e6a0 -- rotates a gob's three part-tree roots
    /// (<c>gob+0x90/0x98/0xa0</c>) and their three <c>Model*</c> (<c>gob+0x68/0x70/0x78</c>) so the ACTIVE pair
    /// is at <c>+0x90</c>/<c>+0x68</c>, writes the level to <see cref="EngineLayout.GobLodLevel"/>, calls
    /// <c>Gob::Reset</c> and sets <see cref="EngineLayout.GobGeomDirty"/>. Its only caller is the BSP visitor
    /// @0x481000, once per in-view gob per frame, which is why <see cref="CX.Alloy.SceneWalk"/> had to
    /// enumerate all three roots. Alloy hooks it to CHOOSE the level (it writes
    /// <see cref="EngineLayout.GobLodDist2"/>, calls the original so the engine does its own swap, then
    /// restores the field) and to learn the swap edge. Windows: name-exported
    /// <c>?SetLODModel@Gob@@QEAAXXZ</c>.</summary>
    [Fn("void(Gob this)")]
    public const string GobSetLODModel = "_ZN3Gob11SetLODModelEv";
    /// <summary>CX-105: <c>g_bEnableLOD</c> @0x18aa148 (int) and <c>g_fLODScaleFactor</c> @0x18aa14c (float) --
    /// the two globals <c>Gob::SetLODModel</c> reads. Alloy's chooser honours both, so the console variables
    /// keep meaning what they say. Data symbols; read only.</summary>
    public const string EnableLod = "g_bEnableLOD";
    /// <inheritdoc cref="EnableLod"/>
    public const string LodScaleFactor = "g_fLODScaleFactor";

    /// <summary>The BSP visit callback's per-frame tile-part lists that AddPartsToDrawBuckets' six flat loops
    /// consume. meshbuckets/fadebuckets are <c>List&lt;Bucket&gt;</c> objects (CExoArrayList: items at +0,
    /// count at +8, alloc at +0xc) whose 16-byte {items, count} pairs live on the heap, one per texture id
    /// (ClearBuckets @0x47f4e0). The shadow and cap lists are single CExoArrayLists (count at +8). The
    /// .bss neighbours are the texture globals and the AurGet*Image function pointers: a write past the
    /// 16-byte object is a decoder crash. staticbeambucket is left alone: Beams.Enroll re-reads bucket 15.</summary>
    public const string MeshBuckets = "meshbuckets", FadeBuckets = "fadebuckets", CapBuckets = "capbuckets",
                        StaticShadowBucket = "staticshadowbucket", MeshShadowBucket = "meshshadowbucket";
    [Fn("ulong(IntPtr resman, IntPtr res)")]
    public const string ResManRelease      = "_ZN10CExoResMan7ReleaseEP4CRes";
    [Fn("int(IntPtr resman, IntPtr res, byte flag)")]
    public const string ResManReleaseObj   = "_ZN10CExoResMan16ReleaseResObjectEP4CResb";

    // #346: the engine's OWN resource read, which is how a stock .shd must be fetched. CResHelper builds a
    // typed CRes subclass per resource type and the engine reads it back through that type -- registering a
    // bare CRes under someone else's resref is a type confusion that crashes the engine, not a leak. See
    // ShaderResource.Override's warning. GLRender's shader-source loader @0x4d2940 calls exactly these three,
    // in this order, and this replays that: AurResGet -> AurResGetData(size) -> AurResFree(handle).
    /// <summary><c>AurResGet(const char* name, const char* ext, int* size, bool)</c> @0x4332c0 -- opens a
    /// resource and pushes it on the <c>Resources</c> stack. Returns the handle and writes the byte count.
    /// The engine passes the extension WITH its dot (".shd") and true for the bool.</summary>
    [Fn("IntPtr(byte* name, byte* ext, int* size, byte push)")]
    public const string AurResGet = "_Z9AurResGetPKcS0_Pib";
    /// <summary><c>AurResGetData(int nBytes)</c> @0x432fe0 -- read that many bytes from the resource on TOP
    /// of the stack into its own buffer and return it. Stack-based, so the trio runs back to back with no
    /// engine call in between, exactly as @0x4d2940 does it.</summary>
    [Fn("byte*(int handle)")]
    public const string AurResGetData = "_Z13AurResGetDatai";
    /// <summary><c>AurResFree(void* handle)</c> @0x4335d0 -- pop and release. Called only on the path where
    /// the handle AND the size are non-zero; the engine's own loader does not free on the failure path, so
    /// neither do we.</summary>
    [Fn("void(IntPtr handle)")]
    public const string AurResFree = "_Z10AurResFreePv";
    [Fn("void(IntPtr res)")]
    public const string CResCtor           = "_ZN4CResC1Ev";

    // ---- CX-55 step 0: the model load funnel, measured. All four are name-exported on nwmain.exe.
    //      Verified from the Linux decompilation: FindModel -> BinaryFindModel (the name cache) and, on a
    //      miss, IODispatcher::ReadSync -> AurResGet(name, ".mdl", 0, true) -> the AurGetResource slot,
    //      which is CAuroraInterface::GetResource. ----
    /// <summary><c>FindModel(const char*)</c> @0x44c2a0 -- the ONE funnel every model load goes through, and
    /// the engine's name cache: <c>BinaryFindModel</c> over the sorted global <c>Models</c> answers a hit with
    /// no I/O, a miss goes to <see cref="IoReadSync"/>. Recurses from inside the parse for a supermodel, so
    /// only the outermost call is one model asked for.</summary>
    [Fn("IntPtr(byte* name)")]
    public const string FindModel = "_Z9FindModelPKc";
    /// <summary><c>IODispatcher::ReadSync(const char*)</c> @0x4bf670 -- the miss path: one model file read plus
    /// its parse, returning a <c>MaxTree*</c>. It calls <c>AurResGet(name, ".mdl", 0, true)</c>, then branches on
    /// the first byte of the 4 <c>AurResGetDataBytes</c> hands back (the compare @0x4bf6e3): 0 = the binary
    /// reader (nm 0x4bee60, which recurses here for the supermodel and demands every texture slot), anything
    /// else = <c>AurResFree</c> and the ASCII <c>dofile</c>, which reads the file a second time.</summary>
    [Fn("IntPtr(IntPtr dispatcher, byte* name)")]
    public const string IoReadSync = "_ZN12IODispatcher8ReadSyncEPKc";
    /// <summary><c>CAuroraInterface::GetResource(const char*, const char*, int*)</c> @0x1123fd0 -- STATIC (the
    /// MSVC export says <c>SA</c>), the <c>AurGetResource</c> function-pointer slot <c>AurResGet</c> calls. This is
    /// the whole file read of a model. Its return is the resource bytes themselves: <c>AurResGet</c> stores it at
    /// <c>OpenResource+0x58</c> and <c>AurResGetDataBytes</c> hands that same pointer back, so byte 0 of what this
    /// returns IS the binary/ASCII discriminator -- the cheapest place to read it.</summary>
    [Fn("byte*(byte* name, byte* ext, int* size)")]
    public const string AuroraGetResource = "_ZN16CAuroraInterface11GetResourceEPKcS1_Pi";
    /// <summary><c>SpawnPartsForTile(...)</c> @0x463450 -- the half of <c>Scene::SpawnTile</c> that loads a tile's
    /// models. Bracketed by <c>ModelZonesService</c> so a model read can be told apart from a placeable's or a
    /// creature's. Returns <c>Gob*</c> (the MSVC export carries the return type Itanium drops).</summary>
    [Fn("IntPtr(byte* resref, IntPtr pos, IntPtr rot, int id, int orientation, IntPtr parts)")]
    public const string SpawnPartsForTile = "_Z17SpawnPartsForTilePKcRK6VectorRK10QuaternioniiR4ListIP4PartE";
    // ---- CX-55 step 1: the model file read. AurResGet's function-pointer slot AurGetResource @0x18b9aa0 IS
    //      CAuroraInterface::GetResource, so every .mdl the engine reads funnels through it, and AurResFree's
    //      AurReleaseResource slot is its pair. The pointer it answers with carries a 10-byte engine header
    //      (CAuroraModel::LoadModel @0x1134df0 writes restype 0x7d2 at ptr-10 and the owning CAuroraModel* at
    //      ptr-8, the same on Windows rva 0xf4a70), which ReleaseResource switches on -- so a buffer of ours
    //      leaves that header ZERO, and the engine's release is a no-op for it if our chain ever misses. ----
    /// <summary><c>CAuroraInterface::ReleaseResource(void*)</c> @0x1123b90 -- STATIC, the matching edge that
    /// <c>AurResFree</c> @0x4335d0 always reaches (<c>AurReleaseResource(res+0x68)</c>, the base pointer
    /// <see cref="AuroraGetResource"/> returned). Reads <c>*(ushort*)(ptr-10)</c> to pick the resource kind.</summary>
    [Fn("void(IntPtr data)")]
    public const string AuroraReleaseResource = "_ZN16CAuroraInterface15ReleaseResourceEPv";
    /// <summary><c>FindAnimation(Model*, const char*)</c> @0x497c30 -- the engine's own animation lookup, and
    /// CX-113's oracle: it walks the supermodel chain (<c>Model+0xa8</c>) answering the first model whose own
    /// <c>Animation*</c> array holds the name, then falls back to <c>"default"</c> in the TERMINAL model only.
    /// A free function, so the <c>Animation*</c> it returns identifies the owning model as well as the
    /// animation -- each model owns its own array. Windows rva 0x50f80 tail-recurses to the same answer.</summary>
    [Fn("IntPtr(IntPtr model, byte* name)")]
    public const string FindAnimation = "_Z13FindAnimationP5ModelPKc";
    /// <summary><c>FindAnimScale(Model*, Animation*)</c> @0x499b10 -- the product of <c>Model+0xcc</c> over
    /// every model in the chain BELOW the one whose array holds <paramref name="anim"/> (found by pointer
    /// identity). Takes no string, so CX can call it directly with the pointer
    /// <see cref="FindAnimation"/> answered. Windows rva 0x50ed0, same loads.</summary>
    [Fn("float(IntPtr model, IntPtr anim)")]
    public const string FindAnimScale = "_Z13FindAnimScaleP5ModelP9Animation";
    /// <summary><c>List&lt;Model*&gt; Models</c> @0x18da6f0 -- the engine's sorted model table itself, which
    /// <c>BinaryInsertModel</c> @0x4481b0 and <c>BinarySearchModel</c> @0x448110 maintain: the items pointer at
    /// <see cref="EngineLayout.ListItems"/> and the count at <see cref="EngineLayout.ListItemCount"/>. Windows
    /// exports it as data too, with the same two offsets -- rva 0x98ba0 loads the items from the symbol itself
    /// and rva 0x99240 the count from symbol+8. CX-113's live verify enumerates it.</summary>
    public const string ModelTable = "Models";
    /// <summary><c>BinaryFindModel(const char*)</c> @0x448190 -- the model name cache, and nothing else:
    /// <c>BinarySearchModel</c> @0x448110 <c>strcasecmp</c>s <c>Model+0x10</c> over the sorted global
    /// <c>Models</c> @0x18da6f0 and reads no file. <c>FindModel</c> @0x44c2a0 calls it first and only loads on
    /// a zero, which is exactly what makes it the "the engine already has this one" test.</summary>
    [Fn("IntPtr(byte* name)")]
    public const string BinaryFindModel = "_Z15BinaryFindModelPKc";
    /// <summary><c>CNWTileSet::GetTileData(int)</c> @0x5e10d0 -- the i-th <c>CNWTileData</c> of a loaded tileset
    /// (records of 0xb8 from <c>tileset+0x58</c> / Windows <c>+0x60</c>). Logs an EXOWARNING past the count, so
    /// callers stop at <see cref="EngineLayout.TileSetTileCount"/>.</summary>
    [Fn("IntPtr(CNWTileSet this, int id)")]
    public const string TileSetGetTileData = "_ZN10CNWTileSet11GetTileDataEi";
    /// <summary><c>CNWTileData::GetModelResRef()</c> @0x632a00 -- the tile's model resref, a <c>CResRef</c> (17
    /// bytes) returned BY VALUE out of <c>tiledata+0x10</c>; identical offset on Windows rva 0x2c5ea0, and the
    /// hidden buffer is ordered per OS by the <c>sret</c> declaration (#456).</summary>
    [Fn("sret(CNWTileData this)")]
    public const string TileDataGetModelResRef = "_ZN11CNWTileData14GetModelResRefEv";
    /// <summary><c>CNWTile::SetID(int)</c> @0x632550 -- a one-store setter (<c>tile+0x14 = id</c>), and the
    /// tile read-ahead's anchor: <c>CNWCArea::LoadArea</c> @0x80ee11 (Windows @0x1407dd382) calls it with the
    /// tile id it has just read off the message, once per record, before the record's model load. Four callers
    /// in all, none hot: this loop, <c>CNWCArea::SetTile</c> @0x708e60, and the two server-side twins.</summary>
    [Fn("void(IntPtr tile, int id)")]
    public const string NwTileSetID = "_ZN7CNWTile5SetIDEi";

    // ---- CX-55 step 3: the ASCII -> binary compile. The compiler is the ENGINE's own, reader and writer
    //      paired in one source file (CX-55); nothing of ours writes an MDL. ----
    /// <summary><c>compilemodelsinternal(bool asciiOnly, const char* name)</c> @0x448410 -- the guarded wrapper
    /// the <c>compilemodel</c> console command uses, and the whole compile in one call: it walks the global
    /// <c>Models</c> for a <c>strncasecmp</c> match on <c>Model+0x10</c>, refuses an already-binary model
    /// (<c>Model+0x80 != 0</c>) when <paramref name="asciiOnly"/>, refuses one carrying an uninitialized
    /// skinmesh (<c>modelHasUninitializedSkinmesh</c> @0x44e030, i.e. a skin node whose
    /// <c>InternalPostProcess</c> never ran -- serialising one would write empty vertex blocks), and otherwise
    /// calls <c>IODispatcher::Write(MaxTree*, const char*)</c> @0x4bc730 with a null name, so the file lands at
    /// <c>&lt;MODELCOMPILER alias&gt;/&lt;Model+0x10&gt;.mdl</c>.
    /// <para>Read-only on the model: the sizer @0x4b99c0 and filler @0x4bbfc0 walk the node tree and write only
    /// their own accumulator and output blocks, which is why the engine's own <c>compileloadedmodels</c> may run
    /// over every live model mid-session. The writer emits the <c>{0, modelDataSize, rawDataSize}</c> wrapper
    /// then the two blocks (@0x4bc560), which is exactly what the binary reader @0x4bee60 copies back out.</para>
    /// <para>Returns a pointer to a 200-byte static message buffer: <c>"Successfully compiled model %s.\n"</c>,
    /// <c>"Model compile error: ..."</c>, or <c>"Success: Compiled 0 models.\n"</c> when the name matched
    /// nothing.</para></summary>
    [Fn("byte*(byte asciiOnly, byte* name)")]
    public const string CompileModels = "_Z21compilemodelsinternalbPKc";
    /// <summary><c>CExoAliasList::GetAliasPath(CExoString const&amp;, int) const</c> @0x516db0 -- the alias table's
    /// raw directory for an alias name, which is where <c>IODispatcher::Write</c> reads <c>MODELCOMPILER</c>
    /// from. NOT an sret call: the MSVC mangling says <c>AEBVCExoString@@</c>, a const REFERENCE into the alias
    /// entry (@0x5179d0 returns <c>entry+0x10</c>), and the disassembly of the call inside <c>Write</c>
    /// @0x4bc7bf passes <c>(this, &amp;name, 0)</c> and reads <c>{char* at +0, int len at +8}</c> off the
    /// returned pointer. Nothing to free.</summary>
    [Fn("IntPtr(CExoAliasList this, IntPtr name, int mode)")]
    public const string AliasGetAliasPath = "_ZNK13CExoAliasList12GetAliasPathERK10CExoStringi";

    // ---- CX-689..CX-693: the radial menu seam (CX-162). The whole
    //      CRadialMenu surface is name-exported on Windows; the client roots are identified by vtable compare
    //      (the _ZTV / vftable rows below), never called by name. ----
    /// <summary><c>CRadialMenu::ActivateInternal()</c> @0x585010 -- the GUI-side half of <c>Activate</c>: LoadGraphics,
    /// CGuiPanel::Activate, static_nMenuDepth++, CaptureMouse, SetModal. Skipped by RadialService: at this point
    /// <c>this</c> is a fully built root (top ring filled, XY at +0x85c/+0x860) and no engine modality has been
    /// engaged, so there is nothing to release. <c>(this) -> void</c>.</summary>
    [Fn("void(CRadialMenu this)")]
    public const string RadialActivateInternal = "_ZN11CRadialMenu16ActivateInternalEv";
    /// <summary><c>CRadialMenu::CreateSubMenu(int slot)</c> @0x584790 -- materializes slot <c>i</c>'s child through its
    /// creator callback under the <see cref="GuiCurrentEventObject"/> bracket, stores it at m_pSubMenu[i] and bumps
    /// the child's refcount. Returns 1 if the child exists after the call. <c>(this, int) -> int</c>.</summary>
    [Fn("int(CRadialMenu this, int slot)")]
    public const string RadialCreateSubMenu = "_ZN11CRadialMenu13CreateSubMenuEi";
    /// <summary><c>CRadialMenu::RecursiveAutoDelete()</c> @0x583680 -- the teardown vanilla's <c>Deactivate</c> runs on
    /// the root: recurses into every materialized child, then virtual-deletes self when m_bAutoDelete and the
    /// refcount reaches 0. CX calls it on the root at dropdown close. <c>(this) -> int</c>.</summary>
    [Fn("int(CRadialMenu this)")]
    public const string RadialRecursiveAutoDelete = "_ZN11CRadialMenu19RecursiveAutoDeleteEv";
    /// <summary><c>CRadialMenu::static_nMenuDepth</c> (int) -- what <c>CGuiMan::InMenuMode</c> returns. Read-only
    /// sanity: stays 0 under the CX scheme because ActivateInternal never runs.</summary>
    public const string RadialMenuDepth = "_ZN11CRadialMenu17static_nMenuDepthE";
    /// <summary><c>CGuiObject::s_pCurrentEventObject</c> (CGuiObject*) -- the engine sets it to the menu around every
    /// creator and leaf callback (CreateSubMenu, HandleMenuAction) and restores it after; CX brackets its own
    /// invokes the same way.</summary>
    public const string GuiCurrentEventObject = "_ZN10CGuiObject21s_pCurrentEventObjectE";
    /// <summary><c>g_nCRadialMenu_ctorCounter</c> (int) -- MONOTONIC: the ctor @0x583460 is its only writer (one
    /// <c>addl $1</c> in the whole image; the dtor @0x583010 never touches it), so a delta counts constructions
    /// per open, not live instances (research §9.1). Exported by both builds.</summary>
    public const string RadialCtorCounter = "g_nCRadialMenu_ctorCounter";
    /// <summary>Root-kind vtables (Linux <c>_ZTV</c>; the object's vptr is the symbol + 0x10). The roots
    /// themselves are not exported on Windows -- the kind is a vptr compare against these, nothing is called.</summary>
    public const string VtCRadialMenu = "_ZTV11CRadialMenu";
    public const string VtCGuiCreatureMenu = "_ZTV16CGuiCreatureMenu";
    public const string VtCClientSpellMenu = "_ZTV16CClientSpellMenu";
    public const string VtCRadialSpellMenu = "_ZTV16CRadialSpellMenu";
    public const string VtCClientTrapMenu = "_ZTV15CClientTrapMenu";
    public const string VtCGuiItemMenu = "_ZTV12CGuiItemMenu";
    public const string VtCGuiPlaceableMenu = "_ZTV17CGuiPlaceableMenu";
    public const string VtCGuiDoorMenu = "_ZTV12CGuiDoorMenu";
    public const string VtCGuiQuickbarMenu = "_ZTV16CGuiQuickbarMenu";
    public const string VtCGuiChatWindowMenu = "_ZTV18CGuiChatWindowMenu";

    // --- CX-694 self pins: the leaf handlers CX may invoke on a stand-in of the SELF root's menu object, i.e. the
    //     actions the self menu fires that read only the menu's target fields (+0x878 / +0x8b0 on CGuiCreatureMenu,
    //     +0x900 / +0x90c / +0x910 on CClientSpellMenu) or nothing at all. A persisted `self:<key>` row names one of
    //     these and nothing else; Windows twins paired by their SetEntryx tuples and bodies (docs/windows-symbols.md).
    /// <summary><c>CGuiCreatureMenu::RadialMenuUseFeat(long)</c> @0x73d540: toggle feats go to <c>PerformToggleMode</c>,
    /// the rest to <c>PerformFeat(feat, +0x878, +0x8b0)</c> or the targeting cursor when the target is self and the feat
    /// needs one. <c>(this, long) -> void</c>.</summary>
    public const string CreatureMenuUseFeat = "_ZN16CGuiCreatureMenu17RadialMenuUseFeatEl";
    /// <summary><c>CGuiCreatureMenu::RadialMenuEmote(long)</c> @0x73dc90: PlayAnimation on the +0x878 creature (self when
    /// it is not one) + the emote's quickchat. <c>(this, long)</c>.</summary>
    public const string CreatureMenuEmote = "_ZN16CGuiCreatureMenu15RadialMenuEmoteEl";
    /// <summary><c>CGuiCreatureMenu::RadialMenuToggleMode(long)</c> @0x73dac0: <c>PerformToggleMode(param)</c>. <c>(this, long)</c>.</summary>
    public const string CreatureMenuToggleMode = "_ZN16CGuiCreatureMenu20RadialMenuToggleModeEl";
    /// <summary><c>CGuiCreatureMenu::RadialMenuQuickChat(long)</c> @0x73d8d0: <c>PerformQuickChat(param)</c>. <c>(this, long)</c>.</summary>
    public const string CreatureMenuQuickChat = "_ZN16CGuiCreatureMenu19RadialMenuQuickChatEl";
    /// <summary><c>CGuiCreatureMenu::RadialMenuUseSkill(long)</c> @0x73c030: <c>PerformSkill(skill, +0x878)</c>, 10 = the
    /// search toggle. <c>(this, long)</c>.</summary>
    public const string CreatureMenuUseSkill = "_ZN16CGuiCreatureMenu18RadialMenuUseSkillEl";
    /// <summary><c>CGuiCreatureMenu::RadialMenuRest(long)</c> @0x73efb0: the rest send; reads nothing off <c>this</c>. <c>(this, long)</c>.</summary>
    public const string CreatureMenuRest = "_ZN16CGuiCreatureMenu14RadialMenuRestEl";
    /// <summary><c>CGuiCreatureMenu::RadialMenuCharacterSheet(long)</c> @0x73bd70: <c>ToggleCharSheet</c>; reads nothing off <c>this</c>.</summary>
    public const string CreatureMenuCharacterSheet = "_ZN16CGuiCreatureMenu24RadialMenuCharacterSheetEl";
    /// <summary><c>CClientSpellMenu::RadialMenuCast(long)</c> @0x6efb10: unpacks the spell key and calls
    /// <c>PerformCastSpell(spell, +0x90c, +0x900, slot, level, meta)</c>. <c>(this, long)</c>.</summary>
    public const string SpellMenuCast = "_ZN16CClientSpellMenu14RadialMenuCastEl";
    /// <summary><c>CClientSpellMenu::RadialMenuCastSpellLikeAbility(long)</c> @0x6efbc0. <c>(this, long)</c>.</summary>
    public const string SpellMenuCastSla = "_ZN16CClientSpellMenu30RadialMenuCastSpellLikeAbilityEl";
    /// <summary><c>CClientSpellMenu::RadialMenuUseFeat(long)</c> @0x6f0050: the feat leaves of the cast tree. <c>(this, long)</c>.</summary>
    public const string SpellMenuUseFeat = "_ZN16CClientSpellMenu17RadialMenuUseFeatEl";
    /// <summary><c>CClientSpellMenu::TargetIsValid(uint spell)</c> @0x6f0450: <c>spells.2da TargetType</c> masked by the
    /// menu's +0x918 against the +0x90c object's type (self / creature / ground / item / door / placeable / trigger);
    /// what the spell creators grey a leaf on. <c>(this, uint) -> ushort</c>.</summary>
    [Fn("ushort(CClientSpellMenu this, uint spell)")]
    public const string SpellMenuTargetIsValid = "_ZN16CClientSpellMenu13TargetIsValidEj";
    /// <summary><c>CGuiCreatureMenu::CGuiCreatureMenu(CNWCCreature*, Vector)</c> @0x73f110: the self root's ctor -- target
    /// id, associate fields and portrait off the creature, the vector at +0x8b0, quickbutton null, m_bAutoDelete 1.
    /// Itanium <c>(xmm0 = x|y, xmm1 = z, this, creature)</c>; Win64 <c>(this, creature, Vector*)</c>.</summary>
    public const string CreatureMenuCtor = "_ZN16CGuiCreatureMenuC1EP12CNWCCreature6Vector";
    /// <summary><c>CClientSpellMenu::CClientSpellMenu(Vector, uint target, CGuiQuickButton*, int build)</c> @0x6f1650:
    /// stores the target fields (only when the player creature has stats -- guard before calling), builds the class
    /// ring when <c>build</c> is set. Itanium <c>(xmm0 = x|y, xmm1 = z, this, target, btn, build)</c>; Win64
    /// <c>(this, Vector*, target, btn, build)</c>.</summary>
    public const string SpellMenuCtor = "_ZN16CClientSpellMenuC1E6VectorjP15CGuiQuickButtoni";

    // --- CX-693: item-root composite icons. The icon subclasses are told apart by vptr; the PLT layer's texture is
    //     found by the same (name, scheme) hash the engine's own ReplaceTexturePLT keys it under.
    /// <summary><c>AurTextureFind(const char* name, const char* pltName, int* scheme, int count, bool byName)</c>
    /// @0x488b90 -- the resman's texture lookup WITHOUT the create. With a pltName and <c>byName == false</c> it takes
    /// the hashed path (<c>AurTextureCalculateHashID(pltName, count, scheme)</c>, then <c>+0xd4 == count</c>, a
    /// strcasecmp on <c>+0xa8</c> and the scheme ints at <c>+0x48</c>) -- exactly how <c>AurTextureGet</c> finds the
    /// texture <c>Material::ReplaceTexturePLT</c> @0x4c7a10 made for a tinted PLT, so a per-frame call with the icon's
    /// own (resref, scheme) hands back that live borrowed pointer or null, and never creates a second one. Same body
    /// on Windows (rva 0xe7560). Returns a BORROWED <c>CAurTexture*</c> -- see <see cref="AurTex"/>.</summary>
    [Fn("IntPtr(byte* name, byte* plt, int* scheme, int count, byte byName)")]
    public const string AurTextureFind = "_Z14AurTextureFindPKcS0_Piib";
    /// <summary>Icon-kind vtables (Linux <c>_ZTV</c>; the object's vptr is the symbol + 0x10). Any vptr not listed is
    /// the plain kind (<c>CGuiIcon</c>, <c>CSpellIcon</c>: every subclass keeps the resref at +0x8); these three change
    /// what a draw is: one PLT texture, three plain textures, up to six PLT textures.</summary>
    public const string VtCLayeredIcon = "_ZTV12CLayeredIcon";
    /// <inheritdoc cref="VtCLayeredIcon"/>
    public const string VtCCompositeIcon = "_ZTV14CCompositeIcon";
    /// <inheritdoc cref="VtCLayeredIcon"/>
    public const string VtCArmorIcon = "_ZTV10CArmorIcon";
    /// <summary><c>CGuiIcon::GetIconModel()</c> @0x58cfa0 -- the icon's model, built on the first call (the virtual
    /// <c>CreateCopyIconModel</c>; a PLT icon's runs <c>ReplaceTexturePLT</c>, which mints the tinted texture) and kept at
    /// <c>+0x30</c> until <c>~CGuiIcon</c> purges it. The stock grid builds models only for its resident page's icon
    /// COPIES (<c>CGuiMultiInvbox::AddItem</c> @0x692f70; <c>RemoveItem</c> deletes the copy), so a PLT item on any other
    /// page has no texture until something builds one; calling this on the item's own icon does. Name-exported on both.</summary>
    [Fn("IntPtr(IntPtr icon)")]
    public const string GuiIconGetIconModel = "_ZN8CGuiIcon12GetIconModelEv";

    /// <summary><c>CExoResMan::AddManifest(Hash::SHA1 const&amp;, unsigned priority)</c> @0x53bae0 -- the one place a
    /// NWSync manifest becomes a search source. It copies the hash's hex text out of <c>sha1+0x15</c> and calls
    /// <c>AddKeyTable(priority, hex, 5, 0, null)</c>, so the kind-5 key table's NAME is that hex (which is what
    /// <c>ServiceFromManifest</c> @0x53a640 turns back into a <c>Hash::SHA1</c>). Hooked, not called: it is the
    /// main-thread edge that tells the reader which manifest is live. <c>(this, SHA1 const*, uint) -&gt; int</c>;
    /// no by-value crossing on either ABI.</summary>
    [Fn("int(IntPtr resman, IntPtr sha1, uint priority)")]
    public const string ResManAddManifest = "_ZN10CExoResMan11AddManifestERKN4Hash4SHA1Ej";
    /// <summary><c>CExoResMan::RemoveManifest(Hash::SHA1 const&amp;)</c> @0x537440 -- the matching edge, same hex read
    /// then <c>RemoveKeyTable(hex, 5, 1)</c>. <c>(this, SHA1 const*) -&gt; int</c>.</summary>
    [Fn("int(IntPtr resman, IntPtr sha1)")]
    public const string ResManRemoveManifest = "_ZN10CExoResMan14RemoveManifestERKN4Hash4SHA1E";
    /// <summary><c>sqlite3_open_v2(path, &amp;db, flags, vfs)</c>. Alloy opens the NWSync DBs READ-ONLY on its own
    /// connection, on the worker thread that uses it. The client links SQLite 3.45.3 (THREADSAFE=1) statically and
    /// name-exports its C API on both binaries; zstd is unexported on Windows and anchored (see the <c>_win</c> rows).</summary>
    [Fn("int(byte* path, IntPtr* db, int flags, byte* vfs)")]
    public const string Sqlite3OpenV2 = "sqlite3_open_v2";
    /// <inheritdoc cref="Sqlite3OpenV2"/>
    [Fn("int(IntPtr db)")]
    public const string Sqlite3CloseV2 = "sqlite3_close_v2";
    /// <inheritdoc cref="Sqlite3OpenV2"/>
    [Fn("int(IntPtr db, byte* sql, int nByte, IntPtr* stmt, byte** tail)")]
    public const string Sqlite3PrepareV2 = "sqlite3_prepare_v2";
    /// <summary><c>sqlite3_exec</c> -- used ONLY by the self-check, to build its own fixture DBs.</summary>
    [Fn("int(IntPtr db, byte* sql, IntPtr cb, IntPtr arg, byte** err)")]
    public const string Sqlite3Exec = "sqlite3_exec";
    /// <summary><c>sqlite3_busy_timeout(db, ms)</c>. The engine writes these DBs from its own task threads, so the
    /// reader sets this explicitly to 0: a locked DB is a counted miss, never a stalled worker.</summary>
    [Fn("int(IntPtr db, int ms)")]
    public const string Sqlite3BusyTimeout = "sqlite3_busy_timeout";
    /// <inheritdoc cref="Sqlite3OpenV2"/>
    [Fn("int(IntPtr stmt, int idx, byte* text, int n, IntPtr dtor)")]
    public const string Sqlite3BindText = "sqlite3_bind_text";
    /// <inheritdoc cref="Sqlite3OpenV2"/>
    [Fn("int(IntPtr stmt, int idx, int v)")]
    public const string Sqlite3BindInt = "sqlite3_bind_int";
    /// <inheritdoc cref="Sqlite3OpenV2"/>
    [Fn("int(IntPtr stmt)")]
    public const string Sqlite3Step = "sqlite3_step";
    /// <inheritdoc cref="Sqlite3OpenV2"/>
    [Fn("int(IntPtr stmt)")]
    public const string Sqlite3Reset = "sqlite3_reset";
    /// <inheritdoc cref="Sqlite3OpenV2"/>
    [Fn("int(IntPtr stmt)")]
    public const string Sqlite3Finalize = "sqlite3_finalize";
    /// <inheritdoc cref="Sqlite3OpenV2"/>
    [Fn("byte*(IntPtr stmt, int col)")]
    public const string Sqlite3ColumnText = "sqlite3_column_text";
    /// <inheritdoc cref="Sqlite3OpenV2"/>
    [Fn("void*(IntPtr stmt, int col)")]
    public const string Sqlite3ColumnBlob = "sqlite3_column_blob";
    /// <inheritdoc cref="Sqlite3OpenV2"/>
    [Fn("int(IntPtr stmt, int col)")]
    public const string Sqlite3ColumnBytes = "sqlite3_column_bytes";
    /// <summary><c>sqlite3_threadsafe()</c> -- the compile-time mode, asserted once. The binaries carry
    /// <c>THREADSAFE=1</c> (serialized), so a private connection per thread is sound.</summary>
    [Fn("int()")]
    public const string Sqlite3ThreadSafe = "sqlite3_threadsafe";
    /// <inheritdoc cref="Sqlite3OpenV2"/>
    [Fn("byte*(IntPtr db)")]
    public const string Sqlite3ErrMsg = "sqlite3_errmsg";
    /// <summary><c>ZSTD_createDCtx()</c> -- one context per reader, never freed, exactly as the engine's own
    /// <c>ZstdCompressionProvider::DecompressBuffer</c> @0x649270 keeps one in thread-local storage.</summary>
    [Fn("IntPtr()")]
    public const string ZstdCreateDCtx = "ZSTD_createDCtx";
    /// <summary><c>ZSTD_decompressDCtx(dctx, dst, dstCap, src, srcSize)</c> -- returns the produced size, or an
    /// error code far larger than any real size, so comparing against the expected length is the check.</summary>
    [Fn("nuint(IntPtr dctx, void* dst, nuint dstCap, void* src, nuint srcSize)")]
    public const string ZstdDecompressDCtx = "ZSTD_decompressDCtx";

    // ---- CX-57: the exosound seams (docs/audio.md, CX-135). The whole of exosound is a
    //      shared-engine library, so every one of these is name-exported on Windows. ----
    /// <summary><c>CExoSoundInternal::PlaySourceOn3DVoice(CExoSoundSourceInternal*, C3DVoice*)</c> @0x577dc0 -- decodes
    /// the WAV, fills the voice's AL buffer and source, and ends in <c>alSourcePlay</c>. The Steam Audio path
    /// brackets it: the source's authored near/far, variance and group ride to the play shim on this thread.</summary>
    [Fn("void(CExoSoundInternal this, CExoSoundSourceInternal source, C3DVoice voice)")]
    public const string SoundPlayOn3DVoice = "_ZN17CExoSoundInternal19PlaySourceOn3DVoiceEP23CExoSoundSourceInternalP8C3DVoice";
    /// <summary><c>CExoSoundInternal::PlaySourceOn2DVoice(CExoSoundSourceInternal*, C2DVoice*)</c> @0x5780e0 -- the
    /// same shape for a non-positional source. A placed sound object that is not Positional plays here (group 2,
    /// Looping_AreaWide_Ambients, when it loops) and, being 2D, is never range-culled: it plays area-wide at
    /// constant volume. It still carries the object's position and its authored min/max distance.</summary>
    [Fn("void(CExoSoundInternal this, CExoSoundSourceInternal source, C2DVoice voice)")]
    public const string SoundPlayOn2DVoice = "_ZN17CExoSoundInternal19PlaySourceOn2DVoiceEP23CExoSoundSourceInternalP8C2DVoice";
    /// <summary><c>CExoSoundSourceInternal::SetPosition(Vector)</c> @0x576860 -- stores the world position and, for a
    /// playing 3D voice, forwards it to AL with z NEGATED (<c>xorps</c> with the sign mask @0x5768b4) while the play
    /// path forwards it as is: the stored vector is the one to trust.</summary>
        [Fn("void(CExoSoundSourceInternal this, VectorArg pos)")]
    public const string SoundSourceSetPosition = "_ZN23CExoSoundSourceInternal11SetPositionE6Vector";
    /// <summary><c>CExoSound::SetEnvironment(unsigned char)</c> @0x574ea0 -- the area's <c>EnvAudio</c> row of
    /// soundeax.2da, once per area from <c>CNWCArea::StartSounds</c> (its only caller); the OpenAL backend drops it.</summary>
    [Fn("void(IntPtr sound, byte row)")]
    public const string SoundSetEnvironment = "_ZN9CExoSound14SetEnvironmentEh";
    /// <summary><c>CExoSoundInternal::Initialize(CExoString, unsigned char n2D, unsigned char n3D)</c> @0x57c4c0 -- the
    /// voice budget (16 + 16 from <c>InitializeSoundOptions</c>, clamped to 8..64 inside). The by-value
    /// <c>CExoString</c> crosses as a pointer on both ABIs (Itanium: non-trivial, so by invisible reference; Win64:
    /// over 8 bytes, so a caller-made copy the callee destroys) -- forwarded untouched, never read.</summary>
    [Fn("int(IntPtr sound, IntPtr provider, byte n2d, byte n3d)")]
    public const string SoundInitialize = "_ZN17CExoSoundInternal10InitializeE10CExoStringhh";
    /// <summary><c>CExoStreamingSoundSource::FadeAndStop(unsigned int ms)</c> @0x574450 -- every music/ambient track
    /// change goes through it (PlayMusic(0), PlayAmbientSound(0), battle, truncated, day/night): the fade-out that the
    /// next track waits on, since <c>PlayX(1)</c> only starts once <c>IsPlaying()</c> is 0. Main thread.</summary>
    [Fn("void(CExoStreamingSoundSource this, uint ms)")]
    public const string StreamFadeAndStop = "_ZN24CExoStreamingSoundSource11FadeAndStopEj";
    /// <summary><c>CExoStreamingSoundSource::CExoStreamingSoundSource(Type)</c> @0x574240 -- the 8-byte handle; it
    /// makes its Internal through <c>Reset(type)</c>. CNWCAmbientSound builds both of its streams as Type 0.</summary>
    [Fn("void(CExoStreamingSoundSource this, int type)")]
    public const string StreamCtor = "_ZN24CExoStreamingSoundSourceC1ENS_4TypeE";
    /// <summary><c>CExoStreamingSoundSource::Reset(Type)</c> @0x5741c0 -- deletes the Internal (and the whole file it
    /// holds) and makes a virgin one.</summary>
    [Fn("void(CExoStreamingSoundSource this, int type)")]
    public const string StreamReset = "_ZN24CExoStreamingSoundSource5ResetENS_4TypeE";
    /// <summary><c>CExoStreamingSoundSource::StartFade(FadeType, unsigned int ms, u8 from, u8 to)</c> @0x5744c0 --
    /// type 1 ramps and keeps playing; <c>CExoSoundInternal::Render</c> ticks it on the main thread.</summary>
    [Fn("void(CExoStreamingSoundSource this, int type, uint ms, byte from, byte to)")]
    public const string StreamStartFade = "_ZN24CExoStreamingSoundSource9StartFadeENS_8FadeTypeEjhh";
    /// <summary><c>CExoStreamingSoundSource::IsPlaying()</c> @0x5742b0 -- nonzero while its AL source plays,
    /// including through a fade-out.</summary>
    [Fn("int(CExoStreamingSoundSource this)")]
    public const string StreamIsPlaying = "_ZN24CExoStreamingSoundSource9IsPlayingEv";
    /// <summary><c>CExoStreamingSoundSource::Stop()</c> @0x574430.</summary>
    [Fn("void(CExoStreamingSoundSource this)")]
    public const string StreamStop = "_ZN24CExoStreamingSoundSource4StopEv";
    /// <summary><c>CExoStreamingSoundSource::GetVolume()</c> @0x574410 -- the set volume (0..127), not the faded one.</summary>
    [Fn("byte(CExoStreamingSoundSource this)")]
    public const string StreamGetVolume = "_ZN24CExoStreamingSoundSource9GetVolumeEv";
    /// <summary><c>CExoStreamingSoundSource::SetLooping(int)</c> @0x5743a0 -- per Internal, and CNWCAmbientSound sets
    /// it once at construction (music 0, ambient 1), so a swapped-in Internal needs it again.</summary>
    [Fn("void(CExoStreamingSoundSource this, int loop)")]
    public const string StreamSetLooping = "_ZN24CExoStreamingSoundSource10SetLoopingEi";

    // ---- the CX inventory screen (CX-146; §10 records this pass). Every client
    //      GUI / CNWCMessage class here is unexported on Windows -> At() + an `rva` line; only
    //      CNWBaseItemArray (shared engine) is a name export. Each RVA below was read from the Windows body,
    //      not pattern-matched: the addresses in the decompiled bodies cross-check each other. ----
    /// <summary><c>CPanelInventory::SetInventoryVisible(int show, int notifyServer, uint oid)</c> @0x68e760 (Windows
    /// rva 0x857cd0) -- the one funnel every inventory open and close goes through (keybind, quick button, radial,
    /// server push, store/levelup teardown). Returns <c>void</c>; no by-value crossing, so one signature for both
    /// ABIs. <c>show == 0</c> tears the container sub-panel down first (MSVC INLINED
    /// <c>SetContainerGUIVisible(0,1,0,INVALID)</c> there, including its <c>GuiContainer_Status(0)</c> send);
    /// <c>show != 0</c> registers the popup, forces the panel visible, resolves <c>INVALID -&gt;
    /// GetPlayerCreatureId()</c> into <see cref="EngineLayout.InvShownOid"/> and calls
    /// <see cref="InvSetCharacterData"/>. The <c>notifyServer</c> tail is the one thing a skip must still do:
    /// <see cref="SendGuiInvStatus"/>(visible, shownOid), or the server stops pushing repository updates.</summary>
    [Fn("void(CPanelInventory this, int show, int notifyServer, uint oid)")]
    public const string InvSetInventoryVisible = "_ZN15CPanelInventory19SetInventoryVisibleEiij";
    /// <summary><c>CPanelInventory::SetCharacterData(uint oid)</c> @0x68d810 (Windows rva 0x856fa0) -- fills the five
    /// labels (name, gold, weight, max weight, AC) from the creature. Safe to call on a HIDDEN panel with the
    /// player's own oid: it reads no visibility field, registers no popup, sends no message and allocates no widget;
    /// every <c>CGuiLabel</c>'s <c>m_pString</c> exists from the label's constructor
    /// (<c>CGuiLabel::CGuiLabel(int)</c> @0x591fd0 calls <c>NewCAurString</c>), so the null-<c>m_pString</c>
    /// EXOWARNING path is unreachable on a constructed panel. It DOES write
    /// <see cref="EngineLayout.InvIsMine"/> = 1, and with a FOREIGN oid it permanently strips the panel
    /// (<c>RemoveFromPanel</c> on the gold label and gold button, the three GuiObjTypes flipped) -- so only ever
    /// pass the player's own id. A null creature only logs.</summary>
    [Fn("void(CPanelInventory this, uint oid)")]
    public const string InvSetCharacterData = "_ZN15CPanelInventory16SetCharacterDataEj";
    /// <summary><c>GameCameraHitCheck(int x, int y, Vector* out, CHitInfo* info)</c> @0x63a270 (Windows rva 0x6d7f50) -- the
    /// world under a GUI-space point (SDL x, <c>GuiManVpH - y</c>) through the module camera: what
    /// <c>CGuiDragSource::HandleLMouseUp</c> asks when a drag ends over no GUI object. Sets <c>info+0x20 = 1</c>, then
    /// the camera's pick fills the kind (1 ground, 2 gob), the point and the gob. The return is unused (void on Windows).</summary>
    [Fn("void(int x, int y, IntPtr outPoint, CHitInfo info)")]
    public const string GameCameraHitCheck = "_Z18GameCameraHitCheckiiP6VectorP8CHitInfo";
    /// <summary><c>int GetGameobjectHit(CHitInfo&amp;, CNWCObject**)</c> @0x65dfd0 (Windows rva 0x6ed4e0) -- the game object
    /// behind a gob hit, and its type: 2 for ground (no object), 6 an item (the object is <c>item+0x48</c>, its
    /// <c>CNWCObject</c> base), 5 a creature, 9 a placeable, 0 nothing.</summary>
    [Fn("int(CHitInfo info, IntPtr* obj)")]
    public const string GetGameobjectHit = "_Z16GetGameobjectHitR8CHitInfoPP10CNWCObject";
    /// <summary><c>..._BarterStartBarter(uint creature, uint item)</c> @0x806400 (rva 0x7ba3f0), major 35 minor 1 -- a
    /// backpack item dropped on another creature (<c>CGuiInvbox::HandleDropAsSource</c>).</summary>
    [Fn("void(IntPtr msg, uint creature, uint item)")]
    public const string SendBarterStart = "_ZN11CNWCMessage35SendPlayerToServerBarterStartBarterEjj";
    /// <summary><c>..._Input_UseItem(uint item, uchar, uchar, uint target, Vector)</c> @0x7ffa10 (rva 0x7c0610), major 6
    /// minor 9 -- a potion dropped on the player (vanilla passes 0, 0xff, the creature, a zero vector).</summary>
    [Fn("void(IntPtr msg, uint item, byte a, byte b, uint target, VectorArg pos)")]
    public const string SendInputUseItem = "_ZN11CNWCMessage31SendPlayerToServerInput_UseItemEjhhj6Vector";
    /// <summary><c>..._Gold_TransferGold(uint target, Vector pos, int amount, int, uchar, uchar)</c> @0x804a50 (rva
    /// 0x7bdc70), major 8 -- minor 0x16 with the point when <c>target</c> is INVALID (the ground), else minor 0x0a to
    /// the object. The stock gold drop passes 0, 0, 0 for the tail.</summary>
    [Fn("void(IntPtr msg, uint target, VectorArg pos, int amount, int flag, byte a, byte b)")]
    public const string SendGoldTransfer = "_ZN11CNWCMessage35SendPlayerToServerGold_TransferGoldEj6Vectoriihh";
    /// <summary><c>CGuiQuickButton::SetButtonItem(uint item, int, int)</c> @0x6b1270 (Windows rva 0x867760) -- what the
    /// quick button's own drop handler does with an item dragged from the backpack or a slot: <c>(item, 0xff00ff, 0)</c>. It
    /// refuses with the engine's own feedback, pairs two dual-wieldable weapons, and sends
    /// <c>SendButtonUpdateToServer</c> itself.</summary>
    [Fn("void(IntPtr button, uint item, int prop, int force)")]
    public const string QuickButtonSetItem = "_ZN15CGuiQuickButton13SetButtonItemEjii";
    /// <summary><c>CNWCItem::PlayDropSound()</c> @0x7cd960 (Windows rva 0x83ea00) -- the quick-button drop's second step.</summary>
    [Fn("void(CNWCItem this)")]
    public const string ItemPlayDropSound = "_ZN8CNWCItem13PlayDropSoundEv";
    /// <summary><c>CClientExoApp::GetInventoryPanel()</c> @0x637c90 (Windows rva 0x6abbe0) -- MY inventory panel
    /// (a one-line forward to <c>CClientExoAppInternal::GetInventoryPanel</c>, Linux @0x63cad0 / rva 0x6d8290).
    /// Someone else's is <c>GetOtherInventoryPanel</c>, which CX does not take over. The player's creature id needs
    /// no symbol -- it is <see cref="EngineLayout.InternalPlayerCreatureId"/>, the very field
    /// <c>CClientExoApp::GetPlayerCreatureId</c> @0x6377a0 (rva 0x6ac1c0) returns.</summary>
    [Fn("CPanelInventory(CClientExoApp this)")]
    public const string GetInventoryPanel = "_ZN13CClientExoApp17GetInventoryPanelEv";
    /// <summary><c>CGuiMultiPageMultiInvbox::AddItem(uint oid, int x, int globalY, int ghost)</c> @0x6943f0 (Windows
    /// rva 0x87f970) -- the seam that sees EVERY backpack item the server sends, on every page: the page test
    /// (<c>page != globalY / rows</c>) and the <c>globalY % rows</c> fold are INSIDE the callee, so a chain hook
    /// observes the off-page items the widget itself drops. <c>globalY</c> is page-major
    /// (<c>page * rows + localY</c>). Windows takes the 5th argument on the stack (Ghidra's callee signature drops
    /// it; the call sites pass it) -- the declared parameter list is the Linux mangling's, <c>Ejiii</c>.</summary>
    [Fn("void(IntPtr box, uint oid, int x, int globalY, int ghost)")]
    public const string InvboxAddItem = "_ZN24CGuiMultiPageMultiInvbox7AddItemEjiii";
    /// <summary><c>CPanelInventory::SetContainerGUIVisible(int show, int notifyServer, int isItem, uint graphicOid)</c>
    /// @0x68e5d0 (Windows rva 0x8575f0, the same body standalone; MSVC also inlined its <c>show == 0</c> half into
    /// <c>SetInventoryVisible</c>). <c>show == 0</c> is the stock container teardown: container mode off, the grid emptied, the
    /// container sub-panel swapped back for "Equip", the container oid INVALID, and with <c>notifyServer</c>
    /// <c>SendPlayerToServerGuiContainer_Status(0)</c> (25 / 1), which the server answers by closing the container
    /// (<c>CNWSPlaceable::CloseInventory</c>: its OnClosed script and the close echo).</summary>
    [Fn("void(CPanelInventory this, int show, int notifyServer, int isItem, uint graphicOid)")]
    public const string InvSetContainerGuiVisible = "_ZN15CPanelInventory22SetContainerGUIVisibleEiiij";
    /// <summary><c>CPanelInventory::HandleContainerPageUp()</c> @0x68c520 (Windows rva 0x855d00) -- the container's
    /// right-arrow: empties the container grid, sends <c>SelectNextPage</c> (25 / 2) and steps the grid's page, wrapping to
    /// 0 past <see cref="EngineLayout.InvContainerPages"/> as the server's <c>SetNextPage</c> does. The server keeps the
    /// page; it streams only that one.</summary>
    [Fn("void(CPanelInventory this)")]
    public const string InvContainerPageUp = "_ZN15CPanelInventory21HandleContainerPageUpEv";
    /// <summary><c>CPanelInventory::HandleContainerPageDown()</c> @0x68c3d0 (Windows rva 0x855be0) -- the left arrow:
    /// <see cref="InvContainerPageUp"/> backwards (<c>SelectPreviousPage</c>, 25 / 3; page 0 wraps to the last).</summary>
    [Fn("void(CPanelInventory this)")]
    public const string InvContainerPageDown = "_ZN15CPanelInventory23HandleContainerPageDownEv";
    /// <summary><c>CPanelInventory::SetContainerPagenumVisible(int show, uchar count)</c> @0x68e100 (Windows rva 0x857ad0,
    /// the same body: count <c>+0x2f60</c>, page clamp <c>+0x2680</c>, <c>"%02d/%02d"</c>) -- stores the page count and
    /// clamps the grid's page to it. Three callers on each build: the container message's open, <c>(0, 1)</c> then
    /// <c>(1, count)</c> for a placeable (Linux nm @0x766f30, Windows rva 0x79ceb0), and <c>Update_GuiContainer</c>'s
    /// <c>'P'</c> (@0x79ee90 / rva 0x7b3790) -- which the server writes ahead of every resend of a multi-page container's
    /// page (<c>WriteRepositoryUpdate</c>, after <c>ClearContainer</c>), so it is the one "that page arrived" signal.</summary>
    [Fn("void(CPanelInventory this, int show, byte count)")]
    public const string InvContainerPagenum = "_ZN15CPanelInventory26SetContainerPagenumVisibleEih";
    /// <summary><c>CNWCItem::ActivateRadialMenu(int flags)</c> @0x7cb8f0 (Windows rva 0x83ce30) -- <c>new</c>s a
    /// <c>CGuiItemMenu</c> (0x888 Linux / 0x808 Windows) and hands it <c>(flags, this-&gt;oid, null)</c>.
    /// <c>flags</c> picks the leaf set in <c>CGuiItemMenu::ActivateRadialMenu</c> @0x7d3720: bit 1 (2) = equipped
    /// (<c>InitializeEquippedItemMenu</c>), bit 4 (0x10) = quickbar, bit 0 (1) = possessed
    /// (<c>InitializePossessedItemMenu</c>), none of them = on the ground; bit 3 (8) drops the "put into" submenu and
    /// is forwarded to the possessed builder. Vanilla passes <c>widget+0x168 | 1</c> for a backpack cell
    /// (<c>CGuiMultiInvbox::HandleRMouseDown</c> @0x692420) and <c>widget+0x168 | 3</c> for an equip slot
    /// (<c>CGuiInvbox::HandleRMouseDown</c> @0x6904e0); <c>+0x168</c> is <b>0</b> for both -- the ctor @0x6905e0
    /// zeroes it and nothing on the inventory path writes it -- so the values are literally <b>1</b> (backpack) and
    /// <b>3</b> (equipped). The menu places itself: <c>CRadialMenu::Activate</c> fills
    /// <see cref="EngineLayout.RadialMenuX"/>/<c>Y</c> from <c>CGuiMan::GetMousePos</c>, so no position is
    /// passed.</summary>
    [Fn("void(CNWCItem this, int flags)")]
    public const string ItemActivateRadialMenu = "_ZN8CNWCItem18ActivateRadialMenuEi";
    /// <summary><c>CNWCItem::GetName(int useCustom)</c> @0x7ce810 (Windows rva 0x83d8a0) -- returns a
    /// <c>CExoString</c> BY VALUE, so <b>sret</b>: Itanium <c>(out, this, int)</c>, Win64 <c>(this, out, int)</c>
    /// (measured -- the Windows body's parameters are exactly <c>(rcx this, rdx out, r8d useCustom)</c>). Body:
    /// <c>useCustom == 0</c>, or a non-empty custom name on an identified item, yields the custom name
    /// (<see cref="EngineLayout.ItemCustomName"/>); otherwise TLK[<c>baseItem-&gt;nameStrRef</c>], prefixed with
    /// TLK[0x15ac] when the item is unidentified. The buffer is the CALLER's and engine-heap: zero it first, free
    /// it with <see cref="ExoStringDtor"/>.</summary>
    [Fn("sret(CNWCItem this, int useCustom)")]
    public const string ItemGetName = "_ZN8CNWCItem7GetNameEi";
    /// <summary><c>CNWBaseItemArray::GetBaseItem(int baseItem) const</c> @0x11a9440 -- bounds-checked
    /// <c>records + baseItem * 200</c>, null when out of range. Name-exported on Windows (shared engine). The array
    /// itself is <see cref="EngineLayout.RulesBaseItems"/> off <see cref="Rules"/>, which is how every client body
    /// reaches it (<c>*(g_pRules + 0x20)</c> on both builds).</summary>
    [Fn("CNWBaseItem(IntPtr array, int baseItem)")]
    public const string GetBaseItem = "_ZNK16CNWBaseItemArray11GetBaseItemEi";

    // The senders. All are CNWCMessage instance methods (`this` = CClientExoApp::GetNWCMessage()), all take
    // plain scalars, all return void, none crosses a class by value -- directly callable, no new opcode.
    // Parameter types are the Linux mangling's; the Windows bodies were read and every (major, minor) pair
    // below is the immediate at that body's SendPlayerToServerMessage (rva 0x7c25c0) call.
    /// <summary><c>CNWCMessage::SendPlayerToServerInventory_Equip(uint item, uint slotBitmask, uint targetCreature)</c>
    /// @0x800c20 (Windows rva 0x7c0d70), major 12 minor 1.</summary>
    [Fn("void(IntPtr msg, uint item, uint slotBitmask, uint target)")]
    public const string SendInvEquip = "_ZN11CNWCMessage33SendPlayerToServerInventory_EquipEjjj";
    /// <summary><c>..._Unequip(uint item, uchar x, uchar globalY, uint target)</c> @0x800f80 (rva 0x7c1100),
    /// major 12 minor 7. <c>globalY = page * rows + localY</c>.</summary>
    [Fn("void(IntPtr msg, uint item, byte x, byte globalY, uint target)")]
    public const string SendInvUnequip = "_ZN11CNWCMessage35SendPlayerToServerInventory_UnequipEjhhj";
    /// <summary><c>..._RepositoryMove(uint item, uchar x, uchar globalY, uint target)</c> @0x800dc0 (rva 0x7c1030),
    /// major 12 minor 9 -- a move inside the backpack or between backpack and container.</summary>
    [Fn("void(IntPtr msg, uint item, byte x, byte globalY, uint target)")]
    public const string SendInvRepositoryMove = "_ZN11CNWCMessage42SendPlayerToServerInventory_RepositoryMoveEjhhj";
    /// <summary><c>..._Drop(uint item, float x, float y, float z)</c> @0x800b20 (rva 0x7c0c90), major 12 minor 3.
    /// The world point for "at my feet" is the player creature's own position,
    /// <see cref="EngineLayout.ObjPosition"/> (<c>new CNWCObject(playerCreature).Position</c>).</summary>
    [Fn("void(IntPtr msg, uint item, float x, float y, float z)")]
    public const string SendInvDrop = "_ZN11CNWCMessage32SendPlayerToServerInventory_DropEjfff";
    /// <summary><c>CNWCMessage::SendPlayerToServerGuiInventory_Status(int visible, uint oid)</c> @0x7ff3f0
    /// (rva 0x7be110), major 13 minor 1 -- what <c>SetInventoryVisible</c>'s notify tail sends. A CX panel MUST keep
    /// sending it or <c>ComputeInventoryUpdateRequired</c> server-side stops pushing repository updates.</summary>
    [Fn("void(IntPtr msg, int visible, uint oid)")]
    public const string SendGuiInvStatus = "_ZN11CNWCMessage37SendPlayerToServerGuiInventory_StatusEij";
    /// <summary><c>..._SelectPanel(uchar page, int isMine)</c> @0x7ff340 (rva 0x7be060), major 13 minor 2 -- the page
    /// change; the other half of the same gate.</summary>
    [Fn("void(IntPtr msg, byte page, int isMine)")]
    public const string SendGuiInvSelectPanel = "_ZN11CNWCMessage42SendPlayerToServerGuiInventory_SelectPanelEhi";
    /// <summary><c>..._UseObject(uint oid, int, int)</c> @0x7ffe50 (rva 0x7c07c0), major 6 minor 11 -- double-click on
    /// a usable item (<see cref="EngineLayout.BaseItemUsable"/> non-zero), and also how the X button closes a
    /// container. Vanilla passes 0 for both ints.</summary>
    [Fn("void(IntPtr msg, uint oid, int a, int b)")]
    public const string SendInputUseObject = "_ZN11CNWCMessage33SendPlayerToServerInput_UseObjectEjii";
    /// <summary><c>..._SplitItem(uint item, int count)</c> @0x804900 (rva 0x7c0050), major 6 minor 23. The count goes
    /// on the wire as a WORD, so it is a 16-bit quantity in an <c>int</c> parameter.</summary>
    [Fn("void(IntPtr msg, uint item, int count)")]
    public const string SendInputSplitItem = "_ZN11CNWCMessage33SendPlayerToServerInput_SplitItemEji";
    /// <summary><c>..._MergeItem(uint dst, uint src)</c> @0x804850 (rva 0x7bfd90), major 6 minor 22.</summary>
    [Fn("void(IntPtr msg, uint dst, uint src)")]
    public const string SendInputMergeItem = "_ZN11CNWCMessage33SendPlayerToServerInput_MergeItemEjj";

    // ---- CX-98: the engine's debug string layer ----
    /// <summary><c>AurPrintString(const char* text, int col, int row, float r, float g, float b, float a,
    /// const char* font)</c> @0x435f30 -- the ONE draw primitive of the <c>AurPostString</c> /
    /// <c>AurPrintString</c> layer. Its three callers are <c>PostedString::Post</c> @0x4362d0 (every
    /// <c>AurRenderStrings</c> entry), the console cursor in <c>Update</c> @0x430030, and
    /// <c>CGuiCGModelViewer::Update</c>; inside it are the viewport read, the 10x14 character grid, the
    /// <c>StringGob</c> build and <c>StringGob::Render</c>. Swallowing it is the whole of CX-98: what is left
    /// running in <c>AurRenderStrings</c> is the list walk, the expiry (which lives in <c>Post</c>, not here)
    /// and two CPU-side state caches. A free function -- no <c>this</c>, no by-value return.</summary>
    [Fn("void(byte* text, int col, int row, float r, float g, float b, float a, byte* font)")]
    public const string AurPrintString = "_Z14AurPrintStringPKciiffffS0_";

    // ---- CX-109/110 ----
    /// <summary><c>Camera::RenderScene()</c> @0x4a7920 -- the per-view loop: viewport and upscale, the
    /// perspective/ortho and view transforms, the world-space frustum planes, the <c>CurrentCamera</c> latch,
    /// the GUI depth range and its once-per-run depth clear, and then <c>Scene::Render</c> through the Scene's
    /// vtable +0x20. Every view goes through it -- the world view and each <c>CGuiPanel</c> 3D view -- and
    /// every call is VIRTUAL (the binary has no direct caller), so the hook sits on the function itself and
    /// tells world from GUI by <c>Scene+0x188</c>. <see cref="CX.Alloy.AlloyCamera"/> measures each view here
    /// and then calls the original, whose remaining outputs are the BSP frustum, the model matrix stack and
    /// the GUI views' own state (CX-109 §1).</summary>
    [Fn("void(Camera this)")]
    public const string CameraRenderScene = "_ZN6Camera11RenderSceneEv";
    /// <summary><c>CNWTileSet::GetEnvMapResRef()</c> @0x5e10a0 -- the tileset's environment-map name, a
    /// 17-byte <c>CResRef</c> copied straight out of <c>CNWTileSet+0x32</c> and returned BY VALUE, so the
    /// hidden buffer is ordered per OS by the <c>sret</c> declaration (#456). <c>Scene::Render</c> @0x4762d0
    /// is its only caller; <see cref="CX.Alloy.AlloyEnvMap"/> is the second.</summary>
    [Fn("sret(CNWTileSet this)")]
    public const string TileSetGetEnvMapResRef = "_ZN10CNWTileSet15GetEnvMapResRefEv";
    /// <summary><c>Camera::UpscaleViewport(float* screenViewport, float* cameraRect, float* out)</c> @0x5a44b0
    /// -- the GUI-scale rewrite of a panel view's viewport. It is a pure QUERY (it writes nothing but its
    /// 10-float <c>out</c>: rect at 0..3, a mode at 4, two offsets at 5/6, two centring terms at 7/8 and the
    /// scale at 9) and it is a table of ~20 named <c>sceneGUI_*</c> panels behind
    /// <c>|CAurora::GetGUIScale() - 1| &gt;= 0.09</c>, so it answers false for the WORLD view on every client.
    /// <see cref="CX.Alloy.AlloyCamera"/> calls it for a GUI view rather than porting the table: the table is
    /// the engine's layout policy, not render arithmetic, and what Alloy needs from it is the rect its own
    /// projection and view matrices are built at (CX-109 §1).</summary>
    [Fn("byte(Camera this, float* screenViewport, float* cameraRect, float* into)")]
    public const string CameraUpscaleViewport = "_ZN6Camera15UpscaleViewportEPfS0_S0_";
    /// <summary><c>WindManager::updateTimer(float dt)</c> @0x491200 -- the engine's own wind clock: advance the
    /// blend, swap and refill the noise field pair at the period, and age the point sources out. Called from
    /// <c>Scene::Render</c> @0x476520 and nowhere else, in the WORLD branch only, once per world frame,
    /// whatever the renderer is doing. <see cref="CX.Alloy.AlloyWind"/> hooks it because that is what makes
    /// its own clock advance on a vanilla frame too: the gusts it collects come from
    /// <see cref="AddPointSourceWind"/>, which the engine calls whether or not Alloy draws
    /// (CX-109 §5).</summary>
    [Fn("void(WindManager this, float dt)")]
    public const string WindUpdateTimer = "_ZN11WindManager11updateTimerEf";

    // ---- CX-107 behaviours ----
    // The 13 CAurBehavior::Control implementations Alloy replaces, plus the four leaf engine calls the
    // ports make. Every one is NAME-EXPORTED on nwmain.exe (`?Control@<Class>@@UEAAHM@Z`), so CX-107
    // needed no RVA anchoring at all; the MSVC mangling also settles the declared signature as
    // `virtual int Control(float)` for all thirteen. CX-107 §3, §6.
    // CameraFollowControl is declared above (#536) and is the one of the thirteen that already existed.

    /// <summary><c>CAurBehavior::Control(float)</c> @0x4a0510 -- the base solve: walk the dependency list
    /// backwards calling each dependency's <c>CAurObject::Animate(dt)</c>, prune dead ones, then answer
    /// "am I still the owner's behaviour". Nothing constructs a bare <c>CAurBehavior</c>; hooked anyway so
    /// that if one ever exists, the port runs.</summary>
    [Fn("int(IntPtr self, float dt)")]
    public const string BehaviorControl = "_ZN12CAurBehavior7ControlEf";
    /// <summary><c>CAurBehaviorAttach::Control(float)</c> @0x4a2810 -- snaps the owner's position and
    /// orientation to the parent part's world transform (<c>Part::GetWorld</c> straight into
    /// <c>Gob+0xa8</c>/<c>+0xb4</c>), or detaches/deletes the owner when the parent has died. The base of
    /// the whole VFX-on-a-hand family.</summary>
    [Fn("int(IntPtr self, float dt)")]
    public const string AttachControl = "_ZN18CAurBehaviorAttach7ControlEf";
    /// <summary><c>CAurBehaviorAttachTranslate::Control(float)</c> @0x4a2960 -- attach, then offset the
    /// owner by <c>orientation * m_vTranslation</c> (<c>+0x70</c>).</summary>
    [Fn("int(IntPtr self, float dt)")]
    public const string AttachTranslateControl = "_ZN27CAurBehaviorAttachTranslate7ControlEf";
    /// <summary><c>CAurBehaviorAttachRotate::Control(float)</c> @0x4a2ad0 -- attach, then post-multiply the
    /// owner's orientation by <c>YawPitchRoll(m_vRotation.z, .x, .y)</c> (<c>+0x70</c>).</summary>
    [Fn("int(IntPtr self, float dt)")]
    public const string AttachRotateControl = "_ZN24CAurBehaviorAttachRotate7ControlEf";
    /// <summary><c>CAurBehaviorAttachNoOrientation::Control(float)</c> @0x4a2bd0 -- attach position only:
    /// the orientation is read before the snap and written back after.</summary>
    [Fn("int(IntPtr self, float dt)")]
    public const string AttachNoOrientationControl = "_ZN31CAurBehaviorAttachNoOrientation7ControlEf";
    /// <summary><c>CAurBehaviorAttachGUIScale::Control(float)</c> @0x4a2c80 -- attach, then divide the
    /// owner's x and y (not z) by <c>CAurora::GetGUIScale()</c>.</summary>
    [Fn("int(IntPtr self, float dt)")]
    public const string AttachGuiScaleControl = "_ZN26CAurBehaviorAttachGUIScale7ControlEf";
    /// <summary><c>CAurBehaviorCrossSceneAttach::Control(float)</c> @0x58b5d0 -- the GUI's attach
    /// (<c>GuiAurAttachToObject</c>): the same snap with the base walk flattened to the single parent, and
    /// it always returns 1.</summary>
    [Fn("int(IntPtr self, float dt)")]
    public const string CrossSceneAttachControl = "_ZN28CAurBehaviorCrossSceneAttach7ControlEf";
    /// <summary><c>CAurBehaviorCrossSceneAttachWithOffset::Control(float)</c> @0x58c280 -- as above plus a
    /// constant world offset at <c>+0x84</c>.</summary>
    [Fn("int(IntPtr self, float dt)")]
    public const string CrossSceneAttachOffsetControl = "_ZN38CAurBehaviorCrossSceneAttachWithOffset7ControlEf";
    /// <summary><c>CAurBehaviorSubwindow::Control(float)</c> @0x58b720 -- maps two dummy parts of a GUI
    /// panel to the owning camera's viewport (<c>Camera::SetViewPort</c>). The GUI-scale surface of the 3D
    /// panels (character appearance, the chat portraits).</summary>
    [Fn("int(IntPtr self, float dt)")]
    public const string SubwindowControl = "_ZN21CAurBehaviorSubwindow7ControlEf";
    /// <summary><c>CAurBehaviorNavigate::Control(float)</c> @0x4a0660 -- the <c>navigate</c> console
    /// function's free-fly: yaw/pitch rates and forward/strafe speeds, each consumed and cleared per
    /// frame.</summary>
    [Fn("int(IntPtr self, float dt)")]
    public const string NavigateControl = "_ZN20CAurBehaviorNavigate7ControlEf";
    /// <summary><c>CAurBehaviorCameraLooseFollow::Control(float)</c> @0x4a1010 -- UNREACHABLE in this
    /// build (no constructor call site anywhere) and unfinished: its body posts on-screen debug strings
    /// every frame. Hooked so the stand-down is visible, not ported.</summary>
    [Fn("int(IntPtr self, float dt)")]
    public const string CameraLooseFollowControl = "_ZN29CAurBehaviorCameraLooseFollow7ControlEf";
    /// <summary><c>CAurBehaviorCameraOnAStick::Control(float)</c> @0x4a0c70 -- UNREACHABLE in this build
    /// (no constructor call site); ported, but the port can never be exercised.</summary>
    [Fn("int(IntPtr self, float dt)")]
    public const string CameraOnAStickControl = "_ZN26CAurBehaviorCameraOnAStick7ControlEf";

    /// <summary><c>CExoArrayList&lt;SafePointer&lt;CAurObject&gt;*&gt;::Add</c> @0x49f3f0 -- appends one
    /// weak reference to an object's back-reference list (<c>CAurObject+0x08</c>), growing 0 -&gt; 16 -&gt;
    /// 2n through the engine's allocator. Every solver registers a stack <c>SafePointer</c> on its owner so
    /// that an owner destroyed inside a dependency's <c>Animate</c> is survivable; the port does the same,
    /// through the engine's own list so the allocation is never the GC's.</summary>
    [Fn("void(IntPtr list, IntPtr safePointer)")]
    public const string SafePtrListAdd = "_ZN13CExoArrayListIP11SafePointerI10CAurObjectEE3AddES3_";
    /// <summary><c>Camera::SetViewPort(float x, float y, float w, float h)</c> @0x4a38a0 -- what the
    /// subwindow solver writes. Virtual, but the only implementation in either image, so it is called by
    /// symbol rather than through a vtable slot nobody has measured.</summary>
    [Fn("void(IntPtr camera, float x, float y, float w, float h)")]
    public const string CameraSetViewPort = "_ZN6Camera11SetViewPortEffff";
    /// <summary><c>Scene::GetObject(char const*)</c> @0x45e880 -- the by-name lookup the subwindow solver
    /// uses to find <c>cameraGUI_pnl_chr_genm1</c>. Virtual with one implementation in either image.</summary>
    [Fn("IntPtr(IntPtr scene, byte* name)")]
    public const string SceneGetObject = "_ZN5Scene9GetObjectEPKc";

    // ---- CX-120 seam-scene: the three scene-shaped free functions CX answers for ----

    /// <summary><c>NewCAurScene(char const*)</c> @0x472df0 (Windows <c>?NewCAurScene@@YAPEAVCAurScene@@PEBD@Z</c>)
    /// -- the engine's scene factory, and one of the four creation points ADR 0028 replaces. Four game
    /// callers: <c>CGuiPanel::CreateSceneView</c>, <c>CGuiMan::Initialize</c>,
    /// <c>CGuiViewport::CGuiViewport</c> and the area load.</summary>
    [Fn("IntPtr(byte* name)")]
    public const string NewCAurScene = "_Z12NewCAurScenePKc";
    /// <summary><c>NewCAurCamera(char const*)</c> @0x4a62d0 (Windows
    /// <c>?NewCAurCamera@@YAPEAVCAurCamera@@PEBD@Z</c>) -- the camera factory. Four game callers.</summary>
    [Fn("IntPtr(byte* name)")]
    public const string NewCAurCamera = "_Z13NewCAurCameraPKc";
    /// <summary><c>CAurScene::SetAmbient(Vector)</c> @0x461ab0 (Windows
    /// <c>?SetAmbient@CAurScene@@QEAAXVVector@@@Z</c>) -- an out-of-line INTERFACE member the game links
    /// non-virtually (three callers), which writes <c>Scene+0x168</c>/<c>+0x170</c> by field. There is no
    /// vtable slot for it, so a facade cannot answer it and it is hooked. The <c>Vector</c> is BY VALUE: the
    /// Itanium mangling says <c>E6Vector</c> and the MSVC export says <c>VVector@@</c>, so SysV passes it in
    /// xmm0:xmm1 and Win64 passes a pointer to the caller's copy -- which is what
    /// <see cref="CX.Engine.VectorArg"/> names once for both.</summary>
    [Fn("void(IntPtr scene, VectorArg colour)")]
    public const string CAurSceneSetAmbient = "_ZN9CAurScene10SetAmbientE6Vector";

    /// <summary><c>FindScene(char const*)</c> @0x45f670 (Windows <c>?FindScene@@YAPEAVScene@@PEBD@Z</c>) --
    /// the engine's scene REGISTRY lookup, and the one way the game layer reaches a scene it does not hold a
    /// pointer to. Disassembled (not inferred): it loads the array base from <c>Scenes+0</c> and the count
    /// from <c>Scenes+8</c>, then compares the argument against <c>Scene+0xf0</c> with <c>strcasecmp</c>.
    /// <para>In DB mode that registry is EMPTY -- every <c>NewCAurScene</c> is answered by a CX shell and the
    /// engine's own factory, which is what appends to it, never runs. So the lookup returns null and every
    /// caller that goes through it silently does nothing; the area's fog is the one that shows
    /// (<c>CNWCArea::SetFog</c> drives it as <c>FuncInterp("mainscene.fog*")</c> text, not through the
    /// vtable). CX answers the lookup from its own scene table instead.</para></summary>
    [Fn("IntPtr(byte* name)")]
    public const string FindScene = "_Z9FindScenePKc";

    /// <summary><c>WindManager::WindManager()</c> @0x491b90 (Windows <c>??0WindManager@@QEAA@XZ</c>) -- the
    /// default ctor, run over a CX-owned block so a scene shell can carry a real <c>WindManager</c> at
    /// <c>+0x160</c>. Called rather than transcribed because the ctor seeds two 0x400-byte noise fields and
    /// then stores POINTERS INTO ITSELF at <c>+0x838</c>/<c>+0x840</c>; a hand-written copy would have to get
    /// that self-reference right, and the engine's own body gets it right by construction.</summary>
    [Fn("void(IntPtr self)")]
    public const string WindManagerCtor = "_ZN11WindManagerC2Ev";

    /// <summary><c>AuroraSetTileFoggedByWar(Vector&amp;, bool)</c> @0x453320 (Windows
    /// <c>?AuroraSetTileFoggedByWar@@YAXAEAVVector@@_N@Z</c>) -- the fog of war's one entry into the tile
    /// layer. A FREE function with no scene argument: it works on the current scene, so CX answers it for the
    /// world scene (CX-116's <c>TileBuild.SetFoggedByWar</c>). The <c>Vector</c> is by REFERENCE on both OSes,
    /// so one signature serves.</summary>
    [Fn("void(IntPtr pos, bool fogged)")]
    public const string AuroraSetTileFoggedByWar = "_Z24AuroraSetTileFoggedByWarR6Vectorb";

    /// <summary><c>setalpha(CAurObject*, float)</c> @0x4524d0 (Windows
    /// <c>?setalpha@@YAXPEAVCAurObject@@M@Z</c>) -- a thin out-of-line helper the game layer links NON-virtually
    /// (11 callers: <c>CPanelScreenFade::SetFade</c>/<c>Update</c>, <c>CGuiInvbox</c>, <c>CGuiMultiInvbox</c>,
    /// <c>CGuiQuickButton::SetGhosting</c>, <c>CPanelActionQueue</c>, <c>CPanelAreaMap</c>, ...). Its body is
    /// <c>FUN_00550040(*(Part**)(gob + 0x90))</c>, which RECURSES the part tree -- child array at
    /// <c>Part+0x38</c>, count at <c>Part+0x40</c> -- and writes each part's alpha at <c>Part+0xa0</c>.
    /// <para>So it is a §1d-class glue function that <b>walks engine Parts from the game layer</b>, which
    /// <c>CX-182</c> §3 concluded did not happen -- the sweep missed it
    /// because <c>setalpha</c> itself lives below the 0x600000 game-layer threshold and was read as renderer
    /// code. In DB mode it is a hard crash at the first module load (measured:
    /// <c>CPanelScreenFade::SetFade</c> -&gt; <c>Part::Project</c>, segfault), so the seam answers it.</para></summary>
    [Fn("void(IntPtr obj, float alpha)")]
    public const string SetAlphaObject = "_Z8setalphaP10CAurObjectf";

    // ---- CX-120 seam-obj: the Aurora seam. Every one is name-exported on Windows (the whole Aurora layer
    // is, #430), so each has an Nm row below and none needs a build-locked RVA. ----
    /// <summary><c>NewCAurObject(char const* model, char const* name, FILE* stream)</c> @0x4412c0 -- the game
    /// layer's one object factory, 72 call sites. Arg 2 is a NAME OVERRIDE (empty = the model's own name), arg
    /// 3 an already-open stream to parse the model from instead of the resref; neither is a flag word. Returns
    /// NULL when the model does not resolve, with no log of its own.</summary>
    public const string NewCAurObject = "_Z13NewCAurObjectPKcS0_P8_IO_FILE";
    /// <summary><c>NewCAurString(int)</c> @0x494050 -- allocates a <c>StringGob</c> (0x558) and returns its
    /// <c>CAurString</c> sub-object at +0x4c0.</summary>
    public const string NewCAurString = "_Z13NewCAurStringi";
    /// <summary><c>SetShadows(Part*, int)</c> @0x460c90 -- a FREE function, not a <c>Part</c> member: it
    /// recurses a part tree and writes the shadow flag on every light node. <c>AurEnable/DisableShadowing</c>
    /// read <c>Gob</c>+0x90 and call it, so hooking this one answers both without hooking either.</summary>
    public const string SetShadowsPart = "_Z10SetShadowsP4Parti";
    /// <summary><c>AurSetDynamicProjectionLight(CAurObject*)</c> @0x461b10 -- walks the object's collected
    /// light list and filters it on each element's +0x74/+0x80, i.e. engine <c>Part</c> memory.</summary>
    public const string AurSetDynamicProjectionLight = "_Z28AurSetDynamicProjectionLightP10CAurObject";
    /// <summary><c>LerpFloat::Refresh()</c> @0x4f8d20 -- run after a raw write of the 0x40-byte record, which
    /// is what <c>Gob::SetVisualTransformData</c> @0x439bd0 does for each of the ten.</summary>
    [Fn("void(IntPtr lerp)")]
    public const string LerpFloatRefresh = "_ZN9LerpFloat7RefreshEv";
    /// <summary><c>YawPitchRoll(float, float, float)</c> @0x4b6690 -- returns a <c>Quaternion</c> BY VALUE, so
    /// its declaration differs per OS (SysV xmm0:xmm1, Win64 a buffer in rcx with the floats one register
    /// right). Called rather than reimplemented because the visual transform's axis convention is the
    /// engine's, and a guess there silently tilts every server-driven transform.</summary>
    public const string YawPitchRoll = "_Z12YawPitchRollfff";
    /// <summary><c>AurSetPalette(int, char const*)</c> @0x485190 -- the object-free palette setter; CX-117's
    /// <c>PltPalettes</c> answers it from a CPU row buffer and builds the texture in Alloy's bracket
    /// (docs/alloy-db-texture-mode.md).</summary>
    public const string AurSetPalette = "_Z13AurSetPaletteiPKc";
    /// <summary><c>controlpart(char*)</c> @0x5437b0 -- the console function <c>CNWCArea::SetGlobalLightAmbient</c>
    /// @0x8022d0 and <c>SetGlobalLightDiffuse</c> @0x802510 drive the area light through
    /// (<c>FuncInterp("controlpart sun gidy_sun_amb|gidy_sun_diff color r g b")</c>); it finds the sun by
    /// <c>FindGob</c>, whose <c>gobs</c> table only <c>Gob::Gob</c> fills, so in DB mode the seam answers it.</summary>
    public const string ControlPart = "_Z11controlpartPc";
    /// <summary><c>AurClearPalette()</c> @0x485090.</summary>
    public const string AurClearPalette = "_Z15AurClearPalettev";
    /// <summary><c>AurCreatePaletteTexture()</c> @0x4874d0 -- the engine built a GL texture inline; the seam
    /// defers it to the frame, because CX's build has to run inside Alloy's bracket.</summary>
    public const string AurCreatePaletteTexture = "_Z23AurCreatePaletteTexturev";
    /// <summary><c>CExoArrayList&lt;CAurObject*&gt;::Add</c> @0x4b3540 -- how the seam appends to a
    /// <c>List&lt;CAurObject*&gt;</c> the GAME owns (<c>Gob::GetAttachedGobs</c>). Calling the engine's own
    /// list code keeps the array on the engine's heap, which is the only arrangement that is correct on
    /// Windows: nwmain statically links its CRT and exports no <c>free</c>/<c>realloc</c>.</summary>
    [Fn("void(IntPtr list, IntPtr item)")]
    public const string ListAddCAurObject = "_ZN13CExoArrayListIP10CAurObjectE3AddES1_";
    /// <summary><c>CExoArrayList&lt;CAurPart*&gt;::Add</c> @0x445a50 -- the same for <c>Gob::EnumParts</c>.</summary>
    [Fn("void(IntPtr list, IntPtr item)")]
    public const string ListAddCAurPart = "_ZN13CExoArrayListIP8CAurPartE3AddES1_";

    /// <summary>A symbol's Windows resolution. Four forms and no fifth -- the point of #447: a missing key
    /// used to mean both "never looked" and "deliberately Linux-only", so the absence could not be counted.
    /// Now every constant has a row, <see cref="Nm"/>/<see cref="At"/> resolve, <see cref="LinuxOnly"/> is a
    /// decision, and <see cref="Hole"/> is the only form that counts against the port.</summary>
    private readonly record struct Win(string? Name, bool At, string? Why, bool Counted);

    /// <summary>Name-exported on Windows: resolve the MSVC mangling the way Linux resolves Itanium.</summary>
    private static Win Nm(string msvc) => new(msvc, false, null, false);
    /// <summary>Not exported: absolute = runtime module base + the running build's RVA for it (its
    /// <c>rva</c> line in <c>Framework/Builds/*.tsv</c>), build-locked by the PE stamp.</summary>
    private static Win At() => new(null, true, null, false);
    /// <summary>A decision: this symbol is not wanted on Windows, and <paramref name="why"/> says why. Does
    /// not count -- CLAUDE.md's rule is that the absence be DECLARED, not that it be filled.</summary>
    private static Win LinuxOnly(string why) => new(null, false, why, false);
    /// <summary>Not yet anchored. Counts, and is printed by <c>/knobs</c> beside EngineLayout's unresolved
    /// offsets: parity is the constraint (#445), so a named instrument is a waypoint, never an exit.</summary>
    private static Win Hole(string why) => new(null, false, why, true);

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
        [MovieShowFrame]    = Nm("?ShowFrame@CExoMoviePlayerInternal@@AEAAXHHHH@Z"),   // CX-99, rva 0x233b20
        [SetText]           = Nm("?SetText@CAuroraStringWrapper@@QEAAXI@Z"),
        [ExoStringInit]     = Nm("?InitFromCharArray@CExoString@@AEAAAEAV1@PEBDI@Z"),
        [C2DAGetCExoStringEntry] = Nm("?GetCExoStringEntry@C2DA@@QEAAHHAEBVCExoString@@PEAV2@@Z"),   // CX-55 step 2
        [Rules]             = Nm("?g_pRules@@3PEAVCNWRules@@EA"),
        [ExoStringDtor]     = Nm("??1CExoString@@QEAA@XZ"),
        [AppManager]        = Nm("?g_pAppManager@@3PEAVCAppManager@@EA"),
        [ExoBase]           = Nm("?g_pExoBase@@3PEAVCExoBase@@EA"),   // userdir CExoString@0x38 identical on both builds
        [ExoConfigGetBool]   = Nm("??$Get@_N@CExoConfig@@QEAA_NAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@_N@Z"),
        [ExoConfigGetLong]   = Nm("??$Get@_J@CExoConfig@@QEAA_JAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@_N@Z"),
        [ExoConfigGetDouble] = Nm("??$Get@N@CExoConfig@@QEAANAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@_N@Z"),
        [AurPrintString]     = Nm("?AurPrintString@@YAXPEBDHHMMMM0@Z"),   // CX-98: the whole Aurora layer is name-exported

        // ---- CX-107 behaviours ----
        // Read straight off nwmain.exe's export table (tools: CX-107 §6).
        // All thirteen Controls are `?Control@<Class>@@UEAAHM@Z`: `H` return, `M` (float) argument, no
        // by-value crossing, so one declared signature covers both platforms.
        [BehaviorControl]               = Nm("?Control@CAurBehavior@@UEAAHM@Z"),
        [AttachControl]                 = Nm("?Control@CAurBehaviorAttach@@UEAAHM@Z"),
        [AttachTranslateControl]        = Nm("?Control@CAurBehaviorAttachTranslate@@UEAAHM@Z"),
        [AttachRotateControl]           = Nm("?Control@CAurBehaviorAttachRotate@@UEAAHM@Z"),
        [AttachNoOrientationControl]    = Nm("?Control@CAurBehaviorAttachNoOrientation@@UEAAHM@Z"),
        [AttachGuiScaleControl]         = Nm("?Control@CAurBehaviorAttachGUIScale@@UEAAHM@Z"),
        [CrossSceneAttachControl]       = Nm("?Control@CAurBehaviorCrossSceneAttach@@UEAAHM@Z"),
        [CrossSceneAttachOffsetControl] = Nm("?Control@CAurBehaviorCrossSceneAttachWithOffset@@UEAAHM@Z"),
        [SubwindowControl]              = Nm("?Control@CAurBehaviorSubwindow@@UEAAHM@Z"),
        [NavigateControl]               = Nm("?Control@CAurBehaviorNavigate@@UEAAHM@Z"),
        [CameraLooseFollowControl]      = Nm("?Control@CAurBehaviorCameraLooseFollow@@UEAAHM@Z"),
        [CameraOnAStickControl]         = Nm("?Control@CAurBehaviorCameraOnAStick@@UEAAHM@Z"),
        [SafePtrListAdd]      = Nm("?Add@?$CExoArrayList@PEAV?$SafePointer@VCAurObject@@@@@@QEAAXPEAV?$SafePointer@VCAurObject@@@@@Z"),
        [CameraSetViewPort]   = Nm("?SetViewPort@Camera@@UEAAXMMMM@Z"),
        [SceneGetObject]      = Nm("?GetObject@Scene@@UEAAPEAVCAurObject@@PEBD@Z"),

        // --- not exported (client GUI / CNWCMessage): Windows RVA, one per build in Framework/Builds/*.tsv.
        //     Anchored on build 0x68e408bc; see docs/windows-symbols.md for how each was. ---
        [HandleServerToPlayerMessage] = At(),
        [HandleServerToPlayerLogin]   = At(),
        [SendPlayerToServerMessage]   = At(),
        [SendDeviceAdvertiseProperty] = At(),
        // #672: the area-loaded hold; none returns a class by value. Anchors in docs/windows-symbols.md.
        [SendAreaLoaded]              = At(),
        [AreaSetTile]                 = At(),
        [HandleUpdateWorkRemaining]   = At(),
        [StartLoadBar]                = At(),
        [EndLoadBar]                  = At(),
        [GetLoadBarState]             = At(),
        [AreaLoadArea]                = At(),   // CX-52 step 5
        [AreaAddPlaceableStatic]      = At(),   // CX-55 step 2
        [GetNWCMessage]               = At(),
        [SetFocusChatPrompt]          = At(),
        [ChatHandleEnter]             = At(),
        [ChatHandleEsc]               = At(),
        [ChatHandleLMouseDown]        = At(),   // Notify event enum 1
        [ChatHandleLoseFocus]         = At(),   // Notify event enum 2
        [TurnOffKeyboardModal]        = At(),   // anchored via CGuiEdit::SetFocus (call after KeyboardModeOn)
        // Footgun: nwmain.exe statically links the CRT, so the panel frees via the game's own
        // operator delete -- allocate from the game's own operator new (build-locked RVA) or the
        // cross-heap free corrupts. Scalar operator new(size_t), VS2017/2019 release.
        [OperatorNew]                 = At(),
        [OperatorDelete]              = At(),   // void thunk to _free_base; pairs with the operator new above
        [EncodingFromUtf8] = Nm("?FromUTF8@Encoding@@YA?AV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@PEBDW4Locale@1@@Z"),

        // --- consent modal + input, name-exported on Windows (verified in nwmain.exe's export table) ---
        [OkCancelCtor]    = Nm("??0CPanelOkCancel@@QEAA@XZ"),
        [OkCancelActivate]= Nm("?Activate@CPanelOkCancel@@QEAAXVCExoString@@II@Z"),
        [GuiMan]          = Nm("?g_pGuiMan@@3PEAVCGuiMan@@EA"),
        [GetGuiScale]     = Nm("?GetGUIScale@CAurora@@SAMXZ"),   // CAurora shared-engine class -> name-exported
        [SdlGetKeyName]   = Nm("SDL_GetKeyName"),
        [SdlGetKeyFromName] = Nm("SDL_GetKeyFromName"),
        [WalkPlayerToPoint] = At(),          // action camera: "gui_nowalk" string, unique on both builds
        [FeedbackHitCheckHostile] = At(),    // action camera: the picker with GetHeightTransition (Callees)
        [FeedbackHitCheckNonHostile] = At(), // action camera: the other picker, same caller cluster
        [SdlGetModState]  = Nm("SDL_GetModState"),
        // head tracking: the three Gob/Part/LookAtAnimation symbols are shared-engine exports; only the
        // CNWC* chooser needs an anchor (its Windows caller-of-"HEADTRACK" chain, docs/windows-symbols.md).
        [UpdateLookAt]       = At(),
        [PlayFootstep]       = At(),
        [BeginLookAtAnimate] = Nm("?BeginLookAtAnimate@Gob@@UEAAXPEAVCAurObject@@PEBD1MMMM@Z"),
        [GobAnimate] = Nm("?Animate@Gob@@UEAAXM@Z"),
        [AnimateHierarchy] = Nm("?AnimateHierarchy@@YAXPEAVPart@@PEAVMdlNode@@MMM@Z"),
        [CtrlGetQuaternion] = Nm("?GetQuaternionValue@NewController@@QEAAHMPEAVMdlNode@@AEAVQuaternion@@@Z"),
        [CtrlGetVector] = Nm("?GetVectorValue@NewController@@QEAAHMPEAVMdlNode@@AEAVVector@@@Z"),
        [PositionScaleFactor] = Nm("?position_scale_factor@@3MA"),
        [AnimGetName] = At(),   // the only user of "pausewalkfl" and "runfr" (and every "CustomNN" name)
        [SpawnHitVisuals] = At(),   // anchor: its own "SpawnHitVisuals" log tag, 5 refs all inside it
        [LookAtControl]      = Nm("?Control@LookAtAnimation@@UEAAXXZ"),
        // ---- CX-106 anim ----
        [SceneAnimate]              = Nm("?Animate@Scene@@UEAAXM@Z"),
        [CtrlGetFloat]              = Nm("?GetFloatValue@NewController@@QEAAHMPEAVMdlNode@@AEAM@Z"),
        [CtrlReportKeys]            = Nm("?ReportKeys@NewController@@QEAAXPEAVPart@@PEAVMdlNode@@MM@Z"),
        [EmitterCalcRadius]         = Nm("?CalculateRadius@PartEmitter@@QEAAXXZ"),
        [AnimRunListRemove]         = Nm("?Remove@?$CExoArrayList@PEAVAnimRun@@@@QEAAXPEAVAnimRun@@@Z"),
        [AnimRunListDelIndex]       = Nm("?DelIndex@?$CExoArrayList@PEAVAnimRun@@@@QEAAXH@Z"),
        [SafePointerAdd]            = Nm("?Add@?$CExoArrayList@PEAV?$SafePointer@VCAurObject@@@@@@QEAAXPEAV?$SafePointer@VCAurObject@@@@@Z"),
        // auto-target: CActionTarget is client-side (not name-exported on Windows). Both anchored by the
        // 'AcTg' GetGuiObjType tag cluster and confirmed by decompile -- ActivateTargetMode is the same
        // CaptureMouse/CaptureKeyboard/RegisterUpdate/RegisterActionTargetGUI body, HandleLMouseDown the
        // same GetMousePos -> hit-check -> allowed-list scan -> fire.
        [ActivateTargetMode]      = At(),
        [ActionTargetLMouseDown]  = At(),
        // tab-target (CX-636): the click seam is the callee PerformLButtonDownAction (rva 0x6f02c0, the one caller
        // of WalkPlayerToPoint) runs on a verdict-2 pick, same type-byte switch as Linux @0x660830; the colour
        // globals are the DAT_ refs of the Windows HiliteAreaObjects (rva 0x7db2f0), per attitude branch.
        [PerformDefaultActionOnObject] = At(),
        [SdlGetKeyboardState] = Nm("SDL_GetKeyboardState"),
        [HiliteEnemy]     = At(),
        [HiliteFriend]    = At(),
        [HiliteNeutral]   = At(),
        [HilitePlaceable] = At(),
        [HiliteDoor]      = At(),
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
        [GuiManHandleGuiTimer] = Nm("?HandleGuiTimer@CGuiMan@@QEAAXH@Z"),
        [GuiManShowTooltipText] = Nm("?ShowTooltipText@CGuiMan@@QEAAXAEBVCExoString@@H@Z"),
        [AurPartTriggerCreate] = Nm("?AurPartTriggerCreate@@YAPEAVPartTrigger@@PEAVCAurScene@@PEAVVector@@HHPEAMPEAX@Z"),
        [AurPartTriggerDelete] = Nm("?AurPartTriggerDelete@@YAXPEAVCAurScene@@PEAVPartTrigger@@@Z"),
        [AurPartTriggerReleaseMemory] = Nm("?AurPartTriggerReleaseMemory@@YAXPEAVPartTrigger@@@Z"),
        [GuiManHandleKeyPress] = Nm("?HandleKeyPress@CGuiMan@@QEAAHH@Z"),
        [GuiManHandleMouseMove] = Nm("?HandleMouseMove@CGuiMan@@QEAAXHH@Z"),
        [GuiEditHandleLMouseDown] = Nm("?HandleLMouseDown@CGuiEdit@@MEAAXXZ"),
        [GuiEditSetFocus] = Nm("?SetFocus@CGuiEdit@@QEAAXXZ"),
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
        // CX-119: the engine's own stb_truetype pack API + codepage mapper, name-exported on both OSes.
        [Cx119StbttPackBegin]           = Nm("stbtt_PackBegin"),
        [Cx119StbttPackEnd]             = Nm("stbtt_PackEnd"),
        [Cx119StbttPackSetOversampling] = Nm("stbtt_PackSetOversampling"),
        [Cx119StbttPackFontRanges]      = Nm("stbtt_PackFontRanges"),
        [Cx119EncodingSingleCharToUtf8] = Nm("?SingleCharToUTF8@Encoding@@YAHDW4Locale@1@@Z"),
        [SdlGlGetProcAddress]        = Nm("SDL_GL_GetProcAddress"),
        [SdlGetMouseState]           = Nm("SDL_GetMouseState"),
        [SdlSetWindowSize]           = Nm("SDL_SetWindowSize"),
        [SdlGetWindowSize]           = Nm("SDL_GetWindowSize"),
        [SdlStartTextInput]          = Nm("SDL_StartTextInput"),
        [SdlGetClipboardText]        = Nm("SDL_GetClipboardText"),
        [SdlSetClipboardText]        = Nm("SDL_SetClipboardText"),
        [SdlFree]                    = Nm("SDL_free"),
        [RiResetRenderStates]        = Nm("?ResetRenderStates@RenderInterface@@SAXXZ"),
        [RiCheckUniformData]         = Nm("?CheckAndUpdateUniformDirtyDataForFrame@RenderInterface@@SAXM@Z"),
        [RiBindBuffer]               = Nm("?BindBuffer@RenderInterface@@SAXII@Z"),
        [GuiPanelDeactivate]         = Nm("?Deactivate@CGuiPanel@@QEAAXH@Z"),
        [GuiPanelUnToggleOff]        = Nm("?UnToggleOff@CGuiPanel@@QEAAXXZ"),
        // Portraits: the texture guard keys on the two vftables. Not exported; RVAs from the RTTI type descriptors
        // (`.?AVCAurTexture@@` / `.?AVCAurTextureBasic@@` -> complete-object locator -> vftable). MSVC's symbol
        // IS the object's first word (no Itanium +0x10 RTTI skew) -- AurTex.Attach knows.
        [AurTextureGet]              = Nm("?AurTextureGet@@YAPEAVCAurTexture@@PEBDPEAV1@0PEAHH_N@Z"),
        [AurTextureGetTID]           = Nm("?AurTextureGetTID@@YAIPEAVCAurTexture@@@Z"),
        [AurTextureVtbl]             = At(),
        [AurTextureBasicVtbl]        = At(),

        // --- chat (#419): client GUI + CNWCMessage senders, RVA, build 0x68e408bc. Anchors in docs/windows-symbols.md.
        //     Win64 ABI notes: every by-value CExoString arrives as a pointer the CALLEE destroys; CResRef too. ---
        [AppendToMsgBuffer]      = At(),
        [AppendToChatBuffer]     = At(),
        [ChatWindowSetFilter]    = At(),
        [ChatWindowShow]         = At(),   // shown flag +0xaf0 (Linux +0xc68)
        [ChatPromptShow]         = At(),   // shown flag +0x200 (Linux +0x230)
        [ChatPromptSetFocus]     = At(),
        [ChatPromptCtor]         = At(),
        [ShowChatPrompt]         = At(),
        [PrepChatPromptForTell]  = At(),
        [NotifyServerGuiEvent]   = At(),
        [GetPlayerByPlayerName]  = At(),
        [GetPlayerByCreatureName]= At(),
        [GetModule]              = At(),
        [SendChatTalk]           = At(),   // major 9 minor 1
        [SendChatShout]          = At(),   // minor 2
        [SendChatWhisper]        = At(),   // minor 3
        [SendChatTell]           = At(),   // minor 4, WriteBOOL(1) + two strings (TellName)
        [SendChatTellId]         = At(),   // minor 4, WriteBOOL(0) + WriteDWORD(id)
        [SendChatParty]          = At(),   // minor 6
        [SendChatDM]             = At(),   // minor 0xe

        // --- nameplates / combat text / dialog (#420), build 0x68e408bc. Anchors in docs/windows-symbols.md.
        //     Win64 ABI: sret comes AFTER `this` (GetSimpleString, the GetPortrait virtual); a by-value Vector
        //     arrives as a pointer; a by-value CExoString is the callee's to destroy. ---
        [TlkTable]           = Nm("?g_pTlkTable@@3PEAVCTlkTable@@EA"),
        [TlkGetSimpleString] = Nm("?GetSimpleString@CTlkTable@@QEAA?AVCExoString@@I@Z"),
        [GetViewPort]        = Nm("?GetViewPort@RenderInterface@@SAXPEAM@Z"),
        [GetGameObject]      = At(),
        [GetObjectName]      = At(),
        [ShowDialogEntry]    = At(),
        [ShowDialogReplies]  = At(),
        [ShowReplyChosen]    = At(),   // destroys its by-value CExoString: a skip must too
        [CloseDialog]        = At(),
        [SendDialogReply]    = At(),
        [SetChatBubbleMode]  = At(),
        [CreatureShowFeedbackBubbleText]  = At(),   // plated flag +0x448
        [PlaceableShowFeedbackBubbleText] = At(),   // +0x1f8
        [ItemShowFeedbackBubbleText]      = At(),   // `this` = the CNWCObject subobject, flag +0x264
        [SetPopUpBubbleText] = At(),
        [HandleServerToPlayerChat] = At(),   // major 9
        [PlaceableUpdateDelete] = At(),   // __FUNCSIG__ "int __cdecl CNWCMessage::HandleServerToPlayerPlaceableUpdate_Delete(void)"
        [ShowFloatyText]     = At(),

        // --- #446: the three CNWC* anchors Alloy's highlight and border ring hook. No CNWC* class is
        //     name-exported on Windows, so each was anchored by a compiler-invariant string and confirmed
        //     against the named Linux original (docs/windows-symbols.md).
        [RefreshHilite]    = At(),   // its own ExoLog nwcanimbase.cpp:0x123a; channels +0x9c/+0xb0/+0xc8
        [AreaLoadBorder]   = At(),   // __FUNCSIG__ "int __cdecl CNWCArea::LoadBorder(class CGuiAreaLoadscreen *,float)"
        [AreaReloadBorder] = At(),   // HandleServerToPlayerArea's flag-bit-2 branch; deletes (vt+0x50) + respawns (vt+0x30)
        // #615: SetWeather anchored by its own "weatherlight"/"ModelLightning" strings (the only user of either);
        //       Start/Stop are the two callees of HandleServerToPlayerArea's case 4 (ReadBOOL ? Start : Stop), bodies
        //       identical to Linux (+0x2f1, +0x2ec = 6000, CNWCAmbientSound::Rain). All field offsets Same.
        [TileSetGet]       = Nm("?GetTileSet@CNWTileSetManager@@QEAAPEAVCNWTileSet@@AEBVCResRef@@@Z"),
        [AreaSetWeather]   = At(),
        [AreaStartWeather] = At(),
        [AreaStopWeather]  = At(),

        // --- #447: the rest of the table. The 142 Nm rows below are MECHANICAL -- MSVC mangling carries the
        //     return type Itanium drops (#433), so llvm-undname over nwmain.exe's 18237 exports pairs on
        //     (qualified name + parameter list) with nothing left ambiguous; the pass reproduces all 39
        //     hand-derived Nm rows above exactly, which is what licenses the other 142. The At rows and the
        //     two declared absences are per-row measurements or decisions; see the ticket.
        [AddPartsToDrawBuckets] = Nm("?AddPartsToDrawBuckets@Scene@@QEAAXXZ"),
        [AurResFree] = Nm("?AurResFree@@YAXPEAX@Z"),
        [AurResGet] = Nm("?AurResGet@@YAPEAXPEBD0PEAH_N@Z"),
        // CX-55 step 1: all five name-exported; GetResource/ReleaseResource are statics, so the 10-byte header
        // and the (name, ext, size) order are the same on both builds (Windows rva 0xf63d0 / 0xf7620).
        [AuroraGetResource] = Nm("?GetResource@CAuroraInterface@@SAPEAXPEBD0PEAH@Z"),
        [AuroraReleaseResource] = Nm("?ReleaseResource@CAuroraInterface@@SAXPEAX@Z"),
        [BinaryFindModel] = Nm("?BinaryFindModel@@YAPEAVModel@@PEBD@Z"),
        [ModelTable] = Nm("?Models@@3V?$List@PEAVModel@@@@A"),   // CX-113
        [FindAnimation] = Nm("?FindAnimation@@YAPEAVAnimation@@PEAVModel@@PEBD@Z"),   // CX-113
        [FindAnimScale] = Nm("?FindAnimScale@@YAMPEAVModel@@PEAVAnimation@@@Z"),   // CX-113
        [TileSetGetTileData] = Nm("?GetTileData@CNWTileSet@@QEAAPEAVCNWTileData@@H@Z"),
        [TileDataGetModelResRef] = Nm("?GetModelResRef@CNWTileData@@QEAA?AVCResRef@@XZ"),
        [NwTileSetID] = Nm("?SetID@CNWTile@@QEAAXH@Z"),
        // CX-55 step 3: both name-exported. compilemodelsinternal is a free function (@@YA), so its message
        // pointer return needs no argument reordering; GetAliasPath returns a const REFERENCE (AEBV), not a
        // class by value, so there is no hidden buffer to order either (#456).
        [CompileModels] = Nm("?compilemodelsinternal@@YAPEBD_NPEBD@Z"),
        [AliasGetAliasPath] = Nm("?GetAliasPath@CExoAliasList@@QEBAAEBVCExoString@@AEBV2@H@Z"),
        [AurResGetData] = Nm("?AurResGetData@@YAPEADH@Z"),
        // CX-55 step 0: the model load funnel. All four name-exported; the MSVC names carry the return types
        // (Model*, MaxTree*, Gob*) Itanium drops, and GetResource's `SA` says it is static -- no `this`.
        [FindModel] = Nm("?FindModel@@YAPEAVModel@@PEBD@Z"),
        [IoReadSync] = Nm("?ReadSync@IODispatcher@@QEAAPEAVMaxTree@@PEBD@Z"),
        [SpawnPartsForTile] = Nm("?SpawnPartsForTile@@YAPEAVGob@@PEBDAEBVVector@@AEBVQuaternion@@HHAEAV?$List@PEAVPart@@@@@Z"),
        [AurTextureBindInUnit] = Nm("?BindInUnit@CAurTexture@@UEAAXIH@Z"),
        [AurTextureBind] = Nm("?Bind@CAurTexture@@UEAAXH@Z"),
        [AurTextureBuildAndStore] = Nm("?BuildAndStore@CAurTexture@@QEAAXXZ"),
        [AurTextureBuildAndStoreTexture] = Nm("?BuildAndStoreTexture@CAurTextureBasic@@QEAAXXZ"),
        [AurTextureBuildAndStoreAll] = Nm("?AurTextureBuildAndStoreAll@@YAXXZ"),
        [RefreshTextures] = Nm("?RefreshTextures@@3V?$List@PEAVCAurTextureBasic@@@@A"),
        [MaterialInitializeTextures] = Nm("?InitializeTextures@Material@@QEAAXPEBD000@Z"),
        [AurTextureConstructImage] = Nm("?ConstructImage@CAurTextureBasic@@QEAAXXZ"),
        [AurTextureGetController] = Nm("?AurTextureGetController@@YAPEAVTextureController@@PEAVCAurTexture@@@Z"),
        [AurTextureDtor] = Nm("??1CAurTexture@@UEAA@XZ"),
        [AurTextureGlImageBasic] = Nm("?glImageBasic@CAurTextureBasic@@QEAAH_N@Z"),
        [OperatorNewArray] = Nm("??2MaxTree@@SAPEAX_K@Z"),
        [AurTextureNullPtr] = Nm("?m_pNullTexture@CAurTextureBasic@@2V?$SafePointer@VCAurTexture@@@@A"),
        [LerpFloatLerped] = Nm("?Lerped@LerpFloat@@QEBAMXZ"),
        [GobSetLODModel] = Nm("?SetLODModel@Gob@@QEAAXXZ"),
        [EnableLod] = Nm("?g_bEnableLOD@@3HA"),
        [LodScaleFactor] = Nm("?g_fLODScaleFactor@@3MA"),
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
        // CX-120: verified against the shipped PE's export table, not derived.
        [CameraFollowCtor] = Nm("??0CAurBehaviorCameraFollow@@QEAA@PEAVCAurObject@@0@Z"),
        // CNWCModule has 0 exports; both found by offset signature (+0x2bc flag, +0x134, +0x88/+0x8c impulse,
        // +0x280) with tools/ghidra/ScanOffsets.java -- bodies decompile identical to Linux, vtable slots -8.
        [TurnCamera] = At(),
        [TiltCamera] = At(),
        [UpdateDriveMode] = At(),   // #540: the one function carrying both 499999 and 250000 (ScanOffsets)
        [WalkUpdateLocation] = At(),   // CX-682: the one function with both "RUNDIST" and "WALKDIST" (StringRefs)
        [TestDirectLine] = At(),       // CX-682: WalkPlayerToPoint's 7-slot line-test callee (docs/windows-symbols.md)
        [SdlSetRelativeMouseMode] = Nm("SDL_SetRelativeMouseMode"),
        [SdlWarpMouseInWindow] = Nm("SDL_WarpMouseInWindow"),
        // #543: CGuiInGame is not exported; both reached from the anchored HandleServerToPlayerMessage (0x7a41a0)
        // case 0x33 -> cutscene handler 0x786450 -> minor 7's ToggleOff / UnToggleOff arms, bodies identical to Linux.
        [GuiInGameToggleOff] = At(),
        [GuiInGameUnToggleOff] = At(),
        [CurrentScene] = Nm("?currentscene@@3PEAVScene@@EA"),
        [HitcheckStaticTrimesh] = Nm("?hitcheck_static_trimesh_parts@@3HA"),
        [HitCheckBspNodeCount] = Nm("?counthitcheckscenebspnode@@3HA"),
        [ComputeZValue] = LinuxOnly("CX-118 diagnostic behind alloy.hitcheck; CNWC* is not name-exported"),
        [AreaGetTile] = LinuxOnly("CX-118 diagnostic behind alloy.hitcheck; CNWC* is not name-exported"),
        [SafePointerObjectAssign] = Nm("??4?$SafePointer@VCAurObject@@@@QEAAAEAV0@QEAVCAurObject@@@Z"),
        [HitcheckStaticBsp] = Nm("?hitcheck_static_bsp_parts@@3HA"),
        [EnableNonBspGroundHit] = Nm("?enablenonbspgroundhit@@3HA"),
        [CycleTidControllerVtbl] = At(),   // RTTI type descriptor -> COL -> vftable (tools/abi/vtable-slots.py)
        [DeltaT] = Nm("?DeltaT@@3MA"),
        [GobHighlightAmplitude] = Nm("?m_fGobHighlightAmplitude@Scene@@0MA"),
        [GobHighlightFrequency] = Nm("?m_fGobHighlightFrequency@Scene@@0MA"),
        [GobHighlightIntensity] = Nm("?m_fGobHighlightIntensity@Scene@@0MA"),
        [GobHighlightSize] = Nm("?m_fGobHighlightSize@Scene@@0MA"),
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
        [ForceDialogVisible] = LinuxOnly("empty body on both builds; nothing to skip"),
        [FullDynamicLighting] = Nm("?g_bFullDynamicLighting@@3HA"),
        [GetCanKeyholeDissolve] = Nm("?GetCanKeyholeDissolve@Camera@@QEAA_NPEBVPartTriMesh@@@Z"),
        [GetEmitterLighting] = Nm("?GetEmitterLighting@LightManager@@QEAA?AVVector@@V2@PEAV?$List@PEAVPartLight@@@@@Z"),
        [GetLensFlareLights] = Nm("?GetLensFlareLights@LightManager@@QEAAAEAV?$List@PEAVPartLight@@@@XZ"),
        [GetLightAdjustedRadius] = Nm("?GetLightAdjustedRadius@@YAMPEBVPartLight@@@Z"),
        [GetIsLightInViewVolume] = Nm("?GetIsLightInViewVolume@@YAEPEBVPartLight@@@Z"),
        [GetLightFadeSpeed] = Nm("?GetLightFadeSpeed@@YAMPEBVPartLight@@@Z"),
        [GetPlayerCreature] = LinuxOnly("#14 chat-render probe, no caller"),
        [GetServerNetworkAddress] = Nm("?GetServerNetworkAddress@CNetLayer@@QEAA?AVCExoString@@XZ"),
        [GlActiveTextureStage] = Nm("?m_activeTextureStage@GLRender@@0IA"),
        [GlArrayBufferBound] = Nm("?m_nArrayBufferBound@GLRender@@0IA"),
        [GlBoundTextures] = Nm("?m_boundTextures@GLRender@@0PAUBoundTexture@1@A"),
        [GlCurrentCustomProgram] = Nm("?m_pCurrentCustomProgram@GLRender@@0PEAUPROG_FIXED@1@EA"),
        [GlDeleteBuffer] = Nm("?DeleteBuffer@GLRender@@CAXI@Z"),
        [GlEnabledTextureStages] = Nm("?m_bEnabledTextureStages@GLRender@@0IA"),
        [GlEnabledStates] = Nm("?m_bEnabledStates@GLRender@@0IA"),
        [GlGlobalColor] = Nm("?m_globalColor@GLRender@@0VVector4@@A"),
        [GlSrcBlendMode] = Nm("?cm_nPrevSrcBlendMode@GLRender@@0W4AuroraBlendModes@Aurora@@A"),
        [GlDstBlendMode] = Nm("?cm_nPrevDstBlendMode@GLRender@@0W4AuroraBlendModes@Aurora@@A"),
        [GlDepthMask] = Nm("?bDepthMask@GLRender@@0EA"),
        [GlViewport] = Nm("?m_viewport@GLRender@@0PAHA"),
        [GlMaterial] = Nm("?m_material@GLRender@@0UMATERIAL@1@A"),
        [RiSetColorv] = Nm("?SetColorv@RenderInterface@@SAXPEAM@Z"),
        [SceneRenderOnlyGobs] = Nm("?RenderOnlyGobs@Scene@@UEAAXXZ"),
        [ScreenWidth] = Nm("?g_nScreenWidth@@3HA"),
        [ScreenHeight] = Nm("?g_nScreenHeight@@3HA"),
        [StatusBarSetStatus] = Nm("?SetStatus@CGuiStatusBar@@QEAAXM@Z"),
        [GlDrawReset] = Nm("?DrawReset@GLRender@@CAXXZ"),
        [SdlGlSwapWindow] = Nm("SDL_GL_SwapWindow"),
        [RiClear] = Nm("?Clear@RenderInterface@@SAXI@Z"),
        [RiSetClearColor] = Nm("?SetClearColor@RenderInterface@@SAXMMMM@Z"),
        [GlDefaultFramebuffer] = Nm("?cm_nDefaultFramebuffer@GLRender@@0IA"),
        [GlDrawFramebufferBound] = Nm("?m_nDrawFrameBufferBound@GLRender@@0IA"),
        [GlGetPool] = Nm("?GetPool@GLRender@@CAPEBXI@Z"),
        [GlGlobalShaderConstantsF] = Nm("?m_globalShaderConstantsf@GLRender@@0PAMA"),
        [GlGlobalShaderConstantsI] = Nm("?m_globalShaderConstantsi@GLRender@@0PAHA"),
        [GlGlobalShaderConstantsV] = Nm("?m_globalShaderConstantsv@GLRender@@0PAVVector@@A"),
        [GlGlobalShaderConstantsV4] = Nm("?m_globalShaderConstantsv4@GLRender@@0PAVVector4@@A"),
        [GlMaxElementIndices] = Nm("?GetVertexBufferMaxElementIndices@GLRender@@CAHXZ"),
        [GlMaxElementVertices] = Nm("?GetVertexBufferMaxElementVertices@GLRender@@CAHXZ"),
        [GlOrphanBuffers] = Nm("?g_bOrphanBuffersBeforeWrite@@3HA"),
        [GlPoolTableAnchor] = At(),   // the {base,count} pair itself -- GetPoolBuffer loads it, UnlockPool leas it
        [GlRenderBones] = Nm("?m_bones@GLRender@@0PAVMatrix4x4@@A"),
        [GlRenderVertexData] = Nm("?RenderVertexData@GLRender@@CAXPEAVVertexDataObject@@IW4AuroraPrimitiveTypes@Aurora@@II@Z"),
        [GlRenderVertexStreamData] = Nm("?RenderVertexStreamData@GLRender@@CAXPEAVVertexStreamDataObject@@W4AuroraPrimitiveTypes@Aurora@@II@Z"),
        [GlSetUniforms] = Nm("?SetUniforms@GLRender@@CAXAEAUPROG_FIXED@1@@Z"),
        [GlSetVertexDataObject] = Nm("?SetVertexDataObject@GLRender@@CAXPEAVVertexDataObject@@@Z"),
        [GlShaderRuntimeData] = Nm("?m_shaderExposedRuntimeData@GLRender@@0UShaderExposedRuntimeData@1@A"),
        [GlTotalMemPool] = Nm("?totalmempool@@3HA"),
        [GlUnlockPool] = Nm("?UnlockPool@GLRender@@CAXIII@Z"),
        [GlUseProgramConditionally] = Nm("?GlUseProgramConditionally@GLRender@@CAXI@Z"),
        [AnimateParticles] = Nm("?AnimateParticles@PartEmitter@@QEAAXM@Z"),
        [EmitterBucket] = Nm("?emitterbucket@@3V?$List@PEAVPartEmitter@@@@A"),
        [RenderNothing] = Nm("?RenderNothing@PartEmitter@@QEAAXXZ"),
        [AddPointSourceWind] = Nm("?AddPointSourceWind@WindManager@@QEAAXVVector@@MMM@Z"),
        [GobBucket] = Nm("?gobbucket@@3V?$List@PEAVGob@@@@A"),
        [GobCollectLights] = Nm("?CollectLights@Gob@@QEAAXAEAV?$List@PEAVPartLight@@@@@Z"),
        [GobDisableInViewVolume] = Nm("?DisableInViewVolume@Gob@@UEAAXXZ"),
        [GobEnableInViewVolume] = Nm("?EnableInViewVolume@Gob@@UEAAXXZ"),
        [GobGetAdjustedScale] = Nm("?GetAdjustedScale@Gob@@UEBAMXZ"),
        [GobListAdd] = Nm("?Add@?$CExoArrayList@PEAVGob@@@@QEAAXPEAVGob@@@Z"),
        [GobOutside] = Nm("?GobOutside@@YAHPEAVGob@@AEAV?$List@VPlane@@@@@Z"),
        [GobProcessGob] = Nm("?ProcessGob@Gob@@QEAAXXZ"),
        [GrassEnabled] = Nm("?m_bGrassIsEnabled@Scene@@0_NA"),
        [GrassFarDistance] = Nm("?m_fGrassFarRenderDistance@Scene@@0MA"),
        [GrassInfo] = Nm("?g_GrassInfo@@3V?$List@VCAurGrassInfo@@@@A"),
        [GrassNearRadius] = Nm("?m_fNearRadiusSquared@CAurTriangleBin@@2MA"),
        [GuiUpdateAndRender] = Nm("?UpdateAndRender@CGuiMan@@QEAAXM@Z"),
        [HideCompass] = At(),
        [LightAreaAmbient] = Nm("?m_lightAreaAmbient@GLRender@@0VVector@@A"),
        [LightAreaDiffuse] = Nm("?m_lightAreaDiffuse@GLRender@@0VVector@@A"),
        [LightAreaDiffuseDir] = Nm("?m_lightAreaDiffuseDirection@GLRender@@0VVector@@A"),
        [LightMgrGetMaxLights] = Nm("?GetMaxLights@LightManager@@SAHXZ"),
        [LightMgrPrioritizeLights] = Nm("?PrioritizeLights@LightManager@@UEAAXAEBVVector@@@Z"),
        [LightMgrSetupLights] = Nm("?SetupLightsForThisPositionAndRadius@LightManager@@UEAAXAEBVVector@@M@Z"),
        [LightMgrUpdateLights] = Nm("?UpdateLights@LightManager@@UEAAXXZ"),
        [LoadMatrixIdentity] = Nm("?LoadMatrixIdentity@RenderInterface@@SAXXZ"),
        [MAurMtxStack] = Nm("?m_aurMtxStack@@3PAVaurMatrixStack@@A"),
        [MCurrentMatrix] = Nm("?m_nCurrentMatrix@GLRender@@0IA"),
        [ManageSceneBSP] = Nm("?ManageSceneBSP@@YAXPEAVScene@@@Z"),
        [MatGetTexture] = Nm("?GetTexture@Material@@QEAAPEAVCAurTexture@@H@Z"),
        [MatHasBumpyShinyMap] = Nm("?HasBumpyShinyMap@Material@@QEAA_NXZ"),
        [MatHasEnvMap] = Nm("?HasEnvMap@Material@@QEAA_NXZ"),
        [MeshBuckets] = Nm("?meshbuckets@@3V?$List@VBucket@@@@A"),
        [MinDoorAlpha] = Nm("?mindooralpha@@3MA"),
        [MsaaSamples] = Nm("?g_nMSAA@@3HA"),
        [MultMatrix] = Nm("?MultMatrix@RenderInterface@@SAXPEAM@Z"),
        [NetProcessReceivedFrames] = Nm("?ProcessReceivedFrames@CNetLayer@@QEAAXH@Z"),
        [NkSdlCopyEvent] = Hole("nuklear is not exported by nwmain.exe"),
        // CX-101: nuklear is unexported on Windows, but nk_sdl_render's Tracy SourceLocationData anchors it and
        // its BODY names the other four call for call (CX-97 §4).
        [NkSdlRender] = At(),             // Tracy "void __cdecl nk_sdl_render(float)"; body identical to Linux @0xe07bb0
        [NkSdlRefreshConfig] = At(),      // the first call in that body
        [NkConvert] = At(),               // its rcx/rdx are the ctx and the cmds buffer
        [NkClear] = At(),                 // the call right after the draw-command walk
        [NkCmds] = At(),                  // DATA: the `lea rdx` of the nk_convert call
        [NkTexNull] = At(),               // DATA: the `movaps xmm0,[rip]` into the convert config at +0x18
        [NkWindowIsAnyHovered] = Hole("nuklear is not exported by nwmain.exe"),
        [NuiStatefulWidgetVtbl] = At(),   // RTTI type descriptor -> COL -> vftable
        [NuiWidgetRender] = At(),         // Widget / StatefulWidget RTTI vftable slot 4 (docs/windows-symbols.md)
        [Nuklear] = At(),                 // DATA: the `mov rax,[rip]` whose +0x5088 takes the frame's delta time
        [OnGainFocus] = LinuxOnly("empty stub reached by indirect dispatch; OS focus handling covers it"),
        [OnLostFocus] = LinuxOnly("empty stub reached by indirect dispatch; OS focus handling covers it"),
        [PartAnimate] = Nm("?Animate@Part@@UEAAXM@Z"),
        [PartEmitterAnimate] = Nm("?Animate@PartEmitter@@UEAAXM@Z"),
        [AnimateStaticLight] = Nm("?AnimateStaticLight@@YAXPEAVCAurObject@@PEBDPEAX@Z"),
        [PartDanglyAnimate] = Nm("?Animate@PartDanglyMesh@@UEAAXM@Z"),
        [PartDanglyMeshVtbl] = At(),   // RTTI type descriptor -> COL -> vftable (tools/abi/vtable-slots.py)
        [PartDtor] = Nm("??1Part@@UEAA@XZ"),
        [GobAppend] = LinuxOnly("#397 diagnostic watch; no swap forensics on Windows yet"),
        [GobAmputate] = LinuxOnly("#397 diagnostic watch; no swap forensics on Windows yet"),
        [GobSetScene] = Nm("?SetScene@Gob@@QEAAXPEAVScene@@@Z"),
        [GobSetSubtreeEnabled] = Nm("?SetSubtreeEnabled@Gob@@UEAA_NPEBDH@Z"),
        // #275: ParseField and Init are called (not inlined) on Windows -- CreateSharedMaterial/GetSharedMaterial.
        [SharedMaterialParseField] = Nm("?ParseField@SharedMaterial@@QEAAXPEBD@Z"),
        [SharedMaterialInit] = Nm("?Init@SharedMaterial@@QEAAXPEBD@Z"),
        // The exported ??1SharedMaterial has NO callers: MSVC inlined it into the scalar deleting destructor
        // (this, flags), which RemoveRef and RefreshSharedMaterial call -- the same two callers as Linux D2.
        [SharedMaterialDtor] = At(),
        [LoadTexture] = Nm("?LoadTexture@@YAPEAVCAurTexture@@PEBD@Z"),
        [AurTextureRelease] = Nm("?AurTextureRelease@@YAXPEAVCAurTexture@@@Z"),
        [PartEmitterDtor] = Nm("??1PartEmitter@@UEAA@XZ"),
        [PartGetIsOpaque] = Nm("?GetIsOpaque@PartTriMesh@@QEAA_NXZ"),
        [PartGetWorld] = Nm("?GetWorld@Part@@UEAAXAEAVVector@@AEAVQuaternion@@@Z"),
        [PartSkinVtbl] = At(),   // RTTI type descriptor -> COL -> vftable (tools/abi/vtable-slots.py)
        [PltOnGpu] = Nm("?g_bGeneratePLTOnGPU@@3HA"),
        [PopCurrentMatrix] = Nm("?PopCurrentMatrix@RenderInterface@@SAXXZ"),
        [PrimTypeTranslationTable] = At(),   // the {0,1,3,4,5,6,2} run in .rdata; RenderVertexData leas it three times
        [ProjectionAlpha] = Nm("?projectionalpha@@3MA"),
        [PushCurrentMatrix] = Nm("?PushCurrentMatrix@RenderInterface@@SAXXZ"),
        [QuickbarReposition] = At(),
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
        [AurGetCompressedImage] = Nm("?AurGetCompressedImage@@3P6APEAEPEBDPEAH1111PEAM@ZEA"),
        [AurReleaseRaster] = Nm("?AurReleaseRaster@@3P6AXPEAXHHHHH@ZEA"),
        [AurGetRaster] = Nm("?AurGetRaster@@3P6APEAEPEBDPEAH11@ZEA"),
        [AurGetImageWrapper] = Nm("?AurGetImageWrapper@@YAPEAEPEBDPEAH111PEAW4AuroraContainerFormats@Aurora@@PEAW4AuroraTextureFormats@2@PEAMH@Z"),
        [TgaServiced] = Nm("?OnResourceServiced@CResTGA@@UEAAHXZ"),
        [ImageDecode] = Nm("?ImageDecode@@YAPEAEPEAEHH0HPEAHH@Z"),
        [AurGetLayeredRasterWrapper] = Nm("?AurGetLayeredRasterWrapper@@YAPEAEPEBDPEAH11@Z"),
        [AurReleaseLayeredRaster] = Nm("?AurReleaseLayeredRaster@@3P6AXPEAXHH@ZEA"),
        [PaletteImage] = Nm("?paletteimage@@3PEAEEA"),
        [PaletteHeight] = Nm("?paletteheight@@3HA"),
        [NkScrollbarBehavior] = At(),   // callee of nk_do_scrollbarv/h (0xa65d10 / 0xa65700) that owns clicked_pos
        [NkDrawScrollbar] = At(),       // their other private callee: style items 0/0x50/0xa0 by state bit
        [NkDoScrollbarV] = At(),        // nk_panel_end's callee whose result is stored through panel+0x20 (offset_y)
        [NkDoScrollbarH] = At(),        // ... and through panel+0x18 (offset_x)
        [NkLoadImg] = At(),   // the only function citing "nk_image: failed to load texture '%s' with ANY..." (docs/windows-symbols.md)
        [ExoResManGet] = Nm("?Get@CExoResMan@@QEAA?AV?$shared_ptr@VDataBlock@@@std@@AEBVCResRef@@G@Z"),
        [DataBlockFromBase64] = Nm("?FromBase64@DataBlock@@SA?AV?$shared_ptr@VDataBlock@@@std@@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@3@@Z"),
        [NwcCreateItem] = At(),   // its own .pdata entry; the only function citing "gifp" and "gi_armor01" (docs/windows-symbols.md)
        [IdentityVisualTransform] = Nm("?s_IdentityVisualTransformData@ObjectVisualTransformData@@0U1@B"),
        [KiwiSubstitute] = Nm("?substitute@SolverImpl@impl@kiwi@@AEAAXAEBVSymbol@23@AEBVRow@23@@Z"),
        [KiwiOptimize] = Nm("?optimize@SolverImpl@impl@kiwi@@AEAAXAEBVRow@23@@Z"),
        [ReportCrashToUser] = Nm("?ReportCrashToUser@CrashReporter_Windows@@UEAAXPEBD0@Z"),
        [CrashHandlerExpose] = Nm("?CrashHandlerExpose@@YAXPEBD0_N@Z"),
        [KiwiCreateRow] = Nm("?createRow@SolverImpl@impl@kiwi@@AEAA?AV?$unique_ptr@VRow@impl@kiwi@@U?$default_delete@VRow@impl@kiwi@@@std@@@std@@AEBVConstraint@3@AEAUTag@123@@Z"),
        [NuiHandleError] = At(),   // its own .pdata entry holding the "Error " reference; its 3 callers match Linux's
        [NuiLayoutGroupVtbl] = At(),   // RTTI type descriptor -> COL -> vftable
        [NuiSingleGroupVtbl] = At(),   // RTTI type descriptor -> COL -> vftable
        [NuiSpanVtbl] = At(),   // RTTI type descriptor -> COL -> vftable
        [ResManExists] = Nm("?Exists@CExoResMan@@QEAAHAEBVCResRef@@GPEAI@Z"),
        [ResManGetKeyEntry] = Nm("?GetKeyEntry@CExoResMan@@QEAAHAEBVCResRef@@GPEAPEAVCExoKeyTable@@PEAPEAVCKeyTableEntry@@_N@Z"),
        [AliasResolveFileName] = Nm("?ResolveFileName@CExoAliasList@@QEBA?AVCExoString@@AEBV2@G@Z"),
        [ResManGetResObject] = Nm("?GetResObject@CExoResMan@@QEAAPEAVCRes@@AEBVCResRef@@G@Z"),
        [ResManRelease] = Nm("?Release@CExoResMan@@QEAAHPEAVCRes@@@Z"),
        [ResManReleaseObj] = Nm("?ReleaseResObject@CExoResMan@@QEAAHPEAVCRes@@_N@Z"),
        [ResManSetResObject] = Nm("?SetResObject@CExoResMan@@QEAAXAEBVCResRef@@GPEAVCRes@@@Z"),
        [ResManUpdate] = Nm("?Update@CExoResMan@@QEAAXI@Z"),
        [RiCreateTexture] = Nm("?CreateTexture@RenderInterface@@SAXPEAVCAurTexture@@_NH@Z"),
        [RiCreateCubeTexture] = Nm("?CreateCubeTexture@RenderInterface@@SAXPEAVCAurTexture@@_NH@Z"),
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
        [SceneGetWindVector] = Nm("?GetWindVector@Scene@@QEAA?AVVector@@AEBV2@M@Z"),
        [SceneHitCheck] = Nm("?HitCheck@Scene@@UEAAHVVector@@0PEAV2@PEAVCHitInfo@@@Z"),
        [HitInfoReset] = Nm("?Reset@CHitInfo@@QEAAXH_JH@Z"),
        [SceneInitBsp] = Nm("?InitBSP@Scene@@UEAAXHHH_NM@Z"),
        [SceneSequence] = Nm("?Sequence@Scene@@2IA"),
        [GeomFadeEnabled] = Nm("?geomFadeEnabled@Scene@@2HA"),
        [SceneSpawnTile] = Nm("?SpawnTile@Scene@@UEAAPEAVCAurObject@@PEBDAEBVVector@@HHPEAVQuaternion@@@Z"),
        [SetMatrixMode] = Nm("?SetMatrixMode@RenderInterface@@SAXW4AuroraTransStates@Aurora@@@Z"),
        [SetModelMatrix] = Nm("?SetModelMatrix@GLRender@@CAXAEBVMatrix4x4@@@Z"),
        [SetMouseCursor] = Hole("CCachedMouseCursorManager is not exported"),
        [SetOrthoTransform] = Nm("?SetOrthoTransform@RenderInterface@@SAXNNNNNN@Z"),
        [SetPositionScreen] = Nm("?SetPositionScreen@CGuiPanel@@QEAAXHH@Z"),
        [SetPriMsgFilter] = LinuxOnly("#14 chat-render probe, no caller"),
        [ShowCompass] = At(),
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

        // --- CX-689: the radial seam. CRadialMenu is name-exported; the root vftables are the MSVC `vftable`
        //     labels (the object's vptr points AT the label, no +0x10 as on Itanium). FindSym.java, build 0x68e408bc.
        [RadialActivateInternal]   = Nm("?ActivateInternal@CRadialMenu@@IEAAXXZ"),
        [RadialCreateSubMenu]      = Nm("?CreateSubMenu@CRadialMenu@@IEAAHH@Z"),
        [RadialRecursiveAutoDelete] = Nm("?RecursiveAutoDelete@CRadialMenu@@IEAAHXZ"),
        [RadialMenuDepth]          = Nm("?static_nMenuDepth@CRadialMenu@@1HA"),
        [GuiCurrentEventObject]    = Nm("?s_pCurrentEventObject@CGuiObject@@1PEAV1@EA"),
        [RadialCtorCounter]        = Nm("?g_nCRadialMenu_ctorCounter@@3HA"),
        [VtCRadialMenu]            = At(),
        [VtCGuiCreatureMenu]       = At(),
        [VtCClientSpellMenu]       = At(),
        [VtCRadialSpellMenu]       = At(),
        [VtCClientTrapMenu]        = At(),
        [VtCGuiItemMenu]           = At(),
        [VtCGuiPlaceableMenu]      = At(),
        [VtCGuiDoorMenu]           = At(),
        [VtCGuiQuickbarMenu]       = At(),
        [VtCGuiChatWindowMenu]     = At(),
        // --- CX-694 self pins: unexported handlers, paired by SetEntryx tuple + body (docs/windows-symbols.md).
        [CreatureMenuUseFeat]      = At(),
        [CreatureMenuEmote]        = At(),
        [CreatureMenuToggleMode]   = At(),
        [CreatureMenuQuickChat]    = At(),
        [CreatureMenuUseSkill]     = At(),
        [CreatureMenuRest]         = At(),
        [CreatureMenuCharacterSheet] = At(),
        [SpellMenuCast]            = At(),
        [SpellMenuCastSla]         = At(),
        [SpellMenuUseFeat]         = At(),
        [SpellMenuTargetIsValid]   = At(),
        [CreatureMenuCtor]         = At(),
        [SpellMenuCtor]            = At(),
        // --- CX-693: AurTextureFind is a name export; the icon vftables are MSVC labels (FindSym.java, build 0x68e408bc).
        [AurTextureFind]           = Nm("?AurTextureFind@@YAPEAVCAurTexture@@PEBD0PEAHH_N@Z"),
        [VtCLayeredIcon]           = At(),
        [VtCCompositeIcon]         = At(),
        [VtCArmorIcon]             = At(),
        [GuiIconGetIconModel]      = Nm("?GetIconModel@CGuiIcon@@QEAAPEAVCAurObject@@XZ"),
        // --- CX-54 step 3: NWSync read. The manifest edges are shared-engine exports; the sqlite3 C API is
        //     exported UNMANGLED on nwmain.exe (311 sqlite3_* names, verified against the build 0x68e408bc
        //     export table), so it resolves by its own name on both platforms.
        [ResManAddManifest]    = Nm("?AddManifest@CExoResMan@@QEAAHAEBUSHA1@Hash@@I@Z"),
        [ResManRemoveManifest] = Nm("?RemoveManifest@CExoResMan@@QEAAHAEBUSHA1@Hash@@@Z"),
        [Sqlite3OpenV2]      = Nm("sqlite3_open_v2"),
        [Sqlite3CloseV2]     = Nm("sqlite3_close_v2"),
        [Sqlite3PrepareV2]   = Nm("sqlite3_prepare_v2"),
        [Sqlite3Exec]        = Nm("sqlite3_exec"),
        [Sqlite3BusyTimeout] = Nm("sqlite3_busy_timeout"),
        [Sqlite3BindText]    = Nm("sqlite3_bind_text"),
        [Sqlite3BindInt]     = Nm("sqlite3_bind_int"),
        [Sqlite3Step]        = Nm("sqlite3_step"),
        [Sqlite3Reset]       = Nm("sqlite3_reset"),
        [Sqlite3Finalize]    = Nm("sqlite3_finalize"),
        [Sqlite3ColumnText]  = Nm("sqlite3_column_text"),
        [Sqlite3ColumnBlob]  = Nm("sqlite3_column_blob"),
        [Sqlite3ColumnBytes] = Nm("sqlite3_column_bytes"),
        [Sqlite3ThreadSafe]  = Nm("sqlite3_threadsafe"),
        [Sqlite3ErrMsg]      = Nm("sqlite3_errmsg"),
        // zstd is statically linked but NOT exported on Windows (0 ZSTD_* names in the export table), so both
        // rows are anchored inside the exported `?DecompressBuffer@ZstdCompressionProvider@...` @rva 0x1aa230,
        // whose body is the Linux @0x649270 one call for call: the zero-argument factory whose result is
        // stashed in TLS and handed to the five-argument decompressor is ZSTD_createDCtx.
        [ZstdCreateDCtx]     = At(),
        [ZstdDecompressDCtx] = At(),
        [SoundPlayOn3DVoice]     = Nm("?PlaySourceOn3DVoice@CExoSoundInternal@@QEAAXPEAVCExoSoundSourceInternal@@PEAVC3DVoice@@@Z"),
        [SoundPlayOn2DVoice]     = Nm("?PlaySourceOn2DVoice@CExoSoundInternal@@QEAAXPEAVCExoSoundSourceInternal@@PEAVC2DVoice@@@Z"),
        [SoundSourceSetPosition] = Nm("?SetPosition@CExoSoundSourceInternal@@QEAAXVVector@@@Z"),
        [SoundSetEnvironment]    = Nm("?SetEnvironment@CExoSound@@QEAAXE@Z"),
        [SoundInitialize]        = Nm("?Initialize@CExoSoundInternal@@QEAAHVCExoString@@EE@Z"),
        [StreamFadeAndStop]      = Nm("?FadeAndStop@CExoStreamingSoundSource@@QEAAXI@Z"),
        [StreamCtor]             = Nm("??0CExoStreamingSoundSource@@QEAA@W4Type@0@@Z"),
        [StreamReset]            = Nm("?Reset@CExoStreamingSoundSource@@QEAAXW4Type@1@@Z"),
        [StreamStartFade]        = Nm("?StartFade@CExoStreamingSoundSource@@QEAAXW4FadeType@1@IEE@Z"),
        [StreamIsPlaying]        = Nm("?IsPlaying@CExoStreamingSoundSource@@QEAAHXZ"),
        [StreamStop]             = Nm("?Stop@CExoStreamingSoundSource@@QEAAXXZ"),
        [StreamGetVolume]        = Nm("?GetVolume@CExoStreamingSoundSource@@QEAAEXZ"),
        [StreamSetLooping]       = Nm("?SetLooping@CExoStreamingSoundSource@@QEAAXH@Z"),
        // --- the CX inventory screen (CX-146 §10). CPanelInventory, CGuiInvbox,
        //     CGuiMulti*Invbox, CNWCItem and CNWCMessage are all unexported on nwmain.exe, so each row is an
        //     RVA read out of the Windows BODY (not a caller-shape guess): SetInventoryVisible's own body names
        //     SetCharacterData / GetNWCMessage / the Status sender, Update_GuiRepository's names AddItem and
        //     GetEquipSlot, and each sender was confirmed by its (major, minor) immediate at rva 0x7c25c0.
        //     Only CNWBaseItemArray is shared engine and therefore a name export.
        [InvSetInventoryVisible] = At(),   // container flag +0x2f64, shown oid +0x2f68 (Linux +0x319c / +0x31a0)
        [InvSetCharacterData]    = At(),   // gold creature+0x1b8, weight +0x28c; label m_pString at label+0x90
        [GetInventoryPanel]      = At(),
        [GameCameraHitCheck]     = At(),   // body read: GetModuleCamera, info+0x20 = 1, camera vtbl +0x60
        [GetGameobjectHit]       = At(),   // body read: kind +0x24, gob +0x48, item -> +0x48
        // ---- CX-120 seam-scene ----
        [NewCAurScene]           = Nm("?NewCAurScene@@YAPEAVCAurScene@@PEBD@Z"),
        [NewCAurCamera]          = Nm("?NewCAurCamera@@YAPEAVCAurCamera@@PEBD@Z"),
        [CAurSceneSetAmbient]    = Nm("?SetAmbient@CAurScene@@QEAAXVVector@@@Z"),
        [FindScene]              = Nm("?FindScene@@YAPEAVScene@@PEBD@Z"),
        [WindManagerCtor]        = Nm("??0WindManager@@QEAA@XZ"),
        [AuroraSetTileFoggedByWar] = Nm("?AuroraSetTileFoggedByWar@@YAXAEAVVector@@_N@Z"),
        [SetAlphaObject]         = Nm("?setalpha@@YAXPEAVCAurObject@@M@Z"),
        [SendBarterStart]        = At(),   // 35 / 1
        [SendInputUseItem]       = At(),   // 6 / 9; the Vector by pointer
        [SendGoldTransfer]       = At(),   // 8 / 1, minor 0x0a / 0x16; the Vector by pointer
        [QuickButtonSetItem]     = At(),   // body read: CGuiQuickButton drop handler (vtbl +0xe8) calls it with 0xff00ff, 0
        [ItemPlayDropSound]      = At(),   // the same handler's next call
        [InvboxAddItem]          = At(),   // page +0x1d0, rows +0x194 on the Windows CGuiMultiInvbox
        [InvSetContainerGuiVisible] = At(), // body read: mode +0x2f64, grid +0x24b0, isItem +0x2690, oid +0x268c, Status 0x7bdeb0
        [InvContainerPageUp]     = At(),   // body read: grid +0x24b0, SelectNextPage 0x7bde70 (25 / 2), page +0x2680, count +0x2f60
        [InvContainerPageDown]   = At(),   // body read: the same, SelectPreviousPage 0x7bde90 (25 / 3)
        [InvContainerPagenum]    = At(),   // body read: count +0x2f60, page clamp +0x2680; called from 0x79ceb0 (twice) and the 'P' of 0x7b3790
        [ItemActivateRadialMenu] = At(),
        [ItemGetName]            = At(),   // sret: Win64 (this, out, int) -- measured in the body
        [GetBaseItem]            = Nm("?GetBaseItem@CNWBaseItemArray@@QEBAPEAVCNWBaseItem@@H@Z"),
        [SendInvEquip]           = At(),   // 12 / 1
        [SendInvUnequip]         = At(),   // 12 / 7
        [SendInvRepositoryMove]  = At(),   // 12 / 9
        [SendInvDrop]            = At(),   // 12 / 3
        [SendGuiInvStatus]       = At(),   // 13 / 1
        [SendGuiInvSelectPanel]  = At(),   // 13 / 2
        [SendInputUseObject]     = At(),   // 6 / 11
        [SendInputSplitItem]     = At(),   // 6 / 23
        [SendInputMergeItem]     = At(),   // 6 / 22

        // ---- CX-109/110 ---- both name-exported (Camera and CNWTileSet are in nwmain.exe's export table,
        // confirmed against it: ?RenderScene@Camera@@UEAAXXZ and ?GetEnvMapResRef@CNWTileSet@@QEAA?AVCResRef@@XZ).
        [CameraRenderScene]      = Nm("?RenderScene@Camera@@UEAAXXZ"),
        [TileSetGetEnvMapResRef] = Nm("?GetEnvMapResRef@CNWTileSet@@QEAA?AVCResRef@@XZ"),
        [CameraUpscaleViewport]  = Nm("?UpscaleViewport@Camera@@QEAA_NPEAM00@Z"),
        [WindUpdateTimer]        = Nm("?updateTimer@WindManager@@QEAAXM@Z"),

        // ---- CX-120 seam-obj ---- read straight off nwmain.exe's export table.
        [NewCAurObject]    = Nm("?NewCAurObject@@YAPEAVCAurObject@@PEBD0PEAU_iobuf@@@Z"),
        [NewCAurString]    = Nm("?NewCAurString@@YAPEAVCAurString@@H@Z"),
        [SetShadowsPart]   = Nm("?SetShadows@@YAXPEAVPart@@H@Z"),
        [AurSetDynamicProjectionLight] = Nm("?AurSetDynamicProjectionLight@@YAXPEAVCAurObject@@@Z"),
        [LerpFloatRefresh] = Nm("?Refresh@LerpFloat@@AEAAXXZ"),
        [YawPitchRoll]     = Nm("?YawPitchRoll@@YA?AVQuaternion@@MMM@Z"),
        [AurSetPalette]           = Nm("?AurSetPalette@@YAHHPEBD@Z"),
        [ControlPart]             = Nm("?controlpart@@YAPEBDPEAD@Z"),
        [AurClearPalette]         = Nm("?AurClearPalette@@YAXXZ"),
        [AurCreatePaletteTexture] = Nm("?AurCreatePaletteTexture@@YAXXZ"),
        [ListAddCAurObject] = Nm("?Add@?$CExoArrayList@PEAVCAurObject@@@@QEAAXPEAVCAurObject@@@Z"),
        [ListAddCAurPart]   = Nm("?Add@?$CExoArrayList@PEAVCAurPart@@@@QEAAXPEAVCAurPart@@@Z"),
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

    /// <summary>Every symbol carried by address on Windows -- what a later build's file must re-anchor.</summary>
    internal static IEnumerable<string> Anchored
    {
        get
        {
            foreach (var kv in _win)
                if (kv.Value.At) yield return kv.Key;
        }
    }

    /// <summary>Join <c>/knobs</c>, beside the layout counter. Once, from the host.</summary>
    public static void Register() => KnobSurface.Note(() =>
    {
        int nm = 0, at = 0, only = 0;
        foreach (Win w in _win.Values)
        {
            if (w.Name is not null) nm++;
            else if (w.At) at++;
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
            if (LinuxAnchored.Contains(linux))
            {
                int lva = ClientBuilds.Lva(linux);   // build-locked: Current is the build whose build-id is running
                IntPtr at = EngineLayout.ImageBase + lva;
                return lva != 0 && EngineLayout.ImageBase != IntPtr.Zero && EngineLayout.InImage(at) ? at : IntPtr.Zero;
            }
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

        if (!_win.TryGetValue(linux, out Win w) || (w.Name is null && !w.At))
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
        int rva = ClientBuilds.Rva(linux);
        return BuildMatches(log) && rva != 0 ? Base + rva : IntPtr.Zero;   // RVA path is build-locked
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
            string why = !OperatingSystem.IsWindows()
                ? LinuxAnchored.Contains(sym) ? "no lva line for this build, or its address is outside the loaded image" : "not exported by this client build"
                : !_win.TryGetValue(sym, out Win w) || (w.Name is null && !w.At) ? Why(w)
                : w.Name is not null ? $"Windows export {w.Name} missing"
                : BuildMatches(null) ? $"build file {ClientBuilds.Current?.Name} carries no RVA for it (docs/update-day.md)"
                : $"nwmain.exe build 0x{_stamp:x8} != expected 0x{ClientBuilds.Current?.Stamp ?? 0:x8} (rot: docs/update-day.md)";
            log.Always($"{feature}: standing down as a unit -- {sym}: {why}");
            return false;
        }
        return true;
    }

    /// <summary>
    /// The loaded module's PE TimeDateStamp must equal the running <see cref="ClientBuilds.Current"/>'s, else the
    /// RVAs address a different build. Checked once; on mismatch all RVA hooks skip.
    /// </summary>
    private static bool BuildMatches(ExtenderLog? log)
    {
        if (_buildOk is bool cached) return cached;
        byte* b = (byte*)Base;
        int lfanew = *(int*)(b + 0x3c);                 // DOS header e_lfanew
        _stamp = *(uint*)(b + lfanew + 4 + 4);          // PE sig(4) + COFF: TimeDateStamp at +4
        uint want = ClientBuilds.Current?.Stamp ?? 0;
        bool ok = _stamp == want;
        if (!ok)
        {
            log?.Info($"symbol: nwmain.exe build 0x{_stamp:x8} != expected 0x{want:x8}; " +
                      "RVA hooks disabled (docs/update-day.md)");
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

    private const uint MB_ICONWARNING = 0x30;  // not-an-offset: a Win32 MessageBox flag

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

}
