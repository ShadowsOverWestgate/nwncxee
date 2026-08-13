using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx;

public static class Entry
{
    // The native bootstrap calls this once, on load. All it does is start the
    // service host -- everything else lives in services.
    [UnmanagedCallersOnly(EntryPoint = "SowxInit")]
    public static void SowxInit() => ServiceHost.Run();
}
