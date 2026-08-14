using System.Runtime.InteropServices;
using System.Text;

namespace Sowx.Framework;

/// <summary>
/// Thin bridge to the client's own functions and data. The binary is unstripped and
/// (near-)fully exported, so we resolve engine symbols by mangled name and either call
/// them through a matching delegate* or read globals through them.
/// </summary>
public static unsafe class Native
{
    /// <summary>
    /// Address of a symbol (function or data), or Zero if absent. Pass the LINUX mangled
    /// name; SymbolMap maps it to the running client. Unresolved -> Zero, so callers that
    /// null-check degrade gracefully on a client that doesn't expose the symbol.
    /// </summary>
    public static IntPtr Sym(string linux) => SymbolMap.Resolve(linux, null);

    private static readonly delegate* unmanaged<IntPtr, byte*, uint, void> _exoInit =
        (delegate* unmanaged<IntPtr, byte*, uint, void>)Sym(SymbolMap.ExoStringInit);
    private static readonly delegate* unmanaged<IntPtr, void> _exoDtor =
        (delegate* unmanaged<IntPtr, void>)Sym(SymbolMap.ExoStringDtor);

    /// <summary>
    /// Build a CExoString on the native heap from a C# string. The layout is opaque: we
    /// zero an over-sized buffer and let the client's own InitFromCharArray populate it.
    /// Caller MUST <see cref="FreeExoString"/>.
    /// </summary>
    public static IntPtr NewExoString(string s)
    {
        IntPtr exo = Marshal.AllocHGlobal(32);              // > sizeof(CExoString); zeroed below
        for (int i = 0; i < 32; i++) ((byte*)exo)[i] = 0;
        if (_exoInit == null) return exo;

        byte[] utf8 = Encoding.UTF8.GetBytes(s);
        IntPtr c = Marshal.AllocHGlobal(utf8.Length + 1);   // temp null-terminated C string
        Marshal.Copy(utf8, 0, c, utf8.Length);
        ((byte*)c)[utf8.Length] = 0;
        _exoInit(exo, (byte*)c, (uint)utf8.Length);         // copies into its own buffer
        Marshal.FreeHGlobal(c);
        return exo;
    }

    public static void FreeExoString(IntPtr exo)
    {
        if (exo == IntPtr.Zero) return;
        if (_exoDtor != null) _exoDtor(exo);                // frees the internal buffer
        Marshal.FreeHGlobal(exo);
    }
}
