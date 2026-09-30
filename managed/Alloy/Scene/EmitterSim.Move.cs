using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// CX-86: birth and motion. Spawn is <c>Particle::initialize</c> @0x4abbf0 with <c>randomPosition</c>
/// @0x4ab7e0, <c>randomDirection</c> @0x4ab160 and <c>inheritVelocity</c> @0x4ab950; the three movers are
/// <c>moveParticleStandard</c>'s body @0x4afa90, <c>moveParticleP2PBezier</c> @0x4b0230 and
/// <c>moveParticleP2PGrav</c> @0x4b0d40.
/// </summary>
internal static unsafe partial class EmitterSim
{
    /// <summary>Birth one particle: place it, orient it, give it a speed, and pick its first UV frame.
    /// <para>The order is the engine's and two steps of it are not interchangeable -- the inherited velocity
    /// is measured against the position the spawn rectangle just chose, and the launch direction is composed
    /// ONTO the emitter's own orientation rather than replacing it.</para></summary>
    private static void Spawn(ref Slot slot, IntPtr em, ref PartEmitter e, MdlNodeEmitter node,
                              int flags, float dt, float scale)
    {
        Particle p = Take(ref slot);
        if (p.IsNull) return;
        Seeded(ref slot, em, ref e, node, flags, p, dt, scale);
    }

    /// <summary>Lightning's birth: a chain particle, which takes its place from the fractal rather than from
    /// the spawn rectangle, so it needs the seeding but not the launch.</summary>
    private static void Seed(ref Slot slot, IntPtr em, ref PartEmitter e, MdlNodeEmitter node)
    {
        Particle p = Take(ref slot);
        if (p.IsNull) return;
        Clear(p);
        p.Quat = e.Orient;
        p.Pos = e.Pos;
        p.PrevPos = e.Pos;
        p.Frame = (int)e.FrameStart;
        p.SplatNormal = AxisZ(e.Basis);
    }

    private static void Seeded(ref Slot slot, IntPtr em, ref PartEmitter e, MdlNodeEmitter node,
                              int flags, Particle p, float dt, float scale)
    {
        Clear(p);
        p.Quat = e.Orient;
        p.Frame = (int)e.FrameStart;

        // The speed jitter is drawn FIRST, because the engine draws it before it moves the particle and the
        // stream's order is the only thing that makes two runs of one effect differ the same way twice.
        int jitter = 0;
        float rv = e.RandVel;
        if (rv > Epsilon)
        {
            jitter = Rnd(ref slot.Rng, (int)(rv * 100f));
            if (!Coin(ref slot.Rng)) jitter = -jitter;
        }

        int sx = (int)(e.SpawnX * 100f * scale), sy = (int)(e.SpawnY * 100f * scale);
        if (sx == 0 && sy == 0) p.Pos = e.Pos;
        else RandomPosition(ref slot, ref e, p, scale);
        p.PrevPos = p.Pos;

        if ((flags & FInheritVel) != 0) Inherit(ref e, p, dt);
        if (e.Spread != 0f) Launch(ref slot, ref e, node, p);

        Vector3 dir = QuatZ(p.Quat);
        p.SplatNormal = dir;
        p.Vel = dir * (e.Velocity + jitter * Centi);

        // chkRandom: a "random" emitter starts each particle on a random cell of its own declared range,
        // which is what makes a sparse smoke plume not look like one animation played N times.
        if ((flags & FRandom) != 0)
        {
            int span = (int)MathF.Abs(e.FrameEnd - e.FrameStart) + 1;
            p.Frame = (int)(Rnd(ref slot.Rng, span) + e.FrameStart);
        }
    }

    /// <summary>A fresh record. The pool is reused, so a birth has to leave nothing of the particle that held
    /// this slot -- a stale velocity or splat byte is a particle that flies off or never moves.</summary>
    private static void Clear(Particle p)
    {
        p.Pos = Vector3.Zero; p.PrevPos = Vector3.Zero; p.Vel = Vector3.Zero;
        p.InhVel = Vector3.Zero; p.InhDecay = 0f; p.SplatNormal = Vector3.Zero;
        p.Roll = 0f; p.Age = 0f; p.FrameTime = 0f; p.Frame = 0; p.Splat = 0;
    }

    /// <summary><c>randomPosition</c>: somewhere in the emitter's local XY rectangle. The engine works in
    /// ten-thousandths and takes HALF the declared extent as the draw range, so the declared size is the full
    /// width rather than a radius; and it skips the draw entirely below four ticks, because
    /// <c>rand() % 1</c> is always zero anyway.</summary>
    private static void RandomPosition(ref Slot slot, ref PartEmitter e, Particle p, float scale)
    {
        int sx = (int)(e.SpawnX * 100f * scale), sy = (int)(e.SpawnY * 100f * scale);
        int ox = sx > 3 ? Rnd(ref slot.Rng, (int)(sx * 0.5f)) : 0;
        int oy = sy > 3 ? Rnd(ref slot.Rng, (int)(sy * 0.5f)) : 0;
        if (Coin(ref slot.Rng)) ox = -ox;
        if (Coin(ref slot.Rng)) oy = -oy;
        Span<float> m = e.Basis;
        p.Pos = e.Pos + AxisX(m) * (ox * Deci4) + AxisY(m) * (oy * Deci4);
    }

    /// <summary><c>randomDirection</c>: tilt off the emitter's Z by up to half the spread, then spin that
    /// tilt a full turn about Z. The cone cap that follows folds a direction pointing too far away from the
    /// viewer back toward it -- which is what keeps a wide-spread impact effect facing the player.
    /// <para>The engine's fold loop has no bound and terminates on float luck; this one stops after
    /// <see cref="MaxConeFolds"/>, which on a frame hook is the difference between a wrong angle and a hung
    /// client.</para></summary>
    private static void Launch(ref Slot slot, ref PartEmitter e, MdlNodeEmitter node, Particle p)
    {
        Span<float> m = e.Basis;
        float tilt = Rnd(ref slot.Rng, (int)(e.Spread * 0.5f * 100f) + 1) * Centi;
        p.Quat = QMul(AxisAngle(Norm(AxisX(m)), tilt), p.Quat);
        float spin = Rnd(ref slot.Rng, AzimuthTicks) * Centi;
        p.Quat = QMul(AxisAngle(Norm(AxisZ(m)), spin), p.Quat);

        float cap = node.ConeCap;
        if (cap == 0f) return;
        IntPtr cam = AlloyCamera.World;
        if (cam == IntPtr.Zero) return;
        Vector3 view = Norm(p.Pos - new CX.Engine.Camera(cam).Pos);
        if (view == Vector3.Zero) return;
        float limit = MathF.Cos(HalfPi - cap);
        for (int k = 0; k < MaxConeFolds; k++)
        {
            Vector3 z = QuatZ(p.Quat);
            float d = Vector3.Dot(z, view);
            if (MathF.Abs(d) >= limit) return;
            float ang = d >= 0f ? cap + (d - HalfPi) : cap + HalfPi - d;
            Vector3 axis = Norm(Vector3.Cross(view, z));
            if (axis == Vector3.Zero) return;
            p.Quat = QMul(AxisAngle(axis, ang), p.Quat);
        }
    }

    /// <summary><c>inheritVelocity</c>: a snapshot of how fast the EMITTER is moving and turning, stored as a
    /// unit direction plus a magnitude that the movers decay toward zero. Both halves count -- the
    /// translation over the step, and the sweep the emitter's own rotation gives a particle standing at
    /// distance <c>d</c> from it.</summary>
    private static void Inherit(ref PartEmitter e, Particle p, float dt)
    {
        float inv = dt > 0f ? 1f / dt : Epsilon;
        float d = (e.Pos - p.Pos).Length();
        Vector3 v = (AxisZ(e.Basis) - QuatZ(e.PrevRot)) * (d * inv) + (e.Pos - e.PrevPos) * inv;
        float mag = v.Length();
        p.InhDecay = mag;
        p.InhVel = mag != 0f ? v / mag : Vector3.Zero;
    }

    // ---- the movers ----------------------------------------------------------------------------------

    /// <summary>Integrate one emitter's particles and then record the transform the next step measures
    /// against.</summary>
    private static void Move(ref Slot slot, IntPtr em, ref PartEmitter e, MdlNodeEmitter node,
                            int flags, IntPtr scene, float dt)
    {
        var gob = new CX.Engine.Part(em).Gob;
        switch (slot.Mover)
        {
            case MoveBezier: Bezier(ref slot, ref e, gob, flags, scene, dt); break;
            case MoveGrav: Grav(ref slot, ref e, gob, flags, scene, dt); break;
            default: Standard(ref slot, ref e, gob, flags, scene, dt); break;
        }
        bool local = (flags & FInheritLocal) != 0;
        if (!gob.IsNull && local) { e.PrevWorldPos = gob.Position; e.PrevWorldRot = gob.Rot; }
        else if ((flags & FInheritPart) != 0) { e.PrevWorldPos = e.Pos; e.PrevWorldRot = e.Orient; }
    }

    /// <summary>The three inherit modes, factored out of the movers that each open with their own copy.
    /// <para>Returns the particle's position expressed in the CURRENT emitter frame, and rotates its velocity
    /// with it when the whole field is being carried. <c>inherit</c> rebases through the emitter's rotation
    /// delta; <c>inherit_local</c> and <c>inherit_part</c> translate by their owner's motion.</para>
    /// <para><b>Departure, on the record:</b> the p2p-gravity mover's own version of this composes the
    /// owner's rotation delta as a second quaternion pair as well as translating. Alloy uses the translation
    /// form for all three movers, which is exactly what the standard mover does -- so a p2p-gravity emitter
    /// declaring <c>inherit_local</c> AND spinning its owner will drift where vanilla would carry it. Named
    /// rather than reproduced: the engine's version reads two more transforms per particle for a combination
    /// no stock effect uses.</para></summary>
    private static Vector3 Carry(ref PartEmitter e, CX.Engine.Gob gob, int flags, Particle p, in Quat rebase)
    {
        Vector3 start = p.Pos;
        if ((flags & FInherit) != 0)
        {
            start = Rotate(rebase, p.Pos - e.PrevPos) + e.Pos;
            p.Vel = Rotate(rebase, p.Vel);
        }
        else if ((flags & FInheritLocal) != 0 && !gob.IsNull)
        {
            Vector3 d = gob.Position - e.PrevWorldPos;
            start += d; p.PrevPos += d;
        }
        if ((flags & FInheritPart) != 0)
        {
            Vector3 d = e.Pos - e.PrevWorldPos;
            start += d; p.PrevPos += d;
        }
        return start;
    }

    /// <summary>The ordinary mover: velocity, the decaying inherited velocity, wind, gravity. The decay is
    /// <c>|(1 - dt) * decay|</c> per step, which is the engine's own -- frame-rate dependent, and kept,
    /// because it is what sets how far a spawn's inherited motion carries.</summary>
    private static void Standard(ref Slot slot, ref PartEmitter e, CX.Engine.Gob gob, int flags,
                                 IntPtr scene, float dt)
    {
        Quat rebase = QMul(e.Orient, Conj(e.PrevRot));
        bool wind = (flags & FWind) != 0;
        bool bounce = (flags & FBounce) != 0 && Bouncing.V;
        float grav = e.Gravity * Gravity * dt;
        for (int i = 0; i < slot.Live; i++)
        {
            Particle p = At(ref slot, i);
            if (p.Splat != 0) continue;
            Vector3 was = p.Pos;
            Vector3 start = Carry(ref e, gob, flags, p, rebase);
            float decay = p.InhDecay;
            Vector3 pos = start + p.Vel * dt + p.InhVel * (decay * dt);
            if (wind) pos += Wind(scene, pos, dt);
            Vector3 v = p.Vel;
            p.Vel = new Vector3(v.X, v.Y, v.Z + grav);
            p.InhDecay = MathF.Abs((1f - dt) * decay);
            p.Pos = pos;
            if (bounce) Reflect(ref e, p, was, dt);
        }
    }

    /// <summary>p2p bezier: a cubic curve from the emitter to its target, blended into the ballistic position
    /// by <c>pow(t³, combineTime)</c> so a particle leaves the emitter under its own velocity and arrives on
    /// the curve. Control points are the emitter's Z axis times <c>bezier2</c> and the target's times
    /// <c>bezier3</c>.</summary>
    private static void Bezier(ref Slot slot, ref PartEmitter e, CX.Engine.Gob gob, int flags,
                               IntPtr scene, float dt)
    {
        var target = e.P2pTarget;
        if (target.IsNull) { _p2pNoTarget++; return; }
        AurXform.V3 t3 = AurXform.Position(target.Ptr);
        Vector3 tip = new(t3.X, t3.Y, t3.Z);
        Quat tq = AurXform.Orientation(target.Ptr);
        Vector3 c0 = e.Pos, c3 = tip;
        Vector3 c1 = c0 + AxisZ(e.Basis) * e.Bezier2;
        Vector3 c2 = c3 + QuatZ(tq) * e.Bezier3;

        Quat rebase = QMul(e.Orient, Conj(e.PrevRot));
        bool wind = (flags & FWind) != 0;
        bool bounce = (flags & FBounce) != 0 && Bouncing.V;
        float life = e.Life, comb = e.CombineTime;
        float grav = e.Gravity * Gravity * dt;
        for (int i = 0; i < slot.Live; i++)
        {
            Particle p = At(ref slot, i);
            if (p.Splat != 0) continue;
            Vector3 was = p.Pos;
            Vector3 start = Carry(ref e, gob, flags, p, rebase);
            Vector3 ball = start + p.Vel * dt;
            if (wind) ball += Wind(scene, ball, dt);

            float t = life > 0f ? MathF.Min(1f, p.Age / life) : 1f;
            float u = 1f - t, tt = t * t;
            Vector3 curve = c0 * (u * u * u) + c1 * (3f * t * u * u) + c2 * (3f * tt * u) + c3 * (tt * t);
            float k = MathF.Pow(tt * t, comb);
            float decay = p.InhDecay;
            Vector3 v = p.Vel;
            p.Vel = new Vector3(v.X, v.Y, v.Z + grav);
            p.InhDecay = MathF.Abs((1f - dt) * decay);
            p.Pos = curve * k + ball * (1f - k) + p.InhVel * (decay * dt);
            if (bounce) Reflect(ref e, p, was, dt);
        }
    }

    /// <summary>p2p gravity: drag plus a constant acceleration toward the target, and a particle that reaches
    /// the target's <c>deadSpace</c> is aged out on the spot. The arrival test is against the SWEPT segment,
    /// not the end point, so a fast particle cannot tunnel past a small target.</summary>
    private static void Grav(ref Slot slot, ref PartEmitter e, CX.Engine.Gob gob, int flags,
                            IntPtr scene, float dt)
    {
        var target = e.P2pTarget;
        if (target.IsNull) { _p2pNoTarget++; return; }
        AurXform.V3 t3 = AurXform.Position(target.Ptr);
        Vector3 tip = new(t3.X, t3.Y, t3.Z);

        Quat rebase = QMul(e.Orient, Conj(e.PrevRot));
        bool wind = (flags & FWind) != 0;
        bool bounce = (flags & FBounce) != 0 && Bouncing.V;
        float dead = e.DeadSpace, dead2 = dead * dead;
        float keep = MathF.Pow(e.P2pDrag, dt), accel = e.P2pGrav;
        float life = e.Life, grav = e.Gravity * Gravity * dt;
        for (int i = 0; i < slot.Live; i++)
        {
            Particle p = At(ref slot, i);
            if (p.Splat != 0) continue;
            Vector3 was = p.Pos;
            Vector3 start = Carry(ref e, gob, flags, p, rebase);
            Vector3 ball = start + p.Vel * dt;
            if (wind) ball += Wind(scene, ball, dt);
            if ((tip - start).LengthSquared() <= dead2) { p.Age = life; continue; }

            p.Vel *= keep;
            Vector3 push = Norm(tip - start) * (accel * dt);
            // The engine integrates the push into POSITION with a second dt and into VELOCITY with one. Kept:
            // it is the half-step that gives the arc its shape, and "fixing" it flattens every p2p trail.
            ball += push * dt;
            p.Vel += push;

            Vector3 moved = ball - start;
            if (moved != Vector3.Zero) p.SplatNormal = Norm(moved);
            if ((tip - Nearest(start, ball, tip)).LengthSquared() <= dead2) p.Age = life;

            float decay = p.InhDecay;
            Vector3 v = p.Vel;
            p.Vel = new Vector3(v.X, v.Y, v.Z + grav);
            p.InhDecay = MathF.Abs((1f - dt) * decay);
            p.Pos = ball + p.InhVel * (decay * dt);
            if (bounce) Reflect(ref e, p, was, dt);
        }
    }

    /// <summary>The bounce, ported from <c>moveParticleStandard</c>'s body @0x4afa90 (the two p2p movers
    /// carry the same block). <paramref name="was"/> is where the particle was before this step -- the
    /// engine sweeps from there, not from the rebased start, and captures it at the TOP of its loop.
    /// <para>Three engine terms that look arbitrary and are not. <b>0.75 on every component</b> is a fixed
    /// loss the engine applies before restitution. <b>Restitution and the frame-rate factor land on Z only</b>
    /// -- the tangential components keep the flat 0.75 -- so a low-<c>bounce</c> emitter slides rather than
    /// stopping. <b>The factor is <c>clamp(0.033 / dt, 0.5, 1)</c></b>: one at 60 and 30fps, damping the
    /// bounce below that, which is what stops a stuttering frame from launching debris.</para>
    /// <para>Geometry is <see cref="BounceWorld"/>'s: up-facing tile faces, no walls and no placeables. See
    /// its class doc for why, and <c>emitsim.bounce=0</c> for the A/B.</para></summary>
    private static void Reflect(ref PartEmitter e, Particle p, Vector3 was, float dt)
    {
        Vector3 now = p.Pos;
        if (was == now) return;
        if (!BounceWorld.Hit(was, now, out float t, out Vector3 n)) return;
        _bounced++;
        p.Pos = was + (now - was) * t;
        Land(e.Bounce, p, n, dt);
    }

    /// <summary>The arithmetic half of <see cref="Reflect"/>, split out so the SelfCheck drives THIS rather
    /// than a copy of it: the contact point is already in <paramref name="p"/> and the geometry is done.
    /// <paramref name="co"/> is the emitter's restitution.</summary>
    internal static void Land(float co, Particle p, Vector3 n, float dt)
    {
        bool land;
        if (co == 0f) { p.Vel = Vector3.Zero; land = true; }
        else
        {
            float f = DtRef / dt;
            float k = f <= 1f ? MathF.Max(MinBounceScale, f) : 1f;
            Vector3 v = p.Vel;
            Vector3 r = v - 2f * Vector3.Dot(n, v) * n;
            v = new Vector3(r.X * BounceLoss, r.Y * BounceLoss, r.Z * BounceLoss * co * k);
            p.Vel = v;
            land = v.LengthSquared() < LandSpeed2;
        }
        if (!land) return;
        // Landed: the quad stops billboarding and lies in the contact plane, which is what the splat byte and
        // the contact normal together mean to the vertex build.
        p.Splat = 1;
        p.Quat = RotationArc(new Vector3(0f, 0f, 1f), n);
        p.Pos += n * LandLift;
        p.SplatNormal = n;
    }

    /// <summary>The engine's own bounce literals, from the disassembly around 0x4afe00: the flat loss every
    /// component takes, the reference step the frame-rate factor is measured against and its floor, the
    /// squared speed below which a particle lands, and how far off the face it is lifted when it does.
    /// </summary>
    private const float BounceLoss = 0.75f, DtRef = 0.033f, MinBounceScale = 0.5f,
                        LandSpeed2 = 0.5f, LandLift = 0.02f;

    private static Vector3 Wind(IntPtr scene, Vector3 at, float dt)
    {
        AurXform.V3 w = AurXform.Wind(scene, new AurXform.V3 { X = at.X, Y = at.Y, Z = at.Z }, dt);
        return new Vector3(w.X, w.Y, w.Z);
    }

    // ---- small maths, all of it the engine's own free functions ---------------------------------------

    /// <summary>The Gob's uniform scale, which every spawn extent is measured in
    /// (<c>Gob::GetAdjustedScale</c>, the vcall three of the four update modes make). One without a Gob is
    /// unscaled.</summary>
    private static float Scale(IntPtr em)
    {
        var gob = new CX.Engine.Part(em).Gob;
        return gob.IsNull ? 1f : gob.GetAdjustedScale();
    }

    /// <summary>Hamilton product, (w, x, y, z) in and out -- the engine's <c>Quaternion::operator*</c>.
    /// </summary>
    private static Quat QMul(in Quat a, in Quat b) => new()
    {
        W = a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        X = a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        Y = a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        Z = a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
    };

    private static Quat Conj(in Quat q) => new() { W = q.W, X = -q.X, Y = -q.Y, Z = -q.Z };

    private static Quat AxisAngle(Vector3 axis, float angle)
    {
        float h = angle * 0.5f;
        float s = MathF.Sin(h);
        return new Quat { W = MathF.Cos(h), X = axis.X * s, Y = axis.Y * s, Z = axis.Z * s };
    }

    /// <summary>A quaternion's Z axis -- the direction a particle launches and splats along.</summary>
    private static Vector3 QuatZ(in Quat q)
        => new(2f * (q.X * q.Z + q.W * q.Y), 2f * (q.Y * q.Z - q.W * q.X),
               1f - 2f * (q.X * q.X + q.Y * q.Y));

    private static Vector3 Rotate(in Quat q, Vector3 v)
    {
        Vector3 u = new(q.X, q.Y, q.Z);
        Vector3 t = Vector3.Cross(u, v) * 2f;
        return v + t * q.W + Vector3.Cross(u, t);
    }

    /// <summary>The shortest rotation carrying <paramref name="from"/> onto <paramref name="to"/> -- the
    /// engine's <c>RotationArc</c>. Both are expected unit; antiparallel takes any perpendicular, because the
    /// arc is genuinely ambiguous there and the alternative is a NaN in every particle's position.</summary>
    private static Quat RotationArc(Vector3 from, Vector3 to)
    {
        float d = Vector3.Dot(from, to);
        if (d >= 1f - 1e-6f) return new Quat { W = 1f };
        if (d <= -1f + 1e-6f)
        {
            Vector3 any = Perp(from);
            return new Quat { W = 0f, X = any.X, Y = any.Y, Z = any.Z };
        }
        Vector3 c = Vector3.Cross(from, to);
        float s = MathF.Sqrt(2f * (1f + d));
        return new Quat { W = s * 0.5f, X = c.X / s, Y = c.Y / s, Z = c.Z / s };
    }

    /// <summary>Any unit vector perpendicular to <paramref name="v"/>, built off its own smallest component so
    /// the cross can never be degenerate.</summary>
    private static Vector3 Perp(Vector3 v)
    {
        Vector3 a = MathF.Abs(v.X) <= MathF.Abs(v.Y) && MathF.Abs(v.X) <= MathF.Abs(v.Z)
                  ? new Vector3(1f, 0f, 0f)
                  : MathF.Abs(v.Y) <= MathF.Abs(v.Z) ? new Vector3(0f, 1f, 0f) : new Vector3(0f, 0f, 1f);
        return Norm(Vector3.Cross(v, a));
    }

    private static Vector3 Norm(Vector3 v)
    {
        float l = v.LengthSquared();
        return l > 0f ? v * (1f / MathF.Sqrt(l)) : Vector3.Zero;
    }

    /// <summary>The point of segment <c>a..b</c> closest to <paramref name="p"/> -- the engine's
    /// <c>segmentproject</c>, clamped to the segment's own ends.</summary>
    private static Vector3 Nearest(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float len = ab.LengthSquared();
        if (len <= 0f) return a;
        float t = Vector3.Dot(p - a, ab) / len;
        return a + ab * (t < 0f ? 0f : t > 1f ? 1f : t);
    }
}
