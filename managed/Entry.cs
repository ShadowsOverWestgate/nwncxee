using System.Runtime.InteropServices;
using CX.Framework;

namespace CX;

public static class Entry
{
    /// <summary>Native bootstrap calls this once on load; it just starts the host.</summary>
    [UnmanagedCallersOnly(EntryPoint = "CxInit")]
    public static void CxInit() => ServiceHost.Run();
}
