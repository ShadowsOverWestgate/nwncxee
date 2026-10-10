using System;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>
/// What a parsed <c>.mtr</c> tells a draw record: its content-shader feature (by <c>customShaderVS</c>), the stock
/// PBR parameters, ADR 0026's <c>Cx</c> vocabulary and the feature's own parameters. Engine-free, so the recipe
/// reads the same answer on any thread.
/// </summary>
internal static class MtrParams
{
    /// <summary>The ceiling on <c>CxEmissiveStrength</c>: well into bloom territory, short of the RGBA16F
    /// target's 65504 once an 8-bit illumination texel of 1.0 is multiplied through.</summary>
    internal const float MaxEmissiveStrength = 100f;

    /// <summary>The <see cref="ShaderFeature"/> a <c>customShaderVS</c> resref names, 0 for none or one Alloy does
    /// not reproduce. Whole-name and case-insensitive, so <c>shd_roofkn_vs</c> cannot match <c>shd_roofknm_vs</c>.</summary>
    internal static int Feature(string vs)
    {
        if (vs.Length == 0) return 0;
        string[] names = ShaderFeatures.VsNames;
        for (int f = 1; f < names.Length; f++)
            if (names[f].Length > 0 && vs.Equals(names[f], StringComparison.OrdinalIgnoreCase)) return f;
        return 0;
    }

    /// <summary>The extension floats before any material is read: -1 = "not declared" for every term a 0 is
    /// legal for (a dielectric, a mirror); the layer weights and the sheen colour are 0 = absent.</summary>
    internal static void ResetExt(Span<float> ext)
    {
        ext.Clear();
        ext[0] = ext[1] = ext[3] = -1f;   // CxMetallic, CxRoughness, CxClearcoatRoughness
        ext[7] = ext[8] = -1f;            // CxSheenRoughness, CxEmissiveStrength
    }

    /// <summary>Fold <paramref name="mtr"/>'s parameters, in file order (a later line wins), into the record's
    /// <paramref name="param"/> floats (stock pair, then <see cref="AlloyRenderer.ParamExt"/>'s extension) and
    /// the feature vec4 <paramref name="feat"/> (x = the feature, yzw its parameters). A name nobody reads is
    /// left alone; an int-typed parameter counts only where the engine's shader reads it as one.</summary>
    internal static void Fold(MtrDef? mtr, Span<float> param, Span<float> feat)
    {
        int f = (int)feat[0];
        if (mtr != null)
            foreach ((string name, float[] v, bool isInt) in mtr.Parameters)
            {
                if (v.Length == 0) continue;
                if (isInt)
                {
                    if ((f == (int)ShaderFeature.RoofKn || f == (int)ShaderFeature.RoofKnM) && Is(name, "iRoofInterior"))
                        feat[3] = v[0];
                    if (Is(name, "CxOrm")) param[AlloyRenderer.ParamExt + 9] = v[0] != 0f ? 1f : 0f;
                    continue;
                }
                if (FeatureParam(f, name, v[0], feat)) continue;
                if (Ext(name, v, param[AlloyRenderer.ParamExt..])) continue;
                if (Is(name, "Specularity")) param[0] = v[0];
                else if (Is(name, "Roughness")) param[1] = v[0];
                else if (Is(name, "Metallicness")) param[2] = v[0];
                else if (Is(name, "CustomSpecularColor") && v.Length >= 3)
                {
                    param[4] = v[0]; param[5] = v[1]; param[6] = v[2];
                    // black IS the engine's "no custom colour" sentinel (inc_material.shd compares against it)
                    param[7] = v[0] != 0f || v[1] != 0f || v[2] != 0f ? 1f : 0f;
                }
            }
        // fSwaySpeed divides, and 1.0 is what every wind .mtr states: an undeclared one is the authored case.
        if (f >= (int)ShaderFeature.WindGrass && f <= (int)ShaderFeature.WindPlant && feat[1] == 0f) feat[1] = 1f;
    }

    /// <summary>The parameters the community content shaders declare, by feature.</summary>
    private static bool FeatureParam(int f, string name, float v, Span<float> feat)
    {
        switch (f)
        {
            case (int)ShaderFeature.WindGrass:
            case (int)ShaderFeature.WindLeaf:
            case (int)ShaderFeature.WindPlant:
                if (Is(name, "fSwaySpeed")) { feat[1] = v; return true; }
                if (Is(name, "fSwayStrength")) { feat[2] = v; return true; }
                if (Is(name, "fTilesetMeshOffsetZ")) { feat[3] = v; return true; }
                return false;
            case (int)ShaderFeature.RoofKn:
            case (int)ShaderFeature.RoofKnM:
                if (Is(name, "fRoofOffsetZ")) { feat[1] = v; return true; }
                if (Is(name, "fRoofCameraClipZ")) { feat[2] = v; return true; }
                return false;
            case (int)ShaderFeature.Flowmap:
                if (Is(name, "fSlideSpeed")) { feat[1] = v; return true; }
                return false;
        }
        return false;
    }

    /// <summary>ADR 0026's vocabulary into the three extension vec4s: (CxMetallic, CxRoughness, CxClearcoat,
    /// CxClearcoatRoughness), (CxSheenColor.rgb, CxSheenRoughness), (CxEmissiveStrength, CxOrm, _, _). Clamped
    /// here, so a typo'd 50 reads as the maximum rather than an HDR blow-out.</summary>
    private static bool Ext(string name, float[] p, Span<float> x)
    {
        float v = Math.Clamp(p[0], 0f, 1f);
        if (Is(name, "CxMetallic")) x[0] = v;
        else if (Is(name, "CxRoughness")) x[1] = v;
        else if (Is(name, "CxClearcoat")) x[2] = v;
        else if (Is(name, "CxClearcoatRoughness")) x[3] = v;
        else if (Is(name, "CxSheenColor") && p.Length >= 3)
        { x[4] = v; x[5] = Math.Clamp(p[1], 0f, 1f); x[6] = Math.Clamp(p[2], 0f, 1f); }
        else if (Is(name, "CxSheenRoughness")) x[7] = v;
        else if (Is(name, "CxEmissiveStrength")) x[8] = Math.Clamp(p[0], 0f, MaxEmissiveStrength);
        else if (Is(name, "CxOrm")) x[9] = p[0] > 0.5f ? 1f : 0f;
        else return false;
        return true;
    }

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A hand-written <c>.mtr</c> through <see cref="MtrDef.Parse"/>, <see cref="Fold"/> and the recipe's
    /// slot mapping. Oracle: the literals in the text, and the slot contract in <c>docs/spec/materials.md</c>
    /// (<c>texture11</c> is the AO/ORM map on its own mask bit, never <c>texture7</c>).</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MtrParams.SelfCheck");
        const string text = "customShaderVS SHD_WINDGRASS_VS\n"
            + "parameter float Specularity 0.5\nparameter float roughness 0.3\n"
            + "parameter float CustomSpecularColor 0.5 0 0\nparameter float CxMetallic 0\n"
            + "parameter float cxclearcoat 2\nparameter float CxSheenColor 0.2 0.3 0.4\n"
            + "parameter int CxOrm 1\nparameter float CxEmissiveStrengthX 7\n"
            + "parameter float fSwayStrength 0.25\nparameter int Metallicness 1\n"
            + "texture7 community_t7\ntexture11 my_orm\ntexture15 last\n";
        MtrDef m = MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes(text), "selfcheck");
        Span<float> param = stackalloc float[20], feat = stackalloc float[4];
        param.Clear(); feat.Clear();
        ResetExt(param[8..]);
        feat[0] = Feature(m.ShaderVs);
        Fold(m, param, feat);
        check.Expect(feat[0] == 1f, "customShaderVS shd_windgrass_vs is not the wind-grass feature (1)");
        check.Expect(Feature("shd_roofkn_vs") == 4 && Feature("shd_roofknm_vs") == 5 && Feature("vs_other") == 0,
                     "the roof forks are not told apart, or an unknown VS names a feature");
        check.Expect(feat[1] == 1f && feat[2] == 0.25f, "wind: an undeclared fSwaySpeed is 1, fSwayStrength lands in feat.z");
        check.Expect(param[0] == 0.5f && param[1] == 0.3f, "the stock Specularity/Roughness do not land in pbr.xy");
        check.Expect(param[2] == 0f, "an int-typed Metallicness was read (the engine binds it as an int uniform)");
        check.Expect(param[4] == 0.5f && param[7] == 1f, "a non-black CustomSpecularColor did not land with its on flag");
        check.Expect(param[8] == 0f && param[9] == -1f, "a declared CxMetallic 0 or an absent CxRoughness lost its meaning");
        check.Expect(param[10] == 1f, "CxClearcoat is not case-folded and clamped to 1");
        check.Expect(param[12] == 0.2f && param[13] == 0.3f && param[14] == 0.4f, "CxSheenColor's three components");
        check.Expect(param[16] == -1f && param[17] == 1f, "an int CxOrm 1 is not read, or a longer name matched CxEmissiveStrength");
        check.Expect(m.Textures[7] == "community_t7" && m.Textures[11] == "my_orm" && m.Textures[15] == "last",
                     "texture7/11/15 are not all parsed");
        check.Expect(DrawRecipe.MtrTextureOf(7) == 11 && DrawRecipe.MtrTextureOf(6) == 6,
                     "the AO recipe slot does not read texture11, or the flow slot moved off texture6");
        check.Expect(DrawRecipe.MaskBitOf(7, 0) == 128 && DrawRecipe.MaskBitOf(1, 0) == 1,
                     "AO is not on mask bit 128 (64 is the env bit), or texture1 is not bit 1");
        check.Expect(DrawRecipe.MaskBitOf(6, 0) == 0 && DrawRecipe.MaskBitOf(6, 7) == 32,
                     "texture6 is bound for a feature that does not read it, or not for the flow map (7)");
        log.Info(check.Ok ? "MtrParams.SelfCheck: passed" : "MtrParams.SelfCheck: FAILED");
        return check.Ok;
    }
}
