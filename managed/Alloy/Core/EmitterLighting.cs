using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// What the scene's lights make of one world point -- the particle tint. CX-108 made this ALLOY's arithmetic
/// (<see cref="LightFade.Accumulate"/>), because the engine's <c>LightManager::GetEmitterLighting</c>
/// @0x4c1970 reads the manager's active list at <c>+0x60</c> and both area lights, and Alloy reads no
/// <c>LightManager</c> field.
/// <para>The engine call is kept behind <c>light.registry 0</c>, and with it the one piece of ABI in Alloy
/// that no declared signature can describe -- so the A/B also keeps that ABI exercised rather than rotting.
/// The <c>lightManager</c> and <c>lights</c> parameters are the engine call's; on the owned path the first is
/// ignored and the second (an exclusion list, always null at the one live call site) would be an exclusion by
/// gob.</para>
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

    /// <summary>Raw, and it stays raw (CX-39): this ONE symbol has two signatures -- the by-value
    /// <c>Vector</c> return puts the hidden buffer in a different argument slot per ABI -- so no single
    /// declared signature describes it.</summary>
    private static IntPtr _fn;

    /// <summary>Resolved on this client? Callers gate on this exactly as they gated on their own pointer. True
    /// while the registry is the source: the arithmetic is Alloy's and needs no symbol.</summary>
    internal static bool Ready => LightRegistry.Live || _fn != IntPtr.Zero;

    internal static void Resolve() => _fn = Native.Sym(SymbolMap.GetEmitterLighting);

    /// <summary>The scene lighting at <paramref name="pos"/>. <paramref name="lightManager"/> and
    /// <paramref name="lights"/> are the engine call's arguments and are used only on the
    /// <c>light.registry 0</c> path; <paramref name="lights"/> may be null there -- the engine reads that as an
    /// empty exclusion list.</summary>
    internal static V3 At(V3 pos, IntPtr lightManager, IntPtr lights)
    {
        if (LightRegistry.Live)
        {
            System.Numerics.Vector3 c = LightFade.Accumulate(new System.Numerics.Vector3(pos.X, pos.Y, pos.Z), IntPtr.Zero);
            return new V3 { X = c.X, Y = c.Y, Z = c.Z };
        }
        if (_fn == IntPtr.Zero) return new V3 { X = 1f, Y = 1f, Z = 1f };
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
