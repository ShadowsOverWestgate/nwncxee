using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>What is left of the <c>gpu</c> group: one shared predicate and the per-draw record's own
/// contract.
/// <para>CX-188 deleted the STATIC FREEZE and its <c>gpu.static</c> knob. The freeze was a second,
/// walk-side answer to "this slot cannot have changed", built when the walk re-chased every visible part off
/// the engine. The scene database made the walk change-driven instead -- a record is rewritten by the dirty
/// fold that owns its inputs (<c>DbFeed.TakeFrame</c>) -- and the freeze's own entry points
/// (<c>StaticShape</c>, <c>StaticFrozen</c>) had lost every production caller: its three counters never
/// incremented, so the knob was on, declared a saving and made none, and the settings panel reported
/// "0 of 0 static parts frozen per frame". ADR 0025 says a knob is one declaration; a declaration whose
/// mechanism is gone is not one. An existing <c>gpu.static</c> line in a user's <c>cx.cfg</c> is preserved
/// verbatim by the knob file and ignored.</para></summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>#655/#673: the enrollment-stable half of "this part is scenery". Asked once per newcomer by
    /// the mesh charter's offer and by the acoustic sweep. 0 = static shape; else the refusing term, 1-based
    /// in <see cref="StWhyName"/>'s order. What it admits: tile geometry -- no gob, or a tile gob, which since
    /// #673's decompile of <c>CNWCArea::AddPlaceableObjectStatic</c> is known to INCLUDE every Static
    /// placeable: the engine spawns its model through <c>Scene::SpawnTile</c>, not through the object's anim
    /// base, so the gob is a tile gob and there is no placeable to detect (CX-170, amendment). Never a
    /// skinned, dangly or displacing mesh, whose vertices move under a record that does not.</summary>
    internal static int StaticAtEnroll(int cls, IntPtr gob, bool tileGob, bool mx, bool skin, bool dangly, int feat)
    {
        if (cls > (int)AlloyClass.Cutout) return 1;
        if (!(gob == IntPtr.Zero || tileGob)) return 2;
        if (mx) return 3;
        if (skin || dangly) return 4;
        if (ShaderFeatures.OutOfPrepass(feat)) return 5;
        return 0;
    }

    /// <summary>Which <see cref="StaticAtEnroll"/> term refused a part, in the order the test asks them.</summary>
    private static readonly string[] StWhyName = { "class", "gob", "mx", "skin/dangly", "feat" };

    /// <summary>Headless: the per-draw record's layout contract. The record every path submits from IS
    /// <c>CxDraw</c> -- one 72-float, 288-byte std430 block at binding 4, indexed by ownership slot -- so
    /// every submission is only sound while that layout is the one the GLSL declares. Paired with the
    /// enrolment predicate's own decision table, each row of which names a way a part's geometry can move
    /// under a record that does not.</summary>
    internal static bool StaticSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.StaticSelfCheck");

        check.Expect(DrawFloats == 72 && DrawFloats * sizeof(float) == 288, "the per-draw record is not the 288-byte CxDraw block");
        check.Expect(ExtFloat == PrevFloat + 16 && ExtFloat + ParamFloats - ParamExt == DrawFloats,
          "ADR 0026's ext0..ext2 do not sit between prev and the record's end");
        check.Expect(ShaderContract.RecordSafe(DrawBufDecl), "the GLSL CxDraw declaration does not add up to DrawFloats");
        check.Expect(DrawBufDecl.Contains($"binding = {BindDraw}"), $"CxDrawBuf is not declared at binding {BindDraw}");
        check.Expect(DrawBufDecl.Contains($"binding = {BindDrawIdx}"), $"CxDrawIdxBuf is not declared at binding {BindDrawIdx}");
        check.Expect(CullPass.SrcUints == 6 && CullPass.CmdUints * sizeof(uint) == 20,
          "the cull's source record or indirect command stride moved under the submission path");

        check.Expect(StaticAtEnroll((int)AlloyClass.Opaque, IntPtr.Zero, false, false, false, false, 0) == 0,
          "a gobless, unposed, opaque part is not scenery");
        check.Expect(StaticAtEnroll((int)AlloyClass.Blended, IntPtr.Zero, false, false, false, false, 0) == 1,
          "a blended part is scenery");
        check.Expect(StaticAtEnroll((int)AlloyClass.Opaque, (IntPtr)1, false, false, false, false, 0) == 2,
          "an instanced part is scenery");
        check.Expect(StaticAtEnroll((int)AlloyClass.Opaque, (IntPtr)1, true, false, false, false, 0) == 0,
          "a TILE gob's part is not scenery (#673: every Static placeable is one)");
        check.Expect(StaticAtEnroll((int)AlloyClass.Opaque, IntPtr.Zero, false, true, false, false, 0) == 3,
          "a matrix-class part is scenery");
        check.Expect(StaticAtEnroll((int)AlloyClass.Opaque, IntPtr.Zero, false, false, true, false, 0) == 4
                  && StaticAtEnroll((int)AlloyClass.Opaque, IntPtr.Zero, false, false, false, true, 0) == 4,
          "a skinned or dangly part is scenery");
        check.Expect(StaticAtEnroll((int)AlloyClass.Opaque, IntPtr.Zero, false, false, false, false, (int)ShaderFeature.WindGrass) == 5,
          "a displacing part is offered to the charter");
        check.Expect(StWhyName.Length == 5, "the refusal names do not cover StaticAtEnroll's terms");

        log.Info(check.Ok ? "AlloyRenderer.StaticSelfCheck: passed" : "AlloyRenderer.StaticSelfCheck: FAILED");
        return check.Ok;
    }
}
