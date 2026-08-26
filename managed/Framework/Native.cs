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

    private static readonly IntPtr _exoBaseSym = Sym(SymbolMap.ExoBase);
    private static string? _userDir;

    /// <summary>Seed <see cref="UserDirectory"/>'s cache, or null to clear it. Only for
    /// <see cref="ConfigFile.SelfCheck"/>'s "unresolved, then resolved" sequence, which cannot otherwise
    /// happen in one headless run. Note it can only make the dir RESOLVE: passing null clears the cache, and
    /// the next call re-reads <c>g_pExoBase</c>. The unresolved half of that sequence is real, not forced.</summary>
    internal static void ForceUserDirectory(string? dir) => _userDir = dir;

    /// <summary>Offset of the user-directory CExoString within CExoBase. Verified identical on the Linux
    /// and Windows builds (CExoBase::CExoBase stores the ctor arg via InitFromCharArray at this+0x38).</summary>
    private const int ExoBaseUserDirOffset = 0x38;

    /// <summary>
    /// The client's own resolved user directory (e.g. <c>~/.local/share/Neverwinter Nights</c>) --
    /// whatever <c>SetUpUserDirectory</c> produced, so it honours <c>-userdirectory</c>, per-OS rules and
    /// the <c>.N</c> instance suffix for free. Read live from <c>g_pExoBase -> CExoString@0x38</c>; null
    /// before the engine sets that up (our loader runs pre-main) or headless (run-demo has no g_pExoBase).
    /// Cached on first success; the engine char* is copied out immediately, never held (cf. #33).
    /// </summary>
    public static string? UserDirectory()
    {
        if (_userDir != null) return _userDir;
        if (_exoBaseSym == IntPtr.Zero) return null;
        IntPtr exoBase = *(IntPtr*)_exoBaseSym;             // g_pExoBase's value: the CExoBase*
        if (exoBase == IntPtr.Zero) return null;            // not constructed yet (pre-main)
        byte* p = *(byte**)(exoBase + ExoBaseUserDirOffset);
        string s = p == null ? "" : Marshal.PtrToStringUTF8((IntPtr)p) ?? "";
        if (s.Length == 0) return null;
        _userDir = s;
        return s;
    }

    /// <summary>
    /// Absolute path for a sowx config file in the client's user directory (next to nwn.ini/settings.tml).
    /// Falls back to <see cref="DefaultUserDirectory"/> when the engine dir is unresolvable -- pre-main, or
    /// headless under run-demo.
    /// </summary>
    public static string ConfigPath(string fileName) =>
        System.IO.Path.Combine(UserDirectory() ?? DefaultUserDirectory(), fileName);

    /// <summary>
    /// Where the client would put its user directory with no <c>-userdirectory</c> override: SDL's pref path,
    /// <c>$XDG_DATA_HOME</c> or <c>~/.local/share</c>, plus the app name. Read off the client binary's own SDL
    /// block (<c>/.local/share/</c>, <c>XDG_DATA_HOME</c>, "neither XDG_DATA_HOME nor HOME environment is set")
    /// rather than assumed, so it agrees with <see cref="UserDirectory"/> in the ordinary case.
    /// </summary>
    /// <remarks>
    /// This is the pre-main answer, and boot config (ADR 0013 bucket 2) is read through it -- before the
    /// engine exists there is nothing else to ask. ponytail: a client launched with <c>-userdirectory</c>
    /// keeps its per-feature cfgs there (those load after the engine answers) while boot settings would be
    /// read from here; parsing the launch line or nwn.ini is the upgrade path if anyone actually runs one.
    /// </remarks>
    public static string DefaultUserDirectory()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string data = string.IsNullOrEmpty(xdg)
            ? System.IO.Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/tmp", ".local", "share")
            : xdg;
        return System.IO.Path.Combine(data, "Neverwinter Nights");
    }
}
