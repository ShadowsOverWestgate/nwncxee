using CX.Framework;

namespace CX.Services.Nameplate;

/// <summary>The <c>nameplate</c> group (spec §4.2) and the storage <see cref="NameplateService"/> reads on the
/// frame: the master (off = the engine's own bubble mode back, stock names and bubbles draw; live both ways,
/// #591), nine User rows, five Advanced, and the
/// Chat-only <see cref="Probe"/> button. <see cref="Queue"/>'s ladder ends at <see cref="PlateTable.MaxLines"/>
/// and <see cref="Lines"/> at <see cref="PlateTable.MaxWrapped"/>: the table cannot hold more, so a higher value
/// would report what the drain silently clamps away. Touched from <c>NameplateService.Initialize</c> so the
/// group declares whether or not the hooks arm.</summary>
internal static class NameplateKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Portraits, Background, Outline, HealthBand, HealthBar, ChannelStyle, ClickToTell;
    internal static readonly Knob<float> MaxDist, Width, FadeDist;
    internal static readonly Knob<int> Queue, Lines, Anim;
    internal static readonly KnobButton Probe;

    /// <summary>Design-px bounds of the text wrap width (bubbles AND plate names).</summary>
    internal const float WidthMin = 120f, WidthMax = 480f;
    /// <summary>Upper bound of the distance knobs, so a file cannot ask for an unbounded cull radius.</summary>
    internal const float DistMax = 100f;

    static NameplateKnobs()
    {
        G = KnobGroup.Declare("nameplate", "UI", "Nameplates", master: "Enable CX nameplates and chat bubbles",
            masterHelp: "Names, health colour and speech bubbles drawn by CX: sharper text, portraits, stacked lines. Off, the stock bubbles draw.");
        Enabled = G.Master!;
        Portraits = G.Toggle("portraits", "Portraits", true, help: "A portrait beside the name.");
        Background = G.Toggle("background", "Background", true, help: "A box behind the plate, as in the stock game.");
        Outline = G.Toggle("outline", "Text outline", true, help: "A dark edge around the letters so they read over bright ground.");
        HealthBand = G.Toggle("healthband", "Health colour", true, help: "The name is tinted by the creature's health, as in the stock game.");
        HealthBar = G.Toggle("healthbar", "Health bar", false, help: "A thin bar under the name. Not in the stock game.");
        MaxDist = G.Slider("maxdist", "Visible distance", 0f, DistMax, 5f, 45f, "m",
                           help: "Plates past this distance are not drawn. 0 draws every plate.");
        Queue = G.Dropdown("queue", "Bubble lines kept", new[] { 1, 2, 3, 4 }, 2,
                           help: "How many recent things someone said stack in their bubble. The stock game keeps 1.");
        Width = G.Slider("width", "Text width", WidthMin, WidthMax, 10f, 300f, "px",
                         help: "The widest a bubble or a name gets before wrapping.");
        ChannelStyle = G.Toggle("channelstyle", "Colour by channel", true, Tier.Advanced,
                                help: "Shouts, whispers and emotes get their own colour and emphasis.");
        ClickToTell = G.Toggle("clicktotell", "Click a name to tell", false, Tier.Advanced,
                               help: "Clicking the name starts a tell. Takes the click the world would have had.");
        FadeDist = G.Slider("fadedist", "Fade band", 0f, DistMax, 1f, 10f, "m", Tier.Advanced,
                            help: "Plates fade out over this distance before Visible distance.");
        Lines = G.Slider("lines", "Wrapped lines per bubble", 1, PlateTable.MaxWrapped, 1, PlateTable.MaxWrapped, tier: Tier.Advanced);
        Anim = G.Slider("anim", "Animation", 0, 1000, 10, 180, "ms", Tier.Advanced, help: "0 = plates pop in.");
        Probe = G.Button("probe", "probe", NameplateService.ArmProbe, Tier.ChatOnly);
    }
}
