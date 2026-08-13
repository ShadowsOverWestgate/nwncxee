using System.Runtime.InteropServices;

namespace Sowx.Framework;

// Wraps funchook. A service calls Install("<mangled symbol>", handler) in its
// Initialize(); the host calls Commit() once, after all services are up.
//
// The client is unstripped and exports its symbols dynamically, so we resolve
// targets by name -- no Ghidra offsets. Use `./sym.sh -m <name>` to get the
// mangled symbol to pass here. `handler` must be a pointer to an
// [UnmanagedCallersOnly] static whose signature matches the target's ABI.
public sealed class HookService : IDisposable
{
    private const string Lib = "funchook";

    [DllImport(Lib)] private static extern IntPtr funchook_create();
    [DllImport(Lib)] private static extern int funchook_prepare(IntPtr fh, ref IntPtr target, IntPtr hook);
    [DllImport(Lib)] private static extern int funchook_install(IntPtr fh, int flags);
    [DllImport(Lib)] private static extern int funchook_uninstall(IntPtr fh, int flags);
    [DllImport(Lib)] private static extern int funchook_destroy(IntPtr fh);

    private readonly ExtenderLog _log;
    private readonly IntPtr _fh;
    private bool _committed;

    public HookService(ExtenderLog log)
    {
        _log = log;
        _fh = funchook_create();
    }

    // Platform-aware overload. Same function, two mangling schemes: `linux` is the
    // Itanium name (./sym.sh -m), `windows` the MSVC name (objdump -x nwmain.exe).
    // A null `windows` means the function is not name-exported on Windows -- Beamdog's
    // PE exports are opt-in and omit the client-only GUI/message classes (CNWCMessage,
    // CGuiInGame*, COptionsPanel, CClientExoApp GUI). Those hooks skip with a log line
    // until an address (RVA/pattern) path is added; the extender is optional by design,
    // so a logged no-op on Windows is acceptable degradation, not a crash.
    public IntPtr Install(string linux, string? windows, IntPtr handler)
    {
        string? sym = OperatingSystem.IsWindows() ? windows : linux;
        if (sym is null)
        {
            _log.Info($"hook: no windows symbol for {linux}; skipping (not name-exported)");
            return IntPtr.Zero;
        }
        return Install(sym, handler);
    }

    // Returns a pointer to the trampoline (the original function) -- cast it to a
    // matching delegate* to call through. IntPtr.Zero means the hook was not set.
    public IntPtr Install(string mangledSymbol, IntPtr handler)
    {
        IntPtr main = NativeLibrary.GetMainProgramHandle();
        if (!NativeLibrary.TryGetExport(main, mangledSymbol, out IntPtr target))
        {
            _log.Info($"hook: symbol not found: {mangledSymbol}");
            return IntPtr.Zero;
        }

        int rc = funchook_prepare(_fh, ref target, handler); // target -> trampoline on success
        if (rc != 0)
        {
            _log.Info($"hook: prepare failed (rc={rc}): {mangledSymbol}");
            return IntPtr.Zero;
        }

        _log.Info($"hook: prepared {mangledSymbol}");
        return target;
    }

    public void Commit()
    {
        if (_committed) return;
        _committed = true;
        int rc = funchook_install(_fh, 0);
        _log.Info($"hook: install rc={rc}");
    }

    public void Dispose()
    {
        funchook_uninstall(_fh, 0);
        funchook_destroy(_fh);
    }
}
