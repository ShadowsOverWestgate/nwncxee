using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The twelve entry points the <c>CAurObject</c> animation slots call, in the spelling
/// <c>Seam/ObjectSlots.Anim.cs</c> uses, each one line over the runtime in the other <c>DbAnim*.cs</c> files.
/// <c>DbAnim.Api.cs</c> is the same operations under the engine's own slot names; both surfaces are kept.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Set by the seam so a refusal from this runtime reaches <c>cx.log</c> (seam-obj's
    /// <c>SeamService</c> assigns it at boot; <see cref="Bind"/> is the same thing by another name).</summary>
    public static ExtenderLog? Log { get => _log; set => _log = value; }

    /// <summary><c>Gob::Animate(float)</c> on ONE object: what the engine's per-gob animate did, for the paths
    /// that advance a single object (<c>CAurObject::AnimateExternal</c>, a GUI panel's model).</summary>
    public static void AnimateObject(DbId obj, float dt) => AnimateOneExternal(obj, dt);

    /// <summary>The animation gates: <c>Enable/DisableAnimations</c> (<paramref name="force"/> false) and
    /// <c>Enable/DisableForceAnimations</c> (true), which exempt an object from the SCENE's gate.</summary>
    public static void SetAnimationsEnabled(DbId obj, bool on, bool force)
        => SetFlag(obj, force ? ObjAnim.Forced : ObjAnim.Animating, on);

    /// <summary><c>Gob::BeginLookAtAnimate</c> (slot 74) in the ENGINE's argument order: @0x49d5b0 looks
    /// <paramref name="targetPart"/> up in the TARGET's +0x90 tree and <paramref name="ownPart"/> in its own.
    /// The floats are the yaw limit, the pitch limit, one with no consumer here, and the maximum distance,
    /// which <c>LookAtAnimation</c> @0x49d480 stores at +0x48..+0x54 via <c>ABS</c>, the first two clamped to 89.</summary>
    public static void BeginLookAt(DbId obj, DbId target, string? targetPart, string? ownPart,
                                  float yawLimitDeg, float pitchLimitDeg, float unresolved, float maxDistance)
        => LookAtBegin(obj, target, ownPart, targetPart, yawLimitDeg, pitchLimitDeg, maxDistance);

    /// <summary><c>Gob::EndLookAtAnimate</c> (slot 75).</summary>
    public static void EndLookAt(DbId obj) => LookAtEnd(obj);

    /// <summary><c>Gob::DoEvent(name, data)</c> with the name as a raw <c>char*</c>, which is what the seam
    /// holds.</summary>
    public static void Event(DbId obj, nint name, nint data) => EventFire(obj, (byte*)name, data);
}
