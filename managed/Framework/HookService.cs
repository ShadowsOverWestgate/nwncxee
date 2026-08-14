using System.Runtime.InteropServices;

namespace Sowx.Framework;

/// <summary>
/// Wraps funchook. A service calls <see cref="Install"/> in its Initialize; the host
/// calls <see cref="Commit"/> once, after all services are up. Pass the LINUX (Itanium)
/// mangled name (<c>./sym.sh -m &lt;name&gt;</c>); SymbolMap maps it to the running client.
/// </summary>
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

    /// <summary>
    /// Hooks <paramref name="linux"/> with <paramref name="handler"/> (a pointer to an
    /// [UnmanagedCallersOnly] static matching the target ABI). Returns the trampoline
    /// (the original) to call through, or Zero if the symbol didn't resolve on this platform.
    /// </summary>
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

    /// <summary>Installs every prepared hook at once. Idempotent.</summary>
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
