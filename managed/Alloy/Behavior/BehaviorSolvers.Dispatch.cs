using System;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class BehaviorSolvers
{
    /// <summary>How many concrete <c>Control</c> implementations there are -- the width of every per-class
    /// table here.</summary>
    internal const int Kinds = 13;

    /// <summary>The Linux symbol per class, index-aligned with <see cref="BehaviorKind"/>.</summary>
    internal static readonly string[] Symbols =
    {
        SymbolMap.BehaviorControl, SymbolMap.AttachControl, SymbolMap.AttachTranslateControl,
        SymbolMap.AttachRotateControl, SymbolMap.AttachNoOrientationControl, SymbolMap.AttachGuiScaleControl,
        SymbolMap.CrossSceneAttachControl, SymbolMap.CrossSceneAttachOffsetControl, SymbolMap.SubwindowControl,
        SymbolMap.NavigateControl, SymbolMap.CameraFollowControl, SymbolMap.CameraLooseFollowControl,
        SymbolMap.CameraOnAStickControl,
    };

    /// <summary>Short names for the period line and the <c>/knobs</c> note, same order.</summary>
    internal static readonly string[] Names =
    {
        "base", "attach", "translate", "rotate", "noorient", "guiscale", "crossscene", "crossoffset",
        "subwindow", "navigate", "follow", "loosefollow", "onastick",
    };

}
