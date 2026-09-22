using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CX.Framework;

namespace CX.Alloy;

/// <summary>#707: the embedded GLSL. Every Alloy stage and library is a <c>.glsl</c> file under
/// <c>managed/Alloy/Shaders/</c>, embedded in the image (ADR 0012 -- shipped inside the extender, never as
/// content), and this is the one reader. A file composes others by naming them on a line of their own:
/// <c>#include "Other.glsl"</c>, spliced here, so a stage's composition is written in GLSL rather than in C#
/// string concatenation. Anything else -- the engine's own <c>#include "inc_common"</c> inside a stand-in,
/// a line inside a comment block -- is left exactly as it stands: only a <c>.glsl</c> name that resolves to
/// an embedded file is expanded.</summary>
internal static class ShaderSource
{
    /// <summary>Include nesting cap. Four is the deepest chain that ships; the cap is what turns a cycle
    /// into a log line instead of a hang.</summary>
    private const int MaxDepth = 8;

    /// <summary>One embedded stage or library, its includes spliced. Returns "" (logged) for a name no
    /// resource answers -- the program built from it then fails to compile, which is the loud failure.
    /// <para>ponytail: re-reads and re-expands per call. Cold by construction -- program builds, the
    /// shader dump and the SelfCheck suites -- so no cache; add one keyed by name if a per-frame caller
    /// ever appears.</para></summary>
    internal static string Get(string name) => Get(name, 0);

    private static string Get(string name, int depth)
    {
        string? src = Read(name);
        if (src == null)
        {
            KnobGroup.Log?.Info($"ShaderSource: no embedded {name} -- whatever names it will not build");
            return "";
        }
        if (depth >= MaxDepth)
        {
            KnobGroup.Log?.Info($"ShaderSource: {name} is {MaxDepth} includes deep -- a cycle? its own includes stand unexpanded");
            return src;
        }
        if (!src.Contains("#include \"")) return src;
        string[] lines = src.Split('\n');
        StringBuilder sb = new(src.Length * 2);
        for (int i = 0; i < lines.Length; i++)
        {
            string inc = Included(lines[i]);
            if (inc.Length > 0)
            {
                string body = Get(inc, depth + 1);
                if (body.Length > 0)
                {
                    sb.Append(body);
                    if (!body.EndsWith('\n')) sb.Append('\n');
                    continue;
                }
            }
            sb.Append(lines[i]);
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The resource an <c>#include</c> line names, or "" for any other line. The whole line has to
    /// be the directive: the SMAA notice carries <c>#include "SMAA.h"</c> inside a comment block, and a
    /// hosted stand-in carries the engine's <c>#include "inc_scriptable"</c>.</summary>
    private static string Included(string line)
    {
        string t = line.Trim();
        return t.StartsWith("#include \"", StringComparison.Ordinal) && t.EndsWith(".glsl\"", StringComparison.Ordinal)
             ? t[10..^1] : "";
    }

    private static string? Read(string name)
    {
        using Stream? s = typeof(ShaderSource).Assembly.GetManifestResourceStream(name);
        if (s == null) return null;
        using StreamReader r = new(s);
        // A CRLF checkout would otherwise put \r inside every line the stages match on themselves.
        return r.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>Every embedded .glsl loads and every include in it resolves. A mistyped include is
    /// otherwise invisible until the one program that carries it builds -- on a machine that reaches that
    /// feature.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        int n = 0;
        List<string> bad = new();
        foreach (string res in typeof(ShaderSource).Assembly.GetManifestResourceNames())
        {
            if (!res.EndsWith(".glsl", StringComparison.Ordinal)) continue;
            n++;
            string src = Get(res);
            if (src.Length == 0) { bad.Add(res); continue; }
            foreach (string line in src.Split('\n'))
                if (Included(line).Length > 0) { bad.Add($"{res} -> {Included(line)}"); break; }
        }
        bool ok = n > 0 && bad.Count == 0;
        log.Info(ok ? $"ShaderSource.SelfCheck: passed ({n} embedded .glsl, every include resolved)"
                    : $"ShaderSource.SelfCheck: FAILED -- {(n == 0 ? "no .glsl embedded at all" : "unresolved: " + string.Join(", ", bad))}");
        return ok;
    }
}
