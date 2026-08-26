using System;

namespace Sowx.Framework;

/// <summary>
/// The game-session predicates every layer branches on (ADR 0014). These are not UI concepts -- they arm the
/// entire 3D pipeline as much as they gate a chat box -- so they live in the framework and UI consumes them
/// like everyone else. The frame host (<c>Sowx.Ui.UiHostService</c>) is the sole writer of
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

    /// <summary>Framework opt-OUT (#23, the extender-optional guarantee): <c>SOWX_LOCKCHAT=1</c> stands the whole
    /// extender down -- the frame host installs no hook, so nothing owns the present and vanilla rendering is
    /// untouched. The single env pivot every service reads.</summary>
    public static bool Locked =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOWX_LOCKCHAT"));

    /// <summary>True when the framework owns the <c>SwapBuffers</c> present (the default path). False under
    /// <c>SOWX_LOCKCHAT</c>. Frame-present-coupled services (Dialog and friends) read this: funchook
    /// single-hooks a symbol, so exactly one owner of SwapBuffers may install.</summary>
    public static bool OwnsFramePresent => !Locked;
}
