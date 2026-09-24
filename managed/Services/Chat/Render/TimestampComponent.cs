using System.Collections.Generic;
using System.Text;
using CX.Services.Chat.Core;

namespace CX.Services.Chat.Render;

/// <summary>Prepend a grey timestamp to the line, in the configured <see cref="TsFormat"/> and from the
/// configured <see cref="TsSource"/> (game time falls back to local when the line carries none). The style
/// is mirrored onto statics from the (cold) config so this stays a cheap, allocation-light hot component.</summary>
public sealed class TimestampComponent : ILineComponent
{
    private const uint Grey = 0x8A8A8A;  // not-an-offset: a colour

    public uint Bit => ChatToggle.Timestamp;

    public void Contribute(ChatWindow win, ChatLine line, List<Rune> runes, List<uint> cols)
    {
        string txt;
        TsSource src = win.TimeSource;
        TsFormat fmt = win.TimeFormat;
        if (src == TsSource.Game && line.GameTime.Valid)
            txt = Stamp(line.GameTime.Hour, line.GameTime.Minute, line.GameTime.Second, fmt);
        else { System.DateTime lt = line.Timestamp.ToLocalTime(); txt = Stamp(lt.Hour, lt.Minute, lt.Second, fmt); }
        foreach (Rune r in txt.EnumerateRunes()) { runes.Add(r); cols.Add(Grey); }
    }

    /// <summary>Format one h/m/s (24-hour inputs) into the current preset, e.g. <c>[09:07] </c>.</summary>
    internal static string Stamp(int h, int m, int s, TsFormat fmt) => fmt switch
    {
        TsFormat.HourMinSec => $"[{h:D2}:{m:D2}:{s:D2}] ",
        TsFormat.Hour12 => $"[{(h % 12 == 0 ? 12 : h % 12)}:{m:D2} {(h < 12 ? "AM" : "PM")}] ",
        _ => $"[{h:D2}:{m:D2}] ",
    };
}
