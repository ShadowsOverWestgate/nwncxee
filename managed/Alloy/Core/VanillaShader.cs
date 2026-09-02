using System;
using System.Collections.Generic;
using System.Text;
using Sowx.Framework;

namespace Sowx.Alloy;

/// <summary>#346: the VANILLA BRIDGE. An Alloy stage can name a stock <c>.shd</c> include and get the
/// engine's own text spliced into it at compile time, so shading the game already expresses -- and that a
/// content repo has already customised -- is not re-implemented here.
/// <para><b>What this is for.</b> Most of what <c>sow-topdata</c> changes about the look is fragment-side
/// arithmetic that is a pure function of the fragment's colour, its world position and the engine's global
/// shader constants: the targeting shape, racial vision, the noir desaturation. None of it has an opinion
/// about how the fragment was shaded, so none of it needs Alloy's SSBO records, its bindless handles or its
/// clustered lights -- it needs a <c>vec3</c> and a world position. Those files are the ones that drift when
/// the extender carries a second copy, and they are exactly the ones this can splice.</para>
/// <para><b>What it is NOT for.</b> A stock stage with a <c>main()</c>: those read per-draw uniforms
/// (<c>MAX_NUM_LIGHTS</c> light arrays, <c>texUnit0..7</c>, the matrix stack) which is the submission model
/// #327 spent the frame budget getting rid of, and a stock VERTEX stage computes <c>gl_Position</c> its own
/// way, which #119's invariance constraint forbids. The bridge carries FUNCTIONS, not stages.</para>
/// <para><b>Why source splice and not a linked shader object.</b> Both work -- a 330-core object links into
/// a 460-core program on Mesa 26.1 and NVIDIA 610.57 alike (measured offline, EGL, RTX 3070) -- but the
/// engine compiles an include as a standalone unit only with ITS includes dragged in, and their
/// declarations (<c>in</c> varyings with no matching Alloy output, uniform blocks) collide at link time.
/// Splicing text lets Alloy supply the environment instead: see <c>TargetFs</c>'s compatibility prelude,
/// which is the whole of what <c>inc_target.shd</c> wants from <c>inc_uniforms</c> and
/// <c>inc_scriptable</c>.</para>
/// <para>Cold: a program builds once per session.</para></summary>
internal static unsafe class VanillaShader
{
    /// <summary>#346: the arming decision (ADR 0013 bucket 2 -- a capability gate, not a look), read once.
    /// <c>vanillabridge=0</c> in <c>sowx.cfg</c> compiles every stage from its fallback stubs and asks the
    /// engine for nothing -- which turns off every feature the bridge carries, and nothing else. It exists because this path took the client down once already: reading a
    /// resource the engine owns is engine surgery, and a kill switch that needs no rebuild is what a
    /// player-facing failure wants.</summary>
    private static readonly bool _armed = Settings.Bool("vanillabridge", true);

    /// <summary>The marker pair. Everything between them is REPLACED by the engine's copy of the named
    /// resref when there is one; what a stage writes there is a STUB -- the feature off -- never a copy of
    /// the file. A copy would be exactly the second implementation this bridge deletes, and it would run
    /// only on the path where the splice failed, where nobody would ever see it drift.
    /// <para>The stub's job is the PROGRAM: it has to link so the rest of the stage still renders.
    /// <c>check-shaders.py</c> splices the content checkout's copy for its offline compile, so the real
    /// file is what gets validated there rather than the stub.</para></summary>
    private const string Begin = "// $VANILLA ", End = "// $VANILLA-END";

    /// <summary>Replace every marked region in <paramref name="src"/> with the stock text of the resref the
    /// BEGIN marker names. A miss leaves the snapshot in place; nothing here can fail the compile that a
    /// missing file would not already have failed.</summary>
    internal static string Splice(string src, ExtenderLog? log, string what, bool fromContent = true)
    {
        int at = src.IndexOf(Begin, StringComparison.Ordinal);
        if (at < 0) return src;
        var sb = new StringBuilder(src.Length);
        int cursor = 0;
        while (at >= 0)
        {
            int nameAt = at + Begin.Length;
            int eol = src.IndexOf('\n', nameAt);
            int end = src.IndexOf(End, nameAt, StringComparison.Ordinal);
            if (eol < 0 || end < 0) break;
            string resref = src[nameAt..eol].Trim();
            string? stock = fromContent && _armed ? EngineText(resref, log) : null;
            sb.Append(src, cursor, eol + 1 - cursor);          // keep the BEGIN line: it names the source
            if (stock != null)
            {
                sb.Append(Includes(stock, log, what, new List<string>(), 0));
                log?.Info($"{what}: {resref}.shd spliced from the resource chain ({stock.Length} chars)");
            }
            else
            {
                sb.Append(src, eol + 1, end - eol - 1);        // the snapshot, unchanged
                if (fromContent)
                    log?.Info($"{what}: {resref}.shd " + (_armed ? "not in the resource chain" : "not asked for (vanillabridge=0)")
                            + " -- compiling the fallback stub, so whatever it carried is off");
            }
            cursor = end;
            at = src.IndexOf(Begin, cursor, StringComparison.Ordinal);
        }
        sb.Append(src, cursor, src.Length - cursor);
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
        var get = (delegate* unmanaged<byte*, byte*, int*, byte, IntPtr>)Native.Sym(SymbolMap.AurResGet);
        var data = (delegate* unmanaged<int, byte*>)Native.Sym(SymbolMap.AurResGetData);
        var free = (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.AurResFree);
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
    internal static bool HasSplice(string src) => src.Contains(Begin, StringComparison.Ordinal);

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
    private const int MaxDepth = 4, MaxIncludes = 32;

    /// <summary>Includes the ENVIRONMENT supplies, and which must therefore never be followed.
    /// <c>inc_common</c>/<c>inc_uniforms</c> are the engine's whole uniform and varying interface, written
    /// for its stages -- pulling them into an Alloy stage would redeclare hundreds of names against a
    /// different vertex layout. <see cref="AlloyRenderer.VanillaEnvFs"/> is the handful of them that a
    /// spliced file actually reads. Everything else (<c>inc_scriptable</c>, <c>inc_ripple</c>, a content
    /// repo's own) is followed, because that is what composition means.</summary>
    private static readonly string[] Supplied = { "inc_common", "inc_uniforms" };

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
            if (name == null || seen.Count >= MaxIncludes || seen.Contains(name)) continue;
            if (Array.IndexOf(Supplied, name) >= 0) continue;
            seen.Add(name);
            string? body = name.Length <= ShaderResource.ResRefBytes ? EngineText(name, null) : null;
            if (body == null) continue;                       // the environment supplies it, or nothing does
            sb.Append(Includes(body, log, what, seen, depth + 1)).Append('\n');
        }
        return sb.ToString();
    }

}
