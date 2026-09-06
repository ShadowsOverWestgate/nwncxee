using System;
using System.IO;
using Sowx.Framework;
using Sowx.Graphics;

namespace Sowx.Alloy;

/// <summary>The overridable stage set: which resrefs exist, what GLSL the extender compiles for each, and
/// the warning header a dumped copy carries. This is the authoritative composition table (ADR 0012) --
/// every program build reads <see cref="Source"/> rather than concatenating its own text, and
/// <c>check-shaders.py</c> is bound to this table by #162.
/// <para>Shape contract, because a Python harvester scrapes it: <see cref="Source"/> is one switch arm per
/// resref, the resref constant on the left, a plain <c>+</c> chain of <c>const string</c> names on the
/// right -- no interpolation, no helper calls, nothing conditional. The header constants are raw literals
/// for the same reason.</para></summary>
internal static class ShaderStages
{
    /// <summary>The dump folder, inside the client's own user directory (next to nwn.ini).</summary>
    internal static string DumpDir => Native.ConfigPath("cx_shader_dump");

    internal const string Banner = """
        // Dumped from sow-client-extender's embedded default by /dumpshaders -- the stage THIS build
        // compiles, so it is current by construction. Drop it in a hak or override as <resref>.shd to
        // replace that stage; the extender falls back to the embedded default if it fails to build.
        // $CAP$ / $GROUP$ / $SRGB$ / $BAND$ are expanded by the extender on load -- leave them as tokens.
        // The contract an override has to keep: docs/shader-overrides.md.


        """;

    internal const string Invariant = """
        // LOAD-BEARING: `invariant gl_Position;` and the clip expression below. The depth prepass writes
        // these depths and the colour pass re-tests them with LEQUAL, so any other spelling loses fragments
        // to a ULP of drift -- holes, and no log line. See docs/shader-overrides.md.


        """;

    /// <summary>#355: the grass vertex stage is invariant over ITSELF, not over the world's clip expression:
    /// its depth prepass and its colour pass are two programs compiled from this one text, and the colour
    /// pass tests LEQUAL against the prepass's depth. It displaces, so it neither computes nor promises the
    /// <see cref="Invariant"/> expression.</summary>
    internal const string SelfInvariant = """
        // LOAD-BEARING: `invariant gl_Position;`. This stage is compiled into TWO programs -- the grass depth
        // prepass and the grass colour pass -- and the second re-tests the first's depth with LEQUAL, so the
        // clip position must come out bit-identical from both. Keep the declaration; the expression is yours.
        // See docs/shader-overrides.md.


        """;

    internal const string Record = """
        // LOAD-BEARING: the SowxDraw struct below is the extender's per-draw SSBO record, and this file is a
        // COPY of a layout that lives in C#. Drop or reorder a field and every draw reads its neighbour's.
        // The extender counts the vec4s and refuses a stage that disagrees. See docs/shader-overrides.md.


        """;

    internal const string CasterRecord = """
        // LOAD-BEARING: this stage reads ALLOY's per-draw record (SowxDraw) at gl_BaseInstance, which IS
        // the ownership slot (#303 -- the gather dispatch writes it into every surviving command). Index it
        // any other way and every caster bakes at its neighbour's matrix. The skinning branch is
        // load-bearing too: without it every creature casts its bind pose. The extender refuses a stage
        // that disagrees. See docs/shader-overrides.md.


        """;

    internal const string BindlessNote = """
        // REFERENCE ONLY -- not a starting point for an override, and not loadable under this name. This is
        // the lit fragment stage after the extender's own bindless rewrite (#153), which it applies to
        // whatever cx_ownlit_fs supplies when ARB_bindless_texture is available. Override that instead.


        """;

    /// <summary>Every overridable resref with the header its dumped copy carries. All twenty-three: four owned,
    /// two water, two grass, the cluster build, two caster, four post-chain stages and the post stack's gamma
    /// stage (#375), the taa stage (#389) and the cel stage. #261 deleted one with SkyComposite and #339 deleted the water tessellation pair with the
    /// custom sea; <see cref="SelfCheck"/> asserts the count, so this prose and that assert have to move
    /// together.</summary>
    internal static readonly (string Ref, string Header)[] All =
    {
        (AlloyRenderer.ShdFullbrightVs, Banner + Invariant + Record),
        (AlloyRenderer.ShdFullbrightFs, Banner),
        (AlloyRenderer.ShdLitVs,        Banner + Invariant + Record),
        (AlloyRenderer.ShdLitFs,        Banner + Record),
        (AlloyRenderer.ShdWaterVs,      Banner + Invariant + Record),
        (AlloyRenderer.ShdWaterFs,      Banner + Record),
        (GrassField.ShdGrassVs,            Banner + SelfInvariant),
        (GrassField.ShdGrassFs,            Banner),
        (AlloyLights.ShdClusterCs,         Banner),
        (CasterPass.ShdCastVs,             Banner + Invariant + Record + CasterRecord),
        (CasterPass.ShdCastFs,             Banner),
        (PostChain.ShdPostVs,              Banner),
        (PostChain.ShdBloomDownFs,         Banner),
        (PostChain.ShdBloomUpFs,           Banner),
        (PostChain.ShdResolveFs,           Banner),
        (PostStack.ShdGammaFs,             Banner),
        (SmaaStage.ShdEdgeFs,              Banner),
        (SmaaStage.ShdWeightFs,            Banner),
        (SmaaStage.ShdBlendFs,             Banner),
        (PostStack.ShdTaaFs,               Banner),
        (PostStack.ShdToonFs,              Banner),
        (SsaoPass.ShdSsaoFs,               Banner),
        (SsaoPass.ShdSsaoBlurFs,           Banner),
    };

    /// <summary>The GLSL the extender compiles for one resref, sizing tokens unexpanded
    /// (<see cref="ShaderResource.Tokens"/> expands them at compile time, for an override exactly as for the
    /// embedded default). Cold: one call per program build, one per dumped file.</summary>
    internal static string Source(string resref) => resref switch
    {
        AlloyRenderer.ShdFullbrightVs => AlloyRenderer.OwnedVs,
        AlloyRenderer.ShdFullbrightFs => AlloyRenderer.OwnedFs,
        AlloyRenderer.ShdLitVs => AlloyRenderer.OwnedLitVs,
        AlloyRenderer.ShdLitFs => AlloyRenderer.OwnedLitFsHead + AlloyLights.LocalShadowLib + AlloyLights.FragmentLib + AlloyLights.SunShadowLib + AlloyRenderer.MaterialLib + AlloyRenderer.KeyFogFs + AlloyRenderer.VanillaEnvFs + AlloyRenderer.HookFs + AlloyRenderer.TargetFs + AlloyRenderer.HostFs + AlloyRenderer.OwnedLitFsMain,
        AlloyRenderer.ShdWaterVs => FrameSnapshot.VsHead + AlloyRenderer.DrawBufDecl + FrameSnapshot.VsMain,
        AlloyRenderer.ShdWaterFs => FrameSnapshot.FsHead + AlloyLights.LocalShadowLib + AlloyLights.FragmentLib + AlloyLights.SunShadowLib + AlloyRenderer.KeyFogFs + AlloyRenderer.VanillaEnvFs + AlloyRenderer.HookFs + AlloyRenderer.TargetFs + FrameSnapshot.FsMain,
        GrassField.ShdGrassVs => GrassField.GrassVs,
        GrassField.ShdGrassFs => GrassField.GrassFsHead + AlloyLights.LocalShadowLib + AlloyLights.FragmentLib + AlloyLights.SunShadowLib + AlloyRenderer.KeyFogFs + AlloyRenderer.VanillaEnvFs + AlloyRenderer.HookFs + AlloyRenderer.TargetFs + GrassField.GrassFsMain,
        AlloyLights.ShdClusterCs => AlloyLights.BuildSrc,
        CasterPass.ShdCastVs => CasterPass.CastVs,
        CasterPass.ShdCastFs => CasterPass.CastFs,
        PostChain.ShdPostVs => PostChain.FullscreenVs,
        PostChain.ShdBloomDownFs => PostChain.BloomDownFs,
        PostChain.ShdBloomUpFs => PostChain.BloomUpFs,
        PostChain.ShdResolveFs => PostChain.ResolveFs,
        PostStack.ShdGammaFs => PostStack.PostStageHead + PostStack.GammaStageFs + PostStack.PostStageMain,
        SmaaStage.ShdEdgeFs => SmaaStage.EdgeFs,
        SmaaStage.ShdWeightFs => SmaaStage.WeightFs,
        SmaaStage.ShdBlendFs => PostStack.PostStageHead + SmaaStage.BlendStageFs + PostStack.PostStageMain,
        PostStack.ShdTaaFs => PostStack.PostStageHead + TaaStage.StageFs + PostStack.PostStageMain,
        PostStack.ShdToonFs => PostStack.PostStageHead + ToonStage.StageFs + PostStack.PostStageMain,
        SsaoPass.ShdSsaoFs => SsaoPass.SsaoFs,
        SsaoPass.ShdSsaoBlurFs => SsaoPass.SsaoBlurFs,
        _ => "",
    };

    /// <summary>Write every overridable stage into <paramref name="dir"/> as an editable
    /// <c>&lt;resref&gt;.shd</c>, plus the bindless variant of the lit stage for reference. Returns the file
    /// count, or -1 if the folder could not be written (message logged).</summary>
    internal static int Dump(string dir, ExtenderLog? log)
    {
        try
        {
            Directory.CreateDirectory(dir);
            int n = 0;
            foreach ((string r, string header) in All)
            {
                File.WriteAllText(Path.Combine(dir, r + ".shd"), header + Source(r));
                n++;
            }
            // A dot makes this unloadable as a resref, which is the point: it documents the rewrite the
            // extender performs, and overriding it would be overriding nothing.
            File.WriteAllText(Path.Combine(dir, "cx_ownlit_fs.bindless.shd"),
                              BindlessNote + AlloyRenderer.BindlessFs(Source(AlloyRenderer.ShdLitFs)));
            n++;
            log?.Info($"ShaderStages: dumped {n} stages to {dir}");
            return n;
        }
        catch (Exception e)
        {
            log?.Info($"ShaderStages: dump to {dir} failed -- {e.Message}");
            return -1;
        }
    }

    /// <summary>The headless trigger: <c>dumpshaders=1</c> in <c>sowx.cfg</c> dumps once at SelfCheck, so
    /// the assembled stages -- including the bindless rewrite, which is a C# function and so out of reach of
    /// the offline checker -- can be compiled without a live client. Bucket 2 of ADR 0013: the decision has
    /// to be made before the first frame, so it cannot be a chat command.</summary>
    internal static void DumpIfConfigured(ExtenderLog? log)
    {
        if (Settings.Bool("dumpshaders", false)) Dump(DumpDir, log);
    }

    /// <summary>Every resref composes to something, and a header claiming the per-draw record is on a stage
    /// that declares it. A missing switch arm is otherwise invisible until a program builds from "".</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = All.Length == 23;
        foreach ((string r, string header) in All)
        {
            string src = Source(r);
            ok &= src.Length > 0 && r.Length <= ShaderResource.ResRefBytes
                  && src.Contains("#version")
                  && header.Contains("docs/shader-overrides.md")
                  && header.Contains(Record) == src.Contains("struct SowxDraw")
                  && header.Contains(CasterRecord) == src.Contains("uDraw[gl_BaseInstance]")
                  && (header.Contains(Invariant) || header.Contains(SelfInvariant)) == src.Contains("invariant gl_Position;");
        }
        log.Info(ok
            ? $"ShaderStages.SelfCheck: passed ({All.Length} overridable resrefs)"
            : "ShaderStages.SelfCheck: FAILED -- a resref composes to nothing, or a dump header claims a "
              + "constraint its stage does not carry");
        return ok;
    }
}
