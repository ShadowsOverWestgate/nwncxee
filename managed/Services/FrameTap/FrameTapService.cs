using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Ui;

namespace Sowx.Services.FrameTap;

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
        // Stand down whenever a real renderer owns SwapBuffers -- funchook can't double-hook it.
        // UiHostService owns the frame present on the default path (#62), so this teaching example no
        // longer installs -- kept as the minimal hook-pattern reference, and it resumes as proof-of-life if
        // the framework is ever stood down (SOWX_LOCKCHAT).
        if (UiHostService.OwnsFramePresent)
            return;
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
