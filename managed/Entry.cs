using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx;

public static class Entry
{
    /// <summary>Native bootstrap calls this once on load; it just starts the host.</summary>
    [UnmanagedCallersOnly(EntryPoint = "SowxInit")]
    public static void SowxInit()
    {
        ResolveNativeDepsBesideUs();
        ServiceHost.Run();
    }

    /// <summary>
    /// Resolve <c>libfunchook.so</c> by absolute path, next to sowx_managed.so.
    /// Inside the Steam Linux Runtime container LD_LIBRARY_PATH is reset, so a
    /// bare-soname DllImport would not find it; SOWX_MANAGED gives us the dir.
    /// </summary>
    private static void ResolveNativeDepsBesideUs()
    {
        var managed = Environment.GetEnvironmentVariable("SOWX_MANAGED");
        var dir = string.IsNullOrEmpty(managed) ? null : Path.GetDirectoryName(managed);
        if (string.IsNullOrEmpty(dir)) return;

        NativeLibrary.SetDllImportResolver(typeof(HookService).Assembly, (name, _, _) =>
            name == "funchook" && NativeLibrary.TryLoad(Path.Combine(dir, "libfunchook.so"), out var h)
                ? h
                : IntPtr.Zero);
    }
}
