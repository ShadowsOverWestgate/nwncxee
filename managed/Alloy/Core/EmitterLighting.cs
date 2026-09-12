using System;
using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx.Alloy;

/// <summary>
/// <c>LightManager::GetEmitterLighting</c>, and the ABI it needs on each platform. Both callers -- the
/// <see cref="RspShim"/> focus-lighting item and the particle tint -- come through here: two declarations of
/// one contract is how the second copy stays Linux-shaped after someone fixes the first (#456).
/// </summary>
/// <remarks>
/// <para>This is the one engine call Alloy makes that returns a class BY VALUE, so its divergence is not an
/// offset but the whole argument list. A hidden return buffer sits in a DIFFERENT position on the two
/// platforms -- Itanium passes <c>(sret, this, ...)</c>, Win64 passes <c>(this, sret, ...)</c> -- and Win64
/// additionally passes the by-value <c>Vector</c> argument BY POINTER where SysV classifies it into
/// xmm0/xmm1.</para>
/// <para>Measured on both builds rather than derived. Linux <c>@0x4c1970</c> opens <c>mov r13,rdi</c> +
/// <c>mov eax,[rdi+0x68]</c> (this in rdi) with the vector arriving as <c>movq [rbp-0x70],xmm0</c> +
/// <c>movss [rbp-0x68],xmm1</c> and no return buffer at all. Windows <c>rva 0x7ee50</c> opens
/// <c>mov rbp,rcx</c> + <c>cmp [rcx+0x68],r15d</c> (this in rcx) and <c>mov [rdx],0</c> + <c>mov [rdx+8],0</c>
/// zeroing the buffer in rdx. Declaring Linux's order on Windows hands the engine our own stack frame as
/// <c>this</c>, which is the #456 crash.</para>
/// </remarks>
internal static unsafe class EmitterLighting
{
    /// <summary>A three-float value classified the way SysV classifies it -- xmm0 carries x and y, xmm1
    /// carries z. <c>Vector3</c>'s ABI is the runtime's to choose, so the layout is declared here instead.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct V3 { public float X, Y, Z; }

    private static IntPtr _fn;

    /// <summary>Resolved on this client? Callers gate on this exactly as they gated on their own pointer.</summary>
    internal static bool Ready => _fn != IntPtr.Zero;

    internal static void Resolve() => _fn = Native.Sym(SymbolMap.GetEmitterLighting);

    /// <summary>What <paramref name="lightManager"/> makes of the light at <paramref name="pos"/>, given a
    /// collected <c>List&lt;PartLight*&gt;</c>. <paramref name="lights"/> may be null -- the engine reads
    /// that as an empty list.</summary>
    internal static V3 At(V3 pos, IntPtr lightManager, IntPtr lights)
    {
        // NativeAOT folds IsWindows() to a constant, so this is not a branch on either platform.
        if (OperatingSystem.IsWindows())
        {
            V3 r = default;
            ((delegate* unmanaged<IntPtr, V3*, V3*, IntPtr, void>)_fn)(lightManager, &r, &pos, lights);
            return r;
        }
        return ((delegate* unmanaged<V3, IntPtr, IntPtr, V3>)_fn)(pos, lightManager, lights);
    }
}
