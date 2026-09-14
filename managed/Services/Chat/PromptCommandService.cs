using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;

namespace CX.Services.Chat;

/// <summary>
/// #580: CX commands dispatch from the game's OWN chat prompt (spec §3 door 1). A chain on
/// <c>CGuiInGameChatPrompt::HandleEnter</c> reads the line vanilla is about to send; when the registry knows it,
/// the prompt's text is emptied BEFORE the original runs, so vanilla closes the prompt exactly as it does on an
/// empty Enter -- clear, <c>CGuiEdit::LoseFocus</c>, <c>ParseChatString</c> returning at length 0 (decompiled:
/// nothing reaches a send), then the unfocus GUI event the server needs to drop its "typing" state -- and the
/// command runs after the prompt has closed. Any other line is forwarded untouched. Installs whenever its symbols
/// resolve; with the CX chatbox up the vanilla prompt never opens, so this fires only while the chatbox is stood
/// down (<c>chat.enabled=0</c>). Cold hook (one Enter per line), so it may allocate.
/// </summary>
/// <remarks>
/// Not a skip: skipping the original would also skip the unfocus GUI event and the vanilla close, both of which
/// this then would have to re-implement from three more anchors. Emptying the prompt and letting vanilla close it
/// is the same outcome from one write. The reply goes where the engine puts its own client-side command output:
/// <c>AppendToMsgBuffer</c> on the console channel, which is the vanilla chat window -- the only one up.
/// </remarks>
public sealed unsafe class PromptCommandService : IExtenderService
{
    /// <summary>Below the hotkey observer's layer on the same symbol: the observer sees every Enter first and always
    /// calls through; this layer is the one that decides what the original sees.</summary>
    public const int EnterPriority = 0;

    /// <summary><c>GUIINGAME_MESSAGE_CONSOLE</c>: the channel <c>ParseChatString</c> echoes client-only command
    /// output on (decompiled @0x7780b0: <c>AppendToMsgBuffer(gui, text, 0x10000000, 0, 0, "")</c>).</summary>
    private const uint ConsoleChannel = 0x10000000;

    private static ExtenderLog? _log;
    private static IntPtr* _slot;
    private static readonly int EditOff = EngineLayout.ChatPromptEdit;
    private static readonly int StringOff = EngineLayout.GuiEditString;
    private static readonly int SetTextSlot = EngineLayout.AurStringSetTextSlot;
    private static delegate* unmanaged<IntPtr, IntPtr, void> _getText;
    private static delegate* unmanaged<IntPtr, void> _exoDtor;
    private static IntPtr _appMgr;
    /// <summary>The <c>""</c> handed to <c>CAurString::SetText</c>: the engine passes a string literal there, so the
    /// callee may keep the pointer -- give it memory that never moves.</summary>
    private static readonly IntPtr _empty = Marshal.AllocHGlobal(1);

    /// <summary>A zeroed 24-byte aggregate: a 17-byte <c>CResRef</c> reading <c>""</c> (char[16] + NUL, all zero),
    /// which SysV passes in memory like the real thing. Win64 hands a pointer to a copy instead.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct CResRef24 { public ulong A, B, C; }
#if WINDOWS
    private static delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, IntPtr, void> _append;
#else
    private static delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef24, void> _append;
#endif

    /// <summary>The unit (#419 shape): the Enter chain, the text read, the echo path and the app walk to it.</summary>
    private static readonly string[] RequiredSymbols =
    {
        SymbolMap.ChatHandleEnter, SymbolMap.GuiEditGetText, SymbolMap.AppendToMsgBuffer, SymbolMap.AppManager,
        SymbolMap.ExoStringInit, SymbolMap.ExoStringDtor,
    };

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        *(byte*)_empty = 0;
        if (!SymbolMap.Require(log, "PromptCommandService", RequiredSymbols)) return;
        if (!EngineLayout.Require(log, "PromptCommandService", "CGuiInGameChatPrompt", "CGuiEdit", "CAurString",
                                  "CClientExoApp", "CClientExoAppInternal")) return;
        _getText = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.GuiEditGetText);
        _exoDtor = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.ExoStringDtor);
        _appMgr = Native.Sym(SymbolMap.AppManager);
#if WINDOWS
        _append = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, IntPtr, void>)Native.Sym(SymbolMap.AppendToMsgBuffer);
#else
        _append = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef24, void>)Native.Sym(SymbolMap.AppendToMsgBuffer);
#endif
        _slot = hooks.Chain(SymbolMap.ChatHandleEnter, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnEnter, EnterPriority);
        log.Info(_slot != null ? "PromptCommandService: vanilla prompt dispatch chained on HandleEnter"
                               : "PromptCommandService: HandleEnter chain refused (single-owner hook on the symbol)");
    }

    [UnmanagedCallersOnly]
    private static void OnEnter(IntPtr prompt)
    {
        string line = ReadLine(prompt + EditOff);
        bool hit = CommandRegistry.Global.Knows(line) && Clear(prompt + EditOff);
        if (_slot != null) ((delegate* unmanaged<IntPtr, void>)*_slot)(prompt);
        if (!hit) return;
        if (CommandRegistry.Global.TryDispatch(line, out string? reply) && reply != null) Echo(reply);
    }

    /// <summary>What vanilla reads: <c>CGuiEdit::GetText</c> (sret, caller-owned buffer), cut at the first line break
    /// the way HandleEnter's two <c>Find</c>/<c>Left</c> passes do. Latin1, as the chatbox reads engine text.</summary>
    private static string ReadLine(IntPtr edit)
    {
        byte* exo = stackalloc byte[16];
        for (int i = 0; i < 16; i++) exo[i] = 0;   // the dtor below frees whatever buffer lands here
        if (OperatingSystem.IsWindows()) _getText(edit, (IntPtr)exo); else _getText((IntPtr)exo, edit);
        byte* buf = *(byte**)exo;
        uint len = *(uint*)(exo + 8);
        var sb = new StringBuilder((int)Math.Min(len, 4096));
        for (uint k = 0; k < len && k < 4096; k++)
        {
            if (buf[k] == '\n' || buf[k] == '\r') break;
            sb.Append((char)buf[k]);
        }
        _exoDtor((IntPtr)exo);
        return sb.ToString();
    }

    /// <summary>One-shot VtblSlot probe on the first CAurString seen; a failed probe is rot and the clear is never called.</summary>
    private static bool? _probeOk;

    /// <summary>The clear HandleEnter itself does first: <c>m_pString->SetText("")</c> through the CAurString vtable.
    /// Returns false when the slot failed its probe, so the line is forwarded as chat rather than dispatched half-way.</summary>
    private static bool Clear(IntPtr edit)
    {
        IntPtr str = *(IntPtr*)(edit + StringOff);
        if (str == IntPtr.Zero) return false;
        _probeOk ??= EngineLayout.Probe("CAurString", str, _log);
        if (_probeOk == false) return false;
        var setText = (delegate* unmanaged<IntPtr, IntPtr, void>)*(IntPtr*)(*(IntPtr*)str + SetTextSlot);
        setText(str, _empty);
        return true;
    }

    /// <summary>One vanilla chat-window line per reply line, on the console channel. <c>*g_pAppManager -&gt;
    /// CClientExoApp -&gt; internal -&gt; CGuiInGame</c>, the walk <c>GetInGameGui</c> is.</summary>
    private static void Echo(string reply)
    {
        IntPtr mgr = *(IntPtr*)_appMgr; if (mgr == IntPtr.Zero) return;
        IntPtr app = *(IntPtr*)mgr;     if (app == IntPtr.Zero) return;
        IntPtr intern = *(IntPtr*)(app + EngineLayout.ExoAppInternal); if (intern == IntPtr.Zero) return;
        IntPtr gui = *(IntPtr*)(intern + EngineLayout.InternalInGameGui); if (gui == IntPtr.Zero) return;
#if WINDOWS
        byte* resref = stackalloc byte[32];
        for (int i = 0; i < 32; i++) resref[i] = 0;
#endif
        foreach (string line in reply.Split('\n'))
        {
            IntPtr exo = Native.NewExoString(line);
#if WINDOWS
            _append(gui, exo, ConsoleChannel, 0, 0, (IntPtr)resref);
            *(long*)exo = 0; *(long*)(exo + 8) = 0;   // Win64: the callee destroyed its by-value CExoString copy
#else
            _append(gui, exo, ConsoleChannel, 0, 0, default);
#endif
            Native.FreeExoString(exo);
        }
    }
}
