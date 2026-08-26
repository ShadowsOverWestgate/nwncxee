using System;
using Sowx.Framework;
using Sowx.Alloy;   // ShaderResource: override lookup + $TOKEN$ expansion, both content/render concerns

namespace Sowx.Graphics;

/// <summary>Shader-object plumbing shared by the Alloy pass's programs (#83): compile, link, uniform lookup.
/// Alloc-free apart from the log strings on the failure paths, which are cold by construction -- a program
/// either builds once at first use or is disabled for the session.</summary>
internal static unsafe class GlProgram
{
    private const int LogChars = 1024;

    /// <summary>Link a vertex+fragment program. Returns 0 on failure (already logged).</summary>
    internal static uint Graphics(string vs, string fs, ExtenderLog? log, string what)
    {
        uint v = Compile(Gl.VERTEX_SHADER, vs, log, what), f = Compile(Gl.FRAGMENT_SHADER, fs, log, what);
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
                                  out string vsSource, out string fsSource)
    {
        string? ovs = ShaderResource.Override(vsRef, log), ofs = ShaderResource.Override(fsRef, log);
        vsSource = ovs ?? vs; fsSource = ofs ?? fs;
        if (ovs == null && ofs == null) return Graphics(vs, fs, log, what);
        uint prog = Graphics(ovs ?? vs, ofs ?? fs, log, what);
        if (prog != 0) return prog;
        log?.Info($"{what}: override .shd failed to build -- falling back to the embedded default");
        vsSource = vs; fsSource = fs;
        return Graphics(vs, fs, log, what);
    }

    /// <summary>The overridable link for callers that do not care where the stages came from.</summary>
    internal static uint Graphics(string vsRef, string vs, string fsRef, string fs, ExtenderLog? log, string what)
        => Graphics(vsRef, vs, fsRef, fs, log, what, out _, out _);

    /// <summary>#147: link a vertex + tessellation-pair + fragment program, every stage overridable. Water is
    /// the only class that tessellates: its geometry is two triangles per ten-unit tile, so a displacement in
    /// the vertex stage would move four corners and nothing between them.
    /// <para>A failure in ANY stage falls back to the whole embedded set rather than mixing a content vertex
    /// stage with an embedded evaluation one -- the two share the wave field and a half-applied override
    /// would displace the surface out from under the normals.</para>
    /// <para>All four sources come back, not just the outer pair: #161's record guard applies to every stage
    /// that declares the per-draw record, and on water that is the tessellation pair too.</para></summary>
    internal static uint Graphics(string vsRef, string vs, string tcRef, string tc, string teRef, string te,
                                  string fsRef, string fs, ExtenderLog? log, string what,
                                  out string vsSource, out string tcSource, out string teSource,
                                  out string fsSource)
    {
        string? ovs = ShaderResource.Override(vsRef, log), otc = ShaderResource.Override(tcRef, log);
        string? ote = ShaderResource.Override(teRef, log), ofs = ShaderResource.Override(fsRef, log);
        vsSource = ovs ?? vs; tcSource = otc ?? tc; teSource = ote ?? te; fsSource = ofs ?? fs;
        uint prog = Tessellated(ovs ?? vs, otc ?? tc, ote ?? te, ofs ?? fs, log, what);
        if (prog != 0 || (ovs == null && otc == null && ote == null && ofs == null)) return prog;
        log?.Info($"{what}: override .shd failed to build -- falling back to the embedded default");
        vsSource = vs; tcSource = tc; teSource = te; fsSource = fs;
        return Tessellated(vs, tc, te, fs, log, what);
    }

    /// <summary>The embedded-only four-stage link, for the rebuild path.</summary>
    internal static uint Tessellated(string vs, string tc, string te, string fs, ExtenderLog? log, string what)
    {
        uint v = Compile(Gl.VERTEX_SHADER, vs, log, what);
        uint c = Compile(Gl.TESS_CONTROL_SHADER, tc, log, what);
        uint e = Compile(Gl.TESS_EVALUATION_SHADER, te, log, what);
        uint f = Compile(Gl.FRAGMENT_SHADER, fs, log, what);
        if (v == 0 || c == 0 || e == 0 || f == 0)
        {
            // Partial success still allocated shader objects; Link is what normally deletes them.
            if (v != 0) Gl.DeleteShader(v);
            if (c != 0) Gl.DeleteShader(c);
            if (e != 0) Gl.DeleteShader(e);
            if (f != 0) Gl.DeleteShader(f);
            return 0;
        }
        return Link(log, what, v, c, e, f);
    }

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
        if (ok != 0) return prog;
        log?.Info($"{what}: link failed -- {InfoLog(prog, false)}");
        Gl.DeleteProgram(prog);
        return 0;
    }

    /// <summary>Compile one stage. The source's sizing tokens are expanded here and nowhere else, so an
    /// embedded default and a content override read identically (see <see cref="ShaderResource.Tokens"/>).</summary>
    private static uint Compile(uint type, string rawSrc, ExtenderLog? log, string what)
    {
        string src = ShaderResource.Tokens(rawSrc);
        uint sh = Gl.CreateShader(type);
        Span<byte> b = stackalloc byte[src.Length + 1];
        for (int i = 0; i < src.Length; i++) b[i] = (byte)src[i];
        b[src.Length] = 0;
        fixed (byte* p = b) { byte* pp = p; Gl.ShaderSource(sh, 1, &pp, null); }
        Gl.CompileShader(sh);
        int ok; Gl.GetShaderiv(sh, Gl.COMPILE_STATUS, &ok);
        if (ok != 0) return sh;
        log?.Info($"{what}: shader compile failed (type=0x{type:x}) -- {InfoLog(sh, true)}");
        Gl.DeleteShader(sh);
        return 0;
    }

    /// <summary>Driver info log, truncated. Cold: only ever runs on a build failure.</summary>
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
