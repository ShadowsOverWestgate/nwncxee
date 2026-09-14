namespace CX.Ui;

/// <summary>What a settings row edits. The row carries the extras each needs (bit, colour key, slider range);
/// <see cref="SettingsPanel"/> only uses it as part of a row's identity key, and the consumer switches on it in
/// its row-press callback. Chat's per-window <c>SettingsWindow</c> is the one consumer left (#590 deleted the
/// dialog, nameplate and quickbar panels); the global half of the union went with them.</summary>
public enum SetAct
{
    ToggleBit, InputTop, AutoHideHdr, LockWin, TellsHere, AnimOn, SliderHold, SliderFade, SliderTau, SliderFont, OutlineOn,
    CycleFmt, CycleSrc, CycleAlign, CycleTellOpen, CycleBgMode, ColorSwatch, ColorChannel,
    TabExpand, TabAdd, TabRemove, TabLabel, TabPin, TabChannel,
    CopyFrom,
}
