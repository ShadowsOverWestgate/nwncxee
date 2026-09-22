using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Ui;

/// <summary>Which layer of the fixed input precedence consumes a key (US #8).</summary>
public enum InputLayer
{
    /// <summary>The focused window owns the keyboard (typing): it eats every key; chords and keybinds withheld.</summary>
    FocusedContent,
    /// <summary>The gate is clear and a registered chord consumer claimed the key.</summary>
    ChordConsumer,
    /// <summary>Nobody claimed it -- the engine's gameplay keybinds get it.</summary>
    GameplayKeybind,
}

/// <summary>A global hotkey owner that consumes chords without owning a Window (US #12). Registered with a
/// priority; the highest-priority consumer wanting a key wins, and only while no window owns the keyboard.
/// <see cref="WantsKey"/> is the off-device arbiter query (spec #61); <see cref="Fire"/> / <see cref="FireMouse"/>
/// are the live entry points (#64) the framework calls once a key/button has fallen past every window and the
/// chord gate is clear -- the consumer self-gates on its own arm/consent/debounce state and does the send.</summary>
public interface IChordConsumer
{
    int Priority { get; }
    bool WantsKey(int key);
    /// <summary>A key press fell through to the chord layer. <paramref name="sdlMods"/> is the raw SDL modifier
    /// state (the consumer maps it to its own wire encoding).</summary>
    void Fire(uint key, int sdlMods);
    /// <summary>A mouse button press fell through to the chord layer (mouse buttons are chords too).</summary>
    void FireMouse(int button, int sdlMods);
}

/// <summary>The pure-C# UI arbiter (spec #61): a static registry of <see cref="Window"/>s plus the
/// value-in/decision-out logic every window needs -- z-order, top-down hit-test, cursor pick, one global
/// keyboard owner, and the fixed input-precedence chain. No GL, no native refs, nothing hooked; the frame host
/// (<c>UiHostService</c>, #62) and the input layer (<c>UiInputService</c>, #64) supply the raw events. The
/// per-frame reads (<see cref="HitTest"/>, <see cref="PickCursor"/>, <see cref="DrawFrame"/>) are zero-alloc --
/// plain loops, no LINQ, no closures, no sorting -- because they run on the render thread inside SwapBuffers
/// where churn is a visible stutter.
/// Cross-thread discipline: the window/chord registry is populated at service init and is immutable thereafter
/// (no Register/Unregister after the first frame), so <see cref="DrawFrame"/>'s render-thread read of the list
/// is race-free without a lock. Input now lands live (#64): <see cref="Focus"/> / <see cref="RequestKeyboard"/>
/// / <see cref="HoverFocusAt"/> run on the input thread while the render thread reads <c>Z</c>/<c>Focused</c> in
/// the same frame -- but those are word-sized writes, so the worst case is one frame of stale z-order, which is
/// benign. ponytail: if a window ever mutates <see cref="Window.Regions"/> live, snapshot-or-swap it (a
/// mid-growth list read on the cursor thread is a real crash) -- today's consumers use a hit-test override, not
/// Regions, so nothing grows.</summary>
public static partial class UiHost
{
    private static readonly List<Window> _windows = new();
    private static readonly List<IChordConsumer> _chords = new();
    private static int _zCounter;

    /// <summary>Focus-mode preference (AC #6): false = click-to-focus (default), true = hover-to-focus. The
    /// <c>ui.hoverfocus</c> knob is the storage; the setter is the SelfCheck's (a plain value write, no set).</summary>
    public static bool HoverToFocus { get => UiKnobs.HoverFocus.V; set => UiKnobs.HoverFocus.V = value; }

    /// <summary>The engine keyboard-capture gate, owned by the framework input layer (#64). It drives
    /// <c>CExoInput::KeyboardMode{On,Off}</c> plus the drive-latch reset (<c>TurnOffKeyboardModal</c>, #34) on
    /// the off→on edge, once for every consumer. Null off-device / under LOCKCHAT (no input layer installed).</summary>
    public static Action<bool>? KeyboardGate;

    /// <summary>The single window that owns focus -- the candidate keyboard owner. Null when nothing is focused.</summary>
    public static Window? Focused { get; private set; }

    /// <summary>The keyboard-owned gate: true only when the focused window is asserting keyboard ownership.
    /// Focus alone does NOT set it -- a focused-but-idle window still lets gameplay keys through.</summary>
    public static bool KeyboardOwned => Focused is { WantsKeyboard: true };

    public static IReadOnlyList<Window> Windows => _windows;

    internal static void Register(Window w)
    {
        w.Z = ++_zCounter;
        _windows.Add(w);
    }

    /// <summary>Z handed out below every registration: <see cref="Register"/> only ever assigns positive
    /// values, so a decreasing counter starting at 0 keeps a sent-back window under all of them.</summary>
    private static int _backZ;

    /// <summary>Push a window under every other one, permanently. Registration order is discovery order,
    /// which is type-name sorted, so a window that must paint BELOW the chat box and the dialog panel
    /// (world-anchored nameplates) cannot get there by naming. <see cref="Focus"/> would raise it again,
    /// but only for a focusable window -- a back window sets <c>Focusable = false</c>.</summary>
    public static void SendToBack(Window w) => w.Z = --_backZ;

    /// <summary>Remove a window; if it held focus, clear the owner so the keyboard-owned gate drops
    /// (stale-owner safety -- a destroyed typing window must not keep gameplay keys suppressed).</summary>
    public static void Unregister(Window w)
    {
        _windows.Remove(w);
        if (Focused == w) SetFocused(null);
    }

    /// <summary>Focus a registered focusable window and raise it to the top of the z-order. No-op for a
    /// non-focusable or already-unregistered window (no dangling focus owner).</summary>
    public static void Focus(Window w)
    {
        if (!w.Focusable || !_windows.Contains(w)) return;
        SetFocused(w);
        w.Z = ++_zCounter;
    }

    /// <summary>Move focus, ending the outgoing window's keyboard-ownership. A window that loses focus is no
    /// longer typing, so its <see cref="Window.WantsKeyboard"/> is cleared -- otherwise re-focusing it would
    /// silently re-raise the gate and suppress gameplay keys (the #34 stuck-input class).</summary>
    private static void SetFocused(Window? w)
    {
        if (Focused == w) return;
        if (Focused != null) Focused.WantsKeyboard = false;
        Focused = w;
    }

    /// <summary>Release focus, as a click on empty space does. For a click that lands somewhere the host
    /// cannot hit-test -- over a NUI window, which draws on top of us and takes the press itself.</summary>
    public static void Blur() => SetFocused(null);

    /// <summary>Topmost window (any) whose live UI covers the point, or null. Drives the cursor -- overlays
    /// count. Uses <see cref="Window.Hit"/> so a window's chrome outside its rect (popped-out strip, dropdown)
    /// still registers.</summary>
    public static Window? HitTest(float px, float py)
    {
        Window? best = null;
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            if (w.Hit(px, py) && (best == null || w.Z > best.Z)) best = w;
        }
        return best;
    }

    /// <summary>Topmost FOCUSABLE window under the point (AC #3: non-focusable windows are skipped for focus,
    /// so a click passes through an overlay to the focusable window beneath it).</summary>
    public static Window? HitTestFocusable(float px, float py)
    {
        Window? best = null;
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            if (w.Focusable && w.Hit(px, py) && (best == null || w.Z > best.Z)) best = w;
        }
        return best;
    }

    /// <summary>Click-to-focus resolution: focus the topmost focusable window under the point and return it;
    /// a click on empty space (no window at all) releases focus; a click on a non-focusable overlay leaves
    /// focus unchanged.</summary>
    public static Window? FocusAt(float px, float py)
    {
        var w = HitTestFocusable(px, py);
        if (w != null) Focus(w);
        else if (HitTest(px, py) == null) SetFocused(null);
        return w;
    }

    /// <summary>Hover-to-focus resolution (AC #6): when <see cref="HoverToFocus"/> is set, focus the topmost
    /// focusable window under the point as the pointer moves over it. No-op (returns null) in click-to-focus
    /// mode; empty space never releases focus on hover (only a click does).</summary>
    public static Window? HoverFocusAt(float px, float py)
    {
        if (!HoverToFocus) return null;
        var w = HitTestFocusable(px, py);
        if (w != null) Focus(w);
        return w;
    }

    /// <summary>The cursor under the pointer: the topmost window's pick, or <see cref="CursorKind.None"/> when
    /// no window is there (the engine cursor stands).</summary>
    public static CursorKind PickCursor(float px, float py)
    {
        var w = HitTest(px, py);
        return w == null ? CursorKind.None : w.CursorAt(px, py);
    }

    /// <summary>Route the keyboard capture on/off for a window (#64): flip its <see cref="Window.WantsKeyboard"/>
    /// and drive the centralized engine gate through <see cref="KeyboardGate"/>. A release while another window
    /// still owns the keyboard leaves the gate up (#588: <c>/settings</c> takes the keyboard inside chat's submit,
    /// and chat releases its own right after). The gate itself edge-gates the drive-latch reset.
    /// Contract: a window that takes the keyboard MUST wire <see cref="Window.OnKey"/> -- the owner consumes every
    /// key (that is how it withholds gameplay keybinds), so an owner without a handler would swallow all input.</summary>
    public static void RequestKeyboard(Window w, bool on)
    {
        w.WantsKeyboard = on;
        if (!on && AnyKeyboardOwner) return;
        KeyboardGate?.Invoke(on);
    }

    /// <summary>True while any registered window is withholding chord consumers (a modal window is up). The
    /// live input layer reads it to decide whether an unconsumed key/button may fall to the hotkey chords.</summary>
    public static bool AnySuppressChords
    {
        get
        {
            for (int i = 0; i < _windows.Count; i++) if (_windows[i].SuppressChords) return true;
            return false;
        }
    }

    /// <summary>True while a window is the full keyboard owner (chat typing). The pump-level swallow is gated on
    /// its negation -- an owner already suppresses engine keybinds via the keyboard-mode gate, so no swallow.</summary>
    public static bool AnyKeyboardOwner
    {
        get
        {
            for (int i = 0; i < _windows.Count; i++) if (_windows[i].WantsKeyboard) return true;
            return false;
        }
    }

    /// <summary>Pump-level swallow (#64): a modal <see cref="Window.SuppressChords"/> window that consumes a key
    /// at the SDL pump, before the engine queues its scancode keybind (<c>CExoInput::AddEvent</c> runs
    /// pre-HandleWMKeyDown with no modifier check, so Ctrl+C would still fire the plain-C keybind). Only
    /// SuppressChords windows swallow here; the keyboard owner suppresses keybinds via the keyboard-mode gate.
    /// Topmost-first; true when one consumed the key.</summary>
    public static bool DispatchKeyPumpSwallow(uint key, int sdlMods)
    {
        int lastZ = int.MaxValue;
        for (int done = 0; done < _windows.Count; done++)
        {
            Window? next = NextBelow(lastZ);
            if (next == null) break;
            lastZ = next.Z;
            if (next.SuppressChords && next.OnKey != null && next.OnKey(key, sdlMods)) return true;
        }
        return false;
    }

    /// <summary>Route a mouse button (press/release) to window content and return true at the first window that
    /// consumes it. Two topmost-first passes: modal windows (<see cref="Window.SuppressChords"/>, e.g. an open
    /// dialog) win the click first -- restoring the "modal: dialog wins the click" precedence -- then the rest.
    /// Each window is visited exactly once (in the pass matching its modal flag), so no double-invoke. Zero-alloc:
    /// the same O(n^2) z-walk the draw roll-call uses, no buffer. An unconsumed press then falls to the chord layer.
    /// <paramref name="captureOnly"/> visits just the <see cref="Window.CapturesPointer"/> windows: the pump's extra
    /// pass BEFORE the engine-GUI yield (CX-690), so every other window keeps the old precedence.</summary>
    public static bool DispatchMouseButton(bool down, int button, int x, int y, bool captureOnly = false)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool modal = pass == 0;
            if (captureOnly && !modal) break;
            int lastZ = int.MaxValue;
            for (int done = 0; done < _windows.Count; done++)
            {
                Window? next = NextBelow(lastZ);
                if (next == null) break;
                lastZ = next.Z;
                if (next.SuppressChords != modal || (captureOnly && !next.CapturesPointer)) continue;
                if (next.OnMouseButton != null && next.OnMouseButton(down, button, x, y)) return true;
            }
        }
        return false;
    }

    /// <summary>Route pointer motion to window content, topmost-first; true at the first consumer (a drag owns
    /// motion until release, so handlers self-gate on their own grab state). Zero-alloc z-walk.</summary>
    public static bool DispatchMouseMotion(int x, int y)
    {
        int lastZ = int.MaxValue;
        for (int done = 0; done < _windows.Count; done++)
        {
            Window? next = NextBelow(lastZ);
            if (next == null) break;
            lastZ = next.Z;
            if (next.OnMouseMotion != null && next.OnMouseMotion(x, y)) return true;
        }
        return false;
    }

    /// <summary>Route a wheel notch to window content, topmost-first, modal windows first (the same two passes and
    /// the same <paramref name="captureOnly"/> as <see cref="DispatchMouseButton"/>); true at the first consumer.
    /// Zero-alloc z-walk.</summary>
    public static bool DispatchMouseWheel(int dy, bool captureOnly = false)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool modal = pass == 0;
            if (captureOnly && !modal) break;
            int lastZ = int.MaxValue;
            for (int done = 0; done < _windows.Count; done++)
            {
                Window? next = NextBelow(lastZ);
                if (next == null) break;
                lastZ = next.Z;
                if (next.SuppressChords != modal || (captureOnly && !next.CapturesPointer)) continue;
                if (next.OnMouseWheel != null && next.OnMouseWheel(dy)) return true;
            }
        }
        return false;
    }

    /// <summary>The registered window with the highest z strictly below <paramref name="belowZ"/>, or null.
    /// Zero-alloc descending-z scan (z values are unique, from the monotonic counter).</summary>
    private static Window? NextBelow(int belowZ)
    {
        Window? next = null;
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            if (w.Z < belowZ && (next == null || w.Z > next.Z)) next = w;
        }
        return next;
    }

    /// <summary>The fixed key precedence, live (#64). First a full keyboard owner (chat typing) eats every key;
    /// then selective consumers (a modal dialog) top-down eat what they handle. Returns true when a window
    /// consumed the key -- the caller then does NOT run the engine's original handler. An unconsumed key falls
    /// to the chord layer (subject to <see cref="AnySuppressChords"/>) and then gameplay keybinds.</summary>
    public static bool DispatchKey(uint key, int sdlMods)
    {
        // Pass 1: the single full keyboard owner eats everything, regardless of z (matches chat-first today).
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            if (w.WantsKeyboard) { w.OnKey?.Invoke(key, sdlMods); return true; }
        }
        // Pass 2: selective consumers, topmost-first.
        int lastZ = int.MaxValue;
        for (int done = 0; done < _windows.Count; done++)
        {
            Window? next = NextBelow(lastZ);
            if (next == null) break;
            lastZ = next.Z;
            if (next.OnKey != null && next.OnKey(key, sdlMods)) return true;
        }
        return false;
    }

    /// <summary>Deliver finished text (SDL_TEXTINPUT payload) to the keyboard owner's <see cref="Window.OnText"/>.
    /// Returns true when an owner took it. No owner -> false (the text is dropped, as gameplay has no text sink).</summary>
    public static bool DispatchText(IntPtr payload)
    {
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            if (w.WantsKeyboard && w.OnText != null) { w.OnText(payload); return true; }
        }
        return false;
    }

    /// <summary>Fire a fallen-through key at every registered chord consumer; each self-gates on its own
    /// arm/consent/debounce state. ponytail: fire-all (one consumer today); switch to a priority resolve
    /// (<see cref="ResolveChord"/>) only if two consumers ever contend for the same key.</summary>
    public static void FireChords(uint key, int sdlMods)
    {
        for (int i = 0; i < _chords.Count; i++) _chords[i].Fire(key, sdlMods);
    }

    /// <summary>Fire a fallen-through mouse button at every registered chord consumer (self-gating).</summary>
    public static void FireMouseChords(int button, int sdlMods)
    {
        for (int i = 0; i < _chords.Count; i++) _chords[i].FireMouse(button, sdlMods);
    }

    /// <summary>The per-frame draw roll-call: invoke every registered window's <see cref="Window.DrawContent"/>
    /// in ascending z-order (bottom-up, so the focused/topmost window paints last). Zero-alloc -- an O(n^2)
    /// min-scan over the z values (unique, from the monotonic counter) with no sort buffer, no LINQ, no
    /// closures -- because it runs on the render thread inside SwapBuffers. n is a handful of windows.
    /// ponytail: O(n^2) is free at this n; swap for a kept-sorted list only if the window count ever grows big.</summary>
    public static void DrawFrame()
    {
        int lastZ = int.MinValue;
        for (int drawn = 0; drawn < _windows.Count; drawn++)
        {
            Window? next = null;
            for (int i = 0; i < _windows.Count; i++)
            {
                var w = _windows[i];
                if (w.Z > lastZ && (next == null || w.Z < next.Z)) next = w;
            }
            if (next == null) break;   // no window with a higher z -> done
            lastZ = next.Z;
            next.DrawContent?.Invoke();
        }
    }

    public static void AddChordConsumer(IChordConsumer c) { if (!_chords.Contains(c)) _chords.Add(c); }
    public static void RemoveChordConsumer(IChordConsumer c) => _chords.Remove(c);

    /// <summary>The highest-priority chord consumer wanting this key, or null. (The gate is not applied here;
    /// callers use <see cref="ResolveKey"/> for the full precedence, and this to reach the winner.)</summary>
    public static IChordConsumer? ResolveChord(int key)
    {
        IChordConsumer? pick = null;
        for (int i = 0; i < _chords.Count; i++)
        {
            var c = _chords[i];
            if (c.WantsKey(key) && (pick == null || c.Priority > pick.Priority)) pick = c;
        }
        return pick;
    }

    /// <summary>The fixed input precedence: keyboard-owned gate → chord consumers → gameplay keybinds. When a
    /// window owns the keyboard, its content eats every key and chords/keybinds are withheld. Uses
    /// <see cref="AnyKeyboardOwner"/> (not <see cref="KeyboardOwned"/>) to match the live path, where the owner
    /// need not be the framework-focused window (chat self-manages focus).</summary>
    public static InputLayer ResolveKey(int key)
    {
        if (AnyKeyboardOwner) return InputLayer.FocusedContent;
        return ResolveChord(key) != null ? InputLayer.ChordConsumer : InputLayer.GameplayKeybind;
    }
}
