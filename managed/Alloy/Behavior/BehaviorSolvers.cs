using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Which solver ran, for the period line and the <c>/knobs</c> note. Index order is the hook
/// table's.</summary>
internal enum BehaviorKind
{
    Base, Attach, AttachTranslate, AttachRotate, AttachNoOrientation, AttachGuiScale,
    CrossSceneAttach, CrossSceneAttachOffset, Subwindow, Navigate, CameraFollow,
    CameraLooseFollow, CameraOnAStick,
}

/// <summary>
/// CX-107: Alloy's own <c>CAurBehavior</c> solvers. One method per concrete engine class, ported term for
/// term from <c>docs/research/behaviour-solvers.md</c> §5; the hooks in
/// <see cref="BehaviorService"/> run these INSTEAD of the engine's <c>Control</c>, so they hold whoever
/// dispatches -- the engine's <c>Gob::Animate</c> today, an owned animation runtime tomorrow (CX-105), both
/// through the same virtual.
/// </summary>
/// <remarks>
/// <para>A solver writes the engine's own fields -- <c>Gob+0xa8</c>/<c>+0xb4</c>, or the camera viewport --
/// because the game layer and the rest of Alloy read them there, and reports the write through
/// <see cref="GobMotion.Mark"/> so the transform owner recomposes exactly what moved.</para>
/// <para><b>Hot.</b> One call per live behaviour per frame, on whatever thread the scene walk is on: pointer
/// reads, float maths, no allocation, no strings.</para>
/// <para><b>Lifetime.</b> Two engine calls here can free memory under the solver: a dependency's
/// <c>Animate</c> can destroy the owner, and the detach path either deletes <c>this</c>
/// (<c>SetBehavior(null)</c>) or the owner (<c>diewithoutparent</c>). Both are survived the way the engine
/// survives the first -- a stack <c>SafePointer</c> registered on the owner, which the owner's destructor
/// nulls (<see cref="BehaviorCall.Register"/>) -- and the second is handled by never touching either pointer
/// after the call. That second part is where the port is SAFER than vanilla: the engine's
/// <c>AttachTranslate</c>/<c>AttachRotate</c> read the owner's vtable after
/// <c>CAurBehaviorAttach::Control</c> has deleted it, and the port does not.</para>
/// </remarks>
internal static unsafe partial class BehaviorSolvers
{
    /// <summary>Solves per class since the last period line (written hot, read and cleared cold).</summary>
    private static readonly int[] _solves = new int[Kinds];
    /// <summary>The engine constant a GUI viewport's part coordinates are expressed in: the subwindow solver
    /// divides by it to get pixels and multiplies back to place the camera.</summary>
    private const float GuiUnit = 0.01f;

    private static IntPtr Owner(IntPtr self) => new CAurBehavior(self).Owner;
    private static IntPtr Target(IntPtr self) => new CAurBehavior(self).Target.Ptr;

    /// <summary>The ADDRESS of the owner's position / orientation, which is what <c>Part::GetWorld</c> takes
    /// and what the engine's own snap hands it -- not a field read, so it does not go through a view
    /// accessor.</summary>
    private static Vector3* Pos(IntPtr obj) => (Vector3*)(obj + EngineLayout.GobPosition);
    /// <inheritdoc cref="Pos"/>
    private static Quat* Rot(IntPtr obj) => (Quat*)(obj + EngineLayout.GobRot);

    // ---- the arithmetic, as seams a self-check can reach -----------------------------------------
    // The solvers below call these; SelfCheck pins each to a number derived from the decompilation. They are
    // separate functions for exactly that reason -- nothing off-device can run a solve, because a solve reads
    // engine memory through engine vtables.

    /// <summary>One solve's exponential zoom: the distance halves per two units of accumulated zoom velocity
    /// per second (<c>dist * pow(2, -zoomVel * dt * 0.5)</c>, the exponent computed in float and widened, as
    /// the engine does).</summary>
    internal static float Zoom(float dist, float zoomVel, float dt) =>
        dist * (float)Math.Pow(2.0, -zoomVel * dt * 0.5f);

    /// <summary>The lag factor the camera solve uses for BOTH the focus lerp and the orientation slerp:
    /// <c>1 - 0.5^(dt * rate)</c>, i.e. "close half the remaining gap every <c>1/rate</c> seconds", which is
    /// frame-rate independent by construction.</summary>
    internal static float Lag(float rate, float dt) => 1f - MathF.Pow(0.5f, dt * rate);

    /// <summary>One step of the camera focus toward its target plus the behaviour's own offset.</summary>
    internal static Vector3 FocusStep(in Vector3 focus, in Vector3 target, in Vector3 offset, float a)
    {
        float keep = 1f - a;
        return new Vector3((target.X + offset.X) * a + focus.X * keep,
                           (target.Y + offset.Y) * a + focus.Y * keep,
                           (target.Z + offset.Z) * a + focus.Z * keep);
    }

    /// <summary>Where the camera sits: one <paramref name="dist"/> along <c>q * +Z</c> from the focus.</summary>
    internal static Vector3 Rig(in Vector3 focus, float dist, in Quat q)
    {
        Vector3 f = BehaviorMath.Forward(q);
        return new Vector3(f.X * dist + focus.X, f.Y * dist + focus.Y, f.Z * dist + focus.Z);
    }

    /// <summary>The GUI-scale attach's whole effect: x and y divided by the live scale, z left alone.</summary>
    internal static Vector3 GuiScaled(in Vector3 p, float scale) => new(p.X / scale, p.Y / scale, p.Z);

    /// <summary>One side of the subwindow viewport, in pixels: the GUI-unit span rounded half-up and never
    /// allowed to collapse.</summary>
    internal static int ViewportSpan(float lo, float hi) => Math.Max(1, BehaviorMath.Round((hi - lo) / GuiUnit));

    /// <summary>The navigate solver's basis at <paramref name="yawDeg"/>: forward, then the right it strafes
    /// along (<c>cross(forward, +Z)</c>).</summary>
    internal static (Vector3 Forward, Vector3 Right) NavBasis(float yawDeg)
    {
        (float sin, float cos) = MathF.SinCos(yawDeg * BehaviorMath.Deg2Rad);
        Vector3 fwd = new(-sin, cos, 0f);
        return (fwd, BehaviorMath.Cross(fwd, new Vector3(0f, 0f, 1f)));
    }

    // ---- the base solve --------------------------------------------------------------------------

    /// <summary>
    /// <c>CAurBehavior::Control</c> @0x4a0510. Drags every dependency's animation forward -- that is what
    /// makes a behaviour's parent posed before its part transform is read, whatever order the scene walk
    /// visited them in -- then answers "am I still the owner's behaviour".
    /// <para><paramref name="survivor"/> is the owner if it survived the walk, else <see cref="IntPtr.Zero"/>:
    /// the one thing a caller cannot re-derive, because the pointer at <c>this+0x18</c> is not cleared when
    /// the object behind it dies.</para>
    /// </summary>
    internal static int Base(IntPtr self, float dt, out IntPtr survivor)
    {
        IntPtr owner = Owner(self);
        BehaviorCall.SafePtr sp;
        BehaviorCall.Register(&sp, owner);
        for (int i = BehaviorCall.DepCount(self) - 1; i >= 0; i--)
        {
            // Re-read the count: a dependency's Animate can add or drop dependencies of ours.
            if (i >= BehaviorCall.DepCount(self)) continue;
            IntPtr dep = BehaviorCall.Dep(self, i)->Ptr;
            if (dep != IntPtr.Zero) BehaviorCall.Animate(dep, dt);
        }
        survivor = sp.Ptr;
        if (sp.Ptr == IntPtr.Zero) return 0;
        int r = BehaviorCall.GetBehavior(sp.Ptr, -1) == self ? 1 : 0;
        BehaviorCall.Unregister(&sp);
        return r;
    }

    // ---- the attach family ----------------------------------------------------------------------

    /// <summary>
    /// <c>CAurBehaviorAttach::Control</c> @0x4a2810: snap the owner's position and orientation to the parent
    /// part's world transform, or -- with the parent gone -- detach (deleting <c>this</c>) or delete the
    /// owner. The four subclasses run this first and then apply their own offset.
    /// <para><paramref name="live"/> is the owner while it is safe to touch, and
    /// <see cref="IntPtr.Zero"/> once it has been deleted or the behaviour detached. The returned int is the
    /// engine's verdict, which is 1 on both detach paths.</para>
    /// <para>The snap is a RAW field write, exactly as the engine does it: <c>Part::GetWorld</c> is handed
    /// <c>&amp;owner-&gt;position</c> and <c>&amp;owner-&gt;orientation</c>, so a <c>SetPosition</c> override
    /// is deliberately not consulted for it. The subclasses' offsets DO go through the virtuals.</para>
    /// </summary>
    internal static int AttachCore(IntPtr self, float dt, out IntPtr live)
    {
        int r = Base(self, dt, out IntPtr owner);
        live = owner;
        if (owner == IntPtr.Zero || r == 0) return r;
        if (BehaviorCall.GetBehavior(owner, -1) == self)
        {
            var att = new CAurBehaviorAttach(self);
            if (Target(self) == IntPtr.Zero)
            {
                live = IntPtr.Zero;
                // Either call frees something the solver holds, so nothing may be read after it.
                if (att.DieWithoutParent == 0) BehaviorCall.SetBehavior(owner, IntPtr.Zero);
                else BehaviorCall.DeleteObject(owner);
                return 1;
            }
            BehaviorCall.PartGetWorld(att.Part, Pos(owner), Rot(owner));
            GobMotion.Mark(owner);
        }
        return 1;
    }

    /// <inheritdoc cref="AttachCore"/>
    internal static int Attach(IntPtr self, float dt) => AttachCore(self, dt, out _);

    /// <summary><c>CAurBehaviorAttachTranslate::Control</c> @0x4a2960 -- attach, then offset the owner by
    /// <c>orientation * m_vTranslation</c>. The offset is in the parent part's frame, which is why it is
    /// rotated by the orientation the snap just wrote.</summary>
    internal static int AttachTranslate(IntPtr self, float dt)
    {
        int r = AttachCore(self, dt, out IntPtr o);
        if (r == 0 || o == IntPtr.Zero) return r;
        Vector3 v = BehaviorMath.Rotate(BehaviorCall.GetRot(o), new CAurBehaviorAttach(self).Offset);
        BehaviorCall.SetPos(o, BehaviorCall.GetPos(o) + v);
        GobMotion.Mark(o);
        return r;
    }

    /// <summary><c>CAurBehaviorAttachRotate::Control</c> @0x4a2ad0 -- attach, then post-multiply the owner's
    /// orientation by the behaviour's Euler triple. The field is a <c>Vector</c> of degrees and the engine
    /// feeds it to <c>YawPitchRoll</c> as <c>(z, x, y)</c>: z is the yaw, x the pitch, y the roll
    /// (<c>objdump</c> 0x4a2b24).</summary>
    internal static int AttachRotate(IntPtr self, float dt)
    {
        int r = AttachCore(self, dt, out IntPtr o);
        if (r == 0 || o == IntPtr.Zero) return r;
        Vector3 e = new CAurBehaviorAttach(self).Offset;
        Quat rot = BehaviorMath.YawPitchRoll(e.Z, e.X, e.Y);
        BehaviorCall.SetRot(o, BehaviorMath.Mul(BehaviorCall.GetRot(o), rot));
        GobMotion.Mark(o);
        return r;
    }

    /// <summary><c>CAurBehaviorAttachNoOrientation::Control</c> @0x4a2bd0 -- follow the part's position but
    /// keep the owner's own facing: the orientation is read BEFORE the snap and written back after.</summary>
    internal static int AttachNoOrientation(IntPtr self, float dt)
    {
        IntPtr before = Owner(self);
        Quat saved = before == IntPtr.Zero ? BehaviorMath.Identity : BehaviorCall.GetRot(before);
        int r = AttachCore(self, dt, out IntPtr o);
        if (r == 0 || o == IntPtr.Zero) return r;
        BehaviorCall.SetRot(o, saved);
        GobMotion.Mark(o);
        return r;
    }

    /// <summary><c>CAurBehaviorAttachGUIScale::Control</c> @0x4a2c80 -- attach, then divide the owner's x and
    /// y by the live GUI scale so a 3D overlay stays where the unscaled layout put it. Z is deliberately not
    /// divided. The tail runs whether or not the base solve said stop, which is why it is guarded on the
    /// owner surviving rather than on <c>r</c>.</summary>
    internal static int AttachGuiScale(IntPtr self, float dt)
    {
        int r = AttachCore(self, dt, out IntPtr o);
        if (o == IntPtr.Zero) return r;
        BehaviorCall.SetPos(o, GuiScaled(BehaviorCall.GetPos(o), BehaviorCall.GuiScale()));
        GobMotion.Mark(o);
        return r;
    }

    // ---- the GUI's cross-scene attach ------------------------------------------------------------

    /// <summary>
    /// <c>CAurBehaviorCrossSceneAttach::Control</c> @0x58b5d0 and
    /// <c>...WithOffset::Control</c> @0x58c280 -- the same snap with the dependency walk flattened to the one
    /// parent (the GUI's attach never has more), the behaviour-identity test inlined to a direct compare
    /// against <c>Gob+0x450</c>, and a constant world offset added afterwards by the subclass. Always
    /// returns 1.
    /// </summary>
    internal static int CrossSceneAttach(IntPtr self, float dt, bool withOffset)
    {
        IntPtr owner = Owner(self);
        BehaviorCall.SafePtr sp;
        BehaviorCall.Register(&sp, owner);
        IntPtr parent = Target(self);
        if (parent != IntPtr.Zero) BehaviorCall.Animate(parent, dt);
        if (sp.Ptr != IntPtr.Zero && new Gob(owner).Attach.Ptr == self)
        {
            var att = new CAurBehaviorAttach(self);
            if (Target(self) == IntPtr.Zero)
            {
                if (att.DieWithoutParent == 0)
                    BehaviorCall.SetBehavior(owner, IntPtr.Zero);   // frees `this`
                else
                {
                    BehaviorCall.DeleteObject(owner);               // nulls sp.Ptr through the destructor
                    return 1;
                }
            }
            else
            {
                Vector3* pos = Pos(owner);
                BehaviorCall.PartGetWorld(att.Part, pos, Rot(owner));
                if (withOffset) *pos += new CAurBehaviorCrossSceneAttach(self).Offset;
                GobMotion.Mark(owner);
            }
        }
        BehaviorCall.Unregister(&sp);
        return 1;
    }

    // ---- the GUI viewport ------------------------------------------------------------------------

    /// <summary>The four panels whose viewport the engine re-derives through the GUI camera rather than
    /// taking the dummy parts at face value. Compared for exact equality including the terminator, which is
    /// what the engine's inlined <c>repz cmpsb</c> of 15 / 15 / 15 / 16 bytes does.</summary>
    private static bool NamedPanel(byte* name)
    {
        if (name == null) return false;
        return Eq(name, "PNL_CHR_APPEAR"u8) || Eq(name, "EDIT_CHAT_MAIN"u8)
            || Eq(name, "EDIT_CHAT_LEFT"u8) || Eq(name, "EDIT_CHAT_RIGHT"u8);
    }

    private static bool Eq(byte* a, ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < b.Length; i++)
            if (a[i] != b[i]) return false;
        return a[b.Length] == 0;
    }

    /// <summary>
    /// <c>CAurBehaviorSubwindow::Control</c> @0x58b720 -- two dummy parts of a GUI panel become the owning
    /// camera's viewport. The parts are in GUI units of <see cref="GuiUnit"/>; the rectangle is rounded
    /// half-up (the engine's <c>(int)(v + 1000.5f) - 1000</c>) and never allowed to collapse below one pixel.
    /// <para>The engine's upscale branch is NOT reproduced, because it is the identity in this build: the
    /// caller hands <c>Camera::UpscaleViewport</c> an all-zero "current viewport" rect, which leaves the
    /// shift at 0 and the scale at 1, and the remap <c>(v/0.01 - 0) * 1 * 0.01</c> is <c>v</c>. Working that
    /// out is docs/research/behaviour-solvers.md §5.9; what survives of the branch is the part that does
    /// move something -- with no <c>cameraGUI_pnl_chr_genm1</c> gob in the panel's scene, the rectangle is
    /// pinned to the origin and scaled by the GUI scale.</para>
    /// </summary>
    internal static int Subwindow(IntPtr self, float dt)
    {
        IntPtr owner = Owner(self);
        BehaviorCall.SafePtr sp;
        BehaviorCall.Register(&sp, owner);
        IntPtr parent = Target(self);
        if (parent != IntPtr.Zero) BehaviorCall.Animate(parent, dt);
        if (sp.Ptr != IntPtr.Zero && new Gob(owner).Attach.Ptr == self)
        {
            parent = Target(self);
            if (parent == IntPtr.Zero)
            {
                BehaviorCall.SetBehavior(owner, IntPtr.Zero);       // frees `this`; no dieWithoutParent here
                BehaviorCall.Unregister(&sp);
                return 1;
            }
            var sub = new CAurBehaviorSubwindow(self);
            Quat q = BehaviorMath.Identity;
            Vector3 tl = default, br = default;
            BehaviorCall.PartGetWorld(sub.PartA, &tl, &q);
            BehaviorCall.PartGetWorld(sub.PartB, &br, &q);
            if (NamedPanel((byte*)BehaviorCall.GetName(parent)))
            {
                IntPtr scene = BehaviorCall.GetScene(parent);
                if (scene != IntPtr.Zero && GuiCamera(scene) == IntPtr.Zero)
                {
                    float s = BehaviorCall.GuiScale();
                    tl.X = 0f; tl.Y = 0f;
                    br.X *= s; br.Y *= s;
                }
            }
            int w = ViewportSpan(tl.X, br.X), h = ViewportSpan(tl.Y, br.Y);
            IntPtr cam = BehaviorCall.AsCamera(owner);
            if (cam != IntPtr.Zero)
            {
                BehaviorCall.SetViewPort(cam, BehaviorMath.Round(tl.X / GuiUnit),
                                         BehaviorMath.Round(tl.Y / GuiUnit), w, h);
                // Camera::SetPosition @0x4a38f0 stores Camera+0xb0 and the owner IS Camera+8. The engine
                // hands it (tl.x, tl.y, the camera's OWN z) @0x58bba8: part A's z would clip the list away.
                if (sub.SetPos != 0)
                {
                    Vector3* p = Pos(owner);
                    p->X = tl.X; p->Y = tl.Y;
                    GobMotion.Mark(owner);
                }
            }
        }
        BehaviorCall.Unregister(&sp);
        return 1;
    }

    /// <summary>The GUI panel camera the upscale branch keys on. Its presence or absence is the only thing
    /// the port takes from that branch (see <see cref="Subwindow"/>).</summary>
    private static IntPtr GuiCamera(IntPtr scene)
    {
        ReadOnlySpan<byte> name = "cameraGUI_pnl_chr_genm1\0"u8;
        fixed (byte* p = name) return BehaviorCall.SceneGetObject(scene, p);
    }

    // ---- navigate --------------------------------------------------------------------------------

    /// <summary>
    /// <c>CAurBehaviorNavigate::Control</c> @0x4a0660 -- the <c>navigate</c> console function's free-fly.
    /// Four inputs, each consumed and cleared by one solve: yaw and pitch rates in deg/s, forward and strafe
    /// speeds in units/s. Forward is <c>(-sin yaw, cos yaw, 0)</c> (yaw 0 faces +Y, the engine's convention
    /// throughout), strafe its right-hand cross with +Z. Pitch is floored at 0.
    /// </summary>
    internal static int Navigate(IntPtr self, float dt)
    {
        int r = Base(self, dt, out IntPtr o);
        if (r == 0 || o == IntPtr.Zero) return r;
        float yaw = BehaviorMath.Yaw(BehaviorCall.GetRot(o));
        float pitch = BehaviorMath.Pitch(BehaviorCall.GetRot(o));
        var nav = new CAurBehaviorNavigate(self);
        ref float yawRate = ref nav.YawRate;
        ref float pitchRate = ref nav.PitchRate;
        ref float forward = ref nav.Forward;
        ref float strafe = ref nav.Strafe;
        yaw += yawRate * dt; yawRate = 0f;
        pitch += pitchRate * dt; pitchRate = 0f;
        if (pitch <= 0f) pitch = 0f;
        (Vector3 fwd, Vector3 right) = NavBasis(yaw);
        if (forward != 0f)
        {
            Vector3 p = BehaviorCall.GetPos(o);
            BehaviorCall.SetPos(o, new Vector3(fwd.X * forward * dt + p.X, fwd.Y * forward * dt + p.Y, p.Z));
        }
        forward = 0f;
        if (strafe != 0f)
        {
            Vector3 p = BehaviorCall.GetPos(o);
            BehaviorCall.SetPos(o, new Vector3(right.X * strafe * dt + p.X,
                                               right.Y * strafe * dt + p.Y,
                                               right.Z * strafe * dt + p.Z));
        }
        strafe = 0f;
        // The engine's own round trip. It looks like a no-op and is one for Gob, but SetPosition is virtual
        // and CNWCCreature's / CNWCVisualEffect's do more than store -- so the port keeps it.
        BehaviorCall.SetPos(o, BehaviorCall.GetPos(o));
        nav.Spare = 0f;
        BehaviorCall.SetRot(o, BehaviorMath.YawPitchRoll(yaw, pitch, BehaviorMath.Roll(BehaviorCall.GetRot(o))));
        GobMotion.Mark(o);
        return 1;
    }

    // ---- the camera solvers ----------------------------------------------------------------------

    /// <summary>
    /// <c>CAurBehaviorCameraFollow::Control</c> @0x4a2290 -- the one camera solve the client actually
    /// installs (<c>Camera::SetFollow</c>). Integrates yaw, pitch and distance from their velocity + impulse
    /// pairs (each consumed by the solve), clamps each against its own optional range, lags the focus toward
    /// the target, slerps the orientation toward <c>YawPitchRoll(yaw, pitch, 0)</c> with the SAME lag factor,
    /// and puts the camera one distance along <c>q * +Z</c> from the focus.
    /// <para>The three <c>*Min</c> spans are (min, max) pairs; the clamp is the engine's
    /// <c>max(min(v, hi), lo)</c>, so a crossed pair answers <c>lo</c>.</para>
    /// <para><see cref="CX.Services.Camera.CameraService"/> layers CX's camera feel on top of this through
    /// the same hook chain and needs no change: the port sits where the trampoline was, so the service's own
    /// link still runs after it and still reads the same fields.</para>
    /// </summary>
    internal static int CameraFollow(IntPtr self, float dt)
    {
        int r = Base(self, dt, out IntPtr o);
        if (r == 0 || o == IntPtr.Zero) return 1;
        if (Target(self) == IntPtr.Zero)
        {
            BehaviorCall.SetBehavior(o, IntPtr.Zero);   // frees `this`
            return 1;
        }
        var b = new CAurBehaviorCameraFollow(self);

        float yaw = b.Yaw + b.YawVel * dt + b.YawImpulse;
        b.YawVel = 0f; b.YawImpulse = 0f;
        if (b.YawClamp != 0f) yaw = BehaviorMath.Clamp(yaw, b.YawMin[0], b.YawMin[1]);
        b.Yaw = yaw;

        float pitch = b.Pitch + b.PitchVel * dt + b.PitchImpulse;
        b.PitchVel = 0f; b.PitchImpulse = 0f;
        if (b.PitchClamp != 0f) pitch = BehaviorMath.Clamp(pitch, b.PitchMin[0], b.PitchMin[1]);
        b.Pitch = pitch;

        float dist = Zoom(b.Dist, b.ZoomVel, dt);
        b.ZoomVel = 0f;
        if (b.DistClamp != 0f) dist = BehaviorMath.Clamp(dist, b.DistMin[0], b.DistMin[1]);
        b.Dist = dist;

        float a = Lag(b.Lag, dt);
        ref Vector3 focus = ref b.Focus;
        Vector3 next = FocusStep(focus, BehaviorCall.GetPos(Target(self)), b.Offset, a);

        Quat want = BehaviorMath.YawPitchRoll(yaw, pitch, 0f);
        BehaviorCall.SetRot(o, BehaviorMath.Normalize(BehaviorMath.Slerp(BehaviorCall.GetRot(o), want, a)));
        BehaviorCall.SetPos(o, Rig(next, dist, BehaviorCall.GetRot(o)));
        focus = next;
        GobMotion.Mark(o);
        return 1;
    }

    /// <summary>
    /// <c>CAurBehaviorCameraOnAStick::Control</c> @0x4a0c70 -- a 2-D spring in XY at a fixed height: the
    /// camera is pushed along the target-to-camera direction by <c>((len - stick) / stick) * dt * gain</c>
    /// and aimed down the same direction at a fixed pitch. With a direction override set (mode non-zero and
    /// a non-degenerate vector) the camera is placed on that bearing instead.
    /// <para><b>UNVERIFIED, and unverifiable.</b> Nothing in this client constructs
    /// <c>CAurBehaviorCameraOnAStick</c> -- there is no call site for its constructor anywhere in
    /// <c>nwmain-linux</c> -- so this port can never run and can never be compared against the engine. The
    /// decompiler's output for it also mixes 2-D and 3-D <c>magnitude</c> in places. It is here because the
    /// hook is: with <c>behav.owned</c> on, the engine's <c>Control</c> does not run, and a solver that would
    /// then do nothing at all is worse than a transcription.
    /// ponytail: if a future build ever installs this behaviour, re-derive it from the disassembly before
    /// trusting it -- the period line will show a non-zero count for <c>onastick</c>.</para>
    /// </summary>
    internal static int CameraOnAStick(IntPtr self, float dt)
    {
        int r = Base(self, dt, out IntPtr o);
        if (r == 0 || o == IntPtr.Zero) return 1;
        IntPtr target = Target(self);
        if (target == IntPtr.Zero)
        {
            BehaviorCall.SetBehavior(o, IntPtr.Zero);
            return 1;
        }
        var st = new CAurBehaviorCameraOnAStick(self);
        Vector3 cam = BehaviorCall.GetPos(o);
        Vector3 tgt = BehaviorCall.GetPos(target);
        float height = st.Height, stick = st.Length;
        Vector3 d = new(tgt.X - cam.X, tgt.Y - cam.Y, 0f);
        float len = d.Length();
        if (len <= 0f || stick == 0f) return 1;
        float push = (len - stick) / stick * dt * st.Gain;
        Vector3 want = new(cam.X + d.X / len * push, cam.Y + d.Y / len * push, height);
        Vector3 dir = st.Dir;
        Vector3 aim = d;
        if (st.Mode != 0 && dir.Length() > 0f)
        {
            float back = new Vector3(tgt.X - want.X, tgt.Y - want.Y, 0f).Length();
            want = new Vector3(tgt.X - back * dir.X, tgt.Y - back * dir.Y, height);
            aim = dir;
        }
        BehaviorCall.SetRot(o, BehaviorMath.YawPitchRoll(BehaviorMath.YawOf(aim), st.Pitch, 0f));
        BehaviorCall.SetPos(o, want);
        st.Last = want;
        GobMotion.Mark(o);
        return 1;
    }
}
