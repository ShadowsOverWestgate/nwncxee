using System;

namespace CX.Graphics;

/// <summary>
/// Persistent-mapped ring storage: immutable buffer storage the CPU keeps a live pointer into, divided into
/// regions the GPU is fenced out of while it reads them (#174/#173). Nothing about it is pass-specific, so
/// it lives here rather than inside whichever pass happened to need it first.
/// </summary>
public static unsafe class GpuRing
{
    /// <summary>How long a region wait tolerates before it gives the frame up. A wait that TIMES OUT must
    /// skip the work, never barge through: overwriting a region the GPU is still reading is silent
    /// corruption, which is strictly worse than a dropped frame's worth of that pass.</summary>
    public const ulong TimeoutNs = 100_000_000UL;

    /// <summary>Create immutable storage of <paramref name="bytes"/> on <paramref name="target"/> and return
    /// its persistent mapping, or null if the driver refused. Leaves nothing bound.</summary>
    /// <remarks>The storage and map flags must agree -- a map asking for a bit the store was not created
    /// with fails -- which is the whole reason this is one call and not two.</remarks>
    public static byte* Map(uint target, long bytes, out uint buf)
    {
        const uint flags = Gl.MAP_WRITE_BIT | Gl.MAP_PERSISTENT_BIT | Gl.MAP_COHERENT_BIT;
        uint b; Gl.GenBuffers(1, &b); buf = b;
        Gl.BindBuffer(target, buf);
        Gl.BufferStorage(target, (IntPtr)bytes, null, flags);
        byte* map = (byte*)Gl.MapBufferRange(target, IntPtr.Zero, (IntPtr)bytes, flags);
        Gl.BindBuffer(target, 0);
        return map;
    }

    /// <summary>Take a region for writing: wait out its fence if one is standing, then clear it. False = the
    /// wait timed out and the caller must skip this write entirely.</summary>
    public static bool Acquire(IntPtr[] fence, int region)
    {
        if (fence[region] == IntPtr.Zero) return true;
        uint r = Gl.ClientWaitSync(fence[region], Gl.SYNC_FLUSH_COMMANDS_BIT, TimeoutNs);
        if (r != Gl.ALREADY_SIGNALED && r != Gl.CONDITION_SATISFIED) return false;
        Gl.DeleteSync(fence[region]);
        fence[region] = IntPtr.Zero;
        return true;
    }

    /// <summary>Fence a region behind the commands just issued out of it. Call after the draw, not after the
    /// write: the CPU write is finished, the GPU read is not.</summary>
    public static void Signal(IntPtr[] fence, int region)
        => fence[region] = Gl.FenceSync(Gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
}
