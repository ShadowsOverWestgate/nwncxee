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
    public const string SendChatTalk        = "_ZN11CNWCMessage27SendPlayerToServerChat_TalkE10CExoString"; // outbound verify
    // outbound send parity (#22): the other five channel senders. All major 0x09; minor per §4.3.
    // Talk/Shout/Whisper/Party/DM take (this, CExoString); Tell takes (this, uint playerId, CExoString).
    public const string SendChatShout   = "_ZN11CNWCMessage28SendPlayerToServerChat_ShoutE10CExoString";        // minor 0x02
    public const string SendChatWhisper = "_ZN11CNWCMessage30SendPlayerToServerChat_WhisperE10CExoString";      // minor 0x03
    public const string SendChatTell    = "_ZN11CNWCMessage27SendPlayerToServerChat_TellEj10CExoString";        // minor 0x04, (uint playerId)
    public const string SendChatParty   = "_ZN11CNWCMessage28SendPlayerToServerChat_PartyE10CExoString";        // minor 0x06
    public const string SendChatDM      = "_ZN11CNWCMessage36SendPlayerToServerChat_DungeonMasterE10CExoString"; // minor 0x0e

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
    // data globals
    public const string AppManager = "g_pAppManager";
    public const string GuiMan     = "g_pGuiMan";
    public const string Nuklear    = "g_pNuklear";

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
        [SendChatTalk]        = default,
        // outbound send parity (#22): Linux-only like the other chat senders -> skip + log on Windows.
        [SendChatShout]   = default,
        [SendChatWhisper] = default,
        [SendChatTell]    = default,
        [SendChatParty]   = default,
        [SendChatDM]      = default,
        // chat portraits (#26): Aurora texture manager, Linux-only anchors -> skip + log on Windows.
        [AurTextureGet]    = default,
        [AurTextureGetTID] = default,

        [OnGainFocus] = default,   // empty focus stub, indirect dispatch
        [OnLostFocus] = default,   // empty focus stub
        [Nuklear]     = default,   // NUI edit-focus probe (client global)

        // consent modal -- name-exported on Windows (verified in nwmain.exe's export table).
        [OkCancelCtor]    = Nm("??0CPanelOkCancel@@QEAA@XZ"),
        [OkCancelActivate]= Nm("?Activate@CPanelOkCancel@@QEAAXVCExoString@@II@Z"),
        [GuiMan]          = Nm("?g_pGuiMan@@3PEAVCGuiMan@@EA"),
        [SdlGetKeyName]   = Nm("SDL_GetKeyName"),
        [SdlGetModState]  = Nm("SDL_GetModState"),
        // Footgun: nwmain.exe statically links the CRT, so the panel frees via the game's own
        // operator delete -- allocate from the game's own operator new (build-locked RVA) or the
        // cross-heap free corrupts. Scalar operator new(size_t), VS2017/2019 release.
        [OperatorNew]     = At(0xc10324),

        // client-native manager (nuklear) -- no Windows anchor yet: skip + log (manager absent on Windows).
        [NkSdlRender]        = default,
        [NkBegin]            = default,
        [NkEnd]              = default,
        [NkLayoutRowDynamic] = default,
        [NkLabel]            = default,
        [NkButtonLabel]      = default,
        [NkWindowIsClosed]   = default,
    };

    // Runtime base of nwmain (ASLR-relocated); RVAs are added to this, never the static file base.
    private static readonly IntPtr Base = NativeLibrary.GetMainProgramHandle();
    private static bool? _buildOk;

    /// <summary>
    /// Resolve a Linux mangled symbol to an absolute address for the running client.
    /// Linux: the Itanium name directly. Windows: name export, build-locked RVA, or skip.
    /// Zero = unresolved (already logged).
    /// </summary>
    public static IntPtr Resolve(string linux, ExtenderLog? log)
    {
        if (!OperatingSystem.IsWindows())
            return NativeLibrary.TryGetExport(Base, linux, out IntPtr p) ? p : IntPtr.Zero;

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
    //      text-input + clipboard. Linux-only for now: no _win entries, so each skips-with-log
    //      on Windows (correct -- the native chatbox is a Linux slice). ----
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
    public const string SdlStartTextInput   = "SDL_StartTextInput";
    public const string SdlStopTextInput    = "SDL_StopTextInput";
    public const string SdlGetClipboardText = "SDL_GetClipboardText";
    public const string SdlSetClipboardText = "SDL_SetClipboardText";
    public const string SdlFree             = "SDL_free";
}
