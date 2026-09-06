using System;
using System.Runtime.InteropServices;

namespace Sowx.Framework;

/// <summary>The main-thread pin (<c>pin=N</c> in sowx.cfg, -1 = off). The scheduler trace showed the kernel's
/// <c>migration/N</c> threads taking the core from the game's main thread ~90 times in 8s -- the load balancer
/// moving it between cores, each move a cold cache and the doubled cycle count the hitch rows carry. A pinned
/// thread cannot be migrated. Pinned at the FIRST FRAME, not at load: a thread inherits its creator's mask,
/// and the engine spawns its own threads between load and the first present.
/// <para>ponytail: an engine thread spawned after the pin (a loader) inherits it and shares the core; the thread
/// table's last-cpu column shows that if it happens. Ceiling: pin from a hook on pthread_create instead.</para></summary>
public static unsafe class Affinity
{
    [DllImport("libc.so.6")] private static extern int sched_setaffinity(int pid, nuint size, byte* mask);
    [DllImport("libc.so.6")] private static extern int sched_getcpu();
    private const int MaskBytes = 128;   // 1024 CPUs, glibc's cpu_set_t

    /// <summary>Pin the calling thread to one CPU. False if the kernel refused.</summary>
    public static bool PinThis(int cpu)
    {
        if (cpu < 0 || cpu >= MaskBytes * 8) return false;
        byte* m = stackalloc byte[MaskBytes];
        for (int i = 0; i < MaskBytes; i++) m[i] = 0;
        m[cpu >> 3] = (byte)(1 << (cpu & 7));
        return sched_setaffinity(0, MaskBytes, m) == 0;
    }

    /// <summary>Let the calling thread run anywhere: for a worker created by a pinned thread.</summary>
    public static void ClearThis()
    {
        int n = Math.Min(Environment.ProcessorCount, MaskBytes * 8);
        byte* m = stackalloc byte[MaskBytes];
        for (int i = 0; i < MaskBytes; i++) m[i] = 0;
        for (int c = 0; c < n; c++) m[c >> 3] |= (byte)(1 << (c & 7));
        sched_setaffinity(0, MaskBytes, m);
    }

    public static int CurrentCpu => sched_getcpu();
}
