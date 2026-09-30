using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
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
/// <para><b>Allocates nothing.</b> This runs once per dangly part per frame on the main thread. Every
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
    /// hand-backs and the engine's child animate, so it is the term a compute solve would remove.</summary>
    private static long _ticks, _peakTicks;

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
        if (frames <= 0 || _parts == 0) { _parts = 0; _verts = 0; _moved = 0; _ticks = 0; _peakTicks = 0; return ""; }
        double toMs = 1000.0 / Stopwatch.Frequency;
        string s = $"dangly {(float)_parts / frames:F1} parts/f, {(float)_verts / frames:F0} verts/f, " +
                   $"{(float)_moved / frames:F1} moved/f, solve {_ticks * toMs / frames:F3} ms/f (peak call {_peakTicks * toMs:F3} ms)";
        _parts = 0; _verts = 0; _moved = 0; _ticks = 0; _peakTicks = 0;
        return s;
    }

    /// <summary>The <c>/dangly</c> status line; the dials are <see cref="Dangly"/>'s.</summary>
    internal static string Describe()
        => $"dangly {(Dangly.Enabled.V && _armed ? "on" : _armed ? "off (vanilla solve)" : "unarmed")} "
         + $"gravity {Dangly.Gravity.V:F2} wind {Dangly.Wind.V:F2} turb {Dangly.Turb.V:F2} turbfreq {Dangly.TurbFreq.V:F2} "
         + $"turbspeed {Dangly.TurbSpeed.V:F2} vary {Dangly.Vary.V:F2} breeze {Dangly.Breeze.V:F2} body {(Dangly.Body.V > 0f ? Dangly.Body.V.ToString("F2") : "off")} cage {Dangly.Cage.V:F2} hz {Dangly.Hz.V:F0} steps {Dangly.Steps.V} still {Dangly.Still.V:F3}";

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
            _commitItemDyn(vdo, (uint)item, 0xffffffff);
            StorePose(part, wpos, wrot);
            AlloyRenderer.NoteDanglyMoved(part, float.PositiveInfinity, Dangly.Still.V);
            Down_TailOnly(part, dt);
            return;
        }

        // Not a teleport, but possibly the first frame: an all-zero xyz at +0xc4..+0xcc means the previous
        // pose was never written, and a zero quaternion would collapse every vertex onto the part. Seeding
        // makes this frame's deltas identity instead. Vanilla's own guard.
        if (prevRot[1] == 0f && prevRot[2] == 0f && prevRot[3] == 0f)
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
        float maxSq = 0f;
        AlloyRenderer.NoteDanglyPrev(part, dst, n);

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

        _commitItemDyn(vdo, (uint)item, 0xffffffff);
        StorePose(part, wpos, wrot);
        long dtTicks = Stopwatch.GetTimestamp() - t0;
        _ticks += dtTicks; if (dtTicks > _peakTicks) _peakTicks = dtTicks;
        _parts++; _verts += n;
        if (AlloyRenderer.NoteDanglyMoved(part, MathF.Sqrt(maxSq), Dangly.Still.V)) _moved++;
        Down_TailOnly(part, dt);
    }

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
