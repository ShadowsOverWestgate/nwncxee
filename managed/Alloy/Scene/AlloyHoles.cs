using System;
using CX.Engine;
using CX.Framework;
using static CX.Alloy.PartTable;

namespace CX.Alloy;

/// <summary>CX-92: <c>/alloy holes</c> -- the per-instance hole dump. For every gob in the scene that has at
/// least one part Alloy is NOT drawing, one line per part of that gob: the node name, the ancestor chain of
/// <c>part+0x34</c> visible bytes, the record state, the ownership flag and slot, and the per-slot gates the
/// route walk would refuse it on. Since #264 an unowned part is drawn by nobody, so "a creature is short one
/// limb" is always one of these lines -- and which COLUMN differs between the limb that drew and the one
/// that did not is the diagnosis.
/// <para>A button, not a knob: it reads the tables once, allocates its own strings and writes them to
/// <c>cx.log</c>. Nothing on the frame path calls it.</para></summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Gobs reported per invocation, and parts reported per gob. A bound rather than a budget: an
    /// area is thousands of gobs and the answer is in the first few creatures in front of the camera.</summary>
    private const int HoleGobs = 32, HolePartsPerGob = 16;

    private static readonly IntPtr[] _holeGob = new IntPtr[HoleGobs];

    /// <summary>The state column, spelled. <see cref="StHidden"/> is the one a reader must not confuse with a
    /// death: the engine is not drawing that subtree either.</summary>
    private static string HoleState(int e) => _pState[e] switch
    {
        StEmpty => "empty",
        StActive => "active",
        StDead => "dead:" + (_pDeath[e] ?? "?"),
        StStale => "stale",
        StUnbound => "unbound" + (SceneWalk.RecordHasNoGeometry(e) ? "(no-geometry)" : ""),
        StHidden => "hidden(part+0x34)",
        _ => "?",
    };

    /// <summary>This entry's live gob, read off the part rather than off <c>_pGob</c>: a held or dead entry's
    /// column is deliberately cleared, and the grouping wants the creature anyway. Zero when the entry holds
    /// no readable part.</summary>
    private static IntPtr HoleGobOf(int e)
    {
        IntPtr part = _pPart[e];
        if (part == IntPtr.Zero || _pFreed[e] || _pState[e] == StEmpty || _pState[e] == StDead) return _pGob[e];
        return new PartRef(part).Gob.Ptr;
    }

    /// <summary>The per-slot gates a routed part can still be refused on after enrolment took it -- the
    /// columns <c>RouteLive</c> and the staging walk read. Empty when the slot would route.</summary>
    private static string HoleSlotGates(int i)
    {
        string s = "";
        if (_oTri[i] == IntPtr.Zero) return "slot vacated";
        if (!_oPlaced[i]) s += " unplaced";
        if (_oLodRoot[i] != IntPtr.Zero && _oGob[i] != IntPtr.Zero
            && new Gob(_oGob[i]).PartTree.Ptr != _oLodRoot[i]) s += " inactive-lod";
        if (_recGen[i] != _recEpoch) s += " record-stale";
        if (_recTexStale[i]) s += " tex-stale";
        if (_oFading[i]) s += " fading";
        if (_oBone[i] >= 0) s += " posed";
        return s.Length == 0 ? "" : s.Trim();
    }

    /// <summary>Walk the part table, gather the gobs with holes, then print each one whole. Cold.</summary>
    internal static void LogHoles()
    {
        var log = _log ?? KnobGroup.Log;
        if (log == null) return;
        int gen = SceneWalk.Gen, gobs = 0, holes = 0, scanned = 0;
        for (int e = 0; e < MaxParts && gobs < HoleGobs; e++)
        {
            if (_pWalkGen[e] != gen || _pState[e] == StEmpty) continue;
            scanned++;
            if (AlloyCensus.OwnFlag[e] != 0) continue;
            IntPtr gob = HoleGobOf(e);
            if (gob == IntPtr.Zero) continue;   // tile geometry: a hole there is not the creature question
            holes++;
            bool seen = false;
            for (int k = 0; k < gobs && !seen; k++) seen = _holeGob[k] == gob;
            if (!seen) _holeGob[gobs++] = gob;
        }
        log.Info($"[holes] {holes} unowned instance parts over {scanned} records in the scene; "
                 + $"{gobs} gob(s) reported (cap {HoleGobs})");
        for (int k = 0; k < gobs; k++)
        {
            IntPtr gob = _holeGob[k];
            var gv = new Gob(gob);
            (int slots, int tile) on = SlotsOnGob(gob);
            log.Info($"[holes] gob=0x{gob:x} visible={gv.Visible} alpha={gv.Alpha:F2} fade={gv.FadeAlpha:F2} "
                     + $"tile={(!gv.TileData.IsNull ? 1 : 0)} owned-slots={on.slots} tile-slots={on.tile}");
            int printed = 0;
            for (int e = 0; e < MaxParts && printed < HolePartsPerGob; e++)
            {
                if (_pWalkGen[e] != gen || _pState[e] == StEmpty || HoleGobOf(e) != gob) continue;
                printed++;
                int i = (uint)e < (uint)_entrySlot.Length ? _entrySlot[e] : -1;
                string slot = i < 0 ? "no slot"
                    : $"slot={i} cls={(AlloyClass)_oCls[i]} gates=[{HoleSlotGates(i)}]";
                log.Info($"[holes]   entry={e} state={HoleState(e)} own={AlloyCensus.OwnFlag[e]} {slot} "
                         + AlloyCensus.PartForensic(e));
            }
        }
    }
}
