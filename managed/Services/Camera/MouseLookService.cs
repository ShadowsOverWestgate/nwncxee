using System;
using System.Runtime.InteropServices;
using Sowx.Framework;
using Sowx.Ui;

namespace Sowx.Services.Camera;

/// <summary>
/// Mouse-look on right-drag (#539, map #535). The engine's only mouse orbit is middle-drag
/// (<c>PerformX/YMouseMoveAction</c> with the middle-held flag calling <c>CNWCModule::Turn/TiltCamera(deg, 1)</c>);
/// this makes right-drag the same orbit with the cursor captured. The right press is HELD (not forwarded) until
/// the drag decides: past <c>mouselook_threshold</c> pixels it becomes a look (SDL relative mode, every motion
/// event swallowed and fed to the engine's impulse path, so locks, limits and dialog bookkeeping stay
/// engine-owned); released under the threshold it is replayed as press+release, so the radial menu opens
/// exactly as before, just on release. A press over the engine GUI (NUI, radial, any panel) or over one of our
/// windows passes through untouched -- the engine's radial path is never hooked. Opt-in: <c>mouselook=1</c>.
/// <para>Outermost layer of the <c>WinMessageHandler</c> chain, above <see cref="UiInputService"/>; per-event
/// state is static and the handler allocates nothing. Capture releases on button-up, on window focus loss and
/// when <see cref="CameraService.GateUp"/> (dialog / cutscene / lock) comes up.</para>
/// </summary>
public sealed unsafe class MouseLookService : IExtenderService
{
    private static ExtenderLog? _log;
    private static IntPtr* _next;
    private static delegate* unmanaged<IntPtr, float, int, void> _turn, _tilt;
    private static delegate* unmanaged<int, int> _setRelative;
    private static delegate* unmanaged<IntPtr, int, int, void> _warp;
    private static readonly IntPtr _guiMan = Native.Sym(SymbolMap.GuiMan);

    // ---- SDL_Event layout (same rows UiInputService reads; motion adds the relative pair) ----
    private const uint SdlWindowEvent = 0x200, SdlMouseMotion = 0x400, SdlMouseDown = 0x401, SdlMouseUp = 0x402;
    private const int EvWindowKindOff = 0xc, EvButtonOff = 0x10, EvXOff = 0x14, EvYOff = 0x18, EvXRelOff = 0x1c, EvYRelOff = 0x20;
    private const byte SdlWindowFocusLost = 0xd, SdlButtonRight = 3;
    /// <summary><c>sizeof(SDL_Event)</c> in SDL2: the held press is copied whole for the replay.</summary>
    private const int SdlEventSize = 56;

    /// <summary>Middle-drag's degrees per pixel at 1920 wide: turn = dx * -75 * 0.2 / (W/1920/0.01), tilt =
    /// -dy * 75 * 0.15 / (W/1080/0.01), both over the viewport WIDTH (engine's own formula, W/1080 included).</summary>
    private const float TurnPerPx = -288f, TiltPerPx = -121.5f;

    private enum State { Idle, Pending, Looking }
    private static State _state;
    private static int _originX, _originY;
    /// <summary>The held right press, copied whole at capture and replayed on a still release. One block for
    /// the life of the hook (a fixed buffer cannot be a static field), so the handler still allocates nothing.</summary>
    private static byte* _held;

    private static float _sens = 1f, _threshold = 4f;
    private static bool _invert;
    private static int _looks, _clicks;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!Settings.Bool("mouselook", false)) { log.Info("MouseLook: off (opt in with mouselook=1)"); return; }
        _sens = Settings.Float("mouselook_sens", 1f, 0.05f, 20f);
        _threshold = Settings.Int("mouselook_threshold", 4, 0, 100);
        _invert = Settings.Bool("mouselook_invert", false);
        if (!EngineLayout.Require(log, "MouseLook", "CGuiMan")) return;
        if (!SymbolMap.Require(log, "MouseLook", SymbolMap.WinMessageHandler, SymbolMap.TurnCamera, SymbolMap.TiltCamera,
                               SymbolMap.SdlSetRelativeMouseMode, SymbolMap.SdlWarpMouseInWindow,
                               SymbolMap.GuiMan, SymbolMap.GuiObjectHitCheck, SymbolMap.GetModule, SymbolMap.AppManager)) return;
        _turn = (delegate* unmanaged<IntPtr, float, int, void>)Native.Sym(SymbolMap.TurnCamera);
        _tilt = (delegate* unmanaged<IntPtr, float, int, void>)Native.Sym(SymbolMap.TiltCamera);
        _setRelative = (delegate* unmanaged<int, int>)Native.Sym(SymbolMap.SdlSetRelativeMouseMode);
        _warp = (delegate* unmanaged<IntPtr, int, int, void>)Native.Sym(SymbolMap.SdlWarpMouseInWindow);

        _next = hooks.Chain(SymbolMap.WinMessageHandler, (IntPtr)(delegate* unmanaged<IntPtr, int>)&OnWinMessage, 100);
        if (_next == null) { log.Info("MouseLook: standing down (WinMessageHandler chain refused)"); return; }
        _held = (byte*)NativeMemory.AllocZeroed(SdlEventSize);
        CameraCommand.Register("look", "mouse-look on right-drag: reports sensitivity / invert / threshold (sowx.cfg: mouselook, mouselook_sens, mouselook_invert, mouselook_threshold)",
                               _ => Report());
        log.Info($"MouseLook: right-drag orbits (sensitivity {_sens}, invert {_invert}, threshold {_threshold}px); radial menu = a still right-click");
    }

    [UnmanagedCallersOnly]
    private static int OnWinMessage(IntPtr ev)
    {
        if (ev == IntPtr.Zero) return Forward(ev);
        switch (*(uint*)ev)
        {
            case SdlMouseDown:
                if (*(byte*)(ev + EvButtonOff) != SdlButtonRight || _state != State.Idle) break;
                int mx = *(int*)(ev + EvXOff), my = *(int*)(ev + EvYOff);
                if (UiInputService.EngineGuiWantsPress() || UiHost.HitTest(mx, my) != null) break;   // the GUI's press
                _originX = mx; _originY = my;
                Buffer.MemoryCopy((void*)ev, _held, SdlEventSize, SdlEventSize);
                _state = State.Pending;
                return 0;
            case SdlMouseMotion:
                if (_state == State.Idle) break;
                if (_state == State.Pending)
                {
                    int dx = *(int*)(ev + EvXOff) - _originX, dy = *(int*)(ev + EvYOff) - _originY;
                    if (dx * dx + dy * dy <= _threshold * _threshold) break;   // a still click so far: the engine tracks it
                    _state = State.Looking; _looks++;
                    _setRelative(1);
                    return 0;   // this event's delta already moved the (soon warped-back) cursor; start clean
                }
                if (CameraService.GateUp) { Release(); return 0; }
                IntPtr module = CameraService.Module();
                if (module != IntPtr.Zero)
                {
                    float w = ViewportWidth();
                    float k = _sens / w;
                    _turn(module, *(int*)(ev + EvXRelOff) * TurnPerPx * k, 1);
                    _tilt(module, *(int*)(ev + EvYRelOff) * TiltPerPx * k * (_invert ? -1f : 1f), 1);
                }
                return 0;
            case SdlMouseUp:
                if (*(byte*)(ev + EvButtonOff) != SdlButtonRight || _state == State.Idle) break;
                if (_state == State.Pending)
                {
                    // A click, not a drag: the engine sees press then release, and opens the radial as it always did.
                    _state = State.Idle; _clicks++;
                    Forward((IntPtr)_held);
                    return Forward(ev);
                }
                Release();
                return 0;
            case SdlWindowEvent:
                if (*(byte*)(ev + EvWindowKindOff) == SdlWindowFocusLost && _state != State.Idle) Release();
                break;
        }
        return Forward(ev);
    }

    /// <summary>End a look or a held press: cursor back where the drag began, relative mode off, state idle.</summary>
    private static void Release()
    {
        if (_state == State.Looking)
        {
            _setRelative(0);
            _warp(IntPtr.Zero, _originX, _originY);   // NULL window = the one with mouse focus
        }
        _state = State.Idle;
    }

    private static float ViewportWidth()
    {
        IntPtr gm = _guiMan != IntPtr.Zero ? *(IntPtr*)_guiMan : IntPtr.Zero;
        int w = gm != IntPtr.Zero ? *(int*)(gm + EngineLayout.GuiManVpW) : 0;
        return w > 0 ? w : 1920f;
    }

    private static int Forward(IntPtr ev) => ((delegate* unmanaged<IntPtr, int>)(*_next))(ev);

    private static string Report() =>
        FormattableString.Invariant($"[camera] mouse-look: sensitivity {_sens:0.##} invert {(_invert ? 1 : 0)} threshold {_threshold:0}px | {_looks} look(s), {_clicks} replayed click(s), state {_state}");
}
