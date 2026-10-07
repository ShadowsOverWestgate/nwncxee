using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>CX-89: the door fade, Alloy's. A behaviour port of <c>Scene::FadeDoors</c> @0x467990, which was
/// both the fade and #132's frame anchor; the anchor is now a direct call out of <see cref="RspShim.Run"/> and
/// the engine function is not called on an Alloy frame at all.
/// <para><b>Why it is not optional.</b> The fade's output is <c>Gob+0x10c</c>
/// (<see cref="EngineLayout.GobFadeAlpha"/>) and <c>Gob::HitCheck</c> @0x4381d0 reads the same field: its
/// guard is <c>hitInfo+0x7d == 0 || (*(gob+0x68)+0x8e &amp; 8) == 0 || mindooralpha &lt; *(float*)(gob+0x10c)</c>,
/// so a door sitting AT <c>mindooralpha</c> is not hit-tested -- freezing the integrator makes doors
/// unclickable without changing a pixel. Hit testing stays the engine's until tier 4 (CX-118), so this writes
/// the engine's field rather than a shadow copy, and Alloy's own draw folds the same field through
/// <see cref="AlloyCensus.GobAlphaMul"/>.</para>
/// <para>GL-free, allocation-free: one pass over <c>gobbucket</c> with the arithmetic in registers.</para>
/// </summary>
internal static unsafe class DoorFade
{
    /// <summary>Seconds a door needs to cross the whole alpha range: the engine divides <c>DeltaT</c> by this
    /// (the 0.3f at 0x12ee9f0) and adds or subtracts the quotient.</summary>
    private const float FadeSeconds = 0.3f;
    /// <summary>How far the entered face's outward normal must lean toward the scene focus before the door is
    /// driven opaque -- the 0.1f at 0x12eeeac, compared as <c>0.1 &lt; dot</c>.</summary>
    private const float OpaqueDot = 0.1f;
    /// <summary>Sanity ceiling on the <c>gobbucket</c> count, mirroring the chase's own
    /// (<c>AlloyRenderer.VisLoadCap</c>): a list this long is a bad read, not a busy area.</summary>
    private const int MaxGobs = 1 << 13;

    /// <summary>CX-104: frames the visible set was unreadable and the whole scene's gobs were stepped. Zero is
    /// the expected value; a rate here means the renderer's own visibility answer is not standing up.</summary>
    private static long _visAllFrames;
    private static float* _minAlpha;
    private static float* _deltaT;
    private static IntPtr* _currentCamera;
    private static ExtenderLog? _log;
    private static bool _cappedLogged;
    /// <summary><c>/alloy doors</c> was asked for; the next <see cref="Tick"/> writes the report.</summary>
    private static bool _armed;

    /// <summary>The engine symbol that did not resolve, or null. Non-null stands the whole Alloy path down
    /// (<see cref="RspShim.Install"/>): a shim that has stopped calling <c>Scene::FadeDoors</c> and cannot run
    /// this in its place would freeze every door's alpha, and with it every door's clickability.</summary>
    internal static string? Missing { get; private set; }

    internal static void Resolve(ExtenderLog log)
    {
        _log = log;
        _minAlpha = (float*)Native.Sym(SymbolMap.MinDoorAlpha);
        _deltaT = (float*)Native.Sym(SymbolMap.DeltaT);
        _currentCamera = (IntPtr*)Native.Sym(SymbolMap.CurrentCamera);
        // CX-104: gobbucket is no longer on this list -- the population comes from AlloyRenderer.VisibleGobs,
        // which answers for itself, so an unresolved bucket global is no longer a reason to stand the whole
        // Alloy path down over the door fade.
        Missing = _minAlpha == null ? SymbolMap.MinDoorAlpha
                : _deltaT == null ? SymbolMap.DeltaT
                : _currentCamera == null ? SymbolMap.CurrentCamera : null;
    }

    /// <summary>Step every visible door's fade, once per Alloy frame, BEFORE the frame draws -- the slot
    /// <c>Scene::FadeDoors</c> held. The population is Alloy's own visible-gob set
    /// (<see cref="AlloyRenderer.VisibleGobs"/>), so a door out of view keeps its alpha exactly as vanilla left
    /// it. CX-104: that accessor is the one visibility answer every pass shares -- reading the
    /// <c>gobbucket</c> global here as well meant two readers could disagree about which frame's list they
    /// were on, and a Tick at the top of <see cref="RspShim.Run"/> reads a list the anchor has not sampled
    /// yet. Runs in engine context and touches no GL.</summary>
    internal static void Tick(IntPtr scene)
    {
        if (Missing != null || scene == IntPtr.Zero) return;
        if (_armed) { _armed = false; LogDoors(scene); }
        // A set the renderer could not read is not a licence to freeze every door: without it there is no
        // in-view answer at all, and a frozen alpha is a door that cannot be clicked (see the class remarks).
        // So the whole scene's gobs are stepped instead -- the same population, minus the cull.
        if (!AlloyRenderer.VisibleGobs(out IntPtr* items, out int n))
        {
            var all = new Scene(scene);
            items = (IntPtr*)all.Gobs;
            n = all.GobCount;
            _visAllFrames++;
        }
        if (n <= 0 || items == null) return;
        if (n > MaxGobs)
        {
            if (!_cappedLogged)
            {
                _cappedLogged = true;
                _log?.Always($"DoorFade: the visible-gob set read {n} entries (cap {MaxGobs}) -- the door fade "
                         + "sits out this frame; doors will neither fade nor refuse clicks, and this list is "
                         + "not sane");
            }
            return;
        }
        var cam = new Camera(AlloyCamera.World);
        if (cam.IsNull) return;
        Vector3 camPos = cam.Pos;
        // Scene+0x190 is the SCENE FOCUS (Scene::SetSceneFocus @0x477890 is its only writer; the sky parks
        // its domes there too, which is why the row is named skyPos). It is the point the door must not hide.
        Vector3 focus = new Scene(scene).SkyPos;
        float step = *_deltaT / FadeSeconds;
        float floor = *_minAlpha;
        for (int i = 0; i < n; i++)
        {
            IntPtr p = items[i];
            if (p == IntPtr.Zero) continue;
            var g = new Gob(p);
            Model node = g.Node;
            if (node.IsNull || (node.OwnerFlags & AlloyCensus.GobOwnerFadeBit) == 0) continue;
            bool opaque = Opaque(g.BoundsMin, g.BoundsMax, g.Position, g.Rot, camPos, focus);
            g.FadeAlpha = Clamp(g.FadeAlpha + (opaque ? step : -step), floor);
        }
    }

    /// <summary>The engine's clamp, in its order: ceiling at 1 first, then the <c>mindooralpha</c> floor, so a
    /// floor above 1 wins. Both branches of the original land here.</summary>
    internal static float Clamp(float alpha, float floor) => MathF.Max(floor, MathF.Min(1f, alpha));

    /// <summary>CX-118, DB mode: one door object's new fade, into the two columns that replace
    /// <c>Gob+0x10c</c>. There is no engine field to write in DB mode, and the one value has two readers
    /// that must never disagree -- <see cref="SceneDb.HitObjFadeAlpha"/> is what the hit test gates a click
    /// on (<c>Gob::HitCheck</c>'s <c>mindooralpha</c> test), and <c>SceneDb.PartFade</c> is what the draw
    /// multiplies in (the seam-feed contract). A door that faded out of the picture but not out of the pick,
    /// or the reverse, is exactly the bug the single writer exists to prevent.</summary>
    internal static void StepDb(DbId obj, float step, bool opaque, float floor)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        int o = obj.Index;
        float a = Clamp(SceneDb.HitObjFadeAlpha[o] + (opaque ? step : -step), floor);
        SceneDb.HitObjFadeAlpha[o] = a;
        int first = SceneDb.ObjPartFirst[o];
        for (int i = first; i < first + SceneDb.ObjPartCount[o]; i++)
        {
            SceneDb.PartFade[i] = a;
            SceneDb.TouchPart(i, Dirty.Material);
        }
        SceneDb.Touch(obj, Dirty.Material);
    }

    /// <summary>Should this door be driven toward opaque? The engine's own test, transcribed from the
    /// disassembly of <c>Scene::FadeDoors</c> @0x467a6e..0x467d5b: shoot a segment through the door's bounding
    /// box from the camera's position in the gob's frame to its mirror about the rotated box centre, take the
    /// outward normal of the face it enters, and ask whether that normal leans toward the scene focus. A
    /// camera INSIDE the box (no face) fades the door out.
    /// <para>The two frames the engine mixes here are deliberate parity, not a transcription slip: the
    /// segment's xy comes from the inverse-rotated camera delta while its z is the ROTATED centre's z
    /// (@0x467bea stores <c>(q*centre).z</c> into both endpoints), and the mirror point is the rotated centre
    /// rather than the local one. Doors are hinged about z and the box is thin across the leaf, so the test
    /// that matters is the xy one.</para></summary>
    internal static bool Opaque(Vector3 bmin, Vector3 bmax, Vector3 pos, Quaternion rot,
                               Vector3 camPos, Vector3 focus)
    {
        Vector3 centre = (bmin + bmax) * 0.5f;
        Vector3 rc = Vector3.Transform(centre, rot);
        Vector3 world = pos + rc;
        // `operator-(Quaternion const&)` @0x4b5d50 negates the STORED w and nothing else; for a rotation that
        // is the inverse, and Numerics reads the same four floats as (x, y, z, w).
        Vector3 local = Vector3.Transform(camPos - world, new Quaternion(rot.X, rot.Y, rot.Z, -rot.W));
        var a = new Vector3(local.X, local.Y, rc.Z);
        var b = new Vector3(2f * rc.X - local.X, 2f * rc.Y - local.Y, rc.Z);
        int face = BoxEnterFace(a, b, bmin, bmax);
        if (face < 0) return false;
        Vector3 n = default;
        // face 1..6 = -x, +x, -y, +y, -z, +z; face 0 (segment misses) takes the engine's own fallthrough,
        // axis 0 with a positive sign, and lets the dot decide.
        int axis = (face - 1) / 2;
        float sign = ((face - 1) & 1) != 0 ? 1f : -1f;
        if (axis == 0) n.X = sign; else if (axis == 1) n.Y = sign; else n.Z = sign;
        return Vector3.Dot(Vector3.Transform(n, rot), Normalize(focus - world)) > OpaqueDot;
    }

    /// <summary><c>normalize(Vector const&amp;)</c> @0x4b37d0: a zero vector comes back as (1, 0, 0).</summary>
    private static Vector3 Normalize(Vector3 v)
    {
        float len2 = v.LengthSquared();
        return len2 == 0f ? new Vector3(1f, 0f, 0f) : v * (1f / MathF.Sqrt(len2));
    }

    // Outcode bits, in the order BoxIntersect @0x4b7f20 ORs them (0x467f20 disassembly, @0x4b7f20..0x4b7fbc).
    private const int OutXLo = 0x20, OutXHi = 0x10, OutYLo = 0x08, OutYHi = 0x04, OutZLo = 0x02, OutZHi = 0x01;   // not-an-offset: the engine's own outcode bit values

    /// <summary><c>BoxIntersect(A, B, min, max)</c> @0x4b7f20: which face of the box the segment A-&gt;B
    /// enters. -1 when A is inside the box, 0 when the segment misses it, else 1..6 for -x, +x, -y, +y, -z,
    /// +z. Cohen-Sutherland outcodes, a trivial reject on the shared bits, then one slab crossing per outcode
    /// bit A carries, in x, y, z order -- the first crossing that lands inside the other two spans wins.</summary>
    internal static int BoxEnterFace(Vector3 a, Vector3 b, Vector3 min, Vector3 max)
    {
        int ca = OutCode(a, min, max);
        if (ca == 0) return -1;
        if ((ca & OutCode(b, min, max)) != 0) return 0;
        if ((ca & OutXLo) != 0 && Crosses(a.X, b.X, min.X, a, b, min, max, 0)) return 1;
        if ((ca & OutXHi) != 0 && Crosses(a.X, b.X, max.X, a, b, min, max, 0)) return 2;
        if ((ca & OutYLo) != 0 && Crosses(a.Y, b.Y, min.Y, a, b, min, max, 1)) return 3;
        if ((ca & OutYHi) != 0 && Crosses(a.Y, b.Y, max.Y, a, b, min, max, 1)) return 4;
        if ((ca & OutZLo) != 0 && Crosses(a.Z, b.Z, min.Z, a, b, min, max, 2)) return 5;
        if ((ca & OutZHi) != 0 && Crosses(a.Z, b.Z, max.Z, a, b, min, max, 2)) return 6;
        return 0;
    }

    private static int OutCode(Vector3 p, Vector3 min, Vector3 max)
        => (p.X < min.X ? OutXLo : p.X > max.X ? OutXHi : 0)
         | (p.Y < min.Y ? OutYLo : p.Y > max.Y ? OutYHi : 0)
         | (p.Z < min.Z ? OutZLo : p.Z > max.Z ? OutZHi : 0);

    /// <summary>Does the segment cross the <paramref name="axis"/> plane at <paramref name="plane"/> inside
    /// the box's other two spans?</summary>
    private static bool Crosses(float pa, float pb, float plane, Vector3 a, Vector3 b,
                                Vector3 min, Vector3 max, int axis)
    {
        float t = (plane - pa) / (pb - pa);
        Vector3 hit = a + (b - a) * t;
        return (axis == 0 || (hit.X >= min.X && hit.X <= max.X))
            && (axis == 1 || (hit.Y >= min.Y && hit.Y <= max.Y))
            && (axis == 2 || (hit.Z >= min.Z && hit.Z <= max.Z));
    }

    /// <summary>The <c>/alloy doors</c> button. The report needs <c>CurrentCamera</c>, which only
    /// <c>Camera::RenderScene</c> @0x5a7920 sets (and clears on the way out), so a chat command arms it and the
    /// next <see cref="Tick"/> writes it from inside the render.</summary>
    internal static void Arm()
    {
        if (Missing != null) { _log?.Info($"DoorFade: {Missing} unresolved -- no fade, and the Alloy path is down"); return; }
        _armed = true;
        _log?.Info("DoorFade: report armed -- written at the next Alloy world frame");
    }

    /// <summary>The <c>/alloy doors</c> report: every visible door's alpha against <c>mindooralpha</c> and
    /// the verdict driving it. A door printed <c>UNCLICKABLE</c> is one <c>Gob::HitCheck</c> will refuse, so
    /// this is how the fade is checked by CLICKING rather than by looking.</summary>
    private static void LogDoors(IntPtr scene)
    {
        ExtenderLog? log = _log;
        if (log == null) return;
        if (!AlloyRenderer.VisibleGobs(out IntPtr* items, out int n))
        {
            var sc = new Scene(scene);
            items = (IntPtr*)sc.Gobs;
            n = sc.GobCount;
        }
        if (n <= 0 || n > MaxGobs || items == null) { log.Info($"DoorFade: visible-gob count {n} -- nothing to report"); return; }
        var cam = new Camera(AlloyCamera.World);
        if (cam.IsNull) { log.Info("DoorFade: no CurrentCamera -- outside a scene render"); return; }
        Vector3 camPos = cam.Pos;
        Vector3 focus = new Scene(scene).SkyPos;
        float floor = *_minAlpha;
        int doors = 0, stuck = 0;
        for (int i = 0; i < n; i++)
        {
            IntPtr p = items[i];
            if (p == IntPtr.Zero) continue;
            var g = new Gob(p);
            Model node = g.Node;
            if (node.IsNull || (node.OwnerFlags & AlloyCensus.GobOwnerFadeBit) == 0) continue;
            doors++;
            float a = g.FadeAlpha;
            bool clickable = a > floor;
            if (!clickable) stuck++;
            log.Info($"DoorFade: door {doors} gob={p:x} alpha={a:0.000} mindooralpha={floor:0.000} "
                   + $"target={(Opaque(g.BoundsMin, g.BoundsMax, g.Position, g.Rot, camPos, focus) ? "opaque" : "faded")}"
                   + (clickable ? "" : " UNCLICKABLE (Gob::HitCheck skips a door at the floor)"));
        }
        log.Info($"DoorFade: {doors} door gob(s) in view, {stuck} at the floor; DeltaT={*_deltaT:0.0000}s, "
               + $"full fade {FadeSeconds:0.00}s, {_visAllFrames} frame(s) stepped over the whole scene");
    }

    /// <summary>Off-device (run-demo.sh): the fade integrator, the clamp and the face test. All three fail
    /// SILENTLY on the screen -- a wrong face normal picks the wrong side of the door, a wrong clamp order
    /// pins the alpha at the floor, and both read as "doors do not fade" or, worse, as doors that no longer
    /// take a click. False = do not arm the Alloy path, so the engine keeps its own FadeDoors.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DoorFade.SelfCheck");

        check.Expect(Clamp(1.5f, 0.25f) == 1f, "the ceiling is 1");
        check.Expect(Clamp(-1f, 0.25f) == 0.25f, "the floor is mindooralpha");
        check.Expect(Clamp(0.5f, 0.25f) == 0.5f, "an alpha inside the range is untouched");
        check.Expect(Clamp(0.5f, 1.5f) == 1.5f, "the floor is applied AFTER the ceiling, so a floor above 1 wins");
        // 0.3s edge to edge: a 60Hz frame moves one twentieth of the range
        check.Expect(MathF.Abs((1f / 60f) / FadeSeconds - 0.05555f) < 1e-4f, "a 60Hz frame steps 1/18 of the range");

        // A door leaf in the xz plane, thin across y, centred on its gob: the shape every NWN door has.
        var bmin = new Vector3(-1f, -0.1f, -1f);
        var bmax = new Vector3(1f, 0.1f, 1f);
        check.Expect(BoxEnterFace(new Vector3(0f, 5f, 0f), new Vector3(0f, -5f, 0f), bmin, bmax) == 4,
                     "a segment from +y enters the +y face");
        check.Expect(BoxEnterFace(new Vector3(0f, -5f, 0f), new Vector3(0f, 5f, 0f), bmin, bmax) == 3,
                     "...and from -y the -y face");
        check.Expect(BoxEnterFace(new Vector3(5f, 0f, 0f), new Vector3(-5f, 0f, 0f), bmin, bmax) == 2,
                     "+x enters the +x face");
        check.Expect(BoxEnterFace(new Vector3(-5f, 0f, 0f), new Vector3(5f, 0f, 0f), bmin, bmax) == 1,
                     "-x enters the -x face");
        check.Expect(BoxEnterFace(new Vector3(0f, 0f, 5f), new Vector3(0f, 0f, -5f), bmin, bmax) == 6,
                     "+z enters the +z face");
        check.Expect(BoxEnterFace(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 5f), bmin, bmax) == 5,
                     "-z enters the -z face");
        check.Expect(BoxEnterFace(default, new Vector3(0f, 5f, 0f), bmin, bmax) == -1,
                     "a start point inside the box is the engine's -1, which fades the door out");
        check.Expect(BoxEnterFace(new Vector3(9f, 5f, 0f), new Vector3(9f, -5f, 0f), bmin, bmax) == 0,
                     "a segment that misses the box is 0");
        check.Expect(BoxEnterFace(new Vector3(5f, 5f, 0f), new Vector3(9f, 9f, 0f), bmin, bmax) == 0,
                     "both endpoints outside the same faces is the trivial reject");

        // The target rule: opaque when camera and focus are on the SAME side of the leaf, faded when the
        // door stands between them. This is the whole feature -- get the sign wrong and doors go solid
        // exactly when they should clear.
        check.Expect(Opaque(bmin, bmax, default, Quaternion.Identity, new Vector3(0f, 5f, 0f), new Vector3(0f, 9f, 0f)),
                     "camera and focus on the same side: opaque");
        check.Expect(!Opaque(bmin, bmax, default, Quaternion.Identity, new Vector3(0f, 5f, 0f), new Vector3(0f, -9f, 0f)),
                     "the door between camera and focus: faded");
        check.Expect(Opaque(bmin, bmax, default, Quaternion.Identity, new Vector3(0f, -5f, 0f), new Vector3(0f, -9f, 0f)),
                     "...and the same from the other side");
        check.Expect(!Opaque(bmin, bmax, default, Quaternion.Identity, default, new Vector3(0f, 9f, 0f)),
                     "a camera inside the door's box fades it");
        // ...and with the leaf hinged 90 degrees about z, so the rotation and its inverse both have to be right
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        check.Expect(Opaque(bmin, bmax, default, yaw, new Vector3(5f, 0f, 0f), new Vector3(9f, 0f, 0f)),
                     "a yawed door is opaque with camera and focus on its same face");
        check.Expect(!Opaque(bmin, bmax, default, yaw, new Vector3(5f, 0f, 0f), new Vector3(-9f, 0f, 0f)),
                     "a yawed door between camera and focus fades");
        // the gob's own position has to move the box with it
        var away = new Vector3(100f, 0f, 0f);
        check.Expect(!Opaque(bmin, bmax, away, Quaternion.Identity, away + new Vector3(0f, 5f, 0f), away - new Vector3(0f, 9f, 0f)),
                     "the test is relative to the gob's world position");

        // Everything above reads the gob through the typed views; a transposed row is a frozen alpha, not an
        // error, so the two rows this tick writes and reads are exercised against a synthetic gob.
        byte* gob = stackalloc byte[0x500];
        for (int i = 0; i < 0x500; i++) gob[i] = 0;
        var gv = new Gob((IntPtr)gob);
        gv.Rot = Quaternion.Identity;
        check.Expect(gv.Rot.W == 1f && gv.Rot.X == 0f && gv.Rot.Y == 0f && gv.Rot.Z == 0f,
                     "Gob+0xb4 holds the orientation as the engine's (w, x, y, z)");
        gv.FadeAlpha = Clamp(2f, 0.25f);
        check.Expect(gv.FadeAlpha == 1f && *(float*)(gob + EngineLayout.GobFadeAlpha) == 1f,
                     "the integrator's output lands in Gob+0x10c, the field the draw folds and HitCheck reads");

        log.Info(check.Ok ? "DoorFade.SelfCheck: passed"
                          : "DoorFade.SelfCheck: FAILED -- the Alloy path stands down so the engine keeps FadeDoors");
        return check.Ok;
    }
}
