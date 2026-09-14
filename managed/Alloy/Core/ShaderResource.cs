using System;
using System.Globalization;
using System.Text;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Content-overridable GLSL (#92): the Alloy pipeline asks for each of its shaders by
/// <c>cx_*</c> resref and gets whatever a hak, <c>override/</c> or NWSync supplies -- or null, which
/// means the caller builds from its embedded default. Cold by construction: a program builds once per
/// session, so the string this allocates never lands in a frame.</summary>
internal static unsafe class ShaderResource
{
    /// <summary>Resource type of a .shd, read off the client's own <c>CResHelper&lt;CResSHD, 2069&gt;</c>.</summary>
    private const ushort ResTypeShd = 2069;
    /// <summary>sizeof(CRes): what <c>CExoResMan::Get</c> passes to operator new before <c>CRes::CRes</c>.</summary>
    private static readonly int SizeOfCRes = EngineLayout.SizeOfCRes;
    /// <summary>CRes::GetData / CRes::GetSize field offsets. A serviced .shd is the raw file bytes --
    /// <c>CResSHD::OnResourceServiced</c> only mirrors these two into its own pair.</summary>
    private static readonly int CResData = EngineLayout.CResData, CResSize = EngineLayout.CResSize;
    /// <summary>A CResRef is 16 bytes of null-padded ASCII; longer names cannot address a resource.</summary>
    internal const int ResRefBytes = 16;

    private static readonly IntPtr _resManSym = Native.Sym(SymbolMap.ExoResMan);
    private static readonly delegate* unmanaged<IntPtr, byte*, ushort, uint*, int> _exists =
        (delegate* unmanaged<IntPtr, byte*, ushort, uint*, int>)Native.Sym(SymbolMap.ResManExists);
    private static readonly delegate* unmanaged<IntPtr, byte*, ushort, IntPtr> _getResObject =
        (delegate* unmanaged<IntPtr, byte*, ushort, IntPtr>)Native.Sym(SymbolMap.ResManGetResObject);
    private static readonly delegate* unmanaged<IntPtr, byte*, ushort, IntPtr, void> _setResObject =
        (delegate* unmanaged<IntPtr, byte*, ushort, IntPtr, void>)Native.Sym(SymbolMap.ResManSetResObject);
    private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr> _demand =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)Native.Sym(SymbolMap.ResManDemand);
    private static readonly delegate* unmanaged<IntPtr, IntPtr, ulong> _release =
        (delegate* unmanaged<IntPtr, IntPtr, ulong>)Native.Sym(SymbolMap.ResManRelease);
    private static readonly delegate* unmanaged<IntPtr, IntPtr, byte, int> _releaseObj =
        (delegate* unmanaged<IntPtr, IntPtr, byte, int>)Native.Sym(SymbolMap.ResManReleaseObj);
    private static readonly delegate* unmanaged<IntPtr, void> _cresCtor =
        (delegate* unmanaged<IntPtr, void>)Native.Sym(SymbolMap.CResCtor);
    private static readonly delegate* unmanaged<IntPtr, IntPtr> _operatorNew =
        (delegate* unmanaged<IntPtr, IntPtr>)Native.Sym(SymbolMap.OperatorNew);

    /// <summary>The GLSL a content author supplied for <paramref name="resref"/>, or null when nothing in
    /// the resource chain provides it (the common case: everyone runs the embedded defaults).
    /// <para><b>OUR resrefs only</b> -- <c>cx_*</c>, which nothing else in the client ever
    /// looks up. Where no <c>CRes</c> exists yet this makes a BARE one and registers it, and the engine
    /// reads a <c>.shd</c> back through <c>CResHelper&lt;CResSHD, 2069&gt;</c>, casting whatever the key
    /// table holds. Point this at a resref the engine also loads and its next shader compile stores through
    /// <c>CResSHD::GetSHDDataPtr()</c> on an object that is not one, and the client dies (#346, live).
    /// A stock file goes through <see cref="VanillaShader"/>, which asks the ENGINE for it.</para></summary>
    internal static string? Override(string resref, ExtenderLog? log)
    {
        byte[]? b = Blob(resref, log);
        if (b == null) return null;
        if (IsSpirv(b))
        {
            log?.Info($"shader override: {resref}.shd is a SPIR-V module -- "
                    + (SpirvArmed ? "taken as a binary stage" : "IGNORED, set alloy.spirv=1 in cx.cfg to accept it"));
            return null;
        }
        // A BOM ahead of #version is a compile error, and an editor on Windows writes one without asking.
        string text = Encoding.UTF8.GetString(b).TrimStart('\uFEFF');
        log?.Info($"shader override: {resref}.shd from resman ({b.Length} bytes)");
        return text;
    }

    /// <summary>The SPIR-V module a content author supplied for <paramref name="resref"/>, or null when the
    /// resource is absent, is GLSL text, or the <c>spirv</c> capability gate is off.
    /// <para>A binary stage is opted into rather than accepted: none of the text machinery a GLSL override
    /// goes through -- <see cref="Tokens"/>, <see cref="VanillaShader.Splice"/>, the
    /// <see cref="ShaderContract"/> audits -- can read a module, so accepting one is trusting the author to
    /// have matched the embedded stage's record layout, bindings and cutout rules.</para></summary>
    internal static byte[]? OverrideSpirv(string resref, ExtenderLog? log)
    {
        if (!SpirvArmed) return null;
        byte[]? b = Blob(resref, log);
        if (b == null || !IsSpirv(b)) return null;
        if ((b.Length & 3) != 0)
        {
            log?.Info($"shader override: {resref}.shd is {b.Length} bytes -- a SPIR-V module is a whole number of words, ignored");
            return null;
        }
        return b;
    }

    /// <summary>SPIR-V's magic word, little-endian. A module that reads byte-swapped was produced for the
    /// other endianness and GL will not take it, so it is refused rather than converted.</summary>
    private const uint SpirvMagic = 0x07230203;   // not-an-offset: the SPIR-V file magic

    private static bool IsSpirv(byte[] b) =>
        b.Length >= 4 && (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24)) == SpirvMagic;

    /// <summary>Restart gate: binary shader stages are off unless the machine's owner says otherwise, because
    /// nothing downstream can audit one.</summary>
    internal static bool SpirvArmed => Renderer.Spirv.Boot;

    /// <summary>The raw bytes behind <paramref name="resref"/>.shd, or null when nothing supplies it.</summary>
    private static byte[]? Blob(string resref, ExtenderLog? log)
    {
        if (_resManSym == IntPtr.Zero || _exists == null || _getResObject == null || _setResObject == null
            || _demand == null || _release == null || _releaseObj == null || _cresCtor == null
            || _operatorNew == null || resref.Length > ResRefBytes) return null;
        IntPtr resMan = *(IntPtr*)_resManSym;
        if (resMan == IntPtr.Zero) return null;

        byte* rr = stackalloc byte[ResRefBytes];
        for (int i = 0; i < ResRefBytes; i++) rr[i] = i < resref.Length ? (byte)resref[i] : (byte)0;
        if (_exists(resMan, rr, ResTypeShd, null) == 0) return null;   // size out-param is null-checked there

        IntPtr res = _getResObject(resMan, rr, ResTypeShd);
        if (res == IntPtr.Zero)
        {
            // First ask for this resource: no CRes exists yet, so make one exactly as CResHelper does.
            res = _operatorNew((IntPtr)SizeOfCRes);
            if (res == IntPtr.Zero) return null;
            _cresCtor(res);
            _setResObject(resMan, rr, ResTypeShd, res);   // seeds the refcount at 1, like GetResObject's bump
        }
        _demand(resMan, res);

        byte* data = *(byte**)(res + CResData);
        int size = *(int*)(res + CResSize);
        byte[]? bytes = null;
        if (data != null && size > 0) { bytes = new byte[size]; new Span<byte>(data, size).CopyTo(bytes); }

        _release(resMan, res);
        // Second arg is ReleaseResObject's "do not queue for freeing"; CResHelper::ClearResHelper passes
        // exactly this predicate -- GetData() == 0, i.e. nothing was ever serviced, so there is nothing to free.
        _releaseObj(resMan, res, data == null ? (byte)1 : (byte)0);

        return bytes;
    }

    /// <summary>Expand the sizing tokens an Alloy pipeline source may carry, so a shipped .shd tracks the
    /// CPU-side constants rather than freezing a copy of them: <c>$CAP$</c> lights per cluster (the CPU sizes
    /// the index SSBO from it), <c>$SORT$</c> the cluster build sort array (#410), <c>$GROUP$</c> compute workgroup size, <c>$SRGB$</c> the engine's sRGB
    /// exponent, <c>$AA$</c> the SMAA preset level the anti-aliasing option picked, <c>$POSTMASK$</c>/<c>$SQM$</c>
    /// the hosted post chain's option mask and shader quality (#352), pinned by its build. <see cref="GlProgram"/>
    /// applies it to every stage it compiles, embedded or overridden, so both read the same.</summary>
    internal static string Tokens(string src) => src
        .Replace("$CAP$", AlloyLights.ClusterCap.ToString())
        .Replace("$SORT$", AlloyLights.SortCap.ToString())
        .Replace("$GROUP$", AlloyLights.GroupSize.ToString())
        .Replace("$SRGB$", AlloyLights.SrgbGamma.ToString("0.0#", CultureInfo.InvariantCulture))
        .Replace("$AA$", SmaaStage.Level.ToString())
        .Replace("$POSTMASK$", FbPostStage.BuildMask.ToString())
        .Replace("$SQM$", FbPostStage.BuildQuality.ToString());
}
