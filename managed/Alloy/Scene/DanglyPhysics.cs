using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Replaces the engine's dangly-mesh solve with a substepped one that has gravity and treats wind as a
/// FORCE. Chains <c>PartDanglyMesh::Animate(float)</c> @0x49bbb0 and, on the happy path, integrates
/// instead of calling down.
/// <para>Vanilla is already a per-vertex sim -- independent damped oscillators, semi-implicit Euler -- so
/// what this buys is not "physics" but the four things it lacks. Three of them are fixed here: no gravity;
/// a frame-rate-dependent <c>dt</c> clamped to <c>[0.0125, 0.035]</c>, which silently lies outside
/// 28.6-80 fps; and a wind term added as a POSITION OFFSET after the integration
/// (<c>pos += windLocal * 20 * cap</c>), so wind never enters the spring and a cloak can only lean, never
/// gust or flutter. The fourth -- no vertex-to-vertex coupling -- is deliberately left alone; see the
/// remarks.</para>
/// </summary>
/// <remarks>
/// <para><b>No topology.</b> A distance-constraint pass over the mesh edges is what makes a gust travel
/// across a sheet as a wave, and it is the honest fix for coupling. It also needs an edge list per MdlNode,
/// which means a keyed cache, a build step, and a death notice when the part is freed (#145/#150) -- for a
/// look that spatially-varying wind approximates at zero storage. The turbulence field is that
/// approximation: one sine of the vertex's REST position and time, so neighbours move together because
/// they are near each other, not because they are linked. If cloaks read as stretchy rather than as
/// under-coupled, that is the signal to add real edges.</para>
/// <para><b>No colliders.</b> The per-axis cage below is vanilla's collision surrogate, kept and widened
/// rather than deleted: it is the only reason NWN cloaks never pass through the wearer's legs. Widening it
/// is the whole knob -- <c>/dangly cage</c> trades freedom of movement for clipping, and there is no third
/// option short of posing the creature's skinmesh into capsules.</para>
/// <para><b>Allocates nothing.</b> This runs once per dangly part per frame -- the gates, the engine reads and
/// the upload on the main thread, the integration itself on a <see cref="StagePool"/> worker (AN2). Every
/// buffer it touches already exists and is owned by the engine: the deformed positions are the VDO dynamic
/// item's host shadow (which is also where last frame's state is read from), and the velocities are the
/// engine's own <c>+0xa0</c> array. Nothing new is created, so nothing new has a lifetime.</para>
/// </remarks>
internal static unsafe class DanglyPhysics
{
    /// <summary>One world unit of movement in a frame is a teleport: snap flat to rest and skip the sim.
    /// Vanilla's threshold, kept so a door-to-door transition behaves the same.</summary>
    private const float TeleportDist = 1.0f;
    /// <summary>The engine's own <c>dt</c> clamp. Only <see cref="_childAnimate"/> sees it now -- children
    /// were tuned against it -- but the sim reads the real frame time instead.</summary>
    private const float MinChildDt = 0.0125f, MaxChildDt = 0.035f;
    private const float ConstraintMax = 255.0f;
    /// <summary>Vanilla's gains on the two terms it does have: <c>0.5 * tightness</c> for the spring and
    /// <c>1.5 * period</c> for the damping. Kept exactly, so the authored per-model feel survives.</summary>
    private const float StiffnessGain = 0.5f, DampingGain = 1.5f;
    /// <summary>Runaway guard. The cage bounds position but not velocity, and a vertex that saturates the
    /// cage every substep would otherwise keep integrating speed it can never spend.</summary>
    private const float MaxSpeed = 60f;
    /// <summary>Radians/s the ambient breeze direction wanders through. Slow enough to read as weather.</summary>
    private const float BreezeTurn = 0.05f;

    private static ExtenderLog? _log;
    private static PartDanglyAnimateFn* _slot;
    private static IntPtr* _pCurrentScene;
    private static PartGetWorldFn _getWorld;
    private static GlGetPoolFn _getPool;
    private static VdoGetItemDynamicDataFn _getItemDyn;
    private static VdoCommitItemDynamicDataFn _commitItemDyn;
    private static VertexPrimGenVelocityFn _genVelocity;
    private static PartAnimateFn _childAnimate;
    private static delegate* unmanaged<IntPtr, Vector3*, float, Vector3> _getWind;
    /// <summary><c>Scene::GetWindVector</c> returns <c>Vector</c> BY VALUE, so it takes a hidden return
    /// buffer -- and the two platforms put that buffer in different argument slots: Itanium
    /// <c>(sret, this, ...)</c>, Win64 <c>(this, sret, ...)</c>. Measured, rva 0xc9b90:
    /// <c>mov rcx,[rcx+0x160]</c> reads the Scene, <c>mov rbx,rdx</c> ... <c>mov rax,rbx</c> returns the
    /// buffer that arrived in rdx. Same function, second declaration (#456).
    /// <para>CX-39: the one pointer pair in this file that stays hand-written, for exactly that reason -- a
    /// declared signature is per SYMBOL, and this symbol has two.</para></summary>
    private static delegate* unmanaged<IntPtr, Vector3*, Vector3*, float, void> _getWindWin;

    /// <summary>The wind at <paramref name="pos"/>. CX-110: <see cref="AlloyWind"/> answers it off Alloy's own
    /// clock, noise field and point-source list; <c>view.wind 0</c> falls back to the engine call, through
    /// whichever argument order this platform uses.</summary>
    private static Vector3 Wind(IntPtr scene, Vector3* pos, float dt)
    {
        if (AlloyWind.Owned) return AlloyWind.Wind(*pos, dt);
        if (!OperatingSystem.IsWindows()) return _getWind(scene, pos, dt);
        Vector3 r = default;
        _getWindWin(scene, &r, pos, dt);
        return r;
    }
    private static bool _armed;

    private static int _parts, _verts, _moved;
    /// <summary>#492's measurement: solve ticks per period and the single worst call. Excludes the early
    /// hand-backs and the engine's child animate, so it is the term a compute solve would remove.
    /// <para>AN0: <see cref="_commitTicks"/> is subtracted out of it, so <c>solve</c> is the arithmetic and
    /// <c>commit</c> is the GL upload funnel -- the two have different fixes and were one number.</para></summary>
    private static long _ticks, _peakTicks, _commitTicks, _peakCommit;
    /// <summary>AN7: the vertex count and the commit share of the period's WORST call. The survey could not
    /// tell a 0,9 ms peak from a 0,9 ms stall: a peak whose vertex count is ordinary and whose commit is
    /// small did no work worth that time, so it is a pause (a GC on this thread, a fault, a first-touch buffer
    /// mint inside the driver) rather than arithmetic, and the fix is not in this file.</summary>
    private static int _peakVerts;
    private static long _peakOwnCommit;
    private static int _commits;
    /// <summary>AN3: solves that displaced nothing, so the upload was skipped.</summary>
    private static int _commitSkipped;

    /// <summary>The VDO dynamic-item upload -- <c>UnlockPool</c> and so Alloy's <c>NamedBufferSubData</c> --
    /// timed apart from the arithmetic. Main thread only: it is the one call in the solve that ends in GL.</summary>
    private static void Commit(IntPtr vdo, int item)
    {
        long t = Stopwatch.GetTimestamp();
        _commitItemDyn(vdo, (uint)item, 0xffffffffu);
        long d = Stopwatch.GetTimestamp() - t;
        _commitTicks += d;
        if (d > _peakCommit) _peakCommit = d;
        _commits++;
    }

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _pCurrentScene = (IntPtr*)Native.Sym(SymbolMap.CurrentScene);
        _getWorld = EngineFn.PartGetWorld;
        _getPool = EngineFn.GlGetPool;
        _getItemDyn = EngineFn.VdoGetItemDynamicData;
        _commitItemDyn = EngineFn.VdoCommitItemDynamicData;
        _genVelocity = EngineFn.VertexPrimGenVelocity;
        _childAnimate = EngineFn.PartAnimate;
        _getWind = (delegate* unmanaged<IntPtr, Vector3*, float, Vector3>)Native.Sym(SymbolMap.SceneGetWindVector);
        _getWindWin = (delegate* unmanaged<IntPtr, Vector3*, Vector3*, float, void>)_getWind;

        if (_pCurrentScene == null || _getWorld == null || _getPool == null || _getItemDyn == null
            || _commitItemDyn == null || _genVelocity == null || _childAnimate == null || _getWind == null)
        {
            log.Info("DanglyPhysics: symbols unresolved -- the engine keeps its own dangly solve");
            return;
        }

        // Chain rather than Install: the solve is a plausible future co-consumer, and #88's rule is that a
        // second Install on the same target silently orphans the first.
        _slot = hooks.ChainPartDanglyAnimate(&OnAnimate, 10);
        if (_slot == null)
        {
            log.Info("DanglyPhysics: could not chain PartDanglyMesh::Animate -- dangly stays vanilla");
            return;
        }

        _armed = true;
        log.Info($"DanglyPhysics: armed -- substepped dangly at {Dangly.Hz.V:F0}Hz with gravity {Dangly.Gravity.V:F1}"
                 + $" and wind as a force ({Dangly.Wind.V:F0}x, {Dangly.Turb.V * 100f:F0}% gust); /dangly enabled 0 for vanilla");
    }

    /// <summary>The period report, and the measurement the research note could not take: how many dangly
    /// parts and vertices this actually solves per frame. Resets its own counters -- the registry calls this
    /// exactly once per period and nothing else reads them.</summary>
    internal static string Stats(int frames)
    {
        if (frames <= 0 || _parts == 0)
        {
            // A period with no solves can still have had a refused drain, and that is the one line in here
            // nobody may lose.
            string only = _drainFailed == 0 ? ""
                        : $"dangly {_drainFailed} DRAIN(S) REFUSED by the stage pool (nothing published)";
            ResetStats();
            return only;
        }
        double toMs = 1000.0 / Stopwatch.Frequency;
        // AN0: the arithmetic and the GL upload funnel, accumulated apart. `_ticks` never includes a commit --
        // the teleport branch commits and returns without adding to it, so a subtraction here could go negative.
        string s = $"dangly {(float)_parts / frames:F1} parts/f, {(float)_verts / frames:F0} verts/f, " +
                   $"{(float)_moved / frames:F1} moved/f, solve {_ticks * toMs / frames:F3} ms/f cpu " +
                   $"(peak call {_peakTicks * toMs:F3} ms over {_peakVerts} verts, {_peakOwnCommit * toMs:F3} of it commit), " +
                   $"commit {_commitTicks * toMs / frames:F3} ms/f " +
                   $"({(float)_commits / frames:F0} calls/f, {(float)_commitSkipped / frames:F1} skipped/f, peak {_peakCommit * toMs:F3} ms)" +
                   $", drain {_drainTicks * toMs / frames:F3} ms/f wall over {_drainChunks} chunk(s)";
        if (_qDead != 0) s += $", {_qDead} queued part(s) died before the drain";
        if (_qFull != 0) s += $", {_qFull} SOLVES OFF THE QUEUE (it filled)";
        if (_drainFailed != 0) s += $", {_drainFailed} DRAIN(S) REFUSED by the stage pool (nothing published)";
        ResetStats();
        return s;
    }

    private static void ResetStats()
    {
        _parts = 0; _verts = 0; _moved = 0; _commits = 0; _commitSkipped = 0;
        _ticks = 0; _peakTicks = 0; _commitTicks = 0; _peakCommit = 0; _peakVerts = 0; _peakOwnCommit = 0;
        _drainTicks = 0; _drainChunks = 0; _qDead = 0; _qFull = 0; _drainFailed = 0;
    }

    /// <summary>The <c>/dangly</c> status line; the dials are <see cref="Dangly"/>'s.</summary>
    internal static string Describe()
        => $"dangly {(Dangly.Enabled.V && _armed ? "on" : _armed ? "off (vanilla solve)" : "unarmed")} "
         + $"gravity {Dangly.Gravity.V:F2} wind {Dangly.Wind.V:F2} turb {Dangly.Turb.V:F2} turbfreq {Dangly.TurbFreq.V:F2} "
         + $"turbspeed {Dangly.TurbSpeed.V:F2} vary {Dangly.Vary.V:F2} breeze {Dangly.Breeze.V:F2} body {(Dangly.Body.V > 0f ? Dangly.Body.V.ToString("F2") : "off")} cage {Dangly.Cage.V:F2} hz {Dangly.Hz.V:F0} steps {Dangly.Steps.V} still {Dangly.Still.V:F3} "
         + $"solve {(Dangly.Parallel.V && Renderer.Threads.V > 0 && !StagePool.Faulted ? $"on {Renderer.Threads.V + 1} chunks" : "serial")}";

    /// <summary>The replacement solve. Every gate below hands the frame back to the engine rather than
    /// reproducing what it does in that case -- vanilla returns WITHOUT animating the part's children on
    /// each of them, which is an oddity worth inheriting rather than re-deriving.</summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void OnAnimate(IntPtr part, float dt)
    {
        if (!Dangly.Enabled.V) { Down(part, dt); return; }

        // The engine returns without integrating on these two (dangly-physics.md), so nothing moved.
        IntPtr scene = *_pCurrentScene;
        if (scene == IntPtr.Zero) { Down(part, dt, solves: false); return; }
        if (new Scene(scene).IsAnimating() == 0) { Down(part, dt, solves: false); return; }

        IntPtr node = new Part(part).Node.Ptr;
        var vpv = new PartTriMesh(part).VertexPrimitive;
        if (node == IntPtr.Zero || vpv.IsNull) { Down(part, dt); return; }

        int item = vpv.Item;
        IntPtr vdo = vpv.Vdo.Ptr;
        if (item == -1 || vdo == IntPtr.Zero) { Down(part, dt); return; }

        var dn = new MdlNodeDanglyMesh(node);
        float* cons = (float*)dn.Constraints;
        int n = Math.Min(new MdlNodeTriMesh(node).VertexCount, dn.ConstraintCount);
        if (cons == null || n <= 0) { Down(part, dt); return; }

        var vpd = new VertexPrimitiveDangly(vpv.AsDangly());
        if (vpd.IsNull) { Down(part, dt); return; }
        // Born lazily on the first Animate. Swallowing the original means nobody else ever creates it.
        if (vpd.Velocity == IntPtr.Zero) _genVelocity(vpd.Ptr);
        float* vel = (float*)vpd.Velocity;
        if (vel == null) { Down(part, dt); return; }

        float* rest = (float*)_getPool(new MdlNodeTriMesh(node).RestPool);
        float* dst = (float*)_getItemDyn(vdo, (uint)item);
        if (rest == null || dst == null) { Down(part, dt); return; }

        long t0 = Stopwatch.GetTimestamp();
        Vector3 wpos;
        float* wrot = stackalloc float[4];
        _getWorld(part, &wpos, wrot);

        // Order matters and is the engine's: teleport test FIRST, seed second. A freshly built part has
        // prevPos = 0, so anywhere but the origin it takes the teleport branch on frame one -- and that
        // memcpy is the only thing that ever initialises the dynamic buffer. Seeding first would skip it
        // and open every spawn by integrating from whatever the buffer held.
        var pd = new PartDanglyMesh(part);
        float* prevRot = (float*)Unsafe.AsPointer(ref pd.PrevRot);
        Vector3 ppos = pd.PrevPos;
        if ((wpos - ppos).Length() > TeleportDist)
        {
            Buffer.MemoryCopy(rest, dst, (long)n * 12, (long)n * 12);
            new Span<float>(vel, n * 3).Clear();
            Commit(vdo, item);
            StorePose(part, wpos, wrot);
            AlloyRenderer.NoteDanglyMoved(part, float.PositiveInfinity, Dangly.Still.V);
            Down_TailOnly(part, dt);
            return;
        }

        // Not a teleport, but possibly the first frame: an all-zero xyz at +0xc4..+0xcc means the previous
        // pose was never written, and a zero quaternion would collapse every vertex onto the part. Seeding
        // makes this frame's deltas identity instead. Vanilla's own guard.
        bool seeded = prevRot[1] == 0f && prevRot[2] == 0f && prevRot[3] == 0f;
        if (seeded)
        {
            StorePose(part, wpos, wrot);
            ppos = wpos;
        }

        float t = (float)(Stopwatch.GetTimestamp() % (Stopwatch.Frequency * 1024L) / (double)Stopwatch.Frequency);
        // This instance's own phase and its own rate. Both come from the PART POINTER, not its position: a
        // position hash would be stable for a banner but would jump every frame on a walking character's
        // cloak, and a position-smooth field aliases -- two props an exact wavelength apart come back into
        // lockstep, which on a 10-unit tile grid is the common case, not the corner one.
        ulong hash = Mix((ulong)part);
        float phaseOff = (hash & 0xffff) * (MathF.Tau / 65535f);
        float rateVar = 1f + ((((hash >> 16) & 0xffff) * (1f / 65535f)) - 0.5f) * 2f * Dangly.Vary.V;

        // The part's own motion, carried into every vertex once rather than per substep. This is where all
        // the inertia comes from: without it a cloak sits perfectly still on a sprinting character.
        float iw = -wrot[0], ix = wrot[1], iy = wrot[2], iz = wrot[3];      // inverse rotation (w negated)
        Qmul(iw, ix, iy, iz, prevRot[0], prevRot[1], prevRot[2], prevRot[3],
             out float dw, out float dx, out float dy, out float dz);
        Vector3 tLocal = Qrot(-prevRot[0], prevRot[1], prevRot[2], prevRot[3], ppos - wpos);

        // The area's wind at this part's world position, with vanilla's clamped dt. CX-110: CX's own field,
        // and its base vector still comes off WindManager+0x20 -- which is ZERO in normal play, because the
        // Scene's manager is default-constructed and nothing but the setglobalwind console command ever writes
        // it, so every bit of felt wind is the point-source term the field composes in.
        Vector3 windW = Wind(scene, &wpos, Math.Clamp(dt, MinChildDt, MaxChildDt));
        if (!float.IsFinite(windW.X) || !float.IsFinite(windW.Y) || !float.IsFinite(windW.Z)) windW = Vector3.Zero;
        // The breeze wanders rather than blowing down a fixed axis -- a banner pinned to one compass
        // direction forever reads as a bug, and the wander is two trig calls per PART.
        windW += new Vector3(MathF.Cos(t * BreezeTurn), MathF.Sin(t * BreezeTurn), 0f) * Dangly.Breeze.V;
        Vector3 windL = Qrot(iw, ix, iy, iz, windW) * Dangly.Wind.V;
        Vector3 gravL = Qrot(iw, ix, iy, iz, new Vector3(0f, 0f, -Dangly.Gravity.V));
        // A gust direction across the wind, so the flutter is a flap and not just a pulse along one axis.
        Vector3 gust = Vector3.Cross(windL, Qrot(iw, ix, iy, iz, Vector3.UnitZ));
        float gl = gust.Length();
        gust = gl > 1e-4f ? gust * (windL.Length() / gl) : Vector3.Zero;

        // Substeps derived from the frame's dt, never accumulated: Animate fires once per PART per frame,
        // so an accumulator would advance N times a frame. Capping dt first is what bounds h to 1/hz -- a
        // loading hitch otherwise arrives as one 0.25s step and detonates the spring.
        // Read once per part: the input thread may move a knob mid-loop, and one solve should see one value.
        float hz = Dangly.Hz.V, turb = Dangly.Turb.V, turbFreq = Dangly.TurbFreq.V, turbSpeed = Dangly.TurbSpeed.V,
              cage = Dangly.Cage.V, body = Dangly.Body.V;
        int maxSteps = Dangly.Steps.V;
        float sdt = MathF.Min(dt, maxSteps / hz);
        int steps = Math.Clamp((int)MathF.Round(sdt * hz), 1, maxSteps);
        float h = sdt / steps;
        float disp = dn.Displacement;
        float stiff = StiffnessGain * dn.Tightness;
        float damp = DampingGain * dn.Period;
        AlloyRenderer.NoteDanglyPrev(part, dst, n);

        Item it = default;
        it.Part = part; it.Vdo = vdo; it.VItem = item; it.N = n; it.Steps = steps;
        it.Cons = (IntPtr)cons; it.Rest = (IntPtr)rest; it.Dst = (IntPtr)dst; it.Vel = (IntPtr)vel;
        it.Wpos = wpos; it.Rw = wrot[0]; it.Rx = wrot[1]; it.Ry = wrot[2]; it.Rz = wrot[3];
        it.Dw = dw; it.Dx = dx; it.Dy = dy; it.Dz = dz;
        it.TLocal = tLocal; it.WindL = windL; it.Gust = gust; it.GravL = gravL;
        it.T = t; it.PhaseOff = phaseOff; it.RateVar = rateVar; it.H = h;
        it.Disp = disp; it.Stiff = stiff; it.Damp = damp;
        it.Turb = turb; it.TurbFreq = turbFreq; it.TurbSpeed = turbSpeed; it.Cage = cage; it.Body = body;
        it.Seeded = seeded;
        it.SetupTicks = Stopwatch.GetTimestamp() - t0;

        // AN2: with a drain guaranteed, the arithmetic is handed to the pool and the part's children are
        // animated now, in the engine's own order. Without one -- the engine owns Scene::Animate
        // (anim.owned 0), or this is CAurObject::AnimateExternal outside any walk -- it is solved inline.
        if (Batching && _qn < MaxQueue) { _q[_qn++] = it; Down_TailOnly(part, dt); return; }
        if (Batching) NoteQueueFull();
        Solve(ref it);
        Publish(ref it);
        Down_TailOnly(part, dt);
    }

    // ================= AN2: collect, solve, publish =================

    /// <summary>One queued solve. Everything the arithmetic reads is here by value or as a pointer into a
    /// buffer only this part owns, which is what lets a worker run it: the engine calls, the knob reads, the
    /// wind sample, the world transform and the motion-vector snapshot are all done at collect time on the
    /// main thread, and the GL upload and the mover report are done at publish time on the main thread.</summary>
    private struct Item
    {
        public IntPtr Part, Vdo, Cons, Rest, Dst, Vel;
        public int VItem, N, Steps;
        public Vector3 Wpos, TLocal, WindL, Gust, GravL;
        public float Rw, Rx, Ry, Rz, Dw, Dx, Dy, Dz;
        public float T, PhaseOff, RateVar, H, Disp, Stiff, Damp;
        public float Turb, TurbFreq, TurbSpeed, Cage, Body;
        public bool Seeded;
        /// <summary>The largest per-vertex displacement this solve produced -- the mover verdict's input.</summary>
        public float MaxD;
        public long SetupTicks, SolveTicks;
    }

    /// <summary>Dangly parts one scene walk may queue. NOT a derived bound -- a scene's dangly part count is
    /// content, and nothing in the engine caps it -- so it is a budget with a counter, which is what CX-45 asks
    /// of a ceiling that cannot be proved: the parts past it are solved INLINE on the main thread, which is
    /// correct and merely not parallel, and the overflow is logged once and counted every period. The live
    /// reading it is set against is 410 parts per frame over every scene.</summary>
    private const int MaxQueue = 1024;
    private static readonly Item[] _q = new Item[MaxQueue];
    private static int _qn, _qFull, _drainFailed;
    private static bool _qFullLogged, _drainFailedLogged;
    private static long _drainTicks;
    private static int _drainChunks;

    /// <summary>True while a scene walk that will drain the queue is in progress. Set by
    /// <see cref="AnimRuntime"/> around its own walk, which is the only thing that calls <see cref="Drain"/>.</summary>
    internal static bool Batching;

    private static void NoteQueueFull()
    {
        _qFull++;
        if (_qFullLogged) return;
        _qFullLogged = true;
        _log?.Always($"DanglyPhysics: the solve queue filled at {MaxQueue} parts in one scene walk -- the rest "
                   + "are solved inline on the main thread, which is correct but not parallel; counted on the "
                   + "dangly clause from here");
    }

    /// <summary>AN2: solve everything this walk collected, then commit it serially.
    /// <para>Every part's arithmetic touches its own four buffers and nothing else -- no engine call, no GL, no
    /// shared counter -- so the chunks are independent and the answer does not depend on how they are cut;
    /// <see cref="SelfCheck"/> asserts that bit for bit. <c>/alloy threads 0</c> and <c>/dangly parallel 0</c>
    /// both take the serial path, and a StagePool fault does too.</para>
    /// <para>Called with the <c>currentscene</c> latch still held, which <c>PartDanglyMesh::Animate</c>'s own
    /// gate reads.</para>
    /// <para><b>No behaviour changes.</b> Every input the arithmetic uses is taken at collect time, at the same
    /// moment the inline solve took it: the world transform, the wind sample, the clock sample, the delta
    /// rotation and all six dials. The only late read is <c>dangly.still</c>, at the mover verdict. Moving the
    /// upload and the stored pose to the end of the walk is unobservable -- nothing reads the committed buffer
    /// during the walk, and the pose written is the one captured before it.</para></summary>
    internal static void Drain()
    {
        int n = _qn;
        _qn = 0;
        if (n == 0) return;
        long t0 = Stopwatch.GetTimestamp();
        // A pool the BUILD side already faulted answers every Run with an early false, which would cost this
        // frame's solves for nothing -- so the serial path is taken directly, and the knob is left alone: the
        // fault and its re-arm are the stage's, not ours.
        int chunks = Dangly.Parallel.V && !StagePool.Faulted
                   ? Math.Min(Renderer.Threads.V, StagePool.MaxChunks - 1) + 1 : 1;
        if (chunks > 1 && n > 1)
        {
            _drainN = n;
            if (!StagePool.Run(chunks, _log, SolveRange))
            {
                // NOT re-run serially. A false return can be the 500 ms join deadline with a worker still
                // inside its chunk (StagePool.cs's join, and Rearm's own comment), and the integration is not
                // idempotent -- a second pass over the same dst/vel double-steps it or tears a vertex. Nothing
                // is read either, so no torn buffer reaches the GPU: the drawn pose stays the one last committed.
                // The live dst/vel are another matter -- a wedged straggler goes on writing them -- which is
                // why the knob goes down here and nothing hands the pool these buffers again.
                _drainFailed++;
                if (!_drainFailedLogged)
                {
                    _drainFailedLogged = true;
                    _log?.Always("DanglyPhysics: the stage pool refused a dangly drain -- this frame's solves are "
                               + "neither published nor redone (a straggler may still be inside one), and the "
                               + "solve is serial from here; /dangly parallel 1 after /alloy threads re-arms it");
                }
                // A plain field write, not a Set: no Changed, no cfg write -- the stand-down is this session's,
                // and it is what keeps the next frame from handing the same buffers to the pool again.
                Dangly.Parallel.V = false;
                _drainChunks = chunks;
                _drainTicks += Stopwatch.GetTimestamp() - t0;
                // The queue is deliberately NOT cleared. A straggler is holding `ref _q[i]` and reading its
                // pointers and count as it goes, so zeroing the entry hands it a null and the fault lands on a
                // worker. Nothing needs the clear: _qn is already 0, the death notice only walks to _qn, and
                // the next collect overwrites each entry it uses.
                return;
            }
        }
        else { SolveRange(0, 1); chunks = 1; }
        _drainChunks = chunks;
        _drainTicks += Stopwatch.GetTimestamp() - t0;
        for (int i = 0; i < n; i++) Publish(ref _q[i]);
    }

    private static int _drainN;
    private static readonly Action<int, int> SolveRange = (w, chunks) =>
    {
        int n = _drainN, from = (int)((long)n * w / chunks), to = (int)((long)n * (w + 1) / chunks);
        // A part that died between collect and drain named buffers the freed object owned; nothing may read
        // them. Deaths can only have happened during the walk -- a worker runs arithmetic and nothing else.
        for (int i = from; i < to; i++) if (_q[i].Part != IntPtr.Zero) Solve(ref _q[i]);
    };

    /// <summary>The per-vertex integration. Pure: its inputs are the item's own fields and the four buffers it
    /// names, its outputs are two of those buffers and <see cref="Item.MaxD"/>. No engine call, no GL, no
    /// static state -- which is the whole reason it can run on a worker.</summary>
    private static void Solve(ref Item it)
    {
        long t0 = Stopwatch.GetTimestamp();
        int n = it.N, steps = it.Steps;
        float* cons = (float*)it.Cons, rest = (float*)it.Rest, dst = (float*)it.Dst, vel = (float*)it.Vel;
        float dw = it.Dw, dx = it.Dx, dy = it.Dy, dz = it.Dz;
        Vector3 tLocal = it.TLocal, windL = it.WindL, gust = it.Gust, gravL = it.GravL;
        float t = it.T, phaseOff = it.PhaseOff, rateVar = it.RateVar, h = it.H;
        float disp = it.Disp, stiff = it.Stiff, damp = it.Damp;
        float turb = it.Turb, turbFreq = it.TurbFreq, turbSpeed = it.TurbSpeed, cage = it.Cage, body = it.Body;
        float maxSq = 0f;

        for (int i = 0; i < n; i++)
        {
            float c = cons[i];
            if (!(c > 0f)) continue;                    // vanilla skips these outright; so must we

            int o = i * 3;
            var r = new Vector3(rest[o], rest[o + 1], rest[o + 2]);
            var p = Qrot(dw, dx, dy, dz, new Vector3(dst[o], dst[o + 1], dst[o + 2]) + tLocal);
            var v = Qrot(dw, dx, dy, dz, new Vector3(vel[o], vel[o + 1], vel[o + 2]));

            // The authored constraint IS the pinning field: 255 is welded, 0 is skipped. Same weight the
            // engine spends on its cage, spent here on how much force the vertex is allowed to feel.
            float w = 1f - c / ConstraintMax;
            float phase = (r.X * 0.71f + r.Y * 1.31f + r.Z * 0.93f) * turbFreq
                        + phaseOff - t * turbSpeed * rateVar;
            float g = MathF.Sin(phase);
            float g2 = 1f - 2f * g * g;                 // cos(2*phase), free: no second sine
            Vector3 acc = (windL * (1f + turb * g) + gust * (turb * g2) + gravL) * w;

            float k = c * stiff;
            for (int s = 0; s < steps; s++)
            {
                v += (acc + (r - p) * k - v * damp) * h;
                p += v * h;
            }

            float speed = v.Length();
            if (speed > MaxSpeed) v *= MaxSpeed / speed;

            // a constraint over 255 or a negative displacement (custom models) inverts Clamp's bounds, which throws
            float cap = MathF.Max(disp * w * cage, 0f);
            Vector3 d = p - r;
            p = r + new Vector3(Math.Clamp(d.X, -cap, cap),
                                Math.Clamp(d.Y, -cap, cap),
                                Math.Clamp(d.Z, -cap, cap));
            // The body proxy, applied AFTER the cage: it only ever pushes a vertex further OUT, so leaving
            // the cage by a hair is the safe direction. Squared throughout -- the sqrt is paid only by the
            // vertices actually inside the body, which in a settled pose is none of them.
            if (body > 0f)
            {
                float minSq = (r.X * r.X + r.Y * r.Y) * body * body;
                float pSq = p.X * p.X + p.Y * p.Y;
                if (pSq < minSq && pSq > 1e-8f)
                {
                    float sc = MathF.Sqrt(minSq / pSq);
                    p.X *= sc; p.Y *= sc;
                    // Kill the INWARD radial velocity too, or the vertex spends every frame pushing into a
                    // wall it is projected back out of, and the result reads as a buzz rather than a drape.
                    float inv = 1f / MathF.Sqrt(minSq);
                    float vr = (v.X * p.X + v.Y * p.Y) * inv;
                    if (vr < 0f) { v.X -= vr * p.X * inv; v.Y -= vr * p.Y * inv; }
                }
            }

            // NaN passes both clamps as "inside" and would land in the GPU buffer; IsFinite catches it.
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) { p = r; v = default; }

            // Displacement against the buffer's PREVIOUS pose (local space; the part's own motion is a
            // record change the caster pass already sees). The max, not the sum: one flapping corner moves.
            float ex = p.X - dst[o], ey = p.Y - dst[o + 1], ez = p.Z - dst[o + 2];
            maxSq = MathF.Max(maxSq, ex * ex + ey * ey + ez * ez);
            dst[o] = p.X; dst[o + 1] = p.Y; dst[o + 2] = p.Z;
            vel[o] = v.X; vel[o + 1] = v.Y; vel[o + 2] = v.Z;
        }

        it.MaxD = maxSq;
        it.SolveTicks = Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>The main-thread half: the upload, the stored pose, the mover verdict and the counters.</summary>
    private static void Publish(ref Item it)
    {
        if (it.Part == IntPtr.Zero) return;                 // a part that died between collect and drain
        float maxSq = it.MaxD;
        long commitWas = _commitTicks;
        // AN3: the upload is the whole point of the commit, and with no displacement there is nothing to
        // upload -- so it is skipped, the morph path's precedent (AnimHierarchy.Morph's `dirty`). What the
        // funnel does besides the write is what makes that safe: CommitItemDynamicData @0x45c7d0 is an offset
        // computation and RenderInterface::UnlockPool @0x45d3a0 -> GLRender::UnlockPool @0x4cfc80, whose every
        // branch ends in glBufferData/glBufferSubData (plus a bind and the pool's "has a buffer"/"uploaded"
        // bytes); on an Alloy frame it is #260's residency funnel. It carries no bookkeeping a skipped frame
        // owes anyone, and the #241 caster-dirty signal it raises would be a FALSE mover here -- the measured
        // verdict below is the one that speaks for a dangly slot (SlotIsDangly).
        // The GPU copy still matches the host shadow by induction: every frame that changes a vertex commits,
        // so a frame that changes none finds the buffer already holding what it would have sent. The base case
        // is the part's first solve, which either took the teleport branch above (and committed) or seeded --
        // and a seeded frame commits for exactly that reason.
        if (maxSq > 0f || it.Seeded) Commit(it.Vdo, it.VItem); else _commitSkipped++;
        float* rot = stackalloc float[4];
        rot[0] = it.Rw; rot[1] = it.Rx; rot[2] = it.Ry; rot[3] = it.Rz;
        StorePose(it.Part, it.Wpos, rot);
        long commitNow = _commitTicks - commitWas;
        _ticks += it.SetupTicks + it.SolveTicks;
        long dtTicks = it.SetupTicks + it.SolveTicks + commitNow;
        if (dtTicks > _peakTicks) { _peakTicks = dtTicks; _peakVerts = it.N; _peakOwnCommit = commitNow; }
        _parts++; _verts += it.N;
        if (AlloyRenderer.NoteDanglyMoved(it.Part, MathF.Sqrt(maxSq), Dangly.Still.V)) _moved++;
    }

    /// <summary>The <c>Part::~Part</c> death notice. A part queued for solving that dies before the drain must
    /// not be solved or published: the buffers it named belong to the freed object. Free when nothing is
    /// queued, which is every moment outside a scene walk.</summary>
    internal static void NotePartDeath(IntPtr part)
    {
        for (int i = 0; i < _qn; i++)
            if (_q[i].Part == part) { _q[i].Part = IntPtr.Zero; _qDead++; }
    }
    private static int _qDead;

    private static void StorePose(IntPtr part, Vector3 pos, float* rot)
    {
        var pd = new PartDanglyMesh(part);
        pd.PrevPos = pos;
        float* p = (float*)Unsafe.AsPointer(ref pd.PrevRot);
        p[0] = rot[0]; p[1] = rot[1]; p[2] = rot[2]; p[3] = rot[3];
    }

    /// <summary>Hand the whole call back to the engine. Whatever the engine's solve does to the vertices is
    /// invisible here, so a hand-back that <paramref name="solves"/> reports the part as a mover --
    /// over-reporting costs a bake, under-reporting freezes a shadow (ADR 0019).</summary>
    private static void Down(IntPtr part, float dt, bool solves = true)
    {
        if (solves) AlloyRenderer.NoteDanglyMoved(part, float.PositiveInfinity, Dangly.Still.V);
        (*_slot)(part, dt);
    }

    /// <summary>The obligation the original's tail carries: animate the part's CHILDREN. Skipped, the
    /// subtree freezes. The engine's <c>dt</c> clamp lives here and only here now -- children were tuned
    /// against it, the sim was not.</summary>
    private static void Down_TailOnly(IntPtr part, float dt)
        => _childAnimate(part, Math.Clamp(dt, MinChildDt, MaxChildDt));

    /// <summary>splitmix64's finalizer: turns a pointer into well-spread bits. Every instance of one model
    /// shares every authored number, so the object's own address is the only thing that tells two of them
    /// apart without storing a table keyed on parts that die (#145/#150).</summary>
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        return z ^ (z >> 31);
    }

    /// <summary>Hamilton product, w-first, matching the engine's Quaternion layout (verified: the identity
    /// it writes at Part+0xc0 is <c>{1,0,0,0}</c> and its emptiness test reads +0xc4..+0xcc only).</summary>
    private static void Qmul(float aw, float ax, float ay, float az,
                             float bw, float bx, float by, float bz,
                             out float w, out float x, out float y, out float z)
    {
        w = aw * bw - ax * bx - ay * by - az * bz;
        x = aw * bx + ax * bw + ay * bz - az * by;
        y = aw * by - ax * bz + ay * bw + az * bx;
        z = aw * bz + ax * by - ay * bx + az * bw;
    }

    /// <summary>Rotate <paramref name="v"/> by a unit quaternion, w-first. The engine's own inverse
    /// (<c>operator-(Quaternion const&amp;)</c> @0x5b5d50) negates W ONLY -- which is the conjugate up to
    /// an overall sign, and so the same rotation. Callers here pass <c>-w</c> for the inverse.</summary>
    private static Vector3 Qrot(float w, float x, float y, float z, Vector3 v)
    {
        var u = new Vector3(x, y, z);
        Vector3 uv = Vector3.Cross(u, v);
        return v + (uv * w + Vector3.Cross(u, uv)) * 2f;
    }

    /// <summary>Off-device: the quaternion helpers, which are the one place a sign error is invisible until
    /// a cloak swings the wrong way and reads as a wind bug.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        // 90 degrees about +Z takes +X to +Y; its inverse (w negated) takes it back.
        float s = MathF.Sqrt(0.5f);
        Vector3 a = Qrot(s, 0f, 0f, s, Vector3.UnitX);
        Vector3 b = Qrot(-s, 0f, 0f, s, a);
        // q * q(-1) is the identity, so rotating by the product must be a no-op.
        Qmul(-s, 0f, 0f, s, s, 0f, 0f, s, out float w, out float x, out float y, out float z);
        Vector3 c = Qrot(w, x, y, z, Vector3.UnitX);
        bool ok = (a - Vector3.UnitY).Length() < 1e-5f
               && (b - Vector3.UnitX).Length() < 1e-5f
               && (c - Vector3.UnitX).Length() < 1e-5f
               && MoverVerdictOk();
        log.Info(ok ? "DanglyPhysics.SelfCheck: passed"
                    : $"DanglyPhysics.SelfCheck: FAILED -- quaternion convention wrong (x->{a}, back->{b}, id->{c})");
        bool par = ParallelOk(log);
        log.Info(par ? "DanglyPhysics.SelfCheck: passed (the pooled solve of 17 parts is bit-for-bit the serial one)"
                     : "DanglyPhysics.SelfCheck: FAILED -- the pooled solve does not match the serial one bit for bit");
    }


    /// <summary>AN2's proof: a synthetic set of dangly parts solved on the pool comes out bit for bit the way
    /// it does solved one after another. Seventeen parts over four chunks, so the cuts are uneven and no chunk
    /// boundary lines up with a part boundary the maths could be reading across; the buffers are restored
    /// between the two runs, so the second starts from exactly the state the first did.
    /// <para>What it actually asserts is that <see cref="Solve"/> is a pure function of one
    /// <see cref="Item"/>: shared state or a stale pointer would show up here as a difference, since the only
    /// thing that changes between the runs is which thread ran which part.</para></summary>
    private static bool ParallelOk(ExtenderLog log)
    {
        const int Parts = 17, MaxVerts = 41;
        int floats = Parts * MaxVerts * 3;
        float* cons = (float*)NativeMemory.AllocZeroed((nuint)(Parts * MaxVerts) * sizeof(float));
        float* rest = (float*)NativeMemory.AllocZeroed((nuint)floats * sizeof(float));
        float* dst = (float*)NativeMemory.AllocZeroed((nuint)floats * sizeof(float));
        float* vel = (float*)NativeMemory.AllocZeroed((nuint)floats * sizeof(float));
        float* dst0 = (float*)NativeMemory.AllocZeroed((nuint)floats * sizeof(float));
        float* vel0 = (float*)NativeMemory.AllocZeroed((nuint)floats * sizeof(float));
        float* dstS = (float*)NativeMemory.AllocZeroed((nuint)floats * sizeof(float));
        float* velS = (float*)NativeMemory.AllocZeroed((nuint)floats * sizeof(float));
        float* maxS = (float*)NativeMemory.AllocZeroed((nuint)Parts * sizeof(float));
        int savedQn = _qn;
        try
        {
            ulong seed = 0x9e3779b97f4a7c15UL;
            float Next() { seed = Mix(seed + 0x9e3779b97f4a7c15UL); return (seed >> 40) * (1f / 16777216f); }
            for (int i = 0; i < Parts * MaxVerts; i++) cons[i] = Next() * 300f - 20f;   // some <= 0, some > 255
            for (int i = 0; i < floats; i++) { rest[i] = Next() * 4f - 2f; dst0[i] = Next() * 4f - 2f; vel0[i] = Next() * 2f - 1f; }
            for (int k = 0; k < Parts; k++)
            {
                int n = 1 + k * 2;
                ref Item it = ref _q[k];
                it = default;
                it.Part = (IntPtr)(0x1000 + k);
                it.N = n;
                it.Steps = 1 + (k & 3);
                it.Cons = (IntPtr)(cons + k * MaxVerts);
                it.Rest = (IntPtr)(rest + k * MaxVerts * 3);
                it.Dst = (IntPtr)(dst + k * MaxVerts * 3);
                it.Vel = (IntPtr)(vel + k * MaxVerts * 3);
                float s2 = MathF.Sqrt(0.5f);
                it.Dw = s2; it.Dx = 0f; it.Dy = 0f; it.Dz = s2;
                it.TLocal = new Vector3(Next(), Next(), Next());
                it.WindL = new Vector3(Next() * 8f, Next() * 8f, 0f);
                it.Gust = new Vector3(Next(), Next(), Next());
                it.GravL = new Vector3(0f, 0f, -3f);
                it.T = 12.5f + k; it.PhaseOff = Next() * 6f; it.RateVar = 0.9f + Next() * 0.2f;
                it.H = 1f / 120f; it.Disp = 0.2f + Next(); it.Stiff = 0.5f * (0.1f + Next()); it.Damp = 1.5f * Next();
                it.Turb = 0.7f; it.TurbFreq = 0.9f; it.TurbSpeed = 2.6f; it.Cage = 3.5f;
                it.Body = (k & 1) == 0 ? 0.95f : 0f;
            }
            _drainN = Parts;

            Buffer.MemoryCopy(dst0, dst, (long)floats * 4, (long)floats * 4);
            Buffer.MemoryCopy(vel0, vel, (long)floats * 4, (long)floats * 4);
            SolveRange(0, 1);
            Buffer.MemoryCopy(dst, dstS, (long)floats * 4, (long)floats * 4);
            Buffer.MemoryCopy(vel, velS, (long)floats * 4, (long)floats * 4);
            for (int k = 0; k < Parts; k++) maxS[k] = _q[k].MaxD;

            Buffer.MemoryCopy(dst0, dst, (long)floats * 4, (long)floats * 4);
            Buffer.MemoryCopy(vel0, vel, (long)floats * 4, (long)floats * 4);
            if (!StagePool.Run(4, log, SolveRange)) return false;
            for (int i = 0; i < floats; i++)
                if (BitConverter.SingleToInt32Bits(dst[i]) != BitConverter.SingleToInt32Bits(dstS[i])
                 || BitConverter.SingleToInt32Bits(vel[i]) != BitConverter.SingleToInt32Bits(velS[i])) return false;
            for (int k = 0; k < Parts; k++)
                if (BitConverter.SingleToInt32Bits(_q[k].MaxD) != BitConverter.SingleToInt32Bits(maxS[k])) return false;
            return true;
        }
        finally
        {
            // Safe here where it is not on the drain's refusal path: this check's only failure is a pool that
            // refused to start, never a worker wedged inside a chunk still holding one of these entries.
            for (int k = 0; k < Parts; k++) _q[k] = default;
            _qn = savedQn;
            _drainN = 0;
            NativeMemory.Free(cons); NativeMemory.Free(rest); NativeMemory.Free(dst); NativeMemory.Free(vel);
            NativeMemory.Free(dst0); NativeMemory.Free(vel0); NativeMemory.Free(dstS); NativeMemory.Free(velS);
            NativeMemory.Free(maxS);
        }
    }

    /// <summary>The mover verdict: two sub-threshold twitches add up to a report, a report resets the path,
    /// still 0 reports any motion at all, and infinity forces one.</summary>
    private static bool MoverVerdictOk()
    {
        float acc = 0f;
        bool a = !AlloyRenderer.Accumulate(ref acc, 0.006f, 0.01f);
        bool b = AlloyRenderer.Accumulate(ref acc, 0.006f, 0.01f) && acc == 0f;
        bool c = !AlloyRenderer.Accumulate(ref acc, 0.006f, 0.01f);
        float z = 0f;
        return a && b && c && AlloyRenderer.Accumulate(ref z, 1e-9f, 0f)
            && AlloyRenderer.Accumulate(ref z, float.PositiveInfinity, 0.01f);
    }
}
