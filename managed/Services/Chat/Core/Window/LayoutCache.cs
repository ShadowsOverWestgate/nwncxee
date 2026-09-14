namespace CX.Services.Chat.Core;

/// <summary>Immutable snapshot of pass-1 wrap results (block heights + content height). Swapped atomically
/// behind <see cref="ChatWindow.WrapCache"/> so concurrent builds (ingest, input) never see a torn state.</summary>
internal sealed class LayoutCache
{
    public readonly float[] BlockH;
    public readonly float ContentH;
    public readonly int Count;
    public readonly float MaxTextW;
    public readonly uint TabMask;
    public readonly uint? TabPlayerId;
    public readonly string PinnedName;
    public readonly uint Toggles;
    public readonly int StoreVer, TimeFormat, TimeSource;
    public readonly object AtlasRef;

    public LayoutCache(float[] blockH, float contentH, int count, float maxTextW, uint tabMask,
                       uint? tabPlayerId, string pinnedName, uint toggles, int storeVer, int timeFormat,
                       int timeSource, object atlasRef)
    {
        BlockH = blockH; ContentH = contentH; Count = count; MaxTextW = maxTextW;
        TabMask = tabMask; TabPlayerId = tabPlayerId; PinnedName = pinnedName; Toggles = toggles;
        StoreVer = storeVer; TimeFormat = timeFormat; TimeSource = timeSource; AtlasRef = atlasRef;
    }

    public bool Hit(float maxTextW, uint tabMask, uint? tabPlayerId, string pinnedName, uint toggles,
                    int storeVer, int timeFormat, int timeSource, object atlasRef, int count)
        => Count == count && MaxTextW == maxTextW && TabMask == tabMask
           && TabPlayerId == tabPlayerId && PinnedName == pinnedName && Toggles == toggles
           && StoreVer == storeVer && TimeFormat == timeFormat && TimeSource == timeSource
           && AtlasRef == atlasRef;
}
