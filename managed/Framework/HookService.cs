using System.Runtime.InteropServices;

namespace Sowx.Framework;

// Wraps funchook. A service calls Install("<linux mangled symbol>", handler) in its
// Initialize(); the host calls Commit() once, after all services are up.
//
// Pass the LINUX (Itanium) mangled name -- `./sym.sh -m <name>`. SymbolMap turns it
// into the right target for the running client: on Linux the same name (the ELF
// exports everything), on Windows the exported MSVC name or a build-locked RVA, or a
// logged skip when the client doesn't expose it. `handler` must point to an
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
    // matching delegate* to call through. IntPtr.Zero means the hook was not set
    // (symbol unresolved on this platform -- SymbolMap already logged why).
    public IntPtr Install(string linux, IntPtr handler)
    {
        IntPtr target = SymbolMap.Resolve(linux, _log);
        if (target == IntPtr.Zero) return IntPtr.Zero;

        int rc = funchook_prepare(_fh, ref target, handler); // target -> trampoline on success
        if (rc != 0)
        {
            _log.Info($"hook: prepare failed (rc={rc}): {linux}");
            return IntPtr.Zero;
        }

        _log.Info($"hook: prepared {linux}");
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
