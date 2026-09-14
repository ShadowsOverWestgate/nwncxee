using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>The <c>chat</c> group (spec §4.2): the Restart master (<see cref="Enabled"/>, read through <c>Boot</c>
/// -- off, <c>ChatService.Initialize</c> installs nothing and the stock chat window runs), the one User row
/// (<see cref="TellOpen"/>, a <see cref="TellAutoOpen"/> stored as its int), and the Hidden per-window state
/// (<see cref="Windows"/>: every <c>w&lt;N&gt;.*</c> key <see cref="ChatConfig"/> writes and reads, edited on
/// each window's own gear, outside <c>/chat reset</c>). Touched from <c>ChatService.Initialize</c> so the
/// group declares whether or not the chatbox comes up.</summary>
internal static class ChatKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<int> TellOpen;
    internal static readonly KnobBag Windows;

    static ChatKnobs()
    {
        G = KnobGroup.Declare("chat", "UI", "Chat", master: "Enable the CX chatbox",
            masterHelp: "CX's own chat: multiple windows, tabs, portraits, tells routing. Off, the stock chat window runs. Takes effect at the next launch.",
            masterEffect: Effect.Restart,
            header: "Window colours, animation and tabs are on each window's own gear.");
        Enabled = G.Master!;
        TellOpen = G.Dropdown("tellopen", "When a tell arrives", new[] { 0, 1, 2 }, 0,
                              new[] { "nothing", "open a tab", "open a window" },
                              help: "Where a tell from someone new lands: nowhere special, a new tab in the main window, or its own window.");
        Windows = G.Bag("w");
    }

    /// <summary>The tell routing mode <see cref="TellOpen"/> stores.</summary>
    internal static TellAutoOpen TellMode => (TellAutoOpen)TellOpen.V;
}
