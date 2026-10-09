using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The graphics quality presets (ADR 0030): one preset, a dropdown per area laddered on it, and every real knob
/// of an area laddered on that area (<see cref="Knob{T}.Ladder"/>). A ladder member's default IS its rung, so
/// <c>cx.cfg</c> keeps only what the player changed from the preset, and a later release's retune reaches every
/// knob nobody touched. High is the shipped defaults.
/// </summary>
internal static class Quality
{
    internal static readonly KnobGroup G;
    /// <summary>The stored values of every ladder driver here, index-aligned with the rung arrays members pass.</summary>
    internal static readonly string[] Levels = { "low", "medium", "high", "ultra" };
    private static readonly string[] LevelNames = { "Low", "Medium", "High", "Ultra" };
    internal static readonly Knob<string> Preset, Shadows, Lighting, Reflections, Ao, Detail, Grass, Weather, Effects;

    static Quality()
    {
        G = KnobGroup.Declare("quality", "Graphics", "Quality", order: 300, title: "Preset",
            header: "Pick a preset, then adjust any area. A changed setting elsewhere in Graphics keeps a blue dot "
                  + "until you pick a preset again.");
        G.PanelStatus = Status;
        Preset = G.Dropdown("preset", "Graphics quality", Levels, "high", LevelNames,
            help: "Sets every area below at once. Low is for integrated graphics; Ultra raises shadow and "
                + "reflection detail past the defaults. Some changes take effect at the next launch.");
        G.Section("Areas");
        Shadows = Area("shadows", "Shadows", "Shadow resolution, distance, softness and how many lights cast them.");
        Lighting = Area("lighting", "Lighting", "How many lights reach each pixel and how far their light carries.");
        Reflections = Area("reflections", "Reflections", "Reflections of the scene on glossy and wet surfaces.");
        Ao = Area("ao", "Ambient occlusion", "Contact darkening in corners and under objects (the game's own "
                                            + "Ambient Occlusion option still turns it on or off).");
        Detail = Area("detail", "Model detail", "How far models keep full detail, and how small a model may get before it is skipped.");
        Grass = Area("grass", "Grass", "How dense grass is and how far it draws.");
        Weather = Area("weather", "Weather", "Rain and snow density, splashes, and the wet and snowed ground.");
        Effects = Area("effects", "Effects", "Particles, light halos and other glows.");
    }

    private static Knob<string> Area(string key, string label, string help)
        => G.Dropdown(key, label, Levels, "high", LevelNames, help: help).Ladder(Preset, Levels);

    /// <summary>"High · 3 settings changed": the members (recursively) that left their rung.</summary>
    private static string Status()
    {
        int n = Changed(Preset);
        string level = LevelNames[System.Math.Max(0, Preset.ChoiceIndex)];
        return n == 0 ? $"{level}, as set." : $"{level}, {n} setting{(n == 1 ? "" : "s")} changed from it.";
    }

    private static int Changed(Knob driver)
    {
        if (driver.Members == null) return 0;
        int n = 0;
        foreach (Knob m in driver.Members) n += (m.StoredIsDefault ? 0 : 1) + Changed(m);
        return n;
    }

    /// <summary>Declare the group: the renderer touches it before any member can.</summary>
    internal static void Touch() { }
}
