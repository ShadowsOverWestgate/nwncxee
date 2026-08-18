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
    // window focus (CClientExoApp)
    public const string OnGainFocus       = "_ZN13CClientExoApp11OnGainFocusEv";
    public const string OnLostFocus       = "_ZN13CClientExoApp11OnLostFocusEv";
    // chat prompt (typing gate)
    public const string SetFocusChatPrompt   = "_ZN10CGuiInGame18SetFocusChatPromptEv";           // chat opened by key
    public const string ChatHandleLMouseDown = "_ZN20CGuiInGameChatPrompt24HandleLMouseDownGuiEventEv"; // clicked into
    public const string ChatHandleEnter      = "_ZN20CGuiInGameChatPrompt11HandleEnterEv";
    public const string ChatHandleEsc        = "_ZN20CGuiInGameChatPrompt9HandleEscEv";
    public const string ChatHandleLoseFocus  = "_ZN20CGuiInGameChatPrompt41HandleLoseKeyboardFocusLMouseDownGuiEventEv";
    // client<->server messages (CNWCMessage)
    public const string HandleServerToPlayerMessage = "_ZN11CNWCMessage27HandleServerToPlayerMessageEPhj";
    public const string HandleServerToPlayerLogin   = "_ZN11CNWCMessage25HandleServerToPlayerLoginEh";
    public const string SendPlayerToServerMessage   = "_ZN11CNWCMessage25SendPlayerToServerMessageEhhPhj";
    public const string SendDeviceAdvertiseProperty = "_ZN11CNWCMessage41SendPlayerToServerDeviceAdvertisePropertyERK10CExoStringi";
    // options panel (repurposed Save-Character button)
    public const string PostAttachmentInitialize = "_ZN13COptionsPanel24PostAttachmentInitializeEv";
    public const string HandleSaveCharButton     = "_ZN13COptionsPanel20HandleSaveCharButtonEv";
    public const string OptionsHandleXButton     = "_ZN13COptionsPanel13HandleXButtonEv";   // closes the options menu (what Resume calls)
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

    // cursor override
    public const string UpdateMouseCursorAppearance = "_ZN21CClientExoAppInternal27UpdateMouseCursorAppearanceEi";
    public const string SetMouseCursor = "_ZN25CCachedMouseCursorManager14SetMouseCursorEiiiii";

    // quickbar positioning
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
    // build (mangled), so nk_sdl_render must use its mangled name or the hook never installs.
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
    /// <summary>Byte offset of the creature's world-space m_vPosition (Vector, 3 floats) -- to verify live.</summary>
    public const int CreaturePosition = 0xa4;

    // data globals
    public const string AppManager = "g_pAppManager";
    public const string GuiMan     = "g_pGuiMan";
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
    /// restore cull around the two-sided shadow depth pass.</summary>
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
    /// <summary>Scene::RenderShadows(int mode, bool clear) @ 0x46afd0 -- the render-thread shadow pass. Hooked
    /// call-through purely as the depth-pass frame slot: its <c>this</c> (rdi) is the live <c>Scene*</c> (no
    /// Scene global exists) and GL/camera transforms are current here. ABI: <c>(Scene* /*rdi*/, int mode /*esi*/,
    /// bool clear /*dl*/)</c>.</summary>
    public const string RenderShadows = "_ZN5Scene13RenderShadowsEib";
    /// <summary>Scene::RenderDrawBucket(int bucketId) @ 0x46d060 -- draws one geometry bucket (the whole
    /// iterate+SetModelMatrix+RenderVertexData loop, DrawBucketManager @Scene+0x208). Ids 1..8 (RenderSinglePass);
    /// bucket 1 = opaque casters. Called directly to render casters into our depth FBO. ABI: <c>(Scene* /*rdi*/,
    /// int bucketId /*esi*/)</c>.</summary>
    public const string RenderDrawBucket = "_ZN5Scene16RenderDrawBucketEi";
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
    /// <summary>RenderInterface::SetModelMatrix(const Matrix4x4&amp;) @0x45d150 -- the cache-coherent front door
    /// (forwards to GLRender::SetModelMatrix). The lean bucket walker uses this per kept caster.</summary>
    public const string RiSetModelMatrix = "_ZN15RenderInterface14SetModelMatrixERK9Matrix4x4";
    /// <summary>LightManager::GetShadowLights(int) @0x5c2910 -- builds the engine's camera-prioritized shadow
    /// set. Hooked CALL-THROUGH to capture <c>this</c> while it is guaranteed alive (inside RenderShadows) and
    /// walk the MASTER light list at <c>this+0x10</c> (data) / <c>+0x18</c> (count) -- the area-wide superset
    /// SetupLightsForThisPositionAndRadius filters from -- so cube ownership is deterministic, not camera-picked.</summary>
    public const string LmGetShadowLights = "_ZN12LightManager15GetShadowLightsEi";
    /// <summary>GLRender::m_nCurrentMatrix @0x19e30d0 -- the selected matrix-stack index; saved/restored around
    /// the pass so we leave the engine's active matrix mode as we found it. Data symbol (read/write int).</summary>
    public const string MCurrentMatrix = "_ZN8GLRender16m_nCurrentMatrixE";
    /// <summary>RenderInterface::SetOrthoTransform(l,r,b,t,n,f doubles) @0x45d0f0 -- ortho into the current
    /// (PROJECTION) stack via aurMatrixStack::Orthographic (glOrtho arg order).</summary>
    public const string SetOrthoTransform = "_ZN15RenderInterface17SetOrthoTransformEdddddd";
    /// <summary>RenderInterface::SetPerspectiveTransform(fovy,aspect,near,far doubles) @0x45d0e0 -- the camera
    /// projection; used to restore it after the light depth pass. Called from Camera::RenderScene @0x4a7db1.</summary>
    public const string SetPerspectiveTransform = "_ZN15RenderInterface23SetPerspectiveTransformEdddd";
    /// <summary>GLRender::LoadShader(PROG_FIXED&amp;, const char* vs, const char* fs, bool, const char*) @0x4d3770
    /// -- links a scene shader program. STATIC. Hooked call-through to discover receiver programs: after it runs,
    /// the GL program id is at <c>PROG_FIXED+0x64</c> (`__glewCreateProgram` result); the name args identify it.
    /// ABI: <c>(PROG_FIXED* /*rdi*/, char* /*rsi*/, char* /*rdx*/, bool /*cl*/, char* /*r8*/)</c>.</summary>
    public const string LoadShader = "_ZN8GLRender10LoadShaderERNS_10PROG_FIXEDEPKcS3_bS3_";
    /// <summary>Byte offset of the linked GL program id inside PROG_FIXED (from LoadShader disasm).</summary>
    public const int ProgFixedProgramId = 0x64;

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
    /// <summary>Scene -&gt; BSPNode* root (the static-geometry BSP). Linux-verified in ManageSceneBSP.</summary>
    public const int SceneBspRoot = 0x188;
    /// <summary>BSPNode -&gt; nodedata* (the node contents / tile model). Null on empty internal nodes.</summary>
    public const int BspNodeData = 0x70;
    /// <summary>nodedata -&gt; PartTriMesh** tile-part array; count at <see cref="NodeDataPartCount"/>.</summary>
    public const int NodeDataParts = 0x20;
    /// <summary>nodedata -&gt; int tile-part count (pairs with <see cref="NodeDataParts"/>).</summary>
    public const int NodeDataPartCount = 0x28;
    /// <summary>nodedata -&gt; PartTriMesh* single "primary" mesh (fallback when the parts array is empty --
    /// the engine's <c>currentscene+0x1e0 != 0</c> fast branch uses only this).</summary>
    public const int NodeDataPrimary = 0x90;
    /// <summary>PartTriMesh -&gt; mesh data (MdlNode) pointer. The renderable-this-frame flag hangs off it.</summary>
    public const int PartMeshData = 0x8;
    /// <summary>meshdata -&gt; int "renderable/submitted this frame" flag. <c>PartTriMesh::IsPartRenderable</c>
    /// (which <c>Scene::AddPartsToDrawBuckets</c> gates every transfer on) returns false while this is 0, so a
    /// re-walked off-screen caster is dropped unless we set it -- exactly as the engine's own
    /// <c>FUN_00581000</c> does before <c>AddPartToMeshBuckets</c>.</summary>
    public const int MeshRenderableFlag = 0x118;

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
    /// <summary>Scene -&gt; the "shadow gob" (the controlled PC whose hero shadow vanilla draws). Verified in

    /// SUPERSEDED as the anchor: its inlined setter lives in the vanilla shadow path, so it reads NULL under
    /// <c>SOWX_NOSHADOWS</c> (live-confirmed). Use <see cref="CurrentCamera"/> instead.</summary>
    public const int SceneShadowGob = 0x58;

    // ---- camera-follow anchor (#65): the real, camera-INDEPENDENT PC position, independent of the vanilla shadow
    //      path. ManageSceneBSP itself walks this exact chain every frame, so it is valid whenever our call-through
    //      hook has run. All Ghidra-verified; Linux-only (skip on Windows). ----
    /// <summary>Global <c>CurrentCamera</c> (@0x19acbb0, in .dynsym) -- the symbol's 8 bytes hold a live
    /// <c>CAurCamera*</c> (or 0).</summary>
    public const string CurrentCamera = "CurrentCamera";
    /// <summary>CAurCamera -&gt; current behavior host (<c>CurrentCamera[0x8b]</c> == +0x458), a <c>CAurBehavior*</c>;
    /// its vtable+<see cref="BehaviorAsFollowVtbl"/> is <c>AsBehaviorCameraFollow()</c>.</summary>
    public const int CameraBehaviorHost = 0x458;
    /// <summary>Behavior-host vtable slot for <c>AsBehaviorCameraFollow()</c> -- pure <c>return this</c> for a follow
    /// cam, null otherwise (a base-class type filter). Replicated exactly from ManageSceneBSP's own devirt call, so
    /// non-follow cameras (free-cam/cutscene) safely yield null.</summary>
    public const int BehaviorAsFollowVtbl = 0x48;
    /// <summary>CAurBehaviorCameraFollow -&gt; <c>SafePointer&lt;CAurObject&gt;</c> to the followed object (the PC);
    /// raw pointer at +0. Its <see cref="GobPosition"/> (+0xa8) is exactly what GetTargetWorldPosition returns.</summary>
    public const int BehaviorTargetObj = 0x20;
    /// <summary>Scene -&gt; Gob** scene-gob array; count at <see cref="SceneGobCount"/>. The camera-cull loop in
    /// ManageSceneBSP walks exactly this list.</summary>
    public const int SceneGobList = 0x130;
    /// <summary>Scene -&gt; int scene-gob count (pairs with <see cref="SceneGobList"/>).</summary>
    public const int SceneGobCount = 0x138;
    /// <summary>Gob -&gt; node pointer. MUST be non-null before appending: the gobbucket sort comparator
    /// <c>FUN_0055e070</c> blindly derefs <c>*(gob+0x68)+0x8e</c> (the render path null-checks it, the sort
    /// does not) -- a gob with 0x68==0 in the bucket crashes the sort.</summary>
    public const int GobNode = 0x68;
    /// <summary>Gob -&gt; Part* part-tree root. <c>ProcessGob</c>/<c>AddPartsTreeToDrawBuckets</c> walk it; guard
    /// non-null so a geometry-less gob is skipped, not derefenced.</summary>
    public const int GobPartTree = 0x90;
    /// <summary>Gob -&gt; world position (float x@+0xa8, y@+0xac, z@+0xb0). Read for the sphere test.</summary>
    public const int GobPosition = 0xa8;
    /// <summary>Gob -&gt; cached "in view volume" byte (<c>Gob::IsInViewVolume</c> returns exactly this). Fresh
    /// after orig ManageSceneBSP ran; we append only <c>== 0</c> gobs (the engine already added the visible ones,
    /// so this avoids a double-add). Read raw -- do NOT hook the devirt-guarded IsInViewVolume.</summary>
    public const int GobInView = 0x1b;
    /// <summary>Gob -&gt; "geometry dirty" byte. <c>ProcessGob</c> recomputes the has-geometry flag
    /// (<c>gob+0x1a4</c>, which AddPartsToDrawBuckets gates the gob draw on) only when this is non-zero, then
    /// clears it. Set it on an appended gob so its geometry flag is (re)computed this frame.</summary>
    public const int GobGeometryDirty = 0x1a5;

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

    /// <summary>
    /// PE TimeDateStamp of the nwmain.exe the RVAs were read from. A client patch shifts
    /// addresses; RVA hooks are refused unless the loaded exe matches this (see BuildMatches).
    /// </summary>
    public const uint WindowsBuildStamp = 0x68e408bc;

    // Windows resolution for a symbol. Exactly one path applies:
    //   Name != null : name-exported (MSVC mangling) -- resolve like Linux
    //   Rva  != 0    : not exported -- absolute = runtime module base + Rva (build-locked)
    //   default      : no Windows resolution -- skip with a log line (graceful no-op)
    private readonly record struct Win(string? Name, int Rva);

    private static Win Nm(string msvc) => new(msvc, 0);
    private static Win At(int rva) => new(null, rva);

    /// <summary>Linux name -> Windows resolution. A name not in here is treated as "skip" too.</summary>
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
        [OptionsHandleXButton]        = default,   // client GUI, no Windows anchor yet: skip (menu just stays open)
        [SetFocusChatPrompt]          = At(0x6fae30),
        [ChatHandleEnter]             = At(0x87b0d0),
        [ChatHandleEsc]               = At(0x87b390),
        [ChatHandleLMouseDown]        = At(0x87b450),   // Notify event enum 1
        [ChatHandleLoseFocus]         = At(0x87b4b0),   // Notify event enum 2
        [TurnOffKeyboardModal]        = At(0x6e5d80),   // anchored via CGuiEdit::SetFocus (call after KeyboardModeOn)

        // --- no Windows anchor: skip + log. These gate edge cases the OS already
        //     covers; losing them degrades gracefully (see docs/windows-symbols.md). ---
        // chat-render probe (#14): Linux-only, no Windows anchors -> skip + log.
        [AppendToMsgBuffer]   = default,
        [SetPriMsgFilter]     = default,
        [ViewportSetViewport] = default,
        [ChatDoFullScreen]    = default,
        [ChatDoSplitScreen]   = default,
        // chat suppression (#23): client GUI, Linux-only for now -> skip + log on Windows.
        [AppendToChatBuffer]  = default,
        [ChatWindowSetFilter] = default,
        [ChatWindowShow]      = default,
        [ChatPromptShow]      = default,
        [ChatPromptSetFocus]  = default,
        [SendChatTalk]        = default,
        // dialog reimplementation (#dialog): client GUI / CNWCMessage, Linux-only for now -> skip + log.
        [ShowDialogEntry]   = default,
        [ShowDialogReplies] = default,
        [ShowReplyChosen]   = default,
        [CloseDialog]       = default,
        [SendDialogReply]   = default,
        [ForceDialogVisible] = default,
        [GetGameObject]     = default,
        [GetObjectName]     = default,
        // outbound send parity (#22): Linux-only like the other chat senders -> skip + log on Windows.
        [SendChatShout]   = default,
        [SendChatWhisper] = default,
        [SendChatTell]    = default,
        [SendChatTellId]  = default,
        [SendChatParty]   = default,
        [SendChatDM]      = default,
        // chat portraits (#26): Aurora texture manager, Linux-only anchors -> skip + log on Windows.
        [AurTextureGet]    = default,
        [AurTextureGetTID] = default,
        // Tell-pinned tab roster (#45): Linux-only for now -> skip + log on Windows.
        [GetPlayerByPlayerName]   = default,
        [GetPlayerByCreatureName] = default,

        [OnGainFocus] = default,   // empty focus stub, indirect dispatch
        [OnLostFocus] = default,   // empty focus stub
        [Nuklear]     = default,   // NUI edit-focus probe (client global)

        // consent modal -- name-exported on Windows (verified in nwmain.exe's export table).
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
        // Footgun: nwmain.exe statically links the CRT, so the panel frees via the game's own
        // operator delete -- allocate from the game's own operator new (build-locked RVA) or the
        // cross-heap free corrupts. Scalar operator new(size_t), VS2017/2019 release.
        [OperatorNew]     = At(0xc10324),

        // cursor override: client-internal, no Windows anchor yet -> skip + log.
        [UpdateMouseCursorAppearance] = default,
        [SetMouseCursor] = default,

        // quickbar / compass: client GUI, no Windows anchor yet -> skip + log.
        [QuickbarReposition] = default,
        [SetPositionScreen]  = default,
        [HideCompass]        = default,
        [ShowCompass]         = default,

        // client-native manager (nuklear) -- no Windows anchor yet: skip + log (manager absent on Windows).
        [NkSdlRender]        = default,
        [NkBegin]            = default,
        [NkEnd]              = default,
        [NkLayoutRowDynamic] = default,
        [NkLabel]            = default,
        [NkButtonLabel]      = default,
        [NkWindowIsClosed]   = default,

        // shadow suppression (#65): Scene sub-passes, Linux-only -> skip + log on Windows.
        [RenderDynamicShadows]     = default,
        [RenderStaticShadows]      = default,
        [RenderShadowPlanes]       = default,
        [DoGobShadows]             = default,
        [DoDynamicMeshProjections] = default,
        // shadow-map input capture (#65 slice 2): Linux-only -> skip + log on Windows.
        [SetLightGL]               = default,
        [SetViewTransform]         = default,
        // shadow-map caster re-drive (#65 slice 3b): Linux-only -> skip + log on Windows.
        [RenderShadows]            = default,
        [RenderDrawBucket]         = default,
        [SetMatrixMode]            = default,
        [LoadMatrixIdentity]       = default,
        [MultMatrix]               = default,
        [PushCurrentMatrix]        = default,
        [PopCurrentMatrix]         = default,
        [FlagCurrentTransformDirty]= default,
        [SetModelMatrix]           = default,
        [RenderVertexData]         = default,
        [BucketGetFirstItem]       = default,
        [LmGetShadowLights]        = default,
        [BucketGetNextItem]        = default,
        [RiSetModelMatrix]         = default,
        [MCurrentMatrix]           = default,
        [SetOrthoTransform]        = default,
        [SetPerspectiveTransform]  = default,
        [GetCanKeyholeDissolve]    = default,
        [LoadShader]               = default,
        [GetPlayerCreature]        = default,
        // caster completeness (#65 slice 3c): BSP sphere re-walk, Linux-only -> skip + log on Windows.
        [ManageSceneBSP]           = default,
        [AddPartToMeshBuckets]     = default,
        [BSPTraverseSphere]        = default,
        [PartGetIsOpaque]          = default,
        // dynamic gob casters (#65 slice 3d): global bucket + Add, Linux-only -> skip + log on Windows.
        [GobBucket]                = default,
        [GobBucketAdd]             = default,
        // camera-follow anchor (#65): the camera-independent PC position, Linux-only -> skip + log on Windows.
        [CurrentCamera]            = default,
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
}
