using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    /// <summary>
    /// Off-device: every part of the simulation that is a DECISION rather than a read. The engine objects are
    /// synthetic -- a <c>PartEmitter</c> is 0x250 zeroed bytes with the fields this file writes, and the pool
    /// is the real one -- so the whole thing runs on a machine with no client and no GL.
    /// <para>What is NOT covered, and cannot be: <c>Part::GetWorld</c>, <c>Gob::GetAdjustedScale</c>, the wind
    /// call and the p2p target's position virtual are engine calls with no seam. Those are read, reasoned
    /// about and left to the live check.</para>
    /// </summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        // ---- the maths the rest of the file stands on ------------------------------------------------
        // A transposed basis column sends every spawn sideways, and the symptom reads as "the effect is in
        // the wrong place" rather than as an algebra error.
        IntPtr em = Fake(out IntPtr nodePtr);
        var node = new MdlNodeEmitter(nodePtr);
        var e = new PartEmitter(em);
        e.Orient = new Quat { W = 1f };
        Basis(ref e);
        check.Expect(AxisX(e.Basis) == new Vector3(1f, 0f, 0f) && AxisY(e.Basis) == new Vector3(0f, 1f, 0f)
                     && AxisZ(e.Basis) == new Vector3(0f, 0f, 1f),
          "the identity quaternion gives the identity spawn basis, column-major");
        float h = MathF.Sqrt(0.5f);
        e.Orient = new Quat { W = h, Z = h };                 // a quarter turn about Z
        Basis(ref e);
        check.Expect(Near(AxisX(e.Basis), new Vector3(0f, 1f, 0f)) && Near(AxisZ(e.Basis), new Vector3(0f, 0f, 1f)),
          "...and a quarter turn about Z carries the basis X onto world Y -- (w,x,y,z), not (x,y,z,w)");
        check.Expect(Near(QuatZ(new Quat { W = 1f }), new Vector3(0f, 0f, 1f))
                     && Near(QuatZ(AxisAngle(new Vector3(1f, 0f, 0f), HalfPi)), new Vector3(0f, -1f, 0f)),
          "a quarter turn about X tips the launch direction from +Z to -Y");
        check.Expect(Near(Rotate(AxisAngle(new Vector3(0f, 0f, 1f), HalfPi), new Vector3(1f, 0f, 0f)),
                          new Vector3(0f, 1f, 0f)),
          "Rotate agrees with the basis it is composed against");
        Quat arc = RotationArc(new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f));
        check.Expect(Near(Rotate(arc, new Vector3(1f, 0f, 0f)), new Vector3(0f, 1f, 0f)),
          "RotationArc carries its `from` exactly onto its `to` -- the bolt's between-fractal transform");
        check.Expect(Near(Rotate(RotationArc(Vector3.UnitX, -Vector3.UnitX), Vector3.UnitX), -Vector3.UnitX),
          "...including the antiparallel case, where the arc is ambiguous and a NaN would move every particle");
        check.Expect(Near(Nearest(Vector3.Zero, new Vector3(10f, 0f, 0f), new Vector3(3f, 4f, 0f)),
                          new Vector3(3f, 0f, 0f))
                     && Nearest(Vector3.Zero, new Vector3(10f, 0f, 0f), new Vector3(-5f, 0f, 0f)) == Vector3.Zero,
          "the p2p arrival test projects onto the SEGMENT and clamps to its ends");

        // ---- the spawn accumulator. A 3/s emitter at 60fps births 0.05 of a particle a frame; without the
        // carry it births none at all, forever, and the effect is simply missing.
        Reset();
        e.BirthRate = 3f; e.Life = 10f; e.Velocity = 0f; e.FrameEnd = 0f; e.FrameStart = 0f;
        e.Orient = new Quat { W = 1f };
        Basis(ref e);
        Declare(node, "Fountain");
        int s = Open(0, em); _tabN = 1;
        check.Expect(s == 0 && _tab[0].Mode == ModeFountain && _tab[0].Mover == MoveStandard,
          "the model's `update Fountain` with no p2p bit resolves to the fountain mode and the plain mover");
        check.Expect(_tab[0].Cap >= 32, "a 3/s emitter living 10s reserves room for its population");
        ref Slot slot = ref _tab[0];
        int frames = 0;
        // Age then birth, which is Advance's order minus the mover -- the mover needs a live Scene and the
        // accumulator is entirely FountainBirth's.
        while (slot.Live == 0 && frames < 60) { Tick(ref slot, em, ref e, node, 1f / 60f); frames++; }
        check.Expect(slot.Live == 1 && frames >= 19 && frames <= 21,
          "the accumulator births the first particle after ~1/3 s at 60fps, not on frame one and not never");
        for (int i = 0; i < 60; i++) Tick(ref slot, em, ref e, node, 1f / 60f);
        check.Expect(slot.Live == 4, "one more second of a 3/s emitter is three more particles");

        // ---- lifetime and death, and the ORDER death leaves behind. An index-domain fade and the linked
        // render type both read the live slice as oldest-first, so a swap-remove would recolour the chain.
        Reset();
        e.BirthRate = 0f; e.Life = 1f;
        s = Open(0, em); _tabN = 1;
        slot = ref _tab[0];
        slot.Cap = 8;
        for (int i = 0; i < 4; i++) { Particle p = Take(ref slot); p.Age = i * 0.3f; p.Roll = i; }
        check.Expect(slot.Live == 4, "four particles taken");
        // The death test reads the age the PREVIOUS step left and only then adds dt, which is the engine's
        // order: a particle lives out the whole frame it reaches its lifespan on.
        Reap(ref slot, ref e, 0.15f);            // -> 0.15 0.45 0.75 1.05
        check.Expect(slot.Live == 4 && At(ref slot, 0).Roll == 0f && At(ref slot, 3).Roll == 3f,
          "nothing dies under its lifespan and the order is untouched");
        Reap(ref slot, ref e, 0.5f);             // 1.05 is past; survivors -> 0.65 0.95 1.25
        check.Expect(slot.Live == 3 && At(ref slot, 0).Roll == 0f && At(ref slot, 1).Roll == 1f
                     && At(ref slot, 2).Roll == 2f,
          "the one past its life dies and the survivors keep their order");
        Reap(ref slot, ref e, 0f);               // 1.25 is past
        check.Expect(slot.Live == 2 && At(ref slot, 0).Roll == 0f && At(ref slot, 1).Roll == 1f,
          "...and compaction stays stable across more than one death (never a swap-remove)");
        Particle reborn = Take(ref slot);
        check.Expect(!reborn.IsNull && slot.Live == 3,
          "a dead particle's record is reused rather than allocated -- the slice's tail IS the free list");
        e.Life = -1f;
        Particle immortal = At(ref slot, 0);
        immortal.Age = 99f;
        Reap(ref slot, ref e, 0.1f);
        check.Expect(slot.Live == 3, "a negative lifespan is the engine's 'never dies' and is honoured");

        // ---- gravity and the inherited-velocity decay. Both are the engine's exact arithmetic, and both are
        // what a comparison shot would show first if they were wrong.
        Reset();
        e.Life = 10f; e.Gravity = 1f; e.BirthRate = 0f;
        e.Pos = Vector3.Zero; e.PrevPos = Vector3.Zero; e.PrevRot = new Quat { W = 1f };
        e.Orient = new Quat { W = 1f }; Basis(ref e);
        s = Open(0, em); _tabN = 1;
        slot = ref _tab[0];
        Particle q = Take(ref slot);
        Clear(q);
        q.Vel = new Vector3(1f, 0f, 0f);
        q.InhVel = new Vector3(0f, 1f, 0f);
        q.InhDecay = 1f;
        Standard(ref slot, ref e, default, 0, IntPtr.Zero, 0.5f);
        check.Expect(Near(q.Pos, new Vector3(0.5f, 0.5f, 0f)),
          "one half-second step moves by velocity AND by the inherited velocity scaled by its own decay");
        check.Expect(MathF.Abs(q.Vel.Z - (-9.81f * 0.5f)) < 1e-4f,
          "gravity is mass * -9.81 * dt into the Z velocity, applied after the position");
        check.Expect(MathF.Abs(q.InhDecay - 0.5f) < 1e-6f,
          "the inherited velocity decays by |(1 - dt)| per step -- the engine's own frame-rate-dependent term");
        q.Splat = 1;
        Vector3 held = q.Pos;
        Standard(ref slot, ref e, default, 0, IntPtr.Zero, 0.5f);
        check.Expect(q.Pos == held, "a landed splat does not integrate at all");

        // ---- the p2p bezier's shape. t=0 must sit exactly on the emitter and t=1 exactly on the target, or
        // a chain starts or ends in mid-air; combineTime 0 means "on the curve from birth".
        Reset();
        e.Life = 1f; e.Gravity = 0f; e.CombineTime = 0f; e.Bezier2 = 0f; e.Bezier3 = 0f;
        e.Pos = new Vector3(1f, 2f, 3f); e.PrevPos = e.Pos;
        Vector3 c0 = e.Pos, c3 = new Vector3(5f, 2f, 3f);
        check.Expect(Near(CubeAt(c0, c0, c3, c3, 0f), c0) && Near(CubeAt(c0, c0, c3, c3, 1f), c3),
          "the p2p curve begins on the emitter and ends on the target");
        check.Expect(Near(CubeAt(c0, c0, c3, c3, 0.5f), (c0 + c3) * 0.5f),
          "...and with both control points on the ends it is a straight line, so midway is the midpoint");

        // ---- the lightning fractal. Determinism is the property that matters: one emitter, one seed, one
        // shape -- otherwise a bolt reshapes itself every frame and reads as noise rather than as lightning.
        Reset();
        e.BoltRadius = 1f; e.ZigZag = 0f;
        s = Open(0, em); _tabN = 1;
        slot = ref _tab[0];
        slot.Cap = 9;
        for (int i = 0; i < 9; i++) { Particle p = Take(ref slot); Clear(p); p.Pos = new Vector3(i, 0f, 0f); }
        uint seed = slot.Rng;
        Fractal(ref slot, ref e, Vector3.UnitY, Vector3.UnitZ, 4, 4, 1f);
        Vector3 a1 = At(ref slot, 4).Pos, a2 = At(ref slot, 2).Pos;
        check.Expect(a1.X == 4f && (a1.Y != 0f || a1.Z != 0f),
          "the fractal displaces the midpoint off the axis without moving it along it");
        check.Expect(a2 != new Vector3(2f, 0f, 0f), "...and recurses into the halves");
        slot.Rng = seed;
        for (int i = 0; i < 9; i++) At(ref slot, i).Pos = new Vector3(i, 0f, 0f);
        Fractal(ref slot, ref e, Vector3.UnitY, Vector3.UnitZ, 4, 4, 1f);
        check.Expect(At(ref slot, 4).Pos == a1 && At(ref slot, 2).Pos == a2,
          "the same seed gives the same bolt -- the stream is per-emitter, not shared with the engine's rand()");
        e.BoltRadius = 0f;
        for (int i = 0; i < 9; i++) At(ref slot, i).Pos = new Vector3(i, 0f, 0f);
        Fractal(ref slot, ref e, Vector3.UnitY, Vector3.UnitZ, 4, 4, 1f);
        check.Expect(At(ref slot, 4).Pos == new Vector3(4f, 0f, 0f),
          "a zero radius leaves a straight bolt rather than dividing by a zero range");

        // ---- the motion-blur trail and the roll wrap.
        Reset();
        s = Open(0, em); _tabN = 1;
        slot = ref _tab[0];
        slot.Cap = 4;
        e.Type = 6; e.BlurLength = 1f;
        Particle b = Take(ref slot);
        Clear(b);
        b.Pos = new Vector3(4f, 0f, 0f); b.PrevPos = Vector3.Zero;
        Blur(ref slot, ref e, 0.5f);
        check.Expect(Near(b.PrevPos, new Vector3(2f, 0f, 0f)),
          "render type 6's trailing point lerps toward the head over blurLength");
        e.Type = 0;
        b.PrevPos = Vector3.Zero;
        Blur(ref slot, ref e, 0.5f);
        check.Expect(b.PrevPos == Vector3.Zero, "...and no other render type pays for it");
        b.Roll = TwoPi - 0.1f;
        Roll(b, 1f, 0.5f);
        check.Expect(b.Roll > -TwoPi && b.Roll < TwoPi,
          "roll wraps into +-2pi instead of growing until sin loses its precision");

        // ---- the flag bit map. Eleven bits read as one word; one off-by-one there turns every emitter into
        // a differently wrong one, silently.
        check.Expect(FP2P == 1 && FP2PSel == 2 && FWind == 4 && FBounce == 0x10 && FRandom == 0x20
                     && FInherit == 0x40 && FInheritVel == 0x80 && FInheritLocal == 0x100
                     && FInheritPart == 0x400,
          "the MdlNodeEmitter flag bits match the Is* accessors' shifts (0,1,2,4,5,6,7,8,10)");

        // ---- the update-mode string compare, which is what picks the whole behaviour.
        check.Expect(Is(Lit("Fountain"), "Fountain") && !Is(Lit("Fountainhead"), "Fountain")
                     && !Is(Lit("Single"), "Fountain") && Is(Lit("Lightning"), "Lightning"),
          "the update mode matches the model's whole literal, not a prefix of it");

        // ---- the draw order. This is a DECISION with a history: the first cut sorted descending on a note
        // that called the engine's sort "back to front", and it is ascending. Getting it wrong recolours
        // every overlapping pair of alpha plumes and nothing else says so.
        Reset();
        _tabN = 4;
        for (int i = 0; i < 4; i++) { _tab[i] = default; _tab[i].Em = (IntPtr)(i + 1); }
        _tab[0].Dist = 9f; _tab[1].Dist = 1f; _tab[2].Dist = 5f; _tab[3].Dist = 5f;
        // The two at equal distance share a Gob, so the tiebreak reaches them; 3 declares the lower order.
        _tab[2].Gob = (IntPtr)0x1000; _tab[2].Order = 7;
        _tab[3].Gob = (IntPtr)0x1000; _tab[3].Order = 2;
        _orderN = 4;
        _order[0] = 0; _order[1] = 1; _order[2] = 2; _order[3] = 3;
        SortDrawOrder();
        check.Expect(_order[0] == 1 && _order[3] == 0,
          "the sort is ASCENDING squared distance -- NEAREST first, which is what the engine's comparator and "
          + "its forward draw loop do, not back to front");
        check.Expect(_order[1] == 3 && _order[2] == 2,
          "...and a tie between two emitters of one Gob breaks on the model's render order, ascending");
        _tab[3].Gob = (IntPtr)0x2000;
        _order[0] = 0; _order[1] = 1; _order[2] = 2; _order[3] = 3;
        SortDrawOrder();
        check.Expect(_order[1] == 2 && _order[2] == 3,
          "...but two UNRELATED emitters at one distance keep bucket order: the tiebreak needs a shared Gob");
        check.Expect(!Before(0, 0), "the comparator is irreflexive, or the insertion sort walks off the front");

        // ---- the bounce: the segment-triangle test and the reflection. Both are decisions of ours (the
        // engine asks Scene::HitCheck for the first and its three literals shape the second), and both fail
        // INVISIBLY -- a floor that never reports a hit looks exactly like a floor nobody bounced on.
        BounceWorld.Check(check);
        Reset();
        s = Open(0, em); _tabN = 1;
        slot = ref _tab[0];
        slot.Cap = 4;
        Floor(check, ref slot, 0.5f);

        // ---- the block's edge. The engine has NO per-emitter ceiling -- it births from the recycle list and,
        // when that is empty, from operator new with nothing to stop it (FountainEmitter::Update @0x4b1c70) --
        // so every refusal here costs a particle vanilla would have drawn, whatever sized the block. One that
        // counts nothing is the silent fallback CX-45 forbids, and it is invisible in a frame.
        Slot edge = default;
        int lost0 = _capLost;
        bool said0 = _capSaid;
        _capSaid = true;                                   // the one-shot line is not this check's business
        edge.Cap = 4; edge.Live = 4;
        check.Expect(Take(ref edge).IsNull && _capLost == lost0 + 1,
          "a birth refused by a full block is counted, whatever sized the block");
        check.Expect(Room(ref edge, 3) == 0 && _capLost == lost0 + 4,
          "...and a burst clamped ahead of the loop counts every birth it clamped away");
        edge.Live = 1;
        check.Expect(Room(ref edge, 3) == 3 && _capLost == lost0 + 4, "a burst that fits costs nothing");
        check.Expect(Room(ref edge, 5) == 3 && _capLost == lost0 + 6, "...and one that half fits counts the rest");
        _capLost = lost0; _capSaid = said0;

        // ---- and the sizing that keeps those refusals from arising. Each term comes off a birth loop: an
        // explosion's whole population lands in ONE step, so sizing it by rate x life alone -- which is what
        // it used to do -- lost most of every bang from an emitter with a lifespan under a second.
        check.Expect(Want(ModeSingle, 100f, 10f, 1f) == 1, "a single emitter is one particle whatever it declares");
        check.Expect(Want(ModeLightning, 12f, 3f, 0.1f) == 12, "a bolt is its fixed chain of birthRate links");
        check.Expect(Want(ModeExplosion, 50f, 0.5f, 0.016f) >= 50,
          "an explosion has room for its whole burst even when rate x life is a fraction of it");
        check.Expect(Want(ModeFountain, 10f, 4f, 0.1f) >= 42,
          "a fountain holds its standing population plus the burst one step can meter");
        check.Expect(Want(ModeFountain, 10f, 4f, 1f) > Want(ModeFountain, 10f, 4f, 0.016f),
          "...and a longer step meters a bigger burst, so the block grows with it");
        check.Expect(Want(ModeFountain, 0f, 0f, 0f) >= 1 && Want(ModeLightning, 0f, 0f, 0f) == 1,
          "nothing ever asks for a block of zero");
        // A grow ABANDONS the old block (the arena is a bump allocator), so an emitter whose controller ramps
        // birthrate would burn ~N^2/2 of the arena growing by ones and take every other emitter down with it
        // at the reset.
        check.Expect(Block(1) == 1 && Block(32) == 32 && Block(33) == 64 && Block(MaxPerEmitter) == MaxPerEmitter,
          "a block is a power of two, so a ramping emitter grows O(log N) times, not N");
        check.Expect(Block(0) == 1 && Block(-5) == 1, "...and is never zero or negative, whatever Want returned");

        Reset();
        NativeMemory.Free(em.ToPointer());
        log.Info(check.Ok ? "EmitterSim.SelfCheck: passed" : "EmitterSim.SelfCheck: FAILED");
    }

    /// <summary>A zeroed <c>LightningEmitter</c> and the <c>MdlNodeEmitter</c> it points at, big enough for
    /// every field this file reads or writes on either. One block, so one free.</summary>
    private static IntPtr Fake(out IntPtr node)
    {
        const int emSize = 0x280;                  // not-an-offset: sizeof(LightningEmitter), rounded up
        const int nodeSize = 0x200;                // not-an-offset: sizeof(MdlNodeEmitter), rounded up
        byte* p = (byte*)NativeMemory.Alloc(emSize + nodeSize);
        NativeMemory.Clear(p, emSize + nodeSize);
        node = (IntPtr)(p + emSize);
        new CX.Engine.Part((IntPtr)p).Node = new MdlNode(node);
        return (IntPtr)p;
    }

    /// <summary>One fountain frame, in <see cref="Advance"/>'s order with the mover left out: the mover reads
    /// a live Scene for wind and collision, and nothing this check asserts goes through it.</summary>
    private static void Tick(ref Slot slot, IntPtr em, ref PartEmitter e, MdlNodeEmitter node, float dt)
    {
        FountainAge(ref slot, ref e, dt);
        FountainBirth(ref slot, em, ref e, node, 0, dt);
    }

    /// <summary>Write a model's <c>update</c> literal into the fake node, which is what <see cref="Open"/>
    /// reads to pick the mode -- and therefore what a check has to set before it takes a slot rather than
    /// after.</summary>
    private static void Declare(MdlNodeEmitter node, string mode)
    {
        Span<byte> u = node.Update;
        for (int i = 0; i < u.Length; i++) u[i] = (byte)(i < mode.Length ? mode[i] : 0);
    }

    /// <summary>The reflection, driven through <see cref="Land"/> itself against a synthetic level floor:
    /// the grid needs a live Scene, the arithmetic does not, and the arithmetic is the part that is a
    /// decision. Calling the real function rather than a copy is the point -- a copy would keep passing
    /// after someone edited the original.</summary>
    private static void Floor(Checker check, ref Slot slot, float co)
    {
        Vector3 up = new(0f, 0f, 1f);
        Particle p = Take(ref slot);
        Clear(p);
        p.Vel = new Vector3(0.5f, 0f, -2f);
        Land(co, p, up, 1f / 60f);
        check.Expect(MathF.Abs(p.Vel.Z - 2f * BounceLoss * co) < 1e-5f
                     && MathF.Abs(p.Vel.X - 0.5f * BounceLoss) < 1e-5f,
          "a bounce off a level floor mirrors Z, keeps the tangent, and applies restitution to Z ONLY "
          + "(the flat 0.75 loss lands on all three)");
        check.Expect(p.Splat == 0,
          "a 0.5-restitution bounce off a 2/s impact is still moving, so the particle does not land");

        // At 10fps the frame-rate factor is its 0.5 floor -- clamp(0.033/dt, 0.5, 1) -- which is what stops
        // a stuttering frame launching debris, and the only way to see it is through the same function.
        Particle q = Take(ref slot);
        Clear(q);
        q.Vel = new Vector3(0f, 0f, -2f);
        Land(co, q, up, 1f / 10f);
        check.Expect(MathF.Abs(q.Vel.Z - 2f * BounceLoss * co * MinBounceScale) < 1e-5f,
          "...and at 10fps Z takes the 0.5 floor of that factor as well");

        Particle w = Take(ref slot);
        Clear(w);
        w.Vel = new Vector3(0f, 0f, -0.4f);
        Land(0.1f, w, up, 1f / 60f);
        check.Expect(w.Splat == 1 && MathF.Abs(w.Pos.Z - LandLift) < 1e-6f && w.SplatNormal == up,
          "a weak bounce falls under the squared-speed threshold: the splat freezes, lifts off the face and "
          + "takes the contact normal as its own");
        check.Expect(MathF.Abs(w.Quat.W - 1f) < 1e-5f,
          "...and its quad lies in the contact plane -- RotationArc from +Z onto a level floor is identity");

        Particle z = Take(ref slot);
        Clear(z);
        z.Vel = new Vector3(3f, 0f, -9f);
        Land(0f, z, up, 1f / 60f);
        check.Expect(z.Vel == Vector3.Zero && z.Splat == 1,
          "a zero restitution stops the particle dead and lands it, however fast it arrived");
    }

    private static Span<byte> Lit(string s)
    {
        byte[] b = new byte[s.Length + 1];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return b;
    }

    private static bool Near(Vector3 a, Vector3 b) => (a - b).Length() < 1e-4f;

    /// <summary>The cubic the p2p bezier mover evaluates, isolated so the check exercises the WEIGHTS rather
    /// than the engine calls around them.</summary>
    private static Vector3 CubeAt(Vector3 c0, Vector3 c1, Vector3 c2, Vector3 c3, float t)
    {
        float u = 1f - t, tt = t * t;
        return c0 * (u * u * u) + c1 * (3f * t * u * u) + c2 * (3f * tt * u) + c3 * (tt * t);
    }
}
