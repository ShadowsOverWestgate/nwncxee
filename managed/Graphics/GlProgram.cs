using System;
using CX.Framework;
using CX.Alloy;   // ShaderResource: override lookup + $TOKEN$ expansion, both content/render concerns

namespace CX.Graphics;

/// <summary>Shader-object plumbing shared by the Alloy pass's programs (#83): compile, link, uniform lookup.
/// Alloc-free apart from the log strings on the failure paths, which are cold by construction -- a program
/// either builds once at first use or is disabled for the session.</summary>
internal static unsafe class GlProgram
{
    private const int LogChars = 1024;

    /// <summary>Link a vertex+fragment program. Returns 0 on failure (already logged).</summary>
    internal static uint Graphics(string vs, string fs, ExtenderLog? log, string what, bool host = true)
    {
        uint v = Compile(Gl.VERTEX_SHADER, vs, log, what, host), f = Compile(Gl.FRAGMENT_SHADER, fs, log, what, host);
        if (v == 0 || f == 0) return 0;
        return Link(log, what, v, f);
    }

    /// <summary>Link a vertex+fragment program whose stages a content author may override as
    /// <c>&lt;resref&gt;.shd</c> (#92). An override that fails to compile or link rebuilds from the embedded
    /// default, so a malformed .shd costs a log line, not the frame. Returns 0 on failure.
    /// <para><paramref name="vsSource"/> and <paramref name="fsSource"/> hand back the sources actually
    /// compiled into the returned program, embedded or content. #119's depth prepass reads the vertex one to
    /// confirm the stage still carries the clip expression whose depth it re-tests, and #120 reads both to
    /// confirm they still declare the per-draw record the CPU stages -- contracts an override can silently
    /// break, so they are checked on what RAN rather than on what shipped.</para></summary>
    internal static uint Graphics(string vsRef, string vs, string fsRef, string fs, ExtenderLog? log, string what,
                                  out string vsSource, out string fsSource, bool host = true)
    {
        byte[]? bvs = ShaderResource.OverrideSpirv(vsRef, log), bfs = ShaderResource.OverrideSpirv(fsRef, log);
        if (bvs != null || bfs != null)
        {
            // ponytail: the contracts see the EMBEDDED text, because a module cannot be read for them --
            // the spirv=1 gate is the audit. Mixed GLSL+SPIR-V stages fail to link per GL 4.6 and land on
            // the same fallback below, so nothing special-cases them.
            vsSource = vs; fsSource = fs;
            uint v = bvs != null ? Binary(Gl.VERTEX_SHADER, bvs, log, what) : Compile(Gl.VERTEX_SHADER, vs, log, what, host);
            uint f = bfs != null ? Binary(Gl.FRAGMENT_SHADER, bfs, log, what) : Compile(Gl.FRAGMENT_SHADER, fs, log, what, host);
            if (v != 0 && f != 0) { uint p = Link(log, what, v, f); if (p != 0) return p; }
            else { if (v != 0) Gl.DeleteShader(v); if (f != 0) Gl.DeleteShader(f); }
            log?.Info($"{what}: SPIR-V override did not build -- falling back to the embedded default");
            return Graphics(vs, fs, log, what, host);
        }
        string? ovs = ShaderResource.Override(vsRef, log), ofs = ShaderResource.Override(fsRef, log);
        vsSource = ovs ?? vs; fsSource = ofs ?? fs;
        if (ovs == null && ofs == null) return Graphics(vs, fs, log, what, host);
        uint prog = Graphics(ovs ?? vs, ofs ?? fs, log, what, host);
        if (prog != 0) return prog;
        log?.Info($"{what}: override .shd failed to build -- falling back to the embedded default");
        vsSource = vs; fsSource = fs;
        return Graphics(vs, fs, log, what, host);
    }

    /// <summary>The overridable link for callers that do not care where the stages came from.</summary>
    internal static uint Graphics(string vsRef, string vs, string fsRef, string fs, ExtenderLog? log, string what)
        => Graphics(vsRef, vs, fsRef, fs, log, what, out _, out _);

    /// <summary>Link a compute program. Returns 0 on failure (already logged).</summary>
    internal static uint Compute(string cs, ExtenderLog? log, string what)
    {
        uint c = Compile(Gl.COMPUTE_SHADER, cs, log, what);
        return c == 0 ? 0 : Link(log, what, c);
    }

    /// <summary>The compute counterpart of the overridable <see cref="Graphics(string, string, string, string,
    /// ExtenderLog?, string)"/>: same lookup, same fall back to the embedded default on a build failure.</summary>
    internal static uint Compute(string csRef, string cs, ExtenderLog? log, string what)
    {
        byte[]? bcs = ShaderResource.OverrideSpirv(csRef, log);
        if (bcs != null)
        {
            uint c = Binary(Gl.COMPUTE_SHADER, bcs, log, what);
            if (c != 0) { uint p = Link(log, what, c); if (p != 0) return p; }
            log?.Info($"{what}: SPIR-V override did not build -- falling back to the embedded default");
            return Compute(cs, log, what);
        }
        string? ocs = ShaderResource.Override(csRef, log);
        if (ocs == null) return Compute(cs, log, what);
        uint prog = Compute(ocs, log, what);
        if (prog != 0) return prog;
        log?.Info($"{what}: override .shd failed to build -- falling back to the embedded default");
        return Compute(cs, log, what);
    }

    private static uint Link(ExtenderLog? log, string what, uint a, uint b = 0, uint c = 0, uint d = 0)
    {
        uint prog = Gl.CreateProgram();
        Gl.AttachShader(prog, a);
        if (b != 0) Gl.AttachShader(prog, b);
        if (c != 0) Gl.AttachShader(prog, c);
        if (d != 0) Gl.AttachShader(prog, d);
        Gl.LinkProgram(prog);
        int ok; Gl.GetProgramiv(prog, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(a);
        if (b != 0) Gl.DeleteShader(b);
        if (c != 0) Gl.DeleteShader(c);
        if (d != 0) Gl.DeleteShader(d);
        if (ok != 0) { Notes(prog, false, log, what); return prog; }
        log?.Info($"{what}: link failed -- {InfoLog(prog, false)}");
        Gl.DeleteProgram(prog);
        return 0;
    }

    /// <summary>Compile one stage. The source's sizing tokens are expanded here and nowhere else, so an
    /// embedded default and a content override read identically (see <see cref="ShaderResource.Tokens"/>),
    /// and <see cref="VanillaShader.Splice"/> runs at the same seam for the same reason.</summary>
    private static uint Compile(uint type, string rawSrc, ExtenderLog? log, string what, bool host = true)
    {
        // #346: the vanilla bridge first, sizing tokens second -- spliced stock text is subject to the same
        // token expansion as everything else, and a stock file carrying none is unaffected either way.
        uint sh = CompileText(type, ShaderResource.Tokens(VanillaShader.Splice(rawSrc, log, what, true, host)), log, what);
        if (sh != 0 || !VanillaShader.HasSplice(rawSrc)) return sh;
        if (host && VanillaShader.HasHost(rawSrc))
        {
            // ponytail: every hosted region at once; bisecting to the one that failed is the upgrade
            log?.Info($"{what}: WARN the spliced text did not compile -- retrying with every hosted content stage "
                    + "stubbed, so those materials draw the plain lit path and everything else still splices");
            sh = CompileText(type, ShaderResource.Tokens(VanillaShader.Splice(rawSrc, log, what, host: false)), log, what);
            if (sh != 0) return sh;
        }
        // #346: a content .shd that renamed a function the stage calls would otherwise take the WHOLE
        // program down -- and for the lit stage that means the world reverts to engine drawing over one
        // edit in a hak. The stub is what that retry is for: the feature off, the world still rendering.
        log?.Info($"{what}: WARN the spliced vanilla text did not compile -- retrying with the fallback stub, "
                + "so the program links and the feature it carried is off");
        return CompileText(type, ShaderResource.Tokens(VanillaShader.Splice(rawSrc, log, what, false)), log, what);
    }

    /// <summary>Specialize one SPIR-V stage (GL 4.6 core). No token expansion, no vanilla splice and no
    /// contract audit reach a module -- see <see cref="ShaderResource.OverrideSpirv"/>. Entry point is
    /// <c>main</c>; specialization constants are not fed, so a module must bake its own sizes.</summary>
    private static uint Binary(uint type, byte[] spirv, ExtenderLog? log, string what)
    {
        if (Gl.ShaderBinary == null || Gl.SpecializeShader == null)
        {
            log?.Info($"{what}: driver exposes no glShaderBinary/glSpecializeShader -- SPIR-V stage refused");
            return 0;
        }
        uint sh = Gl.CreateShader(type);
        // glShaderBinary rejects an unsupported format with INVALID_ENUM and leaves the object uncompiled,
        // which the COMPILE_STATUS below reads as a plain build failure -- no separate capability query.
        fixed (byte* p = spirv) Gl.ShaderBinary(1, &sh, Gl.SHADER_BINARY_FORMAT_SPIR_V, p, spirv.Length);
        byte* entry = stackalloc byte[5];
        entry[0] = (byte)'m'; entry[1] = (byte)'a'; entry[2] = (byte)'i'; entry[3] = (byte)'n'; entry[4] = 0;
        Gl.SpecializeShader(sh, entry, 0, null, null);
        int ok; Gl.GetShaderiv(sh, Gl.COMPILE_STATUS, &ok);
        if (ok != 0)
        {
            Notes(sh, true, log, what);
            log?.Info($"{what}: SPIR-V module accepted ({spirv.Length} bytes, type=0x{type:x}) -- "
                    + "token expansion, vanilla splice and the stage contracts are all skipped for it");
            return sh;
        }
        log?.Info($"{what}: SPIR-V specialize failed (type=0x{type:x}) -- {InfoLog(sh, true)}");
        Gl.DeleteShader(sh);
        return 0;
    }

    private static uint CompileText(uint type, string src, ExtenderLog? log, string what)
    {
        uint sh = Gl.CreateShader(type);
        Span<byte> b = stackalloc byte[src.Length + 1];
        for (int i = 0; i < src.Length; i++) b[i] = (byte)src[i];
        b[src.Length] = 0;
        fixed (byte* p = b) { byte* pp = p; Gl.ShaderSource(sh, 1, &pp, null); }
        Gl.CompileShader(sh);
        int ok; Gl.GetShaderiv(sh, Gl.COMPILE_STATUS, &ok);
        if (ok != 0) { Notes(sh, true, log, what); return sh; }
        log?.Info($"{what}: shader compile failed (type=0x{type:x}) -- {InfoLog(sh, true)}");
        Gl.DeleteShader(sh);
        return 0;
    }

    /// <summary>A successful build's info log, when the driver left one. Mesa's compiler reports warnings
    /// there (unused varyings, implicit conversions, extension notes) that NVIDIA's never mentions, so it is
    /// the one channel a "compiles on both, differs on one" stage announces itself on. Cold: build time.</summary>
    private static void Notes(uint obj, bool shader, ExtenderLog? log, string what)
    {
        if (log == null) return;
        string s = InfoLog(obj, shader);
        if (s.Length == 0 || s == "(no info log)") return;
        log.Info($"{what}: driver notes on a successful build -- {s}");
    }

    /// <summary>Driver info log, truncated. Cold: build failures and <see cref="Notes"/>.</summary>
    private static string InfoLog(uint obj, bool shader)
    {
        Span<byte> buf = stackalloc byte[LogChars];
        int len = 0;
        fixed (byte* p = buf)
        {
            if (shader) Gl.GetShaderInfoLog(obj, LogChars, &len, p);
            else Gl.GetProgramInfoLog(obj, LogChars, &len, p);
        }
        if (len <= 0) return "(no info log)";
        if (len > LogChars) len = LogChars;
        Span<char> chars = stackalloc char[len];
        for (int i = 0; i < len; i++) chars[i] = (char)buf[i];
        return new string(chars).Replace('\n', ' ').Trim();
    }

    /// <summary>Uniform location by name; -1 when the program does not use it.</summary>
    internal static int Loc(uint prog, string name)
    {
        Span<byte> b = stackalloc byte[name.Length + 1];
        for (int i = 0; i < name.Length; i++) b[i] = (byte)name[i];
        b[name.Length] = 0;
        fixed (byte* p = b) return Gl.GetUniformLocation(prog, p);
    }
}
