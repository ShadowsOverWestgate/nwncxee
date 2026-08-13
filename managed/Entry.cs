using System.Runtime.InteropServices;

namespace Sowx;

public static class Entry
{
    // The native bootstrap dlsym's this exact name and calls it once, on load.
    // A [UnmanagedCallersOnly] static IS a plain C function pointer -- which is
    // also exactly what a hooking library (funchook) installs as a hook target.
    // So proving this runs proves the whole C#-in-the-client path.
    [UnmanagedCallersOnly(EntryPoint = "SowxInit")]
    public static void SowxInit()
    {
        string host;
        try { host = File.ReadAllText("/proc/self/comm").Trim(); }
        catch { host = "unknown"; }

        string msg = $"[sowx] C# NativeAOT is running INSIDE host '{host}' (pid {Environment.ProcessId})";
        Console.Error.WriteLine(msg);
        try { File.AppendAllText("/tmp/sowx.log", $"{DateTime.Now:O} {msg}\n"); }
        catch { /* proto: log is best-effort */ }
    }
}
