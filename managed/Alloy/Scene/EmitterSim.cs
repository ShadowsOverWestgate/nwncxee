using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// CX-86: the particle simulation, Alloy's. Replaces <c>Scene::DoEmitterBucket</c> @0x476f00 -- the walk, the
/// per-emitter step and the camera-distance sort -- so no engine emitter code runs on an Alloy frame (CX-81
/// tier 1). ADR 0018's CX-86 amendment moves the boundary; what the engine still owns is listed there.
/// <para><b>Why the replay could not stay.</b> <c>DoEmitterBucket</c> is the only caller of
/// <c>PartEmitter::AnimateParticles</c> @0x4ac660, and that call walks on into
/// <c>RenderStandard</c>/<c>RenderLinked</c>/<c>RenderMotionBlurred</c>, whose draws the funnels eat. The
/// simulation and the draw are one engine call; taking the draw means taking the simulation.</para>
/// <para><b>The pool is laid out AS the engine's <c>Particle</c></b> -- the same 0x78 bytes at the same
/// offsets, minus a vtable that nothing reads. That is not nostalgia: it makes <see cref="Particles"/>'s
/// build loop indifferent to who stepped the particles (it is handed a <c>Particle*</c> array either way),
/// so the whole simulation is A/B-able against the engine's on one knob, and the vertex build stays one
/// piece of code rather than two.</para>
/// <para><b>Zero allocation per frame.</b> Records and their pointer table live in two arenas, grown at load
/// edges only; an emitter owns a contiguous block, and spawn/death move POINTERS inside its own slice
/// (live in order at the front, free at the back) the way the engine's <c>DelIndex</c> + free list did.
/// Nothing is allocated, copied or hashed per particle per frame here.</para>
/// <para><b>Order is load-bearing.</b> The live slice is oldest-first, because an emitter with
/// <c>lifeInterp 0</c> fades over a particle's INDEX and the linked and lightning render types read
/// neighbours as chain segments. Death compacts stably; it never swap-removes.</para>
/// </summary>
internal static unsafe partial class EmitterSim
{
    /// <summary>Emitters tracked at once. Past this an emitter is not stepped and draws nothing, which is
    /// the same cost the draw table's own cap has (<c>Particles</c> MaxEmitters).</summary>
    private const int MaxEmitters = 4096;
    /// <summary>Particles one emitter may hold. The engine has NO ceiling at all: every Update births from
    /// the recycle list at <c>emitter+0x128</c> and, when that is empty, calls <c>operator new</c>
    /// unconditionally (<c>FountainEmitter::Update</c> @0x4b1c70, and the same shape in
    /// <c>SingleEmitter</c>/<c>ExplosionEmitter</c>) -- <c>birthRate x life</c> is an emergent steady state
    /// there, not a bound. So this is purely ours, and every birth it refuses is a particle vanilla would
    /// have drawn: loud once and counted, never quiet (CX-45).
    /// <para>Derived rather than picked: it is the share of the arena one emitter may take before it starves
    /// every other emitter in the area, <see cref="MaxSlots"/>/64 -- 480KB of the arena's 30MB.</para></summary>
    private const int MaxPerEmitter = MaxSlots / 64;
    /// <summary>Pool growth, in particles: start here, double until the frame fits, stop at the cap (0x78
    /// bytes each, so the cap is 30 MiB) and start the arena over rather than truncating one emitter.</summary>
    private const int MinSlots = 4096, MaxSlots = 1 << 18;

    /// <summary>Two turns, the wrap every roll and every random angle is taken modulo. <c>6.2831855f</c> is
    /// the engine's own literal, not <c>MathF.Tau</c> rounded differently.</summary>
    private const float TwoPi = 6.2831855f;
    /// <summary>Gravity, as the engine spells it: <c>mass * -9.81 * dt</c> added to the Z velocity every
    /// step, in every one of the three movers.</summary>
    private const float Gravity = -9.81f;
    /// <summary>The engine's two random quantisations: an angle or a distance is drawn as
    /// <c>rand() % (int)(x * 100)</c> hundredths, and a spawn offset as ten-thousandths.</summary>
    private const float Centi = 0.01f, Deci4 = 0.0001f;
    /// <summary>The floor under a "does this value do anything" test: the engine compares against 0.01
    /// before it divides a range into hundredths, because <c>rand() % 0</c> would trap.</summary>
    private const float Epsilon = 0.01f;
    /// <summary>The full circle, in the hundredths of a radian the spawn's azimuth draw works in
    /// (<c>rand() % 0x274</c> in <c>Particle::randomDirection</c> @0x4ab160 -- 628, i.e. 2π).</summary>
    private const int AzimuthTicks = 628;
    /// <summary>Quarter turn. <c>randomDirection</c>'s cone clamp measures against
    /// <c>cos(HalfPi - coneCap)</c> and folds an out-of-cone direction back through it.</summary>
    private const float HalfPi = 1.5707964f;
    /// <summary>The fallback interpolation rate for an emitter with no lifetime, and the cone clamp's own
    /// iteration ceiling -- the engine's loop is unbounded and can only terminate on float luck.</summary>
    private const int MaxConeFolds = 8;

    // ---- the emitter's update mode, as MdlNodeEmitter::InternalCreateInstance @0x4af5c0 picks it ----
    private const int ModeFountain = 0, ModeSingle = 1, ModeExplosion = 2, ModeLightning = 3;
    // ---- the mover, as PartEmitter::Initialize @0x4aaaa0 picks it ----
    private const int MoveStandard = 0, MoveBezier = 1, MoveGrav = 2;

    /// <summary>The <c>MdlNodeEmitter</c> flag bits, each one an <c>Is*</c> accessor in the binary
    /// (<see cref="EngineLayout.EmitterFlags"/> lists them). Read as a word once per emitter per frame
    /// instead of through eleven virtual calls.</summary>
    private const int FP2P = 1 << 0, FP2PSel = 1 << 1, FWind = 1 << 2,
                      FBounce = 1 << 4, FRandom = 1 << 5, FInherit = 1 << 6, FInheritVel = 1 << 7,
                      FInheritLocal = 1 << 8, FInheritPart = 1 << 10;

    /// <summary>One tracked emitter. The mode, the mover and the chunk verdict are the node's and never
    /// change, so they are resolved once when the slot is taken rather than per frame.</summary>
    private struct Slot
    {
        public IntPtr Em;
        /// <summary>This emitter's block: <see cref="Base"/> records in the pool and the same number of
        /// entries in the pointer table, <see cref="Live"/> of them live and in order.</summary>
        public int Base, Cap, Live;
        public int Mode, Mover;
        /// <summary>The particles are <c>CAurObject</c>s, so this emitter stays the engine's (ADR 0018's
        /// CX-86 amendment): Alloy calls <c>AnimateParticles</c> and <c>RenderNothing</c> on it instead.
        /// </summary>
        public bool Chunk;
        /// <summary>Frame last seen in the bucket, so a slot an area walked away from can be reclaimed --
        /// an emitter that is merely out of view never destructs.</summary>
        public int Seen;
        /// <summary>Squared camera distance, the sort's primary key.</summary>
        public float Dist;
        /// <summary>The sort's TIEBREAK, both halves of it: the owning Gob (two emitters only tie-break
        /// against each other when they share one) and the model's declared render order. Both are fixed for
        /// the emitter's life, so they are read once when the slot is taken.</summary>
        public IntPtr Gob;
        public short Order;
        /// <summary>Per-emitter random state. Seeded from the pointer so two copies of one effect do not
        /// spawn in lockstep, and advanced only here so a SelfCheck can drive the whole simulation
        /// deterministically.</summary>
        public uint Rng;
    }

    private static readonly Slot[] _tab = new Slot[MaxEmitters];
    private static int _tabN;
    /// <summary>Bucket indices in DRAW order -- back to front, which is what the blended class needs.</summary>
    private static readonly int[] _order = new int[MaxEmitters];
    private static int _orderN;

    private static byte* _pool;
    private static IntPtr* _ptrs;
    private static int _slotCap, _slotTop;

    private static ExtenderLog? _log;
    private static EngineList _bucket;
    private static float* _dt;
    private static PartGetWorldFn _getWorld;
    private static AnimateParticlesFn _animate;
    private static RenderNothingFn _renderNothing;
    private static IntPtr* _currentCamera;

    /// <summary>Resolved and ready to take the walk? <see cref="RspShim"/> chains
    /// <see cref="Missing"/> into its own stand-down list, so a false here takes the WHOLE Alloy path with
    /// it and the frame is vanilla -- rather than leaving an Alloy frame with the engine's emitter code
    /// running inside it, which is what CX-81 tier 1 exists to remove.</summary>
    internal static bool Armed { get; private set; }
    /// <summary>The first symbol that did not resolve, or null. Read by <see cref="RspShim.Install"/>, the
    /// way <see cref="DoorFade"/> and <see cref="LightFade"/> are.</summary>
    internal static string? Missing { get; private set; }
    private static bool _poolWarned;

    private static int _frame;
    private static int _emitters, _particles, _spawned, _died, _chunk, _bounced, _p2pNoTarget,
                       _capped, _poolResets, _reclaimed, _full, _foreign;
    /// <summary>The largest block any emitter's own parameters asked for this period, and births actually
    /// LOST to a full block -- the pair that says whether our ceiling costs a visible particle.</summary>
    private static int _capPeak, _capLost;
    private static bool _capSaid;

    /// <summary>Births a full block refused. EVERY one of them: the engine has no per-emitter ceiling
    /// (<c>operator new</c> on an empty recycle list, <c>FountainEmitter::Update</c> @0x4b1c70), so a refusal
    /// here is always a particle vanilla would have drawn, whether the block was sized by the emitter's own
    /// parameters or clamped by <see cref="MaxPerEmitter"/>. Loud once, counted after (CX-45).</summary>
    private static void CapLost(int n)
    {
        if (n <= 0) return;
        _capLost += n;
        if (_capSaid) return;
        _capSaid = true;
        _log?.Always($"EmitterSim: an emitter's block is full and it is losing births -- {n} on this step. Its "
                   + $"parameters asked for {_capPeak} particles against our {MaxPerEmitter} ceiling; vanilla "
                   + "has no ceiling at all. Raise MaxPerEmitter to the peak the [emitsim] line reports, or "
                   + "widen Want if the burst is what overran it. Counted, not repeated, from here on");
    }

    /// <summary>Clamp a birth burst to what the block can still hold, counting what that costs.</summary>
    private static int Room(ref Slot slot, int births)
    {
        int room = slot.Cap - slot.Live;
        if (room < 0) room = 0;
        if (births <= room) return births;
        CapLost(births - room);
        return room;
    }

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _bucket = new EngineList(Native.Sym(SymbolMap.EmitterBucket));
        _dt = (float*)Native.Sym(SymbolMap.DeltaT);
        _currentCamera = (IntPtr*)Native.Sym(SymbolMap.CurrentCamera);
        _getWorld = EngineFn.PartGetWorld;
        _animate = EngineFn.AnimateParticles;
        _renderNothing = EngineFn.RenderNothing;
        AurXform.Resolve();
        BounceWorld.Install(log);

        // Item 7 of the standards review: this used to name CurrentScene for an unresolved CAMERA.
        // CX-104: emitterbucket is NOT on this list any more. The population is SceneParts' registry, and the
        // bucket is kept resolved only for the /buckets emitters 0 A/B and the validator -- so a global that
        // does not resolve no longer stands the whole Alloy path down over a list nothing reads.
        Missing = _dt == null ? SymbolMap.DeltaT
                : _currentCamera == null ? SymbolMap.CurrentCamera
                : _getWorld == null ? SymbolMap.PartGetWorld
                : _animate == null ? SymbolMap.AnimateParticles
                : _renderNothing == null ? SymbolMap.RenderNothing
                : !AurXform.Ready ? AurXform.Missing : null;
        if (Missing != null)
        {
            log.Always($"EmitterSim: {Missing} did not resolve -- the owned simulation cannot run, so the "
                     + "whole Alloy path stands down (RspShim chains this) and the engine renders the frame. "
                     + "Handing the walk back mid-frame is not on offer: that would put the engine's emitter "
                     + "code back inside an Alloy frame, which is what CX-81 tier 1 removes");
            return;
        }
        Armed = true;
        log.Info($"EmitterSim: armed and {(Enabled.V ? "ON" : "IDLE")} -- Scene::DoEmitterBucket replaced "
               + "(/emitsim enabled 0 hands the walk back to the engine)");
    }

    // ---- the walk ------------------------------------------------------------------------------------

    /// <summary>What <see cref="RspShim"/> calls where it called <c>sc.DoEmitterBucket()</c>. Still BEFORE
    /// the frame anchor, so this frame's draw gets this frame's particles (#261's reason, unchanged).</summary>
    internal static void Step(CX.Engine.Scene sc, IntPtr scene)
    {
        _frame++;
        // The knob is the ONE route back to the engine's walk, and it is a deliberate A/B rather than a
        // fallback: an unresolved symbol stood the whole Alloy path down back in Install (see Missing).
        if (!Enabled.V) { sc.DoEmitterBucket(); return; }
        if (!Armed) return;
        // Defensive: RenderSinglePass is world-only by construction (Scene+0x188), so a GUI panel cannot
        // reach here. If one ever did, its emitters are ITS scene's and go undrawn rather than replayed --
        // calling the engine's walk would draw an inventory model's sparks over the map.
        if (!AlloyRenderer.IsWorldScene(scene)) { _foreign++; return; }

        // CX-104: the population is Alloy's own registry of the AREA's emitters, with the engine's own
        // per-emitter in-view test applied to it here (see InView). `emitterbucket` was the frame's in-view
        // slice of the same set, refilled by the BSP visitor @0x481000 -- which made this walk's membership a
        // side effect of the engine's traversal and of anything else that appended to that global.
        bool own = Buckets.Emitters.V && SceneParts.Ready;
        if (!own && _bucket.IsNull) { _orderN = 0; return; }
        ReadOnlySpan<IntPtr> registry = SceneParts.Emitters;
        IntPtr* items = own ? null : (IntPtr*)_bucket.Items;
        int n = own ? registry.Length : _bucket.Count;
        if (own && n == 0 && !_bucket.IsNull) SceneParts.WarnEmpty(BucketValidator.KindEmitter, 0, _bucket.Count);
        if (n <= 0) { _orderN = 0; return; }
        float dt = *_dt;
        // The engine's own gate: a scene that is not animating holds every emitter except the ones whose gob
        // forces animation. Holding means "do not step", NOT "do not draw" -- the sort and the enrolment
        // still run, or a paused game loses its particles rather than freezing them.
        bool animating = sc.IsAnimating() != 0;
        IntPtr cam = AlloyCamera.World;
        Vector3 eye = cam != IntPtr.Zero ? new CX.Engine.Camera(cam).Pos : Vector3.Zero;
        bool cull = own && BuildViewPlanes();

        // Cold, and deliberately here rather than inside a mover: a tile's collision set is built between
        // emitters, never in the middle of one's integration.
        if (Bouncing.V) BounceWorld.Pump(scene);
        _orderN = 0;
        _inView = 0; _outOfView = 0; _selN = 0;
        bool record = own && Buckets.Validate.V;
        for (int i = 0; i < n; i++)
        {
            IntPtr em = own ? registry[i] : items[i];
            if (em == IntPtr.Zero) continue;
            if (cull && !InView(em)) { _outOfView++; continue; }
            _inView++;
            if (record && _selN < MaxEmitters) _selected[_selN++] = em;
            int s = Find(em);
            if (s < 0) { _full++; continue; }
            ref Slot slot = ref _tab[s];
            slot.Seen = _frame;

            var gob = new CX.Engine.Part(em).Gob;
            bool step = animating || (!gob.IsNull && gob.IsForceAnimated() != 0);
            if (slot.Chunk)
            {
                // The engine's, by decision: these particles are gobs. Neither call draws.
                if (step) _animate(em, dt);
                _renderNothing(em);
                _chunk++;
                if (new PartEmitter(em).Count > 0) _chunkLive++;
                continue;
            }
            int fade0 = _fadeExit, rate0 = _rate0;
            if (step) Advance(ref slot, em, scene, dt);
            else _held++;
            // idle is what neither named exit explains: a stepped emitter left empty for another reason
            if (slot.Live <= 0) { if (step && _fadeExit == fade0 && _rate0 == rate0) _idle++; continue; }

            Vector3 at = gob.IsNull ? new CX.Engine.Part(em).Pos : gob.Position;
            slot.Dist = (eye - at).LengthSquared();
            if (_orderN < MaxEmitters) _order[_orderN++] = s;
            _emitters++;
            _particles += slot.Live;
        }

        if (_dump) { _dump = false; DumpGobs(sc, eye); }
        SortDrawOrder();
        for (int i = 0; i < _orderN; i++)
        {
            ref Slot slot = ref _tab[_order[i]];
            Particles.Enroll(slot.Em, scene, _ptrs + slot.Base, slot.Live);
        }
        Reclaim();
        if (own && Buckets.Validate.V)
            BucketValidator.Compare(BucketValidator.KindEmitter, new ReadOnlySpan<IntPtr>(_selected, 0, _selN),
                                    _bucket);
    }

    /// <summary>The in-view set this frame, for the validator alone -- the walk above cannot hand the
    /// validator a span of what it kept without recording it, and recording it is one store per emitter.
    /// </summary>
    private static readonly IntPtr[] _selected = new IntPtr[MaxEmitters];
    private static int _selN;
    private static int _inView, _outOfView;
    /// <summary>The silent exits, period totals, disjoint: the tile-fade exit, a fountain whose birth rate truncates
    /// to 0, and a stepped emitter left empty for neither reason; beside them the emitters held (in view, the scene
    /// not animating -- drawn, not stepped) and the chunk emitters whose engine-side list holds anything after their
    /// step. A flame that never shows is one of these.</summary>
    private static int _held, _idle, _fadeExit, _rate0, _chunkLive;

    /// <summary>Six inward-facing camera planes, rebuilt once per <see cref="Step"/>. False = no camera to
    /// build them from, and every registered emitter is stepped -- an instrument that cannot see may not hide
    /// the world.</summary>
    private static readonly float[] _viewPlanes = new float[24];
    private static bool BuildViewPlanes()
    {
        if (!FrameInputs.HasCamera || !FrameInputs.HasPerspective) return false;
        fixed (float* planes = _viewPlanes)
            ShadowMath.ReceiverPlanes(FrameInputs.CameraPos, FrameInputs.SceneCamFwd, FrameInputs.CamFovy,
                                      FrameInputs.CamAspect, FrameInputs.CamNear, FrameInputs.CamFar, 0f,
                                      new Span<float>(planes, 24));
        return true;
    }

    /// <summary>The engine's own emitter cull, <c>PartOutside(PartEmitter*, List&lt;Plane&gt;&amp;)</c>
    /// @0x47a290: the emitter's WORLD position (the <c>Part::GetWorld</c> vcall, taken fresh rather than off
    /// last frame's cached <c>PartEmitter+0x1e8</c>) against its own view radius at
    /// <see cref="EngineLayout.EmViewRadius"/>, one sphere-vs-plane test per plane. Finer than the engine's,
    /// which also required the emitter's whole BSP node to be in the frustum.</summary>
    private static bool InView(IntPtr em)
    {
        Vector3 pos;
        Quat rot;
        _getWorld(em, &pos, (float*)&rot);
        float r = new PartEmitter(em).ViewRadius;
        fixed (float* planes = _viewPlanes)
            for (int i = 0; i < 6; i++)
            {
                float d = planes[i * 4] * pos.X + planes[i * 4 + 1] * pos.Y + planes[i * 4 + 2] * pos.Z
                        + planes[i * 4 + 3];
                if (d < -r) return false;
            }
        return true;
    }

    /// <summary>The engine's own draw order, which is <c>FUN_0055f140</c> @0x45f140 exactly: ASCENDING
    /// squared camera distance -- nearest first -- and, when two emitters tie and share a Gob, ascending
    /// <see cref="EngineLayout.EmitterRenderOrder"/>.
    /// <para><b>A correction, and it is visible.</b> CX-86's first cut sorted DESCENDING, on the strength of
    /// a note that called the engine's sort "back to front". It is not: the comparator returns true for
    /// <c>a.dist &lt; b.dist</c>, the insertion-sort fixup moves an element back while its distance is
    /// smaller, and <c>DoEmitterBucket</c>'s draw loop walks the result FORWARD. So vanilla composites
    /// particles near-to-far, and since <see cref="Particles"/> keeps this order for its alpha-blended class,
    /// sorting the other way changed how every overlapping pair of plumes read. This restores it.</para>
    /// <para>Insertion sort over the index array: the bucket is tens of entries, nearly sorted frame to
    /// frame, and it allocates nothing -- which <c>Array.Sort</c> with a comparison would not manage.</para>
    /// </summary>
    private static void SortDrawOrder()
    {
        for (int i = 1; i < _orderN; i++)
        {
            int v = _order[i];
            int j = i - 1;
            while (j >= 0 && Before(v, _order[j])) { _order[j + 1] = _order[j]; j--; }
            _order[j + 1] = v;
        }
    }

    /// <summary>Does <paramref name="a"/> draw before <paramref name="b"/>? The engine's comparator, term for
    /// term. The Gob test is what keeps the tiebreak local: two unrelated emitters at one distance are left
    /// in bucket order, exactly as vanilla leaves them.</summary>
    private static bool Before(int a, int b)
    {
        float da = _tab[a].Dist, db = _tab[b].Dist;
        if (da != db) return da < db;
        IntPtr g = _tab[a].Gob;
        if (g == IntPtr.Zero || g != _tab[b].Gob) return false;
        return _tab[a].Order < _tab[b].Order;
    }

    /// <summary>This emitter's slot, taking a fresh one if it has none. Reuses a tombstone first, then grows,
    /// then reclaims the emitter longest unseen -- an emitter left behind by the camera never destructs, so
    /// without that last step one busy area would fill the table permanently.</summary>
    private static int Find(IntPtr em)
    {
        int free = -1, stale = -1, oldest = int.MaxValue;
        for (int i = 0; i < _tabN; i++)
        {
            if (_tab[i].Em == em) return i;
            if (_tab[i].Em == IntPtr.Zero) { if (free < 0) free = i; }
            else if (_tab[i].Seen < oldest) { oldest = _tab[i].Seen; stale = i; }
        }
        int s;
        if (free >= 0) s = free;
        else if (_tabN < MaxEmitters) s = _tabN++;
        else if (stale >= 0 && oldest < _frame - 1) { s = stale; _reclaimed++; }
        else return -1;
        return Open(s, em);
    }

    /// <summary>Resolve everything about the emitter that comes from its model and never changes, and take
    /// its pool block.</summary>
    private static int Open(int s, IntPtr em)
    {
        ref Slot slot = ref _tab[s];
        slot = default;
        slot.Em = em;
        slot.Seen = _frame;
        // A per-emitter seed, not a shared stream: two torches on one wall must not birth in lockstep, and a
        // SelfCheck must be able to drive one emitter reproducibly.
        slot.Rng = Mix((uint)(ulong)em ^ 0x9e3779b9u);

        var node = new MdlNodeEmitter(new CX.Engine.Part(em).Node.Ptr);
        if (node.IsNull) { slot.Em = IntPtr.Zero; return -1; }
        slot.Gob = new CX.Engine.Part(em).Gob.Ptr;
        slot.Order = node.RenderOrder;
        slot.Mode = ModeOf(node);
        int flags = node.Flags;
        slot.Mover = (flags & FP2P) == 0 ? MoveStandard
                   : (flags & FP2PSel) != 0 ? MoveBezier : MoveGrav;
        slot.Chunk = node.ChunkModel[0] != 0;
        // dt 0: the steady-state size. Advance grows again with the frame's own dt before the first birth,
        // so the burst headroom is in place before anything can ask for it.
        if (!slot.Chunk && !Grow(ref slot, em, 0f)) { slot.Em = IntPtr.Zero; return -1; }
        return s;
    }

    /// <summary>The update mode, from the model's own <c>update</c> literal -- the same four-way string
    /// compare <c>MdlNodeEmitter::InternalCreateInstance</c> @0x4af5c0 makes to choose the subclass. Read
    /// from the string rather than from the instance's vtable because <c>nwmain.exe</c> exports no vftables.
    /// </summary>
    private static int ModeOf(MdlNodeEmitter node)
    {
        Span<byte> u = node.Update;
        if (Is(u, "Fountain")) return ModeFountain;
        if (Is(u, "Explosion")) return ModeExplosion;
        if (Is(u, "Lightning")) return ModeLightning;
        return ModeSingle;
    }

    private static bool Is(Span<byte> s, string lit)
    {
        for (int i = 0; i < lit.Length; i++)
            if (i >= s.Length || s[i] != (byte)lit[i]) return false;
        return s.Length <= lit.Length || s[lit.Length] == 0;
    }

    /// <summary>xorshift32. The engine uses <c>rand()</c>; this does not, on purpose -- a per-emitter stream
    /// keeps the SelfChecks deterministic and keeps the simulation off a global the engine also draws from.
    /// </summary>
    private static uint Mix(uint x)
    {
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        return x == 0 ? 0x1234567u : x;
    }

    /// <summary><c>rand() % n</c>, the engine's own quantiser, with the trap it does not have: <c>n</c> at or
    /// below zero yields zero rather than dividing by it.</summary>
    private static int Rnd(ref uint st, int n)
    {
        st = Mix(st);
        return n <= 0 ? 0 : (int)(st % (uint)n);
    }

    private static bool Coin(ref uint st) { st = Mix(st); return (st & 1) != 0; }

    // ---- the pool ------------------------------------------------------------------------------------

    /// <summary>The block one emitter's parameters can fill THIS step: its standing population plus the
    /// largest burst its mode can add in one step, so a birth is never refused for want of a slot the
    /// arithmetic could have predicted.
    /// <list type="bullet">
    /// <item><b>Single</b>: exactly one. Exact.</item>
    /// <item><b>Lightning</b>: a fixed chain of <c>(int)rate</c>, seeded up to and killed down to it. Exact.</item>
    /// <item><b>Explosion</b>: <c>(int)rate</c> at once on the detonate event, plus an ESTIMATE of what a
    /// previous detonation still has alive (<c>rate x life</c>). Not a bound: the event's period P is the
    /// model's and we do not read it, so the true live count is <c>(int)rate x ceil(life / P)</c> and a
    /// re-detonating emitter with <c>life &gt; P</c> overruns this. What it fixes is the old size, which was
    /// the standing term ALONE, so any emitter with <c>life &lt; 1</c> lost most of its burst on every bang.
    /// An overrun is counted like any other loss and <c>CAP-LOST</c> is what would say P has to be read.</item>
    /// <item><b>Fountain</b> (metered): the standing <c>rate x life</c>, plus one for the step a birth and a
    /// death coincide, plus the metered burst. The burst is read off the loop: the accumulator fires at
    /// <c>acc &gt;= 1/rate</c> and births <c>(int)(acc x rate)</c> with <c>acc &lt; 1/rate + dt</c>, so at
    /// most <c>1 + (int)(dt x rate)</c> land in one step.</item>
    /// </list>
    /// Two bursts this deliberately does not cover, both counted by <see cref="Room"/> rather than sized for:
    /// the p2p birth, which is <c>(int)(rate x distance travelled)</c> long and unbounded across a teleport;
    /// and a birthrate RAMP, where the accumulator was filled at the old rate and is spent at the new one, so
    /// up to <c>newRate / oldRate</c> more births can land in the step the controller raises it.</summary>
    /// <summary>Round a wanted population up to a power of two. The arena is a bump allocator and a grow
    /// ABANDONS the old block, so a controller ramping birthrate one particle at a time would burn ~N^2/2 of
    /// the arena on its way up and take everything else down with it at the reset. Growing in powers of two
    /// makes that O(log N) grows and under 2x the block's own size in abandoned space.</summary>
    private static int Block(int want)
        => want < 1 ? 1 : (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)want);

    private static int Want(int mode, float rate, float life, float dt)
    {
        float l = MathF.Max(life, 0f);
        int want = mode switch
        {
            ModeSingle => 1,
            ModeLightning => (int)rate,
            ModeExplosion => (int)rate + (int)(rate * l) + 1,
            _ => (int)(rate * l) + 1 + 1 + (int)(dt * rate),
        };
        return want < 1 ? 1 : want;
    }

    /// <summary>Give this emitter a block big enough for the population its parameters imply, growing the
    /// arena if it must. False means the arena is at its cap and this emitter draws nothing this frame.
    /// </summary>
    private static bool Grow(ref Slot slot, IntPtr em, float dt)
    {
        var e = new PartEmitter(em);
        float rate = e.BirthRate, life = e.Life;
        int want = Want(slot.Mode, rate, life, dt);
        // The peak is the census MaxPerEmitter has to be sized from: nothing else in the client says what an
        // emitter's own parameters ask for, so a clamp reported without it cannot be acted on. Taken before
        // the block is rounded up, so it reports the demand and not our own quantisation.
        if (want > _capPeak) _capPeak = want;
        want = Block(want);
        if (want > MaxPerEmitter) { want = MaxPerEmitter; _capped++; }
        if (slot.Cap >= want) return true;

        int had = slot.Live, from = slot.Base;
        if (!Reserve(want)) return false;
        // Reserve may have dropped every block to grow the arena, in which case there is nothing to carry.
        if (slot.Cap == 0) had = 0;
        int at = _slotTop;
        _slotTop += want;
        // The old block is abandoned rather than freed: the arena is a bump allocator and its only reclaim
        // is the whole-arena reset below, which is exactly the trade Particles' record blocks already make.
        IntPtr* pt = _ptrs + at;
        for (int i = 0; i < want; i++) pt[i] = (IntPtr)(_pool + (long)(at + i) * EngineLayout.ParticleStride);
        // Carry the live particles across. A controller animates birthrate and lifespan, so an emitter's
        // ceiling moves while it burns -- and a grow that dropped its population would make a ramping
        // fountain visibly restart, which is the wrong kind of cheap.
        for (int i = 0; i < had; i++)
            NativeMemory.Copy((void*)_ptrs[from + i], (void*)pt[i], (nuint)EngineLayout.ParticleStride);
        slot.Base = at; slot.Cap = want; slot.Live = had;
        return true;
    }

    /// <summary>Room for <paramref name="want"/> more particles, growing or starting the arena over. A reset
    /// empties every emitter, which costs one frame of missing particles and is counted -- it is not a silent
    /// fallback, because the arena being full is our own limit and the period line names it.</summary>
    private static bool Reserve(int want)
    {
        if (_slotTop + want <= _slotCap) return true;
        int cap = _slotCap > 0 ? _slotCap : MinSlots;
        while (cap < _slotTop + want && cap < MaxSlots) cap <<= 1;
        if (cap > _slotCap && cap <= MaxSlots)
        {
            byte* pool = (byte*)NativeMemory.Alloc((nuint)((long)cap * EngineLayout.ParticleStride));
            IntPtr* ptrs = (IntPtr*)NativeMemory.Alloc((nuint)((long)cap * sizeof(IntPtr)));
            NativeMemory.Clear(pool, (nuint)((long)cap * EngineLayout.ParticleStride));
            // Every live block's records MOVE, so every pointer table entry has to be rewritten -- which is
            // what dropping the whole arena does, at the cost of one frame. Copying instead would mean
            // rebasing each emitter's pointers, for a path that runs a handful of times per session.
            if (_pool != null) { NativeMemory.Free(_pool); NativeMemory.Free(_ptrs); Empty(); }
            _pool = pool; _ptrs = ptrs; _slotCap = cap; _slotTop = 0;
            return want <= cap;
        }
        if (_slotCap == 0) return false;
        Empty();
        _slotTop = 0;
        _poolResets++;
        if (!_poolWarned)
        {
            _poolWarned = true;
            _log?.Always($"EmitterSim: the particle pool hit its {MaxSlots}-particle ceiling and started over "
                       + "-- one frame of missing particles, and it will repeat while the area stays this busy "
                       + "(pool-reset= on the [emitsim] line counts it)");
        }
        return want <= _slotCap;
    }

    /// <summary>Drop every emitter's block. The slots survive so their mode/mover verdicts do; only the
    /// particles go.</summary>
    private static void Empty()
    {
        for (int i = 0; i < _tabN; i++) { _tab[i].Base = 0; _tab[i].Cap = 0; _tab[i].Live = 0; }
        _orderN = 0;
        // Particles holds a RAW Particle* array per captured emitter and draws a one-frame-stale slot, so an
        // arena that moved under it would be read after free. Dropping its table costs one frame of
        // particles; not dropping it costs a crash on whichever emitter left the bucket last frame.
        Particles.Forget();
    }

    /// <summary>Tombstone slots nothing has walked for a while, so an area the camera left stops holding pool
    /// blocks. Cheap and amortised: one slot per frame at most.</summary>
    private static void Reclaim()
    {
        if (_tabN == 0) return;
        int i = _frame % _tabN;
        if (_tab[i].Em != IntPtr.Zero && _tab[i].Seen < _frame - StaleFrames) _tab[i] = default;
    }

    /// <summary>Frames an emitter may go unseen before its slot is dropped. Long enough that turning the
    /// camera away and back does not restart an effect; short enough that an area transition's emitters do
    /// not outlive their own <c>Part</c>s by anything a person would notice.</summary>
    private const int StaleFrames = 120;

    /// <summary>An emitter is being destroyed. <see cref="Particles"/>'s own destructor hook calls this: the
    /// pool block is keyed by a pointer the engine is about to free and reuse.</summary>
    internal static void Forget(IntPtr em)
    {
        for (int i = 0; i < _tabN; i++)
            if (_tab[i].Em == em) { _tab[i] = default; return; }
    }

    /// <summary>Area transition or context loss: every emitter pointer this table holds belongs to Parts the
    /// unload frees.</summary>
    internal static void Reset()
    {
        for (int i = 0; i < _tabN; i++) _tab[i] = default;
        _tabN = 0; _orderN = 0; _slotTop = 0;
        // Same reason Empty() does it: Particles holds a raw Particle* array per captured emitter and draws a
        // one-frame-stale slot, so rewinding the arena under it has to drop its table. The registry happens
        // to revoke Particles too, but relying on that order would make this correct by luck.
        Particles.Forget();
        // The collision set describes the AREA, and an area edge is exactly what invalidates it.
        BounceWorld.Reset();
    }

    /// <summary><c>/emitsim dump</c>: the next <see cref="Step"/> walks every scene gob within
    /// <see cref="DumpReach"/> of the camera and logs each emitter in its trees. Driven from the GOBS, not the
    /// registry, so an emitter the registry never took is listed too. Cold and on demand, so it allocates.</summary>
    private static bool _dump;
    private const float DumpReach = 30f;
    private const int DumpMax = 96;

    private static void DumpGobs(CX.Engine.Scene sc, Vector3 eye)
    {
        IntPtr* gobs = (IntPtr*)sc.Gobs;
        int n = sc.GobCount, lines = 0, near = 0;
        var stack = new (IntPtr Part, bool Hidden)[256];
        for (int i = 0; i < n && gobs != null && lines < DumpMax; i++)
        {
            if (gobs[i] == IntPtr.Zero) continue;
            var g = new CX.Engine.Gob(gobs[i]);
            AurXform.V3 w = AurXform.AdjustedPosition(gobs[i]);
            float d = (eye - new Vector3(w.X, w.Y, w.Z)).Length();
            if (d > DumpReach) continue;
            near++;
            ReadOnlySpan<IntPtr> roots = [g.PartTree.Ptr, g.LodRoot1.Ptr, g.LodRoot2.Ptr];
            int nApp = g.AppendCount;
            IntPtr* app = (IntPtr*)g.AppendRoots;
            for (int r = 0; r < roots.Length + (app == null ? 0 : nApp); r++)
            {
                IntPtr root = r < roots.Length ? roots[r] : app[r - roots.Length];
                if (root == IntPtr.Zero) continue;
                int sp = 0;
                stack[sp++] = (root, false);
                while (sp > 0 && lines < DumpMax)
                {
                    var (pp, hid) = stack[--sp];
                    var part = new CX.Engine.Part(pp);
                    hid |= part.Visible == 0;
                    if (!part.AsPartEmitter().IsNull)
                    {
                        lines++;
                        DumpOne(g, gobs[i], pp, d, r == 0 ? "tree" : r < roots.Length ? "lod-alt" : "append", hid);
                    }
                    IntPtr* kids = (IntPtr*)part.Children;
                    for (int k = 0; kids != null && k < part.ChildCount && sp < stack.Length; k++)
                        if (kids[k] != IntPtr.Zero) stack[sp++] = (kids[k], hid);
                }
            }
        }
        _log?.Info($"EmitterSim dump: {near} gob(s) within {DumpReach:F0} m of {n}, {lines} emitter(s) listed"
                 + (lines >= DumpMax ? " (CAPPED)" : ""));
    }

    /// <summary>One gob-owned emitter: registry membership, the walk's hidden gate, our slot, the parameters,
    /// the gob's animate gates, and each queued run's birthrate key at the run's time -- the value
    /// <see cref="AnimKeys.Control"/> should have written.</summary>
    private static void DumpOne(CX.Engine.Gob g, IntPtr gob, IntPtr em, float d, string root, bool hidden)
    {
        var part = new CX.Engine.Part(em);
        var e = new PartEmitter(em);
        int s = -1;
        for (int i = 0; i < _tabN; i++) if (_tab[i].Em == em) { s = i; break; }
        string slot = s < 0 ? "no slot"
                    : $"mode={_tab[s].Mode} live={_tab[s].Live}/{_tab[s].Cap} seen={(_tab[s].Seen == _frame ? "now" : $"{_frame - _tab[s].Seen}f ago")}";
        IntPtr model = g.Node.Ptr;
        string line = $"EmitterSim dump: {d:F1}m {NodeName(model)}/{NodeName(part.Node.Ptr)} [{root}{(hidden ? ", HIDDEN" : "")}]"
                    + $" registry={SceneParts.Membership(em)} {slot} birthrate={e.BirthRate:F2} life={e.Life:F2}"
                    + $" engine-list={e.Count} | gob animating={g.IsAnimating()} forced={g.IsForceAnimated()}"
                    + $" inview={g.InView} offscreen={g.AnimOffScreen}"
                    + $" ownerbit={(model == IntPtr.Zero ? -1 : new Model(model).OwnerFlags & 1)} runs={g.RunCount}:";
        for (int i = 0; i < g.RunCount; i++)
        {
            var r = g.Run(i);
            if (r.IsNull) continue;
            float k = Keyed(r.Animation.Node.Ptr, part.NodeId, r.Time);
            line += $" [{Marshal.PtrToStringAnsi(r.Animation.Ptr + EngineLayout.AnimationName)} t={r.Time:F2}"
                  + $" w={r.Blend:F2} fl=0x{r.Flags:x} key={(float.IsNaN(k) ? "no node" : k < 0f ? "unkeyed" : k.ToString("F2"))}]";
        }
        _log?.Info(line);
    }

    /// <summary>The birthrate this animation keys for the node with <paramref name="id"/> at <paramref name="t"/>:
    /// NaN when the animation has no such node, -1 when the node carries no birthrate track.</summary>
    private static float Keyed(IntPtr anode, int id, float t)
    {
        if (anode == IntPtr.Zero) return float.NaN;
        var n = new MdlNode(anode);
        if (n.NodeId == id)
        {
            IntPtr c = n.Controllers;
            for (int k = 0; c != IntPtr.Zero && k < n.ControllerCount; k++)
            {
                IntPtr ck = c + k * EngineLayout.NewControllerStride;
                if (new NewController(ck).Id == EngineLayout.EmBirthRate && AnimKeys.Float(ck, anode, t, out float v))
                    return v;
            }
            return -1f;
        }
        IntPtr* kids = (IntPtr*)n.Children;
        for (int k = 0; kids != null && k < n.ChildCount; k++)
        {
            float v = Keyed(kids[k], id, t);
            if (!float.IsNaN(v)) return v;
        }
        return float.NaN;
    }

    private static string NodeName(IntPtr node)
        => node == IntPtr.Zero ? "-" : Marshal.PtrToStringAnsi(node + EngineLayout.MdlNodeName) ?? "?";

    /// <summary>Stats for the period line.</summary>
    internal static string Stats(int frames)
    {
        if (!Armed) return string.Empty;
        int f = frames > 0 ? frames : 1;
        string s = $"[emitsim] em={_emitters / f} parts={_particles / f} born={_spawned} died={_died}";
        if (_chunk != 0) s += $" chunk={_chunk} (with engine particles {_chunkLive})";
        s += $" silent exits/frame: fade={(float)_fadeExit / f:F1} rate0={(float)_rate0 / f:F1} other-empty={(float)_idle / f:F1}"
           + $" (held, drawn but not stepped: {(float)_held / f:F1})";
        if (_bounced != 0) s += $" bounce={_bounced}";
        if (_p2pNoTarget != 0) s += $" p2p-untargeted={_p2pNoTarget}";
        // Per frame, like em= and parts= beside it: a period total here read as an alarm (523) for what is
        // half an emitter a frame. The peak is what MaxPerEmitter would have to be raised to; CAP-LOST is
        // births actually dropped, which is the part that is a bug -- vanilla has no ceiling to drop them at.
        if (_capped != 0) s += $" capped={(float)_capped / f:F2}/frame (wanted up to {_capPeak}, cap {MaxPerEmitter})";
        if (_capLost != 0) s += $" CAP-LOST={_capLost} births VANILLA WOULD HAVE DRAWN";
        if (_poolResets != 0) s += $" pool-reset={_poolResets}";
        if (_reclaimed != 0) s += $" reclaimed={_reclaimed}";
        if (_full != 0) s += $" table-full={_full}";
        if (_foreign != 0) s += $" foreign-scene={_foreign}";
        // CX-104: where the population came from, and how much of it the owned cull kept. A zero culled count
        // in a big area means the cull is not running (no camera), not that everything is on screen.
        s += Buckets.Emitters.V && SceneParts.Ready
           ? $" src=registry({SceneParts.Emitters.Length}) inview={_inView} culled={_outOfView}"
           : " src=emitterbucket";
        if (Bouncing.V) s += BounceWorld.Stats(f);
        _emitters = 0; _particles = 0; _spawned = 0; _died = 0; _chunk = 0; _bounced = 0;
        _p2pNoTarget = 0; _capped = 0; _poolResets = 0; _reclaimed = 0; _full = 0; _foreign = 0;
        _capPeak = 0; _capLost = 0;
        _held = 0; _idle = 0; _fadeExit = 0; _rate0 = 0; _chunkLive = 0;
        return s;
    }
}
