using System;
using System.Numerics;
using CX.Alloy.Db;

namespace CX.Alloy.Seam;

/// <summary>The seam's one indirection point onto subsystems that land from other streams
/// (CX-117 <c>TexRegistry</c>, CX-118 <c>HitTest</c>, CX-114's part bounds). Every entry starts as a counted
/// refusal, so a slot that depends on a subsystem nobody wired reports itself instead of returning a plausible
/// zero -- and wiring it is one assignment, in whichever file the subsystem's stream owns.
/// <para>Delegates, not direct calls, because those types do not exist on this branch yet and the shape of
/// their APIs is theirs to choose; the orchestrator points these at the real ones at merge. They are cold
/// (a texture swap, a click's cast), never per-draw.</para>
/// <para>An entry whose slot calls its subsystem DIRECTLY does not belong here: <c>ReplaceTexture</c>,
/// <c>RestoreTexture</c> and <c>SetEnvironmentMap</c> were bridge entries until the audit found
/// <see cref="ObjectSlots"/> reaching <see cref="ObjectTex"/> itself, so they were deleted rather than wired
/// -- a delegate nothing calls is a refusal counter that can never fire and a second place to look.</para></summary>
public static class SeamBridge
{
    private static readonly long[] _refusals = new long[16];
    private static readonly bool[] _named = new bool[16];

    private static bool Refuse(int slot, string what)
    {
        _refusals[slot]++;
        if (!_named[slot])
        {
            _named[slot] = true;
            Facade.Log?.Always($"seam: {what} is not wired on this build -- the call is refused, not faked (SeamBridge)");
        }
        return false;
    }

    /// <summary>CX-117 / ADR 0026: one material parameter, in the ENGINE's argument order --
    /// <c>Material::SetMaterialParam</c> @0x4c5d70 matches the first against each parameter's name (to 64
    /// characters), takes the second as the value (<b>empty RESETS it</b> to the material's declared default)
    /// and uses the third to filter on the SharedMaterial's name (to 16).</summary>
    public static Action<DbId, string?, string?, string?> SetMaterialParam =
        static (o, paramName, value, materialFilter) => Refuse(2, "TexRegistry.SetMaterialParam (CX-117)");

    /// <summary>CX-117: <c>Gob::SetTextureAttributes</c> -- the <c>.txi</c> RESOURCE named
    /// <c>txiName</c> applied to every texture of the object named <c>texName</c>
    /// (<c>CAurTextureBasic::Init</c> @0x488940 does <c>AurResGet(txiName, ".txi")</c>; it is a resref, not
    /// inline text).</summary>
    public static Func<DbId, string?, string?, int> SetTextureAttributes =
        static (o, texName, txiName) => Refuse(4, "TexRegistry.SetTextureAttributes (CX-117)") ? 1 : 0;

    /// <summary>CX-117: bilinear/nearest override for every texture of the object.</summary>
    public static Action<DbId, bool> SetTextureFilterOverride = static (o, on) => Refuse(5, "TexRegistry.SetTextureFilterOverride (CX-117)");

    /// <summary>CX-114: the object's bounds in object space, from its part bounds -- what
    /// <c>GetBoundingBox</c> / <c>GetMinimumBoundingBox</c> / <c>GetMinimumBoundingSphere</c> answer. Returns
    /// false when the part tree has no bounds yet (the slot then answers an empty box, and says so once).</summary>
    public delegate bool BoundsFn(DbId obj, bool minimumOnly, out Vector3 min, out Vector3 max);

    public static BoundsFn ObjectBounds = RefusedBounds;

    private static bool RefusedBounds(DbId obj, bool minimumOnly, out Vector3 min, out Vector3 max)
    {
        min = default; max = default;
        return Refuse(7, "the part tree's object bounds (CX-114)");
    }

    /// <summary>CX-116: the light records a light part owns, so <c>GetMaximumLightRadius</c> and
    /// <c>ControlPartLightColor</c> have something to read. Returns the largest range of the object's lights.</summary>
    public static Func<DbId, float> MaxLightRadius = static o => { Refuse(8, "the DB light set's radius query (CX-116)"); return 0f; };

    /// <summary>CX-116: recolour the light a named part owns.</summary>
    public static Action<DbId, string?, Vector3> SetPartLightColour = static (o, p, c) => Refuse(9, "the DB light set's recolour (CX-116)");

    /// <summary>CX-112/114: does the model carry a skinned mesh (<c>Gob::ContainsSkinnedMeshes</c>)? The
    /// engine re-walks the part tree on every call; the answer cannot change without a tree change, so the
    /// seam asks once at construction and keeps a bit.</summary>
    public static Func<CX.Alloy.Mdl.ModelDef, bool> HasSkinnedMesh =
        static m => { Refuse(10, "the model's skinned-mesh query (CX-112)"); return false; };

    /// <summary>CX-114: a part's weight for <c>Gob::GetRandomPart</c>'s area-weighted draw.</summary>
    public static Func<int, float> PartArea = static p => { Refuse(11, "the part's area query (CX-114)"); return 0f; };

    /// <summary>How many times each bridge entry refused, for the seam's period line.</summary>
    public static string Stats()
    {
        long total = 0;
        foreach (long n in _refusals) total += n;
        return total == 0 ? "bridge: all wired" : $"bridge: {total} refused call(s) to unwired subsystems";
    }
}
