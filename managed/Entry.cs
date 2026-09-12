using System.Runtime.InteropServices;
using Sowx.Framework;

namespace Sowx;

public static class Entry
{
    /// <summary>Native bootstrap calls this once on load; it just starts the host.</summary>
    [UnmanagedCallersOnly(EntryPoint = "SowxInit")]
    public static void SowxInit() => ServiceHost.Run();
}
