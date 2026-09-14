using System;

namespace CX.Framework;

/// <summary>
/// The game-session predicates every layer branches on (ADR 0014). These are not UI concepts -- they arm the
/// entire 3D pipeline as much as they gate a chat box -- so they live in the framework and UI consumes them
/// like everyone else. The frame host (<c>CX.Ui.UiHostService</c>) is the sole writer of
/// <see cref="InGame"/> / <see cref="InModule"/>, once per frame; everything else reads.
/// </summary>
public static class GameGate
{
    /// <summary>The in-game gate, computed once per frame by the frame host: true when a module is loaded and
    /// no full-screen menu / hidden-HUD is up. Content draws read it to branch (chat draws its box only
    /// in-game). Volatile: written on the render thread, read by input-thread callers.</summary>
    public static volatile bool InGame;

    /// <summary>Whether a module is loaded at all (the gate's chat-window pointer is non-null). Distinct from
    /// <see cref="InGame"/>: true even while the ESC menu is up. False = main menu. Content uses the
    /// true->false edge as the module-unload signal (session reset) and the false state to draw menu overlays.</summary>
    public static volatile bool InModule;

    /// <summary>Framework opt-OUT (#23, the extender-optional guarantee): <c>lockchat=1</c> in <c>cx.cfg</c>
    /// stands the whole extender down -- the frame host installs no hook, so nothing owns the present and
    /// vanilla rendering is untouched. The single pivot every service reads.
    /// <para>Config, not a command (ADR 0013 bucket 2): it decides what hooks at boot, and it is read on
    /// every content draw, so it latches into a field rather than re-reading per frame.</para></summary>
    public static bool Locked => _locked;

    private static readonly bool _locked = Settings.Bool("lockchat", false);

    /// <summary>True when the framework owns the <c>SwapBuffers</c> present (the default path). False under
    /// <c>lockchat</c>. Frame-present-coupled services (Dialog and friends) read this: funchook
    /// single-hooks a symbol, so exactly one owner of SwapBuffers may install.</summary>
    public static bool OwnsFramePresent => !Locked;
}
