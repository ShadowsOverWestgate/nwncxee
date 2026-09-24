using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The VANILLA BRIDGE: an Alloy stage can name a stock <c>.shd</c> include and get the engine's own
/// text spliced into it at compile time, so shading the game already expresses -- and that a content repo has
/// already customised -- is not re-implemented here (#346).
/// <para><b>What this is for.</b> Most of what <c>sow-topdata</c> changes about the look is fragment-side
/// arithmetic that is a pure function of the fragment's colour, its world position and the engine's global
/// shader constants: the targeting shape, racial vision, the noir desaturation. None of it needs Alloy's SSBO
/// records, bindless handles or clustered lights -- it needs a <c>vec3</c> and a world position. Those are
/// the files that drift when the extender carries a second copy, and exactly the ones this can splice.</para>
/// <para><b>What it hosts.</b> A material's own custom FRAGMENT stage, as a function of the lit program: the
/// body between <c>SetupStandardShaderInputs()</c> and <c>ApplyStandardShader()</c> is the look, and both of
/// those calls are Alloy's own work already done. The stage is spliced with its includes, <c>inc_standard</c>
/// is substituted by two no-ops, <c>main()</c> is renamed, and the material terms are read back off its
/// <c>MATERIAL_*_VALUE</c> macros (<see cref="HostText"/>) (#350).</para>
/// <para><b>What it is NOT for.</b> A stock stage's LIGHTING (<c>ComputeLighting</c> over the
/// <c>MAX_NUM_LIGHTS</c> arrays is the submission model the frame budget cannot afford, #327) and any VERTEX
/// stage, which computes <c>gl_Position</c> its own way against the prepass invariance constraint (#119).</para>
/// <para><b>Why source splice and not a linked shader object.</b> Both link (a 330-core object into a
/// 460-core program on Mesa 26.1 and NVIDIA 610.57 alike, measured offline), but the engine compiles an
/// include as a standalone unit only with ITS includes dragged in, whose declarations (<c>in</c> varyings with
/// no matching Alloy output, uniform blocks) collide at link time. Splicing text lets Alloy supply the
/// environment instead: see <c>TargetFs</c>'s compatibility prelude.</para>
/// <para>Cold: a program builds once per session.</para></summary>
internal static unsafe class VanillaShader
{
    /// <summary>#346: the arming decision (ADR 0013 bucket 2 -- a capability gate, not a look), read once.
    /// <c>alloy.vanillabridge=0</c> in <c>cx.cfg</c> compiles every stage from its fallback stubs and asks the
    /// engine for nothing -- which turns off every feature the bridge carries, and nothing else. It exists because this path took the client down once already: reading a
    /// resource the engine owns is engine surgery, and a kill switch that needs no rebuild is what a
    /// player-facing failure wants.</summary>
    private static readonly bool _armed = Renderer.VanillaBridge.Boot;

    /// <summary>#350: the hosting gate alone (a Restart knob). <c>alloy.hostfs=0</c> in <c>cx.cfg</c> compiles
    /// every hosted region from its stub while the plain splices stay -- the A/B for "the lit program got
    /// slower for everyone": three content bodies inside one uber-program cost registers on every fragment,
    /// whether or not a material in the area ever takes that branch.</summary>
    private static readonly bool _hostArmed = Renderer.HostFs.Boot;
    internal static bool HostArmed => _hostArmed;

    /// <summary>The marker pair. Everything between them is REPLACED by the engine's copy of the named
    /// resref when there is one; what a stage writes there is a STUB -- the feature off -- never a copy of
    /// the file. A copy would be exactly the second implementation this bridge deletes, and it would run
    /// only on the path where the splice failed, where nobody would ever see it drift.
    /// <para>The stub's job is the PROGRAM: it has to link so the rest of the stage still renders.
    /// <c>check-shaders.py</c> splices the content checkout's copy for its offline compile, so the real
    /// file is what gets validated there rather than the stub.</para></summary>
    private const string Begin = "// $VANILLA ", End = "// $VANILLA-END";

    /// <summary>#350: the HOST marker. The region names a content FRAGMENT STAGE -- a material's own
    /// <c>customShaderFS</c> -- and Alloy runs that stage's pre-shading body from inside its lit program:
    /// the file is spliced with its includes, its <c>main()</c> renamed to <c>CxHostMain_&lt;resref&gt;</c>,
    /// its <c>gl_FragColor</c> write redirected into <c>FragmentColor</c>, and the two calls Alloy owns
    /// (<c>SetupStandardShaderInputs</c>, <c>ApplyStandardShader</c>) resolved to no-ops by
    /// <see cref="AlloyRenderer.VanillaStandardFs"/> standing in for <c>inc_standard</c>. What is left
    /// between those two calls is the look, and the stage's <c>MATERIAL_*_VALUE</c> terms are read back off
    /// the macros it declared. The stub between the markers is the same empty <c>CxHost_&lt;resref&gt;</c>
    /// the wrapper defines, so a missing or broken file is the plain lit path for that feature.</summary>
    private const string Host = "// $VANILLA-HOST ";

    /// <summary>#352: the SKIP marker. <c>// $VANILLA-SKIP a b c</c> names includes the stage's own environment
    /// stands in for, so the resolver never follows them -- per stage, where <see cref="Supplied"/> is every
    /// stage's. The hosted post chain is the customer: <c>inc_postpr</c> pulls <c>inc_lighting</c> and
    /// <c>inc_fog</c> under <c>LIGHTING 1</c>, which are the engine's forward pipeline, not a post term.</summary>
    private const string Skip = "// $VANILLA-SKIP ";

    /// <summary>The common prefix of all three markers -- what the region scan searches for, so a plain and
    /// a host region are found in document order by one search.</summary>
    private const string Marker = "// $VANILLA";

    /// <summary>Replace every marked region in <paramref name="src"/> with the stock text of the resref the
    /// BEGIN marker names. A miss leaves the stub in place; nothing here can fail the compile that a
    /// missing file would not already have failed. <paramref name="host"/> false leaves every HOST region
    /// at its stub while the plain regions still splice -- the middle rung of <c>GlProgram</c>'s retry.</summary>
    internal static string Splice(string src, ExtenderLog? log, string what, bool fromContent = true, bool host = true)
    {
        int at = src.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return src;
        var sb = new StringBuilder(src.Length);
        // ONE visited list for the whole compilation unit: two regions each pulling inc_scriptable would
        // otherwise declare the block twice, which is a dead stage rather than a warning.
        var seen = new List<string>();
        int cursor = 0;
        while (at >= 0)
        {
            bool hosted = string.CompareOrdinal(src, at, Host, 0, Host.Length) == 0;
            if (!hosted && string.CompareOrdinal(src, at, Begin, 0, Begin.Length) != 0)
            {
                if (string.CompareOrdinal(src, at, Skip, 0, Skip.Length) == 0)
                {
                    int skipEol = src.IndexOf('\n', at);
                    foreach (var n in src[(at + Skip.Length)..(skipEol < 0 ? src.Length : skipEol)].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        seen.Add(n);
                }
                at = src.IndexOf(Marker, at + Marker.Length, StringComparison.Ordinal);
                continue;
            }
            int nameAt = at + (hosted ? Host.Length : Begin.Length);
            int eol = src.IndexOf('\n', nameAt);
            int end = src.IndexOf(End, nameAt, StringComparison.Ordinal);
            if (eol < 0 || end < 0) break;
            string resref = src[nameAt..eol].Trim();
            string stub = src[(eol + 1)..end];
            bool ask = fromContent && _armed && ((host && _hostArmed) || !hosted);
            sb.Append(src, cursor, eol + 1 - cursor);          // keep the marker line: it names the source
            if (seen.Contains(resref))
            {
                // an earlier region already included this file: its functions exist, and the stub would
                // define them a second time
                cursor = end;
                at = src.IndexOf(Marker, end + End.Length, StringComparison.Ordinal);
                continue;
            }
            string? stock = ask ? EngineText(resref, log) : null;
            string? body = stock == null ? null
                         : hosted ? HostText(stock, resref, log, what, seen)
                         : Validate(Includes(stock, log, what, seen, 0), stub, resref, log, what);
            if (body != null)
            {
                seen.Add(resref);
                sb.Append(body);
                log?.Info($"{what}: {resref}.shd " + (hosted ? "hosted" : "spliced") + $" from the resource chain ({stock!.Length} chars)");
            }
            else
            {
                sb.Append(stub);                                // the stub, unchanged
                if (fromContent && stock == null)
                    log?.Info($"{what}: {resref}.shd " + (!_armed ? "not asked for (alloy.vanillabridge=0)" : ask ? "not in the resource chain" : !_hostArmed ? "not hosted (alloy.hostfs=0)" : "not hosted on this retry")
                            + " -- compiling the fallback stub, so whatever it carried is off");
            }
            cursor = end;
            at = src.IndexOf(Marker, end + End.Length, StringComparison.Ordinal);
        }
        sb.Append(src, cursor, src.Length - cursor);
        return sb.ToString();
    }

    /// <summary>#350 AC: the SPLICED text is validated, not just the embedded copy. Every function the stub
    /// defines is one the stage calls, so a chain copy that lacks any of them (an 8017-byte
    /// <c>inc_target.shd</c> off a stale HAK, which predates <c>GetTargetGroundHighlightStrength</c>) is
    /// caught here by name and falls back to the stub for THIS region, instead of failing the whole program
    /// and taking every other region with it.</summary>
    private static string? Validate(string text, string stub, string resref, ExtenderLog? log, string what)
    {
        foreach (Match m in StubFn.Matches(stub))
        {
            string name = m.Groups[1].Value;
            if (Regex.IsMatch(text, @"\b" + name + @"\s*\(")) continue;
            log?.Info($"{what}: {resref}.shd from the resource chain does not define {name} -- a stale copy? "
                    + "compiling the stub for this region");
            return null;
        }
        return text;
    }

    /// <summary>A function DEFINITION in stub text (name, then its parameter list, then the opening brace on
    /// the same line) -- anchored on the brace so a line-initial <c>return Helper(x);</c> in a stub body
    /// never becomes a name the content file is required to define.</summary>
    private static readonly Regex StubFn = new(@"(?m)^\s*(?:\w+\s+)+(\w+)\s*\([^)]*\)\s*\{", RegexOptions.Compiled);
    private static readonly Regex MainFn = new(@"\bvoid\s+main\s*\(\s*(?:void)?\s*\)", RegexOptions.Compiled);
    private static readonly Regex OwnDefine = new(@"(?m)^[ \t]*#[ \t]*define[ \t]+(\w+)", RegexOptions.Compiled);

    /// <summary>The names <see cref="AlloyRenderer.VanillaEnvDefs"/> supplies as macros. A hosted stage's own
    /// declaration of one (<c>td_ground_fs</c> declares <c>uniform sampler2D texUnit6;</c> because
    /// <c>inc_common</c> stops at 5) would redeclare what the macro maps it to, so those lines are dropped.</summary>
    private static readonly Regex EnvDecl = new(
        @"(?m)^[ \t]*uniform\b[^;\n]*\b(?:" + string.Join("|", OwnDefine.Matches(AlloyRenderer.VanillaEnvDefs).Select(m => m.Groups[1].Value)) + @")[ \t]*;[^\n]*\n?",
        RegexOptions.Compiled);

    /// <summary>#350: a content fragment stage, rewritten to run as a function of Alloy's lit program. Pure
    /// text-to-text (the resolver is the only outside call), so <c>SelfCheck</c> can exercise it on a
    /// synthetic stage.
    /// <para>The stage's OWN <c>#define</c>s are scoped to it -- undefined before (so <c>LIGHTING 1</c>
    /// does not collide with the environment's 0) and after (so two stages' <c>WET_DARKEN</c> do not
    /// collide with each other), then the environment's macros are re-issued. Its includes are not scoped:
    /// they carry <c>#ifndef</c> guards of their own and are followed once per compilation unit.</para>
    /// <para>ponytail: a <c>#define</c> under an <c>#if</c> in the stage is still undefined here -- the
    /// scan is textual. A stage that needs conditional macros to survive it is the first customer for a
    /// real preprocessor pass.</para></summary>
    internal static string? HostText(string stock, string resref, ExtenderLog? log, string what, List<string> seen)
    {
        string text = Includes(stock, log, what, seen, 0);
        if (!MainFn.IsMatch(text))
        {
            log?.Info($"{what}: {resref}.shd from the resource chain has no main() to host -- compiling the stub for this region");
            return null;
        }
        text = MainFn.Replace(text, "void CxHostMain_" + resref + "()", 1);
        text = Regex.Replace(text, @"\bgl_FragColor\b", "FragmentColor");
        text = EnvDecl.Replace(text, "");
        var own = OwnDefine.Matches(stock).Select(m => m.Groups[1].Value).Distinct().ToList();
        var sb = new StringBuilder(text.Length + 512);
        foreach (var n in own) sb.Append("#undef ").Append(n).Append('\n');
        sb.Append(text).Append('\n');
        sb.Append("void CxHost_").Append(resref).Append("()\n{\n")
          .Append("    CxHostMain_").Append(resref).Append("();\n")
          .Append("    fSpecularity = MATERIAL_SPECULARITY_VALUE;\n")
          .Append("    fRoughness = MATERIAL_ROUGHNESS_VALUE;\n")
          .Append("    fMetallicness = MATERIAL_METALLICNESS_VALUE;\n")
          .Append("    SpecularColor = MATERIAL_SPECULAR_COLOR_VALUE;\n}\n");
        foreach (var n in own) sb.Append("#undef ").Append(n).Append('\n');
        sb.Append(AlloyRenderer.VanillaEnvDefs);
        return sb.ToString();
    }

    /// <summary>The engine's own text for a stock <c>.shd</c>, read through the engine's own entry points.
    /// <para><b>Why not <see cref="ShaderResource.Override"/>.</b> That path is for resrefs NOBODY else
    /// reads: where no <c>CRes</c> exists it makes a bare one and registers it under the resref. The engine
    /// builds a TYPED subclass per resource type (<c>CResHelper&lt;CResSHD, 2069&gt;</c>) and reads it back
    /// through that type -- <c>CAuroraShader::LoadShader</c> @0x1126b40 calls
    /// <c>CResSHD::GetSHDDataPtr()</c> and stores a back-pointer 8 bytes BEFORE what it returns. Point that
    /// at a bare <c>CRes</c> and the store lands on a wild address: the client crashed there the first time
    /// anything else compiled a shader, ~1s after the splice (crash 1788377010, and it is a type confusion
    /// planted in the engine's key table, not a leak). Reading a resource the ENGINE owns therefore goes
    /// through the ENGINE.</para>
    /// <para>Replays <c>GLRender</c>'s shader-source loader @0x4d2940 argument for argument: the extension
    /// carries its dot, the bool is true, the size out-param is what <c>AurResGetData</c> is then asked for,
    /// and the failure path frees nothing -- because that loader frees nothing there either.</para>
    /// <para>ponytail: the <c>Resources</c> stack means this trio must run back to back. It does, on the
    /// render thread at program-build time; a loader thread opening a resource in the exact gap would
    /// mis-read, and the engine's own loader has the same exposure.</para></summary>
    private static string? EngineText(string resref, ExtenderLog? log)
    {
        AurResGetFn get = EngineFn.AurResGet;
        AurResGetDataFn data = EngineFn.AurResGetData;
        AurResFreeFn free = EngineFn.AurResFree;
        if (get == null || data == null || free == null)
        {
            log?.Info("VanillaShader: AurResGet/AurResGetData/AurResFree unresolved -- no text to splice");
            return null;
        }
        byte* name = stackalloc byte[ShaderResource.ResRefBytes + 1];
        if (resref.Length > ShaderResource.ResRefBytes) return null;
        for (int i = 0; i < resref.Length; i++) name[i] = (byte)resref[i];
        name[resref.Length] = 0;
        byte* ext = stackalloc byte[5];
        ext[0] = (byte)'.'; ext[1] = (byte)'s'; ext[2] = (byte)'h'; ext[3] = (byte)'d'; ext[4] = 0;

        int size = 0;
        IntPtr handle = get(name, ext, &size, 1);
        if (handle == IntPtr.Zero || size <= 0) return null;
        byte* bytes = data(size);
        string? text = bytes != null ? Encoding.UTF8.GetString(bytes, size) : null;
        free(handle);
        return text;
    }

    /// <summary>Does this source carry a marked region at all? The question a failed compile asks before it
    /// decides whether there is a stub to retry with.</summary>
    internal static bool HasSplice(string src) => src.Contains(Begin, StringComparison.Ordinal) || HasHost(src);

    /// <summary>Does this source host a content stage? The question the middle retry rung asks.</summary>
    internal static bool HasHost(string src) => src.Contains(Host, StringComparison.Ordinal);

    /// <summary>Resolve the file's own <c>#include</c> lines against the resource chain, once each.
    /// <para>A stock include gets its dependencies from the stage that splices it -- that is the bridge, and
    /// <c>VanillaEnvFs</c> is the whole of it. A CONTENT file is the other way round: <c>cx_lit_hooks</c>
    /// exists to compose the content's own includes, and dropping them would leave every function it calls
    /// undeclared. So an include is followed when the chain answers for it and dropped when it does not --
    /// which is also how a file that includes <c>inc_uniforms</c> (supplied by the environment, never in the
    /// chain as a resource we should splice) still works.</para>
    /// <para>Guarded by <see cref="MaxDepth"/> and a visited list: the files carry <c>#ifndef INC_x</c>
    /// guards of their own, so including one twice is already a no-op in GLSL, and this makes it a no-op in
    /// bytes too. A cycle would otherwise be an infinite loop inside a shader compile.</para></summary>
    private const int MaxDepth = 4, MaxIncludes = 64;

    /// <summary>Includes the ENVIRONMENT supplies, and which must therefore never be followed.
    /// <c>inc_common</c>/<c>inc_uniforms</c> are the engine's whole uniform and varying interface, written
    /// for its stages -- pulling them into an Alloy stage would redeclare hundreds of names against a
    /// different vertex layout. <see cref="AlloyRenderer.VanillaEnvFs"/> is the handful of them that a
    /// spliced file actually reads. Everything else (<c>inc_scriptable</c>, <c>inc_ripple</c>, a content
    /// repo's own) is followed, because that is what composition means.</summary>
    private static readonly string[] Supplied = { "inc_common", "inc_uniforms" };

    /// <summary>#350: the one include the environment SUBSTITUTES rather than drops. A content stage's
    /// <c>inc_standard</c> is the engine's whole forward pipeline; what a hosted stage needs from it is the
    /// two entry points it calls around its own body, as no-ops, and the scriptable block. Followed once
    /// like any other include.</summary>
    private const string Substituted = "inc_standard";

    private static string Includes(string text, ExtenderLog? log, string what, List<string> seen, int depth)
    {
        if (depth >= MaxDepth || !text.Contains("#include", StringComparison.Ordinal)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            string t = line.TrimStart();
            if (!t.StartsWith("#include", StringComparison.Ordinal)) { sb.Append(line).Append('\n'); continue; }
            int a = t.IndexOf('"'), b = a < 0 ? -1 : t.IndexOf('"', a + 1);
            string? name = a >= 0 && b > a ? t[(a + 1)..b] : null;
            if (name == null || seen.Contains(name)) continue;
            if (seen.Count >= MaxIncludes) { log?.Info($"{what}: include cap {MaxIncludes} reached -- {name} dropped"); continue; }
            if (Array.IndexOf(Supplied, name) >= 0) continue;
            seen.Add(name);
            string? body = name == Substituted ? AlloyRenderer.VanillaStandardFs
                         : name.Length <= ShaderResource.ResRefBytes ? EngineText(name, null) : null;
            if (body == null) continue;                       // the environment supplies it, or nothing does
            sb.Append(Includes(body, log, what, seen, depth + 1)).Append('\n');
        }
        return sb.ToString();
    }

}
