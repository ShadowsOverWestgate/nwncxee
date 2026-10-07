using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The twelve entry points the <c>CAurObject</c> animation slots call, in seam-obj's own spelling
/// (<c>Seam/ObjectSlots.Anim.cs</c>), each one line over the runtime in the other <c>DbAnim*.cs</c> files.
/// <para>This file exists because two streams named the same operations differently: seam-obj's stub spelled
/// the gates as one <c>SetAnimationsEnabled(obj, on, force)</c> and the look-at as
/// <c>BeginLookAt</c>/<c>EndLookAt</c>, while <c>DbAnim.Api.cs</c> mirrors the engine's slot names. Both
/// surfaces are kept -- the seam calls these, the engine-named ones document the mapping -- rather than
/// renaming a caller in another stream's file.</para></summary>
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

    /// <summary><c>Gob::BeginLookAtAnimate</c> (slot 74). The argument order is the engine's, which is not the
    /// one either stream's stub guessed: <paramref name="targetPart"/> is looked up in the TARGET's tree and
    /// <paramref name="ownPart"/> in this object's (@0x49d5b0 calls <c>FindPart</c> on the target's +0x90 tree with the
    /// second argument and on its own +0x90 tree with the third), and the four floats are the yaw limit, the pitch
    /// limit, one whose only consumer is the generated animation's build, and the maximum distance
    /// (<c>LookAtAnimation::LookAtAnimation</c> @0x49d480 stores them at +0x48, +0x4c, +0x50, +0x54, each
    /// through <c>ABS</c>, and clamps the first two to 89 degrees; <c>Control</c> @0x4973c0 clamps the measured
    /// yaw against +0x48, the pitch against +0x4c and deactivates past +0x54).</summary>
    public static void BeginLookAt(DbId obj, DbId target, string? targetPart, string? ownPart,
                                  float yawLimitDeg, float pitchLimitDeg, float unresolved, float maxDistance)
        => LookAtBegin(obj, target, ownPart, targetPart, yawLimitDeg, pitchLimitDeg, maxDistance);

    /// <summary><c>Gob::EndLookAtAnimate</c> (slot 75).</summary>
    public static void EndLookAt(DbId obj) => LookAtEnd(obj);

    /// <summary><c>Gob::DoEvent(name, data)</c> with the name as a raw <c>char*</c>, which is what the seam
    /// holds.</summary>
    public static void Event(DbId obj, nint name, nint data) => EventFire(obj, (byte*)name, data);
}
