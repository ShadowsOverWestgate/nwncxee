using System;

namespace CX.Framework;

/// <summary>
/// The game-session predicates every layer branches on (ADR 0014): they arm the 3D pipeline as much as they gate a
/// chat box, so they live in the framework rather than in UI. The frame host (<c>CX.Ui.UiHostService</c>) is the
/// sole writer of every flag here, once per frame; everything else reads.
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

    /// <summary>The main menu is up: no module and the engine's live <c>CMainMenuPanel</c> exists. False on
    /// the loading screen, in character generation and the server browser (the menu panel is destroyed before
    /// each); true under the pregame Options modal, which only hides the panel. The gear and the settings
    /// panel draw on it; nothing else does.</summary>
    public static volatile bool AtMainMenu;

    /// <summary>The in-game Options panel (the ESC menu) is up and showing: a module is loaded, <c>CGuiInGame</c>'s
    /// live <c>COptionsPanel*</c> is non-null and its panel state is exactly "active" -- false again while a
    /// Game/Video/Controls sub-panel hides it, and on Resume/Exit. The gear and the settings panel draw on it.</summary>
    public static volatile bool OptionsOpen;

    /// <summary>The live <c>CGuiInGame*</c> the frame host already walked to for the gate above, or Zero outside a
    /// module: stashed rather than re-walked, since the walk is four dereferences off <c>g_pAppManager</c>. Valid
    /// only for the frame that wrote it. Not <c>volatile</c> -- C# forbids that on <see cref="IntPtr"/> -- but a
    /// pointer-sized store never tears on either target.</summary>
    public static IntPtr InGameGui;
}
