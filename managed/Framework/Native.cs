using System.Runtime.InteropServices;
using System.Text;

namespace Sowx.Framework;

// Thin bridge to the client's own functions and data. The binary is unstripped
// and (near-)fully dynamically exported, so we resolve engine symbols by mangled
// name -- the same mechanism HookService uses for hook targets -- and either call
// them through a matching delegate* or read globals through them.
//   ./sym.sh -m <name>   gives the mangled symbol to pass here.
public static unsafe class Native
{
    private static readonly IntPtr Main = NativeLibrary.GetMainProgramHandle();

    // Address of a mangled symbol (function or data), or Zero if absent.
    public static IntPtr Sym(string mangled) =>
        NativeLibrary.TryGetExport(Main, mangled, out IntPtr p) ? p : IntPtr.Zero;

    // A client global that holds a pointer (e.g. g_pAppManager): returns *symbol.
    public static IntPtr PtrGlobal(string mangled)
    {
        IntPtr a = Sym(mangled);
        return a == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)a;
    }

    private static readonly delegate* unmanaged<IntPtr, byte*, uint, void> _exoInit =
        (delegate* unmanaged<IntPtr, byte*, uint, void>)Sym("_ZN10CExoString17InitFromCharArrayEPKcj");
    private static readonly delegate* unmanaged<IntPtr, void> _exoDtor =
        (delegate* unmanaged<IntPtr, void>)Sym("_ZN10CExoStringD1Ev");

    // Build a CExoString on the native heap from a C# string. The layout is opaque
    // to us: we zero an over-sized buffer (so the client sees a null internal
    // pointer) and let its own InitFromCharArray populate it. Caller MUST FreeExoString.
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
