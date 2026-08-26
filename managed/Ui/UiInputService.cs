using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Ui;

/// <summary>
/// The framework's raw-input layer (spec #64): owns the SDL/engine input hooks and feeds every captured event
/// into the <see cref="UiHost"/> arbiter, so every window from every service gets routed input for free and no
/// service owns capture. The fixed precedence is: the focused/keyboard-owning window's content → registered
/// chord consumers (hotkeys) → the engine's gameplay keybinds. It also owns the centralized keyboard gate (the
/// engine keyboard-mode toggle + the drive-latch reset, #34, once for all consumers), the environmental chord
/// gates (a NUI edit box, the debug console, a native dialog), and the pointer cursor (folded in from the old
/// CursorService: over any window's UI → the walk cursor).
/// </summary>
/// <remarks>
/// Stands down under <c>SOWX_LOCKCHAT</c> (<see cref="GameGate.OwnsFramePresent"/> false): installs no
/// hooks and leaves <see cref="UiHost.KeyboardGate"/> null, so vanilla input is untouched and
/// <c>HotkeyInputService</c> keeps its own raw capture for that path. State is static (the
/// <c>[UnmanagedCallersOnly]</c> handlers demand it); the input hooks are cold, so they may allocate.
/// </remarks>
public sealed unsafe class UiInputService : IExtenderService
{
    private static ExtenderLog? _log;

    // ---- SDL_Event layout (verified against WinMessageHandler; same offsets the old capture used) ----
    private const uint SdlKeyDown = 0x300, SdlKeyUp = 0x301, SdlTextInput = 0x303;
    private const uint SdlMouseMotion = 0x400, SdlMouseDown = 0x401, SdlMouseUp = 0x402, SdlMouseWheel = 0x403;
    private const int SdlEvTextOff = 0xC, SdlEvSymOff = 0x14, SdlEvButtonOff = 0x10;
    private const int SdlEvMouseXOff = 0x14, SdlEvMouseYOff = 0x18, SdlEvWheelYOff = 0x14;

    // NUI keyboard-focus probe: ctx->active (0x6730) is the focused window; win->edit.active (0x18c) is nonzero
    // while an edit box has focus. NUI runs a parallel SDL pump, so a key typed in a NUI field still reaches us.
    private const int NkActiveWindowOff = 0x6730, NkWinEditActiveOff = 0x18c;
    // Native (vanilla) dialog probe: *(gui+0x70) non-null while a native conversation panel is up.
    private const int ExoAppInternalOff = 0x8, InGameGuiOff = 0x90, DialogPanelOff = 0x70;
    private const int UiCursorId = 3;   // engine walk/default ground pointer

    private static bool _focused = true;
    private static readonly HashSet<uint> _keysDown = new();   // held keys, for the auto-repeat gate on chords

    private static IntPtr _nuklearGlobal, _exoInputGlobal, _appMgr;
    private static delegate* unmanaged<IntPtr, int> _getKbMode;
    private static delegate* unmanaged<int> _modState;
    private static delegate* unmanaged<IntPtr, void> _kbOn, _kbOff;
    private static delegate* unmanaged<void> _turnOffModal;
    private static delegate* unmanaged<int*, int*, uint> _sdlGetMouseState;
    private static bool _kbHeld;   // our view of the engine keyboard-capture gate; edges drive the modal reset

    // Trampolines to the engine originals.
    private static delegate* unmanaged<uint, uint, int> _keyOrig;
    private static delegate* unmanaged<IntPtr, int> _winMsgOrig;
    private static delegate* unmanaged<IntPtr, void> _gainOrig, _lostOrig;
    private static delegate* unmanaged<IntPtr, int, void> _origUpdateCursor;
    private static delegate* unmanaged<IntPtr, int, int, int, int, int, void> _origSetCursor;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!GameGate.OwnsFramePresent)   // SOWX_LOCKCHAT: framework stands down; HotkeyInputService owns capture
        {
            log.Info("UiInputService: SOWX_LOCKCHAT set -- input capture left to the legacy path");
            return;
        }

        _nuklearGlobal   = Native.Sym(SymbolMap.Nuklear);
        _exoInputGlobal  = Native.Sym(SymbolMap.ExoInput);
        _appMgr          = Native.Sym(SymbolMap.AppManager);
        _getKbMode       = (delegate* unmanaged<IntPtr, int>)Native.Sym(SymbolMap.GetKeyboardMode);
        _modState        = (delegate* unmanaged<int>)Native.Sym(SymbolMap.SdlGetModState);
        _kbOn            = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.KeyboardModeOn);
        _kbOff           = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.KeyboardModeOff);
        _turnOffModal    = (delegate* unmanaged<void>)Native.Sym(SymbolMap.TurnOffKeyboardModal);
        _sdlGetMouseState = (delegate* unmanaged<int*, int*, uint>)Native.Sym(SymbolMap.SdlGetMouseState);

        UiHost.KeyboardGate = KbGate;   // centralized engine keyboard gate (#64 AC3): one owner for all consumers

        _keyOrig    = (delegate* unmanaged<uint, uint, int>)hooks.Install(SymbolMap.HandleWMKeyDown,   (IntPtr)(delegate* unmanaged<uint, uint, int>)&OnKeyDown);
        _winMsgOrig = (delegate* unmanaged<IntPtr, int>)hooks.Install(SymbolMap.WinMessageHandler,     (IntPtr)(delegate* unmanaged<IntPtr, int>)&OnWinMessage);
        _gainOrig   = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.OnGainFocus,          (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnGain);
        _lostOrig   = (delegate* unmanaged<IntPtr, void>)hooks.Install(SymbolMap.OnLostFocus,          (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnLost);

        // Cursor fold needs BOTH hooks: OnUpdateCursor decides "over UI", OnSetMouseCursor forces the pointer.
        // A Zero trampoline means Install did not hook the symbol (prepare failed / unresolved), so the handler is
        // never active -- but only arm the fold when both resolved, mirroring the old CursorService clean bail, so
        // OnUpdateCursor does no useless work when the forcing half is missing.
        var updTramp = hooks.Install(SymbolMap.UpdateMouseCursorAppearance, (IntPtr)(delegate* unmanaged<IntPtr, int, void>)&OnUpdateCursor);
        if (updTramp != IntPtr.Zero)
        {
            _origUpdateCursor = (delegate* unmanaged<IntPtr, int, void>)updTramp;
            var setTramp = hooks.Install(SymbolMap.SetMouseCursor, (IntPtr)(delegate* unmanaged<IntPtr, int, int, int, int, int, void>)&OnSetMouseCursor);
            _origSetCursor = (delegate* unmanaged<IntPtr, int, int, int, int, int, void>)setTramp;
            _cursorFoldActive = setTramp != IntPtr.Zero;
            if (!_cursorFoldActive)
                log.Info("UiInputService: cursor fold disabled (SetMouseCursor hook unavailable)");
        }

        log.Info(_winMsgOrig != null
            ? "UiInputService: input capture owned (keys/mouse/focus) -- routing through the UiHost arbiter"
            : "UiInputService: input capture not installed (Install returned Zero: symbol unresolved or already hooked)");
    }

    // ---- the centralized keyboard gate (was ChatService.KeyboardMode) --------------------------
    /// <summary>Drive the engine's gameplay-key gate for the keyboard owner. On the off→on edge only, also run
    /// vanilla's <c>TurnOffKeyboardModal</c> (the step CGuiEdit::SetFocus does after KeyboardModeOn) to clear the
    /// WASD drive latch and send AbortDriveControl -- without it a keyup gated mid-walk leaves the player stuck
    /// moving (#34). Edge-gated so a re-sync doesn't re-hit the HUD on every key.</summary>
    private static void KbGate(bool on)
    {
        if (_exoInputGlobal == IntPtr.Zero) return;
        IntPtr inst = *(IntPtr*)_exoInputGlobal;   // g_pExoInput may be null early in-game
        if (inst == IntPtr.Zero) return;
        if (on)
        {
            if (_kbOn != null) _kbOn(inst);
            // TurnOffKeyboardModal double-derefs g_pAppManager unguarded -> gate on a live app manager.
            if (!_kbHeld && _turnOffModal != null && _appMgr != IntPtr.Zero && *(IntPtr*)_appMgr != IntPtr.Zero)
                _turnOffModal();
        }
        else if (_kbOff != null) _kbOff(inst);
        _kbHeld = on;
    }

    private static int RawMods() => _modState != null ? _modState() : 0;

    // Opt-in raw-key probe (SOWX_KEYDIAG). OFF by default -- it logs every keydown, so the whole point of the
    // feature (not being a keylogger) forbids it as a default. Reports the framework-level gates now.
    private static readonly bool _keyDiag =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOWX_KEYDIAG"));

    // ---- key path: HandleWMKeyDownMessage --------------------------------------------------------
    [UnmanagedCallersOnly]
    private static int OnKeyDown(uint key, uint flags)
    {
        int mods = RawMods();
        if (_keyDiag)
            _log?.Info($"[keydiag] key={key} focused={_focused} owner={UiHost.AnyKeyboardOwner} " +
                       $"suppress={UiHost.AnySuppressChords} nui={NuiEditFocused()} kbmode={KeyboardModeActive()} natdlg={NativeDialogOpen()}");
        // Precedence: a window (keyboard owner or selective modal) eats it -> don't run the engine original.
        if (UiHost.DispatchKey(key, mods)) return 0;
        // Chord layer: only on the fresh press (auto-repeat gate), and only when the gate is clear.
        bool fresh = _keysDown.Add(key);
        if (fresh && ChordGateClear()) UiHost.FireChords(key, mods);
        return _keyOrig != null ? _keyOrig(key, flags) : 0;   // observe, never consume gameplay
    }

    /// <summary>The environmental chord gate: hotkeys fire only while the app is focused and no other text sink
    /// or modal owns input -- the keyboard owner (chat typing), a window withholding chords (our dialog), a NUI
    /// edit box, the engine keyboard mode (debug console / vanilla field), or a native conversation panel. Any
    /// null link reads as "clear". Keys the owner ate never reach here (DispatchKey consumed them); the owner
    /// check matters for mouse chords, which the owner does not consume.</summary>
    private static bool ChordGateClear()
        => _focused && !UiHost.AnyKeyboardOwner && !UiHost.AnySuppressChords
           && !NuiEditFocused() && !KeyboardModeActive() && !NativeDialogOpen();

    // ---- SDL pump: WinMessageHandler ------------------------------------------------------------
    [UnmanagedCallersOnly]
    private static int OnWinMessage(IntPtr ev)
    {
        if (ev != IntPtr.Zero)
        {
            switch (*(uint*)ev)
            {
                case SdlKeyDown:
                    // Swallow a modal window's key HERE, before CExoInput::AddEvent queues the scancode keybind
                    // (it runs pre-HandleWMKeyDown with no modifier check). Only when no full owner is typing
                    // (an owner suppresses keybinds via the keyboard-mode gate). Keyups still pass -> no stuck key.
                    if (!UiHost.AnyKeyboardOwner
                        && UiHost.DispatchKeyPumpSwallow(*(uint*)(ev + SdlEvSymOff), RawMods()))
                        return 0;
                    break;
                case SdlKeyUp:
                    _keysDown.Remove(*(uint*)(ev + SdlEvSymOff));
                    break;
                case SdlTextInput:
                    UiHost.DispatchText(ev + SdlEvTextOff);   // finished UTF-8 -> the keyboard owner's edit line
                    break;
                case SdlMouseMotion:
                {
                    int mx = *(int*)(ev + SdlEvMouseXOff), my = *(int*)(ev + SdlEvMouseYOff);
                    if (UiHost.HoverToFocus) UiHost.HoverFocusAt(mx, my);
                    if (UiHost.DispatchMouseMotion(mx, my)) return 0;
                    break;
                }
                case SdlMouseDown:
                {
                    byte btn = *(byte*)(ev + SdlEvButtonOff);
                    int mx = *(int*)(ev + SdlEvMouseXOff), my = *(int*)(ev + SdlEvMouseYOff);
                    if (!UiHost.HoverToFocus) UiHost.FocusAt(mx, my);   // click-to-focus (default)
                    if (UiHost.DispatchMouseButton(true, btn, mx, my)) return 0;
                    if (ChordGateClear()) UiHost.FireMouseChords(btn, RawMods());
                    break;
                }
                case SdlMouseUp:
                {
                    byte btn = *(byte*)(ev + SdlEvButtonOff);
                    int mx = *(int*)(ev + SdlEvMouseXOff), my = *(int*)(ev + SdlEvMouseYOff);
                    if (UiHost.DispatchMouseButton(false, btn, mx, my)) return 0;
                    break;
                }
                case SdlMouseWheel:
                    if (UiHost.DispatchMouseWheel(*(int*)(ev + SdlEvWheelYOff))) return 0;
                    break;
            }
        }
        return _winMsgOrig != null ? _winMsgOrig(ev) : 0;
    }

    // ---- focus edges ----------------------------------------------------------------------------
    // self is CClientExoApp* -- cache it so the player resolver can walk to the live in-game GUI.
    [UnmanagedCallersOnly] private static void OnGain(IntPtr self) { _focused = true;  ClientApi.LiveExoApp = self; if (_gainOrig != null) _gainOrig(self); }
    [UnmanagedCallersOnly] private static void OnLost(IntPtr self) { _focused = false; ClientApi.LiveExoApp = self; _keysDown.Clear(); if (_lostOrig != null) _lostOrig(self); }

    // ---- cursor fold (was CursorService) --------------------------------------------------------
    private static bool _forceDefault, _prevOverUi, _cursorFoldActive;

    /// <summary>Focus-mode pref (AC #6), restored on the first cursor update that <see cref="ConfigFile"/>'s
    /// deferral gate lets through -- Initialize runs pre-main, where the engine user dir is unresolved.</summary>
    private static readonly ConfigFile _prefs = new("sowx-ui.cfg");
    private static void LoadPrefs()
    {
        if (!_prefs.Load(_log, ApplyPrefs)) return;
        _log?.Info($"[ui] prefs: hoverToFocus={UiHost.HoverToFocus}");
    }

    private static void ApplyPrefs(string text) => ConfigFile.ForEachPair(text, (k, v) =>
    {
        if (k == "hoverToFocus") UiHost.HoverToFocus = v is "1" or "true";
    });

    [UnmanagedCallersOnly]
    private static void OnUpdateCursor(IntPtr @this, int param1)
    {
        LoadPrefs();   // gated + latched inside; a no-op after the first success
        if (!_cursorFoldActive)   // forcing half absent -> forward untouched, do no hit-test work
        {
            if (_origUpdateCursor != null) _origUpdateCursor(@this, param1);
            return;
        }
        int mx = 0, my = 0;
        if (_sdlGetMouseState != null) _sdlGetMouseState(&mx, &my);
        bool overUi = UiHost.HitTest(mx, my) != null;   // any window's UI under the pointer (US #11)
        if (overUi != _prevOverUi)
        {
            _prevOverUi = overUi;
            _log?.Info($"[cursor] overUi {!overUi}->{overUi} at ({mx},{my})");
        }
        if (overUi && param1 == 0) param1 = 1;
        _forceDefault = overUi;
        if (_origUpdateCursor != null) _origUpdateCursor(@this, param1);
    }

    // ponytail: over any UI forces the walk cursor (id 3), the pre-#64 behaviour; distinct grip/link cursors
    // need the real engine cursor ids (Ghidra the SetMouseCursor callers) -- add when a report asks for them.
    [UnmanagedCallersOnly]
    private static void OnSetMouseCursor(IntPtr @this, int cursorId, int hotspot, int p3, int p4, int p5)
    {
        if (_origSetCursor == null) return;
        if (_forceDefault) _origSetCursor(@this, UiCursorId, 0, 1, 0, p5);
        else _origSetCursor(@this, cursorId, hotspot, p3, p4, p5);
    }

    // ---- environmental probes -------------------------------------------------------------------
    private static bool NuiEditFocused()
    {
        if (_nuklearGlobal == IntPtr.Zero) return false;
        IntPtr ctx = *(IntPtr*)_nuklearGlobal;
        if (ctx == IntPtr.Zero) return false;
        IntPtr win = *(IntPtr*)(ctx + NkActiveWindowOff);
        return win != IntPtr.Zero && *(int*)(win + NkWinEditActiveOff) != 0;
    }

    private static bool KeyboardModeActive()
    {
        if (_exoInputGlobal == IntPtr.Zero || _getKbMode == null) return false;
        IntPtr inst = *(IntPtr*)_exoInputGlobal;
        return inst != IntPtr.Zero && _getKbMode(inst) != 0;
    }

    private static bool NativeDialogOpen()
    {
        if (_appMgr == IntPtr.Zero) return false;
        IntPtr mgr = *(IntPtr*)_appMgr;                if (mgr == IntPtr.Zero) return false;
        IntPtr app = *(IntPtr*)mgr;                    if (app == IntPtr.Zero) return false;
        IntPtr intern = *(IntPtr*)(app + ExoAppInternalOff); if (intern == IntPtr.Zero) return false;
        IntPtr gui = *(IntPtr*)(intern + InGameGuiOff);
        return gui != IntPtr.Zero && *(IntPtr*)(gui + DialogPanelOff) != IntPtr.Zero;
    }
}
