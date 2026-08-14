using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Services;

/// <summary>
/// The full pattern in one file: install one hook, call the original through the
/// trampoline. Hooks the frame present and announces the first frame. Groundwork for
/// graphics-pipeline work.
/// </summary>
/// <remarks>
/// [UnmanagedCallersOnly] handlers must be static, so anything they touch is static too.
/// That is the one ergonomic tax of C#-in-a-native-callback; keep the static surface tiny.
/// </remarks>
public sealed unsafe class FrameTapService : IExtenderService
{
    // GLRender::SwapBuffers(SDL_Window*) -- static (no `this`) + one arg (MSVC `CA` proved it).
    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, void> _original;
    private static bool _announced;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _original = (delegate* unmanaged<IntPtr, void>)hooks.Install(
            SymbolMap.SwapBuffers, (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnSwapBuffers);
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
