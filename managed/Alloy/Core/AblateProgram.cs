using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>CX-188: <c>/alloy bench</c>'s ABLATION ladder for a compute pass -- the pass's own stage rebuilt
/// with <c>#define CX_ABLATE n</c> after its <c>#version</c> line, so the shader stops after rung n and folds a
/// cheap function of what that rung produced into its own output. Two rungs' GPU terms differ by exactly the
/// stage between them, which is how the bench prices a pass whose total is known and whose shape is not.
/// <para>Bench-only and lazy. Nothing is built at boot or on any frame the bench is not driving, no variant is
/// ever bound outside a bench step, and a variant that fails to link refuses THAT STEP alone -- the pass keeps
/// its shipped program and the feature stands. <c>check-shaders.py</c> compiles and driver-links every rung, so
/// a rung that stopped compiling is a failed gate rather than a surprise in a live bench.</para></summary>
internal struct AblateProgram
{
    /// <summary>The longest ladder any pass has (<c>cx_deferred_cs</c>'s eight rungs, plus its probes at 9..12). Rung 0
    /// is the shipped program, which no variant stands for.</summary>
    internal const int MaxStage = 12;

    private uint[]? _progs;
    private bool[]? _failed;

    /// <summary>The variant program for <paramref name="stage"/>, built on the first bench step that asks for
    /// it; 0 = it did not link, latched so one refused step does not recompile every frame.</summary>
    internal uint Get(string resref, int stage, ExtenderLog? log, string what)
    {
        if (stage < 1 || stage > MaxStage) return 0;
        _progs ??= new uint[MaxStage + 1];
        _failed ??= new bool[MaxStage + 1];
        if (_progs[stage] != 0) return _progs[stage];
        if (_failed[stage]) return 0;
        // The override's text where there is one, so an ablation measures the stage the session actually runs.
        string src = ShaderResource.Override(resref, log) ?? ShaderStages.Source(resref);
        uint p = GlProgram.Compute(WithDefine(src, $"CX_ABLATE {stage}"), log, $"{what} ablate {stage}");
        if (p == 0) { _failed[stage] = true; return 0; }
        _progs[stage] = p;
        return p;
    }

    /// <summary>Context loss: every variant name is gone, and a rung that failed on the old context gets
    /// another try on the new one.</summary>
    internal void Forget() { _progs = null; _failed = null; }

    /// <summary>The define goes immediately after the <c>#version</c> line -- the one place GLSL takes it --
    /// which is the same splice <c>check-shaders.py</c> makes, so the gate compiles the text the bench runs.</summary>
    internal static string WithDefine(string src, string define)
    {
        int v = src.IndexOf("#version", StringComparison.Ordinal);
        int e = v < 0 ? -1 : src.IndexOf('\n', v);
        return e < 0 ? src : src[..(e + 1)] + $"#define {define}\n" + src[(e + 1)..];
    }
}
