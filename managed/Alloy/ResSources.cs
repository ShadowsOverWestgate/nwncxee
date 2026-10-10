using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Where an area load's resources are actually read from: every outermost <c>CExoResMan::Demand</c> is filed by
/// the container its <c>CRes</c> names -- the kind int at <see cref="EngineLayout.CResSourceKind"/>, the one the
/// engine's own dispatch branches on -- split texture restypes against the rest and printed once on the
/// <see cref="TexStream"/> <c>[load]</c> line. Hot: one bucket map and one array add, no allocation.
/// </summary>
internal static class ResSources
{
    /// <summary>The buckets, in report order. <see cref="Other"/> catches a <c>RESID</c> kind the engine's
    /// dispatch does not name, which would mean the vocabulary moved under us.</summary>
    private enum Src { None, Key, Erf, Dir, NwSync, Other }
    private const int N = (int)Src.Other + 1;
    private static readonly long[] _tex = new long[N], _rest = new long[N];

    /// <summary>The engine's <c>RESID</c> container kinds, as <c>CExoResMan::Demand</c> branches on them.</summary>
    private const int KindNone = 0, KindKey = 1, KindErf = 2, KindDir = 3, KindNwSync = 5;

    /// <summary>A <c>RESID</c> kind to its bucket. Pure -- the self-check is the whole of it.</summary>
    private static int Bucket(int kind) => kind switch
    {
        KindNone => (int)Src.None,
        KindKey => (int)Src.Key,
        KindErf => (int)Src.Erf,
        KindDir => (int)Src.Dir,
        KindNwSync => (int)Src.NwSync,
        _ => (int)Src.Other,
    };

    /// <summary>File one demand, nested ones included. <paramref name="texture"/> = its restype is DDS, TGA or PLT.</summary>
    internal static void Note(int kind, bool texture)
    {
        int b = Bucket(kind);
        if (texture) _tex[b]++; else _rest[b]++;
    }

    /// <summary>Start an area load's tally over.</summary>
    internal static void Reset()
    {
        for (int i = 0; i < N; i++) { _tex[i] = 0; _rest[i] = 0; }
    }

    /// <summary>The tally as one clause, buckets that saw nothing left out; empty when nothing was filed.
    /// Cold: once per area transition.</summary>
    internal static string Line()
    {
        long t = 0, r = 0;
        for (int i = 0; i < N; i++) { t += _tex[i]; r += _rest[i]; }
        if (t + r == 0) return "";
        var sb = new System.Text.StringBuilder($"demands {t + r} ({t} texture)");
        for (int i = 0; i < N; i++)
        {
            if (_tex[i] + _rest[i] == 0) continue;
            sb.Append($" {(Src)i} {_tex[i] + _rest[i]} ({_tex[i]} tex)");
        }
        return sb.ToString();
    }

    /// <summary>Off-device: the bucket map against the four kinds the engine dispatches on, the unknown
    /// catch-all, and that the line counts textures inside the total rather than beside it.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ResSources.SelfCheck");
        check.Expect(Bucket(KindKey) == (int)Src.Key && Bucket(KindErf) == (int)Src.Erf
                     && Bucket(KindDir) == (int)Src.Dir && Bucket(KindNwSync) == (int)Src.NwSync
                     && Bucket(KindNone) == (int)Src.None, "a RESID kind lands in the wrong bucket");
        check.Expect(Bucket(4) == (int)Src.Other && Bucket(-1) == (int)Src.Other && Bucket(99) == (int)Src.Other,
                     "a kind the engine does not dispatch on is not filed as Other");
        Reset();
        check.Expect(Line().Length == 0, "an empty tally prints nothing at all");
        Note(KindKey, true); Note(KindKey, false); Note(KindNwSync, true); Note(7, false);
        string line = Line();
        check.Expect(line.StartsWith("demands 4 (2 texture)"), $"the totals are wrong: {line}");
        check.Expect(line.Contains("Key 2 (1 tex)") && line.Contains("NwSync 1 (1 tex)") && line.Contains("Other 1 (0 tex)")
                     && !line.Contains("Dir"), $"the per-bucket clause is wrong: {line}");
        Reset();
        if (check.Ok) log.Info("ResSources.SelfCheck: passed");
        return check.Ok;
    }
}
