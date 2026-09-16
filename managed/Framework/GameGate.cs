using System;

namespace CX.Framework;

/// <summary>
/// The game-session predicates every layer branches on (ADR 0014). These are not UI concepts -- they arm the
/// entire 3D pipeline as much as they gate a chat box -- so they live in the framework and UI consumes them
/// like everyone else. The frame host (<c>CX.Ui.UiHostService</c>) is the sole writer of
/// <see cref="InGame"/> / <see cref="InModule"/> / <see cref="AtMainMenu"/> / <see cref="OptionsOpen"/> / <see cref="InGameGui"/>, once per frame; everything else reads. There is no whole-framework
/// opt-out any more (ADR 0025): every feature has its own master, and "no CX" is an uninstall.
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

    /// <summary>The main menu is up (#593): no module and the engine's live <c>CMainMenuPanel</c> exists. False on
    /// the loading screen, in character generation and the server browser (the menu panel is destroyed before
    /// each); true under the pregame Options modal, which only hides the panel. The gear and the settings
    /// panel draw on it; nothing else does.</summary>
    public static volatile bool AtMainMenu;

    /// <summary>The in-game Options panel (the ESC menu) is up and showing (#593 follow-up): a module is loaded,
    /// <c>CGuiInGame</c>'s live <c>COptionsPanel*</c> is non-null and its panel state is exactly "active" -- false
    /// again while a Game/Video/Controls sub-panel hides it, and on Resume/Exit. The gear and the settings panel
    /// draw on it, the way they draw on <see cref="AtMainMenu"/>; nothing else does.</summary>
    public static volatile bool OptionsOpen;

    /// <summary>The live <c>CGuiInGame*</c> the frame host already walked to for the gate above, or Zero outside a
    /// module. Stashed rather than re-walked: the walk is four dereferences off <c>g_pAppManager</c> and one
    /// source of it per frame is one place to be wrong. Read by content that needs a panel hanging off the
    /// in-game GUI (the crosshair's action queue); valid for the frame that drew it. Not <c>volatile</c> --
    /// C# forbids it on <see cref="IntPtr"/> -- but a pointer-sized store never tears on either target.</summary>
    public static IntPtr InGameGui;
}
