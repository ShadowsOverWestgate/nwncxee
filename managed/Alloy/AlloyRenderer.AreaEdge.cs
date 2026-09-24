using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-19 Fault 3: THE area edge, and there is one list for it. An area change comes in two shapes.
/// Path A the Scene pointer changes and <c>SceneWalk.Reset</c> fires; Path B a server hop or an in-module
/// transition reuses the Scene object (EE .37 keeps the tileset in memory), and nothing fired at all -- so
/// every table keyed on an identity the engine recycles (VDO slots and their chart/LOD arenas, the draw-arg
/// ranges, the #284 texture-rebuild verdicts) survived and bound the next area's parts to the last area's
/// data. <c>CNWCMessage::SendPlayerToServerArea_AreaLoaded</c> @0x7fa0c0 is the edge the engine does give us.
/// <para>See docs/research/alloy-visibility-structural.md and docs/owned-resource-lifetimes.md.</para></summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Outermost on the send: the lightmap hold chains at 0 and may swallow the message, and this
    /// handler must have raised its flag before that happens.</summary>
    private const int AreaLoadedPriority = 50;
    private static SendAreaLoadedFn* _areaLoadedSlot;

    /// <summary>Raised on the game thread inside message handling, consumed at the render-thread frame
    /// anchor: the revoke walks tables the draw side reads and tears down pass state, and the middle of a
    /// packet handler is not a frame boundary. <c>LightmapBake._edgePending</c>'s pattern.</summary>
    private static volatile bool _areaEdgePending;

    [UnmanagedCallersOnly]
    private static void OnSendAreaLoaded(IntPtr msg)
    {
        _areaEdgePending = true;
        if (_areaLoadedSlot != null) (*_areaLoadedSlot)(msg);
    }

    /// <summary>Consumed at the frame anchor, ahead of the world walk and the enrolment that refill
    /// everything <see cref="AreaEdge"/> drops.</summary>
    private static void TakeAreaEdge()
    {
        if (!_areaEdgePending) return;
        _areaEdgePending = false;
        AreaEdge("AreaLoaded send -- server hop or in-module transition");
    }

    /// <summary>The one revoke both shapes of an area change run. Cold -- twice a transition at worst -- so
    /// the tallies it gathers and the line it writes may allocate.
    /// <para>Content-keyed tables are deliberately NOT here: <c>OwnedTextures._res</c> keys on the resref
    /// hash, the bake blob cache on the bake key, and <c>OwnedBuffers._byToken/_byName</c> ride the engine's
    /// own <c>DeleteBuffer</c> funnel. They describe what they name whichever area is up.</para></summary>
    internal static void AreaEdge(string why)
    {
        int vaos = 0;
        for (int v = 0; v < _vdoCount; v++) if (_vVdo[v] != IntPtr.Zero) vaos++;
        int owned = _ownLive, beams = Beams.VdoSlots, parts = PartTable._partCount;
        int charts = MeshChart.TableCount, lods = MeshLod.TableCount;
        // ORDER: the hold IS this edge. LightmapBake.Revoke runs inside RevokeAll, and on its own it would
        // forward the very send the hold is sitting on (DropHold) and clear the edge marks the static set
        // closes against -- so the drop stands down across the revoke and the edge is re-armed after it,
        // leaving the baseline bake to start from the new area's inputs rather than the old area's.
        LightmapBake.BeginAreaEdge();
        RevokeAll(why);                    // ownership slots, VDO slots, and every pass's captured world
        PartTable.WipeTables(why);         // part records, and the per-record SetupArrays / pose latches
        int ledger = OwnedTextures.AreaEdge();
        InvalidateRecords();               // ...and the per-slot draw-arg range cache (_cIdx/_cOff/_cBase)
        LightmapBake.EndAreaEdge();
        _log?.Info($"AlloyRenderer: [areaedge] {why} -- dropped {parts} part records, {owned} owned slots, "
                 + $"{vaos} VDO slots, {beams} beam VDO slots, {charts} mesh charts, {lods} mesh LODs, "
                 + $"{ledger} texture-rebuild verdicts; draw-arg ranges and every persistent record unstamped. "
                 + "Kept (content-keyed): the texture cache, the bake blob cache, the pool tables");
    }
}
