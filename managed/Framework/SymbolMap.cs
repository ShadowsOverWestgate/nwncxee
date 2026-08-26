using System.Runtime.InteropServices;

namespace Sowx.Framework;

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
    // ---- chat edit control (build slice #20): focus suppression, keyboard-mode gate, SDL
    //      text-input + clipboard. Chatbox-specific symbols are Linux-only (no _win entries -> skip-with-log
    //      on Windows). EXCEPTIONS: ExoInput + GetKeyboardMode carry _win mappings -- the hotkey
    //      keyboard-mode gate (cross-platform) reads them, and both are name-exported on Windows. ----
    /// <summary>CGuiInGame::ShowChatPrompt() -- a chat opener we skip to suppress vanilla focus.</summary>
    public const string ShowChatPrompt = "_ZN10CGuiInGame14ShowChatPromptEv";
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
    // options panel (repurposed Save-Character button)
    public const string PostAttachmentInitialize = "_ZN13COptionsPanel24PostAttachmentInitializeEv";
    public const string HandleSaveCharButton     = "_ZN13COptionsPanel20HandleSaveCharButtonEv";
    // Closes the options menu (what Resume calls). Client GUI, no Windows anchor yet -> skips there,
    // and the menu simply stays open.
    public const string OptionsHandleXButton     = "_ZN13COptionsPanel13HandleXButtonEv";
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
    // SendPlayerToServerDialog_Reply (major 0x14, minor 0x03) with ClientApi.Message() as `this`. Linux-only
    // for now (client GUI / CNWCMessage -> no Windows anchors; skip + log there).
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

    // quickbar positioning. Quickbar and compass alike are client GUI: no Windows anchor yet -> skip + log.
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
    public const string SdlGetModState  = "SDL_GetModState";                            // live Ctrl/Shift/Alt state at capture
    public const string SdlGetMouseState = "SDL_GetMouseState";
    // client-native manager: a window injected into the client's own nuklear pass via the
    // per-frame nk_sdl_render hook. Core nk_* are plain C; the SDL glue is C++-linkage on this
    // build (mangled), so nk_sdl_render must use its mangled name or the hook never installs. The
    // manager is absent on Windows: no anchor, so the whole group skips + logs there.
    public const string NkSdlRender        = "_Z13nk_sdl_renderf";
    public const string NkBegin            = "nk_begin";
    public const string NkEnd              = "nk_end";
    public const string NkLayoutRowDynamic = "nk_layout_row_dynamic";
    public const string NkLabel            = "nk_label";
    public const string NkButtonLabel      = "nk_button_label";
    public const string NkWindowIsClosed   = "nk_window_is_closed";
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

    // data globals
    public const string AppManager = "g_pAppManager";
    public const string GuiMan     = "g_pGuiMan";
    /// <summary>The client's nuklear context (NUI edit-focus probe). Client global -- no Windows anchor.</summary>
    public const string Nuklear    = "g_pNuklear";
    /// <summary>CExoBase* global; deref for the instance whose CExoString@0x38 is the resolved user directory.</summary>
    public const string ExoBase    = "g_pExoBase";

    // ---- owned-render text (chatbox slice #19). Linux-only throwaway rig; no Windows anchors
    //      (Resolve returns Zero + logs on Windows, so the feature degrades gracefully there). ----
    // stb_truetype: the client statically links stb and exports every entry point as extern "C".
    public const string StbttInitFont              = "stbtt_InitFont";
    public const string StbttScaleForPixelHeight   = "stbtt_ScaleForPixelHeight";
    public const string StbttGetFontVMetrics       = "stbtt_GetFontVMetrics";
    public const string StbttGetCodepointHMetrics  = "stbtt_GetCodepointHMetrics";
    public const string StbttGetCodepointBitmapBox = "stbtt_GetCodepointBitmapBox";
    public const string StbttMakeCodepointBitmap   = "stbtt_MakeCodepointBitmap";
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

    // ---- shadow replacement (#65). Linux-only; no _win entries -> skip-with-log on Windows. ----
    // The three self-contained shadow sub-passes Scene::RenderShadows dispatches. No-op'ing these
    // suppresses stencil shadows while RenderShadows keeps managing all pipeline state itself
    // (its post-skip net state is runtime-dependent -- see docs/research/shadow-rendering-impl.md).
    /// <summary>Scene::RenderDynamicShadows() @ 0x4670b0 -- per-frame dynamic shadow-volume sub-pass.</summary>
    public const string RenderDynamicShadows = "_ZN5Scene20RenderDynamicShadowsEv";
    /// <summary>Scene::RenderStaticShadows() @ 0x46a3a0 -- static (world-geometry) shadow-volume sub-pass.</summary>
    public const string RenderStaticShadows  = "_ZN5Scene19RenderStaticShadowsEv";
    /// <summary>Scene::RenderShadowPlanes(int, int) @ 0x463e20 -- planar/projected shadow fallback sub-pass.</summary>
    public const string RenderShadowPlanes   = "_ZN5Scene18RenderShadowPlanesEii";
    /// <summary>Scene::DoGobShadows(PartLight*) @ 0x45fba0 -- per-light gob volume builder, dispatched
    /// directly by RenderShadows (Scene-vtable +0x1c8) alongside the Render* sub-passes.</summary>
    public const string DoGobShadows         = "_ZN5Scene12DoGobShadowsEP9PartLight";
    /// <summary>Scene::DoDynamicMeshProjections(PartLight*) @ 0x45f2e0 -- per-light dynamic mesh shadow
    /// builder, dispatched directly by RenderShadows (Scene-vtable +0x1e0).</summary>
    public const string DoDynamicMeshProjections = "_ZN5Scene24DoDynamicMeshProjectionsEP9PartLight";

    // ---- shadow-map pipeline input capture (#65 slice 2). All call-through (observe, not replace). ----
    /// <summary>SetLightGL(PartLight*, int, float) @ 0x4c0e60 -- pushes one light to GL. Reads the light's
    /// dir/pos vector at PartLight+0x64/+0x68/+0x6c, a type/discriminant int at +0x80, a scalar at +0x70
    /// (color/radius are computed via GetLightAdjusted*). Source of the sun/moon direction for the light matrix.</summary>
    public const string SetLightGL = "_Z10SetLightGLPK9PartLightif";
    /// <summary>RenderInterface::SetViewTransform(Vector&amp; pos, Quaternion&amp; rot) @ 0x45d3d0 -- camera view
    /// set each frame. STATIC (thunks to GLRender::SetViewTransform, no <c>this</c>): rdi = Vector* (camera
    /// world pos, 3 floats), rsi = Quaternion* (x,y,z,w). Clean source of camera pose. (§6.1: the camera's
    /// projection is set separately via RenderInterface::SetPerspectiveTransform, doubles, called from
    /// Camera::RenderScene @0x4a7db1 -- known fovy=45/near=0.1/far~215..1000, not currently hooked.)</summary>
    public const string SetViewTransform = "_ZN15RenderInterface16SetViewTransformER6VectorR10Quaternion";

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
    // ---- static-geometry batching (ADR 0006 phase 1), Linux-only -> BatchService stands down on Windows ----
    /// <summary>PartTriMesh::Draw() @0x4513d0 -- the per-part draw entry the bucket loop calls (via the
    /// item's Render vtable slot). BatchService's skip/record point. ABI: <c>(PartTriMesh* /*rdi*/)</c>.</summary>
    public const string PartTriMeshDraw = "_ZN11PartTriMesh4DrawEv";
    /// <summary>#137: PartSkin::Draw() @0x4830b0 -- the TRUE skinmesh's draw entry (a cloak, a robe, a
    /// tail). It never enters <see cref="PartTriMeshDraw"/>: it calls <c>RenderSkinned</c> @0x482c70, which
    /// gathers the bone chain and hands <c>ExecuteSkinGPU</c> @0x482800 a pose to write into
    /// <see cref="GlRenderBones"/> before ending in the same <c>RenderFlat</c>/<c>RenderEnvironmentMapped</c>
    /// pair every rigid part uses. So this is the second capture point the part table needs, and calling the
    /// trampoline with the funnel swallowed is how the owned pass gets a live pose without a draw.
    /// ABI: <c>(PartSkin* /*rdi*/)</c>.</summary>
    public const string PartSkinDraw = "_ZN8PartSkin4DrawEv";
    /// <summary>#125: Scene::RenderSkyBoxes() @0x466910 -- the whole per-area skybox draw, called from
    /// RenderSinglePass between buckets 0 and 1. Three gobs (current, cross-fading, skyfade) rendered under
    /// their own projection with the depth mask off. Hooked to open the owned pass's capture window and to
    /// colour-mask the engine's copy once the owned pass draws it at its graph slot.</summary>
    public const string RenderSkyBoxes = "_ZN5Scene14RenderSkyBoxesEv";

    // ---- #130: the beam site. Beams are NOT walked by Scene::RenderDrawBucket -- Scene::RenderBeams
    //      @0x46aba0 (vt+0x1d8) walks DRAW BUCKET 15 with its own program and its own state, right after
    //      bucket 4, and it is the last content class in the pass before particles.
    /// <summary>#130: Scene::RenderBeams() @0x46aba0 -- the volumetric light-beam pass. Gated on the
    /// <c>enablebeams</c> global; picks the first scene light with range >= 1000 (the sun/moon) and extrudes
    /// every bucket-15 item away from it through <c>vs_beamvol</c>/<c>fs_beamvol</c>, additive
    /// <c>(SRC_ALPHA, ONE)</c> with the depth mask off. Hooked to open the owned pass's capture window and to
    /// colour-mask the engine's copy once the owned pass draws it at its graph slot.
    /// ABI: <c>(Scene* /*rdi*/)</c>.</summary>
    public const string RenderBeams = "_ZN5Scene11RenderBeamsEv";
    /// <summary>#130: PartBeam::Draw() @0x450120 -- the whole of it is
    /// <c>RenderVertexData(part+0xa0, part+0xa8, 4, 0xffffffff)</c>, so it is both the per-beam capture point
    /// and the identity the owned replay keys on. It is NOT <c>PartTriMesh::Draw</c>, so BatchService's part
    /// hook never sees a beam. ABI: <c>(PartBeam* /*rdi*/)</c>.</summary>
    public const string PartBeamDraw = "_ZN8PartBeam4DrawEv";
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
    /// <summary>Scene::m_bGrassIsEnabled @0x19ab970 -- the player's "Grass Mode" option, and the flag
    /// <c>DoBinBucket</c> gates its whole body on. The owned pass honours it: grass off means the owned
    /// draw does nothing AND does not suppress, so the option keeps meaning what it says.</summary>
    public const string GrassEnabled = "_ZN5Scene17m_bGrassIsEnabledE";
    /// <summary>#134: <c>PartDanglyMesh</c>'s vtable. Not a hook -- an identity. A dangly part reaches the
    /// <c>PartTriMesh::Draw</c> hook (its own Draw @0x451b89 calls straight into it) and must be told apart
    /// from a rigid one, which no field on the part answers. The Itanium ABI stores the address of the
    /// vtable's function-pointer area, 0x10 past the symbol, in the object's first word.</summary>
    public const string PartDanglyMeshVtbl = "_ZTV14PartDanglyMesh";
    /// <summary>RenderInterface::SetCanDissolve(int) @0x45ccd0 -- the bucket loop sets this per item from
    /// Camera::GetCanKeyholeDissolve right before the item renders; latched to know each part's dissolve
    /// state without re-calling the camera test.</summary>
    public const string RiSetCanDissolve = "_ZN15RenderInterface14SetCanDissolveEi";
    /// <summary>Material::HasEnvMap @0x43d1a0-ish -- env-mapped parts take RenderEnvironmentMapped, not
    /// RenderFlat; excluded from batching.</summary>
    public const string MatHasEnvMap = "_ZN8Material9HasEnvMapEv";
    /// <summary>Material::HasBumpyShinyMap -- bumpy-shiny parts take RenderBumpyShiny; excluded.</summary>
    public const string MatHasBumpyShinyMap = "_ZN8Material16HasBumpyShinyMapEv";
    /// <summary>PartTriMesh::GetRenderHint() @0x43ce90 -- RenderFlat feeds this to SetRenderHint per draw
    /// (program selection input); part of the batch group key.</summary>
    public const string PartGetRenderHint = "_ZN11PartTriMesh13GetRenderHintEv";
    /// <summary>Material::GetTexture(int) @0x4c5810 -- texture slot i (SharedMaterial override at
    /// mat+0xd0 wins, else own slot at mat+i*0x10). Batch group key = material CONTENT (each part owns
    /// its Material instance, so pointer identity shatters groups to size 1).</summary>
    public const string MatGetTexture = "_ZN8Material10GetTextureEi";
    /// <summary>Part::~Part() @0x437720 (D1 and D2 share the address) -- the BASE destructor every part
    /// class tail-jumps to: PartSkin and PartDanglyMesh jump to PartTriMesh::~PartTriMesh, which jumps here
    /// with <c>this</c> unchanged (verified in the disassembly, no thunk adjust). #145: the one event that
    /// says a recorded part is GONE. Without it the part table holds the pointer until something happens to
    /// notice, and the owned pass chases it into freed memory.</summary>
    public const string PartDtor = "_ZN4PartD2Ev";
    /// <summary>VertexDataObject::Repack() @0x45c270 -- compacts tombstoned items (RemoveItem only zeroes
    /// sizes), MOVING the surviving items' indices and offsets. Triggered by AddItem on fragmentation, so
    /// streaming fires it mid-play; every batch member of that VDO must recapture its item.</summary>
    public const string VdoRepack = "_ZN16VertexDataObject6RepackEv";
    /// <summary>GLRender::SetVertexDataObject(VertexDataObject*) @0x4cff30 -- binds (lazily creating) the
    /// VDO's VAO + element buffer and updates the engine's current-VDO cache. The owned caster path (ADR 0008)
    /// binds engine geometry through this so the cache never desyncs; null unbinds to the default VAO.</summary>
    public const string GlSetVertexDataObject = "_ZN8GLRender19SetVertexDataObjectEP16VertexDataObject";
    /// <summary>primTypeTranslationTable @0x12f50e0 (R) -- AuroraPrimitiveTypes -> GLenum table indexed by
    /// the prim id RenderVertexData receives.</summary>
    public const string PrimTypeTranslationTable = "primTypeTranslationTable";
    /// <summary>GLRender::m_bones @0x19e1f60 (B) -- the static 64 x mat4 bone array (4KB) the skinning shader
    /// runs on. <c>GLRender::SetBoneMatrix</c> @0x4cbbb0 runs each matrix through
    /// <c>Transpose(const Matrix4x4&amp;)</c> @0x5b5570 before storing it, and <c>GLRender::SetUniforms</c>
    /// @0x4ccbf0 then uploads the array with <c>glUniformMatrix4fv(loc, 64, transpose=0, m_bones)</c> -- so
    /// what SITS in m_bones is already in the column-major order GLSL wants, and a raw memcpy of it into an
    /// std430 <c>mat4[]</c> is byte-identical to what the engine feeds its own shader. The transpose is the
    /// engine's business, not ours: we copy the stored bytes and multiply in the same order its shader does.
    /// Snapshotted per skinned part draw by #96.</summary>
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
    /// <summary>GLRender::m_fog @0x19ad160 (B) -- the area's fog block, read by <c>GLRender::SetUniforms</c>
    /// @0x4ccbf0 to build the engine's own <c>fogParams</c>/<c>fogColor</c>: +0x00 start, +0x04 end,
    /// +0x08 colour (vec4, DISPLAY space -- the stock shader lerps toward it after the encode), +0x18 the
    /// enable flag. <c>fogParams.w</c> is not stored -- SetUniforms computes <c>1/(end - start)</c>.</summary>
    public const string GlFog = "_ZN8GLRender5m_fogE";
    /// <summary>GLRender::m_globalShaderConstantsf @0x19af8a0 (B) -- the float side of the engine's global
    /// shader constants. [21] and [22] are the two keyhole diameters; SetUniforms uploads the MIN of the pair
    /// as <c>keyholeDiameterMin</c> and the MAX as <c>keyholeDiameterMax</c>, so the pair is unordered in
    /// storage and composed at upload. [23] is <c>keyholeMinZOffset</c>, which the generation of
    /// <c>inc_keyhole.shd</c> this client runs never reads.</summary>
    public const string GlGlobalShaderConstantsF = "_ZN8GLRender24m_globalShaderConstantsfE";
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
    /// <summary>GLRender::RenderVertexData(VertexDataObject*, uint, Aurora::AuroraPrimitiveTypes, uint, uint)
    /// @0x4d0570 -- STATIC (no this): the terminal per-part draw. Hooked to SKIP (early-return) a caster the cube
    /// pass sphere-culled out of the current light's range. ABI: rdi=vdo, esi, edx=primType, ecx, r8d.</summary>
    public const string RenderVertexData = "_ZN8GLRender16RenderVertexDataEP16VertexDataObjectjN6Aurora20AuroraPrimitiveTypesEjj";
    /// <summary>DrawBucketManager::GetFirstItem(int bucket, int phase) @0x47bed0 -- returns a pointer to the
    /// iterator slot; the item is <c>*ret</c> (null = phase empty). <c>this</c> = Scene+0x208. Phases 0..2
    /// (opaque / two-sided / front-cull -- Scene::RenderDrawBucket walks all three).</summary>
    public const string BucketGetFirstItem = "_ZN17DrawBucketManager12GetFirstItemEii";
    /// <summary>DrawBucketManager::GetNextItem(int bucket, int phase) @0x47be90 -- advance the same iterator;
    /// item is <c>*ret</c> (null = done). Pairs with <see cref="BucketGetFirstItem"/>.</summary>
    public const string BucketGetNextItem = "_ZN17DrawBucketManager11GetNextItemEii";
    /// <summary>DrawBucketManager::HasItemsOfType(int itemType) @0x47bef0 -- does this bucket hold anything,
    /// in any of its three phases? Two call sites, BOTH inside <c>Scene::RenderSinglePass</c> @0x46e5f0
    /// (verified by xref): one gates the framebuffer-sampling block (type 5) and one the water block
    /// (type 6). Each block is a <c>Camera::CopyRenderSubframe</c> followed by the bucket walk, so answering
    /// "no" for type 6 removes the engine's water copy AND its walk together -- which is what makes #129's
    /// one-copy claim structural rather than a second suppression to keep in step.
    /// <para>The manager itself is <c>Scene+0x208</c>, and its buckets are a flat array: item type
    /// <c>t</c>, phase <c>p</c> lives at <c>mgr + (t*3 + p)*0x10</c> as {<c>Part** items</c> @+0,
    /// <c>int count</c> @+8} (<c>GetDrawBucketByItemType</c> @0x47bd60 / <c>GetNextItem</c> @0x47be90). Read
    /// directly rather than through the iterator pair: those mutate the manager's own cursor at +0x300, and
    /// the engine is walking with it.</para></summary>
    public const string BucketHasItemsOfType = "_ZN17DrawBucketManager14HasItemsOfTypeEi";
    /// <summary>Global <c>g_nEnableBumpyWater</c> @0x19ace10 (B) -- the int
    /// <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 ANDs with <c>Material::HasBumpyShinyMap</c> before
    /// routing a part to draw bucket 6. Data symbol; read, never write.</summary>
    public const string EnableBumpyWater = "g_nEnableBumpyWater";
    /// <summary>RenderInterface::SetModelMatrix(const Matrix4x4&amp;) @0x45d150 -- the cache-coherent front door
    /// (forwards to GLRender::SetModelMatrix). The lean bucket walker uses this per kept caster.</summary>
    public const string RiSetModelMatrix = "_ZN15RenderInterface14SetModelMatrixERK9Matrix4x4";
    /// <summary>Global shader param scalar getter -- param 0x12 is the light-radius multiplier
    /// GetLightAdjustedRadius applies (adjR = range * intensity * overbright * this).</summary>
    public const string RiGetGlobalShaderParamf = "_ZN15RenderInterface21GetGlobalShaderParamfEN6Aurora23AurGlobalShaderParamf_tE";
    /// <summary>Global shader param INTEGER getter -- param 0 is the gamma pipeline, the engine's own gate on
    /// the colour-space round trip (it becomes <c>#define GAMMA_CORRECTION %d</c> in every shader preamble,
    /// and <c>ApplyColorSpace</c>/<c>RevertColorSpace</c> in inc_common.shd are identity when it is 0).
    /// #132's resolve reads it so the one remaining encode is gated where the per-shader encodes never
    /// were.</summary>
    public const string RiGetGlobalShaderParami = "_ZN15RenderInterface21GetGlobalShaderParamiEN6Aurora23AurGlobalShaderParami_tE";
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
    /// <summary>RenderInterface::SetPerspectiveTransform(fovy,aspect,near,far doubles) @0x45d0e0 -- the camera
    /// projection; used to restore it after the light depth pass. Called from Camera::RenderScene @0x4a7db1.</summary>
    public const string SetPerspectiveTransform = "_ZN15RenderInterface23SetPerspectiveTransformEdddd";

    // ---- shadow-map caster completeness (#65 slice 3c). The camera-frustum cull is inlined in the BSP
    //      descent (no per-object hook); the reference mod's proven fix re-walks the BSP in a sphere around
    //      the player and re-submits casters via the engine's own AddPartToMeshBuckets, so the depth pass
    //      sees casters the camera culled. All Linux-verified via Ghidra; no _win entries (skip on Windows). ----
    /// <summary>ManageSceneBSP(Scene*) @0x47fa80 -- the per-frame scene-BSP collection driver (camera-culled
    /// populate of the GLOBAL mesh buckets). Runs immediately before <c>Scene::AddPartsToDrawBuckets</c> in
    /// <c>Scene::Render</c>. Hooked call-through: after the original runs we re-walk the BSP in a sphere and
    /// re-submit off-screen tileset casters, which the next <c>AddPartsToDrawBuckets</c> then transfers into
    /// the DrawBucketManager (type 0) + matrix-sets for free. Free fn: <c>(Scene* /*rdi*/)</c>.</summary>
    public const string ManageSceneBSP = "_Z14ManageSceneBSPP5Scene";
    /// <summary>Scene::AddPartsToDrawBuckets() @0x4751d0 -- transfers this frame's collected mesh buckets into
    /// the DrawBucketManager. Runs in <c>Scene::Render</c> after <c>ManageSceneBSP</c> and BEFORE the virtual
    /// render dispatch (vtbl+0x218, which contains RenderShadows + the bucket draws) -- so its return is the
    /// one deterministic per-frame point where the buckets are complete and nothing has drawn yet (ADR 0007
    /// phase 1: all extender passes fire here, never on engine cadence). Member fn: <c>(Scene* this)</c>.</summary>
    public const string AddPartsToDrawBuckets = "_ZN5Scene21AddPartsToDrawBucketsEv";
    /// <summary>AddPartToMeshBuckets(PartTriMesh*) @0x47f450 -- the engine's own caster submit: pushes a tile
    /// part into the global <c>meshbuckets[textureTID]</c> + <c>meshshadowbucket</c>. Our BSP re-walk callback
    /// calls it exactly as the engine's <c>ProcessTriMeshParts</c> does. NO dedup (caller's job); it also
    /// advances the part's animation by DeltaT, so only submit STATIC OPAQUE tile parts (a double-advance on
    /// an animated part slightly speeds it; a transparent double-submit double-blends). Free fn:
    /// <c>(PartTriMesh* /*rdi*/)</c>.</summary>
    public const string AddPartToMeshBuckets = "_Z20AddPartToMeshBucketsP11PartTriMesh";
    /// <summary>BSPTraverse(BSPNode*, Vector const&amp;, float radiusSq, void(*)(BSPNode*, Vector const&amp;, float))
    /// @0x5b2440 -- sphere-bounded BSP walk (camera-independent, every direction). The 3rd arg is radius
    /// <em>SQUARED</em> (compared against signed-squared node split-plane distances -- pass r*r, not r). The
    /// callback fires on internal nodes too; <c>node+0x20 != 0</c> marks a leaf (stop). ABI:
    /// <c>(BSPNode* /*rdi*/, Vector* /*rsi*/, float /*xmm0*/, cb /*rdx*/)</c>; callback ABI
    /// <c>(BSPNode* /*rdi*/, Vector* /*rsi*/, float /*xmm0*/)</c> (no context pointer -- state via statics).</summary>
    public const string BSPTraverseSphere = "_Z11BSPTraverseP7BSPNodeRK6VectorfPFvS0_S3_fE";
    /// <summary>PartTriMesh::GetIsOpaque() @0x43cee0 -- true if the part is opaque. The re-walk submits only
    /// opaque tile parts. ABI: <c>(PartTriMesh* /*rdi*/) -> bool</c>.</summary>
    public const string PartGetIsOpaque = "_ZN11PartTriMesh11GetIsOpaqueEv";

    // ---- dynamic gob casters (slice 3d, #65). ManageSceneBSP's second loop adds ONLY camera-visible gobs
    //      (gob+0x1b) to the global gobbucket; we append off-screen in-sphere gobs there so movable objects
    //      cast even when off-camera. Scene::AddPartsToDrawBuckets sorts + processes gobbucket into buckets 2/3.
    //      All Linux-verified via Ghidra; no _win entries (skip on Windows). ----
    /// <summary>The global render gobbucket -- a <c>CExoArrayList&lt;Gob*&gt;</c> the engine clears + rebuilds
    /// each frame in <c>ManageSceneBSP</c> (in-view gobs only) and consumes in <c>AddPartsToDrawBuckets</c>. The
    /// symbol's ADDRESS is the list's <c>this</c> (data ptr @+0, count @+8, capacity @+0xc).</summary>
    public const string GobBucket = "gobbucket";
    /// <summary>CExoArrayList&lt;Gob*&gt;::Add(Gob*) @0x445f50 -- append one gob (may realloc). ABI:
    /// <c>(CExoArrayList&lt;Gob*&gt;* /*rdi*/, Gob* /*rsi*/)</c>.</summary>
    public const string GobBucketAdd = "_ZN13CExoArrayListIP3GobE3AddES1_";

    // ---- camera-follow anchor (#65): the real, camera-INDEPENDENT PC position, independent of the vanilla shadow
    //      path. ManageSceneBSP itself walks this exact chain every frame, so it is valid whenever our call-through
    //      hook has run. All Ghidra-verified; Linux-only (skip on Windows). ----
    /// <summary>Global <c>CurrentCamera</c> (@0x19acbb0, in .dynsym) -- the symbol's 8 bytes hold a live
    /// <c>CAurCamera*</c> (or 0).
    /// <para>#131 also reads the orientation QUATERNION at <c>*CurrentCamera + 0xbc</c>, stored
    /// (w, x, y, z) -- <c>PartEmitter::RenderEmitter</c> builds every billboard basis out of it.</para>
    /// <para>This SUPERSEDED the Scene "shadow gob" at Scene+0x58 as the anchor: that field's setter is
    /// inlined in the vanilla shadow path, so it reads NULL under <c>SOWX_NOSHADOWS</c> (live-confirmed).</para>
    /// </summary>
    public const string CurrentCamera = "CurrentCamera";

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
    /// <summary>The two texture vtables, which is the whole of "is this pointer still a texture" --
    /// see <see cref="AurTex"/>. Data symbols, never called: what is read is the value a live object's
    /// first word holds, i.e. the symbol plus the Itanium ABI's 0x10.</summary>
    public const string AurTextureVtbl = "_ZTV11CAurTexture";
    public const string AurTextureBasicVtbl = "_ZTV16CAurTextureBasic";

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
    public const string ResManRelease      = "_ZN10CExoResMan7ReleaseEP4CRes";
    public const string ResManReleaseObj   = "_ZN10CExoResMan16ReleaseResObjectEP4CResb";
    public const string CResCtor           = "_ZN4CResC1Ev";
    /// <summary>libstdc++ <c>operator new(size_t)</c>: the CRes we hand the resman is freed by the engine's
    /// own delete, so it must come from the engine's allocator. Linux shares one heap, so this is that.</summary>
    public const string CxxOperatorNew     = "_Znwm";

    /// <summary>
    /// PE TimeDateStamp of the nwmain.exe the RVAs were read from. A client patch shifts
    /// addresses; RVA hooks are refused unless the loaded exe matches this (see BuildMatches).
    /// </summary>
    public const uint WindowsBuildStamp = 0x68e408bc;

    // Windows resolution for a symbol. Exactly one path applies:
    //   Name != null : name-exported (MSVC mangling) -- resolve like Linux
    //   Rva  != 0    : not exported -- absolute = runtime module base + Rva (build-locked)
    //   absent       : no Windows resolution -- skip with a log line (graceful no-op)
    private readonly record struct Win(string? Name, int Rva);

    private static Win Nm(string msvc) => new(msvc, 0);
    private static Win At(int rva) => new(null, rva);

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

        // --- not exported (client GUI / CNWCMessage / COptionsPanel): Windows RVA,
        //     build 0x68e408bc. See docs/windows-symbols.md for how each was anchored. ---
        [HandleServerToPlayerMessage] = At(0x7a41a0),
        [HandleServerToPlayerLogin]   = At(0x7a31b0),
        [SendPlayerToServerMessage]   = At(0x7c25c0),
        [SendDeviceAdvertiseProperty] = At(0x7bbac0),
        [GetNWCMessage]               = At(0x6abf90),
        [PostAttachmentInitialize]    = At(0x830b50),
        [HandleSaveCharButton]        = At(0x82fb60),
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
        [SdlGetModState]  = Nm("SDL_GetModState"),
        // hotkey suppression while any vanilla text field captures keys (console etc.) -- shared-engine
        // CExoInput, name-exported on Windows. (Chat also uses ExoInput but stays Linux-only for now.)
        [ExoInput]        = Nm("?g_pExoInput@@3PEAVCExoInput@@EA"),
        [GetKeyboardMode] = Nm("?GetKeyboardMode@CExoInput@@QEAAHXZ"),
    };

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
            log?.Info($"symbol: no Windows resolution for {linux}; skipping");
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
    /// The loaded module's PE TimeDateStamp must equal <see cref="WindowsBuildStamp"/>, else the
    /// RVAs address a different build. Checked once; on mismatch all RVA hooks skip.
    /// </summary>
    private static bool BuildMatches(ExtenderLog? log)
    {
        if (_buildOk is bool cached) return cached;
        byte* b = (byte*)Base;
        int lfanew = *(int*)(b + 0x3c);                 // DOS header e_lfanew
        uint stamp = *(uint*)(b + lfanew + 4 + 4);      // PE sig(4) + COFF: TimeDateStamp at +4
        bool ok = stamp == WindowsBuildStamp;
        if (!ok)
            log?.Info($"symbol: nwmain.exe build 0x{stamp:x8} != expected 0x{WindowsBuildStamp:x8}; " +
                      "RVA hooks disabled -- re-derive (docs/windows-symbols.md)");
        _buildOk = ok;
        return ok;
    }

}
