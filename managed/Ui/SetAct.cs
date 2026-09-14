namespace CX.Ui;

/// <summary>What a settings row edits. The row carries the extras each needs (bit, colour key, slider range);
/// <see cref="SettingsPanel"/> only uses it as part of a row's identity key, and the consumer switches on it in
/// its row-press callback. ponytail: one union of every consumer's actions, because the three services already
/// shared this enum verbatim; splitting it per consumer is the ceiling once #167/#168 land.</summary>
public enum SetAct
{
    ToggleBit, InputTop, AutoHideHdr, LockWin, TellsHere, AnimOn, SliderHold, SliderFade, SliderTau, SliderFont, OutlineOn,
    CycleFmt, CycleSrc, CycleAlign, CycleTellOpen, CycleBgMode, ColorSwatch, ColorChannel,
    TabExpand, TabAdd, TabRemove, TabLabel, TabPin, TabChannel,
    CopyFrom,
    CycleGridSize,
    MaxHeight, JustifyText, FadeIn, FadeOut,
}
