using System;

namespace CX.Framework;

/// <summary>
/// The General category (spec §4.1): the <c>general</c> group -- the command gate, the live log toggle and the
/// three boot forensics re-keyed from their flat names -- and the knob-less <c>about</c> group. Declares on
/// first touch, which is <see cref="HookService"/>'s constructor reading <see cref="HookDebug"/> right after
/// <see cref="Settings.Load"/>, so it is the first group in the walk. Also owns the <c>/settings</c> door: the
/// one command <see cref="Commands"/> never gates, a stub that logs until the panel (#588) lands.
/// </summary>
public static class General
{
    public static readonly KnobGroup G;
    /// <summary>Off by default: every CX command but <c>/settings</c> then goes to the server as chat.</summary>
    public static readonly Knob<bool> Commands;
    /// <summary>Live twin of <see cref="ExtenderLog.Enabled"/>; <see cref="Settings.Load"/> reads the same key
    /// for the launch lines, before any knob exists.</summary>
    public static readonly Knob<bool> Logs;
    public static readonly Knob<bool> HookDebug;
    public static readonly Knob<int> PresentProbe, Pin;
    /// <summary>Seconds after the first present at which this run halves the window's width; twice that is
    /// where it puts the width back. 0 = off. A DEBUG trigger for the
    /// engine's own re-layout path (<c>SDL_SetWindowSize</c> -&gt;
    /// <c>SDL_WINDOWEVENT_SIZE_CHANGED</c> -&gt; <c>WinMessageHandler</c> @0x42f960 -&gt;
    /// <c>CGuiMan::SetGuiSize</c> @0x57efc0): a GUI that mishandles a resize can only be SEEN through that
    /// path, and the live harness runs inside a headless compositor with no window edge to drag.</summary>
    public static readonly Knob<int> ResizeAt;

    public static readonly KnobGroup About;

    static General()
    {
        G = KnobGroup.Declare("general", "General", "General",
            header: "No master switch: every feature has its own, and \"no CX\" is an uninstall.");
        Commands = G.Toggle("commands", "Enable chat commands", false,
            help: "Lets /grass, /shadow and the other CX commands work in chat. Off, they go to the server like any "
                + "other text — some servers use the same names. /settings always works.");
        Logs = G.Toggle("logs", "Write cx.log diagnostics", false, Tier.Advanced,
            help: "Writes the extender's diagnostic log to cx.log beside cx.cfg from now on; restart to capture the "
                + "launch lines too. Attach it to a bug report.",
            changed: () => ExtenderLog.Enabled = Logs!.V);
        HookDebug = G.Toggle("hookdebug", "hookdebug", false, Tier.ChatOnly, Effect.Restart);
        PresentProbe = G.Slider("presentprobe", "presentprobe", 0, 200, 1, 0, tier: Tier.ChatOnly, effect: Effect.Restart);
        Pin = G.Slider("pin", "pin", -1, 1023, 1, -1, tier: Tier.ChatOnly, effect: Effect.Restart);
        ResizeAt = G.Slider("resizeat", "resizeat", 0, 600, 1, 0, unit: "s",
            tier: Tier.ChatOnly, effect: Effect.Restart,
            help: "Debug: halve the window's width this many seconds in, and restore it at twice that, through "
                + "SDL and the engine's own panel re-layout. 0 = off.");
        CommandRegistry.Global.Gate = () => Commands.V;

        About = KnobGroup.Declare("about", "General", "About", header: "What this build is and where to report a problem.");
        About.Status = Product.About;
        About.Link("releases", "Releases", Product.Url);

        CommandRegistry.Global.Register("/settings", "open the CX settings panel", OpenSettings, exempt: true);
    }

    /// <summary>The door (#588): registered here, exempt, before any service, so it always dispatches.</summary>
    private static string? OpenSettings(string args) { CX.Ui.KnobPanelService.Open(); return null; }
}
