using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>CX-120: the three OBJECT-side calls the scene and camera seam makes, as assignable hooks so the
/// scene stream and the object stream do not have to share a file. <c>Seam/SeamService</c> (seam-obj) fills
/// them at boot, before the first seam call can arrive; until it does they are honest no-ops and a scene
/// destroy says so rather than silently leaking its members.
/// <para>Named apart from seam-obj's own <c>CX.Alloy.Seam.SeamBridge</c>, which is the OTHER direction (the
/// object slots reaching CX-117/CX-118). Two classes of one name in two namespaces compile and confuse.</para>
/// <para>This is a wiring seam, not an abstraction: there is exactly one implementation, it is assigned once
/// at boot on the game thread, and every call site is cold (an object joins or leaves a scene, a scene dies,
/// a name lookup). The frame path goes through <see cref="SceneDb"/>'s columns and never through here.</para></summary>
public static class ObjectBridge
{
    /// <summary>Destroy an object record, its part range and its facade (the object destroy edge, ADR 0028
    /// section 3). Set by seam-obj.</summary>
    public static System.Action<DbId>? ObjectDestroy;

    /// <summary><b>The one call that goes the other way, and seam-obj must make it.</b> On the object destroy
    /// edge, synchronously, call <c>DbScene.ObjectDied(obj)</c>: a scene's member list, highlight set, shadow
    /// gob, blind-light object and skybox slots can each name the dying object, and the member COUNT is read
    /// by game code (<c>GetAurSceneObjectCount</c>) without waiting for a frame boundary. A fold at the
    /// present would be a frame too late.</summary>
    public const string ObjectDiedContract = "seam-obj: call DbScene.ObjectDied(obj) at the destroy edge";

    /// <summary>A scene was destroyed and this object was still in it. <b>Detach, do not destroy.</b>
    /// <c>Scene::~Scene</c> @0x472e40 walks its member list calling <c>Gob::SetScene(child, NULL)</c> and
    /// nothing else -- the gobs OUTLIVE their scene and the game deletes them through their own destructors.
    /// Destroying them here retired shells the game still held, which showed up live as
    /// <c>AttachToScene</c>/<c>GetPosition</c>/<c>~Gob</c> arriving on retired facades. Set by seam-obj.</summary>
    public static System.Action<DbId>? ObjectSceneDestroyed;

    /// <summary>An object's name, as <c>NewCAurObject</c> was given it -- what <c>Scene::GetObject</c> matches
    /// on. Set by seam-obj.</summary>
    public static System.Func<DbId, string>? ObjectName;

    /// <summary>The object's ADJUSTED scale -- what <c>Gob::GetAdjustedScale</c> (CAurObject vtbl +0x2d8)
    /// answers, which is the object's own scale folded with its attachment chain's.
    /// <c>Scene::GetObjectScale</c> @0x55e240 is nothing but a tail-call to it, so the scene slot must ask the
    /// object rather than read the raw column. Set by seam-obj.</summary>
    public static System.Func<DbId, float>? ObjectAdjustedScale;

    /// <summary>The object's visual-transform scale alone (<c>LerpFloat::Lerped</c> on ScaleX) -- what
    /// <c>Scene::AddPartsToDrawBuckets</c> multiplies in for each ancestor of an attached object. Set by seam-obj.</summary>
    public static System.Func<DbId, float>? ObjectVisualScale;

    /// <summary>Fold the object's visual transform onto its raw placement, as <c>Gob::GetAdjustedPosition</c>
    /// @0x539900, <c>GetAdjustedOrientation</c> @0x537d20 and <c>GetAdjustedScale</c>'s ScaleX term do -- the
    /// three getters <c>Scene::AddPartsToDrawBuckets</c> places a gob by. <paramref name="lerping"/>: a lerp is
    /// still travelling. Set by seam-obj.</summary>
    public delegate void VisualAdjustFn(DbId obj, ref System.Numerics.Vector3 pos, ref System.Numerics.Quaternion rot,
                                        ref float scale, out bool lerping);
    /// <inheritdoc cref="VisualAdjustFn"/>
    public static VisualAdjustFn? ObjectVisualAdjust;

    private static bool _said;

    /// <summary>Un-burn the once-per-session refusal latch. The off-device suite exercises a scene destroy
    /// with the object side deliberately unwired, which is exactly the state the latch exists to announce --
    /// so without this the ONE loud line a genuinely unwired DB session would get has already been spent on
    /// a fixture, and the real fault is silent.</summary>
    internal static void ResetForSelfCheck() => _said = false;

    /// <summary>Spend the latch before the off-device suite runs, so its deliberate refusal does not print a
    /// fault line into a healthy boot log.</summary>
    internal static void MuteForSelfCheck() => _said = true;

    /// <summary>True when the object side is wired; logs the gap once, loudly, if it is not.</summary>
    internal static bool Ready(ExtenderLog? log)
    {
        if (ObjectDestroy != null) return true;
        if (!_said)
        {
            _said = true;
            log?.Always("ObjectBridge: the object side is NOT wired (SeamService did not fill ObjectDestroy) -- "
                      + "a scene destroy cannot destroy its members; the CX scene database is not usable in "
                      + "this state and the seam must stand down");
        }
        return false;
    }
}
