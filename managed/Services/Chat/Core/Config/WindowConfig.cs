using System.Collections.Generic;

namespace CX.Services.Chat.Core;

/// <summary>Per-window persisted state: rect, render toggles, input side, send channel, look settings (colours,
/// animation, outline, timestamps), and an ordered tab list. Nullable fields fall back to the global
/// <see cref="ChatConfig"/> values for migration from configs written before per-window settings.</summary>
public sealed class WindowConfig
{
    public float X, Y, W, H;
    public uint Toggles = ChatToggle.Timestamp | ChatToggle.MessageBody | ChatToggle.Portrait;
    public bool InputOnTop;
    public int SendChannel;

    /// <summary>Per-window look settings. Null = inherit the global <see cref="ChatConfig"/> defaults.</summary>
    public readonly Dictionary<string, uint> Colors = new();
    public bool? AnimEnabled;
    public float? HoldSec, FadeSec, TauMs;
    public bool? Outline;
    public int? TimeFormat, TimeSource;
    public int? Align;
    public int? BgMode;
    public bool? AutoHideHeader;
    public bool? Locked;
    public bool? TellsHere;

    /// <summary>Persisted tabs: (label, channel mask as hex, pinned player name, kept). Empty = use the hardcoded defaults.</summary>
    public readonly List<(string Label, uint Mask, string Pin, bool Kept)> Tabs = new();
}
