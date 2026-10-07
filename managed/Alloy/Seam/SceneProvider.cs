using System;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>Plugs the seam-scene stream's slot tables and hooks into the seam's registry.
/// <para>seam-scene exposes its work as slot-index-to-entry-point arrays (<c>SceneSlots.Table</c>,
/// <c>SceneOnlySlots.Table</c>, <c>CameraSlots.Table</c> / <c>PrimaryExtra</c>) and one
/// <c>SceneFns.Install(log, hooks)</c>, rather than as an <see cref="ISeamProvider"/>. Both shapes are fine --
/// the registry only needs a function pointer per slot -- so this adapter is where the two meet, and it lives
/// with the installer because the installer is what has the <c>HookService</c>.</para>
/// <para>A zero entry in one of those arrays is deliberate: it means "no CX body for this slot", and the
/// builder fills it with the poison entry for that slot's NAME, which is what the boot census then prints.</para></summary>
public sealed unsafe class SceneProvider : ISeamProvider
{
    internal static ExtenderLog? Log;
    internal static HookService? Hooks;

    /// <summary>True once <see cref="SceneFns"/>' hooks took; the service stands DB mode down if they did not.</summary>
    public static bool SceneHooksInstalled { get; private set; }

    public void Register(SeamRegistry r)
    {
        if (Log != null) { SceneSlots.Attach(Log); CameraSlots.Attach(Log); SeamFrame.Attach(Log); }

        Plug(r, SeamTable.Scene, 0, SceneSlots.Table);
        Plug(r, SeamTable.Scene, SceneOnlySlots.First, SceneOnlySlots.Table);
        Plug(r, SeamTable.Camera, 0, CameraSlots.Table);
        Plug(r, SeamTable.Camera, CameraSlots.Count, CameraSlots.PrimaryExtra);

        if (Log != null && Hooks != null)
            SceneHooksInstalled = SceneFns.Install(Log, Hooks);

        // The DB-mode frame boundary is armed HERE, not from the pass registry's Install walk. That walk runs
        // only on a client where the Alloy renderer can arm ("everything above this line has already returned
        // in that case", AlloyRenderer.cs) -- so a session that installed the seam and then stood the renderer
        // down would have no engine scene layer AND no CX frame, which is a black screen with nothing to say
        // why. Armed off the seam itself, the boundary exists whenever the seam does.
        if (Log != null) CX.Alloy.Db.SeamDraw.Install(Log);
    }

    /// <summary>Claim <paramref name="table"/>'s slots from <paramref name="first"/> for the non-zero entries
    /// of <paramref name="entries"/>.</summary>
    private static void Plug(SeamRegistry r, SeamTable table, int first, IntPtr[] entries)
    {
        for (int i = 0; i < entries.Length; i++)
            if (entries[i] != IntPtr.Zero) r.Slot(table, first + i, (void*)entries[i]);
    }
}
