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
