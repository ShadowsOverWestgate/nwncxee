using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using static CX.Alloy.PartTable;
using static CX.Alloy.BucketGate;
using static CX.Alloy.AlloyCensus;
namespace CX.Alloy;

/// <summary>The engine notices Alloy needs, and nothing else (#264). Every hook here has a live consumer:
/// <list type="bullet">
/// <item><c>Part::~Part</c> -- the death notice (#145, #221). Every other liveness test is an inference.</item>
/// <item><c>VertexDataObject::Repack</c> -- the layout-change edge no scene epoch reports.</item>
/// <item><c>Gob::Append</c> / <c>Gob::Amputate</c> -- the equipment-swap pair, a cold #397 diagnostic.</item>
/// </list>
/// <para>The part draw hooks went with the engine's bucket walk (#264); the draw funnels
/// (<c>GLRender::RenderVertexData</c> / <c>RenderVertexStreamData</c>) belong to <see cref="RspShim"/>.</para>
/// <para>Armed by <c>drawhooks</c> in <c>cx.cfg</c> (the default arms this service, <c>stats</c> hands the
/// draw hooks to <c>DrawStatsService</c>, anything else disables both -- one mode, because both hook the
/// same symbols). Hot and alloc-free throughout.</para></summary>
public sealed unsafe class DrawFunnelService : IExtenderService
{
    /// <summary>#182: the ONE key that says who owns the draw hooks. <c>batch</c> (default) = this service
    /// -- the value keeps its name after #258 deleted the batcher, because it is a <c>cx.cfg</c> contract
    /// with existing installs (ADR 0013) and renaming it would silently disarm them. Reads as:
    /// <c>stats</c> = <c>DrawStatsService</c>'s histogram, anything else = neither. It is one mode rather than
    /// two booleans because the two arm the same symbols and cannot coexist; and it is config rather than a
    /// command because both decide what installs, which only happens once.</summary>
    internal const string DrawModeBatch = "batch", DrawModeStats = "stats";

    private const int LogEvery = 900;
    private static ExtenderLog? _log;

    private static bool _wasInModule;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // #170: ahead of every stand-down, as the host's unconditional call was. Priority pins it AFTER
        // DrawStats and BEFORE OwnedDraw -- alphabetical service discovery gives the wrong order on its own.
        FrameRegistry.Global.Register(FramePhase.PostPresent, 10, EndFrame, "DrawFunnels.endFrame", log);
        FrameTap.Install(log);   // CX-374 diagnostic; ahead of the stand-down so a vanilla session can snap the same way
        // #263: read off the knob rather than asked of AlloyGate, so this does not depend on service discovery order.
        if (!Renderer.Enabled.Boot)
        { log.Info("DrawFunnels: standing down (alloy.enabled=0) -- the engine renders this session"); return; }
        string mode = Renderer.DrawHooks.Boot;
        if (mode != DrawModeBatch)
        { log.Info($"DrawFunnels: standing down (cx.cfg alloy.drawhooks={mode})"); return; }

        PartTable.Log = log;
        AlloyCensus.Configure(log);
        // Above the symbol gate on purpose: the mark-and-sweep contract is pure managed logic over synthetic
        // parts, so it is checkable on a headless run -- which is the only place it is ever checked at all.
        SceneWalk.SelfCheck(log);
        if (!PartTable.Resolve())
        { log.Info("DrawFunnels: engine symbols unresolved -- Alloy unavailable (headless/Windows?)"); return; }
        // #256: enumeration is the SOURCE of every record now, so it resolves before the tables arm. Without
        // it there is no discovery at all -- nothing below may pretend otherwise by falling back to capture.
        if (!SceneWalk.Resolve(log)) return;

        SkyBox.Install(log, hooks);   // #261: resolves symbols only -- there is no sky hook any more
        // Chained, not Install()ed: Repack has several subscribers (#88).
        _repackSlot = hooks.ChainVdoRepack(&OnVdoRepack, RepackPriority);
        // #145: the death notice. Every other gate infers liveness (a sighting window, an identity compare
        // against fields that a freed block may still be holding); this one is told.
        _origPartDtor = hooks.InstallPartDtor(&OnPartDtor);
        // #397 diagnostic: the equipment-swap pair, cold, first GobWatchMax events per session.
        _origAppend = hooks.InstallGobAppend(&OnGobAppend);
        _origAmputate = hooks.InstallGobAmputate(&OnGobAmputate);
        // The record tables arm here, and the census and the gate ride on them.
        PartTable.Active = _repackSlot != null && _origPartDtor != null;
        log.Info(PartTable.Active
            ? "DrawFunnels: engine draw funnels armed (cx.cfg alloy.drawhooks=stats disables; [funnels] every 900 frames)"
            : _origPartDtor == null
              // #145: deliberate. Without the death notice, every liveness test left is an inference, and
              // the one the Alloy pass used walked a freed part into a freelist word.
              ? "DrawFunnels: Part::~Part unhooked -- Alloy stands down rather than track parts it "
                + "cannot be told the death of"
              : "DrawFunnels: hook install failed -- Alloy unavailable");
    }

    private static GobAppendFn _origAppend;
    private static GobAmputateFn _origAmputate;
    private static int _gobWatchLeft = GobWatchMax;
    private const int GobWatchMax = 200;

    /// <summary>#397: an item model appended to a gob. Cold (an equip), allocates the two strings for the
    /// line; the guard's doubled shield is two of these with no Amputate that found the first.</summary>
    [UnmanagedCallersOnly]
    private static int OnGobAppend(IntPtr gob, IntPtr model, IntPtr node)
    {
        int r = _origAppend != null ? _origAppend(gob, model, node) : 0;
        if (_gobWatchLeft > 0) { _gobWatchLeft--; _log?.Info($"[gobwatch] Append gob=0x{gob:x} model={Marshal.PtrToStringAnsi(model)} node={Marshal.PtrToStringAnsi(node)} -> {r} (roots now {new Gob(gob).AppendCount})"); }
        return r;
    }

    /// <summary>#397: the counterpart. A 0 return is a name the tree does not hold: nothing was removed.</summary>
    [UnmanagedCallersOnly]
    private static int OnGobAmputate(IntPtr gob, IntPtr name)
    {
        int before = new Gob(gob).AppendCount;
        int r = _origAmputate != null ? _origAmputate(gob, name) : 0;
        if (_gobWatchLeft > 0) { _gobWatchLeft--; _log?.Info($"[gobwatch] Amputate gob=0x{gob:x} name={Marshal.PtrToStringAnsi(name)} -> {r} (roots {before} -> {new Gob(gob).AppendCount})"); }
        return r;
    }

    /// <summary>Chain priority (#88): Repack is shared, and we sit under the shadow pipeline.</summary>
    private const int RepackPriority = 50;
    private static VdoRepackFn* _repackSlot;

    /// <summary>Repack observer: items of this VDO just moved -- force every member on it to re-read its
    /// draw arguments before it can batch again.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoRepack(IntPtr vdo)
    {
        VdoRepackFn next = *_repackSlot;
        if (next != null) next(vdo);
        OwnedBuffers.NoteRepack();   // #260: the arena argument's other rate -- see OwnedBuffers.NoteRepack

        int stale = 0;
        for (int i = 0; i < MaxParts; i++)
            if (_pState[i] == StActive && _pVdo[i] == vdo) { _pState[i] = StStale; SceneWalk.NoteChanged(i); stale++; }
        // #256: a repack changes no scene membership, so nothing else would ever look at these again --
        // the epoch will not move for a layout change. Ask for a walk, whose sweep re-queues them.
        if (stale > 0) SceneWalk.NoteStale();
    }

    private static PartDtorFn _origPartDtor;
    /// <summary>#145: parts destroyed out from under a recorded entry, this session. The [batch] line prints
    /// it, and a count that stays 0 across an area transition means the hook is not firing -- which looks
    /// exactly like "fixed" from the outside, so it is the number to read.</summary>
    internal static long DtorRevokes { get; private set; }

    /// <summary>#145: <c>Part::~Part</c>. The part table is keyed by POINTER and every liveness test on it is
    /// an inference -- a sighting window, or an identity compare against fields a freed block can still be
    /// holding. That is how the Alloy pass came to walk a freed part into <c>GobAlphaMul</c> and chase
    /// <c>*(gob+0x68)</c> into a freelist word: the enrolled entry never learned its part had died.
    /// <para>The handler only COMPARES the dying pointer -- it never dereferences it -- and does its work in
    /// O(1) through the same hash the walk uses, because an area transition destroys parts by the thousand.
    /// Allocation-free for the same reason.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnPartDtor(IntPtr part)
    {
        // #221: the freed mark goes on unconditionally -- including an entry some other gate already killed,
        // whose tombstone would otherwise outlive the allocation and block the address for good.
        int e = MarkFreed(part);
        if (e >= 0 && _pState[e] != StEmpty && _pState[e] != StDead)
        {
            OwnFlag[e] = 0;
            // ...and the gob with it: the dead-entry branch of TryOwnCandidateInner reads _pGob[e] and then
            // dereferences _pPart[e] to confirm it, which on a destroyed part is the same stale chase.
            _pGob[e] = IntPtr.Zero;
            Die(e, "destroyed");
            DtorRevokes++;
            // An in-place model rebuild (armour off) never hits Gob::SetScene, the only Scene::Sequence bump: re-walk for its new parts.
            SceneWalk.RequestWalk();
        }
        // ...and the OWNERSHIP slot with it: #308's persistent slots outlive the enrollment gap, so the
        // notice must reach them directly or the shadow bracket keeps chasing the freed part until the
        // next scan (the busy-area transition crash). Unconditional -- an enrolled tenant can outlive its
        // part-table entry's state.
        AlloyRenderer.NotePartDeath(part);
        _origPartDtor(part);
    }

    /// <summary>Frame tick from UiHostService. Periodic stats; the log is the only cold path.</summary>
    public static void EndFrame()
    {
        if (!PartTable.Active) return;
        // module-unload edge: every part/VDO dies with the module -- drop all recorded identity
        bool inModule = GameGate.InModule;
        if (_wasInModule && !inModule) WipeTables("module unloaded");
        // ...and entering one restarts the settle clock too: the table is empty either way, but an area load
        // is where the frames that matter to the census actually begin.
        if (!_wasInModule && inModule) _wipeFrame = _frame;
        _wasInModule = inModule;
        // Table pressure. Entries are never reclaimed individually -- Insert only ever probes for StEmpty --
        // so an area transition leaves its dead behind forever and the table eventually fills with debris.
        // Wiping is the reclamation, and it is right for exactly that case.
        //
        // What it is NOT right for is a table full of LIVE parts, which is what a large area is. Wiping
        // those reclaims nothing, drops every Alloy part back to the engine, and refills within the second
        // -- the 1Hz "the renderer keeps falling back to vanilla" collapse, which got worse the bigger the
        // area precisely because a bigger area reaches the cap sooner. So the diagnosis is MEASURED here
        // rather than asserted: one sweep of the state array, and only at pressure, so it costs nothing on
        // an ordinary frame.
        if (_partCount >= MaxParts - MaxParts / 4) OnTablePressure();
        if (++_frame % LogEvery != 0) return;
        _log?.Info("DrawFunnels: " + SceneWalk.Period());
        _log?.Info($"DrawFunnels: [funnels] {_partCount} parts tracked, " +
                   (_saturated ? $"TABLE SATURATED ({_satLive} live / {_satDead} dead, surplus NOT DRAWN), " : "") +
                   // #145: parts the engine destroyed under a recorded entry. A 0 that survives an area
                   // transition means Part::~Part is not reaching us, which is indistinguishable from
                   // "nothing died" in every other number here.
                   $" dtor-revoked={DtorRevokes} reclaimed={_reclaims}");
    }
}
