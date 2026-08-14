using System.Runtime.InteropServices;
using System.Text;
using Sowx.Framework;
using Sowx.Services.Chat.Core;
using Sowx.Services.Chat.Render;

namespace Sowx.Services;

/// <summary>
/// Throwaway in-game probe for the chat render-architecture decision (map #13, ticket #14).
/// Env-gated by <c>SOWX_CHATPROBE</c>; dormant otherwise (installs no hooks), so vanilla play is
/// untouched. Drives its experiments from magic <c>~sowx</c> chat lines (seen by the inbound tap),
/// so it needs no contested input hook. #14 resolved (owned-render); KEPT as the rig for the GL
/// text-render prototype (#16) -- extend with a <c>~sowx text …</c> experiment there. Delete once
/// #16 resolves and the real chat service lands.
/// </summary>
/// <remarks>
/// Experiments (type any of these as a normal Talk; the server echo reaches the tap):
///   ~sowx dump            log live CGuiInGame*/window/viewport pointers + line count + filter
///   ~sowx v X Y W H       CGuiViewport::SetViewport on the primary window, then force a full
///                         backlog replay -- the decisive "does it re-wrap to arbitrary width W" test
///   ~sowx full | split    engine 720/360 preset width change (re-wrap sanity, self-locating)
///   ~sowx talk            fire one SendPlayerToServerChat_Talk from C# (outbound ABI proof)
///   ~sowx quad            toggle a zero-alloc GL scissor-clear in the frame hook (chrome proof)
/// The tap is a COLD hook (network thread) so it may allocate; only the SwapBuffers quad is hot,
/// and it allocates nothing (GL_SCISSOR_TEST clear with constant args).
/// </remarks>
public sealed unsafe class ChatProbeService : IExtenderService
{
    private const int ViewportOff = 0xc78;   // CGuiInGameChatWindow -> its CGuiViewport (from DoFullScreen decompile)
    private const int LineCountOff = 0xc5c;  // rendered line count
    private const int FilterOff   = 0xc58;   // window filter mask

    private static ExtenderLog? _log;
    private static IntPtr _inGame;           // live CGuiInGame*, captured from the tap's `this`
    private static volatile bool _drawQuad;

    /// <summary>16-byte CResRef by value. Only r9 is free when it's passed, so SysV puts the whole
    /// struct on the stack (r9 unused) -- declaring it as a struct makes NativeAOT lower it that way.
    /// Passing it as two ulongs instead splits it r9+stack and corrupts the forwarded call.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct CResRef16 { public ulong Lo; public ulong Hi; }

    // tap trampoline: AppendToMsgBuffer(this, CExoString* text, uint channel, uint playerId,
    // int hasPortrait, CResRef).
    private static delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void> _tapOrig;
    private static delegate* unmanaged<IntPtr, void> _swapOrig;
    private static delegate* unmanaged<IntPtr, int, int, int, int, void> _setViewport;
    private static delegate* unmanaged<IntPtr, uint, void> _setFilter;
    private static delegate* unmanaged<IntPtr, void> _doFull, _doSplit;
    private static delegate* unmanaged<IntPtr, IntPtr, void> _sendTalk;   // (this CNWCMessage*, CExoString*)

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        TextWrapper.SelfCheck(log);   // slice #19: pure, runs headless (run-demo) to prove the wrap floor
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOWX_CHATPROBE"))) return;
        _log = log;
        _log.Info("ChatProbeService: ARMED (SOWX_CHATPROBE). Type '~sowx dump' in game to begin.");
        TextRenderer.Init(log);       // bake the owned-render glyph atlas (cold; GL upload is lazy)

        _tapOrig = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void>)hooks.Install(
            SymbolMap.AppendToMsgBuffer, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void>)&OnAppend);
        _swapOrig = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.SwapBuffers, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnSwap);

        _setViewport = (delegate* unmanaged<IntPtr, int, int, int, int, void>)Native.Sym(SymbolMap.ViewportSetViewport);
        _setFilter   = (delegate* unmanaged<IntPtr, uint, void>)Native.Sym(SymbolMap.SetPriMsgFilter);
        _doFull      = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.ChatDoFullScreen);
        _doSplit     = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.ChatDoSplitScreen);
        _sendTalk    = (delegate* unmanaged<IntPtr, IntPtr, void>)Native.Sym(SymbolMap.SendChatTalk);
    }

    /// <summary>Live primary chat window (CGuiInGame + 0x08), or Zero.</summary>
    private static IntPtr PrimaryWindow => _inGame == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(_inGame + 0x08);

    [UnmanagedCallersOnly]
    private static void OnAppend(IntPtr self, IntPtr text, uint channel, uint playerId,
                                 int hasPortrait, CResRef16 portrait)
    {
        _inGame = self;                                                   // the free live CGuiInGame* handle
        if (_tapOrig != null) _tapOrig(self, text, channel, playerId, hasPortrait, portrait);

        string s = ReadExo(text);
        LogLine(s, channel, playerId, hasPortrait);
        int i = s.IndexOf("~sowx", StringComparison.Ordinal);
        if (i >= 0) Dispatch(s.Substring(i + 5).TrimStart());             // everything after the marker
    }

    /// <summary>Decode a CExoString ({char* buf; uint len}) to a C# string (Latin1 keeps color bytes visible).</summary>
    private static string ReadExo(IntPtr exo)
    {
        if (exo == IntPtr.Zero) return "";
        byte* buf = *(byte**)exo;
        uint len = *(uint*)(exo + 8);
        if (buf == null || len == 0 || len > 4096) return "";
        var sb = new StringBuilder((int)len);
        for (uint k = 0; k < len; k++) sb.Append((char)buf[k]);
        return sb.ToString();
    }

    private static void LogLine(string s, uint channel, uint playerId, int hasPortrait)
    {
        var hex = new StringBuilder();
        int n = Math.Min(s.Length, 48);
        for (int k = 0; k < n; k++) hex.Append(((int)s[k]).ToString("x2"));
        _log?.Info($"[tap] ch=0x{channel:x} pid={playerId} portrait={hasPortrait} " +
                   $"len={s.Length} hex={hex} text={s}");
    }

    private static void Dispatch(string cmd)
    {
        IntPtr win = PrimaryWindow;
        _log?.Info($"[probe] cmd='{cmd}' inGame=0x{_inGame:x} win=0x{win:x}");

        if (cmd.StartsWith("dump", StringComparison.Ordinal))
        {
            if (win == IntPtr.Zero) { _log?.Info("[probe] no window yet"); return; }
            _log?.Info($"[probe] primary=0x{win:x} secondary=0x{*(IntPtr*)(_inGame + 0x10):x} " +
                       $"viewport=0x{win + ViewportOff:x} lines={*(uint*)(win + LineCountOff)} " +
                       $"filter=0x{*(uint*)(win + FilterOff):x}");
        }
        else if (cmd.StartsWith("v ", StringComparison.Ordinal))
        {
            int[] a = ParseInts(cmd.Substring(2));
            if (a.Length != 4 || win == IntPtr.Zero || _setViewport == null) { _log?.Info("[probe] v needs 'X Y W H' + a live window"); return; }
            _setViewport(win + ViewportOff, a[0], a[1], a[2], a[3]);
            if (_setFilter != null) _setFilter(_inGame, 0xffffffff);      // force full backlog replay at the new width
            _log?.Info($"[probe] SetViewport({a[0]},{a[1]},{a[2]},{a[3]}) + replay -- look: did the backlog re-wrap to W={a[2]}?");
        }
        else if (cmd.StartsWith("full", StringComparison.Ordinal)) { if (_doFull != null && win != IntPtr.Zero) _doFull(win); }
        else if (cmd.StartsWith("split", StringComparison.Ordinal)) { if (_doSplit != null && win != IntPtr.Zero) _doSplit(win); }
        else if (cmd.StartsWith("talk", StringComparison.Ordinal)) Talk();
        else if (cmd.StartsWith("quad", StringComparison.Ordinal)) { _drawQuad = !_drawQuad; _log?.Info($"[probe] quad={_drawQuad}"); }
        else if (cmd.StartsWith("text", StringComparison.Ordinal)) TextRenderer.ToggleDemo();   // slice #19 owned-render demo
        else _log?.Info("[probe] commands: dump | v X Y W H | full | split | talk | quad | text");
    }

    private static void Talk()
    {
        IntPtr msg = ClientApi.Message();
        if (msg == IntPtr.Zero || _sendTalk == null) { _log?.Info("[probe] no live CNWCMessage"); return; }
        IntPtr exo = Native.NewExoString("[[sowx outbound OK]]");
        _sendTalk(msg, exo);                                             // callee copies; does NOT take ownership
        Native.FreeExoString(exo);
        _log?.Info("[probe] Talk sent -- expect it to echo back through the tap");
    }

    private static int[] ParseInts(string s)
    {
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var outp = new List<int>(parts.Length);
        foreach (var p in parts) if (int.TryParse(p, out int v)) outp.Add(v);
        return outp.ToArray();
    }

    // GL for the zero-alloc chrome quad. Not in the client symtab (libGL provides them at runtime).
    [DllImport("libGL.so.1")] private static extern void glEnable(uint cap);
    [DllImport("libGL.so.1")] private static extern void glDisable(uint cap);
    [DllImport("libGL.so.1")] private static extern void glScissor(int x, int y, int w, int h);
    [DllImport("libGL.so.1")] private static extern void glClearColor(float r, float g, float b, float a);
    [DllImport("libGL.so.1")] private static extern void glClear(uint mask);
    private const uint GL_SCISSOR_TEST = 0x0C11;
    private const uint GL_COLOR_BUFFER_BIT = 0x00004000;

    [UnmanagedCallersOnly]
    private static void OnSwap(IntPtr window)
    {
        if (_drawQuad)                                                    // paint a magenta box: C# owns frame pixels
        {
            glEnable(GL_SCISSOR_TEST);
            glScissor(40, 40, 320, 140);
            glClearColor(1f, 0f, 1f, 1f);
            glClear(GL_COLOR_BUFFER_BIT);
            glDisable(GL_SCISSOR_TEST);
            glClearColor(0f, 0f, 0f, 0f);                                // restore; the game resets it next frame anyway
        }
        TextRenderer.Draw();                                             // slice #19 owned-render text (self-gated, zero-alloc)
        if (_swapOrig != null) _swapOrig(window);                        // present
    }
}
