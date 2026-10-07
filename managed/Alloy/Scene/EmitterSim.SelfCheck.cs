using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    /// <summary>
    /// Off-device: every part of the simulation that is a DECISION rather than a read. The step is driven
    /// through an <see cref="Emitter"/> snapshot, which is what both sources produce, and the pool is the real
    /// one -- so the whole thing runs on a machine with no client and no GL.
    /// <para>CX-120 adds the check that matters most to this ticket: <see cref="BothSources"/> fills ONE
    /// parameter set through both fillers and runs the same fountain from each, so "the DB path behaves like
    /// the engine path" is a measurement rather than a claim.</para>
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
        Emitter e = default;
        e.Part = -1;
        e.M.Orient = new Quat { W = 1f };
        Basis(ref e);
        check.Expect(e.M.Bx == new Vector3(1f, 0f, 0f) && e.M.By == new Vector3(0f, 1f, 0f)
                     && e.M.Bz == new Vector3(0f, 0f, 1f),
          "the identity quaternion gives the identity spawn basis, column-major");
        float h = MathF.Sqrt(0.5f);
        e.M.Orient = new Quat { W = h, Z = h };                 // a quarter turn about Z
        Basis(ref e);
        check.Expect(Near(e.M.Bx, new Vector3(0f, 1f, 0f)) && Near(e.M.Bz, new Vector3(0f, 0f, 1f)),
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
        e = Params(ModeFountain);
        e.BirthRate = 3f; e.Life = 10f;
        int s = Slot0(in e);
        check.Expect(s == 0 && _tab[0].Mode == ModeFountain && _tab[0].Mover == MoveStandard,
          "the model's `update Fountain` with no p2p bit resolves to the fountain mode and the plain mover");
        check.Expect(_tab[0].Cap >= 32, "a 3/s emitter living 10s reserves room for its population");
        ref Slot slot = ref _tab[0];
        int frames = 0;
        // Age then birth, which is Advance's order minus the mover -- the mover needs a scene for wind and
        // collision and the accumulator is entirely FountainBirth's.
        while (slot.Live == 0 && frames < 60) { Tick(ref slot, ref e, 1f / 60f); frames++; }
        check.Expect(slot.Live == 1 && frames >= 19 && frames <= 21,
          "the accumulator births the first particle after ~1/3 s at 60fps, not on frame one and not never");
        for (int i = 0; i < 60; i++) Tick(ref slot, ref e, 1f / 60f);
        check.Expect(slot.Live == 4, "one more second of a 3/s emitter is three more particles");

        // ---- lifetime and death, and the ORDER death leaves behind. An index-domain fade and the linked
        // render type both read the live slice as oldest-first, so a swap-remove would recolour the chain.
        Reset();
        e = Params(ModeFountain);
        e.BirthRate = 0f; e.Life = 1f;
        s = Slot0(in e);
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
        e = Params(ModeFountain);
        e.Life = 10f; e.Gravity = 1f; e.BirthRate = 0f;
        s = Slot0(in e);
        slot = ref _tab[0];
        Particle q = Take(ref slot);
        Clear(q);
        q.Vel = new Vector3(1f, 0f, 0f);
        q.InhVel = new Vector3(0f, 1f, 0f);
        q.InhDecay = 1f;
        Standard(ref slot, ref e, IntPtr.Zero, 0.5f);
        check.Expect(Near(q.Pos, new Vector3(0.5f, 0.5f, 0f)),
          "one half-second step moves by velocity AND by the inherited velocity scaled by its own decay");
        check.Expect(MathF.Abs(q.Vel.Z - (-9.81f * 0.5f)) < 1e-4f,
          "gravity is mass * -9.81 * dt into the Z velocity, applied after the position");
        check.Expect(MathF.Abs(q.InhDecay - 0.5f) < 1e-6f,
          "the inherited velocity decays by |(1 - dt)| per step -- the engine's own frame-rate-dependent term");
        q.Splat = 1;
        Vector3 held = q.Pos;
        Standard(ref slot, ref e, IntPtr.Zero, 0.5f);
        check.Expect(q.Pos == held, "a landed splat does not integrate at all");

        // ---- the p2p bezier's shape. t=0 must sit exactly on the emitter and t=1 exactly on the target, or
        // a chain starts or ends in mid-air; combineTime 0 means "on the curve from birth".
        Vector3 c0 = new(1f, 2f, 3f), c3 = new(5f, 2f, 3f);
        check.Expect(Near(CubeAt(c0, c0, c3, c3, 0f), c0) && Near(CubeAt(c0, c0, c3, c3, 1f), c3),
          "the p2p curve begins on the emitter and ends on the target");
        check.Expect(Near(CubeAt(c0, c0, c3, c3, 0.5f), (c0 + c3) * 0.5f),
          "...and with both control points on the ends it is a straight line, so midway is the midpoint");

        // ---- the lightning fractal. Determinism is the property that matters: one emitter, one seed, one
        // shape -- otherwise a bolt reshapes itself every frame and reads as noise rather than as lightning.
        Reset();
        e = Params(ModeLightning);
        e.BoltRadius = 1f; e.ZigZag = 0f; e.BirthRate = 9f; e.Life = 1f;
        s = Slot0(in e);
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
        e = Params(ModeFountain);
        e.BirthRate = 0f; e.Life = 10f;
        s = Slot0(in e);
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
        // a differently wrong one, silently. The two the DRAW reads are in the same word, measured on
        // MdlNodeEmitter::IsTinted @0x4aafc0 (>> 3) and IsSplat @0x4ab070 (>> 9).
        check.Expect(FP2P == 1 && FP2PSel == 2 && FWind == 4 && FBounce == 0x10 && FRandom == 0x20
                     && FInherit == 0x40 && FInheritVel == 0x80 && FInheritLocal == 0x100
                     && FInheritPart == 0x400 && FTinted == 8 && FSplat == 0x200,
          "the MdlNodeEmitter flag bits match the Is* accessors' shifts (0,1,2,3,4,5,6,7,8,9,10)");

        // ---- the update-mode string compare, which is what picks the whole behaviour.
        check.Expect(Is(Lit("Fountain"), "Fountain") && !Is(Lit("Fountainhead"), "Fountain")
                     && !Is(Lit("Single"), "Fountain") && Is(Lit("Lightning"), "Lightning"),
          "the update mode matches the model's whole literal, not a prefix of it");
        // ...and the DB side reads the same four names out of the model's own string, case-insensitively,
        // because the ASCII corpus spells them both ways and the engine's compare is strcasecmp.
        check.Expect(ModeOf("fountain") == ModeFountain && ModeOf("EXPLOSION") == ModeExplosion
                     && ModeOf("Lightning") == ModeLightning && ModeOf("anything else") == ModeSingle,
          "the DB filler picks the same four modes off the parsed literal, case-insensitively");
        check.Expect(RenderTypeOf("Normal") == 0 && RenderTypeOf("Billboard_to_Local_Z") == 1
                     && RenderTypeOf("Billboard_to_World_Z") == 2 && RenderTypeOf("Linked") == 3
                     && RenderTypeOf("Aligned_to_World_Z") == 4 && RenderTypeOf("Aligned_to_Particle_Dir") == 5
                     && RenderTypeOf("Motion_Blur") == 6 && RenderTypeOf("") == 0,
          "...and the seven render types in PartEmitter::Initialize's own order, unknown falling through to 0");
        check.Expect(BlendOf("Normal") == 0 && BlendOf("Punch-Through") == 1 && BlendOf("Lighten") == 2,
          "...and the three blend modes: normal 0, punch-through 1, anything else additive");

        // ---- the draw order. This is a DECISION with a history: the first cut sorted descending on a note
        // that called the engine's sort "back to front", and it is ascending. Getting it wrong recolours
        // every overlapping pair of alpha plumes and nothing else says so.
        Reset();
        _tabN = 4;
        for (int i = 0; i < 4; i++) { _tab[i] = default; _tab[i].Part = -1; _tab[i].Em = (IntPtr)(i + 1); }
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
        e = Params(ModeFountain);
        e.BirthRate = 0f; e.Life = 10f;
        s = Slot0(in e);
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

        BothSources(check);

        Reset();
        log.Info(check.Ok ? "EmitterSim.SelfCheck: passed" : "EmitterSim.SelfCheck: FAILED");
    }

    /// <summary>CX-120: <b>the two fillers, over one emitter.</b> The same thirty parameters are stated twice
    /// -- once in a synthetic engine <c>PartEmitter</c>/<c>MdlNodeEmitter</c> pair and once as a parsed
    /// <see cref="EmitterDef"/> with the matching controller rest keys -- and the check asserts that the two
    /// snapshots agree field for field, and then that two hundred frames of the same fountain run from each
    /// produce the same particles.
    /// <para>This is the whole argument that CX-120 did not change mode 0: if the engine filler is a
    /// transcription and the step reads nothing but the snapshot, then the step cannot have changed. The
    /// field comparison pins the first half and the simulated run pins the second.</para></summary>
    private static void BothSources(Checker check)
    {
        Reset();
        DbInputs.Reset();

        // The parameter set, stated once. Deliberately awkward values: a rate that needs the accumulator, a
        // lifespan that reaps mid-run, a non-zero spawn rectangle and a spread so the RNG is exercised.
        const float Rate = 7f, Life = 1.5f, Vel = 3f, RandV = 0.5f, Spread = 0.8f, Spin = 2f, Mass = 1f,
                    XSize = 0.4f, YSize = 0.25f, Threshold = 0.3f, Blur = 0.6f, BoltDelay = 0.2f,
                    BoltRadius = 0.9f, BoltScale = 1.3f, Drag = 0.5f, Grav = 4f, Bez2 = 1f, Bez3 = 2f,
                    Comb = 3f, A0 = 0.9f, A1 = 0.1f, Fps = 12f, F0 = 1f, F1 = 5f,
                    SX0 = 0.5f, SX1 = 1.5f, SY0 = 0.2f, SY1 = 0.7f, Bnc = 0.4f;
        const int Flags = FRandom | FInheritVel;

        // ---- the engine side ----
        IntPtr em = Fake(out IntPtr nodePtr);
        var ev = new PartEmitter(em);
        var node = new MdlNodeEmitter(nodePtr);
        ev.BirthRate = Rate; ev.Life = Life; ev.Velocity = Vel; ev.RandVel = RandV; ev.Spread = Spread;
        ev.Spin = Spin; ev.Gravity = Mass; ev.SpawnX = XSize; ev.SpawnY = YSize; ev.DeadSpace = Threshold;
        ev.BlurLength = Blur; ev.BoltDelay = BoltDelay; ev.BoltRadius = BoltRadius; ev.ZigZag = BoltScale;
        ev.P2pDrag = Drag; ev.P2pGrav = Grav; ev.Bezier2 = Bez2; ev.Bezier3 = Bez3; ev.CombineTime = Comb;
        ev.AlphaStart = A0; ev.AlphaEnd = A1; ev.Fps = Fps; ev.FrameStart = F0; ev.FrameEnd = F1;
        ev.SizeStartX = SX0; ev.SizeEndX = SX1; ev.SizeStartY = SY0; ev.SizeEndY = SY1; ev.Bounce = Bnc;
        ev.ColorStart = new Vector3(1f, 0.5f, 0.25f); ev.ColorEnd = new Vector3(0f, 0.25f, 1f);
        ev.Type = 0; ev.Blend = 2; ev.LifeInterp = 1;
        ev.Pos = new Vector3(10f, 20f, 3f); ev.Orient = new Quat { W = 1f };
        new CX.Engine.Part(em).Scale = 1f;   // zeroed memory otherwise; a real Part is seeded to 1
        node.Flags = Flags;
        node.GridX = 4; node.GridY = 2; node.Loop = 1; node.NoCull = 1;
        node.ConeCap = 0.75f; node.WindStrength = 1.25f; node.WindFalloff = 2.5f;
        Declare(node, "Fountain");

        // ---- the database side ----
        ModelDef m = DbModel(Flags);
        DbId obj = SceneDb.Objects.Alloc();
        DbFeed.InitObject(obj.Index);
        DbId ph = SceneDb.Parts.Alloc();
        int part = ph.Index;
        DbFeed.InitPart(part);
        SceneDb.PartObj[part] = obj;
        SceneDb.PartModel[part] = m;
        SceneDb.PartNode[part] = 1;
        SceneDb.PartScale[part] = 1f;
        SceneDb.ObjScale[obj.Index] = 1f;
        SceneDb.PartWorld[part] = Matrix4x4.CreateTranslation(10f, 20f, 3f);
        SceneDb.ObjWorld[obj.Index] = SceneDb.PartWorld[part];
        SceneDb.ObjRot[obj.Index] = Quaternion.Identity;

        Emitter a = default, d = default;
        bool okA = FillEngine(em, ref a);
        bool okD = FillDb(part, ref d);
        check.Expect(okA && okD, "both fillers accept the same emitter");

        string? diff = FirstDiff(in a, in d);
        check.Expect(diff == null,
          "the engine views and the parsed model + controller rest keys fill the SAME snapshot -- which is "
          + $"what makes the DB path the same simulation and not a second one (first difference: {diff ?? "none"})");
        // The three field names that mean something else in the file. Getting any of them wrong is silent:
        // the emitter simply behaves as if the parameter were zero.
        check.Expect(d.ConeCap == 0.75f && d.WindStrength == 1.25f && d.WindFalloff == 2.5f,
          "the MDL's `deadspace`, `blastRadius` and `blastLength` are the engine's coneCap, windStrength and "
          + "windFalloff (node+0x9c/+0xa0/+0xa4, InternalParseField)");
        check.Expect(d.DeadSpace == Threshold,
          "...and the engine's deadSpace -- the p2p arrival radius -- is the MDL's `threshold` CONTROLLER "
          + "(file id 188 -> PartEmitter+0xd4)");

        // ---- and now the step itself, from each snapshot, over the same stream ----
        Reset();
        int sa = Slot0(in a);
        _tab[sa].Rng = 0x5eed;
        for (int i = 0; i < 200; i++) Tick(ref _tab[sa], ref a, 1f / 60f);
        int liveA = _tab[sa].Live;
        var posA = new Vector3[liveA];
        var ageA = new float[liveA];
        for (int i = 0; i < liveA; i++) { posA[i] = At(ref _tab[sa], i).Pos; ageA[i] = At(ref _tab[sa], i).Age; }

        Reset();
        int sd = Slot0(in d);
        _tab[sd].Rng = 0x5eed;
        for (int i = 0; i < 200; i++) Tick(ref _tab[sd], ref d, 1f / 60f);
        int liveD = _tab[sd].Live;
        bool match = liveA == liveD && liveA > 0;
        for (int i = 0; match && i < liveA; i++)
            match = At(ref _tab[sd], i).Pos == posA[i] && At(ref _tab[sd], i).Age == ageA[i];
        check.Expect(match,
          $"200 frames of one fountain run from either filler produce the same particles ({liveA} vs {liveD}) "
          + "-- bit for bit, including the spawn rectangle's and the launch cone's RNG draws");

        // The slot keys: a DB part index the database re-tenants must not inherit the dead emitter's block.
        uint gen = SceneDb.PartObj[part].Gen;
        int k1 = FindDb(part, gen);
        check.Expect(FindDb(part, gen) == k1, "a DB slot is found again by (part, generation)");
        int k2 = FindDb(part, gen + 1);
        check.Expect(k2 != k1 || _tab[k2].Live == 0,
          "...and a re-tenanted part index takes a fresh slot rather than the dead emitter's particles");
        ForgetDbPart(part);
        check.Expect(_tab[k1].Part < 0 || _tab[k2].Part < 0,
          "a destroyed part releases its slot on the edge, not on a stale sweep");

        SceneDb.Parts.Free(ph);
        SceneDb.Objects.Free(obj);
        SceneDb.Parts.TakeDirty();
        SceneDb.Objects.TakeDirty();
        DbInputs.Reset();
        Reset();
        NativeMemory.Free(em.ToPointer());
    }

    /// <summary>The first field on which two snapshots disagree, or null. Named rather than a bare boolean:
    /// a failing field comparison over fifty members is only actionable if it says WHICH member.</summary>
    private static string? FirstDiff(in Emitter a, in Emitter d)
    {
        if (a.Flags != d.Flags) return $"Flags {a.Flags:x} vs {d.Flags:x}";
        if (a.Mode != d.Mode) return $"Mode {a.Mode} vs {d.Mode}";
        if (a.Type != d.Type) return $"Type {a.Type} vs {d.Type}";
        if (a.Blend != d.Blend) return $"Blend {a.Blend} vs {d.Blend}";
        if (a.LifeInterp != d.LifeInterp) return $"LifeInterp {a.LifeInterp} vs {d.LifeInterp}";
        if (a.Loop != d.Loop) return $"Loop {a.Loop} vs {d.Loop}";
        if (a.NoCull != d.NoCull) return $"NoCull {a.NoCull} vs {d.NoCull}";
        if (a.Splat != d.Splat) return "Splat";
        if (a.Tinted != d.Tinted) return "Tinted";
        if (a.GridX != d.GridX) return $"GridX {a.GridX} vs {d.GridX}";
        if (a.GridY != d.GridY) return $"GridY {a.GridY} vs {d.GridY}";
        if (a.ConeCap != d.ConeCap) return $"ConeCap {a.ConeCap} vs {d.ConeCap}";
        if (a.WindStrength != d.WindStrength) return $"WindStrength {a.WindStrength} vs {d.WindStrength}";
        if (a.WindFalloff != d.WindFalloff) return $"WindFalloff {a.WindFalloff} vs {d.WindFalloff}";
        if (a.Chunk != d.Chunk) return "Chunk";
        if (a.BirthRate != d.BirthRate) return $"BirthRate {a.BirthRate} vs {d.BirthRate}";
        if (a.Life != d.Life) return $"Life {a.Life} vs {d.Life}";
        if (a.Velocity != d.Velocity) return $"Velocity {a.Velocity} vs {d.Velocity}";
        if (a.RandVel != d.RandVel) return $"RandVel {a.RandVel} vs {d.RandVel}";
        if (a.Spread != d.Spread) return $"Spread {a.Spread} vs {d.Spread}";
        if (a.Spin != d.Spin) return $"Spin {a.Spin} vs {d.Spin}";
        if (a.Gravity != d.Gravity) return $"Gravity {a.Gravity} vs {d.Gravity}";
        if (a.Bounce != d.Bounce) return $"Bounce {a.Bounce} vs {d.Bounce}";
        if (a.CombineTime != d.CombineTime) return $"CombineTime {a.CombineTime} vs {d.CombineTime}";
        if (a.P2pDrag != d.P2pDrag) return $"P2pDrag {a.P2pDrag} vs {d.P2pDrag}";
        if (a.P2pGrav != d.P2pGrav) return $"P2pGrav {a.P2pGrav} vs {d.P2pGrav}";
        if (a.Bezier2 != d.Bezier2) return $"Bezier2 {a.Bezier2} vs {d.Bezier2}";
        if (a.Bezier3 != d.Bezier3) return $"Bezier3 {a.Bezier3} vs {d.Bezier3}";
        if (a.DeadSpace != d.DeadSpace) return $"DeadSpace {a.DeadSpace} vs {d.DeadSpace}";
        if (a.SpawnX != d.SpawnX) return $"SpawnX {a.SpawnX} vs {d.SpawnX}";
        if (a.SpawnY != d.SpawnY) return $"SpawnY {a.SpawnY} vs {d.SpawnY}";
        if (a.BlurLength != d.BlurLength) return $"BlurLength {a.BlurLength} vs {d.BlurLength}";
        if (a.BoltDelay != d.BoltDelay) return $"BoltDelay {a.BoltDelay} vs {d.BoltDelay}";
        if (a.BoltRadius != d.BoltRadius) return $"BoltRadius {a.BoltRadius} vs {d.BoltRadius}";
        if (a.ZigZag != d.ZigZag) return $"ZigZag {a.ZigZag} vs {d.ZigZag}";
        if (a.AlphaStart != d.AlphaStart) return $"AlphaStart {a.AlphaStart} vs {d.AlphaStart}";
        if (a.AlphaEnd != d.AlphaEnd) return $"AlphaEnd {a.AlphaEnd} vs {d.AlphaEnd}";
        if (a.Fps != d.Fps) return $"Fps {a.Fps} vs {d.Fps}";
        if (a.FrameStart != d.FrameStart) return $"FrameStart {a.FrameStart} vs {d.FrameStart}";
        if (a.FrameEnd != d.FrameEnd) return $"FrameEnd {a.FrameEnd} vs {d.FrameEnd}";
        if (a.SizeStartX != d.SizeStartX) return $"SizeStartX {a.SizeStartX} vs {d.SizeStartX}";
        if (a.SizeEndX != d.SizeEndX) return $"SizeEndX {a.SizeEndX} vs {d.SizeEndX}";
        if (a.SizeStartY != d.SizeStartY) return $"SizeStartY {a.SizeStartY} vs {d.SizeStartY}";
        if (a.SizeEndY != d.SizeEndY) return $"SizeEndY {a.SizeEndY} vs {d.SizeEndY}";
        if (a.ColorStart != d.ColorStart) return $"ColorStart {a.ColorStart} vs {d.ColorStart}";
        if (a.ColorEnd != d.ColorEnd) return $"ColorEnd {a.ColorEnd} vs {d.ColorEnd}";
        if (a.WorldPos != d.WorldPos) return $"WorldPos {a.WorldPos} vs {d.WorldPos}";
        if (a.Scale != d.Scale) return $"Scale {a.Scale} vs {d.Scale}";
        if (a.PartScale != d.PartScale) return $"PartScale {a.PartScale} vs {d.PartScale}";
        return null;
    }

    /// <summary>The database twin of <see cref="BothSources"/>'s engine emitter: one emitter node whose
    /// <see cref="EmitterDef"/> and controller REST keys state the same parameter set.</summary>
    private static ModelDef DbModel(int flags)
    {
        var m = new ModelDef { Name = "cx_bothsrc", ContentHash = 0xB0D15 };
        var root = new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy };
        var emit = new NodeDef
        {
            Name = "puff", Index = 1, Parent = 0, Kind = NodeKind.Emitter,
            Emitter = new EmitterDef
            {
                Update = "Fountain", Render = "Normal", Blend = "Lighten", Texture = "fxpa_flame",
                XGrid = 4, YGrid = 2, Loop = true, TwoSidedTex = true, Flags = (uint)flags,
                DeadSpace = 0.75f, BlastRadius = 1.25f, BlastLength = 2.5f,
            },
            Controllers = new[]
            {
                Ctl1(Ctl.EmBirthRate, 7f), Ctl1(Ctl.EmLifeExp, 1.5f), Ctl1(Ctl.EmVelocity, 3f),
                Ctl1(Ctl.EmRandVel, 0.5f), Ctl1(Ctl.EmSpread, 0.8f), Ctl1(Ctl.EmParticleRot, 2f),
                Ctl1(Ctl.EmMass, 1f), Ctl1(Ctl.EmXSize, 0.4f), Ctl1(Ctl.EmYSize, 0.25f),
                Ctl1(Ctl.EmThreshold, 0.3f), Ctl1(Ctl.EmBlurLength, 0.6f),
                Ctl1(Ctl.EmLightningDelay, 0.2f), Ctl1(Ctl.EmLightningRadius, 0.9f),
                Ctl1(Ctl.EmLightningScale, 1.3f), Ctl1(Ctl.EmDrag, 0.5f), Ctl1(Ctl.EmGrav, 4f),
                Ctl1(Ctl.EmP2PBezier2, 1f), Ctl1(Ctl.EmP2PBezier3, 2f), Ctl1(Ctl.EmCombineTime, 3f),
                Ctl1(Ctl.EmAlphaStart, 0.9f), Ctl1(Ctl.EmAlphaEnd, 0.1f), Ctl1(Ctl.EmFps, 12f),
                Ctl1(Ctl.EmFrameStart, 1f), Ctl1(Ctl.EmFrameEnd, 5f),
                Ctl1(Ctl.EmSizeStart, 0.5f), Ctl1(Ctl.EmSizeEnd, 1.5f),
                Ctl1(Ctl.EmSizeStartY, 0.2f), Ctl1(Ctl.EmSizeEndY, 0.7f), Ctl1(Ctl.EmBounceCo, 0.4f),
                Ctl3(Ctl.EmColorStart, 1f, 0.5f, 0.25f), Ctl3(Ctl.EmColorEnd, 0f, 0.25f, 1f),
            },
        };
        m.Nodes = new[] { root, emit };
        m.NodeByName["root"] = 0; m.NodeByName["puff"] = 1;
        return m;
    }

    private static ControllerTrack Ctl1(int type, float v)
        => new() { Type = type, Columns = 1, Times = new[] { 0f }, Values = new[] { v } };

    private static ControllerTrack Ctl3(int type, float x, float y, float z)
        => new() { Type = type, Columns = 3, Times = new[] { 0f }, Values = new[] { x, y, z } };

    /// <summary>A snapshot with the fixed declarations a check needs and nothing else: an owner at the origin,
    /// the identity orientation, no target and no tile fade.</summary>
    private static Emitter Params(int mode)
    {
        Emitter e = default;
        e.Part = -1;
        e.Mode = mode;
        e.Scale = 1f;
        e.PartScale = 1f;
        e.M.Orient = new Quat { W = 1f };
        e.M.PrevRot = new Quat { W = 1f };
        Basis(ref e);
        return e;
    }

    /// <summary>Open slot 0 for this snapshot, as the walk would. Returns its index.</summary>
    private static int Slot0(in Emitter e)
    {
        _tabN = 1;
        _tab[0] = default;
        _tab[0].Part = e.Part;
        _tab[0].Em = e.Em;
        _tab[0].Rng = 0x1234567u;
        Open(ref _tab[0], in e);
        return 0;
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
    private static void Tick(ref Slot slot, ref Emitter e, float dt)
    {
        FountainAge(ref slot, ref e, dt);
        FountainBirth(ref slot, ref e, dt);
    }

    /// <summary>Write a model's <c>update</c> literal into the fake node, which is what
    /// <see cref="FillEngine"/> reads to pick the mode.</summary>
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
