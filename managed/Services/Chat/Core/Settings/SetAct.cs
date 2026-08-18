namespace Sowx.Services.Chat.Core;

/// <summary>What a settings row edits. The row carries the extras each needs (bit, colour key, slider range),
/// and <see cref="SettingsWindow.MouseDown"/> switches on it to mutate the window/config.</summary>
public enum SetAct
{
    ToggleBit, InputTop, AutoHideHdr, LockWin, TellsHere, AnimOn, SliderHold, SliderFade, SliderTau, SliderFont, OutlineOn,
    CycleFmt, CycleSrc, CycleAlign, CycleTellOpen, CycleBgMode, ColorSwatch, ColorChannel,
    TabExpand, TabAdd, TabRemove, TabLabel, TabPin, TabChannel,
    CopyFrom,
    CycleGridSize,
}
