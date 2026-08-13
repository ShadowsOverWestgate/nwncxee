using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services;

// The full pattern in one file: constructor DI (log + hooks), install one hook,
// call the original through the trampoline. Hooks the client's frame present and
// announces the first frame -- run it against the real client and you see the
// line appear, then the game keeps rendering normally.
//
// [UnmanagedCallersOnly] handlers must be static, so anything they touch is
// static too. That is the one ergonomic tax of C#-in-a-native-callback; keep the
// static surface tiny.
public sealed unsafe class FrameTapService : IExtenderService
{
    // GLRender::SwapBuffers(SDL_Window*)   -- ./sym.sh -m GLRender::SwapBuffers
    // The Windows MSVC name says `CA` (static) + one arg (SDL_Window*): there is NO
    // `this`. On Linux the Itanium name hides that; a spare 2nd arg was forwarded as
    // garbage and ignored, which happened to work. The correct signature is one arg.
    private const string Symbol    = "_ZN8GLRender11SwapBuffersEP10SDL_Window";
    private const string SymbolWin = "?SwapBuffers@GLRender@@CAXPEAUSDL_Window@@@Z";

    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, void> _original;
    private static bool _announced;

    private readonly HookService _hooks;

    public FrameTapService(ExtenderLog log, HookService hooks) // service <- framework DI
    {
        _log = log;
        _hooks = hooks;
    }

    public void Initialize()
    {
        IntPtr trampoline = _hooks.Install(
            Symbol, SymbolWin,
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnSwapBuffers);
        _original = (delegate* unmanaged<IntPtr, void>)trampoline;
    }

    [UnmanagedCallersOnly]
    private static void OnSwapBuffers(IntPtr window)
    {
        if (!_announced)
        {
            _announced = true;
            _log?.Info("FrameTapService: first frame presented -- hook is LIVE");
        }
        if (_original != null) _original(window); // let the game render
    }
}
