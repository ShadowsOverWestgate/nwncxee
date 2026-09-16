using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Ui;
using CX.Graphics;

namespace CX.Services.Camera;

/// <summary>
/// Mouse-look on right-drag (#539, map #535). The engine's only mouse orbit is middle-drag
/// (<c>PerformX/YMouseMoveAction</c> with the middle-held flag calling <c>CNWCModule::Turn/TiltCamera(deg, 1)</c>);
/// this makes right-drag the same orbit with the cursor captured. The right press is HELD (not forwarded) until
/// the drag decides: past <c>mouselook.threshold</c> pixels it becomes a look (SDL relative mode, every motion
/// event swallowed and fed to the engine's impulse path, so locks, limits and dialog bookkeeping stay
/// engine-owned); released under the threshold it is replayed as press+release, so the radial menu opens
/// exactly as before, just on release. A press over the engine GUI (NUI, radial, any panel) or over one of our
/// windows passes through untouched -- the engine's radial path is never hooked. Opt-in: <c>mouselook.right</c>.
/// The knobs are the <see cref="MouseLook"/> group, read live. The master (<c>mouselook.enabled</c>, #584) is the
/// whole service: off, every event is forwarded untouched -- the stock middle-drag orbit, no capture -- and a drag
/// under way is released on the off edge (<see cref="MasterChanged"/>). Live both ways.
/// <para>Middle-drag, the engine's own orbit, gets the same capture unconditionally: the engine turns on ABSOLUTE
/// cursor deltas, so a vanilla drag stalls at the window edge. The press and release are forwarded (the engine
/// keeps its middle-held bookkeeping), every motion in between is swallowed and fed as relative deltas, and the
/// release is forwarded at the press position (a button event is also a mouse-move to the engine).</para>
/// <para>Action camera (<c>mouselook.action</c>): relative mode is held for as long
/// as the world camera is live and nothing else wants the cursor, every motion's delta turns the camera and the
/// event is forwarded rewritten to the screen centre with no delta, so the engine's own hover pick and click land
/// on the crosshair (<see cref="Crosshair"/>, drawn by a registered window since the engine's hardware cursor is
/// hidden under relative mode). Buttons are forwarded at the centre too: left-click attacks or selects what the
/// crosshair points at, a still right-click opens the radial there. Holding Alt, typing in chat, one of our panels
/// owning the keyboard, a dialog / cutscene / lock or the free camera pauses it with the cursor shown at the
/// centre; it resumes on the next event once they clear. While held, click-to-move is refused at
/// <c>WalkPlayerToPoint</c> (movement is the keys') and the world pick never returns the player creature (no
/// self-hover or self-select from a crosshair that sits on your own back).</para>
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
    private const byte SdlWindowFocusLost = 0xd, SdlButtonMiddle = 2, SdlButtonRight = 3;
    /// <summary><c>sizeof(SDL_Event)</c> in SDL2: the held press is copied whole for the replay.</summary>
    private const int SdlEventSize = 56;

    /// <summary>Middle-drag's degrees per pixel at 1920 wide: turn = dx * -75 * 0.2 / (W/1920/0.01), tilt =
    /// -dy * 75 * 0.15 / (W/1080/0.01), both over the viewport WIDTH (engine's own formula, W/1080 included).</summary>
    internal const float TurnPerPx = -288f, TiltPerPx = -121.5f;

    private enum State { Idle, Pending, Looking }
    private static State _state;
    private static byte _button;
    private static int _originX, _originY;
    /// <summary>The held right press, copied whole at capture and replayed on a still release. One block for
    /// the life of the hook (a fixed buffer cannot be a static field), so the handler still allocates nothing.</summary>
    private static byte* _held;

    private static int _looks, _clicks, _actions;
    /// <summary>Action camera held (relative mode ours) and the centre the events are pinned to.</summary>
    private static bool _action;
    private static int _cx, _cy;
    private static delegate* unmanaged<int> _modState;
    /// <summary>SDL <c>KMOD_LALT | KMOD_RALT</c>: the hold that frees the cursor.</summary>
    private const int KmodAlt = 0x300;
    private static delegate* unmanaged<byte*, int> _keyFromName;
    private static delegate* unmanaged<IntPtr, float, float, float, int, uint, int, int> _walkOrig;
    private static delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int*, int, int> _pickHostileOrig, _pickNonHostileOrig;
    private static int _walksRefused, _selfHidden;
    /// <summary>The toggle bind: an SDL keycode, or a mouse button carried negative (-4 = X1, -5 = X2); 0 = none.</summary>
    private static int _toggleCode;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        CameraKnobs.Declare();
        MouseLook.G.Status = Report;
        if (!EngineLayout.Require(log, "MouseLook", "CGuiMan")) return;
        if (!SymbolMap.Require(log, "MouseLook", SymbolMap.WinMessageHandler, SymbolMap.TurnCamera, SymbolMap.TiltCamera,
                               SymbolMap.SdlSetRelativeMouseMode, SymbolMap.SdlWarpMouseInWindow,
                               SymbolMap.GuiMan, SymbolMap.GuiObjectHitCheck, SymbolMap.GetModule, SymbolMap.AppManager)) return;
        _turn = (delegate* unmanaged<IntPtr, float, int, void>)Native.Sym(SymbolMap.TurnCamera);
        _tilt = (delegate* unmanaged<IntPtr, float, int, void>)Native.Sym(SymbolMap.TiltCamera);
        _setRelative = (delegate* unmanaged<int, int>)Native.Sym(SymbolMap.SdlSetRelativeMouseMode);
        _warp = (delegate* unmanaged<IntPtr, int, int, void>)Native.Sym(SymbolMap.SdlWarpMouseInWindow);
        _modState = (delegate* unmanaged<int>)Native.Sym(SymbolMap.SdlGetModState);
        _keyFromName = (delegate* unmanaged<byte*, int>)Native.Sym(SymbolMap.SdlGetKeyFromName);
        Crosshair.Register();
        ActionKeyChanged();
        UiHost.AddChordConsumer(new ActionToggle());
        // The action camera's two refusals: no click-to-move, and the player is never what the crosshair picks.
        if (SymbolMap.Require(log, "MouseLook.action", SymbolMap.WalkPlayerToPoint, SymbolMap.FeedbackHitCheckHostile, SymbolMap.FeedbackHitCheckNonHostile))
        {
            _walkOrig = (delegate* unmanaged<IntPtr, float, float, float, int, uint, int, int>)hooks.Install(SymbolMap.WalkPlayerToPoint,
                (IntPtr)(delegate* unmanaged<IntPtr, float, float, float, int, uint, int, int>)&OnWalkPlayerToPoint);
            _pickHostileOrig = (delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int*, int, int>)hooks.Install(SymbolMap.FeedbackHitCheckHostile,
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int*, int, int>)&OnPickHostile);
            _pickNonHostileOrig = (delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int*, int, int>)hooks.Install(SymbolMap.FeedbackHitCheckNonHostile,
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int*, int, int>)&OnPickNonHostile);
            log.Info((_walkOrig == null || _pickHostileOrig == null || _pickNonHostileOrig == null)
                ? "MouseLook: action camera refusals partly unhooked (walk/pick install refused) -- click-to-move or self-pick may survive it"
                : "MouseLook: action camera hooks WalkPlayerToPoint + FeedbackHitCheck{Hostile,NonHostile}");
        }

        _next = hooks.Chain(SymbolMap.WinMessageHandler, (IntPtr)(delegate* unmanaged<IntPtr, int>)&OnWinMessage, 100);
        if (_next == null) { log.Info("MouseLook: standing down (WinMessageHandler chain refused)"); return; }
        _held = (byte*)NativeMemory.AllocZeroed(SdlEventSize);
        log.Info(!MouseLook.Enabled.V ? "MouseLook: chained, mouselook.enabled=0 -- the stock middle-drag runs until it is turned on"
            : MouseLook.Right.V
            ? $"MouseLook: right-drag orbits (sensitivity {MouseLook.Sens.V}, invert {MouseLook.Invert.V}, threshold {MouseLook.Threshold.V}px); radial menu = a still right-click; middle-drag captured"
            : "MouseLook: middle-drag captured (cursor held in place); right-drag off (opt in with /mouselook right 1)");
    }

    [UnmanagedCallersOnly]
    private static int OnWinMessage(IntPtr ev)
    {
        if (ev == IntPtr.Zero) return Forward(ev);
        if (!MouseLook.Enabled.V) { if (_state != State.Idle) Release(); return Forward(ev); }
        bool want = ActionWanted();
        if (want != _action) SetAction(want);
        if (_action) return OnActionEvent(ev);
        switch (*(uint*)ev)
        {
            case SdlMouseDown:
                byte b = *(byte*)(ev + EvButtonOff);
                if (_state != State.Idle || (b != SdlButtonMiddle && !(b == SdlButtonRight && MouseLook.Right.V))) break;
                int mx = *(int*)(ev + EvXOff), my = *(int*)(ev + EvYOff);
                if (UiInputService.EngineGuiWantsPress() || UiHost.HitTest(mx, my) != null) break;   // the GUI's press
                _originX = mx; _originY = my; _button = b;
                if (b == SdlButtonMiddle)
                {
                    // The engine's own orbit button: it keeps the press, we keep the cursor.
                    _state = State.Looking; _looks++;
                    _setRelative(1);
                    return Forward(ev);
                }
                Buffer.MemoryCopy((void*)ev, _held, SdlEventSize, SdlEventSize);
                _state = State.Pending;
                return 0;
            case SdlMouseMotion:
                if (_state == State.Idle) break;
                if (_state == State.Pending)
                {
                    int dx = *(int*)(ev + EvXOff) - _originX, dy = *(int*)(ev + EvYOff) - _originY;
                    int threshold = MouseLook.Threshold.V;
                    if (dx * dx + dy * dy <= threshold * threshold) break;   // a still click so far: the engine tracks it
                    _state = State.Looking; _looks++;
                    _setRelative(1);
                    return 0;   // this event's delta already moved the (soon warped-back) cursor; start clean
                }
                if (CameraService.GateUp || FreeCameraService.Active) { Release(); return 0; }
                IntPtr module = CameraService.Module();
                if (module != IntPtr.Zero)
                {
                    float w = ViewportWidth();
                    float k = MouseLook.Sens.V / w;
                    _turn(module, *(int*)(ev + EvXRelOff) * TurnPerPx * k, 1);
                    _tilt(module, *(int*)(ev + EvYRelOff) * TiltPerPx * k * (MouseLook.Invert.V ? -1f : 1f), 1);
                }
                return 0;
            case SdlMouseUp:
                if (_state == State.Idle || *(byte*)(ev + EvButtonOff) != _button) break;
                if (_button == SdlButtonMiddle)
                {
                    // CExoInputInternal::AddEvent turns a button event's x/y into a mouse-move BEFORE the button
                    // action, and the engine's orbit is still armed on the up: under relative mode that x/y is
                    // SDL's drifted, window-clamped position, so the up would carry the whole drag as one turn.
                    Release();
                    *(int*)(ev + EvXOff) = _originX; *(int*)(ev + EvYOff) = _originY;
                    return Forward(ev);
                }
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

    // ---- the action camera ----

    private static bool ActionWanted()
        => MouseLook.Action.V && GameGate.InGame && CameraService.Mode >= 0 && !CameraService.GateUp && !FreeCameraService.Active
           && !UiHost.AnyKeyboardOwner && !UiHost.AnySuppressChords && (_modState == null || (_modState() & KmodAlt) == 0);

    private static void SetAction(bool on)
    {
        if (on)
        {
            if (_state != State.Idle) Release();
            IntPtr gm = _guiMan != IntPtr.Zero ? *(IntPtr*)_guiMan : IntPtr.Zero;
            _cx = gm != IntPtr.Zero ? *(int*)(gm + EngineLayout.GuiManVpW) / 2 : 960;
            _cy = gm != IntPtr.Zero ? *(int*)(gm + EngineLayout.GuiManVpH) / 2 : 540;
            _setRelative(1); _actions++;
        }
        else
        {
            _setRelative(0);
            _warp(IntPtr.Zero, _cx, _cy);   // the cursor appears where the engine believes it is
        }
        _action = on;
        Crosshair.Visible = on;
    }

    /// <summary>Every event under the action camera: motion turns the camera and reaches the engine as "the cursor
    /// is at the centre, it did not move"; buttons reach it at the centre; the rest is untouched.</summary>
    private static int OnActionEvent(IntPtr ev)
    {
        switch (*(uint*)ev)
        {
            case SdlMouseMotion:
                IntPtr module = CameraService.Module();
                if (module != IntPtr.Zero)
                {
                    float k = MouseLook.Sens.V / ViewportWidth();
                    _turn(module, *(int*)(ev + EvXRelOff) * TurnPerPx * k, 1);
                    _tilt(module, *(int*)(ev + EvYRelOff) * TiltPerPx * k * (MouseLook.Invert.V ? -1f : 1f), 1);
                }
                *(int*)(ev + EvXOff) = _cx; *(int*)(ev + EvYOff) = _cy;
                *(int*)(ev + EvXRelOff) = 0; *(int*)(ev + EvYRelOff) = 0;
                break;
            case SdlMouseDown:
            case SdlMouseUp:
                *(int*)(ev + EvXOff) = _cx; *(int*)(ev + EvYOff) = _cy;
                break;
            case SdlWindowEvent:
                if (*(byte*)(ev + EvWindowKindOff) == SdlWindowFocusLost) SetAction(false);
                break;
        }
        return Forward(ev);
    }

    [UnmanagedCallersOnly]
    private static int OnWalkPlayerToPoint(IntPtr app, float x, float y, float z, int flags, uint obj, int extra)
    {
        if (_action) { _walksRefused++; return 0; }   // 0 = the engine's own "not walkable" verdict
        return _walkOrig(app, x, y, z, flags, obj, extra);
    }

    [UnmanagedCallersOnly]
    private static int OnPickHostile(IntPtr app, IntPtr* obj, IntPtr point, int* p3, int p4)
        => HidePlayer(_pickHostileOrig(app, obj, point, p3, p4), obj);

    [UnmanagedCallersOnly]
    private static int OnPickNonHostile(IntPtr app, IntPtr* obj, IntPtr point, int* p3, int p4)
        => HidePlayer(_pickNonHostileOrig(app, obj, point, p3, p4), obj);

    /// <summary>Under the action camera the crosshair sits on the player half the time: a pick that landed on the
    /// player creature becomes "nothing" (the engine's 0 verdict), so no self-hover, self-tooltip or self-select.</summary>
    private static int HidePlayer(int verdict, IntPtr* obj)
    {
        if (!_action || verdict == 0 || obj == null || *obj == IntPtr.Zero || *obj != CameraService.PlayerCreature()) return verdict;
        *obj = IntPtr.Zero; _selfHidden++;
        return 0;
    }

    /// <summary>Re-parse <c>mouselook.actionkey</c>: "MouseN" is a button, anything else goes through SDL's own
    /// key-name table (cold: a chord edit, not a hot hook).</summary>
    internal static void ActionKeyChanged()
    {
        string name = (MouseLook.ActionKey.V ?? "").Trim();
        _toggleCode = 0;
        if (name.Length == 0) return;
        if (name.StartsWith("mouse", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(5), out int btn) && btn is >= 1 and <= 5)
        { _toggleCode = -btn; return; }
        if (_keyFromName == null) return;
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = utf8) _toggleCode = _keyFromName(p);
        if (_toggleCode == 0) _log?.Info($"MouseLook: actionkey '{name}' is not an SDL key name; nothing bound");
    }

    /// <summary>The chord that flips <c>mouselook.action</c>: the key or mouse button named by
    /// <c>mouselook.actionkey</c>, fired when no window consumed it.</summary>
    private sealed class ActionToggle : IChordConsumer
    {
        public int Priority => 1;
        public bool WantsKey(int key) => _toggleCode > 0 && key == _toggleCode;
        public void Fire(uint key, int sdlMods) { if (_toggleCode > 0 && key == _toggleCode) Flip(); }
        public void FireMouse(int button, int sdlMods) { if (_toggleCode < 0 && button == -_toggleCode) Flip(); }
        private static void Flip() => MouseLook.Action.Set(!MouseLook.Action.V);
    }

    /// <summary>The action toggle's <c>changed:</c>: the off edge frees the cursor at once (the on edge waits for
    /// the next event, which reads the knob).</summary>
    internal static void ActionChanged() { if (!MouseLook.Action.V && _action) SetAction(false); }

    /// <summary>The master's <c>changed:</c> (#584): the off edge releases a look or a held press at once, so the
    /// cursor is never left captured waiting for the next mouse event. Nothing on the on edge -- the next press
    /// reads <c>MouseLook.Enabled.V</c>.</summary>
    internal static void MasterChanged() { if (!MouseLook.Enabled.V) { Release(); if (_action) SetAction(false); } }

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

    internal static float ViewportWidth()
    {
        IntPtr gm = _guiMan != IntPtr.Zero ? *(IntPtr*)_guiMan : IntPtr.Zero;
        int w = gm != IntPtr.Zero ? *(int*)(gm + EngineLayout.GuiManVpW) : 0;
        return w > 0 ? w : 1920f;
    }

    private static int Forward(IntPtr ev) => ((delegate* unmanaged<IntPtr, int>)(*_next))(ev);

    /// <summary>The bare <c>/mouselook</c> line.</summary>
    private static string Report() =>
        FormattableString.Invariant($"{(MouseLook.Enabled.V ? "on" : "OFF (the stock middle-drag)")}; right-drag {(MouseLook.Right.V ? "on" : "off")}, middle-drag {(MouseLook.Enabled.V ? "captured" : "stock")}; sensitivity {MouseLook.Sens.V:0.##} invert {(MouseLook.Invert.V ? 1 : 0)} threshold {MouseLook.Threshold.V}px | {_looks} look(s), {_clicks} replayed click(s), state {_state}; action camera {(MouseLook.Action.V ? (_action ? "on" : "paused") : "off")}, {_actions} capture(s), {_walksRefused} walk(s) refused, {_selfHidden} self-pick(s) hidden");
}

/// <summary>The action camera's crosshair: a small four-arm reticle at the viewport centre, drawn as a registered
/// window with no rect (it hits nothing), only while <see cref="Visible"/>. The solid texel is the text sheet's.</summary>
internal static class Crosshair
{
    internal static volatile bool Visible;
    private static Window? _window;
    private static VertexBatch? _batch;
    /// <summary>Arm length, gap from the centre and thickness, in px at GUI scale 1.</summary>
    private const float Arm = 8f, Gap = 3f, Thick = 2f;

    internal static void Register()
    {
        _window ??= new Window(0, 0, 0, 0) { Focusable = false, DrawContent = Draw, HitTestOverride = (_, _) => false };
    }

    private static unsafe void Draw()
    {
        if (!Visible) return;
        GlyphAtlas? sheet = TextRig.Atlas;
        if (sheet == null) return;
        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        float s = Metrics.GameUiScale < 1f ? 1f : Metrics.GameUiScale;
        float cx = vp[2] * 0.5f, cy = vp[3] * 0.5f, a = Arm * s, g = Gap * s, t = Thick * s * 0.5f;
        VertexBatch b = _batch ??= new VertexBatch(sheet.SolidU, sheet.SolidV);
        b.Reset(sheet.SolidU, sheet.SolidV);
        // Dark outline under a light cross so it reads on any ground.
        Emit(b, cx, cy, a + 1f, g - 1f, t + 1f, 0f, 0f, 0f, 0.6f);
        Emit(b, cx, cy, a, g, t, 1f, 1f, 1f, 0.9f);
        TextRig.DrawBatch(b.AsSpan(), b.VertexCount);
    }

    private static void Emit(VertexBatch b, float cx, float cy, float a, float g, float t, float r, float gr, float bl, float al)
    {
        b.Rect(cx - a, cy - t, cx - g, cy + t, r, gr, bl, al);
        b.Rect(cx + g, cy - t, cx + a, cy + t, r, gr, bl, al);
        b.Rect(cx - t, cy - a, cx + t, cy - g, r, gr, bl, al);
        b.Rect(cx - t, cy + g, cx + t, cy + a, r, gr, bl, al);
    }
}
