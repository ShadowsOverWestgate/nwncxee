using System;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>CX-118: the bounds columns the hit test reads, kept current from the dirty list -- part-space
/// boxes from each part's <see cref="MeshDef"/>, and per object the world box and the world sphere.
/// <para>Change-driven, not per frame: <see cref="Refresh"/> is called for one object when its transform,
/// tree or visibility changed, and it re-files that object in <see cref="HitGrid"/>. <see cref="Consume"/>
/// does that over the objects table's dirty list, which is the hook the transform step (CX-120) owns.</para>
/// <para>The part boxes come from the model, which is immutable, so a part's own-space box is computed once
/// per part at tree build -- <see cref="PartBox"/> is called from <see cref="Refresh"/> only because the CX-114
/// tree builder has not landed; it costs a model lookup per dirty part, not per frame.</para></summary>
public static class HitBounds
{
    /// <summary>Objects refreshed, and parts whose box was derived -- the counters the self-check reads.</summary>
    public static long Refreshed, PartBoxes;

    /// <summary>Recompute object <paramref name="o"/>'s bounds from its parts and re-file it in the grid.</summary>
    public static void Refresh(int o)
    {
        Refreshed++;
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        int first = SceneDb.ObjPartFirst[o], count = SceneDb.ObjPartCount[o];
        for (int p = first; p < first + count; p++)
        {
            if (!SceneDb.PartEnabled[p] || SceneDb.PartMerged[p]) continue;
            Vector3 a = SceneDb.HitPartBMin[p], b = SceneDb.HitPartBMax[p];
            if (a.X > b.X) continue;
            Matrix4x4 w = SceneDb.PartWorld[p];
            for (int c = 0; c < 8; c++)
            {
                Vector3 v = Vector3.Transform(new Vector3((c & 1) == 0 ? a.X : b.X,
                                                          (c & 2) == 0 ? a.Y : b.Y,
                                                          (c & 4) == 0 ? a.Z : b.Z), w);
                lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v);
            }
        }
        // ...and the one object whose bound is not its parts': a text gob, whose box IS the quad its own
        // HitCheck tests. A GUI list row has no parts at all, so without this it never reached the grid.
        if (TextQuad(o, out Vector3 tlo, out Vector3 thi))
        {
            Matrix4x4 tw = SceneDb.ObjWorld[o];
            for (int c = 0; c < 4; c++)
            {
                Vector3 v = Vector3.Transform(new Vector3((c & 1) == 0 ? tlo.X : thi.X,
                                                          (c & 2) == 0 ? tlo.Y : thi.Y, 0f), tw);
                lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v);
            }
        }
        if (lo.X > hi.X)
        {
            SceneDb.HitObjBMin[o] = new Vector3(1f); SceneDb.HitObjBMax[o] = new Vector3(-1f);
            SceneDb.HitObjSphereR[o] = 0f;
            HitGrid.Remove(o);
            return;
        }
        SceneDb.HitObjBMin[o] = lo; SceneDb.HitObjBMax[o] = hi;
        // The sphere vanilla rejects against is the model's, placed and scaled; the box's own sphere is the
        // same answer without the model deref, and it can only ever be tighter.
        SceneDb.HitObjSphereC[o] = (lo + hi) * 0.5f;
        SceneDb.HitObjSphereR[o] = (hi - lo).Length() * 0.5f;
        HitGrid.File(o);
    }

    /// <summary>The quad <c>StringGob::HitCheck</c> @0x492920 tests, in the gob's own frame, or false for an
    /// object that is not a text gob with a box set. The engine builds it from three fields:
    /// <c>x</c> from <c>-(+0x54c)</c> to <c>(+0x54c) + MaxWidth</c>, <c>y</c> from <c>-MaxHeight</c> to 0, flat
    /// on <c>z</c>; <c>+0x54c</c> is written 0 by the constructor and by nothing else on either build, so the
    /// quad is <c>[0,MaxWidth] x [-MaxHeight,0]</c>. Note that it is the box the game SET
    /// (<c>SetMaxWidth</c>/<c>SetMaxHeight</c>, or the corner pair <c>AttachCornersToObjects</c> hands over),
    /// not the measured text -- so an unmeasured document still has its row.
    /// <para>The engine's own two "no box" sentinels are refused here rather than filed: a width of 0 or a
    /// height at or below <c>-5e-06</c> builds a quad with no area, which <c>polyhit</c> cannot land in.</para>
    /// <para>This is the whole reason a GUI list row was unclickable: rows are part-less text gobs
    /// (<c>kind=Text parts=0</c>) attached to the list control, the bounds came only from parts, so no row
    /// ever entered <see cref="HitGrid"/> and every click fell through to the list frame behind them.</para></summary>
    internal static bool TextQuad(int o, out Vector3 lo, out Vector3 hi)
    {
        lo = default; hi = default;
        if (SceneDb.ObjKindOf[o] != ObjKind.Text) return false;
        TextDoc? d = TextObjects.Of(SceneDb.Objects.IdAt(o));
        if (d == null || !(d.MaxWidth > 0f) || d.MaxHeight <= -5e-06f) return false;
        lo = new Vector3(0f, -d.MaxHeight, 0f);
        hi = new Vector3(d.MaxWidth, 0f, 0f);
        return true;
    }

    /// <summary>Derive part <paramref name="p"/>'s own-space box from its node: the mesh's model-space bounds
    /// for geometry, degenerate for a dummy, light, emitter or reference node (which the engine's
    /// <c>Part::HitCheckGeom</c> answers 0 for).</summary>
    public static void PartBox(int p)
    {
        PartBoxes++;
        SceneDb.HitPartBMin[p] = new Vector3(1f);
        SceneDb.HitPartBMax[p] = new Vector3(-1f);
        ModelDef? m = SceneDb.PartModel[p];
        if (m == null || (uint)SceneDb.PartNode[p] >= (uint)m.Nodes.Length) return;
        NodeDef nd = m.Nodes[SceneDb.PartNode[p]];
        if (nd.Mesh == null) return;
        if (nd.Kind is not (NodeKind.TriMesh or NodeKind.Skin or NodeKind.AnimMesh or NodeKind.Dangly
                            or NodeKind.Aabb or NodeKind.Trigger)) return;
        Vector3 lo = nd.Mesh.BMin, hi = nd.Mesh.BMax;
        if (lo.X > hi.X)
        {
            lo = new Vector3(float.MaxValue); hi = new Vector3(float.MinValue);
            foreach (Vector3 v in nd.Mesh.Positions) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
            if (lo.X > hi.X) return;
        }
        SceneDb.HitPartBMin[p] = lo;
        SceneDb.HitPartBMax[p] = hi;
    }

    /// <summary>The per-frame fold lives in <see cref="HitFeed.Fold"/>, which also re-seeds a changed tree's
    /// part boxes and says so when nothing ever produced the columns.</summary>
    public static void Consume() => HitFeed.Fold();
}
