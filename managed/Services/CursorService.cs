using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Services.Chat;
using Sowx.Services.Quickbar;

namespace Sowx.Services;

/// <summary>
/// Forces the walk pointer when the mouse is over any C#-owned UI.
/// Redirects the engine's own <c>SetMouseCursor</c> to cursor 3 (walk/default ground pointer).
/// </summary>
public sealed unsafe class CursorService : IExtenderService
{
    private static ExtenderLog? _log;
    private static delegate* unmanaged<IntPtr, int, void> _origUpdate;
    private static delegate* unmanaged<IntPtr, int, int, int, int, int, void> _origSetCursor;

    private const int UiCursorId = 3;

    private static delegate* unmanaged<int*, int*, uint> _sdlGetMouseState;

    private static bool _forceDefault;
    private static bool _prevOverUi;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;

        var updateTramp = hooks.Install(
            SymbolMap.UpdateMouseCursorAppearance,
            (IntPtr)(delegate* unmanaged<IntPtr, int, void>)&OnUpdateCursor);
        if (updateTramp == IntPtr.Zero)
        {
            log.Info("CursorService: UpdateMouseCursorAppearance hook failed; disabled");
            return;
        }
        _origUpdate = (delegate* unmanaged<IntPtr, int, void>)updateTramp;

        var setCursorTramp = hooks.Install(
            SymbolMap.SetMouseCursor,
            (IntPtr)(delegate* unmanaged<IntPtr, int, int, int, int, int, void>)&OnSetMouseCursor);
        if (setCursorTramp == IntPtr.Zero)
        {
            log.Info("CursorService: SetMouseCursor hook failed; disabled");
            return;
        }
        _origSetCursor = (delegate* unmanaged<IntPtr, int, int, int, int, int, void>)setCursorTramp;

        IntPtr sdlMs = SymbolMap.Resolve(SymbolMap.SdlGetMouseState, log);
        if (sdlMs != IntPtr.Zero)
            _sdlGetMouseState = (delegate* unmanaged<int*, int*, uint>)sdlMs;

        log.Info("CursorService: cursor override installed (both hooks)");
    }

    [UnmanagedCallersOnly]
    private static void OnUpdateCursor(IntPtr @this, int param1)
    {
        int mx = 0, my = 0;
        if (_sdlGetMouseState != null) _sdlGetMouseState(&mx, &my);
        bool overUi = ChatService.MouseOverUi(mx, my) || QuickbarService.MouseOverUi(mx, my);

        if (overUi != _prevOverUi)
        {
            _prevOverUi = overUi;
            _log?.Info($"[cursor] overUi {!overUi}->{overUi} at ({mx},{my})");
        }

        if (overUi && param1 == 0)
            param1 = 1;

        _forceDefault = overUi;
        if (_origUpdate != null) _origUpdate(@this, param1);
    }

    [UnmanagedCallersOnly]
    private static void OnSetMouseCursor(IntPtr @this, int cursorId, int hotspot, int p3, int p4, int p5)
    {
        if (_forceDefault)
        {
            if (_origSetCursor != null)
                _origSetCursor(@this, UiCursorId, 0, 1, 0, p5);
            return;
        }
        if (_origSetCursor != null)
            _origSetCursor(@this, cursorId, hotspot, p3, p4, p5);
    }
}
