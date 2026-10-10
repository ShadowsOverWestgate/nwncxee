using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The enrolment-stable scenery predicate and the per-draw record's layout contract.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The enrollment-stable half of "this part is scenery", asked once per newcomer by the mesh
    /// charter's offer and by the acoustic sweep. 0 = static shape, else the refusing term, 1-based in
    /// <see cref="StWhyName"/>'s order. It admits tile geometry -- no gob, or a tile gob, which INCLUDES every
    /// Static placeable: the engine spawns their models through <c>Scene::SpawnTile</c>.</summary>
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

    /// <summary>Headless: the per-draw record's layout contract -- <c>CxDraw</c>, one 72-float, 288-byte std430
    /// block at binding 4 indexed by ownership slot, sound only while the GLSL declares that same layout --
    /// plus the enrolment predicate's decision table, a row per way geometry moves under a record that does not.</summary>
    internal static bool StaticSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.StaticSelfCheck");

        check.Expect(ExtFloat == PrevFloat + 16 && ExtFloat + ParamFloats - ParamExt == DrawFloats,
          "ADR 0026's ext0..ext2 do not sit between prev and the record's end");
        check.Expect(ShaderContract.RecordSafe(DrawBufDecl), "the GLSL CxDraw declaration does not add up to DrawFloats");
        check.Expect(DrawBufDecl.Contains($"binding = {BindDraw}"), $"CxDrawBuf is not declared at binding {BindDraw}");
        check.Expect(DrawBufDecl.Contains($"binding = {BindDrawIdx}"), $"CxDrawIdxBuf is not declared at binding {BindDrawIdx}");
        check.Expect(CullPass.SourceStrideSafe(),
          "the cull or sort stage strides the source list by something other than CullPass.SrcUints");

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
