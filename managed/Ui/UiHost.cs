using System;
using System.Collections.Generic;
using Sowx.Framework;

namespace Sowx.Ui;

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
public static class UiHost
{
    private static readonly List<Window> _windows = new();
    private static readonly List<IChordConsumer> _chords = new();
    private static int _zCounter;

    /// <summary>Focus-mode preference (AC #6): false = click-to-focus (default), true = hover-to-focus. A
    /// framework-global set by the user config; the input layer loads it on the deferred first-frame path.</summary>
    public static bool HoverToFocus;

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
    /// and drive the centralized engine gate through <see cref="KeyboardGate"/>. There is one keyboard owner at
    /// a time, so the framework need not resolve contention here. The gate itself edge-gates the drive-latch reset.
    /// Contract: a window that takes the keyboard MUST wire <see cref="Window.OnKey"/> -- the owner consumes every
    /// key (that is how it withholds gameplay keybinds), so an owner without a handler would swallow all input.</summary>
    public static void RequestKeyboard(Window w, bool on)
    {
        w.WantsKeyboard = on;
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
    /// the same O(n^2) z-walk the draw roll-call uses, no buffer. An unconsumed press then falls to the chord layer.</summary>
    public static bool DispatchMouseButton(bool down, int button, int x, int y)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool modal = pass == 0;
            int lastZ = int.MaxValue;
            for (int done = 0; done < _windows.Count; done++)
            {
                Window? next = NextBelow(lastZ);
                if (next == null) break;
                lastZ = next.Z;
                if (next.SuppressChords != modal) continue;
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

    /// <summary>Route a wheel notch to window content, topmost-first; true at the first consumer. Zero-alloc z-walk.</summary>
    public static bool DispatchMouseWheel(int dy)
    {
        int lastZ = int.MaxValue;
        for (int done = 0; done < _windows.Count; done++)
        {
            Window? next = NextBelow(lastZ);
            if (next == null) break;
            lastZ = next.Z;
            if (next.OnMouseWheel != null && next.OnMouseWheel(dy)) return true;
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

    private sealed class TestChord : IChordConsumer
    {
        private readonly int _key;
        public int Priority { get; }
        public int Fired = -1, FiredMouse = -1;
        public TestChord(int priority, int key) { Priority = priority; _key = key; }
        public bool WantsKey(int key) => key == _key;
        public void Fire(uint key, int sdlMods) => Fired = (int)key;
        public void FireMouse(int button, int sdlMods) => FiredMouse = button;
    }

    /// <summary>Off-device proof of the arbiter (AC #4): layout/z-order, focus, hit-test/cursor pick, and
    /// precedence, at two scales, leaving the static registry empty. No GL, no device.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void E(bool c, string w) { if (!c) { ok = false; log.Info($"UiHost.SelfCheck FAIL: {w}"); } }

        float savedScale = Metrics.Scale;
        var created = new List<Window>();
        var chords = new List<IChordConsumer>();

        // Hermetic isolation: the arbiter is a live static singleton and other services Initialize first
        // (discovery is sorted by name). Snapshot and clear the registry so the self-test neither sees nor
        // mutates production windows, then restore in finally.
        var savedWindows = new List<Window>(_windows);
        var savedChords = new List<IChordConsumer>(_chords);
        var savedFocused = Focused;
        int savedZ = _zCounter;
        bool savedHover = HoverToFocus;
        Action<bool>? savedGate = KeyboardGate;
        _windows.Clear(); _chords.Clear(); Focused = null; HoverToFocus = false; KeyboardGate = null;
        try
        {
            // Geometry checks run at 1.0 and 2.0 -- built-in cursor zones must track Metrics.Scale.
            foreach (float scale in new[] { 1f, 2f })
            {
                Metrics.Scale = scale;

                var a = new Window(100, 100, 200, 150); created.Add(a);
                var b = new Window(150, 120, 200, 150); created.Add(b);   // overlaps a, created later -> on top

                E(b.Z > a.Z, "later window gets the higher z");
                E(HitTest(160, 130) == b, "overlap hit-test returns the topmost window");
                E(HitTest(110, 110) == a, "non-overlap hit-test returns the only window there");
                E(HitTest(9999, 9999) == null, "hit-test miss returns null");

                Focus(a);
                E(Focused == a, "focus sets the single owner");
                E(a.Z > b.Z, "focus raises the window to the top");
                E(HitTest(160, 130) == a, "the raised window now wins the overlap");
                Focus(b);
                E(Focused == b, "focusing another replaces the single owner");

                // non-focusable overlay: skipped by focus hit-test, still drives the cursor (AC #3 / US #11)
                var overlay = new Window(150, 120, 60, 60) { Focusable = false }; created.Add(overlay);
                overlay.Regions.Add(new HitRegion(150, 120, 60, 60, CursorKind.Link));
                E(HitTest(160, 130) == overlay, "topmost overlay wins the cursor hit-test");
                E(PickCursor(160, 130) == CursorKind.Link, "cursor follows the overlay's region");
                E(HitTestFocusable(160, 130) == b, "focus hit-test skips the non-focusable overlay");
                E(FocusAt(160, 130) == b, "click through the overlay focuses the window beneath");

                // cursor from capability flags, scaled: grip and drag handle track Metrics.Scale
                var chrome = new Window(0, 0, 300, 200) { Draggable = true, Resizable = true }; created.Add(chrome);
                Focus(chrome);   // raise it above the others so its cursor wins the interior points
                E(PickCursor(1, 1) == CursorKind.Move, "draggable title strip picks Move");
                E(PickCursor(299, 199) == CursorKind.Resize, "resizable grip corner picks Resize");
                E(PickCursor(150, 150) == CursorKind.Default, "window interior picks Default");
                E(PickCursor(9999, 9999) == CursorKind.None, "no window under pointer picks None");

                foreach (var w in created) Unregister(w);
                created.Clear();
                E(Windows.Count == 0, "registry empty between scales");
            }

            // draw roll-call: DrawFrame invokes DrawContent bottom-up in z-order (focused/topmost last).
            var order = new List<int>();
            var d0 = new Window(0, 0, 10, 10); created.Add(d0); d0.DrawContent = () => order.Add(0);
            var d1 = new Window(0, 0, 10, 10); created.Add(d1); d1.DrawContent = () => order.Add(1);
            var d2 = new Window(0, 0, 10, 10); created.Add(d2); d2.DrawContent = () => order.Add(2);
            DrawFrame();
            E(order.Count == 3 && order[0] == 0 && order[1] == 1 && order[2] == 2,
              "draw roll-call runs every window's content once, in ascending z-order");
            order.Clear();
            Focus(d0);   // raising d0 to the top must make it paint last
            DrawFrame();
            E(order.Count == 3 && order[2] == 0, "focus raises a window to paint last");
            var inert = new Window(0, 0, 10, 10); created.Add(inert);   // null DrawContent -> skipped, no throw
            order.Clear(); DrawFrame();
            E(order.Count == 3, "a window with no content draw is skipped without faulting");
            foreach (var w in created) Unregister(w);
            created.Clear();

            // input precedence: gate vs chord consumers vs gameplay keybinds
            var win = new Window(0, 0, 100, 100); created.Add(win);
            var chord = new TestChord(priority: 10, key: 42); chords.Add(chord); AddChordConsumer(chord);

            Focus(win);
            E(!KeyboardOwned, "focus alone does not own the keyboard");
            E(ResolveKey(42) == InputLayer.ChordConsumer, "gate clear: a chord consumer takes its key");
            E(ResolveKey(7) == InputLayer.GameplayKeybind, "gate clear: an unclaimed key falls to gameplay keybinds");

            var hi = new TestChord(priority: 20, key: 42); chords.Add(hi); AddChordConsumer(hi);
            E(ResolveChord(42) == hi, "the higher-priority consumer wins the shared key");

            win.WantsKeyboard = true;
            E(KeyboardOwned, "the focused window asserting WantsKeyboard sets the gate");
            E(ResolveKey(42) == InputLayer.FocusedContent, "gate set: focused content eats even a chord's key");
            E(ResolveKey(7) == InputLayer.FocusedContent, "gate set: focused content eats gameplay keys too");

            // stale owner: unregistering the focused, keyboard-owning window drops the gate
            Unregister(win); created.Remove(win);
            E(Focused == null && !KeyboardOwned, "unregistering the owner clears focus and drops the gate");

            // ---- live routing (#64): mouse/key/text dispatch, keyboard gate, chord fall-through, focus mode ----
            {
                // Two stacked windows; each records the events it consumes. Overlay (higher z) is topmost.
                var hits = new List<string>();
                var lo = new Window(0, 0, 100, 100); created.Add(lo);
                var upper = new Window(0, 0, 100, 100); created.Add(upper);   // registered later -> higher z -> topmost
                lo.OnMouseButton = (d, b, x, y) => { hits.Add("lo.btn"); return false; };   // never consumes
                upper.OnMouseButton = (d, b, x, y) => { hits.Add("up.btn"); return true; }; // consumes
                E(DispatchMouseButton(true, 1, 5, 5), "mouse button consumed by the topmost window");
                E(hits.Count == 1 && hits[0] == "up.btn", "topmost-first: the lower window never sees a consumed press");
                hits.Clear();
                upper.OnMouseButton = (d, b, x, y) => { hits.Add("up.btn"); return false; };   // now passes through
                E(!DispatchMouseButton(true, 1, 5, 5), "no window consumes -> falls through");
                E(hits.Count == 2 && hits[0] == "up.btn" && hits[1] == "lo.btn", "unconsumed press walks top-down to the lower window");

                // A modal (SuppressChords) window wins the press ahead of a higher-z non-modal one.
                hits.Clear();
                lo.SuppressChords = true;
                lo.OnMouseButton = (d, b, x, y) => { hits.Add("lo.btn"); return true; };
                E(DispatchMouseButton(true, 1, 5, 5) && hits.Count == 1 && hits[0] == "lo.btn",
                  "modal window wins the press before a higher-z non-modal window");
                lo.SuppressChords = false;
                lo.OnMouseButton = (d, b, x, y) => { hits.Add("lo.btn"); return false; };
                hits.Clear();

                // Motion + wheel route the same top-down way.
                bool motion = false, wheel = false;
                upper.OnMouseMotion = (x, y) => { motion = true; return true; };
                upper.OnMouseWheel = dy => { wheel = true; return true; };
                E(DispatchMouseMotion(5, 5) && motion, "motion routes to a window");
                E(DispatchMouseWheel(1) && wheel, "wheel routes to a window");

                // Keys: a full owner eats everything; a selective consumer eats only what it handles.
                var keys = new List<uint>();
                lo.OnKey = (k, m) => { keys.Add(k); return k == 9; };   // selective: eats key 9 only
                E(!DispatchKey(7, 0), "no owner + selective consumer rejects -> key falls through");
                E(DispatchKey(9, 0), "selective consumer eats its key");
                keys.Clear();
                upper.OnKey = (k, m) => { keys.Add(k); return false; };
                RequestKeyboard(upper, true);   // upper becomes the full keyboard owner
                E(DispatchKey(7, 0) && keys.Count == 1 && keys[0] == 7, "the keyboard owner eats every key, first");
                E(!lo.WantsKeyboard, "the lower window is not the owner");

                // Text goes to the owner's OnText.
                bool gotText = false;
                upper.OnText = _ => gotText = true;
                E(DispatchText(IntPtr.Zero) && gotText, "text routes to the keyboard owner");
                RequestKeyboard(upper, false);
                E(!DispatchText(IntPtr.Zero), "no owner -> text is dropped");

                // Keyboard gate: RequestKeyboard drives the framework gate callback on each call.
                int gateCalls = 0; bool gateLast = false;
                KeyboardGate = on => { gateCalls++; gateLast = on; };
                RequestKeyboard(upper, true);
                E(gateCalls == 1 && gateLast && upper.WantsKeyboard, "RequestKeyboard(on) sets WantsKeyboard and drives the gate");
                RequestKeyboard(upper, false);
                E(gateCalls == 2 && !gateLast && !upper.WantsKeyboard, "RequestKeyboard(off) clears WantsKeyboard and drives the gate");
                KeyboardGate = null;

                // Chord suppression + fall-through fire.
                var tc = new TestChord(priority: 5, key: 3); chords.Add(tc); AddChordConsumer(tc);
                E(!AnySuppressChords, "no window suppresses chords by default");
                upper.SuppressChords = true;
                E(AnySuppressChords, "a window can withhold chords");
                upper.SuppressChords = false;
                FireChords(3, 0); FireMouseChords(2, 0);
                E(tc.Fired == 3 && tc.FiredMouse == 2, "fall-through key/mouse fire reaches the chord consumer");
                RemoveChordConsumer(tc); chords.Remove(tc);

                // HitTestOverride widens the hit-test past the rect (chrome outside the frame).
                lo.HitTestOverride = (px, py) => px >= 0 && px < 500;   // wider than the 100px rect
                E(HitTest(300, 5) == lo, "hit-test honours a window's override region outside its rect");
                lo.HitTestOverride = null;

                // Focus mode: hover-to-focus focuses on motion only when enabled; click-to-focus is the default.
                E(HoverFocusAt(5, 5) == null, "click-to-focus (default): hover does not focus");
                HoverToFocus = true;
                E(HoverFocusAt(5, 5) == upper && Focused == upper, "hover-to-focus: the topmost focusable window under the pointer takes focus");
                HoverToFocus = false;

                Unregister(lo); created.Remove(lo);
                Unregister(upper); created.Remove(upper);
                SetFocused(null);
            }

            // hygiene: every fake the test created is gone (checked against the cleared baseline, pre-restore)
            E(Windows.Count == 0, "all self-check windows unregistered");
        }
        finally
        {
            foreach (var w in created) Unregister(w);
            foreach (var c in chords) RemoveChordConsumer(c);
            _windows.Clear(); _windows.AddRange(savedWindows);
            _chords.Clear(); _chords.AddRange(savedChords);
            Focused = savedFocused;
            _zCounter = savedZ;
            HoverToFocus = savedHover;
            KeyboardGate = savedGate;
            Metrics.Scale = savedScale;
        }

        log.Info(ok ? "UiHost.SelfCheck: passed" : "UiHost.SelfCheck: FAILED");
    }
}
