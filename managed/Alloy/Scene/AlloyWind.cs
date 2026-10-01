using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// CX-110: Alloy's wind. Its own clock, its own pair of noise fields, its own point-source list and its own
/// evaluation -- a port of <c>WindManager::updateTimer</c> @0x491200,
/// <c>WindManager::GetGlobalWind</c> @0x490ab0, <c>pointSourceWindVector</c> @0x4907d0 and
/// <c>fillNoiseField</c> @0x490e20 (<c>docs/research/camera-loop.md</c> §5). Everything that felt wind used
/// to call <c>Scene::GetWindVector</c> @0x460c60 per particle and per dangly vertex; now the clock advances
/// once per world frame and the field is Alloy's.
/// </summary>
/// <remarks>
/// <para><b>What is still read off the engine's manager, and why.</b> The base wind vector
/// (<c>WindManager+0x20</c>), the yaw/pitch deviation limits (<c>+0x848</c>/<c>+0x84c</c>) and the field
/// period (<c>+0x858</c>) are the game's TUNING, not the clock: the base is what a server's nwscript or the
/// <c>setglobalwind</c> console command writes, and the other three are what
/// <c>WindManager::SetMaximumDeviation</c>/<c>SetUpdateTimeoutValue</c> write. Reading them each tick means a
/// scripted wind still blows. In normal play the base is ZERO -- the Scene builds its manager with the default
/// ctor @0x491b90 and <c>Scene::SetGlobalWind</c> @0x45f0a0 has no callers at all -- so every bit of felt
/// vanilla wind is the point-source term, which is why <c>/dangly breeze</c> exists.</para>
/// <para><b>The point sources are hooked, not polled.</b> <c>WindManager::AddPointSourceWind</c> @0x491dc0 is
/// the one way a source is ever created -- <c>Gob::Animate</c> @0x49dc91 registers every MOVED creature
/// (radius 1.0, life <c>magnitude(delta)</c>, strength <c>Gob+0x1a0</c>, gated on <c>Gob+0x199</c>, which
/// <c>Gob::SetWind</c> @0x436f40 sets through <c>CAurObject</c> vtable <c>+0x1c0</c> from
/// <c>CNWCObject::SetWind</c> @0x8e8df0 every <c>CNWCCreature</c> update while the client's creature-wind
/// option <c>CClientOptions+0x21</c> is on) and <c>ExplosionEmitter::Update</c> @0x4b259e every detonation.
/// So a hook on it is the whole population. CX's own gusts (<see cref="AurXform.Gust"/>) go through the same
/// function and land in the same list. The original still runs, which keeps <c>view.wind 0</c> a working A/B.
/// <para><b>Which is why the clock hangs off the engine's OWN clock site</b> and not off the Alloy frame.
/// <c>Scene::Render</c> @0x476520 calls <c>WindManager::updateTimer</c> once per world frame in its
/// <c>Scene+0x188</c> branch, whatever the renderer is doing -- while the gust hook fires whatever the
/// renderer is doing too. Driving the age from <see cref="RspShim.Run"/> (the first cut) meant that on any
/// run of frames without an Alloy pass -- a shim stand-down, a refused world frame -- sources were added and
/// never aged: the table pinned at its cap, the field froze, and <see cref="DanglyPhysics"/> (which runs on
/// vanilla frames too) sampled a few hundred dead gusts per vertex. <see cref="NewFrame"/> is the belt to
/// that brace: a frame with no world <c>Scene::Render</c> at all (the main menu) ages at the present
/// instead, so exactly one age happens per frame either way.</para></para>
/// <para>Hot: <see cref="Wind"/> is called per particle and per dangly vertex. It allocates nothing, and the
/// two noise fields and the source table are fixed arrays taken once at type init.</para>
/// </remarks>
internal static unsafe class AlloyWind
{
    /// <summary>The noise field is 16x16 (<c>fillNoiseField</c>'s <c>float (*)[16]</c>) and every lookup is
    /// masked into it, so a position cannot leave the field.</summary>
    private const int Dim = 16, Cells = Dim * Dim, Mask = Dim - 1;
    /// <summary>The octave count and the amplitude numerator <c>fillNoiseField</c> uses:
    /// <c>rand() % (500 / (16 / step))</c> over <c>/1000</c>, for step 16, 8, 4, 2, 1.</summary>
    private const int Octaves = 5, NoiseNumer = 500, NoiseDenom = 1000;
    /// <summary>The half-field offset <c>GetGlobalWind</c> takes its YAW sample at.</summary>
    private const int YawOffset = 8;
    /// <summary>Cells per world unit in a noise lookup: <c>abs((int)pos.x * 4) &amp; 15</c>.</summary>
    private const int CellsPerUnit = 4;
    /// <summary>Live point sources Alloy holds (24 bytes each, so 96 KiB). Vanilla's list is a growing
    /// <c>CExoArrayList</c> and this is a ceiling, sized against the real population rather than a guess:
    /// <c>Gob::Animate</c> registers one source per MOVED creature per frame with a life of
    /// <c>magnitude(delta)</c> -- about 0.05 s for a walking creature, i.e. roughly three frames at 60 fps --
    /// so 4096 is on the order of a thousand simultaneously-moving creatures, far past what an NWN area holds.
    /// Over it the newest source is dropped and counted; never a silent grow.
    /// <para>Note the cost this shares with vanilla: <see cref="PointWind"/> walks the whole list per sample,
    /// exactly as <c>pointSourceWindVector</c> @0x4907d0 does, so a crowd is as expensive here as there.</para></summary>
    private const int MaxSources = 4096;
    /// <summary>Fallbacks for the tuning fields when the Scene has no WindManager to read them from. The
    /// default ctor's values @0x491b90: 130.0 yaw, 0.0 pitch, 0.5 s period.</summary>
    private const float DefaultMaxYaw = 130f, DefaultMaxPitch = 0f, DefaultPeriod = 0.5f;
    /// <summary>Below this a point source's direction is undefined. The engine divides by it anyway and
    /// returns NaN; Alloy skips the source (listed as a deliberate difference).</summary>
    private const float MinDist = 1e-4f;

    /// <summary>One live gust. Same five fields as the engine's <c>PointSourceWind</c> (stride 0x1c: pos,
    /// radius at +0x0c, original life at +0x10, remaining life at +0x14, strength at +0x18), minus the
    /// original life, which nothing reads.</summary>
    private struct Source
    {
        internal Vector3 Pos;
        internal float Radius, Life, Strength;
    }

    private static ExtenderLog? _log;
    private static IntPtr _origAdd;
    private static WindUpdateTimerFn _origTimer;
    private static float* _dt;
    /// <summary>The present counter, and the one the clock last advanced on. Their difference is the whole of
    /// "did a world frame tick me this frame".</summary>
    private static int _frameSeq, _tickedSeq = -1;
    private static long _lateTicks;
    private static readonly Source[] _src = new Source[MaxSources];
    private static float[] _fieldA = new float[Cells];
    private static float[] _fieldB = new float[Cells];
    private static int _count;
    private static float _accum, _t, _period = DefaultPeriod;
    private static float _maxYaw = DefaultMaxYaw, _maxPitch = DefaultMaxPitch;
    private static Vector3 _base;
    private static ulong _rng = 0x9e3779b97f4a7c15ul;
    private static long _added, _dropped, _expired, _swaps, _ticks;
    private static bool _armed, _said;

    /// <summary>Alloy's wind is driving. Off (or unarmed) sends every reader back to
    /// <c>Scene::GetWindVector</c>.</summary>
    internal static bool Owned => _armed && ViewKnobs.Wind.V;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        ViewKnobs.Touch();
        _dt = (float*)Native.Sym(SymbolMap.DeltaT);
        if (_dt == null) log.Always("AlloyWind: DeltaT unresolved -- CX's wind clock cannot advance, so the wind "
                                    + "stays the ENGINE's (view.wind is ignored)");
        Fill(_fieldA);
        Fill(_fieldB);
    }

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!EngineLayout.Require(log, "AlloyWind", "WindManager", "Scene")) return;
        _origAdd = hooks.Install(SymbolMap.AddPointSourceWind,
            OperatingSystem.IsWindows()
                ? (IntPtr)(delegate* unmanaged<IntPtr, AurXform.V3*, float, float, float, void>)&OnAddWin
                : (IntPtr)(delegate* unmanaged<AurXform.V3, IntPtr, float, float, float, void>)&OnAddSysV);
        if (_origAdd == IntPtr.Zero)
        {
            // CX-45: say it once, loudly, and stand the feature down rather than run a wind field with no
            // gusts in it -- which would look like "the wind stopped working" with nothing in the log.
            log.Always("AlloyWind: WindManager::AddPointSourceWind did not hook -- CX cannot see the engine's "
                       + "gusts, so the wind stays the ENGINE's (Scene::GetWindVector per sample)");
            return;
        }
        if (_dt == null) return;
        _origTimer = hooks.InstallWindUpdateTimer(&OnUpdateTimer);
        if (_origTimer == null)
        {
            // CX-45: without the engine's own clock site the age would only run on Alloy frames, and the gust
            // hook does not wait for those. Rather than ship a list that pins at its cap, stand down.
            log.Always("AlloyWind: WindManager::updateTimer did not hook -- CX's wind clock has no per-world-frame "
                       + "site, so the wind stays the ENGINE's (Scene::GetWindVector per sample)");
            return;
        }
        _armed = true;
        log.Info("AlloyWind: armed -- CX's own wind clock, noise field and point-source list "
                 + "(view.wind 0 hands it back to WindManager)");
    }

    // ---- the population ---------------------------------------------------------------------------------

    /// <summary>A gust, SysV: a by-value <c>Vector</c> is classified into xmm0 (x, y) + xmm1 (z) with
    /// <c>this</c> in rdi, so the engine's <c>(Vector, float, float, float)</c> member reaches us as
    /// <c>(pos, this, radius, life, strength)</c>. Cold: a handful per frame at most.</summary>
    [UnmanagedCallersOnly]
    private static void OnAddSysV(AurXform.V3 pos, IntPtr wm, float radius, float life, float strength)
    {
        Add(new Vector3(pos.X, pos.Y, pos.Z), radius, life, strength);
        ((delegate* unmanaged<AurXform.V3, IntPtr, float, float, float, void>)_origAdd)(pos, wm, radius, life, strength);
    }

    /// <summary>The same gust, Win64: a 12-byte by-value <c>Vector</c> argument arrives as a POINTER and
    /// <c>this</c> is rcx, so the order is <c>(this, &amp;pos, radius, life, strength)</c>.</summary>
    [UnmanagedCallersOnly]
    private static void OnAddWin(IntPtr wm, AurXform.V3* pos, float radius, float life, float strength)
    {
        if (pos != null) Add(new Vector3(pos->X, pos->Y, pos->Z), radius, life, strength);
        ((delegate* unmanaged<IntPtr, AurXform.V3*, float, float, float, void>)_origAdd)(wm, pos, radius, life, strength);
    }

    private static void Add(Vector3 pos, float radius, float life, float strength)
    {
        if (radius <= 0f || life < 0f) return;   // a source with no reach or no life is never sampled
        if (_count >= MaxSources)
        {
            _dropped++;
            if (!_said) { _said = true; _log?.Always($"AlloyWind: more than {MaxSources} live wind sources -- the newest are NOT FELT (totals on /view)"); }
            return;
        }
        ref Source s = ref _src[_count++];
        s.Pos = pos; s.Radius = radius; s.Life = life; s.Strength = strength;
        _added++;
    }

    // ---- the clock --------------------------------------------------------------------------------------

    /// <summary>The engine's own clock site: <c>Scene::Render</c> @0x476520, once per WORLD frame, ahead of
    /// <c>AddPartsToDrawBuckets</c> and so ahead of everything that samples the wind -- the particle step, the
    /// dangly solve, the grass. Cold, and the original always runs so the engine's own list keeps ageing for
    /// the <c>view.wind 0</c> A/B.
    /// <para><b>SL4: the original is not optional, and "no reader on an Alloy frame" is the wrong question.</b>
    /// MEASURED (objdump @0x49131e..0x491361): <c>updateTimer</c> is the ONLY code in the client that expires
    /// a <c>PointSourceWind</c> -- it decrements each entry's remaining life at <c>+0x14</c>, drops the count
    /// at <c>+0x18</c> and compacts the tail. <c>Gob::Animate</c> @0x49dc91 ADDS one per moved wind-enabled
    /// creature per frame, so skipping the original would grow that <c>CExoArrayList</c> without bound for as
    /// long as anything walks. Its one provably dead product is the <c>windSpeed * dt</c> triple at
    /// <c>+0x2c..0x34</c>, which nothing in the binary reads; the interpolated field pair and the source list
    /// are live. Skipping it would also shift libc <c>rand()</c> by the 341 draws <c>fillNoiseField</c>
    /// @0x490e20 takes per elapsed period (~682/s at the default 0.5 s), which is a change to every other
    /// consumer of the engine's global PRNG.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnUpdateTimer(IntPtr windManager, float dt)
    {
        Tick(windManager, dt);
        _origTimer(windManager, dt);
    }

    /// <summary>One present, from <see cref="AlloyRenderer.EndFrame"/>. A frame with no world
    /// <c>Scene::Render</c> never reached <see cref="OnUpdateTimer"/>, so the age runs here instead -- see the
    /// class remarks. Exactly one of the two runs per frame.</summary>
    internal static void NewFrame()
    {
        if (_armed && _tickedSeq != _frameSeq) { _lateTicks++; Tick(IntPtr.Zero, _dt != null ? *_dt : 0f); }
        _frameSeq++;
    }

    /// <summary>The port of <c>WindManager::updateTimer</c>: advance the blend, swap and refill the field pair
    /// when the period elapses, and age the point sources out. Allocates nothing.
    /// <para><paramref name="windManager"/> is the manager the four TUNING fields are read off, or zero to
    /// keep the last ones (which is the <see cref="NewFrame"/> path: no world scene, no manager).</para></summary>
    internal static void Tick(IntPtr windManager, float dt)
    {
        if (!_armed) return;
        _tickedSeq = _frameSeq;
        _ticks++;
        ReadTuning(windManager);
        if (!float.IsFinite(dt) || dt < 0f) dt = 0f;
        _accum += dt;
        if (_accum > _period)
        {
            // The engine swaps the pair and refills the one that just became B, so A is the field the last
            // period converged ON and the blend runs 0 -> 1 into fresh noise with no discontinuity.
            (_fieldA, _fieldB) = (_fieldB, _fieldA);
            Fill(_fieldB);
            _accum = 0f;
            _swaps++;
        }
        _t = _period > 0f ? _accum / _period : 0f;
        for (int i = 0; i < _count; )
        {
            _src[i].Life -= dt;
            if (_src[i].Life < 0f)
            {
                // Order-preserving compaction, as the engine's own memmove is: a later source must not jump
                // ahead of an earlier one, because the sum is float addition and the order is visible in it.
                for (int j = i + 1; j < _count; j++) _src[j - 1] = _src[j];
                _count--;
                _expired++;
            }
            else i++;
        }
    }

    /// <summary>The four numbers the GAME owns -- see the class remarks. No manager in hand keeps the last
    /// ones rather than collapsing the wind to nothing.</summary>
    private static void ReadTuning(IntPtr wm)
    {
        if (wm == IntPtr.Zero) return;
        var m = new WindManager(wm);
        _base = m.BaseWind;
        _maxYaw = m.MaxYaw;
        _maxPitch = m.MaxPitch;
        float p = m.Period;
        _period = p > 0f ? p : DefaultPeriod;
    }

    // ---- the field --------------------------------------------------------------------------------------

    /// <summary><c>WindManager::fillNoiseField</c> @0x490e20: five octaves, block sizes 16, 8, 4, 2, 1, one
    /// draw per block added to every cell of it, amplitude <c>rand() % (500 / (16 / step)) / 1000</c>. Sums to
    /// roughly [0, 0.97] with a mean near 0.48.
    /// <para>DIFFERENCE from vanilla: the draws come from Alloy's own splitmix64 rather than libc
    /// <c>rand()</c>, so the field is a different field of the same shape -- and, unlike vanilla's, it does
    /// not consume the engine's global PRNG sequence.</para></summary>
    private static void Fill(float[] f)
    {
        Array.Clear(f);
        int step = Dim;
        for (int o = 0; o < Octaves; o++, step >>= 1)
        {
            int amp = NoiseNumer / (Dim / step);
            for (int by = 0; by + step <= Dim; by += step)
                for (int bx = 0; bx + step <= Dim; bx += step)
                {
                    float v = (int)(Next() % (ulong)amp) / (float)NoiseDenom;
                    for (int y = by; y < by + step; y++)
                        for (int x = bx; x < bx + step; x++)
                            f[y * Dim + x] += v;
                }
        }
    }

    /// <summary>splitmix64. Deterministic, seedable for the SelfCheck, and it touches no engine state.</summary>
    private static ulong Next()
    {
        _rng += 0x9e3779b97f4a7c15ul;
        ulong z = _rng;
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9ul;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebul;
        return z ^ (z >> 31);
    }

    /// <summary>The blended field at a cell, the one expression <c>GetGlobalWind</c> repeats three times.</summary>
    private static float N(int ix, int iy)
    {
        int i = (ix & Mask) * Dim + (iy & Mask);
        return (1f - _t) * _fieldA[i] + _t * _fieldB[i];
    }

    /// <summary>The cell a coordinate falls in: <c>abs((int)v * 4) &amp; 15</c>, truncating toward zero the
    /// way the engine's cvttss2si does. The <c>abs</c> means a position and its mirror share a cell.</summary>
    private static int Cell(float v)
    {
        int i = (int)v * CellsPerUnit;
        return (i < 0 ? -i : i) & Mask;
    }

    // ---- the evaluation ---------------------------------------------------------------------------------

    /// <summary>The port of <c>WindManager::GetGlobalWind</c>: the point sources at <paramref name="at"/>,
    /// plus the base wind scaled by <c>dt * noise</c>, the whole sum then rotated by a spatially-varying
    /// yaw/pitch pair. HOT -- per particle, per dangly vertex -- and allocation-free.
    /// <para>Note the asymmetry, which is the engine's: the BASE term is a displacement over
    /// <paramref name="dt"/> while the point-source term is not.</para></summary>
    internal static Vector3 Wind(Vector3 at, float dt)
    {
        int ix = Cell(at.X), iy = Cell(at.Y);
        float n = N(ix, iy);
        Vector3 pre = PointWind(at) + _base * (dt * n);
        // The engine passes the angle straight to sincosf without halving, so the quaternion turns by TWICE
        // it. Kept, because the deviation limit (130 by default) is calibrated against that.
        float yaw = _maxYaw != 0f ? (2f * N(ix + YawOffset, iy + YawOffset) - 1f) * (1f - n) * _maxYaw : 0f;
        // ...and the PITCH sample reads the field transposed (row iy, column ix), which the yaw sample does
        // not. Deliberate in the disassembly, so deliberate here.
        float pitch = _maxPitch != 0f ? (2f * N(iy, ix) - 1f) * _maxPitch : 0f;
        if (yaw == 0f && pitch == 0f) return pre;
        var qYaw = new Quaternion(0f, 0f, MathF.Sin(yaw), MathF.Cos(yaw));
        var qPitch = new Quaternion(MathF.Sin(pitch), 0f, 0f, MathF.Cos(pitch));
        return Vector3.Transform(pre, qYaw * qPitch);
    }

    /// <summary><c>WindManager::pointSourceWindVector</c> @0x4907d0: every source whose sphere contains
    /// <paramref name="at"/> pushes away from itself, linearly to nothing at its own radius.</summary>
    private static Vector3 PointWind(Vector3 at)
    {
        Vector3 sum = default;
        for (int i = 0; i < _count; i++)
        {
            ref Source s = ref _src[i];
            Vector3 d = at - s.Pos;
            float r = s.Radius;
            // The engine's own three-axis reject before the distance, kept: it is what makes a crowd of
            // sources cheap, and dropping it would change nothing but the cost.
            if (MathF.Abs(d.X) > r || MathF.Abs(d.Y) > r || MathF.Abs(d.Z) > r) continue;
            float len2 = d.LengthSquared();
            if (len2 >= r * r) continue;
            float len = MathF.Sqrt(len2);
            if (len < MinDist) continue;
            sum += d * (((r - len) * s.Strength / r) / len);
        }
        return sum;
    }

    /// <summary>Drop every source and restart the clock: a module or area edge, where the gobs that
    /// registered them are gone.</summary>
    internal static void Revoke()
    {
        _count = 0;
        _accum = 0f;
        _t = 0f;
    }

    internal static string Describe()
    {
        if (!_armed) return "engine's (AddPointSourceWind unhooked)";
        if (!ViewKnobs.Wind.V) return "off -- Scene::GetWindVector per sample";
        string s = $"on, {_count} sources (t {_t:F2}, period {_period:F2}, {_swaps} fields)";
        if (_base != Vector3.Zero) s += $", base ({_base.X:F2},{_base.Y:F2},{_base.Z:F2})";
        if (_lateTicks > 0) s += $", {_lateTicks} aged at the present (no world frame)";
        if (_dropped > 0) s += $", {_dropped} NOT FELT over the {MaxSources} cap";
        return s;
    }

    internal static string Stats(int frames)
        => _armed && ViewKnobs.Wind.V ? $"wind {_count} src {_added}+/{_expired}- {_swaps}f" : "";

    // ---- off-device -------------------------------------------------------------------------------------

    /// <summary>Pins the port to numbers derived from the disassembly, over Alloy's own state only: no engine
    /// memory, no hook, no GL. Leaves the class as it found it.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyWind.SelfCheck");
        // Save and restore: run-demo runs this inside a live service graph.
        float sAccum = _accum, sT = _t, sPeriod = _period, sYaw = _maxYaw, sPitch = _maxPitch;
        Vector3 sBase = _base;
        ulong sRng = _rng;
        // The source table is not saved: the check WRITES entries into it, so restoring a count would hand the
        // live list synthetic gusts. It runs at Initialize, before any gob has registered one, and the list
        // refills itself from the hook within a frame either way.
        _count = 0;

        // ---- the field -----------------------------------------------------------------------------------
        _rng = 1;
        Fill(_fieldA);
        float lo = float.MaxValue, hi = float.MinValue, mean = 0f;
        for (int i = 0; i < Cells; i++) { float v = _fieldA[i]; if (v < lo) lo = v; if (v > hi) hi = v; mean += v; }
        mean /= Cells;
        // The five octaves sum at most 500+250+125+62+31 over /1000.
        check.Expect(lo >= 0f && hi <= 0.968f, "the noise field lands in [0, 0.968], the octave sum's own ceiling");
        check.Expect(mean > 0.2f && mean < 0.8f, "...and averages near the middle of it");
        // The coarsest octave is ONE draw over the whole field, so no two cells can differ by more than the
        // four finer octaves can produce. Catches a transposed or mis-strided block walk.
        check.Expect(hi - lo <= 0.469f, "the 16x16 octave is one draw for every cell");
        _rng = 1;
        Fill(_fieldB);
        bool same = true;
        for (int i = 0; i < Cells; i++) if (_fieldA[i] != _fieldB[i]) { same = false; break; }
        check.Expect(same, "the field is a pure function of the seed");

        // ---- the cell lookup -----------------------------------------------------------------------------
        check.Expect(Cell(3.7f) == 12, "a coordinate maps to abs((int)v * 4) & 15, truncating toward zero");
        check.Expect(Cell(-3.7f) == 12, "...and its mirror shares the cell");
        check.Expect(Cell(4f) == 0, "4 units wraps the 16-cell field");
        check.Expect(Cell(0f) == 0, "the origin is cell 0");

        // ---- the point sources ---------------------------------------------------------------------------
        _count = 0;
        _base = Vector3.Zero; _maxYaw = 0f; _maxPitch = 0f;
        Add(new Vector3(0f, 0f, 0f), 10f, 1f, 2f);
        check.Expect(_count == 1, "a gust with reach and life is taken");
        Vector3 w = Wind(new Vector3(5f, 0f, 0f), 0f);
        // ((r - len) * strength) / r = ((10-5) * 2) / 10 = 1, along +X: away from the source.
        check.Expect(MathF.Abs(w.X - 1f) < 1e-5f && MathF.Abs(w.Y) < 1e-6f && MathF.Abs(w.Z) < 1e-6f,
                     "a point source pushes AWAY from itself at (r-d)*s/r");
        check.Expect(Wind(new Vector3(9.9f, 0f, 0f), 0f).X < 0.03f, "...tapering to nothing at its radius");
        check.Expect(Wind(new Vector3(11f, 0f, 0f), 0f) == Vector3.Zero, "a sample past the radius feels nothing");
        // Inside the axis box but outside the sphere: 9^2 + 9^2 = 162 >= 100.
        check.Expect(Wind(new Vector3(9f, 9f, 0f), 0f) == Vector3.Zero, "the box test is a prefilter, the sphere decides");
        check.Expect(Wind(Vector3.Zero, 0f) == Vector3.Zero, "a sample AT a source has no direction and is skipped");

        // ---- the base term -------------------------------------------------------------------------------
        _count = 0;
        _base = new Vector3(1f, 0f, 0f);
        _t = 0f;
        _rng = 1; Fill(_fieldA);
        float n0 = N(Cell(0f), Cell(0f));
        Vector3 b = Wind(Vector3.Zero, 0.5f);
        check.Expect(MathF.Abs(b.X - 0.5f * n0) < 1e-5f, "the base wind is a DISPLACEMENT: base * dt * noise");
        check.Expect(Wind(Vector3.Zero, 0f) == Vector3.Zero, "...so a zero step feels no base wind");

        // ---- the rotation --------------------------------------------------------------------------------
        _maxYaw = MathF.PI / 4f;   // a quarter turn, because the engine's quaternion doubles the angle
        _base = new Vector3(1f, 0f, 0f);
        // Force the yaw sample to a known value by flattening both fields: 2*0.5 - 1 == 0 kills the term.
        Array.Fill(_fieldA, 0.5f); Array.Fill(_fieldB, 0.5f);
        Vector3 flat = Wind(Vector3.Zero, 1f);
        check.Expect(MathF.Abs(flat.X - 0.5f) < 1e-5f && MathF.Abs(flat.Y) < 1e-5f,
                     "a mid-field noise value means no deviation at all (2n-1 == 0)");
        Array.Fill(_fieldA, 1f); Array.Fill(_fieldB, 1f);
        // n == 1 zeroes the (1-n) factor as well, so the yaw term vanishes and the base term is base*dt.
        check.Expect(MathF.Abs(Wind(Vector3.Zero, 1f).X - 1f) < 1e-5f,
                     "a saturated field has no yaw deviation either -- the (1-n) factor");
        Array.Fill(_fieldA, 0f); Array.Fill(_fieldB, 0f);
        // n == 0: (2*0-1) * (1-0) * (pi/4) = -pi/4, and the quaternion turns by twice that = -pi/2 about +Z.
        _count = 0; _maxPitch = 0f;
        Add(new Vector3(-1f, 0f, 0f), 10f, 1f, 1f);   // a source due -X pushes +X before rotation
        Vector3 turned = Wind(Vector3.Zero, 0f);
        check.Expect(MathF.Abs(turned.Y + 0.9f) < 0.05f && MathF.Abs(turned.X) < 0.05f,
                     "the yaw quaternion turns by TWICE the angle, about +Z");

        // ---- ageing --------------------------------------------------------------------------------------
        _count = 0;
        Add(Vector3.Zero, 10f, 0.1f, 1f);
        _armed = true; _period = 1f;
        Tick(IntPtr.Zero, 0.05f);
        check.Expect(_count == 1, "a source with life left survives the tick");
        Tick(IntPtr.Zero, 0.05f);
        check.Expect(_count == 1, "life exactly 0 is still alive -- the engine tests < 0");
        Tick(IntPtr.Zero, 0.05f);
        check.Expect(_count == 0, "...and goes on the next tick");

        // ---- the clock -----------------------------------------------------------------------------------
        _accum = 0f; _period = 0.5f; long swaps0 = _swaps;
        Tick(IntPtr.Zero, 0.2f);
        check.Expect(MathF.Abs(_t - 0.4f) < 1e-5f, "the blend is accum/period");
        Tick(IntPtr.Zero, 0.2f);
        check.Expect(MathF.Abs(_t - 0.8f) < 1e-5f && _swaps == swaps0, "...and does not swap before the period");
        Tick(IntPtr.Zero, 0.2f);
        check.Expect(_t == 0f && _swaps == swaps0 + 1, "past the period the pair swaps and the blend restarts");

        _accum = sAccum; _t = sT; _period = sPeriod; _maxYaw = sYaw; _maxPitch = sPitch;
        _base = sBase; _count = 0; _rng = sRng;
        _armed = _origAdd != IntPtr.Zero && _origTimer != null;
        Fill(_fieldA); Fill(_fieldB);
        log.Info(check.Ok ? "AlloyWind.SelfCheck: passed (noise field, cell lookup, point sources, rotation, clock)"
                          : "AlloyWind.SelfCheck: FAILED");
    }
}
