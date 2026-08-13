using System.Runtime.InteropServices;

namespace Sowx.Framework;

// The one place that knows a symbol's identity and how it resolves per platform.
// Each engine symbol we hook or call is a `public const` Linux (Itanium) mangled
// name here; services and Native/ClientApi reference SymbolMap.<Name> rather than
// holding their own copy, so every mangled string is written exactly once. The
// table below pairs that name with its Windows resolution.
//
// The Linux client exports everything by name; the Windows client (opt-in PE
// exports) name-exports only shared-engine classes, so client GUI/message classes
// resolve by RVA (address = module base + RVA) instead. HookService.Install and
// Native.Sym both go through Resolve(), so this file is the whole platform surface.
//
// RVAs were derived from the Windows client stamped WindowsBuildStamp via Ghidra
// string/xref/immediate/decompile matching against the Linux binary. Provenance and
// the regen recipe: docs/windows-symbols.md.
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
    // misc engine helpers
    public const string GetNWCMessage = "_ZN13CClientExoApp13GetNWCMessageEv";
    public const string SetText       = "_ZN20CAuroraStringWrapper7SetTextEj";
    public const string ExoStringInit = "_ZN10CExoString17InitFromCharArrayEPKcj";
    public const string ExoStringDtor = "_ZN10CExoStringD1Ev";
    // data globals
    public const string AppManager = "g_pAppManager";
    public const string Nuklear    = "g_pNuklear";

    // PE TimeDateStamp of the nwmain.exe the RVAs were read from (build 26c6e573).
    // A client patch shifts addresses; RVA hooks are refused unless the loaded exe
    // matches this (see BuildMatches) -- fail loud, never hook a wrong address.
    public const uint WindowsBuildStamp = 0x68e408bc;

    // Windows resolution for a symbol. Exactly one path applies:
    //   Name != null : name-exported (MSVC mangling) -- resolve like Linux
    //   Rva  != 0    : not exported -- absolute = runtime module base + Rva (build-locked)
    //   default      : no Windows resolution -- skip with a log line (graceful no-op)
    private readonly record struct Win(string? Name, int Rva);

    private static Win Nm(string msvc) => new(msvc, 0);
    private static Win At(int rva) => new(null, rva);

    // Linux name -> Windows resolution. Keyed by the consts above, so a name lives
    // in exactly one place. If the client doesn't recognise a passed name it isn't
    // in here at all; Resolve treats that as "skip" too.
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
        [SetFocusChatPrompt]          = At(0x6fae30),
        [ChatHandleEnter]             = At(0x87b0d0),
        [ChatHandleEsc]               = At(0x87b390),
        [ChatHandleLMouseDown]        = At(0x87b450),   // Notify event enum 1
        [ChatHandleLoseFocus]         = At(0x87b4b0),   // Notify event enum 2

        // --- no Windows anchor: skip + log. These gate edge cases the OS already
        //     covers; losing them degrades gracefully (see docs/windows-symbols.md). ---
        [OnGainFocus] = default,   // empty focus stub, indirect dispatch
        [OnLostFocus] = default,   // empty focus stub
        [Nuklear]     = default,   // NUI edit-focus probe (client global)
    };

    // Runtime base of nwmain (ASLR-relocated); RVAs are added to this, never the
    // static 0x140000000 from the file.
    private static readonly IntPtr Base = NativeLibrary.GetMainProgramHandle();
    private static bool? _buildOk;

    // Resolve a Linux mangled symbol to an absolute address for the running client.
    // Linux: resolve the Itanium name directly. Windows: consult the table (name
    // export, or build-locked RVA, or skip). Zero = unresolved (already logged).
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

    // The loaded module's PE COFF TimeDateStamp must equal WindowsBuildStamp, else the
    // RVAs address a different build. Checked once; on mismatch all RVA hooks skip.
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
