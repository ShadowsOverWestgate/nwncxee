using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The <c>gpu</c> group: the GPU-driven static set. Alloy's visibility and submission are already
/// GPU-driven -- <see cref="CullPass"/> compute-culls every staged draw and submits each batch with one
/// <c>glMultiDrawElementsIndirectCount</c>, and the per-draw record block is persistent per ownership slot
/// (#242). What was still linear in object count is the CPU half of <see cref="AlloyRenderer.BuildBatches"/>:
/// every visible part is re-chased off the engine and its record re-compared every frame. This group freezes
/// that walk for the STATIC set, which is the bulk of an NWN area.
/// <para>Touched from <see cref="RenderCommands.Register"/> so the group declares at Initialize on the main
/// thread. Default OFF: the freeze is a claim about which engine terms can move behind Alloy's back, and the
/// claim has not been held against a live area yet -- see <c>AlloyRenderer.StaticSelfCheck</c> for what is
/// asserted headless and the ticket for what is not.</para></summary>
internal static unsafe class GpuStatic
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<int> Settle;

    static GpuStatic()
    {
        G = KnobGroup.Declare("gpu", "Graphics", "GPU-driven geometry", master: "Freeze static geometry",
            masterHelp: "Stop re-reading unchanging scenery from the game every frame. Tile walls, floors and "
                      + "props that cannot move are submitted from what CX already holds, which cuts the CPU "
                      + "cost of a crowded area. Moving objects, creatures and anything that fades are unaffected.",
            parent: "alloy.enabled");
        Enabled = G.Master!;
        G.Status = AlloyRenderer.StaticStatus;

        Settle = G.Slider("settle", "settle", 1, 64, 1, 4, tier: Tier.ChatOnly,
                          help: "Frames a slot must hold still before it is frozen.");
    }

    /// <summary>One frame in this many re-stages a frozen slot in full, so a term the membership test does
    /// not name cannot go stale for longer than this. Power of two -- the stagger is by slot index, so the
    /// per-frame cost is 1/N of the frozen population.</summary>
    internal const int Recheck = 16;

    private static string? _stood;

    /// <summary>The path's own capability latch, asked once from the first frame that has a context -- the
    /// entry points do not resolve until then. Per <c>docs/alloy-refusals.md</c> this is a per-class stand-down,
    /// not a frame verdict: the frozen set simply goes back on the per-frame walk, which is what this build
    /// does anyway with the knob off.</summary>
    internal static void Latch()
    {
        if (_stood != null || !Enabled.V) return;
        if (Gl.DispatchCompute != null && Gl.MemoryBarrier != null && Gl.BindBufferBase != null
            && Gl.MultiDrawElementsIndirectCount != null) { _stood = ""; return; }
        _stood = "a required GL entry point is missing (compute, SSBO binding or indirect-count submission)";
        AlloyRenderer.LogStandDown(_stood);
    }

    /// <summary>Is the freeze armed this frame? False under <c>/alloy poll 1</c>, whose contract is that every
    /// engine term is re-read every frame and the audit counters read 0.</summary>
    internal static bool On => Enabled.V && _stood == "" && !AlloyRenderer.FullPollOn;

    internal static string? StoodDown => _stood == "" ? null : _stood;
}

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Consecutive frames this slot has been staged as a member of the static set with a record that
    /// did not change. Derived state over the ownership table, with no world edge of its own: it is only
    /// believed beside <c>_recGen[i] == _recEpoch</c>, <c>_recEnroll[i] == _oEnrollGen[i]</c> and
    /// <c>_recFrame[i] == _frameSeq - 1</c>, so a revoke, a re-tenant, an epoch bump or a frame the slot sat
    /// out thaws it without this array being touched (<c>docs/owned-resource-lifetimes.md</c>: a table that
    /// needs no <c>Revoke</c> is one whose every read is already behind one that has one).</summary>
    private static readonly int[] _stSettle = new int[MaxOwned];

    /// <summary>Where the settle run stops counting: past <c>gpu.settle</c>'s ceiling the number says nothing
    /// more, and a slot still at frame one of an hour-long area must not wrap.</summary>
    private const int SettleCeiling = 4096;

    private static long _stFrozen, _stSettled, _stThawed;

    internal static bool FullPollOn => _fullPoll;

    internal static void LogStandDown(string why)
        => _log?.Info($"AlloyRenderer: gpu.static stood down -- {why}; the static set stays on the per-frame walk");

    /// <summary>The <c>gpu</c> group's settings-panel status line.</summary>
    internal static string StaticStatus()
        => GpuStatic.StoodDown is string s ? $"Stood down — {s}"
         : !GpuStatic.Enabled.V ? "Off — every part is re-read from the game each frame"
         : Counters._frames == 0 ? "On"
         : $"On — {(float)_stFrozen / Counters._frames:F0} of {(float)_stSettled / Counters._frames:F0} static parts frozen per frame";

    /// <summary>Is this slot in the STATIC set this frame? Every term is state Alloy already owns -- no new
    /// engine read, so no new <see cref="EngineLayout"/> row.
    /// <para>What it includes: tileset geometry, which is a part with no gob at all
    /// (<see cref="AlloyCandidate.Gob"/>: "zero for static geometry") and no matrix class, so
    /// <c>WriteStable</c> stamped it the identity and there is no transform to re-read -- and a Static
    /// placeable IS tile geometry here (its model is <c>Scene::SpawnTile</c>'d, #673). What it excludes:
    /// every instanced part (creatures, dynamic placeables, doors -- their colour folds and matrices are
    /// per-frame by construction), skinned and dangly meshes, a part whose VDO item committed vertices, a
    /// fading or dissolving part, a part below full alpha, a cycling texture, and the blended/water/additive
    /// classes, which are not on the compacting indirect path.</para></summary>
    private static bool StaticSlot(int i, int cls, bool dis, float dissolve, float alpha)
        => StaticShape(i, cls, dis, dissolve, alpha)
           && !VdoDirty(_oVao[i]) && !ItemMoved(_oVao[i], _oItem[i]);

    /// <summary>The membership test's pure half: everything answerable from the slot's own columns. Split out
    /// so <see cref="StaticSelfCheck"/> can exercise the decision table headless -- the two VDO terms above it
    /// read the engine's commit ledger, which has no meaning before a frame has run. Its enrollment-stable
    /// terms are <see cref="StaticAtEnroll"/>'s, the same function the charter's offer asks (#655: one predicate
    /// over two inputs); what follows is the per-frame refinement.</summary>
    private static bool StaticShape(int i, int cls, bool dis, float dissolve, float alpha)
    {
        // feat is not asked here: a displacing feature moves VERTICES in the vertex stage, never the record,
        // so the freeze is sound for it (224 restages/frame in the first live log when it was) -- the offer
        // side refuses it because a simplified or charted copy of a displaced mesh is thinned at rest pose.
        int why = StaticAtEnroll(cls, _oGob[i], _oTileGob[i], _oMx[i], _vSkin[_oVao[i]], _oDangly[i], 0);
        if (why != 0) why--;
        else if (_oBone[i] >= 0) why = 3;
        else if (_oFading[i] || dis || dissolve != 0f || alpha != 1f) why = 5;
        else if (_recCtl[i] != IntPtr.Zero || _recTexStale[i]) why = 6;
        else if (_cGen[i] != _recEpoch) why = 7;
        else return true;
        _stWhy[why]++;
        return false;
    }

    /// <summary>#655/#673: the enrollment-stable half of the static set, shared by <see cref="StaticShape"/>
    /// (per frame) and the enrollment offer (once per newcomer). 0 = static shape; else the refusing term,
    /// 1-based in <see cref="StWhyName"/>'s order. What it admits: tile geometry -- no gob, or a tile gob,
    /// which since #673's decompile of <c>CNWCArea::AddPlaceableObjectStatic</c> is known to INCLUDE every
    /// Static placeable: the engine spawns its model through <c>Scene::SpawnTile</c>, not through the
    /// object's anim base, so the gob is a tile gob and there is no placeable to detect
    /// (docs/research/static-placeable-rule.md, amendment). Never a skinned, dangly or displacing mesh,
    /// whose vertices move under a record that does not; the feature term is the offer side's alone (see
    /// <see cref="StaticShape"/> for why the freeze passes 0).</summary>
    internal static int StaticAtEnroll(int cls, IntPtr gob, bool tileGob, bool mx, bool skin, bool dangly, int feat)
    {
        if (cls > (int)AlloyClass.Cutout) return 1;
        if (!(gob == IntPtr.Zero || tileGob)) return 2;
        if (mx) return 3;
        if (skin || dangly) return 4;
        if (ShaderFeatures.OutOfPrepass(feat)) return 5;
        return 0;
    }

    /// <summary>#660: which <see cref="StaticShape"/> term refused a slot first this period, in the order the
    /// test asks them (class, gob, matrix, posed, feature, transient, texture, range). The instrument for a
    /// freeze that admits nothing.</summary>
    private static readonly long[] _stWhy = new long[8];
    private static readonly string[] StWhyName = { "class", "gob", "mx", "posed", "feat", "transient", "tex", "range" };

    private static string StaticWhy()
    {
        var sb = new System.Text.StringBuilder(" refused/frame:");
        for (int k = 0; k < _stWhy.Length; k++) sb.Append(' ').Append(StWhyName[k]).Append(' ').Append(((float)_stWhy[k] / Math.Max(1, Counters._frames)).ToString("F0"));
        return sb.ToString();
    }

    /// <summary>May this slot serve last frame's record whole? The settle counter says the record has not
    /// moved for <c>gpu.settle</c> frames of membership; the three stamps say the record belongs to THIS
    /// epoch, THIS tenant and the IMMEDIATELY previous frame (the same frame-continuity rule every other
    /// Alloy cache keeps); the stagger re-stages one frozen slot in <see cref="GpuStatic.Recheck"/> in full.</summary>
    private static bool StaticFrozen(int i, bool member)
        => member && _stSettle[i] >= GpuStatic.Settle.V
           && _recEnroll[i] == _oEnrollGen[i] && _recFrame[i] == _frameSeq - 1
           && ((i + _frameSeq) & (GpuStatic.Recheck - 1)) != 0;

    /// <summary>The <c>[alloy]</c> line's static clause: how much of the owned population left the per-frame
    /// walk, and how much of it came back. "thawed" is the instrument -- a steady nonzero means the membership
    /// test names a term that moves, so the set is churning rather than settling.</summary>
    private static string StaticNote()
        => GpuStatic.StoodDown is string s ? $"gpu.static stood down ({s})"
         : !GpuStatic.Enabled.V ? "gpu.static off"
         : $"gpu.static {(float)_stFrozen / Counters._frames:F0} frozen/frame of {(float)_stSettled / Counters._frames:F0} " +
           $"settled ({_stThawed} thawed this period), {(float)Counters._batches / Counters._frames:F0} indirect calls/frame" + StaticWhy();

    private static void ResetStaticPeriod() { _stFrozen = 0; _stSettled = 0; _stThawed = 0; Array.Clear(_stWhy); }

    /// <summary>Headless: the frozen path's record contract and the membership decision table. The record a
    /// frozen slot serves is the SAME <c>CxDraw</c> the walked path writes -- one 60-float, 240-byte std430
    /// block at binding 4, indexed by ownership slot -- so the freeze is only sound while that layout is the
    /// one the GLSL declares, and while the decision table refuses every class of part that can move.</summary>
    internal static bool StaticSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.StaticSelfCheck");

        // The instance record the static path submits from IS CxDraw: assert its size and its std430 declaration
        // rather than shipping a second layout that could drift from the first.
        check.Expect(DrawFloats == 60 && DrawFloats * sizeof(float) == 240, "the per-draw record is not the 240-byte CxDraw block");
        check.Expect(ShaderContract.RecordSafe(DrawBufDecl), "the GLSL CxDraw declaration does not add up to DrawFloats");
        check.Expect(DrawBufDecl.Contains($"binding = {BindDraw}"), $"CxDrawBuf is not declared at binding {BindDraw}");
        check.Expect(DrawBufDecl.Contains($"binding = {BindDrawIdx}"), $"CxDrawIdxBuf is not declared at binding {BindDrawIdx}");
        check.Expect((GpuStatic.Recheck & (GpuStatic.Recheck - 1)) == 0 && GpuStatic.Recheck > 1,
          "the recheck cadence is not a power of two greater than one (the stagger masks it)");
        check.Expect(CullPass.SrcUints == 6 && CullPass.CmdUints * sizeof(uint) == 20,
          "the cull's source record or indirect command stride moved under the static path");

        // The decision table, on a scratch slot restored afterwards. Each row is one term of StaticSlot, and
        // the row exists because the term names a way the part can move behind a frozen record.
        const int i = MaxOwned - 1;
        IntPtr gob = _oGob[i]; bool mx = _oMx[i], dangly = _oDangly[i], fading = _oFading[i], texStale = _recTexStale[i];
        int bone = _oBone[i], vao = _oVao[i], item = _oItem[i], cgen = _cGen[i];
        IntPtr ctl = _recCtl[i]; float feat = _oFeat[i * 4];
        _oGob[i] = IntPtr.Zero; _oMx[i] = false; _oDangly[i] = false; _oFading[i] = false; _recTexStale[i] = false;
        _oBone[i] = -1; _oVao[i] = 0; _oItem[i] = 0; _cGen[i] = _recEpoch; _recCtl[i] = IntPtr.Zero;
        _oFeat[i * 4] = 0f;
        bool baseline = StaticShape(i, (int)AlloyClass.Opaque, false, 0f, 1f);
        check.Expect(baseline, "a gobless, unposed, opaque, fully-opaque part is not in the static set");
        check.Expect(!StaticShape(i, (int)AlloyClass.Blended, false, 0f, 1f), "a blended part is in the static set");
        check.Expect(!StaticShape(i, (int)AlloyClass.Water, false, 0f, 1f), "a water part is in the static set");
        check.Expect(!StaticShape(i, (int)AlloyClass.Opaque, true, 0f, 1f), "a dissolving part is in the static set");
        check.Expect(!StaticShape(i, (int)AlloyClass.Opaque, false, 0.5f, 1f), "a keyholed part is in the static set");
        check.Expect(!StaticShape(i, (int)AlloyClass.Opaque, false, 0f, 0.5f), "a part below full alpha is in the static set");
        _oGob[i] = (IntPtr)1;
        check.Expect(!StaticShape(i, (int)AlloyClass.Opaque, false, 0f, 1f), "an instanced part is in the static set");
        _oGob[i] = IntPtr.Zero; _oMx[i] = true;
        check.Expect(!StaticShape(i, (int)AlloyClass.Opaque, false, 0f, 1f), "a matrix-class part is in the static set");
        _oMx[i] = false;
        check.Expect(StaticAtEnroll((int)AlloyClass.Opaque, IntPtr.Zero, false, false, false, false, (int)ShaderFeature.WindGrass) == 5,
          "a displacing part is offered to the charter");
        _oFeat[i * 4] = (int)ShaderFeature.WindGrass;
        check.Expect(StaticShape(i, (int)AlloyClass.Opaque, false, 0f, 1f), "a displacing part is refused the freeze (its record does not move)");
        _oFeat[i * 4] = 0f;
        _oBone[i] = 0;
        check.Expect(!StaticShape(i, (int)AlloyClass.Opaque, false, 0f, 1f), "a posed skinmesh is in the static set");
        _oBone[i] = -1; _oDangly[i] = true;
        check.Expect(!StaticShape(i, (int)AlloyClass.Opaque, false, 0f, 1f), "a dangly part is in the static set");
        _oDangly[i] = false; _oFading[i] = true;
        check.Expect(!StaticShape(i, (int)AlloyClass.Opaque, false, 0f, 1f), "a fading tile is in the static set");
        _oFading[i] = false; _recCtl[i] = (IntPtr)1;
        check.Expect(!StaticShape(i, (int)AlloyClass.Opaque, false, 0f, 1f), "a cycling texture is in the static set");
        _recCtl[i] = IntPtr.Zero; _recTexStale[i] = true;
        check.Expect(!StaticShape(i, (int)AlloyClass.Opaque, false, 0f, 1f), "a slot with a stale texture is in the static set");
        _recTexStale[i] = false; _cGen[i] = _recEpoch - 1;
        check.Expect(!StaticShape(i, (int)AlloyClass.Opaque, false, 0f, 1f), "a slot whose index range is unstamped is in the static set");
        _cGen[i] = _recEpoch;

        // The freeze itself refuses a slot that sat a frame out (the frame-continuity rule) or whose tenant changed.
        int settle = _stSettle[i], recFrame = _recFrame[i], recEnroll = _recEnroll[i], enrollGen = _oEnrollGen[i];
        _stSettle[i] = GpuStatic.Settle.V; _recEnroll[i] = _oEnrollGen[i] = 1;
        _recFrame[i] = _frameSeq - 2;
        check.Expect(!StaticFrozen(i, true), "a slot that sat a frame out is frozen");
        _recFrame[i] = _frameSeq - 1; _recEnroll[i] = 2;
        check.Expect(!StaticFrozen(i, true), "a re-tenanted slot is frozen");
        _recEnroll[i] = _oEnrollGen[i];
        _stSettle[i] = 0;
        check.Expect(!StaticFrozen(i, true), "an unsettled slot is frozen");
        _stSettle[i] = settle; _recFrame[i] = recFrame; _recEnroll[i] = recEnroll; _oEnrollGen[i] = enrollGen;

        _oGob[i] = gob; _oMx[i] = mx; _oDangly[i] = dangly; _oFading[i] = fading; _recTexStale[i] = texStale;
        _oBone[i] = bone; _oVao[i] = vao; _oItem[i] = item; _cGen[i] = cgen; _recCtl[i] = ctl;
        _oFeat[i * 4] = feat;
        log.Info(check.Ok ? "AlloyRenderer.StaticSelfCheck: passed" : "AlloyRenderer.StaticSelfCheck: FAILED");
        return check.Ok;
    }
}
